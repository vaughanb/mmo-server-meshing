// Server Meshing add-on — Spike 2 (client connection migration) + P1 server-side zone handoff.
// See Docs/ServerMeshing/SPIKE2.md and Docs/ServerMeshing/DESIGN.md (§11 "The seamless handoff").
//
// EXTENSION-ONLY: this subclasses the kit's MapNetworkManager and is swapped onto the
// map-network-manager prefab. MMOServerInstance/MMOClientInstance resolve it via
// GetComponentInChildren<MapNetworkManager>(), which matches subclasses, so NO core file is edited.
//
// Client side: intercept the warp message so an in-grid crossing is *masked* (screenshot held over a
// normal StopClient/StartClient while server B re-streams) instead of the default disconnect/reconnect.
//
// Server side (P1 "boundary tick", DESIGN.md §11 trigger #1): every tick, owned player characters are
// checked against this cell's zone. Once one is past the edge (+ hysteresis) the kit's own
// save-and-warp path is replayed toward the neighbour cell's map server (same logical map, different
// channel = zone), so the client receives a normal MMOWarpMessage and the masked migration runs.
// Peer addresses come from the cluster's map-server broadcast (no fixed ports).

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Insthync.DevExtension;
using LiteNetLib;
using System.Net.Sockets;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager : MapNetworkManager
    {
        [Header("Spike 2 — Client Migration")]
        [Tooltip("When true, client warps are routed through the masked migration controller instead of the raw StopClient/StartClient.")]
        public bool useMaskedMigration = true;

        [Tooltip("Max seconds to wait for the client to reconnect + the local player to re-spawn before giving up the mask.")]
        public float migrationReadyTimeoutSeconds = 10f;

        [Tooltip("Seconds to fade the screenshot mask out once the player is back (mask strategy only).")]
        public float migrationMaskFadeSeconds = 0.25f;

        [Tooltip("SEAMLESS: keep the world rendering through the swap with dead-reckoned visual stand-ins and a preserved controller/camera/HUD, instead of holding a screenshot.")]
        public bool useVisualStandIns = true;

        [Tooltip("SEAMLESS: a stand-in is retired when a real entity with the same model is within this distance.")]
        public float standInMatchDistance = 2.5f;

        [Tooltip("SEAMLESS: stand-ins that never get matched are removed after this long.")]
        public float standInMaxLifetimeSeconds = 1.5f;

        [Tooltip("SEAMLESS: seconds to glide the local stand-in onto the re-spawned entity before swapping (hides any dead-reckoning error).")]
        public float standInBlendSeconds = 0.3f;

        [Tooltip("SEAMLESS: after the blend, wait up to this long for the new entity's stats (MaxHp > 0) before re-binding the HUD, so it never shows 0/0.")]
        public float entityDataWaitSeconds = 1f;

        [Header("Server Meshing — zone handoff (server side)")]
        [Tooltip("World registry: every meshed map's cut into cells and their placement in the world. A cell is served by the map server whose channelId is the cell id.")]
        public MeshWorldRegistry world;

        [Tooltip("Run the per-tick boundary check on the server and hand players off to the neighbouring zone's map server.")]
        public bool enableServerBoundaryHandoff = true;

        [Tooltip("Seconds between boundary checks.")]
        public float boundaryCheckInterval = 0.1f;

        [Tooltip("Metres a player must be past the zone edge before ownership flips (anti-thrash).")]
        public float handoffHysteresis = 1f;

        [Tooltip("Seconds of the player's current velocity to add to the saved position, so the neighbour spawns it where the client's dead-reckoned stand-in will be by then (~the measured gap).")]
        public float handoffLeadSeconds = 0.3f;

        public bool IsMigrating { get; private set; }

        /// <summary>The cell this map server serves (from -zoneId/-zoneX/-zoneY args or from ChannelId == cell id, resolved against the world registry).</summary>
        public MeshCell OwnCell { get; private set; }
        public string OwnZoneId { get; private set; } = string.Empty;
        public bool HasOwnZone => OwnCell != null;

        private string _mapIdFromArgs;
        /// <summary>The kit map this server serves: the loaded map info, or the -mapName argument before the scene is up.</summary>
        public string OwnMapId
        {
            get
            {
                if (CurrentMapInfo != null && !string.IsNullOrEmpty(CurrentMapInfo.Id))
                    return CurrentMapInfo.Id;
                if (_mapIdFromArgs == null)
                    _mapIdFromArgs = ConfigReader.ReadArgs(System.Environment.GetCommandLineArgs(), ProcessArguments.ARG_MAP_NAME, out string fromArgs, string.Empty) ? fromArgs : string.Empty;
                return _mapIdFromArgs;
            }
        }

        private bool _prevLoadOfflineScene;
        private ClientMigrationController _migration;
        private MeshSendMeter _sendMeter;
        private Sync.MeshStateSender _stateSender;

        protected override void Awake()
        {
            world = MeshLayoutOverride.Resolve(world); // rig: MESH_LAYOUT_OVERRIDE re-cuts maps for a test run
            InstallSendMeter();
            InstallStateSender();
            base.Awake();
        }

        /// <summary>
        /// Phase 0 toggle MESH_SENDER (measure or on): adds the state sender before the kit collects its manager
        /// components. Unset, empty or off adds nothing and logs nothing; any other value logs one warning and adds nothing.
        /// </summary>
        private void InstallStateSender()
        {
            string value = System.Environment.GetEnvironmentVariable(Sync.MeshStateSender.EnvironmentKey);
            if (!Sync.MeshStateSender.TryParseSetting(value, out Sync.MeshStateSenderSetting setting, out string warning))
            {
                Logging.LogWarning("Mesh", warning);
                return;
            }
            if (setting == Sync.MeshStateSenderSetting.Off)
                return;
            Sync.MeshStateSender sender = gameObject.AddComponent<Sync.MeshStateSender>();
            sender.Setting = setting;
            _stateSender = sender;
            Logging.Log("Mesh", $"State sender: {Sync.MeshStateSender.EnvironmentKey}={Sync.MeshStateSender.CleanForLog(value.Trim())}: added before the kit collects its components");
        }

        /// <summary>
        /// Rig toggle MESH_METER_SENDS (count or time): wraps the kit's transport factory in a metered one, so every
        /// server send is counted for the Send window line. Unset, empty or off changes nothing and logs nothing. With the
        /// meter installed, MESH_METER_RECORD_SIZES=1 also counts record sizes; the key alone does nothing.
        /// </summary>
        private void InstallSendMeter()
        {
            string value = System.Environment.GetEnvironmentVariable(MeshMeteredTransportFactory.EnvironmentKey);
            if (!MeshMeteredTransportFactory.TryParseSetting(value, out bool meter, out bool timed, out string warning))
            {
                Logging.LogWarning("Mesh", warning);
                return;
            }
            if (!meter)
                return;
            // Captured before the metered factory exists, or the kit's GetComponent fallback would find the wrapper itself.
            BaseTransportFactory kit = TransportFactory;
            if (kit == null)
                kit = GetComponent<BaseTransportFactory>();
            if (kit == null)
                kit = null; // a missing component is a Unity fake null; the metered factory must see a real one
            MeshMeteredTransportFactory metered = gameObject.AddComponent<MeshMeteredTransportFactory>();
            metered.Inner = kit;
            metered.Meter = new MeshSendMeter(System.Diagnostics.Stopwatch.Frequency);
            metered.Timed = timed;
            if (!MeshRecordSizeCounts.TryParseSetting(System.Environment.GetEnvironmentVariable(MeshRecordSizeCounts.EnvironmentKey), out bool recordSizes, out string sizesWarning))
                Logging.LogWarning("Mesh", sizesWarning);
            metered.Meter.RecordSizes = recordSizes;
            TransportFactory = metered;
            _sendMeter = metered.Meter;
            Logging.Log("Mesh", MeshMeteredTransportFactory.SettingLine(timed));
        }

        private ClientMigrationController Migration
        {
            get
            {
                if (_migration == null)
                {
                    GameObject go = new GameObject("[Spike2] ClientMigrationController");
                    DontDestroyOnLoad(go);
                    _migration = go.AddComponent<ClientMigrationController>();
                    _migration.Setup(this);
                }
                return _migration;
            }
        }

        #region Client side — masked migration

        /// <summary>
        /// Client-side handler for the warp message. The kit base reads an <see cref="MMOWarpMessage"/>
        /// (address+port) and does StopClient(); StartClient(...). We instead drive a masked migration.
        /// </summary>
        protected override void HandleWarpAtClient(MessageHandlerData messageHandler)
        {
            if (!useMaskedMigration)
            {
                base.HandleWarpAtClient(messageHandler);
                return;
            }

            // NOTE: reading the message consumes the reader, so we do not fall back to base after this.
            MMOWarpMessage message = messageHandler.ReadMessage<MMOWarpMessage>();
            Migration.Migrate(message.networkAddress, message.networkPort);
        }

        /// <summary>Debug entry point so the spike can be driven without a portal/server cooperation.</summary>
        public void DebugMigrate(string address, int port)
        {
            Migration.Migrate(address, port);
        }

        /// <summary>
        /// Called by the controller right before StopClient(). We suppress the offline-scene reload so
        /// the current map scene stays loaded across the swap (relevant when A and B share a scene).
        /// We deliberately do NOT try to preserve the spawned-object registry here — that is the
        /// expensive path (would need a core edit, CE-1). The mask hides the re-stream instead.
        /// </summary>
        internal void BeginMigration()
        {
            _prevLoadOfflineScene = loadOfflineSceneWhenClientStopped;
            loadOfflineSceneWhenClientStopped = false;
            IsMigrating = true;
            // SPIKE-TODO: if a kit loading-screen UI appears on warp, suppress it here and restore in EndMigration().
        }

        internal void EndMigration()
        {
            loadOfflineSceneWhenClientStopped = _prevLoadOfflineScene;
            IsMigrating = false;
            MigrationDisconnected = false; // never carried into the next hop
        }

        /// <summary>Set while migrating when the new cell dropped us; the controller reads it, BeginMigrationAttempt clears it.</summary>
        internal bool MigrationDisconnected { get; private set; }
        internal DisconnectReason MigrationDisconnectReason { get; private set; }
        internal SocketError MigrationDisconnectError { get; private set; }
        internal byte[] MigrationDisconnectData { get; private set; }

        /// <summary>Counts the controller's StartClient calls within migrations, so an answer can be told from the attempt it belongs to.</summary>
        internal int MigrationAttempt { get; private set; }
        private int _migrationRefusedAttempt;
        /// <summary>The cell of the current attempt answered enter-game with anything but success: it will not seat us, and waiting on it is pointless.</summary>
        internal bool MigrationRefused => MigrationAttempt > 0 && _migrationRefusedAttempt == MigrationAttempt;

        /// <summary>
        /// Called by the controller after StopClient and before the StartClient of every hop (the first and the
        /// fallback). The order matters: stopping a client can complete its outstanding requests, and an enter-game
        /// answer from the connection just stopped must never be read as the next attempt's refusal.
        /// </summary>
        internal void BeginMigrationAttempt()
        {
            MigrationAttempt++;
            MigrationDisconnected = false;
        }

        /// <summary>
        /// During a migration a disconnect is a failed hop, not the end of the session: the kit's handler would clear
        /// the user id and access token (the fallback hop needs them) and show the disconnect dialog. The controller
        /// retries the cell we came from; only when that fails too does the kit's handler run (see FailMigration).
        /// </summary>
        public override void OnClientDisconnected(DisconnectReason reason, SocketError socketError, byte[] data)
        {
            if (IsMigrating && !IsServer)
            {
                Logging.LogWarning("Mesh", $"Disconnected during a migration ({reason}, {socketError}); the controller decides");
                MigrationDisconnected = true;
                MigrationDisconnectReason = reason;
                MigrationDisconnectError = socketError;
                MigrationDisconnectData = data;
                return;
            }
            base.OnClientDisconnected(reason, socketError, data);
        }

        /// <summary>The fallback failed as well: hand the session to the kit's disconnect handling.</summary>
        internal void FailMigration()
        {
            EndMigration();
            base.OnClientDisconnected(MigrationDisconnected ? MigrationDisconnectReason : DisconnectReason.Timeout, MigrationDisconnected ? MigrationDisconnectError : SocketError.TimedOut, MigrationDisconnected ? MigrationDisconnectData : null);
            MigrationDisconnected = false;
        }

        #endregion

        #region Timing trace (both sides) — millisecond stamps to attribute the migration gap

        [Tooltip("Log millisecond-stamped 'MeshTrace' lines at each step of connect → enter game → ready → spawn (client and server).")]
        public bool traceHandoffTiming = true;

        private void Stamp(string what)
        {
            if (traceHandoffTiming)
                Logging.Log("MeshTrace", $"{System.DateTime.Now:HH:mm:ss.fff} {(IsServer ? "S" : "C")} {what}");
        }

        [Tooltip("DEBUG (editor): save a screenshot every few frames during a seamless swap to <project>/Logs/MigrationFrames for visual inspection.")]
        public bool debugCaptureMigrationFrames = false;

        public override void OnClientConnected()
        {
            Stamp("client connected");
            _heldRpcs.Clear(); // anything held on the previous connection belongs to a cell we left
            if (!IsServer)
                _ = Migration; // create early so the velocity tracker has history before the first hop
            if (!_assetEventsHooked && Assets != null)
            {
                _assetEventsHooked = true;
                Assets.onInitializeStart.AddListener(() => Stamp("assets initialize start"));
                Assets.onInitializeFinish.AddListener(() => Stamp("assets initialize finish"));
            }
            base.OnClientConnected();
        }

        private bool _assetEventsHooked;

        [Tooltip("During a masked migration (same scene, assets already warm) skip the core's 1 s post-scene-load settle delay and its forced GC.Collect. Cuts ~1 s off the gap; extension-only (uses protected seams).")]
        public bool fastClientReadyOnMigration = true;

        public override void OnClientOnlineSceneLoaded()
        {
            Stamp("online scene loaded: begin (pools inited)");
            if (!IsServer)
                SnapshotSceneContent();
            if (IsMigrating && fastClientReadyOnMigration && !IsServer)
            {
                // Same as BaseGameNetworkManager.OnClientOnlineSceneLoaded minus (a) the INSTANTIATES_OBJECTS_DELAY
                // settle window, which we back-date so the ready check passes at once, and (b) RegisterEntities'
                // System.GC.Collect(). The scene did not change and the pools were just re-initialised, so both are
                // pure waiting during a hop.
                this.InvokeInstanceDevExtMethods("OnClientOnlineSceneLoaded");
                foreach (BaseGameNetworkManagerComponent component in ManagerComponents)
                    component.OnClientOnlineSceneLoaded(this);
                _clientSceneLoadedTime = Time.unscaledTime - INSTANTIATES_OBJECTS_DELAY;
                MeshRegisterSceneEntities();
                Stamp("online scene loaded: end (fast path, no GC/settle)");
                if (IsClientReadyToInstantiateObjects())
                    SendClientReady();
                else
                    ProceedUntilClientReady().Forget();
                return;
            }
            base.OnClientOnlineSceneLoaded();
            Stamp("online scene loaded: end (entities registered + GC)");
        }

        /// <summary>Mirror of the private BaseGameNetworkManager.RegisterEntities() without the forced GC.</summary>
        private void MeshRegisterSceneEntities()
        {
            foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsSortMode.InstanceID))
                area.RegisterPrefabs();
            foreach (HarvestableSpawnArea area in FindObjectsByType<HarvestableSpawnArea>(FindObjectsSortMode.InstanceID))
                area.RegisterPrefabs();
            foreach (ItemDropSpawnArea area in FindObjectsByType<ItemDropSpawnArea>(FindObjectsSortMode.InstanceID))
                area.RegisterPrefabs();
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
            GameInstance.AddMonsterCharacterEntities(FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.InstanceID));
            GameInstance.AddHarvestableEntities(FindObjectsByType<HarvestableEntity>(FindObjectsSortMode.InstanceID));
            GameInstance.AddItemDropEntities(FindObjectsByType<ItemDropEntity>(FindObjectsSortMode.InstanceID));
