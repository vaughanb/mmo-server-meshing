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
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCharacterUseSkillComponent : DefaultCharacterUseSkillComponent
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override UniTaskVoid UseSkillRoutine(long peerTimestamp, UseSkillState simulateState)
        {
            if (!IsServer && _manager == null)
                return default; // see MeshCharacterAttackComponent: the component has not started on this client yet
            if (IsServer && !simulateState.UseItem && simulateState.Skill != null && !MeshIdentity.IsGhostEntity(Entity))
            {
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                mesh?.ForwardGhostSkill(Entity, peerTimestamp, simulateState.Skill.DataId, simulateState.SkillLevel, simulateState.WeaponHandlingState, simulateState.TargetObjectId, simulateState.AimPosition);
            }
            return base.UseSkillRoutine(peerTimestamp, simulateState);
        }

        protected override void ApplySkillUsing(BaseSkill skill, int skillLevel, WeaponHandlingState weaponHandlingState, CharacterItem weapon, int simulateSeed, byte triggerIndex, List<Dictionary<DamageElement, MinMaxFloat>> damageAmounts, uint targetObjectId, AimPosition aimPosition)
        {
            if (IsServer && !MeshIdentity.IsGhostEntity(Entity))
            {
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                mesh?.ForwardGhostSkillTrigger(Entity, new SimulateActionTriggerData { simulateSeed = simulateSeed, triggerIndex = triggerIndex, targetObjectId = targetObjectId, aimPosition = aimPosition });
            }
            base.ApplySkillUsing(skill, skillLevel, weaponHandlingState, weapon, simulateSeed, triggerIndex, damageAmounts, targetObjectId, aimPosition);
        }

        /// <summary>Puppet side: what the owner's server told its own observers, told to ours (target already re-resolved).</summary>
        public void ReplayUseSkill(long peerTimestamp, int dataId, int skillLevel, WeaponHandlingState weaponHandlingState, uint targetObjectId, AimPosition aimPosition)
        {
            if (!IsServer)
                return;
            RPC(RpcUseSkill, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, peerTimestamp, dataId, skillLevel, weaponHandlingState, targetObjectId, aimPosition);
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
