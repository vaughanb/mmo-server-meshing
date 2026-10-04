// Server Meshing add-on — what one state fan-out did since its counters were last taken.
using System;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshStateFanoutCounters holds a fan-out's totals since the last TakeAndReset, its timing in Stopwatch ticks, and
    /// gauges describing the last completed build. TakeAndReset copies everything and zeroes the totals and timing; the
    /// gauges are copied and kept. Nothing here allocates after construction.
    /// </summary>
    public sealed class MeshStateFanoutCounters
    {
        /// <summary>HzSlots is the length of PairsByHz: 20, 10, 5, 2, 1 and 0 Hz in that order.</summary>
        public const int HzSlots = 6;

        /// <summary>Builds is the builds started (one per frame at most).</summary>
        public long Builds;

        /// <summary>Skipped is the builds refused because the frame already had one (catch-up ticks).</summary>
        public long Skipped;

        /// <summary>Writes is the successful, non-empty WriteState calls.</summary>
        public long Writes;

        /// <summary>CompactWrites is the WriteCompactState calls (at most one per source per build).</summary>
        public long CompactWrites;

        /// <summary>NotWritten is the WriteState calls that returned false.</summary>
        public long NotWritten;

        /// <summary>EmptyWrites is the WriteState calls that returned true with no bytes.</summary>
        public long EmptyWrites;

        /// <summary>WriteErrors is the WriteState and WriteCompactState calls that threw or broke a compact write's append-only rule.</summary>
        public long WriteErrors;

        /// <summary>AppendOnlyDrops is the sources dropped because WriteState rewound or reset the writer.</summary>
        public long AppendOnlyDrops;

        /// <summary>IdentityDrops is the sources dropped because their identity was gone at a build.</summary>
        public long IdentityDrops;

        /// <summary>UnreliableRecords is the records appended to unreliable packets, deferred ones when they were flushed.</summary>
        public long UnreliableRecords;

        /// <summary>ReliableRecords is the records appended to reliable packets, oversize ones included.</summary>
        public long ReliableRecords;

        /// <summary>DeferredRecords is the routine unreliable records put off to the frame after their build.</summary>
        public long DeferredRecords;

        /// <summary>UnreliablePackets is the unreliable packets the transport accepted (in measure mode, encoded).</summary>
        public long UnreliablePackets;

        /// <summary>ReliablePackets is the reliable packets the transport accepted (in measure mode, encoded), oversize ones included.</summary>
        public long ReliablePackets;

        /// <summary>OversizeReliable is the reliable records too big for a capped packet, each sent alone uncapped.</summary>
        public long OversizeReliable;

        /// <summary>TooBigSkipped is the unreliable records too big for a capped packet plus the unreliable packets the transport refused as too big.</summary>
        public long TooBigSkipped;

        /// <summary>SendErrors is the transport calls that threw anything but a too-big error.</summary>
        public long SendErrors;

        /// <summary>ReliableSendFailures is the reliable transport calls that threw, whatever the error; each one keeps that build's flags unconsumed.</summary>
        public long ReliableSendFailures;

        /// <summary>BytesOut is the bytes of the packets counted in UnreliablePackets and ReliablePackets.</summary>
        public long BytesOut;

        /// <summary>WithheldOwner is the classifications that withheld an owner pair, which then got Hz 0 instead.</summary>
        public long WithheldOwner;

        /// <summary>StuckReliable is the wrapped kit handlers that wrote reliable records StuckReliableBuilds builds in a row.</summary>
        public long StuckReliable;

        /// <summary>HzClamped is the classifications whose Hz was not 0, 1, 2, 5, 10 or 20 and was lowered to the next one that is.</summary>
        public long HzClamped;

        /// <summary>PairsVisited is the (source, subscriber) visits of the builds.</summary>
        public long PairsVisited;

        /// <summary>WriteTicks is the Stopwatch ticks spent in WriteState and WriteCompactState.</summary>
        public long WriteTicks;

        /// <summary>FanoutTicks is the Stopwatch ticks spent routing records, writes and transport calls excluded.</summary>
        public long FanoutTicks;

        /// <summary>SendTicks is the Stopwatch ticks spent inside the transport.</summary>
        public long SendTicks;

        /// <summary>FullPayloads is the Full payloads stored for routing (one per successful, non-empty WriteState).</summary>
        public long FullPayloads;

        /// <summary>FullPayloadBytes is the summed length of the Full payloads stored.</summary>
        public long FullPayloadBytes;

        /// <summary>FullPayloadMin is the shortest Full payload stored; int.MaxValue with none.</summary>
        public int FullPayloadMin = int.MaxValue;

        /// <summary>FullPayloadMax is the longest Full payload stored; 0 with none.</summary>
        public int FullPayloadMax;

        /// <summary>CompactPayloads is the Compact payloads stored for routing.</summary>
        public long CompactPayloads;

        /// <summary>CompactPayloadBytes is the summed length of the Compact payloads stored.</summary>
        public long CompactPayloadBytes;

        /// <summary>CompactPayloadMin is the shortest Compact payload stored; int.MaxValue with none.</summary>
        public int CompactPayloadMin = int.MaxValue;

        /// <summary>CompactPayloadMax is the longest Compact payload stored; 0 with none.</summary>
        public int CompactPayloadMax;

        /// <summary>Sources is a gauge: the registered sources at the last build.</summary>
        public long Sources;

        /// <summary>Pairs is a gauge: the pairs visited by the last build.</summary>
        public long Pairs;

        /// <summary>Connections is a gauge: the connections the fan-out kept packets for at the last build.</summary>
        public long Connections;

        /// <summary>PairsWithheld is a gauge: the non-owner pairs the policy withheld at the last build.</summary>
        public long PairsWithheld;

        /// <summary>PairsByHz is a gauge: the pairs of the last build by rate, 20, 10, 5, 2, 1 and 0 Hz; owner pairs count under their Hz.</summary>
        public readonly long[] PairsByHz = new long[HzSlots];

        /// <summary>TakeCount is how many times TakeAndReset ran; the fan-out logs a repeated problem once per value.</summary>
        public long TakeCount { get; private set; }

        /// <summary>
        /// AddPayload counts one payload of <paramref name="length"/> bytes stored for routing, Compact when
        /// <paramref name="compact"/>, else Full.
        /// </summary>
        public void AddPayload(bool compact, int length)
        {
            if (compact)
            {
                CompactPayloads++;
                CompactPayloadBytes += length;
                CompactPayloadMin = Math.Min(CompactPayloadMin, length);
                CompactPayloadMax = Math.Max(CompactPayloadMax, length);
            }
            else
            {
                FullPayloads++;
                FullPayloadBytes += length;
                FullPayloadMin = Math.Min(FullPayloadMin, length);
                FullPayloadMax = Math.Max(FullPayloadMax, length);
            }
        }

        /// <summary>
        /// TakeAndReset copies every figure into <paramref name="into"/>, then zeroes the totals and timing (the payload
        /// minimums go back to int.MaxValue) and keeps the gauges.
        /// </summary>
        public void TakeAndReset(MeshStateFanoutCounters into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.Builds = Builds;
            into.Skipped = Skipped;
            into.Writes = Writes;
            into.CompactWrites = CompactWrites;
            into.NotWritten = NotWritten;
            into.EmptyWrites = EmptyWrites;
            into.WriteErrors = WriteErrors;
            into.AppendOnlyDrops = AppendOnlyDrops;
            into.IdentityDrops = IdentityDrops;
            into.UnreliableRecords = UnreliableRecords;
            into.ReliableRecords = ReliableRecords;
            into.DeferredRecords = DeferredRecords;
            into.UnreliablePackets = UnreliablePackets;
            into.ReliablePackets = ReliablePackets;
            into.OversizeReliable = OversizeReliable;
            into.TooBigSkipped = TooBigSkipped;
            into.SendErrors = SendErrors;
            into.ReliableSendFailures = ReliableSendFailures;
            into.BytesOut = BytesOut;
            into.WithheldOwner = WithheldOwner;
            into.StuckReliable = StuckReliable;
            into.HzClamped = HzClamped;
            into.PairsVisited = PairsVisited;
            into.WriteTicks = WriteTicks;
            into.FanoutTicks = FanoutTicks;
            into.SendTicks = SendTicks;
            into.FullPayloads = FullPayloads;
            into.FullPayloadBytes = FullPayloadBytes;
            into.FullPayloadMin = FullPayloadMin;
            into.FullPayloadMax = FullPayloadMax;
            into.CompactPayloads = CompactPayloads;
            into.CompactPayloadBytes = CompactPayloadBytes;
            into.CompactPayloadMin = CompactPayloadMin;
            into.CompactPayloadMax = CompactPayloadMax;
            into.Sources = Sources;
            into.Pairs = Pairs;
            into.Connections = Connections;
            into.PairsWithheld = PairsWithheld;
            Array.Copy(PairsByHz, into.PairsByHz, HzSlots);

            Builds = 0;
            Skipped = 0;
            Writes = 0;
            CompactWrites = 0;
            NotWritten = 0;
            EmptyWrites = 0;
            WriteErrors = 0;
            AppendOnlyDrops = 0;
            IdentityDrops = 0;
            UnreliableRecords = 0;
            ReliableRecords = 0;
            DeferredRecords = 0;
            UnreliablePackets = 0;
            ReliablePackets = 0;
            OversizeReliable = 0;
            TooBigSkipped = 0;
            SendErrors = 0;
            ReliableSendFailures = 0;
            BytesOut = 0;
            WithheldOwner = 0;
            StuckReliable = 0;
            HzClamped = 0;
            PairsVisited = 0;
            WriteTicks = 0;
            FanoutTicks = 0;
            SendTicks = 0;
            FullPayloads = 0;
            FullPayloadBytes = 0;
            FullPayloadMin = int.MaxValue;
            FullPayloadMax = 0;
            CompactPayloads = 0;
            CompactPayloadBytes = 0;
            CompactPayloadMin = int.MaxValue;
            CompactPayloadMax = 0;
            TakeCount++;
        }
    }
}
