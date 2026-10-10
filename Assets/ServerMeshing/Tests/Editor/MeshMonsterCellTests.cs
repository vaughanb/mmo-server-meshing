using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshMonsterCellTests
    {
        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData("C E").SetName("TryParseMonsterCell_InnerSpace_Fails");
            yield return new TestCaseData("C,E").SetName("TryParseMonsterCell_List_Fails");
            yield return new TestCaseData("E:1").SetName("TryParseMonsterCell_Colon_Fails");
            yield return new TestCaseData("{E}").SetName("TryParseMonsterCell_Braces_Fails");
            yield return new TestCaseData("É").SetName("TryParseMonsterCell_NonAscii_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseMonsterCell_Invalid(string text)
        {
            bool parsed = MeshMapNetworkManager.TryParseMonsterCell(text, out string cell, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(cell, Is.Null);
            StringAssert.Contains(MeshMapNetworkManager.ENV_MONSTER_CELL, error);
            AssertPrintable(error);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void TryParseMonsterCell_Unset_IsOff(string text)
        {
            bool parsed = MeshMapNetworkManager.TryParseMonsterCell(text, out string cell, out string error);

            Assert.That(parsed, Is.True);
            Assert.That(cell, Is.Null);
            Assert.That(error, Is.Null);
        }

        [TestCase(" E ", "E")]
        [TestCase("cell_2-b", "cell_2-b")]
        public void TryParseMonsterCell_Id_IsTrimmed(string text, string expected)
        {
            bool parsed = MeshMapNetworkManager.TryParseMonsterCell(text, out string cell, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(cell, Is.EqualTo(expected));
        }

        [TestCase(null, "C", true, true)]
        [TestCase(null, "C", false, false)]
        [TestCase("", "E", true, true)]
        [TestCase("E", "E", false, true)]
        [TestCase("E", "E", true, true)]
        [TestCase("E", "C", true, false)]
        [TestCase("E", "C", false, false)]
        [TestCase("E", "e", false, false)]
        public void OwnsMonsterArea_FollowsTheMonsterCell(string monsterCell, string ownCell, bool geographic, bool expected)
        {
            Assert.That(MeshMapNetworkManager.OwnsMonsterArea(monsterCell, ownCell, geographic), Is.EqualTo(expected));
        }

        [TestCase(null, "C", "E", false)]
        [TestCase("", "C", "E", false)]
        [TestCase("E", "E", "C", false)]
        [TestCase("E", "C", "W", false)]
        [TestCase("E", "C", "e", false)]
        [TestCase("E", "C", "E", true)]
        public void StreamsEveryCharacterTo_OnlyFromOtherCellsToTheMonsterCell(string monsterCell, string ownCell, string neighbour, bool expected)
        {
            Assert.That(MeshMapNetworkManager.StreamsEveryCharacterTo(monsterCell, ownCell, neighbour), Is.EqualTo(expected));
        }

        [Test]
        public void MonsterCellLine_MonsterCell_NamesIt()
        {
            string line = MeshMapNetworkManager.MonsterCellLine("E", true);

            Assert.That(line, Is.EqualTo("MESH_MONSTER_CELL=E: cell E owns every monster spawn area"));
            AssertPrintable(line);
        }

        [Test]
        public void MonsterCellLine_OtherCell_OwnsNone()
        {
            string line = MeshMapNetworkManager.MonsterCellLine("E", false);

            Assert.That(line, Is.EqualTo("MESH_MONSTER_CELL=E: this cell owns no monster spawn area"));
            AssertPrintable(line);
        }
    }
}
