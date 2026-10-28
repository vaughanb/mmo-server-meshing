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

        /// <summary>Destination, as the arrival registers (its puppet just promoted): walk it along its crossing track until its client moves it.</summary>
        private void BeginArrivalWalk(BasePlayerCharacterEntity entity, Vector3 trackOrigin, float trackTime, Vector3 velocity)
        {
            velocity.y = 0f;
            if (!walkArrivals || entity == null || velocity.magnitude < ArrivalWalkMinSpeed)
                return;
            float now = Time.unscaledTime;
            Vector3 position = entity.EntityTransform.position;
            Vector3 onTrack = TrackAt(trackOrigin, trackTime, velocity, now);
            Vector3 ease = position - onTrack;
            ease.y = 0f;
            _arrivalWalks.Add(new ArrivalWalk { entity = entity, name = entity.CharacterName, trackOrigin = trackOrigin, trackTime = trackTime, velocity = velocity, ease = ease, since = now, written = position });
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
                    Stamp($"arrival {walk.name}: walked {(now - walk.since) * 1000f:0} ms at {walk.velocity.magnitude:0.0} m/s, then its client's state moved it {moved.magnitude:0.###} m ({offTrack.magnitude:0.###} m off the crossing track)");
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
        private void ArrivalWalkUpdate() { }
        private void SeedDemotedPuppet(BasePlayerCharacterEntity entity, Vector3 velocity) { }
#endif
    }
}
