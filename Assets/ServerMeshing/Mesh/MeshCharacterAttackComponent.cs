// Server Meshing add-on — MeshCharacterAttackComponent (DESIGN.md §12, attacks and projectiles across the edge).
//
// The kit shows an attack to observers with two client RPCs from the attacker's server: RpcAttack (play the animation,
// same seed) and RpcSimulateActionTrigger (launch the damage entity: the visual missile, the melee swing). A puppet on
// the neighbour cell never sends them, so its observers saw nothing — no swing, no arrow. This subclass (swapped onto
// the demo character prefabs) forwards both from the owner cell as GhostReplay payloads and replays them on the
// puppet, whose own clients then run the kit's normal non-owner path. Damage is untouched: it is applied on the
// owner, and a missile that hits a puppet there is forwarded to the target's owner like any hit.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCharacterAttackComponent : DefaultCharacterAttackComponent
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override UniTaskVoid AttackRoutine(long peerTimestamp, AttackState simulateState)
        {
            // A replayed attack can reach a client's freshly spawned puppet before this component's Start ran (the
            // kit's action manager is fetched there): resolve it now (it is a required component) and play the swing.
            if (_manager == null)
                _manager = GetComponent<CharacterActionComponentManager>();
            if (IsServer && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                byte weaponHandlingState = (byte)simulateState.WeaponHandlingState;
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.Attack, 0, w => { w.Put(peerTimestamp); w.Put(weaponHandlingState); });
            }
            return base.AttackRoutine(peerTimestamp, simulateState);
        }

        protected override void ApplyAttack(CharacterDataCache entityCaches, WeaponHandlingState weaponHandlingState, CharacterItem weapon, int simulateSeed, byte triggerIndex, DamageInfo damageInfo, List<Dictionary<DamageElement, MinMaxFloat>> damageAmounts, AimPosition aimPosition)
        {
            if (IsServer && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                SimulateActionTriggerData data = new SimulateActionTriggerData { simulateSeed = simulateSeed, triggerIndex = triggerIndex, aimPosition = aimPosition };
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.AttackTrigger, 0, w => w.Put(data));
            }
            base.ApplyAttack(entityCaches, weaponHandlingState, weapon, simulateSeed, triggerIndex, damageInfo, damageAmounts, aimPosition);
        }

        /// <summary>Puppet side: what the owner's server told its own observers, told to ours.</summary>
        public void ReplayAttack(long peerTimestamp, WeaponHandlingState weaponHandlingState)
        {
            if (!IsServer)
                return;
            RPC(RpcAttack, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, peerTimestamp, weaponHandlingState);
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
