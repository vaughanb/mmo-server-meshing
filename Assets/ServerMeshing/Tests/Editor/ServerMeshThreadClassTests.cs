// Server Meshing add-on — tests for the rig's thread CPU windows: thread classes, the Threads window line, the cycle clock calibration, and live samples.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshThreadClassTests
    {
        private const uint MainId = 1000;
        private const uint WorkerId = 2000;
        private const double SpinSeconds = 0.02;
        private const long SpinCyclesAt2994MHz = 59_880_000;
        private const int BusySpinMs = 300;

        private static IEnumerable<TestCaseData> ClassifyCases()
        {
            yield return new TestCaseData(null, WorkerId, ServerMeshThreadClass.Unnamed).SetName("Classify_NullDescription_IsUnnamed");
            yield return new TestCaseData("", WorkerId, ServerMeshThreadClass.Unnamed).SetName("Classify_EmptyDescription_IsUnnamed");
            yield return new TestCaseData("Thread Pool Worker", WorkerId, ServerMeshThreadClass.Other).SetName("Classify_ThreadPoolWorker_IsOther");
            yield return new TestCaseData("LogicThread", WorkerId, ServerMeshThreadClass.LiteNetLib).SetName("Classify_LogicThread_IsLiteNetLib");
            yield return new TestCaseData("ReceiveThread(7770)", WorkerId, ServerMeshThreadClass.LiteNetLib).SetName("Classify_ReceiveThreadWithPort_IsLiteNetLib");
            yield return new TestCaseData("Job.Worker 3", WorkerId, ServerMeshThreadClass.Jobs).SetName("Classify_JobWorker_IsJobs");
            yield return new TestCaseData("Background Job.Worker 2", WorkerId, ServerMeshThreadClass.Jobs).SetName("Classify_BackgroundJobWorker_IsJobs");
            yield return new TestCaseData("LogicThread", MainId, ServerMeshThreadClass.Main).SetName("Classify_MainIdDescribedAsLogicThread_IsMain");
            yield return new TestCaseData("", MainId, ServerMeshThreadClass.Main).SetName("Classify_MainIdWithoutDescription_IsMain");
        }

        [TestCaseSource(nameof(ClassifyCases))]
        public void Classify(string description, uint id, ServerMeshThreadClass expected)
        {
            Assert.That(ServerMeshThreadCpu.Classify(description, id, MainId), Is.EqualTo(expected));
        }

        private static TestCaseData RejectCase(TestDelegate create, Type exceptionType, string parameter, string name)
        {
            return new TestCaseData(create, exceptionType, parameter).SetName(name);
        }

        private static IEnumerable<TestCaseData> RejectCases()
        {
            double[] cpu = new double[ServerMeshThreadCpuReading.ClassCount];
            string absent = ServerMeshThreadCpuReading.NamesAbsent;
            ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
            long[] oneSpin = { SpinCyclesAt2994MHz };
            double[] oneSecond = { SpinSeconds };
            yield return RejectCase(() => new ServerMeshThreadCpuReading(0.0, cpu, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "seconds", "Reading_ZeroSeconds_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(double.NaN, cpu, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "seconds", "Reading_NaNSeconds_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(double.PositiveInfinity, cpu, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "seconds", "Reading_InfiniteSeconds_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, null, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentNullException), "classCpuMs", "Reading_NullClassCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, new double[4], 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "classCpuMs", "Reading_ClassCpuOfWrongLength_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, new[] { 0.0, 0.0, -1.0, 0.0, 0.0 }, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "classCpuMs", "Reading_NegativeClassCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, new[] { 0.0, double.NaN, 0.0, 0.0, 0.0 }, 0.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "classCpuMs", "Reading_NaNClassCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, -1.0, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "processCpuMs", "Reading_NegativeProcessCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, double.PositiveInfinity, 1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "processCpuMs", "Reading_InfiniteProcessCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, -1, 0.0, 0.0, absent), typeof(ArgumentOutOfRangeException), "threads", "Reading_NegativeThreads_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, -0.5, 0.0, absent), typeof(ArgumentOutOfRangeException), "samplerCpuMs", "Reading_NegativeSamplerCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, double.NaN, 0.0, absent), typeof(ArgumentOutOfRangeException), "samplerCpuMs", "Reading_NaNSamplerCpu_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, -2.994e9, absent), typeof(ArgumentOutOfRangeException), "cyclesPerSecond", "Reading_NegativeCycleRate_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, double.NaN, absent), typeof(ArgumentOutOfRangeException), "cyclesPerSecond", "Reading_NaNCycleRate_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, double.PositiveInfinity, absent), typeof(ArgumentOutOfRangeException), "cyclesPerSecond", "Reading_InfiniteCycleRate_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, 0.0, null), typeof(ArgumentNullException), "namesPreStart", "Reading_NullNames_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, 0.0, "maybe"), typeof(ArgumentOutOfRangeException), "namesPreStart", "Reading_UnknownNames_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuReading(5.0, cpu, 0.0, 1, 0.0, 0.0, absent).MsPerSecond((ServerMeshThreadClass)5), typeof(ArgumentOutOfRangeException), "threadClass", "Reading_MsPerSecondOfUnknownClass_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuSampler(0, MainId, WorkerId, 6, problems), typeof(ArgumentOutOfRangeException), "processId", "Sampler_ZeroProcessId_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuSampler(1, 0, WorkerId, 6, problems), typeof(ArgumentOutOfRangeException), "mainThreadId", "Sampler_ZeroMainThreadId_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuSampler(1, MainId, 0, 6, problems), typeof(ArgumentOutOfRangeException), "samplerThreadId", "Sampler_ZeroSamplerThreadId_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuSampler(1, MainId, WorkerId, 0, problems), typeof(ArgumentOutOfRangeException), "snapshotEveryWindows", "Sampler_ZeroSnapshotPeriod_Throws");
            yield return RejectCase(() => new ServerMeshThreadCpuSampler(1, MainId, WorkerId, 6, null), typeof(ArgumentNullException), "problems", "Sampler_NullProblems_Throws");
            yield return RejectCase(() => ServerMeshThreadCpuSampler.CycleRateFromSpins(null, oneSecond), typeof(ArgumentNullException), "spinCycles", "CycleRate_NullCycles_Throws");
            yield return RejectCase(() => ServerMeshThreadCpuSampler.CycleRateFromSpins(oneSpin, null), typeof(ArgumentNullException), "spinSeconds", "CycleRate_NullSeconds_Throws");
            yield return RejectCase(() => ServerMeshThreadCpuSampler.CycleRateFromSpins(new long[0], new double[0]), typeof(ArgumentOutOfRangeException), "spinCycles", "CycleRate_NoSpins_Throws");
            yield return RejectCase(() => ServerMeshThreadCpuSampler.CycleRateFromSpins(oneSpin, new[] { SpinSeconds, SpinSeconds }), typeof(ArgumentOutOfRangeException), "spinSeconds", "CycleRate_MoreTimesThanSpins_Throws");
        }

        [TestCaseSource(nameof(RejectCases))]
        public void Rejects(TestDelegate create, Type exceptionType, string parameter)
        {
            ArgumentException thrown = Assert.Catch<ArgumentException>(create);

            Assert.That(thrown, Is.TypeOf(exceptionType));
            Assert.That(thrown.ParamName, Is.EqualTo(parameter));
        }

        private static IEnumerable<TestCaseData> CycleRateCases()
        {
            const long c = SpinCyclesAt2994MHz;
            const double s = SpinSeconds;
            yield return new TestCaseData(new[] { c, 0L, c }, new[] { s, s, s }, 0.0).SetName("CycleRate_SpinWithoutCycles_IsZero");
            yield return new TestCaseData(new[] { c, -c, c }, new[] { s, s, s }, 0.0).SetName("CycleRate_SpinWithNegativeCycles_IsZero");
            yield return new TestCaseData(new[] { c, c, c }, new[] { s, 0.0, s }, 0.0).SetName("CycleRate_SpinWithoutTime_IsZero");
            yield return new TestCaseData(new[] { c, c, c }, new[] { s, double.NaN, s }, 0.0).SetName("CycleRate_SpinWithNaNTime_IsZero");
            yield return new TestCaseData(new[] { c, c, c }, new[] { s, double.PositiveInfinity, s }, 0.0).SetName("CycleRate_SpinWithInfiniteTime_IsZero");
            yield return new TestCaseData(new[] { c, c, c, c, c * 2 / 3 }, new[] { s, s, s, s, s }, 0.0).SetName("CycleRate_OnePreemptedSpin_IsZero");
            yield return new TestCaseData(new[] { c, c * 975 / 1000 }, new[] { s, s }, 0.0).SetName("CycleRate_SpinsTwoAndAHalfPercentApart_IsZero");
            yield return new TestCaseData(new[] { c }, new[] { s }, 2.994e9).SetName("CycleRate_OneSpin_IsItsRate");
            yield return new TestCaseData(new[] { c * 985 / 1000, c, c * 990 / 1000 }, new[] { s, s, s }, 2.994e9).SetName("CycleRate_SpinsWithinTwoPercent_IsTheFastest");
            yield return new TestCaseData(new[] { c, c / 2 }, new[] { s, s / 2 }, 2.994e9).SetName("CycleRate_SpinsOfDifferentLengths_CompareRates");
        }

        [TestCaseSource(nameof(CycleRateCases))]
        public void CycleRate(long[] spinCycles, double[] spinSeconds, double expected)
        {
            Assert.That(ServerMeshThreadCpuSampler.CycleRateFromSpins(spinCycles, spinSeconds), Is.EqualTo(expected).Within(1.0));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_EmptyWindowCountedInTicks_PrintsZerosAndClockTicks()
        {
            ServerMeshThreadCpuReading reading = new ServerMeshThreadCpuReading(5.0, new double[ServerMeshThreadCpuReading.ClassCount], 0.0, 0, 0.0, 0.0, ServerMeshThreadCpuReading.NamesUnavailable);

            Assert.That(reading.ToLine(), Is.EqualTo("Threads window 5.00 s: main 0.0 litenetlib 0.0 jobs 0.0 unnamed 0.0 other 0.0 total 0.0 process 0.0 ms/s | threads 0 sampler 0.0 ms/s clock ticks names pre-start=unavailable"));
            Assert.That(reading.CountsCycles, Is.False);
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_FilledWindowCountedInCycles_IsInvariantAndExact()
        {
            ServerMeshThreadCpuReading reading = new ServerMeshThreadCpuReading(5.0, new[] { 1234.5, 100.0, 250.0, 0.0, 50.0 }, 1700.0, 42, 23.1, 2.994e9, ServerMeshThreadCpuReading.NamesAbsent);

            Assert.That(reading.ToLine(), Is.EqualTo("Threads window 5.00 s: main 246.9 litenetlib 20.0 jobs 50.0 unnamed 0.0 other 10.0 total 326.9 process 340.0 ms/s | threads 42 sampler 4.6 ms/s clock cycles 2.994 GHz names pre-start=absent"));
            Assert.That(reading.CountsCycles, Is.True);
            Assert.That(reading.MsPerSecond(ServerMeshThreadClass.Main), Is.EqualTo(246.9).Within(1e-9));
            Assert.That(reading.TotalMsPerSecond, Is.EqualTo(326.9).Within(1e-9));
            Assert.That(reading.ProcessMsPerSecond, Is.EqualTo(340.0).Within(1e-9));
            Assert.That(reading.SamplerMsPerSecond, Is.EqualTo(4.62).Within(1e-9));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_FilledWindowCountedInTicks_EndsWithClockTicks()
        {
            ServerMeshThreadCpuReading reading = new ServerMeshThreadCpuReading(5.0, new[] { 500.0, 0.0, 0.0, 250.0, 15.625 }, 796.875, 7, 15.625, 0.0, ServerMeshThreadCpuReading.NamesPresent);

            Assert.That(reading.ToLine(), Is.EqualTo("Threads window 5.00 s: main 100.0 litenetlib 0.0 jobs 0.0 unnamed 50.0 other 3.1 total 153.1 process 159.4 ms/s | threads 7 sampler 3.1 ms/s clock ticks names pre-start=present"));
        }

        [Test]
        public void Reading_CallerChangesItsArray_KeepsItsOwnCopy()
        {
            double[] cpu = { 1.0, 0.0, 0.0, 0.0, 0.0 };
            ServerMeshThreadCpuReading reading = new ServerMeshThreadCpuReading(1.0, cpu, 0.0, 1, 0.0, 0.0, ServerMeshThreadCpuReading.NamesPresent);
            cpu[0] = 9999.0;

            Assert.That(reading.CpuMs(ServerMeshThreadClass.Main), Is.EqualTo(1.0));
        }

        [Test]
        public void ThreadEntry32_Layout_Is28BytesWithSizeSet()
        {
            Assert.That(Marshal.SizeOf<ServerMeshThreadCpuNative.ServerMeshThreadEntry32>(), Is.EqualTo(28));
            Assert.That(ServerMeshThreadCpuNative.ServerMeshThreadEntry32.Create().dwSize, Is.EqualTo(28u));
        }

        [Test]
        public void Sample_BusyThreadExitsMidWindow_CountsItsCpuAsMain()
        {
            Assume.That(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "the sampler reads Windows thread CPU counters");
            ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
            uint busyId = 0;
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            ManualResetEventSlim go = new ManualResetEventSlim(false);
            Thread busy = new Thread(() =>
            {
                busyId = ServerMeshThreadCpuNative.GetCurrentThreadId();
                ready.Set();
                if (!go.Wait(5000))
                    return;
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * BusySpinMs / 1000;
                while (Stopwatch.GetTimestamp() < until) { }
            }) { IsBackground = true };
            busy.Start();
            try
            {
                Assert.That(ready.Wait(5000), Is.True, "the busy thread started");
                ServerMeshThreadCpuReading reading;
                string names;
                double cyclesPerSecond;
                using (ServerMeshThreadCpuSampler sampler = new ServerMeshThreadCpuSampler(ServerMeshThreadCpuNative.GetCurrentProcessId(), busyId, ServerMeshThreadCpuNative.GetCurrentThreadId(), ServerMeshThreadCpu.SnapshotEveryWindows, problems))
                {
                    sampler.ProbePreStartNames();
                    Assert.That(sampler.CalibrateCycleClock(), Is.True, string.Join(" / ", problems));
                    Assert.That(sampler.Sample(), Is.Null, "the first pass is the baseline");
                    go.Set();
                    Assert.That(busy.Join(5000), Is.True, "the busy thread ended");
                    reading = sampler.Sample(); // a pass without a snapshot: the exited thread is read through its held handle
                    names = sampler.NamesPreStart;
                    cyclesPerSecond = sampler.CyclesPerSecond;
                }

                Assert.That(problems, Is.Empty);
                Assert.That(reading, Is.Not.Null);
                Assert.That(reading.CountsCycles, Is.True);
                Assert.That(reading.CyclesPerSecond, Is.EqualTo(cyclesPerSecond));
                Assert.That(new[] { ServerMeshThreadCpuReading.NamesPresent, ServerMeshThreadCpuReading.NamesAbsent }, Does.Contain(names));
                Assert.That(reading.Threads, Is.GreaterThanOrEqualTo(2));
                Assert.That(reading.CpuMs(ServerMeshThreadClass.Main), Is.GreaterThan(0.0).And.LessThan(BusySpinMs * 1.1), reading.ToLine());
                Assert.That(reading.ProcessMsPerSecond, Is.GreaterThanOrEqualTo(reading.MsPerSecond(ServerMeshThreadClass.Main) * 0.9), reading.ToLine());
                Assert.That(reading.ToLine(), Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("NaN").And.Not.Match(" -[0-9]"));
            }
            finally
            {
                go.Set();
                busy.Join(5000);
            }
        }

        [Test]
        public void CalibrateCycleClock_OnThisMachine_HoldsAndRestoresThePriority()
        {
            Assume.That(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "the sampler reads Windows thread CPU counters");
            ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
            uint testId = ServerMeshThreadCpuNative.GetCurrentThreadId();
            ThreadPriority before = Thread.CurrentThread.Priority;
            using (ServerMeshThreadCpuSampler sampler = new ServerMeshThreadCpuSampler(ServerMeshThreadCpuNative.GetCurrentProcessId(), testId, testId, ServerMeshThreadCpu.SnapshotEveryWindows, problems))
            {
                Assert.That(sampler.CalibrateCycleClock(), Is.True, string.Join(" / ", problems));

                Assert.That(sampler.CyclesPerSecond, Is.GreaterThan(0.0));
            }
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(before));
            Assert.That(problems, Is.Empty);
        }

        [Test]
        public void CalibrateCycleClock_AfterTheFirstPass_Throws()
        {
            Assume.That(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "the sampler reads Windows thread CPU counters");
            ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
            uint testId = ServerMeshThreadCpuNative.GetCurrentThreadId();
            using (ServerMeshThreadCpuSampler sampler = new ServerMeshThreadCpuSampler(ServerMeshThreadCpuNative.GetCurrentProcessId(), testId, testId, ServerMeshThreadCpu.SnapshotEveryWindows, problems))
            {
                Assert.That(sampler.Sample(), Is.Null, "the first pass is the baseline");

                Assert.Throws<InvalidOperationException>(() => sampler.CalibrateCycleClock());
                Assert.That(sampler.CyclesPerSecond, Is.EqualTo(0.0));
            }
            Assert.That(problems, Is.Empty);
        }

        [Test]
        public void Sample_ThreadStartedAfterBaseline_IsCountedFromCreationAtTheNextSnapshot()
        {
            Assume.That(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "the sampler reads Windows thread CPU counters");
            ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
            long spunCycles = 0;
            ManualResetEventSlim spun = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Thread logic = new Thread(() =>
            {
                Thread.CurrentThread.Name = "LogicThread"; // named after Start, so the name reaches Windows and the thread files as LiteNetLib
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * BusySpinMs / 1000;
                while (Stopwatch.GetTimestamp() < until) { }
                ServerMeshThreadCpuNative.QueryThreadCycleTime(ServerMeshThreadCpuNative.GetCurrentThread(), out spunCycles);
                spun.Set();
                release.Wait(5000);
            }) { IsBackground = true };
            uint testId = ServerMeshThreadCpuNative.GetCurrentThreadId();
            try
            {
                ServerMeshThreadCpuReading unseen;
                ServerMeshThreadCpuReading found;
                double cyclesPerSecond;
                using (ServerMeshThreadCpuSampler sampler = new ServerMeshThreadCpuSampler(ServerMeshThreadCpuNative.GetCurrentProcessId(), testId, testId, 2, problems))
                {
                    Assert.That(sampler.CalibrateCycleClock(), Is.True, string.Join(" / ", problems));
                    Assert.That(sampler.Sample(), Is.Null, "the first pass is the baseline");
                    logic.Start();
                    Assert.That(spun.Wait(5000), Is.True, "the logic thread spun");
                    unseen = sampler.Sample(); // no snapshot in this pass, so the new thread is not found yet
                    found = sampler.Sample(); // the snapshot finds it and counts all its cycles since creation
                    cyclesPerSecond = sampler.CyclesPerSecond;
                }
                double spunMs = Volatile.Read(ref spunCycles) * 1000.0 / cyclesPerSecond;

                Assert.That(problems, Is.Empty);
                Assert.That(spunMs, Is.GreaterThan(0.0));
                Assert.That(unseen.CpuMs(ServerMeshThreadClass.LiteNetLib), Is.LessThan(spunMs * 0.5), unseen.ToLine());
                Assert.That(found.CpuMs(ServerMeshThreadClass.LiteNetLib), Is.GreaterThanOrEqualTo(spunMs * 0.999), found.ToLine());
                Assert.That(found.SamplerCpuMs, Is.GreaterThan(0.0), found.ToLine());
            }
            finally
            {
                release.Set();
                if (logic.IsAlive)
                    logic.Join(5000);
            }
        }
    }
}
