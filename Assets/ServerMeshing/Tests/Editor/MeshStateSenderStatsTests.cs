using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshStateSenderStatsTests
    {
        private const long TicksPerSecond = 1_000_000;
        private const string SampleLine = "Sender window 5.00 s: on sources 100 kit 100 direct 0 pairs 5000 clients 50 | builds 100 skipped 0 rewraps 0 refusals 0 | writes 10000 compact 0 false 0 empty 0 errors 0 dropped 0 | records u 499700 r 300 deferred 0 | packets u 15000 r 300 oversize 0 tooBig 0 sendErrors 0 reliableFailed 0 | stuckReliable 0 withheldOwner 0 | hz 20:5000 10:0 5:0 2:0 1:0 0:0 withheld 0 clamped 0 | out 2048.0 kB/s 3060.0 pkt/s per client 41.0 kB/s 61.2 pkt/s | ms/s build 3.20 fanout 4.10 send 6.00 total 13.30 | us/packet 1.96 | build p95 0.20 max 0.50 ms | gc0 0";
        private const string ZerosLine = "Sender window 5.00 s: off sources 0 kit 0 direct 0 pairs 0 clients 0 | builds 0 skipped 0 rewraps 0 refusals 0 | writes 0 compact 0 false 0 empty 0 errors 0 dropped 0 | records u 0 r 0 deferred 0 | packets u 0 r 0 oversize 0 tooBig 0 sendErrors 0 reliableFailed 0 | stuckReliable 0 withheldOwner 0 | hz 20:0 10:0 5:0 2:0 1:0 0:0 withheld 0 clamped 0 | out 0.0 kB/s 0.0 pkt/s per client 0.0 kB/s 0.0 pkt/s | ms/s build 0.00 fanout 0.00 send 0.00 total 0.00 | us/packet 0.00 | build p95 0.00 max 0.00 ms | gc0 0";

        private static MeshStateFanoutCounters SampleCounters()
        {
            MeshStateFanoutCounters c = new MeshStateFanoutCounters
            {
                Pairs = 5000,
                Builds = 100,
                Writes = 10000,
                UnreliableRecords = 499700,
                ReliableRecords = 300,
                UnreliablePackets = 15000,
                ReliablePackets = 300,
                BytesOut = 2048L * 1024 * 5,
                WriteTicks = 16000,
                FanoutTicks = 20500,
                SendTicks = 30000,
            };
            c.PairsByHz[0] = 5000;
            return c;
        }

        private static MeshStateSenderStats SampleStats()
        {
            MeshStateSenderStats stats = new MeshStateSenderStats();
            for (int i = 0; i < 95; ++i)
                stats.AddBuild(0.19);
            for (int i = 0; i < 5; ++i)
                stats.AddBuild(0.5);
            return stats;
        }

        [Test]
        public void ToLine_NullCounters_ThrowsNamingThem()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshStateSenderStats().ToLine(5d, MeshStateSenderSetting.On, 0, 0, 0, 0, 0, null, TicksPerSecond, 0));

            Assert.That(thrown.ParamName, Is.EqualTo("c"));
        }

        [Test]
        public void ToLine_NegativeAndNonFiniteFigures_PrintAsZero()
        {
            MeshStateFanoutCounters c = new MeshStateFanoutCounters { Builds = -4, BytesOut = -100, SendTicks = -9, UnreliablePackets = -1 };
            c.PairsByHz[2] = -3;
            MeshStateSenderStats stats = new MeshStateSenderStats();
            stats.AddBuild(double.NaN);
            stats.AddBuild(-2d);

            string line = stats.ToLine(double.NaN, MeshStateSenderSetting.On, -1, -2, -3, -4, -5, c, -1, -6);

            Assert.That(line, Does.StartWith("Sender window 0.00 s: on sources 0 kit 0 direct 0 pairs 0 clients 0 | builds 0 "));
            Assert.That(line, Does.Not.Contain("-"));
            Assert.That(line, Does.Not.Contain("NaN"));
            Assert.That(line, Does.Not.Contain("{"));
            Assert.That(line, Does.Not.Contain("Exception"));
            Assert.That(line, Does.EndWith("| build p95 0.00 max 0.00 ms | gc0 0"));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_Sample_IsExactUnderAGermanCulture()
        {
            string line = SampleStats().ToLine(5d, MeshStateSenderSetting.On, 100, 0, 50, 0, 0, SampleCounters(), TicksPerSecond, 0);

            Assert.That(line, Is.EqualTo(SampleLine));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_NothingCounted_IsTheZerosLine()
        {
            string line = new MeshStateSenderStats().ToLine(5d, MeshStateSenderSetting.Off, 0, 0, 0, 0, 0, new MeshStateFanoutCounters(), TicksPerSecond, 0);

            Assert.That(line, Is.EqualTo(ZerosLine));
        }

        [Test]
        public void ToLine_MeasureMode_SaysMeasure()
        {
            string line = new MeshStateSenderStats().ToLine(5d, MeshStateSenderSetting.Measure, 3, 0, 1, 0, 0, new MeshStateFanoutCounters(), TicksPerSecond, 0);

            Assert.That(line, Does.StartWith("Sender window 5.00 s: measure sources 3 kit 3 direct 0 pairs 0 clients 1 | "));
        }

        [Test]
        public void ToLine_PrintsEveryCounterInItsPlace()
        {
            MeshStateFanoutCounters c = new MeshStateFanoutCounters
            {
                Builds = 1, Skipped = 2, Writes = 3, CompactWrites = 4, NotWritten = 5, EmptyWrites = 6, WriteErrors = 7, AppendOnlyDrops = 8, IdentityDrops = 1,
                UnreliableRecords = 10, ReliableRecords = 11, DeferredRecords = 12, UnreliablePackets = 13, ReliablePackets = 14, OversizeReliable = 15,
                TooBigSkipped = 16, SendErrors = 17, ReliableSendFailures = 18, StuckReliable = 19, WithheldOwner = 20, PairsWithheld = 21, HzClamped = 22, Pairs = 23,
            };
            for (int i = 0; i < MeshStateFanoutCounters.HzSlots; ++i)
                c.PairsByHz[i] = 30 + i;

            string line = new MeshStateSenderStats().ToLine(1d, MeshStateSenderSetting.On, 4, 5, 6, 7, 8, c, TicksPerSecond, 9);

            Assert.That(line, Does.Contain(": on sources 9 kit 4 direct 5 pairs 23 clients 6 | builds 1 skipped 2 rewraps 7 refusals 8 | writes 3 compact 4 false 5 empty 6 errors 7 dropped 9 | records u 10 r 11 deferred 12 | packets u 13 r 14 oversize 15 tooBig 16 sendErrors 17 reliableFailed 18 | stuckReliable 19 withheldOwner 20 | hz 20:30 10:31 5:32 2:33 1:34 0:35 withheld 21 clamped 22 | "));
            Assert.That(line, Does.EndWith("| gc0 9"));
        }

        [Test]
        public void ToLine_EmptiesTheBuildHistogram()
        {
            MeshStateSenderStats stats = SampleStats();
            stats.ToLine(5d, MeshStateSenderSetting.On, 0, 0, 0, 0, 0, new MeshStateFanoutCounters(), TicksPerSecond, 0);

            string next = stats.ToLine(5d, MeshStateSenderSetting.On, 0, 0, 0, 0, 0, new MeshStateFanoutCounters(), TicksPerSecond, 0);

            Assert.That(next, Does.Contain("| build p95 0.00 max 0.00 ms |"));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_WithPayload_AppendsTheGroupAfterGc0()
        {
            MeshStateFanoutCounters c = SampleCounters();
            for (int i = 0; i < 1000; ++i)
                c.AddPayload(false, 56);
            for (int i = 0; i < 4000; ++i)
                c.AddPayload(true, 28);

            string line = SampleStats().ToLine(5d, MeshStateSenderSetting.On, 100, 0, 50, 0, 0, c, TicksPerSecond, 0, true);

            Assert.That(line, Is.EqualTo(SampleLine + " | payload full n 1000 min 56 max 56 compact n 4000 min 28 max 28"));
        }

        [Test]
        public void ToLine_WithPayloadAndNothingStored_PrintsZeroMinimums()
        {
            string line = new MeshStateSenderStats().ToLine(5d, MeshStateSenderSetting.Off, 0, 0, 0, 0, 0, new MeshStateFanoutCounters(), TicksPerSecond, 0, true);

            Assert.That(line, Is.EqualTo(ZerosLine + " | payload full n 0 min 0 max 0 compact n 0 min 0 max 0"));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_WithoutPayload_IsTheOldLine()
        {
            MeshStateFanoutCounters c = SampleCounters();
            c.AddPayload(false, 56);

            Assert.That(SampleStats().ToLine(5d, MeshStateSenderSetting.On, 100, 0, 50, 0, 0, c, TicksPerSecond, 0, false), Is.EqualTo(SampleLine));
            Assert.That(SampleStats().ToLine(5d, MeshStateSenderSetting.On, 100, 0, 50, 0, 0, c, TicksPerSecond, 0), Is.EqualTo(SampleLine));
        }

        [Test]
        public void TakeAndReset_NullInto_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshStateFanoutCounters().TakeAndReset(null));

            Assert.That(thrown.ParamName, Is.EqualTo("into"));
        }

        [Test]
        public void TakeAndReset_CopiesEverything_ZeroesTheTotals_AndKeepsTheGauges()
        {
            MeshStateFanoutCounters live = SampleCounters();
            live.Connections = 50;
            live.PairsWithheld = 2;
            MeshStateFanoutCounters taken = new MeshStateFanoutCounters();

            live.TakeAndReset(taken);

            Assert.That(taken.Builds, Is.EqualTo(100));
            Assert.That(taken.UnreliableRecords, Is.EqualTo(499700));
            Assert.That(taken.SendTicks, Is.EqualTo(30000));
            Assert.That(taken.PairsByHz[0], Is.EqualTo(5000));
            Assert.That(taken.Connections, Is.EqualTo(50));
            Assert.That(live.Builds, Is.EqualTo(0));
            Assert.That(live.UnreliableRecords, Is.EqualTo(0));
            Assert.That(live.SendTicks, Is.EqualTo(0));
            Assert.That(live.Pairs, Is.EqualTo(5000));
            Assert.That(live.PairsByHz[0], Is.EqualTo(5000));
            Assert.That(live.PairsWithheld, Is.EqualTo(2));
            Assert.That(live.TakeCount, Is.EqualTo(1));
        }

        private static IEnumerable<TestCaseData> SettingCases()
        {
            yield return new TestCaseData(null, MeshStateSenderSetting.Off).SetName("TryParseSetting_Null_IsOff");
            yield return new TestCaseData("", MeshStateSenderSetting.Off).SetName("TryParseSetting_Empty_IsOff");
            yield return new TestCaseData("off", MeshStateSenderSetting.Off).SetName("TryParseSetting_Off_IsOff");
            yield return new TestCaseData(" Measure ", MeshStateSenderSetting.Measure).SetName("TryParseSetting_Measure_IsMeasure");
            yield return new TestCaseData("on", MeshStateSenderSetting.On).SetName("TryParseSetting_On_IsOn");
        }

        [TestCaseSource(nameof(SettingCases))]
        public void TryParseSetting_KnownValues_ParseWithoutAWarning(string value, MeshStateSenderSetting expected)
        {
            bool parsed = MeshStateSender.TryParseSetting(value, out MeshStateSenderSetting setting, out string warning);

            Assert.That(parsed, Is.True);
            Assert.That(setting, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [Test]
        public void TryParseSetting_Junk_WarnsNamingTheKey()
        {
            bool parsed = MeshStateSender.TryParseSetting("{junk}", out MeshStateSenderSetting setting, out string warning);

            Assert.That(parsed, Is.False);
            Assert.That(setting, Is.EqualTo(MeshStateSenderSetting.Off));
            Assert.That(warning, Does.Contain(MeshStateSender.EnvironmentKey));
            Assert.That(warning, Does.Not.Contain("{"));
        }

        public delegate bool SubKeyParser(string value, out string warning);

        private static IEnumerable<TestCaseData> SubKeyCases()
        {
            yield return new TestCaseData(MeshStateSender.SourcesKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseSources(v, out _, out _, out w))).SetName("TryParseSources_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.TiersKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseTiers(v, out _, out _, out _, out _, out w))).SetName("TryParseTiers_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.R0Key, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseR0(v, out _, out w))).SetName("TryParseR0_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.CompactKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseCompact(v, out _, out w))).SetName("TryParseCompact_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.SpreadKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseSwitch(MeshStateSender.SpreadKey, v, out _, out w))).SetName("TryParseSwitch_Spread_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.HorizontalKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseSwitch(MeshStateSender.HorizontalKey, v, out _, out w))).SetName("TryParseSwitch_Horizontal_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.ClassifyKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParseClassifyBuilds(v, out _, out w))).SetName("TryParseClassifyBuilds_UnsetAndJunk");
            yield return new TestCaseData(MeshStateSender.PacketBytesKey, (SubKeyParser)((string v, out string w) => MeshStateSender.TryParsePacketBytes(v, out _, out w))).SetName("TryParsePacketBytes_UnsetAndJunk");
        }

        [TestCaseSource(nameof(SubKeyCases))]
        public void SubKeyParsers_UnsetIsQuiet_AndJunkWarnsNamingTheKey(string key, SubKeyParser parse)
        {
            Assert.That(parse(null, out string nullWarning), Is.True);
            Assert.That(nullWarning, Is.Null);
            Assert.That(parse("", out string emptyWarning), Is.True);
            Assert.That(emptyWarning, Is.Null);

            Assert.That(parse("junk", out string junkWarning), Is.False);
            Assert.That(junkWarning, Does.Contain(key));
            Assert.That(junkWarning, Does.StartWith("State sender: "));
        }

        [Test]
        public void SubKeyParsers_GoodValues_AreRead()
        {
            Assert.That(MeshStateSender.TryParseSources("direct", out bool kit, out bool direct, out _), Is.True);
            Assert.That(kit, Is.False);
            Assert.That(direct, Is.True);
            Assert.That(MeshStateSender.TryParseTiers("20/10/5", out byte near, out byte mid, out byte far, out byte owner, out _), Is.True);
            Assert.That(new[] { near, mid, far, owner }, Is.EqualTo(new byte[] { 20, 10, 5, 20 }));
            Assert.That(MeshStateSender.TryParseR0("120.5", out float r0, out _), Is.True);
            Assert.That(r0, Is.EqualTo(120.5f));
            Assert.That(MeshStateSender.TryParseCompact("far", out bool compactFar, out _), Is.True);
            Assert.That(compactFar, Is.True);
            Assert.That(MeshStateSender.TryParseSwitch(MeshStateSender.SpreadKey, "1", out bool spread, out _), Is.True);
            Assert.That(spread, Is.True);
            Assert.That(MeshStateSender.TryParseClassifyBuilds("10", out int classify, out _), Is.True);
            Assert.That(classify, Is.EqualTo(10));
            Assert.That(MeshStateSender.TryParsePacketBytes("510", out int packetBytes, out _), Is.True);
            Assert.That(packetBytes, Is.EqualTo(510));
        }

        [Test]
        public void CleanForLog_TurnsBracesAndTheWordIntoSafeText()
        {
            Assert.That(MeshStateSender.CleanForLog("a {b} InvalidOperationException"), Is.EqualTo("a (b) InvalidOperationerror"));
            Assert.That(MeshStateSender.CleanForLog(null), Is.EqualTo(string.Empty));
        }
    }
}
