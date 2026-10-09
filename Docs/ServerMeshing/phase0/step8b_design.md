# Phase 0 step 8b: bots on machine B and the two-machine blocks b1, b2, b7 (sink), b9, b10 (implementation contract)

Revision 1, 2026-10-07 on machine A, after two independent critiques (C# and kit; tooling and measurement design; log
at the end). Step 8a (`step8a_design.md`, commits 3d52e53..5b7eac0) built the stage 0b tools and ran b3-b8's
single-machine parts on A (`stage0b_8a_results.md`). 8b builds what the two-machine blocks need (bots on B and their
login address, the meshbot registration and position reset, a peer session that starts and stops B's bots and sink
around each run) and the plans and checks of accept-b (b1), calibrate (b2), b7's sink arms, crossings (b9) and J-full
(b10); then it runs them, attended, with B.

Read first: this contract; the surveys `surveys/survey8/twohost.md` and `blocks.md` (WHERE things are; line numbers as
of 0f81895); for intent `phase0_build_sheet.md` step 8 (:864-879), §2 stage 0b (:945-980), §3 gates (:1004-1043), §5
results (:1136-1171); the space reply `space_reply_2026-10-05.md` (A.4, A.5c, B.3, proposals 4-5). Where they disagree:
THIS CONTRACT, then the surveys, then the sheet.

User direction (2026-10-07): "proceed as you see fit"; B is made available on request (Relics of Imperium and
`RelicTestServer` stopped by the user). Choices the user has not seen are marked (D).

## Rules
Every step 5-8a rule binds (`step8a_design.md` "Rules"): C# 9 / .NET 4.7.1; XML docs on public members; default off and
byte-identical; log lines one physical line, invariant, no braces, never "Exception"; files under ~500 lines; one
MonoBehaviour per file; **no Mesh/ change** (TestMap/ only); `ServerMeshAutoClient.cs` is not edited; `p0tools.py` does
not grow (new keys go in `p0stage0b.STEP8_REGISTRY`); Python `unittest` tables failure-first; ruff clean; bots get
arguments, never env; the database is written directly only while the editor is out of Play, and only rig accounts;
`meshbot`/`meshbotChar` stay the regress fixture (party with meshtest, its own bag): 8b's bots are `meshbot2` and up;
no agents, edits or compile checks while a rig chain measures; only the user stops `RelicTestServer` on B.

## Decisions

