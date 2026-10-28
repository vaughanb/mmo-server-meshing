// Server Meshing add-on — seam crossing on the client (DESIGN.md Q12, slice 2).
//
// Cells of one map share the map's scene, so a hop inside a map keeps the scene (the seamless swap). A neighbour
// of ANOTHER map is a different scene at a fixed frame offset. While the player stands in the band of such a
// neighbour, its scene is loaded additively and placed at that offset (pre-load): the far side is visible before
// the line. The hop is the same seamless swap; when the new server names a scene that is already pre-loaded, the
// client swaps frames instead of letting the kit reload it: the old scene is unloaded (its runtime objects —
// controller, HUD, cameras, stand-ins — are carried over), the pre-loaded scene is moved to its natural origin,
// the carried objects are moved by the same shift, and the kit's own scene-change handling then finds the right
// active scene and proceeds without a load. Without a pre-load (teleport, or a very fast crossing) the kit's normal
// scene load runs, with its loading screen.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Insthync.CameraAndInput;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — seams (client side)")]
        [Tooltip("Pre-load the scene of a neighbouring map (placed at its frame offset) while the player stands within this many metres of the seam; 0 = off (the kit reloads the scene at the crossing).")]
        public float seamPreloadDistance = 80f;
        [Tooltip("Unload a pre-loaded neighbour scene once the player is this many metres beyond the pre-load distance.")]
        public float seamUnloadHysteresis = 40f;

        private sealed class PreloadedSeam
        {
            public string sceneName, mapId, cellId;
            public Vector3 offset;                 // where the neighbour's content sits in the current frame
            public Scene scene;
            public bool loaded;
            public readonly List<GameObject> roots = new List<GameObject>();
            public readonly List<Behaviour> disabled = new List<Behaviour>(); // scene cameras / listeners / event systems silenced while pre-loaded
        }

        private PreloadedSeam _seam;
        private bool _seamLoading;
        private float _nextSeamCheck;
        // The active scene's own root objects, taken when the scene came up: at a swap everything else in the scene was
        // created at runtime (controller, HUD, cameras, indicators, stand-ins) and is carried into the new scene.
        private readonly HashSet<GameObject> _sceneContentRoots = new HashSet<GameObject>();
        private bool _sceneContentFromSwap;

        /// <summary>Remember which roots belong to the scene file (called when the online scene is up; a swap supplies the list itself).</summary>
        private void SnapshotSceneContent()
        {
            if (_sceneContentFromSwap)
            {
                _sceneContentFromSwap = false;
                return;
            }
            _sceneContentRoots.Clear();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                _sceneContentRoots.Add(root);
        }

        /// <summary>Client tick: pre-load the neighbouring map's scene near a seam, drop it when the player walks away.</summary>
        private void SeamClientUpdate()
        {
            if (_seam != null && !_seamLoading && (!_seam.scene.IsValid() || !_seam.scene.isLoaded))
                _seam = null; // the kit reloaded the world (session end, plain warp): the pre-load went with it
            if (world == null || seamPreloadDistance <= 0f || IsMigrating || !IsClientConnected || Time.unscaledTime < _nextSeamCheck)
                return;
            _nextSeamCheck = Time.unscaledTime + 0.5f;
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null || CurrentMapInfo == null)
                return;
            Vector3 pos = player.EntityTransform.position;
            MeshCell cell = world.CellAt(CurrentMapInfo.Id, pos);
            if (cell == null)
                return;
            string activeScene = SceneManager.GetActiveScene().name;
            MeshCell nearest = null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < cell.neighbours.Count; ++i)
            {
                MeshCell neighbour = cell.neighbours[i];
                if (string.Equals(neighbour.mapId, cell.mapId))
                    continue;
                string sceneName = SceneNameOf(neighbour.mapId);
                if (string.IsNullOrEmpty(sceneName) || string.Equals(sceneName, activeScene))
                    continue; // same scene: nothing to pre-load, the swap inside a scene needs no frame change
                float distance = cell.DistanceTo(neighbour, pos);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = neighbour;
                }
            }
            if (_seam != null)
            {
                bool keep = nearest != null && string.Equals(_seam.sceneName, SceneNameOf(nearest.mapId)) && nearestDistance <= seamPreloadDistance + seamUnloadHysteresis;
                if (!keep && !_seamLoading)
                    UnloadSeam("left the band");
                return;
            }
            if (nearest != null && nearestDistance <= seamPreloadDistance && !_seamLoading)
                PreloadSeam(nearest, nearest.OffsetTo(cell)).Forget();
        }

        private static string SceneNameOf(string mapId)
        {
            return GameInstance.MapInfos.TryGetValue(mapId, out BaseMapInfo info) && info != null && info.Scene != null ? info.Scene.SceneName : null;
        }

        private async UniTaskVoid PreloadSeam(MeshCell neighbour, Vector3 offset)
        {
            string sceneName = SceneNameOf(neighbour.mapId);
            PreloadedSeam seam = new PreloadedSeam { sceneName = sceneName, mapId = neighbour.mapId, cellId = neighbour.id, offset = offset };
            _seam = seam;
            _seamLoading = true;
            Stamp($"seam: pre-loading scene {sceneName} of map {neighbour.mapId} at offset {offset} (cell {neighbour.id})");
            AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op == null)
            {
                Logging.LogWarning("Mesh", $"Seam: scene {sceneName} (map {neighbour.mapId}) is not in the build; the kit will reload at the crossing");
                _seam = null;
                _seamLoading = false;
                return;
            }
            while (!op.isDone)
                await UniTask.Yield();
            seam.scene = SceneManager.GetSceneByName(sceneName);
            _seamLoading = false;
            if (!seam.scene.IsValid() || !seam.scene.isLoaded)
            {
                _seam = null;
                Logging.LogWarning("Mesh", $"Seam: scene {sceneName} did not load");
                return;
            }
            if (!ReferenceEquals(_seam, seam))
            {
                _ = SceneManager.UnloadSceneAsync(seam.scene); // superseded while loading; nothing waits for the unload
                return;
            }
            seam.scene.GetRootGameObjects(seam.roots);
            for (int i = 0; i < seam.roots.Count; ++i)
            {
                GameObject root = seam.roots[i];
                root.transform.position += offset;
                foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                    if (camera.enabled) { camera.enabled = false; seam.disabled.Add(camera); }
                foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
                    if (listener.enabled) { listener.enabled = false; seam.disabled.Add(listener); }
                foreach (UnityEngine.EventSystems.EventSystem eventSystem in root.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true))
                    if (eventSystem.enabled) { eventSystem.enabled = false; seam.disabled.Add(eventSystem); }
                // Networked scene objects wait for the server that owns them (the kit deactivates them the same way when it
                // registers a scene); until the swap the neighbour's live entities are its ghosts.
                foreach (LiteNetLibIdentity identity in root.GetComponentsInChildren<LiteNetLibIdentity>(true))
                    if (identity.gameObject.activeSelf) identity.gameObject.SetActive(false);
                foreach (Unity.AI.Navigation.NavMeshSurface surface in root.GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true))
                {
                    surface.RemoveData();
                    surface.AddData(); // at the moved transform
                }
            }
            seam.loaded = true;
            Logging.Log("Mesh", $"Seam: pre-loaded scene {sceneName} of map {neighbour.mapId} at offset {offset} ({seam.roots.Count} roots)");
        }

        private void UnloadSeam(string reason)
        {
            PreloadedSeam seam = _seam;
            _seam = null;
            if (seam == null)
                return;
            if (seam.scene.IsValid() && seam.scene.isLoaded)
                SceneManager.UnloadSceneAsync(seam.scene);
            Logging.Log("Mesh", $"Seam: unloaded pre-loaded scene {seam.sceneName} ({reason})");
        }

        /// <summary>
        /// The new server names its scene. When that scene is pre-loaded here, swap frames first, so the kit's own
        /// handling (below) finds it active and continues without a load.
        /// </summary>
        protected override async void HandleServerSceneChange(ServerSceneInfo serverSceneInfo)
        {
            bool swapped = false;
            if (!IsServer && _seam != null && _seam.loaded && !_seamLoading && _seam.scene.IsValid() && _seam.scene.isLoaded
                && string.Equals(_seam.sceneName, serverSceneInfo.sceneName) && !string.Equals(SceneManager.GetActiveScene().name, serverSceneInfo.sceneName))
            {
                await SwapToPreloadedScene();
                swapped = true;
            }
            // A hop between cells of one scene: the scene the kit is about to "load" is the one already active and
            // registered, so OnClientOnlineSceneLoaded skips the scene-entity registration (and its pool clear).
            SameSceneHop = !swapped && IsMigrating && !IsServer && string.Equals(SceneManager.GetActiveScene().name, serverSceneInfo.sceneName);
            if (SameSceneHop && fastClientReadyOnMigration)
            {
                // The kit's same-scene branch without Assets.Initialize() and InitPoolingQueues(): the prefabs, spawn
                // points, scene objects and pools it would register again are the ones registered when this scene
                // loaded, and nothing cleared them (a migration keeps the client's assets). Measured on the 8 km demo:
                // that registration was the longest frame left in a crossing (9-12 ms against a 3 ms median).
                Stamp("enter-game: same scene, assets kept registered");
                OnClientOnlineSceneLoaded();
                if (!doNotReadyOnSceneLoaded)
                    SendClientReady();
                return;
            }
            base.HandleServerSceneChange(serverSceneInfo);
        }

        /// <summary>Set by HandleServerSceneChange for a migration that stays in the active scene; consumed by OnClientOnlineSceneLoaded.</summary>
        internal bool SameSceneHop { get; private set; }

        private async UniTask SwapToPreloadedScene()
        {
            PreloadedSeam seam = _seam;
            _seam = null;
            Scene oldScene = SceneManager.GetActiveScene();
            Vector3 shift = -seam.offset; // add to anything in the old frame to express it in the new one
            Stamp($"seam: swapping to scene {seam.sceneName} (frame shift {shift})");
            SceneManager.SetActiveScene(seam.scene);

            // What the old scene's cameras and listeners were doing, by name: the new scene's take it over.
            Dictionary<string, bool> cameraStates = new Dictionary<string, bool>();
            List<GameObject> carried = new List<GameObject>();
            System.Text.StringBuilder carriedNames = new System.Text.StringBuilder();
            foreach (GameObject root in oldScene.GetRootGameObjects())
            {
                if (!_sceneContentRoots.Contains(root))
                {
                    SceneManager.MoveGameObjectToScene(root, seam.scene);
                    carried.Add(root);
                    carriedNames.Append(root.name).Append(", ");
                    continue;
                }
                foreach (Camera camera in root.GetComponentsInChildren<Camera>(true)) cameraStates[camera.name] = camera.enabled;
                foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true)) cameraStates[listener.name + "#listener"] = listener.enabled;
            }
            // Pooled instances live in the scene they were made in: let the kit rebuild the pools in the new one.
            Assets.ClearPooledObjects();
            AsyncOperation unload = SceneManager.UnloadSceneAsync(oldScene);
            while (unload != null && !unload.isDone)
                await UniTask.Yield();

            // The new scene to its natural origin, the carried old-frame objects (stand-ins, cameras, controller) by the same shift.
            for (int i = 0; i < seam.roots.Count; ++i)
                if (seam.roots[i] != null) seam.roots[i].transform.position += shift;
            for (int i = 0; i < carried.Count; ++i)
                if (carried[i] != null) carried[i].transform.position += shift;
            for (int i = 0; i < seam.disabled.Count; ++i)
            {
                Behaviour behaviour = seam.disabled[i];
                if (behaviour == null) continue;
                string key = behaviour is AudioListener ? behaviour.name + "#listener" : behaviour.name;
                if (behaviour is UnityEngine.EventSystems.EventSystem)
                    behaviour.enabled = FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null; // one event system at a time
                else
                    behaviour.enabled = cameraStates.TryGetValue(key, out bool wasEnabled) && wasEnabled;
            }
            for (int i = 0; i < seam.roots.Count; ++i)
            {
                if (seam.roots[i] == null) continue;
                foreach (Unity.AI.Navigation.NavMeshSurface surface in seam.roots[i].GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true))
                {
                    surface.RemoveData();
                    surface.AddData();
                }
            }
            _sceneContentRoots.Clear();
            for (int i = 0; i < seam.roots.Count; ++i)
                if (seam.roots[i] != null) _sceneContentRoots.Add(seam.roots[i]);
            _sceneContentFromSwap = true; // the kit's scene-loaded hook follows: keep this list, not a fresh snapshot
            _migration?.NoteFrameShift(shift);
            Logging.Log("Mesh", $"Seam: swapped to scene {seam.sceneName} of map {seam.mapId} (frame shift {shift}, {carried.Count} runtime roots carried: {carriedNames})");
        }

    }
}
