using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The default rule's hit, critical and block chances. Every swing in the game rolls against
    /// these, so a lost cap or a swapped term changes every fight. The documented contract: each
    /// chance stays within 5% and 95%, the hit chance weighs accuracy against evasion and level
    /// against level, the critical chance is the attacker's, the block chance the defender's.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class GameplayRuleChanceTests : KitTestFixture
    {
        private DefaultGameplayRule _rule;

        [SetUp]
        public void SetUp()
        {
            _rule = Objects.Create<DefaultGameplayRule>();
        }

        private PlayerCharacterEntity Character(int level, CharacterStats stats = default)
        {
            return TestCombatants.Create<PlayerCharacterEntity>(Objects, level, stats);
        }

        private float HitChance(int attackerLevel, float accuracy, int targetLevel, float evasion)
        {
            return _rule.GetHitChance(
                Character(attackerLevel, new CharacterStats { accuracy = accuracy }),
                Character(targetLevel, new CharacterStats { evasion = evasion }));
        }

        [Test]
        public void HitChanceNeverExceeds95PercentHoweverOutclassedTheTarget()
        {
            Assert.That(HitChance(50, 1000f, 1, 1f), Is.EqualTo(0.95f));
        }

        [Test]
        public void HitChanceNeverFallsBelow5PercentHoweverOutclassedTheAttacker()
        {
            Assert.That(HitChance(1, 1f, 50, 1000f), Is.EqualTo(0.05f));
        }

        [Test]
        public void AnEvenMatchHitsHalfTheTime()
        {
            Assert.That(HitChance(10, 50f, 10, 50f), Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void HitChanceRisesWithAccuracyAndFallsWithEvasion()
        {
            float low = HitChance(10, 40f, 10, 50f);
            float even = HitChance(10, 50f, 10, 50f);
            float high = HitChance(10, 60f, 10, 50f);
            float evasive = HitChance(10, 50f, 10, 60f);

            Assert.That(low, Is.LessThan(even));
            Assert.That(high, Is.GreaterThan(even));
            Assert.That(evasive, Is.LessThan(even));
        }

        [Test]
        public void HitChanceFallsAsTheTargetOutlevelsTheAttacker()
        {
            float previous = float.MaxValue;
            for (int targetLevel = 10; targetLevel <= 20; ++targetLevel)
            {
                float chance = HitChance(10, 50f, targetLevel, 50f);
                Assert.That(chance, Is.LessThan(previous), $"target level {targetLevel}");
                previous = chance;
            }
        }

        [Test]
        public void AccuracyIsIgnoredWhenTheTargetHasNoEvasion()
        {
            // The accuracy term only applies when both sides have a value; without it an even
            // level match is the 95% ceiling, whatever the attacker's accuracy.
            Assert.That(HitChance(10, 1f, 10, 0f), Is.EqualTo(HitChance(10, 500f, 10, 0f)));
            Assert.That(HitChance(10, 1f, 10, 0f), Is.EqualTo(0.95f));
        }

        [Test]
        public void CriticalChanceIsTheAttackersCritRateWithin5And95Percent()
        {
            PlayerCharacterEntity target = Character(1, new CharacterStats { criRate = 0.9f });

            Assert.That(_rule.GetCriticalChance(Character(1, new CharacterStats { criRate = 0.3f }), target), Is.EqualTo(0.3f));
            Assert.That(_rule.GetCriticalChance(Character(1, new CharacterStats { criRate = 0f }), target), Is.EqualTo(0.05f));
            Assert.That(_rule.GetCriticalChance(Character(1, new CharacterStats { criRate = 3f }), target), Is.EqualTo(0.95f));
        }

        [Test]
        public void BlockChanceIsTheDefendersBlockRateWithin5And95Percent()
        {
            PlayerCharacterEntity attacker = Character(1, new CharacterStats { blockRate = 0.9f });

            Assert.That(_rule.GetBlockChance(attacker, Character(1, new CharacterStats { blockRate = 0.4f })), Is.EqualTo(0.4f));
            Assert.That(_rule.GetBlockChance(attacker, Character(1, new CharacterStats { blockRate = 0f })), Is.EqualTo(0.05f));
            Assert.That(_rule.GetBlockChance(attacker, Character(1, new CharacterStats { blockRate = 2f })), Is.EqualTo(0.95f));
        }

        [Test]
        public void AnAttackWithNoAttackerAlwaysHitsWithoutCritOrBlock()
        {
            for (int i = 0; i < 20; ++i)
            {
                bool hit = _rule.RandomAttackHitOccurs(Vector3.zero, null, Character(1), default, default, null, 0, i,
                    out bool isCritical, out bool isBlocked);
                Assert.That(hit, Is.True);
                Assert.That(isCritical, Is.False);
                Assert.That(isBlocked, Is.False);
            }
        }

        [Test]
        public void ACriticalAlwaysLandsWhenTheRuleSaysSo()
        {
            // Worst case to hit (5%) and best case to crit (95%): with the setting on, no roll
            // may come out a critical miss; with it off, some must.
            PlayerCharacterEntity attacker = Character(1, new CharacterStats { accuracy = 1f, criRate = 1f });
            PlayerCharacterEntity target = Character(50, new CharacterStats { evasion = 1000f });

            _rule.alwaysHitWhenCriticalOccurs = true;
            for (int i = 0; i < 200; ++i)
            {
                bool hit = _rule.RandomAttackHitOccurs(Vector3.zero, attacker, target, default, default, null, 0, i,
                    out bool isCritical, out _);
                Assert.That(!isCritical || hit, $"roll {i} was a critical miss");
            }

            _rule.alwaysHitWhenCriticalOccurs = false;
            int criticalMisses = 0;
            for (int i = 0; i < 200; ++i)
            {
                bool hit = _rule.RandomAttackHitOccurs(Vector3.zero, attacker, target, default, default, null, 0, i,
                    out bool isCritical, out _);
                if (isCritical && !hit)
                    ++criticalMisses;
            }
            Assert.That(criticalMisses, Is.GreaterThan(0));
        }
    }
}
