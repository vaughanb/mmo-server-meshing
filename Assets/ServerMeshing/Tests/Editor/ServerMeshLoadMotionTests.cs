using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshLoadMotionTests
    {
        private static IEnumerable<TestCaseData> InvalidMotionCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseMotion_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseMotion_Empty_Fails");
            yield return new TestCaseData("   ").SetName("TryParseMotion_Whitespace_Fails");
            yield return new TestCaseData("fast").SetName("TryParseMotion_UnknownWord_Fails");
            yield return new TestCaseData("1").SetName("TryParseMotion_One_Fails");
            yield return new TestCaseData("0").SetName("TryParseMotion_Zero_Fails");
            yield return new TestCaseData("steer").SetName("TryParseMotion_Prefix_Fails");
            yield return new TestCaseData("kinematic x").SetName("TryParseMotion_TrailingWord_Fails");
            yield return new TestCaseData("in ert").SetName("TryParseMotion_InnerSpace_Fails");
        }

        [TestCaseSource(nameof(InvalidMotionCases))]
        public void TryParseMotion_Invalid(string text)
        {
            bool parsed = ServerMeshLoadMotion.TryParseMotion(text, out ServerMeshLoadMotionKind motion);

            Assert.That(parsed, Is.False);
            Assert.That(motion, Is.EqualTo(ServerMeshLoadMotionKind.Steered));
        }

        private static IEnumerable<TestCaseData> ValidMotionCases()
        {
            yield return new TestCaseData("steered", ServerMeshLoadMotionKind.Steered).SetName("TryParseMotion_Steered");
            yield return new TestCaseData("kinematic", ServerMeshLoadMotionKind.Kinematic).SetName("TryParseMotion_Kinematic");
            yield return new TestCaseData("inert", ServerMeshLoadMotionKind.Inert).SetName("TryParseMotion_Inert");
            yield return new TestCaseData(" Kinematic ", ServerMeshLoadMotionKind.Kinematic).SetName("TryParseMotion_WhitespaceAndCase_AreIgnored");
            yield return new TestCaseData("INERT", ServerMeshLoadMotionKind.Inert).SetName("TryParseMotion_UpperCase_Parses");
            yield return new TestCaseData("Steered\t", ServerMeshLoadMotionKind.Steered).SetName("TryParseMotion_TrailingTab_IsTrimmed");
        }

        [TestCaseSource(nameof(ValidMotionCases))]
        public void TryParseMotion_Valid(string text, ServerMeshLoadMotionKind expected)
        {
            bool parsed = ServerMeshLoadMotion.TryParseMotion(text, out ServerMeshLoadMotionKind motion);

            Assert.That(parsed, Is.True);
            Assert.That(motion, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> InvalidFightCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseFight_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseFight_Empty_Fails");
            yield return new TestCaseData(" ").SetName("TryParseFight_Whitespace_Fails");
            yield return new TestCaseData("2").SetName("TryParseFight_Two_Fails");
            yield return new TestCaseData("-1").SetName("TryParseFight_Negative_Fails");
            yield return new TestCaseData("01").SetName("TryParseFight_LeadingZero_Fails");
            yield return new TestCaseData("yes").SetName("TryParseFight_Yes_Fails");
            yield return new TestCaseData("true").SetName("TryParseFight_True_Fails");
            yield return new TestCaseData("off").SetName("TryParseFight_Off_Fails");
        }

        [TestCaseSource(nameof(InvalidFightCases))]
        public void TryParseFight_Invalid(string text)
        {
            bool parsed = ServerMeshLoadMotion.TryParseFight(text, out bool fight);

            Assert.That(parsed, Is.False);
            Assert.That(fight, Is.True);
        }

        private static IEnumerable<TestCaseData> ValidFightCases()
        {
            yield return new TestCaseData("0", false).SetName("TryParseFight_Zero_IsOff");
            yield return new TestCaseData("1", true).SetName("TryParseFight_One_IsOn");
            yield return new TestCaseData(" 0 ", false).SetName("TryParseFight_Whitespace_IsTrimmed");
            yield return new TestCaseData("1\n", true).SetName("TryParseFight_TrailingNewline_IsTrimmed");
        }

        [TestCaseSource(nameof(ValidFightCases))]
        public void TryParseFight_Valid(string text, bool expected)
        {
            bool parsed = ServerMeshLoadMotion.TryParseFight(text, out bool fight);

            Assert.That(parsed, Is.True);
            Assert.That(fight, Is.EqualTo(expected));
        }

        [Test]
        public void ResolveMotion_BadValue_WarnsAndKeepsSteered()
        {
            ServerMeshLoadMotionKind motion = ServerMeshLoadMotion.ResolveMotion("fast", out string warning);

            Assert.That(motion, Is.EqualTo(ServerMeshLoadMotionKind.Steered));
            Assert.That(warning, Is.EqualTo("Load generator: MESH_LOAD_MOTION=fast is not steered, kinematic or inert; steered kept"));
        }

        [Test]
        public void ResolveMotion_BracedValue_WarnsWithoutBraces()
        {
            ServerMeshLoadMotion.ResolveMotion("{0}", out string warning);

            Assert.That(warning, Does.Not.Contain("{").And.Not.Contain("}"));
            Assert.That(warning, Does.Contain("MESH_LOAD_MOTION=(0)"));
        }

        private static IEnumerable<TestCaseData> ResolvedMotionCases()
        {
            yield return new TestCaseData(null, ServerMeshLoadMotionKind.Steered).SetName("ResolveMotion_Unset_IsSteeredWithoutWarning");
            yield return new TestCaseData("", ServerMeshLoadMotionKind.Steered).SetName("ResolveMotion_Empty_IsSteeredWithoutWarning");
            yield return new TestCaseData("kinematic", ServerMeshLoadMotionKind.Kinematic).SetName("ResolveMotion_Kinematic_WithoutWarning");
            yield return new TestCaseData(" inert ", ServerMeshLoadMotionKind.Inert).SetName("ResolveMotion_Inert_WithoutWarning");
        }

        [TestCaseSource(nameof(ResolvedMotionCases))]
        public void ResolveMotion_Resolves(string text, ServerMeshLoadMotionKind expected)
        {
            ServerMeshLoadMotionKind motion = ServerMeshLoadMotion.ResolveMotion(text, out string warning);

            Assert.That(motion, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [Test]
        public void ResolveFight_BadValue_WarnsAndKeepsFighting()
        {
            bool fight = ServerMeshLoadMotion.ResolveFight("yes", out string warning);

            Assert.That(fight, Is.True);
            Assert.That(warning, Is.EqualTo("Load generator: MESH_LOAD_FIGHT=yes is not 0 or 1; fighting kept on"));
        }

        [Test]
        public void ResolveFight_BracedValue_WarnsWithoutBraces()
        {
            ServerMeshLoadMotion.ResolveFight("{1}", out string warning);

            Assert.That(warning, Does.Not.Contain("{").And.Not.Contain("}"));
            Assert.That(warning, Does.Contain("MESH_LOAD_FIGHT=(1)"));
        }

        private static IEnumerable<TestCaseData> ResolvedFightCases()
        {
            yield return new TestCaseData(null, true).SetName("ResolveFight_Unset_IsOnWithoutWarning");
            yield return new TestCaseData("", true).SetName("ResolveFight_Empty_IsOnWithoutWarning");
            yield return new TestCaseData("0", false).SetName("ResolveFight_Zero_IsOffWithoutWarning");
            yield return new TestCaseData("1", true).SetName("ResolveFight_One_IsOnWithoutWarning");
        }

        [TestCaseSource(nameof(ResolvedFightCases))]
        public void ResolveFight_Resolves(string text, bool expected)
        {
            bool fight = ServerMeshLoadMotion.ResolveFight(text, out string warning);

            Assert.That(fight, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        private static IEnumerable<TestCaseData> SettingLineCases()
        {
            yield return new TestCaseData(ServerMeshLoadMotionKind.Kinematic, true, "Load generator: motion kinematic (MESH_LOAD_MOTION), fight on (MESH_LOAD_FIGHT)").SetName("SettingLine_Kinematic");
            yield return new TestCaseData(ServerMeshLoadMotionKind.Inert, true, "Load generator: motion inert (MESH_LOAD_MOTION), fight on (MESH_LOAD_FIGHT)").SetName("SettingLine_Inert");
            yield return new TestCaseData(ServerMeshLoadMotionKind.Steered, false, "Load generator: motion steered (MESH_LOAD_MOTION), fight off (MESH_LOAD_FIGHT)").SetName("SettingLine_SteeredWithoutFighting");
        }

        [TestCaseSource(nameof(SettingLineCases))]
        public void SettingLine(ServerMeshLoadMotionKind motion, bool fight, string expected)
        {
            Assert.That(ServerMeshLoadMotion.SettingLine(motion, fight), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> InvalidServerAnimationCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseEnabled_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseEnabled_Empty_Fails");
            yield return new TestCaseData("2").SetName("TryParseEnabled_Two_Fails");
            yield return new TestCaseData("10").SetName("TryParseEnabled_Ten_Fails");
            yield return new TestCaseData("on").SetName("TryParseEnabled_On_Fails");
            yield return new TestCaseData("true").SetName("TryParseEnabled_True_Fails");
        }

        [TestCaseSource(nameof(InvalidServerAnimationCases))]
        public void TryParseEnabled_Invalid(string text)
        {
            bool parsed = ServerMeshServerAnimation.TryParseEnabled(text, out bool enabled);

            Assert.That(parsed, Is.False);
            Assert.That(enabled, Is.False);
        }

        private static IEnumerable<TestCaseData> ValidServerAnimationCases()
        {
            yield return new TestCaseData("0", false).SetName("TryParseEnabled_Zero_IsOff");
            yield return new TestCaseData("1", true).SetName("TryParseEnabled_One_IsOn");
            yield return new TestCaseData(" 1 ", true).SetName("TryParseEnabled_Whitespace_IsTrimmed");
        }

        [TestCaseSource(nameof(ValidServerAnimationCases))]
        public void TryParseEnabled_Valid(string text, bool expected)
        {
            bool parsed = ServerMeshServerAnimation.TryParseEnabled(text, out bool enabled);

            Assert.That(parsed, Is.True);
            Assert.That(enabled, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> StepCases()
        {
            yield return new TestCaseData(new Vector3(9f, 1f, 5f), new Vector2(1f, 0f), 3f, new Vector3(8f, 1f, 5f), new Vector2(-1f, 0f)).SetName("StepWithin_PastMaxX_ReflectsAndTurnsBack");
            yield return new TestCaseData(new Vector3(1f, 0f, 5f), new Vector2(-1f, 0f), 3f, new Vector3(2f, 0f, 5f), new Vector2(1f, 0f)).SetName("StepWithin_PastMinX_ReflectsAndTurnsBack");
            yield return new TestCaseData(new Vector3(5f, 0f, 9f), new Vector2(0f, 1f), 2f, new Vector3(5f, 0f, 9f), new Vector2(0f, -1f)).SetName("StepWithin_PastMaxZ_ReflectsAndTurnsBack");
            yield return new TestCaseData(new Vector3(5f, 0f, 1f), new Vector2(0f, -1f), 2f, new Vector3(5f, 0f, 1f), new Vector2(0f, 1f)).SetName("StepWithin_PastMinZ_ReflectsAndTurnsBack");
            yield return new TestCaseData(new Vector3(9f, 2f, 9f), new Vector2(0.6f, 0.8f), 5f, new Vector3(8f, 2f, 7f), new Vector2(-0.6f, -0.8f)).SetName("StepWithin_PastACorner_ReflectsBothAxes");
            yield return new TestCaseData(new Vector3(-1f, 0f, 5f), new Vector2(1f, 0f), 0.5f, new Vector3(0.5f, 0f, 5f), new Vector2(1f, 0f)).SetName("StepWithin_OutsideHeadingIn_KeepsHeadingIn");
            yield return new TestCaseData(new Vector3(5f, 3f, 5f), new Vector2(1f, 0f), 2f, new Vector3(7f, 3f, 5f), new Vector2(1f, 0f)).SetName("StepWithin_Inside_MovesAlongTheHeading");
            yield return new TestCaseData(new Vector3(5f, 3f, 5f), new Vector2(0.6f, 0.8f), 0f, new Vector3(5f, 3f, 5f), new Vector2(0.6f, 0.8f)).SetName("StepWithin_ZeroDistance_StaysPut");
        }

        [TestCaseSource(nameof(StepCases))]
        public void StepWithin(Vector3 position, Vector2 heading, float distance, Vector3 expectedPosition, Vector2 expectedHeading)
        {
            Rect area = Rect.MinMaxRect(0f, 0f, 10f, 10f);

            Vector3 result = ServerMeshKinematicWalk.StepWithin(position, ref heading, distance, area);

            Assert.That(result.x, Is.EqualTo(expectedPosition.x).Within(1e-4f));
            Assert.That(result.y, Is.EqualTo(expectedPosition.y));
            Assert.That(result.z, Is.EqualTo(expectedPosition.z).Within(1e-4f));
            Assert.That(heading.x, Is.EqualTo(expectedHeading.x).Within(1e-6f));
            Assert.That(heading.y, Is.EqualTo(expectedHeading.y).Within(1e-6f));
        }

        [Test]
        public void StepWithin_AreaNarrowerThanTheStep_ClampsInside()
        {
            Vector2 heading = new Vector2(1f, 0f);

            Vector3 result = ServerMeshKinematicWalk.StepWithin(new Vector3(0.5f, 0f, 0.5f), ref heading, 5f, Rect.MinMaxRect(0f, 0f, 1f, 1f));

            Assert.That(result.x, Is.InRange(0f, 1f));
            Assert.That(heading.x, Is.EqualTo(-1f));
        }

        [Test, SetCulture("de-DE")]
        public void StartedLine_NegativeAndNaNValues_PrintAsZero()
        {
            string line = ServerMeshKinematicWalk.StartedLine(-1, -2, float.NaN);

            Assert.That(line, Is.EqualTo("Kinematic walk: 0 synthetic players walk by transform at 0.90 x move speed; 0 stay steered (no grounded kit movement within 0.0 s)"));
        }

        [Test, SetCulture("de-DE")]
        public void StartedLine_IsInvariant()
        {
            string line = ServerMeshKinematicWalk.StartedLine(298, 2, ServerMeshKinematicWalk.StartTimeoutSeconds);

            Assert.That(line, Is.EqualTo("Kinematic walk: 298 synthetic players walk by transform at 0.90 x move speed; 2 stay steered (no grounded kit movement within 10.0 s)"));
        }
    }
}
