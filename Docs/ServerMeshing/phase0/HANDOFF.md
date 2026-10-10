# Dynamic server meshing: handoff to machine A (2026-10-04; status updated on A, last 2026-10-06)

Until now the work ran from machine B (the laptop, `192.168.50.211`, Ryzen AI 9 HX 370), which also hosted the Unity
editor and every measurement. From here machine A (the desktop, `192.168.50.83`) takes over: it runs the editor and
the harness, and drives B over SSH (`Tools/ServerMeshing/remoteb.py`) whenever a test needs a second machine. The space
team works on A too. Until 2026-10-05 they had no live session, so ship questions were answered from their repo
(`D:\Game Development\mmorpg-kit-space`) and their plan, and the user decided; since 2026-10-05 the user has this work
act for the space team as well (record: `space_reply_2026-10-05.md`).

Read this file first, then the approved plan (the user provides it), then `phase0_build_sheet.md`.

## 1. Where the plan stands

The plan: runtime split/merge of cells, chosen per map (Static = today's pre-split, the default; Dynamic), built in
slices 0-6. On 2026-09-30 the user decided to finish the **whole shared Phase 0** (measurements for the fantasy game
and the space game) before slices 2b-6.

| Part | Status |
|---|---|
| Slice 0: prerequisites, fixes, harness | Done |
| Slice 1: measurement, go/no-go | Done: GO (halves/coarse 0.67-0.74) |
| Slice 2a: per-map mode, overlay, distribution, gate (no ops) | Done, rig-verified (DESIGN 0.72) |
| **Phase 0** | **Step 8a built and measuring on A; 8b and 9 to do** (below) |
| Slice 2b manual split, 2c merge/drain/retire, 2d supervision/fencing, 3 automatic policy, 4 buildings (CE-10), 5 central restart, 6 operator path | After Phase 0 |

### Phase 0 (spec: `phase0_build_sheet.md`; per-step contracts `stepN_design.md`)

- **Stage 0a, done (DESIGN 0.73, `stage0a_report.md`).** Step 1 contract file (`Mesh/Sync/MeshSyncContracts.cs`),
  step 2 measuring instrument, step 3 kinematic puppets, step 4 Python tools, then the measurement chains. Headlines:
  a kinematic puppet costs about a seventh of a steered one (k_band 0.174 against 0.93); G3-F GO; halves/coarse
  0.57-0.60 GO; strips4/halves 0.57-0.69 GO (steered controls NO-GO); G-GEO (b) FAIL narrowly (0.058 against 0.05),
  (c) PASS; W* about 57 m kinematic, 307 m steered; s_min 125 m at an 80 m band on a 1 km ladder. **All stage 0a numbers
  are machine B's CPU** (Ryzen AI 9 HX 370, 60 fps, 0.03 s physics).
- **Step 5, done.** Metered transport, send meter, phantom clients (character, eye), the sink on B. Virtual
  acceptance passed on B; the two-machine sink acceptance passed when driven from A (9/9, -0.04 % bytes).
- **Step 6, done (2026-10-03).** The state sender (`Mesh/Sync/MeshStateSender*.cs`), kit adapter, CE-12 check.
  `accept_step6.py` passed in substance; regress gates default, `MESH_SENDER=on` and on+kinematic all 11/11.
- **Step 7, done (A, 2026-10-04).** Verified on A, re-modelled on the space code at c2eb4154 as **step 7b**
  (`step7b_design.md`; commit 2d420ee), the b1 sink arm passed on B (`phase0/accept_step7_sink.py`, fix 0f81895). As built
  on B: Ship stand-ins modelled on the space game
  (`TestMap/Ships/*`, prefab `TestMap/Prefabs/ServerMeshShip.prefab`), the `ship` phantom kind, the in-process puppet
  feeder, sync/RPC load, record-size counting in the shipped send meter (`MESH_METER_RECORD_SIZES`), and a small
  shipped hook `IMeshSenderOwnedSource`. EditMode 2035 pass / 0 fail (+1 deliberately ignored compact golden), Python
  434 OK. `accept_step7.py`: every group passes on B (record sizes exactly 54/56/28 B with 5 B framing; once per
  build; bots see ships; AI patrol 1.01 m/s, top speed exactly 3.76 m/s at scale 0.03; pilots; mirror ships through
  phantoms; feeder 4,000 snapshots/s; sync load).
- **Step 8 is split (user, 2026-10-04).** **8a, built and accepted on A** (`step8a_design.md`, commit 3d52e53): (build,
  host) machine keying, stage 0b rows and readers, stepped runs, p0load `observed`, `fanout`, `targets`, `syncrpc`,
  `packets`, `prewarm`, `p0geom.py`, and the rig keys `MESH_MONSTER_PLACE`, `MESH_PREWARM`, `MESH_SHIP_PARK_SLEEP`;
  `phase0/accept_step8a.py` passes. **The 8a blocks b3-b8 are measured on A** (observed, targets, syncrpc, packets,
  prewarm on build 3d52e53 under host `BRENDOAIBOX`; fanout under `BRENDOAIBOX#2026-10-06b`, section 3). Open from
  them: S-stress does not fit on A, and 17 fanout step rows (sender arms) were rejected by a false tick miss: a probe
  window one 30 fps frame over 5 s (5.03 s) expects 101 ticks, runs 100 and prints "1 dropped" by arithmetic, with no tick-cap frame. The check now
  counts only a count off by more than 1 or a cap frame (`p0checks.tick_misses`); the 17 runs were re-measured
  (2026-10-07, build 3867cc0) and all those steps passed. The packets saturation probe is moot on virtual phantoms
  (no sockets: 0 datagrams), so the network-thread probe moves to 8b's sink arms. Draft: `stage0b_8a_results.md`.
  **8b, built (715260e) and measured with B (2026-10-08/09); results in `stage0b_8a_results.md` §5:** gate J PASS at 50
  and 100 bots; Crossings half A not decided (handoff_ready p95 1.6-2.0 s with 50 bots, but B's 50 clients ran at
  91 % CPU and latency tracks B's CPU; the servers add ~100-230 ms); ~5 µs of network thread per datagram, not
  saturated at 47,000/s; accept-b part F (meter overhead) marginal at 10 pairs (0.25 %, upper bound 1.07 %). B hosts
  100 bots (`Logs/p0/bots_calibration.json`). Contract `step8b_design.md` (rev 1, two critiques). Bots on B
  log in to A with `-meshLoginAddress` (`TestMap/ServerMeshLoginAddress.cs`); `remoteb.py` bots-start/stop/count/fetch
  (`remotebots.py`, `remotessh.py`, `botpoints.py`); bots are `meshbot2` and up (meshbot stays the regress fixture),
  registered by `p0load register-bots` and placed by `provision.reset_bot_positions`; `p0peer.PeerSession` starts and
  stops B's bots and sink around each run and refuses to start while anything of ours is left on B; new subcommands
  `accept-b` (19 runs), `calibrate` (4), `crossings` (18), `jfull` (4, only if `calibrate-bots` finds B hosts 100
  bots), `packets --sink` (22), `register-bots`, `calibrate-bots`. Ship stand-ins now follow the space game's main
  (IsAtRest with the angular and pending terms; unowned ships acknowledge a teleport at once). Before a B session the
  user stops Relics of Imperium and `RelicTestServer` on B; each measuring chain takes its own baseline under a new
  epoch (`Logs/p0/MACHINE_EPOCH`; 8b used `2026-10-06b` and `2026-10-08n`).
- **Step 9 (stage 0c), not started:** database burst (the user permits creating test accounts), fantasy worker A/B,
  the isolated-spaces floor (the space game's bubbles), loss.
- After Phase 0: the DESIGN 0.74 row (one stage-level row for stage 0b, like 0.73), then slices 2b-6.

## 2. Moving the measurements to A

Stage 0a's rows, baselines and fitted constants are B's hardware. Before any new measurement block on A:
`python Tools/ServerMeshing/p0load.py canary --baseline` on A (A's own baseline; B's was 104.0 ms/s on the old build,
about 107 on the new), and `accept-a` on A. Never pool rows from the two machines; label every number with its CPU. If a
result must be compared with stage 0a, either re-run the needed stage 0a points on A or state that the comparison
crosses machines.

## 3. Step 7: what was left at handoff (all done on A, 2026-10-04)

1. **Verify on A.** Pull, let Unity import, then: `python Tools/ServerMeshing/regress.py --refresh --unit --build
   --package` (EditMode 2035/0, player and both packages rebuilt; the package build runs the stray-reference check).
   The ship prefab is committed; do NOT rebuild it (its asset id is its own .meta GUID). Then
   `python Tools/ServerMeshing/phase0/accept_step7.py` (all groups, about 50 min) and the regress gates (below).
2. **The two-machine sink arm (b1):** `remoteb.py deploy`, then `remoteb.py sink-start --sink-sizes` with ship phantoms
   (`sink:20:ship:C`, `MESH_SHIP_WIRE=header56`): the Sink window's sizes must read `min 56 max 56`. See
   `step7_design.md` "Acceptance" run 2 and `step5_design.md` for the sink setup.
3. **Space team questions:** `step7_space_questions.md` (compact size 28 vs ~26 B and the indicator bit, header bit
   order and flightState values, zero velocities for player ships, the reliable-forever teleport rule, compact uploads,
   server bolts, which hostile prefab). Answers may change `TestMap/Ships/ServerMeshShipWire.cs` (the compact layout is
   rig-only until they decide; its golden test is `[Ignore]`d) and the stand-in defaults.

On A (Ryzen AI MAX+ 395, a Corsair AI Workstation 300, BRENDOAIBOX): accept-a passes (thread CPU counts cycles at
2.995 GHz; ruff is installed and pinned by `Tools/ServerMeshing/ruff.toml`; `p0load` runs it as `python -m ruff`).
A's canary needs a fixed machine state, and three were found the hard way:
- **Power plan.** Under Balanced the canary swung ±9 %; A now measures on a user-created **High performance** plan
  (rows record `power_plan`). The Corsair performance mode stays on its top setting. Balanced-era rows of 3d52e53 are
  set aside in `Logs/p0/balanced_3d52e53/`; baselines taken while the machine was unsettled are in `Logs/p0/retired/`.
- **Machine epochs.** The 2026-10-06 reboot installed Windows build 26300 and the canary moved (68.6 → about 75 ms/s
  idle). Rows are keyed on `host = COMPUTERNAME#<epoch>` when `Logs/p0/MACHINE_EPOCH` names one
  (`rigrun.machine_host`; `p0load report --host NAME`); start a new epoch and baseline whenever the canary settles at a
  new level. Epochs so far: none (`BRENDOAIBOX`, baseline 68.9/68.4 on 3d52e53), `2026-10-06` (its 80.7/80.8 baseline
  was taken while A was in use; set aside), `2026-10-06b` (74.5/75.4; the fanout block's canaries all within 1 %).
  Comparing across epochs states the ratio (about 1.09 from the first to `2026-10-06b`).
- **Leave A idle while it measures.** Interactive use of A during a run (the Claude app streaming a reply, the user
  working over RDP) reads 80-95 against an idle 75. Stay attached but idle (section 4), watch a chain only for its
  end, never wake on a drift line (that spoiled a repeat canary once), and keep Lemonade Server stopped.

Findings for the next session: F2 (100 fighting phantoms + 100 monsters) saturates cell C at 60 fps on A; 100 kinematic
ghosts pre-warmed at once cost about 2 ms each and a 218 ms frame; parked stand-ins sleep unless pinned (the space
game's ships never do); the space game's standing AI (`Ship_Enemy`) stays reliable forever after a respawn (3.6
reliable records/s to 8 subscribers in a 100-AI fight). B also runs "Relics of Imperium" (UDP 6010, 6100, 7000, 8000,
18000) and `RelicTestServer`; the user stops them before two-machine measurements.

## 4. Rules that bind every step (the user's, plus what the project learned)

- Never commit or push unless the user asks. Never print or open `Config/mySqlConfig.json` (or the build's copy).
- Kit edits (`Assets/OpenMMORPG`) only as numbered CE patches with `MESH-EDIT: CE-n` markers, a DESIGN §17 row and an
  upstream patch in `Docs/ServerMeshing/upstream/`. CE-11 (0005), CE-12 (0006) and CE-13 (0007) are merged upstream
  (open-mmorpg/OpenMMORPG); the add-on now requires a kit with 0006.
- `Mesh/` ships to games: it never names a `TestMap`/`Tests` type nor uses the word "ship"; game-neutral `Mesh*` names.
  `phase0/stray_integ.py` and the package build check this.
- Default off and byte-identical: with no new env key set, behaviour and log lines must not change. Log lines have no
  braces and never the word "Exception". Pure code logs through `IMeshLog`.
- Never `AddComponent` an editor-assembly MonoBehaviour in a test (Unity returns null). Files under about 500 lines.
- Env keys are registered in `Tools/ServerMeshing/p0tools.py` (ENV_REGISTRY, counts pinned by `test_p0tools.py`) and
  `regress.py` ENV_KEYS. Bots get command-line arguments, not env.
- Write to the database only while the editor is out of Play. The provisioner writes only rig accounts.
- **Measurements:** no agents or workflows while a rig chain runs (they perturbed the canary by 6-10 %); keep the
  user's session attached (a disconnected RDP session cost +18 %; use `tscon <id> /dest:console` to leave); no edits to
  `.cs`, `.asset`, `.prefab` or harness Python while a chain runs; no compile checks either. Long chains run detached
  (PowerShell `Start-Process`), because background shell tasks are killed after about 30 min; after a kill, run
  `python -c "import regress; regress.stop_play()"` from `Tools/ServerMeshing`.
- **Editor slowdown:** the editor's Play-entry domain reload slows over a long session (5-10 s fresh, 30-46 s after a
  day). `regress.py` now waits while the editor is busy and prints "Play took N s to start ... restart the editor"
  above 45 s; restart the editor before long chains.
- Editor menus (for example `Server Meshing/Test Rig/Build Ship Prefab`) need the user; the harness drives the editor
  only through its request files (`ServerMeshPlayRunner`: refresh, unit tests, Play, player build, packages).
- On B: `RelicTestServer` holds the rig's ports and only the user can stop it (admin). Check `sc query RelicTestServer`.
- Repo `mmorpg-kit-space` (the space game) was read-only for this work until 2026-10-05; since then the user lets this
  work change it too (on branches; ask before merging, committing or pushing, as here).

## 5. Tools

- `Tools/ServerMeshing/regress.py`: scenarios; `--refresh --unit --build --package`; `--env KEY=VALUE`;
  `--remote-cells` for two-machine runs.
- `Tools/ServerMeshing/p0load.py`: stage 0a/0b measurement blocks, canaries, accept-a, report.
- `Tools/ServerMeshing/phase0/accept_step5.py`, `accept_step6.py`, `accept_step7.py`: per-step rig acceptance (write
  `Logs/p0/accept_stepN.txt`). `python accept_step7.py <group> ...` runs chosen groups.
- `Tools/ServerMeshing/phase0/cscheck.py`: compiles Assembly-CSharp(-Editor) outside Unity with the .NET SDK's Roslyn
  (`--editor`, `--client`; set `CSCHECK_OUT` to a unique folder when several run). `--client` shows 33 known,
  pre-existing guard-leak errors in `Mesh/MeshCentralNetworkManager.cs` and `Mesh/MeshMapNetworkManager*.cs` (an open
  task).
- `Tools/ServerMeshing/phase0/stray_integ.py`: the stray-reference check.
- Python tests: `python -B -m unittest discover -s Tools/ServerMeshing -p "test_*.py"`; lint `ruff check Tools/ServerMeshing`.

## 6. Documents here

- `phase0_build_sheet.md`: the Phase 0 spec (stages, steps, run order, gates, results format). Also published at
  https://claude.ai/artifact/QzLEyLPTFxGuFq9mKHD1NB. Its §0.9 note on CE-12 is stale (the kit now carries the pass).
- `step2_design.md` ... `step7_design.md`: per-step implementation contracts (decisions, files, tests, acceptance,
  critique logs); `step2_results.md`, `step3_results.md`; `stage0a_report.md`.
- `stage0b_8a_results.md`: draft results of step 8a's blocks on A (data sets E0, E1, E1'), for the stage 0b report.
- `surveys/`: code surveys behind steps 5-7 (`survey7/space.md` compares the build sheet with the space game's code).
- `kit_patch_9_10_reply.md`: our replies to the space team on their kit patches #9/#10 (now 0006/0007).
- `step7_space_questions.md`: open questions for the space team, plus two findings for them (the kit's frame-0
  `Identity` bug and their approximate aim compensation).
- Paths in these documents that point into `C:\Users\brend\AppData\Local\Temp\claude\...\scratchpad` were B's scratch
  folder; those files are now here (documents) and in `Tools/ServerMeshing/phase0` (scripts).

## 7. Known open items

- Kit bug (not yet patched): `LiteNetLibBehaviour.Identity`'s per-frame cache starts at `_lastGetIdentityFrame = 0`,
  so on frame 0 of Play it returns null without looking; `ServerMeshShipEntity.EnsureRegistered` works around it. A
  one-line upstream fix (start at -1) is a candidate CE patch.
- `party` failed only on accept-a's first default set, twice (login latency after heavy runs?); not investigated.
- SETUP.md lacks the step 5 keys' entries.
- The `--client` compile errors (section 5).
- `Addons/*/dist` packages must be rebuilt (`regress.py --package`) before they are committed: the last package build
  predates the frame-0 workaround.
- Three stale `.meta` deletions under `Assets/OpenMMORPG` are deliberately left uncommitted.
