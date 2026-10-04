// Server Meshing add-on — rig thread CPU (MESH_THREAD_CPU): the sampler's cycle clock calibration (busy spins, step 7b decision 8).
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshThreadCpuSampler
    {
        /// <summary>
        /// CycleRateFromSpins is the cycle rate a set of busy spins measured, spin i having used
        /// <paramref name="spinCycles"/>[i] cycles of the spinning thread in <paramref name="spinSeconds"/>[i] of wall time.
        /// It is the fastest spin's rate, since a spin can only read low (the thread was preempted in it; both cycle reads
        /// sit inside its wall time), and it holds when at least min(<see cref="SpinQuorum"/>, number of spins) spins read
        /// within <see cref="SpinAgreement"/> of it; otherwise, or when a spin has no cycles, no time or a non-finite rate,
        /// it is 0.
        /// </summary>
        public static double CycleRateFromSpins(long[] spinCycles, double[] spinSeconds)
        {
            int agreeing = SpinsNearFastest(spinCycles, spinSeconds, out double fastest, out bool allValid);
            if (!allValid || agreeing < Math.Min(SpinQuorum, spinCycles.Length))
                return 0.0;
            return fastest;
        }

        /// <summary>
        /// CalibrationDisagreement is the description <see cref="CalibrateCycleClock"/> reports when the spins it timed
        /// (<paramref name="spinCycles"/> cycles in <paramref name="spinSeconds"/> of wall time each) give no rate under
        /// <see cref="CycleRateFromSpins"/>: how many read within <see cref="SpinAgreement"/> of the fastest, of how many,
        /// against the quorum. Invariant culture.
        /// </summary>
        public static string CalibrationDisagreement(long[] spinCycles, double[] spinSeconds)
        {
            int agreeing = SpinsNearFastest(spinCycles, spinSeconds, out double fastest, out bool allValid);
            int spins = spinCycles.Length;
            if (!allValid)
                return FormattableString.Invariant($"Thread CPU: the cycle clock calibration had a spin with no cycles, no time or a rate that is not finite ({agreeing} of {spins} spins within {SpinAgreement * 100.0:0}% of the fastest); CPU is counted in scheduler ticks, clock ticks");
            return FormattableString.Invariant($"Thread CPU: the cycle clock calibration found {agreeing} of {spins} spins within {SpinAgreement * 100.0:0}% of the fastest ({fastest / HzPerGHz:0.000} GHz), need {Math.Min(SpinQuorum, spins)}; CPU is counted in scheduler ticks, clock ticks");
        }

        // Counts the spins with a finite rate within SpinAgreement of the fastest finite rate; allValid is false when a spin
        // has no cycles, no time or a non-finite rate (such a spin never counts as agreeing).
        private static int SpinsNearFastest(long[] spinCycles, double[] spinSeconds, out double fastest, out bool allValid)
        {
            if (spinCycles == null)
                throw new ArgumentNullException(nameof(spinCycles));
            if (spinSeconds == null)
                throw new ArgumentNullException(nameof(spinSeconds));
            if (spinCycles.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(spinCycles), spinCycles.Length, "at least one spin is needed");
            if (spinSeconds.Length != spinCycles.Length)
                throw new ArgumentOutOfRangeException(nameof(spinSeconds), spinSeconds.Length, "one wall time per spin is needed");
            fastest = 0.0;
            allValid = true;
            for (int i = 0; i < spinCycles.Length; ++i)
            {
                double rate = SpinRate(spinCycles[i], spinSeconds[i]);
                if (rate > 0.0)
                    fastest = Math.Max(fastest, rate);
                else
                    allValid = false;
            }
            double slowestAllowed = fastest * (1.0 - SpinAgreement);
            int agreeing = 0;
            for (int i = 0; i < spinCycles.Length; ++i)
            {
                double rate = SpinRate(spinCycles[i], spinSeconds[i]);
                if (rate > 0.0 && rate >= slowestAllowed)
                    ++agreeing;
            }
            return agreeing;
        }

        // A spin's rate in cycles per second, or 0 when it has no cycles, no time or a non-finite rate.
        private static double SpinRate(long cycles, double seconds)
        {
            if (cycles <= 0 || !(seconds > 0.0) || double.IsInfinity(seconds))
                return 0.0;
            double rate = cycles / seconds;
            return double.IsNaN(rate) || double.IsInfinity(rate) ? 0.0 : rate;
        }

        /// <summary>
        /// CalibrateCycleClock measures the rate CPU cycle counts advance at by timing <see cref="CalibrationSpins"/> busy
        /// spins of <see cref="CalibrationSpinMs"/> ms on the calling thread (the sampler thread) in each of
        /// <see cref="CalibrationAttempts"/> attempts, and judging all of them together (<see cref="CycleRateFromSpins"/>:
        /// the fastest, held by a <see cref="SpinQuorum"/> of agreeing spins); the thread runs at highest priority meanwhile
        /// (about 550 ms with the pauses between attempts), then gets its own back. It returns true and counts cycles from
        /// then on, or reports why and returns false, leaving the sampler on scheduler ticks. Call it before the first
        /// <see cref="Sample"/>: a window cannot mix the two.
        /// </summary>
        public bool CalibrateCycleClock()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServerMeshThreadCpuSampler));
            if (_pass > 0)
                throw new InvalidOperationException("the cycle clock is calibrated before the first pass, so no window mixes cycles and ticks");
            IntPtr self = ServerMeshThreadCpuNative.GetCurrentThread();
            long spinTicks = Stopwatch.Frequency * CalibrationSpinMs / 1000;
            long[] cycles = new long[CalibrationAttempts * CalibrationSpins];
            double[] seconds = new double[CalibrationAttempts * CalibrationSpins];
            Thread current = Thread.CurrentThread;
            ThreadPriority priority = current.Priority;
            // On a saturated machine normal-priority threads preempt every spin by a similar share, and spins that agree
            // on a rate that low would pass; above them, the spins run whole and read the true rate.
            current.Priority = ThreadPriority.Highest;
            try
            {
                for (int attempt = 0; attempt < CalibrationAttempts; ++attempt)
                {
                    if (attempt > 0)
                        Thread.Sleep(CalibrationPauseMs);
                    for (int s = 0; s < CalibrationSpins; ++s)
                    {
                        int i = attempt * CalibrationSpins + s;
                        if (!TimeSpin(self, spinTicks, out cycles[i], out seconds[i]))
                            return false;
                    }
                }
            }
            finally
            {
                current.Priority = priority;
            }
            double agreed = CycleRateFromSpins(cycles, seconds);
            if (agreed > 0.0)
            {
                CyclesPerSecond = agreed;
                _countsPerMs = agreed / MsPerSecond;
                return true;
            }
            Report(ref _reportedCalibration, CalibrationDisagreement(cycles, seconds));
            return false;
        }

        private bool TimeSpin(IntPtr self, long spinTicks, out long cycles, out double seconds)
        {
            cycles = 0;
            seconds = 0.0;
            long start = Stopwatch.GetTimestamp();
            if (!ServerMeshThreadCpuNative.QueryThreadCycleTime(self, out long before))
            {
                ReportCalibrationRead();
                return false;
            }
            long until = start + spinTicks;
            while (Stopwatch.GetTimestamp() < until)
            {
            }
            if (!ServerMeshThreadCpuNative.QueryThreadCycleTime(self, out long after))
            {
                ReportCalibrationRead();
                return false;
            }
            long end = Stopwatch.GetTimestamp();
            cycles = after - before;
            seconds = (end - start) / (double)Stopwatch.Frequency;
            return true;
        }

        private void ReportCalibrationRead()
        {
            Report(ref _reportedCalibration, FormattableString.Invariant($"Thread CPU: QueryThreadCycleTime failed with Win32 error {Marshal.GetLastWin32Error()} on the sampler thread; CPU is counted in scheduler ticks, clock ticks"));
        }
    }
}
