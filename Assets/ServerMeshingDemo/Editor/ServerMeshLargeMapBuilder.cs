// Server Meshing test rig — builds the 8 km test map, everything of it under ServerMeshingDemo/ (the builder, the scene
// and its terrain and navmesh, the map info): a flat 8,192 m Unity Terrain with every cell edge painted on it, monster and
// harvestable spawn areas spread as a game's land would be, a navmesh baked off the terrain collider, its map info,
// and its 8 x 8 layout of 1,024 m cells in the world registry. The layout sits 20 km north of the other test maps in the
// shared frame, so it touches neither and has no seams.
//
// New characters start StartEdgeDistance inside cell (4,4), facing its edge with cell (3,4). A see-through curtain in the
// edge colour stands over every painted cell edge, so the borders read from afar.
//
// The scene has no camera: the player controller brings its own (a scene camera would draw over it). Its light, sky and
// fog are the kit demo's (DemoMap's directional light, its DayNightSkyCycle and its lighting settings), so the map looks
// and reads like the demo's; without the demo it gets a plain directional light.
//
// Its spawns are the demo's, spread over the map from a seed: each area takes one of DemoMap's spawn areas (what, level
// range, group size, radius). Slow noise makes stretches of land busy or quiet, so some cells are crowded and some nearly
// empty, and a busy stretch has more areas and bigger ones. Nothing is kept off a cell edge: camps stand on the lines, as
// they would in a game whose designers never think about cells. Monsters get harder with distance from the start, and
// ClearRadius around the start stays empty, as around a first town, and the land around that is never quiet. A herd stands
// in sight across the cell edge and a row of harvestables stands on it, so a new character sees the map's life from where
// it starts and can harvest across a line within seconds. Mushrooms and iron, which DemoMap spawns none of, join the
// harvestables. They spawn meshed copies of the demo's prefabs, made
// under Monsters/ and Harvestables/: the add-on's subclasses swapped in (MeshMonsterCharacterEntity,
// MeshCharacterAttackComponent, mesh versions of the demo's line-of-sight AI and interrupt-chance skills,
// MeshHarvestableEntity), each with a network asset id of its own. The demo's prefabs stay as they are. Without the demo
// the areas spawn BaseEnemy-CE and there are no harvestables.
//
// The scene is generated: every root is rebuilt on each run except one named "Authored", which is kept as it is. Put
// hand-placed additions under it. The map info, database entry, build-settings entry and registry layout are updated in
// place, so a rerun is safe.
//
// Reached from the menu and from batch mode (-executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshLargeMapBuilder.BuildFromCommandLine).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>ServerMeshLargeMapBuilder builds the ServerMeshTestMap8k scene, its assets and its registrations.</summary>
    public static class ServerMeshLargeMapBuilder
    {
        public const string MapId = "ServerMeshTestMap8k";
        public const string Folder = "Assets/ServerMeshingDemo";
        public const string SceneFolder = Folder + "/Scenes";
        public const string ScenePath = SceneFolder + "/" + MapId + ".unity";
        public const string AssetFolder = SceneFolder + "/" + MapId;
        public const string TerrainDataPath = AssetFolder + "/Terrain.asset";
        public const string TerrainLayerPath = AssetFolder + "/Ground.terrainlayer";
        public const string GroundTexturePath = AssetFolder + "/Ground.png";
        /// <summary>The kit demo's grass, the ground's layer when the demo is there: plain, so only the cell edges draw lines.</summary>
        public const string DemoGrassLayerPath = "Assets/OpenMMORPG/Demo/Terrain/Island_Grass.terrainlayer";
        public const string EdgeLayerPath = AssetFolder + "/CellEdge.terrainlayer";
        public const string EdgeTexturePath = AssetFolder + "/CellEdge.png";
        public const string NavMeshPath = AssetFolder + "/NavMesh.asset";
        public const string MapInfoPath = Folder + "/GameData/" + MapId + "-MapInfo.asset";
        public const string TemplateMapInfoPath = "Assets/ServerMeshing/TestMap/GameData/ServerMeshTestMap2-MapInfo.asset";
        public const string WorldPath = "Assets/ServerMeshing/TestMap/ServerMeshWorld.asset";
        public const string DatabasePath = "Assets/BaseDemo/GameData/GameDatabase-CE.asset";
        public const string GameInstancePath = "Assets/BaseDemo/Prefabs/GameInstance-CE.prefab";
        public const string GameplayRulePath = "Assets/ServerMeshing/Mesh/MeshGameplayRule-CE.asset";
        public const string MonsterPrefabPath = "Assets/BaseDemo/Prefabs/GamePlay/CharacterEntities/BaseEnemy-CE.prefab";
        /// <summary>MonsterFolder holds the meshed copies of the demo's monster prefabs.</summary>
        public const string MonsterFolder = Folder + "/Monsters";
        /// <summary>HarvestableFolder holds the meshed copies of the demo's harvestable prefabs.</summary>
        public const string HarvestableFolder = Folder + "/Harvestables";
        public const string DemoHarvestableFolder = "Assets/OpenMMORPG/Demo/Prefabs/GamePlay/Harvestables";
        public const string AuthoredRoot = "Authored";
        /// <summary>The kit demo's map, whose light, sky cycle and lighting settings the 8 km map copies.</summary>
        public const string DemoMapScenePath = "Assets/OpenMMORPG/Demo/Scenes/DemoMap.unity";
        public const string DemoLightRoot = "Directional Light";
        public const string DemoSkyRoot = "SkyCycle";
        /// <summary>DemoSkyComponent is the demo's DayNightSkyCycle, by name: a demo script, not the add-on's.</summary>
        public const string DemoSkyComponent = "DayNightSkyCycle";

        /// <summary>MapSize is the terrain's edge in metres; the map is centred on the scene origin.</summary>
        public const float MapSize = 8192f;
        /// <summary>CellSize is the registry cell edge; 8 x 8 cells cover the map.</summary>
        public const float CellSize = 1024f;
        /// <summary>WorldOrigin places the map in the shared frame, clear of ServerMeshTestMap and ServerMeshTestMap2.</summary>
        public static readonly Vector3 WorldOrigin = new Vector3(0f, 0f, 20000f);
        /// <summary>
        /// GroundHeight is the flat ground's world height. Not 0: the demo player's FootstepEffects takes anything below its
        /// sea level (0 m, plus up to 18 cm of swell) as sea, and splashes on every step.
        /// </summary>
        public const float GroundHeight = 20f;
        /// <summary>StartEdgeDistance is how far inside cell (4,4) a new character starts from its west edge (cell (3,4) beyond).</summary>
        public const float StartEdgeDistance = 25f;
        /// <summary>StartPosition is StartEdgeDistance inside cell (4,4)'s west edge, halfway along it, just above the ground.</summary>
        public static readonly Vector3 StartPosition = new Vector3(StartEdgeDistance, GroundHeight + 1f, CellSize * 0.5f);
        /// <summary>StartRotation faces west, at the edge: the first steps forward cross into the neighbour cell.</summary>
        public static readonly Vector3 StartRotation = new Vector3(0f, 270f, 0f);

        /// <summary>CurtainHeight is how tall the see-through curtain over every cell edge is, in metres.</summary>
        public const float CurtainHeight = 15f;
        /// <summary>CurtainAlpha is the curtain's opacity at the ground; it fades to nothing at the top.</summary>
        public const float CurtainAlpha = 0.7f;
        public const string CurtainMeshPath = AssetFolder + "/CellEdgeCurtain.asset";
        public const string CurtainMaterialPath = AssetFolder + "/CellEdgeCurtain.mat";
        public const string CurtainTexturePath = AssetFolder + "/CellEdgeCurtain.png";

        /// <summary>EdgeWidth is how wide the painted cell-edge lines are, in metres.</summary>
        public const float EdgeWidth = 6f;
        /// <summary>EdgeAlphamapResolution: 4 m per splat texel, fine enough for the edge lines.</summary>
        public const int EdgeAlphamapResolution = 2048;
        public static readonly Color EdgeColor = new Color(0.85f, 0.22f, 0.12f);

        /// <summary>ClearRadius keeps the map's spawns this far from the start, as around a first town. It is about the 80 m view, so they come into sight within steps; the crossing rig's patrols run inside it.</summary>
        public const float ClearRadius = 90f;
        /// <summary>Land within StarterLandRadius of the start is at least StarterBusy busy at ClearRadius, fading out: a first zone is never empty.</summary>
        public const float StarterLandRadius = 1200f, StarterBusy = 0.7f;
        /// <summary>HardestDistance from the start is where the hardest monster mixes begin; the ones between rise with distance.</summary>
        public const float HardestDistance = 4000f;
        /// <summary>MixWindow is how many mixes to either side of its distance's own an area may take.</summary>
        public const int MixWindow = 2;
        /// <summary>LandScale is the size of the busy and quiet stretches of land in metres, about a cell.</summary>
        public const float LandScale = 1400f;
        /// <summary>The noise values at and below which land is quiet, and at and above which it is busy.</summary>
        public const float QuietNoise = 0.3f, BusyNoise = 0.65f;
        /// <summary>BusyCampRate is what busy land multiplies an area's group size by: a demo area is a group, a busy one a camp.</summary>
        public const float BusyCampRate = 2f;
        /// <summary>The row of harvestables on the cell edge beside the start: on the east cell, its centre a metre over the line, in reach from both cells, north of the patrols' lane. One of each tool's.</summary>
        private static readonly (string prefab, int amount, float north)[] StartRow =
        {
            (HarvestableFolder + "/Harvest_CommonTree_1.prefab", 6, 44f),
            (HarvestableFolder + "/Harvest_Mushroom_Common.prefab", 6, 64f),
            (HarvestableFolder + "/Harvest_Rock_Medium_1.prefab", 3, 80f),
        };
        public const float StartRowX = 1f, StartRowWidth = 4f, StartRowLength = 16f;
        /// <summary>A herd across the line from the start, in sight of a new character, south of the patrols' lane. Deer start no fight.</summary>
        public const string StartHerdPrefab = MonsterFolder + "/DemoDeer.prefab";
        public static readonly Vector3 StartHerdPosition = new Vector3(-50f, GroundHeight, StartPosition.z - 14f);
        public const float StartHerdRadius = 18f;
        public const int StartHerdSize = 6;
        public const int Seed = 8192;

        /// <summary>SpawnPlan is how one kind of spawn area spreads over the map: a jittered grid of points, each taken by a chance the land sets.</summary>
        private struct SpawnPlan
        {
            public string root;
            public float spacing, jitter;
            public float quietChance, busyChance;
            public float landOffset; // its own noise: forests do not follow the camps
            public bool harderWithDistance;
            public int seed;
        }

        private static readonly SpawnPlan MonsterPlan = new SpawnPlan
        {
            root = "MonsterSpawns", spacing = 128f, jitter = 56f, quietChance = 0.05f, busyChance = 0.85f,
            landOffset = 0f, harderWithDistance = true, seed = Seed,
        };

        /// <summary>A patch of trees or rocks is wider than a camp, so there are fewer of them.</summary>
        private static readonly SpawnPlan NodePlan = new SpawnPlan
        {
            root = "HarvestableSpawns", spacing = 256f, jitter = 112f, quietChance = 0.02f, busyChance = 0.7f,
            landOffset = 37.3f, harderWithDistance = false, seed = Seed + 2,
        };

        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build 8 km Test Map")]
        public static void Menu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Build();
        }

        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build 8 km Test Map Spawns")]
        public static void SpawnsMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            BuildSpawns();
        }

        /// <summary>BuildFromCommandLine is the batch-mode entry point; it exits 0 on success and 1 on failure.</summary>
        public static void BuildFromCommandLine()
        {
            bool ok;
            try
            {
                ok = Build();
            }
            catch (Exception ex)
            {
                Debug.LogError("Large map error: " + ex);
                ok = false;
            }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Build generates the scene and registers the map. It logs one Large map line per step.</summary>
        public static bool Build()
        {
            MeshWorldRegistry world = AssetDatabase.LoadAssetAtPath<MeshWorldRegistry>(WorldPath);
            if (world == null)
            {
                Debug.LogError($"Large map error: missing {WorldPath}");
                return false;
            }
            EnsureFolder(AssetFolder);
            SpawnMixes mixes = LoadSpawnMixes();
            if (mixes == null)
                return false;

            Scene scene = OpenOrCreateScene();
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name != AuthoredRoot)
                    UnityEngine.Object.DestroyImmediate(root);

            CreateLightAndSky(scene);
            Terrain terrain = CreateTerrain();
            CreateCellEdgeCurtains();
            // Opening a scene (this one, and the demo map for its sky) unloads unreferenced assets, which leaves a
            // reference loaded before it dead: assigned, it saves as nothing. So everything assigned below is loaded here.
            world = AssetDatabase.LoadAssetAtPath<MeshWorldRegistry>(WorldPath);
            CreateZoneManager(world);
            string spawns = CreateSpawnAreas(mixes);
            CreateSpawnPoint();
            BakeNavMesh(terrain);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("Large map error: the scene could not be saved at " + ScenePath);
                return false;
            }
            Debug.Log($"Large map: scene {ScenePath}, {MapSize:0} m terrain, {spawns}");

            MapInfo mapInfo = EnsureMapInfo();
            AddToDatabase(mapInfo);
            AddToBuildSettings();
            AddLayout(world);
            CheckGameplayRule();
            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>BuildSpawns replaces the map's spawn areas and leaves the rest of the scene as it is: no terrain, no navmesh bake.</summary>
        public static bool BuildSpawns()
        {
            SpawnMixes mixes = LoadSpawnMixes();
            if (mixes == null)
                return false;
            Scene scene = OpenOrCreateScene();
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == MonsterPlan.root || root.name == NodePlan.root)
                    UnityEngine.Object.DestroyImmediate(root);
            string spawns = CreateSpawnAreas(mixes);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("Large map error: the scene could not be saved at " + ScenePath);
                return false;
            }
            Debug.Log($"Large map: {ScenePath}, {spawns}");
            return true;
        }

        // ---------------------------------------------------------------- scene

        private static Scene OpenOrCreateScene()
        {
            if (File.Exists(ScenePath))
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            return scene;
        }

        private static void CreateLightAndSky(Scene scene)
        {
            if (File.Exists(DemoMapScenePath))
            {
                Scene demo = EditorSceneManager.OpenScene(DemoMapScenePath, OpenSceneMode.Additive);
                try
                {
                    GameObject demoLight = demo.GetRootGameObjects().FirstOrDefault(r => r.name == DemoLightRoot);
                    GameObject demoSky = demo.GetRootGameObjects().FirstOrDefault(r => r.name == DemoSkyRoot);
                    if (demoLight != null && demoSky != null)
                    {
                        GameObject light = CopyRoot(demoLight, scene);
                        GameObject sky = CopyRoot(demoSky, scene);
                        // The copy's sun is the copied light, not the demo scene's.
                        SerializedObject so = new SerializedObject(sky.GetComponent(DemoSkyComponent));
                        so.FindProperty("sun").objectReferenceValue = light.GetComponent<Light>();
                        so.ApplyModifiedPropertiesWithoutUndo();
                        CopyLightingSettings(demo, scene, light.GetComponent<Light>());
                        Debug.Log($"Large map: light, sky cycle and lighting settings from {DemoMapScenePath}");
                        return;
                    }
                    Debug.LogWarning($"Large map: {DemoMapScenePath} has no {DemoLightRoot} or {DemoSkyRoot}; a plain light instead");
                }
                finally
                {
                    EditorSceneManager.CloseScene(demo, true);
                }
            }
            GameObject plain = new GameObject("Directional Light");
            SceneManager.MoveGameObjectToScene(plain, scene);
            Light l = plain.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.5f;
            l.shadows = LightShadows.Soft;
            plain.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static GameObject CopyRoot(GameObject source, Scene scene)
        {
            GameObject copy = UnityEngine.Object.Instantiate(source);
            copy.name = source.name;
            SceneManager.MoveGameObjectToScene(copy, scene);
            return copy;
        }

        /// <summary>The demo map's skybox, fog and ambient (the sky cycle drives them at runtime from these).</summary>
        private static void CopyLightingSettings(Scene from, Scene to, Light sun)
        {
            Scene active = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(from);
            Material skybox = RenderSettings.skybox;
            bool fog = RenderSettings.fog;
            FogMode fogMode = RenderSettings.fogMode;
            Color fogColor = RenderSettings.fogColor;
            float fogStart = RenderSettings.fogStartDistance, fogEnd = RenderSettings.fogEndDistance, fogDensity = RenderSettings.fogDensity;
            AmbientMode ambientMode = RenderSettings.ambientMode;
            Color sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor;
            float ambientIntensity = RenderSettings.ambientIntensity;
            SceneManager.SetActiveScene(to);
            RenderSettings.skybox = skybox;
            RenderSettings.fog = fog;
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientIntensity = ambientIntensity;
            RenderSettings.sun = sun;
            if (active.IsValid() && active != from)
                SceneManager.SetActiveScene(active);
        }

        private static Terrain CreateTerrain()
        {
            TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                data = new TerrainData();
                AssetDatabase.CreateAsset(data, TerrainDataPath);
            }
            // Flat: a coarse heightmap is enough and every height is zero.
            data.heightmapResolution = 33;
            data.size = new Vector3(MapSize, 100f, MapSize);
            data.SetHeights(0, 0, new float[data.heightmapResolution, data.heightmapResolution]);
            data.alphamapResolution = EdgeAlphamapResolution;
            data.baseMapResolution = 1024; // the edges stay visible past the basemap distance
            data.terrainLayers = new[] { EnsureTerrainLayer(), EnsureEdgeLayer() };
            PaintCellEdges(data);
            EditorUtility.SetDirty(data);

            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.position = new Vector3(-MapSize * 0.5f, GroundHeight, -MapSize * 0.5f);
            Terrain terrain = go.GetComponent<Terrain>();
            // A Terrain added from script has no material, and under URP that draws nothing.
            Material material = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial : null;
            if (material != null)
                terrain.materialTemplate = material;
            else
                Debug.LogWarning("Large map: no render pipeline terrain material found; the terrain may not draw");
            terrain.drawInstanced = true;
            terrain.basemapDistance = 4000f;
            return terrain;
        }

        private static TerrainLayer EnsureTerrainLayer()
        {
            TerrainLayer grass = AssetDatabase.LoadAssetAtPath<TerrainLayer>(DemoGrassLayerPath);
            if (grass != null)
                return grass;
            // Without the demo: plain grass green, no pattern, so the painted cell edges are the only lines on the ground.
            const int size = 4;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.SetPixels(Enumerable.Repeat(new Color(0.36f, 0.44f, 0.30f), size * size).ToArray());
            tex.Apply();
            File.WriteAllBytes(GroundTexturePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(GroundTexturePath, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(GroundTexturePath);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.SaveAndReimport();

            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(TerrainLayerPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, TerrainLayerPath);
            }
            layer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(GroundTexturePath);
            layer.tileSize = new Vector2(10f, 10f);
            layer.smoothness = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        private static TerrainLayer EnsureEdgeLayer()
        {
            // A solid colour: the line's look comes from the splat map, not the texture.
            const int size = 4;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.SetPixels(Enumerable.Repeat(EdgeColor, size * size).ToArray());
            tex.Apply();
            File.WriteAllBytes(EdgeTexturePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(EdgeTexturePath, ImportAssetOptions.ForceUpdate);

            TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(EdgeLayerPath);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, EdgeLayerPath);
            }
            layer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EdgeTexturePath);
            layer.tileSize = new Vector2(10f, 10f);
            layer.smoothness = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        /// <summary>
        /// PaintCellEdges paints the edge layer along every line between two cells of the registry layout (the same grid
        /// AddLayout writes: gridOrigin at the terrain's corner, CellSize apart), EdgeWidth wide and antialiased.
        /// </summary>
        private static void PaintCellEdges(TerrainData data)
        {
            int res = data.alphamapResolution;
            float texel = MapSize / res;
            float halfWidth = EdgeWidth * 0.5f;
            float[] edgeWeight = new float[res]; // per row or column: both axes use the same grid
            for (int i = 0; i < res; i++)
            {
                float local = (i + 0.5f) * texel; // metres from the terrain's corner, which is the grid origin
                float toEdge = Mathf.Abs(local - Mathf.Round(local / CellSize) * CellSize);
                bool inside = local > CellSize * 0.5f && local < MapSize - CellSize * 0.5f; // the map's own border is no cell edge
                edgeWeight[i] = inside ? Mathf.Clamp01((halfWidth - toEdge) / texel + 0.5f) : 0f;
            }
            float[,,] maps = new float[res, res, 2];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float edge = Mathf.Max(edgeWeight[x], edgeWeight[z]);
                    maps[z, x, 0] = 1f - edge;
                    maps[z, x, 1] = edge;
                }
            }
            data.SetAlphamaps(0, 0, maps);
            Debug.Log($"Large map: cell edges painted every {CellSize:0} m, {EdgeWidth:0} m wide");
        }

        private static void CreateZoneManager(MeshWorldRegistry world)
        {
            GameObject go = new GameObject("ZoneManager");
            ZoneBoundaryDetector detector = go.AddComponent<ZoneBoundaryDetector>();
            detector.world = world;
            ZoneDebugVisualizer visualizer = go.AddComponent<ZoneDebugVisualizer>();
            visualizer.world = world;
            visualizer.mapId = MapId;
            visualizer.lineHeight = 200f;
            go.AddComponent<ServerMeshZoneBootstrap>();
        }

        private static string CreateSpawnAreas(SpawnMixes mixes)
        {
            string monsters = PlaceAreas<MonsterSpawnArea, BaseMonsterCharacterEntity>(MonsterPlan, mixes.monsters);
            string nodes = PlaceAreas<HarvestableSpawnArea, HarvestableEntity>(NodePlan, mixes.harvestables);
            CreateStartSights();
            return monsters + "; " + nodes;
        }

        /// <summary>CreateStartSights stands what a new character sees first, whatever the land around the start came out as.</summary>
        private static void CreateStartSights()
        {
            GameObject nodes = GameObject.Find(NodePlan.root), monsters = GameObject.Find(MonsterPlan.root);
            GameObject herd = AssetDatabase.LoadAssetAtPath<GameObject>(StartHerdPrefab);
            if (monsters != null && herd != null)
            {
                SpawnTemplate deer = new SpawnTemplate { minLevel = 1, maxLevel = 2, minAmount = StartHerdSize, maxAmount = StartHerdSize, radius = StartHerdRadius };
                AddArea<MonsterSpawnArea>(monsters.transform, "Spawn8k_StartHerd", StartHerdPosition, deer, herd.GetComponent<BaseMonsterCharacterEntity>(), MonsterPlan.seed - 1);
            }
            if (nodes == null)
                return;
            int seed = NodePlan.seed - 1;
            foreach ((string path, int amount, float north) in StartRow)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;
                SpawnTemplate row = new SpawnTemplate { minLevel = 1, maxLevel = 1, minAmount = amount, maxAmount = amount };
                Vector3 position = new Vector3(StartRowX, GroundHeight, StartPosition.z + north);
                SerializedObject so = AddArea<HarvestableSpawnArea>(nodes.transform, "Spawn8k_StartRow_" + prefab.name, position, row, prefab.GetComponent<HarvestableEntity>(), seed--);
                so.FindProperty("type").enumValueIndex = (int)GameAreaType.Square;
                so.FindProperty("squareSizeX").floatValue = StartRowWidth;
                so.FindProperty("squareSizeZ").floatValue = StartRowLength;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>AddArea makes one spawn area under the root, a radius one; the caller may change its shape on what it returns.</summary>
        private static SerializedObject AddArea<TArea>(Transform root, string name, Vector3 position, SpawnTemplate template, Component prefab, int seed) where TArea : GameSpawnArea
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.position = position;
            SerializedObject so = new SerializedObject(go.AddComponent<TArea>());
            so.FindProperty("type").enumValueIndex = (int)GameAreaType.Radius;
            so.FindProperty("randomRadius").floatValue = template.radius;
            so.FindProperty("groundFindingType").enumValueIndex = (int)GameAreaGroundFindingType.NavMesh;
            so.FindProperty("randomPositionSeed").intValue = seed;
            so.FindProperty("minLevel").intValue = template.minLevel;
            so.FindProperty("maxLevel").intValue = template.maxLevel;
            so.FindProperty("minAmount").intValue = template.minAmount;
            so.FindProperty("maxAmount").intValue = template.maxAmount;
            so.FindProperty("prefab").objectReferenceValue = prefab;
            so.ApplyModifiedPropertiesWithoutUndo();
            return so;
        }

        /// <summary>PlaceAreas spreads one kind of spawn area over the map and says how it came out.</summary>
        private static string PlaceAreas<TArea, TEntity>(SpawnPlan plan, List<SpawnTemplate> templates) where TArea : GameSpawnArea where TEntity : Component
        {
            if (templates.Count == 0)
                return plan.root + ": no spawn mixes";
            GameObject root = new GameObject(plan.root);
            System.Random random = new System.Random(plan.seed);
            System.Random pick = new System.Random(plan.seed + 1); // its own stream: the areas' places do not depend on the mix
            Dictionary<string, TEntity> prefabs = templates.Select(t => t.prefabPath).Distinct()
                .ToDictionary(p => p, p => AssetDatabase.LoadAssetAtPath<GameObject>(p).GetComponent<TEntity>());
            Dictionary<Vector2Int, float> perCell = new Dictionary<Vector2Int, float>();
            int perSide = Mathf.RoundToInt(MapSize / plan.spacing);
            float first = -MapSize * 0.5f + plan.spacing * 0.5f;
            int count = 0;
            float expected = 0f;
            for (int gy = 0; gy < perSide; gy++)
            {
                for (int gx = 0; gx < perSide; gx++)
                {
                    float x = first + gx * plan.spacing + Jitter(random, plan.jitter);
                    float z = first + gy * plan.spacing + Jitter(random, plan.jitter);
                    double roll = random.NextDouble();
                    float fromStart = new Vector2(x - StartPosition.x, z - StartPosition.z).magnitude;
                    SpawnTemplate template = templates[plan.harderWithDistance ? MixAt(fromStart, templates.Count, pick) : pick.Next(templates.Count)];
                    float busy = Mathf.Max(Busyness(x, z, plan.landOffset), StarterBusy * Mathf.InverseLerp(StarterLandRadius, ClearRadius, fromStart));
                    float rate = Mathf.Lerp(1f, BusyCampRate, busy);
                    float radius = template.radius * Mathf.Sqrt(rate);
                    if (roll >= Mathf.Lerp(plan.quietChance, plan.busyChance, busy) || fromStart < ClearRadius + radius)
                        continue;
                    SpawnTemplate scaled = template;
                    scaled.radius = radius;
                    scaled.minAmount = Mathf.Max(1, Mathf.RoundToInt(template.minAmount * rate));
                    scaled.maxAmount = Mathf.Max(scaled.minAmount, Mathf.RoundToInt(template.maxAmount * rate));
                    TEntity prefab = prefabs[template.prefabPath];
                    AddArea<TArea>(root.transform, $"Spawn8k_{gx:00}_{gy:00}_{prefab.name}", new Vector3(x, GroundHeight, z), scaled, prefab, plan.seed + count);
                    float amount = (scaled.minAmount + scaled.maxAmount) * 0.5f;
                    expected += amount;
                    Vector2Int cell = new Vector2Int(Mathf.FloorToInt((x + MapSize * 0.5f) / CellSize), Mathf.FloorToInt((z + MapSize * 0.5f) / CellSize));
                    perCell.TryGetValue(cell, out float inCell);
                    perCell[cell] = inCell + amount;
                    count++;
                }
            }
            return $"{plan.root}: {count} areas, about {expected:0} entities ({expected / (MapSize * MapSize / 1000000f):0} per km2), " +
                   $"cells {perCell.Values.Min():0} to {perCell.Values.Max():0}";
        }

        /// <summary>Busyness is how busy the land is at a point, 0 quiet to 1 busy. Slow noise, so neighbouring land is alike.</summary>
        private static float Busyness(float x, float z, float landOffset)
        {
            float noise = Mathf.PerlinNoise((x + MapSize) / LandScale + landOffset, (z + MapSize) / LandScale + landOffset);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(QuietNoise, BusyNoise, noise));
        }

        /// <summary>MixAt picks a mix from a list sorted easiest first: near the start the easy ones, from HardestDistance the hardest.</summary>
        private static int MixAt(float fromStart, int mixes, System.Random pick)
        {
            int own = Mathf.RoundToInt(Mathf.Clamp01(fromStart / HardestDistance) * (mixes - 1));
            return Mathf.Clamp(own + pick.Next(-MixWindow, MixWindow + 1), 0, mixes - 1);
        }

        // ---------------------------------------------------------------- monsters

        /// <summary>SpawnTemplate is one spawn area's settings: what it spawns, how strong, how many, how spread.</summary>
        private struct SpawnTemplate
        {
            public string prefabPath;
            public int minLevel, maxLevel, minAmount, maxAmount;
            public float radius;
        }

        /// <summary>Harvestables the demo has prefabs for and DemoMap spawns none of: mushrooms, for any blade or staff, and iron, for a pick.</summary>
        private static readonly SpawnTemplate[] ExtraNodes =
        {
            new SpawnTemplate { prefabPath = DemoHarvestableFolder + "/Harvest_Mushroom_Common.prefab", minLevel = 1, maxLevel = 1, minAmount = 8, maxAmount = 8, radius = 30f },
            new SpawnTemplate { prefabPath = DemoHarvestableFolder + "/Harvest_Mushroom_Laetiporus.prefab", minLevel = 1, maxLevel = 1, minAmount = 8, maxAmount = 8, radius = 30f },
            new SpawnTemplate { prefabPath = DemoHarvestableFolder + "/Harvest_IronVein_Rock_Medium_1.prefab", minLevel = 1, maxLevel = 1, minAmount = 4, maxAmount = 4, radius = 24f },
            new SpawnTemplate { prefabPath = DemoHarvestableFolder + "/Harvest_IronVein_Rock_Medium_2.prefab", minLevel = 1, maxLevel = 1, minAmount = 4, maxAmount = 4, radius = 24f },
            new SpawnTemplate { prefabPath = DemoHarvestableFolder + "/Harvest_IronVein_Rock_Medium_3.prefab", minLevel = 1, maxLevel = 1, minAmount = 4, maxAmount = 4, radius = 24f },
        };

        /// <summary>SpawnMixes is what the map's spawn areas draw from: monsters sorted easiest first, and harvestables.</summary>
        private sealed class SpawnMixes
        {
            public List<SpawnTemplate> monsters = new List<SpawnTemplate>();
            public List<SpawnTemplate> harvestables = new List<SpawnTemplate>();
        }

        /// <summary>The kit demo's spawn mixes (DemoMap's spawn areas, on meshed copies), or BaseEnemy-CE without it.</summary>
        private static SpawnMixes LoadSpawnMixes()
        {
            if (!File.Exists(DemoMapScenePath))
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(MonsterPrefabPath) == null)
                {
                    Debug.LogError($"Large map error: neither {DemoMapScenePath} nor {MonsterPrefabPath} is there to spawn");
                    return null;
                }
                Debug.Log($"Large map: no {DemoMapScenePath}; the spawn areas spawn {MonsterPrefabPath}");
                SpawnMixes plain = new SpawnMixes();
                plain.monsters.Add(new SpawnTemplate { prefabPath = MonsterPrefabPath, minLevel = 1, maxLevel = 3, minAmount = 5, maxAmount = 5, radius = 40f });
                return plain;
            }
            SpawnMixes mixes = new SpawnMixes();
            Scene demo = EditorSceneManager.OpenScene(DemoMapScenePath, OpenSceneMode.Additive);
            try
            {
                foreach (GameObject root in demo.GetRootGameObjects())
                {
                    AddTemplates<MonsterSpawnArea>(root, mixes.monsters);
                    AddTemplates<HarvestableSpawnArea>(root, mixes.harvestables);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(demo, true);
            }
            if (mixes.monsters.Count == 0)
            {
                Debug.LogError($"Large map error: {DemoMapScenePath} has no monster spawn area");
                return null;
            }
            mixes.harvestables.AddRange(ExtraNodes.Where(t => File.Exists(t.prefabPath)));
            if (!UseMeshedCopies(mixes.monsters, MonsterFolder, MonsterSwaps) || !UseMeshedCopies(mixes.harvestables, HarvestableFolder, HarvestableSwaps))
                return null;
            mixes.monsters = mixes.monsters.OrderBy(t => t.minLevel + t.maxLevel).ThenBy(t => t.prefabPath, StringComparer.Ordinal).ToList();
            Debug.Log($"Large map: {mixes.monsters.Count} monster and {mixes.harvestables.Count} harvestable spawn mixes from {DemoMapScenePath}");
            return mixes;
        }

        private static void AddTemplates<TArea>(GameObject root, List<SpawnTemplate> templates) where TArea : GameSpawnArea
        {
            foreach (TArea area in root.GetComponentsInChildren<TArea>(false))
            {
                SerializedObject so = new SerializedObject(area);
                UnityEngine.Object prefab = so.FindProperty("prefab").objectReferenceValue;
                if (prefab == null) continue;
                templates.Add(new SpawnTemplate
                {
                    prefabPath = AssetDatabase.GetAssetPath(prefab),
                    minLevel = area.minLevel, maxLevel = area.maxLevel, minAmount = area.minAmount, maxAmount = area.maxAmount,
                    radius = so.FindProperty("randomRadius").floatValue,
                });
            }
        }

        /// <summary>UseMeshedCopies points the mixes at meshed copies of their prefabs, made under the folder; false on failure.</summary>
        private static bool UseMeshedCopies(List<SpawnTemplate> templates, string folder, (Type from, Type to)[] swaps)
        {
            Dictionary<string, string> copies = new Dictionary<string, string>();
            foreach (string source in templates.Select(t => t.prefabPath).Distinct())
            {
                string copy = MeshedCopy(source, folder, swaps);
                if (copy == null)
                    return false;
                copies[source] = copy;
            }
            for (int i = 0; i < templates.Count; i++)
            {
                SpawnTemplate t = templates[i];
                t.prefabPath = copies[t.prefabPath];
                templates[i] = t;
            }
            return true;
        }

        /// <summary>Exact types swapped on a monster copy: each kit or demo component for its meshed version (fields kept).</summary>
        private static readonly (Type from, Type to)[] MonsterSwaps =
        {
            (typeof(MonsterCharacterEntity), typeof(MeshMonsterCharacterEntity)),
            (typeof(LineOfSightMonsterActivityComponent), typeof(MeshLineOfSightMonsterActivityComponent)),
            (typeof(MonsterActivityComponent), typeof(MeshMonsterActivityComponent)),
            (typeof(InterruptChanceUseSkillComponent), typeof(MeshInterruptChanceUseSkillComponent)),
            (typeof(DefaultCharacterUseSkillComponent), typeof(MeshCharacterUseSkillComponent)),
            (typeof(DefaultCharacterAttackComponent), typeof(MeshCharacterAttackComponent)),
        };

        /// <summary>The same for a harvestable copy: its yield reaches an attacker standing across a line.</summary>
        private static readonly (Type from, Type to)[] HarvestableSwaps =
        {
            (typeof(HarvestableEntity), typeof(MeshHarvestableEntity)),
        };

        /// <summary>MeshedCopy makes or refreshes &lt;folder&gt;/&lt;name&gt; from a demo prefab; null on failure.</summary>
        private static string MeshedCopy(string source, string folder, (Type from, Type to)[] swaps)
        {
            EnsureFolder(folder);
            string copy = folder + "/" + Path.GetFileName(source);
            if (File.Exists(copy))
            {
                File.Copy(source, copy, true); // a rerun keeps the copy's guid
                AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceUpdate);
            }
            else if (!AssetDatabase.CopyAsset(source, copy))
            {
                Debug.LogError($"Large map error: could not copy {source} to {copy}");
                return null;
            }
            GameObject contents = PrefabUtility.LoadPrefabContents(copy);
            try
            {
                foreach ((Type from, Type to) in swaps)
                {
                    MonoScript script = ScriptOf(to);
                    foreach (Component component in contents.GetComponentsInChildren(from, true).Where(c => c != null && c.GetType() == from).ToList())
                    {
                        SerializedObject so = new SerializedObject(component);
                        so.FindProperty("m_Script").objectReferenceValue = script;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                // Its own network asset id: the copy's guid, not the demo prefab's (the two would collide when both are registered).
                SerializedObject identity = new SerializedObject(contents.GetComponent<LiteNetLibManager.LiteNetLibIdentity>());
                identity.FindProperty("assetId").stringValue = AssetDatabase.AssetPathToGUID(copy);
                identity.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, copy);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            foreach ((Type from, Type to) in swaps)
            {
                if (saved.GetComponentsInChildren(from, true).Any(c => c != null && c.GetType() == from))
                {
                    Debug.LogError($"Large map error: {copy} still has a {from.Name} (inherited from another prefab?)");
                    return null;
                }
            }
            return copy;
        }

        private static Dictionary<Type, MonoScript> s_scripts;

        private static MonoScript ScriptOf(Type type)
        {
            if (s_scripts == null)
            {
                s_scripts = new Dictionary<Type, MonoScript>();
                foreach (string guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/ServerMeshing/Mesh", Folder }))
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

        private static float Jitter(System.Random random, float jitter)
        {
            return (float)(random.NextDouble() * 2.0 - 1.0) * jitter;
        }

        private static void CreateSpawnPoint()
        {
            GameObject root = new GameObject("SpawnPoints");
            GameObject point = new GameObject("SpawnPoint_Start");
            point.transform.SetParent(root.transform, false);
            point.transform.SetPositionAndRotation(StartPosition, Quaternion.Euler(StartRotation));
        }

        /// <summary>
        /// CreateCellEdgeCurtains stands a see-through curtain, in the edge colour, on every line between two cells (the
        /// same grid PaintCellEdges paints), so a border reads from afar and from any angle. Drawn only: no collider, so
        /// nothing walks into it and the navmesh bake does not see it.
        /// </summary>
        private static void CreateCellEdgeCurtains()
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> triangles = new List<int>();
            float half = MapSize * 0.5f;
            float bottom = GroundHeight - 0.5f, top = GroundHeight + CurtainHeight;
            int lines = Mathf.RoundToInt(MapSize / CellSize);
            for (int k = 1; k < lines; k++)
            {
                float at = -half + k * CellSize;
                AddQuad(vertices, uvs, triangles, new Vector3(at, bottom, -half), new Vector3(at, bottom, half), top - bottom); // x = at, runs along z
                AddQuad(vertices, uvs, triangles, new Vector3(-half, bottom, at), new Vector3(half, bottom, at), top - bottom); // z = at, runs along x
            }
            UnityEngine.Mesh mesh = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(CurtainMeshPath);
            if (mesh == null)
            {
                mesh = new UnityEngine.Mesh { name = "CellEdgeCurtain" };
                AssetDatabase.CreateAsset(mesh, CurtainMeshPath);
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            GameObject go = new GameObject("CellEdgeCurtains");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = EnsureCurtainMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Debug.Log($"Large map: {2 * (lines - 1)} cell-edge curtains, {CurtainHeight:0} m tall");
        }

        private static void AddQuad(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles, Vector3 from, Vector3 to, float height)
        {
            int i = vertices.Count;
            vertices.Add(from);
            vertices.Add(to);
            vertices.Add(to + Vector3.up * height);
            vertices.Add(from + Vector3.up * height);
            float length = Vector3.Distance(from, to);
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(length, 0f));
            uvs.Add(new Vector2(length, 1f));
            uvs.Add(new Vector2(0f, 1f));
            triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        /// <summary>URP Unlit, transparent, both faces, tinted the edge colour, over a texture that fades out upward.</summary>
        private static Material EnsureCurtainMaterial()
        {
            const int height = 64;
            Texture2D tex = new Texture2D(1, height, TextureFormat.RGBA32, false);
            for (int y = 0; y < height; y++)
            {
                float t = y / (height - 1f);
                tex.SetPixel(0, y, new Color(1f, 1f, 1f, CurtainAlpha * (1f - t)));
            }
            tex.Apply();
            File.WriteAllBytes(CurtainTexturePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(CurtainTexturePath, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(CurtainTexturePath);
            importer.alphaIsTransparency = true;
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();

            Material material = AssetDatabase.LoadAssetAtPath<Material>(CurtainMaterialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(material, CurtainMaterialPath);
            }
            material.shader = Shader.Find("Universal Render Pipeline/Unlit");
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(CurtainTexturePath));
            material.SetColor("_BaseColor", EdgeColor);
            material.SetFloat("_Surface", 1f); // transparent
            material.SetFloat("_Blend", 0f);   // alpha
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void BakeNavMesh(Terrain terrain)
        {
            NavMeshSurface surface = terrain.gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = 1; // Default only: the terrain
            // Coarser than the default 1/6 m: the default would voxelise 67 km² into ~2.4 billion columns. Flat open
            // ground loses nothing at half a metre.
            surface.overrideVoxelSize = true;
            surface.voxelSize = 0.5f;
            surface.overrideTileSize = true;
            surface.tileSize = 256;
            surface.BuildNavMesh();
            NavMeshData data = surface.navMeshData;
            if (data == null)
            {
                Debug.LogError("Large map error: the navmesh bake produced nothing");
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshPath) != null)
                AssetDatabase.DeleteAsset(NavMeshPath);
            AssetDatabase.CreateAsset(data, NavMeshPath);
            surface.navMeshData = data;
            Debug.Log($"Large map: navmesh baked into {NavMeshPath}");
        }

        // ---------------------------------------------------------------- registrations

        private static MapInfo EnsureMapInfo()
        {
            MapInfo mapInfo = AssetDatabase.LoadAssetAtPath<MapInfo>(MapInfoPath);
            if (mapInfo == null)
            {
                // A copy of map 2's info keeps the rig's map settings (no PK, no drops on death).
                AssetDatabase.CopyAsset(TemplateMapInfoPath, MapInfoPath);
                mapInfo = AssetDatabase.LoadAssetAtPath<MapInfo>(MapInfoPath);
            }
            SerializedObject so = new SerializedObject(mapInfo);
            so.FindProperty("id").stringValue = MapId;
            so.FindProperty("scene").FindPropertyRelative("sceneAsset").objectReferenceValue = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            so.FindProperty("scene").FindPropertyRelative("sceneName").stringValue = MapId;
            so.FindProperty("startPosition").vector3Value = StartPosition;
            so.FindProperty("startRotation").vector3Value = StartRotation;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mapInfo);
            Debug.Log($"Large map: map info {MapInfoPath}");
            return mapInfo;
        }

        private static void AddToDatabase(MapInfo mapInfo)
        {
            GameDatabase database = AssetDatabase.LoadAssetAtPath<GameDatabase>(DatabasePath);
            if (database == null)
            {
                Debug.LogWarning("Large map: no game database at " + DatabasePath + "; add the map info by hand");
                return;
            }
            if (database.mapInfos != null && database.mapInfos.Contains(mapInfo))
                return;
            List<BaseMapInfo> list = database.mapInfos != null ? database.mapInfos.ToList() : new List<BaseMapInfo>();
            list.Add(mapInfo);
            database.mapInfos = list.ToArray();
            EditorUtility.SetDirty(database);
            Debug.Log("Large map: map info added to " + DatabasePath);
        }

        private static void AddToBuildSettings()
        {
            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == ScenePath))
                return;
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            // Batch mode's EditorApplication.Exit does not flush dirty project settings, so the entry is also written
            // into the file itself when it is missing there.
            const string settingsPath = "ProjectSettings/EditorBuildSettings.asset";
            string text = File.ReadAllText(settingsPath);
            int at = text.IndexOf("  m_configObjects:", StringComparison.Ordinal);
            if (!text.Contains(ScenePath) && at >= 0)
            {
                string entry = $"  - enabled: 1\n    path: {ScenePath}\n    guid: {AssetDatabase.AssetPathToGUID(ScenePath)}\n";
                File.WriteAllText(settingsPath, text.Insert(at, entry));
            }
            Debug.Log("Large map: scene added to the build settings");
        }

        private static void AddLayout(MeshWorldRegistry world)
        {
            int perSide = Mathf.RoundToInt(MapSize / CellSize);
            MeshMapLayout layout = new MeshMapLayout
            {
                mapId = MapId, worldOrigin = WorldOrigin, gridOrigin = new Vector3(-MapSize * 0.5f, 0f, -MapSize * 0.5f),
                cellSize = CellSize, columns = perSide, rows = perSide, cellIdFormat = "{map}-{x}-{y}", cellIds = null, cellRects = null,
            };
            int existing = world.maps.FindIndex(l => l != null && string.Equals(l.mapId, MapId));
            if (existing >= 0)
            {
                layout.CopyModeFrom(world.maps[existing]); // a rerun keeps the map's cell mode
                world.maps[existing] = layout;
            }
            else
                world.maps.Add(layout);
            EditorUtility.SetDirty(world);
            world.Rebuild();
            Debug.Log($"Large map: registry layout {perSide} x {perSide} = {world.CellsOf(MapId).Count} cells of {CellSize:0} m, world origin {WorldOrigin}");
        }

        private static void CheckGameplayRule()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameInstancePath);
            GameInstance instance = prefab != null ? prefab.GetComponent<GameInstance>() : null;
            if (instance == null)
            {
                Debug.LogWarning("Large map: no game instance at " + GameInstancePath + "; check its gameplay rule is a MeshGameplayRule");
                return;
            }
            SerializedObject so = new SerializedObject(instance);
            SerializedProperty rule = so.FindProperty("gameplayRule");
            if (rule.objectReferenceValue is MeshGameplayRule)
            {
                Debug.Log($"Large map: gameplay rule is {rule.objectReferenceValue.name} (a MeshGameplayRule)");
                return;
            }
            MeshGameplayRule meshRule = AssetDatabase.LoadAssetAtPath<MeshGameplayRule>(GameplayRulePath);
            if (meshRule == null)
            {
                Debug.LogWarning("Large map: no MeshGameplayRule at " + GameplayRulePath + "; assign one on " + GameInstancePath);
                return;
            }
            rule.objectReferenceValue = meshRule;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SavePrefabAsset(prefab);
            Debug.Log("Large map: gameplay rule set to " + GameplayRulePath);
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
