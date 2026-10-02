using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshWorkHistogramTests
    {
        [Test]
        public void Constructor_RejectsNonPositiveArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MeshWorkHistogram(0f, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MeshWorkHistogram(0.25f, 0));
        }

        [Test]
        public void Constructor_ZeroWidthAfterRounding_ThrowsNamingBinMs()
        {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshWorkHistogram(0.0001f, 10));
            Assert.That(ex.ParamName, Is.EqualTo("binMs"));
        }

        private static IEnumerable<TestCaseData> CountInBinOutOfRangeCases()
        {
            yield return new TestCaseData(false).SetName("CountInBin_MinusOne_ThrowsNamingBin");
            yield return new TestCaseData(true).SetName("CountInBin_BinCount_ThrowsNamingBin");
        }

        [TestCaseSource(nameof(CountInBinOutOfRangeCases))]
        public void CountInBin_OutOfRange(bool pastTheEnd)
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.05f, 4000);
            int bin = pastTheEnd ? histogram.BinCount : -1;

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => histogram.CountInBin(bin));
            Assert.That(ex.ParamName, Is.EqualTo("bin"));
        }

        private static IEnumerable<TestCaseData> OverflowCases()
        {
            yield return new TestCaseData(float.PositiveInfinity).SetName("Add_PositiveInfinity_GoesToOverflowWithoutThrowing");
            yield return new TestCaseData(float.MaxValue).SetName("Add_FloatMax_GoesToOverflowWithoutThrowing");
            yield return new TestCaseData(250f).SetName("Add_AboveTwoHundredMs_GoesToOverflow_AndPercentileIsTheMax");
            yield return new TestCaseData(200f).SetName("Add_AtTheRangeEnd_GoesToOverflow");
        }

        [TestCaseSource(nameof(OverflowCases))]
        public void Add_PastTheLastBin(float ms)
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.05f, 4000);

            Assert.DoesNotThrow(() => histogram.Add(ms));
            Assert.That(histogram.Overflow, Is.EqualTo(1));
            Assert.That(histogram.Count, Is.EqualTo(1));
            Assert.That(histogram.CountInBin(histogram.BinCount - 1), Is.EqualTo(0));
            Assert.That(histogram.Max, Is.EqualTo(ms));
            Assert.That(histogram.Percentile(0.99f), Is.EqualTo(ms));
        }

        private static IEnumerable<TestCaseData> PercentileCases()
        {
            yield return new TestCaseData(new float[0], 0.95f, 0f, 0f).SetName("Percentile_Empty_IsZero");
            yield return new TestCaseData(new[] { -5f }, 0.95f, 0f, 0f).SetName("Percentile_NegativeClampedToZero");
            yield return new TestCaseData(new[] { 500f }, 0.95f, 500f, 500f).SetName("Percentile_Overflow_IsMax");
            yield return new TestCaseData(new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 10f }, 0.95f, 1.25f, 1.45f)
                .SetName("Percentile_NinetyFivePercentAtOneMs");
            yield return new TestCaseData(new[] { 1f, 2f, 3f, 4f }, 1f, 4f, 2.5f).SetName("Percentile_OneHundred_IsMaxBin");
        }

        [TestCaseSource(nameof(PercentileCases))]
        public void Percentile(float[] values, float fraction, float expected, float expectedMean)
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.25f, 400);
            foreach (float v in values)
                histogram.Add(v);

            Assert.That(histogram.Percentile(fraction), Is.EqualTo(expected).Within(1e-4f));
            Assert.That(histogram.Mean, Is.EqualTo(expectedMean).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> BinCases()
        {
            yield return new TestCaseData(-1f, 0).SetName("Add_Negative_LandsInBinZero");
            yield return new TestCaseData(float.NaN, 0).SetName("Add_NaN_LandsInBinZero");
            yield return new TestCaseData(2.4999f, 49).SetName("Add_JustUnder2_5msAtFiftyMicroBins_LandsInBin49");
            yield return new TestCaseData(2.5f, 50).SetName("Add_2_5msAtFiftyMicroBins_LandsInBin50");
            yield return new TestCaseData(2.52f, 50).SetName("Add_2_52msAtFiftyMicroBins_LandsInBin50");
            yield return new TestCaseData(199.99f, 3999).SetName("Add_JustUnderTwoHundredMs_LandsInTheLastBin");
        }

        [TestCaseSource(nameof(BinCases))]
        public void Add_LandsInItsBin(float ms, int expectedBin)
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.05f, 4000);

            histogram.Add(ms);

            Assert.That(histogram.CountInBin(expectedBin), Is.EqualTo(1));
            Assert.That(histogram.Overflow, Is.EqualTo(0));
        }

        private static IEnumerable<TestCaseData> RankCases()
        {
            yield return new TestCaseData(Ramp(100, 100f), 0.99f, 100f).SetName("Percentile_NinetyNineOfOneToOneHundredMs_IsBinNinetyNinesEdge");
            yield return new TestCaseData(Ramp(100, 150f), 0.99f, 100f).SetName("Percentile_NinetyNineOfOneHundredValues_LeavesTheLargestOut");
            yield return new TestCaseData(Ramp(100, 150f), 0.95f, 96f).SetName("Percentile_NinetyFiveOfOneHundredValues_IsTheNinetyFifthValuesBin");
            yield return new TestCaseData(Ramp(100, 150f), 1f, 150f).SetName("Percentile_AllOfOneHundredValues_IsTheMax");
        }

        [TestCaseSource(nameof(RankCases))]
        public void Percentile_ExactRank(float[] values, float fraction, float expected)
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(1f, 400);
            foreach (float v in values)
                histogram.Add(v);

            Assert.That(histogram.Percentile(fraction), Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void BinMs_IsTheWidthRoundedToWholeMicroseconds()
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.0504f, 4000);

            Assert.That(histogram.BinMs, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(histogram.BinCount, Is.EqualTo(4000));
        }

        private static float[] Ramp(int count, float last)
        {
            float[] values = new float[count];
            for (int i = 0; i < count - 1; ++i)
                values[i] = i + 1;
            values[count - 1] = last;
            return values;
        }

        [Test]
        public void Reset_EmptiesEverything()
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram();
            histogram.Add(3f);
            histogram.Reset();
            Assert.That(histogram.Count, Is.EqualTo(0));
            Assert.That(histogram.Max, Is.EqualTo(0f));
            Assert.That(histogram.Percentile(0.95f), Is.EqualTo(0f));
        }

        [Test]
        public void Reset_EmptiesTheBinsAndTheOverflow()
        {
            MeshWorkHistogram histogram = new MeshWorkHistogram(0.05f, 4000);
            histogram.Add(2.52f);
            histogram.Add(500f);

            histogram.Reset();

            Assert.That(histogram.CountInBin(50), Is.EqualTo(0));
            Assert.That(histogram.Overflow, Is.EqualTo(0));
        }
    }

    public class MeshWorkMeterTests
    {
        private const long TicksPerSecond = 10_000_000;
        private const long TicksPerMs = TicksPerSecond / 1000;

        // Plays frames of (work ms, wait ms) on one continuous timeline, as the player loop would.
        private sealed class Driver
        {
            public readonly MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            private long _t = 1000 * TicksPerMs;
            private bool _started;

            public MeshWorkWindow Window(float budgetMs, int frames, float work, float wait)
            {
                if (!_started)
                {
                    meter.WorkStarted(_t);
                    _started = true;
                }
                for (int i = 0; i < frames; ++i)
                {
                    _t += (long)(work * TicksPerMs);
                    meter.WorkEnded(_t);
                    _t += (long)(wait * TicksPerMs);
                    meter.WorkStarted(_t);
                }
                return meter.TakeWindow(budgetMs, 5f);
            }

            public long Now => _t;

            public void Start()
            {
                if (_started)
                    return;
                meter.WorkStarted(_t);
                _started = true;
            }

            public void Work(double ms)
            {
                _t += (long)Math.Round(ms * TicksPerMs);
            }

            public void Tick(double kitMs)
            {
                meter.BeginTick(_t);
                Work(kitMs);
                meter.EndTick(_t);
            }

            public void AoiTick(double syncMs, double aoiMs, double moveMs, bool ranPass)
            {
                meter.BeginTick(_t);
                Work(syncMs);
                long aoiStart = _t;
                Work(aoiMs);
                meter.MarkAoiCall(aoiStart, _t, ranPass);
                Work(moveMs);
                meter.EndTick(_t);
            }

            public void Physics(int steps)
            {
                for (int i = 0; i < steps; ++i)
                    meter.NotePhysicsStep();
            }

            public void EndFrame(double waitMs)
            {
                meter.WorkEnded(_t);
                Work(waitMs);
                meter.WorkStarted(_t);
            }

            public void EndFrameAt(long nextFrameStart)
            {
                meter.WorkEnded(_t);
                _t = nextFrameStart;
                meter.WorkStarted(_t);
            }

            public void Frame(int ticks, double kitMs, double restMs, double waitMs)
            {
                Start();
                for (int i = 0; i < ticks; ++i)
                    Tick(kitMs);
                Work(restMs);
                EndFrame(waitMs);
            }
        }

        private static IEnumerable<TestCaseData> BadFrequencyCases()
        {
            yield return new TestCaseData(0L).SetName("Constructor_ZeroTicksPerSecond_ThrowsNamingIt");
            yield return new TestCaseData(-1L).SetName("Constructor_NegativeTicksPerSecond_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(BadFrequencyCases))]
        public void Constructor_NonPositiveTicksPerSecond(long ticksPerSecond)
        {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshWorkMeter(ticksPerSecond));
            Assert.That(ex.ParamName, Is.EqualTo("ticksPerSecond"));
        }

        [Test]
        public void Uncapped_ReportsUncapped()
        {
            MeshWorkWindow window = new Driver().Window(0f, 10, 3f, 0f);
            Assert.That(window.state, Is.EqualTo(MeshProbeState.Uncapped));
            Assert.That(window.Utilisation, Is.EqualTo(0f));
        }

        [Test]
        public void NeverWaiting_StaysUnknown_AndFlagsTheOverdueProof()
        {
            Driver driver = new Driver();
            MeshWorkWindow window = default;
            for (int w = 0; w < 11; ++w)
            {
                window = driver.Window(16.67f, 60, 16.67f, 0f);
                Assert.That(window.state, Is.EqualTo(MeshProbeState.Unknown), $"window {w}");
                Assert.That(driver.meter.ProofOverdue, Is.False, $"window {w}");
            }
            window = driver.Window(16.67f, 60, 16.67f, 0f);
            Assert.That(window.state, Is.EqualTo(MeshProbeState.Unknown));
            Assert.That(driver.meter.ProofOverdue, Is.True);
            Assert.That(window.saturatedShare, Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void BusyFromTheFirstFrame_IsStillProved()
        {
            // 85 % of the budget from the first frame: the limiter still waits a little every frame.
            MeshWorkWindow window = new Driver().Window(16.67f, 60, 14.17f, 2.5f);
            Assert.That(window.state, Is.EqualTo(MeshProbeState.Valid));
            Assert.That(window.Utilisation, Is.EqualTo(14.17f / 16.67f).Within(1e-3f));
        }

        [Test]
        public void SaturatedThenIdle_IsProvedOnceItWaits()
        {
            Driver driver = new Driver();
            Assert.That(driver.Window(16.67f, 60, 20f, 0f).state, Is.EqualTo(MeshProbeState.Unknown));
            Assert.That(driver.Window(16.67f, 60, 2f, 14.67f).state, Is.EqualTo(MeshProbeState.Valid));
        }

        [Test]
        public void IdleCappedCell_IsValid_AndMeasuresWorkWithoutTheWait()
        {
            MeshWorkWindow window = new Driver().Window(16.67f, 60, 2f, 14.67f);
            Assert.That(window.state, Is.EqualTo(MeshProbeState.Valid));
            Assert.That(window.frames, Is.EqualTo(60));
            Assert.That(window.workMeanMs, Is.EqualTo(2f).Within(0.01f));
            Assert.That(window.waitMeanMs, Is.EqualTo(14.67f).Within(0.01f));
            Assert.That(window.periodMeanMs, Is.EqualTo(16.67f).Within(0.01f));
            Assert.That(window.Utilisation, Is.EqualTo(2f / 16.67f).Within(1e-3f));
            Assert.That(window.saturatedShare, Is.EqualTo(0f));
        }

        [Test]
        public void ValidProbe_StaysValidUnderLoad()
        {
            Driver driver = new Driver();
            driver.Window(16.67f, 60, 2f, 14.67f);
            MeshWorkWindow loaded = driver.Window(16.67f, 60, 16f, 0.1f);
            Assert.That(loaded.state, Is.EqualTo(MeshProbeState.Valid));
            Assert.That(loaded.saturatedShare, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(loaded.workMeanMs, Is.EqualTo(16f).Within(0.01f));
        }

        [Test]
        public void MissingStamps_MakeTheProbeInvalid()
        {
            // The end-of-work stamp fires only every other frame (a runner the loop skipped): the counts disagree.
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.WorkStarted(t);
            MeshWorkWindow window = default;
            for (int w = 0; w < 3; ++w)
            {
                for (int i = 0; i < 60; ++i)
                {
                    if (i % 2 == 0)
                        meter.WorkEnded(t + 2 * TicksPerMs);
                    t += 16 * TicksPerMs;
                    meter.WorkStarted(t);
                }
                window = meter.TakeWindow(16.67f, 5f);
            }
            Assert.That(window.state, Is.EqualTo(MeshProbeState.Invalid));
        }

        [Test]
        public void InvalidProbe_StaysInvalid()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.WorkStarted(t);
            for (int w = 0; w < 3; ++w)
            {
                for (int i = 0; i < 60; ++i)
                {
                    if (i % 2 == 0)
                        meter.WorkEnded(t + 2 * TicksPerMs);
                    t += 16 * TicksPerMs;
                    meter.WorkStarted(t);
                }
                meter.TakeWindow(16.67f, 5f);
            }
            for (int i = 0; i < 60; ++i)
            {
                meter.WorkEnded(t + 2 * TicksPerMs);
                t += 16 * TicksPerMs;
                meter.WorkStarted(t);
            }
            Assert.That(meter.TakeWindow(16.67f, 5f).state, Is.EqualTo(MeshProbeState.Invalid));
        }

        [Test]
        public void TickSchedule_NonPositiveArguments_ThrowNamingThem()
        {
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => new MeshTickScheduleCopy(0, TicksPerSecond)).ParamName, Is.EqualTo("tickHz"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => new MeshTickScheduleCopy(20, 0)).ParamName, Is.EqualTo("frequency"));
        }

        [Test]
        public void TickSchedule_LongGap_FiresFiveAndDropsTheBacklog()
        {
            MeshTickScheduleCopy schedule = new MeshTickScheduleCopy(20, TicksPerSecond);

            Assert.That(schedule.PeriodTicks, Is.EqualTo(500_000));
            Assert.That(schedule.Update(3_000_000), Is.EqualTo(5));
            Assert.That(schedule.Update(499_999), Is.EqualTo(0));
            Assert.That(schedule.Update(1), Is.EqualTo(1));
        }

        private static IEnumerable<TestCaseData> NothingMeasuredCases()
        {
            yield return new TestCaseData(double.NaN, 20, 0f, 0).SetName("TakeTickWindow_NaNSeconds_ExpectsNothing");
            yield return new TestCaseData(-1d, 20, 0f, 0).SetName("TakeTickWindow_NegativeSeconds_ExpectsNothing");
            yield return new TestCaseData(double.PositiveInfinity, 20, 0f, 0).SetName("TakeTickWindow_InfiniteSeconds_ExpectsNothing");
            yield return new TestCaseData(5d, 0, 5f, 0).SetName("TakeTickWindow_NoTickRate_ExpectsNothing");
            yield return new TestCaseData(0d, 0, 0f, 0).SetName("TakeTickWindow_NoTimeAndNoRate_IsAllZeros");
            yield return new TestCaseData(5d, 20, 5f, 100).SetName("TakeTickWindow_NoTicksInFiveSeconds_DropsAllOfThem");
        }

        [TestCaseSource(nameof(NothingMeasuredCases))]
        public void TakeTickWindow_NothingMeasured(double seconds, int tickHz, float expectedSeconds, int expected)
        {
            MeshTickWindow window = new MeshWorkMeter(TicksPerSecond).TakeTickWindow(seconds, tickHz, 0f);

            Assert.That(window.seconds, Is.EqualTo(expectedSeconds));
            Assert.That(window.expected, Is.EqualTo(expected));
            Assert.That(window.dropped, Is.EqualTo(expected));
            foreach (FieldInfo field in typeof(MeshTickWindow).GetFields())
            {
                if (field.Name == nameof(MeshTickWindow.seconds) || field.Name == nameof(MeshTickWindow.tickHz)
                    || field.Name == nameof(MeshTickWindow.expected) || field.Name == nameof(MeshTickWindow.dropped))
                    continue;
                if (field.FieldType == typeof(float))
                    Assert.That((float)field.GetValue(window), Is.EqualTo(0f), field.Name);
                else if (field.FieldType == typeof(int))
                    Assert.That((int)field.GetValue(window), Is.EqualTo(0), field.Name);
            }
            Assert.That(window.bins, Is.Null);
            Assert.That(window.ToLine(), Does.Not.Contain("NaN").And.Not.Contain("Infinity"));
        }

        [Test]
        public void EndTick_WithoutAnOpenTick_IsIgnored()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);

            meter.EndTick(1000 * TicksPerMs);
            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.unclosed, window.tickMeanMs, window.sendSamples), Is.EqualTo((0, 0, 0f, 0)));
        }

        [Test]
        public void BeginTick_WhileATickIsOpen_CountsItAsUnclosed()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;

            meter.BeginTick(t);
            meter.BeginTick(t + 2 * TicksPerMs);
            meter.EndTick(t + 3 * TicksPerMs);
            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.unclosed), Is.EqualTo((2, 1)));
            Assert.That(window.tickMeanMs, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(window.tickMaxMs, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void MarkAoiCall_OutsideATick_IsCountedAsOutside()
        {
            Driver driver = new Driver();
            driver.meter.MarkAoiCall(driver.Now, driver.Now + TicksPerMs, true);
            driver.Frame(1, 1.0, 0.0, 10.0);
            driver.meter.MarkAoiCall(driver.Now, driver.Now + TicksPerMs, true);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.aoiOutsideTick, Is.EqualTo(2));
            Assert.That((window.aoiPasses, window.aoiFrames, window.partsTicks), Is.EqualTo((0, 0, 0)));
        }

        private static IEnumerable<TestCaseData> DroppedCases()
        {
            yield return new TestCaseData(90, 10).SetName("TakeTickWindow_NinetyOfAHundredTicks_DropsTen");
            yield return new TestCaseData(100, 0).SetName("TakeTickWindow_AllHundredTicks_DropsNone");
            yield return new TestCaseData(101, 0).SetName("TakeTickWindow_OneTickMoreThanDue_DropsNone");
        }

        [TestCaseSource(nameof(DroppedCases))]
        public void TakeTickWindow_Dropped_IsExpectedMinusActual(int ticks, int dropped)
        {
            Driver driver = new Driver();
            for (int i = 0; i < ticks; ++i)
                driver.Frame(1, 1.0, 0.0, 30.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.tickHz, window.ticks, window.expected, window.dropped), Is.EqualTo((20, ticks, 100, dropped)));
        }

        [Test]
        public void TickWindow_ThirtyFpsAtTwentyHertz_TicksLandOnTwoFramesOfThree()
        {
            const long framePeriod = 333_334;
            Driver driver = new Driver();
            MeshTickScheduleCopy schedule = new MeshTickScheduleCopy(20, TicksPerSecond);
            driver.Start();
            for (int frame = 0; frame < 150; ++frame)
            {
                long frameStart = driver.Now;
                int ticks = schedule.Update(framePeriod);
                for (int i = 0; i < ticks; ++i)
                    driver.Tick(1.0);
                driver.Work(2.0);
                driver.EndFrameAt(frameStart + framePeriod);
            }

            MeshTickWindow window = driver.meter.TakeTickWindow(150 * framePeriod / (double)TicksPerSecond, 20, 1000f / 30f);

            Assert.That((window.ticks, window.expected, window.dropped, window.capFrames), Is.EqualTo((100, 100, 0, 0)));
            Assert.That((window.frames, window.frames0, window.frames1, window.frames2Plus), Is.EqualTo((150, 50, 100, 0)));
            Assert.That(window.tickFrameMeanMs, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(window.otherFrameMeanMs, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void TickWindow_TwoTicksInOneFrame_IsATwoPlusFrame()
        {
            Driver driver = new Driver();
            driver.Frame(2, 1.0, 1.0, 10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.frames1, window.frames2Plus, window.capFrames), Is.EqualTo((2, 0, 1, 0)));
        }

        [Test]
        public void TickWindow_FiveTicksAfterALongGap_IsACapFrame()
        {
            Driver driver = new Driver();
            MeshTickScheduleCopy schedule = new MeshTickScheduleCopy(20, TicksPerSecond);
            driver.Frame(schedule.Update(3_000_000), 1.0, 1.0, 10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.frames2Plus, window.capFrames, window.dropped), Is.EqualTo((5, 1, 1, 95)));
        }

        [Test]
        public void TickWindow_TickAndOtherFrames_GetSeparateP95()
        {
            Driver driver = new Driver();
            for (int i = 0; i < 19; ++i)
                driver.Frame(1, 1.0, 3.0, 10.0);
            driver.Frame(1, 1.0, 19.0, 10.0);
            for (int i = 0; i < 20; ++i)
                driver.Frame(0, 0.0, 1.0, 10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.frames0, window.frames1), Is.EqualTo((20, 20)));
            Assert.That(window.tickFrameP95Ms, Is.EqualTo(4f).Within(0.051f));
            Assert.That(window.tickFrameMaxMs, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(window.tickFrameMeanMs, Is.EqualTo(4.8f).Within(1e-4f));
            Assert.That(window.otherFrameP95Ms, Is.EqualTo(1f).Within(0.051f));
            Assert.That(window.otherFrameMeanMs, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void TickWindow_AoiPassFrames_AreLeftOutOfTheNonAoiP95_AndCounted()
        {
            Driver driver = new Driver();
            driver.Start();
            for (int i = 0; i < 19; ++i)
                driver.Frame(1, 1.0, 3.0, 10.0);
            driver.AoiTick(0.5, 1.0, 0.5, false);
            driver.Work(2.0);
            driver.EndFrame(10.0);
            for (int i = 0; i < 2; ++i)
            {
                driver.AoiTick(0.5, 1.0, 0.5, true);
                driver.Work(10.0);
                driver.EndFrame(10.0);
            }
            driver.AoiTick(0.5, 1.0, 0.5, true);
            driver.AoiTick(0.5, 1.0, 0.5, true);
            driver.Work(8.0);
            driver.EndFrame(10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.aoiPasses, window.aoiFrames, window.frames1, window.frames2Plus), Is.EqualTo((4, 3, 22, 1)));
            Assert.That(window.nonAoiTickFrameP95Ms, Is.EqualTo(4f).Within(0.051f));
            Assert.That(window.nonAoiTickFrameP99Ms, Is.EqualTo(4f).Within(0.051f));
            Assert.That(window.aoiFrameMeanMs, Is.EqualTo(12f).Within(1e-4f));
            Assert.That(window.aoiFrameMaxMs, Is.EqualTo(12f).Within(1e-4f));
            Assert.That(window.tickFrameP99Ms, Is.EqualTo(12f).Within(0.051f));
        }

        private static IEnumerable<TestCaseData> NominalBudgetCases()
        {
            yield return new TestCaseData(float.NaN, 0f).SetName("TakeTickWindow_NaNBudget_IsZero");
            yield return new TestCaseData(-1f, 0f).SetName("TakeTickWindow_NegativeBudget_IsZero");
            yield return new TestCaseData(float.PositiveInfinity, 0f).SetName("TakeTickWindow_InfiniteBudget_IsZero");
            yield return new TestCaseData(33.33f, 33.33f).SetName("TakeTickWindow_NominalBudget_FlowsThrough");
        }

        [TestCaseSource(nameof(NominalBudgetCases))]
        public void TakeTickWindow_NominalBudget(float budgetMs, float expected)
        {
            MeshTickWindow window = new MeshWorkMeter(TicksPerSecond).TakeTickWindow(5d, 20, budgetMs);

            Assert.That(window.nominalBudgetMs, Is.EqualTo(expected));
        }

        [Test]
        public void TickWindow_PhysicsSteps_AreCountedPerFrame()
        {
            Driver driver = new Driver();
            driver.Start();
            foreach (int steps in new[] { 1, 2, 3, 0 })
            {
                driver.Physics(steps);
                driver.Frame(0, 0.0, 1.0, 10.0);
            }

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.physicsSteps, window.physicsMaxPerFrame, window.physics2PlusFrames, window.frames), Is.EqualTo((6, 3, 2, 4)));
            Assert.That(window.physicsPerSecond, Is.EqualTo(1.2f).Within(1e-4f));
            Assert.That(window.physicsMeanPerFrame, Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void TickWindow_FirstFrameBeforeAnyWorkStart_LeaksNothingIntoTheNextFrame()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.NotePhysicsStep();
            meter.NotePhysicsStep();
            meter.BeginTick(t);
            meter.EndTick(t + TicksPerMs);
            meter.WorkEnded(t + 2 * TicksPerMs);
            meter.WorkStarted(t + 10 * TicksPerMs);
            meter.WorkEnded(t + 11 * TicksPerMs);

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.frames, window.frames0, window.frames1, window.physicsSteps), Is.EqualTo((1, 1, 1, 0, 0)));
        }

        [Test]
        public void TickWindow_WorkPerSecond_IsTheWorkSumOverSeconds()
        {
            Driver driver = new Driver();
            for (int i = 0; i < 5; ++i)
                driver.Frame(1, 1.0, 4.0, 10.0);
            for (int i = 0; i < 5; ++i)
                driver.Frame(0, 0.0, 5.0, 10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(2d, 20, 0f);

            Assert.That(window.workMsPerSecond, Is.EqualTo(25f).Within(1e-3f));
            Assert.That(window.busyShare, Is.EqualTo(0.025f).Within(1e-6f));
        }

        private static IEnumerable<TestCaseData> PartsCases()
        {
            yield return new TestCaseData(-100L, 20_000L, 10_000L).SetName("EndTick_AoiStampsOutsideTheTick_PartsAreClampedToIt");
            yield return new TestCaseData(6_000L, 5_000L, 10_000L).SetName("EndTick_AoiEndBeforeItsStart_PartsStillSumToTheDuration");
            yield return new TestCaseData(3_337L, 5_001L, 12_345L).SetName("EndTick_AoiInsideTheTick_PartsSumToTheDuration");
        }

        [TestCaseSource(nameof(PartsCases))]
        public void EndTick_Parts_SumToTheTickDuration(long aoiStart, long aoiEnd, long end)
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            for (int i = 0; i < 3; ++i)
            {
                long start = t + i * 20_000;
                meter.BeginTick(start);
                meter.MarkAoiCall(start + aoiStart, start + aoiEnd, false);
                meter.EndTick(start + end);
            }

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.partsTicks, Is.EqualTo(3));
            Assert.That(window.syncMeanMs, Is.GreaterThanOrEqualTo(0f));
            Assert.That(window.aoiScheduleMeanMs, Is.GreaterThanOrEqualTo(0f));
            Assert.That(window.movementMeanMs, Is.GreaterThanOrEqualTo(0f));
            Assert.That(window.syncMeanMs + window.aoiScheduleMeanMs + window.movementMeanMs, Is.EqualTo(window.tickMeanMs).Within(0.001f));
            Assert.That(window.tickMeanMs, Is.EqualTo(end / (float)TicksPerMs).Within(1e-4f));
        }

        [Test]
        public void EndTick_TwoAoiCallsInOneTick_KeepTheFirstStartAndTheLastEnd()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.BeginTick(t);
            meter.MarkAoiCall(t + 3_000, t + 4_000, true);
            meter.MarkAoiCall(t + 4_500, t + 5_000, false);
            meter.EndTick(t + 10_000);

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.syncMeanMs, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(window.aoiScheduleMeanMs, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(window.movementMeanMs, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(window.aoiPasses, Is.EqualTo(1));
        }

        [Test]
        public void AddTickWork_AfterEndTick_LandsInTheSendSample_FlushedAtWorkEnded()
        {
            Driver driver = new Driver();
            driver.Start();
            driver.AoiTick(0.2, 0.3, 0.5, false);
            driver.meter.AddTickWork(0.25);
            driver.meter.AddTickWork(0.25);
            driver.Work(1.0);
            driver.EndFrame(10.0);
            driver.meter.AddTickWork(2.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.sendSamples, Is.EqualTo(2));
            Assert.That(window.sendMeanMs, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(window.sendP95Ms, Is.EqualTo(2f).Within(0.051f));
            Assert.That(window.tickMeanMs, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(window.movementMeanMs, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void AddTickWork_NegativeOrNonFinite_AddsNothing()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            meter.AddTickWork(-1.0);
            meter.AddTickWork(double.NaN);
            meter.AddTickWork(double.PositiveInfinity);

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.sendSamples, window.sendMeanMs), Is.EqualTo((1, 0f)));
        }

        [Test]
        public void EndTick_WithoutAMarkedAoiCall_LeavesNoSendSample()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.BeginTick(t);
            meter.EndTick(t + TicksPerMs);
            meter.BeginTick(t + 2 * TicksPerMs);
            meter.EndTick(t + 3 * TicksPerMs);

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((window.ticks, window.sendSamples), Is.EqualTo((2, 0)));
        }

        [Test]
        public void BeginTick_FlushesThePreviousTicksSendSample()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.BeginTick(t);
            meter.EndTick(t + TicksPerMs);
            meter.AddTickWork(1.0);
            meter.BeginTick(t + 2 * TicksPerMs);
            meter.EndTick(t + 3 * TicksPerMs);
            meter.AddTickWork(3.0);

            MeshTickWindow window = meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.sendSamples, Is.EqualTo(2));
            Assert.That(window.sendMeanMs, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void MarkAoiComplete_CountsSumAndMax()
        {
            MeshWorkMeter meter = new MeshWorkMeter(TicksPerSecond);
            long t = 1000 * TicksPerMs;
            meter.MarkAoiComplete(t, t + 2 * TicksPerMs);
            meter.MarkAoiComplete(t, t + 4 * TicksPerMs);
            meter.MarkAoiComplete(t + TicksPerMs, t);

            MeshTickWindow window = meter.TakeTickWindow(2d, 20, 0f);

            Assert.That(window.aoiCompleteCalls, Is.EqualTo(3));
            Assert.That(window.aoiCompleteMeanMs, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(window.aoiCompleteMaxMs, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(window.aoiCompleteMsPerSecond, Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void TickWindow_FrameStraddlingTheBoundary_IsFiledInTheNextWindowAsATickFrame()
        {
            Driver driver = new Driver();
            driver.Start();
            driver.Tick(1.0);
            driver.Work(2.0);

            MeshTickWindow first = driver.meter.TakeTickWindow(5d, 20, 0f);
            driver.EndFrame(10.0);
            MeshTickWindow second = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That((first.ticks, first.frames), Is.EqualTo((1, 0)));
            Assert.That((second.ticks, second.frames, second.frames1, second.frames0), Is.EqualTo((0, 1, 1, 0)));
            Assert.That(second.tickFrameMeanMs, Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void TickWindow_LogBins_ListsTheBinOfA2_52msFrame()
        {
            Driver driver = new Driver();
            driver.meter.LogBins = true;
            driver.Frame(1, 0.52, 2.0, 10.0);

            MeshTickWindow window = driver.meter.TakeTickWindow(5d, 20, 0f);

            Assert.That(window.bins, Is.EqualTo("Work bins 5.00 s: tick 50:1 over:0 | nonaoi 50:1 over:0 | other over:0 | kit 10:1 over:0 | send over:0"));
        }

        private static IEnumerable<TestCaseData> BudgetForCases()
        {
            yield return new TestCaseData(0, 0, 0f).SetName("BudgetFor_NoCapAndNoNominal_IsZero");
            yield return new TestCaseData(-1, 0, 0f).SetName("BudgetFor_UncappedWithoutNominal_IsZero");
            yield return new TestCaseData(60, 0, 16.67f).SetName("BudgetFor_Capped_IsTheCapsBudget");
            yield return new TestCaseData(-1, 30, 33.33f).SetName("BudgetFor_UncappedWithNominal_IsTheNominalBudget");
            yield return new TestCaseData(30, 60, 33.33f).SetName("BudgetFor_CappedWithNominal_TheCapWins");
        }

        [TestCaseSource(nameof(BudgetForCases))]
        public void BudgetFor(int targetFrameRate, int nominalFrameRate, float expected)
        {
            Assert.That(MeshWorkProbe.BudgetFor(targetFrameRate, nominalFrameRate), Is.EqualTo(expected).Within(0.005f));
        }
    }

    public class MeshOccupancyGridTests
    {
        [Test]
        public void Covering_RejectsBadArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MeshOccupancyGrid.Covering(0f, 0f, 10f, 10f, 0f));
            Assert.Throws<ArgumentException>(() => MeshOccupancyGrid.Covering(10f, 0f, 0f, 10f, 5f));
        }

        [Test]
        public void Covering_AlignsToBucketMultiples()
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(-185f, -30f, 185f, 10f, 20f);
            Assert.That(grid.originX, Is.EqualTo(-200f));
            Assert.That(grid.originZ, Is.EqualTo(-40f));
            Assert.That(grid.columns, Is.EqualTo(20));
            Assert.That(grid.rows, Is.EqualTo(3));
        }

        private static IEnumerable<TestCaseData> AddCases()
        {
            yield return new TestCaseData(-1f, 5f, false).SetName("Add_LeftOfGrid_Ignored");
            yield return new TestCaseData(40f, 5f, false).SetName("Add_OnMaxEdge_Ignored");
            yield return new TestCaseData(0f, 0f, true).SetName("Add_OnMinCorner_Counted");
            yield return new TestCaseData(39.9f, 19.9f, true).SetName("Add_InsideLastBucket_Counted");
        }

        [TestCaseSource(nameof(AddCases))]
        public void Add(float x, float z, bool expected)
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(0f, 0f, 40f, 20f, 20f);
            Assert.That(grid.Add(MeshOccupancyLayer.OwnedPlayers, x, z), Is.EqualTo(expected));
        }

        [Test]
        public void Total_SumsBucketsWhoseCentreIsInside()
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(0f, 0f, 60f, 20f, 20f);
            grid.Add(MeshOccupancyLayer.OwnedPlayers, 5f, 5f);
            grid.Add(MeshOccupancyLayer.OwnedPlayers, 25f, 5f);
            grid.Add(MeshOccupancyLayer.OwnedPlayers, 26f, 6f);
            grid.Add(MeshOccupancyLayer.Puppets, 45f, 5f);

            Assert.That(grid.Total(MeshOccupancyLayer.OwnedPlayers, 0f, 0f, 20f, 20f), Is.EqualTo(1));
            Assert.That(grid.Total(MeshOccupancyLayer.OwnedPlayers, 20f, 0f, 60f, 20f), Is.EqualTo(2));
            Assert.That(grid.Total(MeshOccupancyLayer.Puppets, 0f, 0f, 60f, 20f), Is.EqualTo(1));
            Assert.That(grid.Total(MeshOccupancyLayer.OwnedMonsters, 0f, 0f, 60f, 20f), Is.EqualTo(0));
        }

        [Test]
        public void Serialize_RoundTrips()
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(-40f, -20f, 40f, 20f, 20f);
            grid.Add(MeshOccupancyLayer.Clients, -30f, 10f);
            grid.Add(MeshOccupancyLayer.OwnedMonsters, 30f, -10f);
            NetDataWriter writer = new NetDataWriter();
            grid.Serialize(writer);

            MeshOccupancyGrid copy = new MeshOccupancyGrid();
            copy.Deserialize(new NetDataReader(writer.CopyData()));

            Assert.That(copy.originX, Is.EqualTo(grid.originX));
            Assert.That(copy.columns, Is.EqualTo(grid.columns));
            Assert.That(copy.rows, Is.EqualTo(grid.rows));
            Assert.That(copy.Count(MeshOccupancyLayer.Clients, 0, 1), Is.EqualTo(1));
            Assert.That(copy.Count(MeshOccupancyLayer.OwnedMonsters, 3, 0), Is.EqualTo(1));
        }
    }

    public class MeshLayoutOverrideTests
    {
        private static IEnumerable<TestCaseData> MalformedCases()
        {
            yield return new TestCaseData("ServerMeshTestMap").SetName("Parse_NoEquals_Throws");
            yield return new TestCaseData("M=C").SetName("Parse_NoAt_Throws");
            yield return new TestCaseData("M=C@1,2,3").SetName("Parse_ThreeNumbers_Throws");
            yield return new TestCaseData("M=C@1,2,x,4").SetName("Parse_NotANumber_Throws");
            yield return new TestCaseData("M=C@1,2,0,4").SetName("Parse_ZeroSize_Throws");
            yield return new TestCaseData("M=C@0,0,1,1|M=D@0,0,1,1").SetName("Parse_MapTwice_Throws");
        }

        [TestCaseSource(nameof(MalformedCases))]
        public void Parse_Malformed(string value)
        {
            Assert.Throws<FormatException>(() => MeshLayoutOverride.Parse(value));
        }

        [Test]
        public void Parse_Empty_IsEmpty()
        {
            Assert.That(MeshLayoutOverride.Parse(""), Is.Empty);
            Assert.That(MeshLayoutOverride.Parse(null), Is.Empty);
        }

        [Test]
        public void Parse_TwoMapsWithSeveralCells()
        {
            Dictionary<string, MeshCellRect[]> maps = MeshLayoutOverride.Parse("A=C@-300,-300,300,600; E@0,-300,300,600|B=C2@-100,-100,200,200");

            Assert.That(maps.Keys, Is.EquivalentTo(new[] { "A", "B" }));
            Assert.That(maps["A"].Length, Is.EqualTo(2));
            Assert.That(maps["A"][1].id, Is.EqualTo("E"));
            Assert.That(maps["A"][1].min.x, Is.EqualTo(0f));
            Assert.That(maps["A"][1].size.y, Is.EqualTo(600f));
            Assert.That(maps["B"][0].id, Is.EqualTo("C2"));
        }

        private static IEnumerable<TestCaseData> MalformedModeCases()
        {
            yield return new TestCaseData("M").SetName("ParseCellModes_NoColon_Throws");
            yield return new TestCaseData("M:Weird").SetName("ParseCellModes_UnknownMode_Throws");
            yield return new TestCaseData("M:1").SetName("ParseCellModes_NumberForMode_Throws");
            yield return new TestCaseData("M:Dynamic,M:Static").SetName("ParseCellModes_MapTwice_Throws");
            yield return new TestCaseData(":Dynamic").SetName("ParseCellModes_NoMap_Throws");
            yield return new TestCaseData("all:Dynamic,ALL:Static").SetName("ParseCellModes_AllTwice_Throws");
        }

        [TestCaseSource(nameof(MalformedModeCases))]
        public void ParseCellModes_Malformed(string value)
        {
            Assert.Throws<FormatException>(() => MeshLayoutOverride.ParseCellModes(value));
        }

        private static IEnumerable<TestCaseData> ModeCases()
        {
            yield return new TestCaseData("", new string[0], new MeshCellMode[0]).SetName("ParseCellModes_Empty_IsEmpty");
            yield return new TestCaseData(null, new string[0], new MeshCellMode[0]).SetName("ParseCellModes_Null_IsEmpty");
            yield return new TestCaseData("all:Dynamic", new[] { "all" }, new[] { MeshCellMode.Dynamic }).SetName("ParseCellModes_All");
            yield return new TestCaseData(" A:Dynamic , B:Static ", new[] { "A", "B" }, new[] { MeshCellMode.Dynamic, MeshCellMode.Static }).SetName("ParseCellModes_TwoMaps_Trimmed");
            yield return new TestCaseData("All:Dynamic,M:Static", new[] { "all", "M" }, new[] { MeshCellMode.Dynamic, MeshCellMode.Static }).SetName("ParseCellModes_AllWithAMap");
            yield return new TestCaseData("M:dYnAmIc", new[] { "M" }, new[] { MeshCellMode.Dynamic }).SetName("ParseCellModes_ModeIgnoresCase");
        }

        [TestCaseSource(nameof(ModeCases))]
        public void ParseCellModes(string value, string[] maps, MeshCellMode[] modes)
        {
            Dictionary<string, MeshCellMode> parsed = MeshLayoutOverride.ParseCellModes(value);

            Assert.That(parsed.Keys, Is.EqualTo(maps));
            Assert.That(parsed.Values, Is.EqualTo(modes));
        }
    }

    public class MeshCellModeDefaultsTests
    {
        [Test]
        public void Static_IsTheZeroValue()
        {
            Assert.That((int)MeshCellMode.Static, Is.EqualTo(0));
        }

        [Test]
        public void NewLayout_IsStaticWithAutomaticOpsOff()
        {
            MeshMapLayout layout = new MeshMapLayout();

            Assert.That((layout.cellMode, layout.IsDynamic, layout.autoSplit, layout.autoMerge), Is.EqualTo((MeshCellMode.Static, false, false, false)));
        }

        [Test]
        public void RegistryJsonWithoutTheNewKeys_StaysStatic_WithSettings()
        {
            MeshWorldRegistry world = UnityEngine.ScriptableObject.CreateInstance<MeshWorldRegistry>();
            try
            {
                UnityEngine.JsonUtility.FromJsonOverwrite("{\"maps\":[{\"mapId\":\"M\",\"cellSize\":100,\"columns\":2,\"rows\":2}],\"touchTolerance\":0.5}", world);

                Assert.That(world.maps[0].cellMode, Is.EqualTo(MeshCellMode.Static));
                Assert.That(world.dynamicSettings, Is.Not.Null);
                Assert.That(world.HasDynamicMap, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(world);
            }
        }

        [Test]
        public void CopyModeFrom_CarriesEveryDynamicField_AndIgnoresNull()
        {
            MeshMapLayout from = new MeshMapLayout { cellMode = MeshCellMode.Dynamic, minCellSize = 120f, maxCellsPerBase = 3, autoSplit = true, autoMerge = true };
            MeshMapLayout to = new MeshMapLayout { mapId = "M", cellSize = 50f };

            to.CopyModeFrom(null);
            Assert.That(to.cellMode, Is.EqualTo(MeshCellMode.Static));
            to.CopyModeFrom(from);

            Assert.That((to.cellMode, to.minCellSize, to.maxCellsPerBase, to.autoSplit, to.autoMerge), Is.EqualTo((MeshCellMode.Dynamic, 120f, 3, true, true)));
            Assert.That((to.mapId, to.cellSize), Is.EqualTo(("M", 50f)));
        }
    }
}
