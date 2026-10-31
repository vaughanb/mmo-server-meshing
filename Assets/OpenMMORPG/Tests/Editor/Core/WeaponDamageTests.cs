using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    [Category(TestCategories.Harnessed)]
    public class WeaponDamageTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
        }

        private DamageElementMinMaxFloatAmounts WeaponDamages(DamageElement element, float min, float max)
        {
            DamageElementMinMaxFloatAmounts result = default;
            CharacterDataExtensions.GetWeaponDamages(
                CharacterItem.Create(_gameInstance.DefaultWeaponItem),
                _gameInstance.DefaultWeaponItem,
                new KeyValuePair<DamageElement, MinMaxFloat>(element, new MinMaxFloat { min = min, max = max }),
                default, default, default, ref result);
            return result;
        }

        [Test]
        public void WeaponDamageWithNoElementIsDealtAsTheDefaultElement()
        {
            DamageElementMinMaxFloatAmounts damages = WeaponDamages(null, 10f, 20f);

            Assert.That(damages[0].min, Is.EqualTo(10f));
            Assert.That(damages[0].max, Is.EqualTo(20f));
        }

        [Test]
        public void WeaponDamageWithAnElementIsDealtAsThatElement()
        {
            DamageElement fire = Objects.Create<DamageElement>("Fire");
            RuntimeGameDataSlots.Register(fire);

            DamageElementMinMaxFloatAmounts damages = WeaponDamages(fire, 5f, 7f);

            Assert.That(damages[fire.RuntimeSlot].min, Is.EqualTo(5f));
            Assert.That(damages[fire.RuntimeSlot].max, Is.EqualTo(7f));
            Assert.That(damages[0].max, Is.EqualTo(0f), "nothing dealt as the default element");
        }
    }
}
