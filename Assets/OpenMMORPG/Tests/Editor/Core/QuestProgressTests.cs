using LiteNetLibManager;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// How a quest in the log counts towards its tasks (CharacterQuest.GetProgress,
    /// AddKillMonster, IsAllTasksDone). A kill counts only for the monster a task names, a task is
    /// done only at its full amount, collect tasks read the bag as it is now, talk tasks only once
    /// the NPC was spoken to, and the quest is done only when every task of the character's own
    /// task set is. Counting early hands out rewards for work not done; counting late strands a
    /// player at an NPC who will not take the quest back.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class QuestProgressTests : KitTestFixture
    {
        private PlayerCharacterData _player;
        private MonsterCharacter _wolf;
        private MonsterCharacter _bandit;
        private Item _pelt;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _player = TestInventory.Player(Objects);
            _wolf = Objects.Create<MonsterCharacter>("Wolf");
            _bandit = Objects.Create<MonsterCharacter>("Bandit");
            _pelt = TestInventory.Item(Objects, maxStack: 5, name: "Pelt");
        }

        private static QuestTask KillTask(MonsterCharacter monster, int amount)
        {
            return new QuestTask
            {
                taskType = QuestTaskType.KillMonster,
                monsterCharacterAmount = new MonsterCharacterAmount { monster = monster, amount = amount },
            };
        }

        private static QuestTask CollectTask(BaseItem item, int amount)
        {
            return new QuestTask { taskType = QuestTaskType.CollectItem, itemAmount = new ItemAmount { item = item, amount = amount } };
        }

        private static QuestTask TalkTask(int npcEntityId, bool completeAfterTalked = false)
        {
            return new QuestTask { taskType = QuestTaskType.TalkToNpc, npcEntityId = npcEntityId, completeAfterTalked = completeAfterTalked };
        }

        /// <summary>A registered quest with one task set per argument; the character takes set <paramref name="setIndex"/>.</summary>
        private Quest Take(byte setIndex, params QuestTask[][] taskSets)
        {
            Quest quest = Objects.Create<Quest>();
            quest.randomTasks = new QuestTasks[taskSets.Length];
            for (int i = 0; i < taskSets.Length; ++i)
                quest.randomTasks[i] = new QuestTasks { tasks = taskSets[i] };
            GameInstance.Quests[quest.DataId] = quest;
            _player.Quests.Add(CharacterQuest.Create(quest.DataId, setIndex));
            return quest;
        }

        private Quest Take(params QuestTask[] tasks)
        {
            return Take(0, tasks);
        }

        private bool Kill(MonsterCharacter monster, int count)
        {
            CharacterQuest quest = _player.Quests[0];
            bool counted = quest.AddKillMonster(monster.DataId, count);
            _player.Quests[0] = quest;
            return counted;
        }

        private int Progress(int taskIndex, out bool isComplete)
        {
            return _player.Quests[0].GetProgress(_player, taskIndex, out isComplete);
        }

        private bool AllDone()
        {
            return _player.Quests[0].IsAllTasksDone(_player, out _);
        }

        private NpcEntity Npc(string assetId)
        {
            NpcEntity npc = Objects.NewGameObject(assetId).AddComponent<NpcEntity>();
            TestReflection.SetField(npc.GetComponent<LiteNetLibIdentity>(), "assetId", assetId);
            return npc;
        }

        [Test]
        public void AKillTaskIsDoneOnlyAtItsFullAmount()
        {
            Take(KillTask(_wolf, 3));

            Kill(_wolf, 2);
            Assert.That(Progress(0, out bool isComplete), Is.EqualTo(2));
            Assert.That(isComplete, Is.False, "one wolf short");

            Kill(_wolf, 1);
            Progress(0, out isComplete);
            Assert.That(isComplete, Is.True);
        }

        [Test]
        public void KillsOfAMonsterNoTaskNamesAreNotCounted()
        {
            Take(KillTask(_wolf, 3));

            Assert.That(Kill(_bandit, 5), Is.False);
            Assert.That(Progress(0, out _), Is.Zero);
        }

        [Test]
        public void EachKillTaskCountsOnlyItsOwnMonster()
        {
            Take(KillTask(_wolf, 2), KillTask(_bandit, 2));

            Kill(_wolf, 4);

            Assert.That(Progress(1, out bool banditsDone), Is.Zero, "wolves do not count as bandits");
            Assert.That(banditsDone, Is.False);
            Assert.That(AllDone(), Is.False);
        }

        [Test]
        public void ACollectTaskCountsEveryStackButNotWornItems()
        {
            ArmorItem helm = TestGear.Equipment<ArmorItem>(Objects, default);
            Take(CollectTask(_pelt, 7), CollectTask(helm, 1));
            _player.IncreaseItems(CharacterItem.Create(_pelt, 1, 7));
            _player.EquipItems.Add(CharacterItem.Create(helm));

            Assert.That(Progress(0, out bool peltsDone), Is.EqualTo(7), "a full stack of five and two more");
            Assert.That(peltsDone, Is.True);
            Assert.That(Progress(1, out bool helmDone), Is.Zero, "the helm being worn is not handed in");
            Assert.That(helmDone, Is.False);
        }

        [Test]
        public void ACollectTaskIsUndoneWhenTheItemsLeaveTheBag()
        {
            Take(CollectTask(_pelt, 3));
            _player.IncreaseItems(CharacterItem.Create(_pelt, 1, 3));
            Assert.That(AllDone(), Is.True);

            _player.DecreaseItems(_pelt.DataId, 1);

            Assert.That(AllDone(), Is.False, "sold or dropped a pelt, so the quest cannot be handed in");
        }

        [Test]
        public void ATalkTaskIsDoneOnlyOnceTheNpcHasBeenSpokenTo()
        {
            Take(KillTask(_wolf, 1), TalkTask(npcEntityId: 42));
            Kill(_wolf, 1);
            Assert.That(AllDone(), Is.False);

            _player.Quests[0].completedTasks.Add(1);

            Assert.That(Progress(1, out bool talked), Is.EqualTo(1));
            Assert.That(talked, Is.True);
            Assert.That(AllDone(), Is.True);
        }

        [Test]
        public void ATaskThatCompletesOnTalkingIsHandedInOnlyAtThatNpc()
        {
            NpcEntity elder = Npc("Elder");
            NpcEntity smith = Npc("Smith");
            Take(KillTask(_wolf, 1), TalkTask(elder.EntityId, completeAfterTalked: true));
            Kill(_wolf, 1);
            CharacterQuest quest = _player.Quests[0];

            Assert.That(quest.IsAllTasksDoneAndIsCompletingTarget(_player, smith), Is.False, "the wrong NPC");
            Assert.That(quest.IsAllTasksDoneAndIsCompletingTarget(_player, null), Is.False, "no NPC at all");
            Assert.That(quest.IsAllTasksDoneAndIsCompletingTarget(_player, elder), Is.True);
        }

        [Test]
        public void ATaskThatCompletesOnTalkingStillWaitsForTheOtherTasks()
        {
            NpcEntity elder = Npc("Elder");
            Take(KillTask(_wolf, 2), TalkTask(elder.EntityId, completeAfterTalked: true));
            Kill(_wolf, 1);

            Assert.That(_player.Quests[0].IsAllTasksDoneAndIsCompletingTarget(_player, elder), Is.False);
        }

        [Test]
        public void OnlyTheCharactersOwnTaskSetCounts()
        {
            Take(1, new[] { KillTask(_wolf, 1) }, new[] { KillTask(_bandit, 1) });

            Kill(_wolf, 1);
            Assert.That(AllDone(), Is.False, "this character drew the bandit task, not the wolf task");

            Kill(_bandit, 1);
            Assert.That(AllDone(), Is.True);
        }

        [Test]
        public void ATaskSetThatNoLongerExistsFallsBackToTheFirst()
        {
            // The character's saved set index outlived a designer removing that set.
            Take(5, new[] { KillTask(_wolf, 1) });

            Assert.That(AllDone(), Is.False);
            Kill(_wolf, 1);
            Assert.That(AllDone(), Is.True);
        }

        [Test]
        public void AQuestNotInTheGameDataIsNeverDone()
        {
            _player.Quests.Add(CharacterQuest.Create(Objects.Create<Quest>("Removed").DataId, 0));

            Assert.That(_player.Quests[0].IsAllTasksDone(_player, out _), Is.False);
        }

        [Test]
        public void AQuestNotInTheGameDataReadsNoProgress()
        {
            _player.Quests.Add(CharacterQuest.Create(Objects.Create<Quest>("Removed").DataId, 0));

            Assert.That(Progress(0, out bool isComplete), Is.Zero);
            Assert.That(isComplete, Is.False);
        }

        [Test]
        public void OnlyKillTasksWithAMonsterAndAnAmountListenForKills()
        {
            Quest quest = Objects.Create<Quest>();
            quest.randomTasks = new[]
            {
                new QuestTasks { tasks = new[] { KillTask(_wolf, 1), CollectTask(_pelt, 1) } },
                new QuestTasks { tasks = new[] { KillTask(_bandit, 0), KillTask(null, 3) } },
            };

            Assert.That(quest.CacheKillMonsterIds, Is.EquivalentTo(new[] { _wolf.DataId }));
        }

        [Test]
        public void AClonedQuestKeepsItsOwnProgress()
        {
            CharacterQuest original = CharacterQuest.Create(1, 0);
            original.killedMonsters[7] = 2;
            original.completedTasks.Add(0);

            CharacterQuest clone = original.Clone();
            clone.killedMonsters[7] = 9;
            clone.completedTasks.Add(1);

            Assert.That(original.killedMonsters[7], Is.EqualTo(2));
            Assert.That(original.completedTasks, Is.EqualTo(new[] { 0 }));
        }
    }
}
