// Server Meshing add-on — where the login sends a character on a meshed map (DESIGN.md §9, plan D7): the cell that owns
// its saved position in the live layout, else the nearest cell whose server is up. Pure, so the rules are tested apart
// from the login server.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLoginRouting picks the cell a character logs into from the live layout and the cells whose servers are registered.</summary>
    public static class MeshLoginRouting
    {
        /// <summary>
        /// OwnerAt is the cell that owns a position and may take players there, or null: a cell being made (Pending), one
        /// being emptied (Draining) or a Retired one takes no logins.
        /// </summary>
        public static MeshCell OwnerAt(MeshWorldRegistry world, string mapId, Vector3 scenePos)
        {
            if (world == null)
                return null;
            MeshCell cell = world.CellAt(mapId, scenePos);
            return cell != null && cell.IsRoutable ? cell : null;
        }

        /// <summary>
        /// Route is the owner when its server is live, else the live routable cell of the map nearest the position
        /// (<paramref name="fellBack"/> is then true; a tie keeps the order of <paramref name="liveCellIds"/>), else null.
        /// </summary>
        public static MeshCell Route(MeshWorldRegistry world, string mapId, Vector3 scenePos, IEnumerable<string> liveCellIds, out bool fellBack)
        {
            fellBack = false;
            if (world == null || liveCellIds == null)
                return null;
            MeshCell owner = OwnerAt(world, mapId, scenePos);
            MeshCell best = null;
            float bestDistance = float.MaxValue;
            foreach (string id in liveCellIds)
            {
                if (owner != null && string.Equals(id, owner.id, StringComparison.OrdinalIgnoreCase))
                    return owner;
                MeshCell candidate = world.GetCell(mapId, id);
                if (candidate == null || !candidate.IsRoutable)
                    continue;
                // Geometric distance to the candidate's owned rectangle: grid indices mean nothing for explicit or
                // re-cut cells.
                float distance = candidate.DistanceTo(candidate, scenePos);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            fellBack = best != null;
            return best;
        }
    }

    /// <summary>
    /// MeshRecoveryWait is the login's wait for a recovering central (Slice 5, slices/slice5_design.md decision 9): a
    /// login routes once the coordinator runs, or by the layout as it stands when the budget is spent. Pure.
    /// </summary>
    public static class MeshRecoveryWait
    {
        /// <summary>KeepWaiting: the coordinator still recovers and the budget is not spent.</summary>
        public static bool KeepWaiting(bool recovering, float waitedSeconds, float budgetSeconds)
        {
            return recovering && waitedSeconds < Math.Max(0f, budgetSeconds);
        }

        /// <summary>Line is what the login logs once it stops waiting (null when it never waited): the Info line, or the Warning when it gave up.</summary>
        public static string Line(bool waited, bool stillRecovering, float waitedSeconds, out bool warning)
        {
            warning = stillRecovering;
            if (!waited)
                return null;
            string s = waitedSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return stillRecovering
                ? $"Login routing: the layout recovery is not done after {s} s; routing by the layout as it stands"
                : $"Login routing: waited {s} s for the layout recovery";
        }
    }
}
