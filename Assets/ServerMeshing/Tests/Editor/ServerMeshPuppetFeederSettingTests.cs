using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPuppetFeederSettingTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("ship").SetName("TryParseSetting_KindOnly_Fails");
            yield return new TestCaseData("ship:200").SetName("TryParseSetting_MissingRate_Fails");
            yield return new TestCaseData("ship:@20").SetName("TryParseSetting_MissingCount_Fails");
            yield return new TestCaseData(":200@20").SetName("TryParseSetting_MissingKind_Fails");
            yield return new TestCaseData("ship:200@").SetName("TryParseSetting_EmptyRate_Fails");
            yield return new TestCaseData("eye:200@20").SetName("TryParseSetting_UnknownKind_Fails");
            yield return new TestCaseData("ship:x@20").SetName("TryParseSetting_TextCount_Fails");
            yield return new TestCaseData("ship:0@20").SetName("TryParseSetting_ZeroCount_Fails");
            yield return new TestCaseData("ship:-5@20").SetName("TryParseSetting_NegativeCount_Fails");
            yield return new TestCaseData("ship:2001@20").SetName("TryParseSetting_CountAboveMax_Fails");
            yield return new TestCaseData("ship:200@0").SetName("TryParseSetting_ZeroRate_Fails");
            yield return new TestCaseData("ship:200@61").SetName("TryParseSetting_RateAboveMax_Fails");
            yield return new TestCaseData("ship:200@2.5").SetName("TryParseSetting_FractionalRate_Fails");
            yield return new TestCaseData("ship:200@20@20").SetName("TryParseSetting_RepeatedRate_Fails");
            yield return new TestCaseData("ship:200:5@20").SetName("TryParseSetting_RepeatedCount_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshPuppetFeeder.TryParseSetting(text, out bool ships, out int count, out int hz, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(ships, Is.False);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(hz, Is.EqualTo(0));
            StringAssert.Contains(ServerMeshPuppetFeeder.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("ship:200@20", true, 200, 20).SetName("TryParseSetting_Ships_Parses");
            yield return new TestCaseData("character:200@20", false, 200, 20).SetName("TryParseSetting_Characters_Parses");
            yield return new TestCaseData(" Ship : 1 @ 60 ", true, 1, 60).SetName("TryParseSetting_SpacesAndCase_Parses");
            yield return new TestCaseData("CHARACTER:2000@1", false, 2000, 1).SetName("TryParseSetting_Limits_Parse");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParseSetting_Valid(string text, bool expectedShips, int expectedCount, int expectedHz)
        {
            bool parsed = ServerMeshPuppetFeeder.TryParseSetting(text, out bool ships, out int count, out int hz, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(ships, Is.EqualTo(expectedShips));
            Assert.That(count, Is.EqualTo(expectedCount));
            Assert.That(hz, Is.EqualTo(expectedHz));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void SettingLine_ExactLine()
        {
            Assert.That(ServerMeshPuppetFeeder.SettingLine(true, 200, 20, "C"), Is.EqualTo("Feeder: ship 200 @ 20 Hz on cell C"));
            Assert.That(ServerMeshPuppetFeeder.SettingLine(false, 5, 10, null), Is.EqualTo("Feeder: character 5 @ 10 Hz on cell -"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_ExactLine()
        {
            string line = ServerMeshPuppetFeeder.WindowLine(5.0, true, 200, 3, 20000, 1.5, 2.0, 2.5, 6.0, 12.0, 0);

            Assert.That(line, Is.EqualTo("Feeder window 5.00 s: ship 200 observed 3 | snapshots 20000 | ms/s encode 0.30 decode 0.40 accept 0.50 pose 1.20 total 2.40 | us/snapshot 0.600 | gc0 0"));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshPuppetFeeder.WindowLine(double.NaN, false, -1, -1, 0, double.NaN, -1.0, double.PositiveInfinity, 0, 0, -4);

            Assert.That(line, Is.EqualTo("Feeder window 0.00 s: character 0 observed 0 | snapshots 0 | ms/s encode 0.00 decode 0.00 accept 0.00 pose 0.00 total 0.00 | us/snapshot 0.000 | gc0 0"));
        }
    }
}
