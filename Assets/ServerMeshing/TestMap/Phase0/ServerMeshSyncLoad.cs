// Server Meshing test rig — sync-field and RPC load on the stand-in ships (Phase 0 step 7, MESH_SYNC_LOAD=dirty:<D>,rpc:<R>):
// D load-field writes per kit tick and R RpcFired calls per second on the cell's load ships, so the kit's state-sync
// and RPC costs can be measured at chosen rates. The fields and the RPC are ServerMeshShipCombat's (the shield and
// RpcFired copy the space game's ShipCombatComponent at commit 1a3a58d9, read there, not copied).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshSyncLoad writes, at every kit tick, D of the load ships' load0..load7 fields round-robin with fresh
    /// values, and sends R RpcFired calls per second spread over the ticks, each adding the sends it expects to rpcSends.
    /// The allocation figure is GC.GetTotalMemory around each RPC batch, skipping batches a gen-0 collection interrupted:
    /// coarse under Mono (heap blocks, not objects), a window mean, reported only. It needs load ships on its cell.
    /// </summary>
    public sealed class ServerMeshSyncLoad : MonoBehaviour
    {
        /// <summary>EnvironmentKey is the sync load's setting.</summary>
        public const string EnvironmentKey = "MESH_SYNC_LOAD";
        /// <summary>LoadObjectName names the sync load's GameObject.</summary>
        public const string LoadObjectName = "[Mesh] SyncLoad";
        /// <summary>MaxDirty is the most field writes per tick.</summary>
        public const int MaxDirty = 10000;
        /// <summary>MaxRpc is the most RPC calls per second.</summary>
        public const int MaxRpc = 100000;
        /// <summary>ShipWaitSeconds is how long after the server is ready the sync load waits for load ships.</summary>
        public const float ShipWaitSeconds = 10f;

        private int _dirty;
        private int _rpc;
        private MeshMapNetworkManager _mesh;
        private ServerMeshShipLoad _load;
        private bool _ready;
        private bool _running;
        private float _nextTry;
        private float _giveUpAt;
        private uint _lastTick;
        private long _writeCursor;
        private long _rpcCursor;
        private float _value;
        private int _rpcSequence;
        private double _rpcStart;
        private long _rpcDone;
        private int _lastWorkWindow = -1;
        private double _windowStart;
        private int _gcBase;
        private long _writes, _rpcCalls, _rpcSends, _writeTicks, _rpcTicks, _allocBytes, _allocRpcs;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSetting(value, out int dirty, out int rpc, out string error))
            {
                Logging.LogError("Mesh", "Sync load error: " + error + "; nothing loaded");
                return;
            }
            if (FindFirstObjectByType<ServerMeshSyncLoad>() != null)
                return;
            GameObject go = new GameObject(LoadObjectName);
            DontDestroyOnLoad(go);
            ServerMeshSyncLoad load = go.AddComponent<ServerMeshSyncLoad>();
            load._dirty = dirty;
            load._rpc = rpc;
        }

        /// <summary>
        /// TryParseSetting reads <c>dirty:&lt;D&gt;,rpc:&lt;R&gt;</c> in either order, each part exactly once (names ignore
        /// case, spaces trimmed), D 0..10,000 per tick and R 0..100,000 per second; false with an error naming the key and
        /// the bad part otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out int dirty, out int rpc, out string error)
        {
            dirty = rpc = 0;
            string value = text == null ? string.Empty : text.Trim();
            string[] parts = value.Split(',');
            bool haveDirty = false, haveRpc = false;
            int d = 0, r = 0;
            for (int i = 0; i < parts.Length; ++i)
            {
                string part = parts[i].Trim();
                int colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1 || part.IndexOf(':', colon + 1) >= 0)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the part {Printable(part)} is not dirty:<D> or rpc:<R>";
                    return false;
                }
                string name = part.Substring(0, colon).Trim();
                string number = part.Substring(colon + 1).Trim();
                bool isDirty = string.Equals(name, "dirty", StringComparison.OrdinalIgnoreCase);
                if (!isDirty && !string.Equals(name, "rpc", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{EnvironmentKey}={Printable(value)}: {Printable(name)} is not dirty or rpc";
                    return false;
                }
                if (isDirty ? haveDirty : haveRpc)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: {name.ToLowerInvariant()} is given twice";
                    return false;
                }
                int max = isDirty ? MaxDirty : MaxRpc;
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > max)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: {name.ToLowerInvariant()} must be a whole number from 0 to {max}";
                    return false;
                }
                if (isDirty)
                {
                    haveDirty = true;
                    d = n;
                }
                else
                {
                    haveRpc = true;
                    r = n;
                }
            }
            if (!haveDirty || !haveRpc)
            {
                error = $"{EnvironmentKey}={Printable(value)} needs both dirty:<D> and rpc:<R>";
                return false;
            }
            dirty = d;
            rpc = r;
            error = null;
            return true;
        }

        private void Update()
        {
            if (!_running)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryStart();
                return;
            }
            if (_mesh == null || !_mesh.IsServer)
                return;
            IReadOnlyList<ServerMeshShipEntity> ships = _load != null ? _load.LoadShips : null;
            uint tick = _mesh.LocalTick;
            if (ships != null && ships.Count > 0 && tick != _lastTick)
            {
                _lastTick = tick;
                WriteFields(ships);
                SendRpcs(ships);
            }
            WindowIfDue(ships);
        }

        private void TryStart()
        {
            if (!_ready)
            {
                if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                    return;
                _ready = true;
                _mesh = mesh;
                _giveUpAt = Time.unscaledTime + ShipWaitSeconds;
            }
            _load = FindFirstObjectByType<ServerMeshShipLoad>();
            int ships = _load != null ? _load.LoadShips.Count : 0;
            if (ships == 0)
            {
                if (Time.unscaledTime < _giveUpAt)
                    return;
                Logging.LogError("Mesh", $"Sync load error: no load ships on cell {_mesh.OwnZoneId} {(int)ShipWaitSeconds} s after the server was ready; nothing loaded");
                enabled = false;
                return;
            }
            _running = true;
            _lastTick = _mesh.LocalTick;
            _rpcStart = Time.unscaledTimeAsDouble;
            Logging.Log("Mesh", SettingLine(_dirty, _rpc, ships));
            StartWindow();
        }

        private void WriteFields(IReadOnlyList<ServerMeshShipEntity> ships)
        {
            if (_dirty == 0)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int count = ships.Count;
            for (int i = 0; i < _dirty; ++i)
            {
                long slot = _writeCursor++;
                ServerMeshShipEntity ship = ships[(int)(slot / ServerMeshShipCombat.LoadFieldCount % count)];
                if (ship == null || ship.ShipCombat == null)
                    continue;
                _value += 1f;
                ship.ShipCombat.WriteLoadField((int)(slot % ServerMeshShipCombat.LoadFieldCount), _value);
                _writes++;
            }
            _writeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }

        private void SendRpcs(IReadOnlyList<ServerMeshShipEntity> ships)
        {
            if (_rpc == 0)
                return;
            long target = (long)(_rpc * (Time.unscaledTimeAsDouble - _rpcStart));
            long calls = Math.Min(target - _rpcDone, _rpc);
            if (calls <= 0)
                return;
            _rpcDone = Math.Max(_rpcDone + calls, target - _rpc);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int collections = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);
            long sent = 0;
            int count = ships.Count;
            for (long i = 0; i < calls; ++i)
            {
                ServerMeshShipEntity ship = ships[(int)(_rpcCursor++ % count)];
                if (ship == null || ship.ShipCombat == null)
                    continue;
                _rpcSends += ship.ShipCombat.FireRpcForLoad(_rpcSequence++);
                sent++;
            }
            long after = GC.GetTotalMemory(false);
            if (GC.CollectionCount(0) == collections && after >= before)
            {
                _allocBytes += after - before;
                _allocRpcs += sent;
            }
            _rpcCalls += sent;
            _rpcTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }

        private void StartWindow()
        {
            _lastWorkWindow = _mesh.WorkWindowCount;
            _windowStart = Time.unscaledTimeAsDouble;
            _gcBase = GC.CollectionCount(0);
            _writes = _rpcCalls = _rpcSends = _writeTicks = _rpcTicks = _allocBytes = _allocRpcs = 0;
        }

        private void WindowIfDue(IReadOnlyList<ServerMeshShipEntity> ships)
        {
            if (_mesh.WorkWindowCount == _lastWorkWindow)
                return;
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double perRpc = _allocRpcs > 0 ? (double)_allocBytes / _allocRpcs : 0;
            Logging.Log("Mesh", WindowLine(Time.unscaledTimeAsDouble - _windowStart, ships != null ? ships.Count : 0, _writes, _rpcCalls, _rpcSends,
                _writeTicks * toMs, _rpcTicks * toMs, perRpc, GC.CollectionCount(0) - _gcBase));
            StartWindow();
        }

        /// <summary>SettingLine is the sync load's setting line: <c>Sync load: dirty 100/tick rpc 200/s over 50 ships</c>.</summary>
        public static string SettingLine(int dirty, int rpc, int ships)
        {
            return "Sync load: dirty " + Count(dirty) + "/tick rpc " + Count(rpc) + "/s over " + Count(ships) + " ships";
        }

        /// <summary>
        /// WindowLine is the Sync load window line: the ships, field writes, RPC calls and their expected sends, ms/s per
        /// part, allocated bytes per RPC (whole bytes) and gc0; zeros when empty, invariant and without braces.
        /// </summary>
        public static string WindowLine(double seconds, int ships, long writes, long rpc, long rpcSends, double writeMs, double rpcMs, double allocBytesPerRpc, long gc0)
        {
            double s = Positive(seconds);
            StringBuilder b = new StringBuilder(160);
            b.Append("Sync load window ").Append(Fixed(s, "0.00")).Append(" s: ships ").Append(Count(ships));
            b.Append(" | writes ").Append(Count(writes)).Append(" rpc ").Append(Count(rpc)).Append(" rpcSends ").Append(Count(rpcSends));
            b.Append(" | ms/s write ").Append(Fixed(s > 0 ? Positive(writeMs) / s : 0, "0.00")).Append(" rpc ").Append(Fixed(s > 0 ? Positive(rpcMs) / s : 0, "0.00"));
            b.Append(" | alloc ").Append(Fixed(allocBytesPerRpc, "0")).Append(" B/rpc gc0 ").Append(Count(gc0));
            return b.ToString();
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text);

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) ? value : 0;

        private static string Fixed(double value, string format) => Positive(value).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
