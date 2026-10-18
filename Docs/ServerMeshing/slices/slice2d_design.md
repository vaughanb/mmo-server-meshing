# Dynamic cells, Slice 2d: supervision and fencing (implementation contract)

Revision 1, 2026-10-17 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end) of revision 0, itself from three surveys (central, map side, harness) of HEAD 91a2663 (Slice 2c, DESIGN
0.77). 2b split a cell and 2c merged it back; both assume every process stays up. 2d makes the dynamic cells survive
crashes: **a dead child is restarted with the same id and rectangle, or collapsed into its base after repeated
failures; a cell cut off from the cluster while its neighbours are not fences itself (saves, kicks, dies) so no two
processes ever own the same rectangle; a hung or stale process is killed by its spawner; an op whose member crashes
after its commit pauses and resumes instead of freezing the world; a base that crashes with children live comes back
without the children's rectangles, and its monsters are released and respawned exactly once**. Automatic policy (3),
buildings (4) and central-restart recovery (5) are later slices.

Read first: this contract; `slices/slice2b_design.md` and `slices/slice2c_design.md` (their decisions hold unless
changed here); the plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D3 supervision and fencing (:170-174), D8 failure
handling (:317-326), Slice 2d (:420-425), the per-slice gate (:328-340). Where they disagree: THIS CONTRACT, then 2c's,
then 2b's, then the plan.

User decisions (2026-10-17): start 2d. Choices the user has not seen are marked (D).

## Rules
As 2b/2c: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical**
(everything below is gated on a cell of a Dynamic map, an op in progress, or a fault toggle that is set); log lines one
physical line, invariant, no braces, never "Exception"; files under ~500 lines: `MeshMapNetworkManager.cs` (1,143) and
`_Ghosting.cs` (3,723) get one-line hooks only, `_Layout.cs`, `_StaticHandoff.cs` (517) and `MeshLayoutCoordinator_Split.cs`
(474) no new code (pause, abort-on-loss and collapse go to `MeshLayoutCoordinator_Supervision.cs`); kit edits only as
numbered CE patches — **an implementer that finds one necessary stops and reports instead of making it** (the user
decides); no agents, edits or compiles while a rig chain measures; commits only when the user asks.

## Settings (new `MeshDynamicSettings` fields)
`childDownGraceSeconds` 20 (D: the plan's 15), `childMaxRestarts` 3, `childRestartIntervalSeconds` 10 (the least time
between two attempts), `childRestartStableSeconds` 60, `childHungSeconds` 30 (a registered cell that sends no load
report this long is hung), `childFenceSeconds` 10, `fenceSaveSeconds` 5, `retireKillAfterSeconds` 270 (D). Grace
arithmetic, all measured from the moment the cell lost the cluster (the 5 s disconnect timeout delays both sides
alike): a fence finishes within `childFenceSeconds + fenceSaveSeconds + 1` (16 s; it ends in `Process.Kill`, no kit
quit path), the central's clock lags by up to 2 s of tick skew, and a restarted process needs its boot time before it
registers; so the grace must be ≥ 18 s. The validator logs an error when `childDownGraceSeconds < childFenceSeconds +
fenceSaveSeconds + 3`.

## Decisions

### Supervision (central; `MeshCellSupervisor` pure, `MeshLayoutCoordinator_Supervision.cs`)
1. **Watched cells and "down".** Every cell of a Dynamic map's split family that is live in the broadcast (Active and
   owning something) — children (`isDynamic`) and the family's base — except a merge's retiring cell after e2. A cell
   is **down** when unregistered continuously for `childDownGraceSeconds` with its cluster key absent, or **hung** when
   registered but silent (no `MeshLoadReport`) for `childHungSeconds`. A base outside sleep mode is restarted by the kit
   (`autoRestart`), so the supervisor only waits for it; a base in sleep mode and every child are restarted by the
   supervisor (today a crashed split-family base in sleep mode is never restarted: `AwakeTick` skips split families).
