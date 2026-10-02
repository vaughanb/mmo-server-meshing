// Server Meshing add-on — rig frame and physics rates (MESH_RATES, MESH_UNCAPPED_CELLS): a map server's frame cap, physics step and nominal frame rate, from the environment.
using System.Collections.Generic;
using System.Globalization;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshRatesSetting is a parsed MESH_RATES value: a frame cap, or no cap with a nominal frame rate, and a physics
    /// step. The grammar is "&lt;fps&gt;/&lt;step&gt;", "u/&lt;step&gt;" or "u&lt;fps&gt;/&lt;step&gt;" (u in either case): fps is an
    /// integer from 1 to 1000, step is seconds above 0 and at most 1, whitespace around the parts is allowed, and numbers
    /// are read in the invariant culture.
    /// </summary>
    public readonly struct ServerMeshRatesSetting
    {
        /// <summary>MinFramesPerSecond is the lowest frame rate the grammar accepts.</summary>
        public const int MinFramesPerSecond = 1;
        /// <summary>MaxFramesPerSecond is the highest frame rate the grammar accepts.</summary>
        public const int MaxFramesPerSecond = 1000;
        /// <summary>MaxFixedStepSeconds is the longest physics step the grammar accepts.</summary>
        public const float MaxFixedStepSeconds = 1f;
        /// <summary>Uncapped is the FramesPerSecond of a setting that removes the frame cap.</summary>
        public const int Uncapped = -1;

        private const string Shapes = "<fps>/<step>, u/<step> or u<fps>/<step>";

        /// <summary>
        /// ServerMeshRatesSetting validates and stores a setting: framesPerSecond is <see cref="Uncapped"/> or 1 to 1000,
        /// nominalFramesPerSecond 0 to 1000, fixedStepSeconds above 0 and at most 1.
        /// </summary>
        public ServerMeshRatesSetting(int framesPerSecond, int nominalFramesPerSecond, float fixedStepSeconds)
        {
            if (framesPerSecond != Uncapped && !InFrameRange(framesPerSecond))
                throw new System.ArgumentOutOfRangeException(nameof(framesPerSecond), framesPerSecond, "must be -1 (uncapped) or 1 to 1000");
            if (nominalFramesPerSecond != 0 && !InFrameRange(nominalFramesPerSecond))
                throw new System.ArgumentOutOfRangeException(nameof(nominalFramesPerSecond), nominalFramesPerSecond, "must be 0 (the kit's cap) or 1 to 1000");
            if (!(fixedStepSeconds > 0f && fixedStepSeconds <= MaxFixedStepSeconds))
                throw new System.ArgumentOutOfRangeException(nameof(fixedStepSeconds), fixedStepSeconds, "must be above 0 and at most 1 second");
            FramesPerSecond = framesPerSecond;
            NominalFramesPerSecond = nominalFramesPerSecond;
            FixedStepSeconds = fixedStepSeconds;
        }

        /// <summary>FramesPerSecond is the frame cap every cell gets, or <see cref="Uncapped"/>.</summary>
        public int FramesPerSecond { get; }

        /// <summary>
        /// NominalFramesPerSecond is the frame rate the run is modelled at: the cap itself when capped, the number after u
        /// when uncapped, and 0 for a bare u, which means the kit's cap as it stands when the setting is applied.
        /// </summary>
        public int NominalFramesPerSecond { get; }

        /// <summary>FixedStepSeconds is the physics step (Time.fixedDeltaTime) in seconds.</summary>
        public float FixedStepSeconds { get; }

        /// <summary>IsUncapped is true when the setting removes the frame cap.</summary>
        public bool IsUncapped => FramesPerSecond == Uncapped;

        /// <summary>NominalOr is the frame rate an uncapped cell is modelled at: the nominal rate, or the kit's cap when that is 0 (0 when the kit is uncapped too).</summary>
        public int NominalOr(int kitFrameRate) => NominalFramesPerSecond > 0 ? NominalFramesPerSecond : System.Math.Max(0, kitFrameRate);

        /// <summary>TryParse reads a MESH_RATES value; on failure the setting is default and the error names the failing part.</summary>
        public static bool TryParse(string text, out ServerMeshRatesSetting setting, out string error)
        {
            setting = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "is empty; expected " + Shapes;
                return false;
            }
            string[] parts = text.Split('/');
            if (parts.Length != 2)
            {
                error = $"'{text.Trim()}' is not {Shapes}";
                return false;
            }
            string fpsPart = parts[0].Trim();
            string stepPart = parts[1].Trim();
            if (!TryParseFrameRates(fpsPart, out int framesPerSecond, out int nominalFramesPerSecond))
            {
                error = $"fps part '{fpsPart}' is not an integer from 1 to 1000, u, or u and such an integer";
                return false;
            }
            if (!float.TryParse(stepPart, NumberStyles.Float, CultureInfo.InvariantCulture, out float step) || float.IsNaN(step) || float.IsInfinity(step))
            {
                error = $"step part '{stepPart}' is not a number of seconds";
                return false;
            }
            if (!(step > 0f && step <= MaxFixedStepSeconds))
            {
                error = $"step part '{stepPart}' is not above 0 and at most 1 second";
                return false;
            }
            setting = new ServerMeshRatesSetting(framesPerSecond, nominalFramesPerSecond, step);
            error = null;
            return true;
        }

        private static bool TryParseFrameRates(string part, out int framesPerSecond, out int nominalFramesPerSecond)
        {
            framesPerSecond = 0;
            nominalFramesPerSecond = 0;
            if (part.Length > 0 && (part[0] == 'u' || part[0] == 'U'))
            {
                string rest = part.Substring(1).Trim();
                if (rest.Length > 0 && !TryParseFrameCount(rest, out nominalFramesPerSecond))
                    return false;
                framesPerSecond = Uncapped;
                return true;
            }
            if (!TryParseFrameCount(part, out framesPerSecond))
                return false;
            nominalFramesPerSecond = framesPerSecond;
            return true;
        }

        private static bool TryParseFrameCount(string text, out int value)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && InFrameRange(value))
                return true;
            value = 0;
            return false;
        }

        private static bool InFrameRange(int value) => value >= MinFramesPerSecond && value <= MaxFramesPerSecond;
    }

    /// <summary>
    /// ServerMeshRates applies MESH_RATES and MESH_UNCAPPED_CELLS on a map server, once it serves: vSync off, the frame cap
    /// and physics step from MESH_RATES, the nominal frame rate the Work ticks line budgets an uncapped cell at, and no cap
    /// on the cells MESH_UNCAPPED_CELLS lists. Never in the editor, whose frame and physics settings could persist into the
    /// project. With neither variable set it is not installed and nothing changes.
    /// </summary>
    public class ServerMeshRates : MonoBehaviour
    {
        /// <summary>ENV_RATES is "&lt;fps&gt;/&lt;step&gt;", "u/&lt;step&gt;" or "u&lt;fps&gt;/&lt;step&gt;": every cell's frame cap and physics step.</summary>
        public const string ENV_RATES = "MESH_RATES";
        /// <summary>ENV_UNCAPPED_CELLS is a comma list of cell ids that run without a frame cap.</summary>
        public const string ENV_UNCAPPED_CELLS = "MESH_UNCAPPED_CELLS";

        private bool _applied;
        private int _nominalForUncapped;
        private IReadOnlyList<string> _uncappedCells;

        /// <summary>
        /// OwnsFrameRate is true when either variable is set, so other rig code leaves the frame cap alone. It reads the
        /// environment rather than this component's state, so the order the installers run in cannot matter.
        /// </summary>
        public static bool OwnsFrameRate =>
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(ENV_RATES))
            || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(ENV_UNCAPPED_CELLS));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!OwnsFrameRate)
                return;
            if (FindFirstObjectByType<ServerMeshRates>() != null)
                return;
            GameObject go = new GameObject("[Mesh] Rates");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshRates>();
        }

        /// <summary>ParseCellList reads a comma list of cell ids: entries trimmed, empty ones dropped, repeats (ignoring case) kept once.</summary>
        public static IReadOnlyList<string> ParseCellList(string text)
        {
            List<string> cells = new List<string>();
            if (string.IsNullOrEmpty(text))
                return cells;
            foreach (string entry in text.Split(','))
            {
                string cell = entry.Trim();
                if (cell.Length > 0 && !IsListed(cells, cell))
                    cells.Add(cell);
            }
            return cells;
        }

        /// <summary>IsListed is true when cellId is in cells, ignoring case.</summary>
        public static bool IsListed(IReadOnlyList<string> cells, string cellId)
        {
            if (cells == null || string.IsNullOrEmpty(cellId))
                return false;
            for (int i = 0; i < cells.Count; ++i)
            {
                if (string.Equals(cells[i], cellId, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// RatesLine is the one line logged after applying, in the invariant culture and without braces. A frame rate that
        /// is not a cap prints as u followed by the nominal rate (a bare u when there is none); negative or NaN durations
        /// print as 0.
        /// </summary>
        public static string RatesLine(bool ratesSet, int targetFrameRate, int nominalFrameRate, int kitFrameRate, int vSyncCount, int tickHz,
            float fixedStepSeconds, float kitFixedStepSeconds, float maxDeltaTime, float budgetMs, IReadOnlyList<string> uncappedCells)
        {
            string source = ratesSet ? ENV_RATES : ENV_RATES + " unset";
            string cells = uncappedCells == null || uncappedCells.Count == 0 ? "-" : Printable(string.Join(",", uncappedCells));
            return System.FormattableString.Invariant(
                $"Rates: fps {FpsText(targetFrameRate, nominalFrameRate)} ({source}; kit {FpsText(kitFrameRate, 0)}) vSync {System.Math.Max(0, vSyncCount)} | tick {System.Math.Max(0, tickHz)} Hz (updateFps unchanged) | physics {NonNegative(fixedStepSeconds):0.0###} s (project {NonNegative(kitFixedStepSeconds):0.0###}) | maxDeltaTime {NonNegative(maxDeltaTime):0.000} | probe budget {NonNegative(budgetMs):0.00} ms | uncapped cells {cells}");
        }

        private static string FpsText(int targetFrameRate, int nominalFrameRate)
        {
            if (targetFrameRate > 0)
                return targetFrameRate.ToString(CultureInfo.InvariantCulture);
            return nominalFrameRate > 0 ? "u" + nominalFrameRate.ToString(CultureInfo.InvariantCulture) : "u";
        }

        private static float NonNegative(float value) => value > 0f ? value : 0f;

        private static string Printable(string text) => text.Replace('{', '(').Replace('}', ')');

        private void Update()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer)
                return;
            if (!_applied)
            {
                _applied = true;
                if (!Apply(mesh))
                {
                    enabled = false;
                    return;
                }
            }
            if (_uncappedCells.Count > 0 && !mesh.HasOwnZone)
                return; // the cell id can resolve after the server starts
            if (IsListed(_uncappedCells, mesh.OwnZoneId))
                UncapThisCell(mesh);
            enabled = false;
        }

        private bool Apply(MeshMapNetworkManager mesh)
        {
            if (Application.isEditor)
            {
                Logging.LogWarning("Mesh", $"Rates: {ENV_RATES} and {ENV_UNCAPPED_CELLS} are ignored in the editor, whose frame and physics settings could persist into the project; nothing applied");
                return false;
            }
            int kitFrameRate = Application.targetFrameRate;
            float kitFixedStep = Time.fixedDeltaTime;
            string ratesText = System.Environment.GetEnvironmentVariable(ENV_RATES);
            bool ratesSet = !string.IsNullOrEmpty(ratesText);
            ServerMeshRatesSetting setting = default;
            if (ratesSet && !ServerMeshRatesSetting.TryParse(ratesText, out setting, out string error))
            {
                Logging.LogError("Mesh", Printable($"Rates: {ENV_RATES}={ratesText}: {error}; nothing applied"));
                return false;
            }
            _uncappedCells = ParseCellList(System.Environment.GetEnvironmentVariable(ENV_UNCAPPED_CELLS));
            _nominalForUncapped = ratesSet ? setting.NominalOr(kitFrameRate) : System.Math.Max(0, kitFrameRate);
            QualitySettings.vSyncCount = 0;
            if (ratesSet)
            {
                Application.targetFrameRate = setting.FramesPerSecond;
                MeshWorkProbe.NominalFrameRate = setting.IsUncapped ? _nominalForUncapped : 0;
                Time.fixedDeltaTime = setting.FixedStepSeconds;
            }
            Logging.Log("Mesh", RatesLine(ratesSet, Application.targetFrameRate, MeshWorkProbe.NominalFrameRate, kitFrameRate, QualitySettings.vSyncCount,
                mesh.updateFps, Time.fixedDeltaTime, kitFixedStep, Time.maximumDeltaTime, MeshWorkProbe.EffectiveBudgetMs, _uncappedCells));
            return true;
        }

        private void UncapThisCell(MeshMapNetworkManager mesh)
        {
            Application.targetFrameRate = ServerMeshRatesSetting.Uncapped;
            MeshWorkProbe.NominalFrameRate = _nominalForUncapped;
            Logging.Log("Mesh", Printable(System.FormattableString.Invariant($"Rates: cell {mesh.OwnZoneId} uncapped (nominal {_nominalForUncapped} fps)")));
        }
    }
}
