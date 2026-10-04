# Step 8 survey: the p0load framework (read-only, 2026-10-04, machine A, commit 0f81895)

Paths are relative to `Tools/ServerMeshing/`.

## 1. How a block family is defined end to end
- **CLI (p0load.py).** `build_parser()` adds one subparser per family with `--dry-run --resume --no-canary --warmup
  --measure` (:199-206) and one refusing subparser per `STAGE_LATER` name (:228-229). `block_plan(args)` is an if/elif
  chain (:66-80) whose final `else:` returns `p0plans.floor_plan()` (:79-80): **removing a name from `STAGE_LATER`
  without a branch here silently runs the floor plan.** Every spec then gets the warmup/measure overrides and
  `rigrun.check_env` (:81-84). `main()` (:239-266): refusals; `--resume` (`p0blocks.done_keys(history,
  rigrun.build_id())`, :256) drops done keys (:260); `--dry-run` → `dry_run()` (:114-130 → `p0dryrun.dry_run_lines`,
  plus a canary note for `CANARY_FAMILIES` and a third-repeat note); `run_command()` (:269-315) makes
  `p0blocks.Session(args, stamp, rep, rigrun.host_context(), history)` and `session.block(specs, plan)` (:306); accept-a,
  canary and abgeo have their own branches (:296-304).
- **Plan builder (p0plans.py).** A RunSpec dict (:10-15) built by `_spec(...)` (:105-133): env = layout override +
  `MESH_SPAWN_CELLS`, `MESH_LOAD_PLAN`/`SEED`, `MONSTER_ENV`, `MESH_LOAD_AREA`, `MESH_RATES`, `MESH_UNCAPPED_CELLS`,
  `MESH_BAND`, `INSTR` (:29), then the arm's keys; empty values dropped; `rigrun.check_env`; `game` from
  `GAMES[family]` (:35-36, :128; a KeyError for a new family). Run key `plan_key` = sha1 of
  `family|case|arm|json(point)|repeat`, 12 hex (:71-75). `block_order(arms, repeats)` rotates arms left by
  (r−1)·A//R (:86-95); `stepped(points, repeat)` ascending odd, descending even (:78-83); `_blocked` (:142-150).
  Third repeats `third_repeat`/`replaced_repeat` (:403-443), `THIRD_REPEAT_THRESHOLDS` (:67-68), `p0blocks.THIRD`
  (:43-44). `run_seconds` (:446-451).
- **Runner (p0blocks.py).** `run_spec` (:110-126): `rigrun.run_window(env, warmup, measure, rep)`; per cell read
  `{"structural": rigrun.cell_window(...), "p0": p0cells.cell_record(...)}`; `p0checks.run_problems`, `exclusion`;
  `p0results.run_row`; a raising run is a failed row. `Session` (:153-327): rows to
  `rig_<FAMILY_GAME[family]>_<family>_<stamp>.jsonl` (:166-167; KeyError for new families); canaries only for
  `CANARY_FAMILIES` (:37, :165), every 2 h (:38); drift requeue `checkpoint` (:225-235); `thirds` (:252-269); `verdict`
  (:302-327).
- **Reading.** `rigrun.cell_window` (:113-123), `run_problems` (:126-155). `p0cells.cell_record` (:245-275) reads only
  Work window/ticks/bins, Threads, Puppets, Rates, `MARKERS` (:52-54) and Mesh stats memory — none of the Send,
  Phantom, Sender, Ships, Feeder or Sync load lines. `cell_metrics` (:278-317) emits work, tick, thread, memory, rate,
  puppet metrics only. `window_saturated(..., fanout=False)` (:89-99) has the §3 fan-out rule
  (`FANOUT_MULTI_TICK_SHARE` :27), but `cell_record` never passes `fanout` (:257).
- **Grading (p0checks.py).** `COMMON` (:335-336), `FAMILY_CHECKS` (:337-340); `run_problems` raises for a family with
  no entry (:346-348); `exclusion` (:355-367). No phantom, sender, ship or sink check exists.
- **Row (p0results.py).** `run_row` (:153-190) refuses metric names outside `METRIC_NAMES` (:48-55, :168-170).
- **Report/fit.** `p0report.build_report` keys on 0a families (`FIT_ORDER` :38, `PENDING_GATES` :34-37, title :281);
  `p0model.ROW_SELECTION` (:29-46) and `p0gates.evaluate_gates` are 0a-only.
