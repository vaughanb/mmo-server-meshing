using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The smith's other two services. Repair restores full durability for the price of the
    /// item's damage band, charged once per piece, and refuses an item that is whole,
    /// unbreakable or unaffordable without charging. Dismantling turns items into their
    /// materials, gold and socketed gems - for exactly the amount dismantled - and refuses,
    /// keeping the item, when the materials would not fit in the bag.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class RepairAndDismantleTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private Item _ore;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _ore = TestInventory.Item(Objects, maxStack: 10, name: "Ore");
        }

        private static ItemRepairPrice Band(float durabilityRate, int gold)
        {
            return new ItemRepairPrice(durabilityRate, new ItemAmount[0], new CurrencyAmount[0], gold);
        }

        private WeaponItem Sword(params ItemRepairPrice[] bands)
        {
            WeaponItem sword = TestGear.Equipment<WeaponItem>(Objects, default, maxDurability: 100f);
            sword.ItemRefine = Objects.Create<ItemRefine>().With("repairPrices", bands);
            return sword;
        }

        private static CharacterItem Worn(BaseItem item, float durability)
        {
            CharacterItem characterItem = CharacterItem.Create(item);
            characterItem.durability = durability;
            return characterItem;
        }

        private ArmorItem Helm(int returnOre, int returnGold)
        {
            ArmorItem helm = TestGear.Equipment<ArmorItem>(Objects, default);
            helm.DismantleReturnItems = new[] { new ItemAmount { item = _ore, amount = returnOre } };
            helm.DismantleReturnGold = returnGold;
            return helm;
        }

        [Test]
        public void ARepairRestoresFullDurabilityAndChargesOnce()
        {
            _player.NonEquipItems.Add(Worn(Sword(Band(1f, 30)), 40f));
            _player.Gold = 50;

            Assert.That(_player.RepairItem(InventoryType.NonEquipItems, 0, 0, out UITextKeys message), Is.True);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_REPAIR_SUCCESS));
            Assert.That(_player.NonEquipItems[0].durability, Is.EqualTo(100f));
            Assert.That(_player.Gold, Is.EqualTo(20));
        }

        [Test]
        public void AnUndamagedItemIsNeitherRepairedNorCharged()
        {
            _player.NonEquipItems.Add(Worn(Sword(Band(1f, 30)), 100f));
            _player.Gold = 50;

            Assert.That(_player.RepairItem(InventoryType.NonEquipItems, 0, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_CANNOT_REPAIR_ITEM_NOT_BROKEN));
            Assert.That(_player.Gold, Is.EqualTo(50));
        }

        [Test]
        public void WithoutTheGoldNothingIsRepaired()
        {
            _player.NonEquipItems.Add(Worn(Sword(Band(1f, 30)), 40f));
            _player.Gold = 29;

            Assert.That(_player.RepairItem(InventoryType.NonEquipItems, 0, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_CURRENCY_AMOUNTS));
            Assert.That(_player.NonEquipItems[0].durability, Is.EqualTo(40f));
            Assert.That(_player.Gold, Is.EqualTo(29));
        }

        [Test]
        public void UnbreakableGearIsNotRepaired()
        {
            WeaponItem stick = TestGear.Equipment<WeaponItem>(Objects, default);
            stick.ItemRefine = Objects.Create<ItemRefine>().With("repairPrices", new[] { Band(1f, 30) });
            _player.NonEquipItems.Add(CharacterItem.Create(stick));
            _player.Gold = 50;

            Assert.That(_player.RepairItem(InventoryType.NonEquipItems, 0, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_CANNOT_REPAIR_ITEM_UNBREAKABLE));
            Assert.That(_player.Gold, Is.EqualTo(50));
        }

        [Test]
        public void AWreckPaysTheWreckBandAndAScratchPaysTheTopBand()
        {
            WeaponItem sword = Sword(Band(1f, 10), Band(0.25f, 100));

            Assert.That(sword.TryGetRepairPrice(90f, out _, out ItemRepairPrice scratch), Is.True);
            Assert.That(sword.TryGetRepairPrice(10f, out _, out ItemRepairPrice wreck), Is.True);

            Assert.That(scratch.RequireGold, Is.EqualTo(10), "scratched, under 100%");
            Assert.That(wreck.RequireGold, Is.EqualTo(100), "wrecked, under 25%");
        }

        [Test]
        public void RepairingAllWornGearMendsEachPieceAndChargesForEach()
        {
            WeaponItem sword = Sword(Band(1f, 30));
            ArmorItem helm = TestGear.Equipment<ArmorItem>(Objects, default, maxDurability: 50f);
            helm.ItemRefine = sword.ItemRefine;
            _player.EquipWeapons = new EquipWeapons { rightHand = Worn(sword, 10f) };
            _player.EquipItems.Add(Worn(helm, 5f));
            _player.Gold = 100;

            Assert.That(_player.RepairEquipItems(out _), Is.True);

            Assert.That(_player.EquipWeapons.rightHand.durability, Is.EqualTo(100f));
            Assert.That(_player.EquipItems[0].durability, Is.EqualTo(50f));
            Assert.That(_player.Gold, Is.EqualTo(100 - 2 * 30));
        }

        [Test]
        public void DismantlingTurnsTheItemIntoItsMaterialsAndGold()
        {
            ArmorItem helm = Helm(returnOre: 2, returnGold: 5);
            _player.NonEquipItems.Add(CharacterItem.Create(helm));

            Assert.That(_player.DismantleItem(InventoryType.NonEquipItems, 0, 0, 1, out _), Is.True);

            Assert.That(TestInventory.Count(_player, helm), Is.Zero);
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(2));
            Assert.That(_player.Gold, Is.EqualTo(5));
        }

        [Test]
        public void DismantlingPartOfAStackReturnsMaterialsForThatPartOnly()
        {
            ArmorItem helm = Helm(returnOre: 2, returnGold: 5);
            helm.MaxStack = 5;
            _player.NonEquipItems.Add(CharacterItem.Create(helm, 1, 3));

            _player.DismantleItem(InventoryType.NonEquipItems, 0, 0, 2, out _);

            Assert.That(TestInventory.Count(_player, helm), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(2 * 2));
            Assert.That(_player.Gold, Is.EqualTo(2 * 5));
        }

        [Test]
        public void DismantlingIsRefusedWhenTheMaterialsWouldNotFit()
        {
            TestInventory.LimitSlots(_gameInstance, 1);
            Item stone = TestInventory.Item(Objects, maxStack: 10, name: "Stone");
            ArmorItem helm = Helm(returnOre: 2, returnGold: 5);
            helm.DismantleReturnItems = new[]
            {
                new ItemAmount { item = _ore, amount = 2 },
                new ItemAmount { item = stone, amount = 1 },
            };
            _player.NonEquipItems.Add(CharacterItem.Create(helm));

            Assert.That(_player.DismantleItem(InventoryType.NonEquipItems, 0, 0, 1, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_WILL_OVERWHELMING));
            Assert.That(TestInventory.Count(_player, helm), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, _ore), Is.Zero);
            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void SocketedGemsComeBackWhenTheItemIsDismantled()
        {
            Item gem = TestInventory.Item(Objects, maxStack: 10, name: "Ruby");
            ArmorItem helm = Helm(returnOre: 1, returnGold: 0);
            CharacterItem socketed = CharacterItem.Create(helm);
            socketed.sockets[0] = gem.DataId;
            _player.NonEquipItems.Add(socketed);

            _player.DismantleItem(InventoryType.NonEquipItems, 0, 0, 1, out _);

            Assert.That(TestInventory.Count(_player, gem), Is.EqualTo(1));
        }

        [Test]
        public void ItemsTheDismantleFilterLeavesOutAreKept()
        {
            Item potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
            potion.DismantleReturnGold = 5;
            _player.NonEquipItems.Add(CharacterItem.Create(potion, 1, 2));

            Assert.That(_player.DismantleItem(InventoryType.NonEquipItems, 0, 0, 2, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_ITEM_DATA));
            Assert.That(TestInventory.Count(_player, potion), Is.EqualTo(2));
            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void DismantlingSeveralSlotsAtOnceCountsASlotListedTwiceOnce()
        {
            ArmorItem helm = Helm(returnOre: 2, returnGold: 5);
            _player.NonEquipItems.Add(CharacterItem.Create(helm));
            _player.NonEquipItems.Add(CharacterItem.Create(helm));

            Assert.That(_player.DismantleItems(new[] { 0, 1, 1 }, out _), Is.True);

            Assert.That(TestInventory.Count(_player, helm), Is.Zero);
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(2 * 2));
            Assert.That(_player.Gold, Is.EqualTo(2 * 5));
        }
    }
}
