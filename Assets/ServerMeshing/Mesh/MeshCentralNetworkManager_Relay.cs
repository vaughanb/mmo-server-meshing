// Server Meshing add-on — the central's party-share relay (Slice 7, slices/slice7_design.md decision 9). A cell whose map
// links on demand has no link to most cells of its map, so a share for a party member that is neither on it nor a ghost
// on it (the kit's unlimited share distance) comes here and goes on to every other map server of that map; only the one
// holding the member applies it, as with the broadcast over every link that linkAllZones made possible.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshCentralNetworkManager
    {
        private readonly List<MeshMapServerPeer> _relayPeers = new List<MeshMapServerPeer>();

        private void HandlePartyShareRelay(MessageHandlerData messageHandler)
        {
            MeshPartyShareRelayMessage relay = messageHandler.ReadMessage<MeshPartyShareRelayMessage>();
            if (ClusterServer == null)
                return;
            MeshClusterCoordinatorTransport peers = new MeshClusterCoordinatorTransport(ClusterServer);
            if (peers.TryGetMapServer(messageHandler.ConnectionId, out MeshMapServerPeer sender))
                relay.mapId = sender.mapId; // the sender's registration says which map, not the payload
            _relayPeers.Clear();
            peers.GetMapServers(_relayPeers);
            List<long> targets = MeshPartyRelay.Targets(_relayPeers, relay.mapId, messageHandler.ConnectionId);
            for (int i = 0; i < targets.Count; ++i)
                ClusterServer.SendPacket(targets[i], 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.PartyShareRelay, writer => relay.Serialize(writer));
        }
    }
}
