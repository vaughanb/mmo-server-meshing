# Dynamic cells, Slice 2c: merge, drain, retire (implementation contract)

Revision 1, 2026-10-17 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end) of revision 0, itself from three surveys (central, map side, harness) of HEAD 17e3e27 (Slice 2b, DESIGN
0.76). 2b split a Dynamic cell: a child X took the far side R of a parent P. 2c undoes it: **an operator (or the
harness) merges a child back into its base cell; the child drains everything it owns to the survivor and exits; then
the layout retires it and the neighbours drop it cleanly; the survivor owns the union**. Supervision and fencing (2d),
automatic policy (3), buildings (4) and central-restart recovery (5) are later slices.

Read first: this contract; `slices/slice2b_design.md` (its decisions, interfaces and deviations hold unless changed
here); the plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D1 merge geometry (:115-117), D3 (:141-198), D4 merge and
drain (:224-244), D5 Draining (:269-277), D6 (:279-305), D7 (:306-316), Slice 2c (:413-418), the per-slice gate
(:328-340); the build sheet's re-plan (`phase0/phase0_build_sheet.md` §6: "2c, merge and drain: drain rate from B5 and
B10" — 2b's `drainRatePerSecond` 5 is that rate and stays). Where they disagree: THIS CONTRACT, then 2b's contract,
then the re-plan, then the plan.

User decisions (2026-10-17): start 2c. Choices the user has not seen are marked (D).

## Rules
As 2b: Mesh/ is game-neutral and never names TestMap/Tests types; **Static maps byte-identical** (everything below is
gated on a Dynamic map, an op in progress, or a Draining or Retired cell in an applied layout — a Static cell banding
with a Dynamic map, C2, does see a Retired zone); log lines one physical line, invariant, no braces, never
"Exception"; files under ~500 lines: `MeshMapNetworkManager_Layout.cs` (504) gets no new code (its retire functions
and `CloseLink` move to the new partials), `MeshMapNetworkManager.cs` (1,142) and `_Ghosting.cs` (3,719) get one-line
hooks only; kit edits only as numbered CE patches (none planned); no agents, edits or compiles while a rig chain
measures; commits only when the user asks.

## Decisions

### The merge operation (central)
1. **Scope (D).** A merge joins a **child** B into its family's **base cell** A (the survivor) when their owned
   rectangles share a full edge (A∪B is a rectangle, within `touchTolerance`; a new `MeshRect.TryUnion`). A merge of two
   children is refused (`merge of two children (Slice 3)`): when the survivor is not the monsters' home, the home's lent
   records can point at a cell that has exited, and the plan's general survivor rule needs the load board (Slice 3).
   The base cell is never retired (the spawner restarts base cells, `autoRestart=true`; children have none).
2. **Entry.** `<seq> merge <a> <b>` in `Temp/ServerMesh.LayoutCommand.txt` (either order; the coordinator finds the
   base) through `MeshCentralNetworkManager.SubmitLayoutCommand`, which dispatches on the verb;
   `MeshLayoutCommand.TryParse(line, out MeshLayoutCommand cmd, out string error)` with `cmd.kind` = Split or Merge.
   `RequestMerge(string a, string b, out string error)` on the coordinator.
