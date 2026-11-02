// Server Meshing add-on — a trade across a line (DESIGN.md 1.44).
//
// The kit's trade is two components on one server: each holds its own offer (state, gold, items) and a reference to
// the other, every change is sent to its own client and to the other's, and when both have confirmed one call moves
// the items and gold between the two inventories. Across a line the other trader is a puppet here, its client is not
// reachable and its inventory is not ours to change. This subclass is swapped onto the player prefabs in place of the
// kit's component and declares the kit's server RPCs again under the same names (the client's calls are unchanged):
//  * the offer of the real character here is mirrored to the other trader's owner after every change (GhostAction
//    DealMirror): that cell writes it on its puppet of this character and tells the real other's client, so both
//    clients see both offers as before;
//  * the exchange is split in two halves: the cell where the second confirm lands takes its own trader's offer out of
//    its inventory into an escrow and sends it over (DealCommit); the other cell checks its trader can still pay and
//    receive, takes that trader's offer, gives the escrowed one, and answers with what it took (DealCommitted), which
//    the first cell then gives to its trader. A refusal (DealFailed), a cancel from the other side or 10 s without an
//    answer puts the escrow back. The window where a cell dies between the two halves is the window the kit's one call
//    has when its server dies, spread over two cells; nothing is paid twice.
// MeshMapNetworkManager_Social holds the handlers of the other side and the escrow.
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshPlayerCharacterDealingComponent : PlayerCharacterDealingComponent
    {
        private static MeshMapNetworkManager Mesh => BaseGameNetworkManager.Singleton as MeshMapNetworkManager;

        private bool OtherIsGhost => DealingCharacter != null && MeshIdentity.IsGhostEntity(DealingCharacter) && Mesh != null;

        /// <summary>The other trader's offer as its owner last sent it (this component is the mirror of a trader across a line).</summary>
        internal void SetMirror(DealingState state, int gold, DealingCharacterItems items)
        {
            _dealingState = state;
            _dealingGold = gold;
            _dealingItems = items ?? new DealingCharacterItems();
        }

        /// <summary>
        /// Opens a fresh trade for this trader: no offer, state Dealing, its client told once. The kit's accept clears the
        /// data first (state None) and then sets Dealing, and the None can reach the client after the accept message (it
        /// travels ordered, the accept unordered): the kit's trade window, just opened, closes on None and closing it sends
        /// a cancel. Across a line that ordering was common (DESIGN 1.44); here no None is sent.
        /// </summary>
        internal void OpenFresh()
        {
            _dealingGold = 0;
            _dealingItems = new DealingCharacterItems();
            DealingState = DealingState.Dealing;
        }

        /// <summary>Clears the mirror without the kit's RPCs (the puppet has no client; the real other is told by its owner).</summary>
        internal void ClearMirror()
        {
            _dealingState = DealingState.None;
            _dealingGold = 0;
            _dealingItems = new DealingCharacterItems();
        }

        private void Refuse(string why, UITextKeys message)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            Logging.Log("Mesh", $"Trade request by {Entity.CharacterName} refused: {why}");
            GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, message);
#endif
        }

        /// <summary>After a change of this trader's offer: the other trader's owner gets it for its mirror and its client.</summary>
        private void MirrorToOwner()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other == null || !MeshIdentity.IsGhostEntity(other) || Mesh == null)
                return;
            DealingState state = DealingState;
            int gold = DealingGold;
            DealingCharacterItems items = DealingItems;
            Mesh.ForwardGhostAction(other, GhostActionKind.DealMirror, Entity.GetInfo(), writer => { writer.Put((byte)state); writer.Put(gold); writer.Put(items); });
#endif
        }

        /// <summary>Ends the trade on the other side too, with a message for its client, when the other trader is across a line.</summary>
        private void EndAcross(UITextKeys messageForOther)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other == null || !MeshIdentity.IsGhostEntity(other) || Mesh == null)
                return;
            Mesh.ForwardGhostAction(other, GhostActionKind.DealEnded, Entity.GetInfo(), writer => writer.Put((ushort)messageForOther));
