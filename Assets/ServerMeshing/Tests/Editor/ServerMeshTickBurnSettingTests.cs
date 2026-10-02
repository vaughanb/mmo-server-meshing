using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshTickBurnSettingTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseBurn_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseBurn_Empty_Fails");
            yield return new TestCaseData("x").SetName("TryParseBurn_Text_Fails");
            yield return new TestCaseData("0").SetName("TryParseBurn_Zero_Fails");
            yield return new TestCaseData("-5").SetName("TryParseBurn_Negative_Fails");
            yield return new TestCaseData("NaN").SetName("TryParseBurn_NaN_Fails");
            yield return new TestCaseData("Infinity").SetName("TryParseBurn_Infinity_Fails");
            yield return new TestCaseData("C:").SetName("TryParseBurn_CellWithoutMs_Fails");
            yield return new TestCaseData("C:x").SetName("TryParseBurn_CellWithText_Fails");
            yield return new TestCaseData("C:0").SetName("TryParseBurn_CellWithZero_Fails");
            yield return new TestCaseData("C:D:5").SetName("TryParseBurn_TwoColons_Fails");
            yield return new TestCaseData(":5").SetName("TryParseBurn_EmptyCell_Fails");
            yield return new TestCaseData(" :5").SetName("TryParseBurn_BlankCell_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseBurn_Invalid(string text)
        {
            bool parsed = ServerMeshTickBurn.TryParseBurn(text, out string cell, out float ms);

            Assert.That(parsed, Is.False);
            Assert.That(cell, Is.Null);
            Assert.That(ms, Is.EqualTo(0f));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("8", null, 8f).SetName("TryParseBurn_MsOnly_BurnsEveryCell");
            yield return new TestCaseData("C:8", "C", 8f).SetName("TryParseBurn_CellPrefix_BurnsThatCell");
            yield return new TestCaseData(" C : 2.5 ", "C", 2.5f).SetName("TryParseBurn_Whitespace_IsTrimmed");
            yield return new TestCaseData("c:0.5", "c", 0.5f).SetName("TryParseBurn_CellCaseIsKept");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParseBurn_Valid(string text, string expectedCell, float expectedMs)
        {
            bool parsed = ServerMeshTickBurn.TryParseBurn(text, out string cell, out float ms);

            Assert.That(parsed, Is.True);
            Assert.That(cell, Is.EqualTo(expectedCell));
            Assert.That(ms, Is.EqualTo(expectedMs));
        }
    }
}
