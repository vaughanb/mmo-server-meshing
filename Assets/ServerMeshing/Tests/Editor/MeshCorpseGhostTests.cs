using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshCorpseGhostTests
    {
        [Test]
        public void ContainerSnapshot_CarriesItsItems()
        {
            GhostSnapshotMessage msg = new GhostSnapshotMessage
            {
                kind = GhostKind.Container, characterId = "4-4:abc:12", entityId = 77, dataId = 5, characterName = "Deer",
                position = new Vector3(0.5f, 20f, 509f), yaw = 90f, currentHp = 2, maxHp = 2, sampleMs = 1234.5,
                items = new[] { CharacterItem.Create(100, 1, 3), CharacterItem.Create(200, 2, 1) },
            };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);

            GhostSnapshotMessage read = new GhostSnapshotMessage();
            read.Deserialize(new NetDataReader(writer.CopyData()));

            Assert.AreEqual(GhostKind.Container, read.kind);
            Assert.AreEqual("4-4:abc:12", read.characterId);
            Assert.AreEqual("Deer", read.characterName);
            Assert.AreEqual(1234.5, read.sampleMs);
            Assert.IsNotNull(read.items);
            Assert.AreEqual(2, read.items.Length);
            Assert.AreEqual(100, read.items[0].dataId);
            Assert.AreEqual(3, read.items[0].amount);
            Assert.AreEqual(200, read.items[1].dataId);
            Assert.AreEqual(2, read.items[1].level);
        }

        [Test]
        public void OtherSnapshots_CarryNoItems_AndReadAsBefore()
        {
            GhostSnapshotMessage msg = new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "p1", characterName = "Joe", sampleMs = 9.0, items = new[] { CharacterItem.Create(1, 1, 1) } };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            GhostSnapshotMessage read = new GhostSnapshotMessage();
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.AreEqual(GhostKind.Player, read.kind);
            Assert.AreEqual(9.0, read.sampleMs);
            Assert.IsNull(read.items, "only a container writes an item tail");
        }

        [Test]
        public void ContainerSnapshot_WithoutItems_ReadsAnEmptyArray()
        {
            GhostSnapshotMessage msg = new GhostSnapshotMessage { kind = GhostKind.Container, characterId = "c" };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            GhostSnapshotMessage read = new GhostSnapshotMessage();
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.IsNotNull(read.items);
            Assert.AreEqual(0, read.items.Length);
        }

        [Test]
        public void ContainerLootMessage_RoundTrips()
        {
            GhostContainerLootMessage msg = new GhostContainerLootMessage { characterId = "p1", containerMeshId = "4-4:abc:12", itemIndex = -1, amount = 0 };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            GhostContainerLootMessage read = new GhostContainerLootMessage();
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.AreEqual("p1", read.characterId);
            Assert.AreEqual("4-4:abc:12", read.containerMeshId);
            Assert.AreEqual(-1, read.itemIndex);
            Assert.AreEqual(0, read.amount);
        }
    }
}
