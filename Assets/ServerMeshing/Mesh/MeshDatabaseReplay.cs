// Server Meshing add-on — database requests stranded by a dropped connection are sent again when that is safe (DESIGN.md 0.89).
//
// The kit fails every flow on the first failed database request, and a request that was on its way when the database
// connection dropped can never be answered on that connection: since 0.87 the reconnect answers it with a timeout at
// once (MeshMapNetworkManager_Database), so a central restart that lands while a cell admits players -- a merge's drain
// handing them over -- failed their access-token check, their character load or their relates data, and the kit refused
// or kicked them (splitcentral_merge 3/3 failed, 2026-10-21). Here the database client's transport is wrapped and keeps
// the packet of a request until its answer when sending it again is safe: a READ (asking twice changes nothing), or any
// request the transport refused to send (the connection was down or still connecting: it never left this process, so
// its first delivery cannot be its second). At the reconnect such a request keeps its callback (its original request id
// and its kit 30 s timer), and its packet is sent once the new connection is up, so the caller gets the database's
// answer instead of a timeout. A write that did go out (save, gold change, mail...) may have been applied before the
// drop: it is never sent twice and still ends in a timeout, as in 0.87.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshDatabaseReplay: which database requests may be sent twice, and how a request packet starts.</summary>
    public static class MeshDatabaseReplay
    {
        private static HashSet<ushort> s_readTypes;

        /// <summary>A request type named Get*, Find*, Validate* or MailList only reads, so asking it twice changes nothing.</summary>
        public static bool IsReadName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.StartsWith("Get", System.StringComparison.Ordinal) || name.StartsWith("Find", System.StringComparison.Ordinal)
                || name.StartsWith("Validate", System.StringComparison.Ordinal) || name == "MailList";
        }

        /// <summary>The kit's read request types (DatabaseRequestTypes constants with a read name), found once by reflection.</summary>
        public static bool IsRead(ushort requestType)
        {
            if (s_readTypes == null)
            {
                HashSet<ushort> reads = new HashSet<ushort>();
                foreach (System.Reflection.FieldInfo field in typeof(DatabaseRequestTypes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (field.IsLiteral && field.FieldType == typeof(ushort) && IsReadName(field.Name))
                        reads.Add((ushort)field.GetRawConstantValue());
                }
                s_readTypes = reads;
            }
            return s_readTypes.Contains(requestType);
        }

        /// <summary>A request packet starts with the packed request message type, the packed request type and the packed request id (TransportHandler.CreateAndWriteRequest).</summary>
        public static bool TryReadRequestHeader(byte[] data, int length, ushort requestMessageType, out ushort requestType, out uint requestId)
        {
            requestType = 0;
            requestId = 0;
            if (data == null || length <= 0)
                return false;
            try
            {
                NetDataReader reader = new NetDataReader(data, 0, length);
                if (reader.GetPackedUShort() != requestMessageType)
                    return false;
                requestType = reader.GetPackedUShort();
                requestId = reader.GetPackedUInt();
                return true;
            }
            catch (System.Exception)
            {
                return false;   // not a request packet this reader understands; it is simply not kept
            }
        }
    }

    /// <summary>MeshDatabaseReplayBook keeps the packets of read requests still waiting for their answer, by request id.</summary>
    public sealed class MeshDatabaseReplayBook
    {
        public struct Packet
        {
            public byte channel;
            public DeliveryMethod deliveryMethod;
            public ushort requestType;
            public byte[] bytes;
            public bool delivered;   // the transport took it (a write is then never kept)
        }

        private readonly Dictionary<uint, Packet> _packets = new Dictionary<uint, Packet>();
        private readonly object _lock = new object();

        public int Count { get { lock (_lock) return _packets.Count; } }

        /// <summary>
        /// Keeps a copy of a request packet the transport refused (delivered false: it never left), or of a read; a
        /// delivered write is not kept, and an earlier copy of it (a refused write now sent) is forgotten.
        /// </summary>
        public void Record(byte channel, DeliveryMethod deliveryMethod, NetDataWriter writer, ushort requestMessageType, bool delivered)
        {
            if (writer == null || !MeshDatabaseReplay.TryReadRequestHeader(writer.Data, writer.Length, requestMessageType, out ushort requestType, out uint requestId))
                return;
            if (delivered && !MeshDatabaseReplay.IsRead(requestType))
            {
                lock (_lock) _packets.Remove(requestId);
                return;
            }
            Packet packet = new Packet { channel = channel, deliveryMethod = deliveryMethod, requestType = requestType, bytes = writer.CopyData(), delivered = delivered };
            lock (_lock) _packets[requestId] = packet;
        }

        /// <summary>The ids of the kept requests the transport refused: they wait for the connection to come up.</summary>
        public List<uint> Undelivered()
        {
            List<uint> ids = new List<uint>();
            lock (_lock)
            {
                foreach (KeyValuePair<uint, Packet> kv in _packets)
                {
                    if (!kv.Value.delivered)
                        ids.Add(kv.Key);
                }
            }
            return ids;
        }

        public bool TryGet(uint requestId, out Packet packet)
        {
            lock (_lock) return _packets.TryGetValue(requestId, out packet);
        }

        /// <summary>Forgets every packet whose request is no longer waiting (answered, or timed out).</summary>
        public void Prune(System.Func<uint, bool> waiting)
        {
            lock (_lock)
            {
                List<uint> gone = null;
                foreach (uint requestId in _packets.Keys)
                {
                    if (!waiting(requestId))
                        (gone ??= new List<uint>()).Add(requestId);
                }
                if (gone != null)
                    foreach (uint requestId in gone)
                        _packets.Remove(requestId);
            }
        }
    }

    /// <summary>MeshReplayingTransport is the database client's transport, recording request packets on their way out (everything forwards).</summary>
    public sealed class MeshReplayingTransport : ITransport
    {
        private readonly TransportHandler _handler;
        private readonly MeshDatabaseReplayBook _book;

        public ITransport Inner { get; }

        public MeshReplayingTransport(ITransport inner, TransportHandler handler, MeshDatabaseReplayBook book)
        {
            Inner = inner;
            _handler = handler;
            _book = book;
        }

        public int ServerPeersCount => Inner.ServerPeersCount;
        public int ServerMaxConnections => Inner.ServerMaxConnections;
        public bool IsClientStarted => Inner.IsClientStarted;
        public bool IsServerStarted => Inner.IsServerStarted;
        public bool HasImplementedPing => Inner.HasImplementedPing;
        public bool IsReliableOnly => Inner.IsReliableOnly;
        public bool StartClient(string address, int port) => Inner.StartClient(address, port);

        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            bool delivered = Inner.ClientSend(dataChannel, deliveryMethod, writer);
            _book.Record(dataChannel, deliveryMethod, writer, _handler.RequestMessageType, delivered);
            return delivered;
        }

        public bool ClientReceive(out TransportEventData eventData) => Inner.ClientReceive(out eventData);
        public void StopClient() => Inner.StopClient();
        public bool StartServer(int port, int maxConnections) => Inner.StartServer(port, maxConnections);
        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => Inner.ServerSend(connectionId, dataChannel, deliveryMethod, writer);
        public bool ServerReceive(out TransportEventData eventData) => Inner.ServerReceive(out eventData);
        public bool ServerDisconnect(long connectionId) => Inner.ServerDisconnect(connectionId);
        public void StopServer() => Inner.StopServer();
        public void Destroy() => Inner.Destroy();
        public long GetClientRtt() => Inner.GetClientRtt();
        public long GetServerRtt(long connectionId) => Inner.GetServerRtt(connectionId);
    }
}
