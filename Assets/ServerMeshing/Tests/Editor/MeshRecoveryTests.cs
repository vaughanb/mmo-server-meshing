using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// A central restart with a cut map (Slice 5, slices/slice5_design.md decisions 1-9): a first coordinator runs an
    /// operation on one store, stops (the process died), and a second coordinator on the same store, a fresh registry and
    /// a fresh transport is given the cells' hellos holding what the first one sent them. What the second one adopts,
    /// answers, re-adds, resumes, aborts or freezes is asserted here; the pure table is in MeshRecoveryResolverTests.
    /// </summary>
    public class MeshRecoveryTests
    {
        private const long ConnC = 7, ConnE = 8, ConnC2 = 9, ConnX = 20;
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);
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
        private readonly Dictionary<string, (long connection, string incarnation)> _bound = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _clock = new MeshFakeClock();
            _dir = Path.Combine(Path.GetTempPath(), "MeshRecoveryTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            NewCentral();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null)
                UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        /// <summary>NewCentral is a new process: a fresh registry, transport, log and host on the same store and clock; Start is the caller's.</summary>
        private void NewCentral()
        {
            if (_world != null)
                UnityEngine.Object.DestroyImmediate(_world);
            _world = RigLike(map1Dynamic: true);
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost();
            _lines.Clear();
            _bound.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
        }

        private void Hello(long connection, string cellId, string incarnation, MeshLayoutSnapshot held = null, string mapId = Map1, bool dynamicMap = true)
        {
            _transport.Register(connection, cellId, mapId);
            _c.OnLayoutHello(connection, new MeshLayoutHelloMessage { cellId = cellId, mapId = mapId, incarnation = incarnation, dynamicMap = dynamicMap, held = held ?? new MeshLayoutSnapshot(), processId = 100 + (int)connection });
            _bound[cellId] = (connection, incarnation);
        }

        private void Ack(string cellId, MeshLayoutPair pair)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = MeshLayoutAckResult.Applied });
        }

        private void AckAll(MeshLayoutPair pair)
        {
            foreach (string cell in new List<string>(_bound.Keys))
            {
                if (_transport.peers.ContainsKey(_bound[cell].connection))
                    Ack(cell, pair);
            }
        }

        private string[] OthersRegistered(string cellId)
        {
            List<string> others = new List<string>();
            foreach (MeshMapServerPeer peer in _transport.peers.Values)
            {
                if (!string.Equals(peer.cellId, cellId, StringComparison.OrdinalIgnoreCase))
                    others.Add(peer.cellId);
            }
            return others.ToArray();
        }

        /// <summary>HeldOf is the newest layout the first central sent to a cell (a broadcast, its staged step or its hello answer): what the cell holds at the restart.</summary>
        private MeshLayoutSnapshot HeldOf(string cellId)
        {
            long connection = _bound[cellId].connection;
            MeshLayoutSnapshot newest = null;
            foreach ((long to, MeshLayoutMessage message) in _transport.layouts)
            {
                if (to != connection || message.snapshot == null)
                    continue;
                if (newest == null || message.snapshot.pair.IsNewerThan(newest.pair))
                    newest = message.snapshot;
            }
            return newest != null ? newest.Clone() : new MeshLayoutSnapshot();
        }

        /// <summary>FirstCentral starts a central with C and E bound (incarnations c1, e1) and returns it running.</summary>
        private void FirstCentral()
        {
            _c.Start();
            Hello(ConnC, "C", "c1");
            Hello(ConnE, "E", "e1");
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
        }

        /// <summary>SplitTo runs `split C x 0 + C.1` up to the given step (E2Staged, DrainGate, E3Broadcast or Done).</summary>
        private MeshSplitOperation SplitTo(MeshSplitStep step)
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnSpawnResult(op.opId, true, "Success");
            Hello(ConnX, "C.1", "x1");
            if (step == MeshSplitStep.E1Staged)
                return op;
            Ack("C.1", op.e1Pair);
            if (step == MeshSplitStep.E1Gate)
                return op;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnX, MeshSplitOperationTests.Prewarm("C.1", "x1", op.e1Pair, OthersRegistered("C.1")));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged), op.lastGateWait);
            if (step == MeshSplitStep.E2Staged)
                return op;
            Ack("C.1", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate), op.lastGateWait);
            if (step == MeshSplitStep.DrainGate)
                return op;
            AckAll(op.e2Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast), op.lastGateWait);
            if (step == MeshSplitStep.E3Broadcast)
                return op;
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            return op;
        }

        /// <summary>Restart: the first central dies (Stop touches no store), a second one starts on the store and the cells say hello holding what they were sent; every old cell's held layout is returned.</summary>
        private Dictionary<string, MeshLayoutSnapshot> Restart(params string[] cells)
        {
            Dictionary<string, MeshLayoutSnapshot> held = new Dictionary<string, MeshLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (string cell in _bound.Keys)
                held[cell] = HeldOf(cell);
            Dictionary<string, (long connection, string incarnation)> was = new Dictionary<string, (long, string)>(_bound, StringComparer.OrdinalIgnoreCase);
            _c.Stop();
            NewCentral();
            _clock.Now += 10f;
            _clock.UtcTicks += 1000;
            _c.Start();
            foreach (string cell in cells)
                Hello(was[cell].connection, cell, was[cell].incarnation, held[cell]);
            return held;
        }

        private List<MeshLayoutMessage> AnswersTo(string cellId)
        {
            List<MeshLayoutMessage> answers = new List<MeshLayoutMessage>();
            foreach ((long to, MeshLayoutMessage message) in _transport.layouts)
            {
                if (to == _bound[cellId].connection && message.kind == MeshLayoutKind.HelloAnswer)
                    answers.Add(message);
            }
            return answers;
        }

        // ---- a split done ----------------------------------------------------------------------------------------------

        [Test]
        public void ASplitDone_TheRestartAdoptsTheStore_AnswersEveryHelloAndReaddsTheChannel()
        {
            FirstCentral();
            MeshSplitOperation split = SplitTo(MeshSplitStep.Done);
            MeshLayoutPair e3 = split.e3Pair;
            MeshLayoutSnapshot heldC = HeldOf("C"), heldX = HeldOf("C.1");

            _c.Stop();
            NewCentral();
            _clock.UtcTicks += 1000;
            _c.Start();
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Recovering), "the store names C.1: the map waits");
            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.Waiting));
            Hello(ConnC, "C", "c1", heldC);
            Assert.That(_transport.layouts, Is.Empty, "nobody is answered while C.1 is missing");
            Hello(ConnE, "E", "e1");
            Hello(ConnX, "C.1", "x1", heldX);
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.AdoptStored));
            Assert.That(_c.AdoptedPair, Is.EqualTo(e3));
            Assert.That(_c.BroadcastPair, Is.EqualTo(e3));
            Assert.That(_world.AppliedPair, Is.EqualTo(e3), "the central's own registry holds the adopted layout");
            Assert.That(_world.GetCellById("C.1").owned, Is.EqualTo(East));
            Assert.That(_host.channels, Does.Contain("C.1"), "the child's channel is back");
            foreach (string cell in new[] { "C", "E", "C.1" })
                Assert.That(AnswersTo(cell).Count, Is.EqualTo(1), cell + " answered once");
            Assert.That(AnswersTo("C")[0].snapshot.pair, Is.EqualTo(e3));
            Assert.That(_log.infos, Has.Some.Contains($"Coordinator recovery: map {Map1} adopted layout {e3} from the store with 2 entries (C, C.1)"));
            Assert.That(_log.infos, Has.Some.Contains("Coordinator policy: cooldowns start fresh for C after the restart"));
            Assert.That(_c.OwnBroadcastSent, Is.False);
            Assert.That(_c.CurrentOp, Is.Null, "a done split is not stored");
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.False);
            Assert.That(error, Does.Contain("cooldown"));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.hasOperation, data.broadcast.pair), Is.EqualTo((false, e3)));
        }

        [Test]
        public void ASplitDone_TheHelloOfTheChildCountsEvenWhenItRestartedWithNoLayout()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            Restart("C", "E");
            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.Waiting));

            Hello(ConnX, "C.1", "x2");   // a restarted child: no layout yet, and not retired

            Assert.That(_transport.retires, Is.Empty, "a named child is never asked to retire while the map waits");
            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.AdoptStored));
            Assert.That(AnswersTo("C.1").Count, Is.EqualTo(1));
        }

        [Test]
        public void AStaticNeighbour_IsAnsweredOnlyOnceTheDynamicMapsRecovered()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            Dictionary<string, MeshLayoutSnapshot> held = Restart("C", "E");
            Hello(ConnC2, "C2", "z1", null, Map2, false);
            Assert.That(_transport.layouts, Is.Empty, "C2 waits with everyone");

            Hello(ConnX, "C.1", "x1", held["C.1"]);

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(AnswersTo("C2").Count, Is.EqualTo(1));
            Assert.That(AnswersTo("C2")[0].snapshot.entries.Count, Is.EqualTo(2), "C2 gets the adopted layout, not an empty newer one");
        }

        // ---- a split in flight -------------------------------------------------------------------------------------

        [Test]
        public void ASplitAtTheDrainGate_ResumesThere_AndReachesDoneOnFreshEvidence()
        {
            FirstCentral();
            MeshSplitOperation before = SplitTo(MeshSplitStep.DrainGate);
            MeshLayoutPair e2 = before.e2Pair;
            Restart("C", "E", "C.1");

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(_c.AdoptedPair, Is.EqualTo(e2));
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op, Is.Not.Null);
            Assert.That((op.opId, op.step), Is.EqualTo((before.opId, MeshSplitStep.DrainGate)));
            Assert.That(_lines, Has.Some.EqualTo($"Split {op.opId}: resumed at DrainGate after a central restart"));
            Assert.That((op.childBound, op.childIncarnation, op.parentIncarnation), Is.EqualTo((true, "x1", "c1")));

            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", e2, 0));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast), op.lastGateWait);
            Assert.That(op.e3Pair.coordinatorIncarnation, Is.EqualTo(_c.Incarnation), "e3 is this central's own");
            Assert.That(_c.OwnBroadcastSent, Is.True);
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.False, "split-2 would reuse the number otherwise");
            Assert.That(error, Does.Contain("cooldown"));
        }

        [Test]
        public void ASplitWhoseE3WentOut_ResumesAtE3_AndIsDoneFromTheHellos()
        {
            FirstCentral();
            MeshSplitOperation before = SplitTo(MeshSplitStep.E3Broadcast);
            Restart("C", "E", "C.1");

            Assert.That(_c.AdoptedPair, Is.EqualTo(before.e3Pair));
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            Assert.That(_lines, Has.Some.EqualTo($"Split {op.opId}: resumed at E3Broadcast after a central restart"));
            Assert.That(_lines, Has.Some.EqualTo($"Split {op.opId}: done"));
        }

        [Test]
        public void ASplitWithE2StagedAndHeldByTheChild_BroadcastsE2()
        {
            FirstCentral();
            MeshSplitOperation before = SplitTo(MeshSplitStep.E2Staged);
            Restart("C", "E", "C.1");   // C.1 holds the staged e2, C and E the e1 broadcast

            Assert.That(_c.AdoptedPair, Is.EqualTo(before.e2Pair), "the staged e2 the child holds is the newest consistent layout: adopted");
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate), op.lastGateWait);
            Assert.That(_lines, Has.Some.EqualTo($"Split {op.opId}: e2 broadcast {before.e2Pair}"));
            Assert.That(_world.AppliedPair, Is.EqualTo(before.e2Pair));
        }

        [Test]
        public void ASplitWithE2StagedButNotHeld_AbortsWithAFencedRetireAndTheRetiredBroadcast()
        {
            FirstCentral();
            MeshSplitOperation before = SplitTo(MeshSplitStep.E2Staged);
            MeshLayoutSnapshot heldE1 = HeldOf("C");
            Dictionary<string, (long, string)> was = new Dictionary<string, (long, string)>(_bound);
            _c.Stop();
            NewCentral();
            _clock.UtcTicks += 1000;
            _c.Start();
            Hello(was["C"].Item1, "C", "c1", heldE1);
            Hello(was["E"].Item1, "E", "e1", heldE1);
            Hello(was["C.1"].Item1, "C.1", "x1", heldE1.Clone());   // the child never applied e2

            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(_lines, Has.Some.EqualTo($"Split {op.opId}: aborted the central restarted before e2"));
            Assert.That(_transport.retires.Count, Is.EqualTo(1));
            Assert.That((_transport.retires[0].message.cellId, _transport.retires[0].message.reason), Is.EqualTo(("C.1", "fenced: aborted by " + op.opId)));
            Assert.That(_lines, Has.Some.Contains("retired broadcast"));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_host.removed, Does.Contain("C.1"));
            Assert.That(_c.OwnBroadcastSent, Is.True);
        }

        [Test]
        public void ASplitAtTheE1Gate_AbortsAndRetiresTheChildThroughTheBroadcast()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.E1Gate);
            Restart("C", "E", "C.1");

            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Assert.That(_transport.retires[0].message.reason, Is.EqualTo("aborted by " + op.opId), "a Pending child quits on a plain retire");
            Assert.That(_lines, Has.Some.Contains("retired broadcast"));
        }

        [Test]
        public void ASplitStillSpawning_IsAbortedAndItsLateChildRetired()
        {
            FirstCentral();
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            string opId = _c.CurrentSplit.opId;
            Restart("C", "E");

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running), "the child's absence is the step's outcome: not waited for");
            Assert.That(_lines, Has.Some.EqualTo($"Split {opId}: aborted the central restarted before e1"));
            Hello(ConnX, "C.1", "x1");
            Assert.That(_transport.retires.Count, Is.EqualTo(1));
            Assert.That(_transport.retires[0].message.reason, Is.EqualTo("aborted by " + opId));
        }

        // ---- a merge in flight -------------------------------------------------------------------------------------

        private MeshMergeOperation MergeTo(MeshMergeStep step)
        {
            _clock.Now += 31f;
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            if (step == MeshMergeStep.E1Gate)
                return op;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "c1", op.e1Pair, OthersRegistered("C")));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
            if (step == MeshMergeStep.E2Staged)
                return op;
            Ack("C", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate), op.lastGateWait);
            if (step == MeshMergeStep.DrainGate)
                return op;
            AckAll(op.e2Pair);
            _clock.Now += 1f;
            _c.Tick();
            _clock.Now += 6f;
            _c.OnLayoutEvidence(ConnX, MeshMergeOperationTests.Drained("C.1", "x1", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), op.lastGateWait);
            return op;
        }

        [Test]
        public void AMergeAtRetiring_WithTheRetiringCellGone_ReachesDoneWithoutWaitingForIt()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            MeshMergeOperation before = MergeTo(MeshMergeStep.Retiring);
            Restart("C", "E");   // C.1 quit on its Retire while the central was down

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running), "the retiring cell's absence is the exit the step waits for");
            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That((op.opId, op.step), Is.EqualTo((before.opId, MeshMergeStep.Retiring)));
            Assert.That(_lines, Has.Some.EqualTo($"Merge {op.opId}: resumed at Retiring after a central restart"));
            for (int i = 0; i < 6; ++i)
            {
                _clock.Now += 1f;
                _c.Tick();
            }
            Assert.That(_lines, Has.Some.EqualTo($"Merge {op.opId}: C.1 exited"));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), op.lastGateWait);
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.TidyBroadcast), op.lastGateWait);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done), op.lastGateWait);
            Assert.That(_host.removed, Does.Contain("C.1"));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _) && !data.hasOperation, Is.True);
        }

        [Test]
        public void AMergeAtTheDrainGate_ResumesAndSendsRetireOnFreshDrainedEvidence()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            MeshMergeOperation before = MergeTo(MeshMergeStep.DrainGate);
            Restart("C", "E", "C.1");

            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That((op.opId, op.step), Is.EqualTo((before.opId, MeshMergeStep.DrainGate)));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Draining));
            _clock.Now += 1f;
            _c.Tick();
            _clock.Now += 6f;
            _c.OnLayoutEvidence(ConnX, MeshMergeOperationTests.Drained("C.1", "x1", before.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), op.lastGateWait);
            Assert.That(_transport.retires, Has.Some.Matches<(long connection, MeshRetireMessage message)>(r => r.message.cellId == "C.1" && r.message.reason == "merged into C"));
        }

        [Test]
        public void AMergeBeforeItsCommit_IsRevertedByTheRestart()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            MeshMergeOperation before = MergeTo(MeshMergeStep.E1Gate);
            Restart("C", "E", "C.1");

            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Aborted));
            Assert.That(_lines, Has.Some.EqualTo($"Merge {before.opId}: aborted the central restarted before e2"));
            Assert.That(_lines, Has.Some.Contains("reverted broadcast"));
            Assert.That(_world.GetCellById("C").interest, Is.EqualTo(West), "the survivor's interest is back to its own rectangle");
        }

        // ---- a frozen family, a lost store, a re-open --------------------------------------------------------------------

        [Test]
        public void AChildThatNeverSaysHello_FreezesItsFamilyAtTheCeiling_UntilItDoes()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            Restart("C", "E");
            _clock.Now += 61f;
            _c.Tick();

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(_c.OutcomeOf(Map1), Is.EqualTo(MeshRecoveryOutcome.AdoptStored));
            Assert.That(_c.IsFamilyFrozen("C"), Is.True);
            Assert.That(_log.errors, Has.Some.Contains($"Coordinator recovery: family C of map {Map1} frozen: no hello from C.1 after 60 s"));
            Assert.That(AnswersTo("C").Count, Is.EqualTo(1), "the family's live cells are answered");
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.False);
            Assert.That(error, Does.Contain("family C is frozen"));
            Assert.That(_c.SupervisedOf("C.1"), Is.Null, "no supervision of a frozen family");

            Hello(ConnX, "C.1", "x1", HeldOf("C"));
            Assert.That(_c.IsFamilyFrozen("C"), Is.False);
            Assert.That(_log.infos, Has.Some.Contains("unfrozen: C.1 said hello"));
            Assert.That(AnswersTo("C.1").Count, Is.EqualTo(1));
        }

        [Test]
        public void ALostStore_AdoptsTheAssetCutAtStart_ThenTheLayoutTheCellsHold()
        {
            FirstCentral();
            MeshSplitOperation split = SplitTo(MeshSplitStep.Done);
            MeshLayoutSnapshot heldC = HeldOf("C"), heldX = HeldOf("C.1");
            _c.Stop();
            File.Delete(_store.Path);
            NewCentral();
            _clock.UtcTicks += 1000;
            _c.Start();
            Assert.That((_c.Phase, _c.OutcomeOf(Map1)), Is.EqualTo((MeshCoordinatorPhase.Running, MeshRecoveryOutcome.AdoptStatic)));

            Hello(ConnC, "C", "c1", heldC);

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Recovering), "a held layout re-opens an asset-cut recovery");
            Assert.That(_log.warnings, Has.Some.Contains("re-opens recovery"));
            Assert.That(_transport.layouts, Is.Empty, "C is not answered with an empty newer layout");
            Hello(ConnX, "C.1", "x1", heldX);
            Assert.That((_c.Phase, _c.OutcomeOf(Map1), _c.AdoptedPair), Is.EqualTo((MeshCoordinatorPhase.Running, MeshRecoveryOutcome.AdoptStored, split.e3Pair)));
            Assert.That(_log.infos, Has.Some.Contains("from cell C with 2 entries (C, C.1)"));
            Assert.That(_host.channels, Does.Contain("C.1"));
        }

        [Test]
        public void AfterAnOwnBroadcast_ANewerHeldLayoutDoesNotReopen_ItIsAnsweredWithTheCurrentOne()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.DrainGate);
            Restart("C", "E", "C.1");
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That(_c.OwnBroadcastSent, Is.True);
            // A layout of the OLD incarnation, newer than the adopted e2: what a partitioned cell could still hold.
            MeshLayoutSnapshot stale = MeshFakeWorlds.Snapshot(0, Active("C", "C", West), Active("C.1", "C", East));
            stale.pair = new MeshLayoutPair(_c.AdoptedPair.coordinatorIncarnation, _c.AdoptedPair.version + 5);

            Hello(ConnE, "E", "e2", stale);

            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
            Assert.That(_log.errors, Has.Some.Contains("after the central moved on; answered with the current layout"));
            List<MeshLayoutMessage> answers = AnswersTo("E");
            Assert.That(answers[answers.Count - 1].snapshot.pair, Is.EqualTo(_c.BroadcastPair));
        }

        [Test]
        public void ACellOwningSomethingThatTheLayoutNoLongerNames_GetsAFencedRetire()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            MeshSplitOperation merged = null;
            _ = merged;
            MeshMergeOperation merge = MergeTo(MeshMergeStep.Retiring);
            for (int i = 0; i < 6; ++i)
            {
                _transport.peers.Remove(ConnX);
                _clock.Now += 1f;
                _c.Tick();
            }
            AckAll(merge.e3Pair);
            AckAll(merge.tidyPair);
            Assert.That(merge.step, Is.EqualTo(MeshMergeStep.Done), merge.lastGateWait);
            Restart("C", "E");
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));

            MeshLayoutSnapshot zombie = MeshFakeWorlds.Snapshot(3, Active("C", "C", West), Active("C.1", "C", East));
            zombie.pair = new MeshLayoutPair(1, 3);
            Hello(ConnX, "C.1", "x9", zombie);

            Assert.That(_transport.retires.Count, Is.EqualTo(1));
            Assert.That(_transport.retires[0].message.reason, Does.StartWith("fenced: "));
        }

        [Test]
        public void TheStore_NamesTheStepEntered_AndRoundTripsTheOperation()
        {
            FirstCentral();
            MeshSplitOperation op = SplitTo(MeshSplitStep.E2Staged);

            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out string error), Is.True, error);
            Assert.That((data.opStep, data.hasStaged, data.hasOperation), Is.EqualTo(("E2Staged", true, true)));
            Assert.That((data.operation.kind, data.operation.opId, data.operation.step, data.operation.childId, data.operation.e2Pair), Is.EqualTo(("split", op.opId, "E2Staged", "C.1", op.e2Pair)));
            Assert.That(data.operation.kept, Is.EqualTo(West));
            Assert.That(data.operation.given, Is.EqualTo(East));
            Assert.That(data.broadcast.pair, Is.EqualTo(op.e1Pair));
            Assert.That(MeshStoredOperation.E1Broadcast(data.broadcast.pair, data.operation.e1Pair), Is.True);
        }
    }
}
