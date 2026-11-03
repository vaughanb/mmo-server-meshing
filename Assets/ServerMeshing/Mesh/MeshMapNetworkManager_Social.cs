// Server Meshing add-on — MeshMapNetworkManager: duels, trades and invitations across a line (DESIGN.md 1.44).
//
// The owner's half of what MeshPlayerCharacterDuelingComponent, MeshPlayerCharacterDealingComponent and the invitation
// wrappers (MeshSocialHandlers.cs) forward: each GhostAction here resolves the real character the action is for and
// the other party as this cell knows it (its ghost here, normally), and does on them what the kit would have done on
// one server. The trade's escrow lives here too: what a trader's cell took out of its inventory while the other cell
// does its half, given back when that half fails or never answers.
//
// DESIGN 1.49: the other cell decides whether an exchange happened, as the buyer's cell does a sale (1.48). Every
// exchange has an id, the escrow's cell's, carried by its messages. The other cell records its answer by id (with what
// it took, so it can be sent again; the record crosses with the trader in MeshTradeCarrier) and gives the same answer to
// the same exchange. Without an answer in 10 s the escrow's cell asks (DealQuery), every 10 s; a cell that never saw
// the exchange answers it failed (and ends its trader's trade) and refuses it from then on; after 60 s the escrow goes
// back. A trader whose exchange is in escrow is not handed off (the boundary tick holds it); one who leaves otherwise
// (a disconnect) has its exchange asked about at once, and the answer is applied to the character the kit keeps for its
// despawn delay, whose second save takes it. A repeated answer for an exchange already settled here changes nothing.
// MESH_TEST_TRADE_DROP (comma-separated `commit<n>`, `committed<n>`) drops a cell's n-th commit or answer, and
// MESH_TEST_TRADE_ANSWER_DELAY (seconds, one per commit handled) holds a cell's answers, for regress.py trade.
//
// DESIGN 1.51: an exchange has no deadline any more. After a minute without an answer its trader is released (the trade
// ends for it, it may move and hop again) while the exchange stays open, asked about every 30 s; whatever settles it
// (an answer, or 30 minutes without one) is applied to the trader if it is registered here, and mailed to its account
// otherwise (MeshMapNetworkManager_Delivery): a trader who logged out (the kit's safe disconnect despawns at once), was
// disconnected or hopped away. MESH_TEST_TRADE_BLACKOUT (`<n>:<seconds>`) drops every answer to the n-th commit a cell
// handles for that long, as a link down for a minute would.
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
        /// <summary>The answering cell's record of one exchange (DESIGN 1.49): carried with its trader by MeshTradeCarrier.</summary>
        internal sealed class DealDecision
        {
            public string tradeId, otherId, otherZone;
            public bool committed;
            public ushort reason;                                         // failed: the message for the escrow's trader
            public List<CharacterItem> taken = new List<CharacterItem>(); // committed: what this cell's trader gave
            public int goldGiven;
            public float at;
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long a trader's escrow waits for the other cell's answer before it asks, and between asks.</summary>
        public const float DealEscrowSeconds = 10f;
        /// <summary>After this long without an answer the trader is released, the exchange left open (DESIGN 1.51).</summary>
        public const float DealReleaseSeconds = 60f;
        /// <summary>How often a released exchange is asked about.</summary>
        public const float DealReleasedAskSeconds = 30f;
        /// <summary>After this long without an answer the escrow goes back (applied or mailed; logged as an error).</summary>
        public const float DealGiveUpSeconds = 1800f;
        /// <summary>How long the other cell remembers its answer on an exchange.</summary>
        public const float DealDecisionSeconds = 600f;

        private sealed class DealEscrow
        {
            public string traderId, traderUserId, traderName;
            public bool released; // no longer the trader's open trade: settled by the answer, applied or mailed
            public string otherMeshId, otherZone, tradeId;
            public List<CharacterItem> items = new List<CharacterItem>();
            public int goldTaken;   // the offer plus the fee, out of the inventory
            public int goldOffered; // what the other trader receives
            public float since;
        }

        private readonly Dictionary<string, DealEscrow> _dealEscrows = new Dictionary<string, DealEscrow>();        // trader id: its open trade's escrow
        private readonly Dictionary<string, DealEscrow> _dealEscrowsByTrade = new Dictionary<string, DealEscrow>(); // trade id: every escrow until settled
        private readonly Dictionary<string, float> _testTradeBlackouts = new Dictionary<string, float>();       // trade id -> until
        private int _testCommitsHandled;
        private readonly Dictionary<string, List<DealDecision>> _dealDecisions = new Dictionary<string, List<DealDecision>>(); // the answering trader's id
        private readonly Dictionary<string, float> _dealsSettled = new Dictionary<string, float>(); // trade id -> when, on the escrow's cell
        private readonly HashSet<string> _dealHoldLogged = new HashSet<string>();
        private float[] _testTradeAnswerDelays;
        private int _testTradeAnswers, _testCommitsSent, _testCommittedSent;
        private HashSet<string> _testTradeDrops;

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
            _ghostActionHandlers[(ushort)GhostActionKind.DealQuery] = HandleDealQueryAction;
            _ghostActionHandlers[(ushort)GhostActionKind.PartyInvite] = HandlePartyInviteAction;
            _ghostActionHandlers[(ushort)GhostActionKind.GuildInvite] = HandleGuildInviteAction;
            _ghostActionHandlers[(ushort)GhostActionKind.GameMessage] = HandleGameMessageAction;
            RegisterVendingGhostActions(); // DESIGN 1.46
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
            if (state == DealingState.ConfirmDealing && player.DealingComponent is MeshPlayerCharacterDealingComponent mesh)
                mesh.BeginAcrossIfChosen("the other's confirm"); // DESIGN 1.46: two confirms at once each saw the other locked
        }

        /// <summary>The other side ended the trade (declined, cancelled, left): a message, the escrow back if any, both cleared here.</summary>
        private void HandleDealEndedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys message = (UITextKeys)payload.GetUShort();
            if (!(target is BasePlayerCharacterEntity player) || player.DealingComponent == null)
                return;
            if (_dealEscrows.TryGetValue(player.Id, out DealEscrow ended))
                SettleDealEscrow(ended, false, null, 0, message, "the other side ended the trade");
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
            MeshIdentity otherIdentity = MeshIdentity.Get(otherGhost);
            DealEscrow escrow = new DealEscrow
            {
                traderId = trader.Id, traderUserId = trader.UserId, traderName = trader.CharacterName,
                otherMeshId = MeshEntityIdOf(otherGhost), otherZone = otherIdentity != null ? otherIdentity.OwnerZoneId : null,
                tradeId = System.Guid.NewGuid().ToString("N"), goldTaken = goldWithFee, goldOffered = goldOffered, since = Time.unscaledTime,
            };
            if (!MeshDealing.TakeOffered(trader, offered, goldWithFee, escrow.items))
                return false;
            _dealEscrows[trader.Id] = escrow;
            _dealEscrowsByTrade[escrow.tradeId] = escrow;
            List<CharacterItem> items = escrow.items;
            string tradeId = escrow.tradeId;
            if (TestTradeDrop("commit", ++_testCommitsSent))
                Logging.LogWarning("Mesh", $"Trade commit {tradeId} of {trader.CharacterName} dropped (MESH_TEST_TRADE_DROP commit{_testCommitsSent})");
            else
                ForwardGhostAction(otherGhost, GhostActionKind.DealCommit, trader.GetInfo(), writer =>
                {
                    writer.Put(tradeId);
                    writer.Put(items.Count);
                    for (int i = 0; i < items.Count; ++i) writer.Put(items[i]);
                    writer.Put(goldOffered);
                });
            StartCoroutine(DealEscrowTimeout(tradeId));
            Logging.Log("Mesh", $"Trade commit: {trader.CharacterName}'s offer ({items.Count} item(s), {goldOffered} gold, {goldWithFee - goldOffered} fee) in escrow, sent to {otherGhost.CharacterName}'s owner (trade {tradeId})");
            return true;
        }

        /// <summary>The boundary tick holds a trader whose exchange is in escrow here (DESIGN 1.49).</summary>
        internal bool HoldsHandoffForTrade(BasePlayerCharacterEntity player)
        {
            if (player == null || string.IsNullOrEmpty(player.Id) || !_dealEscrows.ContainsKey(player.Id))
            {
                if (player != null && !string.IsNullOrEmpty(player.Id))
                    _dealHoldLogged.Remove(player.Id);
                return false;
            }
            if (_dealHoldLogged.Add(player.Id))
                Logging.Log("Mesh", $"Handoff of {player.CharacterName} held: its exchange is in escrow here (at most {DealReleaseSeconds:0} s)");
            return true;
        }

        private float TestTradeAnswerDelay()
        {
            if (_testTradeAnswerDelays == null)
            {
                List<float> delays = new List<float>();
                string text = System.Environment.GetEnvironmentVariable("MESH_TEST_TRADE_ANSWER_DELAY");
                if (!string.IsNullOrEmpty(text))
                    foreach (string part in text.Split(','))
                        if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0f)
                            delays.Add(s);
                _testTradeAnswerDelays = delays.ToArray();
            }
            if (_testTradeAnswerDelays.Length == 0)
                return 0f;
            return _testTradeAnswerDelays[Mathf.Min(_testTradeAnswers++, _testTradeAnswerDelays.Length - 1)];
        }

        private bool TestTradeDrop(string kind, int n)
        {
            if (_testTradeDrops == null)
            {
                _testTradeDrops = new HashSet<string>();
                string text = System.Environment.GetEnvironmentVariable("MESH_TEST_TRADE_DROP");
                if (!string.IsNullOrEmpty(text))
                    foreach (string part in text.Split(','))
                        _testTradeDrops.Add(part.Trim().ToLowerInvariant());
            }
            return _testTradeDrops.Contains(kind + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private DealEscrow FindDealEscrow(string tradeId)
        {
            return !string.IsNullOrEmpty(tradeId) && _dealEscrowsByTrade.TryGetValue(tradeId, out DealEscrow escrow) ? escrow : null;
        }

        /// <summary>MESH_TEST_TRADE_BLACKOUT `<n>:<seconds>`: the n-th commit this cell handles has its answers dropped for that long.</summary>
        private void TestTradeBlackoutOnCommit(string tradeId)
        {
            int n = ++_testCommitsHandled;
            string text = System.Environment.GetEnvironmentVariable("MESH_TEST_TRADE_BLACKOUT");
            if (string.IsNullOrEmpty(text))
                return;
            string[] parts = text.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int which) && which == n
                && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds))
            {
                _testTradeBlackouts[tradeId] = Time.unscaledTime + seconds;
                Logging.LogWarning("Mesh", $"Trade {tradeId}: every answer dropped for {seconds:0} s (MESH_TEST_TRADE_BLACKOUT {text})");
            }
        }

        /// <summary>
        /// Settles one exchange (DESIGN 1.51): committed gives the trader the other's offer, failed gives its own back;
        /// applied when the trader is registered here (its open trade ended), mailed to its account otherwise.
        /// </summary>
        private void SettleDealEscrow(DealEscrow escrow, bool committed, List<CharacterItem> received, int goldReceived, UITextKeys reason, string why)
        {
            _dealEscrowsByTrade.Remove(escrow.tradeId);
            bool open = _dealEscrows.TryGetValue(escrow.traderId, out DealEscrow attached) && ReferenceEquals(attached, escrow);
            if (open)
                _dealEscrows.Remove(escrow.traderId);
            _dealHoldLogged.Remove(escrow.traderId);
            _dealsSettled[escrow.tradeId] = Time.unscaledTime;
            BasePlayerCharacterEntity player = RegisteredPlayer(escrow.traderId);
            if (player == null)
            {
                if (committed)
                    MailToAbsent(escrow.traderUserId, escrow.traderName, "Trade completed", $"What {escrow.traderName} received in a trade that completed after it left ({why}).",
                        received, goldReceived, $"Trade {escrow.tradeId} committed for {escrow.traderName}, who is not on this cell");
                else
                    MailToAbsent(escrow.traderUserId, escrow.traderName, "Trade cancelled", $"{escrow.traderName}'s offer back from a trade that did not complete ({why}).",
                        escrow.items, escrow.goldTaken, $"Trade {escrow.tradeId} failed for {escrow.traderName}, who is not on this cell; its offer");
                return;
            }
            if (committed)
            {
                ServerLogHandlers.LogExchangeDealingItemsAndGold(player, player.DealingComponent != null ? player.DealingComponent.DealingCharacter : null, escrow.goldOffered, new DealingCharacterItems { });
                MeshDealing.Give(player, received, goldReceived, item => player.OnRewardItem(RewardGivenType.Dealing, item));
                player.OnRewardGold(RewardGivenType.Dealing, goldReceived);
                Logging.Log("Mesh", $"Trade committed here: {player.CharacterName} received {received.Count} item(s) and {goldReceived} gold across a line; its escrow is spent (trade {escrow.tradeId}{(escrow.released ? $", {Time.unscaledTime - escrow.since:0} s after the commit" : string.Empty)})");
            }
            else
            {
                MeshDealing.Give(player, escrow.items, escrow.goldTaken, null);
                Logging.LogWarning("Mesh", $"Trade escrow of {player.CharacterName} returned ({why}): {escrow.items.Count} item(s), {escrow.goldTaken} gold");
                if (open && reason != UITextKeys.NONE)
                    ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, reason);
            }
            if (open)
                player.DealingComponent?.StopDealing();
        }

        /// <summary>The escrow's cell asks the other cell about an exchange it has no answer on.</summary>
        private void SendDealQuery(DealEscrow escrow, string why)
        {
            BaseCharacterEntity other = ResolveGhostOrReal(escrow.otherMeshId);
            string tradeId = escrow.tradeId, escrowZone = OwnZoneId, traderId = escrow.traderId;
            System.Action<NetDataWriter> query = writer => { writer.Put(tradeId); writer.Put(escrowZone ?? string.Empty); writer.Put(traderId ?? string.Empty); };
            BasePlayerCharacterEntity trader = RegisteredPlayer(escrow.traderId);
            Logging.LogWarning("Mesh", $"Trade {tradeId} of {escrow.traderName}: {why}; asking the other cell");
            if (!SendToPlayer(other, GhostActionKind.DealQuery, trader, query) && !SendToPlayerOnZone(escrow.otherZone, escrow.otherMeshId, GhostActionKind.DealQuery, trader, query))
                Logging.LogWarning("Mesh", $"Trade {tradeId}: the other trader is neither here nor streamed here, and zone {escrow.otherZone ?? "?"} is not linked; asked again later");
        }

        internal bool HasDealEscrow(BasePlayerCharacterEntity trader)
        {
            return trader != null && !string.IsNullOrEmpty(trader.Id) && _dealEscrows.ContainsKey(trader.Id);
        }

        /// <summary>
        /// The trader is leaving the cell (its entity destroyed): an open exchange stays open, settled by the answer
        /// (mailed once the trader is gone; DESIGN 1.51). A puppet being destroyed has no id and no escrow.
        /// </summary>
        internal void ReleaseDealEscrowOnLeaving(BasePlayerCharacterEntity trader)
        {
            if (trader == null || string.IsNullOrEmpty(trader.Id) || !_dealEscrows.TryGetValue(trader.Id, out DealEscrow escrow))
                return;
            _dealEscrows.Remove(trader.Id);
            _dealHoldLogged.Remove(trader.Id);
            escrow.released = true;
        }

        /// <summary>Asks about an exchange every 10 s; releases its trader after a minute and asks every 30 s; gives up after 30 minutes.</summary>
        private IEnumerator DealEscrowTimeout(string tradeId)
        {
            while (true)
            {
                bool released = _dealEscrowsByTrade.TryGetValue(tradeId, out DealEscrow before) && before.released;
                yield return new WaitForSecondsRealtime(released ? DealReleasedAskSeconds : DealEscrowSeconds);
                if (!_dealEscrowsByTrade.TryGetValue(tradeId, out DealEscrow escrow))
                    yield break;
                float waited = Time.unscaledTime - escrow.since;
                if (waited >= DealGiveUpSeconds)
                {
                    Logging.LogError("Mesh", $"Trade {tradeId} of {escrow.traderName}: no answer from the other cell in {DealGiveUpSeconds:0} s; the escrow goes back");
                    SettleDealEscrow(escrow, false, null, 0, UITextKeys.UI_ERROR_DEALING_CANCELED, $"no answer from the other cell in {DealGiveUpSeconds:0} s");
                    yield break;
                }
                if (!escrow.released && waited >= DealReleaseSeconds)
                {
                    escrow.released = true;
                    if (_dealEscrows.TryGetValue(escrow.traderId, out DealEscrow open) && ReferenceEquals(open, escrow))
                        _dealEscrows.Remove(escrow.traderId);
                    _dealHoldLogged.Remove(escrow.traderId);
                    BasePlayerCharacterEntity trader = RegisteredPlayer(escrow.traderId);
                    if (trader != null && trader.DealingComponent != null)
                    {
                        ServerGameMessageHandlers.SendGameMessage(trader.ConnectionId, UITextKeys.UI_ERROR_DEALING_CANCELED);
                        trader.DealingComponent.StopDealing();
                    }
                    Logging.LogWarning("Mesh", $"Trade {tradeId} of {escrow.traderName} released after {DealReleaseSeconds:0} s without an answer: the exchange stays open, settled when the other cell answers (mailed if {escrow.traderName} has left)");
                }
                SendDealQuery(escrow, $"no answer in {waited:0} s");
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
            string tradeId = payload.GetString();
            List<CharacterItem> offered = ReadItems(payload);
            int goldOffered = payload.GetInt();
            if (target is BasePlayerCharacterEntity answering && TryGetDealDecision(answering.Id, tradeId, out DealDecision known))
            {
                Logging.LogWarning("Mesh", $"Trade commit {tradeId} for {answering.CharacterName} again: the same answer ({(known.committed ? "committed" : "failed")})");
                SendDealAnswer(answering, instigator, known, 0f);
                return;
            }
            if (!DealPair(target, instigator, out BasePlayerCharacterEntity player, out BasePlayerCharacterEntity other))
                return;
            PlayerCharacterDealingComponent dealing = player.DealingComponent;
            MeshIdentity otherIdentity = MeshIdentity.Get(other);
            string otherZone = otherIdentity != null ? otherIdentity.OwnerZoneId : null;
            float delay = TestTradeAnswerDelay();
            TestTradeBlackoutOnCommit(tradeId);
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
                DealDecision refused = RecordDealDecision(player.Id, new DealDecision { tradeId = tradeId, committed = false, reason = (ushort)forOther, otherId = MeshEntityIdOf(other), otherZone = otherZone });
                SendDealAnswer(player, instigator, refused, delay);
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
            DealDecision done = RecordDealDecision(player.Id, new DealDecision { tradeId = tradeId, committed = true, taken = taken, goldGiven = goldGiven, otherId = MeshEntityIdOf(other), otherZone = otherZone });
            Logging.Log("Mesh", $"Trade committed here: {player.CharacterName} gave {taken.Count} item(s) and {goldGiven} gold, received {offered.Count} item(s) and {goldOffered} gold from {other.CharacterName} across a line (trade {tradeId})");
            SendDealAnswer(player, instigator, done, delay);
            dealing.StopDealing();
        }

        /// <summary>This cell's answer on an exchange, to the escrow's trader (as this cell knows it, or by the zone the commit came from).</summary>
        private void SendDealAnswer(BasePlayerCharacterEntity answering, EntityInfo otherInfo, DealDecision decision, float delay)
        {
            if (delay > 0f)
            {
                Logging.LogWarning("Mesh", $"Trade {decision.tradeId}: the answer held {delay:0.#} s (MESH_TEST_TRADE_ANSWER_DELAY)");
                StartCoroutine(SendDealAnswerLater(answering, otherInfo, decision, delay));
                return;
            }
            if (_testTradeBlackouts.TryGetValue(decision.tradeId, out float blackoutUntil) && Time.unscaledTime < blackoutUntil)
            {
                Logging.LogWarning("Mesh", $"Trade {decision.tradeId}: the answer ({(decision.committed ? "committed" : "failed")}) dropped (MESH_TEST_TRADE_BLACKOUT, {blackoutUntil - Time.unscaledTime:0} s left)");
                return;
            }
            if (decision.committed && TestTradeDrop("committed", ++_testCommittedSent))
            {
                Logging.LogWarning("Mesh", $"Trade {decision.tradeId}: the answer (committed) dropped (MESH_TEST_TRADE_DROP committed{_testCommittedSent})");
                return;
            }
            GhostActionKind kind = decision.committed ? GhostActionKind.DealCommitted : GhostActionKind.DealFailed;
            System.Action<NetDataWriter> write = writer =>
            {
                if (decision.committed)
                {
                    writer.Put(decision.tradeId);
                    writer.Put(decision.taken.Count);
                    for (int i = 0; i < decision.taken.Count; ++i) writer.Put(decision.taken[i]);
                    writer.Put(decision.goldGiven);
                }
                else
                {
                    writer.Put(decision.reason);
                    writer.Put(decision.tradeId);
                }
            };
            // DESIGN 1.52: to the escrow's cell first, wherever its trader is now (a trader released or restarted away from
            // its escrow logged in on this very cell in the first rig run, and the answer, handled here, found no escrow).
            if (SendToPlayerOnZone(decision.otherZone, decision.otherId, kind, answering, write))
                return;
            BaseCharacterEntity other = null;
            if (otherInfo != null)
                otherInfo.TryGetEntity(out other);
            if (other == null)
                other = ResolveGhostOrReal(decision.otherId);
            if (other != null && !MeshIdentity.IsGhostEntity(other) && !string.IsNullOrEmpty(decision.otherZone) && !string.Equals(decision.otherZone, OwnZoneId))
            {
                Logging.LogWarning("Mesh", $"Trade {decision.tradeId}: the escrow's zone {decision.otherZone} is not linked; its cell asks again");
                return; // the trader is here but its escrow is not: handled here, the answer would find nothing
            }
            if (!SendToPlayer(other, kind, answering, write))
                Logging.LogWarning("Mesh", $"Trade {decision.tradeId}: the answer found the other trader neither here nor streamed here; its cell asks again");
        }

        private IEnumerator SendDealAnswerLater(BasePlayerCharacterEntity answering, EntityInfo otherInfo, DealDecision decision, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            SendDealAnswer(answering, otherInfo, decision, 0f);
        }

        private DealDecision RecordDealDecision(string traderId, DealDecision decision)
        {
            decision.at = Time.unscaledTime;
            if (!_dealDecisions.TryGetValue(traderId, out List<DealDecision> decisions))
                _dealDecisions[traderId] = decisions = new List<DealDecision>();
            float now = Time.unscaledTime;
            decisions.RemoveAll(d => now - d.at > DealDecisionSeconds || d.tradeId == decision.tradeId);
            decisions.Add(decision);
            return decision;
        }

        private bool TryGetDealDecision(string traderId, string tradeId, out DealDecision decision)
        {
            decision = null;
            if (string.IsNullOrEmpty(traderId) || !_dealDecisions.TryGetValue(traderId, out List<DealDecision> decisions))
                return false;
            foreach (DealDecision d in decisions)
            {
                if (d.tradeId == tradeId && Time.unscaledTime - d.at <= DealDecisionSeconds)
                {
                    decision = d;
                    return true;
                }
            }
            return false;
        }

        /// <summary>MeshTradeCarrier's part: a trader's recent answers leave with it, and arrive with it.</summary>
        internal List<DealDecision> DealDecisionsOf(string traderId)
        {
            if (string.IsNullOrEmpty(traderId) || !_dealDecisions.TryGetValue(traderId, out List<DealDecision> decisions))
                return null;
            float now = Time.unscaledTime;
            decisions.RemoveAll(d => now - d.at > DealDecisionSeconds);
            return decisions.Count > 0 ? decisions : null;
        }

        internal void ImportDealDecision(string traderId, DealDecision decision, float age)
        {
            RecordDealDecision(traderId, decision);
            decision.at = Time.unscaledTime - Mathf.Max(0f, age);
        }

        /// <summary>
        /// The escrow's cell asks about an exchange: this cell's answer again, or, when it never saw the exchange, failed
        /// from now on (its trader's trade ends: it waited at the confirm for a commit that never came).
        /// </summary>
        private void HandleDealQueryAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            string tradeId = payload.GetString();
            string escrowZone = payload.GetString(), traderId = payload.GetString();
            BasePlayerCharacterEntity player = target as BasePlayerCharacterEntity;
            AnswerDealQuery(player != null ? player.Id : target.Id, player, tradeId, instigator, NullIfEmpty(escrowZone), NullIfEmpty(traderId));
        }

        private void AnswerDealQuery(string traderId, BasePlayerCharacterEntity player, string tradeId, EntityInfo otherInfo, string replyZone, string otherMeshId)
        {
            if (!TryGetDealDecision(traderId, tradeId, out DealDecision decision))
            {
                decision = RecordDealDecision(traderId, new DealDecision { tradeId = tradeId, committed = false, reason = (ushort)UITextKeys.UI_ERROR_DEALING_CANCELED, otherId = otherMeshId, otherZone = replyZone });
                Logging.LogWarning("Mesh", $"Trade {tradeId} asked about for {(player != null ? player.CharacterName : traderId)}, whose cell never saw it: failed");
                if (player != null && player.DealingComponent != null && player.DealingComponent.DealingState != DealingState.None)
                {
                    ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, UITextKeys.UI_ERROR_DEALING_CANCELED);
                    player.DealingComponent.StopDealing();
                }
            }
            else
                Logging.Log("Mesh", $"Trade {tradeId} asked about for {(player != null ? player.CharacterName : traderId)}: the same answer ({(decision.committed ? "committed" : "failed")})");
            if (string.IsNullOrEmpty(decision.otherId))
                decision.otherId = otherMeshId;
            if (!string.IsNullOrEmpty(replyZone))
                decision.otherZone = replyZone; // the question names the escrow's cell
            SendDealAnswer(player, otherInfo, decision, 0f);
        }

        /// <summary>
        /// OnGhostAction's hook for a trade message whose target is not owned here (DESIGN 1.49): an answer for an
        /// escrow here whose trader has left (the kit keeps it for its despawn delay) is applied to it; a commit or a
        /// question for a trader who left is answered from this cell's record, or failed. True when the kind is a trade's.
        /// </summary>
        private bool HandleDealForAbsent(long connectionId, GhostActionMessage msg)
        {
            GhostActionKind kind = (GhostActionKind)msg.action;
            if (kind != GhostActionKind.DealCommitted && kind != GhostActionKind.DealFailed && kind != GhostActionKind.DealCommit && kind != GhostActionKind.DealQuery)
                return false;
            NetDataReader reader = new NetDataReader(msg.payload ?? System.Array.Empty<byte>());
            EntityInfo instigator = ResolveInstigator(msg.instigatorMeshId);
            string replyZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string fromZone) ? fromZone : null;
            if (kind == GhostActionKind.DealCommitted || kind == GhostActionKind.DealFailed)
            {
                ushort reason = kind == GhostActionKind.DealFailed ? reader.GetUShort() : (ushort)0;
                string tradeId = reader.GetString();
                DealEscrow escrow = FindDealEscrow(tradeId);
                if (escrow == null)
                {
                    if (_dealsSettled.ContainsKey(tradeId))
                        Logging.Log("Mesh", $"Trade {tradeId} {(kind == GhostActionKind.DealCommitted ? "committed" : "failed")} for {msg.targetMeshId} again (a repeated answer); settled already, nothing given");
                    else
                        Logging.LogWarning("Mesh", $"Trade {tradeId}: an answer ({kind}) for {msg.targetMeshId}, who is not here, with no escrow here; dropped");
                    return true;
                }
                Logging.LogWarning("Mesh", $"Trade {tradeId}: the answer ({kind}) reached this cell after {escrow.traderName} left it");
                if (kind == GhostActionKind.DealCommitted)
                    SettleDealEscrow(escrow, true, ReadItems(reader), reader.GetInt(), UITextKeys.NONE, "the other cell committed it");
                else
                    SettleDealEscrow(escrow, false, null, 0, (UITextKeys)reason, $"refused by the other cell: {(UITextKeys)reason}");
                return true;
            }
            string asked = reader.GetString();
            if (kind == GhostActionKind.DealQuery && reader.AvailableBytes > 0)
            {
                // The escrow's cell and its trader, from the question itself (the link it came over may be a forwarder's).
                string escrowZone = reader.GetString(), traderId = reader.GetString();
                AnswerDealQuery(msg.targetMeshId, null, asked, instigator, NullIfEmpty(escrowZone) ?? replyZone, NullIfEmpty(traderId) ?? msg.instigatorMeshId);
                return true;
            }
            AnswerDealQuery(msg.targetMeshId, null, asked, instigator, replyZone, msg.instigatorMeshId);
            return true;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>UnregisterPlayerCharacter's hook for a trader leaving otherwise than by a handoff: its exchange is asked about at once.</summary>
        private void TradeOnLeaving(BasePlayerCharacterEntity player)
        {
            if (player == null || string.IsNullOrEmpty(player.Id) || _handoffsInProgress.Contains(player.Id) || !_dealEscrows.TryGetValue(player.Id, out DealEscrow escrow))
                return;
            _dealEscrows.Remove(player.Id); // settled by the answer, mailed (the kit may destroy the character at once)
            escrow.released = true;
            SendDealQuery(escrow, "the trader left the cell with its exchange in escrow");
        }

        /// <summary>The other half is done: this trader receives the other's offer, and the escrow is spent.</summary>
        private void HandleDealCommittedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            string tradeId = payload.GetString();
            if (target is BasePlayerCharacterEntity player && player.DealingComponent != null)
                ApplyDealCommitted(player, tradeId, payload);
        }

        /// <summary>
        /// The answer committed for one exchange: its escrow is spent and the other's offer given. An answer for an exchange
        /// already settled here (a repeat after a question) changes nothing; one with no escrow at all is logged, not given.
        /// </summary>
        private void ApplyDealCommitted(BasePlayerCharacterEntity player, string tradeId, NetDataReader payload)
        {
            List<CharacterItem> received = ReadItems(payload);
            int goldReceived = payload.GetInt();
            DealEscrow escrow = FindDealEscrow(tradeId);
            if (escrow == null)
            {
                if (_dealsSettled.ContainsKey(tradeId))
                    Logging.Log("Mesh", $"Trade {tradeId} committed for {player.CharacterName} again (a repeated answer); settled already, nothing given");
                else
                    Logging.LogError("Mesh", $"Trade {tradeId} committed for {player.CharacterName} with no escrow here: the other side's {received.Count} item(s) and {goldReceived} gold are not given");
                return;
            }
            SettleDealEscrow(escrow, true, received, goldReceived, UITextKeys.NONE, "the other cell committed it");
        }

        /// <summary>The other cell refused the commit: the escrow goes back and the trade ends with the reason.</summary>
        private void HandleDealFailedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys reason = (UITextKeys)payload.GetUShort();
            string tradeId = payload.GetString();
            if (target is BasePlayerCharacterEntity player && player.DealingComponent != null)
                ApplyDealFailed(player, tradeId, reason);
        }

        private void ApplyDealFailed(BasePlayerCharacterEntity player, string tradeId, UITextKeys reason)
        {
            DealEscrow escrow = FindDealEscrow(tradeId);
            if (escrow == null)
            {
                Logging.Log("Mesh", $"Trade {tradeId} failed for {player.CharacterName} with no escrow of it here (settled already)");
                return;
            }
            SettleDealEscrow(escrow, false, null, 0, reason, $"refused by the other cell: {reason}");
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
        internal bool HoldsHandoffForTrade(BasePlayerCharacterEntity player) { return false; }
        private void TradeOnLeaving(BasePlayerCharacterEntity player) { }
        internal List<DealDecision> DealDecisionsOf(string traderId) { return null; }
        internal void ImportDealDecision(string traderId, DealDecision decision, float age) { }
        internal void ReleaseDealEscrowOnLeaving(BasePlayerCharacterEntity trader) { }
#endif
    }
}
