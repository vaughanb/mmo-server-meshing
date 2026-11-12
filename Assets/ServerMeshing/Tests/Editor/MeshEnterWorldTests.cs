using System.Collections.Generic;
using MultiplayerARPG.MMO;
using MultiplayerARPG.MMO.Mesh;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshEnterWorldTests covers entering a sleeping world: which cells a login starts first, how long the login
    /// server waits for them, and the address cells link to each other on.
    /// </summary>
    public class MeshEnterWorldTests
    {
        [TestCase(false, 1, new[] { "A" }, new[] { "B" }, TestName = "AddWithRings_CellStarting_RingKeptNotStarted")]
        [TestCase(false, 2, new[] { "A" }, new[] { "B", "C" }, TestName = "AddWithRings_CellStarting_TwoRingsKeptNotStarted")]
        [TestCase(true, 0, new[] { "A" }, new string[0], TestName = "AddWithRings_CellUp_NoRings")]
        [TestCase(true, 1, new[] { "A", "B" }, new string[0], TestName = "AddWithRings_CellUp_OneRingWanted")]
        [TestCase(true, 2, new[] { "A", "B", "C" }, new string[0], TestName = "AddWithRings_CellUp_TwoRingsWanted")]
        public void AddWithRings(bool cellIsUp, int awakeRings, string[] expectedWanted, string[] expectedKept)
        {
            MeshCell a = new MeshCell { id = "A" };
            MeshCell b = new MeshCell { id = "B" };
            MeshCell c = new MeshCell { id = "C" };
            a.neighbours.Add(b);
            b.neighbours.Add(a);
            b.neighbours.Add(c);
            c.neighbours.Add(b);
            HashSet<string> wanted = new HashSet<string>();
            HashSet<string> kept = new HashSet<string>();

            MeshCentralNetworkManager.AddWithRings(a, cellIsUp, awakeRings, wanted, kept);

            CollectionAssert.AreEquivalent(expectedWanted, wanted);
            CollectionAssert.AreEquivalent(expectedKept, kept);
        }

        [TestCase(200f, 30f, 20f, TestName = "LoginWakeTimeout_AboveClientTimeout_Capped")]
        [TestCase(20f, 30f, 20f, TestName = "LoginWakeTimeout_AtCap")]
        [TestCase(15f, 30f, 15f, TestName = "LoginWakeTimeout_BelowCap_Kept")]
        [TestCase(60f, 90f, 60f, TestName = "LoginWakeTimeout_LongerClientTimeout_Kept")]
        [TestCase(60f, 5f, 0f, TestName = "LoginWakeTimeout_ClientTimeoutBelowTheMargin_Zero")]
        public void LoginWakeTimeout(float configuredSeconds, float clientTimeoutSeconds, float expectedSeconds)
        {
            Assert.AreEqual(expectedSeconds, MeshLoginNetworkManager.LoginWakeTimeout(configuredSeconds, clientTimeoutSeconds));
        }

        [Test]
        public void LoginWakeTimeout_LeavesTheClientTimeToHearTheAnswer()
        {
            Assert.Less(MeshLoginNetworkManager.LoginWakeTimeout(float.MaxValue, 30f), 30f);
        }

        [TestCase("192.168.50.20", "192.168.50.83", "192.168.50.20", TestName = "LinkAddress_OtherHost_ItsOwnAddress")]
        [TestCase("other.example.com", "game.example.com", "other.example.com", TestName = "LinkAddress_OtherPublicName_ItsOwnAddress")]
        [TestCase("game.example.com", "game.example.com", "127.0.0.1", TestName = "LinkAddress_SamePublicName_Loopback")]
        [TestCase("Game.Example.com", "game.example.com", "127.0.0.1", TestName = "LinkAddress_SameNameOtherCase_Loopback")]
        [TestCase("127.0.0.1", "127.0.0.1", "127.0.0.1", TestName = "LinkAddress_Loopback_Loopback")]
        public void LinkAddress(string peerAddress, string ownAddress, string expected)
        {
            Assert.AreEqual(expected, MeshMapNetworkManager.LinkAddress(peerAddress, ownAddress));
        }
    }
}
