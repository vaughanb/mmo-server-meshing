#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A map server that crashed left its players "online" on every other map server, so their
    /// next login was refused everywhere. The cluster server now tracks which map server hosts
    /// each user and forgets that server's users when it disconnects (v1.1.4).
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ClusterServerMapUserTests : KitTestFixture
    {
        private const long MapA = 1;
        private const long MapB = 2;

        private ClusterServer _cluster;

        [SetUp]
        public void SetUp()
        {
            // Never started; no map servers are connected, so nothing is relayed.
            _cluster = new ClusterServer(null);
        }

        private static SocialCharacterData User(string userId)
        {
            return new SocialCharacterData() { userId = userId, id = userId + "-character", characterName = userId };
        }

        private void Send(UpdateUserCharacterMessage.UpdateType type, string userId, long fromMap)
        {
            TestReflection.Invoke(_cluster, "TrackMapUser", type, User(userId), fromMap);
        }

        private void Disconnect(long map)
        {
            TestReflection.Invoke(_cluster, "RemoveMapUsers", map);
        }

        private bool IsOnline(string userId)
        {
            return TestReflection.GetField<Dictionary<string, long>>(_cluster, "_mapUserConnectionIds").ContainsKey(userId);
        }

        [Test]
        public void AMapServerThatDisconnectsTakesItsUsersOffline()
        {
            Send(UpdateUserCharacterMessage.UpdateType.Online, "alice", MapA);
            Send(UpdateUserCharacterMessage.UpdateType.Online, "bob", MapB);

            Disconnect(MapA);

            Assert.That(IsOnline("alice"), Is.False);
            Assert.That(IsOnline("bob"), Is.True, "another map server's user stays online");
        }

        [Test]
        public void AnAddRelayedByAnotherMapServerDoesNotMoveTheUser()
        {
            Send(UpdateUserCharacterMessage.UpdateType.Online, "alice", MapA);
            // A map server that registers re-announces the users it mirrors from others.
            Send(UpdateUserCharacterMessage.UpdateType.Add, "alice", MapB);

            Disconnect(MapB);

            Assert.That(IsOnline("alice"), Is.True);
        }

        [Test]
        public void ARemoveFromTheOldMapAfterAMoveKeepsTheUserOnline()
        {
            Send(UpdateUserCharacterMessage.UpdateType.Online, "alice", MapA);
            // Alice warps to map B, which announces her before map A lets her go.
            Send(UpdateUserCharacterMessage.UpdateType.Online, "alice", MapB);
            Send(UpdateUserCharacterMessage.UpdateType.Remove, "alice", MapA);

            Assert.That(IsOnline("alice"), Is.True);
        }
    }
}
#endif
