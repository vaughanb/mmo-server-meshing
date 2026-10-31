using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// The "how is it going" dialog line (QuestUnfinishedCondition) shows only while the quest is
    /// taken and its tasks are not yet done. Once they are done the hand-in line must be the only
    /// one, or players miss the Complete button (Hilde's venison, 2026-09-24); before the quest
    /// is taken, and after it is handed in, the line does not show at all.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class QuestUnfinishedConditionTests : KitTestFixture
    {
        private PlayerCharacterData _player;
        private Item _venison;
        private Quest _quest;
        private QuestUnfinishedCondition _condition;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _player = TestInventory.Player(Objects);
            _venison = TestInventory.Item(Objects, maxStack: 10, name: "Venison");
            _quest = Objects.Create<Quest>("TestVenison");
            _quest.randomTasks = new[]
            {
                new QuestTasks
                {
                    tasks = new[]
                    {
                        new QuestTask { taskType = QuestTaskType.CollectItem, itemAmount = new ItemAmount { item = _venison, amount = 4 } },
                    },
                },
            };
            GameInstance.Quests[_quest.DataId] = _quest;
            _condition = Objects.Create<QuestUnfinishedCondition>();
            _condition.quest = _quest;
        }

        private bool Shows()
        {
            return TestAsync.Wait(_condition.IsPass(_player));
        }

        [Test]
        public void ItDoesNotShowBeforeTheQuestIsTaken()
        {
            Assert.That(Shows(), Is.False);
        }

        [Test]
        public void ItShowsWhileTheTasksAreUnderWay()
        {
            _player.Quests.Add(CharacterQuest.Create(_quest.DataId, 0));
            _player.IncreaseItems(CharacterItem.Create(_venison, 1, 3));

            Assert.That(Shows(), Is.True);
        }

        [Test]
        public void ItStopsShowingOnceTheTasksAreDone()
        {
            _player.Quests.Add(CharacterQuest.Create(_quest.DataId, 0));
            _player.IncreaseItems(CharacterItem.Create(_venison, 1, 4));

            Assert.That(Shows(), Is.False, "the hand-in line takes over");
        }

        [Test]
        public void ItDoesNotShowOnceTheQuestIsHandedIn()
        {
            CharacterQuest done = CharacterQuest.Create(_quest.DataId, 0);
            done.isComplete = true;
            _player.Quests.Add(done);

            Assert.That(Shows(), Is.False);
        }
    }
}
