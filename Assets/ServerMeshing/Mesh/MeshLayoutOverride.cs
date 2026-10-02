// Server Meshing add-on — re-cut a map, or change its cell mode, for a test run without touching the registry asset
// (DESIGN.md 0.71, 0.72).
//
// MESH_LAYOUT_OVERRIDE=<mapId>=<cellId>@<minX>,<minZ>,<sizeX>,<sizeZ>[;<cellId>@...][|<mapId>=...]
// replaces those maps' layouts with explicit rectangles (scene frame) in a runtime copy of the registry. Every process
// that reads the environment resolves the same copy: the editor's central, login and spawner, and the cells it starts,
// which inherit the environment. Example, one 600 m cell over the rig's 3x3 ground:
//   MESH_LAYOUT_OVERRIDE=ServerMeshTestMap=C@-300,-300,600,600
// MESH_CELL_MODE=<mapId|all>:<Static|Dynamic>[,...] sets maps' cell modes in the same copy ("all" first, then each map
// named; no | & < > ^, so it survives a .cmd file's set line). Example: MESH_CELL_MODE=all:Dynamic
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutOverride applies the MESH_LAYOUT_OVERRIDE rig toggle to a world registry.</summary>
    public static class MeshLayoutOverride
    {
        public const string ENV = "MESH_LAYOUT_OVERRIDE";
        public const string ENV_CELL_MODE = "MESH_CELL_MODE";
        /// <summary>The MESH_CELL_MODE key that sets every map; a map's own entry wins over it.</summary>
        public const string AllMaps = "all";

        private static readonly Dictionary<MeshWorldRegistry, MeshWorldRegistry> s_resolved = new Dictionary<MeshWorldRegistry, MeshWorldRegistry>();

        /// <summary>
        /// Parse reads an override value into explicit cell rectangles per map. It throws FormatException, naming the
        /// part it could not read, on anything malformed: a half-applied layout would split ownership between processes.
        /// </summary>
        public static Dictionary<string, MeshCellRect[]> Parse(string value)
        {
            Dictionary<string, MeshCellRect[]> maps = new Dictionary<string, MeshCellRect[]>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
                return maps;
            foreach (string mapPart in value.Split('|'))
            {
                int eq = mapPart.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException($"{ENV}: '{mapPart}' is not <mapId>=<cells>");
                string mapId = mapPart.Substring(0, eq).Trim();
                if (maps.ContainsKey(mapId))
                    throw new FormatException($"{ENV}: map '{mapId}' is given twice");
                List<MeshCellRect> cells = new List<MeshCellRect>();
                foreach (string cellPart in mapPart.Substring(eq + 1).Split(';'))
                    cells.Add(ParseCell(mapId, cellPart));
                maps[mapId] = cells.ToArray();
            }
            return maps;
        }

        private static MeshCellRect ParseCell(string mapId, string cellPart)
        {
            int at = cellPart.IndexOf('@');
            if (at <= 0)
                throw new FormatException($"{ENV}: cell '{cellPart}' of map '{mapId}' is not <cellId>@<minX>,<minZ>,<sizeX>,<sizeZ>");
            string id = cellPart.Substring(0, at).Trim();
            string[] numbers = cellPart.Substring(at + 1).Split(',');
            if (numbers.Length != 4)
                throw new FormatException($"{ENV}: cell '{id}' of map '{mapId}' needs 4 numbers, has {numbers.Length}");
            float[] values = new float[4];
            for (int i = 0; i < 4; ++i)
            {
                if (!float.TryParse(numbers[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    throw new FormatException($"{ENV}: cell '{id}' of map '{mapId}': '{numbers[i]}' is not a number");
            }
            if (values[2] <= 0f || values[3] <= 0f)
                throw new FormatException($"{ENV}: cell '{id}' of map '{mapId}' needs a positive size");
            return new MeshCellRect { id = id, min = new Vector2(values[0], values[1]), size = new Vector2(values[2], values[3]) };
        }

        /// <summary>
        /// ParseCellModes reads a MESH_CELL_MODE value into a cell mode per map ("all" for every map). It throws
        /// FormatException, naming the part it could not read, on anything malformed. Static and Dynamic are read ignoring case.
        /// </summary>
        public static Dictionary<string, MeshCellMode> ParseCellModes(string value)
        {
            Dictionary<string, MeshCellMode> modes = new Dictionary<string, MeshCellMode>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
                return modes;
            foreach (string rawPart in value.Split(','))
            {
                string part = rawPart.Trim();
                int colon = part.IndexOf(':');
                if (colon < 0)
                    throw new FormatException($"{ENV_CELL_MODE}: '{part}' is not <mapId|all>:<Static|Dynamic>");
                string mapId = part.Substring(0, colon).Trim();
                if (mapId.Length == 0)
                    throw new FormatException($"{ENV_CELL_MODE}: '{part}' names no map");
                if (string.Equals(mapId, AllMaps, StringComparison.OrdinalIgnoreCase))
                    mapId = AllMaps;
                string modeName = part.Substring(colon + 1).Trim();
                MeshCellMode mode;
                if (string.Equals(modeName, nameof(MeshCellMode.Static), StringComparison.OrdinalIgnoreCase))
                    mode = MeshCellMode.Static;
                else if (string.Equals(modeName, nameof(MeshCellMode.Dynamic), StringComparison.OrdinalIgnoreCase))
                    mode = MeshCellMode.Dynamic;
                else
                    throw new FormatException($"{ENV_CELL_MODE}: '{modeName}' in '{part}' is neither Static nor Dynamic");
                if (modes.ContainsKey(mapId))
                    throw new FormatException($"{ENV_CELL_MODE}: map '{mapId}' is given twice");
                modes[mapId] = mode;
            }
            return modes;
        }

        /// <summary>
        /// Resolve returns the registry this process should use: the configured asset, or, when a rig toggle is set, one
        /// runtime copy of it with the given maps re-cut and cell modes set (the same copy for every caller in the process).
        /// A malformed toggle is logged and ignored on its own; with nothing usable left, the asset is used.
        /// </summary>
        public static MeshWorldRegistry Resolve(MeshWorldRegistry configured)
        {
            if (configured == null)
                return null;
            if (s_resolved.ContainsValue(configured))
                return configured; // already the process's copy
            string layoutValue = Environment.GetEnvironmentVariable(ENV);
            string modeValue = Environment.GetEnvironmentVariable(ENV_CELL_MODE);
            if (string.IsNullOrWhiteSpace(layoutValue) && string.IsNullOrWhiteSpace(modeValue))
                return configured;
            if (s_resolved.TryGetValue(configured, out MeshWorldRegistry copy) && copy != null)
                return copy;
            Dictionary<string, MeshCellRect[]> maps = ParseOrIgnore(layoutValue, Parse);
            Dictionary<string, MeshCellMode> modes = ParseOrIgnore(modeValue, ParseCellModes);
            if (maps.Count == 0 && modes.Count == 0)
                return configured;
            copy = UnityEngine.Object.Instantiate(configured);
            copy.name = configured.name + " (" + (maps.Count > 0 && modes.Count > 0 ? ENV + ", " + ENV_CELL_MODE : maps.Count > 0 ? ENV : ENV_CELL_MODE) + ")";
            foreach (KeyValuePair<string, MeshCellRect[]> kv in maps)
            {
                MeshMapLayout layout = copy.maps.Find(l => l != null && l.mapId == kv.Key);
                if (layout == null)
                {
                    Debug.LogError($"[Mesh] {ENV}: map '{kv.Key}' has no layout in {configured.name}; ignored");
                    continue;
                }
                layout.cellRects = kv.Value;
            }
            ApplyCellModes(copy, modes, configured.name);
            copy.Rebuild();
            s_resolved[configured] = copy;
            if (maps.Count > 0)
                Debug.Log($"[Mesh] {ENV}={layoutValue}: using a re-cut copy of {configured.name}");
            if (modes.Count > 0)
                Debug.Log($"[Mesh] {ENV_CELL_MODE}={modeValue}: maps {DynamicMapList(copy)} run Dynamic");
            return copy;
        }

        private static Dictionary<string, T> ParseOrIgnore<T>(string value, Func<string, Dictionary<string, T>> parse)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new Dictionary<string, T>();
            try
            {
                return parse(value);
            }
            catch (FormatException ex)
            {
                Debug.LogError("[Mesh] " + ex.Message + "; ignored");
                return new Dictionary<string, T>();
            }
        }

        private static void ApplyCellModes(MeshWorldRegistry copy, Dictionary<string, MeshCellMode> modes, string assetName)
        {
            if (modes.TryGetValue(AllMaps, out MeshCellMode every))
            {
                foreach (MeshMapLayout layout in copy.maps)
                {
                    if (layout != null)
                        layout.cellMode = every;
                }
            }
            foreach (KeyValuePair<string, MeshCellMode> kv in modes)
            {
                if (kv.Key == AllMaps)
                    continue;
                MeshMapLayout layout = copy.maps.Find(l => l != null && l.mapId == kv.Key);
                if (layout == null)
                {
                    Debug.LogError($"[Mesh] {ENV_CELL_MODE}: map '{kv.Key}' has no layout in {assetName}; ignored");
                    continue;
                }
                layout.cellMode = kv.Value;
            }
        }

        private static string DynamicMapList(MeshWorldRegistry copy)
        {
            List<string> ids = new List<string>();
            foreach (MeshMapLayout layout in copy.maps)
            {
                if (layout != null && layout.IsDynamic)
                    ids.Add(layout.mapId);
            }
            return ids.Count > 0 ? string.Join(", ", ids) : "(none)";
        }
    }
}
