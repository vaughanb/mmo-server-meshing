// Server Meshing add-on — rig phantom input payload: the kit's ServerAuthoritative client movement input, written by hand (the kit's writer runs only on an owner client).
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomInputPayload copies ClientWriteMovementInput3D: byte input state, packed uint movement state, byte
    /// extra state unless stopped, the position when it changed, packed int yaw × 1000 when it changed. Every frame carries
    /// IsKeyMovement, so the server walks straight to the position instead of path-finding. A moving phantom sends
    /// Forward with its position and yaw; an idle one sends one reliable stop frame, then nothing until it moves again; a
    /// teleport confirm is reliable and carries IsTeleport with the server's destination. IsJump and IsDash are never set.
    /// </summary>
    public sealed class ServerMeshPhantomInputPayload : IServerMeshPhantomPayload
    {
        /// <summary>Instance is the one shared writer; it holds no state.</summary>
        public static readonly ServerMeshPhantomInputPayload Instance = new ServerMeshPhantomInputPayload();

        /// <summary>MovingInput is the input state of a moving frame: key movement with a position and a yaw.</summary>
        public const EntityMovementInputState MovingInput = EntityMovementInputState.IsKeyMovement | EntityMovementInputState.PositionChanged | EntityMovementInputState.RotationChanged;
        /// <summary>StopInput is the input state of the stop frame.</summary>
        public const EntityMovementInputState StopInput = EntityMovementInputState.IsKeyMovement | EntityMovementInputState.IsStopped;

        /// <summary>TryWrite writes a moving, teleport-confirm or stop frame, or nothing for an idle phantom whose stop was already sent.</summary>
        public bool TryWrite(NetDataWriter writer, bool moving, bool teleport, in ServerMeshPhantomPose pose, ref bool stopSent, out bool reliable)
        {
            if (!moving && !teleport)
            {
                reliable = true;
                if (stopSent)
                    return false;
                writer.Put((byte)StopInput);
                writer.PutPackedUInt((uint)MovementState.None);
                stopSent = true;
                return true;
            }
            writer.Put((byte)MovingInput);
            writer.PutPackedUInt((uint)(teleport ? MovementState.IsTeleport : MovementState.Forward));
            writer.Put((byte)ExtraMovementState.None);
            writer.PutVector3(pose.Position);
            writer.PutPackedInt(EntityMovementFunctions.GetCompressedAngle(Mathf.Repeat(pose.YawDegrees, 360f)));
            stopSent = false;
            reliable = teleport;
            return true;
        }
    }
}
