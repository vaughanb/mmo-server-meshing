using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The per-item cache (CharacterItemCacheData) that the stats and the item UI read an item's
    /// buff from. It rebuilds the buff when what the buff is rolled from changes: the item, its
    /// level, its random seed or its version (the random bonus rules changed between versions,
    /// so one seed rolls differently under each).
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class ItemCacheTests : KitTestFixture
    {
        private ArmorItem _ring;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            Attribute strength = Objects.Create<Attribute>("Strength");
            Attribute dexterity = Objects.Create<Attribute>("Dexterity");
            GameInstance.Attributes[strength.DataId] = strength;
            GameInstance.Attributes[dexterity.DataId] = dexterity;
            // One bonus out of two certain ones: version 1 takes them in order, version 2 shuffles.
            _ring = TestGear.Equipment<ArmorItem>(Objects, default).With("randomBonus", new ItemRandomBonus
            {
                maxRandomStatsAmount = 1,
                randomAttributeAmounts = new[]
                {
                    new AttributeRandomAmount { attribute = strength, minAmount = 5f, maxAmount = 5f, applyRate = 1f },
                    new AttributeRandomAmount { attribute = dexterity, minAmount = 7f, maxAmount = 7f, applyRate = 1f },
                },
            });
        }

        private CharacterItem Ring(int randomSeed, byte version)
        {
            CharacterItem item = CharacterItem.Create(_ring, level: 1, randomSeed: randomSeed);
            item.version = version;
            return item;
        }

        private static Dictionary<Attribute, float> Bonus(CharacterItemCacheData cache, CharacterItem item)
        {
            cache.Prepare(in item);
            return new Dictionary<Attribute, float>(cache.GetBuff().GetIncreaseAttributes());
        }

        [Test]
        public void AnItemWhoseVersionChangesHasItsBonusRolledAgain()
        {
            // A seed that rolls differently under versions 1 and 2, read from fresh caches.
            int seed = 0;
            Dictionary<Attribute, float> asVersion2 = null;
            for (int s = 1; s <= 100 && asVersion2 == null; ++s)
            {
                Dictionary<Attribute, float> v1 = Bonus(new CharacterItemCacheData(), Ring(s, 1));
                Dictionary<Attribute, float> v2 = Bonus(new CharacterItemCacheData(), Ring(s, 2));
                if (!new HashSet<Attribute>(v1.Keys).SetEquals(v2.Keys))
                {
                    seed = s;
                    asVersion2 = v2;
                }
            }
            Assert.That(asVersion2, Is.Not.Null, "a seed that rolls differently under versions 1 and 2");

            // The same item (level 1, version 1) cached, then upgraded to version 2.
            CharacterItemCacheData cache = new CharacterItemCacheData();
            Bonus(cache, Ring(seed, 1));

            Assert.That(Bonus(cache, Ring(seed, 2)), Is.EquivalentTo(asVersion2));
        }
    }
}
