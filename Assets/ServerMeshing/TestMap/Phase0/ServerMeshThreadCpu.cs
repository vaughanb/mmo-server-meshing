// Server Meshing add-on — rig thread CPU (MESH_THREAD_CPU=1): where a process's CPU goes, thread class by thread class, every 5 s (Windows only).
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshThreadClass is the bucket a thread's CPU is counted in (see <see cref="ServerMeshThreadCpu.Classify"/>).</summary>
    public enum ServerMeshThreadClass
    {
        /// <summary>Main is Unity's main thread, recognised by its id.</summary>
        Main,

        /// <summary>LiteNetLib is a "LogicThread" or "ReceiveThread(port)" whose name reached Windows.</summary>
        LiteNetLib,

        /// <summary>Jobs is a Unity job worker ("Job.Worker N" or "Background Job.Worker N").</summary>
        Jobs,

        /// <summary>Unnamed is a thread with no description: LiteNetLib's, when names set before Start do not reach Windows.</summary>
        Unnamed,

        /// <summary>Other is every other named thread, the sampler itself included. It must stay the last value.</summary>
        Other
    }

    /// <summary>
    /// ServerMeshThreadCpu logs, every <see cref="WindowSeconds"/> s, how many milliseconds of CPU per second the main
    /// thread, LiteNetLib's threads, Unity's job workers, unnamed threads and the rest used, beside the process total:
    /// <c>Threads window 5.00 s: main X litenetlib X jobs X unnamed X other X total X process X ms/s | threads N sampler X ms/s clock cycles G.GGG GHz names pre-start=absent</c>.
    /// CPU is counted in cycles, converted with a time-stamp counter rate calibrated when sampling starts; when the
    /// calibration fails the line reads <c>clock ticks</c> and CPU is counted in 15.625 ms scheduler ticks instead.
    /// "sampler" is the sampler thread's own CPU (also counted in other). One background thread samples (the system
    /// snapshot walks every thread on the machine, which would spike a main-thread frame) and hands each finished window
    /// to the main thread, which alone logs.
    /// LiteNetLib names its threads before starting them; "names pre-start" says whether such a name reaches Windows, and
    /// when it does not their CPU is in unnamed. Rig only: it installs itself when MESH_THREAD_CPU=1, never in the
    /// editor, stays inert off Windows, and starts sampling once this process is a running map server.
    /// </summary>
    public class ServerMeshThreadCpu : MonoBehaviour
    {
        /// <summary>ENV_THREAD_CPU turns the thread CPU windows on with the value "1".</summary>
        public const string ENV_THREAD_CPU = "MESH_THREAD_CPU";

        /// <summary>WindowSeconds is the length of one sampling window.</summary>
        public const float WindowSeconds = 5f;

        /// <summary>
        /// SnapshotEveryWindows is how many windows apart the system thread snapshot that finds new threads is taken; the
        /// windows between read the thread handles already held. Every window: a thread found only at a later snapshot is
        /// counted from its creation, so its earlier CPU lands in the window that finds it (a cell's neighbour links start
        /// their threads just after sampling begins; found 30 s later, one window read twice the process total). A
        /// snapshot costs about 70 ms of the sampler thread's CPU per window (the line's sampler figure).
        /// </summary>
        public const int SnapshotEveryWindows = 1;

        private const string LiteNetLibLogicThread = "LogicThread";
        private const string LiteNetLibReceiveThreadPrefix = "ReceiveThread(";
        private const string JobWorkerMarker = "Job.Worker";
        private const string SamplerThreadName = "Mesh thread CPU sampler";
        private const int StopJoinMs = 2000;

        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly ConcurrentQueue<string> _problems = new ConcurrentQueue<string>();
        private uint _mainThreadId;
        private Thread _thread;
        private bool _started;
        private ServerMeshThreadCpuReading _pending; // swapped with Interlocked by both threads
        private Exception _failure; // swapped with Interlocked by both threads

        /// <summary>
        /// Classify files a thread, first match wins: the main thread by id; LiteNetLib's "LogicThread" and
        /// "ReceiveThread(port)"; Unity's job workers (any description containing "Job.Worker"); threads with no
        /// description; everything else. Descriptions are compared ordinally, exactly as Windows returns them.
        /// </summary>
        public static ServerMeshThreadClass Classify(string description, uint id, uint mainId)
        {
            if (id == mainId)
                return ServerMeshThreadClass.Main;
            if (string.IsNullOrEmpty(description))
                return ServerMeshThreadClass.Unnamed;
            if (description == LiteNetLibLogicThread || description.StartsWith(LiteNetLibReceiveThreadPrefix, StringComparison.Ordinal))
                return ServerMeshThreadClass.LiteNetLib;
            if (description.IndexOf(JobWorkerMarker, StringComparison.Ordinal) >= 0)
                return ServerMeshThreadClass.Jobs;
            return ServerMeshThreadClass.Other;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (Environment.GetEnvironmentVariable(ENV_THREAD_CPU) != "1")
                return;
            if (Application.isEditor)
            {
                Logging.Log("Mesh", $"Thread CPU: {ENV_THREAD_CPU}=1 is not sampled in the editor; the processes it starts sample themselves");
                return;
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Logging.Log("Mesh", $"Thread CPU: {ENV_THREAD_CPU}=1 reads Windows thread times; not sampled on this platform");
                return;
            }
            if (FindFirstObjectByType<ServerMeshThreadCpu>() != null)
                return;
            uint mainThreadId = ServerMeshThreadCpuNative.GetCurrentThreadId(); // AfterSceneLoad runs on the main thread
            GameObject go = new GameObject("[Mesh] ThreadCpu");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshThreadCpu>()._mainThreadId = mainThreadId;
        }

        private void Start()
        {
            if (_mainThreadId == 0)
            {
                Logging.LogError("Mesh", $"Thread CPU: {nameof(ServerMeshThreadCpu)} on {name} was added without its installer, so the main thread id is unknown; not sampling");
                enabled = false;
            }
        }

        private void Update()
        {
            if (!_started)
            {
                if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer)
                    return;
                _started = true;
                _thread = new Thread(Run) { IsBackground = true }; // named from inside Run: only a running thread's name reaches Windows
                _thread.Start();
                Logging.Log("Mesh", FormattableString.Invariant($"Thread CPU: sampling this process's threads every {WindowSeconds:0.00} s on a background thread, finding new ones every {WindowSeconds * SnapshotEveryWindows:0} s; main thread id {_mainThreadId}"));
            }
            LogFromSampler();
            ServerMeshThreadCpuReading reading = Interlocked.Exchange(ref _pending, null);
            if (reading != null)
                Logging.Log("Mesh", reading.ToLine());
        }

        private void OnApplicationQuit()
        {
            StopSampler();
        }

        private void OnDestroy()
        {
            StopSampler();
        }

        private void StopSampler()
        {
            if (_thread == null)
                return;
            _stop.Set();
            if (_thread.Join(StopJoinMs))
                _stop.Dispose();
            else
                Logging.LogWarning("Mesh", FormattableString.Invariant($"Thread CPU: the sampler thread did not stop within {StopJoinMs} ms"));
            _thread = null;
            LogFromSampler();
        }

        private void LogFromSampler()
        {
            while (_problems.TryDequeue(out string problem))
                Logging.LogWarning("Mesh", problem);
            Exception failure = Interlocked.Exchange(ref _failure, null);
            if (failure == null)
                return;
            Logging.LogError("Mesh", "Thread CPU: the sampler thread stopped; no further Threads window lines");
            Logging.LogException("Mesh", failure);
        }

        // Runs on the sampler thread: it must never log or touch a Unity object. Results go through _pending,
        // problems through _problems and a crash through _failure, all read on the main thread.
        private void Run()
        {
            try
            {
                Thread.CurrentThread.Name = SamplerThreadName;
                using (ServerMeshThreadCpuSampler sampler = new ServerMeshThreadCpuSampler(ServerMeshThreadCpuNative.GetCurrentProcessId(), _mainThreadId, ServerMeshThreadCpuNative.GetCurrentThreadId(), SnapshotEveryWindows, _problems))
                {
                    sampler.ProbePreStartNames();
                    sampler.CalibrateCycleClock(); // on failure it reports why and the lines read clock ticks
                    sampler.Sample(); // the baseline
                    long period = (long)(WindowSeconds * System.Diagnostics.Stopwatch.Frequency);
                    long due = System.Diagnostics.Stopwatch.GetTimestamp() + period;
                    while (!_stop.Wait(MillisecondsUntil(due)))
                    {
                        ServerMeshThreadCpuReading reading = sampler.Sample();
                        if (reading != null)
                            Interlocked.Exchange(ref _pending, reading);
                        due += period;
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (due <= now)
                            due = now + period; // a stalled pass overran a whole window: restart the cadence instead of catching up
                    }
                }
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _failure, e);
            }
        }

        private static int MillisecondsUntil(long due)
        {
            long remaining = due - System.Diagnostics.Stopwatch.GetTimestamp();
            return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining * 1000 / System.Diagnostics.Stopwatch.Frequency);
        }
    }
}
