#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using Mono.Data.Sqlite;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// DoMigration runs on every server start against whatever database is there. On a current
    /// database it must change nothing; on an older one it must add every column the code now
    /// reads, without losing rows. A failed migration surfaces later as a save or load that
    /// silently loses data, far from the cause.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteMigrationTests : KitTestFixture
    {
        /// <summary>The columns DoMigration adds with ALTER TABLE, per table (mail is tested apart).</summary>
        private static readonly Dictionary<string, string[]> AddedColumns = new Dictionary<string, string[]>()
        {
            ["characterhotkey"] = new[] { "relateId" },
            ["characteritem"] = new[] { "equipSlotIndex", "durability", "exp", "lockRemainsDuration", "expireTime", "randomSeed", "ammo", "sockets", "version", "ammoDataId" },
            ["storageitem"] = new[] { "durability", "exp", "lockRemainsDuration", "expireTime", "randomSeed", "ammo", "sockets", "version", "ammoDataId" },
            ["characterquest"] = new[] { "randomTasksIndex", "completeTime", "isTracking", "completedTasks" },
            ["friend"] = new[] { "state" },
            ["userlogin"] = new[] { "gold", "cash", "userLevel", "unbanTime", "isEmailVerified" },
            ["buildings"] = new[] { "remainsLifeTime", "isLocked", "lockPassword", "extraData", "channel", "isSceneObject" },
            ["characters"] = new[]
            {
                "partyId", "guildId", "guildRole", "sharedGuildExp", "entityId", "factionId", "equipWeaponSet", "iconDataId",
                "frameDataId", "backgroundDataId", "titleDataId", "currentChannel", "currentMapName", "currentRotationX",
                "currentRotationY", "currentRotationZ", "currentSafeArea", "reputation", "lastDeadTime", "unmuteTime",
            },
            ["charactersummon"] = new[] { "sourceId" },
            ["guild"] = new[] { "guildMessage2", "gold", "score", "options", "autoAcceptRequests", "rank", "currentMembers", "maxMembers" },
            ["guildrole"] = new[] { "canUseStorage" },
        };

        private TempSqliteDatabase _db;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        private void Migrate()
        {
            TestAsync.Wait(_db.Database.DoMigration());
        }

        private List<string> Tables()
        {
            List<string> tables = new List<string>();
            _db.Database.ExecuteReader(reader =>
            {
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            return tables;
        }

        /// <summary>"name type" for each column, in table order.</summary>
        private List<string> Columns(string table)
        {
            List<string> columns = new List<string>();
            _db.Database.ExecuteReader(reader =>
            {
                while (reader.Read())
                    columns.Add(reader.GetString(1) + " " + reader.GetString(2));
            }, $"PRAGMA table_info({table})");
            return columns;
        }

        private List<string> ColumnNames(string table)
        {
            return Columns(table).Select(c => c.Split(' ')[0]).ToList();
        }

        /// <summary>Rebuilds a table without some columns, keeping its rows: an older schema.</summary>
        private void DropColumns(string table, IEnumerable<string> dropped)
        {
            string kept = string.Join(", ", ColumnNames(table).Except(dropped));
            _db.Database.ExecuteNonQuery($"CREATE TABLE {table}_legacy AS SELECT {kept} FROM {table}");
            _db.Database.ExecuteNonQuery($"DROP TABLE {table}");
            _db.Database.ExecuteNonQuery($"ALTER TABLE {table}_legacy RENAME TO {table}");
        }

        [Test]
        public void MigratingACurrentDatabaseAgainChangesNothing()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer();
            character.Level = 6;
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            Dictionary<string, List<string>> before = Tables().ToDictionary(t => t, Columns);

            Migrate();
            Migrate();

            Assert.That(Tables(), Is.EqualTo(before.Keys.ToList()));
            foreach (KeyValuePair<string, List<string>> table in before)
                Assert.That(Columns(table.Key), Is.EqualTo(table.Value), table.Key);
            Assert.That(TestAsync.Wait(_db.Database.GetCharacter(character.Id)).Level, Is.EqualTo(6));
        }

        [Test]
        public void MigratingAnOlderDatabaseAddsEveryColumnAndKeepsTheRows()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer("Veteran");
            character.Level = 33;
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            foreach (KeyValuePair<string, string[]> table in AddedColumns)
            {
                DropColumns(table.Key, table.Value);
                Assert.That(ColumnNames(table.Key).Intersect(table.Value), Is.Empty, table.Key + " was made old");
            }

            Migrate();

            foreach (KeyValuePair<string, string[]> table in AddedColumns)
                Assert.That(ColumnNames(table.Key), Is.SupersetOf(table.Value), table.Key);
            PlayerCharacterData loaded = TestAsync.Wait(_db.Database.GetCharacter(character.Id));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.CharacterName, Is.EqualTo("Veteran"));
            Assert.That(loaded.Level, Is.EqualTo(33));
            Assert.That(TestAsync.Wait(_db.Database.ValidateUserLogin("tester", "secret")), Is.EqualTo(_userId));
        }

        [Test]
        public void MigratingAnOldMailTableKeepsTheMails()
        {
            _db.Database.ExecuteNonQuery("DROP TABLE mail");
            _db.Database.ExecuteNonQuery(@"CREATE TABLE mail (
              id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              eventId TEXT NULL DEFAULT NULL,
              senderId TEXT NULL DEFAULT NULL,
              senderName TEXT NULL DEFAULT NULL,
              receiverId TEXT NOT NULL,
              title TEXT NOT NULL,
              content TEXT NOT NULL,
              gold REAL NOT NULL DEFAULT 0,
              currencies TEXT NOT NULL,
              items TEXT NOT NULL,
              isRead INTEGER NOT NULL DEFAULT 0,
              readTimestamp TIMESTAMP NULL DEFAULT NULL,
              isDelete INTEGER NOT NULL DEFAULT 0,
              deleteTimestamp TIMESTAMP NULL DEFAULT NULL,
              sentTimestamp TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            )");
            _db.Database.ExecuteNonQuery("INSERT INTO mail (senderName, receiverId, title, content, gold, currencies, items) VALUES ('Old', @receiverId, 'Kept', 'Hi', 10, '', '')",
                new SqliteParameter("@receiverId", _userId));

            Migrate();

            Assert.That(ColumnNames("mail"), Is.SupersetOf(new[] { "cash", "isClaim", "claimTimestamp" }));
            Assert.That(Columns("mail"), Contains.Item("gold INTEGER"));
            Assert.That(TestAsync.Wait(_db.Database.MailList(_userId, false)).Select(m => m.Title), Is.EqualTo(new[] { "Kept" }));
        }

        [Test]
        public void MigratingIntegerStatPointsKeepsTheCharacters()
        {
            _db.Database.ExecuteNonQuery("DROP TABLE characters");
            _db.Database.ExecuteNonQuery(@"CREATE TABLE characters (
              id TEXT NOT NULL PRIMARY KEY,
              userId TEXT NOT NULL,
              dataId INTEGER NOT NULL DEFAULT 0,
              entityId INTEGER NOT NULL DEFAULT 0,
              factionId INTEGER NOT NULL DEFAULT 0,
              characterName TEXT NOT NULL UNIQUE,
              level INTEGER NOT NULL,
              exp INTEGER NOT NULL,
              currentHp INTEGER NOT NULL,
              currentMp INTEGER NOT NULL,
              currentStamina INTEGER NOT NULL,
              currentFood INTEGER NOT NULL,
              currentWater INTEGER NOT NULL,
              equipWeaponSet INTEGER NOT NULL DEFAULT 0,
              statPoint INTEGER NOT NULL DEFAULT 0,
              skillPoint INTEGER NOT NULL DEFAULT 0,
              gold INTEGER NOT NULL,
              partyId INTEGER NOT NULL DEFAULT 0,
              guildId INTEGER NOT NULL DEFAULT 0,
              guildRole INTEGER NOT NULL DEFAULT 0,
              sharedGuildExp INTEGER NOT NULL DEFAULT 0,
              currentMapName TEXT NOT NULL,
              currentPositionX REAL NOT NULL DEFAULT 0,
              currentPositionY REAL NOT NULL DEFAULT 0,
              currentPositionZ REAL NOT NULL DEFAULT 0,
              currentRotationX REAL NOT NULL DEFAULT 0,
              currentRotationY REAL NOT NULL DEFAULT 0,
              currentRotationZ REAL NOT NULL DEFAULT 0,
              respawnMapName TEXT NOT NULL,
              respawnPositionX REAL NOT NULL DEFAULT 0,
              respawnPositionY REAL NOT NULL DEFAULT 0,
              respawnPositionZ REAL NOT NULL DEFAULT 0,
              lastDeadTime INTEGER NOT NULL DEFAULT 0,
              unmuteTime INTEGER NOT NULL DEFAULT 0,
              createAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
              updateAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            )");
            _db.Database.ExecuteNonQuery(@"INSERT INTO characters (id, userId, characterName, level, exp, currentHp, currentMp, currentStamina, currentFood, currentWater, statPoint, skillPoint, gold, currentMapName, respawnMapName)
                VALUES ('old-char', @userId, 'Ancient', 40, 0, 1, 1, 1, 1, 1, 3, 2, 0, 'TestMap', 'TestMap')",
                new SqliteParameter("@userId", _userId));

            Migrate();

            Assert.That(Columns("characters"), Contains.Item("statPoint REAL"));
            PlayerCharacterData loaded = TestAsync.Wait(_db.Database.GetCharacter("old-char"));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.Level, Is.EqualTo(40));
            Assert.That(loaded.StatPoint, Is.EqualTo(3f));
        }
    }
}
#endif
