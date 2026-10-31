using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Repairing a player's building (BuildingEntity.TryGetRepairAmount, Repair and the repair
    /// entries on the building). One repair restores at most the entry's step and never past full
    /// HP, shrinks to what the player can pay for, and is refused with a reason when the player
    /// cannot pay for a single point. Repairing from the building's menu must charge the same
    /// price as repairing with a hammer. Also who counts as the building's creator, which decides
    /// who may pull it down.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class BuildingRepairTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterEntity _player;
        private Item _wood;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _gameInstance.Instance.noInventoryWeightLimit = true;
            _player = TestCombatants.Create<PlayerCharacterEntity>(Objects);
            _wood = TestInventory.Item(Objects, maxStack: 50, name: "Wood");
        }

        private BuildingEntity Building(int maxHp, int currentHp, params BuildingRepairData[] repairs)
        {
            BuildingEntity building = Objects.NewGameObject("Building").AddComponent<BuildingEntity>()
                .With("maxHp", maxHp)
                .With("repairs", new List<BuildingRepairData>(repairs));
            building.CurrentHp = currentHp;
            return building;
        }

        /// <summary>A hammer repair: hitting the building with the default weapon.</summary>
        private BuildingRepairData HammerRepair(int step, int goldPerHp = 0, int woodPerHp = 0)
        {
            return new BuildingRepairData
            {
                weaponItem = _gameInstance.DefaultWeaponItem,
                maxRecoveryHp = step,
                requireGold = goldPerHp,
                requireItems = woodPerHp > 0 ? new[] { new ItemAmount { item = _wood, amount = woodPerHp } } : new ItemAmount[0],
            };
        }

        private static BuildingRepairData MenuRepair(int step, int goldPerHp)
        {
            return new BuildingRepairData { canRepairFromMenu = true, maxRecoveryHp = step, requireGold = goldPerHp };
        }

        private int RepairAmount(BuildingEntity building, BuildingRepairData repair, out UITextKeys message)
        {
            building.TryGetRepairAmount(_player, repair, out int amount, out message);
            return amount;
        }

        [Test]
        public void ARepairRestoresAtMostItsStepAndNeverPastFullHp()
        {
            BuildingRepairData repair = HammerRepair(step: 10);

            Assert.That(RepairAmount(Building(100, 50, repair), repair, out _), Is.EqualTo(10), "half down: one step");
            Assert.That(RepairAmount(Building(100, 95, repair), repair, out _), Is.EqualTo(5), "only the missing 5");
        }

        [Test]
        public void AFullBuildingIsNotRepaired()
        {
            BuildingRepairData repair = HammerRepair(step: 10);

            Assert.That(Building(100, 100, repair).TryGetRepairAmount(_player, repair, out _, out _), Is.False);
        }

        [Test]
        public void ARepairShrinksToWhatThePlayerCanPayFor()
        {
            BuildingRepairData repair = HammerRepair(step: 10, goldPerHp: 2, woodPerHp: 1);
            BuildingEntity building = Building(100, 50, repair);

            _player.Gold = 7;
            _player.IncreaseItems(CharacterItem.Create(_wood, 1, 20));
            Assert.That(RepairAmount(building, repair, out _), Is.EqualTo(3), "seven gold pays for three points");

            _player.Gold = 100;
            _player.DecreaseItems(_wood.DataId, 16);
            Assert.That(RepairAmount(building, repair, out _), Is.EqualTo(4), "four wood pays for four points");
        }

        [Test]
        public void APlayerWhoCannotPayForOnePointIsToldWhy()
        {
            BuildingRepairData goldRepair = HammerRepair(step: 10, goldPerHp: 2);
            BuildingRepairData woodRepair = HammerRepair(step: 10, woodPerHp: 1);
            _player.Gold = 1;

            Assert.That(Building(100, 50, goldRepair).TryGetRepairAmount(_player, goldRepair, out _, out UITextKeys goldMessage), Is.False);
            Assert.That(goldMessage, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD));
            Assert.That(Building(100, 50, woodRepair).TryGetRepairAmount(_player, woodRepair, out _, out UITextKeys woodMessage), Is.False);
            Assert.That(woodMessage, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS));
        }

        [Test]
        public void RepairingFromTheMenuChargesForTheHpRestored()
        {
            BuildingEntity building = Building(100, 40, MenuRepair(step: 10, goldPerHp: 2), HammerRepair(step: 10));
            _player.Gold = 100;

            Assert.That(building.Repair(_player, out _), Is.True);

            Assert.That(building.CurrentHp, Is.EqualTo(50));
            Assert.That(_player.Gold, Is.EqualTo(100 - 10 * 2));
        }

        [Test]
        public void ABuildingWithOnlyAMenuRepairCanBeRepairedFromTheMenu()
        {
            BuildingEntity building = Building(100, 40, MenuRepair(step: 10, goldPerHp: 0));

            Assert.That(building.CanRepairByMenu(), Is.True);
            Assert.That(building.Repair(_player, out _), Is.True);
            Assert.That(building.CurrentHp, Is.EqualTo(50));
        }

        [Test]
        public void OnlyTheBuilderIsTheCreator()
        {
            BuildingEntity building = Building(100, 100);
            building.CreatorId = "char-1";
            PlayerCharacterData builder = TestCharacters.NewPlayer("Builder");
            builder.Id = "char-1";
            PlayerCharacterData passerBy = TestCharacters.NewPlayer("PasserBy");

            Assert.That(building.IsCreator(builder), Is.True);
            Assert.That(building.IsCreator(passerBy), Is.False);
            Assert.That(building.IsCreator((IPlayerCharacterData)null), Is.False);
        }
    }
}
