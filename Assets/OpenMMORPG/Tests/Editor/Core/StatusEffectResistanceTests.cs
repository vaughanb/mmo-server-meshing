using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The chance to shrug off a status effect, from a character's total resistance to it and the
    /// level the effect is applied at. Resistance above the effect's maximum counts as the maximum
    /// (zero means no maximum), the chance never passes 100%, a stronger application is never
    /// easier to resist, and the per-level table caps each level's chance (levels past the table
    /// use its last entry). Each whole point of resistance resists one level fully and the
    /// remainder partly resists the next, as the field tooltips' worked numbers show.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class StatusEffectResistanceTests : KitTestFixture
    {
        private StatusEffect Effect(float maxResistance, params float[] maxEachLevel)
        {
            return Objects.Create<StatusEffect>()
                .With("maxResistanceAmount", maxResistance)
                .With("maxResistanceAmountEachLevels", maxEachLevel);
        }

        [Test]
        public void ResistanceAboveTheMaximumCountsAsTheMaximum()
        {
            StatusEffect effect = Effect(1.5f);

            for (int level = 1; level <= 3; ++level)
                Assert.That(effect.GetResistanceByLevel(9f, level), Is.EqualTo(effect.GetResistanceByLevel(1.5f, level)), $"level {level}");
        }

        [Test]
        public void AMaximumOfZeroMeansNoMaximum()
        {
            Assert.That(Effect(0f).GetResistanceByLevel(3f, 3), Is.EqualTo(1f));
            Assert.That(Effect(0f).GetResistanceByLevel(3f, 3), Is.EqualTo(Effect(100f).GetResistanceByLevel(3f, 3)));
        }

        [Test]
        public void TheChanceNeverPasses100Percent()
        {
            Assert.That(Effect(0f).GetResistanceByLevel(50f, 1), Is.EqualTo(1f));
        }

        [Test]
        public void AStrongerApplicationIsNeverEasierToResist()
        {
            StatusEffect effect = Effect(0f);
            float previous = float.MaxValue;
            for (int level = 1; level <= 6; ++level)
            {
                float chance = effect.GetResistanceByLevel(2.5f, level);
                Assert.That(chance, Is.LessThanOrEqualTo(previous), $"level {level}");
                previous = chance;
            }
        }

        [Test]
        public void ThePerLevelTableCapsEachLevelAndItsLastEntryCoversHigherLevels()
        {
            StatusEffect effect = Effect(0f, 0.8f, 0.5f);

            Assert.That(effect.GetResistanceByLevel(10f, 1), Is.EqualTo(0.8f));
            Assert.That(effect.GetResistanceByLevel(10f, 2), Is.EqualTo(0.5f));
            Assert.That(effect.GetResistanceByLevel(10f, 5), Is.EqualTo(0.5f));
        }

        [Test]
        public void NoResistanceNeverResists()
        {
            StatusEffect effect = Effect(1f);
            for (int i = 0; i < 100; ++i)
                Assert.That(effect.RandomResistOccurs(0f, 1), Is.False);
        }

        [Test]
        public void FullResistanceAlwaysResists()
        {
            StatusEffect effect = Effect(1f);
            for (int i = 0; i < 100; ++i)
                Assert.That(effect.RandomResistOccurs(1f, 1), Is.True);
        }

        /// <summary>
        /// The code divided resistance by level (1.5 resisted level 2 at 75%) while both field tooltips
        /// subtract a level per whole point; the tooltips were taken as the intent (2026-10-29).
        /// </summary>
        [Test]
        public void ResistanceMatchesTheTooltipsWorkedExample()
        {
            // maxResistanceAmount's tooltip: "If status effect resistance is `1.5`, it will `100%`
            // resist status effect level `1` and `50%` resist status effect level `2`."
            StatusEffect effect = Effect(0f);

            Assert.That(effect.GetResistanceByLevel(1.5f, 1), Is.EqualTo(1f));
            Assert.That(effect.GetResistanceByLevel(1.5f, 2), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(effect.GetResistanceByLevel(1.5f, 3), Is.EqualTo(0f), "resistance runs out after its whole points and fraction");
        }

        [Test]
        public void ResistanceMatchesThePerLevelTablesWorkedExample()
        {
            // maxResistanceAmountEachLevels' tooltip: "If value is `[0.8, 0.5, 0.25]`, and your
            // character's status effect resistance is `2.15`, it will have chance `80%` to resist
            // status effect level `1`, `50%` to resist level `2`, and `15%` to resist level `3`."
            StatusEffect effect = Effect(0f, 0.8f, 0.5f, 0.25f);

            Assert.That(effect.GetResistanceByLevel(2.15f, 1), Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(effect.GetResistanceByLevel(2.15f, 2), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(effect.GetResistanceByLevel(2.15f, 3), Is.EqualTo(0.15f).Within(1e-5f));
        }
    }
}
