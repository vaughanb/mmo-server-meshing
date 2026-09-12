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
                SendBandSnapshots();
                ExpireGhosts();
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
                    client.SendGhostSnapshot(new GhostSnapshotMessage
                    {
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
                ghost.entity.NetworkDestroy();
            }
        }

        /// <summary>Called when the real character registers on this cell (handoff-in): the ghost must go.</summary>
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter)
        {
            if (playerCharacter != null)
                DestroyGhost(playerCharacter.Id, "promoted to real");
        }

        private void GhostingOnRealPlayerUnregistered(long connectionId)
        {
            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData data) && data != null)
                SendGhostRemoveToAll(data.Id);
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
                entity.onReceiveDamage += (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                    ForwardGhostHit(capturedMeshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
                ghost = new MeshGhost { entity = entity, kind = msg.kind, lastTarget = msg.position };
                _ghosts[msg.characterId] = ghost;
                Logging.Log("Mesh", $"Ghost spawned {msg.kind} {msg.characterName} ({msg.characterId}) from zone {id.OwnerZoneId} at {msg.position} (silenced {silenced})");
            }

            ghost.lastSeen = Time.unscaledTime;
            SteerGhost(ghost, msg);
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
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter) { }
        private void GhostingOnRealPlayerUnregistered(long connectionId) { }
        internal void OnMeshNeighbourHello(long connectionId, string zoneId) { }
        internal void OnMeshNeighbourDisconnected(long connectionId) { }
        internal void OnGhostSnapshot(long connectionId, GhostSnapshotMessage msg) { }
        internal void OnGhostRemove(string characterId) { }
        internal void OnHandoffNotice(HandoffNoticeMessage msg) { }
        internal void OnGhostHit(long connectionId, GhostHitMessage msg) { }
        internal void OnGhostHitResult(GhostHitResultMessage msg) { }
        internal void SendHandoffNotice(string zoneId, string characterId, string userId) { }
#endif
    }
}
