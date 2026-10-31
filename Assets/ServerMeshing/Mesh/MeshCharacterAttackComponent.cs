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
        private int _triggersPaid;

        protected override UniTaskVoid AttackRoutine(long peerTimestamp, AttackState simulateState)
        {
            _triggersPaid = 0;
            OnAttackTrigger -= CountTrigger;
            OnAttackTrigger += CountTrigger;
            // A replayed attack can reach a client's freshly spawned puppet before this component's Start ran (the
            // kit's action manager is fetched there): resolve it now (it is a required component) and play the swing.
            if (_manager == null)
                _manager = GetComponent<CharacterActionComponentManager>();
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"attack start seed={(simulateState.SimulateSeed != 0 ? simulateState.SimulateSeed : MeshActionTrace.SeedOf(peerTimestamp))}");
            if (IsServer && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                byte weaponHandlingState = (byte)simulateState.WeaponHandlingState;
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.Attack, 0, w => { w.Put(peerTimestamp); w.Put(weaponHandlingState); });
            }
            return base.AttackRoutine(peerTimestamp, simulateState);
        }

        protected override void ApplyAttack(CharacterDataCache entityCaches, WeaponHandlingState weaponHandlingState, CharacterItem weapon, int simulateSeed, byte triggerIndex, DamageInfo damageInfo, List<DamageElementMinMaxFloatAmounts> damageAmounts, AimPosition aimPosition)
        {
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"attack trigger seed={simulateSeed} i={triggerIndex}");
            if (IsServer && MeshIdentity.IsGhostEntity(Entity))
                return; // a demoted character's swing belongs to the cell it crossed to (DESIGN 1.06; the routine is cancelled at the demotion)
            if (IsServer && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
            {
                SimulateActionTriggerData data = new SimulateActionTriggerData { simulateSeed = simulateSeed, triggerIndex = triggerIndex, aimPosition = aimPosition };
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.AttackTrigger, 0, w => w.Put(data));
            }
            base.ApplyAttack(entityCaches, weaponHandlingState, weapon, simulateSeed, triggerIndex, damageInfo, damageAmounts, aimPosition);
        }

        private void CountTrigger(int triggerIndex)
        {
            _triggersPaid = triggerIndex + 1; // the routine pays for it in the same frame, with no wait between
        }

        /// <summary>Origin side (DESIGN 1.35): what the routine the demotion cancels has not taken yet: a shot per trigger, and a weapon spent by firing.</summary>
        public void SettleInFlight()
        {
            if (!TryGetInFlight(out MeshCarriedAction action))
                return;
            int left = action.TriggersAfter(_triggersPaid);
            if (left <= 0)
                return;
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"attack settled seed={action.seed} triggers={left}");
            bool isLeftHand = ((WeaponHandlingState)action.weaponHandlingState).Has(WeaponHandlingState.IsLeftHand);
            for (int i = 0; i < left; ++i)
                Entity.DecreaseAmmos(isLeftHand, 1, out _);
            IWeaponItem weaponItem = Entity.GetAvailableWeapon(ref isLeftHand).GetWeaponItem();
            if (weaponItem == null || !weaponItem.DestroyImmediatelyAfterFired)
                return;
            EquipWeapons equipWeapons = Entity.EquipWeapons;
            if (isLeftHand)
                equipWeapons.leftHand = CharacterItem.Empty;
            else
                equipWeapons.rightHand = CharacterItem.Empty;
            Entity.EquipWeapons = equipWeapons;
        }

        /// <summary>Origin side (DESIGN 1.06): the swing in flight at a crossing, as its start prepared it for the client's triggers.</summary>
        public bool TryGetInFlight(out MeshCarriedAction action)
        {
            action = default;
            if (!_simulateState.HasValue)
                return false;
            int seed = _simulateState.Value.SimulateSeed;
            HitValidateData validate = HitRegistrationManager.GetHitValidateData(Entity, seed);
            if (validate == null || validate.TriggerDurations == null)
                return false;
            action = new MeshCarriedAction
            {
                kind = MeshCarriedActionKind.Attack,
                seed = seed,
                weaponHandlingState = (byte)validate.WeaponHandlingState,
                triggerDurations = validate.TriggerDurations,
                fireSpread = validate.FireSpread,
            };
            return true;
        }

        /// <summary>
        /// Destination side: the hit validation the origin prepared at the swing's start, prepared again for the arriving
        /// character so the client's trigger is accepted here. Mirrors the kit's AttackRoutine (weapon, damage info, damage
        /// amounts per trigger); the animation and its timing stay on the client, which is already playing them.
        /// </summary>
        public void PrepareCarried(MeshCarriedAction action)
        {
            WeaponHandlingState weaponHandlingState = (WeaponHandlingState)action.weaponHandlingState;
            bool isLeftHand = weaponHandlingState.Has(WeaponHandlingState.IsLeftHand);
            Entity.GetAttackingData(ref isLeftHand, out _, out _, out CharacterItem weapon, out DamageInfo damageInfo);
            CharacterDataCache entityCaches = Entity.GetCaches();
            DamageElementMinMaxFloatAmounts baseDamageAmounts = isLeftHand && entityCaches.HasIndexedLeftHandDamages
                ? entityCaches.IndexedLeftHandDamages
                : entityCaches.IndexedRightHandDamages;
            List<DamageElementMinMaxFloatAmounts> damageAmounts = Entity.PrepareDamageAmounts(isLeftHand, baseDamageAmounts, action.triggerDurations.Length, 1);
            HitRegistrationManager.PrepareHitRegValidation(Entity, action.seed, action.triggerDurations, action.fireSpread, damageInfo, damageAmounts, weaponHandlingState, weapon, null, 0);
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
