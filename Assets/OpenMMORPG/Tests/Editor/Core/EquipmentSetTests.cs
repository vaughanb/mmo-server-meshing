using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Equipment set bonuses. A set's first bonus (effects[0]) is the TWO-piece bonus - one piece
    /// alone gives nothing - and each further piece adds the next bonus on top of the earlier
    /// ones. Weapons in hand count towards their set like armour does. The demo's armour sets
    /// are written against this, so an off-by-one here silently moves every set bonus a piece.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class EquipmentSetTests : KitTestFixture
    {
        private PlayerCharacterData _player;
        private EquipmentSet _set;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _player = TestCharacters.NewPlayer();
            _set = TestGear.Set(Objects,
                new CharacterStats { hp = 10f },
                new CharacterStats { hp = 100f },
                new CharacterStats { hp = 1000f });
        }

        private void Wear(int pieces, EquipmentSet set)
        {
            for (int i = 0; i < pieces; ++i)
                _player.EquipItems.Add(CharacterItem.Create(TestGear.Equipment<ArmorItem>(Objects, default, set)));
        }

        private float Hp()
        {
            return TestGear.StatsOf(_player).hp;
        }

        [Test]
        public void OnePieceAloneGivesNoSetBonus()
        {
            Wear(1, _set);

            Assert.That(Hp(), Is.EqualTo(0f));
        }

        [Test]
        public void TwoPiecesGiveTheFirstBonus()
        {
            Wear(2, _set);

            Assert.That(Hp(), Is.EqualTo(10f));
        }

        [Test]
        public void EachFurtherPieceStacksTheNextBonusOnTheEarlierOnes()
        {
            Wear(3, _set);
            Assert.That(Hp(), Is.EqualTo(110f), "three pieces");

            Wear(1, _set);
            Assert.That(Hp(), Is.EqualTo(1110f), "four pieces");
        }

        [Test]
        public void PiecesBeyondTheLastBonusAddNothingMore()
        {
            Wear(7, _set);

            Assert.That(Hp(), Is.EqualTo(1110f));
        }

        [Test]
        public void AWeaponInHandCountsTowardsItsSet()
        {
            WeaponItem sword = TestGear.Equipment<WeaponItem>(Objects, default, _set);
            _player.EquipWeapons = new EquipWeapons { rightHand = CharacterItem.Create(sword) };
            Wear(1, _set);

            Assert.That(Hp(), Is.EqualTo(10f));
        }

        [Test]
        public void PiecesOfDifferentSetsDoNotMakeASet()
        {
            EquipmentSet other = TestGear.Set(Objects, new CharacterStats { hp = 5f });
            Wear(1, _set);
            Wear(1, other);

            Assert.That(Hp(), Is.EqualTo(0f));
        }

        [Test]
        public void SetBonusesAreLeftOutWhenEquipmentIsNotSummed()
        {
            Wear(3, _set);

            Assert.That(TestGear.StatsOf(_player, sumWithEquipments: false).hp, Is.EqualTo(0f));
        }
    }
}
