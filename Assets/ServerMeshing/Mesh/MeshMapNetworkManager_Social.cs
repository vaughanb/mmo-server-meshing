// Server Meshing add-on — MeshMapNetworkManager: duels, trades and invitations across a line (DESIGN.md 1.44).
//
// The owner's half of what MeshPlayerCharacterDuelingComponent, MeshPlayerCharacterDealingComponent and the invitation
// wrappers (MeshSocialHandlers.cs) forward: each GhostAction here resolves the real character the action is for and
// the other party as this cell knows it (its ghost here, normally), and does on them what the kit would have done on
// one server. The trade's escrow lives here too: what a trader's cell took out of its inventory while the other cell
// does its half, given back when that half fails or never answers.
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long a trader's escrow waits for the other cell's answer before it goes back.</summary>
        public const float DealEscrowSeconds = 10f;

        private sealed class DealEscrow
        {
            public BasePlayerCharacterEntity trader;
            public string otherMeshId;
            public List<CharacterItem> items = new List<CharacterItem>();
            public int goldTaken;   // the offer plus the fee, out of the inventory
            public int goldOffered; // what the other trader receives
            public float since;
        }

        private readonly Dictionary<string, DealEscrow> _dealEscrows = new Dictionary<string, DealEscrow>();

        private void RegisterSocialGhostActions()
        {
            _ghostActionHandlers[(ushort)GhostActionKind.DuelRequest] = HandleDuelRequestAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DuelAccepted] = HandleDuelAcceptedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DuelEnded] = HandleDuelEndedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealRequest] = HandleDealRequestAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealAccepted] = HandleDealAcceptedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealMirror] = HandleDealMirrorAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealEnded] = HandleDealEndedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealCommit] = HandleDealCommitAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealCommitted] = HandleDealCommittedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.DealFailed] = HandleDealFailedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.PartyInvite] = HandlePartyInviteAction;
            _ghostActionHandlers[(ushort)GhostActionKind.GuildInvite] = HandleGuildInviteAction;
            _ghostActionHandlers[(ushort)GhostActionKind.GameMessage] = HandleGameMessageAction;
        }

        /// <summary>A player streamed to this cell as a ghost, by character id; null when it is not here or is this cell's own.</summary>
        internal BasePlayerCharacterEntity FindPlayerGhost(string characterId)
        {
            BaseCharacterEntity entity = ResolveGhostOrReal(characterId);
            return entity is BasePlayerCharacterEntity player && MeshIdentity.IsGhostEntity(player) ? player : null;
        }

        /// <summary>A game message for a player across a line, through its owner.</summary>
        internal void SendGameMessageAcross(BasePlayerCharacterEntity ghost, UITextKeys message, EntityInfo from)
        {
            ForwardGhostAction(ghost, GhostActionKind.GameMessage, from, writer => writer.Put((ushort)message));
        }

        private void HandleGameMessageAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys message = (UITextKeys)payload.GetUShort();
            if (target is BasePlayerCharacterEntity player && player.ConnectionId >= 0)
                ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, message);
        }

        // ---- duels ----

        private static bool DuelPair(BaseCharacterEntity target, EntityInfo instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other)
        {
            player = target as BasePlayerCharacterEntity;
            other = null;
            return player != null && player.DuelingComponent != null && instigator.TryGetEntity(out other) && other != null && other.DuelingComponent != null;
        }

        /// <summary>The other duelist asked across the line: pair the two here and ask this cell's real player.</summary>
        private void HandleDuelRequestAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            if (!DuelPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            if (player.DuelingComponent.DuelingCharacter != null)
            {
                if (MeshIdentity.IsGhostEntity(other))
                    SendGameMessageAcross(other, UITextKeys.UI_ERROR_CHARACTER_IS_DUELING, player.GetInfo());
                return;
            }
            player.DuelingComponent.DuelingCharacter = other;
            other.DuelingComponent.DuelingCharacter = player;
            player.DuelingComponent.CallOwnerReceiveDuelingRequest(other.ObjectId);
            Logging.Log("Mesh", $"Duel request from {other.CharacterName} across a line reached {player.CharacterName}");
        }

        /// <summary>This cell's player asked and the other accepted: start both components here (the mirror too) and tell the client.</summary>
        private void HandleDuelAcceptedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            float countDownDuration = payload.GetFloat(), duelDuration = payload.GetFloat();
            if (!DuelPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            player.DuelingComponent.DuelingCharacter = other;
            other.DuelingComponent.DuelingCharacter = player;
            other.DuelingComponent.ClearDuelingData();
            (other.DuelingComponent as MeshPlayerCharacterDuelingComponent)?.StartDuelingHere(countDownDuration, duelDuration);
            player.DuelingComponent.ClearDuelingData();
            (player.DuelingComponent as MeshPlayerCharacterDuelingComponent)?.StartDuelingHere(countDownDuration, duelDuration);
            player.DuelingComponent.CallOwnerAcceptedDuelingRequest(other.ObjectId, countDownDuration, duelDuration);
            Logging.Log("Mesh", $"Duel {player.CharacterName} <-> {other.CharacterName} started here from the other side's accept ({countDownDuration:0}+{duelDuration:0} s)");
        }

        /// <summary>The other side ended it: a decline, or the other duelist lost (died or left) there.</summary>
        private void HandleDuelEndedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            bool declined = payload.GetBool(), otherLost = payload.GetBool();
            if (!(target is BasePlayerCharacterEntity player) || player.DuelingComponent == null)
                return;
            instigator.TryGetEntity(out BasePlayerCharacterEntity other);
            if (declined)
            {
                ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, UITextKeys.UI_ERROR_DUELING_REQUEST_DECLINED);
                player.DuelingComponent.StopDueling();
                return;
            }
            uint loserObjectId = otherLost && other != null ? other.ObjectId : 0u;
            player.DuelingComponent.CallOwnerEndDueling(loserObjectId);
            player.DuelingComponent.StopDueling();
            Logging.Log("Mesh", $"Duel of {player.CharacterName} ended from across the line ({(otherLost ? (other != null ? other.CharacterName : "the other") + " lost" : "over")})");
        }

        // ---- trades ----

        private static bool DealPair(BaseCharacterEntity target, EntityInfo instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other)
        {
            player = target as BasePlayerCharacterEntity;
            other = null;
            return player != null && player.DealingComponent != null && instigator.TryGetEntity(out other) && other != null && other.DealingComponent != null;
        }

        private void HandleDealRequestAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            if (!DealPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            if (player.DealingComponent.DealingCharacter != null)
            {
                if (MeshIdentity.IsGhostEntity(other))
                    SendGameMessageAcross(other, UITextKeys.UI_ERROR_CHARACTER_IS_DEALING, player.GetInfo());
                return;
            }
            player.DealingComponent.DealingCharacter = other;
            other.DealingComponent.DealingCharacter = player;
            player.DealingComponent.CallOwnerReceiveDealingRequest(other.ObjectId);
            Logging.Log("Mesh", $"Trade request from {other.CharacterName} across a line reached {player.CharacterName}");
        }

        /// <summary>This cell's player asked and the other accepted: both open here, the mirror empty, the client told.</summary>
        private void HandleDealAcceptedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            if (!DealPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            player.DealingComponent.DealingCharacter = other;
            other.DealingComponent.DealingCharacter = player;
            (other.DealingComponent as MeshPlayerCharacterDealingComponent)?.SetMirror(DealingState.Dealing, 0, new DealingCharacterItems());
            if (player.DealingComponent is MeshPlayerCharacterDealingComponent mesh)
                mesh.OpenFresh(); // no None before the accept: the kit's trade window would close on it and cancel
            else
            {
                player.DealingComponent.ClearDealingData();
                player.DealingComponent.DealingState = DealingState.Dealing;
            }
            player.DealingComponent.CallOwnerAcceptedDealingRequest(other.ObjectId);
            player.DealingComponent.CallOwnerUpdateAnotherDealingState(DealingState.Dealing);
            Logging.Log("Mesh", $"Trade {player.CharacterName} <-> {other.CharacterName} open here from the other side's accept");
        }

        /// <summary>The other trader's offer changed: its mirror here and this cell's client.</summary>
        private void HandleDealMirrorAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            DealingState state = (DealingState)payload.GetByte();
            int gold = payload.GetInt();
            DealingCharacterItems items = new DealingCharacterItems();
            items.Deserialize(payload);
            if (!DealPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            if (!ReferenceEquals(player.DealingComponent.DealingCharacter, other))
                return; // not the trade this player is in (a stale mirror after a cancel)
            (other.DealingComponent as MeshPlayerCharacterDealingComponent)?.SetMirror(state, gold, items);
            player.DealingComponent.CallOwnerUpdateAnotherDealingState(state);
            player.DealingComponent.CallOwnerUpdateAnotherDealingGold(gold);
            player.DealingComponent.CallOwnerUpdateAnotherDealingItems(items);
        }

        /// <summary>The other side ended the trade (declined, cancelled, left): a message, the escrow back if any, both cleared here.</summary>
        private void HandleDealEndedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys message = (UITextKeys)payload.GetUShort();
            if (!(target is BasePlayerCharacterEntity player) || player.DealingComponent == null)
                return;
            RefundDealEscrow(player, "the other side ended the trade");
            if (player.DealingComponent.DealingState != DealingState.None || player.DealingComponent.DealingCharacter != null)
                ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, message);
            player.DealingComponent.StopDealing();
        }

        /// <summary>
        /// BeginDealEscrow takes a trader's offer (items, gold and fee) out of its inventory, keeps it, and sends the
        /// offer to the other trader's owner for the other half. False when the offer is no longer in the inventory.
        /// </summary>
        internal bool BeginDealEscrow(BasePlayerCharacterEntity trader, BasePlayerCharacterEntity otherGhost, IList<CharacterItem> offered, int goldWithFee, int goldOffered)
        {
            if (trader == null || otherGhost == null || _dealEscrows.ContainsKey(trader.Id))
                return false;
            DealEscrow escrow = new DealEscrow { trader = trader, otherMeshId = MeshEntityIdOf(otherGhost), goldTaken = goldWithFee, goldOffered = goldOffered, since = Time.unscaledTime };
            if (!MeshDealing.TakeOffered(trader, offered, goldWithFee, escrow.items))
                return false;
            _dealEscrows[trader.Id] = escrow;
            List<CharacterItem> items = escrow.items;
            ForwardGhostAction(otherGhost, GhostActionKind.DealCommit, trader.GetInfo(), writer =>
            {
                writer.Put(items.Count);
                for (int i = 0; i < items.Count; ++i) writer.Put(items[i]);
                writer.Put(goldOffered);
            });
            StartCoroutine(DealEscrowTimeout(trader.Id));
            Logging.Log("Mesh", $"Trade commit: {trader.CharacterName}'s offer ({items.Count} item(s), {goldOffered} gold, {goldWithFee - goldOffered} fee) in escrow, sent to {otherGhost.CharacterName}'s owner");
            return true;
        }

        internal bool HasDealEscrow(BasePlayerCharacterEntity trader)
        {
            return trader != null && !string.IsNullOrEmpty(trader.Id) && _dealEscrows.ContainsKey(trader.Id);
        }

        /// <summary>Puts a trader's escrow back into its inventory, if it has one (a puppet being destroyed has no id and no escrow).</summary>
        internal void RefundDealEscrow(BasePlayerCharacterEntity trader, string why)
        {
            if (trader == null || string.IsNullOrEmpty(trader.Id) || !_dealEscrows.TryGetValue(trader.Id, out DealEscrow escrow))
                return;
            _dealEscrows.Remove(trader.Id);
            MeshDealing.Give(trader, escrow.items, escrow.goldTaken, null);
            Logging.LogWarning("Mesh", $"Trade escrow of {trader.CharacterName} returned ({why}): {escrow.items.Count} item(s), {escrow.goldTaken} gold");
        }

        private IEnumerator DealEscrowTimeout(string traderId)
        {
            yield return new WaitForSecondsRealtime(DealEscrowSeconds);
            if (!_dealEscrows.TryGetValue(traderId, out DealEscrow escrow) || escrow.trader == null)
                yield break;
            RefundDealEscrow(escrow.trader, $"no answer from the other cell in {DealEscrowSeconds:0} s");
            if (escrow.trader.DealingComponent != null)
            {
                ServerGameMessageHandlers.SendGameMessage(escrow.trader.ConnectionId, UITextKeys.UI_ERROR_DEALING_CANCELED);
                escrow.trader.DealingComponent.StopDealing();
            }
        }

        private static List<CharacterItem> ReadItems(NetDataReader payload)
        {
            int count = payload.GetInt();
            List<CharacterItem> items = new List<CharacterItem>(count);
            for (int i = 0; i < count; ++i)
                items.Add(payload.Get<CharacterItem>());
            return items;
        }

        /// <summary>
        /// The other trader's offer is in escrow on its cell: this trader must still be confirmed with that trader, able
        /// to pay its own offer and to receive the other's; then its offer is taken, the other's given, and the taken
        /// offer answered. Any refusal answers DealFailed, and nothing here changes.
        /// </summary>
        private void HandleDealCommitAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            List<CharacterItem> offered = ReadItems(payload);
            int goldOffered = payload.GetInt();
            if (!DealPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            PlayerCharacterDealingComponent dealing = player.DealingComponent;
            UITextKeys forPlayer = UITextKeys.NONE, forOther = UITextKeys.NONE;
            if (!ReferenceEquals(dealing.DealingCharacter, other) || dealing.DealingState != DealingState.ConfirmDealing)
                forOther = UITextKeys.UI_ERROR_INVALID_DEALING_STATE;
            else if (!dealing.HaveEnoughGold())
            {
                forPlayer = UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD;
                forOther = UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD;
            }
            else if (MeshDealing.WillOverwhelm(player, offered))
            {
                forPlayer = UITextKeys.UI_ERROR_WILL_OVERWHELMING;
                forOther = UITextKeys.UI_ERROR_ANOTHER_CHARACTER_WILL_OVERWHELMING;
            }
            List<CharacterItem> taken = new List<CharacterItem>();
            int goldTaken = dealing.GetRequiredGold(), goldGiven = dealing.DealingGold;
            if (forOther == UITextKeys.NONE && !MeshDealing.TakeOffered(player, dealing.DealingItems, goldTaken, taken))
            {
                forPlayer = UITextKeys.UI_ERROR_INVALID_DEALING_STATE;
                forOther = UITextKeys.UI_ERROR_DEALING_CANCELED;
            }
            if (forOther != UITextKeys.NONE)
            {
                UITextKeys reason = forOther;
                ForwardGhostAction(other, GhostActionKind.DealFailed, player.GetInfo(), writer => writer.Put((ushort)reason));
                if (forPlayer != UITextKeys.NONE)
                    ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, forPlayer);
                if (ReferenceEquals(dealing.DealingCharacter, other))
                    dealing.StopDealing();
                Logging.LogWarning("Mesh", $"Trade commit from {other.CharacterName} refused for {player.CharacterName}: {forOther}");
                return;
            }
            MeshDealing.Give(player, offered, goldOffered, item => player.OnRewardItem(RewardGivenType.Dealing, item));
            player.OnRewardGold(RewardGivenType.Dealing, goldOffered);
            ServerLogHandlers.LogExchangeDealingItemsAndGold(player, other, goldGiven, dealing.DealingItems);
            ForwardGhostAction(other, GhostActionKind.DealCommitted, player.GetInfo(), writer =>
            {
                writer.Put(taken.Count);
                for (int i = 0; i < taken.Count; ++i) writer.Put(taken[i]);
                writer.Put(goldGiven);
            });
            Logging.Log("Mesh", $"Trade committed here: {player.CharacterName} gave {taken.Count} item(s) and {goldGiven} gold, received {offered.Count} item(s) and {goldOffered} gold from {other.CharacterName} across a line");
            dealing.StopDealing();
        }

        /// <summary>The other half is done: this trader receives the other's offer, and the escrow is spent.</summary>
        private void HandleDealCommittedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            List<CharacterItem> received = ReadItems(payload);
            int goldReceived = payload.GetInt();
            if (!(target is BasePlayerCharacterEntity player) || player.DealingComponent == null)
                return;
            if (!_dealEscrows.TryGetValue(player.Id, out DealEscrow escrow))
            {
                Logging.LogWarning("Mesh", $"Trade committed for {player.CharacterName} with no escrow here: the other side's {received.Count} item(s) and {goldReceived} gold are given all the same");
            }
            else
            {
                _dealEscrows.Remove(player.Id);
                ServerLogHandlers.LogExchangeDealingItemsAndGold(player, player.DealingComponent.DealingCharacter, escrow.goldOffered, new DealingCharacterItems { });
            }
            MeshDealing.Give(player, received, goldReceived, item => player.OnRewardItem(RewardGivenType.Dealing, item));
            player.OnRewardGold(RewardGivenType.Dealing, goldReceived);
            Logging.Log("Mesh", $"Trade committed here: {player.CharacterName} received {received.Count} item(s) and {goldReceived} gold across a line; its escrow is spent");
            player.DealingComponent.StopDealing();
        }

        /// <summary>The other cell refused the commit: the escrow goes back and the trade ends with the reason.</summary>
        private void HandleDealFailedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys reason = (UITextKeys)payload.GetUShort();
            if (!(target is BasePlayerCharacterEntity player) || player.DealingComponent == null)
                return;
            RefundDealEscrow(player, $"refused by the other cell: {reason}");
            ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, reason);
            player.DealingComponent.StopDealing();
        }

        // ---- invitations ----

        /// <summary>A party invitation from an inviter across the line: the invitation is kept on this cell (where the accept comes in) and the party loaded here.</summary>
        private void HandlePartyInviteAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            PartyInvitationData data = new PartyInvitationData
            {
                InviterId = payload.GetString(), InviterName = payload.GetString(), InviterLevel = payload.GetInt(),
                PartyId = payload.GetInt(), ShareExp = payload.GetBool(), ShareItem = payload.GetBool(),
            };
            if (!(target is BasePlayerCharacterEntity player))
                return;
            DeliverPartyInvitation(player, data, instigator).Forget();
        }

        private async UniTaskVoid DeliverPartyInvitation(BasePlayerCharacterEntity player, PartyInvitationData data, EntityInfo inviter)
        {
            if (player.PartyId > 0)
            {
                if (inviter.TryGetEntity(out BasePlayerCharacterEntity inviterGhost) && MeshIdentity.IsGhostEntity(inviterGhost))
                    SendGameMessageAcross(inviterGhost, UITextKeys.UI_ERROR_CHARACTER_JOINED_ANOTHER_PARTY, player.GetInfo());
                return;
            }
            if (!ServerPartyHandlers.TryGetParty(data.PartyId, out _))
                await LoadPartyRoutine(data.PartyId);
            if (player == null || !ServerPartyHandlers.TryGetParty(data.PartyId, out _))
            {
                Logging.LogWarning("Mesh", $"Party invitation for {(player != null ? player.CharacterName : "a player")} across a line: party {data.PartyId} could not be loaded here");
                return;
            }
            ServerPartyHandlers.AppendPartyInvitation(data.PartyId, player.Id);
            ServerGameMessageHandlers.SendNotifyPartyInvitation(player.ConnectionId, data);
            Logging.Log("Mesh", $"Party invitation from {data.InviterName} across a line reached {player.CharacterName} (party {data.PartyId})");
        }

        private void HandleGuildInviteAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            GuildInvitationData data = new GuildInvitationData
            {
                InviterId = payload.GetString(), InviterName = payload.GetString(), InviterLevel = payload.GetInt(),
                GuildId = payload.GetInt(), GuildName = payload.GetString(), GuildLevel = payload.GetInt(),
            };
            if (!(target is BasePlayerCharacterEntity player))
                return;
            DeliverGuildInvitation(player, data, instigator).Forget();
        }

        private async UniTaskVoid DeliverGuildInvitation(BasePlayerCharacterEntity player, GuildInvitationData data, EntityInfo inviter)
        {
            if (player.GuildId > 0)
            {
                if (inviter.TryGetEntity(out BasePlayerCharacterEntity inviterGhost) && MeshIdentity.IsGhostEntity(inviterGhost))
                    SendGameMessageAcross(inviterGhost, UITextKeys.UI_ERROR_CHARACTER_JOINED_ANOTHER_GUILD, player.GetInfo());
                return;
            }
            if (!ServerGuildHandlers.TryGetGuild(data.GuildId, out _))
                await LoadGuildRoutine(data.GuildId);
            if (player == null || !ServerGuildHandlers.TryGetGuild(data.GuildId, out _))
            {
                Logging.LogWarning("Mesh", $"Guild invitation for {(player != null ? player.CharacterName : "a player")} across a line: guild {data.GuildId} could not be loaded here");
                return;
            }
            ServerGuildHandlers.AppendGuildInvitation(data.GuildId, player.Id);
            ServerGameMessageHandlers.SendNotifyGuildInvitation(player.ConnectionId, data);
            Logging.Log("Mesh", $"Guild invitation from {data.InviterName} across a line reached {player.CharacterName} (guild {data.GuildId})");
        }
#else
        internal BasePlayerCharacterEntity FindPlayerGhost(string characterId) { return null; }
        internal void SendGameMessageAcross(BasePlayerCharacterEntity ghost, UITextKeys message, EntityInfo from) { }
        internal bool BeginDealEscrow(BasePlayerCharacterEntity trader, BasePlayerCharacterEntity otherGhost, IList<CharacterItem> offered, int goldWithFee, int goldOffered) { return false; }
        internal bool HasDealEscrow(BasePlayerCharacterEntity trader) { return false; }
        internal void RefundDealEscrow(BasePlayerCharacterEntity trader, string why) { }
#endif
    }
}
