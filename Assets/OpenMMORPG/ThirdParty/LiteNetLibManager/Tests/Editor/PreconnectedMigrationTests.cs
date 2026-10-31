using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace LiteNetLibManager.Tests
{
    /// <summary>
    /// A client migrates onto a transport that is connected already (opened ahead of the hop): the
    /// client takes it over without starting it, the manager's connected event fires at once, and the old transport
    /// is destroyed. A transport that is not started is refused.
    /// </summary>
    public class PreconnectedMigrationTests
    {
        private sealed class ConnectedTransport : ITransport
        {
            public bool Started = true;
            public bool Destroyed;
            public readonly List<byte[]> Sent = new List<byte[]>();
            public int ServerPeersCount => 0;
            public int ServerMaxConnections => 0;
            public bool IsClientStarted => Started;
            public bool IsServerStarted => false;
            public bool HasImplementedPing => false;
            public bool IsReliableOnly => false;
            public bool StartClient(string address, int port) { Started = true; return true; }
            public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) { Sent.Add(writer.CopyData()); return true; }
            public bool ClientReceive(out TransportEventData eventData) { eventData = default; return false; }
            public void StopClient() { Started = false; }
            public bool StartServer(int port, int maxConnections) => false;
            public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) => false;
            public bool ServerReceive(out TransportEventData eventData) { eventData = default; return false; }
            public bool ServerDisconnect(long connectionId) => false;
            public void StopServer() { }
            public void Destroy() { Destroyed = true; Started = false; }
            public long GetClientRtt() => 0;
            public long GetServerRtt(long connectionId) => 0;
        }

        private sealed class MigratingManager : GameManagerHarness
        {
            public int ConnectedEvents;
            public override void OnClientConnected()
            {
                ConnectedEvents++;
                base.OnClientConnected();
            }
        }

        private GameObject _managerObject;
        private MigratingManager _manager;
        private ConnectedTransport _old;

        [SetUp]
        public void StartARunningClient()
        {
            _managerObject = new GameObject("preconnect manager");
            _manager = _managerObject.AddComponent<MigratingManager>();
            _manager.InitializeForTest();
            _manager.currentLogLevel = ELogLevel.Fatal; // the kit's logger is not set up in EditMode tests
            _manager.doNotEnterGameOnConnect = true;    // the harness registers no request handlers; the connected event still pings
            _old = new ConnectedTransport();
            var client = new LiteNetLibClient(_manager) { Transport = _old };
            typeof(LiteNetLibManager).GetProperty("Client").SetValue(_manager, client);
            typeof(LiteNetLibManager).GetProperty("IsClient").SetValue(_manager, true);
            typeof(LiteNetLibManager).GetField("_clientTransport", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_manager, _old);
            typeof(LiteNetLibClient).GetField("_isNetworkActive", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, true);
        }

        [TearDown]
        public void Destroy()
        {
            Object.DestroyImmediate(_managerObject);
        }

        [Test]
        public void MigrateOntoAConnectedTransport_TakesItOverAndEntersTheGameAtOnce()
        {
            var warm = new ConnectedTransport();

            Assert.IsTrue(_manager.MigrateClient("10.0.0.2", 8004, warm));

            Assert.IsTrue(_manager.IsMigratingClient);
            Assert.IsTrue(_manager.IsClient);
            Assert.AreSame(warm, _manager.Client.Transport);
            Assert.AreSame(warm, _manager.ClientTransport);
            Assert.IsTrue(_manager.Client.IsNetworkActive);
            Assert.AreEqual("10.0.0.2", _manager.networkAddress);
            Assert.AreEqual(8004, _manager.networkPort);
            Assert.AreEqual(1, _manager.ConnectedEvents, "the connect event was consumed by the holder, so the manager raises it itself");
            Assert.IsTrue(_old.Destroyed, "the connection to the cell we left is closed");
            Assert.IsFalse(warm.Destroyed);
            Assert.That(warm.Sent.Count, Is.GreaterThanOrEqualTo(1), "what the connected event sends (the ping here, enter-game in the game) goes out on the warm connection");
        }

        [Test]
        public void MigrateOntoATransportThatIsNotStarted_IsRefused()
        {
            var cold = new ConnectedTransport { Started = false };

            Assert.IsFalse(_manager.MigrateClient("10.0.0.2", 8004, cold));

            Assert.IsFalse(_manager.IsMigratingClient);
            Assert.AreSame(_old, _manager.Client.Transport);
            Assert.IsFalse(_old.Destroyed);
            Assert.AreEqual(0, _manager.ConnectedEvents);
        }

        [Test]
        public void AdoptConnectedTransport_WhileActive_IsRefused()
        {
            var warm = new ConnectedTransport();

            Assert.IsFalse(_manager.Client.AdoptConnectedTransport(warm));
            Assert.AreSame(_old, _manager.Client.Transport);
        }
    }
}
