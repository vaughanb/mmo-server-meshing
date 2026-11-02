// Server Meshing add-on — MeshMapNetworkManager: a player's shop bought from across a line (DESIGN.md 1.46).
//
// The other cell's half of what MeshPlayerCharacterVendingComponent forwards. On the vendor's cell: a buyer across the
// line opened the shop (kept as a remote customer and sent the items), closed it, or bought an entry (the vendor's half
// of the sale, answered with the item or the reason). On the buyer's cell: the items for the puppet of the vendor and
// its customers here, the item bought, or the refusal. The buyer's escrow (the price, out of its gold while the
// vendor's cell does its half) lives here, given back on a refusal or after 10 s without an answer.
using System.Collections;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long a buyer's escrow waits for the vendor's cell before the gold goes back.</summary>
        public const float VendingEscrowSeconds = 10f;

        private sealed class VendingEscrow
        {
            public BasePlayerCharacterEntity buyer;
            public int gold;
            public string vendorName;
        }

        private readonly Dictionary<string, VendingEscrow> _vendingEscrows = new Dictionary<string, VendingEscrow>();

        private void RegisterVendingGhostActions()
        {
            _ghostActionHandlers[(ushort)GhostActionKind.VendingSubscribe] = HandleVendingSubscribeAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingUnsubscribe] = HandleVendingUnsubscribeAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingItems] = HandleVendingItemsAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingBuy] = HandleVendingBuyAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingSold] = HandleVendingSoldAction;
            _ghostActionHandlers[(ushort)GhostActionKind.VendingFailed] = HandleVendingFailedAction;
        }

        private static MeshPlayerCharacterVendingComponent VendingOf(BaseCharacterEntity entity)
        {
            return entity is BasePlayerCharacterEntity player ? player.VendingComponent as MeshPlayerCharacterVendingComponent : null;
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

        /// <summary>The vendor's half of a purchase from across the line; the buyer's cell holds the price in escrow.</summary>
        private void HandleVendingBuyAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            int index = payload.GetInt();
            string itemId = payload.GetString();
            int price = payload.GetInt();
            MeshPlayerCharacterVendingComponent vending = VendingOf(target);
            if (!instigator.TryGetEntity(out BasePlayerCharacterEntity buyer) || buyer == null || !MeshIdentity.IsGhostEntity(buyer))
            {
                Logging.LogWarning("Mesh", $"Vending: a purchase from {target.CharacterName}'s shop by a buyer not streamed here; nothing sold, the buyer's escrow goes back on its timeout");
                return;
            }
            CharacterItem sold = CharacterItem.Empty;
            UITextKeys reason = vending != null ? vending.SellAcross(buyer, index, itemId, price, out sold) : UITextKeys.UI_ERROR_INVALID_DATA;
            if (reason != UITextKeys.NONE)
            {
                ForwardGhostAction(buyer, GhostActionKind.VendingFailed, target.GetInfo(), writer => writer.Put((ushort)reason));
                Logging.LogWarning("Mesh", $"Vending: {buyer.CharacterName}'s purchase of entry {index} from {target.CharacterName} refused here: {reason}");
                return;
            }
            ForwardGhostAction(buyer, GhostActionKind.VendingSold, target.GetInfo(), writer => { writer.Put(sold); writer.Put(price); });
            Logging.Log("Mesh", $"Vending sold here: {target.CharacterName} sold {sold.amount} x item {sold.dataId} for {price} gold to {buyer.CharacterName} across a line");
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
            _vendingEscrows[buyer.Id] = new VendingEscrow { buyer = buyer, gold = entry.price, vendorName = vendorGhost.CharacterName };
            string itemId = entry.item.id;
            int price = entry.price;
            ForwardGhostAction(vendorGhost, GhostActionKind.VendingBuy, buyer.GetInfo(), writer => { writer.Put(index); writer.Put(itemId); writer.Put(price); });
            StartCoroutine(VendingEscrowTimeout(buyer.Id));
            Logging.Log("Mesh", $"Vending buy: {buyer.CharacterName}'s {price} gold in escrow for entry {index} ({entry.item.amount} x item {entry.item.dataId}) of {vendorGhost.CharacterName}'s shop, asked its owner");
            return true;
        }

        private void RefundVendingEscrow(BasePlayerCharacterEntity buyer, string why)
        {
            if (buyer == null || string.IsNullOrEmpty(buyer.Id) || !_vendingEscrows.TryGetValue(buyer.Id, out VendingEscrow escrow))
                return;
            _vendingEscrows.Remove(buyer.Id);
            buyer.Gold = buyer.Gold.Increase(escrow.gold);
            Logging.LogWarning("Mesh", $"Vending escrow of {buyer.CharacterName} returned ({why}): {escrow.gold} gold");
        }

        private IEnumerator VendingEscrowTimeout(string buyerId)
        {
            yield return new WaitForSecondsRealtime(VendingEscrowSeconds);
            if (!_vendingEscrows.TryGetValue(buyerId, out VendingEscrow escrow) || escrow.buyer == null)
                yield break;
            BasePlayerCharacterEntity buyer = escrow.buyer;
            RefundVendingEscrow(buyer, $"no answer from {escrow.vendorName}'s cell in {VendingEscrowSeconds:0} s");
            ServerGameMessageHandlers.SendGameMessage(buyer.ConnectionId, UITextKeys.UI_ERROR_INTERNAL_SERVER_ERROR);
        }

        /// <summary>The vendor's half is done: the buyer receives the item and the escrow is spent.</summary>
        private void HandleVendingSoldAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            CharacterItem sold = payload.Get<CharacterItem>();
            int price = payload.GetInt();
            if (!(target is BasePlayerCharacterEntity buyer))
                return;
            instigator.TryGetEntity(out BasePlayerCharacterEntity vendor);
            if (_vendingEscrows.ContainsKey(buyer.Id))
                _vendingEscrows.Remove(buyer.Id);
            else if (buyer.Gold >= price)
            {
                buyer.Gold -= price; // the escrow had already gone back (a late answer): the item is paid for now
                Logging.LogWarning("Mesh", $"Vending sold to {buyer.CharacterName} after its escrow went back: {price} gold taken again");
            }
            else
                Logging.LogWarning("Mesh", $"Vending sold to {buyer.CharacterName} after its escrow went back, and it can no longer pay {price} gold: given all the same");
            buyer.IncreaseItems(sold, item => buyer.OnRewardItem(RewardGivenType.Vending, item));
            buyer.FillEmptySlots();
            if (vendor != null)
                ServerLogHandlers.LogBuyVendingItem(buyer, vendor, sold, price);
            Logging.Log("Mesh", $"Vending bought here: {buyer.CharacterName} received {sold.amount} x item {sold.dataId} for {price} gold from {(vendor != null ? vendor.CharacterName : "a vendor")} across a line; its escrow is spent");
        }

        /// <summary>The vendor's cell refused: the gold goes back and the client hears why.</summary>
        private void HandleVendingFailedAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            UITextKeys reason = (UITextKeys)payload.GetUShort();
            if (!(target is BasePlayerCharacterEntity buyer))
                return;
            RefundVendingEscrow(buyer, $"refused by the vendor's cell: {reason}");
            ServerGameMessageHandlers.SendGameMessage(buyer.ConnectionId, reason);
        }
#else
        internal bool BeginVendingEscrow(BasePlayerCharacterEntity buyer, BasePlayerCharacterEntity vendorGhost, int index, VendingItem entry) { return false; }
#endif
    }
}
