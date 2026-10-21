// Server Meshing add-on — a map server keeps its database connection (DESIGN.md 0.63; moved out of
// MeshMapNetworkManager.cs, with the stranded-request expiry, in 0.87).
//
// The kit opens the database connection once and never again, so this re-opens it whenever it is down. Reconnecting
// clears the kit's request table (LiteNetLibClient.StartClient), and an awaiter whose callback is cleared waits forever:
// SendRequestAsync polls a flag only the callback sets, and the kit's own 30 s timeout finds nothing left to time out.
// So the requests still waiting when the connection is re-opened are first taken out of the table and answered with
// the kit's own Timeout, on the frame of the reconnect. Every one of them is doomed: the connection is down (IsClient
// is false) and the database manager answers a request only over the connection that carried it, so nothing can
// answer them. Until 0.87 the reconnect instead waited up to databaseDrainWaitSeconds (35 s) for them to time out on
// their own, which held a cell's database down for 35 s whenever an attempt failed -- the central-restart rig's late
// login landed in that window and timed out (splitcentral 157/161, 2026-10-21). The drain remains only as the
// fallback for a kit whose request table cannot be reached.
using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Tooltip("Seconds between attempts to open the database connection whenever this server does not have one. " +
                 "The kit opens it once at start-up and never again: the transport calls Manager.StopClient() on a " +
                 "disconnect and nothing restarts it, so a map server that loses the database manager -- or that " +
                 "started before it existed -- keeps running and keeps accepting players while every save silently " +
                 "fails. 0 disables.")]
        public float databaseReconnectSeconds = 5f;
        [Tooltip("How long after this server starts to leave the database connection alone, so the retry never " +
                 "races the kit's own first StartClient.")]
        public float databaseReconnectGraceSeconds = 15f;
        [Tooltip("Fallback only, for a kit whose request table cannot be reached: how long to let database requests " +
                 "still waiting when the connection dropped expire before reconnecting anyway (reconnecting clears " +
                 "their callbacks, which strands their awaiters). Normally they are answered with a timeout at the " +
                 "reconnect and nothing waits. Slightly over the kit's 30 s request timeout.")]
        public float databaseDrainWaitSeconds = 35f;
        private float _nextDatabaseReconnect;
        private DatabaseNetworkManager _databaseManager;
        private bool _databaseWasConnected;
        private bool _databaseDrainLogged;
        private float _databaseDrainSince;
        private bool _databaseEverConnected;
        private float _databaseGraceUntil;
        private static System.Reflection.FieldInfo s_requestCallbacksField;
        private static bool s_requestCallbacksMissingLogged;

        /// <summary>
        /// Whether the database manager is actually reachable, which is not what the kit's own flags report:
        /// IsClient and IsClientConnected both go true as soon as a socket exists and Connect() returned a peer,
        /// seconds before that peer connects and even when it never does. Only the transport knows.
        /// </summary>
        private bool IsDatabaseConnected
        {
            get
            {
                return _databaseManager != null && _databaseManager.IsClient && _databaseManager.Client != null
                    && _databaseManager.Client.Transport != null && _databaseManager.Client.Transport.IsClientStarted;
            }
        }

        /// <summary>OnStartServer's hook: a new server start has not connected yet and leaves the kit's first attempt alone for the grace period.</summary>
        private void DatabaseOnStartServer()
        {
            _databaseManager = MMOServerInstance.Singleton != null ? MMOServerInstance.Singleton.DatabaseClient as DatabaseNetworkManager : null;
            _databaseWasConnected = false;
            _databaseEverConnected = false;
            _databaseGraceUntil = Time.unscaledTime + Mathf.Max(1f, databaseReconnectGraceSeconds);
            _databaseDrainLogged = false;
            _databaseDrainSince = 0f;
            _nextDatabaseReconnect = 0f;
        }

        /// <summary>Server tick: re-open the database connection when it drops, which the kit never does.</summary>
        private void DatabaseConnectTick()
        {
            if (databaseReconnectSeconds <= 0f || _databaseManager == null)
                return;
            if (_databaseManager.IsServer)
                return;     // this process hosts the database manager itself; there is no client to re-open
            bool connected = IsDatabaseConnected;
            if (connected != _databaseWasConnected)
            {
                _databaseWasConnected = connected;
                if (connected)
                {
                    Logging.Log("Mesh", $"Database connection to {_databaseManager.networkAddress}:{_databaseManager.networkPort} is open");
                    _databaseEverConnected = true;
                    _databaseDrainLogged = false;
                }
            }
            if (connected || _databaseManager.IsClient)
            {
                // Either reachable, or a connection attempt is in flight. A failed attempt ends in ConnectionFailed,
                // which the transport turns into a disconnect and the kit into StopClient, so it lands back here.
                return;
            }
            if (!_databaseEverConnected && Time.unscaledTime < _databaseGraceUntil)
                return;     // the kit's own first StartClient may still be in flight; do not race it
            if (Time.unscaledTime < _nextDatabaseReconnect)
                return;
            LiteNetLibClient client = _databaseManager.Client;
            int inFlight = client != null ? client.RequestsCount : 0;
            ConcurrentDictionary<uint, LiteNetLibRequestCallback> table = inFlight > 0 ? RequestCallbacksOf(client) : null;
            if (inFlight > 0 && table == null && DatabaseDrainWaits(inFlight))
                return;
            _databaseDrainSince = 0f;
            _nextDatabaseReconnect = Time.unscaledTime + databaseReconnectSeconds;
            List<LiteNetLibRequestCallback> waiting = TakeRequestCallbacks(table);
            string note = waiting.Count > 0 ? $", timing out {waiting.Count} request(s) it can no longer answer"
                : inFlight > 0 ? $", stranding {inFlight} request(s) that waited out {databaseDrainWaitSeconds:0}s" : string.Empty;
            Logging.LogWarning("Mesh", $"Database connection lost; reconnecting to {_databaseManager.networkAddress}:{_databaseManager.networkPort}{note}");
            _databaseManager.StartClient();
            int failed = ExpireRequestCallbacks(waiting, out string firstFailure);
            if (failed > 0)
                Logging.LogWarning("Mesh", $"Database reconnect: {failed} of {waiting.Count} timed-out request handler(s) failed: {firstFailure}");
        }

        /// <summary>
        /// The pre-0.87 drain, kept for a kit whose request table cannot be reached: true while requests that were
        /// waiting when the connection dropped are given up to databaseDrainWaitSeconds to time out on their own.
        /// Bounded because this server keeps issuing requests that can never be sent, so the count rarely reaches zero;
        /// waiting for it cost 2m16s of database outage on the first run that tried it (2026-09-17).
        /// </summary>
        private bool DatabaseDrainWaits(int inFlight)
        {
            if (_databaseDrainSince <= 0f)
                _databaseDrainSince = Time.unscaledTime;
            if (Time.unscaledTime - _databaseDrainSince >= databaseDrainWaitSeconds)
                return false;
            if (!_databaseDrainLogged)
            {
                _databaseDrainLogged = true;
                Logging.LogWarning("Mesh", $"Database connection lost with {inFlight} request(s) in flight; letting them time out for up to {databaseDrainWaitSeconds:0}s");
            }
            return true;
        }

        /// <summary>The kit's request table of a client (TransportHandler._requestCallbacks), or null when this kit has none by that name.</summary>
        public static ConcurrentDictionary<uint, LiteNetLibRequestCallback> RequestCallbacksOf(TransportHandler handler)
        {
            if (handler == null)
                return null;
            if (s_requestCallbacksField == null)
                s_requestCallbacksField = typeof(TransportHandler).GetField("_requestCallbacks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (s_requestCallbacksField?.GetValue(handler) is ConcurrentDictionary<uint, LiteNetLibRequestCallback> table)
                return table;
            if (!s_requestCallbacksMissingLogged)
            {
                s_requestCallbacksMissingLogged = true;
                Logging.LogWarning("Mesh", "The kit's database request table is out of reach (TransportHandler._requestCallbacks); a reconnect waits for waiting requests to time out instead");
            }
            return null;
        }

        /// <summary>
        /// Takes every waiting callback out of the table, so neither the reconnect's clear nor the kit's own timeout
        /// (which only answers a callback it can still remove) reaches it: each is answered exactly once, by ExpireRequestCallbacks.
        /// </summary>
        public static List<LiteNetLibRequestCallback> TakeRequestCallbacks(ConcurrentDictionary<uint, LiteNetLibRequestCallback> table)
        {
            List<LiteNetLibRequestCallback> taken = new List<LiteNetLibRequestCallback>();
            if (table == null)
                return taken;
            foreach (uint requestId in new List<uint>(table.Keys))
            {
                if (table.TryRemove(requestId, out LiteNetLibRequestCallback callback) && callback != null)
                    taken.Add(callback);
            }
            return taken;
        }

        /// <summary>
        /// Answers taken callbacks with the kit's own Timeout (LiteNetLibRequestCallback.ResponseTimeout, the call its
        /// 30 s timer makes), after the reconnect: a caller that retries at once sends on the new connection. One handler
        /// that throws does not stop the rest; returns how many threw, and the first one's message.
        /// </summary>
        public static int ExpireRequestCallbacks(List<LiteNetLibRequestCallback> callbacks, out string firstFailure)
        {
            int failed = 0;
            firstFailure = null;
            foreach (LiteNetLibRequestCallback callback in callbacks)
            {
                try
                {
                    callback.ResponseTimeout();
                }
                catch (System.Exception e)
                {
                    failed++;
                    firstFailure ??= OneLine(e.Message);
                }
            }
            return failed;
        }

        /// <summary>OneLine keeps a message on one physical line.</summary>
        private static string OneLine(string text)
        {
            return string.IsNullOrEmpty(text) ? "no message" : text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
#endif
    }
}
