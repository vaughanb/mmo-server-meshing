// Server Meshing add-on — a kinematic character puppet: it stands in the kit's movement-handler slot, places the puppet
// from a track of its owner's snapshots, and writes exactly the kit's server bytes, so clients read it with their own
// kit handler.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshKinematicCharacterBody replaces a character puppet's kit movement while the puppet mirrors an entity another cell
    /// owns. Begin disables the kit's movement component and installs the body in the object's handler slot; Accept feeds
    /// it each owner snapshot; Tick (observed) and ApplyNow (unobserved) place the transform; the kit's send loop calls
    /// WriteServerState, which writes the kit's own server layout from the transform; End gives everything back.
    /// A discontinuity on a NavMesh-kit (monster) body sends IsTeleport alone, reliably, for exactly one write timestamp;
    /// a character-controller-kit (player) body never sets it, since its clients snap by themselves past their threshold.
    /// </summary>
    public sealed class MeshKinematicCharacterBody : IEntityMovementDataHandler, IMeshSenderOwnedSource
    {
        private const int OwnerOnlyFlags = (int)(MovementState.IsJump | MovementState.IsTeleport);
        private const uint TeleportFlags = (uint)MovementState.IsTeleport;
        private const float RestSpeedSquared = 0.0001f;

        private readonly uint _objectId;
        private readonly LiteNetLibIdentity _identity;
        private readonly Transform _transform;
        private readonly IEntityMovementDataHandler _kitHandler;
        private readonly bool _kitIsNavMesh;
        private readonly List<EntityMovementForceApplier> _noForceAppliers = new List<EntityMovementForceApplier>(0);
        private MeshKinematicTrack _track;
        private byte _flags;
        private byte _extra;
        private uint _discreteVersion;
        private bool _teleportArmed;
        private bool _teleportStamped;
        private long _teleportStamp;
        private bool _isBegun;
        private bool _carriesSeat;
        private Vector3 _seatOffset;
        private float _seatYaw;

        /// <summary>
        /// MeshKinematicCharacterBody creates the body of object <paramref name="objectId"/>, which moves
        /// <paramref name="transform"/> and stands in front of <paramref name="kitHandler"/> (the handler the slot held,
        /// a NavMesh kit handler when <paramref name="kitIsNavMesh"/>). <paramref name="identity"/> may be null (no
        /// subscribers known); <paramref name="track"/> must come from its constructor.
        /// </summary>
        public MeshKinematicCharacterBody(uint objectId, LiteNetLibIdentity identity, Transform transform,
            IEntityMovementDataHandler kitHandler, bool kitIsNavMesh, MeshKinematicTrack track)
        {
            if (transform == null)
                throw new ArgumentNullException(nameof(transform));
            if (kitHandler == null)
                throw new ArgumentNullException(nameof(kitHandler));
            if (!track.IsConfigured)
                throw new ArgumentException("the track must be created with its constructor, not default", nameof(track));
            _objectId = objectId;
            _identity = identity;
            _transform = transform;
            _kitHandler = kitHandler;
            _kitIsNavMesh = kitIsNavMesh;
            _track = track;
        }

        /// <summary>ObjectId is the puppet's network object id, the handler slot's key and the state record's key.</summary>
        public uint ObjectId => _objectId;

        /// <summary>ConnectionId is -1, so the kit drops every client upload addressed to the body.</summary>
        public long ConnectionId => -1;

        /// <summary>Identity is the puppet's network identity (null when the body was created without one).</summary>
        public LiteNetLibIdentity Identity => _identity;

        /// <summary>DiscreteVersion goes up by one every time an accepted snapshot snaps the pose (a discontinuity, or a miss past the snap distance).</summary>
        public uint DiscreteVersion => _discreteVersion;

        /// <summary>IsAtRest is true when the owner's velocity is under 0.01 m/s and no teleport is waiting to be sent.</summary>
        public bool IsAtRest => _track.Velocity.sqrMagnitude < RestSpeedSquared && !_teleportArmed && !_teleportStamped;

        /// <summary>KitHandler is the kit handler the body stands in front of.</summary>
        public IEntityMovementDataHandler KitHandler => _kitHandler;

        /// <summary>KitIsNavMesh is true for a NavMesh kit handler (a monster puppet).</summary>
        public bool KitIsNavMesh => _kitIsNavMesh;

        /// <summary>IsBegun is true between a successful Begin and End.</summary>
        public bool IsBegun => _isBegun;

        /// <summary>
        /// CarriesSeat is true for the body of a mounted puppet's vehicle (DESIGN 1.27): its track holds the rider's pose,
        /// the one its snapshots carry, and the transform it moves is the vehicle's, placed so that the seat stands there.
        /// </summary>
        public bool CarriesSeat => _carriesSeat;

        /// <summary>
        /// SetSeat makes the body carry a seat: <paramref name="localOffset"/> is where the seat stands in the vehicle's frame
        /// (its position relative to the vehicle's, unrotated by the vehicle's rotation) and <paramref name="yawOffset"/> the
        /// seat's yaw relative to the vehicle's, in degrees. Snapshots and Pose stay in the rider's coordinates.
        /// </summary>
        public void SetSeat(Vector3 localOffset, float yawOffset)
        {
            _carriesSeat = true;
            _seatOffset = localOffset;
            _seatYaw = yawOffset;
        }

        /// <summary>SeatOffsetAt is where the seat stands relative to the vehicle, in world axes, when the rider faces <paramref name="riderYaw"/>.</summary>
        public Vector3 SeatOffsetAt(float riderYaw)
        {
            return _carriesSeat ? Quaternion.Euler(0f, riderYaw - _seatYaw, 0f) * _seatOffset : Vector3.zero;
        }

        /// <summary>
        /// SentBySender is true while the body is registered with the state sender, which then writes it once per build
        /// through WriteState with one timestamp; the kit-facing WriteServerState returns false and writes nothing, so the
        /// body never has two writers. Whoever takes the body out of the sender clears it (End always does), so the kit
        /// sends the body again.
        /// </summary>
        public bool SentBySender { get; set; }

        /// <summary>OnSenderReleased clears SentBySender when the state sender lets go of the body unasked, so the kit sends it again.</summary>
        public void OnSenderReleased() => SentBySender = false;

        /// <summary>
        /// Refusal is why a puppet cannot move kinematically, or None: Handler unless <paramref name="kitHandler"/> is a
        /// BuiltInEntityMovementFunctions3D or a NavMeshEntityMovement (the handlers whose bytes the body reproduces), then
        /// Mounted, then Summoned. The first never changes for a puppet; the last two can. The body is not a quantized
        /// handler, so the kit's quantized send path calls its WriteServerState like the raw one does.
        /// </summary>
        public static MeshPuppetFallback Refusal(IEntityMovementDataHandler kitHandler, bool mounted, bool summoned)
        {
            if (kitHandler is not (BuiltInEntityMovementFunctions3D or NavMeshEntityMovement))
                return MeshPuppetFallback.Handler;
            if (mounted)
                return MeshPuppetFallback.Mounted;
            if (summoned)
                return MeshPuppetFallback.Summoned;
            return MeshPuppetFallback.None;
        }

        /// <summary>
        /// QuantizedMode is the compression mode of every quantized position the add-on writes: the finest (about 2 mm
        /// steps in 128 m cells), since one record goes to every receiver whatever its distance.
        /// </summary>
        public const int QuantizedMode = QuantizedPosition.MaxMode;

        /// <summary>
        /// QuantizedCellSize is the kit's cell size in metres while the kit sends quantized positions, so clients read
        /// every 3D server record in that layout; 0 while it sends raw floats or there is no grid manager (EditMode).
        /// </summary>
        public static float QuantizedCellSize
        {
            get
            {
                IBaseGridManagerComponenent grid = DefaultGridManagerComponent.Instance;
                if (grid is UnityEngine.Object gridObject ? gridObject == null : grid == null)
                    return 0f;
                return grid.IsDisabled ? 0f : grid.CellSize;
            }
        }

        /// <summary>
        /// WriteKitTransform appends exactly the kit's server transform record for the kit's current position format (see
        /// QuantizedCellSize): packed flags, the extra state byte, the pose (WriteKitPose) and an empty force-applier list.
        /// </summary>
        public static void WriteKitTransform(NetDataWriter writer, uint flags, byte extra, Vector3 position, float yawDegrees)
        {
            WriteKitTransform(writer, flags, extra, position, yawDegrees, QuantizedCellSize);
        }

        /// <summary>
        /// WriteKitTransform appends the kit's server transform record with the pose raw when
        /// <paramref name="quantizedCellSize"/> is 0, else quantized in cells of that size.
        /// </summary>
        public static void WriteKitTransform(NetDataWriter writer, uint flags, byte extra, Vector3 position, float yawDegrees, float quantizedCellSize)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.PutPackedUInt(flags);
            writer.Put(extra);
            WriteKitPose(writer, position, yawDegrees, quantizedCellSize);
            writer.Put(0);
        }

        /// <summary>
        /// WriteKitPose appends the position and yaw of a kit server record. Raw when <paramref name="quantizedCellSize"/>
        /// is 0: the Vector3 and the packed compressed yaw (normalised to [0, 360) as Transform.eulerAngles.y is), as
        /// EntityMovementFunctions.ServerWriteSyncTransform3D. Quantized otherwise: the signed cells and offsets in
        /// QuantizedMode, then the one-byte yaw, as DefaultGridManagerComponent.WriteEntityServerState.
        /// </summary>
        public static void WriteKitPose(NetDataWriter writer, Vector3 position, float yawDegrees, float quantizedCellSize)
        {
            if (quantizedCellSize > 0f)
            {
                writer.PutQuantizedPosition(QuantizedPosition.Encode(position.x, position.y, position.z, quantizedCellSize, QuantizedMode));
                writer.Put(GridUtility.CompressAngle(yawDegrees));
                return;
            }
            writer.PutVector3(position);
            writer.PutPackedInt(EntityMovementFunctions.GetCompressedAngle(Mathf.Repeat(yawDegrees, 360f)));
        }

        /// <summary>
        /// Begin takes the puppet over: stops the kit's movement, installs the body in the slot (not replaceable), adds the
        /// body as a key to the entity's MovementDisableState and FallDamageDisableState, and disables the movement
        /// component. False, with the reason, when the entity is another object, has no movement component, the body has
        /// already begun, or the slot refuses it; nothing is left changed then except the stopped movement.
        /// </summary>
        public bool Begin(BaseGameEntity entity, MeshMovementHandlerSlots slots, out string error)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));
            if (slots == null)
                throw new ArgumentNullException(nameof(slots));
            if (_isBegun)
            {
                error = $"object {_objectId} kinematic body has already begun";
                return false;
            }
            if (entity.ObjectId != _objectId)
            {
                error = $"object {_objectId} kinematic body cannot begin on object {entity.ObjectId}";
                return false;
            }
            IEntityMovementComponent movement = entity.Movement;
            if (movement.IsNull())
            {
                error = $"object {_objectId} has no movement component for a kinematic body to disable";
                return false;
            }
            movement.StopMove();
            if (!slots.TryInstall(_objectId, this, false, out string installError))
            {
                error = installError;
                return false;
            }
            BaseCharacterEntity character = entity as BaseCharacterEntity; // a vehicle has no fall damage
            try
            {
                entity.MovementDisableState.Add(this);
                character?.FallDamageDisableState.Add(this);
                movement.enabled = false;
            }
            catch
            {
                entity.MovementDisableState.Remove(this);
                character?.FallDamageDisableState.Remove(this);
                slots.TryRemove(_objectId, this, out _);
                throw;
            }
            _isBegun = true;
            error = null;
            return true;
        }

        /// <summary>
        /// End gives the puppet back to the kit; a second call does nothing. It removes the body from the slot (or forgets
        /// it when the entity is destroyed), removes both disable keys, enables the movement component and turns the kit's
        /// look rotation to the current yaw at once, so the kit does not turn the puppet back to its yaw before Begin.
        /// It clears SentBySender first, even on a body that never began.
        /// </summary>
        public void End(BaseGameEntity entity, MeshMovementHandlerSlots slots)
        {
            SentBySender = false;
            if (slots == null)
                throw new ArgumentNullException(nameof(slots));
            if (!_isBegun)
                return;
            _isBegun = false;
            _teleportArmed = false;
            _teleportStamped = false;
            bool alive = entity != null && entity.Identity != null && !entity.Identity.IsDestroyed;
            if (!alive)
                slots.Forget(_objectId);
            else if (!slots.TryRemove(_objectId, this, out string error))
                Logging.LogWarning("Mesh", $"Kinematic body end: {error}");
            if (entity == null)
                return;
            entity.MovementDisableState.Remove(this);
            (entity as BaseCharacterEntity)?.FallDamageDisableState.Remove(this);
            IEntityMovementComponent movement = entity.Movement;
            if (movement.IsNull())
                return;
            movement.enabled = true;
            movement.SetLookRotation(Quaternion.Euler(0f, _transform.eulerAngles.y, 0f), true);
        }

        /// <summary>
        /// Accept feeds one owner snapshot that arrived at <paramref name="now"/>; false (nothing changes) when the track
        /// refuses it. The owner's transient IsJump and IsTeleport bits are masked out of <paramref name="flags"/>. When the
        /// track snaps (a discontinuity, or a miss past its snap distance: LastAcceptSnapped) it raises DiscreteVersion and,
        /// on a NavMesh-kit body that is not known to be unobserved, arms the teleport for the next write timestamp.
        /// <paramref name="sampleAgeSeconds"/> is how old the snapshot was when it arrived (MeshKinematicTrack.TryAccept).
        /// </summary>
        public bool Accept(Vector3 position, Vector3 velocity, float yawDegrees, byte flags, byte extra, bool discontinuity, float now, float sampleAgeSeconds = 0f)
        {
            if (!_track.TryAccept(position, velocity, yawDegrees, discontinuity, now, sampleAgeSeconds))
                return false;
            _flags = (byte)(flags & ~OwnerOnlyFlags);
            _extra = extra;
            if (_track.LastAcceptSnapped)
            {
                _discreteVersion++;
                if (_kitIsNavMesh && !IsKnownUnobserved)
                {
                    _teleportArmed = true;
                    _teleportStamped = false;
                }
            }
            return true;
        }

        /// <summary>LastAcceptSnapped is true when the last accepted snapshot snapped the pose (see Accept).</summary>
        public bool LastAcceptSnapped => _track.LastAcceptSnapped;

        /// <summary>
        /// Pose is where the puppet stands at <paramref name="now"/> on its track (the owner's snapshot extrapolated to now,
        /// plus the decaying offset of the last one), or the transform's position before the first snapshot.
        /// </summary>
        public Vector3 Pose(float now)
        {
            if (_track.HasSample)
                return _track.Pose(now, 0f, out _);
            float vehicleYaw = _transform.eulerAngles.y;
            return _transform.position + SeatOffsetAt(vehicleYaw + _seatYaw);
        }

        /// <summary>
        /// Tick places an observed puppet's transform at its pose for <paramref name="now"/>; the manager calls it right
        /// before the kit sends movement, so what clients receive is current (nothing before the first snapshot).
        /// </summary>
        public void Tick(float now)
        {
            if (!_track.HasSample)
                return;
            Vector3 position = _track.Pose(now, 0f, out float yaw);
            if (_carriesSeat)
            {
                position -= SeatOffsetAt(yaw); // the vehicle stands so that its seat is where the rider is
                yaw -= _seatYaw;
            }
            _transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        }

        /// <summary>
        /// ApplyNow places an unobserved puppet's transform on its owner's snapshot extrapolated to <paramref name="now"/>, at
        /// a snapshot: nothing moves it between snapshots, and with no client to glide for, the offset is dropped so the
        /// server's own logic reads where the owner is. A teleport waiting to be sent is dropped too.
        /// </summary>
        public void ApplyNow(float now)
        {
            _teleportArmed = false;
            _teleportStamped = false;
            if (!_track.HasSample)
                return;
            _track.DropOffset();
            Tick(now);
        }

        /// <summary>CreateMovementData is the current pose with no force appliers (the kit's send loops never ask the body for it).</summary>
        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            forceAppliers = _noForceAppliers;
            return new MovementData
            {
                movementState = _flags,
                extraMovementState = _extra,
                worldPosition = _transform.position,
                yAngle = _transform.eulerAngles.y,
                shouldSendReliably = false,
            };
        }

        /// <summary>ReadClientStateAtServer ignores the upload: the owner moves the entity on its own cell.</summary>
        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
        }

        /// <summary>ReadServerStateAtClient consumes exactly one record of the kit's server layout (raw or quantized, see QuantizedCellSize) and applies nothing.</summary>
        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            if (QuantizedCellSize > 0f)
            {
                reader.GetPackedUInt();
                reader.GetByte();
                reader.GetEncodedPosition();
                reader.GetByte();
            }
            else
            {
                reader.ServerReadSyncTransformMessage3D(out _, out _, out _, out _);
            }
            reader.GetList<EntityMovementForceApplier>();
        }

        /// <summary>WriteClientState writes nothing: a server never uploads.</summary>
        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        /// <summary>
        /// WriteServerState appends the kit's server record of the transform's position and yaw with the snapshot's flags
        /// and extra state. It changes nothing except the teleport stamp: an armed teleport is sent as IsTeleport alone,
        /// reliably, by the first write and by every write with that same timestamp; a write with another timestamp ends it.
        /// Two writes with one timestamp therefore write identical bytes. Drive it from one clock only. While SentBySender
        /// it returns false and writes nothing (the sender writes the body through WriteState).
        /// </summary>
        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (SentBySender)
            {
                shouldSendReliably = false;
                return false;
            }
            return WriteRecord(writeTimestamp, writer, out shouldSendReliably);
        }

        /// <summary>
        /// WriteState writes the kit's server record as WriteServerState does without SentBySender, whatever SentBySender
        /// says, and routes the record reliably while a teleport is sent.
        /// </summary>
        public bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            return WriteRecord(sampleTimestamp, writer, out sendReliably);
        }

        private bool WriteRecord(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            bool teleport = TakeTeleport(writeTimestamp);
            shouldSendReliably = teleport;
            WriteKitTransform(writer, teleport ? TeleportFlags : _flags, _extra, _transform.position, _transform.eulerAngles.y);
            return true;
        }

        private bool IsKnownUnobserved => _identity != null && _identity.CountSubscribers() == 0;

        private bool TakeTeleport(long writeTimestamp)
        {
            if (_teleportArmed)
            {
                _teleportArmed = false;
                _teleportStamped = true;
                _teleportStamp = writeTimestamp;
                return true;
            }
            if (!_teleportStamped)
                return false;
            if (_teleportStamp == writeTimestamp)
                return true;
            _teleportStamped = false;
            return false;
        }
    }
}
