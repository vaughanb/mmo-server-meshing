# Phase 0 step 9 (stage 0c): database burst, fantasy worker A/B, isolated-spaces floor, packet loss (implementation contract)

Revision 2, 2026-10-09 on machine A, after two independent critiques and the smokes (C# and kit; design and tooling; log at the end).
Stage 0c's blocks (`phase0_build_sheet.md` :986-991): c1 B5 database burst, c2 B7 fantasy worker A/B, c3 B11
isolated-spaces floor, c4 B3-loss. Step 8b (`step8b_design.md`, commits 715260e..a74372d) built the two-machine rig.

Read first: this contract; the sheet's step 9 (:886-895), §2 stage 0c (:986-991), §3 gates (Crossings :1035, Database
:1041), §4.5 (:1093-1096), §5 (:1143-1162); HANDOFF §4. Where they disagree: THIS CONTRACT, then the sheet.

User decisions (2026-10-09): do step 9 now; **c4's loss comes from clumsy on B** (`remoteb impair`), not LiteNetLib's
simulation (compiled out of the player: `[Conditional]` at `LiteNetManager.cs:787`, `#if` at :828). Choices the user
has not seen are marked (D).

## Rules
Every step 5-8b rule binds (`step8b_design.md` "Rules"): Mesh/ changes default off and byte-identical, game-neutral,
in new partials (`MeshMapNetworkManager.cs` is 1,139 lines, `_Ghosting.cs` 3,682); TestMap seams in new files
(`ServerMeshLoadGenerator.cs` is 499); `p0cells0b.py` is full (new parsers go in `p0cells0c.py`). The database is
MySQL (`databaseOptionIndex` 0), possibly shared with another game. **Direct** writes (provision.py) happen only out of
Play and only to rig rows. The burst's in-Play writes go through the database manager (the same path a player's save
takes), which keeps its cache coherent: that is the reason behind HANDOFF §4's rule, so they are allowed (HANDOFF
gains this sentence). Reflection is allowed read-only on kit objects where no public member exists (precedent: the
phantom fighter, `PurgeRemoteSocialMirror`).

## Decisions

### c1 database burst (B5; family `dbburst`, game neutral: Database is a shared gate)
1. **Burst rows (provision.py).** `provision.py --burst N` / `provision.burst(n)`: accounts `meshburst0001..N` with
   email `<user>@meshburst.invalid` and one character `meshburst<k>Char` each on ServerMeshTestMap channel C, west
   half; idempotent; out of Play only; one transaction. The guard is generalised: every touched account must match the
   rig name pattern **and** the rig email, and an existing `meshburst` name with another email is refused; tests use a
   fake cursor. N = 800 (one distinct character per arrival, decision 4). `before_play` (A-only hook) calls it.
