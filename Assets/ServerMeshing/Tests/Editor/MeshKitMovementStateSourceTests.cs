using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshKitMovementStateSourceTests
    {
        private const uint Id = 42;

        private MeshFakeIdentities _identities;
        private LiteNetLibIdentity _identity;
        private MeshFakePassKitHandler _kit;
        private List<MeshKitMovementStateSource> _replaced;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _identity = _identities.New(Id, 7);
            _kit = new MeshFakePassKitHandler(Id);
            _replaced = new List<MeshKitMovementStateSource>();
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private MeshKitMovementStateSource NewWrapper() => new MeshKitMovementStateSource(_kit, _identity, _replaced.Add);

        private static IEnumerable<TestCaseData> NullArgumentCases()
        {
            yield return new TestCaseData("inner").SetName("Constructor_NullInner_ThrowsNamingIt");
            yield return new TestCaseData("identity").SetName("Constructor_NullIdentity_ThrowsNamingIt");
            yield return new TestCaseData("replaced").SetName("Constructor_NullCallback_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(NullArgumentCases))]
        public void Constructor_NullArgument_ThrowsNamingIt(string missing)
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshKitMovementStateSource(
                missing == "inner" ? null : _kit,
                missing == "identity" ? null : _identity,
                missing == "replaced" ? null : (Action<MeshKitMovementStateSource>)_replaced.Add));

            Assert.That(thrown.ParamName, Is.EqualTo(missing));
        }

        [Test]
        public void Constructor_DestroyedIdentity_ThrowsNamingIt()
        {
            _identities.Destroy(_identity);

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => NewWrapper());

            Assert.That(thrown.ParamName, Is.EqualTo("identity"));
        }

        [Test]
        public void Constructor_AWrapperAsInner_ThrowsNamingIt()
        {
            MeshKitMovementStateSource inner = NewWrapper();

            ArgumentException thrown = Assert.Throws<ArgumentException>(() => new MeshKitMovementStateSource(inner, _identity, _replaced.Add));

            Assert.That(thrown.ParamName, Is.EqualTo("inner"));
            Assert.That(thrown.Message, Does.Contain(Id.ToString()));
        }

        [Test]
        public void Constructor_AKinematicBodyAsInner_ThrowsNamingIt()
        {
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, _identity, _identities.TransformOf(_identity), _kit, false, new MeshKinematicTrack(15f, 0.3f, 4f));

            ArgumentException thrown = Assert.Throws<ArgumentException>(() => new MeshKitMovementStateSource(body, _identity, _replaced.Add));

            Assert.That(thrown.ParamName, Is.EqualTo("inner"));
        }

        [Test]
        public void Wrapper_NeverImplementsTheKitFlagsInterface_SoTheKitPassNeverRecordsIt()
        {
            MeshKitMovementStateSource wrapper = NewWrapper();
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();

            pass.Record(wrapper);

            Assert.That(typeof(IEntityMovementServerStateFlags).IsAssignableFrom(typeof(MeshKitMovementStateSource)), Is.False);
            Assert.That(pass.RecordedCount, Is.EqualTo(0));
        }

        [Test]
        public void Members_DelegateToTheInnerHandler()
        {
            MeshKitMovementStateSource wrapper = NewWrapper();
            _kit.ConnectionId = 7;

            Assert.That(wrapper.Inner, Is.SameAs(_kit));
            Assert.That(wrapper.ObjectId, Is.EqualTo(Id));
            Assert.That(wrapper.Identity, Is.SameAs(_identity));
            Assert.That(wrapper.ConnectionId, Is.EqualTo(7));
            _kit.ConnectionId = 9;
            Assert.That(wrapper.ConnectionId, Is.EqualTo(9));
            Assert.That(wrapper.DiscreteVersion, Is.EqualTo(0u));
            Assert.That(wrapper.IsAtRest, Is.False);

            wrapper.ReadClientStateAtServer(1, new NetDataReader(new byte[4]));
            wrapper.ReadServerStateAtClient(1, new NetDataReader(new byte[4]));
            Assert.That(_kit.uploads, Is.EqualTo(1));
            Assert.That(_kit.clientReads, Is.EqualTo(1));
            Assert.That(wrapper.WriteClientState(1, new NetDataWriter(), out bool clientReliable), Is.False);
            Assert.That(clientReliable, Is.False);
            wrapper.CreateMovementData(out List<EntityMovementForceApplier> appliers);
            Assert.That(appliers, Is.Null);
        }

        [Test]
        public void WriteServerState_TellsTheKitToSkipIt_WritingNothing()
        {
            MeshKitMovementStateSource wrapper = NewWrapper();
            _kit.pendingTeleport = true;
            NetDataWriter writer = new NetDataWriter();

            bool wrote = wrapper.WriteServerState(1000, writer, out bool reliable);

            Assert.That(wrote, Is.False);
            Assert.That(reliable, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
            Assert.That(_kit.writes, Is.EqualTo(0));
        }

        [Test]
        public void WriteState_WritesTheInnerHandlersBytes()
        {
            MeshKitMovementStateSource wrapper = NewWrapper();
            _kit.pendingTeleport = true;
            NetDataWriter direct = new NetDataWriter();
            new MeshFakePassKitHandler(Id) { pendingTeleport = true }.WriteServerState(1000, direct, out bool directReliable);
            NetDataWriter writer = new NetDataWriter();

            bool wrote = wrapper.WriteState(1000, writer, out bool reliable);

            Assert.That(wrote, Is.True);
            Assert.That(reliable, Is.EqualTo(directReliable));
            Assert.That(reliable, Is.True);
            Assert.That(writer.CopyData(), Is.EqualTo(direct.CopyData()));
            Assert.That(writer.Length, Is.EqualTo(22));
        }

        [Test]
        public void OnReplaced_HandsTheWrapperToItsCallback()
        {
            MeshKitMovementStateSource wrapper = NewWrapper();

            wrapper.OnReplaced();

            Assert.That(_replaced, Is.EqualTo(new[] { wrapper }));
        }

        private static IEnumerable<TestCaseData> AllowListCases()
        {
            yield return new TestCaseData(typeof(BuiltInEntityMovementFunctions3D), true).SetName("IsDefaultAllowed_BuiltInFunctions3D_IsTrue");
            yield return new TestCaseData(typeof(NavMeshEntityMovement), true).SetName("IsDefaultAllowed_NavMeshEntityMovement_IsTrue");
            yield return new TestCaseData(typeof(MeshFakeBuiltInSubclass), false).SetName("IsDefaultAllowed_ASubclass_IsFalse");
            yield return new TestCaseData(typeof(MeshFakeKitHandler), false).SetName("IsDefaultAllowed_AnotherHandler_IsFalse");
            yield return new TestCaseData(null, false).SetName("IsDefaultAllowed_Null_IsFalse");
        }

        [TestCaseSource(nameof(AllowListCases))]
        public void IsDefaultAllowed_IsExact(Type handlerType, bool expected)
        {
            Assert.That(MeshKitMovementStateSource.IsDefaultAllowed(handlerType), Is.EqualTo(expected));
        }
    }
}
