// Server Meshing add-on — forwarded-hit timing (opt-in MESH_FORWARD_TIMING=1; Phase 0 step 9 decision 8).
// The cell that forwards a hit on a ghost to the ghost's owner times it from the send to the owner's result, on its own
// clock (first in, first out per target), and logs one "Forwarded hits window" line every 5 s. Under the key the per-hit
// trace lines of the forwarding path are not written: they cost work in the cut arm only. Default off: no state, no line.
using System.Diagnostics;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>ENV_FORWARD_TIMING is the opt-in key: "1" times forwarded hits and drops their per-hit trace lines.</summary>
        public const string ENV_FORWARD_TIMING = "MESH_FORWARD_TIMING";
        /// <summary>ForwardTimingWindowSeconds is the length of one Forwarded hits window on the cell's own clock.</summary>
        public const double ForwardTimingWindowSeconds = 5.0;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private static readonly bool s_forwardTiming = System.Environment.GetEnvironmentVariable(ENV_FORWARD_TIMING) == "1";
        private MeshForwardTiming _forwardTiming;
        private double _forwardTimingStart = -1.0;

        private static double ForwardTimingClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private MeshForwardTiming ForwardTiming => _forwardTiming ?? (_forwardTiming = new MeshForwardTiming());

        /// <summary>
        /// NoteForwardedHit records a hit just sent to <paramref name="targetMeshId"/>'s owner and returns true when the key is
        /// on, so the caller skips its per-hit trace line; false (and nothing recorded) when it is off.
        /// </summary>
        private bool NoteForwardedHit(string targetMeshId)
        {
            if (!s_forwardTiming)
                return false;
            ForwardTiming.NoteSent(targetMeshId, ForwardTimingClock());
            return true;
        }

        /// <summary>NoteForwardedResult records the owner's result for a hit this cell forwarded (key on only).</summary>
        private void NoteForwardedResult(string targetMeshId)
        {
            if (s_forwardTiming && IsServer)
                ForwardTiming.NoteResult(targetMeshId, ForwardTimingClock());
        }

        /// <summary>ForwardTimingTick closes a window every 5 s on the cell's own clock and logs its line (key on only).</summary>
        private void ForwardTimingTick()
        {
            if (!s_forwardTiming)
                return;
            double now = ForwardTimingClock();
            if (_forwardTimingStart < 0.0)
            {
                ForwardTiming.ResetWindow();
                _forwardTimingStart = now;
                return;
            }
            double seconds = now - _forwardTimingStart;
            if (seconds < ForwardTimingWindowSeconds)
                return;
            ForwardTiming.Expire(now);
            Logging.Log("Mesh", ForwardTiming.WindowLine(seconds));
            ForwardTiming.ResetWindow();
            _forwardTimingStart = now;
        }
#endif
    }
}
