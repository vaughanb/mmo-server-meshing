using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshTickWindowLineTests
    {
        private const string ZeroLine = "Work ticks 0.00 s: ticks 0 expected 0 dropped 0 cap 0 unclosed 0 | frames 0 t0 0 t1 0 t2 0 | tick frames mean 0.000 ms p95 0.00 ms p99 0.00 ms max 0.0 ms | nonaoi p95 0.00 ms p99 0.00 ms | aoi passes 0 frames 0 mean 0.000 ms max 0.0 ms | other mean 0.000 ms p95 0.00 ms | kit mean 0.000 ms p95 0.00 ms p99 0.00 ms max 0.0 ms | parts 0 sync 0.000 ms aoi 0.000 ms move 0.000 ms | late 0 mean 0.000 ms max 0.0 ms 0.0 ms/s | send 0 mean 0.000 ms p95 0.00 ms p99 0.00 ms | physics 0.0/s mean 0.00 max 0 f2 0 | work 0.0 ms/s busy 0.0% | budget 0.00 ms | gc0 0 | outside 0";

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_NegativeAndNonFiniteValues_PrintAsZero()
        {
            MeshTickWindow window = new MeshTickWindow
            {
                seconds = -5f,
                ticks = -1,
                tickFrameMeanMs = float.NaN,
                tickFrameMaxMs = float.PositiveInfinity,
                otherFrameP95Ms = float.NegativeInfinity,
                syncMeanMs = -0.0001f,
                workMsPerSecond = -3f,
                busyShare = -0f,
                gen0Collections = -2,
            };

            Assert.That(window.ToLine(), Is.EqualTo(ZeroLine));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_Default_IsAllZerosWithoutNaN()
        {
            string line = default(MeshTickWindow).ToLine();

            Assert.That(line, Is.EqualTo(ZeroLine));
            Assert.That(line, Does.Not.Contain("NaN"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_FilledWindow_IsTheExactInvariantLine()
        {
            MeshTickWindow window = new MeshTickWindow
            {
                seconds = 5f,
                tickHz = 20,
                ticks = 100,
                expected = 100,
                dropped = 0,
                capFrames = 0,
                unclosed = 0,
                aoiOutsideTick = 0,
                frames = 150,
                frames0 = 50,
                frames1 = 100,
                frames2Plus = 0,
                tickFrameMeanMs = 3.412f,
                tickFrameP95Ms = 4.1f,
                tickFrameP99Ms = 5.2f,
                tickFrameMaxMs = 6.1f,
                nonAoiTickFrameP95Ms = 4f,
                nonAoiTickFrameP99Ms = 4.5f,
                aoiPasses = 5,
                aoiFrames = 5,
                aoiFrameMeanMs = 4f,
                aoiFrameMaxMs = 8.2f,
                otherFrameMeanMs = 1.203f,
                otherFrameP95Ms = 1.5f,
                tickMeanMs = 0.812f,
                tickP95Ms = 1f,
                tickP99Ms = 1.2f,
                tickMaxMs = 2f,
                partsTicks = 100,
                syncMeanMs = 0.3f,
                aoiScheduleMeanMs = 0.05f,
                movementMeanMs = 0.462f,
                aoiCompleteCalls = 150,
                aoiCompleteMeanMs = 0.02f,
                aoiCompleteMaxMs = 0.9f,
                aoiCompleteMsPerSecond = 0.6f,
                sendSamples = 100,
                sendMeanMs = 0.462f,
                sendP95Ms = 0.6f,
                sendP99Ms = 0.7f,
                physicsSteps = 250,
                physicsMaxPerFrame = 2,
                physics2PlusFrames = 100,
                physicsPerSecond = 50f,
                physicsMeanPerFrame = 1.67f,
                workMsPerSecond = 312.4f,
                busyShare = 0.3124f,
                nominalBudgetMs = 33.333332f,
                gen0Collections = 0,
            };

            Assert.That(window.ToLine(), Is.EqualTo(
                "Work ticks 5.00 s: ticks 100 expected 100 dropped 0 cap 0 unclosed 0 | frames 150 t0 50 t1 100 t2 0 | tick frames mean 3.412 ms p95 4.10 ms p99 5.20 ms max 6.1 ms | nonaoi p95 4.00 ms p99 4.50 ms | aoi passes 5 frames 5 mean 4.000 ms max 8.2 ms | other mean 1.203 ms p95 1.50 ms | kit mean 0.812 ms p95 1.00 ms p99 1.20 ms max 2.0 ms | parts 100 sync 0.300 ms aoi 0.050 ms move 0.462 ms | late 150 mean 0.020 ms max 0.9 ms 0.6 ms/s | send 100 mean 0.462 ms p95 0.60 ms p99 0.70 ms | physics 50.0/s mean 1.67 max 2 f2 100 | work 312.4 ms/s busy 31.2% | budget 33.33 ms | gc0 0 | outside 0"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void ToLine_KeepsTheRigsLogRules()
        {
            MeshTickWindow window = new MeshTickWindow { seconds = 5f, ticks = 12345, workMsPerSecond = 1234.5f, busyShare = 1.2344f };

            string line = window.ToLine();

            Assert.That(line, Does.StartWith("Work ticks "));
            Assert.That(line, Does.Not.Contain("Work window"));
            Assert.That(line, Does.Not.Contain("Exception"));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}"));
            Assert.That(line, Does.Not.Contain("\n").And.Not.Contain("\r"));
            Assert.That(Regex.IsMatch(line, @"\b(Split|Merge|Retire|Retired)\b"), Is.False);
            Assert.That(line, Does.Contain("work 1234.5 ms/s busy 123.4%"));
        }
    }
}
