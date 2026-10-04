using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipLoadPlanTests
    {
        private static IEnumerable<TestCaseData> JunkCases()
        {
            yield return new TestCaseData("", "empty").SetName("TryParse_Empty_Fails");
            yield return new TestCaseData("C:50:ai,,E:2:ai", "part 2").SetName("TryParse_EmptyPart_FailsNamingIt");
            yield return new TestCaseData("C:ai", "'C:ai'").SetName("TryParse_MissingCount_FailsNamingThePart");
            yield return new TestCaseData("C::ai", "'C::ai'").SetName("TryParse_EmptyCount_FailsNamingThePart");
            yield return new TestCaseData("C:-1:ai", "'C:-1:ai'").SetName("TryParse_NegativeCount_FailsNamingThePart");
            yield return new TestCaseData("C:0:ai", "'C:0:ai'").SetName("TryParse_ZeroCount_FailsNamingThePart");
            yield return new TestCaseData("C:2001:ai", "'C:2001:ai'").SetName("TryParse_CountAbove2000_FailsNamingThePart");
            yield return new TestCaseData("C:x:ai", "'C:x:ai'").SetName("TryParse_NonIntegerCount_FailsNamingThePart");
            yield return new TestCaseData("C:5:pirate", "'C:5:pirate'").SetName("TryParse_UnknownAuthority_FailsNamingThePart");
            yield return new TestCaseData(":5:ai", "':5:ai'").SetName("TryParse_NoCell_FailsNamingThePart");
            yield return new TestCaseData("C:5:ai,C:3:AI", "'C:3:AI'").SetName("TryParse_DuplicateCellAndAuthority_FailsNamingThePart");
            yield return new TestCaseData("C:5:ai:extra", "'C:5:ai:extra'").SetName("TryParse_FourFields_FailsNamingThePart");
            yield return new TestCaseData("C:{5}:ai", "'C:(5):ai'").SetName("TryParse_Braces_AreCleanedInTheError");
        }

        [TestCaseSource(nameof(JunkCases))]
        public void TryParse_Junk_GivesNullAndAnErrorNamingThePart(string text, string named)
        {
            ServerMeshShipLoadPlan plan = ServerMeshShipLoadPlan.TryParse(text, out string error);

            Assert.That(plan, Is.Null);
            Assert.That(error, Does.Contain(named));
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [Test]
        public void TryParse_ContractSample_Parses()
        {
            ServerMeshShipLoadPlan plan = ServerMeshShipLoadPlan.TryParse("C:50:ai,C:20:mirror,E:10:parked", out string error);

            Assert.That(error, Is.Null);
            Assert.That(plan.Groups.Count, Is.EqualTo(3));
            Assert.That(plan.Groups[0].Cell, Is.EqualTo("C"));
            Assert.That(plan.Groups[0].Count, Is.EqualTo(50));
            Assert.That(plan.Groups[0].Authority, Is.EqualTo(ServerMeshShipAuthority.ServerAI));
            Assert.That(plan.Groups[1].Authority, Is.EqualTo(ServerMeshShipAuthority.OwnerMirror));
            Assert.That(plan.Groups[2].Cell, Is.EqualTo("E"));
            Assert.That(plan.Groups[2].Authority, Is.EqualTo(ServerMeshShipAuthority.Parked));
            Assert.That(plan.CountFor("C"), Is.EqualTo(70));
            Assert.That(plan.CountFor("E"), Is.EqualTo(10));
            Assert.That(plan.CountFor("W"), Is.EqualTo(0));
        }

        [Test]
        public void TryParse_WhitespaceAndKeywordCase_AreIgnored()
        {
            ServerMeshShipLoadPlan plan = ServerMeshShipLoadPlan.TryParse("  C : 2000 : Mirror , C:1:PARKED ", out string error);

            Assert.That(error, Is.Null);
            Assert.That(plan.Groups.Count, Is.EqualTo(2));
            Assert.That(plan.Groups[0].Count, Is.EqualTo(2000));
            Assert.That(plan.Groups[0].Authority, Is.EqualTo(ServerMeshShipAuthority.OwnerMirror));
            Assert.That(plan.Groups[1].Authority, Is.EqualTo(ServerMeshShipAuthority.Parked));
        }

        [Test]
        public void LoadGroup_NullCell_ThrowsNamingIt()
        {
            System.ArgumentNullException thrown = Assert.Throws<System.ArgumentNullException>(() => new ServerMeshShipLoadGroup(null, 1, ServerMeshShipAuthority.Parked));

            Assert.That(thrown.ParamName, Is.EqualTo("cell"));
        }
    }
}
