// Server Meshing add-on — every remote character on this client drawn through a MeshRemoteMotionTrack (DESIGN.md 1.17).
//
// A layer in each remote character's movement-handler slot reads the position and timestamp of every state before the
// kit's own handler applies it; once a frame, after the kit's update, the character is put where its track says. The
// kit still applies every state (its rotation, animation states, jumps and teleports are untouched); only the position
// it interpolated is drawn over. The buffer stands aside while this client hops (the kept extrapolator walks the kept
// characters then, DESIGN 1.08), for a teleport, and for a character the kit snaps, and starts again from two states.
// Only the kit's 3D character controller handler is layered; anything else is drawn by the kit as before.
// DESIGN 1.27: a remote vehicle is layered too, and a passenger is not drawn (its seat carries it: the buffer drew it from
// its own states while the kit snapped it to the seat of a vehicle the kit drew, two pictures of one rider). A drawn
// vehicle's passengers are put on their seats at once: the kit's own seat snap can run before the buffer in the frame,
// and it left the rider on the kit's picture of the vehicle, up to ~0.4 m off the drawn one and jumping between them.
//
// With MESH_TRACE_OBSERVED=1 it also logs (tag MeshRemote) what the observed trace cannot tell apart: a layer put in
// or lost, a restart and its reason, the first pose after one, a gap in a character's states and a stand in its
// stamped positions, each with the time and where.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    internal sealed class MeshRemoteMotionBuffer
    {
        /// <summary>MESH_REMOTE_BUFFER=0 turns the buffer off for this process (to compare against the kit's drawing).</summary>
        public static readonly bool DisabledByEnvironment = System.Environment.GetEnvironmentVariable("MESH_REMOTE_BUFFER") == "0";

        private const float SweepSeconds = 0.25f;

        private sealed class Layer : IEntityMovementDataHandler, IMeshReplaceableHandlerLayer
        {
            public readonly BuiltInEntityMovementFunctions3D inner;
            public readonly MeshRemoteMotionTrack track;
            public readonly uint objectId;
            public bool replaced;
            private readonly Stopwatch _clock;
            private double _lastStamp = double.NaN, _standSince = double.NaN;
            private Vector3 _lastPosition;

            public Layer(BuiltInEntityMovementFunctions3D inner, uint objectId, MeshRemoteMotionTrack track, Stopwatch clock)
            {
                this.inner = inner;
                this.objectId = objectId;
                this.track = track;
                _clock = clock;
            }

            public uint ObjectId => inner.ObjectId;
            public long ConnectionId => inner.ConnectionId;

            public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
            {
                return inner.CreateMovementData(out forceAppliers);
            }

            public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
            {
                inner.ReadClientStateAtServer(peerTimestamp, reader);
            }

            /// <summary>Keeps the state's position and stamp, then lets the kit's handler read the same bytes.</summary>
            public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
            {
                int start = reader.Position;
                reader.ClientReadSyncTransformMessage3D(out MovementState movementState, out _, out Vector3 position, out _, out _);
                reader.SetPosition(start);
                if (!inner.IsPreparingToTeleport)
                {
                    bool snap = movementState.Has(MovementState.IsTeleport)
                        || (track.Count > 0 && Vector3.Distance(position, inner.Entity.EntityTransform.position) >= inner.snapThreshold);
                    if (Trace)
                        TraceSample(peerTimestamp, position, movementState.Has(MovementState.IsTeleport), snap);
                    track.Add(peerTimestamp, _clock.Elapsed.TotalMilliseconds, position, snap);
                }
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

            /// <summary>The trace's view of one state: a gap since the last, a stand in the stamped positions, a snap.</summary>
            private void TraceSample(double stamp, Vector3 position, bool teleport, bool snap)
            {
                if (snap)
                    Log(inner.Entity, teleport ? "restart: teleport" : $"restart: snap of {Vector3.Distance(position, inner.Entity.EntityTransform.position):0.00} m", position);
                if (!double.IsNaN(_lastStamp))
                {
                    double gap = stamp - _lastStamp;
                    if (gap > 40.0)
                        Log(inner.Entity, $"no state for {gap:0} ms", position);
                    if (Flat(position - _lastPosition) < 0.002f)
                    {
                        if (double.IsNaN(_standSince))
                            _standSince = _lastStamp;
                    }
                    else
                    {
                        if (!double.IsNaN(_standSince) && _lastStamp - _standSince >= 25.0)
                            Log(inner.Entity, $"states stood {_lastStamp - _standSince:0} ms, then {Flat(position - _lastPosition) / Math.Max(1.0, gap) * 1000.0:0.0} m/s", _lastPosition);
                        _standSince = double.NaN;
                    }
                }
                _lastStamp = stamp;
                _lastPosition = position;
            }
        }

        private readonly BaseGameNetworkManager _manager;
        private readonly MeshMovementHandlerSlots _slots;
        private readonly Dictionary<uint, Layer> _layers = new Dictionary<uint, Layer>();
        private readonly List<uint> _gone = new List<uint>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly double _delayIntervals;
        private readonly double _maxExtrapolationMilliseconds;
        private float _nextSweep;

        private static readonly bool Trace = MeshObservedMotionTrace.Enabled;

        private static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        private static void Log(BaseGameEntity entity, string what, Vector3 at)
        {
            string name = entity is BaseCharacterEntity character && !string.IsNullOrEmpty(character.CharacterName) ? character.CharacterName : entity != null ? entity.name : "?";
            Logging.Log("MeshRemote", $"{System.DateTime.Now:HH:mm:ss.fff} {name} {what} at ({at.x:0.0}, {at.z:0.0})");
        }

        public MeshRemoteMotionBuffer(BaseGameNetworkManager manager, double delayIntervals, double maxExtrapolationMilliseconds)
        {
            _manager = manager;
            _slots = MeshMovementHandlerSlots.For(manager);
            _delayIntervals = delayIntervals;
            _maxExtrapolationMilliseconds = maxExtrapolationMilliseconds;
        }

        /// <summary>Drawn is the number of characters drawn by the buffer in the last frame.</summary>
        public int Drawn { get; private set; }

        /// <summary>One frame, after the kit's update. <paramref name="standAside"/>: this client is hopping.</summary>
        public void Frame(bool standAside)
        {
            if (Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + SweepSeconds;
                Sweep();
            }
            double now = _clock.Elapsed.TotalMilliseconds;
            float delta = Time.unscaledDeltaTime;
            int drawn = 0;
            _gone.Clear();
            foreach (KeyValuePair<uint, Layer> kv in _layers)
            {
                Layer layer = kv.Value;
                BaseGameEntity entity = layer.inner.Entity;
                if (layer.replaced || entity == null || !_slots.Holds(kv.Key, layer))
                {
                    if (Trace && entity != null)
                        Log(entity, layer.replaced ? "layer replaced" : "layer lost (the kit rewrote its slot)", entity.EntityTransform.position);
                    _gone.Add(kv.Key);
                    continue;
                }
                if (!entity.PassengingVehicleEntity.IsNull())
                {
                    layer.track.Reset(); // the seat carries it
                    continue;
                }
                if (standAside || layer.inner.IsPreparingToTeleport || entity.IsOwnerClient)
                {
                    if (Trace && layer.track.IsDrawing && !standAside)
                        Log(entity, layer.inner.IsPreparingToTeleport ? "restart: preparing to teleport" : "restart: owned here", entity.EntityTransform.position);
                    layer.track.Reset();
                    continue;
                }
                bool wasDrawing = layer.track.IsDrawing;
                if (layer.track.TryPose(now, delta, entity.EntityTransform.position, out Vector3 pose))
                {
                    if (Trace && !wasDrawing)
                        Log(entity, "drawn by the buffer", pose);
                    layer.inner.EntityMovement.SetPosition(pose);
                    if (entity is VehicleEntity vehicle)
                        SeatPassengers(vehicle);
                    drawn++;
                }
            }
            for (int i = 0; i < _gone.Count; ++i)
            {
                uint id = _gone[i];
                Layer layer = _layers[id];
                _layers.Remove(id);
                if (!layer.replaced)
                    _slots.TryRemove(id, layer, out _);
            }
            Drawn = drawn;
        }

        /// <summary>Puts a layer in front of every remote character's kit handler that has none.</summary>
        private void Sweep()
        {
            Dictionary<uint, LiteNetLibIdentity>.Enumerator it = _manager.Assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                uint id = it.Current.Key;
                if (_layers.ContainsKey(id) || _slots.HasLayer(id))
                    continue;
                if (!_slots.TryGetKitHandler(id, out IEntityMovementDataHandler handler) || !(handler is BuiltInEntityMovementFunctions3D functions))
                    continue;
                if (!(functions.Entity is BaseCharacterEntity || functions.Entity is VehicleEntity) || functions.Entity.IsOwnerClient || functions.Entity.IsServer)
                    continue;
                Layer layer = new Layer(functions, id, new MeshRemoteMotionTrack(_delayIntervals, 20.0, _maxExtrapolationMilliseconds), _clock);
                if (_slots.TryInstall(id, layer, true, out _))
                {
                    _layers.Add(id, layer);
                    if (Trace)
                        Log(functions.Entity, "layered", functions.Entity.EntityTransform.position);
                }
            }
        }

        /// <summary>Puts every passenger of a drawn vehicle on its seat, as the kit's late-update seat snap does.</summary>
        private static void SeatPassengers(VehicleEntity vehicle)
        {
            if (vehicle.Seats.Count == 0)
                return;
            List<BaseGameEntity> passengers = vehicle.GetAllPassengers();
            for (int i = 0; i < passengers.Count; ++i)
            {
                BaseGameEntity passenger = passengers[i];
                VehicleSeat seat = passenger.PassengingVehicleSeat;
                if (seat == null)
                    continue;
                Transform from = seat.passengingTransform != null ? seat.passengingTransform : vehicle.EntityTransform;
                passenger.EntityTransform.SetPositionAndRotation(from.position, from.rotation);
            }
        }

        /// <summary>Hands every slot back to the kit (the client stopped or the buffer was turned off).</summary>
        public void Clear()
        {
            foreach (KeyValuePair<uint, Layer> kv in _layers)
                if (!kv.Value.replaced)
                    _slots.TryRemove(kv.Key, kv.Value, out _);
            _layers.Clear();
        }
    }
}
