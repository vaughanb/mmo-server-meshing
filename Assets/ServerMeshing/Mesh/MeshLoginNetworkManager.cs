// Server Meshing add-on — MeshLoginNetworkManager (DESIGN.md §9 routing, §13 "a cell that comes back").
//
// The kit's login server routes a selected character to the map server registered for {the client's chosen channel}
// + {the character's map}. On a meshed map the channel IS the cell, and the right cell is the one whose zone contains
// the position the map server will place the character at (its map info's enter point: the saved position, or a
// first arrival's surface exit) — not whatever channel the client picked, and not the one that saved the row. This
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
        [Tooltip("Seconds a character selection waits for its sleeping cell to start. Capped 10 s below clientSelectTimeoutSeconds so the player gets the server's answer rather than a timeout.")]
        public float loginWakeTimeoutSeconds = 20f;
        [Tooltip("Seconds the client waits for the answer to its character selection: the kit's request timeout (30 s). Raise it together with the timeout the client sends RequestSelectCharacter with, when a cold cell needs longer to start.")]
        public float clientSelectTimeoutSeconds = 30f;

        // The server must answer before the client gives up, with room for the rest of the selection.
        private const float CLIENT_TIMEOUT_MARGIN_SECONDS = 10f;

        /// <summary>LoginWakeTimeout is how long a selection waits for a sleeping cell: the configured seconds, capped so the client still gets an answer.</summary>
        public static float LoginWakeTimeout(float configuredSeconds, float clientTimeoutSeconds)
        {
            return Mathf.Max(0f, Mathf.Min(configuredSeconds, clientTimeoutSeconds - CLIENT_TIMEOUT_MARGIN_SECONDS));
        }
        [Tooltip("Slice 5: seconds a login waits for the central's layout coordinator to finish recovering (a restarted central adopting the cells' layout) before it routes; past it the layout as it stands routes.")]
        public float recoveryWaitSeconds = 15f;

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
                await WaitForRecovery();
                DatabaseApiResult<CharacterResp> characterResp = await DatabaseClient.GetCharacterAsync(new GetCharacterReq()
                {
                    UserId = userPeerInfo.userId,
                    CharacterId = request.characterId,
                    ForceClearCache = true,
                });
                PlayerCharacterData character = characterResp.IsSuccess ? characterResp.Response.CharacterData : null;
                if (character != null && world.HasLayout(character.CurrentMapName))
                {
                    // Where the map server will place the character, which is not always the saved position (a first
                    // arrival goes to a surface exit). This copy is ours and never saved, so letting the map info
                    // write to it is harmless; the kit's handler fetches its own.
                    if (GameInstance.MapInfos.TryGetValue(character.CurrentMapName, out BaseMapInfo mapInfo) && mapInfo != null)
                    {
                        mapInfo.GetEnterMapPoint(character, out _, out Vector3 arrival, out _);
                        character.CurrentPosition = arrival;
                    }
                    if (CentralNetworkManager is MeshCentralNetworkManager central && central.sleepCells)
                    {
                        // Sleeping cells: the owning cell serves the character; wake it first if it is asleep.
                        MeshCell owner = MeshLoginRouting.OwnerAt(world, character.CurrentMapName, character.CurrentPosition);
                        if (owner != null && !owner.isDynamic && !central.IsCellUp(owner))
                        {
                            Logging.Log("Mesh", $"Login into sleeping cell {owner.id}: waking it for {character.CharacterName}");
                            bool up = await central.WakeAndWait(owner, LoginWakeTimeout(loginWakeTimeoutSeconds, clientSelectTimeoutSeconds));
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

        /// <summary>
        /// WaitForRecovery (Slice 5 decision 9): while the central's coordinator recovers, the layout it will adopt is not
        /// applied yet, so a login waits up to recoveryWaitSeconds for it (200 ms polls) and says how long it waited.
        /// </summary>
        private async UniTask WaitForRecovery()
        {
            MeshLayoutCoordinator coordinator = CentralNetworkManager is MeshCentralNetworkManager central ? central.Coordinator : null;
            if (coordinator == null || coordinator.Phase != MeshCoordinatorPhase.Recovering)
                return;
            float started = Time.unscaledTime;
            while (MeshRecoveryWait.KeepWaiting(coordinator.Phase == MeshCoordinatorPhase.Recovering, Time.unscaledTime - started, recoveryWaitSeconds))
                await UniTask.Delay(200);
            string line = MeshRecoveryWait.Line(true, coordinator.Phase == MeshCoordinatorPhase.Recovering, Time.unscaledTime - started, out bool warning);
            if (warning)
                Logging.LogWarning("Mesh", line);
            else
                Logging.Log("Mesh", line);
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
            // Rig only: MESH_TEST_LOGIN_VIA=<cell id> routes every login of the map through that cell while it is live, the
            // way a login lands on a neighbour while the owner re-registers; the neighbour hands the character on at once.
            string via = System.Environment.GetEnvironmentVariable("MESH_TEST_LOGIN_VIA");
            if (!string.IsNullOrEmpty(via) && live.Contains(via))
            {
                Logging.LogWarning("Mesh", $"Login routing: MESH_TEST_LOGIN_VIA sends {character.CharacterName} through {via}");
                return via;
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
