// Server Meshing add-on — automated test client, Slice 2d additions (slices/slice2d_design.md decision 14).
//
// The login flow (login server, login or register, channel, character, select, enter the map, respawn a dead
// character) lives here as step coroutines; Run's first login composes them exactly as the inline flow did, with the
// same log lines. Once the first login has entered the map, the bot watches its map session: a disconnect the kit's
// handling sees (the non-migration branch of MeshMapNetworkManager.OnClientDisconnected, or a migration that ended in
// FailMigration; both reach ClientGenericActions.onClientDisconnected, a migration's own hops never do) logs
// `Session lost (<why>); re-login <n>` and re-logs in: login, channel, select (retried while the cluster still lists the
// user on the dead cell), `Re-login routed to <ip:port>`, the map. The mode keeps running meanwhile (a stand window
// keeps counting); stand, harvest-after-migration and walk RESULTs gain ` relogins=<n>`.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        [Header("Re-login (Slice 2d)")]
        [Tooltip("How long one re-login may take (login, channel, select and entering the map, with their retries).")]
        public float reloginBudgetSeconds = 120f;
        [Tooltip("Seconds between re-login attempts (the cluster lists the user on the dead cell for about 5 s).")]
        public float reloginRetrySeconds = 2f;
        [Tooltip("Seconds a re-login waits for the character to spawn after a select before it tries again.")]
        public float reloginEnterSeconds = 30f;

        /// <summary>Relogins counts the sessions this bot has re-logged in after (each 'Session lost' line).</summary>
        public int Relogins { get; private set; }

        private PlayerCharacterData _character;   // the character the first login selected (a re-login selects it again)
        private bool _relogging;                  // a re-login runs: its own failed attempts are not new session losses
        private string _sessionLost;              // why the map session was lost, set by the disconnect handler
        private bool _migratingSeen;              // a migration was in progress at the watcher's last frame

        /// <summary>What one login step left: ok, or the error (the first login fails the run with it).</summary>
        private sealed class LoginStep
        {
            public bool ok;
            public string error;

            public void Failed(string why)
            {
                ok = false;
                error = why;
            }

            public void Reset()
            {
                ok = true;
                error = null;
            }
        }

        /// <summary>SessionLostLine is the line a bot logs when its map session ends outside a migration: the n-th re-login.</summary>
        public static string SessionLostLine(string why, int n)
        {
            return string.Format(CultureInfo.InvariantCulture, "Session lost ({0}); re-login {1}", OneParen(why), System.Math.Max(0, n));
        }

        /// <summary>ReloginRoutedLine is the line a re-login logs once the login server routed it to a map server.</summary>
        public static string ReloginRoutedLine(string address, int port)
        {
            return string.Format(CultureInfo.InvariantCulture, "Re-login routed to {0}:{1}", string.IsNullOrEmpty(address) ? "?" : address, port);
        }

        /// <summary>ReloginSuffix is what a RESULT carrying the migration fields gains, with a leading space.</summary>
        public static string ReloginSuffix(int relogins)
        {
            return string.Format(CultureInfo.InvariantCulture, " relogins={0}", System.Math.Max(0, relogins));
        }

        /// <summary>
        /// SessionLostWhy names a lost session: the disconnect reason, the kit's message when it carries one (a kick), and
        /// "migration failed: " in front when a migration was in progress (it ended in FailMigration).
        /// </summary>
        public static string SessionLostWhy(DisconnectReason reason, UITextKeys message, bool migrationFailed)
        {
            string why = reason.ToString() + (message != UITextKeys.NONE ? "/" + message : string.Empty);
            return migrationFailed ? "migration failed: " + why : why;
        }

        /// <summary>OneParen keeps a reason on one line and free of parentheses and braces, so the line parses.</summary>
        private static string OneParen(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "unknown";
            return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('(', '[').Replace(')', ']').Replace('{', '[').Replace('}', ']');
        }

        private void SetPhase(string phase) => Phase = _relogging ? "relogin-" + phase : phase;

        /// <summary>The first login, as Run did it inline: login server, login (register on first run), channel, character, enter.</summary>
        private IEnumerator FirstLogin(MMOClientInstance client, float deadline, LoginStep step)
        {
            yield return ConnectLoginServer(client, deadline, step);
            if (!step.ok) yield break;
            yield return LoginUser(client, deadline, true, step);
            if (!step.ok) yield break;
            yield return SelectChannel(client, deadline, step);
            if (!step.ok) yield break;
            yield return SelectOrCreateCharacter(client, deadline, step);
            if (!step.ok) yield break;
            yield return SelectAndEnter(client, deadline, -1f, step);
        }

        /// <summary>
        /// 0) Connect to the login server (retried every 3 s), after giving in-process servers a moment. Connected means
        /// the transport's peer is (LoginPeerUp), not the kit's IsClientConnected: that goes true as soon as a socket
        /// exists, and a request sent before the server has accepted the peer is dropped without a word while its
        /// callback waits out the kit's 30 s request timeout (DESIGN.md 0.84: the split topology's first-login timeouts).
        /// A socket whose peer is not up when the next attempt is due is stopped and opened again.
        /// </summary>
        private IEnumerator ConnectLoginServer(MMOClientInstance client, float deadline, LoginStep step)
        {
            SetPhase("connect-login");
            yield return new WaitForSecondsRealtime(1f);
            float nextAttempt = 0f;
            float opened = 0f;
            int openedFrame = 0;
            while (!LoginPeerUp(client) && Time.realtimeSinceStartup < deadline)
            {
                if (Time.realtimeSinceStartup >= nextAttempt)
                {
                    nextAttempt = Time.realtimeSinceStartup + 3f;
                    if (client.IsConnectedToCentralServer())
                        client.StopCentralClient();
                    Log($"Connecting to login server {client.LoginNetworkManager.networkAddress}:{client.LoginNetworkManager.networkPort} ...");
                    client.StartCentralClient();
                    opened = Time.realtimeSinceStartup;
                    openedFrame = Time.frameCount;
                }
                yield return null;
            }
            if (!LoginPeerUp(client)) { step.Failed("Could not connect to the login server"); yield break; }
            Log(LoginConnectedLine(Time.frameCount - openedFrame, (Time.realtimeSinceStartup - opened) * 1000f));
            step.ok = true;
        }

        /// <summary>Whether the login server has accepted this client's peer (the transport's state, not the kit's flag).</summary>
        private static bool LoginPeerUp(MMOClientInstance client)
        {
            LoginNetworkManager login = client.LoginNetworkManager;
            return login != null && login.IsClientConnected && login.Client != null && login.Client.Transport != null
                && login.Client.Transport.IsClientStarted;
        }

        /// <summary>LoginConnectedLine is the line a bot logs once its login peer is up: how many frames and ms after the socket opened.</summary>
        public static string LoginConnectedLine(int frames, float milliseconds)
        {
            return string.Format(CultureInfo.InvariantCulture, "Connected to login server (peer up after {0} frame(s), {1:0} ms)",
                System.Math.Max(0, frames), Finite(System.Math.Max(0f, milliseconds)));
        }

        /// <summary>1) Login; on the first login a refusal registers the user and logs in again (a re-login never registers).</summary>
        private IEnumerator LoginUser(MMOClientInstance client, float deadline, bool mayRegister, LoginStep step)
        {
            SetPhase("login");
            bool done = false; AckResponseCode code = AckResponseCode.Unimplemented; UITextKeys msg = UITextKeys.NONE;
            for (int attempt = 1; ; ++attempt)
            {
                done = false;
                client.RequestUserLogin(username, password, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                // A timeout is not "no such user" (registering then fails with USERNAME_EXISTED), so try again before
                // falling back to register. (The split topology's first-login timeouts were a request sent before the
                // peer was up, which ConnectLoginServer now waits for; DESIGN.md 0.84.)
                if (code == AckResponseCode.Success || code != AckResponseCode.Timeout || attempt >= 4) break;
                Log($"Login timed out (attempt {attempt}); retrying");
                yield return new WaitForSecondsRealtime(2f * attempt);
            }
            if (code != AckResponseCode.Success)
            {
                if (!mayRegister) { step.Failed($"Login failed: {code}/{msg}"); yield break; }
                Log($"Login failed ({code}/{msg}); registering '{username}'");
                SetPhase("register");
                done = false;
                client.RequestUserRegister(username, password, RegistrationEmail(), (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { step.Failed($"Register failed: {code}/{msg}"); yield break; }
                SetPhase("login2");
                done = false;
                client.RequestUserLogin(username, password, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { step.Failed($"Login after register failed: {code}/{msg}"); yield break; }
            }
            Log($"Logged in as '{username}' (userId={GameInstance.UserId})");
            step.ok = true;
        }

        /// <summary>2) Channel: the configured one when the login server lists it, else its first.</summary>
        private IEnumerator SelectChannel(MMOClientInstance client, float deadline, LoginStep step)
        {
            SetPhase("channels");
            bool done = false; AckResponseCode code = AckResponseCode.Unimplemented; List<ChannelEntry> channels = null;
            client.RequestChannels((h, c, r) => { code = c; channels = r.channels; done = true; });
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (code != AckResponseCode.Success || channels == null || channels.Count == 0) { step.Failed($"No channels ({code})"); yield break; }
            string chosen = channels.Any(ch => ch.id == channelId) ? channelId : channels[0].id;
            client.SelectedChannelId = chosen;
            Log($"Channels: {string.Join(",", channels.Select(ch => ch.id))} -> selected '{chosen}'");
            step.ok = true;
        }

        /// <summary>3) Character: the first on the expected map, or a new one (create on first run).</summary>
        private IEnumerator SelectOrCreateCharacter(MMOClientInstance client, float deadline, LoginStep step)
        {
            SetPhase("characters");
            bool done = false; AckResponseCode code = AckResponseCode.Unimplemented; UITextKeys msg = UITextKeys.NONE;
            List<PlayerCharacterData> characters = null;
            client.RequestCharacters((h, c, r) => { code = c; characters = r.characters; done = true; });
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (code != AckResponseCode.Success) { step.Failed($"RequestCharacters failed: {code}"); yield break; }
            PlayerCharacterData character = characters?.FirstOrDefault(ch => ch != null && (string.IsNullOrEmpty(expectedMapName) || ch.CurrentMapName == expectedMapName));
            if (character == null)
            {
                int existing = characters?.Count ?? 0;
                if (existing > 0)
                    Log($"{existing} character(s) exist but none on '{expectedMapName}' (maps: {string.Join(",", characters.Select(ch => ch.CurrentMapName))}); creating a new one");
                string newName = existing == 0 ? characterName : characterName + (existing + 1);
                SetPhase("create-character");
                BasePlayerCharacterEntity prefab = GameInstance.PlayerCharacterEntities.Values.FirstOrDefault();
                if (prefab == null || prefab.CharacterDatabases == null || prefab.CharacterDatabases.Length == 0) { step.Failed("No player character entity/database in GameInstance"); yield break; }
                int factionId = GameInstance.Factions.Values.FirstOrDefault(f => !f.IsLocked)?.DataId ?? 0;
                PlayerCharacterData data = new PlayerCharacterData();
                data.Id = GenericUtils.GetUniqueId();
                data.SetNewPlayerCharacterData(newName, prefab.CharacterDatabases[0].DataId, prefab.EntityId, factionId);
                done = false;
                client.RequestCreateCharacter(data, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { step.Failed($"Create character failed: {code}/{msg}"); yield break; }
                done = false;
                client.RequestCharacters((h, c, r) => { code = c; characters = r.characters; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                character = characters?.FirstOrDefault(ch => ch != null && ch.CharacterName == newName) ?? characters?.FirstOrDefault(ch => ch != null);
                if (character == null) { step.Failed("Character still missing after create"); yield break; }
                Log($"Created character '{newName}' on map '{character.CurrentMapName}'");
            }
            _character = character;
            step.ok = true;
        }

        /// <summary>
        /// 4) Select and enter the map: the select is retried while the map server is not ready (first login) or on any
        /// refusal (a re-login: the cluster still lists the user on the dead cell for a few seconds); then the map
        /// client, the spawned character (waited for enterSeconds after the select, or until the deadline when negative)
        /// and a respawn of a character loaded dead.
        /// </summary>
        private IEnumerator SelectAndEnter(MMOClientInstance client, float deadline, float enterSeconds, LoginStep step)
        {
            SetPhase("select-character");
            bool done = false; AckResponseCode code = AckResponseCode.Unimplemented;
            ResponseSelectCharacterMessage sel = default;
            // Map servers spawned by MapSpawn take a few seconds to register; retry while "not ready".
            while (true)
            {
                done = false;
                client.RequestSelectCharacter(_character.Id, (h, c, r) => { code = c; sel = r; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code == AckResponseCode.Success) break;
                bool retry = _relogging ? client.IsConnectedToCentralServer() : sel.message == UITextKeys.UI_ERROR_MAP_SERVER_NOT_READY;
                if (!retry || Time.realtimeSinceStartup >= deadline)
                {
                    step.Failed($"Select character failed: {code}/{sel.message}");
                    yield break;
                }
                if (_relogging)
                    Log($"Select refused ({code}/{sel.message}); retrying select in {reloginRetrySeconds:0.#}s ...");
                else
                    Log("Map server not ready yet; retrying select in 2s ...");
                yield return new WaitForSecondsRealtime(_relogging ? reloginRetrySeconds : 2f);
            }
            if (!GameInstance.MapInfos.TryGetValue(sel.mapName, out BaseMapInfo mapInfo)) { step.Failed($"Unknown map '{sel.mapName}'"); yield break; }
            _mapAddress = sel.networkAddress; _mapPort = sel.networkPort;
            Log($"Selected '{_character.CharacterName}' on map '{sel.mapName}' -> {_mapAddress}:{_mapPort} (channel {client.SelectedChannelId})");
            if (_relogging)
                Log(ReloginRoutedLine(_mapAddress, _mapPort));
            client.StartMapClient(mapInfo, _mapAddress, _mapPort);

            SetPhase("enter-map");
            float enterDeadline = enterSeconds < 0f ? deadline : Mathf.Min(deadline, Time.realtimeSinceStartup + enterSeconds);
            while (GameInstance.PlayingCharacterEntity == null && Time.realtimeSinceStartup < enterDeadline
                   && (!_relogging || client.MapNetworkManager == null || client.MapNetworkManager.IsClientConnected))
                yield return null;
            if (GameInstance.PlayingCharacterEntity == null) { step.Failed("Player never spawned"); yield break; }
            Log($"In map at {GameInstance.PlayingCharacterEntity.EntityTransform.position}");
            yield return new WaitForSecondsRealtime(settleSeconds);

            // A character that died in an earlier run is loaded dead; respawn it (same request the UI sends).
            if (GameInstance.PlayingCharacterEntity != null && GameInstance.PlayingCharacterEntity.IsDead())
            {
                SetPhase("respawn");
                Log("Character is dead; requesting respawn");
                done = false;
                GameInstance.ClientCharacterHandlers.RequestRespawn(new RequestRespawnMessage() { option = 0 }, (h, c, r) => { code = c; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                float aliveDeadline = Time.realtimeSinceStartup + 20f;
                float aliveSince = -1f;
                while (Time.realtimeSinceStartup < aliveDeadline)
                {
                    BasePlayerCharacterEntity pe = GameInstance.PlayingCharacterEntity;
                    bool alive = pe != null && !pe.IsDead() && pe.MaxHp > 0;
                    if (!alive) aliveSince = -1f;
                    else if (aliveSince < 0f) aliveSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - aliveSince > 1f) break; // stable for a second (a respawn may hand us off first)
                    yield return null;
                }
                if (GameInstance.PlayingCharacterEntity == null || GameInstance.PlayingCharacterEntity.IsDead()) { step.Failed($"Respawn failed ({code})"); yield break; }
                Log($"Respawned at {GameInstance.PlayingCharacterEntity.EntityTransform.position}");
                yield return new WaitForSecondsRealtime(1f);
            }
            step.ok = true;
        }

        /// <summary>
        /// Watches the map session once the first login has entered the map, until the run has finished. The kit cleans
        /// ClientGenericActions' events on every client stop (each migration hop is one), so the handler is added again
        /// every frame; a loss the handler saw starts a re-login here, while the mode's own coroutine keeps running.
        /// </summary>
        private IEnumerator WatchSession()
        {
            while (!Finished)
            {
                ClientGenericActions.onClientDisconnected -= OnMapSessionDisconnected;
                ClientGenericActions.onClientDisconnected += OnMapSessionDisconnected;
                if (_sessionLost != null)
                {
                    string why = _sessionLost;
                    _sessionLost = null;
                    yield return Relogin(why);
                    continue;
                }
                _migratingSeen = BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh && mesh.IsMigrating;
                yield return null;
            }
            ClientGenericActions.onClientDisconnected -= OnMapSessionDisconnected;
        }

        /// <summary>The kit's map-client disconnect: never a migration's own hop (MeshMapNetworkManager keeps those to itself).</summary>
        private void OnMapSessionDisconnected(DisconnectReason reason, SocketError socketError, UITextKeys message)
        {
            if (_relogging || Finished || _sessionLost != null)
                return;
            _sessionLost = SessionLostWhy(reason, message, _migratingSeen);
        }

        /// <summary>One re-login: login server, login, channel and select until the character is in a map again, or the budget is spent.</summary>
        private IEnumerator Relogin(string why)
        {
            Relogins++;
            int n = Relogins;
            Log(SessionLostLine(why, n));
            string phaseBefore = Phase;
            _relogging = true;
            float deadline = Time.realtimeSinceStartup + reloginBudgetSeconds;
            MMOClientInstance client = MMOClientInstance.Singleton;
            LoginStep step = new LoginStep();
            // A migration that timed out without a disconnect can leave its last hop's client running: start clean.
            if (client != null && client.MapNetworkManager != null && client.MapNetworkManager.IsClientConnected)
                client.StopMapClient();
            bool entered = false;
            while (!entered && Time.realtimeSinceStartup < deadline)
            {
                if (client == null || _character == null)
                {
                    client = MMOClientInstance.Singleton;
                    if (client == null || _character == null) { step.Failed(client == null ? "No MMOClientInstance" : "no character selected before"); break; }
                }
                step.Reset();
                if (!client.IsConnectedToCentralServer() || string.IsNullOrEmpty(GameInstance.UserId))
                {
                    yield return ConnectLoginServer(client, deadline, step);
                    if (!step.ok) continue;
                    yield return LoginUser(client, deadline, false, step);
                }
                if (step.ok)
                    yield return SelectChannel(client, deadline, step);
                if (step.ok)
                    yield return SelectAndEnter(client, deadline, reloginEnterSeconds, step);
                entered = step.ok;
                if (!entered && Time.realtimeSinceStartup < deadline)
                {
                    LogInvariant($"Re-login {n}: {step.error}; trying again in {reloginRetrySeconds:0.#}s");
                    if (client.MapNetworkManager != null && client.MapNetworkManager.IsClientConnected && GameInstance.PlayingCharacterEntity == null)
                        client.StopMapClient();
                    yield return new WaitForSecondsRealtime(reloginRetrySeconds);
                }
            }
            if (!entered)
                LogInvariant($"Re-login {n} gave up after {reloginBudgetSeconds:0}s: {step.error ?? "out of time"}");
            _relogging = false;
            if (Phase.StartsWith("relogin-", System.StringComparison.Ordinal))
                Phase = phaseBefore;   // the mode's own phase, unless the mode moved on meanwhile
        }
    }
}
