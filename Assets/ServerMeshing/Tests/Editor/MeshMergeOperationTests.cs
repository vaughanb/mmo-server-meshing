using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The coordinator's merge, driven step by step through fakes (Slice 2c decisions 1-7): C is split at x = 0 (child
    /// C.1 takes x [0, 100)), then C.1 is merged back. MeshMergeGatesTests holds the pure parts; the refusals, aborts and
    /// freezes are in MeshMergeOperationTests_Failures.cs.
    /// </summary>
    public partial class MeshMergeOperationTests
    {
        private const long ConnC = 7, ConnE = 8, ConnX = 20, ConnY = 21;
        private static readonly MeshRect Whole = new MeshRect(-100f, -100f, 100f, 100f);
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
            _world = RigLike(map1Dynamic: true);
            _clock = new MeshFakeClock();
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost();
            _dir = Path.Combine(Path.GetTempPath(), "MeshMergeTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            _lines.Clear();
            _bound.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
            _c.Start();
            Bind(ConnC, "C", "pc");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        private void Bind(long connection, string cellId, string incarnation)
        {
            _transport.Register(connection, cellId, Map1);
            _c.OnLayoutHello(connection, new MeshLayoutHelloMessage { cellId = cellId, mapId = Map1, incarnation = incarnation, dynamicMap = true, held = new MeshLayoutSnapshot() });
            _bound[cellId] = (connection, incarnation);
        }

        private void Ack(string cellId, MeshLayoutPair pair, bool applied = true)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = applied ? MeshLayoutAckResult.Applied : MeshLayoutAckResult.Rejected, reason = applied ? null : "refused" });
        }

        /// <summary>AckAll: every bound, registered cell acknowledges the pair (a non-participant's ack changes nothing).</summary>
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

        /// <summary>RunSplit drives one split to done, every cell answering at once; then the cooldown runs out.</summary>
        private MeshSplitOperation RunSplit(string parent, MeshSplitAxis axis, float at, bool positive, string child, long connection, string incarnation)
        {
            Assert.That(_c.RequestSplit(parent, axis, at, positive, child, out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnSpawnResult(op.opId, true, "Success");
            Bind(connection, child, incarnation);
            Ack(child, op.e1Pair);
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(connection, MeshSplitOperationTests.Prewarm(child, incarnation, op.e1Pair, OthersRegistered(child)));
            Ack(child, op.e2Pair);
            AckAll(op.e2Pair);
            (long parentConnection, string parentIncarnation) = _bound[parent];
            _c.OnLayoutEvidence(parentConnection, MeshSplitOperationTests.Drain(parent, parentIncarnation, op.e2Pair, 0));
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            return op;
        }

        private void SplitC(bool waitCooldown = true)
        {
            RunSplit("C", MeshSplitAxis.X, 0f, true, "C.1", ConnX, "xc");
            if (waitCooldown)
                _clock.Now += 31f;
        }

        private MeshMergeOperation RequestMerge(string a = "C.1", string b = "C")
        {
            Assert.That(_c.RequestMerge(a, b, out string error), Is.True, error);
            return _c.CurrentMerge;
        }

        /// <summary>The survivor's evidence: every other registered cell heard, an empty queue, and (on purpose) a saturated window.</summary>
        private MeshLayoutEvidenceMessage Survivor(MeshLayoutPair pair, string cellId = "C")
        {
            MeshLayoutEvidenceMessage evidence = MeshSplitOperationTests.Prewarm(cellId, _bound[cellId].incarnation, pair, OthersRegistered(cellId));
            evidence.saturatedShare = 0.5f;
            return evidence;
        }

        internal static MeshLayoutEvidenceMessage Drained(string cellId, string incarnation, MeshLayoutPair pair, bool drained = true, MeshDrainState state = default)
        {
            return new MeshLayoutEvidenceMessage
            {
                cellId = cellId, incarnation = incarnation, pair = pair, windowStartedAfterApplySeconds = 0.5f,
                neighbours = new List<MeshNeighbourEvidence>(), drainState = state, drained = drained,
            };
        }

        private void ToE2Staged(MeshMergeOperation op)
        {
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(_bound[op.survivorId].connection, Survivor(op.e1Pair, op.survivorId));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
        }

        private void ToDrainGate(MeshMergeOperation op)
        {
            ToE2Staged(op);
            Ack(op.survivorId, op.e2Pair);
            AckAll(op.e2Pair);
            Assert.That((op.step, op.e2AckedAt >= 0f), Is.EqualTo((MeshMergeStep.DrainGate, true)), op.lastGateWait);
        }

        private void ToRetiring(MeshMergeOperation op)
        {
            ToDrainGate(op);
            _clock.Now += 5f;
            (long connection, string incarnation) = _bound[op.retiringId];
            _c.OnLayoutEvidence(connection, Drained(op.retiringId, incarnation, op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), op.lastGateWait);
        }

        /// <summary>Exit: the retiring cell leaves the cluster and stays gone 5 s.</summary>
        private void Exit(MeshMergeOperation op)
        {
            _transport.peers.Remove(_bound[op.retiringId].connection);
            _c.Tick();
            _clock.Now += 5f;
            _c.Tick();
        }

        private List<string> MergeLines() => _lines.FindAll(l => l.StartsWith("Merge ", StringComparison.Ordinal));

        private static string L(string text) => "Merge merge-1: " + text;

        private List<(long connection, MeshLayoutMessage message)> Layouts(MeshLayoutKind kind) => _transport.layouts.FindAll(l => l.message.kind == kind);

        private List<(long connection, MeshRetireMessage message)> Retires(long connection) => _transport.retires.FindAll(r => r.connection == connection && r.message.reason == "merged into C");

        // ---- the happy path ------------------------------------------------------------------------------------------

        [Test]
        public void Merge_RunsEveryStepInOrder_ToDone_AndTidiesTheFamily()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Exit(op);
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);

            Assert.That(MergeLines(), Is.EqualTo(new[]
            {
                L("requested C C.1"), L("e1 broadcast f4240/5"), L("e1 gate open"), L("e2 staged"), L("e2 acked"),
                L("e2 broadcast f4240/6"), L("drain gate open"), L("retire sent"), L("C.1 exited"), L("channel C.1 removed"),
                L("e3 broadcast f4240/7"), L("tidy broadcast f4240/8"), L("done"),
            }));
            Assert.That((op.step, op.survivorId, op.retiringId, op.union), Is.EqualTo((MeshMergeStep.Done, "C", "C.1", Whole)));
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            Assert.That(_host.channels, Is.Empty);
            Assert.That(Retires(ConnX).Count, Is.EqualTo(1));
            Assert.That(Retires(ConnX)[0].message.incarnation, Is.EqualTo("xc"));
            MeshCell c = _world.GetCellById("C"), child = _world.GetCellById("C.1");
            Assert.That((c.state, c.owned, c.interest), Is.EqualTo((MeshCellState.Active, Whole, Whole)));
            Assert.That((child.state, child.owned.IsEmpty), Is.EqualTo((MeshCellState.Retired, true)), "the retired id stays resolvable");
            Assert.That(_world.AppliedPair, Is.EqualTo(op.tidyPair));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.broadcast.pair, data.hasStaged, data.opStep), Is.EqualTo((op.tidyPair, false, "Done")));
            Assert.That(data.broadcast.entries.FindAll(e => e.baseId == "C"), Is.Empty, "the tidy drops the family's entries");
            Assert.That(_c.IsSplitFamily("C"), Is.False, "a family whole again may sleep");
            Assert.That(_log.warnings, Has.None.StartsWith("Merge "));
        }

        [Test]
        public void E1_WidensTheSurvivorsInterest_AndIsBroadcastAtOnce()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge("C", "C.1");

            MeshLayoutMessage e1 = Layouts(MeshLayoutKind.Broadcast)[Layouts(MeshLayoutKind.Broadcast).Count - 1].message;
            Assert.That((e1.stepId, e1.snapshot.pair), Is.EqualTo(("merge-1/e1", op.e1Pair)));
            MeshLayoutEntry c = e1.snapshot.entries.Find(e => e.id == "C"), child = e1.snapshot.entries.Find(e => e.id == "C.1");
            Assert.That((c.state, c.owned, c.interest), Is.EqualTo((MeshCellState.Active, West, Whole)));
            Assert.That((child.state, child.owned, child.interest), Is.EqualTo((MeshCellState.Active, East, East)));
            Assert.That(op.participants, Is.EquivalentTo(new[] { "C", "C.1" }));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E1Gate));
        }

        [Test]
        public void E1Gate_IgnoresSaturation_ButWaitsForEveryAckAndAFreshWindow()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            _c.OnLayoutEvidence(ConnC, Survivor(op.e1Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E1Gate), "no ack yet");
            Ack("C", op.e1Pair);
            Ack("C.1", op.e1Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), "a saturated survivor still opens its e1 gate");

            Assert.That(op.lastGateWait, Is.Empty);
        }

        [Test]
        public void E1Gate_NeedsTheSurvivorsHelloFromEachBandNeighbour()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "pc", op.e1Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E1Gate));
            Assert.That(op.lastGateWait, Is.EqualTo("no hello from C.1"));
            MeshLayoutEvidenceMessage queued = Survivor(op.e1Pair);
            queued.prewarmQueue = 2;
            _c.OnLayoutEvidence(ConnC, queued);
            Assert.That(op.lastGateWait, Is.EqualTo("prewarm queue 2"));

            _c.OnLayoutEvidence(ConnC, Survivor(op.e1Pair));

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged));
        }

        [Test]
        public void E2_IsStagedToTheSurvivorAlone_AndItsHelloGetsItAgain()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            int broadcasts = Layouts(MeshLayoutKind.Broadcast).Count;
            ToE2Staged(op);

            (long connection, MeshLayoutMessage staged) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((connection, staged.kind, staged.targetCellId, staged.targetIncarnation, staged.stepId), Is.EqualTo((ConnC, MeshLayoutKind.Staged, "C", "pc", "merge-1/e2")));
            MeshLayoutEntry c = staged.snapshot.entries.Find(e => e.id == "C"), child = staged.snapshot.entries.Find(e => e.id == "C.1");
            Assert.That((c.state, c.owned, c.interest), Is.EqualTo((MeshCellState.Active, Whole, Whole)));
            Assert.That((child.state, child.owned.IsEmpty, child.interest), Is.EqualTo((MeshCellState.Draining, true, East)));
            Assert.That(Layouts(MeshLayoutKind.Broadcast).Count, Is.EqualTo(broadcasts), "nobody else sees e2 before the survivor acks it");
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.hasStaged, data.stagedStepId, data.stagedFirstApplier), Is.EqualTo((true, "merge-1/e2", "C")));

            // The survivor says hello again (same process, a new connection): it is answered with the staged e2.
            _transport.peers.Remove(ConnC);
            Bind(ConnC + 100, "C", "pc");
            (long again, MeshLayoutMessage answer) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((again, answer.kind, answer.stepId, answer.snapshot.pair), Is.EqualTo((ConnC + 100, MeshLayoutKind.HelloAnswer, "merge-1/e2", op.e2Pair)));

            Ack("C", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate));
            Assert.That(_lines, Has.Some.EqualTo(L("e2 acked")));
        }

        [Test]
        public void E2Participants_IncludeEveryCellBandingWithTheRetiringCell()
        {
            Bind(ConnE, "E", "pe");
            SplitC();
            MeshMergeOperation op = RequestMerge();
            Assert.That(op.participants, Is.EquivalentTo(new[] { "C", "C.1", "E" }), "e1: E's band gains C (its interest now reaches E)");
            ToE2Staged(op);
            Ack("C", op.e2Pair);

            // Under e2 E's band set and rectangles do not change, yet it routes handoffs to C.1 until it applies e2.
            Assert.That(op.participants, Is.EquivalentTo(new[] { "C", "C.1", "E" }));
            Ack("C.1", op.e2Pair);
            Assert.That(op.e2AckedAt, Is.LessThan(0f), "E has not acked e2");
            Assert.That(op.lastGateWait, Is.EqualTo("no e2 ack from E"));

            Ack("E", op.e2Pair);
            Assert.That(op.e2AckedAt, Is.EqualTo(_clock.Now));
        }

        [Test]
        public void DrainGate_CountsOnlyAWindowReceivedALoadWindowAfterTheLastE2Ack()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToE2Staged(op);
            Ack("C", op.e2Pair);
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate), "C.1 has not acked e2");
            Ack("C.1", op.e2Pair);
            float ackedAt = op.e2AckedAt;

            _clock.Now += 4f;
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate));
            Assert.That(op.lastGateWait, Does.StartWith("the window arrived 4 s after the last e2 ack"));
            _clock.Now += 1f;
            MeshLayoutEvidenceMessage early = Drained("C.1", "xc", op.e2Pair);
            early.windowStartedAfterApplySeconds = -1f;
            _c.OnLayoutEvidence(ConnX, early);
            Assert.That(op.lastGateWait, Is.EqualTo("the window started before the apply"));
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair, false, new MeshDrainState { characters = 1, pendingAcks = 1 }));
            Assert.That(op.lastGateWait, Is.EqualTo("still holds characters=1 pendingAcks=1"));
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "other", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate), "another incarnation's evidence is not C.1's");

            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair));

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring));
            Assert.That(_clock.Now - ackedAt, Is.EqualTo(5f));
            Assert.That(_lines, Has.Some.EqualTo(L("drain gate open")));
        }

        [Test]
        public void Retire_IsSentToTheBoundConnection_AndResentEvery10s()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Assert.That(Retires(ConnX).Count, Is.EqualTo(1));
            Assert.That((Retires(ConnX)[0].message.cellId, Retires(ConnX)[0].message.incarnation), Is.EqualTo(("C.1", "xc")));

            _clock.Now += 9f;
            _c.Tick();
            Assert.That(Retires(ConnX).Count, Is.EqualTo(1));
            _clock.Now += 1f;
            _c.Tick();
            Assert.That(Retires(ConnX).Count, Is.EqualTo(2));
            _clock.Now += 10f;
            _c.Tick();
            Assert.That(Retires(ConnX).Count, Is.EqualTo(3));
            Assert.That(_lines.FindAll(l => l == L("retire sent")).Count, Is.EqualTo(1), "a resend is not logged");
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring));
        }

        [Test]
        public void Exited_NeedsFiveSecondsUnregisteredWithoutABreak()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            _transport.peers.Remove(ConnX);
            _c.Tick();
            _clock.Now += 3f;
            _transport.Register(ConnX, "C.1", Map1);   // a cluster blip
            _c.Tick();
            _clock.Now += 1f;
            _transport.peers.Remove(ConnX);
            _c.Tick();
            _clock.Now += 4f;
            _c.Tick();
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), "4 s gone since the blip");

            _clock.Now += 1f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast));
            Assert.That(_lines, Has.Some.EqualTo(L("C.1 exited")));
            Assert.That(_lines, Has.Some.EqualTo(L("channel C.1 removed")));
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
        }

        [Test]
        public void E3_RetiresTheExitedCell_WithoutWaitingForIt()
        {
            Bind(ConnE, "E", "pe");
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Exit(op);

            MeshLayoutMessage e3 = Layouts(MeshLayoutKind.Broadcast)[Layouts(MeshLayoutKind.Broadcast).Count - 1].message;
            Assert.That((e3.stepId, e3.snapshot.pair), Is.EqualTo(("merge-1/e3", op.e3Pair)));
            MeshLayoutEntry child = e3.snapshot.entries.Find(e => e.id == "C.1");
            Assert.That((child.state, child.owned.IsEmpty, child.interest.IsEmpty), Is.EqualTo((MeshCellState.Retired, true, true)));
            Assert.That(op.participants, Does.Not.Contain("C.1"), "an exited cell is no participant");
            Assert.That(op.participants, Is.EquivalentTo(new[] { "C", "E" }), "E's band loses C.1");
            Assert.That(_transport.layouts.FindAll(l => l.connection == ConnX && l.message.stepId == "merge-1/e3"), Is.Empty);
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));

            Ack("C", op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), "E has not acked");
            Ack("E", op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.TidyBroadcast));
        }

        [Test]
        public void RetiringCellExitingBeforeDrained_GoesStraightToE3()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToDrainGate(op);
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair, false, new MeshDrainState { characters = 2 }));

            Exit(op);

            Assert.That(MergeLines().GetRange(MergeLines().Count - 3, 3), Is.EqualTo(new[] { L("C.1 exited before drained"), L("channel C.1 removed"), L("e3 broadcast f4240/7") }));
            Assert.That(_log.warnings, Has.Some.EqualTo(L("C.1 exited before drained")));
            Assert.That(Retires(ConnX), Is.Empty, "no Retire to a cell that is gone");
            Assert.That((op.step, op.exitedBeforeDrained), Is.EqualTo((MeshMergeStep.E3Broadcast, true)));
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done));
        }

        [Test]
        public void ThreeCellFamily_EndsWithoutTheTidy_AndKeepsTheRetiredEntry()
        {
            SplitC();
            RunSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", ConnY, "x2");
            _clock.Now += 31f;
            MeshMergeOperation op = RequestMerge("C.2", "C");
            Assert.That(op.union, Is.EqualTo(West));
            ToDrainGate(op);
            _clock.Now += 5f;
            _c.OnLayoutEvidence(ConnY, Drained("C.2", "x2", op.e2Pair));
            Exit(op);
            AckAll(op.e3Pair);

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done));
            Assert.That(MergeLines(), Has.None.Contains("tidy"));
            Assert.That(_world.GetCellById("C.2").state, Is.EqualTo(MeshCellState.Retired));
            MeshLayoutSnapshot held = _world.AppliedSnapshot;
            Assert.That(held.entries.Find(e => e.id == "C.2").state, Is.EqualTo(MeshCellState.Retired), "the retired entry stays in the broadcast");
            Assert.That(held.entries.Find(e => e.id == "C").owned, Is.EqualTo(West));
            Assert.That(_c.IsSplitFamily("C"), Is.True, "C.1 is still cut from C");
        }

        [Test]
        public void ARetiredChildId_MayBeSplitAgain_OnceUnregisteredAndItsChannelRemoved()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Exit(op);
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done));
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.False);
            Assert.That(error, Is.EqualTo("cooldown 30 s left on C"), "the merge's done starts the family's cooldown");
            _clock.Now += 31f;
            _transport.Register(ConnX + 1, "C.1", Map1);
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out error), Is.False);
            Assert.That(error, Is.EqualTo("a map server of C.1 is still registered"));
            _transport.peers.Remove(ConnX + 1);

            MeshSplitOperation again = RunSplit("C", MeshSplitAxis.X, 0f, true, "C.1", ConnX + 2, "xc2");

            Assert.That(again.opId, Is.EqualTo("split-2"), "splits and merges are numbered on their own");
            Assert.That(_host.channels, Is.EqualTo(new[] { "C.1" }));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Active));
            Assert.That(_c.BindingOf("C.1").incarnation, Is.EqualTo("xc2"));
        }
    }
}
