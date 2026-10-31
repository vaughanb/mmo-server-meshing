using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Putting gear on and taking it off (EquipArmor, EquipWeapon, UnEquipWeapon). Equipping
    /// moves the piece out of the bag; whatever it displaces goes back into the bag - never lost,
    /// never doubled. A piece the character may not wear (level, class, attributes, wrong hand,
    /// a weapon set that does not exist) is refused and stays in the bag.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class EquipRulesTests : KitTestFixture
    {
        private PlayerCharacterData _player;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _player = TestInventory.Player(Objects);
        }

        private ArmorItem Armour(ArmorType position = null)
        {
            ArmorItem armour = TestGear.Equipment<ArmorItem>(Objects, default);
            armour.ArmorType = position;
            return armour;
        }

        private WeaponItem Weapon(WeaponItemEquipType equipType)
        {
            WeaponItem weapon = TestGear.Equipment<WeaponItem>(Objects, default);
            weapon.WeaponType = Objects.Create<WeaponType>(equipType.ToString()).With("equipType", equipType);
            return weapon;
        }

        private PlayerCharacter Class(string name)
        {
            PlayerCharacter playerClass = Objects.Create<PlayerCharacter>(name);
            playerClass.Stats = new CharacterStatsIncremental();
            GameInstance.Characters[playerClass.DataId] = playerClass;
            return playerClass;
        }

        private int CarriedAndWorn()
        {
            int count = _player.EquipItems.Count;
            foreach (CharacterItem item in _player.NonEquipItems)
                count += item.IsEmptySlot() ? 0 : item.amount;
            foreach (EquipWeapons set in _player.SelectableWeaponSets)
                count += (set.rightHand.IsEmptySlot() ? 0 : 1) + (set.leftHand.IsEmptySlot() ? 0 : 1);
            return count;
        }

        [Test]
        public void EquippingArmourMovesItFromTheBagOntoTheCharacter()
        {
            ArmorItem helm = Armour();
            _player.NonEquipItems.Add(CharacterItem.Create(helm));

            Assert.That(_player.EquipArmor(0, 0, out _), Is.True);

            Assert.That(_player.EquipItems, Has.Count.EqualTo(1));
            Assert.That(_player.EquipItems[0].dataId, Is.EqualTo(helm.DataId));
            Assert.That(TestInventory.Count(_player, helm), Is.Zero);
        }

        [Test]
        public void ArmourForAnOccupiedPositionSwapsTheOldPieceIntoTheBag()
        {
            ArmorType head = Objects.Create<ArmorType>("Head");
            ArmorItem oldHelm = Armour(head);
            ArmorItem newHelm = Armour(head);
            _player.EquipItems.Add(CharacterItem.Create(oldHelm));
            _player.NonEquipItems.Add(CharacterItem.Create(newHelm));

            Assert.That(_player.EquipArmor(0, 0, out _), Is.True);

            Assert.That(_player.EquipItems, Has.Count.EqualTo(1), "one helm worn");
            Assert.That(_player.EquipItems[0].dataId, Is.EqualTo(newHelm.DataId));
            Assert.That(TestInventory.Count(_player, oldHelm), Is.EqualTo(1), "the old helm is back in the bag");
            Assert.That(CarriedAndWorn(), Is.EqualTo(2));
        }

        [Test]
        public void ArmourForADifferentPositionIsWornAlongside()
        {
            ArmorItem helm = Armour(Objects.Create<ArmorType>("Head"));
            ArmorItem boots = Armour(Objects.Create<ArmorType>("Feet"));
            _player.EquipItems.Add(CharacterItem.Create(helm));
            _player.NonEquipItems.Add(CharacterItem.Create(boots));

            _player.EquipArmor(0, 0, out _);

            Assert.That(_player.EquipItems, Has.Count.EqualTo(2));
            Assert.That(_player.NonEquipItems, Is.Empty);
        }

        [Test]
        public void GearAboveTheCharactersLevelIsRefusedAndStaysInTheBag()
        {
            ArmorItem helm = Armour();
            helm.Requirement.level = 10;
            _player.Level = 9;
            _player.NonEquipItems.Add(CharacterItem.Create(helm));

            Assert.That(_player.EquipArmor(0, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_LEVEL));
            Assert.That(_player.EquipItems, Is.Empty);
            Assert.That(TestInventory.Count(_player, helm), Is.EqualTo(1));

            _player.Level = 10;
            Assert.That(_player.EquipArmor(0, 0, out _), Is.True, "allowed at the required level");
        }

        [Test]
        public void GearForAnotherClassIsRefused()
        {
            PlayerCharacter warrior = Class("Warrior");
            PlayerCharacter mage = Class("Mage");
            ArmorItem plate = Armour();
            plate.Requirement.availableClasses.Add(warrior);
            _player.DataId = mage.DataId;
            _player.NonEquipItems.Add(CharacterItem.Create(plate));

            Assert.That(_player.EquipArmor(0, 0, out UITextKeys message), Is.False);
            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_MATCH_CHARACTER_CLASS));

            _player.DataId = warrior.DataId;
            Assert.That(_player.EquipArmor(0, 0, out _), Is.True, "the warrior may wear it");
        }

        [Test]
        public void GearNeedingMoreOfAnAttributeThanTheCharacterHasIsRefused()
        {
            Attribute strength = Objects.Create<Attribute>("Strength");
            GameInstance.Attributes[strength.DataId] = strength;
            RuntimeGameDataSlots.Register(strength);
            ArmorItem plate = Armour();
            plate.Requirement.attributeAmounts = new[] { new AttributeAmount { attribute = strength, amount = 5f } };
            _player.Attributes.Add(CharacterAttribute.Create(strength, 4));
            _player.NonEquipItems.Add(CharacterItem.Create(plate));

            Assert.That(_player.EquipArmor(0, 0, out UITextKeys message), Is.False);
            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ATTRIBUTE_AMOUNTS));

            _player.Attributes[0] = CharacterAttribute.Create(strength, 5);
            Assert.That(_player.EquipArmor(0, 0, out _), Is.True, "allowed with enough strength");
        }

        [Test]
        public void ATwoHandedWeaponSendsBothHandsBackToTheBag()
        {
            WeaponItem sword = Weapon(WeaponItemEquipType.DualWieldable);
            WeaponItem dagger = Weapon(WeaponItemEquipType.DualWieldable);
            WeaponItem greatsword = Weapon(WeaponItemEquipType.TwoHand);
            _player.EquipWeapons = new EquipWeapons { rightHand = CharacterItem.Create(sword), leftHand = CharacterItem.Create(dagger) };
            _player.NonEquipItems.Add(CharacterItem.Create(greatsword));

            Assert.That(_player.EquipWeapon(0, 0, false, out _), Is.True);

            Assert.That(_player.EquipWeapons.rightHand.dataId, Is.EqualTo(greatsword.DataId));
            Assert.That(_player.EquipWeapons.leftHand.IsEmptySlot(), Is.True);
            Assert.That(TestInventory.Count(_player, sword), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, dagger), Is.EqualTo(1));
            Assert.That(TestInventory.Count(_player, greatsword), Is.Zero);
            Assert.That(CarriedAndWorn(), Is.EqualTo(3));
        }

        [Test]
        public void AMainHandWeaponCannotGoInTheOffHand()
        {
            WeaponItem mace = Weapon(WeaponItemEquipType.MainHandOnly);
            _player.NonEquipItems.Add(CharacterItem.Create(mace));

            Assert.That(_player.EquipWeapon(0, 0, true, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_EQUIP_POSITION_RIGHT_HAND));
            Assert.That(TestInventory.Count(_player, mace), Is.EqualTo(1));
        }

        [Test]
        public void AShieldOnlyGoesInTheLeftHand()
        {
            ShieldItem shield = TestGear.Equipment<ShieldItem>(Objects, default);
            _player.NonEquipItems.Add(CharacterItem.Create(shield));

            Assert.That(_player.EquipWeapon(0, 0, false, out _), Is.False, "right hand");
            Assert.That(_player.EquipWeapon(0, 0, true, out _), Is.True, "left hand");
            Assert.That(_player.EquipWeapons.leftHand.dataId, Is.EqualTo(shield.DataId));
        }

        [Test]
        public void AWeaponSetBeyondTheLastIsRefused()
        {
            WeaponItem sword = Weapon(WeaponItemEquipType.DualWieldable);
            _player.NonEquipItems.Add(CharacterItem.Create(sword));
            byte pastTheLast = GameInstance.Singleton.maxEquipWeaponSet;

            Assert.That(_player.EquipWeapon(0, pastTheLast, false, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_CANNOT_EQUIP));
            Assert.That(TestInventory.Count(_player, sword), Is.EqualTo(1));
        }

        [Test]
        public void AnItemThatIsNotGearCannotBeEquipped()
        {
            Item potion = TestInventory.Item(Objects, maxStack: 10);
            _player.NonEquipItems.Add(CharacterItem.Create(potion, 1, 3));

            Assert.That(_player.EquipArmor(0, 0, out _), Is.False, "as armour");
            Assert.That(_player.EquipWeapon(0, 0, false, out _), Is.False, "as a weapon");
            Assert.That(TestInventory.Count(_player, potion), Is.EqualTo(3));
        }

        [Test]
        public void TakingAWeaponOffPutsItInTheBag()
        {
            WeaponItem sword = Weapon(WeaponItemEquipType.DualWieldable);
            _player.EquipWeapons = new EquipWeapons { rightHand = CharacterItem.Create(sword) };

            Assert.That(_player.UnEquipWeapon(0, false, false, out _, out _), Is.True);

            Assert.That(_player.EquipWeapons.rightHand.IsEmptySlot(), Is.True);
            Assert.That(TestInventory.Count(_player, sword), Is.EqualTo(1));
        }
    }
}
