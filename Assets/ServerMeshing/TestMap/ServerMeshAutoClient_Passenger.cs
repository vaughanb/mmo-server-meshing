// Server Meshing test rig — the stand client as a passenger (DESIGN 1.39).
//
// A standing client boards a seat of the first vehicle that passes within reach with that seat free (the two-seat horse a
// patrolling driver rides across the line), then logs its ride once a second: where it is, which vehicle and seat it holds,
// whether the vehicle's passenger list agrees, and how many hops it has made. A ride that goes on while the seat reads free,
// or a position that drifts from the vehicle's, is the split DESIGN 1.39 fixed.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        public const string ARG_BOARD_SEAT = "-meshBoardSeat";

        [Header("Passenger (stand mode, DESIGN 1.39)")]
        [Tooltip("Stand mode: board this seat of the first vehicle that passes within boardReach with the seat free, and log the ride once a second. 0 = off.")]
        public byte boardSeat = 0;
        [Tooltip("How close a vehicle must pass (m) to be boarded.")]
        public float boardReach = 3f;

        private float _nextBoardTry, _nextPassengerLog;
        private int _boardAttempts;

        /// <summary>Every frame of a stand: board when a vehicle with the wanted seat free is within reach; log the ride once a second.</summary>
        private void PollPassengerBoarding(BasePlayerCharacterEntity self)
        {
            if (boardSeat == 0 || self == null)
                return;
            float now = Time.realtimeSinceStartup;
            if (self.PassengingVehicleEntity == null && now >= _nextBoardTry)
            {
                _nextBoardTry = now + 0.5f;
                VehicleEntity nearest = null;
                float best = boardReach;
                foreach (VehicleEntity vehicle in FindObjectsByType<VehicleEntity>(FindObjectsSortMode.None))
                {
                    if (vehicle == null || vehicle.Identity == null || !vehicle.HasDriver || !vehicle.IsSeatAvailable(boardSeat))
                        continue;
                    float distance = Vector3.Distance(vehicle.EntityTransform.position, self.EntityTransform.position);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = vehicle;
                    }
                }
                if (nearest != null)
                {
                    _boardAttempts++;
                    self.CallCmdEnterVehicle(nearest.ObjectId, boardSeat);
                    Log($"passenger: boarding seat {boardSeat} of {nearest.name} #{nearest.ObjectId} at {best:0.0} m (try {_boardAttempts})");
                }
            }
            if (now >= _nextPassengerLog)
            {
                _nextPassengerLog = now + 1f;
                VehicleEntity ridden = self.PassengingVehicleEntity as VehicleEntity;
                string ride = ridden != null
                    ? $"{ridden.name} #{ridden.ObjectId} seat={self.PassengingVehicleSeatIndex} seatFree={ridden.IsSeatAvailable(self.PassengingVehicleSeatIndex)} driver={ridden.HasDriver} vehiclePos={ridden.EntityTransform.position}"
                    : "none";
                Log($"passenger: pos={self.EntityTransform.position} object={self.ObjectId} vehicle={ride} migrations={ClientMigrationController.CompletedCount}");
            }
        }
    }
}
