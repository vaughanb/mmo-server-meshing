using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomLayoutTests
    {
        private static readonly Rect Coarse = new Rect(-300f, -300f, 600f, 600f);
        private static readonly Rect GridC = new Rect(-100f, -100f, 200f, 200f);
        private const float Band = 80f;

        private static ServerMeshPhantomGroup[] Build(int phantoms, int view, float range, Rect area, bool bands, out string error, int seed = 12345, bool eyes = false)
        {
            return ServerMeshPhantomLayout.Build(phantoms, view, range, area, Band, bands, bands, bands, bands, seed, eyes, out error);
        }

        private static float RectDistance(Rect a, Rect b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(b.xMin - a.xMax, a.xMin - b.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(b.yMin - a.yMax, a.yMin - b.yMax));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static bool Inside(Rect outer, Rect inner, float slack = 1e-3f)
        {
            return inner.xMin >= outer.xMin - slack && inner.xMax <= outer.xMax + slack && inner.yMin >= outer.yMin - slack && inner.yMax <= outer.yMax + slack;
        }

        private static bool Inside(Rect outer, Vector2 point) => point.x >= outer.xMin && point.x <= outer.xMax && point.y >= outer.yMin && point.y <= outer.yMax;

        private static IEnumerable<TestCaseData> FailureCases()
        {
            yield return new TestCaseData(0, 100, 80f, Coarse, false, "at least 1 phantom").SetName("Build_NoPhantoms_Errors");
            yield return new TestCaseData(-3, 100, 80f, Coarse, false, "at least 1 phantom").SetName("Build_NegativePhantoms_Errors");
            yield return new TestCaseData(10, 0, 80f, Coarse, false, "V of at least 1").SetName("Build_ViewZeroForCharacters_Errors");
            yield return new TestCaseData(10, -1, 80f, Coarse, false, "V of at least 1").SetName("Build_NegativeView_Errors");
            yield return new TestCaseData(10, 9, 2f, Coarse, false, "cannot hold").SetName("Build_RangeTooSmallForADisc_Errors");
            yield return new TestCaseData(10, 9, float.NaN, Coarse, false, "cannot hold").SetName("Build_NaNRange_Errors");
            yield return new TestCaseData(10, 9, 80f, new Rect(0f, 0f, 0f, 10f), false, "empty").SetName("Build_EmptyArea_Errors");
            yield return new TestCaseData(200, 99, 80f, GridC, true, "needs 2 groups, 1 fit").SetName("Build_GridCWithFourBandEdges_TwoGroupsDoNotFit_NamesTheCounts");
            yield return new TestCaseData(400, 1, 80f, Coarse, false, "needs 200 groups, 64 fit").SetName("Build_CoarseTwoHundredGroups_NamesTheSixtyFourThatFit");
            yield return new TestCaseData(10, 9, 80f, new Rect(0f, 0f, 1f, 1f), false, "needs 1 groups, 0 fit").SetName("Build_AreaSmallerThanADisc_NamesNoneFit");
        }

        [TestCaseSource(nameof(FailureCases))]
        public void Build_Failure(int phantoms, int view, float range, Rect area, bool bands, string message)
        {
            ServerMeshPhantomGroup[] groups = Build(phantoms, view, range, area, bands, out string error);

            Assert.That(groups, Is.Null);
            Assert.That(error, Does.Contain(message));
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        private static IEnumerable<TestCaseData> CountCases()
        {
            yield return new TestCaseData(400, 100, 4, new[] { 100, 100, 100, 100 }, new[] { 1, 1, 1, 1 }).SetName("Build_400At100_FourGroupsOfOneHundredPlusOneFiller");
            yield return new TestCaseData(50, 500, 1, new[] { 50 }, new[] { 451 }).SetName("Build_50At500_OneGroupOfFiftyPlus451Filler");
            yield return new TestCaseData(100, 300, 1, new[] { 100 }, new[] { 201 }).SetName("Build_100At300_OneGroupOfOneHundredPlus201Filler");
            yield return new TestCaseData(150, 100, 2, new[] { 75, 75 }, new[] { 26, 26 }).SetName("Build_150At100_TwoGroupsOf75Plus26Filler");
            yield return new TestCaseData(7, 2, 3, new[] { 3, 2, 2 }, new[] { 0, 1, 1 }).SetName("Build_Remainder_SpreadsEvenly_FillerNeverBelowZero");
        }

        [TestCaseSource(nameof(CountCases))]
        public void Build_Counts(int phantoms, int view, int groupCount, int[] phantomCounts, int[] fillerCounts)
        {
            ServerMeshPhantomGroup[] groups = Build(phantoms, view, 80f, Coarse, false, out string error);

            Assert.That(error, Is.Null);
            Assert.That(groups.Length, Is.EqualTo(groupCount));
            for (int g = 0; g < groups.Length; ++g)
            {
                Assert.That(groups[g].PhantomCount, Is.EqualTo(phantomCounts[g]));
                Assert.That(groups[g].FillerCount, Is.EqualTo(fillerCounts[g]));
                Assert.That(groups[g].PhantomCount + groups[g].FillerCount - 1, Is.GreaterThanOrEqualTo(view));
            }
        }

        private static IEnumerable<TestCaseData> RadiusCases()
        {
            yield return new TestCaseData(400, 24, 80f, Coarse, false, 16, 39.5f).SetName("Build_CoarseR80_SixteenGroupsAtTheLargestRadius");
            yield return new TestCaseData(400, 23, 80f, Coarse, false, 17, 27.59f).SetName("Build_CoarseR80_SeventeenGroupsShrinkTheRadius");
            yield return new TestCaseData(100, 99, 80f, GridC, true, 1, 19f).SetName("Build_GridCFourBandEdges_OneGroupAtNineteenMetres");
            yield return new TestCaseData(400, 100, 300f, Coarse, false, 4, 74.74f).SetName("Build_CoarseR300_FourGroupsAt74_74");
        }

        [TestCaseSource(nameof(RadiusCases))]
        public void Build_Radius(int phantoms, int view, float range, Rect area, bool bands, int groupCount, float radius)
        {
            ServerMeshPhantomGroup[] groups = Build(phantoms, view, range, area, bands, out string error);

            Assert.That(error, Is.Null);
            Assert.That(groups.Length, Is.EqualTo(groupCount));
            foreach (ServerMeshPhantomGroup group in groups)
                Assert.That(group.Radius, Is.EqualTo(radius).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> GeometryCases()
        {
            yield return new TestCaseData(400, 24, 80f, Coarse, false).SetName("Geometry_CoarseSixteenGroups");
            yield return new TestCaseData(400, 23, 80f, Coarse, false).SetName("Geometry_CoarseSeventeenGroups");
            yield return new TestCaseData(400, 100, 300f, Coarse, false).SetName("Geometry_CoarseR300FourGroups");
            yield return new TestCaseData(100, 99, 80f, GridC, true).SetName("Geometry_GridCWithBands");
            yield return new TestCaseData(30, 4, 50f, new Rect(0f, 0f, 400f, 300f), false).SetName("Geometry_RectangularAreaSixGroups");
        }

        [TestCaseSource(nameof(GeometryCases))]
        public void Build_Geometry(int phantoms, int view, float range, Rect area, bool bands)
        {
            ServerMeshPhantomGroup[] groups = Build(phantoms, view, range, area, bands, out string error);
            float edge = bands ? Mathf.Max(Band, range) + 1f : 0f;
            Rect shrunk = Rect.MinMaxRect(area.xMin + edge, area.yMin + edge, area.xMax - edge, area.yMax - edge);

            Assert.That(error, Is.Null);
            for (int g = 0; g < groups.Length; ++g)
            {
                ServerMeshPhantomGroup group = groups[g];
                Rect region = group.Region;
                Vector2[] corners = { new Vector2(region.xMin, region.yMin), new Vector2(region.xMax, region.yMin), new Vector2(region.xMin, region.yMax), new Vector2(region.xMax, region.yMax) };
                foreach (Vector2 a in corners)
                {
                    Assert.That(Vector2.Distance(a, group.Centre), Is.LessThanOrEqualTo(group.Radius + 1e-3f));
                    foreach (Vector2 b in corners)
                        Assert.That(Vector2.Distance(a, b), Is.LessThanOrEqualTo(range - 1f + 1e-3f));
                }
                Assert.That(Inside(shrunk, Rect.MinMaxRect(group.Centre.x - group.Radius, group.Centre.y - group.Radius, group.Centre.x + group.Radius, group.Centre.y + group.Radius)), Is.True, "disc " + g);
                Assert.That(Inside(shrunk, region), Is.True, "region " + g);
                foreach (Vector2 p in group.PhantomPositions)
                    Assert.That(Inside(region, p), Is.True);
                foreach (Vector2 p in group.FillerPositions)
                    Assert.That(Inside(region, p), Is.True);
                for (int h = g + 1; h < groups.Length; ++h)
                    Assert.That(RectDistance(region, groups[h].Region), Is.GreaterThan(range + 1f), "groups " + g + " and " + h);
            }
        }

        [Test]
        public void Build_KeepOut_NoPlaceLeftGivesAnError()
        {
            Vector2[] everywhere = { new Vector2(0f, 0f) };
            ServerMeshPhantomGroup[] groups = ServerMeshPhantomLayout.Build(10, 100, 80f, GridC, Band, false, false, false, false, 12345, false, everywhere, 200f, out string error);

            Assert.That(groups, Is.Null);
            Assert.That(error, Does.Contain("keep-out"));
        }

        [Test]
        public void Build_KeepOut_EveryGroupStaysClearOfTheTestMapsWarpPortals()
        {
            Vector2[] portals = { new Vector2(200f, 0f), new Vector2(-200f, 0f), new Vector2(0f, 200f), new Vector2(0f, -200f),
                new Vector2(200f, 200f), new Vector2(-200f, -200f), new Vector2(200f, -200f), new Vector2(-200f, 200f) };
            ServerMeshPhantomGroup[] free = Build(200, 100, 80f, Coarse, false, out _);
            ServerMeshPhantomGroup[] kept = ServerMeshPhantomLayout.Build(200, 100, 80f, Coarse, Band, false, false, false, false, 12345, false, portals, 10f, out string error);

            Assert.That(error, Is.Null);
            Assert.That(kept.Length, Is.EqualTo(free.Length));
            foreach (ServerMeshPhantomGroup group in kept)
            {
                foreach (Vector2 portal in portals)
                    Assert.That(Vector2.Distance(group.Centre, portal), Is.GreaterThanOrEqualTo(group.Radius + 10f - 1e-3f));
            }
            for (int a = 0; a < kept.Length; ++a)
            {
                for (int b = a + 1; b < kept.Length; ++b)
                    Assert.That(RectDistance(kept[a].Region, kept[b].Region), Is.GreaterThan(80f));
            }
        }

        [Test]
        public void Build_SameSeed_SameLayout_DifferentSeed_DifferentPositions()
        {
            ServerMeshPhantomGroup[] a = Build(150, 100, 80f, Coarse, false, out _, 7);
            ServerMeshPhantomGroup[] b = Build(150, 100, 80f, Coarse, false, out _, 7);
            ServerMeshPhantomGroup[] c = Build(150, 100, 80f, Coarse, false, out _, 8);

            Assert.That(a.Length, Is.EqualTo(b.Length));
            bool differs = false;
            for (int g = 0; g < a.Length; ++g)
            {
                Assert.That(a[g].Centre, Is.EqualTo(b[g].Centre));
                Assert.That(a[g].PhantomPositions, Is.EqualTo(b[g].PhantomPositions));
                Assert.That(a[g].FillerPositions, Is.EqualTo(b[g].FillerPositions));
                for (int i = 0; i < a[g].PhantomCount; ++i)
                    differs |= a[g].PhantomPositions[i] != c[g].PhantomPositions[i];
            }
            Assert.That(differs, Is.True);
        }

        [Test]
        public void Build_Eyes_OneClusterOfViewFillerAtTheAreaCentre_HoldingEveryEye()
        {
            ServerMeshPhantomGroup[] groups = Build(50, 200, 80f, Coarse, false, out string error, eyes: true);

            Assert.That(error, Is.Null);
            Assert.That(groups.Length, Is.EqualTo(1));
            Assert.That(groups[0].Centre, Is.EqualTo(Coarse.center));
            Assert.That(groups[0].PhantomCount, Is.EqualTo(50));
            Assert.That(groups[0].FillerCount, Is.EqualTo(200));
            foreach (Vector2 p in groups[0].PhantomPositions)
                Assert.That(Inside(groups[0].Region, p), Is.True);
            Assert.That(groups[0].Line(), Is.EqualTo("Phantom group 0: centre 0.0 0.0 side 55.9 phantoms 50 filler 200"));
        }

        [Test]
        public void Build_EyesAtViewZero_NoGroupsAndNoError()
        {
            ServerMeshPhantomGroup[] groups = Build(400, 0, 80f, Coarse, false, out string error, eyes: true);

            Assert.That(error, Is.Null);
            Assert.That(groups, Is.Empty);
        }

        private static IEnumerable<TestCaseData> AreaCases()
        {
            yield return new TestCaseData(false, new Vector2(10f, -20f), new Vector2(30f, 40f), 0f).SetName("PlaceInArea_Rect_StaysInside");
            yield return new TestCaseData(true, new Vector2(-5f, 5f), Vector2.zero, 25f).SetName("PlaceInArea_Disc_StaysInside");
        }

        [TestCaseSource(nameof(AreaCases))]
        public void PlaceInArea(bool disc, Vector2 centre, Vector2 size, float radius)
        {
            Vector2[] positions = ServerMeshPhantomLayout.PlaceInArea(500, disc, centre, size, radius, 99);
            Rect rect = new Rect(centre - size * 0.5f, size);

            Assert.That(positions.Length, Is.EqualTo(500));
            foreach (Vector2 p in positions)
            {
                if (disc)
                    Assert.That(Vector2.Distance(p, centre), Is.LessThanOrEqualTo(radius + 1e-3f));
                else
                    Assert.That(Inside(rect, p), Is.True);
            }
            Assert.That(ServerMeshPhantomLayout.PlaceInArea(500, disc, centre, size, radius, 99), Is.EqualTo(positions));
        }

        [Test]
        public void GroupCount_CharacterAndEyeRules()
        {
            Assert.That(ServerMeshPhantomLayout.GroupCount(400, 100, false), Is.EqualTo(4));
            Assert.That(ServerMeshPhantomLayout.GroupCount(101, 100, false), Is.EqualTo(1));
            Assert.That(ServerMeshPhantomLayout.GroupCount(102, 100, false), Is.EqualTo(2));
            Assert.That(ServerMeshPhantomLayout.GroupCount(400, 100, true), Is.EqualTo(1));
            Assert.That(ServerMeshPhantomLayout.GroupCount(400, 0, true), Is.EqualTo(0));
            Assert.That(ServerMeshPhantomLayout.GroupCount(0, 10, false), Is.EqualTo(0));
            Assert.That(ServerMeshPhantomLayout.InscribedSquare(Vector2.zero, 10f).width, Is.EqualTo(10f * Mathf.Sqrt(2f)).Within(1e-3f));
        }
    }
}
