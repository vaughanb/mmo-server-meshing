using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshStatePacketTests
    {
        private const long Timestamp = 1_790_000_000_000;

        private static string Hex(byte[] data, int length)
        {
            return BitConverter.ToString(data, 0, length).Replace("-", " ");
        }

        private static byte[] Payload(int length, byte seed)
        {
            byte[] payload = new byte[length];
            for (int i = 0; i < length; ++i)
                payload[i] = (byte)(seed + i);
            return payload;
        }

        private static IEnumerable<TestCaseData> BadCapCases()
        {
            yield return new TestCaseData(63).SetName("Constructor_Cap63_ThrowsNamingIt");
            yield return new TestCaseData(1021).SetName("Constructor_Cap1021_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(BadCapCases))]
        public void Constructor_CapOutOfRange_ThrowsNamingIt(int cap)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshStatePacketWriter(cap));

            Assert.That(thrown.ParamName, Is.EqualTo("maxPacketBytes"));
        }

        [Test]
        public void TryAppend_AboveMaxPayload_ReturnsFalseAndWritesNothing()
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);
            int max = writer.MaxPayload(7);

            bool appended = writer.TryAppend(7, new byte[max + 1], 0, max + 1);

            Assert.That(appended, Is.False);
            Assert.That(writer.Count, Is.EqualTo(0));
            Assert.That(writer.Length, Is.EqualTo(writer.HeaderLength));
        }

        private static IEnumerable<TestCaseData> BadSpanCases()
        {
            yield return new TestCaseData(-1, 2).SetName("TryAppend_NegativeOffset_Throws");
            yield return new TestCaseData(5, 1).SetName("TryAppend_OffsetPastTheBuffer_Throws");
            yield return new TestCaseData(2, 3).SetName("TryAppend_LengthPastTheBuffer_Throws");
            yield return new TestCaseData(0, -1).SetName("TryAppend_NegativeLength_Throws");
        }

        [TestCaseSource(nameof(BadSpanCases))]
        public void TryAppend_SpanOutsideTheBuffer_Throws(int offset, int length)
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);

            Assert.Throws<ArgumentOutOfRangeException>(() => writer.TryAppend(7, new byte[4], offset, length));
            Assert.That(writer.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryAppend_NullBuffer_ThrowsNamingIt()
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => writer.TryAppend(7, null, 0, 0));

            Assert.That(thrown.ParamName, Is.EqualTo("buffer"));
        }

        [Test]
        public void TryAppend_BeforeBegin_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => new MeshStatePacketWriter().TryAppend(7, new byte[1], 0, 1));
        }

        [Test]
        public void WriteSingle_NullWriter_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => MeshStatePacketWriter.WriteSingle(null, Timestamp, 7, new byte[1], 0, 1));

            Assert.That(thrown.ParamName, Is.EqualTo("writer"));
        }

        private static IEnumerable<TestCaseData> HeaderCases()
        {
            yield return new TestCaseData(1000L, "77 00 F7 E0 00 00 00 00").SetName("Begin_Timestamp1000_WritesTheKitHeader");
            yield return new TestCaseData(Timestamp, "77 F3 50 FB FF 27 5F 77 00 00 00 00").SetName("Begin_TodaysTimestamp_WritesTheKitHeader");
        }

        [TestCaseSource(nameof(HeaderCases))]
        public void Begin_WritesTheKitHeader(long timestamp, string expected)
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();

            writer.Begin(timestamp);
            NetDataWriter packet = writer.Finish();

            Assert.That(Hex(packet.Data, packet.Length), Is.EqualTo(expected));
            Assert.That(writer.HeaderLength, Is.EqualTo(packet.Length));
            Assert.That(writer.Timestamp, Is.EqualTo(timestamp));
            Assert.That(writer.IsEmpty, Is.True);
        }

        [Test]
        public void Packet_RoundTripsThroughTheKitLoop_EndingAtThePacketEnd()
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);
            byte[] first = Payload(22, 1), second = Payload(5, 100);
            Assert.That(writer.TryAppend(42, first, 0, first.Length), Is.True);
            Assert.That(writer.TryAppend(70000, second, 1, 3), Is.True);

            NetDataWriter packet = writer.Finish();
            List<(uint id, int length, byte[] bytes)> records = new List<(uint, int, byte[])>();
            bool parsed = MeshEntityStatePacketParser.TryParse(packet.Data, packet.Length, out ushort type, out long timestamp, out int count, records, out int end);

            Assert.That(parsed, Is.True);
            Assert.That(type, Is.EqualTo(MeshStatePacketWriter.EntityStateMessageType));
            Assert.That(timestamp, Is.EqualTo(Timestamp));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(end, Is.EqualTo(packet.Length));
            Assert.That(records[0].id, Is.EqualTo(42u));
            Assert.That(records[0].bytes, Is.EqualTo(first));
            Assert.That(records[1].id, Is.EqualTo(70000u));
            Assert.That(records[1].bytes, Is.EqualTo(new byte[] { 101, 102, 103 }));
            Assert.That(writer.Length, Is.EqualTo(packet.Length));
        }

        private static IEnumerable<TestCaseData> PackedIdCases()
        {
            yield return new TestCaseData(240u, 1).SetName("PackedUIntSize_240_IsOneByte");
            yield return new TestCaseData(241u, 2).SetName("PackedUIntSize_241_IsTwoBytes");
            yield return new TestCaseData(2287u, 2).SetName("PackedUIntSize_2287_IsTwoBytes");
            yield return new TestCaseData(2288u, 3).SetName("PackedUIntSize_2288_IsThreeBytes");
            yield return new TestCaseData(67823u, 3).SetName("PackedUIntSize_67823_IsThreeBytes");
            yield return new TestCaseData(67824u, 4).SetName("PackedUIntSize_67824_IsFourBytes");
            yield return new TestCaseData(16777215u, 4).SetName("PackedUIntSize_16777215_IsFourBytes");
            yield return new TestCaseData(16777216u, 5).SetName("PackedUIntSize_16777216_IsFiveBytes");
        }

        [TestCaseSource(nameof(PackedIdCases))]
        public void PackedUIntSize_MatchesTheKitEncoding(uint objectId, int expected)
        {
            NetDataWriter kit = new NetDataWriter();
            kit.PutPackedUInt(objectId);
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);
            writer.TryAppend(objectId, new byte[3], 0, 3);
            int recordBytes = writer.Length - writer.HeaderLength;

            Assert.That(MeshStatePacketWriter.PackedUIntSize(objectId), Is.EqualTo(expected));
            Assert.That(kit.Length, Is.EqualTo(expected));
            Assert.That(MeshStatePacketWriter.RecordSize(objectId, 3), Is.EqualTo(recordBytes));
            Assert.That(MeshEntityStatePacketParser.Parse(writer.Finish().CopyData())[0].id, Is.EqualTo(objectId));
        }

        [Test]
        public void MaxPayload_AtTheFullCap_Is1004LessTheIdBytes()
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(Timestamp);

            Assert.That(writer.HeaderLength, Is.EqualTo(12));
            Assert.That(writer.MaxPayload(42), Is.EqualTo(1003));
            Assert.That(writer.MaxPayload(70000), Is.EqualTo(1000));
        }

        private static IEnumerable<TestCaseData> StraddleCases()
        {
            foreach (int cap in new[] { 1020, 510, 255 })
                foreach (int payload in new[] { 21, 28, 56, -1 })
                    yield return new TestCaseData(cap, payload).SetName($"Packets_Cap{cap}_Payload{(payload < 0 ? "Max" : payload.ToString())}_StayWithinTheCap");
        }

        [TestCaseSource(nameof(StraddleCases))]
        public void Packets_StayWithinTheCapAndCarryEveryRecord(int cap, int payloadLength)
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter(cap);
            writer.Begin(Timestamp);
            int length = payloadLength < 0 ? writer.MaxPayload(300) : payloadLength;
            byte[] payload = Payload(length, 9);
            List<byte[]> packets = new List<byte[]>();
            const int recordsToWrite = 100;
            for (uint id = 300; id < 300 + recordsToWrite; ++id)
            {
                if (writer.TryAppend(id, payload, 0, length))
                    continue;
                packets.Add(writer.Finish().CopyData());
                writer.Begin(Timestamp);
                Assert.That(writer.TryAppend(id, payload, 0, length), Is.True);
            }
            packets.Add(writer.Finish().CopyData());

            int records = 0;
            uint expectedId = 300;
            foreach (byte[] packet in packets)
            {
                Assert.That(packet.Length, Is.LessThanOrEqualTo(cap));
                foreach ((uint id, int recordLength, byte[] bytes) in MeshEntityStatePacketParser.Parse(packet))
                {
                    Assert.That(id, Is.EqualTo(expectedId++));
                    Assert.That(bytes, Is.EqualTo(payload));
                    records++;
                }
            }
            Assert.That(records, Is.EqualTo(recordsToWrite));
        }

        [Test]
        public void WriteSingle_ThreeThousandBytes_ParsesAsOneRecord()
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)0xFF);
            byte[] payload = Payload(3000, 3);

            MeshStatePacketWriter.WriteSingle(writer, Timestamp, 42, payload, 0, payload.Length);
            List<(uint id, int length, byte[] bytes)> records = MeshEntityStatePacketParser.Parse(writer.CopyData(), out long timestamp);

            Assert.That(timestamp, Is.EqualTo(Timestamp));
            Assert.That(records.Count, Is.EqualTo(1));
            Assert.That(records[0].id, Is.EqualTo(42u));
            Assert.That(records[0].bytes, Is.EqualTo(payload));
        }

        [Test]
        public void EntityStateMessageType_IsTheKitsEntityState()
        {
            Assert.That(MeshStatePacketWriter.EntityStateMessageType, Is.EqualTo(GameNetworkingConsts.EntityState));
        }

        [Test]
        public void MaxPacketBytes_IsTheKitsUnreliableLimit()
        {
            Assert.That(MeshStatePacketWriter.MaxPacketBytes, Is.EqualTo(1020));
        }
    }
}
