// Server Meshing add-on — the coordinator's way to the map servers through the kit's cluster server (DESIGN.md 0.72).
// The only coordinator code that touches ClusterServer, so the coordinator itself stays testable.
using System.Collections.Generic;
using LiteNetLib;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshClusterCoordinatorTransport reaches registered map servers over the kit's cluster server.</summary>
    public sealed class MeshClusterCoordinatorTransport : IMeshCoordinatorTransport
    {
        private readonly ClusterServer _server;

        public MeshClusterCoordinatorTransport(ClusterServer server)
        {
            _server = server ?? throw new System.ArgumentNullException(nameof(server));
        }

        /// <summary>ToPeer turns a kit peer into the coordinator's view of it; false for anything that is not a cell's map server.</summary>
        public static bool ToPeer(CentralServerPeerInfo info, out MeshMapServerPeer peer)
        {
            peer = default;
            // The same table also holds instance map servers; those are never cells.
            if (info.peerType != CentralServerPeerType.MapServer)
                return false;
            peer = new MeshMapServerPeer { connectionId = info.connectionId, cellId = info.channelId, mapId = info.refId, address = info.networkAddress, port = info.networkPort };
            return true;
        }

        public void GetMapServers(List<MeshMapServerPeer> into)
        {
            foreach (CentralServerPeerInfo info in _server.MapServerPeers.Values)
            {
                if (ToPeer(info, out MeshMapServerPeer peer))
                    into.Add(peer);
            }
        }

        public bool TryGetMapServer(long connectionId, out MeshMapServerPeer peer)
        {
            peer = default;
            return _server.MapServerPeers.TryGetValue(connectionId, out CentralServerPeerInfo info) && ToPeer(info, out peer);
        }

        public void Send(long connectionId, MeshLayoutMessage message)
        {
            _server.SendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.Layout, writer => message.Serialize(writer));
        }

        public void Send(long connectionId, MeshRetireMessage message)
        {
            _server.SendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.Retire, writer => message.Serialize(writer));
        }

        public void Send(long connectionId, MeshCoordinatorNoteMessage message)
        {
            _server.SendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.CoordinatorNote, writer => message.Serialize(writer));
        }
    }
}
