// Server Meshing add-on — rig phantom layout: where phantom groups, their filler and placement-area phantoms stand, so each sees V movers and no other group.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomGroup is one disc of a phantom layout (scene frame, y is z): its centre and radius, the square its
    /// members walk in (the disc's inscribed square), and the seeded start positions of its phantoms and filler.
    /// </summary>
    public sealed class ServerMeshPhantomGroup
    {
        /// <summary>ServerMeshPhantomGroup builds a group; the position arrays are kept, not copied.</summary>
        public ServerMeshPhantomGroup(int index, Vector2 centre, float radius, Vector2[] phantomPositions, Vector2[] fillerPositions)
        {
            Index = index;
            Centre = centre;
            Radius = radius;
            Region = ServerMeshPhantomLayout.InscribedSquare(centre, radius);
            PhantomPositions = phantomPositions ?? throw new ArgumentNullException(nameof(phantomPositions));
            FillerPositions = fillerPositions ?? throw new ArgumentNullException(nameof(fillerPositions));
        }

        /// <summary>Index is the group's number in its layout, from 0.</summary>
        public int Index { get; }
        /// <summary>Centre is the disc's centre.</summary>
        public Vector2 Centre { get; }
        /// <summary>Radius is the disc's radius r.</summary>
        public float Radius { get; }
        /// <summary>Region is the disc's inscribed square (side r·√2): where its members spawn and walk.</summary>
        public Rect Region { get; }
        /// <summary>PhantomCount is how many phantoms the group holds.</summary>
        public int PhantomCount => PhantomPositions.Length;
        /// <summary>FillerCount is how many server-owned filler movers the group holds.</summary>
        public int FillerCount => FillerPositions.Length;
        /// <summary>PhantomPositions are the phantoms' start positions inside Region.</summary>
        public Vector2[] PhantomPositions { get; }
        /// <summary>FillerPositions are the filler movers' start positions inside Region.</summary>
        public Vector2[] FillerPositions { get; }

        /// <summary>Line is the group's log line, centre and side in metres to 0.1.</summary>
        public string Line()
        {
            return FormattableString.Invariant(
                $"Phantom group {Index}: centre {Centre.x:0.0} {Centre.y:0.0} side {Region.width:0.0} phantoms {PhantomCount} filler {FillerCount}");
        }
    }

    /// <summary>
    /// ServerMeshPhantomLayout places phantom groups (a pure function of its arguments and the seed). Character kind: G =
    /// ceil(P / (V+1)) groups share the phantoms evenly and each tops up with filler to V+1 members, so every phantom sees V
    /// movers besides its own object. Eye kind: one group of V filler at the area's centre holding every eye (eyes are hidden
    /// from each other). Each group is a disc of radius r whose members stay in its inscribed square, so any two are at most
    /// 2r ≤ R − 1 apart; centres sit on a near-square grid at a pitch above R + 1 + 2r, so no two groups see each other
    /// (the AOI test is inclusive). Discs keep r inside the area and max(bandWidth, R) + 1 + r inside an edge shared with a
    /// band neighbour; r is the largest value in 0.01 m steps, at most (R − 1)/2, at which the groups fit.
    /// </summary>
    public static class ServerMeshPhantomLayout
    {
        /// <summary>Margin is the metre kept between the inclusive AOI range and the layout's distances.</summary>
        public const float Margin = 1f;
        /// <summary>MinRadius is the smallest disc radius a layout accepts.</summary>
        public const float MinRadius = 1f;

        private static readonly ServerMeshPhantomGroup[] s_none = new ServerMeshPhantomGroup[0];
        private static readonly Vector2[] s_noKeepOut = new Vector2[0];

        /// <summary>GroupCount is G = ceil(P / (V+1)) for the character kind, 1 for eyes with a view, 0 otherwise.</summary>
        public static int GroupCount(int phantoms, int view, bool eyes)
        {
            if (phantoms <= 0 || view < 0)
                return 0;
            if (eyes)
                return view == 0 ? 0 : 1;
            return view < 1 ? 0 : (int)((phantoms + (long)view) / (view + 1L));
        }

        /// <summary>InscribedSquare is the square inscribed in the disc of the given centre and radius (side r·√2).</summary>
        public static Rect InscribedSquare(Vector2 centre, float radius)
        {
            float half = radius * 0.70710678f;
            return new Rect(centre.x - half, centre.y - half, 2f * half, 2f * half);
        }

        /// <summary>
        /// Build lays out phantoms (P) for view (V) in area (the cell's scene rect, y is z) at visible range R. The band flags
        /// name the edges shared with a band neighbour. It returns null with an error for P below 1, V below 1 with characters,
        /// a range or area that cannot hold a 1 m disc, or groups that do not fit (naming the groups needed and the most that
        /// fit). Eyes with view 0 (host mode) return no groups and no error.
        /// </summary>
        public static ServerMeshPhantomGroup[] Build(int phantoms, int view, float range, Rect area, float bandWidth, bool bandWest, bool bandEast, bool bandSouth, bool bandNorth, int seed, bool eyes, out string error)
        {
            return Build(phantoms, view, range, area, bandWidth, bandWest, bandEast, bandSouth, bandNorth, seed, eyes, s_noKeepOut, 0f, out error);
        }

        /// <summary>
        /// Build with keep-out points (scene frame, y is z), such as warp portals: no group disc reaches within keepOutRadius
        /// of one, or its members would walk into it. r is then the largest value in 0.01 m steps at which the groups fit
        /// and clear every point; none gives an error naming the points.
        /// </summary>
        public static ServerMeshPhantomGroup[] Build(int phantoms, int view, float range, Rect area, float bandWidth, bool bandWest, bool bandEast, bool bandSouth, bool bandNorth, int seed, bool eyes, Vector2[] keepOut, float keepOutRadius, out string error)
        {
            keepOut = keepOut ?? s_noKeepOut;
            error = null;
            if (phantoms < 1)
            {
                error = FormattableString.Invariant($"the layout needs at least 1 phantom, not {phantoms}");
                return null;
            }
            if (eyes && view == 0)
                return s_none;
            if (view < 1)
            {
                error = FormattableString.Invariant($"the layout needs V of at least 1, not {view}");
                return null;
            }
            if (float.IsNaN(range) || float.IsInfinity(range) || range < 2f * MinRadius + Margin)
            {
                error = FormattableString.Invariant($"the visible range {range:0.00} m cannot hold a {MinRadius:0} m disc");
                return null;
            }
            if (!(area.width > 0f) || !(area.height > 0f))
            {
                error = "the layout area is empty";
                return null;
            }
            int groups = GroupCount(phantoms, view, eyes);
            PhantomLayoutEdges edges = new PhantomLayoutEdges(area, range, bandWidth, bandWest, bandEast, bandSouth, bandNorth);
            int radiusCm = LargestFittingRadiusCm(groups, edges);
            if (radiusCm >= 0 && keepOut.Length > 0)
            {
                int low = (int)Math.Round(MinRadius * 100f);
                while (radiusCm >= low && !(Fits(groups, radiusCm / 100.0, edges) && Clear(groups, radiusCm / 100.0, edges, keepOut, keepOutRadius)))
                    --radiusCm;
                if (radiusCm < low)
                {
                    error = FormattableString.Invariant($"the layout's {groups} groups cannot keep {keepOutRadius:0.0} m clear of {keepOut.Length} keep-out point(s) (P {phantoms} V {view} R {range:0.0} m)");
                    return null;
                }
            }
            if (radiusCm < 0)
            {
                int most = 0;
                for (int g = groups - 1; g >= 1 && most == 0; --g)
                {
                    if (Fits(g, MinRadius, edges))
                        most = g;
                }
                error = FormattableString.Invariant($"the layout needs {groups} groups, {most} fit (P {phantoms} V {view} R {range:0.0} m)");
                return null;
            }
            float radius = radiusCm / 100f;
            ServerMeshPhantomGroup[] result = new ServerMeshPhantomGroup[groups];
            Vector2[] centres = Centres(groups, radius, edges);
            for (int g = 0; g < groups; ++g)
            {
                int members = eyes ? phantoms : phantoms / groups + (g < phantoms % groups ? 1 : 0);
                int filler = eyes ? view : Math.Max(0, view + 1 - members);
                Rect region = InscribedSquare(centres[g], radius);
                System.Random random = new System.Random(GroupSeed(seed, g));
                result[g] = new ServerMeshPhantomGroup(g, centres[g], radius, Scatter(members, region, random), Scatter(filler, region, random));
            }
            return result;
        }

        /// <summary>
        /// PlaceInArea gives phantoms start positions uniformly by seed inside a rectangle (centre and size) or, when disc is
        /// true, inside the disc of the given centre and radius.
        /// </summary>
        public static Vector2[] PlaceInArea(int phantoms, bool disc, Vector2 centre, Vector2 size, float radius, int seed)
        {
            Vector2[] positions = new Vector2[Math.Max(0, phantoms)];
            System.Random random = new System.Random(seed);
            for (int i = 0; i < positions.Length; ++i)
            {
                if (!disc)
                {
                    positions[i] = new Vector2(centre.x + ((float)random.NextDouble() - 0.5f) * size.x, centre.y + ((float)random.NextDouble() - 0.5f) * size.y);
                    continue;
                }
                double angle = random.NextDouble() * 2.0 * Math.PI;
                double distance = radius * Math.Sqrt(random.NextDouble());
                positions[i] = new Vector2(centre.x + (float)(Math.Cos(angle) * distance), centre.y + (float)(Math.Sin(angle) * distance));
            }
            return positions;
        }

        private readonly struct PhantomLayoutEdges
        {
            public readonly double xMin, xMax, zMin, zMax, range, west, east, south, north;

            public PhantomLayoutEdges(Rect area, float range, float bandWidth, bool bandWest, bool bandEast, bool bandSouth, bool bandNorth)
            {
                xMin = area.xMin;
                xMax = area.xMax;
                zMin = area.yMin;
                zMax = area.yMax;
                this.range = range;
                double band = Math.Max(bandWidth, range) + Margin;
                west = bandWest ? band : 0.0;
                east = bandEast ? band : 0.0;
                south = bandSouth ? band : 0.0;
                north = bandNorth ? band : 0.0;
            }

            public double Width(double r) => (xMax - east - r) - (xMin + west + r);
            public double Height(double r) => (zMax - north - r) - (zMin + south + r);
        }

        private static int LargestFittingRadiusCm(int groups, PhantomLayoutEdges edges)
        {
            int high = (int)Math.Floor((edges.range - Margin) / 2.0 * 100.0 + 1e-6);
            int low = (int)Math.Round(MinRadius * 100f);
            if (high < low || !Fits(groups, low / 100.0, edges))
                return -1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (Fits(groups, mid / 100.0, edges))
                    low = mid;
                else
                    high = mid - 1;
            }
            return low;
        }

        private static bool Clear(int groups, double r, PhantomLayoutEdges edges, Vector2[] keepOut, float keepOutRadius)
        {
            Vector2[] centres = Centres(groups, r, edges);
            double reach = r + keepOutRadius;
            for (int g = 0; g < centres.Length; ++g)
            {
                for (int i = 0; i < keepOut.Length; ++i)
                {
                    if ((centres[g] - keepOut[i]).sqrMagnitude < reach * reach)
                        return false;
                }
            }
            return true;
        }

        private static void Grid(int groups, out int columns, out int rows)
        {
            columns = 1;
            while (columns * columns < groups)
                ++columns;
            rows = (groups + columns - 1) / columns;
        }

        private static bool Fits(int groups, double r, PhantomLayoutEdges edges)
        {
            double width = edges.Width(r), height = edges.Height(r);
            if (width < 0.0 || height < 0.0)
                return false;
            Grid(groups, out int columns, out int rows);
            double pitch = edges.range + Margin + 2.0 * r;
            return (columns == 1 || (columns - 1) * pitch < width) && (rows == 1 || (rows - 1) * pitch < height);
        }

        private static Vector2[] Centres(int groups, double r, PhantomLayoutEdges edges)
        {
            Grid(groups, out int columns, out int rows);
            double width = edges.Width(r), height = edges.Height(r);
            double left = edges.xMin + edges.west + r, bottom = edges.zMin + edges.south + r;
            double pitchX = columns > 1 ? width / (columns - 1) : 0.0;
            double pitchZ = rows > 1 ? height / (rows - 1) : 0.0;
            double startX = columns > 1 ? left : left + width / 2.0;
            double startZ = rows > 1 ? bottom : bottom + height / 2.0;
            Vector2[] centres = new Vector2[groups];
            for (int g = 0; g < groups; ++g)
                centres[g] = new Vector2((float)(startX + (g % columns) * pitchX), (float)(startZ + (g / columns) * pitchZ));
            return centres;
        }

        private static Vector2[] Scatter(int count, Rect region, System.Random random)
        {
            Vector2[] positions = new Vector2[count];
            for (int i = 0; i < count; ++i)
                positions[i] = new Vector2(region.xMin + (float)random.NextDouble() * region.width, region.yMin + (float)random.NextDouble() * region.height);
            return positions;
        }

        private static int GroupSeed(int seed, int group)
        {
            unchecked { return (seed * 486187739) ^ (group * 16777619 + 97); }
        }
    }
}
