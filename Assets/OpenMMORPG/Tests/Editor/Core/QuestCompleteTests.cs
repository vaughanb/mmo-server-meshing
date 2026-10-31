using System;
using System.Linq;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Handing a quest in (BasePlayerCharacterEntity.CompleteQuest). It is refused until every
    /// task is done, and a refusal changes nothing. A hand-in takes the collected items (unless
    /// the task keeps them), gives the fixed rewards, exactly the one selectable reward chosen
    /// and one weighted random reward, and stat and skill points - all once: a completed quest
    /// cannot be handed in again. Rewards that will not fit refuse the hand-in before anything
    /// is taken. Exp and gold go through RewardExp/RewardGold, which act only on a running
    /// server, so here only the rule's MakeQuestReward is checked; LevelUpAndRewardTests covers
    /// the payout.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class QuestCompleteTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private TestGameMessages _messages;
        private PlayerCharacterEntity _player;
        private MonsterCharacter _wolf;
        private Item _pelt;
        private Item _potion;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _messages = TestGameMessages.Use(Objects);
            // A slot-limited bag never closes gaps, so removing a used-up stack shifts nothing.
            _gameInstance.Instance.noInventoryWeightLimit = true;
            TestInventory.LimitSlots(_gameInstance, 10);
            PlayerCharacter adventurer = Objects.Create<PlayerCharacter>("Adventurer");
            GameInstance.Characters[adventurer.DataId] = adventurer;
            _player = TestCombatants.Create<PlayerCharacterEntity>(Objects);
            _player.DataId = adventurer.DataId;
            // Work the slot limit out from the game data rather than the combat-test stats.
            _player.MarkToMakeCaches();
            _wolf = Objects.Create<MonsterCharacter>("Wolf");
            _pelt = TestInventory.Item(Objects, maxStack: 10, name: "Pelt");
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
        }

        private static QuestTask KillWolves(MonsterCharacter wolf, int amount)
        {
            return new QuestTask
            {
                taskType = QuestTaskType.KillMonster,
                monsterCharacterAmount = new MonsterCharacterAmount { monster = wolf, amount = amount },
            };
        }

        private QuestTask CollectPelts(int amount, bool keep = false)
        {
            return new QuestTask
            {
                taskType = QuestTaskType.CollectItem,
                itemAmount = new ItemAmount { item = _pelt, amount = amount },
                doNotDecreaseItemsOnQuestComplete = keep,
            };
        }

        private Quest Take(params QuestTask[] tasks)
        {
            Quest quest = Objects.Create<Quest>();
            quest.randomTasks = new[] { new QuestTasks { tasks = tasks } };
            GameInstance.Quests[quest.DataId] = quest;
            _player.Quests.Add(CharacterQuest.Create(quest.DataId, 0));
            return quest;
        }

        private void Kill(Quest quest, MonsterCharacter monster, int count)
        {
            int index = _player.IndexOfQuest(quest.DataId);
            CharacterQuest entry = _player.Quests[index];
            entry.AddKillMonster(monster.DataId, count);
            _player.Quests[index] = entry;
        }

        private void Give(Item item, int amount)
        {
            _player.IncreaseItems(CharacterItem.Create(item, 1, amount));
        }

        private CharacterQuest Entry(Quest quest)
        {
            return _player.Quests[_player.IndexOfQuest(quest.DataId)];
        }

        private static ItemAmount Reward(BaseItem item, int amount)
        {
            return new ItemAmount { item = item, level = 1, amount = amount };
        }

        [Test]
        public void AQuestWithATaskLeftIsNotHandedInAndNothingChanges()
        {
            Quest quest = Take(KillWolves(_wolf, 3), CollectPelts(2));
            quest.rewardItems = new[] { Reward(_potion, 1) };
            quest.rewardStatPoints = 2;
            Kill(quest, _wolf, 2);
            Give(_pelt, 2);

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.False);

            Assert.That(Entry(quest).isComplete, Is.False);
            Assert.That(TestInventory.Count(_player, _pelt), Is.EqualTo(2), "the pelts are not taken");
            Assert.That(TestInventory.Count(_player, _potion), Is.Zero);
            Assert.That(_player.StatPoint, Is.Zero);
        }

        [Test]
        public void HandingInTakesTheCollectedItemsAndGivesTheRewards()
        {
            Quest quest = Take(CollectPelts(3));
            quest.rewardItems = new[] { Reward(_potion, 2) };
            quest.rewardStatPoints = 3;
            quest.rewardSkillPoints = 1;
            Give(_pelt, 5);
            long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.True);

            Assert.That(TestInventory.Count(_player, _pelt), Is.EqualTo(2), "three of five handed in");
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(2));
            Assert.That(_player.StatPoint, Is.EqualTo(3f));
            Assert.That(_player.SkillPoint, Is.EqualTo(1f));
            Assert.That(Entry(quest).isComplete, Is.True);
            Assert.That(Entry(quest).completeTime, Is.InRange(before, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Assert.That(_messages.RewardedItems, Is.EqualTo(new[] { (_potion.DataId, 2) }), "the player is told once");
        }

        [Test]
        public void ATaskThatKeepsItsItemsLeavesThemInTheBag()
        {
            Quest quest = Take(CollectPelts(3, keep: true));
            Give(_pelt, 3);

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.True);

            Assert.That(TestInventory.Count(_player, _pelt), Is.EqualTo(3));
        }

        [Test]
        public void AQuestIsHandedInOnlyOnce()
        {
            Quest quest = Take(KillWolves(_wolf, 1));
            quest.rewardItems = new[] { Reward(_potion, 1) };
            quest.rewardStatPoints = 1;
            Kill(quest, _wolf, 1);

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.True);
            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.False, "the kills still count, but it is done");

            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(1));
            Assert.That(_player.StatPoint, Is.EqualTo(1f));
        }

        [Test]
        public void OnlyTheChosenSelectableRewardIsGiven()
        {
            Item sword = TestInventory.Item(Objects, name: "Sword");
            Item shield = TestInventory.Item(Objects, name: "Shield");
            Quest quest = Take(KillWolves(_wolf, 1));
            quest.selectableRewardItems = new[] { Reward(sword, 1), Reward(shield, 1) };
            Kill(quest, _wolf, 1);

            Assert.That(_player.CompleteQuest(quest.DataId, 1), Is.True);

            Assert.That(TestInventory.Count(_player, shield), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, sword), Is.Zero);
        }

        [Test]
        public void AChoiceThatIsNotOnTheListIsRefusedAndTakesNothing()
        {
            Item sword = TestInventory.Item(Objects, name: "Sword");
            Quest quest = Take(CollectPelts(2));
            quest.selectableRewardItems = new[] { Reward(sword, 1) };
            Give(_pelt, 2);

            Assert.That(_player.CompleteQuest(quest.DataId, 1), Is.False);

            Assert.That(_messages.Messages, Does.Contain(UITextKeys.UI_ERROR_INVALID_ITEM_INDEX));
            Assert.That(Entry(quest).isComplete, Is.False);
            Assert.That(TestInventory.Count(_player, _pelt), Is.EqualTo(2));
            Assert.That(TestInventory.Count(_player, sword), Is.Zero);
        }

        [Test]
        public void RewardsThatWillNotFitRefuseTheHandInBeforeAnythingIsTaken()
        {
            _gameInstance.Instance.baseSlotLimit = 2;
            _player.MarkToMakeCaches();
            Item sword = TestInventory.Item(Objects, name: "Sword");
            Quest quest = Take(CollectPelts(2));
            quest.rewardItems = new[] { Reward(sword, 1) };
            Give(_pelt, 2);
            Give(_potion, 1);

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.False);

            Assert.That(_messages.Messages, Does.Contain(UITextKeys.UI_ERROR_WILL_OVERWHELMING));
            Assert.That(Entry(quest).isComplete, Is.False);
            Assert.That(TestInventory.Count(_player, _pelt), Is.EqualTo(2));
        }

        [Test]
        public void ARandomRewardGivesOneEntryAndNeverOneWithoutWeight()
        {
            Item gem = TestInventory.Item(Objects, maxStack: 10, name: "Gem");
            Item coal = TestInventory.Item(Objects, maxStack: 10, name: "Coal");
            Quest quest = Take(KillWolves(_wolf, 1));
            quest.randomRewardItems = new[]
            {
                new ItemRandomByWeight { item = gem, maxAmount = 1, randomWeight = 0 },
                new ItemRandomByWeight { item = coal, maxAmount = 4, randomWeight = 1 },
            };
            Kill(quest, _wolf, 1);

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.True);

            Assert.That(TestInventory.Count(_player, gem), Is.Zero);
            Assert.That(TestInventory.Count(_player, coal), Is.EqualTo(4));
        }

        [Test]
        public void HandingInAssignsTheNextQuestsNotAlreadyInTheLog()
        {
            Quest next = Objects.Create<Quest>("Next");
            Quest alsoNext = Objects.Create<Quest>("AlsoNext");
            GameInstance.Quests[next.DataId] = next;
            GameInstance.Quests[alsoNext.DataId] = alsoNext;
            Quest quest = Take(KillWolves(_wolf, 1));
            quest.nextAssignQuests = new[] { next, alsoNext };
            Kill(quest, _wolf, 1);
            _player.Quests.Add(CharacterQuest.Create(alsoNext.DataId, 0));

            Assert.That(_player.CompleteQuest(quest.DataId, 0), Is.True);

            Assert.That(_player.IndexOfQuest(next.DataId), Is.GreaterThanOrEqualTo(0));
            Assert.That(_player.Quests.Count(q => q.dataId == alsoNext.DataId), Is.EqualTo(1), "not taken twice");
        }

        [Test]
        public void TheRuleRewardsTheQuestsExpGoldAndCurrencies()
        {
            Currency fame = Objects.Create<Currency>("Fame");
            Quest quest = Objects.Create<Quest>();
            quest.rewardExp = 120;
            quest.rewardGold = 35;
            quest.rewardCurrencies = new[] { new CurrencyAmount { currency = fame, amount = 4 } };

            Reward reward = _gameInstance.GameplayRule.MakeQuestReward(quest);

            Assert.That(reward.exp, Is.EqualTo(quest.rewardExp));
            Assert.That(reward.gold, Is.EqualTo(quest.rewardGold));
#if !DISABLE_CUSTOM_CHARACTER_CURRENCIES
            Assert.That(reward.currencies, Is.EqualTo(quest.rewardCurrencies));
#endif
        }
    }
}
