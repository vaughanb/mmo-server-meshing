using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshExceptionTraceTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(null, null, "Exception stack: ").SetName("Format_Nothing_IsJustTheLabel");
            yield return new TestCaseData("NullReferenceException: Object reference not set", "", "Exception stack: NullReferenceException: Object reference not set")
                .SetName("Format_NoStack_IsTheCondition");
            yield return new TestCaseData("E: x\r\ny", "A.B () (at A.cs:1)\n\nC.D () (at C.cs:2)\n", "Exception stack: E: x  y at A.B () (at A.cs:1) <- C.D () (at C.cs:2)")
                .SetName("Format_FramesJoinedOnOneLine");
        }

        [TestCaseSource(nameof(Cases))]
        public void Format(string condition, string stackTrace, string expected)
        {
            Assert.That(MeshExceptionTrace.Format(condition, stackTrace), Is.EqualTo(expected));
        }

        [Test]
        public void Format_CapsARunawayStack()
        {
            string stack = string.Join("\n", System.Linq.Enumerable.Repeat("Frame.Method () (at File.cs:10)", 1000));
            string line = MeshExceptionTrace.Format("E", stack);
            Assert.That(line.Length, Is.LessThanOrEqualTo(MeshExceptionTrace.MaxLength + 4));
            Assert.That(line, Does.Not.Contain("\n"));
        }
    }
}
