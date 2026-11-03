// Server Meshing add-on — MeshMapNetworkManager: a player's interactions across a hop (DESIGN.md 1.37).
//
// The kit's chat and storage handlers are wrapped as the server's handler references are set (MeshInteractionHandlers.cs),
// and a storage the arrival had open on the origin is reopened here once both its carried state is read and its relates
// data is loaded, in whichever order those come. A chest does not cross (DESIGN 1.57): its storage is served by the cell
// it stands on, so the boundary tick holds a player who has one open in reach on this cell, and closes one out of reach
// the moment it sees it, by the kit's own rule, so what the wrapper counts open is what the kit has open.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long an arrival's loaded mark is kept for a carried storage that never comes.</summary>
        private const float ArrivalLoadedKeepSeconds = 60f;

        private MeshServerStorageHandlers _storageWrapper;
        private readonly Dictionary<string, List<MeshInteractionState.CarriedStorage>> _storageReopens = new Dictionary<string, List<MeshInteractionState.CarriedStorage>>();
        private readonly Dictionary<string, float> _arrivalsLoaded = new Dictionary<string, float>();
        private readonly List<string> _arrivalsLoadedGone = new List<string>();
        private readonly HashSet<string> _chestHoldLogged = new HashSet<string>();

        /// <summary>The kit's handlers, wrapped: a local chat reaches the players across a line, storage opens are known to the handoff, and a party or guild invitation reaches a player across a line (DESIGN 1.44).</summary>
        protected override void SetServerHandlersRef()
        {
            if (ServerChatHandlers != null && !(ServerChatHandlers is MeshServerChatHandlers))
                ServerChatHandlers = new MeshServerChatHandlers(ServerChatHandlers, this);
            if (ServerPartyMessageHandlers != null && !(ServerPartyMessageHandlers is MeshServerPartyMessageHandlers))
                ServerPartyMessageHandlers = new MeshServerPartyMessageHandlers(ServerPartyMessageHandlers, this);
            if (ServerGuildMessageHandlers != null && !(ServerGuildMessageHandlers is MeshServerGuildMessageHandlers))
                ServerGuildMessageHandlers = new MeshServerGuildMessageHandlers(ServerGuildMessageHandlers, this);
            // The kit registered its request handlers as delegates when it registered its messages, before this ran: the
            // two the wrappers change are registered again, so a request reaches the wrapper (the chat and storage
            // handlers are reached through the property at call time and need no such thing).
            if (ServerPartyMessageHandlers is MeshServerPartyMessageHandlers)
                RegisterRequestToServer<RequestSendPartyInvitationMessage, ResponseSendPartyInvitationMessage>(GameNetworkingConsts.SendPartyInvitation, ServerPartyMessageHandlers.HandleRequestSendPartyInvitation);
            if (ServerGuildMessageHandlers is MeshServerGuildMessageHandlers)
                RegisterRequestToServer<RequestSendGuildInvitationMessage, ResponseSendGuildInvitationMessage>(GameNetworkingConsts.SendGuildInvitation, ServerGuildMessageHandlers.HandleRequestSendGuildInvitation);
            if (ServerStorageHandlers != null && !(ServerStorageHandlers is MeshServerStorageHandlers))
                ServerStorageHandlers = _storageWrapper = new MeshServerStorageHandlers(ServerStorageHandlers);
            base.SetServerHandlersRef();
        }

        /// <summary>Right before the crossing character is unregistered: its open storages go with the handoff instead of closing.</summary>
        private void BeginStorageHandoff(long connectionId)
        {
            _storageWrapper?.BeginHandoff(connectionId);
        }

        /// <summary>
        /// The boundary tick, for every player here: whether it has a chest open within the chest's reach. One out of reach
        /// (or gone) is closed now, as the kit's once-a-second sweep would close it (the same test on the same positions),
        /// because that sweep closes inside the kit, past the wrapper, which would go on counting the chest open.
        /// </summary>
        private bool ChestOpenInReach(BasePlayerCharacterEntity player)
        {
            if (_storageWrapper == null || !_storageWrapper.TryGetOpenChest(player.ConnectionId, out StorageId storageId, out IActivatableEntity chest))
                return false;
            if (!chest.IsNull() && GameplayUtils.IsTargetInDistance(player.EntityTransform.position, chest.EntityTransform, chest.GetActivatableDistance()))
                return true;
            _storageWrapper.CloseStorage(player.ConnectionId, storageId);
            Logging.Log("Mesh", $"Chest {storageId.storageOwnerId} closed for {player.CharacterName}: {(chest.IsNull() ? "gone" : "out of reach")}, as the kit closes it");
            return false;
        }

        /// <summary>
        /// The boundary tick holds the handoff of a player past the line with a chest open in reach (DESIGN 1.57): the chest's
        /// storage is served here and its puppet next door cannot open it, so the player stays until it closes the chest or
        /// walks out of the chest's reach. A draining cell holds nobody: its handoff closes the chest as before.
        /// </summary>
        private bool HoldsHandoffForChest(BasePlayerCharacterEntity player, bool chestOpenInReach)
        {
            if (!chestOpenInReach || IsDraining)
            {
                if (_chestHoldLogged.Remove(player.Id))
                    Logging.Log("Mesh", $"Handoff of {player.CharacterName} no longer held for a chest");
                return false;
            }
            if (_chestHoldLogged.Add(player.Id) && _storageWrapper.TryGetOpenChest(player.ConnectionId, out StorageId storageId, out IActivatableEntity chest))
                Logging.Log("Mesh", $"Handoff of {player.CharacterName} held: chest {storageId.storageOwnerId} is open here (until it closes or the player is {chest.GetActivatableDistance():0.#} m from it)");
            return true;
        }

        /// <summary>The carrier, as the arrival registers: the storages it had open, to reopen once its data is in.</summary>
        internal void QueueStorageReopen(BasePlayerCharacterEntity player, List<MeshInteractionState.CarriedStorage> storages)
        {
            if (player == null || storages == null || storages.Count == 0)
                return;
            _storageReopens[player.Id] = storages;
            TryReopenCarriedStorages(player).Forget();
        }

        /// <summary>The arrival's relates data is loaded, its player storage among it: a carried storage can be reopened now.</summary>
        private void MarkArrivalLoaded(BasePlayerCharacterEntity player)
        {
            if (player == null)
                return;
            float now = Time.unscaledTime;
            _arrivalsLoadedGone.Clear();
            foreach (KeyValuePair<string, float> kv in _arrivalsLoaded)
                if (now - kv.Value > ArrivalLoadedKeepSeconds)
                    _arrivalsLoadedGone.Add(kv.Key);
            for (int i = 0; i < _arrivalsLoadedGone.Count; ++i)
                _arrivalsLoaded.Remove(_arrivalsLoadedGone[i]);
            _arrivalsLoaded[player.Id] = now;
            TryReopenCarriedStorages(player).Forget();
        }

        private async UniTaskVoid TryReopenCarriedStorages(BasePlayerCharacterEntity player)
        {
            string id = player.Id;
            if (!_arrivalsLoaded.ContainsKey(id) || !_storageReopens.TryGetValue(id, out List<MeshInteractionState.CarriedStorage> storages))
                return;
            _storageReopens.Remove(id);
            _arrivalsLoaded.Remove(id);
            long connectionId = player.ConnectionId;
            for (int i = 0; i < storages.Count; ++i)
            {
                MeshInteractionState.CarriedStorage carried = storages[i];
                if (!carried.hasEntity)
                    continue; // the kit opens a storage against the entity it was opened at
                StorageId storageId = new StorageId(carried.type, carried.ownerId);
                if (carried.type == StorageType.Guild)
                    await LoadStorageRoutine(storageId); // the player's own is loaded with the arrival; a guild's on demand
                if (player == null || !ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData held) || !ReferenceEquals(held, player))
                    return; // left meanwhile
                NpcEntity entity = MeshInteractionCarrier.FindNpc(carried.entityPosition, 0);
                if (entity == null)
                {
                    Logging.LogWarning("Mesh", $"Arrival {player.CharacterName}: no NPC at {carried.entityPosition} here; its {carried.type} storage stays closed");
                    continue;
                }
                ServerStorageHandlers.OpenStorage(connectionId, player, entity, storageId);
                Stamp($"arrival {player.CharacterName}: {carried.type} storage reopened");
            }
        }
#else
        private bool ChestOpenInReach(BasePlayerCharacterEntity player) { return false; }
        private bool HoldsHandoffForChest(BasePlayerCharacterEntity player, bool chestOpenInReach) { return false; }
        private void BeginStorageHandoff(long connectionId) { }
        internal void QueueStorageReopen(BasePlayerCharacterEntity player, List<MeshInteractionState.CarriedStorage> storages) { }
        private void MarkArrivalLoaded(BasePlayerCharacterEntity player) { }
#endif
    }
}
