using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Insthync.SerializationSurrogates;
using LiteNetLib.Utils;
using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A player's building as it is saved and sent (BuildingSaveData). Every field - where it
    /// stands, which way it faces, who built it, its lock and password, its HP and remaining life
    /// - must come back unchanged from the network form, from CloneTo and from the offline world
    /// save's binary surrogates. A field lost there is a house that reappears unlocked, at full
    /// health, turned round or owned by nobody after a restart.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class BuildingDataTests : KitTestFixture
    {
        private static BuildingSaveData House()
        {
            return new BuildingSaveData
            {
                Id = "house-1",
                ParentId = "foundation-1",
                EntityId = 4242,
                CurrentHp = 73,
                RemainsLifeTime = 120.5f,
                IsLocked = true,
                LockPassword = "1234",
                Position = new Vec3(10.5f, 2f, -7.25f),
                Rotation = new Vec3(0f, 135f, 0f),
                CreatorId = "char-9",
                CreatorName = "Bram",
                ExtraData = "{\"note\":1}",
                IsSceneObject = true,
            };
        }

        private static void AssertSame(IBuildingSaveData expected, IBuildingSaveData actual)
        {
            Assert.That(actual.Id, Is.EqualTo(expected.Id), nameof(actual.Id));
            Assert.That(actual.ParentId, Is.EqualTo(expected.ParentId), nameof(actual.ParentId));
            Assert.That(actual.EntityId, Is.EqualTo(expected.EntityId), nameof(actual.EntityId));
            Assert.That(actual.CurrentHp, Is.EqualTo(expected.CurrentHp), nameof(actual.CurrentHp));
            Assert.That(actual.RemainsLifeTime, Is.EqualTo(expected.RemainsLifeTime), nameof(actual.RemainsLifeTime));
            Assert.That(actual.IsLocked, Is.EqualTo(expected.IsLocked), nameof(actual.IsLocked));
            Assert.That(actual.LockPassword, Is.EqualTo(expected.LockPassword), nameof(actual.LockPassword));
            Assert.That((Vector3)actual.Position, Is.EqualTo((Vector3)expected.Position), nameof(actual.Position));
            Assert.That(Quaternion.Angle(Quaternion.Euler(actual.Rotation), Quaternion.Euler(expected.Rotation)), Is.LessThan(0.01f), nameof(actual.Rotation));
            Assert.That(actual.CreatorId, Is.EqualTo(expected.CreatorId), nameof(actual.CreatorId));
            Assert.That(actual.CreatorName, Is.EqualTo(expected.CreatorName), nameof(actual.CreatorName));
            Assert.That(actual.ExtraData, Is.EqualTo(expected.ExtraData), nameof(actual.ExtraData));
            Assert.That(actual.IsSceneObject, Is.EqualTo(expected.IsSceneObject), nameof(actual.IsSceneObject));
        }

        /// <summary>The binary formatter as WorldSaveData sets it up, writing to memory instead of the save folder.</summary>
        private static BinaryFormatter WorldSaveFormatter()
        {
            SurrogateSelector selector = new SurrogateSelector();
            selector.AddAllUnitySurrogate();
            StreamingContext all = new StreamingContext(StreamingContextStates.All);
            selector.AddSurrogate(typeof(BuildingSaveData), all, new BuildingSaveDataSerializationSurrogate());
            selector.AddSurrogate(typeof(WorldSaveData), all, new WorldSaveDataSerializationSurrogate());
            return new BinaryFormatter { SurrogateSelector = selector, Binder = new PlayerCharacterDataTypeBinder() };
        }

        [Test]
        public void ABuildingSurvivesTheNetworkFormUnchanged()
        {
            BuildingSaveData house = House();
            NetDataWriter writer = new NetDataWriter();
            house.Serialize(writer);

            BuildingSaveData received = new BuildingSaveData();
            received.Deserialize(new NetDataReader(writer));

            AssertSame(house, received);
        }

        [Test]
        public void CloneToCopiesEveryField()
        {
            BuildingSaveData house = House();

            BuildingSaveData copy = house.CloneTo(new BuildingSaveData());

            AssertSame(house, copy);
        }

        [Test]
        public void BuildingsSurviveTheOfflineWorldSave()
        {
            WorldSaveData world = new WorldSaveData();
            BuildingSaveData house = House();
            BuildingSaveData wall = House();
            wall.Id = "wall-1";
            wall.IsLocked = false;
            wall.Rotation = new Vec3(0f, -90f, 0f);
            world.buildings.Add(house);
            world.buildings.Add(wall);

            WorldSaveData loaded;
            using (MemoryStream stream = new MemoryStream())
            {
                WorldSaveFormatter().Serialize(stream, world);
                stream.Position = 0;
                loaded = (WorldSaveData)WorldSaveFormatter().Deserialize(stream);
            }

            Assert.That(loaded.buildings, Has.Count.EqualTo(2));
            AssertSame(house, loaded.buildings[0]);
            AssertSame(wall, loaded.buildings[1]);
        }
    }
}
