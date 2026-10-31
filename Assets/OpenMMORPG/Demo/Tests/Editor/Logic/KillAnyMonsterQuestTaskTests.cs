using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// Rowan's "Thin the Camp" counts every bandit family towards one total. Before
    /// KillAnyMonsterQuestTask, archers and marauders counted for nothing (2026-10-02).
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class KillAnyMonsterQuestTaskTests : KitTestFixture
    {
        private MonsterCharacter _bandit;
        private MonsterCharacter _archer;
        private MonsterCharacter _wolf;
        private Quest _quest;
        private PlayerCharacterData _player;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _bandit = Objects.Create<MonsterCharacter>("TestBandit");
            _archer = Objects.Create<MonsterCharacter>("TestBanditArcher");
            _wolf = Objects.Create<MonsterCharacter>("TestWolf");

            KillAnyMonsterQuestTask killAny = Objects.Create<KillAnyMonsterQuestTask>();
            killAny.title = "Bandits";
            killAny.monsters = new[] { _bandit, _archer };
            killAny.amount = 5;

            _quest = Objects.Create<Quest>("TestThinTheCamp");
            _quest.randomTasks = new[]
            {
                new QuestTasks()
                {
                    tasks = new[]
                    {
                        new QuestTask() { taskType = QuestTaskType.Custom, customQuestTask = killAny },
                    },
                },
            };
            GameInstance.Quests[_quest.DataId] = _quest;
            // What the demo does once the game data has loaded.
            TestReflection.InvokeStatic(typeof(KillAnyMonsterQuestTask), "RegisterKillIds");

            _player = TestCharacters.NewPlayer();
            _player.Quests.Add(CharacterQuest.Create(_quest.DataId, 0));
        }

        private bool Kill(MonsterCharacter monster, int count)
        {
            CharacterQuest quest = _player.Quests[0];
            bool counted = quest.AddKillMonster(monster.DataId, count);
            _player.Quests[0] = quest;
            return counted;
        }

        private int Progress(out bool isComplete)
        {
            return _player.Quests[0].GetProgress(_player, 0, out _, out _, out isComplete);
        }

        [Test]
        public void KillsOfEveryListedFamilyCountTowardsOneTotal()
        {
            Kill(_bandit, 2);
            Kill(_archer, 1);

            Assert.That(Progress(out bool isComplete), Is.EqualTo(3));
            Assert.That(isComplete, Is.False);
        }

        [Test]
        public void AMonsterNotListedDoesNotCount()
        {
            Assert.That(Kill(_wolf, 3), Is.False);
            Assert.That(Progress(out _), Is.EqualTo(0));
        }

        [Test]
        public void ProgressStopsAtTheAmountAndCompletes()
        {
            Kill(_bandit, 4);
            Kill(_archer, 5);

            Assert.That(Progress(out bool isComplete), Is.EqualTo(5), "reads 5/5, not 9/5");
            Assert.That(isComplete, Is.True);
        }
    }
}
