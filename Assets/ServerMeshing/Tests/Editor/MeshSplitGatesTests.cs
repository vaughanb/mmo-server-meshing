using System.Collections.Generic;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The split's pure parts: the gate predicates, the cut, the command line, the new validator rule, the recovery fix.</summary>
    public class MeshSplitGatesTests
    {
        private static readonly MeshLayoutPair E1 = new MeshLayoutPair(5, 2);
        private static readonly MeshLayoutPair E2 = new MeshLayoutPair(5, 3);
        private static readonly string[] Band = { "C", "E" };

        private static MeshLayoutEvidenceMessage Ready()
        {
            MeshLayoutEvidenceMessage evidence = MeshSplitOperationTests.Prewarm("C.1", "x", E1, "C", "E");
            evidence.budgetMs = 16.7f;
            return evidence;
        }

        [Test]
        public void Prewarm_OpensOnlyWithEveryNeighbourOldEnoughAndHeard()
        {
            Assert.That(MeshSplitGates.PrewarmReady(Ready(), E1, Band, 2f, out string why), Is.True, why);

            MeshLayoutEvidenceMessage early = Ready();
            early.pair = new MeshLayoutPair(5, 1);
            Assert.That(MeshSplitGates.PrewarmReady(early, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("evidence at 5/1, before e1 5/2"));

            MeshLayoutEvidenceMessage onApply = Ready();
            onApply.windowStartedAfterApplySeconds = -2f;
            Assert.That(MeshSplitGates.PrewarmReady(onApply, E1, Band, 2f, out why), Is.False, "the on-apply message never opens the gate by itself");
            Assert.That(why, Is.EqualTo("the window started before the apply"));

            MeshLayoutEvidenceMessage later = Ready();
            later.pair = E2;
            Assert.That(MeshSplitGates.PrewarmReady(later, E1, Band, 2f, out _), Is.True, "a window at or after e1 counts");

            MeshLayoutEvidenceMessage missing = MeshSplitOperationTests.Prewarm("C.1", "x", E1, "C");
            Assert.That(MeshSplitGates.PrewarmReady(missing, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("no hello from E"));

            MeshLayoutEvidenceMessage young = Ready();
            young.neighbours[1] = new MeshNeighbourEvidence { zoneId = "E", helloAgeSeconds = 1.5f, datagramsSinceHello = 9 };
            Assert.That(MeshSplitGates.PrewarmReady(young, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("hello from E 1.5 s old"));

            MeshLayoutEvidenceMessage silent = Ready();
            silent.neighbours[0] = new MeshNeighbourEvidence { zoneId = "c", helloAgeSeconds = 9f, datagramsSinceHello = 0 };
            Assert.That(MeshSplitGates.PrewarmReady(silent, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("no datagram from C since its hello"));

            MeshLayoutEvidenceMessage never = Ready();
            never.neighbours[0] = new MeshNeighbourEvidence { zoneId = "C", helloAgeSeconds = -1f, datagramsSinceHello = 3 };
            Assert.That(MeshSplitGates.PrewarmReady(never, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("no hello from C"));
        }

        [Test]
        public void Prewarm_NeedsAnEmptyQueueAndNoSaturation()
        {
            MeshLayoutEvidenceMessage queued = Ready();
            queued.prewarmQueue = 3;
            Assert.That(MeshSplitGates.PrewarmReady(queued, E1, Band, 2f, out string why), Is.False);
            Assert.That(why, Is.EqualTo("prewarm queue 3"));

            MeshLayoutEvidenceMessage saturated = Ready();
            saturated.saturatedShare = 0.02f;
            Assert.That(MeshSplitGates.PrewarmReady(saturated, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Is.EqualTo("saturated share 0.02"));

            MeshLayoutEvidenceMessage uncapped = Ready();
            uncapped.budgetMs = 0f;
            uncapped.saturatedShare = 0.5f;   // an uncapped cell never waits: its p95 is what counts
            uncapped.p95Ms = 12f;
            Assert.That(MeshSplitGates.PrewarmReady(uncapped, E1, Band, 2f, out why), Is.True, why);
            uncapped.p95Ms = 20f;
            Assert.That(MeshSplitGates.PrewarmReady(uncapped, E1, Band, 2f, out why), Is.False);
            Assert.That(why, Does.StartWith("p95 20 ms over the nominal"));
        }

        [Test]
        public void Drain_CountsOnlyWindowsAfterE2_AndOpensAtZero()
        {
            MeshLayoutEvidenceMessage zero = MeshSplitOperationTests.Drain("C", "p", E2, 0);
            Assert.That(MeshSplitGates.Drained(zero, E2, out string why), Is.True, why);

            MeshLayoutEvidenceMessage before = MeshSplitOperationTests.Drain("C", "p", E1, 0);
            Assert.That(MeshSplitGates.DrainWindowQualifies(before, E2), Is.False);
            Assert.That(MeshSplitGates.Drained(before, E2, out why), Is.False);
            Assert.That(why, Is.EqualTo("evidence at 5/2, before e2 5/3"));

            MeshLayoutEvidenceMessage startedBefore = MeshSplitOperationTests.Drain("C", "p", E2, 0);
            startedBefore.windowStartedAfterApplySeconds = -0.2f;
            Assert.That(MeshSplitGates.Drained(startedBefore, E2, out why), Is.False);
            Assert.That(why, Is.EqualTo("the window started before the apply"));

            MeshLayoutEvidenceMessage notShrunk = MeshSplitOperationTests.Drain("C", "p", E2, 0);
            notShrunk.shrunk = false;
            Assert.That(MeshSplitGates.Drained(notShrunk, E2, out _), Is.False);

            MeshLayoutEvidenceMessage inFlight = MeshSplitOperationTests.Drain("C", "p", E2, 0);
            inFlight.drain.pendingHandoffs = 1;
            Assert.That(MeshSplitGates.DrainWindowQualifies(inFlight, E2), Is.True);
            Assert.That(MeshSplitGates.Drained(inFlight, E2, out why), Is.False, "a handoff toward the child not yet acked still counts");
            Assert.That(why, Does.StartWith("1 left"));

            Assert.That(MeshSplitGates.DrainTimeoutSeconds(50, 5f, 20f), Is.EqualTo(40f).Within(1e-4f));
        }

        [Test]
        public void Cut_SplitsTheOwnedRectangleAtTheLine()
        {
            MeshRect c = new MeshRect(-100f, -100f, 100f, 100f);
            Assert.That(MeshSplitOperation.Cut(c, MeshSplitAxis.X, 0f, true, out MeshRect given, out MeshRect kept), Is.True);
            Assert.That((given, kept), Is.EqualTo((new MeshRect(0f, -100f, 100f, 100f), new MeshRect(-100f, -100f, 0f, 100f))));
            Assert.That(MeshSplitOperation.Cut(c, MeshSplitAxis.Z, 20f, false, out given, out kept), Is.True);
            Assert.That((given, kept), Is.EqualTo((new MeshRect(-100f, -100f, 100f, 20f), new MeshRect(-100f, 20f, 100f, 100f))));
            Assert.That(MeshSplitOperation.Cut(c, MeshSplitAxis.X, 100f, true, out _, out _), Is.False, "the edge is not inside");
        }

        [Test]
        public void Command_ParsesTheContractLine()
        {
            Assert.That(MeshLayoutCommand.TryParse("12 split C x 0 + C.1", out MeshLayoutCommand command, out string error), Is.True, error);
            Assert.That((command.seq, command.cellId, command.axis, command.at, command.childOnPositiveSide, command.childId),
                Is.EqualTo((12L, "C", MeshSplitAxis.X, 0f, true, "C.1")));
            Assert.That(MeshLayoutCommand.TryParse("3  SPLIT E z -12.5 -z E.1 ", out command, out error), Is.True, error);
            Assert.That((command.axis, command.at, command.childOnPositiveSide), Is.EqualTo((MeshSplitAxis.Z, -12.5f, false)));
            Assert.That(MeshLayoutCommand.TryParse("x split C x 0 + C.1", out _, out error), Is.False);
            Assert.That(MeshLayoutCommand.TryParse("1 join C C.1", out _, out error), Is.False);
            Assert.That(error, Does.Contain("unknown verb 'join'"));
            Assert.That(MeshLayoutCommand.TryParse("1 split C y 0 + C.1", out _, out error), Is.False);
            Assert.That(MeshLayoutCommand.TryParse("1 split C x 0 +z C.1", out _, out error), Is.False);
            Assert.That(MeshLayoutCommand.TryParse("1 split C x 0 +", out _, out error), Is.False);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        [Test]
        public void Validator_MaxCellsPerBase_CountsPendingCells_WhereTheCountGrew()
        {
            MeshLayoutWorld world = new MeshLayoutWorld { bandWidth = 80f };
            world.baseCells.Add(new MeshLayoutBaseCell { id = "C", mapId = "M", rect = new MeshRect(-100f, -100f, 100f, 100f) });
            world.dynamicByMap["M"] = true;
            world.minCellSizeByMap["M"] = 80f;
            world.maxCellsPerBaseByMap["M"] = 1;
            MeshLayoutSnapshot pending = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 2) };
            pending.entries.Add(Entry("C.1", "C", MeshCellState.Pending, MeshRect.Empty, new MeshRect(0f, -100f, 100f, 100f), "M"));

            Assert.That(MeshLayoutValidator.Validate(world, pending, null, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("maxCellsPerBase: C would be cut into 2 cells, more than the map's 1"));

            world.maxCellsPerBaseByMap["M"] = 2;
            Assert.That(MeshLayoutValidator.Validate(world, pending, null, out reason), Is.True, reason);

            world.maxCellsPerBaseByMap["M"] = 1;
            Assert.That(MeshLayoutValidator.Validate(world, pending, pending, out reason), Is.True, "a count that did not grow is kept: " + reason);
            world.maxCellsPerBaseByMap.Clear();
            Assert.That(MeshLayoutValidator.Validate(world, pending, null, out reason), Is.True, "no limit when the map sets none: " + reason);
        }

        [Test]
        public void Validator_Rule7_BindsTheParentsSideToo()
        {
            MeshLayoutWorld world = new MeshLayoutWorld { bandWidth = 80f };
            world.baseCells.Add(new MeshLayoutBaseCell { id = "C", mapId = "M", rect = new MeshRect(-100f, -100f, 100f, 100f) });
            world.dynamicByMap["M"] = true;
            world.minCellSizeByMap["M"] = 100f;
            MeshLayoutSnapshot cut = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 2) };
            cut.entries.Add(Entry("C", "C", MeshCellState.Active, new MeshRect(-100f, -100f, -10f, 100f), new MeshRect(-100f, -100f, 100f, 100f), "M"));
            cut.entries.Add(Entry("C.1", "C", MeshCellState.Active, new MeshRect(-10f, -100f, 100f, 100f), new MeshRect(-10f, -100f, 100f, 100f), "M"));

            Assert.That(MeshLayoutValidator.Validate(world, cut, null, out string reason), Is.False);
            Assert.That(reason, Does.StartWith("C would own"));
        }

        [Test]
        public void RecoveryFix_OnlyAnotherCoordinatorsLayoutReopensRecovery()
        {
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, new MeshLayoutPair(7, 3), 7, MeshLayoutPair.None, false), Is.False);
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, new MeshLayoutPair(6, 3), 7, MeshLayoutPair.None, false), Is.True);
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(0, new MeshLayoutPair(6, 3), 7, MeshLayoutPair.None, false), Is.False);
        }

        [Test]
        public void StoreData_RoundTripsTheOperationFields()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeshSplitStore-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                MeshCoordinatorStore store = new MeshCoordinatorStore(System.IO.Path.Combine(dir, "store.json"));
                MeshCoordinatorStoreData data = new MeshCoordinatorStoreData { opId = "split-2", opStep = "E1Gate" };
                data.abortedIds.Add("C.1");
                Assert.That(store.TrySave(data, out string error), Is.True, error);
                Assert.That(store.TryLoad(out MeshCoordinatorStoreData read, out error), Is.True, error);
                Assert.That((read.opId, read.opStep), Is.EqualTo(("split-2", "E1Gate")));
                Assert.That(read.abortedIds, Is.EqualTo(new List<string> { "C.1" }));
            }
            finally
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);
            }
        }
    }
}
