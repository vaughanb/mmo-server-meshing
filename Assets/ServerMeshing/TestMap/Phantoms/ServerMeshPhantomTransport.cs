// Server Meshing add-on — rig phantom transport: synthetic client connections inside a map server, whose downstream is counted and swallowed, plus the sink's hello.
using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomTransport sits between the metered transport (when there is one) and the kit's LiteNetLib transport.
    /// It forwards everything for real peers. For phantoms (ids <see cref="IdBase"/> + k, k never reused) it queues synthetic
    /// connect, data and disconnect events, delivered after the inner events of the same ServerReceive drain, and swallows
    /// every send to a bound phantom (counted per delivery and per phantom, no allocation), mirroring LiteNetLib's too-big
    /// rule. It also consumes the sink's hello (message <see cref="HelloMessageType"/>, an int32 index) from real peers and
    /// queues it for the binder. Everything runs on the main thread, as the kit's server loop does.
    /// </summary>
    public sealed class ServerMeshPhantomTransport : ITransport, IMeshWrappedTransport
    {
        /// <summary>IdBase is the first phantom connection id; LiteNetLib's peer ids are small and recycled, far below it.</summary>
        public const long IdBase = 1_000_000;
        /// <summary>HelloMessageType is the sink peer's hello: [packed ushort 65000][int32 index], ReliableOrdered on channel 0.</summary>
        public const ushort HelloMessageType = 65000;
        /// <summary>BindReplyMessageType is the binder's reply to a hello, sent through the manager.</summary>
        public const ushort BindReplyMessageType = 65001;
        /// <summary>DeliverySlots is the number of swallowed counters: one per LiteNetLib delivery method, then other.</summary>
        public const int DeliverySlots = 6;
        /// <summary>HelloBytes is the hello's length: three bytes of packed type and four of index.</summary>
        public const int HelloBytes = 7;

        private const int Pending = -1;
        private const int Leaving = -2;

        private struct PendingPhantomEvent
        {
            public ENetworkEvent type;
            public long connectionId;
            public NetDataWriter frame;
            public DisconnectReason reason;
        }

        private readonly ITransport _inner;
        private readonly IMeshLog _log;
        private readonly Dictionary<long, int> _phantoms = new Dictionary<long, int>();
        private readonly Queue<PendingPhantomEvent> _events = new Queue<PendingPhantomEvent>();
        private readonly Stack<NetDataWriter> _framePool = new Stack<NetDataWriter>();
        private readonly Stack<int> _freeSlots = new Stack<int>();
        private readonly Queue<long> _helloConnections = new Queue<long>();
        private readonly Queue<int> _helloIndices = new Queue<int>();
        private readonly NetDataReader _reader = new NetDataReader();
        private readonly long[] _swallowedCalls = new long[DeliverySlots];
        private readonly long[] _swallowedBytes = new long[DeliverySlots];
        private long[] _slotBytes = new long[64];
        private int _slotsUsed;
        private int _bound;
        private NetDataWriter _inFlight;
        private bool _hasDeferred;
        private TransportEventData _deferred;

        /// <summary>ServerMeshPhantomTransport wraps inner, logs through log and numbers its phantoms from firstK.</summary>
        public ServerMeshPhantomTransport(ITransport inner, IMeshLog log, long firstK)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            if (firstK < 0)
                throw new ArgumentOutOfRangeException(nameof(firstK), firstK, "phantom numbers start at 0");
            NextK = firstK;
        }

        /// <summary>Inner is the transport real peers go through.</summary>
        public ITransport Inner => _inner;
        /// <summary>NextK is the number the next phantom gets; a later transport of the same factory continues from it.</summary>
        public long NextK { get; private set; }
        /// <summary>BoundCount is how many phantoms have had their connect delivered and are not leaving.</summary>
        public int BoundCount => _bound;
        /// <summary>PhantomCount is how many phantoms are queued, bound or leaving.</summary>
        public int PhantomCount => _phantoms.Count;

        /// <summary>ServerPeersCount is the inner peers plus the bound phantoms.</summary>
        public int ServerPeersCount => _inner.ServerPeersCount + _bound;
        /// <summary>ServerMaxConnections is forwarded.</summary>
        public int ServerMaxConnections => _inner.ServerMaxConnections;
        /// <summary>IsClientStarted is forwarded.</summary>
        public bool IsClientStarted => _inner.IsClientStarted;
        /// <summary>IsServerStarted is forwarded.</summary>
        public bool IsServerStarted => _inner.IsServerStarted;
        /// <summary>HasImplementedPing is forwarded.</summary>
        public bool HasImplementedPing => _inner.HasImplementedPing;
        /// <summary>IsReliableOnly is forwarded.</summary>
        public bool IsReliableOnly => _inner.IsReliableOnly;

        /// <summary>ViewSampler, when set (by the binder, for virtual phantoms), reads the swallowed sends of its sampled phantoms.</summary>
        public ServerMeshPhantomViewSampler ViewSampler { get; set; }

        /// <summary>IsPhantom is true for a bound phantom.</summary>
        public bool IsPhantom(long connectionId) => _phantoms.TryGetValue(connectionId, out int slot) && slot >= 0;

        /// <summary>SwallowedCalls is the sends swallowed for a delivery slot (the DeliveryMethod value, or 5 for other).</summary>
        public long SwallowedCalls(int deliverySlot) => _swallowedCalls[deliverySlot];

        /// <summary>SwallowedBytes is the payload bytes (writer length) swallowed for a delivery slot.</summary>
        public long SwallowedBytes(int deliverySlot) => _swallowedBytes[deliverySlot];

        /// <summary>PayloadBytesOf is the payload bytes swallowed for a bound phantom since it was bound, else 0.</summary>
        public long PayloadBytesOf(long connectionId) => _phantoms.TryGetValue(connectionId, out int slot) && slot >= 0 ? _slotBytes[slot] : 0;

        /// <summary>DeliverySlotOf is the counter slot of a delivery: its enum value, or 5 for an undefined one.</summary>
        public static int DeliverySlotOf(DeliveryMethod deliveryMethod)
        {
            int value = (int)deliveryMethod;
            return value >= 0 && value < DeliverySlots - 1 ? value : DeliverySlots - 1;
        }

        /// <summary>TooBigLimit is the largest writer LiteNetLib sends without throwing for a delivery (MTU less its header), or int.MaxValue for the reliable ones that fragment.</summary>
        public static int TooBigLimit(DeliveryMethod deliveryMethod)
        {
            switch (deliveryMethod)
            {
                case DeliveryMethod.Unreliable:
                    return NetConstants.InitialMtu - NetConstants.HeaderSize;
                case DeliveryMethod.Sequenced:
                case DeliveryMethod.ReliableSequenced:
                    return NetConstants.InitialMtu - NetConstants.ChanneledHeaderSize;
                default:
                    return int.MaxValue;
            }
        }

        /// <summary>
        /// TryQueueConnect queues a new phantom's connect and gives its id; false when the server is not started or inner
        /// peers plus phantoms already reach the server's connection limit.
        /// </summary>
        public bool TryQueueConnect(out long connectionId)
        {
            connectionId = -1;
            if (!_inner.IsServerStarted || _inner.ServerPeersCount + _phantoms.Count >= _inner.ServerMaxConnections)
                return false;
            connectionId = IdBase + NextK;
            ++NextK;
            _phantoms.Add(connectionId, Pending);
            _events.Enqueue(new PendingPhantomEvent { type = ENetworkEvent.ConnectEvent, connectionId = connectionId });
            return true;
        }

        /// <summary>QueueUpload copies frame into a pooled writer and queues it as a data event from a bound phantom; false (nothing queued) otherwise.</summary>
        public bool QueueUpload(long connectionId, NetDataWriter frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (!IsPhantom(connectionId))
                return false;
            NetDataWriter copy = _framePool.Count > 0 ? _framePool.Pop() : new NetDataWriter(true, 64);
            copy.Reset();
            copy.Put(frame.Data, 0, frame.Length);
            _events.Enqueue(new PendingPhantomEvent { type = ENetworkEvent.DataEvent, connectionId = connectionId, frame = copy });
            return true;
        }

        /// <summary>QueueDisconnect unbinds a queued or bound phantom and queues its one disconnect (RemoteConnectionClose); false for any other id.</summary>
        public bool QueueDisconnect(long connectionId) => Leave(connectionId, DisconnectReason.RemoteConnectionClose);

        /// <summary>TryDequeueHello gives the next sink hello: the real connection that sent it and the index it asked for.</summary>
        public bool TryDequeueHello(out long connectionId, out int index)
        {
            if (_helloConnections.Count == 0)
            {
                connectionId = -1;
                index = -1;
                return false;
            }
            connectionId = _helloConnections.Dequeue();
            index = _helloIndices.Dequeue();
            return true;
        }

        /// <summary>
        /// ServerReceive returns the previous synthetic frame to the pool, then gives inner events first (consuming sink
        /// hellos; an inner connect that carries a bound phantom's id is preceded by that phantom's disconnect, logged as an
        /// error), then the synthetic events in the order they were queued.
        /// </summary>
        public bool ServerReceive(out TransportEventData eventData)
        {
            if (_inFlight != null)
            {
                _framePool.Push(_inFlight);
                _inFlight = null;
                _reader.Clear();
            }
            if (_hasDeferred)
            {
                _hasDeferred = false;
                eventData = _deferred;
                _deferred = default;
                return true;
            }
            while (_inner.ServerReceive(out eventData))
            {
                if (eventData.type == ENetworkEvent.DataEvent && TryTakeHello(eventData.connectionId, eventData.reader))
                    continue;
                if (eventData.type == ENetworkEvent.ConnectEvent && _phantoms.ContainsKey(eventData.connectionId))
                {
                    _log.Error(FormattableString.Invariant($"Phantoms error: an inner connection carries phantom id {eventData.connectionId}; the phantom is disconnected first"));
                    _deferred = eventData;
                    _hasDeferred = true;
                    long id = eventData.connectionId;
                    Forget(id);
                    eventData = new TransportEventData { type = ENetworkEvent.DisconnectEvent, connectionId = id, disconnectInfo = new DisconnectInfo { Reason = DisconnectReason.RemoteConnectionClose } };
                    return true;
                }
                return true;
            }
            while (_events.Count > 0)
            {
                PendingPhantomEvent next = _events.Dequeue();
                switch (next.type)
                {
                    case ENetworkEvent.ConnectEvent:
                        if (!_phantoms.TryGetValue(next.connectionId, out int state))
                            continue;
                        if (state == Pending)
                            Bind(next.connectionId);
                        eventData = new TransportEventData { type = ENetworkEvent.ConnectEvent, connectionId = next.connectionId };
                        return true;
                    case ENetworkEvent.DataEvent:
                        if (!IsPhantom(next.connectionId))
                        {
                            _framePool.Push(next.frame);
                            continue;
                        }
                        _inFlight = next.frame;
                        _reader.SetSource(next.frame);
                        eventData = new TransportEventData { type = ENetworkEvent.DataEvent, connectionId = next.connectionId, reader = _reader };
                        return true;
                    default:
                        if (!_phantoms.TryGetValue(next.connectionId, out int leaving) || leaving != Leaving)
                            continue;
                        _phantoms.Remove(next.connectionId);
                        eventData = new TransportEventData { type = ENetworkEvent.DisconnectEvent, connectionId = next.connectionId, disconnectInfo = new DisconnectInfo { Reason = next.reason } };
                        return true;
                }
            }
            eventData = default;
            return false;
        }

        /// <summary>
        /// ServerSend swallows a send to a bound phantom (counted, true; a writer LiteNetLib would refuse throws
        /// TooBigPacketException and is not counted), returns false for any other phantom-range id, and forwards the rest.
        /// </summary>
        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            if (_phantoms.TryGetValue(connectionId, out int slot))
            {
                if (slot < 0)
                    return false;
                int length = writer.Length;
                if (length > TooBigLimit(deliveryMethod))
                    throw new TooBigPacketException("phantom send of " + length + " bytes is above the " + TooBigLimit(deliveryMethod) + " byte limit of " + deliveryMethod);
                int deliverySlot = DeliverySlotOf(deliveryMethod);
                ++_swallowedCalls[deliverySlot];
                _swallowedBytes[deliverySlot] += length;
                _slotBytes[slot] += length;
                ServerMeshPhantomViewSampler sampler = ViewSampler;
                if (sampler != null && sampler.IsSampled(connectionId))
                    sampler.OnSwallowed(connectionId, writer.Data, length, UnityEngine.Time.frameCount);
                return true;
            }
            if (connectionId >= IdBase)
                return false;
            return _inner.ServerSend(connectionId, dataChannel, deliveryMethod, writer);
        }

        /// <summary>ServerDisconnect unbinds a phantom and queues its one disconnect (DisconnectPeerCalled); false for an unknown phantom-range id; real peers are forwarded.</summary>
        public bool ServerDisconnect(long connectionId)
        {
            if (connectionId >= IdBase || _phantoms.ContainsKey(connectionId))
                return Leave(connectionId, DisconnectReason.DisconnectPeerCalled);
            return _inner.ServerDisconnect(connectionId);
        }

        /// <summary>GetServerRtt is 0 for a phantom-range id and forwarded otherwise.</summary>
        public long GetServerRtt(long connectionId) => connectionId >= IdBase || _phantoms.ContainsKey(connectionId) ? 0 : _inner.GetServerRtt(connectionId);

        /// <summary>StartServer is forwarded.</summary>
        public bool StartServer(int port, int maxConnections) => _inner.StartServer(port, maxConnections);

        /// <summary>StopServer drops every phantom, queued event and hello without emitting anything, as LiteNetLib does, then stops the inner server; phantom numbers continue.</summary>
        public void StopServer()
        {
            Clear();
            _inner.StopServer();
        }

        /// <summary>StartClient is forwarded.</summary>
        public bool StartClient(string address, int port) => _inner.StartClient(address, port);
        /// <summary>ClientSend is forwarded.</summary>
        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => _inner.ClientSend(dataChannel, deliveryMethod, writer);
        /// <summary>ClientReceive is forwarded.</summary>
        public bool ClientReceive(out TransportEventData eventData) => _inner.ClientReceive(out eventData);
        /// <summary>StopClient is forwarded.</summary>
        public void StopClient() => _inner.StopClient();
        /// <summary>GetClientRtt is forwarded.</summary>
        public long GetClientRtt() => _inner.GetClientRtt();

        /// <summary>Destroy drops every phantom, then destroys the inner transport.</summary>
        public void Destroy()
        {
            Clear();
            _inner.Destroy();
        }

        private bool TryTakeHello(long connectionId, NetDataReader reader)
        {
            if (reader == null || reader.RawData == null)
                return false;
            byte[] data = reader.RawData;
            int at = reader.Position;
            int end = Math.Min(reader.RawDataSize, data.Length);
            if (end - at < HelloBytes || data[at] != 249 || data[at + 1] != 244 || data[at + 2] != 248)
                return false;
            int index = data[at + 3] | (data[at + 4] << 8) | (data[at + 5] << 16) | (data[at + 6] << 24);
            _helloConnections.Enqueue(connectionId);
            _helloIndices.Enqueue(index);
            return true;
        }

        private bool Leave(long connectionId, DisconnectReason reason)
        {
            if (!_phantoms.TryGetValue(connectionId, out int slot) || slot == Leaving)
                return false;
            if (slot >= 0)
                Unbind(slot);
            _phantoms[connectionId] = Leaving;
            _events.Enqueue(new PendingPhantomEvent { type = ENetworkEvent.DisconnectEvent, connectionId = connectionId, reason = reason });
            return true;
        }

        private void Bind(long connectionId)
        {
            int slot;
            if (_freeSlots.Count > 0)
                slot = _freeSlots.Pop();
            else
            {
                if (_slotsUsed == _slotBytes.Length)
                    Array.Resize(ref _slotBytes, _slotBytes.Length * 2);
                slot = _slotsUsed++;
            }
            _slotBytes[slot] = 0;
            _phantoms[connectionId] = slot;
            ++_bound;
        }

        private void Unbind(int slot)
        {
            _freeSlots.Push(slot);
            --_bound;
        }

        private void Forget(long connectionId)
        {
            if (_phantoms.TryGetValue(connectionId, out int slot) && slot >= 0)
                Unbind(slot);
            _phantoms.Remove(connectionId);
        }

        private void Clear()
        {
            while (_events.Count > 0)
            {
                PendingPhantomEvent dropped = _events.Dequeue();
                if (dropped.frame != null)
                    _framePool.Push(dropped.frame);
            }
            if (_inFlight != null)
            {
                _framePool.Push(_inFlight);
                _inFlight = null;
                _reader.Clear();
            }
            _phantoms.Clear();
            _freeSlots.Clear();
            _slotsUsed = 0;
            _bound = 0;
            _helloConnections.Clear();
            _helloIndices.Clear();
            _hasDeferred = false;
            _deferred = default;
        }
    }
}
