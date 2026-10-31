// The kit compiles SQLite only for standalone targets; match its condition.
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System;
using System.IO;
using LiteNetLibManager;
using Mono.Data.Sqlite;
using MultiplayerARPG.MMO;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A SQLiteDatabase on a throwaway file in the system temp folder. Initialize() is never
    /// called: it reads and writes ./Config/sqliteConfig.json and opens the project's real
    /// database. This points the database at the temp file, opens the connection and runs the
    /// same Init and DoMigration a server does.
    ///
    /// The database catches its own exceptions and logs them as errors, so a write that silently
    /// failed still fails the test through the Test Runner's unexpected-error check.
    /// </summary>
    public sealed class TempSqliteDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly bool _ownsLogger;

        public SQLiteDatabase Database { get; }
        public string FilePath { get; }

        public static TempSqliteDatabase Create(TestObjects objects)
        {
            return objects.Own(new TempSqliteDatabase(objects));
        }

        private TempSqliteDatabase(TestObjects objects)
        {
            // Null outside play mode; the database logs through it.
            if (LogManager.LoggerManager == null)
            {
                LogManager.Initialize();
                _ownsLogger = true;
            }

            string folder = Path.Combine(Path.GetTempPath(), "OpenMMORPG.Tests");
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");

            Database = objects.NewGameObject(nameof(TempSqliteDatabase)).AddComponent<SQLiteDatabase>();
            // Awake installs the default user-login manager (password hashing).
            TestReflection.Invoke(Database, "Awake");
            TestReflection.SetField(Database, "dbPath", FilePath);
            TestReflection.SetField(Database, "editorDbPath", FilePath);

            _connection = Database.NewConnection();
            _connection.Open();
            TestReflection.SetField(Database, "_connection", _connection);
            TestReflection.Invoke(Database, "Init");
            TestAsync.Wait(Database.DoMigration());
        }

        /// <summary>Creates a login and returns its user id.</summary>
        public string CreateUser(string username = "tester", string password = "secret")
        {
            TestAsync.Wait(Database.CreateUserLogin(username, password, username + "@example.com"));
            return TestAsync.Wait(Database.ValidateUserLogin(username, password));
        }

        public void Dispose()
        {
            _connection.Close();
            _connection.Dispose();
            // Mono's provider keeps the file open until the pool is cleared and the commands the
            // database never disposed are finalized.
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // Still locked; it is in the temp folder and harmless.
            }
            if (_ownsLogger)
                LogManager.LoggerManager = null;
        }
    }
}
#endif
