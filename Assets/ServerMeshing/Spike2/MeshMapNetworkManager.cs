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
        [Tooltip("Zone grid definition. Each zone is served by a map server whose channelId == zoneId.")]
        public ServerMeshTestConfig meshConfig;

        [Tooltip("Run the per-tick boundary check on the server and hand players off to the neighbouring zone's map server.")]
        public bool enableServerBoundaryHandoff = true;

        [Tooltip("Seconds between boundary checks.")]
        public float boundaryCheckInterval = 0.1f;

        [Tooltip("Metres a player must be past the zone edge before ownership flips (anti-thrash).")]
        public float handoffHysteresis = 1f;

        [Tooltip("Seconds of the player's current velocity to add to the saved position, so the neighbour spawns it where the client's dead-reckoned stand-in will be by then (~the measured gap).")]
        public float handoffLeadSeconds = 0.3f;

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

        [Tooltip("DEBUG (editor): save a screenshot every few frames during a seamless swap to <project>/Logs/MigrationFrames for visual inspection.")]
        public bool debugCaptureMigrationFrames = false;

        public override void OnClientConnected()
        {
            Stamp("client connected");
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
            return base.DeserializeClientReadyData(requestId, connectionId, reader, playerIdentity);
        }

        public override void RegisterPlayerCharacter(long connectionId, BasePlayerCharacterEntity playerCharacter)
        {
            Stamp($"player registered {playerCharacter.CharacterName} conn={connectionId}");
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
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _meshPeers.Clear();
            _handoffsInProgress.Clear();
            OwnZoneCoord = new Vector2Int(-1, -1);
            OwnZoneId = string.Empty;
            TryResolveOwnZone();
            // Test rig toggle: MESH_NO_MONSTER_HANDOFF=1 in the environment keeps monsters with their spawn cell even
            // when they chase across the line (forces cross-edge kills so loot ghosting can be exercised).
            if (System.Environment.GetEnvironmentVariable("MESH_NO_MONSTER_HANDOFF") == "1")
            {
                handoffMonsters = false;
                Logging.Log("Mesh", "MESH_NO_MONSTER_HANDOFF=1: monster handoff disabled on this cell");
            }
            GhostingOnStartServer();
        }

        public override void OnStopServer()
        {
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
            // Lead the saved position by the expected gap so the neighbour spawns the character where the
            // client's dead-reckoned stand-in will be, instead of ~gap*speed behind it.
            Vector3 velocity = _velocities.TryGetValue(characterId, out VelocitySample vs) ? vs.vel : Vector3.zero;
            velocity.y = 0f;
            Vector3 savePosition = position + velocity * Mathf.Max(0f, handoffLeadSeconds);
            _velocities.Remove(characterId);
            Logging.Log("Mesh", $"Handoff START {entity.CharacterName} ({characterId}) zone {OwnZoneId}->{zone.zoneId} pos={position} vel={velocity} save={savePosition} -> {peer.networkAddress}:{peer.networkPort}");

            try
            {
                // Mirrors MapNetworkManager.SaveAndWarpCharacterByPeerInfo (changeMap=true keeps the logical map name),
                // except IsWarping is NOT raised: it would show the kit's "warping" UI on the client, and the entity
                // is unregistered and destroyed within milliseconds anyway.
                // The destination keeps its ghost of this character until the real one registers there (no blink for
                // its observers): make sure neither the unregister nor the next snapshot tick sends it a GhostRemove.
                GhostingOnPlayerHandoffStart(characterId, zone.zoneId);
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
                    Logging.LogWarning("Mesh", $"Handoff ABORTED {entity?.CharacterName} -> zone {zone.zoneId}: saved={saved}; character stays on {OwnZoneId}");
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
                    SendHandoffNotice(zone.zoneId, characterId, userId);
                else
                    SendHandoffNotice(zone.zoneId, characterId, entity.UserId);

                MMOWarpMessage message = new MMOWarpMessage
                {
                    networkAddress = peer.networkAddress,
                    networkPort = peer.networkPort,
                };
                ServerSendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, GameNetworkingConsts.Warp, message);
                // After the warp packet (ordered): the entity becomes a server-owned puppet that survives the client's
                // disconnect; observers on this cell keep seeing it, now steered by the neighbour's snapshots.
                if (demotePlayerOnHandoff && entity != null)
                    DemotePlayerToGhost(entity, zone.zoneId);
                Logging.Log("Mesh", $"Handoff SENT {entity.CharacterName} -> zone {zone.zoneId} saved={saved} serverSide={sw.ElapsedMilliseconds}ms demoted={demotePlayerOnHandoff}");
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
    }
}
