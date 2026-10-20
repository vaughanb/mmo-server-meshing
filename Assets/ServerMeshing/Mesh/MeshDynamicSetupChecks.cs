// Server Meshing add-on — the Setup window's checks of Dynamic maps (Slice 6, slices/slice6_design.md decisions 1-7).
// Pure: they read a registry's asset cut and settings as the runtime computes them and return findings, which the
// window shows as Validate lines. They say before Play what the central would only find out later: a base cell that can
// never split, a minimum cell the band overrides, a world whose cut can pass the 32 band neighbours a cell streams to or
// the 64 links a cell takes, spawners' served cells that send a base's children elsewhere, and the settings the central
// checks at start. They judge the asset's band: a MESH_BAND or MESH_SPAWN_CELLS set for a run is not seen.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshSetupSeverity is how a finding reads: a fault the operator must fix, or a limit to know.</summary>
    public enum MeshSetupSeverity
    {
        Warning,
        Error,
    }

    /// <summary>MeshSetupFix is the asset edit that clears a finding without changing what runs, if there is one.</summary>
    public enum MeshSetupFix
    {
        None,
        /// <summary>Set the map's minCellSize to the band: the runtime already uses the band in its place.</summary>
        RaiseMinCellSizeToBand,
    }

    /// <summary>MeshSetupFinding is one thing the checks found: a line of the Setup window's Validate panel.</summary>
    public sealed class MeshSetupFinding
    {
        public MeshSetupSeverity severity;
        public string mapId;
        public readonly List<string> cellIds = new List<string>();
        public string text;
        public MeshSetupFix fix;

        public override string ToString() => $"{severity}: {text}";
    }

    /// <summary>MeshSetupSpawner is one map-spawn server prefab as the checks see it: its name and its servedCells.</summary>
    public sealed class MeshSetupSpawner
    {
        public string owner;
        public string[] servedCells;
    }

    /// <summary>MeshDynamicSetupChecks judges a registry's Dynamic maps before any server starts.</summary>
    public static class MeshDynamicSetupChecks
    {
        /// <summary>How many cell ids a line names before it says how many more there are.</summary>
        public const int IdsShown = 10;
        /// <summary>The links a cell's mesh link server takes (MeshMapNetworkManager_Ghosting's StartServer).</summary>
        public const int MaxLinks = 64;
        /// <summary>The band a process uses when the asset's is not above 0.</summary>
        public const float FallbackBand = 80f;
        private const float Eps = 1e-3f;
        // Rule 4 lets an owned rectangle overhang its base by 1e-3 on each side: the bound compares gaps with this slack.
        private const float GapSlack = 2e-3f;

        /// <summary>The asset's band: dynamicSettings.bandWidth, or 80 m when it is not above 0 (as the map servers fall back).</summary>
        public static float AssetBand(MeshDynamicSettings settings)
        {
            return settings != null && settings.bandWidth > 0f ? settings.bandWidth : FallbackBand;
        }

        /// <summary>The map's minimum cell as the operator set it: its own minCellSize, else the registry's default.</summary>
        public static float RawMinCellSize(MeshMapLayout layout, MeshDynamicSettings settings)
        {
            return layout != null && layout.minCellSize > 0f ? layout.minCellSize : settings != null ? settings.defaultMinCellSize : 0f;
        }

        /// <summary>The minimum cell the runtime uses: never under the band (MeshWorldRegistry.ValidationWorld, the policy).</summary>
        public static float EffectiveMinCellSize(MeshMapLayout layout, MeshDynamicSettings settings, float band)
        {
            return Math.Max(band, RawMinCellSize(layout, settings));
        }

        /// <summary>The most live cells one base may become: the map's maxCellsPerBase, else the registry's default (0 or less: no limit).</summary>
        public static int EffectiveMaxCellsPerBase(MeshMapLayout layout, MeshDynamicSettings settings)
        {
            return layout != null && layout.maxCellsPerBase > 0 ? layout.maxCellsPerBase : settings != null ? settings.defaultMaxCellsPerBase : 1;
        }

        /// <summary>
        /// CanSplit: a cut keeps the parent's length on the uncut axis and leaves both sides at least the minimum cell on
        /// both axes (rule 7), so it needs twice the minimum on one axis and the minimum on the other.
        /// </summary>
        public static bool CanSplit(MeshRect rect, float minCell)
        {
            return (rect.Width >= 2f * minCell - Eps && rect.Depth >= minCell - Eps) || (rect.Depth >= 2f * minCell - Eps && rect.Width >= minCell - Eps);
        }

        /// <summary>
        /// MostCells is the most live cells a base can become: its maxCellsPerBase, never more than its area holds in
        /// minimum cells (all of that with no limit); 1 on a Static map.
        /// </summary>
        public static int MostCells(MeshRect rect, MeshMapLayout layout, MeshDynamicSettings settings, float band)
        {
            if (layout == null || !layout.IsDynamic)
                return 1;
            float minCell = EffectiveMinCellSize(layout, settings, band);
            double fit = minCell > 0f ? Math.Floor((double)rect.Area / ((double)minCell * minCell) + 1e-6) : int.MaxValue;
            int byArea = (int)Math.Max(1d, Math.Min(fit, int.MaxValue / 4));
            int limit = EffectiveMaxCellsPerBase(layout, settings);
            return limit > 0 ? Math.Max(1, Math.Min(limit, byArea)) : byArea;
        }

        /// <summary>Check is every registry finding: the minimum under the band, the never-split bases, the band neighbours, the links, the settings.</summary>
        public static List<MeshSetupFinding> Check(MeshWorldRegistry world, bool linkAllZones)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            if (world == null)
                return findings;
            findings.AddRange(MinCellSizeUnderBand(world));
            findings.AddRange(BasesThatCannotSplit(world));
            findings.AddRange(BandNeighbourFindings(world));
            findings.AddRange(LinkFindings(world, linkAllZones));
            findings.AddRange(SettingsFindings(world));
            return findings;
        }

        /// <summary>
        /// BasesThatCannotSplit (decision 3): one finding per map whose bases no cut fits (CanSplit), and one per map whose
        /// maxCellsPerBase forbids or does not bound a cut.
        /// </summary>
        public static List<MeshSetupFinding> BasesThatCannotSplit(MeshWorldRegistry world)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            MeshLayoutWorld validation = world.ValidationWorld();
            float band = AssetBand(world.dynamicSettings);
            foreach (string mapId in DynamicMaps(world))
            {
                MeshMapLayout layout = world.GetLayout(mapId);
                int cells = EffectiveMaxCellsPerBase(layout, world.dynamicSettings);
                if (cells == 1)
                {
                    findings.Add(new MeshSetupFinding { severity = MeshSetupSeverity.Warning, mapId = mapId, text = $"Dynamic map {mapId}: maxCellsPerBase is 1, so no base cell can be cut" });
                    continue;
                }
                if (cells <= 0)
                    findings.Add(new MeshSetupFinding { severity = MeshSetupSeverity.Warning, mapId = mapId, text = $"Dynamic map {mapId}: maxCellsPerBase is {cells}: a command meets no limit and the central's policy splits nothing; set it to 2 or more" });
                float minCell = EffectiveMinCellSize(layout, world.dynamicSettings, band);
                MeshSetupFinding small = new MeshSetupFinding { severity = MeshSetupSeverity.Warning, mapId = mapId };
                foreach (MeshLayoutBaseCell cell in validation.baseCells)
                {
                    if (cell.mapId == mapId && !CanSplit(cell.rect, minCell))
                        small.cellIds.Add(cell.id);
                }
                if (small.cellIds.Count == 0)
                    continue;
                small.text = $"Dynamic map {mapId}: {small.cellIds.Count} base cell(s) can never split ({Ids(small.cellIds)}): no cut leaves both sides at least the {Number(minCell)} m minimum cell";
                findings.Add(small);
            }
            return findings;
        }

        /// <summary>
        /// MinCellSizeUnderBand (decision 5): a minimum cell under the band is not the one that runs (the central cuts
        /// nothing narrower than the band). An error, with a Fix that sets the map's own field to the band; a band not
        /// above 0 is an error of its own (the map servers fall back to 80 m, the central's policy uses it as it is).
        /// </summary>
        public static List<MeshSetupFinding> MinCellSizeUnderBand(MeshWorldRegistry world)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            List<string> maps = DynamicMaps(world);
            if (maps.Count == 0)
                return findings;
            bool bandSet = world.dynamicSettings != null && world.dynamicSettings.bandWidth > 0f;
            if (!bandSet)
                findings.Add(new MeshSetupFinding { severity = MeshSetupSeverity.Error, text = $"Registry: dynamicSettings.bandWidth is {Number(world.dynamicSettings != null ? world.dynamicSettings.bandWidth : 0f)}: the map servers fall back to {Number(FallbackBand)} m but the central's policy uses it as it is; set the band" });
            float band = AssetBand(world.dynamicSettings);
            foreach (string mapId in maps)
            {
                MeshMapLayout layout = world.GetLayout(mapId);
                float raw = RawMinCellSize(layout, world.dynamicSettings);
                if (raw >= band - Eps)
                    continue;
                string which = layout.minCellSize > 0f ? "minCellSize" : "minCellSize (the registry's defaultMinCellSize)";
                findings.Add(new MeshSetupFinding
                {
                    severity = MeshSetupSeverity.Error, mapId = mapId, fix = bandSet ? MeshSetupFix.RaiseMinCellSizeToBand : MeshSetupFix.None,
                    text = $"Dynamic map {mapId}: {which} {Number(raw)} m is under the {Number(band)} m band; the central cuts no cell narrower than the band, so it uses {Number(band)} m",
                });
            }
            return findings;
        }

        /// <summary>
        /// WorstCaseBandCounts (decision 4): for every base cell of every map, the most band neighbours any cell of its
        /// family can have when every base near it is cut to the most cells it can become (MostCells), under these
        /// settings. Every cell's interest lies inside its base, so a cell of family F streams at most to the rest of F
        /// and to every cell of each base near F: touching, or, with either on a Dynamic map, within the band.
        /// </summary>
        public static Dictionary<string, int> WorstCaseBandCounts(MeshWorldRegistry world)
        {
            return Counts(world, links: false);
        }

        /// <summary>
        /// WorstCaseLinkCounts: for every base cell, the most mesh links a cell of its family can hold with linkAllZones on:
        /// every other live cell of its map at the most each base can become, plus every cell of another map's bases near it.
        /// </summary>
        public static Dictionary<string, int> WorstCaseLinkCounts(MeshWorldRegistry world)
        {
            return Counts(world, links: true);
        }

        private static Dictionary<string, int> Counts(MeshWorldRegistry world, bool links)
        {
            MeshLayoutWorld validation = world.ValidationWorld();
            List<MeshLayoutBaseCell> bases = validation.baseCells;
            float band = AssetBand(world.dynamicSettings);
            int[] cells = new int[bases.Count];
            bool[] dynamic = new bool[bases.Count];
            Dictionary<string, int> perMap = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < bases.Count; ++i)
            {
                MeshMapLayout layout = world.GetLayout(bases[i].mapId);
                dynamic[i] = layout != null && layout.IsDynamic;
                cells[i] = MostCells(bases[i].rect, layout, world.dynamicSettings, band);
                string map = bases[i].mapId ?? string.Empty;
                perMap[map] = (perMap.TryGetValue(map, out int sum) ? sum : 0) + cells[i];
            }
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < bases.Count; ++i)
            {
                int n = links ? perMap[bases[i].mapId ?? string.Empty] - 1 : cells[i] - 1;
                for (int j = 0; j < bases.Count; ++j)
                {
                    if (i == j || (links && bases[i].mapId == bases[j].mapId))
                        continue;
                    float gap = bases[i].rect.GapTo(bases[j].rect);
                    if (gap <= validation.touchTolerance + GapSlack || ((dynamic[i] || dynamic[j]) && gap <= band + GapSlack))
                        n += cells[j];
                }
                counts[bases[i].id] = n;
            }
            return counts;
        }

        /// <summary>BandNeighbourFindings: one warning per base whose worst case passes the 32 band neighbours a cell can stream to.</summary>
        public static List<MeshSetupFinding> BandNeighbourFindings(MeshWorldRegistry world)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            if (!world.HasDynamicMap)
                return findings;
            Dictionary<string, int> counts = WorstCaseBandCounts(world);
            foreach (MeshLayoutBaseCell cell in world.ValidationWorld().baseCells)
            {
                if (!counts.TryGetValue(cell.id, out int count) || count <= MeshWorldRegistry.MaxNeighbours)
                    continue;
                MeshSetupFinding finding = new MeshSetupFinding
                {
                    severity = MeshSetupSeverity.Warning, mapId = cell.mapId,
                    text = $"{cell.id} (map {cell.mapId}): up to {count} band neighbours when every base near it is cut to maxCellsPerBase, over the {MeshWorldRegistry.MaxNeighbours} a cell can stream to; a layout change that would pass {MeshWorldRegistry.MaxNeighbours} (a split, or a merge whose survivor reaches more cells) is refused (lower maxCellsPerBase, or cut the map coarser)",
                };
                finding.cellIds.Add(cell.id);
                findings.Add(finding);
            }
            return findings;
        }

        /// <summary>
        /// LinkFindings: with linkAllZones on (any map manager prefab), one warning per map whose worst case gives a cell
        /// more links than its link server takes. Judged on a world with a Dynamic map only (a Static world's count is
        /// its asset cut, which the registry's own check sees).
        /// </summary>
        public static List<MeshSetupFinding> LinkFindings(MeshWorldRegistry world, bool linkAllZones)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            if (!linkAllZones || !world.HasDynamicMap)
                return findings;
            Dictionary<string, int> counts = WorstCaseLinkCounts(world);
            Dictionary<string, MeshSetupFinding> byMap = new Dictionary<string, MeshSetupFinding>(StringComparer.Ordinal);
            Dictionary<string, int> most = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MeshLayoutBaseCell cell in world.ValidationWorld().baseCells)
            {
                if (!counts.TryGetValue(cell.id, out int count) || count <= MaxLinks)
                    continue;
                if (!byMap.TryGetValue(cell.mapId, out MeshSetupFinding finding))
                    byMap[cell.mapId] = finding = new MeshSetupFinding { severity = MeshSetupSeverity.Warning, mapId = cell.mapId };
                finding.cellIds.Add(cell.id);
                most[cell.mapId] = Math.Max(count, most.TryGetValue(cell.mapId, out int m) ? m : 0);
            }
            foreach (KeyValuePair<string, MeshSetupFinding> pair in byMap)
            {
                pair.Value.text = $"Map {pair.Key}: with linkAllZones a cell links to every live cell of its map, and cut to maxCellsPerBase a cell of {Ids(pair.Value.cellIds)} could need up to {most[pair.Key]} links, over the {MaxLinks} its link server takes (lower maxCellsPerBase, or turn linkAllZones off on the map managers)";
                findings.Add(pair.Value);
            }
            return findings;
        }

        /// <summary>
        /// ServedCellsFindings (decision 6), over every spawner prefab at once: a child, every supervised restart (a base's
        /// only with sleeping cells), a kill and the policy's host headroom go to the spawner that advertised the base's
        /// id. With any list set (several hosts), a Dynamic base no list names, a host whose empty list advertises nothing,
        /// and entries that name no asset cell are warned.
        /// </summary>
        public static List<MeshSetupFinding> ServedCellsFindings(MeshWorldRegistry world, IReadOnlyList<MeshSetupSpawner> spawners)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            if (world == null || spawners == null || !world.HasDynamicMap)
                return findings;
            HashSet<string> served = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> listing = new List<string>(), empty = new List<string>();
            Dictionary<string, List<string>> entriesByOwner = new Dictionary<string, List<string>>();
            foreach (MeshSetupSpawner spawner in spawners)
            {
                if (spawner == null)
                    continue;
                List<string> entries = new List<string>();
                if (spawner.servedCells != null)
                {
                    foreach (string id in spawner.servedCells)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            entries.Add(id.Trim());
                    }
                }
                if (entries.Count == 0)
                {
                    empty.Add(spawner.owner);
                    continue;
                }
                listing.Add(spawner.owner);
                entriesByOwner[spawner.owner ?? string.Empty] = entries;
                foreach (string id in entries)
                    served.Add(id);
            }
            if (listing.Count == 0)
                return findings; // one host serving every cell: nothing to judge
            MeshLayoutWorld validation = world.ValidationWorld();
            HashSet<string> assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutBaseCell cell in validation.baseCells)
                assetIds.Add(cell.id);
            string owners = string.Join(", ", listing);
            const string Route = "a child, every supervised restart, a kill and the policy's host headroom go to the spawner that advertises the base";
            if (empty.Count > 0)
                findings.Add(new MeshSetupFinding { severity = MeshSetupSeverity.Warning, text = $"{string.Join(", ", empty)}: an empty servedCells serves every cell but advertises none, while {owners} list theirs; {Route}, so a Dynamic base only that host runs has them started on any spawner: give every host its list" });
            foreach (string mapId in DynamicMaps(world))
            {
                MeshSetupFinding missing = new MeshSetupFinding { severity = MeshSetupSeverity.Warning, mapId = mapId };
                foreach (MeshLayoutBaseCell cell in validation.baseCells)
                {
                    if (cell.mapId == mapId && !served.Contains(cell.id))
                        missing.cellIds.Add(cell.id);
                }
                if (missing.cellIds.Count == 0)
                    continue;
                missing.text = $"servedCells of {owners} leave out the Dynamic base cell(s) {Ids(missing.cellIds)} of map {mapId}: {Route}; a host that starts its cells by hand (MULTIHOST: machine B) advertises nothing, and the central then picks any registered spawner";
                findings.Add(missing);
            }
            foreach (KeyValuePair<string, List<string>> pair in entriesByOwner)
            {
                MeshSetupFinding unknown = new MeshSetupFinding { severity = MeshSetupSeverity.Warning };
                foreach (string id in pair.Value)
                {
                    if (!assetIds.Contains(id) && !unknown.cellIds.Contains(id))
                        unknown.cellIds.Add(id);
                }
                if (unknown.cellIds.Count == 0)
                    continue;
                unknown.text = $"{pair.Key}: servedCells names {Ids(unknown.cellIds)}, which {(unknown.cellIds.Count == 1 ? "is" : "are")} no cell of the registry (a child starts on its base's spawner: list base cells only)";
                findings.Add(unknown);
            }
            return findings;
        }

        /// <summary>SettingsFindings (decision 7): the dynamic settings' rules the central checks at start, when a map is Dynamic.</summary>
        public static List<MeshSetupFinding> SettingsFindings(MeshWorldRegistry world)
        {
            List<MeshSetupFinding> findings = new List<MeshSetupFinding>();
            if (!world.HasDynamicMap)
                return findings;
            foreach (string error in new[] { MeshLayoutValidator.SupervisionSettingsError(world.dynamicSettings), MeshLayoutValidator.PolicySettingsError(world.dynamicSettings) })
            {
                if (error != null)
                    findings.Add(new MeshSetupFinding { severity = MeshSetupSeverity.Error, text = "Registry: " + error });
            }
            return findings;
        }

        /// <summary>ApplyFix makes the finding's asset edit on the registry (the caller records an Undo and saves); true when it changed something.</summary>
        public static bool ApplyFix(MeshWorldRegistry world, MeshSetupFinding finding)
        {
            if (world == null || finding == null || finding.fix != MeshSetupFix.RaiseMinCellSizeToBand || world.dynamicSettings == null || !(world.dynamicSettings.bandWidth > 0f))
                return false;
            MeshMapLayout layout = world.GetLayout(finding.mapId);
            float band = world.dynamicSettings.bandWidth;
            if (layout == null || !layout.IsDynamic || RawMinCellSize(layout, world.dynamicSettings) >= band - Eps)
                return false;
            layout.minCellSize = band;
            return true;
        }

        /// <summary>The Dynamic maps in the registry's order, each once.</summary>
        public static List<string> DynamicMaps(MeshWorldRegistry world)
        {
            List<string> maps = new List<string>();
            foreach (MeshMapLayout layout in world.maps)
            {
                if (layout != null && layout.IsDynamic && !string.IsNullOrEmpty(layout.mapId) && !maps.Contains(layout.mapId) && world.GetLayout(layout.mapId) == layout)
                    maps.Add(layout.mapId);
            }
            return maps;
        }

        private static string Ids(List<string> ids)
        {
            if (ids.Count <= IdsShown)
                return string.Join(", ", ids);
            return string.Join(", ", ids.GetRange(0, IdsShown)) + $" and {ids.Count - IdsShown} more";
        }

        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
