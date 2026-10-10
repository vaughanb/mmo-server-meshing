// Server Meshing test rig — net stats probe (Phase 0 step 9 c4, MESH_NET_STATS=1, step9_design.md decision 13): every
// 5 s on its own clock, the packets the cell's client-facing LiteNetLib NetManager sent and received and the reliable
// resends it counted (Statistics.PacketLoss, raised once per resent packet: ReliableChannel.cs:252-255), so a loss run
// shows where loss lands. Statistics are switched on by the mesh (MeshMapNetworkManager.cs:707).
using System;
using System.Globalization;
using System.Text;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshNetStats logs a Net stats window per 5 s on a map server: the deltas of the server NetManager's
    /// PacketsSent, PacketsReceived and PacketLoss (resent), and the loss share resent / sent. The server NetManager is
    /// the innermost LiteNetLibTransport's under the mesh's (possibly metered) server transport: client traffic, not
    /// the mesh links.
    /// </summary>
    public sealed class ServerMeshNetStats : MonoBehaviour
    {
        /// <summary>EnvironmentKey switches the probe on with 1.</summary>
        public const string EnvironmentKey = "MESH_NET_STATS";
        /// <summary>ProbeObjectName names the probe's GameObject.</summary>
        public const string ProbeObjectName = "[Mesh] NetStats";
        /// <summary>WindowSeconds is the length of a Net stats window on the probe's own clock.</summary>
        public const double WindowSeconds = 5;

        private MeshMapNetworkManager _mesh;
        private NetStatistics _stats;
        private bool _started;
        private float _nextTry;
        private double _windowStart;
        private long _sent, _received, _resent;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSwitch(value, out bool on, out string error))
            {
                Logging.LogError("Mesh", "Net stats error: " + error + "; no probe");
                return;
            }
            if (!on || FindFirstObjectByType<ServerMeshNetStats>() != null)
                return;
            GameObject go = new GameObject(ProbeObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshNetStats>();
        }

        /// <summary>
        /// TryParseSwitch reads the key: unset, empty or 0 is off, 1 is on (spaces trimmed); anything else is false with an
        /// error naming the key.
        /// </summary>
        public static bool TryParseSwitch(string text, out bool on, out string error)
        {
            on = false;
            error = null;
            string value = text == null ? string.Empty : text.Trim();
            if (value.Length == 0 || value == "0")
                return true;
            if (value == "1")
            {
                on = true;
                return true;
            }
            error = $"{EnvironmentKey}={Sync.MeshStateSender.CleanForLog(value).Replace(' ', '_')} is not 1 or 0";
            return false;
        }

        /// <summary>LossShare is resent / sent for a window, 0 when nothing was sent or a count is negative.</summary>
        public static double LossShare(long resent, long sent)
        {
            if (sent <= 0 || resent <= 0)
                return 0;
            return (double)resent / sent;
        }

        /// <summary>
        /// WindowLine is the Net stats window:
        /// <c>Net stats window 5.00 s: sent 5000 pkt received 4800 pkt resent 52 | loss share 0.0104</c>; zeros for
        /// negatives, the share with four decimals.
        /// </summary>
        public static string WindowLine(double seconds, long sent, long received, long resent)
        {
            StringBuilder b = new StringBuilder(96);
            b.Append("Net stats window ").Append(Fixed(seconds, "0.00")).Append(" s: sent ").Append(Count(sent));
            b.Append(" pkt received ").Append(Count(received)).Append(" pkt resent ").Append(Count(resent));
            b.Append(" | loss share ").Append(Fixed(LossShare(resent, sent), "0.0000"));
            return b.ToString();
        }

        private static string Fixed(double value, string format) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);

        private void Update()
        {
            if (!_started)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryStart();
                return;
            }
            double now = Time.unscaledTimeAsDouble;
            if (now - _windowStart >= WindowSeconds)
                EndWindow(now);
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || !mesh.IsServerReadyToInstantiateObjects())
                return;
            LiteNetLibTransport transport = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(mesh.ServerTransport);
            if (transport == null || transport.Server == null)
                return; // the server transport starts with the server
            _started = true;
            _mesh = mesh;
            if (!transport.Server.EnableStatistics)
            {
                Logging.LogError("Mesh", $"Net stats error: the server NetManager of {Sync.MeshStateSender.CleanForLog(mesh.OwnZoneId)} counts no statistics; no probe");
                enabled = false;
                return;
            }
            _stats = transport.Server.Statistics;
            _sent = _stats.PacketsSent;
            _received = _stats.PacketsReceived;
            _resent = _stats.PacketLoss;
            _windowStart = Time.unscaledTimeAsDouble;
        }

        private void EndWindow(double now)
        {
            long sent = _stats.PacketsSent, received = _stats.PacketsReceived, resent = _stats.PacketLoss;
            Logging.Log("Mesh", WindowLine(now - _windowStart, sent - _sent, received - _received, resent - _resent));
            _sent = sent;
            _received = received;
            _resent = resent;
            _windowStart = now;
        }
    }
}
