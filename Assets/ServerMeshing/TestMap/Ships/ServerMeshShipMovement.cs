// Server Meshing test rig — the stand-in ship's movement and state handler (Phase 0 step 7), modelled on the space
// game's ShipMovementComponent at commits 1a3a58d9 and c2eb4154 (read there, not copied): the kit handler for its
// object, a rigidbody simulated only where the ship is not a mirror, observer smoothing with velocity extrapolation, the
// teleport sequence and its acknowledgement, and the record codecs of ServerMeshShipWire. The sender side is in _Sender.cs.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipMovement registers itself as its object's kit movement handler at OnIdentityInitialize, as the space
    /// ship does. The server simulates ServerAI and Parked ships (dynamic rigidbody, the engine in FixedUpdate); OwnerMirror
    /// ships, puppets and every client copy are kinematic, and all but puppets run the observer smoothing. It has its own
    /// teleport (TeleportShip) and is no IEntityMovementComponent, so the ship's kit Movement is null.
    /// </summary>
    public sealed partial class ServerMeshShipMovement : LiteNetLibBehaviour, IEntityMovementDataHandler, IMeshCompactStateSource, IMeshSenderOwnedSource
    {
        /// <summary>SteeringGain is the yaw and pitch P gain (the space ship's steeringGain).</summary>
        public const float SteeringGain = 2.5f;
        /// <summary>RollLevelGain is the roll auto-level P gain (the space ship's rollLevelGain).</summary>
        public const float RollLevelGain = 0.6f;
        /// <summary>ObserverLerpSpeed is the observer smoothing rate (the space ship's observerLerpSpeed).</summary>
        public const float ObserverLerpSpeed = 10f;
        /// <summary>SelfAckTicks is how many logic ticks a server-owned ship waits before acknowledging its own teleport.</summary>
        public const uint SelfAckTicks = 2;

        private static bool s_badUploadLogged;

        private ServerMeshShipEntity _ship;
        private Rigidbody _rigidbody;
        private ServerMeshShipEngine _engine;
        private ServerMeshShipWireKind _wire = ServerMeshShipWireKind.Legacy54;
        private ServerMeshShipScale _scale;
        private bool _realMirrorVelocity;
        private bool _selfAck;
        private byte _teleportSequence;
        private byte _ackedSequence;
        private byte _clientSequence;
        private byte _epoch;
        private uint _teleportTick;
        private bool _hasFeed;
        private long _feedTimestamp;
        private int _feedFrame;
        private bool _hasNet;
        private Vector3 _netPosition;
        private Quaternion _netRotation = Quaternion.identity;
        private Vector3 _netVelocity;
        private Vector3 _netAngularVelocity;
        private bool _netForward;
        private bool _hasLook;
        private Vector3 _lookDirection;
        private Vector3 _moveIntent;
        private bool _boost;
        private Vector3 _steerInput;
        private Vector3 _moveInput;
        private bool _simulating;

        /// <summary>TeleportSequence is the server's teleport sequence; an upload is applied only once it acknowledges it.</summary>
        public byte TeleportSequence => _teleportSequence;

        /// <summary>AcknowledgedSequence is the last teleport sequence the owner (or the server, for its own ships) acknowledged.</summary>
        public byte AcknowledgedSequence => _ackedSequence;

        /// <summary>
        /// Epoch rides in the header56 and compact28 records: 0 on the server (the space game never advances it in Phase 0),
        /// the last record's on a client.
        /// </summary>
        public byte Epoch => _epoch;

        /// <summary>TeleportPending is true on the server while the teleport sequence is unacknowledged.</summary>
        public bool TeleportPending => _teleportSequence != _ackedSequence;

        /// <summary>Wire is the record variant this peer writes and reads.</summary>
        public ServerMeshShipWireKind Wire => _wire;

        /// <summary>IsSimulating is true on the peer that runs the ship's rigidbody (the server, for ServerAI and Parked ships).</summary>
        public bool IsSimulating => _simulating;

        /// <summary>
        /// PuppetHeld is set by the puppet feeder right after NetworkSpawn, before the ship's first Update: the ship then
        /// never registers with the sender, never simulates and never smooths; its puppet body poses the transform.
        /// </summary>
        public bool PuppetHeld { get; set; }

        /// <summary>CurrentVelocity is the rigidbody's velocity on the simulating peer, else the last received velocity.</summary>
        public Vector3 CurrentVelocity => _simulating && _rigidbody != null ? _rigidbody.linearVelocity : _netVelocity;

        private void Awake()
        {
            _ship = GetComponent<ServerMeshShipEntity>();
            _rigidbody = GetComponent<Rigidbody>();
            if (_rigidbody != null)
            {
                _rigidbody.useGravity = false;
                _rigidbody.interpolation = RigidbodyInterpolation.None;
                _rigidbody.isKinematic = true;
            }
        }

        public override void OnIdentityInitialize()
        {
            base.OnIdentityInitialize();
            ResetState();
            ServerMeshShipSettings settings = _ship != null && _ship.IsConfigured ? _ship.Settings : ServerMeshShipLoad.Active;
            _wire = settings != null ? settings.Wire : ServerMeshShipWireKind.Legacy54;
            _scale = _ship != null && _ship.Scale != null ? _ship.Scale : settings != null ? settings.Scale : new ServerMeshShipScale(1f);
            _realMirrorVelocity = settings == null || settings.RealMirrorVelocity;
            _selfAck = settings != null && settings.SelfAck;
            // A pooled instance does not re-run Awake: the class's damping is applied at every spawn.
            ServerMeshShipClass shipClass = _ship != null ? _ship.ShipClass : ServerMeshShipClass.Base;
            if (_rigidbody != null)
            {
                _rigidbody.linearDamping = shipClass.LinearDrag;
                _rigidbody.angularDamping = shipClass.AngularDrag;
            }
            _engine = new ServerMeshShipEngine(_scale, shipClass);
            if (Manager is BaseGameNetworkManager manager)
            {
                manager.EntityMovementDataHandlers.TryRemove(ObjectId, out _);
                manager.EntityMovementDataHandlers.TryAdd(ObjectId, this);
                if (IsServer)
                    CacheSender(manager);
            }
            _netPosition = transform.position;
            _netRotation = transform.rotation;
            RefreshSimulationMode();
        }

        public override void OnNetworkDestroy(byte reasons)
        {
            base.OnNetworkDestroy(reasons);
            if (Manager is BaseGameNetworkManager manager)
            {
                ((ICollection<KeyValuePair<uint, IEntityMovementDataHandler>>)manager.EntityMovementDataHandlers)
                    .Remove(new KeyValuePair<uint, IEntityMovementDataHandler>(ObjectId, this));
            }
            ResetState();
        }

        private void ResetState()
        {
            _teleportSequence = _ackedSequence = _clientSequence = _epoch = 0;
            _teleportTick = 0;
            _hasFeed = false;
            _feedTimestamp = 0;
            _feedFrame = 0;
            _hasNet = false;
            _netVelocity = _netAngularVelocity = Vector3.zero;
            _netForward = false;
            _hasLook = false;
            _lookDirection = _moveIntent = _steerInput = _moveInput = Vector3.zero;
            _boost = false;
            PuppetHeld = false;
            ResetSender();
            if (_engine != null)
                _engine.Reset();
        }

        /// <summary>
        /// SetControl is the pilot's intent for the simulating peer: <paramref name="lookDirection"/> is the world direction to
        /// face (zero: no steering), <paramref name="worldMove"/> the world thrust whose magnitude, capped at 1, is the
        /// throttle; the P-steering turns them into the engine's local inputs each frame.
        /// </summary>
        public void SetControl(Vector3 lookDirection, Vector3 worldMove, bool boost)
        {
            _hasLook = lookDirection.sqrMagnitude > 1e-8f;
            _lookDirection = _hasLook ? lookDirection.normalized : Vector3.zero;
            _moveIntent = worldMove;
            _boost = boost;
        }

        /// <summary>
        /// TeleportShip moves the ship at once on the server: the teleport sequence goes up, transform and rigidbody take the
        /// pose, velocities and the engine's spooled force are zeroed. Records go reliably until the sequence is acknowledged.
        /// </summary>
        public void TeleportShip(Vector3 position, Quaternion rotation)
        {
            if (!IsServer)
                return;
            unchecked { _teleportSequence++; }
            _teleportTick = Manager != null ? Manager.LocalTick : 0;
            transform.SetPositionAndRotation(position, rotation);
            if (_rigidbody != null)
            {
                _rigidbody.position = position;
                _rigidbody.rotation = rotation;
                if (!_rigidbody.isKinematic)
                {
                    _rigidbody.linearVelocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }
            }
            _netPosition = position;
            _netRotation = rotation;
            _netVelocity = _netAngularVelocity = Vector3.zero;
            if (_engine != null)
                _engine.Reset();
        }

        /// <summary>
        /// MarkFeed records an in-process owner feed on the server: <paramref name="peerTimestamp"/> is the timestamp the feed
        /// passed to ReadClientStateAtServer and <paramref name="frame"/> its Time.frameCount, for the Ships window's mirror age.
        /// </summary>
        public void MarkFeed(long peerTimestamp, int frame)
        {
            _hasFeed = true;
            _feedTimestamp = peerTimestamp;
            _feedFrame = frame;
        }

        private void Update()
        {
            if (!IsSpawned)
                return;
            RefreshSimulationMode();
            if (PuppetHeld)
                return;
            if (IsServer)
            {
                if (SelfAckDue(_selfAck, ConnectionId < 0, _teleportSequence, _ackedSequence, Manager.LocalTick, _teleportTick))
                    _ackedSequence = _teleportSequence;
                RetryRegistration();
            }
            if (_simulating)
            {
                ApplyControlIntent();
                return;
            }
            if (!_hasNet)
                return;
            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            ObserverStep(position, rotation, ref _netPosition, _netVelocity, _netRotation, Time.deltaTime, _scale.SnapDistance, out position, out rotation);
            transform.SetPositionAndRotation(position, rotation);
        }

        private void FixedUpdate()
        {
            if (!_simulating || _rigidbody == null || _engine == null)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _engine.Step(_steerInput, _moveInput, _boost, _rigidbody.linearVelocity.magnitude, Time.fixedDeltaTime, out Vector3 torque, out Vector3 force);
            _rigidbody.AddRelativeTorque(torque, ForceMode.Acceleration);
            _rigidbody.AddRelativeForce(force);
            ServerMeshShipCounters.Current.EngineMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void RefreshSimulationMode()
        {
            bool configured = _ship != null && _ship.IsConfigured;
            ServerMeshShipAuthority authority = _ship != null ? _ship.Authority : ServerMeshShipAuthority.OwnerMirror;
            _simulating = Simulates(IsServer, PuppetHeld, authority, configured);
            if (_rigidbody == null)
                return;
            if (_rigidbody.isKinematic == _simulating)
                _rigidbody.isKinematic = !_simulating;
            if (_rigidbody.interpolation != RigidbodyInterpolation.None)
                _rigidbody.interpolation = RigidbodyInterpolation.None;
            if (!_simulating)
                _steerInput = _moveInput = Vector3.zero;
        }

        private void ApplyControlIntent()
        {
            Quaternion rotation = transform.rotation;
            _moveInput = LocalThrust(rotation, _moveIntent);
            _steerInput = _hasLook ? SteeringInputs(rotation, _lookDirection) : Vector3.zero;
        }

        /// <summary>
        /// ReadClientStateAtServer applies an owner's upload and never throws (the kit's upload path has no catch): fewer
        /// bytes than the upload size, or a header whose wire mode is not 0, counts bad and is dropped; otherwise it stores the
        /// acknowledged sequence, drops the state while that lags the teleport sequence (stale), and else caches it and sets
        /// the transform directly.
        /// </summary>
        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            int available = reader == null ? 0 : reader.AvailableBytes;
            byte first = available > 0 ? reader.PeekByte() : (byte)0;
            if (!IsUploadReadable(_wire, available, first))
            {
                counters.Bad++;
                if (!s_badUploadLogged)
                {
                    s_badUploadLogged = true;
                    Logging.LogError("Mesh", $"Ships error: object {ObjectId} dropped an upload of {available} bytes with first byte {first} under wire {ServerMeshShipLoad.WireName(_wire)}; later bad uploads are only counted");
                }
                return;
            }
            ServerMeshShipSample sample;
            if (_wire == ServerMeshShipWireKind.Legacy54)
                ServerMeshShipWire.ReadLegacy54(reader, out sample);
            else
                ServerMeshShipWire.ReadHeader56(reader, out sample);
            _ackedSequence = sample.TeleportByte;
            if (IsStaleUpload(_ackedSequence, _teleportSequence))
            {
                counters.Stale++;
                return;
            }
            _netPosition = sample.Position;
            _netRotation = sample.Rotation;
            _netVelocity = sample.Velocity;
            _netAngularVelocity = sample.AngularVelocity;
            _netForward = sample.Forward;
            _hasNet = true;
            transform.SetPositionAndRotation(sample.Position, sample.Rotation);
            counters.Uploads++;
        }

        /// <summary>
        /// ReadServerStateAtClient decodes one record of the configured wire and never throws on a record's content. A record
        /// of a wire mode the client does not read (1 or 2), or one the reader has too few bytes left for, is counted by the
        /// bot watch as a bad length and skipped (the kit resumes at the record's end). The first record, or a new teleport
        /// sequence, snaps at once: a mode 0 record compares all 8 bits; a compact one compares the low 7
        /// (CompactSequenceDiffers) and advances the client's sequence by their difference (AdvanceToCompactSequence).
        /// Anything else is cached for the observer smoothing. The watch gets every decoded record.
        /// </summary>
        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            ServerMeshShipWatch watch = ServerMeshShipWatch.Instance;
            int available = reader.AvailableBytes;
            int need = ClientRecordSize(_wire, available, available > 0 ? reader.PeekByte() : (byte)0);
            if (need == 0 || available < need)
            {
                if (watch != null)
                    watch.CountBadLength();
                return;
            }
            ServerMeshShipWire.Read(reader, _wire, out ServerMeshShipSample sample);
            bool snap;
            if (sample.Compact)
            {
                snap = !_hasNet || CompactSequenceDiffers(_clientSequence, sample.TeleportByte);
                if (snap)
                    _clientSequence = AdvanceToCompactSequence(_clientSequence, sample.TeleportByte);
            }
            else
            {
                snap = !_hasNet || sample.TeleportByte != _clientSequence;
                if (snap)
                    _clientSequence = sample.TeleportByte;
            }
            if (snap)
                transform.SetPositionAndRotation(sample.Position, sample.Rotation);
            _netPosition = sample.Position;
            _netRotation = sample.Rotation;
            _netVelocity = sample.Velocity;
            _netAngularVelocity = sample.AngularVelocity;
            _netForward = sample.Forward;
            _epoch = sample.Epoch;
            _hasNet = true;
            if (watch != null && _ship != null)
                watch.Record(ObjectId, peerTimestamp, in sample, _ship.FlightPathIndex, _ship.PathCentre);
        }

        /// <summary>Simulates is true on the peer that runs the rigidbody: a configured ServerAI or Parked ship on the server that no puppet holds.</summary>
        public static bool Simulates(bool isServer, bool puppetHeld, ServerMeshShipAuthority authority, bool configured)
        {
            return isServer && configured && !puppetHeld && authority != ServerMeshShipAuthority.OwnerMirror;
        }

        /// <summary>SmoothingFactor is the observer lerp share for one frame of <paramref name="deltaTime"/> seconds: 1 − exp(−10·dt).</summary>
        public static float SmoothingFactor(float deltaTime) => 1f - Mathf.Exp(-ObserverLerpSpeed * deltaTime);

        /// <summary>
        /// ObserverStep is one frame of the space ship's observer smoothing: <paramref name="netPosition"/> is first
        /// extrapolated by <paramref name="netVelocity"/>·dt; past <paramref name="snapDistance"/> from the current position
        /// the pose snaps to the target (true), else position and rotation move by SmoothingFactor(dt) (lerp and slerp).
        /// </summary>
        public static bool ObserverStep(Vector3 currentPosition, Quaternion currentRotation, ref Vector3 netPosition, Vector3 netVelocity,
            Quaternion netRotation, float deltaTime, float snapDistance, out Vector3 position, out Quaternion rotation)
        {
            netPosition += netVelocity * deltaTime;
            if (Vector3.Distance(currentPosition, netPosition) > snapDistance)
            {
                position = netPosition;
                rotation = netRotation;
                return true;
            }
            float t = SmoothingFactor(deltaTime);
            position = Vector3.Lerp(currentPosition, netPosition, t);
            rotation = Quaternion.Slerp(currentRotation, netRotation, t);
            return false;
        }

        /// <summary>IsStaleUpload is true while an upload's acknowledged sequence lags the server's teleport sequence.</summary>
        public static bool IsStaleUpload(byte acknowledged, byte teleportSequence) => acknowledged != teleportSequence;

        /// <summary>
        /// SelfAckDue is true when a server-owned ship should acknowledge its own teleport: only while <paramref name="enabled"/>
        /// (MESH_SHIP_SELF_ACK=1; by default only an owner's upload acknowledges, as in the space game), the sequence is
        /// unacknowledged and the logic tick has advanced by SelfAckTicks since the teleport, so a full send pass carried it
        /// reliably.
        /// </summary>
        public static bool SelfAckDue(bool enabled, bool serverOwned, byte sequence, byte acknowledged, uint nowTick, uint teleportTick)
        {
            return enabled && serverOwned && sequence != acknowledged && unchecked(nowTick - teleportTick) >= SelfAckTicks;
        }

        /// <summary>
        /// CompactSequenceDiffers is true when a compact record's 7-bit teleport sequence <paramref name="sequence7"/> is not
        /// the low 7 bits of the client's sequence <paramref name="current"/>.
        /// </summary>
        public static bool CompactSequenceDiffers(byte current, byte sequence7) => (current & 0x7F) != sequence7;

        /// <summary>
        /// AdvanceToCompactSequence is the client's sequence after a compact record carrying <paramref name="sequence7"/>:
        /// <paramref name="current"/> moved forward by the 7-bit difference, so its low 7 bits equal the record's and the
        /// reliable mode 0 record of the same teleport, if a compact record overtook it, finds nothing new to snap to.
        /// </summary>
        public static byte AdvanceToCompactSequence(byte current, byte sequence7) => unchecked((byte)(current + ((sequence7 - current) & 0x7F)));

        /// <summary>UploadSize is the bytes an upload must carry: 54 under legacy54, else 56 (uploads are never compact).</summary>
        public static int UploadSize(ServerMeshShipWireKind wire)
        {
            return wire == ServerMeshShipWireKind.Legacy54 ? ServerMeshShipWire.Legacy54Bytes : ServerMeshShipWire.Header56Bytes;
        }

        /// <summary>
        /// IsUploadReadable is false for an upload with fewer than UploadSize bytes, or, under header56 and compact28, one
        /// whose header byte's wire mode is not 0 (Bubble). A legacy54 record has no header, so its first byte is never tested.
        /// </summary>
        public static bool IsUploadReadable(ServerMeshShipWireKind wire, int availableBytes, byte firstByte)
        {
            if (availableBytes < UploadSize(wire))
                return false;
            return wire == ServerMeshShipWireKind.Legacy54 || ServerMeshShipWire.ModeOf(firstByte) == ServerMeshShipWire.ModeBubble;
        }

        /// <summary>
        /// ClientRecordSize is the bytes a record of <paramref name="wire"/> needs on a client: 54 under legacy54; else, by
        /// the header byte's wire mode, 56 for mode 0, 28 for mode 3 and 0 for modes 1 and 2, which a client does not read
        /// (56 when nothing is left to peek).
        /// </summary>
        public static int ClientRecordSize(ServerMeshShipWireKind wire, int availableBytes, byte firstByte)
        {
            if (wire == ServerMeshShipWireKind.Legacy54)
                return ServerMeshShipWire.Legacy54Bytes;
            if (availableBytes <= 0)
                return ServerMeshShipWire.Header56Bytes;
            int mode = ServerMeshShipWire.ModeOf(firstByte);
            if (mode == ServerMeshShipWire.ModeBubble)
                return ServerMeshShipWire.Header56Bytes;
            return mode == ServerMeshShipWire.ModeCompact ? ServerMeshShipWire.Compact28Bytes : 0;
        }


        /// <summary>
        /// SteeringInputs is the space ship's P-steering toward <paramref name="lookDirection"/>: (pitch, yaw, roll), the
        /// yaw and pitch errors times SteeringGain and the roll toward world up times RollLevelGain, each clamped to ±1.
        /// </summary>
        public static Vector3 SteeringInputs(Quaternion rotation, Vector3 lookDirection)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 local = inverse * lookDirection;
            float yawError = Mathf.Atan2(local.x, Mathf.Max(0.0001f, local.z));
            float pitchError = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f));
            Vector3 localUp = inverse * Vector3.up;
            float rollError = Mathf.Atan2(localUp.x, Mathf.Max(0.0001f, localUp.y));
            return new Vector3(
                Mathf.Clamp(pitchError * SteeringGain, -1f, 1f),
                Mathf.Clamp(yawError * SteeringGain, -1f, 1f),
                Mathf.Clamp(rollError * RollLevelGain, -1f, 1f));
        }

        /// <summary>LocalThrust is <paramref name="worldMove"/> in the ship's frame with its magnitude capped at 1 (zero under 0.01).</summary>
        public static Vector3 LocalThrust(Quaternion rotation, Vector3 worldMove)
        {
            if (worldMove.sqrMagnitude <= 0.0001f)
                return Vector3.zero;
            Vector3 local = Quaternion.Inverse(rotation) * worldMove;
            if (local.sqrMagnitude > 1f)
                local.Normalize();
            return local;
        }
    }
}
