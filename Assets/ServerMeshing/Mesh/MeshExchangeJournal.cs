// Server Meshing add-on — the open exchanges of a cell, written as it stops and read back as it starts again (DESIGN.md
// 1.52).
//
// A trade's escrow, a purchase's escrow and a vendor's pending sale live in the cell's memory until the other cell's
// answer settles them (MeshMapNetworkManager_Social, _Vending), and so do the answers a cell gave (so the same question
// gets the same answer). A cell that stops cleanly (an operator's stop, a deploy, the kit's quit path: it saves every
// player first) writes them here; the next process of the same cell reads them, settles what it can at once and asks
// about the rest, mailing what is owed to players who are gone (MeshMapNetworkManager_Delivery). The file is the cell's:
// one per map and cell id, in the add-on's data folder. Game-neutral codec.
//
// DESIGN 1.54: the journal is written ahead of every step, so a crash leaves it as current as a clean stop does. A step
// that changes a player (an escrow taken, an offer or an item given, gold back) is `pending` until the player's save is
// accepted, and leaves a marker in the player's own saved data (a server int keyed by the exchange id, MarkerKey) in
// the same save: a restarted cell reads the marker back from the database for every pending record and keeps the step
// only if it reached it (Resolve). A mail is recorded with a reference written into its text until the database
// accepted it, and a restarted cell looks for that reference in the mailbox before it sends it again.
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshExchangeJournal is a cell's open exchanges and recent answers, with ages in seconds at the time it was written.</summary>
    public sealed class MeshExchangeJournal
    {
        public const byte Version = 2;

        /// <summary>The marker states a step leaves in a player's saved data (DESIGN 1.54); 0 (no marker) is "never reached the database".</summary>
        public const byte StepNone = 0, StepTaken = 1, StepDelivered = 2, StepReturned = 3;

        /// <summary>The settling steps a cell records until the player's save is accepted.</summary>
        public enum SettlementKind : byte
        {
            PurchaseBought = 1,   // the buyer received the item: its escrow spent, or the price taken again
            PurchaseRefunded = 2, // the buyer's escrow given back
            SaleSettled = 3,      // the vendor paid (Delivered) or its item back in its bag (Returned)
            TradeSettled = 4,     // the escrow's trader received the other's offer (Delivered) or its own back (Returned)
            TradeAnswered = 5,    // the answering trader gave its offer and received the other's (Delivered)
        }

        /// <summary>What a restarted cell does with a settling step, given the marker the database holds.</summary>
        public enum Outcome : byte
        {
            Landed,        // the step reached the database: nothing to do (a purchase's word stays paid)
            MailIntended,  // the step did not, the exchange did: what it gave goes by mail
            RefundByMail,  // a purchase still in escrow in the database: its price goes back by mail, its word not paid
            WordNotPaid,   // the buyer never received the item: the word on the sale is not paid
            WordFailed,    // the answering trader's half did not reach the database: the word on the trade is failed
            Nothing,
        }

        /// <summary>A trade escrow on the cell that began the exchange: the trader's offer, out of its inventory.</summary>
        public sealed class Trade
        {
            public string tradeId, traderId, traderUserId, traderName, otherMeshId, otherZone;
            public List<CharacterItem> items = new List<CharacterItem>();
            public int goldTaken, goldOffered;
            public float age;
            public bool pending; // the escrow taken, the trader's save not yet accepted (DESIGN 1.54)
        }

        /// <summary>A purchase's escrow on the buyer's cell: the price, out of its gold.</summary>
        public sealed class Purchase
        {
            public string saleId, buyerId, buyerUserId, buyerName, vendorName;
            public int gold;
            public bool pending;
        }

        /// <summary>A vendor's sale waiting for the buyer's cell's word: the item, out of the vendor's bag.</summary>
        public sealed class Sale
        {
            public string saleId, vendorId, vendorUserId, vendorName, buyerId, buyerName, buyerZone;
            public CharacterItem item;
            public int price;
            public float age;
            public bool pending;
        }

        /// <summary>A settling step on a player, recorded until its save is accepted (DESIGN 1.54): what it gave, should it go by mail.</summary>
        public sealed class Settlement
        {
            public SettlementKind kind;
            public string exchangeId, characterId, userId, characterName, otherName;
            public byte state; // the marker it set
            public List<CharacterItem> items = new List<CharacterItem>();
            public int gold;
        }

        /// <summary>A mail not yet accepted by the database (DESIGN 1.54), with the reference written into its text.</summary>
        public sealed class Mail
        {
            public string reference, userId, characterName, title, content, what;
            public List<CharacterItem> items = new List<CharacterItem>();
            public int gold;
            public long sentAt; // unix seconds, when it was first sent
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
        public readonly List<Settlement> settlements = new List<Settlement>();
        public readonly List<Mail> mails = new List<Mail>();

        /// <summary>Open is the count of exchanges still to settle (escrows, sales, steps and mails not yet accepted; the words are records only).</summary>
        public int Open => trades.Count + purchases.Count + sales.Count + settlements.Count + mails.Count;

        public bool IsEmpty => Open == 0 && saleWords.Count == 0 && tradeWords.Count == 0 && settledTrades.Count == 0;

        /// <summary>
        /// MarkerKey is the server int a step on an exchange sets in the player's saved data: FNV-1a over the exchange id,
        /// never 0. Stable across processes and builds (a restarted cell reads it back).
        /// </summary>
        public static int MarkerKey(string exchangeId)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in "mesh.exchange." + (exchangeId ?? string.Empty))
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash == 0 ? 1 : (int)hash;
            }
        }

        /// <summary>
        /// Resolve says what a restarted cell does with a settling step it recorded (`recorded`, the marker the step set),
        /// given the marker the player's saved data holds (`landed`). A step whose marker is there reached the database.
        /// </summary>
        public static Outcome Resolve(SettlementKind kind, byte recorded, byte landed)
        {
            if (landed == recorded)
                return Outcome.Landed;
            switch (kind)
            {
                case SettlementKind.PurchaseBought:
                    return landed == StepTaken ? Outcome.RefundByMail : Outcome.WordNotPaid;
                case SettlementKind.PurchaseRefunded:
                    return landed == StepTaken ? Outcome.RefundByMail : Outcome.Nothing;
                case SettlementKind.SaleSettled:
                case SettlementKind.TradeSettled:
                    return landed == StepTaken ? Outcome.MailIntended : Outcome.Nothing;
                case SettlementKind.TradeAnswered:
                    return Outcome.WordFailed;
            }
            return Outcome.Nothing;
        }

        /// <summary>An escrow or sale recorded as taken: kept when its marker reached the database (or it was not pending), dropped otherwise.</summary>
        public static bool KeepsOpen(bool pending, byte landed)
        {
            return !pending || landed == StepTaken;
        }

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
                writer.Put(t.pending);
            }
            writer.Put(purchases.Count);
            foreach (Purchase p in purchases)
            {
                PutStrings(writer, p.saleId, p.buyerId, p.buyerUserId, p.buyerName, p.vendorName);
                writer.Put(p.gold);
                writer.Put(p.pending);
            }
            writer.Put(sales.Count);
            foreach (Sale s in sales)
            {
                PutStrings(writer, s.saleId, s.vendorId, s.vendorUserId, s.vendorName, s.buyerId, s.buyerName, s.buyerZone);
                writer.Put(s.item);
                writer.Put(s.price);
                writer.Put(s.age);
                writer.Put(s.pending);
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
            writer.Put(settlements.Count);
            foreach (Settlement s in settlements)
            {
                writer.Put((byte)s.kind);
                PutStrings(writer, s.exchangeId, s.characterId, s.userId, s.characterName, s.otherName);
                writer.Put(s.state);
                PutItems(writer, s.items);
                writer.Put(s.gold);
            }
            writer.Put(mails.Count);
            foreach (Mail m in mails)
            {
                PutStrings(writer, m.reference, m.userId, m.characterName, m.title, m.content, m.what);
                PutItems(writer, m.items);
                writer.Put(m.gold);
                writer.Put(m.sentAt);
            }
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
                t.pending = reader.GetBool();
                journal.trades.Add(t);
            }
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.purchases.Add(new Purchase
                {
                    saleId = reader.GetString(), buyerId = reader.GetString(), buyerUserId = reader.GetString(),
                    buyerName = reader.GetString(), vendorName = reader.GetString(), gold = reader.GetInt(), pending = reader.GetBool(),
                });
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
                journal.sales.Add(new Sale
                {
                    saleId = reader.GetString(), vendorId = reader.GetString(), vendorUserId = reader.GetString(),
                    vendorName = reader.GetString(), buyerId = reader.GetString(), buyerName = reader.GetString(),
                    buyerZone = reader.GetString(), item = reader.Get<CharacterItem>(), price = reader.GetInt(), age = reader.GetFloat(),
                    pending = reader.GetBool(),
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
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
            {
                Settlement s = new Settlement
                {
                    kind = (SettlementKind)reader.GetByte(), exchangeId = reader.GetString(), characterId = reader.GetString(),
                    userId = reader.GetString(), characterName = reader.GetString(), otherName = reader.GetString(), state = reader.GetByte(),
                };
                GetItems(reader, s.items);
                s.gold = reader.GetInt();
                journal.settlements.Add(s);
            }
            n = reader.GetInt();
            for (int i = 0; i < n; ++i)
            {
                Mail m = new Mail
                {
                    reference = reader.GetString(), userId = reader.GetString(), characterName = reader.GetString(),
                    title = reader.GetString(), content = reader.GetString(), what = reader.GetString(),
                };
                GetItems(reader, m.items);
                m.gold = reader.GetInt();
                m.sentAt = reader.GetLong();
                journal.mails.Add(m);
            }
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