3. **Refusals** (`Layout op refused: <rule>`): the split guards factored into one `CheckOpCommon` (store writable, host
   present, cell known and not Retired, Dynamic map, recovery AdoptStatic, Active and owning something, bound and
   answered, no op in progress or frozen world-wide, the family's cooldown — `splitCooldownSeconds` after any op's
   `done`); plus: same family, one of them the base, the union a rectangle, **no cell of the family with interest ≠
   owned** (a pending split settle would be lost), and e1, e2, e3 each validating (rule 8 `MaxNeighbours` can refuse
   e1: the survivor's band widens).
4. **Generalised op (refactor of 2b; split behaviour and its tests unchanged).** An abstract `MeshLayoutOperation`
   (opId, kind, line prefix `Split`/`Merge`, mapId, baseId, step timers, pairs, the staged target's id, connection and
   incarnation, participants, lastGateWait, endReason, member incarnations for restart detection, `InProgress`,
   `PastCommit`, `StepId`); `MeshSplitOperation` derives from it; a new pure `MeshMergeOperation` (survivorId,
   retiringId, both owned rectangles, the union; snapshot builders `E1`, `E2`, `E3`, `Reverted`, `Tidy`). The
   coordinator's `_op` is typed by the base, with typed accessors `CurrentSplit`/`CurrentMerge` (tests use
   `CurrentOp` as a split today: keep `CurrentOp` returning the base and fix the test casts), `_opCounter` shared
   (`split-<n>`, `merge-<n>`). Generalised: `SaveStore` (`stagedFirstApplier` = the op's staged target), a re-hello
   from the staged target during a staged step gets that staged step again (today child-only), the undo rule (the
   staged target rejecting its staged step aborts, since nobody else saw it), `Participants` seeded from the op,
   `Freeze` (the op's own step id and staged target), `OpLine`/prefixes, the tick dispatch, `IsSplitFamily` (any op's
   family; also read by the central's wake refusal), `IsOperationChild`, the `HelloGetsRetire` reason text, the Frozen
   check in `CheckOpCommon`. `TrySettleStragglers` stays split-only. Map side: 2b's `_splitShrunk`/`_splitPrevOwned`
   are reset when `OpInProgress` turns false (today the base keeps split-era state and its pacer would wake at the
   merge's e1 with a stale rectangle).
5. **Steps** (each stored before it is sent; pairs strictly increasing):
   - **e1 Prepare**: A's interest = A∪B (owned A); broadcast. Participants: A, B and every registered cell whose band
     set, state or rectangles change.
   - **e1 gate**: every participant acked e1, and A's evidence (pair ≥ e1, window started after its apply) shows an
     inbound hello older than `prewarmSeconds` with traffic since from each of A's band neighbours under e1, and an
     empty spawn queue. The saturation and p95 clauses of 2b's `PrewarmReady` are **not** applied to the survivor (it
     is a running, possibly loaded cell; a gate it can never pass would abort every merge of a busy base).
   - **e2 Commit**: A Active owning A∪B, interest A∪B; B **Draining**, owned ∅, interest B. Staged to A first (kind
     Staged, A's id and incarnation), A's ack, then broadcast. Participants: as e1 **plus every cell banding with B
     under e1** (a cell touching B keeps routing handoffs to B until it applies e2, even though its band set does not
     change).
   - **drain gate**: B's evidence at pair ≥ e2, with a window that started after B's apply **and** was received at
     least one load window (5 s) after the last e2 ack, reports `drained` (decision 9). No timeout ends it: past
     `retireMaxSeconds` (new `dynamicSettings` field, 90 s) the coordinator logs `Merge <op>: drain straggling <s> s:
     <counts>` every 30 s and sends B a CoordinatorNote at error level (`retire-straggler`, once a minute); players are
     never kicked; the op stays in this step (one op world-wide). If B unregisters here (crash), it counts as exited:
     `Merge <op>: <B> exited before drained`, straight to e3 (A already owns everything; the neighbours' link-loss and
     prune paths release records; players re-login routed to A).
   - **retire**: the central sends B `Retire` (30017, reason `merged into <A>`) to its bound connection, `Merge <op>:
     retire sent`, resent every 10 s. B quits on it (decision 13). B is **not** told it is Retired by a layout while it
     runs: every reply B needs (handoff acks, static replies, MonsterHandoffFailed) travels over the neighbours'
     outbound links, which a Retired entry closes.
   - **exited**: B unregistered from the cluster continuously for 5 s (a cluster blip does not count). `Merge <op>: <B>
     exited`, then `RemoveChannel(B)` logged by the coordinator as `Merge <op>: channel <B> removed`. Past
     `retireMaxSeconds` in the retire step: `Merge <op>: retire straggling <s> s` every 30 s and the note.
   - **e3 Retire**: B Retired (owned ∅, interest ∅); broadcast. B is unregistered, so `Participants` drops it. Every
     other cell closes B's link and prunes it (decision 14).
   - **tidy** (D): when A is Active with owned = interest = its base rectangle and is its family's only live cell, a
     version drops the family's entries (the base's identity entry and B's Retired entry): `ApplyEntries` resets the
     base to its base rectangle and leaves B Retired and resolvable (survey), held layouts carry no dynamic entries for
     that family again, and a later central restart can adopt the asset cut (2a recovery). Broadcast and acked like
     any step; then `done` and the family's cooldown. Without the tidy (a family of three or more cells) B's Retired
     entry stays in the broadcast and a central restart freezes the map (2a; known limit until Slice 5).
6. **Abort and freeze.** Before e2: a Rejected ack, a silent participant past `splitStepTimeoutSeconds`, A or B
   restarting (a new incarnation's hello), or B unregistering → abort: broadcast `Reverted` (A's interest back to A),
   `Merge <op>: aborted <reason>`, `Merge <op>: reverted broadcast <pair>`; nothing retired or removed. From e2 on:
   only a Rejected ack or a participant silent past `splitStepTimeoutSeconds` on the e2, e3 or tidy acks freezes
   (`Merge <op>: frozen <reason>`); the drain and the retire never freeze (they straggle, decision 5). A restarting
   after e2 (autoRestart) is answered with the current layout and the op continues. A B hung in its quit keeps the
   world-wide op in the retire step; the operator escape is 2d's (known limit, visible through the straggling lines).
7. **Child id reuse (2c rule; fencing is 2d).** A split may name a child id a merge retired once that id is not
   registered and its channel is removed; its new incarnation's hello makes every neighbour release the old one's
   records (existing rule, which needs `_neighbourIncarnation`: kept, decision 14). Known limit: a reused id loads the
   buildings saved under its channel (Slice 4). EditMode-tested; not run live in 2c.
8. **Login.** Routing already skips Draining and Retired cells (`MeshCell.IsRoutable`). Known limit (kit, 2a): while B
   quits but is still registered, a login's `ConfirmDespawn` asks B too.

### Draining and retire (map side)
9. **`MeshDrainState` (new, pure struct + predicate).** What a Draining cell still holds, **anywhere** (not by
   rectangle): `characters` (every non-puppet `BasePlayerCharacterEntity` present, the kit's despawn delay included,
   plus `_pendingSpawnPlayerCharactersByUserId`), `summons` (pets, mounts, other summons), `monsters` (summoned
   included), `itemDrops`, `rewardDrops` (`BaseRewardDropEntity`, sampled at 1 Hz while Draining), `harvestables`,
   `buildings`, `connections` (**game-transport peers only**: neither the MeshLinkServer's neighbour links nor the
   cluster client), and the tables `inProgress` (`_handoffsInProgress`), `pendingAcks`, `rolledBack`, `deferredAcks`,
   `reserved`, `incoming` (`_incomingHandoffFrom`), `staticLoans`, `serverCharacters` (handoffs in flight). `IsDrained`
   = every count except `buildings` is 0 (D: buildings are Slice 4; B logs `Merge retire: <B> leaves <n> building(s)
   saved under its channel` at warning level). While Draining, a game connection with no user and no pending spawn
   for 10 s is closed (`Drain on <B>: idle connection closed`), else a refused client that never disconnects blocks
   the retire. The same predicate decides the evidence's `drained` and B's exit.
10. **Evidence wire version 2.** `MeshLayoutEvidenceMessage` appends the `MeshDrainState` counts (decision 9's order)
    and `bool drained` after 2b's fields; WireVersion 2; a reader refuses other versions (map and central are one
    build). 2b's split drain gate reads 2b's fields as before.
11. **Handing everything off.** With owned ∅, `DistanceToEdge` is −∞ and `OwnerOf` gives the survivor: players, pets,
    mounts, monsters, statics, drops and synthetics leave through 2b's paths at 2b's rates. Fixes: the pacer's
    nearest-first order measures against the previous owned rectangle when owned is ∅ (today every entity ties);
    monster-summoned monsters (skipped by `MonsterHandoffTick`) are despawned on a Draining cell (D), `Drain on <B>:
    <n> summoned monster(s) despawned`; reward drops are waited out (they expire after their appear duration).
12. **Arrivals on a Draining cell (D, a change from the plan).** A HandoffNotice toward B is **accepted** and the drain
    passes the character on (one extra hop) — `AckHandoffFailed` would destroy the origin's puppet. In the
    `DeserializeEnterGameData` override, **before** calling base (after base, a refusal would orphan a despawning entity
    base adopted), the ids are peeked from a copy of the reader (`NetDataReader.Position`/`SetPosition`): accepted when
    the character is in `_pendingHandoffAcks`, `_rolledBackHandoffs` or `_incomingHandoffFrom`, **or B holds a puppet of
    it** (a real arrival always has one; covers a HandoffNotice that lands after the enter-game); else refused with
    `UI_ERROR_APP_NOT_READY` through `_enterGameRequestResponseMessages[requestId]`, `Enter game refused on draining cell
    <B>: <characterId>`. A refused fresh login sees the kit's disconnect dialog (user-visible; login routing never sends
    one to a Draining cell, so only a stale client can); a migrating client takes its existing fallback hop.
13. **Retire of a running cell.** `MeshRetireRule` gains: a cell that is **Draining in its applied layout** retires on
    a `Retire` for its id and incarnation once `IsDrained`: `Merge retire: <B> drained, quitting`, `_retiring`, then
    `Application.Quit()` (the kit's `wantsToQuit` → `ProceedBeforeQuit` path, as Sleep uses). Not yet drained: keep
    handing off, re-check every second, and past `retireMaxSeconds` log `Merge retire: <B> still holds <counts>` every
    30 s; never kick. B never applies e3 or tidy (they are broadcast after it exited). 2b's Pending-child retire is
    unchanged. The retire functions (`HandleRetireMessage`, `MayRetireNow`, `RetiresInLayout`, `RetireThisCell`) move
    from `_Layout.cs` to `_Drain.cs`.

### Neighbours of a retired cell
14. **Prune and release.** When a cell applies a layout in which zone Z is Retired (`CloseLink`, moved to `_Prune.cs`:
    the link, `_linkClients`, `_meshPeers`, the stream tables, `_statsLinkPrevOut`), it also: releases lent and borrowed
    monsters and **statics** recorded with Z (`_lentStatics`, `_borrowedStatics`, the loan book), rolls back pending
    acks toward Z, drops Z's deferred acks and `_incomingHandoffFrom` entries, removes Z's inbound connection entries
    (`_neighbourZoneByConnection`, `_frameOffsetByConnection`), `_neighbourHellos[Z]`, and Z's puppets (characters,
    monsters, `_ghostStatics`, `_petGhostsBySummoner`). `_neighbourIncarnation[Z]` is **kept** (decision 7's release
    and the unreachable-zone sweep's `wasLinked` need it); a `_retiredZones` set makes the sweep and
    `EnsureNeighbourLinks` skip Z. One line per zone: `Retired zone <Z>: link closed, <n> record(s) released` (Info
    when n = 0, Warning when n > 0), where n counts records (not puppets) released at the prune **plus** those the
    link-loss path released for Z since Z became Draining. Because B quits before e3, every neighbour first sees B's
    link drop: while Z is Draining in the applied layout, that line is Info, `Neighbour zone <Z> link lost (draining,
    retire expected)`, and the existing release on link loss runs as today. Every cell that applies e3 logs the prune
    line once (with `linkAllZones` that is every cell of the map, and C2 in `mergec2`).

### Harness and scenarios
15. **Command file and mirroring.** The PlayRunner mirrors lines starting `Merge ` too (`s_mirroredPrefixes`); its
    forwarding is verb-agnostic. Status lines `<seq> Merge <op>: <state>`; graders filter by kind, since the runner
    mirrors any late `Split` line under the latest seq.
16. **Scenario `merge`** (Dynamic; base C, child C.1 at x = 0 as in `split`). `regress_split.run` is split into phases
    (setup + split; finish + grade) so the merge flow reuses the split's setup. Flow:
    1. the split phase with `-meshObserveSeconds 480` (`MERGE_OBSERVE_S`), recording each bot's first "Standing at";
    2. split `done` and C's e3 apply (interest = its kept half);
    3. wait for the harvest bot's RESULT (cap 200 s after the split's e3) **and** until `splitCooldownSeconds` + 5 s
       after the split's `done`, whichever is later; on a `cooldown` refusal retry once with a new seq;
    4. window guard: refuse to send (fail `stand window too short`) when the earliest R-bot has under 150 s left;
    5. positive control: exactly one `-channelId C.1 ` process;
    6. write `<seq> merge C C.1`; wait for the op (`MERGE_TIMEOUT_S` 240);
    7. wait for C's apply **of the tidy pair** owning and interested in x[-100,100) z[-100,100) (30 s);
    8. sample processes every 2 s: none with `-channelId C.1 ` within 30 s of C.1's `drained, quitting` stamp (the
       measured exit time is a note), and none for 20 s after;
    9. wait for the RESULTs (the rest of the window + 90 s); grade.
    Checks:
    - status: kind Merge, `merge-1` accepted, states e1 broadcast, e1 gate open, e2 staged, e2 acked, e2 broadcast,
      drain gate open, retire sent, C.1 exited, channel C.1 removed, e3 broadcast, tidy broadcast, done; no aborted,
      frozen, reverted, `exited before drained` or straggling line;
    - R-bots meshbot2..5: `migrations=2`, allReady, maxGap < 1000 ms, exactly one `Handoff START <bot> zone C.1->C` in
      C.1's log each; P-bots `migrations=0`; the harvest bot: RESULT passed with `migrations=1` (written before the
      merge), one `Handoff START meshbotChar (…) zone C.1->C` in C.1's log, two `Migration to … fellBack=False` lines in
      its log; **no `fellBack=True` in any bot log** (a fallback hop counts as a migration);
    - monsters: no pre-warmed monster spawned by handoff C.1->C (2b's id rule, against C's ghosts before the merge's
      e2); promoted and respawn counts as notes;
    - statics: when C.1 held one at e2 (its `Static handoff … C->C.1: promoted|spawned` lines minus its sends before
      e2), ≥ 1 sent C.1->C, taken by C, 0 failed; else a note; synthetics: ≥ 1 adopted by C from C.1;
    - C.1: `Merge retire: C.1 drained, quitting`, one lifetime, no Warning `Merge retire` line; its process gone and
      staying gone (above);
    - every cell that applied the e3 pair logs `Retired zone C.1: link closed, 0 record(s) released`; no Warning
      prune line and no `Lent monster … released` / `Borrowed monster … released` line on any cell;
    - after `done`, `Temp/ServerMesh.Coordinator.json` holds no entry for C's family (the tidy's purpose);
    - the merge's own layout grade: apply expectations per cell from the status pairs — C: the split's four, then e1
      (kept, P), e2 (P, P), e3 (P, P), tidy (P, P); C.1: the split's three, then e1 (R, R), e2 (∅, R), nothing after;
      other test-map cells their own rectangle at every version; C2 not counted (P = x[-100,100) z[-100,100), K = its
      kept half x[-100,0), R = x[0,100)); `Merge retire:`, `Drain on`, `Enter game refused` allowed only on C.1, the
      prune and `link lost (draining…)` lines on every cell, nothing else of Split/Merge/Retire; 0 teleports and 0
      exceptions everywhere (C.1's quit is the first map-server shutdown ever graded: its log tail is inside the
      exception grade — a first-run risk).
    The acceptance chain runs `merge pet dynidle` once: `pet` (Static) and `dynidle` (a fresh Dynamic Play) as leak
    canaries.
