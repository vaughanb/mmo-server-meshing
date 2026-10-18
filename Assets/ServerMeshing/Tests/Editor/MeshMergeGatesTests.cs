using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The merge's pure parts: the union, the command line, the gates, the layouts each step makes, the tidy condition.</summary>
    public class MeshMergeGatesTests
    {
        private static readonly MeshLayoutPair E1 = new MeshLayoutPair(5, 2);
        private static readonly MeshLayoutPair E2 = new MeshLayoutPair(5, 3);
        private static readonly MeshRect Whole = new MeshRect(-100f, -100f, 100f, 100f);
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);

        private static MeshMergeOperation Op()
        {
            MeshLayoutSnapshot split = SplitC();
            return new MeshMergeOperation("merge-1")
            {
                mapId = Map1, baseId = "C", survivorId = "C", retiringId = "C.1", survivorOwned = West, retiringOwned = East, union = Whole,
                survivorEntry = split.entries.Find(e => e.id == "C").Clone(), retiringEntry = split.entries.Find(e => e.id == "C.1").Clone(),
            };
        }

        [Test]
        public void TryUnion_JoinsRectanglesSharingAFullEdge()
        {
            Assert.That(West.TryUnion(East, 0.5f, out MeshRect union), Is.True);
            Assert.That(union, Is.EqualTo(Whole));
            Assert.That(East.TryUnion(West, 0.5f, out union), Is.True, "either order");
            Assert.That(union, Is.EqualTo(Whole));
            MeshRect south = new MeshRect(-100f, -100f, 100f, 0f), north = new MeshRect(-100f, 0f, 100f, 100f);
            Assert.That(north.TryUnion(south, 0.5f, out union), Is.True, "along z too");
            Assert.That(union, Is.EqualTo(Whole));
            Assert.That(West.TryUnion(new MeshRect(0.3f, -100.2f, 100f, 99.9f), 0.5f, out union), Is.True, "within the tolerance");
            Assert.That(union, Is.EqualTo(new MeshRect(-100f, -100.2f, 100f, 100f)));

            Assert.That(West.TryUnion(new MeshRect(0f, -100f, 100f, 0f), 0.5f, out union), Is.False, "a half edge is an L, not a rectangle");
            Assert.That(union.IsEmpty, Is.True);
            Assert.That(West.TryUnion(new MeshRect(1f, -100f, 100f, 100f), 0.5f, out _), Is.False, "apart");
            Assert.That(West.TryUnion(new MeshRect(-50f, -100f, 100f, 100f), 0.5f, out _), Is.False, "overlapping");
            Assert.That(West.TryUnion(West, 0.5f, out _), Is.False, "the same rectangle");
            Assert.That(West.TryUnion(MeshRect.Empty, 0.5f, out _), Is.False);
            Assert.That(MeshRect.Empty.TryUnion(East, 0.5f, out _), Is.False);
        }

        [Test]
        public void Command_ParsesAMerge_InEitherOrder()
        {
            Assert.That(MeshLayoutCommand.TryParse("7 merge C C.1", out MeshLayoutCommand command, out string error), Is.True, error);
            Assert.That((command.seq, command.kind, command.verb, command.mergeA, command.mergeB), Is.EqualTo((7L, MeshLayoutCommandKind.Merge, "merge", "C", "C.1")));
            Assert.That(MeshLayoutCommand.TryParse(" 8  MERGE C.1 C ", out command, out error), Is.True, error);
            Assert.That((command.kind, command.mergeA, command.mergeB), Is.EqualTo((MeshLayoutCommandKind.Merge, "C.1", "C")));
            Assert.That(MeshLayoutCommand.TryParse("9 split C x 0 + C.1", out command, out error), Is.True, error);
            Assert.That(command.kind, Is.EqualTo(MeshLayoutCommandKind.Split));

            Assert.That(MeshLayoutCommand.TryParse("1 merge C", out _, out error), Is.False);
            Assert.That(error, Does.Contain("a merge takes 4"));
            Assert.That(MeshLayoutCommand.TryParse("1 merge C C.1 E", out _, out error), Is.False);
            Assert.That(MeshLayoutCommand.TryParse("1 merge C c", out _, out error), Is.False);
            Assert.That(error, Does.Contain("two different cells"));
            Assert.That(MeshLayoutCommand.TryParse("m merge C C.1", out _, out error), Is.False);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        [Test]
        public void SurvivorReady_LeavesOutTheLoadClause_NotTheHellosOrTheQueue()
        {
            MeshLayoutEvidenceMessage saturated = MeshSplitOperationTests.Prewarm("C", "pc", E1, "C.1", "E");
            saturated.saturatedShare = 0.4f;
            Assert.That(MeshSplitGates.PrewarmReady(saturated, E1, new[] { "C.1", "E" }, 2f, out _), Is.False, "a new child's gate would wait");
            Assert.That(MeshMergeGates.SurvivorReady(saturated, E1, new[] { "C.1", "E" }, 2f, out string why), Is.True, why);
            MeshLayoutEvidenceMessage uncapped = saturated;
            uncapped.budgetMs = 0f;
            uncapped.p95Ms = 80f;
            Assert.That(MeshMergeGates.SurvivorReady(uncapped, E1, new[] { "C.1" }, 2f, out why), Is.True, why);

            Assert.That(MeshMergeGates.SurvivorReady(saturated, E1, new[] { "C.1", "N" }, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("no hello from N"));
            MeshLayoutEvidenceMessage queued = saturated;
            queued.prewarmQueue = 4;
            Assert.That(MeshMergeGates.SurvivorReady(queued, E1, new[] { "C.1" }, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("prewarm queue 4"));
            MeshLayoutEvidenceMessage early = saturated;
            early.pair = new MeshLayoutPair(5, 1);
            Assert.That(MeshMergeGates.SurvivorReady(early, E1, new[] { "C.1" }, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("evidence at 5/1, before e1 5/2"));
        }

        [Test]
        public void Drained_NeedsE2_AFreshWindow_ALoadWindowAfterTheLastAck_AndTheFlag()
        {
            MeshLayoutEvidenceMessage drained = MeshMergeOperationTests.Drained("C.1", "xc", E2);
            Assert.That(MeshMergeGates.Drained(drained, E2, 15f, 10f, out string why), Is.True, why);
            Assert.That(MeshMergeGates.Drained(drained, E2, 14.9f, 10f, out why), Is.False);
            Assert.That(why, Is.EqualTo("the window arrived 4.9 s after the last e2 ack, under 5 s"));
            Assert.That(MeshMergeGates.Drained(drained, E2, 15f, -1f, out why), Is.False);
            Assert.That(why, Is.EqualTo("e2 is not acknowledged by every participant"));
            Assert.That(MeshMergeGates.Drained(drained, new MeshLayoutPair(5, 4), 15f, 10f, out why), Is.False);
            Assert.That(why, Is.EqualTo("evidence at 5/3, before e2 5/4"));
            MeshLayoutEvidenceMessage onApply = drained;
            onApply.windowStartedAfterApplySeconds = -0.1f;
            Assert.That(MeshMergeGates.Drained(onApply, E2, 15f, 10f, out why), Is.False);
            Assert.That(why, Is.EqualTo("the window started before the apply"));
            MeshLayoutEvidenceMessage holding = MeshMergeOperationTests.Drained("C.1", "xc", E2, false, new MeshDrainState { monsters = 2, staticLoans = 1, buildings = 3 });
            Assert.That(MeshMergeGates.Drained(holding, E2, 15f, 10f, out why), Is.False);
            Assert.That(why, Is.EqualTo("still holds monsters=2 buildings=3 staticLoans=1"));
            MeshLayoutEvidenceMessage later = drained;
            later.pair = new MeshLayoutPair(5, 9);
            Assert.That(MeshMergeGates.Drained(later, E2, 15f, 10f, out _), Is.True, "a window at a later pair counts");
        }

        [Test]
        public void Exited_IsFiveSecondsGone()
        {
            Assert.That(MeshMergeGates.Exited(-1f, 100f), Is.False);
            Assert.That(MeshMergeGates.Exited(96f, 100f), Is.False);
            Assert.That(MeshMergeGates.Exited(95f, 100f), Is.True);
        }

        [Test]
        public void Steps_BuildTheContractsLayouts()
        {
            MeshMergeOperation op = Op();
            MeshLayoutSnapshot broadcast = SplitC(4);

            MeshLayoutSnapshot e1 = op.E1(broadcast, E1);
            Assert.That(e1.pair, Is.EqualTo(E1));
            Assert.That(Shape(e1, "C"), Is.EqualTo((MeshCellState.Active, West, Whole)));
            Assert.That(Shape(e1, "C.1"), Is.EqualTo((MeshCellState.Active, East, East)));
            MeshLayoutSnapshot e2 = op.E2(e1, E2);
            Assert.That(Shape(e2, "C"), Is.EqualTo((MeshCellState.Active, Whole, Whole)));
            Assert.That(Shape(e2, "C.1"), Is.EqualTo((MeshCellState.Draining, MeshRect.Empty, East)));
            MeshLayoutSnapshot e3 = op.E3(e2, new MeshLayoutPair(5, 4));
            Assert.That(Shape(e3, "C"), Is.EqualTo((MeshCellState.Active, Whole, Whole)));
            Assert.That(Shape(e3, "C.1"), Is.EqualTo((MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty)));
            MeshLayoutSnapshot tidy = op.Tidy(e3, new MeshLayoutPair(5, 5));
            Assert.That(tidy.entries, Is.Empty);
            MeshLayoutSnapshot reverted = op.Reverted(e1, new MeshLayoutPair(5, 3));
            Assert.That(Shape(reverted, "C"), Is.EqualTo((MeshCellState.Active, West, West)));
            Assert.That(Shape(reverted, "C.1"), Is.EqualTo((MeshCellState.Active, East, East)));
            Assert.That(broadcast.entries.Find(e => e.id == "C").interest, Is.EqualTo(West), "the builders copy");
        }

        [Test]
        public void Steps_AreValidLayouts()
        {
            BandEnvironment band = new BandEnvironment();
            MeshWorldRegistry world = RigLike(map1Dynamic: true);
            try
            {
                MeshLayoutWorld validation = world.ValidationWorld();
                MeshMergeOperation op = Op();
                MeshLayoutSnapshot broadcast = SplitC(4);
                MeshLayoutSnapshot e1 = op.E1(broadcast, new MeshLayoutPair(1, 5));
                MeshLayoutSnapshot e2 = op.E2(e1, new MeshLayoutPair(1, 6));
                MeshLayoutSnapshot e3 = op.E3(e2, new MeshLayoutPair(1, 7));
                MeshLayoutSnapshot tidy = op.Tidy(e3, new MeshLayoutPair(1, 8));
                Assert.That(MeshLayoutValidator.Validate(validation, e1, broadcast, out string why), Is.True, why);
                Assert.That(MeshLayoutValidator.Validate(validation, e2, e1, out why), Is.True, why);
                Assert.That(MeshLayoutValidator.Validate(validation, e3, e2, out why), Is.True, why);
                Assert.That(MeshLayoutValidator.Validate(validation, tidy, e3, out why), Is.True, why);
                Assert.That(world.ApplyLayout(broadcast, out why), Is.True, why);
                Assert.That(world.ApplyLayout(e3, out why), Is.True, why);
                Assert.That(world.ApplyLayout(tidy, out why), Is.True, why);
                Assert.That((world.GetCellById("C").owned, world.GetCellById("C").interest, world.GetCellById("C.1").state), Is.EqualTo((Whole, Whole, MeshCellState.Retired)));
                Assert.That(world.FamilyOf("C").Count, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(world);
                band.Dispose();
            }
        }

        [Test]
        public void TidyApplies_OnlyToAWholeSurvivorLeftAlone()
        {
            MeshMergeOperation op = Op();
            MeshLayoutSnapshot e3 = op.E3(op.E2(SplitC(4), E1), E2);
            Assert.That(op.TidyApplies(e3, Whole, out string why), Is.True, why);

            MeshLayoutSnapshot third = e3.Clone();
            third.entries.Add(Active("C.2", "C", new MeshRect(-100f, 0f, 0f, 100f)));
            Assert.That(op.TidyApplies(third, Whole, out why), Is.False);
            Assert.That(why, Is.EqualTo("C.2 is still Active"));
            MeshLayoutSnapshot part = Snapshot(9, Active("C", "C", West), Entry("C.1", "C", MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty));
            Assert.That(op.TidyApplies(part, Whole, out why), Is.False);
            Assert.That(why, Does.StartWith("C is Active owning x [-100, 0)"));
            MeshLayoutSnapshot wide = Snapshot(9, Entry("C", "C", MeshCellState.Active, West, Whole));
            Assert.That(op.TidyApplies(wide, Whole, out _), Is.False, "interest beyond owned");
            MeshLayoutSnapshot other = Snapshot(9, Active("E", "E", new MeshRect(100f, -100f, 300f, 100f)), Active("C", "C", Whole));
            Assert.That(op.TidyApplies(other, Whole, out why), Is.True, "another family's entries do not count: " + why);
            Assert.That(op.Tidy(other, new MeshLayoutPair(1, 10)).entries.ConvertAll(e => e.id), Is.EqualTo(new[] { "E" }));
        }

        [Test]
        public void Operation_NamesItsStepsAndPairs()
        {
            MeshMergeOperation op = Op();
            op.e1Pair = E1;
            op.e2Pair = E2;
            op.e3Pair = new MeshLayoutPair(5, 4);
            op.tidyPair = new MeshLayoutPair(5, 5);
            Assert.That((op.LinePrefix, op.kind, op.StagedTargetId, op.PastCommit, op.CurrentStepName, op.CurrentStepPair), Is.EqualTo(("Merge", MeshLayoutOpKind.Merge, "C", false, "e1", E1)));
            op.step = MeshMergeStep.E2Staged;
            Assert.That((op.PastCommit, op.CommitStaged, op.CurrentStepName, op.CurrentStepPair), Is.EqualTo((true, true, "e2", E2)));
            op.step = MeshMergeStep.Retiring;
            Assert.That((op.CommitStaged, op.CurrentStepName, op.CurrentStepPair, op.InProgress), Is.EqualTo((false, "e2", E2, true)));
            op.step = MeshMergeStep.TidyBroadcast;
            Assert.That((op.CurrentStepName, op.CurrentStepPair), Is.EqualTo(("tidy", op.tidyPair)));
            op.step = MeshMergeStep.Frozen;
            Assert.That((op.InProgress, op.IsFrozen, op.HoldsFamily, op.StepText), Is.EqualTo((false, true, true, "Frozen")));
            op.step = MeshMergeStep.Done;
            Assert.That((op.InProgress, op.HoldsFamily), Is.EqualTo((false, false)));
            Assert.That(op.IsMember("c.1") && op.IsMember("C") && !op.IsMember("E"), Is.True);
            MeshSplitOperation split = new MeshSplitOperation("split-1") { childId = "C.1", parentId = "C" };
            Assert.That((split.LinePrefix, split.kind, split.StagedTargetId, split.CurrentStepName), Is.EqualTo(("Split", MeshLayoutOpKind.Split, "C.1", "ChannelAdded")));
        }

        private static (MeshCellState, MeshRect, MeshRect) Shape(MeshLayoutSnapshot snapshot, string id)
        {
            MeshLayoutEntry entry = snapshot.entries.Find(e => e.id == id);
            return (entry.state, entry.owned, entry.interest);
        }
    }
}
