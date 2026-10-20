# Dynamic cells, Slice 6: the operator path (implementation contract)

Revision 1, 2026-10-19 on machine A, after one independent critique (log at the end) of revision 0, itself from a read
of HEAD 975099b (DESIGN 0.82, package 0.82.0): `slices/slice3_design.md` and `slice5_design.md` (the pattern), DESIGN
0.72-0.82, SETUP.md, MULTIHOST.md, both Addons READMEs, `Mesh/Editor/ServerMeshSetupWindow.cs`,
`Mesh/MeshLayoutValidator.cs`, `Mesh/MeshWorldRegistry.cs`, `Mesh/MeshMapSpawnNetworkManager.cs` and
`MeshCentralNetworkManager_Split.cs` (`SpawnerForKey`). 2a-5 built dynamic cells: a Dynamic map's cells split and merge
by command and by the central's own policy, survive crashes, carry their buildings across a cut and survive a central
restart. Their operator documentation grew one fragment per slice, and nothing tells an operator before Play that a
Dynamic map's settings cannot work. 6 is the last slice of the plan: **one "Dynamic cells" section in SETUP.md an
operator can follow end to end; the Setup window's Validate panel judging every Dynamic map (base cells that can never
split, a minimum cell under the band, the worst-case band-neighbour and link counts, spawners' `servedCells` that leave a
base's children without their host, the settings the central checks at start) from pure, tested functions, with a Fix
where the fix changes nothing at runtime; MULTIHOST's port ranges widened and its two-host limit stated; the README and
package texts brought up to what the add-on does; and two cleanups the earlier slices left.** No runtime behaviour
changes.

Read first: this contract; `slices/slice5_design.md` and `slice3_design.md` (their decisions hold); the plan
`DYNAMIC_MESHING_PLAN.md` (the user's copy) "Slice 6" (:452-460) and "Known limits carried forward" (:503-508). Where
they disagree: THIS CONTRACT, then 5's, 4's, 3's contracts, then the plan.

User decisions: proceed with Slice 6 (2026-10-19, through the session that started this one). Choices the user has not
seen are marked (D).

## Rules
As 2b-5: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical** and, this
slice, Dynamic maps too (no runtime path changes: the new code runs in the editor's Validate panel only, and the
resolver's split moves code without editing it); log lines unchanged; files under ~500 lines (`ServerMeshSetupWindow.cs`
is 540 today: its prefab-editing helpers move to a partial so it ends under 500; `MeshRecoveryResolver.cs` 512 → split);
kit edits none (an implementer that finds one necessary stops and reports); no edits or compiles while a rig chain
measures; nothing committed (the user commits).

## Decisions

### The checks (pure `Mesh/MeshDynamicSetupChecks.cs`)
1. **Where.** A static class beside `MeshLayoutValidator` (runtime folder, pure: no UnityEditor), reading a
   `MeshWorldRegistry` outside Play (its asset cut; `ValidationWorld()` for the base cells' world rectangles,
   `GetLayout` for each map's raw fields). **The band judged is the asset's** (`dynamicSettings.bandWidth`, 80 m when it
   is not above 0): the map servers and the layout rules use `MESH_BAND` when a run sets it, but the central's policy
   plans and clamps with the asset's value (`MeshLayoutCoordinator_Policy.cs:36, 106`), so only the asset's band makes
   decision 5's Fix runtime-neutral. A `MESH_BAND` or `MESH_SPAWN_CELLS` set for a run is not seen (the window says so in
   SETUP). Findings are values: `MeshSetupFinding {severity (Warning|Error), mapId, cellIds, text, fix}`; the window turns
   each into one Validate line. Only worlds with a Dynamic map are judged.
2. **Effective values,** as the runtime computes them: the minimum cell `max(band, minCellSize > 0 ? minCellSize :
   defaultMinCellSize)`; cells per base `maxCellsPerBase > 0 ? maxCellsPerBase : defaultMaxCellsPerBase`, where **0 or
   less means no limit** for a command (rule 9 skips it) and no split at all for the policy (`familyCells >= max`).
3. **A base that can never split (Warning, no Fix).** A cut keeps the parent's length on the uncut axis and must leave
   both sides at least the minimum cell on both axes (rule 7), so a base splits only when `(W ≥ 2m and D ≥ m) or
   (D ≥ 2m and W ≥ m)` (eps 1e-3). One finding per map naming the bases that fail (the first ten, then `and <n> more`):
   `Dynamic map <m>: <n> base cell(s) can never split (<ids>): no cut leaves both sides at least the <m> m minimum cell`.
   A map whose cells per base is 1: `...: maxCellsPerBase is 1, so no base cell can be cut`; 0 or less: `...:
   maxCellsPerBase is <k>: a command meets no limit and the central's policy splits nothing; set it to 2 or more` (the
   geometry is still judged). (D) No Fix: lowering the map's minimum or raising its cells per base changes the operator's
   policy. The planner cuts only on the load report's bucket (`occupancyBucketSize`, a map-server field the registry
   cannot see), so a base just over 2m may split by command and never by the policy: a known limit, not a line.
