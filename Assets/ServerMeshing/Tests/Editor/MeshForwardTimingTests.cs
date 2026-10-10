using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshForwardTimingTests
    {
        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        [Test]
        public void NoteResult_WithoutSend_IsUnmatched()
        {
            MeshForwardTiming timing = new MeshForwardTiming();

            bool matched = timing.NoteResult("m1", 1.0);

            Assert.That(matched, Is.False);
            Assert.That(timing.Results, Is.EqualTo(1));
            Assert.That(timing.Unmatched, Is.EqualTo(1));
            Assert.That(timing.Matched, Is.EqualTo(0));
        }

        [Test]
        public void NoteResult_NullTarget_IsUnmatched()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent(null, 0.0);

            Assert.That(timing.NoteResult(null, 0.1), Is.False);
            Assert.That(timing.Sent, Is.EqualTo(0));
            Assert.That(timing.Unmatched, Is.EqualTo(1));
        }

        [Test]
        public void NoteResult_OtherTarget_DoesNotPair()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 0.0);

            Assert.That(timing.NoteResult("m2", 0.01), Is.False);
            Assert.That(timing.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void NoteResult_SameTarget_PairsFirstInFirstOut()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 10.000);
            timing.NoteSent("m1", 10.100);
            timing.NoteSent("m2", 10.050);

            Assert.That(timing.NoteResult("m1", 10.020), Is.True); // pairs 10.000: 20 ms
            Assert.That(timing.NoteResult("m2", 10.060), Is.True); // 10 ms
            Assert.That(timing.NoteResult("m1", 10.140), Is.True); // pairs 10.100: 40 ms

            Assert.That(timing.Sent, Is.EqualTo(3));
            Assert.That(timing.Results, Is.EqualTo(3));
            Assert.That(timing.Matched, Is.EqualTo(3));
            Assert.That(timing.PendingCount, Is.EqualTo(0));
            Assert.That(timing.MaxMs, Is.EqualTo(40f).Within(0.01f));
            Assert.That(timing.P50Ms, Is.EqualTo(20f).Within(2f * MeshForwardTiming.BinMs)); // a bin's upper edge
        }

        [Test]
        public void NoteResult_AfterAnUnansweredSendTimedOut_SkipsIt()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 0.0); // never answered
            timing.NoteSent("m1", 6.0);

            Assert.That(timing.NoteResult("m1", 6.015), Is.True);

            Assert.That(timing.Expired, Is.EqualTo(1));
            Assert.That(timing.MaxMs, Is.EqualTo(15f).Within(0.01f));
        }

        [Test]
        public void NoteResult_EarlierThanItsSend_CountsZero()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 5.0);

            Assert.That(timing.NoteResult("m1", 4.9), Is.True);
            Assert.That(timing.MaxMs, Is.EqualTo(0f));
        }

        [Test]
        public void Expire_DropsOldSendsAndReleasesEmptyTargets()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 0.0);
            timing.NoteSent("m2", 4.0);

            timing.Expire(6.0);

            Assert.That(timing.Expired, Is.EqualTo(1));
            Assert.That(timing.PendingCount, Is.EqualTo(1));
            Assert.That(timing.TargetCount, Is.EqualTo(1));
            Assert.That(timing.NoteResult("m1", 6.1), Is.False);
            Assert.That(timing.NoteResult("m2", 6.1), Is.True);
        }

        [Test]
        public void ResetWindow_KeepsPendingSends()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 1.0);
            timing.NoteResult("m9", 1.0);

            timing.ResetWindow();

            Assert.That(timing.Sent, Is.EqualTo(0));
            Assert.That(timing.Results, Is.EqualTo(0));
            Assert.That(timing.Unmatched, Is.EqualTo(0));
            Assert.That(timing.Matched, Is.EqualTo(0));
            Assert.That(timing.PendingCount, Is.EqualTo(1));
            Assert.That(timing.NoteResult("m1", 1.002), Is.True);
        }

        [Test]
        public void Clear_ForgetsPendingSends()
        {
            MeshForwardTiming timing = new MeshForwardTiming();
            timing.NoteSent("m1", 1.0);

            timing.Clear();

            Assert.That(timing.PendingCount, Is.EqualTo(0));
            Assert.That(timing.TargetCount, Is.EqualTo(0));
            Assert.That(timing.NoteResult("m1", 1.1), Is.False);
        }

        [Test]
        public void WindowLine_Empty_IsZeros()
        {
            string line = new MeshForwardTiming().WindowLine(5.0);

            Assert.That(line, Is.EqualTo("Forwarded hits window 5.00 s: sent 0 results 0 | p50 0.00 p95 0.00 max 0.00 ms"));
            AssertPrintable(line);
        }

        [Test]
        public void Line_IsInvariantAndClampsBadNumbers()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = MeshForwardTiming.Line(5.016, 12, 11, 1.5, double.NaN, double.PositiveInfinity);

                Assert.That(line, Is.EqualTo("Forwarded hits window 5.02 s: sent 12 results 11 | p50 1.50 p95 0.00 max 0.00 ms"));
                AssertPrintable(line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
