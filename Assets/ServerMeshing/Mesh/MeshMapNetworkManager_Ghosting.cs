// Server Meshing add-on — border ghosting (Spike 1b / P2).
// See Docs/ServerMeshing/DESIGN.md §5, §7, §8, §10 and SPIKE1.md §5.
//
// EXTENSION-ONLY (partial of the MapNetworkManager subclass). Server side only:
//  * every `ghostSnapshotInterval` each owned player inside the border band B of a shared edge is
//    streamed (full-precision position + movement state) to that neighbour over the CellMeshLink;
//  * a received snapshot spawns/steers a GHOST: a normal player entity spawned server-side, with its
//    server-driver components silenced (GhostSuppression) but its movement component kept, so it is
//    steered toward the snapshot with the kit's own movement API ("puppet"), animates, and reaches
//    this cell's clients through the ordinary AOI/sync path;
//  * dedup/promotion (DESIGN §8): a snapshot for a character this cell owns for real is ignored, and
//    when the real character registers here (handoff-in) any ghost with the same id is destroyed.
// Ghosts are never registered as player characters, so the boundary tick and streaming ignore them.

using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — border ghosting (server side)")]
        [Tooltip("Stream owned players near a shared zone edge to the neighbour cell, and spawn ghosts for what the neighbour streams to us.")]
        public bool enableBorderGhosting = true;

        [Tooltip("Border band B (metres from the shared edge) inside which players are ghosted into the neighbour.")]
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
            public BasePlayerCharacterEntity entity;
            public float lastSeen;
            public Vector3 lastTarget;
        }

        private MeshLinkServer _linkServer;
        private readonly Dictionary<string, MeshLinkClient> _linkClients = new Dictionary<string, MeshLinkClient>();   // zoneId -> link
        private readonly Dictionary<long, string> _neighbourZoneByConnection = new Dictionary<long, string>();
        private readonly Dictionary<string, MeshGhost> _ghosts = new Dictionary<string, MeshGhost>();                  // characterId -> ghost
        private readonly Dictionary<string, HashSet<string>> _streamedToZone = new Dictionary<string, HashSet<string>>(); // zoneId -> character ids streamed last tick
        private readonly List<string> _tmpIds = new List<string>();
        private float _nextSnapshotTime;
        private float _nextLinkCheck;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void GhostingOnStartServer()
        {
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

        /// <summary>Distance from a world position to the edge shared with the neighbour at offset index i (∞ if not adjacent along that axis).</summary>
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

                foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
                {
                    BasePlayerCharacterEntity entity = data as BasePlayerCharacterEntity;
                    if (entity == null || entity.IsWarping)
                        continue;
                    Vector3 pos = entity.EntityTransform.position;
                    if (DistanceToSharedEdge(pos, i) > ghostBandWidth)
                        continue;
                    client.SendGhostSnapshot(new GhostSnapshotMessage
                    {
                        characterId = entity.Id,
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
                    streamed.Add(entity.Id);
                }

                // Anything streamed last tick but not now has left the band (or this cell): tell the neighbour.
                for (int k = 0; k < _tmpIds.Count; ++k)
                {
                    if (!streamed.Contains(_tmpIds[k]))
                        client.SendGhostRemove(_tmpIds[k]);
                }
            }
        }

        private void SendGhostRemoveToAll(string characterId)
        {
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                if (_streamedToZone.TryGetValue(kv.Key, out HashSet<string> streamed) && streamed.Remove(characterId))
                    kv.Value.SendGhostRemove(characterId);
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

        private void DestroyGhost(string characterId, string reason)
        {
            if (!_ghosts.TryGetValue(characterId, out MeshGhost ghost))
                return;
            _ghosts.Remove(characterId);
            if (ghost.entity != null)
            {
                Logging.Log("Mesh", $"Ghost destroyed {ghost.entity.CharacterName} ({characterId}) reason={reason}");
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
            if (ServerUserHandlers.TryGetPlayerCharacterById(msg.characterId, out _))
                return;

            if (!_ghosts.TryGetValue(msg.characterId, out MeshGhost ghost) || ghost.entity == null)
            {
                int hashAssetId = GameInstance.GetPlayerCharacterEntityHashAssetId(msg.entityId, out int metaDataId);
                LiteNetLibIdentity identity = Assets.NetworkSpawn(hashAssetId, msg.position, Quaternion.Euler(0f, msg.yaw, 0f));
                if (identity == null)
                {
                    WarnThrottled($"Cannot spawn ghost for entity id {msg.entityId}");
                    return;
                }
                BasePlayerCharacterEntity entity = identity.GetComponent<BasePlayerCharacterEntity>();
                if (entity == null)
                {
                    identity.NetworkDestroy();
                    return;
                }
                GameInstance.SetupByMetaData(entity, metaDataId);
                entity.Id = msg.characterId;
                entity.DataId = msg.dataId;
                entity.CharacterName = msg.characterName;
                entity.Level = msg.level;
                int silenced = GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
                ghost = new MeshGhost { entity = entity, lastTarget = msg.position };
                _ghosts[msg.characterId] = ghost;
                string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
                Logging.Log("Mesh", $"Ghost spawned {msg.characterName} ({msg.characterId}) from zone {fromZone} at {msg.position} (silenced {silenced})");
            }

            ghost.lastSeen = Time.unscaledTime;
            SteerGhost(ghost, msg);
        }

        private void SteerGhost(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            BasePlayerCharacterEntity entity = ghost.entity;
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

        internal void OnGhostRemove(string characterId)
        {
            DestroyGhost(characterId, "neighbour removed");
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
#endif
    }
}
