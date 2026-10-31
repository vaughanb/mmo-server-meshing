using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Moving items about: dragging one bag slot onto another (merge or swap) and moving items
    /// into and out of a storage chest. A move must keep the total of every item the same - a
    /// merge that overflows a stack keeps the rest, a part-stack move splits it - and a storage
    /// that is full, or not the player's own, must refuse without touching either side.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class InventoryMoveTests : KitTestFixture
    {
        private static readonly StorageId PlayerStorage = new StorageId(StorageType.Player, "user");
        private static readonly StorageId GuildStorage = new StorageId(StorageType.Guild, "1");

        private PlayerCharacterData _player;
        private Item _potion;
        private Item _sword;
        private List<CharacterItem> _storage;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
            _sword = TestInventory.Item(Objects, name: "Sword");
            _storage = new List<CharacterItem>();
        }

        private static int Count(IList<CharacterItem> list, BaseItem item)
        {
            int count = 0;
            foreach (CharacterItem entry in list)
            {
                if (entry.dataId == item.DataId)
                    count += entry.amount;
            }
            return count;
        }

        [Test]
        public void MergingTwoPartStacksPutsThemAllInTheTargetSlot()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));
            _player.NonEquipItems.Add(CharacterItem.Create(_sword));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 3));

            Assert.That(_player.SwapOrMergeItem(2, 0, out _), Is.True);

            Assert.That(_player.NonEquipItems[0].amount, Is.EqualTo(7));
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(7));
            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(1));
        }

        [Test]
        public void MergingPastAFullStackLeavesTheRestWhereItCameFrom()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 8));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            _player.SwapOrMergeItem(1, 0, out _);

            Assert.That(_player.NonEquipItems[0].amount, Is.EqualTo(_potion.MaxStack));
            Assert.That(_player.NonEquipItems[1].amount, Is.EqualTo(3));
        }

        [Test]
        public void DifferentItemsSwapPlaces()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));
            _player.NonEquipItems.Add(CharacterItem.Create(_sword));

            _player.SwapOrMergeItem(0, 1, out _);

            Assert.That(_player.NonEquipItems[0].dataId, Is.EqualTo(_sword.DataId));
            Assert.That(_player.NonEquipItems[1].dataId, Is.EqualTo(_potion.DataId));
            Assert.That(_player.NonEquipItems[1].amount, Is.EqualTo(4));
        }

        [Test]
        public void ASlotCannotBeMergedIntoItself()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));

            Assert.That(_player.SwapOrMergeItem(0, 0, out UITextKeys message), Is.False);
            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_ITEM_INDEX));
            Assert.That(_player.NonEquipItems[0].amount, Is.EqualTo(4));
        }

        [Test]
        public void PartOfAStackGoesIntoStorageAndComesBackWhole()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            Assert.That(_player.MoveItemToStorage(PlayerStorage, false, 0f, false, 0, _storage, -1,
                InventoryType.NonEquipItems, 0, 3, 0, out _), Is.True);
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(2), "bag after storing");
            Assert.That(Count(_storage, _potion), Is.EqualTo(3), "storage after storing");

            Assert.That(_player.MoveItemFromStorage(PlayerStorage, false, 0f, false, 0, _storage, 0, 3,
                InventoryType.NonEquipItems, -1, 0, out _), Is.True);
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(5), "bag after taking back");
            Assert.That(Count(_storage, _potion), Is.Zero, "storage after taking back");
        }

        [Test]
        public void AFullStorageRefusesAndBothSidesStayAsTheyWere()
        {
            _storage.Add(CharacterItem.Create(_sword));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            Assert.That(_player.MoveItemToStorage(PlayerStorage, false, 0f, true, 1, _storage, -1,
                InventoryType.NonEquipItems, 0, 5, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_WILL_OVERWHELMING));
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(5));
            Assert.That(Count(_storage, _potion), Is.Zero);
        }

        [Test]
        public void AnUntradeableItemCannotBePassedOnThroughAGuildStorage()
        {
            Item bound = TestInventory.Item(Objects, name: "SoulboundRing").With("restrictDealing", true);
            _player.NonEquipItems.Add(CharacterItem.Create(bound));

            Assert.That(_player.MoveItemToStorage(GuildStorage, false, 0f, false, 0, _storage, -1,
                InventoryType.NonEquipItems, 0, 1, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_ITEM_DEALING_RESTRICTED));
            Assert.That(TestInventory.Count(_player, bound), Is.EqualTo(1));
            Assert.That(_storage, Is.Empty);
        }

        [Test]
        public void StoringMoreThanTheSlotHoldsIsRefused()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 2));

            Assert.That(_player.MoveItemToStorage(PlayerStorage, false, 0f, false, 0, _storage, -1,
                InventoryType.NonEquipItems, 0, 3, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS));
            Assert.That(_storage, Is.Empty, "no potion made up in the chest");
        }

        [Test]
        public void AStorageMoveToASlotThatDoesNotExistIsRefused()
        {
            _storage.Add(CharacterItem.Create(_potion, 1, 2));

            Assert.That(_player.SwapOrMergeStorageItem(PlayerStorage, false, 0, _storage, 0, 5, out UITextKeys message), Is.False);
            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_ITEM_INDEX));
        }
    }
}
