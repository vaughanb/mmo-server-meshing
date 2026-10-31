#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The login server's request handlers, called directly on a never-started LoginNetworkManager
    /// with a <see cref="TestDatabaseClient"/>. They decide who is logged in on which connection,
    /// which is what stops two people playing one account, and they must answer every request
    /// (a login that never gets a reply leaves the player stuck on the login screen). Handlers
    /// that need the cluster's answer (a first login asks whether the user is still on a map
    /// server) are not reachable this way.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class LoginNetworkManagerTests : KitTestFixture
    {
        private TestDatabaseClient _db;
        private LoginNetworkManager _login;

        /// <summary>Accepts every class and model, so these tests need no game data.</summary>
        private class AcceptingDataManager : ILoginServerDataManager
        {
            public string GenerateCharacterId() => GenericUtils.GetUniqueId();

            public bool CanCreateCharacter(ref int dataId, ref int entityId, ref int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats, out UITextKeys errorMessage)
            {
                errorMessage = UITextKeys.NONE;
                return true;
            }

            public void SetNewPlayerCharacterData(PlayerCharacterData playerCharacterData, string characterName, int dataId, int entityId, int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats)
            {
                playerCharacterData.CharacterName = characterName;
                playerCharacterData.DataId = dataId;
                playerCharacterData.EntityId = entityId;
                playerCharacterData.FactionId = factionId;
            }

            public string GenerateAccessToken(string userId) => userId + "-token";

            public string GetUserIdFromAccessToken(string accessToken) => accessToken.Replace("-token", string.Empty);
        }

        [SetUp]
        public void SetUp()
        {
            _db = TestDatabaseClient.Install(Objects);
            // Awake and Start never run in edit mode, so no transport or socket is created.
            _login = Objects.NewGameObject(nameof(LoginNetworkManagerTests)).AddComponent<LoginNetworkManager>();
            _login.DataManager = new AcceptingDataManager();
            // Kicks are sent through the cluster client; unconnected, it drops them.
            TestReflection.SetProperty(_login, nameof(LoginNetworkManager.ClusterClient), new ClusterClient(_login));
        }

        private Dictionary<long, CentralUserPeerInfo> PeersByConnection => TestReflection.GetField<Dictionary<long, CentralUserPeerInfo>>(_login, "_userPeers");
        private Dictionary<string, CentralUserPeerInfo> PeersByUser => TestReflection.GetField<Dictionary<string, CentralUserPeerInfo>>(_login, "_userPeersByUserId");

        private struct Reply<T>
        {
            public AckResponseCode Code;
            public T Response;
        }

        /// <summary>Calls a request handler as connection <paramref name="connectionId"/> and returns its replies.</summary>
        private List<Reply<TResponse>> Request<TRequest, TResponse>(string handler, long connectionId, TRequest request)
            where TResponse : INetSerializable
        {
            List<Reply<TResponse>> replies = new List<Reply<TResponse>>();
            RequestProceedResultDelegate<TResponse> result = (code, response, extra) => replies.Add(new Reply<TResponse>() { Code = code, Response = response });
            RequestHandlerData data = new RequestHandlerData(0, 0, null, connectionId, new NetDataReader(new byte[0]));
            TestReflection.Invoke(_login, handler, data, request, result);
            return replies;
        }

        private static Reply<T> Single<T>(List<Reply<T>> replies)
        {
            Assert.That(replies, Has.Count.EqualTo(1), "the request is answered exactly once");
            return replies[0];
        }

        private List<Reply<ResponseUserLoginMessage>> Login(long connectionId, string username, string password = "secret")
        {
            return Request<RequestUserLoginMessage, ResponseUserLoginMessage>("HandleRequestUserLogin", connectionId, new RequestUserLoginMessage() { username = username, password = password });
        }

        private Reply<ResponseUserRegisterMessage> Register(string username, string password = "secret", string email = "")
        {
            return Single(Request<RequestUserRegisterMessage, ResponseUserRegisterMessage>("HandleRequestUserRegister", 1, new RequestUserRegisterMessage() { username = username, password = password, email = email }));
        }

        private Reply<ResponseCreateCharacterMessage> CreateCharacter(long connectionId, string characterName)
        {
            return Single(Request<RequestCreateCharacterMessage, ResponseCreateCharacterMessage>("HandleRequestCreateCharacter", connectionId, new RequestCreateCharacterMessage() { characterName = characterName }));
        }

        private void TokensPass(bool isPass, long unbanTime = 0)
        {
            _db.On<ValidateAccessTokenReq, ValidateAccessTokenResp>(nameof(IDatabaseClient.ValidateAccessTokenAsync), req => new ValidateAccessTokenResp() { IsPass = isPass });
            _db.On<GetUserUnbanTimeReq, GetUserUnbanTimeResp>(nameof(IDatabaseClient.GetUserUnbanTimeAsync), req => new GetUserUnbanTimeResp() { UnbanTime = unbanTime });
        }

        private Reply<ResponseValidateAccessTokenMessage> ShowToken(long connectionId, string userId)
        {
            return Single(Request<RequestValidateAccessTokenMessage, ResponseValidateAccessTokenMessage>("HandleRequestValidateAccessToken", connectionId, new RequestValidateAccessTokenMessage() { userId = userId, accessToken = userId + "-token" }));
        }

        /// <summary>Logs a user in on a connection the way a returning client does, with its access token.</summary>
        private void SignIn(long connectionId, string userId)
        {
            TokensPass(true);
            Assert.That(ShowToken(connectionId, userId).Code, Is.EqualTo(AckResponseCode.Success));
        }

        private void PasswordsBelongTo(string userId)
        {
            _db.On<ValidateUserLoginReq, ValidateUserLoginResp>(nameof(IDatabaseClient.ValidateUserLoginAsync), req => new ValidateUserLoginResp() { UserId = userId });
        }

        // Login

        [Test]
        public void WithDefaultLoginDisabledLoginAndRegistrationAreRefused()
        {
            _login.disableDefaultLogin = true;

            Assert.That(Single(Login(1, "alice")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
            Assert.That(Register("alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void AWrongPasswordLogsNoOneIn()
        {
            PasswordsBelongTo(string.Empty);

            Reply<ResponseUserLoginMessage> reply = Single(Login(1, "alice", "wrong"));

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(reply.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(reply.Response.accessToken, Is.Null.Or.Empty);
            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.UpdateAccessTokenAsync)), Is.False);
        }

        [Test]
        public void ALoginToAnAccountThatIsAlreadyLoggedInIsRefusedAndEndsTheOtherSession()
        {
            SignIn(1, "alice");
            PasswordsBelongTo("alice");

            Reply<ResponseUserLoginMessage> reply = Single(Login(2, "alice"));

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(reply.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_ALREADY_LOGGED_IN));
            Assert.That(PeersByConnection.ContainsKey(1), Is.False, "the first session no longer acts as alice");
            Assert.That(PeersByConnection.ContainsKey(2), Is.False, "the second is not let in either");
            Assert.That(PeersByUser.ContainsKey("alice"), Is.False);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.UpdateAccessTokenAsync)), Is.False, "no new token is issued");
        }

        [Test]
        public void LoginsBeyondTheLimitWaitAndAreAnsweredInArrivalOrder()
        {
            List<string> asked = new List<string>();
            _db.On<ValidateUserLoginReq, ValidateUserLoginResp>(nameof(IDatabaseClient.ValidateUserLoginAsync), req =>
            {
                asked.Add(req.Username);
                return new ValidateUserLoginResp() { UserId = string.Empty };
            });
            _login.MaxConcurrentRequest = 1;
            // One login is already being checked.
            TestReflection.SetField(_login, "activeLogins", 1);

            List<Reply<ResponseUserLoginMessage>> alice = Login(1, "alice");
            List<Reply<ResponseUserLoginMessage>> bob = Login(2, "bob");

            Assert.That(alice, Is.Empty, "alice waits");
            Assert.That(bob, Is.Empty, "bob waits");
            Assert.That(asked, Is.Empty);

            // The running login finishes.
            TestReflection.SetField(_login, "activeLogins", 0);
            TestReflection.Invoke(_login, "TryDequeue");

            Assert.That(asked, Is.EqualTo(new[] { "alice", "bob" }));
            Assert.That(Single(alice).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(Single(bob).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(TestReflection.GetField<int>(_login, "activeLogins"), Is.Zero, "every finished login is counted out");
        }

        // Access tokens and sessions

        [Test]
        public void AnAccessTokenShownOnANewConnectionMovesTheSessionThere()
        {
            SignIn(1, "alice");
            SignIn(2, "alice");

            Assert.That(PeersByConnection.ContainsKey(1), Is.False, "the old connection no longer acts as alice");
            Assert.That(PeersByConnection[2].userId, Is.EqualTo("alice"));
            Assert.That(PeersByUser["alice"].connectionId, Is.EqualTo(2));
        }

        [Test]
        public void AnInvalidTokenOrABannedAccountIsNotLoggedIn()
        {
            TokensPass(false);
            Assert.That(ShowToken(1, "alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USER_TOKEN));

            TokensPass(true, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
            Assert.That(ShowToken(1, "alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USER_BANNED));

            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(PeersByUser, Is.Empty);
        }

        [Test]
        public void DisconnectingOrLoggingOutEndsTheSession()
        {
            _db.Succeed(nameof(IDatabaseClient.UpdateAccessTokenAsync));
            SignIn(1, "alice");
            SignIn(2, "bob");

            _login.OnPeerDisconnected(1, DisconnectReason.RemoteConnectionClose, SocketError.Success);
            Reply<EmptyMessage> logout = Single(Request<EmptyMessage, EmptyMessage>("HandleRequestUserLogout", 2, EmptyMessage.Value));

            Assert.That(logout.Code, Is.EqualTo(AckResponseCode.Success));
            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(PeersByUser, Is.Empty, "neither account stays 'already logged in'");
            UpdateAccessTokenReq cleared = _db.LastRequest<UpdateAccessTokenReq>(nameof(IDatabaseClient.UpdateAccessTokenAsync));
            Assert.That(cleared.UserId, Is.EqualTo("bob"));
            Assert.That(cleared.AccessToken, Is.Empty, "logging out revokes the token");
        }

        // Registration

        [Test]
        public void AnInvalidUsernameOrEmailIsRefusedBeforeTheDatabaseIsAsked()
        {
            _login.requireEmail = true;

            Assert.That(Register("alice smith", email: "a@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME));
            Assert.That(Register("alice", email: "not-an-email").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_EMAIL));
            Assert.That(Register("alice", email: "").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_EMAIL));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void ATakenUsernameOrEmailIsRefusedAndNothingIsCreated()
        {
            _login.requireEmail = true;
            _db.On<FindEmailReq, FindEmailResp>(nameof(IDatabaseClient.FindEmailAsync), req => new FindEmailResp() { FoundAmount = req.Email == "taken@b.com" ? 1 : 0 });
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = req.Username == "alice" ? 1 : 0 });

            Assert.That(Register("bob", email: "taken@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_EMAIL_ALREADY_IN_USE));
            Assert.That(Register("alice", email: "free@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_EXISTED));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateUserLoginAsync)), Is.False);
        }

        [Test]
        public void UsernamesAndPasswordsOutsideTheLengthLimitsAreRefused()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });

            Assert.That(Register(new string('a', _login.minUsernameLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_TOO_SHORT));
            Assert.That(Register(new string('a', _login.maxUsernameLength + 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_TOO_LONG));
            Assert.That(Register("alice", new string('p', _login.minPasswordLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_PASSWORD_TOO_SHORT));
            Assert.That(Register("alice", "  " + new string('p', _login.minPasswordLength - 1) + "  ").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_PASSWORD_TOO_SHORT), "spaces do not count");
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateUserLoginAsync)), Is.False);
        }

        [Test]
        public void AGoodRegistrationCreatesTheTrimmedLogin()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });
            _db.Succeed(nameof(IDatabaseClient.CreateUserLoginAsync));

            Reply<ResponseUserRegisterMessage> reply = Register(" alice ", " secret ", " a@b.com ");

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Success));
            CreateUserLoginReq created = _db.LastRequest<CreateUserLoginReq>(nameof(IDatabaseClient.CreateUserLoginAsync));
            Assert.That(created.Username, Is.EqualTo("alice"));
            Assert.That(created.Password, Is.EqualTo("secret"));
            Assert.That(created.Email, Is.EqualTo("a@b.com"));
        }

        // Characters

        [Test]
        public void ACharacterNameThatBreaksTheRulesIsRefusedBeforeTheDatabaseIsAsked()
        {
            SignIn(1, "alice");

            Assert.That(CreateCharacter(1, "Ro wan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_CHARACTER_NAME));
            Assert.That(CreateCharacter(1, new string('a', _login.minCharacterNameLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_TOO_SHORT));
            Assert.That(CreateCharacter(1, new string('a', _login.maxCharacterNameLength + 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_TOO_LONG));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.FindCharacterNameAsync)), Is.False);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateCharacterAsync)), Is.False);
        }

        [Test]
        public void ATakenNameOrAConnectionThatIsNotLoggedInCreatesNoCharacter()
        {
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = req.CharacterName == "Taken" ? 1 : 0 });
            SignIn(1, "alice");

            Assert.That(CreateCharacter(1, "Taken").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_EXISTED));
            Assert.That(CreateCharacter(2, "Rowan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateCharacterAsync)), Is.False);
        }

        [Test]
        public void ANewCharacterBelongsToTheLoggedInAccount()
        {
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = 0 });
            _db.On<CreateCharacterReq, CharacterResp>(nameof(IDatabaseClient.CreateCharacterAsync), req => new CharacterResp() { CharacterData = req.CharacterData });
            SignIn(1, "alice");
            SignIn(2, "bob");

            Assert.That(CreateCharacter(2, "  Rowan  ").Code, Is.EqualTo(AckResponseCode.Success));

            CreateCharacterReq created = _db.LastRequest<CreateCharacterReq>(nameof(IDatabaseClient.CreateCharacterAsync));
            Assert.That(created.UserId, Is.EqualTo("bob"));
            Assert.That(created.CharacterData.CharacterName, Is.EqualTo("Rowan"));
            Assert.That(created.CharacterData.Id, Is.Not.Empty);
        }

        [Test]
        public void ListingDeletingOrSelectingCharactersNeedsALogin()
        {
            Assert.That(Single(Request<EmptyMessage, ResponseCharactersMessage>("HandleRequestCharacters", 1, EmptyMessage.Value)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(Single(Request<RequestDeleteCharacterMessage, ResponseDeleteCharacterMessage>("HandleRequestDeleteCharacter", 1, new RequestDeleteCharacterMessage() { characterId = "c1" })).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(Single(Request<RequestSelectCharacterMessage, ResponseSelectCharacterMessage>("HandleRequestSelectCharacter", 1, new RequestSelectCharacterMessage() { characterId = "c1" })).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void AnAccountListsAndDeletesOnlyItsOwnCharacters()
        {
            _db.On<GetCharactersReq, CharactersResp>(nameof(IDatabaseClient.GetCharactersAsync), req => new CharactersResp() { List = new List<PlayerCharacterData>() });
            _db.Succeed(nameof(IDatabaseClient.DeleteCharacterAsync));
            SignIn(1, "alice");
            SignIn(2, "bob");

            Single(Request<EmptyMessage, ResponseCharactersMessage>("HandleRequestCharacters", 2, EmptyMessage.Value));
            Reply<ResponseDeleteCharacterMessage> deleted = Single(Request<RequestDeleteCharacterMessage, ResponseDeleteCharacterMessage>("HandleRequestDeleteCharacter", 2, new RequestDeleteCharacterMessage() { characterId = "alices-character" }));

            Assert.That(_db.LastRequest<GetCharactersReq>(nameof(IDatabaseClient.GetCharactersAsync)).UserId, Is.EqualTo("bob"));
            Assert.That(deleted.Code, Is.EqualTo(AckResponseCode.Success));
            DeleteCharacterReq delete = _db.LastRequest<DeleteCharacterReq>(nameof(IDatabaseClient.DeleteCharacterAsync));
            Assert.That(delete.UserId, Is.EqualTo("bob"), "the database is asked for bob's character, so alice's is not deleted");
            Assert.That(delete.CharacterId, Is.EqualTo("alices-character"));
        }
    }
}
#endif
