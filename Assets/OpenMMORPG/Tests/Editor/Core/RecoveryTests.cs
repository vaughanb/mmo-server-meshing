using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The default rule's per-second recovery and drain. Health and mana regenerate by a share of
    /// the maximum plus the recovery stat, and stop when the character is hungry (health) or
    /// thirsty (mana); hunger and thirst only exist while food and water drain. Starving drains
    /// health, and monsters neither starve nor go hungry.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class RecoveryTests : KitTestFixture
    {
        private DefaultGameplayRule _rule;

        [SetUp]
        public void SetUp()
        {
            _rule = Objects.Create<DefaultGameplayRule>();
            _rule.hpRecoveryRatePerSeconds = 0.05f;
            _rule.mpRecoveryRatePerSeconds = 0.1f;
            _rule.hungryWhenFoodLowerThan = 40;
            _rule.thirstyWhenWaterLowerThan = 40;
            _rule.foodDecreasePerSeconds = 4f;
            _rule.waterDecreasePerSeconds = 2f;
        }

        private PlayerCharacterEntity Player(CharacterStats stats)
        {
            return TestCombatants.Create<PlayerCharacterEntity>(Objects, 1, stats);
        }

        [Test]
        public void HealthRegenIsAShareOfMaxHealthPlusTheRecoveryStat()
        {
            PlayerCharacterEntity player = Player(new CharacterStats { hp = 200f, hpRecovery = 3f });

            Assert.That(_rule.GetRecoveryHpPerSeconds(player), Is.EqualTo(200f * 0.05f + 3f).Within(1e-4f));
        }

        [Test]
        public void ManaRegenIsAShareOfMaxManaPlusTheRecoveryStat()
        {
            PlayerCharacterEntity player = Player(new CharacterStats { mp = 50f, mpRecovery = 2f });

            Assert.That(_rule.GetRecoveryMpPerSeconds(player), Is.EqualTo(50f * 0.1f + 2f).Within(1e-4f));
        }

        [Test]
        public void AHungryCharacterRegainsNoHealthAndAThirstyOneNoMana()
        {
            PlayerCharacterEntity player = Player(new CharacterStats { hp = 200f, mp = 50f, hpRecovery = 3f, mpRecovery = 2f });
            player.CurrentFood = 39;

            Assert.That(_rule.GetRecoveryHpPerSeconds(player), Is.EqualTo(0f), "hungry");
            Assert.That(_rule.GetRecoveryMpPerSeconds(player), Is.GreaterThan(0f), "hunger leaves mana alone");

            player.CurrentFood = 100;
            player.CurrentWater = 39;
            Assert.That(_rule.GetRecoveryMpPerSeconds(player), Is.EqualTo(0f), "thirsty");
            Assert.That(_rule.GetRecoveryHpPerSeconds(player), Is.GreaterThan(0f), "thirst leaves health alone");
        }

        [Test]
        public void ThereIsNoHungerWhenFoodDoesNotDrain()
        {
            _rule.foodDecreasePerSeconds = 0f;
            PlayerCharacterEntity player = Player(new CharacterStats { hp = 200f });
            player.CurrentFood = 0;

            Assert.That(_rule.IsHungry(player), Is.False);
            Assert.That(_rule.GetRecoveryHpPerSeconds(player), Is.GreaterThan(0f));
        }

        [Test]
        public void StaminaRegenIsTheRulesRatePlusTheRecoveryStat()
        {
            _rule.staminaRecoveryPerSeconds = 5f;

            Assert.That(_rule.GetRecoveryStaminaPerSeconds(Player(new CharacterStats { staminaRecovery = 1.5f })),
                Is.EqualTo(6.5f).Within(1e-4f));
        }

        [Test]
        public void HungerAndThirstEachDrainAShareOfMaxHealth()
        {
            _rule.hpDecreaseRatePerSecondsWhenHungry = 0.02f;
            _rule.hpDecreaseRatePerSecondsWhenThirsty = 0.03f;
            PlayerCharacterEntity player = Player(new CharacterStats { hp = 100f });

            Assert.That(_rule.GetDecreasingHpPerSeconds(player), Is.EqualTo(0f), "fed and watered");
            player.CurrentFood = 0;
            Assert.That(_rule.GetDecreasingHpPerSeconds(player), Is.EqualTo(2f).Within(1e-4f), "hungry");
            player.CurrentWater = 0;
            Assert.That(_rule.GetDecreasingHpPerSeconds(player), Is.EqualTo(5f).Within(1e-4f), "hungry and thirsty");
        }

        [Test]
        public void FoodDrainIsSlowedByTheStatButNeverReversed()
        {
            Assert.That(_rule.GetDecreasingFoodPerSeconds(Player(new CharacterStats { decreaseFoodDecreation = 1f })),
                Is.EqualTo(3f).Within(1e-4f));
            Assert.That(_rule.GetDecreasingFoodPerSeconds(Player(new CharacterStats { decreaseFoodDecreation = 10f })),
                Is.EqualTo(0f));
            Assert.That(_rule.GetDecreasingWaterPerSeconds(Player(new CharacterStats { decreaseWaterDecreation = 10f })),
                Is.EqualTo(0f));
        }

        [Test]
        public void MonstersNeitherGetHungryNorStarve()
        {
            MonsterCharacterEntity monster = TestCombatants.Create<MonsterCharacterEntity>(Objects, 1, new CharacterStats { hp = 100f });
            monster.CurrentFood = 0;
            monster.CurrentWater = 0;

            Assert.That(_rule.GetDecreasingFoodPerSeconds(monster), Is.EqualTo(0f));
            Assert.That(_rule.GetDecreasingWaterPerSeconds(monster), Is.EqualTo(0f));
            Assert.That(_rule.GetDecreasingHpPerSeconds(monster), Is.EqualTo(0f));
        }
    }
}
