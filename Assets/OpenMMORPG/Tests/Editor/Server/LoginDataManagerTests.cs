using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The login server's default data manager: which new characters it allows and the access
    /// token it hands out at login. A character with a class or model the game does not have
    /// cannot be loaded later, so it must be refused; a locked faction must not be pickable by
    /// sending its id. The access token is what a player shows the login and map servers to
    /// prove who they are, so it must not be something another player can work out.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class LoginDataManagerTests : KitTestFixture
    {
        private DefaultLoginServerDataManager _manager;
        private PlayerCharacter _class;
        private PlayerCharacterEntityMetaData _entity;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _manager = new DefaultLoginServerDataManager();
            _class = Objects.Create<PlayerCharacter>("TestClass");
            GameInstance.PlayerCharacters[_class.DataId] = _class;
            _entity = Objects.Create<PlayerCharacterEntityMetaData>("TestEntity");
            GameInstance.PlayerCharacterEntityMetaDataList[_entity.DataId] = _entity;
        }

        private Faction AddFaction(string name, bool locked)
        {
            Faction faction = Objects.Create<Faction>(name);
            faction.IsLocked = locked;
            GameInstance.Factions[faction.DataId] = faction;
            return faction;
        }

        private bool CanCreate(int dataId, int entityId, ref int factionId, out UITextKeys error)
        {
            return _manager.CanCreateCharacter(ref dataId, ref entityId, ref factionId, null, null, null, out error);
        }

        [Test]
        public void ACharacterOfAnUnknownClassOrModelIsRefused()
        {
            int factionId = 0;

            Assert.That(CanCreate(_class.DataId + 1, _entity.DataId, ref factionId, out UITextKeys classError), Is.False);
            Assert.That(classError, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_CHARACTER_DATA));
            Assert.That(CanCreate(_class.DataId, _entity.DataId + 1, ref factionId, out UITextKeys entityError), Is.False);
            Assert.That(entityError, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_CHARACTER_ENTITY));
            Assert.That(CanCreate(_class.DataId, _entity.DataId, ref factionId, out UITextKeys none), Is.True);
            Assert.That(none, Is.EqualTo(UITextKeys.NONE));
        }

        [Test]
        public void ALockedOrUnknownFactionIsSwappedForAnOpenOne()
        {
            Faction open = AddFaction("Open", false);
            Faction locked = AddFaction("Locked", true);

            int lockedId = locked.DataId;
            Assert.That(CanCreate(_class.DataId, _entity.DataId, ref lockedId, out _), Is.True);
            Assert.That(lockedId, Is.EqualTo(open.DataId));

            int unknownId = 12345;
            Assert.That(CanCreate(_class.DataId, _entity.DataId, ref unknownId, out _), Is.True);
            Assert.That(unknownId, Is.EqualTo(open.DataId));

            int openId = open.DataId;
            Assert.That(CanCreate(_class.DataId, _entity.DataId, ref openId, out _), Is.True);
            Assert.That(openId, Is.EqualTo(open.DataId), "an open faction is kept");
        }

        [Test]
        public void WithNoOpenFactionTheCharacterGetsNone()
        {
            Faction locked = AddFaction("Locked", true);

            int factionId = locked.DataId;
            Assert.That(CanCreate(_class.DataId, _entity.DataId, ref factionId, out _), Is.True);
            Assert.That(factionId, Is.Zero);
        }

        [Test]
        public void AnAccessTokenNamesTheUserItWasMadeFor()
        {
            string userId = GenericUtils.GetUniqueId();

            Assert.That(_manager.GetUserIdFromAccessToken(_manager.GenerateAccessToken(userId)), Is.EqualTo(userId));
        }

        [Test]
        public void EachLoginGetsADifferentAccessToken()
        {
            string userId = GenericUtils.GetUniqueId();

            Assert.That(_manager.GenerateAccessToken(userId), Is.Not.EqualTo(_manager.GenerateAccessToken(userId)));
        }
    }
}