- **A new family must add to:** `p0plans.GAMES` + a plan builder + removal from `STAGE_LATER`; a `p0load.block_plan`
  branch and parser; `p0results.FAMILY_GAME` (and `FAMILIES_0A` or a 0b set); `p0checks.FAMILY_CHECKS`;
  `p0blocks.CANARY_FAMILIES` (and `THIRD` for 2-repeat arms); `METRIC_NAMES`; `p0dryrun.predict_load` if it predicts;
  p0report/p0model/p0gates for fits and gates.
- **Predicted load.** `p0dryrun.predict_load` (:38-68) costs owners, puppets and monsters at 60 fps only (E); it
  ignores phantoms, sender, ships and feeder, and raises for an unknown `MESH_LOAD_MOTION`/`MESH_PUPPET_MOTION`
  (:31-35).

## 2. Refusal stubs and exposed names
- `p0plans.STAGE_LATER` (:60-62): fanout, packets, prewarm, crossings, calibrate, workerab, dbburst, spaces — stage
  labels stale (fanout/packets "steps 5-6", prewarm "step 7", crossings/calibrate "step 5"). `later_refusal` (:454-459);
  `OBSERVED_REFUSAL` (:63) used by `presence --observed` (p0load.py:215, :242-244); exit 2. Pinned by
  test_p0plans.py:46-60 and test_p0report.py:471-474 (`PENDING_GATES` text tied to `STAGE_LATER`).
- Exposed: real canary, accept-a, rates, presence, density, floor, abgeo, report; refused the eight above. **Missing:**
  accept-b, targets, syncrpc, and observed as its own command. The sheet's §5 family vocabulary (:1144) lists none of
  observed, targets, syncrpc, accept-b: their row families are undecided.

## 3. Row schema mesh-p0/1 and what a 0b row needs
- Required (p0results.py:35-45): schema, team, game, model, family, case, arm, stage, repeat, seed, order,
  started_utc, **host, cpu**, cores, os, power_plan, build, kit_patches, rates, capped, accepted, problems,
  excluded_reason; load fields kind, owned, puppets, awake_monsters, clients, v_move_mean/p95, density,
  visible_range_m, band_m, payload_B, budget_kBps, scale; metrics. Kept extras: plan_key, point, window_local, logical,
  env, layout, cells_started, dropped_windows, windows, third-repeat fields.
- `host`/`cpu` exist (`CONTEXT_FIELDS` :32 from `rigrun.host_context()` rigrun.py:231-245; host = COMPUTERNAME, cpu =
  Win32_Processor.Name, `cpu_name` :158-162), taken once per invocation on the machine running p0load (p0load.py:295):
  one host/cpu per row, no second-machine field.
- 0a hard-codes: `STAGE="0a"` (:31); `_load_fields` (:134-150) kind "character", clients 0, observer_kind None,
  v_move/payload/budget/awake_monsters None, scale 1.0; `summary_row` clients 0 (:259).
- In `METRIC_NAMES` but emitted by nothing: downlink_wire_kBps_per_client, entity_state_kBps_per_client,
  datagrams_per_s_per_client, rate_hz_by_tier, reliable_records_per_s, pkt_main_us, pkt_net_us, sync_scan_ns,
  rpc_send_ns, upload_in_us, c_pair_ns, residual_ns, latency_ms.
- Missing: per-window phantom/sender/ship/sink data; second-machine labels (sink_host/cpu, bots_host); a step index.
- **Pooling risk.** `p0model._check_pool` refuses mixed cpu and builds (p0model.py:247-256), but `summary_rows` groups
  by (game, family, case, arm, point, rates) with no cpu/host (p0results.py:271-272); `header_of` joins cpus with "+"
  (:201-213); `canary_baselines` (:399-406), `apply_drift` (:451), `bridge_ok`, `done_keys` (p0blocks :88-91) and
  `canary_state` (:94-107) key on **build only**. Same build on A and B → A's canary judged against B's baseline (or a
  mean), and `--resume` skips A's runs because B has rows.

