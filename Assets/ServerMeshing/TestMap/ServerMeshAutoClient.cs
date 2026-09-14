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
        public const string ARG_MOUNT = "-meshMount";
        public const string ARG_PET = "-meshPet";

#if UNITY_EDITOR
        public const string PREF_ENABLE = "ServerMesh.AutoClient.Enabled";
        public const string PREF_MODE = "ServerMesh.AutoClient.Mode";
        public const string PREF_CHANNEL = "ServerMesh.AutoClient.Channel";
        public const string PREF_USER = "ServerMesh.AutoClient.User";
        public const string PREF_WALK_X = "ServerMesh.AutoClient.WalkX";
        public const string PREF_WALK_Z = "ServerMesh.AutoClient.WalkZ";
        public const string PREF_MOUNT = "ServerMesh.AutoClient.Mount";
        public const string PREF_PET = "ServerMesh.AutoClient.Pet";
#endif

        public enum Mode { none, walk, self, stand, pace, observe, fight, pvp }

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
        [Tooltip("Use the first mount item in the inventory before the test (needs a MountItem in the character's bag).")]
        public bool mountUp = false;
        [Tooltip("Use the first pet item in the inventory before the test (needs a PetItem in the character's bag).")]
        public bool summonPet = false;
        [Tooltip("World X to walk back to after reaching walkTargetX (zone C).")]
        public float walkReturnX = 0f;
        [Tooltip("Seconds to wait after entering the map before starting the test.")]
        public float settleSeconds = 3f;
        [Tooltip("observe mode: seconds to watch for other player entities after arriving.")]
        public float observeSeconds = 100f;
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
            bool mount = args.Contains(ARG_MOUNT);
            bool pet = args.Contains(ARG_PET);
