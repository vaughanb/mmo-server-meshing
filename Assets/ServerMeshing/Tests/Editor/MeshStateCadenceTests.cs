using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshStateCadenceTests
    {
        private const long Frequency = 10_000_000;
        private const long Ts = 1_790_000_000_000;

        private MeshFakeIdentities _identities;
        private MeshFakeStateTransport _transport;
        private MeshFakeSendPolicy _policy;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _transport = new MeshFakeStateTransport();
            _policy = new MeshFakeSendPolicy();
            _frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private MeshStateFanout NewFanout()
        {
            MeshStateFanoutSettings settings = new MeshStateFanoutSettings(20, 4, 1020, false, false, false);
            return new MeshStateFanout(_transport, new MeshFakeStatePositions(), _policy, settings, new MeshFakeLog(), null);
        }

        private int Frame(MeshStateFanout fanout, MeshTickScheduleCopy schedule, long frameTicks)
        {
            int ticks = schedule.Update(frameTicks);
            _frame++;
            for (int i = 0; i < ticks; ++i)
                fanout.Build(Ts + _frame, _frame, -1);
            return ticks;
        }

        private int RecordsTo(long connection)
        {
            int records = 0;
            foreach ((long _, LiteNetLib.DeliveryMethod _, byte[] data) in _transport.To(connection))
                records += MeshEntityStatePacketParser.Parse(data).Count;
            return records;
        }

        private static IEnumerable<TestCaseData> FrameRateCases()
        {
            yield return new TestCaseData(30, 1200).SetName("Cadence_30Fps_BuildsOnTwoFramesOfThree_AndPairsGetTheirRates");
            yield return new TestCaseData(60, 1200).SetName("Cadence_60Fps_BuildsOnOneFrameOfThree_AndPairsGetTheirRates");
        }

        [TestCaseSource(nameof(FrameRateCases))]
        public void Cadence_OverAMinute_GivesEachPairItsRate(int fps, int expectedBuilds)
        {
            MeshStateFanout fanout = NewFanout();
            _policy.byConnection[7] = new MeshSendRate(20, 0);
            _policy.byConnection[8] = new MeshSendRate(10, 0);
            _policy.byConnection[9] = new MeshSendRate(5, 0);
            fanout.Register(new MeshFakeStateSource(42, _identities.New(42, 7, 8, 9)), out _);
            MeshTickScheduleCopy schedule = new MeshTickScheduleCopy(20, Frequency);
            long frameTicks = Frequency / fps;
            int framesWithBuild = 0;
            bool firstSendsDone = false;

            for (int f = 0; f < fps * 60; ++f)
            {
                if (Frame(fanout, schedule, frameTicks) > 0)
                    framesWithBuild++;
                if (!firstSendsDone && fanout.Counters.Builds == MeshStateFanoutSettings.FirstSendBuilds)
                {
                    firstSendsDone = true;
                    _transport.sends.Clear();
                }
            }

            long builds = fanout.Counters.Builds;
            long routineBuilds = builds - MeshStateFanoutSettings.FirstSendBuilds;
            Assert.That(builds, Is.InRange(expectedBuilds - 1, expectedBuilds + 1));
            Assert.That(fanout.Counters.Skipped, Is.EqualTo(0));
            Assert.That(framesWithBuild, Is.EqualTo(builds));
            Assert.That(RecordsTo(7), Is.EqualTo(routineBuilds));
            Assert.That(RecordsTo(8), Is.InRange(routineBuilds / 2 - 1, routineBuilds / 2 + 1));
            Assert.That(RecordsTo(9), Is.InRange(routineBuilds / 4 - 1, routineBuilds / 4 + 1));
        }

        private static IEnumerable<TestCaseData> StallCases()
        {
            yield return new TestCaseData(1_500_000L, 2).SetName("Cadence_150MsStall_GivesOneBuildAndTwoSkipped");
            yield return new TestCaseData(2_500_000L, 4).SetName("Cadence_FiveCatchUpTicksInOneFrame_GiveOneBuildAndFourSkipped");
        }

        [TestCaseSource(nameof(StallCases))]
        public void Cadence_ManyTicksInOneFrame_BuildOnce(long frameTicks, int skipped)
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource source = new MeshFakeStateSource(42, _identities.New(42, 7));
            fanout.Register(source, out _);
            MeshTickScheduleCopy schedule = new MeshTickScheduleCopy(20, Frequency);

            int ticks = Frame(fanout, schedule, frameTicks);

            Assert.That(ticks, Is.EqualTo(skipped + 1));
            Assert.That(fanout.Counters.Builds, Is.EqualTo(1));
            Assert.That(fanout.Counters.Skipped, Is.EqualTo(skipped));
            Assert.That(source.writes, Is.EqualTo(1));
        }

        [Test]
        public void Cadence_Divisor4Over400Pairs_SpreadsTheRecordsEvenlyOverTheBuilds()
        {
            MeshStateFanout fanout = NewFanout();
            _transport.countOnly = true;
            _policy.all = new MeshSendRate(5, 0);
            long[] subscribers = new long[20];
            for (int i = 0; i < subscribers.Length; ++i)
                subscribers[i] = 100 + i;
            for (uint id = 1; id <= 20; ++id)
                fanout.Register(new MeshFakeStateSource(id, _identities.New(id, subscribers)), out _);
            for (int i = 0; i < MeshStateFanoutSettings.FirstSendBuilds; ++i)
            {
                _frame++;
                fanout.Build(Ts + _frame, _frame, -1);
            }

            long min = long.MaxValue, max = long.MinValue;
            for (int i = 0; i < 12; ++i)
            {
                long before = fanout.Counters.UnreliableRecords;
                _frame++;
                fanout.Build(Ts + _frame, _frame, -1);
                long records = fanout.Counters.UnreliableRecords - before;
                min = System.Math.Min(min, records);
                max = System.Math.Max(max, records);
            }

            Assert.That(max - min, Is.LessThanOrEqualTo(1));
            Assert.That(min, Is.InRange(99, 101));
        }
    }
}
