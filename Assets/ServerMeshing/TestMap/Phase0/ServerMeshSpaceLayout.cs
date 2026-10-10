// Server Meshing test rig — isolated spaces, the pure part (Phase 0 step 9 decision 10, MESH_SPACES=S[:stack|grid]):
// the setting's grammar, where each space sits, which space an entity joins, the content points and the log lines.
using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshSpaceArrangement is where the spaces of MESH_SPACES sit.</summary>
    public enum ServerMeshSpaceArrangement
    {
        /// <summary>Stack puts every space at one centre (the dungeon-instance case: only the sub-channel separates them).</summary>
        Stack = 0,
        /// <summary>Grid puts the spaces on a square grid of <see cref="ServerMeshSpaceLayout.GridPitch"/> inside the ground.</summary>
        Grid = 1,
    }

    /// <summary>
    /// ServerMeshSpaceLayout is the pure part of the isolated-spaces rig. Space k (0 to S-1) has the SubChannelId
    /// <c>space:k</c>. Stack: every centre is the ground's centre, and an entity joins space ordinal mod S, its ordinal
    /// counted per population (observers, load) in spawn order, so a population splits evenly. Grid: C columns by R rows
    /// (C = ceil(sqrt S), R = ceil(S / C)) at <see cref="GridPitch"/>, centred on the ground and filled row by row, and an
    /// entity joins the space whose centre is nearest its spawn point (Chebyshev, ties to the lower index): the square it
    /// stands in. Each space holds <see cref="ContentPerSpace"/> content points on a ring about its centre.
    /// </summary>
    public static class ServerMeshSpaceLayout
    {
        /// <summary>EnvironmentKey is the setting.</summary>
        public const string EnvironmentKey = "MESH_SPACES";
        /// <summary>MaxSpaces is the most spaces one setting asks for.</summary>
        public const int MaxSpaces = 256;
        /// <summary>GridPitch is the distance in metres between neighbouring grid spaces' centres.</summary>
        public const float GridPitch = 150f;
        /// <summary>ContentPerSpace is how many stationary content objects each space holds.</summary>
        public const int ContentPerSpace = 5;
        /// <summary>ContentRingMetres is the radius of the ring the content points sit on about their space's centre.</summary>
        public const float ContentRingMetres = 4f;
        /// <summary>IdPrefix starts every space's SubChannelId.</summary>
        public const string IdPrefix = "space:";

        /// <summary>
        /// TryParseSetting reads <c>S[:stack|grid]</c> (trimmed, the layout ignoring case, stack when omitted), S a whole number
        /// from 1 to <see cref="MaxSpaces"/>; false with an error naming the key and the bad part otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out int spaces, out ServerMeshSpaceArrangement layout, out string error)
        {
            spaces = 0;
            layout = ServerMeshSpaceArrangement.Stack;
            string value = text == null ? string.Empty : text.Trim();
            if (value.Length == 0)
            {
                error = $"{EnvironmentKey} is empty; it needs S[:stack|grid]";
                return false;
            }
            int colon = value.IndexOf(':');
            if (colon >= 0 && value.IndexOf(':', colon + 1) >= 0)
            {
                error = $"{EnvironmentKey}={Printable(value)} is not S[:stack|grid]";
                return false;
            }
            string count = colon >= 0 ? value.Substring(0, colon).Trim() : value;
            if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s < 1 || s > MaxSpaces)
            {
                error = $"{EnvironmentKey}={Printable(value)}: the space count must be a whole number from 1 to {MaxSpaces}";
                return false;
            }
            if (colon >= 0)
            {
                string kind = value.Substring(colon + 1).Trim();
                if (string.Equals(kind, "grid", StringComparison.OrdinalIgnoreCase))
                    layout = ServerMeshSpaceArrangement.Grid;
                else if (!string.Equals(kind, "stack", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the layout {Printable(kind)} is not stack or grid";
                    return false;
                }
            }
            spaces = s;
            error = null;
            return true;
        }

        /// <summary>LayoutName is "stack" or "grid".</summary>
        public static string LayoutName(ServerMeshSpaceArrangement layout) => layout == ServerMeshSpaceArrangement.Grid ? "grid" : "stack";

        /// <summary>Pitch is the layout's pitch in metres: 0 for a stack, <see cref="GridPitch"/> for a grid.</summary>
        public static float Pitch(ServerMeshSpaceArrangement layout) => layout == ServerMeshSpaceArrangement.Grid ? GridPitch : 0f;

        /// <summary>GridShape is the grid's columns C = ceil(sqrt S) and rows R = ceil(S / C), for S of at least 1.</summary>
        public static void GridShape(int spaces, out int columns, out int rows)
        {
            int s = Math.Max(1, spaces);
            columns = 1;
            while (columns * columns < s)
                columns++;
            rows = (s + columns - 1) / columns;
        }

        /// <summary>
        /// TryCentres places the spaces' centres (scene frame, y is z) on <paramref name="ground"/>: all at its centre for a
        /// stack; for a grid, false with an error when C or R times the pitch exceeds the ground's width or depth.
        /// </summary>
        public static bool TryCentres(int spaces, ServerMeshSpaceArrangement layout, Rect ground, out Vector2[] centres, out string error)
        {
            centres = null;
            if (spaces < 1 || spaces > MaxSpaces)
            {
                error = $"{EnvironmentKey}: {spaces.ToString(CultureInfo.InvariantCulture)} spaces is outside 1 to {MaxSpaces}";
                return false;
            }
            if (!(ground.width > 0f) || !(ground.height > 0f))
            {
                error = $"{EnvironmentKey}: the ground is empty";
                return false;
            }
            Vector2[] result = new Vector2[spaces];
            Vector2 middle = ground.center;
            if (layout != ServerMeshSpaceArrangement.Grid)
            {
                for (int k = 0; k < spaces; ++k)
                    result[k] = middle;
                centres = result;
                error = null;
                return true;
            }
            GridShape(spaces, out int columns, out int rows);
            const float tolerance = 0.01f;
            if (columns * GridPitch > ground.width + tolerance || rows * GridPitch > ground.height + tolerance)
            {
                error = FormattableString.Invariant(
                    $"{EnvironmentKey}={spaces}:grid needs {columns} by {rows} squares of {GridPitch:0} m, more than the ground's {ground.width:0} by {ground.height:0} m");
                return false;
            }
            for (int k = 0; k < spaces; ++k)
            {
                int column = k % columns, row = k / columns;
                result[k] = new Vector2(middle.x + (column - (columns - 1) * 0.5f) * GridPitch, middle.y + (row - (rows - 1) * 0.5f) * GridPitch);
            }
            centres = result;
            error = null;
            return true;
        }

        /// <summary>
        /// SpaceOf is the space an entity joins: ordinal mod S for a stack (a negative ordinal counts as its absolute value),
        /// the nearest centre to <paramref name="position"/> for a grid (Chebyshev, ties to the lower index). -1 without centres.
        /// </summary>
        public static int SpaceOf(ServerMeshSpaceArrangement layout, Vector2[] centres, int ordinal, Vector2 position)
        {
            if (centres == null || centres.Length == 0)
                return -1;
            if (layout != ServerMeshSpaceArrangement.Grid)
                return (int)((ordinal < 0 ? -(long)ordinal : ordinal) % centres.Length);
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < centres.Length; ++k)
            {
                float d = Mathf.Max(Mathf.Abs(position.x - centres[k].x), Mathf.Abs(position.y - centres[k].y));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = k;
                }
            }
            return best;
        }

        /// <summary>ContentPoint is content point j of a space: on a ring of <see cref="ContentRingMetres"/> about its centre, j of 5 evenly spaced.</summary>
        public static Vector2 ContentPoint(Vector2 centre, int j)
        {
            double angle = 2.0 * Math.PI * j / ContentPerSpace;
            return new Vector2(centre.x + (float)(Math.Cos(angle) * ContentRingMetres), centre.y + (float)(Math.Sin(angle) * ContentRingMetres));
        }

        /// <summary>SpaceId is space k's SubChannelId, <c>space:k</c>.</summary>
        public static string SpaceId(int k) => IdPrefix + k.ToString(CultureInfo.InvariantCulture);

        /// <summary>IsSpaceId is true for a SubChannelId this rig sets (it starts with <see cref="IdPrefix"/>).</summary>
        public static bool IsSpaceId(string subChannelId) => subChannelId != null && subChannelId.StartsWith(IdPrefix, StringComparison.Ordinal);

        /// <summary>PerSpace is how many of <paramref name="total"/> each of S spaces holds on average (0 for S below 1).</summary>
        public static double PerSpace(int total, int spaces) => spaces > 0 ? Math.Max(0, total) / (double)spaces : 0.0;

        /// <summary>
        /// SettingLine is the once line, <c>Spaces: S 4 layout stack pitch 0 m | per space observers 1 content 5</c>; a fractional
        /// observer share keeps up to two decimals.
        /// </summary>
        public static string SettingLine(int spaces, ServerMeshSpaceArrangement layout, double observersPerSpace, int contentPerSpace)
        {
            StringBuilder b = new StringBuilder(96);
            b.Append("Spaces: S ").Append(Math.Max(0, spaces).ToString(CultureInfo.InvariantCulture));
            b.Append(" layout ").Append(LayoutName(layout));
            b.Append(" pitch ").Append(Pitch(layout).ToString("0", CultureInfo.InvariantCulture)).Append(" m");
            b.Append(" | per space observers ").Append(Positive(observersPerSpace).ToString("0.##", CultureInfo.InvariantCulture));
            b.Append(" content ").Append(Math.Max(0, contentPerSpace).ToString(CultureInfo.InvariantCulture));
            return b.ToString();
        }

        /// <summary>
        /// WindowLine is <c>Spaces window 5.00 s: observers n | own seen mean x | other seen max y</c>: the observers alive, the
        /// mean over them of the entities of their own space they see, and the most entities of other spaces any one sees.
        /// </summary>
        public static string WindowLine(double seconds, int observers, double ownSeenMean, int otherSeenMax)
        {
            StringBuilder b = new StringBuilder(96);
            b.Append("Spaces window ").Append(Positive(seconds).ToString("0.00", CultureInfo.InvariantCulture));
            b.Append(" s: observers ").Append(Math.Max(0, observers).ToString(CultureInfo.InvariantCulture));
            b.Append(" | own seen mean ").Append(Positive(ownSeenMean).ToString("0.00", CultureInfo.InvariantCulture));
            b.Append(" | other seen max ").Append(Math.Max(0, otherSeenMax).ToString(CultureInfo.InvariantCulture));
            return b.ToString();
        }

        /// <summary>Printable replaces braces and line breaks so a value can sit in a log line.</summary>
        public static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : text.Replace('{', '(').Replace('}', ')').Replace('\n', ' ').Replace('\r', ' ');

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) && !double.IsNaN(value) ? value : 0;
    }
}
