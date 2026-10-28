// Server Meshing test rig — MeshCharacterUseSkillComponent (skills replayed across a cell edge) with the kit demo's
// InterruptChanceUseSkillComponent rule: a hit taken while casting breaks the cast only interruptChance of the time.
// The mesh finds a puppet's skill component as a MeshCharacterUseSkillComponent, so the demo's rule is carried over
// here rather than subclassed. ServerMeshLargeMapBuilder puts this on its meshed copies of the demo's monsters.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshInterruptChanceUseSkillComponent : MeshCharacterUseSkillComponent
    {
        [Tooltip("The chance that a hit taken while casting breaks the cast. 1 is the kit's own rule: every hit that lands does.")]
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

        // The kit raises onReceivedDamage and interrupts in the same frame: the roll lets off only that frame's interrupt.
        private void RollForFocus(DamageableEntity target, HitBoxPosition position, Vector3 fromPosition,
            EntityInfo instigator, CombatAmountType combatAmountType, int totalDamage, CharacterItem weapon,
            BaseSkill skill, int skillLevel, CharacterBuff buff, bool isDamageOverTime)
        {
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
