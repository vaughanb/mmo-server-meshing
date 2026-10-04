// Server Meshing add-on — rig phantom transform payload: the kit's NotSecure client sync transform, written by hand (the kit's writer runs only on an owner client).
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomTransformPayload copies ClientWriteSyncTransform3D: packed uint movement state (Forward|IsGrounded
    /// while moving, IsGrounded while idle or standing, plus IsTeleport on a teleport confirm), byte extra state 0, the
    /// position as three floats, packed int yaw × 1000. It always sends; only a teleport confirm is reliable. No force count
    /// follows: that is the server's record, not an upload. IsJump and IsDash are never set.
    /// </summary>
    public sealed class ServerMeshPhantomTransformPayload : IServerMeshPhantomPayload
    {
        /// <summary>Instance is the one shared writer; it holds no state.</summary>
        public static readonly ServerMeshPhantomTransformPayload Instance = new ServerMeshPhantomTransformPayload();

        /// <summary>MovingState is the state a moving phantom uploads (Forward|IsGrounded, 17).</summary>
        public const MovementState MovingState = MovementState.Forward | MovementState.IsGrounded;
        /// <summary>IdleState is the state an idle or standing phantom uploads (IsGrounded, 16).</summary>
        public const MovementState IdleState = MovementState.IsGrounded;

        /// <summary>TryWrite writes the sync transform and returns true; reliable only for a teleport confirm.</summary>
        public bool TryWrite(NetDataWriter writer, bool moving, bool teleport, in ServerMeshPhantomPose pose, ref bool stopSent, out bool reliable)
        {
            MovementState state = moving ? MovingState : IdleState;
            if (teleport)
                state |= MovementState.IsTeleport;
            writer.PutPackedUInt((uint)state);
            writer.Put((byte)ExtraMovementState.None);
            writer.PutVector3(pose.Position);
            writer.PutPackedInt(EntityMovementFunctions.GetCompressedAngle(Mathf.Repeat(pose.YawDegrees, 360f)));
            stopSent = false;
            reliable = teleport;
            return true;
        }
    }
}
