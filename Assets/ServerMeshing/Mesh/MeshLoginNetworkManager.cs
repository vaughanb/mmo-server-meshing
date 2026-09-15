// Server Meshing add-on — MeshLoginNetworkManager (DESIGN.md §9 routing, §13 "a cell that comes back").
//
// The kit's login server routes a selected character to the map server registered for {the client's chosen channel}
// + {the character's map}. On a meshed map the channel IS the cell, and the right cell is the one whose zone contains
// the character's saved position — not whatever channel the client picked, and not the one that saved the row. This
// subclass (swapped onto the demo's server-instance prefab) rewrites the request's channel before the kit's own
// handler runs: the zone that owns the position when a server serves it, otherwise the nearest live cell of the same
// map (any cell can host a character standing anywhere and hands it off by position once the right cell is back).
// Maps that are not meshed keep the kit's routing untouched.
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshLoginNetworkManager : LoginNetworkManager
    {
        [Header("Server Meshing — routing by position")]
        [Tooltip("Zone grid of the meshed map (zoneId == channelId). Characters on this map are routed to the cell that owns their saved position.")]
        public ServerMeshTestConfig meshConfig;
        [Tooltip("The logical map the grid covers; characters on any other map keep the kit's channel routing.")]
        public string meshMapName = "ServerMeshTestMap";
        [Tooltip("Route selected characters by their saved position instead of the client's chosen channel.")]
        public bool routeByPosition = true;

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        protected override void RegisterMessages()
        {
            base.RegisterMessages();
            // Same request type, our handler in front of the kit's (the registration table keeps the last one).
            RegisterRequestToServer<RequestSelectCharacterMessage, ResponseSelectCharacterMessage>(MMORequestTypes.SelectCharacter, HandleRequestSelectCharacterByPosition);
        }

        protected async UniTaskVoid HandleRequestSelectCharacterByPosition(RequestHandlerData requestHandler, RequestSelectCharacterMessage request, RequestProceedResultDelegate<ResponseSelectCharacterMessage> result)
        {
            if (routeByPosition && meshConfig != null && !string.IsNullOrEmpty(request.characterId) && _userPeers.TryGetValue(requestHandler.ConnectionId, out CentralUserPeerInfo userPeerInfo))
            {
                DatabaseApiResult<CharacterResp> characterResp = await DatabaseClient.GetCharacterAsync(new GetCharacterReq()
                {
                    UserId = userPeerInfo.userId,
                    CharacterId = request.characterId,
                    ForceClearCache = true,
                });
                PlayerCharacterData character = characterResp.IsSuccess ? characterResp.Response.CharacterData : null;
                if (character != null && string.Equals(character.CurrentMapName, meshMapName))
                {
                    string routed = RouteByPosition(character);
                    if (!string.IsNullOrEmpty(routed) && !string.Equals(routed, request.channelId))
                    {
                        Logging.Log("Mesh", $"Login routing: {character.CharacterName} at {(Vector3)character.CurrentPosition} -> channel {routed} (client asked for '{request.channelId}', row says '{character.CurrentChannel}')");
                        request.channelId = routed;
                    }
                }
            }
            HandleRequestSelectCharacter(requestHandler, request, result).Forget();
        }

        /// <summary>The cell whose zone owns the position when it is up, else the nearest live cell of the map, else null (the kit's routing decides).</summary>
        private string RouteByPosition(PlayerCharacterData character)
        {
            Vector2Int coord = meshConfig.WorldToZoneCoord(character.CurrentPosition);
            ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZone(coord);
            if (zone != null && ClusterServer.MapServerPeersByKey.ContainsKey(PeerInfoExtensions.GetPeerInfoKey(zone.zoneId, meshMapName)))
                return zone.zoneId;
            // That cell is down (or the position lies outside the grid): the nearest live cell serves the character where
            // it stands and hands it off by position as soon as the right cell is back.
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (CentralServerPeerInfo peer in ClusterServer.MapServerPeersByKey.Values)
            {
                if (peer.peerType != CentralServerPeerType.MapServer || !string.Equals(peer.refId, meshMapName))
                    continue;
                ServerMeshTestConfig.ZoneDefinition candidate = meshConfig.GetZoneById(peer.channelId);
                if (candidate == null)
                    continue;
                int distance = Mathf.Max(Mathf.Abs(candidate.gridX - coord.x), Mathf.Abs(candidate.gridY - coord.y));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate.zoneId;
                }
            }
            if (best != null)
                Logging.LogWarning("Mesh", $"Login routing: no server for zone {(zone != null ? zone.zoneId : coord.ToString())} ({character.CharacterName} at {(Vector3)character.CurrentPosition}); the nearest live cell {best} serves it");
            return best;
        }
#endif
    }
}