#endif
        }

        [ServerRpc]
        protected new void CmdSendDealingRequest(uint objectId)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            Logging.Log("Mesh", $"CmdSendDealingRequest on {Entity.CharacterName} (object {ObjectId}, conn {ConnectionId}, ghost {MeshIdentity.IsGhostEntity(Entity)}) for object {objectId}");
            if (DisableDealing)
            {
                Refuse("dealing disabled", UITextKeys.UI_ERROR_FEATURE_IS_DISABLED);
                return;
            }
            if (!Manager.TryGetEntityByObjectId(objectId, out BasePlayerCharacterEntity target))
            {
                Refuse($"no player object {objectId}", UITextKeys.UI_ERROR_CHARACTER_NOT_FOUND);
                return;
            }
            if (target.DealingComponent.DealingCharacter != null)
            {
                Refuse($"{target.CharacterName} (object {target.ObjectId}, conn {target.ConnectionId}) is already dealing with {target.DealingComponent.DealingCharacter.CharacterName} (object {target.DealingComponent.DealingCharacter.ObjectId}, state {target.DealingComponent.DealingState}, set {Time.unscaledTime - target.DealingComponent.DealingRequestTime:0.0} s ago)", UITextKeys.UI_ERROR_CHARACTER_IS_DEALING);
                return;
            }
            if (!Entity.IsGameEntityInDistance(target))
            {
                Refuse($"{target.CharacterName} is too far", UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                return;
            }
            DealingCharacter = target;
            target.DealingComponent.DealingCharacter = Entity;
            if (MeshIdentity.IsGhostEntity(target) && Mesh != null)
            {
                Mesh.ForwardGhostAction(target, GhostActionKind.DealRequest, Entity.GetInfo(), null);
                Logging.Log("Mesh", $"Trade request {Entity.CharacterName} -> {target.CharacterName} across a line, sent to its owner");
                return;
            }
            DealingCharacter.DealingComponent.CallOwnerReceiveDealingRequest(ObjectId);
#endif
        }

        [ServerRpc]
        protected new void CmdAcceptDealingRequest()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other == null)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CANNOT_ACCEPT_DEALING_REQUEST);
                StopDealing();
                return;
            }
            if (!Entity.IsGameEntityInDistance(other))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                StopDealing();
                return;
            }
            bool across = MeshIdentity.IsGhostEntity(other) && Mesh != null;
            if (across)
            {
                // The mirror of the other trader here starts empty and open; its owner does the same on the real one.
                if (other.DealingComponent is MeshPlayerCharacterDealingComponent mirror)
                    mirror.SetMirror(DealingState.Dealing, 0, new DealingCharacterItems());
                Mesh.ForwardGhostAction(other, GhostActionKind.DealAccepted, Entity.GetInfo(), null);
                Logging.Log("Mesh", $"Trade accepted {Entity.CharacterName} <-> {other.CharacterName} across a line, sent to its owner");
            }
            else
            {
                other.DealingComponent.ClearDealingData();
                other.DealingComponent.DealingState = DealingState.Dealing;
                other.DealingComponent.CallOwnerAcceptedDealingRequest(ObjectId);
            }
            if (across)
                OpenFresh();
            else
            {
                ClearDealingData();
                DealingState = DealingState.Dealing;
            }
            CallOwnerAcceptedDealingRequest(other.ObjectId);
            if (across)
                CallOwnerUpdateAnotherDealingState(DealingState.Dealing); // the mirror's setter did not run: tell this client the other is open
            MirrorToOwner();
#endif
        }

        [ServerRpc]
        protected new void CmdDeclineDealingRequest()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other != null)
            {
                if (MeshIdentity.IsGhostEntity(other) && Mesh != null)
                    EndAcross(UITextKeys.UI_ERROR_DEALING_REQUEST_DECLINED);
                else
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_DEALING_REQUEST_DECLINED);
            }
            GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_DEALING_REQUEST_DECLINED);
            StopDealing();
#endif
        }

        [ServerRpc]
        protected new void CmdSetDealingItem(string id, int amount)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (DealingState != DealingState.Dealing)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                return;
            }
            int index = Entity.IndexOfNonEquipItem(id);
            if (index < 0)
                return;
            CharacterItem nonEquipItem = Entity.NonEquipItems[index];
            if (nonEquipItem.IsEmptySlot())
                return;
            if (nonEquipItem.GetItem().RestrictDealing)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_ITEM_DEALING_RESTRICTED);
                return;
            }
            DealingCharacterItems dealingItems = DealingItems;
            if (GameInstance.Singleton.dealingItemsLimit > 0 && dealingItems.Count + 1 > GameInstance.Singleton.dealingItemsLimit)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_REACHED_DEALING_ITEMS_LIMIT);
                return;
            }
            for (int i = dealingItems.Count - 1; i >= 0; --i)
            {
                if (id == dealingItems[i].id)
                {
                    dealingItems.RemoveAt(i);
                    break;
                }
            }
            CharacterItem dealingItem = Entity.NonEquipItems[index].Clone();
            dealingItem.amount = amount;
            dealingItems.Add(dealingItem);
            DealingItems = dealingItems;
            MirrorToOwner();
#endif
        }

        [ServerRpc]
        protected new void CmdSetDealingGold(int gold)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (DealingState != DealingState.Dealing)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                return;
            }
            if (gold > Entity.Gold)
                gold = Entity.Gold;
            if (gold < 0)
                gold = 0;
            DealingGold = gold;
            MirrorToOwner();
#endif
        }

        [ServerRpc]
        protected new void CmdLockDealing()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (DealingState != DealingState.Dealing)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                return;
            }
            if (!HaveEnoughGold())
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
                return;
            }
            DealingState = DealingState.LockDealing;
            MirrorToOwner();
