// Server Meshing add-on — rig phantom pose (Phase 0 step 7): what one phantom upload says about its object, for every payload kind.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomPose is the state a phantom upload carries. Character payloads read only Position and YawDegrees;
    /// ship payloads read the rest too. Fields, not properties, so a read through <c>in</c> makes no copy.
    /// </summary>
    public readonly struct ServerMeshPhantomPose
    {
        /// <summary>Position is the object's position (a teleport confirm's destination).</summary>
        public readonly Vector3 Position;
        /// <summary>YawDegrees is the heading in degrees: the given float for characters, the rotation's yaw for ships.</summary>
        public readonly float YawDegrees;
        /// <summary>Rotation is the ship's rotation (identity for characters, never read by their payloads).</summary>
        public readonly Quaternion Rotation;
        /// <summary>Velocity is the ship's linear velocity in m/s.</summary>
        public readonly Vector3 Velocity;
        /// <summary>AngularVelocity is the ship's angular velocity in rad/s.</summary>
        public readonly Vector3 AngularVelocity;
        /// <summary>Forward is the ship's movement state: true for Forward, false for None.</summary>
        public readonly bool Forward;
        /// <summary>Sequence is the teleport sequence the upload acknowledges.</summary>
        public readonly byte Sequence;
        /// <summary>FlightState is the header's flight state, 0 to 7.</summary>
        public readonly byte FlightState;
        /// <summary>Epoch is the header's epoch byte.</summary>
        public readonly byte Epoch;

        /// <summary>
        /// ServerMeshPhantomPose for a character keeps position and yaw verbatim (the yaw is never re-derived, so its bits
        /// and the upload's bytes stay as they were) and computes nothing else: Rotation is identity, the rest zero.
        /// </summary>
        public ServerMeshPhantomPose(Vector3 position, float yawDegrees)
        {
            Position = position;
            YawDegrees = yawDegrees;
            Rotation = Quaternion.identity;
            Velocity = Vector3.zero;
            AngularVelocity = Vector3.zero;
            Forward = false;
            Sequence = 0;
            FlightState = 0;
            Epoch = 0;
        }

        /// <summary>ServerMeshPhantomPose for a ship holds every field; YawDegrees is the rotation's yaw.</summary>
        public ServerMeshPhantomPose(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity, bool forward, byte sequence, byte flightState, byte epoch)
        {
            Position = position;
            YawDegrees = rotation.eulerAngles.y;
            Rotation = rotation;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            Forward = forward;
            Sequence = sequence;
            FlightState = flightState;
            Epoch = epoch;
        }
    }
}
