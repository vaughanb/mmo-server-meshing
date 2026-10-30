using System;
using System.Diagnostics;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshProcessSelfTests
    {
        private const string Stat = "4242 (kit-ce-project) S 1 4242 4242 0 -1 4194304 100 0 0 0 250 75 0 0 20 0 30 0 12345 999 888";

        [Test]
        public void ProcessorTime_MatchesTheClassLibrary()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                TimeSpan before = MeshProcessSelf.ProcessorTime();
                TimeSpan library = process.TotalProcessorTime;
                process.Refresh();
                TimeSpan after = MeshProcessSelf.ProcessorTime();

                Assert.That(before, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(library, Is.GreaterThanOrEqualTo(before));
                Assert.That(after, Is.GreaterThanOrEqualTo(library));
            }
        }

        [Test]
        public void ProcessorTime_GrowsWithWork()
        {
            TimeSpan before = MeshProcessSelf.ProcessorTime();
            Stopwatch spin = Stopwatch.StartNew();
            while (spin.ElapsedMilliseconds < 100) { }

            Assert.That(MeshProcessSelf.ProcessorTime(), Is.GreaterThan(before));
        }

        [Test]
        public void TryParseProcStat_AddsUserAndKernelTicks()
        {
            Assert.That(MeshProcessSelf.TryParseProcStat(Stat, out TimeSpan cpu), Is.True);
            Assert.That(cpu, Is.EqualTo(TimeSpan.FromSeconds(3.25)));
        }

        [TestCase("4242 (kit ce) project) S 1 4242 4242 0 -1 4194304 100 0 0 0 250 75 0 0 20 0")]
        [TestCase("4242 (a (b) c) S 1 4242 4242 0 -1 4194304 100 0 0 0 250 75 0 0 20 0")]
        public void TryParseProcStat_CommandNameMayHoldSpacesAndParentheses(string stat)
        {
            Assert.That(MeshProcessSelf.TryParseProcStat(stat, out TimeSpan cpu), Is.True);
            Assert.That(cpu, Is.EqualTo(TimeSpan.FromSeconds(3.25)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("no parentheses at all")]
        [TestCase("4242 (kit) S 1 2")]
        [TestCase("4242 (kit) S 1 4242 4242 0 -1 4194304 100 0 0 0 abc 75")]
        public void TryParseProcStat_RefusesWhatIsNotAStatLine(string stat)
        {
            Assert.That(MeshProcessSelf.TryParseProcStat(stat, out TimeSpan cpu), Is.False);
            Assert.That(cpu, Is.EqualTo(TimeSpan.Zero));
        }
    }
}
