// Server Meshing add-on — forwarded-hit timing (opt-in MESH_FORWARD_TIMING=1): pairs each hit a cell forwards to a
// ghost's owner with the owner's result, first in first out per target, on the sending cell's own clock.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshForwardTiming matches forwarded hits to their results. A send is queued under its target's mesh id; a result for
    /// that target takes the oldest queued send (the link is ordered, and the owner answers every hit it resolves in
    /// order). A send whose result never comes (the target died or left before the hit landed) is dropped once it is older
    /// than <see cref="PendingTimeoutSeconds"/>, before any later result for the same target is matched, so it cannot pair
    /// with that result. Latencies go into a fixed-bin histogram; nothing allocates per hit once a target's queue exists.
    /// Times are seconds on any monotonic clock the caller chooses.
    /// </summary>
    public sealed class MeshForwardTiming
    {
        /// <summary>PendingTimeoutSeconds is how long a send waits for its result before it is dropped as unanswered.</summary>
        public const double PendingTimeoutSeconds = 5.0;
        /// <summary>BinMs is the latency histogram's bin width in milliseconds.</summary>
        public const float BinMs = 0.25f;
        /// <summary>BinCount is the latency histogram's bin count (BinMs times BinCount is the range before the overflow).</summary>
        public const int BinCount = 4000;

        private readonly Dictionary<string, Queue<double>> _pending = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
        private readonly Stack<Queue<double>> _spare = new Stack<Queue<double>>();
        private readonly List<string> _emptied = new List<string>();
        private readonly MeshWorkHistogram _latency = new MeshWorkHistogram(BinMs, BinCount);
        private int _pendingCount;

        /// <summary>Sent is how many sends were noted since the last window reset.</summary>
        public int Sent { get; private set; }
        /// <summary>Results is how many results were noted since the last window reset, matched or not.</summary>
        public int Results { get; private set; }
        /// <summary>Matched is how many results were paired with a send since the last window reset (the latency samples).</summary>
        public int Matched => _latency.Count;
        /// <summary>Unmatched is how many results found no queued send since the last window reset.</summary>
        public int Unmatched { get; private set; }
        /// <summary>Expired is how many sends were dropped unanswered since the last window reset.</summary>
        public int Expired { get; private set; }
        /// <summary>PendingCount is how many sends wait for a result now, over every target.</summary>
        public int PendingCount => _pendingCount;
        /// <summary>TargetCount is how many targets hold a queue now (emptied queues are released by <see cref="Expire"/>).</summary>
        public int TargetCount => _pending.Count;

        /// <summary>P50Ms is the window's median latency in milliseconds (a bin's upper edge, clamped to the maximum), 0 when empty.</summary>
        public float P50Ms => _latency.Percentile(0.5f);
        /// <summary>P95Ms is the window's 95th percentile latency in milliseconds, 0 when empty.</summary>
        public float P95Ms => _latency.Percentile(0.95f);
        /// <summary>MaxMs is the window's largest latency in milliseconds, 0 when empty.</summary>
        public float MaxMs => _latency.Max;

        /// <summary>NoteSent queues one send to <paramref name="target"/> at <paramref name="now"/>; a null or empty target is ignored.</summary>
        public void NoteSent(string target, double now)
        {
            if (string.IsNullOrEmpty(target))
                return;
            if (!_pending.TryGetValue(target, out Queue<double> queue))
            {
                queue = _spare.Count > 0 ? _spare.Pop() : new Queue<double>();
                _pending[target] = queue;
            }
            queue.Enqueue(now);
            _pendingCount++;
            Sent++;
        }

        /// <summary>
        /// NoteResult counts one result for <paramref name="target"/> at <paramref name="now"/>: it drops the target's sends
        /// older than the timeout, then pairs the oldest one left and records now minus its time (negative counts as 0).
        /// True when a send was paired; a null or empty target counts as an unmatched result.
        /// </summary>
        public bool NoteResult(string target, double now)
        {
            Results++;
            if (string.IsNullOrEmpty(target) || !_pending.TryGetValue(target, out Queue<double> queue))
            {
                Unmatched++;
                return false;
            }
            DropOld(queue, now);
            if (queue.Count == 0)
            {
                Unmatched++;
                return false;
            }
            double sent = queue.Dequeue();
            _pendingCount--;
            double ms = (now - sent) * 1000.0;
            _latency.Add(ms > 0 && !double.IsNaN(ms) ? (float)ms : 0f);
            return true;
        }

        /// <summary>Expire drops every send older than the timeout and releases the queues it leaves empty for reuse.</summary>
        public void Expire(double now)
        {
            _emptied.Clear();
            foreach (KeyValuePair<string, Queue<double>> kv in _pending)
            {
                DropOld(kv.Value, now);
                if (kv.Value.Count == 0)
                    _emptied.Add(kv.Key);
            }
            for (int i = 0; i < _emptied.Count; ++i)
            {
                Queue<double> queue = _pending[_emptied[i]];
                _pending.Remove(_emptied[i]);
                _spare.Push(queue);
            }
            _emptied.Clear();
        }

        /// <summary>ResetWindow zeroes the window's counts and latencies; sends still waiting stay queued.</summary>
        public void ResetWindow()
        {
            Sent = Results = Unmatched = Expired = 0;
            _latency.Reset();
        }

        /// <summary>Clear forgets everything, the queued sends included.</summary>
        public void Clear()
        {
            foreach (Queue<double> queue in _pending.Values)
            {
                queue.Clear();
                _spare.Push(queue);
            }
            _pending.Clear();
            _pendingCount = 0;
            ResetWindow();
        }

        /// <summary>
        /// WindowLine is <c>Forwarded hits window 5.00 s: sent n results n | p50 x p95 y max z ms</c> for this window,
        /// invariant, without braces, negative or non-finite numbers written as 0.
        /// </summary>
        public string WindowLine(double seconds)
        {
            return Line(seconds, Sent, Results, P50Ms, P95Ms, MaxMs);
        }

        /// <summary>Line formats a Forwarded hits window line from its parts (see <see cref="WindowLine"/>).</summary>
        public static string Line(double seconds, long sent, long results, double p50Ms, double p95Ms, double maxMs)
        {
            StringBuilder b = new StringBuilder(96);
            b.Append("Forwarded hits window ").Append(Fixed(seconds)).Append(" s: sent ").Append(Count(sent));
            b.Append(" results ").Append(Count(results));
            b.Append(" | p50 ").Append(Fixed(p50Ms)).Append(" p95 ").Append(Fixed(p95Ms)).Append(" max ").Append(Fixed(maxMs)).Append(" ms");
            return b.ToString();
        }

        private void DropOld(Queue<double> queue, double now)
        {
            while (queue.Count > 0 && now - queue.Peek() > PendingTimeoutSeconds)
            {
                queue.Dequeue();
                _pendingCount--;
                Expired++;
            }
        }

        private static string Fixed(double value) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString("0.00", CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0L, value).ToString(CultureInfo.InvariantCulture);
    }
}
