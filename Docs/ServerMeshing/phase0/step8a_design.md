# Phase 0 step 8a: stage 0b tools for machine A, and the rig features blocks b3-b8 need (implementation contract)

Revision 1, 2026-10-04 on machine A, after one independent critique (log at the end). Step 8 is split (user,
2026-10-04): **8a** builds what the single-machine blocks b3-b8 need and runs them on A; **8b** (a later contract)
builds bots on B (login address, meshbot2..N registration), accept-b (b1), calibrate (b2), crossings (b9), J-full (b10)
and b7's sink arms, attended with B.

Read first: this contract; the surveys `surveys/survey8/framework.md`, `twohost.md`, `csharp.md`, `blocks.md` (the
authority on WHERE things are); for intent `phase0_build_sheet.md` step 8 (:864-878), §2 stage 0b (:945-978), §3 gates
(:1004-1042), §5 results (:1136-1171); `meshing_reply.txt` §7 item 5 (:1160-1172). Where they disagree: THIS CONTRACT,
then the surveys, then the sheet.

User decisions (2026-10-04): p0geom on a **seeded scenario set**; **both AOI metrics** (X/Z and 3D; A0 2 km and 3 km);
a **new rig key places monsters**; step 8 **split 8a/8b**.

## Rules
Every step 5-7 rule binds (`step7_design.md` "Rules", `step7b_design.md`): C# 9 / .NET 4.7.1; XML docs on every public
member; errors with context; default off and byte-identical; log lines one physical line, invariant, no braces, never
"Exception"; `<X> error:` prefixes fail acceptance; `== null` for Unity objects; no allocation per tick; files under
~500 lines and the "not to grow" list (`survey8/csharp.md` §4); one MonoBehaviour per file; no `.meta` by hand; **no
Mesh/ change in 8a** (TestMap/ shares Mesh/'s assembly and may call its `internal` members); Python `unittest` tables
failure-first; ruff (pinned rules) clean; `regress.py` only its ENV_KEYS lines; `p0tools.py` does not grow.

## Decisions

### Framework (Python)
1. **Machine keying on (build, host).** A and B may share a CPU model, so the machine is `host`; `cpu` must be constant
   per host in a history (a check that refuses otherwise). Every build-only site keys on (build, host):
   `p0results.canary_baselines`, `apply_drift`, `bridge_ok`, `_canaries` (:394-396), `summary_rows` (group key gains
   host), `header_of` (refuses mixed hosts instead of joining); `p0blocks.done_keys`, `canary_state`, `Session.ours`
   (:171-173, feeding `earlier_runs` and `thirds` :242-269), the baseline lookup in `Session.canary` (:200),
   `Session.verdict` (:311); `p0report.latest_per_plan_key` (:225; key (build, host, plan_key)), `_one_build` (:232-240);
   `p0model._check_pool` already refuses mixed cpu (keep, add host). A test greps the harness for any comparison on
   `"build"` alone outside these functions. Rows gain optional `peer_host`, `peer_cpu` (None in 8a; 8b fills them).
2. **Stage 0b rows.** `p0results.STAGE` per family (0a families "0a"; new ones "0b"); `FAMILIES_0B` = observed, fanout,
   targets, syncrpc, packets, prewarm (8a) plus accept-b, calibrate, crossings, jfull (8b; refused in 8a naming "stage
   0b, step 8b"). A spec may carry its own `game`; the jsonl name uses it. `_load_fields` reads `kind`, `clients`
   (phantoms bound), `v_move_mean/p95` (Phantom window), `payload_B`, `scale` from the spec and the 0b record. §5's
   family list gains the six. `presence --observed` now says "use p0load observed".
3. **0b readers** (new pure `p0cells0b.py`): `cell_record_0b(cell, lines, start, end)` collects Send, Sender, Phantom,
   Ships, Feeder, Sync load, Prewarm and Monsters windows in [start, end] with the existing parsers;
   `cell_metrics_0b(record)` emits `downlink_wire_kBps_per_client` (Phantom down mean and p95),
   `entity_state_kBps_per_client` (Send t119 bytes / clients), `datagrams_per_s_per_client`, `reliable_records_per_s`
   (Send `records ro`), `rate_hz_by_tier` (Sender hz gauges), `upload_in_us`, plus the step 7 ship and feeder figures.
   `p0blocks.run_spec` calls both readers for 0b families; 0a families are unchanged. `p0cells.window_saturated(...,
   fanout=True)` for family fanout (the §3 rule, never wired).
4. **Stepped runs (b6 and the packets saturation arm).** The hold of `MESH_PHANTOM_STEPS` is timed from when a step is
   reached (`ServerMeshPhantomClients.cs:316-325`; `StepTarget`, `ServerMeshPhantomSettings.cs:458-466`), and step 1
   starts when the binder starts, so stepped specs use **warmup 10 s** and **measure = Σ holds + the bind ramps at
   50 binds/s (0 → P1, then each increase) + 20 s**. Step boundaries come from the stamped Info line
   `Phantom step i/n: P a -> b` (:322), clipped to [start, end]; a step's window is [reached + 10 s, next reached]. One
   row per step: `plan_key` = the run's key, `point.step` = P. `done_keys`, `verdict` and `thirds` treat a stepped run as
   done only when all n step rows are accepted. Coverage is checked per step window against its length, not
   `spec["measure"]`. Even repeats reverse the step list.
5. **Play hook.** `rigrun.run_window(env, warmup, measure, rep, on_play=None)`: `on_play(play_start)` runs after
   `wait_play`, before the warmup sleep; an exception stops Play and re-raises (`kill_bots` already runs before it and
   `stop_play` after). 8a uses it only in tests; 8b's sink and bots use it.

### New subcommands (plan builders in a new `p0plans0b.py`; CLI branches in p0load)
Run counts are planned runs (stepped runs count once; their rows are per step).
6. **observed** (b3, b4, b5), `--part O|R|SA|M|S`:
   - **O** (12): halves, `MESH_BAND` and owners as a4 (owners on C, puppets on E), eyes **on E**
     (`MESH_PHANTOM=virtual:P:eye:E`, `MESH_PHANTOM_VIEW=0` host mode) P {50, 200} × `MESH_PUPPET_MOTION` {steered,
     kinematic} × N 200 × 3.
   - **R** (6): client-driven owners: `virtual:N:character:C` with `MESH_PHANTOM_VIEW=50`, N {200, 400} × 3.
   - **SA** (6): the same with `MESH_PHANTOM_PAYLOAD=input` (sets `movementSecure` server-authoritative, sheet :670, gap
     H).
   - **M** (18): halves, a4's band, `MESH_NO_MONSTERS=1`, `MESH_NO_MONSTER_HANDOFF=1`, 200 placed monsters on C inside
     the band (decision 12). Arms: owned (eyes `eye:C` host mode on the monsters' area), steered puppets (eyes
     `eye:C,E`: C's eyes keep the real monsters awake, E's watch the puppets), kinematic puppets (same,
     `MESH_PUPPET_MOTION=kinematic`) × rates {60/0.03, 30/0.02} × 3.
   - **S** (42; the sheet says ~36): 30/0.02, scale 0.03: AI {100, 200, 400}, mirror {100, 200, 400, 800}, feeder ship
     {100, 200, 400}, feeder character 200, one dense row (AI 100 at scale 1.0), parked 200 × `MESH_SHIP_PARK_SLEEP`
     {0, 1} = 14 points × 3.
   Games: O, R, SA, M fantasy; S space.
7. **fanout** (b6; 66): the seven arms (sheet :961-975), V {100, 300, 500} (tiered arms skip 100), P stepped 50 → 100 →
   200 → 400 at 90 s per step (decision 4), 3 repeats = 18 points × 3 = 54; plus classify-every-10-builds
   (`MESH_SENDER_CLASSIFY_BUILDS=10`) on ship-sender-56 parity at V 300 × 3 = 3, and the 60 fps subset
   (`MESH_RATES=60/0.03`) on the three character arms at V 300 × 3 = 9. Geometry: eyes in host mode sit on their hosts,
   so pairwise host distance must be inside the range. **Character arms:** V load players in `MESH_LOAD_AREA=x,z,25`
   (2√2 × 25 = 71 m < 80 m). **Ship arms:** scale 0.03, rates 30/0.02, V load mirror ships
   (`MESH_LOAD_SHIPS=C:<V>:mirror`) in `MESH_LOAD_AREA=x,z,15` (2√2 × 15 = 42 m < 60 m; `TryChooseCentre` needs
   2r ≥ 16.8 m), `MESH_PHANTOM=virtual:400:eye:C`, `MESH_PHANTOM_VIEW=0`. V is read from the Phantom window's V move;
   a row is accepted only when V move mean ≥ 0.95 × V. sync/RPC is its own family (decision 9). Points above 1 GbE are
   virtual only.
8. **targets** (36): S-T (`virtual:64:ship:C` + `C:64:ai`) and S-stress (`virtual:400:ship:C` + `C:100:ai`) at
   **`scaled_env(0.5)`** (a 1,000 m range covers the coarse cell's 849 m diagonal; at 0.03 each client saw about 4
   ships), rates 30/0.02; F1 (`virtual:150:character:C`, `MESH_PHANTOM_AREA=0,0,100,100`, `MESH_PHANTOM_STAND=0.7`,
   30 placed **passive** monsters in a disc of r 50 m at the square's centre) and F2 (`virtual:100:character:C`,
   `MESH_PHANTOM_AREA=disc:0,0,50`, `MESH_PHANTOM_FIGHT=1`, 100 placed monsters in the same disc), each F at both rate
   sets: 6 points × arms {kit, tiered sender} × 3. Accepted only when V move mean ≥ 0.95 × the expected V (128, 500,
   and every phantom for F1/F2).
9. **syncrpc** (12): D {0, 250} × R {0, 2,100/s} (`MESH_SYNC_LOAD`) on S-T at scale 0.5 × 3.
10. **packets** (b7 virtual part; 24): kit P 400 character phantoms V 500 (`virtual:400:character:C`,
    `MESH_PHANTOM_VIEW=500`) × 3; ship-sender-28 P {100, 400} V 500 at scale 0.5 × `MESH_SENDER_PACKET_BYTES` {1020,
    510, 255} × 3 (the cap applies to the sender only); the **saturation arm**: the kit character arm with eyes stepped
    P 100 → 200 → 400 → 800 at 60 s per step × 3, `MESH_THREAD_CPU=1`, reporting per step the LiteNetLib thread ms/s,
    datagrams/s and wire kB/s, and "saturated" (reported, never failed) where the LiteNetLib thread reaches ≥ 900 ms/s
    or datagrams/s rise < 10 % for a doubling of P. Sink arms are 8b.
11. **prewarm** (b8; 45): bursts at **t = warmup + 10 s** (inside the window). Points: {steered, kinematic, ship} × N
    {100, 300, 650} all at once (9); 650 kinematic at 50/frame (1); 650 kinematic with 100 eyes (`virtual:100:eye:C`,
    host mode on 200 load players in `MESH_LOAD_AREA` at the burst disc) (1); range-change 80 → 160 m and 80 → 300 m
    with 100 eyes on 200 load players spread over the cell (no `MESH_LOAD_AREA`) (2); group arrival N {100, 300} with
    100 eyes on 200 load players at the burst disc (2) = 15 × 3. For eye arms acceptance also requires a rise in the Send
    window's spawn messages within 2 s of the burst. No gate; feeds gap D and slice 2b's pre-warm pacing.
    Every new family joins `CANARY_FAMILIES` (and `THIRD` for 2-repeat arms); `p0dryrun.predict_load` prints "(E) not
    modelled for phantoms, ships or monsters" rather than a wrong number. `STAGE_LATER` keeps workerab, dbburst, spaces
    ("stage 0c, step 9") and the 8b names ("stage 0b, step 8b"); stale labels go.

### p0geom (new `Tools/ServerMeshing/p0geom.py`, pure; meshing reply §7 item 5)
G. **Scenarios** (seeded `random.Random(seed)`): `furball` (all ships uniform in a sphere of radius R, headings
   uniform), `lines` (two groups facing at distance D, each uniform in a disc of radius r, headings toward the other
   group), `patrol` (uniform in a box L × L × H). Fights 64 + 64 and 400 + 100; 10 seeds per point; default grid furball
   R {500, 1000, 2000} m, lines D {1000, 3000} r 500, patrol L {5000, 10000} H 1000.
   **Speeds:** player ships (Base) full 125.33 m/s 75 %, boost 250.67 m/s 15 %, patrol 50.13 m/s 10 %; AI (Enemy) patrol
   37.6 m/s 30 %, attack clamp(d/400, 0.35, 1) × 94.0 m/s 60 % (d = distance to its nearest enemy), full 94.0 m/s 10 %,
   never boost. Each ship is its own group, so f = the fastest ship in the fight (an accepted reading of "fastest group
   in the same bubble", `large-star-systems.md:1008`).
   **Counts per client** (every player ship is a client; AI ships are not): V = ships t whose range A_t covers the client
   (the range belongs to the target), A_t = A0 + (|v_t| + f) × 1.25 s, capped at a roam R_cap (default 30 km),
   A0 ∈ {2,000, 3,000}; distances in **3D and X/Z**; share of ships within 1 km; pilots' neighbours = other player
   ships within 80 m X/Z (kit default range), and under "seated characters carry their ship's range" = player ships
   whose A covers the client (AI pilots carry no character, (E)); locks: **player ships only**, each locks the nearest
   enemy inside 3,000 m and a **25° half-angle** cone about its heading, skipped inside 100 m
   (`ShipMissileComponent.cs:35-38, 135`); extra subscriptions = locks with distance > A_t.
   Output per scenario point: mean, p95 and max over clients and seeds, as markdown and jsonl
   (`Logs/p0/geom/p0geom_<stamp>.md/.jsonl`: a sub-folder, since `p0blocks.read_history` reads every `Logs/p0/*.jsonl` as rig rows), labelled "offline (no rig)". CLI `python Tools/ServerMeshing/p0geom.py
   [--fight 64+64] [--shape furball] [--radius 1000] [--seeds 10]`; no args runs the default grid. Tests (exact counts on
   hand-built geometry): pairs at exactly 1 km and 80 m (inclusive `<=`); an X/Z-near Y-far pair (1 km X/Z, 20 km Y);
   A at A0 2 km reproducing 2,313.3 m (both at full) and 2,626.7 m (both at boost), and at A0 3 km; the roam cap; a lock
   beyond A and inside A, one inside 100 m, one outside the cone; AI never locks; mean and p95 over a 5-client set;
   seeded determinism.

### Rig C# (TestMap only)
12. **`MESH_MONSTER_PLACE=<n>@<x>,<z>,<r>[:passive][;...]`** (new rig key; `TestMap/Phase0/ServerMeshMonsterPlacer.cs` +
    `_Window.cs`).
    - **Prefab:** the test map's first monster prefab of its spawn areas (inactive included), registered on **every**
      cell the way `RegisterHarvestablePrefabs` does (`Ghosting.cs:2119-2140`: `GameInstance.AddMonsterCharacterEntities`
      + `Assets.RegisterPrefab`), since `MESH_NO_MONSTERS=1` deactivates the areas before the kit's `RegisterEntities`
      (`BaseGameNetworkManager.cs:1130-1135, 1253`) and neighbours need the prefab to ghost it (`Ghosting.cs:1899-1903`).
    - **Spawn** on the cell that owns the disc's centre, after the server can instantiate, with the kit's GM recipe
      (`DefaultGMCommands.cs:590-605`: `GetObjectInstance` → `SetSpawnArea(null, prefab, level, point)` →
      `Assets.NetworkSpawn`), so `OnAssetObjectSpawn` tracks it (`Ghosting.cs:585-593`); seeded points
      (`MESH_LOAD_SEED`).
    - **Handoff:** a placed monster has no spawn area, so no `LentMonster` would be recorded (`Ghosting.cs:2523`):
      every spec that places monsters sets `MESH_NO_MONSTER_HANDOFF=1`, and the placer counts only its monsters that
      are not ghosts.
    - **Constant counts:** a placed monster below 50 % hit points is healed to full (as the phantom fighter heals
      phantoms), so fights keep it alive; one that is destroyed anyway is re-placed at its point after 10 s.
    - **`:passive`** (F1's town NPCs): the monster never targets anyone. The implementation chooses the least invasive
      rig mechanism (a rig subclass setting, or clearing the AI's target each frame) and reports it; awake still means
      subscribers > 0.
    - **Errors:** a disc whose centre another cell owns is skipped silently on this cell; a disc that crosses its owning
      cell's edge, r ≤ 0, n < 1 or a total over 2,000 is `Monsters error:` naming the part. Works with
      `MESH_NO_MONSTERS=1`.
    - **Lines:** `Monsters: placed N in <discs> on cell C` once; `Monsters window 5.00 s: cell C placed N alive A awake
      W healed H respawned R | gc0 G` per work window (alive counts non-ghosts; awake = subscribers > 0).
13. **`MESH_PREWARM=<kind>:<N>@<t>[/<perFrame>]`** (moves from pending; `TestMap/Phase0/ServerMeshPrewarm.cs`, `_Window.cs`,
    `_Ghosts.cs`). t = seconds after the server can instantiate; perFrame absent = all N in one frame.
    - `steered` / `kinematic`: N synthetic Player ghosts through the real neighbour path,
      `mesh.OnGhostSnapshot(PrewarmConnectionId = -9001, msg)` with a `GhostSnapshotMessage` per puppet serialised as
      the puppet feeder does; `entityId`/`dataId` from the game instance's first playable character as the phantom
      binder takes them (`ServerMeshPhantomClients.cs:160-165`); seeded points in a disc (r 50 m) at `MESH_LOAD_AREA`'s
      centre (else the cell centre); re-fed at 10 Hz (inside `ghostStaleSeconds` 2 s). The kind must match the process's
      `MESH_PUPPET_MOTION` (steered = unset or steered) or it is an error naming both keys. They join `_playerGhosts` and
      so wake monsters near them (stated, not suppressed).
    - `ship`: N stand-in ships spawned and posed as the puppet feeder's ship kind does (PuppetHeld + puppet body), path
      indices from 3,000 (the feeder uses 2,000), fed at 20 Hz.
    - `range`: at t, `mesh.InterestManager.defaultVisibleRange` goes from its value to N metres (N in (0, 10,000]);
      `GetVisibleRange` reads it live each pass (`BaseInterestManager.cs:56-59`; the timed AOI under
      `MESH_AOI_TIMING=1`), so every identity on the default range re-subscribes; the sender's R0 keeps its start value
      (`MeshStateSender.cs:551`).
    - `group`: N **owned server characters** (spawned at t − 10 s with the feeder's `SpawnServerPlayer` recipe, at a
      point beyond every eye's range inside the cell) moved at t with the kit's `Teleport` into the burst disc: an
      arrival into view without spawn cost and without the per-puppet "Ghost teleported" warnings a ghost jump logs.
      Group needs `MESH_LOAD_AREA` and t ≥ 10 s (its lead); the park point must lie beyond the eyes' disc (radius
      max(50 m, r·√2), r the area's half-size) plus the larger of the default range and the group identities' own
      range, re-checked after the spawn; the burst disc must lie inside the owned cell (steered, kinematic, group).
      `ServerMeshPrewarm` runs just before the map network manager (execution order −895) so its ghost feeds land
      before the mesh's stale-ghost expiry; the burst `n` counts ghosts that actually spawned.
    - **Measured** from the burst's first frame to 2 s after its last spawn (or the range change or teleport): frames;
      spawn ms total and per puppet (Stopwatch around this component's own spawn calls; "n/a" for range and group); the
      worst frame period; frames over budget = frames whose period exceeds 1.05 × `MeshWorkProbe.EffectiveBudgetMs`
      (capped; "uncapped" otherwise).
    - **Lines:** `Prewarm: kind K n N at T s perFrame F on cell C` once; `Prewarm burst: cell C kind K n N perFrame F frames
      S | spawn ms T per puppet P | after frames W over budget O budget B ms worst X ms | gc0 G` once per burst. A ship
      prewarm counts as a ship key (`ServerMeshShipSettings`). Errors `Prewarm error:`.
14. **`MESH_SHIP_PARK_SLEEP=0|1`** (moves from pending; `TestMap/Ships/ServerMeshShipMovement_Park.cs`). Unset or 0 = today
    (parked ships are dynamic bodies with exactly zero force, `ServerMeshShipEngine.cs:66-88`; PhysX may sleep them,
    (E)). 1 = the space game's `ShipLander` Landed pin (`ShipLander.cs:362-365`): every FixedUpdate a parked ship's
    rigidbody position/rotation are set to its parked pose and its velocities zeroed, so it never sleeps; the zero-force
    calls still run. With the key set (either value) the Ships window appends ` | parked sleeping S of P`
    (`Rigidbody.IsSleeping()` at the window's end) and the settings line (`ServerMeshShipLoad.cs:202`) ` parkSleep 0|1`;
    unset, both lines are byte-identical. `IsAtRest` is unaffected.
15. **Keys and parsers** (new pure `p0stage0b.py`, `STEP8_REGISTRY` merged into `ENV_REGISTRY` beside step 7's):
    `MESH_PREWARM`, `MESH_SHIP_PARK_SLEEP` (leave `PENDING_KEYS`, which keeps `MESH_SHIP_JUMP_SECONDS` "stage 0c, step 9"),
    `MESH_MONSTER_PLACE` (new). **`regress.ENV_KEYS` 62 → 65** (all three are added: `rigrun.check_env` refuses keys outside
    ENV_KEYS); the `test_p0tools.py` pins (:300-302 and the §0.16 key list) move to match. Parsers `parse_prewarm_setting`,
    `parse_prewarm_burst`, `parse_monsters_window`, `parse_phantoms_port` (the cell's `Phantoms: cell C port N …` line;
    `accept_step7_sink.py` uses it instead of its inline regex), `parse_phantom_step` (the `Phantom step i/n: P a -> b`
    Info line); grammar mirrors `prewarm_setting`, `monster_place_setting`, `park_sleep_setting`; `p0ships.parse_ships_window`
    and `parse_ships_settings` read the optional `parked sleeping` and `parkSleep` groups.

## Files
- **Python:** `p0results.py`, `p0blocks.py`, `p0report.py` (keying sites), `p0model.py` (host in `_check_pool`),
  `p0load.py`, `p0plans.py` (STAGE_LATER, GAMES, the `--observed` message), `p0checks.py` (FAMILY_CHECKS),
  `p0cells.py` (saturation flag), `p0dryrun.py`, `rigrun.py` (on_play), `p0ships.py` (Ships fields),
  `phase0/accept_step7_sink.py` (`parse_phantoms_port`); new `p0cells0b.py`, `p0plans0b.py`, `p0stage0b.py`,
  `p0geom.py`; tests `test_p0cells0b.py`, `test_p0plans0b.py`, `test_p0stage0b.py`, `test_p0geom.py` and the moved pins
  in `test_p0tools.py`, `test_p0plans.py`, `test_p0results.py`, `test_p0blocks.py`, `test_p0report.py`,
  `test_imports.py`; `regress.py` ENV_KEYS only.
- **C#:** `TestMap/Phase0/ServerMeshMonsterPlacer.cs`, `_Window.cs`; `TestMap/Phase0/ServerMeshPrewarm.cs`, `_Window.cs`,
  `_Ghosts.cs`; `TestMap/Ships/ServerMeshShipMovement_Park.cs` and the few call lines in `ServerMeshShipMovement.cs`,
  `ServerMeshShipSettings.cs`, `ServerMeshShipLoad.cs` (:202), `ServerMeshShipLoad_Window.cs`; tests
  `ServerMeshMonsterPlacerSettingTests`, `ServerMeshPrewarmSettingTests` (grammar failures first; window and burst
  formatters exact under de-DE), a park-pin pure-helper test.
- **Docs:** SETUP §7 (three keys, lines); build sheet §0.16 (`MESH_MONSTER_PLACE`; prewarm and park step "8a"), §5
  family list.

## Acceptance (8a)
1. **Unit gates:** EditMode green; Python unittest and ruff clean; the stray test.
2. **Default byte-identical:** regress default scenarios pass with no key set; no Prewarm, Monsters or `parked sleeping`
   line.
3. **Dry runs** print exactly: observed O 12, R 6, SA 6, M 18, S 42; fanout 66; targets 36; syncrpc 12; packets 24;
   prewarm 45. `--resume` on an empty history plans everything.
4. **Machine keying:** on a test history with A and B rows of one build, A's canary is judged against A's baseline only,
   `--resume` on A skips only A's rows, summaries and reports keep the hosts apart; the "build alone" grep finds nothing.
5. **One smoke run per subcommand** (one point each, the block's checks): observed O (eyes 50 on E, kinematic) and M
   (owned, 200 placed: `Monsters window` placed 200 alive 200 awake ≥ 190); fanout one character arm with steps 50 → 100
   (40 s each) giving two accepted step rows; targets F2 (placed 100, awake ≥ 90 once phantoms bind, alive 100 in every
   window) and S-T (V move mean ≥ 121.6); syncrpc (D 250, R 2,100); packets saturation (two steps); prewarm
   `kinematic:100`, `ship:100`, `range:160`, `group:100` (each a `Prewarm burst` line inside the window with n 100, and
   for range and group a Send spawn rise within 2 s); park sleep: 200 parked ships `MESH_SHIP_PARK_SLEEP=0` then `=1`:
   `parked sleeping` > 0 then 0.
6. **p0geom** default grid runs offline and writes its table.
Then the 8a measurement chains (b3-b8; about 16-18 h on A) as overnight runs, canaries every 2 h, rows labelled with
A's host and CPU.

## Critique log
- **Revision 1 (2026-10-04),** one independent critic, read-only. Blockers: (B1) ENV_KEYS 62 → 65, not 63 (the two
  pending keys were never in ENV_KEYS) → decision 15; (B2) stepped runs lost step 1 to the warmup, measure ignored bind
  ramps, and per-step keys broke `--resume`/`verdict` → decision 4 (warmup 10, ramps, step-line boundaries, run key +
  `point.step`, all steps for done); (B3) prewarm bursts at t = 20 fell before the window, load players never subscribe
  (`BaseInterestManager.cs:62-63`), eye arms named no hosts → decision 11 (t = warmup + 10, eyes on load-player hosts,
  spread players for range, a Send check); (B4) at scale 0.03 a client saw ~4 ships, not 128/500 → S-T, S-stress,
  syncrpc and b7's ship arm at scale 0.5 with a V check; (B5) SA is client-input phantoms (`MESH_PHANTOM_PAYLOAD=input`,
  sheet :670), and R lacked the required VIEW → decision 6. Should-fix: keying sites and host-not-cpu (1); b4's three arms
  with eyes on C and E (6 M); O's eyes on E (6 O); monster placement's prefab registration, GM spawn recipe, handoff off,
  non-ghost counting and the error rule (12); F2's monsters healed so 100 stay awake (12); b6 geometry radii (7); p0geom
  speeds, player-only 25° half-angle locks skipped inside 100 m, pilots under the carry rule, f per ship (G); the group
  burst as owned server characters moved by Teleport, without per-ghost teleport warnings (13); exact run counts (6-11,
  acceptance 3). Nits: prewarm ghost ids and connection id, range on the timed AOI and "n/a" spawn ms, the Files list,
  F1 as a disc, ship prewarm path indices. All applied. Checked correct by the critic: `on_play` safety, the fake
  connection in `OnGhostSnapshot`, phantom setting combinations, ship hosts in eye host mode, the park pin, b3's count,
  defaults byte-identical, p0geom's subscription rule.
- **Implementation review (2026-10-04),** two independent reviewers (Python and plans; rig C#), read-only. Python:
  a stepped run's window opened 10 s after the step line, but the line is logged when the step starts and the binder
  then ramps at 50 binds/s (800 → 16 s), so reversed saturation repeats would fail `check_phantoms` forever → the window
  opens at line + ramp + 10 s (`p0cells0b.step_windows`, test with 800 → 400); `--warmup`/`--measure` would silently break
  stepped timing → refused for stepped plans; a Send message type the window does not list read as 0 → unknown (None);
  the smoke runs had no runner → `phase0/accept_step8a.py` (rows to `Logs/p0/accept8a/`, never counted by `--resume`);
  `p0load report --host`. C# (no blockers): prewarm runs before the mesh's ghost expiry (execution order) with an
  immediate first re-feed; the burst counts ghosts that spawned; "uncapped" from the probe's capped budget; passive
  restored before a placed monster returns to the pool; group refused for t < 10 s (C# and the Python mirror); the park
  check uses the eyes' real radius and the spawned identity's range, and the burst disc must lie in the cell; the
  puppet-motion check reads the mesh's own setting. Plans and machine keying were checked correct (run counts, env per
  arm, keying sites). F1's V counts the 30 placed monsters as movers (expected V ≈ 154; reported as such).
