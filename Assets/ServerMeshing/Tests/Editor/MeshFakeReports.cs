using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeReports builds load reports with occupancy grids for the planner and policy tests (Slice 3).</summary>
    internal static class MeshFakeReports
    {
        public const float Budget = 1000f / 60f;

        /// <summary>
        /// Report makes a Valid report of a cell owning <paramref name="owned"/>, at <paramref name="util"/> of the 60 fps
        /// budget, with owned players at the given world positions and puppets at theirs (the grid covers owned ± band).
        /// </summary>
        public static MeshLoadReportMessage Report(string cellId, MeshRect owned, float util, IEnumerable<(float x, float z)> players = null,
            IEnumerable<(float x, float z)> puppets = null, IEnumerable<(float x, float z)> monsters = null, float band = 80f, string incarnation = "pc",
            float saturatedShare = 0f, float cpuPercent = 50f)
        {
            MeshLoadReportMessage report = new MeshLoadReportMessage
            {
                cellId = cellId, mapId = MeshFakeWorlds.Map1, incarnation = incarnation, windowSeconds = 5f, cpuPercent = cpuPercent,
                work = new MeshWorkWindow { frames = 300, workMeanMs = util * Budget, workP95Ms = util * Budget, budgetMs = Budget, saturatedShare = saturatedShare, state = MeshProbeState.Valid },
                grid = MeshOccupancyGrid.Covering(owned.minX - band, owned.minZ - band, owned.maxX + band, owned.maxZ + band, band / 4f),
            };
            foreach ((float x, float z) in players ?? new (float, float)[0])
            {
                report.ownedPlayers++;
                report.grid.Add(MeshOccupancyLayer.OwnedPlayers, x, z);
            }
            foreach ((float x, float z) in monsters ?? new (float, float)[0])
            {
                report.ownedMonsters++;
                report.grid.Add(MeshOccupancyLayer.OwnedMonsters, x, z);
            }
            foreach ((float x, float z) in puppets ?? new (float, float)[0])
            {
                report.puppetPlayers++;
                report.grid.Add(MeshOccupancyLayer.Puppets, x, z);
            }
            return report;
        }

        /// <summary>Spread puts n points on a vertical line at x, spread evenly over z in (-90, 90).</summary>
        public static List<(float, float)> Spread(float x, int n)
        {
            List<(float, float)> points = new List<(float, float)>();
            for (int i = 0; i < n; ++i)
                points.Add((x, -90f + 180f * (i + 0.5f) / n));
            return points;
        }
    }
}
