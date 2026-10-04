using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshRecordSizeCountsTests
    {
        private const long Ts = 1_790_000_000_000;
        private const string EmptyGroup = " | sizes n 0 min 0 max 0 top 0:0 0:0 0:0 payload 0.0 framed 0.0 B over 0 unread 0";
        private const string ContractGroup = " | sizes n 5000 min 54 max 54 top 54:5000 0:0 0:0 payload 54.0 framed 60.0 B over 0 unread 0";

        private static byte[] Packet(params (uint id, int length)[] records)
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Ts);
            foreach ((uint id, int length) in records)
                Assert.That(writer.TryAppend(id, new byte[length], 0, length), Is.True);
            return writer.Finish().CopyData();
        }

        private static int HeaderLength()
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Ts);
            return writer.HeaderLength;
        }

        private static void SetInt(byte[] data, int position, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, position, 4);
        }

        private static IEnumerable<TestCaseData> SettingJunkCases()
        {
            foreach (string value in new[] { "2", "on", "true", "-1", "1 1", "{1}" })
                yield return new TestCaseData(value).SetName($"TryParseSetting_{value.Replace(" ", "Space").Replace("{", "(").Replace("}", ")")}_FailsNamingTheKey");
        }

        [TestCaseSource(nameof(SettingJunkCases))]
        public void TryParseSetting_Junk_FailsOffWithAWarningNamingTheKey(string value)
        {
            bool parsed = MeshRecordSizeCounts.TryParseSetting(value, out bool enabled, out string warning);

            Assert.That(parsed, Is.False);
            Assert.That(enabled, Is.False);
            Assert.That(warning, Does.StartWith(MeshRecordSizeCounts.EnvironmentKey + "="));
            Assert.That(warning, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase(" 0 ", false)]
        [TestCase("1", true)]
        [TestCase(" 1 ", true)]
        public void TryParseSetting_Valid(string value, bool expected)
        {
            Assert.That(MeshRecordSizeCounts.TryParseSetting(value, out bool enabled, out string warning), Is.True);
            Assert.That(enabled, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [Test]
        public void TryAddPacket_TruncatedAtEveryByte_CountsUnreadableAndKeepsEarlierRecords()
        {
            byte[] packet = Packet((7, 54), (300, 56));
            int firstEnd = HeaderLength() + 1 + 4 + 54;
            for (int end = 1; end < packet.Length; ++end)
            {
                MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

                bool read = counts.TryAddPacket(packet, 0, end);

                Assert.That(read, Is.False, $"end {end}");
                Assert.That(counts.Unreadable, Is.EqualTo(1), $"end {end}");
                Assert.That(counts.Records, Is.EqualTo(end >= firstEnd ? 1 : 0), $"end {end}");
            }
        }

        private static IEnumerable<TestCaseData> BrokenSpanCases()
        {
            yield return new TestCaseData(null, 0, 0).SetName("TryAddPacket_NullData_CountsUnreadable");
            yield return new TestCaseData(new byte[] { 0x77 }, -1, 1).SetName("TryAddPacket_NegativeOffset_CountsUnreadable");
            yield return new TestCaseData(new byte[] { 0x77 }, 0, 2).SetName("TryAddPacket_EndPastTheData_CountsUnreadable");
            yield return new TestCaseData(new byte[] { 0x77 }, 1, 1).SetName("TryAddPacket_EmptySpan_CountsUnreadable");
            yield return new TestCaseData(new byte[] { 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 9).SetName("TryAddPacket_UndecodableType_CountsUnreadable");
            yield return new TestCaseData(new byte[] { 0x77, 0xFC, 0, 0, 0, 0, 0, 0, 0 }, 0, 9).SetName("TryAddPacket_BadTimestampLeadByte_CountsUnreadable");
        }

        [TestCaseSource(nameof(BrokenSpanCases))]
        public void TryAddPacket_BrokenSpan_CountsUnreadable(byte[] data, int offset, int end)
        {
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(data, offset, end), Is.False);
            Assert.That(counts.Unreadable, Is.EqualTo(1));
            Assert.That(counts.Records, Is.EqualTo(0));
        }

        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void TryAddPacket_NegativeRecordLength_CountsUnreadableAndKeepsEarlierRecords(int length)
        {
            byte[] packet = Packet((7, 54), (8, 28));
            SetInt(packet, HeaderLength() + 1 + 4 + 54 + 1, length);
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(packet, 0, packet.Length), Is.False);
            Assert.That(counts.Unreadable, Is.EqualTo(1));
            Assert.That(counts.Records, Is.EqualTo(1));
            Assert.That(counts.CountOf(54), Is.EqualTo(1));
        }

        [Test]
        public void TryAddPacket_LengthPastTheEnd_CountsUnreadableAndKeepsEarlierRecords()
        {
            byte[] packet = Packet((7, 54), (8, 28));
            SetInt(packet, HeaderLength() + 1 + 4 + 54 + 1, 29);
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(packet, 0, packet.Length), Is.False);
            Assert.That(counts.Unreadable, Is.EqualTo(1));
            Assert.That(counts.Records, Is.EqualTo(1));
        }

        [Test]
        public void TryAddPacket_CountAboveTheRecordsPresent_CountsUnreadableAndKeepsThem()
        {
            byte[] packet = Packet((7, 54), (8, 28));
            SetInt(packet, HeaderLength() - 4, 3);
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(packet, 0, packet.Length), Is.False);
            Assert.That(counts.Unreadable, Is.EqualTo(1));
            Assert.That(counts.Records, Is.EqualTo(2));
        }

        [Test]
        public void TryAddPacket_NegativeCount_CountsUnreadable()
        {
            byte[] packet = Packet((7, 54));
            SetInt(packet, HeaderLength() - 4, -1);
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(packet, 0, packet.Length), Is.False);
            Assert.That(counts.Unreadable, Is.EqualTo(1));
        }

        [Test]
        public void TryAddPacket_WriterPacket_GivesExactCounts()
        {
            uint[] ids = { 240, 241, 2288 };
            byte[] packet = Packet((ids[0], 54), (ids[1], 56), (ids[2], 28));
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            bool read = counts.TryAddPacket(packet, 0, packet.Length);

            long framing = 0;
            foreach (uint id in ids)
                framing += 4 + MeshStatePacketWriter.PackedUIntSize(id);
            Assert.That(read, Is.True);
            Assert.That(counts.Records, Is.EqualTo(3));
            Assert.That(counts.PayloadBytes, Is.EqualTo(54 + 56 + 28));
            Assert.That(counts.FramedBytes - counts.PayloadBytes, Is.EqualTo(framing));
            Assert.That(framing, Is.EqualTo(18));
            Assert.That(counts.Min, Is.EqualTo(28));
            Assert.That(counts.Max, Is.EqualTo(56));
            Assert.That(counts.CountOf(54), Is.EqualTo(1));
            Assert.That(counts.CountOf(56), Is.EqualTo(1));
            Assert.That(counts.CountOf(28), Is.EqualTo(1));
            Assert.That(counts.CountOf(-1), Is.EqualTo(0));
            Assert.That(counts.Over, Is.EqualTo(0));
            Assert.That(counts.Unreadable, Is.EqualTo(0));
        }

        [Test]
        public void TryAddPacket_AtAnOffset_ReadsFromThere()
        {
            byte[] packet = Packet((7, 54));
            byte[] data = new byte[packet.Length + 9];
            Buffer.BlockCopy(packet, 0, data, 5, packet.Length);
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(data, 5, 5 + packet.Length), Is.True);
            Assert.That(counts.Records, Is.EqualTo(1));
            Assert.That(counts.FramedBytes, Is.EqualTo(59));
        }

        [Test]
        public void TryAddPacket_LongPayload_CountsOver()
        {
            byte[] packet = Packet((7, 300), (7, 255));
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            counts.TryAddPacket(packet, 0, packet.Length);

            Assert.That(counts.Over, Is.EqualTo(1));
            Assert.That(counts.CountOf(255), Is.EqualTo(1));
            Assert.That(counts.CountOf(300), Is.EqualTo(0));
            Assert.That(counts.Max, Is.EqualTo(300));
        }

        [Test]
        public void TryAddPacket_EmptyPacket_IsReadWithNoRecord()
        {
            byte[] packet = Packet();
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(counts.TryAddPacket(packet, 0, packet.Length), Is.True);
            Assert.That(counts.Records, Is.EqualTo(0));
            Assert.That(counts.ToGroup(), Is.EqualTo(EmptyGroup));
        }

        [Test]
        public void TopLengths_MostFrequentFirst_TiesBySmallerLength()
        {
            List<(uint, int)> records = new List<(uint, int)>();
            for (int i = 0; i < 5; ++i)
                records.Add((7, 54));
            for (int i = 0; i < 5; ++i)
                records.Add((7, 28));
            for (int i = 0; i < 7; ++i)
                records.Add((7, 56));
            records.Add((7, 100));
            byte[] packet = Packet(records.ToArray());
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();
            counts.TryAddPacket(packet, 0, packet.Length);

            counts.TopLengths(out int l1, out long c1, out int l2, out long c2, out int l3, out long c3);

            Assert.That((l1, c1, l2, c2, l3, c3), Is.EqualTo((56, 7L, 28, 5L, 54, 5L)));
        }

        [Test, SetCulture("de-DE")]
        public void ToGroup_ContractSample_IsExactUnderAGermanCulture()
        {
            List<(uint, int)> records = new List<(uint, int)>();
            for (int i = 0; i < 10; ++i)
                records.Add((300, 54));
            byte[] packet = Packet(records.ToArray());
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            for (int i = 0; i < 500; ++i)
                counts.TryAddPacket(packet, 0, packet.Length);

            Assert.That(counts.ToGroup(), Is.EqualTo(ContractGroup));
        }

        [Test, SetCulture("de-DE")]
        public void ToGroup_MixedLengths_IsExact()
        {
            byte[] packet = Packet((240, 54), (241, 56), (2288, 28));
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();
            counts.TryAddPacket(packet, 0, packet.Length);
            counts.TryAddPacket(packet, 0, 3);

            Assert.That(counts.ToGroup(), Is.EqualTo(" | sizes n 3 min 28 max 56 top 28:1 54:1 56:1 payload 46.0 framed 52.0 B over 0 unread 1"));
        }

        [Test]
        public void ToGroup_Empty_IsZeros()
        {
            Assert.That(new MeshRecordSizeCounts().ToGroup(), Is.EqualTo(EmptyGroup));
        }

        [Test]
        public void AddCopyToAndClear_KeepEveryFigure()
        {
            byte[] a = Packet((7, 54), (7, 54));
            byte[] b = Packet((300, 28), (7, 400));
            MeshRecordSizeCounts first = new MeshRecordSizeCounts();
            MeshRecordSizeCounts second = new MeshRecordSizeCounts();
            first.TryAddPacket(a, 0, a.Length);
            second.TryAddPacket(b, 0, b.Length);
            second.TryAddPacket(b, 0, 2);
            MeshRecordSizeCounts total = new MeshRecordSizeCounts();

            total.Add(first);
            total.Add(second);
            total.Add(new MeshRecordSizeCounts());
            MeshRecordSizeCounts copy = new MeshRecordSizeCounts();
            total.CopyTo(copy);
            total.Clear();

            Assert.That(copy.Records, Is.EqualTo(4));
            Assert.That(copy.PayloadBytes, Is.EqualTo(54 + 54 + 28 + 400));
            Assert.That(copy.FramedBytes, Is.EqualTo(59 + 59 + 34 + 405));
            Assert.That(copy.Min, Is.EqualTo(28));
            Assert.That(copy.Max, Is.EqualTo(400));
            Assert.That(copy.Over, Is.EqualTo(1));
            Assert.That(copy.Unreadable, Is.EqualTo(1));
            Assert.That(copy.CountOf(54), Is.EqualTo(2));
            Assert.That(copy.CountOf(28), Is.EqualTo(1));
            Assert.That(total.ToGroup(), Is.EqualTo(EmptyGroup));
            Assert.That(total.CountOf(54), Is.EqualTo(0));
        }

        [Test]
        public void AddAndCopyTo_Null_ThrowNamingTheArgument()
        {
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();

            Assert.That(Assert.Throws<ArgumentNullException>(() => counts.Add(null)).ParamName, Is.EqualTo("other"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => counts.CopyTo(null)).ParamName, Is.EqualTo("into"));
        }

        [Test]
        public void TryAddPacket_AllocatesNothing()
        {
            byte[] packet = Packet((7, 54), (300, 56), (70000, 28));
            MeshRecordSizeCounts counts = new MeshRecordSizeCounts();
            counts.TryAddPacket(packet, 0, packet.Length);

            Assert.That(() =>
            {
                counts.TryAddPacket(packet, 0, packet.Length);
                counts.TryAddPacket(packet, 0, 5);
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