2. **The burst (C#, TestMap/Phase0/ServerMeshDbBurst.cs, `MESH_DB_BURST`).** Grammar `<N>@<t>` (N arrivals at once) or
   `<R>/s@<t>+<d>` (R arrivals per second for d s), each arrival a distinct burst character. Warm-up: through the
   cell's public `DatabaseClient` it reads each character (`GetCharacterAsync`, full data) and sets its access token
   (`UpdateAccessTokenAsync(UpdateAccessTokenReq)`, `IDatabaseClient.cs:21`). One arrival = a real handoff's database
   requests: origin save `UpdateCharacterAsync` with the full data read in the warm-up (not the kit's internal
   `SaveCharacter`, which refuses while `savingCharacters` holds the id), then the destination's
   `ValidateAccessTokenAsync`, `GetCharacterAsync` (a cache hit after the save: stated, not hidden), and the
   background reads `GetGoldAsync`, `GetCashAsync`, `GetUserLevelAsync`, `GetSummonBuffsAsync`
   (`MapNetworkManager.cs:688, 769-805`): 7 requests, the sheet's §4.5 estimate. Lines (stamped ms): `DB burst <i>:
   save <a> token <b> get <c> bg <e> ready <d> ms ok` (or `failed <step>`) and `DB burst window 5.00 s: started
   ready failed | ready p50 p95 max ms`. Wrapped in the kit's server guard (`#if (UNITY_EDITOR || UNITY_SERVER ...) &&
   UNITY_STANDALONE`, as `MapNetworkManager.cs:52`). Default off.
3. **Save drain (C#, TestMap, editor process: the database manager runs in the editor, the cells are its clients).**
   `MESH_DB_SAVES_PROBE=1`: every 5 s, `DB saves window 5.00 s: pending <n> enqueued <n> written <n> failed <n> | db
   fps <f>`. pending is the scheduler's public `PendingCount` (:56) on the private `_characterSaveScheduler`
   (`DatabaseNetworkManager.cs:73`, read by reflection); enqueued is counted from the burst's acknowledged saves
   (passed through the window line of the cell, so the probe line carries written = enqueued − Δpending); failed counts
   the kit's "Deferred character save failed" lines (:194-195) through a log hook; db fps is the editor's frame rate.
   The configured ceiling (one lane per 500 ms tick, ≤ 8 writes: 16 writes/s) is printed beside it.
4. **Points** (~24 runs, ~1.0 h; A, and B for the calibration). Cell C coarse, no load but the burst. Bursts N {50,
   100, 200} × 3 (9). Sustained R {5, 10, 15, 20} /s for 40 s × 2 (8), one distinct character per arrival. Calibration
   (2 pairs, 4 runs, B): 25 real bots entering at once (stagger 0) against a 25 burst; the bots' figure is the cell's
   `enter-game validated+loaded ... in N ms` (`MeshMapNetworkManager.cs:380`), the burst's is token + get; compared by
   median and p90 (25 samples). Every run measures until the drain is empty (pending 0, a `stop_when` on the probe line,
   bounded at 120 s after the last arrival) so no save is left for after Play; the smoke checks (read-only) that no
   burst row's `updateAt` changes after Play stops.
5. **Metrics and gates.** `latency_ms{db_ready}` p50/p95/p99/max and errors; requests/s and arrivals/s; drain:
   written/s, peak pending, time to empty. Gate Database (:1041): 200 simultaneous ready within 5 s at p95 with 0
   errors → PASS/FAIL. Sustained capacity = the highest R whose ready p95 < 1 s and whose pending slope over the last
   24 s (≥ 1.5 lane cycles of 16 s) is ≤ 0.5/s; reported in arrivals/s and requests/s beside the configured 16
   writes/s. The crossing load comes from the recommended geography, so the gate's "≥ 2× the crossing load" and
   Crossings half B (:1035) are stated with the capacity and compared in the stage report (D).

### c2 fantasy worker A/B (B7; family `workerab`)
6. **`MESH_MONSTER_CELL=<cell>` (Mesh/, new partial `MeshMapNetworkManager_MonsterCell.cs`).** When set: the named
   cell owns every `MonsterSpawnArea` and the others own none (only that loop, `_Ghosting.cs:303-312`; harvestables
   and drops keep geographic ownership); monster handoff is off; and **every non-monster cell streams every owned
   character to the monster cell** (one predicate in `SendBandSnapshots`), so the monster cell's monsters see C's
   players (AI awake, aggro, rewards). E's monsters are already ghosted into C: the band test is distance to the
   neighbour's interest rectangle, 0 inside it (`MeshLayoutGate.cs:174`, `MeshLayout.cs:62-69`). The placer
   (`MESH_MONSTER_PLACE`) is not used. Logged once. Default off.
7. **Fighters may hit ghosts (TestMap, `MESH_FIGHT_GHOSTS=1`).** `SyntheticPlayerBrain.FindMonsterInReach` skips
   ghosts (`ServerMeshLoadGenerator.cs:459`); under the key it does not, so the hit reaches `ForwardGhostHit` through
   the ghost's damage hook (`_Ghosting.cs:1529-1531`). Set in both arms (a no-op in coarse).
8. **Forwarded-hit timing (Mesh/, opt-in `MESH_FORWARD_TIMING=1`).** A window on the sending cell timing
   `ForwardGhostHit` send → `GhostHitResult` receipt on its own clock (first in, first out per target): `Forwarded
   hits window 5.00 s: sent <n> results <n> p50 p95 max ms`. Under the key the per-hit trace lines (:2761, :2839) are
   not written (they cost work in the halves arm only). Default off.
9. **Points** (~14 runs, ~0.7 h, A only, 30/0.02): first one coarse probe per load; loads {100, 200} players (half
   character phantoms `virtual:<n/2>:character:C,E` with a fight share, half load players) with all scene monsters
   (`MESH_MONSTER_AREAS=all`), falling back to {50, 100} if coarse 200 saturates; arms {coarse; halves +
   `MESH_MONSTER_CELL=E`} × 3. Metrics: work per cell, awake monsters per cell, `latency_ms{forwarded_hit}`, forwarded
   hits/s. Acceptance (:895): E owns every monster and C none; C sees E's monsters (ghosts > 0 m); forwarded hits > 0;
   the two arms' awake-monster counts within 10 %; no exception line. No gate; the windows feed the §4.1 refit (:1061).

### c3 isolated-spaces floor (B11; family `spaces`, `model: spaces`)
10. **`MESH_SPACES=S[:layout]` (TestMap/Phase0/ServerMeshSpaces.cs + a SubChannel seam in a new file beside the load
    generator).** S isolated spaces in one process and physics scene, each with its own SubChannelId `space:<k>`, set
    before `NetworkSpawn` on every spawn (and cleared on pool return: `SubChannelId` is never reset, `LiteNetLibIdentity
    .cs:54, 171`). Each space holds: tagged **observers** (virtual character phantoms, so the kit's per-connection AOI
    and `IsHideFrom` are exercised: `JobifiedGridSpatialPartitioningAOI.cs:183-200, 273`), and **content**: 5
    stationary, non-always-visible networked stand-ins for a station (built from an already registered prefab, the
    placer's runtime recipe; always-visible objects bypass `IsHideFrom`, AOI :212, :318). Layout `stack` (default: every
    space at one centre, the dungeon-instance case where only the sub-channel separates them) or `grid` (150 m pitch,
    inside the 600 m ground). `MESH_NO_MONSTERS=1`, no fighting. Logged once. Default off.
11. **Points** (~18 runs, ~0.6 h, A only, 30/0.02): stack, S {1, 4, 16, 64} × load {1 observer per space; 100 players
    split evenly} × 2 (16); grid, S 16, load 100 × 2 (2). Metrics: work, AOI pass cost, per-space visible entities
    (each observer must see only its own space). The floor = work per space at 1 observer, with its interval (one space
    of content is near canary noise, so S 64 carries the floor); the slope = load 100 against S. Feeds the
    isolated-spaces policy (:89, :1193; `space_contract_6.1c` item 3 allows up to 98 slots per process). No gate.

### c4 packet loss (B3-loss; family `packets`, case `loss`)
12. **Loss from clumsy on B.** `remoteb impair <profile>` (clumsy/WinDivert, UDP to and from A, both directions:
    `remoteb.py:206-249`, profiles :55-65); add `loss1`. A spec may carry `peer.impair`: the PeerSession starts it after
    the sink is up, and the measurement opens only after `impairment on`; after_play stops it (its own try/finally).
    `quiet_b` counts clumsy and every peer run refuses to start while one runs (clumsy is started outside the SSH job,
    so a crashed harness leaves it running: the docstring at :207-212 is corrected). If the SSH session cannot start
    clumsy (no admin), c4 is skipped and the report says why (D).
13. **Points and hypothesis** (9 runs, ~0.5 h, A and B): b7's cap-sink point (ship-sender-28 tiered, `sink:100:ship:C`
    + `C:400:ai`, V 500, scale 0.5, 1,020 B) × loss {0, 1, 5} % × 3, the arms' order rotated per repeat. Hypothesis:
    ship records are unreliable, so A's work barely moves; reliable traffic is resent with probability ~2p (both
    directions); what loss costs is received rate. Metrics: work, network thread, datagrams/s, LiteNetLib
    `Statistics.PacketLoss` (reliable resends, counted already: `ReliableChannel.cs:252-255`, statistics on at
    `MeshMapNetworkManager.cs:707`) logged per window by a TestMap probe (`MESH_NET_STATS=1`), and at the sink the
    received records per pair per second against the sender's tier rate. No gate.

### Harness (Python)
14. Families `dbburst` (neutral), `workerab` (fantasy), `spaces` (neutral, `model: spaces`: `p0results.run_row` stops
    hard-coding the model, :178); c4 is `packets` with case `loss` (no new family; §5 has none). `p0plans.STAGE_LATER`
    loses workerab, dbburst, spaces; `p0load` gains `dbburst`, `workerab`, `spaces`, `packets --loss`; plans in a new
    `p0plans9.py`, CLI in a new `p0load9.py`; all in `p0blocks.CANARY_FAMILIES`. Keys `MESH_DB_BURST`,
    `MESH_DB_SAVES_PROBE`, `MESH_MONSTER_CELL`, `MESH_FIGHT_GHOSTS`, `MESH_FORWARD_TIMING`, `MESH_SPACES`,
    `MESH_NET_STATS` go to a step 9 registry (`p0stage0b`-style) and `regress.ENV_KEYS`; `MESH_SHIP_JUMP_SECONDS` stays
    pending. Parsers in `p0cells0c.py`; checks and gate rows in `p0checks9.py`; `p0report` rows for Database and
    Crossings half B's B5 term. Pinned tests rewritten (`test_p0plans`, `test_p0plans0b`, `test_p0checks`,
    `test_p0results`, `test_p0report`, `test_p0tools` :294-331, `test_rigrun`).

## Log lines (exact; C# writes them, `p0cells0c.py` parses them; invariant culture, no braces, never "Exception")
Every line goes through `Logging.Log("Mesh", ...)` unless marked MeshTrace; the 5 s windows end on the probe's own
clock and carry the window's seconds first, like the Work window.
- `DB burst <i>: save <a> token <b> get <c> bg <e> ready <d> ms ok` / `DB burst <i>: failed <step> after <d> ms` (i the
  arrival's 1-based index; ms as integers).
- `DB burst window 5.00 s: started <n> ready <n> failed <n> | ready p50 <x> p95 <y> max <z> ms`
- `DB burst: <N> arrivals at once at <t> s` or `DB burst: <R>/s for <d> s from <t> s` (once, when it starts) and `DB
  burst: done, <n> ready <m> failed` (once, after the last arrival settles).
- `DB saves window 5.00 s: pending <n> enqueued <n> written <n> failed <n> | db fps <f> | ceiling 16/s`
- `MESH_MONSTER_CELL=<cell>: cell <cell> owns every monster spawn area` (once; the other cells log `MESH_MONSTER_CELL=
  <cell>: this cell owns no monster spawn area`).
- `Forwarded hits window 5.00 s: sent <n> results <n> | p50 <x> p95 <y> max <z> ms`
- `Spaces: S <n> layout <stack|grid> pitch <m> m | per space observers <o> content <c>` (once) and `Spaces window
  5.00 s: observers <n> | own seen mean <x> | other seen max <y>` (other seen must be 0).
- `Net stats window 5.00 s: sent <n> pkt received <n> pkt resent <n> | loss share <x>` (resent = the delta of
  `Statistics.PacketLoss`).

## Files
- C# Mesh/: new `MeshMapNetworkManager_MonsterCell.cs` (decision 6) and the forwarded-hit timing (decision 8) in a new
  partial; the one-line hooks they need in existing files.
- C# TestMap: new `Phase0/ServerMeshDbBurst.cs`, `Phase0/ServerMeshDbSavesProbe.cs`, `Phase0/ServerMeshSpaces.cs`,
  `Phase0/ServerMeshNetStats.cs`, a SubChannel seam and the ghost-target opt-in beside the load generator (new files);
  EditMode tests for every parser and pure decision.
- Python: `provision.py` (+ tests), `remoteb.py` (loss1, docstring), `p0peer.py` (impair, clumsy in quiet_b),
  `p0blocks.py` (CANARY_FAMILIES), new `p0plans9.py`, `p0load9.py`, `p0cells0c.py`, `p0checks9.py`; `p0results.py`,
  `p0plans.py`, `p0load.py`, `p0report.py`, registry, `regress.py` ENV_KEYS; tests.
- Docs: this log; HANDOFF (the in-Play write sentence, status); the results draft.

## Acceptance (step 9)
1. Unit and EditMode tests pass; ruff clean; the stray-reference check and package build pass.
2. `p0load <cmd> --dry-run` prints each plan with the counts above.
3. Smokes (rows to `Logs/p0/accept9/`): a 50 burst and a 5/s sustained run (DB burst and DB saves lines, drain to 0,
   no `updateAt` change after Play); a workerab coarse probe at 200 and one halves run (E owns all, C none, C sees E's
   monsters, forwarded hits > 0 with timing, awake counts close); spaces stack S 4 at 100 (each observer sees only its
   space); one loss5 run with B (clumsy up and down, `PacketLoss` rising, the row accepted).
4. Then the blocks: c2 and c3 (A only), c1 (calibration with B), c4 (with B).

## Critique log
- **Rev 1 → 2 (2026-10-09), the smokes (`Logs/p0/accept9/`):** c2's players are all connected character phantoms on C
  in one rect inside C's half, in both arms (`p0plans9.WORKER_AREA`): load players have no connection, so in coarse
  they never woke a monster while their ghosts on E did (10 against 63 awake), and VIEW groups could not keep clear of
  C's keep-out points in the narrower half. With that, the awake counts still differ by design (coarse: subscribed,
  out to the 80 m view; the monster cell: a player ghost within the monster's visual range + 10 m; 164 against 85),
  so decision 9's "within 10 %" is reported, not judged. A new TestMap probe, `MESH_MONSTER_AWAKE=1`
  (`ServerMeshMonsterAwake.cs`), writes the Monsters window for scene monsters (placed 0). The burst's line carries
  per-step laps, so the calibration figure is token + get. `p0blocks.measured` also covers `stop_after_drain`.
- **Rev 0 → 1 (2026-10-09), C# and kit review:** forwarded hits were structurally 0 (the fighters skip ghosts) → a
  ghost-target opt-in; forwarded-hit latency was not measurable (whole-second stamps, trace-only lines) → a Mesh opt-in
  window; decision 7 checked the wrong direction (E's monsters already reach C; C's players never reach E, so its
  monsters idle) → stream every character to the monster cell; the arrival lacked the background reads and its get is
  a cache hit; the calibration span sat after the database work; sustained points reused rows and saves coalesce;
  the scheduler is private → read-only reflection; SubChannel isolation was never exercised (no subscribers; spaces
  already apart) → observers, a stack layout, station stand-ins that are not always visible; file sizes → new
  partials; only the MonsterSpawnArea loop; API names (`UpdateAccessTokenAsync`, `UpdateCharacterAsync`, full data,
  the server guard); `Statistics.PacketLoss` already counts resends; SubChannelId reset on pool return.
- **Rev 0 → 1, design and tooling review:** the c2 arms ran different monster workloads → the two-way stream plus an
  awake-count acceptance; capacity in requests/s and handoffs/s; the calibration statistic (median, p90, 2 pairs);
  sustained capacity was fixed by kit settings (16 writes/s, coalescing) → distinct rows, R 15, a slope rule, the
  ceiling reported; deferred saves keep writing after Play → each run ends with pending 0, the smoke checks
  `updateAt`; c3 grid did not fit the ground and the floor was below noise → stack layout, 150 m grid, S 64,
  intervals; clumsy could be left running → quiet_b counts it, runs refuse, the window opens after `impairment on`,
  the no-admin fallback decided; c4's metrics missed where loss lands → resends, received rate per pair, a stated
  hypothesis; coarse 200 may saturate → a probe and a fallback load; provision guard generalised; harness gaps
  (CANARY_FAMILIES, p0cells0c, game neutral, model, test_p0tools pins, an A-only before_play, arm order rotated).
