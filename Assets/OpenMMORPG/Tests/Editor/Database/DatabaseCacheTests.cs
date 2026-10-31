using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The database server keeps characters, social data, buildings, parties, guilds, storage
    /// and summons in a cache and answers reads from it before the database. A cached read must
    /// return what was last written for that key and nothing written for another, and a remove
    /// must really remove, or a deleted character, building or party comes back. Run against
    /// both caches the server can use: the local one (default) and the "disabled" one
    /// (disableDatabaseCaching), which still caches in process.
    /// </summary>
    [Category(TestCategories.Logic)]
    [TestFixture(typeof(LocalDatabaseCache))]
    [TestFixture(typeof(DisabledDatabaseCache))]
    public class DatabaseCacheTests : KitTestFixture
    {
        private readonly Type _cacheType;
        private IDatabaseCache _cache;

        public DatabaseCacheTests(Type cacheType)
        {
            _cacheType = cacheType;
        }

        [SetUp]
        public void SetUp()
        {
            _cache = (IDatabaseCache)Objects.NewGameObject(_cacheType.Name).AddComponent(_cacheType);
        }

        private static BuildingSaveData Building(string id)
        {
            return new BuildingSaveData() { Id = id, EntityId = 1, CurrentHp = 100 };
        }

        [Test]
        public void ACachedCharacterReadsBackUntilItIsRemoved()
        {
            PlayerCharacterData rowan = TestCharacters.NewPlayer("Rowan");
            PlayerCharacterData ash = TestCharacters.NewPlayer("Ash");
            Assert.That(TestAsync.Wait(_cache.SetPlayerCharacter(rowan)), Is.True);
            Assert.That(TestAsync.Wait(_cache.SetPlayerCharacter(ash)), Is.True);

            rowan.Level = 9;
            TestAsync.Wait(_cache.SetPlayerCharacter(rowan));
            DatabaseCacheResult<PlayerCharacterData> read = TestAsync.Wait(_cache.GetPlayerCharacter(rowan.Id));
            Assert.That(read.HasValue, Is.True);
            Assert.That(read.Value.CharacterName, Is.EqualTo("Rowan"));
            Assert.That(read.Value.Level, Is.EqualTo(9), "the last write wins");

            Assert.That(TestAsync.Wait(_cache.RemovePlayerCharacter(rowan.Id)), Is.True);
            Assert.That(TestAsync.Wait(_cache.GetPlayerCharacter(rowan.Id)).HasValue, Is.False);
            Assert.That(TestAsync.Wait(_cache.RemovePlayerCharacter(rowan.Id)), Is.False, "nothing left to remove");
            Assert.That(TestAsync.Wait(_cache.GetPlayerCharacter(ash.Id)).Value.CharacterName, Is.EqualTo("Ash"), "another character stays");
            Assert.That(TestAsync.Wait(_cache.GetPlayerCharacter("never-cached")).HasValue, Is.False);
        }

        [Test]
        public void PartyAndGuildChangesReachTheCachedCharacterButCreateNone()
        {
            PlayerCharacterData rowan = TestCharacters.NewPlayer("Rowan");
            TestAsync.Wait(_cache.SetPlayerCharacter(rowan));

            Assert.That(TestAsync.Wait(_cache.SetPlayerCharacterPartyId(rowan.Id, 5)), Is.True);
            Assert.That(TestAsync.Wait(_cache.SetPlayerCharacterGuildIdAndRole(rowan.Id, 7, 2)), Is.True);
            PlayerCharacterData read = TestAsync.Wait(_cache.GetPlayerCharacter(rowan.Id)).Value;
            Assert.That(read.PartyId, Is.EqualTo(5));
            Assert.That(read.GuildId, Is.EqualTo(7));
            Assert.That(read.GuildRole, Is.EqualTo(2));

            Assert.That(TestAsync.Wait(_cache.SetPlayerCharacterGuildId("never-cached", 7)), Is.False);
            Assert.That(TestAsync.Wait(_cache.GetPlayerCharacter("never-cached")).HasValue, Is.False);
        }

        [Test]
        public void ACachedSocialCharacterReadsBackUntilItIsRemoved()
        {
            SocialCharacterData rowan = new SocialCharacterData() { id = "rowan", userId = "u1", characterName = "Rowan", level = 3 };
            TestAsync.Wait(_cache.SetSocialCharacter(rowan));

            DatabaseCacheResult<SocialCharacterData> read = TestAsync.Wait(_cache.GetSocialCharacter("rowan"));
            Assert.That(read.HasValue, Is.True);
            Assert.That(read.Value.characterName, Is.EqualTo("Rowan"));

            TestAsync.Wait(_cache.RemoveSocialCharacter("rowan"));
            Assert.That(TestAsync.Wait(_cache.GetSocialCharacter("rowan")).HasValue, Is.False);
        }

        [Test]
        public void PartyAndGuildChangesReachTheCachedSocialCharacter()
        {
            TestAsync.Wait(_cache.SetSocialCharacter(new SocialCharacterData() { id = "rowan", userId = "u1", characterName = "Rowan" }));

            Assert.That(TestAsync.Wait(_cache.SetSocialCharacterPartyId("rowan", 5)), Is.True);
            Assert.That(TestAsync.Wait(_cache.SetSocialCharacterGuildIdAndRole("rowan", 7, 1)), Is.True);

            SocialCharacterData read = TestAsync.Wait(_cache.GetSocialCharacter("rowan")).Value;
            Assert.That(read.partyId, Is.EqualTo(5));
            Assert.That(read.guildId, Is.EqualTo(7));
            Assert.That(read.guildRole, Is.EqualTo(1));

            Assert.That(TestAsync.Wait(_cache.SetSocialCharacterGuildId("rowan", 0)), Is.True);
            Assert.That(TestAsync.Wait(_cache.GetSocialCharacter("rowan")).Value.guildId, Is.Zero, "leaving the guild reaches the cache too");
            Assert.That(TestAsync.Wait(_cache.SetSocialCharacterPartyId("nobody", 5)), Is.False, "a character not in the cache is not changed");
        }

        [Test]
        public void BuildingsAreCachedPerChannelAndMap()
        {
            TestAsync.Wait(_cache.SetBuildings("eu", "Town", new[] { Building("wall"), Building("door") }));
            TestAsync.Wait(_cache.SetBuilding("eu", "Forest", Building("hut")));

            Assert.That(TestAsync.Wait(_cache.GetBuildings("eu", "Town")).Value.Select(b => b.Id), Is.EquivalentTo(new[] { "wall", "door" }));
            Assert.That(TestAsync.Wait(_cache.GetBuilding("eu", "Town", "hut")).HasValue, Is.False, "another map's building is not here");
            Assert.That(TestAsync.Wait(_cache.GetBuilding("us", "Town", "wall")).HasValue, Is.False, "another channel's map is not this one");

            Assert.That(TestAsync.Wait(_cache.RemoveBuilding("eu", "Town", "wall")), Is.True);
            Assert.That(TestAsync.Wait(_cache.GetBuildings("eu", "Town")).Value.Select(b => b.Id), Is.EqualTo(new[] { "door" }));
            Assert.That(TestAsync.Wait(_cache.GetBuilding("eu", "Town", "wall")).HasValue, Is.False);

            TestAsync.Wait(_cache.RemoveBuildings("eu", "Town"));
            Assert.That(TestAsync.Wait(_cache.GetBuilding("eu", "Town", "door")).HasValue, Is.False);
            Assert.That(TestAsync.Wait(_cache.GetBuilding("eu", "Forest", "hut")).HasValue, Is.True, "other maps stay cached");
        }

        [Test]
        public void PartiesAndGuildsAreCachedByIdUntilRemoved()
        {
            TestAsync.Wait(_cache.SetParty(new PartyData(1, true, false, "leader-1")));
            TestAsync.Wait(_cache.SetParty(new PartyData(2, false, true, "leader-2")));
            TestAsync.Wait(_cache.SetGuild(new GuildData(1, "Wolves", "leader-1", new GuildRoleData[0])));

            Assert.That(TestAsync.Wait(_cache.GetParty(1)).Value.leaderId, Is.EqualTo("leader-1"));
            Assert.That(TestAsync.Wait(_cache.GetGuild(1)).Value.guildName, Is.EqualTo("Wolves"));

            TestAsync.Wait(_cache.RemoveParty(1));
            TestAsync.Wait(_cache.RemoveGuild(1));
            Assert.That(TestAsync.Wait(_cache.GetParty(1)).HasValue, Is.False);
            Assert.That(TestAsync.Wait(_cache.GetGuild(1)).HasValue, Is.False);
            Assert.That(TestAsync.Wait(_cache.GetParty(2)).HasValue, Is.True);
        }

        [Test]
        public void StorageIsCachedPerKindAndOwner()
        {
            List<CharacterItem> playerItems = new List<CharacterItem>() { CharacterItem.Create(1, 1, 3) };
            List<CharacterItem> guildItems = new List<CharacterItem>() { CharacterItem.Create(2, 1, 5) };
            TestAsync.Wait(_cache.SetStorageItems(StorageType.Player, "owner", playerItems));
            TestAsync.Wait(_cache.SetStorageItems(StorageType.Guild, "owner", guildItems));

            Assert.That(TestAsync.Wait(_cache.GetStorageItems(StorageType.Player, "owner")).Value.Single().dataId, Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_cache.GetStorageItems(StorageType.Guild, "owner")).Value.Single().dataId, Is.EqualTo(2), "a guild's storage is not a player's with the same id");
            Assert.That(TestAsync.Wait(_cache.GetStorageItems(StorageType.Player, "other")).HasValue, Is.False);

            TestAsync.Wait(_cache.RemoveStorageItems(StorageType.Player, "owner"));
            Assert.That(TestAsync.Wait(_cache.GetStorageItems(StorageType.Player, "owner")).HasValue, Is.False);
            Assert.That(TestAsync.Wait(_cache.GetStorageItems(StorageType.Guild, "owner")).HasValue, Is.True);
        }

        [Test]
        public void SummonBuffsAreCachedPerCharacterUntilRemoved()
        {
            List<CharacterBuff> buffs = new List<CharacterBuff>() { new CharacterBuff() { id = "b1", dataId = 3 } };
            TestAsync.Wait(_cache.SetSummonBuffs("rowan", buffs));

            Assert.That(TestAsync.Wait(_cache.GetSummonBuffs("rowan")).Value.Single().id, Is.EqualTo("b1"));
            Assert.That(TestAsync.Wait(_cache.GetSummonBuffs("ash")).HasValue, Is.False);

            TestAsync.Wait(_cache.RemoveSummonBuffs("rowan"));
            Assert.That(TestAsync.Wait(_cache.GetSummonBuffs("rowan")).HasValue, Is.False);
        }
    }
}
