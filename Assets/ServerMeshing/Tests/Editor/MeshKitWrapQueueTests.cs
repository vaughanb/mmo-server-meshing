using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshKitWrapQueueTests
    {
        private const uint Id = 42;
        private const long Ts = 1_790_000_000_000;

        private MeshFakeIdentities _identities;
        private ConcurrentDictionary<uint, IEntityMovementDataHandler> _handlers;
        private MeshMovementHandlerSlots _slots;
        private Dictionary<uint, LiteNetLibIdentity> _spawned;
        private MeshFakeStateTransport _transport;
        private MeshFakeSendPolicy _policy;
        private MeshFakeLog _log;
        private MeshStateFanout _fanout;
        private MeshKitWrapQueue _queue;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _handlers = new ConcurrentDictionary<uint, IEntityMovementDataHandler>();
            _slots = new MeshMovementHandlerSlots(_handlers);
            _spawned = new Dictionary<uint, LiteNetLibIdentity>();
            _transport = new MeshFakeStateTransport();
            _policy = new MeshFakeSendPolicy();
            _log = new MeshFakeLog();
            _frame = 0;
            _slots.LayerRemoved += id =>
            {
                if (_queue != null && _spawned.TryGetValue(id, out LiteNetLibIdentity identity))
                    _queue.OnLayerRemoved(identity);
            };
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private void NewQueue(bool install = true, bool wrapKit = true)
        {
            MeshStateFanoutSettings settings = new MeshStateFanoutSettings(20, 4, 1020, false, !install, false);
            _fanout = new MeshStateFanout(_transport, new MeshFakeStatePositions(), _policy, settings, _log, (source, reason) => _queue.OnDropped(source, reason));
            _queue = new MeshKitWrapQueue(_slots, _fanout, _log, install, wrapKit);
            _queue.AllowHandlerType(typeof(MeshFakePassKitHandler));
        }

        private LiteNetLibIdentity Spawn(IEntityMovementDataHandler kit, uint objectId, params long[] subscribers)
        {
            LiteNetLibIdentity identity = _identities.New(objectId, subscribers);
            if (kit != null)
                _handlers[objectId] = kit;
            _spawned[objectId] = identity;
            return identity;
        }

        private void Wrap(LiteNetLibIdentity identity)
        {
            _queue.Enqueue(identity);
            _queue.Drain();
        }

        private void Build()
        {
            _frame++;
            _fanout.Build(Ts + _frame, _frame, -1);
        }

        private List<(DeliveryMethod method, uint id, byte[] bytes)> Records(long connection)
        {
            List<(DeliveryMethod, uint, byte[])> records = new List<(DeliveryMethod, uint, byte[])>();
            foreach ((long _, DeliveryMethod method, byte[] data) in _transport.To(connection))
                foreach ((uint id, int _, byte[] bytes) in MeshEntityStatePacketParser.Parse(data))
                    records.Add((method, id, bytes));
            return records;
        }

        private static bool IsTeleport(byte[] kitPayload) => (kitPayload[0] & MeshFakePassKitHandler.IsTeleport) != 0;

        private static IEnumerable<TestCaseData> NullArgumentCases()
        {
            yield return new TestCaseData("slots").SetName("Constructor_NullSlots_ThrowsNamingIt");
            yield return new TestCaseData("fanout").SetName("Constructor_NullFanout_ThrowsNamingIt");
            yield return new TestCaseData("log").SetName("Constructor_NullLog_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(NullArgumentCases))]
        public void Constructor_NullArgument_ThrowsNamingIt(string missing)
        {
            NewQueue();

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshKitWrapQueue(
                missing == "slots" ? null : _slots,
                missing == "fanout" ? null : _fanout,
                missing == "log" ? null : _log,
                true, true));

            Assert.That(thrown.ParamName, Is.EqualTo(missing));
        }

        [Test]
        public void AllowHandlerType_Null_ThrowsNamingIt()
        {
            NewQueue();

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => _queue.AllowHandlerType(null));

            Assert.That(thrown.ParamName, Is.EqualTo("handlerType"));
        }

        [Test]
        public void Drain_TypeNotAllowed_RefusesBothIds_LoggingOnce()
        {
            NewQueue();

            Wrap(Spawn(new MeshFakeKitHandler(Id), Id, 7));
            Wrap(Spawn(new MeshFakeKitHandler(Id + 1), Id + 1, 7));

            Assert.That(_handlers[Id], Is.InstanceOf<MeshFakeKitHandler>());
            Assert.That(_fanout.SourceCount, Is.EqualTo(0));
            Assert.That(_queue.Refusals, Is.EqualTo(2));
            Assert.That(_log.warnings.Count, Is.EqualTo(1));
            Assert.That(_log.warnings[0], Does.Contain("not allowed"));
        }

        [Test]
        public void Drain_KitHandlerIsAStateSource_Refuses()
        {
            NewQueue();
            LiteNetLibIdentity identity = _identities.New(Id, 7);
            MeshKinematicCharacterBody standIn = new MeshKinematicCharacterBody(Id, identity, _identities.TransformOf(identity), new MeshFakePassKitHandler(Id), false, new MeshKinematicTrack(15f, 0.3f, 4f));
            _handlers[Id] = standIn;

            Wrap(identity);

            Assert.That(_handlers[Id], Is.SameAs(standIn));
            Assert.That(_queue.Refusals, Is.EqualTo(1));
            Assert.That(_queue.Wrapped, Is.EqualTo(0));
        }

        [Test]
        public void Drain_SlotHoldsAnotherLayer_Refuses()
        {
            NewQueue();
            LiteNetLibIdentity identity = Spawn(new MeshFakePassKitHandler(Id), Id, 7);
            MeshFakeReplaceableLayer layer = new MeshFakeReplaceableLayer(Id);
            Assert.That(_slots.TryInstall(Id, layer, true, out string error), Is.True, error);

            Wrap(identity);

            Assert.That(_handlers[Id], Is.SameAs(layer));
            Assert.That(layer.replacedCount, Is.EqualTo(0));
            Assert.That(_queue.Refusals, Is.EqualTo(1));
        }

        [Test]
        public void Drain_KitSourcesOff_Refuses()
        {
            NewQueue(wrapKit: false);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);

            Wrap(Spawn(kit, Id, 7));

            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_queue.Refusals, Is.EqualTo(1));
        }

        [Test]
        public void AppendOnlyDrop_HandsTheSlotBack_AndTheObjectIsNeverWrappedAgain()
        {
            NewQueue();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id) { resetWriter = true };
            LiteNetLibIdentity identity = Spawn(kit, Id, 7);
            Wrap(identity);
            Assert.That(_handlers[Id], Is.InstanceOf<MeshKitMovementStateSource>());

            Build();

            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
            Assert.That(_queue.Wrapped, Is.EqualTo(0));
            Assert.That(_queue.QueuedCount, Is.EqualTo(0));
            Assert.That(_log.errors, Has.Some.Contains($"object {Id}"));

            Wrap(identity);

            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_queue.Refusals, Is.EqualTo(1));
        }

        [Test]
        public void Drain_NoHandlerDestroyedOrReidentifiedOrRegistered_SkipsSilently()
        {
            NewQueue();
            LiteNetLibIdentity noHandler = Spawn(null, 40, 7);
            LiteNetLibIdentity destroyed = Spawn(new MeshFakePassKitHandler(41), 41, 7);
            LiteNetLibIdentity reidentified = Spawn(new MeshFakePassKitHandler(42), 42, 7);
            LiteNetLibIdentity registered = Spawn(new MeshFakePassKitHandler(43), 43, 7);
            Assert.That(_fanout.Register(new MeshFakeStateSource(43, registered), out _), Is.True);
            _queue.Enqueue(noHandler);
            _queue.Enqueue(destroyed);
            _queue.Enqueue(reidentified);
            _queue.Enqueue(registered);
            _identities.Destroy(destroyed);
            MeshFakeIdentities.SetObjectId(reidentified, 99);

            _queue.Drain();

            Assert.That(_queue.Wrapped, Is.EqualTo(0));
            Assert.That(_queue.Refusals, Is.EqualTo(0));
            Assert.That(_queue.QueuedCount, Is.EqualTo(0));
            Assert.That(_log.warnings, Is.Empty);
            Assert.That(_log.errors, Is.Empty);
            Assert.That(_handlers[42], Is.InstanceOf<MeshFakePassKitHandler>());
        }

        [Test]
        public void Drain_FanoutRefusesTheWrapper_HandsTheSlotBackNamingTheId()
        {
            NewQueue();
            const uint OtherId = 77;
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(OtherId);
            LiteNetLibIdentity identity = Spawn(kit, Id, 7);
            Assert.That(_fanout.Register(new MeshFakeStateSource(OtherId, _identities.New(OtherId, 7)), out _), Is.True);

            Wrap(identity);

            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_queue.Wrapped, Is.EqualTo(0));
            Assert.That(_queue.QueuedCount, Is.EqualTo(0));
            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith($"State sender error: object {Id} "));
        }

        [Test]
        public void Drain_KitHandlerReplacedAfterEnqueue_WrapsTheCurrentOne()
        {
            NewQueue();
            LiteNetLibIdentity identity = Spawn(new MeshFakePassKitHandler(Id), Id, 7);
            _queue.Enqueue(identity);
            MeshFakePassKitHandler current = new MeshFakePassKitHandler(Id);
            _handlers[Id] = current;

            _queue.Drain();

            Assert.That(_handlers[Id], Is.InstanceOf<MeshKitMovementStateSource>());
            Assert.That(((MeshKitMovementStateSource)_handlers[Id]).Inner, Is.SameAs(current));
        }

        [Test]
        public void Teleport_OneWrite_ReachesAllThreeSubscribersReliably_IsConsumedOnce_ThenGoesUnflagged()
        {
            NewQueue();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id) { ConnectionId = 7 };
            LiteNetLibIdentity identity = Spawn(kit, Id, 7, 8, 9);
            MeshFakeIdentities.SetOwner(identity, 7);
            Wrap(identity);
            kit.pendingTeleport = true;

            Build();

            Assert.That(kit.writes, Is.EqualTo(1));
            Assert.That(kit.consumes, Is.EqualTo(1));
            foreach (long connection in new long[] { 7, 8, 9 })
            {
                List<(DeliveryMethod method, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(1));
                Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
                Assert.That(IsTeleport(records[0].bytes), Is.True);
            }

            _transport.sends.Clear();
            Build();

            foreach (long connection in new long[] { 7, 8, 9 })
            {
                List<(DeliveryMethod method, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(1));
                Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.Unreliable));
                Assert.That(IsTeleport(records[0].bytes), Is.False);
            }
        }

        [Test]
        public void KitReRegistration_IsFoundByCheckHeld_SkipsTheBuild_AndRewrapsOnTheNextDrain()
        {
            NewQueue();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            Wrap(Spawn(kit, Id, 7));
            _handlers[Id] = kit;

            int lost = _queue.CheckHeld();
            Build();

            Assert.That(lost, Is.EqualTo(1));
            Assert.That(kit.writes, Is.EqualTo(0));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
            Assert.That(_queue.QueuedCount, Is.EqualTo(1));

            _queue.Drain();

            Assert.That(_handlers[Id], Is.InstanceOf<MeshKitMovementStateSource>());
            Assert.That(_fanout.IsRegistered(Id), Is.True);
            Assert.That(_queue.Rewraps, Is.EqualTo(1));
            Assert.That(_queue.CheckHeld(), Is.EqualTo(0));
        }

        [Test]
        public void TwentyReliableBuilds_WarnOnceForAWrappedHandler_AndNeverForADirectSource()
        {
            NewQueue();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            Wrap(Spawn(kit, Id, 7));
            MeshFakeStateSource direct = new MeshFakeStateSource(Id + 1, _identities.New(Id + 1, 7)) { reliable = true };
            _fanout.Register(direct, out _);

            for (int i = 0; i < 25; ++i)
            {
                kit.pendingTeleport = true;
                Build();
            }

            Assert.That(_log.warnings.Count, Is.EqualTo(1));
            Assert.That(_log.warnings[0], Is.EqualTo($"State sender: object {Id} (MeshFakePassKitHandler) wrote 20 reliable builds in a row; its one-shot flags are not being consumed"));
            Assert.That(_fanout.Counters.StuckReliable, Is.EqualTo(1));
        }

        [Test]
        public void Forget_UnregistersTheWrapperAndForgetsItsSlot()
        {
            NewQueue();
            Wrap(Spawn(new MeshFakePassKitHandler(Id), Id, 7));

            _queue.Forget(Id);

            Assert.That(_fanout.IsRegistered(Id), Is.False);
            Assert.That(_queue.Wrapped, Is.EqualTo(0));
            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_handlers.ContainsKey(Id), Is.False);
        }

        [Test]
        public void MeasureMode_NeverInstalls_AndChecksTheKitHandlerIsStillItsInner()
        {
            NewQueue(install: false);
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            Wrap(Spawn(kit, Id, 7));

            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_fanout.IsRegistered(Id), Is.True);
            Assert.That(_queue.CheckHeld(), Is.EqualTo(0));
            Assert.That(_queue.Wrapped, Is.EqualTo(1));

            _handlers[Id] = new MeshFakePassKitHandler(Id);

            Assert.That(_queue.CheckHeld(), Is.EqualTo(1));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
            Assert.That(_queue.QueuedCount, Is.EqualTo(1));
        }

        [Test]
        public void AllowHandlerType_AdmitsExactlyThatType()
        {
            NewQueue();

            Assert.That(_queue.IsAllowed(typeof(BuiltInEntityMovementFunctions3D)), Is.True);
            Assert.That(_queue.IsAllowed(typeof(MeshFakeBuiltInSubclass)), Is.False);
            Assert.That(_queue.IsAllowed(null), Is.False);

            _queue.AllowHandlerType(typeof(MeshFakeBuiltInSubclass));

            Assert.That(_queue.IsAllowed(typeof(MeshFakeBuiltInSubclass)), Is.True);
            Assert.That(_queue.IsAllowed(typeof(MeshFakeKitHandler)), Is.False);
        }

        [Test]
        public void TakeCounts_ReturnsAndZeroesRewrapsAndRefusals()
        {
            NewQueue();
            Wrap(Spawn(new MeshFakeKitHandler(40), 40, 7));
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            Wrap(Spawn(kit, Id, 7));
            _handlers[Id] = kit;
            _queue.CheckHeld();

            _queue.TakeCounts(out int rewraps, out int refusals);

            Assert.That(rewraps, Is.EqualTo(1));
            Assert.That(refusals, Is.EqualTo(1));
            Assert.That(_queue.Rewraps, Is.EqualTo(0));
            Assert.That(_queue.Refusals, Is.EqualTo(0));
        }

        [Test]
        public void OrderTest_WrapThenBodyThenPromotionThenRewrap_SendsThePendingFlagOnce()
        {
            NewQueue();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            LiteNetLibIdentity identity = Spawn(kit, Id, 7, 8, 9);

            Wrap(identity);
            Assert.That(_handlers[Id], Is.InstanceOf<MeshKitMovementStateSource>());
            Assert.That(_fanout.IsRegistered(Id), Is.True);

            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, identity, _identities.TransformOf(identity), kit, false, new MeshKinematicTrack(15f, 0.3f, 4f));
            Assert.That(_slots.TryInstall(Id, body, false, out string installError), Is.True, installError);
            Assert.That(_fanout.IsRegistered(Id), Is.False);
            Assert.That(_queue.Wrapped, Is.EqualTo(0));

            Assert.That(_fanout.Register(body, out string registerError), Is.True, registerError);
            body.SentBySender = true;
            Assert.That(_fanout.SourceCount, Is.EqualTo(1));

            Assert.That(_fanout.Unregister(body), Is.True);
            Assert.That(_slots.TryRemove(Id, body, out string removeError), Is.True, removeError);
            Assert.That(_handlers[Id], Is.SameAs(kit));
            Assert.That(_queue.QueuedCount, Is.EqualTo(1));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
            kit.pendingTeleport = true;

            _queue.Drain();
            Assert.That(_handlers[Id], Is.InstanceOf<MeshKitMovementStateSource>());
            Assert.That(((MeshKitMovementStateSource)_handlers[Id]).Inner, Is.SameAs(kit));
            Assert.That(_fanout.SourceCount, Is.EqualTo(1));

            Build();

            Assert.That(kit.consumes, Is.EqualTo(1));
            foreach (long connection in new long[] { 7, 8, 9 })
            {
                List<(DeliveryMethod method, uint id, byte[] bytes)> records = Records(connection);
                Assert.That(records.Count, Is.EqualTo(1));
                Assert.That(records[0].method, Is.EqualTo(DeliveryMethod.ReliableOrdered));
                Assert.That(IsTeleport(records[0].bytes), Is.True);
            }
        }

        [Test]
        public void OnDropped_ABodyDroppedByTheFanout_IsWrittenByTheKitAgain()
        {
            NewQueue();
            LiteNetLibIdentity identity = _identities.New(Id, 7);
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, identity, _identities.TransformOf(identity), new MeshFakePassKitHandler(Id), false, new MeshKinematicTrack(15f, 0.3f, 4f));
            Assert.That(_fanout.Register(body, out _), Is.True);
            body.SentBySender = true;
            _identities.Destroy(identity);

            Build();

            Assert.That(body.SentBySender, Is.False);
            Assert.That(_fanout.IsRegistered(Id), Is.False);
        }
    }
}
