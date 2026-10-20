using System.Collections.Generic;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The pure recovery decisions (Slice 2a; Slice 5 decisions 1-5) and the stored operation's table (decision 7).</summary>
    public class MeshRecoveryResolverTests
    {
        private const string Map = Map1;
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);

        private static MeshLayoutSnapshot Cut(long incarnation, long version)
        {
            MeshLayoutSnapshot snapshot = Snapshot(version, Active("C", "C", West), Active("C.1", "C", East));
            snapshot.pair = new MeshLayoutPair(incarnation, version);
            return snapshot;
        }

        private static MeshRecoveryInput Input(bool dynamicMap = true, bool trusted = true, MeshLayoutSnapshot stored = null, MeshLayoutSnapshot staged = null,
            bool storedOp = false, float elapsed = 0f, string[] registered = null, string[] hello = null, params (string cell, MeshLayoutSnapshot held)[] held)
        {
            MeshRecoveryInput input = new MeshRecoveryInput
            {
                mapId = Map, mapDynamic = dynamicMap, storeTrusted = trusted, storedBroadcast = stored, storedStaged = staged, storedOperation = storedOp,
                elapsedSeconds = elapsed, ceilingSeconds = 60f,
            };
            input.dynamicMaps.Add(Map);
            input.registeredCells.AddRange(registered ?? new string[0]);
            foreach (string id in hello ?? new string[0])
                input.helloCells.Add(id);
            foreach ((string cell, MeshLayoutSnapshot snapshot) in held)
                input.held.Add(new MeshHeldLayout { cellId = cell, snapshot = snapshot });
            return input;
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(Input(dynamicMap: false, stored: Cut(1, 3)), MeshRecoveryOutcome.AdoptStatic, "2 stored dynamic entries ignored").SetName("Resolve_StaticMapWithStoredEntries_IgnoresThem");
            yield return new TestCaseData(Input(), MeshRecoveryOutcome.AdoptStatic, "no runtime layout anywhere").SetName("Resolve_NothingRegistered_AdoptsAtOnce");
            yield return new TestCaseData(Input(registered: new[] { "C" }, hello: new[] { "c" }), MeshRecoveryOutcome.AdoptStatic, "no runtime layout anywhere").SetName("Resolve_EveryCellSaidHello_Adopts");
            yield return new TestCaseData(Input(elapsed: 5f, registered: new[] { "C", "E" }, hello: new[] { "C" }), MeshRecoveryOutcome.Waiting, "waiting for the hello of E").SetName("Resolve_MissingHelloBeforeTheCeiling_Waits");
            yield return new TestCaseData(Input(elapsed: 60f, registered: new[] { "C", "E" }, hello: new[] { "C" }), MeshRecoveryOutcome.Frozen, "no hello from E").SetName("Resolve_MissingHelloAtTheCeiling_Freezes");
            yield return new TestCaseData(Input(trusted: false, elapsed: 5f), MeshRecoveryOutcome.Waiting, "cannot be trusted").SetName("Resolve_UntrustedStore_Waits");
            yield return new TestCaseData(Input(trusted: false, elapsed: 60f), MeshRecoveryOutcome.AdoptStatic, "could not be trusted").SetName("Resolve_UntrustedStoreAtTheCeiling_AdoptsTheAssetCut");
            // Slice 5: the stored layout names C.1, so the map waits for it, then adopts.
            yield return new TestCaseData(Input(stored: Cut(1, 3), registered: new[] { "C" }, hello: new[] { "C" }), MeshRecoveryOutcome.Waiting, "waiting for the hello of C.1").SetName("Resolve_StoredChildMissing_Waits");
            yield return new TestCaseData(Input(stored: Cut(1, 3), registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }), MeshRecoveryOutcome.AdoptStored, "adopted layout 1/3 from the store with 2 entries (C, C.1)").SetName("Resolve_StoredChildSaidHello_AdoptsTheStore");
            yield return new TestCaseData(Input(stored: Cut(1, 3), elapsed: 60f, registered: new[] { "C" }, hello: new[] { "C" }), MeshRecoveryOutcome.AdoptStored, "adopted layout 1/3").SetName("Resolve_StoredChildMissingAtTheCeiling_AdoptsWithTheFamilyFrozen");
            yield return new TestCaseData(Input(trusted: false, registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C", Cut(1, 3)), ("C.1", Cut(1, 3)) }), MeshRecoveryOutcome.AdoptStored, "from cell C with 2 entries").SetName("Resolve_UntrustedStoreButCellsHoldALayout_AdoptsIt");
            yield return new TestCaseData(Input(stored: Cut(1, 3), registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C.1", Cut(1, 4)) }), MeshRecoveryOutcome.AdoptStored, "adopted layout 1/4 from cell C.1").SetName("Resolve_ACellHoldsANewerPair_ItWins");
            yield return new TestCaseData(Input(stored: Cut(1, 3), registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C", Cut(1, 3)), ("C.1", Snapshot(3, Active("C", "C", West))) }), MeshRecoveryOutcome.Frozen, "the store and cell C.1 hold different layouts at 1/3").SetName("Resolve_TwoLayoutsAtOnePair_Freezes");
            yield return new TestCaseData(Input(stored: Snapshot(0, Active("C", "C", West)), storedOp: true, registered: new[] { "C" }, hello: new[] { "C" }), MeshRecoveryOutcome.AdoptStored, "adopted layout").SetName("Resolve_StoredOperationWithoutEntries_IsStillARuntimeLayout");
        }

        [TestCaseSource(nameof(Cases))]
        public void Resolve(MeshRecoveryInput input, MeshRecoveryOutcome expected, string reasonContains)
        {
            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);

            Assert.That(decision.outcome, Is.EqualTo(expected));
            Assert.That(decision.reason, Does.Contain(reasonContains));
        }

        [Test]
        public void Resolve_AMissingChildAtTheCeiling_FreezesItsFamilyOnly()
        {
            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(Input(stored: Cut(1, 3), elapsed: 60f, registered: new[] { "C", "E" }, hello: new[] { "C", "E" }));

            Assert.That(decision.outcome, Is.EqualTo(MeshRecoveryOutcome.AdoptStored));
            Assert.That(decision.isError, Is.True);
            Assert.That(decision.frozenFamilies.Count, Is.EqualTo(1));
            Assert.That((decision.frozenFamilies[0].baseId, decision.frozenFamilies[0].missingIds[0]), Is.EqualTo(("C", "C.1")));
            Assert.That(decision.frozenFamilies[0].reason, Does.Contain("no hello from C.1 after 60 s"));
        }

        [Test]
        public void Resolve_TheStagedStep_CountsOnlyWhenItsTargetHoldsIt()
        {
            MeshLayoutSnapshot stored = Cut(1, 3), staged = Cut(1, 4);
            MeshRecoveryInput notHeld = Input(stored: stored, staged: staged, registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C", stored), ("C.1", stored) });
            MeshRecoveryInput held = Input(stored: stored, staged: staged, registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C", stored), ("C.1", staged) });

            Assert.That(MeshRecoveryResolver.Resolve(notHeld).chosen.pair, Is.EqualTo(new MeshLayoutPair(1, 3)));
            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(held);
            Assert.That(decision.chosen.pair, Is.EqualTo(new MeshLayoutPair(1, 4)));
            Assert.That(decision.chosenSource, Does.Contain("cell C.1").Or.Contain("staged"));
        }

        [Test]
        public void Resolve_AnExcludedId_IsNotWaitedFor()
        {
            MeshRecoveryInput input = Input(stored: Cut(1, 3), registered: new[] { "C" }, hello: new[] { "C" });
            input.excludedIds.Add("C.1");

            Assert.That(MeshRecoveryResolver.Resolve(input).outcome, Is.EqualTo(MeshRecoveryOutcome.AdoptStored));
        }

        [Test]
        public void Resolve_AStaticNeighboursHeldLayout_IsACandidate()
        {
            MeshRecoveryInput input = Input(trusted: false, registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C2", Cut(1, 5)) });

            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);

            Assert.That((decision.outcome, decision.chosen.pair), Is.EqualTo((MeshRecoveryOutcome.AdoptStored, new MeshLayoutPair(1, 5))));
        }

        [Test]
        public void HeldReopensRecovery_Rules()
        {
            MeshLayoutPair old = new MeshLayoutPair(1, 7), adopted = new MeshLayoutPair(1, 5), ours = new MeshLayoutPair(9, 1);
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, old, 9, MeshLayoutPair.None, false), Is.True, "no runtime layout adopted: any held layout re-opens");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, old, 9, adopted, false), Is.True, "newer than the adopted one");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, adopted, 9, adopted, false), Is.False, "at the adopted pair");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, new MeshLayoutPair(1, 4), 9, adopted, false), Is.False, "older than the adopted one");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, old, 9, adopted, true), Is.False, "after an own broadcast");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(0, old, 9, MeshLayoutPair.None, false), Is.False, "no entries");
            Assert.That(MeshRecoveryResolver.HeldReopensRecovery(2, ours, 9, MeshLayoutPair.None, false), Is.False, "our own incarnation");
        }

        // ---- the stored operation (decision 7) ---------------------------------------------------------------------

        private static MeshStoredOperation Split(MeshSplitStep step, int stragglers = 0, bool settled = false)
        {
            return new MeshStoredOperation
            {
                kind = MeshStoredOperation.SplitKind, opId = "split-2", step = step.ToString(), baseId = "C", mapId = Map, parentId = "C", childId = "C.1",
                e1Pair = new MeshLayoutPair(1, 2), e2Pair = new MeshLayoutPair(1, 3), e3Pair = step >= MeshSplitStep.E3Broadcast && step != MeshSplitStep.Frozen ? new MeshLayoutPair(1, 4) : MeshLayoutPair.None,
                stragglers = stragglers, settled = settled,
            };
        }

        private static MeshStoredOperation Merge(MeshMergeStep step)
        {
            return new MeshStoredOperation
            {
                kind = MeshStoredOperation.MergeKind, opId = "merge-1", step = step.ToString(), baseId = "C", mapId = Map, survivorId = "C", retiringId = "C.1",
                e1Pair = new MeshLayoutPair(1, 5), e2Pair = new MeshLayoutPair(1, 6), e3Pair = step >= MeshMergeStep.E3Broadcast && step != MeshMergeStep.Frozen ? new MeshLayoutPair(1, 7) : MeshLayoutPair.None,
                tidyPair = step == MeshMergeStep.TidyBroadcast ? new MeshLayoutPair(1, 8) : MeshLayoutPair.None,
            };
        }

        private static Dictionary<string, MeshLayoutPair> Held(params (string cell, long version)[] pairs)
        {
            Dictionary<string, MeshLayoutPair> held = new Dictionary<string, MeshLayoutPair>(System.StringComparer.OrdinalIgnoreCase);
            foreach ((string cell, long version) in pairs)
                held[cell] = new MeshLayoutPair(1, version);
            return held;
        }

        private static IEnumerable<TestCaseData> OperationCases()
        {
            yield return new TestCaseData(Split(MeshSplitStep.Spawning), 1L, Held(("C", 1)), MeshOperationVerdictKind.Abort, "before e1").SetName("Operation_SplitSpawning_Aborts");
            yield return new TestCaseData(Split(MeshSplitStep.E1Staged), 1L, Held(("C", 1), ("C.1", 2)), MeshOperationVerdictKind.ResendStaged, "holds the staged e1").SetName("Operation_SplitE1StagedHeld_Resends");
            yield return new TestCaseData(Split(MeshSplitStep.E1Staged), 1L, Held(("C", 1)), MeshOperationVerdictKind.Abort, "before e1").SetName("Operation_SplitE1StagedNotHeld_Aborts");
            yield return new TestCaseData(Split(MeshSplitStep.E1Gate), 2L, Held(("C", 2), ("C.1", 2)), MeshOperationVerdictKind.Abort, "before e2").SetName("Operation_SplitE1Gate_Aborts");
            yield return new TestCaseData(Split(MeshSplitStep.E2Staged), 2L, Held(("C", 2), ("C.1", 3)), MeshOperationVerdictKind.ResendStaged, "holds the staged e2").SetName("Operation_SplitE2StagedHeld_Resends");
            yield return new TestCaseData(Split(MeshSplitStep.E2Staged), 2L, Held(("C", 2), ("C.1", 2)), MeshOperationVerdictKind.Abort, "before e2").SetName("Operation_SplitE2StagedNotHeld_Aborts");
            yield return new TestCaseData(Split(MeshSplitStep.DrainGate), 3L, Held(("C", 3), ("C.1", 3)), MeshOperationVerdictKind.Resume, "drain gate").SetName("Operation_SplitDrainGate_ResumesThere");
            yield return new TestCaseData(Split(MeshSplitStep.E3Broadcast), 3L, Held(("C", 3), ("C.1", 3)), MeshOperationVerdictKind.Resume, "drain gate").SetName("Operation_SplitE3BroadcastButE2Chosen_ResumesAtTheDrainGate");
            yield return new TestCaseData(Split(MeshSplitStep.E3Broadcast), 4L, Held(("C", 4), ("C.1", 4)), MeshOperationVerdictKind.Resume, "e3 was broadcast").SetName("Operation_SplitE3BroadcastChosen_ResumesAtE3");
            yield return new TestCaseData(Split(MeshSplitStep.Done, stragglers: 2), 4L, Held(("C", 4), ("C.1", 4)), MeshOperationVerdictKind.ResumeSettle, "straggler").SetName("Operation_SplitDoneWithStragglers_ResumesTheSettle");
            yield return new TestCaseData(Split(MeshSplitStep.Frozen), 2L, Held(("C", 2), ("C.1", 3)), MeshOperationVerdictKind.ResendStaged, "holds the staged e2").SetName("Operation_SplitFrozenWithStagedHeld_Resends");
            yield return new TestCaseData(Split(MeshSplitStep.Frozen), 3L, Held(("C", 3), ("C.1", 3)), MeshOperationVerdictKind.Drop, "frozen").SetName("Operation_SplitFrozen_Drops");
            yield return new TestCaseData(Split(MeshSplitStep.DrainGate), 9L, Held(("C", 9)), MeshOperationVerdictKind.Drop, "stale").SetName("Operation_AdoptedNewerThanTheOperation_Drops");
            yield return new TestCaseData(Merge(MeshMergeStep.E1Gate), 5L, Held(("C", 5), ("C.1", 5)), MeshOperationVerdictKind.Abort, "before e2").SetName("Operation_MergeE1Gate_Aborts");
            yield return new TestCaseData(Merge(MeshMergeStep.E2Staged), 5L, Held(("C", 6), ("C.1", 5)), MeshOperationVerdictKind.ResendStaged, "holds the staged e2").SetName("Operation_MergeE2StagedHeld_Resends");
            yield return new TestCaseData(Merge(MeshMergeStep.E2Staged), 5L, Held(("C", 5), ("C.1", 5)), MeshOperationVerdictKind.Abort, "before e2").SetName("Operation_MergeE2StagedNotHeld_Aborts");
            yield return new TestCaseData(Merge(MeshMergeStep.DrainGate), 6L, Held(("C", 6), ("C.1", 6)), MeshOperationVerdictKind.Resume, "DrainGate").SetName("Operation_MergeDrainGate_Resumes");
            yield return new TestCaseData(Merge(MeshMergeStep.Retiring), 6L, Held(("C", 6)), MeshOperationVerdictKind.Resume, "Retiring").SetName("Operation_MergeRetiring_Resumes");
            yield return new TestCaseData(Merge(MeshMergeStep.E3Broadcast), 7L, Held(("C", 7)), MeshOperationVerdictKind.Resume, "E3Broadcast").SetName("Operation_MergeE3_Resumes");
            yield return new TestCaseData(Merge(MeshMergeStep.TidyBroadcast), 8L, Held(("C", 8)), MeshOperationVerdictKind.Resume, "TidyBroadcast").SetName("Operation_MergeTidy_Resumes");
            yield return new TestCaseData(Merge(MeshMergeStep.Frozen), 5L, Held(("C", 5)), MeshOperationVerdictKind.Drop, "frozen").SetName("Operation_MergeFrozen_Drops");
        }

        [TestCaseSource(nameof(OperationCases))]
        public void ResolveOperation(MeshStoredOperation stored, long chosenVersion, Dictionary<string, MeshLayoutPair> held, MeshOperationVerdictKind expected, string reasonContains)
        {
            MeshOperationVerdict verdict = MeshRecoveryResolver.ResolveOperation(stored, new MeshLayoutPair(1, chosenVersion), held);

            Assert.That(verdict.kind, Is.EqualTo(expected), verdict.reason);
            Assert.That(verdict.reason, Does.Contain(reasonContains));
        }

        [Test]
        public void ResolveOperation_ResumeSteps_AreTheOnesToEnter()
        {
            Assert.That(MeshRecoveryResolver.ResolveOperation(Split(MeshSplitStep.E3Broadcast), new MeshLayoutPair(1, 3), Held(("C", 3), ("C.1", 3))).splitStep, Is.EqualTo(MeshSplitStep.DrainGate));
            Assert.That(MeshRecoveryResolver.ResolveOperation(Split(MeshSplitStep.E3Broadcast), new MeshLayoutPair(1, 4), Held(("C", 4))).splitStep, Is.EqualTo(MeshSplitStep.E3Broadcast));
            Assert.That(MeshRecoveryResolver.ResolveOperation(Split(MeshSplitStep.Frozen), new MeshLayoutPair(1, 2), Held(("C.1", 3))).splitStep, Is.EqualTo(MeshSplitStep.E2Staged));
            Assert.That(MeshRecoveryResolver.ResolveOperation(Merge(MeshMergeStep.Retiring), new MeshLayoutPair(1, 6), Held(("C", 6))).mergeStep, Is.EqualTo(MeshMergeStep.Retiring));
        }

        [Test]
        public void Resolve_AnEmptyNewerHeldLayout_WinsAndIsRememberedAsTheAdoptedPair()
        {
            MeshLayoutSnapshot tidy = Snapshot(0);
            tidy.pair = new MeshLayoutPair(1, 8);
            MeshRecoveryInput input = Input(trusted: false, registered: new[] { "C", "C.1" }, hello: new[] { "C", "C.1" }, held: new[] { ("C", tidy), ("C.1", Cut(1, 3)) });

            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);

            Assert.That((decision.outcome, decision.chosen.pair), Is.EqualTo((MeshRecoveryOutcome.AdoptStatic, new MeshLayoutPair(1, 8))));
        }

        [Test]
        public void Resolve_AnAbortableMissingChild_FreezesNoFamily()
        {
            MeshRecoveryInput input = Input(stored: Cut(1, 3), storedOp: true, elapsed: 60f, registered: new[] { "C" }, hello: new[] { "C" });
            input.abortableIds.Add("C.1");

            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);

            Assert.That((decision.outcome, decision.frozenFamilies.Count, decision.isError), Is.EqualTo((MeshRecoveryOutcome.AdoptStored, 0, false)));
        }

        [Test]
        public void StoredOperation_ExcludedAndAbortableIds()
        {
            Assert.That(Split(MeshSplitStep.Spawning).ExcludedId, Is.EqualTo("C.1"));
            Assert.That(Split(MeshSplitStep.E1Gate).ExcludedId, Is.EqualTo("C.1"));
            Assert.That(Split(MeshSplitStep.E1Staged).ExcludedId, Is.Null, "its hello decides whether e1 is re-sent");
            Assert.That(Split(MeshSplitStep.E2Staged).ExcludedId, Is.Null);
            Assert.That(Split(MeshSplitStep.DrainGate).ExcludedId, Is.Null);
            Assert.That(Merge(MeshMergeStep.E2Staged).ExcludedId, Is.Null);
            Assert.That(Merge(MeshMergeStep.DrainGate).ExcludedId, Is.EqualTo("C.1"));
            Assert.That(Merge(MeshMergeStep.Retiring).ExcludedId, Is.EqualTo("C.1"));
            Assert.That(Split(MeshSplitStep.E2Staged).AbortableId, Is.EqualTo("C.1"));
            Assert.That(Split(MeshSplitStep.DrainGate).AbortableId, Is.Null);
            Assert.That(Merge(MeshMergeStep.E1Gate).AbortableId, Is.EqualTo("C.1"));
            Assert.That(Merge(MeshMergeStep.DrainGate).AbortableId, Is.Null);
            Assert.That(MeshStoredOperation.Number("merge-12"), Is.EqualTo(12));
            Assert.That(MeshStoredOperation.E1Broadcast(new MeshLayoutPair(1, 2), new MeshLayoutPair(1, 2)), Is.True);
            Assert.That(MeshStoredOperation.E1Broadcast(new MeshLayoutPair(1, 1), new MeshLayoutPair(1, 2)), Is.False);
        }
    }
}
