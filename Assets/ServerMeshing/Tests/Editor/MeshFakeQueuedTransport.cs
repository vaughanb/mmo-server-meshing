using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    internal sealed class MeshFakeQueuedTransport : ITransport
    {
        public readonly Queue<TransportEventData> serverEvents = new Queue<TransportEventData>();
        public bool isServerStarted = true;
        public int serverPeersCount;
        public int serverMaxConnections = 1100;
        public bool sendResult = true;
        public int serverSendCalls;
        public int serverDisconnectCalls;
        public int serverRttCalls;
        public int stopServerCalls;
        public int destroyCalls;

        public int ServerPeersCount => serverPeersCount;
        public int ServerMaxConnections => serverMaxConnections;
        public bool IsClientStarted => false;
        public bool IsServerStarted => isServerStarted;
        public bool HasImplementedPing => true;
        public bool IsReliableOnly => false;

        public bool StartClient(string address, int port) => false;
        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => false;

        public bool ClientReceive(out TransportEventData eventData)
        {
            eventData = default;
            return false;
        }

        public void StopClient()
        {
        }

        public bool StartServer(int port, int maxConnections)
        {
            isServerStarted = true;
            return true;
        }

        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            serverSendCalls++;
            return sendResult;
        }

        public bool ServerReceive(out TransportEventData eventData)
        {
            if (serverEvents.Count == 0)
            {
                eventData = default;
                return false;
            }
            eventData = serverEvents.Dequeue();
            return true;
        }

        public bool ServerDisconnect(long connectionId)
        {
            serverDisconnectCalls++;
            return true;
        }

        public void StopServer()
        {
            stopServerCalls++;
            isServerStarted = false;
        }

        public void Destroy() => destroyCalls++;

        public long GetClientRtt() => 0;

        public long GetServerRtt(long connectionId)
        {
            serverRttCalls++;
            return 33;
        }

        public void EnqueueData(long connectionId, NetDataReader reader)
        {
            serverEvents.Enqueue(new TransportEventData { type = ENetworkEvent.DataEvent, connectionId = connectionId, reader = reader });
        }

        public void EnqueueConnect(long connectionId)
        {
            serverEvents.Enqueue(new TransportEventData { type = ENetworkEvent.ConnectEvent, connectionId = connectionId });
        }
    }
}
