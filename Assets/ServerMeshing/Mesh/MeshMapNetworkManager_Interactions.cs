// Server Meshing add-on — MeshMapNetworkManager: a player's interactions across a hop (DESIGN.md 1.37).
//
// The kit's chat and storage handlers are wrapped as the server's handler references are set (MeshInteractionHandlers.cs),
// and a storage the arrival had open on the origin is reopened here once both its carried state is read and its relates
// data is loaded, in whichever order those come.
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

        /// <summary>The kit's handlers, wrapped: a local chat reaches the players across a line, and storage opens are known to the handoff.</summary>
        protected override void SetServerHandlersRef()
        {
            if (ServerChatHandlers != null && !(ServerChatHandlers is MeshServerChatHandlers))
                ServerChatHandlers = new MeshServerChatHandlers(ServerChatHandlers, this);
            if (ServerStorageHandlers != null && !(ServerStorageHandlers is MeshServerStorageHandlers))
                ServerStorageHandlers = _storageWrapper = new MeshServerStorageHandlers(ServerStorageHandlers);
            base.SetServerHandlersRef();
        }

        /// <summary>Right before the crossing character is unregistered: its open storages go with the handoff instead of closing.</summary>
        private void BeginStorageHandoff(long connectionId)
        {
            _storageWrapper?.BeginHandoff(connectionId);
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
        private void BeginStorageHandoff(long connectionId) { }
        internal void QueueStorageReopen(BasePlayerCharacterEntity player, List<MeshInteractionState.CarriedStorage> storages) { }
        private void MarkArrivalLoaded(BasePlayerCharacterEntity player) { }
#endif
    }
}
