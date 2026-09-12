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

        [Tooltip("Seconds to fade the screenshot mask out once the player is back.")]
        public float migrationMaskFadeSeconds = 0.25f;

        [Header("Server Meshing — zone handoff (server side)")]
        [Tooltip("Zone grid definition. Each zone is served by a map server whose channelId == zoneId.")]
        public ServerMeshTestConfig meshConfig;

        [Tooltip("Run the per-tick boundary check on the server and hand players off to the neighbouring zone's map server.")]
        public bool enableServerBoundaryHandoff = true;

        [Tooltip("Seconds between boundary checks.")]
        public float boundaryCheckInterval = 0.1f;

        [Tooltip("Metres a player must be past the zone edge before ownership flips (anti-thrash).")]
        public float handoffHysteresis = 1f;

        public bool IsMigrating { get; private set; }

        /// <summary>Zone grid coordinate this map server owns (resolved from -zoneX/-zoneY args or from ChannelId == zoneId).</summary>
        public Vector2Int OwnZoneCoord { get; private set; } = new Vector2Int(-1, -1);
        public string OwnZoneId { get; private set; } = string.Empty;
        public bool HasOwnZone => OwnZoneCoord.x >= 0 && OwnZoneCoord.y >= 0;

        private bool _prevLoadOfflineScene;
        private ClientMigrationController _migration;

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

        public override void OnClientConnected()
        {
            Stamp("client connected");
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
            return ok;
        }

        public override UniTask<bool> DeserializeClientReadyData(uint requestId, long connectionId, NetDataReader reader, LiteNetLibIdentity playerIdentity)
        {
            Stamp($"client ready request from {connectionId}");
            return base.DeserializeClientReadyData(requestId, connectionId, reader, playerIdentity);
        }

        public override void RegisterPlayerCharacter(long connectionId, BasePlayerCharacterEntity playerCharacter)
        {
            Stamp($"player registered {playerCharacter.CharacterName} conn={connectionId}");
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (IsServer)
                GhostingOnRealPlayerRegistered(playerCharacter);
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
        private readonly List<BasePlayerCharacterEntity> _handoffCandidates = new List<BasePlayerCharacterEntity>();
        private float _nextBoundaryCheck;
        private float _nextMissingPeerWarning;

        protected override void Start()
        {
            base.Start();
            // base.Start() assigns the kit's handler with `=`; we append ours so both run.
            ClusterClient.onResponseAppServerAddress += OnMeshAppServerAddress;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _meshPeers.Clear();
            _handoffsInProgress.Clear();
            OwnZoneCoord = new Vector2Int(-1, -1);
            OwnZoneId = string.Empty;
            TryResolveOwnZone();
            GhostingOnStartServer();
        }

        public override void OnStopServer()
        {
            GhostingOnStopServer();
            base.OnStopServer();
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
        /// Resolve which zone this server owns: explicit -zoneX/-zoneY args (ServerMeshZoneBootstrap) win,
        /// otherwise the zone whose zoneId equals this server's ChannelId (the launch convention).
        /// </summary>
        private bool TryResolveOwnZone()
        {
            if (HasOwnZone)
                return true;
            if (meshConfig == null)
                return false;

            ServerMeshZoneBootstrap bootstrap = ServerMeshZoneBootstrap.Instance;
            if (bootstrap != null && bootstrap.HasZoneAssignment)
            {
                OwnZoneCoord = bootstrap.AssignedZoneCoord;
                ServerMeshTestConfig.ZoneDefinition z = meshConfig.GetZone(OwnZoneCoord);
                OwnZoneId = z != null ? z.zoneId : bootstrap.AssignedZoneId;
                Logging.Log("Mesh", $"Own zone from args: {OwnZoneId} {OwnZoneCoord}");
                return true;
            }

            if (!string.IsNullOrEmpty(ChannelId))
            {
                ServerMeshTestConfig.ZoneDefinition z = meshConfig.GetZoneById(ChannelId);
                if (z != null)
                {
                    OwnZoneCoord = new Vector2Int(z.gridX, z.gridY);
                    OwnZoneId = z.zoneId;
                    Logging.Log("Mesh", $"Own zone from channel id: {OwnZoneId} {OwnZoneCoord}");
                    return true;
                }
            }
            return false;
        }

        protected override void Update()
        {
            base.Update();
            if (IsServer)
            {
                MeshBoundaryTick();
                GhostingUpdate();
            }
        }

        /// <summary>DESIGN.md §11 trigger #1 — walked crossings are just movement, so we poll.</summary>
        private void MeshBoundaryTick()
        {
            if (!enableServerBoundaryHandoff || meshConfig == null)
                return;
            if (Time.unscaledTime < _nextBoundaryCheck)
                return;
            _nextBoundaryCheck = Time.unscaledTime + Mathf.Max(0.02f, boundaryCheckInterval);

            if (!TryResolveOwnZone())
                return;

            // Collect first: the handoff unregisters the character, which would mutate the collection.
            _handoffCandidates.Clear();
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                BasePlayerCharacterEntity entity = data as BasePlayerCharacterEntity;
                if (entity == null || entity.IsWarping || _handoffsInProgress.Contains(entity.Id))
                    continue;
                Vector3 pos = entity.EntityTransform.position;
                Vector2Int coord = meshConfig.WorldToZoneCoord(pos);
                if (coord == OwnZoneCoord)
                    continue;
                // Hysteresis: only flip once clearly past the edge, so loitering on the line doesn't thrash.
                if (meshConfig.IsNearBoundary(pos, handoffHysteresis))
                    continue;
                _handoffCandidates.Add(entity);
            }

            for (int i = 0; i < _handoffCandidates.Count; ++i)
            {
                BasePlayerCharacterEntity entity = _handoffCandidates[i];
                MeshHandoff(entity, meshConfig.WorldToZoneCoord(entity.EntityTransform.position)).Forget();
            }
            _handoffCandidates.Clear();
        }

        /// <summary>
        /// Replays the kit's own SaveAndWarpCharacterByPeerInfo (private in core) toward the neighbour
        /// zone's map server: unregister, save with the crossing position, destroy, tell the client to
        /// reconnect. The neighbour loads the character from the DB at the saved position (same logical
        /// map, same world frame in this rig), so the player continues where it crossed.
        /// </summary>
        private async UniTaskVoid MeshHandoff(BasePlayerCharacterEntity entity, Vector2Int toZone)
        {
            ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZone(toZone);
            if (zone == null)
            {
                WarnThrottled($"No zone definition for {toZone}; player {entity.CharacterName} stays on {OwnZoneId}.");
                return;
            }

            string mapName = CurrentMapInfo != null ? CurrentMapInfo.Id : string.Empty;
            string key = PeerInfoExtensions.GetPeerInfoKey(zone.zoneId, mapName);
            if (!_meshPeers.TryGetValue(key, out CentralServerPeerInfo peer))
            {
                WarnThrottled($"No map server registered for zone peer key '{key}' ({_meshPeers.Count} known); player {entity.CharacterName} stays on {OwnZoneId}.");
                return;
            }

            long connectionId = entity.ConnectionId;
            if (!ServerUserHandlers.TryGetPlayerCharacter(connectionId, out _))
                return;

            string characterId = entity.Id;
            _handoffsInProgress.Add(characterId);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Vector3 position = entity.EntityTransform.position;
            Vector3 rotation = entity.CurrentRotation;
            Logging.Log("Mesh", $"Handoff START {entity.CharacterName} ({characterId}) zone {OwnZoneId}->{zone.zoneId} pos={position} -> {peer.networkAddress}:{peer.networkPort}");

            try
            {
                // Mirrors MapNetworkManager.SaveAndWarpCharacterByPeerInfo (changeMap=true keeps the logical map name).
                entity.IsWarping = true;
                UnregisterPlayerCharacter(connectionId);
                if (entity.TryGetComponent(out PlayerCharacterDataUpdater updater))
                    Destroy(updater);
                DataUpdater.PlayerCharacterDataSaved(characterId);
                bool saved = await WaitAndSaveCharacter(TransactionUpdateCharacterState.All, entity.CloneTo(new PlayerCharacterData()), true, mapName, position, true, rotation);
                entity.IsWarping = false;
                entity.NetworkDestroy();

                MMOWarpMessage message = new MMOWarpMessage
                {
                    networkAddress = peer.networkAddress,
                    networkPort = peer.networkPort,
                };
                ServerSendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, GameNetworkingConsts.Warp, message);
                Logging.Log("Mesh", $"Handoff SENT {entity.CharacterName} -> zone {zone.zoneId} saved={saved} serverSide={sw.ElapsedMilliseconds}ms");
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

        private void WarnThrottled(string message)
        {
            if (Time.unscaledTime < _nextMissingPeerWarning)
                return;
            _nextMissingPeerWarning = Time.unscaledTime + 5f;
            Logging.LogWarning("Mesh", message);
        }

        #endregion
#endif
    }
}
