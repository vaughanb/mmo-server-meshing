using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PrewarmKind = MultiplayerARPG.MMO.Mesh.ServerMeshPrewarm.ServerMeshPrewarmKind;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPrewarmSettingTests
    {
        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("NaN").And.Not.Contain("\n"));
        }

        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("kinematic").SetName("TryParseSetting_KindOnly_Fails");
            yield return new TestCaseData("kinematic:100").SetName("TryParseSetting_MissingTime_Fails");
            yield return new TestCaseData("kinematic:100@").SetName("TryParseSetting_EmptyTime_Fails");
            yield return new TestCaseData("kinematic:@30").SetName("TryParseSetting_MissingCount_Fails");
            yield return new TestCaseData(":100@30").SetName("TryParseSetting_MissingKind_Fails");
            yield return new TestCaseData("eye:100@30").SetName("TryParseSetting_UnknownKind_Fails");
            yield return new TestCaseData("character:100@30").SetName("TryParseSetting_CharacterKind_Fails");
            yield return new TestCaseData("kinematic:0@30").SetName("TryParseSetting_ZeroCount_Fails");
            yield return new TestCaseData("kinematic:10001@30").SetName("TryParseSetting_CountAboveMax_Fails");
            yield return new TestCaseData("kinematic:-5@30").SetName("TryParseSetting_NegativeCount_Fails");
            yield return new TestCaseData("ship:2.5@30").SetName("TryParseSetting_FractionalCount_Fails");
            yield return new TestCaseData("kinematic:100@-1").SetName("TryParseSetting_NegativeTime_Fails");
            yield return new TestCaseData("kinematic:100@soon").SetName("TryParseSetting_TextTime_Fails");
            yield return new TestCaseData("kinematic:100@Infinity").SetName("TryParseSetting_InfiniteTime_Fails");
            yield return new TestCaseData("kinematic:100@30/").SetName("TryParseSetting_EmptyPerFrame_Fails");
            yield return new TestCaseData("kinematic:100@30/0").SetName("TryParseSetting_ZeroPerFrame_Fails");
            yield return new TestCaseData("kinematic:100@30/101").SetName("TryParseSetting_PerFrameAboveCount_Fails");
            yield return new TestCaseData("kinematic:100@30/5/5").SetName("TryParseSetting_RepeatedPerFrame_Fails");
            yield return new TestCaseData("kinematic:100@30@40").SetName("TryParseSetting_RepeatedTime_Fails");
            yield return new TestCaseData("kinematic:100:5@30").SetName("TryParseSetting_RepeatedCount_Fails");
            yield return new TestCaseData("kinematic/5:100@30").SetName("TryParseSetting_PerFrameBeforeCount_Fails");
            yield return new TestCaseData("range:0@30").SetName("TryParseSetting_ZeroRange_Fails");
            yield return new TestCaseData("range:10000.5@30").SetName("TryParseSetting_RangeAboveMax_Fails");
            yield return new TestCaseData("range:NaN@30").SetName("TryParseSetting_NaNRange_Fails");
            yield return new TestCaseData("range:160@30/10").SetName("TryParseSetting_RangeWithPerFrame_Fails");
            yield return new TestCaseData("group:100@9.5").SetName("TryParseSetting_GroupBeforeItsLead_Fails");
            yield return new TestCaseData("group:100@0/10").SetName("TryParseSetting_PacedGroupAtZero_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshPrewarm.TryParseSetting(text, out PrewarmKind kind, out float amount, out float start, out int perFrame, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(kind, Is.EqualTo(PrewarmKind.Steered));
            Assert.That(amount, Is.EqualTo(0f));
            Assert.That(start, Is.EqualTo(0f));
            Assert.That(perFrame, Is.EqualTo(0));
            StringAssert.Contains(ServerMeshPrewarm.EnvironmentKey, error);
            // The error names the bad part as given, so an input of "NaN" is echoed; a computed NaN is still refused.
            AssertPrintable(text != null && text.Contains("NaN") ? error.Replace("NaN", "") : error);
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("kinematic:100@30", PrewarmKind.Kinematic, 100f, 30f, 0).SetName("TryParseSetting_AllAtOnce_Parses");
            yield return new TestCaseData(" Kinematic : 650 @ 40 / 50 ", PrewarmKind.Kinematic, 650f, 40f, 50).SetName("TryParseSetting_SpacesCaseAndPerFrame_Parse");
            yield return new TestCaseData("steered:10000@0.5/10000", PrewarmKind.Steered, 10000f, 0.5f, 10000).SetName("TryParseSetting_Limits_Parse");
            yield return new TestCaseData("SHIP:300@20", PrewarmKind.Ship, 300f, 20f, 0).SetName("TryParseSetting_Ship_Parses");
            yield return new TestCaseData("range:160@40", PrewarmKind.Range, 160f, 40f, 0).SetName("TryParseSetting_Range_Parses");
            yield return new TestCaseData("range:80.5@1e1", PrewarmKind.Range, 80.5f, 10f, 0).SetName("TryParseSetting_FractionalRangeAndExponentTime_Parse");
            yield return new TestCaseData("group:100@40/25", PrewarmKind.Group, 100f, 40f, 25).SetName("TryParseSetting_PacedGroup_Parses");
            yield return new TestCaseData("group:100@10", PrewarmKind.Group, 100f, 10f, 0).SetName("TryParseSetting_GroupAtItsLead_Parses");
            yield return new TestCaseData("kinematic:100@0", PrewarmKind.Kinematic, 100f, 0f, 0).SetName("TryParseSetting_GhostsAtZero_Parse");
        }

        [Test]
        public void TryParseSetting_GroupBeforeItsLead_NamesTheLead()
        {
            ServerMeshPrewarm.TryParseSetting("group:300@5", out _, out _, out _, out _, out string error);

            StringAssert.Contains("the time 5 is under the group's 10 s lead", error);
            AssertPrintable(error);
        }

        [TestCaseSource(nameof(ValidCases))]
        [SetCulture("de-DE")]
        public void TryParseSetting_Valid(string text, PrewarmKind expectedKind, float expectedAmount, float expectedStart, int expectedPerFrame)
        {
            bool parsed = ServerMeshPrewarm.TryParseSetting(text, out PrewarmKind kind, out float amount, out float start, out int perFrame, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(kind, Is.EqualTo(expectedKind));
            Assert.That(amount, Is.EqualTo(expectedAmount));
            Assert.That(start, Is.EqualTo(expectedStart));
            Assert.That(perFrame, Is.EqualTo(expectedPerFrame));
        }

        private static IEnumerable<TestCaseData> MotionCases()
        {
            yield return new TestCaseData(PrewarmKind.Kinematic, null, false).SetName("CheckPuppetMotion_KinematicWithMotionUnset_Fails");
            yield return new TestCaseData(PrewarmKind.Kinematic, "steered", false).SetName("CheckPuppetMotion_KinematicWithSteered_Fails");
            yield return new TestCaseData(PrewarmKind.Steered, "kinematic", false).SetName("CheckPuppetMotion_SteeredWithKinematic_Fails");
            yield return new TestCaseData(PrewarmKind.Steered, "wobbly", false).SetName("CheckPuppetMotion_SteeredWithAnUnreadableMotion_Fails");
            yield return new TestCaseData(PrewarmKind.Kinematic, " KINEMATIC ", true).SetName("CheckPuppetMotion_KinematicWithKinematic_Passes");
            yield return new TestCaseData(PrewarmKind.Steered, null, true).SetName("CheckPuppetMotion_SteeredWithMotionUnset_Passes");
            yield return new TestCaseData(PrewarmKind.Steered, " ", true).SetName("CheckPuppetMotion_SteeredWithMotionBlank_Passes");
            yield return new TestCaseData(PrewarmKind.Steered, "Steered", true).SetName("CheckPuppetMotion_SteeredWithSteered_Passes");
            yield return new TestCaseData(PrewarmKind.Ship, "kinematic", true).SetName("CheckPuppetMotion_ShipWithAnyMotion_Passes");
            yield return new TestCaseData(PrewarmKind.Range, "wobbly", true).SetName("CheckPuppetMotion_RangeWithAnyMotion_Passes");
            yield return new TestCaseData(PrewarmKind.Group, null, true).SetName("CheckPuppetMotion_GroupWithAnyMotion_Passes");
        }

        [TestCaseSource(nameof(MotionCases))]
        public void CheckPuppetMotion(PrewarmKind kind, string motion, bool expected)
        {
            bool passed = ServerMeshPrewarm.CheckPuppetMotion(kind, motion, out string error);

            Assert.That(passed, Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(error, Is.Null);
                return;
            }
            StringAssert.Contains(ServerMeshPrewarm.EnvironmentKey, error);
            StringAssert.Contains(MeshPuppetMotionSetting.EnvironmentKey, error);
            AssertPrintable(error);
        }

        private static IEnumerable<TestCaseData> MeshMotionCases()
        {
            yield return new TestCaseData(PrewarmKind.Kinematic, MeshPuppetMotion.Steered, false).SetName("CheckMeshPuppetMotion_KinematicOnASteeredMesh_Fails");
            yield return new TestCaseData(PrewarmKind.Steered, MeshPuppetMotion.Kinematic, false).SetName("CheckMeshPuppetMotion_SteeredOnAKinematicMesh_Fails");
            yield return new TestCaseData(PrewarmKind.Kinematic, MeshPuppetMotion.Kinematic, true).SetName("CheckMeshPuppetMotion_KinematicOnAKinematicMesh_Passes");
            yield return new TestCaseData(PrewarmKind.Steered, MeshPuppetMotion.Steered, true).SetName("CheckMeshPuppetMotion_SteeredOnASteeredMesh_Passes");
            yield return new TestCaseData(PrewarmKind.Ship, MeshPuppetMotion.Kinematic, true).SetName("CheckMeshPuppetMotion_ShipOnAnyMesh_Passes");
            yield return new TestCaseData(PrewarmKind.Group, MeshPuppetMotion.Kinematic, true).SetName("CheckMeshPuppetMotion_GroupOnAnyMesh_Passes");
        }

        [TestCaseSource(nameof(MeshMotionCases))]
        public void CheckMeshPuppetMotion(PrewarmKind kind, MeshPuppetMotion motion, bool expected)
        {
            bool passed = ServerMeshPrewarm.CheckMeshPuppetMotion(kind, motion, out string error);

            Assert.That(passed, Is.EqualTo(expected));
            if (expected)
            {
                Assert.That(error, Is.Null);
                return;
            }
            StringAssert.Contains(ServerMeshPrewarm.EnvironmentKey, error);
            StringAssert.Contains(MeshPuppetMotionSetting.EnvironmentKey, error);
            StringAssert.Contains("puppetMotion", error);
            AssertPrintable(error);
        }

        [TestCase(25f, 50f)]
        [TestCase(0f, 50f)]
        [TestCase(-5f, 50f)]
        [TestCase(float.NaN, 50f)]
        [TestCase(100f, 141.4214f)]
        public void EyesRadius_IsTheLoadSquaresCornerOrTheBurstDisc(float halfSize, float expected)
        {
            Assert.That(ServerMeshPrewarm.EyesRadius(halfSize), Is.EqualTo(expected).Within(1e-3f));
        }

        [Test]
        public void GroupViewRange_TakesTheLargerRangeOrInfinity()
        {
            Assert.That(ServerMeshPrewarm.GroupViewRange(80f, 0f, false), Is.EqualTo(80f));
            Assert.That(ServerMeshPrewarm.GroupViewRange(80f, 40f, false), Is.EqualTo(80f));
            Assert.That(ServerMeshPrewarm.GroupViewRange(80f, 200f, false), Is.EqualTo(200f));
            Assert.That(ServerMeshPrewarm.GroupViewRange(80f, 0f, true), Is.EqualTo(float.PositiveInfinity));
        }

        [Test]
        public void ParkOutOfView_MeasuresBetweenTheParkAndEyesDiscs()
        {
            Assert.That(ServerMeshPrewarm.ParkOutOfView(new Vector2(200f, 0f), Vector2.zero, 50f, 139f, out float gap), Is.True);
            Assert.That(gap, Is.EqualTo(140f).Within(1e-4f));
            Assert.That(ServerMeshPrewarm.ParkOutOfView(new Vector2(200f, 0f), Vector2.zero, 50f, 140f, out _), Is.False);
            Assert.That(ServerMeshPrewarm.ParkOutOfView(new Vector2(200f, 0f), Vector2.zero, 100f, 90f, out gap), Is.False);
            Assert.That(gap, Is.EqualTo(90f).Within(1e-4f));
            Assert.That(ServerMeshPrewarm.ParkOutOfView(new Vector2(9000f, 0f), Vector2.zero, 50f, float.PositiveInfinity, out _), Is.False);
        }

        private static IEnumerable<TestCaseData> BurstDiscCases()
        {
            Rect cell = Rect.MinMaxRect(-100f, -100f, 100f, 100f);
            yield return new TestCaseData(cell, new Vector2(60f, 0f), false).SetName("BurstDiscInside_CrossesTheEastEdge_False");
            yield return new TestCaseData(cell, new Vector2(0f, -51f), false).SetName("BurstDiscInside_CrossesTheSouthEdge_False");
            yield return new TestCaseData(cell, new Vector2(500f, 500f), false).SetName("BurstDiscInside_OutsideTheCell_False");
            yield return new TestCaseData(cell, Vector2.zero, true).SetName("BurstDiscInside_AtTheCentre_True");
            yield return new TestCaseData(cell, new Vector2(50f, -50f), true).SetName("BurstDiscInside_TouchingTwoEdges_True");
        }

        [TestCaseSource(nameof(BurstDiscCases))]
        public void BurstDiscInside(Rect cell, Vector2 centre, bool expected)
        {
            Assert.That(ServerMeshPrewarm.BurstDiscInside(cell, centre), Is.EqualTo(expected));
        }

        [Test]
        public void TryChooseParkCentre_WideEyes_FailsWhereTheBurstDiscAlonePasses()
        {
            Rect cell = Rect.MinMaxRect(-300f, -300f, 300f, 0f);
            Vector2 centre = new Vector2(-150f, -160f);

            Assert.That(ServerMeshPrewarm.TryChooseParkCentre(cell, centre, ServerMeshPrewarm.DiscRadiusMetres, 350f, out _, out string error), Is.True, error);
            Assert.That(ServerMeshPrewarm.TryChooseParkCentre(cell, centre, ServerMeshPrewarm.EyesRadius(100f), 350f, out _, out error), Is.False);
            StringAssert.Contains("eyes' disc (141.42 m about the burst centre)", error);
            StringAssert.Contains("350 m visible range", error);
            AssertPrintable(error);
        }

        [Test]
        public void TryChooseParkCentre_AlwaysVisible_FailsNamingIt()
        {
            bool chosen = ServerMeshPrewarm.TryChooseParkCentre(Rect.MinMaxRect(-3000f, -3000f, 3000f, 3000f), Vector2.zero, 50f, float.PositiveInfinity, out _, out string error);

            Assert.That(chosen, Is.False);
            StringAssert.Contains("unlimited (always visible) visible range", error);
            AssertPrintable(error);
        }

        [Test]
        public void TryChooseParkCentre_BurstCoversTheCell_FailsNamingTheRange()
        {
            bool chosen = ServerMeshPrewarm.TryChooseParkCentre(Rect.MinMaxRect(-100f, -100f, 100f, 100f), Vector2.zero, ServerMeshPrewarm.DiscRadiusMetres, 80f, out _, out string error);

            Assert.That(chosen, Is.False);
            StringAssert.Contains("80 m visible range", error);
            AssertPrintable(error);
        }

        [Test]
        public void TryChooseParkCentre_TinyCell_Fails()
        {
            Assert.That(ServerMeshPrewarm.TryChooseParkCentre(Rect.MinMaxRect(0f, 0f, 20f, 20f), Vector2.zero, ServerMeshPrewarm.DiscRadiusMetres, 1f, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryChooseParkCentre_LongCell_TakesTheFarthestInsetCorner()
        {
            bool chosen = ServerMeshPrewarm.TryChooseParkCentre(Rect.MinMaxRect(-300f, -300f, 300f, 0f), new Vector2(-150f, -160f), ServerMeshPrewarm.DiscRadiusMetres, 80f, out Vector2 park, out string error);

            Assert.That(chosen, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(park.x, Is.EqualTo(285f).Within(1e-4f));
            Assert.That(park.y, Is.EqualTo(-15f).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> AreaCases()
        {
            yield return new TestCaseData(null, false, 0f, 0f).SetName("TryReadAreaCentre_Unset_False");
            yield return new TestCaseData("1,2", false, 0f, 0f).SetName("TryReadAreaCentre_TwoNumbers_False");
            yield return new TestCaseData("1,2,0", false, 0f, 0f).SetName("TryReadAreaCentre_ZeroHalfSize_False");
            yield return new TestCaseData("1,x,5", false, 0f, 0f).SetName("TryReadAreaCentre_Text_False");
            yield return new TestCaseData(" 10.5, -4 ,25 ", true, 10.5f, -4f).SetName("TryReadAreaCentre_Valid_TheCentre");
        }

        [TestCaseSource(nameof(AreaCases))]
        [SetCulture("de-DE")]
        public void TryReadAreaCentre(string text, bool valid, float x, float z)
        {
            Assert.That(ServerMeshPrewarm.TryReadAreaCentre(text, out Vector2 centre), Is.EqualTo(valid));
            Assert.That(centre, Is.EqualTo(new Vector2(x, z)));
        }

        [Test]
        [SetCulture("de-DE")]
        public void TryReadArea_ReadsTheHalfSize()
        {
            Assert.That(ServerMeshPrewarm.TryReadArea(" 10.5, -4 ,25.5 ", out Vector2 centre, out float halfSize), Is.True);
            Assert.That(centre, Is.EqualTo(new Vector2(10.5f, -4f)));
            Assert.That(halfSize, Is.EqualTo(25.5f));
            Assert.That(ServerMeshPrewarm.TryReadArea("1,2,-3", out _, out halfSize), Is.False);
            Assert.That(halfSize, Is.EqualTo(0f));
        }

        [TestCase(PrewarmKind.Steered, "steered")]
        [TestCase(PrewarmKind.Kinematic, "kinematic")]
        [TestCase(PrewarmKind.Ship, "ship")]
        [TestCase(PrewarmKind.Range, "range")]
        [TestCase(PrewarmKind.Group, "group")]
        public void KindName_IsTheSettingWord(PrewarmKind kind, string expected)
        {
            Assert.That(ServerMeshPrewarm.KindName(kind), Is.EqualTo(expected));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SettingLine_ExactLines()
        {
            Assert.That(ServerMeshPrewarm.SettingLine(PrewarmKind.Kinematic, 650, 40f, 50, "C"), Is.EqualTo("Prewarm: kind kinematic n 650 at 40.0 s perFrame 50 on cell C"));
            Assert.That(ServerMeshPrewarm.SettingLine(PrewarmKind.Range, 160.25, 12.34f, 0, null), Is.EqualTo("Prewarm: kind range n 160.25 at 12.3 s perFrame 0 on cell -"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void BurstLine_Timed_ExactLine()
        {
            string line = ServerMeshPrewarm.BurstLine("C", PrewarmKind.Kinematic, 100, 0, 1, true, 45.5, 120, 3, 1000.0 / 60.0, 52.0041, 2);

            Assert.That(line, Is.EqualTo("Prewarm burst: cell C kind kinematic n 100 perFrame 0 frames 1 | spawn ms 45.500 per puppet 0.455"
                + " | after frames 120 over budget 3 budget 16.667 ms worst 52.004 ms | gc0 2"));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void BurstLine_RangeUncapped_ExactLine()
        {
            string line = ServerMeshPrewarm.BurstLine("E", PrewarmKind.Range, 160, 0, 1, false, 99.0, 240, 7, 0, 9.5, 0);

            Assert.That(line, Is.EqualTo("Prewarm burst: cell E kind range n 160 perFrame 0 frames 1 | spawn ms n/a per puppet n/a"
                + " | after frames 240 over budget 0 budget uncapped ms worst 9.500 ms | gc0 0"));
            AssertPrintable(line);
        }

        [Test]
        public void BurstBudgetMs_UncappedIsZeroEvenWithANominalRate()
        {
            Assert.That(ServerMeshPrewarm.BurstBudgetMs(0f, MeshWorkProbe.BudgetFor(0, 60)), Is.EqualTo(0.0));
            Assert.That(ServerMeshPrewarm.BurstBudgetMs(0f, 0f), Is.EqualTo(0.0));
            Assert.That(ServerMeshPrewarm.BurstBudgetMs(float.PositiveInfinity, 16f), Is.EqualTo(0.0));
            Assert.That(ServerMeshPrewarm.BurstBudgetMs(MeshWorkProbe.BudgetFor(30, 60), MeshWorkProbe.BudgetFor(30, 60)), Is.EqualTo(1000.0 / 30.0).Within(1e-4));
        }

        [Test]
        [SetCulture("de-DE")]
        public void BurstLine_UncappedWithANominalRate_PrintsUncappedAndNoFrameOver()
        {
            double budget = ServerMeshPrewarm.BurstBudgetMs(0f, MeshWorkProbe.BudgetFor(0, 60));
            string line = ServerMeshPrewarm.BurstLine("C", PrewarmKind.Steered, 300, 0, 1, true, 30.0, 120, 9, budget, 41.25, 1);

            Assert.That(line, Is.EqualTo("Prewarm burst: cell C kind steered n 300 perFrame 0 frames 1 | spawn ms 30.000 per puppet 0.100"
                + " | after frames 120 over budget 0 budget uncapped ms worst 41.250 ms | gc0 1"));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void BurstLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshPrewarm.BurstLine(null, PrewarmKind.Ship, 0, -1, -1, true, double.NaN, -1, -1, double.NaN, double.PositiveInfinity, -3);

            Assert.That(line, Is.EqualTo("Prewarm burst: cell - kind ship n 0 perFrame 0 frames 0 | spawn ms 0.000 per puppet 0.000"
                + " | after frames 0 over budget 0 budget uncapped ms worst 0.000 ms | gc0 0"));
            AssertPrintable(line);
        }
    }
}
