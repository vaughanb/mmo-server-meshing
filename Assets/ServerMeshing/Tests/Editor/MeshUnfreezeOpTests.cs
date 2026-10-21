using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// `unfreeze &lt;base&gt;` on an operation frozen after its commit (slices/unfreeze_design.md decisions 7-8): resolved by a
    /// restarted central's rules at the step it froze at — re-sent, resumed or aborted — with the slot holder named when
    /// the family is not frozen.
    /// </summary>
    public class MeshUnfreezeOpTests : MeshUnfreezeHarness
    {
        private const float StepTimeout = 61f;

        private MeshSplitOperation FrozenSplit(MeshSplitStep at)
        {
            FirstCentral();
            MeshSplitOperation op = SplitTo(at);
            TickAfter(StepTimeout);
            Assert.That(op.IsFrozen, Is.True, op.lastGateWait);
            Assert.That(op.frozenStep, Is.EqualTo(at.ToString()));
            return op;
        }

        /// <summary>BlockStore makes the store unwritable (its path a directory) until UnblockStore.</summary>
        private void BlockStore()
        {
            System.IO.File.Delete(_store.Path);
            System.IO.Directory.CreateDirectory(_store.Path);
        }

        private void UnblockStore() => System.IO.Directory.Delete(_store.Path, true);

        private int RetiresTo(string cellId) => _transport.retires.FindAll(r => r.message.cellId == cellId).Count;

        [Test]
        public void ASplitFrozenAtTheDrainGate_IsResumedThere_TheLayoutReSent_AndReachesDone()
        {
            MeshSplitOperation frozen = FrozenSplit(MeshSplitStep.DrainGate);
            Assert.That((Stored().opStep, Stored().operation.step), Is.EqualTo(("Frozen", "Frozen")), "the store still says Frozen");
            int sent = _transport.layouts.Count;

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(LinesStarting("Unfreeze C: "), Is.EqualTo(new[]
            {
                $"Unfreeze C: split-1 frozen at DrainGate ({frozen.endReason})",
                $"Unfreeze C: layout {frozen.e2Pair} re-sent",
            }));
            Assert.That(_lines, Has.Member("Split split-1: resumed at DrainGate after the unfreeze"));
            Assert.That(_transport.layouts.Count - sent, Is.EqualTo(_transport.peers.Count), "the broadcast went to every map server again");
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That((op.step, op.InProgress), Is.EqualTo((MeshSplitStep.DrainGate, true)));
            Assert.That(Stored().operation.step, Is.EqualTo("DrainGate"));
            AckAll(op.e2Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast), op.lastGateWait);
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));
            Assert.That(_world.GetCellById("C").interest, Is.EqualTo(West));
        }

        [Test]
        public void ASplitFrozenWithE2StagedThatTheChildNeverApplied_IsAborted_WithAFencedRetireAndTheRetiredBroadcast()
        {
            FrozenSplit(MeshSplitStep.E2Staged);

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(_lines, Has.Member("Split split-1: aborted by the unfreeze: C.1 did not acknowledge the staged e2"));
            Assert.That(LastRetire().reason, Is.EqualTo("fenced: aborted by split-1"));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(Whole), "nobody but the child saw e2: C keeps R");
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            Assert.That(_c.IsAborted("C.1"), Is.True);
            Assert.That(Stored().hasOperation, Is.False);
        }

        [Test]
        public void ASplitFrozenWithE2StagedThatTheChildAckedLate_BroadcastsE2AndGoesOnAtTheDrainGate()
        {
            MeshSplitOperation frozen = FrozenSplit(MeshSplitStep.E2Staged);
            Ack("C.1", frozen.e2Pair);   // after the freeze: counted by no gate, but the binding knows it

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(_lines, Has.Member("Split split-1: resumed at E2Staged after the unfreeze"));
            Assert.That(_lines, Has.Member($"Split split-1: e2 broadcast {frozen.e2Pair}"));
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate));
            Assert.That(_world.GetCellById("C.1").owned, Is.EqualTo(East));
            Assert.That(LinesStarting("Unfreeze C: layout"), Is.Empty, "a re-sent step needs no re-sent layout");
        }

        [Test]
        public void ASplitFrozenAtE3_IsResumedAndDone_AndOneThatStaysSilentFreezesAgain()
        {
            MeshSplitOperation frozen = FrozenSplit(MeshSplitStep.E3Broadcast);
            Assert.That(Unfreeze("C", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast));

            TickAfter(StepTimeout);
            Assert.That((op.IsFrozen, op.frozenStep), Is.EqualTo((true, "E3Broadcast")), "the cause persists: frozen again");
            Assert.That(Unfreeze("C", out error), Is.True, error);
            op = _c.CurrentSplit;
            AckAll(frozen.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done));
        }

        [Test]
        public void ASplitWhoseE2BroadcastFailed_IsResolvedAtE2Staged_AndTheUnfreezeBroadcastsE2()
        {
            FirstCentral();
            MeshSplitOperation op = SplitTo(MeshSplitStep.E2Staged);
            BlockStore();
            Ack("C.1", op.e2Pair);   // e2 is broadcast now: the save before it fails
            Assert.That(op.IsFrozen, Is.True);
            Assert.That(op.frozenStep, Is.EqualTo("E2Staged"), "frozen entering DrainGate, but e2 never went out");
            Assert.That(Unfreeze("C", out string error), Is.False);
            Assert.That(error, Is.EqualTo("the coordinator store is not writable"));
            UnblockStore();

            Assert.That(Unfreeze("C", out error), Is.True, error);

            Assert.That(_lines, Has.Member($"Split split-1: e2 broadcast {op.e2Pair}"));
            Assert.That(_c.CurrentSplit.step, Is.EqualTo(MeshSplitStep.DrainGate));
            Assert.That(_world.GetCellById("C.1").owned, Is.EqualTo(East));
        }

        [Test]
        public void ASplitWhoseE3BroadcastFailed_IsResumedAtTheDrainGate_AndBroadcastsANewE3()
        {
            FirstCentral();
            MeshSplitOperation op = SplitTo(MeshSplitStep.DrainGate);
            AckAll(op.e2Pair);
            BlockStore();
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That((op.IsFrozen, op.frozenStep), Is.EqualTo((true, "DrainGate")));
            UnblockStore();

            Assert.That(Unfreeze("C", out string error), Is.True, error);
            MeshSplitOperation resumed = _c.CurrentSplit;
            Assert.That(resumed.step, Is.EqualTo(MeshSplitStep.DrainGate));
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That(resumed.step, Is.EqualTo(MeshSplitStep.E3Broadcast), resumed.lastGateWait);
            Assert.That(resumed.e3Pair.IsNewerThan(op.e3Pair), Is.True, "a new e3, not the one that never went out");
            AckAll(resumed.e3Pair);
            Assert.That(resumed.step, Is.EqualTo(MeshSplitStep.Done));
        }

        private MeshMergeOperation MergeTo(MeshMergeStep at)
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            TickAfter(31f);
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "c1", op.e1Pair, OthersRegistered("C")));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
            if (at == MeshMergeStep.E2Staged)
                return op;
            Ack("C", op.e2Pair);
            AckAll(op.e2Pair);
            TickAfter(6f);
            _c.OnLayoutEvidence(ConnX, MeshMergeOperationTests.Drained("C.1", "x1", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), op.lastGateWait);
            _transport.peers.Remove(ConnX);
            TickAfter(1f);
            TickAfter(5f);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), op.lastGateWait);
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.TidyBroadcast), op.lastGateWait);
            return op;
        }

        [Test]
        public void AMergeFrozenWithE2StagedNotAcked_IsReverted()
        {
            MeshMergeOperation frozen = MergeTo(MeshMergeStep.E2Staged);
            TickAfter(StepTimeout);
            Assert.That((frozen.IsFrozen, frozen.frozenStep), Is.EqualTo((true, "E2Staged")));

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(_lines, Has.Member("Merge merge-1: aborted by the unfreeze: C did not acknowledge the staged e2"));
            Assert.That(LinesStarting("Merge merge-1: reverted broadcast"), Has.Count.EqualTo(1));
            Assert.That((_world.GetCellById("C").owned, _world.GetCellById("C").interest), Is.EqualTo((West, West)));
            Assert.That(_world.GetCellById("C.1").owned, Is.EqualTo(East));
            Assert.That(RetiresTo("C.1"), Is.EqualTo(0), "a reverted merge retires nobody");
        }

        [Test]
        public void AMergeFrozenAtItsTidy_IsResumedAndDone()
        {
            MeshMergeOperation frozen = MergeTo(MeshMergeStep.TidyBroadcast);
            TickAfter(StepTimeout);
            Assert.That((frozen.IsFrozen, frozen.frozenStep), Is.EqualTo((true, "TidyBroadcast")));

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(_lines, Has.Member("Merge merge-1: resumed at TidyBroadcast after the unfreeze"));
            MeshMergeOperation op = _c.CurrentMerge;
            AckAll(frozen.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done));
            Assert.That(Stored().hasOperation, Is.False);
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(Whole));
        }

        [Test]
        public void Refused_AnotherFamily_NamesTheFrozenOpThatHoldsTheSlot()
        {
            FrozenSplit(MeshSplitStep.DrainGate);
            Assert.That(Unfreeze("E", out string error), Is.False);
            Assert.That(error, Is.EqualTo("family E is not frozen; split-1 of family C is frozen: unfreeze C"));
        }

        [Test]
        public void Refused_AnOperationInProgress_IsNotFrozen()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.E1Gate);
            Assert.That(Unfreeze("C", out string error), Is.False);
            Assert.That(error, Is.EqualTo("family C is not frozen; split-1 is in progress"));
            Assert.That(Unfreeze("E", out error), Is.False);
            Assert.That(error, Is.EqualTo("family E is not frozen; split-1 of family C is in progress"));
        }
    }
}
