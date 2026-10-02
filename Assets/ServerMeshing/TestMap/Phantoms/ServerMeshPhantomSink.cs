// Server Meshing add-on — rig phantom sink (-meshPhantomSink): real LiteNetLib peers on another machine that bind as phantoms, upload like clients and count what the cell sends them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomSink runs in a batchmode process started with -meshPhantomSink host:port:count[:portBase] (no server
    /// argument). It creates count NetManagers (16 channels, the kit's connect key, statistics on, IPv6 off, readers
    /// recycled automatically), starts them in manual mode on the main thread after its own quit handler, and hands them to
    /// workers of about <see cref="PeersPerWorker"/> each that poll and update them well inside LiteNetLib's 5 s timeout.
    /// Each peer says hello with its index; after the bind reply a character peer uploads at the reply's rate on channel 2,
    /// walking inside the reply's square through the virtual feeder's writers, on an absolute schedule with no burst. Workers
    /// only count; the main thread logs a "Sink window" line every <see cref="WindowSeconds"/>.
    /// </summary>
    public class ServerMeshPhantomSink : MonoBehaviour
    {
        /// <summary>PeersPerWorker is how many peers one worker thread polls.</summary>
        public const int PeersPerWorker = 100;
        /// <summary>WindowSeconds is the Sink window's length.</summary>
        public const double WindowSeconds = 5.0;
        /// <summary>ConnectKey is the kit's default connect key (LiteNetLibTransportFactory).</summary>
        public const string ConnectKey = "SampleConnectKey";
        /// <summary>ChannelsCount is the kit's channel count; with fewer, channel 2 traffic is dropped silently.</summary>
        public const byte ChannelsCount = 16;
        /// <summary>ReconnectSeconds is how long a peer waits before connecting again after it lost or never got its connection.</summary>
        public const double ReconnectSeconds = 2.0;
        /// <summary>MaxTypeGroups is how many received message types the line lists before other.</summary>
        public const int MaxTypeGroups = 6;

        private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();

        private readonly List<ServerMeshPhantomSinkPeer> _peers = new List<ServerMeshPhantomSinkPeer>();
        private readonly List<ServerMeshPhantomSinkWorker> _workers = new List<ServerMeshPhantomSinkWorker>();
        private readonly Dictionary<ushort, long> _lastTypeCalls = new Dictionary<ushort, long>();
        private readonly Dictionary<ushort, long> _lastTypeBytes = new Dictionary<ushort, long>();
        private long[] _lastIn, _lastInPackets, _lastOut, _lastUploads;
        private long _lastOtherCalls, _lastOtherBytes, _lastDisconnects;
        private double _windowStart;
        private bool _stopped;

        /// <summary>Now is the sink's clock in seconds.</summary>
        public static double Now => s_clock.Elapsed.TotalSeconds;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string[] args = Environment.GetCommandLineArgs();
            string value = null;
            for (int i = 0; i < args.Length - 1 && value == null; ++i)
            {
                if (args[i] == ServerMeshPhantomSettings.ARG_SINK)
                    value = args[i + 1];
            }
            if (value == null)
                return;
            if (!ServerMeshPhantomSettings.TryParseSink(value, out string host, out int port, out int count, out int portBase, out string error))
            {
                Logging.LogWarning("Mesh", ServerMeshPhantomSettings.Printable($"Phantom sink: {error}; no sink started"));
                return;
            }
            GameObject go = new GameObject("[Mesh] PhantomSink");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshPhantomSink>().Begin(host, port, count, portBase);
        }

        /// <summary>WriteHello resets writer and writes a hello: [packed ushort 65000][int32 index].</summary>
        public static void WriteHello(NetDataWriter writer, int index)
        {
            TransportHandler.WritePacket(writer, ServerMeshPhantomTransport.HelloMessageType);
            writer.Put(index);
        }

        /// <summary>TryReadHello reads a hello from reader's position; false (position kept) when it is not a whole hello.</summary>
        public static bool TryReadHello(NetDataReader reader, out int index)
        {
            index = -1;
            if (reader == null || reader.RawData == null)
                return false;
            int start = reader.Position;
            if (!MeshSendMeter.TryReadMessageType(reader.RawData, start, reader.RawDataSize, out ushort type, out int typeBytes) || type != ServerMeshPhantomTransport.HelloMessageType || reader.RawDataSize - start - typeBytes < 4)
                return false;
            reader.SetPosition(start + typeBytes);
            index = reader.GetInt();
            return true;
        }

        /// <summary>UploadDue is the sink's upload schedule: due at absolute times next, next + period, ..., at most once per call, skipping ahead after a stall.</summary>
        public static bool UploadDue(double now, double period, ref double next) => ServerMeshPhantomUploads.TakeDue(now, period, ref next);

        /// <summary>
        /// SinkWindowLine is the Sink window line: integer in bytes and packets, the per-peer down distribution in kB/s
        /// (wire), up to six received types by window bytes (descending, ties by type) with the rest folded into other, the
        /// uploads sent with the sent bytes' rate, disconnects, workers and the worst poll pass. Invariant, no NaN or
        /// negatives; it sorts downKiloBytesPerSecond in place.
        /// </summary>
        public static string SinkWindowLine(double seconds, int peers, int connected, int bound, long inBytes, long inPackets, List<double> downKiloBytesPerSecond,
            IList<ushort> types, IList<long> typeCalls, IList<long> typeBytes, long otherCalls, long otherBytes, long outUploads, long outBytes, long disconnects, int workers, double maxPassMs)
        {
            double s = seconds > 0 && !double.IsInfinity(seconds) ? seconds : 0;
            downKiloBytesPerSecond.Sort();
            int n = downKiloBytesPerSecond.Count;
            double mean = 0;
            for (int i = 0; i < n; ++i)
                mean += downKiloBytesPerSecond[i];
            mean = n > 0 ? mean / n : 0;
            int[] order = new int[types.Count];
            for (int i = 0; i < order.Length; ++i)
                order[i] = i;
            Array.Sort(order, (a, b) => typeBytes[a] != typeBytes[b] ? typeBytes[b].CompareTo(typeBytes[a]) : types[a].CompareTo(types[b]));
            StringBuilder b = new StringBuilder(256);
            b.Append("Sink window ").Append(Fixed(s, "0.00")).Append(" s: peers ").Append(Count(peers)).Append(" connected ").Append(Count(connected)).Append(" bound ").Append(Count(bound));
            b.Append(" | in ").Append(Count(inBytes)).Append(" B ").Append(Count(inPackets)).Append(" pkt ").Append(Fixed(s > 0 ? inBytes / 1024.0 / s : 0, "0.0")).Append(" kB/s");
            b.Append(" | down n ").Append(n).Append(" mean ").Append(Fixed(mean, "0.0")).Append(" p95 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(downKiloBytesPerSecond, 0.95), "0.0"));
            b.Append(" max ").Append(Fixed(n > 0 ? downKiloBytesPerSecond[n - 1] : 0, "0.0")).Append(" kB/s");
            long restCalls = Math.Max(0, otherCalls), restBytes = Math.Max(0, otherBytes);
            for (int i = 0; i < order.Length; ++i)
            {
                int t = order[i];
                if (i < MaxTypeGroups)
                    b.Append(" | t").Append(types[t].ToString(CultureInfo.InvariantCulture)).Append(" calls ").Append(Count(typeCalls[t])).Append(" bytes ").Append(Count(typeBytes[t]));
                else
                {
                    restCalls += Math.Max(0, typeCalls[t]);
                    restBytes += Math.Max(0, typeBytes[t]);
                }
            }
            b.Append(" | other calls ").Append(Count(restCalls)).Append(" bytes ").Append(Count(restBytes));
            b.Append(" | out ").Append(Count(outUploads)).Append(" uploads ").Append(Fixed(s > 0 ? outUploads / s : 0, "0.0")).Append("/s ").Append(Fixed(s > 0 ? outBytes / 1024.0 / s : 0, "0.0")).Append(" kB/s");
            b.Append(" | disconnects ").Append(Count(disconnects)).Append(" | workers ").Append(Count(workers)).Append(" maxPassMs ").Append(Fixed(maxPassMs, "0.0"));
            return b.ToString();
        }

        private static string Fixed(double value, string format) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);

        private void Begin(string host, int port, int count, int portBase)
        {
            Application.quitting += Shutdown;
            long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int workerCount = (count + PeersPerWorker - 1) / PeersPerWorker;
            for (int w = 0; w < workerCount; ++w)
                _workers.Add(new ServerMeshPhantomSinkWorker(w));
            int failed = 0;
            for (int i = 0; i < count; ++i)
            {
                ServerMeshPhantomSinkWorker worker = _workers[i / PeersPerWorker];
                ServerMeshPhantomSinkPeer peer = new ServerMeshPhantomSinkPeer(i, host, port, baseMs, worker.Counts);
                NetManager manager = new NetManager(peer)
                {
                    ChannelsCount = ChannelsCount,
                    IPv6Enabled = false,
                    EnableStatistics = true,
                    AutoRecycle = true,
                };
                if (!manager.StartInManualMode(portBase > 0 ? portBase + i : 0))
                {
                    ++failed;
                    continue;
                }
                peer.Manager = manager;
                peer.Connect(Now);
                _peers.Add(peer);
                worker.Peers.Add(peer);
            }
            _lastIn = new long[_peers.Count];
            _lastInPackets = new long[_peers.Count];
            _lastOut = new long[_peers.Count];
            _lastUploads = new long[_peers.Count];
            for (int w = 0; w < _workers.Count; ++w)
                _workers[w].Start();
            _windowStart = Now;
            string started = FormattableString.Invariant($"Phantom sink: {_peers.Count} peers to {host}:{port} on {_workers.Count} workers (port base {portBase})");
            if (failed > 0)
                Logging.LogError("Mesh", FormattableString.Invariant($"Phantoms error: sink: {failed} of {count} peers could not open a socket; {started}"));
            else
                Logging.Log("Mesh", started);
        }

        private void Update()
        {
            if (_stopped)
                return;
            double now = Now;
            if (now - _windowStart < WindowSeconds)
                return;
            double seconds = now - _windowStart;
            _windowStart = now;
            Logging.Log("Mesh", TakeWindow(seconds));
        }

        private string TakeWindow(double seconds)
        {
            int connected = 0, bound = 0;
            long inBytes = 0, inPackets = 0, outBytes = 0, uploads = 0, disconnects = 0;
            List<double> down = new List<double>(_peers.Count);
            for (int i = 0; i < _peers.Count; ++i)
            {
                ServerMeshPhantomSinkPeer peer = _peers[i];
                NetStatistics stats = peer.Manager.Statistics;
                long received = stats.BytesReceived, packets = stats.PacketsReceived, sent = stats.BytesSent, sentUploads = peer.Uploads;
                inBytes += received - _lastIn[i];
                inPackets += packets - _lastInPackets[i];
                outBytes += sent - _lastOut[i];
                uploads += sentUploads - _lastUploads[i];
                down.Add(seconds > 0 ? (received - _lastIn[i]) / 1024.0 / seconds : 0);
                _lastIn[i] = received;
                _lastInPackets[i] = packets;
                _lastOut[i] = sent;
                _lastUploads[i] = sentUploads;
                if (peer.IsConnected)
                    ++connected;
                if (peer.IsBound)
                    ++bound;
                disconnects += peer.Disconnects;
            }
            long windowDisconnects = disconnects - _lastDisconnects;
            _lastDisconnects = disconnects;
            Dictionary<ushort, long> calls = new Dictionary<ushort, long>(), bytes = new Dictionary<ushort, long>();
            long otherCalls = 0, otherBytes = 0;
            double maxPassMs = 0;
            for (int w = 0; w < _workers.Count; ++w)
            {
                ServerMeshPhantomSinkWorker worker = _workers[w];
                worker.Counts.AddTo(calls, bytes, ref otherCalls, ref otherBytes);
                maxPassMs = Math.Max(maxPassMs, worker.TakeMaxPassMs());
            }
            List<ushort> types = new List<ushort>(calls.Count);
            List<long> typeCalls = new List<long>(calls.Count), typeBytes = new List<long>(calls.Count);
            foreach (KeyValuePair<ushort, long> entry in calls)
            {
                _lastTypeCalls.TryGetValue(entry.Key, out long lastCalls);
                _lastTypeBytes.TryGetValue(entry.Key, out long lastBytes);
                types.Add(entry.Key);
                typeCalls.Add(entry.Value - lastCalls);
                typeBytes.Add(bytes[entry.Key] - lastBytes);
                _lastTypeCalls[entry.Key] = entry.Value;
                _lastTypeBytes[entry.Key] = bytes[entry.Key];
            }
            long windowOtherCalls = otherCalls - _lastOtherCalls, windowOtherBytes = otherBytes - _lastOtherBytes;
            _lastOtherCalls = otherCalls;
            _lastOtherBytes = otherBytes;
            return SinkWindowLine(seconds, _peers.Count, connected, bound, inBytes, inPackets, down, types, typeCalls, typeBytes, windowOtherCalls, windowOtherBytes, uploads, outBytes, windowDisconnects, _workers.Count, maxPassMs);
        }

        private void OnDestroy() => Shutdown();

        private void Shutdown()
        {
            if (_stopped)
                return;
            _stopped = true;
            Application.quitting -= Shutdown;
            for (int w = 0; w < _workers.Count; ++w)
                _workers[w].Stop();
            for (int i = 0; i < _peers.Count; ++i)
                _peers[i].Manager.Stop();
        }
    }
}