## 4. b6 stepping inside one Play
- `run_window` (rigrun.py:83-110): one Play, one [start, end], no hooks (step5_design.md:371; acceptance scripts drive
  Play themselves or run it on a thread, phase0/accept_step7_sink.py:43-60).
- `MESH_PHANTOM_STEPS=<P1,..,Pn>@<s>` (p0tools.py:62): eye kind only (step5_design.md:113, :198), every Pi ≤ P; the
  hold is timed from when each step is reached, so boundaries are not fixed offsets; each Phantom window logs
  `step i/n target T` (p0phantom.parse_phantom_window :63-76, :222-239).
- Workable (inference): one run_window with measure ≈ Σ holds; read once; derive per-step sub-windows from the
  Phantom step field; `cell_record` per sub-window (readers are pure over (lines, start, end)). Not fitting today:
  `check_coverage`/`run_problems` compare against `spec["measure"]` (p0checks.py:111-131; rigrun.py:149); one point per
  run/key (needs a step field or per-step keys); up/down per repeat = reverse STEPS by repeat; Phantom↔Work alignment by
  log stamp (step5_design.md:463, :485); fan-out saturation not wired.

## 5. Parsers
- Exist: Send window `p0phantom.parse_send_window` (:160) + `send_window_problems` (:190); Phantom window (:222); Sink
  window (:242; B's local stamps); Bot net window (:261; `end` None, UTC `at`); `p0sender.parse_sender_window` (:65),
  `parse_ce12_check` (:88); `p0ships.parse_ships_window` (:140), `parse_ships_settings` (:162),
  `parse_feeder_window` (:170), `parse_sync_load_window` (:178), `parse_bot_ships` (:184; `end` None); grammar mirrors
  (phantom_setting, phantom_area_setting, meter_sends_setting, sender_setting, sender_tiers, ship_load_plan,
  puppet_feeder, sync_load, bot_argument).
- Missing: **Prewarm** (no parser; no C# matches Prewarm/PREWARM/PARK_SLEEP; both keys still `PENDING_KEYS` "stage 0b,
  step 8", p0tools.py:86-89, so `check_env` refuses plans that set them, rigrun.py:190-209). A parser for the cell's
  `Phantoms: cell C port N` line (accept_step7_sink.py:54 uses an inline regex). step6_design.md:251 "step 8 adds
  Prewarm and Feeder" is half stale (Feeder exists).
- **None of these parsers is wired into p0cells, p0checks or p0results**; only the phase0/accept_step*.py scripts use
  them.

## 6. Single-machine assumptions
- `run_window` calls `r.kill_bots()` first (rigrun.py:86): bots must start after Play (a hook). Leftover/survivor
  counts are local by EXE path (regress.py:193, :234-239, `stop_play`); nothing cleans up B's sink or bots.
- `regress.launch_bot` (:242-248): local EXE, `-meshChannel C`, logs to `Logs/regress_<scenario>_<user>.log`; no remote
  bot launcher; remoteb has sink-start/stop (remoteb.py:15-18, :269-301) but no bots; twohost has up/walk/status/down
  (twohost.py:1-15).
- `db_reset` provisions only meshtest and meshbot (regress.py:458-477); meshbot2..N self-register on first login
  (provision.py:15).
- `zone_lines` reads `Logs/` or `Logs/remote/` only for remote cells in `REMOTE_CELLS` (regress.py:133-135, :540-545);
  sink.log is fetched separately (accept_step7_sink.py:69-70). The run window is A's local time; B's lines carry B's
  clock with no skew handling. `select_windows` raises for unstamped windows (p0cells.py:83-85), so Bot net and Bot
  ships lines cannot go through it.
- Fixed addresses: remoteb defaults (remoteb.py:32-38); accept_step7_sink.py hard-codes the SSH target (:24-25);
  `RIG_PORTS` preflight is local (regress.py:198-227).
- `host_context` and the report header are local only (rigrun.py:231-245; p0load.py:290); baselines, drift, resume key
  on build, not host/cpu.

## Open decisions for the contract
- Row families for observed, targets, syncrpc and accept-b (§5 lists none).
- How a two-machine row labels its second machine.
- Whether summaries, canary baselines, drift and resume split by host/cpu (today build only; they can pool A with B).
