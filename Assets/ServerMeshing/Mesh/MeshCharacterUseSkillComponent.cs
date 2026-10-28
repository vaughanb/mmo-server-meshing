// Server Meshing add-on — MeshCharacterUseSkillComponent (DESIGN.md §12, skills seen across the edge).
//
// Same recipe as MeshCharacterAttackComponent: the kit shows a skill to observers with RpcUseSkill (cast/attack
// animation) and RpcSimulateActionTrigger (the skill's visual: missile, area effect, buff fx). The owner cell forwards
// both to the neighbours streaming the caster; the puppet replays them so its own clients run the kit's non-owner
// path. The skill's target is an object id local to the owner cell, so it travels as a mesh id and is re-resolved
// on the puppet's cell (the target's ghost or real entity there, or nobody). Damage, buffs and resources are untouched:
// they are applied on the owner (and forwarded to a target's owner when the target is a ghost there).
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCharacterUseSkillComponent : DefaultCharacterUseSkillComponent
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override UniTaskVoid UseSkillRoutine(long peerTimestamp, UseSkillState simulateState)
        {
            if (_manager == null)
                _manager = GetComponent<CharacterActionComponentManager>(); // see MeshCharacterAttackComponent
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"skill start seed={MeshActionTrace.SeedOf(peerTimestamp)} skill={simulateState.Skill?.DataId}");
            if (IsServer && simulateState.Skill != null && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                bool isItem = simulateState.ItemDataId.HasValue; // the kit's item path fills ItemDataId (UseItem is only set on the owner's index path)
                int dataId = isItem ? simulateState.ItemDataId.Value : simulateState.Skill.DataId;
                int skillLevel = simulateState.SkillLevel;
                byte weaponHandlingState = (byte)simulateState.WeaponHandlingState;
                AimPosition aimPosition = simulateState.AimPosition;
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.Skill, simulateState.TargetObjectId, w =>
                {
                    w.Put(peerTimestamp); w.Put(isItem); w.Put(dataId); w.Put(skillLevel); w.Put(weaponHandlingState); w.Put(aimPosition);
                });
            }
            return base.UseSkillRoutine(peerTimestamp, simulateState);
        }

        protected override void ApplySkillUsing(BaseSkill skill, int skillLevel, WeaponHandlingState weaponHandlingState, CharacterItem weapon, int simulateSeed, byte triggerIndex, List<DamageElementMinMaxFloatAmounts> damageAmounts, uint targetObjectId, AimPosition aimPosition)
        {
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"skill trigger seed={simulateSeed} i={triggerIndex}");
            if (IsServer && MeshIdentity.IsGhostEntity(Entity))
                return; // see MeshCharacterAttackComponent.ApplyAttack
            if (IsServer && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                SimulateActionTriggerData data = new SimulateActionTriggerData { simulateSeed = simulateSeed, triggerIndex = triggerIndex, targetObjectId = targetObjectId, aimPosition = aimPosition };
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.SkillTrigger, targetObjectId, w => w.Put(data));
            }
            base.ApplySkillUsing(skill, skillLevel, weaponHandlingState, weapon, simulateSeed, triggerIndex, damageAmounts, targetObjectId, aimPosition);
        }

        /// <summary>Origin side (DESIGN 1.06): the skill in flight at a crossing (see MeshCharacterAttackComponent.TryGetInFlight).</summary>
        public bool TryGetInFlight(out MeshCarriedAction action)
        {
            action = default;
            if (!_simulateState.HasValue || _simulateState.Value.Skill == null)
                return false;
            UseSkillState state = _simulateState.Value;
            HitValidateData validate = HitRegistrationManager.GetHitValidateData(Entity, state.SimulateSeed);
            if (validate == null || validate.TriggerDurations == null)
                return false;
            action = new MeshCarriedAction
            {
                kind = MeshCarriedActionKind.Skill,
                seed = state.SimulateSeed,
                weaponHandlingState = (byte)validate.WeaponHandlingState,
                triggerDurations = validate.TriggerDurations,
                fireSpread = validate.FireSpread,
                skillDataId = state.Skill.DataId,
                skillLevel = state.SkillLevel,
                itemDataId = state.ItemDataId ?? 0,
            };
            return true;
        }

        /// <summary>
        /// Destination side: mirrors the kit's UseSkillRoutine preparation (see MeshCharacterAttackComponent.PrepareCarried).
        /// Not carried: a usable item the kit takes after the last trigger. Whether the origin's routine got that far before
        /// the crossing is not visible from here, so it is left as it was (DESIGN 1.06, open items).
        /// </summary>
        public void PrepareCarried(MeshCarriedAction action)
        {
            if (!GameInstance.Skills.TryGetValue(action.skillDataId, out BaseSkill skill))
                return;
            WeaponHandlingState weaponHandlingState = (WeaponHandlingState)action.weaponHandlingState;
            bool isLeftHand = weaponHandlingState.Has(WeaponHandlingState.IsLeftHand);
            Entity.GetUsingSkillData(skill, ref isLeftHand, out _, out _, out CharacterItem weapon, out DamageInfo damageInfo);
            DamageElementMinMaxFloatAmounts baseDamageAmounts = skill.GetAttackDamages(Entity, action.skillLevel, isLeftHand);
            List<DamageElementMinMaxFloatAmounts> damageAmounts = skill.PrepareDamageAmounts(Entity, isLeftHand, baseDamageAmounts, action.triggerDurations.Length);
            HitRegistrationManager.PrepareHitRegValidation(Entity, action.seed, action.triggerDurations, action.fireSpread, damageInfo, damageAmounts, weaponHandlingState, weapon, skill, action.skillLevel);
        }

        /// <summary>Puppet side: what the owner's server told its own observers, told to ours (target already re-resolved).</summary>
        public void ReplayUseSkill(long peerTimestamp, int dataId, int skillLevel, WeaponHandlingState weaponHandlingState, uint targetObjectId, AimPosition aimPosition)
        {
            if (!IsServer)
                return;
            RPC(RpcUseSkill, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, peerTimestamp, dataId, skillLevel, weaponHandlingState, targetObjectId, aimPosition);
        }

        public void ReplayUseSkillItem(long peerTimestamp, int itemDataId, WeaponHandlingState weaponHandlingState, uint targetObjectId, AimPosition aimPosition)
        {
            if (!IsServer)
                return;
            RPC(RpcUseSkillItem, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, peerTimestamp, itemDataId, weaponHandlingState, targetObjectId, aimPosition);
        }

        public void ReplayTrigger(SimulateActionTriggerData data)
        {
            if (!IsServer)
                return;
            RPC(RpcSimulateActionTrigger, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, data);
        }
#endif
    }
}
