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
            public bool preconnected;       // continuity only: the hop used a connection opened ahead of it (DESIGN 1.03)
        }

        /// <summary>Continuity hops that used a pre-connected transport, for automated tests / tooling.</summary>
        public static int PreconnectedHops;

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
            _manager.StopClient();
            _manager.BeginMigrationAttempt();
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

        /// <summary>
        /// A warp that arrives while we still wait for the new cell's character (that cell seated us and handed us on at
        /// once, so our character there came and went unseen) is acted on now: the server already moved us, so the cell
        /// that sent it is the one we come from for the next leg, and the wait restarts toward the queued destination.
        /// Before, the wait ran out, the queued hop was dropped and the session was lost. Returns true when it redirected.
        /// </summary>
        private bool TryRedirectToQueuedHop(ref string address, ref int port, ref bool legFellBack)
        {
            if (string.IsNullOrEmpty(_queuedAddress))
                return false;
            Logging.LogWarning("Spike2", $"Hop to {address}:{port} redirected to {_queuedAddress}:{_queuedPort} before the character arrived there");
            _prevAddress = address;
            _prevPort = port;
            address = _queuedAddress;
            port = _queuedPort;
            _queuedAddress = null;
            legFellBack = false; // a new leg: the cell that redirected us keeps our puppet, so it is the fallback now
            _manager.StopClient();
            _manager.BeginMigrationAttempt();
            _manager.StartClient(address, port);
            return true;
        }

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
            if (_manager.useEntityContinuity)
                StartCoroutine(MigrateContinuousRoutine(address, port));
            else if (_manager.useVisualStandIns)
                StartCoroutine(MigrateSeamlessRoutine(address, port));
            else
                StartCoroutine(MigrateMaskedRoutine(address, port));
        }

        private bool _warmedUp;

        private void Update()
        {
            if (_manager != null)
                PollPreconnect();
            if (_running || _manager == null || !_manager.IsClientConnected || _manager.Assets == null)
                return;
            if (!_warmedUp && _manager.warmUpMigrationCode && MeshMigrationWarmup.IsReady(GameInstance.PlayingCharacterEntity))
            {
                // Once per session, as soon as the character's data is in: the first hop then finds the adoption's
                // read already compiled (CE-14, DESIGN 1.02).
                _warmedUp = true;
                MeshMigrationWarmup.Run(_manager.Assets, GameInstance.PlayingCharacterEntity);
            }
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
                    // Smoothed over ~0.1 s, not per frame: a remote character's motion comes in logic ticks, and at a few
                    // milliseconds a frame a per-frame blend followed each tick's pause (DESIGN 1.08).
                    t.velocity = Vector3.Lerp(t.velocity, v, 1f - Mathf.Exp(-dt / VelocitySmoothingSeconds));
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

        private const float VelocitySmoothingSeconds = 0.1f;

        private Vector3 VelocityOf(BaseGameEntity entity)
        {
            return entity != null && _tracked.TryGetValue(entity.GetInstanceID(), out Tracked t) ? t.velocity : Vector3.zero;
        }

        #region Seamless strategy (visual stand-ins)

        private Vector3 _frameShift; // a seam crossing re-bases the world during the hop: old-frame samples are compared in the new frame
        internal void NoteFrameShift(Vector3 shift) { _frameShift += shift; }

        private IEnumerator MigrateSeamlessRoutine(string address, int port, bool isFallback = false)
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

            // 2) Keep the controller / camera / HUD alive across the entity's death. The kit (v1.1.4+) keeps the controller
            //    out of the entity's InstantiatedObjects, so it outlives the entity by itself; it only has to be frozen.
            List<Behaviour> disabledInput = new List<Behaviour>();   // controller scripts: resume as soon as the new entity exists
            List<Behaviour> disabledCamera = new List<Behaviour>();  // camera controllers: resume at the model swap
            List<Behaviour> disabledHud = new List<Behaviour>();     // old HUD scripts: never resumed (HUD is re-created)
            List<FollowCameraControls> cameras = new List<FollowCameraControls>();
            Transform cameraAnchor = null;
            bool keptController = controller != null && prevEntity != null;
            bool reboundByKit = false; // the kit re-binds the controller itself when the new owned entity spawns
            System.Action<BasePlayerCharacterController> onControllerSetup = c => reboundByKit = true;
            if (keptController)
            {
                controller.onSetup += onControllerSetup;
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
                // The follow cameras keep rendering; they follow an anchor of our own for the gap. It starts exactly
                // on the camera target they had, rides on the local stand-in, and never goes null when the entity
                // dies (a null target makes FollowCamera add its target offset to the last position every frame).
                Transform oldTarget = controller.CameraTargetTransform; // what the camera controllers assign (view-mode aware)
                if (oldTarget == null) oldTarget = prevEntity.EntityTransform;
                cameraAnchor = new GameObject("[Mesh] CameraAnchor").transform;
                cameraAnchor.SetPositionAndRotation(oldTarget.position, oldTarget.rotation);
                if (localStandIn != null && localStandIn.go != null)
                    cameraAnchor.SetParent(localStandIn.go.transform, true);
                foreach (FollowCameraControls cam in FindObjectsByType<FollowCameraControls>(FindObjectsSortMode.None))
                {
                    if (cam == null) continue;
                    if (cam.target != null && cam.target != oldTarget && !IsPartOf(cam.target, prevEntity)) continue;
                    cameras.Add(cam);
                    cam.target = cameraAnchor;
                }
            }

            // 3) Swap the connection (offline-scene reload suppressed by the manager; Assets.Clear() still runs).
            _manager.BeginMigration();
            _manager.StopClient();
            _manager.BeginMigrationAttempt();
            long tStop = sw.ElapsedMilliseconds;
            _manager.StartClient(address, port);

            // 4) Wait for B: connected, then a NEW owned entity. Dead-reckon the stand-ins meanwhile.
            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            long tConnected = -1;
            int framesWithoutCamera = 0;
            bool fellBack = isFallback; // a continuity hop that fell back here is already the fallback leg
            bool legFellBack = isFallback; // the fallback is per leg: a redirect (TryRedirectToQueuedHop) starts a new one
            BasePlayerCharacterEntity newEntity = null;
            while (true)
            {
                if (TryRedirectToQueuedHop(ref address, ref port, ref legFellBack))
                {
                    deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
                    tConnected = -1;
                }
                if (Time.realtimeSinceStartup >= deadline || _manager.MigrationDisconnected || _manager.MigrationRefused)
                {
                    // Refused, dropped, or silent: one hop back to where we came from, then give up. A refusal is
                    // acted on the moment it arrives; the deadline is only for a cell that never answers.
                    string why = _manager.MigrationDisconnected ? _manager.MigrationDisconnectReason.ToString() : _manager.MigrationRefused ? "refused" : "timeout";
                    if (!TryFallbackHop(ref address, ref port, ref legFellBack, why))
                        break;
                    fellBack = true;
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
                // Input resumes NOW on the new entity (it is already walking when the models swap). The camera
                // controllers stay off until the swap so the eye keeps following the anchor on the stand-in.
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
            Transform st = localStandIn != null && localStandIn.go != null ? localStandIn.go.transform : null;
            // The camera anchor glides on its own onto the camera target the controller will hand the cameras at the swap,
            // so that hand-over lands on the same point (riding the stand-in would leave its model-to-target offset).
            if (cameraAnchor != null) cameraAnchor.SetParent(null, true);
            if (playerReady && (st != null || cameraAnchor != null))
            {
                if (realModel != null && st != null) realModel.SetActive(false);
                float blend = Mathf.Max(0.05f, _manager.standInBlendSeconds);
                float t0 = Time.realtimeSinceStartup;
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
                    if (st != null)
                    {
                        // Glide onto the real MODEL's pose (it may sit, lean or tilt independently of the entity root).
                        Transform modelT = newEntity.Model != null ? newEntity.Model.transform : newEntity.EntityTransform;
                        GlideTowards(st, modelT, remaining);
                    }
                    Transform newTarget = cameraAnchor != null ? controller.CameraTargetTransform : null;
                    if (newTarget != null)
                        GlideTowards(cameraAnchor, newTarget, remaining);
                    yield return null;
                }
            }
            long tSettled = sw.ElapsedMilliseconds;

            // 6) Re-bind the kept controller to the new entity (the kit already did when the owned entity spawned; this
            //    covers a kit that does not), show the real model, drop the stand-in, hand the cameras the real camera
            //    target the anchor now sits on, resume controller / camera / HUD scripts.
            if (keptController && controller != null)
                controller.onSetup -= onControllerSetup;
            if (playerReady && keptController && controller != null)
            {
                if (!reboundByKit)
                    controller.PlayingCharacterEntity = newEntity;   // Desetup(old) + Setup(new): HUD re-created in place
                ResetNotificationBadges(controller.UISceneGameplay);
                Transform newTarget = controller.CameraTargetTransform;
                for (int i = 0; i < cameras.Count; ++i)
                    if (cameras[i] != null && newTarget != null) cameras[i].target = newTarget;
            }
            if (realModel != null) realModel.SetActive(true);
            if (realVehicleModel != null) realVehicleModel.SetActive(true);
            if (localStandIn != null && localStandIn.go != null)
            {
                Destroy(localStandIn.go);
                localStandIn.go = null;
            }
            if (cameraAnchor != null && playerReady)
                Destroy(cameraAnchor.gameObject); // on a failed hop the cameras keep it until the kit tears them down
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
                // A model animated by a script (the kit's PlayableCharacterModel) would freeze once stripped: keep its clips playing.
                MeshStandInAnimation.Attach(entity.Model.gameObject, clone);
                // The footsteps play from a script on the entity too: keep them stepping on the stand-in.
                MeshStandInFootsteps.Attach(entity, clone);
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

        /// <summary>Move and turn <paramref name="t"/> so it lands on <paramref name="target"/>'s pose in <paramref name="remaining"/> seconds.</summary>
        private static void GlideTowards(Transform t, Transform target, float remaining)
        {
            float k = Time.unscaledDeltaTime / remaining;
            t.position = Vector3.MoveTowards(t.position, target.position, Vector3.Distance(t.position, target.position) * k);
            t.rotation = Quaternion.RotateTowards(t.rotation, target.rotation, Quaternion.Angle(t.rotation, target.rotation) * k);
        }

        /// <summary>The transform belongs to the entity or to the vehicle it rides.</summary>
        private static bool IsPartOf(Transform t, BasePlayerCharacterEntity entity)
        {
            if (t == null || entity == null) return false;
            if (t.IsChildOf(entity.EntityTransform)) return true;
            BaseGameEntity vehicle = entity.PassengingVehicleEntity != null ? entity.PassengingVehicleEntity.Entity : null;
            return vehicle != null && t.IsChildOf(vehicle.EntityTransform);
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

        #region Pre-connect (DESIGN 1.03)

        // The idle connection to the cell we are approaching, opened on the cell's hint (MeshMapNetworkManager_Preconnect)
        // and handed to the continuity hop when it goes to that very address. Its connect event is consumed here, so
        // LiteNetLibManager.MigrateClient(address, port, transport) raises the manager's connected event itself.
        private sealed class WarmConnection
        {
            public ITransport transport;
            public string address;
            public int port;
            public string cellId;
            public bool connected;
            public float startedAt;
            public long buildMs;
        }

        private WarmConnection _warm;

        [Tooltip("A pre-connect that has not connected after this many seconds is dropped (the hop then connects as before).")]
        public float preconnectTimeoutSeconds = 5f;

        /// <summary>The cell we are on says we approach <paramref name="cellId"/>: hold a connection to it. Idempotent for the same address.</summary>
        public void Preconnect(string address, int port, string cellId)
        {
            // Also while a hop settles: the hint is the new cell's (the one we left stopped ticking for us), and a cell
            // sends it once per change, so a dropped hint would never come again.
            if (_warm != null && _warm.address == address && _warm.port == port)
                return;
            DropPreconnect("another cell is nearer now");
            if (_manager.TransportFactory == null)
            {
                Logging.LogWarning("Spike2", "Pre-connect: no transport factory yet; the hop will connect as before");
                return;
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            ITransport transport = _manager.TransportFactory.Build();
            if (transport == null || !transport.StartClient(address, port))
            {
                transport?.Destroy();
                Logging.LogWarning("Spike2", $"Pre-connect: could not start a connection to cell {cellId} at {address}:{port}; the hop will connect as before");
                return;
            }
            _warm = new WarmConnection { transport = transport, address = address, port = port, cellId = cellId, startedAt = Time.realtimeSinceStartup, buildMs = sw.ElapsedMilliseconds };
            Logging.Log("Spike2", $"Pre-connect: connecting to cell {cellId} at {address}:{port} (transport built and started in {_warm.buildMs} ms, frame {Time.frameCount})");
        }

        /// <summary>Closes the warm connection, if any.</summary>
        public void DropPreconnect(string why)
        {
            if (_warm == null)
                return;
            WarmConnection warm = _warm;
            _warm = null;
            try
            {
                warm.transport.Destroy();
            }
            catch (System.Exception ex)
            {
                Logging.LogWarning("Spike2", $"Pre-connect: closing the connection to cell {warm.cellId} threw: {ex.Message}");
            }
            Logging.Log("Spike2", $"Pre-connect: dropped the connection to cell {warm.cellId} ({why})");
        }

        /// <summary>Every frame: the warm connection's events (connected, dropped) and its timeout.</summary>
        private void PollPreconnect()
        {
            if (_warm == null)
                return;
            while (_warm != null && _warm.transport.ClientReceive(out TransportEventData eventData))
            {
                switch (eventData.type)
                {
                    case ENetworkEvent.ConnectEvent:
                        _warm.connected = true;
                        Logging.Log("Spike2", $"Pre-connect: connected to cell {_warm.cellId} after {(Time.realtimeSinceStartup - _warm.startedAt) * 1000f:0} ms (frame {Time.frameCount})");
                        break;
                    case ENetworkEvent.DisconnectEvent:
                        DropPreconnect($"it disconnected: {eventData.disconnectInfo.Reason}");
                        break;
                    case ENetworkEvent.ErrorEvent:
                        DropPreconnect($"network error {eventData.socketError}");
                        break;
                    default:
                        break; // nothing is expected before we enter the game there
                }
            }
            if (_warm != null && !_warm.connected && Time.realtimeSinceStartup - _warm.startedAt > preconnectTimeoutSeconds)
                DropPreconnect($"no answer in {preconnectTimeoutSeconds:0.#} s");
        }

        /// <summary>
        /// At the hop: the warm connection to exactly this address, connected, is handed over (and forgotten here); a
        /// warm connection to anywhere else, or one still connecting, is dropped and the hop connects as before.
        /// </summary>
        private ITransport TakeWarmConnection(string address, int port)
        {
            if (_warm == null)
                return null;
            if (_warm.connected && _warm.address == address && _warm.port == port)
            {
                ITransport transport = _warm.transport;
                _warm = null;
                return transport;
            }
            DropPreconnect(_warm.connected ? $"the hop goes to {address}:{port}, not to cell {_warm.cellId}" : "it was still connecting at the hop");
            return null;
        }

        private void OnDisable()
        {
            DropPreconnect("controller disabled");
        }

        #endregion

        #region Continuity strategy (CE-14)

        /// <summary>"max X (median Y)" of per-frame samples: a hitch or a jump stands out against the median.</summary>
        private static string Spread(List<float> samples, string unit)
        {
            if (samples.Count == 0)
                return "n/a";
            List<float> sorted = new List<float>(samples);
            sorted.Sort();
            return $"max {sorted[sorted.Count - 1]:0.###}{unit} (median {sorted[sorted.Count / 2]:0.###}{unit}, {sorted.Count} frames)";
        }

        /// <summary>
        /// The client moves to the new cell without stopping (LiteNetLibManager.MigrateClient): everything on screen is
        /// kept, still running, and the new cell's spawn of each entity adopts the kept one instead of creating another.
        /// The player's character is the same object throughout, so its controller, camera, HUD, animation, sounds and
        /// effects carry on with nothing to stand in for them. Once it is adopted, the new cell's other spawns get
        /// keptObjectGraceSeconds to adopt theirs; kept entities it never spawned are then removed. A cell that does
        /// not seat us falls back to the stand-in swap toward the cell we came from.
        /// </summary>
        private IEnumerator MigrateContinuousRoutine(string address, int port)
        {
            _running = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            BasePlayerCharacterEntity entity = GameInstance.PlayingCharacterEntity;
            LiteNetLibIdentity identity = entity != null ? entity.Identity : null;
            Vector3 posBefore = entity != null ? entity.EntityTransform.position : Vector3.zero;
            uint objectIdBefore = identity != null ? identity.ObjectId : 0;
            int adoptedCount = 0;
            double adoptionMs = 0, playerAdoptionMs = -1; // the spawn work of the adoptions (all, and the player's own)
            void CountAdoption(LiteNetLibIdentity adopted, uint previousObjectId)
            {
                adoptedCount++;
                adoptionMs += _manager.Assets.LastAdoptionMilliseconds;
                if (ReferenceEquals(adopted, identity))
                    playerAdoptionMs = _manager.Assets.LastAdoptionMilliseconds;
            }
            // What the player could notice, frame by frame, from the hop until shortly after it settles.
            List<float> frameMs = new List<float>(), playerSteps = new List<float>(), cameraSteps = new List<float>();
            // The longest frame and where it fell: the MeshTrace stamps carry the same frame numbers (f<n>).
            float worstFrameMs = 0f;
            int worstFrame = -1, connectedFrame = -1, adoptedFrame = -1;
            // Steps far longer than the frame explains (more than twice the move speed allows): where a jump happened.
            System.Text.StringBuilder jumps = new System.Text.StringBuilder();
            MeshKeptMotionMeter others = null;
            MeshKeptExtrapolator keepWalking = null;
            Vector3 lastPlayer = posBefore;
            Vector3 lastCamera = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            // Garbage collections during the hop, and whether one fell in the longest frame (Mono's collector stops the world).
            int gcAtStart = System.GC.CollectionCount(0), gcLastSample = gcAtStart;
            bool gcInWorstFrame = false;
            void Sample()
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                frameMs.Add(ms);
                keepWalking?.Sample(); // before the meter: it measures what is drawn
                others?.Sample(ms);
                int gcNow = System.GC.CollectionCount(0);
                if (ms > worstFrameMs)
                {
                    worstFrameMs = ms;
                    worstFrame = Time.frameCount - 1; // the delta is the frame that just ended
                    gcInWorstFrame = gcNow != gcLastSample;
                }
                gcLastSample = gcNow;
                if (entity != null)
                {
                    Vector3 p = entity.EntityTransform.position;
                    float step = Vector3.Distance(p, lastPlayer);
                    playerSteps.Add(step);
                    float allowed = Mathf.Max(0.05f, 2f * entity.GetMoveSpeed() * Time.unscaledDeltaTime);
                    if (step > allowed && jumps.Length < 400)
                        jumps.Append($" {step:0.##}m@{sw.ElapsedMilliseconds}ms{(identity.IsKept ? "(kept)" : "")}");
                    lastPlayer = p;
                }
                if (Camera.main != null)
                {
                    Vector3 c = Camera.main.transform.position;
                    cameraSteps.Add(Vector3.Distance(c, lastCamera));
                    lastCamera = c;
                }
            }

            _manager.BeginMigration();
            _manager.BeginMigrationAttempt();
            _manager.Assets.onObjectAdopted += CountAdoption;
            // A connection opened ahead of the hop to this very cell is taken over: no transport rebuilt, no connect round trip.
            ITransport warm = TakeWarmConnection(address, port);
            bool preconnected = warm != null;
            bool started = identity != null && (preconnected ? _manager.MigrateClient(address, port, warm) : _manager.MigrateClient(address, port));
            if (!started)
            {
                if (warm != null)
                    try { warm.Destroy(); } catch (System.Exception) { }
                Logging.LogWarning("Spike2", $"Continuity migration to {address}:{port} could not start; using the stand-in swap");
                _manager.Assets.onObjectAdopted -= CountAdoption;
                _manager.EndMigrateClient();
                _manager.EndMigration();
                _running = false;
                StartCoroutine(MigrateSeamlessRoutine(address, port));
                yield break;
            }
            int keptCount = _manager.Assets.KeptObjects.Count;
            others = new MeshKeptMotionMeter(_manager.Assets.KeptObjects, entity); // DESIGN 1.08: the other characters through the gap
            if (_manager.extrapolateKeptCharacters)
                keepWalking = new MeshKeptExtrapolator(_manager.Assets.KeptObjects, entity, VelocityOf, _manager.keptExtrapolationMaxSeconds);

            long tConnected = -1, tAdopted = -1;
            if (preconnected)
            {
                tConnected = sw.ElapsedMilliseconds; // connected before the hop began
                connectedFrame = Time.frameCount;
            }
            bool adoptedPlayer = false;
            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (tConnected < 0 && _manager.IsClientConnected)
                {
                    tConnected = sw.ElapsedMilliseconds;
                    connectedFrame = Time.frameCount;
                }
                if (entity == null)
                    break;
                if (!identity.IsKept && identity.IsSpawned && entity.IsOwnerClient)
                {
                    adoptedPlayer = true;
                    tAdopted = sw.ElapsedMilliseconds;
                    adoptedFrame = Time.frameCount;
                    break;
                }
                if (_manager.MigrationRefused || _manager.MigrationDisconnected)
                    break;
                yield return null;
                Sample();
            }

            if (!adoptedPlayer)
            {
                string why = entity == null ? "character lost" : _manager.MigrationDisconnected ? _manager.MigrationDisconnectReason.ToString() : _manager.MigrationRefused ? "refused" : "timeout";
                Logging.LogWarning("Spike2", $"Continuity migration to {address}:{port} failed ({why}); falling back to {_prevAddress}:{_prevPort} with the stand-in swap");
                _manager.Assets.onObjectAdopted -= CountAdoption;
                _manager.EndMigrateClient();
                _manager.EndMigration();
                _running = false;
                FallbackCount++;
                StartCoroutine(MigrateSeamlessRoutine(_prevAddress, _prevPort, true));
                yield break;
            }

            // The new cell's other spawns come with the player's; give stragglers a moment, then drop what it never spawned.
            float settleEnd = Time.realtimeSinceStartup + _manager.keptObjectGraceSeconds;
            while (Time.realtimeSinceStartup < settleEnd && _manager.Assets.KeptObjects.Count > 0)
            {
                yield return null;
                Sample();
            }
            long tSettled = sw.ElapsedMilliseconds;
            int unadopted = _manager.Assets.KeptObjects.Count;
            _manager.Assets.DestroyKeptObjects();
            _manager.Assets.onObjectAdopted -= CountAdoption;
            _manager.EndMigrateClient();
            _manager.EndMigration();
            _running = false;
            // Half a second more of samples: a hitch from the adoption can land a frame or two after it.
            float tailEnd = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < tailEnd && !_running)
            {
                yield return null;
                Sample();
            }
            sw.Stop();
            keepWalking?.Release();

            Vector3 posAfter = entity != null ? entity.EntityTransform.position : posBefore;
            LastResult = new Result
            {
                address = address, port = port, connected = tConnected >= 0, playerReady = true, seamless = true, fellBack = false,
                connectedMs = tConnected, playerReadyMs = tAdopted, fadedMs = tSettled,
                positionBefore = posBefore, positionAfter = posAfter,
                positionJump = 0f, resyncJump = 0f, framesWithoutCamera = 0, standIns = 0, preconnected = preconnected,
            };
            CompletedCount++;
            if (preconnected)
                PreconnectedHops++;
            RunQueuedHop();
            Logging.Log("Spike2",
                $"Migration to {address}:{port} | CONTINUITY fellBack=False preconnected={preconnected} kept={keptCount} adopted={adoptedCount} unadopted={unadopted} | " +
                $"connected={tConnected}ms playerAdopted={tAdopted}ms settled={tSettled}ms | player object {objectIdBefore} -> {identity.ObjectId}, " +
                $"moved {Vector3.Distance(posBefore, posAfter):0.##} m during the hop | frames {Spread(frameMs, "ms")} | player step {Spread(playerSteps, "m")} | camera step {Spread(cameraSteps, "m")} | jumps:{(jumps.Length > 0 ? jumps.ToString() : " none")} | " +
                $"longest frame {worstFrameMs:0.#}ms at f{worstFrame} (connected f{connectedFrame}, adopted f{adoptedFrame}){(gcInWorstFrame ? " with a GC" : "")} | gcs {System.GC.CollectionCount(0) - gcAtStart} | adoption spawn work {playerAdoptionMs:0.##}ms player, {adoptionMs:0.##}ms all{others?.Summary()}{(keepWalking != null ? $" | kept walking {keepWalking.Walked}, handed back {keepWalking.HandedBack}{keepWalking.CorrectionSummary()}" : " | kept walking off")}");
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
            _manager.BeginMigrationAttempt();
            long tStop = sw.ElapsedMilliseconds;
            _manager.StartClient(address, port);

            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            bool fellBack = false;
            bool legFellBack = false; // per leg, as in the seamless strategy
            long tConnected = -1;
            while (true)
            {
                if (TryRedirectToQueuedHop(ref address, ref port, ref legFellBack))
                {
                    deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
                    tConnected = -1;
                }
                if (Time.realtimeSinceStartup >= deadline || _manager.MigrationDisconnected || _manager.MigrationRefused)
                {
                    string why = _manager.MigrationDisconnected ? _manager.MigrationDisconnectReason.ToString() : _manager.MigrationRefused ? "refused" : "timeout";
                    if (!TryFallbackHop(ref address, ref port, ref legFellBack, why))
                        break;
                    fellBack = true;
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
