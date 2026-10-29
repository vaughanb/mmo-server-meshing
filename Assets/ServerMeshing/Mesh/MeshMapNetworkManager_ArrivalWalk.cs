// Server Meshing add-on — a crossing character keeps walking for everyone watching it (DESIGN.md 1.09).
//
// While a client hops, no cell gets its movement: its character on the destination, a puppet promoted when the client
// registers, stands until the client's first state after the adoption, and its demoted copy on the origin stands until
// the destination's first snapshot of it. A player standing at the line saw a character walking past stop for 50-150 ms
// at its crossing and then lunge after it (12-16 m/s for a few frames), every time.
//
// Destination: the origin sends where the character was and how fast it walked at the crossing with the handoff state
// (the row's position, led by the velocity, and the velocity). When the arrival registers, its character follows that
// track (where the client is walking, by the origin's last reckoning) until the kit moves it with the client's first
// state, starting from where its puppet stood and easing onto the track (the puppet trails it: its snapshots were old
// and stopped at the crossing). Capped at arrivalWalkMaxSeconds after the crossing. Origin: the demoted copy's kinematic
// puppet body starts at the demotion from where the character is and that velocity, instead of standing until the
// destination's first snapshot (Kinematic puppet motion; a steered puppet keeps waiting for the snapshot).
//
// DESIGN 1.18: a snapshot's velocity is the move since the cell's last snapshot of that character, and the destination
// has none of an arrival, so its first snapshot said 0 m/s. The origin's puppet took it for a stop and drifted back
// (about 6 cm over the 100 ms to the next snapshot), then lunged after the next one at 8-12 m/s, at every crossing, for
// everyone on the origin's side. The arrival's first snapshot now carries its crossing velocity instead.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Destination: walk an arriving character on along its crossing track until its client's first state (DESIGN 1.09). Off: it stands through its client's gap.")]
        public bool walkArrivals = true;

        [Tooltip("Destination: the longest after the crossing an arriving character is walked on without its client's state, in seconds; past it, it holds.")]
        public float arrivalWalkMaxSeconds = 0.5f;

        [Tooltip("Destination: how fast an arriving character's puppet pose eases onto its crossing track (the time constant, seconds).")]
        public float arrivalWalkEaseSeconds = 0.1f;

        [Tooltip("Destination: how far ahead of the origin's last reckoning the crossing track runs, in seconds of the crossing velocity: the client is ahead of what its server last heard (about 0.1 m at 4 m/s on one machine, DESIGN 1.11).")]
        public float arrivalTrackLeadSeconds = 0.025f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private sealed class ArrivalWalk
        {
            public BasePlayerCharacterEntity entity;
            public string name;
            public Vector3 trackOrigin;  // where the character was at trackTime, by the origin
            public float trackTime;      // local time of the crossing (the handoff state's arrival)
            public Vector3 velocity;
            public Vector3 ease;         // the puppet's pose minus the track at the start, eased away
            public float since;
            public Vector3 written;
        }

        private const float ArrivalWalkStill = 0.0005f;
        private const float ArrivalWalkMinSpeed = 0.3f;
        private readonly List<ArrivalWalk> _arrivalWalks = new List<ArrivalWalk>();
        private readonly Dictionary<string, Vector3> _arrivalSnapshotVelocity = new Dictionary<string, Vector3>();

        /// <summary>Destination, as the arrival registers (its puppet just promoted): walk it along its crossing track until its client moves it.</summary>
        private void BeginArrivalWalk(BasePlayerCharacterEntity entity, Vector3 trackOrigin, float trackTime, Vector3 velocity)
        {
            velocity.y = 0f;
            if (entity != null)
                _arrivalSnapshotVelocity[OwnedKeyOf(entity)] = velocity;
            if (!walkArrivals || entity == null || velocity.magnitude < ArrivalWalkMinSpeed)
                return;
            float now = Time.unscaledTime;
            Vector3 position = entity.EntityTransform.position;
            Vector3 onTrack = TrackAt(trackOrigin, trackTime, velocity, now);
            Vector3 ease = position - onTrack;
            ease.y = 0f;
            _arrivalWalks.Add(new ArrivalWalk { entity = entity, name = entity.CharacterName, trackOrigin = trackOrigin, trackTime = trackTime, velocity = velocity, ease = ease, since = now, written = position });
        }

        /// <summary>
        /// The velocity for a character's snapshot when this cell has no earlier snapshot of it to measure from: an arrival's
        /// crossing velocity, once (DESIGN 1.18). False for anything else.
        /// </summary>
        private bool TryTakeArrivalSnapshotVelocity(string meshId, out Vector3 velocity)
        {
            if (_arrivalSnapshotVelocity.Count == 0 || !_arrivalSnapshotVelocity.TryGetValue(meshId, out velocity))
            {
                velocity = Vector3.zero;
                return false;
            }
            _arrivalSnapshotVelocity.Remove(meshId);
            return true;
        }

        private Vector3 TrackAt(Vector3 origin, float time, Vector3 velocity, float now)
        {
            return origin + velocity * Mathf.Clamp(now - time, 0f, arrivalWalkMaxSeconds);
        }

        /// <summary>Every server frame, after the frame's network input: carry each arrival on, or hand it back to the kit.</summary>
        private void ArrivalWalkUpdate()
        {
            if (_arrivalWalks.Count == 0)
                return;
            float now = Time.unscaledTime;
            for (int i = _arrivalWalks.Count - 1; i >= 0; --i)
            {
                ArrivalWalk walk = _arrivalWalks[i];
                if (walk.entity == null || walk.entity.Identity == null || walk.entity.Identity.IsDestroyed || walk.entity.IsDead())
                {
                    _arrivalWalks.RemoveAt(i);
                    continue;
                }
                Transform t = walk.entity.EntityTransform;
                Vector3 onTrack = TrackAt(walk.trackOrigin, walk.trackTime, walk.velocity, now);
                Vector3 moved = t.position - walk.written;
                moved.y = 0f;
                if (moved.magnitude > ArrivalWalkStill)
                {
                    // The kit moved it: the client's first state. What it corrected is what an observer sees jump.
                    Vector3 offTrack = t.position - onTrack;
                    offTrack.y = 0f;
                    Vector3 along = walk.velocity.normalized;
                    float ahead = Vector3.Dot(offTrack, along);                                 // + the client is ahead of the track
                    float aside = Vector3.Cross(along, offTrack).y;
                    Stamp($"arrival {walk.name}: walked {(now - walk.since) * 1000f:0} ms at {walk.velocity.magnitude:0.0} m/s, then its client's state moved it {moved.magnitude:0.###} m ({offTrack.magnitude:0.###} m off the crossing track: {ahead:+0.###;-0.###} m along, {aside:+0.###;-0.###} m aside, {(now - walk.trackTime) * 1000f:0} ms after the crossing)");
                    _arrivalWalks.RemoveAt(i);
                    continue;
                }
                Vector3 target = onTrack + walk.ease * Mathf.Exp(-(now - walk.since) / Mathf.Max(0.01f, arrivalWalkEaseSeconds));
                target.y = t.position.y;
                t.position = target;
                walk.written = target;
            }
        }

        /// <summary>
        /// Destination, as the handoff state arrives (DESIGN 1.11): the crossing character's kinematic puppet here takes the
        /// crossing track as its sample, so its track's own blend carries it onto the track before the arrival registers. The
        /// walk then starts from a pose near the track instead of one that trailed it by ~0.5 m and was still easing off when
        /// the client's first state came (most of the 0.12-0.66 m step).
        /// </summary>
        private void SteerArrivingPuppet(string characterId, Vector3 trackOrigin, Vector3 velocity)
        {
            if (!walkArrivals || puppetMotion != MeshPuppetMotion.Kinematic || string.IsNullOrEmpty(characterId)
                || !_ghosts.TryGetValue(characterId, out MeshGhost ghost) || ghost.body == null || ghost.entity == null)
                return;
            velocity.y = 0f;
            if (velocity.magnitude < ArrivalWalkMinSpeed)
                return;
            BaseCharacterEntity entity = ghost.entity;
            GhostSnapshotMessage track = new GhostSnapshotMessage
            {
                kind = GhostKind.Player,
                characterId = characterId,
                position = trackOrigin,
                yaw = entity.EntityTransform.eulerAngles.y,
                velocity = velocity,
                movementState = (byte)entity.MovementState,
                extraMovementState = (byte)entity.ExtraMovementState,
            };
            FeedKinematicPuppet(ghost, track, Vector3.Distance(ghost.body.Pose(Time.unscaledTime), trackOrigin), true);
        }

        /// <summary>
        /// Origin, at the demotion: the puppet's kinematic body starts from where the character stands and its crossing
        /// velocity, as a snapshot would start it, so the copy here walks on until the destination's snapshots arrive.
        /// </summary>
        private void SeedDemotedPuppet(BasePlayerCharacterEntity entity, Vector3 velocity)
        {
            if (puppetMotion != MeshPuppetMotion.Kinematic || entity == null || !_ghosts.TryGetValue(entity.Id, out MeshGhost ghost))
                return;
            velocity.y = 0f;
            Transform t = entity.EntityTransform;
            GhostSnapshotMessage start = new GhostSnapshotMessage
            {
                kind = GhostKind.Player,
                characterId = entity.Id,
                position = t.position,
                yaw = t.eulerAngles.y,
                velocity = velocity,
                movementState = (byte)entity.MovementState,
                extraMovementState = (byte)entity.ExtraMovementState,
            };
            EnsurePuppetMotion(ghost, start);
            if (ghost.body != null)
                FeedKinematicPuppet(ghost, start, 0f, velocity.sqrMagnitude > 0.04f);
        }
#else
        private void BeginArrivalWalk(BasePlayerCharacterEntity entity, Vector3 trackOrigin, float trackTime, Vector3 velocity) { }
        private void SteerArrivingPuppet(string characterId, Vector3 trackOrigin, Vector3 velocity) { }
        private void ArrivalWalkUpdate() { }
        private void SeedDemotedPuppet(BasePlayerCharacterEntity entity, Vector3 velocity) { }
#endif
    }
}
