# Phase 0 step 4 — the stage 0a tools (Python): implementation contract

Repository: `C:\dev\Game Design\mmo-server-meshing`. Harness folder: `Tools/ServerMeshing/` (NOT `Tools/` as the sheet says).
Python 3.14.3, standard library plus pymysql (imported lazily inside `provision.db_connect`, so tests and every
`--dry-run` run without it; only a real rig run needs it), no numpy. ruff 0.15.10 with no config file. Tests: `unittest`, `cases = {name: (input, expected)}` + `subTest`, failure cases first (`test_rigtools.py:12-24`).

Background (read-only): the build sheet `...\7cb7243d-...\scratchpad\phase0_build_sheet.md` — step 4 at 579-622, run
order §2 at 904-1003, gates §3 at 1004-1044, geography §4 at 1045-1135, results format §5 at 1136-1172, rules §0 at
94-272. The step 2 and step 3 contracts (`step2_design.md`, `step3_design.md` beside this file) define the log lines parsed
here. Survey material: `step4_survey\regrade_reference.py` (offline re-grade, proven on all ten saved reports),
`p0model_proto.py` / `p0model_check.py` (stats and split-model prototype; every number quoted in §E/§F was checked with
it). Where this contract and the sheet disagree, THIS CONTRACT WINS.

## Rules (every file)
- **Timing.** chain7 finished on 2026-09-26 15:29 (`chain7_kinematic.log` ends with `dynidle PASS (50/50 checks)`;
  `run_chain7.py` printed `CHAIN7 DONE` only to its own stdout, which was not kept). Before editing any file in
  `Tools/ServerMeshing/`, confirm no python process runs `run_chain*.py` or `regress.py`
  (`Get-CimInstance Win32_Process -Filter "Name like 'python%'"`). Never touch `Temp/ServerMesh.*`. Do not run Unity, the
  rig, `regress.py`, `p0load.py` without `--dry-run`, or any git write.
- Standard library only (+ pymysql through regress/provision). No numpy, no new dependency, no type-hint-only imports.
  Match the harness style: module docstring with usage, a docstring on every public function/class saying what it
  returns and refuses, minimal other comments, f-strings, no type hints (none of the harness has them).
- `ruff check Tools/ServerMeshing` clean; `python -B -m unittest discover -s Tools/ServerMeshing -p "test_*.py"` passes.
- Pure modules (`p0base`, `p0tools`, `p0cells`, `p0checks`, `p0model`, `p0geo`, `p0results`, `p0plans`, `abgrade`,
  `p0report`) never read files, the clock, the environment or a process: everything comes in as arguments. I/O lives in
  `rigrun`, `abload`, `p0load`, `abregrade`. `p0checks`, `p0plans` and `abgrade` import `rigrun` only for its constants,
  `layout_override`, `check_env` and `run_problems` (no I/O happens at import; `regress` imports nothing third-party at
  module level). Import graph, no cycles (every module uses plain `import x` and reads attributes at call time):
  p0base → (none); p0tools → rigtools; p0cells → p0base, p0tools, rigtools; p0geo → p0base, rigtools; p0model →
  p0base, p0geo, rigtools; p0results → p0base, p0cells; p0checks → rigrun, p0base, p0tools, p0cells; p0plans → rigrun,
  p0tools, p0geo, rigtools (never abload); rigrun → regress, rigtools, p0tools, p0results; abgrade → rigrun, rigtools,
  p0plans; abload → rigrun, abgrade, p0cells, p0checks, p0results, p0plans; p0report → p0base, p0results, p0model,
  p0geo; p0load → everything + abload + p0report; abregrade → abload, rigrun, regress. Every module import is side-effect
  free (no plan built at import). A test imports every module in a fresh subprocess (`python -B -c "import X"`), in
  dependency order and in reverse, so a cycle or an import-time side effect fails the suite.
- Clock: `datetime.datetime.now(datetime.timezone.utc)` / `.astimezone(datetime.timezone.utc)`; never
  `datetime.utcnow()` (deprecated since 3.12, warns on 3.14).
- Errors: never swallowed. Pure code raises `ValueError` subclasses whose message names the thing at fault (the regressor,
  repeat, key, cell, field, file and line). `except Exception` is allowed only at a run boundary (one rig run, one abload
  sub-block), and the text goes into the report and the JSONL row's `problems[]`. Line parsers keep today's style: `None`
  for a line that does not match.
- Floating thresholds use the `p0base` helpers (§E: `at_most`, `at_least`, `above`); histogram statistics are integer µs.
- **abload's default output stays byte-identical** (the offline re-grade in §I proves it): new text appears only when a
  new flag is given.
- Keep every file under ~500 lines (user rule); that is why the pure layer is split (DECISION 2). By this contract's own
  estimates `p0tools` (225 + ~350 ≈ 575) and `abload` (451 − ~132 moved + ~250 new ≈ 569) would pass ~500, so the split
  is part of the plan, not a contingency: `p0cells.py` (pure: `select_windows`, `window_saturated`, `span_saturated`,
  `aggregate_work_ticks`, `cell_record`, `cell_metrics`); `abgrade.py` (pure: `ab_points`, `run_order`, `ab_plan`,
  `grade_points`, `summarize_layout`, `run_line`, `cell_mean`), with abload keeping `build_parser`, `ab_command`,
  `probe_command` and `main` (abregrade still replays `abload.ab_command`, so the re-grade seam is unchanged);
  `p0report.py` (pure `build_report`, §H report); `p0base.py` (leaf: `EPS`, `ModelError`, `at_most`, `at_least`,
  `above`, `ci_verdict`, `kill_verdict`, `point_verdict_eps`, `t975`). If `p0model` still passes ~500, `evaluate_gates`
  moves to `p0report`.

## Decisions (made for the user — report them)
1. **Paths and line numbers corrected.** Everything lives in `Tools/ServerMeshing/`. `p0tools.py` and `test_p0tools.py`
   already exist (steps 2/3) and are extended, not created. abload's moved block is :71-169 (`run_window` :97,
   `cell_window` :127), LAYOUTS :58-62; `regress.ENV_KEYS` is :69-81 and already holds every stage 0a key.
2. **Module split.** The sheet names `rigrun`, `p0tools`, `p0load`. The listed contents are ~2,500 lines, so: `p0base`
   (EPS, ModelError, comparison and verdict helpers, t975: the leaf every other pure module may import, which keeps the
   graph acyclic), `p0tools` (log lines → numbers), `p0cells` (windows → one cell's record and metrics), `p0checks`
   (numbers → run problems), `p0model` (statistics, fits, gates), `p0geo` (split model, layout predictor, map-size
   table), `p0results` (JSONL schema, summaries, Markdown), `p0plans` (run plans as data), `abgrade` (abload's pure plan
   and grading), `p0report` (the report as a pure function), `p0load` (CLI and execution), `abregrade` (offline
   re-grade). Collapsing them later is mechanical.
3. **rigrun moves more than the four names** — also the layout data (`GRID_IDS`, `LAYOUTS`, `CUT_LENGTH`, `GROUND`,
   `MONSTER_ENV`, `layout_override`), `now`, `PARK`, `LINKED`, `cpu_name` — all textually unchanged, because p0load needs
   them too. The ONE signature change: `run_problems(..., allow_uncapped=False)` (the default keeps abload identical); the
   density bridge to 0.53 runs uncapped. strips4 is added by two NEW statements after the moved block
   (`LAYOUTS["strips4"] = …`, `CUT_LENGTH["strips4"] = …`), never by editing the moved dict literals, so the textual
   diff of §L step 3 stays exact at every implementation step. abload re-exports `Report` and `run_window` because `accept_step2.py` /
   `accept_step3.py` / `accept_canary.py` import them from abload.
4. **regress.py is not edited.** Every stage 0a key is already in `ENV_KEYS`. The "ENV_KEYS check" is a plan-time guard,
   `rigrun.check_env(env)`, called by abload and p0load before any run (and in `--dry-run`), plus tests; so
   `request_play`'s silent drop of an unknown key can never bite. (It also keeps regress untouched while chains run.)
5. **ENV_REGISTRY stays the ten implemented keys.** §0.16's other keys are accounted for in two new tables:
   `PENDING_KEYS` (stage 0b/0c keys with the step that builds them) and `DROPPED_KEYS` (`MESH_LOAD_COLLIDE`, step 3
   decision 4). A test proves registry ∪ pending ∪ dropped equals §0.16 exactly, so "mirrors §0.16" is checkable; plans
   using a pending key are refused. Adding pending keys to ENV_REGISTRY would put keys nothing reads into every Play.
6. **abload ordering.** `--order point-major` (today, default) keeps saved reports replayable; `--order repeat-major`
   (the §2 rule: points ascending in odd repeats, descending in even; layouts rotated by repeat) is what p0load passes.
7. **abload flags beyond the sheet:** `--instrument` (the three instrument keys), `--order`, `--jsonl PATH`, `--case NAME`,
   `--dry-run`. `--bands` ALWAYS sets `MESH_BAND` explicitly, 80 included, so the band is asserted by the cell's
   `MESH_BAND=80:` line; a8 sub-block 2 therefore does not reuse sub-block 1's B=80 runs (6 runs, ~13 min).
8. **strips4 uses registry ids W, C, E, NE** (west to east, 150 m wide). C and E keep the halves cut at x = 0 inside
   strips4, C stays in every layout (the re-grade and canary key on C), and no new Log_Map files appear.
9. **Pair grading generalised** without changing today's text: pairs (halves, coarse), (grid, coarse), (strips4, coarse),
   (strips4, halves); a pair is left out when either side's PEAK cell is saturated (for coarse, identical to today); the
   "busiest cell's mean over X's mean" uses X's own busiest cell mean (for coarse, C: identical). strips4/halves gets its
   own per-point verdict and its own overall line, printed even without coarse.
10. **`abload --rates` refuses uncapped values** (`u…`): the Work window budget is then 0 and every run fails the
    uncapped guard. Uncapped measurement stays a p0load-only feature (density bridge).
11. **Offline re-grade in the repo.** `abregrade.py` (from the survey prototype) + a trimmed fixture of the chain3 cell
    logs in `Tools/ServerMeshing/fixtures/abload_chain3_20260925/` (not gitignored, so a Logs cleanup cannot destroy the
    acceptance evidence; ~400 KB) + a unittest that replays it.
12. **a4 and a6 run at 60/0.03** (the fantasy rig's own rates, the same set as a8 and DESIGN 0.71, so a8's frozen
    predictions need no rate conversion). Utilisation is W/1000 at ANY fps (ms/frame ÷ budget = ms/s ÷ 1000), so 30/0.02
    buys headroom only if the per-frame share is large, which K has not measured yet. **Steered points top out at 250
    (a4) and 200 (a6)** because the sat-share rule, not the 0.95 util rule, is the limit on this CPU: the util rule would
    allow ≈ 352 steered owners ((15.83 − 0.60)/0.0432), but chain3's C windows at 13.3-14.3 ms/frame showed 7-18 %
    unwaited frames and one at 14.65 ms 23.1 %, and 0.71's no-monster coarse series (0.047 ms per steered owner: 6.78 ms
    at 150, 11.5 ms and sat 0 % in all six 250-player runs) puts 300 steered owners at ≈ 13.9 ms, inside that zone; 250
    keeps ≈ 3 ms of margin. Kinematic/inert points keep 400. `--players` / `--steered-players` restore the sheet's values.
    Consequence to report: G3-F and the kill rule are evaluated at 60/0.03 only. They cannot be converted to 30/0.02,
    because block K splits owned kinds into per-frame/per-step parts but never puppets; the report prints "30/0.02:
    needs `p0load presence --rates 30/0.02`" for both. The 400-point kinematic/inert arms stay unsaturated only if a
    kinematic or inert owner plus its streaming (a + g) costs < 0.038 ms/frame ((15.833 − 0.6 − 0.12)/400 = 0.0378: C at
    400 owners with one link); step 3's saved C logs (2026-09-26 14:15-14:22, 60 owners on C, streaming included,
    indicative, one run each, label E) give 0.0103 (kinematic) and 0.0164 (inert) ms/frame against 0.0438 steered
    (0.71: 0.0432), so `p0load presence` prints `predict_load`'s figures (§H, label E).
13. **a4 reading of "kinds".** Puppet motion is its own axis on E, with C's owners walking kinematically (cheap, so C
    never saturates and E still receives moving 10 Hz snapshots); "inert" = standing owners with kinematic puppets; the
    owned cost a comes from a **halves arm with only C started** (same rectangle and density as halves' C, no streaming,
    MESH_BAND=300) instead of the sheet's coarse arm, which halves the density; "2 pairings" = steered / kinematic
    puppets; additivity: the puppets-kinematic pairing at the sheet's 200/200 and 400/400 (C ≈ 0.6 + 400·0.0103 +
    400·0.0047 = 6.6 ms/frame at 400/400), the puppets-steered pairing at 100/100 and 200/200 (≈ 0.6 + 200·0.0103 +
    200·0.0440 = 11.5 ms at 200/200; 400/400 would be ≈ 22 ms); 12 runs. Owner costs from step 3's saved C logs
    (indicative, one run each, label E): kinematic 0.0103, inert 0.0164 ms/frame at 60 fps. Still 66 runs. The
    animation-off arm and the additivity arm are reported as their own comparisons (§E `additivity_residual`,
    `arm_difference`); neither the animation-off rows nor the additivity rows ever enter the presence fit, and
    `additivity_residual` = measured − the prediction of the fit made without them (per cell, with that fit's jackknife
    interval), so the residual is not shrunk by rows that helped make the fit. In a bridged report it pools the two
    builds' additivity rows as the presence fit did (review S4).
