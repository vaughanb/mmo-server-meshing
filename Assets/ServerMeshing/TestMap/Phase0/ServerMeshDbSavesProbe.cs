// Server Meshing test rig — save drain probe (Phase 0 step 9 c1, MESH_DB_SAVES_PROBE=1, step9_design.md decision 3):
// in the process that hosts the database manager (the editor on the rig; the cells are its clients), a DB saves window
// every 5 s with the deferred save scheduler's backlog, the saves the manager acknowledged, the writes that drained the
// backlog, the kit's failed deferred writes and the process's frame rate, beside the scheduler's configured ceiling.
using System;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshDbSavesProbe watches the database manager's deferred character saves. pending is the scheduler's public
    /// PendingCount on the manager's private _characterSaveScheduler (read by reflection, read-only). enqueued counts the
    /// UpdateCharacter requests the manager acknowledged: the probe reads the kit's registered handler by reflection and
    /// registers, through the server's public RegisterRequestHandler, a pass-through that calls it and counts each Success
    /// response (in the deferred path the kit enqueues the save before it answers, DatabaseNetworkManager_RequestHandlers
    /// .cs:189-206); the kit's own handler is put back when the probe goes. written = enqueued - delta pending over the window
    /// (a save coalesced onto one already pending counts as written). failed counts the kit's "Deferred character save
    /// failed" error lines through Unity's threaded log hook. Every request is counted, the burst's and any other's, so
    /// the figures stay consistent with pending, which is the whole scheduler.
    /// </summary>
    public sealed class ServerMeshDbSavesProbe : MonoBehaviour
    {
        /// <summary>EnvironmentKey switches the probe on with 1.</summary>
        public const string EnvironmentKey = "MESH_DB_SAVES_PROBE";
        /// <summary>ProbeObjectName names the probe's GameObject.</summary>
        public const string ProbeObjectName = "[Mesh] DbSavesProbe";
        /// <summary>WindowSeconds is the length of a DB saves window on the probe's own clock.</summary>
        public const double WindowSeconds = 5;
        /// <summary>WaitSeconds is how long the probe waits for a database server in its process before it gives up.</summary>
        public const float WaitSeconds = 60f;
        /// <summary>SaveFailedText is what the kit's failed deferred write logs (DatabaseNetworkManager.cs InternalPersistCharacterUpdate).</summary>
        public const string SaveFailedText = "Deferred character save failed";

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
            error = $"{EnvironmentKey}={MeshStateSender.CleanForLog(value).Replace(' ', '_')} is not 1 or 0";
            return false;
        }

        /// <summary>
        /// Written is the writes that drained the backlog in a window: the acknowledged saves minus the backlog's growth
        /// (pendingAfter - pendingBefore). Kept signed: a negative figure means saves reached the scheduler some other way
        /// than an acknowledged UpdateCharacter request, which the parser is to see rather than have hidden.
        /// </summary>
        public static long Written(long enqueued, int pendingBefore, int pendingAfter)
        {
            return enqueued - ((long)pendingAfter - pendingBefore);
        }

        /// <summary>
        /// Ceiling is the scheduler's configured write ceiling per second: one lane a tick, at most maxPerTick writes, with
        /// the scheduler's own floors (tick at least 50 ms, at least 1 write; DatabaseCharacterSaveScheduler.cs:45-46).
        /// 16/s for the kit's 500 ms and 8.
        /// </summary>
        public static double Ceiling(int maxPerTick, int tickMilliseconds)
        {
            return Math.Max(1, maxPerTick) * 1000.0 / Math.Max(50, tickMilliseconds);
        }

        /// <summary>IsSaveFailedLine is true for a log message that carries the kit's failed deferred write text.</summary>
        public static bool IsSaveFailedLine(string message)
        {
            return message != null && message.IndexOf(SaveFailedText, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// WindowLine is the DB saves window:
        /// <c>DB saves window 5.00 s: pending 120 enqueued 50 written 16 failed 0 | db fps 59.8 | ceiling 16/s</c>; written
        /// keeps its sign, other negatives are zeros, fps has one decimal and the ceiling is a whole number of writes/s.
        /// </summary>
        public static string WindowLine(double seconds, int pending, long enqueued, long written, long failed, double fps, double ceiling)
        {
            StringBuilder b = new StringBuilder(112);
            b.Append("DB saves window ").Append(Fixed(seconds, "0.00")).Append(" s: pending ").Append(Count(pending));
            b.Append(" enqueued ").Append(Count(enqueued)).Append(" written ").Append(written.ToString(CultureInfo.InvariantCulture)).Append(" failed ").Append(Count(failed));
            b.Append(" | db fps ").Append(Fixed(fps, "0.0")).Append(" | ceiling ").Append(Fixed(ceiling, "0")).Append("/s");
            return b.ToString();
        }

        private static string Fixed(double value, string format) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo s_handlersField = typeof(TransportHandler).GetField("_requestHandlers", Private);
        private static readonly FieldInfo s_handlerDelegateField = typeof(LiteNetLibRequestHandler<DbRequestMessage<UpdateCharacterReq>, CharacterResp>).GetField("_requestHandler", Private);
        private static readonly FieldInfo s_schedulerField = typeof(DatabaseNetworkManager).GetField("_characterSaveScheduler", Private);
        private static readonly FieldInfo s_tickField = typeof(DatabaseNetworkManager).GetField("characterSaveTickMilliseconds", Private);
        private static readonly FieldInfo s_maxField = typeof(DatabaseNetworkManager).GetField("characterSaveMaxPerTick", Private);

        private DatabaseNetworkManager _db;
        private RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> _kitHandler;
        private RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> _counting;
        private bool _installed;
        private float _giveUpAt, _nextTry;
        private double _windowStart, _ceiling;
        private int _frames, _pendingBefore;
        private long _enqueued, _failed, _enqueuedBefore, _failedBefore;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return; // a map server is a client of the database manager, never its host
            if (!TryParseSwitch(value, out bool on, out string error))
            {
                Logging.LogError("Mesh", "DB saves probe error: " + error + "; no probe");
                return;
            }
            if (!on || FindFirstObjectByType<ServerMeshDbSavesProbe>() != null)
                return;
            GameObject go = new GameObject(ProbeObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshDbSavesProbe>();
        }

        private void Awake()
        {
            _counting = CountUpdateCharacter;
            _giveUpAt = Time.unscaledTime + WaitSeconds;
        }

        private void OnEnable() => Application.logMessageReceivedThreaded += OnLog;

        private void OnDisable() => Application.logMessageReceivedThreaded -= OnLog;

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error && IsSaveFailedLine(condition))
                Interlocked.Increment(ref _failed);
        }

        private void Update()
        {
            if (!_installed)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryInstall();
                return;
            }
            _frames++;
            double now = Time.unscaledTimeAsDouble;
            if (now - _windowStart >= WindowSeconds)
                EndWindow(now);
        }

        private void TryInstall()
        {
            DatabaseNetworkManager db = FindFirstObjectByType<DatabaseNetworkManager>();
            if (db == null || !db.IsServer || !TryGetKitHandler(db, out RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> kit))
            {
                if (Time.unscaledTime < _giveUpAt)
                    return;
                if (Application.isEditor)
                    Logging.LogError("Mesh", $"DB saves probe error: no database server with an UpdateCharacter handler in this process {WaitSeconds:0} s after Play; no probe");
                enabled = false;
                return;
            }
            if (s_schedulerField == null || s_tickField == null || s_maxField == null)
            {
                Logging.LogError("Mesh", "DB saves probe error: the database manager has no _characterSaveScheduler, characterSaveTickMilliseconds or characterSaveMaxPerTick field (kit changed); no probe");
                enabled = false;
                return;
            }
            _db = db;
            Register(kit);
            _ceiling = Ceiling((int)s_maxField.GetValue(db), (int)s_tickField.GetValue(db));
            _installed = true;
            _windowStart = Time.unscaledTimeAsDouble;
            _pendingBefore = Pending();
            _enqueuedBefore = Interlocked.Read(ref _enqueued);
            _failedBefore = Interlocked.Read(ref _failed);
            string mode = db.UseDeferredCharacterSaveScheduler ? "deferred" : "off (saves write at once, pending stays 0)";
            Logging.Log("Mesh", $"DB saves probe: counting UpdateCharacter on the database manager; deferred save scheduler {mode}; ceiling {Fixed(_ceiling, "0.##")} writes/s");
        }

        private static bool TryGetKitHandler(DatabaseNetworkManager db, out RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> handler)
        {
            handler = null;
            if (s_handlersField == null || s_handlerDelegateField == null || db.Server == null)
                return false;
            if (!(s_handlersField.GetValue(db.Server) is Dictionary<ushort, ILiteNetLibRequestHandler> handlers)
                || !handlers.TryGetValue(DatabaseRequestTypes.UpdateCharacter, out ILiteNetLibRequestHandler registered)
                || !(registered is LiteNetLibRequestHandler<DbRequestMessage<UpdateCharacterReq>, CharacterResp>))
                return false;
            handler = s_handlerDelegateField.GetValue(registered) as RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp>;
            return handler != null;
        }

        private void Register(RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> kit)
        {
            _kitHandler = kit;
            _db.Server.RegisterRequestHandler(DatabaseRequestTypes.UpdateCharacter, _counting);
        }

        private UniTaskVoid CountUpdateCharacter(RequestHandlerData requestHandler, DbRequestMessage<UpdateCharacterReq> request, RequestProceedResultDelegate<CharacterResp> result)
        {
            RequestProceedResultDelegate<CharacterResp> counted = (code, response, extra) =>
            {
                if (code == AckResponseCode.Success)
                    Interlocked.Increment(ref _enqueued);
                result(code, response, extra);
            };
            return _kitHandler(requestHandler, request, counted);
        }

        private int Pending()
        {
            return s_schedulerField.GetValue(_db) is DatabaseCharacterSaveScheduler scheduler ? scheduler.PendingCount : 0;
        }

        private void EndWindow(double now)
        {
            if (_db == null)
            {
                enabled = false; // the database manager went with Play
                return;
            }
            KeepCounting();
            int pending = Pending();
            long enqueued = Interlocked.Read(ref _enqueued) - _enqueuedBefore;
            long failed = Interlocked.Read(ref _failed) - _failedBefore;
            double seconds = now - _windowStart;
            double fps = seconds > 0 ? _frames / seconds : 0;
            Logging.Log("Mesh", WindowLine(seconds, pending, enqueued, Written(enqueued, _pendingBefore, pending), failed, fps, _ceiling));
            _windowStart = now;
            _frames = 0;
            _pendingBefore = pending;
            _enqueuedBefore += enqueued;
            _failedBefore += failed;
        }

        private void KeepCounting()
        {
            if (!TryGetKitHandler(_db, out RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> current) || current == _counting)
                return;
            Logging.LogWarning("Mesh", "DB saves probe: the database manager registered its UpdateCharacter handler again; counting resumes from now");
            Register(current);
        }

        private void OnDestroy()
        {
            if (!_installed || _db == null || _db.Server == null || _kitHandler == null)
                return;
            if (TryGetKitHandler(_db, out RequestDelegate<DbRequestMessage<UpdateCharacterReq>, CharacterResp> current) && current == _counting)
                _db.Server.RegisterRequestHandler(DatabaseRequestTypes.UpdateCharacter, _kitHandler);
        }
#endif
    }
}