#endif
        }

        [ServerRpc]
        protected new void CmdConfirmDealing()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other == null || DealingState != DealingState.LockDealing || !(other.DealingComponent.DealingState == DealingState.LockDealing || other.DealingComponent.DealingState == DealingState.ConfirmDealing))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                return;
            }
            if (!HaveEnoughGold())
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
                return;
            }
            DealingState = DealingState.ConfirmDealing;
            MirrorToOwner();
            if (other.DealingComponent.DealingState != DealingState.ConfirmDealing)
                return; // the other has not confirmed yet: its confirm (or, across a line, its mirror here) closes the trade
            if (!(MeshIdentity.IsGhostEntity(other) && Mesh != null))
            {
                // Both here: the kit's exchange, as it was.
                if (!HaveEnoughGold() || !other.DealingComponent.HaveEnoughGold())
                {
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
                }
                else if (ExchangingDealingItemsWillOverwhelming())
                {
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_ANOTHER_CHARACTER_WILL_OVERWHELMING);
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_WILL_OVERWHELMING);
                }
                else if (other.DealingComponent.ExchangingDealingItemsWillOverwhelming())
                {
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_WILL_OVERWHELMING);
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_ANOTHER_CHARACTER_WILL_OVERWHELMING);
                }
                else
                {
                    ExchangeDealingItemsAndGold();
                    other.DealingComponent.ExchangeDealingItemsAndGold();
                }
                StopDealing();
                return;
            }
            BeginAcrossIfChosen("its own confirm");
#endif
        }

        /// <summary>
        /// Both traders have confirmed and the other is across a line: exactly one of the two cells begins the exchange,
        /// the one whose trader has the smaller character id (both cells decide alike), as soon as it sees both
        /// confirmed, from its own trader's confirm or from the other's arriving as a mirror. Two confirms sent within the
        /// same moment each saw the other still locked, and neither cell began it (DESIGN 1.46: the rig's trade stuck at
        /// both confirmed). The other cell waits for the DealCommit.
        /// </summary>
        internal void BeginAcrossIfChosen(string seenBy)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other == null || other.DealingComponent == null || !MeshIdentity.IsGhostEntity(other) || Mesh == null)
                return;
            if (DealingState != DealingState.ConfirmDealing || other.DealingComponent.DealingState != DealingState.ConfirmDealing || Mesh.HasDealEscrow(Entity))
                return;
            if (string.CompareOrdinal(Entity.Id, other.Id) > 0)
            {
                Logging.Log("Mesh", $"Trade {Entity.CharacterName} <-> {other.CharacterName}: both confirmed (seen by {seenBy}); the other cell begins the exchange");
                return;
            }
            // This trader can receive what the other offers (its offer is the mirror's), and its own offer goes into
            // escrow and over to the other's owner, which does the other half and answers.
            if (MeshDealing.WillOverwhelm(Entity, other.DealingComponent.DealingItems))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_WILL_OVERWHELMING);
                EndAcross(UITextKeys.UI_ERROR_ANOTHER_CHARACTER_WILL_OVERWHELMING);
                StopDealing();
                return;
            }
            if (!Mesh.BeginDealEscrow(Entity, other, DealingItems, GetRequiredGold(), DealingGold))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                EndAcross(UITextKeys.UI_ERROR_DEALING_CANCELED);
                StopDealing();
                return;
            }
            Logging.Log("Mesh", $"Trade {Entity.CharacterName} <-> {other.CharacterName}: both confirmed (seen by {seenBy}); this cell begins the exchange");
            // The state stays ConfirmDealing until the answer: the kit refuses every other action meanwhile.
#endif
        }

        [ServerRpc]
        protected new void CmdCancelDealing()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (Mesh != null && Mesh.HasDealEscrow(Entity))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DEALING_STATE);
                return; // the exchange is in flight: it ends on the answer
            }
            BasePlayerCharacterEntity other = DealingCharacter;
            if (other != null && other.DealingComponent.DealingState != DealingState.None)
            {
                if (MeshIdentity.IsGhostEntity(other) && Mesh != null)
                    EndAcross(UITextKeys.UI_ERROR_DEALING_CANCELED);
                else
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(other.ConnectionId, UITextKeys.UI_ERROR_DEALING_CANCELED);
            }
            if (DealingState != DealingState.None)
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_DEALING_CANCELED);
            StopDealing();
#endif
        }

        /// <summary>This trader is leaving the cell mid-trade (a hop, a demotion, a disconnect): an escrow goes back, and the other trader's owner ends it there.</summary>
        internal void EndAcrossBeforeLeaving()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (!IsServer || Mesh == null)
                return;
            Mesh.RefundDealEscrow(Entity, "the trader left the cell");
            if (OtherIsGhost && DealingState != DealingState.None)
                EndAcross(UITextKeys.UI_ERROR_DEALING_CANCELED);
#endif
        }

        protected override void OnDestroy()
        {
            EndAcrossBeforeLeaving();
            base.OnDestroy();
        }
    }
}
