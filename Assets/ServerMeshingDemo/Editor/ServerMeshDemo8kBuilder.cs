// Server Meshing test rig — a copy of the kit demo's init scene (Assets/OpenMMORPG/Demo) whose new characters start on the
// 8 km test map. The copy is one scene, ServerMeshingDemo/DemoMesh8kInit, and one gameplay rule asset; it uses the demo's
// prefabs, database and home scene as they are, and no demo scene changes.
//
// The scene is 00Init with these differences, all held in the scene (prefab overrides and added objects):
//   - the game instance's rule is DemoMesh8k-GameplayRule, a MeshCombatGameplayRule (the demo's combat rules plus kill
//     credit across a cell edge), copied from the demo's rule, and its day/night clock is DemoMesh8k-Daytime, held at noon;
//   - the server instance runs sleeping cells (one ring), its map spawner starts only the 8 km map's cells, headless, from
//     Build/DemoMesh8kCells, and the database is a file of its own (the demo's characters stay out of it);
//   - DemoMesh8kMapInfo registers the 8 km map info at load (the demo's database does not list it);
//   - DemoMesh8kStartMap, under the server instance's login manager (which creates characters), starts every new
//     character on the 8 km map.
// The add-on's subclasses go on the demo prefabs the copy runs (server instance, map network manager, player entities),
// as Setup's Swap all would; on a map without a layout they behave as the kit's.
//
// Map servers boot the build's first scene, so the copy has builds of its own: Build 8 km Demo Cells and Player puts
// DemoMesh8kInit, 01Home and the 8 km map into Build/DemoMesh8kCells, a Server build for the map servers, and into
// Build/DemoMesh8k, a player for headless bots (DESIGN 1.32). A rerun refreshes the scene from 00Init in place.
//
// Reached from the menu and from batch mode (-executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshDemo8kBuilder.BuildFromCommandLine).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>ServerMeshDemo8kBuilder makes the demo copy's init scene and builds its player.</summary>
    public static class ServerMeshDemo8kBuilder
    {
        public const string SourceFolder = "Assets/OpenMMORPG/Demo";
        public const string SourceInitScene = SourceFolder + "/Scenes/00Init.unity";
        public const string SourceRule = SourceFolder + "/GameData/GameplayRule.asset";
        public const string HomeScene = SourceFolder + "/Scenes/01Home.unity";
        public const string MapNetworkManagerPrefab = SourceFolder + "/Prefabs/MapNetworkManager.prefab";

        public const string Folder = "Assets/ServerMeshingDemo";
        public const string InitScene = Folder + "/DemoMesh8kInit.unity";
        public const string RulePath = Folder + "/DemoMesh8k-GameplayRule.asset";
        /// <summary>DaytimePath is the copy's day/night clock: a FixedTimeOfDayUpdater held at DaytimeHour.</summary>
        public const string DaytimePath = Folder + "/DemoMesh8k-Daytime.asset";
        public const float DaytimeHour = 12f;
        /// <summary>RuleScriptGuid is MeshCombatGameplayRule.cs's guid (fixed in its .meta).</summary>
        public const string RuleScriptGuid = "ebc5d3a258104008bd81e9d8d7d761de";
        /// <summary>DbFile is the copy's SQLite file, beside the project in the editor and beside the exe in a build.</summary>
        public const string DbFile = "./mmorpg-demomesh8k.sqlite3";
        public const string BuildFolder = "Build/DemoMesh8k";
        public const string CellsBuildFolder = "Build/DemoMesh8kCells";
        public const string Il2cppBuildFolder = "Build/DemoMesh8kIl2cpp";
        /// <summary>AwakeRings: one ring of 1,024 m cells is a full cell ahead of a player in any direction.</summary>
        public const int AwakeRings = 1;
        /// <summary>
        /// The cells' frame rate (the kit's GameInstance.serverTargetFrameRate, 30 by default). A crossing's gap is a few
        /// server frames (enter-game, ready, the spawn's sync tick), so 60 halves it (DESIGN 1.05); the copy's scene
        /// overrides it, the kit's prefab and the rig keep 30.
        /// </summary>
        public const int ServerFrameRate = 60;
        /// <summary>
        /// The map network manager's network tick (LiteNetLibManager.updateFps, 20 by default), on the cells and on the
        /// client alike: the spawn of a crossing's character waits for the next server tick and its client's first state
        /// for the next client tick, up to 50 ms each at 20 (DESIGN 1.12). The copy's scene overrides it; the kit's prefab,
        /// the login, central and database managers, and the rig keep 20.
        /// </summary>
        public const int NetworkUpdateFps = 60;
        public const string MapInfoObject = "[Mesh] 8 km map info";
        public const string StartMapObject = "[Mesh] 8 km start map";

        private static readonly (Type kit, Type mesh)[] Swaps =
        {
            (typeof(MapNetworkManager), typeof(MeshMapNetworkManager)),
            (typeof(LoginNetworkManager), typeof(MeshLoginNetworkManager)),
            (typeof(CentralNetworkManager), typeof(MeshCentralNetworkManager)),
            (typeof(MapSpawnNetworkManager), typeof(MeshMapSpawnNetworkManager)),
            (typeof(PlayerCharacterEntity), typeof(MeshPlayerCharacterEntity)),
            (typeof(DefaultCharacterAttackComponent), typeof(MeshCharacterAttackComponent)),
            (typeof(DefaultCharacterUseSkillComponent), typeof(MeshCharacterUseSkillComponent)),
            (typeof(DefaultCharacterChargeComponent), typeof(MeshCharacterChargeComponent)),
        };

        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build 8 km Demo Copy")]
        public static void Menu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Build();
        }

        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build 8 km Demo Cells and Player")]
        public static void MenuCellsAndPlayer()
        {
            string result = BuildCellsAndPlayer();
            if (result.StartsWith("ok", StringComparison.Ordinal))
                Debug.Log("Demo copy: cells and player " + result);
            else
                Debug.LogError("Demo copy error: cells and player " + result);
        }

        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build 8 km Demo IL2CPP Cells (Build/DemoMesh8kIl2cpp)")]
        public static void MenuIl2cppCells()
        {
            string result = BuildIl2cppCells();
            if (result.StartsWith("ok", StringComparison.Ordinal))
                Debug.Log("Demo copy: IL2CPP cells " + result);
            else
                Debug.LogError("Demo copy error: IL2CPP cells " + result);
        }

        /// <summary>BuildFromCommandLine is the batch-mode entry point: the copy, then its cells and player. Exits 0 on success.</summary>
        public static void BuildFromCommandLine()
        {
            bool ok;
            try
            {
                ok = Build();
                if (ok)
                {
                    string result = BuildCellsAndPlayer();
                    Debug.Log("Demo copy: cells and player " + result);
                    ok = result.StartsWith("ok", StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Demo copy error: " + ex);
                ok = false;
            }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Build makes or refreshes the copy and leaves its scene open. It logs one Demo copy line per step.</summary>
        public static bool Build()
        {
            MeshWorldRegistry world = AssetDatabase.LoadAssetAtPath<MeshWorldRegistry>(ServerMeshLargeMapBuilder.WorldPath);
            MapInfo mapInfo = AssetDatabase.LoadAssetAtPath<MapInfo>(ServerMeshLargeMapBuilder.MapInfoPath);
            if (world == null || mapInfo == null || !world.HasLayout(ServerMeshLargeMapBuilder.MapId))
            {
                Debug.LogError("Demo copy error: needs the world registry and the 8 km map (Build 8 km Test Map first)");
                return false;
            }
            EnsureFolder(Folder);

            // 1. The rule: the demo's, on the mesh subclass (the swap keeps every serialized field).
            if (!CopyRule())
                return false;

            // 2. The scene: a fresh copy of 00Init (a rerun keeps the copy's guid).
            if (File.Exists(InitScene))
                File.Copy(SourceInitScene, InitScene, true);
            else
                AssetDatabase.CopyAsset(SourceInitScene, InitScene);
            AssetDatabase.ImportAsset(InitScene, ImportAssetOptions.ForceUpdate);
            Scene scene = EditorSceneManager.OpenScene(InitScene, OpenSceneMode.Single);
            GameInstance gameInstance = Find<GameInstance>(scene);
            MeshCentralNetworkManager central;

            // 3. The add-on's subclasses on the demo prefabs the copy runs, and the registry on them.
            List<string> prefabs = new List<string>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                if (!string.IsNullOrEmpty(source) && root.GetComponentInChildren<MMOClientInstance>(true) == null && root.GetComponentInChildren<GameInstance>(true) == null)
                    prefabs.Add(source); // the server instance and the map network manager
            }
            if (gameInstance != null)
            {
                SerializedObject so = new SerializedObject(gameInstance);
                GameDatabase database = so.FindProperty("gameDatabase").objectReferenceValue as GameDatabase;
                if (database != null)
                    prefabs.AddRange(database.playerCharacterEntities.Where(e => e != null).Select(e => AssetDatabase.GetAssetPath(e)));
            }
            prefabs = prefabs.Distinct().ToList();
            foreach (string path in prefabs)
                Edit(path, contents => SwapScripts(contents));
            foreach (string path in prefabs)
                Edit(path, contents => AssignRegistry(contents, world));
            // The prefab changes reach the open scene's instances on a reload.
            scene = EditorSceneManager.OpenScene(InitScene, OpenSceneMode.Single);
            // Opening a scene unloads unreferenced assets: what is assigned below is loaded after it.
            MeshCombatGameplayRule rule = AssetDatabase.LoadAssetAtPath<MeshCombatGameplayRule>(RulePath);
            mapInfo = AssetDatabase.LoadAssetAtPath<MapInfo>(ServerMeshLargeMapBuilder.MapInfoPath);
            gameInstance = Find<GameInstance>(scene);
            central = Find<MeshCentralNetworkManager>(scene);
            bool wired = gameInstance != null && central != null;
            if (!wired)
                Debug.LogError("Demo copy error: 00Init has no game instance or no central server");
            foreach (string path in prefabs)
                wired &= CheckSwapped(AssetDatabase.LoadAssetAtPath<GameObject>(path), path);
            Debug.Log($"Demo copy: add-on subclasses on {string.Join(", ", prefabs.Select(Path.GetFileNameWithoutExtension))}");
            if (!wired)
                return false;

            // 4. The scene's own settings: the rule, the server instance, the map info and the start map.
            SerializedObject instance = new SerializedObject(gameInstance);
            instance.FindProperty("gameplayRule").objectReferenceValue = rule;
            instance.FindProperty("dayNightTimeUpdater").objectReferenceValue = EnsureDaytime();
            instance.FindProperty("serverTargetFrameRate").intValue = ServerFrameRate;
            instance.ApplyModifiedPropertiesWithoutUndo();
            ConfigureServer(central.transform.root.gameObject, mapInfo);

            GameObject mapInfoGo = new GameObject(MapInfoObject);
            mapInfoGo.AddComponent<DemoMesh8kMapInfo>().mapInfos = new BaseMapInfo[] { mapInfo };
            SceneManager.MoveGameObjectToScene(mapInfoGo, scene);
            GameObject startMapGo = new GameObject(StartMapObject);
            // The login server creates characters and takes the first data manager among its children.
            startMapGo.transform.SetParent(central.transform.root.GetComponentInChildren<MeshLoginNetworkManager>(true).transform, false);
            startMapGo.AddComponent<DemoMesh8kStartMap>().startMap = mapInfo;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"Demo copy: {InitScene}: rule {RulePath}, new characters start on {ServerMeshLargeMapBuilder.MapId}, " +
                      $"cells on demand ({AwakeRings} ring) from {CellsExeRelative()}, database {DbFile}");

            EnableInBuildSettings(ServerMeshLargeMapBuilder.ScenePath);
            EnableInBuildSettings(HomeScene);
            AssetDatabase.SaveAssets();
            if (!File.Exists(CellsExeRelative()))
                Debug.LogWarning($"Demo copy: no cells at {CellsExeRelative()} yet; run Build 8 km Demo Cells and Player before pressing Play");
            return true;
        }

        /// <summary>BuildCellsAndPlayer builds what a run needs, cells first: "ok ..." or the first "error: ...".</summary>
        public static string BuildCellsAndPlayer()
        {
            string cells = BuildCells();
            if (!cells.StartsWith("ok", StringComparison.Ordinal))
                return cells;
            string player = BuildPlayer();
            return player.StartsWith("ok", StringComparison.Ordinal) ? cells + "; " + player : player;
        }

        /// <summary>
        /// BuildCells builds the map servers into Build/DemoMesh8kCells; "ok ..." or "error: ...". The Server subtarget
        /// costs a cell about half a player build's CPU (DESIGN 1.31).
        /// </summary>
        public static string BuildCells()
        {
            return BuildExe(CellsExeRelative(), StandaloneBuildSubtarget.Server);
        }

        /// <summary>BuildPlayer builds the copy's player, which headless bots run from, into Build/DemoMesh8k; "ok ..." or "error: ...".</summary>
        public static string BuildPlayer()
        {
            return BuildExe(ExeRelative(), StandaloneBuildSubtarget.Player); // a bot needs the client code the Server subtarget strips
        }

        /// <summary>
        /// BuildIl2cppCells builds the same scenes into Build/DemoMesh8kIl2cpp as map servers with no JIT (DESIGN 1.31):
        /// "ok ..." or "error: ...". It is the Server subtarget because this Unity install's Windows support has IL2CPP
        /// only there, so it runs no bot, and it needs Visual Studio's C++ tools and a Windows SDK. Only cells run from
        /// it: a map spawner needs the Mono build. The Server target's scripting backend goes back to Mono afterwards.
        /// </summary>
        public static string BuildIl2cppCells()
        {
            NamedBuildTarget server = NamedBuildTarget.Server;
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(server);
            PlayerSettings.SetScriptingBackend(server, ScriptingImplementation.IL2CPP);
            try
            {
                return BuildExe(Il2cppBuildFolder + "/" + Application.productName + ".exe", StandaloneBuildSubtarget.Server);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(server, backend);
            }
        }

        private static string BuildExe(string exe, StandaloneBuildSubtarget subtarget)
        {
            if (!File.Exists(InitScene))
                return "error: no copy yet (Build 8 km Demo Copy first)";
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { InitScene, HomeScene, ServerMeshLargeMapBuilder.ScenePath },
                locationPathName = Path.GetFullPath(exe),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)subtarget,
                options = BuildOptions.None,
            };
            BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
            if (summary.result != BuildResult.Succeeded)
                return $"error: build {summary.result} with {summary.totalErrors} error(s)";
            return $"ok {summary.totalSize / (1024 * 1024)} MB in {summary.totalTime.TotalSeconds:0} s at {exe}";
        }

        /// <summary>ExeRelative is the copy's player, relative to the project (the editor's working directory).</summary>
        public static string ExeRelative()
        {
            return BuildFolder + "/" + Application.productName + ".exe";
        }

        /// <summary>CellsExeRelative is the copy's map servers, relative to the project (the editor's working directory).</summary>
        public static string CellsExeRelative()
        {
            return CellsBuildFolder + "/" + Application.productName + ".exe";
        }

        // ---------------------------------------------------------------- rule and clock

        /// <summary>EnsureDaytime makes or refreshes the copy's clock: always DaytimeHour, so the demo's sky stays in daylight.</summary>
        private static FixedTimeOfDayUpdater EnsureDaytime()
        {
            FixedTimeOfDayUpdater clock = AssetDatabase.LoadAssetAtPath<FixedTimeOfDayUpdater>(DaytimePath);
            if (clock == null)
            {
                clock = ScriptableObject.CreateInstance<FixedTimeOfDayUpdater>();
                AssetDatabase.CreateAsset(clock, DaytimePath);
            }
            clock.hour = DaytimeHour;
            EditorUtility.SetDirty(clock);
            return clock;
        }

        private static bool CopyRule()
        {
            ScriptableObject source = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SourceRule);
            if (source == null || AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(RuleScriptGuid)) == null)
            {
                Debug.LogError("Demo copy error: needs " + SourceRule + " and a compiled MeshCombatGameplayRule.cs");
                return false;
            }
            string sourceScript = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(source)));
            if (File.Exists(RulePath))
                File.Copy(SourceRule, RulePath, true);
            else
                AssetDatabase.CopyAsset(SourceRule, RulePath);
            File.WriteAllText(RulePath, File.ReadAllText(RulePath).Replace("guid: " + sourceScript, "guid: " + RuleScriptGuid));
            AssetDatabase.ImportAsset(RulePath, ImportAssetOptions.ForceUpdate);
            if (AssetDatabase.LoadAssetAtPath<MeshCombatGameplayRule>(RulePath) == null)
            {
                Debug.LogError("Demo copy error: the copied rule is not a MeshCombatGameplayRule: " + RulePath);
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- wiring

        private static T Find<T>(Scene scene) where T : Component
        {
            return scene.GetRootGameObjects().Select(r => r.GetComponentInChildren<T>(true)).FirstOrDefault(c => c != null);
        }

        private static void Edit(string prefabPath, Func<GameObject, bool> edit)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                if (edit(contents))
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        /// <summary>The components of a type a prefab owns: not inherited from another prefab.</summary>
        private static IEnumerable<Component> Owned(GameObject root, Type type)
        {
            return root.GetComponentsInChildren(type, true).Where(c => c != null && PrefabUtility.GetCorrespondingObjectFromSource(c) == null);
        }

        private static bool SwapScripts(GameObject root)
        {
            bool changed = false;
            foreach ((Type kit, Type mesh) in Swaps)
            {
                MonoScript script = ScriptOf(mesh);
                foreach (Component component in Owned(root, kit).Where(c => !mesh.IsInstanceOfType(c)).ToList())
                {
                    SerializedObject so = new SerializedObject(component);
                    so.FindProperty("m_Script").objectReferenceValue = script;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }
            }
            return changed;
        }

        private static bool AssignRegistry(GameObject root, MeshWorldRegistry world)
        {
            bool changed = false;
            foreach ((Type kit, Type mesh) in Swaps)
            {
                foreach (Component component in Owned(root, mesh))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty property = so.FindProperty("world");
                    if (property == null || property.objectReferenceValue == world) continue;
                    property.objectReferenceValue = world;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }
            }
            return changed;
        }

        private static bool CheckSwapped(GameObject root, string where)
        {
            bool ok = true;
            foreach ((Type kit, Type mesh) in Swaps)
            {
                foreach (Component component in root.GetComponentsInChildren(kit, true))
                {
                    if (component == null || mesh.IsInstanceOfType(component)) continue;
                    Debug.LogError($"Demo copy error: {where}: {component.GetType().Name} on {component.name} is not a {mesh.Name} (it comes from another prefab)");
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>The server instance's settings, as overrides on the scene's instance (SerializedObject records them).</summary>
        private static void ConfigureServer(GameObject root, MapInfo mapInfo)
        {
            // Character puppets take their pose from the snapshots (DESIGN 1.09): a steered one dashed to each snapshot and
            // stood until the next, so a character just across a line moved in stop-go bursts. Dynamic maps already default
            // to it; the 8 km map is Static, so the copy sets it.
            foreach (MeshMapNetworkManager map in root.GetComponentsInChildren<MeshMapNetworkManager>(true))
            {
                SerializedObject so = new SerializedObject(map);
                so.FindProperty("puppetMotion").enumValueIndex = (int)MeshPuppetMotion.Kinematic;
                so.FindProperty("updateFps").intValue = NetworkUpdateFps;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (MeshCentralNetworkManager central in root.GetComponentsInChildren<MeshCentralNetworkManager>(true))
            {
                SerializedObject so = new SerializedObject(central);
                so.FindProperty("sleepCells").boolValue = true;
                so.FindProperty("awakeRings").intValue = AwakeRings;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (MeshMapSpawnNetworkManager spawner in root.GetComponentsInChildren<MeshMapSpawnNetworkManager>(true))
            {
                SerializedObject so = new SerializedObject(spawner);
                so.FindProperty("sleepCells").boolValue = true;
                // Map servers start headless (-batchmode -nographics: no window, no rendering), from the editor too: the
                // demo's prefab starts them windowed in the editor, a rendering window per cell.
                so.FindProperty("notSpawnInBatchMode").boolValue = false;
                so.FindProperty("editorNotSpawnInBatchMode").boolValue = false;
                so.FindProperty("spawnExePath").stringValue = "./" + Application.productName + ".exe";
                so.FindProperty("isOverrideExePath").boolValue = true;
                so.FindProperty("overrideExePath").stringValue = "./" + CellsExeRelative();
                SerializedProperty maps = so.FindProperty("spawningMaps");
                maps.arraySize = 1;
                maps.GetArrayElementAtIndex(0).objectReferenceValue = mapInfo;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            // The SQLite database by its type name: SQLiteDatabase compiles only for standalone targets.
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.GetType().Name != "SQLiteDatabase") continue;
                SerializedObject so = new SerializedObject(behaviour);
                so.FindProperty("dbPath").stringValue = DbFile;
                so.FindProperty("editorDbPath").stringValue = DbFile;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static Dictionary<Type, MonoScript> s_scripts;

        private static MonoScript ScriptOf(Type type)
        {
            if (s_scripts == null)
            {
                s_scripts = new Dictionary<Type, MonoScript>();
                foreach (string guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/ServerMeshing/Mesh" }))
                {
                    MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                    Type scriptType = script != null ? script.GetClass() : null;
                    if (scriptType != null)
                        s_scripts[scriptType] = script;
                }
            }
            if (s_scripts.TryGetValue(type, out MonoScript found))
                return found;
            throw new InvalidOperationException("No MonoScript for " + type.FullName);
        }

        // ---------------------------------------------------------------- project

        private static void EnableInBuildSettings(string scenePath)
        {
            // In the editor a scene loads by name only when it is in the build settings and enabled.
            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
            int at = scenes.FindIndex(s => s.path == scenePath);
            if (at >= 0 && scenes[at].enabled)
                return;
            if (at >= 0)
                scenes[at] = new EditorBuildSettingsScene(scenePath, true);
            else
                scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log($"Demo copy: {scenePath} enabled in the build settings");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
