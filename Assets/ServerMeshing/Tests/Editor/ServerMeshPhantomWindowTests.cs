using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomWindowTests
    {
        private const string ZeroLine = "Phantom window 0.00 s: bound virtual 0 sink 0 ready 0 filler 0 eyeSeen 0 | step 0/0 target 0 | vmove n 0 mean 0.0 p95 0 min 0 | uploads 0.0/s | posErr n 0 p50 0.00 p95 0.00 max 0.00 m | down n 0 mean 0.0 p95 0.0 max 0.0 kB/s | swallowed ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 0 0 | frames 0 rig feeder 0.00 binder 0.00 sampler 0.00 ms/s 0.000 ms/frame";
        private const string FullLine = "Phantom window 5.00 s: bound virtual 200 sink 0 ready 200 filler 2 eyeSeen 0 | step 0/0 target 200 | vmove n 20 mean 100.0 p95 100 min 99 | uploads 4000.0/s | posErr n 100 p50 0.50 p95 0.95 max 1.00 m | down n 4 mean 25.0 p95 40.0 max 40.0 kB/s | swallowed ru 0 0 seq 0 0 ro 1000 56000 rs 0 0 u 60000 56000000 other 0 0 | frames 150 rig feeder 1.20 binder 0.10 sampler 0.05 ms/s 0.045 ms/frame";

        private static ServerMeshPhantomWindow FullWindow()
        {
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow
            {
                Seconds = 5.0,
                BoundVirtual = 200,
                Ready = 200,
                Filler = 2,
                Target = 200,
                Uploads = 20000,
                Frames = 150,
                FeederMs = 6.0,
                BinderMs = 0.5,
                SamplerMs = 0.25,
            };
            for (int i = 0; i < 18; ++i)
                window.AddVMove(100);
            window.AddVMove(101);
            window.AddVMove(99);
            for (int i = 100; i >= 1; --i)
                window.AddPosErr(i / 100f);
            window.AddDown(40.0);
            window.AddDown(10.0);
            window.AddDown(30.0);
            window.AddDown(20.0);
            window.SetSwallowed(2, 1000, 56000);
            window.SetSwallowed(4, 60000, 56000000);
            return window;
        }

        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("NaN").And.Not.Contain("\n"));
            Assert.That(line, Does.Not.StartWith("Work window"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_NegativeAndNonFiniteValues_PrintAsZero()
        {
            ServerMeshPhantomWindow window = new ServerMeshPhantomWindow
            {
                Seconds = double.NaN,
                BoundVirtual = -1,
                Frames = -3,
                FeederMs = double.PositiveInfinity,
                BinderMs = -2.0,
                SamplerMs = double.NaN,
                Uploads = -10,
            };
            window.SetSwallowed(0, -5, -6);

            string line = window.ToLine();

            Assert.That(line, Is.EqualTo(ZeroLine));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_Empty_IsTheZerosLine()
        {
            Assert.That(new ServerMeshPhantomWindow().ToLine(), Is.EqualTo(ZeroLine));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_ExactLine()
        {
            string line = FullWindow().ToLine();

            Assert.That(line, Is.EqualTo(FullLine));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_FightSet_PrintsFightingAfterEyeSeen()
        {
            ServerMeshPhantomWindow window = FullWindow();
            window.ShowFighting = true;
            window.Fighting = 3;
            window.StepIndex = 2;
            window.StepCount = 3;

            string line = window.ToLine();

            Assert.That(line, Does.Contain("filler 2 eyeSeen 0 fighting 3 | step 2/3 target 200 |"));
        }

        [Test]
        public void Reset_EmptiesTheWindow_ButKeepsShowFighting()
        {
            ServerMeshPhantomWindow window = FullWindow();
            window.ShowFighting = true;

            window.Reset();

            Assert.That(window.ToLine(), Is.EqualTo(ZeroLine.Replace("eyeSeen 0 |", "eyeSeen 0 fighting 0 |")));
            Assert.That(window.VMoveSamples + window.PosErrSamples + window.DownSamples, Is.EqualTo(0));
        }

        private static IEnumerable<TestCaseData> MoverCases()
        {
            yield return new TestCaseData(new uint[] { }, new uint[] { 1, 2 }, 1u, 0).SetName("CountMovers_NoSubscriptions_Zero");
            yield return new TestCaseData(new uint[] { 1 }, new uint[] { 1 }, 1u, 0).SetName("CountMovers_OnlyItsOwnObject_Zero");
            yield return new TestCaseData(new uint[] { 1, 2, 3, 4, 5 }, new uint[] { 1, 2, 3, 9 }, 1u, 2).SetName("CountMovers_ExcludesSelfAndObjectsWithoutHandlers");
            yield return new TestCaseData(new uint[] { 2, 3, 4 }, new uint[] { 2, 3, 4 }, 1u, 3).SetName("CountMovers_EyeOwnObjectHasNoHandler_CountsAll");
        }

        [TestCaseSource(nameof(MoverCases))]
        public void CountMovers(uint[] subscribing, uint[] handlerIds, uint own, int expected)
        {
            HashSet<uint> ids = new HashSet<uint>(subscribing);
            Dictionary<uint, int> handlers = new Dictionary<uint, int>();
            foreach (uint id in handlerIds)
                handlers[id] = 0;

            Assert.That(ServerMeshPhantomWindow.CountMovers(ids.GetEnumerator(), handlers, own), Is.EqualTo(expected));
        }

        [Test]
        public void CountMovers_NullHandlers_Throws()
        {
            Assert.That(Assert.Throws<ArgumentNullException>(() => ServerMeshPhantomWindow.CountMovers<int>(new HashSet<uint>().GetEnumerator(), null, 1)).ParamName, Is.EqualTo("handlers"));
        }

        private static IEnumerable<TestCaseData> SeenCases()
        {
            yield return new TestCaseData(0, false, false).SetName("IsSeenByOthers_NoSubscribers_False");
            yield return new TestCaseData(1, true, false).SetName("IsSeenByOthers_OnlyItsOwner_False");
            yield return new TestCaseData(1, false, true).SetName("IsSeenByOthers_OneOtherSubscriber_True");
            yield return new TestCaseData(2, true, true).SetName("IsSeenByOthers_OwnerAndAnother_True");
        }

        [TestCaseSource(nameof(SeenCases))]
        public void IsSeenByOthers(int subscribers, bool ownerSubscribes, bool expected)
        {
            Assert.That(ServerMeshPhantomWindow.IsSeenByOthers(subscribers, ownerSubscribes), Is.EqualTo(expected));
        }

        [Test]
        public void RotatingSample_TakesATenth_AndCoversEveryPhantomInTenRounds()
        {
            Assert.That(ServerMeshPhantomWindow.SampleSize(0), Is.EqualTo(0));
            Assert.That(ServerMeshPhantomWindow.SampleSize(5), Is.EqualTo(1));
            Assert.That(ServerMeshPhantomWindow.SampleSize(200), Is.EqualTo(20));
            Assert.That(ServerMeshPhantomWindow.SampleStart(3, 0), Is.EqualTo(0));
            HashSet<int> covered = new HashSet<int>();
            for (int round = 0; round < 10; ++round)
            {
                int start = ServerMeshPhantomWindow.SampleStart(round, 200);
                for (int i = 0; i < 20; ++i)
                    covered.Add((start + i) % 200);
            }
            Assert.That(covered.Count, Is.EqualTo(200));
            Assert.That(ServerMeshPhantomWindow.SampleStart(10, 200), Is.EqualTo(0));
        }

        [Test]
        public void Percentile_IsNearestRank()
        {
            List<int> values = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            Assert.That(ServerMeshPhantomWindow.Percentile(values, 0.5), Is.EqualTo(5));
            Assert.That(ServerMeshPhantomWindow.Percentile(values, 0.95), Is.EqualTo(10));
            Assert.That(ServerMeshPhantomWindow.Percentile(new List<int>(), 0.95), Is.EqualTo(0));
        }

        [Test]
        [SetCulture("de-DE")]
        public void BotNetWindowLine_ExactLine()
        {
            string line = ServerMeshAutoClient.BotNetWindowLine(5.0, 267776, 600, 15872, 110, 0, 1, 200, new DateTime(2026, 10, 1, 12, 0, 5, DateTimeKind.Utc));

            Assert.That(line, Is.EqualTo("Bot net window 5.00 s: in 52.3 kB/s 120 pkt/s out 3.1 kB/s 22 pkt/s | hops 0 | rtt 1 ms | players seen 200 | at 2026-10-01T12:00:05Z"));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void BotNetWindowLine_NoLengthOrNegatives_PrintAsZero()
        {
            string line = ServerMeshAutoClient.BotNetWindowLine(double.NaN, -5, 7, 9, -1, -2, -3, -4, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            Assert.That(line, Is.EqualTo("Bot net window 0.00 s: in 0.0 kB/s 0 pkt/s out 0.0 kB/s 0 pkt/s | hops 0 | rtt 0 ms | players seen 0 | at 2026-01-02T03:04:05Z"));
        }
    }
}