#endif
            PoolSystem.Clear();
        }

        protected override void HandleEnterGameResponse(ResponseHandlerData responseHandler, AckResponseCode responseCode, EnterGameResponseMessage response)
        {
            Stamp($"enter-game response {responseCode}");
            if (IsMigrating && !IsServer && responseCode != AckResponseCode.Success)
            {
                // A definitive answer -- most often a restarted destination that has no reservation for us. The
                // controller falls back at once instead of waiting out migrationReadyTimeoutSeconds on a cell that
                // has already said no (7 of the 10.5 s cross-machine crash gap were spent that way).
                _migrationRefusedAttempt = MigrationAttempt;
                Logging.LogWarning("Mesh", $"Enter-game refused ({responseCode}) on migration attempt {MigrationAttempt}");
            }
            base.HandleEnterGameResponse(responseHandler, responseCode, response);
        }

        public override void SendClientReady()
        {
            Stamp("send client ready");
            base.SendClientReady();
        }

        public override void OnPeerConnected(long connectionId)
        {
            Stamp($"peer connected {connectionId}");
            base.OnPeerConnected(connectionId);
        }

        public override async UniTask<bool> DeserializeEnterGameData(uint requestId, long connectionId, EnterGameRequestMessage request, NetDataReader reader)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Stamp($"enter-game request from {connectionId}");
            bool ok = await base.DeserializeEnterGameData(requestId, connectionId, request, reader);
            Stamp($"enter-game validated+loaded ok={ok} in {sw.ElapsedMilliseconds}ms");
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (ok && ServerUserHandlers.TryGetUserId(connectionId, out string traceUserId) && _pendingSpawnPlayerCharactersByUserId.TryGetValue(traceUserId, out IPlayerCharacterData tracePending))
                Stamp($"enter-game data for {tracePending.CharacterName}: position {(Vector3)tracePending.CurrentPosition} map {tracePending.CurrentMapName}");
