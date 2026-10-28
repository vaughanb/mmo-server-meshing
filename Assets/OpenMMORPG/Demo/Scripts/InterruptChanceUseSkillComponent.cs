using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The kit's skill component, except that a hit taken while casting only sometimes breaks
    /// the cast.
    ///
    /// The kit's rule is that it always does: `BaseCharacterEntity.ReceivedDamage` calls
    /// `InterruptCastingSkill` for every hit that is not a miss - an invincible character's
    /// immune hits included. Once the mage fought in melee (2026-09-23) that meant a wolf's
    /// bites cut off nearly every Meteor, whose cast is 1.4s, and often an Arcane Bolt. Now
    /// each such hit rolls <see cref="interruptChance"/>, and a cast that holds carries on.
    /// Skills marked as unable to be interrupted (Mend) are untouched; the kit never
    /// interrupts those at all. Monsters built by the demo use this too, so the rule is the
    /// same for the Hierophant's casts as for the player's.
    ///
    /// **Only a hit rolls.** The kit raises `onReceivedDamage` on the server and calls the
    /// interrupt immediately after it, in the same frame; the roll marks that frame, and only
    /// the interrupt that follows in it is let off. Anything else that interrupts a cast - a
    /// player moving on purpose - still always does.
    ///
    /// Put on entity prefabs by `DemoEntityBuilder`, in place of the kit's component (the same
    /// object with its script swapped, so nothing that points at it moves).
    /// </summary>
    public class InterruptChanceUseSkillComponent : DefaultCharacterUseSkillComponent
    {
        [Tooltip("The chance that a hit taken while casting breaks the cast. 1 is the kit's own " +
                 "rule: every hit that lands does.")]
        [Range(0f, 1f)]
        public float interruptChance = 0.35f;

        private int _heldThroughFrame = -1;
        private BaseCharacterEntity _subscribed;

        protected override void Start()
        {
            base.Start();
            _subscribed = Entity;
            if (_subscribed != null)
                _subscribed.onReceivedDamage += RollForFocus;
        }

        protected override void OnDestroy()
        {
            if (_subscribed != null)
                _subscribed.onReceivedDamage -= RollForFocus;
            _subscribed = null;
            base.OnDestroy();
        }

        private void RollForFocus(DamageableEntity target, HitBoxPosition position, Vector3 fromPosition,
            EntityInfo instigator, CombatAmountType combatAmountType, int totalDamage, CharacterItem weapon,
            BaseSkill skill, int skillLevel, CharacterBuff buff, bool isDamageOverTime)
        {
            // The kit does not interrupt on a miss, so there is nothing to roll for.
            if (combatAmountType == CombatAmountType.Miss)
                return;
            if (Random.value >= interruptChance)
                _heldThroughFrame = Time.frameCount;
        }

        public override void InterruptCastingSkill()
        {
            if (_heldThroughFrame == Time.frameCount)
            {
                _heldThroughFrame = -1;
                return;
            }
            base.InterruptCastingSkill();
        }
    }
}
