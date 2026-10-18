// Server Meshing add-on — entities across a split, the parts that are not statics (Slice 2b, slices/slice2b_design.md
// decisions 11, 12 and 13): MonsterHandoffFailed (128), so a receiver that cannot spawn a monster no longer leaks the
// sender's lent record; server-owned characters (no client) handed to the cell that owns their position through the
// registered IMeshServerCharacterAdopter of their kind (ServerCharacterHandoff, 129); every spawn area's prefabs
// registered on a Dynamic map (inactive areas included, so a child that disables every area can spawn puppets and
// handed-off entities); the spawn-area filter on a Dynamic map tested against the cell's base rectangle (spawn areas
// never move: the base process is home for its whole base rectangle); and the per-snapshot-tick entry point of the
// split work. Also the "Ghost owner changed" line once this cell has applied a layout. Static maps: unchanged.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Dynamic maps: after a monster handoff is refused by its receiver, seconds before that monster is handed again.")]
        public float monsterHandoffFailedRetrySeconds = 10f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly List<BasePlayerCharacterEntity> _serverCharacterCandidates = new List<BasePlayerCharacterEntity>();
        private bool _layoutAppliedHere;

        /// <summary>The split work of a Dynamic map's cell, every snapshot tick: evidence samples, statics, server-owned characters.</summary>
        private void SplitEntityTick()
        {
            if (!IsDynamicMap || world == null || !HasOwnZone)
                return;
            float now = Time.unscaledTime;
            SplitEvidenceTick(now);
            DrainTick(now); // Slice 2c: a Draining cell's own work and its retire check
            if (IsLayoutGated || !enableServerBoundaryHandoff)
                return;
            StaticHandoffTick(now);
            if (_splitOpInProgress)
                ServerCharacterTick();
        }

        /// <summary>OnServerOnlineSceneLoaded's hook, after the kit registered the active areas: a Dynamic map registers every area's prefabs.</summary>
        private void SplitOnSceneLoaded()
        {
            if (world != null && world.IsDynamicMap(OwnMapId))
                RegisterSpawnAreaPrefabs();
        }

        /// <summary>Registers the prefabs of every monster, harvestable and item drop spawn area in the scene, inactive ones included.</summary>
        private int RegisterSpawnAreaPrefabs()
        {
            int areas = 0;
            foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                area.RegisterPrefabs();
                areas++;
            }
            foreach (HarvestableSpawnArea area in FindObjectsByType<HarvestableSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                area.RegisterPrefabs();
                areas++;
            }
            foreach (ItemDropSpawnArea area in FindObjectsByType<ItemDropSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                area.RegisterPrefabs();
                areas++;
            }
            Logging.Log("Mesh", $"Spawn-area prefabs registered for puppets and handoffs: {areas} area(s)");
            return areas;
        }

        /// <summary>
        /// Whether this cell runs the spawn area at a scene position: on a Dynamic map, a base cell runs every area of its
        /// base rectangle and a child none; elsewhere the cell that owns the position (today's rule).
        /// </summary>
        private bool SpawnAreaOwnedHere(Vector3 areaScenePos)
        {
            if (world != null && world.IsDynamicMap(OwnMapId))
            {
                if (OwnCell == null || OwnCell.isDynamic)
                    return false;
                Vector3 areaWorld = OwnCell.SceneToWorld(areaScenePos);
                return OwnCell.baseRect.Contains(areaWorld.x, areaWorld.z);
            }
            return MeshCell.Same(world.OwnerOf(OwnCell, areaScenePos), OwnCell);
        }

        /// <summary>OnGhostSnapshot's hook when an existing puppet's snapshots start coming from another zone (logged once this cell has applied a layout).</summary>
        private void NoteGhostOwnerChange(GhostSnapshotMessage msg, string oldZone, string newZone)
        {
            if (_layoutAppliedHere)
                Logging.Log("Mesh", $"Ghost owner changed {msg.kind} {msg.characterName} ({msg.characterId}) zone {oldZone}->{newZone} at {msg.position}");
        }

        // ---- MonsterHandoffFailed (128) ----

        /// <summary>OnMonsterHandoff's hook where it cannot spawn the monster: on a Dynamic map the sender is told, so it re-promotes.</summary>
        private void ReplyMonsterHandoffFailed(string fromZone, string meshId, string reason)
        {
            if (!IsDynamicMap || string.IsNullOrEmpty(fromZone) || !_linkClients.TryGetValue(fromZone, out MeshLinkClient link) || !link.IsConnected)
                return;
            link.SendMonsterHandoffFailed(meshId, reason);
            Logging.LogWarning("Mesh", $"Monster handoff {meshId} from {fromZone} not taken ({reason}); told the sender");
        }

        internal void OnMonsterHandoffFailed(long connectionId, MonsterHandoffFailedMessage msg)
        {
            if (!IsServer)
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            if (!_ghosts.TryGetValue(msg.meshId, out MeshGhost ghost) || !(ghost.entity is BaseMonsterCharacterEntity monster)
                || monster.Identity == null || monster.Identity.IsDestroyed || monster.IsDead())
            {
                if (_lentMonsters.TryGetValue(msg.meshId, out LentMonster lent))
                {
                    _lentMonsters.Remove(msg.meshId);
                    DestroyGhost(msg.meshId, "handoff failed");
                    if (lent.area != null && lent.prefab != null)
                        lent.area.Spawn(lent.prefab, lent.level, lent.respawnDelay, lent.respawnDelay);
                }
                Logging.LogWarning("Mesh", $"Monster handoff {msg.meshId} {OwnZoneId}->{fromZone} failed there ({msg.reason}); no puppet left to re-promote");
                return;
            }
            PromoteGhostInPlace(msg.meshId, ghost);
            MeshIdentity id = MeshIdentity.GetOrAdd(monster);
            string home = string.IsNullOrEmpty(id.HomeZoneId) ? OwnZoneId : id.HomeZoneId;
            if (string.Equals(home, OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
            {
                _lentMonsters.Remove(msg.meshId); // its own spawn area is still set on the entity
            }
            else
            {
                string capturedMeshId = msg.meshId;
                CharacterEntityKilledDelegate killHook = (target, instigator) => OnBorrowedMonsterKilled(capturedMeshId, home);
                monster.onKilled += killHook;
                _borrowedKillHooks[msg.meshId] = killHook;
                if (_linkClients.TryGetValue(home, out MeshLinkClient homeLink) && homeLink.IsConnected)
                    homeLink.SendLentMonsterOwner(msg.meshId, OwnZoneId);
            }
            TrackOwned(monster);
            // The handoff cooldown runs from LastHandoffTime: hold this one back for the retry delay instead.
            id.LastHandoffTime = Time.unscaledTime + Mathf.Max(0f, monsterHandoffFailedRetrySeconds - monsterHandoffCooldown);
            Logging.LogWarning("Mesh", $"Monster handoff {msg.meshId} {OwnZoneId}->{fromZone} failed there ({msg.reason}); re-promoted here");
        }

        // ---- Server-owned characters (129) ----

        /// <summary>
        /// While an operation is in progress: a server-owned character (no client) standing in another cell's owned
        /// rectangle of this map is captured by the adopter of its kind and sent there. Without an adopter it stays (and
        /// the drain gate counts it).
        /// </summary>
        private void ServerCharacterTick()
        {
            if (MeshServerCharacterAdopters.All.Count == 0)
                return;
            _serverCharacterCandidates.Clear();
            List<OwnedEntry<BaseCharacterEntity>> owned = _ownedCharacters.Entries;
            for (int i = 0; i < owned.Count; ++i)
            {
                if (!(owned[i].entity is BasePlayerCharacterEntity character) || character == null || character.Identity == null || character.Identity.IsDestroyed)
                    continue;
                if (MeshIdentity.IsGhostEntity(character) || _handoffsInProgress.Contains(character.Id) || ServerUserHandlers.TryGetPlayerCharacterById(character.Id, out _))
                    continue; // a client's character goes by MeshHandoff
                Vector3 pos = character.EntityTransform.position;
                MeshCell owner = world.OwnerOf(OwnCell, pos);
                if (owner == null || MeshCell.Same(owner, OwnCell) || !string.Equals(owner.mapId, OwnCell.mapId, System.StringComparison.Ordinal))
                    continue;
                if (-OwnCell.DistanceToEdge(pos) < handoffHysteresis || !IsNeighbourLinked(owner.id) || !IsNeighbourSettled(owner.id))
                    continue;
                if (AdmitEntityDrain(pos))
                    _serverCharacterCandidates.Add(character);
            }
            for (int i = 0; i < _serverCharacterCandidates.Count; ++i)
                HandServerCharacter(_serverCharacterCandidates[i]);
            _serverCharacterCandidates.Clear();
        }

        private void HandServerCharacter(BasePlayerCharacterEntity character)
        {
            Vector3 pos = character.EntityTransform.position;
            MeshCell owner = world.OwnerOf(OwnCell, pos);
            if (owner == null || !_linkClients.TryGetValue(owner.id, out MeshLinkClient link) || !link.IsConnected)
                return;
            IReadOnlyList<IMeshServerCharacterAdopter> adopters = MeshServerCharacterAdopters.All;
            for (int i = 0; i < adopters.Count; ++i)
            {
                if (!adopters[i].TryCapture(character, out byte[] state))
                    continue;
                string meshId = character.Id;
                link.SendServerCharacterHandoff(new ServerCharacterHandoffMessage
                {
                    kind = adopters[i].Kind,
                    meshId = meshId,
                    state = state,
                    position = pos,
                    yaw = character.EntityTransform.eulerAngles.y,
                });
                UntrackOwned(character);
                foreach (HashSet<string> streamed in _streamedToZone.Values)
                    streamed.Remove(meshId); // the new owner's snapshots take the other cells' puppets over
                Logging.Log("Mesh", $"Server character {character.CharacterName} ({meshId}) handed to zone {owner.id} by adopter {adopters[i].Kind}");
                character.NetworkDestroy();
                return;
            }
        }

        internal void OnServerCharacterHandoff(long connectionId, ServerCharacterHandoffMessage msg)
        {
            if (!IsServer)
                return;
            msg.position += OffsetFrom(connectionId);
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            PrewarmConsume(msg.meshId);
            if (!string.IsNullOrEmpty(msg.meshId) && _ghosts.ContainsKey(msg.meshId))
                DestroyGhost(msg.meshId, "adopted here");
            IMeshServerCharacterAdopter adopter = MeshServerCharacterAdopters.ByKind(msg.kind);
            if (adopter == null)
            {
                Logging.LogWarning("Mesh", $"Server character {msg.meshId} from zone {fromZone}: no adopter of kind {msg.kind} here; it is lost");
                return;
            }
            if (!adopter.TryAdopt(msg.state, msg.position, msg.yaw, OwnCell != null ? OwnCell.owned : MeshRect.Empty))
                Logging.LogWarning("Mesh", $"Server character {msg.meshId} from zone {fromZone}: adopter {msg.kind} could not adopt it; it is lost");
        }
#else
        internal void OnMonsterHandoffFailed(long connectionId, MonsterHandoffFailedMessage msg) { }
        internal void OnServerCharacterHandoff(long connectionId, ServerCharacterHandoffMessage msg) { }
#endif
    }
}
