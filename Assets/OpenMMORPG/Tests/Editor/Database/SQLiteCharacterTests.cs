#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    [Category(TestCategories.Harnessed)]
    public class SQLiteCharacterTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        private PlayerCharacterData Load(string id)
        {
            return TestAsync.Wait(_db.Database.GetCharacter(id));
        }

        [Test]
        public void ACreatedCharacterReadsBackUnchanged()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer("Rowan");
            character.Level = 7;
            character.Exp = 123;
            character.Gold = 45;
            character.CurrentPosition = new Vec3(1f, 2f, 3f);

            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            PlayerCharacterData loaded = Load(character.Id);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.UserId, Is.EqualTo(_userId));
            Assert.That(loaded.CharacterName, Is.EqualTo("Rowan"));
            Assert.That(loaded.Level, Is.EqualTo(7));
            Assert.That(loaded.Exp, Is.EqualTo(123));
            Assert.That(loaded.Gold, Is.EqualTo(45));
            Assert.That(loaded.CurrentPosition.x, Is.EqualTo(1f));
            Assert.That(loaded.CurrentPosition.y, Is.EqualTo(2f));
            Assert.That(loaded.CurrentPosition.z, Is.EqualTo(3f));
        }

        [Test]
        public void ACharacterUpdateIsSaved()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer();
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));

            character.Level = 8;
            character.Exp = 0;
            TestAsync.Wait(_db.Database.UpdateCharacter(TransactionUpdateCharacterState.Character, character, null, false));

            Assert.That(Load(character.Id).Level, Is.EqualTo(8));
        }
    }
}
#endif
