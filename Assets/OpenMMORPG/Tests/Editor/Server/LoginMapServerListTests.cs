#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The login server's list of map servers, which select-character uses to tell the client
    /// where its map is. The cluster server announces each map server as it registers, but
    /// announces nothing when one disconnects, so a map server that crashed or was stopped must
    /// still not be handed out (the player would be sent to a dead address). The login server
    /// runs beside the cluster server, as in the kit's central server; neither is started, the
    /// cluster server is only marked running.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class LoginMapServerListTests : KitTestFixture
    {
        private ClusterServer _cluster;
        private LoginNetworkManager _login;

        [SetUp]
        public void SetUp()
        {
            // The cluster server logs every registration and disconnect.
            TestLogger.Use(Objects);
            // Awake and Start never run in edit mode, so no transport or socket is created.
            CentralNetworkManager central = Objects.NewGameObject(nameof(LoginMapServerListTests)).AddComponent<CentralNetworkManager>();
            central.channels = new List<ChannelData>() { new ChannelData() { id = "eu", title = "Europe", maxConnections = 10 } };
            _cluster = new ClusterServer(central);
            TestReflection.SetField(_cluster, "_isNetworkActive", true);
            _login = Objects.NewGameObject(nameof(LoginNetworkManager)).AddComponent<LoginNetworkManager>();
            TestReflection.SetField(_login, "centralNetworkManager", central);
            TestReflection.SetField(_login, "clusterServer", _cluster);
        }

        /// <summary>Registers a map server with the cluster and announces it to the login server, as over the wire.</summary>
        private void StartMapServer(long connectionId, string mapName, int port)
        {
            CentralServerPeerInfo peer = new CentralServerPeerInfo()
            {
                peerType = CentralServerPeerType.MapServer,
                networkAddress = "10.0.0.1",
                networkPort = port,
                channelId = "eu",
                refId = mapName,
                maxUsers = 100,
            };
            NetDataWriter writer = new NetDataWriter();
            // Serialize stamps the time and the hash, as a real map server does.
            new RequestAppServerRegisterMessage() { peerInfo = peer }.Serialize(writer);
            RequestAppServerRegisterMessage request = new RequestAppServerRegisterMessage();
            request.Deserialize(new NetDataReader(writer.CopyData()));
            List<UITextKeys> replies = new List<UITextKeys>();
            RequestProceedResultDelegate<ResponseAppServerRegisterMessage> result = (code, response, extra) => replies.Add(response.message);
            TestReflection.Invoke(_cluster, "HandleRequestAppServerRegister", new RequestHandlerData(0, 0, null, connectionId, new NetDataReader(new byte[0])), request, result);
            Assert.That(replies, Is.EqualTo(new[] { UITextKeys.NONE }));

            peer.connectionId = connectionId;
            TestReflection.Invoke(_login, "OnResponseAppServerAddress", AckResponseCode.Success, peer);
        }

        [Test]
        public void ARunningMapServerIsHandedOut()
        {
            StartMapServer(1, "Town", 7001);

            Assert.That(_login.TryGetMapServer("eu", "Town", out CentralServerPeerInfo town), Is.True);
            Assert.That(town.networkPort, Is.EqualTo(7001));
            Assert.That(_login.TryGetMapServer("eu", "Forest", out _), Is.False, "a map with no server is not ready");
        }

        [Test]
        public void AMapServerThatDisconnectedIsNotHandedOut()
        {
            StartMapServer(1, "Town", 7001);
            StartMapServer(2, "Forest", 7002);

            _cluster.OnServerReceive(new TransportEventData() { type = ENetworkEvent.DisconnectEvent, connectionId = 1 });

            Assert.That(_login.TryGetMapServer("eu", "Town", out _), Is.False);
            Assert.That(_login.TryGetMapServer("eu", "Forest", out CentralServerPeerInfo forest), Is.True, "other maps stay");
            Assert.That(forest.networkPort, Is.EqualTo(7002));
        }

        [Test]
        public void AMapServerThatStartsAgainIsHandedOutAtItsNewAddress()
        {
            StartMapServer(1, "Town", 7001);
            _cluster.OnServerReceive(new TransportEventData() { type = ENetworkEvent.DisconnectEvent, connectionId = 1 });

            StartMapServer(3, "Town", 7003);

            Assert.That(_login.TryGetMapServer("eu", "Town", out CentralServerPeerInfo town), Is.True);
            Assert.That(town.networkPort, Is.EqualTo(7003));
        }

        [Test]
        public void ALoginServerWithoutTheClusterInItsProcessKeepsTheAddressesItWasSent()
        {
            // A login server of its own (-startLoginServer without -startCentralServer) has a cluster server
            // object that never starts, so its list is always empty and must not turn every map away.
            StartMapServer(1, "Town", 7001);
            TestReflection.SetField(_cluster, "_isNetworkActive", false);
            _cluster.MapServerPeersByKey.Clear();

            Assert.That(_login.TryGetMapServer("eu", "Town", out CentralServerPeerInfo town), Is.True);
            Assert.That(town.networkPort, Is.EqualTo(7001));
        }
    }
}
#endif
