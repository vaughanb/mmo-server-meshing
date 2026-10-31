// The database server and its SQLite database compile for standalone server builds only.
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A never-started DatabaseNetworkManager (the database server) over a
    /// <see cref="TempSqliteDatabase"/> and a LocalDatabaseCache, the cache a server uses by
    /// default. <see cref="Request{TRequest,TResponse}"/> calls one of its request handlers the
    /// way a network request would and returns the replies; with SQLite and the local cache they
    /// finish at once. The deferred character save scheduler is off until
    /// <see cref="UseSaveScheduler"/> installs one, which is never started: a test writes the
    /// waiting saves itself (FlushCharacterSavesAsync, or the scheduler's own methods).
    /// </summary>
    public sealed class TestDatabaseServer
    {
        public TempSqliteDatabase Db { get; }
        public DatabaseNetworkManager Manager { get; }
        public LocalDatabaseCache Cache { get; }

        public static TestDatabaseServer Create(TestObjects objects)
        {
            return new TestDatabaseServer(objects);
        }

        private TestDatabaseServer(TestObjects objects)
        {
            Db = TempSqliteDatabase.Create(objects);
            // Awake and Start never run in edit mode, so no transport or socket is created.
            Manager = objects.NewGameObject(nameof(TestDatabaseServer)).AddComponent<DatabaseNetworkManager>();
            Cache = Manager.gameObject.AddComponent<LocalDatabaseCache>();
            Manager.Database = Db.Database;
            Manager.DatabaseCache = Cache;
        }

        /// <summary>Installs a scheduler as OnStartServer would, without starting its loop, and returns it.</summary>
        public DatabaseCharacterSaveScheduler UseSaveScheduler(int laneCount = 1, int maxSavesPerTick = 8, float minSaveIntervalSeconds = 20f, float retryDelaySeconds = 5f)
        {
            DatabaseCharacterSaveScheduler scheduler = new DatabaseCharacterSaveScheduler(Manager, laneCount, 500, maxSavesPerTick, minSaveIntervalSeconds, retryDelaySeconds);
            TestReflection.SetField(Manager, "_characterSaveScheduler", scheduler);
            return scheduler;
        }

        public struct Reply<T>
        {
            public AckResponseCode Code;
            public T Response;
        }

        /// <summary>Calls a request handler (e.g. "GetCharacter") and returns its only reply.</summary>
        public Reply<TResponse> Request<TRequest, TResponse>(string handler, TRequest data)
            where TRequest : struct, INetSerializable
            where TResponse : INetSerializable
        {
            List<Reply<TResponse>> replies = new List<Reply<TResponse>>();
            RequestProceedResultDelegate<TResponse> result = (code, response, extra) => replies.Add(new Reply<TResponse>() { Code = code, Response = response });
            RequestHandlerData handlerData = new RequestHandlerData(0, 0, null, 1, new NetDataReader(new byte[0]));
            DbRequestMessage<TRequest> request = new DbRequestMessage<TRequest>() { RequestTimeUtc = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Data = data };
            TestReflection.Invoke(Manager, handler, handlerData, request, result);
            Assert.That(replies, Has.Count.EqualTo(1), $"{handler} answers exactly once");
            return replies[0];
        }
    }
}
#endif
