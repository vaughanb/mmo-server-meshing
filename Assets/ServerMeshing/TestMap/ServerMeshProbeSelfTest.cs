// Server Meshing test rig — checks the work probe where it will run: a headless, frame-capped player (DESIGN.md 0.71).
//
// Build/Server/kit-ce-project.exe -batchmode -nographics -logFile probe.log with MESH_PROBE_SELFTEST=0,5,10,24 in the
// environment burns each listed number of milliseconds of main-thread work per frame for two probe windows (the first
// is discarded as settling), logs one "[ProbeSelfTest]" line per kept window and quits. No cluster or database is needed,
// so it answers the one question the EditMode tests cannot: whether this Unity version's frame limiter waits inside
// the bracket the probe measures around.
using System.Diagnostics;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class ServerMeshProbeSelfTest : MonoBehaviour
    {
        public const string ENV = "MESH_PROBE_SELFTEST";
        private const float WindowSeconds = 5f;
        private const float WaitForCapSeconds = 20f;

        private float[] _burns;
        private int _step;
        private int _windowInStep;
        private float _nextWindow;
        private float _startedAt;
        private bool _running;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = System.Environment.GetEnvironmentVariable(ENV);
            if (string.IsNullOrEmpty(value))
                return;
            string[] parts = value.Split(',');
            float[] burns = new float[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
            {
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out burns[i]) || burns[i] < 0f)
                {
                    UnityEngine.Debug.LogError($"[ProbeSelfTest] {ENV}={value}: '{parts[i]}' is not a burn in milliseconds");
                    return;
                }
            }
            GameObject go = new GameObject("[Mesh] ProbeSelfTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshProbeSelfTest>()._burns = burns;
        }

        private void Start()
        {
            _startedAt = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            if (!_running)
            {
                // The game instance caps a headless process in its Awake; give it the time to do so, then start.
                if (Application.targetFrameRate <= 0 && Time.realtimeSinceStartup - _startedAt < WaitForCapSeconds)
                    return;
                _ = MeshWorkProbe.Meter;
                _running = true;
                _nextWindow = Time.realtimeSinceStartup + WindowSeconds;
                UnityEngine.Debug.Log($"[ProbeSelfTest] started: targetFrameRate={Application.targetFrameRate} graphics={SystemInfo.graphicsDeviceType} burns={string.Join(",", _burns)}");
                return;
            }
            float burn = _burns[_step];
            if (burn > 0f)
            {
                long until = Stopwatch.GetTimestamp() + (long)(burn / 1000f * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until) { }
            }
            if (Time.realtimeSinceStartup < _nextWindow)
                return;
            _nextWindow = Time.realtimeSinceStartup + WindowSeconds;
            MeshWorkWindow w = MeshWorkProbe.Meter.TakeWindow(MeshWorkProbe.BudgetMs, WindowSeconds);
            if (_windowInStep++ == 0)
                return; // settling: the burn changed mid-window
            UnityEngine.Debug.Log($"[ProbeSelfTest] burn={burn:0.##} frames={w.frames} work={w.workMeanMs:0.00} p95={w.workP95Ms:0.00} wait={w.waitMeanMs:0.00} period={w.periodMeanMs:0.00} sat={w.saturatedShare * 100f:0}% budget={w.budgetMs:0.00} state={w.state}");
            _windowInStep = 0;
            if (++_step < _burns.Length)
                return;
            UnityEngine.Debug.Log("[ProbeSelfTest] done");
            Application.Quit();
        }
    }
}
