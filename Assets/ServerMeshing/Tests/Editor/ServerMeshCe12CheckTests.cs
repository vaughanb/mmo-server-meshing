using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshCe12CheckTests
    {
        private static IEnumerable<TestCaseData> ClassifyCases()
        {
            yield return new TestCaseData(false, false, false, false, false, false, "absent").SetName("Classify_NoSuccessfulFirstWrite_Absent");
            yield return new TestCaseData(false, true, true, true, true, false, "absent").SetName("Classify_FirstWriteFalseWhateverElse_Absent");
            yield return new TestCaseData(true, false, true, true, true, false, "absent").SetName("Classify_FirstWriteWithoutTheFlag_Absent");
            yield return new TestCaseData(true, true, true, true, false, false, "stuck").SetName("Classify_HeldThroughT50WithNoConsume_Stuck");
            yield return new TestCaseData(true, true, true, true, true, true, "stuck").SetName("Classify_StillSetAfterTheConsume_Stuck");
            yield return new TestCaseData(true, true, false, true, false, false, "stuck").SetName("Classify_FlagBackAtT50AfterClearing_Stuck");
            yield return new TestCaseData(true, true, false, false, false, false, "perCall").SetName("Classify_ClearedByTheFirstWrite_PerCall");
            yield return new TestCaseData(true, true, false, false, true, false, "perCall").SetName("Classify_ClearedByTheFirstWriteWithAConsume_PerCall");
            yield return new TestCaseData(true, true, true, false, false, false, "stamp").SetName("Classify_HeldForOneTimestamp_Stamp");
            yield return new TestCaseData(true, true, true, false, true, false, "stamp").SetName("Classify_HeldForOneTimestampWithAConsume_Stamp");
            yield return new TestCaseData(true, true, true, true, true, false, "pass").SetName("Classify_HeldUntilTheConsume_Pass");
        }

        [TestCaseSource(nameof(ClassifyCases))]
        public void Classify(bool firstWrote, bool first, bool second, bool next, bool consumeAvailable, bool after, string expected)
        {
            Assert.That(ServerMeshCe12Shape.Classify(firstWrote, first, second, next, consumeAvailable, after), Is.EqualTo(expected));
        }

        [Test]
        public void Line_NoHandler_PrintsZeroBitsAndNone()
        {
            string line = ServerMeshCe12Shape.Line("C", false, false, false, false, false, false, false, false, false, false, null);

            Assert.That(line, Is.EqualTo("CE-12 check: cell C shape=absent first=0 second=0 next=0 consume=no after=0 reliable=0/0/0/0 handler=none"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void Line_TodaysKit_PrintsPass()
        {
            string line = ServerMeshCe12Shape.Line("C", true, true, true, true, true, false, true, true, true, false, "BuiltInEntityMovementFunctions3D");

            Assert.That(line, Is.EqualTo("CE-12 check: cell C shape=pass first=1 second=1 next=1 consume=yes after=0 reliable=1/1/1/0 handler=BuiltInEntityMovementFunctions3D"));
        }

        [Test]
        public void Line_SpacesInTheCellOrHandler_BecomeUnderscores()
        {
            string line = ServerMeshCe12Shape.Line("cell one", true, true, true, false, false, false, true, true, false, false, "My Handler");

            Assert.That(line, Is.EqualTo("CE-12 check: cell cell_one shape=stamp first=1 second=1 next=0 consume=no after=0 reliable=1/1/0/0 handler=My_Handler"));
        }
    }
}
