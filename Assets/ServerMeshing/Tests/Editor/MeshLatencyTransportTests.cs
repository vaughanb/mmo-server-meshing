using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLatencyTransportTests
    {
        private sealed class Fake : ITransport
        {
            public readonly List<byte[]> sent = new List<byte[]>();
            public readonly Queue<TransportEventData> incoming = new Queue<TransportEventData>();
            public bool started = true;
            public int stops;

            public int ServerPeersCount => 7;
            public int ServerMaxConnections => 0;
            public bool IsClientStarted => started;
            public bool IsServerStarted => false;
            public bool HasImplementedPing => true;
            public bool IsReliableOnly => false;
            public bool StartClient(string address, int port) => true;

            public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
            {
                sent.Add(writer.CopyData());
                return true;
            }

            public bool ClientReceive(out TransportEventData eventData)
            {
                if (incoming.Count == 0)
                {
                    eventData = default;
                    return false;
                }
                eventData = incoming.Dequeue();
                return true;
            }

            public void StopClient() => stops++;
            public bool StartServer(int port, int maxConnections) => false;
            public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => false;

            public bool ServerReceive(out TransportEventData eventData)
            {
                eventData = default;
                return false;
            }

            public bool ServerDisconnect(long connectionId) => false;
            public void StopServer() { }
            public void Destroy() { }
            public long GetClientRtt() => 3;
            public long GetServerRtt(long connectionId) => 5;
        }

        private long _now;
        private Fake _inner;
        private MeshLatencyTransport _transport;

        [SetUp]
        public void SetUp()
        {
            _now = 1000;
            _inner = new Fake();
            _transport = new MeshLatencyTransport(_inner, 50, () => _now);
        }

        private static NetDataWriter Message(byte value)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put(value);
            return writer;
        }

        private void Incoming(byte value)
        {
            _inner.incoming.Enqueue(new TransportEventData { type = ENetworkEvent.DataEvent, reader = new NetDataReader(new[] { value }) });
        }

        [Test]
        public void ClientSend_IsHeldForTheDelay_ThenSentInOrder()
        {
            Assert.That(_transport.ClientSend(0, DeliveryMethod.ReliableOrdered, Message(1)), Is.True);
            _now += 10;
            _transport.ClientSend(0, DeliveryMethod.ReliableOrdered, Message(2));
            _now += 39;
            _transport.ClientReceive(out _);
            Assert.That(_inner.sent, Is.Empty);

            _now += 1;
            _transport.ClientReceive(out _);
            Assert.That(_inner.sent.Count, Is.EqualTo(1));
            Assert.That(_inner.sent[0], Is.EqualTo(new byte[] { 1 }));

            _now += 10;
            _transport.ClientReceive(out _);
            Assert.That(_inner.sent.Count, Is.EqualTo(2));
            Assert.That(_inner.sent[1], Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void ClientSend_ClientNotStarted_IsFalseAndHoldsNothing()
        {
            _inner.started = false;

            Assert.That(_transport.ClientSend(0, DeliveryMethod.Unreliable, Message(1)), Is.False);
            _now += 100;
            _transport.ClientReceive(out _);
            Assert.That(_inner.sent, Is.Empty);
        }

        [Test]
        public void ClientReceive_IsHeldForTheDelay_ThenHandedOutInOrder()
        {
            Incoming(7);
            Incoming(8);
            Assert.That(_transport.ClientReceive(out _), Is.False);

            _now += 49;
            Assert.That(_transport.ClientReceive(out _), Is.False);

            _now += 1;
            Assert.That(_transport.ClientReceive(out TransportEventData first), Is.True);
            Assert.That(first.reader.GetByte(), Is.EqualTo(7));
            Assert.That(_transport.ClientReceive(out TransportEventData second), Is.True);
            Assert.That(second.reader.GetByte(), Is.EqualTo(8));
            Assert.That(_transport.ClientReceive(out _), Is.False);
        }

        [Test]
        public void ClientReceive_ConnectEvent_IsDelayedToo()
        {
            _inner.incoming.Enqueue(new TransportEventData { type = ENetworkEvent.ConnectEvent });
            Assert.That(_transport.ClientReceive(out _), Is.False);

            _now += 50;

            Assert.That(_transport.ClientReceive(out TransportEventData eventData), Is.True);
            Assert.That(eventData.type, Is.EqualTo(ENetworkEvent.ConnectEvent));
        }

        [Test]
        public void StopClient_SendsWhatIsHeld_DropsWhatWasNotHandedOut()
        {
            _transport.ClientSend(0, DeliveryMethod.ReliableOrdered, Message(1));
            Incoming(7);
            _transport.ClientReceive(out _);

            _transport.StopClient();

            Assert.That(_inner.sent.Count, Is.EqualTo(1));
            Assert.That(_inner.stops, Is.EqualTo(1));
            _now += 100;
            Assert.That(_transport.ClientReceive(out _), Is.False);
        }

        [Test]
        public void GetClientRtt_IsTheInnersPlusTheDelayBothWays()
        {
            Assert.That(_transport.GetClientRtt(), Is.EqualTo(103));
        }

        [Test]
        public void ServerMembers_AreForwarded()
        {
            Assert.That(_transport.ServerPeersCount, Is.EqualTo(7));
            Assert.That(_transport.GetServerRtt(1), Is.EqualTo(5));
        }

        [TestCase(null, false, 0)]
        [TestCase("", false, 0)]
        [TestCase("0", false, 0)]
        [TestCase("-5", false, -5)]
        [TestCase("abc", false, 0)]
        [TestCase(" 50 ", true, 50)]
        public void TryParseSetting(string value, bool expected, int delay)
        {
            Assert.That(MeshLatencyTransportFactory.TryParseSetting(value, out int delayMs), Is.EqualTo(expected));
            Assert.That(delayMs, Is.EqualTo(delay));
        }
    }
}
