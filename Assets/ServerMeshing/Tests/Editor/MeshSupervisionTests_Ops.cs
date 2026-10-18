using System.Collections.Generic;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Operations and their members' crashes (Slice 2d decision 6): a lost member aborts before the e2 broadcast (E2Staged
    /// included) and pauses the operation after it; the step timers are shifted by the paused time; a new incarnation is
    /// rebound before anything is re-answered; a blip resumes; a split whose child is queued for collapse ends.
    /// </summary>
    public partial class MeshSupervisionTests
    {
        private static string S(string text) => "Split split-1: " + text;

        private static string M(string text) => "Merge merge-1: " + text;

        private MeshSplitOperation SplitToE1Gate()
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnSpawnResult(op.opId, true, "Success");
            Bind(ConnX, "C.1", "a1", PidX);
            Ack("C.1", op.e1Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate));
            return op;
        }

        private MeshSplitOperation SplitToE2Staged()
        {
            MeshSplitOperation op = SplitToE1Gate();
            Ack("C", op.e1Pair);
            _c.OnLayoutEvidence(ConnX, MeshSplitOperationTests.Prewarm("C.1", "a1", op.e1Pair, "C"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged));
            return op;
        }

        private MeshSplitOperation SplitToDrainGate()
        {
            MeshSplitOperation op = SplitToE2Staged();
            Ack("C.1", op.e2Pair);
            Ack("C", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate));
            return op;
        }

        private MeshMergeOperation MergeToE2Staged()
        {
            SplitC();
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "pc", op.e1Pair, OthersRegistered("C")));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
            return op;
        }

        // ---- before the e2 broadcast: abort ---------------------------------------------------------------------

        [Test]
        public void AChildLostAfterTheE1Broadcast_AbortsPromptly_WithAFencedRetire_AndIsNotRestarted()
        {
            MeshSplitOperation op = SplitToE1Gate();
            Drop("C.1");
            Tick(4);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate));

            Tick(1);

            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            List<string> lines = SplitLines();
            Assert.That(lines.GetRange(lines.Count - 3, 3), Is.EqualTo(new[] { S("aborted C.1 lost"), S("retired broadcast f4240/3"), S("channel C.1 removed") }));
            Assert.That(_transport.retires.Exists(r => r.connection == ConnX && r.message.reason == "fenced: aborted by split-1"), Is.True);
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            Tick(60);
            Assert.That(_host.restarts, Is.Empty, "an aborted child is not restarted");
            Assert.That(SupLines(), Is.Empty);
        }

        [Test]
        public void AParentLostWhileE2IsStaged_Aborts_TheChildGetsAFencedRetire()
        {
            MeshSplitOperation op = SplitToE2Staged();
            Drop("C");
            Tick(5);

            Assert.That((op.step, op.endReason), Is.EqualTo((MeshSplitStep.Aborted, "C lost")));
            Assert.That(_transport.retires.Exists(r => r.connection == ConnX && r.message.incarnation == "a1" && r.message.reason == "fenced: aborted by split-1"), Is.True, "the child may own R");
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
        }

        [Test]
        public void AChildRestartingWhileE2IsStaged_Aborts_InsteadOfFreezing()
        {
            MeshSplitOperation op = SplitToE2Staged();
            _transport.peers.Remove(ConnX);

            Bind(ConnX + 1, "C.1", "a2");

            Assert.That((op.step, op.endReason), Is.EqualTo((MeshSplitStep.Aborted, "child C.1 restarted: incarnation a1 -> a2")));
            Assert.That(_transport.retires.Exists(r => r.connection == ConnX && r.message.reason == "fenced: aborted by split-1"), Is.True);
            Assert.That(_transport.retires.Exists(r => r.connection == ConnX + 1 && r.message.incarnation == "a2" && r.message.reason == "aborted by split-1"), Is.True);
        }

        [Test]
        public void AMergesSurvivorLostBeforeE2_Aborts_WithTheRevertedBroadcast()
        {
            SplitC();
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            Drop("C");
            Tick(5);

            Assert.That((op.step, op.endReason), Is.EqualTo((MeshMergeStep.Aborted, "C lost")));
            List<string> lines = MergeLines();
            Assert.That(lines.GetRange(lines.Count - 2, 2), Is.EqualTo(new[] { M("aborted C lost"), M("reverted broadcast f4240/6") }));
        }

        [Test]
        public void AMergesRetiringCellLostWhileE2IsStaged_Aborts()
        {
            MeshMergeOperation op = MergeToE2Staged();
            Drop("C.1");
            Tick(4);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged));
            Tick(1);
            Assert.That((op.step, op.endReason), Is.EqualTo((MeshMergeStep.Aborted, "C.1 lost")));
        }

        // ---- after the e2 broadcast: pause, rebind, resume ---------------------------------------------------------

        [Test]
        public void AChildLostAfterE2_PausesTheSplit_WhichResumesRebound_OnItsRestart()
        {
            MeshSplitOperation op = SplitToDrainGate();
            Drop("C.1");
            Tick(5);
            Assert.That(SplitLines()[SplitLines().Count - 1], Is.EqualTo(S("C.1 lost, op paused")));
            Assert.That(op.Paused, Is.True);
            Tick(15);
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C.1 down 20 s, restart 1 of 3 requested") }), "the supervisor watches an op's own child");
            Tick(50);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate), "no step timer runs while paused (60 s of silence would freeze it)");

            Bind(ConnX + 1, "C.1", "a2", PidX + 1);

            Assert.That(SupLines()[SupLines().Count - 1], Is.EqualTo(Sup("C.1 back, incarnation a2")));
            Assert.That(SplitLines()[SplitLines().Count - 1], Is.EqualTo(S("C.1 rebound a2, op resumed")));
            Assert.That(_lines.IndexOf(Sup("C.1 back, incarnation a2")), Is.LessThan(_lines.IndexOf(S("C.1 rebound a2, op resumed"))));
            (long connection, MeshLayoutMessage answer) = _transport.layouts[_transport.layouts.Count - 1];
            Assert.That((connection, answer.kind, answer.targetIncarnation, answer.snapshot.pair), Is.EqualTo((ConnX + 1, MeshLayoutKind.HelloAnswer, "a2", op.e2Pair)), "rebound before the answer");
            Assert.That((op.childIncarnation, op.childConnection, op.Paused), Is.EqualTo(("a2", ConnX + 1, false)));
            Assert.That((op.stepStartedAt, op.drainDeadline), Is.EqualTo((_clock.Now, -1f)), "the step's gate restarts");

            Ack("C.1", op.e2Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "pc", op.e2Pair, 0));
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            Assert.That(SplitLines(), Has.None.Contains("frozen"));
        }

        [Test]
        public void ABlip_ResumesWithoutAGateRestart_AndTheTimersMoveByThePausedTime()
        {
            SplitC();
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "pc", op.e1Pair, OthersRegistered("C")));
            Ack("C", op.e2Pair);
            AckAll(op.e2Pair);
            _clock.Now += 5f;
            _c.OnLayoutEvidence(ConnX, MeshMergeOperationTests.Drained("C.1", "a1", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring));
            float retireSentAt = op.retireSentAt, stepStartedAt = op.stepStartedAt, e2AckedAt = op.e2AckedAt;
            int retires = _transport.retires.Count;

            Drop("C");
            Tick(5);
            Assert.That(MergeLines()[MergeLines().Count - 1], Is.EqualTo(M("C lost, op paused")));
            Tick(9);
            Assert.That(_transport.retires.Count, Is.EqualTo(retires), "no Retire is resent while paused");

            Bind(ConnC + 100, "C", "pc", PidC);

            Assert.That(MergeLines()[MergeLines().Count - 1], Is.EqualTo(M("C back, op resumed")));
            Assert.That((op.retireSentAt, op.stepStartedAt, op.e2AckedAt), Is.EqualTo((retireSentAt + 9f, stepStartedAt + 9f, e2AckedAt)));
            Assert.That((op.Paused, op.survivorConnection), Is.EqualTo((false, ConnC + 100)));
            Assert.That(SupLines(), Is.Empty, "a blip inside the grace is no restart");
        }

        [Test]
        public void ASurvivorRestartingAfterE2_IsRebound_AndItsE2AckCountsAgain()
        {
            MeshMergeOperation op = MergeToE2Staged();
            Ack("C", op.e2Pair);
            AckAll(op.e2Pair);
            Assert.That((op.step, op.e2AckedAt >= 0f), Is.EqualTo((MeshMergeStep.DrainGate, true)));
            Drop("C");
            Tick(5);
            Assert.That(op.Paused, Is.True);

            Bind(ConnC + 100, "C", "pc2", PidC + 1);

            Assert.That(MergeLines()[MergeLines().Count - 1], Is.EqualTo(M("C rebound pc2, op resumed")));
            Assert.That((op.survivorIncarnation, op.survivorConnection, op.e2AckedAt), Is.EqualTo(("pc2", ConnC + 100, -1f)));
            MeshLayoutMessage answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.targetIncarnation, answer.snapshot.pair), Is.EqualTo(("pc2", op.e2Pair)));
            Ack("C", op.e2Pair);
            Assert.That(op.e2AckedAt, Is.EqualTo(_clock.Now));
        }

        [Test]
        public void ASplitWhoseChildCannotComeBack_EndsForItsCollapse_AndTheCollapseRuns()
        {
            MeshSplitOperation split = SplitToDrainGate();
            _host.restartOk = false;
            Drop("C.1");
            Tick(40);

            Assert.That(SupLines().GetRange(5, 2), Is.EqualTo(new[] { Sup("C.1 restart 3 failed (spawn error: spawner gone)"), Sup("C.1 collapse queued after 3 failed restart(s)") }));
            Assert.That(SplitLines()[SplitLines().Count - 1], Is.EqualTo(S("ended, C.1 collapsing")));
            Assert.That((split.step, split.InProgress, split.IsFrozen, split.endReason), Is.EqualTo((MeshSplitStep.Ended, false, false, "C.1 collapsing")));
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.opId, data.opStep), Is.EqualTo(("", "Ended")));

            Tick(1);

            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That(MergeLines()[0], Is.EqualTo(M("requested C C.1 collapse")), "the pending settle (C's interest still wide) does not refuse a collapse");
            Assert.That(op.survivorEntry.interest, Is.EqualTo(Whole));
            DriveCollapse();
            MeshCell c = _world.GetCellById("C");
            Assert.That((c.owned, c.interest, _world.GetCellById("C.1").state), Is.EqualTo((Whole, Whole, MeshCellState.Retired)));
        }

        [Test]
        public void ANonMemberParticipantThatUnregisters_IsDroppedFromTheStep()
        {
            Bind(ConnE, "E", "pe");
            _transport.Register(ConnC2, "C2", Map2);
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.X, 200f, true, "E.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            Bind(ConnX, "E.1", "xe");
            Ack("E.1", op.e1Pair);
            Assert.That(op.participants, Is.EquivalentTo(new[] { "E.1", "E", "C2" }));
            Ack("E", op.e1Pair);
            _c.OnLayoutEvidence(ConnX, MeshSplitOperationTests.Prewarm("E.1", "xe", op.e1Pair, "E"));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E1Gate), "C2 has not acked");

            _transport.peers.Remove(ConnC2);
            _c.Tick();

            Assert.That(op.participants, Is.EquivalentTo(new[] { "E.1", "E" }));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged), "it catches up from its hello answer");
        }
    }
}
