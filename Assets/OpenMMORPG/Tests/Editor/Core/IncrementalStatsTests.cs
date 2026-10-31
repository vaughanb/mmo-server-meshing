using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// How class, item and buff stats grow with level (CharacterStatsIncremental). Its tooltips
    /// are the contract: the base is the amount at level 1, the flat increase is added for each
    /// level above 1, and the rate is "+N% per level". IncrementalFloat, which every other
    /// levelled amount uses, compounds that rate once per level above 1; stats must grow the same
    /// way, or an hp curve and an hp potion tuned with the same numbers disagree.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class IncrementalStatsTests : KitTestFixture
    {
        [Test]
        public void FlatIncreaseIsAddedForEachLevelAboveOne()
        {
            CharacterStatsIncremental stats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 100f, mp = 20f },
                statsIncreaseEachLevel = new CharacterStats { hp = 10f, mp = 2f },
            };

            Assert.That(stats.GetCharacterStats(1).hp, Is.EqualTo(100f));
            Assert.That(stats.GetCharacterStats(5).hp, Is.EqualTo(140f));
            Assert.That(stats.GetCharacterStats(5).mp, Is.EqualTo(28f));
        }

        [Test]
        public void TheRateDoesNotApplyAtLevelOne()
        {
            CharacterStatsIncremental stats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 100f },
                rateIncreaseEachLevel = new CharacterStats { hp = 0.1f },
            };

            Assert.That(stats.GetCharacterStats(1).hp, Is.EqualTo(100f), "the base is the amount at level 1");
        }

        [Test]
        public void StatsGrowWithLevelExactlyAsAnIncrementalFloatDoes()
        {
            CharacterStatsIncremental stats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 100f },
                statsIncreaseEachLevel = new CharacterStats { hp = 10f },
                rateIncreaseEachLevel = new CharacterStats { hp = 0.05f },
            };
            IncrementalFloat amount = new IncrementalFloat
            {
                baseAmount = 100f,
                amountIncreaseEachLevel = 10f,
                rateIncreaseEachLevel = 0.05f,
            };

            for (int level = 1; level <= 6; ++level)
                Assert.That(stats.GetCharacterStats(level).hp, Is.EqualTo(amount.GetAmount(level)).Within(1e-3f), $"level {level}");
        }

        [Test]
        public void ATableEntryNotYetReachedLeavesTheDefaultGrowthInPlace()
        {
            CharacterStatsIncremental stats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 100f },
                statsIncreaseEachLevel = new CharacterStats { hp = 10f },
                statsIncreaseEachLevelByLevels = new[]
                {
                    new CharacterStatsIncrementalByLevel { minLevel = 10, statsIncreaseEachLevel = new CharacterStats { hp = 50f } },
                },
            };

            Assert.That(stats.GetCharacterStats(5).hp, Is.EqualTo(140f), "levels 2-5 use the default +10");
            Assert.That(stats.GetCharacterStats(11).hp, Is.EqualTo(100f + 8 * 10f + 2 * 50f), "levels 10 and 11 use the table's +50");
        }
    }
}
