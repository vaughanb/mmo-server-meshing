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
        [Tooltip("Unity's background loading priority while a neighbour scene pre-loads: how much of each frame its objects' integration may take (Low about 2 ms, Normal about 10 ms, High the whole frame). The pre-load has the band to finish in.")]
        public UnityEngine.ThreadPriority seamPreloadPriority = UnityEngine.ThreadPriority.Low;

        private sealed class PreloadedSeam
        {
            public string sceneName, mapId, cellId;
            public Vector3 offset;                 // where the neighbour's content sits in the current frame (a swap to another seam moves it with the frame)
            public Scene scene;
            public bool loading, loaded;
            public readonly List<GameObject> roots = new List<GameObject>();
            public readonly List<Behaviour> disabled = new List<Behaviour>(); // scene cameras / listeners / event systems silenced while pre-loaded; at the swap they take the old scene's state by name
            public readonly List<Behaviour> silenced = new List<Behaviour>(); // the scene's lights and its own scripts, off while pre-loaded, back on at the swap
        }

        // One pre-load per neighbouring map's scene within the band (DESIGN 1.41): at a corner where three maps meet the
        // player stands in two seams' bands at once, and whichever line is crossed first finds its scene ready. Until 1.40
        // one scene was held, the nearest seam's.
        private readonly List<PreloadedSeam> _seams = new List<PreloadedSeam>();
        private readonly Dictionary<string, MeshCell> _seamNearestByScene = new Dictionary<string, MeshCell>();
        private readonly Dictionary<string, float> _seamDistanceByScene = new Dictionary<string, float>();
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
            {
                // The kit makes its pooled instances in the active scene before this runs (InitPoolingQueues comes first):
                // they are runtime objects, so a swap carries them with the rest instead of unloading them (DESIGN 1.58).
                if (root.TryGetComponent(out LiteNetLibIdentity identity) && identity.IsPooledInstance)
                    continue;
                _sceneContentRoots.Add(root);
            }
        }

        /// <summary>Client tick: pre-load every neighbouring map's scene near a seam, drop each when the player walks away from its seam.</summary>
        private void SeamClientUpdate()
        {
            for (int i = _seams.Count - 1; i >= 0; --i)
                if (!_seams[i].loading && (!_seams[i].scene.IsValid() || !_seams[i].scene.isLoaded))
                    _seams.RemoveAt(i); // the kit reloaded the world (session end, plain warp): the pre-load went with it
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
            // The nearest cell of every other map's scene among the neighbours: a corner where three maps meet has two.
            _seamNearestByScene.Clear();
            _seamDistanceByScene.Clear();
            for (int i = 0; i < cell.neighbours.Count; ++i)
            {
                MeshCell neighbour = cell.neighbours[i];
                if (string.Equals(neighbour.mapId, cell.mapId))
                    continue;
                string sceneName = SceneNameOf(neighbour.mapId);
                if (string.IsNullOrEmpty(sceneName) || string.Equals(sceneName, activeScene))
                    continue; // same scene: nothing to pre-load, the swap inside a scene needs no frame change
                float distance = cell.DistanceTo(neighbour, pos);
                if (!_seamDistanceByScene.TryGetValue(sceneName, out float nearest) || distance < nearest)
                {
                    _seamDistanceByScene[sceneName] = distance;
                    _seamNearestByScene[sceneName] = neighbour;
                }
            }
            for (int i = _seams.Count - 1; i >= 0; --i)
            {
                PreloadedSeam seam = _seams[i];
                if (seam.loading)
                    continue;
                if (!_seamDistanceByScene.TryGetValue(seam.sceneName, out float distance) || distance > seamPreloadDistance + seamUnloadHysteresis)
                    UnloadSeam(seam, "left the band");
            }
            foreach (KeyValuePair<string, float> entry in _seamDistanceByScene)
            {
                if (entry.Value > seamPreloadDistance || FindSeam(entry.Key) != null)
                    continue;
                MeshCell neighbour = _seamNearestByScene[entry.Key];
                PreloadSeam(neighbour, neighbour.OffsetTo(cell)).Forget();
            }
        }

        private PreloadedSeam FindSeam(string sceneName)
        {
            for (int i = 0; i < _seams.Count; ++i)
                if (string.Equals(_seams[i].sceneName, sceneName))
                    return _seams[i];
            return null;
        }

        private static void ReAddNavMesh(List<GameObject> roots)
        {
            for (int i = 0; i < roots.Count; ++i)
            {
                if (roots[i] == null) continue;
                foreach (Unity.AI.Navigation.NavMeshSurface surface in roots[i].GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true))
                {
                    surface.RemoveData();
                    surface.AddData(); // at the moved transform
                }
            }
        }

        private static string SceneNameOf(string mapId)
        {
            return GameInstance.MapInfos.TryGetValue(mapId, out BaseMapInfo info) && info != null && info.Scene != null ? info.Scene.SceneName : null;
        }

        private async UniTaskVoid PreloadSeam(MeshCell neighbour, Vector3 offset)
        {
            string sceneName = SceneNameOf(neighbour.mapId);
            PreloadedSeam seam = new PreloadedSeam { sceneName = sceneName, mapId = neighbour.mapId, cellId = neighbour.id, offset = offset, loading = true };
            _seams.Add(seam);
            Stamp($"seam: pre-loading scene {sceneName} of map {neighbour.mapId} at offset {offset} (cell {neighbour.id})");
            // The scene's integration (its objects made on the main thread) is spread over frames at this priority: Low gives
            // it about 2 ms a frame. At the default, an 8 km map's 2,300 objects integrated in one 100 ms frame (DESIGN 1.40),
            // while the player walked. The pre-load has the whole band to finish in.
            UnityEngine.ThreadPriority loadingPriority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = seamPreloadPriority;
            AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op == null)
            {
                Application.backgroundLoadingPriority = loadingPriority;
                Logging.LogWarning("Mesh", $"Seam: scene {sceneName} (map {neighbour.mapId}) is not in the build; the kit will reload at the crossing");
                _seams.Remove(seam);
                return;
            }
            float loadBegan = Time.realtimeSinceStartup;
            int loadFrames = 0;
            while (!op.isDone)
            {
                await UniTask.Yield();
                loadFrames++;
            }
            Application.backgroundLoadingPriority = loadingPriority;
            seam.scene = SceneManager.GetSceneByName(sceneName);
            seam.loading = false;
            if (!seam.scene.IsValid() || !seam.scene.isLoaded)
            {
                _seams.Remove(seam);
                Logging.LogWarning("Mesh", $"Seam: scene {sceneName} did not load");
                return;
            }
            if (!_seams.Contains(seam))
            {
                _ = SceneManager.UnloadSceneAsync(seam.scene); // dropped while loading (the kit reloaded the world); nothing waits for the unload
                return;
            }
            offset = seam.offset; // a swap to another seam while this one loaded moved the frame, and the offset with it
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
                // A pre-loaded scene is scenery (DESIGN 1.40, the 8 km maps): its lights would be a second sun over the strip,
                // and its own scripts (a sky cycle writing the render settings and turning its sun, a boundary detector reading
                // the player against the wrong frame) would run against the active scene's. Both wait for the swap. The navmesh
                // surface stays: its data is re-added at the offset below. Networked objects are the kit's (deactivated next).
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                    if (light.enabled) { light.enabled = false; seam.silenced.Add(light); }
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || !behaviour.enabled || behaviour is Unity.AI.Navigation.NavMeshSurface || behaviour is UnityEngine.EventSystems.EventSystem
                        || behaviour.GetComponentInParent<LiteNetLibIdentity>(true) != null)
                        continue;
                    behaviour.enabled = false;
                    seam.silenced.Add(behaviour);
                }
                // Networked scene objects wait for the server that owns them (the kit deactivates them the same way when it
                // registers a scene); until the swap the neighbour's live entities are its ghosts.
                foreach (LiteNetLibIdentity identity in root.GetComponentsInChildren<LiteNetLibIdentity>(true))
                    if (identity.gameObject.activeSelf) identity.gameObject.SetActive(false);
            }
            ReAddNavMesh(seam.roots);
            seam.loaded = true;
            System.Text.StringBuilder rootNames = new System.Text.StringBuilder();
            for (int i = 0; i < seam.roots.Count; ++i)
                rootNames.Append(i > 0 ? ", " : "").Append(seam.roots[i] != null ? seam.roots[i].name : "null");
            Logging.Log("Mesh", $"Seam: pre-loaded scene {sceneName} of map {neighbour.mapId} at offset {offset} in {(Time.realtimeSinceStartup - loadBegan) * 1000f:0} ms over {loadFrames} frames " +
                               $"({seam.roots.Count} roots: {rootNames}; {seam.silenced.Count} lights and scripts silenced)");
        }

        private void UnloadSeam(PreloadedSeam seam, string reason)
        {
            if (seam == null || !_seams.Remove(seam))
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
            PreloadedSeam ready = IsServer ? null : FindSeam(serverSceneInfo.sceneName);
            if (ready != null && ready.loaded && !ready.loading && ready.scene.IsValid() && ready.scene.isLoaded
                && !string.Equals(SceneManager.GetActiveScene().name, serverSceneInfo.sceneName))
            {
                await SwapToPreloadedScene(ready);
                swapped = true;
                Stamp("seam: swapped; the kit's scene change follows");
            }
            // A hop between cells of one scene: the scene the kit is about to "load" is the one already active and
            // registered, so OnClientOnlineSceneLoaded skips the scene-entity registration (and its pool clear). Only while
            // the assets are still registered: the stand-in swap's StopClient cleared them (DESIGN 1.29: a dead destination's
            // fallback skipped the registration too, nothing could spawn, and the client never got its character back).
            SameSceneHop = !swapped && IsMigrating && !IsServer && Assets.IsInitialized && string.Equals(SceneManager.GetActiveScene().name, serverSceneInfo.sceneName);
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

        private static System.Reflection.FieldInfo s_pooledObjects;
        private static bool s_pooledObjectsMissing;

        /// <summary>
        /// DropDestroyedPooledInstances: a pooled instance the swap did not carry (one a game parented under the old scene's
        /// content) went with the unload, and the kit would hand the dead object out of its queue. The kit keeps its pools
        /// in an internal field of LiteNetLibAssets, so this reaches it by name and drops what is destroyed; a kit without
        /// the field gets the pre-1.58 behaviour, its pools cleared for InitPoolingQueues to remake. Returns the instances
        /// dropped, or -1 when the pools were cleared instead.
        /// </summary>
        private int DropDestroyedPooledInstances()
        {
            if (!s_pooledObjectsMissing && s_pooledObjects == null)
            {
                s_pooledObjects = typeof(LiteNetLibAssets).GetField("PooledObjects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                s_pooledObjectsMissing = s_pooledObjects == null;
                if (s_pooledObjectsMissing)
                    Logging.LogWarning("Mesh", "Seam: the kit's LiteNetLibAssets has no PooledObjects field; a swap clears the pools for the kit to remake (DropDestroyedPooledInstances)");
            }
            Dictionary<int, Queue<LiteNetLibIdentity>> pools = s_pooledObjects != null ? s_pooledObjects.GetValue(Assets) as Dictionary<int, Queue<LiteNetLibIdentity>> : null;
            if (pools == null)
            {
                Assets.ClearPooledObjects();
                return -1;
            }
            int dropped = 0;
            foreach (Queue<LiteNetLibIdentity> queue in pools.Values)
            {
                int count = queue.Count;
                for (int i = 0; i < count; ++i)
                {
                    LiteNetLibIdentity instance = queue.Dequeue();
                    if (instance != null)
                        queue.Enqueue(instance);
                    else
                        dropped++;
                }
            }
            return dropped;
        }

        /// <summary>The arrived scene's own roots, set by a swap for the kit's scene-loaded hook that follows (MeshRegisterSceneEntities); null otherwise.</summary>
        private List<GameObject> _swapArrivedRoots;

        private static System.Reflection.FieldInfo s_groundedTransform;
        private static bool s_groundedTransformMissing;

        /// <summary>
        /// ForgetPlatforms: the kit rides whatever a character stands on as a moving platform — the collider its controller
        /// last hit below it, and that transform's displacement each frame. The swap moves the ground under the carried
        /// characters by the frame shift, and the kit would then carry them along once more: two of three east-to-west swaps
        /// put the player 8,192 m past the seam, by which of two coplanar colliders (the leaving scene's apron, destroyed with
        /// it, or the arriving scene's terrain, shifted) the controller had hit last (DESIGN 1.40). Forgetting the platform
        /// makes the next step find the ground afresh, with no displacement to ride. The kit keeps that transform in a private
        /// field of its built-in movement functions, so this reaches it by name; a kit without it is left alone.
        /// </summary>
        private static int ForgetPlatforms(List<GameObject> carried)
        {
            if (s_groundedTransformMissing)
                return 0;
            if (s_groundedTransform == null)
            {
                s_groundedTransform = typeof(BuiltInEntityMovementFunctions3D).GetField("_groundedTransform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (s_groundedTransform == null)
                {
                    s_groundedTransformMissing = true;
                    Logging.LogWarning("Mesh", "Seam: the kit's movement has no _groundedTransform field; a carried character may ride the frame shift once more (see ForgetPlatforms)");
                    return 0;
                }
            }
            int forgotten = 0;
            for (int i = 0; i < carried.Count; ++i)
            {
                if (carried[i] == null)
                    continue;
                foreach (IEntityMovementComponent movement in carried[i].GetComponentsInChildren<IEntityMovementComponent>(true))
                {
                    BuiltInEntityMovementFunctions3D functions = movement is CharacterControllerEntityMovement controller ? controller.Functions : null;
                    if (functions == null || s_groundedTransform.GetValue(functions) == null)
                        continue;
                    s_groundedTransform.SetValue(functions, null);
                    forgotten++;
                }
            }
            return forgotten;
        }

        /// <summary>
        /// TraceOwnPlayerFrames stamps where the own character ends the next few frames after a swap (DESIGN 1.40): the
        /// carry's stamp says where it was put, these say whether anything moved it again before the hop settled.
        /// </summary>
        private async UniTaskVoid TraceOwnPlayerFrames(int frames)
        {
            for (int i = 0; i < frames; ++i)
            {
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
                BasePlayerCharacterEntity own = GameInstance.PlayingCharacterEntity;
                Stamp(own == null ? $"seam: frame +{i + 1}: no own player"
                    : $"seam: frame +{i + 1}: own player {own.ObjectId} at {own.EntityTransform.position} (kept {own.Identity.IsKept}, owner {own.IsOwnerClient})");
            }
        }

        private async UniTask SwapToPreloadedScene(PreloadedSeam seam)
        {
            _seams.Remove(seam);
            Scene oldScene = SceneManager.GetActiveScene();
            Vector3 shift = -seam.offset; // add to anything in the old frame to express it in the new one
            Stamp($"seam: swapping to scene {seam.sceneName} (frame shift {shift})");
            System.Diagnostics.Stopwatch swapWatch = System.Diagnostics.Stopwatch.StartNew();
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
            double carriedMs = swapWatch.Elapsed.TotalMilliseconds;
            AsyncOperation unload = SceneManager.UnloadSceneAsync(oldScene);
            double unloadCallMs = swapWatch.Elapsed.TotalMilliseconds;
            int unloadFrames = 0;
            while (unload != null && !unload.isDone)
            {
                await UniTask.Yield();
                unloadFrames++;
            }
            // The kit's pooled instances were carried (SnapshotSceneContent leaves them out of the scene's content), so the
            // pools stay and the kit's InitPoolingQueues after the swap finds every queue made. Until 1.58 they were cleared
            // here and that re-instantiation was most of the frame after the swap (43 of 75 ms on the 8 km demo).
            int poolsDropped = DropDestroyedPooledInstances();
            Stamp($"seam: swap: {carried.Count} roots carried in {carriedMs:0.#} ms, unload called by {unloadCallMs:0.#}, unloaded after {unloadFrames} frame(s), " +
                  (poolsDropped < 0 ? "pools cleared (no pool field)" : $"pools kept ({poolsDropped} destroyed instance(s) dropped)"));
            _swapArrivedRoots = seam.roots;
            swapWatch.Restart();

            // The new scene to its natural origin, the carried old-frame objects (stand-ins, cameras, controller) by the same shift.
            BasePlayerCharacterEntity own = GameInstance.PlayingCharacterEntity;
            Transform ownTransform = own != null ? own.EntityTransform : null;
            if (ownTransform != null)
                Stamp($"seam: own player before the carry at {ownTransform.position} (root {ownTransform.root.name}, scene {ownTransform.gameObject.scene.name}, carried {carried.Contains(ownTransform.root.gameObject)})");
            for (int i = 0; i < seam.roots.Count; ++i)
                if (seam.roots[i] != null) seam.roots[i].transform.position += shift;
            for (int i = 0; i < carried.Count; ++i)
                if (carried[i] != null) carried[i].transform.position += shift;
            int platformsForgotten = ForgetPlatforms(carried);
            if (ownTransform != null)
                Stamp($"seam: own player after the carry at {ownTransform.position} ({platformsForgotten} platform(s) forgotten)");
            TraceOwnPlayerFrames(3).Forget();
            // Any other pre-loaded seam (a corner, DESIGN 1.41) was placed in the old frame: its content moves by the same
            // shift, and its offset is read in the new frame from here on. One still loading lands at the moved offset.
            int othersShifted = 0;
            for (int i = 0; i < _seams.Count; ++i)
            {
                PreloadedSeam other = _seams[i];
                other.offset += shift;
                if (!other.loaded)
                    continue;
                for (int r = 0; r < other.roots.Count; ++r)
                    if (other.roots[r] != null) other.roots[r].transform.position += shift;
                ReAddNavMesh(other.roots);
                othersShifted++;
            }
            double shiftedMs = swapWatch.Elapsed.TotalMilliseconds;
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
            // The scene's lights and scripts come back as they were authored: this is their scene now.
            for (int i = 0; i < seam.silenced.Count; ++i)
                if (seam.silenced[i] != null) seam.silenced[i].enabled = true;
            ReAddNavMesh(seam.roots);
            _sceneContentRoots.Clear();
            for (int i = 0; i < seam.roots.Count; ++i)
                if (seam.roots[i] != null) _sceneContentRoots.Add(seam.roots[i]);
            _sceneContentFromSwap = true; // the kit's scene-loaded hook follows: keep this list, not a fresh snapshot
            _migration?.NoteFrameShift(shift);
            Stamp($"seam: swap: roots and carried shifted in {shiftedMs:0.#} ms ({othersShifted} other pre-load(s) moved), scripts back and navmesh re-added by {swapWatch.Elapsed.TotalMilliseconds:0.#} ms");
            Logging.Log("Mesh", $"Seam: swapped to scene {seam.sceneName} of map {seam.mapId} (frame shift {shift}, {carried.Count} runtime roots carried: {carriedNames}{(othersShifted > 0 ? $"; {othersShifted} other pre-loaded scene(s) moved with the frame" : "")})");
        }

    }
}
