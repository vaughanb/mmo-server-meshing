// Server Meshing add-on — the split planner (Slice 3, slices/slice3_design.md decisions 1-4, plan D3): from a cell's
// latest load report (its occupancy grid, counts and work window) it finds the guillotine cut whose busier side would
// be the least loaded, predicts both sides' utilisation with the fitted cost model, and accepts the cut only when the
// peak is worth a process (splitBenefitRatio). It also predicts what two cells of a family would cost as one (a
// merge). Pure: the coordinator's policy (MeshLayoutCoordinator_Policy.cs) feeds it and acts on the plan.
using System;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshPlannerWeights is the cost model in player-equivalents (an owned player is 1): the per-process floor and
    /// what a puppet player, an owned monster and a puppet monster cost against an owned player (DESIGN 0.71, 0.73).
    /// </summary>
    public sealed class MeshPlannerWeights
    {
        public float floorPlayers = 9.1f;
        public float puppetPlayer = 0.174f;
        public float ownedMonster = 0.126f;
        public float puppetMonster = 0.1f;

        /// <summary>From reads the weights from the registry's dynamic settings (null: the defaults above).</summary>
        public static MeshPlannerWeights From(MeshDynamicSettings settings)
        {
            if (settings == null)
                return new MeshPlannerWeights();
            return new MeshPlannerWeights
            {
                floorPlayers = settings.modelFloorPlayers, puppetPlayer = settings.puppetPlayerWeight,
                ownedMonster = settings.ownedMonsterWeight, puppetMonster = settings.puppetMonsterWeight,
            };
        }

        /// <summary>Error names the first weight that cannot be used (null when all can).</summary>
        public string Error()
        {
            if (!(floorPlayers > 0f))
                return "modelFloorPlayers must be positive";
            if (puppetPlayer < 0f || ownedMonster < 0f || puppetMonster < 0f)
                return "the planner weights must not be negative";
            return null;
        }

        /// <summary>
        /// ExternalPuppetWeight blends the puppet weights by a report's puppet players and monsters: the grid's Puppets
        /// layer counts both together (decision 2). The player weight when a cell holds no puppet.
        /// </summary>
        public float ExternalPuppetWeight(MeshLoadReportMessage report)
        {
            int players = Math.Max(0, report.puppetPlayers), monsters = Math.Max(0, report.puppetMonsters);
            if (players + monsters == 0)
                return puppetPlayer;
            return (puppetPlayer * players + puppetMonster * monsters) / (players + monsters);
        }
    }

    /// <summary>MeshSplitPlan is an accepted cut: where, which side the child takes, and what the model predicts.</summary>
    public sealed class MeshSplitPlan
    {
        public MeshSplitAxis axis;
        public float at;
        public bool childOnPositiveSide;
        /// <summary>The parent's utilisation the plan was made from.</summary>
        public float utilNow;
        public float predictedParent;
        public float predictedChild;
        /// <summary>The larger of the two predictions.</summary>
        public float peak;
        /// <summary>How many cuts were compared.</summary>
        public int candidates;
        public int ownedPlayersKept;
        public int ownedPlayersGiven;

        public string AxisText => axis == MeshSplitAxis.X ? "x" : "z";
        public string SideText => childOnPositiveSide ? "+" : "-";
    }

    /// <summary>MeshSplitPlanner predicts what a cut or a union would cost, from the cells' load reports. Pure.</summary>
    public static class MeshSplitPlanner
    {
        /// <summary>Ratio prints as 0.## with the invariant culture (every policy line uses it).</summary>
        public static string Ratio(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Usable: a report the planner may read (a Valid probe with a budget, a grid, a positive utilisation).</summary>
        public static bool Usable(MeshLoadReportMessage report, out string why)
        {
            why = null;
            if (report == null)
                why = "no report";
            else if (report.work.state != MeshProbeState.Valid)
                why = "probe " + report.work.state;
            else if (report.grid == null)
                why = "no occupancy grid";
            else if (!(report.work.Utilisation > 0f))
                why = "util 0";
            return why == null;
        }

        /// <summary>
        /// Load is the model's player-equivalents for a cell holding what its report counts: the floor, its owned
        /// players and monsters, and its puppets (decision 2, the L_now of a report).
        /// </summary>
        public static float Load(MeshLoadReportMessage report, MeshRect owned, MeshPlannerWeights weights)
        {
            MeshOccupancyGrid grid = report.grid;
            float ownedLoad = grid != null
                ? grid.Total(MeshOccupancyLayer.OwnedPlayers, owned.minX, owned.minZ, owned.maxX, owned.maxZ)
                    + weights.ownedMonster * grid.Total(MeshOccupancyLayer.OwnedMonsters, owned.minX, owned.minZ, owned.maxX, owned.maxZ)
                : report.ownedPlayers + weights.ownedMonster * report.ownedMonsters;
            return weights.floorPlayers + ownedLoad + weights.puppetPlayer * Math.Max(0, report.puppetPlayers) + weights.puppetMonster * Math.Max(0, report.puppetMonsters);
        }

        /// <summary>
        /// TryPlan finds the cut of <paramref name="owned"/> with the lowest peak (decision 3) among every line at a
        /// multiple of the grid's bucket that leaves both sides at least <paramref name="minCellSize"/> (decision 1), and
        /// accepts it when the peak is at most <paramref name="benefitRatio"/> of the utilisation now. False with the
        /// refusal otherwise (<paramref name="plan"/> then still describes the best cut when one was compared).
        /// </summary>
        public static bool TryPlan(MeshLoadReportMessage report, MeshRect owned, float minCellSize, float band, MeshPlannerWeights weights, float benefitRatio, out MeshSplitPlan plan, out string refusal)
        {
            plan = null;
            if (!Usable(report, out refusal))
                return false;
            if (owned.IsEmpty)
            {
                refusal = "the cell owns nothing";
                return false;
            }
            MeshOccupancyGrid grid = report.grid;
            float utilNow = report.work.Utilisation;
            float loadNow = Load(report, owned, weights);
            float external = weights.ExternalPuppetWeight(report);
            MeshSplitPlan best = null;
            int candidates = 0;
            for (int axisIndex = 0; axisIndex < 2; ++axisIndex)
            {
                MeshSplitAxis axis = axisIndex == 0 ? MeshSplitAxis.X : MeshSplitAxis.Z;
                float min = axis == MeshSplitAxis.X ? owned.minX : owned.minZ;
                float max = axis == MeshSplitAxis.X ? owned.maxX : owned.maxZ;
                float middle = (min + max) * 0.5f;
                float bucket = Math.Max(1e-3f, grid.bucketSize);
                long first = (long)Math.Ceiling((min + minCellSize) / bucket - 1e-4), last = (long)Math.Floor((max - minCellSize) / bucket + 1e-4);
                for (long k = first; k <= last; ++k)
                {
                    float at = (float)(k * bucket);
                    if (!MeshSplitOperation.Cut(owned, axis, at, true, out MeshRect high, out MeshRect low))
                        continue;
                    candidates++;
                    float lowLoad = SideLoad(grid, low, high, axis, at, false, band, weights, external);
                    float highLoad = SideLoad(grid, high, low, axis, at, true, band, weights, external);
                    float lowUtil = utilNow * lowLoad / loadNow, highUtil = utilNow * highLoad / loadNow;
                    float peak = Math.Max(lowUtil, highUtil);
                    bool better = best == null || peak < best.peak - 1e-6f
                        || (Math.Abs(peak - best.peak) <= 1e-6f && Math.Abs(at - middle) < Math.Abs(best.at - MiddleOf(owned, best.axis)) - 1e-6f);
                    if (!better)
                        continue;
                    int lowPlayers = grid.Total(MeshOccupancyLayer.OwnedPlayers, low.minX, low.minZ, low.maxX, low.maxZ);
                    int highPlayers = grid.Total(MeshOccupancyLayer.OwnedPlayers, high.minX, high.minZ, high.maxX, high.maxZ);
                    bool childHigh = highPlayers <= lowPlayers;   // the child takes the side with fewer owned players (ties: the positive side)
                    best = new MeshSplitPlan
                    {
                        axis = axis, at = at, childOnPositiveSide = childHigh, utilNow = utilNow, peak = peak,
                        predictedParent = childHigh ? lowUtil : highUtil, predictedChild = childHigh ? highUtil : lowUtil,
                        ownedPlayersKept = childHigh ? lowPlayers : highPlayers, ownedPlayersGiven = childHigh ? highPlayers : lowPlayers,
                    };
                }
            }
            if (best == null)
            {
                refusal = $"no cut leaves both sides at least {MeshSplitOperation.Number(minCellSize)} m";
                return false;
            }
            best.candidates = candidates;
            plan = best;
            if (best.peak > benefitRatio * utilNow + 1e-6f)
            {
                refusal = $"best peak {Ratio(best.peak)} over {Ratio(benefitRatio)} x util {Ratio(utilNow)}";
                return false;
            }
            refusal = null;
            return true;
        }

        private static float MiddleOf(MeshRect owned, MeshSplitAxis axis) => axis == MeshSplitAxis.X ? (owned.minX + owned.maxX) * 0.5f : (owned.minZ + owned.maxZ) * 0.5f;

        /// <summary>
        /// SideLoad is a side's player-equivalents after the cut (decision 2): the floor, what it owns, the puppets
        /// within the band of it (the ring outside the parent), and the other side's owned entities within the band of
        /// the cut, which become its puppets.
        /// </summary>
        private static float SideLoad(MeshOccupancyGrid grid, MeshRect side, MeshRect other, MeshSplitAxis axis, float at, bool sideIsHigh, float band, MeshPlannerWeights weights, float external)
        {
            float load = weights.floorPlayers
                + grid.Total(MeshOccupancyLayer.OwnedPlayers, side.minX, side.minZ, side.maxX, side.maxZ)
                + weights.ownedMonster * grid.Total(MeshOccupancyLayer.OwnedMonsters, side.minX, side.minZ, side.maxX, side.maxZ)
                + external * grid.Total(MeshOccupancyLayer.Puppets, side.minX - band, side.minZ - band, side.maxX + band, side.maxZ + band);
            MeshRect strip = axis == MeshSplitAxis.X
                ? (sideIsHigh ? new MeshRect(Math.Max(other.minX, at - band), other.minZ, at, other.maxZ) : new MeshRect(at, other.minZ, Math.Min(other.maxX, at + band), other.maxZ))
                : (sideIsHigh ? new MeshRect(other.minX, Math.Max(other.minZ, at - band), other.maxX, at) : new MeshRect(other.minX, at, other.maxX, Math.Min(other.maxZ, at + band)));
            if (!strip.IsEmpty)
            {
                load += weights.puppetPlayer * grid.Total(MeshOccupancyLayer.OwnedPlayers, strip.minX, strip.minZ, strip.maxX, strip.maxZ)
                    + weights.puppetMonster * grid.Total(MeshOccupancyLayer.OwnedMonsters, strip.minX, strip.minZ, strip.maxX, strip.maxZ);
            }
            return load;
        }

        /// <summary>
        /// TryPredictUnion predicts the utilisation of one cell holding what two cells of a family hold now (decision 4):
        /// both cells' owned entities plus each one's puppets outside the union, at the larger of the two cells' measured
        /// cost per player-equivalent. False with the reason when either report cannot be read.
        /// </summary>
        public static bool TryPredictUnion(MeshLoadReportMessage a, MeshRect ownedA, MeshLoadReportMessage b, MeshRect ownedB, MeshRect union, float band, MeshPlannerWeights weights, out float predicted, out string why)
        {
            predicted = 0f;
            if (!Usable(a, out why) || !Usable(b, out why))
                return false;
            float loadA = Load(a, ownedA, weights), loadB = Load(b, ownedB, weights);
            float scale = Math.Max(a.work.Utilisation / loadA, b.work.Utilisation / loadB);
            float load = weights.floorPlayers + OwnedLoad(a.grid, ownedA, weights) + OwnedLoad(b.grid, ownedB, weights)
                + PuppetsOutside(a, ownedA, union, band, weights) + PuppetsOutside(b, ownedB, union, band, weights);
            predicted = scale * load;
            why = null;
            return true;
        }

        private static float OwnedLoad(MeshOccupancyGrid grid, MeshRect owned, MeshPlannerWeights weights)
        {
            return grid.Total(MeshOccupancyLayer.OwnedPlayers, owned.minX, owned.minZ, owned.maxX, owned.maxZ)
                + weights.ownedMonster * grid.Total(MeshOccupancyLayer.OwnedMonsters, owned.minX, owned.minZ, owned.maxX, owned.maxZ);
        }

        /// <summary>The weighted puppets of a cell that lie outside the union (its puppets inside it are the sibling's entities).</summary>
        private static float PuppetsOutside(MeshLoadReportMessage report, MeshRect owned, MeshRect union, float band, MeshPlannerWeights weights)
        {
            MeshOccupancyGrid grid = report.grid;
            int all = grid.Total(MeshOccupancyLayer.Puppets, owned.minX - band, owned.minZ - band, owned.maxX + band, owned.maxZ + band);
            int inside = grid.Total(MeshOccupancyLayer.Puppets, union.minX, union.minZ, union.maxX, union.maxZ);
            return weights.ExternalPuppetWeight(report) * Math.Max(0, all - inside);
        }
    }
}
