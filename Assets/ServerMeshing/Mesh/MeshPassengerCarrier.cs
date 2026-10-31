// Server Meshing add-on — the seat a passenger holds, across a hop (DESIGN.md 1.39).
//
// The kit binds a passenger to its seat only through the vehicle's passengerIds list, and a character's data says nothing
// about a vehicle it does not drive (Mount is the driver's own summon). So the first passenger that crossed a line arrived
// with no seat: its puppet had been given a vehicle of its own, its promotion dropped the seat (no Mount to keep), and its
// client kept riding a horse the server had put it off. MeshPassengerCarrier carries "behind driver <mesh id>, seat N" with
// the handoff; the destination seats the arrival on that driver's vehicle (the driver's puppet's, or the real driver's,
// whichever is there), or waits a few seconds for it (MeshMapNetworkManager.SeatArrival).
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshPassengerSeat is the seat a crossing passenger holds: behind which driver (its mesh id), and which seat.</summary>
    public struct MeshPassengerSeat
    {
        public string driverMeshId;
        public byte seat;

        /// <summary>A driver (seat 0), someone on foot, or a passenger behind nobody carries nothing.</summary>
        public bool IsEmpty => seat == 0 || string.IsNullOrEmpty(driverMeshId);

        public void Write(NetDataWriter writer)
        {
            writer.Put(driverMeshId ?? string.Empty);
            writer.Put(seat);
        }

        public static MeshPassengerSeat Read(NetDataReader reader)
        {
            return new MeshPassengerSeat { driverMeshId = reader.GetString(), seat = reader.GetByte() };
        }
    }

    /// <summary>MeshPassengerCarrier carries MeshPassengerSeat with the handoff, for every player (MeshHandoffCarriers.BuiltIn).</summary>
    public sealed class MeshPassengerCarrier : IMeshHandoffCarrier
    {
        public const string Key = "mesh.passenger";

        public string CarrierKey => Key;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            MeshPassengerSeat seat = Collect(player);
            if (seat.IsEmpty)
                return false;
            seat.Write(writer);
            return true;
        }

        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            MeshPassengerSeat seat = MeshPassengerSeat.Read(reader);
            if (!seat.IsEmpty && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
                mesh.SeatArrival(player, seat.driverMeshId, seat.seat);
        }

        /// <summary>Origin: the seat <paramref name="player"/> holds behind a driver; empty for a driver, on foot, or behind nobody.</summary>
        public static MeshPassengerSeat Collect(BasePlayerCharacterEntity player)
        {
            if (player == null || player.PassengingVehicleSeatIndex == 0 || !(player.PassengingVehicleEntity is VehicleEntity vehicle) || !vehicle.HasDriver)
                return default;
            if (!(vehicle.GetPassenger(0) is BaseCharacterEntity driver))
                return default;
            string driverMeshId = driver.Id;
            if (!(driver is BasePlayerCharacterEntity))
            {
                MeshIdentity id = MeshIdentity.Get(driver); // a monster's mesh id is its birth-provenance id, not its entity id
                if (id != null && !string.IsNullOrEmpty(id.MeshEntityId))
                    driverMeshId = id.MeshEntityId;
            }
            return new MeshPassengerSeat { driverMeshId = driverMeshId, seat = player.PassengingVehicleSeatIndex };
        }
#else
        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer) => false;
        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader) { }
#endif
    }
}
