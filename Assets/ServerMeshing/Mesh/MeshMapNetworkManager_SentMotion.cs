// Server Meshing add-on — a client-driven player is sent along its client's own trajectory, not wherever its last state
// left it (DESIGN.md 1.20).
//
// The kit's server puts a player where its client's latest state says, as that state arrives, and sends whatever is
// there at its next tick. Client states arrive unevenly (the client's frames, the transport's 15 ms send cycle, the
// server's frames), so some ticks found no new state and sent the same position twice and the next one a double step:
// a one-tick stand about every 250 ms for everyone watching a walking player, and right after every crossing a burst
// of them while the new connection's states settle, on top of the step from the destination's walk onto the client's
// first state (2-14 cm). Observers' buffers (1.17) draw the stamped positions faithfully, so they drew both.
//
// Now a layer in the slot of each player this cell owns reads each client state's timestamp (the client's estimate of
// the server clock) and position before the kit applies it, into a MeshRemoteMotionTrack, and right before each kit
// send the player is put where that track says: the client's trajectory, a sample interval behind its newest state,
// extrapolated for at most 100 ms past it. The track starts from where the player stands (an arrival: where its walk
// had it) and glides off that over ~70 ms. Only what is sent moves: the kit puts the transform back on its own path
// every frame, and its checks of the client's movement read its own state. A teleport, a snap, a player that cannot
// move, or a transform the track's pose is a metre off (the server moved it) start the track again from two states.
//
// DESIGN 1.27: a mounted player is moved by its vehicle's seat, and its client drives the vehicle. The layer posed the
// rider from the rider's own client states anyway, which are not where the seat has it. A passenger is no longer posed
// (its track waits until it is on foot again), and a client-driven vehicle is layered like a player, so what is sent of a
// mount follows its client's trajectory. A pose that is not finite is never written.
//
// DESIGN 1.27 also: the layer answered the kit's send loop with its kit handler's WriteServerState, the raw layout, while
// the kit's grid is on (1.13) and clients read every 3D record quantized. Every record of a layered player reached its
// observers as ~1e11 m, and it vanished for them for up to a few seconds at a time (69 times in the 4 minutes 1.20 was
// measured in; the stall trace only follows characters within 30 m, so it never saw them). The layer is now a quantized
// handler, so the kit builds its record from CreateMovementData in the layout clients read, and it passes the kit's
// one-shot flags (jump, dash, teleport) on to be consumed: the kit consumes only the flags of handlers that expose them.
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Server: send a client-driven player along its client's stamped trajectory instead of its latest state as it arrived (DESIGN 1.20).")]
        public bool smoothSentPlayerMotion = true;

        [Tooltip("Server: how far behind its client's newest state a player is sent, in sample intervals (at least 10 ms).")]
        public float sentMotionDelayIntervals = 1f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private const float SentMotionSweepSeconds = 0.25f;
        private const float SentMotionOffTrack = 1f;
        private const float SentMotionMaxLeadSeconds = 0.25f;

        private sealed class SentMotionLayer : IEntityMovementDataHandler, IQuantizedEntityMovementDataHandler, IEntityMovementServerStateFlags, IMeshReplaceableHandlerLayer
        {
            public readonly BuiltInEntityMovementFunctions3D inner;
            public readonly MeshRemoteMotionTrack track;
            public bool replaced;
            public float lastPoseAt = -1f;
            public bool hasLead;          // an arrival's walk, sent until its client's states can be drawn
            public Vector3 leadPosition;
            public Vector3 leadVelocity;
            public float leadAt;
            private readonly Stopwatch _clock;

            public SentMotionLayer(BuiltInEntityMovementFunctions3D inner, MeshRemoteMotionTrack track, Stopwatch clock)
            {
                this.inner = inner;
                this.track = track;
                _clock = clock;
            }

            public uint ObjectId => inner.ObjectId;
            public long ConnectionId => inner.ConnectionId;

            /// <summary>As the kit handler's: the kit's quantized send path builds the record from CreateMovementData (DESIGN 1.27).</summary>
            public bool CanWriteQuantizedServerState => inner.CanWriteQuantizedServerState;

            public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
            {
                return inner.CreateMovementData(out forceAppliers);
            }

            /// <summary>The kit consumes one-shot flags only through the handler in the slot: pass them on.</summary>
            public void ConsumeServerStateFlags()
            {
                inner.ConsumeServerStateFlags();
            }

            /// <summary>Keeps the state's stamp and position, then lets the kit's handler read the same bytes.</summary>
            public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
            {
                if (inner.movementSecure == MovementSecure.NotSecure)
                {
                    int start = reader.Position;
                    reader.ServerReadSyncTransformMessage3D(out MovementState movementState, out _, out Vector3 position, out _);
                    reader.SetPosition(start);
                    BaseGameEntity entity = inner.Entity;
                    if (entity is BaseCharacterEntity character && !character.CanMove())
                        track.Reset();
                    else
                    {
                        bool snap = movementState.Has(MovementState.IsTeleport)
                            || (track.Count > 0 && Vector3.Distance(position, entity.EntityTransform.position) >= inner.snapThreshold);
                        track.Add(peerTimestamp, _clock.Elapsed.TotalMilliseconds, position, snap);
                    }
                }
                inner.ReadClientStateAtServer(peerTimestamp, reader);
            }

            public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
            {
                inner.ReadServerStateAtClient(peerTimestamp, reader);
            }

            public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
            {
                return inner.WriteClientState(writeTimestamp, writer, out shouldSendReliably);
            }

            public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
            {
                return inner.WriteServerState(writeTimestamp, writer, out shouldSendReliably);
            }

            public void OnReplaced()
            {
                replaced = true;
            }
        }

        private readonly Dictionary<uint, SentMotionLayer> _sentMotionLayers = new Dictionary<uint, SentMotionLayer>();
        private readonly List<uint> _sentMotionGone = new List<uint>();
        private readonly Stopwatch _sentMotionClock = Stopwatch.StartNew();
        private float _sentMotionNextSweep;

        /// <summary>Right before the kit sends movement: every layered player is put where its client's trajectory says.</summary>
        private void PoseSentPlayerMotion()
        {
            if (!smoothSentPlayerMotion)
            {
                if (_sentMotionLayers.Count > 0)
                    ClearSentMotionLayers();
                return;
            }
            float now = Time.unscaledTime;
            if (now >= _sentMotionNextSweep)
            {
                _sentMotionNextSweep = now + SentMotionSweepSeconds;
                SweepSentMotionLayers();
            }
            if (_sentMotionLayers.Count == 0)
                return;
            MeshMovementHandlerSlots slots = MeshMovementHandlerSlots.For(this);
            double clock = _sentMotionClock.Elapsed.TotalMilliseconds;
            _sentMotionGone.Clear();
            foreach (KeyValuePair<uint, SentMotionLayer> kv in _sentMotionLayers)
            {
                SentMotionLayer layer = kv.Value;
                BaseGameEntity entity = layer.inner.Entity;
                if (layer.replaced || entity == null || !slots.Holds(kv.Key, layer) || !IsSentMotionEntity(entity))
                {
                    _sentMotionGone.Add(kv.Key);
                    continue;
                }
                float delta = layer.lastPoseAt < 0f ? 0f : now - layer.lastPoseAt;
                layer.lastPoseAt = now;
                Transform t = entity.EntityTransform;
                if (entity is BaseCharacterEntity character && (character.IsDead() || !character.PassengingVehicleEntity.IsNull()))
                {
                    layer.track.Reset(); // dead, or a passenger: the seat moves it, and its own states are not where it is
                    layer.hasLead = false;
                    continue;
                }
                Vector3 from = t.position;
                if (layer.hasLead)
                {
                    float lead = now - layer.leadAt;
                    if (lead > SentMotionMaxLeadSeconds)
                        layer.hasLead = false;
                    else
                    {
                        from = layer.leadPosition + layer.leadVelocity * lead;
                        from.y = t.position.y;
                    }
                }
                if (!layer.track.TryPose(clock, delta, from, out Vector3 pose))
                {
                    if (layer.hasLead)
                        t.position = from; // the walk goes on until the client's states can be drawn
                    continue;
                }
                layer.hasLead = false;
                if (!IsFinite(pose) || !((pose - t.position).sqrMagnitude <= SentMotionOffTrack * SentMotionOffTrack))
                {
                    layer.track.Reset(); // the server moved it (a teleport, a respawn): its client's old states are not where it is
                    continue;
                }
                pose.y = t.position.y;
                t.position = pose;
            }
            for (int i = 0; i < _sentMotionGone.Count; ++i)
            {
                uint id = _sentMotionGone[i];
                SentMotionLayer layer = _sentMotionLayers[id];
                _sentMotionLayers.Remove(id);
                if (!layer.replaced)
                    slots.TryRemove(id, layer, out _);
            }
        }

        /// <summary>Puts a layer in front of the kit handler of every client-driven player this cell owns that has none.</summary>
        private void SweepSentMotionLayers()
        {
            if (Assets == null)
                return;
            Dictionary<uint, LiteNetLibIdentity>.Enumerator it = Assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                LiteNetLibIdentity identity = it.Current.Value;
                if (identity == null || identity.ConnectionId < 0)
                    continue;
                if (identity.TryGetComponent(out BasePlayerCharacterEntity _) || identity.TryGetComponent(out VehicleEntity _))
                    TryLayerSentMotion(it.Current.Key);
            }
        }

        /// <summary>
        /// Layers player object <paramref name="id"/> for sent motion, unless it is layered already, is a ghost, has another
        /// layer, or is not moved by its client's transforms. An arrival is layered as it registers, before its owner is set.
        /// </summary>
        private bool TryLayerSentMotion(uint id)
        {
            if (!smoothSentPlayerMotion || Assets == null)
                return false;
            if (_sentMotionLayers.ContainsKey(id))
                return true;
            MeshMovementHandlerSlots slots = MeshMovementHandlerSlots.For(this);
            if (slots.HasLayer(id) || !slots.TryGetKitHandler(id, out IEntityMovementDataHandler handler) || !(handler is BuiltInEntityMovementFunctions3D functions))
                return false;
            if (!IsSentMotionEntity(functions.Entity) || functions.movementSecure != MovementSecure.NotSecure)
                return false;
            SentMotionLayer layer = new SentMotionLayer(functions, new MeshRemoteMotionTrack(sentMotionDelayIntervals, 10.0, 100.0), _sentMotionClock);
            if (!slots.TryInstall(id, layer, true, out _))
                return false;
            _sentMotionLayers.Add(id, layer);
            return true;
        }

        /// <summary>
        /// The arrival walk handed player <paramref name="id"/> back at <paramref name="position"/>, walking at
        /// <paramref name="velocity"/>: that path is sent until its client's states can be drawn, and their track starts from it.
        /// </summary>
        private void LeadSentMotion(uint id, Vector3 position, Vector3 velocity, float now)
        {
            if (!_sentMotionLayers.TryGetValue(id, out SentMotionLayer layer) || layer.track.IsDrawing)
                return;
            layer.hasLead = true;
            layer.leadPosition = position;
            layer.leadVelocity = velocity;
            layer.leadAt = now;
        }

        /// <summary>
        /// IsSentMotionEntity is true for what a client drives on this cell: a player that is not a ghost, or a vehicle whose
        /// driver is one (DESIGN 1.27). A vehicle without a driver, or driven by a puppet, is the cell's or its body's.
        /// </summary>
        private static bool IsSentMotionEntity(BaseGameEntity entity)
        {
            if (entity is BasePlayerCharacterEntity player)
                return !MeshIdentity.IsGhostEntity(player);
            if (!(entity is VehicleEntity vehicle) || !vehicle.HasDriver)
                return false;
            return vehicle.GetPassenger(0) is BasePlayerCharacterEntity driver && !MeshIdentity.IsGhostEntity(driver);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private void ClearSentMotionLayers()
        {
            MeshMovementHandlerSlots slots = MeshMovementHandlerSlots.For(this);
            foreach (KeyValuePair<uint, SentMotionLayer> kv in _sentMotionLayers)
                if (!kv.Value.replaced)
                    slots.TryRemove(kv.Key, kv.Value, out _);
            _sentMotionLayers.Clear();
        }
#else
        private void PoseSentPlayerMotion() { }
        private bool TryLayerSentMotion(uint id) { return false; }
        private void LeadSentMotion(uint id, Vector3 position, Vector3 velocity, float now) { }
#endif
    }
}
