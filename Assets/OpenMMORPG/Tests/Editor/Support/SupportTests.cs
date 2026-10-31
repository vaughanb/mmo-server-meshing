using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>The test helpers themselves: they must set the kit up and leave nothing behind.</summary>
    public class SupportTests : KitTestFixture
    {
        [Test]
        [Category(TestCategories.Logic)]
        public void WithSetsAPrivateSerializedField()
        {
            DamageElement element = Objects.Create<DamageElement>().With("id", "Fire");

            Assert.That(element.Id, Is.EqualTo("Fire"));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void TestObjectsDestroysWhatItCreated()
        {
            DamageElement asset;
            GameObject gameObject;
            using (TestObjects objects = new TestObjects())
            {
                asset = objects.Create<DamageElement>();
                gameObject = objects.NewGameObject("Probe");
            }

            Assert.That(asset == null, "asset destroyed");
            Assert.That(gameObject == null, "GameObject destroyed");
        }

        [Test]
        [Category(TestCategories.Harnessed)]
        public void TestGameInstanceIsTheSingletonUntilDisposed()
        {
            GameInstance before = GameInstance.Singleton;
            using (TestObjects objects = new TestObjects())
            {
                TestGameInstance gameInstance = TestGameInstance.Create(objects);

                Assert.That(GameInstance.Singleton, Is.SameAs(gameInstance.Instance));
                Assert.That(GameInstance.Singleton.GameplayRule, Is.SameAs(gameInstance.GameplayRule));
                Assert.That(GameInstance.Singleton.DefaultWeaponItem, Is.Not.Null);
                Assert.That(RuntimeGameDataSlots.GetSlot(gameInstance.DefaultDamageElement), Is.EqualTo(0),
                    "the default damage element is registered in slot zero");
            }

            Assert.That(GameInstance.Singleton, Is.SameAs(before));
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Test]
        [Category(TestCategories.Harnessed)]
        public void TempSqliteDatabaseDeletesItsFile()
        {
            string path;
            using (TestObjects objects = new TestObjects())
            {
                path = TempSqliteDatabase.Create(objects).FilePath;
                Assert.That(System.IO.File.Exists(path), "database file created");
            }

            Assert.That(System.IO.File.Exists(path), Is.False, "database file deleted");
        }
#endif
    }
}
