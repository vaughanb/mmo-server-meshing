# Dynamic cells, Slice 3: automatic policy and benefit (implementation contract)

Revision 1, 2026-10-18 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end) of revision 0, itself from three surveys (central, map side, harness) of HEAD 56321c6 (DESIGN 0.79).
2b split a cell by hand, 2c merged it back, 2d made the cells survive crashes. 3 makes the central decide for itself:
**a cell that stays hot splits along the cut the fitted cost model says relieves it most, only when that relief is
worth a process; two cells of a family that would be cool together merge back; a cut that would not help is refused
and said so; every automatic operation is measured afterwards and a family that flaps is slowed down; a split is
deferred while its host has no headroom.** It also closes 2c's and 2d's deferrals: **a merge of two children**, and
**a child that cannot collapse into its base collapses into a sibling**. Buildings (4), central-restart recovery (5)
and the operator path (6) stay later slices.

Read first: this contract; `slices/slice2b_design.md`, `slice2c_design.md`, `slice2d_design.md` (their decisions hold
unless changed here); the plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D1 merge geometry (:115-117), D2 load
measurement (:120-140), D3 concurrency, planner, triggers, host headroom (:141-198), Slice 3 (:427-436); DESIGN 0.71
(the fitted model, machine B, steered puppets) and 0.73 (the kinematic refit). Where they disagree: THIS CONTRACT, then
2d's, 2c's, 2b's, then the plan.

User decisions: proceed with Slice 3 (2026-10-18). Choices the user has not seen are marked (D).

## Rules
As 2b-2d: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical** but
for one start-up step (everything below is gated on a Dynamic map with `autoSplit` or `autoMerge` set, an op in
progress, or a rig toggle; the exception: every map-spawn server now sends the central a host report, and the central
logs one line per spawner, on Static-only worlds too, as 0.79 excepted the link hold); log lines one physical line,
invariant, no braces, never "Exception"; files under ~500 lines: `MeshMapNetworkManager.cs` (1,147) and `_Ghosting.cs`
(3,717) get one-line hooks only, `MeshLayoutCoordinator.cs` (450), `_Split.cs` (482), `_Supervision.cs` (438),
`_Fence.cs` (469), `_StaticHandoff.cs` (517) take hooks and small rule changes only (the policy is its own partial and
its own pure classes); kit edits only as numbered CE patches (none planned; **an implementer that finds one necessary
stops and reports**); no agents, edits or compiles while a rig chain measures; commits only when the user asks.

## Settings (new `MeshDynamicSettings` fields; the plan's names where it has them)
Triggers: `splitUtil` 0.8, `splitDwellSeconds` 45, `fastPathSaturatedShare` 0.2, `fastPathUtil` 0.95,
`fastPathSeconds` 10, `mergeUtil` 0.5 (the merge threshold is `min(mergeUtil, 0.6 x splitUtil)`, so a `mergeUtil`
over `splitUtil` is bounded, not an error), `mergeDwellSeconds` 300, `opCooldownSeconds` 120 (the policy's cooldown
per base family after any op ends, never under 2b's manual guard `splitCooldownSeconds`, 30, which `CheckOpCommon`
keeps), `splitBenefitRatio` 0.75, `reportFreshSeconds` 15 (three load windows: an older report is no evidence),
`measureAfterSeconds` 10 (two windows after an op's done before its relief is read). Flaps: `flapWindowSeconds` 900,
`flapReliefShare` 0.10, `flapMaxMultiplier` 8. Hosts: `hostHeadroomShare` 0.8. Cost model (player-equivalents; an
owned player is 1): `modelFloorPlayers` 9.1 (0.73: c0 23.8 ms/s over 2.62 ms/s per steered owner at 60 fps),
`puppetPlayerWeight` 0.174 (0.73 kinematic k_band; Dynamic maps run kinematic puppets since 2b), `ownedMonsterWeight`
0.126 (0.33 / 2.62), `puppetMonsterWeight` 0.1 (D: a kinematic monster puppet; 0.74 puts it at ~0.56 of a steered
one, which 0.71 measured at 0.2 of an owned player). All figures are machine B's; the model predicts ratios, so the
scale cancels (decision 2). `MESH_PLANNER_WEIGHTS` re-fits them for a run. Validation at the coordinator's Start (an
error line, the policy still runs): `0 < splitUtil <= 1`, `0 < splitBenefitRatio <= 1`, `fastPathUtil >= splitUtil`,
every seconds field `>= 0`, every weight `>= 0`, `modelFloorPlayers > 0`, `flapMaxMultiplier >= 1`,
`hostHeadroomShare > 0`.

## Decisions

