using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshKinematicCharacterBodyTests
    {
        [Test]
        public void OnSenderReleased_ClearsSentBySender()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.SentBySender = true;

            ((IMeshSenderOwnedSource)body).OnSenderReleased();

            Assert.That(body.SentBySender, Is.False);
        }
    }
}
