// Server Meshing add-on — a player's shop seen and bought from across a line (DESIGN.md 1.46).
//
// The kit's vending is one component on one server: the vendor's component holds the shop (a synced sign, the items
// and prices) and its customers; a customer subscribes to the vendor's component, receives the items by a target RPC
// and buys by index, and the sale moves the item and the gold between the two inventories in one call. Across a line
// the vendor is a puppet on the buyer's cell: it had no sign (the sign is a sync field the owner's cell writes), no
// items, and a sale on it would have changed the puppet's inventory, which is nobody's. This subclass is swapped onto
// the player prefabs in place of the kit's component (MeshPlayerCharacterEntity.InitialRequiredComponents) and declares
// the kit's subscribe, unsubscribe and buy server RPCs again under the same names (the client's calls are unchanged):
//  * the sign travels in the ghost snapshot (GhostSnapshotMessage.vendingTitle) and is written on the puppet, so a
//    client across the line sees the shop and can open it;
//  * a subscription to a puppet is kept here for the buyer's client and forwarded to the vendor's owner
//    (GhostAction VendingSubscribe), which keeps the buyer as a remote customer and answers with the items
//    (VendingItems), again after every sale;
//  * a purchase is split in two halves, as a trade is (DESIGN 1.44): the buyer's cell takes the price out of the
//    buyer's gold into an escrow and asks the vendor's cell (VendingBuy); that cell checks the entry is still the one
//    the buyer saw, takes the item from the vendor, pays the vendor and answers with the item (VendingSold), which the
//    buyer's cell gives; a refusal (VendingFailed) or 10 s without an answer gives the gold back. DESIGN 1.48: the
//    vendor is paid only when the buyer's cell says it gave the item (VendingDone), and gets the item back when it says
//    it did not (MeshMapNetworkManager_Vending).
// The carrier also takes a buyer's recent words on sales across with it (DESIGN 1.48).
// The shop is also carried with a handoff (IMeshHandoffCarrier): a vendor cannot walk, but a split or a merge of
// cells hands it over all the same, and the kit's shop is not character data.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshPlayerCharacterVendingComponent : PlayerCharacterVendingComponent, IMeshHandoffCarrier
    {
        public const string Key = "mesh.vending";

        private static MeshMapNetworkManager Mesh => BaseGameNetworkManager.Singleton as MeshMapNetworkManager;

        /// <summary>The vendor's cell: buyers across a line who opened this shop, by character id.</summary>
        private readonly HashSet<string> _remoteCustomers = new HashSet<string>();

        internal bool IsPuppet => MeshIdentity.IsGhostEntity(Entity);

        /// <summary>What this shop sells, as this cell knows it (the owner's list, on a puppet).</summary>
        internal VendingItems Items => _items;

        // ---- a puppet of a vendor across a line ----

        /// <summary>The sign as the owner's snapshot has it: its title while the shop is open, null when it is not.</summary>
        internal void SetMirrorSign(string title)
        {
            bool started = title != null;
            VendingData current = data.Value;
            if (current.isStarted == started && (!started || current.title == title))
                return;
            data.Value = started ? new VendingData { isStarted = true, title = title } : new VendingData();
            if (!started)
            {
                _items.Clear();
                NotifyLocalCustomers();
            }
        }

        /// <summary>The items as the vendor's cell last sent them: this cell's customers of the puppet are told.</summary>
        internal void SetMirrorItems(VendingItems items)
        {
            _items = items ?? new VendingItems();
            NotifyLocalCustomers();
        }

        /// <summary>A puppet becomes the real character here: what it mirrored is not its own (a carried shop is read back after this).</summary>
        internal void ClearMirror()
        {
            if (data.Value.isStarted)
                data.Value = new VendingData();
            _items.Clear();
            _remoteCustomers.Clear();
        }

        private void NotifyLocalCustomers()
        {
            foreach (PlayerCharacterVendingComponent customer in _customers)
            {
                if (customer != null)
                    CallTargetRpcNotifyItems(customer.ConnectionId, _items);
            }
        }

        private void AddLocalCustomer(PlayerCharacterVendingComponent customer)
        {
            if (_customers.Add(customer) && (!IsPuppet || _items.Count > 0))
                CallTargetRpcNotifyItems(customer.ConnectionId, _items);
        }

        private void RemoveLocalCustomer(PlayerCharacterVendingComponent customer)
        {
            _customers.Remove(customer);
        }

        // ---- the vendor's cell: buyers across a line ----

        internal void AddRemoteCustomer(BasePlayerCharacterEntity buyerGhost)
        {
            if (buyerGhost == null || string.IsNullOrEmpty(buyerGhost.Id))
                return;
            if (Data.isStarted)
                _remoteCustomers.Add(buyerGhost.Id);
            SendItemsTo(buyerGhost);
        }

        internal void RemoveRemoteCustomer(string buyerId)
        {
            if (!string.IsNullOrEmpty(buyerId))
                _remoteCustomers.Remove(buyerId);
        }

        private void SendItemsTo(BasePlayerCharacterEntity buyerGhost)
        {
            VendingItems items = Data.isStarted ? _items : new VendingItems();
            Mesh?.ForwardGhostAction(buyerGhost, GhostActionKind.VendingItems, Entity.GetInfo(), writer => writer.Put(items));
        }

        /// <summary>After a sale: every buyer across a line who has this shop open gets the new list (a closed shop: its sign tells them).</summary>
        internal void NotifyRemoteCustomers()
        {
            if (_remoteCustomers.Count == 0 || Mesh == null)
                return;
            if (!Data.isStarted)
            {
                _remoteCustomers.Clear();
                return;
            }
            List<string> gone = null;
            foreach (string buyerId in _remoteCustomers)
            {
                BasePlayerCharacterEntity ghost = Mesh.FindPlayerGhost(buyerId);
                if (ghost == null)
                {
                    (gone ??= new List<string>()).Add(buyerId);
                    continue;
                }
                SendItemsTo(ghost);
            }
            if (gone != null)
            {
                foreach (string buyerId in gone)
                    _remoteCustomers.Remove(buyerId);
            }
        }

        /// <summary>
        /// The vendor's half of a purchase from across a line: the entry must still be the one the buyer saw (same item,
        /// same price); then the item leaves the vendor's inventory. The buyer's gold is already in escrow on the buyer's
        /// cell; the vendor is paid when that cell says it gave the item (PaySale; DESIGN 1.48). NONE and the item sold,
        /// or the reason for the buyer.
        /// </summary>
        internal UITextKeys SellAcross(BasePlayerCharacterEntity buyerGhost, int index, string itemId, int price, out CharacterItem sold)
        {
            sold = CharacterItem.Empty;
            if (!Data.isStarted)
                return UITextKeys.UI_ERROR_INVALID_DATA;
            if (index < 0 || index >= _items.Count)
                return UITextKeys.UI_ERROR_INVALID_ITEM_INDEX;
            VendingItem entry = _items[index];
            if (entry.item.id != itemId || entry.price != price)
                return UITextKeys.UI_ERROR_INVALID_ITEM_DATA; // the list changed after the buyer's cell last heard it
            int inventoryIndex = Entity.NonEquipItems.IndexOf(entry.item.id);
            if (inventoryIndex < 0)
                return UITextKeys.UI_ERROR_INVALID_ITEM_DATA;
            if (!Entity.DecreaseItemsByIndex(inventoryIndex, entry.item.amount, true))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS);
                return UITextKeys.UI_ERROR_INVALID_ITEM_DATA;
            }
            sold = entry.item;
            _items.RemoveAt(index);
            if (_items.Count <= 0)
                StopVending(); // sold out, as the kit's sale ends it; the sign in the snapshot closes it across the line
            else
            {
                NotifyItems();
                NotifyRemoteCustomers();
            }
            if (buyerGhost != null)
                GameInstance.ServerLogHandlers.LogSellVendingItem(Entity, buyerGhost, sold, price);
            return UITextKeys.NONE;
        }

        /// <summary>The buyer's cell gave the item of a sale: the vendor is paid.</summary>
        internal void PaySale(int price)
        {
            Entity.Gold = Entity.Gold.Increase(price);
            Entity.OnRewardGold(RewardGivenType.Vending, price);
        }

        /// <summary>The buyer's cell gave no item: it goes back to the vendor's bag (not to the shop, which may have closed).</summary>
        internal void ReturnSale(CharacterItem item)
        {
            if (item.IsEmptySlot())
                return;
            Entity.IncreaseItems(item);
            Entity.FillEmptySlots();
        }

        // ---- the kit's server RPCs, again under the same names ----

        [ServerRpc]
        protected new void CmdSubscribe(uint objectId)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (!Manager.TryGetEntityByObjectId(objectId, out BasePlayerCharacterEntity vendor) || vendor.VendingComponent == null)
                return;
            if (!vendor.VendingComponent.Data.isStarted)
                return;
            CmdUnsubscribe();
            _store = vendor.VendingComponent;
            if (!(_store is MeshPlayerCharacterVendingComponent store))
                return;
            store.AddLocalCustomer(this);
            if (store.IsPuppet && Mesh != null)
            {
                Mesh.ForwardGhostAction(vendor, GhostActionKind.VendingSubscribe, Entity.GetInfo(), null);
                Logging.Log("Mesh", $"Vending: {Entity.CharacterName} opened {vendor.CharacterName}'s shop across a line, asked its owner for the items");
            }
