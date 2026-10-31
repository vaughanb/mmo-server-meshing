#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The database server answers character, list and building requests from its caches before
    /// SQLite, and defers character saves. What it answers must match what was written: a new
    /// character must be listed at once, an update must be read back before the deferred save
    /// is written (or a relog rolls the player back), a deleted character or building must not
    /// be served from a cache, a name must not be given twice, and one account must never read
    /// or delete another's character. Handlers are called directly on a <see cref="TestDatabaseServer"/>.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class DatabaseServerCacheTests : KitTestFixture
    {
        private TestDatabaseServer _server;
        private string _alice;
        private string _bob;

        [SetUp]
        public void SetUp()
        {
            _server = TestDatabaseServer.Create(Objects);
            _alice = _server.Db.CreateUser("alice", "secret");
            _bob = _server.Db.CreateUser("bob", "secret");
        }

        private TestDatabaseServer.Reply<CharacterResp> Create(string userId, PlayerCharacterData character)
        {
            return _server.Request<CreateCharacterReq, CharacterResp>("CreateCharacter", new CreateCharacterReq() { UserId = userId, CharacterData = character });
        }

        private PlayerCharacterData CreateFor(string userId, string name)
        {
            PlayerCharacterData character = TestCharacters.NewPlayer(name);
            Assert.That(Create(userId, character).Code, Is.EqualTo(AckResponseCode.Success));
            return character;
        }

        private PlayerCharacterData Read(string userId, string characterId)
        {
            return _server.Request<GetCharacterReq, CharacterResp>("GetCharacter", new GetCharacterReq() { UserId = userId, CharacterId = characterId }).Response.CharacterData;
        }

        private List<PlayerCharacterData> List(string userId)
        {
            return _server.Request<GetCharactersReq, CharactersResp>("GetCharacters", new GetCharactersReq() { UserId = userId }).Response.List;
        }

        private void Delete(string userId, string characterId)
        {
            _server.Request<DeleteCharacterReq, EmptyMessage>("DeleteCharacter", new DeleteCharacterReq() { UserId = userId, CharacterId = characterId });
        }

        private void Update(PlayerCharacterData character)
        {
            TestDatabaseServer.Reply<CharacterResp> reply = _server.Request<UpdateCharacterReq, CharacterResp>("UpdateCharacter", new UpdateCharacterReq()
            {
                State = TransactionUpdateCharacterState.Character,
                CharacterData = character,
            });
            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Success));
        }

        /// <summary>The character as a map server holds it: the loaded row with a change.</summary>
        private PlayerCharacterData Loaded(string characterId)
        {
            return TestAsync.Wait(_server.Db.Database.GetCharacter(characterId));
        }

        [Test]
        public void AnAccountCannotReadAnotherAccountsCharacter()
        {
            PlayerCharacterData rowan = CreateFor(_alice, "Rowan");

            Assert.That(Read(_bob, rowan.Id), Is.Null);
            Assert.That(Read(_alice, rowan.Id).CharacterName, Is.EqualTo("Rowan"));
            Assert.That(Read(_bob, rowan.Id), Is.Null, "nor once it is cached");
        }

        [Test]
        public void ANewCharacterIsListedStraightAwayOnItsAccountOnly()
        {
            Assert.That(List(_alice), Is.Empty);
            Assert.That(List(_bob), Is.Empty);

            CreateFor(_alice, "Rowan");

            Assert.That(List(_alice).Select(c => c.CharacterName), Is.EqualTo(new[] { "Rowan" }));
            Assert.That(List(_bob), Is.Empty);
        }

        [Test]
        public void ANameAlreadyTakenIsRefusedAndNothingIsInserted()
        {
            CreateFor(_alice, "Rowan");

            TestDatabaseServer.Reply<CharacterResp> second = Create(_bob, TestCharacters.NewPlayer("Rowan"));

            Assert.That(second.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(List(_bob), Is.Empty);
            Assert.That(TestAsync.Wait(_server.Db.Database.FindCharacterName("Rowan")), Is.EqualTo(1));
            Assert.That(Create(_bob, TestCharacters.NewPlayer("Ash")).Code, Is.EqualTo(AckResponseCode.Success), "the name lock was released");
        }

        [Test]
        public void AnUpdateIsReadBackAtOnceAndWrittenToTheDatabaseByTheFlush()
        {
            _server.UseSaveScheduler();
            PlayerCharacterData rowan = CreateFor(_alice, "Rowan");
            List(_alice);
            PlayerCharacterData changed = Loaded(rowan.Id);
            changed.Level = 9;

            Update(changed);

            Assert.That(Read(_alice, rowan.Id).Level, Is.EqualTo(9), "a relog before the save sees the update");
            Assert.That(List(_alice).Single().Level, Is.EqualTo(9), "and so does the character list");
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(1), "the save is deferred");

            TestAsync.Wait(_server.Manager.FlushCharacterSavesAsync());

            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(9));
        }

        [Test]
        public void ADeletedCharacterIsGoneFromTheListAndFromReads()
        {
            PlayerCharacterData rowan = CreateFor(_alice, "Rowan");
            PlayerCharacterData ash = CreateFor(_alice, "Ash");
            // Warm both caches.
            Read(_alice, rowan.Id);
            List(_alice);

            Delete(_alice, rowan.Id);

            Assert.That(Read(_alice, rowan.Id), Is.Null);
            Assert.That(List(_alice).Select(c => c.Id), Is.EqualTo(new[] { ash.Id }));
            Assert.That(Loaded(rowan.Id), Is.Null);
        }

        [Test]
        public void AnAccountCannotDeleteAnotherAccountsCharacter()
        {
            PlayerCharacterData rowan = CreateFor(_alice, "Rowan");
            List(_alice);

            Delete(_bob, rowan.Id);

            Assert.That(Read(_alice, rowan.Id), Is.Not.Null);
            Assert.That(List(_alice).Select(c => c.Id), Is.EqualTo(new[] { rowan.Id }));
            Assert.That(Loaded(rowan.Id), Is.Not.Null);
        }

        [Test]
        public void ADeletedCharacterDoesNotComeBackWhenItsWaitingSaveIsWritten()
        {
            _server.UseSaveScheduler();
            PlayerCharacterData rowan = CreateFor(_alice, "Rowan");
            PlayerCharacterData changed = Loaded(rowan.Id);
            changed.Level = 9;
            // The map server saves on logout, then the player deletes the character at once.
            Update(changed);
            Delete(_alice, rowan.Id);

            TestAsync.Wait(_server.Manager.FlushCharacterSavesAsync());

            Assert.That(Read(_alice, rowan.Id), Is.Null);
            Assert.That(Loaded(rowan.Id), Is.Null);
        }

        [Test]
        public void ADeletedBuildingIsGoneFromTheCacheAndTheDatabase()
        {
            BuildingSaveData wall = new BuildingSaveData() { Id = "wall", ParentId = string.Empty, EntityId = 501, CurrentHp = 300, CreatorId = "c", CreatorName = "Builder", ExtraData = string.Empty, LockPassword = string.Empty };
            BuildingSaveData door = new BuildingSaveData() { Id = "door", ParentId = string.Empty, EntityId = 502, CurrentHp = 300, CreatorId = "c", CreatorName = "Builder", ExtraData = string.Empty, LockPassword = string.Empty };
            _server.Request<CreateBuildingReq, BuildingResp>("CreateBuilding", new CreateBuildingReq() { ChannelId = "eu", MapName = "Town", BuildingData = wall });
            _server.Request<CreateBuildingReq, BuildingResp>("CreateBuilding", new CreateBuildingReq() { ChannelId = "eu", MapName = "Town", BuildingData = door });

            _server.Request<DeleteBuildingReq, EmptyMessage>("DeleteBuilding", new DeleteBuildingReq() { ChannelId = "eu", MapName = "Town", BuildingId = "wall" });

            List<BuildingSaveData> served = _server.Request<GetBuildingsReq, BuildingsResp>("GetBuildings", new GetBuildingsReq() { ChannelId = "eu", MapName = "Town" }).Response.List;
            Assert.That(served.Select(b => b.Id), Is.EqualTo(new[] { "door" }));
            Assert.That(TestAsync.Wait(_server.Db.Database.GetBuildings("eu", "Town")).Select(b => b.Id), Is.EqualTo(new[] { "door" }));
            Assert.That(_server.Request<GetBuildingsReq, BuildingsResp>("GetBuildings", new GetBuildingsReq() { ChannelId = "us", MapName = "Town" }).Response.List, Is.Empty, "another channel's map has none");
        }
    }
}
#endif
