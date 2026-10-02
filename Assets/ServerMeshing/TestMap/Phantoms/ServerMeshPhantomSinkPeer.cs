// Server Meshing add-on — rig phantom sink, worker side: the sink's peers, their polling workers and the received-type counts.
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshPhantomSinkCounts counts received messages by packed type: written by one worker thread, read by the main thread.</summary>
    internal sealed class ServerMeshPhantomSinkCounts
    {
        private const int Slots = 64;
        private readonly int[] _types = new int[Slots];
        private readonly long[] _calls = new long[Slots];
        private readonly long[] _bytes = new long[Slots];
        private long _otherCalls, _otherBytes;

        public ServerMeshPhantomSinkCounts()
        {
            for (int i = 0; i < Slots; ++i)
                _types[i] = -1;
        }

        /// <summary>Record counts one message; unknown and overflowing types count as other. Worker thread only.</summary>
        public void Record(bool known, ushort type, int bytes)
        {
            if (known)
            {
                for (int probe = 0; probe < Slots; ++probe)
                {
                    int slot = (type + probe) & (Slots - 1);
                    int held = Volatile.Read(ref _types[slot]);
                    if (held == -1)
                    {
                        Volatile.Write(ref _types[slot], type);
                        held = type;
                    }
                    if (held != type)
                        continue;
                    Interlocked.Increment(ref _calls[slot]);
                    Interlocked.Add(ref _bytes[slot], bytes);
                    return;
                }
            }
            Interlocked.Increment(ref _otherCalls);
            Interlocked.Add(ref _otherBytes, bytes);
        }

        /// <summary>AddTo adds the cumulative counts into the main thread's per-type totals.</summary>
        public void AddTo(Dictionary<ushort, long> calls, Dictionary<ushort, long> bytes, ref long otherCalls, ref long otherBytes)
        {
            for (int slot = 0; slot < Slots; ++slot)
            {
                int type = Volatile.Read(ref _types[slot]);
                if (type < 0)
                    continue;
                ushort key = (ushort)type;
                calls.TryGetValue(key, out long c);
                bytes.TryGetValue(key, out long b);
                calls[key] = c + Interlocked.Read(ref _calls[slot]);
                bytes[key] = b + Interlocked.Read(ref _bytes[slot]);
            }
            otherCalls += Interlocked.Read(ref _otherCalls);
            otherBytes += Interlocked.Read(ref _otherBytes);
        }
    }

    /// <summary>ServerMeshPhantomSinkWorker is one polling thread over its peers.</summary>
    internal sealed class ServerMeshPhantomSinkWorker
    {
        public readonly List<ServerMeshPhantomSinkPeer> Peers = new List<ServerMeshPhantomSinkPeer>();
        public readonly ServerMeshPhantomSinkCounts Counts = new ServerMeshPhantomSinkCounts();
        private readonly int _index;
        private Thread _thread;
        private volatile bool _stop;
        private long _maxPassTicks;

        public ServerMeshPhantomSinkWorker(int index) => _index = index;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "MeshPhantomSink" + _index.ToString(CultureInfo.InvariantCulture) };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            if (_thread != null && !_thread.Join(2000))
                Logging.LogWarning("Mesh", "Phantom sink: worker " + _index.ToString(CultureInfo.InvariantCulture) + " did not stop within 2 s");
        }

        public double TakeMaxPassMs() => Interlocked.Exchange(ref _maxPassTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private void Run()
        {
            long lastUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!_stop)
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                double now = ServerMeshPhantomSink.Now;
                float elapsedMs = (float)((started - lastUpdate) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                lastUpdate = started;
                for (int i = 0; i < Peers.Count; ++i)
                {
                    ServerMeshPhantomSinkPeer peer = Peers[i];
                    peer.Manager.PollEvents();
                    peer.Tick(now);
                    peer.Manager.ManualUpdate(elapsedMs);
                }
                long pass = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                if (pass > Interlocked.Read(ref _maxPassTicks))
                    Interlocked.Exchange(ref _maxPassTicks, pass);
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>ServerMeshPhantomSinkPeer is one sink peer: its NetManager's listener, hello, bind state and uploads. Worker thread only, except the counters.</summary>
    internal sealed class ServerMeshPhantomSinkPeer : INetEventListener
    {
        private readonly int _index;
        private readonly string _host;
        private readonly int _port;
        private readonly long _baseMs;
        private readonly ServerMeshPhantomSinkCounts _counts;
        private readonly NetDataWriter _writer = new NetDataWriter(true, 64);
        private ServerMeshPhantomBindReply _reply;
        private NetPeer _peer;
        private volatile bool _connected, _bound;
        private long _uploads, _disconnects;
        private double _nextConnect, _nextDue, _lastStep, _nextTurn;
        private long _lastTimestamp;
        private bool _stopSent;
        private Vector3 _position;
        private Vector2 _heading = Vector2.up;
        private System.Random _random;

        public ServerMeshPhantomSinkPeer(int index, string host, int port, long baseMs, ServerMeshPhantomSinkCounts counts)
        {
            _index = index;
            _host = host;
            _port = port;
            _baseMs = baseMs;
            _counts = counts;
            _random = new System.Random(unchecked(index * 486187739 + 97));
        }

        public NetManager Manager;
        public bool IsConnected => _connected;
        public bool IsBound => _bound;
        public long Uploads => Interlocked.Read(ref _uploads);
        public long Disconnects => Interlocked.Read(ref _disconnects);

        public void Connect(double now)
        {
            _nextConnect = now + ServerMeshPhantomSink.ReconnectSeconds;
            _peer = Manager.Connect(_host, _port, ServerMeshPhantomSink.ConnectKey);
        }

        public void Tick(double now)
        {
            if (!_connected)
            {
                if (now >= _nextConnect && (_peer == null || _peer.ConnectionState == ConnectionState.Disconnected))
                    Connect(now);
                return;
            }
            if (!_bound || _reply.kind != (byte)ServerMeshPhantomKind.Character || _reply.hz == 0)
                return;
            if (!ServerMeshPhantomSink.UploadDue(now, 1.0 / _reply.hz, ref _nextDue))
                return;
            bool moving = _reply.stand == 0;
            if (moving)
            {
                if (now >= _nextTurn)
                {
                    float angle = (float)_random.NextDouble() * Mathf.PI * 2f;
                    _heading = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    _nextTurn = now + ServerMeshPhantomUploads.MinTurnSeconds + _random.NextDouble() * ServerMeshPhantomUploads.TurnSpreadSeconds;
                }
                _position = ServerMeshPhantomUploads.Step(_position, ref _heading, _reply.speed * (float)(now - _lastStep), _reply.Region);
            }
            _lastStep = now;
            long timestamp = ServerMeshPhantomUploads.NextTimestamp(_lastTimestamp, _baseMs + (long)(now * 1000.0));
            ServerMeshPhantomUploads.WriteFrame(_writer, timestamp, _reply.objectId);
            IServerMeshPhantomPayload payload = _reply.payload == (byte)ServerMeshPhantomPayloadKind.Input ? (IServerMeshPhantomPayload)ServerMeshPhantomInputPayload.Instance : ServerMeshPhantomTransformPayload.Instance;
            if (!payload.TryWrite(_writer, moving, false, _position, ServerMeshPhantomUploads.YawOf(_heading), ref _stopSent, out bool reliable))
                return;
            _peer.Send(_writer, 2, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            _lastTimestamp = timestamp;
            Interlocked.Increment(ref _uploads);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            _connected = true;
            ServerMeshPhantomSink.WriteHello(_writer, _index);
            peer.Send(_writer, 0, DeliveryMethod.ReliableOrdered);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (_connected)
                Interlocked.Increment(ref _disconnects);
            _connected = false;
            _bound = false;
            _nextConnect = ServerMeshPhantomSink.Now + ServerMeshPhantomSink.ReconnectSeconds;
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int bytes = reader.AvailableBytes;
            bool known = MeshSendMeter.TryReadMessageType(reader.RawData, reader.Position, reader.RawDataSize, out ushort type, out _);
            _counts.Record(known, type, bytes);
            if (!known || type != ServerMeshPhantomTransport.BindReplyMessageType || !ServerMeshPhantomBindReply.TryRead(reader, out ServerMeshPhantomBindReply reply))
                return;
            _reply = reply;
            _position = new Vector3(reply.x, 0f, reply.z);
            double now = ServerMeshPhantomSink.Now;
            _lastStep = now;
            _nextTurn = now;
            _stopSent = false;
            _nextDue = ServerMeshPhantomUploads.FirstDue(now, reply.hz > 0 ? 1.0 / reply.hz : 1.0, ServerMeshPhantomUploads.Unit(0, _index, ServerMeshPhantomClients.PhaseSalt));
            _bound = true;
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
