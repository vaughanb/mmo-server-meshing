using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshAutoClientMigrationTests
    {
        private CultureInfo _culture;

        [SetUp]
        public void UseACommaCulture()
        {
            // Every line here is read by a Python grader: a decimal comma would break it.
            _culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TearDown]
        public void RestoreCulture()
        {
            Thread.CurrentThread.CurrentCulture = _culture;
        }

        [Test]
        public void MigrationLine_IsTheContractFormat()
        {
            string line = ServerMeshAutoClient.MigrationLine(1, 412, true, 0.25f, 35.5f, -80.25f);

            Assert.That(line, Is.EqualTo("MIGRATION 1: gap=412 ready=True jump=0.25 pos=35.5,-80.25"));
        }

        [Test]
        public void MigrationLine_NotReady_AndNonFiniteValuesPrintAsZero()
        {
            string line = ServerMeshAutoClient.MigrationLine(2, 0, false, float.NaN, float.PositiveInfinity, 0f);

            Assert.That(line, Is.EqualTo("MIGRATION 2: gap=0 ready=False jump=0 pos=0,0"));
        }

        [Test]
        public void MigrationSuffix_IsTheContractFormat_WithALeadingSpace()
        {
            Assert.That(ServerMeshAutoClient.MigrationSuffix(1, true, 640, 0.5f), Is.EqualTo(" migrations=1 allReady=True maxGap=640 maxJump=0.5"));
        }

        [Test]
        public void MigrationSuffix_NoMigrations_ReadsAllReadyAndZeros()
        {
            Assert.That(ServerMeshAutoClient.MigrationSuffix(0, true, 0, 0f), Is.EqualTo(" migrations=0 allReady=True maxGap=0 maxJump=0"));
        }

        [Test]
        public void MigrationSuffix_ClampsNegatives()
        {
            Assert.That(ServerMeshAutoClient.MigrationSuffix(-1, false, -5, float.NaN), Is.EqualTo(" migrations=0 allReady=False maxGap=0 maxJump=0"));
        }

        [Test]
        public void HarvestSuffix_IsTheContractFormat()
        {
            Assert.That(ServerMeshAutoClient.HarvestSuffix(3, true), Is.EqualTo(" yield=3 pickup=True"));
            Assert.That(ServerMeshAutoClient.HarvestSuffix(-2, false), Is.EqualTo(" yield=0 pickup=False"));
        }

        [Test]
        public void LineSideLine_TheLineItselfIsOnThePlusSide()
        {
            Assert.That(ServerMeshAutoClient.LineSideLine(0f, 0f), Is.EqualTo("Line x=0: starting on the + side at x=0"));
            Assert.That(ServerMeshAutoClient.LineSideLine(-35.5f, 0f), Is.EqualTo("Line x=0: starting on the - side at x=-35.5"));
            Assert.That(ServerMeshAutoClient.LineSideLine(250.25f, 200f), Is.EqualTo("Line x=200: starting on the + side at x=250.25"));
        }

        [Test]
        public void ParseMigrationSettings_Absent_SetsNothing()
        {
            ServerMeshAutoClient.MigrationSettings s = ServerMeshAutoClient.ParseMigrationSettings(null, null, null);

            Assert.That(s.hasObserveSeconds, Is.False);
            Assert.That(s.hasLineX, Is.False);
            Assert.That(s.harvestAfterMigration, Is.False);
        }

        [Test]
        public void ParseMigrationSettings_ReadsInvariantNumbers()
        {
            ServerMeshAutoClient.MigrationSettings s = ServerMeshAutoClient.ParseMigrationSettings(" 180.5 ", "-12.25", "1");

            Assert.That(s.hasObserveSeconds, Is.True);
            Assert.That(s.observeSeconds, Is.EqualTo(180.5f));
            Assert.That(s.hasLineX, Is.True);
            Assert.That(s.lineX, Is.EqualTo(-12.25f));
            Assert.That(s.harvestAfterMigration, Is.True);
        }

        [TestCase("0")]
        [TestCase("-5")]
        [TestCase("abc")]
        [TestCase("")]
        [TestCase("NaN")]
        [TestCase("Infinity")]
        public void ParseMigrationSettings_UnusableObserveSeconds_IsNotSet(string value)
        {
            Assert.That(ServerMeshAutoClient.ParseMigrationSettings(value, null, null).hasObserveSeconds, Is.False);
        }

        [TestCase("NaN")]
        [TestCase("x")]
        [TestCase(" ")]
        public void ParseMigrationSettings_UnusableLineX_IsNotSet(string value)
        {
            Assert.That(ServerMeshAutoClient.ParseMigrationSettings(null, value, null).hasLineX, Is.False);
        }

        [Test]
        public void ParseMigrationSettings_LineXZero_IsSet()
        {
            ServerMeshAutoClient.MigrationSettings s = ServerMeshAutoClient.ParseMigrationSettings(null, "0", null);

            Assert.That(s.hasLineX, Is.True);
            Assert.That(s.lineX, Is.EqualTo(0f));
        }

        [TestCase("0", false)]
        [TestCase("true", false)]
        [TestCase("2", false)]
        [TestCase(" 1 ", true)]
        public void ParseMigrationSettings_HarvestAfterMigration_OnlyOneTurnsItOn(string value, bool expected)
        {
            Assert.That(ServerMeshAutoClient.ParseMigrationSettings(null, null, value).harvestAfterMigration, Is.EqualTo(expected));
        }

        [Test]
        public void ParseMigrationArgs_ReadsTheValueAfterEachFlag()
        {
            string[] args = { "Rig.exe", "-meshAutoClient", "-meshAutoMode", "harvest", "-meshObserveSeconds", "240", "-meshLineX", "0", "-meshHarvestAfterMigration", "1" };

            ServerMeshAutoClient.MigrationSettings s = ServerMeshAutoClient.ParseMigrationArgs(args);

            Assert.That(s.observeSeconds, Is.EqualTo(240f));
            Assert.That(s.hasLineX, Is.True);
            Assert.That(s.lineX, Is.EqualTo(0f));
            Assert.That(s.harvestAfterMigration, Is.True);
        }

        [Test]
        public void ParseMigrationArgs_FlagWithoutValue_IsAbsent()
        {
            string[] args = { "Rig.exe", "-meshAutoClient", "-meshLineX" };

            ServerMeshAutoClient.MigrationSettings s = ServerMeshAutoClient.ParseMigrationArgs(args);

            Assert.That(s.hasLineX, Is.False);
            Assert.That(s.hasObserveSeconds, Is.False);
            Assert.That(s.harvestAfterMigration, Is.False);
        }
    }
}
