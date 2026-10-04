using System.Threading;
using LiteNetLib;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshSendMeterTests
    {
        private const string EmptySizesLine = "Send window 0.00 s: calls 0 0.0/s bytes 0 0.0 kB/s | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 0 0 | sizes n 0 min 0 max 0 top 0:0 0:0 0:0 payload 0.0 framed 0.0 B over 0 unread 0 | channels other:0 | wire 0 B 0 pkt | offThread 0";

        private static byte[] SizesPacket(params (uint id, int length)[] records)
        {
            MeshStatePacketWriter writer = new MeshStatePacketWriter();
            writer.Begin(1_790_000_000_000L);
            foreach ((uint id, int length) in records)
                writer.TryAppend(id, new byte[length], 0, length);
            return writer.Finish().CopyData();
        }

        [Test]
        public void RecordEntityStateSizes_WithRecordSizesOff_CountsNothingAndPrintsNoGroup()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond);
            byte[] packet = SizesPacket((7, 54));

            meter.RecordEntityStateSizes(packet, 0, packet.Length);
            MeshSendWindow window = meter.TakeWindow(0d);

            Assert.That(meter.RecordSizes, Is.False);
            Assert.That(window.RecordSizes, Is.Null);
            Assert.That(window.ToLine(0, 0), Is.EqualTo(EmptyLine));
        }

        [Test]
        public void ToLine_RecordSizesOnWithNothingSent_PrintsAZeroSizesGroup()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };

            Assert.That(meter.TakeWindow(0d).ToLine(0, 0), Is.EqualTo(EmptySizesLine));
        }

        [Test, SetCulture("de-DE")]
        public void ToLine_RecordSizesOn_PrintsTheGroupAfterTheRecordsGroup()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };
            byte[] packet = SizesPacket((7, 54), (300, 56));
            meter.RecordEntityStateRecords(DeliveryMethod.Unreliable, 2);

            meter.RecordEntityStateSizes(packet, 0, packet.Length);
            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.RecordSizes.Records, Is.EqualTo(2));
            Assert.That(window.ToLine(0, 0), Does.Contain("| records ro 0 u 2 other 0 unread 0 | sizes n 2 min 54 max 56 top 54:1 56:1 0:0 payload 55.0 framed 60.5 B over 0 unread 0 | channels other:0 |"));
        }

        [Test]
        public void TakeWindow_RecordSizes_StartsTheNextWindowEmpty()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };
            byte[] packet = SizesPacket((7, 54));
            meter.RecordEntityStateSizes(packet, 0, packet.Length);

            MeshSendWindow first = meter.TakeWindow(1d);
            MeshSendWindow next = meter.TakeWindow(0d);

            Assert.That(first.RecordSizes.Records, Is.EqualTo(1));
            Assert.That(next.RecordSizes.Records, Is.EqualTo(0));
            Assert.That(first.RecordSizes, Is.Not.SameAs(next.RecordSizes));
            Assert.That(next.ToLine(0, 0), Is.EqualTo(EmptySizesLine));
        }

        [Test]
        public void RecordSizes_SetFalse_DropsTheCounts()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };

            meter.RecordSizes = false;

            Assert.That(meter.RecordSizes, Is.False);
            Assert.That(meter.TakeWindow(0d).RecordSizes, Is.Null);
        }

        [Test]
        public void RecordEntityStateSizes_FromAnotherThread_CountsOnlyOffThread()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };
            byte[] packet = SizesPacket((7, 54));
            Thread other = new Thread(() => meter.RecordEntityStateSizes(packet, 0, packet.Length));
            other.Start();
            other.Join();

            MeshSendWindow window = meter.TakeWindow(1d);

            Assert.That(window.OffThread, Is.EqualTo(1));
            Assert.That(window.RecordSizes.Records, Is.EqualTo(0));
        }

        [Test]
        public void RecordEntityStateSizes_UnreadablePacket_CountsUnread()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };
            byte[] packet = SizesPacket((7, 54));

            meter.RecordEntityStateSizes(packet, 0, packet.Length - 1);

            Assert.That(meter.TakeWindow(1d).RecordSizes.Unreadable, Is.EqualTo(1));
        }

        [Test]
        public void RecordEntityStateSizes_AllocatesNothing()
        {
            MeshSendMeter meter = new MeshSendMeter(TicksPerSecond) { RecordSizes = true };
            byte[] packet = SizesPacket((7, 54), (300, 56));
            meter.RecordEntityStateSizes(packet, 0, packet.Length);

            Assert.That(() => meter.RecordEntityStateSizes(packet, 0, packet.Length), Is.Not.AllocatingGCMemory());
        }
    }
}
