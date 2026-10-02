using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshPuppetMotionSettingTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParse_Null_Fails");
            yield return new TestCaseData("").SetName("TryParse_Empty_Fails");
            yield return new TestCaseData("   ").SetName("TryParse_Whitespace_Fails");
            yield return new TestCaseData("fast").SetName("TryParse_OtherWord_Fails");
            yield return new TestCaseData("1").SetName("TryParse_EnumNumber_Fails");
            yield return new TestCaseData("0").SetName("TryParse_ZeroNumber_Fails");
            yield return new TestCaseData("kinematics").SetName("TryParse_LongerWord_Fails");
            yield return new TestCaseData("steer").SetName("TryParse_ShorterWord_Fails");
            yield return new TestCaseData("kinematic,steered").SetName("TryParse_TwoWords_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParse_Invalid(string text)
        {
            bool parsed = MeshPuppetMotionSetting.TryParse(text, out MeshPuppetMotion motion);

            Assert.That(parsed, Is.False);
            Assert.That(motion, Is.EqualTo(MeshPuppetMotion.Steered));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("steered", MeshPuppetMotion.Steered).SetName("TryParse_Steered_IsSteered");
            yield return new TestCaseData("kinematic", MeshPuppetMotion.Kinematic).SetName("TryParse_Kinematic_IsKinematic");
            yield return new TestCaseData("KINEMATIC", MeshPuppetMotion.Kinematic).SetName("TryParse_UpperCase_IgnoresCase");
            yield return new TestCaseData("Steered", MeshPuppetMotion.Steered).SetName("TryParse_MixedCase_IgnoresCase");
            yield return new TestCaseData(" \tkinematic\n", MeshPuppetMotion.Kinematic).SetName("TryParse_SurroundingWhitespace_IsTrimmed");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParse_Valid(string text, MeshPuppetMotion expected)
        {
            bool parsed = MeshPuppetMotionSetting.TryParse(text, out MeshPuppetMotion motion);

            Assert.That(parsed, Is.True);
            Assert.That(motion, Is.EqualTo(expected));
        }

        [Test]
        public void EnvironmentKey_IsMeshPuppetMotion()
        {
            Assert.That(MeshPuppetMotionSetting.EnvironmentKey, Is.EqualTo("MESH_PUPPET_MOTION"));
        }

        [Test]
        public void DefaultMotion_IsSteered()
        {
            Assert.That(default(MeshPuppetMotion), Is.EqualTo(MeshPuppetMotion.Steered));
            Assert.That((byte)MeshPuppetMotion.Steered, Is.EqualTo(0));
            Assert.That((byte)MeshPuppetMotion.Kinematic, Is.EqualTo(1));
        }

        [Test]
        public void FallbackValues_AreInTheirDeclaredOrder()
        {
            Assert.That(default(MeshPuppetFallback), Is.EqualTo(MeshPuppetFallback.None));
            Assert.That(new[]
            {
                (byte)MeshPuppetFallback.None, (byte)MeshPuppetFallback.Mounted, (byte)MeshPuppetFallback.Summoned,
                (byte)MeshPuppetFallback.Handler, (byte)MeshPuppetFallback.Grid, (byte)MeshPuppetFallback.Install,
            }, Is.EqualTo(new byte[] { 0, 1, 2, 3, 4, 5 }));
        }
    }
}