#if UNITY_EDITOR
            if (!enabled && EditorPrefs.GetBool(PREF_ENABLE, false))
            {
                enabled = true;
                modeStr = modeStr ?? EditorPrefs.GetString(PREF_MODE, "walk");
                channel = channel ?? EditorPrefs.GetString(PREF_CHANNEL, "C");
                user = user ?? EditorPrefs.GetString(PREF_USER, "meshtest");
                walkX = walkX ?? EditorPrefs.GetString(PREF_WALK_X, "130");
                walkZ = walkZ ?? EditorPrefs.GetString(PREF_WALK_Z, "0");
                mount = mount || EditorPrefs.GetBool(PREF_MOUNT, false);
                pet = pet || EditorPrefs.GetBool(PREF_PET, false);
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
            c.mountUp = mount;
            c.summonPet = pet;
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
            if (Application.isBatchMode)
                StartCoroutine(HeadlessGuards());
        }

        /// <summary>
        /// Headless (-batchmode -nographics) client: the kit's RaycastButtonManager3D (created by the vending sign's
        /// RaycastButton3D on every player character) polls the Input System mouse each frame, and Mouse.current is null
        /// without a display — an NRE per frame. Test tooling only: keep it disabled while the bot runs.
        /// </summary>
        private IEnumerator HeadlessGuards()
        {
            WaitForSecondsRealtime wait = new WaitForSecondsRealtime(0.1f); // the manager is created with the player entity; every frame until then is one NRE
            while (true)
            {
                foreach (UtilsComponents.RaycastButtonManager3D m in FindObjectsByType<UtilsComponents.RaycastButtonManager3D>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (m != null && m.enabled)
                        m.enabled = false;
                }
                yield return wait;
            }
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

            if (mountUp)
                yield return MountUp();
            if (summonPet)
                yield return SummonPet();

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
                case Mode.pvp:
                    yield return RunPvp(deadline);
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

        private IEnumerator MountUp()
        {
            Phase = "mount";
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) yield break;
            if (self.PassengingVehicleEntity != null) { Log("Already mounted"); yield break; }
            int index = -1;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                BaseItem bagItem = self.NonEquipItems[i].GetItem();
                if (bagItem != null && bagItem.IsMount() && bagItem is IMountItem) { index = i; break; }
            }
            if (index < 0) { Log("No mount item in the inventory; continuing on foot"); yield break; }
            bool sent = self.CallCmdUseItem(index);
            float end = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < end && (GameInstance.PlayingCharacterEntity == null || GameInstance.PlayingCharacterEntity.PassengingVehicleEntity == null))
                yield return null;
            self = GameInstance.PlayingCharacterEntity;
            bool mounted = self != null && self.PassengingVehicleEntity != null;
            Log($"Mount up: item slot {index} sent={sent} mounted={mounted}" + (mounted ? $" on '{self.PassengingVehicleEntity.Entity.name}' speed={self.GetMoveSpeed():0.#}" : ""));
            yield return new WaitForSecondsRealtime(1f);
        }

        private IEnumerator SummonPet()
        {
            Phase = "pet";
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) yield break;
            if (self.Summons.Count > 0) { Log($"Already has {self.Summons.Count} summon(s)"); yield break; }
            int index = -1;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                BaseItem bagItem = self.NonEquipItems[i].GetItem();
                if (bagItem != null && bagItem.IsPet() && bagItem is IPetItem) { index = i; break; }
            }
            if (index < 0) { Log("No pet item in the inventory; continuing without a pet"); yield break; }
            bool sent = self.CallCmdUseItem(index);
            float end = Time.realtimeSinceStartup + 10f;
            BaseMonsterCharacterEntity pet = null;
            while (Time.realtimeSinceStartup < end && (pet = FindOwnPet()) == null)
                yield return new WaitForSecondsRealtime(0.25f);
            self = GameInstance.PlayingCharacterEntity;
            Log($"Summon pet: item slot {index} sent={sent} summons={(self != null ? self.Summons.Count : 0)}" + (pet != null ? $" pet '{pet.CharacterName}' lv{pet.Level} hp={pet.CurrentHp}/{pet.MaxHp} at {pet.EntityTransform.position}" : " (pet entity not seen)"));
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>The playing character's summoned monster as this client sees it (null when none is spawned here).</summary>
        private static BaseMonsterCharacterEntity FindOwnPet()
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) return null;
            foreach (BaseMonsterCharacterEntity m in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
            {
                if (m != null && m.IsSummoned && m.Summoner.objectId == self.ObjectId && !m.IsDead()) return m;
            }
            return null;
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
                // With a Z target the out leg goes to (walkTargetX, walkTargetZ) — e.g. across a corner — and the return
                // leg comes back to (walkReturnX, 0).
                _walkZOverride = walkTargetZ != 0f ? (leg == 0 ? walkTargetZ : 0f) : (float?)null;
                yield return WalkTo(legs[leg], deadline, before, r => reached = r);
                _walkZOverride = null;
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
            BasePlayerCharacterEntity walker = GameInstance.PlayingCharacterEntity;
            Log($"RESULT walk: migrations={migrations} allReady={_allMigrationsReady} maxGap={_maxGapMs}ms maxJump={_maxJump:0.##}m finalPos={finalPos} summons={(walker != null ? walker.Summons.Count : -1)} pet={(FindOwnPet() != null ? "seen" : "none")} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private IEnumerator RunStand(float deadline)
        {
            Phase = "stand";
            bool reached = false;
            _walkZOverride = walkTargetZ != 0f ? walkTargetZ : (float?)null;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            Log($"Standing at {(self != null ? self.EntityTransform.position : Vector3.zero)} party={(self != null ? self.PartyId : 0)}; watching exp/gold/quests for {observeSeconds}s");
            int expStart = self != null ? self.Exp : 0, goldStart = self != null ? self.Gold : 0, levelStart = self != null ? self.Level : 0, killsStart = CountQuestKills(self);
            float end = Time.realtimeSinceStartup + observeSeconds, nextLog = 0f;
            while (Time.realtimeSinceStartup < end)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self != null && Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 10f;
                    Log($"stand: level={self.Level} exp={self.Exp} gold={self.Gold} questKills={CountQuestKills(self)} hp={self.CurrentHp}/{self.MaxHp} buffs={self.Buffs.Count}");
                }
                yield return null;
            }
            self = GameInstance.PlayingCharacterEntity;
            Finished = true; Passed = reached;
            Log($"RESULT stand: reached={reached} exp+{(self != null ? self.Exp - expStart : 0)} gold+{(self != null ? self.Gold - goldStart : 0)} level+{(self != null ? self.Level - levelStart : 0)} questKills+{CountQuestKills(self) - killsStart} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private static int CountQuestKills(BasePlayerCharacterEntity self)
        {
            if (self == null) return 0;
            int n = 0;
            for (int i = 0; i < self.Quests.Count; ++i)
            {
                CharacterQuest q = self.Quests[i];
                if (q.killedMonsters == null) continue;
                foreach (KeyValuePair<int, int> kv in q.killedMonsters) n += kv.Value;
            }
            return n;
        }

        private IEnumerator RunPace(float deadline)
        {
            Phase = "pace";
            bool reached = false;
            _walkZOverride = walkTargetZ != 0f ? walkTargetZ : (float?)null; // load runs spread the pacers along z
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
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
            _walkZOverride = walkTargetZ != 0f ? walkTargetZ : (float?)null;
            yield return WalkTo(walkTargetX, deadline, before, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the observation point"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            Log($"Observing from {self.EntityTransform.position} for {observeSeconds}s");
            Dictionary<string, List<Vector3>> seen = new Dictionary<string, List<Vector3>>();
            // Continuity of OTHER players as seen from here: an entity instance change is a "swap" (ghost <-> real),
            // frames with no entity for a name we have seen are an "absence" (what an observer perceives as a blink).
            Dictionary<string, int> lastInstance = new Dictionary<string, int>();
            Dictionary<string, float> absentSince = new Dictionary<string, float>();
            HashSet<string> presentNow = new HashSet<string>();
            int swaps = 0, absences = 0; float maxAbsenceMs = 0f;
            // Attacks and missiles seen on the far side of the line (replayed on ghosts): a swing is counted when an
            // entity beyond the line goes from idle to attacking; missiles by distinct damage-entity instances.
            int ghostAttacks = 0, ghostSkills = 0; HashSet<int> attackingNow = new HashSet<int>(), castingNow = new HashSet<int>(); HashSet<int> missileIds = new HashSet<int>();
            const string petPrefix = "pet of ";
            // One entity we watch under one name: present again after a gap (an absence), a different instance under
            // the same name (a swap), or first seen.
            void Track(string name, int inst, Vector3 pos, float now, string detail)
            {
                presentNow.Add(name);
                if (absentSince.TryGetValue(name, out float since))
                {
                    float gapMs = (now - since) * 1000f;
                    absences++; maxAbsenceMs = Mathf.Max(maxAbsenceMs, gapMs);
                    absentSince.Remove(name);
                    Log($"'{name}' absent for {gapMs:0} ms at {pos}");
                }
                if (lastInstance.TryGetValue(name, out int prev) && prev != inst)
                {
                    swaps++;
                    Log($"'{name}' entity swapped (instance {prev} -> {inst}) at {pos}");
                }
                else if (!lastInstance.ContainsKey(name))
                    Log($"Watching '{name}' {detail}from {pos}");
                lastInstance[name] = inst;
            }
            float end = Time.realtimeSinceStartup + observeSeconds;
            float nextSample = 0f;
            while (Time.realtimeSinceStartup < end)
            {
                presentNow.Clear();
                float now = Time.realtimeSinceStartup;
                // Players and their mounts every frame: an absence is measured in frames.
                foreach (BasePlayerCharacterEntity pe in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
                {
                    if (pe == GameInstance.PlayingCharacterEntity || string.IsNullOrEmpty(pe.CharacterName)) continue;
                    Track(pe.CharacterName, pe.GetInstanceID(), pe.EntityTransform.position, now, "");
                }
                foreach (VehicleEntity ve in FindObjectsByType<VehicleEntity>(FindObjectsSortMode.None))
                {
                    BaseGameEntity driver = ve.HasDriver ? ve.GetPassenger(0) : null; // GetPassenger indexes the seat map directly
                    if (driver == null || ReferenceEquals(driver, GameInstance.PlayingCharacterEntity) || !(driver is BaseCharacterEntity dc) || string.IsNullOrEmpty(dc.CharacterName)) continue;
                    Track("mount of " + dc.CharacterName, ve.GetInstanceID(), ve.EntityTransform.position, now, $"({ve.name}) ");
                }
                // Everything else twice a second: pets, positions beyond the line, attacks, skills, missiles.
                bool sampled = Time.realtimeSinceStartup >= nextSample;
                if (sampled)
                {
                    nextSample = Time.realtimeSinceStartup + 0.5f;
                    foreach (BaseCharacterEntity e in FindObjectsByType<BaseCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (e == GameInstance.PlayingCharacterEntity) continue;
                        Vector3 pos = e.EntityTransform.position;
                        if (e is BaseMonsterCharacterEntity me && me.IsSummoned && !me.IsDead())
                        {
                            BaseCharacterEntity owner = me.SummonerEntity;
                            if (owner != null && !ReferenceEquals(owner, GameInstance.PlayingCharacterEntity) && !string.IsNullOrEmpty(owner.CharacterName))
                                Track(petPrefix + owner.CharacterName, me.GetInstanceID(), pos, now, $"({me.CharacterName}, objectId={me.ObjectId}) ");
                        }
                        string key = $"{e.GetType().Name}|{e.CharacterName}|{e.ObjectId}";
                        if (!seen.TryGetValue(key, out List<Vector3> list)) { list = new List<Vector3>(); seen[key] = list; Log($"Saw other entity '{e.CharacterName}' ({e.GetType().Name}) at {pos}"); }
                        if (pos.x < observeGhostBeyondX) continue; // still on our own server; not a ghost
                        if (list.Count == 0 || Vector3.Distance(list[list.Count - 1], pos) > 0.5f) list.Add(pos);
                        bool attacking = e.AttackComponent != null && e.AttackComponent.IsAttacking;
                        int eid = e.GetInstanceID();
                        if (attacking && attackingNow.Add(eid)) { ghostAttacks++; Log($"Ghost '{e.CharacterName}' attacks at {pos}"); }
                        else if (!attacking) attackingNow.Remove(eid);
                        bool casting = e.UseSkillComponent != null && e.UseSkillComponent.IsUsingSkill;
                        if (casting && castingNow.Add(eid)) { ghostSkills++; Log($"Ghost '{e.CharacterName}' uses a skill at {pos}"); }
                        else if (!casting) castingNow.Remove(eid);
                    }
                    foreach (MissileDamageEntity m in FindObjectsByType<MissileDamageEntity>(FindObjectsSortMode.None))
                    {
                        if (m != null && m.gameObject.activeInHierarchy && m.transform.position.x >= observeGhostBeyondX - 5f && missileIds.Add(m.GetInstanceID()))
                            Log($"Missile seen at {m.transform.position}");
                    }
                }
                foreach (string name in lastInstance.Keys)
                {
                    if (!sampled && name.StartsWith(petPrefix)) continue; // pets were not looked for this frame
                    if (!presentNow.Contains(name) && !absentSince.ContainsKey(name))
                        absentSince[name] = now;
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
            Log($"RESULT observe: ghostsSeen={ghostsSeen} (players={playerGhosts} monsters={monsterGhosts}) moved={movedGhost} migrationsDuringObserve={migrations} playerSwaps={swaps} absences={absences} maxAbsenceMs={maxAbsenceMs:0} ghostAttacks={ghostAttacks} ghostSkills={ghostSkills} missiles={missileIds.Count} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private float _deadSince = -1f;
        private int _respawnsUsed;
        private static int CountBagItems(BasePlayerCharacterEntity self)
        {
            int n = 0;
            if (self == null) return 0;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
                if (self.NonEquipItems[i].dataId != 0) n += self.NonEquipItems[i].amount;
            return n;
        }

        /// <summary>
        /// Hostile actions at the nearest OTHER player beyond the line (its ghost): melee attacks when within reach
        /// and the hostile buff skill every few seconds within its range. With a party member over there the kit
        /// must refuse both on this cell (ally) — and the owner refuses again if a hit were ever forwarded; with a
        /// stranger on a PvP map the hits and the debuff must land on the real character next door.
        /// </summary>
        private IEnumerator RunPvp(float deadline)
        {
            Phase = "pvp";
            _walkZOverride = walkTargetZ;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the pvp position"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            int enemySkillId = 0;
            for (int i = 0; i < self.Skills.Count; ++i)
                if (self.Skills[i].GetSkill() is Skill sk && sk.skillBuffType == Skill.SkillBuffType.BuffToEnemy) { enemySkillId = sk.DataId; break; }
            float attackRange = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f);
            float holdX = observeGhostBeyondX + 0.8f; // close in on a target across the line, never past the hysteresis (no handoff)
            Log($"pvp: weapon={self.EquipWeapons.rightHand.dataId} range={attackRange:0.#} enemySkill={enemySkillId} hold x<={holdX:0.#}");
            float end = Time.realtimeSinceStartup + 120f, attackUntil = -1f;
            float nextAttack = 0f, nextCast = 0f, nextLog = 0f;
            int attacks = 0, castsSent = 0, castsRefused = 0, targetHpStart = -1, minTargetHp = int.MaxValue;
            string targetName = "";
            bool hurt = false;
            while (Time.realtimeSinceStartup < end && (attackUntil < 0f || Time.realtimeSinceStartup < attackUntil))
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self == null || self.IsDead()) break;
                BasePlayerCharacterEntity target = null; float best = float.MaxValue;
                foreach (BasePlayerCharacterEntity pe in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
                {
                    if (pe == self || pe.IsDead() || string.IsNullOrEmpty(pe.CharacterName) || pe.EntityTransform.position.x < observeGhostBeyondX) continue;
                    float d = Vector3.Distance(self.EntityTransform.position, pe.EntityTransform.position);
                    if (d < best) { best = d; target = pe; }
                }
                float now = Time.realtimeSinceStartup;
                if (target != null)
                {
                    if (targetHpStart < 0 || target.CharacterName != targetName) { targetHpStart = target.CurrentHp; targetName = target.CharacterName; Log($"pvp target '{targetName}' hp={target.CurrentHp}/{target.MaxHp} party={target.PartyId} at {target.EntityTransform.position} dist={best:0.#}"); }
                    minTargetHp = Mathf.Min(minTargetHp, target.CurrentHp);
                    if (target.CurrentHp < targetHpStart) hurt = true;
                    if (best <= 8f && attackUntil < 0f) attackUntil = now + 45f;
                    attackRange = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f);
                    if (enemySkillId != 0 && best <= 8f && now >= nextCast)
                    {
                        nextCast = now + 2.5f;
                        self.SetTargetEntity(target);
                        bool sent = self.UseSkill(enemySkillId, WeaponHandlingState.None, target.ObjectId, AimPosition.CreatePosition(target.EntityTransform.position));
                        if (sent) castsSent++; else castsRefused++;
                        Log($"pvp cast {enemySkillId} on '{targetName}' sent={sent} (dist {best:0.#})");
                    }
                    if (best > attackRange)
                    {
                        if (self.EntityTransform.position.x < holdX)
                        {
                            Vector3 dest = target.EntityTransform.position; dest.x = Mathf.Min(dest.x, holdX);
                            self.PointClickMovement(dest);
                        }
                        else self.StopMove();
                    }
                    else if (now >= nextAttack)
                    {
                        nextAttack = now + 0.6f;
                        self.StopMove();
                        Vector3 toTarget = target.EntityTransform.position - self.EntityTransform.position; toTarget.y = 0f;
                        if (toTarget.sqrMagnitude > 0.001f) self.SetLookRotation(Quaternion.LookRotation(toTarget.normalized), true);
                        self.SetTargetEntity(target);
                        bool isLeftHand = false;
                        self.AimPosition = self.GetAttackAimPosition(ref isLeftHand, target.EntityTransform.position);
                        WeaponHandlingState whs = WeaponHandlingState.None;
                        self.Attack(ref whs);
                        attacks++;
                    }
                }
                if (now >= nextLog)
                {
                    nextLog = now + 5f;
                    Log($"pvp: target={(target != null ? target.CharacterName : "none beyond the line")} dist={(target != null ? best : -1f):0.#} range={attackRange:0.#} attacks={attacks} casts={castsSent}/{castsSent + castsRefused} targetHp={(target != null ? target.CurrentHp : -1)} selfHp={self.CurrentHp} selfX={self.EntityTransform.position.x:0.#}");
                }
                yield return null;
            }
            Log($"RESULT pvp: target='{targetName}' attacks={attacks} castsSent={castsSent} castsRefused={castsRefused} targetHurt={hurt} targetHp {targetHpStart}->{minTargetHp} -> {(attacks + castsSent + castsRefused > 0 ? "PASS" : "FAIL")}");
            Finished = true; Passed = attacks + castsSent + castsRefused > 0;
        }

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
            // Hold the line from whichever side the fight position is on: west of the line (the usual C fighter) or east
            // of it (a fighter standing in E, whose ghost on C is what an observer over there watches attack).
            bool east = self.EntityTransform.position.x >= observeGhostBeyondX;
            float holdX = east ? observeGhostBeyondX + 0.5f : observeGhostBeyondX - 0.5f;
            bool Beyond(float x, float margin) => east ? x < holdX - margin : x > holdX + margin;
            float attackRange = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f);
            float end = Time.realtimeSinceStartup + 150f;
            float nextAttack = 0f, nextLog = 0f;
            BaseMonsterCharacterEntity target = null;
            int targetHpStart = 0, minTargetHp = int.MaxValue, minSelfHp = selfHpStart, kills = 0;
            bool anyHurt = false;
            // Loot: drops seen (any side), pickups attempted on drops within reach, items gained in the bag.
            int itemsStart = CountBagItems(self), pickups = 0, questKillsStart = CountQuestKills(self);
            Log($"fight: party={self.PartyId} quests={self.Quests.Count} questKills={questKillsStart} skills={self.Skills.Count}");
            // Skills: an enemy-buff skill is cast on each engaged target once, an ally-buff skill on the nearest OTHER player
            // (a ghost across the line, when the bot stands over there) every few seconds. Skill outcomes are verified on
            // the servers (GhostAction logs); here we only count casts.
            int enemySkillId = 0, allySkillId = 0, casts = 0;
            for (int i = 0; i < self.Skills.Count; ++i)
            {
                if (!(self.Skills[i].GetSkill() is Skill sk)) continue;
                if (sk.skillBuffType == Skill.SkillBuffType.BuffToEnemy && enemySkillId == 0) enemySkillId = sk.DataId;
                if ((sk.skillBuffType == Skill.SkillBuffType.BuffToAlly || sk.skillBuffType == Skill.SkillBuffType.BuffToTarget) && allySkillId == 0) allySkillId = sk.DataId;
            }
            Log($"fight: enemySkill={enemySkillId} allySkill={allySkillId}");
            uint lastEnemyCastTarget = 0, lastItemCastTarget = 0; float nextAllyCast = 0f; int itemTries = 0;
            int skillItemIndex = -1;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                BaseItem bagItem = self.NonEquipItems[i].GetItem();
                if (bagItem != null && bagItem.IsSkill() && bagItem is ISkillItem) { skillItemIndex = i; break; }
            }
            Log($"fight: skillItemSlot={skillItemIndex}");
            HashSet<uint> dropsSeen = new HashSet<uint>();
            HashSet<uint> dropsTried = new HashSet<uint>();
            float nextPickup = 0f;
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
                if (target == null || Beyond(target.EntityTransform.position.x, attackRange + 0.5f))
                {
                    // Nearest alive monster we can reach without leaving our side.
                    target = null; float best = float.MaxValue;
                    foreach (BaseMonsterCharacterEntity m in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (m == null || m.IsDead() || Beyond(m.EntityTransform.position.x, attackRange + 0.5f)) continue;
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
                        dest.x = east ? Mathf.Max(dest.x, holdX) : Mathf.Min(dest.x, holdX);
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
                        // A cast is refused while another action plays (or on cooldown): latch a target only when accepted.
                        // The skill item goes first (its skill may share a cooldown with the direct cast), a few tries at most.
                        if (skillItemIndex >= 0 && lastItemCastTarget != target.ObjectId && itemTries < 5)
                        {
                            itemTries++;
                            self.ValidateSkillItemToUse(skillItemIndex, false, target.ObjectId, out _, out _, out _, out UITextKeys why);
                            bool sent = self.UseSkillItem(skillItemIndex, WeaponHandlingState.None, target.ObjectId, AimPosition.CreatePosition(target.EntityTransform.position));
                            if (sent) { lastItemCastTarget = target.ObjectId; itemTries = 0; casts++; }
                            Log($"cast skill item (slot {skillItemIndex}) on '{target.name}' sent={sent} check={why}");
                        }
                        else if (enemySkillId != 0 && lastEnemyCastTarget != target.ObjectId)
                        {
                            bool sent = self.UseSkill(enemySkillId, WeaponHandlingState.None, target.ObjectId, AimPosition.CreatePosition(target.EntityTransform.position));
                            if (sent) { lastEnemyCastTarget = target.ObjectId; casts++; }
                            Log($"cast enemy skill {enemySkillId} on '{target.name}' sent={sent}");
                        }
                        else
                            self.Attack(ref whs);
                    }
                    if (target.CurrentHp < targetHpStart) anyHurt = true;
                    minTargetHp = Mathf.Min(minTargetHp, target.CurrentHp);
                }
                else
                {
                    // Nothing within reach: bait. Stand on the line level with the nearest monster that is close enough
                    // to the line to notice our ghost over there (visual range ~5 m), so it comes to us.
                    BaseMonsterCharacterEntity bait = null; float bestBait = float.MaxValue;
                    foreach (BaseMonsterCharacterEntity m in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (m == null || m.IsDead() || Beyond(m.EntityTransform.position.x, 6f)) continue;
                        float d = Mathf.Abs(m.EntityTransform.position.z - self.EntityTransform.position.z);
                        if (d < bestBait) { bestBait = d; bait = m; }
                    }
                    Vector3 dest = self.EntityTransform.position;
                    dest.x = east ? holdX + 0.3f : holdX - 0.3f;
                    if (bait != null) dest.z = bait.EntityTransform.position.z;
                    if (Vector3.Distance(dest, self.EntityTransform.position) > 0.8f)
                        self.PointClickMovement(dest);
                }
                minSelfHp = Mathf.Min(minSelfHp, self.CurrentHp);
                if (allySkillId != 0 && Time.realtimeSinceStartup >= nextAllyCast)
                {
                    nextAllyCast = Time.realtimeSinceStartup + 5f;
                    BasePlayerCharacterEntity ally = null; float bestAlly = 12f;
                    foreach (BasePlayerCharacterEntity pe in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
                    {
                        if (pe == null || ReferenceEquals(pe, self) || pe.IsDead()) continue;
                        float d = Vector3.Distance(pe.EntityTransform.position, self.EntityTransform.position);
                        if (d < bestAlly) { bestAlly = d; ally = pe; }
                    }
                    if (ally != null)
                    {
                        self.SetTargetEntity(ally);
                        bool sent = self.UseSkill(allySkillId, WeaponHandlingState.None, ally.ObjectId, AimPosition.CreatePosition(ally.EntityTransform.position));
                        casts++;
                        Log($"cast ally skill {allySkillId} on '{ally.CharacterName}' at {ally.EntityTransform.position} (dist {bestAlly:0.#}) sent={sent}");
                    }
                }
                foreach (ItemDropEntity drop in FindObjectsByType<ItemDropEntity>(FindObjectsSortMode.None))
                {
                    if (drop == null) continue;
                    if (dropsSeen.Add(drop.ObjectId))
                        Log($"Drop seen '{drop.EntityTitle}' at {drop.EntityTransform.position} (ghost={drop.GetComponent<MeshIdentity>() != null && drop.GetComponent<MeshIdentity>().IsGhost})");
                    if (Time.realtimeSinceStartup < nextPickup || dropsTried.Contains(drop.ObjectId)) continue;
                    if (Vector3.Distance(drop.EntityTransform.position, self.EntityTransform.position) > 2.2f) continue;
                    nextPickup = Time.realtimeSinceStartup + 1f;
                    dropsTried.Add(drop.ObjectId);
                    bool sent = self.CallCmdPickup(drop.ObjectId);
                    pickups++;
                    Log($"Pickup requested on '{drop.EntityTitle}' at {drop.EntityTransform.position} sent={sent}");
                }
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
            int itemsGained = self != null ? CountBagItems(self) - itemsStart : 0;
            Log($"RESULT fight: hurtTarget={anyHurt} kills={kills} selfHp {selfHpStart}/{selfMaxHp}->{minSelfHp} (hitBack={selfHurt}) exp+{expGained} gold+{goldGained} level+{levelGained} drops={dropsSeen.Count} pickups={pickups} items+{itemsGained} questKills+{CountQuestKills(self) - questKillsStart} casts={casts} -> {(Passed ? "PASS" : "FAIL")}");
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
                        Log($"pos={pos} migrations={ClientMigrationController.CompletedCount - migrationsBefore}" + (p.Summons.Count > 0 ? $" summons={p.Summons.Count} pet={(FindOwnPet() != null ? "seen" : "missing")}" : ""));
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
