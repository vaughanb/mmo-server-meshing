using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Buying from and selling to NPCs: the gameplay rule's CurrenciesEnoughToBuyItem,
    /// DecreaseCurrenciesWhenBuyItem and IncreaseCurrenciesWhenSellItem, and SellItem/SellItems
    /// on the bag. The check and the charge must agree (a player who passes the check can pay,
    /// and pays exactly once); a sale takes exactly the items it pays for; nothing is sold for
    /// nothing, and no amount makes gold wrap round. Price rates come from the class's stats, as
    /// the demo's buy-back share does.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class ShopCurrencyTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private Item _potion;

        private BaseGameplayRule Rule => _gameInstance.GameplayRule;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _potion = TestInventory.Item(Objects, maxStack: 10, sellPrice: 40, name: "Potion");
        }

        private NpcSellItem Offer(int price, params CurrencyAmount[] currencyPrices)
        {
            return new NpcSellItem { item = _potion, level = 1, amount = 1, sellPrice = price, sellPrices = currencyPrices };
        }

        private void GiveClassPriceRates(float buyRate, float sellRate)
        {
            PlayerCharacter playerClass = Objects.Create<PlayerCharacter>("Trader");
            playerClass.Stats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { buyItemPriceRate = buyRate, sellItemPriceRate = sellRate },
            };
            GameInstance.Characters[playerClass.DataId] = playerClass;
            _player.DataId = playerClass.DataId;
            _player.MarkToMakeCaches();
        }

        [Test]
        public void BuyingNeedsTheWholePriceForEveryUnit()
        {
            _player.Gold = 3 * 40 - 1;
            Assert.That(Rule.CurrenciesEnoughToBuyItem(_player, Offer(40), 3), Is.False, "one gold short");

            _player.Gold = 3 * 40;
            Assert.That(Rule.CurrenciesEnoughToBuyItem(_player, Offer(40), 3), Is.True, "exactly enough");
        }

        [Test]
        public void BuyingChargesThePriceOfEveryUnitOnce()
        {
            _player.Gold = 200;

            Rule.DecreaseCurrenciesWhenBuyItem(_player, Offer(40), 3);

            Assert.That(_player.Gold, Is.EqualTo(200 - 3 * 40));
        }

        [TestCase(0.25f)]
        [TestCase(-0.5f)]
        public void AGoldPriceRateIsChargedExactlyAsItIsChecked(float buyRate)
        {
            GiveClassPriceRates(buyRate, 0f);
            // Find the least gold the check accepts; paying must then leave the purse at zero.
            _player.Gold = 0;
            while (!Rule.CurrenciesEnoughToBuyItem(_player, Offer(40), 3))
                ++_player.Gold;

            Rule.DecreaseCurrenciesWhenBuyItem(_player, Offer(40), 3);

            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void AnAmountWhoseTotalPriceOverflowsIsRefused()
        {
            _player.Gold = 100;
            // 10 x 429,496,730 is 2^32 + 4: in 32 bits a four-gold purchase.
            Assert.That(Rule.CurrenciesEnoughToBuyItem(_player, Offer(10), 429_496_730), Is.False);
        }

#if !DISABLE_CUSTOM_CHARACTER_CURRENCIES
        private Currency Fame()
        {
            Currency fame = Objects.Create<Currency>("Fame");
            GameInstance.Currencies[fame.DataId] = fame;
            RuntimeGameDataSlots.Register(fame);
            return fame;
        }

        private int Balance(Currency currency)
        {
            int index = _player.IndexOfCurrency(currency.DataId);
            return index < 0 ? 0 : _player.Currencies[index].amount;
        }

        [Test]
        public void ACurrencyPriceIsCheckedAndChargedForEveryUnit()
        {
            Currency fame = Fame();
            NpcSellItem offer = Offer(0, new CurrencyAmount { currency = fame, amount = 5 });
            _player.Currencies.Add(CharacterCurrency.Create(fame, 9));
            Assert.That(Rule.CurrenciesEnoughToBuyItem(_player, offer, 2), Is.False, "one fame short");

            _player.Currencies[0] = CharacterCurrency.Create(fame, 10);
            Assert.That(Rule.CurrenciesEnoughToBuyItem(_player, offer, 2), Is.True, "exactly enough");

            Rule.DecreaseCurrenciesWhenBuyItem(_player, offer, 2);
            Assert.That(Balance(fame), Is.Zero);
        }

        [Test]
        public void ACurrencyPriceRateIsChargedExactlyAsItIsChecked()
        {
            GiveClassPriceRates(-0.5f, 0f);
            Currency fame = Fame();
            NpcSellItem offer = Offer(0, new CurrencyAmount { currency = fame, amount = 10 });
            _player.Currencies.Add(CharacterCurrency.Create(fame, 5));
            Assume.That(Rule.CurrenciesEnoughToBuyItem(_player, offer, 1), Is.True, "half price passes the check");

            Rule.DecreaseCurrenciesWhenBuyItem(_player, offer, 1);

            Assert.That(Balance(fame), Is.Zero, "charged the half price it checked, not the full price");
        }
#endif

        [Test]
        public void SellingPaysForEachUnitAndTakesExactlyThose()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            Assert.That(_player.SellItem(InventoryType.NonEquipItems, 0, 0, 3, out _), Is.True);

            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(2));
            Assert.That(_player.Gold, Is.EqualTo(3 * _potion.SellPrice));
        }

        [Test]
        public void ANegativeSellRateIsTheShareTheMerchantKeeps()
        {
            // The demo pays a quarter back this way (sellItemPriceRate -0.75 on every class).
            GiveClassPriceRates(0f, -0.75f);
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));

            _player.SellItem(InventoryType.NonEquipItems, 0, 0, 4, out _);

            Assert.That(_player.Gold, Is.EqualTo(_potion.SellPrice * 4 / 4));
        }

        [Test]
        public void SellingMoreThanTheStackHoldsSellsNothing()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            Assert.That(_player.SellItem(InventoryType.NonEquipItems, 0, 0, 6, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS));
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(5));
            Assert.That(_player.Gold, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-3)]
        public void SellingNoneOrANegativeAmountDoesNothing(int amount)
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 5));

            Assert.That(_player.SellItem(InventoryType.NonEquipItems, 0, 0, amount, out _), Is.False);

            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(5));
            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void AnItemThatCannotBeSoldIsKeptAndPaysNothing()
        {
            Item heirloom = TestInventory.Item(Objects, sellPrice: 500, name: "Heirloom").With("restrictSelling", true);
            _player.NonEquipItems.Add(CharacterItem.Create(heirloom));

            Assert.That(_player.SellItem(InventoryType.NonEquipItems, 0, 0, 1, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_ITEM_SELLING_RESTRICTED));
            Assert.That(TestInventory.Count(_player, heirloom), Is.EqualTo(1));
            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void SellingNearTheGoldCapStopsAtTheCapInsteadOfWrapping()
        {
            _player.Gold = int.MaxValue - 10;
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 1));

            _player.SellItem(InventoryType.NonEquipItems, 0, 0, 1, out _);

            Assert.That(_player.Gold, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void ASlotListedTwiceInOneSaleIsSoldOnce()
        {
            Item sword = TestInventory.Item(Objects, sellPrice: 100, name: "Sword");
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 1));
            _player.NonEquipItems.Add(CharacterItem.Create(sword));

            _player.SellItems(new[] { 0, 0 }, out _);

            Assert.That(TestInventory.Count(_player, sword), Is.EqualTo(1), "the sword was not selected");
            Assert.That(_player.Gold, Is.EqualTo(_potion.SellPrice));
        }

        [Test]
        public void SellingAWeaponFromTheSecondSetLeavesEveryOtherWeaponWhereItWas()
        {
            // The equipment window sends an equipped weapon as index 0 plus its set in equipSlotIndex.
            WeaponItem sword = TestGear.Equipment<WeaponItem>(Objects, default);
            sword.SellPrice = 50;
            WeaponItem dagger = TestGear.Equipment<WeaponItem>(Objects, default);
            CharacterItem firstRight = CharacterItem.Create(sword);
            CharacterItem firstLeft = CharacterItem.Create(dagger);
            CharacterItem secondRight = CharacterItem.Create(sword);
            CharacterItem secondLeft = CharacterItem.Create(dagger);
            _player.SelectableWeaponSets = new List<EquipWeapons>
            {
                new EquipWeapons { rightHand = firstRight, leftHand = firstLeft },
                new EquipWeapons { rightHand = secondRight, leftHand = secondLeft },
            };

            Assert.That(_player.SellItem(InventoryType.EquipWeaponRight, 0, 1, 1, out _), Is.True);

            Assert.That(_player.SelectableWeaponSets[1].rightHand.IsEmptySlot(), Is.True, "the sold sword is gone");
            Assert.That(_player.SelectableWeaponSets[1].leftHand.id, Is.EqualTo(secondLeft.id), "the second set keeps its own off-hand");
            Assert.That(_player.SelectableWeaponSets[0].rightHand.id, Is.EqualTo(firstRight.id), "the first set is untouched");
            Assert.That(_player.SelectableWeaponSets[0].leftHand.id, Is.EqualTo(firstLeft.id), "the first set is untouched");
            Assert.That(_player.Gold, Is.EqualTo(50));
        }
    }
}
