// Server Meshing add-on — the state sender's window line.
using System;
using System.Text;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>MeshStateSenderStats keeps the state sender's build durations for a window and formats its "Sender window" log line.</summary>
    public sealed class MeshStateSenderStats
    {
        private readonly MeshWorkHistogram _builds = new MeshWorkHistogram(0.02f, 1000);

        /// <summary>AddBuild records one build's whole duration in milliseconds.</summary>
        public void AddBuild(double totalMs)
        {
            _builds.Add((float)totalMs);
        }

        /// <summary>
        /// ToLine formats one "Sender window" line in invariant culture from the fan-out's counters taken for the window
        /// and the sender's own figures, then empties the build histogram. Every field is always printed, integers before
        /// rates; kB is 1024 B; the ms/s figures come from Stopwatch ticks at <paramref name="ticksPerSecond"/>; hz and
        /// withheld are gauges at the last build; <paramref name="gen0"/> is the sender's gen-0 collection delta. A
        /// negative or non-finite figure prints as 0. It prints no payload group.
        /// </summary>
        public string ToLine(double seconds, MeshStateSenderSetting setting, int kitSources, int directSources, int clients, int rewraps, int wrapRefusals, MeshStateFanoutCounters c, long ticksPerSecond, int gen0)
        {
            return ToLine(seconds, setting, kitSources, directSources, clients, rewraps, wrapRefusals, c, ticksPerSecond, gen0, false);
        }

        /// <summary>
        /// ToLine formats the line as the overload without <paramref name="showPayload"/> does and, when it is true,
        /// appends the payload group after gc0: " | payload full n 1000 min 56 max 56 compact n 4000 min 28 max 28" (the
        /// payloads the fan-out stored in the window, a minimum with none printing 0).
        /// </summary>
        public string ToLine(double seconds, MeshStateSenderSetting setting, int kitSources, int directSources, int clients, int rewraps, int wrapRefusals, MeshStateFanoutCounters c, long ticksPerSecond, int gen0, bool showPayload)
        {
            if (c == null)
                throw new ArgumentNullException(nameof(c));
            double window = seconds > 0d && !double.IsInfinity(seconds) ? seconds : 0d;
            long kit = Clean(kitSources), direct = Clean(directSources), clientCount = Clean(clients);
            long packets = Clean(c.UnreliablePackets) + Clean(c.ReliablePackets);
            double kiloRate = Rate(Clean(c.BytesOut), window) / 1024d;
            double packetRate = Rate(packets, window);
            double buildMs = MsPerSecond(c.WriteTicks, ticksPerSecond, window);
            double fanoutMs = MsPerSecond(c.FanoutTicks, ticksPerSecond, window);
            double sendMs = MsPerSecond(c.SendTicks, ticksPerSecond, window);
            double microsPerPacket = packets > 0 && ticksPerSecond > 0 ? Clean(c.SendTicks) * 1_000_000d / ticksPerSecond / packets : 0d;
            long[] hz = c.PairsByHz;

            StringBuilder line = new StringBuilder(640);
            line.Append(FormattableString.Invariant($"Sender window {window:0.00} s: {Word(setting)} sources {kit + direct} kit {kit} direct {direct} pairs {Clean(c.Pairs)} clients {clientCount}"));
            line.Append(FormattableString.Invariant($" | builds {Clean(c.Builds)} skipped {Clean(c.Skipped)} rewraps {Clean(rewraps)} refusals {Clean(wrapRefusals)}"));
            line.Append(FormattableString.Invariant($" | writes {Clean(c.Writes)} compact {Clean(c.CompactWrites)} false {Clean(c.NotWritten)} empty {Clean(c.EmptyWrites)} errors {Clean(c.WriteErrors)} dropped {Clean(c.AppendOnlyDrops) + Clean(c.IdentityDrops)}"));
            line.Append(FormattableString.Invariant($" | records u {Clean(c.UnreliableRecords)} r {Clean(c.ReliableRecords)} deferred {Clean(c.DeferredRecords)}"));
            line.Append(FormattableString.Invariant($" | packets u {Clean(c.UnreliablePackets)} r {Clean(c.ReliablePackets)} oversize {Clean(c.OversizeReliable)} tooBig {Clean(c.TooBigSkipped)} sendErrors {Clean(c.SendErrors)} reliableFailed {Clean(c.ReliableSendFailures)}"));
            line.Append(FormattableString.Invariant($" | stuckReliable {Clean(c.StuckReliable)} withheldOwner {Clean(c.WithheldOwner)}"));
            line.Append(FormattableString.Invariant($" | hz 20:{Clean(hz[0])} 10:{Clean(hz[1])} 5:{Clean(hz[2])} 2:{Clean(hz[3])} 1:{Clean(hz[4])} 0:{Clean(hz[5])} withheld {Clean(c.PairsWithheld)} clamped {Clean(c.HzClamped)}"));
            line.Append(FormattableString.Invariant($" | out {kiloRate:0.0} kB/s {packetRate:0.0} pkt/s per client {PerClient(kiloRate, clientCount):0.0} kB/s {PerClient(packetRate, clientCount):0.0} pkt/s"));
            line.Append(FormattableString.Invariant($" | ms/s build {buildMs:0.00} fanout {fanoutMs:0.00} send {sendMs:0.00} total {buildMs + fanoutMs + sendMs:0.00}"));
            line.Append(FormattableString.Invariant($" | us/packet {microsPerPacket:0.00}"));
            line.Append(FormattableString.Invariant($" | build p95 {Finite(_builds.Percentile(0.95f)):0.00} max {Finite(_builds.Max):0.00} ms | gc0 {Clean(gen0)}"));
            if (showPayload)
                line.Append(FormattableString.Invariant($" | payload full n {Clean(c.FullPayloads)} min {Least(c.FullPayloadMin)} max {Clean(c.FullPayloadMax)} compact n {Clean(c.CompactPayloads)} min {Least(c.CompactPayloadMin)} max {Clean(c.CompactPayloadMax)}"));
            _builds.Reset();
            return line.ToString();
        }

        private static string Word(MeshStateSenderSetting setting)
        {
            switch (setting)
            {
                case MeshStateSenderSetting.On: return "on";
                case MeshStateSenderSetting.Measure: return "measure";
                default: return "off";
            }
        }

        private static double MsPerSecond(long ticks, long ticksPerSecond, double seconds)
        {
            if (ticks <= 0 || ticksPerSecond <= 0 || seconds <= 0d)
                return 0d;
            return ticks * 1000d / ticksPerSecond / seconds;
        }

        private static double Rate(long value, double seconds) => seconds > 0d && value > 0 ? value / seconds : 0d;

        private static double PerClient(double rate, long clients) => clients > 0 ? rate / clients : 0d;

        private static double Finite(float value) => value > 0f && !float.IsInfinity(value) ? value : 0d;

        private static long Clean(long value) => value > 0 ? value : 0;

        private static long Least(int min) => min == int.MaxValue ? 0 : Clean(min);
    }
}
