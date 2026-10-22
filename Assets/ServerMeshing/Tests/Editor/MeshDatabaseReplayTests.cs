using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>DESIGN.md 0.89: a stranded read, or a request that never left, is sent again; a write that went out never is.</summary>
    public class MeshDatabaseReplayTests
    {
        private const ushort RequestMessageType = 1;

        /// <summary>A packet as TransportHandler.CreateAndWriteRequest writes it.</summary>
        private static NetDataWriter Request(ushort requestType, uint requestId, string payload = "x")
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(RequestMessageType);
            writer.PutPackedUShort(requestType);
            writer.PutPackedUInt(requestId);
            writer.Put(payload);
            return writer;
        }

        [Test]
        public void OnlyReadsMayBeSentTwice()
        {
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.ValidateAccessToken), Is.True);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.GetGold), Is.True);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.GetCharacter), Is.True);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.MailList), Is.True);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.ChangeGold), Is.False, "a gold change applied before the drop would apply twice");
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.UpdateCharacter), Is.False);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.SendMail), Is.False);
            Assert.That(MeshDatabaseReplay.IsRead(DatabaseRequestTypes.UpdateAccessToken), Is.False);
            Assert.That(MeshDatabaseReplay.IsReadName("CreateCharacter"), Is.False);
            Assert.That(MeshDatabaseReplay.IsReadName(null), Is.False);
        }

        [Test]
        public void TheRequestHeaderIsReadBackAsTheKitWroteIt()
        {
            NetDataWriter writer = Request(DatabaseRequestTypes.GetCash, 70000);
            Assert.That(MeshDatabaseReplay.TryReadRequestHeader(writer.Data, writer.Length, RequestMessageType, out ushort type, out uint id), Is.True);
            Assert.That(type, Is.EqualTo(DatabaseRequestTypes.GetCash));
            Assert.That(id, Is.EqualTo(70000u));
            Assert.That(MeshDatabaseReplay.TryReadRequestHeader(writer.Data, writer.Length, 2, out _, out _), Is.False, "another message type is not a request");
            Assert.That(MeshDatabaseReplay.TryReadRequestHeader(new byte[0], 0, RequestMessageType, out _, out _), Is.False);
        }

        [Test]
        public void TheBookKeepsReadsUntilAnswered()
        {
            MeshDatabaseReplayBook book = new MeshDatabaseReplayBook();
            NetDataWriter read = Request(DatabaseRequestTypes.GetUserLevel, 5, "user");
            book.Record(0, DeliveryMethod.ReliableUnordered, read, RequestMessageType, true);
            book.Record(0, DeliveryMethod.ReliableUnordered, Request(DatabaseRequestTypes.ChangeCash, 6), RequestMessageType, true);
            Assert.That(book.Count, Is.EqualTo(1), "a write that went out is not kept");
            Assert.That(book.Undelivered(), Is.Empty);
            Assert.That(book.TryGet(5, out MeshDatabaseReplayBook.Packet packet), Is.True);
            Assert.That(packet.bytes, Is.EqualTo(read.CopyData()), "sent again byte for byte, under its own id");
            Assert.That(packet.deliveryMethod, Is.EqualTo(DeliveryMethod.ReliableUnordered));
            book.Prune(id => id != 5);
            Assert.That(book.Count, Is.EqualTo(0), "answered: forgotten");
        }

        [Test]
        public void ARequestTheTransportRefusedIsKeptWhateverItIsUntilItGoesOut()
        {
            MeshDatabaseReplayBook book = new MeshDatabaseReplayBook();
            NetDataWriter save = Request(DatabaseRequestTypes.UpdateCharacter, 9, "row");
            book.Record(0, DeliveryMethod.ReliableUnordered, save, RequestMessageType, false);
            Assert.That(book.Undelivered(), Is.EqualTo(new[] { 9u }), "it never left: its first delivery cannot be its second");
            Assert.That(book.TryGet(9, out MeshDatabaseReplayBook.Packet packet), Is.True);
            Assert.That(packet.delivered, Is.False);
            book.Record(0, DeliveryMethod.ReliableUnordered, save, RequestMessageType, true);
            Assert.That(book.Count, Is.EqualTo(0), "now it went out: a write is never sent twice");
            NetDataWriter read = Request(DatabaseRequestTypes.GetCharacter, 10);
            book.Record(0, DeliveryMethod.ReliableUnordered, read, RequestMessageType, false);
            book.Record(0, DeliveryMethod.ReliableUnordered, read, RequestMessageType, true);
            Assert.That(book.Undelivered(), Is.Empty);
            Assert.That(book.Count, Is.EqualTo(1), "a read that went out stays kept until answered");
        }

        [Test]
        public void TheReconnectKeepsTheKeptReadsAndTimesOutTheRest()
        {
            MeshDatabaseReplayBook book = new MeshDatabaseReplayBook();
            book.Record(0, DeliveryMethod.ReliableUnordered, Request(DatabaseRequestTypes.ValidateAccessToken, 2), RequestMessageType, true);
            var handler = new LiteNetLibResponseHandler<EmptyMessage, EmptyMessage>((data, code, response) => { });
            var waiting = new List<LiteNetLibRequestCallback>
            {
                new LiteNetLibRequestCallback(1, null, handler, null),   // a save: no packet kept
                new LiteNetLibRequestCallback(2, null, handler, null),   // a read
                new LiteNetLibRequestCallback(3, null, handler, null),   // a read sent before the wrap: no packet
            };
            List<LiteNetLibRequestCallback> kept = MeshMapNetworkManager.SplitResendable(waiting, book);
            Assert.That(kept.ConvertAll(c => c.RequestId), Is.EqualTo(new[] { 2u }));
            Assert.That(waiting.ConvertAll(c => c.RequestId), Is.EqualTo(new[] { 1u, 3u }));
            Assert.That(MeshMapNetworkManager.SplitResendable(new List<LiteNetLibRequestCallback>(), null), Is.Empty);
        }
    }
}
