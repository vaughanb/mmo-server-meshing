// Server Meshing add-on — MeshMapNetworkManager: open exchanges survive a cell's clean stop (DESIGN.md 1.52).
//
// An exchange across a line (a trade's escrow, a purchase's escrow, a vendor's pending sale) and the answers a cell gave
// live in this process's memory. A cell does not sleep (HandleSleepMessage) or retire after a merge (the drain gate and
// its own exit) while it holds an open exchange: they settle in milliseconds, and a released one when its link is back.
// A cell stopped all the same (an operator, a deploy) writes them to its journal (MeshExchangeJournal) as it quits,
// after the kit's quit path has saved every player, and its next process reads them back: escrows and sales are asked
// about again and settled by mail to players who are gone, a purchase in escrow is refunded by mail (its item never
// reached the buyer), and the recorded answers are answered again. MESH_FAULT_QUIT_WITH_EXCHANGE (`<cell>:<seconds>`,
// comma-separated for several cells) makes the cell quit cleanly once an exchange has been open that long, once per run
// (a fault marker), for the rig.
//
// DESIGN 1.53: a cell with only answers (no open exchange) may sleep; the cell that waits for one of them asks the central
// to wake it when its link is down (WakeToAsk), and the woken cell answers from the journal it wrote as it went to sleep.
//
// DESIGN 1.54: the journal is written ahead of every step (MeshMapNetworkManager_ExchangeSteps), not only at the quit; a
// restarted cell reads it as before, and its pending records are resolved against the database before they are acted on.
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Folder of the cells' exchange journals (DESIGN 1.52); empty: <persistentDataPath>/ServerMeshing. Shared by the processes of a cell that restart on this host.")]
        public string exchangeJournalFolder = string.Empty;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public const string FaultQuitWithExchange = "MESH_FAULT_QUIT_WITH_EXCHANGE";
        private bool _exchangesRestored;

        /// <summary>Open exchanges on this cell: trade escrows (open or released), purchases in escrow, sales waiting for a word.</summary>
        internal int OpenExchangeCount => _dealEscrowsByTrade.Count + _vendingEscrows.Count + _pendingSales.Count
            + _mailsInFlight.Count + _restoredPurchases.Count; // a settling step is the journal's to resolve, and does not keep a cell up

        private string ExchangeJournalPath()
        {
            string folder = string.IsNullOrEmpty(exchangeJournalFolder) ? Path.Combine(Application.persistentDataPath, "ServerMeshing") : exchangeJournalFolder;
            return Path.Combine(folder, MeshExchangeJournal.FileName(OwnMapId, OwnZoneId));
        }

        /// <summary>The kit's quit path has saved every player by now (MMOServerInstance's wantsToQuit): the open exchanges go to the journal.</summary>
        protected override void OnApplicationQuit()
        {
            if (IsServer && HasOwnZone)
                WriteExchangeJournal(true);
            base.OnApplicationQuit();
        }

        private void WriteExchangeJournal(bool quitting)
        {
            string path;
            try
            {
                path = ExchangeJournalPath();
            }
            catch (System.Exception)
            {
                return;
            }
            MeshExchangeJournal journal = BuildExchangeJournal();
            try
            {
                if (journal.IsEmpty)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                NetDataWriter writer = new NetDataWriter();
                journal.Write(writer);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, writer.CopyData());
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                if (quitting)
                    Logging.Log("Mesh", $"Exchanges journal written as the cell stops: {journal.trades.Count} trade escrow(s), {journal.purchases.Count} purchase(s), {journal.sales.Count} sale(s), {journal.settlements.Count} settling step(s), {journal.mails.Count} mail(s), {journal.tradeWords.Count + journal.saleWords.Count} answer(s) -> {path}");
            }
            catch (System.Exception e)
            {
                Logging.LogError("Mesh", $"Exchanges journal not written ({e.Message}): {journal.Open} open exchange(s) are lost with this process");
            }
        }

        private MeshExchangeJournal BuildExchangeJournal()
        {
            float now = Time.unscaledTime;
            MeshExchangeJournal journal = new MeshExchangeJournal { mapId = OwnMapId, cellId = OwnZoneId };
            foreach (DealEscrow e in _dealEscrowsByTrade.Values)
            {
                MeshExchangeJournal.Trade t = new MeshExchangeJournal.Trade
                {
                    tradeId = e.tradeId, traderId = e.traderId, traderUserId = e.traderUserId, traderName = e.traderName,
                    otherMeshId = e.otherMeshId, otherZone = e.otherZone, goldTaken = e.goldTaken, goldOffered = e.goldOffered, age = now - e.since, pending = e.pending,
                };
                t.items.AddRange(e.items);
                journal.trades.Add(t);
            }
            foreach (KeyValuePair<string, VendingEscrow> kv in _vendingEscrows)
            {
                BasePlayerCharacterEntity buyer = kv.Value.buyer;
                journal.purchases.Add(new MeshExchangeJournal.Purchase
                {
                    saleId = kv.Value.saleId, buyerId = kv.Key, buyerUserId = buyer != null ? buyer.UserId : string.Empty,
                    buyerName = buyer != null ? buyer.CharacterName : kv.Key, vendorName = kv.Value.vendorName, gold = kv.Value.gold, pending = kv.Value.pending,
                });
            }
            journal.purchases.AddRange(_restoredPurchases); // restored, pending, not yet resolved
            foreach (PendingSale s in _pendingSales.Values)
                journal.sales.Add(new MeshExchangeJournal.Sale
                {
                    saleId = s.saleId, vendorId = s.vendorId, vendorUserId = s.vendorUserId, vendorName = s.vendorName, buyerId = s.buyerId,
                    buyerName = s.buyerName, buyerZone = s.buyerZone, item = s.item, price = s.price, age = now - s.since, pending = s.pending,
                });
            journal.settlements.AddRange(_settlements.Values);
            journal.mails.AddRange(_mailsInFlight.Values);
            foreach (KeyValuePair<string, List<SaleDecision>> kv in _saleDecisions)
                foreach (SaleDecision d in kv.Value)
                    if (now - d.at <= VendingDecisionSeconds)
                        journal.saleWords.Add(new MeshExchangeJournal.SaleWord { buyerId = kv.Key, saleId = d.saleId, paid = d.paid, age = now - d.at });
            foreach (KeyValuePair<string, List<DealDecision>> kv in _dealDecisions)
                foreach (DealDecision d in kv.Value)
                {
                    if (now - d.at > DealDecisionSeconds)
                        continue;
                    MeshExchangeJournal.TradeWord w = new MeshExchangeJournal.TradeWord
                    {
                        traderId = kv.Key, tradeId = d.tradeId, otherId = d.otherId, otherZone = d.otherZone, committed = d.committed,
                        reason = d.reason, goldGiven = d.goldGiven, age = now - d.at,
                    };
                    w.taken.AddRange(d.taken);
                    journal.tradeWords.Add(w);
                }
            foreach (KeyValuePair<string, float> kv in _dealsSettled)
                if (now - kv.Value <= DealDecisionSeconds)
                    journal.settledTrades.Add(kv.Key);
            return journal;
        }

        /// <summary>Once the cell knows its zone: the journal its last process wrote, if any, is read back and taken up.</summary>
        private void RestoreExchangesOnce()
        {
            if (_exchangesRestored || !HasOwnZone)
                return;
            _exchangesRestored = true;
            string path;
            try
            {
                path = ExchangeJournalPath();
                if (!File.Exists(path))
                    return;
            }
            catch (System.Exception)
            {
                return;
            }
            MeshExchangeJournal journal;
            try
            {
                // DESIGN 1.54: the file stays: it is rewritten from what is taken up, so a crash meanwhile reads it again.
                journal = MeshExchangeJournal.Read(new NetDataReader(File.ReadAllBytes(path)));
                File.Copy(path, path + ".restored", true);
            }
            catch (System.Exception e)
            {
                Logging.LogError("Mesh", $"Exchanges journal {path} unreadable ({e.Message}); its exchanges are not taken up");
                return;
            }
            if (journal == null)
            {
                Logging.LogError("Mesh", $"Exchanges journal {path} is of another version; its exchanges are not taken up");
                return;
            }
            float now = Time.unscaledTime;
            HoldJournal();
            foreach (string id in journal.settledTrades)
                _dealsSettled[id] = now;
            foreach (MeshExchangeJournal.TradeWord w in journal.tradeWords)
            {
                DealDecision d = new DealDecision { tradeId = w.tradeId, otherId = w.otherId, otherZone = w.otherZone, committed = w.committed, reason = w.reason, goldGiven = w.goldGiven };
                d.taken.AddRange(w.taken);
                ImportDealDecision(w.traderId, d, w.age);
            }
            foreach (MeshExchangeJournal.SaleWord w in journal.saleWords)
                ImportSaleDecision(w.buyerId, w.saleId, w.paid, w.age);
            foreach (MeshExchangeJournal.Trade t in journal.trades)
            {
                DealEscrow escrow = new DealEscrow
                {
                    tradeId = t.tradeId, traderId = t.traderId, traderUserId = t.traderUserId, traderName = t.traderName, released = true,
                    otherMeshId = t.otherMeshId, otherZone = t.otherZone, goldTaken = t.goldTaken, goldOffered = t.goldOffered, since = now - t.age, pending = t.pending,
                };
                escrow.items.AddRange(t.items);
                _dealEscrowsByTrade[escrow.tradeId] = escrow;
                if (!escrow.pending)
                    StartCoroutine(DealEscrowTimeout(escrow.tradeId));
            }
            foreach (MeshExchangeJournal.Sale s in journal.sales)
            {
                _pendingSales[s.saleId] = new PendingSale
                {
                    saleId = s.saleId, vendorId = s.vendorId, vendorUserId = s.vendorUserId, vendorName = s.vendorName, buyerId = s.buyerId,
                    buyerName = s.buyerName, buyerZone = s.buyerZone, item = s.item, price = s.price, since = now - s.age, released = true, pending = s.pending,
                };
                if (!s.pending)
                    StartCoroutine(WatchPendingSale(s.saleId));
            }
            foreach (MeshExchangeJournal.Purchase p in journal.purchases)
            {
                // The item never reached the buyer on this cell: from now on its sale is refused here, and the price goes
                // back. A pending one waits for its marker (DESIGN 1.54): its gold may never have left in the database.
                if (p.pending)
                    _restoredPurchases.Add(p);
                else
                    RefundRestoredPurchase(p);
            }
            foreach (MeshExchangeJournal.Settlement st in journal.settlements)
                _settlements[SettlementKey(st.kind, st.exchangeId, st.characterId)] = st;
            foreach (MeshExchangeJournal.Mail m in journal.mails)
                _mailsInFlight[m.reference] = m;
            int pending = _restoredPurchases.Count + journal.settlements.Count;
            foreach (MeshExchangeJournal.Trade t in journal.trades)
                if (t.pending)
                    pending++;
            foreach (MeshExchangeJournal.Sale s in journal.sales)
                if (s.pending)
                    pending++;
            _exchangesResolving = pending > 0 || journal.mails.Count > 0;
            ReleaseJournal();
            Logging.Log("Mesh", $"Exchanges journal taken up: {journal.trades.Count} trade escrow(s) and {journal.sales.Count} sale(s) asked about again, {journal.purchases.Count} purchase(s) refunded, {journal.tradeWords.Count + journal.saleWords.Count} answer(s) kept, {pending} pending record(s) and {journal.mails.Count} mail(s) to resolve (from {path})");
            if (_exchangesResolving)
                ResolveRestoredExchanges().Forget();
        }

        /// <summary>The per-frame part: the journal taken up once the zone is known, and the rig's clean-quit fault.</summary>
        private void ExchangeJournalTick()
        {
            if (!IsServer)
                return;
            RestoreExchangesOnce();
            FaultWithExchange(FaultQuitWithExchange);
            FaultWithExchange(FaultCrashWithExchange); // DESIGN 1.54: a kill instead of the quit
        }

        private void FaultWithExchange(string key)
        {
            string toggle = MeshFault.Toggle(key);
            if (toggle == null || !HasOwnZone || OpenExchangeCount == 0)
                return;
            foreach (string part in toggle.Split(','))
            {
                int colon = part.LastIndexOf(':');
                if (colon <= 0 || !MeshFault.NamesCell(part.Substring(0, colon), OwnZoneId)
                    || !float.TryParse(part.Substring(colon + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds))
                    continue;
                float oldest = 0f, now = Time.unscaledTime;
                foreach (DealEscrow e in _dealEscrowsByTrade.Values)
                    oldest = Mathf.Max(oldest, now - e.since);
                foreach (VendingEscrow e in _vendingEscrows.Values)
                    oldest = Mathf.Max(oldest, now - e.since);
                foreach (PendingSale s in _pendingSales.Values)
                    oldest = Mathf.Max(oldest, now - s.since);
                if (oldest >= seconds)
                {
                    if (key == FaultCrashWithExchange)
                        MeshFault.FireOnce(key, OwnZoneId, $"an exchange open {oldest:0} s");
                    else
                        MeshFault.QuitOnce(key, OwnZoneId, $"an exchange open {oldest:0} s");
                }
                return;
            }
        }

        /// <summary>A cell this one waits on for an answer is not linked: it may be asleep; the central is asked to wake it (throttled).</summary>
        private void WakeToAsk(string zone)
        {
            if (string.IsNullOrEmpty(zone) || string.Equals(zone, OwnZoneId) || world == null)
                return;
            MeshCell cell = world.GetCellById(zone);
            if (cell == null)
                return;
            Logging.Log("Mesh", $"Exchange: zone {zone} is not linked; asking the central to wake it for its answer");
            RequestWake(cell);
        }
#else
        internal int OpenExchangeCount => 0;
        private void ExchangeJournalTick() { }
#endif
    }
}
