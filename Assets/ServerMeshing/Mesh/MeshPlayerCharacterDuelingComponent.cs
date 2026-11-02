// Server Meshing add-on — a duel across a line (DESIGN.md 1.44).
//
// The kit's duel is two components on one server pointing at each other: a request sets both DuelingCharacter fields,
// an accept starts both timers and tells both clients with a target RPC, a death or a timeout ends both. Across a line
// the other duelist is a puppet here, and a target RPC at a puppet goes nowhere. This subclass is swapped onto the
// player prefabs in place of the kit's component (the setup window and the demo copy builder do it) and declares the
// kit's server RPCs again under the same names, so the client's unchanged calls reach these. Each keeps the kit's checks
// and its local bookkeeping (the puppet's component mirrors the other duelist's state here, which is what the map's
// "duelers may hit each other" rule reads) and forwards what the other side's owner must do as a GhostAction
// (MeshMapNetworkManager_Social): the request, the accept with its durations, a decline, and the end when this side's
// duelist dies or leaves. Timeouts and safe-area ends happen on each cell by its own clock, the mirror's included.
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshPlayerCharacterDuelingComponent : PlayerCharacterDuelingComponent
    {
        private static MeshMapNetworkManager Mesh => BaseGameNetworkManager.Singleton as MeshMapNetworkManager;

        /// <summary>The kit's StartDueling, reachable on another instance of this class (it is protected).</summary>
        internal void StartDuelingHere(float countDownDuration, float duelDuration)
        {
            StartDueling(countDownDuration, duelDuration);
        }

        [ServerRpc]
        protected new void CmdSendDuelingRequest(uint objectId)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            Logging.Log("Mesh", $"CmdSendDuelingRequest on {Entity.CharacterName} (object {ObjectId}, conn {ConnectionId}, ghost {MeshIdentity.IsGhostEntity(Entity)}) for object {objectId}");
            if (DisableDueling)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_FEATURE_IS_DISABLED);
                return;
            }
            if (!Manager.TryGetEntityByObjectId(objectId, out BasePlayerCharacterEntity target))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_NOT_FOUND);
                return;
            }
            if (target.DuelingComponent.DuelingCharacter != null)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_DUELING);
                return;
            }
            if (!Entity.IsGameEntityInDistance(target))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                return;
            }
            if (target.IsInSafeArea)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_IN_SAFE_AREA);
                return;
            }
            DuelingCharacter = target;
            target.DuelingComponent.DuelingCharacter = Entity;
            if (MeshIdentity.IsGhostEntity(target) && Mesh != null)
            {
                // The other duelist lives on another cell: its owner sets the pair up there and asks the real player.
                Mesh.ForwardGhostAction(target, GhostActionKind.DuelRequest, Entity.GetInfo(), null);
                Logging.Log("Mesh", $"Duel request {Entity.CharacterName} -> {target.CharacterName} across a line, sent to its owner");
                return;
            }
            DuelingCharacter.DuelingComponent.CallOwnerReceiveDuelingRequest(ObjectId);
#endif
        }

        [ServerRpc]
        protected new void CmdAcceptDuelingRequest()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DuelingCharacter;
            if (other == null)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CANNOT_ACCEPT_DUELING_REQUEST);
                StopDueling();
                return;
            }
            if (!Entity.IsGameEntityInDistance(other))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                StopDueling();
                return;
            }
            if (Entity.IsInSafeArea)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_IN_SAFE_AREA);
                StopDueling();
                return;
            }
            float countDownDuration = CurrentGameInstance.duelingCountDownDuration;
            float duelDuration = CurrentGameInstance.duelingDuration;
            // The other duelist's component here (the mirror, when it is a puppet): the same timers, so the map's rule
            // and the end-by-time read the same on both cells.
            other.DuelingComponent.ClearDuelingData();
            if (other.DuelingComponent is MeshPlayerCharacterDuelingComponent otherMesh)
                otherMesh.StartDuelingHere(countDownDuration, duelDuration);
            if (MeshIdentity.IsGhostEntity(other) && Mesh != null)
            {
                Mesh.ForwardGhostAction(other, GhostActionKind.DuelAccepted, Entity.GetInfo(), writer => { writer.Put(countDownDuration); writer.Put(duelDuration); });
                Logging.Log("Mesh", $"Duel accepted {Entity.CharacterName} <-> {other.CharacterName} across a line ({countDownDuration:0}+{duelDuration:0} s), sent to its owner");
            }
            else
            {
                other.DuelingComponent.CallOwnerAcceptedDuelingRequest(ObjectId, countDownDuration, duelDuration);
            }
            ClearDuelingData();
            StartDueling(countDownDuration, duelDuration);
            CallOwnerAcceptedDuelingRequest(other.ObjectId, countDownDuration, duelDuration);
#endif
        }

        [ServerRpc]
        protected new void CmdDeclineDuelingRequest()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DuelingCharacter;
            if (other != null)
            {
                if (MeshIdentity.IsGhostEntity(other) && Mesh != null)
                    Mesh.ForwardGhostAction(other, GhostActionKind.DuelEnded, Entity.GetInfo(), writer => { writer.Put(true); writer.Put(false); });
                else
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_DUELING_REQUEST_DECLINED);
            }
            GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_DUELING_REQUEST_DECLINED);
            StopDueling();
#endif
        }

        /// <summary>
        /// This duelist's side is over on this cell (a death, a hop, a disconnect): the other duelist's owner ends it
        /// there with this one as the loser. The kit's own EndDueling runs after and clears the mirror here.
        /// </summary>
        internal void ForwardEndAsLoser(string why)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (!IsServer || !DuelingStartingOrStarted)
                return;
            BasePlayerCharacterEntity other = DuelingCharacter;
            if (other == null || !MeshIdentity.IsGhostEntity(other) || Mesh == null)
                return;
            Mesh.ForwardGhostAction(other, GhostActionKind.DuelEnded, Entity.GetInfo(), writer => { writer.Put(false); writer.Put(true); });
            Logging.Log("Mesh", $"Duel ended by {Entity.CharacterName} ({why}) across a line, {other.CharacterName}'s owner told");
#endif
        }

        protected override void OnDestroy()
        {
            // A hop or a disconnect mid-duel: the kit's OnDestroy ends it here as a loss; the other side hears the same.
            if (IsServer && DuelingStarted)
                ForwardEndAsLoser("left the cell");
            base.OnDestroy();
        }
    }
}
