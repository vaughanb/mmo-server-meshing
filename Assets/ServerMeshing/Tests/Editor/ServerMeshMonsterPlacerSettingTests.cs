using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshMonsterPlacerSettingTests
    {
        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("   ").SetName("TryParseSetting_Blank_Fails");
            yield return new TestCaseData("200").SetName("TryParseSetting_CountOnly_Fails");
            yield return new TestCaseData("200@0,-40").SetName("TryParseSetting_TwoNumbers_Fails");
            yield return new TestCaseData("200@0,-40,30,5").SetName("TryParseSetting_FourNumbers_Fails");
            yield return new TestCaseData("@0,-40,30").SetName("TryParseSetting_MissingCount_Fails");
            yield return new TestCaseData("200@").SetName("TryParseSetting_MissingDisc_Fails");
            yield return new TestCaseData("200@@0,-40,30").SetName("TryParseSetting_RepeatedAt_Fails");
            yield return new TestCaseData("0@0,-40,30").SetName("TryParseSetting_ZeroCount_Fails");
            yield return new TestCaseData("-5@0,-40,30").SetName("TryParseSetting_NegativeCount_Fails");
            yield return new TestCaseData("+5@0,-40,30").SetName("TryParseSetting_SignedCount_Fails");
            yield return new TestCaseData("2.5@0,-40,30").SetName("TryParseSetting_FractionalCount_Fails");
            yield return new TestCaseData("200@x,-40,30").SetName("TryParseSetting_TextCentre_Fails");
            yield return new TestCaseData("200@0,-40,NaN").SetName("TryParseSetting_NaNRadius_Fails");
            yield return new TestCaseData("200@0,Infinity,30").SetName("TryParseSetting_InfiniteCentre_Fails");
            yield return new TestCaseData("200@0,-40,0").SetName("TryParseSetting_ZeroRadius_Fails");
            yield return new TestCaseData("200@0,-40,-3").SetName("TryParseSetting_NegativeRadius_Fails");
            yield return new TestCaseData("200@0,-40,30:angry").SetName("TryParseSetting_UnknownSuffix_Fails");
            yield return new TestCaseData("200@0,-40,30:").SetName("TryParseSetting_EmptySuffix_Fails");
            yield return new TestCaseData("200@0,-40,30:passive:passive").SetName("TryParseSetting_RepeatedSuffix_Fails");
            yield return new TestCaseData("200@0,-40,30;").SetName("TryParseSetting_EmptyLastPart_Fails");
            yield return new TestCaseData("2001@0,0,10").SetName("TryParseSetting_OneDiscOverTheTotal_Fails");
            yield return new TestCaseData("1000@0,0,10;1001@50,0,10").SetName("TryParseSetting_PartsOverTheTotal_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshMonsterPlacer.TryParseSetting(text, out List<ServerMeshMonsterPlacer.ServerMeshMonsterDisc> discs, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(discs, Is.Null);
            StringAssert.Contains(ServerMeshMonsterPlacer.EnvironmentKey, error);
            AssertPrintable(error);
        }

        [Test]
        public void TryParseSetting_BadSecondPart_NamesThatPart()
        {
            ServerMeshMonsterPlacer.TryParseSetting("10@0,0,5;3@1,1,0", out _, out string error);

            StringAssert.Contains("the part 3@1,1,0", error);
        }

        [Test]
        public void TryParseSetting_OneDisc_Parses()
        {
            bool parsed = ServerMeshMonsterPlacer.TryParseSetting(" 200@0,-40,30 ", out List<ServerMeshMonsterPlacer.ServerMeshMonsterDisc> discs, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(discs.Count, Is.EqualTo(1));
            Assert.That(discs[0].Count, Is.EqualTo(200));
            Assert.That(discs[0].X, Is.EqualTo(0f));
            Assert.That(discs[0].Z, Is.EqualTo(-40f));
            Assert.That(discs[0].Radius, Is.EqualTo(30f));
            Assert.That(discs[0].Passive, Is.False);
            Assert.That(discs[0].Text, Is.EqualTo("200@0,-40,30"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void TryParseSetting_PassiveAndPlainParts_ParseInOrder()
        {
            bool parsed = ServerMeshMonsterPlacer.TryParseSetting("30@0.5, 0 ,50 : PASSIVE;1970@-100,25.25,12", out List<ServerMeshMonsterPlacer.ServerMeshMonsterDisc> discs, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(discs.Count, Is.EqualTo(2));
            Assert.That(discs[0].Count, Is.EqualTo(30));
            Assert.That(discs[0].X, Is.EqualTo(0.5f));
            Assert.That(discs[0].Passive, Is.True);
            Assert.That(discs[0].Text, Is.EqualTo("30@0.5,0,50:PASSIVE"));
            Assert.That(discs[1].Count, Is.EqualTo(1970));
            Assert.That(discs[1].Z, Is.EqualTo(25.25f));
            Assert.That(discs[1].Passive, Is.False);
        }

        private static IEnumerable<TestCaseData> InsideCases()
        {
            Rect cell = Rect.MinMaxRect(-100f, -100f, 100f, 100f);
            yield return new TestCaseData(cell, 95f, 0f, 10f, false).SetName("DiscInside_CrossesTheEastEdge_False");
            yield return new TestCaseData(cell, 0f, -95f, 10f, false).SetName("DiscInside_CrossesTheSouthEdge_False");
            yield return new TestCaseData(cell, 0f, -40f, 30f, true).SetName("DiscInside_WellInside_True");
            yield return new TestCaseData(cell, 90f, 90f, 10f, true).SetName("DiscInside_TouchingTwoEdges_True");
        }

        [TestCaseSource(nameof(InsideCases))]
        public void DiscInside(Rect cell, float x, float z, float r, bool expected)
        {
            ServerMeshMonsterPlacer.ServerMeshMonsterDisc disc = new ServerMeshMonsterPlacer.ServerMeshMonsterDisc { Count = 1, X = x, Z = z, Radius = r };

            Assert.That(ServerMeshMonsterPlacer.DiscInside(cell, disc), Is.EqualTo(expected));
        }

        [Test]
        public void DiscInside_NullDisc_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshMonsterPlacer.DiscInside(Rect.MinMaxRect(0f, 0f, 1f, 1f), null));
            Assert.That(ex.ParamName, Is.EqualTo("disc"));
        }

        [Test]
        public void DiscPoint_SameInputs_SamePointInsideTheDiscOnTheGround()
        {
            for (int k = 0; k < 500; ++k)
            {
                Vector3 a = ServerMeshMonsterPlacer.DiscPoint(12345, ServerMeshMonsterPlacer.Salt, 2, k, 10f, -40f, 30f);
                Vector3 b = ServerMeshMonsterPlacer.DiscPoint(12345, ServerMeshMonsterPlacer.Salt, 2, k, 10f, -40f, 30f);
                Assert.That(a, Is.EqualTo(b));
                Assert.That(Vector2.Distance(new Vector2(a.x, a.z), new Vector2(10f, -40f)), Is.LessThanOrEqualTo(30f + 1e-3f));
                Assert.That(a.y, Is.EqualTo(ServerMeshShipLoad.GroundY));
            }
        }

        [Test]
        public void DiscPoint_OtherSeedOrDisc_OtherPoint()
        {
            Vector3 a = ServerMeshMonsterPlacer.DiscPoint(12345, ServerMeshMonsterPlacer.Salt, 0, 0, 0f, 0f, 30f);

            Assert.That(ServerMeshMonsterPlacer.DiscPoint(12346, ServerMeshMonsterPlacer.Salt, 0, 0, 0f, 0f, 30f), Is.Not.EqualTo(a));
            Assert.That(ServerMeshMonsterPlacer.DiscPoint(12345, ServerMeshMonsterPlacer.Salt, 1, 0, 0f, 0f, 30f), Is.Not.EqualTo(a));
        }

        private static IEnumerable<TestCaseData> SeedCases()
        {
            yield return new TestCaseData("abc", false, ServerMeshLoadGenerator.DefaultSeed).SetName("TryParseSeed_Text_FailsWithTheDefault");
            yield return new TestCaseData("1.5", false, ServerMeshLoadGenerator.DefaultSeed).SetName("TryParseSeed_Fraction_FailsWithTheDefault");
            yield return new TestCaseData(null, true, ServerMeshLoadGenerator.DefaultSeed).SetName("TryParseSeed_Unset_IsTheDefault");
            yield return new TestCaseData(" ", true, ServerMeshLoadGenerator.DefaultSeed).SetName("TryParseSeed_Blank_IsTheDefault");
            yield return new TestCaseData(" -7 ", true, -7).SetName("TryParseSeed_Negative_Parses");
        }

        [TestCaseSource(nameof(SeedCases))]
        public void TryParseSeed(string text, bool valid, int expected)
        {
            Assert.That(ServerMeshMonsterPlacer.TryParseSeed(text, out int seed), Is.EqualTo(valid));
            Assert.That(seed, Is.EqualTo(expected));
        }

        [Test]
        public void TryReadSeed_Text_FailsNamingTheKeyAndText()
        {
            Assert.That(ServerMeshMonsterPlacer.TryReadSeed(" abc ", out int seed, out string error), Is.False);
            Assert.That(seed, Is.EqualTo(ServerMeshLoadGenerator.DefaultSeed));
            Assert.That(error, Is.EqualTo("MESH_LOAD_SEED=abc is not a whole number"));
            AssertPrintable(error);
        }

        [Test]
        public void TryReadSeed_Whole_ParsesWithNoError()
        {
            Assert.That(ServerMeshMonsterPlacer.TryReadSeed("42", out int seed, out string error), Is.True);
            Assert.That(seed, Is.EqualTo(42));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void ErrorLine_BadSeed_IsAMonstersError()
        {
            ServerMeshMonsterPlacer.TryReadSeed("1.5", out _, out string error);

            string line = ServerMeshMonsterPlacer.ErrorLine("C", error);

            Assert.That(line, Is.EqualTo("Monsters error: cell C: MESH_LOAD_SEED=1.5 is not a whole number; nothing placed"));
            AssertPrintable(line);
        }

        [Test]
        public void ErrorLine_NoCellOrReason_IsStillOneLine()
        {
            Assert.That(ServerMeshMonsterPlacer.ErrorLine(null, null), Is.EqualTo("Monsters error: cell -: -; nothing placed"));
        }

        [Test]
        public void SettingLine_ExactLine()
        {
            Assert.That(ServerMeshMonsterPlacer.SettingLine(200, "200@0,-40,30", "C"), Is.EqualTo("Monsters: placed 200 in 200@0,-40,30 on cell C"));
            Assert.That(ServerMeshMonsterPlacer.SettingLine(-1, "30@0,0,50:passive;70@10,10,5", null),
                Is.EqualTo("Monsters: placed 0 in 30@0,0,50:passive;70@10,10,5 on cell -"));
            Assert.That(ServerMeshMonsterPlacer.SettingLine(0, "", "E"), Is.EqualTo("Monsters: placed 0 in - on cell E"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_ExactLine()
        {
            string line = ServerMeshMonsterPlacer.WindowLine(5.0, "C", 200, 198, 190, 12, 2, 1);

            Assert.That(line, Is.EqualTo("Monsters window 5.00 s: cell C placed 200 alive 198 awake 190 healed 12 respawned 2 | gc0 1"));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshMonsterPlacer.WindowLine(double.NaN, null, -1, -1, -1, -1, -1, -3);

            Assert.That(line, Is.EqualTo("Monsters window 0.00 s: cell - placed 0 alive 0 awake 0 healed 0 respawned 0 | gc0 0"));
        }
    }
}
