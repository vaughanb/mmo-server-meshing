// Server Meshing add-on — rig: a client's connection with real latency on one machine (DESIGN.md 1.30).
//
// Everything so far was measured on localhost. With MESH_CLIENT_LATENCY_MS=<ms> every client transport the map manager
// builds holds what it sends and what it receives for that many milliseconds each way, so a round trip costs twice that:
// the delay sits at the client's edge, where a real one is, and the cell-to-cell links stay fast. Server members are
// forwarded untouched, so a cell that inherits the variable is not affected. Unset, empty or 0 changes nothing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshLatencyTransport wraps a transport and delays its client traffic by a fixed time each way. What is sent is
    /// held and handed to the inner transport when due, from the next ClientReceive or StopClient (the kit polls
    /// ClientReceive every update, so a send goes out within one update of its time). What is received is copied and
    /// handed out when due, in order. StopClient sends what is still held first, so a hop's last sends are not lost.
    /// </summary>
    public sealed class MeshLatencyTransport : ITransport, IMeshWrappedTransport
    {
        private readonly struct HeldSend
        {
            public readonly long due;
            public readonly byte channel;
            public readonly DeliveryMethod delivery;
            public readonly byte[] data;

            public HeldSend(long due, byte channel, DeliveryMethod delivery, byte[] data)
            {
                this.due = due;
                this.channel = channel;
                this.delivery = delivery;
                this.data = data;
            }
        }

        private readonly ITransport _inner;
        private readonly long _delayMs;
        private readonly Func<long> _nowMs;
        private readonly object _lock = new object();
        private readonly Queue<HeldSend> _sends = new Queue<HeldSend>();
        private readonly Queue<KeyValuePair<long, TransportEventData>> _received = new Queue<KeyValuePair<long, TransportEventData>>();
        private readonly NetDataWriter _writer = new NetDataWriter();

        /// <summary>MeshLatencyTransport delays <paramref name="inner"/>'s client traffic by <paramref name="delayMs"/> each way. <paramref name="nowMs"/> is the clock, a stopwatch's when null.</summary>
        public MeshLatencyTransport(ITransport inner, int delayMs, Func<long> nowMs = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner), "the latency transport needs a transport to wrap");
            if (delayMs < 0)
                throw new ArgumentOutOfRangeException(nameof(delayMs), delayMs, "the latency transport's delay cannot be negative");
            _delayMs = delayMs;
            if (nowMs == null)
            {
                Stopwatch clock = Stopwatch.StartNew();
                nowMs = () => clock.ElapsedMilliseconds;
            }
            _nowMs = nowMs;
        }

        /// <summary>Inner is the wrapped transport.</summary>
        public ITransport Inner => _inner;

        public int ServerPeersCount => _inner.ServerPeersCount;
        public int ServerMaxConnections => _inner.ServerMaxConnections;
        public bool IsClientStarted => _inner.IsClientStarted;
        public bool IsServerStarted => _inner.IsServerStarted;
        public bool HasImplementedPing => _inner.HasImplementedPing;
        public bool IsReliableOnly => _inner.IsReliableOnly;

        public bool StartClient(string address, int port) => _inner.StartClient(address, port);

        /// <summary>ClientSend copies the message and holds it for the delay. False when the client is not started, as the inner transport answers.</summary>
        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            if (!_inner.IsClientStarted)
                return false;
            lock (_lock)
                _sends.Enqueue(new HeldSend(_nowMs() + _delayMs, dataChannel, deliveryMethod, writer.CopyData()));
            return true;
        }

        /// <summary>ClientReceive sends what is due, takes everything the inner transport has, and hands out the oldest event when it is due.</summary>
        public bool ClientReceive(out TransportEventData eventData)
        {
            long now = _nowMs();
            lock (_lock)
                FlushSends(now);
            while (_inner.ClientReceive(out TransportEventData incoming))
            {
                if (incoming.type == ENetworkEvent.DataEvent && incoming.reader != null)
                    incoming.reader = new NetDataReader(incoming.reader.GetRemainingBytes()); // the inner reader is reused by the next poll
                _received.Enqueue(new KeyValuePair<long, TransportEventData>(now + _delayMs, incoming));
            }
            if (_received.Count > 0 && _received.Peek().Key <= now)
            {
                eventData = _received.Dequeue().Value;
                return true;
            }
            eventData = default;
            return false;
        }

        /// <summary>StopClient sends what is still held, drops what was not yet handed out, and stops the inner client.</summary>
        public void StopClient()
        {
            lock (_lock)
                FlushSends(long.MaxValue);
            _received.Clear();
            _inner.StopClient();
        }

        private void FlushSends(long now)
        {
            while (_sends.Count > 0 && _sends.Peek().due <= now)
            {
                HeldSend send = _sends.Dequeue();
                _writer.Reset();
                _writer.Put(send.data);
                _inner.ClientSend(send.channel, send.delivery, _writer);
            }
        }

        public bool StartServer(int port, int maxConnections) => _inner.StartServer(port, maxConnections);
        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => _inner.ServerSend(connectionId, dataChannel, deliveryMethod, writer);
        public bool ServerReceive(out TransportEventData eventData) => _inner.ServerReceive(out eventData);
        public bool ServerDisconnect(long connectionId) => _inner.ServerDisconnect(connectionId);
        public void StopServer() => _inner.StopServer();
        public void Destroy() => _inner.Destroy();

        /// <summary>GetClientRtt is the inner transport's plus the delay both ways.</summary>
        public long GetClientRtt() => _inner.GetClientRtt() + _delayMs * 2;

        public long GetServerRtt(long connectionId) => _inner.GetServerRtt(connectionId);
    }

    /// <summary>MeshLatencyTransportFactory builds a <see cref="MeshLatencyTransport"/> around each transport of <see cref="Inner"/>.</summary>
    public class MeshLatencyTransportFactory : BaseTransportFactory
    {
        /// <summary>EnvironmentKey is the one-way client delay in milliseconds.</summary>
        public const string EnvironmentKey = "MESH_CLIENT_LATENCY_MS";

        /// <summary>Inner is the factory whose transports are wrapped; when null, Build adds the kit's default factory.</summary>
        public BaseTransportFactory Inner;

        /// <summary>DelayMs is the delay each way.</summary>
        public int DelayMs;

        /// <summary>TryParseSetting reads the variable: true with a delay above 0 when it asks for one.</summary>
        public static bool TryParseSetting(string value, out int delayMs)
        {
            delayMs = 0;
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out delayMs) && delayMs > 0;
        }

        public override ITransport Build()
        {
            if (Inner == this)
                throw new InvalidOperationException($"the latency transport factory under {gameObject.name} is its own inner factory");
            if (Inner == null)
                Inner = MeshMeteredTransportFactory.KitDefault(gameObject);
            return new MeshLatencyTransport(Inner.Build(), DelayMs);
        }
    }
}
