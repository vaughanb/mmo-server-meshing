// Server Meshing add-on — MeshCharacterChargeComponent (DESIGN.md 1.35, a charge seen across the edge and carried over a hop).
//
// The kit shows a charge to observers with RpcStartCharge and RpcStopCharge from the charger's server. A puppet on the
// neighbour cell never sent them, so nobody across a line saw a charge, and a character that crossed charging stayed in
// the charge pose on the origin for good: its stop went to the destination. The owner cell now forwards both, as the
// attack and skill components forward theirs, and a charge in progress at a crossing goes with the handoff state, so the
// destination holds it and the stop reaches the origin's observers as a replay.
using LiteNetLib;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCharacterChargeComponent : DefaultCharacterChargeComponent
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private bool _chargingLeftHand;
        private bool _carrying;

        protected override void PlayChargeAnimation(bool isLeftHand)
        {
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, $"charge start left={isLeftHand}");
            _chargingLeftHand = isLeftHand;
            base.PlayChargeAnimation(isLeftHand);
            if (!_carrying && IsServer && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.ChargeStart, 0, w => w.Put(isLeftHand));
        }

        protected override void StopChargeAnimation()
        {
            if (MeshActionTrace.Enabled)
                MeshActionTrace.Log(Entity, "charge stop");
            base.StopChargeAnimation();
            if (IsServer && !MeshIdentity.IsGhostEntity(Entity) && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
                mesh.ForwardGhostReplay(Entity, GhostReplayKind.ChargeStop, 0, null);
        }

        /// <summary>Origin side: the charge in progress at a crossing.</summary>
        public bool TryGetInFlight(out MeshCarriedAction action)
        {
            action = default;
            if (!IsCharging)
                return false;
            action = new MeshCarriedAction
            {
                kind = MeshCarriedActionKind.Charge,
                weaponHandlingState = (byte)(_chargingLeftHand ? WeaponHandlingState.IsLeftHand : WeaponHandlingState.None),
            };
            return true;
        }

        /// <summary>
        /// Destination side: the charge goes on here. Nobody is told: this cell's observers saw it start on the puppet, and
        /// a second start would play the charge again from its beginning at the line.
        /// </summary>
        public void PrepareCarried(MeshCarriedAction action)
        {
            _carrying = true;
            PlayChargeAnimation(((WeaponHandlingState)action.weaponHandlingState).Has(WeaponHandlingState.IsLeftHand));
            _carrying = false;
        }

        /// <summary>Puppet side: what the owner's server told its own observers, told to ours.</summary>
        public void ReplayStart(bool isLeftHand)
        {
            if (IsServer)
                RPC(RpcStartCharge, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, isLeftHand);
        }

        public void ReplayStop()
        {
            if (IsServer)
                RPC(RpcStopCharge, BaseGameEntity.ACTION_DATA_CHANNEL, DeliveryMethod.ReliableOrdered);
        }
#endif
    }
}
