using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteNetLibManager;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>DESIGN.md 0.87: a database reconnect answers the requests it would strand, and an unanswered enter-game is a refusal.</summary>
    public class MeshDatabaseReconnectTests
    {
        private static LiteNetLibRequestCallback Callback(uint id, List<string> heard)
        {
            var handler = new LiteNetLibResponseHandler<EmptyMessage, EmptyMessage>((data, code, response) => heard.Add($"{data.RequestId}:{code}:{(data.Reader == null ? "no reader" : "reader")}"));
            return new LiteNetLibRequestCallback(id, null, handler, null);
        }

        [Test]
        public void TheKitStillKeepsItsRequestTableWhereTheReconnectLooks()
        {
            System.Reflection.FieldInfo field = typeof(TransportHandler).GetField("_requestCallbacks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "TransportHandler._requestCallbacks is gone: a reconnect falls back to the 35 s drain");
            Assert.That(field.FieldType, Is.EqualTo(typeof(ConcurrentDictionary<uint, LiteNetLibRequestCallback>)));
            Assert.That(MeshMapNetworkManager.RequestCallbacksOf(null), Is.Null);
        }

        [Test]
        public void TakenRequestsLeaveTheTableAndAreEachAnsweredOnceWithTheKitsTimeout()
        {
            var heard = new List<string>();
            var table = new ConcurrentDictionary<uint, LiteNetLibRequestCallback>();
            table[7] = Callback(7, heard);
            table[9] = Callback(9, heard);
            List<LiteNetLibRequestCallback> taken = MeshMapNetworkManager.TakeRequestCallbacks(table);
            Assert.That(table, Is.Empty, "the kit's own timer finds nothing left to answer twice");
            Assert.That(taken.Count, Is.EqualTo(2));
            Assert.That(heard, Is.Empty, "nothing is answered before the reconnect");
            Assert.That(MeshMapNetworkManager.ExpireRequestCallbacks(taken, out string failure), Is.EqualTo(0));
            Assert.That(failure, Is.Null);
            heard.Sort();
            Assert.That(heard, Is.EqualTo(new[] { "7:Timeout:no reader", "9:Timeout:no reader" }));
        }

        [Test]
        public void OneThrowingHandlerDoesNotStrandTheOthers()
        {
            var heard = new List<string>();
            var throwing = new LiteNetLibResponseHandler<EmptyMessage, EmptyMessage>((data, code, response) => throw new System.InvalidOperationException("boom"));
            var callbacks = new List<LiteNetLibRequestCallback> { new LiteNetLibRequestCallback(1, null, throwing, null), Callback(2, heard) };
            Assert.That(MeshMapNetworkManager.ExpireRequestCallbacks(callbacks, out string failure), Is.EqualTo(1));
            Assert.That(failure, Is.EqualTo("boom"));
            Assert.That(heard, Is.EqualTo(new[] { "2:Timeout:no reader" }));
        }

        [Test]
        public void NoTableTakesNothing()
        {
            Assert.That(MeshMapNetworkManager.TakeRequestCallbacks(null), Is.Empty);
            Assert.That(MeshMapNetworkManager.ExpireRequestCallbacks(new List<LiteNetLibRequestCallback>(), out _), Is.EqualTo(0));
        }

        [Test]
        public void AnUnansweredEnterGameIsReadAsTheMatchingRefusal()
        {
            Assert.That(MeshMapNetworkManager.UnansweredEnterGameMessage(AckResponseCode.Timeout), Is.EqualTo(UITextKeys.UI_ERROR_REQUEST_TIMEOUT));
            Assert.That(MeshMapNetworkManager.UnansweredEnterGameMessage(AckResponseCode.Unimplemented), Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
        }

        [Test]
        public void EnterGameRetryLine_IsOneParseableLine()
        {
            Assert.That(ServerMeshAutoClient.EnterGameRetryLine(AckResponseCode.Timeout, "127.0.0.1", 8011, 1, 2f),
                Is.EqualTo("Enter-game refused (Timeout) by 127.0.0.1:8011, attempt 1; selecting again in 2s"));
            Assert.That(ServerMeshAutoClient.EnterGameRetryLine(AckResponseCode.Error, null, 0, 0, 0.5f),
                Is.EqualTo("Enter-game refused (Error) by ?:0, attempt 1; selecting again in 0.5s"));
        }
    }
}
