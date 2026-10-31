#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The mailbox. A mail goes to its receiver only, and its read, claim and delete flags are
    /// what stop attachments being claimed twice, so they must stick and only the receiver may
    /// set them. Gold, cash and items attached to a mail must arrive as sent.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteMailTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _senderId;
        private string _receiverId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _senderId = _db.CreateUser("sender", "secret");
            _receiverId = _db.CreateUser("receiver", "secret");
        }

        /// <summary>
        /// A player-to-player mail as the database server receives it. The map server leaves the
        /// event id null, and the network message turns null into an empty string on the way.
        /// </summary>
        private Mail NewMail(string title = "Hello", int gold = 0, int cash = 0)
        {
            return new Mail()
            {
                EventId = string.Empty,
                SenderId = _senderId,
                SenderName = "Sender",
                ReceiverId = _receiverId,
                Title = title,
                Content = "Some words",
                Gold = gold,
                Cash = cash,
            };
        }

        /// <summary>Sends a mail and returns its id.</summary>
        private string Send(Mail mail)
        {
            Assert.That(TestAsync.Wait(_db.Database.CreateMail(mail)), Is.EqualTo(1), "one mail inserted");
            return TestAsync.Wait(_db.Database.MailList(mail.ReceiverId, false)).First(m => m.Title == mail.Title).Id;
        }

        private Mail Get(string mailId, string userId = null)
        {
            return TestAsync.Wait(_db.Database.GetMail(mailId, userId ?? _receiverId));
        }

        [Test]
        public void ASentMailIsListedForTheReceiverOnly()
        {
            Send(NewMail("Hello"));

            List<MailListEntry> inbox = TestAsync.Wait(_db.Database.MailList(_receiverId, false));
            Assert.That(inbox.Select(m => m.Title), Is.EqualTo(new[] { "Hello" }));
            Assert.That(inbox[0].SenderName, Is.EqualTo("Sender"));
            Assert.That(inbox[0].IsRead, Is.False);
            Assert.That(inbox[0].SentTimestamp, Is.GreaterThan(0));
            Assert.That(TestAsync.Wait(_db.Database.MailList(_senderId, false)), Is.Empty);
        }

        [Test]
        public void ASentMailReadsBackAsSent()
        {
            string id = Send(NewMail("Payment", gold: 250, cash: 3));

            Mail mail = Get(id);
            Assert.That(mail.Id, Is.EqualTo(id));
            Assert.That(mail.SenderId, Is.EqualTo(_senderId));
            Assert.That(mail.SenderName, Is.EqualTo("Sender"));
            Assert.That(mail.ReceiverId, Is.EqualTo(_receiverId));
            Assert.That(mail.Title, Is.EqualTo("Payment"));
            Assert.That(mail.Content, Is.EqualTo("Some words"));
            Assert.That(mail.Gold, Is.EqualTo(250));
            Assert.That(mail.Cash, Is.EqualTo(3));
            Assert.That(mail.IsRead, Is.False);
            Assert.That(mail.IsClaim, Is.False);
        }

        [Test]
        public void AMailWithANullEventIdOpens()
        {
            Mail sent = NewMail("No event");
            sent.EventId = null;
            string id = Send(sent);

            Assert.That(Get(id).Title, Is.EqualTo("No event"));
        }

        [Test]
        public void AnotherUserCannotOpenOrChangeTheMail()
        {
            string id = Send(NewMail(gold: 10));

            Assert.That(Get(id, _senderId).Id, Is.Null, "not found for the sender");
            Assert.That(TestAsync.Wait(_db.Database.UpdateReadMailState(id, _senderId)), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.UpdateClaimMailItemsState(id, _senderId)), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.UpdateDeleteMailState(id, _senderId)), Is.Zero);

            Mail mail = Get(id);
            Assert.That(mail.IsRead, Is.False);
            Assert.That(mail.IsClaim, Is.False);
        }

        [Test]
        public void ReadingAMailMarksItRead()
        {
            string id = Send(NewMail());

            Assert.That(TestAsync.Wait(_db.Database.UpdateReadMailState(id, _receiverId)), Is.EqualTo(1));

            Mail mail = Get(id);
            Assert.That(mail.IsRead, Is.True);
            Assert.That(mail.ReadTimestamp, Is.GreaterThan(0));
        }

        [Test]
        public void ClaimingAMailMarksItClaimedAndClearsTheNotification()
        {
            string id = Send(NewMail(gold: 100));
            TestAsync.Wait(_db.Database.UpdateReadMailState(id, _receiverId));
            Assert.That(TestAsync.Wait(_db.Database.GetMailNotification(_receiverId)), Is.EqualTo(1), "read but unclaimed still notifies");

            Assert.That(TestAsync.Wait(_db.Database.UpdateClaimMailItemsState(id, _receiverId)), Is.EqualTo(1));

            Mail mail = Get(id);
            Assert.That(mail.IsClaim, Is.True);
            Assert.That(mail.ClaimTimestamp, Is.GreaterThan(0));
            Assert.That(TestAsync.Wait(_db.Database.GetMailNotification(_receiverId)), Is.Zero);
        }

        [Test]
        public void ADeletedMailIsGoneFromTheMailbox()
        {
            string id = Send(NewMail("Spam"));
            Send(NewMail("Keep"));

            Assert.That(TestAsync.Wait(_db.Database.UpdateDeleteMailState(id, _receiverId)), Is.EqualTo(1));

            Assert.That(TestAsync.Wait(_db.Database.MailList(_receiverId, false)).Select(m => m.Title), Is.EqualTo(new[] { "Keep" }));
            Assert.That(Get(id).Id, Is.Null);
        }

        [Test]
        public void NewMailsAreTheUnreadAndTheUnclaimed()
        {
            string readNote = Send(NewMail("Read note"));
            string claimedGift = Send(NewMail("Claimed gift", gold: 5));
            Send(NewMail("Unread note"));
            string readGift = Send(NewMail("Read gift", gold: 5));
            TestAsync.Wait(_db.Database.UpdateReadMailState(readNote, _receiverId));
            TestAsync.Wait(_db.Database.UpdateReadMailState(claimedGift, _receiverId));
            TestAsync.Wait(_db.Database.UpdateClaimMailItemsState(claimedGift, _receiverId));
            TestAsync.Wait(_db.Database.UpdateReadMailState(readGift, _receiverId));

            List<MailListEntry> newMails = TestAsync.Wait(_db.Database.MailList(_receiverId, true));

            Assert.That(newMails.Select(m => m.Title), Is.EquivalentTo(new[] { "Unread note", "Read gift" }));
            Assert.That(TestAsync.Wait(_db.Database.GetMailNotification(_receiverId)), Is.EqualTo(2));
        }

        [Test]
        public void AttachedItemsArriveAsSent()
        {
            UseGameInstance();
            Item potion = Objects.Create<Item>("TestMailPotion");
            GameInstance.Items[potion.DataId] = potion;
            Mail mail = NewMail("Gift");
            CharacterItem item = new CharacterItem() { id = "gift-1", dataId = potion.DataId, level = 2, amount = 5, durability = 7.5f, randomSeed = 33 };
            item.sockets.socket1 = 401;
            mail.Items.Add(item);

            string id = Send(mail);

            Mail loaded = Get(id);
            Assert.That(loaded.Items.Count, Is.EqualTo(1));
            Assert.That(loaded.Items[0].dataId, Is.EqualTo(potion.DataId));
            Assert.That(loaded.Items[0].level, Is.EqualTo(2));
            Assert.That(loaded.Items[0].amount, Is.EqualTo(5));
            Assert.That(loaded.Items[0].durability, Is.EqualTo(7.5f));
            Assert.That(loaded.Items[0].randomSeed, Is.EqualTo(33));
            Assert.That(loaded.Items[0].sockets.socket1, Is.EqualTo(401));
        }

        [Test]
        public void AttachedCurrenciesArriveAsSent()
        {
            Mail mail = NewMail("Tokens");
            mail.Currencies.Add(new CharacterCurrency() { dataId = 901, amount = 15 });
            mail.Currencies.Add(new CharacterCurrency() { dataId = 902, amount = 2 });

            string id = Send(mail);

            Mail loaded = Get(id);
            Assert.That(loaded.Currencies.Select(c => $"{c.dataId}:{c.amount}"), Is.EqualTo(new[] { "901:15", "902:2" }));
        }
    }
}
#endif
