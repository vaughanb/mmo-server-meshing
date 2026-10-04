# Dynamic server meshing: handoff to machine A (2026-10-04)

Until now the work ran from machine B (the laptop, `192.168.50.211`, Ryzen AI 9 HX 370), which also hosted the Unity
editor and every measurement. From here machine A (the desktop, `192.168.50.83`) takes over: it runs the editor and
the harness, and drives B over SSH (`Tools/ServerMeshing/remoteb.py`) whenever a test needs a second machine. The space
team works on A too, so ship questions can be settled there directly.

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
| **Phase 0** | **Step 7 of 9 is finishing** (below) |
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
- **Step 7, code done and accepted on B; three items left (section 3).** Ship stand-ins modelled on the space game
  (`TestMap/Ships/*`, prefab `TestMap/Prefabs/ServerMeshShip.prefab`), the `ship` phantom kind, the in-process puppet
  feeder, sync/RPC load, record-size counting in the shipped send meter (`MESH_METER_RECORD_SIZES`), and a small
  shipped hook `IMeshSenderOwnedSource`. EditMode 2035 pass / 0 fail (+1 deliberately ignored compact golden), Python
  434 OK. `accept_step7.py`: every group passes on B (record sizes exactly 54/56/28 B with 5 B framing; once per
  build; bots see ships; AI patrol 1.01 m/s, top speed exactly 3.76 m/s at scale 0.03; pilots; mirror ships through
  phantoms; feeder 4,000 snapshots/s; sync load).
- **Step 8, not started:** stage 0b tools (`p0load.py` subcommands accept-b, calibrate, observed, fanout, targets,
  syncrpc, packets, prewarm, crossings; `twohost.py`/`remoteb.py` bots and sink modes; meshbot2..N registration;
  `Tools/p0geom.py`; parsers; ENV_KEYS), then measurement blocks b1-b10, several of them two-machine.
- **Step 9 (stage 0c), not started:** database burst (the user permits creating test accounts), fantasy worker A/B,
  the isolated-spaces floor (the space game's bubbles), loss.
- After Phase 0: the DESIGN 0.74 row (one stage-level row for stage 0b, like 0.73), then slices 2b-6.

## 2. Moving the measurements to A

Stage 0a's rows, baselines and fitted constants are B's hardware. Before any new measurement block on A:
`python Tools/ServerMeshing/p0load.py canary --baseline` on A (A's own baseline; B's was 104.0 ms/s on the old build,
about 107 on the new), and `accept-a` on A. Never pool rows from the two machines; label every number with its CPU. If a
result must be compared with stage 0a, either re-run the needed stage 0a points on A or state that the comparison
crosses machines.

## 3. Step 7: what is left

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

B's last chain (canary on the new build, accept-a, sender gates) was still running at handoff; its result is reported
to the user from B. A repeats it on its own hardware anyway (section 2).

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
- Repo `mmorpg-kit-space` (the space game) is read-only for this work.

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
