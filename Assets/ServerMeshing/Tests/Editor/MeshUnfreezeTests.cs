using System.Collections.Generic;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// `unfreeze &lt;base&gt;` on a family frozen after a central restart (slices/unfreeze_design.md decisions 1-6, 9): the
    /// missing child is declared dead and fenced, collapsed into the base (or retired when it owns nothing), a stored
    /// operation waiting on the family is dropped, and the refusals.
    /// </summary>
    public class MeshUnfreezeTests : MeshUnfreezeHarness
    {
        /// <summary>FrozenAfterRestart: `split C x 0 + C.1` done, the central restarts, C.1 never says hello, the ceiling freezes family C.</summary>
        private void FrozenAfterRestart(MeshSplitStep step = MeshSplitStep.Done)
        {
            FirstCentral();
            SplitTo(step);
            Restart("C", "E");
            TickAfter(_ceiling + 1f);
            Assert.That(_c.IsFamilyFrozen("C"), Is.True);
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
        }

        [Test]
        public void AFrozenFamily_TheMissingChildIsDeclaredDead_Fenced_AndCollapsedIntoTheBase()
        {
            FrozenAfterRestart();
            string floor = MeshUnfreeze.FenceFloor(_c.Incarnation);

            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Assert.That(LinesStarting("Unfreeze C: "), Is.EqualTo(new[]
            {
                $"Unfreeze C: C.1 declared dead (no hello 61 s after the central started); incarnations started before {floor} fenced",
                $"Unfreeze C: family C of map {Map1} unfrozen by the operator: C.1 declared dead",
                "Unfreeze C: C.1 collapses into C",
            }));
            Assert.That(_log.warnings, Has.Some.StartsWith("Unfreeze C: C.1 declared dead"));
            Assert.That(_c.IsFamilyFrozen("C"), Is.False);
            Assert.That(_c.IsFencedIncarnation("C.1", "a"), Is.True, "a process started before the command is fenced");
            Assert.That(_c.IsFencedIncarnation("C.1", "7fffffffffffffff"), Is.False, "a later one is not");
            Assert.That(Stored().fencedIncarnations, Has.Some.Matches<MeshFencedIncarnation>(f => f.cellId == "C.1" && f.incarnation == floor));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.CollapseQueued));

            TickAfter(1f);
            Assert.That(LinesStarting("Merge ")[0], Is.EqualTo("Merge merge-1: requested C C.1 collapse"));
            MeshMergeOperation op = DriveCollapseToDone();

            Assert.That(LinesStarting("Merge "), Has.Member("Merge merge-1: C.1 exited before drained"));
            MeshCell c = _world.GetCellById("C");
            Assert.That((c.owned, c.interest), Is.EqualTo((Whole, Whole)), "C owns its whole base again: one owner of R");
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            MeshCoordinatorStoreData data = Stored();
            Assert.That(data.broadcast.pair, Is.EqualTo(op.tidyPair));
            Assert.That(data.broadcast.entries.FindAll(e => e.baseId == "C"), Is.Empty, "the family is back to its asset cut");
            Assert.That(data.hasOperation, Is.False);
            Assert.That(_c.IsSplitFamily("C"), Is.False);
        }

        [Test]
        public void AfterTheUnfreeze_AZombieOfTheDeadChildIsFenced_AndANewerProcessIsFencedWhileItCollapses()
        {
            FrozenAfterRestart();
            Assert.That(Unfreeze("C", out string error), Is.True, error);

            Hello(ConnX, "C.1", "a", SplitC());
            Assert.That(LastRetire().reason, Is.EqualTo("fenced: stale incarnation"));
            _transport.peers.Remove(ConnX);
            Hello(ConnX + 1, "C.1", "7ffffffffffffff");
            Assert.That(LastRetire().reason, Is.EqualTo("fenced: collapsing"));
            Assert.That(_c.IsFamilyFrozen("C"), Is.False, "a fenced hello unfreezes nothing again");
        }

        [Test]
        public void TheUnfrozenFamily_IsSupervisedAndOpenToOperationsAgain()
        {
            FrozenAfterRestart();
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.False);
            Assert.That(error, Does.Contain("family C is frozen"));
            Assert.That(Unfreeze("C", out error), Is.True, error);
            TickAfter(1f);
            Assert.That(_c.SupervisedOf("C"), Is.Not.Null, "the base is watched again");
            DriveCollapseToDone();
            TickAfter(31f);
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", out error), Is.True, error);
        }

        [Test]
        public void AStoredSplitWaitingOnTheFrozenFamily_HoldsTheOpSlot_AndIsDroppedWhenItsChildIsDeclaredDead()
        {
            FrozenAfterRestart(MeshSplitStep.DrainGate);
            Assert.That(LinesStarting("Split split-1: waits after the restart: family C is frozen"), Has.Count.EqualTo(1));
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.Z, 0f, true, "E.1", out string error), Is.False, "the waiting split holds the world-wide slot");
            Assert.That(error, Does.StartWith("split-1 waits for family C, which is frozen: no hello from C.1"));
            Assert.That(Unfreeze("E", out error), Is.False);
            Assert.That(error, Is.EqualTo("family E is not frozen; split-1 waits for family C, which is frozen: unfreeze C"));

            Assert.That(Unfreeze("C", out error), Is.True, error);

            Assert.That(LinesStarting("Split "), Has.Member("Split split-1: dropped at the unfreeze: C.1 declared dead"));
            Assert.That(Stored().hasOperation, Is.False);
            Assert.That(_c.CurrentOp, Is.Null);
            TickAfter(1f);
            Assert.That(LinesStarting("Merge ")[0], Is.EqualTo("Merge merge-1: requested C C.1 collapse"), "the collapse takes the freed slot (C's interest still wide does not refuse it)");
            DriveCollapseToDone();
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(Whole));
            TickAfter(31f);
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.Z, 0f, true, "E.1", out error), Is.True, error);
            Assert.That(_c.CurrentOp.opId, Is.EqualTo("split-2"), "the op counter continues from the dropped split");
        }

        [Test]
        public void ADeadChildOwningNothing_IsRetiredByOneVersion_OnceTheOpSlotIsFree()
        {
            // The store holds C owning its base with C.1 Pending (a split's e1 whose operation is gone).
            MeshLayoutSnapshot stored = Snapshot(5, Entry("C", "C", MeshCellState.Active, Whole, Whole), Entry("C.1", "C", MeshCellState.Pending, MeshRect.Empty, East));
            stored.pair = new MeshLayoutPair(500, 5);
            Assert.That(_store.TrySave(new MeshCoordinatorStoreData { lastIncarnation = 500, broadcast = stored }, out string error), Is.True, error);
            _c.Start();
            Hello(ConnC, "C", "c1", stored.Clone());
            Hello(ConnE, "E", "e1", stored.Clone());
            TickAfter(_ceiling + 1f);
            Assert.That(_c.IsFamilyFrozen("C"), Is.True);
            // Another family's split holds the slot: the retire version waits for it.
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.Z, 0f, true, "E.1", out error), Is.True, error);

            Assert.That(Unfreeze("C", out error), Is.True, error);
            Assert.That(LinesStarting("Unfreeze C: retire version"), Is.Empty, "the version waits while split-1 runs");
            Assert.That(_c.SupervisedOf("C.1"), Is.Null, "nothing to collapse");
            TickAfter(_ceiling + 1f);   // split-1 is aborted: no hello from E.1
            Assert.That(_c.CurrentSplit.IsAborted, Is.True);
            TickAfter(1f);

            List<string> retired = LinesStarting("Unfreeze C: retire version");
            Assert.That(retired, Has.Count.EqualTo(1));
            Assert.That(retired[0], Does.EndWith("broadcast (C.1 Retired)"));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_host.removed, Does.Contain("C.1"));
            Assert.That(Stored().broadcast.entries.Find(e => e.id == "C.1").state, Is.EqualTo(MeshCellState.Retired));
            TickAfter(1f);
            Assert.That(LinesStarting("Unfreeze C: retire version"), Has.Count.EqualTo(1), "sent once");
        }

        [Test]
        public void ACentralRestartDuringTheCollapse_FreezesTheFamilyAgain_AndASecondUnfreezeFinishesIt()
        {
            FrozenAfterRestart();
            Assert.That(Unfreeze("C", out string error), Is.True, error);
            TickAfter(1f);
            Assert.That(_c.CurrentMerge.step, Is.EqualTo(MeshMergeStep.E1Gate), "the collapse's e1 went out");

            Restart("C", "E");
            TickAfter(_ceiling + 1f);

            Assert.That(_c.IsFamilyFrozen("C"), Is.True, "the dead child is waited for again, not taken as the collapse's expected absence");
            Assert.That(_lines, Has.Member("Merge merge-1: aborted the central restarted before e2"));
            Assert.That(_world.GetCellById("C.1").owned, Is.EqualTo(East), "the reverted collapse gave C.1 its rectangle back");
            Assert.That(Unfreeze("C", out error), Is.True, error);
            TickAfter(1f);
            Assert.That(LinesStarting("Merge merge-2: requested C C.1 collapse"), Has.Count.EqualTo(1));
            DriveCollapseToDone();
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(Whole));
        }

        // ---- refusals --------------------------------------------------------------------------------------------------

        [Test]
        public void Refused_AFamilyThatIsNotFrozen_ACellThatIsNoBase_AStaticCell_AnUnknownOne()
        {
            FrozenAfterRestart();
            Assert.That(Unfreeze("E", out string error), Is.False);
            Assert.That(error, Is.EqualTo("family E is not frozen"));
            Assert.That(_lines, Has.Member("Layout op refused: family E is not frozen"));
            Assert.That(Unfreeze("C.1", out error), Is.False);
            Assert.That(error, Is.EqualTo("C.1 is a child of C: unfreeze takes the family's base"));
            Assert.That(Unfreeze("C2", out error), Is.False);
            Assert.That(error, Is.EqualTo("C2 is on map Map2, which is Static"));
            Assert.That(Unfreeze("Q", out error), Is.False);
            Assert.That(error, Is.EqualTo("cell Q is not in the layout"));
            Assert.That(_c.IsFamilyFrozen("C"), Is.True, "nothing changed");
        }

        [Test]
        public void Refused_WhileTheCentralStillRecovers()
        {
            FirstCentral();
            SplitTo(MeshSplitStep.Done);
            Restart("C", "E");
            Assert.That(Unfreeze("C", out string error), Is.False);
            Assert.That(error, Is.EqualTo("the layout coordinator is still recovering"));
        }

        [Test]
        public void Refused_WhileAMissingChildHasARegisteredMapServer()
        {
            FrozenAfterRestart();
            _transport.Register(ConnX, "C.1", Map1);   // registered, but no hello
            Assert.That(Unfreeze("C", out string error), Is.False);
            Assert.That(error, Is.EqualTo("a map server of C.1 is registered but has not said hello: stop it or let it say hello"));
            Assert.That(_c.IsFamilyFrozen("C"), Is.True);
            Assert.That(_c.IsFencedIncarnation("C.1", "a"), Is.False, "nothing fenced");
        }

        [Test]
        public void Refused_BeforeACutOffCellCouldHaveFenced()
        {
            _ceiling = 5f;
            FrozenAfterRestart();
            Assert.That(Unfreeze("C", out string error), Is.False);
            Assert.That(error, Is.EqualTo("the central started 6 s ago, under the 16 s a cut-off cell may take to fence"));
            TickAfter(10f);
            Assert.That(Unfreeze("C", out error), Is.True, error);
        }
    }
}
