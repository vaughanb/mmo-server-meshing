// Server Meshing add-on — what a player is doing with the world when it crosses a line (DESIGN.md 1.37).
//
// Three things the kit holds about a player on its map server outside the character data, and so outside the handoff:
//  * a local chat goes to the characters within localChatDistance by their connection, and a puppet has none, so two
//    players a few metres apart across a line could not hear each other. MeshServerChatHandlers forwards it to each
//    puppet's owner as a GhostAction, and that cell delivers it to the real character as the kit would;
//  * the NPC dialog a player has open (PlayerCharacterNpcActionComponent.CurrentNpcDialog) was null on the destination,
//    so the dialog stayed on screen with buttons that did nothing;
//  * the storages a player has open were closed at the hop by the kit's UnregisterPlayerCharacter, and the window shut.
// MeshInteractionCarrier carries the dialog and the open storages (player and guild, the kinds the database holds) with
// the handoff; the destination restores the dialog as the arrival registers and reopens the storages once its relates
// data is loaded. MeshServerStorageHandlers wraps the kit's storage handlers to know what a connection has open, to leave
// the close to the kit's own sweep during a handoff (so the client never sees one), and to make NotifyStorageItemsUpdated
// safe for a storage nobody has opened on this cell, where the kit indexes a dictionary and throws.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshInteractionState is what a crossing player had open: an NPC dialog and storages, each by its NPC's place in the scene.</summary>
    public struct MeshInteractionState
    {
        public int dialogDataId;
        public int afterRewardDialogDataId;
        public int completingQuestDataId;
        public bool hasNpc;
        public Vector3 npcPosition;
        public int npcAssetHash;
        public List<CarriedStorage> storages;

        public struct CarriedStorage
        {
            public StorageType type;
            public string ownerId;
            public bool hasEntity;
            public Vector3 entityPosition;
        }

        public bool IsEmpty => dialogDataId == 0 && afterRewardDialogDataId == 0 && (storages == null || storages.Count == 0);

        public void Write(NetDataWriter writer)
        {
            writer.Put(dialogDataId);
            writer.Put(afterRewardDialogDataId);
            writer.Put(completingQuestDataId);
            writer.Put(hasNpc);
            writer.Put(npcPosition.x); writer.Put(npcPosition.y); writer.Put(npcPosition.z);
            writer.Put(npcAssetHash);
            int count = storages != null ? storages.Count : 0;
            writer.Put(count);
            for (int i = 0; i < count; ++i)
            {
                writer.Put((byte)storages[i].type);
                writer.Put(storages[i].ownerId ?? string.Empty);
                writer.Put(storages[i].hasEntity);
                writer.Put(storages[i].entityPosition.x); writer.Put(storages[i].entityPosition.y); writer.Put(storages[i].entityPosition.z);
            }
        }

        public static MeshInteractionState Read(NetDataReader reader)
        {
            MeshInteractionState state = new MeshInteractionState();
            state.dialogDataId = reader.GetInt();
            state.afterRewardDialogDataId = reader.GetInt();
            state.completingQuestDataId = reader.GetInt();
            state.hasNpc = reader.GetBool();
            state.npcPosition = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            state.npcAssetHash = reader.GetInt();
            int count = reader.GetInt();
            state.storages = new List<CarriedStorage>(count);
            for (int i = 0; i < count; ++i)
            {
                CarriedStorage carried = new CarriedStorage();
                carried.type = (StorageType)reader.GetByte();
                carried.ownerId = reader.GetString();
                carried.hasEntity = reader.GetBool();
                carried.entityPosition = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
                state.storages.Add(carried);
            }
            return state;
        }
    }

    /// <summary>MeshInteractionCarrier carries MeshInteractionState with the handoff, for every player (MeshHandoffCarriers.BuiltIn).</summary>
    public sealed class MeshInteractionCarrier : IMeshHandoffCarrier
    {
        public const string Key = "mesh.interaction";
        /// <summary>How far the destination's copy of an NPC may stand from where the origin's stood: the same scene, so a metre is generous.</summary>
        public const float NpcMatchDistance = 1f;

        public string CarrierKey => Key;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            MeshInteractionState state = Collect(player);
            if (state.IsEmpty)
                return false;
            state.Write(writer);
            return true;
        }

        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            Restore(player, MeshInteractionState.Read(reader));
        }

        /// <summary>Origin: the dialog the player has open, and the storages the storage wrapper set aside for this handoff.</summary>
        public static MeshInteractionState Collect(BasePlayerCharacterEntity player)
        {
            MeshInteractionState state = new MeshInteractionState();
            PlayerCharacterNpcActionComponent npc = player != null ? player.NpcActionComponent : null;
            if (npc != null)
            {
                state.dialogDataId = npc.CurrentNpcDialog != null ? npc.CurrentNpcDialog.DataId : 0;
                state.afterRewardDialogDataId = npc.NpcDialogAfterSelectRewardItem != null ? npc.NpcDialogAfterSelectRewardItem.DataId : 0;
                state.completingQuestDataId = npc.CompletingQuest != null ? npc.CompletingQuest.DataId : 0;
                NpcEntity entity = npc.CurrentNpcEntity != null ? npc.CurrentNpcEntity : npc.NpcEntityAfterSelectRewardItem;
                if (entity != null)
                {
                    state.hasNpc = true;
                    state.npcPosition = entity.EntityTransform.position;
                    state.npcAssetHash = entity.Identity != null ? entity.Identity.HashAssetId : 0;
                }
            }
            if (player != null && GameInstance.ServerStorageHandlers is MeshServerStorageHandlers storage)
                state.storages = storage.TakeCarried(player.ConnectionId);
            return state;
        }

        /// <summary>
        /// Destination, as the arrival registers: the dialog back on the player, on this cell's copy of its NPC, and the
        /// storages queued for the manager to reopen once the arrival's data is loaded.
        /// </summary>
        public static void Restore(BasePlayerCharacterEntity player, MeshInteractionState state)
        {
            if (player == null)
                return;
            PlayerCharacterNpcActionComponent npc = player.NpcActionComponent;
            if (npc != null && (state.dialogDataId != 0 || state.afterRewardDialogDataId != 0))
            {
                NpcEntity entity = state.hasNpc ? FindNpc(state.npcPosition, state.npcAssetHash) : null;
                if (state.hasNpc && entity == null)
                    Logging.LogWarning("Mesh", $"Arrival {player.CharacterName}: no NPC at {state.npcPosition} here; its dialog is dropped");
                else
                {
                    if (state.dialogDataId != 0 && GameInstance.NpcDialogs.TryGetValue(state.dialogDataId, out BaseNpcDialog dialog))
                    {
                        npc.CurrentNpcEntity = entity;
                        npc.CurrentNpcDialog = dialog;
                    }
                    if (state.afterRewardDialogDataId != 0 && GameInstance.NpcDialogs.TryGetValue(state.afterRewardDialogDataId, out BaseNpcDialog after))
                    {
                        npc.NpcEntityAfterSelectRewardItem = entity;
                        npc.NpcDialogAfterSelectRewardItem = after;
                    }
                    if (state.completingQuestDataId != 0 && GameInstance.Quests.TryGetValue(state.completingQuestDataId, out Quest quest))
                        npc.CompletingQuest = quest;
                }
            }
            if (state.storages != null && state.storages.Count > 0 && BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh)
                mesh.QueueStorageReopen(player, state.storages);
        }

        /// <summary>The NPC standing where the origin's stood: the cells load the same scene, so the copy is at the same place.</summary>
        public static NpcEntity FindNpc(Vector3 position, int assetHash)
        {
            NpcEntity best = null;
            float bestDistance = NpcMatchDistance * NpcMatchDistance;
            foreach (NpcEntity npc in UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None))
            {
                if (assetHash != 0 && npc.Identity != null && npc.Identity.HashAssetId != assetHash)
                    continue;
                float distance = (npc.EntityTransform.position - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = npc;
                }
            }
            return best;
        }
