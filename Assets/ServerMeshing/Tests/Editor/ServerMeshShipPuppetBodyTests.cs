using System;
using System.Collections.Concurrent;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipPuppetBodyTests
    {
        private const uint Id = 77;

        private MeshFakeIdentities _identities;
        private LiteNetLibIdentity _identity;
        private Transform _transform;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _identity = _identities.New(Id);
            _transform = _identity.transform;
            _transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private ServerMeshShipPuppetBody NewBody(ServerMeshShipWireKind wire = ServerMeshShipWireKind.Header56, float scale = 1f)
        {
            return new ServerMeshShipPuppetBody(Id, _identity, _transform, wire, new ServerMeshShipScale(scale));
        }

        private static ServerMeshShipSample Sample(Vector3 position, Vector3 velocity)
        {
            return new ServerMeshShipSample
            {
                Position = position,
                Rotation = Quaternion.Euler(0f, 90f, 0f),
                Velocity = velocity,
                AngularVelocity = new Vector3(0f, 0.5f, 0f),
                Forward = true,
                TeleportByte = 3,
                Authority = ServerMeshShipAuthority.OwnerMirror,
                FlightState = 1,
                Epoch = 7,
            };
        }

        [Test]
        public void Constructor_NullIdentity_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipPuppetBody(Id, null, _transform, ServerMeshShipWireKind.Header56, new ServerMeshShipScale(1f)));
            Assert.That(ex.ParamName, Is.EqualTo("identity"));
        }

        [Test]
        public void Constructor_NullTransform_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipPuppetBody(Id, _identity, null, ServerMeshShipWireKind.Header56, new ServerMeshShipScale(1f)));
            Assert.That(ex.ParamName, Is.EqualTo("transform"));
        }

        [Test]
        public void Constructor_NullScale_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipPuppetBody(Id, _identity, _transform, ServerMeshShipWireKind.Header56, null));
            Assert.That(ex.ParamName, Is.EqualTo("scale"));
        }

        [Test]
        public void Begin_NullSlots_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => NewBody().Begin(null, out _));
            Assert.That(ex.ParamName, Is.EqualTo("slots"));
        }

        [Test]
        public void Begin_NoHandlerInSlot_FailsNamingTheObject()
        {
            MeshMovementHandlerSlots slots = new MeshMovementHandlerSlots(new ConcurrentDictionary<uint, IEntityMovementDataHandler>());

            bool begun = NewBody().Begin(slots, out string error);

            Assert.That(begun, Is.False);
            StringAssert.Contains(Id.ToString(), error);
        }

        [Test]
        public void WriteServerState_SentBySender_WritesNothing()
        {
            ServerMeshShipPuppetBody body = NewBody();
            body.SentBySender = true;
            NetDataWriter writer = new NetDataWriter();

            bool wrote = body.WriteServerState(1000, writer, out bool reliable);

            Assert.That(wrote, Is.False);
            Assert.That(reliable, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
        }

        [Test]
        public void Tick_BeforeAnySample_LeavesTheTransform()
        {
            ServerMeshShipPuppetBody body = NewBody();
            _transform.position = new Vector3(1f, 2f, 3f);

            body.Tick(0.02f);
            body.ApplyNow();

            Assert.That(_transform.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void AcceptThenApplyNow_PosesTheSample()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(10f, 60f, -5f), new Vector3(5f, 0f, 0f));

            body.Accept(in sample, 12.5);
            body.ApplyNow();

            Assert.That(_transform.position, Is.EqualTo(sample.Position));
            Assert.That(Quaternion.Angle(_transform.rotation, sample.Rotation), Is.LessThan(0.01f));
            Assert.That(body.LastAcceptTime, Is.EqualTo(12.5));
        }

        [Test]
        public void Tick_WithinSnapDistance_LerpsTowardTheExtrapolatedTarget()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(4f, 0f, 0f), new Vector3(10f, 0f, 0f));
            body.Accept(in sample, 0);
            const float dt = 0.02f;

            body.Tick(dt);

            Vector3 target = sample.Position + sample.Velocity * dt;
            Vector3 expected = Vector3.Lerp(Vector3.zero, target, 1f - Mathf.Exp(-10f * dt));
            Assert.That(Vector3.Distance(_transform.position, expected), Is.LessThan(1e-5f));
        }

        [Test]
        public void Tick_Repeated_ConvergesOnAStillTarget()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(4f, 0f, 0f), Vector3.zero);
            body.Accept(in sample, 0);

            for (int i = 0; i < 200; ++i)
                body.Tick(0.02f);

            Assert.That(Vector3.Distance(_transform.position, sample.Position), Is.LessThan(1e-3f));
        }

        [Test]
        public void Tick_JumpBeyondScaledSnapDistance_Snaps()
        {
            ServerMeshShipPuppetBody body = NewBody(scale: 0.5f);
            ServerMeshShipSample sample = Sample(new Vector3(26f, 0f, 0f), new Vector3(1f, 0f, 0f));
            body.Accept(in sample, 0);

            body.Tick(0.1f);

            Assert.That(Vector3.Distance(_transform.position, new Vector3(26.1f, 0f, 0f)), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(_transform.rotation, sample.Rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void WriteState_IsTheWireRecordOfThePosedTransform()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(10f, 60f, -5f), new Vector3(5f, 0f, 0f));
            body.Accept(in sample, 0);
            body.ApplyNow();
            _transform.position = new Vector3(11f, 60f, -5f);
            ServerMeshShipSample expected = sample;
            expected.Position = _transform.position;
            expected.Rotation = _transform.rotation;
            NetDataWriter want = new NetDataWriter();
            ServerMeshShipWire.WriteHeader56(want, in expected);
            NetDataWriter got = new NetDataWriter();

            bool wrote = body.WriteState(1000, got, out bool reliable);

            Assert.That(wrote, Is.True);
            Assert.That(reliable, Is.False);
            Assert.That(got.CopyData(), Is.EqualTo(want.CopyData()));
        }

        [Test]
        public void WriteServerState_NotSentBySender_WritesTheSameBytesAsWriteState()
        {
            ServerMeshShipPuppetBody body = NewBody(ServerMeshShipWireKind.Legacy54);
            ServerMeshShipSample sample = Sample(new Vector3(1f, 2f, 3f), Vector3.zero);
            body.Accept(in sample, 0);
            body.ApplyNow();
            NetDataWriter state = new NetDataWriter();
            NetDataWriter server = new NetDataWriter();

            body.WriteState(5, state, out _);
            body.WriteServerState(5, server, out _);

            Assert.That(server.Length, Is.EqualTo(ServerMeshShipWire.Legacy54Bytes));
            Assert.That(server.CopyData(), Is.EqualTo(state.CopyData()));
        }

        [Test]
        public void WriteCompactState_NotCompactWire_WritesNothing()
        {
            NetDataWriter writer = new NetDataWriter();

            bool wrote = NewBody(ServerMeshShipWireKind.Header56).WriteCompactState(5, writer);

            Assert.That(wrote, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteCompactState_CompactWire_Writes28Bytes()
        {
            ServerMeshShipPuppetBody body = NewBody(ServerMeshShipWireKind.Compact28);
            ServerMeshShipSample sample = Sample(new Vector3(1f, 2f, 3f), Vector3.zero);
            body.Accept(in sample, 0);
            body.ApplyNow();
            NetDataWriter writer = new NetDataWriter();

            bool wrote = body.WriteCompactState(5, writer);

            Assert.That(wrote, Is.True);
            Assert.That(writer.Length, Is.EqualTo(ServerMeshShipWire.Compact28Bytes));
        }

        [Test]
        public void BeginThenEnd_HandsTheSlotBackAndClearsSentBySender()
        {
            ConcurrentDictionary<uint, IEntityMovementDataHandler> handlers = new ConcurrentDictionary<uint, IEntityMovementDataHandler>();
            MeshFakeKitHandler kit = new MeshFakeKitHandler(Id);
            handlers[Id] = kit;
            MeshMovementHandlerSlots slots = new MeshMovementHandlerSlots(handlers);
            ServerMeshShipPuppetBody body = NewBody();

            Assert.That(body.Begin(slots, out string error), Is.True, error);
            Assert.That(handlers[Id], Is.SameAs(body));
            body.SentBySender = true;
            body.End(slots);

            Assert.That(body.SentBySender, Is.False);
            Assert.That(body.IsBegun, Is.False);
            Assert.That(handlers[Id], Is.SameAs(kit));
        }

        [Test]
        public void End_NeverBegun_StillClearsSentBySender()
        {
            ServerMeshShipPuppetBody body = NewBody();
            body.SentBySender = true;

            body.End(new MeshMovementHandlerSlots(new ConcurrentDictionary<uint, IEntityMovementDataHandler>()));

            Assert.That(body.SentBySender, Is.False);
        }

        [Test]
        public void OnSenderReleased_ClearsSentBySender()
        {
            ServerMeshShipPuppetBody body = NewBody();
            body.SentBySender = true;

            body.OnSenderReleased();

            Assert.That(body.SentBySender, Is.False);
        }

        [Test]
        public void DiscreteVersion_PacksTheSamplesSequenceAuthorityAndEpoch()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(Vector3.zero, Vector3.zero);
            body.Accept(in sample, 0);

            Assert.That(body.DiscreteVersion, Is.EqualTo(3u | 0u << 8 | 7u << 16));
            Assert.That(body.ConnectionId, Is.EqualTo(-1));
        }
    }
}
