// Server Meshing test rig — the MESH_LOAD_SHIPS grammar (Phase 0 step 7): which cell spawns how many load ships of
// which authority. Rig-only: the space game at commit 1a3a58d9 spawns its ships in waves instead.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipLoadPlan is a parsed MESH_LOAD_SHIPS value: comma-separated parts &lt;cell&gt;:&lt;n&gt;:&lt;ai|mirror|parked&gt;,
    /// each trimmed, the authority keyword in any case, n from 1 to 2000. One cell may hold several groups, one per
    /// authority.
    /// </summary>
    public sealed class ServerMeshShipLoadPlan
    {
        /// <summary>MaxCount is the largest ship count one group accepts.</summary>
        public const int MaxCount = 2000;

        private readonly List<ServerMeshShipLoadGroup> _groups;

        private ServerMeshShipLoadPlan(List<ServerMeshShipLoadGroup> groups)
        {
            _groups = groups;
        }

        /// <summary>Groups is the plan's groups in the order given.</summary>
        public IReadOnlyList<ServerMeshShipLoadGroup> Groups => _groups;

        /// <summary>CountFor is the total ship count of <paramref name="cell"/>'s groups; 0 for a cell the plan does not name.</summary>
        public int CountFor(string cell)
        {
            int total = 0;
            foreach (ServerMeshShipLoadGroup group in _groups)
                if (string.Equals(group.Cell, cell, StringComparison.Ordinal))
                    total += group.Count;
            return total;
        }

        /// <summary>
        /// TryParse reads a plan. It returns null with an error naming the part for an empty text or part, a part that is
        /// not three fields, an empty cell, a missing, non-integer or out-of-range count, an unknown authority and a
        /// duplicate (cell, authority).
        /// </summary>
        public static ServerMeshShipLoadPlan TryParse(string text, out string error)
        {
            error = null;
            string trimmed = text == null ? string.Empty : text.Trim();
            if (trimmed.Length == 0)
            {
                error = "the ship load plan is empty";
                return null;
            }
            List<ServerMeshShipLoadGroup> groups = new List<ServerMeshShipLoadGroup>();
            string[] parts = trimmed.Split(',');
            for (int i = 0; i < parts.Length; ++i)
            {
                string part = parts[i].Trim();
                string shown = Sync.MeshStateSender.CleanForLog(part);
                if (part.Length == 0)
                {
                    error = FormattableString.Invariant($"part {i + 1} is empty");
                    return null;
                }
                string[] fields = part.Split(':');
                if (fields.Length != 3)
                {
                    error = $"part '{shown}' is not cell:count:ai|mirror|parked";
                    return null;
                }
                string cell = fields[0].Trim();
                if (cell.Length == 0)
                {
                    error = $"part '{shown}' names no cell";
                    return null;
                }
                string countText = fields[1].Trim();
                if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1 || count > MaxCount)
                {
                    error = $"part '{shown}': the count must be a whole number from 1 to {MaxCount.ToString(CultureInfo.InvariantCulture)}";
                    return null;
                }
                if (!TryParseAuthority(fields[2].Trim(), out ServerMeshShipAuthority authority))
                {
                    error = $"part '{shown}': the authority is not ai, mirror or parked";
                    return null;
                }
                foreach (ServerMeshShipLoadGroup group in groups)
                {
                    if (string.Equals(group.Cell, cell, StringComparison.Ordinal) && group.Authority == authority)
                    {
                        error = $"part '{shown}' repeats cell {Sync.MeshStateSender.CleanForLog(cell)} with the same authority";
                        return null;
                    }
                }
                groups.Add(new ServerMeshShipLoadGroup(cell, count, authority));
            }
            return new ServerMeshShipLoadPlan(groups);
        }

        private static bool TryParseAuthority(string text, out ServerMeshShipAuthority authority)
        {
            authority = ServerMeshShipAuthority.ServerAI;
            if (string.Equals(text, "ai", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(text, "mirror", StringComparison.OrdinalIgnoreCase))
            {
                authority = ServerMeshShipAuthority.OwnerMirror;
                return true;
            }
            if (string.Equals(text, "parked", StringComparison.OrdinalIgnoreCase))
            {
                authority = ServerMeshShipAuthority.Parked;
                return true;
            }
            return false;
        }
    }
}