#endif
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            // Arrival by handoff with a ghost already standing here: hand the kit the ghost as the "already spawned"
            // entity (CE-8 makes the pending map reachable), so its ready path re-owns and registers it in place.
            if (ok && IsServer && promoteGhostOnArrival
                && ServerUserHandlers.TryGetUserId(connectionId, out string userId)
                && _pendingSpawnPlayerCharactersByUserId.TryGetValue(userId, out IPlayerCharacterData pending)
                && pending is PlayerCharacterData pendingData
                && TryPromoteGhostForArrival(connectionId, userId, pendingData, out BasePlayerCharacterEntity promoted))
            {
                _pendingSpawnPlayerCharactersByUserId[userId] = promoted;
                Stamp($"arrival {pendingData.CharacterName}: ghost promoted in place, pending spawn replaced");
            }
#endif
            return ok;
        }

        public override UniTask<bool> DeserializeClientReadyData(uint requestId, long connectionId, NetDataReader reader, LiteNetLibIdentity playerIdentity)
        {
            Stamp($"client ready request from {connectionId}");
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            // The kit refuses client-ready on four conditions and logs none of them: it sets a UITextKeys for the
            // response and returns false, and the peer is dropped. Seen as a cell accepting enter-game, loading the
            // character, then kicking on ready with nothing in its log at all (2026-09-17, a direct login into a
            // cell that had outlived a central restart). Name the condition so the next one is not a guess.
            if (IsServer)
            {
                bool hasUser = ServerUserHandlers.TryGetUserId(connectionId, out string readyUserId);
                bool hasToken = hasUser && ServerUserHandlers.TryGetAccessToken(connectionId, out _);
                bool hasPending = hasUser && _pendingSpawnPlayerCharactersByUserId.ContainsKey(readyUserId);
                if (!hasUser || !hasToken || !hasPending)
                    Logging.LogWarning("Mesh", $"Client ready from {connectionId} is about to be refused: user={hasUser} token={hasToken} pendingSpawn={hasPending}");
                // The kit then runs SetPlayerReadyRoutine, which KICKS WITHOUT LOGGING on three conditions: a
                // database call failed, the client left, or the user is already in this cell's social registry
                // ("prevent nested login"). ValidatePlayerConnection's failures do log (the prefab's log level is
                // Info, so LogError is on), and a kick that arrives with nothing in any log is one of these three.
                // Name them. Do not warn on "the user is already registered in ServerUserHandlers": enter-game
                // registers that a moment earlier, so it is true on every successful login too (2026-09-17).
                else
                {
                    bool social = IsInKitSocialRegistry(readyUserId);
                    bool database = IsDatabaseConnected;
                    if (social || !database)
                        Logging.LogWarning("Mesh", $"Client ready from {connectionId} for user {readyUserId}: the kit will kick silently -- alreadyInSocialRegistry={social} databaseConnected={database}");
                }
            }
#endif
            return base.DeserializeClientReadyData(requestId, connectionId, reader, playerIdentity);
        }

        private static System.Reflection.FieldInfo s_socialField;

        /// <summary>
        /// Whether the kit's private per-cell social registry still holds this user. It is the "prevent nested
        /// login" check in SetPlayerReadyRoutine, which kicks without logging; it is cleared on unregister and by a
        /// central-driven cluster message, so an entry can outlive a session that ended while the central was gone.
        /// Reflection, because the field is private and this is a diagnostic, not a dependency.
        /// </summary>
        private bool IsInKitSocialRegistry(string userId)
        {
            if (s_socialField == null)
                s_socialField = typeof(MapNetworkManager).GetField("_socialCharactersByUserId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (s_socialField == null || string.IsNullOrEmpty(userId))
                return false;
            return s_socialField.GetValue(this) is System.Collections.Concurrent.ConcurrentDictionary<string, SocialCharacterData> social && social.ContainsKey(userId);
        }

        public override void RegisterPlayerCharacter(long connectionId, BasePlayerCharacterEntity playerCharacter)
        {
            Stamp($"player registered {playerCharacter.CharacterName} conn={connectionId} at {playerCharacter.EntityTransform.position} (data {(Vector3)playerCharacter.CurrentPosition})");
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (IsServer)
                GhostingOnRealPlayerRegistered(playerCharacter, connectionId);
#endif
            base.RegisterPlayerCharacter(connectionId, playerCharacter);
        }

        #endregion

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        #region Server side — zone handoff

        // Our own view of the cluster's map-server directory, keyed `{channelId}_{mapName}` like the kit's
        // (the kit's copy is private, so we listen to the same cluster broadcast).
        private readonly Dictionary<string, CentralServerPeerInfo> _meshPeers = new Dictionary<string, CentralServerPeerInfo>();
        private readonly HashSet<string> _handoffsInProgress = new HashSet<string>();
        private readonly Dictionary<string, float> _handoffRetryAfter = new Dictionary<string, float>(); // characterId -> unscaled time
        [Tooltip("After a handoff is aborted (save failed) wait this long before the boundary tick tries that character again.")]
        public float handoffRetrySeconds = 2f;
        private readonly List<BasePlayerCharacterEntity> _handoffCandidates = new List<BasePlayerCharacterEntity>();
        private float _nextBoundaryCheck;
        private float _nextMissingPeerWarning;

        protected override void Start()
        {
            base.Start();
            // base.Start() assigns the kit's handler with `=`; we append ours so both run.
            ClusterClient.onResponseAppServerAddress += OnMeshAppServerAddress;
            ClusterClient.onResponseAppServerRegister += OnMeshAppServerRegister;
        }

        [Tooltip("A map server restarted by MapSpawn registers with the cluster before the cluster has timed out its dead predecessor (same channel + map key) and the kit does not retry the refused registration, leaving a server that serves nobody. Retry every this many seconds until it is accepted.")]
        public float clusterRegisterRetrySeconds = 3f;
        private int _clusterRegisterRetries;

        [Tooltip("Seconds between attempts to open the cluster connection when it could not be opened at all. " +
                 "Once the transport client is up the kit takes over: a failed attempt raises ConnectionFailed, " +
                 "which the kit answers by reconnecting every five seconds, so a cell started before the central " +
                 "joins by itself. That recovery never starts if StartClient returns false, because the client is " +
                 "then inactive and its Update polls nothing, ever. 0 disables.")]
        public float clusterConnectRetrySeconds = 5f;
        private float _nextClusterConnectRetry;
        private bool _clusterHandedToKit;

        /// <summary>Server tick: open the cluster connection if start-up could not, then leave it to the kit.</summary>
        private void ClusterConnectTick()
        {
            if (clusterConnectRetrySeconds <= 0f || _clusterHandedToKit || ClusterClient == null)
                return;
            if (ClusterClient.IsNetworkActive)
            {
                // The transport client is up, so events flow and the kit's own reconnect loop covers every later
                // failure. Stand down for good rather than racing its five-second countdown, during which the
                // client is briefly inactive and a retry here would start a second one.
                _clusterHandedToKit = true;
                return;
            }
            if (Time.unscaledTime < _nextClusterConnectRetry)
                return;
            _nextClusterConnectRetry = Time.unscaledTime + clusterConnectRetrySeconds;
            Logging.Log("Mesh", $"Could not open the cluster connection to {ClusterServerAddress}:{ClusterServerPort}; retrying");
            ClusterClient.OnAppStart();
        }

        private bool _clusterWasRegistered;

        /// <summary>
        /// Server tick: the moment the cluster link drops, forget every mirrored user who is not connected here.
        ///
        /// The kit keeps a per-cell mirror of every online user in the world (_socialCharactersByUserId), fed by the
        /// central's UpdateMapUser Add/Remove relays, and SetPlayerReadyRoutine kicks -- without logging -- anyone the
        /// mirror lists ("prevent nested login"). The central keeps no user list, so when it dies every Remove in
        /// flight is lost and the entries stay; worse, on re-registration the kit re-announces its WHOLE mirror as
        /// Adds, which the fresh central relays to every cell, so one stale entry anywhere is "online" everywhere and
        /// that user is kicked from every cell until the whole cluster restarts. Reproduced 2026-09-18: a user online
        /// on one cell when the central dies cannot log into a cell that outlived it. Purging on the drop, before the
        /// kit's re-announcement, leaves only this cell's own players to be re-announced; the central relays the
        /// other cells' live players again as they re-register.
        /// </summary>
        private void ClusterMirrorTick()
        {
            bool registered = ClusterClient != null && ClusterClient.IsAppRegistered;
            if (_clusterWasRegistered && !registered)
                PurgeRemoteSocialMirror();
            _clusterWasRegistered = registered;
        }

        private void PurgeRemoteSocialMirror()
        {
            if (s_socialField == null)
                s_socialField = typeof(MapNetworkManager).GetField("_socialCharactersByUserId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (!(s_socialField?.GetValue(this) is System.Collections.Concurrent.ConcurrentDictionary<string, SocialCharacterData> mirror))
                return;
            int dropped = 0, kept = 0;
            foreach (string userId in new System.Collections.Generic.List<string>(mirror.Keys))
            {
                if (ServerUserHandlers.TryGetConnectionIdByUserId(userId, out _)) { kept++; continue; }
                if (mirror.TryRemove(userId, out _)) dropped++;
            }
            Logging.LogWarning("Mesh", $"Cluster link lost: forgot {dropped} mirrored user(s) of other cells, kept {kept} of this cell's own; the central re-sends the live ones");
        }

        [Tooltip("Seconds between attempts to open the database connection whenever this server does not have one. " +
                 "The kit opens it once at start-up and never again: the transport calls Manager.StopClient() on a " +
                 "disconnect and nothing restarts it, so a map server that loses the database manager -- or that " +
                 "started before it existed -- keeps running and keeps accepting players while every save silently " +
                 "fails. 0 disables.")]
        public float databaseReconnectSeconds = 5f;
        [Tooltip("How long after this server starts to leave the database connection alone, so the retry never " +
                 "races the kit's own first StartClient.")]
        public float databaseReconnectGraceSeconds = 15f;
        [Tooltip("How long to let database requests that were still in flight when the connection dropped expire " +
                 "before reconnecting anyway. Reconnecting clears the callback table, so anything still waiting is " +
                 "stranded; but a busy server always has a request in flight, so waiting for the table to empty is " +
                 "waiting for a gap that may never come. Slightly over the kit's 30 s request timeout.")]
        public float databaseDrainWaitSeconds = 35f;
        private float _nextDatabaseReconnect;
        private DatabaseNetworkManager _databaseManager;
        private bool _databaseWasConnected;
        private bool _databaseDrainLogged;
        private float _databaseDrainSince;
        private bool _databaseEverConnected;
        private float _databaseGraceUntil;

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
            int inFlight = _databaseManager.Client != null ? _databaseManager.Client.RequestsCount : 0;
            if (inFlight > 0)
            {
                // Requests that were in flight when the connection dropped are still waiting on their own 30 s
                // timeout, and StartClient clears the callback table, so reconnecting strands whoever awaits them.
                // Give them that long to expire -- but no longer: this server keeps issuing new requests that can
                // never be sent, so the count rarely reaches zero, and waiting for it cost 2m16s of total database
                // outage on the first run that tried it (2026-09-17). A few stranded callbacks that were doomed
                // anyway beat every save failing for minutes.
                if (_databaseDrainSince <= 0f)
                    _databaseDrainSince = Time.unscaledTime;
                if (Time.unscaledTime - _databaseDrainSince < databaseDrainWaitSeconds)
                {
                    if (!_databaseDrainLogged)
                    {
                        _databaseDrainLogged = true;
                        Logging.LogWarning("Mesh", $"Database connection lost with {inFlight} request(s) in flight; letting them time out for up to {databaseDrainWaitSeconds:0}s");
                    }
                    return;
                }
            }
            _databaseDrainSince = 0f;
            _nextDatabaseReconnect = Time.unscaledTime + databaseReconnectSeconds;
            string stranded = inFlight > 0 ? $", stranding {inFlight} request(s) that waited out {databaseDrainWaitSeconds:0}s" : string.Empty;
            Logging.LogWarning("Mesh", $"Database connection lost; reconnecting to {_databaseManager.networkAddress}:{_databaseManager.networkPort}{stranded}");
            _databaseManager.StartClient();
        }

        private void OnMeshAppServerRegister(AckResponseCode responseCode)
        {
            if (responseCode == AckResponseCode.Success)
            {
                if (_clusterRegisterRetries > 0)
                    Logging.Log("Mesh", $"Cluster registration accepted after {_clusterRegisterRetries} retr{(_clusterRegisterRetries == 1 ? "y" : "ies")}");
                _clusterRegisterRetries = 0;
                SendLayoutHello();
                return;
            }
            if (!IsServer)
                return;
            _clusterRegisterRetries++;
            Logging.LogWarning("Mesh", $"Cluster registration refused ({responseCode}); retrying in {clusterRegisterRetrySeconds:0.#} s (attempt {_clusterRegisterRetries})");
            RetryClusterRegister().Forget();
        }

        private async UniTaskVoid RetryClusterRegister()
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(Mathf.Max(0.5f, clusterRegisterRetrySeconds)), true);
            if (!IsServer || ClusterClient == null || ClusterClient.IsAppRegistered || !ClusterClient.IsNetworkActive)
                return;
            ClusterClient.RequestAppServerRegister(new CentralServerPeerInfo
            {
                peerType = PeerType,
                networkAddress = AppAddress,
                networkPort = AppPort,
                channelId = ChannelId,
                refId = RefId,
            });
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _meshPeers.Clear();
            _handoffsInProgress.Clear();
            OwnCell = null;
            OwnZoneId = string.Empty;
            TryResolveOwnZone();
            _clusterHandedToKit = false;
            _clusterWasRegistered = false;
            _databaseManager = MMOServerInstance.Singleton != null ? MMOServerInstance.Singleton.DatabaseClient as DatabaseNetworkManager : null;
            _databaseWasConnected = false;
            _databaseEverConnected = false;
            _databaseGraceUntil = Time.unscaledTime + Mathf.Max(1f, databaseReconnectGraceSeconds);
            _databaseDrainLogged = false;
            _databaseDrainSince = 0f;
            _nextDatabaseReconnect = 0f;
            _nextClusterConnectRetry = Time.unscaledTime + Mathf.Max(1f, clusterConnectRetrySeconds);
            if (ClusterClient != null)
            {
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.Sleep, HandleSleepMessage);
                // A request needs its response type registered on the sending side, or the transport throws
                // before anything is sent (TransportHandler.CreateAndWriteRequest).
                ClusterClient.RegisterResponseHandler<MeshWakeRequest, MeshWakeResponse>(MeshClusterMsg.Wake);
            }
            LiteNetLibManager.LiteNetLibTransport statsTransport = MeshTransportUnwrap.Innermost<LiteNetLibManager.LiteNetLibTransport>(ServerTransport);
            if (statsTransport != null && statsTransport.Server != null)
                statsTransport.Server.EnableStatistics = true; // client traffic in the Mesh stats line
            if (_sendMeter != null && !(ServerTransport is MeshMeteredTransport))
                Logging.LogError("Mesh", $"Send meter error: {MeshMeteredTransportFactory.EnvironmentKey} asked for a send meter but the server transport is {(ServerTransport == null ? "none" : ServerTransport.GetType().Name)}, not the metered one (useWebSocket replaces the transport factory); the Send window lines of this run count nothing");
            // Test rig toggle: MESH_NO_MONSTER_HANDOFF=1 in the environment keeps monsters with their spawn cell even
            // when they chase across the line (forces cross-edge kills so loot ghosting can be exercised).
            if (System.Environment.GetEnvironmentVariable("MESH_NO_MONSTER_HANDOFF") == "1")
            {
                handoffMonsters = false;
                Logging.Log("Mesh", "MESH_NO_MONSTER_HANDOFF=1: monster handoff disabled on this cell");
            }
            // Test rig toggle: MESH_GHOST_LEAD=<seconds> overrides the ghost steering lead (0 = puppets trail by a snapshot).
            string leadEnv = System.Environment.GetEnvironmentVariable("MESH_GHOST_LEAD");
            if (!string.IsNullOrEmpty(leadEnv) && float.TryParse(leadEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float leadSeconds))
            {
                ghostLeadSeconds = leadSeconds;
                Logging.Log("Mesh", $"MESH_GHOST_LEAD={leadEnv}: ghost steering lead set to {leadSeconds:0.##} s");
            }
            ApplyBandOverride();
            GhostingOnStartServer();
            LoadOnStartServer();
            LayoutOnStartServer();
            MeshExceptionTrace.Install();
        }

        public override void OnStopServer()
        {
            MeshExceptionTrace.Uninstall();
            LayoutOnStopServer();
            GhostingOnStopServer();
            _velocities.Clear();
            _handoffsInProgress.Clear();
            _handoffRetryAfter.Clear();
            base.OnStopServer();
        }

        public override void OnPeerDisconnected(long connectionId, DisconnectReason reason, SocketError socketError)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            // The kit's relates-data loader destroys and kicks on a DB failure; with the background load of a handoff
            // arrival that can hit an entity that is already registered. Unregister a dead entity first so the kit's
            // despawn path (SetOwnerClient, StopMove, CloneTo) never runs on a destroyed object.
            if (IsServer && ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData data)
                && data is BasePlayerCharacterEntity dead && (dead == null || dead.Identity == null || dead.Identity.IsDestroyed))
            {
                Logging.LogWarning("Mesh", $"Connection {connectionId} disconnected with a destroyed character entity; unregistering before the kit's despawn");
                UnregisterPlayerCharacter(connectionId);
            }
