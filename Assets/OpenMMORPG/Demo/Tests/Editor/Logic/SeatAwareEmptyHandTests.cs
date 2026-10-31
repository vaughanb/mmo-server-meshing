using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// Every draw and sheathe played on two animation layers (2026-10-04). The kit records what
    /// the hands show with Clone(), which gives an item with no id a new one, so an empty left
    /// hand never matched an empty left hand. SeatAwarePlayableCharacterModel compares empty hands
    /// as empty.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SeatAwareEmptyHandTests : KitTestFixture
    {
        private CharacterItem _sword;
        private CharacterItem _bow;

        [SetUp]
        public void SetUp()
        {
            // An item the game does not know reads as an empty hand, so register real ones.
            UseGameInstance();
            _sword = Register("TestSword");
            _bow = Register("TestBow");
        }

        private CharacterItem Register(string name)
        {
            Item item = Objects.Create<Item>(name);
            GameInstance.Items[item.DataId] = item;
            return CharacterItem.Create(item);
        }

        private static bool SameWeapons(EquipWeapons a, EquipWeapons b)
        {
            return (bool)TestReflection.InvokeStatic(typeof(SeatAwarePlayableCharacterModel), "SameWeapons", a, b);
        }

        [Test]
        public void CloneGivesAnEmptyHandANewId()
        {
            EquipWeapons shown = new EquipWeapons();

            Assert.That(shown.Clone().leftHand.id, Is.Not.EqualTo(shown.leftHand.id),
                "the kit behaviour this works around; if Clone keeps empty ids, the workaround can go");
        }

        [Test]
        public void EmptyHandsMatchAfterAClone()
        {
            EquipWeapons shown = new EquipWeapons() { rightHand = _sword };

            Assert.That(SameWeapons(shown, shown.Clone()), Is.True);
        }

        [Test]
        public void ADifferentWeaponDoesNotMatch()
        {
            EquipWeapons sword = new EquipWeapons() { rightHand = _sword };

            Assert.That(SameWeapons(sword, new EquipWeapons() { rightHand = _bow }), Is.False);
            Assert.That(SameWeapons(sword, new EquipWeapons()), Is.False, "drawn against empty");
        }
    }
}
