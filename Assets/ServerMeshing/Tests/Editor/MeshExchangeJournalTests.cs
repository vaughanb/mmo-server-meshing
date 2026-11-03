using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The exchange journal a cell writes ahead of every step and reads as it starts (DESIGN 1.52, 1.54).</summary>
    public class MeshExchangeJournalTests
    {
        private static CharacterItem Item(string id, int dataId, int amount)
        {
            return new CharacterItem { id = id, dataId = dataId, amount = amount, level = 1 };
        }

        private static MeshExchangeJournal Filled()
        {
            MeshExchangeJournal journal = new MeshExchangeJournal { mapId = "Map1", cellId = "C.1" };
            MeshExchangeJournal.Trade trade = new MeshExchangeJournal.Trade
            {
                tradeId = "t1", traderId = "a", traderUserId = "ua", traderName = "A", otherMeshId = "b", otherZone = "E",
                goldTaken = 7, goldOffered = 5, age = 12.5f, pending = true,
            };
            trade.items.Add(Item("i1", 357861288, 1));
            trade.items.Add(Item("i2", 418037898, 3));
            journal.trades.Add(trade);
            journal.purchases.Add(new MeshExchangeJournal.Purchase { saleId = "s1", buyerId = "b", buyerUserId = "ub", buyerName = "B", vendorName = "V", gold = 7, pending = true });
            journal.sales.Add(new MeshExchangeJournal.Sale
            {
                saleId = "s2", vendorId = "v", vendorUserId = "uv", vendorName = "V", buyerId = "b", buyerName = "B", buyerZone = "C",
                item = Item("i3", 1275161172, 1), price = 7, age = 40f,
            });
            MeshExchangeJournal.Settlement settlement = new MeshExchangeJournal.Settlement
            {
                kind = MeshExchangeJournal.SettlementKind.TradeSettled, exchangeId = "t3", characterId = "a", userId = "ua", characterName = "A",
                otherName = "B", state = MeshExchangeJournal.StepDelivered, gold = 5,
            };
            settlement.items.Add(Item("i5", 357861288, 2));
            journal.settlements.Add(settlement);
            MeshExchangeJournal.Mail mail = new MeshExchangeJournal.Mail
            {
                reference = "r1", userId = "uv", characterName = "V", title = "Shop sale", content = "gold (reference r1)", what = "sale s9", gold = 7, sentAt = 1793664000L,
            };
            mail.items.Add(Item("i6", 418037898, 1));
            journal.mails.Add(mail);
            journal.saleWords.Add(new MeshExchangeJournal.SaleWord { buyerId = "b", saleId = "s3", paid = true, age = 3f });
            MeshExchangeJournal.TradeWord word = new MeshExchangeJournal.TradeWord
            {
                traderId = "b", tradeId = "t2", otherId = "a", otherZone = "C", committed = true, reason = 0, goldGiven = 5, age = 8f,
            };
            word.taken.Add(Item("i4", 418037898, 1));
            journal.tradeWords.Add(word);
            journal.settledTrades.Add("t0");
            return journal;
        }

        private static MeshExchangeJournal RoundTrip(MeshExchangeJournal journal)
        {
            NetDataWriter writer = new NetDataWriter();
            journal.Write(writer);
            return MeshExchangeJournal.Read(new NetDataReader(writer.CopyData()));
        }

        [Test]
        public void EveryRecordComesBack()
        {
            MeshExchangeJournal read = RoundTrip(Filled());

            Assert.That(read, Is.Not.Null);
            Assert.That((read.mapId, read.cellId), Is.EqualTo(("Map1", "C.1")));
            Assert.That(read.Open, Is.EqualTo(5));
            MeshExchangeJournal.Trade t = read.trades[0];
            Assert.That((t.tradeId, t.traderId, t.traderUserId, t.traderName, t.otherMeshId, t.otherZone), Is.EqualTo(("t1", "a", "ua", "A", "b", "E")));
            Assert.That((t.goldTaken, t.goldOffered, t.age, t.pending), Is.EqualTo((7, 5, 12.5f, true)));
            Assert.That(t.items.Count, Is.EqualTo(2));
            Assert.That((t.items[1].id, t.items[1].dataId, t.items[1].amount), Is.EqualTo(("i2", 418037898, 3)));
            MeshExchangeJournal.Purchase p = read.purchases[0];
            Assert.That((p.saleId, p.buyerId, p.buyerUserId, p.buyerName, p.vendorName, p.gold, p.pending), Is.EqualTo(("s1", "b", "ub", "B", "V", 7, true)));
            MeshExchangeJournal.Sale s = read.sales[0];
            Assert.That((s.saleId, s.vendorId, s.vendorUserId, s.buyerZone, s.price, s.age), Is.EqualTo(("s2", "v", "uv", "C", 7, 40f)));
            Assert.That((s.item.id, s.item.dataId, s.pending), Is.EqualTo(("i3", 1275161172, false)));
            Assert.That((read.saleWords[0].saleId, read.saleWords[0].paid), Is.EqualTo(("s3", true)));
            MeshExchangeJournal.TradeWord w = read.tradeWords[0];
            Assert.That((w.traderId, w.tradeId, w.otherId, w.otherZone, w.committed, w.goldGiven), Is.EqualTo(("b", "t2", "a", "C", true, 5)));
            Assert.That(w.taken[0].dataId, Is.EqualTo(418037898));
            Assert.That(read.settledTrades, Is.EqualTo(new[] { "t0" }));
            MeshExchangeJournal.Settlement st = read.settlements[0];
            Assert.That((st.kind, st.exchangeId, st.characterId, st.userId, st.characterName, st.otherName, st.state, st.gold),
                Is.EqualTo((MeshExchangeJournal.SettlementKind.TradeSettled, "t3", "a", "ua", "A", "B", MeshExchangeJournal.StepDelivered, 5)));
            Assert.That((st.items[0].id, st.items[0].amount), Is.EqualTo(("i5", 2)));
            MeshExchangeJournal.Mail m = read.mails[0];
            Assert.That((m.reference, m.userId, m.characterName, m.title, m.content, m.what, m.gold, m.sentAt),
                Is.EqualTo(("r1", "uv", "V", "Shop sale", "gold (reference r1)", "sale s9", 7, 1793664000L)));
            Assert.That(m.items[0].dataId, Is.EqualTo(418037898));
        }

        [Test]
        public void TheMarkerKeyIsStableAndNeverZero()
        {
            // Read back by another process from the player's saved data: it is FNV-1a, not string.GetHashCode.
            Assert.That(MeshExchangeJournal.MarkerKey(""), Is.EqualTo(unchecked((int)Fnv("mesh.exchange."))));
            Assert.That(MeshExchangeJournal.MarkerKey("0123456789abcdef"), Is.EqualTo(unchecked((int)Fnv("mesh.exchange.0123456789abcdef"))));
            Assert.That(MeshExchangeJournal.MarkerKey("a"), Is.Not.EqualTo(MeshExchangeJournal.MarkerKey("b")));
            Assert.That(MeshExchangeJournal.MarkerKey(null), Is.Not.Zero);
        }

        private static uint Fnv(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        [Test]
        public void AStepThatReachedTheDatabaseLanded()
        {
            foreach (MeshExchangeJournal.SettlementKind kind in System.Enum.GetValues(typeof(MeshExchangeJournal.SettlementKind)))
                Assert.That(MeshExchangeJournal.Resolve(kind, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepDelivered), Is.EqualTo(MeshExchangeJournal.Outcome.Landed), kind.ToString());
        }

        [Test]
        public void APurchaseWhoseItemDidNotLandIsRefundedOrNotPaid()
        {
            // The database still has the escrow: the price goes back by mail; refunded already (a late item) or never taken: not paid.
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.PurchaseBought, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepTaken),
                Is.EqualTo(MeshExchangeJournal.Outcome.RefundByMail));
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.PurchaseBought, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepReturned),
                Is.EqualTo(MeshExchangeJournal.Outcome.WordNotPaid));
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.PurchaseBought, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepNone),
                Is.EqualTo(MeshExchangeJournal.Outcome.WordNotPaid));
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.PurchaseRefunded, MeshExchangeJournal.StepReturned, MeshExchangeJournal.StepTaken),
                Is.EqualTo(MeshExchangeJournal.Outcome.RefundByMail));
        }

        [Test]
        public void ASettlementThatDidNotLandIsMailed()
        {
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.SaleSettled, MeshExchangeJournal.StepReturned, MeshExchangeJournal.StepTaken),
                Is.EqualTo(MeshExchangeJournal.Outcome.MailIntended));
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.TradeSettled, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepTaken),
                Is.EqualTo(MeshExchangeJournal.Outcome.MailIntended));
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.TradeSettled, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepNone),
                Is.EqualTo(MeshExchangeJournal.Outcome.Nothing));
        }

        [Test]
        public void AnAnsweringHalfThatDidNotLandIsFailed()
        {
            Assert.That(MeshExchangeJournal.Resolve(MeshExchangeJournal.SettlementKind.TradeAnswered, MeshExchangeJournal.StepDelivered, MeshExchangeJournal.StepNone),
                Is.EqualTo(MeshExchangeJournal.Outcome.WordFailed));
        }

        [Test]
        public void AnEscrowIsKeptOnlyIfItReachedTheDatabase()
        {
            Assert.That(MeshExchangeJournal.KeepsOpen(false, MeshExchangeJournal.StepNone), Is.True);
            Assert.That(MeshExchangeJournal.KeepsOpen(true, MeshExchangeJournal.StepTaken), Is.True);
            Assert.That(MeshExchangeJournal.KeepsOpen(true, MeshExchangeJournal.StepNone), Is.False);
        }

        [Test]
        public void AnEmptyJournalIsEmpty()
        {
            MeshExchangeJournal read = RoundTrip(new MeshExchangeJournal { mapId = "Map1", cellId = "C" });

            Assert.That(read.IsEmpty, Is.True);
            Assert.That(read.Open, Is.Zero);
        }

        [Test]
        public void AJournalOfAnotherVersionIsNotRead()
        {
            NetDataWriter writer = new NetDataWriter();
            Filled().Write(writer);
            byte[] data = writer.CopyData();
            data[0] = (byte)(MeshExchangeJournal.Version + 1);

            Assert.That(MeshExchangeJournal.Read(new NetDataReader(data)), Is.Null);
        }

        [Test]
        public void TheFileNameIsSafeForAPath()
        {
            Assert.That(MeshExchangeJournal.FileName("Map 1", "C.1/x"), Is.EqualTo("ServerMesh.Exchanges.Map_1.C.1_x.bin"));
            Assert.That(MeshExchangeJournal.FileName(null, ""), Is.EqualTo("ServerMesh.Exchanges._._.bin"));
        }
    }
}