#endif
        }

        [ServerRpc]
        protected new void CmdUnsubscribe()
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (_store == null)
                return;
            if (_store is MeshPlayerCharacterVendingComponent store)
            {
                store.RemoveLocalCustomer(this);
                if (store.IsPuppet && Mesh != null)
                {
                    string buyerId = Entity.Id;
                    Mesh.ForwardGhostAction(store.Entity, GhostActionKind.VendingUnsubscribe, Entity.GetInfo(), writer => writer.Put(buyerId));
                }
            }
            _store = null;
#endif
        }

        [ServerRpc]
        protected new void CmdBuyItem(int index)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (!(_store is MeshPlayerCharacterVendingComponent store) || store.Entity == null)
                return;
            if (!store.IsPuppet || Mesh == null)
            {
                store.SellItem(this, index); // the kit's sale, both sides on this cell
                store.NotifyRemoteCustomers();
                return;
            }
            VendingItems items = store._items;
            if (index < 0 || index >= items.Count)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_ITEM_INDEX);
                return;
            }
            VendingItem entry = items[index];
            if (Entity.Gold < entry.price)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ENOUGH_GOLD);
                return;
            }
            if (Entity.IncreasingItemsWillOverwhelming(new List<CharacterItem> { entry.item }))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_WILL_OVERWHELMING);
                return;
            }
            if (!Mesh.BeginVendingEscrow(Entity, store.Entity, index, entry))
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_INVALID_DATA); // a purchase already on its way
#endif
        }

        // ---- the shop with a handoff ----

        public string CarrierKey => Key;

        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            if (!ReferenceEquals(player, Entity) || IsPuppet)
                return false;
            bool shop = Data.isStarted && _items.Count > 0;
            List<MeshMapNetworkManager.SaleDecision> decisions = Mesh != null ? Mesh.SaleDecisionsOf(player.Id) : null;
            if (!shop && decisions == null)
                return false;
            writer.Put(shop);
            if (shop)
            {
                writer.Put(Data.title ?? string.Empty);
                writer.Put(_items.Count);
                for (int i = 0; i < _items.Count; ++i)
                {
                    writer.Put(_items[i].item.id ?? string.Empty);
                    writer.Put(_items[i].item.amount);
                    writer.Put(_items[i].price);
                }
            }
            writer.Put(decisions != null ? decisions.Count : 0);
            if (decisions != null)
            {
                foreach (MeshMapNetworkManager.SaleDecision d in decisions)
                {
                    writer.Put(d.saleId);
                    writer.Put(d.paid);
                    writer.Put(Time.unscaledTime - d.at);
                }
            }
            return true;
        }

        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            if (reader.GetBool())
                ReadShop(player, reader);
            int decisions = reader.GetInt();
            for (int i = 0; i < decisions; ++i)
            {
                string saleId = reader.GetString();
                bool paid = reader.GetBool();
                float age = reader.GetFloat();
                Mesh?.ImportSaleDecision(player.Id, saleId, paid, age);
            }
            if (decisions > 0)
                Logging.Log("Mesh", $"Vending: {player.CharacterName}'s word on {decisions} recent sale(s) carried with the handoff");
        }

        private void ReadShop(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            string title = reader.GetString();
            int count = reader.GetInt();
            VendingItems items = new VendingItems();
            for (int i = 0; i < count; ++i)
            {
                string id = reader.GetString();
                int amount = reader.GetInt();
                int price = reader.GetInt();
                int index = player.NonEquipItems.IndexOf(id);
                if (index < 0 || amount <= 0)
                    continue;
                CharacterItem item = player.NonEquipItems[index].Clone(false);
                item.amount = Mathf.Min(amount, player.NonEquipItems[index].amount);
                items.Add(new VendingItem { item = item, price = price });
            }
            if (items.Count == 0)
                return;
            _items = items;
            data.Value = new VendingData { isStarted = true, title = title };
            Logging.Log("Mesh", $"Vending: {player.CharacterName}'s shop '{title}' carried with the handoff, {items.Count} item(s)");
        }
    }
}