### Bots on B
1. **Login address (C#).** New static class `TestMap/ServerMeshLoginAddress.cs` (no MonoBehaviour): a
   `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` installer that reads `-meshLoginAddress host[:port]` and, when
   present, sets `MMOClientInstance.Singleton.LoginNetworkManager.networkAddress` (and `networkPort`) at once. The
   bot's first scene `BaseInit-CE` holds `MMOClientInstance-CE`, whose Awake sets the singleton before AfterSceneLoad,
   and every connect (the auto client's first `StartCentralClient()`, each `OnMapStopped` reconnect after a map stop or
   migration) reads those fields (`MMOClientInstance.cs:141-171`, `LiteNetLibManager.cs:242-243`). Absent argument →
   returns (byte-identical). A pure `TryParse(arg, out host, out port)` (refuses empty host, port outside 1-65535, extra
   colons) and one line `Login address: host:port from -meshLoginAddress`; a null singleton logs `Login address error:
   no MMOClientInstance` once (the bot then fails its own login, visibly).
2. **No login-capacity key.** (Rev 0 proposed one; dropped: `LoginNetworkManager.Initialize()` already sets
   `maxConnections = int.MaxValue` at Start, `OpenMMORPG/Server/Login/LoginNetworkManager.cs:121-135`. The binding
   limits, `MaxConcurrentRequest` 150 and channel C's 200, fit 100 bots.)
3. **B's process classes (remoteb).** `IS_BOT` = `kit-ce-project.exe` with `-meshAutoClient`; `IS_SINK` as today;
   `IS_CELL` excludes both. `STOP_BOTS` kills `IS_BOT` or `*start-bots*` (the `NOT_THIS_SESSION` pattern). `deploy`
   stops cells, sink and bots. `status` adds bot and sink counts, B's clock (`Get-Date -Format o`), and whether
   `RelicTestServer` or a Relics process runs. The deploy's SHA comparison becomes a function `deployed_matches()`.
   `test_remoteb.py`'s pinned strings follow.
4. **remoteb bots.** `bots-start --first K --n N --mode M [--points FILE] [--bot-net S] [--rig-ships] [--stagger S]`,
   `bots-stop`, `bots-count`, `bots-fetch`. A pure `bot_arguments(user, mode, walk_x, walk_z, return_x, ...)` mirrors
   `regress.launch_bot` plus `-meshLoginAddress <A>:7500`; **each bot gets its own `-meshWalkX/-meshWalkZ`** (stand and
   pace first walk to that point, `ServerMeshAutoClient.cs:738-745`), from a points file the caller writes (one
   `x,z[,return_x]` line per bot). Users `meshbot<K>`..`meshbot<K+N-1>`, K ≥ 2. One `start-bots.cmd` (it creates
   `Logs\bots`) launched detached starts them with `timeout /t <stagger>` between them (default 1 s). `bots-fetch`
   clears `Logs/remote/bots/` and copies B's `Logs\bots\*.log` there. `twohost.py` gains nothing (D: remoteb is the one
   tool that drives B).
5. **Registration pass (one-off, outside measurement).** `p0load register-bots --n N`: editor Play up with no load;
   bots `meshbot2..meshbot<N+1>` started in groups of 5 in `stand` mode with their own points on the west side
   (x = -50), each group waited until every bot logged `In map at` (or 90 s), then `bots-stop`, Play stopped. A bot
   whose login fails registers itself (existing client code) and creates `<user>Char`. Logs `registered k of N`.
   Idempotent.
6. **Position reset (provision.py).** `reset_bot_positions(first, n, points)`: out of Play only (refuses like
   `db_reset`), only `meshbot<first>`..`meshbot<first+n-1>` characters on ServerMeshTestMap (`refuse_foreign_owners`
   pattern), each saved position from the run's points. Pure `bot_points(n, placement)`: `spread` = an n-point grid in
   a 100 × 100 m square centred on (0, 0) (coarse layout, cell C); `west` = a column at x = -30, z spread ±40 m;
   `pace-cut` = x = -4 (paces -4 → +4 across the cut; hysteresis 1 m, `MeshMapNetworkManager.cs:66`); `pace-west` =
   x = -44 (paces -44 → -36, never crossing).
7. **Calibration file.** The bot smoke (acceptance 3) writes `Logs/p0/bots_calibration.json` on the build: how many
   bots B hosted (100 tried; all in map within 4 min?), B's free memory, and the bots' measured upload rate per bot (Bot
   net window out pkt/s, median). b10's plan includes P 100 only when the file says 100 fit (else b10 is not planned and
   the report says "not measured: B cannot host 100 bots"); b2/b10 phantom arms take `MESH_PHANTOM_UPLOAD_HZ` = the
   rounded bot upload rate from it (so the phantom run pays the same upload work as the bot run).

### Peer session (Python, new `Tools/ServerMeshing/p0peer.py`)
8. A spec may carry `peer = {"sink": {"count": P, "kind": k, "sizes": bool} | None, "bots": {"first": K, "n": N, "mode":
   m, "placement": p, "bot_net": s, "rig_ships": bool} | None}`. `PeerSession(spec)` gives run_window three hooks:
   - `before_play()`, called after `db_reset` and before `request_play`: stops bots and sink on B and **asserts their
     counts are 0** (else raises `Stopped`, exit 3), then resets the bots' positions;
   - `on_play(play_start)`: sink-start, then bots-start on B; a failure raises, which stops Play;
   - `after_play()`: in its own try/finally around `stop_play`: bots-stop, sink-stop, bots-fetch, `remoteb fetch`; a
     failed stop marks the row not accepted ("B: 3 bots survived") and raises `Stopped` after the row is written.
   `rigrun.run_window` gains `before_play` and `after_play`; `p0blocks.run_spec` composes the peer hooks with the canary's
   `MachineProbe` (one `on_play` calling both) and `p0stepped.run_stepped` passes them too. Warm-up of a bot run =
   `stagger × N + 45 s` except b9 (decision 14). Every run with bots or a sink must end < 600 s after the first bot's map
   entry (pace stops at 600 s, `ServerMeshAutoClient.cs:749`).
9. **Peer context.** `rigrun.host_context(peer=True)` adds `peer_host`, `peer_cpu` and the A-B clock offset from one
   SSH query (cached per invocation; unreachable → exit 2). B-stamped lines (Sink, Bot net) are shifted by the offset
   before they are selected against A's window; a window within 2 s of an edge is dropped.
