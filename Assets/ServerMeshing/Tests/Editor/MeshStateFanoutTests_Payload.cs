using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshStateFanoutTests
    {
        [Test]
        public void Build_PayloadCounters_CountFullAndCompactWrites()
        {
            MeshStateFanout fanout = NewFanout();
            _policy.all = new MeshSendRate(20, 0, MeshStateVariant.Compact);
            MeshFakeCompactStateSource compact = new MeshFakeCompactStateSource(42, _identities.New(42, 7, 8));
            compact.payload = new byte[56];
            compact.compactPayload = new byte[28];
            Assert.That(fanout.Register(compact, out string error), Is.True, error);
            MeshFakeStateSource full = Source(fanout, 43, 7);
            full.payload = new byte[54];

            Builds(fanout, 6);

            MeshStateFanoutCounters c = fanout.Counters;
            Assert.That(c.FullPayloads, Is.EqualTo(c.Writes));
            Assert.That(c.FullPayloads, Is.EqualTo(12));
            Assert.That(c.FullPayloadBytes, Is.EqualTo(6 * 56 + 6 * 54));
            Assert.That(c.FullPayloadMin, Is.EqualTo(54));
            Assert.That(c.FullPayloadMax, Is.EqualTo(56));
            Assert.That(c.CompactPayloads, Is.EqualTo(c.CompactWrites));
            Assert.That(c.CompactPayloads, Is.GreaterThan(0));
            Assert.That(c.CompactPayloadBytes, Is.EqualTo(28 * c.CompactPayloads));
            Assert.That(c.CompactPayloadMin, Is.EqualTo(28));
            Assert.That(c.CompactPayloadMax, Is.EqualTo(28));
        }

        [Test]
        public void Build_EmptyAndFalseWrites_StoreNoPayload()
        {
            MeshStateFanout fanout = NewFanout();
            MeshFakeStateSource empty = Source(fanout, 42, 7);
            empty.payload = new byte[0];
            MeshFakeStateSource refused = Source(fanout, 43, 7);
            refused.returns = false;

            Builds(fanout, 3);

            Assert.That(fanout.Counters.FullPayloads, Is.EqualTo(0));
            Assert.That(fanout.Counters.FullPayloadMin, Is.EqualTo(int.MaxValue));
            Assert.That(fanout.Counters.FullPayloadMax, Is.EqualTo(0));
        }

        [Test]
        public void TakeAndReset_CopiesThePayloadCountersAndResetsMinAndMax()
        {
            MeshStateFanoutCounters counters = new MeshStateFanoutCounters();
            counters.AddPayload(false, 56);
            counters.AddPayload(false, 54);
            counters.AddPayload(true, 28);
            MeshStateFanoutCounters taken = new MeshStateFanoutCounters();

            counters.TakeAndReset(taken);

            Assert.That((taken.FullPayloads, taken.FullPayloadBytes, taken.FullPayloadMin, taken.FullPayloadMax), Is.EqualTo((2L, 110L, 54, 56)));
            Assert.That((taken.CompactPayloads, taken.CompactPayloadBytes, taken.CompactPayloadMin, taken.CompactPayloadMax), Is.EqualTo((1L, 28L, 28, 28)));
            Assert.That((counters.FullPayloads, counters.FullPayloadBytes, counters.FullPayloadMin, counters.FullPayloadMax), Is.EqualTo((0L, 0L, int.MaxValue, 0)));
            Assert.That((counters.CompactPayloads, counters.CompactPayloadBytes, counters.CompactPayloadMin, counters.CompactPayloadMax), Is.EqualTo((0L, 0L, int.MaxValue, 0)));
        }
    }
}