2. **Restart.** `Supervisor: <cell> down <s> s, restart <n> of <max> requested` (or `hung <s> s`). First the down
   incarnation joins the **fenced set** (decision 5), and any process of the cell's channel is killed through its
   spawner (decision 4); then `IMeshSplitHost.RequestRestart(cellId, baseId, mapId, out error)` (2b's spawn without
   `AddChannel`; its own callback route). Attempts are at least `childRestartIntervalSeconds` apart. A restart
   **succeeds** only on a hello from a **new** incarnation (newer than every fenced one): `Supervisor: <cell> back,
   incarnation <inc>`; the kit's spawn callback alone proves nothing (it fires on any registration of the key). It
   **fails** (`Supervisor: <cell> restart <n> failed (<why>)`) on a spawn error, no new-incarnation hello within
   `splitStepTimeoutSeconds`, or the cell going down again within `childRestartStableSeconds`; the next attempt first
   kills the channel's processes, so a late process of a failed attempt never lives beside a new one. The count resets
   after `childRestartStableSeconds` up. The restarted cell's hello is answered with the current layout (its staged
   step, if its op stages to it).
3. **Collapse.** After `childMaxRestarts` failures of a child: `Supervisor: <cell> collapse queued after <n> failed
   restart(s)`; when the world-wide op slot is free, 2c's merge with `collapse=true` into the base: `CheckOpCommon`
   accepts the unbound, unregistered child and ignores the family's cooldown; no drain evidence, no Retire, no exit
   wait; the pending-settle refusal is lifted (a split that ended at e2 leaves the base's interest wide); the abort on an
   unregistered retiring cell before e2 is suppressed; at the drain gate the cell is already gone, so 2c's `<cell>
   exited before drained` line is expected; then channel removal, e3, tidy, done. Status `Merge <op>: requested <base>
   <cell> collapse`. When `TryUnion(base, cell)` fails (the cell does not share a full edge with the base): `Supervisor:
   <cell> cannot collapse (Slice 3); left down`, an error note to the base once a minute, no more restarts (D). A base is
   never collapsed; a base that keeps failing is left to the kit and logged.
4. **Spawner kill (new cluster message 30020 `KillChannel` {channelId, mapId, processId, incarnation, reason}, central →
   the base's spawner).** The kit keeps its children's pids in a private set with no channel (`MapSpawnNetworkManager.cs:83`),
   so no kit edit: every map server's `MeshLayoutHelloMessage` gains a trailing `processId` (its own pid; 0 when unknown),
   the central keeps it in the binding, and the spawner kills `Process.GetProcessById(processId)` only when that process
   is the build's executable and its start time lies within 10 s of the incarnation's ticks (pid reuse):
   `Spawner: killed <channelId> pid <pid> (<reason>)`, or `Spawner: kill of <channelId> pid <pid> refused (<why>)`. A cell
   that never said hello has no known pid; its failed attempt is left to the kit (no auto-restart) and logged. Used by the restart (decision 2), for a **hung** cell, by the
   operator verb `<seq> kill <cell>` (`MeshLayoutCommandKind.Kill`; D), and automatically when a merge's retire step is
   older than `retireKillAfterSeconds` (2c's hung-quit limit; the merge then sees the cell exit). A Retire cannot stop a
   hung main thread: LiteNetLib's own thread keeps the cell registered while Unity's main thread is stuck (critique).
5. **Incarnations only move forward.** The coordinator never rebinds a cell to an incarnation older than its binding's
   or one in the fenced set (incarnations are hex UTC ticks): such a hello is answered with `Retire` reason `fenced:
   stale incarnation`. Every Retire the central sends to a cell that may own something (a stale or fenced incarnation, a
   child aborted after its staged e2) carries a reason starting `fenced:`. Neighbours (map side) ignore a `MeshHello`
   from a zone of a Dynamic map whose incarnation is older than the one they last saw (today any change, back or
   forth, releases records — a zombie's hello would release the live incarnation's). The fenced set is kept in memory
   and in the store (an optional field; format version unchanged; normalised when null).
6. **Ops and their members' crashes** (replaces 2b's freeze on a parent restart and on a child restart after e2, and
   2b's 60 s wait for a dead child). The op's commit is its **e2 broadcast** (a split in `E2Staged` has not committed:
   nobody but the staged target saw e2).
   - **Before the e2 broadcast**, a member unregistered for 5 s, or back with a new incarnation, aborts (2b/2c's abort,
     now prompt): `<Kind> <op>: aborted <cell> lost`; a split's abort Retires its child with `fenced: aborted` and logs
     `Split <op>: channel <child> removed` (mirrored; today only the host logs it); an aborted child is not restarted.
   - **After the e2 broadcast**, a member that is down (a split's parent or child, a merge's survivor) pauses the op:
     `<Kind> <op>: <cell> lost, op paused`; while paused no step timer runs (the split's `SplitTimers`, the merge's tick,
     the drain deadline — they are shifted by the paused time; `retireSentAt` and `nextStraggleAt` too); the supervisor
     restarts the member (it watches an op's own child despite the op); on the member's new-incarnation hello the
     binding is rebound **before** any staged step is re-answered (today `AnswerStaged` targets the old incarnation),
     `<Kind> <op>: <cell> rebound <inc>, op resumed`, the binding's evidence is cleared, `e2AckedAt` resets to −1, and the
     step's gate restarts. A return with the **same** incarnation within the grace (a blip) resumes without a gate
     restart: `<Kind> <op>: <cell> back, op resumed`.
   - **A split whose child is queued for collapse** ends: `Split <op>: ended, <child> collapsing` (a terminal, not
     frozen, state that frees the op slot); if the child cannot collapse the split ends the same way and the child is
     left down (decision 3).
   - **Non-member participants** that unregister are dropped from the step's participants (they catch up from the hello
     answer).
7. **Id reuse (tightens 2c decision 7).** A child id may be named again (split) or restarted only when its key is absent
   and it has been unregistered for at least `childFenceSeconds + fenceSaveSeconds + 1`, or its process was killed by
   its spawner. The store's `abortedIds` are loaded at start (today never read back; the first save erased them).

### Fencing (map side; new partial `MeshMapNetworkManager_Fence.cs`, pure `MeshFenceRule`)
8. **Registration witnesses.** Every cell of a Dynamic map sends each linked neighbour a new link message 130
   `RegistrationState` {registered} every 2 s and on change. A neighbour whose state is "not registered" is not chosen
   as a handoff target (its inbound handoffs pause; existing handoffs toward it follow their guards) — Static maps send
   and read nothing.
9. **Self-fence.** A cell of a Dynamic map (base or child) fences when, **after its first successful registration**:
   - it has not been registered for `childFenceSeconds` (measured from the last tick at which it was registered, so a
     process resumed after a suspension fences at once) **and** at least one linked neighbour reported "registered"
     within the last 5 s (a witness: with the central down every cell is unregistered and nobody fences — the
     restarted central cannot restore them until Slice 5, so staying up is safer), or
   - it got 3 consecutive registration refusals (`AckResponseCode.Error`: the kit's only refusals are MAP_EXISTED and a
     bad server hash) after having been registered before (a fresh process refused because a zombie holds its key just
     retries), or
   - it receives a `Retire` for its id and incarnation whose reason starts `fenced:` (any state; 2c's retire paths are
     unchanged for other reasons).
   A process that never registers is the supervisor's (decision 2: no hello → failed attempt → killed).
10. **`FenceThisCell(reason)`** (a `fenced:` reason bypasses `_retiring`, so it also stops a cell already quitting):
    `Fence: <cell> incarnation <inc> fenced (<reason>): saving <n> player(s)`; close the mesh link server and clients and
    stop accepting connections; save every registered player through the kit's save-and-despawn-immediately path, all
    awaited together, bounded by `fenceSaveSeconds`; kick each (`UI_ERROR_KICKED_FROM_SERVER`); `Fence: <cell> saved <n>,
    kicked <m>, exiting`; after 0.5 s `Process.GetCurrentProcess().Kill()` (not `Application.Quit()`: the kit's quit path
    re-saves everything with its 30 s request timeout each, after the players re-logged in elsewhere). A kicked client
    re-logs in and is routed by position.
11. **Base crash with children live.** The restarted base is gated until the central's answer; while gated it holds the
    asset layout and simulates the monsters its spawn areas make, but hands nothing off (existing gate); the answer
    gives it P∖R. The child releases every borrowed monster of the base's old incarnation (existing: link loss when
    down both ways, the unreachable sweep, or the base's new-incarnation hello: `Borrowed monster <id> released (home
    zone link lost|unreachable|restarted)`) and its borrowed statics on the new hello (existing; no link-loss release —
    it would duplicate on a one-way loss); after the gate opens the base hands R's new monsters to the child by position
    (existing). New line on a Dynamic map's base once its kept monster areas are set up: `Spawn areas of <cell>: <n>
    monster(s) planned in the base rectangle` (sum of their max amounts; the scenario's area has min = max).

### Faults and the bots (rig)
12. **Fault toggles** (a shared `MeshFault.FireOnce(key, zone, what)`, the existing marker scheme `Temp/ServerMesh.
    FaultFired.<key>.<zone>` written before `Process.Kill()`, `FAULT: dying on <what> (<key>)` at error level; the harness
    already clears `ServerMesh.FaultFired.*`; the existing notice fault moves onto the helper with `<what>` = `handoff
    notice for <id>`, so `crash`'s grep still matches): `MESH_FAULT_DIE_ON_PREWARM=<cell>` fires on the first
    **Broadcast**-kind layout naming the cell Pending, before it acks (so the e1 broadcast is out and the abort logs
    `retired broadcast`); `MESH_FAULT_DIE_ON_DRAIN=<cell>` fires on a child's first incoming character HandoffNotice
    after it applied an owned rectangle (the split's drain). Both join `regress.py`'s `ENV_KEYS`.
13. **`MESH_MONSTER_AREAS=only:<names>`** (D) switches on the named areas and **off every other monster area** (E's
    `Spawn_Boundary_X100_Z60` lies on x = 100, C.1's outer edge), so a scenario's monster total is exact.
14. **Bot re-login (TestMap).** The auto client's inline login flow (`ServerMeshAutoClient.cs` ~:400-593) is extracted
    into a reusable coroutine in a partial file. When the map connection is lost outside a migration (the non-migration
    branch of `MeshMapNetworkManager.OnClientDisconnected`), or a migration ends in `FailMigration`, the bot logs `Session
    lost (<why>); re-login <n>`, retries login → channels → select (while the cluster still lists the user on the dead
    cell, about 5 s), logs `Re-login routed to <ip:port>`, and resumes its mode; the stand window keeps running; RESULT
    gains ` relogins=<n>` (`rigsplit.RESULT_FIELDS`).

### Scenarios (`Tools/ServerMeshing/regress_splitcrash.py`, `rigsplitcrash.py`)
15. **`start_split` gains** `bots=`, `load_plan=`, `enabled=`, `env=` and `grade_status=` (a callable or None) so these
    cases can reuse it; `rigsplit.op_outcome` knows `ended`; `rigmerge.REQUESTED` accepts a trailing `collapse`; a
    `rigsplitcrash.parse_supervisor(lines, seq)` reads the Supervisor lines; a per-lifetime apply grader takes
    `[[(owned, interest, pair)]]` per cell and checks rising versions within each lifetime; the PlayRunner mirrors lines
    starting `Supervisor:` (they go through the coordinator's `OpLine`). `regress.py` dispatches the cases' runs and
    layout grades, registers them in `ALL`, `TWO_HOST_SKIP` and `PERSIST_SKIP`, and adds both fault keys to `ENV_KEYS`.
16. **`splitcrash_prewarm`**: no harvest bot, no synthetics, `only:Spawn_Interior_C`, `MESH_FAULT_DIE_ON_PREWARM=C.1`,
    stand window 180 s. After the split command, wait for `aborted` (90 s; expected ~10 s after the FAULT). Checks: one
    FAULT line on C.1; status `e1 staged`, `e1 acked`, `e1 broadcast`, `aborted C.1 lost`, `retired broadcast`, `channel
    C.1 removed`, no done; no `-channelId C.1 ` process for 30 s after the abort; applies: C first, e1 (P,P), retired
    (P,P); other cells first, e1, retired; C.1 one lifetime, one apply (∅, R); C and the neighbours log `Retired zone
    C.1: link closed, 0 record(s) released`; every bot `migrations=0`, `relogins=0`.
17. **`splitcrash_drain`**: no harvest bot, no synthetics, `only:Spawn_Interior_C`, `MESH_FAULT_DIE_ON_DRAIN=C.1`, stand
    window 300 s. Wait for `op paused` (120 s), then `done` (240 s in all; a drain timeout plus settle is a note). All
    four R-bots are warped toward C.1 within about a second (C drains at 5/s and sees the link up for ~5 s), so per R-bot
    with f = its `fellBack=True` count (0 or 1): Σf ≥ 1 and equals C's `Handoff ROLLBACK … -> zone C.1` count;
    `migrations = 1 + f` and C's `Handoff START <char> … C->C.1` count is `1 + f`; its last MIGRATION `ready=True` at x ≥
    0; C logs `Handoff ACK <char> by zone C.1` after the Supervisor `back` line (not by port: the restart gets a new one);
    no maxGap check. Also: one FAULT line on C.1; status `C.1 lost, op paused`, `Supervisor: C.1 down … restart 1 of 3
    requested`, `Supervisor: C.1 back, incarnation <inc>`, `C.1 rebound <inc>, op resumed`, `done`; C.1 process 1 → 0 →
    1; applies: C the split's four; C.1 lifetime 1 (∅,R,e1), (R,R,e2), lifetime 2 (R,R,e2), (R,R,e3); others four each;
    P-bots `migrations=0`; 0 teleports, 0 exceptions.
18. **`splitcrash_base`**: no harvest bot, no synthetics, no tester (`enabled=False`), R-bots **and** P-bots (meshbot6..9
    stand on C), `only:Spawn_Interior_C`, stand window 300 s. After `done` + 10 s the harness kills C by `-channelId C `
    (the kit restarts it; MAP_EXISTED until the old key drops, ~5 s). The held set: from C.1's own lines up to the kill
    second, `Monster (promoted|spawned) by handoff <id> C->C.1` minus `Monster handoff <id> C.1->C at` minus `Borrowed
    monster <id> died here`, corrected for `failed there; re-promoted`, ids with C's old incarnation head. Checks: C two
    lifetimes, its second's only apply (kept, kept) with the e3 or settle pair; C logs `Spawn areas of C: 5 monster(s)
    planned in the base rectangle`; every held id has a `Borrowed monster <id> released (…)` line on C.1 within 30 s of
    the kill, or was handed back in the kill second (a note when the held set is empty, ~1 run in 32); within 90 s of
    C's answer, pairing each of C's second-lifetime work windows with C.1's latest window stamped within 5 s of it, the
    owned monsters sum to 5 for 3 consecutive pairs and never exceed 5 for 3 consecutive pairs; borrowed statics of C's
    old incarnation released on C.1 (a note); R-bots `migrations=1`, `relogins=0`; P-bots `relogins=1`, a `Re-login
    routed to` line, and ending on C (C's second lifetime registers them, possibly after a hop through N: 0 or 1
    migration).
19. **Graders allow per case**: `aborted`, `retired broadcast`, the pause/resume and Supervisor lines, the FAULT line on
    the faulted cell, the base case's release lines and C's second lifetime; everywhere 0 exceptions and 0 player
    teleports. Other scenarios keep failing on any of them.

### Acceptance
20. EditMode tests (the supervisor: down and hung, key presence, the interval, success only on a new incarnation, the
    stable window, collapse after the budget, `cannot collapse`, sleep-mode bases, kill before each attempt; collapse as
    a merge with its relaxations; abort on loss before the e2 broadcast incl. `E2Staged`; pause, timer shifts, rebind
    before re-answer, blip resume, a split ending for a collapse; non-member drop; the kill command and the retire kill;
    stale incarnations refused; id reuse timing; `abortedIds` loaded; `MeshFenceRule` incl. the witness and the
    registered-before rules; neighbours ignoring an older hello; `RegistrationState` codec and target exclusion; the
    fault predicates; `only:` parsing; all 2b/2c tests unchanged); Python tests and `py_compile`; ruff; zero new
    warnings; the stray check; the 11 scenarios Static and `all:Dynamic`; `split`, `splitc2`, `merge`, `mergec2`,
    `splitcrash_prewarm`, `splitcrash_drain`, `splitcrash_base` 3/3 each (flake policy: one rerun, recorded); `crash` in
    both modes; DESIGN 0.78; package 0.78.0; SETUP (settings, the kill command, the fault toggles, `only:`, the
    scenarios, the fence and witness rules).

## Interfaces and log lines (exact)
- **Command file:** `<seq> kill <cell>` (`MeshLayoutCommandKind.Kill`, field `cellId`).
- **Central:** `Supervisor: <cell> down <s> s, restart <n> of <max> requested`; `Supervisor: <cell> hung <s> s, restart <n>
  of <max> requested`; `Supervisor: <cell> back, incarnation <inc>`; `Supervisor: <cell> restart <n> failed (<why>)`;
  `Supervisor: <cell> collapse queued after <n> failed restart(s)`; `Supervisor: <cell> cannot collapse (Slice 3); left
  down`; `Supervisor: <cell> killed by its spawner (<why>)`; `<Kind> <op>: aborted <cell> lost`; `<Kind> <op>: <cell>
  lost, op paused`; `<Kind> <op>: <cell> rebound <inc>, op resumed`; `<Kind> <op>: <cell> back, op resumed`; `Split <op>:
  channel <child> removed`; `Split <op>: ended, <child> collapsing`; `Merge <op>: requested <base> <cell> collapse`.
  `<Kind>` is `Split` or `Merge`. Spawner: `Spawner: killed <channelId> pid <pid> (<reason>)`.
- **Map side:** `Fence: <cell> incarnation <inc> fenced (<reason>): saving <n> player(s)`; `Fence: <cell> saved <n>, kicked
  <m>, exiting`; `Spawn areas of <cell>: <n> monster(s) planned in the base rectangle`; `FAULT: dying on <what>
  (<key>)`; `Neighbour zone <Z>: hello from older incarnation <a> (current <b>) ignored`.
- **Wire:** cluster 30020 `KillChannel`; link 130 `RegistrationState`; `MeshLayoutHelloMessage` trailing `int processId`.
- **Auto client:** `Session lost (<why>); re-login <n>`; `Re-login routed to <ip:port>`; RESULT ` relogins=<n>`.

## Files
Mesh/: new `MeshCellSupervisor.cs`, `MeshLayoutCoordinator_Supervision.cs`, `MeshMapNetworkManager_Fence.cs`,
`MeshFault.cs`; edits to `MeshLayoutCoordinator.cs` (rebind rule, the tick hook), `MeshLayoutCoordinator_*Steps.cs`
(pause hooks), `MeshMergeOperation.cs` (collapse), `MeshLayoutOperation.cs` (pause state), `MeshCoordinatorTypes.cs`
(`RequestRestart`, `KillChannel`), `MeshCentralNetworkManager_Split.cs` (restart spawn, kill dispatch),
`MeshMapSpawnNetworkManager.cs` (process records, kill), `MeshCoordinatorStore.cs` (fenced set, `abortedIds` load),
`MeshLayoutCommand.cs` (kill), `MeshLayout.cs` (settings), `MeshLayoutValidator.cs` (the grace rule), `MeshSleep.cs`
(30020), `CellMeshLink.cs` (130), `_Drain.cs` (Retire `fenced:`), `_Pacing.cs` (the prewarm fault), `_SplitEntities.cs`
(the planned-monsters line), one-line hooks in `MeshMapNetworkManager.cs` (registration responses, fence tick) and
`_Ghosting.cs` (`only:` areas, the drain fault, the notice fault through `MeshFault`, the older-hello guard, the
target exclusion). TestMap: an AutoClient partial (login flow extracted, re-login), `Editor/ServerMeshLayoutCommandFile.cs`
(`Supervisor:` prefix). Tools: new `regress_splitcrash.py`, `rigsplitcrash.py`; `regress_split.py` (start_split
parameters), `rigsplit.py` (`ended`, `relogins`), `rigmerge.py` (`collapse`), `regress.py`; tests.

## Critique log
- **Rev 0 → 1 (2026-10-17), protocol and Mesh/ review:** a hung main thread stays registered (LiteNetLib's own thread
  pings), so a Retire can fence nothing and the supervisor never fires → hung = registered but silent, killed by its
  spawner (30020), and the fence command became a kill; a suspended process resumed with a stale edge clock → the clock
  runs from the last registered tick; stale incarnations were re-adopted, their Retires ignored ("cannot retire a
  running cell"), and a zombie's hello released the live incarnation's records → incarnations only move forward,
  `fenced:` Retires, neighbours ignore older hellos, restarts succeed only on a new incarnation and kill first; the
  fence's Quit re-saved everything unbounded and the second save could land after a re-login → save once bounded, kick,
  `Process.Kill`, and the grace arithmetic redone; supervised bases could split-brain and a central outage would fence
  every child → bases fence too, with a registered-neighbour witness required (no witness when the central is down);
  `DIE_ON_PREWARM` raced the staged ack → it fires on the first Broadcast naming the cell Pending; "e2" defined as its
  broadcast (`E2Staged` aborts); pause mechanics (rebind before re-answer, timers suppressed, blip resume, `e2AckedAt`
  reset, the other merge timers); the collapse's further relaxations (unbound B, cooldown, the ended split's state, the
  expected `exited before drained`, `TryUnion` as the test, a stuck split ends, attempt spacing, late hellos); the
  static release on link loss could duplicate → dropped; the 3-refusal rule now needs a prior registration; inbound
  handoffs toward an unregistered cell pause through the witness message, the outbound pause dropped; the mirror
  prefix is `Supervisor:`; file sizes.
- **Rev 0 → 1, harness review:** the prewarm fault could fire before the e1 broadcast (no `retired broadcast`) → the
  Broadcast trigger and the full expected status and applies; `Supervisor ` never matched → `Supervisor:` and a parser;
  in the drain case all four R-bots are in flight and a fallback counts as a migration → the per-bot rule with f; the
  re-login was never exercised → P-bots on C in the base case, and the login flow must be extracted (inline today) and
  `FailMigration` counted; `start_split` grades the status unconditionally and has no switches → parameters; a
  per-lifetime apply grader with stated expectations; the base case's held set comes from C.1's own lines, not a
  recording that goes stale (monsters on the cut flip owner); the monster sum pairs C's windows with C.1's; `only:` is
  needed (E's area on C.1's edge); the drain case drops its synthetics; a pre-registration fence clock risked fencing
  slow starts → the fence needs a prior registration; `op_outcome` and `REQUESTED` gaps; the fault line text and
  `ENV_KEYS`; the static release has no live check (a note); wording fixes.
- **First live run (2026-10-17), `splitcrash_drain` 99/104, three faults found and fixed:** (1) a restarted cell's mesh
  link listened before the kit's `Assets.Initialize()`, so neighbours' early snapshots spawned puppets that the kit
  then captured as scene objects and re-spawned under ids clients do not know; a client's spawn batch was misread and
  its own character never arrived (10 s migration timeouts), the kit's Initialize also cleared the lazily registered
  spawn-area prefabs (a dropped monster handoff, a drop snapshot's NRE) → on Dynamic maps the mesh link is read only
  once the server scene is ready (`Mesh link of <cell> read from now: …`, new partial `_Readiness.cs`); (2) a
  neighbour with no report counted as ready and the old incarnation's report outlived its connection → a Dynamic-map
  neighbour is a handoff target only with a live report, `RegistrationState` means registered **and** layout gate
  open, and a report dies with its connection (a gated cell no longer witnesses a fence); (3) no back-off after a
  fallback, and the client's queued hop failed the session when the warp arrived before its character → a returned
  player waits `rehandoffCooldownSeconds` (2 s, doubling per consecutive return to 30 s; Dynamic maps), and the client
  redirects a queued hop to the warp's destination (`Hop to … redirected to …`; map-agnostic client code, it only
  changes a case that always lost the session). Not fixed: a restarted cell of a Static map has the same start-up
  hazard (outside 2d's gate).
- **Implementation deviations (2026-10-17/18), kept:** a third coordinator partial `MeshLayoutCoordinator_Collapse.cs`
  (collapse, kills, the fenced store, id reuse; `_Supervision.cs` would have been 638 lines) and small hooks in
  `_Split.cs` (an `ignoreCooldown` on `CheckOpCommon`, the id-reuse call, `Abort(…, fence)`); `IMeshSplitHost` gained
  `KitRestartsBaseCells` (the central answers `!sleepCells`); a kit-restarted base is only waited for (`Supervisor:
  <cell> down|hung <s> s, left to the kit's restart`), a hung one is killed so the kit restarts it; a merge survivor
  restarting while e2 is staged to it gets the staged e2 again (2c), and a retiring cell unregistered before e2 still
  aborts at once; new-incarnation aborts keep 2b/2c's reason text; a member returning as a new incarnation before it
  was paused is rebound directly; `Split <op>: channel <child> removed` is mirrored on loss aborts only; the pre-restart
  kill is silent; the spawner's kill window is **60 s** (not 10 s: the incarnation is made at server start, after the
  batchmode boot). Map side: the prewarm fault is a peeking Layout handler in `_Fence.cs` (the e1 broadcast reaches the
  child at a pair it already holds, acked before any `_Pacing` hook); an ignored older hello also closes its link
  connection; a fencing cell's `FenceTick` short-circuits the rest of `Update`; an editor-hosted cell never kills the
  editor; two Info lines for registration-state flips; a `fenced:` Retire to a Pending child fences it; the witness
  test treats a neighbour's "not registered" report within 5 s before this cell's last registered tick as the same
  outage (cells notice a central's death up to a disconnect timeout apart). Harness: `start_split`'s new parameter is
  `plan=` (not `bots=`), plus `on_poll=`; the "op paused" deadline is graded as order inside the op wait; the retired
  broadcast and the channel removal may come in either order; the split and merge grades now fail on any Supervisor,
  paused or resumed line; `SPLIT_OP_PROBLEM` catches `Fence:` and `FAULT: dying on`; a re-login in the drain case is a
  note.
- **Acceptance runs (2026-10-18):** every scenario passed; one `split` and one `merge` failed once each on the harvest
  bot's pickup (it stood 41 m from the drop for 60 s: the harvest left the node targeted and the walk never started,
  2 of ~13 harvest runs) → the bot clears its target and stops before the pickup and re-issues a walk that makes no
  progress for 3 s (`pickup: no progress for 3 s at <pos>; walk issued again`); the reruns passed (flake policy).
