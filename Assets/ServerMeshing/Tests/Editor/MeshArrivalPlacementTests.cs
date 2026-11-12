using MultiplayerARPG.MMO.Mesh;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Where a cell puts a character arriving by handoff: the handoff notice's pose over the row the kit loaded, and
    /// the puppet it promotes in place moved there (a yank, counted, past the teleport distance). Pure, no network.
    /// </summary>
    public class MeshArrivalPlacementTests
    {
        private const float Teleport = 4f;

        // Session 8, run 2 (17 Oct 2026): cell 6-2 saved the handoff at z = -2050.28; cell 6-1 loaded a row still at
        // z = -2040.03 (the position from before the walk) and its puppet, steered through the walk, stood about a
        // metre short of the save.
        private static readonly Vector3 Save = new Vector3(2393.00f, 298.88f, -2050.28f);
        private static readonly Vector3 StaleRow = new Vector3(2393.00f, 302.01f, -2040.03f);
        private static readonly Vector3 PuppetNearSave = new Vector3(2393.00f, 298.90f, -2049.20f);

        [Test]
        public void HandoffPose_BeatsAStaleRow()
        {
            MeshMapNetworkManager.ArrivalPlacement p = MeshMapNetworkManager.PlaceArrival(true, Save, StaleRow, true, PuppetNearSave, Teleport);
            Assert.AreEqual(Save, p.position);
            Assert.IsTrue(p.fromHandoff);
            Assert.IsTrue(p.rowStale);
            Assert.AreEqual(10.7f, p.rowError, 0.1f);
            Assert.IsFalse(p.puppetYanked, "a puppet that followed the walk is only nudged to the save");
            Assert.AreEqual(1.08f, p.puppetError, 0.01f);
        }

        [Test]
        public void HandoffPose_MatchingRow_IsNotStale()
        {
            Vector3 row = Save + new Vector3(0.01f, 0f, -0.02f); // a float's round trip through the database
            MeshMapNetworkManager.ArrivalPlacement p = MeshMapNetworkManager.PlaceArrival(true, Save, row, true, PuppetNearSave, Teleport);
            Assert.AreEqual(Save, p.position);
            Assert.IsFalse(p.rowStale);
        }

        [Test]
        public void HandoffPose_StalePuppet_IsYanked()
        {
            MeshMapNetworkManager.ArrivalPlacement p = MeshMapNetworkManager.PlaceArrival(true, Save, Save, true, StaleRow, Teleport);
            Assert.AreEqual(Save, p.position);
            Assert.IsFalse(p.rowStale);
            Assert.IsTrue(p.puppetYanked);
            Assert.AreEqual(10.7f, p.puppetError, 0.1f);
        }

        [Test]
        public void NoPose_RowStands_PuppetFollowsIt()
        {
            MeshMapNetworkManager.ArrivalPlacement p = MeshMapNetworkManager.PlaceArrival(false, default, StaleRow, true, PuppetNearSave, Teleport);
            Assert.AreEqual(StaleRow, p.position);
            Assert.IsFalse(p.fromHandoff);
            Assert.IsFalse(p.rowStale);
            Assert.AreEqual(0f, p.rowError);
            Assert.IsTrue(p.puppetYanked, "without a pose the row is the only truth, and a puppet 10 m from it is yanked");
        }

        [Test]
        public void NoPuppet_NothingToYank()
        {
            MeshMapNetworkManager.ArrivalPlacement p = MeshMapNetworkManager.PlaceArrival(true, Save, StaleRow, false, default, Teleport);
            Assert.AreEqual(Save, p.position);
            Assert.IsTrue(p.rowStale);
            Assert.IsFalse(p.puppetYanked);
            Assert.AreEqual(0f, p.puppetError);
        }

        [Test]
        public void PuppetAtTheTeleportDistance_IsNotYanked()
        {
            Vector3 puppet = Save + new Vector3(0f, 0f, Teleport);
            Assert.IsFalse(MeshMapNetworkManager.PlaceArrival(true, Save, Save, true, puppet, Teleport).puppetYanked);
            Assert.IsTrue(MeshMapNetworkManager.PlaceArrival(true, Save, Save, true, puppet + new Vector3(0f, 0f, 0.01f), Teleport).puppetYanked);
        }
    }
}