10. **Lifting the 8b refusals.** `FAMILY_GAME` gains accept-b, calibrate, crossings, jfull (fantasy; packets keeps its
    own); `run_row` stops refusing them; `p0checks._later_8b` and `p0plans.STAGE_LATER`/`later_refusal` lose them;
    `p0load` gains `accept-b`, `calibrate`, `crossings`, `jfull`, `register-bots` and `packets --sink`; the refusal tests
    are rewritten. **All 8b families get canaries** (`CANARY_FAMILIES`; B idle during canaries: the session stops B's
    processes before each canary). Every 8b subcommand refuses to start (exit 2, before Play) when B is unreachable,
    runs a cell, runs `RelicTestServer`/Relics, or holds a build whose SHA differs from A's.

### Blocks (plans in a new `Tools/ServerMeshing/p0plans8b.py`; checks in a new `p0checks8b.py`)
11. **b1 accept-b** (19 runs, ~1.1 h), graded like accept-a (`p0accept`):
    - (A) virtual vs sink: `virtual:50:character:C`, then `sink:50:character:C`; Send `wire` within ±3 % of Sink `in`
      (2 runs).
    - (B) eye hiding with a real bot: `virtual:50:eye:C` with `MESH_PHANTOM_VIEW=200` (the step 5 test, V_move within
      ±10 % of 201) plus 1 bot on B placed at the eye group's centre (taken from the Phantoms installed line; if no line
      names the centre, add one in TestMap/Phantoms); eyeSeen 0, and the bot sees no eye entity (Bot net players seen ≤
      the real count) (1 run).
    - (C) sender byte-exact with bots: `accept_step6` gate 6 as it is (50 synthetic players) plus 2 bots on B, kit vs
      sender, 2 pairs; records/s within ±2 % (4 runs).
    - (D) a ship bot on B (`-meshRigShips`) sees ≥ 45 ships (1 run).
    - (E) CE-12 reports the post-pass shape (`shape=pass`, the two consumed sets never overlap; sheet §0.9 as of
      ff25dea; 1 run, no B).
    - (F) meter overhead < 1 %: `MESH_METER_SENDS` off/on × 5 pairs (`p0accept.overhead`, t(4); 10 runs, no B).
12. **b2 calibrate (J-lite)** (4 runs, ~0.5 h): arms kit and char-sender (`MESH_SENDER=on`, tiers 20/20/20) × {50 bots
    on B pacing at `spread` points; 50 sink character phantoms on B in the same 100 × 100 m square,
    `MESH_PHANTOM_UPLOAD_HZ` from the calibration file}. Gate J per arm (`p0checks8b.gate_j`): **work** — the phantom
    run's C tick work (minus the Phantom window's rig ms, which production would not pay) within ±15 % of the bot run's;
    **bytes** — Send wire kB/s per client (divided by the Work window's `clients`) within ±5 %. Both runs report V (Bot
    net players seen vs Phantom vmove) and uploads/s; a V difference over 20 % makes J "not decided" rather than FAIL.
    Verdicts are reported, never failing the block. Runs before any fan-out re-run (sheet :1001).
13. **b7 sink arms** (21 runs, ~1.2 h).
    - (a) Cap: ship-sender-28 tiered, `sink:100:ship:C` + `C:400:ai`, V 500, scale 0.5, packet cap {1,020, 510, 255} ×
      3 (9 runs). P 400 is not planned (it saturated A's main thread in 8a's virtual form; reported "not measured").
    - (a') The same 9 points virtual on the 8b build (decision 17 changes the ships' reliable behaviour, so 8a's virtual
      cap rows do not compare): per-packet cost = (sink − virtual) per datagram (9 runs).
    - (b) Network-thread probe: `sink:<P>:eye:C`, P {100, 200, 400, 800}, one unstepped run per P (sink mode refuses
      steps, `ServerMeshPhantomSettings.cs:535-536`), char-sender tiered watching V 100 load characters,
      `MESH_THREAD_CPU=1`, 1 repeat each (4 runs; a second repeat only where the saturation verdict sits within 10 % of
      its threshold): LiteNetLib thread ms/s, datagrams/s, wire kB/s per P; "saturated" (reported, never failed) where
      the thread reaches ≥ 900 ms/s or datagrams/s rise < 10 % for a doubling of P, computed across the runs. The main
      thread is expected around 430-500 ms/s at P 800 (8a has the kit-rules sender at 248 for P 400, V 100; tiered was
      not run at V 100); a run that saturates the main thread says so and stops the probe there.
