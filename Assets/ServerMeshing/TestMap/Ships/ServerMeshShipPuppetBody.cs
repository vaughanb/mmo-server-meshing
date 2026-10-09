// Server Meshing test rig — an in-process ship puppet's body (Phase 0 step 7): it stands in a ship's handler slot,
// takes owner samples, poses the transform with the space game's observer smoothing (ShipMovementComponent at commit
// 1a3a58d9, read there, not copied: velocity extrapolation, then a 1 − exp(−10·dt) lerp and slerp, a snap past 50 m
// times the scale) and writes the ship's records from the posed transform.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipPuppetBody poses a puppet ship that mirrors a ship owned elsewhere (the puppet feeder's stand-in for a
    /// neighbour cell's ship). Begin installs it in the slot in front of the ship's ServerMeshShipMovement (which the feeder
    /// marks PuppetHeld); Accept feeds it a sample; Tick (observed) smooths toward it and ApplyNow (unobserved) snaps to it;
    /// the kit or the sender then write the ship's record of the posed transform. Main thread only; no allocation per call.
    /// </summary>
    public sealed class ServerMeshShipPuppetBody : IEntityMovementDataHandler, IMeshCompactStateSource, IMeshSenderOwnedSource
    {
        private static readonly List<EntityMovementForceApplier> s_noForceAppliers = new List<EntityMovementForceApplier>(0);

        private readonly uint _objectId;
        private readonly LiteNetLibIdentity _identity;
        private readonly Transform _transform;
        private readonly ServerMeshShipWireKind _wire;
        private readonly ServerMeshShipScale _scale;
        private ServerMeshShipSample _sample;
        private Vector3 _netPosition;
        private bool _hasSample;
        private bool _isBegun;

        /// <summary>
        /// ServerMeshShipPuppetBody creates the body of ship <paramref name="objectId"/>, which poses <paramref name="transform"/>
        /// and writes <paramref name="wire"/> records. It throws, naming the argument, for a null identity, transform or scale.
        /// </summary>
        public ServerMeshShipPuppetBody(uint objectId, LiteNetLibIdentity identity, Transform transform, ServerMeshShipWireKind wire, ServerMeshShipScale scale)
        {
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));
            if (transform == null)
                throw new ArgumentNullException(nameof(transform));
            _scale = scale ?? throw new ArgumentNullException(nameof(scale));
            _objectId = objectId;
            _identity = identity;
            _transform = transform;
            _wire = wire;
            _sample.Rotation = Quaternion.identity;
        }

        /// <summary>ObjectId is the puppet ship's object id.</summary>
        public uint ObjectId => _objectId;

        /// <summary>ConnectionId is -1, so the kit drops every upload addressed to the body.</summary>
        public long ConnectionId => -1;

        /// <summary>Identity is the puppet ship's identity.</summary>
        public LiteNetLibIdentity Identity => _identity;

        /// <summary>DiscreteVersion packs the last sample's teleport byte, flight state and epoch.</summary>
        public uint DiscreteVersion => ServerMeshShipMovement.PackDiscreteVersion(_sample.TeleportByte, _sample.FlightState, _sample.Epoch);

        /// <summary>
        /// IsAtRest is the space game's rule (ServerMeshShipMovement.AtRest) on the last sample's velocities: linear under
        /// 0.01 m/s and angular under 0.001 rad/s. It has no pending term: a puppet never sends reliably.
        /// </summary>
        public bool IsAtRest => ServerMeshShipMovement.AtRest(_sample.Velocity, _sample.AngularVelocity, false);

        /// <summary>IsBegun is true between a successful Begin and End.</summary>
        public bool IsBegun => _isBegun;

        /// <summary>HasSample is true once Accept took a sample.</summary>
        public bool HasSample => _hasSample;

        /// <summary>LastAcceptTime is the time the last sample was accepted at.</summary>
        public double LastAcceptTime { get; private set; }

        /// <summary>
        /// SentBySender is true while the body is registered with the state sender (the feeder sets it after a successful
        /// Register); the kit-facing WriteServerState then writes nothing.
        /// </summary>
        public bool SentBySender { get; set; }

        /// <summary>Begin installs the body in the ship's slot (not replaceable); false with the reason when the slot refuses it or the body has begun.</summary>
        public bool Begin(MeshMovementHandlerSlots slots, out string error)
        {
            if (slots == null)
                throw new ArgumentNullException(nameof(slots));
            if (_isBegun)
            {
                error = $"object {_objectId} ship puppet body has already begun";
                return false;
            }
            if (!slots.TryInstall(_objectId, this, false, out error))
                return false;
            _isBegun = true;
            return true;
        }

        /// <summary>
        /// End clears SentBySender first, then takes the body out of the slot (or forgets it when the ship is destroyed); a
        /// second call does nothing else. A slot that refuses the removal is logged as a warning.
        /// </summary>
        public void End(MeshMovementHandlerSlots slots)
        {
            SentBySender = false;
            if (slots == null)
                throw new ArgumentNullException(nameof(slots));
            if (!_isBegun)
                return;
            _isBegun = false;
            if (_identity == null || _identity.IsDestroyed)
            {
                slots.Forget(_objectId);
                return;
            }
            if (!slots.TryRemove(_objectId, this, out string error))
                Logging.LogWarning("Mesh", "Feeder: ship puppet body end: " + MeshStateSender.CleanForLog(error));
        }

        /// <summary>OnSenderReleased clears SentBySender; it never calls the registry.</summary>
        public void OnSenderReleased() => SentBySender = false;

        /// <summary>Accept takes <paramref name="sample"/> as the new target, received at <paramref name="now"/>.</summary>
        public void Accept(in ServerMeshShipSample sample, double now)
        {
            _sample = sample;
            _netPosition = sample.Position;
            _hasSample = true;
            LastAcceptTime = now;
        }

        /// <summary>Tick runs one step of the observer smoothing over <paramref name="deltaTime"/> seconds (nothing before the first sample).</summary>
        public void Tick(float deltaTime)
        {
            if (!_hasSample)
                return;
            _transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            ServerMeshShipMovement.ObserverStep(position, rotation, ref _netPosition, _sample.Velocity, _sample.Rotation, deltaTime, _scale.SnapDistance, out position, out rotation);
            _transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>ApplyNow snaps the transform to the latest sample (nothing before the first one).</summary>
        public void ApplyNow()
        {
            if (!_hasSample)
                return;
            _netPosition = _sample.Position;
            _transform.SetPositionAndRotation(_sample.Position, _sample.Rotation);
        }

        /// <summary>WriteServerState writes nothing while SentBySender; else the Full record of the posed transform.</summary>
        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            if (SentBySender)
            {
                shouldSendReliably = false;
                return false;
            }
            return WriteState(writeTimestamp, writer, out shouldSendReliably);
        }

        /// <summary>WriteState appends the Full record (legacy54, else header56) of the posed transform; never reliable.</summary>
        public bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            Posed(out ServerMeshShipSample posed);
            if (_wire == ServerMeshShipWireKind.Legacy54)
                ServerMeshShipWire.WriteLegacy54(writer, in posed);
            else
                ServerMeshShipWire.WriteHeader56(writer, in posed);
            sendReliably = false;
            return true;
        }

        /// <summary>WriteCompactState appends compact28 of the posed transform under that wire only.</summary>
        public bool WriteCompactState(long sampleTimestamp, NetDataWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (_wire != ServerMeshShipWireKind.Compact28)
                return false;
            Posed(out ServerMeshShipSample posed);
            return ServerMeshShipWire.WriteCompact28(writer, in posed);
        }

        /// <summary>
        /// Posed is the record the body writes: the transform's position and rotation with the last sample's velocities,
        /// forward flag, teleport byte, flight state and epoch.
        /// </summary>
        public void Posed(out ServerMeshShipSample posed)
        {
            posed = _sample;
            _transform.GetPositionAndRotation(out posed.Position, out posed.Rotation);
            posed.Compact = false;
        }

        /// <summary>CreateMovementData is the posed transform with no force appliers.</summary>
        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            forceAppliers = s_noForceAppliers;
            return new MovementData
            {
                movementState = _sample.Forward ? (uint)MovementState.Forward : (uint)MovementState.None,
                worldPosition = _transform.position,
                yAngle = _transform.eulerAngles.y,
                shouldSendReliably = false,
            };
        }

        /// <summary>ReadClientStateAtServer ignores the upload: the ship is owned elsewhere.</summary>
        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
        }

        /// <summary>ReadServerStateAtClient consumes one record of the body's wire and applies nothing.</summary>
        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            ServerMeshShipWire.Read(reader, _wire, out _);
        }

        /// <summary>WriteClientState writes nothing: a server never uploads.</summary>
        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }
    }
}
