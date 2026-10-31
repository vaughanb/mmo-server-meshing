#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The cluster server's register of app servers: which map server runs which map on which
    /// channel, which is how players are sent to a map. Two servers must never both own one map
    /// on one channel (players on the same map would not see each other), a server that
    /// disconnects must stop being handed out (players would be sent to a dead address) and its
    /// map must be free to start again. It also counts players per channel to keep channels
    /// under their limit. Driven through the request handlers of a never-started ClusterServer;
    /// it sends nothing because no connection is open.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ClusterServerMapServerTests : KitTestFixture
    {
        private CentralNetworkManager _central;
        private ClusterServer _cluster;

        [SetUp]
        public void SetUp()
        {
            // The cluster server logs every registration and disconnect.
            TestLogger.Use(Objects);
            // Awake and Start never run in edit mode; the cluster server only reads its channels.
            _central = Objects.NewGameObject(nameof(ClusterServerMapServerTests)).AddComponent<CentralNetworkManager>();
            _central.channels = new List<ChannelData>()
            {
                new ChannelData() { id = "eu", title = "Europe", maxConnections = 10 },
                new ChannelData() { id = "us", title = "America", maxConnections = 10 },
            };
            _cluster = new ClusterServer(_central);
        }

        private static RequestHandlerData From(long connectionId)
        {
            return new RequestHandlerData(0, 0, null, connectionId, new NetDataReader(new byte[0]));
        }

        private static CentralServerPeerInfo Peer(CentralServerPeerType type, string channelId = "", string refId = "", int port = 0, int users = 0)
        {
            return new CentralServerPeerInfo()
            {
                peerType = type,
                networkAddress = "10.0.0.1",
                networkPort = port,
                channelId = channelId,
                refId = refId,
                currentUsers = users,
                maxUsers = 100,
            };
        }

        /// <summary>Registers a server as connection <paramref name="connectionId"/>, as it would over the wire.</summary>
        private UITextKeys Register(long connectionId, CentralServerPeerInfo peer, bool validHash = true)
        {
            NetDataWriter writer = new NetDataWriter();
            if (validHash)
            {
                // Serialize stamps the time and the hash, as a real app server does.
                RequestAppServerRegisterMessage outgoing = new RequestAppServerRegisterMessage() { peerInfo = peer };
                outgoing.Serialize(writer);
            }
            else
            {
                peer.Serialize(writer);
                writer.PutPackedLong(System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                writer.Put("forged");
            }
            RequestAppServerRegisterMessage request = new RequestAppServerRegisterMessage();
            request.Deserialize(new NetDataReader(writer.CopyData()));

            List<ResponseAppServerRegisterMessage> replies = new List<ResponseAppServerRegisterMessage>();
            RequestProceedResultDelegate<ResponseAppServerRegisterMessage> result = (code, response, extra) => replies.Add(response);
            TestReflection.Invoke(_cluster, "HandleRequestAppServerRegister", From(connectionId), request, result);
            Assert.That(replies, Has.Count.EqualTo(1));
            return replies[0].message;
        }

        private ResponseAppServerAddressMessage Address(CentralServerPeerType type, string channelId = "", string refId = "")
        {
            List<ResponseAppServerAddressMessage> replies = new List<ResponseAppServerAddressMessage>();
            RequestProceedResultDelegate<ResponseAppServerAddressMessage> result = (code, response, extra) => replies.Add(response);
            RequestAppServerAddressMessage request = new RequestAppServerAddressMessage() { peerType = type, channelId = channelId, refId = refId };
            TestReflection.Invoke(_cluster, "HandleRequestAppServerAddress", From(99), request, result);
            Assert.That(replies, Has.Count.EqualTo(1));
            return replies[0];
        }

        private void Disconnect(long connectionId)
        {
            _cluster.OnServerReceive(new TransportEventData() { type = ENetworkEvent.DisconnectEvent, connectionId = connectionId });
        }

        private void ReportUsers(long connectionId, int currentUsers)
        {
            NetDataWriter writer = new NetDataWriter();
            new UpdateUserCountMessage() { currentUsers = currentUsers, maxUsers = 100 }.Serialize(writer);
            _cluster.HandleUpdateUserCount(new MessageHandlerData(MMOMessageTypes.UpdateUserCount, null, connectionId, new NetDataReader(writer.CopyData())));
        }

        private bool ChannelHasRoom(string channelId)
        {
            List<AckResponseCode> replies = new List<AckResponseCode>();
            RequestProceedResultDelegate<EmptyMessage> result = (code, response, extra) => replies.Add(code);
            TestReflection.Invoke(_cluster, "HandleRequestCheckChannelsLimit", From(99), new RequestCheckChannelsLimit() { channelId = channelId }, result);
            Assert.That(replies, Has.Count.EqualTo(1));
            return replies[0] == AckResponseCode.Success;
        }

        [Test]
        public void AMapServerIsFoundByItsChannelAndMapOnly()
        {
            Assert.That(Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7001)), Is.EqualTo(UITextKeys.NONE));

            ResponseAppServerAddressMessage found = Address(CentralServerPeerType.MapServer, "eu", "Town");
            Assert.That(found.message, Is.EqualTo(UITextKeys.NONE));
            Assert.That(found.peerInfo.networkPort, Is.EqualTo(7001));
            Assert.That(found.peerInfo.connectionId, Is.EqualTo(1));

            Assert.That(Address(CentralServerPeerType.MapServer, "us", "Town").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND), "another channel's map is not this one");
            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Forest").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
            Assert.That(Address(CentralServerPeerType.InstanceMapServer, "eu", "Town").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND), "a map server is not an instance");
        }

        [Test]
        public void ASecondServerForAMapAlreadyRunningOnThatChannelIsRefused()
        {
            Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7001));

            Assert.That(Register(2, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7002)), Is.EqualTo(UITextKeys.UI_ERROR_MAP_EXISTED));
            Assert.That(Register(3, Peer(CentralServerPeerType.MapServer, "us", "Town", 7003)), Is.EqualTo(UITextKeys.NONE), "the same map on another channel is fine");

            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Town").peerInfo.networkPort, Is.EqualTo(7001), "the first server keeps the map");
            Assert.That(_cluster.MapServerPeers.Keys, Is.EquivalentTo(new long[] { 1, 3 }));

            // The refused server going away must not take the map from the first.
            Disconnect(2);
            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Town").peerInfo.networkPort, Is.EqualTo(7001));
        }

        [Test]
        public void AServerWithABadHashIsNotRegistered()
        {
            Assert.That(Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7001), validHash: false), Is.EqualTo(UITextKeys.UI_ERROR_INVALID_SERVER_HASH));
            Assert.That(Register(2, Peer(CentralServerPeerType.LoginServer), validHash: false), Is.EqualTo(UITextKeys.UI_ERROR_INVALID_SERVER_HASH));

            Assert.That(_cluster.MapServerPeers, Is.Empty);
            Assert.That(_cluster.LoginServerPeers, Is.Empty);
            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Town").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
        }

        [Test]
        public void AMapServerThatDisconnectsIsNoLongerHandedOutAndItsMapCanStartAgain()
        {
            Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7001));
            Register(2, Peer(CentralServerPeerType.MapServer, "eu", "Forest", 7002));

            Disconnect(1);

            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Town").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Forest").peerInfo.networkPort, Is.EqualTo(7002), "other maps stay");
            Assert.That(_cluster.MapServerPeers.ContainsKey(1), Is.False);

            Assert.That(Register(3, Peer(CentralServerPeerType.MapServer, "eu", "Town", 7003)), Is.EqualTo(UITextKeys.NONE));
            Assert.That(Address(CentralServerPeerType.MapServer, "eu", "Town").peerInfo.networkPort, Is.EqualTo(7003));
        }

        [Test]
        public void AnInstanceIsFoundByItsInstanceIdUntilItsServerDisconnects()
        {
            Assert.That(Register(1, Peer(CentralServerPeerType.InstanceMapServer, "eu", "instance-1", 7101)), Is.EqualTo(UITextKeys.NONE));
            Assert.That(Register(2, Peer(CentralServerPeerType.InstanceMapServer, "eu", "instance-1", 7102)), Is.EqualTo(UITextKeys.UI_ERROR_EVENT_EXISTED));

            Assert.That(Address(CentralServerPeerType.InstanceMapServer, "eu", "instance-1").peerInfo.networkPort, Is.EqualTo(7101));

            Disconnect(1);
            Assert.That(Address(CentralServerPeerType.InstanceMapServer, "eu", "instance-1").message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
        }

        [Test]
        public void AnAllocatedMapServerThatDisconnectsLeavesThePool()
        {
            Register(1, Peer(CentralServerPeerType.AllocateMapServer, "__ALLOC__", "Dungeon", 7201));
            Register(2, Peer(CentralServerPeerType.AllocateMapServer, "__ALLOC__", "Dungeon", 7202));
            Register(3, Peer(CentralServerPeerType.AllocateMapServer, "__ALLOC__", "Cave", 7203));

            Disconnect(1);

            Assert.That(_cluster.AllocateMapServerPeersByRefId["Dungeon"].Select(p => p.connectionId), Is.EqualTo(new long[] { 2 }));
            Assert.That(_cluster.AllocateMapServerPeersByRefId["Cave"].Select(p => p.connectionId), Is.EqualTo(new long[] { 3 }));
        }

        [Test]
        public void LoginAndMapSpawnServersAreFoundWhileConnected()
        {
            Assert.That(Address(CentralServerPeerType.LoginServer).message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
            Assert.That(Address(CentralServerPeerType.MapSpawnServer).message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));

            Register(1, Peer(CentralServerPeerType.LoginServer, port: 6000));
            Register(2, Peer(CentralServerPeerType.MapSpawnServer, port: 6100));

            Assert.That(Address(CentralServerPeerType.LoginServer).peerInfo.networkPort, Is.EqualTo(6000));
            Assert.That(Address(CentralServerPeerType.MapSpawnServer).peerInfo.networkPort, Is.EqualTo(6100));

            Disconnect(1);
            Disconnect(2);
            Assert.That(Address(CentralServerPeerType.LoginServer).message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
            Assert.That(Address(CentralServerPeerType.MapSpawnServer).message, Is.EqualTo(UITextKeys.UI_ERROR_SERVER_NOT_FOUND));
        }

        [Test]
        public void PlayersAreCountedPerChannelFromTheirMapServersReports()
        {
            Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town"));
            Register(2, Peer(CentralServerPeerType.MapServer, "eu", "Forest"));
            Register(3, Peer(CentralServerPeerType.MapServer, "us", "Town"));

            ReportUsers(1, 4);
            ReportUsers(2, 3);
            ReportUsers(3, 5);
            ReportUsers(42, 1000); // not a registered map server

            Assert.That(_cluster.CountUsers("eu"), Is.EqualTo(7));
            Assert.That(_cluster.CountUsers("us"), Is.EqualTo(5));
            Assert.That(_cluster.CountUsers(), Is.EqualTo(12));

            Disconnect(1);
            Assert.That(_cluster.CountUsers("eu"), Is.EqualTo(3), "a gone server's players are not counted");

            Dictionary<string, ChannelEntry> channels = _cluster.GetChannels().ToDictionary(c => c.id);
            Assert.That(channels["eu"].connections, Is.EqualTo(3));
            Assert.That(channels["us"].connections, Is.EqualTo(5));
        }

        [Test]
        public void AFullChannelTurnsPlayersAwayAndOneWithRoomLetsThemIn()
        {
            Register(1, Peer(CentralServerPeerType.MapServer, "eu", "Town"));
            Register(2, Peer(CentralServerPeerType.MapServer, "us", "Town"));
            ReportUsers(1, _central.Channels["eu"].maxConnections);
            ReportUsers(2, _central.Channels["us"].maxConnections - 1);

            Assert.That(ChannelHasRoom("eu"), Is.False);
            Assert.That(ChannelHasRoom("us"), Is.True);
        }

        [Test]
        public void AChannelWithNoLimitSetShowsTheDefaultLimit()
        {
            _central.channels.Add(new ChannelData() { id = "asia", title = "Asia", maxConnections = 0 });

            ChannelEntry asia = _cluster.GetChannels().Single(c => c.id == "asia");

            Assert.That(asia.maxConnections, Is.EqualTo(_central.defaultChannelMaxConnections));
        }

        [Test]
        public void AChannelWithNoLimitSetLetsPlayersIn()
        {
            _central.channels.Add(new ChannelData() { id = "asia", title = "Asia", maxConnections = 0 });
            Register(1, Peer(CentralServerPeerType.MapServer, "asia", "Town"));
            ReportUsers(1, 1);

            Assert.That(ChannelHasRoom("asia"), Is.True);
        }
    }
}
#endif