14. **b9 crossings and wake** (18 runs, ~1.2 h). Layout halves (cut x = 0), 30/0.02.
    - **Latency (walk)**: bots at `west` points, walk mode to x = +30 and back (two crossings per bot); arms {20, 50}
      bots × {neighbour awake; neighbour asleep: `MESH_SLEEP=1` with `MESH_SLEEP_RINGS=0`, the regress `wake` pattern} ×
      3 (12 runs). The measurement opens 10 s after Play is up and ends by `stop_when` once the origin cells logged 2N
      handoff outcomes (ACK, ROLLBACK or failure) or after stagger × N + 120 s. `latency_ms{handoff_ready}` p50/p95/p99
      from `Handoff ACK <name> by zone <z>: registered <a> ms after the notice, <t> ms after START` (t; a new parser in
      `p0cells0b`; origin clock, no skew); failures and rollbacks are counted and reported beside it, never dropped.
      `wake` = the first ACK's stamp minus C's `Wake requested for sleeping cell E` stamp (both on A). Asleep runs are
      exempt from E's coverage check. Gate Crossings (sheet :1035) half A: handoff_ready p95 ≤ 1.0 s with 50 bots; half
      B (rate vs B5) waits for stage 0c.
    - **Cost per crossing (pace)**: 20 bots at `pace-cut` (each crosses about every 2-3 s) vs 20 bots at `pace-west`
      (no crossing), interleaved, × 3 (6 runs; warm-up stagger × N + 45 s, measure 60 s). ms per crossing = (C + E work
      ms/s, pace-cut − pace-west) ÷ crossings/s, with its interval over the 3 pairs. Each migration also reconnects the
      bot to A's login server (`OnMapStopped`); that work lands on the editor's login, not on C or E, and is noted.
15. **b10 J-full** (4 runs, ~0.5 h): b2's two arms at P 100 × {bots, phantoms}; P 50 is b2 itself (same build). Planned
    only when the calibration file says B hosts 100 bots. Gate J as b2.

### Ship stand-ins follow the space game's main (TestMap/Ships; space reply A.5c, B.3)
16. **IsAtRest** (the space rule, `ShipMovementComponent.cs:136-148`, thresholds `:86-87`, commit 771ad38c): linear
    speed² < 1e-4 and angular speed² < 1e-6 and no teleport pending. Sender (`ServerMeshShipMovement_Sender.cs:32`):
    drop the Parked gate; velocities from the dynamic rigidbody while simulating, else the last received ones
    (`_netVelocity`, `_netAngularVelocity`), never the zeroed record velocity. Puppet body (`ServerMeshShipPuppetBody.cs:
    67`): add the angular term; it has no pending term (a puppet never sends reliably, `:178`). Nothing in Mesh/ reads
    IsAtRest (only its definitions), so no 8a figure moves.
