using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshSpaceLayoutTests
    {
        private static readonly Rect Ground600 = Rect.MinMaxRect(-300f, -300f, 300f, 300f);

        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("  ").SetName("TryParseSetting_Blank_Fails");
            yield return new TestCaseData("0").SetName("TryParseSetting_Zero_Fails");
            yield return new TestCaseData("-4").SetName("TryParseSetting_Negative_Fails");
            yield return new TestCaseData("+4").SetName("TryParseSetting_Signed_Fails");
            yield return new TestCaseData("2.5").SetName("TryParseSetting_Fraction_Fails");
            yield return new TestCaseData("257").SetName("TryParseSetting_OverTheMost_Fails");
            yield return new TestCaseData("four").SetName("TryParseSetting_Text_Fails");
            yield return new TestCaseData(":grid").SetName("TryParseSetting_MissingCount_Fails");
            yield return new TestCaseData("4:").SetName("TryParseSetting_EmptyLayout_Fails");
            yield return new TestCaseData("4:ring").SetName("TryParseSetting_UnknownLayout_Fails");
            yield return new TestCaseData("4:grid:stack").SetName("TryParseSetting_TwoLayouts_Fails");
            yield return new TestCaseData("4 grid").SetName("TryParseSetting_NoColon_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshSpaceLayout.TryParseSetting(text, out int spaces, out ServerMeshSpaceArrangement layout, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(spaces, Is.EqualTo(0));
            Assert.That(layout, Is.EqualTo(ServerMeshSpaceArrangement.Stack));
            StringAssert.Contains(ServerMeshSpaceLayout.EnvironmentKey, error);
            AssertPrintable(error);
        }

        [TestCase("1", 1, ServerMeshSpaceArrangement.Stack)]
        [TestCase(" 64 ", 64, ServerMeshSpaceArrangement.Stack)]
        [TestCase("4:stack", 4, ServerMeshSpaceArrangement.Stack)]
        [TestCase("16 : GRID", 16, ServerMeshSpaceArrangement.Grid)]
        [TestCase("256:Stack", 256, ServerMeshSpaceArrangement.Stack)]
        public void TryParseSetting_Valid(string text, int expectedSpaces, ServerMeshSpaceArrangement expectedLayout)
        {
            bool parsed = ServerMeshSpaceLayout.TryParseSetting(text, out int spaces, out ServerMeshSpaceArrangement layout, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(spaces, Is.EqualTo(expectedSpaces));
            Assert.That(layout, Is.EqualTo(expectedLayout));
        }

        [TestCase(1, 1, 1)]
        [TestCase(2, 2, 1)]
        [TestCase(4, 2, 2)]
        [TestCase(5, 3, 2)]
        [TestCase(16, 4, 4)]
        [TestCase(17, 5, 4)]
        [TestCase(64, 8, 8)]
        public void GridShape_IsCeilSqrtByRows(int spaces, int expectedColumns, int expectedRows)
        {
            ServerMeshSpaceLayout.GridShape(spaces, out int columns, out int rows);

            Assert.That(columns, Is.EqualTo(expectedColumns));
            Assert.That(rows, Is.EqualTo(expectedRows));
        }

        [Test]
        public void TryCentres_Stack_PutsEverySpaceAtTheGroundCentre()
        {
            Rect ground = Rect.MinMaxRect(0f, -100f, 600f, 500f);

            bool built = ServerMeshSpaceLayout.TryCentres(64, ServerMeshSpaceArrangement.Stack, ground, out Vector2[] centres, out string error);

            Assert.That(built, Is.True, error);
            Assert.That(centres.Length, Is.EqualTo(64));
            foreach (Vector2 centre in centres)
                Assert.That(centre, Is.EqualTo(new Vector2(300f, 200f)));
        }

        [Test]
        public void TryCentres_Grid16_TilesThe600MetreGround()
        {
            bool built = ServerMeshSpaceLayout.TryCentres(16, ServerMeshSpaceArrangement.Grid, Ground600, out Vector2[] centres, out string error);

            Assert.That(built, Is.True, error);
            Assert.That(centres[0], Is.EqualTo(new Vector2(-225f, -225f)));
            Assert.That(centres[3], Is.EqualTo(new Vector2(225f, -225f)));
            Assert.That(centres[4], Is.EqualTo(new Vector2(-225f, -75f)));
            Assert.That(centres[15], Is.EqualTo(new Vector2(225f, 225f)));
        }

        [Test]
        public void TryCentres_Grid4_SitsAboutTheCentreAtThePitch()
        {
            bool built = ServerMeshSpaceLayout.TryCentres(4, ServerMeshSpaceArrangement.Grid, Ground600, out Vector2[] centres, out string error);

            Assert.That(built, Is.True, error);
            Assert.That(centres[0], Is.EqualTo(new Vector2(-75f, -75f)));
            Assert.That(centres[1], Is.EqualTo(new Vector2(75f, -75f)));
            Assert.That(centres[2], Is.EqualTo(new Vector2(-75f, 75f)));
            Assert.That(centres[3], Is.EqualTo(new Vector2(75f, 75f)));
        }

        [Test]
        public void TryCentres_GridTooBigForTheGround_Fails()
        {
            bool built = ServerMeshSpaceLayout.TryCentres(17, ServerMeshSpaceArrangement.Grid, Ground600, out Vector2[] centres, out string error);

            Assert.That(built, Is.False);
            Assert.That(centres, Is.Null);
            StringAssert.Contains("5 by 4", error);
            AssertPrintable(error);
        }

        [Test]
        public void TryCentres_EmptyGround_Fails()
        {
            bool built = ServerMeshSpaceLayout.TryCentres(1, ServerMeshSpaceArrangement.Stack, Rect.MinMaxRect(0f, 0f, 0f, 10f), out _, out string error);

            Assert.That(built, Is.False);
            AssertPrintable(error);
        }

        [Test]
        public void SpaceOf_Stack_IsTheOrdinalModS()
        {
            Vector2[] centres = new Vector2[4];

            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, centres, 0, Vector2.zero), Is.EqualTo(0));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, centres, 5, new Vector2(999f, 999f)), Is.EqualTo(1));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, centres, 103, Vector2.zero), Is.EqualTo(3));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, centres, -6, Vector2.zero), Is.EqualTo(2));
        }

        [Test]
        public void SpaceOf_Stack_SplitsAPopulationEvenly()
        {
            Vector2[] centres = new Vector2[16];
            int[] counts = new int[16];
            for (int k = 0; k < 96; ++k)
                counts[ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, centres, k, Vector2.zero)]++;

            foreach (int count in counts)
                Assert.That(count, Is.EqualTo(6));
        }

        [Test]
        public void SpaceOf_Grid_IsTheSquareThePointStandsIn()
        {
            ServerMeshSpaceLayout.TryCentres(16, ServerMeshSpaceArrangement.Grid, Ground600, out Vector2[] centres, out _);

            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Grid, centres, 7, new Vector2(-290f, -290f)), Is.EqualTo(0));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Grid, centres, 0, new Vector2(10f, 140f)), Is.EqualTo(10));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Grid, centres, 0, new Vector2(299f, 299f)), Is.EqualTo(15));
        }

        [Test]
        public void SpaceOf_GridTie_GoesToTheLowerIndex()
        {
            ServerMeshSpaceLayout.TryCentres(4, ServerMeshSpaceArrangement.Grid, Ground600, out Vector2[] centres, out _);

            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Grid, centres, 0, Vector2.zero), Is.EqualTo(0));
        }

        [Test]
        public void SpaceOf_NoCentres_IsMinusOne()
        {
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Stack, null, 3, Vector2.zero), Is.EqualTo(-1));
            Assert.That(ServerMeshSpaceLayout.SpaceOf(ServerMeshSpaceArrangement.Grid, new Vector2[0], 3, Vector2.zero), Is.EqualTo(-1));
        }

        [Test]
        public void ContentPoint_FivePointsOnTheRing()
        {
            Vector2 centre = new Vector2(10f, -20f);
            Assert.That(ServerMeshSpaceLayout.ContentPoint(centre, 0).x, Is.EqualTo(10f + ServerMeshSpaceLayout.ContentRingMetres).Within(1e-4f));
            for (int j = 0; j < ServerMeshSpaceLayout.ContentPerSpace; ++j)
            {
                Vector2 p = ServerMeshSpaceLayout.ContentPoint(centre, j);
                Assert.That(Vector2.Distance(p, centre), Is.EqualTo(ServerMeshSpaceLayout.ContentRingMetres).Within(1e-4f));
                for (int i = 0; i < j; ++i)
                    Assert.That(Vector2.Distance(p, ServerMeshSpaceLayout.ContentPoint(centre, i)), Is.GreaterThan(1f));
            }
        }

        [Test]
        public void SpaceId_IsSpaceColonIndex()
        {
            Assert.That(ServerMeshSpaceLayout.SpaceId(0), Is.EqualTo("space:0"));
            Assert.That(ServerMeshSpaceLayout.SpaceId(63), Is.EqualTo("space:63"));
            Assert.That(ServerMeshSpaceLayout.IsSpaceId("space:3"), Is.True);
            Assert.That(ServerMeshSpaceLayout.IsSpaceId(string.Empty), Is.False);
            Assert.That(ServerMeshSpaceLayout.IsSpaceId(null), Is.False);
            Assert.That(ServerMeshSpaceLayout.IsSpaceId("dungeon:3"), Is.False);
        }

        [Test]
        public void PerSpace_IsTheShare()
        {
            Assert.That(ServerMeshSpaceLayout.PerSpace(64, 64), Is.EqualTo(1.0));
            Assert.That(ServerMeshSpaceLayout.PerSpace(100, 16), Is.EqualTo(6.25));
            Assert.That(ServerMeshSpaceLayout.PerSpace(5, 0), Is.EqualTo(0.0));
            Assert.That(ServerMeshSpaceLayout.PerSpace(-5, 4), Is.EqualTo(0.0));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SettingLine_StackAndGrid()
        {
            string stack = ServerMeshSpaceLayout.SettingLine(4, ServerMeshSpaceArrangement.Stack, 1.0, 5);
            string grid = ServerMeshSpaceLayout.SettingLine(16, ServerMeshSpaceArrangement.Grid, 6.25, 5);

            Assert.That(stack, Is.EqualTo("Spaces: S 4 layout stack pitch 0 m | per space observers 1 content 5"));
            Assert.That(grid, Is.EqualTo("Spaces: S 16 layout grid pitch 150 m | per space observers 6.25 content 5"));
            AssertPrintable(stack);
            AssertPrintable(grid);
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_IsInvariantAndClamped()
        {
            string line = ServerMeshSpaceLayout.WindowLine(5.004, 4, 7.5, 0);
            string bad = ServerMeshSpaceLayout.WindowLine(double.NaN, -1, double.PositiveInfinity, -3);

            Assert.That(line, Is.EqualTo("Spaces window 5.00 s: observers 4 | own seen mean 7.50 | other seen max 0"));
            Assert.That(bad, Is.EqualTo("Spaces window 0.00 s: observers 0 | own seen mean 0.00 | other seen max 0"));
            AssertPrintable(line);
        }
    }
}
