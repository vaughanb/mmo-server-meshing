// Server Meshing add-on — the central's view of every cell's load (DESIGN.md 0.71): the latest report per cell, kept
// for the split/merge planner and summarised in the central's log.
using System.Collections.Generic;
using System.Text;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLoadBoard keeps the latest load report of every cell, with the time it arrived.</summary>
    public sealed class MeshLoadBoard
    {
        /// <summary>MeshLoadEntry is one cell's latest report.</summary>
        public struct MeshLoadEntry
        {
            public MeshLoadReportMessage report;
            public float receivedAt;
        }

        private readonly Dictionary<string, MeshLoadEntry> _latest = new Dictionary<string, MeshLoadEntry>(System.StringComparer.OrdinalIgnoreCase);

        public int Count => _latest.Count;

        /// <summary>Record keeps a report as its cell's latest; a report without a cell id is refused and reported false.</summary>
        public bool Record(MeshLoadReportMessage report, float now)
        {
            if (report == null || string.IsNullOrEmpty(report.cellId))
                return false;
            _latest[report.cellId] = new MeshLoadEntry { report = report, receivedAt = now };
            return true;
        }

        /// <summary>TryGet returns a cell's latest report.</summary>
        public bool TryGet(string cellId, out MeshLoadEntry entry) => _latest.TryGetValue(cellId ?? string.Empty, out entry);

        /// <summary>Forget drops a cell (it exited).</summary>
        public void Forget(string cellId)
        {
            if (!string.IsNullOrEmpty(cellId))
                _latest.Remove(cellId);
        }

        /// <summary>Summary is one line per the central's log: every cell with a report no older than maxAge seconds, by id.</summary>
        public string Summary(float now, float maxAge)
        {
            List<string> ids = new List<string>(_latest.Keys);
            ids.Sort(System.StringComparer.OrdinalIgnoreCase);
            StringBuilder line = new StringBuilder();
            foreach (string id in ids)
            {
                MeshLoadEntry entry = _latest[id];
                if (now - entry.receivedAt > maxAge)
                    continue;
                MeshLoadReportMessage r = entry.report;
                if (line.Length > 0)
                    line.Append(" | ");
                line.Append(id).Append(' ');
                if (r.work.state == MeshProbeState.Valid)
                    line.Append("util ").Append((r.work.Utilisation * 100f).ToString("0")).Append("% work ").Append(r.work.workMeanMs.ToString("0.0")).Append(" ms");
                else
                    line.Append("probe ").Append(r.work.state);
                line.Append(" owned ").Append(r.ownedPlayers).Append("p/").Append(r.ownedMonsters).Append('m')
                    .Append(" puppets ").Append(r.puppetPlayers).Append("p/").Append(r.puppetMonsters).Append('m')
                    .Append(" clients ").Append(r.clients);
            }
            return line.Length > 0 ? line.ToString() : "no recent reports";
        }
    }
}
