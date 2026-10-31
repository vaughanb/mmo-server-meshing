using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Refining gear at the smith (RefineItem against an ItemRefine ladder). Success raises the
    /// item one level; failure may lower it (never below level 1) or destroy it, which enhancer
    /// materials can prevent. Every attempt costs its gold and materials, an attempt the player
    /// cannot pay for changes nothing, and nothing but the refined item and what was spent on it
    /// may change in the bag.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class RefineTests : KitTestFixture
    {
        private static readonly int[] NoEnhancers = new int[0];

        private PlayerCharacterData _player;
        private ArmorItem _helm;
        private Item _ore;
        private Item _potion;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _helm = TestGear.Equipment<ArmorItem>(Objects, default);
            _ore = TestInventory.Item(Objects, maxStack: 10, name: "Ore");
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
        }

        private static ItemRefineLevel Step(float successRate, int gold = 0)
        {
            return new ItemRefineLevel().With("successRate", successRate).With("requireGold", gold);
        }

        private void Ladder(params ItemRefineLevel[] steps)
        {
            _helm.ItemRefine = Objects.Create<ItemRefine>().With("levels", steps);
        }

        private int HelmIndex()
        {
            return _player.IndexOfNonEquipItem(_helm.DataId);
        }

        private CharacterItem Helm()
        {
            return _player.NonEquipItems[HelmIndex()];
        }

        private bool Refine(params int[] enhancers)
        {
            return _player.RefineItem(InventoryType.NonEquipItems, HelmIndex(), 0, enhancers, out _);
        }

        [Test]
        public void ASuccessRaisesTheLevelByOneAndChargesTheGold()
        {
            Ladder(Step(1f, gold: 100));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.Gold = 150;

            Assert.That(_player.RefineItem(InventoryType.NonEquipItems, 0, 0, NoEnhancers, out UITextKeys message), Is.True);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_REFINE_SUCCESS));
            Assert.That(Helm().level, Is.EqualTo(2));
            Assert.That(_player.Gold, Is.EqualTo(50));
        }

        [Test]
        public void WithoutTheGoldTheRefineIsRefusedAndNothingChanges()
        {
            Ladder(Step(1f, gold: 100));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.Gold = 99;

            Assert.That(_player.RefineItem(InventoryType.NonEquipItems, 0, 0, NoEnhancers, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_CURRENCY_AMOUNTS));
            Assert.That(Helm().level, Is.EqualTo(1));
            Assert.That(_player.Gold, Is.EqualTo(99));
        }

        [Test]
        public void RequiredMaterialsMustBeCarriedAndAreUsedUp()
        {
            Ladder(Step(1f).With("requireItems", new[] { new ItemAmount { item = _ore, amount = 2 } }));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(_ore, 1, 1));

            Assert.That(Refine(), Is.False, "one ore short");
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(1), "a refused refine takes nothing");

            _player.IncreaseItems(CharacterItem.Create(_ore, 1, 2));
            Assert.That(Refine(), Is.True);
            Assert.That(TestInventory.Count(_player, _ore), Is.EqualTo(1), "two of three used");
            Assert.That(Helm().level, Is.EqualTo(2));
        }

        [Test]
        public void AFailureThatLosesLevelsStopsAtLevelOneAndStillCharges()
        {
            ItemRefineLevel failing = Step(0f, gold: 10).With("refineFailDecreaseLevels", 5);
            Ladder(failing, failing, failing);
            _player.NonEquipItems.Add(CharacterItem.Create(_helm, 3));
            _player.Gold = 10;

            Assert.That(_player.RefineItem(InventoryType.NonEquipItems, 0, 0, NoEnhancers, out UITextKeys message), Is.True);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_REFINE_FAIL));
            Assert.That(Helm().level, Is.EqualTo(1));
            Assert.That(_player.Gold, Is.Zero);
        }

        [Test]
        public void AFailureThatDestroysTakesTheItemAndNothingElse()
        {
            Ladder(Step(0f).With("refineFailDestroyItem", true));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));

            Refine();

            Assert.That(HelmIndex(), Is.LessThan(0), "destroyed");
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(4));
        }

        [Test]
        public void AnEnhancerThatSavesTheItemKeepsItAndIsUsedUp()
        {
            Item ward = TestInventory.Item(Objects, maxStack: 10, name: "Ward");
            Ladder(Step(0f).With("refineFailDestroyItem", true).With("availableEnhancers", new[]
            {
                new ItemRefineEnhancer { item = ward, chanceToNotDestroyItem = 1f },
            }));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(ward, 1, 2));

            Refine(ward.DataId);

            Assert.That(HelmIndex(), Is.GreaterThanOrEqualTo(0), "the ward saved it");
            Assert.That(TestInventory.Count(_player, ward), Is.EqualTo(1));
        }

        [Test]
        public void AnEnhancerThatRaisesTheChanceTurnsAFailureIntoASuccess()
        {
            Item charm = TestInventory.Item(Objects, maxStack: 10, name: "Charm");
            Ladder(Step(0f).With("availableEnhancers", new[]
            {
                new ItemRefineEnhancer { item = charm, increaseSuccessRate = 1f },
            }));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(charm, 1, 3));

            Refine(charm.DataId);

            Assert.That(Helm().level, Is.EqualTo(2));
            Assert.That(TestInventory.Count(_player, charm), Is.EqualTo(2));
        }

        [Test]
        public void AnItemAtTheTopOfItsLadderCannotBeRefined()
        {
            Ladder(Step(1f, gold: 10));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm, 2));
            _player.Gold = 10;

            Assert.That(_player.RefineItem(InventoryType.NonEquipItems, 0, 0, NoEnhancers, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_REFINE_ITEM_REACHED_MAX_LEVEL));
            Assert.That(_player.Gold, Is.EqualTo(10));
        }

        [Test]
        public void AnItemThatIsNotGearCannotBeRefined()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 1));

            Assert.That(_player.RefineItem(InventoryType.NonEquipItems, 0, 0, NoEnhancers, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_ITEM_NOT_EQUIPMENT));
        }

        [Test]
        public void AnEnhancerListedBeforeTheItemDoesNotShiftTheRefineOntoAnotherItem()
        {
            Item charm = TestInventory.Item(Objects, maxStack: 10, name: "Charm");
            Ladder(Step(1f).With("availableEnhancers", new[] { new ItemRefineEnhancer { item = charm } }));
            _player.NonEquipItems.Add(CharacterItem.Create(charm, 1, 1));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 4));

            Refine(charm.DataId);

            Assert.That(TestInventory.Count(_player, _helm), Is.EqualTo(1), "one helm, not two");
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(4), "the potions are not overwritten");
            Assert.That(Helm().level, Is.EqualTo(2));
        }
    }
}
