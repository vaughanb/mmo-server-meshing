using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Arrows and other ammunition. Firing a weapon without a magazine takes ammo of its type
    /// from the bag - across as many stacks as it needs, leaving every other item alone, and
    /// nothing at all when there is not enough. Loading a weapon with a magazine moves ammo from
    /// the bag into the weapon, and unloading it puts the same amount back.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class AmmoTests : KitTestFixture
    {
        private PlayerCharacterData _player;
        private AmmoType _arrowType;
        private AmmoItem _arrows;
        private Item _potion;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _arrowType = Objects.Create<AmmoType>("Arrow");
            _arrows = Objects.Create<AmmoItem>("Arrows").With("ammoType", _arrowType);
            _arrows.MaxStack = 10;
            GameInstance.Items[_arrows.DataId] = _arrows;
            _potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
        }

        private bool Shoot(int amount)
        {
            return _player.NonEquipItems.DecreaseAmmos(_arrowType, amount, out _, null);
        }

        [Test]
        public void FiringTakesArrowsFromSeveralStacksAndLeavesOtherItemsAlone()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_arrows, 1, 10));
            _player.NonEquipItems.Add(CharacterItem.Create(_potion, 1, 2));
            _player.NonEquipItems.Add(CharacterItem.Create(_arrows, 1, 5));

            Assert.That(Shoot(12), Is.True);

            Assert.That(TestInventory.Count(_player, _arrows), Is.EqualTo(3));
            Assert.That(TestInventory.Count(_player, _potion), Is.EqualTo(2));
        }

        [Test]
        public void FiringWithTooFewArrowsTakesNone()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_arrows, 1, 4));

            Assert.That(Shoot(5), Is.False);
            Assert.That(TestInventory.Count(_player, _arrows), Is.EqualTo(4));
        }

        [Test]
        public void LoadingAndUnloadingAMagazineKeepsEveryArrow()
        {
            WeaponItem crossbow = TestGear.Equipment<WeaponItem>(Objects, default)
                .With("ammoItems", new BaseItem[] { _arrows })
                .With("ammoCapacity", 8);
            CharacterItem weapon = CharacterItem.Create(crossbow);
            weapon.ammo = 0;
            _player.NonEquipItems.Add(weapon);
            CharacterItem quiver = CharacterItem.Create(_arrows, 1, 10);
            _player.NonEquipItems.Add(quiver);

            Assert.That(_player.PutAmmoToItem(InventoryType.NonEquipItems, 0, 0, quiver.id, out _), Is.True);
            Assert.That(_player.NonEquipItems[0].ammo, Is.EqualTo(8), "loaded to capacity");
            Assert.That(TestInventory.Count(_player, _arrows), Is.EqualTo(2), "the rest stays in the bag");

            Assert.That(_player.RemoveAmmoFromItem(InventoryType.NonEquipItems, 0, 0, out _), Is.True);
            Assert.That(_player.NonEquipItems[0].ammo, Is.Zero);
            Assert.That(TestInventory.Count(_player, _arrows), Is.EqualTo(10), "every arrow back");
        }
    }
}