17. **Scenario `mergec2`** (D): `splitc2`'s flow (E at x = 200, child E.1 = [200, 300) touching C2) with
    `-meshObserveSeconds 300` and no harvest wait (the cooldown wait applies), then `merge E E.1`. Checks: the merge
    status as above; R-bots 2 migrations; E.1's process gone and staying gone; C2 logs `Retired zone E.1: link closed,
    0 record(s) released` and, at the e3 pair, a `neighbours applied` band holding E and not E.1; at least one R-bot's
    puppet on C2 changes owner E.1->E (`Ghost owner changed Player <bot> (…) zone E.1->E` or a fresh `Ghost spawned
    Player <bot> … from zone E`) at its translated position (x − 400); the layout grade with E's rectangles.
18. **Graders and files.** New `Tools/ServerMeshing/regress_merge.py` (flows; the process sampling through `rig.ps`, one
    `Get-CimInstance Win32_Process -Filter "Name='kit-ce-project.exe'"` per sample, executable = the build) and
    `rigmerge.py` (pure: the Merge status parser with `pairs` e1/e2/e3/tidy, `exited`, `retire sent`, `straggling`, the
    channel filter `*-channelId <cell> *`, the prune and link-lost lines, the merge apply expectations, the bot checks);
    `rigsplit.OP_STATE` becomes `^(Split|Merge) (\S+): (.*)$` with the kind recorded; `regress_split.py` split into
    phases; `regress.py` registers `merge` and `mergec2` in `ALL`, `TWO_HOST_SKIP` and `PERSIST_SKIP` and dispatches
    their layout grade. Other scenarios keep failing on any Merge or Retire line. `rigtools.py` (520) gets nothing.

### Acceptance (the plan's per-slice gate)
19. EditMode tests (merge op machine with fakes: refusals incl. cooldown and two children, e1 gate without
    saturation, staged e2 and its re-answer, e2 participants with B's band neighbours, the drain gate's window rule
    and straggling, B exiting before drained, retire and its resend, the 5 s unregistered rule, channel removal, e3
    without B, tidy and its condition, abort with revert, freeze only on acks, A restarting after e2;
    `MeshDrainState`; evidence v2 codec; `MeshRetireRule` for a Draining cell; the enter-game decision; prune and
    release with `_retiredZones`; `MeshRect.TryUnion`; the pacer order with owned ∅; all split tests unchanged); Python
    tests and `py_compile`; ruff; zero new compiler warnings; the stray check; all 11 scenarios Static and
    `all:Dynamic`; `split`, `splitc2`, `merge`, `mergec2` 3/3 each (flake policy: one rerun, recorded); `merge pet
    dynidle` once; DESIGN 0.77; `ServerMeshPackageBuilder` 0.77.0 and UpdateDate; the docs (SETUP: the merge command,
    the scenarios, `retireMaxSeconds`).

## Interfaces and log lines (exact; the writers and the graders rely on them)
- **Command file:** `<seq> merge <a> <b>`; `MeshLayoutCommand.TryParse(string line, out MeshLayoutCommand cmd, out
  string error)` (`cmd.kind` Split|Merge); `RequestMerge(string a, string b, out string error)`.
- **Coordinator** (op ids `merge-<n>`): `Merge <op>: requested <survivor> <retiring>`; `Merge <op>: <state>` for each of
  `e1 broadcast <pair>`, `e1 gate open`, `e2 staged`, `e2 acked`, `e2 broadcast <pair>`, `drain gate open`, `retire
  sent`, `<retiring> exited`, `channel <retiring> removed`, `e3 broadcast <pair>`, `tidy broadcast <pair>`, `done`;
  `Merge <op>: <retiring> exited before drained`; `Merge <op>: drain straggling <s> s: <counts>`; `Merge <op>: retire
  straggling <s> s`; `Merge <op>: aborted <reason>`; `Merge <op>: reverted broadcast <pair>`; `Merge <op>: frozen
  <reason>`; `Layout op refused: <rule>` (existing). The coordinator's line for a split's channel stays `Channel <id>
  removed`.
- **Map side:** `Merge retire: <cell> drained, quitting`; `Merge retire: <cell> still holds <counts>`; `Merge retire:
  <cell> leaves <n> building(s) saved under its channel`; `Enter game refused on draining cell <cell>: <characterId>`;
  `Drain on <cell>: <n> summoned monster(s) despawned`; `Drain on <cell>: idle connection closed`; `Neighbour zone <Z>
  link lost (draining, retire expected)`; `Retired zone <Z>: link closed, <n> record(s) released`. `<counts>` is
  `name=value` pairs separated by single spaces, only the non-zero ones, in decision 9's order (e.g. `characters=1
  pendingAcks=1`).
- **Wire:** `MeshLayoutEvidenceMessage.WireVersion = 2`, appending `MeshDrainState` (decision 9's counts in order, ints)
  and `bool drained`.
- **Settings:** `MeshDynamicSettings.retireMaxSeconds = 90f`.

## Files
Mesh/: new `MeshLayoutOperation.cs`, `MeshMergeOperation.cs`, `MeshLayoutCoordinator_Merge.cs` (+ `_MergeSteps.cs` if
needed), `MeshDrainState.cs`, `MeshMapNetworkManager_Drain.cs` (drain sampling, idle connections, enter-game rule, the
retire functions moved from `_Layout.cs`, summoned monsters), `MeshMapNetworkManager_Prune.cs` (`CloseLink` moved from
`_Layout.cs`, prune and release, `_retiredZones`); edits to `MeshSplitOperation.cs`, `MeshLayoutCoordinator.cs`,
`MeshLayoutCoordinator_Split*.cs`, `MeshCoordinatorTypes.cs`, `MeshLayoutCommand.cs`, `MeshCentralNetworkManager_Split.cs`,
`MeshCentralNetworkManager.cs` (wake refusal), `MeshLayoutEvidence.cs`, `MeshLayout.cs` (setting, `MeshRect.TryUnion`),
`MeshLayoutGate.cs` (`MeshRetireRule`), `_Layout.cs` (removals only), `_Pacing.cs` (order, reset), `_Evidence.cs` (v2),
one-line hooks in `MeshMapNetworkManager.cs` (enter-game) and `_Ghosting.cs` (link loss, the sweep, summoned
monsters). TestMap: `Editor/ServerMeshLayoutCommandFile.cs`. Tools: new `regress_merge.py`, `rigmerge.py`;
`regress_split.py` (phases), `rigsplit.py` (status kind), `regress.py`; tests in `Tests/Editor/` and
`Tools/ServerMeshing/` (`test_regress_merge.py`, `test_rigmerge.py`, `test_regress_split.py`).

## Critique log
- **Rev 0 → 1 (2026-10-17), protocol and Mesh/ review:** e3 before B's exit let the neighbours' `CloseLink` cut B's
  reply path (acks, static replies, MonsterHandoffFailed all ride their outbound links) and could livelock the drain →
  Retire first, B quits, exited after 5 s unregistered, then e3 (B drops out of the participants); B's touching
  neighbours were not e2 participants (their band does not change) and could keep routing to B → they join, and the
  drained window must arrive a full window after the last e2 ack; the shared tick freezes a drain without a deadline
  and a B crash froze the op → merge freezes only on acks, B exiting before drained goes to e3, A restarting after e2
  continues; `connections` counted neighbour links (never 0) and `characters` missed the despawn delay and pending
  spawns → game-transport peers only, idle connections closed, every non-puppet character plus pending spawns;
  clearing `_neighbourIncarnation` broke decision 7's release and the sweep → kept, `_retiredZones`; the prune list
  missed frame offsets, static puppets, pet ghosts, incoming entries and the static loan tables, and one line per zone
  must count what link loss already released; the enter-game refusal must come before base (orphaned despawn) and a
  late HandoffNotice → accept when B holds a puppet; B's retire path rejected e3 → B never applies e3; the refactor
  list (typed accessor, staged target in the store, staged re-answer, undo rule, settle split-only, wake refusal,
  pacer reset); `_Layout.cs` at 504 lines gets no new code; tidy's condition tightened and its limit named; the
  survivor's e1 gate drops the saturation clause; a hung quit is 2d's escape.
- **Rev 0 → 1, harness review:** the split's 30 s cooldown refused the merge → wait `splitCooldownSeconds` + 5 s, retry
  once; "C applied the whole base" matched its very first layout → wait for the tidy pair; the 300 s stand window
  could expire → 480 s for `merge` and a window guard; the harvest bot writes its RESULT before the merge → graded by
  `migrations=1`, its C.1->C handoff and two `fellBack=False` lines; fallback hops count as migrations → no
  `fellBack=True`; the apply expectations were missing and the split graders trip on merge lines → the table above
  and an own layout grade; the status parser needs the tidy pair, exited, retire sent, straggling, and a kind filter;
  `Channel removed` is not mirrored → `Merge <op>: channel <B> removed`; B quitting before its neighbours applied e3
  made the neighbours' checks racy → B quits on Retire only, and link loss of a Draining zone is its own Info line;
  "every cell that banded with C.1" → every cell that applied e3, C2 included; the process check from C.1's quitting
  stamp, one CIM query per sample, exit time a note; C.1's quit is the first graded shutdown (risk named); statics in
  R may be gone by the merge → conditional; the process counter cannot go into `rigtools.py` (520, pure) and
  `regress_split.run` must be phased; the tidy's purpose is checked through the store file; decision 7 is not run
  live; `dynidle` added to the canary.
- **Implementation deviations (2026-10-17), kept:** merges number from their own counter (`merge-1` after `split-1`;
  the harness grades any `merge-<n>`); "exited before drained" also needs 5 s continuously unregistered and removes the
  channel before e3; B unregistering before e2 aborts on the first tick; restart detection keeps typed incarnation
  fields per op (split: parent, child; merge: survivor, retiring); a restart of A or B after e2 is logged as a plain
  (not mirrored) line, the merge goes on; freeze reasons read `e2|e3|tidy not acknowledged in 60 s: <wait>`; the wake
  refusal of a family's base reads `split-family base cell`. Map side: dead monsters, drops with no items left and
  harvestables at 0 HP are not counted (nothing would hand them off); `serverCharacters` is always 0 (a server-owned
  character's handoff has no ack; one still present counts as a character); an idle connection is one with no
  registered character and no pending spawn for 10 s; the straggling clock starts at the first Retire; the prune line
  replaces 2b's `Link to retired cell <id> closed`; not pruned: `_rolledBackHandoffs` (no zone recorded),
  `_reservedArrivals`, queued pre-warm spawns from Z's connection; between B's exit and e3 the neighbours keep
  reconnecting to B (log noise only, a few seconds). Harness: the merge scenarios grade the split phase's status only
  (its entity and C2 checks belong to `split`/`splitc2`); a cooldown refusal is retried once after 10 s; `regress.py`
  grew 9 lines (already over the size rule).
- **Live runs (2026-10-17):** the first `merge` passed 141/142: the merge grade allowed `Drain on` lines only on the
  retiring cell, but 2b's split logs `Drain on <parent>: gave away …` on the parent → a cell may log `Drain on` about
  itself. Then every scenario passed (below); the retiring cell's process was gone 7-9 s after its quitting line.
