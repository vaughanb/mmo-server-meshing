// Server Meshing add-on — the pose track a kinematic puppet body follows: the owner's latest snapshot extrapolated by its
// velocity, plus an error offset that decays, so a new snapshot never makes the puppet jump and it never trails.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshKinematicTrack holds the latest accepted snapshot of a puppet's owner (position, velocity, yaw and the local time
    /// it arrived) and turns it into a pose that is a function of time alone. Predict extrapolates the snapshot by its
    /// velocity for its age plus a lead, capped at the maximum extrapolation. Pose adds an offset: when a snapshot is
    /// accepted, the offset takes the difference between the pose the puppet was showing and the new snapshot, and it then
    /// decays at the blend rate, so the puppet glides onto the new path instead of jumping, and follows an owner moving at a
    /// steady velocity with no lag at any frame rate. A snapshot the pose misses by more than the snap distance, or one
    /// accepted as a discontinuity, clears the offset: the pose snaps (LastAcceptSnapped). It is a mutable struct: hold it
    /// in a field that is not readonly, and never mutate a copy read out of a collection. default(MeshKinematicTrack) is not
    /// configured, and its methods throw. A snapshot may be accepted with its age at arrival (DESIGN 1.19): it is then
    /// extrapolated from when it was sampled, and its offset still starts from the pose shown at its arrival.
    /// </summary>
    public struct MeshKinematicTrack
    {
        private readonly float _blendRate;
        private readonly float _maxExtrapolationSeconds;
        private readonly float _snapDistance;
        private readonly bool _isConfigured;
        private Vector3 _position;
        private Vector3 _velocity;
        private float _yawDegrees;
        private float _receivedAt;
        private float _sampledAt;
        private bool _hasSample;
        private Vector3 _offset;
        private float _yawOffset;
        private bool _lastAcceptSnapped;

        /// <summary>
        /// MeshKinematicTrack creates a track. <paramref name="blendRate"/> is the exponential rate (per second) at which an
        /// accepted snapshot's offset decays, <paramref name="maxExtrapolationSeconds"/> caps how far past a snapshot
        /// Predict looks, and <paramref name="snapDistance"/> is the miss in metres beyond which an accepted snapshot snaps
        /// the pose. Each must be positive.
        /// </summary>
        public MeshKinematicTrack(float blendRate = 15f, float maxExtrapolationSeconds = 0.3f, float snapDistance = 4f)
        {
            if (!(blendRate > 0f))
                throw new ArgumentOutOfRangeException(nameof(blendRate), blendRate, "blend rate must be positive");
            if (!(maxExtrapolationSeconds > 0f))
                throw new ArgumentOutOfRangeException(nameof(maxExtrapolationSeconds), maxExtrapolationSeconds, "maximum extrapolation must be positive");
            if (!(snapDistance > 0f))
                throw new ArgumentOutOfRangeException(nameof(snapDistance), snapDistance, "snap distance must be positive");
            _blendRate = blendRate;
            _maxExtrapolationSeconds = maxExtrapolationSeconds;
            _snapDistance = snapDistance;
            _isConfigured = true;
            _position = Vector3.zero;
            _velocity = Vector3.zero;
            _yawDegrees = 0f;
            _receivedAt = 0f;
            _sampledAt = 0f;
            _hasSample = false;
            _offset = Vector3.zero;
            _yawOffset = 0f;
            _lastAcceptSnapped = false;
        }

        /// <summary>IsConfigured is false for default(MeshKinematicTrack), whose methods throw.</summary>
        public bool IsConfigured => _isConfigured;

        /// <summary>HasSample is true once a snapshot was accepted.</summary>
        public bool HasSample => _hasSample;

        /// <summary>Velocity is the accepted snapshot's velocity in metres per second (zero before the first).</summary>
        public Vector3 Velocity => _velocity;

        /// <summary>YawDegrees is the accepted snapshot's yaw in [0, 360) (zero before the first).</summary>
        public float YawDegrees => _yawDegrees;

        /// <summary>
        /// LastAcceptSnapped is true when the last accepted snapshot cleared the offset: it was accepted as a
        /// discontinuity, or the pose missed it by more than the snap distance. The first snapshot never snaps.
        /// </summary>
        public bool LastAcceptSnapped => _lastAcceptSnapped;

        /// <summary>
        /// TryAccept takes a snapshot that arrived at <paramref name="receivedAt"/>. It refuses (false, nothing changes) a NaN
        /// or infinite value anywhere, or a snapshot that arrived before the last accepted one. The pose the track was
        /// showing at <paramref name="receivedAt"/> becomes the new offset from the snapshot, unless the snapshot is a
        /// discontinuity or the pose missed it by more than the snap distance: then the pose snaps to it.
        /// <paramref name="sampleAgeSeconds"/> is how old the snapshot already was when it arrived (clamped to [0, maximum
        /// extrapolation]): it is predicted from then, so a late snapshot is not taken for one that fell behind. A snapshot
        /// sampled before the last accepted one is refused too.
        /// </summary>
        public bool TryAccept(Vector3 position, Vector3 velocity, float yawDegrees, bool discontinuity, float receivedAt, float sampleAgeSeconds = 0f)
        {
            ThrowIfNotConfigured();
            if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(yawDegrees) || !IsFinite(receivedAt) || !IsFinite(sampleAgeSeconds))
                return false;
            float sampledAt = receivedAt - Mathf.Clamp(sampleAgeSeconds, 0f, _maxExtrapolationSeconds);
            if (_hasSample && (receivedAt < _receivedAt || sampledAt < _sampledAt))
                return false;
            float yaw = Mathf.Repeat(yawDegrees, 360f);
            Vector3 offset = Vector3.zero;
            float yawOffset = 0f;
            bool snapped = false;
            if (_hasSample)
            {
                Vector3 shown = Pose(receivedAt, 0f, out float shownYaw);
                offset = shown - (position + velocity * (receivedAt - sampledAt));
                if (discontinuity || !(offset.sqrMagnitude <= _snapDistance * _snapDistance))
                {
                    offset = Vector3.zero;
                    snapped = true;
                }
                else
                    yawOffset = Mathf.DeltaAngle(yaw, shownYaw);
            }
            _position = position;
            _velocity = velocity;
            _yawDegrees = yaw;
            _receivedAt = receivedAt;
            _sampledAt = sampledAt;
            _hasSample = true;
            _offset = offset;
            _yawOffset = yawOffset;
            _lastAcceptSnapped = snapped;
            return true;
        }

        /// <summary>
        /// Predict is the snapshot's position moved by its velocity for its age at <paramref name="now"/> plus
        /// <paramref name="leadSeconds"/>, that time clamped to [0, maximum extrapolation]. It throws before the first sample.
        /// </summary>
        public Vector3 Predict(float now, float leadSeconds)
        {
            ThrowIfNotConfigured();
            if (!_hasSample)
                throw new InvalidOperationException("MeshKinematicTrack has no sample to predict from: accept a snapshot first");
            float horizon = now - _sampledAt + leadSeconds;
            if (!(horizon > 0f))
                horizon = 0f;
            else if (horizon > _maxExtrapolationSeconds)
                horizon = _maxExtrapolationSeconds;
            return _position + _velocity * horizon;
        }

        /// <summary>
        /// Pose is where the puppet stands at <paramref name="now"/>: Predict(now, leadSeconds) plus the offset left by the
        /// last accepted snapshot, decayed by exp(-rate x its age). <paramref name="yawDegrees"/> is the snapshot's yaw plus
        /// its decayed offset, in [0, 360). A function of time alone, so it can be read at any moment and any frame rate. It
        /// throws before the first sample.
        /// </summary>
        public Vector3 Pose(float now, float leadSeconds, out float yawDegrees)
        {
            Vector3 predicted = Predict(now, leadSeconds);
            float age = now - _receivedAt;
            float decay = age > 0f ? Mathf.Exp(-_blendRate * age) : 1f;
            yawDegrees = Mathf.Repeat(_yawDegrees + _yawOffset * decay, 360f);
            return predicted + _offset * decay;
        }

        /// <summary>
        /// DropOffset clears the offset the last snapshot left, so Pose is the prediction alone: for a puppet nobody watches,
        /// which has nothing to glide for and should stand where its owner is.
        /// </summary>
        public void DropOffset()
        {
            ThrowIfNotConfigured();
            _offset = Vector3.zero;
            _yawOffset = 0f;
        }

        private void ThrowIfNotConfigured()
        {
            if (!_isConfigured)
                throw new InvalidOperationException("MeshKinematicTrack is not configured: create it with its constructor, not default");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
