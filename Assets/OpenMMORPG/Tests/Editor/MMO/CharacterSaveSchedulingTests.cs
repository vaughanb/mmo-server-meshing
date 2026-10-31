#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The database server's deferred character saves, written through a real owner: a
    /// DatabaseNetworkManager over a temp SQLite database (<see cref="TestDatabaseServer"/>). The
    /// scheduler's loop is never started; each test runs the step it needs. Every waiting save
    /// must reach the database (on flush at shutdown, or tick by tick), a save that fails must be
    /// retried with every table it was meant to write, and no character may be unsaveable.
    /// DatabaseCharacterSaveSchedulerTests covers how waiting saves merge.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class CharacterSaveSchedulingTests : KitTestFixture
    {
        private TestDatabaseServer _server;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _server = TestDatabaseServer.Create(Objects);
            _userId = _server.Db.CreateUser();
        }

        private PlayerCharacterData NewCharacter(string name)
        {
            PlayerCharacterData character = TestCharacters.NewPlayer(name);
            TestAsync.Wait(_server.Db.Database.CreateCharacter(_userId, character));
            return Loaded(character.Id);
        }

        private PlayerCharacterData Loaded(string characterId)
        {
            return TestAsync.Wait(_server.Db.Database.GetCharacter(characterId));
        }

        private static UpdateCharacterReq Save(PlayerCharacterData character, int level, TransactionUpdateCharacterState state = TransactionUpdateCharacterState.Character)
        {
            character.Level = level;
            return new UpdateCharacterReq() { State = state, CharacterData = character };
        }

        private static bool SaveNow(DatabaseCharacterSaveScheduler scheduler, string characterId, bool ignoreInterval = false)
        {
            return TestAsync.Wait((UniTask<bool>)TestReflection.Invoke(scheduler, "SaveCharacterNow", characterId, ignoreInterval));
        }

        private static UpdateCharacterReq Waiting(DatabaseCharacterSaveScheduler scheduler, string characterId)
        {
            IDictionary pending = TestReflection.GetField<IDictionary>(scheduler, "_pending");
            Assert.That(pending.Contains(characterId), "a save is waiting");
            return TestReflection.GetField<UpdateCharacterReq>(pending[characterId], "Request");
        }

        [Test]
        public void AFlushWritesEveryWaitingSaveAndLeavesNoneWaiting()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler(laneCount: 4);
            PlayerCharacterData rowan = NewCharacter("Rowan");
            PlayerCharacterData ash = NewCharacter("Ash");
            scheduler.Enqueue(Save(rowan, 5));
            scheduler.Enqueue(Save(ash, 6));
            scheduler.Enqueue(Save(rowan, 7));

            Assert.That(scheduler.PendingCount, Is.EqualTo(2), "one waiting save per character");
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(1), "nothing is written before the flush");

            TestAsync.Wait(_server.Manager.FlushCharacterSavesAsync());

            Assert.That(scheduler.PendingCount, Is.Zero);
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(7), "the newest snapshot is written");
            Assert.That(Loaded(ash.Id).Level, Is.EqualTo(6));
        }

        [Test]
        public void EachTickWritesAtMostItsShareAndTheRestOnLaterTicks()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler(laneCount: 1, maxSavesPerTick: 2);
            PlayerCharacterData[] characters = { NewCharacter("Rowan"), NewCharacter("Ash"), NewCharacter("Wren") };
            foreach (PlayerCharacterData character in characters)
                scheduler.Enqueue(Save(character, 3));

            TestAsync.Wait((UniTask)TestReflection.Invoke(scheduler, "ProcessNextLane"));
            Assert.That(scheduler.PendingCount, Is.EqualTo(characters.Length - 2));

            TestAsync.Wait((UniTask)TestReflection.Invoke(scheduler, "ProcessNextLane"));
            Assert.That(scheduler.PendingCount, Is.Zero);
            foreach (PlayerCharacterData character in characters)
                Assert.That(Loaded(character.Id).Level, Is.EqualTo(3), character.CharacterName);
        }

        [Test]
        public void ASaveThatIsNotDueYetNeitherUsesTheTicksShareNorHoldsUpTheOthers()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler(laneCount: 1, maxSavesPerTick: 1, minSaveIntervalSeconds: 60f);
            PlayerCharacterData rowan = NewCharacter("Rowan");
            PlayerCharacterData ash = NewCharacter("Ash");
            scheduler.Enqueue(Save(rowan, 2));
            TestAsync.Wait((UniTask)TestReflection.Invoke(scheduler, "ProcessNextLane"));
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(2));
            // Rowan waits out the minimum interval at the front of the lane, Ash is due behind it.
            scheduler.Enqueue(Save(rowan, 3));
            scheduler.Enqueue(Save(ash, 4));

            TestAsync.Wait((UniTask)TestReflection.Invoke(scheduler, "ProcessNextLane"));

            Assert.That(Loaded(ash.Id).Level, Is.EqualTo(4), "the due save is written on this tick");
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(2), "the early one still waits");
            Queue<string>[] lanes = TestReflection.GetField<Queue<string>[]>(scheduler, "_lanes");
            Assert.That(lanes[0], Is.EqualTo(new[] { rowan.Id }), "queued once, not once per pass");
        }

        [Test]
        public void AFailedSaveKeepsItsTablesAndIsWrittenByTheNextFlush()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler();
            PlayerCharacterData rowan = NewCharacter("Rowan");
            scheduler.Enqueue(Save(rowan, 2, TransactionUpdateCharacterState.Items));

            // The cache write throws, so the save counts as failed.
            _server.Manager.DatabaseCache = null;
            LogAssert.Expect(LogType.Error, new Regex("Deferred character save failed"));
            Assert.That(SaveNow(scheduler, rowan.Id), Is.False);
            _server.Manager.DatabaseCache = _server.Cache;

            scheduler.Enqueue(Save(rowan, 5, TransactionUpdateCharacterState.Character));
            Assert.That(Waiting(scheduler, rowan.Id).State, Is.EqualTo(TransactionUpdateCharacterState.Items | TransactionUpdateCharacterState.Character),
                "the retry still writes the items the failed save was for");
            TestAsync.Wait(_server.Manager.FlushCharacterSavesAsync());

            Assert.That(scheduler.PendingCount, Is.Zero);
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(5));
        }

        [Test]
        public void ASaveRightAfterAWrittenOneWaitsTheMinimumInterval()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler(minSaveIntervalSeconds: 60f);
            PlayerCharacterData rowan = NewCharacter("Rowan");
            scheduler.Enqueue(Save(rowan, 2));
            Assert.That(SaveNow(scheduler, rowan.Id), Is.True);

            scheduler.Enqueue(Save(rowan, 3));

            Assert.That(SaveNow(scheduler, rowan.Id), Is.False, "too soon after the last write");
            Assert.That(Loaded(rowan.Id).Level, Is.EqualTo(2));
            scheduler.Enqueue(Save(rowan, 4), forceImmediate: true);
            Assert.That(SaveNow(scheduler, rowan.Id), Is.True, "unless forced");
        }

        [Test]
        public void EveryCharacterIdCanBeQueued()
        {
            DatabaseCharacterSaveScheduler scheduler = _server.UseSaveScheduler(laneCount: 3);
            PlayerCharacterData character = TestCharacters.NewPlayer("Rowan");
            // Chosen so the scheduler's 31-based string hash comes to exactly int.MinValue.
            character.Id = "lane1tcezrz";

            Assert.DoesNotThrow(() => scheduler.Enqueue(Save(character, 2)));
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));
        }
    }
}
#endif
