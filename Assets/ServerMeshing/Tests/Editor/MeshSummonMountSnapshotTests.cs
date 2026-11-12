using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The summon mount in GhostSnapshotMessage (a rider on one of its own summons, IMeshSummonMount). It is written
    /// after the vending title and before the trailing fields (the sample stamp and a container's items), and its
    /// seat has its own field: mountSeat is the seat on a kit vehicle.
    /// </summary>
    public class MeshSummonMountSnapshotTests
    {
        private static GhostSnapshotMessage RoundTrip(GhostSnapshotMessage msg)
        {
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            byte[] data = new byte[writer.Length];
            System.Array.Copy(writer.Data, data, data.Length);
            GhostSnapshotMessage read = default;
            read.Deserialize(new NetDataReader(data));
            return read;
        }

        [Test]
        public void RiderSnapshotCarriesTheSummonAndSeat()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "c1", characterName = "Probe", mountSummonId = "s-42", mountSummonSeat = 1 });
            Assert.AreEqual("s-42", read.mountSummonId);
            Assert.AreEqual(1, read.mountSummonSeat);
            Assert.AreEqual(0, read.mountSeat, "the kit vehicle's seat is a field of its own");
            Assert.AreEqual("Probe", read.characterName);
        }

        [Test]
        public void BuildingRotationStillReadsBeforeTheSummonMount()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage { kind = GhostKind.Building, characterId = "b1", characterName = string.Empty, fullRotation = new Vector3(1f, 2f, 3f) });
            Assert.AreEqual(new Vector3(1f, 2f, 3f), read.fullRotation);
            Assert.AreEqual(string.Empty, read.mountSummonId);
            Assert.AreEqual(0, read.mountSummonSeat);
        }

        [Test]
        public void UpstreamFieldsAroundTheSummonMountStillRoundTrip()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage
            {
                kind = GhostKind.Player, characterId = "c1", characterName = "Probe", isPkOn = true, isWeaponsSheathed = true,
                vendingTitle = "Hides", mountSummonId = "s-7", mountSummonSeat = 2, mountSeat = 1, driverMeshId = "c2", sampleMs = 1234.5,
            });
            Assert.IsTrue(read.isPkOn);
            Assert.IsTrue(read.isWeaponsSheathed);
            Assert.AreEqual("Hides", read.vendingTitle);
            Assert.AreEqual("s-7", read.mountSummonId);
            Assert.AreEqual(2, read.mountSummonSeat);
            Assert.AreEqual(1, read.mountSeat);
            Assert.AreEqual("c2", read.driverMeshId);
            Assert.AreEqual(1234.5, read.sampleMs);
        }

        [Test]
        public void RiderOnFootReadsNoSummon()
        {
            GhostSnapshotMessage read = RoundTrip(new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "c1", characterName = "Probe", sampleMs = 9.0 });
            Assert.AreEqual(string.Empty, read.mountSummonId);
            Assert.AreEqual(0, read.mountSummonSeat);
            Assert.AreEqual(9.0, read.sampleMs);
        }
    }
}
