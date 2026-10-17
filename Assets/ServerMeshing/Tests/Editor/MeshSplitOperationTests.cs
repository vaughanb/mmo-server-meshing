using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The coordinator's split, driven step by step through fakes (Slice 2b decisions 2-5b).</summary>
    public class MeshSplitOperationTests
    {
        private const long ConnC = 7, ConnE = 8, ConnC2 = 9, ConnX = 20;
        private MeshWorldRegistry _world;
        private MeshFakeClock _clock;
        private MeshFakeCoordinatorTransport _transport;
        private MeshFakeLog _log;
        private MeshFakeSplitHost _host;
        private string _dir;
        private MeshCoordinatorStore _store;
        private BandEnvironment _band;
        private MeshLayoutCoordinator _c;
        private readonly List<string> _lines = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: true);
            _clock = new MeshFakeClock();
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost();
            _dir = Path.Combine(Path.GetTempPath(), "MeshSplitTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            _lines.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
            _c.Start();
            _transport.Register(ConnC, "C", Map1);
            _c.OnLayoutHello(ConnC, Hello("C", Map1, "pc"));
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        private static MeshLayoutHelloMessage Hello(string cellId, string mapId, string incarnation, MeshLayoutSnapshot held = null, bool dynamicMap = true)
        {
            return new MeshLayoutHelloMessage { cellId = cellId, mapId = mapId, incarnation = incarnation, dynamicMap = dynamicMap, held = held ?? new MeshLayoutSnapshot() };
        }

        private void Ack(long connection, string cellId, string incarnation, MeshLayoutPair pair, bool applied = true)
        {
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = applied ? MeshLayoutAckResult.Applied : MeshLayoutAckResult.Rejected, reason = applied ? null : "refused" });
        }

        internal static MeshLayoutEvidenceMessage Prewarm(string cellId, string incarnation, MeshLayoutPair pair, params string[] heard)
        {
            MeshLayoutEvidenceMessage evidence = new MeshLayoutEvidenceMessage { cellId = cellId, incarnation = incarnation, pair = pair, neighbours = new List<MeshNeighbourEvidence>(), budgetMs = 16.7f };
            foreach (string zone in heard)
                evidence.neighbours.Add(new MeshNeighbourEvidence { zoneId = zone, helloAgeSeconds = 3f, datagramsSinceHello = 4 });
            return evidence;
        }

        internal static MeshLayoutEvidenceMessage Drain(string cellId, string incarnation, MeshLayoutPair pair, int left)
        {
            return new MeshLayoutEvidenceMessage
            {
                cellId = cellId, incarnation = incarnation, pair = pair, windowStartedAfterApplySeconds = 0.5f, shrunk = true,
                givenAway = new MeshRect(0f, -100f, 100f, 100f), neighbours = new List<MeshNeighbourEvidence>(),
                drain = new MeshDrainCounts { characters = left },
            };
        }

        private MeshSplitOperation RequestC()
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            return _c.CurrentOp;
        }

        private void ChildHello(long connection = ConnX, string incarnation = "xc")
        {
            _transport.Register(connection, "C.1", Map1);
            _c.OnLayoutHello(connection, Hello("C.1", Map1, incarnation));
        }

        private MeshSplitOperation ToE1Gate()
        {
            MeshSplitOperation op = RequestC();
            _c.OnSpawnResult(op.opId, true, "Success");
            ChildHello();
            Ack(ConnX, "C.1", "xc", op.e1Pair);
            return op;
        }

        private MeshSplitOperation ToDrainGate()
        {
            MeshSplitOperation op = ToE1Gate();
            Ack(ConnC, "C", "pc", op.e1Pair);
            _c.OnLayoutEvidence(ConnX, Prewarm("C.1", "xc", op.e1Pair, "C"));
            Ack(ConnX, "C.1", "xc", op.e2Pair);
            return op;
        }

        private List<(long connection, MeshLayoutMessage message)> Layouts(MeshLayoutKind kind) => _transport.layouts.FindAll(l => l.message.kind == kind);

        private string L(string text) => "Split split-1: " + text;

        [Test]
        public void Split_RunsEveryStepInOrder_ToDone()
        {
            MeshSplitOperation op = ToDrainGate();
            Ack(ConnC, "C", "pc", op.e2Pair);
            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e2Pair, 0));
            Ack(ConnC, "C", "pc", op.e3Pair);
            Ack(ConnX, "C.1", "xc", op.e3Pair);

            Assert.That(_lines, Is.EqualTo(new[]
            {
                L("requested C x 0 + child C.1"), L("channel added"), L("spawning"), L("bound xc"), L("e1 staged"), L("e1 acked"),
                L("e1 broadcast f4240/2"), L("e1 gate open"), L("e2 staged"), L("e2 acked"), L("e2 broadcast f4240/3"),
                L("drain gate open"), L("e3 broadcast f4240/4"), L("done"),
            }));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));
            Assert.That(_host.channels, Is.EqualTo(new[] { "C.1" }));
            Assert.That(_host.spawns, Is.EqualTo(new[] { "split-1 C C.1 Map1" }));
            Assert.That(_world.AppliedPair, Is.EqualTo(op.e3Pair));
            MeshCell parent = _world.GetCellById("C"), child = _world.GetCellById("C.1");
            Assert.That((parent.owned, parent.interest), Is.EqualTo((new MeshRect(-100f, -100f, 0f, 100f), new MeshRect(-100f, -100f, 0f, 100f))));
            Assert.That((child.state, child.owned), Is.EqualTo((MeshCellState.Active, new MeshRect(0f, -100f, 100f, 100f))));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.broadcast.pair, data.hasStaged), Is.EqualTo((op.e3Pair, false)));
        }

        [Test]
        public void E1_IsStagedToTheChildAlone_AsItsHelloAnswer()
        {
            MeshSplitOperation op = RequestC();
            ChildHello();

            (long connection, MeshLayoutMessage answer) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((connection, answer.kind, answer.targetCellId, answer.targetIncarnation, answer.stepId), Is.EqualTo((ConnX, MeshLayoutKind.HelloAnswer, "C.1", "xc", "split-1/e1")));
            MeshLayoutEntry entry = answer.snapshot.entries.Find(e => e.id == "C.1");
            Assert.That((entry.state, entry.owned.IsEmpty, entry.interest), Is.EqualTo((MeshCellState.Pending, true, new MeshRect(0f, -100f, 100f, 100f))));
            Assert.That(Layouts(MeshLayoutKind.Broadcast), Is.Empty, "nobody else sees e1 before the child acks it");
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.hasStaged, data.stagedStepId, data.stagedFirstApplier), Is.EqualTo((true, "split-1/e1", "C.1")));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Staged));
        }

        [Test]
        public void ChildHelloAgain_WhileAStepIsStaged_GetsTheStagedPair()
        {
            MeshSplitOperation op = RequestC();
            ChildHello();
            _transport.peers.Remove(ConnX);
            ChildHello(ConnX + 1, "xc");
            (long connection, MeshLayoutMessage answer) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((connection, answer.kind, answer.stepId, answer.snapshot.pair), Is.EqualTo((ConnX + 1, MeshLayoutKind.HelloAnswer, "split-1/e1", op.e1Pair)));

            Ack(ConnX + 1, "C.1", "xc", op.e1Pair);
            Ack(ConnC, "C", "pc", op.e1Pair);
            _c.OnLayoutEvidence(ConnX + 1, Prewarm("C.1", "xc", op.e1Pair, "C"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged));
            ChildHello(ConnX + 1, "xc");
            answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.kind, answer.stepId, answer.snapshot.pair), Is.EqualTo((MeshLayoutKind.HelloAnswer, "split-1/e2", op.e2Pair)));
        }

        [Test]
        public void Acks_CountByPair_OlderOnesDoNot()
        {
            MeshSplitOperation op = ToE1Gate();
            Assert.That(op.participants, Is.EquivalentTo(new[] { "C.1", "C" }));
            Ack(ConnC, "C", "pc", new MeshLayoutPair(_c.Incarnation, 1));
            _c.OnLayoutEvidence(ConnX, Prewarm("C.1", "xc", op.e1Pair, "C"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate), "an ack of the layout before e1 is not an ack of e1");

            Ack(ConnC, "C", "pc", op.e1Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged));
            (long connection, MeshLayoutMessage staged) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((connection, staged.kind, staged.targetCellId, staged.stepId), Is.EqualTo((ConnX, MeshLayoutKind.Staged, "C.1", "split-1/e2")));

            // The child's ack of the staged e2 also counts for the e2 broadcast it is a participant of.
            Ack(ConnX, "C.1", "xc", op.e2Pair);
            Ack(ConnC, "C", "pc", op.e2Pair);
            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e2Pair, 0));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast));
        }

        [Test]
        public void Participants_IncludeAStaticNeighbourWhoseBandChanges_AndTheGateWaitsForIt()
        {
            _transport.Register(ConnE, "E", Map1);
            _c.OnLayoutHello(ConnE, Hello("E", Map1, "pe"));
            _transport.Register(ConnC2, "C2", Map2);
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.X, 200f, true, "E.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentOp;
            _transport.Register(ConnX, "E.1", Map1);
            _c.OnLayoutHello(ConnX, Hello("E.1", Map1, "xe"));
            Ack(ConnX, "E.1", "xe", op.e1Pair);

            Assert.That(op.participants, Is.EquivalentTo(new[] { "E.1", "E", "C2" }), "C is registered but E.1 is outside its band");
            Assert.That(Layouts(MeshLayoutKind.Broadcast).ConvertAll(l => l.connection), Is.EquivalentTo(new[] { ConnC, ConnE, ConnC2, ConnX }));
            Ack(ConnE, "E", "pe", op.e1Pair);
            _c.OnLayoutEvidence(ConnX, Prewarm("E.1", "xe", op.e1Pair, "E", "C2"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate), "C2 has not acked");

            Ack(ConnC2, "C2", "c2i", op.e1Pair);

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged));
            Assert.That(_c.BindingOf("C2").staticNeighbour, Is.True);
        }

        [Test]
        public void StaticNeighbourHello_IsBoundWithoutTheModeMismatchNote()
        {
            _transport.Register(ConnC2, "C2", Map2);

            _c.OnLayoutHello(ConnC2, Hello("C2", Map2, "c2i", dynamicMap: false));

            Assert.That(_transport.notes, Is.Empty);
            Assert.That(_transport.layouts[_transport.layouts.Count - 1].message.kind, Is.EqualTo(MeshLayoutKind.HelloAnswer));
            Assert.That(_c.BindingOf("C2").staticNeighbour, Is.True);
        }

        [Test]
        public void PrewarmNotReady_KeepsTheE1GateShut()
        {
            MeshSplitOperation op = ToE1Gate();
            Ack(ConnC, "C", "pc", op.e1Pair);

            MeshLayoutEvidenceMessage young = Prewarm("C.1", "xc", op.e1Pair, "C");
            young.neighbours[0] = new MeshNeighbourEvidence { zoneId = "C", helloAgeSeconds = 1f, datagramsSinceHello = 3 };
            _c.OnLayoutEvidence(ConnX, young);
            _c.OnLayoutEvidence(ConnX, Prewarm("C.1", "other-incarnation", op.e1Pair, "C"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate));

            _c.OnLayoutEvidence(ConnX, Prewarm("C.1", "xc", op.e1Pair, "C"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged));
        }

        [Test]
        public void RejectedE1_AbortsBeforeE2_RetiresTheChildAndRemovesItsChannel()
        {
            MeshSplitOperation op = RequestC();
            _c.OnSpawnResult(op.opId, true, "Success");
            ChildHello();

            Ack(ConnX, "C.1", "xc", op.e1Pair, applied: false);

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("aborted C.1 rejected e1 f4240/2: refused")));
            Assert.That(_log.warnings, Has.Some.EqualTo(L("aborted C.1 rejected e1 f4240/2: refused")));
            Assert.That(_transport.retires, Has.Some.Matches<(long connection, MeshRetireMessage message)>(r => r.connection == ConnX && r.message.cellId == "C.1" && r.message.incarnation == "xc"));
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            Assert.That(Layouts(MeshLayoutKind.Broadcast), Is.Empty, "e1 never went out: nothing to retire on the other cells");
            Assert.That(_c.IsAborted("C.1"), Is.True);
        }

        [Test]
        public void AbortedChildHello_IsAnsweredWithRetire()
        {
            MeshSplitOperation op = RequestC();
            _c.OnSpawnResult(op.opId, false, "CANNOT_EXCUTE_MAP_SERVER UI_ERROR_CANNOT_EXCUTE_MAP_SERVER");
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            int layouts = _transport.layouts.Count;

            ChildHello(ConnX, "late");

            Assert.That(_transport.retires, Has.Some.Matches<(long connection, MeshRetireMessage message)>(r => r.connection == ConnX && r.message.incarnation == "late"));
            Assert.That(_transport.layouts.Count, Is.EqualTo(layouts), "no layout answer for an aborted child");
            Assert.That(_lines, Has.Some.EqualTo(L("hello from aborted child C.1 answered with Retire")));
        }

        [Test]
        public void LateSpawnCallback_AfterAnAbort_IsIgnored()
        {
            MeshSplitOperation op = RequestC();
            _clock.Now += 61f;
            _c.Tick();
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(op.endReason, Is.EqualTo("no hello from C.1 in 60 s"));
            int lines = _lines.Count;

            _c.OnSpawnResult(op.opId, true, "Success");
            _c.OnSpawnResult(op.opId, false, "UNKNOW");

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(_lines.Count, Is.EqualTo(lines + 2));
            Assert.That(_lines[lines], Is.EqualTo(L("spawn callback Success after the operation ended; ignored")));
        }

        [Test]
        public void SecondSpawnCallback_IsIgnored()
        {
            MeshSplitOperation op = RequestC();
            _c.OnSpawnResult(op.opId, true, "Success");
            _c.OnSpawnResult(op.opId, false, "UNKNOW");
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Spawning));
        }

        [Test]
        public void E1GateTimeout_AfterTheE1Broadcast_AbortsWithARetiredVersion()
        {
            MeshSplitOperation op = ToE1Gate();
            Ack(ConnC, "C", "pc", op.e1Pair);
            int broadcasts = Layouts(MeshLayoutKind.Broadcast).Count;

            _clock.Now += 61f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(op.endReason, Is.EqualTo("e1 gate not open in 60 s: no evidence from C.1"));
            List<(long connection, MeshLayoutMessage message)> after = Layouts(MeshLayoutKind.Broadcast);
            Assert.That(after.Count, Is.GreaterThan(broadcasts));
            MeshLayoutMessage retired = after[after.Count - 1].message;
            Assert.That(retired.stepId, Is.EqualTo("split-1/abort"));
            Assert.That(retired.snapshot.entries.Find(e => e.id == "C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_world.AppliedPair, Is.EqualTo(retired.snapshot.pair));
            Assert.That(retired.snapshot.pair.IsNewerThan(op.e1Pair), Is.True);
        }

        [Test]
        public void SilenceAfterE2_FreezesTheOperation_AndRefusesTheNext()
        {
            MeshSplitOperation op = ToDrainGate();
            Ack(ConnC, "C", "pc", op.e2Pair);

            _clock.Now += 61f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Frozen));
            Assert.That(_lines[_lines.Count - 1], Does.StartWith(L("frozen drain gate not open in 60 s: no drain evidence from C")));
            Assert.That(_host.removed, Is.Empty, "a frozen child keeps its channel");
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.X, 200f, true, "E.1", out string error), Is.False);
            Assert.That(error, Does.StartWith("split-1 is frozen"));
            Assert.That(_lines[_lines.Count - 1], Does.StartWith("Layout op refused: split-1 is frozen"));
        }

        [Test]
        public void RejectedE2Broadcast_Freezes()
        {
            MeshSplitOperation op = ToDrainGate();
            Ack(ConnC, "C", "pc", op.e2Pair, applied: false);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Frozen));
        }

        [Test]
        public void DrainTimeout_SettlesWithStragglersInsideTheOldInterest()
        {
            MeshSplitOperation op = ToDrainGate();
            Ack(ConnC, "C", "pc", op.e2Pair);
            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e2Pair, 3));
            Assert.That(op.drainDeadline, Is.EqualTo(op.stepStartedAt + 3f / 5f + 20f + 10f).Within(1e-3f));

            _clock.Now += 31f;
            _c.Tick();

            Assert.That(_lines, Has.Some.EqualTo(L("drain timeout, 3 straggler(s)")));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast));
            Assert.That(_world.GetCellById("C").interest, Is.EqualTo(new MeshRect(-100f, -100f, 100f, 100f)));

            Ack(ConnC, "C", "pc", op.e3Pair);
            Ack(ConnX, "C.1", "xc", op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));
            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e3Pair, 1));
            Assert.That(op.settled, Is.False);

            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e3Pair, 0));

            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("stragglers gone, settle broadcast f4240/5")));
            Assert.That(_world.GetCellById("C").interest, Is.EqualTo(new MeshRect(-100f, -100f, 0f, 100f)));
        }

        [Test]
        public void HelloHoldingThisCoordinatorsLayout_DoesNotReopenRecovery()
        {
            MeshSplitOperation op = ToDrainGate();
            MeshLayoutSnapshot held = _world.AppliedSnapshot;
            _transport.peers.Remove(ConnC);
            _transport.Register(ConnC + 100, "C", Map1);
            int layouts = _transport.layouts.Count;

            _c.OnLayoutHello(ConnC + 100, Hello("C", Map1, "pc", held));

            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.AdoptStatic));
            Assert.That(_log.warnings, Has.None.Contains("re-opens recovery"));
            Assert.That(_transport.layouts.Count, Is.EqualTo(layouts + 1));
            Assert.That(_transport.layouts[layouts].message.snapshot.pair, Is.EqualTo(op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate));
        }

        [Test]
        public void UnknownDynamicId_IsAnsweredWithRetire()
        {
            _transport.Register(ConnX, "C.9", Map1);

            _c.OnLayoutHello(ConnX, Hello("C.9", Map1, "stray"));

            Assert.That(_transport.retires.Count, Is.EqualTo(1));
            Assert.That(_transport.retires[0].message.reason, Is.EqualTo("not in the layout"));
            Assert.That(_c.BindingOf("C.9"), Is.Null);

            // An asset cell on the same Dynamic map holding nothing is answered as usual, not retired.
            _transport.Register(ConnE, "E", Map1);
            _c.OnLayoutHello(ConnE, Hello("E", Map1, "pe"));

            Assert.That(_transport.retires.Count, Is.EqualTo(1));
            Assert.That(_c.BindingOf("E"), Is.Not.Null);
        }

        [Test]
        public void ChildRestartBeforeE2_Aborts()
        {
            MeshSplitOperation op = ToE1Gate();
            _transport.peers.Remove(ConnX);

            ChildHello(ConnX + 1, "xc2");

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(op.endReason, Is.EqualTo("child C.1 restarted: incarnation xc -> xc2"));
            Assert.That(_transport.retires, Has.Some.Matches<(long connection, MeshRetireMessage message)>(r => r.connection == ConnX + 1 && r.message.incarnation == "xc2"));
        }

        [Test]
        public void Refusals_NameTheRule()
        {
            string Refused(string cell, float at, string child)
            {
                Assert.That(_c.RequestSplit(cell, MeshSplitAxis.X, at, true, child, out string error), Is.False);
                Assert.That(_lines[_lines.Count - 1], Is.EqualTo("Layout op refused: " + error));
                return error;
            }

            Assert.That(Refused("C2", 400f, "C2.1"), Is.EqualTo("C2 is on map Map2, which is Static"));
            Assert.That(Refused("Q", 0f, "Q.1"), Is.EqualTo("cell Q is not in the layout"));
            Assert.That(Refused("E", 200f, "E.1"), Is.EqualTo("E is not running with a layout answer"));
            Assert.That(Refused("C", 150f, "C.1"), Does.StartWith("the x 150 line does not cross C"));
            Assert.That(Refused("C", 0f, "E"), Is.EqualTo("cell E already exists"));
            Assert.That(Refused("C", -10f, "C.1"), Does.StartWith("C would own").And.Contains("below the map's 100 m minimum cell"), "rule 7 binds the parent's side too");
            _world.maps[0].maxCellsPerBase = 1;
            Assert.That(Refused("C", 0f, "C.1"), Does.StartWith("maxCellsPerBase: C would be cut into 2 cells"));
            _world.maps[0].maxCellsPerBase = 0;
            _host.canSpawn = false;
            Assert.That(Refused("C", 0f, "C.1"), Is.EqualTo("no map-spawn server is registered"));
            _host.canSpawn = true;
            Assert.That(_c.CurrentOp, Is.Null, "nothing refused started an operation");
            RequestC();
            Assert.That(Refused("C", 50f, "C.2"), Is.EqualTo("split-1 is in progress"));
            Assert.That(_c.IsSplitFamily("C"), Is.True);
            Assert.That(_c.IsSplitFamily("E"), Is.False);
        }

        [Test]
        public void Cooldown_FollowsDone()
        {
            MeshSplitOperation op = ToDrainGate();
            Ack(ConnC, "C", "pc", op.e2Pair);
            _c.OnLayoutEvidence(ConnC, Drain("C", "pc", op.e2Pair, 0));
            Ack(ConnC, "C", "pc", op.e3Pair);
            Ack(ConnX, "C.1", "xc", op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));

            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.False);
            Assert.That(error, Is.EqualTo("cooldown 30 s left on C"));
            Assert.That(_c.IsSplitFamily("C"), Is.True, "a cut family stays out of the awake set");
        }
    }
}
