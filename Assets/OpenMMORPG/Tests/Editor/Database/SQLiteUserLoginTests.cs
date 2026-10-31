#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using Mono.Data.Sqlite;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Logins and the account-wide values on them. A wrong password must never return a user id,
    /// gold and cash change by the amount given (they are spent through ChangeGold/ChangeCash with
    /// negative amounts) and bans and access tokens apply to the right account.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteUserLoginTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser("tester", "secret");
        }

        [Test]
        public void TheRightPasswordReturnsTheUserId()
        {
            Assert.That(_userId, Is.Not.Empty);
            Assert.That(TestAsync.Wait(_db.Database.ValidateUserLogin("tester", "secret")), Is.EqualTo(_userId));
        }

        [Test]
        public void AWrongPasswordOrUnknownUserReturnsNoUserId()
        {
            Assert.That(TestAsync.Wait(_db.Database.ValidateUserLogin("tester", "Secret")), Is.Empty);
            Assert.That(TestAsync.Wait(_db.Database.ValidateUserLogin("tester", "")), Is.Empty);
            Assert.That(TestAsync.Wait(_db.Database.ValidateUserLogin("nobody", "secret")), Is.Empty);
        }

        [Test]
        public void ThePasswordIsNotStoredInPlainText()
        {
            object stored = _db.Database.ExecuteScalar("SELECT password FROM userlogin WHERE id=@id", new SqliteParameter("@id", _userId));

            Assert.That(stored, Is.Not.EqualTo("secret"));
        }

        [Test]
        public void UsernamesAndEmailsAreFound()
        {
            Assert.That(TestAsync.Wait(_db.Database.FindUsername("tester")), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.FindUsername("nobody")), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.FindEmail("tester@example.com")), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.FindEmail("nobody@example.com")), Is.Zero);
        }

        [Test]
        public void GoldAndCashChangeByTheAmountGivenOnThatAccountOnly()
        {
            string otherUserId = _db.CreateUser("other", "secret2");

            Assert.That(TestAsync.Wait(_db.Database.ChangeGold(_userId, 50)), Is.EqualTo(50));
            Assert.That(TestAsync.Wait(_db.Database.ChangeGold(_userId, -20)), Is.EqualTo(30));
            Assert.That(TestAsync.Wait(_db.Database.ChangeCash(_userId, 7)), Is.EqualTo(7));
            Assert.That(TestAsync.Wait(_db.Database.ChangeCash(_userId, -2)), Is.EqualTo(5));

            Assert.That(TestAsync.Wait(_db.Database.GetGold(_userId)), Is.EqualTo(30));
            Assert.That(TestAsync.Wait(_db.Database.GetCash(_userId)), Is.EqualTo(5));
            Assert.That(TestAsync.Wait(_db.Database.GetGold(otherUserId)), Is.Zero);
            Assert.That(TestAsync.Wait(_db.Database.GetCash(otherUserId)), Is.Zero);
        }

        [Test]
        public void AnAccessTokenValidatesOnlyForItsUser()
        {
            string otherUserId = _db.CreateUser("other", "secret2");

            TestAsync.Wait(_db.Database.UpdateAccessToken(_userId, "token-1"));

            Assert.That(TestAsync.Wait(_db.Database.ValidateAccessToken(_userId, "token-1")), Is.True);
            Assert.That(TestAsync.Wait(_db.Database.ValidateAccessToken(_userId, "token-2")), Is.False);
            Assert.That(TestAsync.Wait(_db.Database.ValidateAccessToken(otherUserId, "token-1")), Is.False);
        }

        [Test]
        public void ABanByCharacterNameLandsOnThatCharactersAccount()
        {
            string otherUserId = _db.CreateUser("other", "secret2");
            PlayerCharacterData character = TestCharacters.NewPlayer("Cheater");
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));

            TestAsync.Wait(_db.Database.SetUserUnbanTimeByCharacterName("Cheater", 1900000000L));

            Assert.That(TestAsync.Wait(_db.Database.GetUserUnbanTime(_userId)), Is.EqualTo(1900000000L));
            Assert.That(TestAsync.Wait(_db.Database.GetUserUnbanTime(otherUserId)), Is.Zero);
        }

        [Test]
        public void AVerifiedEmailValidates()
        {
            Assert.That(TestAsync.Wait(_db.Database.ValidateEmailVerification(_userId)), Is.False);

            _db.Database.ExecuteNonQuery("UPDATE userlogin SET isEmailVerified=1 WHERE id=@id", new SqliteParameter("@id", _userId));

            Assert.That(TestAsync.Wait(_db.Database.ValidateEmailVerification(_userId)), Is.True);
        }
    }
}
#endif
