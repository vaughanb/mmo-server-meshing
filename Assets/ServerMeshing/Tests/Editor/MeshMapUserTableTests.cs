using System.Collections.Generic;
using NUnit.Framework;
using UpdateType = MultiplayerARPG.MMO.UpdateUserCharacterMessage.UpdateType;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The central's record of which map server announced each user (DESIGN.md 0.91, MeshMapUserTable).</summary>
    public class MeshMapUserTableTests
    {
        private static SocialCharacterData User(string id) => new SocialCharacterData { userId = id, id = id + "-char" };

        [Test]
        public void ADeadServersUsers_AreHandedBack_AndForgotten()
        {
            MeshMapUserTable table = new MeshMapUserTable();
            table.Record(UpdateType.Add, User("a"), 5, "C.1_Map");
            table.Record(UpdateType.Add, User("b"), 5, "C.1_Map");
            table.Record(UpdateType.Add, User("c"), 7, "C_Map");
            Dictionary<long, string> live = new Dictionary<long, string> { { 7, "C_Map" } };

            List<MeshMapUserTable.Gone> gone = table.TakeGone(c => live.TryGetValue(c, out string k) ? k : null);

            Assert.That(gone.Count, Is.EqualTo(1));
            Assert.That((gone[0].connectionId, gone[0].peerKey), Is.EqualTo((5L, "C.1_Map")));
            Assert.That(gone[0].users.ConvertAll(u => u.userId), Is.EquivalentTo(new[] { "a", "b" }));
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.TakeGone(c => live.TryGetValue(c, out string k) ? k : null), Is.Empty, "handed back once");
            Assert.That(MeshMapUserTable.GoneLine(gone[0], 9), Is.EqualTo("Map users: C.1_Map (connection 5) left the cluster with 2 user(s) still online; told 9 map server(s) they are offline"));
        }

        [Test]
        public void AReusedConnection_UnderAnotherKey_CountsAsGone()
        {
            MeshMapUserTable table = new MeshMapUserTable();
            table.Record(UpdateType.Add, User("a"), 13, "E.1_Map");
            Assert.That(table.TakeGone(c => c == 13 ? "E.1_Map" : null), Is.Empty);
            Assert.That(table.TakeGone(c => c == 13 ? "NE.1_Map" : null).Count, Is.EqualTo(1), "LiteNetLib gave the id to another cell");
        }

        [Test]
        public void ARemove_ForgetsOnlyTheUsersOwnServer_AndAHandoffMovesIt()
        {
            MeshMapUserTable table = new MeshMapUserTable();
            table.Record(UpdateType.Add, User("a"), 5, "C.1_Map");
            table.Record(UpdateType.Add, User("a"), 7, "C_Map");      // the destination announced first
            table.Record(UpdateType.Remove, User("a"), 5, "C.1_Map"); // the origin's late Remove
            Assert.That(table.Count, Is.EqualTo(1));
            List<MeshMapUserTable.Gone> gone = table.TakeGone(c => c == 7 ? null : "x");
            Assert.That(gone.Count == 1 && gone[0].connectionId == 7, Is.True, "the user is the destination's");
            table.Record(UpdateType.Online, User("b"), 8, "N_Map");
            table.Record(UpdateType.Remove, User("b"), 8, "N_Map");
            table.Record(UpdateType.Add, new SocialCharacterData(), 8, "N_Map");
            Assert.That(table.Count, Is.EqualTo(0));
        }
    }
}
