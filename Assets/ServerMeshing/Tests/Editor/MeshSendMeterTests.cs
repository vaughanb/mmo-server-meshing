using System;
using System.Collections.Generic;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshSendMeterTests
    {
        private const long TicksPerSecond = 1_000_000;
        private const string ContractLine = "Send window 5.00 s: calls 1250 250.0/s bytes 512000 100.0 kB/s | t119 calls 1000 bytes 501760 200.0/s 98.0 kB/s 0.00 us | t2 calls 250 bytes 10240 50.0/s 2.0 kB/s 0.00 us | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 250 10240 rs 0 0 u 1000 501760 other 0 0 | channels 0:250 2:1000 other:0 | wire 530000 B 1300 pkt | offThread 0";
        private const string EmptyLine = "Send window 0.00 s: calls 0 0.0/s bytes 0 0.0 kB/s | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 0 0 | channels other:0 | wire 0 B 0 pkt | offThread 0";

        private static byte[] Bytes(string hex)
        {
            if (hex.Length == 0)
                return new byte[0];
            string[] parts = hex.Split(' ');
            byte[] data = new byte[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
                data[i] = Convert.ToByte(parts[i], 16);
            return data;
        }

        private static IEnumerable<TestCaseData> UndecodableCases()
        {
            yield return new TestCaseData(null, 0, 0).SetName("TryReadMessageType_NullData_Fails");
            yield return new TestCaseData("", 0, 0).SetName("TryReadMessageType_Empty_Fails");
            yield return new TestCaseData("77", 0, 0).SetName("TryReadMessageType_EmptySpan_Fails");
            yield return new TestCaseData("77", 1, 1).SetName("TryReadMessageType_OffsetAtEnd_Fails");
            yield return new TestCaseData("77", -1, 1).SetName("TryReadMessageType_NegativeOffset_Fails");
            yield return new TestCaseData("77", 0, 2).SetName("TryReadMessageType_EndPastTheData_Fails");
            yield return new TestCaseData("F1", 0, 1).SetName("TryReadMessageType_TruncatedTwoByteForm_Fails");
            yield return new TestCaseData("F1 01", 0, 1).SetName("TryReadMessageType_TwoByteFormCutByEnd_Fails");
            yield return new TestCaseData("F9", 0, 1).SetName("TryReadMessageType_ThreeByteFormWithOneByte_Fails");
            yield return new TestCaseData("F9 F4", 0, 2).SetName("TryReadMessageType_ThreeByteFormWithTwoBytes_Fails");
            yield return new TestCaseData("FA 00 00 00", 0, 4).SetName("TryReadMessageType_LeadByte250_Fails");
            yield return new TestCaseData("FF 00 00 00 00 00 00 00 00", 0, 9).SetName("TryReadMessageType_LeadByte255_Fails");
            yield return new TestCaseData("F9 FF FF", 0, 3).SetName("TryReadMessageType_AboveUShortMax_Fails");
            yield return new TestCaseData("F9 F7 10", 0, 3).SetName("TryReadMessageType_OneAboveUShortMax_Fails");
        }

        [TestCaseSource(nameof(UndecodableCases))]
        public void TryReadMessageType_Undecodable(string hex, int offset, int end)
        {
            byte[] data = hex == null ? null : Bytes(hex);

            bool read = MeshSendMeter.TryReadMessageType(data, offset, end, out ushort type, out int typeBytes);

            Assert.That(read, Is.False);
            Assert.That(type, Is.EqualTo(0));
            Assert.That(typeBytes, Is.EqualTo(0));
        }

        private static IEnumerable<TestCaseData> GoldenCases()
        {
            yield return new TestCaseData((ushort)0, "00").SetName("TryReadMessageType_0_OneByte");
            yield return new TestCaseData((ushort)119, "77").SetName("TryReadMessageType_119_OneByte");
            yield return new TestCaseData((ushort)240, "F0").SetName("TryReadMessageType_240_OneByte");
            yield return new TestCaseData((ushort)241, "F1 01").SetName("TryReadMessageType_241_TwoBytes");
            yield return new TestCaseData((ushort)2287, "F8 FF").SetName("TryReadMessageType_2287_TwoBytes");
            yield return new TestCaseData((ushort)2288, "F9 00 00").SetName("TryReadMessageType_2288_ThreeBytes");
            yield return new TestCaseData((ushort)65000, "F9 F4 F8").SetName("TryReadMessageType_65000_ThreeBytes");
            yield return new TestCaseData((ushort)65535, "F9 F7 0F").SetName("TryReadMessageType_65535_ThreeBytes");
        }

        [TestCaseSource(nameof(GoldenCases))]
        public void TryReadMessageType_Golden(ushort expected, string hex)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(expected);
            writer.Put((byte)0xAB);
            byte[] golden = Bytes(hex);

            Assert.That(BitConverter.ToString(writer.CopyData(), 0, golden.Length).Replace("-", " "), Is.EqualTo(hex));
            Assert.That(MeshSendMeter.TryReadMessageType(writer.Data, writer.Length, out ushort type, out int typeBytes), Is.True);
            Assert.That(type, Is.EqualTo(expected));
            Assert.That(typeBytes, Is.EqualTo(golden.Length));

            byte[] shifted = new byte[4 + golden.Length];
            Array.Copy(golden, 0, shifted, 4, golden.Length);
            Assert.That(MeshSendMeter.TryReadMessageType(shifted, 4, shifted.Length, out ushort atOffset, out int offsetBytes), Is.True);
            Assert.That(atOffset, Is.EqualTo(expected));
            Assert.That(offsetBytes, Is.EqualTo(golden.Length));
        }

        private static IEnumerable<TestCaseData> ConstructorFailureCases()
        {
            yield return new TestCaseData(0L).SetName("Constructor_ZeroTicksPerSecond_Throws");
            yield return new TestCaseData(-1L).SetName("Constructor_NegativeTicksPerSecond_Throws");
        }

        [TestCaseSource(nameof(ConstructorFailureCases))]
        public void Constructor_NonPositiveFrequency_ThrowsNamingTheArgument(long ticksPerSecond)
        {
            Assert.That(() => new MeshSendMeter(ticksPerSecond),
                Throws.TypeOf<ArgumentOutOfRangeException>().With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo("ticksPerSecond"));
        }

        private static MeshSendWindow MixedWindow()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, false);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, true);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, false);
            meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 40, 0, false);
            meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 40, 0, false);
            meter.Record(2288, DeliveryMethod.Sequenced, 15, 10, 0, false);
            meter.Record(65535, DeliveryMethod.ReliableSequenced, 1, 7, 0, true);
            meter.Record(0, DeliveryMethod.ReliableUnordered, 0, 1, 0, false);
            return meter.TakeWindow(2.5d);
        }

        private static IEnumerable<TestCaseData> MixedTotalCases()
        {
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(5)), 0L).SetName("Totals_UnseenTypeCalls_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(5)), 0L).SetName("Totals_UnseenTypeBytes_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls((DeliveryMethod)7)), 0L).SetName("Totals_UndefinedDeliveryCalls_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.ChannelCalls(16)), 0L).SetName("Totals_OtherChannelCalls_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.OtherCalls), 0L).SetName("Totals_OtherCalls_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.OtherBytes), 0L).SetName("Totals_OtherBytes_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.TooBigCalls), 0L).SetName("Totals_TooBigCalls_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.TooBigBytes), 0L).SetName("Totals_TooBigBytes_AreZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.OffThread), 0L).SetName("Totals_OffThread_IsZero");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(119)), 3L).SetName("Totals_Type119Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(119)), 300L).SetName("Totals_Type119Bytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(2)), 2L).SetName("Totals_Type2Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(2)), 80L).SetName("Totals_Type2Bytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(2288)), 1L).SetName("Totals_Type2288Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(2288)), 10L).SetName("Totals_Type2288Bytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(65535)), 1L).SetName("Totals_Type65535Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(65535)), 7L).SetName("Totals_Type65535Bytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Calls(0)), 1L).SetName("Totals_Type0Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Bytes(0)), 1L).SetName("Totals_Type0Bytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.TypeCount), 5L).SetName("Totals_TypeCount");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.TotalCalls), 8L).SetName("Totals_TotalCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.TotalBytes), 398L).SetName("Totals_TotalBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.Refused), 2L).SetName("Totals_Refused");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls(DeliveryMethod.ReliableUnordered)), 1L).SetName("Totals_ReliableUnorderedCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryBytes(DeliveryMethod.ReliableUnordered)), 1L).SetName("Totals_ReliableUnorderedBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls(DeliveryMethod.Sequenced)), 1L).SetName("Totals_SequencedCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryBytes(DeliveryMethod.Sequenced)), 10L).SetName("Totals_SequencedBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls(DeliveryMethod.ReliableOrdered)), 2L).SetName("Totals_ReliableOrderedCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryBytes(DeliveryMethod.ReliableOrdered)), 80L).SetName("Totals_ReliableOrderedBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls(DeliveryMethod.ReliableSequenced)), 1L).SetName("Totals_ReliableSequencedCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryBytes(DeliveryMethod.ReliableSequenced)), 7L).SetName("Totals_ReliableSequencedBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryCalls(DeliveryMethod.Unreliable)), 3L).SetName("Totals_UnreliableCalls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.DeliveryBytes(DeliveryMethod.Unreliable)), 300L).SetName("Totals_UnreliableBytes");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.ChannelCalls(0)), 3L).SetName("Totals_Channel0Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.ChannelCalls(1)), 1L).SetName("Totals_Channel1Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.ChannelCalls(2)), 3L).SetName("Totals_Channel2Calls");
            yield return new TestCaseData(new Func<MeshSendWindow, long>(w => w.ChannelCalls(15)), 1L).SetName("Totals_Channel15Calls");
        }

        [TestCaseSource(nameof(MixedTotalCases))]
        public void Totals_AreExact(Func<MeshSendWindow, long> accessor, long expected)
        {
            MeshSendWindow window = MixedWindow();

            Assert.That(accessor(window), Is.EqualTo(expected));
            Assert.That(window.Seconds, Is.EqualTo(2.5d));
        }

        [Test]
        public void Record_SixtyFifthDistinctType_GoesToOther()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            for (int type = 1000; type < 1000 + MeshSendMeter.TypeSlots; ++type)
                meter.Record((ushort)type, DeliveryMethod.Unreliable, 2, 10, 0, false);
            meter.Record(5000, DeliveryMethod.Unreliable, 2, 33, 0, false);
            meter.Record(5000, DeliveryMethod.Unreliable, 2, 33, 0, true);
            meter.Record(1000, DeliveryMethod.Unreliable, 2, 10, 0, false);

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.TypeCount, Is.EqualTo(MeshSendMeter.TypeSlots));
            Assert.That(window.Calls(5000), Is.EqualTo(0));
            Assert.That(window.OtherCalls, Is.EqualTo(2));
            Assert.That(window.OtherBytes, Is.EqualTo(66));
            Assert.That(window.Calls(1000), Is.EqualTo(2));
            Assert.That(window.Calls(1063), Is.EqualTo(1));
            Assert.That(window.TotalCalls, Is.EqualTo(MeshSendMeter.TypeSlots + 3));
            Assert.That(window.TotalBytes, Is.EqualTo(MeshSendMeter.TypeSlots * 10 + 76));
            Assert.That(window.Refused, Is.EqualTo(1));
        }

        [Test]
        public void Record_TypesSharingASlot_StayApart()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(7, DeliveryMethod.Unreliable, 2, 1, 0, false);
            meter.Record(71, DeliveryMethod.Unreliable, 2, 20, 0, false);
            meter.Record(71, DeliveryMethod.Unreliable, 2, 20, 0, false);
            meter.Record(135, DeliveryMethod.Unreliable, 2, 300, 0, false);
            meter.Record(7, DeliveryMethod.Unreliable, 2, 1, 0, false);

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.TypeCount, Is.EqualTo(3));
            Assert.That(window.Calls(7), Is.EqualTo(2));
            Assert.That(window.Bytes(7), Is.EqualTo(2));
            Assert.That(window.Calls(71), Is.EqualTo(2));
            Assert.That(window.Bytes(71), Is.EqualTo(40));
            Assert.That(window.Calls(135), Is.EqualTo(1));
            Assert.That(window.Bytes(135), Is.EqualTo(300));
            Assert.That(window.OtherCalls, Is.EqualTo(0));
        }

        [Test]
        public void Record_UndefinedDeliveryAndOutOfRangeChannel_GoToTheOtherSlots()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, (DeliveryMethod)7, 16, 12, 0, false);
            meter.Record(119, (DeliveryMethod)5, 200, 3, 0, false);

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.DeliveryCalls((DeliveryMethod)7), Is.EqualTo(2));
            Assert.That(window.DeliveryBytes((DeliveryMethod)7), Is.EqualTo(15));
            Assert.That(window.DeliveryCalls((DeliveryMethod)5), Is.EqualTo(2));
            Assert.That(window.DeliveryCalls(DeliveryMethod.Unreliable), Is.EqualTo(0));
            Assert.That(window.ChannelCalls(16), Is.EqualTo(2));
            Assert.That(window.ChannelCalls(-1), Is.EqualTo(2));
            Assert.That(window.ChannelCalls(0), Is.EqualTo(0));
            Assert.That(window.ToLine(0, 0), Does.Contain("| delivery ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 2 15 | channels other:2 |"));
        }

        [Test]
        public void Record_FromAnotherThread_CountsOnlyOffThread()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 40, 0, false);
            Thread other = new Thread(() => meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 5, true));
            other.Start();
            other.Join();

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.OffThread, Is.EqualTo(1));
            Assert.That(window.TotalCalls, Is.EqualTo(1));
            Assert.That(window.TotalBytes, Is.EqualTo(40));
            Assert.That(window.Calls(119), Is.EqualTo(0));
            Assert.That(window.TypeCount, Is.EqualTo(1));
            Assert.That(window.Refused, Is.EqualTo(0));
            Assert.That(window.DeliveryCalls(DeliveryMethod.Unreliable), Is.EqualTo(0));
            Assert.That(window.ChannelCalls(2), Is.EqualTo(0));
            Assert.That(meter.TakeWindow(1d).OffThread, Is.EqualTo(0));
        }

        [Test]
        public void RecordTooBig_MovesOnlyTheTooBigTotals()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.RecordTooBig(119, DeliveryMethod.Unreliable, 1100);
            meter.RecordTooBig(2, DeliveryMethod.Sequenced, 1030);

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.TooBigCalls, Is.EqualTo(2));
            Assert.That(window.TooBigBytes, Is.EqualTo(2130));
            Assert.That(window.TotalCalls, Is.EqualTo(0));
            Assert.That(window.TotalBytes, Is.EqualTo(0));
            Assert.That(window.TypeCount, Is.EqualTo(0));
            Assert.That(window.Refused, Is.EqualTo(0));
            Assert.That(window.DeliveryCalls(DeliveryMethod.Unreliable), Is.EqualTo(0));
            Assert.That(window.ChannelCalls(0), Is.EqualTo(0));
        }

        [Test]
        public void RecordUnknown_GoesToOtherAndTheRoutingTotals()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.RecordUnknown(DeliveryMethod.ReliableOrdered, 0, 0, 0, true);

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.TypeCount, Is.EqualTo(0));
            Assert.That(window.OtherCalls, Is.EqualTo(1));
            Assert.That(window.OtherBytes, Is.EqualTo(0));
            Assert.That(window.TotalCalls, Is.EqualTo(1));
            Assert.That(window.Refused, Is.EqualTo(1));
            Assert.That(window.DeliveryCalls(DeliveryMethod.ReliableOrdered), Is.EqualTo(1));
            Assert.That(window.ChannelCalls(0), Is.EqualTo(1));
        }

        [Test]
        public void TakeWindow_StartsTheNextWindowEmpty()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 9, true);
            meter.RecordUnknown(DeliveryMethod.Unreliable, 2, 0, 0, false);
            meter.RecordTooBig(119, DeliveryMethod.Unreliable, 1100);
            meter.TakeWindow(5d);

            MeshSendWindow next = meter.TakeWindow(0d);

            Assert.That(next.TypeCount, Is.EqualTo(0));
            Assert.That(next.Calls(119), Is.EqualTo(0));
            Assert.That(next.TotalCalls, Is.EqualTo(0));
            Assert.That(next.OtherCalls, Is.EqualTo(0));
            Assert.That(next.Refused, Is.EqualTo(0));
            Assert.That(next.TooBigCalls, Is.EqualTo(0));
            Assert.That(next.MeanMicroseconds(119), Is.EqualTo(0d));
            Assert.That(next.ToLine(-5, -1), Is.EqualTo(EmptyLine));
        }

        [Test]
        public void Record_TimedAgainstCountOnly()
        {
            MeshSendMeter timed = new MeshSendMeter(TicksPerSecond);
            MeshSendMeter counted = new MeshSendMeter(TicksPerSecond);
            for (int i = 0; i < 4; ++i)
            {
                timed.Record(119, DeliveryMethod.Unreliable, 2, 100, 3, false);
                counted.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, false);
            }

            MeshSendWindow timedWindow = timed.TakeWindow(1d);
            MeshSendWindow countedWindow = counted.TakeWindow(1d);

            Assert.That(timedWindow.MeanMicroseconds(119), Is.EqualTo(3d).Within(1e-9));
            Assert.That(countedWindow.MeanMicroseconds(119), Is.EqualTo(0d));
            Assert.That(timedWindow.ToLine(0, 0), Does.Contain("| t119 calls 4 bytes 400 4.0/s 0.4 kB/s 3.00 us |"));
            Assert.That(countedWindow.ToLine(0, 0), Does.Contain("| t119 calls 4 bytes 400 4.0/s 0.4 kB/s 0.00 us |"));
        }

        private static IEnumerable<TestCaseData> AllocationCases()
        {
            yield return new TestCaseData(0L).SetName("Record_Untimed_AllocatesNothing");
            yield return new TestCaseData(17L).SetName("Record_Timed_AllocatesNothing");
        }

        [TestCaseSource(nameof(AllocationCases))]
        public void Record_AllocatesNothing(long elapsedTicks)
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, elapsedTicks, false);
            meter.RecordUnknown(DeliveryMethod.Unreliable, 2, 0, elapsedTicks, false);
            meter.RecordTooBig(119, DeliveryMethod.Unreliable, 1100);

            Assert.That(() =>
            {
                meter.Record(119, DeliveryMethod.Unreliable, 2, 100, elapsedTicks, false);
                meter.Record(4321, DeliveryMethod.ReliableOrdered, 0, 30, elapsedTicks, true);
                meter.RecordUnknown(DeliveryMethod.Unreliable, 2, 0, elapsedTicks, false);
                meter.RecordTooBig(119, DeliveryMethod.Unreliable, 1100);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_ContractSample_IsExactUnderAGermanCulture()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            for (int i = 0; i < 760; ++i)
                meter.Record(119, DeliveryMethod.Unreliable, 2, 502, 0, false);
            for (int i = 0; i < 240; ++i)
                meter.Record(119, DeliveryMethod.Unreliable, 2, 501, 0, false);
            for (int i = 0; i < 240; ++i)
                meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 41, 0, false);
            for (int i = 0; i < 10; ++i)
                meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 40, 0, false);

            string line = meter.TakeWindow(5d).ToLine(530000, 1300);

            Assert.That(line, Is.EqualTo(ContractLine));
        }

        [Test]
        public void ToLine_NamesSixTypesByBytesAndFoldsTheRestIntoOther()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(1, DeliveryMethod.Unreliable, 2, 100, 0, false);
            meter.Record(2, DeliveryMethod.Unreliable, 2, 100, 0, false);
            meter.Record(3, DeliveryMethod.Unreliable, 2, 300, 0, false);
            meter.Record(4, DeliveryMethod.Unreliable, 2, 50, 0, false);
            meter.Record(5, DeliveryMethod.Unreliable, 2, 400, 0, false);
            meter.Record(7, DeliveryMethod.Unreliable, 2, 10, 0, false);
            meter.Record(6, DeliveryMethod.Unreliable, 2, 10, 0, false);
            meter.Record(8, DeliveryMethod.Unreliable, 2, 5, 0, false);

            MeshSendWindow window = meter.TakeWindow(1d);
            string line = window.ToLine(0, 0);

            Assert.That(line, Does.StartWith("Send window 1.00 s: calls 8 8.0/s bytes 975 1.0 kB/s | t5 calls 1 bytes 400 1.0/s 0.4 kB/s 0.00 us | t3 calls 1 bytes 300 1.0/s 0.3 kB/s 0.00 us | t1 calls 1 bytes 100 1.0/s 0.1 kB/s 0.00 us | t2 calls 1 bytes 100 1.0/s 0.1 kB/s 0.00 us | t4 calls 1 bytes 50 1.0/s 0.0 kB/s 0.00 us | t6 calls 1 bytes 10 1.0/s 0.0 kB/s 0.00 us | other calls 2 bytes 15 2.0/s 0.0 kB/s 0.00 us | refused 0"));
            Assert.That(window.OtherCalls, Is.EqualTo(0));
            Assert.That(window.Calls(7), Is.EqualTo(1));
        }

        [Test]
        public void ToLine_NeverHoldsBracesNegativesOrTheWorkWindowPrefix()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, true);
            meter.RecordTooBig(119, DeliveryMethod.Unreliable, 1100);

            string line = meter.TakeWindow(double.NaN).ToLine(long.MinValue, -3);

            Assert.That(line, Does.StartWith("Send window 0.00 s: calls 1 0.0/s bytes 100 0.0 kB/s"));
            Assert.That(line, Does.Not.Contain("-"));
            Assert.That(line, Does.Not.Contain("{"));
            Assert.That(line, Does.Not.Contain("}"));
            Assert.That(line, Does.Not.Contain("NaN"));
            Assert.That(line, Does.Not.Contain("Exception"));
            Assert.That(line, Does.Contain("| refused 1 tooBig 1 bytes 1100 |"));
            Assert.That(line, Does.Contain("| wire 0 B 0 pkt | offThread 0"));
        }

        private const string RecordsLine = "Send window 5.00 s: calls 1250 250.0/s bytes 512000 100.0 kB/s | t119 calls 1000 bytes 501760 200.0/s 98.0 kB/s 0.00 us | t2 calls 250 bytes 10240 50.0/s 2.0 kB/s 0.00 us | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 250 10240 rs 0 0 u 1000 501760 other 0 0 | records ro 300 u 49700 other 5 unread 2 | channels 0:250 2:1000 other:0 | wire 530000 B 1300 pkt | offThread 0";

        private static byte[] EntityStateHeader(long timestamp, int count)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(119);
            writer.PutPackedLong(timestamp);
            writer.Put(count);
            return writer.CopyData();
        }

        private static IEnumerable<TestCaseData> UnreadableCountCases()
        {
            yield return new TestCaseData(null, 0, 0).SetName("TryReadEntityStateCount_NullData_Fails");
            yield return new TestCaseData("", 0, 0).SetName("TryReadEntityStateCount_Empty_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 1).SetName("TryReadEntityStateCount_CutAfterTheType_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 2).SetName("TryReadEntityStateCount_CutAfterTheHighWord_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 3).SetName("TryReadEntityStateCount_CutInsideTheLowWord_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 4).SetName("TryReadEntityStateCount_CutBeforeTheCount_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 7).SetName("TryReadEntityStateCount_CutInsideTheCount_Fails");
            yield return new TestCaseData("77 FC 00 00 00 00 00 00 00 00 00 00 00 00", 0, 14).SetName("TryReadEntityStateCount_LeadByte252_Fails");
            yield return new TestCaseData("77 00 FF 00 00 00 00 00 00 00 00 00 00 00", 0, 14).SetName("TryReadEntityStateCount_LeadByte255_Fails");
            yield return new TestCaseData("77 00 F7 E0 FF FF FF FF", 0, 8).SetName("TryReadEntityStateCount_NegativeCount_Fails");
            yield return new TestCaseData("77 00 F7 E0 03 00 00 00", 0, 9).SetName("TryReadEntityStateCount_EndPastTheData_Fails");
        }

        [TestCaseSource(nameof(UnreadableCountCases))]
        public void TryReadEntityStateCount_Unreadable(string hex, int offset, int end)
        {
            byte[] data = hex == null ? null : Bytes(hex);

            bool read = MeshSendMeter.TryReadEntityStateCount(data, offset, end, out int count);

            Assert.That(read, Is.False);
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void TryReadEntityStateCount_GoldenHeaderForTimestamp1000_ReadsTheCount()
        {
            byte[] data = Bytes("77 00 F7 E0 03 00 00 00");

            Assert.That(MeshSendMeter.TryReadEntityStateCount(data, 0, data.Length, out int count), Is.True);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(EntityStateHeader(1000, 3), Is.EqualTo(data));
        }

        [TestCase(1_790_000_000_000L, 0)]
        [TestCase(1_790_000_000_000L, 254)]
        [TestCase(1L, 70000)]
        [TestCase(long.MaxValue, int.MaxValue)]
        public void TryReadEntityStateCount_HeaderTheKitWrites_ReadsTheCount(long timestamp, int expected)
        {
            byte[] data = EntityStateHeader(timestamp, expected);

            Assert.That(MeshSendMeter.TryReadEntityStateCount(data, 0, data.Length, out int count), Is.True);
            Assert.That(count, Is.EqualTo(expected));
        }

        [Test]
        public void TryReadEntityStateCount_AtAnOffset_ReadsFromThere()
        {
            byte[] header = EntityStateHeader(1_790_000_000_000L, 9);
            byte[] data = new byte[header.Length + 3];
            Array.Copy(header, 0, data, 2, header.Length);

            Assert.That(MeshSendMeter.TryReadEntityStateCount(data, 2, 2 + header.Length, out int count), Is.True);
            Assert.That(count, Is.EqualTo(9));
        }

        [Test]
        public void RecordEntityStateRecords_FromAnotherThread_CountsOnlyOffThread()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            Thread other = new Thread(() =>
            {
                meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, 40);
                meter.RecordEntityStateUnreadable();
            });
            other.Start();
            other.Join();

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.OffThread, Is.EqualTo(2));
            Assert.That(window.EntityStateRecordsTotal, Is.EqualTo(0));
            Assert.That(window.EntityStateUnreadable, Is.EqualTo(0));
            Assert.That(window.ToLine(0, 0), Does.Not.Contain("| records"));
        }

        [Test]
        public void RecordEntityStateRecords_TotalsPerDeliveryAndResetsWithTheWindow()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.RecordEntityStateRecords(DeliveryMethod.ReliableOrdered, 2);
            meter.RecordEntityStateRecords(DeliveryMethod.ReliableOrdered, 3);
            meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, 7);
            meter.RecordEntityStateRecords(DeliveryMethod.Sequenced, 4);
            meter.RecordEntityStateRecords((DeliveryMethod)9, 1);
            meter.RecordEntityStateUnreadable();

            MeshSendWindow window = meter.TakeWindow(1d);
            MeshSendWindow next = meter.TakeWindow(0d);

            Assert.That(window.EntityStateRecords(DeliveryMethod.ReliableOrdered), Is.EqualTo(5));
            Assert.That(window.EntityStateRecords(DeliveryMethod.Unreliable), Is.EqualTo(7));
            Assert.That(window.EntityStateRecords(DeliveryMethod.Sequenced), Is.EqualTo(4));
            Assert.That(window.EntityStateRecords((DeliveryMethod)7), Is.EqualTo(1));
            Assert.That(window.EntityStateRecordsTotal, Is.EqualTo(17));
            Assert.That(window.EntityStateUnreadable, Is.EqualTo(1));
            Assert.That(window.ToLine(0, 0), Does.Contain("| delivery ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 0 0 | records ro 5 u 7 other 5 unread 1 | channels other:0 |"));
            Assert.That(next.EntityStateRecordsTotal, Is.EqualTo(0));
            Assert.That(next.EntityStateUnreadable, Is.EqualTo(0));
            Assert.That(next.ToLine(0, 0), Is.EqualTo(EmptyLine));
        }

        [Test]
        public void ToLine_UnreadableOnly_PrintsTheRecordsGroup()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.RecordEntityStateUnreadable();

            Assert.That(meter.TakeWindow(1d).ToLine(0, 0), Does.Contain("| records ro 0 u 0 other 0 unread 1 | channels"));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_WithRecords_IsExactUnderAGermanCulture()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            for (int i = 0; i < 760; ++i)
                meter.Record(119, DeliveryMethod.Unreliable, 2, 502, 0, false);
            for (int i = 0; i < 240; ++i)
                meter.Record(119, DeliveryMethod.Unreliable, 2, 501, 0, false);
            for (int i = 0; i < 240; ++i)
                meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 41, 0, false);
            for (int i = 0; i < 10; ++i)
                meter.Record(2, DeliveryMethod.ReliableOrdered, 0, 40, 0, false);
            meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, 49700);
            meter.RecordEntityStateRecords(DeliveryMethod.ReliableOrdered, 300);
            meter.RecordEntityStateRecords(DeliveryMethod.ReliableUnordered, 5);
            meter.RecordEntityStateUnreadable();
            meter.RecordEntityStateUnreadable();

            string line = meter.TakeWindow(5d).ToLine(530000, 1300);

            Assert.That(line, Is.EqualTo(RecordsLine));
        }

        [Test]
        public void ToLine_WithoutRecords_PrintsNoRecordsGroup()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.Record(119, DeliveryMethod.Unreliable, 2, 100, 0, false);

            Assert.That(meter.TakeWindow(1d).ToLine(0, 0), Does.Not.Contain("records"));
        }

        [Test]
        public void RecordEntityStateRecords_AllocatesNothing()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, 1);
            meter.RecordEntityStateUnreadable();
            byte[] header = EntityStateHeader(1_790_000_000_000L, 12);

            Assert.That(() =>
            {
                MeshSendMeter.TryReadEntityStateCount(header, 0, header.Length, out int count);
                meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, count);
                meter.RecordEntityStateUnreadable();
            }, Is.Not.AllocatingGCMemory());
        }
    }
}