### The planner (pure `MeshSplitPlanner`; `MeshSplitPlan`)
1. **Input** is the parent's latest `MeshLoadReportMessage` (its grid, counts and work window), its owned rectangle,
   the map's minimum cell `max(minCellSize, band)`, the band, the weights and the ratio. **Candidates:** on each
   axis, every line `t` at a multiple of the grid's bucket (B/4, 20 m on the rig) with `t - min >= minCellSize` and
   `max - t >= minCellSize`. A grid whose bucket is not the quarter band (a huge map, `MeshOccupancyGrid.MaxSide`)
   uses its own bucket. No candidate: refused `no cut leaves both sides at least <minCellSize> m`.
2. **The model.** For a rectangle S cut from the parent P at `t`, in player-equivalents:
   `L_S = floor + P_own(S) + w_m M_own(S) + k_ext Pup(S ± B) + k_p P_own(strip) + k_m M_own(strip)` where `P_own`,
   `M_own` are the grid's OwnedPlayers and OwnedMonsters totals over S, `Pup(S ± B)` the Puppets layer's total over S
   grown by the band on every side (puppets lie outside P's owned rectangle, so this is the ring within B of S), the
   `strip` is the other side's owned entities within B of the cut (they become S's puppets), and `k_ext` blends
   `puppetPlayerWeight` and `puppetMonsterWeight` by the report's `puppetPlayers : puppetMonsters` (the grid's Puppets
   layer does not tell them apart; `puppetPlayerWeight` when both are 0). `L_now` is the same sum over P itself (no
   strip; its puppet term is the report's counts, weighted). **Predicted util** of S is `util_now x L_S / L_now` (`f`
   is linear: 0.73 found kinematic load additive); `util_now` is the report's `Utilisation`. The plan's `s x clients
   x visible` term is dropped (D): 0.71's fit had no client term and the puppet terms already carry the observers. A
   report whose probe is not `Valid` plans nothing. **A consequence (critique B1):** with kinematic weights the best
   cut of N clustered players costs `9.1 + N/2 x (1 + k)` per side, 0.605 of the whole at k = 0.174, so the model
   *accepts* a crowd on the cut (0.73's G3-F data says a kinematic crowd is relieved); it refuses one only with
   steered weights (k ≈ 1: 0.71's "none" case). Whether a kinematic crowd really gets 40 % is an open measurement.
3. **Choice and gate.** Each candidate's **peak** is the larger side's predicted util. The plan picks the lowest peak;
   ties go to the cut nearer the parent's centre, then x before z. **The child takes the side with fewer owned players**
   (plan D1: fewest migrations; ties: the positive side). Accepted only when `peak <= splitBenefitRatio x util_now`;
   otherwise refused with the best peak: `best peak <p> over <ratio> x util <u>`. The plan carries axis, `at`, the
   child's side, the predicted parent and child utils, the peak and how many cuts were tried.
4. **Merge prediction** (`MeshSplitPlanner.TryPredictUnion`): for two cells A and B whose owned rectangles make a
   rectangle U, `L_U = floor + own(A) + own(B) + k_ext (Pup_A(A ± B) - Pup_A(U) + Pup_B(B ± B) - Pup_B(U))` (each cell's
   puppets outside the union: the ones inside it are the sibling's entities, already owned; a third cell's entity near
   both counts twice, which overestimates and so only delays a merge). The scale is the larger of the two cells'
   `util_i / L_i` (D: conservative). The predicted merged util is `scale x L_U`. Both reports must be fresh and Valid.

### The policy (pure `MeshLayoutPolicy`; partial `MeshLayoutCoordinator_Policy.cs` does the sending and the lines)
5. **Tick and inputs.** `PolicyTick` runs once a second after `OpTick`, only while some Dynamic map sets `autoSplit`
   or `autoMerge`, the coordinator is Running and its store is writable. The coordinator keeps each binding's latest
   report (`MeshCellBinding.report`, nulled on a new incarnation; it arrived at `lastReportAt`), so the policy reads
   the bindings, not the central's `MeshLoadBoard`. A cell takes part when it is Active, owns something, is bound and
   answered, registered, its map's outcome is AdoptStatic, its report is younger than `reportFreshSeconds` and its
   probe is Valid (an Invalid, Unknown or Uncapped probe is excluded, as 0.71's error line promises). Each cell also
   carries its family's live cell count, the map's `maxCellsPerBase`, and whether any cell of the family (participating
   or not) has interest beyond owned. Every line the policy writes starts `Policy: ` and goes through `Line` (mirrored
   by the runner, decision 16); the coordinator's own start line is `Coordinator policy: automatic split on <maps>,
   automatic merge on <maps>; ...` (no `Policy: ` prefix, so a scenario can enumerate the policy's lines).
6. **Split trigger.** Per cell: **hot** is `util >= splitUtil`, **saturated** is `saturatedShare > fastPathSaturatedShare
   or util >= fastPathUtil`. `hotSince` is set at the first tick that sees a fresh hot report and cleared by one that
   is not (or by a stale report); `satSince` likewise. A cell is a **candidate** when `now - hotSince >=
   splitDwellSeconds x m` or `now - satSince >= fastPathSeconds x m` (`m` is the family's flap multiplier, decision
   10). Both clocks are **cleared** (not paused) while the family is in the policy's cooldown or an op runs or froze
   world-wide: dwell never accumulates through either (D; the plan's rule is per family, the slot is world-wide).
   p95 is in every cell's `Work window` line and is never read by the policy. A candidate is **planned at most once
   per report** (the report's arrival time is remembered) and only with `autoSplit` on its map; every due candidate
   is planned (and refused or noted) in the tick, hottest first, but only the first accepted one is requested (one
   op world-wide, decision 13).
7. **Acting on a plan.** Before the planner runs, a family at its map's `maxCellsPerBase` is refused on the policy's
   side: `Policy: split of <cell> refused: family <base> has <n> cell(s), the map's most` (the manual path would refuse
   it every time otherwise). Accepted: `Policy: split of <cell> planned: <x|z> <at> <+|-> child <id>, util <u> -> peak
   <p> (<cell> <pP>, <id> <pX>), <n> cut(s) tried`, then `RequestSplit` through the manual path (2b's lines follow;
   its refusals too). The child id is `<base>.<n>` with the smallest `n >= 1` whose id is not a live cell, not
   registered and not refused by 2d's id-reuse rule (2c decision 7); none under `maxCellsPerBase x 4` tried: refused
   `no free child id for <base>`. The record for the measurement (decision 10) is made right after the request; a
   request whose op ends inside it (a store or spawn failure aborts synchronously) is reported `not measured` at
   once, and the decisions of a tick are acted on from a snapshot, so the op-ended hook never re-enters a list being
   iterated. A `Layout op refused:` from the request puts the cell off for `NoteRepeatSeconds` (60 s). Refused by
   the gate or the cap: `Policy: split of <cell> refused: <why>` at most once a minute per cell, and a
   CoordinatorNote `split-refused` (Warning) to the cell: the plan's line. The planner runs again on the next report,
   so a crowd that spreads out is caught within a window.
8. **Host headroom.** Every map-spawn server sends the central a new cluster message 30021 `HostReport` {hostName
   (`Environment.MachineName`), cores (`Environment.ProcessorCount`; rig override `MESH_HOST_CORES`), coresInUse} at
   every registration and every 10 s while registered. **coresInUse is host-wide**: the sum over every process the
   spawner can read of `TotalProcessorTime`, differenced over the interval (pure `MeshHostCpuSampler`; 0 on the first
   report; a process it may not read, or one that exited, is skipped), so the editor, the bots and anything else on
   the host count, not just the cells. The central keeps the latest per spawner connection and the coordinator reads a
   base family's host through `IMeshSplitHost.TryGetHost(baseId, out MeshHostInfo)` (the spawner that advertised the
   base, else the first registered one, as `SpawnerForKey` resolves it: on a two-host rig a base the editor's spawner
   never advertised may resolve to the other host, 2b's known `which did not advertise` case). **Projected** is
   coresInUse plus the child's share, `cpu_parent / 100 x pX / util_now` (the parent's `cpuPercent` is 0 in its first
   report: the share is then 0). A split is **deferred** when `projected > hostHeadroomShare x cores`: `Policy: split
   of <cell> deferred: host <h> at <n>/<m> cores` once a minute per cell; its dwell is kept and the report is planned
   again, so it goes the moment the host has room. With no host report (an older spawner) nothing is deferred and
   `Policy: no host report for <base>'s spawner; headroom not checked` is logged once per base. The plan's "util rises
   on every cell of a host together" is folded into this rule (D): contention shows as the host over its share.
9. **Merge trigger** (`autoMerge`). Every pair of participating cells of one family whose owned rectangles share a
   full edge (`TryUnion`), with no cell of the family at interest ≠ owned (2c's pending-settle refusal, read from the
   whole family), gets a predicted merged util (decision 4). The pair's `coolSince` runs while that is `<=
   min(mergeUtil, 0.6 x splitUtil)`, is cleared when it is not or a report goes stale, and does not run through a
   cooldown or an op. At `mergeDwellSeconds x m`: `Policy: merge of <a> <b> planned: predicted <p> (<a> <ua>, <b>
   <ub>)`, then `RequestMerge(a, b)` (decision 11 picks the survivor). Pairs are taken lowest prediction first, one
   request per tick, and only when no split was requested in it.
10. **Measurement and flaps.** A policy-planned op is **measured**: a split at its `done` when it drained, else at its
    settle; a merge at its `done`; from `measureAfterSeconds` later, the first tick at which every measured cell's
    fresh report arrived after that moment reads it. Split: `measured = max(util_parent, util_child)`, `relief = 1 -
    measured / util_before`: `Policy: split <op> measured: predicted peak <p>, measured <m> (<parent> <up>, <child>
    <uc>), relief <r> %`. Merge: `Policy: merge <op> measured: predicted <p>, measured <m> (<survivor>)`. An op that
    aborts, freezes or ends otherwise: `Policy: <kind> <op> not measured (<end reason>)`; one whose cells send no
    fresh Valid report for `MeasureTimeoutSeconds` (60) past the moment: `... not measured (no fresh report from
    <cell> in <n> s)`; one still waiting when another op starts on its family: `... not measured (superseded by
    <op>)`. A manual op is not measured (it was not predicted) but does start the family's cooldown (decision 12).
    **Flap:** a split whose relief is under `flapReliefShare`, or a split planned within `flapWindowSeconds` of the
    family's last merge done, doubles the family's multiplier `m` (dwells and the cooldown), up to
    `flapMaxMultiplier`: `Policy: flap on <base>: <why>; dwell and cooldown x<m>`. A split measured at `relief >= 2 x
    flapReliefShare` while `m > 1` resets it: `Policy: <base> steady again: split <op> relief <r> %; dwell and
    cooldown x1` (D). The multipliers and the records live in memory only (a central restart forgets them; Slice 5
    territory).
11. **Merge of two children** (closes 2c decision 1). `CheckMerge` accepts two `isDynamic` cells of one family sharing a
    full edge (two base cells are refused as `both base cells`). The survivor is the base when it is one of the two;
    else **the child with more owned players** by the latest reports (none: 0; ties: the lower id, ordinal ignore
    case). Line `Merge <op>: requested <survivor> <retiring>` as today. Why it is safe: a borrower tells the
    monster's home its new owner on every hand-on when it is linked to the home (`LentMonsterOwner`,
    `_Ghosting.cs:2580`, sent before the handoff on the ordered link), **and from this slice the receiver tells the
    home too when it is linked** (one-line hook in `OnMonsterHandoff`; `SetOwner` is idempotent), so a grandchild
    that does not band with the base (critique S4) no longer hands on silently; the drain gate holds the retiring
    child until nothing is left, and e3's prune releases only records still naming the retired zone (`_Prune.cs`).
    Residual limit: a hand-on where neither party is linked to the home. Children run no spawn areas, so the union's
    monsters stay the base's. The tidy still applies only when the family is one cell again. The retire path, the
    Draining rules and the neighbours' prune are unchanged.
12. **Cooldown and manual ops.** The policy's cooldown `max(opCooldownSeconds x m, splitCooldownSeconds)` starts for a
    family when any op on it ends (done, settled, aborted, frozen, ended), manual or automatic. 2b's 30 s guard stays
    in `CheckOpCommon`. The manual path neither consults nor is blocked by the policy (an operator may split a cell
    the policy would refuse); its one change is decision 11 (a merge of two children is now accepted).
13. **Concurrency (D, deviates from plan D3).** One operation world-wide stays. The drain rate is the database's
    cluster-wide capacity (0.75: 5 handoffs/s sustained, `drainRatePerSecond` is the cluster's); a second concurrent
    drain would halve each; the plan's "at most one per spawner host" is met trivially; and a one-host rig cannot
    verify more. A second candidate waits for the slot (its dwell cleared, decision 6) and its own cooldown.
14. **Collapse into a sibling** (closes 2d decision 3). At queue time the test is geometry only: the base, or any live
    Active sibling, whose owned rectangle shares a full edge with the down child; none: `Supervisor: <cell> cannot
    collapse (no live cell of its family shares a full edge); left down` (the `(Slice 3)` text goes; the error note's
    text likewise), still no more restarts. When the collapse may start, the survivor is the base when it shares the
    edge, else the **participating** sibling (Active, bound, answered, registered) that does, **the least loaded by
    its own fresh Valid report** first (the down cell's load is gone and its rectangle refills by position), then by
    fewest owned players for those without one; none participating yet: `collapse waits: no participating cell of
    <base>'s family shares a full edge with <cell>` (the existing wait line), tried every tick. The merge is the same
    relaxed merge (`collapse=true`) with the sibling as survivor: `Merge <op>: requested <sibling> <cell> collapse`.

### Rig toggles (`MeshPolicyEnv`, applied to `MeshLayoutOverride.Resolve`'s runtime copy)
15. `MESH_AUTO_OPS=split|merge|split,merge` sets `autoSplit` / `autoMerge` on every Dynamic map of the copy;
    `MESH_SPLIT_UTIL`, `MESH_SPLIT_DWELL`, `MESH_MERGE_UTIL`, `MESH_MERGE_AFTER` (`mergeDwellSeconds`),
    `MESH_OP_COOLDOWN`, `MESH_SPLIT_BENEFIT` (`splitBenefitRatio`; 1 accepts any cut that does not raise the peak),
    `MESH_MONSTER_WEIGHT` (both monster weights), `MESH_PLANNER_WEIGHTS=<puppetPlayer>,<ownedMonster>,<puppetMonster>,<floor>`
    (the full re-fit). **Any policy key makes the runtime copy** (as a layout or mode key does); the asset is never
    written, which matters because the central is the editor process on the rig. Every process that resolves the
    registry logs `[Mesh] Policy settings from the environment (<k=v ...>)` (no `Policy: ` prefix: cell logs must stay
    free of policy lines); a value that does not parse is a warning and ignored. `MESH_HOST_CORES` on the spawner.
    **The burn becomes switchable:** `MESH_PROBE_BURN_MS=@<path>` names a file the cell re-reads at every load window
    (same `[cell:]ms` syntax inside; an absent or empty file burns nothing); `Burn <ms> ms on <cell|every cell>` /
    `Burn off` logged on change. The path is resolved by the **cell process**, so the harness passes an absolute path
    (`Temp/ServerMesh.ProbeBurn.txt` under the project) and writes it as `write_command` does (a temp file and
    `os.replace`), never truncating in place. The nine policy keys and `MESH_HOST_CORES` join `regress.py`'s `ENV_KEYS`
    (`MESH_PROBE_BURN_MS` is there already).
16. **The runner mirrors automatic ops.** `ServerMeshLayoutCommandFile` mirrors lines starting `Policy: ` too; while no
    command's seq is live and Play runs, the mirrored lines go to the status file under the pseudo-seq `auto` (today
    they are dropped: `MirrorLayoutLine` returns when `s_layoutSeq == null`). A command's seq takes over when it is
    submitted, as today. **The "no policy" guarantee of every other scenario reads the status file**, not the cell
    logs (the central writes no log the harness reads; a cell sees only `Coordinator: split-refused: ...` notes):
    `regress.grade_layout` fails on any status line matching `^\S+ Policy: `; the dynamic scenarios' own grades
    enumerate the lines they allow.

