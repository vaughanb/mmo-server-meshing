// Server Meshing add-on — the rig's fault toggles and the monster-area toggle's `only:` form (Slice 2d,
// slices/slice2d_design.md decisions 12 and 13). A fault toggle names one cell; that cell dies the first time its moment
// comes in a test run: MESH_FAULT_DIE_ON_NOTICE on its next incoming handoff notice (the window between the origin's
// notice and the client's arrival), MESH_FAULT_DIE_ON_PREWARM on the first layout broadcast that names it Pending (a
// split's e1, before it acks), MESH_FAULT_DIE_ON_DRAIN on a child's first incoming handoff notice after it applied an
// owned rectangle (a split's drain). Once per run: the marker file Temp/ServerMesh.FaultFired.<key>.<zone> is written
// before the process kills itself, so the restarted incarnation (same environment) lives; the harness deletes the
// markers at every scenario start. Unset toggles change nothing.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshFault holds the fault toggles' keys, their pure predicates and the one helper that fires them.</summary>
    public static class MeshFault
    {
        public const string DieOnNotice = "MESH_FAULT_DIE_ON_NOTICE";
        public const string DieOnPrewarm = "MESH_FAULT_DIE_ON_PREWARM";
        public const string DieOnDrain = "MESH_FAULT_DIE_ON_DRAIN";

        private static readonly Dictionary<string, string> s_toggles = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Toggle is the trimmed value of a fault key in this process's environment (read once), or null when unset.</summary>
        public static string Toggle(string key)
        {
            lock (s_toggles)
            {
                if (!s_toggles.TryGetValue(key, out string value))
                {
                    string raw = Environment.GetEnvironmentVariable(key);
                    value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                    s_toggles[key] = value;
                }
                return value;
            }
        }

        /// <summary>NamesCell: the toggle is set and names this cell (ordinal, as the notice fault always compared).</summary>
        public static bool NamesCell(string toggle, string cellId)
        {
            return !string.IsNullOrEmpty(toggle) && !string.IsNullOrEmpty(cellId) && string.Equals(toggle.Trim(), cellId, StringComparison.Ordinal);
        }

        /// <summary>
        /// PrewarmFires: the toggle names the cell and the layout is a Broadcast that names the cell Pending (a split's
        /// e1 broadcast: the staged e1 and the hello answer come first and do not count, so the e1 broadcast is out).
        /// </summary>
        public static bool PrewarmFires(string toggle, string cellId, MeshLayoutKind kind, MeshLayoutSnapshot snapshot)
        {
            if (!NamesCell(toggle, cellId) || kind != MeshLayoutKind.Broadcast || snapshot == null || snapshot.entries == null)
                return false;
            for (int i = 0; i < snapshot.entries.Count; ++i)
            {
                MeshLayoutEntry entry = snapshot.entries[i];
                if (entry != null && entry.state == MeshCellState.Pending && string.Equals(entry.id, cellId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>DrainFires: the toggle names the cell, which is a child that holds an owned rectangle in the layout it applied.</summary>
        public static bool DrainFires(string toggle, string cellId, bool isChild, bool ownsSomething)
        {
            return NamesCell(toggle, cellId) && isChild && ownsSomething;
        }

        /// <summary>MarkerPath is the once-per-run marker of a fault on a zone: Temp/ServerMesh.FaultFired.&lt;key&gt;.&lt;zone&gt;.</summary>
        public static string MarkerPath(string key, string zone)
        {
            return System.IO.Path.Combine("Temp", $"ServerMesh.FaultFired.{key}.{zone}");
        }

        /// <summary>FaultLine is the line logged at error level before dying: <c>FAULT: dying on &lt;what&gt; (&lt;key&gt;)</c>.</summary>
        public static string FaultLine(string key, string what) => $"FAULT: dying on {what} ({key})";

        /// <summary>
        /// FireOnce kills this process for the fault <paramref name="key"/> on <paramref name="zone"/> unless it already fired
        /// in this run (its marker exists): the marker is written first, then the FAULT line, then Process.Kill. Returns
        /// false when the marker says it fired already (nothing happens).
        /// </summary>
        public static bool FireOnce(string key, string zone, string what)
        {
            string marker = MarkerPath(key, zone);
            if (System.IO.File.Exists(marker))
                return false;
            try
            {
                System.IO.Directory.CreateDirectory("Temp");
                System.IO.File.WriteAllText(marker, DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                // A marker that cannot be written only means the restarted incarnation dies too; the fault still fires.
            }
            LiteNetLibManager.Logging.LogError("Mesh", FaultLine(key, what));
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return true;
        }
    }

    /// <summary>
    /// MeshMonsterAreasToggle is MESH_MONSTER_AREAS's `only:` form (decision 13): <c>only:A,B</c> switches the named areas
    /// on (as the plain list does) and every other monster area off, so a scenario's monster total is exact.
    /// </summary>
    public static class MeshMonsterAreasToggle
    {
        public const string OnlyPrefix = "only:";

        /// <summary>
        /// StripOnly: when the value starts with <c>only:</c> (any case), removes the prefix (leaving the comma list) and
        /// returns true; otherwise leaves the value as it is and returns false.
        /// </summary>
        public static bool StripOnly(ref string value)
        {
            if (value == null)
                return false;
            string trimmed = value.TrimStart();
            if (!trimmed.StartsWith(OnlyPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            value = trimmed.Substring(OnlyPrefix.Length);
            return true;
        }

        /// <summary>Names parses a comma list of area names (trimmed, empty ones dropped); null when it names none.</summary>
        public static HashSet<string> Names(string list)
        {
            if (string.IsNullOrWhiteSpace(list))
                return null;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in list.Split(','))
            {
                if (name.Trim().Length > 0)
                    names.Add(name.Trim());
            }
            return names.Count > 0 ? names : null;
        }

        /// <summary>SwitchesOff: under <c>only:</c>, an area the list does not name is off (an empty list names none).</summary>
        public static bool SwitchesOff(bool only, HashSet<string> named, string areaName)
        {
            return only && (named == null || areaName == null || !named.Contains(areaName));
        }
    }
}
