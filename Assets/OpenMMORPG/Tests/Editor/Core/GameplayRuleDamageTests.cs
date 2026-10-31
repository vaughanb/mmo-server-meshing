using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// What the default rule does to a hit's damage: criticals multiply it by the attacker's
    /// critical damage rate, blocks take off the defender's block share (never all of it, never
    /// none), resistance takes off its share up to the element's maximum, and armour divides by
    /// (100 + armour) / 100. Resistance runs through two overloads (indexed, and dictionaries for
    /// custom rules) and subclasses of DefaultGameplayRule are sent down the dictionary one, so the
    /// two must agree or SimpleGameplayRule - the demo's rule - takes different damage.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class GameplayRuleDamageTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private DefaultGameplayRule _rule;
        private DamageElement _fire;
        private DamageElement _ice;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _rule = Objects.Create<DefaultGameplayRule>();
            _fire = Objects.Create<DamageElement>("Fire").With("maxResistanceAmount", 0.75f);
            _ice = Objects.Create<DamageElement>("Ice");
            RuntimeGameDataSlots.Register(_fire);
            RuntimeGameDataSlots.Register(_ice);
        }

        private PlayerCharacterEntity Character(CharacterStats stats)
        {
            return TestCombatants.Create<PlayerCharacterEntity>(Objects, 1, stats);
        }

        private static DamageElementFloatAmounts Amounts(DamageElement element, float amount)
        {
            DamageElementFloatAmounts amounts = default;
            if (element != null)
                amounts.Add(element.RuntimeSlot, amount);
            return amounts;
        }

        private float Reduced(BaseGameplayRule rule, float damage, DamageElement element,
            DamageElement resistElement = null, float resistance = 0f, DamageElement armourElement = null, float armour = 0f)
        {
            return rule.GetDamageReducedByResistance(Amounts(resistElement, resistance), Amounts(armourElement, armour), damage, element);
        }

        [Test]
        public void CriticalDamageIsMultipliedByTheAttackersCritDamageRate()
        {
            PlayerCharacterEntity attacker = Character(new CharacterStats { criDmgRate = 1.5f });
            PlayerCharacterEntity target = Character(new CharacterStats { criDmgRate = 9f });

            Assert.That(_rule.GetCriticalDamage(attacker, target, 40f), Is.EqualTo(60f).Within(1e-4f));
        }

        [Test]
        public void ABlockTakesOffTheDefendersBlockShare()
        {
            PlayerCharacterEntity attacker = Character(new CharacterStats { blockDmgRate = 0.9f });

            Assert.That(_rule.GetBlockDamage(attacker, Character(new CharacterStats { blockDmgRate = 0.25f }), 100f),
                Is.EqualTo(75f).Within(1e-4f));
        }

        [Test]
        public void ABlockNeverStopsAllTheDamageNorNoneOfIt()
        {
            PlayerCharacterEntity attacker = Character(default);

            Assert.That(_rule.GetBlockDamage(attacker, Character(new CharacterStats { blockDmgRate = 5f }), 100f),
                Is.EqualTo(5f).Within(1e-4f), "a block lets at least 5% through");
            Assert.That(_rule.GetBlockDamage(attacker, Character(new CharacterStats { blockDmgRate = 0f }), 100f),
                Is.EqualTo(95f).Within(1e-4f), "a block stops at least 5%");
        }

        [Test]
        public void TotalDamageRoundsDown()
        {
            Assert.That(_rule.GetTotalDamage(Vector3.zero, EntityInfo.Empty, null, 9.99f, default, null, 0), Is.EqualTo(9));
        }

        [Test]
        public void ResistanceTakesOffItsShareOfTheDamage()
        {
            Assert.That(Reduced(_rule, 100f, _ice, _ice, 0.25f), Is.EqualTo(75f).Within(1e-4f));
        }

        [Test]
        public void ResistanceAboveTheElementsMaximumCountsAsTheMaximum()
        {
            Assert.That(Reduced(_rule, 100f, _fire, _fire, 5f), Is.EqualTo(Reduced(_rule, 100f, _fire, _fire, 0.75f)));
            Assert.That(Reduced(_rule, 100f, _fire, _fire, 5f), Is.GreaterThan(0f), "fire can never be fully resisted");
        }

        [Test]
        public void NegativeResistanceIncreasesTheDamage()
        {
            Assert.That(Reduced(_rule, 100f, _ice, _ice, -0.5f), Is.EqualTo(150f).Within(1e-4f));
        }

        [Test]
        public void ResistanceOnlyCountsAgainstItsOwnElement()
        {
            Assert.That(Reduced(_rule, 100f, _ice, _fire, 0.5f, _fire, 100f), Is.EqualTo(100f));
        }

        [Test]
        public void ArmourDividesDamageBy100PlusArmourOver100()
        {
            Assert.That(Reduced(_rule, 100f, _ice, armourElement: _ice, armour: 100f), Is.EqualTo(50f).Within(1e-4f));

            float previous = float.MaxValue;
            foreach (float armour in new[] { 0f, 10f, 50f, 200f, 1000f })
            {
                float damage = Reduced(_rule, 100f, _ice, armourElement: _ice, armour: armour);
                Assert.That(damage, Is.LessThan(previous), $"armour {armour}");
                Assert.That(damage, Is.GreaterThan(0f), $"armour {armour}");
                previous = damage;
            }
        }

        [Test]
        public void NegativeArmourDoesNotIncreaseTheDamage()
        {
            Assert.That(Reduced(_rule, 100f, _ice, armourElement: _ice, armour: -50f), Is.EqualTo(100f));
        }

        [Test]
        public void DamageWithNoElementIsResistedAsTheDefaultElement()
        {
            DamageElement defaultElement = _gameInstance.DefaultDamageElement;

            Assert.That(Reduced(_rule, 100f, null, defaultElement, 0.5f), Is.EqualTo(50f).Within(1e-4f));
        }

        [Test]
        public void ASubclassRuleReducesDamageExactlyAsTheDefaultRuleDoes()
        {
            SimpleGameplayRule simple = Objects.Create<SimpleGameplayRule>();
            var cases = new List<(DamageElement element, DamageElement resist, float resistance, DamageElement armourElement, float armour)>
            {
                (_ice, _ice, 0.25f, _ice, 40f),
                (_fire, _fire, 5f, _fire, 10f),
                (_ice, _ice, -0.5f, null, 0f),
                (_ice, _fire, 0.5f, _fire, 100f),
                (_ice, null, 0f, _ice, -50f),
                (null, _gameInstance.DefaultDamageElement, 0.3f, _gameInstance.DefaultDamageElement, 25f),
            };

            foreach (var c in cases)
            {
                float expected = Reduced(_rule, 120f, c.element, c.resist, c.resistance, c.armourElement, c.armour);
                float actual = Reduced(simple, 120f, c.element, c.resist, c.resistance, c.armourElement, c.armour);
                Assert.That(actual, Is.EqualTo(expected).Within(1e-4f),
                    $"{c.element?.name ?? "no element"}, resist {c.resistance}, armour {c.armour}");
            }
        }
    }
}
