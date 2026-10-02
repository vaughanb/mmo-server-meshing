using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomSinkTests
    {
        private static string Hex(NetDataWriter writer) => BitConverter.ToString(writer.CopyData()).Replace("-", " ");

        private static IEnumerable<TestCaseData> InvalidArgumentCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSink_Null_Refused");
            yield return new TestCaseData("").SetName("TryParseSink_Empty_Refused");
            yield return new TestCaseData("host:8000").SetName("TryParseSink_NoCount_Refused");
            yield return new TestCaseData(":8000:5").SetName("TryParseSink_NoHost_Refused");
            yield return new TestCaseData("host:0:5").SetName("TryParseSink_PortZero_Refused");
            yield return new TestCaseData("host:70000:5").SetName("TryParseSink_PortAbove65535_Refused");
            yield return new TestCaseData("host:8000:0").SetName("TryParseSink_CountZero_Refused");
            yield return new TestCaseData("host:8000:x").SetName("TryParseSink_WordCount_Refused");
            yield return new TestCaseData("host:8000:5:0").SetName("TryParseSink_PortBaseZero_Refused");
            yield return new TestCaseData("host:8000:5:65533").SetName("TryParseSink_PortBaseWithoutRoom_Refused");
            yield return new TestCaseData("host:8000:5:9000:1").SetName("TryParseSink_FifthField_Refused");
        }

        [TestCaseSource(nameof(InvalidArgumentCases))]
        public void TryParseSink_Invalid(string text)
        {
            bool parsed = ServerMeshPhantomSettings.TryParseSink(text, out string host, out int port, out int count, out int portBase, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(host, Is.Null);
            Assert.That(port + count + portBase, Is.EqualTo(0));
            Assert.That(error, Does.Contain(ServerMeshPhantomSettings.ARG_SINK));
        }

        private static IEnumerable<TestCaseData> ValidArgumentCases()
        {
            yield return new TestCaseData("192.168.50.83:8001:50", "192.168.50.83", 8001, 50, 0).SetName("TryParseSink_HostPortCount");
            yield return new TestCaseData(" a-host :8001:400:9000", "a-host", 8001, 400, 9000).SetName("TryParseSink_WithPortBase");
            yield return new TestCaseData("h:65535:1:65535", "h", 65535, 1, 65535).SetName("TryParseSink_EdgesOfThePortRange");
        }

        [TestCaseSource(nameof(ValidArgumentCases))]
        public void TryParseSink_Valid(string text, string host, int port, int count, int portBase)
        {
            Assert.That(ServerMeshPhantomSettings.TryParseSink(text, out string gotHost, out int gotPort, out int gotCount, out int gotBase, out string error), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(gotHost, Is.EqualTo(host));
            Assert.That(gotPort, Is.EqualTo(port));
            Assert.That(gotCount, Is.EqualTo(count));
            Assert.That(gotBase, Is.EqualTo(portBase));
        }

        [Test]
        public void Hello_EncodesAndRoundTrips()
        {
            NetDataWriter writer = new NetDataWriter();
            ServerMeshPhantomSink.WriteHello(writer, 7);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            Assert.That(Hex(writer), Is.EqualTo("F9 F4 F8 07 00 00 00"));
            Assert.That(ServerMeshPhantomSink.TryReadHello(reader, out int index), Is.True);
            Assert.That(index, Is.EqualTo(7));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void Hello_TruncatedOrAnotherType_IsRefused_PositionKept()
        {
            NetDataReader truncated = new NetDataReader(new byte[] { 0xF9, 0xF4, 0xF8, 0x07, 0x00 });
            NetDataReader other = new NetDataReader(new byte[] { 0x77, 0x07, 0x00, 0x00, 0x00 });

            Assert.That(ServerMeshPhantomSink.TryReadHello(truncated, out _), Is.False);
            Assert.That(truncated.Position, Is.EqualTo(0));
            Assert.That(ServerMeshPhantomSink.TryReadHello(other, out _), Is.False);
            Assert.That(other.Position, Is.EqualTo(0));
        }

        private static ServerMeshPhantomBindReply SampleReply()
        {
            return new ServerMeshPhantomBindReply
            {
                objectId = 2500,
                x = 12.5f,
                z = -40.25f,
                kind = 0,
                payload = 1,
                hz = 20,
                stand = 1,
                speed = 3f,
                centreX = 10f,
                centreZ = -38f,
                halfSide = 27.93f,
            };
        }

        [Test]
        public void BindReply_RoundTrips()
        {
            NetDataWriter writer = new NetDataWriter();
            SampleReply().Write(writer);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            Assert.That(writer.Data[0], Is.EqualTo(0xF9));
            Assert.That(writer.Data[1], Is.EqualTo(0xF4));
            Assert.That(writer.Data[2], Is.EqualTo(0xF9));
            Assert.That(ServerMeshPhantomBindReply.TryRead(reader, out ServerMeshPhantomBindReply reply), Is.True);
            Assert.That(reply, Is.EqualTo(SampleReply()));
            Assert.That(reader.EndOfData, Is.True);
            Assert.That(reply.Region.center.x, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(reply.Region.width, Is.EqualTo(55.86f).Within(1e-3f));
        }

        [Test]
        public void BindReply_TruncatedOrAHello_IsRefused_PositionKept()
        {
            NetDataWriter writer = new NetDataWriter();
            SampleReply().Write(writer);
            byte[] bytes = writer.CopyData();
            NetDataReader truncated = new NetDataReader(bytes, 0, bytes.Length - 1);
            NetDataWriter hello = new NetDataWriter();
            ServerMeshPhantomSink.WriteHello(hello, 3);
            NetDataReader notAReply = new NetDataReader(hello.CopyData());

            Assert.That(ServerMeshPhantomBindReply.TryRead(truncated, out _), Is.False);
            Assert.That(truncated.Position, Is.EqualTo(0));
            Assert.That(ServerMeshPhantomBindReply.TryRead(notAReply, out _), Is.False);
            Assert.That(notAReply.Position, Is.EqualTo(0));
        }

        [Test]
        public void UploadDue_WithA15Point6MsPollQuantum_TwentyPerSecond()
        {
            const double quantum = 0.0156, period = 0.05;
            double next = period * 0.37;
            int[] perSecond = new int[60];
            for (double now = 0; now < 60.0; now += quantum)
            {
                if (ServerMeshPhantomSink.UploadDue(now, period, ref next))
                    perSecond[(int)now]++;
            }

            for (int s = 1; s < perSecond.Length; ++s)
                Assert.That(perSecond[s], Is.InRange(19, 21), "second " + s);
        }

        [Test]
        public void UploadDue_AfterAStall_NoBurst()
        {
            double next = 0.0, period = 0.05;
            for (double now = 0; now < 1.0; now += 0.0156)
                ServerMeshPhantomSink.UploadDue(now, period, ref next);
            double stalled = 1.6;

            int burst = 0;
            for (int i = 0; i < 5; ++i)
                burst += ServerMeshPhantomSink.UploadDue(stalled, period, ref next) ? 1 : 0;

            Assert.That(burst, Is.EqualTo(1));
            Assert.That(next, Is.GreaterThan(stalled));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SinkWindowLine_ExactLine()
        {
            string line = ServerMeshPhantomSink.SinkWindowLine(5.0, 50, 50, 50, 2621440, 6000, new List<double> { 10.9, 9.4, 10.5, 10.0 },
                new ushort[] { 65001, 119 }, new long[] { 0, 5000 }, new long[] { 0, 2500000 }, 1000, 30000, 5000, 153600, 0, 1, 3.2);

            Assert.That(line, Is.EqualTo("Sink window 5.00 s: peers 50 connected 50 bound 50 | in 2621440 B 6000 pkt 512.0 kB/s | down n 4 mean 10.2 p95 10.9 max 10.9 kB/s | t119 calls 5000 bytes 2500000 | t65001 calls 0 bytes 0 | other calls 1000 bytes 30000 | out 5000 uploads 1000.0/s 30.0 kB/s | disconnects 0 | workers 1 maxPassMs 3.2"));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SinkWindowLine_MoreThanSixTypes_FoldsTheSmallestIntoOther_TiesByType()
        {
            ushort[] types = { 9, 1, 2, 3, 4, 5, 6, 7 };
            long[] calls = { 1, 1, 1, 1, 1, 1, 1, 1 };
            long[] bytes = { 50, 80, 80, 70, 60, 40, 30, 20 };

            string line = ServerMeshPhantomSink.SinkWindowLine(5.0, 1, 1, 1, 0, 0, new List<double>(), types, calls, bytes, 2, 100, 0, 0, 0, 1, 0);

            Assert.That(line, Does.Contain("| t1 calls 1 bytes 80 | t2 calls 1 bytes 80 | t3 calls 1 bytes 70 | t4 calls 1 bytes 60 | t9 calls 1 bytes 50 | t5 calls 1 bytes 40 | other calls 4 bytes 150 |"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SinkWindowLine_Empty_PrintsZeros()
        {
            string line = ServerMeshPhantomSink.SinkWindowLine(double.NaN, 0, 0, 0, -1, -1, new List<double>(), new ushort[0], new long[0], new long[0], -1, -1, -1, -1, -1, 0, double.NaN);

            Assert.That(line, Is.EqualTo("Sink window 0.00 s: peers 0 connected 0 bound 0 | in 0 B 0 pkt 0.0 kB/s | down n 0 mean 0.0 p95 0.0 max 0.0 kB/s | other calls 0 bytes 0 | out 0 uploads 0.0/s 0.0 kB/s | disconnects 0 | workers 0 maxPassMs 0.0"));
        }
    }
}
