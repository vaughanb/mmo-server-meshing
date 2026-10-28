// Server Meshing test rig — the stand-in ship's movement, sender part (Phase 0 step 7): the record it writes for the kit
// and for the state sender, the registration retry, and the release hook. Modelled on the space game's
// ShipMovementComponent.WriteServerState at commits 1a3a58d9 and c2eb4154 (read there, not copied): no rest or dirty
// suppression, reliable (and never compact) while a teleport is unacknowledged. IsAtRest follows commit 771ad38c (step 8b).
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshShipMovement
    {
        /// <summary>RestSpeedSquared is the squared linear speed under which a ship may be at rest (0.01 m/s, the space game's RestSpeed).</summary>
        public const float RestSpeedSquared = 1e-4f;
        /// <summary>RestAngularSpeedSquared is the squared angular speed under which a ship may be at rest (0.001 rad/s, the space game's RestAngularSpeed).</summary>
        public const float RestAngularSpeedSquared = 1e-6f;
        private const byte RigFlightState = 0;

        private static readonly List<EntityMovementForceApplier> s_noForceAppliers = new List<EntityMovementForceApplier>(0);

        private MeshStateSender _sender;
        private bool _retryRegistration;

        /// <summary>
        /// SentBySender is true while the state sender holds the ship: the kit-facing WriteServerState then writes nothing,
        /// so the ship has one writer. OnSenderReleased clears it.
        /// </summary>
        public bool SentBySender { get; private set; }

        /// <summary>DiscreteVersion packs the teleport sequence, the flight state (0 in the rig) and the epoch (see PackDiscreteVersion).</summary>
        public uint DiscreteVersion => PackDiscreteVersion(_teleportSequence, RigFlightState, _epoch);

        /// <summary>
        /// IsAtRest is the space game's rule (AtRest) for any ship: its best-known velocities, the dynamic rigidbody's while
        /// this peer simulates it, else the last received ones (never the zeroed record velocity of a mirror), are under the
        /// rest thresholds and no teleport is pending.
        /// </summary>
        public bool IsAtRest
        {
            get
            {
                bool body = _simulating && _rigidbody != null;
                return AtRest(body ? _rigidbody.linearVelocity : _netVelocity, body ? _rigidbody.angularVelocity : _netAngularVelocity, TeleportPending);
            }
        }

        /// <summary>
        /// AtRest is the space game's IsAtRest (ShipMovementComponent, commit 771ad38c): false while
        /// <paramref name="teleportPending"/>, else true when the linear speed is under 0.01 m/s and the angular speed under
        /// 0.001 rad/s (squared: RestSpeedSquared, RestAngularSpeedSquared).
        /// </summary>
        public static bool AtRest(Vector3 velocity, Vector3 angularVelocity, bool teleportPending)
        {
            return !teleportPending && velocity.sqrMagnitude < RestSpeedSquared && angularVelocity.sqrMagnitude < RestAngularSpeedSquared;
        }

        /// <summary>OnSenderReleased clears SentBySender and stops the registration retry; it never calls the registry.</summary>
        public void OnSenderReleased()
        {
            SentBySender = false;
            _retryRegistration = false;
        }

        /// <summary>
        /// WriteServerState writes nothing and returns false on the server while SentBySender; otherwise it appends the
        /// configured Full record (legacy54, or header56 under header56 and compact28), reliable while the teleport sequence
        /// is unacknowledged, and returns true every tick.
        /// </summary>
        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            if (SentBySender && IsServer)
            {
                shouldSendReliably = false;
                return false;
            }
            return WriteState(writeTimestamp, writer, out shouldSendReliably);
        }

        /// <summary>
        /// WriteState appends the Full record (header mode 0) whatever SentBySender says; it changes no ship state, and adds
        /// the record's mirror age to the Ships window when the ship has an in-process feed (MarkFeed).
        /// </summary>
        public bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)
        {
            BuildSample(out ServerMeshShipSample sample);
            if (_wire == ServerMeshShipWireKind.Legacy54)
                ServerMeshShipWire.WriteLegacy54(writer, in sample);
            else
                ServerMeshShipWire.WriteHeader56(writer, in sample);
            sendReliably = SendsReliably(_teleportSequence, _ackedSequence);
            CountMirrorAge(sampleTimestamp);
            return true;
        }

        /// <summary>
        /// WriteCompactState appends compact28 (mode 3) only under that wire and while no teleport is unacknowledged (a
        /// pending teleport is never quantised); false otherwise, or for a non-finite field, so the fan-out sends Full.
        /// </summary>
        public bool WriteCompactState(long sampleTimestamp, NetDataWriter writer)
        {
            if (!CompactAllowed(_wire, _teleportSequence, _ackedSequence))
                return false;
            BuildSample(out ServerMeshShipSample sample);
            if (!ServerMeshShipWire.WriteCompact28(writer, in sample))
                return false;
            CountMirrorAge(sampleTimestamp);
            return true;
        }

        /// <summary>SendsReliably is true when the ship's records go reliably: while the teleport <paramref name="sequence"/> is not the <paramref name="acknowledged"/> one.</summary>
        public static bool SendsReliably(byte sequence, byte acknowledged) => sequence != acknowledged;

        /// <summary>
        /// CompactAllowed is true when a compact record may be written: the wire is compact28 and the teleport
        /// <paramref name="sequence"/> equals the <paramref name="acknowledged"/> one.
        /// </summary>
        public static bool CompactAllowed(ServerMeshShipWireKind wire, byte sequence, byte acknowledged)
        {
            return wire == ServerMeshShipWireKind.Compact28 && sequence == acknowledged;
        }

        /// <summary>CreateMovementData is the pose with no force appliers (the kit's send loops never ask it: it is not a quantized handler).</summary>
        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            forceAppliers = s_noForceAppliers;
            return new MovementData
            {
                movementState = _netForward || _moveIntent.sqrMagnitude > 0.0001f ? (uint)MovementState.Forward : (uint)MovementState.None,
                worldPosition = transform.position,
                yAngle = transform.eulerAngles.y,
                shouldSendReliably = SendsReliably(_teleportSequence, _ackedSequence),
            };
        }

        /// <summary>WriteClientState writes nothing: no rig client owns a ship.</summary>
        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        /// <summary>
        /// PackDiscreteVersion is teleportSequence | flightState &lt;&lt; 8 | epoch &lt;&lt; 16: every observer must see the
        /// next sample after a teleport, a flight state change or an epoch change.
        /// </summary>
        public static uint PackDiscreteVersion(byte teleportSequence, byte flightState, byte epoch)
        {
            return teleportSequence | (uint)flightState << 8 | (uint)epoch << 16;
        }

        /// <summary>
        /// RecordVelocities is what a record carries: an OwnerMirror ship's kinematic body reports zero (the space server
        /// mirror's behaviour) unless <paramref name="realMirrorVelocity"/>, which sends the last upload's; any other ship
        /// sends its rigidbody's.
        /// </summary>
        public static void RecordVelocities(ServerMeshShipAuthority authority, bool realMirrorVelocity, Vector3 bodyVelocity, Vector3 bodyAngular,
            Vector3 uploadVelocity, Vector3 uploadAngular, out Vector3 velocity, out Vector3 angularVelocity)
        {
            if (authority == ServerMeshShipAuthority.OwnerMirror)
            {
                velocity = realMirrorVelocity ? uploadVelocity : Vector3.zero;
                angularVelocity = realMirrorVelocity ? uploadAngular : Vector3.zero;
                return;
            }
            velocity = bodyVelocity;
            angularVelocity = bodyAngular;
        }

        private ServerMeshShipAuthority AuthorityOf() => _ship != null ? _ship.Authority : ServerMeshShipAuthority.OwnerMirror;

        private void CountMirrorAge(long writeTimestamp)
        {
            // One sample per ship per frame: the kit writes a record per subscriber and the sender writes Full and
            // compact, but a send pass is one frame, so its records all carry the same age. Capped at the lists'
            // preallocated size so the send path never grows them.
            if (!_hasFeed || _lastAgeFrame == Time.frameCount)
                return;
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            if (counters.MirrorAges.Count >= ServerMeshShipCounters.MirrorAgeCapacity)
                return;
            _lastAgeFrame = Time.frameCount;
            counters.MirrorAges.Add(writeTimestamp - _feedTimestamp);
            counters.MirrorFrames.Add(Time.frameCount - _feedFrame);
        }

        private int _lastAgeFrame = -1;

        private void BuildSample(out ServerMeshShipSample sample)
        {
            ServerMeshShipAuthority authority = AuthorityOf();
            bool dynamic = _rigidbody != null && !_rigidbody.isKinematic;
            RecordVelocities(authority, _realMirrorVelocity,
                dynamic ? _rigidbody.linearVelocity : Vector3.zero, dynamic ? _rigidbody.angularVelocity : Vector3.zero,
                _netVelocity, _netAngularVelocity, out Vector3 velocity, out Vector3 angular);
            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            sample = new ServerMeshShipSample
            {
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                AngularVelocity = angular,
                Forward = authority == ServerMeshShipAuthority.OwnerMirror ? _netForward : _moveIntent.sqrMagnitude > 0.0001f,
                TeleportByte = _teleportSequence,
                FlightState = RigFlightState,

                Epoch = _epoch,
                Compact = false,
            };
        }

        private void CacheSender(BaseGameNetworkManager manager)
        {
            _sender = manager.GetComponent<MeshStateSender>();
            _retryRegistration = _sender != null;
        }

        private void ResetSender()
        {
            SentBySender = false;
            _retryRegistration = false;
            _sender = null;
        }

        /// <summary>
        /// RetryRegistration polls only IsSending while it is false (MESH_SENDER unset caches no sender, so nothing is
        /// polled); the first frame it is true it calls Register once, and a refusal ends the retry for good. A puppet-held
        /// ship never registers.
        /// </summary>
        private void RetryRegistration()
        {
            if (!_retryRegistration)
                return;
            if (PuppetHeld || _sender == null)
            {
                _retryRegistration = false;
                return;
            }
            if (!_sender.IsSending)
                return;
            _retryRegistration = false;
            SentBySender = _sender.Register(this);
        }
    }
}