### Scenarios (`Tools/ServerMeshing/regress_auto.py`, `rigauto.py`)
17. **`autosplit`** (test map Dynamic, `MESH_AUTO_OPS=split,merge`, `MESH_SPLIT_UTIL=0.6`, `MESH_SPLIT_DWELL=20`,
    `MESH_MERGE_UTIL=0.5`, `MESH_MERGE_AFTER=30`, `MESH_OP_COOLDOWN=30`, `MESH_SPLIT_BENEFIT=1` (D: the burn is work
    the model cannot see; the gate is `autobenefit`'s to prove), `MESH_MONSTER_WEIGHT=0`, `MESH_NO_MONSTERS=1` (the
    +x stand points lie inside `Spawn_Boundary_X100_Z60`'s radius; nothing here needs monsters), `MESH_PROBE_BURN_MS=@<abs
    path>`, no synthetics, no harvest bot, no tester). Eight bots meshbot2..9 stand at x = +88 and x = -88 (four each,
    `ACROSS` z = 75, -75, 90, -90): both groups are more than the band from the cut at x = 0, so neither side puppets
    the other, and two bots per side sit inside the band of z = 0, so the z cut's strip is heavier and `x 0 +` wins
    outright (ties on players: `+`). **A three-stage burn** (critique B2: with the burn simply removed the pair is
    cool at once and the merge fires while the bots still stand, giving `migrations=2`): all eight `Standing at`
    (nothing is hot before: idle util ≈ 0.06); write `C:14` (util ≈ 0.9); wait for `auto Policy: split of C planned`
    and `auto Split split-1: done` (240 s); write `C:7` (util ≈ 0.46: not hot, measured relief ≈ 49 %, predicted
    union ≈ 0.6 over the 0.36 threshold, so no merge); wait for `auto Policy: split split-1 measured` (60 s); wait for
    every bot's RESULT; check no `Merge` line yet; kill the eight bots (`rig.kill_bots()`: the tester lives in the
    editor); empty the file; wait for `auto Policy: merge of C C.1 planned` and `auto Merge merge-1: done` (240 s).
    Grades: the planned line names `x 0 + child C.1` with util `>= 0.6`; the measured line's relief `>= 30 %`; R-bots
    (x > 0) `migrations=1`, P-bots 0, `allReady`, `maxGap < 1000`; one `Handoff START` per R-bot; no `Merge` line
    before the kill; the merge requested `C C.1`, done; C.1's process gone and staying gone; the store holds no entry
    for the family; applies by rectangle as `split` then `merge` grade them; 0 exceptions, 0 player teleports; no
    `Supervisor:` line; no `auto Layout op refused:` line; the `Policy:` lines allowed by prefix: `split of C planned`,
    `split split-1 measured`, `merge of C C.1 planned`, `merge merge-1 measured` (a `refused`, `deferred`, `flap`,
    `steady again` or `not measured` line fails). It joins `TWO_HOST_SKIP` (the burn file is on this disk) and
    `PERSIST_SKIP`.
18. **`autobenefit`** (the Coarse cut `MESH_LAYOUT_OVERRIDE=ServerMeshTestMap=C@-300,-300,600,600`, Dynamic,
    `MESH_AUTO_OPS=split`, `MESH_SPLIT_UTIL=0.3` (the Coarse cell at 200 steered synthetics ran at util 0.43 on this
    machine at 60 fps on 2026-10-18, not the 0.70 of an earlier run; the 600 s cooldown keeps a second split out),
    `MESH_SPLIT_DWELL=20`, `MESH_OP_COOLDOWN=600`, `MESH_NO_MONSTERS=1`, `MESH_LOAD_PLAN=C:200` spread **over C's inner
    200 m square** (`MESH_LOAD_AREA=0,0,100`, `MESH_LOAD_SEED` fixed; see the live-run log below for why not the whole
    ground), no client (`request_play("none", ..., enabled=False)`), overall wait 420 s). Wait for `auto Policy: split of C planned` (predicted peak p, util before u;
    the axis and side are **parsed from the line**, not assumed), `auto Split split-1: done`, `auto Policy: split
    split-1 measured: predicted peak p, measured m`. Grades: `|m - p| <= 0.15 x p` (the plan's ±15 %; 0.73's
    halves/coarse 0.57-0.60 against the model's 0.53-0.57 is inside); `m <= 0.75 x u` (a real relief); exactly one
    `Split` op in the status file; the synthetics are all accounted for: C's and C.1's `Work window` `owned players`,
    paired by stamp, sum to 200 for three consecutive pairs after e3, and `rigsplit.adoptions` on the child `>= 80`;
    an independent read of `m`: for each measured cell the first `Work window` line stamped at least 10 s after the
    harness saw `done`, `work mean / budget` within 0.05 of the measured line's figure (a note when not: the status
    file carries no stamps, the poll has 2 s of slack); 0 exceptions; no `flap`. Expected zones for `grade_common`
    are C, C.1 and C2 (the override leaves the map one cell; C2 touches it and logs `neighbours applied`, which the
    apply grader allows); `expected_zones` accepts ids outside `ZONES` with the map given. The layout grade for the
    override's cells lives in `regress_auto`, not `regress.py`.
19. **`autorefuse`**: `autobenefit`'s run with `MESH_LOAD_AREA=0,0,30` (a 60 m crowd on the centre), **with steered
    puppets and the steered fit** (`MESH_PUPPET_MOTION=steered`, `MESH_PLANNER_WEIGHTS=1,0.126,0.2,9.1`: 0.71's k_p ≈
    1.0; decision 2 says why the kinematic model cannot refuse a crowd). Grades: `auto Policy: split of C refused:
    best peak <p> over 0.75 x util <u>` with `p > 0.75 x u`, no `Split ` line within 90 s after the first refusal,
    refusal lines at least 55 s apart (once a minute), 0 exceptions. Both Coarse scenarios register in `ALL`,
    `TWO_HOST_SKIP` (spawner) and `PERSIST_SKIP`.
20. **Graders of the other scenarios.** `split`, `merge`, `splitcrash_*` and the eleven base scenarios run with the
    policy off (`autoSplit`/`autoMerge` are off in the asset and no `MESH_AUTO_OPS` is set), and fail on any `auto
    Policy: ` status line (decision 16). `rigsplitcrash.SUP_CANNOT` reads the new cannot-collapse text. All three new
    scenarios fail on any `auto Layout op refused:` line.

### Acceptance
21. EditMode tests (the planner: candidate lines at bucket multiples and under `minCellSize`, the blended puppet weight,
    the strip, the side choice, ties, the gate, no candidate, invalid probe; the union prediction and its scale;
    the policy: dwell and its reset, the fast path, no accumulation through a cooldown or an op, once per report,
    hottest first, the refusal rate and note, the family cap, headroom deferral and its absence without a report,
    the merge pair's dwell and the settle refusal, measurement timing and lines, the timeout and the superseding, flap
    doubling and cap, the reset, the window flap, manual ops starting the cooldown, not-measured lines; the
    two-children survivor rule; the collapse into a sibling and the new left-down text; the env parsing; the host
    report and sampler; the burn parser; the runner's `Policy: ` prefix; the settings validation; all 2b-2d tests
    unchanged but for the texts named here); Python tests (`rigauto` parsing, `regress_auto` flows with a FakeRig,
    the registration test, `expected_zones` outside `ZONES`); ruff; zero new warnings; the stray check; the eleven
    scenarios Static and `all:Dynamic`; `split`, `merge`, `splitc2`, `mergec2`, `splitcrash_prewarm`,
    `splitcrash_drain`, `splitcrash_base` 1/1 each (unchanged code paths; 3/3 on any that fails once); `autosplit`,
    `autobenefit`, `autorefuse` 3/3 (flake policy: one rerun, recorded; `autobenefit`'s ±15 % is tight against
    machine-B figures, so its first run is recorded whatever it says); DESIGN 0.80 (the benefit figures: predicted
    against measured, this CPU); package 0.80.0; SETUP (settings, toggles, the `auto` seq, the scenarios, the
    two-children merge, the sibling collapse).

## Interfaces and log lines (exact)
- **Central:** `Policy: split of <cell> planned: <x|z> <at> <+|-> child <id>, util <u> -> peak <p> (<cell> <pP>, <id> <pX>),
  <n> cut(s) tried`; `Policy: split of <cell> refused: best peak <p> over <ratio> x util <u>`; `Policy: split of <cell>
  refused: <reason>` (no candidate, the family cap, no free id); `Policy: split of <cell> deferred: host <h> at
  <n>/<m> cores`; `Policy: no host report for <base>'s spawner; headroom not checked`; `Policy: merge of <a> <b>
  planned: predicted <p> (<a> <ua>, <b> <ub>)`; `Policy: split <op> measured: predicted peak <p>, measured <m>
  (<parent> <up>, <child> <uc>), relief <r> %`; `Policy: merge <op> measured: predicted <p>, measured <m>
  (<survivor>)`; `Policy: <split|merge> <op> not measured (<why>)`; `Policy: flap on <base>: <why>; dwell and
  cooldown x<m>`; `Policy: <base> steady again: split <op> relief <r> %; dwell and cooldown x1`; `Coordinator policy:
  automatic split on <maps>, automatic merge on <maps>; ...`; `Supervisor: <cell> cannot collapse (no live cell of its
  family shares a full edge); left down`. Utils and ratios print as `0.##`; `at` as `0.###`; relief as a whole percent.
- **Map side:** `Burn <ms> ms on <cell|every cell>` / `Burn off` (file mode); every process: `Policy settings from the
  environment (<k=v ...>)`.
- **Spawner:** `Host report: <host> <cores> cores` once; the central: `Map-spawn server on connection <c> runs on host
  <h> with <n> core(s)` once per spawner.
- **Wire:** cluster 30021 `HostReport` {hostName, cores, coresInUse}. `IMeshSplitHost.TryGetHost(string baseId, out
  MeshHostInfo host)`; `MeshHostInfo` {spawnerConnection, hostName, cores, coresInUse}.
- **Runner:** status lines `auto <line>` while no command is live; `Policy: ` mirrored.
- **Env:** `MESH_AUTO_OPS`, `MESH_SPLIT_UTIL`, `MESH_SPLIT_DWELL`, `MESH_MERGE_UTIL`, `MESH_MERGE_AFTER`, `MESH_OP_COOLDOWN`,
  `MESH_SPLIT_BENEFIT`, `MESH_MONSTER_WEIGHT`, `MESH_PLANNER_WEIGHTS`, `MESH_HOST_CORES`, `MESH_PROBE_BURN_MS=@<path>`.

## Files
Mesh/: new `MeshSplitPlanner.cs`, `MeshLayoutPolicy.cs`, `MeshLayoutCoordinator_Policy.cs`, `MeshPolicyEnv.cs`,
`MeshHostReport.cs` (the message, `MeshHostInfo`, `MeshHostCpuSampler`); edits to `MeshLayout.cs` (settings),
`MeshLayoutValidator.cs` (the settings check), `MeshCoordinatorTypes.cs` (binding report, `TryGetHost`),
`MeshLayoutCoordinator.cs` (report kept, the tick hook, Start, Stop), `MeshLayoutCoordinator_Merge.cs` (survivor rule,
abort hook), `MeshLayoutCoordinator_Collapse.cs` (sibling, ended hook), `MeshCellSupervisor.cs` and
`MeshMergeOperation.cs` (texts), `_SplitSteps.cs` (done and settle hooks), `_MergeSteps.cs` (done hook), `_Split.cs`
(abort and freeze hooks), `MeshCentralNetworkManager.cs` and `_Split.cs` (30021, `TryGetHost`),
`MeshMapSpawnNetworkManager.cs` (the host report every 10 s), `MeshSleep.cs` (30021), `MeshLayoutOverride.cs` (the env
makes the copy), `MeshMapNetworkManager_Load.cs` (the burn file), `_Ghosting.cs` (the receiver's `LentMonsterOwner`,
one line). TestMap: `Editor/ServerMeshLayoutCommandFile.cs`, `Editor/ServerMeshPlayRunner.cs` (`Policy: `, `auto`).
Tools: new `regress_auto.py`, `rigauto.py`, their tests; `regress.py` (registration, `ENV_KEYS`, `expected_zones`
outside `ZONES`, the status-file policy check in `grade_layout`), `rigsplitcrash.py` (`SUP_CANNOT`); tests. Docs:
DESIGN, SETUP, this contract.

## Critique log
- **Acceptance (2026-10-18, BRENDOAIBOX):** 2878 EditMode tests (one skipped, as before), 795 harness tests, ruff clean; `split`,
  `merge`, `splitc2`, `mergec2`, `splitcrash_prewarm`, `splitcrash_drain`, `splitcrash_base` 1/1 each on the slice's build;
  `autosplit` 3/3 (117/117), `autobenefit` 3/3 at its final configuration (42/42; predicted peak 0.23/0.21/0.21 against
  measured 0.24/0.23/0.23, relief 35/32/33 %), `autorefuse` 3/3 (36/36); the eleven base scenarios Static 11/11 and
  `all:Dynamic` 11/11; package 0.80.0.
- **Live runs (2026-10-18):** `autosplit` passed first time (117/117: planned `x 0 + child C.1` at util 0.88, peak
  0.68 predicted, measured 0.45 with the warm burn, relief 49 %, the merge after the kill). `autorefuse` passed once
  the threshold fit this machine (refused `best peak 0.35 over 0.75 x util 0.35`, twice 60 s apart, no split).
  `autobenefit` found two things: (1) the policy saw no end for a split done with stragglers (the cooldown never
  started, a second split was requested into the manual guard) → the cooldown starts at `done`, and the settle only
  re-dates the measurement (`OpSettled`); (2) with 200 steered synthetics spread over the whole 600 m ground, four of
  them were snapped by the kit to round points such as (-200, 1, 200) (no navmesh there) and bounced between C and
  C.1 on every hand-over (1,007 adoptions, 4 stragglers, a drain timeout), and their puppets jumped 400 m; kinematic
  walkers were tried and are worse for a split (the parent's confinement moves them by transform into its kept area:
  C kept 493 of 500, relief −5 %) → the benefit load stays steered and inside C's inner 200 m square, which 2b's
  `split` proved walkable; the navmesh holes of the outer ground are a rig limit, noted in DESIGN.
- **Rev 0 → 1 (2026-10-18), protocol and Mesh/ review:** the kinematic model never refuses a crowd on the cut (best
  side 0.605 of the whole at k = 0.174) → decision 2 says so and `autorefuse` runs steered with the steered fit; the
  merge in `autosplit` fired while the bots stood → the three-stage burn (decision 17); decision 14 needed both
  reports fresh for a cell that is down, and decided "left down" once at queue time with binding conditions → geometry
  only at queue time, siblings ranked by their own fresh util, the collapse waits for a participating one; the
  op-ended hook re-entered the policy from inside its own request (a synchronous abort) → records made right after the
  request, decisions acted on from a snapshot, a synchronous end reported `not measured`; `mergeUtil < splitUtil`
  failed the Coarse runs and the policy's cooldown could undercut 2b's 30 s guard → the rule dropped (the threshold
  bounds it), the cooldown never under `splitCooldownSeconds`; headroom summed the cells' CPU only → the spawner
  reports host-wide cores in use every 10 s; a grandchild not linked to the home handed a monster on silently → the
  receiver tells the home too; measurement had no bound → the 60 s timeout and the superseding; refusals only the
  manual path could see (the family cap) → refused on the policy's side; the settle check read the participants, not
  the family → the family; the burn path was the cell's cwd → absolute; texts: the start line's prefix, the env line,
  `OP_LINE`, the merge doc, N5 (`lastReportAt` reused).
- **Rev 0 → 1, harness review:** the same two blockers (the refusal, the merge timing) and: `autobenefit` would split
  twice at `MESH_SPLIT_UTIL=0.25` (the Coarse cell runs at util 0.70 here, its children at 0.40) → 0.5 and a 600 s
  cooldown, exactly one op graded; the "no policy lines" rule on cell logs was vacuous (the central writes no log the
  harness reads) → the status file is graded; the allowed `Policy:` lines enumerated by prefix; `expected_zones`
  dropped ids outside `ZONES` → it takes (zone, map) outside them; the +x bots stood inside the boundary spawn area →
  `MESH_NO_MONSTERS=1`; `auto Layout op refused:` graded as a failure; the burn file written atomically with an
  absolute path; the synthetic count and the independent util read defined from `Work window` lines; the cut axis
  parsed from the planned line; `MESH_PROBE_BURN_MS` already in `ENV_KEYS`; `rig.kill_bots()` for the kill; the
  override-aware layout grade kept out of `regress.py`.
