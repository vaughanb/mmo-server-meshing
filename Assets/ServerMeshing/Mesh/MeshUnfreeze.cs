// Server Meshing add-on — the pure parts of the operator's unfreeze (DESIGN 0.85, slices/unfreeze_design.md decisions 3-5):
// the fence floor that fences every process of a dead id started before the command, the classification of a frozen
// family's entries (a dead id owning a rectangle collapses, one owning nothing is retired, a live cell stays as it is),
// the retire version, and which member of a stored operation is dead. The coordinator (MeshLayoutCoordinator_Unfreeze.cs)
// acts on them.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshUnfreeze holds the unfreeze's pure decisions.</summary>
    public static class MeshUnfreeze
    {
        /// <summary>
        /// FenceFloor is an incarnation text for <paramref name="utcTicks"/> (hex ticks, as a cell's incarnation is): fenced,
        /// it fences every incarnation of the cell not newer than it (MeshFencedSet.IsFenced), so every process started before.
        /// </summary>
        public static string FenceFloor(long utcTicks) => Math.Max(1L, utcTicks).ToString("x", CultureInfo.InvariantCulture);

        /// <summary>
        /// Classify sorts the dead ids of a family by their entry in <paramref name="broadcast"/>: Active and owning a
        /// rectangle → <paramref name="collapse"/>; Pending, Draining or Active owning nothing → <paramref name="retire"/>;
        /// no entry, a Retired one, or one of another family → neither. Ids keep the order given.
        /// </summary>
        public static void Classify(MeshLayoutSnapshot broadcast, string baseId, IEnumerable<string> dead, List<string> collapse, List<string> retire)
        {
            if (dead == null)
                return;
            foreach (string id in dead)
            {
                MeshLayoutEntry entry = EntryOf(broadcast, id);
                if (entry == null || entry.state == MeshCellState.Retired || !Same(entry.baseId, baseId))
                    continue;
                if (entry.state == MeshCellState.Active && !entry.owned.IsEmpty)
                    collapse?.Add(entry.id);
                else
                    retire?.Add(entry.id);
            }
        }

        /// <summary>Retired is <paramref name="broadcast"/> at <paramref name="pair"/> with each of <paramref name="ids"/> Retired, owning and interested in nothing.</summary>
        public static MeshLayoutSnapshot Retired(MeshLayoutSnapshot broadcast, IEnumerable<string> ids, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = broadcast.Clone();
            next.pair = pair;
            foreach (string id in ids)
            {
                MeshLayoutEntry entry = EntryOf(next, id);
                if (entry == null)
                    continue;
                entry.state = MeshCellState.Retired;
                entry.owned = MeshRect.Empty;
                entry.interest = MeshRect.Empty;
            }
            return next;
        }

        /// <summary>DeadMember is the first member of the stored operation (a split's parent or child, a merge's survivor or retiring cell) among <paramref name="dead"/>, or null.</summary>
        public static string DeadMember(MeshStoredOperation stored, ICollection<string> dead)
        {
            if (stored == null || dead == null)
                return null;
            string[] members = stored.IsSplit ? new[] { stored.parentId, stored.childId } : new[] { stored.survivorId, stored.retiringId };
            foreach (string member in members)
            {
                foreach (string id in dead)
                {
                    if (!string.IsNullOrEmpty(member) && Same(member, id))
                        return member;
                }
            }
            return null;
        }

        private static MeshLayoutEntry EntryOf(MeshLayoutSnapshot snapshot, string id)
        {
            if (snapshot == null || snapshot.entries == null)
                return null;
            return snapshot.entries.Find(e => e != null && Same(e.id, id));
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
