using System;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Who may be offered a quest (Quest.CanReceiveQuest, which NPC dialogs use to decide
    /// whether to show it): a minimum level, a class, a faction and quests that must have been
    /// completed first - taken is not enough. A completed quest is offered again only as its
    /// repeat type allows: never, at any time, the next day or the next week. Getting this wrong
    /// either locks players out of a quest line or lets them farm a one-off reward.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class QuestRequirementTests : KitTestFixture
    {
        private PlayerCharacterData _player;
        private Quest _quest;

        [SetUp]
        public void SetUp()
        {
            _player = TestCharacters.NewPlayer();
            _quest = Objects.Create<Quest>("Errand");
        }

        private void Completed(Quest quest, DateTimeOffset when)
        {
            CharacterQuest done = CharacterQuest.Create(quest.DataId, 0);
            done.isComplete = true;
            done.completeTime = when.ToUnixTimeSeconds();
            _player.Quests.Add(done);
        }

        [Test]
        public void ACharacterBelowTheLevelIsNotOfferedIt()
        {
            _quest.requirement.level = 5;

            _player.Level = 4;
            Assert.That(_quest.CanReceiveQuest(_player), Is.False);

            _player.Level = 5;
            Assert.That(_quest.CanReceiveQuest(_player), Is.True);
        }

        [Test]
        public void APrerequisiteMustBeCompletedNotJustTaken()
        {
            Quest first = Objects.Create<Quest>("First");
            _quest.requirement.completedQuests = new[] { first };
            Assert.That(_quest.CanReceiveQuest(_player), Is.False, "never taken");

            _player.Quests.Add(CharacterQuest.Create(first.DataId, 0));
            Assert.That(_quest.CanReceiveQuest(_player), Is.False, "taken but not handed in");

            _player.Quests.Clear();
            Completed(first, DateTimeOffset.UtcNow);
            Assert.That(_quest.CanReceiveQuest(_player), Is.True);
        }

        [Test]
        public void AClassQuestIsOfferedOnlyToThatClass()
        {
            PlayerCharacter warrior = Objects.Create<PlayerCharacter>("Warrior");
            PlayerCharacter mage = Objects.Create<PlayerCharacter>("Mage");
            _quest.requirement.character = warrior;

            _player.DataId = mage.DataId;
            Assert.That(_quest.CanReceiveQuest(_player), Is.False);

            _player.DataId = warrior.DataId;
            Assert.That(_quest.CanReceiveQuest(_player), Is.True);
        }

        [Test]
        public void AFactionQuestIsOfferedOnlyToThatFaction()
        {
            Faction guard = Objects.Create<Faction>("Guard");
            Faction bandits = Objects.Create<Faction>("Bandits");
            _quest.requirement.faction = guard;

            _player.FactionId = bandits.DataId;
            Assert.That(_quest.CanReceiveQuest(_player), Is.False);

            _player.FactionId = guard.DataId;
            Assert.That(_quest.CanReceiveQuest(_player), Is.True);
        }

        [Test]
        public void ACompletedQuestThatDoesNotRepeatIsNeverOfferedAgain()
        {
            _quest.repeatType = QuestRepeatType.None;
            Completed(_quest, DateTimeOffset.UtcNow.AddDays(-30));

            Assert.That(_quest.CanReceiveQuest(_player), Is.False);
        }

        [Test]
        public void AQuestThatRepeatsAnyTimeIsOfferedAgainAtOnce()
        {
            _quest.repeatType = QuestRepeatType.AnyTime;
            Completed(_quest, DateTimeOffset.UtcNow);

            Assert.That(_quest.CanReceiveQuest(_player), Is.True);
        }

        [Test]
        public void ADailyQuestIsOfferedAgainOnlyOnALaterDay()
        {
            _quest.repeatType = QuestRepeatType.Daily;

            Completed(_quest, DateTimeOffset.UtcNow);
            Assert.That(_quest.CanReceiveQuest(_player), Is.False, "done today");

            _player.Quests.Clear();
            Completed(_quest, DateTimeOffset.UtcNow.AddDays(-2));
            Assert.That(_quest.CanReceiveQuest(_player), Is.True, "done two days ago");
        }

        [Test]
        public void AWeeklyQuestIsOfferedAgainOnlyInALaterWeek()
        {
            _quest.repeatType = QuestRepeatType.Weekly;

            Completed(_quest, DateTimeOffset.UtcNow);
            Assert.That(_quest.CanReceiveQuest(_player), Is.False, "done this week");

            _player.Quests.Clear();
            Completed(_quest, DateTimeOffset.UtcNow.AddDays(-8));
            Assert.That(_quest.CanReceiveQuest(_player), Is.True, "done more than a week ago");
        }

        [Test]
        public void ARepeatableQuestStillChecksItsOtherRequirements()
        {
            _quest.repeatType = QuestRepeatType.AnyTime;
            _quest.requirement.level = 10;
            Completed(_quest, DateTimeOffset.UtcNow);

            Assert.That(_quest.CanReceiveQuest(_player), Is.False);
        }
    }
}
