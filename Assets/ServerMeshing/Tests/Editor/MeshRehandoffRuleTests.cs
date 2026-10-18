using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The re-handoff back-off of a Dynamic map's cell (MeshMapNetworkManager_Readiness): every arrival is held for the
    /// cooldown; a player handed out a moment ago and back without a handoff notice (its client fell back, or it re-logged
    /// in) is a return, and consecutive returns double the hold up to the cap.
    /// </summary>
    public class MeshRehandoffRuleTests
    {
        [Test]
        public void Return_OnlyWithoutANoticeAndWithinTheWindow()
        {
            Assert.That(MeshRehandoffRule.IsReturn(true, 100f, false, 106f), Is.True, "the splitcrash fallback: back 6 s after the handoff");
            Assert.That(MeshRehandoffRule.IsReturn(true, 100f, true, 106f), Is.False, "handed back by the neighbour with a notice: a crossing, not a return");
            Assert.That(MeshRehandoffRule.IsReturn(false, 0f, false, 106f), Is.False, "never handed out by this cell");
            Assert.That(MeshRehandoffRule.IsReturn(true, 100f, false, 100f + MeshRehandoffRule.ReturnWindowSeconds), Is.True);
            Assert.That(MeshRehandoffRule.IsReturn(true, 100f, false, 100.1f + MeshRehandoffRule.ReturnWindowSeconds), Is.False, "too long ago");
            Assert.That(MeshRehandoffRule.IsReturn(true, 100f, false, 99f), Is.False, "a record from a later clock is no return");
        }

        [Test]
        public void Hold_TheCooldownThenDoublingPerReturnUpToTheCap()
        {
            Assert.That(MeshRehandoffRule.HoldSeconds(0, 2f, 30f), Is.EqualTo(2f), "any arrival");
            Assert.That(MeshRehandoffRule.HoldSeconds(1, 2f, 30f), Is.EqualTo(2f), "the first return");
            Assert.That(MeshRehandoffRule.HoldSeconds(2, 2f, 30f), Is.EqualTo(4f));
            Assert.That(MeshRehandoffRule.HoldSeconds(3, 2f, 30f), Is.EqualTo(8f));
            Assert.That(MeshRehandoffRule.HoldSeconds(5, 2f, 30f), Is.EqualTo(30f), "capped");
            Assert.That(MeshRehandoffRule.HoldSeconds(50, 2f, 30f), Is.EqualTo(30f));
        }

        [Test]
        public void Hold_ZeroCooldownHoldsNothingAndACapBelowTheCooldownKeepsTheCooldown()
        {
            Assert.That(MeshRehandoffRule.HoldSeconds(0, 0f, 30f), Is.EqualTo(0f));
            Assert.That(MeshRehandoffRule.HoldSeconds(4, 0f, 30f), Is.EqualTo(0f));
            Assert.That(MeshRehandoffRule.HoldSeconds(4, 5f, 1f), Is.EqualTo(5f));
            Assert.That(MeshRehandoffRule.HoldSeconds(0, -1f, 30f), Is.EqualTo(0f));
        }
    }
}
