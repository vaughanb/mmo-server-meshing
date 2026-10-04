using LiteNetLibManager;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshKitWrapQueueTests
    {
        [Test]
        public void OnDropped_AnOwnedSourceDroppedByTheFanout_IsReleasedExactlyOnce()
        {
            NewQueue();
            LiteNetLibIdentity identity = _identities.New(Id, 7);
            MeshFakeSenderOwnedSource source = new MeshFakeSenderOwnedSource(Id, identity);
            Assert.That(_fanout.Register(source, out _), Is.True);
            _identities.Destroy(identity);

            Build();
            Build();

            Assert.That(source.released, Is.EqualTo(1));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
        }

        [Test]
        public void OnDropped_AnOwnedSourceThatBreaksAppendOnly_IsReleasedOnce()
        {
            NewQueue();
            MeshFakeSenderOwnedSource source = new MeshFakeSenderOwnedSource(Id, _identities.New(Id, 7)) { resetWriter = true };
            Assert.That(_fanout.Register(source, out _), Is.True);

            Build();

            Assert.That(source.released, Is.EqualTo(1));
            Assert.That(_fanout.IsRegistered(Id), Is.False);
        }

        [Test]
        public void Unregister_AnOwnedSource_IsNeverReleased()
        {
            NewQueue();
            MeshFakeSenderOwnedSource source = new MeshFakeSenderOwnedSource(Id, _identities.New(Id, 7));
            Assert.That(_fanout.Register(source, out _), Is.True);

            _fanout.Unregister(source);
            Build();

            Assert.That(source.released, Is.EqualTo(0));
            Assert.That(source.writes, Is.EqualTo(0));
        }
    }
}
