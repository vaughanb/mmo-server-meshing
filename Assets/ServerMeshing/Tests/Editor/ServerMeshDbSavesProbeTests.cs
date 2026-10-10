using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshDbSavesProbeTests
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("0", false)]
        [TestCase(" 0 ", false)]
        [TestCase("1", true)]
        [TestCase(" 1 ", true)]
        public void TryParseSwitch_Valid(string text, bool expected)
        {
            bool parsed = ServerMeshDbSavesProbe.TryParseSwitch(text, out bool on, out string error);

            Assert.That(parsed, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(on, Is.EqualTo(expected));
        }

        [TestCase("2")]
        [TestCase("yes")]
        [TestCase("true")]
        [TestCase("{1}")]
        public void TryParseSwitch_Invalid(string text)
        {
            bool parsed = ServerMeshDbSavesProbe.TryParseSwitch(text, out bool on, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(on, Is.False);
            StringAssert.Contains(ServerMeshDbSavesProbe.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        [TestCase(50L, 0, 50, 0L)]
        [TestCase(50L, 0, 34, 16L)]
        [TestCase(0L, 34, 18, 16L)]
        [TestCase(10L, 100, 84, 26L)]
        [TestCase(0L, 0, 0, 0L)]
        [TestCase(0L, 0, 5, -5L)]
        public void Written_IsEnqueuedLessTheBacklogGrowth_Signed(long enqueued, int before, int after, long expected)
        {
            Assert.That(ServerMeshDbSavesProbe.Written(enqueued, before, after), Is.EqualTo(expected));
        }

        [TestCase(8, 500, 16.0)]
        [TestCase(8, 1000, 8.0)]
        [TestCase(0, 500, 2.0)]
        [TestCase(8, 10, 160.0)]
        [TestCase(3, 400, 7.5)]
        public void Ceiling_FollowsTheSchedulerFloors(int maxPerTick, int tickMs, double expected)
        {
            Assert.That(ServerMeshDbSavesProbe.Ceiling(maxPerTick, tickMs), Is.EqualTo(expected).Within(1e-9));
        }

        [TestCase("[DatabaseNetworkManager] Deferred character save failed for characterId=abc. boom", true)]
        [TestCase("Deferred character save failed", true)]
        [TestCase("[DatabaseNetworkManager] Character save scheduler started. lanes=32", false)]
        [TestCase("deferred character save failed", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsSaveFailedLine_MatchesTheKitText(string message, bool expected)
        {
            Assert.That(ServerMeshDbSavesProbe.IsSaveFailedLine(message), Is.EqualTo(expected));
        }

        [Test]
        public void WindowLine_MatchesTheContract()
        {
            string line = ServerMeshDbSavesProbe.WindowLine(5.002, 120, 50, 16, 0, 59.84, 16);

            Assert.That(line, Is.EqualTo("DB saves window 5.00 s: pending 120 enqueued 50 written 16 failed 0 | db fps 59.8 | ceiling 16/s"));
        }

        [Test]
        public void WindowLine_SignedWrittenWholeCeilingAndZeros()
        {
            string line = ServerMeshDbSavesProbe.WindowLine(double.PositiveInfinity, -1, -2, -3, -4, double.NaN, 7.5);

            Assert.That(line, Is.EqualTo("DB saves window 0.00 s: pending 0 enqueued 0 written -3 failed 0 | db fps 0.0 | ceiling 8/s"));
        }

        [Test]
        public void WindowLine_IsInvariantUnderACommaCulture()
        {
            System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string line = ServerMeshDbSavesProbe.WindowLine(5, 1000, 2000, 3000, 0, 30.5, 16);

                Assert.That(line, Is.EqualTo("DB saves window 5.00 s: pending 1000 enqueued 2000 written 3000 failed 0 | db fps 30.5 | ceiling 16/s"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
