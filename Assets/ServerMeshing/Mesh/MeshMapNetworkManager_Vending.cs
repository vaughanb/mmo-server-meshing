// Server Meshing add-on — MeshMapNetworkManager: a player's shop bought from across a line (DESIGN.md 1.46).
//
// The other cell's half of what MeshPlayerCharacterVendingComponent forwards. On the vendor's cell: a buyer across the
// line opened the shop (kept as a remote customer and sent the items), closed it, or bought an entry (the vendor's half
// of the sale, answered with the item or the reason). On the buyer's cell: the items for the puppet of the vendor and
// its customers here, the item bought, or the refusal. The buyer's escrow (the price, out of its gold while the
// vendor's cell does its half) lives here, given back on a refusal or after 10 s without an answer.
//
// DESIGN 1.47: the buyer is not handed off while its escrow waits (the boundary tick holds it, at most the 10 s), so
// the answer finds it where the escrow is; before, a buyer who crossed a line meanwhile lost the price or the item. An
// answer that comes after the timeout and after a hop is passed on to the buyer's new cell (or handled here when the
// buyer has moved onto the vendor's own cell), where it is paid for again. MESH_TEST_VENDING_ANSWER_DELAY (seconds,
// comma-separated, one per sale, the last repeated) delays the vendor's cell's answers, for regress.py market.
//
// DESIGN 1.48: every purchase has an id, the buyer's cell's, which the vendor's cell keeps as the sale's id and every
// answer carries: an answer for an earlier purchase never spends or returns a later one's escrow.
// The buyer's cell decides whether a sale happened, and the vendor is paid only on its word. The vendor's
// cell takes the item when it sells and keeps the sale pending (the vendor's handoff is held meanwhile); the buyer's
// cell gives the item and answers VendingDone paid, or answers not paid (the buyer left, cannot pay any more, or the
// cell had already told the vendor's cell no item came), and the item goes back to the vendor's bag. The decision is
// recorded by sale id for the buyer and crosses with it (the vending carrier), so a question about the same sale gets
// the same answer. Without an answer in 15 s the vendor's cell asks (VendingQuery), again every 15 s; a buyer's cell
// that never saw the item says not paid and refuses it from then on; after 75 s the sale is settled as paid. A buyer
// who leaves while its purchase is on its way gets its escrow back before the kit saves it, and a vendor who leaves is
// paid for its pending sales. A sale's answer, a question or the word for a player who has moved on is passed to its
// new owner (at most 3 times). With no trace of the player here (a buyer who logged out is streamed nowhere), a sale's
// message goes to the cell it came from: the vendor's cell keeps the buyer's cell of each sale, and the buyer's cell
// answers the cell that asked. MESH_TEST_VENDING_DROP (comma-separated `sold<n>`, `done<n>`) drops this cell's n-th
// answer or word, as a link that went down would.
//
// DESIGN 1.51: a sale has no deadline any more. After a minute without the buyer's cell's word the vendor is released
// (its handoff no longer held) while the sale stays open, asked about every 30 s; its settlement (the word, or 30
// minutes without one: paid) is applied to the vendor if it is registered here and mailed to its account otherwise
// (MeshMapNetworkManager_Delivery): a vendor who leaves no longer gets paid up front for a sale whose item may come
// back. MESH_TEST_VENDING_BLACKOUT (`<n>:<seconds>`) drops every word about the sale of this cell's n-th word for that
// long, as a link down for a minute would.
using System.Collections;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>A buyer's cell's word on one sale (DESIGN 1.48): carried with the buyer by the vending carrier.</summary>
        internal struct SaleDecision
        {
            public string saleId;
            public bool paid;
            public float at;
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long a buyer's escrow waits for the vendor's cell before the gold goes back.</summary>
        public const float VendingEscrowSeconds = 10f;
        /// <summary>How long the vendor's cell waits for the buyer's cell's word on a sale before it asks, and between asks.</summary>
        public const float VendingSaleAskSeconds = 15f;
        /// <summary>After this long without a word the vendor is released, the sale left open (DESIGN 1.51).</summary>
        public const float VendingSaleReleaseSeconds = 60f;
        /// <summary>How often a released sale is asked about.</summary>
        public const float VendingSaleReleasedAskSeconds = 30f;
        /// <summary>After this long without a word the sale is settled as paid (the item has left the vendor; logged as an error).</summary>
        public const float VendingSaleGiveUpSeconds = 1800f;
        /// <summary>How long the buyer's cell remembers its word on a sale.</summary>
        public const float VendingDecisionSeconds = 600f;
        /// <summary>How many times a sale's message is passed on after the player it is for.</summary>
        private const byte VendingMaxForwards = 3;

        private sealed class VendingEscrow
        {
            public BasePlayerCharacterEntity buyer;
            public int gold;
            public string vendorName;
            public string saleId;
        }

        private sealed class PendingSale
        {
            public string saleId, vendorId, vendorUserId, vendorName, buyerId, buyerName, buyerZone;
            public bool released; // the vendor's handoff no longer waits for it
            public CharacterItem item;
            public int price;
            public float since;
        }

        private readonly Dictionary<string, VendingEscrow> _vendingEscrows = new Dictionary<string, VendingEscrow>();
        private readonly HashSet<string> _vendingHoldLogged = new HashSet<string>();
        private readonly Dictionary<string, PendingSale> _pendingSales = new Dictionary<string, PendingSale>();
        private readonly Dictionary<string, List<SaleDecision>> _saleDecisions = new Dictionary<string, List<SaleDecision>>(); // buyer id
        private float[] _testVendingAnswerDelays;
        private int _testVendingAnswers, _testSoldSent, _testDoneSent;
        private HashSet<string> _testVendingDrops;
        private readonly Dictionary<string, float> _testVendingBlackouts = new Dictionary<string, float>(); // sale id -> until

        /// <summary>
        /// The boundary tick holds the handoff of a buyer whose purchase is on its way (DESIGN 1.47), and of a vendor whose
        /// sale waits for the buyer's cell's word (DESIGN 1.48).
        /// </summary>
        internal bool HoldsHandoffForVending(BasePlayerCharacterEntity player)
        {
            if (player == null || string.IsNullOrEmpty(player.Id))
                return false;
            if (_vendingEscrows.TryGetValue(player.Id, out VendingEscrow escrow))
            {
                if (_vendingHoldLogged.Add(player.Id))
                    Logging.Log("Mesh", $"Handoff of {player.CharacterName} held: a purchase from {escrow.vendorName}'s shop is on its way (at most {VendingEscrowSeconds:0} s)");
                return true;
            }
            foreach (PendingSale sale in _pendingSales.Values)
            {
                if (sale.vendorId != player.Id || sale.released)
                    continue;
                if (_vendingHoldLogged.Add(player.Id))
                    Logging.Log("Mesh", $"Handoff of {player.CharacterName} held: its sale to {sale.buyerName} waits for the buyer's cell (at most {VendingSaleReleaseSeconds:0} s)");
                return true;
            }
            _vendingHoldLogged.Remove(player.Id);
            return false;
        }

        private void EndVendingEscrow(string buyerId)
        {
            _vendingEscrows.Remove(buyerId);
            _vendingHoldLogged.Remove(buyerId);
        }

        private float TestVendingAnswerDelay()
        {
            if (_testVendingAnswerDelays == null)
            {
                List<float> delays = new List<float>();
                string text = System.Environment.GetEnvironmentVariable("MESH_TEST_VENDING_ANSWER_DELAY");
                if (!string.IsNullOrEmpty(text))
                    foreach (string part in text.Split(','))
                        if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s >= 0f)
                            delays.Add(s);
                _testVendingAnswerDelays = delays.ToArray();
            }
            if (_testVendingAnswerDelays.Length == 0)
                return 0f;
            return _testVendingAnswerDelays[Mathf.Min(_testVendingAnswers++, _testVendingAnswerDelays.Length - 1)];
        }

        /// <summary>MESH_TEST_VENDING_BLACKOUT `<n>:<seconds>`: the sale of this cell's n-th word has every word dropped for that long.</summary>
        private bool TestVendingBlackout(string saleId, int n, out float left)
        {
            left = 0f;
            string text = System.Environment.GetEnvironmentVariable("MESH_TEST_VENDING_BLACKOUT");
            if (!string.IsNullOrEmpty(text))
            {
                string[] parts = text.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int which) && which == n
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds))
                    _testVendingBlackouts[saleId] = Time.unscaledTime + seconds;
            }
            if (_testVendingBlackouts.TryGetValue(saleId, out float until) && Time.unscaledTime < until)
            {
                left = until - Time.unscaledTime;
                return true;
            }
            return false;
        }

        /// <summary>MESH_TEST_VENDING_DROP: true when this cell's n-th message of this kind ("sold", "done") is to be dropped.</summary>
        private bool TestVendingDrop(string kind, int n)
        {
            if (_testVendingDrops == null)
            {
                _testVendingDrops = new HashSet<string>();
                string text = System.Environment.GetEnvironmentVariable("MESH_TEST_VENDING_DROP");
                if (!string.IsNullOrEmpty(text))
                    foreach (string part in text.Split(','))
                        _testVendingDrops.Add(part.Trim().ToLowerInvariant());
            }
            return _testVendingDrops.Contains(kind + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private void RegisterVendingGhostActions()
        {
            _ghostActionHandlers[(ushort)GhostActionKind.VendingSubscribe] = HandleVendingSubscribeAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingUnsubscribe] = HandleVendingUnsubscribeAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingItems] = HandleVendingItemsAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingBuy] = HandleVendingBuyAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingSold] = HandleVendingSoldAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingFailed] = HandleVendingFailedAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingDone] = HandleVendingDoneAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingQuery] = HandleVendingQueryAction;
        }

        private static MeshPlayerCharacterVendingComponent VendingOf(BaseCharacterEntity entity)
        {
            return entity is BasePlayerCharacterEntity player ? player.VendingComponent as MeshPlayerCharacterVendingComponent : null;
        }

        /// <summary>
        /// Sends a sale's message to `to` where it is now: through its owner while it is a ghost here, or handled here
        /// when it is this cell's own (as if it had come over a link). False when `to` is neither.
        /// </summary>
        private bool SendToPlayer(BaseCharacterEntity to, GhostActionKind kind, BasePlayerCharacterEntity from, System.Action<NetDataWriter> write)
        {
            if (to == null)
                return false;
            EntityInfo fromInfo = from != null ? from.GetInfo() : EntityInfo.Empty;
            if (MeshIdentity.IsGhostEntity(to))
                return ForwardGhostAction(to, kind, fromInfo, write);
            NetDataWriter writer = new NetDataWriter();
            write(writer);
            if (_ghostActionHandlers.TryGetValue((ushort)kind, out GhostActionHandler handler))
                handler(to, fromInfo, new NetDataReader(writer.CopyData()));
            return true;
        }

        /// <summary>
        /// A sale's message to a player this cell has no trace of, sent to the cell it is known to be on (by mesh id;
        /// that cell's OnGhostAction finds it, or answers for it). False without a live link to that cell.
        /// </summary>
        private bool SendToPlayerOnZone(string zone, string targetMeshId, GhostActionKind kind, BaseCharacterEntity from, System.Action<NetDataWriter> write)
        {
            if (string.IsNullOrEmpty(zone) || string.IsNullOrEmpty(targetMeshId) || string.Equals(zone, OwnZoneId)
                || !_linkClients.TryGetValue(zone, out MeshLinkClient link) || !link.IsConnected)
                return false;
            _actionWriter.Reset();
            write(_actionWriter);
            link.SendGhostAction(new GhostActionMessage
            {
                targetMeshId = targetMeshId,
                instigatorMeshId = from != null ? MeshEntityIdOf(from) : string.Empty,
                action = (ushort)kind,
                payload = _actionWriter.CopyData(),
            });
            Logging.Log("Mesh", $"{kind} for {targetMeshId}, who is not streamed here, sent to zone {zone}");
            return true;
        }

        // ---- the vendor's cell ----

        /// <summary>A buyer across the line opened this cell's vendor's shop: keep it as a customer and send it the items.</summary>
        private void HandleVendingSubscribeAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            MeshPlayerCharacterVendingComponent vending = VendingOf(target);
            if (vending == null || !instigator.TryGetEntity(out BasePlayerCharacterEntity buyer) || buyer == null || !MeshIdentity.IsGhostEntity(buyer))
                return;
            vending.AddRemoteCustomer(buyer);
            Logging.Log("Mesh", $"Vending: {buyer.CharacterName} across a line opened {target.CharacterName}'s shop, sent {(vending.Data.isStarted ? vending.Items.Count : 0)} item(s)");
        }

        private void HandleVendingUnsubscribeAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            string buyerId = payload.GetString();
            VendingOf(target)?.RemoveRemoteCustomer(buyerId);
        }

        /// <summary>
        /// The vendor's half of a purchase from across the line; the buyer's cell holds the price in escrow. The item leaves
        /// the vendor now; the price reaches it when the buyer's cell says the item was delivered (DESIGN 1.48).
        /// </summary>
        private void HandleVendingBuyAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            int index = payload.GetInt();
            string itemId = payload.GetString();
            int price = payload.GetInt();
            string saleId = payload.GetString();
            MeshPlayerCharacterVendingComponent vending = VendingOf(target);
            if (!instigator.TryGetEntity(out BasePlayerCharacterEntity buyer) || buyer == null || !MeshIdentity.IsGhostEntity(buyer))
            {
                Logging.LogWarning("Mesh", $"Vending: a purchase from {target.CharacterName}'s shop by a buyer not streamed here; nothing sold, the buyer's escrow goes back on its timeout");
                return;
            }
            MeshIdentity buyerIdentity = MeshIdentity.Get(buyer);
            string buyerZone = buyerIdentity != null ? buyerIdentity.OwnerZoneId : null; // where the escrow is
            CharacterItem sold = CharacterItem.Empty;
            UITextKeys reason = vending != null ? vending.SellAcross(buyer, index, itemId, price, out sold) : UITextKeys.UI_ERROR_INVALID_DATA;
            float delay = TestVendingAnswerDelay();
            BasePlayerCharacterEntity vendor = target as BasePlayerCharacterEntity;
            if (reason != UITextKeys.NONE)
            {
                Logging.LogWarning("Mesh", $"Vending: {buyer.CharacterName}'s purchase of entry {index} from {target.CharacterName} refused here: {reason}");
                AnswerBuyer(buyer.Id, buyerZone, GhostActionKind.VendingFailed, vendor, writer => { writer.Put((ushort)reason); writer.Put(saleId); }, delay);
                return;
            }
            _pendingSales[saleId] = new PendingSale
            {
                saleId = saleId, vendorId = target.Id, vendorUserId = vendor != null ? vendor.UserId : null, vendorName = target.CharacterName, buyerId = buyer.Id, buyerName = buyer.CharacterName,
                buyerZone = buyerZone, item = sold, price = price, since = Time.unscaledTime,
            };
            StartCoroutine(WatchPendingSale(saleId));
            Logging.Log("Mesh", $"Vending sold here: {target.CharacterName} sold {sold.amount} x item {sold.dataId} for {price} gold to {buyer.CharacterName} across a line (sale {saleId}; paid on the buyer's cell's word)");
            string saleZone = OwnZoneId, vendorId = target.Id;
            AnswerBuyer(buyer.Id, buyerZone, GhostActionKind.VendingSold, vendor, writer =>
            {
                writer.Put((byte)0); writer.Put(saleId); writer.Put(sold); writer.Put(price);
                writer.Put(saleZone ?? string.Empty); writer.Put(vendorId ?? string.Empty); // DESIGN 1.52: where the word goes
            }, delay);
        }

        /// <summary>
        /// The vendor's cell answers the buyer where it is now: through its owner while it is a ghost here, or here when
        /// it has since crossed onto this cell (the answer is then handled as if it had come over a link).
        /// </summary>
        private void AnswerBuyer(string buyerId, string buyerZone, GhostActionKind kind, BasePlayerCharacterEntity vendor, System.Action<NetDataWriter> write, float delay)
        {
            if (delay > 0f)
            {
                Logging.LogWarning("Mesh", $"Vending: the answer ({kind}) to {buyerId} held {delay:0.#} s (MESH_TEST_VENDING_ANSWER_DELAY)");
                StartCoroutine(AnswerBuyerLater(buyerId, buyerZone, kind, vendor, write, delay));
                return;
            }
            if (kind == GhostActionKind.VendingSold && TestVendingDrop("sold", ++_testSoldSent))
            {
                Logging.LogWarning("Mesh", $"Vending: the answer ({kind}) to {buyerId} dropped (MESH_TEST_VENDING_DROP sold{_testSoldSent})");
                return;
            }
            BaseCharacterEntity buyer = ResolveGhostOrReal(buyerId);
            if (buyer is BasePlayerCharacterEntity player && player != null && !MeshIdentity.IsGhostEntity(player))
                Logging.Log("Mesh", $"Vending: {player.CharacterName} crossed onto the vendor's cell before the answer ({kind}); handled here");
            if (!SendToPlayer(buyer, kind, vendor, write) && !SendToPlayerOnZone(buyerZone, buyerId, kind, vendor, write))
                Logging.LogWarning("Mesh", $"Vending: the answer ({kind}) to {buyerId} found it neither here nor streamed here, and zone {buyerZone ?? "?"} is not linked; dropped (a sale is asked about later)");
        }

        private IEnumerator AnswerBuyerLater(string buyerId, string buyerZone, GhostActionKind kind, BasePlayerCharacterEntity vendor, System.Action<NetDataWriter> write, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            AnswerBuyer(buyerId, buyerZone, kind, vendor, write, 0f);
        }

        /// <summary>
        /// A sale without the buyer's cell's word: asked about every VendingSaleAskSeconds; after a minute the vendor is
        /// released and the sale asked about every 30 s; settled as paid at the give-up.
        /// </summary>
        private IEnumerator WatchPendingSale(string saleId)
        {
            while (true)
            {
                bool released = _pendingSales.TryGetValue(saleId, out PendingSale before) && before.released;
                yield return new WaitForSecondsRealtime(released ? VendingSaleReleasedAskSeconds : VendingSaleAskSeconds);
                if (!_pendingSales.TryGetValue(saleId, out PendingSale sale))
                    yield break;
                if (Time.unscaledTime - sale.since >= VendingSaleGiveUpSeconds)
                {
                    Logging.LogError("Mesh", $"Vending: no word on sale {saleId} ({sale.vendorName} to {sale.buyerName}) in {VendingSaleGiveUpSeconds:0} s; settled as paid");
                    SettleSale(saleId, true, $"no word from the buyer's cell in {VendingSaleGiveUpSeconds:0} s");
                    yield break;
                }
                if (!sale.released && Time.unscaledTime - sale.since >= VendingSaleReleaseSeconds)
                {
                    sale.released = true;
                    _vendingHoldLogged.Remove(sale.vendorId);
                    Logging.LogWarning("Mesh", $"Vending: sale {saleId} of {sale.vendorName} to {sale.buyerName} released after {VendingSaleReleaseSeconds:0} s without a word: the sale stays open, settled when the buyer's cell answers (mailed if {sale.vendorName} has left)");
                }
                BaseCharacterEntity buyer = ResolveGhostOrReal(sale.buyerId);
                BasePlayerCharacterEntity vendor = ResolveOwnedCharacter(sale.vendorId) as BasePlayerCharacterEntity;
                Logging.LogWarning("Mesh", $"Vending: no word on sale {saleId} ({sale.vendorName} to {sale.buyerName}) after {Time.unscaledTime - sale.since:0} s; asking the buyer's cell");
                string id = saleId, saleZone = OwnZoneId, vendorId = sale.vendorId;
                System.Action<NetDataWriter> query = writer => { writer.Put((byte)0); writer.Put(id); writer.Put(saleZone ?? string.Empty); writer.Put(vendorId ?? string.Empty); };
                if (!SendToPlayer(buyer, GhostActionKind.VendingQuery, vendor, query) && !SendToPlayerOnZone(sale.buyerZone, sale.buyerId, GhostActionKind.VendingQuery, vendor, query))
                    Logging.LogWarning("Mesh", $"Vending: {sale.buyerName} is neither here nor streamed here, and zone {sale.buyerZone ?? "?"} is not linked; sale {saleId} is asked about again later");
            }
        }

        /// <summary>The buyer's cell's word on a sale: the vendor is paid, or the item goes back to its bag.</summary>
        private void SettleSale(string saleId, bool paid, string why)
        {
            if (!_pendingSales.TryGetValue(saleId, out PendingSale sale))
                return;
            _pendingSales.Remove(saleId);
            _vendingHoldLogged.Remove(sale.vendorId);
            BasePlayerCharacterEntity vendor = RegisteredPlayer(sale.vendorId);
            MeshPlayerCharacterVendingComponent vending = VendingOf(vendor);
            if (vending == null)
            {
                // DESIGN 1.51: the vendor has left this cell; what the sale owes it goes by mail.
                if (paid)
                    MailToAbsent(sale.vendorUserId, sale.vendorName, "Shop sale", $"The gold for an item {sale.vendorName}'s shop sold to {sale.buyerName} after it left ({why}).",
                        null, sale.price, $"Vending: sale {saleId} paid for {sale.vendorName}, who is not on this cell");
                else
                    MailToAbsent(sale.vendorUserId, sale.vendorName, "Shop item back", $"The item {sale.vendorName}'s shop sold to {sale.buyerName}, back: it did not reach the buyer ({why}).",
                        new List<CharacterItem> { sale.item }, 0, $"Vending: sale {saleId} not delivered for {sale.vendorName}, who is not on this cell; its item");
                return;
            }
            if (paid)
            {
                vending.PaySale(sale.price);
                Logging.Log("Mesh", $"Vending paid here: {sale.vendorName} received {sale.price} gold for sale {saleId} to {sale.buyerName} ({why})");
            }
            else
            {
                vending.ReturnSale(sale.item);
                Logging.LogWarning("Mesh", $"Vending: sale {saleId} of {sale.vendorName} to {sale.buyerName} not delivered ({why}); {sale.item.amount} x item {sale.item.dataId} back in {sale.vendorName}'s bag");
            }
        }

        /// <summary>The buyer's cell's word on one of this cell's sales (DESIGN 1.48).</summary>
        private void HandleVendingDoneAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            payload.GetByte();
            string saleId = payload.GetString();
            bool paid = payload.GetBool();
            if (!_pendingSales.ContainsKey(saleId))
            {
                Logging.Log("Mesh", $"Vending: word on sale {saleId} ({(paid ? "paid" : "not paid")}) for no pending sale here (settled already)");
                return;
            }
            SettleSale(saleId, paid, paid ? "the buyer's cell gave the item" : "the buyer's cell gave no item");
        }

        // ---- the buyer's cell ----

        /// <summary>The vendor's cell sent its items: the puppet of the vendor here keeps them and tells its customers here.</summary>
        private void HandleVendingItemsAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            VendingItems items = new VendingItems();
            items.Deserialize(payload);
            if (!instigator.TryGetEntity(out BasePlayerCharacterEntity vendor) || vendor == null || !MeshIdentity.IsGhostEntity(vendor))
                return;
            MeshPlayerCharacterVendingComponent vending = VendingOf(vendor);
            if (vending == null)
                return;
            vending.SetMirrorItems(items);
            Logging.Log("Mesh", $"Vending: {vendor.CharacterName}'s shop across a line has {items.Count} item(s) for {target.CharacterName}");
        }

        /// <summary>
        /// BeginVendingEscrow takes the entry's price out of the buyer's gold, keeps it, and asks the vendor's owner to sell.
        /// False when the buyer already has a purchase on its way or cannot pay.
        /// </summary>
        internal bool BeginVendingEscrow(BasePlayerCharacterEntity buyer, BasePlayerCharacterEntity vendorGhost, int index, VendingItem entry)
        {
            if (buyer == null || vendorGhost == null || string.IsNullOrEmpty(buyer.Id) || _vendingEscrows.ContainsKey(buyer.Id) || buyer.Gold < entry.price)
                return false;
            buyer.Gold -= entry.price;
            string saleId = System.Guid.NewGuid().ToString("N");
            _vendingEscrows[buyer.Id] = new VendingEscrow { buyer = buyer, gold = entry.price, vendorName = vendorGhost.CharacterName, saleId = saleId };
            string itemId = entry.item.id;
            int price = entry.price;
            ForwardGhostAction(vendorGhost, GhostActionKind.VendingBuy, buyer.GetInfo(), writer => { writer.Put(index); writer.Put(itemId); writer.Put(price); writer.Put(saleId); });
            StartCoroutine(VendingEscrowTimeout(buyer.Id, saleId));
            Logging.Log("Mesh", $"Vending buy: {buyer.CharacterName}'s {price} gold in escrow for entry {index} ({entry.item.amount} x item {entry.item.dataId}) of {vendorGhost.CharacterName}'s shop, asked its owner (sale {saleId})");
            return true;
        }

        /// <summary>The buyer's escrow goes back; with a sale id, only that purchase's.</summary>
        private void RefundVendingEscrow(BasePlayerCharacterEntity buyer, string why, string saleId = null)
        {
            if (buyer == null || string.IsNullOrEmpty(buyer.Id) || !_vendingEscrows.TryGetValue(buyer.Id, out VendingEscrow escrow))
                return;
            if (saleId != null && escrow.saleId != saleId)
                return;
            EndVendingEscrow(buyer.Id);
            buyer.Gold = buyer.Gold.Increase(escrow.gold);
            Logging.LogWarning("Mesh", $"Vending escrow of {buyer.CharacterName} returned ({why}): {escrow.gold} gold");
        }

        private IEnumerator VendingEscrowTimeout(string buyerId, string saleId)
        {
            yield return new WaitForSecondsRealtime(VendingEscrowSeconds);
            if (!_vendingEscrows.TryGetValue(buyerId, out VendingEscrow escrow) || escrow.buyer == null || escrow.saleId != saleId)
                yield break;
            BasePlayerCharacterEntity buyer = escrow.buyer;
            RefundVendingEscrow(buyer, $"no answer from {escrow.vendorName}'s cell in {VendingEscrowSeconds:0} s");
            ServerGameMessageHandlers.SendGameMessage(buyer.ConnectionId, UITextKeys.UI_ERROR_INTERNAL_SERVER_ERROR);
        }

        private void RecordSaleDecision(string buyerId, string saleId, bool paid)
        {
            if (string.IsNullOrEmpty(buyerId) || string.IsNullOrEmpty(saleId))
                return;
            if (!_saleDecisions.TryGetValue(buyerId, out List<SaleDecision> decisions))
                _saleDecisions[buyerId] = decisions = new List<SaleDecision>();
            float now = Time.unscaledTime;
            decisions.RemoveAll(d => now - d.at > VendingDecisionSeconds || d.saleId == saleId);
            decisions.Add(new SaleDecision { saleId = saleId, paid = paid, at = now });
        }

        private bool TryGetSaleDecision(string buyerId, string saleId, out bool paid)
        {
            paid = false;
            if (string.IsNullOrEmpty(buyerId) || !_saleDecisions.TryGetValue(buyerId, out List<SaleDecision> decisions))
                return false;
            foreach (SaleDecision d in decisions)
            {
                if (d.saleId == saleId && Time.unscaledTime - d.at <= VendingDecisionSeconds)
                {
                    paid = d.paid;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The vending carrier's part: a buyer's recent words on sales leave with it, and arrive with it.</summary>
        internal List<SaleDecision> SaleDecisionsOf(string buyerId)
        {
            if (string.IsNullOrEmpty(buyerId) || !_saleDecisions.TryGetValue(buyerId, out List<SaleDecision> decisions))
                return null;
            float now = Time.unscaledTime;
            decisions.RemoveAll(d => now - d.at > VendingDecisionSeconds);
            return decisions.Count > 0 ? decisions : null;
        }

        internal void ImportSaleDecision(string buyerId, string saleId, bool paid, float age)
        {
            RecordSaleDecision(buyerId, saleId, paid);
            List<SaleDecision> decisions = _saleDecisions[buyerId];
            SaleDecision d = decisions[decisions.Count - 1];
            d.at = Time.unscaledTime - Mathf.Max(0f, age);
            decisions[decisions.Count - 1] = d;
        }

        /// <summary>
        /// This cell's word on a sale, to the vendor (the instigator of the sale's message, as this cell knows it), or to
        /// the cell the message came from (`replyZone`, by the vendor's mesh id) when the vendor is not streamed here.
        /// </summary>
        private void SendSaleDone(EntityInfo vendorInfo, string saleId, bool paid, BasePlayerCharacterEntity buyer, string replyZone = null, string vendorMeshId = null)
        {
            int n = ++_testDoneSent;
            if (TestVendingBlackout(saleId, n, out float left))
            {
                Logging.LogWarning("Mesh", $"Vending: the word on sale {saleId} ({(paid ? "paid" : "not paid")}) dropped (MESH_TEST_VENDING_BLACKOUT, {left:0} s left)");
                return;
            }
            if (TestVendingDrop("done", n))
            {
                Logging.LogWarning("Mesh", $"Vending: the word on sale {saleId} ({(paid ? "paid" : "not paid")}) dropped (MESH_TEST_VENDING_DROP done{_testDoneSent})");
                return;
            }
            BasePlayerCharacterEntity vendor = null;
            if (vendorInfo != null)
                vendorInfo.TryGetEntity(out vendor);
            System.Action<NetDataWriter> done = writer => { writer.Put((byte)0); writer.Put(saleId); writer.Put(paid); };
            // DESIGN 1.52: to the sale's cell first, wherever its vendor is now (a released or restarted sale's vendor may be elsewhere).
            if (SendToPlayerOnZone(replyZone, vendorMeshId, GhostActionKind.VendingDone, buyer, done))
                return;
            if (!SendToPlayer(vendor, GhostActionKind.VendingDone, buyer, done))
                Logging.LogWarning("Mesh", $"Vending: the word on sale {saleId} ({(paid ? "paid" : "not paid")}) found the vendor neither here nor streamed here; its cell asks again");
        }

        /// <summary>
        /// The vendor's half is done: the buyer receives the item and the escrow is spent (or, after the escrow went back,
        /// the price is taken again), and the vendor's cell is told; a buyer who cannot pay any more gets nothing and the
        /// item goes back to the vendor. A sale this cell already decided gets the same word again.
        /// </summary>
        private void HandleVendingSoldAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            payload.GetByte();
            string saleId = payload.GetString();
            CharacterItem sold = payload.Get<CharacterItem>();
            int price = payload.GetInt();
            string saleZone = payload.GetString(), vendorId = payload.GetString();
            if (!(target is BasePlayerCharacterEntity buyer))
                return;
            instigator.TryGetEntity(out BasePlayerCharacterEntity vendor);
            if (TryGetSaleDecision(buyer.Id, saleId, out bool decided))
            {
                Logging.LogWarning("Mesh", $"Vending: the item of sale {saleId} reached {buyer.CharacterName} after this cell's word on it ({(decided ? "paid" : "not paid")}); the same word again, nothing given");
                SendSaleDone(instigator, saleId, decided, buyer, saleZone, vendorId);
                return;
            }
            bool paid = true;
            if (_vendingEscrows.TryGetValue(buyer.Id, out VendingEscrow escrow) && escrow.saleId == saleId)
                EndVendingEscrow(buyer.Id);
            else if (buyer.Gold >= price)
            {
                buyer.Gold -= price; // the escrow had already gone back (a late answer): the item is paid for now
                Logging.LogWarning("Mesh", $"Vending sold to {buyer.CharacterName} after its escrow went back: {price} gold taken again");
            }
            else
            {
                paid = false;
                Logging.LogWarning("Mesh", $"Vending: the item of sale {saleId} reached {buyer.CharacterName} after its escrow went back, and it can no longer pay {price} gold; the item goes back to the vendor");
                ServerGameMessageHandlers.SendGameMessage(buyer.ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
            }
            RecordSaleDecision(buyer.Id, saleId, paid);
            if (paid)
            {
                buyer.IncreaseItems(sold, item => buyer.OnRewardItem(RewardGivenType.Vending, item));
                buyer.FillEmptySlots();
                if (vendor != null)
                    ServerLogHandlers.LogBuyVendingItem(buyer, vendor, sold, price);
                Logging.Log("Mesh", $"Vending bought here: {buyer.CharacterName} received {sold.amount} x item {sold.dataId} for {price} gold from {(vendor != null ? vendor.CharacterName : "a vendor")} across a line; its escrow is spent");
            }
            SendSaleDone(instigator, saleId, paid, buyer, saleZone, vendorId);
        }

        /// <summary>The vendor's cell asks about a sale it has no word on: this cell's word, or not paid when it never saw the item.</summary>
        private void HandleVendingQueryAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            payload.GetByte();
            string saleId = payload.GetString();
            string saleZone = payload.GetString(), vendorId = payload.GetString();
            BasePlayerCharacterEntity buyer = target as BasePlayerCharacterEntity;
            AnswerSaleQuery(buyer != null ? buyer.Id : target.Id, buyer != null ? buyer.CharacterName : target.Id, saleId, instigator, buyer,
                string.IsNullOrEmpty(saleZone) ? null : saleZone, string.IsNullOrEmpty(vendorId) ? null : vendorId);
        }

        private void AnswerSaleQuery(string buyerId, string buyerName, string saleId, EntityInfo vendorInfo, BasePlayerCharacterEntity buyer, string replyZone = null, string vendorMeshId = null)
        {
            if (!TryGetSaleDecision(buyerId, saleId, out bool paid))
            {
                // The item never came: from now on this cell refuses it. A purchase still in escrow gets its gold back.
                paid = false;
                RecordSaleDecision(buyerId, saleId, false);
                if (buyer != null)
                    RefundVendingEscrow(buyer, "the vendor's cell asked about a sale whose item never came", saleId);
                Logging.LogWarning("Mesh", $"Vending: asked about sale {saleId} for {buyerName}, whose item never came here; not paid");
            }
            else
                Logging.Log("Mesh", $"Vending: asked about sale {saleId} for {buyerName}; this cell's word was {(paid ? "paid" : "not paid")}");
            SendSaleDone(vendorInfo, saleId, paid, buyer, replyZone, vendorMeshId);
        }

        /// <summary>The vendor's cell refused: the gold goes back and the client hears why.</summary>
        private void HandleVendingFailedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys reason = (UITextKeys)payload.GetUShort();
            string saleId = payload.GetString();
            if (!(target is BasePlayerCharacterEntity buyer) || MeshIdentity.IsGhostEntity(buyer))
                return; // a ghost: it moved on once its escrow here went back, so there is nothing left to give back
            RefundVendingEscrow(buyer, $"refused by the vendor's cell: {reason}", saleId);
            ServerGameMessageHandlers.SendGameMessage(buyer.ConnectionId, reason);
        }

        // ---- a sale's message for a player this cell does not own ----

        /// <summary>
        /// OnGhostAction's hook for a target not owned here (DESIGN 1.48): a sale's answer, question or word follows the
        /// player to its owner while it is a ghost here; with no trace of it, the answer and the question are answered
        /// here (not paid: a buyer who left), and a word for a vendor who left is logged. True when the kind is a sale's.
        /// </summary>
        private bool HandleVendingForAbsent(long connectionId, GhostActionMessage msg)
        {
            GhostActionKind kind = (GhostActionKind)msg.action;
            if (kind != GhostActionKind.VendingSold && kind != GhostActionKind.VendingQuery && kind != GhostActionKind.VendingDone)
                return false;
            byte[] data = msg.payload ?? System.Array.Empty<byte>();
            if (data.Length == 0)
                return true;
            EntityInfo instigator = ResolveInstigator(msg.instigatorMeshId);
            BaseCharacterEntity ghost = ResolveGhostOrReal(msg.targetMeshId);
            byte forwards = data[0];
            if (ghost != null && MeshIdentity.IsGhostEntity(ghost) && forwards < VendingMaxForwards)
            {
                Logging.Log("Mesh", $"Vending: {kind} for {ghost.CharacterName}, who moved on; passed to its owner");
                ForwardGhostAction(ghost, kind, instigator, writer => { writer.Put((byte)(forwards + 1)); writer.Put(data, 1, data.Length - 1); });
                return true;
            }
            NetDataReader reader = new NetDataReader(data);
            reader.GetByte();
            string saleId = reader.GetString();
            string replyZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string fromZone) ? fromZone : null;
            string vendorMeshId = msg.instigatorMeshId;
            if (kind != GhostActionKind.VendingDone)
            {
                // The sale's cell and its vendor, from the message itself (the link it came over may be a forwarder's).
                if (kind == GhostActionKind.VendingSold)
                {
                    reader.Get<CharacterItem>();
                    reader.GetInt();
                }
                if (reader.AvailableBytes > 0)
                {
                    string saleZone = reader.GetString(), vendorId = reader.GetString();
                    if (!string.IsNullOrEmpty(saleZone)) replyZone = saleZone;
                    if (!string.IsNullOrEmpty(vendorId)) vendorMeshId = vendorId;
                }
            }
            if (kind == GhostActionKind.VendingDone)
            {
                Logging.LogWarning("Mesh", $"Vending: word on sale {saleId} for {msg.targetMeshId}, who is not here; dropped");
                return true;
            }
            if (kind == GhostActionKind.VendingSold && !TryGetSaleDecision(msg.targetMeshId, saleId, out _))
            {
                RecordSaleDecision(msg.targetMeshId, saleId, false);
                Logging.LogWarning("Mesh", $"Vending: the item of sale {saleId} reached this cell after {msg.targetMeshId} left it; not paid, the item goes back to the vendor");
                SendSaleDone(instigator, saleId, false, null, replyZone, vendorMeshId);
                return true;
            }
            AnswerSaleQuery(msg.targetMeshId, msg.targetMeshId, saleId, instigator, null, replyZone, vendorMeshId);
            return true;
        }

        /// <summary>
        /// UnregisterPlayerCharacter's hook for a player leaving otherwise than by a handoff (a disconnect, a warp): its
        /// purchase on its way gets its escrow back before the kit saves it. Its pending sales stay open and are settled by
        /// the buyer's cell's word, mailed to it (DESIGN 1.51). A handoff is held while either waits (HoldsHandoffForVending).
        /// </summary>
        private void VendingOnLeaving(BasePlayerCharacterEntity player)
        {
            if (player == null || string.IsNullOrEmpty(player.Id) || _handoffsInProgress.Contains(player.Id))
                return;
            RefundVendingEscrow(player, "left the cell before the answer");
        }
#else
        internal bool BeginVendingEscrow(BasePlayerCharacterEntity buyer, BasePlayerCharacterEntity vendorGhost, int index, VendingItem entry) { return false; }
        internal bool HoldsHandoffForVending(BasePlayerCharacterEntity player) { return false; }
        private void VendingOnLeaving(BasePlayerCharacterEntity player) { }
        internal List<SaleDecision> SaleDecisionsOf(string buyerId) { return null; }
        internal void ImportSaleDecision(string buyerId, string saleId, bool paid, float age) { }
#endif
    }
}
