using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace LiteNetLibManager.Tests
{
    /// <summary>
    /// A kept object adopted by the new server's spawn applies its initial state as a diff: a sync
    /// field fires its change only when its value differs, a sync list fires the operations that turn the list it has
    /// into the one sent (Set / Add / RemoveLast), and an identical state fires nothing at all.
    /// </summary>
    public class KeptObjectAdoptionTests
    {
        public class AdoptionBehaviour : LiteNetLibBehaviour
        {
            public SyncFieldInt value = new SyncFieldInt();
            public SyncListInt items = new SyncListInt();
        }

        private GameObject _managerObject, _entityObject;
        private GameManagerHarness _manager;
        private LiteNetLibIdentity _identity;
        private AdoptionBehaviour _behaviour;
        private readonly List<string> _events = new List<string>();

        [SetUp]
        public void SpawnAnObjectWithState()
        {
            _managerObject = new GameObject("adoption manager");
            _entityObject = new GameObject("adoption entity");
            _manager = _managerObject.AddComponent<GameManagerHarness>();
            _manager.InitializeForTest();
            _identity = _entityObject.AddComponent<LiteNetLibIdentity>();
            _behaviour = _entityObject.AddComponent<AdoptionBehaviour>();
            typeof(LiteNetLibBehaviour).GetField("_identity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_behaviour, _identity);
            Assert.AreSame(_identity, _manager.Assets.NetworkSpawn(_identity, 42));
            // The state the object had on the server it left.
            Assert.IsTrue(_manager.ReadSyncElements(State(17, 1, 2, 3), _identity, 1, true));
            Assert.AreEqual(17, _behaviour.value.Value);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, _behaviour.items);
            _events.Clear();
            _behaviour.value.onChange += (initial, oldValue, newValue) => _events.Add($"value {oldValue}->{newValue} initial={initial}");
            _behaviour.items.onOperation += (op, index, oldItem, newItem) => _events.Add($"{op}@{index} {oldItem}->{newItem}");
        }

        [TearDown]
        public void Destroy()
        {
            Object.DestroyImmediate(_entityObject);
            Object.DestroyImmediate(_managerObject);
        }

        private NetDataReader State(int value, params int[] items)
        {
            var writer = new NetDataWriter();
            writer.PutPackedInt(2);
            writer.PutPackedInt(_behaviour.value.ElementId);
            writer.PutPackedInt(value);
            writer.PutPackedInt(_behaviour.items.ElementId);
            writer.PutPackedInt(items.Length);
            foreach (int item in items)
                writer.PutPackedInt(item);
            return new NetDataReader(writer.CopyData());
        }

        [Test]
        public void FreshInitialState_FiresEverything()
        {
            Assert.IsTrue(_manager.ReadSyncElements(State(17, 1, 2, 3), _identity, 2, true));

            CollectionAssert.AreEqual(new[] { "value 17->17 initial=True", "AddInitial@0 1->1", "AddInitial@1 2->2", "AddInitial@2 3->3" }, _events);
        }

        [Test]
        public void AdoptedWithTheSameState_FiresNothing()
        {
            _identity.BeginAdoption(42);

            Assert.IsTrue(_manager.ReadSyncElements(State(17, 1, 2, 3), _identity, 2, true));

            CollectionAssert.IsEmpty(_events);
            Assert.AreEqual(17, _behaviour.value.Value);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, _behaviour.items);
        }

        [Test]
        public void AdoptedWithChanges_FiresOnlyTheDifferences()
        {
            _identity.BeginAdoption(42);

            Assert.IsTrue(_manager.ReadSyncElements(State(18, 1, 9, 3, 4), _identity, 2, true));

            CollectionAssert.AreEqual(new[] { "value 17->18 initial=False", "Set@1 2->9", "Add@3 0->4" }, _events);
            Assert.AreEqual(18, _behaviour.value.Value);
            CollectionAssert.AreEqual(new[] { 1, 9, 3, 4 }, _behaviour.items);
        }

        [Test]
        public void AdoptedWithAShorterList_RemovesFromTheEnd()
        {
            _identity.BeginAdoption(42);

            Assert.IsTrue(_manager.ReadSyncElements(State(17, 1), _identity, 2, true));

            CollectionAssert.AreEqual(new[] { "RemoveLast@2 3->0", "RemoveLast@1 2->0" }, _events);
            CollectionAssert.AreEqual(new[] { 1 }, _behaviour.items);
        }

        [Test]
        public void AfterTheAdoption_AnInitialStateIsAFirstLoadAgain()
        {
            _identity.BeginAdoption(42);
            _identity.IsBeingAdopted = false;

            Assert.IsTrue(_manager.ReadSyncElements(State(17, 1, 2, 3), _identity, 2, true));

            Assert.AreEqual(4, _events.Count, string.Join(" | ", _events));
            Assert.AreEqual("value 17->17 initial=True", _events[0]);
        }
    }
}