4. **The worst-case band-neighbour count (Warning, no Fix).** At runtime rule 8 refuses any layout change that would give
   a cell more than 32 band neighbours where the count grew (a split, or a merge whose e1 widens the survivor's interest
   to the union), so a world that can reach it has operations refused. Every cell's interest lies inside its base by
   construction (a split's halves, a merge's union; the validator does not enforce it, and rule 4 lets owned overhang by
   1e-3, so gaps are compared with 2e-3 of slack). The most band neighbours any cell of family F can have is
   `(cells(F) - 1) + Σ cells(B)` over every other base B near F: touching (gap ≤ `touchTolerance`), or, when F or B is on
   a Dynamic map, a gap between the base rectangles ≤ the band. **`cells(X)` is the most live cells X can become**: on a
   Dynamic map `min(maxCellsPerBase, floor(area / m²))` (only the area bound with no limit), 1 on a Static map. The bound
   is over every map in the world frame (a Static cell beside a Dynamic family is judged too, as rule 8 judges it), and
   holds for a world started under these settings (a store cut under an older, larger `maxCellsPerBase` can exceed it).
   One finding per base over 32: `<base> (map <m>): up to <n> band neighbours when every base near it is cut to
   maxCellsPerBase, over the 32 a cell can stream to; a layout change that would pass 32 (a split, or a merge whose
   survivor reaches more cells) is refused (lower maxCellsPerBase, or cut the map coarser)`. The OK line gives the
   largest count and its base.
