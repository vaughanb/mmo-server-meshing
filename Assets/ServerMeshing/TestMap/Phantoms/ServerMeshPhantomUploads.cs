// Server Meshing add-on — rig phantom feeder: walks every virtual character phantom and queues its uploads into the phantom transport before the kit drains the server.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomUploads is the virtual feeder. Every frame, before the kit's manager drains the server (a negative
    /// execution order), it samples posErr for a rotating tenth of the virtual character phantoms, walks each one inside its
    /// region with ServerMeshKinematicWalk.StepWithin at the character's move speed, and queues at most one upload per
    /// phantom when its seeded schedule is due: [119][packed long ts][packed uint object id][payload], ts = max(last + 1,
    /// ServerTimestamp − 50). A server teleport is confirmed once per destination, as a real client does. Sink-bound
    /// phantoms are fed by their sink, never here. The pure parts are public statics.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ServerMeshPhantomUploads : MonoBehaviour
    {
        /// <summary>TimestampDelayMs is how far behind the server clock a fresh upload's timestamp sits.</summary>
        public const long TimestampDelayMs = 50;
        /// <summary>MinTurnSeconds is the shortest time a phantom walks one heading.</summary>
        public const float MinTurnSeconds = 2f;
        /// <summary>TurnSpreadSeconds is the random part of a heading's time.</summary>
        public const float TurnSpreadSeconds = 4f;
        /// <summary>SameDestinationMetres is how close two teleport destinations are to count as the same teleport.</summary>
        public const float SameDestinationMetres = 0.01f;

        private readonly NetDataWriter _writer = new NetDataWriter(true, 64);
        private int _sampleCursor;

        internal List<ServerMeshPhantomSlot> Slots;
        internal ServerMeshPhantomTransport Transport;
        internal MeshMapNetworkManager Mesh;
        internal ServerMeshPhantomWindow Window;
        internal IServerMeshPhantomPayload Payload;
        internal IMeshLog Log;
        internal double Period = 1.0 / ServerMeshPhantomSettings.DefaultUploadHz;

        /// <summary>NextTimestamp is an upload's timestamp: max(last + 1, serverTimestampMs − 50), so it rises strictly whatever the clock does.</summary>
        public static long NextTimestamp(long last, long serverTimestampMs)
        {
            long delayed = serverTimestampMs - TimestampDelayMs;
            return delayed > last ? delayed : last + 1;
        }

        /// <summary>FirstDue is a phantom's first due time: now plus its seeded phase (0 to 1) of a period.</summary>
        public static double FirstDue(double now, double period, double phase) => now + period * phase;

        /// <summary>
        /// TakeDue is true when an upload is due at now, at most once per call: it then moves nextDue on by one period, and
        /// past now by whole periods when a stall left it behind, so a late phantom skips to its next slot with no burst.
        /// </summary>
        public static bool TakeDue(double now, double period, ref double nextDue)
        {
            if (now < nextDue)
                return false;
            nextDue += period;
            if (nextDue <= now)
                nextDue += period * System.Math.Floor((now - nextDue) / period + 1.0);
            return true;
        }

        /// <summary>
        /// ShouldConfirmTeleport is true once per server teleport destination: the first time a wait shows, and again when a
        /// wait shows a new destination; it remembers the destination, and forgets it once the wait clears.
        /// </summary>
        public static bool ShouldConfirmTeleport(bool waiting, Vector3 serverPosition, ref bool confirmed, ref Vector3 destination)
        {
            if (!waiting)
            {
                confirmed = false;
                return false;
            }
            if (confirmed && (serverPosition - destination).sqrMagnitude <= SameDestinationMetres * SameDestinationMetres)
                return false;
            confirmed = true;
            destination = serverPosition;
            return true;
        }

        /// <summary>
        /// Step moves position by distance: inside region with ServerMeshKinematicWalk.StepWithin (reflecting off its edges),
        /// or straight towards region's nearest point when it starts outside (after a teleport), never further than distance.
        /// </summary>
        public static Vector3 Step(Vector3 position, ref Vector2 heading, float distance, Rect region)
        {
            if (position.x >= region.xMin && position.x <= region.xMax && position.z >= region.yMin && position.z <= region.yMax)
                return ServerMeshKinematicWalk.StepWithin(position, ref heading, distance, region);
            Vector3 target = new Vector3(Mathf.Clamp(position.x, region.xMin, region.xMax), position.y, Mathf.Clamp(position.z, region.yMin, region.yMax));
            return Vector3.MoveTowards(position, target, distance);
        }

        /// <summary>YawOf is the yaw in degrees (0 to 360) of a heading (x, z).</summary>
        public static float YawOf(Vector2 heading) => Mathf.Repeat(Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg, 360f);

        /// <summary>WriteFrame resets writer and writes an upload's header: [packed ushort 119][packed long ts][packed uint object id].</summary>
        public static void WriteFrame(NetDataWriter writer, long timestamp, uint objectId)
        {
            TransportHandler.WritePacket(writer, GameNetworkingConsts.EntityState);
            writer.PutPackedLong(timestamp);
            writer.PutPackedUInt(objectId);
        }

        /// <summary>Unit is a deterministic number from 0 to 1 for (seed, index, salt): seeded phases and share draws.</summary>
        public static double Unit(int seed, int index, int salt)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u ^ (uint)index * 2246822519u ^ (uint)salt * 3266489917u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return h / 4294967296.0;
            }
        }

        /// <summary>InShare is true for the share (0 to 1) of indices a seed picks for a salt.</summary>
        public static bool InShare(int seed, int index, int salt, float share) => share > 0f && Unit(seed, index, salt) < share;

        private void Update()
        {
            if (Slots == null || Transport == null || Mesh == null || Window == null || Payload == null)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Time.unscaledTimeAsDouble;
            float deltaTime = Time.unscaledDeltaTime;
            int frame = Time.frameCount;
            SamplePositionError(frame);
            long serverTimestamp = Mesh.ServerTimestamp;
            for (int i = 0; i < Slots.Count; ++i)
            {
                ServerMeshPhantomSlot slot = Slots[i];
                if (!slot.FedHere || slot.entity == null || slot.entity.IsDestroyed)
                    continue;
                Feed(slot, now, deltaTime, frame, serverTimestamp);
            }
            Window.FeederMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void Feed(ServerMeshPhantomSlot slot, double now, float deltaTime, int frame, long serverTimestamp)
        {
            BasePlayerCharacterEntity entity = slot.entity;
            Transform entityTransform = entity.EntityTransform;
            if (ShouldConfirmTeleport(entity.IsWaitingClientTeleportConfirm(), entityTransform.position, ref slot.teleportConfirmed, ref slot.teleportDestination))
            {
                slot.teleportPending = true;
                Vector3 destination = slot.teleportDestination;
                slot.walkPosition = new Vector3(destination.x, slot.walkPosition.y, destination.z);
                Rect region = slot.region;
                if (!slot.outsideWarned && (destination.x < region.xMin || destination.x > region.xMax || destination.z < region.yMin || destination.z > region.yMax))
                {
                    slot.outsideWarned = true;
                    Log.Warning(System.FormattableString.Invariant(
                        $"Phantoms: phantom {slot.index} was teleported to {destination.x:0.0} {destination.z:0.0}, outside its region; it walks back in at move speed"));
                }
            }
            else if (slot.fighting && !slot.teleportPending && Chase(slot, entity, now, deltaTime))
            {
            }
            else if (!slot.standing && !slot.teleportPending)
            {
                if (now >= slot.nextTurn)
                    Turn(slot, now);
                slot.walkPosition = Step(slot.walkPosition, ref slot.heading, entity.GetMoveSpeed() * deltaTime, slot.region);
            }
            if (!TakeDue(now, Period, ref slot.nextDue))
                return;
            bool teleport = slot.teleportPending;
            Vector3 position = teleport ? slot.teleportDestination : slot.walkPosition;
            float yaw = teleport ? entityTransform.eulerAngles.y : YawOf(slot.heading);
            long timestamp = NextTimestamp(slot.lastTimestamp, serverTimestamp);
            WriteFrame(_writer, timestamp, slot.objectId);
            if (!Payload.TryWrite(_writer, (!slot.standing || slot.chasing) && !teleport, teleport, position, yaw, ref slot.stopSent, out _))
                return;
            if (!Transport.QueueUpload(slot.connectionId, _writer))
                return;
            slot.teleportPending = false;
            slot.lastTimestamp = timestamp;
            slot.lastQueuedPosition = position;
            slot.lastQueuedFrame = frame;
            ++Window.Uploads;
        }

        private void SamplePositionError(int frame)
        {
            int count = Slots.Count;
            int samples = ServerMeshPhantomWindow.SampleSize(count);
            for (int n = 0; n < samples; ++n)
            {
                if (_sampleCursor >= count)
                    _sampleCursor = 0;
                ServerMeshPhantomSlot slot = Slots[_sampleCursor++];
                if (!slot.FedHere || slot.entity == null || slot.entity.IsDestroyed || slot.lastQueuedFrame < 0 || slot.lastQueuedFrame >= frame || slot.entity.IsWaitingClientTeleportConfirm())
                    continue;
                Vector3 at = slot.entity.EntityTransform.position;
                float dx = at.x - slot.lastQueuedPosition.x, dz = at.z - slot.lastQueuedPosition.z;
                Window.AddPosErr(Mathf.Sqrt(dx * dx + dz * dz));
            }
        }

        /// <summary>ChaseLookSeconds is how often a fighting phantom looks again for the nearest monster in reach.</summary>
        public const double ChaseLookSeconds = 0.25;
        /// <summary>ChaseStopDistance is how close a chasing phantom walks to its monster: inside melee reach.</summary>
        public const float ChaseStopDistance = 1.5f;

        // A fighting phantom chases as a real player does: its uploaded walk steers toward the nearest monster within the
        // fight reach, inside its region, so the server-side fighter finds it in melee reach. False when there is none.
        private static bool Chase(ServerMeshPhantomSlot slot, BasePlayerCharacterEntity entity, double now, float deltaTime)
        {
            if (now >= slot.nextChaseLook)
            {
                slot.nextChaseLook = now + ChaseLookSeconds;
                slot.chaseTarget = SyntheticPlayerBrain.FindMonsterInReach(entity, entity.EntityTransform.position, out _);
            }
            BaseCharacterEntity target = slot.chaseTarget;
            if (target == null || target.IsDestroyed || target.IsDead())
            {
                slot.chasing = false;
                return false;
            }
            Vector3 at = target.EntityTransform.position;
            Vector2 toTarget = new Vector2(at.x - slot.walkPosition.x, at.z - slot.walkPosition.z);
            float distance = toTarget.magnitude;
            slot.chasing = distance > ChaseStopDistance;
            if (slot.chasing)
            {
                slot.heading = toTarget / distance;
                slot.walkPosition = Step(slot.walkPosition, ref slot.heading, Mathf.Min(entity.GetMoveSpeed() * deltaTime, distance - ChaseStopDistance), slot.region);
            }
            return true;
        }

        private static void Turn(ServerMeshPhantomSlot slot, double now)
        {
            float angle = (float)slot.random.NextDouble() * Mathf.PI * 2f;
            slot.heading = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            slot.nextTurn = now + MinTurnSeconds + slot.random.NextDouble() * TurnSpreadSeconds;
        }

        /// <summary>Clear forgets the binder's state (the server stopped).</summary>
        internal void Clear()
        {
            Slots = null;
            Transport = null;
            Mesh = null;
            _sampleCursor = 0;
        }
    }
}
