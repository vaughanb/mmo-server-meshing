using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshStateFanoutTests
    {
        private const long Ts = 1_790_000_000_000;

        private MeshFakeIdentities _identities;
        private MeshFakeStateTransport _transport;
        private MeshFakeStatePositions _positions;
        private MeshFakeSendPolicy _policy;
        private MeshFakeLog _log;
        private List<(IMeshStateSource source, string reason)> _drops;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _transport = new MeshFakeStateTransport();
            _positions = new MeshFakeStatePositions();
            _policy = new MeshFakeSendPolicy();
            _log = new MeshFakeLog();
            _drops = new List<(IMeshStateSource, string)>();
            _frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private MeshStateFanout NewFanout(int classify = 1000, int cap = 1020, bool spread = false, bool measure = false)
        {
            MeshStateFanoutSettings settings = new MeshStateFanoutSettings(20, classify, cap, spread, measure, false);
            return new MeshStateFanout(_transport, _positions, _policy, settings, _log, (source, reason) => _drops.Add((source, reason)));
        }

        private MeshFakeStateSource Source(MeshStateFanout fanout, uint objectId, params long[] subscribers)
        {
            MeshFakeStateSource source = new MeshFakeStateSource(objectId, _identities.New(objectId, subscribers));
            Assert.That(fanout.Register(source, out string error), Is.True, error);
            return source;
        }

        private MeshKitMovementStateSource Wrapped(MeshStateFanout fanout, MeshFakePassKitHandler kit, params long[] subscribers)
        {
            MeshKitMovementStateSource wrapper = new MeshKitMovementStateSource(kit, _identities.New(kit.ObjectId, subscribers), _ => { });
            Assert.That(fanout.Register(wrapper, out string error), Is.True, error);
            return wrapper;
        }

        private void Build(MeshStateFanout fanout, long host = -1)
        {
            _frame++;
            fanout.Build(Ts + _frame, _frame, host);
        }

        private void Builds(MeshStateFanout fanout, int count)
        {
            for (int i = 0; i < count; ++i)
                Build(fanout);
        }

        private List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> Records(long connection)
        {
            List<(DeliveryMethod, long, uint, byte[])> records = new List<(DeliveryMethod, long, uint, byte[])>();
            foreach ((long _, DeliveryMethod method, byte[] data) in _transport.To(connection))
                foreach ((uint id, int _, byte[] bytes) in MeshEntityStatePacketParser.Parse(data, out long timestamp))
                    records.Add((method, timestamp, id, bytes));
            return records;
        }

        private int Count(long connection, uint objectId, DeliveryMethod? method = null)
        {
            int count = 0;
            foreach ((DeliveryMethod recordMethod, long _, uint id, byte[] _) in Records(connection))
                if (id == objectId && (method == null || recordMethod == method))
                    count++;
            return count;
        }

        private static bool IsTeleport(byte[] kitPayload) => (kitPayload[0] & MeshFakePassKitHandler.IsTeleport) != 0;

        private static IEnumerable<TestCaseData> NullArgumentCases()
        {
            yield return new TestCaseData("transport").SetName("Constructor_NullTransport_ThrowsNamingIt");
            yield return new TestCaseData("positions").SetName("Constructor_NullPositions_ThrowsNamingIt");
            yield return new TestCaseData("policy").SetName("Constructor_NullPolicy_ThrowsNamingIt");
            yield return new TestCaseData("settings").SetName("Constructor_NullSettings_ThrowsNamingIt");
            yield return new TestCaseData("log").SetName("Constructor_NullLog_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(NullArgumentCases))]
        public void Constructor_NullArgument_ThrowsNamingIt(string missing)
        {
            MeshStateFanoutSettings settings = new MeshStateFanoutSettings(20, 4, 1020, false, false, false);

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshStateFanout(
                missing == "transport" ? null : _transport,
                missing == "positions" ? null : _positions,
                missing == "policy" ? null : _policy,
                missing == "settings" ? null : settings,
                missing == "log" ? null : _log,
                null));

            Assert.That(thrown.ParamName, Is.EqualTo(missing));
        }

        private static IEnumerable<TestCaseData> SettingsCases()
        {
            yield return new TestCaseData(0, 4, 1020, "buildHz").SetName("Settings_BuildHz0_ThrowsNamingIt");
            yield return new TestCaseData(61, 4, 1020, "buildHz").SetName("Settings_BuildHz61_ThrowsNamingIt");
            yield return new TestCaseData(20, 0, 1020, "classifyEveryBuilds").SetName("Settings_Classify0_ThrowsNamingIt");
            yield return new TestCaseData(20, 1001, 1020, "classifyEveryBuilds").SetName("Settings_Classify1001_ThrowsNamingIt");
            yield return new TestCaseData(20, 4, 63, "maxPacketBytes").SetName("Settings_Packet63_ThrowsNamingIt");
            yield return new TestCaseData(20, 4, 1021, "maxPacketBytes").SetName("Settings_Packet1021_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(SettingsCases))]
        public void Settings_OutOfRange_ThrowsNamingTheField(int buildHz, int classify, int cap, string field)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshStateFanoutSettings(buildHz, classify, cap, false, false, false));

            Assert.That(thrown.ParamName, Is.EqualTo(field));
        }

        [Test]
        public void Register_Null_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => NewFanout().Register(null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("source"));
        }

        [Test]
        public void Register_DuplicateId_IsFalseNamingTheId()
        {
            MeshStateFanout fanout = NewFanout();
            Source(fanout, 42, 7);

            bool registered = fanout.Register(new MeshFakeStateSource(42, _identities.New(42, 7)), out string error);

            Assert.That(registered, Is.False);
            Assert.That(error, Does.Contain("42"));
            Assert.That(fanout.SourceCount, Is.EqualTo(1));
        }

        [Test]
        public void Register_NullIdentity_IsFalseNamingTheId()
        {
            MeshStateFanout fanout = NewFanout();

            bool registered = fanout.Register(new MeshFakeStateSource(42, null), out string error);

            Assert.That(registered, Is.False);
            Assert.That(error, Does.Contain("42"));
            Assert.That(fanout.IsRegistered(42), Is.False);
        }

        [Test]
        public void Unregister_UnknownSourceOrAnotherWithTheSameId_IsFalse()
        {
            MeshStateFanout fanout = NewFanout();
            Source(fanout, 42, 7);

            Assert.That(fanout.Unregister(new MeshFakeStateSource(43, _identities.New(43))), Is.False);
            Assert.That(fanout.Unregister(new MeshFakeStateSource(42, _identities.New(42))), Is.False);
            Assert.That(fanout.IsRegistered(42), Is.True);
        }

        private static IEnumerable<TestCaseData> NoReceiverCases()
        {
            yield return new TestCaseData(new long[0]).SetName("Build_NoSubscribers_NeverWrites");
            yield return new TestCaseData(new long[] { 3 }).SetName("Build_HostOnlySubscriber_NeverWrites");
        }

        [TestCaseSource(nameof(NoReceiverCases))]
        public void Build_NoReceiver_NeverWrites(long[] subscribers)
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource source = Source(fanout, 42, subscribers);

            Build(fanout, host: 3);
            Build(fanout, host: 3);

            Assert.That(source.writes, Is.EqualTo(0));
            Assert.That(_transport.calls, Is.EqualTo(0));
        }

        [Test]
        public void Build_WriteReturnsFalse_SendsNothingAndRecordsNothing()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true, returns = false };
            Wrapped(fanout, kit, 7);

            Build(fanout);

            Assert.That(_transport.calls, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(0));
            Assert.That(fanout.Counters.NotWritten, Is.EqualTo(1));
        }

        [Test]
        public void Build_WriteTrueWithNoBytes_SendsNothingAndRecordsNothing()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeServerStateHandler kit = new MeshFakeServerStateHandler(42);
            fanout.Register(new MeshKitMovementStateSource(kit, _identities.New(42, 7), _ => { }), out _);

            Build(fanout);

            Assert.That(_transport.calls, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(0));
            Assert.That(fanout.Counters.EmptyWrites, Is.EqualTo(1));
        }

        [Test]
        public void Build_WriteThrows_LogsTheIdAndType_OthersUnaffected_SentAgainNextBuild()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource failing = Source(fanout, 42, 7);
            MeshFakeStateSource healthy = Source(fanout, 43, 7);
            failing.throws = true;

            Build(fanout);
            Build(fanout);

            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith("State sender error: object 42 (MeshFakeStateSource) write failed: "));
            Assert.That(_log.errors[0], Does.Not.Contain("Exception"));
            Assert.That(_log.errors[0], Does.Not.Contain("{"));
            Assert.That(fanout.Counters.WriteErrors, Is.EqualTo(2));
            Assert.That(Count(7, 42), Is.EqualTo(0));
            Assert.That(Count(7, 43), Is.EqualTo(2));

            failing.throws = false;
            Build(fanout);

            Assert.That(Count(7, 42), Is.EqualTo(1));
            Assert.That(healthy.writes, Is.EqualTo(3));
        }

        [Test]
        public void Build_KitWriteThrows_IsNotRecorded_AndItsFlagGoesOutOnceItWrites()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true, throws = true };
            Wrapped(fanout, kit, 7);

            Build(fanout);

            Assert.That(kit.consumes, Is.EqualTo(0));

            kit.throws = false;
            Build(fanout);

            Assert.That(kit.consumes, Is.EqualTo(1));
            Assert.That(Count(7, 42, DeliveryMethod.ReliableOrdered), Is.EqualTo(1));
        }

        private static IEnumerable<TestCaseData> AppendOnlyCases()
        {
            yield return new TestCaseData(true).SetName("Build_SourceResetsTheWriter_IsDroppedNamingIt");
            yield return new TestCaseData(false).SetName("Build_SourceRewindsTheWriter_IsDroppedNamingIt");
        }

        [TestCaseSource(nameof(AppendOnlyCases))]
        public void Build_SourceBreaksAppendOnly_IsDroppedNamingIt(bool reset)
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource before = Source(fanout, 41, 7);
            MeshFakeStateSource breaking = Source(fanout, 42, 7);
            MeshFakeStateSource after = Source(fanout, 43, 7);
            before.payload = new byte[] { 1, 2, 3, 4 };
            after.payload = new byte[] { 5, 6 };
            breaking.resetWriter = reset;
            breaking.rewindWriter = !reset;

            Build(fanout);
            Build(fanout);

            Assert.That(_drops.Count, Is.EqualTo(1));
            Assert.That(_drops[0].source, Is.SameAs(breaking));
            Assert.That(_drops[0].reason, Is.EqualTo(MeshStateFanout.DropReasonAppendOnly));
            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith("State sender error: object 42 "));
            Assert.That(fanout.IsRegistered(42), Is.False);
            Assert.That(breaking.writes, Is.EqualTo(1));
            Assert.That(fanout.Counters.AppendOnlyDrops, Is.EqualTo(1));
            List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(7);
            Assert.That(records.Count, Is.EqualTo(4));
            foreach ((DeliveryMethod _, long _, uint id, byte[] bytes) in records)
            {
                Assert.That(id, Is.Not.EqualTo(42u));
                Assert.That(bytes, Is.EqualTo(id == 41 ? before.payload : after.payload));
            }
        }

        [Test]
        public void Build_DestroyedIdentity_IsDroppedThroughTheCallback()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource source = Source(fanout, 42, 7);
            Source(fanout, 43, 7);
            _identities.Destroy(source.Identity);

            Build(fanout);

            Assert.That(_drops.Count, Is.EqualTo(1));
            Assert.That(_drops[0].source, Is.SameAs(source));
            Assert.That(_drops[0].reason, Is.EqualTo(MeshStateFanout.DropReasonIdentityGone));
            Assert.That(_log.warnings, Has.Some.Contains("object 42"));
            Assert.That(fanout.IsRegistered(42), Is.False);
            Assert.That(source.writes, Is.EqualTo(0));
            Assert.That(Count(7, 43), Is.EqualTo(1));
        }

        [Test]
        public void Build_ReliableSendThrows_SkipsTheConsume_AndTheNextBuildSendsTheFlagToAllAndConsumes()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            Wrapped(fanout, kit, 7, 8);
            _transport.fault = (connection, method, call) => method == DeliveryMethod.ReliableOrdered && call == 1 ? new InvalidOperationException("the link failed on purpose") : null;

            Build(fanout);

            Assert.That(kit.consumes, Is.EqualTo(0));
            Assert.That(fanout.Counters.ReliableSendFailures, Is.EqualTo(1));
            Assert.That(fanout.Counters.SendErrors, Is.EqualTo(1));

            _transport.sends.Clear();
            Build(fanout);

            Assert.That(kit.consumes, Is.EqualTo(1));
            foreach (long connection in new long[] { 7, 8 })
            {
                List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(1));
                Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
                Assert.That(IsTeleport(records[0].bytes), Is.True);
            }
        }

        [Test]
        public void Build_OversizeReliableSendTooBig_CountsAReliableFailure_AndConsumesNothing()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true, padding = 1500 };
            Wrapped(fanout, kit, 7);
            _transport.fault = (connection, method, call) => method == DeliveryMethod.ReliableOrdered ? new TooBigPacketException("too big on purpose") : null;

            Assert.DoesNotThrow(() => Build(fanout));

            Assert.That(fanout.Counters.OversizeReliable, Is.EqualTo(1));
            Assert.That(fanout.Counters.ReliableSendFailures, Is.EqualTo(1));
            Assert.That(fanout.Counters.SendErrors, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(0));
        }

        [Test]
        public void Build_UnreliableSendThrows_CountsASendError_AndStillConsumes()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            Wrapped(fanout, kit, 7);
            Source(fanout, 43, 7);
            _transport.fault = (connection, method, call) => method == DeliveryMethod.Unreliable ? new InvalidOperationException("the link failed {on purpose}") : null;

            Build(fanout);

            Assert.That(fanout.Counters.SendErrors, Is.EqualTo(1));
            Assert.That(fanout.Counters.ReliableSendFailures, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(1));
            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith("State sender error: "));
            Assert.That(_log.errors[0], Does.Not.Contain("{"));
        }

        private static IEnumerable<TestCaseData> ThrowingBuildCases()
        {
            yield return new TestCaseData(true).SetName("Build_PositionsThrowMidway_ConsumesNothing_SendsNothingLater_NextBuildReachesAll");
            yield return new TestCaseData(false).SetName("Build_PolicyThrows_ConsumesNothing_SendsNothingLater_NextBuildReachesAll");
        }

        [TestCaseSource(nameof(ThrowingBuildCases))]
        public void Build_ThrowingMidway_LeavesNothingBehind(bool positionsThrow)
        {
            MeshStateFanout fanout = NewFanout(spread: true);
            Source(fanout, 41, 7, 8);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            Wrapped(fanout, kit, 7, 8);
            _positions.anchors[7] = Vector3.zero;
            _positions.anchors[8] = Vector3.zero;
            _positions.throws = positionsThrow;
            _positions.throwOnlyFor = 42;
            _policy.throws = !positionsThrow;
            int frame = _frame + 1;

            Assert.Throws<InvalidOperationException>(() => fanout.Build(Ts, frame, -1));
            _frame = frame;

            Assert.That(_transport.calls, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(0));
            Assert.That(fanout.HasDeferred, Is.False);

            _positions.throws = false;
            _policy.throws = false;
            fanout.FlushDeferred(frame + 1);
            Assert.That(_transport.calls, Is.EqualTo(0));
            Build(fanout);

            Assert.That(kit.consumes, Is.EqualTo(1));
            foreach (long connection in new long[] { 7, 8 })
            {
                List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(2));
                foreach ((DeliveryMethod method, long timestamp, uint id, byte[] bytes) in records)
                {
                    Assert.That(timestamp, Is.Not.EqualTo(Ts));
                    if (id == 42)
                    {
                        Assert.That(method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
                        Assert.That(IsTeleport(bytes), Is.True);
                    }
                }
            }
        }

        [Test]
        public void Build_WithholdOnANonOwner_SendsNothingEvenReliable_AndStillConsumes()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = MeshSendRate.Withhold;
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            Wrapped(fanout, kit, 7, 8);

            Build(fanout);

            Assert.That(kit.pendingTeleport, Is.False);

            Builds(fanout, 2);

            Assert.That(_transport.calls, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(3));
            Assert.That(fanout.Counters.PairsWithheld, Is.EqualTo(2));
        }

        [Test]
        public void Build_PolicyHz3_IsSentAt2Hz_LoggedOnce()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = new MeshSendRate(3, 0);
            Source(fanout, 42, 7);

            Builds(fanout, 3);
            _transport.sends.Clear();
            Builds(fanout, 20);

            Assert.That(Count(7, 42), Is.EqualTo(2));
            Assert.That(fanout.Counters.HzClamped, Is.EqualTo(1));
            Assert.That(fanout.Counters.PairsByHz[3], Is.EqualTo(1));
            Assert.That(_log.warnings.Count, Is.EqualTo(1));
            Assert.That(_log.warnings[0], Does.Contain("3 Hz"));
        }

        [Test]
        public void Build_UnreliableRecordAboveMaxPayload_IsSkippedAndCounted_OthersSent()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource big = Source(fanout, 42, 7);
            Source(fanout, 43, 7);
            big.payload = new byte[1500];

            Build(fanout);
            Build(fanout);

            Assert.That(Count(7, 42), Is.EqualTo(0));
            Assert.That(Count(7, 43), Is.EqualTo(2));
            Assert.That(fanout.Counters.TooBigSkipped, Is.EqualTo(2));
            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith("State sender error: object 42 "));
        }

        [Test]
        public void Build_UnreliableSendTooBig_IsCountedAndNotRethrown()
        {
            MeshStateFanout fanout = NewFanout();
            Source(fanout, 42, 7);
            _transport.fault = (connection, method, call) => new TooBigPacketException("too big on purpose");

            Assert.DoesNotThrow(() => Build(fanout));

            Assert.That(fanout.Counters.TooBigSkipped, Is.EqualTo(1));
            Assert.That(fanout.Counters.UnreliablePackets, Is.EqualTo(0));
        }

        private MeshStateFanout DeferringFanout(out MeshFakeStateSource first, out MeshFakeStateSource deferred)
        {
            MeshStateFanout fanout = NewFanout(spread: true);
            first = Source(fanout, 41, 7);
            deferred = Source(fanout, 42, 7);
            Builds(fanout, 3);
            _transport.sends.Clear();
            Build(fanout);
            Assert.That(fanout.HasDeferred, Is.True);
            Assert.That(Count(7, 41), Is.EqualTo(1));
            Assert.That(Count(7, 42), Is.EqualTo(0));
            _transport.sends.Clear();
            return fanout;
        }

        [Test]
        public void FlushDeferred_SourceUnregisteredAndItsIdTakenAgain_SendsNothing()
        {
            MeshStateFanout fanout = DeferringFanout(out _, out MeshFakeStateSource deferred);
            Assert.That(fanout.Unregister(deferred), Is.True);
            MeshFakeStateSource replacement = Source(fanout, 42, 7);

            fanout.FlushDeferred(_frame + 1);

            Assert.That(_transport.sends, Is.Empty);
            Assert.That(replacement.writes, Is.EqualTo(0));
            Assert.That(fanout.HasDeferred, Is.False);
        }

        [Test]
        public void FlushDeferred_ConnectionForgotten_SendsNothing()
        {
            MeshStateFanout fanout = DeferringFanout(out _, out _);
            fanout.ForgetConnection(7);

            fanout.FlushDeferred(_frame + 1);

            Assert.That(_transport.sends, Is.Empty);
        }

        [Test]
        public void ForgetConnection_TheIdComesBack_StartsANewPairWithItsFirstSends()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = new MeshSendRate(0, 0);
            Source(fanout, 42, 7);
            Builds(fanout, 4);
            Assert.That(Count(7, 42), Is.EqualTo(3));
            _transport.sends.Clear();

            fanout.ForgetConnection(7);
            Builds(fanout, 4);

            Assert.That(Count(7, 42), Is.EqualTo(3));
        }

        [Test]
        public void Build_WithholdOnTheOwner_DeliversItsReliableRecord_WithOneWarningForTwoBuilds()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = MeshSendRate.Withhold;
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            MeshKitMovementStateSource wrapper = Wrapped(fanout, kit, 7, 8);
            MeshFakeIdentities.SetOwner(wrapper.Identity, 7);

            Build(fanout);
            Build(fanout);

            List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> owner = Records(7);
            Assert.That(owner[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
            Assert.That(IsTeleport(owner[0].bytes), Is.True);
            Assert.That(owner.Count, Is.EqualTo(2));
            Assert.That(Records(8), Is.Empty);
            Assert.That(_log.warnings.Count, Is.EqualTo(1));
            Assert.That(_log.warnings[0], Does.StartWith("State sender: "));
            Assert.That(fanout.Counters.WithheldOwner, Is.EqualTo(1));
            Assert.That(fanout.Counters.PairsByHz[5], Is.EqualTo(1));
        }

        [Test]
        public void Build_ReliableRecordAboveMaxPayload_GoesAloneInAReliableOrderedPacketThatParses()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource big = Source(fanout, 42, 7);
            Source(fanout, 43, 7).reliable = true;
            big.payload = new byte[2000];
            big.payload[1999] = 0x5A;
            big.reliable = true;

            Build(fanout);

            List<(long connection, DeliveryMethod method, byte[] data)> sends = _transport.To(7);
            Assert.That(sends.Count, Is.EqualTo(2));
            Assert.That(sends[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
            List<(uint id, int length, byte[] bytes)> alone = MeshEntityStatePacketParser.Parse(sends[0].data);
            Assert.That(alone.Count, Is.EqualTo(1));
            Assert.That(alone[0].id, Is.EqualTo(42u));
            Assert.That(alone[0].bytes, Is.EqualTo(big.payload));
            Assert.That(sends[1].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
            Assert.That(sends[1].data.Length, Is.LessThanOrEqualTo(1020));
            Assert.That(fanout.Counters.OversizeReliable, Is.EqualTo(1));
            Assert.That(fanout.Counters.ReliableRecords, Is.EqualTo(2));
        }

        [Test]
        public void Build_ReliableAndUnreliableRecords_GoInSeparatePacketsPerConnection()
        {
            MeshStateFanout fanout = NewFanout();
            Source(fanout, 42, 7).reliable = true;
            Source(fanout, 43, 7);

            Build(fanout);

            List<(long connection, DeliveryMethod method, byte[] data)> sends = _transport.To(7);
            Assert.That(sends.Count, Is.EqualTo(2));
            Assert.That(sends[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
            Assert.That(MeshEntityStatePacketParser.Parse(sends[0].data)[0].id, Is.EqualTo(42u));
            Assert.That(MeshEntityStatePacketParser.Parse(sends[0].data).Count, Is.EqualTo(1));
            Assert.That(sends[1].method, Is.EqualTo(DeliveryMethod.Unreliable));
            Assert.That(MeshEntityStatePacketParser.Parse(sends[1].data)[0].id, Is.EqualTo(43u));
            Assert.That(MeshEntityStatePacketParser.Parse(sends[1].data).Count, Is.EqualTo(1));
        }

        [Test]
        public void Build_FlaggedRecord_ReachesSubscribersAt20_5And1Hz_WhenOnlyOneIsDue()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.byConnection[7] = new MeshSendRate(20, 0);
            _policy.byConnection[8] = new MeshSendRate(5, 0);
            _policy.byConnection[9] = new MeshSendRate(1, 0);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42);
            Wrapped(fanout, kit, 7, 8, 9);
            Builds(fanout, 3);
            int onlyOneDue = -1;
            for (int build = 4; build <= 23; ++build)
            {
                _transport.sends.Clear();
                Build(fanout);
                if (onlyOneDue < 0 && Count(8, 42) == 0 && Count(9, 42) == 0)
                    onlyOneDue = build;
            }
            Assert.That(onlyOneDue, Is.GreaterThan(0));
            for (int build = 24; build < onlyOneDue + 20; ++build)
                Build(fanout);
            _transport.sends.Clear();

            kit.pendingTeleport = true;
            Build(fanout);

            foreach (long connection in new long[] { 7, 8, 9 })
            {
                List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(1), $"connection {connection}");
                Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
                Assert.That(IsTeleport(records[0].bytes), Is.True);
            }
            Assert.That(kit.consumes, Is.EqualTo(onlyOneDue + 20));
            Assert.That(kit.pendingTeleport, Is.False);
        }

        [Test]
        public void Build_OwnerAt1Hz_GetsUrgentRecordsPlusOneRoutineRecordPerSecond()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.hasOwnerRate = true;
            _policy.ownerRate = new MeshSendRate(1, 0);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42);
            MeshKitMovementStateSource wrapper = Wrapped(fanout, kit, 7);
            MeshFakeIdentities.SetOwner(wrapper.Identity, 7);

            Builds(fanout, 3);
            _transport.sends.Clear();
            Builds(fanout, 20);

            Assert.That(Count(7, 42, DeliveryMethod.Unreliable), Is.EqualTo(1));

            _transport.sends.Clear();
            kit.pendingTeleport = true;
            Build(fanout);

            Assert.That(Count(7, 42, DeliveryMethod.ReliableOrdered), Is.EqualTo(1));
        }

        private static IEnumerable<TestCaseData> PopulationCases()
        {
            foreach ((int sources, int observers) in new[] { (1, 1), (3, 2), (50, 100), (400, 500) })
                foreach (bool compact in new[] { false, true })
                    yield return new TestCaseData(sources, observers, compact).SetName($"Build_{sources}x{observers}{(compact ? "Compact" : "")}_WritesEachSourceOncePerBuild");
        }

        [TestCaseSource(nameof(PopulationCases))]
        public void Build_WritesEachSourceAtMostOncePerBuild(int sourceCount, int observers, bool compact)
        {
            MeshStateFanout fanout = NewFanout(classify: 4);
            _transport.countOnly = true;
            if (compact)
                _policy.all = new MeshSendRate(20, 0, MeshStateVariant.Compact);
            long[] subscribers = new long[observers];
            for (int i = 0; i < observers; ++i)
                subscribers[i] = 1000 + i;
            List<MeshFakeStateSource> sources = new List<MeshFakeStateSource>();
            for (uint id = 1; id <= sourceCount; ++id)
            {
                MeshFakeStateSource source = compact ? new MeshFakeCompactStateSource(id, _identities.New(id, subscribers)) : new MeshFakeStateSource(id, _identities.New(id, subscribers));
                fanout.Register(source, out _);
                sources.Add(source);
            }

            Builds(fanout, 4);

            foreach (MeshFakeStateSource source in sources)
            {
                Assert.That(source.writes, Is.EqualTo(4));
                if (source is MeshFakeCompactStateSource compactSource)
                    Assert.That(compactSource.compactWrites, Is.EqualTo(1));
            }
            Assert.That(fanout.Counters.Writes, Is.EqualTo(4L * sourceCount));
            Assert.That(fanout.Counters.UnreliableRecords, Is.EqualTo(4L * sourceCount * observers));
        }

        [Test]
        public void Build_NewPairAtHz0_ReceivesOnItsFirstThreeBuildsOnly()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = new MeshSendRate(0, 0);
            MeshFakeStateSource source = Source(fanout, 42, 7);
            Builds(fanout, 5);
            source.Identity.AddSubscriber(8);

            int[] perBuild = new int[5];
            for (int i = 0; i < perBuild.Length; ++i)
            {
                _transport.sends.Clear();
                Build(fanout);
                perBuild[i] = Count(8, 42);
            }

            Assert.That(perBuild, Is.EqualTo(new[] { 1, 1, 1, 0, 0 }));
            Assert.That(Count(7, 42), Is.EqualTo(0));
        }

        [Test]
        public void Build_DiscreteVersionChange_ReachesEverySubscriberOnThreeBuilds()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = new MeshSendRate(0, 0);
            MeshFakeStateSource source = Source(fanout, 42, 7, 8, 9);
            Builds(fanout, 5);
            int callsBefore = _policy.calls;
            source.DiscreteVersion++;

            int[] perBuild = new int[4];
            for (int i = 0; i < perBuild.Length; ++i)
            {
                _transport.sends.Clear();
                Build(fanout);
                perBuild[i] = Count(7, 42) + Count(8, 42) + Count(9, 42);
            }

            Assert.That(perBuild, Is.EqualTo(new[] { 3, 3, 3, 0 }));
            Assert.That(_policy.calls - callsBefore, Is.EqualTo(3));
        }

        [Test]
        public void Build_UnsubscribedPair_GetsNothing_TheDeferredHalfIncluded()
        {
            MeshStateFanout fanout = DeferringFanout(out MeshFakeStateSource first, out MeshFakeStateSource deferred);
            deferred.Identity.RemoveSubscriber(7);
            first.Identity.RemoveSubscriber(7);

            fanout.FlushDeferred(_frame + 1);
            Build(fanout);

            Assert.That(_transport.sends, Is.Empty);
        }

        [Test]
        public void Spread_TheDeferredHalfNeverHoldsAReliableRecord_KeepsItsTimestamp_AndFlushesBeforeTheNextBuild()
        {
            MeshStateFanout fanout = NewFanout(spread: true);
            Source(fanout, 40, 7);
            Source(fanout, 41, 7);
            Source(fanout, 42, 7);
            Source(fanout, 43, 7).reliable = true;
            Builds(fanout, 3);
            _transport.sends.Clear();

            Build(fanout);
            long deferredBuild = Ts + _frame;
            fanout.FlushDeferred(_frame);

            Assert.That(Count(7, 43, DeliveryMethod.ReliableOrdered), Is.EqualTo(1));
            Assert.That(Count(7, 41), Is.EqualTo(0));
            Assert.That(fanout.HasDeferred, Is.True);
            Assert.That(fanout.Counters.DeferredRecords, Is.EqualTo(1));

            _transport.sends.Clear();
            Build(fanout);

            List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(7);
            Assert.That(records[0].id, Is.EqualTo(41u));
            Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.Unreliable));
            Assert.That(records[0].timestamp, Is.EqualTo(deferredBuild));
            Assert.That(records.Count, Is.EqualTo(4));
        }

        [Test]
        public void FlushDeferred_OnTheNextFrame_SendsTheDeferredHalfWithItsBuildsTimestamp()
        {
            MeshStateFanout fanout = DeferringFanout(out _, out _);
            long deferredBuild = Ts + _frame;

            fanout.FlushDeferred(_frame + 1);

            List<(DeliveryMethod method, long timestamp, uint id, byte[] bytes)> records = Records(7);
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].id, Is.EqualTo(42u));
            Assert.That(records[0].timestamp, Is.EqualTo(deferredBuild));
            Assert.That(fanout.HasDeferred, Is.False);
        }

        [Test]
        public void MarkCalls_ClassifyTheirPairsAgainOnTheNextBuild()
        {
            MeshStateFanout fanout = NewFanout();
            Source(fanout, 41, 7, 8, 9);
            Source(fanout, 42, 7, 8, 9);
            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(6));

            fanout.MarkRelevanceChanged(41, 8);
            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(7));

            fanout.MarkSourceChanged(42);
            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(10));

            fanout.MarkObserverChanged(9);
            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(12));

            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(12));
        }

        [Test]
        public void OwnerFlip_ClassifiesThePairAgainAsOwner()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource source = Source(fanout, 42, 7, 8);
            Build(fanout);
            int callsBefore = _policy.calls;

            MeshFakeIdentities.SetOwner(source.Identity, 8);
            Build(fanout);

            Assert.That(_policy.calls - callsBefore, Is.EqualTo(1));
            Assert.That(_policy.last.ObserverConnectionId, Is.EqualTo(8));
            Assert.That(_policy.last.IsOwner, Is.True);
        }

        [Test]
        public void Classify4Over400Pairs_EachBuildClassifiesAQuarter()
        {
            MeshStateFanout fanout = NewFanout(classify: 4);
            _transport.countOnly = true;
            long[] subscribers = new long[20];
            for (int i = 0; i < subscribers.Length; ++i)
                subscribers[i] = 100 + i;
            for (uint id = 1; id <= 20; ++id)
                Source(fanout, id, subscribers);
            Build(fanout);
            Assert.That(_policy.calls, Is.EqualTo(400));

            for (int build = 0; build < 8; ++build)
            {
                int before = _policy.calls;
                Build(fanout);
                Assert.That(_policy.calls - before, Is.InRange(99, 101));
            }
        }

        [Test]
        public void Gauges_MatchThePolicy()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.byConnection[1] = new MeshSendRate(20, 0);
            _policy.byConnection[2] = new MeshSendRate(10, 0);
            _policy.byConnection[3] = new MeshSendRate(5, 0);
            _policy.byConnection[4] = MeshSendRate.Withhold;
            _policy.byConnection[5] = MeshSendRate.Withhold;
            _policy.byConnection[6] = new MeshSendRate(1, 0);
            MeshFakeStateSource source = Source(fanout, 42, 1, 2, 3, 4, 5, 6);
            MeshFakeIdentities.SetOwner(source.Identity, 5);

            Build(fanout);

            Assert.That(fanout.Counters.PairsByHz, Is.EqualTo(new long[] { 1, 1, 1, 0, 1, 1 }));
            Assert.That(fanout.Counters.PairsWithheld, Is.EqualTo(1));
            Assert.That(fanout.Counters.Pairs, Is.EqualTo(6));
            Assert.That(fanout.Counters.Sources, Is.EqualTo(1));
            Assert.That(fanout.Counters.Connections, Is.EqualTo(6));
        }

        [Test]
        public void Build_SecondCallInTheSameFrame_IsSkippedAndWritesNothing()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource source = Source(fanout, 42, 7);

            fanout.Build(Ts, 5, -1);
            fanout.Build(Ts + 1, 5, -1);

            Assert.That(source.writes, Is.EqualTo(1));
            Assert.That(_transport.calls, Is.EqualTo(1));
            Assert.That(fanout.Counters.Builds, Is.EqualTo(1));
            Assert.That(fanout.Counters.Skipped, Is.EqualTo(1));
        }

        [Test]
        public void Measure_NeverCallsTheTransportOrConsumes_ButCountsRecords()
        {
            MeshStateFanout fanout = NewFanout(measure: true, spread: true);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42) { pendingTeleport = true };
            Wrapped(fanout, kit, 7, 8);
            MeshFakeStateSource big = Source(fanout, 43, 7);
            big.payload = new byte[2000];
            big.reliable = true;
            Source(fanout, 44, 7);
            Source(fanout, 45, 7);

            Builds(fanout, 5);
            fanout.FlushDeferred(_frame + 1);

            Assert.That(_transport.calls, Is.EqualTo(0));
            Assert.That(kit.consumes, Is.EqualTo(0));
            Assert.That(fanout.Counters.ReliableRecords, Is.EqualTo(5 * 2 + 5));
            Assert.That(fanout.Counters.OversizeReliable, Is.EqualTo(5));
            Assert.That(fanout.Counters.UnreliableRecords, Is.EqualTo(10));
            Assert.That(fanout.Counters.ReliablePackets, Is.GreaterThan(0));
            Assert.That(fanout.Counters.UnreliablePackets, Is.GreaterThan(0));
            Assert.That(fanout.Counters.BytesOut, Is.GreaterThan(0));
        }

        [Test]
        public void Build_AsksTheAnchorOncePerConnectionAndThePositionOncePerSource()
        {
            MeshStateFanout fanout = NewFanout(classify: 1);
            for (uint id = 1; id <= 5; ++id)
                Source(fanout, id, 7, 8, 9);
            _positions.anchors[7] = Vector3.zero;
            _positions.anchors[8] = Vector3.one;

            Builds(fanout, 3);

            Assert.That(_positions.anchorCallsBy[7], Is.EqualTo(3));
            Assert.That(_positions.anchorCallsBy[8], Is.EqualTo(3));
            Assert.That(_positions.anchorCallsBy[9], Is.EqualTo(3));
            Assert.That(_positions.positionCalls, Is.EqualTo(15));
        }

        [Test]
        public void Build_PassesTheDistanceFromTheAnchor()
        {
            MeshStateFanout fanout = NewFanout();
            _positions.anchors[7] = new Vector3(0f, 10f, 0f);
            _positions.positions[42] = new Vector3(3f, 10f, 4f);
            Source(fanout, 42, 7, 8);

            Build(fanout);

            Assert.That(_policy.last.ObserverConnectionId, Is.EqualTo(8));
            Assert.That(float.IsNaN(_policy.last.Distance), Is.True);
            fanout.MarkRelevanceChanged(42, 7);
            Build(fanout);
            Assert.That(_policy.last.Distance, Is.EqualTo(5f).Within(1e-5f));
        }

        [Test]
        public void Build_SteadyPopulation_AllocatesNothing()
        {
            MeshStateFanout fanout = NewFanout(classify: 4);
            _transport.countOnly = true;
            long[] subscribers = new long[50];
            for (int i = 0; i < subscribers.Length; ++i)
                subscribers[i] = 100 + i;
            for (uint id = 1; id <= 49; ++id)
                Source(fanout, id, subscribers);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(50);
            Wrapped(fanout, kit, subscribers);
            _positions.anchors[100] = Vector3.zero;
            Builds(fanout, 10);
            int frame = _frame;

            Assert.That(() =>
            {
                for (int i = 0; i < 10; ++i)
                {
                    frame++;
                    fanout.Build(Ts + frame, frame, -1);
                }
            }, Is.Not.AllocatingGCMemory());
            Assert.That(fanout.Counters.Writes, Is.EqualTo(20L * 50));
        }

        [Test]
        public void FlagsHandlerOf_IsTheInnerKitHandler_TheSourceItself_OrNull()
        {
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(42);
            LiteNetLibIdentity identity = _identities.New(42);

            Assert.That(MeshStateFanout.FlagsHandlerOf(new MeshKitMovementStateSource(kit, identity, _ => { })), Is.SameAs(kit));
            Assert.That(MeshStateFanout.FlagsHandlerOf(new MeshFakeStateSource(42, identity)), Is.Null);
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(42, identity, _identities.TransformOf(identity), kit, false, new MeshKinematicTrack(15f, 0.3f, 4f));
            Assert.That(MeshStateFanout.FlagsHandlerOf(body), Is.SameAs(body));
        }
    }
}