14. **a6.** Attribution at 200 characters in 141.42 m (the sheet's 100 m² per character, at half its N) with **sparse
    controls** (the same arms at 200 on the 600 m ground) so "excess removed" is separable from the linear cost; the
    removal arms decide the N² gate, so they get 3 repeats; the no-self-collision arm is dropped (no toggle exists);
    Q is the analytic expectation (E) because occupancy is never logged. Also dropped: the sheet's grid N 400 (N
    {100, 200} instead of {200, 400}) and the scale point 400 @ 200 m (both steered 400 saturates at 60/0.03); the scale
    arm adds 50 @ 70.71 m instead (with 100 @ 100 from the grid and 200 @ 141.42, three points at 100 m² per character).
    The N² gate is judged at 100 m² per character (the sheet's own attribution density, 200 @ 141.42 m), NOT at the
    grid's densest point (200 @ 100 m, 50 m² per character) that §3's "at the densest point" names; the sheet is
    inconsistent here (its own attribution, 400 in 200 m, is 100 m² per character while its grid reaches 25 m²), and this
    contract follows the attribution density (open question for the user). The Development-build profiler runs (sheet
    a6, 0.5 h) are not in 0a tools (Decision 24).
15. **a3.** Adds an idle arm (N = 0 at the four rate points, 12 runs at 3 repeats, ~25 min), without which per-entity
    rate splits are unidentifiable; T is read directly from the Work ticks kit mean; the grid identifies F, Ph and C =
    20·T + S only. **3 repeats, not the sheet's 2:** a delete-one-repeat jackknife with 2 repeats has 1 df (t 12.706);
    on the §K noisy example (noise ≤ 0.5 ms/s, a sixth of step 2's between-Play sd of 3 ms/s) it resolves F but gives
    Ph 0.1955 [−0.281, 0.672] and C 30.41 [10.24, 50.58], so no conversion between rate sets is resolved (57.6667 ms/s
    at 60/0.03 converts to anywhere in 42.6-58.5 ms/s at 30/0.02 from Ph's interval alone); with a third repeat t is
    4.303 (illustrative: Ph [0.096, 0.286]). +16 runs, +34 min (open question for the user).
16. **a7.** Adds a linked-idle arm (the registry grid, nine cells idle, read C: 8 links, 4 runs) so λ is measured on
    this CPU; memory is Unity reserved (`mem` in Mesh stats) only — the OS working set is not sampled in 0a.
17. **Canary** = coarse layout, C, 200 kinematic owners, 30/0.02, seed 1 (the same condition as K's kinematic arm at
    30/0.02, so the baseline can be compared with that point; canary rows are NEVER pooled into a fit: they would add
    unbalanced rows at one design point, all in jackknife group 1). Drift is
    |w − baseline|/baseline > 5 % on C's Work ticks work ms/s. A drifted canary is repeated at once: if the repeat passes,
    the rows since the last good canary are re-queued once at the end of the invocation; if it drifts again p0load stops
    (exit 3, "machine state changed"). A canary that is not accepted (failed run, problems, saturated) is repeated at
    once too; two unaccepted canaries in a row stop p0load (exit 3, "the canary cannot run"). For abgeo, whose runs
    happen inside abload subprocesses, the canary after a sub-block (the next sub-block's leading canary, or the
    invocation-end canary) decides: a drifted one re-runs that whole sub-block once. The JSONL stays append-only;
    `report` derives the `drift` exclusion from canary rows by time. The baseline is per build; rows of two builds are
    never pooled unless `report --bridge` passes (§G).
18. **Saturation** (§3's rule per window), judged **per read cell**. A run is excluded **whole** from fits and ratio
    verdicts when, on any read cell, its span aggregate is saturated (frame-weighted sat > 20 % or utilisation ≥ 0.95
    over the selected windows, abload's own run-level rule) or 2 or more of that cell's windows are saturated (dropping
    many bad windows biases slopes low). A single saturated window on a cell is dropped (the sheet's window rule): that
    cell's `ticks` aggregate leaves it out, the run is kept, and the run row's `dropped_windows` {cell: [end, …]} names
    it. A dropped window is NOT a `problems[]` entry (accepted = not problems and exclusion is None, so a problems entry
    would un-accept the run the rule keeps). Evidence (from the saved logs): chain3's band-20 coarse repeat 2 (250
    players, monsters all, run sat 3 %) had one window at 23.1 % (C, 2026-09-25 14:21:58); "any saturated window
    excludes the run" would have thrown away a coarse run abload itself graded as usable. Saturation counts as FAIL for
    budget gates / a trigger for the kill rule; uncapped arms skip it. (Open question for the user: this replaces the
    draft's "any window" rule.)
19. **Work model** adds a streaming term g·G (the owner's cost of streaming its in-band entities to a neighbour) and
    reports two ks: `k_hold = e/a_ref` (G3-F) and `k_band = (e + g)/a_ref` (the split model). The owner reference is
    the steered owned cost (`a_steered`, the production owner) and is stored in the fit row; this reads §4.1's
    k_kind·a_kind as k_kind = e_kind/a_steered, a sheet correction (listed below; open question for the user). Fit rows
    use §5's names through `p0results.FIT_NAMES` (§G). ℓ (snapshot receive) is folded into e in 0a. Fits
    use one row per accepted (run, cell) — cell means over the window — with the jackknife over repeats; per-window data
    is kept inside each run row for later.
20. **Overhead gate** (a1): the sheet's 0.02 ms/frame is kept but evaluated as a CI verdict over 5 interleaved pairs;
    only NO-GO fails accept-a (5 pairs resolve about ±0.06). Other frames under the tick burn: the measured 0.15 ms
    between-Play spread replaces ±5 % (as step 2 already did).
21. **JSONL stays `mesh-p0/1`** with additive optional fields (`row`, `plan_key`, `window_local`, `env`,
    `cells_started`, `windows`, `dropped_windows`, `point`, `logical`, `layout`, a metric's `cell`, families
    `canary`/`accept`, fit names `rate_F/rate_Ph/rate_C/rate_S`, `g_kind`, `k_hold_kind` (also written as §5's
    `k_kind`), `k_band_kind`), and summary rows per (game, family, case, arm, point, cell) where §5 has one per (case,
    arm). A row without `row` reads as `"run"` (the sheet's §5 has no row field).
    Rows are written for EVERY run, accepted or not (`accepted: false` + `problems[]`; the sheet says one row per
    accepted run, so consumers filter on `accepted`). Metric names extend §5's fixed vocabulary with `frames_per_s`,
    `physics_steps_per_s`, `ticks_per_s`, `puppet_count`, `puppet_steer_m`, `puppet_snaps`,
    `puppet_unobserved_poses_per_s`, `puppet_fallbacks`; labelled families use the sheet's brace notation inside
    `name` (`thread_ms_per_s{main}`, `puppet_fallbacks{grid}`), so a metric stays `{name, stat, value, unit, label,
    cell}`. `p0results.METRIC_NAMES` holds the vocabulary. The space team must be told before they write rows.
22. **abgeo needs frozen predictions** (`report --predict abgeo`) unless `--without-predictions` (G-GEO (b) then has no
    verdict). Monster coefficients for predictions come from a6's monsters arm (O) and DESIGN 0.71 (U), labelled E.
    Sub-block 2 sets `--puppets kinematic --rates 60/0.03`, which the sheet's a8 command omits although G-GEO grades
    kinematic runs.
23. **Stage 0b/0c is refused, not stubbed with guesses:** their families exist as p0load subcommands that exit 2 naming
    the step that builds them; `presence --observed` is refused; gates needing clients print "needs 0b (step N)".
24. **No Development build or profiler in 0a tools**: the N² gate's "profiler agrees" half prints "profiler pending".

## Sheet corrections found while designing (tell the user; the tools compute from data, not from these constants)
- §4 is referenced as "§6" (step 4, split model/map table) and §5 as "§7" (0.14, step 4 JSONL).
- The measured band series 0.555 + 1.0·B/W implies W* = B/0.195 ≈ 410 m at B = 80, not 337 m; W* must come from the
  refit's intercept, links term and k_band. §4.7 "≤ 0.35 km: no split pays" fails at exactly 350 m (ratio 0.741 at
  x = 0.05). §4.4 is the interior approximation (2.69 vs an exact 2.19 at S = 1 km, W = 250 m); the table uses the map form.
- Predicted strips4/halves is 0.829 at k = 1 (NO-GO) and 0.703 at k = 0.5 with links ignored (floor 0.05 of the
  ground's load; strips4 interior = 0.30 + 0.2667k, halves = 0.55 + 0.1333k): the go then needs k_band ≤ 0.675; with
  the per-link cost (λ = 0.12 ms/frame ≈ 0.010 of the ground's 11.85 ms/frame variable load at 250 players + 284
  monsters, §H's costs 250 × 0.0432 + 284 × 0.0037; strips4's interior cells have 2 links, halves' 1) it needs
  k_band ≤ 0.600 (0.846 at k = 1). Note: this is stricter than §4.3's own rule for splitting 250 m to 125 m at B = 80
  (W* ≤ 250 ⇔ k ≤ 0.742 at x 0.05), so a k_band in (0.60, 0.74] passes the model's 125 m rule but fails G-GEO (a).
  Say so before a8.
- §4.1's `Σ k_kind·a_kind·U_kind` is read as k_kind = e_kind/a_steered (the production owner), not e_kind/a_kind:
  the rig's kinematic and inert owners are cheap synthetic stand-ins (step 3: 0.0103 and 0.0164 ms/frame against 0.0438
  steered), so e_kin/a_kin would be about 4× e_kin/a_steered. G3-F, the kill rule and the split model use a_steered.
- At k ≤ 0.7, §4.6's own base rule (W_base ≥ 2·W*, W* = 235.8 m) gives 500 m bases: 1 km = 4 × 500 m bases (16 cells
  each, not §4.7's 1 × 1 km base with 64), 8 km = 256 bases (not 64). The 0.5 km map at k 0.7 halves twice (to
  125 m), so its mode is "Dynamic if busy", not §4.7's "Static unless its peak exceeds one process". The table follows
  §4.6; an explicit `base=1000` reproduces §4.3's framing.
- §3's "c0 is 0.60 ms/frame … 18 ms/s at 30 fps and 36 ms/s at 60 fps" treats the floor as purely per-frame. Step 2
  measured the idle ten-cell C at 31.4 ms/s (60/0.03) and 23.2 ms/s (30/0.02), not 15.7: the floor has per-step and
  per-second parts, so c0 at another rate set comes from a7's idle rows or block K (§F `split_inputs`).
- `rigtools.crossing_rate` (both directions across one cut) is not the sheet's 4v/(πW) (outward crossings per player);
  a new `p0geo.crossings_per_player` implements the sheet's formula.
- The Work bins line has never been logged on the rig (no `Work bins` line in any Logs file): accept-a part A is the
  parser's first real input.

## Files
| File | New/edit | Role | Tests |
|---|---|---|---|
| `rigrun.py` | new | one rig run: moved abload block + layouts + env guard + host context | `test_rigrun.py` |
| `abload.py` | edit | imports rigrun and abgrade; strips4, bands, puppets, rates, instrument, order, jsonl, dry-run; `build_parser`, `ab_command`, `probe_command`, `main` | `test_abload.py` (new: parser, refusals) |
| `abgrade.py` | new | abload's pure plan and grading (`ab_points`, `run_order`, `ab_plan`, `grade_points`, `summarize_layout`, `run_line`, `cell_mean`) | `test_abgrade.py` |
| `abregrade.py` | new | offline re-grade of saved abload reports; fixture writer | `test_abregrade.py` |
| `fixtures/abload_chain3_20260925/` | new | 5 chain3 reports + trimmed cell logs (repository only: the add-on snapshot copies `.py`/`.md` only) | used by `test_abregrade.py` |
| `p0base.py` | new | EPS, ModelError, at_most/at_least/above, ci_verdict, kill_verdict, point_verdict_eps, t975 | `test_p0base.py` |
| `p0tools.py` | edit | + Work bins, pairing, markers, key tables | `test_p0tools.py` (extend) |
| `p0cells.py` | new | window selection, saturation, aggregation, cell record, metrics | `test_p0cells.py` |
| `p0checks.py` | new | per-block acceptance checks, accept-a checks | `test_p0checks.py` |
| `p0model.py` | new | least squares, jackknife, fits, rate decomposition, N² form, gates | `test_p0model.py` |
| `p0geo.py` | new | split model, layout loads/predictions, map-size table, crossings | `test_p0geo.py` |
| `p0results.py` | new | JSONL rows, reader/writer, drift, summaries, Markdown | `test_p0results.py` |
| `p0plans.py` | new | run plans as data, ordering, keys, predictions for dry-run | `test_p0plans.py` |
| `p0report.py` | new | `build_report(rows, …)` → Markdown text + summary/fit/gate/prediction rows (pure; p0load reads and writes the files) | `test_p0report.py` |
| `p0load.py` | new | CLI, execution, canaries, report files | via `--dry-run` acceptance |
| `test_imports.py` | new | every module imports alone in a fresh subprocess, in dependency order and in reverse | itself |
| `regress.py`, `rigtools.py` | unchanged | | |

## A. `rigrun.py` — running and reading one rig run
Module docstring: what a run is (abload's protocol) and who uses it (abload, p0load, abregrade). Imports: `import regress
as r`, `import rigtools`, `import p0tools` (for the key tables). **Every call to the rig goes through the module
attribute** (`r.zone_lines(...)`, never `from regress import zone_lines`): abregrade patches `r.zone_lines` and
`rigrun.run_window`, and a from-import silently bypasses the patch (the replay then reads to end of file and reports
"C started N times").

Moved unchanged (text identical to abload today; verify with the diff in §K):
- `GRID_IDS`, `LAYOUTS`, `CUT_LENGTH`, `GROUND`, `PARK`, `MONSTER_ENV`, `LINKED` (abload :57-68, not `GO_RATIO`, which
  stays in abload), plus the strips4 entries: `LAYOUTS["strips4"] = [("W", (-300, -300, 150, 600)), ("C", (-150, -300,
  150, 600)), ("E", (0, -300, 150, 600)), ("NE", (150, -300, 150, 600))]`, `CUT_LENGTH["strips4"] = 3 * 600`.
- `class Report` (:71-82), `layout_override(name)` (:85-90), `now()` (:93-94), `run_window(env, warmup, measure, rep)`
  (:97-124), `cell_window(cell, map_id, since, start, end)` (:127-137), `cpu_name()` (:389-393).
- `run_problems(cells, plan, results, measure, allow_uncapped=False)` (:140-169) — the uncapped guard at :167-168 becomes
  `if not allow_uncapped and s["budget"] <= 0:`. Nothing else changes.

New:
- `start_problems()` → list of the exact lines abload's main prints today before refusing (:416-435, in order: no build,
  already playing, compile errors, stale build with its "Run: python Tools/ServerMeshing/regress.py --build" text, then
  `cannot run: {problem}` per preflight problem). Like main today it STOPS at the first of the four single-line
  refusals (returning that one line, without reading the status when the build is missing) and only then lists every
  preflight problem. Empty list = may start. abload's main prints them and returns 2.
- `check_env(env)` → None; raises `ValueError` naming every key not in `r.ENV_KEYS` ("... are not in regress.ENV_KEYS, so
  a Play request would drop them silently") and, separately, every key in `p0tools.PENDING_KEYS` ("MESH_PHANTOM is a
  stage 0b key (step 5) that no code reads yet"). Values are not checked.
- `build_id()` → sha1 hex of `r.BUILD_MANIFEST`'s bytes, or `""` when it is missing (the JSONL `build`).
- `host_context()` → dict `host` (COMPUTERNAME), `cpu` (`cpu_name()`), `cores` and `logical` (one PowerShell call:
  `Win32_Processor` NumberOfCores/NumberOfLogicalProcessors), `os` (`platform.platform()`), `power_plan` (`powercfg
  /getactivescheme` parsed by `p0results.parse_power_plan`), `build` (`build_id()`), `kit_patches`
  (`p0results.kit_patches(texts)` over every `Assets/**/*.cs` containing `MESH-EDIT: CE-`; today CE-6, CE-7, CE-8,
  CE-9 and CE-11 are present). Called once per invocation, never in `--dry-run`.
- `cell_lines(cell, map_id, since)` → `r.zone_lines(cell, since.strftime("%H:%M:%S"), since.strftime("%Y-%m-%d"),
  map_id)`: the same read cell_window does, for the p0 families (read twice per cell; cost is negligible).

## B. `abload.py` edits
Imports: `import rigrun`, `import abgrade`; `from rigrun import CUT_LENGTH, GROUND, LAYOUTS, MONSTER_ENV, layout_override`; and
`from rigrun import Report, run_window  # noqa: F401 -- re-exported: accept_step2.py / accept_step3.py use abload.<name>`.
Delete the moved definitions and the imports they leave unused (`re`, `datetime`; ruff F401 would flag them). `ab_command` and `probe_command` call **`rigrun.run_window(...)` and
`rigrun.cell_window(...)`** through the module (the re-grade seam). `main` uses `rigrun.start_problems()` and
`rigrun.cpu_name()`. The module docstring gains the new flags and strips4.

New `ab` arguments (all default to today's behaviour):
| Flag | Values | Effect |
|---|---|---|
| `--layouts` | + `strips4` | four 150 × 600 m strips (Decision 8) |
| `--bands` | `20,40,80,150` | a loop dimension; MESH_BAND always set explicitly; error with `--band` > 0 |
| `--puppets` | `steered` \| `kinematic` | MESH_PUPPET_MOTION; unset = "" (today) |
| `--rates` | e.g. `60/0.03` | MESH_RATES, validated by `p0tools.rates_setting`; fps −1 (u…) refused with its message |
| `--instrument` | flag | MESH_AOI_TIMING=1, MESH_WORK_BINS=1, MESH_THREAD_CPU=1 (`p0plans.INSTR`) |
| `--order` | `point-major` (default) \| `repeat-major` | run order (Decision 6) |
| `--jsonl` | path | append one `mesh-p0/1` run row per run (accepted, discarded or failed) |
| `--case` | name, default `ab` | the rows' `case` |
| `--dry-run` | flag | print each run's plan and full env; no preconditions, no rig, no PowerShell, and NO report file in Logs/ |

Structure (pure functions testable without a rig; `build_parser` stays in abload, every other function below lives in
`abgrade.py` and abload calls it as `abgrade.<name>`; abgrade never calls `rigrun.run_window`/`cell_window`, so the
re-grade seam stays in abload):
- `build_parser()` → the argparse parser (`main` uses it; abregrade and p0load build their Namespaces with it, so a new
  flag's default can never be missing from a replay).
- `ab_points(args)` → list of `(players, monsters, band)` in the order `for n in players: for mon in monsters: for band
  in bands` (band innermost, so with one band the order is today's `(n, mon)`); band = each `--bands` value, else
  `args.band` (0 = configured). The per-point dict in `ab_command` is keyed `(n, mon, band)`; every text that names a
  point (the `--- … players, monsters …` run line, the summary table's `players mon` columns, the verdict lines) gains
  `, band {B:g}` (run and verdict lines) or a `band` column (summary table) ONLY when `--bands` is given, so a
  multi-band report never prints two indistinguishable rows. With `--bands` the first header line's band text reads
  `band per point (--bands)` instead of `as configured (80 m)`.
- `run_order(points, layouts, repeats, order)` → list of `(attempt, point, position, layout)`. point-major: `for point
  in points: for attempt in range(repeats): for position, layout in enumerate(rigtools.rotate(layouts, attempt))` (exactly
  today). repeat-major: `for attempt: for point in (points if attempt % 2 == 0 else reversed(points)): for position,
  layout in enumerate(rigtools.rotate(layouts, attempt))`.
- `ab_plan(args)` → list of run specs (the `p0plans` RunSpec shape, §H) with the env today's code builds plus
  `MESH_BAND` per point, `MESH_PUPPET_MOTION`, `MESH_RATES`, INSTR when given; every env passes `rigrun.check_env`.
  abload specs carry `family: "abgeo"`, `game: "fantasy"`, `case` = `--case`, `arm` = the layout name, `point` =
  {players, monsters, band, load, puppets, rates}, `repeat` = attempt + 1, `seed` = `--seed` + attempt, `cells_read`
  = `cells_started` = the layout's cells, `expect` = {} (no puppet-count expectation: monster positions are not known
  in advance). Also used by `p0load abgeo --dry-run`.
- `grade_points(points, layouts, area, repeats, bands_given)` → `(lines, verdict_groups)` — today's verdict section
  (:333-379) generalised per Decision 9, returning text lines instead of printing. For existing layouts the lines are
  byte-identical: the ratio text `"{cut}/{base} = … busiest cell's mean over {base}'s mean {x:.2f}"`, the left-out text
  `"repeat {k} ({label} saturated)"` with label = `base` for a one-cell base else `"{base} peak cell"` (and `"{cut} peak
  cell"` for the cut), `"no usable {base} run"`. The point label gains `", band {B:g}"` only when `--bands` is given. The
  crossing line is unchanged; with strips4 present one more line follows it, exactly
  `f"  not measured: a strips4 layout would see about {rate:.1f} crossings/s at {n} players, each a handoff"` with
  `rate = rigtools.crossing_rate(n, GROUND, GROUND, CUT_LENGTH["strips4"])`. Per point the line order stays: monster
  WARNING, halves/coarse, grid/coarse, strips4/coarse, strips4/halves, crossing line(s).
- `summarize_layout` lists every cell mean when a layout has ≤ 4 cells (was ≤ 3; coarse/halves/grid unchanged).
- Exit code: the halves/coarse group is today's; the strips4/halves group exists when `area is None` and both layouts are
  present, and prints `"strips4/halves (spread load, every point): {overall}"` after today's go/no-go line. With no
  group, today's "no go/no-go" text and exit rule. Otherwise exit 0 iff every printed overall is GO, NO-GO or MARGINAL.
- Header: today's `=== static A/B …` line unchanged; only when any new flag is given, a second line
  `=== phase 0: puppets {p|as configured}, rates {r|as configured}, bands {list|-}, instruments {on|off}, order {o}, rows {path|-}`.
- JSONL (only with `--jsonl`): after each run, `p0results.run_row(spec, run, context, problems, excluded_reason)`
  (the §G signature) where `run` carries `since`, `start`, `end`, the structural `cell_window` results and
  `p0cells.cell_record` per cell (lines from `rigrun.cell_lines`); `problems` = `p0checks.run_problems(spec, run)`
  ALONE (its `check_structure` already returns abload's `run_problems`, so adding both would list every structural
  problem twice), `excluded_reason` = `p0checks.exclusion(spec, run)`; a failed run gets
  `accepted: false, excluded_reason: "run failed", problems: [str(ex)]`, `window_local: null`. Rows are appended with
  `p0results.write_rows`. `context` = `rigrun.host_context()` once at start. The A/B text verdict still uses abload's own
  problems only (unchanged output); `p0load report` is authoritative for gates.
- `probe` is untouched apart from calling `rigrun.run_window` / `rigrun.cell_window`.

## C. `p0tools.py` additions (log lines → numbers; pure) and `p0cells.py`
Existing parsers and `rates_setting` stay as they are. Add the items below; `select_windows`, `window_saturated`,
`span_saturated`, `aggregate_work_ticks`, `cell_record` and `cell_metrics` live in the new pure `p0cells.py` (Rules,
~500-line split), everything else in p0tools. EPS helpers come from `p0base`.
- Constants: `BIN_US = 50`, `BIN_COUNT = 4000`, `HISTOGRAMS = ("tick", "nonaoi", "other", "kit", "send")`.
- `parse_work_bins(line)` → `{"end", "seconds", "tick": {"bins": {index: count}, "over": n}, …}` or None. Grammar from
  `MeshWorkProbe.BinsLine` (`Mesh/MeshWorkProbe.cs:447-472`): `Work bins S s: tick( i:c)* over:N | nonaoi… | other… |
  kit… | send…`. None when: indices not strictly increasing, an index ≥ 4000, a count < 1, a label wrong/out of order,
  a group missing, trailing text.
- `merge_bins(hists)` → `{"bins", "over"}` (exact integer sums per index).
- `percentile_us(hist, ppm, max_us=None)` → int µs or None: C#'s rule in integers, `rank = max(1, (ppm·count + 999999)
  // 1_000_000)`, walk bins upward, return `(i + 1)·50`; rank in overflow → `max_us` (None when unknown, meaning ≥ 200 ms,
  which fails every gate below 200 ms); count 0 → 0. Unclamped (the C# clamp to the histogram max cannot be reproduced:
  nonaoi/other/send maxima are not logged). Do NOT copy the survey prototype's in-bin clamp (`p0model_proto.py:297`,
  `min(edge, max_us)`): return `(i + 1)·50` for any in-bin rank; `max_us` only for an overflow rank.
- `pair_work_lines(lines)` → `(triples, problems)`: take only the Work window / Work ticks / Work bins lines, in file
  order (all three are info lines logged back to back by `LoadTick`, `MeshMapNetworkManager_Load.cs:153-156`, and all
  print the same window seconds with `0.00`; a retired cell logs none of them). A `Work ticks` line pairs with the
  IMMEDIATELY preceding unpaired `Work window` line when both print the same seconds and its stamp is the window's
  stamp or one second later; a `Work bins` line pairs the same way with the immediately preceding Work ticks line. NOT
  "same stamp": the two Logging.Log calls can straddle a second boundary — measured in the 2026-09-26 logs, 3 of 10,543
  Work ticks lines carry the next second's stamp, which a same-stamp rule would turn into a spurious run problem.
  Triple = `{"end": the Work window's stamp (so p0 selects exactly the windows abload selects), "seconds", "window": ww,
  "ticks": wt, "bins": wb or None}`. Unpaired lines → one problem each ("Work ticks at 10:00:05 has no Work window"),
  but `cell_record` keeps only the problems of lines inside the selected span (the warm-up and the Play stop are not
  graded).
- `bins_problems(triple)` → list: tick total ≠ t1 + t2; nonaoi total ≠ t1 + t2 − aoi_frames; other total ≠ t0; send
  total ≠ send_samples; kit total not within `unclosed` of ticks − unclosed.
- `select_windows(items, start, end)` → items with `"end"`/`"seconds"` wholly inside [start − 1 s, end], sorted — exactly
  `rigtools.aggregate_windows`' rule, used for every line family (Work ticks, Threads, Puppets).
- `window_saturated(ww, wt, fanout=False)` → bool: FIRST `ww["budget"] <= 0` → False (an uncapped cell never waits, so
  its sat is ~100 % and the sat test would always fire); then `ww["sat"] > 20.0`, or `ww["work_mean"]/ww["budget"] ≥
  0.95` (`p0base` EPS helpers), or (fanout only) `wt["t2"]/wt["frames"] > 0.02`.
- `span_saturated(windows)` → bool: abload's run-level rule on the selected Work window lines
  (`rigtools.is_saturated(rigtools.aggregate_windows(...))` semantics with the `p0base` EPS helpers). Used by Decision 18.
- `aggregate_work_ticks(triples)` → dict or None (no triples). Sums: seconds, ticks, expected, dropped, cap, unclosed,
  frames, t0, t1, t2, aoi_passes, aoi_frames, parts, late_calls, send_samples, physics_f2, gc0, outside.
  `work_ms_per_s` time-weighted; `work_ms_per_measured_frame` = Σ(w·s)/Σframes (named so it is never confused with the
  metric `work_ms_per_frame` = ms/s ÷ STATED fps; the two differ for an uncapped cell); tick_frame_mean weighted by t1 + t2; other_mean by
  t0; aoi_frame_mean by aoi_frames; kit_mean by ticks − unclosed; sync/aoi/move means by parts; late_mean by late_calls;
  send_mean by send_samples; physics_per_s, late_ms_per_s and busy_percent time-weighted; frames_per_s =
  Σframes/Σseconds; ticks_per_s = Σticks/Σseconds;
  every max = max; `multi_tick_per_1000` = 1000·Σt2/Σframes; p95/p99 from `merge_bins` when every triple has bins
  (`p95_source: "bins"`), else the worst window (`p95_source: "worst-window"`); `budget` is the value shared by every
  window, and a window with another budget adds a `problems` entry naming both values and its stamp (the run is then
  not accepted) instead of raising — a raise would turn a gradable run into "run failed" with no row data; zero
  weights give 0, never NaN. Worked example: windows (5 s, 300 frames,
  400 ms/s, t1 200, t2 0, mean 3.0) and (5 s, 100 frames, 200 ms/s, t1 50, t2 50, mean 6.0) → 300.0 ms/s, 7.5 ms per
  measured frame, tick-frame mean 4.0. Bins example: A {49:96, 60:4} p95 2.50 ms, B {40:90, 70:10} p95 3.55 ms, merged p95 3.05 ms.
- Marker parsers (each → dict or None, `end` = stamp): `parse_band(line)` (`MESH_BAND=20: ghost band and visible range set
  to 20 m`), `parse_load_motion(line)` (`Load generator: motion kinematic (MESH_LOAD_MOTION), fight on (MESH_LOAD_FIGHT)` —
  logged only when not the default), `parse_kinematic_walk(line)` (`Kinematic walk: 60 synthetic players walk by
  transform at 0.90 x move speed; 0 stay steered (…)`), `parse_server_animation(line)` (`Server animation:
  updateAnimationAtServer 0 (MESH_RIG_SERVER_ANIMATION; kit 1)`), `parse_aoi_timing(line)` (`AOI timing: installed on cell
  C (…)`), `parse_tick_burn(line)` (`Tick burn: 2 ms per tick on C` / `on every cell`), `parse_spawned(line)` (`Load
  generator: spawned 250 synthetic players on cell C over x [-150, 150] z [-150, 150] (seed 1, planned 250)`).
- `cell_record(cell, lines, start, end)` → everything p0 reads for one cell of one run: `triples` (selected), `pairing`
  problems, `saturated` (list of saturated window ends), `ticks` (`aggregate_work_ticks` over the selected windows
  minus a single saturated one; that window's end goes in `dropped` and the run row's `dropped_windows`; with 2 or more
  saturated windows nothing is dropped and `p0checks.exclusion` excludes the run, Decision 18), `bins` (merged per
  histogram over the same windows, or None), `threads` (selected + time-weighted means),
  `puppets` (selected + sums/means), `rates` and `rates_cells` (all since Play), `markers` (the parsers above, all since
  Play), `mem_mb` (median `mem` of `rigtools.parse_stats_line` lines in the window).
- `cell_metrics(record, fps, cell)` → list of §5 metric dicts `{name, stat, value, unit, label: "M", cell}`: work_ms_per_s
  (mean), work_ms_per_frame (mean = work_ms_per_s / STATED fps, §0.7), tick_frame_ms (mean, p95, p99, max),
  nonaoi_tick_frame_ms (p95, p99), aoi_tick_ms (mean, max), other_frame_ms (mean, p95), tick_send_ms (mean, p95, p99; the
  kit movement loop in 0a), kit_sync_ms / kit_aoi_ms / kit_move_ms (mean), multi_tick_per_1000, sat_pct (mean),
  `thread_ms_per_s{main|litenetlib|jobs|unnamed|other|sampler}` (mean), process_cores ((process − sampler)/1000),
  mem_MB (median, Unity reserved), frames_per_s, physics_steps_per_s, ticks_per_s, and when Puppets lines exist
  `puppet_count{kinematic|steered}`, `puppet_steer_m{kinematic}` (p95, max), puppet_snaps, puppet_unobserved_poses_per_s,
  `puppet_fallbacks{reason}`.
- `PENDING_KEYS` = {key: "stage 0b, step N" or "stage 0c, step N"} for every §0.16 key not implemented:
  MESH_METER_SENDS (5), MESH_SENDER, MESH_SENDER_SOURCES, MESH_SENDER_TIERS, MESH_SENDER_R0, MESH_SENDER_COMPACT,
  MESH_SENDER_SPREAD, MESH_SENDER_XZ, MESH_SENDER_CLASSIFY_BUILDS, MESH_SENDER_PACKET_BYTES, MESH_SENDER_RESIDUAL_PROBE,
  MESH_CE12_CHECK (6), MESH_PHANTOM, MESH_PHANTOM_VIEW, MESH_PHANTOM_ARRIVAL_PER_S, MESH_PHANTOM_UPLOAD_HZ,
  MESH_PHANTOM_PAYLOAD, MESH_PHANTOM_STEPS, MESH_PHANTOM_REGISTER (5), MESH_LOAD_SHIPS, MESH_LOAD_SHIP_PILOTS,
  MESH_SHIP_WIRE, MESH_SHIP_SCALE, MESH_SHIP_PROJECTILES, MESH_SHIP_PARK_SLEEP, MESH_SHIP_JUMP_SECONDS, MESH_RIG_SHIPS,
  MESH_PUPPET_FEEDER, MESH_SYNC_LOAD, MESH_PREWARM (7), MESH_MONSTER_CELL, MESH_DB_BURST, MESH_SPACES (9, stage 0c).
  `DROPPED_KEYS = {"MESH_LOAD_COLLIDE": "dropped in step 3 (decision 4): the kit already ignores every character pair"}`.
  ENV_REGISTRY is unchanged (ten keys).

## D. `p0checks.py` — per-block acceptance (numbers → run problems; pure)
A **run** is `{"since", "start", "end", "cells": {cid: {"structural": <rigrun.cell_window result>, "p0": <cell_record>}}}`;
a **spec** is a p0plans RunSpec. Each check is `check_x(spec, run) -> list[str]`, messages naming the cell and values.
- `check_structure` → `rigrun.run_problems([(cid, rect) for the READ cells], spec["plan"], structural results,
  spec["measure"], allow_uncapped=bool(spec["uncapped"]))` (links among read cells, started once, rectangle, spawned,
  exceptions, 80 % Work window coverage, probe not Invalid); plus, when `spec["expect"]["links"]` is set (floor's
  linked arm: 8), the read cell's linked set has that many neighbours.
- `check_pairing` → pairing problems + `bins_problems` per triple (only when MESH_WORK_BINS=1: every Work ticks line in the
  window must have its Work bins line).
- `check_coverage` → Work ticks windows ≥ 0.8·measure seconds; Threads windows ≥ 0.8·measure when MESH_THREAD_CPU=1;
  ≥ 4 Puppets windows on puppet cells when MESH_PUPPET_MOTION=kinematic.
- `check_rates` → when `spec["rates"]` is None (accept-a's plain run; abload rows written without `--rates`): no
  `Rates:` line on any read cell (ServerMeshRates installs only when MESH_RATES or MESH_UNCAPPED_CELLS is set, so a Rates
  line is then a problem) and `check_frames` is skipped; otherwise exactly one Rates line per read cell with fps/step as
  `spec["rates"]`, `from_env` true, tick_hz 20; a `Rates: cell X uncapped` line iff X is in `spec["uncapped"]`.
- `check_instruments` → `AOI timing: installed` when MESH_AOI_TIMING=1, and AOI passes 4..6 in every window; no
  `Tick burn` line unless MESH_PROBE_BURN_TICK_MS is set.
- `check_ticks` → every unsaturated window has 100 ± 1 ticks and dropped 0.
- `check_band` → a `MESH_BAND=B:` line with B = spec band on every started cell when set; none when unset.
- `check_motion` → on every cell with a plan: the motion line matches the arm's MESH_LOAD_MOTION/MESH_LOAD_FIGHT (absent
  for the defaults); kinematic owners → a Kinematic walk line with `0 stay steered` (else "kinematic owners fell back to
  the brain").
- `check_animation` → the Server animation line with the set value when MESH_RIG_SERVER_ANIMATION is set.
- `check_puppets` → when MESH_PUPPET_MOTION=kinematic, on each cell in `spec["expect"]["puppets"]`: all fallback counts
  0, steered count 0, mean kinematic count within ±3 % of the expected puppets.
- `check_puppet_count` → mean Work window puppet players within ±3 % of `spec["expect"]["puppets"][cid]` (presence).
- `check_frames` → frames_per_s within ±2 % of the capped fps and physics_steps_per_s within ±1 of 1/step (rates).
- `FAMILY_CHECKS` = {"canary", "rates", "presence", "density", "floor", "abgeo", "accept"} → tuples: COMMON =
  (structure, pairing, coverage, rates, instruments, ticks, band, motion, animation); rates adds frames; presence adds
  puppets and puppet_count; abgeo = COMMON minus motion plus puppets when kinematic.
- `run_problems(spec, run)` → all checks of the spec's family (or of `spec["checks_family"]` when set: accept-a part E
  grades each first-use run with its target block's tuple), in the order above.
- `exclusion(spec, run)` → None or a reason, for any read cell: `span_saturated` over its selected windows →
  `"saturated (span) on C"`; 2 or more saturated windows on that cell → `"saturated in 3 of 12 windows on C"`; None for
  uncapped arms. A single saturated window on a cell is not an exclusion (it is dropped from that cell's aggregate and
  listed in `dropped_windows`, never in `problems[]`) — Decision 18. p0load/abload set
  `accepted = not problems and exclusion is None`.
- accept-a (from `accept_step2.py` / `accept_step3.py`, same thresholds): `accept_instrument_checks(runs)` → list of
  (ok, text): ≥ 6 Work ticks windows; no exceptions; probe not Invalid; 100 ± 1 ticks, none dropped; tick share 1/3 and
  2/3 ± 0.02; physics 33.3 ± 1 and 50 ± 1 /s; AOI passes 5 ± 1; parts ≥ 95 % of ticks; thread classes within ±5 % of
  process and clock = cycles; burn +2.000 ± 0.100 ms on tick frames; other frames within 0.15 ms; every Work ticks line
  has a Work bins line; for single windows the merged-bin p95 equals the line's p95 within 0.05 ms (clamp cases
  excepted). `accept_puppet_checks(runs)` → steered has no Puppets line; E holds ≥ 10 kinematic; snaps ≤ max(2, 1 % of
  samples); 7-11 unwatched poses per puppet per s; steer kinematic p95 < 1.0 m; inert has 0 moving samples.
  `overhead(pairs)` → `{"mean", "se", "lo", "hi", "verdict"}` from paired differences (instrumented − plain) in C's
  work_ms_per_frame, t with n − 1 df, `p0base.ci_verdict(lo, hi, 0.02)` (Decision 20).

## E. `p0model.py` — statistics, fits, gates (pure)
Conventions: sums with `math.fsum`. `EPS = 1e-9` relative slack, `class ModelError(ValueError)`, the comparisons, the
verdicts and `t975` live in the leaf `p0base.py` (so p0geo, p0checks, p0results and p0cells can use them without importing
p0model); p0model imports them from there.
- `t975(df)` (p0base) → two-sided 95 % t: 1 12.706, 2 4.303, 3 3.182, 4 2.776, 5 2.571, 6 2.447, 7 2.365, 8 2.306,
  9 2.262, 10 2.228, …, 30 2.042; above 30 the next LOWER tabulated df (40 2.021, 60 2.000, 120 1.980); above 120,
  1.980 (the df-120 value, conservative; 1.960 is below the true t at every finite df and is never used); df < 1 →
  ModelError.
- `least_squares(rows, y, names)` → `{coef, n, p, df, rms, r2, min_sine, residuals}`. rows = dicts (missing name = 0).
  Householder QR on columns scaled to unit norm, in the declared order; `|R_jj| < 1e-8` → refuse naming the combination
  (solve R[:j,:j]·c = (Qᵀx_j)[:j], unscale). Exact messages: `"{n} rows but {m} responses"`; `"{n} rows for {p} regressors
  ({names}): need at least {p+1}"`; `"singular design: regressor 'U_kinematic' is zero in every row"`; `"singular design:
  regressor 'links' equals 1 x 'c0' in every row (drop one of them, or add runs where they differ)"`. `min_sine` =
  min_j 1/sqrt([(XsᵀXs)⁻¹]_jj) with Xs the unit-norm columns (the sine between column j and the span of all the others;
  order-invariant), computed from R as 1/‖row j of R⁻¹‖ (diag of R⁻¹R⁻ᵀ); NOT min |R_jj|, which depends on the declared
  order (0.3086-0.4714 for the §K 7-row example). `min_sine < 1e-3` adds a warning the caller copies into `problems[]`.
- `jackknife(rows, y, names, groups, derived=None)` → `(full, {name: {value, se, lo, hi, t, leave_one_out}})`;
  delete-one-group refits; SE = sqrt((G−1)/G·Σ(θ₋ⱼ − θ̄)²); CI = θ̂ ± t975(G−1)·SE; derived = {name: f(coef, where,
  keep)} recomputed per deletion. G < 2 → `"a jackknife over repeats needs at least 2 repeats; got 1"`; a singular
  deletion → `"leaving out repeat 3: singular design: …"` (never skipped).
  **Groups must be balanced:** G = the number of repeats present at EVERY design point of the fit. A third-repeat run
  (§H `third_repeat`) stays in the full fit but is never left out as its own group: it joins the group of the repeat it
  replaces (`replaces_repeat` in its spec). A small extra group would barely move the estimate when left out while
  cutting t (12.706 → 4.303 at G 2 → 3), so the interval would under-cover (simulation, 4000 trials, slope over
  n ∈ {100, 200, 400}, sd 3 ms/s: 2 repeats 0.953; 2 repeats + one third-repeat run as group 3 0.904; the same run
  merged into its `replaces_repeat` group 0.949; 3 repeats + one extra run as its own group 0.929). The plans keep every fit-feeding arm at one
  repeat count; a fit that still pools arms with different repeat counts (a user-overridden plan: some point has a
  repeat outside the most common set) uses delete-one-run groups and says so in `method` (`"ols + jackknife over runs
  (unbalanced repeats)"`). Runs LOST at some points (every point's repeats a subset of the most common set: a
  saturated or failed run, none extra) keep the delete-one-repeat groups, the lost run's group simply left out without
  it, and a note names the points (review 2026-09-28, S1: one lost run otherwise switched the fit to run groups, and
  the kill statistic's coverage fell from 0.95 to 0.79-0.86 under repeat drift).
  **G = 2** (floor; any 2-repeat block): the t(1) = 12.706 interval stays the verdict interval (its coverage is
  nominal, just wide); beside it the report prints the delete-one-run interval labelled "runs as groups (assumes no
  between-repeat drift)", information only, never used for a verdict.
- `ratio_of(num, den)` → derived function; denominator ≤ 0 → `"ratio undefined: denominator O_steered = -0.01 ms/s in
  repeat 2 left out"`. A ratio whose denominator's CI includes 0 is reported `unbounded` and its gates give NO VERDICT.
- `expected_local_pairs(n, rect, bucket_m=20.0, origin=(-300.0, -300.0))` → E[Σn_b²] = n + n(n−1)·Σ_b(a_b/A)² for
  uniform placement over rect (any rectangle; a_b = overlap of bucket b). 400 in (−100,−100,200,200) → 1996.0;
  200 in (−50,−50,100,100) → 1489.52; 250 on the 600 m ground → 319.17. n < 0 or empty rect → ModelError.
- `FIT_SPECS` = {"presence": required c0, links, O_steered, O_kinematic, U_steered, U_kinematic, G_player; optional
  O_inert, U_inert, and Q (or N², whichever `n2_form` chose) included when density's q interval excludes 0 — the
  choice is written in the fit rows' `notes`; presence rows vary Q nonlinearly in N (C at 400 in 300 × 600 m: Q 754.7,
  Q/N 1.89; at 100: 122.0, 1.22), so omitting a real q biases a_steered and every k}, {"density": c0, O_steered, Q;
  optional O_kinematic, O_inert, O_monster}, {"refit": c0, links, O_*, U_*, G_*, Q, all optional except c0}.
  **Row selection:** `n2_form` and `fit("density")` use only density rows with case ∈ {grid, scale} and arm base; the
  attribution and sparse arms feed only `n2_removed`; bridge-0.53 feeds only its bridge ratio; `fit("refit")` takes
  from density the same base rows plus the attribution monsters-on arm (O_monster, Decision 22); the presence fit never
  takes the animation-off or additivity rows (Decision 13). `work_rows(run_rows, fit_id)` → `(rows, y, groups, notes)`: one row per
  accepted (run, cell); y = the cell's work_ms_per_s; O_kind from Work window owned players tagged by the cell's
  MESH_LOAD_MOTION (monsters → O_monster); U_kind from puppet players tagged by the holding cell's MESH_PUPPET_MOTION
  (U_inert when the owners are inert; puppet monsters → U_monster_{motion}); G_player = the other cell's measured
  puppet players in two-cell layouts, else `p0geo.layout_loads` (E, noted) — moving owners only: an inert arm's
  streaming is absorbed into O_inert, because no solo-inert arm separates them; links = started cells touching this cell;
  Q = `expected_local_pairs` over placement ∩ cell. Refuses mixed rates ("rows from two rate sets (60/0.03 and 30/0.02):
  fit one rate set at a time and convert with block K"), mixed cpu, mixed build (unless bridged, §F), non-accepted rows,
  rows with `capped` false ("uncapped rows are not work at a rate set"), and rows with MESH_LOAD_FIGHT=0 unless the spec
  lists an `O_steered_nofight` regressor. All-zero optional columns are dropped and named in `notes`; an all-zero
  required column is refused.
- `fit(run_rows, fit_id)` → fit rows (§G shape, named through `p0results.FIT_NAMES`) for every coefficient plus derived
  `k_hold_{kind}` = e_kind/a_steered (also written as §5's `k_{kind}`), `k_band_{kind}` = (e_kind + g_player)/a_steered,
  `g_player`; jackknife over the `repeat` field (balanced groups, above).
- `rate_decomposition(runs)` → `(full, ci)` for columns `per_frame` (F, ms per frame), `per_step` (Ph, ms per physics
  step), `per_second` (C, ms/s) over measured frames/s and steps/s; derived `other_per_second` S = C − mean(20·kit_mean)
  over the fit's rows, and the interaction contrast I = m(60,50) − m(60,33.3) − m(30,50) + m(30,33.3) (its own
  jackknife: when the full fit or a deletion lacks one of the four cells, I is left out with a note and F, Ph, C, S are
  still fitted, since three cells identify them; review S3) (flag "non-additive
  rates" when |I| > max(t·SE_I, 5 % of mean w)). Refusals: one frame rate or one step (`"the rate decomposition needs two
  frame rates and two physics rates; got fps [60] and physics steps/s [33.3, 50.0]"`), an uncapped row, a saturated row.
  Exact case F 0.35, Ph 0.20, C 30 (T 1.2 ms × 20 + S 6): w = 47.1667 / 50.5 / 57.6667 / 61.0 at (30,0.03) (30,0.02)
  (60,0.03) (60,0.02) → recovered exactly. Per-entity splits: `rate_split(kind_runs, idle_runs, n)` → one least-squares
  fit over both arms with regressors per_frame, per_step, per_second and their products with an arm indicator; the
  split = the indicator coefficients / n, jackknifed over repeat (each leave-one-out drops that repeat from both arms),
  so the two arms' shared repeats are handled jointly instead of combining two separate intervals.
- `convert_rates(w, f1, p1, f2, p2, per_frame, per_step)` → w + F·(f2 − f1) + Ph·(p2 − p1) (57.6667 at 60/0.03 → 50.5 at
  30/0.02 with F 0.35, Ph 0.20).
- `n2_form(rows)` → `{"form": "qQ" | "qN2", "sse", "aic", "other"}`: fits c0 + a·N + q·Q and c0 + a·N + q·N² on the
  density base rows (row selection above), chooses the lower AIC (n·ln(SSE/n) + 2p); ties keep qQ.
- `n2_removed(base_dense, arm_dense, base_sparse, arm_sparse)` → share of the excess removed =
  [(base_dense − arm_dense) − (base_sparse − arm_sparse)] / (base_dense − base_sparse); excess ≤ 0 → ModelError.
- `band_slope(points)` → jackknifed slope of halves/coarse ratio against B/600 over (repeat, band, ratio) points.
- Comparisons (p0base): `at_most(v, T)`: v ≤ T + EPS·max(1, |T|); `at_least(v, T)`: v ≥ T − EPS·max(1, |T|);
  `above(v, T)`: v > T + EPS·max(1, |T|). `threshold_bin_edge_us(ms)` = (round(ms·1000)//50)·50 (2.5 → 2500, 1000/60 →
  16650). A histogram gate passes iff the p95 bin's upper edge (i + 1)·50 ≤ T_us; values in [T, T + 50 µs) fail (a
  frame of exactly 2.5 ms lands in bin 50, edge 2.55 ms): a conservative bias of less than one bin, stated in the gate
  row's notes. This is the one gate that is not exact at its threshold.
- Verdicts (p0base): `ci_verdict(lo, hi, T)` (pass means ≤ T): GO if at_most(hi, T), NO-GO if above(lo, T), else
  MARGINAL. `kill_verdict(lo, hi, T)`: KILL if at_least(lo, T), NOT KILLED if hi < T (strictly, with EPS), else
  MARGINAL. Repeated ratios: `point_verdict_eps(ratios, threshold, min_runs)` = `rigtools.point_verdict`'s rule with
  `at_most`/`above` (2.1/2.8 = 0.7500000000000001 must still be GO at 0.75; 61 two-decimal pairs with a/b = 0.75
  exactly compute above 0.75); the report's G-GEO (a) uses it, while abload's text keeps `rigtools.point_verdict`
  (byte-identical output).
- `evaluate_gates(summary, fits, rows)` → gate rows (§G), 0a gates only. Every gate row's `rule` states each threshold
  in ms/s and in ms/frame at the stated rate (§3, critique 7b), e.g. `"600 ms/s = 10.00 ms/frame at 60 fps"`, and at
  30 fps too where the gate is evaluated there:

| Gate | Statistic | Rule | At the boundary |
|---|---|---|---|
| G3-F (U) | `k_hold_kinematic` CI | ci_verdict(·, 0.5) | hi 0.5 → GO; lo 0.5 → MARGINAL; lo 0.5000001 → NO-GO. Note "O half needs 0b (step 5)" |
| Kill, characters (U) | c0 + λ + 500·e_kinematic (derived, CI) | kill_verdict(·, 600 ms/s) | lo 600 → KILL; lo 599.9, hi 600 → MARGINAL; hi 599.9 → NOT KILLED |
| N² | `n2_removed` at the attribution point (200 @ 141.42 m, 100 m² per character, not the grid's densest point: Decision 14), per removal arm | best arm ≥ 0.80 → PASS | 0.80 passes; verdict text adds "profiler pending" |
| G-GEO (a) | strips4/halves per repeat, B = 80, kinematic, spread | point_verdict_eps(·, 0.75, 2) | [0.75, 0.74, 0.70] → GO; [2.1/2.8, 0.74] → GO; [0.76, 0.74] → MARGINAL; 1 pair → INSUFFICIENT |
| G-GEO (b) | \|predicted − measured mean\| per point, against the newest prediction set made at or before the first accepted abgeo run started (none: NO VERDICT, "not frozen") | at_most(·, 0.05) at every point; a predicted point with no accepted pair is named, and PASS then becomes INSUFFICIENT (a miss still FAILs) | 0.80 vs 0.75 passes; 0.8001 fails |
| G-GEO (c) | predicted band slope inside the measured band-slope CI | inclusive | 0.9 in [0.9, 1.1] passes; 0.8999 fails |
| Floor | idle cell work ms/s (a7) per rate set | at_most(·, 600) | 600 passes |
| Canary drift | \|w − base\|/base | above(·, 0.05) drifts | 105.0 vs 100 → no drift; 105.1 → drift; 95.0 → no drift |
| Coverage | covered/measured seconds | at_least(·, 0.8) | 48.0 of 60 passes; 47.99 fails |

  G-GEO (c)'s predicted slope is NOT the characters-only k_band/(1+x): b9-bands runs with `--monsters all`, so the
  measured slope is load-weighted (Σ k_kind·V_kind/((1+x)·ΣV)); monster puppets carry their own k (0.71: steered monster
  puppet/owned 0.0087/0.0037 = 2.35; owned monsters are ≈ 9 % of the ground's 11.85 ms/frame), which at k_char 0.6
  moves the effective k to ≈ 0.755 (+26 %). Predicted slope = the least-squares slope against B/600 of
  `p0geo.predict_ratio(halves, coarse)` at B = 20, 40, 80, 150 with the refit's coefficients (monsters included, the
  same players and monsters as b9-bands); the x inside predict_ratio is the coarse run's own c0/(w − c0), not
  split_inputs' x at the 800 ms/s trigger.

  Saturated rows: excluded from fits/ratios; a budget gate (G2-style, none in 0a) would FAIL; the kill rule counts a
  saturated puppet cell as KILL-triggering. Everything else prints "needs 0b (step N)": G1-F, G2-F, G3-F (O),
  Crossings (B10), Database (0c), Phantom validity (J), and all space gates.

## F. `p0geo.py` — split model, layout predictor, map-size table (pure; raises `p0base.ModelError`)
- `halving_ratio(width, band, k, x=0.0, sigma=1.0, link_share=0.0)` = (x + ½ + link_share + σ·k·B/W)/(1 + x). At x = 0,
  σ = 1: ½ + kB/W (W 400, B 80, k 1 → 0.70). At x 0.05, k 1, B 80: 0.6000 / 0.6762 / 0.7415 / 0.8286 for W = 1000 /
  500 / 350 / 250.
- `break_even_width(band, k, x=0.0, sigma=1.0, gate=0.75, link_share=0.0)` = σkB/(g(1+x) − x − ½ − link_share) — with
  g = 0.75, link_share 0: 4σkB/(1−x) (336.8421 at k 1, B 80, x 0.05). Denominator ≤ 0 → `"no width pays: the floor alone
  gives a ratio of 0.773, above 0.75"` (x = 1.2); B ≤ 0, k < 0, σ ∉ (0, 1], x < 0 → error naming the argument.
- `smallest_cell(base, band, w_star)` — executable ladder: `W = base; while W >= W*·(1−EPS) and W/2 >= B·(1−EPS): W /= 2`.
  Equals max(B, W*/2) rounded up the ladder whenever that is ≤ base/2 (the sheet's "s_min = max(B, W*/2)").
  B exactly 125 splits 250 → 125; B 125.0001 stays 250; W* 500.00000000000006 still lets 500 split.
- `split_inputs(fit_ci, rates, trigger_ms_per_s=800.0, parent_links=0, links_gained=1, floor=None)` → `{k_hold,
  k_band, x, link_share, V}`. V = trigger − c0 − λ·parent_links; x = (c0 + λ·parent_links)/V (the floor includes the
  parent's EXISTING links); link_share = λ·links_gained/V (the links the split adds). Worked (parent_links 0): c0 36,
  λ 7.2, a 2.592, e_kin 1.296, g 0.2592 → k_hold 0.5, k_band 0.6, V 764, x = 36/764 = 0.04712, link_share = 7.2/764 =
  0.009424; W* = 131.1 / 209.8 / 314.7 m at B = 50 / 80 / 120; s_min from 1000 m: 125 / 125 / 250. With parent_links 1:
  x 0.05708, link_share 0.00951, W* 132.6 / 212.2 / 318.3 m. c0 and λ at a rate set other than the fit's are NOT
  scaled as per-frame costs (step 2: idle C 31.4 ms/s at 60/0.03 but 23.2, not 15.7, at 30/0.02): they come from
  `floor` = {c0, lam} measured at `rates` (a7 measures idle and linked idle at 30/0.02 and 60/0.03), or for another rate
  point from `convert_rates` with the idle arm's F and Ph (a3 `rate_decomposition`); split_inputs refuses (ModelError
  `"c0 at 30/0.02 needs a7's idle rows or block K's idle split"`) when neither is given. Worked 30/0.02 example: c0
  23.2 → x = 23.2/776.8 = 0.0299. k_hold and k_band stay at the fit's rate set (Decision 12: block K never splits
  puppets).
- `layout_loads(cells, band, players, monsters=0, owner_motion="steered", puppet_motion="steered",
  monster_counts=None)` → per cell {O, U, G, links, O_monster, U_monster}. Characters use uniform density ρ: O =
  ρ·area; U = ρ·Σ_j≠i area(rect_j ∩ expand(rect_i, B)); G = ρ·Σ_j linked area(rect_i ∩ expand(rect_j, B)); links from
  `rigtools.rects_touch` (label E). Monsters are NOT spread uniformly (the rig places them in spawn areas, each on the
  cell that owns it): O_monster/U_monster per cell come from `monster_counts`, the measured Work window monster counts
  of that layout and band (a6 monsters-on arm, b9-main, or the chain3 logs; label M); for a layout never run with
  monsters (strips4) they are estimated from the grid layout's measured per-cell counts, each 200 m grid cell's monsters
  split by area overlap (label E). Without `monster_counts` and with monsters > 0 → ModelError. Before a8 (frozen
  predictions) the only halves/grid per-cell counts are in saved abload reports (`own Np/Mm pup Pp/Qm` per cell, e.g.
  halves C 140 m / E 143 m, grid C 56 m at 250 players): p0load parses them from the fixture's chain3 reports
  (`fixtures/abload_chain3_20260925/`, halves and grid with monsters all) and passes them in (label M, older build).
- `predict_ratio(coef, cut, base, band, players, monsters, owner_motion, puppet_motion, monster_counts=None)` → max
  ŵ(cut)/max ŵ(base) (monster loads per `layout_loads`). Worked (no monsters, floor 0.05 of the ground's load, links
  ignored, B 80): strips4/halves = 0.829 at k = 1, 0.703 at k = 0.5.
- `total_cpu_ratio(side, width, band, k, c0=0.0, load=1.0, lam=0.0, max_neighbours=8, edges="map")` — "interior" is the
  sheet's §4.4 form (1.3456 / 1.7424 / 2.6896 / 5.1984 for W = 1000/500/250/125 at k 1, B 80; 1.1728 / 1.3712 / 1.8448 /
  3.0992 at k 0.5); "map" counts real links (0 when m = 1; otherwise 4·min(3,cap) + 4(m−2)·min(5,cap) +
  (m−2)²·min(8,cap); 84 for 4×4; the formula alone gives 2 links for one cell at cap 2) and
  internal band only (S 1 km, k 1: 1.0, 1.3456, 2.1904, 4.4944). S/W not whole → `"the map side 1000 m is not a whole
  number of 300 m cells"`.
- `crossings_per_player(speed, width)` = 4v/(πW): 0.00637 / 0.02546 / 0.05093 per s at 1000 / 250 / 125 m, v = 5
  (300 players × 0.3 × 0.05093 = 4.584/s).
- `choose_base(side, w_star, band, max_base=None)` → (base, population_limited): the smallest side/2ⁿ with base ≥ 2·W*
  and base ≥ B (side itself when side < 2·W*); a smaller `max_base` wins and flags "population", never below B. Refuses
  a side narrower than B and a max_base no rung ≥ B meets; map_size_table refuses an explicit base narrower than B.
  So no base or s_min is narrower than B (review S2: at k_band ≲ 0.07-0.09 the table printed 31.25 m and 62.5 m cells
  at B 50/80, which the validator refuses).
- `map_size_table(sides, bands, ks, x, link_share=0.0, sigma=1.0, gate=0.75, base=None, core_budget=None,
  max_base=None, speed=5.0, players=None, moving=0.3, c0=0.0, lam=0.0, load=1.0)` — passes `link_share` to
  `break_even_width`/`halving_ratio` (at c0 36, λ 7.2, k 0.6: W* 125.9 / 201.5 / 302.2 m without it, 131.1 / 209.8 /
  314.7 m with it) and `c0`/`lam`/`load` to `total_cpu_ratio(edges="map")` → rows {side, band, k, w_star, base, bases, s_min, halvings, max_cells_per_base (capped by
  core_budget), first_ratio, total_cpu (map form), crossings_per_s, mode}. Mode: bases > 1 → Dynamic; halvings ≥ 2 →
  "Dynamic if busy"; 1 → "Static unless its peak exceeds one process"; 0 → Static. ks given as (k̂, lo, hi) emit three
  rows. Worked rows (B 80, x 0.05): k 1 → 300 m base 300 s_min 300 Static (ratio 0.778); 350 m → 175, h 1 (0.741);
  500 m → 250, h 1 (0.676); 1000 m → 250, 16 per base, "Dynamic if busy" (0.600); 2/4/8 km → 4/16/64 × 1000 m bases,
  Dynamic. k 0.7 → 300 m → 150 (0.702); 500 m → 125, 16 per base (0.630); 1000 m → 4 × 500 m bases, 125 m (these
  follow §4.6's base rule W_base ≥ 2·W*, not §4.7's table: see Sheet corrections).
  An explicit base of 1000 reproduces all 12 cells of the sheet's §4.3 table.

## G. `p0results.py` — the `mesh-p0/1` rows (pure)
- `SCHEMA = "mesh-p0/1"`; `ROW_TYPES = ("run", "summary", "fit", "gate", "prediction")`; `FAMILIES_0A = ("canary",
  "accept", "rates", "presence", "density", "floor", "abgeo", "fit")`; `class SchemaError(ValueError)`.
- Run row (all §5 fields, plus the Decision 21 extensions): `schema`, `row: "run"`, `team: "rig"`, `game` (fantasy for
  presence/density/abgeo; neutral for canary/accept/rates/floor), `model` (geographic for presence/density/abgeo, else
  none), `family`, `case`, `arm`, `stage: "0a"`, `repeat`, `seed`, `order` (int: the run's 1-based position in its
  block), `plan_key`, `point` (dict), `started_utc`
  (measurement start in UTC ISO), `window_local` {since, start, end} (local ISO; the logs carry local stamps), `host`,
  `cpu`, `cores`, `logical`, `os`, `power_plan`, `build`, `kit_patches` ({"CE-6": "present", …, "CE-12": "absent"}),
  `rates` {fps, fixed_step_s, tick_hz: 20} (fps and fixed_step_s null when the spec sets no MESH_RATES: accept-a's
  plain run, abload rows without `--rates`), `capped`, `env` (the full dict sent), `layout`, `cells_started`,
  `accepted`, `problems[]`, `excluded_reason` (null | "saturated …" | "run failed" | "drift" applied by report),
  `dropped_windows` {cell: [end, …]} (a single saturated window left out of that cell's aggregate, Decision 18; not a
  problem);
  load: `kind: "character"`, `owned` {character_steered|character_kinematic|character_inert: n, monster: n} (measured
  means, summed over cells), `puppets` {character_steered|character_kinematic: n, monster: n}, `awake_monsters: null`,
  `clients` {real: 0, phantom_virtual: 0, phantom_sink: 0, observer_kind: null}, `v_move_mean: null`, `v_move_p95: null`,
  `density` (m² per character over the load area), `visible_range_m` = `band_m` (MESH_BAND or 80), `payload_B: null`,
  `budget_kBps: null`, `scale: 1.0`; `metrics[]` (`p0cells.cell_metrics` per read cell); `windows` {cell: [{end, seconds,
  frames, work_ms_per_s, owned_players, owned_monsters, puppet_players, puppet_monsters, saturated}]}.
- `run_row(spec, run, context, problems, excluded_reason)` → dict (above). `summary_row`, `fit_row(fit_id, name, ci,
  regressors, n, method="ols + jackknife over repeats", reference=None)`, `gate_row(name, verdict, inputs, rule, notes)`,
  `prediction_row(point, ratio, inputs_sha, created_utc)` share the header subset `schema, row, team, game, family,
  stage, build, cpu`. `gate_row`'s `rule` states each threshold in ms/s and ms/frame at the stated rate (§E).
- `FIT_NAMES` maps regressors to §5's fit-row vocabulary, and fit rows use the §5 names: c0 → `c0`; O_{kind} →
  `a_{kind}`; U_{kind} → `e_{kind}`; links → `lambda`; Q (or N²) → `q`; G_player → `g_player` (extension);
  k_{kind} = `k_hold_{kind}`, written under both names (`k_{kind}` is §5's); `k_band_{kind}` (extension); block K's
  `rate_F/rate_Ph/rate_C/rate_S` (extensions).
- `REQUIRED` per row type (read_rows refuses a row missing one, naming it): run = §5 header + load fields + `metrics`;
  summary = header subset + case, arm, point, cell, n; fit = header subset + fit_id, name, value, lo, hi; gate = header
  subset + name, verdict; prediction = header subset + point, ratio, inputs_sha.
- `write_rows(path, rows)` appends one `json.dumps(row, separators=(",", ":"), ensure_ascii=False)` per line (creates
  the folder). `read_rows(paths)` → rows; `SchemaError("rows.jsonl:12: schema 'mesh-p0/2' is not mesh-p0/1")`, unknown
  `row`, missing required field (named, per `REQUIRED`), invalid JSON (line named). Unknown extra fields are kept
  (forward compatible).
- File names follow §5's `<team>_<game>_<family>_<stamp>.jsonl`: run rows `rig_<game>_<family>_<stamp>.jsonl`
  (canaries `rig_neutral_canary_<stamp>.jsonl`), report output `rig_<game>_fit_<stamp>.jsonl` (one file per game),
  frozen predictions `rig_fantasy_abgeo-prediction_<stamp>.jsonl`. The report fits only `run` rows; summary, fit and
  gate rows from earlier reports in `Logs/p0/` are ignored, and prediction rows are read only for G-GEO (b).
- `apply_drift(rows, threshold=0.05)` → new list: each canary `check` row gets `drift` true/false against its build's
  baseline (mean of accepted `baseline` rows); every non-canary run row whose next canary on the same build (by
  `window_local.end`) drifted becomes `accepted: false, excluded_reason: "drift"` with a problems entry; rows with no
  later canary get a `problems` note "no canary after this run" (still accepted).
- `bridge_ok(rows, old_build, new_build)` → canary check on the new build within 5 % of the old baseline.
- `summary_rows(rows)` → one per (game, family, case, arm, point, cell) over accepted run rows: median, min, max and n of
  work_ms_per_s, plus nonaoi_tick_frame_ms p95 median and tick_send_ms p95 median.
- `MARKDOWN_COLUMNS = ("Game", "Case", "Arm", "Kind", "Rates", "Owned / puppets / clients / V", "Work ms/s (median
  [range])", "Non-AOI tick-frame p95 ms", "Tick send p95 ms", "Downlink p95 kB/s", "Key metric", "Gate", "Verdict", "n")`
  (§5 order); `markdown_summary(summary_rows, gate_rows)` → str (Case cell = `"{case} [{cell}]"`; downlink "–" in 0a;
  clients 0, V "–"). `map_size_markdown(table)` → str (side, B, k, W*, base, s_min, max cells, mode).
- `parse_power_plan(text)` → "Balanced" etc. from `powercfg /getactivescheme` output, else None.
  `kit_patches(texts, expected=("CE-6", "CE-7", "CE-8", "CE-9", "CE-11", "CE-12"))` → {id: present|absent}.

## H. `p0plans.py` — stage 0a run plans as data (pure) and the run plans
**RunSpec** (dict, shared with abload): `family, case, arm, point` (dict), `repeat` (1-based), `seed`, `order`, `layout`,
`cells_started`, `cells_read`, `plan` ({cid: n}), `env` (dict passed to request_play), `warmup`, `measure`, `rates`
("60/0.03", or None when no MESH_RATES is set), `uncapped` (list), `band` (float or None), `expect` ({"puppets": {cid:
n}, …}), `key`; optional `replaces_repeat` (third repeats, §E balanced groups) and `checks_family` (accept-a part E).
- `INSTR = {"MESH_AOI_TIMING": "1", "MESH_WORK_BINS": "1", "MESH_THREAD_CPU": "1"}` on every measurement run (accept-a
  "plain" excepted: no INSTR, no MESH_RATES, `rates` None); `MESH_NO_MONSTERS=1` unless the arm has monsters;
  `MESH_RATES` always explicit (plain excepted); `MESH_LOAD_SEED` =
  str(seed), seed = repeat (canary: 1); layout env = `MESH_LAYOUT_OVERRIDE` (rigrun.layout_override) + `MESH_SPAWN_CELLS`.
  Every other `regress.ENV_KEYS` key is cleared by request_play. Every spec passes `rigrun.check_env`.
- `stepped(points, repeat)` → ascending on odd repeats, descending on even. `block_order(arms, repeats)` → for repeat r,
  arms rotated left by (r − 1)·A // R (A arms, R repeats; offsets all below A, so no arm takes the same position twice:
  0, 1, 2 for block K's 4 arms and 0, 3, 6 for 10 arms; review F3: ceil(A/R) wrapped, so block K's repeat 3 replayed
  repeat 1) (for A ≤ R this is `rotate(arms, r − 1)`, every position covered); each arm's points run back to back,
  stepped. `plan_key(spec)` = sha1 of `family|case|arm|json(point, sort_keys)|repeat`, first 12 hex.
- `third_repeat(specs, rows, key_metric, thresholds=())` → specs for (arm, point) of 2-repeat blocks whose two accepted
  runs differ by > 10 % on the key metric, OR lie on opposite sides of any threshold in `thresholds` (the sheet's
  "straddle a threshold", §2; per block: floor 600 ms/s; saturation util 0.95 and sat 20 %) (seed 3, appended at the end
  of the block). Each carries `replaces_repeat` = the original repeat whose run is further from the median of the
  three runs at that (arm, point), so the fit's jackknife groups stay balanced (§E).
- `predict_load(spec)` → per started cell {ms_per_s, util, label}, all labelled E, at 60 fps: 0.60 + 0.0432/steered
  owner (0.71) + 0.0103/kinematic owner + 0.0164/inert owner (step 3's saved C logs, streaming included, one run each)
  + 0.0440/steered puppet (0.71; step 3 gives 0.0369) + 0.0047/kinematic puppet (step 3: 16.8 ms/s over 60 puppets
  at 60 fps, 0.28/60, not 0.28/50) + 0.0037/owned monster + 0.0087/monster puppet ms/frame; util = ms/s / 1000;
  "[predicted saturated]" at util ≥ 0.85 (a chain3 window at util 0.88 already had sat 23.1 %). abgeo specs carry
  `expect` = {}, so their per-cell owned and puppet counts come from `p0geo.layout_loads` (hence p0plans → p0geo).
- `dry_run_lines(specs, per_run_s)` → per run: `[i/n] family case arm point rN seed S key K | layout L | start … |
  read … | W+M s`, `env: KEY=VALUE …` (only the keys set, sorted), `predicted: C 9.2 ms/frame util 0.55 …`; header "every
  other regress.ENV_KEYS key is cleared (N keys)"; footer: runs, minutes at 126 s per 45+60 run and 90 s per 25+40 run.
- `STAGE_LATER = {"fanout": "stage 0b, steps 5-6", "packets": "stage 0b, steps 5-6", "prewarm": "stage 0b, step 7",
  "crossings": "stage 0b, step 5", "calibrate": "stage 0b, step 5", "workerab": "stage 0c, step 9", "dbburst": "stage 0c,
  step 9", "spaces": "stage 0c, step 9"}`.

Plan builders (defaults below; every list overridable from p0load): `canary_plan(arm, runs)`, `accept_plan(parts)`,
`rates_plan(repeats=3)`, `presence_plan(repeats=3, rates="60/0.03", points=(100, 200, 400), steered_points=(100, 200,
250), additivity_kinematic=(200, 400), additivity_steered=(100, 200))`, `density_plan(rates="60/0.03", players=(100,
200))`, `floor_plan(repeats=2)`, `abgeo_invocations(jsonl_path, subs=(1, 2, 3))` → [(label, abload argv)].

### Run plans (45 s warm-up + 60 s measured unless stated; reads = cells whose rows are written)
**canary** (family canary, game neutral) — layout coarse, start/read C, `MESH_LOAD_PLAN=C:200`,
`MESH_LOAD_MOTION=kinematic`, `MESH_RATES=30/0.02`, seed 1, INSTR.
| Arm | When | Runs |
|---|---|---|
| baseline (`canary --baseline`, a2) | once per build | 2, + 1 when they differ > 2.5 % |
| check | invocation start (unless a canary on this build is < 2 h old in Logs/p0), before any run once 2 h have passed, invocation end; on drift: one immediate repeat | ~1 per 2 h |

**accept-a** (a1, family accept, attended)
| Part | Runs | Env / command | Read | Checks |
|---|---|---|---|---|
| A instrument (25 + 40 s, ten-cell rig, all cells) | plain {}; r60 {MESH_RATES=60/0.03 + INSTR}; r60burn {r60 + MESH_PROBE_BURN_TICK_MS=C:2}; r30 {MESH_RATES=30/0.02 + INSTR}; r30burn; 5 overhead pairs (A = MESH_RATES=60/0.03, B = A + INSTR), orders AB BA AB BA AB | as listed | C | `accept_instrument_checks`, `overhead` |
| B puppets (ten-cell rig) | steered; kin {MESH_PUPPET_MOTION=kinematic}; kin_kwalk {+ MESH_LOAD_MOTION=kinematic}; kin_inert {+ MESH_LOAD_MOTION=inert}; all with MESH_LOAD_PLAN=C:60, MESH_LOAD_AREA=80,0,30, MESH_NO_MONSTERS=1, MESH_RATES=60/0.03, INSTR | as listed | C, E | `accept_puppet_checks` |
| E first-use (25 + 40 s, coarse, C) | r30s03 {MESH_RATES=30/0.03 + INSTR}; r60s02 {MESH_RATES=60/0.02 + INSTR}; uncap {MESH_RATES=60/0.03, MESH_UNCAPPED_CELLS=C + INSTR}; nofight {C:60 steered, MESH_LOAD_FIGHT=0, MESH_RATES=60/0.03 + INSTR}; noanim {C:60, MESH_RIG_SERVER_ANIMATION=0, MESH_RATES=60/0.03 + INSTR}; band300 {halves C,E, C:60, MESH_LOAD_MOTION=kinematic, MESH_PUPPET_MOTION=kinematic, MESH_BAND=300, MESH_RATES=60/0.03 + INSTR} | as listed | C (band300: C, E) | `p0checks.run_problems` with `checks_family`: r30s03, r60s02 → rates (adds frames); uncap, nofight → density (structure with allow_uncapped for uncap; motion); noanim, band300 → presence (animation; band, puppets, puppet_count with E = 60). None of these toggles or values has ever produced a log line on the rig (no `Server animation:`, no `Rates: cell … uncapped`, no `fight off`, no 30/0.03 or 60/0.02 Rates line, MESH_BAND only 20 and 40 in any Logs file), so their first real input is seen attended, not in an overnight chain |
| C regress (subprocess, a failed scenario rerun once) | `regress.py pet party crash harvest build seam seamcold sleep pvp wake`; same + `--env MESH_CELL_MODE=all:Dynamic`; `regress.py dynidle`; the ten + dynidle with `--env MESH_PUPPET_MOTION=kinematic` | | | exit 0 each |
| D tests | `regress.py --unit`; `python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"`; `ruff check Tools/ServerMeshing` | | | exit 0 each |

**rates** (a3, block K, family rates, game neutral) — layout coarse, start/read C, INSTR, `MESH_LOAD_PLAN=C:200` (idle:
empty). Arms (block_order): idle, steered (MESH_LOAD_MOTION empty), kinematic, inert; each arm's rate points
`30/0.03, 30/0.02, 60/0.03, 60/0.02` stepped. 4 × 4 × 3 repeats = **48 runs** (Decision 15: 2 repeats leave Ph and
C unresolved; +16 runs, +34 min at 126 s per run; no third repeats in a 3-repeat block). Key metric: C work_ms_per_s.
Fit: `rate_decomposition` per arm; `rate_split` per kind against idle (one joint fit per kind, §E).

**presence** (a4, B1-U, family presence, game fantasy) — `MESH_RATES=60/0.03`, `MESH_BAND=300`, INSTR, no monsters,
spread load over the owning cell. 3 repeats.
| Case / arm | Layout, started / read | Plan and arm env | Points | Runs |
|---|---|---|---|---|
| baseline | halves, C,E / C,E | no load | – | 3 |
| main / puppets-steered | halves, C,E / C,E | C:N, MESH_LOAD_MOTION=kinematic | 100, 200, 250 | 9 |
| main / puppets-kinematic | same | C:N, MESH_LOAD_MOTION=kinematic, MESH_PUPPET_MOTION=kinematic | 100, 200, 400 | 9 |
| main / owners-inert | same | C:N, MESH_LOAD_MOTION=inert, MESH_PUPPET_MOTION=kinematic | 100, 200, 400 | 9 |
| additivity / puppets-steered | same | C:N,E:N, MESH_LOAD_MOTION=kinematic | 100, 200 | 6 |
| additivity / puppets-kinematic | same | + MESH_PUPPET_MOTION=kinematic | 200, 400 | 6 |
| solo / owners-steered | halves, C / C | C:N | 100, 200, 250 | 9 |
| solo / owners-kinematic | halves, C / C | C:N, MESH_LOAD_MOTION=kinematic | 100, 200, 400 | 9 |
| animation-off / puppets-steered, puppets-kinematic | halves, C,E / C,E | main arm env + MESH_RIG_SERVER_ANIMATION=0 | 200 | 6 |
**66 runs.** Every steered arm shares N = 200 with the animation-off arm and the additivity pairings, so
`arm_difference` and `additivity_residual` compare at measured points. expect.puppets: E = N (C = N in additivity).
Fits: presence spec (with Q when density's q interval excludes 0, §E), over main, solo and baseline rows only
(never animation-off or additivity rows, Decision 13); e from E, a from solo C, g from halves C minus solo C;
k_hold/k_band. Gates: G3-F (U), kill rule (U). `--observed` → exit 2 "B1-O needs phantom observers (stage 0b, step 5)".

**density** (a6, B4, family density, game fantasy) — coarse, start/read C, `MESH_RATES=60/0.03`, INSTR, owners steered
with fight on, no monsters unless stated; crowd square `MESH_LOAD_AREA=0,0,R` (R = side/2; empty for side 600).
| Case / arm | Points (N @ side m) | Repeats | Runs |
|---|---|---|---|
| grid / base | N {100, 200} × side {100, 200, 400, 600} | 3 | 24 |
| scale / base (100 m² per character; 100 @ 100 is in the grid) | 50 @ 70.71 | 3 (it enters fit("density"), whose jackknife groups must be balanced, §E) | 3 |
| scale / base — also the attribution base | 200 @ 141.42 | 3 (it decides the N² gate) | 3 |
| attribution / inert, no-fight, kinematic (MESH_LOAD_MOTION=inert; MESH_LOAD_FIGHT=0; MESH_LOAD_MOTION=kinematic) | 200 @ 141.42 | 3 | 9 |
| attribution / monsters-on (MESH_NO_MONSTERS empty, MESH_MONSTER_AREAS=all) | 200 @ 141.42 | 3 (it enters fit("refit") for O_monster, Decision 22; balanced groups, §E) | 3 |
| sparse / inert, no-fight, kinematic | 200 @ 600 | 3 | 9 |
| bridge-0.53 | grid layout (no override), the nine map-1 cells started, read C (expect links 8, as floor's linked/idle; review F5), C:500, area empty, MESH_RATES=60/0.03, MESH_UNCAPPED_CELLS=C, monsters at the scene default (both keys empty) | 2 | 2 |
**53 runs** (51 + one more repeat each of scale 50 @ 70.71 and monsters-on, ~4 min). The bridge is exempt from
saturation (allow_uncapped) and never enters a fit (work_rows refuses uncapped rows); its ratio = C's
`work_ms_per_measured_frame` / 47.1 ms (0.53), a cross-CPU, cross-build ratio (label E): ms/s ÷ 60 means nothing for an
uncapped cell (~21 frames/s at ~47 ms per frame). Fits (row selection §E): `n2_form` and fit("density") on grid/scale
base rows, then `n2_removed` per removal arm (attribution vs sparse); N² gate.

**floor** (a7, family floor, game neutral) — INSTR, no monsters, 2 repeats.
| Case / arm | Layout, started / read | Plan | Rates | Runs |
|---|---|---|---|---|
| cell / idle, players-150 | coarse, C / C | none; C:150 (steered) | 30/0.02, 60/0.03 | 8 |
| linked / idle | grid (no override), nine map-1 cells / C | none | 30/0.02, 60/0.03 | 4 |
**12 runs.** Metrics: main ms/s (Threads main and Work ticks work), process − sampler, cores, 'unnamed' (network threads
under Mono), mem_MB (Unity reserved). Gate: floor ≤ 600 ms/s; c0 compared with 0.60 ms/frame (info). λ = linked idle −
idle per link at each rate set, jackknifed over its 2 repeats (t 12.706, the verdict interval) with the delete-one-run
interval printed beside it (§E, G = 2). third_repeat thresholds: 600 ms/s.

**abgeo** (a8, B9, family abgeo, game fantasy, model geographic) — runs abload as a subprocess per sub-block, each
preceded by a canary; always `--instrument --order repeat-major --rates 60/0.03 --jsonl <file> --case <label>`:
| Label | abload arguments | Runs |
|---|---|---|
| b9-main | `ab --puppets kinematic --players 150,250 --monsters off,all --layouts coarse,halves,grid --repeats 3` | 36 |
| b9-bands | `ab --puppets kinematic --players 250 --monsters all --layouts coarse,halves,strips4 --bands 20,40,80,150 --repeats 3` | 36 |
| b9-steered-b150 | `ab --puppets steered --players 250 --monsters all --layouts coarse,halves --bands 150 --repeats 3` | 6 |
| b9-steered-b80 | `ab --puppets steered --players 250 --monsters all --layouts halves,strips4 --bands 80 --repeats 3` | 6 |
**84 runs.** Refused without a prediction file (`Logs/p0/rig_fantasy_abgeo-prediction_*.jsonl`) unless
`--without-predictions`.

**report** — p0load reads `Logs/p0/*.jsonl` (+ `--import other.jsonl`) with `read_rows` and hands the rows to the pure
`p0report.build_report(rows, bridge=None, predict=None, build=None)`: `apply_drift` → the latest accepted row of each
(build, plan_key) (a planned run made twice, e.g. an abgeo sub-block re-run in full by --resume, counts once; review
F4/IO-2) → only team rig rows feed fits and gates (other teams' `--import` rows are summarised per team and game; review
R1), `--build SHA` keeping one rig build → groups by build (refuses to pool rig builds unless
`--bridge OLD_SHA` and `bridge_ok`) → `summary_rows` → fits in this order: rates, density/N² (`n2_form` fixes the fit
form, §3 "fixed before any grid is fitted"), floor/λ, presence (with Q when density's q interval excludes 0), refit
over presence + density + abgeo rows at 60/0.03 (row selection §E) → `evaluate_gates` → map-size table (sides 0.3,
0.35, 0.5, 1, 2, 4, 8 km × B {50, 80, 120} × k_band (k̂, lo, hi); x, link_share and load = V from `split_inputs`, c0 and
lam from the floor fit) → returns the Markdown text and the summary, fit, gate and prediction rows; p0load writes
`Logs/p0/report_<stamp>.md` (Markdown summary, gates, map-size table) and `Logs/p0/rig_<game>_fit_<stamp>.jsonl` (one
per game: summary, fit, gate rows). `--predict abgeo` writes frozen prediction rows to
`Logs/p0/rig_fantasy_abgeo-prediction_<stamp>.jsonl` for every b9 point (ratio per pair, players, monsters, band,
puppets; monster loads per §F `layout_loads`) with `inputs_sha` (sha1 of the input rows' plan_keys and values) and
`created_utc`.

## I. `p0load.py` — CLI and execution
```
p0load.py canary [--baseline]            p0load.py rates [--repeats 3]
p0load.py accept-a [--parts A,B,C,D,E]   p0load.py presence [--players …] [--steered-players …] [--rates …] [--observed]
p0load.py density [--players …] [--rates …]   p0load.py floor
p0load.py abgeo [--subs 1,2,3] [--without-predictions]
p0load.py report [--import FILE …] [--predict abgeo] [--bridge SHA | --build SHA]
p0load.py fanout|packets|prewarm|crossings|calibrate|workerab|dbburst|spaces   -> exit 2, names the stage and step
common: --dry-run --resume --no-canary --warmup S --measure S
```
- `--dry-run`: builds the plan, runs `check_env` on every spec, prints `p0plans.dry_run_lines` (abgeo: each abload argv
  and `abgrade.ab_plan` of it; report: the files it would read and the fits it would make); touches no editor, database,
  PowerShell or Temp file; exit 0 (2 on a refused plan).
- Otherwise: `rigrun.start_problems()` (non-empty → print, exit 2); `rigrun.host_context()`; for each spec:
  `rigrun.run_window(spec["env"], warmup, measure, rep)`, `rigrun.cell_window` + `p0cells.cell_record(cell,
  rigrun.cell_lines(...), start, end)` per read cell, `p0checks.run_problems` + `exclusion`, `p0results.run_row`,
  `write_rows` to `Logs/p0/rig_<game>_<family>_<YYYYmmdd_HHMMSS>.jsonl` (canaries to `rig_neutral_canary_<stamp>.jsonl`),
  and one report line per run (`rigrun.Report`, saved as `Logs/p0/rig_<family>_<stamp>.txt` in a `finally`). A run that
  raises is a failed run (row written), the block goes on.
- `report`: reads the files, calls `p0report.build_report`, writes its outputs (§H report).
- Review 2026-09-28: third repeats are decided over the WHOLE block plan (not the --resume-filtered specs) and over
  this build's rows only, less those already accepted; a drifted canary decides them again (a third runs at most twice
  per invocation) and thirds are never in the drift requeue list (F1, IO-1). rates --repeats 2 gets them too
  (thresholds: saturation; F2). A resumed block seeds its requeue list with the earlier invocation's runs on this
  build after the last good canary, and its verdict counts every plan key (IO-3). The start canary is skipped only
  when the NEWEST canary on this build passed under 2 h ago (a drifted or unaccepted newest canary forces one; IO-4).
  `accept-a --resume` is refused, exit 2: it grades its runs together (IO-6).
- Canary scheduling and drift per Decision 17; `third_repeat` (with the block's thresholds) after repeat 2 of 2-repeat blocks; `--resume` skips
  plan_keys that already have an accepted, non-drift row on the same build; `--no-canary` skips scheduled canaries (rows
  then carry "no canary after this run").
- accept-a part C/D: `subprocess.call([sys.executable, "-u", …], cwd=ROOT)`, output to `Logs/p0/accept_<part>_<stamp>.log`.
- Exit codes: 0 every planned run accepted (report: done); 1 the block ran but some runs were not accepted (listed);
  2 refused (preconditions, bad arguments, stage 0b/0c, pending key, missing predictions); 3 stopped by a second drifted
  canary.

## J. `abregrade.py` + fixture — the offline re-grade (from `step4_survey\regrade_reference.py`)
```
abregrade.py replay [--logs DIR] [REPORT …]            logs from DIR (default Logs/); with no REPORT, every abload_*.txt
                                                     in DIR whose header is the 0.71 ab/probe shape (others listed as skipped)
abregrade.py fixture --out DIR [--logs DIR] REPORT …  copy the reports and trimmed cell logs for their sessions into DIR
```
(Two explicit subcommands: argparse cannot mix an optional subcommand with bare REPORT positionals — the first
positional would be matched against the subcommand names, so `abregrade.py Logs/abload_ab_….txt` would fail with
"invalid choice".) Output per report: `<name>: IDENTICAL, exit N; <verdict line>` or `<name>: K line(s) differ, exit N`
with the first five differences; exit 1 when any replayed report differs (skipped reports do not count).
- `parse_report(lines)` → (command, argparse.Namespace, [recorded window start or None per run]); the Namespace comes
  from `abload.build_parser().parse_args([command, "--players", …])` rebuilt from the report text, so every newer flag
  has its default; ValueError for a report that is neither the 0.71 ab shape nor probe. A report written with the new
  flags still carries the unchanged `=== static A/B …` line on lines[1] (§B), so the header alone would take it for a
  0.71 report (and `float("per point (--bands)")` would raise): `parse_report` raises
  `SkipReport("written with phase 0 flags; its JSONL rows carry window_local")` when lines[2] starts with
  `"=== phase 0:"`, and the no-REPORT mode lists such files under skipped. (Such reports are out of scope: their JSONL
  rows carry `window_local`, which a later re-grade can use exactly.)
- `CellLogs(logs_dir)` reads `os.path.join(logs_dir, basename(r.zone_log(cell, suffix, day, map_id)))`, stamped lines
  sorted by stamp; `sessions(logs, lines, args, starts)` → (since, until, start, end) per run: the k-th `Own cell from`
  line of C after the header time; since = Own − 3 s, until = next Own − 3 s; start = the run line's HH:MM:SS, else Own
  + warmup − 2 s; end = start + measure. Fewer sessions than runs → ValueError. Every day's lines are read with the
  next day's (`CellLogs.span_lines`, as regress.zone_lines does), and a recorded start earlier than its Own line's time
  of day lies on the next day, so a report that runs over midnight replays (review IO-5).
- `regrade(path, logs)` → (exit code, diffs): patches `rigrun.run_window` (returns the next recorded window) and
  `regress.zone_lines` (bounded to [since, until)) with `unittest.mock.patch.object`, runs the UNCHANGED
  `abload.ab_command` / `probe_command` with stdout redirected, compares text line by line.
- `fixture(out_dir, report_paths, logs_dir)` copies the reports and, per cell log file touched, only the lines inside
  [first since, last until] that match `Work window|Own cell from|Load generator: spawned|Neighbour zone \S+ linked|
  Exception` (everything grading reads), keeping file names; a cell log file with no kept lines is not written. The fixture is
  `Tools/ServerMeshing/fixtures/abload_chain3_20260925/` with the five chain3 reports (`abload_ab_20260925_142623.txt`
  band 20, `…143808` band 40, `…144600` crowd 0,0,60, `…145351` crowd 0,0,150, `…150142` grid repro).

## K. Tests (failure cases first; `cases` dicts + `subTest`)
The sheet's list (lines 604-617) → where it is covered:
| Sheet item | Test |
|---|---|
| every parser on valid, malformed, incomplete lines, plus the old format | `test_p0tools`: `parse_work_bins` (valid; each rejection in §C; empty groups `tick over:0 …`), `pair_work_lines` (paired, unpaired ticks, unpaired bins, stamp mismatch), each marker parser (real lines from §C, truncated, wrong key); `percentile_us` ({10:90} over 10 with max_us 231400 → 231400; {49:96, 60:4} with max_us 2400 → 2500, no in-bin clamp); old format: a saved 0.71 `Work window` line still parses with `rigtools.parse_work_window` and is ignored by `parse_work_ticks` (exists) |
| each acceptance failure alone, then all passing | `test_p0checks`: one good run fixture (built in the test from sample lines) that passes every family's checks; one case per check with a single mutation (unlinked cell, second Own line, wrong spawn, exception line, 47 s coverage, missing bins line, bins total mismatch, two Rates lines, wrong fps, stray uncapped line, no AOI install, 3 AOI passes, 99 ticks with 1 dropped, missing band line, motion line on a default arm, `2 stay steered`, missing animation line, a puppet fallback, kinematic count −4 %, puppet players −4 %, frames/s −3 %); `check_rates` with rates None: no Rates line → no problem, a Rates line present → problem; `checks_family` selects the target tuple; `exclusion`: one saturated window → None (and the window in `dropped_windows`, not in problems), two on one cell → reason, span saturated → reason, uncapped arm → None; accept-a checks and `overhead` |
| least squares recovers known coefficients; singular refused with a message | `test_p0model`: no rows; rows = regressors; mismatched y; zero column; links = c0; L = 10·U; O_total = O_steered + O_kinematic (exact messages §E); then exact recovery (7 rows: (links, O, U) → y = (0,100,0) 147.6, (0,200,0) 277.2, (0,400,0) 536.4, (1,0,100) 153.6, (1,0,200) 285.6, (1,0,400) 549.6, (1,0,0) 21.6 → c0 18, links 3.6, O_steered 1.296, U_steered 1.32, k_hold 1.0185185, min_sine 0.308607); permuted names (same coefficients, min_sine still 0.308607); scaled column |
| jackknife intervals | 1 repeat; singular deletion ("leaving out repeat 1: …"); denominator ≤ 0 in a deletion; y = 10, 12, 14 → CI [7.0313, 16.9687]; the noisy 21-row example (c0 18.016667 [17.403919, 18.629414], k 1.018165 [1.010638, 1.025692], leave-one-out 1.018941, 1.016420, 1.019137); G = 2 → SE = \|d\|/2, t 12.706; balanced groups: 2 full repeats + 1 third-repeat run with `replaces_repeat` 2 → G = 2, not 3; arms with different planned repeat counts → method "ols + jackknife over runs (unbalanced repeats)"; the noisy 2-repeat block K rows: groups = repeat → per_step 0.1955 [−0.281, 0.672], t 12.706; groups = run → [0.1458, 0.2452], t 2.365; `t975` (test_p0base) table incl. 35 → 2.042, 45 → 2.021, 500 → 1.980, 0 → ModelError |
| rate decomposition recovers F, Ph and T | only fps 60; only one step; an uncapped row; a saturated row; exact F 0.35 / Ph 0.20 / C 30 / S 6 (T = 1.2 from the kit mean); off-nominal rates (59.8, 33.2) still exact; noisy F 0.345833 [0.3141, 0.3776]; an injected interaction of 5 ms/s flagged; `convert_rates` 57.6667 → 50.5; `rate_split` on exact kind and idle arms recovers the per-entity split, and each leave-one-out drops that repeat from both arms |
| split model | `test_p0geo`: refusals (§F); ratio 0.70 at x 0; the four x-0.05 ratios; W* 336.8421; s_min boundary cases; the ladder; §4.3's 12 cells; both total-CPU forms, map form m = 1 at cap 2 → 0 links; crossings 0.00637/0.02546/0.05093 and 4.584; strips4/halves 0.829 and 0.703; `split_inputs` worked examples (parent_links 0 → x 0.04712, link_share 0.009424, W* 131.1 / 209.8 / 314.7; parent_links 1 → x 0.05708, link_share 0.00951, W* 132.6 / 212.2 / 318.3; 30/0.02 with floor c0 23.2 → x 0.0299; another rate set without `floor` or a rates fit → ModelError); `layout_loads` for halves at B 80; monsters without `monster_counts` → ModelError; strips4 monster counts from grid counts by area overlap |
| map-size table for known k and B | the worked rows as exact dicts; `link_share` passed through (k 0.6, x 0.04712, B 50: W* 131.1 with 0.009424, 125.9 without); core_budget 10 caps 16; max_base 500 at k 1 → base 500 flagged population; empty sides → ModelError; side not a power-of-two multiple of an explicit base → ModelError |
| gates exactly at each threshold | every boundary row of the §E gate table (G-GEO (a) includes [2.1/2.8, 0.74] → GO through `point_verdict_eps`); `at_most(0.8 − 0.75, 0.05)` true where the naive `<=` is false; `threshold_bin_edge_us` 2.5 → 2500, 1000/60 → 16650; bin gate {0:94, 49:6} → 2500 passes 2.5 ms, {0:94, 50:6} → 2550 fails (the stated one-bin bias); a gate row's rule reads "600 ms/s = 10.00 ms/frame at 60 fps" |
| saturation exclusion | `test_p0cells.window_saturated` (sat 20.0 not, 20.1 yes; 15.8365/16.67 yes, 15.83 no; fanout t2 3/150 not, 4/150 yes; budget 0 never); `test_p0cells.cell_record` (one saturated window → left out of `ticks` and listed in `dropped`); `test_p0checks.exclusion`; `test_abgrade.grade_points`: base peak saturated → left out ("coarse saturated", text identical to today), cut peak saturated, halves as base with its peak saturated, all pairs left out → SATURATED |
| stepping order by repeat, and rotation covering every position | `test_abgrade.run_order` (point-major equals today's nested loops; repeat-major reverses points on even repeats; each layout in each position once over 3 repeats); `test_p0plans.stepped`/`block_order` (A ≤ R: every position; A = 9, R = 3: each arm in a different third per repeat) |
| JSONL round trip and schema version | `test_p0results`: bad JSON, wrong schema, unknown row type, missing field per `REQUIRED` for each row type (each names file:line); write → read equals input; unknown extra field kept; `FIT_NAMES` (O_steered → a_steered, U_kinematic → e_kinematic, links → lambda, k_hold_kinematic also as k_kinematic); `apply_drift` (drifted canary marks the rows before it, a passing repeat after it keeps later rows; no-canary note) |
| Markdown column order | `markdown_summary` header equals `MARKDOWN_COLUMNS` joined; one row's cells in that order; `map_size_markdown` header |
| every ENV_REGISTRY key is in regress.ENV_KEYS | exists; plus: ENV_REGISTRY (10) ∪ PENDING_KEYS (33) ∪ DROPPED_KEYS (1) equals §0.16's 44 keys (14 Mesh/ + 30 TestMap/, listed literally in the test), pairwise disjoint; no PENDING/DROPPED key is in ENV_KEYS |
Other new tests:
- `test_rigrun`: `run_problems` table (one problem at a time, failure first, the exact texts; `allow_uncapped` keeps an
  uncapped cell); `layout_override` for coarse/halves/strips4/grid; strips4 touching pairs W-C, C-E, E-NE only and
  `rigtools.load_plan(strips4, 250)` = W 63, C 63, E 62, NE 62; `check_env` (unknown key, pending key, all known ok).
  `start_problems`, `host_context`, `build_id`, `cell_lines`, `run_window`, `cell_window` are I/O wrappers: covered by
  the re-grade and the dry run, not unit-tested (stated exception).
- `test_abload`: `build_parser` defaults (every new flag defaults to today's behaviour); `--rates 30/x` refused with
  rates_setting's message; `--rates u30/0.02` refused with abload's own "runs uncapped: every run would fail the uncapped
  guard" message; `--band` with `--bands` refused.
- `test_abgrade`: `ab_points`; `ab_plan` env per run (band explicit with `--bands`, puppets, rates, INSTR, check_env);
  `grade_points` (existing pairs give today's text for a synthetic record set; strips4/halves verdict and overall line;
  missing partner skipped; monster WARNING; no strips4 group → today's exit rule); `run_order`.
- `test_p0base`: `t975`; `at_most`/`at_least`/`above` at the EPS edges; `ci_verdict`, `kill_verdict`,
  `point_verdict_eps` boundary cases.
- `test_p0plans`: every plan's run count (canary 2, rates 48, presence 66, density 53, floor 12, accept_plan part E 6
  specs, abgeo invocations 4 with 84 abload runs); every spec passes `check_env`; each presence/density arm's env and
  points exactly as the §H tables (steered 100, 200, 250; additivity kinematic 200, 400 and steered 100, 200);
  `plan_key` stable and distinct; `third_repeat` (11 % → added, 9 % → not; 599/601 against 600 → added; the added spec's
  `replaces_repeat` is the run further from the median); `predict_load` (250 steered owners → 11.4 ms/frame; 400 steered
  → "[predicted saturated]"; 400 kinematic owners → 4.7 ms/frame, not flagged); `dry_run_lines` header/footer;
  STAGE_LATER refusals.
- `test_p0report`: `build_report` on synthetic rows: fits in the stated order (density before presence; presence gains
  Q only when density's q interval excludes 0); a G = 2 fit prints both intervals, the second labelled "runs as groups";
  earlier fit/summary/gate rows in the input are ignored; output file names per §G.
- `test_imports`: `python -B -c "import X"` in a fresh subprocess for every module, in dependency order and in reverse.
- `test_abregrade`: `parse_report` refuses another text; a report whose third line is `=== phase 0: …` raises
  SkipReport (skipped, not replayed); `sessions` refuses too few Own lines; **the five fixture reports regrade with no
  difference and exit code 0**. The fixture is located from `os.path.dirname(os.path.abspath(__file__))`; inside the
  repository (the test file is at `<regress.ROOT>/Tools/ServerMeshing`) a missing fixture is a failure, not a skip;
  outside it (the add-on's `TestMap/Tools` snapshot, which `ServerMeshPackageBuilder.cs:148` fills with `.py`/`.md`
  only) the test skips with "the chain3 fixture ships only in the repository".

## L. Acceptance (exact)
1. Before editing (after confirming no chain or regress process runs, Rules → Timing): copy `Tools/ServerMeshing/abload.py` to `...\scratchpad\step4_impl\abload_before.py`;
   `python -B "...\scratchpad\step4_survey\regrade_reference.py"` prints IDENTICAL for all ten saved reports.
2. `python -B -m unittest discover -s Tools/ServerMeshing -p "test_*.py"` passes (test_rigtools, test_p0tools,
   test_provision and every new test file; baseline today is 66 OK: test_rigtools 38, test_p0tools 12, test_provision
   16); `ruff check Tools/ServerMeshing` is clean.
3. The move is textual: a scratch script imports `abload_before` and `rigrun` and compares `inspect.getsource` of Report,
   now, layout_override, run_window, cell_window, run_problems, cpu_name (and the moved constants' source lines): identical
   except `run_problems`' `def` line and its uncapped guard line.
4. **Re-grade to the same verdicts:** `python -B Tools/ServerMeshing/abregrade.py replay --logs
   Tools/ServerMeshing/fixtures/abload_chain3_20260925` (no report arguments: every report in the fixture)
   prints `IDENTICAL, exit 0` for all five (band 20: `halves/coarse = 0.55 0.60 0.62 … -> GO`, go/no-go GO; band 40:
   `0.60 0.64 … -> GO`, GO; crowd 0,0,60: `0.95 1.02`, no go/no-go; crowd 0,0,150: `no usable coarse run`, no go/no-go;
   grid repro: no go/no-go); `python -B Tools/ServerMeshing/abregrade.py replay` (all ten saved reports against `Logs/`, while
   those logs exist) prints IDENTICAL ten times. (The scratchpad reference builds its own Namespace without the new
   flags, so it is only valid up to implementation step 2 below; from step 3 on, abregrade is the check.)
5. `python -B Tools/ServerMeshing/p0load.py <cmd> --dry-run` for canary, canary --baseline, accept-a, rates, presence,
   density, floor, abgeo (with `--without-predictions`), report prints every run's plan and set environment with the
   counts of §H and exits 0; `p0load.py fanout` exits 2 with "stage 0b, steps 5-6"; `presence --observed` exits 2;
   `abload.py ab --layouts coarse,halves,strips4 --bands 20,40,80,150 --players 250 --monsters all --puppets kinematic
   --rates 60/0.03 --instrument --order repeat-major --dry-run` prints 36 runs (the b9-bands argv; without
   `--players 250 --monsters all`, abload's defaults 150,250 and off,all make it 144 runs, which is also correct);
   `abload.py ab --rates u30/0.02 --dry-run` exits 1 naming the refusal
  (new argument refusals use today's `raise SystemExit(message)`, exit 1, like the unknown-layout error).
6. No file outside `Tools/ServerMeshing/` changed; `regress.py` and `rigtools.py` unchanged (hash before/after).

## M. Rig time (E; 126 s per 45+60 run and 90 s per 25+40 run, from the 2026-09-26 A/B log's 121-126 s spacing)
| Block | Runs | Time |
|---|---|---|
| a1 accept-a (attended) | 15 (A) + 4 (B) + 6 (E, 25 + 40 s) + regress 4 sets + tests | ~109 min |
| a2 canary baseline | 2 (+1) | 4-6 min |
| a3 rates | 48 (3 repeats, no third repeats) | 101 min |
| a4 presence | 66 | 139 min |
| a6 density | 53 (+ ≤ 1, the bridge) | 111 min |
| a7 floor | 12 (+ ≤ 6) | 25 min |
| a8 abgeo | 84 | 176 min |
| canaries | ~11 (chain 1: a3 end, a4 mid-run and end, a7 end; chain 2: a6 start and end, abgeo's four leading canaries and its end canary) | 23 min |
Measurement total about 9.7 h (about 11.6 h with 20 % re-runs; the sheet's §2 budget is 10.8 h / 13 h), plus a1 ≈ 1.8 h.
Chain 1: a2, a3, a4, a7 (≈ 4.6 h); chain 2: a6, `report --predict abgeo`, a8 (≈ 5.0 h). Not included: any rebuild
(+ a1 again + a bridge canary), the Development-build profiler runs (none exist in 0a).

## N. Implementation order
1. Confirm no chain or regress process runs (Rules → Timing); acceptance step 1 (baseline copy and reference re-grade).
2. `rigrun.py` + abload imports only; re-run the reference re-grade (10 IDENTICAL) and the source diff.
3. `abregrade.py`, the fixture, `test_abregrade.py`; re-grade IDENTICAL from the fixture.
4. `p0base` → `p0tools` additions → `p0cells` → `p0checks` → `p0geo` → `p0model` → `p0results`, each with its test file,
   failure cases first; `test_imports.py`.
5. `abgrade.py` (abload's pure plan/grading moved and generalised) + `test_abgrade.py`; abload's new flags, strips4,
   JSONL, dry-run + `test_abload.py`; re-grade still IDENTICAL.
6. `p0plans.py` + `test_p0plans.py`, `p0report.py` + `test_p0report.py`, then `p0load.py`; the dry runs of acceptance
   step 5.
7. ruff, the full unittest discover, acceptance steps 2-6; report what changed, the decisions above, and anything that
   did not match this contract.

## Critic changes (2026-09-27)
Every finding was checked against the sheet, the harness, the saved logs and the critic scripts (check_a/b/c re-run;
one extra simulation for A3's merged group). Applied:
- A1: Saturation made one rule everywhere: per read cell, exclude on span saturation or ≥ 2 saturated windows; a single saturated window is dropped from that cell's aggregate and listed in the new run-row field `dropped_windows`, never in `problems[]` (Decision 18, §C cell_record, §D exclusion, §G, §K). Decision 18's evidence corrected to run sat 3 % (the 12 % was repeat 1).
- A2: Block K raised to 3 repeats (48 runs, +34 min; Decision 15, §H, §I, §K, §M); G = 2 fits (floor) print the delete-one-run interval beside the t(1) interval, labelled, information only. Skipped part: "G = 2 → NO VERDICT" — a t(1) interval keeps nominal coverage (check_b: 0.953), it is only wide, so a verdict it gives is valid.
- A3: Balanced jackknife groups (§E): a third-repeat run joins its `replaces_repeat` group (simulated coverage 0.949 against 0.904 as its own group); unbalanced user plans fall back to delete-one-run groups named in `method`. To keep every fit-feeding arm balanced, density's scale 50 @ 70.71 and monsters-on arms go from 2 to 3 repeats (53 runs, +4 min).
- A4: split_inputs formula matches its worked numbers: V = trigger − c0 − λ·parent_links, x = (c0 + λ·parent_links)/V, link_share = λ·links_gained/V; parent_links 1 case added (§F, §K).
- A5: c0/λ at another rate set come from a7's idle rows (`floor`) or block K's idle F/Ph, never per-frame scaling; refusal added; 18/782 replaced by 23.2/776.8 = 0.0299 (§F, Sheet corrections). Modified: a7 measures idle and linked idle at both rate sets directly, so it is the first source, convert_rates the second.
- A6: strips4/halves thresholds recomputed: k_band ≤ 0.675 without links, ≤ 0.600 with links (11.85 ms/frame from §H's costs); stricter than §4.3's 125 m rule (k ≤ 0.742), stated (Sheet corrections).
- A7: Row selection for n2_form / fit("density") (grid, scale base only), attribution/sparse to n2_removed only, bridge to its ratio only; refit takes base rows + monsters-on; work_rows refuses uncapped rows and no-fight rows without an `O_steered_nofight` regressor (§E, §H).
- A8: Report fits density/N² before presence; presence spec gains optional Q (n2_form's form) when density's q interval excludes 0 (§E, §H report).
- A9: `p0results.FIT_NAMES` maps regressors to §5 names (c0, a_kind, e_kind, lambda, q, k_kind = k_hold_kind); the a_steered reference listed as a sheet correction (Decision 19, 21, §E, §G, Sheet corrections).
- A10: G-GEO (c) compares the measured band slope with the slope of predict_ratio(halves, coarse) at the four bands with monsters included and the coarse run's own x (§E gate table); layout_loads takes measured per-cell monster counts (strips4 estimated from grid counts by area overlap; pre-a8 counts parsed from the fixture's chain3 reports) (§F).
- A11: map_size_table gains link_share, c0, lam, load, passed through to break_even_width/halving_ratio/total_cpu_ratio; the report passes them from split_inputs (which now also returns V) and the floor fit (§F, §H).
- A12: `point_verdict_eps` (p0base) for the report's G-GEO (a); abload's text keeps rigtools.point_verdict; boundary case [2.1/2.8, 0.74] → GO (§E, §K).
- A13: Kinematic puppet cost 0.0047 ms/frame (0.28/60); Decision 12's threshold restated on a + g with λ ((15.833 − 0.6 − 0.12)/400 = 0.0378); Decision 12 no longer claims a3-driven predictions (merged with B3/B7). Skipped part: `predict_load(spec, rate_rows)` from a3 rows — step 3's measured owner costs (B7) already put the 400-point arms at util ≈ 0.28-0.43, so the extra plumbing changes no decision.
- A14: third_repeat also triggers when two runs straddle a block threshold (floor 600 ms/s; util 0.95, sat 20 %) (§H, §K).
- A15: min_sine is the order-invariant min_j 1/sqrt([(XsᵀXs)⁻¹]_jj); §K expects 0.308607 under every name order (§E, §K).
- A16: t975 above df 120 returns 1.980, not 1.960 (§E, §K).
- A17: map-form link count is 0 at m = 1 (§F, §K).
- A18: aggregate field renamed `work_ms_per_measured_frame`; the bridge ratio uses it (§C, §H).
- A19: §4.6-vs-§4.7 base-cell discrepancy at k ≤ 0.7 listed as a sheet correction; the table follows §4.6 (Sheet corrections, §F).
- A20: rate_split is one joint fit over both arms with arm-indicator interactions, jackknifed over repeat (§E, §K).
- A21: additivity rows never enter the presence fit; additivity_residual uses the fit made without them (Decision 13, §E, §H).
- A22 + B6: check_rates with rates None requires NO Rates line and skips check_frames; such rows carry fps/fixed_step_s null (§D, §G, §H, §K).
- A23: Decision 21 lists `point`, `logical`, `layout`, `dropped_windows` and per-point/cell summary rows; `REQUIRED` per row type; `order` = 1-based position in the block; file names `rig_<game>_fit_<stamp>.jsonl` and `rig_fantasy_abgeo-prediction_<stamp>.jsonl`; the report fits run rows only (§G, §H, §I).
- A24: gate rows state thresholds in ms/s and ms/frame at the stated rate (§E, §G, §K).
- A25: Decision 14 declares the N² gate density (100 m² per character, not the grid's densest point) and the added scale point 50 @ 70.71 m; gate table annotated.
- A26: the histogram gate's one-bin conservative bias stated (§E).
- A27: percentile_us must not copy the prototype's in-bin clamp; two §K cases (§C, §K).
- B1: accept-a part E (6 attended first-use runs, +9 min) for 30/0.03, 60/0.02, MESH_UNCAPPED_CELLS, MESH_LOAD_FIGHT=0, MESH_RIG_SERVER_ANIMATION=0, MESH_BAND=300, graded via a new spec field `checks_family` (§D, §H, §I, §K, §M).
- B2: Decision 12's reason for the 250 top corrected (sat-share rule, not util; 0.71's 250 runs were sat 0-1 %); steered points (100, 200, 250) so steered arms share N = 200 with animation-off and additivity (Decision 12, §H, §K).
- B3: additivity restored to the sheet's 200/200 and 400/400 for the kinematic pairing (≈ 6.6 ms/frame), 100/100 and 200/200 for the steered pairing, from step 3's saved C logs (kinematic 0.0103, inert 0.0164 ms/frame, re-derived here from the 2026-09-26 14:15-14:22 windows); `--additivity-points` fallback removed (Decision 12, 13, §H).
- B4: new leaf `p0base.py` breaks the p0geo ↔ p0model cycle; the import graph rewritten with the missing edges (p0results → p0cells, p0plans → p0geo); `test_imports.py` (Rules, Files, §E, §F). Modified: abgrade imports only rigrun, rigtools, p0plans (JSONL writing stays in abload), not p0checks/p0results.
- B5: the ~500-line split is part of the plan: `p0cells.py`, `abgrade.py`, `p0report.py` (a pure `build_report` with its own test file) (Rules, Decision 2, Files, §B, §C, §H, §I, §K, §N).
- B7: predict_load constants corrected (measured kinematic/inert owner costs, kinematic puppet 0.0047, flag at util 0.85); §K cases (§H, §K).
- B8: abregrade skips reports whose third line is `=== phase 0:` (SkipReport); the exit code counts only replayed reports (§J, §K).
- B9: test_abregrade skips outside the repository (the add-on snapshot ships .py/.md only, ServerMeshPackageBuilder.cs:148) and still fails inside it on a missing fixture; the fixture writer omits empty files (§J, §K, Files).
- B10: abregrade CLI uses explicit `replay` and `fixture` subcommands; §L.4 commands updated (§J, §L).
- B11: test baseline is 66 OK (test_rigtools 38, test_p0tools 12, test_provision 16) (§L).
- B12: the CHAIN7 DONE gate replaced: chain7 finished 2026-09-26 15:29; check for running chain/regress processes instead (Rules, §L, §N).
- B13: canaries ~11 (23 min); §M totals recomputed with the other changes: measurement ≈ 9.7 h (≈ 11.6 h with re-runs), chain 1 ≈ 4.6 h, chain 2 ≈ 5.0 h, a1 ≈ 1.8 h (§M).

Skipped findings: none in full. Partial skips (reasons above): A2's "G = 2 → NO VERDICT" rule; A13's `rate_rows` option; B4's abgrade → p0checks/p0results edges.

## Review changes (2026-09-28)
Four review lenses (stats, plans, io, outputs) each re-derived the tools; every finding was verified and CONFIRMED, and
all were applied, each with a failing test first:
- S1 `p0model.run_groups`: lost runs keep delete-one-repeat groups (fall back to run groups only when a point has a
  repeat outside the most common set). S2 `p0geo.choose_base(side, w_star, band, max_base)`: no base or s_min below B;
  side < B, an unmeetable max_base and an explicit base < B are refused. S3 `p0rates.rate_decomposition`: the
  interaction is its own jackknife and optional (`full["notes"]`), so a lost rate cell keeps F, Ph, C, S and the
  per-entity splits. S4 `additivity_residual(..., bridged)` threaded from the report.
- F1/IO-1 third repeats over the whole plan and this build's rows, decided again after a drifted canary, never
  requeued; F2 `rates` in `p0blocks.THIRD` and `THIRD_REPEAT_THRESHOLDS`; F3 `block_order` offsets (r − 1)·A // R
  (floor's repeat 2 order is now cell/players-150, linked/idle, cell/idle); F5 bridge-0.53 expects 8 links.
- IO-3 resumed blocks requeue the earlier invocation's runs after the last good canary on a drift, and the verdict
  counts every plan key; IO-4 the start canary is skipped only when the newest canary on the build passed; IO-5
  abregrade replays across midnight; IO-6 `accept-a --resume` refused (exit 2).
- F4/IO-2 `p0report.latest_per_plan_key`: one accepted row per (build, plan_key) in summaries, fits and gates (the
  Markdown names how many were left out); R1 only team rig rows feed fits and gates (others summarised into
  `<team>_<game>_fit_<stamp>.jsonl`), `report --build SHA` reports one rig build, and the two-build refusal advises
  `--bridge` or `--build`; R2 G-GEO (b) judges the newest prediction set made before the first abgeo run (NO VERDICT
  when every set is later) and names predicted points without an accepted pair (PASS then INSUFFICIENT).
Not applied: the optional parts (an abload `--skip-keys` for resumed abgeo sub-blocks, and `report --predict abgeo`
refusing once abgeo rows exist); the report's dedup and the frozen-set choice already make their outputs correct.

## Questions for the user
1. **Saturation (Decision 18):** a run with ONE saturated window on a cell keeps its other windows (that window dropped and listed in `dropped_windows`, not a problem); 2 or more saturated windows on a cell, or a saturated span, exclude the run. Accept this, or should any saturated window exclude the whole run?
2. **Block K (a3) repeats:** the contract now plans 3 repeats (48 runs, +34 min) because 2 repeats leave Ph and C unresolved (t 12.706). Keep 3, or go back to 2 and rely on the delete-one-run interval (which assumes no drift between repeats)?
3. **The k reference:** G3-F, the kill rule and the split model use k = e_kind/a_steered (the production owner). Sheet §4.1 literally reads k_kind·a_kind (e_kind/a_kind would be about 4× larger for kinematic puppets, because the rig's kinematic owners are cheap stand-ins). Accept the a_steered reading as a sheet correction?
4. **N² gate density:** judge it at 100 m² per character (200 @ 141.42 m, the sheet's own attribution density), or must the removal arms move to the grid's densest point (200 @ 100 m, 50 m²) as §3's wording says (if that point does not saturate at 60/0.03)?
5. **G-GEO (c) with `--monsters all`:** compare the measured band slope with the monster-inclusive predicted slope (what the contract now does), or run b9-bands with monsters off so the characters-only k_band/(1+x) applies directly?

(The second critic's questions on presence steered points, the additivity points, accept-a part E and the shipped test_abregrade were resolved by applying B2, B3, B1 and B9: each is backed by saved data or packaging code and changes no gate's meaning.)

## Amendment (2026-10-01): the Canary drift gate
A drifted canary already excludes the runs before it (p0results.apply_drift), and p0load queues them again, so a drift
does not reach the fits. The gate therefore FAILs only while a point that lost runs to a drift has no accepted run
since (listed in its notes); drifts that cost no kept run (an aborted block's start canaries, manual check canaries)
are notes. NO VERDICT without a judged check canary, as before. Tests: test_p0report.test_canary_drift (unchanged) and
test_canary_drift_counts_only_points_left_unmeasured (a drift before any run; excluded points re-measured).
