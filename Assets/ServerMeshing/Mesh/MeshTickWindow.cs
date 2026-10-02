// Server Meshing add-on — one window of the kit's logic ticks as the work meter saw them, logged as a "Work ticks" line
// next to the Work window line. Log-only: it never goes on the load-report wire, which stays frozen.

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshTickWindow summarises the kit ticks and the frames they fell on over one probe window: how many ticks ran
    /// against how many were due, what a frame costs with and without ticks (and with an interest-management pass), the
    /// tick's own duration and its parts, the interest manager's completion work, the send work after each tick, physics
    /// steps, and the frame budget the run is modelled at. Every figure is 0 when nothing was measured.
    /// </summary>
    public struct MeshTickWindow
    {
        /// <summary>seconds is the window's length.</summary>
        public float seconds;
        /// <summary>tickHz is the kit's tick rate during the window.</summary>
        public int tickHz;
        /// <summary>ticks is the number of kit ticks that started in the window.</summary>
        public int ticks;
        /// <summary>expected is seconds x tickHz, rounded: the ticks that were due.</summary>
        public int expected;
        /// <summary>dropped is expected minus ticks, never below 0 (±1 at window edges is jitter, not a drop).</summary>
        public int dropped;
        /// <summary>capFrames is the number of frames that ran the kit's per-frame tick cap, where it drops its backlog.</summary>
        public int capFrames;
        /// <summary>unclosed is the number of ticks that began and never ended (the tick threw).</summary>
        public int unclosed;
        /// <summary>aoiOutsideTick is the number of interest-manager calls timed while no tick was open.</summary>
        public int aoiOutsideTick;
        /// <summary>frames is the number of frames recorded in the window: frames0 + frames1 + frames2Plus.</summary>
        public int frames;
        /// <summary>frames0 is the number of frames that ran no tick.</summary>
        public int frames0;
        /// <summary>frames1 is the number of frames that ran exactly one tick.</summary>
        public int frames1;
        /// <summary>frames2Plus is the number of frames that ran two ticks or more.</summary>
        public int frames2Plus;

        /// <summary>tickFrameMeanMs is the mean work of frames that ran at least one tick.</summary>
        public float tickFrameMeanMs;
        /// <summary>tickFrameP95Ms is the 95th percentile work of frames that ran at least one tick.</summary>
        public float tickFrameP95Ms;
        /// <summary>tickFrameP99Ms is the 99th percentile work of frames that ran at least one tick.</summary>
        public float tickFrameP99Ms;
        /// <summary>tickFrameMaxMs is the largest work of a frame that ran at least one tick.</summary>
        public float tickFrameMaxMs;
        /// <summary>nonAoiTickFrameP95Ms is the 95th percentile work of tick frames without an interest-management pass.</summary>
        public float nonAoiTickFrameP95Ms;
        /// <summary>nonAoiTickFrameP99Ms is the 99th percentile work of tick frames without an interest-management pass.</summary>
        public float nonAoiTickFrameP99Ms;

        /// <summary>aoiPasses is the number of ticks whose interest-management call ran a pass.</summary>
        public int aoiPasses;
        /// <summary>aoiFrames is the number of frames that held at least one interest-management pass.</summary>
        public int aoiFrames;
        /// <summary>aoiFrameMeanMs is the mean work of the frames that held a pass.</summary>
        public float aoiFrameMeanMs;
        /// <summary>aoiFrameMaxMs is the largest work of a frame that held a pass.</summary>
        public float aoiFrameMaxMs;

        /// <summary>otherFrameMeanMs is the mean work of frames that ran no tick.</summary>
        public float otherFrameMeanMs;
        /// <summary>otherFrameP95Ms is the 95th percentile work of frames that ran no tick.</summary>
        public float otherFrameP95Ms;

        /// <summary>tickMeanMs is the mean duration of one kit tick (sync pass, interest manager and movement send).</summary>
        public float tickMeanMs;
        /// <summary>tickP95Ms is the 95th percentile duration of one kit tick.</summary>
        public float tickP95Ms;
        /// <summary>tickP99Ms is the 99th percentile duration of one kit tick.</summary>
        public float tickP99Ms;
        /// <summary>tickMaxMs is the longest kit tick.</summary>
        public float tickMaxMs;

        /// <summary>partsTicks is the number of ticks whose interest-management call was timed, so their parts are known.</summary>
        public int partsTicks;
        /// <summary>syncMeanMs is the mean time from the tick's start to the interest-manager call (state sync and ping).</summary>
        public float syncMeanMs;
        /// <summary>aoiScheduleMeanMs is the mean time of the interest-manager call inside the tick (gather and schedule only).</summary>
        public float aoiScheduleMeanMs;
        /// <summary>movementMeanMs is the mean time from the interest-manager call's end to the tick's end (the movement send).</summary>
        public float movementMeanMs;

        /// <summary>aoiCompleteCalls is the number of timed interest-manager completion calls outside the tick.</summary>
        public int aoiCompleteCalls;
        /// <summary>aoiCompleteMeanMs is the mean duration of a completion call.</summary>
        public float aoiCompleteMeanMs;
        /// <summary>aoiCompleteMaxMs is the longest completion call.</summary>
        public float aoiCompleteMaxMs;
        /// <summary>aoiCompleteMsPerSecond is the completion work per second of the window.</summary>
        public float aoiCompleteMsPerSecond;

        /// <summary>sendSamples is the number of per-tick send samples (movement send plus work added after the tick).</summary>
        public int sendSamples;
        /// <summary>sendMeanMs is the mean send work of one tick.</summary>
        public float sendMeanMs;
        /// <summary>sendP95Ms is the 95th percentile send work of one tick.</summary>
        public float sendP95Ms;
        /// <summary>sendP99Ms is the 99th percentile send work of one tick.</summary>
        public float sendP99Ms;

        /// <summary>physicsSteps is the number of physics steps on the recorded frames.</summary>
        public int physicsSteps;
        /// <summary>physicsMaxPerFrame is the most physics steps on one frame.</summary>
        public int physicsMaxPerFrame;
        /// <summary>physics2PlusFrames is the number of frames that ran two physics steps or more.</summary>
        public int physics2PlusFrames;
        /// <summary>physicsPerSecond is physicsSteps over the window's seconds.</summary>
        public float physicsPerSecond;
        /// <summary>physicsMeanPerFrame is physicsSteps over the recorded frames.</summary>
        public float physicsMeanPerFrame;

        /// <summary>workMsPerSecond is the frames' total work over the window's seconds.</summary>
        public float workMsPerSecond;
        /// <summary>busyShare is workMsPerSecond / 1000: the share of wall time the main thread worked.</summary>
        public float busyShare;
        /// <summary>nominalBudgetMs is the frame budget the run is modelled at (the cap, or the nominal rate when uncapped); 0 = none.</summary>
        public float nominalBudgetMs;
        /// <summary>gen0Collections is the number of generation-0 collections during the window, filled in by the caller.</summary>
        public int gen0Collections;
        /// <summary>bins is the window's second log line (non-empty 0.05 ms bins as index:count), or null unless bins were asked for.</summary>
        public string bins;

        /// <summary>
        /// ToLine formats the window as one "Work ticks" log line in invariant culture. Every field is always printed; a
        /// negative or non-finite value prints as 0, so the line never holds a minus sign, NaN or Infinity.
        /// </summary>
        public string ToLine()
        {
            return System.FormattableString.Invariant(
                $"Work ticks {Clean(seconds):0.00} s: ticks {Clean(ticks)} expected {Clean(expected)} dropped {Clean(dropped)} cap {Clean(capFrames)} unclosed {Clean(unclosed)} | frames {Clean(frames)} t0 {Clean(frames0)} t1 {Clean(frames1)} t2 {Clean(frames2Plus)} | tick frames mean {Clean(tickFrameMeanMs):0.000} ms p95 {Clean(tickFrameP95Ms):0.00} ms p99 {Clean(tickFrameP99Ms):0.00} ms max {Clean(tickFrameMaxMs):0.0} ms | nonaoi p95 {Clean(nonAoiTickFrameP95Ms):0.00} ms p99 {Clean(nonAoiTickFrameP99Ms):0.00} ms | aoi passes {Clean(aoiPasses)} frames {Clean(aoiFrames)} mean {Clean(aoiFrameMeanMs):0.000} ms max {Clean(aoiFrameMaxMs):0.0} ms | other mean {Clean(otherFrameMeanMs):0.000} ms p95 {Clean(otherFrameP95Ms):0.00} ms | kit mean {Clean(tickMeanMs):0.000} ms p95 {Clean(tickP95Ms):0.00} ms p99 {Clean(tickP99Ms):0.00} ms max {Clean(tickMaxMs):0.0} ms | parts {Clean(partsTicks)} sync {Clean(syncMeanMs):0.000} ms aoi {Clean(aoiScheduleMeanMs):0.000} ms move {Clean(movementMeanMs):0.000} ms | late {Clean(aoiCompleteCalls)} mean {Clean(aoiCompleteMeanMs):0.000} ms max {Clean(aoiCompleteMaxMs):0.0} ms {Clean(aoiCompleteMsPerSecond):0.0} ms/s | send {Clean(sendSamples)} mean {Clean(sendMeanMs):0.000} ms p95 {Clean(sendP95Ms):0.00} ms p99 {Clean(sendP99Ms):0.00} ms | physics {Clean(physicsPerSecond):0.0}/s mean {Clean(physicsMeanPerFrame):0.00} max {Clean(physicsMaxPerFrame)} f2 {Clean(physics2PlusFrames)} | work {Clean(workMsPerSecond):0.0} ms/s busy {Clean(busyShare * 100f):0.0}% | budget {Clean(nominalBudgetMs):0.00} ms | gc0 {Clean(gen0Collections)} | outside {Clean(aoiOutsideTick)}");
        }

        private static float Clean(float value) => value > 0f && !float.IsInfinity(value) ? value : 0f;

        private static int Clean(int value) => value > 0 ? value : 0;
    }
}
