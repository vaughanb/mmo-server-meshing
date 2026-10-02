// Server Meshing add-on — MeshLoginNetworkManager (DESIGN.md §9 routing, §13 "a cell that comes back").
//
// The kit's login server routes a selected character to the map server registered for {the client's chosen channel}
// + {the character's map}. On a meshed map the channel IS the cell, and the right cell is the one whose zone contains
// the character's saved position — not whatever channel the client picked, and not the one that saved the row. This
// subclass (swapped onto the demo's server-instance prefab) rewrites the request's channel before the kit's own
// handler runs: the zone that owns the position when a server serves it, otherwise the nearest live cell of the same
// map (any cell can host a character standing anywhere and hands it off by position once the right cell is back).
// Maps that are not meshed keep the kit's routing untouched.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshLoginNetworkManager : LoginNetworkManager
    {
        [Header("Server Meshing — routing by position")]
        [Tooltip("World registry: characters on a map that has a layout are routed to the cell that owns their saved position; other maps keep the kit's channel routing.")]
        public MeshWorldRegistry world;
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
            // With a Dynamic map the login routes by the central's registry, which holds the runtime layout (the login
            // runs in the central's process); an all-Static world keeps its own, as it always has.
            world = CentralNetworkManager is MeshCentralNetworkManager meshCentral && meshCentral.world != null && meshCentral.world.HasDynamicMap
                ? meshCentral.world
                : MeshLayoutOverride.Resolve(world);
            if (routeByPosition && world != null && !string.IsNullOrEmpty(request.characterId) && _userPeers.TryGetValue(requestHandler.ConnectionId, out CentralUserPeerInfo userPeerInfo))
            {
                DatabaseApiResult<CharacterResp> characterResp = await DatabaseClient.GetCharacterAsync(new GetCharacterReq()
                {
                    UserId = userPeerInfo.userId,
                    CharacterId = request.characterId,
                    ForceClearCache = true,
                });
                PlayerCharacterData character = characterResp.IsSuccess ? characterResp.Response.CharacterData : null;
                if (character != null && world.HasLayout(character.CurrentMapName))
                {
                    if (CentralNetworkManager is MeshCentralNetworkManager central && central.sleepCells)
                    {
                        // Sleeping cells: the owning cell serves the character; wake it first if it is asleep.
                        MeshCell owner = MeshLoginRouting.OwnerAt(world, character.CurrentMapName, character.CurrentPosition);
                        if (owner != null && !owner.isDynamic && !central.IsCellUp(owner))
                        {
                            Logging.Log("Mesh", $"Login into sleeping cell {owner.id}: waking it for {character.CharacterName}");
                            bool up = await central.WakeAndWait(owner, 20f);
                            Logging.Log("Mesh", $"Cell {owner.id} {(up ? "is up" : "did not come up in time")} for {character.CharacterName}");
                        }
                    }
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

        /// <summary>The cell owning the position when its server is up, else the nearest live cell of the map, else null (the kit's routing decides).</summary>
        private string RouteByPosition(PlayerCharacterData character)
        {
            string mapId = character.CurrentMapName;
            List<string> live = new List<string>();
            foreach (CentralServerPeerInfo peer in ClusterServer.MapServerPeersByKey.Values)
            {
                if (peer.peerType == CentralServerPeerType.MapServer && string.Equals(peer.refId, mapId))
                    live.Add(peer.channelId);
            }
            // With the owner down, the nearest live cell of the map serves the character where it stands and hands it
            // off by position as soon as the right cell is back.
            MeshCell routed = MeshLoginRouting.Route(world, mapId, character.CurrentPosition, live, out bool fellBack);
            if (fellBack)
            {
                MeshCell owner = MeshLoginRouting.OwnerAt(world, mapId, character.CurrentPosition);
                Logging.LogWarning("Mesh", $"Login routing: no server for cell {(owner != null ? owner.id : "?")} of {mapId} ({character.CharacterName} at {(Vector3)character.CurrentPosition}); the nearest live cell {routed.id} serves it");
            }
            return routed?.id;
        }
#endif
    }
}
