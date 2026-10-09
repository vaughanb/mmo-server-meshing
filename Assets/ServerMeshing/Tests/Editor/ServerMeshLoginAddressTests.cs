using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshLoginAddressTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData(null, "no host").SetName("TryParse_Null_Refused");
            yield return new TestCaseData("", "no host").SetName("TryParse_Empty_Refused");
            yield return new TestCaseData("   ", "no host").SetName("TryParse_Blank_Refused");
            yield return new TestCaseData(":7500", "no host").SetName("TryParse_PortOnly_Refused");
            yield return new TestCaseData("host a:7500", "space").SetName("TryParse_HostWithASpace_Refused");
            yield return new TestCaseData("10.0.0.5:", "port").SetName("TryParse_EmptyPort_Refused");
            yield return new TestCaseData("10.0.0.5:0", "port 0 is not from 1 to 65535").SetName("TryParse_PortZero_Refused");
            yield return new TestCaseData("10.0.0.5:65536", "port 65536").SetName("TryParse_PortOver65535_Refused");
            yield return new TestCaseData("10.0.0.5:-7500", "port").SetName("TryParse_NegativePort_Refused");
            yield return new TestCaseData("10.0.0.5:+7500", "port").SetName("TryParse_SignedPort_Refused");
            yield return new TestCaseData("10.0.0.5:75a0", "port").SetName("TryParse_WordPort_Refused");
            yield return new TestCaseData("10.0.0.5:7500.0", "port").SetName("TryParse_DecimalPort_Refused");
            yield return new TestCaseData("10.0.0.5:7500:1", "more than one colon").SetName("TryParse_ExtraColon_Refused");
            yield return new TestCaseData("::1", "more than one colon").SetName("TryParse_Ipv6Literal_Refused");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParse_Invalid(string text, string reason)
        {
            bool parsed = ServerMeshLoginAddress.TryParse(text, out string host, out int port, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(host, Is.Null);
            Assert.That(port, Is.EqualTo(0));
            Assert.That(error, Does.Contain(ServerMeshLoginAddress.ArgLoginAddress));
            Assert.That(error, Does.Contain(reason));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("192.168.1.20:7500", "192.168.1.20", 7500).SetName("TryParse_HostAndPort");
            yield return new TestCaseData("machine-a", "machine-a", 0).SetName("TryParse_HostOnly_PortZeroKeepsTheManagers");
            yield return new TestCaseData(" machine-a : 7500 ", "machine-a", 7500).SetName("TryParse_Spaces_Trimmed");
            yield return new TestCaseData("h:1", "h", 1).SetName("TryParse_PortOne");
            yield return new TestCaseData("h:65535", "h", 65535).SetName("TryParse_Port65535");
            yield return new TestCaseData("h:07500", "h", 7500).SetName("TryParse_LeadingZero");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParse_Valid(string text, string expectedHost, int expectedPort)
        {
            bool parsed = ServerMeshLoginAddress.TryParse(text, out string host, out int port, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(host, Is.EqualTo(expectedHost));
            Assert.That(port, Is.EqualTo(expectedPort));
        }

        [Test]
        public void Line_IsExact()
        {
            Assert.That(ServerMeshLoginAddress.Line("192.168.1.20", 7500), Is.EqualTo("Login address: 192.168.1.20:7500 from -meshLoginAddress"));
        }

        private static IEnumerable<TestCaseData> ArgumentCases()
        {
            yield return new TestCaseData(null, null).SetName("ArgumentValue_NoArgs_Null");
            yield return new TestCaseData(new[] { "kit.exe", "-meshAutoClient" }, null).SetName("ArgumentValue_Absent_Null");
            yield return new TestCaseData(new[] { "kit.exe", "-meshLoginAddress" }, null).SetName("ArgumentValue_LastWithNoValue_Null");
            yield return new TestCaseData(new[] { "kit.exe", "-meshLoginAddress", "a:1", "-meshLoginAddress", "b:2" }, "a:1").SetName("ArgumentValue_Twice_First");
            yield return new TestCaseData(new[] { "kit.exe", "-meshAutoClient", "-meshLoginAddress", "10.0.0.5:7500" }, "10.0.0.5:7500").SetName("ArgumentValue_Present");
        }

        [TestCaseSource(nameof(ArgumentCases))]
        public void ArgumentValue(string[] args, string expected)
        {
            Assert.That(ServerMeshLoginAddress.ArgumentValue(args, ServerMeshLoginAddress.ArgLoginAddress), Is.EqualTo(expected));
        }
    }
}
