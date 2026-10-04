using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Serialization;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public partial class MeshKinematicCharacterBodyTests
    {
        private const uint Id = 42;
        private const string GoldenA = "11 00 00 00 C0 3F 00 00 20 C0 00 00 50 40 FA 20 BF 02 00 00 00 00";

        private GameObject _gameObject;
        private Transform _transform;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("MeshKinematicCharacterBodyTests") { hideFlags = HideFlags.HideAndDontSave };
            _transform = _gameObject.transform;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_gameObject);
        }

        private MeshKinematicCharacterBody NewBody(bool kitIsNavMesh, LiteNetLibIdentity identity = null)
        {
            return new MeshKinematicCharacterBody(Id, identity, _transform, new MeshFakeKitHandler(Id), kitIsNavMesh, new MeshKinematicTrack(15f, 0.3f, 4f));
        }

        private LiteNetLibIdentity NewIdentity(int subscribers)
        {
            LiteNetLibIdentity identity = _gameObject.AddComponent<LiteNetLibIdentity>();
            for (int connectionId = 1; connectionId <= subscribers; ++connectionId)
                Assert.That(identity.AddSubscriber(connectionId), Is.True);
            Assert.That(identity.CountSubscribers(), Is.EqualTo(subscribers));
            return identity;
        }

        private static string Hex(NetDataWriter writer)
        {
            return BitConverter.ToString(writer.CopyData()).Replace("-", " ");
        }

        private static string ServerBytes(MeshKinematicCharacterBody body, long timestamp, out bool reliable)
        {
            NetDataWriter writer = new NetDataWriter();
            Assert.That(body.WriteServerState(timestamp, writer, out reliable), Is.True);
            return Hex(writer);
        }

        private string KitBytes(uint flags, byte extra)
        {
            NetDataWriter writer = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, extra, _transform.position, _transform.eulerAngles.y);
            return Hex(writer);
        }

        private static IEntityMovementDataHandler KitOfKind(string kind)
        {
            switch (kind)
            {
                case "builtin":
                    return (IEntityMovementDataHandler)FormatterServices.GetUninitializedObject(typeof(BuiltInEntityMovementFunctions3D));
                case "navmesh":
                    return (IEntityMovementDataHandler)FormatterServices.GetUninitializedObject(typeof(NavMeshEntityMovement));
                case "fake":
                    return new MeshFakeKitHandler(Id);
                default:
                    return null;
            }
        }

        [Test]
        public void Constructor_NullTransform_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshKinematicCharacterBody(Id, null, null, new MeshFakeKitHandler(Id), false, new MeshKinematicTrack(15f, 0.3f, 4f)));

            Assert.That(thrown.ParamName, Is.EqualTo("transform"));
        }

        [Test]
        public void Constructor_NullKitHandler_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshKinematicCharacterBody(Id, null, _transform, null, false, new MeshKinematicTrack(15f, 0.3f, 4f)));

            Assert.That(thrown.ParamName, Is.EqualTo("kitHandler"));
        }

        [Test]
        public void Constructor_DefaultTrack_ThrowsNamingIt()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(() => new MeshKinematicCharacterBody(Id, null, _transform, new MeshFakeKitHandler(Id), false, default));

            Assert.That(thrown.ParamName, Is.EqualTo("track"));
        }

        [Test]
        public void Begin_NullEntity_ThrowsNamingIt()
        {
            MeshMovementHandlerSlots slots = new MeshMovementHandlerSlots(new ConcurrentDictionary<uint, IEntityMovementDataHandler>());

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => NewBody(false).Begin(null, slots, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("entity"));
        }

        [Test]
        public void End_NullSlots_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => NewBody(false).End(null, null));

            Assert.That(thrown.ParamName, Is.EqualTo("slots"));
        }

        [Test]
        public void End_NeverBegun_ChangesNothing()
        {
            ConcurrentDictionary<uint, IEntityMovementDataHandler> handlers = new ConcurrentDictionary<uint, IEntityMovementDataHandler>();
            MeshFakeKitHandler kit = new MeshFakeKitHandler(Id);
            handlers[Id] = kit;
            MeshKinematicCharacterBody body = NewBody(false);

            body.End(null, new MeshMovementHandlerSlots(handlers));

            Assert.That(handlers[Id], Is.SameAs(kit));
            Assert.That(body.IsBegun, Is.False);
        }

        [Test]
        public void WriteKitTransform_NullWriter_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => MeshKinematicCharacterBody.WriteKitTransform(null, 0, 0, Vector3.zero, 0f));

            Assert.That(thrown.ParamName, Is.EqualTo("writer"));
        }

        private static IEnumerable<TestCaseData> RefusalCases()
        {
            yield return new TestCaseData("fake", false, false, true, MeshPuppetFallback.Handler).SetName("Refusal_OtherHandler_Handler");
            yield return new TestCaseData("null", false, false, true, MeshPuppetFallback.Handler).SetName("Refusal_NoHandler_Handler");
            yield return new TestCaseData("fake", true, true, false, MeshPuppetFallback.Handler).SetName("Refusal_OtherHandlerMountedSummonedGrid_HandlerFirst");
            yield return new TestCaseData("builtin", false, false, false, MeshPuppetFallback.Grid).SetName("Refusal_GridEnabled_Grid");
            yield return new TestCaseData("navmesh", true, true, false, MeshPuppetFallback.Grid).SetName("Refusal_GridEnabledMounted_GridBeforeMounted");
            yield return new TestCaseData("builtin", true, false, true, MeshPuppetFallback.Mounted).SetName("Refusal_Mounted_Mounted");
            yield return new TestCaseData("navmesh", true, true, true, MeshPuppetFallback.Mounted).SetName("Refusal_MountedSummoned_MountedBeforeSummoned");
            yield return new TestCaseData("navmesh", false, true, true, MeshPuppetFallback.Summoned).SetName("Refusal_Summoned_Summoned");
            yield return new TestCaseData("builtin", false, false, true, MeshPuppetFallback.None).SetName("Refusal_PlayerKitHandler_None");
            yield return new TestCaseData("navmesh", false, false, true, MeshPuppetFallback.None).SetName("Refusal_NavMeshKitHandler_None");
        }

        [TestCaseSource(nameof(RefusalCases))]
        public void Refusal(string kitKind, bool mounted, bool summoned, bool gridDisabled, MeshPuppetFallback expected)
        {
            MeshPuppetFallback refusal = MeshKinematicCharacterBody.Refusal(KitOfKind(kitKind), mounted, summoned, gridDisabled);

            Assert.That(refusal, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> GoldenCases()
        {
            yield return new TestCaseData(17u, (byte)0, new Vector3(1.5f, -2.5f, 3.25f), 90f, GoldenA)
                .SetName("WriteKitTransform_FourByteYaw_MatchesTheKitLayout");
            yield return new TestCaseData(128u, (byte)1, Vector3.zero, 0f, "80 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")
                .SetName("WriteKitTransform_TeleportAloneAtTheOrigin_NineteenBytes");
            yield return new TestCaseData(241u, (byte)2, new Vector3(100f, 5f, -200f), 359.5f, "F1 01 02 00 00 C8 42 00 00 A0 40 00 00 48 C3 FA 98 F8 0A 00 00 00 00")
                .SetName("WriteKitTransform_TwoByteFlags_MatchesTheKitLayout");
            yield return new TestCaseData(255u, (byte)0, Vector3.zero, 1f, "F1 0F 00 00 00 00 00 00 00 00 00 00 00 00 00 F7 E0 00 00 00 00")
                .SetName("WriteKitTransform_AllLowFlags_TwoBytes");
            yield return new TestCaseData(145u, (byte)0, new Vector3(-7.25f, 0.5f, 12f), -90f, "91 00 00 00 E8 C0 00 00 00 3F 00 00 40 41 FA 60 3D 08 00 00 00 00")
                .SetName("WriteKitTransform_NegativeYaw_IsNormalisedLikeEulerAngles");
            yield return new TestCaseData(145u, (byte)0, new Vector3(-7.25f, 0.5f, 12f), 270f, "91 00 00 00 E8 C0 00 00 00 3F 00 00 40 41 FA 60 3D 08 00 00 00 00")
                .SetName("WriteKitTransform_Yaw270_SameBytesAsMinus90");
            yield return new TestCaseData(0u, (byte)0, Vector3.zero, 12.3125f, "00 00 00 00 00 00 00 00 00 00 00 00 00 00 F9 57 40 00 00 00 00")
                .SetName("WriteKitTransform_HalfMilliDegree_RoundsHalfToEven");
            yield return new TestCaseData(0u, (byte)0, Vector3.zero, 450f, "00 00 00 00 00 00 00 00 00 00 00 00 00 00 FA 20 BF 02 00 00 00 00")
                .SetName("WriteKitTransform_YawPastOneTurn_IsNormalised");
            yield return new TestCaseData(0u, (byte)0, Vector3.zero, 360f, "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")
                .SetName("WriteKitTransform_FullTurn_IsZero");
            yield return new TestCaseData(0u, (byte)0, Vector3.zero, 0.12f, "00 00 00 00 00 00 00 00 00 00 00 00 00 00 F0 00 00 00 00")
                .SetName("WriteKitTransform_SmallYaw_OneByte");
        }

        [TestCaseSource(nameof(GoldenCases))]
        public void WriteKitTransform_Golden(uint flags, byte extra, Vector3 position, float yaw, string expected)
        {
            NetDataWriter writer = new NetDataWriter();

            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, extra, position, yaw);

            Assert.That(Hex(writer), Is.EqualTo(expected));
        }

        [Test]
        public void WriteKitTransform_RoundTrip_ThroughTheKitServerReaderEndsAtEndOfData()
        {
            NetDataWriter writer = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(writer, 241u, 2, new Vector3(100f, 5f, -200f), 359.5f);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            reader.ServerReadSyncTransformMessage3D(out MovementState state, out ExtraMovementState extra, out Vector3 position, out float yaw);
            int forceApplierCount = reader.GetInt();

            Assert.That((uint)state, Is.EqualTo(241u));
            Assert.That(extra, Is.EqualTo(ExtraMovementState.IsWalking));
            Assert.That(position, Is.EqualTo(new Vector3(100f, 5f, -200f)));
            Assert.That(yaw, Is.EqualTo(359.5f).Within(0.001f));
            Assert.That(forceApplierCount, Is.EqualTo(0));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void WriteServerState_WritesTheTransformPoseWithTheSnapshotFlags_GoldenBytes()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(new Vector3(9f, 9f, 9f), Vector3.zero, 10f, 17, 0, false, 1f);
            _transform.SetPositionAndRotation(new Vector3(1.5f, -2.5f, 3.25f), Quaternion.Euler(0f, 90f, 0f));

            string bytes = ServerBytes(body, 100, out bool reliable);

            Assert.That(bytes, Is.EqualTo(GoldenA));
            Assert.That(reliable, Is.False);
        }

        [TestCase(0xD1, 0x11)]
        [TestCase(0xFF, 0x3F)]
        [TestCase(0x40, 0x00)]
        [TestCase(0x80, 0x00)]
        public void Accept_OwnersJumpAndTeleportBits_AreMaskedOut(int snapshotFlags, int writtenFlags)
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 0f, (byte)snapshotFlags, 3, false, 1f);

            string bytes = ServerBytes(body, 100, out bool reliable);

            Assert.That(bytes, Is.EqualTo(KitBytes((uint)writtenFlags, 3)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void Accept_RefusedSnapshot_ReturnsFalseAndChangesNothing()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            Assert.That(body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 1, false, 1f), Is.True);

            bool accepted = body.Accept(new Vector3(float.NaN, 0f, 0f), Vector3.zero, 0f, 0x01, 2, true, 2f);

            Assert.That(accepted, Is.False);
            Assert.That(body.DiscreteVersion, Is.EqualTo(0u));
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void WriteState_WritesTheSameBytesAsWriteServerState()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(Vector3.zero, new Vector3(1f, 0f, 0f), 0f, 0x11, 1, false, 1f);
            _transform.SetPositionAndRotation(new Vector3(3f, 0f, -4f), Quaternion.Euler(0f, 200f, 0f));
            NetDataWriter writer = new NetDataWriter();

            Assert.That(body.WriteState(100, writer, out bool reliable), Is.True);

            Assert.That(Hex(writer), Is.EqualTo(ServerBytes(body, 100, out bool serverReliable)));
            Assert.That(reliable, Is.EqualTo(serverReliable));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TwoWritesWithOneTimestamp_AreIdentical(bool discontinuity)
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, discontinuity, 1f);

            string first = ServerBytes(body, 100, out bool firstReliable);
            string second = ServerBytes(body, 100, out bool secondReliable);

            Assert.That(firstReliable, Is.EqualTo(discontinuity));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(secondReliable, Is.EqualTo(firstReliable));
        }

        [Test]
        public void Teleport_FirstSnapshotMarkedDiscontinuous_IsNotArmed()
        {
            MeshKinematicCharacterBody body = NewBody(true);

            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);

            Assert.That(body.LastAcceptSnapped, Is.False);
            Assert.That(body.DiscreteVersion, Is.EqualTo(0u));
            Assert.That(body.IsAtRest, Is.True);
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void Teleport_NavMeshBodyNobodyWatches_IsNotArmedButTheVersionRises()
        {
            LiteNetLibIdentity identity = NewIdentity(0);
            MeshKinematicCharacterBody body = NewBody(true, identity);
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);

            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);

            Assert.That(body.Identity, Is.SameAs(identity));
            Assert.That(body.LastAcceptSnapped, Is.True);
            Assert.That(body.DiscreteVersion, Is.EqualTo(1u));
            Assert.That(body.IsAtRest, Is.True);
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void Teleport_NavMeshBodyWithASubscriber_IsSentAloneAndReliablyForExactlyOneTimestamp()
        {
            MeshKinematicCharacterBody body = NewBody(true, NewIdentity(1));
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);

            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);

            Assert.That(body.DiscreteVersion, Is.EqualTo(1u));
            Assert.That(body.IsAtRest, Is.False);
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(ServerBytes(body, 100, out reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(ServerBytes(body, 150, out reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
            Assert.That(body.IsAtRest, Is.True);
        }

        private static IEnumerable<TestCaseData> MissCases()
        {
            yield return new TestCaseData(3.99f, false, 0u, 0x11u, false).SetName("Accept_MissJustUnderTheSnapDistance_NeitherSnapsNorArms");
            yield return new TestCaseData(4.01f, true, 1u, 128u, true).SetName("Accept_MissPastTheSnapDistance_SnapsAndArmsTheTeleport");
        }

        [TestCaseSource(nameof(MissCases))]
        public void Accept_MissAgainstTheSnapDistance(float miss, bool snaps, uint version, uint writtenFlags, bool writtenReliably)
        {
            MeshKinematicCharacterBody body = NewBody(true, NewIdentity(1));
            body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 1, false, 0f);

            body.Accept(new Vector3(miss, 0f, 0f), Vector3.zero, 0f, 0x11, 1, false, 0.1f);

            Assert.That(body.LastAcceptSnapped, Is.EqualTo(snaps));
            Assert.That(body.DiscreteVersion, Is.EqualTo(version));
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(writtenFlags, 1)));
            Assert.That(reliable, Is.EqualTo(writtenReliably));
        }

        [Test]
        public void Teleport_NavMeshBody_IsSentAloneAndReliablyForExactlyOneTimestamp()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);

            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(ServerBytes(body, 100, out reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(body.IsAtRest, Is.False);

            Assert.That(ServerBytes(body, 150, out reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
            Assert.That(ServerBytes(body, 100, out reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
            Assert.That(body.IsAtRest, Is.True);
        }

        [Test]
        public void Teleport_PlayerBody_IsNeverSet()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x91, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x91, 1, true, 1f);

            Assert.That(body.LastAcceptSnapped, Is.True);
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
            Assert.That(body.IsAtRest, Is.True);
        }

        [Test]
        public void Teleport_ArmedWhileUnobserved_IsDroppedByApplyNow()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);
            Assert.That(body.IsAtRest, Is.False);

            body.ApplyNow(1f);

            Assert.That(body.IsAtRest, Is.True);
            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void Teleport_ASecondDiscontinuity_IsSentAtItsOwnFirstTimestamp()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 0, false, 0f);
            body.Accept(new Vector3(1f, 0f, 0f), Vector3.zero, 0f, 0x11, 0, true, 1f);
            Assert.That(ServerBytes(body, 100, out _), Is.EqualTo(KitBytes(128u, 0)));

            body.Accept(new Vector3(9f, 0f, 0f), Vector3.zero, 0f, 0x11, 0, true, 2f);

            Assert.That(ServerBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(128u, 0)));
            Assert.That(reliable, Is.True);
            Assert.That(ServerBytes(body, 200, out reliable), Is.EqualTo(KitBytes(0x11, 0)));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void DiscreteVersion_ChangesOnlyWhenTheTrackSnaps()
        {
            MeshKinematicCharacterBody navMesh = NewBody(true);
            MeshKinematicCharacterBody player = NewBody(false);

            navMesh.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, false, 1f);
            player.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, false, 1f);
            Assert.That(navMesh.DiscreteVersion, Is.EqualTo(0u));
            Assert.That(player.DiscreteVersion, Is.EqualTo(0u));

            navMesh.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, true, 2f);
            player.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, true, 2f);
            navMesh.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, false, 3f);
            player.Accept(Vector3.zero, Vector3.one, 0f, 0, 0, false, 3f);

            Assert.That(navMesh.DiscreteVersion, Is.EqualTo(1u));
            Assert.That(player.DiscreteVersion, Is.EqualTo(1u));
        }

        [Test]
        public void IsAtRest_FollowsTheOwnersVelocity()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            Assert.That(body.IsAtRest, Is.True);

            body.Accept(Vector3.zero, new Vector3(0.02f, 0f, 0f), 0f, 0, 0, false, 1f);
            Assert.That(body.IsAtRest, Is.False);

            body.Accept(Vector3.zero, new Vector3(0.005f, 0f, 0f), 0f, 0, 0, false, 2f);
            Assert.That(body.IsAtRest, Is.True);
        }

        [Test]
        public void ReadClientStateAtServer_IgnoresTheUpload()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 0, false, 1f);
            _transform.position = new Vector3(1f, 2f, 3f);
            NetDataWriter upload = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(upload, 0x01, 0, new Vector3(50f, 0f, 50f), 180f);
            NetDataReader reader = new NetDataReader(upload.CopyData());

            body.ReadClientStateAtServer(5, reader);

            Assert.That(reader.Position, Is.EqualTo(0));
            Assert.That(_transform.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(ServerBytes(body, 100, out _), Is.EqualTo(KitBytes(0x11, 0)));
        }

        [Test]
        public void ReadServerStateAtClient_ConsumesExactlyOneRecordAndEndsAtEndOfData()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 1, true, 1f);
            _transform.SetPositionAndRotation(new Vector3(-7.25f, 0.5f, 12f), Quaternion.Euler(0f, 270f, 0f));
            NetDataWriter writer = new NetDataWriter();
            body.WriteServerState(100, writer, out _);
            body.WriteServerState(100, writer, out _);

            NetDataReader reader = new NetDataReader(writer.CopyData());
            body.ReadServerStateAtClient(100, reader);
            Assert.That(reader.EndOfData, Is.False);
            body.ReadServerStateAtClient(100, reader);

            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void HandlerMembers_ServeNoClient()
        {
            MeshFakeKitHandler kit = new MeshFakeKitHandler(Id);
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, null, _transform, kit, true, new MeshKinematicTrack(15f, 0.3f, 4f));
            NetDataWriter writer = new NetDataWriter();

            bool wrote = body.WriteClientState(100, writer, out bool reliable);

            Assert.That(wrote, Is.False);
            Assert.That(reliable, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
            Assert.That(body.ConnectionId, Is.EqualTo(-1));
            Assert.That(body.ObjectId, Is.EqualTo(Id));
            Assert.That(body.Identity, Is.Null);
            Assert.That(body.KitHandler, Is.SameAs(kit));
            Assert.That(body.KitIsNavMesh, Is.True);
            Assert.That(body.IsBegun, Is.False);
        }

        [Test]
        public void CreateMovementData_IsTheCurrentPose()
        {
            MeshKinematicCharacterBody body = NewBody(true);
            body.Accept(Vector3.zero, Vector3.zero, 0f, 0x11, 2, true, 1f);
            _transform.SetPositionAndRotation(new Vector3(1.5f, -2.5f, 3.25f), Quaternion.Euler(0f, 90f, 0f));

            MovementData data = body.CreateMovementData(out List<EntityMovementForceApplier> forceAppliers);

            Assert.That(data.movementState, Is.EqualTo(0x11u));
            Assert.That(data.extraMovementState, Is.EqualTo(2));
            Assert.That(new Vector3(data.worldPosition.x, data.worldPosition.y, data.worldPosition.z), Is.EqualTo(new Vector3(1.5f, -2.5f, 3.25f)));
            Assert.That(data.yAngle, Is.EqualTo(90f).Within(0.001f));
            Assert.That(data.shouldSendReliably, Is.False);
            Assert.That(forceAppliers, Is.Not.Null.And.Empty);
        }

        [Test]
        public void Pose_BeforeTheFirstSnapshot_IsTheTransformPosition()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            _transform.position = new Vector3(4f, 5f, 6f);

            Assert.That(body.Pose(1f), Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Tick_BeforeTheFirstSnapshot_MovesNothing()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            _transform.position = new Vector3(4f, 5f, 6f);

            body.Tick(1f);

            Assert.That(_transform.position, Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Tick_ObservedPuppet_GlidesOntoANewSnapshotWithoutAJump()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(new Vector3(2f, 0f, 0f), Vector3.zero, 90f, 0x10, 0, false, 0f);
            body.Tick(1f / 60f);
            Assert.That(_transform.position, Is.EqualTo(new Vector3(2f, 0f, 0f)));
            Assert.That(_transform.eulerAngles.y, Is.EqualTo(90f).Within(1e-3f));

            body.Accept(new Vector3(3f, 0f, 0f), Vector3.zero, 90f, 0x10, 0, false, 0.1f);
            body.Tick(0.1f);

            Assert.That(body.LastAcceptSnapped, Is.False);
            Assert.That(_transform.position, Is.EqualTo(new Vector3(2f, 0f, 0f)));
            body.Tick(0.6f);
            Assert.That(Vector3.Distance(_transform.position, new Vector3(3f, 0f, 0f)), Is.EqualTo(Mathf.Exp(-7.5f)).Within(1e-5f));
            Assert.That(_transform.position, Is.EqualTo(body.Pose(0.6f)));
            Assert.That(_transform.eulerAngles.y, Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void ApplyNow_LaterThanTheAcceptTime_IsPlacedAtThePose()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 45f, 0x11, 0, false, 10f);

            body.ApplyNow(10.05f);

            Assert.That(Vector3.Distance(_transform.position, new Vector3(1.1f, 0f, 0f)), Is.LessThan(1e-4f));
            Assert.That(_transform.eulerAngles.y, Is.EqualTo(45f).Within(1e-3f));
            Assert.That(Vector3.Distance(body.Pose(10.05f), _transform.position), Is.LessThan(1e-4f));
        }

        private static IEnumerable<TestCaseData> ApplyNowAtTheAcceptTimeCases()
        {
            yield return new TestCaseData(false, false, new Vector3(1.5f, -2.5f, 3.25f), 45f)
                .SetName("ApplyNow_AtTheFirstSnapshotsAcceptTime_PlacesTheTransformExactlyAtIt");
            yield return new TestCaseData(true, true, new Vector3(1.5f, -2.5f, 3.25f), 45f)
                .SetName("ApplyNow_AtASnappedSnapshotsAcceptTime_PlacesTheTransformExactlyAtIt");
            yield return new TestCaseData(true, false, new Vector3(1.5f, -2.5f, 3.25f), 45f)
                .SetName("ApplyNow_AtAGlidingSnapshotsAcceptTime_DropsTheOffsetAndPlacesItExactly");
        }

        [TestCaseSource(nameof(ApplyNowAtTheAcceptTimeCases))]
        public void ApplyNow_AtTheAcceptTime(bool earlierSnapshot, bool discontinuity, Vector3 expectedPosition, float expectedYaw)
        {
            MeshKinematicCharacterBody body = NewBody(true, NewIdentity(0));
            if (earlierSnapshot)
                body.Accept(new Vector3(1f, -2.5f, 3.25f), Vector3.zero, 10f, 0x11, 0, false, 9.9f);
            body.Accept(new Vector3(1.5f, -2.5f, 3.25f), new Vector3(2f, 0f, 0f), 45f, 0x11, 0, discontinuity, 10f);

            body.ApplyNow(10f);

            Assert.That(_transform.position, Is.EqualTo(expectedPosition));
            Assert.That(_transform.eulerAngles.y, Is.EqualTo(expectedYaw).Within(1e-3f));
            Assert.That(body.Pose(10f), Is.EqualTo(expectedPosition));
        }

        [Test]
        public void WriteServerState_SentBySender_ReturnsFalseAndWritesNothing()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(new Vector3(1.5f, -2.5f, 3.25f), Vector3.zero, 90f, 0x11, 0, false, 0f);
            body.SentBySender = true;
            NetDataWriter writer = new NetDataWriter();

            bool wrote = body.WriteServerState(100, writer, out bool reliable);

            Assert.That(wrote, Is.False);
            Assert.That(reliable, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
        }

        [Test]
        public void WriteServerState_SentBySender_LeavesAnArmedTeleportForWriteState()
        {
            MeshKinematicCharacterBody body = NewBody(true, NewIdentity(1));
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);
            body.SentBySender = true;

            Assert.That(body.WriteServerState(99, new NetDataWriter(), out _), Is.False);
            NetDataWriter writer = new NetDataWriter();
            Assert.That(body.WriteState(100, writer, out bool reliable), Is.True);

            Assert.That(Hex(writer), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
        }

        [Test]
        public void WriteState_NullWriter_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => NewBody(false).WriteState(100, null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("writer"));
        }

        [Test]
        public void SentBySender_IsFalseOnANewBody()
        {
            Assert.That(NewBody(false).SentBySender, Is.False);
        }

        [Test]
        public void End_NeverBegun_ClearsSentBySender()
        {
            ConcurrentDictionary<uint, IEntityMovementDataHandler> handlers = new ConcurrentDictionary<uint, IEntityMovementDataHandler>();
            handlers[Id] = new MeshFakeKitHandler(Id);
            MeshKinematicCharacterBody body = NewBody(false);
            body.SentBySender = true;

            body.End(null, new MeshMovementHandlerSlots(handlers));

            Assert.That(body.SentBySender, Is.False);
            Assert.That(body.IsBegun, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WriteState_GoldenBytes_WhateverSentBySenderSays(bool sentBySender)
        {
            _transform.SetPositionAndRotation(new Vector3(1.5f, -2.5f, 3.25f), Quaternion.Euler(0f, 90f, 0f));
            MeshKinematicCharacterBody body = NewBody(false);
            body.Accept(new Vector3(1.5f, -2.5f, 3.25f), Vector3.zero, 90f, 0x11, 0, false, 0f);
            body.SentBySender = sentBySender;
            NetDataWriter writer = new NetDataWriter();

            Assert.That(body.WriteState(100, writer, out bool reliable), Is.True);

            Assert.That(Hex(writer), Is.EqualTo(GoldenA));
            Assert.That(reliable, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WriteState_Teleport_IsSentAloneAndReliablyForExactlyOneTimestamp(bool sentBySender)
        {
            MeshKinematicCharacterBody body = NewBody(true, NewIdentity(1));
            body.Accept(Vector3.zero, Vector3.zero, 30f, 0x11, 1, false, 0f);
            body.Accept(new Vector3(1f, 2f, 3f), Vector3.zero, 30f, 0x11, 1, true, 1f);
            body.SentBySender = sentBySender;

            Assert.That(StateBytes(body, 100, out bool reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(StateBytes(body, 100, out reliable), Is.EqualTo(KitBytes(128u, 1)));
            Assert.That(reliable, Is.True);
            Assert.That(StateBytes(body, 150, out reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
            Assert.That(StateBytes(body, 100, out reliable), Is.EqualTo(KitBytes(0x11, 1)));
            Assert.That(reliable, Is.False);
        }

        private static string StateBytes(MeshKinematicCharacterBody body, long timestamp, out bool reliable)
        {
            NetDataWriter writer = new NetDataWriter();
            Assert.That(body.WriteState(timestamp, writer, out reliable), Is.True);
            return Hex(writer);
        }
    }
}
