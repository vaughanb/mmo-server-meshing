# Dynamic cells: the operator's unfreeze (implementation contract)

Revision 1, 2026-10-20 on machine A, after one independent critique (log at the end) of revision 0, itself from a read of HEAD 8ce1332 (DESIGN 0.84, package 0.84.0): `Mesh/MeshLayoutCoordinator*.cs`
(the recovery, resume, split, merge, collapse and supervision partials), `MeshRecoveryResolver_Operation.cs`,
`MeshStoredOperation.cs`, `MeshLayoutCommand.cs`, `MeshCentralNetworkManager_Split.cs`, the map side's layout, retire and
fence handlers, the kit's `MapSpawnNetworkManager` (a requested spawn is started with `autoRestart` false), and the
harness's `regress_central.py`, `regress_procs.py`, `rigcentral.py`. (Another session's `slice7_design.md` is the
on-demand links slice; this contract is independent of it and numbered by name.) The plan (2a-6) is complete; this
closes the gap it left most open: **a Dynamic map's family can freeze with no way out short of restarting everything.**
Two things freeze today:

- **A frozen family** (Slice 5 decision 3): after a central restart a dynamic id the store or the cells name never says
  hello by `recoveryCeilingSeconds`; its family gets no operation, supervision or policy until that id says hello. A
  child that died while the central was down never does: the kit's spawner does not restart a child it was asked to spawn
  (`HandleRequestSpawnMap` passes `autoRestart` false), and the supervisor watches only cells that said hello to it. Its
  rectangle is unserved and the family is stuck. If the restarted central has a stored operation for that family, the
  operation waits too (decision 7's "a Resume on a frozen family waits").
- **A frozen operation** (2b/2c): a step past its commit whose participants stay silent past `splitStepTimeoutSeconds`,
  a rejected commit ack, or a store or apply failure after the commit freezes the operation where it is. Frozen holds the
  world-wide operation slot (`CheckOpCommon`: `<op> is frozen`), so no split, merge or collapse runs anywhere, and only a
  central restart (which drops it, Slice 5 decision 7) clears it.

This adds **one operator command, `<seq> unfreeze <base>`, that resolves either: for a frozen family it declares the
missing children dead (their incarnations fenced as the supervisor fences), retires those that own nothing, collapses
those that own a rectangle into a live sibling or the base through 2d's collapse, and adopts the rest as they stand; for
a frozen operation it resolves it forward or back by the rules a restarted central applies to a stored operation
(Slice 5 decision 7, keyed on the step it froze at), so no rectangle ever has two owners. Every decision is one line
through the coordinator's `Line`, mirrored by the runners.** A rig scenario kills the child while the central is down,
unfreezes, and grades that logins route, the store is consistent and no rectangle has two owners.

Read first: this contract; `slices/slice5_design.md` (decisions 1-7), `slice2d_design.md` (fencing, the collapse),
`slice2b_design.md`/`slice2c_design.md` (the freeze paths). Where they disagree: THIS CONTRACT, then 5's, 2d's, 2c's,
2b's.

User decisions: build the operator escape (2026-10-20, through the session that started this one: the command's shape
`<seq> unfreeze <base>`, declare-dead + fence + retire/collapse for a family, the restart resolver's rules for an
operation). Choices the user has not seen are marked (D).

## Rules
As 2b-6: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical** (the
command refuses any cell not on a Dynamic map; nothing else changes for a Static map); log lines one physical line,
invariant, no braces, never "Exception"; files under ~500 lines (`_Split.cs` 489 takes three lines, `MeshLayoutCoordinator.cs`
316 one hook; the new code is a new partial); kit edits none; no edits or compiles while a rig chain measures; nothing
committed (the user commits).

## Decisions

### The command
1. **Shape.** `<seq> unfreeze <base>` (three words; `MeshLayoutCommandKind.Unfreeze`, `cellId` = the base). The id must
   be a family's **base** (an asset cell of a Dynamic map): a child id is refused with `<id> is a child of <base>: unfreeze
   takes the family's base` (D: the family is what froze, and the base is the id an operator sees in both freeze lines).
   `MeshCentralNetworkManager.SubmitLayoutCommand` hands it to `MeshLayoutCoordinator.RequestUnfreeze(baseId, out error)`;
   a refusal is logged `Layout op refused: <rule>` through `Refuse`, as every command's is. Usage text gains `| <seq>
   unfreeze <base>`.
2. **What it meets.** In order: the coordinator Stopped (`the layout coordinator is not running`), Recovering (`the
   layout coordinator is still recovering`), the store not writable, no split host; the id unknown, not a base, on a
   Static map; then **a frozen family** of that base (Slice 5's `FrozenFamily`) → decisions 3-6; else **the frozen
   operation** (`_op.IsFrozen` with `_op.baseId` = the base) → decisions 7-8; else refused `family <base> is not frozen`
   plus what holds the slot when something does (`; <op> is in progress` when an operation runs on the family, `; <op> of
   family <b> is frozen: unfreeze <b>` when another family's frozen op holds the world-wide slot, `; <op> waits for family
   <b>, which is frozen: unfreeze <b>` for a stored op, decision 9). A family that is both cannot occur (a frozen family
   refuses every operation); the family path runs first.

### A frozen family (central; new partial `MeshLayoutCoordinator_Unfreeze.cs`, pure `MeshUnfreeze.cs`)
3. **The dead.** The family's missing ids (`FrozenFamily.missing`) are declared dead. Refused, nothing changed:
   - **a missing id has a registered map server** (`a map server of <id> is registered but has not said hello: stop it
     or let it say hello`): a live process the central can see is not dead (its hello would unfreeze the family by
     itself);
   - **the central started under `IdReuseSeconds` ago** (`childFenceSeconds + fenceSaveSeconds + 1`; `the central started
     <s> s ago, under the <t> s a cut-off cell may take to fence`): the 2d id-reuse rule. A family freezes only at the
     ceiling (60 s ≥ 16 s with the defaults), so this guards odd settings only.
   Each dead id is **fenced** as the supervisor fences: the fenced set takes a **fence floor**, the incarnation text of
   **this central's start** (`MeshUnfreeze.FenceFloor(Incarnation)`, hex ticks like every cell's incarnation,
   `DateTime.UtcNow.Ticks.ToString("x")`), and `MeshFencedSet`'s "not newer than a fenced one" rule then fences every
   process of that id started before this central: a zombie's later hello gets `fenced: stale incarnation` (it saves,
   kicks and exits, 2d decision 5); the set is saved (`SaveFencedSet`). Nothing can have started a process of a missing id
   since (the old central is dead, the family frozen and unsupervised), and a process started later (the policy's next
   split names `<base>.1` again about 30 s after the collapse) is newer by the ceiling plus the operator's delay at
   least, the margin for a spawning host's clock lagging the central's (critique: the command's own time left a 30 s
   margin). **The command asserts the processes are gone (limit, D):** a process of a missing id that is alive but
   unseen by the central (hung, suspended, or cut off from the cluster) never fences itself after a central outage (2d's
   witness rule disqualifies every neighbour that saw the outage), and the central cannot kill it (`KillChannel` needs the
   pid and incarnation a hello would have named). It keeps serving its rectangle beside the survivor until its next
   hello is fenced. SETUP tells the operator to make sure the process is gone on its host (or kill it there) first. Line
   (Warning) `Unfreeze <base>: <id> declared dead (no hello <s> s after the central started); incarnations started
   before <floor> fenced`.
4. **The family's entries** in the broadcast are classified (`MeshUnfreeze.Classify`, pure, table-tested): a dead id
   Active and owning a rectangle **collapses**; a dead id owning nothing (Pending, Draining, or Active with an empty
   rectangle) is **retired**; a dead id with no entry or a Retired one needs nothing; every live cell is **adopted as it
   stands** (no layout change).
   - **Collapse** (D: rather than a restart; the supervisor's own end state for a child that cannot come back, it needs
     only live cells, and the policy may cut the family again): the id gets a supervisor record in `CollapseQueued`
     (`isBase` false, no failures), so 2d's machinery runs it unchanged: `TryStartCollapse` once the op slot is free,
     `CheckCollapse`'s relaxations (unbound, unregistered retiring cell; no cooldown), `CollapseSurvivorOf` (the base when
     their rectangles make one, else the least loaded participating sibling that does), the merge's exit "before drained"
     5 s in, e3, the tidy when the base is whole. While queued or collapsing any process of the id is answered `fenced:
     collapsing` (`HelloIsFenced`). Line `Unfreeze <base>: <id> collapses into <survivor>` (the geometric choice at the
     command among the **live** cells: the base, else a live Active sibling, never another dead id; the merge picks the
     participating one when it starts). With several dead ids and no live cell sharing a full edge with this one, it
     waits for the others' collapses (one may grow a cell to its edge): `Unfreeze <base>: <id> waits for the family's
     other collapses (no live cell of its family shares a full edge with it yet)` (Warning; if none ever does, it stays
     queued with 2d's one `collapse waits` line, a limit). With no live cell sharing a full edge and no other dead id
     the record is `LeftDown` as 2d leaves it: `Unfreeze <base>: <id> cannot collapse (no live cell of its family shares a
     full edge); left down` (Warning) and the `cannot-collapse` note to the base; its rectangle stays unserved (the 2d
     limit).
   - **Retire:** one **retire version** (`MeshUnfreeze.Retired(broadcast, ids, pair)`: those entries Retired, owning and
     interested in nothing) at `NextPair()`, validated against the broadcast, saved (no operation), applied on the
     central, broadcast, and the ids' channels removed — only while the op slot is free; with it held the version waits
     (queued, decision 6's tick). Line `Unfreeze <base>: retire version <pair> broadcast (<ids> Retired)`; a validation or
     store failure: `Unfreeze <base>: the retire version could not be broadcast: <reason>` (Warning; retried next tick).
     A live cell never meets it (only dead ids are retired), so no running cell is asked to retire through a layout.
5. **The family's stored operation.** When the store's operation is unresolved and its base is this family (it waits,
   Slice 5 decision 7):
   - a **member** of it is dead (a split's parent or child, a merge's survivor or retiring cell) → **dropped**:
     `<Kind> <op>: dropped at the unfreeze: <id> declared dead` (Warning), the store's operation cleared, the op counters
     continued (as a restart's drop); the layout stays as adopted and decision 4 collapses or retires the dead member.
     A merge's retiring cell with no registered process joins the dead set then (it was excluded from the wait set from
     the commit on, so it is not "missing", and a dropped merge would leave it Draining with nobody to retire it); a
     **live** Draining retiring cell of a dropped merge is a known limit (a running cell cannot be retired through a
     layout; it keeps its old interest until an operator kills it, after which it is a dead entry nobody names);
   - otherwise → the family is unfrozen first, then `ResumeStoredOperation()` runs the restart's verdict, which no longer
     waits. Its op then holds the slot; queued collapses and retire versions follow it.
6. **Unfrozen.** The family leaves `frozenFamilies` before any collapse or resume runs (so `CheckOpCommon`,
   `CollectWatched` and the policy see it unfrozen); line (Info) `Unfreeze <base>: family <base> of map <m> unfrozen by
   the operator: <ids> declared dead`. From here supervision watches its live cells as after a hello-unfreeze. A new
   `UnfreezeTick()` (one hook in `Tick`, after `SupervisionTick`) broadcasts a queued retire version once the op slot is
   free. Nothing about the dead ids is stored besides the fence floor: a central restart before the collapse's e2 finds
   the store naming them and freezes the family again (a known limit; unfreeze again). For that, **a collapse's retiring
   cell is no longer an `AbortableId`** (`MeshStoredOperation.AbortableId` returns null for a stored collapse): today a
   restart during a collapse before its e2 takes the dead cell's absence as the abort's expected outcome, reverts the
   collapse (the dead cell owns its rectangle again), and freezes nothing, so the rectangle is unserved with no line and
   no command applies (critique). Now the family freezes at the ceiling and the revert's abort still runs (an Abort
   verdict runs on a frozen family); the next unfreeze collapses it again. A collapse from the commit broadcast on is
   unchanged (its retiring cell is the excluded id; it resumes and exits).

### A frozen operation
7. **Its step.** `Freeze` records the step the operation is resolved by (`MeshLayoutOperation.frozenStep`, set before
   `MarkFrozen`; memory only: the store keeps writing `Frozen`, so a restart's rules are unchanged): the step it was at,
   **unless that step's broadcast never went out** (critique: `BroadcastStep` enters the next step before its save and
   apply, so a store or apply failure freezes the op one step on, at a pair no cell holds): when the step's pair is newer
   than the broadcast, the step before it (`StepToResolve`): a split's unsent e2 (frozen at DrainGate) → E2Staged (the
   child acknowledged e2, so ResendStaged; `op.staged` survives a failed broadcast), an unsent e3 → DrainGate; a merge's
   unsent e2 → E2Staged, unsent e3 → Retiring (DrainGate when Retire was never sent), unsent tidy → E3Broadcast. The unfreeze
   builds the stored form (`MeshStoredOperation.From(op)` with `step` = `frozenStep`) and asks the restart's resolver
   `MeshRecoveryResolver.ResolveOperation(stored, broadcast pair, applied)` where `applied` maps every registered,
   bound cell of a Dynamic map to its **latest acknowledged pair** (`binding.applied`: in-process, the acks are the
   truth a restart reads from the hellos). The verdicts, by the frozen step:

   | frozen at | verdict |
   |---|---|
   | split E2Staged | the child acknowledged e2 → **ResendStaged**: e2 broadcast → DrainGate; else **Abort**: the Retired broadcast and a `fenced: aborted by <op>` Retire to the child (2b's abort with e2 staged; nobody else saw e2) |
   | split DrainGate | **Resume at DrainGate** (fresh drain evidence at or after e2; a drain deadline from its first qualifying window) |
   | split E3Broadcast | **Resume at E3Broadcast** (the participants' acks of e3 → done, the settle if stragglers) |
   | merge E2Staged | the survivor acknowledged e2 → **ResendStaged** → DrainGate; else **Abort**: the Reverted broadcast |
   | merge DrainGate, Retiring, E3Broadcast, TidyBroadcast | **Resume at that step** (Retiring re-sends Retire at once) |
   | no frozen step (a frozen op restored by an older path), an unknown step | **refused** `<op> froze at an unknown step` (nothing changed; a restart drops it) |

   A step before the commit never freezes (it aborts); a merge's Retiring has no timeout and freezes only through the
   stepped-back e3. The resolver's stale rule (`the
   adopted layout is newer than anything <op> issued`) cannot fire in-process (no other layout went out while it was
   frozen) and is refused like an unknown step if it ever does.
8. **Carrying it out** reuses the resume path (`MeshLayoutCoordinator_Resume.cs`, factored so both callers share it):
   the op is rebuilt from the stored form at the verdict's step with the members' **current** bindings (`BindMembers`),
   its staged snapshot when re-sent (`op.staged` as frozen), `participants` = every bound, registered cell of a Dynamic
   map, `stepStartedAt` = now (a fresh step timer: a cause that persists freezes it again after
   `splitStepTimeoutSeconds`, with a new reason), `prewarmNeighbours` recomputed; the store is written with the step
   entered. Lines `Unfreeze <base>: <op> frozen at <step> (<endReason>)`, then `<Kind> <op>: resumed at <step> after the
   unfreeze` (Resume, ResendStaged) or `<Kind> <op>: aborted by the unfreeze: <target> did not acknowledge the staged
   e2` (through `Abort`/`AbortMerge`, whose own lines and broadcasts follow). **A Resume re-sends the current broadcast**
   to every map server (`Unfreeze <base>: layout <pair> re-sent`) before the gates run: a cell that lost the step's
   broadcast applies it now, one that has it acks "already at pair" (the map side's rule), so a lost message no longer
   freezes the step again. Members that are down are 2d's business from here: a member lost after the commit broadcast
   pauses the op until it is back (the supervisor restarts it: the family is watched), one lost while e2 is staged
   aborts. The policy is told nothing new (it counted the freeze as the op's end; the resumed op's end only re-dates the
   family's cooldown).

### The op slot
9. **A stored operation waiting on a frozen family holds the world-wide slot (D).** Today a restarted central whose
   stored op waits for a frozen family lets operations start on other families; the waiting op, resumed later by the
   missing id's hello, then replaces `_op` while another op runs (two operations at once), or (with the layout moved on)
   is dropped as stale; worse (critique), the other op's `SaveStore` overwrites the stored operation, so the
   hello-unfreeze's `ResumeStoredOperation` rebuilds **the running op** from the store and replaces `_op` with a copy of
   itself. `CheckOpCommon` gains, after the frozen-family refusal: `<op> waits for family <b>, which is frozen: <reason>`
   while the store's operation is unresolved and the map Running (`StoredOpWaits`); the policy's `busy` reads the same (a
   collapse's `CheckOpCommon` refuses it too and logs `collapse waits` once). The cost, named: every family in the world
   waits for the operator, as it does for a frozen in-memory op; one line says so when the wait starts: `<Kind> <op>:
   holds the world-wide operation slot until family <b> is unfrozen (unfreeze <b>)` (Warning). The unfreeze (decision 5)
   or the id's hello releases it. Frozen families with no stored op hold nothing (as today). The store gate (decision 2)
   tries one save of the coordinator's data before refusing `not writable`: `MeshCoordinatorStore` stays unwritable after
   a failure until a save succeeds, and no other save may come while everything is frozen.

### Rig: `splitcentral_unfreeze` (`regress_central.py`; graders in `rigcentral.py`; split topology)
10. **Flow.** `regress_split.start_split` as `splitcentral`, but with the P-side stand bots only (meshbot6..9, in the kept
    half; no R-bots, no tester: killing the child would kick them and their re-logins during a frozen recovery would test
    the login fallback, not the escape), no harvest bot, `MESH_MONSTER_AREAS=only:Spawn_Interior_C`. 10 s after the
    split's done: **kill the central**; at once **kill C.1's process** (`rigsplitcrash.kill_script`; the kit's spawner
    does not restart a requested child, and the new central never sees it); wait `CENTRAL_DOWN_S`, **start the central
    again**; wait for the adoption line (`... with 2 entries (C, C.1)` from the store) and then for `Coordinator
    recovery: family C of map ServerMeshTestMap frozen: no hello from C.1 after 60 s` in the second lifetime: with C.1
    missing the adoption itself waits for the ceiling, counted from the coordinator's start, so both lines land together
    80-100 s after the kill (`ADOPT_TIMEOUT_S` + the ceiling; `restart()`'s 90 s check is not reused, critique); write `<seq> unfreeze C`; wait for the collapse merge's `done` in the second lifetime
    (`UNFREEZE_END_S` 180 s); then launch the late bot `meshbot` (`stand` at (60, −60), its row in R at (60, 70)):
    it logs in after the collapse.
11. **Grades.** The kills (1 central, 1 C.1 process); the adoption as above; the family frozen line and its
    `recovery-frozen` absence for the map (only the family froze); the unfreeze lines in order: `C.1 declared dead`,
    `family C of map ServerMeshTestMap unfrozen by the operator`, `C.1 collapses into C`; no `Layout op refused`; the
    collapse merge's lines in order: `requested C C.1 collapse`, `e1 broadcast`, `e1 gate open`, `e2 staged`, `e2
    broadcast`, `C.1 exited before drained`, `channel C.1 removed`, `e3 broadcast`, `tidy broadcast`, `done`; **C.1 never
    comes back**: its process count 0 at every sample from the kill to the teardown and its log one lifetime; **no two
    owners**: C applies nothing owning any of R before the collapse's e2 pair, its applies after the restart are the
    collapse's e1 (kept, base), e2 (base, base), e3 and tidy (base, base) in that order (`base` = C's whole asset
    rectangle) (`rigcentral.expected_lives("unfreeze", ...)`:
    one lifetime for every cell, C's ending with those four, C.1's ending at the split's e3); **logins route**: the late
    bot stands, `Login routing: meshbotChar ... -> channel C` in the second lifetime, `player registered meshbotChar` on C;
    the P-bots `migrations=0 relogins=0`, never kicked; **the store is consistent**: no entry of C's family, no operation,
    the broadcast at the tidy's pair, C.1 in `fencedIncarnations`; 0 exceptions, 0 teleports, no `Fence:` on any cell.
    It joins `ALL`, `TWO_HOST_SKIP`, `PERSIST_SKIP` with the other `splitcentral*` scenarios.

### Acceptance
12. EditMode (a new `MeshUnfreezeTests.cs` on Slice 5's restart harness, `MeshUnfreezeOpTests.cs` for frozen ops,
    `MeshUnfreezeRulesTests.cs` for the pure parts): the parse (`7 unfreeze C`, wrong word counts); a frozen family:
    declared dead, fenced (an older incarnation's hello → `fenced:` Retire; a newer one not fenced by the floor), the
    family unfrozen, the collapse queued and driven to done (the family's entries gone, C owning P, the store with no
    entry and no op); a dead id owning nothing → the retire version (and its wait while the slot is held); a stored op
    whose member is dead → dropped and the member collapsed; a stored op waiting holds the slot (another family's split
    refused, the policy busy) and the unfreeze releases it; refusals: not frozen (with the slot holder named), a child id,
    a Static map's cell, unknown id, still recovering, a missing id still registered, under the id-reuse time; frozen ops:
    split at E2Staged acked → e2 broadcast → DrainGate; not acked → abort with the fenced Retire and the Retired
    broadcast; split at DrainGate → resumed, re-sent broadcast, fresh evidence → e3 → done; split at E3Broadcast →
    resumed → done; merge at E2Staged not acked → Reverted; merge at TidyBroadcast → resumed → done; a resumed op that
    stays silent freezes again; `frozenStep` recorded and the store still says `Frozen`; all earlier tests unchanged.
    Python: `rigcentral` (the unfreeze line parser, `expected_lives("unfreeze")`), `regress_central`'s new flow with the
    FakeRig (kills, the command written, the bots), `test_registration`; ruff.
13. Rig: zero new compiler warnings; `splitcentral_unfreeze` **3/3**; `splitcentral` and the split, merge and crash
    families (`split`, `merge`, `splitc2`, `mergec2`, `splitcrash_prewarm`, `splitcrash_drain`, `splitcrash_base`,
    `splitbuild`, `splitcentral_merge`, `splitcentral_base`) 1/1 each (3/3 on one that fails once). DESIGN 0.85 (the row,
    the known limits replaced), SETUP §6a (the command in 6a.5, the recovery paragraph in 6a.9, the limits in 6a.12),
    `ServerMeshPackageBuilder` 0.85.0, `regress.py --package`.

## Interfaces and log lines (exact)
- **Command:** `<seq> unfreeze <base>`; `MeshLayoutCommandKind.Unfreeze`; `MeshLayoutCoordinator.RequestUnfreeze(string
  baseId, out string error)`.
- **Central:** `Unfreeze <base>: <id> declared dead (no hello <s> s after the central started); incarnations started
  before <floor> fenced` (Warning); `Unfreeze <base>: <id> collapses into <survivor>`; `Unfreeze <base>: <id> cannot
  collapse (no live cell of its family shares a full edge); left down` (Warning); `Unfreeze <base>: retire version <pair>
  broadcast (<ids> Retired)`; `Unfreeze <base>: the retire version could not be broadcast: <reason>` (Warning);
  `Unfreeze <base>: family <base> of map <m> unfrozen by the operator: <ids> declared dead`; `<Kind> <op>: dropped at
  the unfreeze: <id> declared dead` (Warning); `Unfreeze <base>: <op> frozen at <step> (<reason>)`; `<Kind> <op>:
  resumed at <step> after the unfreeze`; `<Kind> <op>: aborted by the unfreeze: <target> did not acknowledge the staged
  e2`; `Unfreeze <base>: layout <pair> re-sent`; refusals `Layout op refused: family <base> is not frozen[; ...]`,
  `<id> is a child of <base>: unfreeze takes the family's base`, `a map server of <id> is registered but has not said
  hello: stop it or let it say hello`, `the central started <s> s ago, under the <t> s a cut-off cell may take to fence`,
  `<op> froze at an unknown step`, and in `CheckOpCommon` `<op> waits for family <b>, which is frozen: <reason>`.
- **Pure:** `MeshUnfreeze.FenceFloor(long)`, `.Classify(MeshLayoutSnapshot, string baseId, ICollection<string> dead,
  List<string> collapse, List<string> retire)`, `.Retired(MeshLayoutSnapshot, IEnumerable<string>, MeshLayoutPair)`,
  `.DeadMember(MeshStoredOperation, ICollection<string>)`; `MeshLayoutOperation.frozenStep`.
- **Runner:** `ServerMeshLayoutCommandFile` mirrors lines starting `Unfreeze `.
- **Harness:** `regress_central.flow_unfreeze`; `rigcentral.UNFREEZE`, `unfreeze_lines`, `expected_lives(case="unfreeze")`.

## Files
Mesh/: new `MeshLayoutCoordinator_Unfreeze.cs` (the command, both paths, the retire queue and tick, `StoredOpWaits`),
`MeshUnfreeze.cs` (pure); edits `MeshLayoutCommand.cs` (the verb), `MeshCentralNetworkManager_Split.cs` (the dispatch),
`MeshLayoutCoordinator.cs` (the tick hook), `_Split.cs` (`frozenStep` in `Freeze`, the slot refusal), `_Resume.cs` (the
shared carry-out), `_Policy.cs` (`busy`), `MeshLayoutOperation.cs` (`frozenStep`). TestMap: `ServerMeshLayoutCommandFile.cs`
(the prefix). Tests: the three new files and the existing parse tests. Tools: `regress_central.py`, `rigcentral.py`,
`regress.py` (registration), their tests. Docs: DESIGN, SETUP, this contract.

## Implementation deviations (2026-10-20), kept
- **The stored-op flag.** `_storedOpResolved` stayed false in a lifetime with nothing stored, so a later op's own store
  entry read as "the stored operation" (decision 9's refusal fired on a split's pending settle in a central that never
  restarted, `MeshMergeOperationTests.APendingSettle_IsRefused`; and a hello-unfreeze could rebuild a running op from
  it). `ResumeStoredOperation` now marks it resolved when nothing is stored, and `StoredOpWaits` also needs the stored
  op's family frozen.
- **A resumed drain gate waits for a fresh window.** In-process the parent's binding keeps the evidence it had at the
  freeze, so a split resumed at DrainGate opened at once on it; the unfreeze clears the parent's `hasEvidence` (as a
  restart's hello does), so the gate opens on the next window after the unfreeze.
- **The store gate probes.** As decision 9 says: one save of the coordinator's data before refusing `not writable`.
- **The rig's line grades.** The central's kit log is three files (info, warn, error) merged by stamp, so lines of one
  second from two files have no order: the unfreeze's three lines are graded present (the `declared dead` one is a
  warning), the collapse's order over its info-level states, its warning `exited before drained` apart. The login logs
  `Login routing:` only when it changes the channel; the late bot asks for C, so the grade is "no route to anything but
  C" plus `player registered meshbotChar` on C only (run 1 failed these three grades only, 122/125, with the collapse
  done 6 s after the command).

## Critique log
- **Rev 0 → 1 (2026-10-20), one independent review of the contract against HEAD:** a live but unseen process of a
  missing id never self-fences after a central outage (the witness rule), and the central cannot kill it → named as the
  command's assertion and a limit, SETUP tells the operator to stop it first; `BroadcastStep` enters the next step before
  its save and apply, so a broadcast failure freezes the op one step on at a pair nobody holds and a resume would wait
  for ever → `StepToResolve` steps back to the step whose layout everyone has (tests per failure point); a restart during
  a collapse before e2 reverted it without freezing (the collapse's retiring id was abortable), leaving the rectangle
  unserved with no command → `AbortableId` is null for a collapse; the fence floor at the command's time left a 30 s
  margin against a lagging host's clock before the policy reuses `<base>.1` → the floor is the central's start; the
  geometric survivor could be another dead id → live cells only, a dead id with none waits for the others' collapses or
  is left down; decision 9's bug is worse than described (the running op replaced by its own store copy) → the refusal
  stays, its cost is named with a line; the rig's adoption itself waits for the ceiling → the adoption timeout is
  `ADOPT_TIMEOUT_S` + the ceiling; `P` meant two rectangles → `base`. Considered and kept: rebuilding the frozen op from
  its stored form (the restart's path, so "the same rules" literally; it loses `pausedMembers`, re-derived in 5 s, and
  `exitedBeforeDrained`, `retireKillAt`, the drain counts, as a restart does) rather than rewinding the object in place.
- **Live runs and acceptance (2026-10-20/21, BRENDOAIBOX, a tree shared with the on-demand links session under the
  parent's rig lock).** `splitcentral_unfreeze` run 1 122/125 (three grader artefacts, see the deviations: the collapse
  ran e1 to done 6 s after the command), then 3/3 on the final harness, 126/126 each. Families 1/1 on a fresh build:
  `splitcentral` 161/161, `splitcentral_merge` 126/126, `splitcentral_base` 145/145, `split` 119/119, `merge` 144/144,
  `splitc2` 111/111, `mergec2` 138/138, `splitcrash_prewarm` 114/114, `splitcrash_drain` 104/104, `splitcrash_base`
  106/106, `splitbuild` 142/142. EditMode Mesh group 3051 (3050 passed, 1 skipped as before) after the package bump; no
  new compiler warning; harness 851 tests OK, ruff clean. No kit edit. DESIGN 0.85, SETUP §6a.5/6a.9/6a.12/§7, package
  0.85.0 (`regress.py --package`). **Accepted.**
