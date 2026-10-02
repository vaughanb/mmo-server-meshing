// Server Meshing add-on — the outermost server transport when MESH_METER_SENDS is set: it forwards every member to the
// transport it wraps and records each server send in a MeshSendMeter. Client members are forwarded and never metered.
using System;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshMeteredTransport wraps a transport and meters its ServerSend: the message type is read from the writer's first
    /// bytes, and the send is recorded once it returns (refused when it returned false). A TooBigPacketException from the
    /// inner transport is recorded as too big and rethrown as the same instance, so the kit's own handling still runs.
    /// Every other member is forwarded unchanged.
    /// </summary>
    public sealed class MeshMeteredTransport : ITransport, IMeshWrappedTransport
    {
        private readonly ITransport _inner;
        private readonly MeshSendMeter _meter;
        private readonly bool _timed;

        /// <summary>
        /// MeshMeteredTransport wraps <paramref name="inner"/> and records its server sends in <paramref name="meter"/>.
        /// <paramref name="timed"/> also times each send inside the inner transport. Null arguments throw, naming the argument.
        /// </summary>
        public MeshMeteredTransport(ITransport inner, MeshSendMeter meter, bool timed)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner), "the metered transport needs a transport to wrap");
            _meter = meter ?? throw new ArgumentNullException(nameof(meter), "the metered transport needs a send meter");
            _timed = timed;
        }

        /// <summary>Inner is the wrapped transport.</summary>
        public ITransport Inner => _inner;

        /// <summary>Meter is the send meter this transport records into.</summary>
        public MeshSendMeter Meter => _meter;

        /// <summary>Timed is true when each server send is also timed inside the inner transport.</summary>
        public bool Timed => _timed;

        /// <summary>ServerPeersCount forwards to the inner transport.</summary>
        public int ServerPeersCount => _inner.ServerPeersCount;

        /// <summary>ServerMaxConnections forwards to the inner transport.</summary>
        public int ServerMaxConnections => _inner.ServerMaxConnections;

        /// <summary>IsClientStarted forwards to the inner transport.</summary>
        public bool IsClientStarted => _inner.IsClientStarted;

        /// <summary>IsServerStarted forwards to the inner transport.</summary>
        public bool IsServerStarted => _inner.IsServerStarted;

        /// <summary>HasImplementedPing forwards to the inner transport.</summary>
        public bool HasImplementedPing => _inner.HasImplementedPing;

        /// <summary>IsReliableOnly forwards to the inner transport.</summary>
        public bool IsReliableOnly => _inner.IsReliableOnly;

        /// <summary>StartClient forwards to the inner transport.</summary>
        public bool StartClient(string address, int port) => _inner.StartClient(address, port);

        /// <summary>ClientSend forwards to the inner transport, unmetered.</summary>
        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => _inner.ClientSend(dataChannel, deliveryMethod, writer);

        /// <summary>ClientReceive forwards to the inner transport.</summary>
        public bool ClientReceive(out TransportEventData eventData) => _inner.ClientReceive(out eventData);

        /// <summary>StopClient forwards to the inner transport.</summary>
        public void StopClient() => _inner.StopClient();

        /// <summary>StartServer forwards to the inner transport.</summary>
        public bool StartServer(int port, int maxConnections) => _inner.StartServer(port, maxConnections);

        /// <summary>
        /// ServerSend forwards to the inner transport and records the send: under its packed message type, or as unknown
        /// when the writer holds none. It allocates nothing.
        /// </summary>
        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            int bytes = writer != null ? writer.Length : 0;
            ushort type = 0;
            bool known = writer != null && MeshSendMeter.TryReadMessageType(writer.Data, 0, bytes, out type, out _);
            long start = _timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            bool sent;
            try
            {
                sent = _inner.ServerSend(connectionId, dataChannel, deliveryMethod, writer);
            }
            catch (TooBigPacketException)
            {
                _meter.RecordTooBig(type, deliveryMethod, bytes);
                throw;
            }
            long elapsed = _timed ? System.Diagnostics.Stopwatch.GetTimestamp() - start : 0L;
            if (known)
                _meter.Record(type, deliveryMethod, dataChannel, bytes, elapsed, !sent);
            else
                _meter.RecordUnknown(deliveryMethod, dataChannel, bytes, elapsed, !sent);
            return sent;
        }

        /// <summary>ServerReceive forwards to the inner transport.</summary>
        public bool ServerReceive(out TransportEventData eventData) => _inner.ServerReceive(out eventData);

        /// <summary>ServerDisconnect forwards to the inner transport.</summary>
        public bool ServerDisconnect(long connectionId) => _inner.ServerDisconnect(connectionId);

        /// <summary>StopServer forwards to the inner transport.</summary>
        public void StopServer() => _inner.StopServer();

        /// <summary>Destroy forwards to the inner transport.</summary>
        public void Destroy() => _inner.Destroy();

        /// <summary>GetClientRtt forwards to the inner transport.</summary>
        public long GetClientRtt() => _inner.GetClientRtt();

        /// <summary>GetServerRtt forwards to the inner transport.</summary>
        public long GetServerRtt(long connectionId) => _inner.GetServerRtt(connectionId);
    }
}
