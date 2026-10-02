// Server Meshing add-on — rig thread CPU (MESH_THREAD_CPU): per-thread CPU cycle deltas from held thread handles, taken on the sampler thread.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshThreadCpuSampler measures how much CPU each thread of this process used between two calls to
    /// <see cref="Sample"/>, grouped by <see cref="ServerMeshThreadCpu.Classify"/>, beside the process total. It counts
    /// CPU cycles (QueryThreadCycleTime, QueryProcessCycleTime), converted to milliseconds with a time-stamp counter rate
    /// that <see cref="CalibrateCycleClock"/> measures; without a calibration it counts scheduler ticks (GetThreadTimes,
    /// GetProcessTimes), which are only 15.625 ms fine. It finds this process's threads in a system thread snapshot,
    /// taken on the first pass and then only every few windows because it walks every thread on the machine, and in the
    /// passes between it reads the handles it already holds. It holds a handle to every thread it has found until a
    /// snapshot no longer lists it, so the id cannot be reused under it and the final count of a thread that exited is
    /// still read. A thread born between snapshots is counted from its creation at the snapshot that finds it; one born
    /// and ended between two snapshots is in the process total only. It belongs to one thread (the sampler thread): it
    /// never logs and never touches Unity; each kind of Win32 failure is described once in the queue it was given, for
    /// the main thread to log. Windows only.
    /// </summary>
    public sealed class ServerMeshThreadCpuSampler : IDisposable
    {
        /// <summary>CalibrationSpins is how many busy spins one calibration attempt times.</summary>
        public const int CalibrationSpins = 5;

        /// <summary>CalibrationSpinMs is how long each calibration spin runs, in milliseconds of wall time.</summary>
        public const int CalibrationSpinMs = 20;

        /// <summary>CalibrationAttempts is how many sets of spins calibration tries before it falls back to scheduler ticks.</summary>
        public const int CalibrationAttempts = 4;

        /// <summary>SpinAgreement is how far, as a fraction, a spin may read below the fastest one for a calibration to hold.</summary>
        public const double SpinAgreement = 0.02;

        /// <summary>DescriptionSnapshots is how many snapshots, from the one that finds a thread, read its description while it is empty.</summary>
        public const int DescriptionSnapshots = 3;

        private const string ProbeThreadName = "Mesh thread name probe";
        private const int ProbeTimeoutMs = 2000;
        private const int CalibrationPauseMs = 50;
        private const double HundredNsPerMs = 10000.0;
        private const double MsPerSecond = 1000.0;
        private const double HzPerGHz = 1e9;

        /// <summary>A thread found in a snapshot: its open handle, cached description and CPU count at the last pass.</summary>
        private sealed class ServerMeshWatchedThread
        {
            public readonly uint id;
            public readonly IntPtr handle;
            public string description = string.Empty;
            public int descriptionReads;
            public long count; // cycles, or 100 ns units when counting scheduler ticks; 0 until first read, so a new thread counts from creation
            public long seenPass; // the pass of the latest snapshot that listed it

            public ServerMeshWatchedThread(uint id, IntPtr handle)
            {
                this.id = id;
                this.handle = handle;
            }
        }

        private readonly uint _processId;
        private readonly uint _mainThreadId;
        private readonly uint _samplerThreadId;
        private readonly int _snapshotEveryWindows;
        private readonly ConcurrentQueue<string> _problems;
        private readonly Dictionary<uint, ServerMeshWatchedThread> _threads = new Dictionary<uint, ServerMeshWatchedThread>();
        private readonly List<ServerMeshWatchedThread> _departed = new List<ServerMeshWatchedThread>();
        private readonly long[] _classCounts = new long[ServerMeshThreadCpuReading.ClassCount];
        private readonly double[] _classCpuMs = new double[ServerMeshThreadCpuReading.ClassCount];
        private double _countsPerMs = HundredNsPerMs;
        private long _samplerCount;
        private long _pass;
        private int _windowsSinceSnapshot;
        private int _threadsAtSnapshot;
        private bool _hasBaseline;
        private long _baselineStamp;
        private long _baselineProcessCount;
        private bool _descriptionsUnavailable;
        private bool _disposed;

        // One report per kind of failure: a failing call would otherwise repeat for every thread on every pass.
        private bool _reportedSnapshot;
        private bool _reportedWalk;
        private bool _reportedOpen;
        private bool _reportedOwner;
        private bool _reportedThreadCpu;
        private bool _reportedDescription;
        private bool _reportedFree;
        private bool _reportedClose;
        private bool _reportedProcess;
        private bool _reportedProbe;
        private bool _reportedCalibration;

        /// <summary>
        /// ServerMeshThreadCpuSampler samples process <paramref name="processId"/>, filing thread
        /// <paramref name="mainThreadId"/> as main and thread <paramref name="samplerThreadId"/> (the caller's) as other,
        /// and takes a system thread snapshot on the first pass and then every <paramref name="snapshotEveryWindows"/>
        /// windows; failures are described in <paramref name="problems"/>.
        /// </summary>
        public ServerMeshThreadCpuSampler(uint processId, uint mainThreadId, uint samplerThreadId, int snapshotEveryWindows, ConcurrentQueue<string> problems)
        {
            if (processId == 0)
                throw new ArgumentOutOfRangeException(nameof(processId), processId, "0 is the idle process, never this one");
            if (mainThreadId == 0)
                throw new ArgumentOutOfRangeException(nameof(mainThreadId), mainThreadId, "0 is not a thread id");
            if (samplerThreadId == 0)
                throw new ArgumentOutOfRangeException(nameof(samplerThreadId), samplerThreadId, "0 is not a thread id");
            if (snapshotEveryWindows < 1)
                throw new ArgumentOutOfRangeException(nameof(snapshotEveryWindows), snapshotEveryWindows, "a snapshot is taken every window or less often");
            _processId = processId;
            _mainThreadId = mainThreadId;
            _samplerThreadId = samplerThreadId;
            _snapshotEveryWindows = snapshotEveryWindows;
            _problems = problems ?? throw new ArgumentNullException(nameof(problems));
        }

        /// <summary>
        /// NamesPreStart is the result of <see cref="ProbePreStartNames"/> (<see cref="ServerMeshThreadCpuReading.NamesUnavailable"/>
        /// until it has run).
        /// </summary>
        public string NamesPreStart { get; private set; } = ServerMeshThreadCpuReading.NamesUnavailable;

        /// <summary>
        /// CyclesPerSecond is the time-stamp counter rate <see cref="CalibrateCycleClock"/> measured, or 0 while the sampler
        /// counts scheduler ticks (before calibration, or after it failed).
        /// </summary>
        public double CyclesPerSecond { get; private set; }

        /// <summary>
        /// CycleRateFromSpins is the cycle rate a set of busy spins measured, spin i having used
        /// <paramref name="spinCycles"/>[i] cycles of the spinning thread in <paramref name="spinSeconds"/>[i] of wall time.
        /// It is the fastest spin's rate, since a spin the thread was preempted in reads low, and it holds only when every
        /// spin reads within <see cref="SpinAgreement"/> of it; otherwise, or when a spin has no cycles or no time, it is 0.
        /// </summary>
        public static double CycleRateFromSpins(long[] spinCycles, double[] spinSeconds)
        {
            if (spinCycles == null)
                throw new ArgumentNullException(nameof(spinCycles));
            if (spinSeconds == null)
                throw new ArgumentNullException(nameof(spinSeconds));
            if (spinCycles.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(spinCycles), spinCycles.Length, "at least one spin is needed");
            if (spinSeconds.Length != spinCycles.Length)
                throw new ArgumentOutOfRangeException(nameof(spinSeconds), spinSeconds.Length, "one wall time per spin is needed");
            double fastest = 0.0;
            for (int i = 0; i < spinCycles.Length; ++i)
            {
                if (spinCycles[i] <= 0 || !(spinSeconds[i] > 0.0) || double.IsInfinity(spinSeconds[i]))
                    return 0.0;
                fastest = Math.Max(fastest, spinCycles[i] / spinSeconds[i]);
            }
            double slowestAllowed = fastest * (1.0 - SpinAgreement);
            for (int i = 0; i < spinCycles.Length; ++i)
            {
                if (spinCycles[i] / spinSeconds[i] < slowestAllowed)
                    return 0.0;
            }
            return fastest;
        }

        /// <summary>
        /// ProbePreStartNames starts a short-lived thread named before Start, exactly as LiteNetLib names its LogicThread
        /// and ReceiveThread, and records in <see cref="NamesPreStart"/> whether that name reaches its Windows description.
        /// </summary>
        public void ProbePreStartNames()
        {
            uint probeId = 0;
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Thread probe = new Thread(() =>
            {
                probeId = ServerMeshThreadCpuNative.GetCurrentThreadId();
                ready.Set();
                release.Wait(ProbeTimeoutMs);
            })
            {
                Name = ProbeThreadName,
                IsBackground = true
            };
            probe.Start();
            try
            {
                if (ready.Wait(ProbeTimeoutMs))
                {
                    NamesPreStart = ReadProbeName(probeId);
                }
                else
                {
                    Report(ref _reportedProbe, FormattableString.Invariant($"Thread CPU: the name probe thread did not start within {ProbeTimeoutMs} ms; names pre-start=unavailable"));
                    NamesPreStart = ServerMeshThreadCpuReading.NamesUnavailable;
                }
            }
            finally
            {
                release.Set();
                if (probe.Join(ProbeTimeoutMs))
                {
                    ready.Dispose();
                    release.Dispose();
                }
                else
                {
                    Report(ref _reportedProbe, FormattableString.Invariant($"Thread CPU: the name probe thread did not end within {ProbeTimeoutMs} ms"));
                }
            }
        }

        /// <summary>
        /// CalibrateCycleClock measures the rate CPU cycle counts advance at by timing <see cref="CalibrationSpins"/> busy
        /// spins of <see cref="CalibrationSpinMs"/> ms on the calling thread (the sampler thread), up to
        /// <see cref="CalibrationAttempts"/> times until the spins agree (<see cref="CycleRateFromSpins"/>); the thread
        /// runs at highest priority meanwhile (about 100 ms), then gets its own back. It returns true and counts cycles
        /// from then on, or reports why and returns false, leaving the sampler on scheduler ticks. Call it before the
        /// first <see cref="Sample"/>: a window cannot mix the two.
        /// </summary>
        public bool CalibrateCycleClock()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServerMeshThreadCpuSampler));
            if (_pass > 0)
                throw new InvalidOperationException("the cycle clock is calibrated before the first pass, so no window mixes cycles and ticks");
            IntPtr self = ServerMeshThreadCpuNative.GetCurrentThread();
            long spinTicks = Stopwatch.Frequency * CalibrationSpinMs / 1000;
            long[] cycles = new long[CalibrationSpins];
            double[] seconds = new double[CalibrationSpins];
            double slowest = double.MaxValue;
            double fastest = 0.0;
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
                    for (int i = 0; i < CalibrationSpins; ++i)
                    {
                        if (!TimeSpin(self, spinTicks, out cycles[i], out seconds[i]))
                            return false;
                        double rate = seconds[i] > 0.0 ? cycles[i] / seconds[i] : 0.0;
                        slowest = Math.Min(slowest, rate);
                        fastest = Math.Max(fastest, rate);
                    }
                    double agreed = CycleRateFromSpins(cycles, seconds);
                    if (agreed > 0.0)
                    {
                        CyclesPerSecond = agreed;
                        _countsPerMs = agreed / MsPerSecond;
                        return true;
                    }
                }
            }
            finally
            {
                current.Priority = priority;
            }
            Report(ref _reportedCalibration, FormattableString.Invariant($"Thread CPU: the cycle clock calibration disagreed in {CalibrationAttempts} attempts of {CalibrationSpins} spins, which read {Math.Max(0.0, slowest) / HzPerGHz:0.000} to {fastest / HzPerGHz:0.000} GHz, more than {SpinAgreement * 100.0:0}% apart; CPU is counted in scheduler ticks, clock ticks"));
            return false;
        }

        /// <summary>
        /// Sample returns the CPU used since the previous pass, or null for the first pass (the baseline, retried until a
        /// whole snapshot has been read). A snapshot pass (the first, then every few windows, and the next one after a
        /// snapshot failed) finds new threads and closes departed ones; every pass reads each held thread's count.
        /// </summary>
        public ServerMeshThreadCpuReading Sample()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServerMeshThreadCpuSampler));
            ++_pass;
            ++_windowsSinceSnapshot;
            Array.Clear(_classCounts, 0, _classCounts.Length);
            _samplerCount = 0;
            bool snapshotRead = false;
            if (!_hasBaseline || _windowsSinceSnapshot >= _snapshotEveryWindows)
                snapshotRead = Walk();
            long stamp = Stopwatch.GetTimestamp(); // after the walk, so every window spans count read to count read
            CountHeld();
            long processCount = ReadProcessCount();
            if (snapshotRead)
            {
                CloseDeparted();
                _windowsSinceSnapshot = 0;
            }
            if (!_hasBaseline)
            {
                if (!snapshotRead)
                    return null; // the threads read so far keep their counts as baselines; the next pass walks again
                _hasBaseline = true;
                _baselineStamp = stamp;
                _baselineProcessCount = processCount;
                return null;
            }
            double seconds = (stamp - _baselineStamp) / (double)Stopwatch.Frequency;
            long processDelta = Math.Max(0L, processCount - _baselineProcessCount);
            _baselineStamp = stamp;
            _baselineProcessCount = processCount;
            for (int i = 0; i < _classCounts.Length; ++i)
                _classCpuMs[i] = _classCounts[i] / _countsPerMs;
            return new ServerMeshThreadCpuReading(seconds, _classCpuMs, processDelta / _countsPerMs, _threadsAtSnapshot, _samplerCount / _countsPerMs, CyclesPerSecond, NamesPreStart);
        }

        /// <summary>Dispose closes every thread handle the sampler holds.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (KeyValuePair<uint, ServerMeshWatchedThread> kv in _threads)
                CloseReported(kv.Value.handle, kv.Key);
            _threads.Clear();
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

        // Returns true when the whole snapshot was walked; then threads it did not list have exited.
        private bool Walk()
        {
            IntPtr snapshot = ServerMeshThreadCpuNative.CreateToolhelp32Snapshot(ServerMeshThreadCpuNative.TH32CS_SNAPTHREAD, 0);
            if (snapshot == ServerMeshThreadCpuNative.INVALID_HANDLE_VALUE)
            {
                Report(ref _reportedSnapshot, FormattableString.Invariant($"Thread CPU: CreateToolhelp32Snapshot failed with Win32 error {Marshal.GetLastWin32Error()}; the threads already found are still counted and the snapshot is retried next window"));
                return false;
            }
            try
            {
                int threads = 0;
                ServerMeshThreadCpuNative.ServerMeshThreadEntry32 entry = ServerMeshThreadCpuNative.ServerMeshThreadEntry32.Create();
                bool more = ServerMeshThreadCpuNative.Thread32First(snapshot, ref entry);
                while (more)
                {
                    if (entry.th32OwnerProcessID == _processId)
                    {
                        ++threads;
                        Visit(entry.th32ThreadID);
                    }
                    more = ServerMeshThreadCpuNative.Thread32Next(snapshot, ref entry);
                }
                int error = Marshal.GetLastWin32Error(); // the failed Thread32First/Next is the last call that set it
                if (error != ServerMeshThreadCpuNative.ERROR_NO_MORE_FILES)
                {
                    Report(ref _reportedWalk, FormattableString.Invariant($"Thread CPU: walking the thread snapshot stopped with Win32 error {error}; threads not reached are found by a later snapshot, retried next window"));
                    return false;
                }
                _threadsAtSnapshot = threads;
                return true;
            }
            finally
            {
                CloseReported(snapshot, 0);
            }
        }

        private void Visit(uint threadId)
        {
            if (!_threads.TryGetValue(threadId, out ServerMeshWatchedThread thread))
            {
                thread = Open(threadId);
                if (thread == null)
                    return;
                _threads.Add(threadId, thread);
            }
            thread.seenPass = _pass;
            // Unity may name a thread just after creating it, so an empty description is read again at the next few
            // snapshots; a name set before Start never arrives later (names pre-start), so the reads then stop.
            if (thread.description.Length > 0 || thread.descriptionReads >= DescriptionSnapshots)
                return;
            ++thread.descriptionReads;
            if (TryReadDescription(thread.handle, threadId, out string description))
                thread.description = description;
        }

        private ServerMeshWatchedThread Open(uint threadId)
        {
            IntPtr handle = ServerMeshThreadCpuNative.OpenThread(ServerMeshThreadCpuNative.THREAD_QUERY_LIMITED_INFORMATION, false, threadId);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ServerMeshThreadCpuNative.ERROR_INVALID_PARAMETER) // that one means it exited after the snapshot
                    Report(ref _reportedOpen, FormattableString.Invariant($"Thread CPU: OpenThread failed with Win32 error {error} for thread {threadId}; its CPU is left out"));
                return null;
            }
            uint owner = ServerMeshThreadCpuNative.GetProcessIdOfThread(handle);
            if (owner == 0)
            {
                Report(ref _reportedOwner, FormattableString.Invariant($"Thread CPU: GetProcessIdOfThread failed with Win32 error {Marshal.GetLastWin32Error()} for thread {threadId}; it is counted without that check"));
            }
            else if (owner != _processId)
            {
                CloseReported(handle, threadId); // it exited after the snapshot and another process's new thread took its id
                return null;
            }
            return new ServerMeshWatchedThread(threadId, handle);
        }

        private void CountHeld()
        {
            foreach (KeyValuePair<uint, ServerMeshWatchedThread> kv in _threads)
            {
                ServerMeshWatchedThread thread = kv.Value;
                if (!TryReadCount(thread, out long count))
                    continue;
                long delta = Math.Max(0L, count - thread.count);
                thread.count = count;
                _classCounts[(int)ClassOf(thread)] += delta;
                if (thread.id == _samplerThreadId)
                    _samplerCount += delta;
            }
        }

        private void CloseDeparted()
        {
            _departed.Clear();
            foreach (KeyValuePair<uint, ServerMeshWatchedThread> kv in _threads)
            {
                if (kv.Value.seenPass != _pass)
                    _departed.Add(kv.Value);
            }
            foreach (ServerMeshWatchedThread thread in _departed)
            {
                CloseReported(thread.handle, thread.id); // its final count was read through this handle already
                _threads.Remove(thread.id);
            }
            _departed.Clear();
        }

        private bool TryReadCount(ServerMeshWatchedThread thread, out long count)
        {
            if (CyclesPerSecond > 0.0)
            {
                if (ServerMeshThreadCpuNative.QueryThreadCycleTime(thread.handle, out count))
                    return true;
                Report(ref _reportedThreadCpu, FormattableString.Invariant($"Thread CPU: QueryThreadCycleTime failed with Win32 error {Marshal.GetLastWin32Error()} for thread {thread.id}; its CPU is counted when a read succeeds"));
                return false;
            }
            if (ServerMeshThreadCpuNative.GetThreadTimes(thread.handle, out _, out _, out long kernel, out long user))
            {
                count = kernel + user;
                return true;
            }
            count = 0;
            Report(ref _reportedThreadCpu, FormattableString.Invariant($"Thread CPU: GetThreadTimes failed with Win32 error {Marshal.GetLastWin32Error()} for thread {thread.id}; its CPU is counted when a read succeeds"));
            return false;
        }

        private ServerMeshThreadClass ClassOf(ServerMeshWatchedThread thread)
        {
            ServerMeshThreadClass threadClass = ServerMeshThreadCpu.Classify(thread.description, thread.id, _mainThreadId);
            // The sampler files itself as other whatever its description reads, so its own cost never looks like the game's.
            return threadClass != ServerMeshThreadClass.Main && thread.id == _samplerThreadId ? ServerMeshThreadClass.Other : threadClass;
        }

        private long ReadProcessCount()
        {
            IntPtr process = ServerMeshThreadCpuNative.GetCurrentProcess();
            if (CyclesPerSecond > 0.0)
            {
                if (ServerMeshThreadCpuNative.QueryProcessCycleTime(process, out long cycles))
                    return cycles;
                Report(ref _reportedProcess, FormattableString.Invariant($"Thread CPU: QueryProcessCycleTime failed with Win32 error {Marshal.GetLastWin32Error()}; process CPU reads 0 for that window"));
                return _baselineProcessCount;
            }
            if (ServerMeshThreadCpuNative.GetProcessTimes(process, out _, out _, out long kernel, out long user))
                return kernel + user;
            Report(ref _reportedProcess, FormattableString.Invariant($"Thread CPU: GetProcessTimes failed with Win32 error {Marshal.GetLastWin32Error()}; process CPU reads 0 for that window"));
            return _baselineProcessCount;
        }

        private string ReadProbeName(uint probeId)
        {
            IntPtr handle = ServerMeshThreadCpuNative.OpenThread(ServerMeshThreadCpuNative.THREAD_QUERY_LIMITED_INFORMATION, false, probeId);
            if (handle == IntPtr.Zero)
            {
                Report(ref _reportedProbe, FormattableString.Invariant($"Thread CPU: OpenThread failed with Win32 error {Marshal.GetLastWin32Error()} for the name probe thread {probeId}; names pre-start=unavailable"));
                return ServerMeshThreadCpuReading.NamesUnavailable;
            }
            try
            {
                if (!TryReadDescription(handle, probeId, out string description))
                    return ServerMeshThreadCpuReading.NamesUnavailable;
                return description == ProbeThreadName ? ServerMeshThreadCpuReading.NamesPresent : ServerMeshThreadCpuReading.NamesAbsent;
            }
            finally
            {
                CloseReported(handle, probeId);
            }
        }

        private bool TryReadDescription(IntPtr thread, uint threadId, out string description)
        {
            description = string.Empty;
            if (_descriptionsUnavailable)
                return false;
            IntPtr buffer = IntPtr.Zero;
            int result;
            try
            {
                result = ServerMeshThreadCpuNative.GetThreadDescription(thread, out buffer);
                if (result >= 0 && buffer != IntPtr.Zero)
                    description = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            catch (EntryPointNotFoundException)
            {
                _descriptionsUnavailable = true;
                Report(ref _reportedDescription, "Thread CPU: this kernel32 has no GetThreadDescription (Windows 10 1607 or later has it); thread names are unavailable, so every thread but main and the sampler counts as unnamed");
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero && ServerMeshThreadCpuNative.LocalFree(buffer) != IntPtr.Zero)
                    Report(ref _reportedFree, FormattableString.Invariant($"Thread CPU: LocalFree of the description of thread {threadId} failed with Win32 error {Marshal.GetLastWin32Error()}"));
            }
            if (result < 0)
            {
                Report(ref _reportedDescription, FormattableString.Invariant($"Thread CPU: GetThreadDescription failed with HRESULT 0x{result:X8} for thread {threadId}; it counts as unnamed unless a later snapshot reads it"));
                return false;
            }
            return true;
        }

        private void CloseReported(IntPtr handle, uint threadId)
        {
            if (!ServerMeshThreadCpuNative.CloseHandle(handle))
                Report(ref _reportedClose, FormattableString.Invariant($"Thread CPU: CloseHandle failed with Win32 error {Marshal.GetLastWin32Error()} (thread {threadId}, 0 for the snapshot)"));
        }

        private void Report(ref bool reported, string problem)
        {
            if (reported)
                return;
            reported = true;
            _problems.Enqueue(problem + " (reported once)");
        }
    }
}
