// Server Meshing add-on — automated test client.
// See Docs/ServerMeshing/SPIKE2.md ("auto client").
//
// EXTENSION-ONLY. Drives the kit's real client flow without touching the UI so the migration rig can
// run unattended: connect to the login server, log in (registering on first run), pick a channel,
// create/select a character, enter the map, then run a scripted test:
//
//   walk  — walk east across the C→E zone edge; the SERVER-side boundary tick hands the player off and
//           the client's masked migration is measured (Spike 2 Option B, real A→B hop).
//   self  — after entering, migrate back to the SAME map server (Spike 2 Option 0, connection swap only).
//   none  — just log in and enter the map.
//   stand — walk to -meshWalkX and hold there (a "bot" standing in a neighbour zone's border band).
//   pace  — walk to -meshWalkX, then pace between there and +8 m forever (a moving bot for ghost tests).
//   observe — walk to -meshWalkX, then watch for OTHER player entities (ghosts streamed from the
//             neighbour zone) for observeSeconds and report whether one was seen moving.
//   fight — walk to (-meshWalkX, -meshWalkZ), attack the nearest monster beyond x=100 (a ghost owned by the
//           neighbour zone) and report whether its hp dropped (cross-border hit) and whether we got hit back.
//
// Enable with command-line `-meshAutoClient` (build) or the "Server Meshing/Auto Client" menu (editor).
// Optional: -meshAutoMode walk|self|none  -meshChannel C  -meshUser name  -meshWalkX 130

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LiteNetLibManager;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    public class ServerMeshAutoClient : MonoBehaviour
    {
        public const string ARG_ENABLE = "-meshAutoClient";
        public const string ARG_MODE = "-meshAutoMode";
        public const string ARG_CHANNEL = "-meshChannel";
        public const string ARG_USER = "-meshUser";
        public const string ARG_WALK_X = "-meshWalkX";
        public const string ARG_WALK_Z = "-meshWalkZ";

#if UNITY_EDITOR
        public const string PREF_ENABLE = "ServerMesh.AutoClient.Enabled";
        public const string PREF_MODE = "ServerMesh.AutoClient.Mode";
        public const string PREF_CHANNEL = "ServerMesh.AutoClient.Channel";
        public const string PREF_USER = "ServerMesh.AutoClient.User";
        public const string PREF_WALK_X = "ServerMesh.AutoClient.WalkX";
        public const string PREF_WALK_Z = "ServerMesh.AutoClient.WalkZ";
#endif

        public enum Mode { none, walk, self, stand, pace, observe, fight }

        [Header("Session")]
        public string username = "meshtest";
        public string password = "meshtest123";
        public string email = "meshtest@example.com";
        public string channelId = "C";
        public string characterName = "MeshTester";
        [Tooltip("Map the test expects to play on; an existing character on another map is skipped and a new one created.")]
        public string expectedMapName = "ServerMeshTestMap";

        [Header("Test")]
        public Mode mode = Mode.walk;
        [Tooltip("World X to walk to (zone E starts at x=100 with the default 3x3 grid).")]
        public float walkTargetX = 130f;
        [Tooltip("World Z to walk at (fight mode walks to (walkTargetX, walkTargetZ)).")]
        public float walkTargetZ = 0f;
        [Tooltip("World X to walk back to after reaching walkTargetX (zone C).")]
        public float walkReturnX = 0f;
        [Tooltip("Seconds to wait after entering the map before starting the test.")]
        public float settleSeconds = 3f;
        [Tooltip("observe mode: seconds to watch for other player entities after arriving.")]
        public float observeSeconds = 60f;
        [Tooltip("observe mode: only positions beyond this world X count as ghost sightings (the neighbour zone starts at x=100).")]
        public float observeGhostBeyondX = 100f;
        [Tooltip("Overall timeout for the whole run.")]
        public float timeoutSeconds = 240f;

        public static ServerMeshAutoClient Instance { get; private set; }
        public string Phase { get; private set; } = "idle";
        public bool Finished { get; private set; }
        public bool Passed { get; private set; }

        private string _mapAddress;
        private int _mapPort;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool enabled = args.Contains(ARG_ENABLE);
            string modeStr = ReadArg(args, ARG_MODE, null);
            string channel = ReadArg(args, ARG_CHANNEL, null);
            string user = ReadArg(args, ARG_USER, null);
            string walkX = ReadArg(args, ARG_WALK_X, null);
            string walkZ = ReadArg(args, ARG_WALK_Z, null);
#if UNITY_EDITOR
            if (!enabled && EditorPrefs.GetBool(PREF_ENABLE, false))
            {
                enabled = true;
                modeStr = modeStr ?? EditorPrefs.GetString(PREF_MODE, "walk");
                channel = channel ?? EditorPrefs.GetString(PREF_CHANNEL, "C");
                user = user ?? EditorPrefs.GetString(PREF_USER, "meshtest");
                walkX = walkX ?? EditorPrefs.GetString(PREF_WALK_X, "130");
                walkZ = walkZ ?? EditorPrefs.GetString(PREF_WALK_Z, "0");
            }
#endif
            if (!enabled)
                return;

            GameObject go = new GameObject("[ServerMesh] AutoClient");
            DontDestroyOnLoad(go);
            ServerMeshAutoClient c = go.AddComponent<ServerMeshAutoClient>();
            if (!string.IsNullOrEmpty(modeStr) && System.Enum.TryParse(modeStr, true, out Mode m)) c.mode = m;
            if (!string.IsNullOrEmpty(channel)) c.channelId = channel;
            if (!string.IsNullOrEmpty(user)) { c.username = user; c.characterName = user + "Char"; } // names are global: derive from the user
            if (!string.IsNullOrEmpty(walkX) && float.TryParse(walkX, out float wx)) c.walkTargetX = wx;
            if (!string.IsNullOrEmpty(walkZ) && float.TryParse(walkZ, out float wz)) c.walkTargetZ = wz;
        }

        private static string ReadArg(string[] args, string key, string fallback)
        {
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (args[i] == key)
                    return args[i + 1];
            }
            return fallback;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        private void Log(string msg) => Debug.Log($"[MeshAuto] {msg}");
        private void Fail(string msg)
        {
            Debug.LogError($"[MeshAuto] FAIL ({Phase}): {msg}");
            Finished = true;
            Passed = false;
        }

        private IEnumerator Run()
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;

            // 0) Wait for the client instance and connect to the login server.
            Phase = "wait-client-instance";
            while (MMOClientInstance.Singleton == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (MMOClientInstance.Singleton == null) { Fail("No MMOClientInstance"); yield break; }
            MMOClientInstance client = MMOClientInstance.Singleton;

            Phase = "connect-login";
            // Give in-process servers a moment to come up, then (re)try connecting.
            yield return new WaitForSecondsRealtime(1f);
            float nextAttempt = 0f;
            while (!client.IsConnectedToCentralServer() && Time.realtimeSinceStartup < deadline)
            {
                if (Time.realtimeSinceStartup >= nextAttempt)
                {
                    nextAttempt = Time.realtimeSinceStartup + 3f;
                    Log($"Connecting to login server {client.LoginNetworkManager.networkAddress}:{client.LoginNetworkManager.networkPort} ...");
                    client.StartCentralClient();
                }
                yield return null;
            }
            if (!client.IsConnectedToCentralServer()) { Fail("Could not connect to the login server"); yield break; }
            Log("Connected to login server");

            // 1) Login (register on first run).
            Phase = "login";
            bool done = false; AckResponseCode code = AckResponseCode.Unimplemented; UITextKeys msg = UITextKeys.NONE;
            client.RequestUserLogin(username, password, (h, c, r) => { code = c; msg = r.message; done = true; });
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (code != AckResponseCode.Success)
            {
                Log($"Login failed ({code}/{msg}); registering '{username}'");
                Phase = "register";
                done = false;
                client.RequestUserRegister(username, password, email, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { Fail($"Register failed: {code}/{msg}"); yield break; }
                Phase = "login2";
                done = false;
                client.RequestUserLogin(username, password, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { Fail($"Login after register failed: {code}/{msg}"); yield break; }
            }
            Log($"Logged in as '{username}' (userId={GameInstance.UserId})");

            // 2) Channel.
            Phase = "channels";
            done = false; List<ChannelEntry> channels = null;
            client.RequestChannels((h, c, r) => { code = c; channels = r.channels; done = true; });
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (code != AckResponseCode.Success || channels == null || channels.Count == 0) { Fail($"No channels ({code})"); yield break; }
            string chosen = channels.Any(ch => ch.id == channelId) ? channelId : channels[0].id;
            client.SelectedChannelId = chosen;
            Log($"Channels: {string.Join(",", channels.Select(ch => ch.id))} -> selected '{chosen}'");

            // 3) Character (create on first run).
            Phase = "characters";
            done = false; List<PlayerCharacterData> characters = null;
            client.RequestCharacters((h, c, r) => { code = c; characters = r.characters; done = true; });
            while (!done && Time.realtimeSinceStartup < deadline) yield return null;
            if (code != AckResponseCode.Success) { Fail($"RequestCharacters failed: {code}"); yield break; }
            PlayerCharacterData character = characters?.FirstOrDefault(ch => ch != null && (string.IsNullOrEmpty(expectedMapName) || ch.CurrentMapName == expectedMapName));
            if (character == null)
            {
                int existing = characters?.Count ?? 0;
                if (existing > 0)
                    Log($"{existing} character(s) exist but none on '{expectedMapName}' (maps: {string.Join(",", characters.Select(ch => ch.CurrentMapName))}); creating a new one");
                string newName = existing == 0 ? characterName : characterName + (existing + 1);
                Phase = "create-character";
                BasePlayerCharacterEntity prefab = GameInstance.PlayerCharacterEntities.Values.FirstOrDefault();
                if (prefab == null || prefab.CharacterDatabases == null || prefab.CharacterDatabases.Length == 0) { Fail("No player character entity/database in GameInstance"); yield break; }
                int factionId = GameInstance.Factions.Values.FirstOrDefault(f => !f.IsLocked)?.DataId ?? 0;
                PlayerCharacterData data = new PlayerCharacterData();
                data.Id = GenericUtils.GetUniqueId();
                data.SetNewPlayerCharacterData(newName, prefab.CharacterDatabases[0].DataId, prefab.EntityId, factionId);
                done = false;
                client.RequestCreateCharacter(data, (h, c, r) => { code = c; msg = r.message; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code != AckResponseCode.Success) { Fail($"Create character failed: {code}/{msg}"); yield break; }
                done = false;
                client.RequestCharacters((h, c, r) => { code = c; characters = r.characters; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                character = characters?.FirstOrDefault(ch => ch != null && ch.CharacterName == newName) ?? characters?.FirstOrDefault(ch => ch != null);
                if (character == null) { Fail("Character still missing after create"); yield break; }
                Log($"Created character '{newName}' on map '{character.CurrentMapName}'");
            }

            // 4) Select + enter map.
            Phase = "select-character";
            ResponseSelectCharacterMessage sel = default;
            // Map servers spawned by MapSpawn take a few seconds to register; retry while "not ready".
            while (true)
            {
                done = false;
                client.RequestSelectCharacter(character.Id, (h, c, r) => { code = c; sel = r; done = true; });
                while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                if (code == AckResponseCode.Success) break;
                if (sel.message != UITextKeys.UI_ERROR_MAP_SERVER_NOT_READY || Time.realtimeSinceStartup >= deadline)
                {
                    Fail($"Select character failed: {code}/{sel.message}");
                    yield break;
                }
                Log("Map server not ready yet; retrying select in 2s ...");
                yield return new WaitForSecondsRealtime(2f);
            }
            if (!GameInstance.MapInfos.TryGetValue(sel.mapName, out BaseMapInfo mapInfo)) { Fail($"Unknown map '{sel.mapName}'"); yield break; }
            _mapAddress = sel.networkAddress; _mapPort = sel.networkPort;
            Log($"Selected '{character.CharacterName}' on map '{sel.mapName}' -> {_mapAddress}:{_mapPort} (channel {chosen})");
            client.StartMapClient(mapInfo, _mapAddress, _mapPort);

            Phase = "enter-map";
            while (GameInstance.PlayingCharacterEntity == null && Time.realtimeSinceStartup < deadline) yield return null;
            if (GameInstance.PlayingCharacterEntity == null) { Fail("Player never spawned"); yield break; }
            Log($"In map at {GameInstance.PlayingCharacterEntity.EntityTransform.position}");
            yield return new WaitForSecondsRealtime(settleSeconds);

            // A character that died in an earlier run is loaded dead; respawn it (same request the UI sends).
            if (GameInstance.PlayingCharacterEntity != null && GameInstance.PlayingCharacterEntity.IsDead())
            {
                Phase = "respawn";
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
                if (GameInstance.PlayingCharacterEntity == null || GameInstance.PlayingCharacterEntity.IsDead()) { Fail($"Respawn failed ({code})"); yield break; }
                Log($"Respawned at {GameInstance.PlayingCharacterEntity.EntityTransform.position}");
                yield return new WaitForSecondsRealtime(1f);
            }

            // 5) Test.
            switch (mode)
            {
                case Mode.self:
                    yield return RunSelfMigration(deadline);
                    break;
                case Mode.walk:
                    yield return RunWalk(deadline);
                    break;
                case Mode.stand:
                    yield return RunStand(deadline);
                    break;
                case Mode.pace:
                    yield return RunPace(deadline);
                    break;
                case Mode.observe:
                    yield return RunObserve(deadline);
                    break;
                case Mode.fight:
                    yield return RunFight(deadline);
                    break;
                default:
                    Finished = true; Passed = true;
                    Log("Mode none: done");
                    break;
            }
        }

        private IEnumerator RunSelfMigration(float deadline)
        {
            Phase = "self-migrate";
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null) { Fail("BaseGameNetworkManager.Singleton is not a MeshMapNetworkManager"); yield break; }
            int before = ClientMigrationController.CompletedCount;
            Log($"Self-migrating to {_mapAddress}:{_mapPort}");
            mesh.DebugMigrate(_mapAddress, _mapPort);
            while (ClientMigrationController.CompletedCount == before && Time.realtimeSinceStartup < deadline) yield return null;
            if (ClientMigrationController.CompletedCount == before) { Fail("Migration never completed"); yield break; }
            ClientMigrationController.Result r = ClientMigrationController.LastResult;
            Finished = true; Passed = r.connected && r.playerReady;
            Log($"RESULT self-migration: playerReady={r.playerReady} gap={r.playerReadyMs}ms jump={r.positionJump:0.##}m -> {(Passed ? "PASS" : "FAIL")}");
        }

        private IEnumerator RunWalk(float deadline)
        {
            // Round trip: out to walkTargetX (zone E), back to walkReturnX (zone C). A character that was
            // saved in another zone gets handed off right at login, which also counts as a migration.
            int before = ClientMigrationController.CompletedCount;
            float[] legs = { walkTargetX, walkReturnX };
            bool allLegsReached = true;
            for (int leg = 0; leg < legs.Length; ++leg)
            {
                Phase = $"walk-leg{leg + 1}";
                bool reached = false;
                yield return WalkTo(legs[leg], deadline, before, r => reached = r);
                allLegsReached &= reached;
                if (!reached) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            BasePlayerCharacterEntity final = GameInstance.PlayingCharacterEntity;
            Vector3 finalPos = final != null ? final.EntityTransform.position : Vector3.zero;
            int migrations = ClientMigrationController.CompletedCount - before;
            ClientMigrationController.Result last = ClientMigrationController.LastResult;
            Finished = true;
            Passed = allLegsReached && migrations >= 1 && _allMigrationsReady;
            Log($"RESULT walk: migrations={migrations} allReady={_allMigrationsReady} maxGap={_maxGapMs}ms maxJump={_maxJump:0.##}m finalPos={finalPos} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private IEnumerator RunStand(float deadline)
        {
            Phase = "stand";
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            Finished = true; Passed = reached;
            Log($"RESULT stand: reached={reached} pos={(GameInstance.PlayingCharacterEntity != null ? GameInstance.PlayingCharacterEntity.EntityTransform.position : Vector3.zero)} -> {(Passed ? "PASS" : "FAIL")} (holding position)");
        }

        private IEnumerator RunPace(float deadline)
        {
            Phase = "pace";
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            if (!reached) { Fail("never reached the pacing start"); yield break; }
            Log($"Pacing between x={walkTargetX} and x={walkTargetX + 8f}");
            float far = walkTargetX + 8f;
            float target = far;
            float pacingDeadline = Time.realtimeSinceStartup + 600f;
            while (Time.realtimeSinceStartup < pacingDeadline)
            {
                BasePlayerCharacterEntity p = GameInstance.PlayingCharacterEntity;
                if (p != null)
                {
                    Vector3 pos = p.EntityTransform.position;
                    if (Mathf.Abs(pos.x - target) < 1f)
                        target = Mathf.Approximately(target, far) ? walkTargetX : far;
                    p.PointClickMovement(new Vector3(target, pos.y, pos.z));
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Finished = true; Passed = true;
        }

        private IEnumerator RunObserve(float deadline)
        {
            Phase = "observe";
            int before = ClientMigrationController.CompletedCount;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, before, r => reached = r);
            if (!reached) { Fail("never reached the observation point"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            Log($"Observing from {self.EntityTransform.position} for {observeSeconds}s");
            Dictionary<string, List<Vector3>> seen = new Dictionary<string, List<Vector3>>();
            float end = Time.realtimeSinceStartup + observeSeconds;
            float nextSample = 0f;
            while (Time.realtimeSinceStartup < end)
            {
                if (Time.realtimeSinceStartup >= nextSample)
                {
                    nextSample = Time.realtimeSinceStartup + 0.5f;
                    foreach (BaseCharacterEntity e in FindObjectsByType<BaseCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (e == GameInstance.PlayingCharacterEntity) continue;
                        string key = $"{e.GetType().Name}|{e.CharacterName}|{e.ObjectId}";
                        if (!seen.TryGetValue(key, out List<Vector3> list)) { list = new List<Vector3>(); seen[key] = list; Log($"Saw other entity '{e.CharacterName}' ({e.GetType().Name}) at {e.EntityTransform.position}"); }
                        Vector3 pos = e.EntityTransform.position;
                        if (pos.x < observeGhostBeyondX) continue; // still on our own server; not a ghost
                        if (list.Count == 0 || Vector3.Distance(list[list.Count - 1], pos) > 0.5f) list.Add(pos);
                    }
                }
                yield return null;
            }
            bool movedGhost = false;
            int ghostsSeen = 0, playerGhosts = 0, monsterGhosts = 0;
            foreach (KeyValuePair<string, List<Vector3>> kv in seen)
            {
                if (kv.Value.Count == 0) { Log($"Other entity {kv.Key}: seen only on our side of the edge (not a ghost)"); continue; }
                ghostsSeen++;
                if (kv.Key.StartsWith("BasePlayer") || kv.Key.Contains("Player")) playerGhosts++; else monsterGhosts++;
                Log($"Ghost {kv.Key}: {kv.Value.Count} distinct positions beyond x={observeGhostBeyondX}, first={kv.Value[0]} last={kv.Value[kv.Value.Count - 1]}");
                if (kv.Value.Count >= 2) movedGhost = true;
            }
            int migrations = ClientMigrationController.CompletedCount - before;
            Finished = true;
            // Monsters in the demo idle unless aggroed, so movement is only required of player ghosts.
            Passed = ghostsSeen > 0 && (playerGhosts == 0 || movedGhost) && migrations == 0;
            Log($"RESULT observe: ghostsSeen={ghostsSeen} (players={playerGhosts} monsters={monsterGhosts}) moved={movedGhost} migrationsDuringObserve={migrations} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private float _deadSince = -1f;
        private int _respawnsUsed;
        private IEnumerator RunFight(float deadline)
        {
            Phase = "fight";
            _walkZOverride = walkTargetZ;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the fight position"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            int selfHpStart = self.CurrentHp, selfMaxHp = self.MaxHp;
            int expStart = self.Exp, goldStart = self.Gold, levelStart = self.Level;

            // Hold the line: stay on OUR side of the edge (x <= holdX) and fight whatever monster wanders within reach.
            // Our zone runs no spawn areas here, so every monster we see is a ghost owned by the neighbour; hits we land
            // are forwarded to the owner, hits the owner's real monster lands on our ghost come back the same way.
            float holdX = observeGhostBeyondX - 0.5f;
            float attackRange = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f);
            float end = Time.realtimeSinceStartup + 150f;
            float nextAttack = 0f, nextLog = 0f;
            BaseMonsterCharacterEntity target = null;
            int targetHpStart = 0, minTargetHp = int.MaxValue, minSelfHp = selfHpStart, kills = 0;
            bool anyHurt = false;
            while (Time.realtimeSinceStartup < end)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self == null) break;
                if (self.IsDead()) { Log("we died"); minSelfHp = 0; break; }
                if (target != null && target.IsDead())
                {
                    ++kills;
                    Log($"target died (kill #{kills}); exp={self.Exp} gold={self.Gold} level={self.Level}");
                    target = null;
                }
                if (target == null || target.EntityTransform.position.x > holdX + attackRange + 0.5f)
                {
                    // Nearest alive monster we can reach without leaving our side.
                    target = null; float best = float.MaxValue;
                    foreach (BaseMonsterCharacterEntity m in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (m == null || m.IsDead() || m.EntityTransform.position.x > holdX + attackRange + 0.5f) continue;
                        float d = Vector3.Distance(m.EntityTransform.position, self.EntityTransform.position);
                        if (d < best) { best = d; target = m; }
                    }
                    if (target != null)
                    {
                        targetHpStart = target.CurrentHp; minTargetHp = targetHpStart;
                        Log($"Engaging '{target.name}' hp={target.CurrentHp}/{target.MaxHp} at {target.EntityTransform.position} (dist {best:0.#}m)");
                    }
                }
                if (target != null)
                {
                    Vector3 toTarget = target.EntityTransform.position - self.EntityTransform.position; toTarget.y = 0f;
                    if (toTarget.magnitude > attackRange)
                    {
                        Vector3 dest = target.EntityTransform.position - toTarget.normalized * (attackRange * 0.7f);
                        dest.x = Mathf.Min(dest.x, holdX);
                        self.PointClickMovement(dest);
                    }
                    else if (Time.realtimeSinceStartup >= nextAttack)
                    {
                        nextAttack = Time.realtimeSinceStartup + 0.6f;
                        self.StopMove();
                        self.SetLookRotation(Quaternion.LookRotation(toTarget.normalized), true);
                        self.SetTargetEntity(target);
                        bool isLeftHand = false;
                        self.AimPosition = self.GetAttackAimPosition(ref isLeftHand, target.EntityTransform.position);
                        WeaponHandlingState whs = WeaponHandlingState.None;
                        self.Attack(ref whs);
                    }
                    if (target.CurrentHp < targetHpStart) anyHurt = true;
                    minTargetHp = Mathf.Min(minTargetHp, target.CurrentHp);
                }
                else if (self.EntityTransform.position.x > holdX)
                {
                    Vector3 back = self.EntityTransform.position; back.x = holdX - 1f;
                    self.PointClickMovement(back);
                }
                minSelfHp = Mathf.Min(minSelfHp, self.CurrentHp);
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 3f;
                    Log(target != null
                        ? $"target hp={target.CurrentHp} self hp={self.CurrentHp} pos={self.EntityTransform.position}"
                        : $"waiting for a monster within reach; self hp={self.CurrentHp} pos={self.EntityTransform.position}");
                }
                if (kills >= 3) break;
                yield return null;
            }
            // Give a pending kill credit a moment to travel owner -> our cell.
            yield return new WaitForSecondsRealtime(1.5f);
            self = GameInstance.PlayingCharacterEntity;
            int expGained = self != null ? self.Exp - expStart : 0, goldGained = self != null ? self.Gold - goldStart : 0;
            int levelGained = self != null ? self.Level - levelStart : 0;
            bool selfHurt = minSelfHp < selfHpStart;
            Finished = true; Passed = anyHurt;
            Log($"RESULT fight: hurtTarget={anyHurt} kills={kills} selfHp {selfHpStart}/{selfMaxHp}->{minSelfHp} (hitBack={selfHurt}) exp+{expGained} gold+{goldGained} level+{levelGained} -> {(Passed ? "PASS" : "FAIL")}");
            if (self != null && !self.IsDead())
            {
                // Retreat to the zone start so the next run does not begin next to the neighbour's monsters.
                _walkZOverride = 0f;
                bool back = false;
                yield return WalkTo(walkReturnX, Time.realtimeSinceStartup + 90f, ClientMigrationController.CompletedCount, r => back = r);
                _walkZOverride = null;
                Log($"Retreated to start: {back}");
            }
        }

        private bool _allMigrationsReady = true;
        private long _maxGapMs;
        private float _maxJump;

        private float? _walkZOverride;

        private IEnumerator WalkTo(float targetX, float deadline, int migrationsBefore, System.Action<bool> onDone)
        {
            BasePlayerCharacterEntity p0 = GameInstance.PlayingCharacterEntity;
            if (p0 == null) { onDone(false); yield break; }
            Vector3 start = p0.EntityTransform.position;
            if (_walkZOverride.HasValue) start.z = _walkZOverride.Value;
            Log($"Walking from {start} to x={targetX}");
            float nextOrder = 0f;
            float lastLogT = 0f;
            int seen = ClientMigrationController.CompletedCount;
            while (Time.realtimeSinceStartup < deadline)
            {
                BasePlayerCharacterEntity p = GameInstance.PlayingCharacterEntity;
                if (p != null)
                {
                    Vector3 pos = p.EntityTransform.position;
                    // A freshly spawned entity reports hp=0 until its sync fields arrive; only a sustained death counts.
                    if (p.IsDead())
                    {
                        if (_deadSince < 0f) _deadSince = Time.realtimeSinceStartup;
                        if (Time.realtimeSinceStartup - _deadSince > 3f)
                        {
                            if (_respawnsUsed >= 1)
                            {
                                Fail($"Character died at {pos} (hp={p.CurrentHp}); disable monsters on the test map");
                                onDone(false);
                                yield break;
                            }
                            // One free respawn (falls off-map, edge monsters): go back to the map start and keep walking.
                            _respawnsUsed++;
                            _deadSince = -1f;
                            Log($"Character died at {pos}; respawning and continuing");
                            bool rdone = false;
                            GameInstance.ClientCharacterHandlers.RequestRespawn(new RequestRespawnMessage() { option = 0 }, (h, c, r) => { rdone = true; });
                            float rd = Time.realtimeSinceStartup + 10f;
                            while (!rdone && Time.realtimeSinceStartup < rd) yield return null;
                            while ((GameInstance.PlayingCharacterEntity == null || GameInstance.PlayingCharacterEntity.IsDead()) && Time.realtimeSinceStartup < rd) yield return null;
                            yield return new WaitForSecondsRealtime(2f);
                            continue;
                        }
                    }
                    else _deadSince = -1f;
                    if (Time.realtimeSinceStartup - lastLogT > 2f)
                    {
                        lastLogT = Time.realtimeSinceStartup;
                        Log($"pos={pos} migrations={ClientMigrationController.CompletedCount - migrationsBefore}");
                    }
                    if (Mathf.Abs(pos.x - targetX) < 1.5f && (!_walkZOverride.HasValue || Mathf.Abs(pos.z - start.z) < 1.5f))
                    {
                        p.StopMove();
                        onDone(true);
                        yield break;
                    }
                    if (Time.realtimeSinceStartup >= nextOrder)
                    {
                        nextOrder = Time.realtimeSinceStartup + 0.5f;
                        p.PointClickMovement(new Vector3(targetX, pos.y, start.z));
                    }
                }
                if (ClientMigrationController.CompletedCount > seen)
                {
                    seen = ClientMigrationController.CompletedCount;
                    ClientMigrationController.Result r = ClientMigrationController.LastResult;
                    _allMigrationsReady &= r.playerReady;
                    _maxGapMs = System.Math.Max(_maxGapMs, r.playerReadyMs);
                    _maxJump = Mathf.Max(_maxJump, r.positionJump);
                    Log($"Migration observed: seamless={r.seamless} playerReady={r.playerReady} gap={r.playerReadyMs}ms jump={r.positionJump:0.##}m resync={r.resyncJump:0.##}m framesWithoutCamera={r.framesWithoutCamera} standIns={r.standIns}");
                }
                yield return null;
            }
            onDone(false);
        }

#if UNITY_EDITOR
        [MenuItem("Server Meshing/Auto Client/Enable (walk C->E)")]
        private static void MenuEnableWalk() { EditorPrefs.SetBool(PREF_ENABLE, true); EditorPrefs.SetString(PREF_MODE, "walk"); Debug.Log("[MeshAuto] enabled: walk"); }
        [MenuItem("Server Meshing/Auto Client/Enable (self-migration)")]
        private static void MenuEnableSelf() { EditorPrefs.SetBool(PREF_ENABLE, true); EditorPrefs.SetString(PREF_MODE, "self"); Debug.Log("[MeshAuto] enabled: self"); }
        [MenuItem("Server Meshing/Auto Client/Enable (enter only)")]
        private static void MenuEnableNone() { EditorPrefs.SetBool(PREF_ENABLE, true); EditorPrefs.SetString(PREF_MODE, "none"); Debug.Log("[MeshAuto] enabled: none"); }
        [MenuItem("Server Meshing/Auto Client/Disable")]
        private static void MenuDisable() { EditorPrefs.SetBool(PREF_ENABLE, false); Debug.Log("[MeshAuto] disabled"); }
#endif
    }
}
