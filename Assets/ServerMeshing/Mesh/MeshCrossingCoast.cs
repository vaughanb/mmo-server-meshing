// Server Meshing add-on — a crossing character's puppet walks on until real data reaches it (DESIGN.md 1.35).
//
// A kinematic puppet coasts 0.3 s past its last sample and stands. From the crossing until the arrival registers nothing
// feeds the destination's puppet, and until the destination's first snapshot nothing feeds the origin's demoted copy: on a
// 200 ms round trip that outlasts the coast on every hop, and a lost packet does at any latency, so a watcher on either
// side saw it stand and then catch up. This feeds the puppet the crossing track at the snapshot rate meanwhile.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshCrossingCoast says when a coasting puppet is due its next sample, and where on the crossing track that is.</summary>
    public sealed class MeshCrossingCoast
    {
        private readonly Vector3 _origin;
        private readonly float _startedAt;
        private readonly float _maxSeconds;
        private readonly float _intervalSeconds;
        private float _nextFeed;

        /// <summary>MeshCrossingCoast starts at <paramref name="origin"/> at <paramref name="startedAt"/>, where the first sample went, and ends <paramref name="maxSeconds"/> later, or at a snapshot when <paramref name="endsOnSnapshot"/>.</summary>
        public MeshCrossingCoast(Vector3 origin, Vector3 velocity, float startedAt, float maxSeconds, float intervalSeconds, bool endsOnSnapshot)
        {
            EndsOnSnapshot = endsOnSnapshot;
            _origin = origin;
            Velocity = velocity;
            _startedAt = startedAt;
            _maxSeconds = maxSeconds;
            _intervalSeconds = intervalSeconds;
            _nextFeed = startedAt + intervalSeconds;
        }

        /// <summary>OnGround is true when the kit found ground under the start, and AboveGround its height over that: the coast keeps it.</summary>
        public bool OnGround { get; set; }

        public float AboveGround { get; set; }

        /// <summary>EndsOnSnapshot is true on the origin, where the destination's first snapshot is real data.</summary>
        public bool EndsOnSnapshot { get; }

        /// <summary>Velocity is the crossing velocity.</summary>
        public Vector3 Velocity { get; }

        /// <summary>Ended is true once the coast has run its time: past it, the puppet stands.</summary>
        public bool Ended(float now)
        {
            return now - _startedAt >= _maxSeconds;
        }

        /// <summary>TryFeed is true when a sample is due, with where it goes.</summary>
        public bool TryFeed(float now, out Vector3 position)
        {
            position = _origin + Velocity * Mathf.Clamp(now - _startedAt, 0f, _maxSeconds);
            if (now < _nextFeed)
                return false;
            _nextFeed = now + _intervalSeconds;
            return true;
        }
    }

    /// <summary>
    /// MeshCrossingVelocity is a player's velocity as its cell tracks it for a crossing, when it has no stamped states to
    /// read it from (MeshRemoteMotionTrack.TryGetVelocity, the better source). A client's states come in bunches
    /// on a jittery connection, so one frame's movement says little: halving towards each frame's read a 4 m/s walker as
    /// 3.6 to 7 m/s at the crossing, and the arrival's track ran off by up to 1.9 m. A frame without a new state does not
    /// count, and each sample is weighted by the time it spans. Past <see cref="StandSeconds"/> without one the velocity
    /// falls, but the next state still measures from the last: taking the hole for a stop read the catch-up as a teleport,
    /// and a character that crossed in it went over at 0.09 m/s, with no walk.
    /// </summary>
    public struct MeshCrossingVelocity
    {
        /// <summary>StandSeconds is how long without a new position means the character may have stopped, not that a state is late.</summary>
        public const float StandSeconds = 0.25f;

        /// <summary>MoveSpeedMargin is how far over its move speed a crossing is believed: a little, for what the kit does not count.</summary>
        public const float MoveSpeedMargin = 1.15f;

        private const float MinSpanSeconds = 0.0001f;

        public Vector3 position;
        public Vector3 velocity;
        public float time;
        private float _lookedAt;

        /// <summary>
        /// Capped is <paramref name="velocity"/> at no more than <see cref="MoveSpeedMargin"/> of what the character can move
        /// at: states that come late still read fast now and then, and a track 4 m/s too fast is 3 m off after a second.
        /// A speed of zero or less says nothing, so nothing is capped.
        /// </summary>
        public static Vector3 Capped(Vector3 velocity, float moveSpeed)
        {
            return moveSpeed > 0f ? Vector3.ClampMagnitude(velocity, moveSpeed * MoveSpeedMargin) : velocity;
        }

        /// <summary>Next is this with the character at <paramref name="newPosition"/> at <paramref name="now"/>. A speed over <paramref name="maxSpeed"/> counts as standing.</summary>
        public MeshCrossingVelocity Next(Vector3 newPosition, float now, float maxSpeed, float timeConstantSeconds)
        {
            float span = now - time;
            if (span <= MinSpanSeconds)
                return this;
            float timeConstant = Mathf.Max(MinSpanSeconds, timeConstantSeconds);
            MeshCrossingVelocity next = this;
            next._lookedAt = now;
            if (newPosition == position)
            {
                if (span >= StandSeconds)
                    next.velocity = Vector3.Lerp(velocity, Vector3.zero, 1f - Mathf.Exp(-Mathf.Max(0f, now - _lookedAt) / timeConstant));
                return next;
            }
            Vector3 sample = (newPosition - position) / span;
            if (sample.magnitude > maxSpeed)
                sample = Vector3.zero;
            next.position = newPosition;
            next.velocity = Vector3.Lerp(velocity, sample, 1f - Mathf.Exp(-span / timeConstant));
            next.time = now;
            return next;
        }
    }
}
