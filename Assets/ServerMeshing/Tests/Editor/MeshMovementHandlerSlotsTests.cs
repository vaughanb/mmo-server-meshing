using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshMovementHandlerSlotsTests
    {
        private const uint Id = 42;

        private ConcurrentDictionary<uint, IEntityMovementDataHandler> _handlers;
        private MeshMovementHandlerSlots _slots;
        private MeshFakeKitHandler _kit;
        private List<uint> _removed;

        [SetUp]
        public void SetUp()
        {
            _handlers = new ConcurrentDictionary<uint, IEntityMovementDataHandler>();
            _slots = new MeshMovementHandlerSlots(_handlers);
            _kit = new MeshFakeKitHandler(Id);
            _handlers[Id] = _kit;
            _removed = new List<uint>();
            _slots.LayerRemoved += _removed.Add;
        }

        [Test]
        public void Constructor_NullDictionary_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new MeshMovementHandlerSlots(null));

            Assert.That(thrown.ParamName, Is.EqualTo("handlers"));
        }

        [Test]
        public void For_NullManager_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => MeshMovementHandlerSlots.For(null));

            Assert.That(thrown.ParamName, Is.EqualTo("manager"));
        }

        [Test]
        public void TryInstall_NullLayer_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => _slots.TryInstall(Id, null, false, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("layer"));
        }

        [Test]
        public void TryInstall_ReplaceableLayerWithoutTheInterface_ThrowsNamingIt()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(() => _slots.TryInstall(Id, new MeshFakeKitHandler(Id), true, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("layer"));
            Assert.That(thrown.Message, Does.Contain(Id.ToString()));
            Assert.That(_handlers[Id], Is.SameAs(_kit));
        }

        [Test]
        public void TryRemove_NullLayer_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => _slots.TryRemove(Id, null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("layer"));
        }

        [Test]
        public void TryInstall_UnknownId_FailsNamingTheId()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(7);

            bool installed = _slots.TryInstall(7, layer, false, out string error);

            Assert.That(installed, Is.False);
            Assert.That(error, Does.Contain("7"));
            Assert.That(_handlers.ContainsKey(7), Is.False);
            Assert.That(_slots.HasLayer(7), Is.False);
        }

        [Test]
        public void TryInstall_OverANonReplaceableLayer_FailsNamingTheId()
        {
            MeshFakeKitHandler first = new MeshFakeKitHandler(Id);
            MeshFakeReplaceableLayer second = new MeshFakeReplaceableLayer(Id);
            Assert.That(_slots.TryInstall(Id, first, false, out _), Is.True);

            bool installed = _slots.TryInstall(Id, second, true, out string error);

            Assert.That(installed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(_handlers[Id], Is.SameAs(first));
        }

        [Test]
        public void TryInstall_TheSameLayerTwice_Fails()
        {
            MeshFakeReplaceableLayer layer = new MeshFakeReplaceableLayer(Id);
            Assert.That(_slots.TryInstall(Id, layer, true, out _), Is.True);

            bool installed = _slots.TryInstall(Id, layer, true, out string error);

            Assert.That(installed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(layer.replacedCount, Is.EqualTo(0));
        }

        [Test]
        public void TryRemove_ByANonOwner_FailsNamingTheId()
        {
            MeshFakeKitHandler owner = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, owner, false, out _), Is.True);

            bool removed = _slots.TryRemove(Id, new MeshFakeKitHandler(Id), out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(_handlers[Id], Is.SameAs(owner));
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void TryRemove_WithoutALayer_FailsNamingTheId()
        {
            bool removed = _slots.TryRemove(Id, new MeshFakeKitHandler(Id), out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(_handlers[Id], Is.SameAs(_kit));
        }

        [Test]
        public void TryRemove_AfterTheKitReRegisteredTheId_DoesNotOverwriteTheNewHandler()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);
            MeshFakeKitHandler reused = new MeshFakeKitHandler(Id);
            _handlers[Id] = reused;

            bool removed = _slots.TryRemove(Id, layer, out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(_handlers[Id], Is.SameAs(reused));
            Assert.That(_removed, Is.Empty);
            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_slots.TryGetKitHandler(Id, out IEntityMovementDataHandler kit), Is.True);
            Assert.That(kit, Is.SameAs(reused));
        }

        [Test]
        public void TryRemove_AfterTheKitRemovedTheEntry_WritesNothingBack()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);
            _handlers.TryRemove(Id, out _);

            bool removed = _slots.TryRemove(Id, layer, out string error);

            Assert.That(removed, Is.False);
            Assert.That(error, Does.Contain(Id.ToString()));
            Assert.That(_handlers.ContainsKey(Id), Is.False);
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void Forget_AfterTheKitRemovedTheEntry_DoesNotRestoreIt()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);
            _handlers.TryRemove(Id, out _);

            _slots.Forget(Id);

            Assert.That(_handlers.ContainsKey(Id), Is.False);
            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void Forget_AfterTheKitReRegisteredTheId_KeepsTheNewHandler()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);
            MeshFakeKitHandler reused = new MeshFakeKitHandler(Id);
            _handlers[Id] = reused;

            _slots.Forget(Id);

            Assert.That(_handlers[Id], Is.SameAs(reused));
        }

        [Test]
        public void Forget_WhileTheLayerIsInstalled_RemovesTheEntryWithoutRestoringTheKit()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);

            _slots.Forget(Id);

            Assert.That(_handlers.ContainsKey(Id), Is.False);
            Assert.That(_slots.TryRemove(Id, layer, out _), Is.False);
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void Forget_WithoutARecord_ChangesNothing()
        {
            _slots.Forget(Id);

            Assert.That(_handlers[Id], Is.SameAs(_kit));
        }

        [Test]
        public void TryInstall_AfterTheKitEvictedAReplaceableLayer_TakesTheNewKitHandlerWithoutOnReplaced()
        {
            MeshFakeReplaceableLayer evicted = new MeshFakeReplaceableLayer(Id);
            Assert.That(_slots.TryInstall(Id, evicted, true, out _), Is.True);
            MeshFakeKitHandler reused = new MeshFakeKitHandler(Id);
            _handlers[Id] = reused;
            MeshFakeKitHandler body = new MeshFakeKitHandler(Id);

            bool installed = _slots.TryInstall(Id, body, false, out string error);

            Assert.That(installed, Is.True, error);
            Assert.That(evicted.replacedCount, Is.EqualTo(0));
            Assert.That(_slots.TryGetKitHandler(Id, out IEntityMovementDataHandler kit), Is.True);
            Assert.That(kit, Is.SameAs(reused));
            Assert.That(_slots.TryRemove(Id, body, out _), Is.True);
            Assert.That(_handlers[Id], Is.SameAs(reused));
        }

        [Test]
        public void TryGetKitHandler_UnknownId_IsFalse()
        {
            bool found = _slots.TryGetKitHandler(7, out IEntityMovementDataHandler kit);

            Assert.That(found, Is.False);
            Assert.That(kit, Is.Null);
        }

        [Test]
        public void TryGetKitHandler_WithoutALayer_IsTheSlotsValue()
        {
            bool found = _slots.TryGetKitHandler(Id, out IEntityMovementDataHandler kit);

            Assert.That(found, Is.True);
            Assert.That(kit, Is.SameAs(_kit));
        }

        [Test]
        public void Reset_DropsEveryRecordAndLeavesTheDictionary()
        {
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);

            _slots.Reset();

            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_slots.TryRemove(Id, layer, out _), Is.False);
            Assert.That(_handlers[Id], Is.SameAs(layer));
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void FullOrder_ReplaceableLayerThenBodyThenRemoval_RestoresTheOriginalKitHandler()
        {
            MeshFakeReplaceableLayer source = new MeshFakeReplaceableLayer(Id);
            MeshFakeKitHandler body = new MeshFakeKitHandler(Id);

            Assert.That(_slots.TryInstall(Id, source, true, out string error), Is.True, error);
            Assert.That(_handlers[Id], Is.SameAs(source));
            Assert.That(_slots.HasLayer(Id), Is.True);

            Assert.That(_slots.TryInstall(Id, body, false, out error), Is.True, error);
            Assert.That(_handlers[Id], Is.SameAs(body));
            Assert.That(source.replacedCount, Is.EqualTo(1));
            Assert.That(_slots.TryGetKitHandler(Id, out IEntityMovementDataHandler kit), Is.True);
            Assert.That(kit, Is.SameAs(_kit), "the record keeps the original kit handler, never the replaced layer");

            Assert.That(_slots.TryInstall(Id, new MeshFakeReplaceableLayer(Id), true, out error), Is.False);
            Assert.That(_slots.TryRemove(Id, source, out error), Is.False);

            Assert.That(_slots.TryRemove(Id, body, out error), Is.True, error);
            Assert.That(_handlers[Id], Is.SameAs(_kit));
            Assert.That(_slots.HasLayer(Id), Is.False);
            Assert.That(_removed, Is.EqualTo(new[] { Id }));
            Assert.That(source.replacedCount, Is.EqualTo(1));

            Assert.That(_slots.TryRemove(Id, body, out error), Is.False);
            Assert.That(_removed, Is.EqualTo(new[] { Id }));
        }

        [Test]
        public void LayerRemoved_HandlerMayInstallAReplaceableLayerAgain()
        {
            MeshFakeReplaceableLayer source = new MeshFakeReplaceableLayer(Id);
            MeshFakeKitHandler body = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, source, true, out _), Is.True);
            Assert.That(_slots.TryInstall(Id, body, false, out _), Is.True);
            bool reinstalled = false;
            _slots.LayerRemoved += id => reinstalled = _slots.TryInstall(id, source, true, out _);

            Assert.That(_slots.TryRemove(Id, body, out string error), Is.True, error);

            Assert.That(reinstalled, Is.True);
            Assert.That(_handlers[Id], Is.SameAs(source));
            Assert.That(_slots.TryGetKitHandler(Id, out IEntityMovementDataHandler kit), Is.True);
            Assert.That(kit, Is.SameAs(_kit));
        }

        [Test]
        public void OnReplaced_RunsAfterTheNewLayerIsRecorded()
        {
            MeshFakeReplaceableLayer source = new MeshFakeReplaceableLayer(Id);
            MeshFakeKitHandler body = new MeshFakeKitHandler(Id);
            Assert.That(_slots.TryInstall(Id, source, true, out _), Is.True);
            bool bodyHeldTheSlot = false;
            source.onReplaced = () => bodyHeldTheSlot = _slots.HasLayer(Id) && ReferenceEquals(_handlers[Id], body);

            Assert.That(_slots.TryInstall(Id, body, false, out _), Is.True);

            Assert.That(bodyHeldTheSlot, Is.True);
        }

        [Test]
        public void TwoIds_AreIndependent()
        {
            MeshFakeKitHandler otherKit = new MeshFakeKitHandler(43);
            _handlers[43] = otherKit;
            MeshFakeKitHandler layer = new MeshFakeKitHandler(Id);
            MeshFakeKitHandler otherLayer = new MeshFakeKitHandler(43);

            Assert.That(_slots.TryInstall(Id, layer, false, out _), Is.True);
            Assert.That(_slots.TryInstall(43, otherLayer, false, out _), Is.True);
            Assert.That(_slots.TryRemove(Id, layer, out _), Is.True);

            Assert.That(_handlers[Id], Is.SameAs(_kit));
            Assert.That(_handlers[43], Is.SameAs(otherLayer));
            Assert.That(_slots.HasLayer(43), Is.True);
        }
    }
}