#endif
            if (IsServer)
                GhostingOnPeerDisconnected(connectionId);
            base.OnPeerDisconnected(connectionId, reason, socketError);
        }

        public override void UnregisterPlayerCharacter(long connectionId)
        {
            if (IsServer)
                GhostingOnRealPlayerUnregistered(connectionId);
            base.UnregisterPlayerCharacter(connectionId);
        }

        private void OnMeshAppServerAddress(AckResponseCode responseCode, CentralServerPeerInfo peerInfo)
        {
            if (responseCode != AckResponseCode.Success || peerInfo.peerType != CentralServerPeerType.MapServer)
                return;
            if (string.IsNullOrEmpty(peerInfo.channelId) || string.IsNullOrEmpty(peerInfo.refId))
                return;
            string key = peerInfo.GetPeerInfoKey();
            _meshPeers[key] = peerInfo;
            Logging.Log("Mesh", $"Zone peer registered: {key} -> {peerInfo.networkAddress}:{peerInfo.networkPort}");
        }

        /// <summary>
        /// Resolve which cell this server serves: an explicit -zoneId (or -zoneX/-zoneY) argument wins, otherwise the
        /// cell of this server's map whose id equals its ChannelId (the map-spawner convention). Needs the map name,
        /// which is known from the -mapName argument before the scene loads.
        /// </summary>
        private bool TryResolveOwnZone()
        {
            if (HasOwnZone)
                return true;
            if (world == null)
                return false;
            string mapId = OwnMapId;
            if (string.IsNullOrEmpty(mapId) || !world.HasLayout(mapId))
                return false;

            MeshCell cell = null;
            string how = string.Empty;
            ServerMeshZoneBootstrap bootstrap = ServerMeshZoneBootstrap.Instance;
            if (bootstrap != null && bootstrap.HasZoneAssignment)
            {
                cell = !string.IsNullOrEmpty(bootstrap.AssignedZoneId) ? world.GetCell(mapId, bootstrap.AssignedZoneId) : null;
                if (cell == null)
                    cell = world.CellAtGrid(mapId, bootstrap.AssignedZoneCoord.x, bootstrap.AssignedZoneCoord.y);
                how = "args";
            }
            if (cell == null && !string.IsNullOrEmpty(ChannelId))
            {
                cell = world.GetCell(mapId, ChannelId);
                how = "channel id";
            }
            if (cell == null)
                return false;
            OwnCell = cell;
            OwnZoneId = cell.id;
            Logging.Log("Mesh", $"Own cell from {how}: {cell.id} of map {mapId}, grid ({cell.gridX},{cell.gridY}), world x [{cell.worldMinX:0}, {cell.worldMaxX:0}) z [{cell.worldMinZ:0}, {cell.worldMaxZ:0}), {cell.neighbours.Count} neighbours");
            return true;
        }

        protected override void Update()
        {
            base.Update();
            if (IsServer)
            {
                SampleFrame();
                ClusterConnectTick();
                ClusterMirrorTick();
                DatabaseConnectTick();
                LayoutTick();
                MeshBoundaryTick();
                GhostingUpdate();
                LoadTick();
            }
            else
            {
                ReplayHeldRpcs();
                SeamClientUpdate();
            }
        }

        #region Server RPCs held until the playing character exists (client)
        // The kit parks an RPC that arrives before its entity's spawn and replays it at that spawn; its combat-text
        // handler then reads GameInstance.PlayingCharacterEntity, which is null from a hop's disconnect until the
        // migration controller adopts the new entity (a frame after it spawns; the login has the same window). The
        // exception aborts the rest of that game-state message, spawns included. So a pure client holds every server
        // RPC that arrives while there is no playing character and replays them, in order, as soon as there is one.
        private struct HeldRpc { public ushort messageType; public TransportHandler transportHandler; public long connectionId; public byte[] data; }
        private readonly List<HeldRpc> _heldRpcs = new List<HeldRpc>();
        private float _heldRpcsSince;
        [Tooltip("Client: server RPCs that arrive while there is no playing character (during a hop, or at login) wait for one; after this many seconds they are delivered anyway.")]
        public float heldRpcTimeoutSeconds = 15f;

        private bool CanDeliverRpcs()
        {
            if (IsServer || GameInstance.PlayingCharacterEntity != null)
                return true;
            return _heldRpcs.Count > 0 && Time.unscaledTime - _heldRpcsSince >= heldRpcTimeoutSeconds;
        }

        protected override void HandleServerCallFunction(MessageHandlerData messageHandler)
        {
            if (!CanDeliverRpcs())
            {
                if (_heldRpcs.Count == 0)
                    _heldRpcsSince = Time.unscaledTime;
                NetDataReader reader = messageHandler.Reader;
                byte[] data = new byte[reader.AvailableBytes];
                System.Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, data.Length);
                _heldRpcs.Add(new HeldRpc { messageType = messageHandler.MessageType, transportHandler = messageHandler.TransportHandler, connectionId = messageHandler.ConnectionId, data = data });
                return;
            }
            ReplayHeldRpcs();
            base.HandleServerCallFunction(messageHandler);
        }

        private void ReplayHeldRpcs()
        {
            if (_heldRpcs.Count == 0 || !CanDeliverRpcs())
                return;
            Stamp($"replaying {_heldRpcs.Count} RPCs held while there was no playing character");
            for (int i = 0; i < _heldRpcs.Count; ++i)
            {
                HeldRpc held = _heldRpcs[i];
                base.HandleServerCallFunction(new MessageHandlerData(held.messageType, held.transportHandler, held.connectionId, new NetDataReader(held.data)));
            }
            _heldRpcs.Clear();
        }
        #endregion

        /// <summary>DESIGN.md §11 trigger #1 — walked crossings are just movement, so we poll.</summary>
        private void MeshBoundaryTick()
        {
            if (!enableServerBoundaryHandoff || world == null)
                return;
            if (Time.unscaledTime < _nextBoundaryCheck)
                return;
            _nextBoundaryCheck = Time.unscaledTime + Mathf.Max(0.02f, boundaryCheckInterval);

            if (!TryResolveOwnZone())
                return;
            if (IsLayoutGated)
                return; // a Dynamic map's cell hands nobody out before it knows the layout

            // Collect first: the handoff unregisters the character, which would mutate the collection.
            _handoffCandidates.Clear();
            float now = Time.unscaledTime;
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                BasePlayerCharacterEntity entity = data as BasePlayerCharacterEntity;
                if (entity == null || entity.IsWarping || _handoffsInProgress.Contains(entity.Id))
                    continue;
                if (_handoffRetryAfter.TryGetValue(entity.Id, out float retryAt))
                {
                    if (Time.unscaledTime < retryAt)
                        continue;
                    _handoffRetryAfter.Remove(entity.Id);
                }
                Vector3 pos = entity.EntityTransform.position;
                TrackVelocity(entity.Id, pos, now);
                if (MeshCell.Same(world.OwnerOf(OwnCell, pos), OwnCell))
                    continue;
                // Hysteresis: only flip once clearly past the edge, so loitering on the line doesn't thrash.
                if (-OwnCell.DistanceToEdge(pos) < handoffHysteresis)
                    continue;
                _handoffCandidates.Add(entity);
            }

            for (int i = 0; i < _handoffCandidates.Count; ++i)
            {
                BasePlayerCharacterEntity entity = _handoffCandidates[i];
                MeshHandoff(entity, world.OwnerOf(OwnCell, entity.EntityTransform.position)).Forget();
            }
            _handoffCandidates.Clear();
        }

        /// <summary>
        /// Replays the kit's own SaveAndWarpCharacterByPeerInfo (private in core) toward the neighbour
        /// zone's map server: unregister, save with the crossing position, destroy, tell the client to
        /// reconnect. The neighbour loads the character from the DB at the saved position (same logical
        /// map, same world frame in this rig), so the player continues where it crossed.
        /// </summary>
        private async UniTaskVoid MeshHandoff(BasePlayerCharacterEntity entity, MeshCell zone)
        {
            if (zone == null || MeshCell.Same(zone, OwnCell))
                return;

            string mapName = zone.mapId; // the destination's map: this logical map today, a neighbouring one once continents touch
            string key = PeerInfoExtensions.GetPeerInfoKey(zone.id, mapName);
            if (!_meshPeers.TryGetValue(key, out CentralServerPeerInfo peer))
            {
                WarnThrottled($"No map server registered for zone peer key '{key}' ({_meshPeers.Count} known); player {entity.CharacterName} stays on {OwnZoneId}.");
                RequestWake(zone); // sleeping cells: the ring should have woken it already; this is the safety net
                return;
            }

            long connectionId = entity.ConnectionId;
            if (!ServerUserHandlers.TryGetPlayerCharacter(connectionId, out _))
                return;
            if (!IsNeighbourLinked(zone.id))
            {
                // The cluster still lists the peer but our link to it is down: the notice and the ack could not be
                // delivered and the client might be warped to a dead cell. Stay, retry after the backoff.
                WarnThrottled($"Mesh link to zone {zone.id} is down; player {entity.CharacterName} stays on {OwnZoneId} (retry in {handoffRetrySeconds:0.#} s).");
                _handoffRetryAfter[entity.Id] = Time.unscaledTime + handoffRetrySeconds;
                return;
            }
            if (!IsNeighbourSettled(zone.id))
            {
                // The link just came up: give the destination a snapshot or two so it has a puppet to promote.
                _handoffRetryAfter[entity.Id] = Time.unscaledTime + handoffLinkSettleSeconds;
                return;
            }

            string characterId = entity.Id;
            _handoffsInProgress.Add(characterId);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            float startTime = Time.unscaledTime;
            Vector3 position = entity.EntityTransform.position;
            Vector3 rotation = entity.CurrentRotation;
            // Lead the saved position by the expected gap so the neighbour spawns the character where the
            // client's dead-reckoned stand-in will be, instead of ~gap*speed behind it.
            Vector3 velocity = _velocities.TryGetValue(characterId, out VelocitySample vs) ? vs.vel : Vector3.zero;
            velocity.y = 0f;
            Vector3 savePosition = position + velocity * Mathf.Max(0f, handoffLeadSeconds);
            // The row is read by the destination, in ITS frame (the same frame inside a map; a neighbouring map's frame
            // sits at a fixed offset, so a seam crossing is a frame change and a map change in one hop).
            Vector3 frameOffset = OwnCell.OffsetTo(zone);
            savePosition += frameOffset;
            _velocities.Remove(characterId);
            Logging.Log("Mesh", $"Handoff START {entity.CharacterName} ({characterId}) zone {OwnZoneId}->{zone.id} pos={position} vel={velocity} save={savePosition}{(frameOffset != Vector3.zero ? $" (frame offset {frameOffset}, map {mapName})" : string.Empty)} -> {peer.networkAddress}:{peer.networkPort}");

            try
            {
                // Mirrors MapNetworkManager.SaveAndWarpCharacterByPeerInfo (changeMap=true keeps the logical map name),
                // except IsWarping is NOT raised: it would show the kit's "warping" UI on the client, and the entity
                // is unregistered and destroyed within milliseconds anyway.
                // The destination keeps its ghost of this character until the real one registers there (no blink for
                // its observers): make sure neither the unregister nor the next snapshot tick sends it a GhostRemove.
                GhostingOnPlayerHandoffStart(characterId, zone.id);
                UnregisterPlayerCharacter(connectionId);
                DataUpdater.PlayerCharacterDataSaved(characterId);
                bool saved = false;
                try
                {
                    saved = await WaitAndSaveCharacter(TransactionUpdateCharacterState.All, entity.CloneTo(new PlayerCharacterData()), true, mapName, savePosition, true, rotation);
                }
                catch (System.Exception ex)
                {
                    Logging.LogException("Mesh", ex);
                }
                if (!saved || entity == null || entity.Identity == null || entity.Identity.IsDestroyed || !ContainsConnectionId(connectionId))
                {
                    // The destination would load a stale row (or the client is gone): abort, keep the character here,
                    // re-register it so the kit keeps saving it, and let the boundary tick retry after a short backoff.
                    Logging.LogWarning("Mesh", $"Handoff ABORTED {entity?.CharacterName} -> zone {zone.id}: saved={saved}; character stays on {OwnZoneId}");
                    if (entity != null && entity.Identity != null && !entity.Identity.IsDestroyed && ContainsConnectionId(connectionId))
                        RegisterPlayerCharacter(connectionId, entity);
                    _handoffRetryAfter[characterId] = Time.unscaledTime + handoffRetrySeconds;
                    return;
                }
                if (sw.ElapsedMilliseconds > 500)
                    Logging.LogWarning("Mesh", $"Handoff save for {entity.CharacterName} took {sw.ElapsedMilliseconds} ms; the saved position may lag the client");
                if (entity.TryGetComponent(out PlayerCharacterDataUpdater updater))
                    Destroy(updater);
                if (!demotePlayerOnHandoff)
                    entity.NetworkDestroy();

                // Tell the neighbour who is coming so it can spawn without re-awaiting relates data.
                if (ServerUserHandlers.TryGetUserId(connectionId, out string userId))
                    SendHandoffNotice(zone.id, characterId, userId);
                else
                    SendHandoffNotice(zone.id, characterId, entity.UserId);

                MMOWarpMessage message = new MMOWarpMessage
                {
                    networkAddress = peer.networkAddress,
                    networkPort = peer.networkPort,
                };
                ServerSendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, GameNetworkingConsts.Warp, message);
                // After the warp packet (ordered): the entity becomes a server-owned puppet that survives the client's
                // disconnect; observers on this cell keep seeing it, now steered by the neighbour's snapshots.
                if (demotePlayerOnHandoff && entity != null)
                    DemotePlayerToGhost(entity, zone.id);
                RegisterPendingHandoffAck(characterId, entity.CharacterName, zone.id, startTime);
                Logging.Log("Mesh", $"Handoff SENT {entity.CharacterName} -> zone {zone.id} saved={saved} serverSide={sw.ElapsedMilliseconds}ms demoted={demotePlayerOnHandoff}");
            }
            catch (System.Exception ex)
            {
                Logging.LogException("Mesh", ex);
            }
            finally
            {
                _handoffsInProgress.Remove(characterId);
            }
        }

        private struct VelocitySample { public Vector3 pos; public Vector3 vel; public float t; }
        private readonly Dictionary<string, VelocitySample> _velocities = new Dictionary<string, VelocitySample>();

        [Tooltip("Velocity samples above this (m/s) are treated as teleports/respawns and ignored for lead prediction.")]
        public float handoffMaxLeadSpeed = 15f;

        [Tooltip("On handoff, keep the departing player's entity as a ghost puppet (steered by the new owner) instead of destroying it, so other players on this cell see no blink. The destination cell likewise keeps its ghost until the real character registers.")]
        public bool demotePlayerOnHandoff = true;

        [Tooltip("On arrival by handoff, reuse this cell's ghost of the character as its real entity (the kit's own 'already spawned entity' ready path) instead of spawning a new one: observers here see no entity swap at all.")]
        public bool promoteGhostOnArrival = true;

        private void TrackVelocity(string characterId, Vector3 pos, float now)
        {
            if (_velocities.TryGetValue(characterId, out VelocitySample s) && now - s.t > 0.0001f)
            {
                Vector3 v = (pos - s.pos) / (now - s.t);
                if (v.magnitude > handoffMaxLeadSpeed)
                    v = Vector3.zero; // teleport / respawn / spawn: not a walking velocity
                _velocities[characterId] = new VelocitySample { pos = pos, vel = Vector3.Lerp(s.vel, v, 0.5f), t = now };
            }
            else
                _velocities[characterId] = new VelocitySample { pos = pos, vel = Vector3.zero, t = now };
        }

        private void WarnThrottled(string message)
        {
            if (Time.unscaledTime < _nextMissingPeerWarning)
                return;
            _nextMissingPeerWarning = Time.unscaledTime + 5f;
            Logging.LogWarning("Mesh", message);
        }

        #endregion
