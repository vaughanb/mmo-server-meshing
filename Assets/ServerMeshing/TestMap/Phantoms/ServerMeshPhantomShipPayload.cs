// Server Meshing add-on — rig phantom ship payload (Phase 0 step 7): a mirror ship's upload, the space game's ShipMovementComponent
// client record (space repo 1a3a58d9, SMC:329-344) in legacy54 or the header56 layout (c2eb4154's header, mode 0),
// written by hand for the phantom.
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomShipPayload writes a ship phantom's upload: legacy54 (54 B) for legacy54, else header56 (56 B,
    /// wire mode 0, so byte 0 is the flight state shifted by 2; compact records are a far-observer encoding and are never
    /// uploaded). Ships upload every tick, moving or not, and never reliably. The pose's Sequence is the acknowledged
    /// teleport sequence; a teleport confirm writes the pose's position (the server's destination) at rest: zero
    /// velocities and movement state None.
    /// </summary>
    public sealed class ServerMeshPhantomShipPayload : IServerMeshPhantomPayload
    {
        private readonly ServerMeshShipWireKind _wire;

        /// <summary>ServerMeshPhantomShipPayload uploads what wire names: legacy54 as legacy54, header56 and compact28 as header56.</summary>
        public ServerMeshPhantomShipPayload(ServerMeshShipWireKind wire)
        {
            _wire = ServerMeshPhantomSettings.UploadWireOf(wire);
        }

        /// <summary>Wire is the variant this payload writes: legacy54 or header56.</summary>
        public ServerMeshShipWireKind Wire => _wire;

        /// <summary>UploadBytes is the payload's length: 54 or 56.</summary>
        public int UploadBytes => ServerMeshShipWire.SizeOf(_wire);

        /// <summary>TryWrite writes one record and returns true; never reliable.</summary>
        public bool TryWrite(NetDataWriter writer, bool moving, bool teleport, in ServerMeshPhantomPose pose, ref bool stopSent, out bool reliable)
        {
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Position = pose.Position,
                Rotation = pose.Rotation,
                Velocity = teleport ? Vector3.zero : pose.Velocity,
                AngularVelocity = teleport ? Vector3.zero : pose.AngularVelocity,
                Forward = !teleport && pose.Forward,
                TeleportByte = pose.Sequence,
                FlightState = pose.FlightState,
                Epoch = pose.Epoch,
                Compact = false,
            };
            if (_wire == ServerMeshShipWireKind.Legacy54)
                ServerMeshShipWire.WriteLegacy54(writer, in sample);
            else
                ServerMeshShipWire.WriteHeader56(writer, in sample);
            stopSent = false;
            reliable = false;
            return true;
        }
    }
}
