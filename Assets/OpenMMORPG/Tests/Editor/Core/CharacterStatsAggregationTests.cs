using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// GetAllStats, which every character's stats cache is built from: the class's stats at the
    /// character's level, plus attribute points (the character's and its gear's) turned into stats,
    /// plus equipped gear, plus buffs, with stat rates applied to that whole sum. Carried gear,
    /// broken gear and anything left out by the sum flags must not count. Also the rule's
    /// durability tiers, which scale a worn item's armour and damage.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class CharacterStatsAggregationTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _player = TestCharacters.NewPlayer();
        }

        private PlayerCharacter RegisterClass(CharacterStatsIncremental stats)
        {
            PlayerCharacter playerClass = Objects.Create<PlayerCharacter>("TestClass");
            playerClass.Stats = stats;
            GameInstance.Characters[playerClass.DataId] = playerClass;
            return playerClass;
        }

        private ArmorItem Armour(float hp, float maxDurability = 0f)
        {
            return TestGear.Equipment<ArmorItem>(Objects, new CharacterStats { hp = hp }, maxDurability: maxDurability);
        }

        private float Hp(bool sumWithEquipments = true, bool sumWithBuffs = true)
        {
            return TestGear.StatsOf(_player, sumWithEquipments, sumWithBuffs).hp;
        }

        [Test]
        public void ClassStatsGrowWithTheCharactersLevel()
        {
            _player.DataId = RegisterClass(new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 100f },
                statsIncreaseEachLevel = new CharacterStats { hp = 10f },
            }).DataId;

            _player.Level = 1;
            Assert.That(Hp(), Is.EqualTo(100f));
            _player.Level = 3;
            Assert.That(Hp(), Is.EqualTo(120f));
        }

        [Test]
        public void EquippedGearAddsItsStatsButCarriedGearDoesNot()
        {
            _player.EquipItems.Add(CharacterItem.Create(Armour(25f)));
            _player.NonEquipItems.Add(CharacterItem.Create(Armour(1000f)));

            Assert.That(Hp(), Is.EqualTo(25f));
        }

        [Test]
        public void GearIsLeftOutWhenEquipmentIsNotSummed()
        {
            _player.EquipItems.Add(CharacterItem.Create(Armour(25f)));

            Assert.That(Hp(sumWithEquipments: false), Is.EqualTo(0f));
        }

        [Test]
        public void BrokenGearGivesNoStatsButWornGearStillDoes()
        {
            CharacterItem broken = CharacterItem.Create(Armour(25f, maxDurability: 100f));
            broken.durability = 0f;
            CharacterItem worn = CharacterItem.Create(Armour(7f, maxDurability: 100f));
            worn.durability = 1f;
            _player.EquipItems.Add(broken);
            _player.EquipItems.Add(worn);

            Assert.That(Hp(), Is.EqualTo(7f));
        }

        [Test]
        public void ABrokenOffHandItemGivesNoStats()
        {
            WeaponItem sword = TestGear.Equipment<WeaponItem>(Objects, new CharacterStats { hp = 3f });
            ShieldItem shield = TestGear.Equipment<ShieldItem>(Objects, new CharacterStats { hp = 40f }, maxDurability: 100f);
            CharacterItem brokenShield = CharacterItem.Create(shield);
            brokenShield.durability = 0f;
            _player.EquipWeapons = new EquipWeapons
            {
                rightHand = CharacterItem.Create(sword),
                leftHand = brokenShield,
            };

            Assert.That(Hp(), Is.EqualTo(3f), "only the sword counts");
        }

        [Test]
        public void StatRatesScaleTheClassStatsAndFlatBonusesTogether()
        {
            _player.DataId = RegisterClass(new CharacterStatsIncremental { baseStats = new CharacterStats { hp = 100f } }).DataId;
            ArmorItem amulet = Armour(0f).With("increaseStatsRate",
                new CharacterStatsIncremental { baseStats = new CharacterStats { hp = 0.5f } });
            _player.EquipItems.Add(CharacterItem.Create(Armour(20f)));
            _player.EquipItems.Add(CharacterItem.Create(amulet));

            Assert.That(Hp(), Is.EqualTo((100f + 20f) * 1.5f).Within(1e-3f));
        }

        [Test]
        public void AttributePointsFromTheCharacterAndItsGearBecomeStats()
        {
            Attribute vitality = Objects.Create<Attribute>("Vitality")
                .With("statsIncreaseEachLevel", new CharacterStats { hp = 5f });
            GameInstance.Attributes[vitality.DataId] = vitality;
            RuntimeGameDataSlots.Register(vitality);
            ArmorItem belt = Armour(0f).With("increaseAttributes", new[]
            {
                new AttributeIncremental { attribute = vitality, amount = new IncrementalFloat { baseAmount = 2f } },
            });
            _player.Attributes.Add(CharacterAttribute.Create(vitality, 4));
            _player.EquipItems.Add(CharacterItem.Create(belt));

            Assert.That(Hp(), Is.EqualTo((4f + 2f) * 5f));
        }

        [Test]
        public void ABuffAddsItsStatsOnlyWhenBuffsAreSummed()
        {
            StatusEffect blessing = Objects.Create<StatusEffect>("Blessing");
            blessing.Buff.increaseStats = new CharacterStatsIncremental { baseStats = new CharacterStats { hp = 30f } };
            GameInstance.StatusEffects[blessing.DataId] = blessing;
            _player.Buffs.Add(CharacterBuff.Create(BuffType.StatusEffect, blessing.DataId));

            Assert.That(Hp(), Is.EqualTo(30f));
            Assert.That(Hp(sumWithBuffs: false), Is.EqualTo(0f));
        }

        [Test]
        public void ABuffsStatsGrowWithTheBuffsLevel()
        {
            StatusEffect blessing = Objects.Create<StatusEffect>("Blessing");
            blessing.Buff.increaseStats = new CharacterStatsIncremental
            {
                baseStats = new CharacterStats { hp = 10f },
                statsIncreaseEachLevel = new CharacterStats { hp = 5f },
            };
            GameInstance.StatusEffects[blessing.DataId] = blessing;
            _player.Buffs.Add(CharacterBuff.Create(BuffType.StatusEffect, blessing.DataId, 3));

            Assert.That(Hp(), Is.EqualTo(20f));
        }

        [Test]
        public void StatusEffectResistanceIsCappedAtTheEffectsMaximum()
        {
            StatusEffect stun = Objects.Create<StatusEffect>("Stun").With("maxResistanceAmount", 1.5f);
            ArmorItem helm = Armour(0f).With("increaseStatusEffectResistances", new[]
            {
                new StatusEffectResistanceIncremental { statusEffect = stun, amount = new IncrementalFloat { baseAmount = 4f } },
            });
            _player.EquipItems.Add(CharacterItem.Create(helm));

            float resistance = -1f;
            _player.GetAllStats(true, true, true,
                onGetStatusEffectResistances: resistances => resistance = resistances.TryGetValue(stun, out float value) ? value : 0f);

            Assert.That(resistance, Is.EqualTo(1.5f));
        }

        [Test]
        public void WornGearKeepsFullStrengthUntilHalfDurabilityThenStepsDownToNothing()
        {
            DefaultGameplayRule rule = _gameInstance.GameplayRule;
            ArmorItem armour = Armour(0f, maxDurability: 100f);
            CharacterItem item = CharacterItem.Create(armour);
            float Rate(float durability)
            {
                item.durability = durability;
                return rule.GetEquipmentStatsRate(item);
            }

            Assert.That(Rate(100f), Is.EqualTo(1f));
            Assert.That(Rate(51f), Is.EqualTo(1f));
            Assert.That(Rate(0f), Is.EqualTo(0f), "broken");
            float previous = 1f;
            foreach (float durability in new[] { 100f, 60f, 50f, 40f, 30f, 20f, 15f, 10f, 5f, 1f, 0f })
            {
                float rate = Rate(durability);
                Assert.That(rate, Is.LessThanOrEqualTo(previous), $"durability {durability}");
                Assert.That(rate, Is.InRange(0f, 1f));
                previous = rate;
            }
        }

        [Test]
        public void GearWithoutDurabilityIsAlwaysAtFullStrength()
        {
            CharacterItem item = CharacterItem.Create(Armour(0f));
            item.durability = 0f;

            Assert.That(_gameInstance.GameplayRule.GetEquipmentStatsRate(item), Is.EqualTo(1f));
        }
    }
}
