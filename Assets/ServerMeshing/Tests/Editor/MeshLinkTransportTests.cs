using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLinkTransportTests
    {
        [TestCase("40,10,1", 40, 10, 1f, 0)]
        [TestCase(" 75 , 30 , 2.5 , 60 ", 75, 30, 2.5f, 60)]
        [TestCase("0,0,100", 0, 0, 100f, 0)]
        public void TryParse_ReadsTheNetwork(string value, int delay, int jitter, float loss, int outage)
        {
            Assert.That(MeshLinkConditions.TryParse(value, out MeshLinkConditions conditions, out string warning), Is.True);
            Assert.That(warning, Is.Null);
            Assert.That(conditions.delayMs, Is.EqualTo(delay));
            Assert.That(conditions.jitterMs, Is.EqualTo(jitter));
            Assert.That(conditions.lossPercent, Is.EqualTo(loss));
            Assert.That(conditions.outageMs, Is.EqualTo(outage));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void TryParse_UnsetAsksForNothing(string value)
        {
            Assert.That(MeshLinkConditions.TryParse(value, out _, out string warning), Is.False);
            Assert.That(warning, Is.Null);
        }

        [TestCase("40")]
        [TestCase("40,10")]
        [TestCase("40,10,1,3,5")]
        [TestCase("-1,10,1")]
        [TestCase("40,-1,1")]
        [TestCase("40,10,101")]
        [TestCase("40,10,1,-1")]
        [TestCase("a,b,c")]
        public void TryParse_MalformedWarns(string value)
        {
            Assert.That(MeshLinkConditions.TryParse(value, out _, out string warning), Is.False);
            Assert.That(warning, Does.Contain(MeshLinkTransportFactory.EnvironmentKey));
        }

        [Test]
        public void Admit_DelaysEveryDatagramWithinTheJitter()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(40, 10, 0f), 7);
            for (int i = 0; i < 1000; ++i)
            {
                long now = i * 100;
                Assert.That(direction.Admit(now, out long due), Is.True);
                Assert.That(due - now, Is.InRange(30, 50));
            }
        }

        [Test]
        public void Admit_KeepsDatagramsInOrder()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(40, 30, 0f), 7);
            long last = 0;
            long delays = 0;
            for (int i = 0; i < 1000; ++i)
            {
                long now = i;
                direction.Admit(now, out long due);
                Assert.That(due, Is.GreaterThanOrEqualTo(last));
                delays += due - now;
                last = due;
            }
            Assert.That(delays / 1000, Is.GreaterThan(50), "a datagram behind a late one waits for it, so the mean is past the 40 ms asked");
        }

        [Test]
        public void Admit_NeverComesOutBeforeItWentIn()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(5, 50, 0f), 7);
            for (int i = 0; i < 1000; ++i)
            {
                long now = i * 100;
                direction.Admit(now, out long due);
                Assert.That(due, Is.GreaterThanOrEqualTo(now));
            }
        }

        [TestCase(1f, 40)]
        [TestCase(3f, 60)]
        [TestCase(5f, 80)]
        [TestCase(20f, 30)]
        public void Admit_IsDownTheAskedShareOfTheTimeInOutagesOfTheAskedLength(float lossPercent, int outageMs)
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(0, 0, lossPercent, outageMs), 11);
            const int Milliseconds = 20000000;
            int lost = 0;
            int outages = 0;
            bool down = false;
            for (int now = 0; now < Milliseconds; ++now)
            {
                bool passed = direction.Admit(now, out _);
                if (!passed)
                {
                    ++lost;
                    if (!down)
                        ++outages;
                }
                down = !passed;
            }
            Assert.That(100f * lost / Milliseconds, Is.EqualTo(lossPercent).Within(lossPercent * 0.1f));
            Assert.That((float)lost / outages, Is.EqualTo(outageMs).Within(outageMs * 0.1f));
        }

        [Test]
        public void Admit_AnOutageIsTimeNotDatagrams()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(0, 0, 5f, 80), 11);
            int lost = 0;
            int lostAfterLost = 0;
            bool lastLost = false;
            for (int i = 0; i < 100000; ++i)
            {
                bool passed = direction.Admit(i * 500L, out _); // a quiet connection: one datagram each half second
                if (!passed)
                {
                    ++lost;
                    lostAfterLost += lastLost ? 1 : 0;
                }
                lastLost = !passed;
            }
            Assert.That((float)lostAfterLost / lost, Is.LessThan(0.1f), "an 80 ms outage does not take datagrams half a second apart together");
        }

        [Test]
        public void Admit_WithoutOutagesLosesEachDatagramOnItsOwn()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(0, 0, 3f), 11);
            const int Datagrams = 1000000;
            int lost = 0;
            for (int i = 0; i < Datagrams; ++i)
                lost += direction.Admit(0, out _) ? 0 : 1;
            Assert.That(100f * lost / Datagrams, Is.EqualTo(3f).Within(0.2f));
        }

        [Test]
        public void Admit_LosesNothingAtNoLoss()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(0, 0, 0f, 50), 7);
            for (int i = 0; i < 100000; ++i)
                Assert.That(direction.Admit(i, out _), Is.True);
        }

        [Test]
        public void Admit_LosesEverythingAtAHundredPercent()
        {
            MeshLinkDirection direction = new MeshLinkDirection(new MeshLinkConditions(0, 0, 100f), 7);
            for (int i = 0; i < 1000; ++i)
                Assert.That(direction.Admit(i, out _), Is.False);
        }

        private static byte[] Datagram(byte value) => new[] { value };

        /// <summary>Sender sends to the relay and Far is the server; the relay learns the sender from its first datagram.</summary>
        private sealed class Loop : System.IDisposable
        {
            public readonly UdpClient far = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            public readonly UdpClient near = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            public readonly MeshLinkRelay relay;

            public Loop(MeshLinkConditions conditions)
            {
                far.Client.ReceiveTimeout = 1000;
                near.Client.ReceiveTimeout = 1000;
                relay = new MeshLinkRelay((IPEndPoint)far.Client.LocalEndPoint, conditions, 7);
                near.Connect(IPAddress.Loopback, relay.LocalPort);
                relay.Start();
            }

            public void Dispose()
            {
                relay.Close();
                far.Dispose();
                near.Dispose();
            }
        }

        [Test]
        public void Relay_PassesDatagramsBothWaysAfterTheDelay()
        {
            using (Loop loop = new Loop(new MeshLinkConditions(30, 0, 0f)))
            {
                Stopwatch clock = Stopwatch.StartNew();
                loop.near.Send(Datagram(1), 1);
                IPEndPoint relayEnd = null;
                Assert.That(loop.far.Receive(ref relayEnd), Is.EqualTo(Datagram(1)));
                Assert.That(clock.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(30));

                clock.Restart();
                loop.far.Send(Datagram(2), 1, relayEnd);
                IPEndPoint back = null;
                Assert.That(loop.near.Receive(ref back), Is.EqualTo(Datagram(2)));
                Assert.That(clock.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(30));
            }
        }

        [Test]
        public void Relay_LosesWhatTheNetworkLoses()
        {
            using (Loop loop = new Loop(new MeshLinkConditions(0, 0, 100f)))
            {
                loop.far.Client.ReceiveTimeout = 100;
                loop.near.Send(Datagram(1), 1);
                IPEndPoint any = null;
                Assert.Throws<SocketException>(() => loop.far.Receive(ref any));
                Assert.That(loop.relay.Counts, Does.Contain("to server 0 passed 1 lost"));
            }
        }

        [Test]
        public void Relay_PassesWhatItHoldsAfterClose()
        {
            using (Loop loop = new Loop(new MeshLinkConditions(30, 0, 0f)))
            {
                loop.near.Send(Datagram(9), 1);
                System.Threading.Thread.Sleep(5); // the relay has taken it and holds it
                loop.relay.Close();
                IPEndPoint any = null;
                Assert.That(loop.far.Receive(ref any), Is.EqualTo(Datagram(9)));
            }
        }
    }
}
