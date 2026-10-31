using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The "will it fit" check (IncreasingItemsWillOverwhelming) that every purchase, loot and
    /// reward asks before it adds items. IncreaseItems itself never refuses for room, so a check
    /// that says "fits" when it does not lets a bag grow past its slots, and one that says "full"
    /// when it is not costs the player a purchase or a reward. Also: taking gear off into a full
    /// bag must be refused, not lose the piece.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class InventoryLimitsTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private Item _potion;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _gameInstance.Instance.noInventoryWeightLimit = true;
            _player = TestInventory.Player(Objects);
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
        }

        private Item Junk(string name)
        {
            return TestInventory.Item(Objects, name: name);
        }

        [Test]
        public void AFullBagRefusesAnItemThatNeedsANewSlot()
        {
            TestInventory.LimitSlots(_gameInstance, 2);
            _player.IncreaseItems(CharacterItem.Create(Junk("Rock")));
            _player.IncreaseItems(CharacterItem.Create(Junk("Stick")));

            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 1), Is.True);
        }

        [Test]
        public void AFullBagStillTakesWhatFitsOnAPartStack()
        {
            TestInventory.LimitSlots(_gameInstance, 2);
            _player.IncreaseItems(CharacterItem.Create(_potion, 1, 4));
            _player.IncreaseItems(CharacterItem.Create(Junk("Rock")));

            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 6), Is.False, "tops the stack up to ten");
            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 7), Is.True, "the seventh needs a third slot");
        }

        [Test]
        public void AnAmountIsCountedInWholeStacksAgainstTheFreeSlots()
        {
            TestInventory.LimitSlots(_gameInstance, 2);

            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 20), Is.False, "two full stacks");
            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 21), Is.True, "a third stack");
        }

        [Test]
        public void ItemsThatUseNoSlotNeverFillTheBag()
        {
            TestInventory.LimitSlots(_gameInstance, 1);
            _player.IncreaseItems(CharacterItem.Create(Junk("Rock")));
            Item key = Junk("Key");
            key.NoSlotUsage = true;

            Assert.That(_player.IncreasingItemsWillOverwhelming(key.DataId, 5), Is.False);
        }

        [Test]
        public void AnUnlimitedBagAlwaysHasRoom()
        {
            for (int i = 0; i < 50; ++i)
                _player.IncreaseItems(CharacterItem.Create(Junk("Junk" + i)));

            Assert.That(_player.IncreasingItemsWillOverwhelming(_potion.DataId, 1000), Is.False);
        }

        [Test]
        public void AnOverweightBagRefusesMoreAndALighterOneAcceptsIt()
        {
            List<CharacterItem> bag = new List<CharacterItem> { CharacterItem.Create(Junk("Anvil")) };

            Assert.That(bag.IncreasingItemsWillOverwhelming(_potion.DataId, 1, true, 50f, 51f, false, 0), Is.True, "already over");
            Assert.That(bag.IncreasingItemsWillOverwhelming(_potion.DataId, 1, true, 50f, 10f, false, 0), Is.False, "under the limit");
            Assert.That(bag.IncreasingItemsWillOverwhelming(_potion.DataId, 1, false, 50f, 51f, false, 0), Is.False, "weight limit off");
        }

        [Test]
        public void FillingEmptySlotsPadsTheBagButNeverDropsAnItem()
        {
            TestInventory.LimitSlots(_gameInstance, 2);
            Item rock = Junk("Rock");
            Item stick = Junk("Stick");
            Item gem = Junk("Gem");
            _player.IncreaseItems(CharacterItem.Create(rock));
            _player.IncreaseItems(CharacterItem.Create(stick));
            _player.IncreaseItems(CharacterItem.Create(gem));

            _player.FillEmptySlots();
            Assert.That(_player.NonEquipItems, Has.Count.EqualTo(3), "an over-full bag keeps every item");

            _player.DecreaseItems(rock.DataId, 1);
            _player.DecreaseItems(stick.DataId, 1);
            _player.FillEmptySlots();
            Assert.That(_player.NonEquipItems, Has.Count.EqualTo(2), "trimmed back to the limit");
            Assert.That(TestInventory.Count(_player, gem), Is.EqualTo(1), "only empty slots were trimmed");
        }

        [Test]
        public void TakingArmourOffIntoAFullBagIsRefusedAndTheArmourStaysOn()
        {
            TestInventory.LimitSlots(_gameInstance, 1);
            ArmorItem helm = TestGear.Equipment<ArmorItem>(Objects, default);
            _player.EquipItems.Add(CharacterItem.Create(helm));
            _player.IncreaseItems(CharacterItem.Create(Junk("Rock")));

            Assert.That(_player.UnEquipArmor(0, false, out UITextKeys message, out _), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_WILL_OVERWHELMING));
            Assert.That(_player.EquipItems, Has.Count.EqualTo(1), "still worn");
            Assert.That(_player.NonEquipItems, Has.Count.EqualTo(1), "bag unchanged");
        }
    }
}
