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
}
