// Server Meshing add-on — Setup window (Tools ▸ Open MMORPG ▸ Server Meshing ▸ Setup): mesh a map in a few clicks and check the wiring.
//
// Four panels: the world registry, a map's layout from the open scene's measured bounds and a cell size (with each
// map's cell mode and on-demand links, and the server instance's sleeping cells), a validator (the DISABLE_ADDRESSABLES define per build target, registry errors, every Dynamic map's cut and settings, map infos and scenes registered,
// the add-on's subclasses on the kit's manager and entity prefabs, the registry reference on each, a spawner's served
// cells, the gameplay rule, the server tick) with Fix buttons, and a one-click cell visualizer for the open scene. Everything it changes is an asset edit you could also do by hand;
// Docs/ServerMeshing/SETUP.md is the written version of the same steps.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public partial class ServerMeshSetupWindow : EditorWindow
    {
        private const string DefaultRegistryPath = "Assets/ServerMeshing/ServerMeshWorld.asset";
        private const string KitFolder = "Assets/OpenMMORPG/";           // the kit's own prefabs are not the game's: not validated
        private const string KitDemoFolder = "Assets/OpenMMORPG/Demo/";  // ...except its demo, which a game built on it runs as its own
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
        private SerializedObject _registryObject;

        private sealed class Check
        {
            public bool ok;
            /// <summary>A finding to read, not a fault: shown as "!" instead of "!!".</summary>
            public bool warning;
            public string text;
            public Action fix;
            public string fixLabel;
            /// <summary>The prefab a Swap script line is about (null on other lines): what Swap all works through.</summary>
            public string swapPath;
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

        [MenuItem("Tools/Open MMORPG/Server Meshing/Setup")]
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
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    _registry.Rebuild(); // in Play the running servers' registry holds a runtime layout: leave it
                EditorGUILayout.LabelField($"{_registry.maps.Count} map layout(s), {_registry.Cells.Count} cell(s)");
                foreach (MeshMapLayout layout in _registry.maps)
                {
                    if (layout == null) continue;
                    string shape = layout.IsExplicit ? $"{layout.cellRects.Length} explicit cells" : $"{layout.columns} x {layout.rows} cells of {layout.cellSize:0} m";
                    string dynamicCells = layout.IsDynamic ? $", Dynamic (auto split {(layout.autoSplit ? "on" : "off")}, merge {(layout.autoMerge ? "on" : "off")})" : string.Empty;
                    EditorGUILayout.LabelField($"  {layout.mapId}: {shape}, origin {layout.worldOrigin}{dynamicCells}");
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
            using (new EditorGUI.DisabledScope(_registry == null || _mapInfo == null || !_hasBounds || _cellSize <= 0f || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Add / replace layout"))
                    AddLayout();
            }
            EditorGUILayout.EndHorizontal();
            if (_hasBounds)
                EditorGUILayout.LabelField($"Scene bounds: x [{_bounds.min.x:0}, {_bounds.max.x:0}]  z [{_bounds.min.z:0}, {_bounds.max.z:0}]  ->  {Mathf.CeilToInt(_bounds.size.x / Mathf.Max(1f, _cellSize))} x {Mathf.CeilToInt(_bounds.size.z / Mathf.Max(1f, _cellSize))} cells");
            if (!string.IsNullOrEmpty(_layoutReport))
                EditorGUILayout.HelpBox(_layoutReport, MessageType.Info);
            DrawCellModes();
        }

        /// <summary>Each map's cell mode: Static (the cut is the cut) or Dynamic (the cut gives base cells the central may split).</summary>
        private void DrawCellModes()
        {
            if (_registry == null || _registry.maps.Count == 0)
                return;
            if (_registryObject == null || _registryObject.targetObject != _registry)
                _registryObject = new SerializedObject(_registry);
            _registryObject.Update();
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Cell mode per map", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox("Static: the cut above is the cut, as it always was. Dynamic: the cut gives the base cells; the central may split a busy one into more cells and merge them back, and every server of the map waits for the central's layout before handing anything over.", MessageType.None);
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            if (playing)
                EditorGUILayout.HelpBox("The cell mode is read when the servers start; stop Play to change it.", MessageType.Info);
            using (new EditorGUI.DisabledScope(playing))
            {
                bool anyDynamic = false;
                SerializedProperty maps = _registryObject.FindProperty(nameof(MeshWorldRegistry.maps));
                for (int i = 0; i < maps.arraySize; ++i)
                {
                    SerializedProperty layout = maps.GetArrayElementAtIndex(i);
                    SerializedProperty cellMode = layout.FindPropertyRelative(nameof(MeshMapLayout.cellMode));
                    string mapId = layout.FindPropertyRelative(nameof(MeshMapLayout.mapId)).stringValue;
                    EditorGUILayout.PropertyField(cellMode, new GUIContent(string.IsNullOrEmpty(mapId) ? $"Map {i}" : mapId));
                    EditorGUI.indentLevel++;
                    // Static or Dynamic alike; past about 64 live cells on a map it is what lifts the link ceiling (SETUP §6a.2).
                    EditorGUILayout.PropertyField(layout.FindPropertyRelative(nameof(MeshMapLayout.onDemandLinks)),
                        new GUIContent("On-demand links", "Each cell links only to the cells it needs, instead of to every cell of the map. Needed past about 64 live cells on a map."));
                    if (cellMode.intValue == (int)MeshCellMode.Dynamic)
                    {
                        anyDynamic = true;
                        EditorGUILayout.PropertyField(layout.FindPropertyRelative(nameof(MeshMapLayout.minCellSize)));
                        EditorGUILayout.PropertyField(layout.FindPropertyRelative(nameof(MeshMapLayout.maxCellsPerBase)));
                        EditorGUILayout.PropertyField(layout.FindPropertyRelative(nameof(MeshMapLayout.autoSplit)));
                        EditorGUILayout.PropertyField(layout.FindPropertyRelative(nameof(MeshMapLayout.autoMerge)));
                    }
                    EditorGUI.indentLevel--;
                }
                if (anyDynamic)
                    EditorGUILayout.PropertyField(_registryObject.FindProperty(nameof(MeshWorldRegistry.dynamicSettings)), true);
            }
            if (_registryObject.ApplyModifiedProperties())
                AssetDatabase.SaveAssetIfDirty(_registry);
            DrawSleepingCells();
        }

        /// <summary>
        /// Sleeping cells, on each server instance's swapped central and its map spawner together (they must agree). The
        /// fields only exist on the add-on's subclasses, so before Validate's swap there is nothing to edit.
        /// </summary>
        private void DrawSleepingCells()
        {
            if (_serverInstances == null)
                _serverInstances = FindServerInstances();
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Sleeping cells", EditorStyles.miniBoldLabel);
            if (_serverInstances.Count == 0)
            {
                EditorGUILayout.HelpBox("No server instance has the add-on's MeshCentralNetworkManager yet: press Validate in panel 3 and swap the server instance's CentralNetworkManager, then set sleeping cells here.", MessageType.None);
                return;
            }
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(playing))
            {
                foreach (ServerInstanceRef instance in _serverInstances)
                {
                    if (instance.central == null) { _serverInstances = null; GUIUtility.ExitGUI(); }
                    SerializedObject central = new SerializedObject(instance.central);
                    SerializedProperty sleep = central.FindProperty(nameof(MeshCentralNetworkManager.sleepCells));
                    EditorGUILayout.LabelField(ShortPath(instance.path), EditorStyles.label);
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(sleep, new GUIContent("Sleep cells", "Cells start when a player comes near and exit when nobody needs them. Off: every cell runs from boot."));
                    using (new EditorGUI.DisabledScope(!sleep.boolValue))
                    {
                        EditorGUILayout.PropertyField(central.FindProperty(nameof(MeshCentralNetworkManager.awakeRings)), new GUIContent("Awake rings", "Rings of neighbours kept running around any cell with a player."));
                        EditorGUILayout.PropertyField(central.FindProperty(nameof(MeshCentralNetworkManager.sleepAfterSeconds)), new GUIContent("Sleep after (s)"));
                    }
                    EditorGUI.indentLevel--;
                    if (EditorGUI.EndChangeCheck())
                    {
                        central.ApplyModifiedProperties();
                        // The spawner follows the central: the two must agree (Validate reports a mismatch otherwise).
                        if (instance.spawner != null)
                        {
                            SerializedObject spawner = new SerializedObject(instance.spawner);
                            spawner.FindProperty(nameof(MeshMapSpawnNetworkManager.sleepCells)).boolValue = sleep.boolValue;
                            spawner.ApplyModifiedProperties();
                        }
                        AssetDatabase.SaveAssets();
                    }
                }
            }
        }

        private sealed class ServerInstanceRef
        {
            public string path;
            public MeshCentralNetworkManager central;
            public MeshMapSpawnNetworkManager spawner;
        }

        // Found once and again after Validate or a window focus, not every repaint (a scan loads every prefab).
        private List<ServerInstanceRef> _serverInstances;

        private void OnFocus() { _serverInstances = null; }

        private static List<ServerInstanceRef> FindServerInstances()
        {
            List<ServerInstanceRef> found = new List<ServerInstanceRef>();
            foreach (string path in GamePrefabPaths())
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                MeshCentralNetworkManager central = prefab != null ? prefab.GetComponentInChildren<MeshCentralNetworkManager>(true) : null;
                if (central == null || prefab.GetComponentInChildren<MMOClientInstance>(true) != null) continue;
                found.Add(new ServerInstanceRef { path = path, central = central, spawner = prefab.GetComponentInChildren<MeshMapSpawnNetworkManager>(true) });
            }
            return found;
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
            if (existing >= 0)
            {
                layout.CopyModeFrom(_registry.maps[existing]); // a new cut keeps the map's cell mode
                _registry.maps[existing] = layout;
            }
            else
                _registry.maps.Add(layout);
            EditorUtility.SetDirty(_registry);
            AssetDatabase.SaveAssetIfDirty(_registry);
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                _registry.Rebuild();
            int cells = _registry.CellsOf(_mapInfo.Id).Count;
            _layoutReport = $"{_mapInfo.Id}: {columns} x {rows} = {cells} cells of {cell:0} m from ({gridOrigin.x:0}, {gridOrigin.z:0}); ids {prefix}-0-0 … {prefix}-{columns - 1}-{rows - 1}. " +
                            "A cell id is the channel id of the map server that serves it. To make some cells smaller than others, switch the layout to explicit rectangles in the inspector.";
        }

        // ---------------------------------------------------------------- validation

        private void DrawValidatePanel()
        {
            EditorGUILayout.LabelField("3. Validate the wiring", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode)) // the checks rebuild the registry
            {
                if (GUILayout.Button("Validate"))
                    RunChecks();
                int swaps = 0;
                HashSet<string> swapPrefabs = new HashSet<string>();
                foreach (Check check in _checks)
                {
                    if (check.swapPath == null) continue;
                    ++swaps;
                    swapPrefabs.Add(check.swapPath);
                }
                if (swaps > 0 && GUILayout.Button($"Swap all scripts ({swaps} on {swapPrefabs.Count} prefab{(swapPrefabs.Count == 1 ? "" : "s")})")
                    && EditorUtility.DisplayDialog("Swap all scripts",
                        $"Swap {swaps} kit component(s) for the add-on's subclasses on {swapPrefabs.Count} prefab(s), and assign the world registry on each? Every serialized field is kept. Prefab edits are not undoable: commit or back up first.",
                        "Swap all", "Cancel"))
                {
                    SwapAll(swapPrefabs);
                    RunChecks();
                    GUIUtility.ExitGUI();
                }
            }
            foreach (Check check in _checks)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(check.ok ? "OK" : check.warning ? "!" : "!!", GUILayout.Width(24));
                EditorGUILayout.LabelField(check.text, EditorStyles.wordWrappedLabel);
                if (!check.ok && check.fix != null && !EditorApplication.isPlayingOrWillChangePlaymode && GUILayout.Button(check.fixLabel ?? "Fix", GUILayout.Width(120)))
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
            _serverInstances = null;   // a swap may have just made one
            AddDefineChecks();         // registry or not: without the define the add-on does not compile for that target
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
            AddDynamicChecks();

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
            foreach (string path in GamePrefabPaths())
            {
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
                                swapPath = path,
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
                AddCrossingChecks(path, prefab);
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
                        bool low = MeshCrossingSetupChecks.RateIsLow(tick.intValue);
                        bool tickOk = !low && tick.intValue <= 60;
                        _checks.Add(new Check
                        {
                            ok = tickOk,
                            warning = low,
                            text = low ? $"{ShortPath(path)}: {MeshCrossingSetupChecks.LowFrameRateText(tick.intValue)}" : $"{ShortPath(path)}: server target frame rate {tick.intValue}" + (tickOk ? "." : " — 60 is the measured sweet spot (about 270 players per cell process; 120 halves that)."),
                            fixLabel = "Set 60",
                            fix = () => SetInt(capturedPath, typeof(GameInstance), "serverTargetFrameRate", 60),
                        });
                    }
                }
            }
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
            EditorGUILayout.HelpBox("The visualizer draws every cell of the chosen map in the Scene view (and at runtime). Start the servers as SETUP.md describes.", MessageType.None);
        }
    }
}
