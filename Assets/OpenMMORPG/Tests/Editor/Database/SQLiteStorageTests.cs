#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Player, guild and building storage. Items must keep their slot order and every field, a
    /// save replaces the whole storage (an item taken out must not stay behind and be duplicated),
    /// storages of different owners and types stay apart, and a bag-to-storage move saves both
    /// sides together. Reservations stop two players opening the same storage at once.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteStorageTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        private static CharacterItem Item(string id, int dataId, int amount)
        {
            CharacterItem item = new CharacterItem()
            {
                id = id, dataId = dataId, level = 3, amount = amount, durability = 12.5f, exp = 40,
                lockRemainsDuration = 2.5f, expireTime = 1900000000L, randomSeed = 99, ammo = 4, version = 2,
            };
            item.sockets.socket2 = 5002;
            return item;
        }

        private static string Text(CharacterItem i)
        {
            return $"{i.id}:{i.dataId}:{i.level}:{i.amount}:{i.durability}:{i.exp}:{i.lockRemainsDuration}:{i.expireTime}:{i.randomSeed}:{i.ammo}:{i.WriteSockets()}:{i.version}";
        }

        private void Save(StorageType type, string owner, params CharacterItem[] items)
        {
            TestAsync.Wait(_db.Database.UpdateStorageItems(type, owner, items.ToList()));
        }

        private List<string> Load(StorageType type, string owner)
        {
            return TestAsync.Wait(_db.Database.GetStorageItems(type, owner)).Select(Text).ToList();
        }

        [Test]
        public void StorageItemsReadBackInSlotOrderWithEveryField()
        {
            CharacterItem[] items = { Item("c", 3, 1), Item("a", 1, 20), Item("b", 2, 5) };

            Save(StorageType.Player, _userId, items);

            Assert.That(Load(StorageType.Player, _userId), Is.EqualTo(items.Select(Text)));
        }

        [Test]
        public void AStoredWeaponsLoadedAmmoTypeIsSaved()
        {
            CharacterItem weapon = Item("w", 1, 1);
            weapon.ammoDataId = 8001;

            Save(StorageType.Player, _userId, weapon);

            Assert.That(TestAsync.Wait(_db.Database.GetStorageItems(StorageType.Player, _userId)).Single().ammoDataId, Is.EqualTo(8001));
        }

        [Test]
        public void SavingAStorageDeletesTheItemsLeftOut()
        {
            Save(StorageType.Player, _userId, Item("a", 1, 1), Item("b", 2, 1));

            Save(StorageType.Player, _userId, Item("b", 2, 9));

            Assert.That(Load(StorageType.Player, _userId), Is.EqualTo(new[] { Text(Item("b", 2, 9)) }));
        }

        [Test]
        public void StoragesAreKeptApartByTypeAndOwner()
        {
            Save(StorageType.Player, "owner-1", Item("p1", 1, 1));
            Save(StorageType.Player, "owner-2", Item("p2", 2, 1));
            Save(StorageType.Guild, "owner-1", Item("g1", 3, 1));

            Save(StorageType.Player, "owner-1");

            Assert.That(Load(StorageType.Player, "owner-1"), Is.Empty);
            Assert.That(Load(StorageType.Player, "owner-2").Count, Is.EqualTo(1));
            Assert.That(Load(StorageType.Guild, "owner-1").Count, Is.EqualTo(1));
        }

        [Test]
        public void MovingAnItemFromTheBagToStorageSavesBothSides()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer();
            character.NonEquipItems = new List<CharacterItem>() { Item("potion", 1, 5), Item("ore", 2, 1) };
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));

            TestAsync.Wait(_db.Database.UpdateStorageAndCharacterItems(StorageType.Player, _userId,
                new List<CharacterItem>() { Item("potion", 1, 5) },
                character.Id, new List<EquipWeapons>(), new List<CharacterItem>(), new List<CharacterItem>() { Item("ore", 2, 1) }));

            Assert.That(Load(StorageType.Player, _userId), Is.EqualTo(new[] { Text(Item("potion", 1, 5)) }));
            PlayerCharacterData loaded = TestAsync.Wait(_db.Database.GetCharacter(character.Id));
            Assert.That(loaded.NonEquipItems.Select(i => i.id), Is.EqualTo(new[] { "ore" }));
        }

        [Test]
        public void AReservationIsFoundUntilItIsDeleted()
        {
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Guild, "guild-1", "char-1"));
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Building, "chest-1", "char-2"));
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Building, "chest-2", "char-2"));

            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-1")), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-2")), Is.Zero);

            TestAsync.Wait(_db.Database.DeleteReservedStorage(StorageType.Guild, "guild-1"));
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-1")), Is.Zero);

            TestAsync.Wait(_db.Database.DeleteReservedStorageByReserver("char-2"));
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Building, "chest-1")), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Building, "chest-2")), Is.Zero);
        }

        [Test]
        public void DeleteAllReservedStorageClearsEveryReservation()
        {
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Guild, "guild-1", "char-1"));
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Building, "chest-1", "char-2"));

            TestAsync.Wait(_db.Database.DeleteAllReservedStorage());

            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-1")), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Building, "chest-1")), Is.Zero);
        }

        [Test]
        public void ACharacterSaveCanReleaseTheCharactersReservation()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer();
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            TestAsync.Wait(_db.Database.UpdateReservedStorage(StorageType.Guild, "guild-1", character.Id));

            TestAsync.Wait(_db.Database.UpdateCharacter(TransactionUpdateCharacterState.Character, character, null, false));
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-1")), Is.EqualTo(1), "kept without the flag");

            TestAsync.Wait(_db.Database.UpdateCharacter(TransactionUpdateCharacterState.Character, character, null, true));
            Assert.That(TestAsync.Wait(_db.Database.FindReservedStorage(StorageType.Guild, "guild-1")), Is.Zero);
        }
    }
}
#endif