17. **Unowned ships acknowledge at the teleport** (space commit bcbe9754, `ShipMovementComponent.cs:369-389`). In
    `TeleportShip`, right after the sequence bump: `if (!_selfAck && ConnectionId < 0) _ackedSequence =
    _teleportSequence;` so the next record goes **unreliably** (DiscreteVersion still bumps) and compact28 is allowed at
    once. A new partial adds `OnSetOwnerClient(bool)`: on the server, when the ship becomes unowned (`ConnectionId < 0`)
    with a teleport pending and `!_selfAck`, acknowledge. "No owner" is exactly `IsServer && ConnectionId < 0`: ships fed
    by the mirror feeder count as unowned; phantom-owned ships stay reliable until their upload echoes; an upload
    echoing an older sequence makes the teleport pending again (`ReadClientStateAtServer`, kept). `MESH_SHIP_SELF_ACK=1`
    takes precedence: no immediate ack, the 2-tick self-ack as before (doc at `ServerMeshShipSettings.cs:97` updated).
    EditMode tests pin: unowned teleport → acked, next record unreliable; owned → pending; SELF_ACK=1 → pending for 2
    ticks; owner-leave → acked (only EditMode reaches this: the rig's one owner-leave, `ReleaseShip`, destroys the ship
    in the same call). 8a's ship rows were measured under the step 7b rule (an AI ship respawned in a fight stayed
    reliable for the rest of the run); the stage 0b report says so and b7 (a') re-runs the comparison.

### Results
18. **Rows directory: deferred.** (Rev 0's `--rows-dir` is cut: rows outside `Logs/p0` are invisible to `read_history`.
    The space team's S8 rows get their own decision when they are due.)
19. Every 8b row carries `peer_host`, `peer_cpu`, the clock offset, and the bot or sink counts reached (Phantom window
    `bound`; the Work window's `clients`); `load_fields_0b` reads `clients.real` from the Work window instead of 0. A bot
    run whose cell saw fewer than N clients in more than one window is not accepted ("C: 47 of 50 bots on the cell").
20. **Report gates.** `p0report.PENDING_GATES` relabels what 8b leaves pending (G1-F, G2-F, G3-F (O) and the space
    gates stay "stage 0b report", not "step 8b"); new gate rows for J (b2, b10) and Crossings half A (b9).

## Files
- C# (TestMap): new `ServerMeshLoginAddress.cs`; `Ships/ServerMeshShipMovement.cs` (the TeleportShip ack),
  `Ships/ServerMeshShipMovement_Owner.cs` (new partial: OnSetOwnerClient), `_Sender.cs` (IsAtRest),
  `ServerMeshShipPuppetBody.cs` (IsAtRest), `ServerMeshShipSettings.cs` (doc); Phantoms only if the eye-group centre is
  not logged; EditMode tests in `Tests/Editor/` for each.
- Python: `remoteb.py`, `provision.py`, `rigrun.py` (hooks, peer context), `p0blocks.py` and `p0stepped.py` (hook
  composition), new `p0peer.py`, `p0plans8b.py`, `p0checks8b.py`; `p0cells0b.py` (handoff outcome parser, wake stamps,
  B-clock shift), `p0results.py` (`FAMILY_GAME`, `run_row`, `load_fields_0b`), `p0checks.py` (`_later_8b`, coverage
  exemption), `p0plans.py` (`STAGE_LATER`), `p0load.py` (subcommands), `p0report.py` (gates); tests: `test_remoteb`,
  `test_provision`, `test_rigrun`, `test_p0blocks`, new `test_p0peer`, `test_p0plans8b`, `test_p0checks8b`, and the pins
  in `test_p0checks`, `test_p0plans`, `test_p0plans0b`, `test_p0report`, `test_p0results`.
- Docs: this contract's log; HANDOFF status.

## Acceptance (8b)
1. Unit tests and EditMode tests pass; ruff clean; the stray-reference check and the package build pass.
2. `p0load <cmd> --dry-run` for every new subcommand prints the plans above (run counts as stated).
3. With B (attended): `remoteb deploy`; `bots-start --first 2 --n 3` reaches the map from B (In map lines fetched);
   `register-bots --n 100`; the capacity and upload smoke writes `bots_calibration.json`; then one smoke run per
   subcommand (`accept-b` part A, `calibrate` one arm, `packets --sink` one cap point and one probe P, `crossings` one
   20-bot walk run) with rows to `Logs/p0/accept8b/` (never counted).
4. Then the blocks: b1, b2, b9 in one attended session; b7; b10 last. A fresh baseline on the new build first.

## Critique log
- **Rev 0 → 1 (2026-10-07), C# and kit review:** the login-capacity key was moot (the kit sets int.MaxValue at Start)
  → dropped; the space game acknowledges an unowned ship *in* Teleport, so no reliable record goes at all, not "for
  that tick" → the ack moves into TeleportShip, compact28 allowed at once; "owner leaves" was not observable → an
  OnSetOwnerClient partial, EditMode-only; IsAtRest's exact changes (thresholds, Parked gate, velocity source); the
  login-address polling was unneeded (the singleton exists before AfterSceneLoad); each migration reconnects to login.
- **Rev 0 → 1, tooling and design review:** sink refuses steps and stepped runs had no peer hooks → b7's probe is one
  unstepped run per P; gate J's upload term would double-count (sink peers upload for real) and `upload_in_us` exists
  only for ship mirrors → phantoms upload at the bots' measured rate, bytes per Work-window client; b9's window missed
  the walk → stop_when on 2N outcomes, plus a separate pace-cut vs pace-west cost measurement, wake from C's Wake line,
  rollbacks counted, `MESH_SLEEP_RINGS=0`, asleep coverage exemption; `db_reset` re-provisions meshbot at (0, 0) →
  bots start at meshbot2, `before_play` after `db_reset`; stand/pace collapse a spread → per-bot walk points; host mode
  has no eye group → VIEW 200; leftovers on B were never caught → asserted at `before_play`, survivors raise; Files
  gaps (FAMILY_GAME, run_row, `_later_8b`, `load_fields_0b`, report gates, canaries for all 8b families, test pins);
  remoteb status lacks Relic/ports → added, SHA check factored; registration waits for map entry, not RESULT; B clock
  offset; b7 main-thread estimate corrected and a virtual re-run on the 8b build (a'); b1 C keeps gate 6's 50 players,
  F uses 5 pairs; `p0tools` frozen; `--rows-dir` cut; b10 P 50 reuses b2, P 100 gated by a persisted calibration file.
