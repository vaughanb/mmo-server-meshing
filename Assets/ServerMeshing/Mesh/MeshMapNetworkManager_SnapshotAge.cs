// Server Meshing add-on — a puppet extrapolates its owner's snapshot from when it was sampled, not from when it arrived
// (DESIGN.md 1.19).
//
// A cell sends a snapshot of each character it owns every ghostSnapshotInterval (100 ms), and the neighbour's puppet was
// extrapolated from the snapshot's arrival. The link's transport sends what was queued every 15 ms and the receiver reads
// it in its next frame, so snapshots arrived 0-30 ms late, unevenly: each one moved the puppet's track by up to ~12 cm at
// a walk, and the track's blend turned that into its gait. A puppet walking at 4 m/s drew about 2 m/s and 6 m/s in turns,
// 100 ms each.
//
// The sender stamps each character snapshot with its own clock (MeshLinkClock) when it samples the position. The receiver
// keeps, per link, the smallest transit (arrival minus stamp, in whatever offset the two clocks have) it has seen, rising
// TransitDriftPerSecond so a lasting change is followed; a snapshot's age is its transit above that, at most
// MaxSnapshotAgeSeconds. The track predicts the snapshot from its arrival less that age. The lag every snapshot shares
// is not removed, only its unevenness.
//
// That alone took little of the pulse (in 50 ms windows the speed's spread went from 1.03 to 0.88 m/s): the snapshots'
// velocities alternated, about 3.2 and 4.8 m/s for a 4 m/s walk. A snapshot's velocity was the move over the last
// interval, between positions that follow the client's uneven states on the owner. It is now the move over the last two
// intervals, unless the character changed its pace or heading over the last one (SnapshotVelocity): a stop, a start or a
// turn still reaches the neighbour within one snapshot.
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLinkClock is this process's monotonic clock for the mesh link's snapshot stamps, in ms.</summary>
    public static class MeshLinkClock
    {
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();

        /// <summary>NowMs is the milliseconds since this process first read the clock.</summary>
        public static double NowMs => s_clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// MeshLinkTransit is the smallest transit one link has shown, which a snapshot's lateness is measured against. Pure, so
    /// it is tested without a link.
    /// </summary>
    public sealed class MeshLinkTransit
    {
        /// <summary>The smallest transit seen rises this many ms per second, so a lasting change of route is followed.</summary>
        public const double TransitDriftPerSecond = 5.0;
        /// <summary>A snapshot is never taken for older than this: more is a stall, not jitter.</summary>
        public const float MaxSnapshotAgeSeconds = 0.1f;

        private double _fastest;
        private double _fastestAt;
        private bool _hasFastest;

        /// <summary>
        /// Age is how much later than the link's fastest a snapshot stamped <paramref name="sampleMs"/> by its sender arrived
        /// at <paramref name="arrivalMs"/> on this side, in seconds, in [0, MaxSnapshotAgeSeconds]. 0 for an unstamped one.
        /// </summary>
        public float Age(double sampleMs, double arrivalMs)
        {
            if (!(sampleMs > 0.0) || double.IsInfinity(sampleMs) || double.IsNaN(arrivalMs))
                return 0f;
            double transit = arrivalMs - sampleMs;
            if (_hasFastest)
                _fastest += System.Math.Max(0.0, arrivalMs - _fastestAt) * TransitDriftPerSecond / 1000.0;
            _fastestAt = arrivalMs;
            if (!_hasFastest || transit < _fastest)
            {
                _fastest = transit;
                _hasFastest = true;
            }
            return Mathf.Clamp((float)((transit - _fastest) / 1000.0), 0f, MaxSnapshotAgeSeconds);
        }
    }

    public partial class MeshMapNetworkManager
    {
        [Tooltip("Neighbour: extrapolate a puppet's snapshot from when its owner sampled it, not from when it arrived (DESIGN 1.19).")]
        public bool ageGhostSnapshots = true;

        /// <summary>
        /// SnapshotVelocity is the velocity a snapshot sends from <paramref name="overLast"/>, measured over the last snapshot
        /// interval, and <paramref name="overTwo"/>, over the last two: the longer one, which an uneven position at either
        /// end throws off half as much, unless the character stood, started, stopped (its pace changed by more than
        /// PaceChange either way) or turned by more than TurnDegrees over the last interval.
        /// </summary>
        public static Vector3 SnapshotVelocity(Vector3 overLast, Vector3 overTwo)
        {
            float last = overLast.magnitude, two = overTwo.magnitude;
            if (last < StandingSpeed || two < StandingSpeed)
                return overLast;
            if (last < two / PaceChange || last > two * PaceChange)
                return overLast;
            if (Vector3.Angle(overLast, overTwo) > TurnDegrees)
                return overLast;
            return overTwo;
        }

        private const float StandingSpeed = 0.3f;
        private const float PaceChange = 1.6f;
        private const float TurnDegrees = 30f;

        private readonly Dictionary<long, MeshLinkTransit> _linkTransit = new Dictionary<long, MeshLinkTransit>();

        /// <summary>How late a snapshot from link <paramref name="connectionId"/> arrived against that link's fastest, in seconds.</summary>
        private float GhostSnapshotAge(long connectionId, double sampleMs)
        {
            if (!ageGhostSnapshots || !(sampleMs > 0.0))
                return 0f;
            if (!_linkTransit.TryGetValue(connectionId, out MeshLinkTransit transit))
                _linkTransit[connectionId] = transit = new MeshLinkTransit();
            return transit.Age(sampleMs, MeshLinkClock.NowMs);
        }
    }
}
