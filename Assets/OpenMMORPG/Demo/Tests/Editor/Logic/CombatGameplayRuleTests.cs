using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;
using UnityEngine;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// The demo's CombatGameplayRule (2026-10-02, the user's call after playing the warrior).
    /// Hit chance: the kit's accuracy-against-evasion term and 95% ceiling, minus
    /// missPerLevelAbove for each level the target is above the attacker, floored at
    /// minHitChance - a gentler slope than the kit's level ratio. Health regen: the kit's amount,
    /// held to hpRegenRateInCombat of it for combatSeconds after dealing or taking damage, never
    /// touching a drain. Aggro range shrinks a metre a level the target is above the monster and
    /// grows a metre a level below, floored at aggroRangeMin. Rage regains no MP, focus a flat
    /// rate, mana the kit's; rage starts each life empty.
    /// </summary>
    public class CombatGameplayRuleTests : KitTestFixture
    {
        private CombatGameplayRule _rule;

        [SetUp]
        public void SetUp()
        {
            _rule = Objects.Create<CombatGameplayRule>();
            _rule.missPerLevelAbove = 0.05f;
            _rule.minHitChance = 0.5f;
            _rule.combatSeconds = 6f;
            _rule.hpRegenRateInCombat = 0f;
            _rule.hpRecoveryRatePerSeconds = 0.05f;
            _rule.aggroRange = 15f;
            _rule.aggroRangePerLevel = 1f;
            _rule.aggroRangeMin = 4.5f;
        }

        private PlayerCharacterEntity Character(int level = 1, CharacterStats stats = default)
        {
            return TestCombatants.Create<PlayerCharacterEntity>(Objects, level, stats);
        }

        private float HitChance(int attackerLevel, int targetLevel, float accuracy = 0f, float evasion = 0f)
        {
            return _rule.GetHitChance(
                Character(attackerLevel, new CharacterStats { accuracy = accuracy }),
                Character(targetLevel, new CharacterStats { evasion = evasion }));
        }

        private void MarkInCombat(BaseCharacterEntity character, float secondsAgo = 0f)
        {
            var lastCombat = TestReflection.GetField<Dictionary<uint, float>>(_rule, "_lastCombatTime");
            lastCombat[character.ObjectId] = Time.unscaledTime - secondsAgo;
        }

        // Hit chance

        [Test]
        [Category(TestCategories.Logic)]
        public void AnEvenLevelSwingHitsAtTheKitsCeiling()
        {
            Assert.That(HitChance(5, 5), Is.EqualTo(0.95f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void ALowerLevelTargetIsNoEasierThanTheCeiling()
        {
            Assert.That(HitChance(20, 1), Is.EqualTo(0.95f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void EachLevelTheTargetIsAboveCostsMissPerLevelAbove()
        {
            _rule.missPerLevelAbove = 0.1f;
            _rule.minHitChance = 0.05f;

            Assert.That(HitChance(5, 8), Is.EqualTo(1f - 3 * 0.1f).Within(1e-5f));
            Assert.That(HitChance(5, 8) - HitChance(5, 9), Is.EqualTo(0.1f).Within(1e-5f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void ALevelGapNeverPushesTheChanceBelowTheFloor()
        {
            Assert.That(HitChance(1, 40), Is.EqualTo(0.5f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void TheKitsAccuracyTermStillApplies()
        {
            _rule.minHitChance = 0.05f;

            Assert.That(HitChance(5, 5, accuracy: 10f, evasion: 30f), Is.EqualTo(2f * 10f / 40f).Within(1e-5f));
            Assert.That(HitChance(5, 5, accuracy: 10f, evasion: 0f), Is.EqualTo(0.95f), "the term needs both sides");
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void TheFloorAlsoHoldsAgainstPoorAccuracy()
        {
            Assert.That(HitChance(5, 5, accuracy: 1f, evasion: 1000f), Is.EqualTo(0.5f));
        }

        // Health regen

        [Test]
        [Category(TestCategories.Logic)]
        public void OutOfCombatRegenIsTheKitsOwn()
        {
            SimpleGameplayRule kit = Objects.Create<SimpleGameplayRule>();
            kit.hpRecoveryRatePerSeconds = _rule.hpRecoveryRatePerSeconds;
            PlayerCharacterEntity character = Character(stats: new CharacterStats { hp = 200f, hpRecovery = 2f });

            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.EqualTo(kit.GetRecoveryHpPerSeconds(character)));
            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.GreaterThan(0f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void InCombatRegenIsHeldToTheInCombatShare()
        {
            _rule.hpRegenRateInCombat = 0.25f;
            PlayerCharacterEntity character = Character(stats: new CharacterStats { hp = 200f, hpRecovery = 2f });
            float outOfCombat = _rule.GetRecoveryHpPerSeconds(character);

            MarkInCombat(character);

            Assert.That(_rule.IsInCombat(character), Is.True);
            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.EqualTo(outOfCombat * 0.25f).Within(1e-4f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void CombatEndsCombatSecondsAfterTheLastBlow()
        {
            PlayerCharacterEntity character = Character(stats: new CharacterStats { hp = 200f });
            float outOfCombat = _rule.GetRecoveryHpPerSeconds(character);

            MarkInCombat(character, secondsAgo: _rule.combatSeconds - 1f);
            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.EqualTo(0f), "still in combat");

            MarkInCombat(character, secondsAgo: _rule.combatSeconds + 1f);
            Assert.That(_rule.IsInCombat(character), Is.False);
            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.EqualTo(outOfCombat));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void OnlyTheCharactersInTheFightAreHeldBack()
        {
            PlayerCharacterEntity fighter = Character(stats: new CharacterStats { hp = 200f });
            PlayerCharacterEntity bystander = Character(stats: new CharacterStats { hp = 200f });
            TestCombatants.SetObjectId(fighter, 11);
            TestCombatants.SetObjectId(bystander, 12);

            MarkInCombat(fighter);

            Assert.That(_rule.GetRecoveryHpPerSeconds(fighter), Is.EqualTo(0f));
            Assert.That(_rule.GetRecoveryHpPerSeconds(bystander), Is.GreaterThan(0f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void AMissPutsBothSidesInCombat()
        {
            PlayerCharacterEntity attacker = Character(stats: new CharacterStats { hp = 200f });
            PlayerCharacterEntity target = Character(stats: new CharacterStats { hp = 200f });
            PlayerCharacterEntity bystander = Character(stats: new CharacterStats { hp = 200f });
            TestCombatants.SetObjectId(attacker, 21);
            TestCombatants.SetObjectId(target, 22);
            TestCombatants.SetObjectId(bystander, 23);

            _rule.OnCharacterReceivedDamage(attacker, target, CombatAmountType.Miss, 0, default, null, 0, default, false);

            Assert.That(_rule.IsInCombat(attacker), Is.True, "attacker");
            Assert.That(_rule.IsInCombat(target), Is.True, "target");
            Assert.That(_rule.IsInCombat(bystander), Is.False, "bystander");
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void ACombatWindowOfZeroNeverHoldsRegenBack()
        {
            _rule.combatSeconds = 0f;
            PlayerCharacterEntity character = Character(stats: new CharacterStats { hp = 200f });
            MarkInCombat(character);

            Assert.That(_rule.IsInCombat(character), Is.False);
            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.GreaterThan(0f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void AHealthDrainIsNotReducedInCombat()
        {
            PlayerCharacterEntity character = Character(stats: new CharacterStats { hp = 100f, hpRecovery = -20f });
            float drain = _rule.GetRecoveryHpPerSeconds(character);
            Assert.That(drain, Is.LessThan(0f));

            MarkInCombat(character);

            Assert.That(_rule.GetRecoveryHpPerSeconds(character), Is.EqualTo(drain));
        }

        // Aggro range

        [Test]
        [Category(TestCategories.Logic)]
        public void AMonsterNoticesATargetOfItsOwnLevelAtTheAggroRange()
        {
            Assert.That(_rule.GetAggroRange(7, 7), Is.EqualTo(15f));
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void AggroRangeShrinksForHigherTargetsAndGrowsForLowerOnes()
        {
            Assert.That(_rule.GetAggroRange(7, 10), Is.EqualTo(12f), "three levels above");
            Assert.That(_rule.GetAggroRange(7, 4), Is.EqualTo(18f), "three levels below");
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void AggroRangeNeverShrinksBelowTheMinimum()
        {
            Assert.That(_rule.GetAggroRange(1, 60), Is.EqualTo(4.5f));
        }

        // Class power

        private PlayerCharacter _warrior;
        private PlayerCharacter _ranger;
        private PlayerCharacter _mage;

        private void SetUpClasses()
        {
            _warrior = Objects.Create<PlayerCharacter>("TestWarrior");
            _ranger = Objects.Create<PlayerCharacter>("TestRanger");
            _mage = Objects.Create<PlayerCharacter>("TestMage");
            _rule.rageClasses = new[] { _warrior };
            _rule.focusClasses = new[] { _ranger };
            _rule.focusRegenPerSecond = 6f;
            _rule.mpRecoveryRatePerSeconds = 0.1f;
        }

        private PlayerCharacterEntity Member(PlayerCharacter playerClass)
        {
            PlayerCharacterEntity character = Character(stats: new CharacterStats { mp = 100f, mpRecovery = 1f });
            character.DataId = playerClass.DataId;
            return character;
        }

        [Test]
        [Category(TestCategories.Logic)]
        public void RageRegainsNothingFocusAFlatRateAndManaTheKitsRegen()
        {
            SetUpClasses();
            SimpleGameplayRule kit = Objects.Create<SimpleGameplayRule>();
            kit.mpRecoveryRatePerSeconds = _rule.mpRecoveryRatePerSeconds;
            PlayerCharacterEntity mage = Member(_mage);

            Assert.That(_rule.GetRecoveryMpPerSeconds(Member(_warrior)), Is.EqualTo(0f), "rage");
            Assert.That(_rule.GetRecoveryMpPerSeconds(Member(_ranger)), Is.EqualTo(6f), "focus");
            Assert.That(_rule.GetRecoveryMpPerSeconds(mage), Is.EqualTo(kit.GetRecoveryMpPerSeconds(mage)), "mana");
            Assert.That(_rule.GetRecoveryMpPerSeconds(mage), Is.GreaterThan(0f));
        }

        [Test]
        [Category(TestCategories.Harnessed)]
        public void RespawnFillsManaAndFocusButRageStartsEmpty()
        {
            TestGameInstance gameInstance = UseGameInstance();
            // A full stats cache falls back to the default weapon, which must resolve.
            GameInstance.Items[gameInstance.DefaultWeaponItem.DataId] = gameInstance.DefaultWeaponItem;
            SetUpClasses();
            CharacterStatsIncremental stats = new CharacterStatsIncremental { baseStats = new CharacterStats { hp = 100f, mp = 50f } };
            foreach (PlayerCharacter playerClass in new[] { _warrior, _ranger, _mage })
            {
                playerClass.Stats = stats;
                GameInstance.Characters[playerClass.DataId] = playerClass;
            }

            int MpAfterRespawn(PlayerCharacter playerClass)
            {
                PlayerCharacterData character = TestCharacters.NewPlayer();
                character.DataId = playerClass.DataId;
                character.CurrentMp = 20;
                _rule.OnCharacterRespawn(character);
                int mp = character.CurrentMp;
                character.RemoveCaches();
                return mp;
            }

            Assert.That(MpAfterRespawn(_mage), Is.EqualTo(50), "mana");
            Assert.That(MpAfterRespawn(_ranger), Is.EqualTo(50), "focus");
            Assert.That(MpAfterRespawn(_warrior), Is.EqualTo(0), "rage");
        }
    }
}
