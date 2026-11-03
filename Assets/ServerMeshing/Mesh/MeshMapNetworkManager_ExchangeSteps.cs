// Server Meshing add-on — MeshMapNetworkManager: an exchange across a line survives a crash of either cell (DESIGN.md 1.54).
//
// Before 1.54 a cell's exchanges reached its journal only as it stopped cleanly, and the player changes they made only
// with the kit's next save: a crash lost every open exchange, and with them an escrow's gold or an offer. Now every
// step is written ahead (MeshExchangeJournal, at once and synchronously), the changed player is saved (the kit's own
// save, sent to the database server at once rather than with the kit's next round), and only once that save is
// accepted does the step's message leave for the other cell: what another cell acts on is always in the database. A
// step that changes a player sets a marker in its saved data (a server int keyed by the exchange, carried by the same
// save) and stays `pending` in the journal until its save is accepted. A restarted cell reads the marker back from the
// database for every pending record (one read per character) and keeps the step only if it reached it: an escrow that
// never did is dropped (its gold never left in the database, and its message never left), a settlement that never did
// is mailed (its exchange did happen), a word whose step never did becomes not paid or failed. A mail carries a
// reference in its text until the database accepts it; a restarted cell looks for the reference in the receiver's
// mailbox before it sends it again. Exchange messages that come while the markers are read are dropped (each is asked
// about again). The handoff of a player whose step is being saved is held (milliseconds). MESH_FAULT_CRASH_WITH_EXCHANGE
// (as MESH_FAULT_QUIT_WITH_EXCHANGE) kills the cell instead of quitting it, and MESH_FAULT_CRASH_IN_STEP
// (`<cell>:<n>:before|after`, comma-separated) kills it at its n-th step, after the journal and before the save is sent,
// or after the save is accepted and before the journal says so, for the rig.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public const string FaultCrashWithExchange = "MESH_FAULT_CRASH_WITH_EXCHANGE";
        public const string FaultCrashInStep = "MESH_FAULT_CRASH_IN_STEP";
        /// <summary>How long a mail the database refused is tried again, every 30 s.</summary>
        public const float MeshMailRetrySeconds = 1800f;

        private readonly Dictionary<string, MeshExchangeJournal.Settlement> _settlements = new Dictionary<string, MeshExchangeJournal.Settlement>();
        private readonly Dictionary<string, MeshExchangeJournal.Mail> _mailsInFlight = new Dictionary<string, MeshExchangeJournal.Mail>(); // by reference
        private readonly List<MeshExchangeJournal.Purchase> _restoredPurchases = new List<MeshExchangeJournal.Purchase>(); // pending, until resolved
        private readonly Dictionary<string, int> _stepsSaving = new Dictionary<string, int>(); // character id -> steps whose save is on its way
        private readonly List<System.Action> _afterJournal = new List<System.Action>();
        private int _journalHold, _stepsCommitted;
        private bool _journalDirty, _exchangesResolving;

        private static string SettlementKey(MeshExchangeJournal.SettlementKind kind, string exchangeId, string characterId) => $"{(byte)kind}:{exchangeId}:{characterId}";

        private static void SetStepMarker(IPlayerCharacterData player, string exchangeId, byte state)
        {
            player.SetServerInt32(MeshExchangeJournal.MarkerKey(exchangeId), state);
        }

        private static void ClearStepMarker(IPlayerCharacterData player, string exchangeId)
        {
            int index = player.IndexOfServerInt32(MeshExchangeJournal.MarkerKey(exchangeId));
            if (index >= 0)
                player.ServerInts.RemoveAt(index);
        }

        /// <summary>The journal is written now, unless a compound change holds it (then once the hold is released).</summary>
        private void JournalChanged()
        {
            if (!IsServer || !HasOwnZone)
                return;
            if (_journalHold > 0)
            {
                _journalDirty = true;
                return;
            }
            WriteExchangeJournal(false);
        }

        private void HoldJournal()
        {
            _journalHold++;
        }

        private void ReleaseJournal()
        {
            if (--_journalHold > 0)
                return;
            _journalHold = 0;
            if (_journalDirty)
            {
                _journalDirty = false;
                WriteExchangeJournal(false);
            }
            if (_afterJournal.Count == 0)
                return;
            System.Action[] after = _afterJournal.ToArray();
            _afterJournal.Clear();
            foreach (System.Action a in after)
                a();
        }

        /// <summary>The boundary tick holds a player whose step's save is on its way (milliseconds).</summary>
        internal bool HoldsHandoffForSteps(BasePlayerCharacterEntity player)
        {
            return player != null && !string.IsNullOrEmpty(player.Id) && _stepsSaving.ContainsKey(player.Id);
        }

        /// <summary>
        /// A step on `player` (its marker set and its record pending, in memory): the journal first, then the player's save,
        /// then `durable` (true when the save was accepted), which sends what the step allows. Inside a held compound change
        /// the save starts once the hold is released and the journal written.
        /// </summary>
        private void CommitStep(BasePlayerCharacterEntity player, string what, System.Action<bool> durable)
        {
            if (player == null)
            {
                durable?.Invoke(false);
                return;
            }
            int n = ++_stepsCommitted;
            string id = player.Id;
            _stepsSaving[id] = _stepsSaving.TryGetValue(id, out int saving) ? saving + 1 : 1;
            System.Action start = () =>
            {
                PlayerCharacterData data = player.CloneTo(new PlayerCharacterData()); // after the whole held change
                CrashInStep(n, "before", what);
                SaveStep(id, data, n, what, durable).Forget();
            };
            if (_journalHold > 0)
            {
                _journalDirty = true;
                _afterJournal.Add(start);
                return;
            }
            WriteExchangeJournal(false);
            start();
        }

        private async UniTaskVoid SaveStep(string characterId, PlayerCharacterData data, int n, string what, System.Action<bool> durable)
        {
            bool saved = await WaitAndSaveCharacter(TransactionUpdateCharacterState.All, data);
            if (_stepsSaving.TryGetValue(characterId, out int saving))
            {
                if (saving <= 1)
                    _stepsSaving.Remove(characterId);
                else
                    _stepsSaving[characterId] = saving - 1;
            }
            if (!saved)
                Logging.LogError("Mesh", $"Exchange step {n} ({what}): the save of {data.CharacterName} was not accepted; the step stays pending in the journal (a restart asks the database), and goes on");
            CrashInStep(n, "after", what);
            durable?.Invoke(saved);
        }

        /// <summary>A settling step whose save was accepted: its record goes, and its marker with the player's next save.</summary>
        private void SettlementDurable(MeshExchangeJournal.Settlement s, bool saved)
        {
            if (!saved)
                return; // stays in the journal: a restart reads the marker
            if (_settlements.Remove(SettlementKey(s.kind, s.exchangeId, s.characterId)))
                JournalChanged();
            BasePlayerCharacterEntity player = RegisteredPlayer(s.characterId);
            if (player != null)
                ClearStepMarker(player, s.exchangeId);
        }

        /// <summary>Records a settling step on a registered player (its marker set) and commits it.</summary>
        private MeshExchangeJournal.Settlement Settle(MeshExchangeJournal.SettlementKind kind, BasePlayerCharacterEntity player, string exchangeId, byte state,
            string otherName, IList<CharacterItem> items, int gold, string what, System.Action<bool> durable = null)
        {
            MeshExchangeJournal.Settlement s = new MeshExchangeJournal.Settlement
            {
                kind = kind, exchangeId = exchangeId, characterId = player.Id, userId = player.UserId, characterName = player.CharacterName,
                otherName = otherName, state = state, gold = gold,
            };
            if (items != null)
                foreach (CharacterItem item in items)
                    s.items.Add(item.Clone(false));
            _settlements[SettlementKey(kind, exchangeId, player.Id)] = s;
            SetStepMarker(player, exchangeId, state);
            CommitStep(player, what, saved =>
            {
                SettlementDurable(s, saved);
                durable?.Invoke(saved);
            });
            return s;
        }

        /// <summary>MESH_FAULT_CRASH_IN_STEP `<cell>:<n>:before|after`: the cell dies at its n-th step, once per run.</summary>
        private void CrashInStep(int n, string when, string what)
        {
            string toggle = MeshFault.Toggle(FaultCrashInStep);
            if (toggle == null || !HasOwnZone)
                return;
            foreach (string part in toggle.Split(','))
            {
                string[] p = part.Trim().Split(':');
                if (p.Length == 3 && MeshFault.NamesCell(p[0], OwnZoneId) && int.TryParse(p[1], out int which) && which == n
                    && string.Equals(p[2], when, System.StringComparison.OrdinalIgnoreCase))
                    MeshFault.FireOnce(FaultCrashInStep, OwnZoneId, $"exchange step {n} ({what}) {(when == "before" ? "before its save" : "after its save, before the journal")}");
            }
        }

        /// <summary>The kinds of exchange message dropped while a restarted cell reads its markers back (each is asked about again).</summary>
        private static bool IsExchangeMessage(ushort action)
        {
            switch ((GhostActionKind)action)
            {
                case GhostActionKind.DealCommit:
                case GhostActionKind.DealCommitted:
                case GhostActionKind.DealFailed:
                case GhostActionKind.DealQuery:
                case GhostActionKind.VendingBuy:
                case GhostActionKind.VendingSold:
                case GhostActionKind.VendingFailed:
                case GhostActionKind.VendingDone:
                case GhostActionKind.VendingQuery:
                    return true;
            }
            return false;
        }

        /// <summary>OnGhostAction's guard: true when the message is dropped because the restored exchanges are still being resolved.</summary>
        private bool DropWhileResolving(GhostActionMessage msg)
        {
            if (!_exchangesResolving || !IsExchangeMessage(msg.action))
                return false;
            WarnThrottled($"Exchange message {(GhostActionKind)msg.action} for {msg.targetMeshId} dropped: the restored exchanges are being resolved (it is asked about again)");
            return true;
        }

        // ---- the restart: what the pending records' markers say ----

        /// <summary>
        /// The markers of every pending record, read from the database (one read per character), then applied: escrows
        /// and sales kept or dropped, settlements kept, mailed or turned into words, purchases refunded or dropped; then
        /// the mails not known to be accepted, looked for in their mailboxes.
        /// </summary>
        private async UniTaskVoid ResolveRestoredExchanges()
        {
            Dictionary<string, PlayerCharacterData> characters = new Dictionary<string, PlayerCharacterData>();
            HashSet<string> unread = new HashSet<string>();
            async UniTask Read(string characterId, string userId)
            {
                if (string.IsNullOrEmpty(characterId) || characters.ContainsKey(characterId) || unread.Contains(characterId))
                    return;
                DatabaseApiResult<CharacterResp> resp = await DatabaseClient.GetCharacterAsync(new GetCharacterReq { CharacterId = characterId, UserId = userId });
                PlayerCharacterData data = resp.IsSuccess ? resp.Response.CharacterData : null;
                if (data != null)
                    characters[characterId] = data;
                else
                    unread.Add(characterId);
            }
            foreach (DealEscrow e in new List<DealEscrow>(_dealEscrowsByTrade.Values))
                if (e.pending)
                    await Read(e.traderId, e.traderUserId);
            foreach (PendingSale s in new List<PendingSale>(_pendingSales.Values))
                if (s.pending)
                    await Read(s.vendorId, s.vendorUserId);
            foreach (MeshExchangeJournal.Purchase p in new List<MeshExchangeJournal.Purchase>(_restoredPurchases))
                await Read(p.buyerId, p.buyerUserId);
            foreach (MeshExchangeJournal.Settlement s in new List<MeshExchangeJournal.Settlement>(_settlements.Values))
                await Read(s.characterId, s.userId);

            // Unread (the database did not answer): the record is taken as it was written, as before 1.54.
            byte Landed(string characterId, string exchangeId, byte recorded)
            {
                if (characters.TryGetValue(characterId ?? string.Empty, out PlayerCharacterData data))
                    return (byte)data.GetServerInt32(MeshExchangeJournal.MarkerKey(exchangeId));
                Logging.LogError("Mesh", $"Exchange {exchangeId}: the saved data of {characterId} could not be read; its step is taken as written");
                return recorded;
            }

            int kept = 0, dropped = 0, settled = 0;
            HoldJournal();
            foreach (DealEscrow e in new List<DealEscrow>(_dealEscrowsByTrade.Values))
            {
                if (!e.pending)
                    continue;
                if (MeshExchangeJournal.KeepsOpen(true, Landed(e.traderId, e.tradeId, MeshExchangeJournal.StepTaken)))
                {
                    e.pending = false;
                    StartCoroutine(DealEscrowTimeout(e.tradeId));
                    kept++;
                }
                else
                {
                    _dealEscrowsByTrade.Remove(e.tradeId);
                    Logging.LogWarning("Mesh", $"Trade {e.tradeId} of {e.traderName}: its escrow never reached the database (the commit was never sent); dropped");
                    dropped++;
                }
            }
            foreach (PendingSale s in new List<PendingSale>(_pendingSales.Values))
            {
                if (!s.pending)
                    continue;
                if (MeshExchangeJournal.KeepsOpen(true, Landed(s.vendorId, s.saleId, MeshExchangeJournal.StepTaken)))
                {
                    s.pending = false;
                    StartCoroutine(WatchPendingSale(s.saleId));
                    kept++;
                }
                else
                {
                    _pendingSales.Remove(s.saleId);
                    Logging.LogWarning("Mesh", $"Vending: sale {s.saleId} of {s.vendorName} to {s.buyerName} never reached the database (the item never left, the answer was never sent); dropped");
                    dropped++;
                }
            }
            foreach (MeshExchangeJournal.Purchase p in new List<MeshExchangeJournal.Purchase>(_restoredPurchases))
            {
                _restoredPurchases.Remove(p);
                if (MeshExchangeJournal.KeepsOpen(true, Landed(p.buyerId, p.saleId, MeshExchangeJournal.StepTaken)))
                {
                    RefundRestoredPurchase(p);
                    kept++;
                }
                else
                {
                    Logging.LogWarning("Mesh", $"Vending: purchase {p.saleId} of {p.buyerName} never reached the database (its gold never left, the purchase was never sent); dropped");
                    dropped++;
                }
            }
            foreach (MeshExchangeJournal.Settlement s in new List<MeshExchangeJournal.Settlement>(_settlements.Values))
            {
                _settlements.Remove(SettlementKey(s.kind, s.exchangeId, s.characterId));
                ApplyResolved(s, MeshExchangeJournal.Resolve(s.kind, s.state, Landed(s.characterId, s.exchangeId, s.state)));
                settled++;
            }
            _exchangesResolving = false;
            ReleaseJournal();
            Logging.Log("Mesh", $"Exchanges resolved against the database: {kept} kept, {dropped} dropped (never reached it), {settled} settling step(s) applied; {_mailsInFlight.Count} mail(s) looked for");
            foreach (MeshExchangeJournal.Mail m in new List<MeshExchangeJournal.Mail>(_mailsInFlight.Values))
                ResendUnlessDelivered(m).Forget();
        }

        private void ApplyResolved(MeshExchangeJournal.Settlement s, MeshExchangeJournal.Outcome outcome)
        {
            string label = $"{s.kind} of {s.characterName} on {s.exchangeId}";
            switch (outcome)
            {
                case MeshExchangeJournal.Outcome.Landed:
                    Logging.Log("Mesh", $"Exchange step {label}: reached the database");
                    if (s.kind == MeshExchangeJournal.SettlementKind.TradeSettled)
                        _dealsSettled[s.exchangeId] = UnityEngine.Time.unscaledTime;
                    break;
                case MeshExchangeJournal.Outcome.MailIntended:
                    Logging.LogWarning("Mesh", $"Exchange step {label}: never reached the database; what it gave goes by mail");
                    if (s.kind == MeshExchangeJournal.SettlementKind.TradeSettled)
                    {
                        _dealsSettled[s.exchangeId] = UnityEngine.Time.unscaledTime;
                        bool committed = s.state == MeshExchangeJournal.StepDelivered;
                        MailToAbsent(s.userId, s.characterName, committed ? "Trade completed" : "Trade cancelled",
                            committed ? $"What {s.characterName} received in a trade that completed as the server restarted." : $"{s.characterName}'s offer back from a trade that did not complete before the server restarted.",
                            s.items, s.gold, $"Trade {s.exchangeId} {(committed ? "committed" : "failed")} for {s.characterName} after the restart");
                    }
                    else if (s.state == MeshExchangeJournal.StepDelivered)
                        MailToAbsent(s.userId, s.characterName, "Shop sale", $"The gold for an item {s.characterName}'s shop sold to {s.otherName}, as the server restarted.",
                            null, s.gold, $"Vending: sale {s.exchangeId} paid for {s.characterName} after the restart");
                    else
                        MailToAbsent(s.userId, s.characterName, "Shop item back", $"The item {s.characterName}'s shop sold to {s.otherName}, back: it did not reach the buyer before the server restarted.",
                            s.items, 0, $"Vending: sale {s.exchangeId} not delivered for {s.characterName} after the restart; its item");
                    break;
                case MeshExchangeJournal.Outcome.RefundByMail:
                    Logging.LogWarning("Mesh", $"Exchange step {label}: never reached the database, the purchase is still in escrow there");
                    RefundRestoredPurchase(new MeshExchangeJournal.Purchase
                    {
                        saleId = s.exchangeId, buyerId = s.characterId, buyerUserId = s.userId, buyerName = s.characterName, vendorName = s.otherName, gold = s.gold,
                    });
                    break;
                case MeshExchangeJournal.Outcome.WordNotPaid:
                    Logging.LogWarning("Mesh", $"Exchange step {label}: never reached the database; the word on the sale is not paid");
                    RecordSaleDecision(s.characterId, s.exchangeId, false);
                    break;
                case MeshExchangeJournal.Outcome.WordFailed:
                    Logging.LogWarning("Mesh", $"Exchange step {label}: never reached the database; the word on the trade is failed");
                    TryGetDealDecision(s.characterId, s.exchangeId, out DealDecision was);
                    RecordDealDecision(s.characterId, new DealDecision
                    {
                        tradeId = s.exchangeId, committed = false, reason = (ushort)UITextKeys.UI_ERROR_DEALING_CANCELED,
                        otherId = was != null ? was.otherId : null, otherZone = was != null ? was.otherZone : null,
                    });
                    break;
            }
        }

        /// <summary>A restored purchase still in escrow in the database: from now on its sale is refused here, and the price goes back by mail.</summary>
        private void RefundRestoredPurchase(MeshExchangeJournal.Purchase p)
        {
            HoldJournal();
            RecordSaleDecision(p.buyerId, p.saleId, false);
            MailToAbsent(p.buyerUserId, p.buyerName, "Purchase cancelled", $"The gold for a purchase from {p.vendorName}'s shop that did not complete before the server restarted.",
                null, p.gold, $"Vending: purchase {p.saleId} of {p.buyerName} refunded after the restart");
            ReleaseJournal();
        }

        /// <summary>A mail the journal says was not accepted: sent again unless its reference is in the receiver's mailbox.</summary>
        private async UniTaskVoid ResendUnlessDelivered(MeshExchangeJournal.Mail m)
        {
            DatabaseApiResult<MailListResp> list = await DatabaseClient.MailListAsync(new MailListReq { UserId = m.userId, OnlyNewMails = false });
            if (list.IsSuccess && list.Response.List != null)
            {
                foreach (MailListEntry entry in list.Response.List)
                {
                    if (entry.SenderName != MeshMailSender || entry.Title != m.title)
                        continue;
                    DatabaseApiResult<GetMailResp> got = await DatabaseClient.GetMailAsync(new GetMailReq { MailId = entry.Id, UserId = m.userId });
                    if (got.IsSuccess && got.Response.Mail != null && got.Response.Mail.Content != null && got.Response.Mail.Content.Contains(m.reference))
                    {
                        Logging.Log("Mesh", $"{m.what}: the mail to {m.characterName} (reference {m.reference}) reached the database before the restart; not sent again");
                        if (_mailsInFlight.Remove(m.reference))
                            JournalChanged();
                        return;
                    }
                }
            }
            else
                Logging.LogWarning("Mesh", $"{m.what}: the mailbox of {m.characterName} could not be read; the mail (reference {m.reference}) is sent again");
            Logging.LogWarning("Mesh", $"{m.what}: the mail to {m.characterName} (reference {m.reference}) never reached the database; sent again");
            SendMeshMail(m).Forget();
        }
#else
        internal bool HoldsHandoffForSteps(BasePlayerCharacterEntity player) { return false; }
#endif
    }
}
