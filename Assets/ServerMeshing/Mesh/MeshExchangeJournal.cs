// Server Meshing add-on — the open exchanges of a cell, written as it stops and read back as it starts again (DESIGN.md
// 1.52).
//
// A trade's escrow, a purchase's escrow and a vendor's pending sale live in the cell's memory until the other cell's
// answer settles them (MeshMapNetworkManager_Social, _Vending), and so do the answers a cell gave (so the same question
// gets the same answer). A cell that stops cleanly (an operator's stop, a deploy, the kit's quit path: it saves every
// player first) writes them here; the next process of the same cell reads them, settles what it can at once and asks
// about the rest, mailing what is owed to players who are gone (MeshMapNetworkManager_Delivery). A crash writes nothing:
// the kit itself keeps a character's state no fresher than its last save (every 20 s at most), and the exchanges follow
// whatever was saved. The file is the cell's: one per map and cell id, in the add-on's data folder. Game-neutral codec.
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshExchangeJournal is a cell's open exchanges and recent answers, with ages in seconds at the time it was written.</summary>
    public sealed class MeshExchangeJournal
    {
        public const byte Version = 1;

        /// <summary>A trade escrow on the cell that began the exchange: the trader's offer, out of its inventory.</summary>
        public sealed class Trade
        {
            public string tradeId, traderId, traderUserId, traderName, otherMeshId, otherZone;
            public List<CharacterItem> items = new List<CharacterItem>();
            public int goldTaken, goldOffered;
            public float age;
        }

        /// <summary>A purchase's escrow on the buyer's cell: the price, out of its gold.</summary>
        public sealed class Purchase
        {
            public string saleId, buyerId, buyerUserId, buyerName, vendorName;
            public int gold;
        }

        /// <summary>A vendor's sale waiting for the buyer's cell's word: the item, out of the vendor's bag.</summary>
        public sealed class Sale
        {
            public string saleId, vendorId, vendorUserId, vendorName, buyerId, buyerName, buyerZone;
            public CharacterItem item;
            public int price;
            public float age;
        }

        /// <summary>A buyer's cell's word on a sale.</summary>
        public sealed class SaleWord
        {
            public string buyerId, saleId;
            public bool paid;
            public float age;
        }

        /// <summary>The answering cell's word on a trade (MeshMapNetworkManager.DealDecision's fields).</summary>
        public sealed class TradeWord
        {
            public string traderId, tradeId, otherId, otherZone;
            public bool committed;
            public ushort reason;
            public List<CharacterItem> taken = new List<CharacterItem>();
            public int goldGiven;
            public float age;
        }

        public string mapId, cellId;
        public readonly List<Trade> trades = new List<Trade>();
        public readonly List<Purchase> purchases = new List<Purchase>();
        public readonly List<Sale> sales = new List<Sale>();
        public readonly List<SaleWord> saleWords = new List<SaleWord>();
        public readonly List<TradeWord> tradeWords = new List<TradeWord>();
        /// <summary>Trade ids this cell settled lately: a repeated answer for one changes nothing after the restart either.</summary>
        public readonly List<string> settledTrades = new List<string>();

        /// <summary>Open is the count of exchanges still to settle (escrows and sales; the words are records only).</summary>
        public int Open => trades.Count + purchases.Count + sales.Count;

        public bool IsEmpty => Open == 0 && saleWords.Count == 0 && tradeWords.Count == 0 && settledTrades.Count == 0;

        public void Write(NetDataWriter writer)
        {
            writer.Put(Version);
            writer.Put(mapId ?? string.Empty);
            writer.Put(cellId ?? string.Empty);
            writer.Put(trades.Count);
            foreach (Trade t in trades)
            {
                PutStrings(writer, t.tradeId, t.traderId, t.traderUserId, t.traderName, t.otherMeshId, t.otherZone);
                PutItems(writer, t.items);
                writer.Put(t.goldTaken);
                writer.Put(t.goldOffered);
                writer.Put(t.age);
            }
            writer.Put(purchases.Count);
            foreach (Purchase p in purchases)
            {
                PutStrings(writer, p.saleId, p.buyerId, p.buyerUserId, p.buyerName, p.vendorName);
                writer.Put(p.gold);
            }
            writer.Put(sales.Count);
            foreach (Sale s in sales)
            {
                PutStrings(writer, s.saleId, s.vendorId, s.vendorUserId, s.vendorName, s.buyerId, s.buyerName, s.buyerZone);
                writer.Put(s.item);
                writer.Put(s.price);
                writer.Put(s.age);
            }
            writer.Put(saleWords.Count);
            foreach (SaleWord w in saleWords)
            {
                PutStrings(writer, w.buyerId, w.saleId);
                writer.Put(w.paid);
                writer.Put(w.age);
            }
            writer.Put(tradeWords.Count);
            foreach (TradeWord w in tradeWords)
            {
                PutStrings(writer, w.traderId, w.tradeId, w.otherId, w.otherZone);
                writer.Put(w.committed);
                writer.Put(w.reason);
                PutItems(writer, w.taken);
                writer.Put(w.goldGiven);
                writer.Put(w.age);
            }
            writer.Put(settledTrades.Count);
            foreach (string id in settledTrades)
                writer.Put(id ?? string.Empty);
        }

        /// <summary>Read is null for a journal of another version (it is not read; the cell starts without it).</summary>
        public static MeshExchangeJournal Read(NetDataReader reader)
        {
            if (reader.GetByte() != Version)
                return null;
            MeshExchangeJournal journal = new MeshExchangeJournal { mapId = reader.GetString(), cellId = reader.GetString() };
            int n = reader.GetInt();
            for (int i = 0; i < n; ++i)
            {
                Trade t = new Trade
                {
                    tradeId = reader.GetString(), traderId = reader.GetString(), traderUserId = reader.GetString(),
                    traderName = reader.GetString(), otherMeshId = reader.GetString(), otherZone = reader.GetString(),
                };
                GetItems(reader, t.items);
                t.goldTaken = reader.GetInt();
                t.goldOffered = reader.GetInt();
                t.age = reader.GetFloat();
                journal.trades.Add(t);
            }
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.purchases.Add(new Purchase
                {
                    saleId = reader.GetString(), buyerId = reader.GetString(), buyerUserId = reader.GetString(),
                    buyerName = reader.GetString(), vendorName = reader.GetString(), gold = reader.GetInt(),
                });
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.sales.Add(new Sale
                {
                    saleId = reader.GetString(), vendorId = reader.GetString(), vendorUserId = reader.GetString(),
                    vendorName = reader.GetString(), buyerId = reader.GetString(), buyerName = reader.GetString(),
                    buyerZone = reader.GetString(), item = reader.Get<CharacterItem>(), price = reader.GetInt(), age = reader.GetFloat(),
                });
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.saleWords.Add(new SaleWord { buyerId = reader.GetString(), saleId = reader.GetString(), paid = reader.GetBool(), age = reader.GetFloat() });
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
            {
                TradeWord w = new TradeWord
                {
                    traderId = reader.GetString(), tradeId = reader.GetString(), otherId = reader.GetString(), otherZone = reader.GetString(),
                    committed = reader.GetBool(), reason = reader.GetUShort(),
                };
                GetItems(reader, w.taken);
                w.goldGiven = reader.GetInt();
                w.age = reader.GetFloat();
                journal.tradeWords.Add(w);
            }
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.settledTrades.Add(reader.GetString());
            return journal;
        }

        /// <summary>FileName is the journal's file for a map's cell (ids made safe for a path).</summary>
        public static string FileName(string mapId, string cellId)
        {
            return $"ServerMesh.Exchanges.{Safe(mapId)}.{Safe(cellId)}.bin";
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "_";
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_' && chars[i] != '.')
                    chars[i] = '_';
            return new string(chars);
        }

        private static void PutStrings(NetDataWriter writer, params string[] values)
        {
            foreach (string v in values)
                writer.Put(v ?? string.Empty);
        }

        private static void PutItems(NetDataWriter writer, List<CharacterItem> items)
        {
            writer.Put(items != null ? items.Count : 0);
            if (items != null)
                foreach (CharacterItem item in items)
                    writer.Put(item);
        }

        private static void GetItems(NetDataReader reader, List<CharacterItem> items)
        {
            int n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                items.Add(reader.Get<CharacterItem>());
        }
    }
}
