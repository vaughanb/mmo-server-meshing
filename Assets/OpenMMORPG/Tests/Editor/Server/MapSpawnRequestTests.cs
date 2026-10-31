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
    /// When the map spawn server refuses to start a map server. A spawn server that is not yet
    /// registered with the cluster would start a map server that cannot find the cluster, and an
    /// empty map name would start one that loads nothing; both must be answered with an error so
    /// the cluster can tell the waiting player, and no process may be started. A request that
    /// passes these checks starts a real process, so it is not run here.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class MapSpawnRequestTests : KitTestFixture
    {
        private MapSpawnNetworkManager _spawner;

        [SetUp]
        public void SetUp()
        {
            _spawner = Objects.NewGameObject(nameof(MapSpawnRequestTests)).AddComponent<MapSpawnNetworkManager>();
            // What Start would do, without starting anything: creates the (unconnected) cluster client.
            TestReflection.Invoke(_spawner, "Initialize");
        }

        private List<ResponseSpawnMapMessage> Spawn(string mapName)
        {
            List<ResponseSpawnMapMessage> replies = new List<ResponseSpawnMapMessage>();
            RequestProceedResultDelegate<ResponseSpawnMapMessage> result = (code, response, extra) =>
            {
                Assert.That(code, Is.EqualTo(AckResponseCode.Error));
                replies.Add(response);
            };
            RequestHandlerData data = new RequestHandlerData(0, 0, null, 1, new NetDataReader(new byte[0]));
            TestReflection.Invoke(_spawner, "HandleRequestSpawnMap", data, new RequestSpawnMapMessage() { channelId = "eu", mapName = mapName }, result);
            return replies;
        }

        private int PortCounter => TestReflection.GetField<int>(_spawner, "_portCounter");

        [Test]
        public void ASpawnServerNotYetRegisteredWithTheClusterRefuses()
        {
            int ports = PortCounter;

            List<ResponseSpawnMapMessage> replies = Spawn("Town");

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].message, Is.EqualTo(UITextKeys.UI_ERROR_APP_NOT_READY));
            Assert.That(PortCounter, Is.EqualTo(ports), "no port was taken for a map server");
        }

        [Test]
        public void ARequestWithNoMapNameIsRefused()
        {
            TestReflection.SetProperty(_spawner.ClusterClient, nameof(ClusterClient.IsAppRegistered), true);
            int ports = PortCounter;

            List<ResponseSpawnMapMessage> replies = Spawn(string.Empty);

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].message, Is.EqualTo(UITextKeys.UI_ERROR_EMPTY_SCENE_NAME));
            Assert.That(PortCounter, Is.EqualTo(ports), "no port was taken for a map server");
        }
    }
}
#endif
