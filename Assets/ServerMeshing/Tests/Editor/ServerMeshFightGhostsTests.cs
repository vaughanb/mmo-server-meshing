using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshFightGhostsTests
    {
        [TestCase("2")]
        [TestCase("yes")]
        [TestCase("true")]
        [TestCase("1,1")]
        [TestCase("{1}")]
        public void TryParseSetting_Invalid_IsOffWithAnError(string text)
        {
            bool parsed = ServerMeshFightGhosts.TryParseSetting(text, out bool on, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(on, Is.False);
            StringAssert.Contains(ServerMeshFightGhosts.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("  ", false)]
        [TestCase("0", false)]
        [TestCase(" 1 ", true)]
        [TestCase("1", true)]
        public void TryParseSetting_Valid(string text, bool expected)
        {
            bool parsed = ServerMeshFightGhosts.TryParseSetting(text, out bool on, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(on, Is.EqualTo(expected));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void SkipsTarget_GhostsOnlyWithoutTheKey(bool isGhost, bool fightGhosts, bool expected)
        {
            Assert.That(ServerMeshFightGhosts.SkipsTarget(isGhost, fightGhosts), Is.EqualTo(expected));
        }
    }
}
