// Server Meshing add-on — Spike 2 (client connection migration).
// See Docs/ServerMeshing/SPIKE2.md and Docs/ServerMeshing/DESIGN.md.
//
// EXTENSION-ONLY: a runtime-created MonoBehaviour. Captures a full-screen screenshot, holds it over
// a normal StopClient()/StartClient() while server B re-streams the world, then fades it out once the
// local player has re-spawned. Logs per-step timings so we can judge whether the swap is seamless.
//
// This measures approach "M — Mask the reload" from SPIKE2.md §2 (zero core edits).

using System.Collections;
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
            public long captureMs, stopMs, connectedMs, playerReadyMs, fadedMs;
            public Vector3 positionBefore, positionAfter;
            public float positionJump;
        }

        public static Result LastResult;
        public static int CompletedCount;

        private MeshMapNetworkManager _manager;
        private bool _running;

        // Mask state (drawn via IMGUI so it sits above uGUI).
        private Texture2D _maskTex;
        private float _maskAlpha;

        public void Setup(MeshMapNetworkManager manager)
        {
            _manager = manager;
        }

        public bool IsRunning => _running;

        public void Migrate(string address, int port)
        {
            if (_running)
            {
                Logging.LogWarning("Spike2", "Migration already in progress; ignoring request.");
                return;
            }
            if (_manager == null)
            {
                Logging.LogError("Spike2", "No manager set on ClientMigrationController.");
                return;
            }
            StartCoroutine(MigrateRoutine(address, port));
        }

        private IEnumerator MigrateRoutine(string address, int port)
        {
            _running = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            BasePlayerCharacterEntity prevEntity = GameInstance.PlayingCharacterEntity;
            Vector3 posBefore = prevEntity != null ? prevEntity.EntityTransform.position : Vector3.zero;

            // 1) Capture the screen at end-of-frame and raise the mask.
            yield return new WaitForEndOfFrame();
            DisposeMask();
            _maskTex = ScreenCapture.CaptureScreenshotAsTexture(); // SPIKE-TODO: may be vertically flipped on some platforms.
            _maskAlpha = 1f;
            long tCapture = sw.ElapsedMilliseconds;

            // 2) Tear down the connection to A (offline-scene reload suppressed by the manager).
            _manager.BeginMigration();
            _manager.StopClient();
            long tStop = sw.ElapsedMilliseconds;

            // 3) Connect to B.
            _manager.StartClient(address, port);

            // 4) Wait for "connected to B".
            float deadline = Time.realtimeSinceStartup + _manager.migrationReadyTimeoutSeconds;
            while (!_manager.IsClientConnected && Time.realtimeSinceStartup < deadline)
                yield return null;
            long tConnected = sw.ElapsedMilliseconds;
            bool connected = _manager.IsClientConnected;

            // 5) Wait for the local player to re-spawn — the gap that actually matters.
            // The old entity is Destroy()ed by Assets.Clear() but only vanishes at end of frame, so a
            // non-null check alone gives a false "ready"; require a NEW entity instance from server B.
            // SPIKE-TODO: consider a stricter readiness signal (first transform sync) than a new entity.
            while ((GameInstance.PlayingCharacterEntity == null || ReferenceEquals(GameInstance.PlayingCharacterEntity, prevEntity)) && Time.realtimeSinceStartup < deadline)
                yield return null;
            long tPlayerReady = sw.ElapsedMilliseconds;
            bool playerReady = GameInstance.PlayingCharacterEntity != null && !ReferenceEquals(GameInstance.PlayingCharacterEntity, prevEntity);
            Vector3 posAfter = playerReady ? GameInstance.PlayingCharacterEntity.EntityTransform.position : Vector3.zero;

            // Let one or two frames render the live world before revealing it.
            yield return null;
            yield return null;

            // 6) Fade the mask out.
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
                address = address,
                port = port,
                connected = connected,
                playerReady = playerReady,
                captureMs = tCapture,
                stopMs = tStop,
                connectedMs = tConnected,
                playerReadyMs = tPlayerReady,
                fadedMs = tFaded,
                positionBefore = posBefore,
                positionAfter = posAfter,
                positionJump = playerReady ? Vector3.Distance(posBefore, posAfter) : -1f,
            };
            CompletedCount++;

            Logging.Log("Spike2",
                $"Migration to {address}:{port} | connected={connected} playerReady={playerReady} | " +
                $"capture={tCapture}ms stop={tStop}ms connected={tConnected}ms " +
                $"playerReady={tPlayerReady}ms faded={tFaded}ms | pos {posBefore} -> {posAfter} (jump={LastResult.positionJump:0.##}m)" +
                "  << 'playerReady' is the gap to hide");
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
    }
}
