#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Player-built structures. A map server loads only the buildings of its own map and channel,
    /// so the two must keep buildings apart; a building's lock, hit points and position must
    /// survive a restart, and deleting one building must not take its neighbours with it.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteBuildingTests : KitTestFixture
    {
        private TempSqliteDatabase _db;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
        }

        private static BuildingSaveData Building(string id)
        {
            return new BuildingSaveData()
            {
                Id = id,
                ParentId = "foundation-1",
                EntityId = 501,
                CurrentHp = 300,
                RemainsLifeTime = 120.5f,
                Position = new Vec3(10.5f, 2f, -4.25f),
                Rotation = new Vec3(0f, 90f, 0f),
                CreatorId = "char-1",
                CreatorName = "Builder",
                ExtraData = "{\"stage\":2}",
                IsSceneObject = false,
                LockPassword = string.Empty,
            };
        }

        private static string Text(IBuildingSaveData b)
        {
            return $"{b.Id}|{b.ParentId}|{b.EntityId}|{b.CurrentHp}|{b.RemainsLifeTime}|{b.IsLocked}|{b.LockPassword}|" +
                $"{b.Position.x},{b.Position.y},{b.Position.z}|{b.Rotation.x},{b.Rotation.y},{b.Rotation.z}|" +
                $"{b.CreatorId}|{b.CreatorName}|{b.ExtraData}|{b.IsSceneObject}";
        }

        private List<BuildingSaveData> Load(string channel, string map)
        {
            return TestAsync.Wait(_db.Database.GetBuildings(channel, map));
        }

        [Test]
        public void ACreatedBuildingReadsBack()
        {
            BuildingSaveData building = Building("wall-1");

            TestAsync.Wait(_db.Database.CreateBuilding("default", "Island", building));

            Assert.That(Load("default", "Island").Select(Text), Is.EqualTo(new[] { Text(building) }));
        }

        [Test]
        public void ABuildingUpdateIsSaved()
        {
            BuildingSaveData building = Building("door-1");
            TestAsync.Wait(_db.Database.CreateBuilding("default", "Island", building));

            building.CurrentHp = 150;
            building.RemainsLifeTime = 60.5f;
            building.IsLocked = true;
            building.LockPassword = "1234";
            building.Position = new Vec3(1f, 1f, 1f);
            building.Rotation = new Vec3(0f, 180f, 0f);
            building.CreatorId = "char-2";
            building.CreatorName = "NewOwner";
            building.ExtraData = "{\"stage\":3}";
            building.ParentId = "foundation-2";
            TestAsync.Wait(_db.Database.UpdateBuilding("default", "Island", building));

            Assert.That(Load("default", "Island").Select(Text), Is.EqualTo(new[] { Text(building) }));
        }

        [Test]
        public void BuildingsAreKeptApartByChannelAndMap()
        {
            TestAsync.Wait(_db.Database.CreateBuilding("default", "Island", Building("a")));
            TestAsync.Wait(_db.Database.CreateBuilding("default", "Dungeon", Building("b")));
            TestAsync.Wait(_db.Database.CreateBuilding("ch2", "Island", Building("c")));

            Assert.That(Load("default", "Island").Select(b => b.Id), Is.EqualTo(new[] { "a" }));
            Assert.That(Load("default", "Dungeon").Select(b => b.Id), Is.EqualTo(new[] { "b" }));
            Assert.That(Load("ch2", "Island").Select(b => b.Id), Is.EqualTo(new[] { "c" }));
            Assert.That(Load("ch2", "Dungeon"), Is.Empty);
        }

        [Test]
        public void DeletingABuildingRemovesOnlyThatOne()
        {
            TestAsync.Wait(_db.Database.CreateBuilding("default", "Island", Building("a")));
            TestAsync.Wait(_db.Database.CreateBuilding("default", "Island", Building("b")));

            TestAsync.Wait(_db.Database.DeleteBuilding("default", "Island", "a"));

            Assert.That(Load("default", "Island").Select(b => b.Id), Is.EqualTo(new[] { "b" }));
        }
    }
}
#endif
