// Server Meshing add-on — the central tells the map servers that a dead map server's users are offline (DESIGN.md 0.91).
//
// The central takes over the kit's UpdateMapUser handler (ClusterServer registers it privately; registering the same
// message type again replaces it): every message is relayed exactly as the kit does (ClusterServer.UpdateMapUser to every
// other map server) and recorded in a MeshMapUserTable. Once a second the table is checked against the registered map
// servers; the users of one that left the cluster are relayed as a Remove to the rest, as its own disconnect handling
// would have done had it not died, so their re-login into another cell is not refused as a nested login
// (MeshMapUserTable has the whole story). A restarted central starts empty and learns every live user again from the
// map servers' re-registration, which re-announces their own players (MeshMapNetworkManager.ClusterMirrorTick purges the
// others first).
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshCentralNetworkManager
    {
        private readonly MeshMapUserTable _mapUsers = new MeshMapUserTable();
        private float _nextMapUsersTick;

        /// <summary>The users each map server announced, as the central tracks them (tests and tools).</summary>
        public MeshMapUserTable MapUsers => _mapUsers;

        /// <summary>Takes over the kit's UpdateMapUser handler (called once the cluster server exists).</summary>
        private void MapUsersRegister()
        {
            ClusterServer.RegisterMessageHandler(MMOMessageTypes.UpdateMapUser, HandleUpdateMapUserTracked);
        }

        private void HandleUpdateMapUserTracked(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            long connectionId = messageHandler.ConnectionId;
            UpdateUserCharacterMessage message = messageHandler.ReadMessage<UpdateUserCharacterMessage>();
            ClusterServer.UpdateMapUser(message.type, message.character, connectionId);   // what the kit's handler does
            string key = ClusterServer.MapServerPeers.TryGetValue(connectionId, out CentralServerPeerInfo peer) ? peer.GetPeerInfoKey() : null;
            _mapUsers.Record(message.type, message.character, connectionId, key);
#endif
        }

        /// <summary>Once a second: the users of every map server that left the cluster are relayed as offline to the rest.</summary>
        private void MapUsersTick()
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (ClusterServer == null || !IsServer || _mapUsers.Count == 0 || Time.unscaledTime < _nextMapUsersTick)
                return;
            _nextMapUsersTick = Time.unscaledTime + 1f;
            foreach (MeshMapUserTable.Gone left in _mapUsers.TakeGone(c => ClusterServer.MapServerPeers.TryGetValue(c, out CentralServerPeerInfo p) ? p.GetPeerInfoKey() : null))
            {
                foreach (SocialCharacterData user in left.users)
                    ClusterServer.UpdateMapUser(UpdateUserCharacterMessage.UpdateType.Remove, user, left.connectionId);
                Logging.LogWarning("Mesh", MeshMapUserTable.GoneLine(left, ClusterServer.MapServerPeers.Count));
            }
#endif
        }
    }
}
