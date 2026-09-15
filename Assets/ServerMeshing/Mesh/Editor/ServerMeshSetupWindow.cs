// Server Meshing add-on — Setup window (Server Meshing ▸ Setup): mesh a map in a few clicks and check the wiring.
//
// Three panels: the world registry (create it, add a map's layout from the open scene's measured bounds and a cell
// size), a validator (registry errors, map infos and scenes registered, the add-on's subclasses on the kit's manager and
// entity prefabs, the registry reference on each, the gameplay rule, the server tick) with Fix buttons, and a
// one-click cell visualizer for the open scene. Everything it changes is an asset edit you could also do by hand;
// Docs/ServerMeshing/SETUP.md is the written version of the same steps.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public class ServerMeshSetupWindow : EditorWindow
    {
        private const string DefaultRegistryPath = "Assets/ServerMeshing/ServerMeshWorld.asset";
        private const string KitFolder = "Assets/OpenMMORPG/";           // the kit's own prefabs are not the game's: not validated
        private const string AddOnFolder = "Assets/ServerMeshing/";       // the add-on's own test prefabs: validated like the game's

        private MeshWorldRegistry _registry;
        private BaseMapInfo _mapInfo;
        private float _cellSize = 300f;
        private string _idPrefix = "";
        private Vector3 _worldOrigin;
        private bool _hasBounds;
        private Bounds _bounds;
        private string _layoutReport = "";
        private Vector2 _scroll;
        private readonly List<Check> _checks = new List<Check>();
        private static Dictionary<Type, MonoScript> s_scripts;

        private sealed class Check
        {
            public bool ok;
            public string text;
            public Action fix;
            public string fixLabel;
        }

        // Kit component type -> the add-on subclass that replaces it (same serialized fields, so a script swap keeps the data).
        private static readonly (Type kit, Type mesh)[] s_swaps =
        {
            (typeof(MapNetworkManager), typeof(MeshMapNetworkManager)),
            (typeof(LoginNetworkManager), typeof(MeshLoginNetworkManager)),
            (typeof(CentralNetworkManager), typeof(MeshCentralNetworkManager)),
            (typeof(MapSpawnNetworkManager), typeof(MeshMapSpawnNetworkManager)),
            (typeof(PlayerCharacterEntity), typeof(MeshPlayerCharacterEntity)),
            (typeof(MonsterCharacterEntity), typeof(MeshMonsterCharacterEntity)),
            (typeof(MonsterActivityComponent), typeof(MeshMonsterActivityComponent)),
            (typeof(DefaultCharacterAttackComponent), typeof(MeshCharacterAttackComponent)),
            (typeof(DefaultCharacterUseSkillComponent), typeof(MeshCharacterUseSkillComponent)),
            (typeof(HarvestableEntity), typeof(MeshHarvestableEntity)),
        };

        [MenuItem("Server Meshing/Setup")]
        public static void Open()
        {
            GetWindow<ServerMeshSetupWindow>("Server Meshing Setup");
        }

        private void OnEnable()
        {
            if (_registry == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:MeshWorldRegistry");
                if (guids.Length > 0)
                    _registry = AssetDatabase.LoadAssetAtPath<MeshWorldRegistry>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawRegistryPanel();
            EditorGUILayout.Space(12);
            DrawLayoutPanel();
            EditorGUILayout.Space(12);
            DrawValidatePanel();
            EditorGUILayout.Space(12);
            DrawScenePanel();
            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- registry

        private void DrawRegistryPanel()
        {
            EditorGUILayout.LabelField("1. World registry", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("One asset describes the whole world: every meshed map, its placement in the shared frame and its cut into cells. Every server and the client read it.", MessageType.None);
            _registry = (MeshWorldRegistry)EditorGUILayout.ObjectField("Registry", _registry, typeof(MeshWorldRegistry), false);
            if (_registry == null && GUILayout.Button("Create registry asset"))
            {
                MeshWorldRegistry created = CreateInstance<MeshWorldRegistry>();
                string dir = System.IO.Path.GetDirectoryName(DefaultRegistryPath);
                if (!AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.CreateFolder("Assets", "ServerMeshing");
                AssetDatabase.CreateAsset(created, AssetDatabase.GenerateUniqueAssetPath(DefaultRegistryPath));
                AssetDatabase.SaveAssets();
                _registry = created;
            }
            if (_registry != null)
            {
                _registry.Rebuild();
                EditorGUILayout.LabelField($"{_registry.maps.Count} map layout(s), {_registry.Cells.Count} cell(s)");
                foreach (MeshMapLayout layout in _registry.maps)
                {
                    if (layout == null) continue;
                    string shape = layout.IsExplicit ? $"{layout.cellRects.Length} explicit cells" : $"{layout.columns} x {layout.rows} cells of {layout.cellSize:0} m";
                    EditorGUILayout.LabelField($"  {layout.mapId}: {shape}, origin {layout.worldOrigin}");
                }
            }
        }

        // ---------------------------------------------------------------- layout from the open scene

        private void DrawLayoutPanel()
        {
            EditorGUILayout.LabelField("2. Add a map's layout", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Open the map's scene, pick its map info, choose a cell size and press Measure: the grid covers everything with a renderer, collider or terrain. Cells of a map share the scene's frame; World origin places the map in the shared frame (leave it at zero for a single world map, offset a second map so its cells touch the first's).", MessageType.None);
            _mapInfo = (BaseMapInfo)EditorGUILayout.ObjectField("Map info", _mapInfo, typeof(BaseMapInfo), false);
            _cellSize = EditorGUILayout.FloatField("Cell size (m)", _cellSize);
            _idPrefix = EditorGUILayout.TextField("Cell id prefix", _idPrefix);
            _worldOrigin = EditorGUILayout.Vector3Field("World origin", _worldOrigin);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Measure open scene"))
                MeasureOpenScene();
            using (new EditorGUI.DisabledScope(_registry == null || _mapInfo == null || !_hasBounds || _cellSize <= 0f))
            {
                if (GUILayout.Button("Add / replace layout"))
                    AddLayout();
            }
            EditorGUILayout.EndHorizontal();
            if (_hasBounds)
                EditorGUILayout.LabelField($"Scene bounds: x [{_bounds.min.x:0}, {_bounds.max.x:0}]  z [{_bounds.min.z:0}, {_bounds.max.z:0}]  ->  {Mathf.CeilToInt(_bounds.size.x / Mathf.Max(1f, _cellSize))} x {Mathf.CeilToInt(_bounds.size.z / Mathf.Max(1f, _cellSize))} cells");
            if (!string.IsNullOrEmpty(_layoutReport))
                EditorGUILayout.HelpBox(_layoutReport, MessageType.Info);
        }

        private void MeasureOpenScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            bool any = false;
            Bounds bounds = default;
            void Add(Bounds b)
            {
                if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
            }
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    if (terrain.terrainData != null) Add(new Bounds(terrain.transform.position + terrain.terrainData.size * 0.5f, terrain.terrainData.size));
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    if (!(renderer is ParticleSystemRenderer) && renderer.bounds.size.sqrMagnitude > 0f) Add(renderer.bounds);
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                    if (!collider.isTrigger && collider.bounds.size.sqrMagnitude > 0f) Add(collider.bounds);
            }
            _hasBounds = any;
            _bounds = bounds;
            if (_mapInfo != null && string.IsNullOrEmpty(_idPrefix))
                _idPrefix = _mapInfo.Id;
            _layoutReport = any ? "" : "Nothing measurable in the open scene (no terrain, renderer or collider).";
        }

        private void AddLayout()
        {
            float cell = _cellSize;
            Vector3 gridOrigin = new Vector3(Mathf.Floor(_bounds.min.x), 0f, Mathf.Floor(_bounds.min.z));
            int columns = Mathf.Max(1, Mathf.CeilToInt((_bounds.max.x - gridOrigin.x) / cell));
            int rows = Mathf.Max(1, Mathf.CeilToInt((_bounds.max.z - gridOrigin.z) / cell));
            string prefix = string.IsNullOrEmpty(_idPrefix) ? _mapInfo.Id : _idPrefix;
            MeshMapLayout layout = new MeshMapLayout
            {
                mapId = _mapInfo.Id, worldOrigin = _worldOrigin, gridOrigin = gridOrigin, cellSize = cell, columns = columns, rows = rows,
                cellIdFormat = prefix + "-{x}-{y}", cellIds = null, cellRects = null,
            };
            Undo.RecordObject(_registry, "Add map layout");
            int existing = _registry.maps.FindIndex(l => l != null && string.Equals(l.mapId, _mapInfo.Id));
            if (existing >= 0) _registry.maps[existing] = layout; else _registry.maps.Add(layout);
            EditorUtility.SetDirty(_registry);
            AssetDatabase.SaveAssets();
            _registry.Rebuild();
            int cells = _registry.CellsOf(_mapInfo.Id).Count;
            _layoutReport = $"{_mapInfo.Id}: {columns} x {rows} = {cells} cells of {cell:0} m from ({gridOrigin.x:0}, {gridOrigin.z:0}); ids {prefix}-0-0 … {prefix}-{columns - 1}-{rows - 1}. " +
                            "A cell id is the channel id of the map server that serves it. To make some cells smaller than others, switch the layout to explicit rectangles in the inspector.";
        }

        // ---------------------------------------------------------------- validation

        private void DrawValidatePanel()
        {
            EditorGUILayout.LabelField("3. Validate the wiring", EditorStyles.boldLabel);
            if (GUILayout.Button("Validate"))
                RunChecks();
            foreach (Check check in _checks)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(check.ok ? "OK" : "!!", GUILayout.Width(24));
                EditorGUILayout.LabelField(check.text, EditorStyles.wordWrappedLabel);
                if (!check.ok && check.fix != null && GUILayout.Button(check.fixLabel ?? "Fix", GUILayout.Width(120)))
                {
                    check.fix();
                    RunChecks();
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void RunChecks()
        {
            _checks.Clear();
            if (_registry == null)
            {
                _checks.Add(new Check { ok = false, text = "No world registry: create one in panel 1." });
                return;
            }
            // Registry consistency: errors the rebuild reports (duplicate ids, overlapping explicit cells, too many neighbours).
            List<string> errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) => { if (type == LogType.Error && message.Contains("[Mesh]")) errors.Add(message); };
            Application.logMessageReceived += capture;
            try { _registry.Rebuild(); } finally { Application.logMessageReceived -= capture; }
            _checks.Add(new Check { ok = errors.Count == 0, text = errors.Count == 0 ? $"Registry: {_registry.Cells.Count} cells, no errors." : "Registry: " + string.Join(" | ", errors) });
            if (_registry.maps.Count == 0)
                _checks.Add(new Check { ok = false, text = "Registry has no map layout: add one in panel 2." });

            // Map infos and scenes.
            List<BaseMapInfo> databaseMaps = new List<BaseMapInfo>();
            foreach (string guid in AssetDatabase.FindAssets("t:GameDatabase"))
            {
                GameDatabase database = AssetDatabase.LoadAssetAtPath<GameDatabase>(AssetDatabase.GUIDToAssetPath(guid));
                if (database == null) continue;
                SerializedProperty maps = new SerializedObject(database).FindProperty("mapInfos");
                for (int i = 0; maps != null && i < maps.arraySize; ++i)
                    if (maps.GetArrayElementAtIndex(i).objectReferenceValue is BaseMapInfo info) databaseMaps.Add(info);
            }
            foreach (MeshMapLayout layout in _registry.maps)
            {
                if (layout == null) continue;
                BaseMapInfo info = databaseMaps.Find(m => string.Equals(m.Id, layout.mapId));
                _checks.Add(new Check { ok = info != null, text = info != null ? $"Map {layout.mapId}: in the game database." : $"Map {layout.mapId}: no map info with this id in any GameDatabase asset (add it to the database's Map Infos)." });
                if (info == null) continue;
                string sceneName = info.Scene != null ? info.Scene.SceneName : null;
                bool inBuild = false;
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                    if (scene.enabled && scene.path.EndsWith("/" + sceneName + ".unity")) inBuild = true;
                _checks.Add(new Check { ok = inBuild, text = inBuild ? $"Scene {sceneName}: in the build settings." : $"Scene {sceneName}: not in the build settings (File ▸ Build Settings)." });
            }

            // Prefab wiring: the add-on's subclasses on the kit's managers and entities, and the registry reference on the managers.
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") || path.StartsWith(KitFolder)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                // The client instance carries the client halves of the login and central managers: not servers, not swapped.
                bool clientInstance = prefab.GetComponentInChildren<MMOClientInstance>(true) != null;
                foreach ((Type kit, Type mesh) in s_swaps)
                {
                    if (clientInstance && typeof(LiteNetLibManager.LiteNetLibManager).IsAssignableFrom(kit))
                        continue;
                    foreach (Component component in prefab.GetComponentsInChildren(kit, true))
                    {
                        if (component == null) continue;
                        if (!mesh.IsInstanceOfType(component))
                        {
                            string capturedPath = path; Type capturedMesh = mesh; Type capturedKit = kit;
                            _checks.Add(new Check
                            {
                                ok = false,
                                text = $"{ShortPath(path)}: {component.GetType().Name} should be {mesh.Name} (the add-on's subclass; the swap keeps every serialized field).",
                                fixLabel = "Swap script",
                                fix = () => SwapScript(capturedPath, capturedKit, capturedMesh),
                            });
                            continue;
                        }
                        SerializedProperty world = new SerializedObject(component).FindProperty("world");
                        if (world != null && world.objectReferenceValue == null)
                        {
                            string capturedPath = path; Type capturedMesh = mesh;
                            _checks.Add(new Check
                            {
                                ok = false,
                                text = $"{ShortPath(path)}: {mesh.Name} has no world registry assigned.",
                                fixLabel = "Assign registry",
                                fix = () => AssignRegistry(capturedPath, capturedMesh),
                            });
                        }
                        else
                        {
                            _checks.Add(new Check { ok = true, text = $"{ShortPath(path)}: {mesh.Name} wired." });
                        }
                    }
                }
                // Sleeping cells: the central and the spawner must agree.
                MeshCentralNetworkManager central = prefab.GetComponentInChildren<MeshCentralNetworkManager>(true);
                MeshMapSpawnNetworkManager spawner = prefab.GetComponentInChildren<MeshMapSpawnNetworkManager>(true);
                if (central != null && spawner != null && central.sleepCells != spawner.sleepCells)
                {
                    string capturedPath = path; bool wanted = central.sleepCells;
                    _checks.Add(new Check
                    {
                        ok = false,
                        text = $"{ShortPath(path)}: sleeping cells is {(central.sleepCells ? "on" : "off")} on the central but {(spawner.sleepCells ? "on" : "off")} on the map spawner; they must agree.",
                        fixLabel = "Match spawner",
                        fix = () => SetBool(capturedPath, typeof(MeshMapSpawnNetworkManager), "sleepCells", wanted),
                    });
                }
                // The game instance: gameplay rule and server tick.
                foreach (GameInstance instance in prefab.GetComponentsInChildren<GameInstance>(true))
                {
                    if (clientInstance) continue;
                    SerializedObject so = new SerializedObject(instance);
                    SerializedProperty rule = so.FindProperty("gameplayRule");
                    bool ruleOk = rule != null && rule.objectReferenceValue is MeshGameplayRule;
                    string capturedPath = path;
                    _checks.Add(new Check
                    {
                        ok = ruleOk,
                        text = ruleOk ? $"{ShortPath(path)}: gameplay rule is a MeshGameplayRule." : $"{ShortPath(path)}: gameplay rule should be a MeshGameplayRule asset (kill credit and party share across the line).",
                        fixLabel = "Assign rule",
                        fix = () => AssignGameplayRule(capturedPath),
                    });
                    SerializedProperty tick = so.FindProperty("serverTargetFrameRate");
                    if (tick != null)
                    {
                        bool tickOk = tick.intValue <= 60;
                        _checks.Add(new Check
                        {
                            ok = tickOk,
                            text = $"{ShortPath(path)}: server target frame rate {tick.intValue}" + (tickOk ? "." : " — 60 is the measured sweet spot (about 270 players per cell process; 120 halves that)."),
                            fixLabel = "Set 60",
                            fix = () => SetInt(capturedPath, typeof(GameInstance), "serverTargetFrameRate", 60),
                        });
                    }
                }
            }
        }

        private static string ShortPath(string path)
        {
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private static MonoScript ScriptOf(Type type)
        {
            if (s_scripts == null)
            {
                s_scripts = new Dictionary<Type, MonoScript>();
                foreach (string guid in AssetDatabase.FindAssets("t:MonoScript"))
                {
                    MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                    Type scriptType = script != null ? script.GetClass() : null;
                    if (scriptType != null) s_scripts[scriptType] = script;
                }
            }
            return s_scripts.TryGetValue(type, out MonoScript found) ? found : null;
        }

        private void SwapScript(string prefabPath, Type kit, Type mesh)
        {
            MonoScript script = ScriptOf(mesh);
            if (script == null) { Debug.LogError($"[Mesh] No MonoScript for {mesh.Name}"); return; }
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(kit, true))
                {
                    if (component == null || mesh.IsInstanceOfType(component)) continue;
                    SerializedObject so = new SerializedObject(component);
                    so.FindProperty("m_Script").objectReferenceValue = script;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            AssignRegistry(prefabPath, mesh);
        }

        private void AssignRegistry(string prefabPath, Type mesh)
        {
            if (_registry == null) return;
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(mesh, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty world = so.FindProperty("world");
                    if (world == null) continue;
                    world.objectReferenceValue = _registry;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private void AssignGameplayRule(string prefabPath)
        {
            string[] guids = AssetDatabase.FindAssets("t:MeshGameplayRule");
            MeshGameplayRule rule = guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<MeshGameplayRule>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
            if (rule == null)
            {
                rule = CreateInstance<MeshGameplayRule>();
                AssetDatabase.CreateAsset(rule, AssetDatabase.GenerateUniqueAssetPath("Assets/ServerMeshing/MeshGameplayRule.asset"));
                AssetDatabase.SaveAssets();
            }
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (GameInstance instance in contents.GetComponentsInChildren<GameInstance>(true))
                {
                    SerializedObject so = new SerializedObject(instance);
                    so.FindProperty("gameplayRule").objectReferenceValue = rule;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void SetBool(string prefabPath, Type componentType, string property, bool value)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(componentType, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty p = so.FindProperty(property);
                    if (p == null) continue;
                    p.boolValue = value;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void SetInt(string prefabPath, Type componentType, string property, int value)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(componentType, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty p = so.FindProperty(property);
                    if (p == null) continue;
                    p.intValue = value;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        // ---------------------------------------------------------------- scene helper

        private void DrawScenePanel()
        {
            EditorGUILayout.LabelField("4. See the cells", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_registry == null || _mapInfo == null))
            {
                if (GUILayout.Button("Add cell visualizer to the open scene"))
                {
                    ZoneDebugVisualizer existing = FindFirstObjectByType<ZoneDebugVisualizer>();
                    GameObject go = existing != null ? existing.gameObject : new GameObject("[Mesh] Cell Visualizer");
                    ZoneDebugVisualizer visualizer = existing != null ? existing : go.AddComponent<ZoneDebugVisualizer>();
                    Undo.RegisterCreatedObjectUndo(go, "Add cell visualizer");
                    visualizer.world = _registry;
                    visualizer.mapId = _mapInfo.Id;
                    EditorUtility.SetDirty(visualizer);
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                    Selection.activeGameObject = go;
                }
            }
            EditorGUILayout.HelpBox("The visualizer draws every cell of the chosen map in the Scene view (and at runtime). Play the rig from Server Meshing ▸ Launch Test Cluster, or start the servers as SETUP.md describes.", MessageType.None);
        }
    }
}
