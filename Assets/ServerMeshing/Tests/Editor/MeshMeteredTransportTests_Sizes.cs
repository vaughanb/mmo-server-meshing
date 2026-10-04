using LiteNetLib;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshMeteredTransportTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ServerSend_EntityState_FillsRecordSizesOnlyWhenEnabled(bool enabled)
        {
            MeshSendMeter meter = new MeshSendMeter(1_000_000) { RecordSizes = enabled };
            MeshMeteredTransport metered = new MeshMeteredTransport(NewFake(), meter, false);

            metered.ServerSend(1, 2, DeliveryMethod.Unreliable, EntityStatePacket(5));
            metered.ServerSend(1, 2, DeliveryMethod.Unreliable, Message(118, 12));

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.EntityStateRecordsTotal, Is.EqualTo(5));
            if (!enabled)
            {
                Assert.That(window.RecordSizes, Is.Null);
                Assert.That(window.ToLine(0, 0), Does.Not.Contain("sizes"));
                return;
            }
            Assert.That(window.RecordSizes.Records, Is.EqualTo(5));
            Assert.That(window.RecordSizes.PayloadBytes, Is.EqualTo(5));
            Assert.That(window.RecordSizes.FramedBytes, Is.EqualTo(5 * (1 + 4 + 1)));
            Assert.That(window.RecordSizes.Unreadable, Is.EqualTo(0));
        }

        [Test]
        public void ServerSend_TooBigEntityState_CountsNoSizes()
        {
            MeshFakeTransport fake = NewFake();
            fake.serverSendThrows = new TooBigPacketException("too big");
            MeshSendMeter meter = new MeshSendMeter(1_000_000) { RecordSizes = true };
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, false);

            Assert.That(() => metered.ServerSend(1, 2, DeliveryMethod.Unreliable, EntityStatePacket(4)), Throws.TypeOf<TooBigPacketException>());

            Assert.That(meter.TakeWindow(1d).RecordSizes.Records, Is.EqualTo(0));
        }
    }
}
