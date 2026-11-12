// Server Meshing add-on — a mount that is one of its rider's summons (a tamed animal that follows on foot and is ridden
// on demand). The kit's mounts are VehicleEntity prefabs the saved CharacterMount re-summons, and the handoff keeps or
// re-summons those from the saved data. A summon mount travels as a summon instead (demoted and promoted in place with
// its summoner), so the handoff notice names the summon the rider sits on and the destination seats the rider on it
// again once both are promoted (MeshMapNetworkManager_Ghosting.RestoreSummonMount).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Optional on a summoned IVehicleEntity: seat a rider who was already riding it on the cell it came from. Without it
    /// the destination seats the rider with IVehicleEntity.SetPassenger, as the kit's EnterVehicle does minus the enter
    /// animation and the reach rule.
    /// </summary>
    public interface IMeshSummonMount : IVehicleEntity
    {
        /// <summary>
        /// Server, destination cell: <paramref name="rider"/> just arrived by handoff and is owned by its client; the
        /// mount was promoted in place a moment ago and already belongs to the rider's connection. Seat it on
        /// <paramref name="seatIndex"/> as a continuation of the ride (no mount animation), or return false to leave it
        /// on foot. The rider may already sit there: its puppet kept the seat it had on the pet's puppet.
        /// </summary>
        bool SeatArrivingRider(BaseCharacterEntity rider, byte seatIndex);

        /// <summary>
        /// Server, neighbour cell: both are puppets, the rider's snapshots name this summon as its mount. Seat
        /// <paramref name="rider"/> on <paramref name="seatIndex"/> as a ride already in progress (no mount animation,
        /// no stop, no ride rules: the owner's cell decided those) and do not move the mount, whose own snapshots steer
        /// it. Return false to leave the puppet on foot.
        /// </summary>
        bool SeatPuppetRider(BaseCharacterEntity rider, byte seatIndex);
    }
}
