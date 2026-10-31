using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Taking and dropping quests on the server (BasePlayerCharacterEntity.AcceptQuest and
    /// AbandonQuest). A quest is in the log at most once, taking a repeatable quest again starts
    /// it from nothing, taking a quest drops the in-progress quests it replaces, and abandoning
    /// removes only a quest still under way: a completed quest stays in the log, because that
    /// entry is what stops a one-off quest from being offered again.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class QuestAcceptAndAbandonTests : KitTestFixture
    {
        private PlayerCharacterEntity _player;
        private MonsterCharacter _wolf;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            TestInventory.UseServerLog(Objects);
            TestGameMessages.Use(Objects);
            _player = TestCombatants.Create<PlayerCharacterEntity>(Objects);
            _wolf = Objects.Create<MonsterCharacter>("Wolf");
        }

        private Quest Quest(string name)
        {
            Quest quest = Objects.Create<Quest>(name);
            quest.randomTasks = new[]
            {
                new QuestTasks
                {
                    tasks = new[]
                    {
                        new QuestTask
                        {
                            taskType = QuestTaskType.KillMonster,
                            monsterCharacterAmount = new MonsterCharacterAmount { monster = _wolf, amount = 3 },
                        },
                    },
                },
            };
            GameInstance.Quests[quest.DataId] = quest;
            return quest;
        }

        private CharacterQuest Entry(Quest quest)
        {
            return _player.Quests[_player.IndexOfQuest(quest.DataId)];
        }

        private void MarkComplete(Quest quest)
        {
            int index = _player.IndexOfQuest(quest.DataId);
            CharacterQuest entry = _player.Quests[index];
            entry.isComplete = true;
            _player.Quests[index] = entry;
        }

        [Test]
        public void AQuestIsInTheLogOnlyOnce()
        {
            Quest errand = Quest("Errand");

            _player.AcceptQuest(errand.DataId);
            _player.AcceptQuest(errand.DataId);

            Assert.That(_player.Quests, Has.Count.EqualTo(1));
            Assert.That(Entry(errand).isComplete, Is.False);
        }

        [Test]
        public void AQuestTheGameDoesNotKnowIsNotTaken()
        {
            _player.AcceptQuest(Objects.Create<Quest>("Unregistered").DataId);

            Assert.That(_player.Quests, Is.Empty);
        }

        [Test]
        public void TakingARepeatableQuestAgainStartsItFromNothing()
        {
            Quest errand = Quest("Errand");
            errand.repeatType = QuestRepeatType.AnyTime;
            _player.AcceptQuest(errand.DataId);
            CharacterQuest entry = Entry(errand);
            entry.AddKillMonster(_wolf.DataId, 3);
            _player.Quests[0] = entry;
            MarkComplete(errand);

            _player.AcceptQuest(errand.DataId);

            Assert.That(_player.Quests, Has.Count.EqualTo(1));
            Assert.That(Entry(errand).isComplete, Is.False);
            Assert.That(Entry(errand).CountKillMonster(_wolf.DataId), Is.Zero, "last time's kills do not count again");
        }

        [Test]
        public void TakingAQuestDropsTheQuestsItReplacesUnlessTheyAreDone()
        {
            Quest north = Quest("NorthPath");
            Quest east = Quest("EastPath");
            Quest south = Quest("SouthPath");
            south.abandonQuests = new[] { north, east };
            _player.AcceptQuest(north.DataId);
            _player.AcceptQuest(east.DataId);
            MarkComplete(east);

            _player.AcceptQuest(south.DataId);

            Assert.That(_player.IndexOfQuest(north.DataId), Is.LessThan(0), "in progress, so dropped");
            Assert.That(_player.IndexOfQuest(east.DataId), Is.GreaterThanOrEqualTo(0), "already done, so kept");
            Assert.That(_player.IndexOfQuest(south.DataId), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void AnAutoTrackedQuestIsTrackedWhenTaken()
        {
            Quest tracked = Quest("Tracked");
            tracked.autoTrackQuest = true;
            Quest plain = Quest("Plain");

            _player.AcceptQuest(tracked.DataId);
            _player.AcceptQuest(plain.DataId);

            Assert.That(Entry(tracked).isTracking, Is.True);
            Assert.That(Entry(plain).isTracking, Is.False);
        }

        [Test]
        public void AbandoningDropsAQuestUnderWayButKeepsACompletedOne()
        {
            Quest underWay = Quest("UnderWay");
            Quest done = Quest("Done");
            _player.AcceptQuest(underWay.DataId);
            _player.AcceptQuest(done.DataId);
            MarkComplete(done);

            _player.AbandonQuest(underWay.DataId);
            _player.AbandonQuest(done.DataId);

            Assert.That(_player.IndexOfQuest(underWay.DataId), Is.LessThan(0));
            Assert.That(_player.IndexOfQuest(done.DataId), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void AQuestThatCannotBeAbandonedStaysInTheLog()
        {
            // The dialog hides the Abandon button for such a quest, but the server's abandon
            // handler calls AbandonQuest without checking, so this is the only guard.
            Quest oath = Quest("Oath");
            oath.canAbandon = false;
            _player.AcceptQuest(oath.DataId);

            _player.AbandonQuest(oath.DataId);

            Assert.That(_player.IndexOfQuest(oath.DataId), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void AbandoningAQuestTheGameDoesNotKnowDoesNothing()
        {
            Quest errand = Quest("Errand");
            _player.AcceptQuest(errand.DataId);

            Assert.DoesNotThrow(() => _player.AbandonQuest(Objects.Create<Quest>("Unregistered").DataId));
            Assert.That(_player.Quests, Has.Count.EqualTo(1));
        }
    }
}
