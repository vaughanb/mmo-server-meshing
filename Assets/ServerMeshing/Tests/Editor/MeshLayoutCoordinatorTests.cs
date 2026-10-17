using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLayoutCoordinatorTests
    {
        private MeshWorldRegistry _world;
        private MeshFakeClock _clock;
        private MeshFakeCoordinatorTransport _transport;
        private MeshFakeLog _log;
        private string _dir;
        private MeshCoordinatorStore _store;
        private BandEnvironment _band;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: true);
            _clock = new MeshFakeClock();
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _dir = Path.Combine(Path.GetTempPath(), "MeshCoordinatorTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        private MeshLayoutCoordinator Started()
        {
            MeshLayoutCoordinator coordinator = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log);
            coordinator.Start();
            return coordinator;
        }

        private static MeshLayoutHelloMessage Hello(string cellId, string mapId = Map1, string incarnation = "i1", MeshLayoutSnapshot held = null)
        {
            return new MeshLayoutHelloMessage { cellId = cellId, mapId = mapId, incarnation = incarnation, dynamicMap = true, held = held ?? new MeshLayoutSnapshot() };
        }

        [Test]
        public void Constructor_NullArgument_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MeshLayoutCoordinator(null, _clock, _transport, _store, _log));
            Assert.Throws<ArgumentNullException>(() => new MeshLayoutCoordinator(_world, _clock, null, _store, _log));
        }

        [Test]
        public void Start_FreshStore_RunsAtOnceWithTheIncarnationPersisted()
        {
            MeshLayoutCoordinator coordinator = Started();

            Assert.That(coordinator.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(coordinator.Incarnation, Is.EqualTo(_clock.UtcTicks));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That(data.lastIncarnation, Is.EqualTo(coordinator.Incarnation));
            Assert.That(_transport.layouts, Is.Empty);
            Assert.That(_world.AppliedPair, Is.EqualTo(new MeshLayoutPair(coordinator.Incarnation, 1)));
            Assert.That(coordinator.OpsAllowed, Is.True);
        }

        [Test]
        public void Start_AfterAClockStepBack_StillTakesANewerIncarnation()
        {
            Assert.That(_store.TrySave(new MeshCoordinatorStoreData { lastIncarnation = 5_000_000 }, out _), Is.True);

            MeshLayoutCoordinator coordinator = Started();

            Assert.That(coordinator.Incarnation, Is.EqualTo(5_000_001));
        }

        [Test]
        public void Hello_FromAnUnregisteredConnection_IsIgnored()
        {
            MeshLayoutCoordinator coordinator = Started();

            coordinator.OnLayoutHello(7, Hello("C"));

            Assert.That(_transport.layouts, Is.Empty);
            Assert.That(_log.warnings, Has.Some.Contains("does not match a registered map server"));
            Assert.That(coordinator.BindingOf("C"), Is.Null);
        }

        [Test]
        public void Hello_WhoseCellDiffersFromTheRegistration_IsIgnored()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "E", Map1);

            coordinator.OnLayoutHello(7, Hello("C"));

            Assert.That(_transport.layouts, Is.Empty);
        }

        [Test]
        public void Hello_FromADynamicCell_IsAnsweredOnceWithTheBroadcast()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);

            coordinator.OnLayoutHello(7, Hello("C", incarnation: "abc"));

            Assert.That(_transport.layouts.Count, Is.EqualTo(1));
            (long connection, MeshLayoutMessage answer) = _transport.layouts[0];
            Assert.That(connection, Is.EqualTo(7));
            Assert.That(answer.kind, Is.EqualTo(MeshLayoutKind.HelloAnswer));
            Assert.That((answer.targetCellId, answer.targetIncarnation), Is.EqualTo(("C", "abc")));
            Assert.That(answer.snapshot.pair, Is.EqualTo(coordinator.BroadcastPair));
            Assert.That(answer.snapshot.entries, Is.Empty);
            Assert.That(coordinator.IsLayoutAnswered("C"), Is.True);
        }

        [Test]
        public void Hello_FromAStaticMapCell_IsAnsweredWithANoteAndNotBound()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(9, "C2", Map2);

            coordinator.OnLayoutHello(9, Hello("C2", Map2));

            Assert.That(_transport.layouts.Count, Is.EqualTo(1));
            Assert.That(_transport.notes.Count, Is.EqualTo(1));
            Assert.That(_transport.notes[0].message.code, Is.EqualTo("mode-mismatch"));
            Assert.That(coordinator.BindingOf("C2"), Is.Null);
        }

        [Test]
        public void Hello_AgainWithANewIncarnation_RebindsAndAnswersAgain()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            coordinator.OnLayoutHello(7, Hello("C", incarnation: "i1"));
            _transport.peers.Remove(7);
            _transport.Register(8, "C", Map1);

            coordinator.OnLayoutHello(8, Hello("C", incarnation: "i2"));

            Assert.That(_transport.layouts.Count, Is.EqualTo(2));
            Assert.That(_transport.layouts[1].connection, Is.EqualTo(8));
            MeshCellBinding binding = coordinator.BindingOf("C");
            Assert.That((binding.connectionId, binding.incarnation), Is.EqualTo((8L, "i2")));
            Assert.That(_log.infos, Has.Some.Contains("re-bound"));
        }

        [Test]
        public void Tick_MarksARemovedPeerUnregisteredOnce_AndWarnsOnceForASilentOne()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            coordinator.OnLayoutHello(7, Hello("C"));
            _transport.peers.Remove(7);
            _transport.Register(8, "E", Map1);

            coordinator.Tick();
            _clock.Now += 11f;
            coordinator.Tick();
            coordinator.Tick();

            Assert.That(coordinator.BindingOf("C").unregisteredAt, Is.GreaterThanOrEqualTo(0f));
            Assert.That(coordinator.IsLayoutAnswered("C"), Is.False);
            Assert.That(_log.infos.FindAll(m => m.Contains("Cell C unregistered")).Count, Is.EqualTo(1));
            Assert.That(_log.warnings.FindAll(m => m.Contains("Cell E of Dynamic map")).Count, Is.EqualTo(1));
        }

        [Test]
        public void Ack_OnlyFromTheBoundConnectionAndIncarnationCounts()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            coordinator.OnLayoutHello(7, Hello("C", incarnation: "i1"));
            MeshLayoutPair pair = coordinator.BroadcastPair;

            coordinator.OnLayoutAck(7, new MeshLayoutAckMessage { cellId = "C", incarnation = "old", pair = pair, result = MeshLayoutAckResult.Applied });
            coordinator.OnLayoutAck(9, new MeshLayoutAckMessage { cellId = "C", incarnation = "i1", pair = pair, result = MeshLayoutAckResult.Applied });
            Assert.That(coordinator.BindingOf("C").applied.IsNone, Is.True);

            coordinator.OnLayoutAck(7, new MeshLayoutAckMessage { cellId = "C", incarnation = "i1", pair = pair, result = MeshLayoutAckResult.Rejected, reason = "stale" });
            Assert.That(_log.warnings, Has.Some.Contains("rejected layout"));

            coordinator.OnLayoutAck(7, new MeshLayoutAckMessage { cellId = "C", incarnation = "i1", pair = pair, result = MeshLayoutAckResult.Applied });
            Assert.That(coordinator.BindingOf("C").applied, Is.EqualTo(pair));
        }

        [Test]
        public void LoadReport_OnlyTheBoundIncarnationIsEvidence()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            coordinator.OnLayoutHello(7, Hello("C", incarnation: "i1"));

            coordinator.OnLoadReport(7, new MeshLoadReportMessage { cellId = "C", mapId = Map1, incarnation = "stale" });
            Assert.That(coordinator.BindingOf("C").lastEvidenceAt, Is.LessThan(0f));

            _clock.Now = 150f;
            coordinator.OnLoadReport(7, new MeshLoadReportMessage { cellId = "C", mapId = Map1, incarnation = "i1" });
            Assert.That(coordinator.BindingOf("C").lastEvidenceAt, Is.EqualTo(150f));
        }

        [Test]
        public void UnwritableStore_StillAnswersHellos_ButAllowsNoOperation()
        {
            Directory.CreateDirectory(_store.Path);   // the store's path is a directory: every write fails
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);

            coordinator.OnLayoutHello(7, Hello("C"));

            Assert.That(_transport.layouts.Count, Is.EqualTo(1));
            Assert.That(coordinator.OpsAllowed, Is.False);
            Assert.That(_log.errors, Has.Some.Contains("write failed"));
        }

        [Test]
        public void StoreWithAChild_QueuesNothing_FreezesAndAnswersNobody()
        {
            MeshCoordinatorStoreData data = new MeshCoordinatorStoreData { lastIncarnation = 10 };
            data.broadcast.pair = new MeshLayoutPair(10, 2);
            data.broadcast.entries.Add(Active("C.1", "C", new MeshRect(0f, -100f, 100f, 100f)));
            Assert.That(_store.TrySave(data, out _), Is.True);
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);

            coordinator.OnLayoutHello(7, Hello("C"));

            Assert.That(coordinator.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.Frozen));
            Assert.That(_transport.layouts, Is.Empty);
            Assert.That(_transport.notes, Has.Some.Matches<(long connection, MeshCoordinatorNoteMessage message)>(n => n.message.code == "recovery-frozen"));
            Assert.That(_log.errors, Has.Some.Contains("Slice 5 recovery"));
        }

        [Test]
        public void UnreadableStore_IsMovedAside_AndTheMapWaitsForItsCells()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_store.Path, "{ broken");
            _transport.Register(7, "C", Map1);
            MeshLayoutCoordinator coordinator = Started();

            Assert.That(Directory.GetFiles(_dir, "store.json.unreadable-*").Length, Is.EqualTo(1));
            Assert.That(coordinator.Phase, Is.EqualTo(MeshCoordinatorPhase.Recovering));
            coordinator.OnLayoutHello(7, Hello("C"));
            Assert.That(_transport.layouts, Is.Empty, "queued while the store cannot be trusted");

            _clock.Now += 61f;
            coordinator.Tick();

            Assert.That(coordinator.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(_transport.layouts.Count, Is.EqualTo(1), "the queued hello is answered once the map is adopted");
            Assert.That(_log.errors, Has.Some.Contains("could not be trusted"));
        }

        [Test]
        public void LateHelloHoldingALayout_ReopensRecoveryAndFreezes()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            MeshLayoutSnapshot held = Snapshot(3, Active("C", "C", new MeshRect(-100f, -100f, 0f, 100f)), Active("C.1", "C", new MeshRect(0f, -100f, 100f, 100f)));
            held.pair = new MeshLayoutPair(1, 3);

            coordinator.OnLayoutHello(7, Hello("C", held: held));

            Assert.That(coordinator.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.Frozen));
            Assert.That(_log.warnings, Has.Some.Contains("re-opens recovery"));
            Assert.That(_transport.layouts, Is.Empty);
        }

        [Test]
        public void Stop_ForgetsEverything()
        {
            MeshLayoutCoordinator coordinator = Started();
            _transport.Register(7, "C", Map1);
            coordinator.OnLayoutHello(7, Hello("C"));

            coordinator.Stop();

            Assert.That(coordinator.Phase, Is.EqualTo(MeshCoordinatorPhase.Stopped));
            Assert.That(coordinator.BindingOf("C"), Is.Null);
            coordinator.OnLayoutHello(7, Hello("C"));
            Assert.That(_transport.layouts.Count, Is.EqualTo(1), "a stopped coordinator answers nothing");
        }

        [Test]
        public void TransportFilter_DropsInstanceMapServers()
        {
            Assert.That(MeshClusterCoordinatorTransport.ToPeer(new CentralServerPeerInfo { peerType = CentralServerPeerType.InstanceMapServer, channelId = "C", refId = Map1 }, out _), Is.False);
            Assert.That(MeshClusterCoordinatorTransport.ToPeer(new CentralServerPeerInfo { peerType = CentralServerPeerType.MapServer, channelId = "C", refId = Map1, connectionId = 4 }, out MeshMapServerPeer peer), Is.True);
            Assert.That((peer.cellId, peer.mapId, peer.connectionId), Is.EqualTo(("C", Map1, 4L)));
        }
    }
}
