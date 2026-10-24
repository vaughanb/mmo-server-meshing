using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>A slow handoff save made again at the player's position (DESIGN.md 0.91, MeshMapNetworkManager_HandoffResave).</summary>
    public class MeshHandoffResaveTests
    {
        [Test]
        public void ShouldResave_OnlyAfterASlowSaveWhenThePlayerMovedOn()
        {
            Assert.That(MeshMapNetworkManager.ShouldResave(8762, 25.5f, 2f, 0, 3), Is.True);
            Assert.That(MeshMapNetworkManager.ShouldResave(MeshMapNetworkManager.SlowSaveMs, 25.5f, 2f, 0, 3), Is.False);   // not slow
            Assert.That(MeshMapNetworkManager.ShouldResave(8762, 1.9f, 2f, 0, 3), Is.False);    // barely moved
            Assert.That(MeshMapNetworkManager.ShouldResave(8762, 25.5f, 0f, 0, 3), Is.False);   // turned off
            Assert.That(MeshMapNetworkManager.ShouldResave(8762, 25.5f, 2f, 3, 3), Is.False);   // attempts used up
        }

        [Test]
        public void SlowSaveLine_SaysWhatTheResavesDid()
        {
            Assert.That(MeshMapNetworkManager.SlowSaveLine("bot", 8762, 0, 0f, 8762, false),
                Is.EqualTo("Handoff save for bot took 8762 ms; the saved position may lag the client"));
            Assert.That(MeshMapNetworkManager.SlowSaveLine("bot", 8762, 1, 25.54f, 12, false),
                Is.EqualTo("Handoff save for bot took 8762 ms; saved again 1 time(s) at the player's position, 25.5 m on, the last in 12 ms"));
            Assert.That(MeshMapNetworkManager.SlowSaveLine("bot", 8762, 2, 3f, 30, true),
                Does.EndWith("the last in 30 ms (the last re-save failed: the row keeps the one before)"));
        }
    }
}
