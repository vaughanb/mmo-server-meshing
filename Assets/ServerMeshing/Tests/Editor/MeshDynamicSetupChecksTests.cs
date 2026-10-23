using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshDynamicSetupChecksTests
    {
        private BandEnvironment _band;
        private readonly List<MeshWorldRegistry> _worlds = new List<MeshWorldRegistry>();

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (MeshWorldRegistry world in _worlds)
                Object.DestroyImmediate(world);
            _worlds.Clear();
            _band.Dispose();
        }

        private MeshWorldRegistry Rig(bool dynamic = true, float minCellSize = 100f, float band = 80f, Action<MeshWorldRegistry> edit = null)
        {
            MeshWorldRegistry world = RigLike(dynamic, minCellSize, band);
            _worlds.Add(world);
            if (edit != null)
            {
                edit(world);
                world.Rebuild();
            }
            return world;
        }

        /// <summary>One Dynamic map of explicit cells (scene frame = world frame), in the given order.</summary>
        private MeshWorldRegistry Explicit(float band, float minCellSize, params (string id, float minX, float minZ, float sizeX, float sizeZ)[] cells)
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            _worlds.Add(world);
            world.dynamicSettings.bandWidth = band;
            MeshCellRect[] rects = new MeshCellRect[cells.Length];
            for (int i = 0; i < cells.Length; ++i)
                rects[i] = new MeshCellRect { id = cells[i].id, min = new Vector2(cells[i].minX, cells[i].minZ), size = new Vector2(cells[i].sizeX, cells[i].sizeZ) };
            world.maps.Add(new MeshMapLayout { mapId = Map1, cellRects = rects, cellMode = MeshCellMode.Dynamic, minCellSize = minCellSize });
            world.Rebuild();
            return world;
        }

        private static List<MeshSetupSpawner> Spawners(params (string owner, string[] cells)[] spawners)
        {
            List<MeshSetupSpawner> list = new List<MeshSetupSpawner>();
            foreach ((string owner, string[] cells) in spawners)
                list.Add(new MeshSetupSpawner { owner = owner, servedCells = cells });
            return list;
        }

        // ---------------------------------------------------------------- bases that can never split

        [Test]
        public void BasesThatCannotSplit_UnderTwiceTheMinimumOnBothAxes_IsWarned()
        {
            MeshWorldRegistry world = Rig(minCellSize: 120f);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.BasesThatCannotSplit(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].severity, Is.EqualTo(MeshSetupSeverity.Warning));
            Assert.That(findings[0].mapId, Is.EqualTo(Map1));
            Assert.That(findings[0].cellIds, Is.EqualTo(new[] { "SW", "S", "SE", "W", "C", "E", "NW", "N", "NE" }));
            Assert.That(findings[0].text, Is.EqualTo("Dynamic map Map1: 9 base cell(s) can never split (SW, S, SE, W, C, E, NW, N, NE): no cut leaves both sides at least the 120 m minimum cell"));
        }

        [Test]
        public void BasesThatCannotSplit_LongButUnderTheMinimumAcross_IsWarned()
        {
            // 300 m along x would take a cut, but each side keeps the 90 m depth, under the 100 m minimum (rule 7).
            MeshWorldRegistry world = Explicit(80f, 100f, ("N", 0f, 0f, 300f, 90f));

            Assert.That(MeshDynamicSetupChecks.BasesThatCannotSplit(world)[0].cellIds, Is.EqualTo(new[] { "N" }));
        }

        [Test]
        public void BasesThatCannotSplit_MaxCellsPerBaseOne_IsWarned()
        {
            MeshWorldRegistry world = Rig(edit: w => w.maps[0].maxCellsPerBase = 1);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.BasesThatCannotSplit(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].text, Is.EqualTo("Dynamic map Map1: maxCellsPerBase is 1, so no base cell can be cut"));
        }

        [Test]
        public void BasesThatCannotSplit_NoLimit_IsWarnedAndStillJudgesTheCut()
        {
            MeshWorldRegistry world = Rig(minCellSize: 120f, edit: w => w.dynamicSettings.defaultMaxCellsPerBase = 0);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.BasesThatCannotSplit(world);

            Assert.That(findings, Has.Count.EqualTo(2));
            Assert.That(findings[0].text, Is.EqualTo("Dynamic map Map1: maxCellsPerBase is 0: a command meets no limit and the central's policy splits nothing; set it to 2 or more"));
            Assert.That(findings[1].cellIds, Has.Count.EqualTo(9));
        }

        [Test]
        public void BasesThatCannotSplit_ManyCells_NamesTenAndCountsTheRest()
        {
            List<(string, float, float, float, float)> cells = new List<(string, float, float, float, float)>();
            for (int i = 0; i < 12; ++i)
                cells.Add(("K" + i, i * 150f, 0f, 150f, 150f));
            MeshWorldRegistry world = Explicit(80f, 100f, cells.ToArray());

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.BasesThatCannotSplit(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].cellIds, Has.Count.EqualTo(12));
            Assert.That(findings[0].text, Does.Contain("(K0, K1, K2, K3, K4, K5, K6, K7, K8, K9 and 2 more)"));
        }

        [Test]
        public void BasesThatCannotSplit_ExactlyTwiceTheMinimum_IsClean()
        {
            Assert.That(MeshDynamicSetupChecks.BasesThatCannotSplit(Rig(minCellSize: 100f)), Is.Empty);
        }

        [Test]
        public void BasesThatCannotSplit_OneLongAxis_IsClean()
        {
            MeshWorldRegistry world = Explicit(80f, 100f, ("L", 0f, 0f, 300f, 150f));

            Assert.That(MeshDynamicSetupChecks.BasesThatCannotSplit(world), Is.Empty);
        }

        [Test]
        public void BasesThatCannotSplit_StaticMap_IsNotJudged()
        {
            Assert.That(MeshDynamicSetupChecks.BasesThatCannotSplit(Rig(dynamic: false, minCellSize: 120f)), Is.Empty);
        }

        // ---------------------------------------------------------------- the minimum cell under the band

        [Test]
        public void MinCellSizeUnderBand_MapsOwnValue_IsAnErrorWithAFix()
        {
            MeshWorldRegistry world = Rig(minCellSize: 60f);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.MinCellSizeUnderBand(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].severity, Is.EqualTo(MeshSetupSeverity.Error));
            Assert.That(findings[0].fix, Is.EqualTo(MeshSetupFix.RaiseMinCellSizeToBand));
            Assert.That(findings[0].text, Is.EqualTo("Dynamic map Map1: minCellSize 60 m is under the 80 m band; the central cuts no cell narrower than the band, so it uses 80 m"));
        }

        [Test]
        public void MinCellSizeUnderBand_Fix_RaisesTheMapsFieldAndClearsTheFinding()
        {
            MeshWorldRegistry world = Rig(minCellSize: 60f);
            float before = MeshDynamicSetupChecks.EffectiveMinCellSize(world.GetLayout(Map1), world.dynamicSettings, world.dynamicSettings.bandWidth);

            bool changed = MeshDynamicSetupChecks.ApplyFix(world, MeshDynamicSetupChecks.MinCellSizeUnderBand(world)[0]);

            Assert.That(changed, Is.True);
            Assert.That(world.GetLayout(Map1).minCellSize, Is.EqualTo(80f));
            Assert.That(MeshDynamicSetupChecks.EffectiveMinCellSize(world.GetLayout(Map1), world.dynamicSettings, world.dynamicSettings.bandWidth), Is.EqualTo(before), "the runtime value does not change");
            Assert.That(MeshDynamicSetupChecks.MinCellSizeUnderBand(world), Is.Empty);
        }

        [Test]
        public void MinCellSizeUnderBand_RegistryDefault_IsFixedOnTheMap()
        {
            MeshWorldRegistry world = Rig(minCellSize: 0f, edit: w => w.dynamicSettings.defaultMinCellSize = 60f);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.MinCellSizeUnderBand(world);
            bool changed = MeshDynamicSetupChecks.ApplyFix(world, findings[0]);

            Assert.That(findings[0].text, Does.Contain("minCellSize (the registry's defaultMinCellSize) 60 m"));
            Assert.That(changed, Is.True);
            Assert.That(world.GetLayout(Map1).minCellSize, Is.EqualTo(80f));
            Assert.That(world.dynamicSettings.defaultMinCellSize, Is.EqualTo(60f), "the registry-wide default stays the operator's");
        }

        [Test]
        public void MinCellSizeUnderBand_BandNotSet_IsAnErrorAndNoFix()
        {
            MeshWorldRegistry world = Rig(minCellSize: 60f, edit: w => w.dynamicSettings.bandWidth = 0f);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.MinCellSizeUnderBand(world);

            Assert.That(findings, Has.Count.EqualTo(2));
            Assert.That(findings[0].text, Does.StartWith("Registry: dynamicSettings.bandWidth is 0: the map servers fall back to 80 m"));
            Assert.That(findings[1].fix, Is.EqualTo(MeshSetupFix.None), "the fix would change the policy's band");
            Assert.That(MeshDynamicSetupChecks.ApplyFix(world, new MeshSetupFinding { mapId = Map1, fix = MeshSetupFix.RaiseMinCellSizeToBand }), Is.False);
        }

        [Test]
        public void MinCellSizeUnderBand_DefaultAboveTheBand_IsClean()
        {
            Assert.That(MeshDynamicSetupChecks.MinCellSizeUnderBand(Rig(minCellSize: 0f)), Is.Empty);
        }

        [Test]
        public void MinCellSizeUnderBand_StaticMap_IsNotJudged()
        {
            Assert.That(MeshDynamicSetupChecks.MinCellSizeUnderBand(Rig(dynamic: false, minCellSize: 60f)), Is.Empty);
        }

        [Test]
        public void ApplyFix_FindingWithoutAFix_ChangesNothing()
        {
            MeshWorldRegistry world = Rig(minCellSize: 120f);

            Assert.That(MeshDynamicSetupChecks.ApplyFix(world, MeshDynamicSetupChecks.BasesThatCannotSplit(world)[0]), Is.False);
            Assert.That(world.GetLayout(Map1).minCellSize, Is.EqualTo(120f));
        }

        // ---------------------------------------------------------------- the worst-case band-neighbour count

        [Test]
        public void BandNeighbourFindings_RigCutToFourPerBase_WarnsForCOnly()
        {
            MeshWorldRegistry world = Rig();

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.BandNeighbourFindings(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].cellIds, Is.EqualTo(new[] { "C" }));
            Assert.That(findings[0].text, Does.StartWith("C (map Map1): up to 35 band neighbours when every base near it is cut to maxCellsPerBase, over the 32"));
        }

        private static IEnumerable<TestCaseData> WorstCaseCases()
        {
            // 3 cells of its own family besides itself, plus 4 for each of 8 touching bases.
            yield return new TestCaseData("C", 4, 35).SetName("WorstCaseBandCounts_CentreAtFour_Is35");
            yield return new TestCaseData("SW", 4, 15).SetName("WorstCaseBandCounts_CornerAtFour_Is15");
            // E: 3 + 5 touching Dynamic bases x 4 + the Static C2 across the seam.
            yield return new TestCaseData("E", 4, 24).SetName("WorstCaseBandCounts_SeamCellAtFour_CountsTheStaticNeighbourOnce");
            // C2 (Static) beside the Dynamic E, NE and SE: every cell of each.
            yield return new TestCaseData("C2", 4, 12).SetName("WorstCaseBandCounts_StaticBesideDynamicFamilies_Is12");
            yield return new TestCaseData("C", 3, 26).SetName("WorstCaseBandCounts_CentreAtThree_Is26");
            // A limit above what a 200 m base holds in 100 m cells (4) counts the 4.
            yield return new TestCaseData("C", 9, 35).SetName("WorstCaseBandCounts_LimitAboveTheArea_CountsTheArea");
        }

        [TestCaseSource(nameof(WorstCaseCases))]
        public void WorstCaseBandCounts(string cellId, int perBase, int expected)
        {
            MeshWorldRegistry world = Rig(edit: w => w.maps[0].maxCellsPerBase = perBase);

            Assert.That(MeshDynamicSetupChecks.WorstCaseBandCounts(world)[cellId], Is.EqualTo(expected));
        }

        [Test]
        public void WorstCaseBandCounts_NoLimit_CountsWhatTheAreaHolds()
        {
            MeshWorldRegistry world = Rig(edit: w => w.dynamicSettings.defaultMaxCellsPerBase = 0);

            Assert.That(MeshDynamicSetupChecks.WorstCaseBandCounts(world)["C"], Is.EqualTo(35));
            Assert.That(MeshDynamicSetupChecks.MostCells(new MeshRect(0f, 0f, 200f, 200f), world.GetLayout(Map1), world.dynamicSettings, 80f), Is.EqualTo(4));
        }

        [Test]
        public void BandNeighbourFindings_ThreePerBase_IsClean()
        {
            Assert.That(MeshDynamicSetupChecks.BandNeighbourFindings(Rig(edit: w => w.maps[0].maxCellsPerBase = 3)), Is.Empty);
        }

        [Test]
        public void WorstCaseBandCounts_GapWithinTheBand_Counts()
        {
            MeshWorldRegistry near = Explicit(100f, 100f, ("A", -300f, 0f, 200f, 200f), ("B", 0f, 0f, 200f, 200f));
            MeshWorldRegistry far = Explicit(80f, 100f, ("A", -300f, 0f, 200f, 200f), ("B", 0f, 0f, 200f, 200f));

            Assert.That(MeshDynamicSetupChecks.WorstCaseBandCounts(near)["A"], Is.EqualTo(3 + 4), "a 100 m gap is within a 100 m band");
            Assert.That(MeshDynamicSetupChecks.WorstCaseBandCounts(far)["A"], Is.EqualTo(3), "and outside an 80 m one");
        }

        [Test]
        public void WorstCaseBandCounts_StaticWorld_IsTheTouchingCount()
        {
            MeshWorldRegistry world = Rig(dynamic: false);

            Assert.That(MeshDynamicSetupChecks.WorstCaseBandCounts(world)["C"], Is.EqualTo(8));
            Assert.That(MeshDynamicSetupChecks.BandNeighbourFindings(world), Is.Empty);
        }

        // ---------------------------------------------------------------- links with linkAllZones

        private MeshWorldRegistry BigGrid()
        {
            // Nine 400 m bases at a 100 m minimum and 8 per base: 72 cells, so a cell could link to 71 others.
            List<(string, float, float, float, float)> cells = new List<(string, float, float, float, float)>();
            for (int i = 0; i < 9; ++i)
                cells.Add(("G" + i, (i % 3) * 400f, (i / 3) * 400f, 400f, 400f));
            MeshWorldRegistry world = Explicit(80f, 100f, cells.ToArray());
            world.maps[0].maxCellsPerBase = 8;
            return world;
        }

        [Test]
        public void LinkFindings_MapThatCanPassSixtyFourCells_IsWarned()
        {
            MeshWorldRegistry world = BigGrid();

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.LinkFindings(world, linkAllZones: true);

            Assert.That(MeshDynamicSetupChecks.WorstCaseLinkCounts(world)["G4"], Is.EqualTo(71));
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].cellIds, Has.Count.EqualTo(9));
            Assert.That(findings[0].text, Does.Contain("could need up to 71 links, over the 64 its link server takes (turn onDemandLinks on for the map"));
        }

        [Test]
        public void LinkFindings_OnDemandMap_IsNotJudgedByTheAllZonesCount()
        {
            MeshWorldRegistry world = BigGrid();
            world.maps[0].onDemandLinks = true;

            Assert.That(MeshDynamicSetupChecks.LinkFindings(world, linkAllZones: true), Is.Empty);
            Assert.That(MeshDynamicSetupChecks.OnDemandLinks(world, Map1), Is.True);
        }

        [Test]
        public void LinkFindings_MixedWorld_JudgesOnlyTheAllZonesMap()
        {
            MeshWorldRegistry world = BigGrid();
            MeshCellRect[] rects = new MeshCellRect[9];
            for (int i = 0; i < 9; ++i)
                rects[i] = new MeshCellRect { id = "H" + i, min = new Vector2(1200f + (i % 3) * 400f, (i / 3) * 400f), size = new Vector2(400f, 400f) };
            world.maps.Add(new MeshMapLayout { mapId = Map2, cellRects = rects, cellMode = MeshCellMode.Dynamic, minCellSize = 100f, maxCellsPerBase = 8, onDemandLinks = true });
            world.Rebuild();

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.LinkFindings(world, linkAllZones: true);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].mapId, Is.EqualTo(Map1));
        }

        [Test]
        public void LinkFindings_WithoutLinkAllZones_IsClean()
        {
            Assert.That(MeshDynamicSetupChecks.LinkFindings(BigGrid(), linkAllZones: false), Is.Empty);
        }

        [Test]
        public void WorstCaseLinkCounts_Rig_CountsTheMapAndTheSeam()
        {
            MeshWorldRegistry world = Rig();

            Dictionary<string, int> links = MeshDynamicSetupChecks.WorstCaseLinkCounts(world);

            Assert.That(links["C"], Is.EqualTo(35), "36 cells of the map less itself");
            Assert.That(links["E"], Is.EqualTo(36), "and C2 across the seam");
            Assert.That(links["C2"], Is.EqualTo(12), "C2's own map holds only itself");
            Assert.That(MeshDynamicSetupChecks.LinkFindings(world, linkAllZones: true), Is.Empty);
        }

        // ---------------------------------------------------------------- servedCells and the children's host

        [Test]
        public void ServedCellsFindings_DynamicBasesLeftOut_AreWarned()
        {
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("A", new[] { "C", "N", "NE" })));

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].severity, Is.EqualTo(MeshSetupSeverity.Warning));
            Assert.That(findings[0].cellIds, Is.EqualTo(new[] { "SW", "S", "SE", "W", "E", "NW" }));
            Assert.That(findings[0].text, Does.StartWith("servedCells of A leave out the Dynamic base cell(s) SW, S, SE, W, E, NW of map Map1: a child, every supervised restart, a kill and the policy's host headroom go to the spawner that advertises the base, never to another host"));
            Assert.That(findings[0].text, Does.Contain("refuses their splits and restarts").And.Contain("run a map-spawn server there"));
        }

        [Test]
        public void ServedCellsFindings_EmptyListBesideAListingHost_IsWarned()
        {
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("A", new string[0]), ("B", new[] { "E" })));

            Assert.That(findings, Has.Count.EqualTo(1), "the bases B leaves out go to A's family host, warned once");
            Assert.That(findings[0].text, Does.StartWith("A: an empty servedCells starts every cell at boot, the cells B list too"));
            Assert.That(findings[0].text, Does.Contain("takes the family of every Dynamic base no list names"));
        }

        [Test]
        public void ServedCellsFindings_CellListedByTwoHosts_IsWarned()
        {
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("A", new[] { "SW", "S", "SE", "W", "C", "E" }), ("B", new[] { "E", "NW", "N", "NE", "C2" })));

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].cellIds, Is.EqualTo(new[] { "E" }));
            Assert.That(findings[0].text, Is.EqualTo("E is listed by more than one of A, B: each host starts it at boot (the cluster keeps the first) and its family goes to whichever spawner registered last: list each base on one host"));
        }

        [Test]
        public void ServedCellsFindings_ChildIdListed_IsWarned()
        {
            string[] served = { "SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C.1" };

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("Server", served)));

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].text, Is.EqualTo("Server: servedCells names C.1, which is no cell of the registry (a child starts on its base's spawner: list base cells only)"));
        }

        [Test]
        public void ServedCellsFindings_TwoHostsSplittingTheMap_AreJudgedTogether()
        {
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("A", new[] { "SW", "S", "SE", "W", "C" }), ("B", new[] { "E", "NW", "N", "NE", "C2" })));

            Assert.That(findings, Is.Empty);
        }

        [Test]
        public void ServedCellsFindings_EmptyLists_AreClean()
        {
            Assert.That(MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("Server", new string[0]))), Is.Empty);
            Assert.That(MeshDynamicSetupChecks.ServedCellsFindings(Rig(), Spawners(("Server", new[] { " ", "" }))), Is.Empty);
        }

        [Test]
        public void ServedCellsFindings_StaticWorld_IsNotJudged()
        {
            Assert.That(MeshDynamicSetupChecks.ServedCellsFindings(Rig(dynamic: false), Spawners(("Server", new[] { "C", "N" }))), Is.Empty);
        }

        // ---------------------------------------------------------------- the settings the central checks at start

        [Test]
        public void SettingsFindings_SupervisionGraceTooShort_IsAnError()
        {
            MeshWorldRegistry world = Rig(edit: w => w.dynamicSettings.childDownGraceSeconds = 5f);

            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.SettingsFindings(world);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].severity, Is.EqualTo(MeshSetupSeverity.Error));
            Assert.That(findings[0].text, Does.StartWith("Registry: dynamicSettings.childDownGraceSeconds 5 is under"));
        }

        [Test]
        public void SettingsFindings_PolicyOutOfRange_IsAnError()
        {
            MeshWorldRegistry world = Rig(edit: w => w.dynamicSettings.splitUtil = 1.5f);

            Assert.That(MeshDynamicSetupChecks.SettingsFindings(world)[0].text, Does.StartWith("Registry: dynamicSettings.splitUtil 1.5"));
        }

        [Test]
        public void SettingsFindings_StaticWorld_IsNotJudged()
        {
            Assert.That(MeshDynamicSetupChecks.SettingsFindings(Rig(dynamic: false, edit: w => w.dynamicSettings.childDownGraceSeconds = 5f)), Is.Empty);
        }

        [Test]
        public void Check_DefaultRig_FindsOnlyTheCentresBandCount()
        {
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.Check(Rig(), linkAllZones: true);

            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].cellIds, Is.EqualTo(new[] { "C" }));
        }

        [Test]
        public void EffectiveValues_FollowTheRuntime()
        {
            MeshDynamicSettings settings = new MeshDynamicSettings { defaultMinCellSize = 100f, defaultMaxCellsPerBase = 4 };

            Assert.That(MeshDynamicSetupChecks.EffectiveMinCellSize(new MeshMapLayout { minCellSize = 60f }, settings, 80f), Is.EqualTo(80f));
            Assert.That(MeshDynamicSetupChecks.EffectiveMinCellSize(new MeshMapLayout(), settings, 80f), Is.EqualTo(100f));
            Assert.That(MeshDynamicSetupChecks.EffectiveMaxCellsPerBase(new MeshMapLayout(), settings), Is.EqualTo(4));
            Assert.That(MeshDynamicSetupChecks.EffectiveMaxCellsPerBase(new MeshMapLayout { maxCellsPerBase = 2 }, settings), Is.EqualTo(2));
            Assert.That(MeshDynamicSetupChecks.AssetBand(new MeshDynamicSettings { bandWidth = -1f }), Is.EqualTo(80f));
        }
    }
}
