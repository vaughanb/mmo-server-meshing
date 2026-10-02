// Server Meshing add-on — rig tick burn (MESH_PROBE_BURN_TICK_MS): busy-waits in every kit tick of a map server, after the tick stamps, to prove the Work ticks line sees known tick work.
using System.Globalization;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshTickBurn busy-waits a fixed time in every kit tick of a map server: "8" on every cell, "C:8" on cell C
    /// only. It subscribes to the logic updater's tick after the server has started, so it runs after the kit's tick and
    /// its stamps: the burn shows in tick-frame work, not in the tick duration or its parts, and multiplies with the
    /// ticks of a frame. It refuses to run beside MESH_PROBE_BURN_MS, whose per-frame burn lands in tick frames too.
    /// Never in the editor.
    /// </summary>
    public class ServerMeshTickBurn : MonoBehaviour
    {
        /// <summary>ENV_PROBE_BURN_TICK_MS is "[cell:]milliseconds": the busy-wait per kit tick, on one cell or on every cell.</summary>
        public const string ENV_PROBE_BURN_TICK_MS = "MESH_PROBE_BURN_TICK_MS";

        private LogicUpdateDelegate _burnHandler;
        private LogicUpdater _updater;
        private bool _prepared;
        private string _cell;
        private float _burnMs;
        private long _burnTicks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(ENV_PROBE_BURN_TICK_MS)))
                return;
            if (FindFirstObjectByType<ServerMeshTickBurn>() != null)
                return;
            GameObject go = new GameObject("[Mesh] TickBurn");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshTickBurn>();
        }

        /// <summary>
        /// TryParseBurn reads "[cell:]milliseconds" as MESH_PROBE_BURN_MS does: cell is null for every cell, ms is finite
        /// and above 0. An empty cell before the colon is refused, since it would match no cell. On failure cell is null
        /// and ms is 0.
        /// </summary>
        public static bool TryParseBurn(string text, out string cell, out float ms)
        {
            cell = null;
            ms = 0f;
            if (string.IsNullOrEmpty(text))
                return false;
            int colon = text.IndexOf(':');
            string cellText = colon >= 0 ? text.Substring(0, colon).Trim() : null;
            string msText = colon >= 0 ? text.Substring(colon + 1) : text;
            if (cellText != null && cellText.Length == 0)
                return false;
            if (!float.TryParse(msText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !(value > 0f) || float.IsInfinity(value))
                return false;
            cell = cellText;
            ms = value;
            return true;
        }

        private static string Printable(string text) => text.Replace('{', '(').Replace('}', ')');

        private void Awake()
        {
            _burnHandler = Burn;
        }

        private void Update()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer)
                return;
            if (!_prepared)
            {
                _prepared = true;
                if (!Prepare())
                {
                    enabled = false;
                    return;
                }
            }
            if (_cell != null)
            {
                if (!mesh.HasOwnZone)
                    return; // the cell id can resolve after the server starts
                if (!string.Equals(_cell, mesh.OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                    return;
                }
            }
            Subscribe(mesh);
            enabled = false;
        }

        private bool Prepare()
        {
            if (Application.isEditor)
            {
                Logging.LogWarning("Mesh", $"Tick burn: {ENV_PROBE_BURN_TICK_MS} is ignored in the editor; no tick burn");
                return false;
            }
            string text = System.Environment.GetEnvironmentVariable(ENV_PROBE_BURN_TICK_MS);
            if (!TryParseBurn(text, out _cell, out _burnMs))
            {
                Logging.LogWarning("Mesh", Printable($"{ENV_PROBE_BURN_TICK_MS}={text} is not [cell:]milliseconds; ignored"));
                return false;
            }
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(MeshMapNetworkManager.ENV_PROBE_BURN_MS)))
            {
                Logging.LogError("Mesh", $"Tick burn: {ENV_PROBE_BURN_TICK_MS} and {MeshMapNetworkManager.ENV_PROBE_BURN_MS} are both set, and both burns land in tick frames; no tick burn");
                return false;
            }
            _burnTicks = (long)(_burnMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
            return true;
        }

        private void Subscribe(MeshMapNetworkManager mesh)
        {
            LogicUpdater updater = mesh.LogicUpdater;
            if (updater == null)
            {
                Logging.LogError("Mesh", $"Tick burn: map server {mesh.OwnZoneId} has no logic updater; no tick burn");
                return;
            }
            _updater = updater;
            _updater.OnTick += _burnHandler;
            string where = _cell != null ? mesh.OwnZoneId : "every cell";
            Logging.Log("Mesh", Printable(System.FormattableString.Invariant($"Tick burn: {_burnMs:0.##} ms per tick on {where}")));
        }

        private void OnDestroy()
        {
            if (_updater != null)
                _updater.OnTick -= _burnHandler;
            _updater = null;
        }

        private void Burn(LogicUpdater updater)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + _burnTicks;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
        }
    }
}
