// Server Meshing test rig — the stand-in ship's movement, owner part (Phase 0 step 8b): an unowned ship acknowledges its
// own teleports, modelled on the space game's ShipMovementComponent.AcknowledgeTeleportIfUnowned and OnSetOwnerClient at
// commit bcbe9754 (read there, not copied). Nobody would ever echo an unowned ship's teleport, so without this its records
// would go reliably for the rest of the run. MESH_SHIP_SELF_ACK=1 keeps step 7's two-tick self-acknowledgement instead.
namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshShipMovement
    {
        /// <summary>
        /// OnSetOwnerClient runs on every peer when the ship's owner changes (and at spawn). On the server, a ship that has
        /// just become unowned with a teleport pending acknowledges it at once (AcknowledgesUnowned): an owner who left
        /// before echoing it never will.
        /// </summary>
        public override void OnSetOwnerClient(bool isOwnerClient)
        {
            base.OnSetOwnerClient(isOwnerClient);
            if (AcknowledgesUnowned(IsServer, _selfAck, ConnectionId, _teleportSequence, _ackedSequence))
                _ackedSequence = _teleportSequence;
        }

        /// <summary>
        /// AcknowledgesUnowned is true when the server acknowledges a ship's teleport itself, at once: on the server
        /// (<paramref name="isServer"/>), unless MESH_SHIP_SELF_ACK=1 (<paramref name="selfAck"/>, which keeps the two-tick
        /// SelfAckDue instead), for a ship with no owner connection (<paramref name="connectionId"/> below 0, as mirror-fed
        /// ships are), while the teleport <paramref name="sequence"/> is not the <paramref name="acknowledged"/> one. A ship a
        /// phantom or client owns stays pending until its owner's upload echoes the sequence.
        /// </summary>
        public static bool AcknowledgesUnowned(bool isServer, bool selfAck, long connectionId, byte sequence, byte acknowledged)
        {
            return isServer && !selfAck && connectionId < 0 && sequence != acknowledged;
        }
    }
}