#endif

        // ---- sleeping cells (DESIGN.md Q13): the central tells an unwanted cell to exit; a cell asks it to wake a neighbour ----
        private float _nextWakeRequest;

        private void HandleSleepMessage(MessageHandlerData messageHandler)
        {
            MeshSleepMessage message = messageHandler.ReadMessage<MeshSleepMessage>();
            if (!string.Equals(message.cellId, OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
            {
                Logging.LogWarning("Mesh", $"Sleep message for cell {message.cellId} reached cell {OwnZoneId}; ignored");
                return;
            }
            if (OwnCell != null && OwnCell.isDynamic)
            {
                Logging.Log("Mesh", $"Sleep ignored: cell {OwnZoneId} is a dynamic cell");
                return;
            }
            int players = 0;
            foreach (IPlayerCharacterData _ in ServerUserHandlers.GetPlayerCharacters()) players++;
            if (players > 0 || Players.Count > 0)
            {
                Logging.Log("Mesh", $"Sleep asked for cell {message.cellId} but {players} player(s) / {Players.Count} connection(s) are here: staying up");
                return;
            }
            Logging.Log("Mesh", $"Sleeping: cell {OwnZoneId} has nobody near it; saving and exiting");
            Application.Quit(); // the kit's quit routine saves buildings, then quits for real
        }

        /// <summary>Ask the central to wake a cell (throttled). The awake ring normally makes this unnecessary.</summary>
        private void RequestWake(MeshCell cell)
        {
            if (cell == null || ClusterClient == null || !ClusterClient.IsAppRegistered || Time.unscaledTime < _nextWakeRequest)
                return;
            _nextWakeRequest = Time.unscaledTime + 5f;
            string cellId = cell.id;
            bool sent = ClusterClient.SendRequest(MeshClusterMsg.Wake, new MeshWakeRequest { cellId = cell.id, mapId = cell.mapId },
                (responseHandler, responseCode, response) =>
                {
                    if (responseCode != AckResponseCode.Success)
                        Logging.LogWarning("Mesh", $"Wake request for cell {cellId} answered {responseCode}");
                    else if (response is MeshWakeResponse wake && !wake.accepted && wake.reason == MeshWakeRefusal.UnknownCell)
                        Logging.LogWarning("Mesh", $"Wake request for cell {cellId} refused: the central does not know that cell");
                    // SleepingOff: every cell runs already, so the neighbour is simply not registered yet; nothing to do.
                });
            if (sent)
                Logging.Log("Mesh", $"Wake requested for sleeping cell {cell.id} ({cell.mapId})");
            else
                Logging.LogWarning("Mesh", $"Wake request for cell {cell.id} could not be sent");
        }

    }
}
