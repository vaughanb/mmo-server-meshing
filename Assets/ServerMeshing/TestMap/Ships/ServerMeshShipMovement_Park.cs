// Server Meshing test rig — the stand-in ship's park pin (Phase 0 step 8a, MESH_SHIP_PARK_SLEEP=1), modelled on the space
// game's ShipLander at commit c2eb4154 (read there, not copied): a Landed ship's rigidbody is set to its parked pose and
// its velocities zeroed every physics step, which keeps PhysX from ever sleeping it. Unset or 0, parked ships stay
// dynamic bodies under exactly zero force, as in step 7, and PhysX may sleep them.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshShipMovement
    {
        private bool _parkPin;
        private bool _hasParkedPose;
        private Vector3 _parkedPosition;
        private Quaternion _parkedRotation = Quaternion.identity;

        /// <summary>IsBodySleeping is true when the ship has a rigidbody and PhysX reports it asleep.</summary>
        public bool IsBodySleeping => _rigidbody != null && _rigidbody.IsSleeping();

        /// <summary>
        /// PinsParked is true when a ship is pinned this physics step: MESH_SHIP_PARK_SLEEP=1 (<paramref name="pinEnabled"/>),
        /// the peer runs the rigidbody (<paramref name="simulating"/>) and the ship is a Parked one.
        /// </summary>
        public static bool PinsParked(bool pinEnabled, bool simulating, ServerMeshShipAuthority authority)
        {
            return pinEnabled && simulating && authority == ServerMeshShipAuthority.Parked;
        }

        private void ResetPark() => _hasParkedPose = false;

        /// <summary>MoveParkedPose makes a teleport the parked pose, so a pinned ship stays where it was sent.</summary>
        private void MoveParkedPose(Vector3 position, Quaternion rotation)
        {
            if (!_hasParkedPose)
                return;
            _parkedPosition = position;
            _parkedRotation = rotation;
        }

        /// <summary>
        /// PinParked runs after the engine's (zero) force each FixedUpdate: the first pinned step takes the body's pose as the
        /// parked pose; every step sets position and rotation to it and zeroes both velocities, as ShipLander's Landed state.
        /// </summary>
        private void PinParked()
        {
            if (_rigidbody == null || !PinsParked(_parkPin, _simulating, _ship != null ? _ship.Authority : ServerMeshShipAuthority.OwnerMirror))
                return;
            if (!_hasParkedPose)
            {
                _parkedPosition = _rigidbody.position;
                _parkedRotation = _rigidbody.rotation;
                _hasParkedPose = true;
            }
            _rigidbody.position = _parkedPosition;
            _rigidbody.rotation = _parkedRotation;
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}
