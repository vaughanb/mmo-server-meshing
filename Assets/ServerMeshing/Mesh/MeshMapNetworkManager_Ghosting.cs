// Server Meshing add-on — border ghosting (Spike 1b / P2).
// See Docs/ServerMeshing/DESIGN.md §5, §7, §8, §10 and SPIKE1.md §0b.
//
// EXTENSION-ONLY (partial of the MapNetworkManager subclass). Server side only:
//  * every `ghostSnapshotInterval` each owned entity (registered player, or server-owned monster)
//    inside the border band B of a shared edge is streamed (full-precision position + movement state)
//    to that neighbour over the CellMeshLink;
//  * a received snapshot spawns/steers a GHOST: a normal entity spawned server-side, with its
//    server-driver components silenced (GhostSuppression) but its movement component kept, so it is
//    steered toward the snapshot with the kit's own movement API ("puppet"), animates, and reaches
//    this cell's clients through the ordinary AOI/sync path. Ghosts are invincible so the local
//    simulation cannot damage them (cross-border hits are resolved by the owner — P3);
//  * dedup/promotion (DESIGN §8): a snapshot for a character this cell owns for real is ignored, and
//    when the real character registers here (handoff-in) any ghost with the same id is destroyed;
//  * monster ownership: each cell only runs the spawn areas that sit inside its own zone, so every
//    server-spawned entity has exactly one owner; its MeshEntityId is the birth-provenance id
//    "{zone}:{incarnation}:{objectId}" (DESIGN §8).
// Ghosts are never registered as player characters, so the boundary tick and streaming ignore them.

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — border ghosting (server side)")]
        [Tooltip("Stream owned entities near a shared zone edge to the neighbour cell, and spawn ghosts for what the neighbour streams to us.")]
        public bool enableBorderGhosting = true;

        [Tooltip("Also ghost server-owned monsters (not only players).")]
        public bool ghostMonsters = true;

        [Tooltip("Only run spawn areas whose position lies inside this cell's zone (one owner per server-spawned entity).")]
        public bool ownSpawnAreasByZone = true;

        [Tooltip("Border band B (metres from the shared edge) inside which entities are ghosted into the neighbour.")]
        public float ghostBandWidth = 60f;

        [Tooltip("Seconds between ghost snapshots.")]
        public float ghostSnapshotInterval = 0.1f;

        [Tooltip("A ghost that received no snapshot for this long is destroyed.")]
        public float ghostStaleSeconds = 2f;

        [Tooltip("If a ghost is further than this from its snapshot it is teleported instead of steered.")]
        public float ghostTeleportDistance = 4f;

        [Tooltip("Mesh link listens on mapPort + this offset.")]
        public int meshLinkPortOffset = 10000;

        public int GhostCount => _ghosts.Count;
        public int NeighbourLinkCount => _linkClients.Count;

        private sealed class MeshGhost
        {
            public BaseCharacterEntity entity;
            public GhostKind kind;
            public float lastSeen;
            public Vector3 lastTarget;
            public ReceiveDamageDelegate hitHook; // so a promoted puppet can drop its hit forwarding
            public VehicleEntity vehicle;         // the mount this ghost rides (spawned here for a puppet, kept for a demoted player)
            public bool mountPending;             // an async mount/dismount is in flight
        }

        private MeshLinkServer _linkServer;
        private readonly Dictionary<string, MeshLinkClient> _linkClients = new Dictionary<string, MeshLinkClient>();   // zoneId -> link
        private readonly Dictionary<long, string> _neighbourZoneByConnection = new Dictionary<long, string>();
        private readonly Dictionary<string, MeshGhost> _ghosts = new Dictionary<string, MeshGhost>();                  // meshEntityId -> ghost
        private readonly Dictionary<string, HashSet<string>> _streamedToZone = new Dictionary<string, HashSet<string>>(); // zoneId -> ids streamed last tick
        private readonly List<string> _tmpIds = new List<string>();
        private readonly List<BaseCharacterEntity> _ownedCandidates = new List<BaseCharacterEntity>();
        private float _nextSnapshotTime;
        private float _nextLinkCheck;
        private float _nextMonsterScan;
        private string _cellIncarnation;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void GhostingOnStartServer()
        {
            _cellIncarnation = System.DateTime.UtcNow.Ticks.ToString("x");
            if (!enableBorderGhosting)
                return;
            _linkServer = new MeshLinkServer(this);
            int port = networkPort + meshLinkPortOffset;
            if (_linkServer.StartServer(port, 64))
                Logging.Log("Mesh", $"Mesh link listening on {port}");
            else
                Logging.LogError("Mesh", $"Mesh link failed to listen on {port}");
        }

        private void GhostingOnStopServer()
        {
            foreach (MeshLinkClient client in _linkClients.Values)
                client.StopClient();
            _linkClients.Clear();
            _linkServer?.StopServer();
            _linkServer = null;
            foreach (MeshGhost ghost in _ghosts.Values)
            {
                if (ghost.entity != null)
                    ghost.entity.NetworkDestroy();
            }
            _ghosts.Clear();
            _streamedToZone.Clear();
            _neighbourZoneByConnection.Clear();
            _ownedCandidates.Clear();
        }

        /// <summary>
        /// DESIGN §12 / risk #6: a map scene is shared by every cell of the logical map, so without this each
        /// cell would spawn every spawn area's monsters (double spawn). Deactivate areas outside our zone
        /// before the base class discovers them.
        /// </summary>
        public override void OnServerOnlineSceneLoaded()
        {
            if (ownSpawnAreasByZone && meshConfig != null && TryResolveOwnZone())
            {
                int kept = 0, disabled = 0;
                foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                foreach (HarvestableSpawnArea area in FindObjectsByType<HarvestableSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                foreach (ItemDropSpawnArea area in FindObjectsByType<ItemDropSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                Logging.Log("Mesh", $"Spawn areas owned by zone {OwnZoneId}: {kept} kept, {disabled} disabled (outside zone)");
            }
            base.OnServerOnlineSceneLoaded();
        }

        private void GhostingUpdate()
        {
            _linkServer?.Update();
            foreach (MeshLinkClient client in _linkClients.Values)
                client.Update();

            if (!enableBorderGhosting || meshConfig == null || !HasOwnZone)
                return;

            if (Time.unscaledTime >= _nextLinkCheck)
            {
                _nextLinkCheck = Time.unscaledTime + 1f;
                EnsureNeighbourLinks();
            }
            if (Time.unscaledTime >= _nextMonsterScan)
            {
                _nextMonsterScan = Time.unscaledTime + 1f;
                RescanOwnedCandidates();
            }
            if (Time.unscaledTime >= _nextSnapshotTime)
            {
                _nextSnapshotTime = Time.unscaledTime + Mathf.Max(0.02f, ghostSnapshotInterval);
                MonsterHandoffTick();
                SendBandSnapshots();
                ExpireGhosts();
                ExpireReservedArrivals();
            }
        }

        /// <summary>Open (or re-open) one link per 4-neighbour zone whose map server the cluster has told us about.</summary>
        private void EnsureNeighbourLinks()
        {
            string mapName = CurrentMapInfo != null ? CurrentMapInfo.Id : string.Empty;
            for (int i = 0; i < 4; ++i)
            {
                Vector2Int coord = NeighbourCoord(i);
                ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZone(coord);
                if (zone == null)
                    continue;
                if (!_meshPeers.TryGetValue(PeerInfoExtensions.GetPeerInfoKey(zone.zoneId, mapName), out CentralServerPeerInfo peer))
                    continue;
                if (!_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient client))
                {
                    client = new MeshLinkClient(this, zone.zoneId);
                    _linkClients[zone.zoneId] = client;
                }
                if (!client.IsConnected && Time.unscaledTime - client.LastConnectAttempt > 5f)
                    client.Connect(peer.networkAddress, peer.networkPort + meshLinkPortOffset);
            }
        }

        private static readonly Vector2Int[] s_neighbourOffsets = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };
        private Vector2Int NeighbourCoord(int i) => OwnZoneCoord + s_neighbourOffsets[i];

        /// <summary>Distance from a world position to the edge shared with the neighbour at offset index i.</summary>
        private float DistanceToSharedEdge(Vector3 pos, int i)
        {
            Vector2Int off = s_neighbourOffsets[i];
            if (off.x != 0)
            {
                float lineX = meshConfig.gridOrigin.x + Mathf.Max(OwnZoneCoord.x, OwnZoneCoord.x + off.x) * meshConfig.zoneSize;
                return Mathf.Abs(pos.x - lineX);
            }
            float lineZ = meshConfig.gridOrigin.z + Mathf.Max(OwnZoneCoord.y, OwnZoneCoord.y + off.y) * meshConfig.zoneSize;
            return Mathf.Abs(pos.z - lineZ);
        }

        /// <summary>Owned = registered players + server-owned monsters that are not ghosts. Monsters are rescanned once a second.</summary>
        private readonly Dictionary<string, BaseCharacterEntity> _ownedByMeshId = new Dictionary<string, BaseCharacterEntity>();

        private void RescanOwnedCandidates()
        {
            _ownedCandidates.Clear();
            _ownedByMeshId.Clear();
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                if (data is BasePlayerCharacterEntity entity && !entity.IsWarping)
                {
                    _ownedCandidates.Add(entity);
                    _ownedByMeshId[entity.Id] = entity;
                }
            }
            if (!ghostMonsters)
                return;
            foreach (BaseMonsterCharacterEntity monster in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
            {
                if (monster == null || !monster.IsServer || !monster.IsOwnedByServer || monster.IsDead() || MeshIdentity.IsGhostEntity(monster))
                    continue;
                MeshIdentity id = MeshIdentity.GetOrAdd(monster);
                if (string.IsNullOrEmpty(id.MeshEntityId))
                {
                    id.MeshEntityId = $"{OwnZoneId}:{_cellIncarnation}:{monster.ObjectId}"; // birth provenance, never the current owner
                    id.OwnerZoneId = OwnZoneId;
                }
                _ownedCandidates.Add(monster);
                _ownedByMeshId[id.MeshEntityId] = monster;
            }
        }

        private string MeshEntityIdOf(BaseCharacterEntity entity)
        {
            if (entity is BasePlayerCharacterEntity)
                return entity.Id;
            MeshIdentity id = MeshIdentity.Get(entity);
            return id != null ? id.MeshEntityId : entity.Id;
        }

        private void SendBandSnapshots()
        {
            for (int i = 0; i < 4; ++i)
            {
                ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZone(NeighbourCoord(i));
                if (zone == null || !_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient client) || !client.IsConnected)
                    continue;
                if (!_streamedToZone.TryGetValue(zone.zoneId, out HashSet<string> streamed))
                {
                    streamed = new HashSet<string>();
                    _streamedToZone[zone.zoneId] = streamed;
                }
                _tmpIds.Clear();
                _tmpIds.AddRange(streamed);
                streamed.Clear();

                for (int c = 0; c < _ownedCandidates.Count; ++c)
                {
                    BaseCharacterEntity entity = _ownedCandidates[c];
                    if (entity == null || entity.IsDead())
                        continue;
                    if (entity is BasePlayerCharacterEntity player && player.IsWarping)
                        continue;
                    Vector3 pos = entity.EntityTransform.position;
                    if (DistanceToSharedEdge(pos, i) > ghostBandWidth)
                        continue;
                    string meshId = MeshEntityIdOf(entity);
                    VehicleEntity mount = entity.PassengingVehicleEntity as VehicleEntity;
                    client.SendGhostSnapshot(new GhostSnapshotMessage
                    {
                        mountEntityId = mount != null ? mount.EntityId : 0,
                        mountLevel = mount != null ? mount.Level : 0,
                        kind = entity is BasePlayerCharacterEntity ? GhostKind.Player : GhostKind.Monster,
                        characterId = meshId,
                        entityId = entity.EntityId,
                        dataId = entity.DataId,
                        characterName = entity.CharacterName,
                        level = entity.Level,
                        position = pos,
                        yaw = entity.EntityTransform.eulerAngles.y,
                        movementState = (byte)entity.MovementState,
                        extraMovementState = (byte)entity.ExtraMovementState,
                        currentHp = entity.CurrentHp,
                        maxHp = entity.MaxHp,
                    });
                    streamed.Add(meshId);
                }

                // Anything streamed last tick but not now has left the band (or this cell, or died): tell the neighbour.
                for (int k = 0; k < _tmpIds.Count; ++k)
                {
                    if (!streamed.Contains(_tmpIds[k]))
                        client.SendGhostRemove(_tmpIds[k]);
                }
            }
        }

        private void SendGhostRemoveToAll(string meshId)
        {
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                if (_streamedToZone.TryGetValue(kv.Key, out HashSet<string> streamed) && streamed.Remove(meshId))
                    kv.Value.SendGhostRemove(meshId);
            }
        }

        private void ExpireGhosts()
        {
            _tmpIds.Clear();
            foreach (KeyValuePair<string, MeshGhost> kv in _ghosts)
            {
                if (kv.Value.entity == null || Time.unscaledTime - kv.Value.lastSeen > ghostStaleSeconds)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
                DestroyGhost(_tmpIds[i], "stale");
        }

        private void DestroyGhost(string meshId, string reason)
        {
            if (!_ghosts.TryGetValue(meshId, out MeshGhost ghost))
                return;
            _ghosts.Remove(meshId);
            if (ghost.entity != null)
            {
                Logging.Log("Mesh", $"Ghost destroyed {ghost.kind} {ghost.entity.CharacterName} ({meshId}) reason={reason}");
                VehicleEntity mount = ghost.entity.PassengingVehicleEntity as VehicleEntity;
                if (mount == null) mount = ghost.vehicle;
                ghost.entity.NetworkDestroy();
                if (mount != null && mount.Identity != null && !mount.Identity.IsDestroyed)
                    mount.NetworkDestroy();
            }
        }

        /// <summary>Called when the real character registers on this cell (handoff-in): the ghost must go.</summary>
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter, long connectionId)
        {
            if (playerCharacter == null)
                return;
            if (_reservedArrivals.TryGetValue(playerCharacter.Id, out ReservedArrival reserved) && playerCharacter.Identity != null)
            {
                _reservedArrivals.Remove(playerCharacter.Id);
                // A fresh spawn is pushed to its owner at once. A re-owned entity would wait for the AOI's next pass
                // (1 s): the kit's SetOwnerClient is deferred by the AOI (manuallyApplyOwnerChanges) and the owner's
                // subscription only comes with that pass. Apply the owner now and subscribe the owner now.
                Assets.SetObjectOwnerImmediately(playerCharacter.ObjectId, connectionId);
                if (InterestManager != null)
                    InterestManager.NotifyNewObject(playerCharacter.Identity);
                // The mount: the kept vehicle is re-owned by the driver the same way; otherwise re-summon from the
                // saved data (what the kit's fresh-spawn path does). Summons likewise.
                if (reserved.keepMount && reserved.mount != null && reserved.mount.Identity != null && !reserved.mount.Identity.IsDestroyed)
                {
                    Assets.SetObjectOwnerImmediately(reserved.mount.ObjectId, connectionId);
                    if (InterestManager != null)
                        InterestManager.NotifyNewObject(reserved.mount.Identity);
                }
                else if (!playerCharacter.IsDead() && playerCharacter.Mount.type != MountType.None)
                {
                    playerCharacter.SpawnMount(playerCharacter.Mount.type, playerCharacter.Mount.sourceId, playerCharacter.Mount.mountRemainsDuration, playerCharacter.Mount.level, playerCharacter.Mount.currentHp);
                }
                if (!playerCharacter.IsDead())
                {
                    for (int i = 0; i < playerCharacter.Summons.Count; ++i)
                    {
                        CharacterSummon summon = playerCharacter.Summons[i];
                        summon.Summon(playerCharacter, summon.level, summon.summonRemainsDuration, summon.exp, summon.currentHp, summon.currentMp);
                        playerCharacter.Summons[i] = summon;
                    }
                }
            }
            DestroyGhost(playerCharacter.Id, "promoted to real");
        }

        // ---- Arrival by handoff: promote the ghost in place (DESIGN §7) ----
        // The kit's SetPlayerReadyRoutine has an "already spawned entity" branch (relates data, SetOwnerClient,
        // Register, respawn RPC). Handing it the ghost entity means the arriving character IS the entity observers
        // here were already looking at: no swap, no pop.

        private sealed class ReservedArrival { public BasePlayerCharacterEntity entity; public float time; public VehicleEntity mount; public bool keepMount; }
        private readonly Dictionary<string, ReservedArrival> _reservedArrivals = new Dictionary<string, ReservedArrival>();
        [Tooltip("If an arriving client never becomes ready, its promoted ghost is destroyed after this many seconds.")]
        public float reservedArrivalTimeout = 15f;

        private bool TryPromoteGhostForArrival(long connectionId, PlayerCharacterData data, out BasePlayerCharacterEntity entity)
        {
            entity = null;
            if (data == null || !_ghosts.TryGetValue(data.Id, out MeshGhost ghost) || ghost.entity == null)
                return false;
            BasePlayerCharacterEntity player = ghost.entity as BasePlayerCharacterEntity;
            if (player == null || player.Identity == null || player.Identity.IsDestroyed || player.EntityId != data.EntityId)
                return false;
            _ghosts.Remove(data.Id);
            if (ghost.hitHook != null)
                player.onReceiveDamage -= ghost.hitHook;
            GhostSuppression.Unghostify(player, this);
            player.IsInvincible = false;
            MeshIdentity id = MeshIdentity.GetOrAdd(player);
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            // The mount: keep the vehicle the ghost is riding when it is the one the saved data says (the usual case),
            // otherwise drop it and let the registration re-summon from the saved data like a fresh spawn would.
            VehicleEntity ridden = player.PassengingVehicleEntity as VehicleEntity;
            bool keepMount = false;
            if (ridden != null)
            {
                VehicleEntity wanted = null;
                if (data.Mount.type != MountType.None)
                    data.Mount.type.GetPrefab(data, data.Mount.sourceId, out wanted);
                keepMount = wanted != null && wanted.EntityId == ridden.EntityId;
                if (!keepMount)
                {
                    player.ExitVehicleAndForget();
                    if (ridden.Identity != null && !ridden.Identity.IsDestroyed)
                        ridden.NetworkDestroy();
                    ridden = null;
                }
            }
            // Same setup the kit does for a fresh spawn, on the existing object.
            GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId);
            GameInstance.SetupByMetaData(player, metaDataId);
            data.CloneTo(player);
            player.Teleport(data.CurrentPosition, Quaternion.Euler(data.CurrentRotation), false);
            player.ForceMakeCaches();
            player.FillEmptySlots();
            player.Identity.DoNotDestroyWhenDisconnect = true;
            _reservedArrivals[data.Id] = new ReservedArrival { entity = player, time = Time.unscaledTime, mount = ridden, keepMount = keepMount };
            Logging.Log("Mesh", $"Arrival {data.CharacterName} ({data.Id}) conn={connectionId}: ghost promoted in place at {player.EntityTransform.position} mount={(keepMount ? "kept" : data.Mount.type != MountType.None ? "re-summon" : "none")}");
            entity = player;
            return true;
        }

        private void ExpireReservedArrivals()
        {
            if (_reservedArrivals.Count == 0)
                return;
            _tmpIds.Clear();
            foreach (KeyValuePair<string, ReservedArrival> kv in _reservedArrivals)
            {
                if (kv.Value.entity == null || Time.unscaledTime - kv.Value.time > reservedArrivalTimeout)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                ReservedArrival r = _reservedArrivals[_tmpIds[i]];
                _reservedArrivals.Remove(_tmpIds[i]);
                if (r.entity != null && !ServerUserHandlers.TryGetPlayerCharacterById(_tmpIds[i], out _))
                {
                    Logging.LogWarning("Mesh", $"Promoted ghost {_tmpIds[i]} was never claimed by its client; destroying it");
                    r.entity.NetworkDestroy();
                }
            }
        }

        private void GhostingOnRealPlayerUnregistered(long connectionId)
        {
            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData data) && data != null)
                SendGhostRemoveToAll(data.Id);
        }

        /// <summary>
        /// Player handoff to <paramref name="toZone"/> is starting: forget that we streamed this character there so no
        /// GhostRemove follows (the destination promotes/replaces its ghost when the real character registers).
        /// </summary>
        private void GhostingOnPlayerHandoffStart(string characterId, string toZone)
        {
            if (_streamedToZone.TryGetValue(toZone, out HashSet<string> streamed))
                streamed.Remove(characterId);
        }

        /// <summary>
        /// The departing player's entity stays as a ghost puppet: server-owned (so the client's disconnect does not
        /// destroy it), silenced, invincible, steered by the new owner's snapshots and removed when they stop.
        /// </summary>
        private void DemotePlayerToGhost(BasePlayerCharacterEntity entity, string newOwnerZone)
        {
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                return;
            string meshId = entity.Id;
            entity.SetTargetEntity(null);
            entity.StopMove();
            Assets.SetObjectOwnerImmediately(entity.ObjectId, -1);
            VehicleEntity riddenMount = entity.PassengingVehicleEntity as VehicleEntity;
            if (riddenMount != null && riddenMount.Identity != null)
                Assets.SetObjectOwnerImmediately(riddenMount.ObjectId, -1); // the mount survives the disconnect too
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = meshId;
            id.IsGhost = true;
            id.OwnerZoneId = newOwnerZone;
            id.LastHandoffTime = Time.unscaledTime;
            int silenced = GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
            entity.IsInvincible = true;
            ReceiveDamageDelegate hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                ForwardGhostHit(meshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
            entity.onReceiveDamage += hitHook;
            if (_ghosts.TryGetValue(meshId, out MeshGhost stale) && stale.entity != null && stale.entity != entity)
                stale.entity.NetworkDestroy(); // should not happen (a ghost and the real one on the same cell), be safe
            _ghosts[meshId] = new MeshGhost { entity = entity, kind = GhostKind.Player, lastSeen = Time.unscaledTime, lastTarget = entity.EntityTransform.position, hitHook = hitHook, vehicle = riddenMount };
            foreach (HashSet<string> streamed in _streamedToZone.Values)
                streamed.Remove(meshId);
            Logging.Log("Mesh", $"Player {entity.CharacterName} demoted to ghost (owner {newOwnerZone}, silenced {silenced}, mounted={riddenMount != null})");
        }

        internal void OnMeshNeighbourHello(long connectionId, string zoneId)
        {
            _neighbourZoneByConnection[connectionId] = zoneId;
            Logging.Log("Mesh", $"Neighbour zone {zoneId} linked (conn {connectionId})");
        }

        internal void OnMeshNeighbourDisconnected(long connectionId)
        {
            _neighbourZoneByConnection.Remove(connectionId);
        }

        internal void OnGhostSnapshot(long connectionId, GhostSnapshotMessage msg)
        {
            if (!IsServer || !enableBorderGhosting)
                return;
            // Dedup: this cell owns the real thing → the neighbour's copy is stale; ignore.
            if (msg.kind == GhostKind.Player && ServerUserHandlers.TryGetPlayerCharacterById(msg.characterId, out _))
                return;
            // A ghost already promoted for an arriving client: the departing cell's last snapshots are in flight; ignore.
            if (_reservedArrivals.ContainsKey(msg.characterId))
                return;

            if (!_ghosts.TryGetValue(msg.characterId, out MeshGhost ghost) || ghost.entity == null)
            {
                BaseCharacterEntity entity = SpawnGhostEntity(msg);
                if (entity == null)
                    return;
                MeshIdentity id = MeshIdentity.GetOrAdd(entity);
                id.MeshEntityId = msg.characterId;
                id.IsGhost = true;
                id.OwnerZoneId = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
                int silenced = GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
                entity.IsInvincible = true; // never damaged by the local simulation: hits are forwarded to the owner (P3)
                string capturedMeshId = msg.characterId;
                ReceiveDamageDelegate hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                    ForwardGhostHit(capturedMeshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
                entity.onReceiveDamage += hitHook;
                // Kill credit: rewards the kit gives this ghost (it was in a monster's damage records here) are intercepted
                // by MeshGameplayRule and forwarded to the owner — see ForwardGhostReward.
                ghost = new MeshGhost { entity = entity, kind = msg.kind, lastTarget = msg.position, hitHook = hitHook };
                _ghosts[msg.characterId] = ghost;
                Logging.Log("Mesh", $"Ghost spawned {msg.kind} {msg.characterName} ({msg.characterId}) from zone {id.OwnerZoneId} at {msg.position} (silenced {silenced})");
            }

            ghost.lastSeen = Time.unscaledTime;
            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string ownerZone))
            {
                MeshIdentity gid = MeshIdentity.Get(ghost.entity);
                if (gid != null && gid.OwnerZoneId != ownerZone)
                    gid.OwnerZoneId = ownerZone; // ownership moved (monster handoff between neighbours)
            }
            SyncGhostMount(ghost, msg);
            SteerGhost(ghost, msg);
        }

        // ---- Mounts on ghosts ----
        // A rider's snapshot names the vehicle prefab; the ghost cell spawns that vehicle (server-owned) and seats
        // the ghost on it, so observers across the edge see the mount. Steering goes through the rider: the kit's
        // ActiveMovement is the vehicle's movement while seated, so PointClickMovement/Teleport drive the mount.

        private void SyncGhostMount(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            if (ghost.entity == null || ghost.mountPending)
                return;
            VehicleEntity current = ghost.entity.PassengingVehicleEntity as VehicleEntity;
            if (msg.mountEntityId != 0)
            {
                if (current != null && current.EntityId == msg.mountEntityId)
                    return;
                if (!GameInstance.VehicleEntities.TryGetValue(msg.mountEntityId, out VehicleEntity prefab) || prefab == null)
                {
                    WarnThrottled($"Ghost {msg.characterName} rides unknown vehicle entity id {msg.mountEntityId}; shown on foot");
                    return;
                }
                MountGhost(ghost, prefab, msg.mountLevel, msg.position, msg.yaw).Forget();
            }
            else if (current != null)
            {
                DismountGhost(ghost).Forget();
            }
        }

        private async UniTaskVoid MountGhost(MeshGhost ghost, VehicleEntity prefab, int level, Vector3 position, float yaw)
        {
            ghost.mountPending = true;
            try
            {
                BaseCharacterEntity entity = ghost.entity;
                if (entity == null)
                    return;
                if (entity.PassengingVehicleEntity != null)
                {
                    VehicleEntity old = entity.PassengingVehicleEntity as VehicleEntity;
                    await entity.ExitVehicle();
                    if (old != null && old.Identity != null && !old.Identity.IsDestroyed)
                        old.NetworkDestroy();
                }
                LiteNetLibIdentity identity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, position, Quaternion.Euler(0f, yaw, 0f));
                VehicleEntity vehicle = identity != null ? identity.GetComponent<VehicleEntity>() : null;
                if (vehicle == null)
                {
                    if (identity != null) identity.NetworkDestroy();
                    return;
                }
                vehicle.Level = Mathf.Max(1, level);
                vehicle.InitStats();
                vehicle.CurrentHp = vehicle.MaxHp;
                if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                {
                    vehicle.NetworkDestroy();
                    return;
                }
                await entity.EnterVehicle(vehicle, 0);
                ghost.vehicle = vehicle;
                if (traceHandoffTiming)
                    Logging.Log("Mesh", $"Ghost {entity.CharacterName} mounted on {prefab.name}");
            }
            finally
            {
                ghost.mountPending = false;
            }
        }

        private async UniTaskVoid DismountGhost(MeshGhost ghost)
        {
            ghost.mountPending = true;
            try
            {
                BaseCharacterEntity entity = ghost.entity;
                VehicleEntity vehicle = entity != null ? entity.PassengingVehicleEntity as VehicleEntity : null;
                if (entity != null)
                    await entity.ExitVehicle();
                if (vehicle != null && vehicle.Identity != null && !vehicle.Identity.IsDestroyed)
                    vehicle.NetworkDestroy();
                ghost.vehicle = null;
                if (traceHandoffTiming && entity != null)
                    Logging.Log("Mesh", $"Ghost {entity.CharacterName} dismounted");
            }
            finally
            {
                ghost.mountPending = false;
            }
        }

        private BaseCharacterEntity SpawnGhostEntity(GhostSnapshotMessage msg)
        {
            Quaternion rot = Quaternion.Euler(0f, msg.yaw, 0f);
            if (msg.kind == GhostKind.Player)
            {
                int hashAssetId = GameInstance.GetPlayerCharacterEntityHashAssetId(msg.entityId, out int metaDataId);
                LiteNetLibIdentity identity = Assets.NetworkSpawn(hashAssetId, msg.position, rot);
                BasePlayerCharacterEntity player = identity != null ? identity.GetComponent<BasePlayerCharacterEntity>() : null;
                if (player == null)
                {
                    WarnThrottled($"Cannot spawn player ghost for entity id {msg.entityId}");
                    if (identity != null) identity.NetworkDestroy();
                    return null;
                }
                GameInstance.SetupByMetaData(player, metaDataId);
                player.Id = msg.characterId;
                player.DataId = msg.dataId;
                player.CharacterName = msg.characterName;
                player.Level = msg.level;
                return player;
            }
            if (!GameInstance.MonsterCharacterEntities.TryGetValue(msg.entityId, out BaseMonsterCharacterEntity prefab) || prefab == null)
            {
                WarnThrottled($"Cannot spawn monster ghost: unknown monster entity id {msg.entityId}");
                return null;
            }
            LiteNetLibIdentity monsterIdentity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, msg.position, rot);
            BaseMonsterCharacterEntity monster = monsterIdentity != null ? monsterIdentity.GetComponent<BaseMonsterCharacterEntity>() : null;
            if (monster == null)
            {
                WarnThrottled($"Cannot spawn monster ghost for entity id {msg.entityId}");
                if (monsterIdentity != null) monsterIdentity.NetworkDestroy();
                return null;
            }
            monster.Level = msg.level;
            return monster;
        }

        private void SteerGhost(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            BaseCharacterEntity entity = ghost.entity;
            Vector3 current = entity.EntityTransform.position;
            float dist = Vector3.Distance(current, msg.position);
            if (dist > ghostTeleportDistance)
            {
                entity.Teleport(msg.position, Quaternion.Euler(0f, msg.yaw, 0f), false);
            }
            else if (dist > 0.25f)
            {
                // Puppet: let the kit's movement component walk it there (animates + syncs like any entity).
                entity.PointClickMovement(msg.position);
            }
            else
            {
                entity.StopMove();
            }
            ghost.lastTarget = msg.position;
            if (entity.CurrentHp != msg.currentHp && msg.maxHp > 0)
                entity.CurrentHp = msg.currentHp;
        }

        internal void OnGhostRemove(string meshId)
        {
            DestroyGhost(meshId, "neighbour removed");
        }

        // ---- P3/P4: monster handoff (DESIGN §12) ----
        // A server-owned monster that walks clearly into the neighbour's cell (chasing a player's ghost across the
        // edge) is handed over: this cell demotes it to a ghost IN PLACE and the neighbour promotes its puppet to the
        // real monster, so neither cell's clients see a despawn/spawn. Respawn accounting stays with the home cell's
        // spawn area: when the borrowed monster dies elsewhere, the borrower tells the home cell (MonsterGone).

        [Tooltip("Hand a server-owned monster to the neighbour cell once it walks clearly into that cell.")]
        public bool handoffMonsters = true;
        [Tooltip("Minimum seconds between two handoffs of the same monster (a chase back and forth across the line would otherwise ping-pong every tick).")]
        public float monsterHandoffCooldown = 2f;

        private sealed class LentMonster
        {
            public GameSpawnArea<BaseMonsterCharacterEntity> area;
            public BaseMonsterCharacterEntity prefab;
            public int level;
            public float respawnDelay;
            public Vector3 spawnPosition;
        }

        private readonly Dictionary<string, LentMonster> _lentMonsters = new Dictionary<string, LentMonster>();                 // meshId -> home spawn info (this cell is home)
        private readonly Dictionary<string, CharacterEntityKilledDelegate> _borrowedKillHooks = new Dictionary<string, CharacterEntityKilledDelegate>();
        private readonly List<BaseMonsterCharacterEntity> _monsterHandoffCandidates = new List<BaseMonsterCharacterEntity>();

        private void MonsterHandoffTick()
        {
            if (!handoffMonsters || !enableServerBoundaryHandoff || !ghostMonsters)
                return;
            _monsterHandoffCandidates.Clear();
            for (int i = 0; i < _ownedCandidates.Count; ++i)
            {
                BaseMonsterCharacterEntity monster = _ownedCandidates[i] as BaseMonsterCharacterEntity;
                if (monster == null || monster.IsDead() || monster.IsSummoned)
                    continue;
                Vector3 pos = monster.EntityTransform.position;
                if (meshConfig.WorldToZoneCoord(pos) == OwnZoneCoord)
                    continue;
                if (meshConfig.IsNearBoundary(pos, handoffHysteresis))
                    continue; // same hysteresis as players: only once clearly past the line
                _monsterHandoffCandidates.Add(monster);
            }
            for (int i = 0; i < _monsterHandoffCandidates.Count; ++i)
                HandoffMonster(_monsterHandoffCandidates[i]);
            _monsterHandoffCandidates.Clear();
        }

        private void HandoffMonster(BaseMonsterCharacterEntity monster)
        {
            Vector3 pos = monster.EntityTransform.position;
            ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZoneAt(pos);
            if (zone == null || string.Equals(zone.zoneId, OwnZoneId))
                return;
            if (!_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient link) || !link.IsConnected)
                return; // stays ours until the link is up
            MeshIdentity id = MeshIdentity.GetOrAdd(monster);
            if (string.IsNullOrEmpty(id.MeshEntityId))
                return;
            if (Time.unscaledTime - id.LastHandoffTime < monsterHandoffCooldown)
                return;
            string meshId = id.MeshEntityId;
            string homeZone = string.IsNullOrEmpty(id.HomeZoneId) ? OwnZoneId : id.HomeZoneId;
            if (string.Equals(homeZone, OwnZoneId) && monster.SpawnArea != null && !_lentMonsters.ContainsKey(meshId))
            {
                _lentMonsters[meshId] = new LentMonster
                {
                    area = monster.SpawnArea, prefab = monster.SpawnPrefab, level = monster.SpawnLevel,
                    respawnDelay = monster.DestroyRespawnDelay, spawnPosition = monster.SpawnPosition,
                };
            }
            string targetMeshId = string.Empty;
            if (monster.TryGetTargetEntity(out BaseCharacterEntity target) && target != null)
                targetMeshId = MeshEntityIdOf(target);
            link.SendMonsterHandoff(new MonsterHandoffMessage
            {
                meshId = meshId, entityId = monster.EntityId, dataId = monster.DataId, level = monster.Level, currentHp = monster.CurrentHp,
                position = pos, yaw = monster.EntityTransform.eulerAngles.y, spawnPosition = monster.SpawnPosition,
                homeZoneId = homeZone, targetMeshId = targetMeshId,
            });
            Logging.Log("Mesh", $"Monster handoff {meshId} {OwnZoneId}->{zone.zoneId} at {pos} hp={monster.CurrentHp}/{monster.MaxHp} home={homeZone} target={targetMeshId}");
            DemoteToGhost(monster, id, zone.zoneId);
        }

        /// <summary>The neighbour owns it now: keep the entity (no pop for our clients) but run it as a puppet.</summary>
        private void DemoteToGhost(BaseMonsterCharacterEntity monster, MeshIdentity id, string newOwnerZone)
        {
            string meshId = id.MeshEntityId;
            monster.SetTargetEntity(null);
            monster.StopMove();
            id.IsGhost = true;
            id.OwnerZoneId = newOwnerZone;
            id.LastHandoffTime = Time.unscaledTime;
            if (_borrowedKillHooks.TryGetValue(meshId, out CharacterEntityKilledDelegate killHook))
            {
                monster.onKilled -= killHook;
                _borrowedKillHooks.Remove(meshId);
            }
            GhostSuppression.Ghostify(monster, this, null, false, false, disableMovement: false);
            monster.IsInvincible = true;
            ReceiveDamageDelegate hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                ForwardGhostHit(meshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
            monster.onReceiveDamage += hitHook;
            _ghosts[meshId] = new MeshGhost { entity = monster, kind = GhostKind.Monster, lastSeen = Time.unscaledTime, lastTarget = monster.EntityTransform.position, hitHook = hitHook };
            _ownedByMeshId.Remove(meshId);
            _ownedCandidates.Remove(monster);
            // We just handed it over: do not follow up with a GhostRemove for something we no longer stream.
            foreach (HashSet<string> streamed in _streamedToZone.Values)
                streamed.Remove(meshId);
        }

        internal void OnMonsterHandoff(long connectionId, MonsterHandoffMessage msg)
        {
            if (!IsServer)
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            Quaternion rot = Quaternion.Euler(0f, msg.yaw, 0f);
            BaseMonsterCharacterEntity monster = null;
            bool promoted = false;
            if (_ghosts.TryGetValue(msg.meshId, out MeshGhost ghost) && ghost.entity != null && !ghost.entity.IsDead())
            {
                monster = ghost.entity as BaseMonsterCharacterEntity;
                if (monster != null)
                {
                    // Promote the puppet in place: our clients already see this entity.
                    _ghosts.Remove(msg.meshId);
                    if (ghost.hitHook != null)
                        monster.onReceiveDamage -= ghost.hitHook;
                    GhostSuppression.Unghostify(monster, this);
                    monster.IsInvincible = false;
                    if (Vector3.Distance(monster.EntityTransform.position, msg.position) > ghostTeleportDistance)
                        monster.Teleport(msg.position, rot, false);
                    promoted = true;
                }
                else
                {
                    DestroyGhost(msg.meshId, "replaced by handoff");
                }
            }
            GameInstance.MonsterCharacterEntities.TryGetValue(msg.entityId, out BaseMonsterCharacterEntity prefab);
            if (monster == null)
            {
                if (prefab == null)
                {
                    WarnThrottled($"Monster handoff {msg.meshId} from {fromZone}: unknown monster entity id {msg.entityId}; dropped");
                    return;
                }
                LiteNetLibIdentity identity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, msg.position, rot);
                monster = identity != null ? identity.GetComponent<BaseMonsterCharacterEntity>() : null;
                if (monster == null)
                {
                    WarnThrottled($"Monster handoff {msg.meshId} from {fromZone}: cannot spawn entity id {msg.entityId}; dropped");
                    if (identity != null) identity.NetworkDestroy();
                    return;
                }
            }
            MeshIdentity id = MeshIdentity.GetOrAdd(monster);
            id.MeshEntityId = msg.meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.HomeZoneId = msg.homeZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            monster.Level = msg.level;
            if (msg.currentHp > 0)
                monster.CurrentHp = msg.currentHp;
            if (_borrowedKillHooks.TryGetValue(msg.meshId, out CharacterEntityKilledDelegate oldHook))
            {
                monster.onKilled -= oldHook;
                _borrowedKillHooks.Remove(msg.meshId);
            }
            if (string.Equals(msg.homeZoneId, OwnZoneId) && _lentMonsters.TryGetValue(msg.meshId, out LentMonster lent))
            {
                // Home again: the spawn area's respawn accounting comes back with it.
                _lentMonsters.Remove(msg.meshId);
                monster.SetSpawnArea(lent.area, lent.prefab, lent.level, lent.spawnPosition);
                monster.DestroyRespawnDelay = lent.respawnDelay;
            }
            else
            {
                // Borrowed: no local respawn; when it dies here the home cell is told so its spawn area respawns it.
                monster.SetSpawnArea(null, prefab, msg.level, msg.spawnPosition);
                string capturedMeshId = msg.meshId, home = msg.homeZoneId;
                CharacterEntityKilledDelegate killHook = (target, instigator) => OnBorrowedMonsterKilled(capturedMeshId, home);
                monster.onKilled += killHook;
                _borrowedKillHooks[msg.meshId] = killHook;
            }
            _ownedByMeshId[msg.meshId] = monster;
            if (!_ownedCandidates.Contains(monster))
                _ownedCandidates.Add(monster);
            // Keep the chase going: the target is either the real player here or its ghost.
            string targetDesc = "none";
            if (!string.IsNullOrEmpty(msg.targetMeshId))
            {
                BaseCharacterEntity target = null;
                if (ServerUserHandlers.TryGetPlayerCharacterById(msg.targetMeshId, out IPlayerCharacterData data) && data is BasePlayerCharacterEntity real)
                    target = real;
                else if (_ghosts.TryGetValue(msg.targetMeshId, out MeshGhost tg) && tg.entity != null)
                    target = tg.entity;
                if (target != null)
                {
                    monster.SetAttackTarget(target);
                    targetDesc = target.CharacterName;
                }
            }
            Logging.Log("Mesh", $"Monster {(promoted ? "promoted" : "spawned")} by handoff {msg.meshId} {fromZone}->{OwnZoneId} at {monster.EntityTransform.position} hp={monster.CurrentHp}/{monster.MaxHp} home={msg.homeZoneId} target={targetDesc}");
        }

        private void OnBorrowedMonsterKilled(string meshId, string homeZone)
        {
            _borrowedKillHooks.Remove(meshId);
            _ownedByMeshId.Remove(meshId);
            if (!_linkClients.TryGetValue(homeZone, out MeshLinkClient link) || !link.IsConnected)
            {
                Logging.LogWarning("Mesh", $"Borrowed monster {meshId} died but home zone {homeZone} is not linked: its respawn is lost");
                return;
            }
            link.SendMonsterGone(meshId);
            Logging.Log("Mesh", $"Borrowed monster {meshId} died here; told home zone {homeZone}");
        }

        internal void OnMonsterGone(string meshId)
        {
            if (!IsServer)
                return;
            DestroyGhost(meshId, "died on borrower");
            if (!_lentMonsters.TryGetValue(meshId, out LentMonster lent))
                return;
            _lentMonsters.Remove(meshId);
            if (lent.area == null || lent.prefab == null)
                return;
            lent.area.Spawn(lent.prefab, lent.level, lent.respawnDelay, lent.respawnDelay);
            Logging.Log("Mesh", $"Lent monster {meshId} died on the borrower; home spawn area respawns it in {lent.respawnDelay:0.#}s");
        }

        // ---- P3: cross-border hits (DESIGN §12) ----
        // A hit on a ghost is forwarded (raw attacker damage) to the ghost's owner, which resolves it against the
        // real entity with the kit's own DamageableEntity.ApplyDamage (internal, reachable from Assembly-CSharp)
        // and reports the outcome back so the ghost's cell can show real combat text and hp.

        [Tooltip("Forward hits landing on ghosts to the owning cell and apply them to the real entity there.")]
        public bool forwardGhostHits = true;

        private void ForwardGhostHit(string targetMeshId, HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, Dictionary<DamageElement, MinMaxFloat> damageAmounts, CharacterItem weapon, BaseSkill skill, int skillLevel)
        {
            if (!forwardGhostHits || !_ghosts.TryGetValue(targetMeshId, out MeshGhost ghost) || ghost.entity == null)
                return;
            MeshIdentity id = MeshIdentity.Get(ghost.entity);
            if (id == null || string.IsNullOrEmpty(id.OwnerZoneId) || !_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link))
                return;
            string instigatorMeshId = string.Empty;
            if (instigator != null)
            {
                Component instigatorComponent = instigator.Entity as Component;
                MeshIdentity instigatorId = instigatorComponent != null ? MeshIdentity.Get(instigatorComponent) : null;
                instigatorMeshId = instigatorId != null && !string.IsNullOrEmpty(instigatorId.MeshEntityId) ? instigatorId.MeshEntityId : (instigator.Id ?? string.Empty);
            }
            int n = damageAmounts != null ? damageAmounts.Count : 0;
            GhostHitMessage msg = new GhostHitMessage
            {
                targetMeshId = targetMeshId,
                instigatorMeshId = instigatorMeshId,
                hitBoxPosition = (byte)position,
                fromPosition = fromPosition,
                elementDataIds = new int[n], mins = new float[n], maxs = new float[n],
                hasWeapon = weapon.dataId != 0,
                weapon = weapon,
                skillDataId = skill != null ? skill.DataId : 0,
                skillLevel = skillLevel,
                randomSeed = Random.Range(int.MinValue, int.MaxValue),
            };
            int i = 0;
            if (damageAmounts != null)
            {
                foreach (KeyValuePair<DamageElement, MinMaxFloat> kv in damageAmounts)
                {
                    msg.elementDataIds[i] = kv.Key != null ? kv.Key.DataId : 0;
                    msg.mins[i] = kv.Value.min;
                    msg.maxs[i] = kv.Value.max;
                    i++;
                }
            }
            link.SendGhostHit(msg);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Hit on ghost {ghost.entity.CharacterName} ({targetMeshId}) forwarded to zone {id.OwnerZoneId}");
        }

        internal void OnGhostHit(long connectionId, GhostHitMessage msg)
        {
            if (!IsServer)
                return;
            BaseCharacterEntity target = null;
            if (_ownedByMeshId.TryGetValue(msg.targetMeshId, out BaseCharacterEntity owned) && owned != null)
                target = owned;
            else if (ServerUserHandlers.TryGetPlayerCharacterById(msg.targetMeshId, out IPlayerCharacterData data) && data is BaseCharacterEntity ce)
                target = ce;
            if (target == null || target.IsDead())
                return;

            Dictionary<DamageElement, MinMaxFloat> damageAmounts = new Dictionary<DamageElement, MinMaxFloat>();
            for (int i = 0; i < msg.elementDataIds.Length; ++i)
            {
                DamageElement element = null;
                if (msg.elementDataIds[i] == 0 || !GameInstance.DamageElements.TryGetValue(msg.elementDataIds[i], out element) || element == null)
                    element = GameInstance.Singleton.DefaultDamageElement;
                if (element == null) continue;
                damageAmounts[element] = new MinMaxFloat { min = msg.mins[i], max = msg.maxs[i] };
            }
            BaseSkill skill = null;
            if (msg.skillDataId != 0)
                GameInstance.Skills.TryGetValue(msg.skillDataId, out skill);
            // The attacker is (usually) a ghost here: use it as the instigator so hit rules see a real entity.
            EntityInfo instigator = EntityInfo.Empty;
            if (!string.IsNullOrEmpty(msg.instigatorMeshId) && _ghosts.TryGetValue(msg.instigatorMeshId, out MeshGhost attackerGhost) && attackerGhost.entity != null)
                instigator = attackerGhost.entity.GetInfo();

            int hpBefore = target.CurrentHp;
            target.ApplyDamage((HitBoxPosition)msg.hitBoxPosition, msg.fromPosition, instigator, damageAmounts, msg.hasWeapon ? msg.weapon : CharacterItem.Empty, skill, msg.skillLevel, msg.randomSeed);
            int dealt = Mathf.Max(0, hpBefore - target.CurrentHp);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Forwarded hit applied to {target.CharacterName} ({msg.targetMeshId}): {hpBefore} -> {target.CurrentHp}");

            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string fromZone) && _linkClients.TryGetValue(fromZone, out MeshLinkClient back))
            {
                back.SendGhostHitResult(new GhostHitResultMessage
                {
                    targetMeshId = msg.targetMeshId,
                    combatAmountType = (byte)(dealt > 0 ? CombatAmountType.NormalDamage : CombatAmountType.Miss),
                    totalDamage = dealt,
                    currentHp = target.CurrentHp,
                });
            }
        }

        /// <summary>
        /// Called by MeshGameplayRule on the cell where a monster died: the attacker's ghost was about to be rewarded.
        /// Sends the raw reward to the ghost's owner cell.
        /// </summary>
        internal void ForwardGhostReward(BasePlayerCharacterEntity ghost, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType givenType, int giverLevel, int sourceLevel)
        {
            if (!IsServer || ghost == null)
                return;
            MeshIdentity id = MeshIdentity.Get(ghost);
            if (id == null || !id.IsGhost || string.IsNullOrEmpty(id.OwnerZoneId))
                return;
            if (!_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
            {
                Logging.LogWarning("Mesh", $"Reward for ghost {ghost.CharacterName} dropped: no link to owner zone {id.OwnerZoneId}");
                return;
            }
            List<int> ids = new List<int>(), amounts = new List<int>();
            if (currencies != null)
            {
                foreach (CurrencyAmount c in currencies)
                {
                    if (c.currency == null || c.amount <= 0) continue;
                    ids.Add(c.currency.DataId); amounts.Add(c.amount);
                }
            }
            if (exp <= 0 && gold <= 0 && ids.Count == 0)
                return;
            link.SendGhostReward(new GhostRewardMessage
            {
                attackerMeshId = id.MeshEntityId, exp = exp, gold = gold, multiplier = multiplier, givenType = (byte)givenType,
                giverLevel = giverLevel, sourceLevel = sourceLevel, currencyDataIds = ids.ToArray(), currencyAmounts = amounts.ToArray(),
            });
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Reward on ghost {ghost.CharacterName} forwarded to zone {id.OwnerZoneId}: exp {exp} gold {gold} currencies {ids.Count} x{multiplier} ({givenType})");
        }

        /// <summary>
        /// True when a player ghost (a real player standing on a neighbouring cell) is within range of a position.
        /// Used by MeshMonsterActivityComponent to keep edge monsters awake for players watching from next door.
        /// </summary>
        internal bool IsPlayerGhostNear(Vector3 position, float range)
        {
            float sqr = range * range;
            foreach (MeshGhost g in _ghosts.Values)
            {
                if (g.kind != GhostKind.Player || g.entity == null || g.entity.IsDead())
                    continue;
                if ((g.entity.EntityTransform.position - position).sqrMagnitude <= sqr)
                    return true;
            }
            return false;
        }

        internal void OnGhostReward(GhostRewardMessage msg)
        {
            if (!IsServer)
                return;
            if (!ServerUserHandlers.TryGetPlayerCharacterById(msg.attackerMeshId, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity player))
            {
                Logging.LogWarning("Mesh", $"Forwarded reward for {msg.attackerMeshId} dropped: character not on this cell");
                return;
            }
            RewardGivenType givenType = (RewardGivenType)msg.givenType;
            if (msg.exp > 0)
                player.RewardExp(msg.exp, msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            if (msg.gold > 0)
                player.RewardGold(msg.gold, msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            if (msg.currencyDataIds != null && msg.currencyDataIds.Length > 0)
            {
                List<CurrencyAmount> currencies = new List<CurrencyAmount>();
                for (int i = 0; i < msg.currencyDataIds.Length; ++i)
                {
                    if (GameInstance.Currencies.TryGetValue(msg.currencyDataIds[i], out Currency currency))
                        currencies.Add(new CurrencyAmount { currency = currency, amount = msg.currencyAmounts[i] });
                }
                player.RewardCurrencies(currencies, msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            }
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Forwarded reward applied to {player.CharacterName}: exp {msg.exp} gold {msg.gold} x{msg.multiplier} -> level={player.Level} exp={player.Exp} gold={player.Gold}");
        }

        internal void OnGhostHitResult(GhostHitResultMessage msg)
        {
            if (!IsServer || !_ghosts.TryGetValue(msg.targetMeshId, out MeshGhost ghost) || ghost.entity == null)
                return;
            ghost.entity.CurrentHp = msg.currentHp;
            ghost.entity.CallRpcAppendCombatText((CombatAmountType)msg.combatAmountType, HitEffectsSourceType.None, 0, msg.totalDamage);
        }

        // ---- Incoming handoffs: the neighbour tells us a character is about to reconnect here ----

        [Tooltip("For a character arriving by mesh handoff, do not block its spawn on re-loading relates data (gold, cash, user level, party, guild, storage) from the DB; load them in the background instead. Cuts ~150-300 ms off the swap.")]
        public bool fastSpawnOnHandoff = true;

        private readonly Dictionary<string, float> _incomingHandoffs = new Dictionary<string, float>(); // characterId -> notice time

        internal void OnHandoffNotice(HandoffNoticeMessage msg)
        {
            _incomingHandoffs[msg.characterId] = Time.unscaledTime;
        }

        internal void SendHandoffNotice(string zoneId, string characterId, string userId)
        {
            if (_linkClients.TryGetValue(zoneId, out MeshLinkClient client))
                client.SendHandoffNotice(characterId, userId);
        }

        private bool IsIncomingHandoff(string characterId)
        {
            return _incomingHandoffs.TryGetValue(characterId, out float t) && Time.unscaledTime - t < 15f;
        }

        /// <summary>
        /// The kit awaits ~5 sequential DB round trips here before it spawns the player. A character that was
        /// handed off a moment ago was fully loaded on the neighbour and saved by it, so for those we let the
        /// spawn proceed and fill the relates data in the background (extension seam: protected virtual).
        /// </summary>
        protected override UniTask LoadPlayerCharacterEntityRelatesData(long connectionId, BasePlayerCharacterEntity playerCharacterEntity, string userId, string accessToken)
        {
            if (fastSpawnOnHandoff && playerCharacterEntity != null && IsIncomingHandoff(playerCharacterEntity.Id))
            {
                _incomingHandoffs.Remove(playerCharacterEntity.Id);
                Stamp($"handoff arrival {playerCharacterEntity.CharacterName}: relates data loading in background");
                base.LoadPlayerCharacterEntityRelatesData(connectionId, playerCharacterEntity, userId, accessToken).Forget();
                return UniTask.CompletedTask;
            }
            return base.LoadPlayerCharacterEntityRelatesData(connectionId, playerCharacterEntity, userId, accessToken);
        }
#else
        private void GhostingOnStartServer() { }
        private void GhostingOnStopServer() { }
        private void GhostingUpdate() { }
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter, long connectionId) { }
        private void GhostingOnRealPlayerUnregistered(long connectionId) { }
        private void GhostingOnPlayerHandoffStart(string characterId, string toZone) { }
        private bool TryPromoteGhostForArrival(long connectionId, PlayerCharacterData data, out BasePlayerCharacterEntity entity) { entity = null; return false; }
        private void DemotePlayerToGhost(BasePlayerCharacterEntity entity, string newOwnerZone) { }
        internal void OnMeshNeighbourHello(long connectionId, string zoneId) { }
        internal void OnMeshNeighbourDisconnected(long connectionId) { }
        internal void OnGhostSnapshot(long connectionId, GhostSnapshotMessage msg) { }
        internal void OnGhostRemove(string characterId) { }
        internal void OnHandoffNotice(HandoffNoticeMessage msg) { }
        internal void OnGhostHit(long connectionId, GhostHitMessage msg) { }
        internal void OnGhostHitResult(GhostHitResultMessage msg) { }
        internal void OnGhostReward(GhostRewardMessage msg) { }
        internal void OnMonsterHandoff(long connectionId, MonsterHandoffMessage msg) { }
        internal void OnMonsterGone(string meshId) { }
        internal bool IsPlayerGhostNear(Vector3 position, float range) { return false; }
        internal void ForwardGhostReward(BasePlayerCharacterEntity ghost, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType givenType, int giverLevel, int sourceLevel) { }
        internal void SendHandoffNotice(string zoneId, string characterId, string userId) { }
#endif
    }
}
