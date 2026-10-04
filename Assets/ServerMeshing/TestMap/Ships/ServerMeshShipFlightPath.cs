// Server Meshing test rig — the flight paths of pathed stand-in ships (Phase 0 step 7): load mirror ships, ship phantoms
// and puppets fly seeded ellipses in server time, and a bot recomputes each path from (seed, index, centre, scale).
// Rig-only: the space game at commit 1a3a58d9 has no scripted paths.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipFlightPath is one seeded, flat ellipse flown in server time (t = timestamp_ms / 1000): semi-major
    /// axis 200 to 280 m scaled, axis ratio 0.8 to 1, top speed 50 to 125 m/s scaled, a random phase, direction and
    /// major-axis heading. Index -2 is a parked "path" that stays at its centre; index -1 (an AI ship) has none.
    /// </summary>
    public sealed class ServerMeshShipFlightPath
    {
        /// <summary>AltitudeMetres is the flight altitude above the ground, never scaled.</summary>
        public const float AltitudeMetres = 60f;

        /// <summary>MinAxisRatio is the smallest semi-minor to semi-major ratio.</summary>
        public const float MinAxisRatio = 0.8f;

        /// <summary>PhantomIndexBase is the path index of phantom ship 0; load mirror k uses index k.</summary>
        public const int PhantomIndexBase = 1000;

        /// <summary>NoPath is the path index of an AI ship.</summary>
        public const int NoPath = -1;

        /// <summary>ParkedPath is the path index of a parked ship, which stays at its centre.</summary>
        public const int ParkedPath = -2;

        /// <summary>ScanStepSeconds is DistanceToPath's sampling step.</summary>
        public const double ScanStepSeconds = 0.005;

        private const int CentreSalt = 0x5350;
        private const int ShapeSalt = 0x5351;
        private const double TwoPi = 2d * Math.PI;
        private const int RefineSteps = 2;

        private readonly bool _parked;
        private readonly double _centreX, _centreZ;
        private readonly double _a, _b, _omega, _phase, _direction, _cosHeading, _sinHeading;

        /// <summary>
        /// ServerMeshShipFlightPath builds path <paramref name="index"/> of <paramref name="seed"/> around
        /// <paramref name="centre"/>: index -2 is parked; an index below -2 or -1 throws naming it, a null scale throws
        /// naming it.
        /// </summary>
        public ServerMeshShipFlightPath(int seed, int index, Vector3 centre, ServerMeshShipScale scale)
        {
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            Centre = centre;
            _centreX = centre.x;
            _centreZ = centre.z;
            if (index == ParkedPath)
            {
                _parked = true;
                return;
            }
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), index, "a flight path index is -2 (parked) or 0 and up; -1 marks an AI ship, which has no path");
            System.Random random = new System.Random(MixSeed(seed ^ ShapeSalt, index));
            _a = scale.PathSemiMajorMin + random.NextDouble() * (scale.PathSemiMajorMax - scale.PathSemiMajorMin);
            _b = _a * (MinAxisRatio + random.NextDouble() * (1d - MinAxisRatio));
            double speed = scale.PathSpeedMin + random.NextDouble() * (scale.PathSpeedMax - scale.PathSpeedMin);
            _omega = speed / _a;
            _phase = random.NextDouble() * TwoPi;
            _direction = random.Next(2) == 0 ? 1d : -1d;
            double heading = random.NextDouble() * Math.PI;
            _cosHeading = Math.Cos(heading);
            _sinHeading = Math.Sin(heading);
            SemiMajor = (float)_a;
            SemiMinor = (float)_b;
            MaxSpeed = (float)speed;
        }

        /// <summary>Centre is the ellipse's centre (a parked ship's position).</summary>
        public Vector3 Centre { get; }

        /// <summary>SemiMajor is the semi-major axis in metres; 0 when parked.</summary>
        public float SemiMajor { get; }

        /// <summary>SemiMinor is the semi-minor axis in metres; 0 when parked.</summary>
        public float SemiMinor { get; }

        /// <summary>MaxSpeed is the path's top speed in m/s, reached at the ends of the minor axis; 0 when parked.</summary>
        public float MaxSpeed { get; }

        /// <summary>IsParked is true for the parked path (index -2).</summary>
        public bool IsParked => _parked;

        /// <summary>ParkedLiftMetres is how far above the path plane parked ships fly: 20 m plus 3 m scaled (clear of a hull).</summary>
        public static float ParkedLiftMetres(ServerMeshShipScale scale)
        {
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            return 20f + 3f * scale.Factor;
        }

        /// <summary>MixSeed is the load generator's per-index seed formula: (seed * 486187739) ^ (k * 16777619 + 97), unchecked.</summary>
        public static int MixSeed(int seed, int k)
        {
            unchecked { return (seed * 486187739) ^ (k * 16777619 + 97); }
        }

        /// <summary>
        /// TryChooseCentre draws path <paramref name="index"/>'s centre inside <paramref name="area"/> (x is world x, y is
        /// world z) inset by the widest semi-major axis, at <paramref name="groundY"/> + AltitudeMetres. An area narrower
        /// than twice that axis on either side returns false with an error naming its size.
        /// </summary>
        public static bool TryChooseCentre(int seed, int index, Rect area, float groundY, ServerMeshShipScale scale, out Vector3 centre, out string error)
        {
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            centre = default;
            error = null;
            float inset = scale.PathSemiMajorMax;
            if (!(area.width >= 2f * inset) || !(area.height >= 2f * inset))
            {
                error = FormattableString.Invariant($"area {area.width:0.0} x {area.height:0.0} m cannot hold a flight path 2 x {inset:0.0} m wide");
                return false;
            }
            System.Random random = new System.Random(MixSeed(seed ^ CentreSalt, index));
            double x = area.xMin + inset + random.NextDouble() * (area.width - 2f * inset);
            double z = area.yMin + inset + random.NextDouble() * (area.height - 2f * inset);
            centre = new Vector3((float)x, groundY + AltitudeMetres, (float)z);
            return true;
        }

        /// <summary>
        /// Evaluate gives the path's pose at <paramref name="timeSeconds"/>: the position, the rotation looking along the
        /// velocity with world up, the velocity and the yaw rate around up as the angular velocity. A parked path gives
        /// its centre, identity and zeros.
        /// </summary>
        public void Evaluate(double timeSeconds, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angularVelocity)
        {
            if (_parked)
            {
                position = Centre;
                rotation = Quaternion.identity;
                velocity = Vector3.zero;
                angularVelocity = Vector3.zero;
                return;
            }
            double theta = Angle(timeSeconds);
            PositionAt(theta, out double x, out double z);
            VelocityAt(theta, out double vx, out double vz);
            position = new Vector3((float)x, Centre.y, (float)z);
            velocity = new Vector3((float)vx, 0f, (float)vz);
            rotation = Quaternion.LookRotation(velocity, Vector3.up);
            double sin = Math.Sin(theta), cos = Math.Cos(theta);
            double yawRate = -_direction * _omega * _a * _b / (_a * _a * sin * sin + _b * _b * cos * cos);
            angularVelocity = new Vector3(0f, (float)yawRate, 0f);
        }

        /// <summary>
        /// DistanceToPath is the distance from <paramref name="point"/> to the path curve over path time
        /// [t - behind, t + ahead]: a scan at ScanStepSeconds finds the nearest sample, the point-to-segment distance on
        /// the chords beside it gives the foot's time, and two projection steps on the curve itself refine it. lagSeconds
        /// is t minus the matched path time (positive when the point is behind). It allocates nothing; a parked path
        /// gives the distance to its centre and no lag. A negative or non-finite window throws naming it.
        /// </summary>
        public float DistanceToPath(Vector3 point, double timeSeconds, double behindSeconds, double aheadSeconds, out double lagSeconds)
        {
            if (!(behindSeconds >= 0d) || double.IsInfinity(behindSeconds))
                throw new ArgumentOutOfRangeException(nameof(behindSeconds), behindSeconds, "must be zero or more seconds");
            if (!(aheadSeconds >= 0d) || double.IsInfinity(aheadSeconds))
                throw new ArgumentOutOfRangeException(nameof(aheadSeconds), aheadSeconds, "must be zero or more seconds");
            lagSeconds = 0d;
            if (_parked)
                return Vector3.Distance(point, Centre);

            double start = timeSeconds - behindSeconds;
            double span = behindSeconds + aheadSeconds;
            int steps = Math.Max(1, (int)Math.Ceiling(span / ScanStepSeconds - 1e-9));
            double step = span / steps;
            int best = 0;
            double bestSquared = double.MaxValue;
            for (int i = 0; i <= steps; ++i)
            {
                double squared = SquaredDistance(point, start + i * step);
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    best = i;
                }
            }

            double bestTime = start + best * step;
            double footTime = bestTime;
            double footSquared = double.MaxValue;
            if (best > 0)
                ChordFoot(point, start + (best - 1) * step, bestTime, ref footTime, ref footSquared);
            if (best < steps)
                ChordFoot(point, bestTime, start + (best + 1) * step, ref footTime, ref footSquared);

            double low = start + Math.Max(0, best - 1) * step;
            double high = start + Math.Min(steps, best + 1) * step;
            double refined = footTime;
            for (int i = 0; i < RefineSteps; ++i)
                refined = ProjectionStep(point, refined, low, high);

            Consider(point, footTime, ref bestTime, ref bestSquared);
            Consider(point, refined, ref bestTime, ref bestSquared);
            lagSeconds = timeSeconds - bestTime;
            return (float)Math.Sqrt(bestSquared);
        }

        private double Angle(double timeSeconds)
        {
            return _phase + _direction * ((_omega * timeSeconds) % TwoPi);
        }

        private void PositionAt(double theta, out double x, out double z)
        {
            double localX = _a * Math.Cos(theta);
            double localZ = _b * Math.Sin(theta);
            x = _centreX + _cosHeading * localX + _sinHeading * localZ;
            z = _centreZ - _sinHeading * localX + _cosHeading * localZ;
        }

        private void VelocityAt(double theta, out double vx, out double vz)
        {
            double rate = _direction * _omega;
            double localX = -_a * Math.Sin(theta) * rate;
            double localZ = _b * Math.Cos(theta) * rate;
            vx = _cosHeading * localX + _sinHeading * localZ;
            vz = -_sinHeading * localX + _cosHeading * localZ;
        }

        private double SquaredDistance(Vector3 point, double timeSeconds)
        {
            PositionAt(Angle(timeSeconds), out double x, out double z);
            double dx = point.x - x, dy = point.y - (double)Centre.y, dz = point.z - z;
            return dx * dx + dy * dy + dz * dz;
        }

        private void ChordFoot(Vector3 point, double t0, double t1, ref double footTime, ref double footSquared)
        {
            PositionAt(Angle(t0), out double x0, out double z0);
            PositionAt(Angle(t1), out double x1, out double z1);
            double ex = x1 - x0, ez = z1 - z0;
            double length = ex * ex + ez * ez;
            double u = length > 0d ? ((point.x - x0) * ex + (point.z - z0) * ez) / length : 0d;
            u = Math.Max(0d, Math.Min(1d, u));
            double fx = x0 + u * ex - point.x, fz = z0 + u * ez - point.z, fy = Centre.y - (double)point.y;
            double squared = fx * fx + fy * fy + fz * fz;
            if (squared < footSquared)
            {
                footSquared = squared;
                footTime = t0 + u * (t1 - t0);
            }
        }

        private double ProjectionStep(Vector3 point, double timeSeconds, double low, double high)
        {
            double theta = Angle(timeSeconds);
            PositionAt(theta, out double x, out double z);
            VelocityAt(theta, out double vx, out double vz);
            double speedSquared = vx * vx + vz * vz;
            if (speedSquared <= 0d)
                return timeSeconds;
            double next = timeSeconds + ((point.x - x) * vx + (point.z - z) * vz) / speedSquared;
            return Math.Max(low, Math.Min(high, next));
        }

        private void Consider(Vector3 point, double timeSeconds, ref double bestTime, ref double bestSquared)
        {
            double squared = SquaredDistance(point, timeSeconds);
            if (squared < bestSquared)
            {
                bestSquared = squared;
                bestTime = timeSeconds;
            }
        }
    }
}
