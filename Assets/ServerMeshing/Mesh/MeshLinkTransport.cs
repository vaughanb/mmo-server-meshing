// Server Meshing add-on — rig: a client's connection with a real network's faults on one machine (DESIGN.md 1.35).
//
// MESH_CLIENT_LATENCY_MS (1.30) holds whole messages above LiteNetLib, so it cannot lose one, and a lost reliable packet
// is what a bad network costs: its resend, and everything queued behind it on its channel. With
// MESH_CLIENT_NET=<one way ms>,<jitter ms>,<loss %>[,<outage ms>] every client transport the map manager builds
// connects through a UDP relay on this machine that delays and drops datagrams both ways, under LiteNetLib, so its
// handshake, acks and resends meet the same network as its data. Datagrams stay in order, as they do on most paths: jitter
// is a queue, not a shuffle. Loss comes in outages of a length in time, as a network's does: counted in datagrams, one
// burst lasted seconds on a quiet connection and took every resend of a message. LiteNetLib's own simulation is compiled
// only under DEBUG, and acts only on what a manager receives. Server members are forwarded untouched, so a cell that
// inherits the variable is not affected.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLinkConditions is what a client's network does to each datagram, each way.</summary>
    public readonly struct MeshLinkConditions
    {
        public readonly int delayMs;
        public readonly int jitterMs;
        public readonly float lossPercent;
        public readonly int outageMs;

        public MeshLinkConditions(int delayMs, int jitterMs, float lossPercent, int outageMs = 0)
        {
            this.delayMs = delayMs;
            this.jitterMs = jitterMs;
            this.lossPercent = lossPercent;
            this.outageMs = outageMs;
        }

        /// <summary>TryParse reads "delay,jitter,loss[,outage]": true when it asks for a network. A malformed value is false with a warning; unset or empty is false without one.</summary>
        public static bool TryParse(string value, out MeshLinkConditions conditions, out string warning)
        {
            conditions = default;
            warning = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string[] parts = value.Split(',');
            int outage = 0;
            if ((parts.Length != 3 && parts.Length != 4)
                || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay < 0
                || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int jitter) || jitter < 0
                || !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float loss) || loss < 0f || loss > 100f
                || (parts.Length == 4 && (!int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out outage) || outage < 0)))
            {
                warning = $"{MeshLinkTransportFactory.EnvironmentKey}='{value}' is not <one way ms>,<jitter ms>,<loss %>[,<outage ms>]; no network is simulated";
                return false;
            }
            conditions = new MeshLinkConditions(delay, jitter, loss, outage);
            return true;
        }

        public override string ToString() => $"{delayMs} ms each way, jitter {jitterMs} ms, loss {lossPercent.ToString("0.##", CultureInfo.InvariantCulture)} %, outages of {outageMs} ms";
    }

    /// <summary>
    /// MeshLinkDirection judges the datagrams going one way. With <see cref="MeshLinkConditions.outageMs"/> the network
    /// goes down for that long on average, often enough to be down the asked share of the time, and loses what comes
    /// meanwhile; without, each datagram is lost on its own. A datagram's time out is the delay plus or minus the jitter,
    /// and never before the one ahead of it.
    /// </summary>
    public sealed class MeshLinkDirection
    {
        private readonly MeshLinkConditions _conditions;
        private readonly System.Random _random;
        private readonly double _loss;
        private readonly double _upMs;
        private double _nextChangeMs;
        private bool _down;
        private long _lastDue;

        public MeshLinkDirection(MeshLinkConditions conditions, int seed)
        {
            _conditions = conditions;
            _random = new System.Random(seed);
            _loss = conditions.lossPercent / 100d;
            _upMs = _loss > 0d && _loss < 1d ? conditions.outageMs * (1d - _loss) / _loss : 0d;
            _nextChangeMs = Lasting(_upMs);
        }

        /// <summary>Admit is false for a datagram the network loses; otherwise <paramref name="dueMs"/> is when it comes out.</summary>
        public bool Admit(long nowMs, out long dueMs)
        {
            dueMs = 0;
            if (_loss >= 1d || Lost(nowMs))
                return false;
            long due = nowMs + _conditions.delayMs + (_conditions.jitterMs > 0 ? _random.Next(-_conditions.jitterMs, _conditions.jitterMs + 1) : 0);
            _lastDue = dueMs = Math.Max(Math.Max(due, nowMs), _lastDue);
            return true;
        }

        private bool Lost(long nowMs)
        {
            if (_conditions.outageMs == 0)
                return _random.NextDouble() < _loss;
            if (_loss <= 0d)
                return false;
            while (nowMs >= _nextChangeMs)
            {
                _down = !_down;
                _nextChangeMs += Lasting(_down ? _conditions.outageMs : _upMs);
            }
            return _down;
        }

        /// <summary>Lasting is how long a state holds: random, exponential around <paramref name="meanMs"/>.</summary>
        private double Lasting(double meanMs)
        {
            return -meanMs * Math.Log(1d - _random.NextDouble());
        }
    }

    /// <summary>
    /// MeshLinkRelay sits between one client and one server. The client talks to <see cref="LocalPort"/>; a thread passes
    /// datagrams on through a <see cref="MeshLinkDirection"/> each way. <see cref="Close"/> leaves it running long
    /// enough to pass what it holds, as the network would, so a closing client's last packets still arrive.
    /// </summary>
    public sealed class MeshLinkRelay
    {
        private const int MaxDatagram = 65536;
        private const int WaitMicroseconds = 1000;
        private const int LingerMs = 100;
        private const int IdleCloseMs = 60000;

        private readonly struct Held
        {
            public readonly long due;
            public readonly byte[] data;

            public Held(long due, byte[] data)
            {
                this.due = due;
                this.data = data;
            }
        }

        private readonly Socket _clientSide;
        private readonly Socket _serverSide;
        private readonly IPEndPoint _server;
        private readonly MeshLinkDirection _up;
        private readonly MeshLinkDirection _down;
        private readonly Queue<Held> _toServer = new Queue<Held>();
        private readonly Queue<Held> _toClient = new Queue<Held>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _lingerMs;
        private EndPoint _client;
        private long _closeAtMs = long.MaxValue;
        private int _upPassed;
        private int _upDropped;
        private int _downPassed;
        private int _downDropped;

        /// <summary>MeshLinkRelay binds a port on this machine for a client of <paramref name="server"/>. Start begins the passing.</summary>
        public MeshLinkRelay(IPEndPoint server, MeshLinkConditions conditions, int seed)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server), "a link relay needs a server to pass to");
            _up = new MeshLinkDirection(conditions, seed);
            _down = new MeshLinkDirection(conditions, unchecked(seed * 31 + 1));
            _lingerMs = conditions.delayMs + conditions.jitterMs + LingerMs;
            _clientSide = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _clientSide.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _serverSide = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _serverSide.Bind(new IPEndPoint(IPAddress.Any, 0));
            LocalPort = ((IPEndPoint)_clientSide.LocalEndPoint).Port;
        }

        /// <summary>LocalPort is the port on 127.0.0.1 the client connects to.</summary>
        public int LocalPort { get; }

        public void Start()
        {
            Thread thread = new Thread(Pump) { IsBackground = true, Name = "MeshLinkRelay" };
            thread.Start();
        }

        /// <summary>Close stops the relay once what it holds has gone out. The thread closes the sockets.</summary>
        public void Close()
        {
            Interlocked.Exchange(ref _closeAtMs, _clock.ElapsedMilliseconds + _lingerMs);
        }

        /// <summary>Counts is the datagrams so far, each way, for the log line that shows a run met the network it asked for.</summary>
        public string Counts => $"to server {Volatile.Read(ref _upPassed)} passed {Volatile.Read(ref _upDropped)} lost, to client {Volatile.Read(ref _downPassed)} passed {Volatile.Read(ref _downDropped)} lost";

        private void Pump()
        {
            byte[] buffer = new byte[MaxDatagram];
            List<Socket> readable = new List<Socket>(2);
            long lastTraffic = _clock.ElapsedMilliseconds;
            try
            {
                while (true)
                {
                    long now = _clock.ElapsedMilliseconds;
                    Flush(_toServer, _serverSide, _server, now);
                    if (_client != null)
                        Flush(_toClient, _clientSide, _client, now);
                    if (now >= Interlocked.Read(ref _closeAtMs) || now - lastTraffic > IdleCloseMs)
                        return;
                    readable.Clear();
                    readable.Add(_clientSide);
                    readable.Add(_serverSide);
                    Socket.Select(readable, null, null, WaitMicroseconds);
                    now = _clock.ElapsedMilliseconds;
                    for (int i = 0; i < readable.Count; ++i)
                    {
                        if (Take(readable[i], buffer, now))
                            lastTraffic = now;
                    }
                }
            }
            finally
            {
                _clientSide.Close();
                _serverSide.Close();
            }
        }

        private bool Take(Socket socket, byte[] buffer, long now)
        {
            bool fromClient = socket == _clientSide;
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                return false; // an ICMP port unreachable: the far end closed, and the next datagram may still pass
            }
            if (fromClient)
                _client = from;
            if (!(fromClient ? _up : _down).Admit(now, out long due))
            {
                Interlocked.Increment(ref fromClient ? ref _upDropped : ref _downDropped);
                return true;
            }
            Interlocked.Increment(ref fromClient ? ref _upPassed : ref _downPassed);
            byte[] data = new byte[length];
            Buffer.BlockCopy(buffer, 0, data, 0, length);
            (fromClient ? _toServer : _toClient).Enqueue(new Held(due, data));
            return true;
        }

        private static void Flush(Queue<Held> held, Socket socket, EndPoint to, long now)
        {
            while (held.Count > 0 && held.Peek().due <= now)
            {
                try
                {
                    socket.SendTo(held.Dequeue().data, to);
                }
                catch (SocketException)
                {
                    // the far end is gone: as on a network, the datagram is simply lost
                }
            }
        }
    }

    /// <summary>
    /// MeshLinkTransport wraps a transport and connects it through a <see cref="MeshLinkRelay"/>. Everything but the
    /// client's connect and stop is forwarded, so what LiteNetLib does about the network is what a client would.
    /// </summary>
    public sealed class MeshLinkTransport : ITransport, IMeshWrappedTransport
    {
        private readonly ITransport _inner;
        private readonly MeshLinkConditions _conditions;
        private MeshLinkRelay _relay;
        private string _serverName;

        /// <summary>MeshLinkTransport puts <paramref name="inner"/>'s client connections on a network of <paramref name="conditions"/>.</summary>
        public MeshLinkTransport(ITransport inner, MeshLinkConditions conditions)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner), "the link transport needs a transport to wrap");
            _conditions = conditions;
        }

        /// <summary>Inner is the wrapped transport.</summary>
        public ITransport Inner => _inner;

        public int ServerPeersCount => _inner.ServerPeersCount;
        public int ServerMaxConnections => _inner.ServerMaxConnections;
        public bool IsClientStarted => _inner.IsClientStarted;
        public bool IsServerStarted => _inner.IsServerStarted;
        public bool HasImplementedPing => _inner.HasImplementedPing;
        public bool IsReliableOnly => _inner.IsReliableOnly;

        /// <summary>StartClient opens a relay to the server and connects the inner client to it.</summary>
        public bool StartClient(string address, int port)
        {
            CloseRelay();
            IPAddress resolved = Resolve(address);
            if (resolved == null)
            {
                Logging.LogError("Mesh", $"Link transport: {address} has no IPv4 address to relay to");
                return false;
            }
            _relay = new MeshLinkRelay(new IPEndPoint(resolved, port), _conditions, unchecked(Environment.TickCount + port));
            _serverName = $"{address}:{port}";
            _relay.Start();
            if (_inner.StartClient(IPAddress.Loopback.ToString(), _relay.LocalPort))
                return true;
            CloseRelay();
            return false;
        }

        private static IPAddress Resolve(string address)
        {
            if (IPAddress.TryParse(address, out IPAddress parsed))
                return parsed;
            return Array.Find(Dns.GetHostAddresses(address), candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        }

        /// <summary>StopClient stops the inner client, whose goodbye still crosses the network, and then the relay.</summary>
        public void StopClient()
        {
            _inner.StopClient();
            CloseRelay();
        }

        private void CloseRelay()
        {
            if (_relay == null)
                return;
            Logging.Log("Mesh", $"Link to {_serverName} closed: {_relay.Counts}");
            _relay.Close();
            _relay = null;
        }

        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => _inner.ClientSend(dataChannel, deliveryMethod, writer);
        public bool ClientReceive(out TransportEventData eventData) => _inner.ClientReceive(out eventData);
        public bool StartServer(int port, int maxConnections) => _inner.StartServer(port, maxConnections);
        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => _inner.ServerSend(connectionId, dataChannel, deliveryMethod, writer);
        public bool ServerReceive(out TransportEventData eventData) => _inner.ServerReceive(out eventData);
        public bool ServerDisconnect(long connectionId) => _inner.ServerDisconnect(connectionId);
        public void StopServer() => _inner.StopServer();
        public long GetClientRtt() => _inner.GetClientRtt();
        public long GetServerRtt(long connectionId) => _inner.GetServerRtt(connectionId);

        public void Destroy()
        {
            CloseRelay();
            _inner.Destroy();
        }
    }

    /// <summary>MeshLinkTransportFactory builds a <see cref="MeshLinkTransport"/> around each transport of <see cref="Inner"/>.</summary>
    public class MeshLinkTransportFactory : BaseTransportFactory
    {
        /// <summary>EnvironmentKey is the network: "one way ms,jitter ms,loss %[,outage ms]".</summary>
        public const string EnvironmentKey = "MESH_CLIENT_NET";

        /// <summary>Inner is the factory whose transports are wrapped; when null, Build adds the kit's default factory.</summary>
        public BaseTransportFactory Inner;

        /// <summary>Conditions is the network each client connection is put on.</summary>
        public MeshLinkConditions Conditions;

        public override ITransport Build()
        {
            if (Inner == this)
                throw new InvalidOperationException($"the link transport factory under {gameObject.name} is its own inner factory");
            if (Inner == null)
                Inner = MeshMeteredTransportFactory.KitDefault(gameObject);
            return new MeshLinkTransport(Inner.Build(), Conditions);
        }
    }
}
