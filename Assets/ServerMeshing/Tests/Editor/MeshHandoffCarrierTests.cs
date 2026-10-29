using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshHandoffCarrierTests
    {
        private sealed class FakeCarrier : IMeshHandoffCarrier
        {
            public string key;
            public int? write;
            public bool throwOnWrite, throwOnRead;
            public int read = -1;
            public string CarrierKey => key;
            public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
            {
                if (throwOnWrite) throw new InvalidOperationException("boom");
                if (write == null) return false;
                writer.Put(write.Value);
                return true;
            }
            public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
            {
                if (throwOnRead) throw new InvalidOperationException("boom");
                read = reader.GetInt();
            }
        }

        [Test]
        public void Collect_NothingToCarry_IsNull()
        {
            Assert.That(MeshHandoffCarriers.Collect(new List<IMeshHandoffCarrier> { new FakeCarrier { key = "a" } }, null, null), Is.Null);
            Assert.That(MeshHandoffCarriers.Collect(new List<IMeshHandoffCarrier>(), null, null), Is.Null);
        }

        [Test]
        public void EachCarrier_ReadsBackItsOwnBytes_ByKey()
        {
            byte[] carried = MeshHandoffCarriers.Collect(new List<IMeshHandoffCarrier> { new FakeCarrier { key = "a", write = 7 }, new FakeCarrier { key = "b", write = 9 } }, null, null);
            FakeCarrier a = new FakeCarrier { key = "a" }, b = new FakeCarrier { key = "b" };

            string keys = MeshHandoffCarriers.Apply(new List<IMeshHandoffCarrier> { b, a }, null, carried, null);

            Assert.That(a.read, Is.EqualTo(7));
            Assert.That(b.read, Is.EqualTo(9));
            Assert.That(keys, Is.EqualTo("a,b"));
        }

        [Test]
        public void AKeyWithNoCarrierHere_IsSkipped_AndTheNextStillReads()
        {
            byte[] carried = MeshHandoffCarriers.Collect(new List<IMeshHandoffCarrier> { new FakeCarrier { key = "gone", write = 1 }, new FakeCarrier { key = "b", write = 2 } }, null, null);
            FakeCarrier b = new FakeCarrier { key = "b" };

            Assert.That(MeshHandoffCarriers.Apply(new List<IMeshHandoffCarrier> { b }, null, carried, null), Is.EqualTo("b"));
            Assert.That(b.read, Is.EqualTo(2));
        }

        [Test]
        public void ACarrierThatThrows_IsReported_AndTheOthersAreCarried()
        {
            List<string> warnings = new List<string>();
            byte[] carried = MeshHandoffCarriers.Collect(new List<IMeshHandoffCarrier> { new FakeCarrier { key = "bad", throwOnWrite = true }, new FakeCarrier { key = "b", write = 3 } }, null, warnings.Add);
            FakeCarrier badReader = new FakeCarrier { key = "b", throwOnRead = true };

            MeshHandoffCarriers.Apply(new List<IMeshHandoffCarrier> { badReader }, null, carried, warnings.Add);

            Assert.That(warnings.Count, Is.EqualTo(2));
            Assert.That(warnings[0], Does.Contain("bad"));
        }

        [Test]
        public void HandoffState_CarriesTheBytes_AndAPayloadWithoutThemReadsNull()
        {
            HandoffStateMessage msg = new HandoffStateMessage { characterId = "c", userId = "u", data = new PlayerCharacterData(), carried = new byte[] { 1, 2, 3 } };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            HandoffStateMessage read = default;
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.That(read.carried, Is.EqualTo(new byte[] { 1, 2, 3 }));

            msg.carried = null;
            writer.Reset();
            msg.Serialize(writer);
            read = default;
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.That(read.carried, Is.Null);
        }
    }
}
