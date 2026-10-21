using LiteNetLib;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The auto client's Slice 2d re-login lines (slices/slice2d_design.md decision 14), read by rigsplitcrash.</summary>
    public class ServerMeshAutoClientReloginTests
    {
        [Test]
        public void SessionLostLine_IsTheContractFormat()
        {
            Assert.That(ServerMeshAutoClient.SessionLostLine("Timeout", 1), Is.EqualTo("Session lost (Timeout); re-login 1"));
        }

        [Test]
        public void SessionLostLine_KeepsTheReasonParseable()
        {
            Assert.That(ServerMeshAutoClient.SessionLostLine("a (b)\nc {d}", 2), Is.EqualTo("Session lost (a [b] c [d]); re-login 2"));
            Assert.That(ServerMeshAutoClient.SessionLostLine(null, -1), Is.EqualTo("Session lost (unknown); re-login 0"));
        }

        [Test]
        public void SessionLostWhy_NamesTheReasonTheKickAndAFailedMigration()
        {
            Assert.That(ServerMeshAutoClient.SessionLostWhy(DisconnectReason.Timeout, UITextKeys.NONE, false), Is.EqualTo("Timeout"));
            Assert.That(ServerMeshAutoClient.SessionLostWhy(DisconnectReason.RemoteConnectionClose, UITextKeys.UI_ERROR_KICKED_FROM_SERVER, false),
                Is.EqualTo("RemoteConnectionClose/UI_ERROR_KICKED_FROM_SERVER"));
            Assert.That(ServerMeshAutoClient.SessionLostWhy(DisconnectReason.Timeout, UITextKeys.NONE, true), Is.EqualTo("migration failed: Timeout"));
        }

        [Test]
        public void LoginConnectedLine_SaysHowLongThePeerTook()
        {
            Assert.That(ServerMeshAutoClient.LoginConnectedLine(2, 17.4f), Is.EqualTo("Connected to login server (peer up after 2 frame(s), 17 ms)"));
            Assert.That(ServerMeshAutoClient.LoginConnectedLine(-1, float.NaN), Is.EqualTo("Connected to login server (peer up after 0 frame(s), 0 ms)"));
        }

        [Test]
        public void ReloginRoutedLine_AndSuffix_AreTheContractFormat()
        {
            Assert.That(ServerMeshAutoClient.ReloginRoutedLine("192.168.0.2", 8004), Is.EqualTo("Re-login routed to 192.168.0.2:8004"));
            Assert.That(ServerMeshAutoClient.ReloginRoutedLine(null, 0), Is.EqualTo("Re-login routed to ?:0"));
            Assert.That(ServerMeshAutoClient.ReloginSuffix(1), Is.EqualTo(" relogins=1"));
            Assert.That(ServerMeshAutoClient.ReloginSuffix(-3), Is.EqualTo(" relogins=0"));
        }
    }
}
