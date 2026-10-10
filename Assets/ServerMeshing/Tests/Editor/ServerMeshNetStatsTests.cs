using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshNetStatsTests
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("0", false)]
        [TestCase("1", true)]
        [TestCase(" 1 ", true)]
        public void TryParseSwitch_Valid(string text, bool expected)
        {
            bool parsed = ServerMeshNetStats.TryParseSwitch(text, out bool on, out string error);

            Assert.That(parsed, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(on, Is.EqualTo(expected));
        }

        [TestCase("2")]
        [TestCase("on")]
        [TestCase("{1}")]
        public void TryParseSwitch_Invalid(string text)
        {
            bool parsed = ServerMeshNetStats.TryParseSwitch(text, out bool on, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(on, Is.False);
            StringAssert.Contains(ServerMeshNetStats.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        [TestCase(0L, 0L, 0.0)]
        [TestCase(5L, 0L, 0.0)]
        [TestCase(-5L, 100L, 0.0)]
        [TestCase(0L, 100L, 0.0)]
        [TestCase(1L, 100L, 0.01)]
        [TestCase(52L, 5000L, 0.0104)]
        public void LossShare_IsResentOverSent(long resent, long sent, double expected)
        {
            Assert.That(ServerMeshNetStats.LossShare(resent, sent), Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void WindowLine_MatchesTheContract()
        {
            string line = ServerMeshNetStats.WindowLine(5.001, 5000, 4800, 52);

            Assert.That(line, Is.EqualTo("Net stats window 5.00 s: sent 5000 pkt received 4800 pkt resent 52 | loss share 0.0104"));
        }

        [Test]
        public void WindowLine_EmptyWindowIsZeros()
        {
            string line = ServerMeshNetStats.WindowLine(double.NaN, -1, -1, -1);

            Assert.That(line, Is.EqualTo("Net stats window 0.00 s: sent 0 pkt received 0 pkt resent 0 | loss share 0.0000"));
        }

        [Test]
        public void WindowLine_IsInvariantUnderACommaCulture()
        {
            System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string line = ServerMeshNetStats.WindowLine(4.5, 10000, 9000, 500);

                Assert.That(line, Is.EqualTo("Net stats window 4.50 s: sent 10000 pkt received 9000 pkt resent 500 | loss share 0.0500"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
