using System.Linq;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Adding items to and taking them out of a bag (DefaultInventoryManager through
    /// IncreaseItems/DecreaseItems). Whatever goes in must come out in stacks no bigger than the
    /// item's MaxStack and with nothing lost or made up; taking items must take exactly what was
    /// asked from that item and nothing from its neighbours, or nothing at all when there is not
    /// enough. Every shop, loot, craft and quest hand-in goes through these two calls.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class InventoryStackingTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private Item _potion;
        private Item _sword;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _player = TestInventory.Player(Objects);
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
            _sword = TestInventory.Item(Objects, maxStack: 1, name: "Sword");
        }

        private int[] PotionStacks()
        {
            return _player.NonEquipItems.Where(i => i.dataId == _potion.DataId && i.amount > 0).Select(i => i.amount).ToArray();
        }

        [Test]
        public void AddingMoreThanAStackFillsWholeStacksAndNoneOverflows()
        {
            Assert.That(_player.IncreaseItems(CharacterItem.Create(_potion, 1, 25)), Is.True);

            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(25), "nothing lost or made up");
            Assert.That(PotionStacks(), Has.All.LessThanOrEqualTo(_potion.MaxStack));
            Assert.That(PotionStacks(), Has.Length.EqualTo(3), "two full stacks and the rest");
        }

        [Test]
        public void AddingTopsUpAPartStackBeforeStartingANewOne()
        {
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 7));
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 5));

            Assert.That(PotionStacks(), Is.EqualTo(new[] { 10, 2 }));
        }

        [Test]
        public void ItemsOfDifferentLevelsAreNotStackedTogether()
        {
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 2));
            _player.IncreaseItems(CharacterItem.Create(_potion, 2, 2));

            Assert.That(_player.NonEquipItems.Select(i => i.level), Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void AnItemTheGameDoesNotKnowIsNotAdded()
        {
            Item unknown = Objects.Create<Item>("Unregistered");

            Assert.That(_player.IncreaseItems(CharacterItem.Create(unknown, 1, 1)), Is.False);
            Assert.That(_player.NonEquipItems, Is.Empty);
        }

        [Test]
        public void TheGoldRepresentItemNeverLandsInTheBag()
        {
            // Coins on the ground or in a corpse stand for gold; picking them up must credit the
            // purse, never put a coin item in the bag that could then be sold.
            Item coins = TestInventory.Item(Objects, maxStack: 1000, name: "GoldCoins");
            TestReflection.SetField(_gameInstance.Instance, "goldDropRepresentItem", coins);

            Assert.That(_player.IncreaseItems(CharacterItem.Create(coins, 1, 3)), Is.False);
            Assert.That(TestInventory.Count(_player, coins), Is.Zero);
        }

        [Test]
        public void RemovingTakesFromEveryStackItNeedsInASlotLimitedBag()
        {
            TestInventory.LimitSlots(_gameInstance, 5);
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 13));
            _player.IncreaseItems(CharacterItem.Create(_sword));
            int swordSlot = _player.IndexOfNonEquipItem(_sword.DataId);

            Assert.That(_player.DecreaseItems(_potion.DataId, 12), Is.True);

            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(1));
            Assert.That(_player.NonEquipItems[swordSlot].dataId, Is.EqualTo(_sword.DataId), "the sword kept its slot");
        }

        [Test]
        public void RemovingAcrossStacksLeavesOtherItemsAloneInAnUnlimitedBag()
        {
            // The kit's default bag (InventorySystem.Simple) closes the gap a used-up stack leaves.
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 13));
            _player.IncreaseItems(CharacterItem.Create(_sword));

            Assert.That(_player.DecreaseItems(_potion.DataId, 12), Is.True);

            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(1), "the sword must not be taken");
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(1), "exactly twelve potions taken");
        }

        [Test]
        public void RemovingMoreThanTheBagHoldsRemovesNothing()
        {
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 13));

            Assert.That(_player.DecreaseItems(_potion.DataId, 14), Is.False);
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(13));
        }

        [Test]
        public void TakingMoreThanASlotHoldsIsRefusedUnlessAskedToTakeWhatIsThere()
        {
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 3));

            Assert.That(_player.NonEquipItems.DecreaseItemsByIndex(0, 5, false, adjustMaxAmount: false), Is.False);
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(3), "refused take leaves the stack");

            Assert.That(_player.NonEquipItems.DecreaseItemsByIndex(0, 5, false, adjustMaxAmount: true), Is.True);
            Assert.That(TestInventory.Count(_player, _potion), Is.Zero, "adjusted take empties the stack and no more");
        }

        [Test]
        public void InASlotLimitedBagAnEmptiedSlotStaysWhereItWas()
        {
            TestInventory.LimitSlots(_gameInstance, 3);
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 3));
            _player.IncreaseItems(CharacterItem.Create(_sword));
            _player.FillEmptySlots();

            _player.DecreaseItems(_potion.DataId, 3);
            _player.FillEmptySlots();

            Assert.That(_player.NonEquipItems, Has.Count.EqualTo(3), "the bag keeps its slot count");
            Assert.That(_player.NonEquipItems[0].IsEmptySlot(), Is.True);
            Assert.That(_player.NonEquipItems[1].dataId, Is.EqualTo(_sword.DataId));
        }
    }
}