5. **The minimum cell under the band (Error, Fix).** A map whose `minCellSize > 0 ? minCellSize : defaultMinCellSize` is
   under the asset band: `Dynamic map <m>: minCellSize <v> m is under the <band> m band; the central cuts no cell
   narrower than the band, so it uses <band> m`. **Fix `Raise to band`** sets the map's own `minCellSize` to the asset
   band: every runtime use clamps to it already, so nothing that runs changes. A map that uses a registry default under
   the band gets the same line; the Fix sets the map's field (D: the registry-wide default stays the operator's). A band
   not above 0 is its own Error (`Registry: dynamicSettings.bandWidth is <v>: the map servers fall back to 80 m but the
   central's policy uses it as it is; set the band`) and the minimum-cell lines then carry no Fix (writing 80 would change
   the policy's input).
6. **`servedCells` and the children's host (Warning, no Fix), judged over every spawner prefab at once.** A child, every
   supervised restart (a base's only with sleeping cells: otherwise the kit's own auto-restart reruns it on the spawner
   that started it, `MeshLayoutCoordinator_Supervision.cs:382`), a kill (`KillChannel`) and the policy's host headroom
   (`TryGetHost`) go to the spawner that advertised the base's id (`SpawnerForKey`); with none, to any registered spawner
   (the cluster's peer order). A spawner with an empty list advertises nothing. The window gathers every
   `MeshMapSpawnNetworkManager` prefab's list: none set = one host, nothing to judge. Otherwise: (a) an empty list beside
   a listing one: `<owners>: an empty servedCells serves every cell but advertises none, while <owners> list theirs; ...
   give every host its list`; (b) Dynamic bases no list names: `servedCells of <owners> leave out the Dynamic base
   cell(s) <ids> of map <m>: a child, every supervised restart, a kill and the policy's host headroom go to the spawner
   that advertises the base; a host that starts its cells by hand (MULTIHOST: machine B) advertises nothing, and the
   central then picks any registered spawner`; (c) per prefab, entries that name no asset cell (`ValidationWorld`'s base
   cells): `<prefab>: servedCells names <ids>, which is no cell of the registry (a child starts on its base's spawner:
   list base cells only)`. No Fix: adding a base to a host's list would start it there as well as where it runs.
7. **The settings the central checks at start** (`MeshLayoutValidator.SupervisionSettingsError`,
   `PolicySettingsError`) become Validate lines too (Error, no Fix): today an operator sees them only in the central's
   log after start.
8. **The link count (Warning, no Fix; added by the critique).** With `linkAllZones` (default on) every cell links to
   every live cell of its map plus its band neighbours on other maps (`EnsureNeighbourLinks`), and a cell's link server
   takes 64 (`StartServer(port, 64)`). Per base: `Σ cells(B) over its map - 1 + Σ cells(B)` over other maps' bases near
   it. With any `MeshMapNetworkManager` prefab's `linkAllZones` on, one finding per map over 64: `Map <m>: with
   linkAllZones a cell links to every live cell of its map, and cut to maxCellsPerBase a cell of <ids> could need up to
   <n> links, over the 64 its link server takes (lower maxCellsPerBase, or turn linkAllZones off on the map managers)`.

### The window (`Mesh/Editor/ServerMeshSetupWindow*.cs`)
9. `ServerMeshSetupWindow` becomes `partial`. New `ServerMeshSetupWindow_Dynamic.cs`: `AddDynamicChecks()` (one hook
   after the registry line) gathers the spawner prefabs' lists and the map managers' `linkAllZones`, adds a line per
   finding and one OK line per Dynamic map without one (`Dynamic map <m>: every base can split, minimum cell <v> m, at
   most <n> band neighbours (<base>).`). A `Check` gains `warning`: the label is `OK`, `!!` (error) or `!` (warning). The
   Fix records an Undo on the registry, applies `MeshDynamicSetupChecks.ApplyFix`, marks it dirty and saves it, as the
   cell-mode panel saves its edits (an Undo then reverts the editor's copy; the file follows at the next save). New
   `ServerMeshSetupWindow_Fixes.cs`: `ShortPath`, `ScriptOf`, `SwapScript`, `AssignRegistry`, `AssignGameplayRule`,
   `SetBool`, `SetInt` moved unchanged. The Validate button stays disabled in Play.

### Documents
10. **SETUP.md**: a new `## 6a. Dynamic cells` an operator follows end to end — when to choose Dynamic and what it costs,
   the cut and the settings that matter (two short tables), the Validate lines, running it (the spawner and the hosts,
   ports, the production topology), the manual commands (`SubmitLayoutCommand`, `LayoutOpLine`; split, merge, kill),
   the automatic policy, supervision and fencing, buildings, a central restart, the log lines, the full settings
   reference, and one known-limits list (DESIGN 0.76-0.82 and the plan's carried-forward limits, deduplicated). The
   fragments it replaces go: §4.2's cell-mode and settings paragraphs shrink to the choice and pointers; §4.5's
   several-hosts bullet points at §6a.4; §7's seven Dynamic bullets become one rig bullet (toggles, the command file,
   the topology, the 14 scenarios); §8's Dynamic bullet becomes a pointer. §4.3's table gains the new checks; §6's ports
   name 8000-8063; §7's harness bullet counts 25 scenarios.
11. **MULTIHOST.md**: §4's tables and firewall rule 8000-8063 and 18000-18063, with why: a host's spawner takes ports
   from `spawnStartPort` and reuses one only after its process exited, so a host's range covers every process it runs at
   once (cells, children, other maps, an overlapping restart); 64 is also where the links stop (decision 8). §5 says to
   list base cells and that a family stays on its base's host. §9's existing "map-spawn server on the second machine"
   bullet gains the two-host limit of dynamic cells (no second bullet). `regress.py` `RIG_PORTS` widens to the same
   ranges (the foreign-port check), with a test.
12. **READMEs and the package descriptions** (`ServerMeshPackageBuilder`): the add-on's texts (`Addons/ServerMeshing`,
   and the packaged `Assets/ServerMeshing/README.md`, which still lists no dynamic cells) say what 2a-5 added and what
   Validate now judges; the test rig's say 25 scenarios (eleven of the mesh, fourteen dynamic) and the split-process
   topology; "nine regression scenarios" goes. **The kit requirement** (added 2026-10-19 at the parent session's request,
   checked against §17 and `upstream/README.md`): every text that said "the kit's core is not modified" and "four core
   changes in master since 2026-09-15" says instead that the package changes no kit file and builds on eight kit
   changes, seven merged (0001-0005 on `master`, 0006-0007 on `develop` at bb9895f), and CE-10 (patch 0008, not pushed)
   applied by hand, without which the add-on does not compile at all (`MeshMapNetworkManager` overrides its virtual
   unconditionally), Dynamic maps or not: both READMEs, the package description, SETUP §3 and §10.
13. **DESIGN.md**: the 0.83 row; the Status header and §15's P4 row say the plan is complete; §15 gains a short "left as
   known limits" list.

### Cleanups
14. `MeshRecoveryResolver.cs` (512): `MeshOperationVerdictKind`, `MeshOperationVerdict` and `ResolveOperation` with its
   helpers move unchanged to `MeshRecoveryResolver_Operation.cs` (`public static partial class MeshRecoveryResolver`).
15. The CS0414 in `TestMap/ServerMeshAutoClient_Buildings.cs`: `_storageClosedSeen` is written and never read; it goes.

### Acceptance
16. EditMode `MeshDynamicSetupChecksTests` (failure cases first): the never-split rule (both axes under twice the
   minimum; long but under the minimum across; one long axis enough; cells per base 1; 0 = no limit with the geometry
   still judged; ten ids then `and <n> more`; a Static map not judged); the minimum under the band (the map's own value,
   the registry default, the Fix and a re-check clean with the runtime value unchanged, a band not above 0 with no Fix,
   a default above the band clean); the worst-case band count (the rig's 3x3 Dynamic at 4 per base gives C 35 and nobody
   else over, SW 15, E 24 with the Static C2, C2 12; at 3 per base C 26 and clean; a limit above the area counts the
   area; no limit counts the area; a gap within the band counts, outside it not; a Static world is the touching count);
   the link count (a 72-cell map warned only with `linkAllZones`; the rig's C 35, E 36, C2 12, clean); `servedCells`
   (one host's left-out bases; two hosts splitting the map clean; an empty list beside a listing one; a child id; empty
   lists clean; a Static world not judged); the settings errors; `Check` on the default rig finds only C. Every 2a-5 test
   unchanged. Python: the `RIG_PORTS` test; the suite and ruff.
17. Rig: zero new compiler warnings (and the CS0414 gone); the stray-reference check; because Mesh/ code moves, the eleven
   base scenarios Static 11/11 (`dynidle` among them, Dynamic by its own environment) and `all:Dynamic` 11/11, and
   `split`, `merge`, `splitcrash_drain`, `splitbuild` and `splitcentral` 1/1 each (one rerun on a fail, recorded); DESIGN
   0.83, `ServerMeshPackageBuilder` 0.83.0 and `UpdateDate`, `regress.py --package`.

## Interfaces (exact)
- `MeshSetupSeverity { Warning, Error }`; `MeshSetupFix { None, RaiseMinCellSizeToBand }`; `MeshSetupFinding { severity,
  mapId, cellIds (List<string>), text, fix }`; `MeshSetupSpawner { owner, servedCells }`.
- `MeshDynamicSetupChecks.AssetBand(MeshDynamicSettings)`, `RawMinCellSize`, `EffectiveMinCellSize(MeshMapLayout,
  MeshDynamicSettings, float band)`, `EffectiveMaxCellsPerBase(MeshMapLayout, MeshDynamicSettings)`, `CanSplit(MeshRect,
  float)`, `MostCells(MeshRect, MeshMapLayout, MeshDynamicSettings, float band)`, `BasesThatCannotSplit(world)`,
  `MinCellSizeUnderBand(world)`, `WorstCaseBandCounts(world)` and `WorstCaseLinkCounts(world)` (base id → count, every
  base of every map), `BandNeighbourFindings(world)`, `LinkFindings(world, bool linkAllZones)`,
  `ServedCellsFindings(world, IReadOnlyList<MeshSetupSpawner>)`, `SettingsFindings(world)`, `Check(world, bool
  linkAllZones)` (every registry finding: the minimum, the never-split bases, band, links, settings), `ApplyFix(world,
  finding)` (true when it changed something), `DynamicMaps(world)`; constants `IdsShown` 10, `MaxLinks` 64,
  `FallbackBand` 80.
- Window: `Check.warning`; labels `OK` / `!!` / `!`; Fix label `Raise to band`.

## Files
Mesh/: new `MeshDynamicSetupChecks.cs`, `MeshRecoveryResolver_Operation.cs`; `MeshRecoveryResolver.cs` (partial, code
moved out). Mesh/Editor/: `ServerMeshSetupWindow.cs` (partial, `warning`, one hook line, helpers moved out), new
`ServerMeshSetupWindow_Dynamic.cs`, `ServerMeshSetupWindow_Fixes.cs`; `ServerMeshPackageBuilder.cs` (version, date,
descriptions). TestMap: `ServerMeshAutoClient_Buildings.cs` (the field). Tests: new `MeshDynamicSetupChecksTests.cs`.
Tools: `regress.py` (`RIG_PORTS`), `test_regress.py`. Docs: SETUP, MULTIHOST, DESIGN, the Addons READMEs, the packaged
README, this contract.

## Critique log
- **Rev 0 → 1 (2026-10-19), one review against the code:** a `maxCellsPerBase` of 0 or less means no limit for a command
  (rule 9 skips it) and no split for the policy, and a cells count of 0 broke the bound → decision 2's meaning, its own
  line, and `cells(X)` bounded by what the base's area holds in minimum cells (always, which is also tighter); the
  never-split rule missed a long base under the minimum across (rule 7 judges both axes of each side) → `CanSplit`; the
  policy's planner cuts on the report's bucket only → a known limit; the Fix was judged against `world.BandWidth`
  (`MESH_BAND`), while the policy clamps to the asset's raw `bandWidth`, so a Fix made with `MESH_BAND` set, or with a band
  of 0, would have changed the planner's input → the asset band, a band error, no Fix then; a base's restart goes through
  `SpawnerForKey` only with sleeping cells (the kit restarts it otherwise), and kills and host headroom route the same
  way → decision 6's wording; judging each spawner prefab alone flagged two hosts that split a map between them, and an
  empty list beside a listing one (it advertises nothing) went unseen → judged over every prefab's list, the mixed case
  its own line; "64 cells is the ceiling" mixed the per-host port range with the per-cell link cap → MULTIHOST's
  wording, and the link count as a new Validate line (decision 8); rule 8 also refuses merges that widen a survivor's
  interest → the text; the bound needs slack for rule 4's 1e-3 overhang and holds for a world started under these
  settings → 2e-3 and the wording; SETUP §4.5's several-hosts bullet and MULTIHOST §9's existing spawner bullet were
  fragments to fold, not to add beside; the packaged README listed no dynamic cells → in scope; `dynidle` runs Dynamic
  inside the "Static" set → said; the Fix's Undo after a save → kept and said. Confirmed: the rig's counts, the 25
  scenarios, the file sizes, the CS0414 field, both settings checks running at the coordinator's start only.
- **Acceptance (2026-10-20, BRENDOAIBOX):** 3041 EditMode tests (one skipped, as before; 40 new; 3000 in the Mesh group),
  828 harness tests, ruff clean, no compiler warning (Slice 4's CS0414 gone); one player build, then the eleven base
  scenarios Static 11/11 and `all:Dynamic` 11/11, `split` 118/118, `merge` 143/143, `splitcrash_drain` 103/103,
  `splitbuild` 141/141, `splitcentral` 160/160, each first time (`Logs/regress_slice6_chain.txt`); the lower check counts
  (six per scenario after midnight in `all:Dynamic`, two per family scenario against 0.82) are vacuous checks of child
  cells whose day log files did not exist on 2026-10-20, not lost coverage (DESIGN 0.83); package 0.83.0. No kit edit.
  **Accepted.**
