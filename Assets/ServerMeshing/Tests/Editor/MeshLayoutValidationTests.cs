using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLayoutValidationTests
    {
        private static readonly MeshRect CWest = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect CEast = new MeshRect(0f, -100f, 100f, 100f);
        private static readonly MeshRect CBase = new MeshRect(-100f, -100f, 100f, 100f);

        private MeshWorldRegistry _world;
        private BandEnvironment _band;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_world);
            _band.Dispose();
        }

        private static IEnumerable<TestCaseData> IdCases()
        {
            yield return new TestCaseData("", false).SetName("IsSafeCellId_Empty_IsRefused");
            yield return new TestCaseData("C_1", false).SetName("IsSafeCellId_Underscore_IsRefused");
            yield return new TestCaseData("C:1", false).SetName("IsSafeCellId_Colon_IsRefused");
            yield return new TestCaseData("C|1", false).SetName("IsSafeCellId_Bar_IsRefused");
            yield return new TestCaseData("C 1", false).SetName("IsSafeCellId_Whitespace_IsRefused");
            yield return new TestCaseData("C?1", false).SetName("IsSafeCellId_FileNameCharacter_IsRefused");
            yield return new TestCaseData(new string('c', 51), false).SetName("IsSafeCellId_TooLong_IsRefused");
            yield return new TestCaseData(new string('c', 50), true).SetName("IsSafeCellId_FiftyCharacters_IsAccepted");
            yield return new TestCaseData("C.1", true).SetName("IsSafeCellId_ChildId_IsAccepted");
        }

        [TestCaseSource(nameof(IdCases))]
        public void IsSafeCellId(string id, bool expected)
        {
            Assert.That(MeshLayoutValidator.IsSafeCellId(id, out string reason), Is.EqualTo(expected), reason);
        }

        private static IEnumerable<TestCaseData> RefusedCases()
        {
            yield return new TestCaseData(Snapshot(1, Active("C", "C", CWest), Active("c", "C", CEast)), "appears twice").SetName("Validate_IdDifferingOnlyByCase_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", CWest), Active("E", "C", CEast)), "takes the id of an asset cell").SetName("Validate_ChildTakingAnAssetId_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C.1", "NOPE", CEast)), "is not a cell of map").SetName("Validate_UnknownBase_IsRefused");
            yield return new TestCaseData(Snapshot(1, Entry("C2.1", "C2", MeshCellState.Active, CEast, CEast, Map2)), "is Static").SetName("Validate_EntryOnAStaticMap_IsRefused");
            yield return new TestCaseData(Snapshot(1, Entry("C.1", "C", MeshCellState.Active, MeshRect.Empty, MeshRect.Empty)), "owns nothing").SetName("Validate_ActiveOwningNothing_IsRefused");
            yield return new TestCaseData(Snapshot(1, Entry("C.1", "C", MeshCellState.Pending, CEast, CEast)), "is Pending but owns").SetName("Validate_PendingOwningSomething_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", CWest), Entry("C.1", "C", MeshCellState.Active, CEast, new MeshRect(0f, -100f, 50f, 100f))), "does not cover owned").SetName("Validate_InterestSmallerThanOwned_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", CWest), Active("C.1", "C", new MeshRect(0f, -100f, 150f, 100f))), "outside its base").SetName("Validate_OwnedOutsideBase_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, 1f, 100f)), Active("C.1", "C", CEast)), "overlap").SetName("Validate_OverlappingByOneMetre_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, -10f, 100f)), Active("C.1", "C", CEast)), "must tile it").SetName("Validate_FamilyLeavingAGap_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, -40f, 100f)), Active("C.1", "C", new MeshRect(-40f, -100f, 100f, 100f))), "narrower than").SetName("Validate_CutNarrowerThanTheBand_IsRefused");
            yield return new TestCaseData(Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, -10f, 100f)), Active("C.1", "C", new MeshRect(-10f, -100f, 100f, 100f))), "minimum cell").SetName("Validate_CutBelowMinCellSize_IsRefused");
            yield return new TestCaseData(Snapshot(1, Entry("C", "C", MeshCellState.Static, CBase, CBase)), "cannot appear").SetName("Validate_StaticStateInAnEntry_IsRefused");
        }

        [TestCaseSource(nameof(RefusedCases))]
        public void Validate_Refused(MeshLayoutSnapshot snapshot, string reasonContains)
        {
            bool ok = MeshLayoutValidator.Validate(_world.ValidationWorld(), snapshot, null, out string reason);

            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain(reasonContains));
        }

        [Test]
        public void Validate_TooManyBandNeighbours_IsRefused()
        {
            // A row of 400 x 200 m cells, band 200, at most 2 band neighbours: B1 had B0 and B2; cut in two, B1 keeps B0, gains
            // B1.1, and still sees B2 across its new 200 m gap, which is within the band.
            MeshLayoutWorld world = new MeshLayoutWorld { bandWidth = 200f, maxNeighbours = 2 };
            world.dynamicByMap["M"] = true;
            world.minCellSizeByMap["M"] = 200f;
            for (int i = 0; i < 4; ++i)
                world.baseCells.Add(new MeshLayoutBaseCell { id = "B" + i, mapId = "M", rect = MeshRect.FromMinSize(i * 400f, 0f, 400f, 200f) });
            MeshLayoutSnapshot split = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 1) };
            split.entries.Add(new MeshLayoutEntry { id = "B1", mapId = "M", baseId = "B1", state = MeshCellState.Active, owned = MeshRect.FromMinSize(400f, 0f, 200f, 200f), interest = MeshRect.FromMinSize(400f, 0f, 200f, 200f) });
            split.entries.Add(new MeshLayoutEntry { id = "B1.1", mapId = "M", baseId = "B1", state = MeshCellState.Active, owned = MeshRect.FromMinSize(600f, 0f, 200f, 200f), interest = MeshRect.FromMinSize(600f, 0f, 200f, 200f) });

            bool ok = MeshLayoutValidator.Validate(world, split, null, out string reason);

            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("band neighbours"));
        }

        private static IEnumerable<TestCaseData> AcceptedCases()
        {
            yield return new TestCaseData(Snapshot(1)).SetName("Validate_EmptySnapshot_IsAccepted");
            yield return new TestCaseData(SplitC()).SetName("Validate_MidpointSplit_IsAccepted");
            yield return new TestCaseData(Snapshot(1, Entry("C.1", "C", MeshCellState.Pending, MeshRect.Empty, CEast))).SetName("Validate_PendingChildBeforeCommit_IsAccepted");
            yield return new TestCaseData(Snapshot(1, Entry("C", "C", MeshCellState.Active, CWest, CBase), Active("C.1", "C", CEast))).SetName("Validate_ParentStillInterestedInTheMovedHalf_IsAccepted");
            yield return new TestCaseData(Snapshot(1, Entry("C.1", "C", MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty))).SetName("Validate_RetiredChild_IsAccepted");
        }

        [TestCaseSource(nameof(AcceptedCases))]
        public void Validate_Accepted(MeshLayoutSnapshot snapshot)
        {
            Assert.That(MeshLayoutValidator.Validate(_world.ValidationWorld(), snapshot, null, out string reason), Is.True, reason);
        }

        [Test]
        public void Validate_CarriedRectangleBelowMinCellSize_IsAccepted()
        {
            MeshLayoutSnapshot previous = Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, -10f, 100f)), Active("C.1", "C", new MeshRect(-10f, -100f, 100f, 100f)));
            MeshLayoutSnapshot next = Snapshot(2, Active("C", "C", new MeshRect(-100f, -100f, -10f, 100f)), Active("C.1", "C", new MeshRect(-10f, -100f, 100f, 100f)));

            Assert.That(MeshLayoutValidator.Validate(_world.ValidationWorld(), next, previous, out string reason), Is.True, reason);
        }

        [Test]
        public void Validate_EmptySnapshot_IsAcceptedOnABaseLayoutThatBreaksTheBandFloor()
        {
            MeshLayoutWorld world = new MeshLayoutWorld { bandWidth = 80f };
            world.dynamicByMap["M"] = true;
            world.baseCells.Add(new MeshLayoutBaseCell { id = "Thin", mapId = "M", rect = MeshRect.FromMinSize(0f, 0f, 20f, 200f) });

            Assert.That(MeshLayoutValidator.Validate(world, new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 1) }, null, out string reason), Is.True, reason);
        }
    }
}
