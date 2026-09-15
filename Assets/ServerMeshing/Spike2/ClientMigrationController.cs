// Server Meshing add-on — client connection migration (P1).
// See Docs/ServerMeshing/SPIKE2.md and Docs/ServerMeshing/DESIGN.md §11.
//
// EXTENSION-ONLY: a runtime-created MonoBehaviour that moves the client from cell A to cell B without a
// visible break. Two strategies:
//
//  * SEAMLESS (default, "visual stand-ins"): the world keeps rendering. Before StopClient() every visible
//    entity's model is cloned into a script-free stand-in that keeps its animation and is dead-reckoned
//    with the entity's last velocity; the player's controller, gameplay camera and HUD are detached from
//    the dying entity and kept alive (camera follows the local stand-in). When B has spawned the new
//    owned entity the controller is re-bound to it, the stand-ins are retired as their real counterparts
//    appear, and the kit's normal flow resumes. No screenshot, no loading UI, no camera gap.
//
//  * MASK (fallback): hold a screenshot over the swap and fade it out (the original Spike 2 probe).
//
// Both log `[Spike2]` timings; `playerReady` is the gap between StopClient and the new local entity.

using System.Collections;
using System.Collections.Generic;
using Insthync.CameraAndInput;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class ClientMigrationController : MonoBehaviour
    {
        /// <summary>Last measured migration, for automated tests / tooling.</summary>
        public struct Result
        {
            public string address;
            public int port;
            public bool connected;
            public bool playerReady;
            public bool seamless;
            public bool fellBack;           // the hop ended on the cell we came from (the destination refused or never answered)
            public long captureMs, stopMs, connectedMs, playerReadyMs, fadedMs;
            public Vector3 positionBefore, positionAfter;
            public float positionJump;      // real position before vs after (server-side continuity)
            public float resyncJump;        // seamless only: dead-reckoned stand-in vs where B spawned us (what the eye could see)
            public int framesWithoutCamera; // seamless only: frames during the gap with no rendering camera (must be 0)
            public int standIns;
            public bool mountedBefore;      // the character was riding when the handoff started
            public long mountedAfterMs;     // when the new entity was seen riding again (-1 = never during the swap)
        }

        public static Result LastResult;
        public static int CompletedCount;
        /// <summary>Migrations that reached the cell they were sent to only through the fallback (or not at all).</summary>
        public static int FallbackCount;

        private string _prevAddress;
        private int _prevPort;

        /// <summary>
        /// The new cell refused us, dropped us, or never connected: try the cell we came from once. It kept our entity
        /// as a puppet for exactly this (a rolled-back handoff) and promotes it in place when we register there again.
        /// Returns true when a fallback hop was started (the caller extends its wait).
        /// </summary>
        private bool TryFallbackHop(ref string address, ref int port, ref bool fellBack, string why)
        {
            if (fellBack || string.IsNullOrEmpty(_prevAddress))
                return false;
            fellBack = true;
            FallbackCount++;
            Logging.LogWarning("Spike2", $"Migration to {address}:{port} failed ({why}); falling back to {_prevAddress}:{_prevPort}");
            _manager.ClearMigrationDisconnect();
            _manager.StopClient();
            address = _prevAddress;
            port = _prevPort;
            _manager.StartClient(address, port);
            return true;
        }

        private sealed class StandIn
        {
            public GameObject go;
            public Vector3 velocity;
            public bool isLocalPlayer;
            public bool isLocalVehicle;     // the local player's mount: parented to the local stand-in, moves with it
            public BaseGameEntity source;
            public string modelName;
            public float bornAt;
        }

        private sealed class Tracked
        {
            public Vector3 lastPos;
            public Vector3 velocity;
            public float lastTime;
        }

        private MeshMapNetworkManager _manager;
        private bool _running;

        // Velocity tracking (for dead reckoning of stand-ins).
        private readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        private readonly List<int> _tmpRemove = new List<int>();

        // Mask state (fallback strategy; drawn via IMGUI so it sits above uGUI).
        private Texture2D _maskTex;
        private float _maskAlpha;

        public void Setup(MeshMapNetworkManager manager)
        {
            _manager = manager;
        }

        public bool IsRunning => _running;

        private void RunQueuedHop()
        {
            if (string.IsNullOrEmpty(_queuedAddress))
                return;
            string address = _queuedAddress; int port = _queuedPort;
            _queuedAddress = null;
            Logging.Log("Spike2", $"Running the queued hop to {address}:{port}");
            Migrate(address, port);
        }

        // A warp that arrives while a hop is still settling (a character that lands past another cell's line, a
        // rolled-back handoff re-sent the moment the client is back) is queued, not dropped: the server has already
        // unregistered us there, so dropping it would leave the client connected to a cell that no longer owns it.
        private string _queuedAddress;
        private int _queuedPort;

        public void Migrate(string address, int port)
        {
            if (_running)
            {
                Logging.LogWarning("Spike2", $"Migration already in progress; queuing the hop to {address}:{port}.");
                _queuedAddress = address;
                _queuedPort = port;
                return;
            }
            if (_manager == null)
            {
                Logging.LogError("Spike2", "No manager set on ClientMigrationController.");
                return;
            }
            _prevAddress = _manager.networkAddress;
            _prevPort = _manager.networkPort;
            if (_manager.useVisualStandIns)
                StartCoroutine(MigrateSeamlessRoutine(address, port));
            else
                StartCoroutine(MigrateMaskedRoutine(address, port));
        }

        private void Update()
        {
            if (_running || _manager == null || !_manager.IsClientConnected || _manager.Assets == null)
                return;
            TrackVelocities();
        }

        /// <summary>Sample every spawned entity's velocity each frame so a stand-in can keep moving during the gap.</summary>
        private void TrackVelocities()
        {
            float now = Time.unscaledTime;
            Dictionary<uint, LiteNetLibIdentity>.Enumerator it = _manager.Assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                LiteNetLibIdentity identity = it.Current.Value;
                if (identity == null) continue;
                BaseGameEntity entity = identity.GetComponent<BaseGameEntity>();
                if (entity == null) continue;
                int key = entity.GetInstanceID();
                Vector3 pos = entity.EntityTransform.position;
                if (!_tracked.TryGetValue(key, out Tracked t))
                {
                    _tracked[key] = new Tracked { lastPos = pos, lastTime = now };
                    continue;
                }
                float dt = now - t.lastTime;
                if (dt > 0.0001f)
                {
                    Vector3 v = (pos - t.lastPos) / dt;
                    if (v.magnitude > 15f) v = Vector3.zero; // teleport, not motion
                    t.velocity = Vector3.Lerp(t.velocity, v, 0.5f);
                }
                t.lastPos = pos;
                t.lastTime = now;
            }
            // Drop stale entries occasionally.
            if (_tracked.Count > 0 && Time.frameCount % 120 == 0)
            {
                _tmpRemove.Clear();
                foreach (KeyValuePair<int, Tracked> kv in _tracked)
                    if (now - kv.Value.lastTime > 2f) _tmpRemove.Add(kv.Key);
                for (int i = 0; i < _tmpRemove.Count; ++i) _tracked.Remove(_tmpRemove[i]);
            }
        }

        private Vector3 VelocityOf(BaseGameEntity entity)
        {
            return entity != null && _tracked.TryGetValue(entity.GetInstanceID(), out Tracked t) ? t.velocity : Vector3.zero;
        }

        #region Seamless strategy (visual stand-ins)

        private Vector3 _frameShift; // a seam crossing re-bases the world during the hop: old-frame samples are compared in the new frame
        internal void NoteFrameShift(Vector3 shift) { _frameShift += shift; }

        private IEnumerator MigrateSeamlessRoutine(string address, int port)
        {
            _running = true;
            _frameShift = Vector3.zero;
            _inputUsedDuringGap = false;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            BasePlayerCharacterEntity prevEntity = GameInstance.PlayingCharacterEntity;
            Vector3 posBefore = prevEntity != null ? prevEntity.EntityTransform.position : Vector3.zero;
            BasePlayerCharacterController controller = BasePlayerCharacterController.Singleton;

            if (_manager.debugCaptureMigrationFrames)
                StartCoroutine(CaptureFramesRoutine(3.0f));

            // 1) Stand-ins for everything visible, made while the entities still exist.
            float localMoveSpeed = prevEntity != null ? prevEntity.GetMoveSpeed() : 0f;
            bool mountedBefore = prevEntity != null && prevEntity.PassengingVehicleEntity != null;
            long mountedAfterMs = -1;
            List<StandIn> standIns = CreateStandIns(prevEntity);
            StandIn localStandIn = standIns.Find(s => s.isLocalPlayer);
            StandIn localVehicleStandIn = standIns.Find(s => s.isLocalVehicle);
            long tCapture = sw.ElapsedMilliseconds;

            // 2) Keep the controller / camera / HUD alive across the entity's death.
            List<Behaviour> disabledInput = new List<Behaviour>();   // controller scripts: resume as soon as the new entity exists
            List<Behaviour> disabledCamera = new List<Behaviour>();  // camera controllers: resume at the model swap
            List<Behaviour> disabledHud = new List<Behaviour>();     // old HUD scripts: never resumed (HUD is re-created)
            List<FollowCameraControls> cameras = new List<FollowCameraControls>();
            bool keptController = false;
            if (controller != null && prevEntity != null)
            {
                keptController = prevEntity.InstantiatedObjects.Remove(controller.gameObject);
                if (keptController)
                {
                    // Nothing that dereferences PlayingCharacterEntity may tick while it is null.
                    foreach (MonoBehaviour mb in controller.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null || !mb.enabled) continue;
                        mb.enabled = false;
                        if (mb is IGameplayCameraController || mb is IMinimapCameraController) disabledCamera.Add(mb); else disabledInput.Add(mb);
                    }
                    foreach (MonoBehaviour mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                    {
                        if (mb == null || !mb.enabled) continue;
                        if (mb is IGameplayCameraController || mb is IMinimapCameraController) { mb.enabled = false; disabledCamera.Add(mb); }
                    }
                    if (controller.UISceneGameplay != null)
                    {
                        // Freeze the HUD's kit scripts (not the uGUI graphics) so it keeps showing the last real values
                        // instead of reading 0/0 from the dying entity. It is replaced wholesale on re-bind anyway.
                        foreach (MonoBehaviour mb in controller.UISceneGameplay.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null || !mb.enabled || mb is UnityEngine.EventSystems.UIBehaviour) continue;
                            string ns = mb.GetType().Namespace ?? string.Empty;
                            if (!ns.StartsWith("MultiplayerARPG")) continue;
                            mb.enabled = false;
                            disabledHud.Add(mb);
                        }
                    }
                    // The follow camera keeps rendering; point it at the local stand-in for the gap.
                    foreach (FollowCameraControls cam in FindObjectsByType<FollowCameraControls>(FindObjectsSortMode.None))
                    {
                        if (cam == null) continue;
                        cameras.Add(cam);
                        if (localStandIn != null)
                            cam.target = localStandIn.go.transform;
                    }
                }
            }

            // 3) Swap the connection (offline-scene reload suppressed by the manager; Assets.Clear() still runs).
            _manager.BeginMigration();
            _manager.StopClient();
            long tStop = sw.ElapsedMilliseconds;
            _manager.StartClient(address, port);

            // 4) Wait for B: connected, then a NEW owned entity. Dead-reckon the stand-ins meanwhile.
            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            long tConnected = -1;
            int framesWithoutCamera = 0;
            bool fellBack = false;
            BasePlayerCharacterEntity newEntity = null;
            while (true)
            {
                if (Time.realtimeSinceStartup >= deadline || _manager.MigrationDisconnected)
                {
                    // Refused, dropped, or silent: one hop back to where we came from, then give up.
                    if (!TryFallbackHop(ref address, ref port, ref fellBack, _manager.MigrationDisconnected ? _manager.MigrationDisconnectReason.ToString() : "timeout"))
                        break;
                    deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
                    tConnected = -1;
                }
                if (tConnected < 0 && _manager.IsClientConnected)
                    tConnected = sw.ElapsedMilliseconds;
                if (Camera.main == null)
                    framesWithoutCamera++;
                float dt = Time.unscaledDeltaTime;
                // Client-side prediction for the local stand-in: the player's movement keys keep working during the
                // gap (camera-relative, at the character's move speed). Without any input it dead-reckons instead.
                if (localStandIn != null && localStandIn.go != null && localMoveSpeed > 0f)
                {
                    float h = InputManager.GetAxis("Horizontal", false);
                    float v = InputManager.GetAxis("Vertical", false);
                    if (Mathf.Abs(h) + Mathf.Abs(v) > 0.1f)
                    {
                        Transform camT = Camera.main != null ? Camera.main.transform : null;
                        Vector3 fwd = camT != null ? camT.forward : Vector3.forward;
                        Vector3 right = camT != null ? camT.right : Vector3.right;
                        fwd.y = 0f; right.y = 0f; fwd.Normalize(); right.Normalize();
                        Vector3 dir = Vector3.ClampMagnitude(fwd * v + right * h, 1f);
                        localStandIn.velocity = dir * localMoveSpeed;
                        if (dir.sqrMagnitude > 0.001f)
                            localStandIn.go.transform.rotation = Quaternion.RotateTowards(localStandIn.go.transform.rotation, Quaternion.LookRotation(dir), 720f * dt);
                        _inputUsedDuringGap = true;
                    }
                    else if (_inputUsedDuringGap)
                        localStandIn.velocity = Vector3.zero; // keys released: stop, as the real character would
                }
                for (int i = 0; i < standIns.Count; ++i)
                {
                    StandIn s = standIns[i];
                    if (s.go != null && s.velocity.sqrMagnitude > 0.0001f)
                        s.go.transform.position += s.velocity * dt;
                }
                newEntity = FindNewOwnedEntity(prevEntity);
                if (newEntity != null)
                    break;
                yield return null;
            }
            long tPlayerReady = sw.ElapsedMilliseconds;
            if (newEntity == null)
            {
                _queuedAddress = null;
                _manager.FailMigration(); // both cells refused us: the kit's disconnect handling takes over
            }
            if (newEntity != null && newEntity.PassengingVehicleEntity != null)
                mountedAfterMs = tPlayerReady;
            if (newEntity != null && keptController && controller != null)
            {
                // Input resumes NOW on the new entity (it is already walking when the models swap). The HUD and
                // camera re-bind are deferred to the swap so the eye keeps following the stand-in.
                GameInstance.PlayingCharacter = newEntity;
                for (int i = 0; i < disabledInput.Count; ++i)
                    if (disabledInput[i] != null) disabledInput[i].enabled = true;
            }
            bool connected = tConnected >= 0;
            bool playerReady = newEntity != null;
            Vector3 posAfter = playerReady ? newEntity.EntityTransform.position : Vector3.zero;
            float resyncJump = playerReady && localStandIn != null && localStandIn.go != null
                ? Vector3.Distance(localStandIn.go.transform.position, posAfter) : -1f;

            // 5) Blend + settle. B spawned us where it thinks we are; the eye has been following the stand-in. Hide the
            //    real model, glide the stand-in onto the real entity, and wait until B has finished loading the
            //    character's data (stats arrive a few hundred ms after the spawn) so the re-created HUD never shows
            //    0/0 values. Then swap models and re-bind the controller — no pop, no flash.
            GameObject realModel = playerReady && newEntity.Model != null ? newEntity.Model.gameObject : null;
            GameObject realVehicleModel = null;
            if (playerReady && localStandIn != null && localStandIn.go != null)
            {
                if (realModel != null) realModel.SetActive(false);
                float blend = Mathf.Max(0.05f, _manager.standInBlendSeconds);
                float t0 = Time.realtimeSinceStartup;
                Transform st = localStandIn.go.transform;
                while (newEntity != null)
                {
                    float elapsed = Time.realtimeSinceStartup - t0;
                    // A rider's mount arrives a moment after the rider (re-owned or re-summoned by the new cell): keep
                    // the mount stand-in until the real one exists, and hide the real one behind the stand-in meanwhile.
                    bool riding = newEntity.PassengingVehicleEntity != null;
                    if (riding && mountedAfterMs < 0)
                        mountedAfterMs = sw.ElapsedMilliseconds;
                    if (riding && realVehicleModel == null && localVehicleStandIn != null && newEntity.PassengingVehicleEntity.Entity.Model != null)
                    {
                        realVehicleModel = newEntity.PassengingVehicleEntity.Entity.Model.gameObject;
                        realVehicleModel.SetActive(false);
                    }
                    bool dataReady = newEntity.MaxHp > 0 && (!mountedBefore || riding);
                    if (elapsed >= blend && (dataReady || elapsed >= blend + _manager.entityDataWaitSeconds))
                        break;
                    float remaining = Mathf.Max(blend - elapsed, Time.unscaledDeltaTime);
                    Vector3 target = newEntity.EntityTransform.position;
                    float step = Vector3.Distance(st.position, target) / remaining * Time.unscaledDeltaTime;
                    st.position = Vector3.MoveTowards(st.position, target, step);
                    // Ease the pose onto the real MODEL's orientation (it may lean/tilt independently of the entity root).
                    Quaternion targetRot = newEntity.Model != null ? newEntity.Model.transform.rotation : newEntity.EntityTransform.rotation;
                    st.rotation = Quaternion.RotateTowards(st.rotation, targetRot, Quaternion.Angle(st.rotation, targetRot) / remaining * Time.unscaledDeltaTime);
                    yield return null;
                }
            }
            long tSettled = sw.ElapsedMilliseconds;

            // 6) Re-bind the kept controller to the new entity (the kit skips this when a controller already exists),
            //    show the real model, drop the stand-in, resume controller / camera / HUD scripts.
            if (playerReady && keptController && controller != null)
            {
                controller.PlayingCharacterEntity = newEntity;   // Desetup(old) + Setup(new): HUD re-created in place
                if (!newEntity.InstantiatedObjects.Contains(controller.gameObject))
                    newEntity.InstantiatedObjects.Add(controller.gameObject); // normal teardown ownership restored
                ResetNotificationBadges(controller.UISceneGameplay);
            }
            if (realModel != null) realModel.SetActive(true);
            if (realVehicleModel != null) realVehicleModel.SetActive(true);
            if (localStandIn != null && localStandIn.go != null)
            {
                Destroy(localStandIn.go);
                localStandIn.go = null;
            }
            for (int i = 0; i < disabledCamera.Count; ++i)
                if (disabledCamera[i] != null) disabledCamera[i].enabled = true;

            // 7) Retire the other stand-ins as their real entity shows up.
            yield return null;
            float retireDeadline = Time.realtimeSinceStartup + _manager.standInMaxLifetimeSeconds;
            while (Time.realtimeSinceStartup < retireDeadline && RetireMatchedStandIns(standIns) > 0)
                yield return null;
            for (int i = 0; i < standIns.Count; ++i)
                if (standIns[i].go != null) Destroy(standIns[i].go);
            long tFaded = sw.ElapsedMilliseconds;

            if (_manager.MigrationDisconnected)
            {
                // The new cell dropped us after we had arrived (between the entity and the end of the swap): that is
                // the end of the session, not a hop — the kit's handling runs now, and nothing queued can follow.
                playerReady = false;
                _queuedAddress = null;
                _manager.FailMigration();
            }
            _manager.EndMigration();
            sw.Stop();
            _running = false;
            _tracked.Clear();

            LastResult = new Result
            {
                address = address, port = port, connected = connected, playerReady = playerReady, seamless = true, fellBack = fellBack,
                captureMs = tCapture, stopMs = tStop, connectedMs = tConnected, playerReadyMs = tPlayerReady, fadedMs = tFaded,
                positionBefore = posBefore, positionAfter = posAfter,
                positionJump = playerReady ? Vector3.Distance(posBefore + _frameShift, posAfter) : -1f,
                resyncJump = resyncJump, framesWithoutCamera = framesWithoutCamera, standIns = standIns.Count,
            };
            LastResult.mountedBefore = mountedBefore;
            LastResult.mountedAfterMs = mountedAfterMs;
            CompletedCount++;
            RunQueuedHop();
            Logging.Log("Spike2",
                $"Migration to {address}:{port} | SEAMLESS connected={connected} playerReady={playerReady} keptController={keptController} fellBack={fellBack} | " +
                $"standIns={tCapture}ms stop={tStop}ms connected={tConnected}ms playerReady={tPlayerReady}ms settled={tSettled}ms retired={tFaded}ms | " +
                $"pos {posBefore} -> {posAfter} (jump={LastResult.positionJump:0.##}m, resync={resyncJump:0.##}m) | " +
                $"standIns={standIns.Count} framesWithoutCamera={framesWithoutCamera} mounted={(mountedBefore ? (mountedAfterMs >= 0 ? mountedAfterMs + "ms" : "LOST") : "no")}");
        }

        /// <summary>
        /// The kit's notification badges (mail/chat/friend/guild) are active in the HUD prefab with a "99+" placeholder
        /// until their count response arrives, which is visible for ~200 ms on a freshly created HUD. Hide them until
        /// the real count refreshes them.
        /// </summary>
        private static void ResetNotificationBadges(Component hud)
        {
            if (hud == null) return;
            foreach (MonoBehaviour mb in hud.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                System.Reflection.MethodInfo m = mb.GetType().GetMethod("SetNotificationCount", new[] { typeof(int) });
                if (m != null && mb.GetType().Name.EndsWith("Notification"))
                    m.Invoke(mb, new object[] { 0 });
            }
        }

        private bool _inputUsedDuringGap;

        private static int s_captureSeq;

        /// <summary>DEBUG: dump frames around the swap so seamlessness can be checked by eye (every 3rd frame, ~3 s).</summary>
        private IEnumerator CaptureFramesRoutine(float seconds)
        {
            string dir = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, "Logs", "MigrationFrames");
            System.IO.Directory.CreateDirectory(dir);
            int seq = ++s_captureSeq;
            float end = Time.realtimeSinceStartup + seconds;
            int frame = 0;
            while (Time.realtimeSinceStartup < end)
            {
                if (frame % 3 == 0)
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"mig{seq:00}_{frame:0000}_{(int)(Time.realtimeSinceStartup * 1000f) % 100000:00000}.png"));
                frame++;
                yield return null;
            }
        }

        private List<StandIn> CreateStandIns(BasePlayerCharacterEntity localEntity)
        {
            List<StandIn> list = new List<StandIn>();
            if (_manager.Assets == null)
                return list;
            // Instantiate under an inactive parent so no model script runs Awake on the clone; strip the scripts; activate.
            GameObject root = new GameObject("[Mesh] StandIns");
            root.SetActive(false);
            Dictionary<uint, LiteNetLibIdentity>.Enumerator it = _manager.Assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                LiteNetLibIdentity identity = it.Current.Value;
                if (identity == null) continue;
                BaseGameEntity entity = identity.GetComponent<BaseGameEntity>();
                if (entity == null || entity.Model == null) continue;
                Transform src = entity.Model.transform;
                GameObject clone;
                try
                {
                    clone = Instantiate(entity.Model.gameObject, src.position, src.rotation, root.transform);
                }
                catch (System.Exception ex)
                {
                    Logging.LogWarning("Spike2", $"Stand-in clone failed for {entity.name}: {ex.Message}");
                    continue;
                }
                clone.transform.localScale = src.lossyScale;
                foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    Destroy(mb);
                foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
                    Destroy(col);
                clone.name = "[StandIn] " + entity.name;
                list.Add(new StandIn
                {
                    go = clone,
                    source = entity,
                    velocity = VelocityOf(entity),
                    isLocalPlayer = ReferenceEquals(entity, localEntity),
                    modelName = entity.Model.name,
                    bornAt = Time.unscaledTime,
                });
            }
            // The local player's mount rides along with the local stand-in (input-predicted), not on its own velocity.
            StandIn local = list.Find(s => s.isLocalPlayer);
            BaseGameEntity localVehicle = localEntity != null && localEntity.PassengingVehicleEntity != null ? localEntity.PassengingVehicleEntity.Entity : null;
            if (local != null && localVehicle != null)
            {
                StandIn v = list.Find(s => ReferenceEquals(s.source, localVehicle));
                if (v != null && v.go != null)
                {
                    v.go.transform.SetParent(local.go.transform, true);
                    v.velocity = Vector3.zero;
                    v.isLocalVehicle = true;
                }
            }
            root.SetActive(true);
            return list;
        }

        private static BasePlayerCharacterEntity FindNewOwnedEntity(BasePlayerCharacterEntity prevEntity)
        {
            foreach (BasePlayerCharacterEntity e in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
            {
                if (e != null && e.IsOwnerClient && !ReferenceEquals(e, prevEntity))
                    return e;
            }
            return null;
        }

        /// <summary>Destroy stand-ins that have a real entity with the same model within reach. Returns how many are still alive.</summary>
        private int RetireMatchedStandIns(List<StandIn> standIns)
        {
            int alive = 0;
            BaseGameEntity[] entities = FindObjectsByType<BaseGameEntity>(FindObjectsSortMode.None);
            for (int i = 0; i < standIns.Count; ++i)
            {
                StandIn s = standIns[i];
                if (s.go == null) continue;
                Vector3 p = s.go.transform.position;
                bool matched = false;
                for (int j = 0; j < entities.Length; ++j)
                {
                    BaseGameEntity e = entities[j];
                    if (e == null || e.Model == null || e.Model.name != s.modelName) continue;
                    if ((e.EntityTransform.position - p).sqrMagnitude <= _manager.standInMatchDistance * _manager.standInMatchDistance) { matched = true; break; }
                }
                if (matched) { Destroy(s.go); s.go = null; }
                else alive++;
            }
            return alive;
        }

        #endregion

        #region Mask strategy (fallback)

        private IEnumerator MigrateMaskedRoutine(string address, int port)
        {
            _running = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            BasePlayerCharacterEntity prevEntity = GameInstance.PlayingCharacterEntity;
            Vector3 posBefore = prevEntity != null ? prevEntity.EntityTransform.position : Vector3.zero;

            yield return new WaitForEndOfFrame();
            DisposeMask();
            _maskTex = ScreenCapture.CaptureScreenshotAsTexture();
            _maskAlpha = 1f;
            long tCapture = sw.ElapsedMilliseconds;

            _manager.BeginMigration();
            _manager.StopClient();
            long tStop = sw.ElapsedMilliseconds;
            _manager.StartClient(address, port);

            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            bool fellBack = false;
            long tConnected = -1;
            while (true)
            {
                if (Time.realtimeSinceStartup >= deadline || _manager.MigrationDisconnected)
                {
                    if (!TryFallbackHop(ref address, ref port, ref fellBack, _manager.MigrationDisconnected ? _manager.MigrationDisconnectReason.ToString() : "timeout"))
                        break;
                    deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
                    tConnected = -1;
                }
                if (tConnected < 0 && _manager.IsClientConnected)
                    tConnected = sw.ElapsedMilliseconds;
                if (GameInstance.PlayingCharacterEntity != null && !ReferenceEquals(GameInstance.PlayingCharacterEntity, prevEntity))
                    break;
                yield return null;
            }
            bool connected = tConnected >= 0;
            long tPlayerReady = sw.ElapsedMilliseconds;
            bool playerReady = GameInstance.PlayingCharacterEntity != null && !ReferenceEquals(GameInstance.PlayingCharacterEntity, prevEntity);
            if (!playerReady)
            {
                _queuedAddress = null;
                _manager.FailMigration();
            }
            Vector3 posAfter = playerReady ? GameInstance.PlayingCharacterEntity.EntityTransform.position : Vector3.zero;

            yield return null;
            yield return null;
            float fade = Mathf.Max(0.01f, _manager.migrationMaskFadeSeconds);
            float t = 0f;
            while (t < fade)
            {
                t += Time.unscaledDeltaTime;
                _maskAlpha = Mathf.Lerp(1f, 0f, t / fade);
                yield return null;
            }
            _maskAlpha = 0f;
            DisposeMask();
            long tFaded = sw.ElapsedMilliseconds;

            _manager.EndMigration();
            sw.Stop();
            _running = false;

            LastResult = new Result
            {
                address = address, port = port, connected = connected, playerReady = playerReady, seamless = false, fellBack = fellBack,
                captureMs = tCapture, stopMs = tStop, connectedMs = tConnected, playerReadyMs = tPlayerReady, fadedMs = tFaded,
                positionBefore = posBefore, positionAfter = posAfter,
                positionJump = playerReady ? Vector3.Distance(posBefore + _frameShift, posAfter) : -1f, resyncJump = -1f,
            };
            CompletedCount++;
            RunQueuedHop();
            Logging.Log("Spike2",
                $"Migration to {address}:{port} | MASK connected={connected} playerReady={playerReady} | " +
                $"capture={tCapture}ms stop={tStop}ms connected={tConnected}ms playerReady={tPlayerReady}ms faded={tFaded}ms | " +
                $"pos {posBefore} -> {posAfter} (jump={LastResult.positionJump:0.##}m)");
        }

        private void OnGUI()
        {
            if (_maskTex == null || _maskAlpha <= 0f)
                return;
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _maskAlpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _maskTex, ScaleMode.StretchToFill, false);
            GUI.color = prev;
        }

        private void DisposeMask()
        {
            if (_maskTex != null)
            {
                Destroy(_maskTex);
                _maskTex = null;
            }
        }

        private void OnDestroy()
        {
            DisposeMask();
        }

        #endregion
    }
}
