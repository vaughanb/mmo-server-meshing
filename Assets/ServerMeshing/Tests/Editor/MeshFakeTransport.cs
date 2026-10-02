using System;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    internal sealed class MeshFakeTransport : ITransport, IMeshWrappedTransport
    {
        public int serverPeersCount = 7;
        public int serverMaxConnections = 1100;
        public bool isClientStarted = true;
        public bool isServerStarted = true;
        public bool hasImplementedPing = true;
        public bool isReliableOnly = true;
        public bool result = true;
        public long clientRtt = 11;
        public long serverRtt = 22;
        public TransportEventData nextEvent;
        public Exception serverSendThrows;

        public string lastMember;
        public int calls;
        public int serverSendCalls;
        public string lastAddress;
        public int lastPort;
        public int lastMaxConnections;
        public long lastConnectionId;
        public byte lastChannel;
        public DeliveryMethod lastDelivery;
        public NetDataWriter lastWriter;

        public ITransport Inner { get; set; }

        public int ServerPeersCount { get { Note(nameof(ServerPeersCount)); return serverPeersCount; } }
        public int ServerMaxConnections { get { Note(nameof(ServerMaxConnections)); return serverMaxConnections; } }
        public bool IsClientStarted { get { Note(nameof(IsClientStarted)); return isClientStarted; } }
        public bool IsServerStarted { get { Note(nameof(IsServerStarted)); return isServerStarted; } }
        public bool HasImplementedPing { get { Note(nameof(HasImplementedPing)); return hasImplementedPing; } }
        public bool IsReliableOnly { get { Note(nameof(IsReliableOnly)); return isReliableOnly; } }

        public bool StartClient(string address, int port)
        {
            Note(nameof(StartClient));
            lastAddress = address;
            lastPort = port;
            return result;
        }

        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Note(nameof(ClientSend));
            lastChannel = dataChannel;
            lastDelivery = deliveryMethod;
            lastWriter = writer;
            return result;
        }

        public bool ClientReceive(out TransportEventData eventData)
        {
            Note(nameof(ClientReceive));
            eventData = nextEvent;
            return result;
        }

        public void StopClient() => Note(nameof(StopClient));

        public bool StartServer(int port, int maxConnections)
        {
            Note(nameof(StartServer));
            lastPort = port;
            lastMaxConnections = maxConnections;
            return result;
        }

        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Note(nameof(ServerSend));
            serverSendCalls++;
            lastConnectionId = connectionId;
            lastChannel = dataChannel;
            lastDelivery = deliveryMethod;
            lastWriter = writer;
            if (serverSendThrows != null)
                throw serverSendThrows;
            return result;
        }

        public bool ServerReceive(out TransportEventData eventData)
        {
            Note(nameof(ServerReceive));
            eventData = nextEvent;
            return result;
        }

        public bool ServerDisconnect(long connectionId)
        {
            Note(nameof(ServerDisconnect));
            lastConnectionId = connectionId;
            return result;
        }

        public void StopServer() => Note(nameof(StopServer));

        public void Destroy() => Note(nameof(Destroy));

        public long GetClientRtt()
        {
            Note(nameof(GetClientRtt));
            return clientRtt;
        }

        public long GetServerRtt(long connectionId)
        {
            Note(nameof(GetServerRtt));
            lastConnectionId = connectionId;
            return serverRtt;
        }

        private void Note(string member)
        {
            lastMember = member;
            calls++;
        }
    }
}
