using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Holds the demo's toggle skills to the terms their descriptions give, in the two places the
    /// kit does not.
    ///
    /// **A mana-fed toggle goes off when the mana runs out.** A toggle's buff has no duration,
    /// and a negative `recoveryMp` on it drains that much a second for as long as it is on -
    /// which is how Arcane Ward is paid for. But the kit only stops *draining* at zero
    /// (`CharacterRecoveryData.Apply` skips the decrease once `CurrentMp` is 0); it never takes
    /// the buff off, so a mage who ran dry would keep the ward for nothing, forever.
    ///
    /// It listens for the drain rather than watching the mana. The buff drains in one-second
    /// lumps (`CharacterSkillAndBuffComponent`, every `SKILL_BUFF_UPDATE_DURATION`) and ordinary
    /// regeneration tops up on the same one-second beat, in the same frame - so the pool drops to
    /// zero and is back at a few points before anything else looks. The first version polled
    /// `CurrentMp < 1` and never once saw it: measured live, a dry mage's mana sat between 4 and 8
    /// with the ward still up. `onBuffMpDecrease` fires as the lump is taken, which is the moment
    /// the pool is actually empty.
    ///
    /// **A toggle that breaks on attacking breaks on attacking skills too.** The kit's
    /// `removeBuffWhenAttackChance` is only rolled for weapon attacks
    /// (`DefaultCharacterAttackComponent` calls `OnAttack`); a skill calls `OnUseSkill` instead,
    /// which rolls the separate use-skill chance. That one cannot be used for Fleet of Foot:
    /// it is rolled as ANY skill starts - including the toggle's own second press, before the
    /// toggle looks for its buff - so pressing to turn it off would take the buff off and then put
    /// it straight back. So an *attacking* skill (`IsAttack`) is treated as an attack here.
    ///
    /// Buffs come off in the entity's late update, not in the events: both are raised from inside
    /// the kit's own loops. They come off the way the kit's own toggle does when pressed again
    /// (`Skill.ApplySkillBuff`, `SkillBuffType.Toggle`): `OnRemoveBuff`, then out of the list.
    /// Server only - the buff list is synced from there. Nothing ticks between events: an event
    /// that leaves work subscribes for one late update (<see cref="BaseGameEntity.onLateUpdate"/>),
    /// which does it and lets go again.
    ///
    /// The demo puts it on every player from <c>DemoEntitySetting</c>.
    /// </summary>
    public class ToggleBuffUpkeep : MonoBehaviour
    {
        private BaseCharacterEntity _entity;
        private bool _ranDry;
        private bool _attackedWithSkill;
        private bool _pending;

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            if (_entity == null)
            {
                enabled = false;
                return;
            }
            _entity.onBuffMpDecrease += OnBuffMpDecrease;
            _entity.onUseSkillRoutine += OnUseSkillRoutine;
        }

        private void OnDestroy()
        {
            if (_entity == null)
                return;
            _entity.onBuffMpDecrease -= OnBuffMpDecrease;
            _entity.onUseSkillRoutine -= OnUseSkillRoutine;
            if (_pending)
                _entity.onLateUpdate -= Flush;
        }

        private void OnBuffMpDecrease(BaseCharacterEntity entity, EntityInfo causer, int amount)
        {
            if (_entity.CurrentMp <= 0)
            {
                _ranDry = true;
                Schedule();
            }
        }

        /// <summary>Asks for one late update, in which <see cref="Flush"/> does the work.</summary>
        private void Schedule()
        {
            if (_pending || !_entity.IsServer)
                return;
            _pending = true;
            _entity.onLateUpdate += Flush;
        }

        private void OnUseSkillRoutine(BaseCharacterEntity entity, BaseSkill skill, int level, bool isLeftHand,
            CharacterItem weapon, int simulateSeed, byte triggerIndex,
            List<DamageElementMinMaxFloatAmounts> damageAmounts, uint targetObjectId, AimPosition aimPosition)
        {
            // Once per use, not once per trigger: the event fires for every trigger of a skill,
            // and a skill with several would roll the break chance several times. It fires before
            // the skill pays its costs, so a shot that fizzles for want of mana still counts -
            // which is fair: the ranger tried to loose.
            if (skill != null && skill.IsAttack && triggerIndex == 0)
            {
                _attackedWithSkill = true;
                Schedule();
            }
        }

        private void Flush(BaseGameEntity entity)
        {
            _pending = false;
            _entity.onLateUpdate -= Flush;
            bool ranDry = _ranDry;
            bool attacked = _attackedWithSkill;
            _ranDry = false;
            _attackedWithSkill = false;
            if (!_entity.IsServer || (!ranDry && !attacked))
                return;

            for (int i = _entity.Buffs.Count - 1; i >= 0; --i)
            {
                CharacterBuff buff = _entity.Buffs[i];
                if (buff.type != BuffType.SkillBuff)
                    continue;
                var skill = buff.GetSkill() as Skill;
                if (skill == null || skill.skillBuffType != Skill.SkillBuffType.Toggle)
                    continue;
                CalculatedBuff calculated = buff.GetBuff();
                if (ranDry && calculated.GetRecoveryMp() < 0)
                {
                    _entity.OnRemoveBuff(buff, BuffRemoveReasons.RemoveByToggle);
                    _entity.Buffs.RemoveAt(i);
                }
                else if (attacked && calculated.GetRemoveBuffWhenAttackChance() > 0f &&
                         Random.value <= calculated.GetRemoveBuffWhenAttackChance())
                {
                    _entity.OnRemoveBuff(buff, BuffRemoveReasons.RemoveByAttackRemoveChance);
                    _entity.Buffs.RemoveAt(i);
                }
            }
        }
    }
}
