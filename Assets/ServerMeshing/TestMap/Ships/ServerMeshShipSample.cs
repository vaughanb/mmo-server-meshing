// Server Meshing test rig — one stand-in ship state as its records carry it (Phase 0 step 7). The fields follow the
// space game's ShipMovementComponent record at commit 1a3a58d9, plus the rig's header fields.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipSample is one ship state as a record carries it. Its members are fields, so reads through an
    /// <c>in</c> parameter make no defensive copies.
    /// </summary>
    public struct ServerMeshShipSample
    {
        /// <summary>Position is the ship's position in metres.</summary>
        public Vector3 Position;

        /// <summary>Rotation is the ship's rotation.</summary>
        public Quaternion Rotation;

        /// <summary>Velocity is the linear velocity in m/s.</summary>
        public Vector3 Velocity;

        /// <summary>AngularVelocity is the angular velocity in rad/s.</summary>
        public Vector3 AngularVelocity;

        /// <summary>Forward is the movement state: true for forward thrust, false for none.</summary>
        public bool Forward;

        /// <summary>TeleportByte is the teleport sequence from the server, or the acknowledged one in an upload.</summary>
        public byte TeleportByte;

        /// <summary>Authority is who simulates the ship (header56 and compact28 only).</summary>
        public ServerMeshShipAuthority Authority;

        /// <summary>FlightState is a 3-bit flight state, 0 to 7 (header56 and compact28 only).</summary>
        public byte FlightState;

        /// <summary>Epoch is the ship's epoch byte (header56 and compact28 only).</summary>
        public byte Epoch;

        /// <summary>Compact is true when the sample was read from a compact28 record.</summary>
        public bool Compact;
    }
}
