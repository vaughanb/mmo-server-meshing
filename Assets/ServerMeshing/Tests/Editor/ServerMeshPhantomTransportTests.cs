using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomTransportTests
    {
        private MeshFakeQueuedTransport _inner;
        private MeshFakeLog _log;
        private ServerMeshPhantomTransport _transport;
        private GameObject _gameObject;

        [SetUp]
        public void SetUp()
        {
            _inner = new MeshFakeQueuedTransport();
            _log = new MeshFakeLog();
            _transport = new ServerMeshPhantomTransport(_inner, _log, 0);
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);
        }

        private long ConnectAndDeliver()
        {
            Assert.That(_transport.TryQueueConnect(out long id), Is.True);
            Assert.That(_transport.ServerReceive(out TransportEventData connect), Is.True);
            Assert.That(connect.type, Is.EqualTo(ENetworkEvent.ConnectEvent));
            Assert.That(connect.connectionId, Is.EqualTo(id));
            return id;
        }

        private static NetDataWriter Bytes(int length)
        {
            NetDataWriter writer = new NetDataWriter(true, length + 4);
            for (int i = 0; i < length; ++i)
                writer.Put((byte)(i & 0x7F));
            return writer;
        }

        private static NetDataReader HelloReader(int offset, int index, int truncateBy)
        {
            NetDataWriter hello = new NetDataWriter();
            ServerMeshPhantomSink.WriteHello(hello, index);
            byte[] body = hello.CopyData();
            byte[] buffer = new byte[offset + body.Length - truncateBy];
            System.Array.Copy(body, 0, buffer, offset, body.Length - truncateBy);
            return new NetDataReader(buffer, offset, buffer.Length);
        }

        [Test]
        public void Constructor_NullInnerOrLog_ThrowsNamingIt()
        {
            Assert.That(Assert.Throws<System.ArgumentNullException>(() => new ServerMeshPhantomTransport(null, _log, 0)).ParamName, Is.EqualTo("inner"));
            Assert.That(Assert.Throws<System.ArgumentNullException>(() => new ServerMeshPhantomTransport(_inner, null, 0)).ParamName, Is.EqualTo("log"));
        }

        [Test]
        public void ServerSend_BeforeTheConnectIsDelivered_FalseWithoutInnerCall()
        {
            Assert.That(_transport.TryQueueConnect(out long id), Is.True);

            Assert.That(_transport.ServerSend(id, 0, DeliveryMethod.ReliableOrdered, Bytes(4)), Is.False);
            Assert.That(_inner.serverSendCalls, Is.EqualTo(0));
            Assert.That(_transport.IsPhantom(id), Is.False);
        }

        [Test]
        public void ServerSend_UnboundPhantomRangeId_FalseWithoutInnerCall()
        {
            Assert.That(_transport.ServerSend(ServerMeshPhantomTransport.IdBase + 99, 0, DeliveryMethod.Unreliable, Bytes(4)), Is.False);
            Assert.That(_inner.serverSendCalls, Is.EqualTo(0));
        }

        [Test]
        public void ServerDisconnect_UnknownPhantomId_FalseWithoutInnerCall()
        {
            Assert.That(_transport.ServerDisconnect(ServerMeshPhantomTransport.IdBase + 5), Is.False);
            Assert.That(_transport.QueueDisconnect(ServerMeshPhantomTransport.IdBase + 5), Is.False);
            Assert.That(_inner.serverDisconnectCalls, Is.EqualTo(0));
        }

        [Test]
        public void TryQueueConnect_BeforeStartServer_False()
        {
            _inner.isServerStarted = false;

            Assert.That(_transport.TryQueueConnect(out long id), Is.False);
            Assert.That(id, Is.EqualTo(-1));
        }

        [Test]
        public void TryQueueConnect_AtInnerPeersPlusPhantomsEqualToMax_False()
        {
            _inner.serverPeersCount = 3;
            _inner.serverMaxConnections = 5;

            Assert.That(_transport.TryQueueConnect(out _), Is.True);
            Assert.That(_transport.TryQueueConnect(out _), Is.True);
            Assert.That(_transport.TryQueueConnect(out _), Is.False);
        }

        [Test]
        public void InnerConnectCarryingABoundPhantomId_GivesThePhantomsDisconnectFirst_AndOneError()
        {
            long id = ConnectAndDeliver();
            _inner.EnqueueConnect(id);

            Assert.That(_transport.ServerReceive(out TransportEventData first), Is.True);
            Assert.That(_transport.ServerReceive(out TransportEventData second), Is.True);

            Assert.That(first.type, Is.EqualTo(ENetworkEvent.DisconnectEvent));
            Assert.That(first.connectionId, Is.EqualTo(id));
            Assert.That(second.type, Is.EqualTo(ENetworkEvent.ConnectEvent));
            Assert.That(second.connectionId, Is.EqualTo(id));
            Assert.That(_log.errors.Count, Is.EqualTo(1));
            Assert.That(_log.errors[0], Does.StartWith("Phantoms error:"));
            Assert.That(_transport.IsPhantom(id), Is.False);
            Assert.That(_transport.ServerReceive(out _), Is.False);
        }

        [Test]
        public void TruncatedHello_PassesThroughUnconsumed()
        {
            NetDataReader reader = HelloReader(4, 7, 2);
            _inner.EnqueueData(3, reader);

            Assert.That(_transport.ServerReceive(out TransportEventData data), Is.True);

            Assert.That(data.type, Is.EqualTo(ENetworkEvent.DataEvent));
            Assert.That(data.reader, Is.SameAs(reader));
            Assert.That(reader.Position, Is.EqualTo(4));
            Assert.That(_transport.TryDequeueHello(out _, out _), Is.False);
        }

        private static IEnumerable<TestCaseData> OversizeCases()
        {
            yield return new TestCaseData(DeliveryMethod.Unreliable, 1024, true).SetName("ServerSend_PhantomUnreliableAbove1023_ThrowsTooBig_NotSwallowed");
            yield return new TestCaseData(DeliveryMethod.Sequenced, 1021, true).SetName("ServerSend_PhantomSequencedAbove1020_ThrowsTooBig_NotSwallowed");
            yield return new TestCaseData(DeliveryMethod.ReliableSequenced, 1021, true).SetName("ServerSend_PhantomReliableSequencedAbove1020_ThrowsTooBig_NotSwallowed");
            yield return new TestCaseData(DeliveryMethod.Unreliable, 1023, false).SetName("ServerSend_PhantomUnreliableAt1023_Swallowed");
            yield return new TestCaseData(DeliveryMethod.Sequenced, 1020, false).SetName("ServerSend_PhantomSequencedAt1020_Swallowed");
            yield return new TestCaseData(DeliveryMethod.ReliableSequenced, 1020, false).SetName("ServerSend_PhantomReliableSequencedAt1020_Swallowed");
            yield return new TestCaseData(DeliveryMethod.ReliableOrdered, 4000, false).SetName("ServerSend_PhantomReliableOrdered4000_Swallowed");
            yield return new TestCaseData(DeliveryMethod.ReliableUnordered, 4000, false).SetName("ServerSend_PhantomReliableUnordered4000_Swallowed");
        }

        [TestCaseSource(nameof(OversizeCases))]
        public void ServerSend_OversizeRule(DeliveryMethod delivery, int length, bool throws)
        {
            long id = ConnectAndDeliver();
            int slot = ServerMeshPhantomTransport.DeliverySlotOf(delivery);
            NetDataWriter writer = Bytes(length);

            if (throws)
            {
                Assert.Throws<TooBigPacketException>(() => _transport.ServerSend(id, 0, delivery, writer));
                Assert.That(_transport.SwallowedCalls(slot), Is.EqualTo(0));
                Assert.That(_transport.SwallowedBytes(slot), Is.EqualTo(0));
                Assert.That(_transport.PayloadBytesOf(id), Is.EqualTo(0));
            }
            else
            {
                Assert.That(_transport.ServerSend(id, 0, delivery, writer), Is.True);
                Assert.That(_transport.SwallowedCalls(slot), Is.EqualTo(1));
                Assert.That(_transport.SwallowedBytes(slot), Is.EqualTo(length));
            }
            Assert.That(_inner.serverSendCalls, Is.EqualTo(0));
        }

        [Test]
        public void ServerSend_RealPeer_IsForwarded()
        {
            Assert.That(_transport.ServerSend(3, 0, DeliveryMethod.Unreliable, Bytes(4)), Is.True);
            Assert.That(_inner.serverSendCalls, Is.EqualTo(1));
        }

        [Test]
        public void ServerReceive_InnerEventsComeBeforeSyntheticOnes()
        {
            Assert.That(_transport.TryQueueConnect(out long id), Is.True);
            _inner.EnqueueConnect(3);
            _inner.EnqueueConnect(4);

            Assert.That(_transport.ServerReceive(out TransportEventData a), Is.True);
            Assert.That(_transport.ServerReceive(out TransportEventData b), Is.True);
            Assert.That(_transport.ServerReceive(out TransportEventData c), Is.True);

            Assert.That(a.connectionId, Is.EqualTo(3));
            Assert.That(b.connectionId, Is.EqualTo(4));
            Assert.That(c.connectionId, Is.EqualTo(id));
            Assert.That(_transport.ServerReceive(out _), Is.False);
        }

        [Test]
        public void Hello_ThroughAnOffsetFourReader_IsConsumedAndQueued()
        {
            NetDataReader reader = HelloReader(4, 7, 0);
            _inner.EnqueueData(3, reader);
            _inner.EnqueueConnect(9);

            Assert.That(_transport.ServerReceive(out TransportEventData next), Is.True);

            Assert.That(next.type, Is.EqualTo(ENetworkEvent.ConnectEvent));
            Assert.That(next.connectionId, Is.EqualTo(9));
            Assert.That(reader.Position, Is.EqualTo(4));
            Assert.That(_transport.TryDequeueHello(out long connection, out int index), Is.True);
            Assert.That(connection, Is.EqualTo(3));
            Assert.That(index, Is.EqualTo(7));
            Assert.That(_transport.TryDequeueHello(out _, out _), Is.False);
        }

        [Test]
        public void ServerPeersCount_IsInnerPlusBoundPhantoms()
        {
            _inner.serverPeersCount = 2;
            ConnectAndDeliver();
            ConnectAndDeliver();
            Assert.That(_transport.TryQueueConnect(out _), Is.True);

            Assert.That(_transport.ServerPeersCount, Is.EqualTo(4));
            Assert.That(_transport.BoundCount, Is.EqualTo(2));
            Assert.That(_transport.PhantomCount, Is.EqualTo(3));
        }

        [Test]
        public void ServerDisconnect_Phantom_GivesOneDisconnectPeerCalled_AndLaterSendsAreRefused()
        {
            long id = ConnectAndDeliver();

            Assert.That(_transport.ServerDisconnect(id), Is.True);
            Assert.That(_transport.ServerDisconnect(id), Is.False);
            Assert.That(_transport.ServerSend(id, 0, DeliveryMethod.Unreliable, Bytes(4)), Is.False);
            Assert.That(_transport.ServerReceive(out TransportEventData disconnect), Is.True);

            Assert.That(disconnect.type, Is.EqualTo(ENetworkEvent.DisconnectEvent));
            Assert.That(disconnect.connectionId, Is.EqualTo(id));
            Assert.That(disconnect.disconnectInfo.Reason, Is.EqualTo(DisconnectReason.DisconnectPeerCalled));
            Assert.That(_transport.ServerReceive(out _), Is.False);
            Assert.That(_inner.serverSendCalls, Is.EqualTo(0));
            Assert.That(_inner.serverDisconnectCalls, Is.EqualTo(0));
        }

        [Test]
        public void QueueDisconnect_Twice_GivesOneRemoteConnectionClose()
        {
            long id = ConnectAndDeliver();

            Assert.That(_transport.QueueDisconnect(id), Is.True);
            Assert.That(_transport.QueueDisconnect(id), Is.False);
            Assert.That(_transport.ServerReceive(out TransportEventData disconnect), Is.True);

            Assert.That(disconnect.type, Is.EqualTo(ENetworkEvent.DisconnectEvent));
            Assert.That(disconnect.disconnectInfo.Reason, Is.EqualTo(DisconnectReason.RemoteConnectionClose));
            Assert.That(_transport.ServerReceive(out _), Is.False);
        }

        [Test]
        public void GetServerRtt_Phantom_IsZeroWithoutInnerCall()
        {
            long id = ConnectAndDeliver();

            Assert.That(_transport.GetServerRtt(id), Is.EqualTo(0));
            Assert.That(_inner.serverRttCalls, Is.EqualTo(0));
            Assert.That(_transport.GetServerRtt(3), Is.EqualTo(33));
            Assert.That(_inner.serverRttCalls, Is.EqualTo(1));
        }

        [Test]
        public void ServerSend_SwallowedForABoundPhantom_AllocatesNothing()
        {
            long id = ConnectAndDeliver();
            NetDataWriter writer = Bytes(100);
            _transport.ServerSend(id, 2, DeliveryMethod.Unreliable, writer);

            Assert.That(() =>
            {
                _transport.ServerSend(id, 2, DeliveryMethod.Unreliable, writer);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void QueuedUpload_ReaderYieldsTheFrameToEndOfData_UntilTheNextReceive()
        {
            long id = ConnectAndDeliver();
            NetDataWriter frame = new NetDataWriter();
            frame.Put((byte)1);
            frame.Put((byte)2);
            frame.Put((byte)3);
            Assert.That(_transport.QueueUpload(id, frame), Is.True);
            frame.Reset();
            frame.Put((byte)9);
            Assert.That(_transport.QueueUpload(id, frame), Is.True);

            Assert.That(_transport.ServerReceive(out TransportEventData first), Is.True);
            Assert.That(first.type, Is.EqualTo(ENetworkEvent.DataEvent));
            Assert.That(first.connectionId, Is.EqualTo(id));
            Assert.That(first.reader.GetByte(), Is.EqualTo(1));
            Assert.That(first.reader.GetByte(), Is.EqualTo(2));
            Assert.That(first.reader.GetByte(), Is.EqualTo(3));
            Assert.That(first.reader.EndOfData, Is.True);

            Assert.That(_transport.ServerReceive(out TransportEventData second), Is.True);
            Assert.That(second.reader.GetByte(), Is.EqualTo(9));
            Assert.That(second.reader.EndOfData, Is.True);
            Assert.That(_transport.ServerReceive(out _), Is.False);
        }

        [Test]
        public void QueueUpload_UnboundId_IsRefused()
        {
            Assert.That(_transport.QueueUpload(ServerMeshPhantomTransport.IdBase, Bytes(3)), Is.False);
            Assert.That(_transport.ServerReceive(out _), Is.False);
        }

        [Test]
        public void SwallowedPayloadBytes_AreCountedPerPhantom()
        {
            long a = ConnectAndDeliver();
            long b = ConnectAndDeliver();

            _transport.ServerSend(a, 2, DeliveryMethod.Unreliable, Bytes(10));
            _transport.ServerSend(a, 0, DeliveryMethod.ReliableOrdered, Bytes(20));
            _transport.ServerSend(b, 2, DeliveryMethod.Unreliable, Bytes(5));

            Assert.That(_transport.PayloadBytesOf(a), Is.EqualTo(30));
            Assert.That(_transport.PayloadBytesOf(b), Is.EqualTo(5));
            Assert.That(_transport.SwallowedCalls(ServerMeshPhantomTransport.DeliverySlotOf(DeliveryMethod.Unreliable)), Is.EqualTo(2));
            Assert.That(_transport.SwallowedBytes(ServerMeshPhantomTransport.DeliverySlotOf(DeliveryMethod.Unreliable)), Is.EqualTo(15));
            Assert.That(_transport.SwallowedCalls(ServerMeshPhantomTransport.DeliverySlotOf(DeliveryMethod.ReliableOrdered)), Is.EqualTo(1));
            Assert.That(ServerMeshPhantomTransport.DeliverySlotOf((DeliveryMethod)7), Is.EqualTo(ServerMeshPhantomTransport.DeliverySlots - 1));
        }

        [Test]
        public void StopServer_DropsQueuedEventsAndHellos_EmitsNone_AndStopsTheInner()
        {
            long bound = ConnectAndDeliver();
            Assert.That(_transport.TryQueueConnect(out _), Is.True);
            Assert.That(_transport.QueueUpload(bound, Bytes(3)), Is.True);
            _inner.EnqueueData(3, HelloReader(0, 1, 0));
            Assert.That(_transport.ServerReceive(out _), Is.True);

            _transport.StopServer();

            Assert.That(_inner.stopServerCalls, Is.EqualTo(1));
            Assert.That(_transport.ServerReceive(out _), Is.False);
            Assert.That(_transport.TryDequeueHello(out _, out _), Is.False);
            Assert.That(_transport.IsPhantom(bound), Is.False);
            Assert.That(_transport.BoundCount, Is.EqualTo(0));
            Assert.That(_transport.PhantomCount, Is.EqualTo(0));
        }

        [Test]
        public void PhantomNumbers_StartAtFirstKAndAreNeverReusedAfterStopServer()
        {
            MeshFakeQueuedTransport inner = new MeshFakeQueuedTransport();
            ServerMeshPhantomTransport resumed = new ServerMeshPhantomTransport(inner, _log, 3);

            Assert.That(resumed.TryQueueConnect(out long first), Is.True);
            Assert.That(first, Is.EqualTo(1_000_003));
            resumed.StopServer();
            inner.isServerStarted = true;
            Assert.That(resumed.TryQueueConnect(out long afterStop), Is.True);
            Assert.That(afterStop, Is.EqualTo(1_000_004));
            Assert.That(resumed.NextK, Is.EqualTo(5));
        }

        [Test]
        public void Innermost_SeesThroughMeteredOverPhantomOverLiteNetLib()
        {
            LiteNetLibTransport lnl = new LiteNetLibTransport("k", 16, 16);
            ServerMeshPhantomTransport phantom = new ServerMeshPhantomTransport(lnl, _log, 0);
            MeshMeteredTransport metered = new MeshMeteredTransport(phantom, new MeshSendMeter(System.Diagnostics.Stopwatch.Frequency), false);

            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(metered), Is.SameAs(lnl));
            Assert.That(MeshTransportUnwrap.Innermost<ServerMeshPhantomTransport>(metered), Is.SameAs(phantom));
            Assert.That(phantom.Inner, Is.SameAs(lnl));
        }

        [Test]
        public void Splice_NullPhantomOrPhantomAsRoot_Throws()
        {
            _gameObject = new GameObject("ServerMeshPhantomTransportTests") { hideFlags = HideFlags.HideAndDontSave };
            ServerMeshPhantomTransportFactory phantom = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();

            Assert.Throws<System.ArgumentNullException>(() => ServerMeshPhantomInstaller.Splice(null, null));
            Assert.Throws<System.ArgumentException>(() => ServerMeshPhantomInstaller.Splice(phantom, phantom));
        }

        [Test]
        public void Splice_WithAMeteredRoot_GoesUnderIt_AndTheChainBuildsDownToLiteNetLib()
        {
            _gameObject = new GameObject("ServerMeshPhantomTransportTests") { hideFlags = HideFlags.HideAndDontSave };
            LiteNetLibTransportFactory kit = _gameObject.AddComponent<LiteNetLibTransportFactory>();
            MeshMeteredTransportFactory metered = _gameObject.AddComponent<MeshMeteredTransportFactory>();
            metered.Inner = kit;
            metered.Meter = new MeshSendMeter(System.Diagnostics.Stopwatch.Frequency);
            ServerMeshPhantomTransportFactory phantom = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();
            phantom.Log = _log;

            BaseTransportFactory root = ServerMeshPhantomInstaller.Splice(metered, phantom);
            ITransport built = root.Build();

            Assert.That(root, Is.SameAs(metered));
            Assert.That(metered.Inner, Is.SameAs(phantom));
            Assert.That(phantom.Inner, Is.SameAs(kit));
            Assert.That(built, Is.InstanceOf<MeshMeteredTransport>());
            Assert.That(MeshTransportUnwrap.Innermost<ServerMeshPhantomTransport>(built), Is.Not.Null);
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(built), Is.Not.Null);
        }

        [Test]
        public void Splice_WithAMeteredRootWithoutInner_LeavesThePhantomsInnerForTheKitDefault()
        {
            _gameObject = new GameObject("ServerMeshPhantomTransportTests") { hideFlags = HideFlags.HideAndDontSave };
            MeshMeteredTransportFactory metered = _gameObject.AddComponent<MeshMeteredTransportFactory>();
            ServerMeshPhantomTransportFactory phantom = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();
            phantom.Log = _log;

            ServerMeshPhantomInstaller.Splice(metered, phantom);
            ITransport built = phantom.Build();
            phantom.Build();

            Assert.That(metered.Inner, Is.SameAs(phantom));
            Assert.That(phantom.Inner, Is.InstanceOf<LiteNetLibTransportFactory>());
            Assert.That(_gameObject.GetComponents<LiteNetLibTransportFactory>().Length, Is.EqualTo(1));
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(built), Is.Not.Null);
        }

        [Test]
        public void Splice_WithoutAMeteredRoot_GoesOnTop()
        {
            _gameObject = new GameObject("ServerMeshPhantomTransportTests") { hideFlags = HideFlags.HideAndDontSave };
            LiteNetLibTransportFactory kit = _gameObject.AddComponent<LiteNetLibTransportFactory>();
            ServerMeshPhantomTransportFactory phantom = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();
            ServerMeshPhantomTransportFactory bare = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();

            Assert.That(ServerMeshPhantomInstaller.Splice(kit, phantom), Is.SameAs(phantom));
            Assert.That(phantom.Inner, Is.SameAs(kit));
            Assert.That(ServerMeshPhantomInstaller.Splice(null, bare), Is.SameAs(bare));
            Assert.That(bare.Inner == null, Is.True);
        }

        [Test]
        public void Build_InnerIsItself_Throws()
        {
            _gameObject = new GameObject("ServerMeshPhantomTransportTests") { hideFlags = HideFlags.HideAndDontSave };
            ServerMeshPhantomTransportFactory phantom = _gameObject.AddComponent<ServerMeshPhantomTransportFactory>();
            phantom.Inner = phantom;

            Assert.Throws<System.InvalidOperationException>(() => phantom.Build());
        }
    }
}
