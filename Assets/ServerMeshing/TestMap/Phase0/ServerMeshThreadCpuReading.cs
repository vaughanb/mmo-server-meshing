// Server Meshing add-on — rig thread CPU (MESH_THREAD_CPU): one finished window of per-thread CPU, and its Threads window line.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshThreadCpuReading is one finished window of the thread CPU sampler: the CPU each thread class, the whole
    /// process and the sampler thread itself used, in milliseconds, over <see cref="Seconds"/> of wall time, and the clock
    /// it was counted with. It is immutable, so the sampler thread hands it to the main thread by swapping one reference.
    /// </summary>
    public sealed class ServerMeshThreadCpuReading
    {
        /// <summary>ClassCount is the number of <see cref="ServerMeshThreadClass"/> values (Other is the last).</summary>
        public const int ClassCount = (int)ServerMeshThreadClass.Other + 1;

        /// <summary>NamesPresent: a thread named before it started reads that name back from Windows.</summary>
        public const string NamesPresent = "present";

        /// <summary>NamesAbsent: a thread named before it started reads no name back, so LiteNetLib's threads count as unnamed.</summary>
        public const string NamesAbsent = "absent";

        /// <summary>NamesUnavailable: the name probe could not read a description at all.</summary>
        public const string NamesUnavailable = "unavailable";

        private const double HzPerGHz = 1e9;

        private readonly double[] _classCpuMs;

        /// <summary>
        /// ServerMeshThreadCpuReading records a window of <paramref name="seconds"/>: <paramref name="classCpuMs"/> holds
        /// one CPU figure per <see cref="ServerMeshThreadClass"/> (indexed by its value), <paramref name="processCpuMs"/>
        /// is the whole process's, <paramref name="threads"/> how many threads the process had at the latest snapshot,
        /// <paramref name="samplerCpuMs"/> the sampler thread's own CPU, <paramref name="cyclesPerSecond"/> the calibrated
        /// time-stamp counter rate the figures were converted from cycles with (0 when they were counted in scheduler
        /// ticks), and <paramref name="namesPreStart"/> one of <see cref="NamesPresent"/>, <see cref="NamesAbsent"/> or
        /// <see cref="NamesUnavailable"/>. Every CPU figure is in milliseconds.
        /// </summary>
        public ServerMeshThreadCpuReading(double seconds, double[] classCpuMs, double processCpuMs, int threads, double samplerCpuMs, double cyclesPerSecond, string namesPreStart)
        {
            if (!(seconds > 0.0) || double.IsInfinity(seconds))
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "a window lasts a positive, finite number of seconds");
            if (classCpuMs == null)
                throw new ArgumentNullException(nameof(classCpuMs));
            if (classCpuMs.Length != ClassCount)
                throw new ArgumentOutOfRangeException(nameof(classCpuMs), classCpuMs.Length, "one CPU figure per thread class is needed");
            foreach (double cpu in classCpuMs)
            {
                if (!IsCpu(cpu))
                    throw new ArgumentOutOfRangeException(nameof(classCpuMs), cpu, "CPU used in a window is finite and never negative");
            }
            if (!IsCpu(processCpuMs))
                throw new ArgumentOutOfRangeException(nameof(processCpuMs), processCpuMs, "CPU used in a window is finite and never negative");
            if (threads < 0)
                throw new ArgumentOutOfRangeException(nameof(threads), threads, "a thread count is never negative");
            if (!IsCpu(samplerCpuMs))
                throw new ArgumentOutOfRangeException(nameof(samplerCpuMs), samplerCpuMs, "CPU used in a window is finite and never negative");
            if (!(cyclesPerSecond >= 0.0) || double.IsInfinity(cyclesPerSecond))
                throw new ArgumentOutOfRangeException(nameof(cyclesPerSecond), cyclesPerSecond, "a cycle rate is finite and positive, or 0 for scheduler ticks");
            if (namesPreStart == null)
                throw new ArgumentNullException(nameof(namesPreStart));
            if (namesPreStart != NamesPresent && namesPreStart != NamesAbsent && namesPreStart != NamesUnavailable)
                throw new ArgumentOutOfRangeException(nameof(namesPreStart), namesPreStart, "expected present, absent or unavailable");

            Seconds = seconds;
            _classCpuMs = (double[])classCpuMs.Clone();
            ProcessCpuMs = processCpuMs;
            Threads = threads;
            SamplerCpuMs = samplerCpuMs;
            CyclesPerSecond = cyclesPerSecond;
            NamesPreStart = namesPreStart;
        }

        /// <summary>Seconds is the wall time the window covers.</summary>
        public double Seconds { get; }

        /// <summary>ProcessCpuMs is the CPU the whole process used in the window, in milliseconds.</summary>
        public double ProcessCpuMs { get; }

        /// <summary>Threads is the number of this process's threads in the latest system snapshot, which is not taken every window.</summary>
        public int Threads { get; }

        /// <summary>SamplerCpuMs is the CPU the sampler thread itself used in the window, in milliseconds (it is counted in other too).</summary>
        public double SamplerCpuMs { get; }

        /// <summary>
        /// CyclesPerSecond is the calibrated time-stamp counter rate the window's cycle counts were converted with, or 0
        /// when the window was counted in scheduler ticks (GetThreadTimes and GetProcessTimes) because calibration failed.
        /// </summary>
        public double CyclesPerSecond { get; }

        /// <summary>CountsCycles is true when the window was counted in CPU cycles, false when in scheduler ticks.</summary>
        public bool CountsCycles => CyclesPerSecond > 0.0;

        /// <summary>NamesPreStart says whether a thread named before it started reads its name back from Windows.</summary>
        public string NamesPreStart { get; }

        /// <summary>TotalMsPerSecond is the CPU of all thread classes together, in milliseconds per second of wall time.</summary>
        public double TotalMsPerSecond
        {
            get
            {
                double total = 0.0;
                foreach (double cpu in _classCpuMs)
                    total += cpu;
                return total / Seconds;
            }
        }

        /// <summary>ProcessMsPerSecond is the process's CPU in milliseconds per second of wall time.</summary>
        public double ProcessMsPerSecond => ProcessCpuMs / Seconds;

        /// <summary>SamplerMsPerSecond is the sampler thread's own CPU in milliseconds per second of wall time.</summary>
        public double SamplerMsPerSecond => SamplerCpuMs / Seconds;

        /// <summary>CpuMs is the CPU one thread class used in the window, in milliseconds.</summary>
        public double CpuMs(ServerMeshThreadClass threadClass)
        {
            if (threadClass < 0 || (int)threadClass >= ClassCount)
                throw new ArgumentOutOfRangeException(nameof(threadClass), threadClass, "not a thread class");
            return _classCpuMs[(int)threadClass];
        }

        /// <summary>MsPerSecond is one thread class's CPU in milliseconds per second of wall time.</summary>
        public double MsPerSecond(ServerMeshThreadClass threadClass)
        {
            return CpuMs(threadClass) / Seconds;
        }

        /// <summary>
        /// ToLine is the window's log line: invariant culture, one physical line, no braces, no negative numbers. The clock
        /// reads <c>clock cycles G.GGG GHz</c> for cycle counting, or <c>clock ticks</c> for the scheduler-tick fallback.
        /// </summary>
        public string ToLine()
        {
            string clock = CountsCycles
                ? FormattableString.Invariant($"cycles {CyclesPerSecond / HzPerGHz:0.000} GHz")
                : "ticks";
            return FormattableString.Invariant($"Threads window {Seconds:0.00} s: main {MsPerSecond(ServerMeshThreadClass.Main):0.0} litenetlib {MsPerSecond(ServerMeshThreadClass.LiteNetLib):0.0} jobs {MsPerSecond(ServerMeshThreadClass.Jobs):0.0} unnamed {MsPerSecond(ServerMeshThreadClass.Unnamed):0.0} other {MsPerSecond(ServerMeshThreadClass.Other):0.0} total {TotalMsPerSecond:0.0} process {ProcessMsPerSecond:0.0} ms/s | threads {Threads} sampler {SamplerMsPerSecond:0.0} ms/s clock {clock} names pre-start={NamesPreStart}");
        }

        private static bool IsCpu(double ms)
        {
            return ms >= 0.0 && !double.IsInfinity(ms);
        }
    }
}
