// Server Meshing add-on — the work probe: main-thread work per frame without the frame limiter's wait
// (DESIGN.md 0.71; 0.68 showed a capped cell's frame time reads ~1/fps whatever its load).
//
// Unity's frame limiter waits in TimeUpdate.WaitForLastPresentationAndUpdateTime, the first phase of the player loop.
// UniTask injects a runner at the start of TimeUpdate (before that wait) and one at its end (after it), so:
//   TimeUpdate      -> the previous frame's work has ended, the wait begins
//   LastTimeUpdate  -> the wait has ended, this frame's work begins
// The placement is checked rather than trusted. With both stamps firing every frame, work + wait is the period by
// construction, so that sum proves nothing; what can go wrong is (a) a stamp that does not fire every frame, seen as
// work, wait and period sample counts that disagree, and (b) the limiter waiting outside the bracket, seen as a capped
// cell whose frames never wait. A cell below its budget waits at least a little on most frames at any load, so one
// window in which most frames waited proves the bracket. MeshWorkMeter reports Valid once that is seen, Invalid (for
// good) after repeated count mismatches, and stays Unknown -- with ProofOverdue set -- when a minute of capped frames
// never waited, which is a saturated cell or a misplaced bracket and cannot be told apart.
//
// The meter also sees the kit's logic ticks (the map server stamps each one) and the physics steps (a FixedUpdate
// runner), and files each frame's work by what ran in it: the Work ticks line (MeshTickWindow). Everything that feeds
// the meter runs on the main thread, and nothing on the per-frame or per-tick path allocates.
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshProbeState says whether a probe's work figures can be trusted.</summary>
    public enum MeshProbeState : byte
    {
        /// <summary>Unknown: no window has proved the bracket yet (or the cell has been saturated since it started).</summary>
        Unknown = 0,
        /// <summary>Valid: a window in which most frames waited proved the bracket; the work figures are trusted.</summary>
        Valid = 1,
        /// <summary>Invalid: the stamps did not fire every frame; the figures cannot be trusted for the process's life.</summary>
        Invalid = 2,
        /// <summary>Uncapped: the process has no frame cap, so there is no wait to bracket and no budget to measure against.</summary>
        Uncapped = 3,
    }

    /// <summary>MeshWorkWindow summarises one reporting window of the work probe.</summary>
    public struct MeshWorkWindow
    {
        /// <summary>frames is the number of frames whose work was measured in the window.</summary>
        public int frames;
        /// <summary>workMeanMs is the mean main-thread work of a frame, without the limiter's wait.</summary>
        public float workMeanMs;
        /// <summary>workP95Ms is the 95th percentile work of a frame.</summary>
        public float workP95Ms;
        /// <summary>workMaxMs is the largest work of a frame.</summary>
        public float workMaxMs;
        /// <summary>waitMeanMs is the mean time a frame spent in the limiter's wait.</summary>
        public float waitMeanMs;
        /// <summary>periodMeanMs is the mean time from one frame's work start to the next.</summary>
        public float periodMeanMs;
        /// <summary>saturatedShare is the share of frames that did not wait at all: the cell is using its whole budget.</summary>
        public float saturatedShare;
        /// <summary>budgetMs is the frame budget the process is capped at, or 0 when it is not capped.</summary>
        public float budgetMs;
        /// <summary>state says whether the window's figures can be trusted.</summary>
        public MeshProbeState state;

        /// <summary>Utilisation is mean work over the frame budget (0 when uncapped or unknown).</summary>
        public float Utilisation => budgetMs > 0f ? workMeanMs / budgetMs : 0f;
    }

    /// <summary>
    /// MeshWorkMeter turns work-start and work-end timestamps into per-window statistics and decides whether the
    /// bracket around the limiter's wait is where it is assumed to be. It also takes the kit's tick stamps and physics
    /// steps and files each frame's work by what ran in it. Pure: the probe feeds it Stopwatch ticks.
    /// </summary>
    public sealed class MeshWorkMeter
    {
        /// <summary>A frame that waited less than this did not wait: the timer resolution and the loop's own overhead.</summary>
        public const float NoWaitMs = 0.25f;
        /// <summary>A capped cell has had this long to show a real wait before its missing proof is reported.</summary>
        public const float ProveWaitWithinSeconds = 60f;
        /// <summary>A window proves the bracket when fewer than this share of its frames did not wait at all.</summary>
        public const float ProofSaturatedShare = 0.5f;
        /// <summary>MaxTicksPerFrame is the kit's cap: its logic updater runs at most this many ticks per Update, then drops its backlog.</summary>
        public const int MaxTicksPerFrame = 5;

        private const float TickBinMs = 0.05f;
        private const int TickBinCount = 4000; // 200 ms of 50 µs bins

        private readonly double _msPerTick;
        private readonly MeshWorkHistogram _work = new MeshWorkHistogram();
        private readonly MeshWorkHistogram _wait = new MeshWorkHistogram();
        private readonly MeshWorkHistogram _period = new MeshWorkHistogram();
        private long _workStart, _waitStart;
        private int _noWaitFrames;
        private bool _sawWait;
        private int _inconsistentWindows;
        private float _cappedSeconds;

        // Tick window totals: reset by TakeTickWindow.
        private readonly MeshWorkHistogram _tickFrames = new MeshWorkHistogram(TickBinMs, TickBinCount);
        private readonly MeshWorkHistogram _nonAoiTickFrames = new MeshWorkHistogram(TickBinMs, TickBinCount);
        private readonly MeshWorkHistogram _otherFrames = new MeshWorkHistogram(TickBinMs, TickBinCount);
        private readonly MeshWorkHistogram _tickDurations = new MeshWorkHistogram(TickBinMs, TickBinCount);
        private readonly MeshWorkHistogram _sendWork = new MeshWorkHistogram(TickBinMs, TickBinCount);
        private int _ticks, _unclosed, _aoiOutsideTick, _capFrames;
        private int _frames0, _frames1, _frames2Plus;
        private int _aoiPasses, _aoiFrames;
        private double _aoiFrameSumMs;
        private float _aoiFrameMaxMs;
        private int _partsTicks;
        private long _syncTicks, _aoiScheduleTicks, _movementTicks;
        private int _aoiCompleteCalls;
        private long _aoiCompleteTicks, _aoiCompleteMaxTicks;
        private int _physicsSteps, _physicsMaxPerFrame, _physics2PlusFrames;
        private double _workSumMs;

        // The frame in progress: consumed and reset by WorkEnded only, because a window is taken mid-frame.
        private int _frameTicks;
        private bool _frameAoiPass;
        private int _framePhysicsSteps;

        // The open tick, and the send sample of the last tick that closed.
        private bool _tickOpen;
        private long _tickStart;
        private bool _tickAoiMarked;
        private long _tickAoiStart, _tickAoiEnd;
        private bool _tickAoiPass;
        private bool _sendPending;
        private double _sendPendingMs;

        /// <summary>MeshWorkMeter creates a meter for timestamps with the given tick frequency (ticks per second).</summary>
        public MeshWorkMeter(long ticksPerSecond)
        {
            if (ticksPerSecond <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(ticksPerSecond), ticksPerSecond, "tick frequency must be positive");
            _msPerTick = 1000d / ticksPerSecond;
        }

        /// <summary>State is the probe's verdict after the last window.</summary>
        public MeshProbeState State { get; private set; } = MeshProbeState.Unknown;

        /// <summary>ProofOverdue is true once a minute of capped frames has passed without a window proving the bracket.</summary>
        public bool ProofOverdue { get; private set; }

        /// <summary>LogBins makes <see cref="TakeTickWindow"/> also list the non-empty bins of its histograms in <see cref="MeshTickWindow.bins"/>.</summary>
        public bool LogBins { get; set; }

        /// <summary>WorkEnded is called just before the limiter's wait: the previous frame's work is over.</summary>
        public void WorkEnded(long ticks)
        {
            if (_workStart != 0)
            {
                float work = (float)((ticks - _workStart) * _msPerTick);
                _work.Add(work);
                RecordFrame(work);
            }
            FlushSend();
            _frameTicks = 0;
            _frameAoiPass = false;
            _framePhysicsSteps = 0;
            _waitStart = ticks;
        }

        /// <summary>WorkStarted is called just after the limiter's wait: this frame's work begins.</summary>
        public void WorkStarted(long ticks)
        {
            if (_waitStart != 0)
            {
                float wait = (float)((ticks - _waitStart) * _msPerTick);
                _wait.Add(wait);
                if (wait < NoWaitMs)
                    _noWaitFrames++;
            }
            if (_workStart != 0)
                _period.Add((float)((ticks - _workStart) * _msPerTick));
            _workStart = ticks;
        }

        /// <summary>TakeWindow summarises the frames since the last call and starts a new window.</summary>
        public MeshWorkWindow TakeWindow(float budgetMs, float windowSeconds)
        {
            bool stampsAgree = System.Math.Abs(_work.Count - _wait.Count) <= 1 && System.Math.Abs(_period.Count - _wait.Count) <= 1;
            MeshWorkWindow window = new MeshWorkWindow
            {
                frames = _work.Count,
                workMeanMs = _work.Mean,
                workP95Ms = _work.Percentile(0.95f),
                workMaxMs = _work.Max,
                waitMeanMs = _wait.Mean,
                periodMeanMs = _period.Mean,
                saturatedShare = _wait.Count > 0 ? (float)_noWaitFrames / _wait.Count : 0f,
                budgetMs = budgetMs,
            };
            window.state = Judge(window, stampsAgree, windowSeconds);
            _work.Reset();
            _wait.Reset();
            _period.Reset();
            _noWaitFrames = 0;
            return window;
        }

        private MeshProbeState Judge(MeshWorkWindow window, bool consistent, float windowSeconds)
        {
            if (window.budgetMs <= 0f)
                return State = MeshProbeState.Uncapped;
            if (window.frames == 0 || window.periodMeanMs <= 0f)
                return State;
            _inconsistentWindows = consistent ? 0 : _inconsistentWindows + 1;
            if (State == MeshProbeState.Invalid || _inconsistentWindows >= 3)
                return State = MeshProbeState.Invalid; // stamps that do not fire every frame: disproved for the process's life
            if (consistent && window.saturatedShare < ProofSaturatedShare)
                _sawWait = true;
            _cappedSeconds += windowSeconds;
            if (!_sawWait && _cappedSeconds >= ProveWaitWithinSeconds)
                ProofOverdue = true;
            return State = _sawWait && consistent ? MeshProbeState.Valid : MeshProbeState.Unknown;
        }

        /// <summary>
        /// BeginTick is called when a kit tick starts. A tick still open (the previous one threw before it ended) is counted
        /// as unclosed and dropped. The send sample of the previous tick is complete and is recorded now.
        /// </summary>
        public void BeginTick(long stamp)
        {
            if (_tickOpen)
                _unclosed++;
            FlushSend();
            _tickOpen = true;
            _tickStart = stamp;
            _tickAoiMarked = false;
            _tickAoiStart = 0;
            _tickAoiEnd = 0;
            _tickAoiPass = false;
            _ticks++;
            _frameTicks++;
        }

        /// <summary>
        /// MarkAoiCall records the interest manager's call inside the open tick, and whether it ran a pass. Several calls
        /// in one tick keep the first start and the last end. A call while no tick is open is only counted.
        /// </summary>
        public void MarkAoiCall(long startStamp, long endStamp, bool ranPass)
        {
            if (!_tickOpen)
            {
                _aoiOutsideTick++;
                return;
            }
            if (!_tickAoiMarked)
            {
                _tickAoiMarked = true;
                _tickAoiStart = startStamp;
            }
            _tickAoiEnd = endStamp;
            _tickAoiPass |= ranPass;
        }

        /// <summary>
        /// EndTick is called the moment the kit's tick work returns. It records the tick's duration and, when the
        /// interest-manager call was marked, its three parts (sync and ping, the call, the movement send), each clamped
        /// to the tick so they add up to its duration exactly, and leaves a send sample of the movement send's time for
        /// <see cref="AddTickWork"/> to add to. Without a marked call the movement send cannot be told from the rest of the
        /// tick, so no sample is started. Without an open tick it does nothing.
        /// </summary>
        public void EndTick(long stamp)
        {
            if (!_tickOpen)
                return;
            _tickOpen = false;
            long end = System.Math.Max(stamp, _tickStart);
            _tickDurations.Add((float)((end - _tickStart) * _msPerTick));
            if (_tickAoiMarked)
            {
                long aoiStart = System.Math.Min(System.Math.Max(_tickAoiStart, _tickStart), end);
                long aoiEnd = System.Math.Min(System.Math.Max(_tickAoiEnd, aoiStart), end);
                _partsTicks++;
                _syncTicks += aoiStart - _tickStart;
                _aoiScheduleTicks += aoiEnd - aoiStart;
                _movementTicks += end - aoiEnd;
                _sendPendingMs = (_sendPending ? _sendPendingMs : 0d) + (end - aoiEnd) * _msPerTick;
                _sendPending = true;
                if (_tickAoiPass)
                {
                    _aoiPasses++;
                    _frameAoiPass = true;
                }
            }
        }

        /// <summary>
        /// AddTickWork adds work done for the last tick after it ended (its sender) to that tick's send sample, starting
        /// one if none is pending. The sample is recorded at the next BeginTick, WorkEnded or TakeTickWindow. A negative
        /// or non-finite value adds nothing.
        /// </summary>
        public void AddTickWork(double ms)
        {
            if (!(ms > 0d) || double.IsInfinity(ms))
                ms = 0d;
            _sendPendingMs = (_sendPending ? _sendPendingMs : 0d) + ms;
            _sendPending = true;
        }

        /// <summary>MarkAoiComplete records one call of the interest manager's completion work outside the tick (its late update).</summary>
        public void MarkAoiComplete(long startStamp, long endStamp)
        {
            long duration = System.Math.Max(0L, endStamp - startStamp);
            _aoiCompleteCalls++;
            _aoiCompleteTicks += duration;
            if (duration > _aoiCompleteMaxTicks)
                _aoiCompleteMaxTicks = duration;
        }

        /// <summary>NotePhysicsStep counts one physics step (one FixedUpdate) in the frame in progress.</summary>
        public void NotePhysicsStep()
        {
            _framePhysicsSteps++;
        }

        /// <summary>
        /// TakeTickWindow summarises the ticks and frames since the last call and starts a new tick window. It is taken
        /// mid-frame, so the frame in progress is left alone: its ticks count in this window and its work in the next
        /// (±1 frame per window, by design). <paramref name="nominalBudgetMs"/> is only carried to the line.
        /// </summary>
        public MeshTickWindow TakeTickWindow(double seconds, int tickHz, float nominalBudgetMs)
        {
            FlushSend();
            bool timed = seconds > 0d && !double.IsInfinity(seconds);
            double due = timed && tickHz > 0 ? System.Math.Round(seconds * tickHz) : 0d;
            int expected = due >= int.MaxValue ? int.MaxValue : (int)due;
            int frames = _frames0 + _frames1 + _frames2Plus;
            float workMsPerSecond = timed ? (float)(_workSumMs / seconds) : 0f;
            MeshTickWindow window = new MeshTickWindow
            {
                seconds = timed ? (float)seconds : 0f,
                tickHz = tickHz > 0 ? tickHz : 0,
                ticks = _ticks,
                expected = expected,
                dropped = System.Math.Max(0, expected - _ticks),
                capFrames = _capFrames,
                unclosed = _unclosed,
                aoiOutsideTick = _aoiOutsideTick,
                frames = frames,
                frames0 = _frames0,
                frames1 = _frames1,
                frames2Plus = _frames2Plus,
                tickFrameMeanMs = _tickFrames.Mean,
                tickFrameP95Ms = _tickFrames.Percentile(0.95f),
                tickFrameP99Ms = _tickFrames.Percentile(0.99f),
                tickFrameMaxMs = _tickFrames.Max,
                nonAoiTickFrameP95Ms = _nonAoiTickFrames.Percentile(0.95f),
                nonAoiTickFrameP99Ms = _nonAoiTickFrames.Percentile(0.99f),
                aoiPasses = _aoiPasses,
                aoiFrames = _aoiFrames,
                aoiFrameMeanMs = _aoiFrames > 0 ? (float)(_aoiFrameSumMs / _aoiFrames) : 0f,
                aoiFrameMaxMs = _aoiFrameMaxMs,
                otherFrameMeanMs = _otherFrames.Mean,
                otherFrameP95Ms = _otherFrames.Percentile(0.95f),
                tickMeanMs = _tickDurations.Mean,
                tickP95Ms = _tickDurations.Percentile(0.95f),
                tickP99Ms = _tickDurations.Percentile(0.99f),
                tickMaxMs = _tickDurations.Max,
                partsTicks = _partsTicks,
                syncMeanMs = MeanMs(_syncTicks, _partsTicks),
                aoiScheduleMeanMs = MeanMs(_aoiScheduleTicks, _partsTicks),
                movementMeanMs = MeanMs(_movementTicks, _partsTicks),
                aoiCompleteCalls = _aoiCompleteCalls,
                aoiCompleteMeanMs = MeanMs(_aoiCompleteTicks, _aoiCompleteCalls),
                aoiCompleteMaxMs = (float)(_aoiCompleteMaxTicks * _msPerTick),
                aoiCompleteMsPerSecond = timed ? (float)(_aoiCompleteTicks * _msPerTick / seconds) : 0f,
                sendSamples = _sendWork.Count,
                sendMeanMs = _sendWork.Mean,
                sendP95Ms = _sendWork.Percentile(0.95f),
                sendP99Ms = _sendWork.Percentile(0.99f),
                physicsSteps = _physicsSteps,
                physicsMaxPerFrame = _physicsMaxPerFrame,
                physics2PlusFrames = _physics2PlusFrames,
                physicsPerSecond = timed ? (float)(_physicsSteps / seconds) : 0f,
                physicsMeanPerFrame = frames > 0 ? (float)_physicsSteps / frames : 0f,
                workMsPerSecond = workMsPerSecond,
                busyShare = workMsPerSecond / 1000f,
                nominalBudgetMs = nominalBudgetMs > 0f && !float.IsInfinity(nominalBudgetMs) ? nominalBudgetMs : 0f,
                bins = LogBins ? BinsLine(timed ? seconds : 0d) : null,
            };
            ResetTickWindow();
            return window;
        }

        private void RecordFrame(float work)
        {
            _workSumMs += work;
            if (_frameTicks > 0)
            {
                _tickFrames.Add(work);
                if (_frameAoiPass)
                {
                    _aoiFrames++;
                    _aoiFrameSumMs += work;
                    if (work > _aoiFrameMaxMs)
                        _aoiFrameMaxMs = work;
                }
                else
                    _nonAoiTickFrames.Add(work);
                if (_frameTicks == 1)
                    _frames1++;
                else
                    _frames2Plus++;
                if (_frameTicks >= MaxTicksPerFrame)
                    _capFrames++;
            }
            else
            {
                _otherFrames.Add(work);
                _frames0++;
            }
            _physicsSteps += _framePhysicsSteps;
            if (_framePhysicsSteps > _physicsMaxPerFrame)
                _physicsMaxPerFrame = _framePhysicsSteps;
            if (_framePhysicsSteps >= 2)
                _physics2PlusFrames++;
        }

        private void FlushSend()
        {
            if (!_sendPending)
                return;
            _sendWork.Add((float)_sendPendingMs);
            _sendPending = false;
            _sendPendingMs = 0d;
        }

        private float MeanMs(long ticks, int count) => count > 0 ? (float)(ticks * _msPerTick / count) : 0f;

        private void ResetTickWindow()
        {
            _tickFrames.Reset();
            _nonAoiTickFrames.Reset();
            _otherFrames.Reset();
            _tickDurations.Reset();
            _sendWork.Reset();
            _ticks = _unclosed = _aoiOutsideTick = _capFrames = 0;
            _frames0 = _frames1 = _frames2Plus = 0;
            _aoiPasses = _aoiFrames = 0;
            _aoiFrameSumMs = 0d;
            _aoiFrameMaxMs = 0f;
            _partsTicks = 0;
            _syncTicks = _aoiScheduleTicks = _movementTicks = 0;
            _aoiCompleteCalls = 0;
            _aoiCompleteTicks = _aoiCompleteMaxTicks = 0;
            _physicsSteps = _physicsMaxPerFrame = _physics2PlusFrames = 0;
            _workSumMs = 0d;
        }

        // "Work bins 5.00 s: tick 50:3 51:7 over:0 | nonaoi ... | other ... | kit ... | send ...": brace-free, because the
        // logger treats a message as a format string.
        private string BinsLine(double seconds)
        {
            StringBuilder line = new StringBuilder(256);
            line.Append("Work bins ").Append(seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" s:");
            AppendBins(line, " tick", _tickFrames);
            AppendBins(line, " | nonaoi", _nonAoiTickFrames);
            AppendBins(line, " | other", _otherFrames);
            AppendBins(line, " | kit", _tickDurations);
            AppendBins(line, " | send", _sendWork);
            return line.ToString();
        }

        private static void AppendBins(StringBuilder line, string label, MeshWorkHistogram histogram)
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            line.Append(label);
            for (int bin = 0; bin < histogram.BinCount; ++bin)
            {
                int count = histogram.CountInBin(bin);
                if (count > 0)
                    line.Append(' ').Append(bin.ToString(invariant)).Append(':').Append(count.ToString(invariant));
            }
            line.Append(" over:").Append(histogram.Overflow.ToString(invariant));
        }
    }

    /// <summary>
    /// MeshWorkProbe hooks the process's player loop once and feeds a <see cref="MeshWorkMeter"/>; the map server takes
    /// one window every probe window (its load-report cadence) and shows the latest in the Mesh stats line.
    /// </summary>
    public static class MeshWorkProbe
    {
        private static MeshWorkMeter s_meter;

        /// <summary>Meter is the process's meter, hooked into the player loop on first use.</summary>
        public static MeshWorkMeter Meter
        {
            get
            {
                if (s_meter == null)
                {
                    s_meter = new MeshWorkMeter(System.Diagnostics.Stopwatch.Frequency);
                    PlayerLoopHelper.AddAction(PlayerLoopTiming.TimeUpdate, new WorkEndItem());
                    PlayerLoopHelper.AddAction(PlayerLoopTiming.LastTimeUpdate, new WorkStartItem());
                    PlayerLoopHelper.AddAction(PlayerLoopTiming.FixedUpdate, new PhysicsStepItem());
                }
                return s_meter;
            }
        }

        /// <summary>BudgetMs is the frame budget the process is capped at, or 0 when it is not capped.</summary>
        public static float BudgetMs => Application.targetFrameRate > 0 ? 1000f / Application.targetFrameRate : 0f;

        /// <summary>
        /// NominalFrameRate is the frame rate a run is modelled at while the process is uncapped (0 = none). Rig code sets
        /// it; it feeds only the Work ticks line's budget.
        /// </summary>
        public static int NominalFrameRate { get; set; }

        /// <summary>BudgetFor is 1000 / target when the target frame rate is positive, else 1000 / nominal when that is, else 0.</summary>
        public static float BudgetFor(int targetFrameRate, int nominalFrameRate)
        {
            if (targetFrameRate > 0)
                return 1000f / targetFrameRate;
            return nominalFrameRate > 0 ? 1000f / nominalFrameRate : 0f;
        }

        /// <summary>
        /// EffectiveBudgetMs is the cap's budget, or the nominal rate's when uncapped: for the Work ticks line only. The
        /// probe's verdict, the load report and the Work window line keep <see cref="BudgetMs"/>, where 0 means uncapped.
        /// </summary>
        public static float EffectiveBudgetMs => BudgetFor(Application.targetFrameRate, NominalFrameRate);

        private sealed class WorkEndItem : IPlayerLoopItem
        {
            public bool MoveNext()
            {
                s_meter.WorkEnded(System.Diagnostics.Stopwatch.GetTimestamp());
                return true;
            }
        }

        private sealed class WorkStartItem : IPlayerLoopItem
        {
            public bool MoveNext()
            {
                s_meter.WorkStarted(System.Diagnostics.Stopwatch.GetTimestamp());
                return true;
            }
        }

        // UniTask drops an item for good if it throws once, so this only counts.
        private sealed class PhysicsStepItem : IPlayerLoopItem
        {
            public bool MoveNext()
            {
                s_meter.NotePhysicsStep();
                return true;
            }
        }
    }
}
