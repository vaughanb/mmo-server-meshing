#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using Mono.Data.Sqlite;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Characters as the account sees them: the character select list shows only the user's own
    /// characters, deleting one removes every row it owns and nobody else's, and a user cannot
    /// delete someone else's character. Name lookups back whisper, friend and GM commands.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteCharacterAccountTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _userId;

        /// <summary>Every per-character table and the column that holds the character id.</summary>
        private static readonly string[][] CharacterTables =
        {
            new[] { "characterattribute", "characterId" },
            new[] { "charactercurrency", "characterId" },
            new[] { "characterbuff", "characterId" },
            new[] { "characterhotkey", "characterId" },
            new[] { "characteritem", "characterId" },
            new[] { "characterquest", "characterId" },
            new[] { "characterskill", "characterId" },
            new[] { "characterskillusage", "characterId" },
            new[] { "charactersummon", "characterId" },
            new[] { "charactermount", "id" },
            new[] { "character_pk", "id" },
            new[] { "character_server_boolean", "characterId" },
            new[] { "character_server_int32", "characterId" },
            new[] { "character_server_float32", "characterId" },
            new[] { "character_private_boolean", "characterId" },
            new[] { "character_private_int32", "characterId" },
            new[] { "character_private_float32", "characterId" },
            new[] { "character_public_boolean", "characterId" },
            new[] { "character_public_int32", "characterId" },
            new[] { "character_public_float32", "characterId" },
        };

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        private long Count(string table, string column, string characterId)
        {
            return (long)_db.Database.ExecuteScalar($"SELECT COUNT(*) FROM {table} WHERE {column}=@id", new SqliteParameter("@id", characterId));
        }

        /// <summary>A character with one row in every per-character table.</summary>
        private PlayerCharacterData CreateCharacter(string userId, string name)
        {
            PlayerCharacterData c = TestCharacters.NewPlayer(name);
            c.Attributes = new List<CharacterAttribute>() { new CharacterAttribute() { dataId = 1, amount = 1 } };
            c.Currencies = new List<CharacterCurrency>() { new CharacterCurrency() { dataId = 1, amount = 1 } };
            c.Buffs = new List<CharacterBuff>() { new CharacterBuff() { id = name + "-buff", dataId = 1, level = 1, buffRemainsDuration = 1f } };
            c.Hotkeys = new List<CharacterHotkey>() { new CharacterHotkey() { hotkeyId = "HK1", type = HotkeyType.Skill, relateId = "1" } };
            c.NonEquipItems = new List<CharacterItem>() { new CharacterItem() { id = name + "-item", dataId = 1, level = 1, amount = 1 } };
            c.Quests = new List<CharacterQuest>() { new CharacterQuest() { dataId = 1 } };
            c.Skills = new List<CharacterSkill>() { new CharacterSkill() { dataId = 1, level = 1 } };
            c.SkillUsages = new List<CharacterSkillUsage>() { new CharacterSkillUsage() { dataId = 1, coolDownRemainsDuration = 1f } };
            c.Summons = new List<CharacterSummon>() { new CharacterSummon() { type = SummonType.PetItem, sourceId = "pet", dataId = 1, level = 1 } };
            c.Mount = new CharacterMount() { type = MountType.MountItem, sourceId = "horse", level = 1 };
            c.ServerBools = new List<CharacterDataBoolean>() { new CharacterDataBoolean() { hashedKey = 1, value = true } };
            c.ServerInts = new List<CharacterDataInt32>() { new CharacterDataInt32() { hashedKey = 1, value = 1 } };
            c.ServerFloats = new List<CharacterDataFloat32>() { new CharacterDataFloat32() { hashedKey = 1, value = 1f } };
            c.PrivateBools = new List<CharacterDataBoolean>() { new CharacterDataBoolean() { hashedKey = 1, value = true } };
            c.PrivateInts = new List<CharacterDataInt32>() { new CharacterDataInt32() { hashedKey = 1, value = 1 } };
            c.PrivateFloats = new List<CharacterDataFloat32>() { new CharacterDataFloat32() { hashedKey = 1, value = 1f } };
            c.PublicBools = new List<CharacterDataBoolean>() { new CharacterDataBoolean() { hashedKey = 1, value = true } };
            c.PublicInts = new List<CharacterDataInt32>() { new CharacterDataInt32() { hashedKey = 1, value = 1 } };
            c.PublicFloats = new List<CharacterDataFloat32>() { new CharacterDataFloat32() { hashedKey = 1, value = 1f } };
            TestAsync.Wait(_db.Database.CreateCharacter(userId, c));
            return c;
        }

        [Test]
        public void DeletingACharacterRemovesItAndEveryRowItOwns()
        {
            PlayerCharacterData character = CreateCharacter(_userId, "Doomed");
            foreach (string[] table in CharacterTables)
                Assume.That(Count(table[0], table[1], character.Id), Is.EqualTo(1), table[0] + " has a row before the delete");

            TestAsync.Wait(_db.Database.DeleteCharacter(_userId, character.Id));

            Assert.That(TestAsync.Wait(_db.Database.GetCharacter(character.Id)), Is.Null);
            foreach (string[] table in CharacterTables)
                Assert.That(Count(table[0], table[1], character.Id), Is.Zero, table[0] + " is emptied");
        }

        [Test]
        public void DeletingACharacterLeavesOtherCharactersAlone()
        {
            PlayerCharacterData doomed = CreateCharacter(_userId, "Doomed");
            PlayerCharacterData kept = CreateCharacter(_userId, "Kept");

            TestAsync.Wait(_db.Database.DeleteCharacter(_userId, doomed.Id));

            PlayerCharacterData loaded = TestAsync.Wait(_db.Database.GetCharacter(kept.Id));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.NonEquipItems.Count, Is.EqualTo(1));
            foreach (string[] table in CharacterTables)
                Assert.That(Count(table[0], table[1], kept.Id), Is.EqualTo(1), table[0] + " still has the other character's row");
        }

        [Test]
        public void AUserCannotDeleteAnotherUsersCharacter()
        {
            string otherUserId = _db.CreateUser("other", "secret2");
            PlayerCharacterData character = CreateCharacter(_userId, "Mine");

            TestAsync.Wait(_db.Database.DeleteCharacter(otherUserId, character.Id));

            Assert.That(TestAsync.Wait(_db.Database.GetCharacter(character.Id)), Is.Not.Null);
        }

        [Test]
        public void DeletingACharacterRemovesItsSummonBuffs()
        {
            PlayerCharacterData character = CreateCharacter(_userId, "Doomed");
            TestAsync.Wait(_db.Database.UpdateCharacter(TransactionUpdateCharacterState.Summons, character,
                new List<CharacterBuff>() { new CharacterBuff() { id = "sbuff", dataId = 1, level = 1, buffRemainsDuration = 5f } }, false));

            TestAsync.Wait(_db.Database.DeleteCharacter(_userId, character.Id));

            Assert.That(Count("summonbuffs", "characterId", character.Id), Is.Zero);
        }

        [Test]
        public void TheCharacterListHoldsOnlyTheUsersOwnCharacters()
        {
            string otherUserId = _db.CreateUser("other", "secret2");
            PlayerCharacterData first = CreateCharacter(_userId, "First");
            PlayerCharacterData second = CreateCharacter(_userId, "Second");
            CreateCharacter(otherUserId, "Stranger");

            List<PlayerCharacterData> list = TestAsync.Wait(_db.Database.GetCharacters(_userId));

            Assert.That(list.Select(c => c.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
            Assert.That(list.All(c => c.UserId == _userId));
        }

        [Test]
        public void CharacterNamesLeadToTheCharacterAndItsUser()
        {
            PlayerCharacterData character = CreateCharacter(_userId, "Rowan");

            Assert.That(TestAsync.Wait(_db.Database.FindCharacterName("Rowan")), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.FindCharacterName("Nobody")), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.GetIdByCharacterName("Rowan")), Is.EqualTo(character.Id));
            Assert.That(TestAsync.Wait(_db.Database.GetUserIdByCharacterName("Rowan")), Is.EqualTo(_userId));
            Assert.That(TestAsync.Wait(_db.Database.GetIdByCharacterName("Nobody")), Is.Empty);
        }

        [Test]
        public void AMuteSetByNameIsSavedOnTheCharacter()
        {
            PlayerCharacterData character = CreateCharacter(_userId, "Loud");

            TestAsync.Wait(_db.Database.SetCharacterUnmuteTimeByName("Loud", 1900000000L));

            Assert.That(TestAsync.Wait(_db.Database.GetCharacter(character.Id)).UnmuteTime, Is.EqualTo(1900000000L));
        }
    }
}
#endif
