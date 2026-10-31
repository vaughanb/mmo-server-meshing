using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshInteractionStateTests
    {
        [Test]
        public void RoundTrip_KeepsDialogNpcAndStorages()
        {
            MeshInteractionState state = new MeshInteractionState
            {
                dialogDataId = 42, afterRewardDialogDataId = 7, completingQuestDataId = 9,
                hasNpc = true, npcPosition = new Vector3(0.5f, 20f, 530f), npcAssetHash = 1234,
                storages = new List<MeshInteractionState.CarriedStorage>
                {
                    new MeshInteractionState.CarriedStorage { type = StorageType.Player, ownerId = "user-1", hasEntity = true, entityPosition = new Vector3(0.5f, 20f, 530f) },
                    new MeshInteractionState.CarriedStorage { type = StorageType.Guild, ownerId = "12", hasEntity = false },
                },
            };
            NetDataWriter writer = new NetDataWriter();
            state.Write(writer);

            MeshInteractionState read = MeshInteractionState.Read(new NetDataReader(writer.CopyData()));

            Assert.AreEqual(42, read.dialogDataId);
            Assert.AreEqual(7, read.afterRewardDialogDataId);
            Assert.AreEqual(9, read.completingQuestDataId);
            Assert.IsTrue(read.hasNpc);
            Assert.AreEqual(new Vector3(0.5f, 20f, 530f), read.npcPosition);
            Assert.AreEqual(1234, read.npcAssetHash);
            Assert.AreEqual(2, read.storages.Count);
            Assert.AreEqual(StorageType.Player, read.storages[0].type);
            Assert.AreEqual("user-1", read.storages[0].ownerId);
            Assert.IsTrue(read.storages[0].hasEntity);
            Assert.AreEqual(new Vector3(0.5f, 20f, 530f), read.storages[0].entityPosition);
            Assert.AreEqual(StorageType.Guild, read.storages[1].type);
            Assert.AreEqual("12", read.storages[1].ownerId);
            Assert.IsFalse(read.storages[1].hasEntity);
        }

        [Test]
        public void IsEmpty_OnlyWithNoDialogAndNoStorage()
        {
            Assert.IsTrue(new MeshInteractionState().IsEmpty);
            Assert.IsTrue(new MeshInteractionState { hasNpc = true, storages = new List<MeshInteractionState.CarriedStorage>() }.IsEmpty);
            Assert.IsFalse(new MeshInteractionState { dialogDataId = 1 }.IsEmpty);
            Assert.IsFalse(new MeshInteractionState { afterRewardDialogDataId = 1 }.IsEmpty);
            Assert.IsFalse(new MeshInteractionState { storages = new List<MeshInteractionState.CarriedStorage> { new MeshInteractionState.CarriedStorage() } }.IsEmpty);
        }

        [Test]
        public void RoundTrip_WithoutStorages_ReadsAnEmptyList()
        {
            NetDataWriter writer = new NetDataWriter();
            new MeshInteractionState { dialogDataId = 3 }.Write(writer);
            MeshInteractionState read = MeshInteractionState.Read(new NetDataReader(writer.CopyData()));
            Assert.AreEqual(3, read.dialogDataId);
            Assert.IsNotNull(read.storages);
            Assert.AreEqual(0, read.storages.Count);
        }
    }

    public class MeshServerStorageHandlersTests
    {
        /// <summary>The kit's storage handler as these tests need it: it counts what it is asked and accepts every open.</summary>
        private sealed class FakeKit : IServerStorageHandlers
        {
            public int Opens, Closes, CloseAlls, Notifies, Clears;
            public readonly List<StorageId> Closed = new List<StorageId>();
            public bool Accept = true;

            public IDictionary<StorageId, List<CharacterItem>> GetAllStorageItems() => new Dictionary<StorageId, List<CharacterItem>>();
            public void OpenStorage(long connectionId, IPlayerCharacterData playerCharacter, IActivatableEntity storageEntity, StorageId storageId) { Opens++; }
            public void CloseStorage(long connectionId, StorageId storageId) { Closes++; Closed.Add(storageId); }
            public void CloseAllStorages(long connectionId) { CloseAlls++; }
            public UniTask<bool> ConvertStorageItems(StorageId storageId, List<StorageConvertItemsEntry> convertItems, List<CharacterItem> droppingItems) => UniTask.FromResult(true);
            public List<CharacterItem> GetStorageItems(StorageId storageId) => new List<CharacterItem>();
            public void SetStorageItems(StorageId storageId, List<CharacterItem> items) { }
            public bool IsStorageEntityOpen(StorageEntity storageEntity) => false;
            public List<CharacterItem> GetStorageEntityItems(StorageEntity storageEntity) => new List<CharacterItem>();
            public Storage GetStorage(StorageId storageId, out uint objectId) { objectId = 0; return default; }
            public bool CanAccessStorage(IPlayerCharacterData playerCharacter, IActivatableEntity storageEntity, StorageId storageId, out UITextKeys uiTextKeys) { uiTextKeys = UITextKeys.NONE; return Accept; }
            public void ClearStorage() { Clears++; }
            public void NotifyStorageItemsUpdated(StorageType storageType, string storageOwnerId) { Notifies++; }
            public bool WillProceedStorageSaving(StorageType storageType, string storageOwnerId) => false;
        }

        private static readonly StorageId Player = new StorageId(StorageType.Player, "user-1");
        private static readonly StorageId Guild = new StorageId(StorageType.Guild, "12");
        private static readonly StorageId Chest = new StorageId(StorageType.Building, "chest-1");

        [Test]
        public void ItemsUpdated_ForAStorageNobodyOpenedHere_IsNotPassedOn()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);

            mesh.NotifyStorageItemsUpdated(StorageType.Player, "user-1");
            Assert.AreEqual(0, kit.Notifies);
            Assert.AreEqual(1, mesh.Guarded);

            mesh.OpenStorage(1, null, null, Player);
            mesh.NotifyStorageItemsUpdated(StorageType.Player, "user-1");
            Assert.AreEqual(1, kit.Notifies);
            Assert.AreEqual(1, mesh.Guarded);
        }

        [Test]
        public void ARefusedOpen_IsNotCountedAsOpen()
        {
            FakeKit kit = new FakeKit { Accept = false };
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.OpenStorage(1, null, null, Player);
            Assert.AreEqual(1, kit.Opens, "the kit still answers the client");
            mesh.NotifyStorageItemsUpdated(StorageType.Player, "user-1");
            Assert.AreEqual(0, kit.Notifies);
        }

        [Test]
        public void CloseAll_OutsideAHandoff_ClosesInTheKitAndCarriesNothing()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.OpenStorage(1, null, null, Player);

            mesh.CloseAllStorages(1);

            Assert.AreEqual(1, kit.CloseAlls);
            Assert.IsNull(mesh.TakeCarried(1));
        }

        [Test]
        public void CloseAll_InAHandoff_CarriesPlayerAndGuild_ClosesAChest_AndLeavesTheKitsClose()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.OpenStorage(1, null, null, Player);
            mesh.OpenStorage(1, null, null, Guild);
            mesh.OpenStorage(1, null, null, Chest);
            mesh.OpenStorage(2, null, null, Player); // another connection, untouched

            mesh.BeginHandoff(1);
            mesh.CloseAllStorages(1);

            Assert.AreEqual(0, kit.CloseAlls, "the kit's own sweep closes the handed-off connection's entries");
            Assert.AreEqual(1, kit.Closes);
            Assert.AreEqual(Chest, kit.Closed[0]);
            List<MeshInteractionState.CarriedStorage> carried = mesh.TakeCarried(1);
            Assert.IsNotNull(carried);
            Assert.AreEqual(2, carried.Count);
            Assert.AreEqual(StorageType.Player, carried[0].type);
            Assert.AreEqual("user-1", carried[0].ownerId);
            Assert.AreEqual(StorageType.Guild, carried[1].type);
            Assert.IsNull(mesh.TakeCarried(1), "taken once");

            mesh.CloseAllStorages(2);
            Assert.AreEqual(1, kit.CloseAlls, "the other connection closes as before");
        }

        [Test]
        public void CloseAll_InAHandoff_WithNothingOpen_CarriesNothing()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.BeginHandoff(1);
            mesh.CloseAllStorages(1);
            Assert.AreEqual(0, kit.CloseAlls);
            Assert.IsNull(mesh.TakeCarried(1));
            mesh.CloseAllStorages(1);
            Assert.AreEqual(1, kit.CloseAlls, "the handoff mark is spent");
        }

        [Test]
        public void ClosingOneStorage_ForgetsOnlyThatOne()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.OpenStorage(1, null, null, Player);
            mesh.OpenStorage(1, null, null, Guild);
            mesh.CloseStorage(1, Guild);
            mesh.BeginHandoff(1);
            mesh.CloseAllStorages(1);
            List<MeshInteractionState.CarriedStorage> carried = mesh.TakeCarried(1);
            Assert.AreEqual(1, carried.Count);
            Assert.AreEqual(StorageType.Player, carried[0].type);
        }

        [Test]
        public void Clear_ForgetsEverything()
        {
            FakeKit kit = new FakeKit();
            MeshServerStorageHandlers mesh = new MeshServerStorageHandlers(kit);
            mesh.OpenStorage(1, null, null, Player);
            mesh.ClearStorage();
            Assert.AreEqual(1, kit.Clears);
            mesh.NotifyStorageItemsUpdated(StorageType.Player, "user-1");
            Assert.AreEqual(0, kit.Notifies);
        }
    }
}
