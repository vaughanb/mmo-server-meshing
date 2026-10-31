using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Crafting from a recipe (ItemCraft.CanCraft and CraftItem, which NPC crafting dialogs and
    /// workbench queues both use). A recipe needs every material in full, its gold and its
    /// currencies, and room for the product. Crafting takes exactly those materials and that
    /// price and gives the recipe's amount of the product; nothing else in the bag changes, and a
    /// product that cannot be added takes nothing at all.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class ItemCraftTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private Item _ore;
        private Item _wood;
        private Item _sword;
        private Item _potion;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            TestInventory.UseServerLog(Objects);
            // A slot-limited bag never closes gaps, so a used-up material stack shifts nothing.
            _gameInstance.Instance.noInventoryWeightLimit = true;
            TestInventory.LimitSlots(_gameInstance, 10);
            _player = TestInventory.Player(Objects);
            _ore = TestInventory.Item(Objects, maxStack: 10, name: "Ore");
            _wood = TestInventory.Item(Objects, maxStack: 10, name: "Wood");
            _sword = TestInventory.Item(Objects, maxStack: 5, name: "Sword");
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
        }

        private static ItemCraft Recipe(BaseItem product, int amount = 1, int gold = 0, params ItemAmount[] materials)
        {
            return new ItemCraft()
                .With("craftingItem", product)
                .With("amount", amount)
                .With("requireGold", gold)
                .With("requireItems", materials);
        }

        private static ItemAmount Needs(Item item, int amount)
        {
            return new ItemAmount { item = item, amount = amount };
        }

        private void Give(Item item, int amount)
        {
            _player.IncreaseItems(CharacterItem.Create(item, 1, amount));
        }

        private UITextKeys Refusal(ItemCraft recipe)
        {
            Assert.That(recipe.CanCraft(_player, out UITextKeys message), Is.False);
            return message;
        }

        [Test]
        public void EveryMaterialIsNeededInFull()
        {
            ItemCraft recipe = Recipe(_sword, materials: new[] { Needs(_ore, 3), Needs(_wood, 2) });
            Give(_ore, 3);
            Give(_wood, 1);

            Assert.That(Refusal(recipe), Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS), "one wood short");

            Give(_wood, 1);
            Assert.That(recipe.CanCraft(_player), Is.True);
        }

        [Test]
        public void MaterialsCountAcrossStacks()
        {
            ItemCraft recipe = Recipe(_sword, materials: new[] { Needs(_ore, 14) });
            Give(_ore, 14);

            Assert.That(recipe.CanCraft(_player), Is.True, "a full stack of ten and four more");
        }

        [Test]
        public void TheGoldIsNeededInFull()
        {
            ItemCraft recipe = Recipe(_sword, gold: 50);
            _player.Gold = 49;

            Assert.That(Refusal(recipe), Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_CURRENCY_AMOUNTS));

            _player.Gold = 50;
            Assert.That(recipe.CanCraft(_player), Is.True);
        }

        [Test]
        public void ARecipeWithNoProductCannotBeCrafted()
        {
            Assert.That(Refusal(Recipe(null)), Is.EqualTo(UITextKeys.UI_ERROR_INVALID_ITEM_DATA));
        }

        [Test]
        public void AProductThatWillNotFitIsRefused()
        {
            _gameInstance.Instance.baseSlotLimit = 2;
            _player.MarkToMakeCaches();
            ItemCraft recipe = Recipe(_sword);
            Give(_ore, 1);
            Give(_potion, 1);

            Assert.That(Refusal(recipe), Is.EqualTo(UITextKeys.UI_ERROR_WILL_OVERWHELMING));
        }

        [Test]
        public void CraftingTakesExactlyThePriceAndGivesTheProduct()
        {
            ItemCraft recipe = Recipe(_sword, amount: 2, gold: 30, Needs(_ore, 3), Needs(_wood, 2));
            Give(_ore, 5);
            Give(_wood, 2);
            Give(_potion, 4);
            _player.Gold = 100;

            recipe.CraftItem(_player);

            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(2), "the recipe makes two");
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(2));
            Assert.That(TestInventory.Count(_player, _wood), Is.Zero);
            Assert.That(_player.Gold, Is.EqualTo(70));
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(4), "not part of the recipe");
        }

        [Test]
        public void CraftingTwiceCostsTwice()
        {
            ItemCraft recipe = Recipe(_sword, materials: new[] { Needs(_ore, 3) });
            Give(_ore, 7);

            recipe.CraftItem(_player);
            recipe.CraftItem(_player);

            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(2));
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(1));
            Assert.That(recipe.CanCraft(_player), Is.False, "one ore left for a three-ore recipe");
        }

        [Test]
        public void AProductTheGameDoesNotKnowTakesNothing()
        {
            Item unknown = Objects.Create<Item>("Unregistered");
            ItemCraft recipe = Recipe(unknown, gold: 10, materials: new[] { Needs(_ore, 2) });
            Give(_ore, 2);
            _player.Gold = 10;

            recipe.CraftItem(_player);

            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(2));
            Assert.That(_player.Gold, Is.EqualTo(10));
        }

#if !DISABLE_CUSTOM_CHARACTER_CURRENCIES
        [Test]
        public void ACurrencyPriceIsCheckedAndCharged()
        {
            Currency tokens = Objects.Create<Currency>("Tokens");
            GameInstance.Currencies[tokens.DataId] = tokens;
            RuntimeGameDataSlots.Register(tokens);
            ItemCraft recipe = Recipe(_sword).With("requireCurrencies", new[] { new CurrencyAmount { currency = tokens, amount = 5 } });
            _player.Currencies.Add(CharacterCurrency.Create(tokens, 4));

            Assert.That(Refusal(recipe), Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_CURRENCY_AMOUNTS));

            _player.Currencies[0] = CharacterCurrency.Create(tokens, 7);
            Assert.That(recipe.CanCraft(_player), Is.True);
            recipe.CraftItem(_player);

            Assert.That(_player.Currencies[_player.IndexOfCurrency(tokens.DataId)].amount, Is.EqualTo(2));
            Assert.That(TestInventory.Count(_player, _sword), Is.EqualTo(1));
        }
#endif
    }
}