#else
        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer) => false;
        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader) { }
#endif
    }

    /// <summary>
    /// MeshServerChatHandlers is the kit's chat handler with one addition: a local chat also reaches the players standing
    /// within range across a line, who are puppets here. The kit sends to each receiver by connection, which a puppet has
    /// none of, so the message goes to the puppet's owner as a GhostAction and that cell delivers it to the real character.
    /// </summary>
    public sealed class MeshServerChatHandlers : IServerChatHandlers
    {
        private readonly IServerChatHandlers _kit;
        private readonly MeshMapNetworkManager _manager;
        private readonly List<BasePlayerCharacterEntity> _receivers = new List<BasePlayerCharacterEntity>();

        public MeshServerChatHandlers(IServerChatHandlers kit, MeshMapNetworkManager manager)
        {
            _kit = kit;
            _manager = manager;
        }

        public IServerChatHandlers Kit => _kit;

        public bool CanSendSystemAnnounce(string senderId) => _kit.CanSendSystemAnnounce(senderId);
        public bool ChatTooFast(string senderId) => _kit.ChatTooFast(senderId);
        public void ChatFlooded(string senderId) => _kit.ChatFlooded(senderId);

        public void OnChatMessage(ChatMessage message)
        {
            _kit.OnChatMessage(message);
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            ForwardLocalToGhosts(message);
#endif
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void ForwardLocalToGhosts(ChatMessage message)
        {
            if (message.channel != ChatChannel.Local || message.sendByServer || _manager == null || !_manager.IsServer || string.IsNullOrEmpty(message.senderName))
                return;
            if (!GameInstance.ServerUserHandlers.TryGetPlayerCharacterByName(message.senderName, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity sender))
                return;
            if (GameInstance.Singleton.GMCommands.IsGMCommand(message.message, out _))
                return; // the kit answers a command to its sender alone
            message.timestamp = BaseGameNetworkManager.Singleton.ServerTimestamp;
            _receivers.Clear();
            sender.FindEntities(_receivers, GameInstance.Singleton.localChatDistance, false, true, true, true, GameInstance.Singleton.playerLayer.Mask | GameInstance.Singleton.playingLayer.Mask);
            EntityInfo instigator = sender.GetInfo();
            int forwarded = 0;
            for (int i = 0; i < _receivers.Count; ++i)
            {
                BasePlayerCharacterEntity receiver = _receivers[i];
                if (receiver == null || !MeshIdentity.IsGhostEntity(receiver))
                    continue;
                ChatMessage copy = message;
                if (_manager.ForwardGhostAction(receiver, GhostActionKind.Chat, instigator, writer => writer.Put(copy)))
                    forwarded++;
            }
            _receivers.Clear();
            if (forwarded > 0)
                Logging.Log("Mesh", $"Local chat from {message.senderName} forwarded to {forwarded} player(s) across a line");
        }
#endif
    }

    /// <summary>
    /// MeshServerStorageHandlers is the kit's storage handler with the bookkeeping a hop needs: what each connection has
    /// open, so the handoff can carry it; a handoff's close left to the kit's own sweep, so the client's window stays; and
    /// NotifyStorageItemsUpdated made safe for a storage nobody opened on this cell.
    /// </summary>
    public sealed class MeshServerStorageHandlers : IServerStorageHandlers
    {
        private struct Open
        {
            public StorageId id;
            public IActivatableEntity entity;
        }

        private readonly IServerStorageHandlers _kit;
        private readonly Dictionary<long, List<Open>> _open = new Dictionary<long, List<Open>>();
        private readonly HashSet<StorageId> _everOpened = new HashSet<StorageId>();
        private readonly Dictionary<long, List<MeshInteractionState.CarriedStorage>> _carried = new Dictionary<long, List<MeshInteractionState.CarriedStorage>>();
        private readonly HashSet<long> _handingOff = new HashSet<long>();

        public MeshServerStorageHandlers(IServerStorageHandlers kit)
        {
            _kit = kit;
        }

        public IServerStorageHandlers Kit => _kit;
        /// <summary>How many item updates were for a storage nobody opened here: each would have thrown in the kit.</summary>
        public int Guarded { get; private set; }

        /// <summary>The mesh, right before it unregisters a crossing character: the next CloseAllStorages for this connection is the handoff's.</summary>
        public void BeginHandoff(long connectionId)
        {
            _handingOff.Add(connectionId);
        }

        /// <summary>The carrier, as the handoff state is written: the player and guild storages the connection had open, once; null for none.</summary>
        public List<MeshInteractionState.CarriedStorage> TakeCarried(long connectionId)
        {
            _handingOff.Remove(connectionId);
            if (!_carried.TryGetValue(connectionId, out List<MeshInteractionState.CarriedStorage> list))
                return null;
            _carried.Remove(connectionId);
            return list;
        }

        public void OpenStorage(long connectionId, IPlayerCharacterData playerCharacter, IActivatableEntity storageEntity, StorageId storageId)
        {
            bool accepted = _kit.CanAccessStorage(playerCharacter, storageEntity, storageId, out _);
            _kit.OpenStorage(connectionId, playerCharacter, storageEntity, storageId);
            if (!accepted)
                return;
            _everOpened.Add(storageId);
            if (!_open.TryGetValue(connectionId, out List<Open> list))
                _open[connectionId] = list = new List<Open>();
            for (int i = 0; i < list.Count; ++i)
            {
                if (list[i].id.Equals(storageId))
                {
                    list[i] = new Open { id = storageId, entity = storageEntity };
                    return;
                }
            }
            list.Add(new Open { id = storageId, entity = storageEntity });
        }

        public void CloseStorage(long connectionId, StorageId storageId)
        {
            _kit.CloseStorage(connectionId, storageId);
            if (!_open.TryGetValue(connectionId, out List<Open> list))
                return;
            for (int i = list.Count - 1; i >= 0; --i)
                if (list[i].id.Equals(storageId))
                    list.RemoveAt(i);
        }

        public void CloseAllStorages(long connectionId)
        {
            if (_handingOff.Remove(connectionId))
            {
                // The handoff: what the player has open goes with it (TakeCarried) and the client is told nothing, so its
                // window stays for the destination to fill. A chest belongs to the cell it stands on, so that one closes
                // as the kit closes it, window and all. The kit's own sweep closes this connection's entries within a
                // second, once the character is unregistered.
                if (_open.TryGetValue(connectionId, out List<Open> list))
                {
                    List<MeshInteractionState.CarriedStorage> carried = new List<MeshInteractionState.CarriedStorage>();
                    for (int i = 0; i < list.Count; ++i)
                    {
                        if (list[i].id.storageType == StorageType.Building)
                        {
                            _kit.CloseStorage(connectionId, list[i].id);
                            continue;
                        }
                        Component entity = list[i].entity as Component;
                        carried.Add(new MeshInteractionState.CarriedStorage
                        {
                            type = list[i].id.storageType, ownerId = list[i].id.storageOwnerId,
                            hasEntity = entity != null, entityPosition = entity != null ? entity.transform.position : Vector3.zero,
                        });
                    }
                    if (carried.Count > 0)
                        _carried[connectionId] = carried;
                    _open.Remove(connectionId);
                }
                return;
            }
            _kit.CloseAllStorages(connectionId);
            _open.Remove(connectionId);
            _carried.Remove(connectionId);
        }

        public void NotifyStorageItemsUpdated(StorageType storageType, string storageOwnerId)
        {
            if (!_everOpened.Contains(new StorageId(storageType, storageOwnerId)))
            {
                // The kit indexes its open-clients table here and throws for a storage nobody has opened on this cell,
                // which leaves the mover in its storage users for good. Nobody is looking at it: nothing to notify.
                Guarded++;
                return;
            }
            _kit.NotifyStorageItemsUpdated(storageType, storageOwnerId);
        }

        public void ClearStorage()
        {
            _kit.ClearStorage();
            _open.Clear();
            _everOpened.Clear();
            _carried.Clear();
            _handingOff.Clear();
        }

        public IDictionary<StorageId, List<CharacterItem>> GetAllStorageItems() => _kit.GetAllStorageItems();
        public UniTask<bool> ConvertStorageItems(StorageId storageId, List<StorageConvertItemsEntry> convertItems, List<CharacterItem> droppingItems) => _kit.ConvertStorageItems(storageId, convertItems, droppingItems);
        public List<CharacterItem> GetStorageItems(StorageId storageId) => _kit.GetStorageItems(storageId);
        public void SetStorageItems(StorageId storageId, List<CharacterItem> items) => _kit.SetStorageItems(storageId, items);
        public bool IsStorageEntityOpen(StorageEntity storageEntity) => _kit.IsStorageEntityOpen(storageEntity);
        public List<CharacterItem> GetStorageEntityItems(StorageEntity storageEntity) => _kit.GetStorageEntityItems(storageEntity);
        public Storage GetStorage(StorageId storageId, out uint objectId) => _kit.GetStorage(storageId, out objectId);
        public bool CanAccessStorage(IPlayerCharacterData playerCharacter, IActivatableEntity storageEntity, StorageId storageId, out UITextKeys uiTextKeys) => _kit.CanAccessStorage(playerCharacter, storageEntity, storageId, out uiTextKeys);
        public bool WillProceedStorageSaving(StorageType storageType, string storageOwnerId) => _kit.WillProceedStorageSaving(storageType, storageOwnerId);
    }
}
