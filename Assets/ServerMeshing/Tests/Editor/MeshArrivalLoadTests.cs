using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>DESIGN.md 0.89: a handoff arrival's relates-data query is asked again only when the connection that carried it dropped.</summary>
    public class MeshArrivalLoadTests
    {
        [Test]
        public void AFailureOverALiveConnectionIsTheKits()
        {
            Assert.That(MeshArrivalLoadRules.Transient(true, true, 3, 3), Is.False, "the database manager failed it: kick as the kit does");
        }

        [Test]
        public void AFailureWhoseConnectionDroppedIsAskedAgain()
        {
            Assert.That(MeshArrivalLoadRules.Transient(true, true, 3, 4), Is.True, "dropped while it waited (the reconnect answered it with a timeout)");
            Assert.That(MeshArrivalLoadRules.Transient(true, false, 3, 3), Is.True, "sent while the connection was down");
        }

        [Test]
        public void AServerThatDoesNotWatchItsDatabaseClientNeverRetries()
        {
            Assert.That(MeshArrivalLoadRules.Transient(false, false, 3, 4), Is.False);
        }

        [Test]
        public void TheLinesAreOnePhysicalLine()
        {
            Assert.That(MeshArrivalLoadRules.RetryLine("MeshTester2", 5, "cash", 1),
                Is.EqualTo("Arrival MeshTester2 conn=5: cash failed with the database connection lost; asking again once it is back (retry 1)"));
            Assert.That(MeshArrivalLoadRules.GiveUpLine("MeshTester2", 5, "gold", true, 90f),
                Is.EqualTo("Arrival MeshTester2 conn=5: gold failed and the database connection was not back within 90s; kicked as the kit does"));
            Assert.That(MeshArrivalLoadRules.GiveUpLine("MeshTester2", 5, "gold", false, 90f),
                Is.EqualTo("Arrival MeshTester2 conn=5: gold failed over a live database connection; kicked as the kit does"));
        }
    }
}
