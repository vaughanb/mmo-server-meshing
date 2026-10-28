using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The kit's rule with two changes to how a fight feels, both the user's call on
    /// 2026-10-02 after playing the warrior.
    ///
    /// **No health regen in combat.** The kit heals every character by a share of its
    /// maximum health every second plus its `hpRecovery` stat, and does so in a fight exactly
    /// as out of one. A level-five warrior got back about six and a half a second while a
    /// bandit landed about ten, so one bandit cost a sixth of the bar and the bar was full
    /// again before the next one. Here anyone who has dealt or taken damage within
    /// <see cref="combatSeconds"/> regens at <see cref="hpRegenRateInCombat"/> of the usual
    /// amount - nothing, by default - and the usual amount otherwise. Monsters are under the
    /// same rule, which also replaces the per-monster negative `hpRecovery` that used to
    /// cancel their share: a monster heals only once nobody has hit it for a while, which is
    /// what lets one that walks home from a chase reset. Mana regen is untouched in combat; the
    /// toggles are costed against the kit's regen and the mage's whole kit runs on it.
    ///
    /// **A gentler miss chance against higher levels.** The kit's `GetHitChance` multiplies by
    /// <c>2 × yourLevel / (yourLevel + theirLevel)</c>, which is a coin toss two levels under
    /// at level two and still a quarter of swings lost at five against eight. The warrior,
    /// whose output is many small rolls, pays that on every swing; the mage never notices it
    /// because two bolts kill. Here each level the target has over the attacker costs
    /// <see cref="missPerLevelAbove"/>, floored at <see cref="minHitChance"/>. The kit's
    /// accuracy-against-evasion term and its 95% ceiling are kept as they are.
    ///
    /// **Rage and focus in the MP slot** (2026-10-06, see <see cref="ClassPower"/>). The warrior's
    /// MP is rage: earned by weapon blows and by damage taken, no regeneration, gone a while after
    /// the fight, empty on respawn. The ranger's is focus: a fixed pool that refills at a flat rate.
    /// The mage keeps the kit's mana. Which class is which is <see cref="rageClasses"/> and
    /// <see cref="focusClasses"/>, written by `DemoDatabaseWiring.WriteClassPowers`.
    ///
    /// Put on the demo's `GameplayRule.asset` by `DemoDatabaseWiring.WriteGameplayRule`, which
    /// swaps the asset's script from the kit's `SimpleGameplayRule` so every value already on
    /// it stays. Combat is tracked on the server from the rule's own
    /// <see cref="OnCharacterReceivedDamage"/> hook, which the kit calls for every hit, miss
    /// and block that lands on a character.
    /// </summary>
    public class CombatGameplayRule : SimpleGameplayRule
    {
        [Header("Demo: class power (the MP slot - see ClassPower)")]
        [Tooltip("Classes whose MP is rage. Their skills' MP costs are rage costs.")]
        public PlayerCharacter[] rageClasses = new PlayerCharacter[0];

        [Tooltip("Classes whose MP is focus. Their skills' MP costs are focus costs.")]
        public PlayerCharacter[] focusClasses = new PlayerCharacter[0];

        [Tooltip("Rage for each weapon blow that lands - an auto-attack, or a skill that costs no rage " +
                 "(Charge). A skill paid for in rage earns none back, as in WoW.")]
        public float rageOnHit = 10f;

        [Tooltip("What a critical blow multiplies rageOnHit by.")]
        public float rageCritMultiplier = 1.5f;

        [Tooltip("Rage for each 1% of maximum health lost to a hit or a damage-over-time tick.")]
        public float ragePerPercentHpTaken = 1f;

        [Tooltip("Rage lost per second once out of combat (combatSeconds after the last blow either way). " +
                 "Applied by ClassPowerUpkeep, which takes it straight off rather than through the kit's " +
                 "MP drain, so no floating '-5' numbers.")]
        public float rageDecayPerSecond = 5f;

        [Tooltip("Focus regained per second, in combat and out of it. Replaces the kit's MP regen for focus " +
                 "classes, so Intelligence and the 1%-of-pool share do not apply.")]
        public float focusRegenPerSecond = 6f;

        private System.Collections.Generic.HashSet<int> _rageIds;
        private System.Collections.Generic.HashSet<int> _focusIds;

        private void OnValidate()
        {
            // Rebuilt from the lists on next use, so an inspector edit takes effect at once.
            _rageIds = null;
            _focusIds = null;
        }

        public ClassPowerType GetClassPower(int playerCharacterDataId)
        {
            if (_rageIds == null || _focusIds == null)
            {
                _rageIds = IdsOf(rageClasses);
                _focusIds = IdsOf(focusClasses);
            }
            if (_rageIds.Contains(playerCharacterDataId))
                return ClassPowerType.Rage;
            if (_focusIds.Contains(playerCharacterDataId))
                return ClassPowerType.Focus;
            return ClassPowerType.Mana;
        }

        public ClassPowerType GetClassPower(ICharacterData character)
        {
            if (!(character is IPlayerCharacterData))
                return ClassPowerType.Mana;
            return GetClassPower(character.DataId);
        }

        private static System.Collections.Generic.HashSet<int> IdsOf(PlayerCharacter[] classes)
        {
            var ids = new System.Collections.Generic.HashSet<int>();
            if (classes == null)
                return ids;
            foreach (PlayerCharacter entry in classes)
            {
                if (entry != null)
                    ids.Add(entry.DataId);
            }
            return ids;
        }

        /// <summary>Adds to a character's MP slot, clamped to its pool. Server only - the slot is synced.</summary>
        public static void AddPower(BaseCharacterEntity character, float amount)
        {
            int add = Mathf.RoundToInt(amount);
            if (character == null || add <= 0)
                return;
            character.CurrentMp = Mathf.Min(character.MaxMp, character.CurrentMp + add);
        }

        [Header("Demo: combat")]
        [Tooltip("How long after dealing or taking damage a character counts as in combat.")]
        public float combatSeconds = 6f;

        [Tooltip("Health regen while in combat, as a share of the usual amount. 0 is none; 1 is the kit's own rule.")]
        [Range(0f, 1f)]
        public float hpRegenRateInCombat = 0f;

        [Tooltip("Hit chance lost for each level the target is above the attacker.")]
        [Range(0f, 0.25f)]
        public float missPerLevelAbove = 0.05f;

        [Tooltip("The least a level gap can bring the hit chance down to.")]
        [Range(0.05f, 0.95f)]
        public float minHitChance = 0.5f;

        [Header("Demo: monster sight")]
        [Tooltip("On, a monster has to see a target (a clear line from its eyes to the target's chest, past the " +
                 "attack-obstacle and building layers) before it notices it on its own. Off, it notices anything " +
                 "inside its visual range through walls and floors, as the kit does. Being hit aggroes it either " +
                 "way. Read live by LineOfSightMonsterActivityComponent, so this can be flipped without a rebuild.")]
        public bool monsterLineOfSight = true;

        [Tooltip("How far a monster notices a target of its own level, in metres. World of Warcraft's is 20 yards " +
                 "(about 18 m) on a 2-yard-tall body; 15 suits the demo's tighter camera. A monster's own visual " +
                 "range (the data asset) is the ceiling, whatever the levels.")]
        public float aggroRange = 15f;

        [Tooltip("Metres taken off the range for each level the target is above the monster, and added for each " +
                 "level below - WoW's one yard a level. A level-12 warrior walks past level-2 wolves until almost on them.")]
        public float aggroRangePerLevel = 1f;

        [Tooltip("The least the level gap can shrink the range to. WoW floors at 5 yards; a monster stepped on always notices.")]
        public float aggroRangeMin = 4.5f;

        [Tooltip("The cone in front of a monster, in degrees across, inside which it notices a target on its own " +
                 "(user's call, 2026-10-06: monsters should only notice what they are looking at). 360 is WoW's own rule - " +
                 "an aggro radius all round, facing irrelevant. Inside a monster's hearing range (3 m) it notices " +
                 "whatever its facing. Read live by LineOfSightMonsterActivityComponent.")]
        [Range(10f, 360f)]
        public float aggroFieldOfView = 120f;

        /// <summary>
        /// How far a monster at <paramref name="monsterLevel"/> notices a target at
        /// <paramref name="targetLevel"/>, before the monster's own visual range caps it.
        /// </summary>
        public float GetAggroRange(int monsterLevel, int targetLevel)
        {
            float range = aggroRange - (targetLevel - monsterLevel) * aggroRangePerLevel;
            return Mathf.Max(aggroRangeMin, range);
        }

        /// <summary>
        /// When each character last dealt or took damage, by object id. Ids are recycled when
        /// an entity despawns, but an entry only matters for <see cref="combatSeconds"/>, so
        /// a stale one is harmless; the table is pruned whenever it grows past a few hundred.
        /// </summary>
        private readonly Dictionary<uint, float> _lastCombatTime = new Dictionary<uint, float>();
        private readonly List<uint> _expired = new List<uint>();
        private const int PruneAbove = 512;

        public bool IsInCombat(BaseCharacterEntity character)
        {
            if (character == null || combatSeconds <= 0f)
                return false;
            return _lastCombatTime.TryGetValue(character.ObjectId, out float last)
                && Time.unscaledTime - last < combatSeconds;
        }

        private void MarkCombat(BaseCharacterEntity character)
        {
            if (character == null)
                return;
            if (_lastCombatTime.Count > PruneAbove)
                Prune();
            _lastCombatTime[character.ObjectId] = Time.unscaledTime;
        }

        private void Prune()
        {
            _expired.Clear();
            float now = Time.unscaledTime;
            foreach (KeyValuePair<uint, float> entry in _lastCombatTime)
            {
                if (now - entry.Value >= combatSeconds)
                    _expired.Add(entry.Key);
            }
            foreach (uint id in _expired)
                _lastCombatTime.Remove(id);
        }

        public override void OnCharacterReceivedDamage(BaseCharacterEntity attacker, BaseCharacterEntity damageReceiver, CombatAmountType combatAmountType, int damage, CharacterItem weapon, BaseSkill skill, int skillLevel, CharacterBuff buff, bool isDamageOverTime)
        {
            // A miss or a block is still a fight; a burn ticking is still a fight.
            MarkCombat(attacker);
            MarkCombat(damageReceiver);
            GainRage(attacker, damageReceiver, combatAmountType, damage, skill, skillLevel, isDamageOverTime);
            base.OnCharacterReceivedDamage(attacker, damageReceiver, combatAmountType, damage, weapon, skill, skillLevel, buff, isDamageOverTime);
        }

        /// <summary>
        /// WoW's two sources of rage: blows you land and blows you take. A miss earns nothing; a
        /// skill paid for in rage earns nothing back (WoW's "yellow" hits), so only auto-attacks and
        /// free skills like Charge build it. Taking damage is measured against the warrior's own
        /// health, so a bite earns the same share at level twelve as at level one.
        /// </summary>
        private void GainRage(BaseCharacterEntity attacker, BaseCharacterEntity damageReceiver, CombatAmountType combatAmountType,
            int damage, BaseSkill skill, int skillLevel, bool isDamageOverTime)
        {
            if (damage <= 0 || combatAmountType == CombatAmountType.Miss)
                return;

            if (attacker != null && attacker.IsServer && !isDamageOverTime && attacker != damageReceiver &&
                GetClassPower(attacker) == ClassPowerType.Rage &&
                (skill == null || skill.GetTotalConsumeMp(skillLevel, attacker) <= 0))
            {
                float gain = rageOnHit;
                if (combatAmountType == CombatAmountType.CriticalDamage)
                    gain *= rageCritMultiplier;
                AddPower(attacker, gain);
            }

            if (damageReceiver != null && damageReceiver.IsServer && damageReceiver.MaxHp > 0 &&
                GetClassPower(damageReceiver) == ClassPowerType.Rage)
            {
                AddPower(damageReceiver, 100f * damage / damageReceiver.MaxHp * ragePerPercentHpTaken);
            }
        }

        public override float GetRecoveryMpPerSeconds(BaseCharacterEntity character)
        {
            switch (GetClassPower(character))
            {
                case ClassPowerType.Rage:
                    return 0f;
                case ClassPowerType.Focus:
                    return focusRegenPerSecond;
                default:
                    return base.GetRecoveryMpPerSeconds(character);
            }
        }

        public override void OnCharacterRespawn(ICharacterData character)
        {
            base.OnCharacterRespawn(character);
            // The kit fills every pool on respawn; rage starts a life empty.
            if (GetClassPower(character) == ClassPowerType.Rage)
                character.CurrentMp = 0;
        }

        public override bool RewardExp(BaseCharacterEntity character, int exp, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel, out int rewardedExp)
        {
            // `recoverMpWhenLevelUp` fills MP on a level-up, which would be a free bar of rage.
            // Mana and focus keep the kit's refill.
            bool isRage = GetClassPower(character) == ClassPowerType.Rage;
            int rageBefore = character != null ? character.CurrentMp : 0;
            bool isLevelUp = base.RewardExp(character, exp, multiplier, rewardGivenType, giverLevel, sourceLevel, out rewardedExp);
            if (isLevelUp && isRage)
                character.CurrentMp = Mathf.Min(rageBefore, character.MaxMp);
            return isLevelUp;
        }

        public override float GetRecoveryHpPerSeconds(BaseCharacterEntity character)
        {
            float amount = base.GetRecoveryHpPerSeconds(character);
            // Only regen is held back. A negative amount is a drain from some stat and is
            // left alone, as is anything already at zero (hungry, in the kit's rule).
            if (amount > 0f && IsInCombat(character))
                return amount * hpRegenRateInCombat;
            return amount;
        }

        public override float GetHitChance(BaseCharacterEntity attacker, BaseCharacterEntity damageReceiver)
        {
            CharacterStats attackerStats = attacker.GetCaches().Stats;
            CharacterStats dmgReceiverStats = damageReceiver.GetCaches().Stats;
            float attackerAcc = attackerStats.accuracy;
            float dmgReceiverEva = dmgReceiverStats.evasion;
            float hitChance = 1f;

            // The kit's own accuracy term, unchanged: it only speaks when both sides have one.
            if (attackerAcc != 0 && dmgReceiverEva != 0)
                hitChance *= 2f * (attackerAcc / (attackerAcc + dmgReceiverEva));

            int levelsAbove = damageReceiver.Level - attacker.Level;
            if (levelsAbove > 0)
                hitChance -= levelsAbove * missPerLevelAbove;

            if (hitChance < minHitChance)
                hitChance = minHitChance;
            // The kit's ceiling: one swing in twenty always misses.
            if (hitChance > 0.95f)
                hitChance = 0.95f;
            return hitChance;
        }
    }
}
