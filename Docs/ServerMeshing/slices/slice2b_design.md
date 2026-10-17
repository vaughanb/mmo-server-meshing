# Dynamic cells, Slice 2b: manual split of a Dynamic cell (implementation contract)

Revision 1, 2026-10-16 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end). Slice 2a (DESIGN 0.72) built the per-map mode, the runtime layout overlay, the coordinator skeleton, the
gate and the routing; Phase 0 (DESIGN 0.73-0.75) measured what a split costs. 2b makes one operation work end to end:
**an operator (or the harness) splits a Dynamic cell at a line; a child process X takes the far side R; players,
monsters, statics and item drops move to it; the parent P keeps the rest**. Merge, drain-to-retire, supervision,
automatic policy, buildings and central-restart recovery are later slices.

Read first: this contract; the plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D3 (:141-198), D4 (:199-245), D5
(:246-278), D6 (:279-305), D7 (:306-316), D8 (:317-326), Slice 2b (:389-411), the per-slice gate (:328-340); the
build sheet's re-plan (`phase0/phase0_build_sheet.md` §6 :1175-1190); DESIGN 0.72-0.75. Where they disagree: THIS
CONTRACT, then the re-plan, then the plan (its line references have drifted; the 2026-10-16 surveys give current ones).

User decisions (2026-10-16): start 2b; **Dynamic maps default to kinematic puppets** (Static maps keep steered;
`MESH_PUPPET_MOTION` still overrides) — an approved exception to "byte-identical for Dynamic maps that never split".
Choices the user has not seen are marked (D).

## Rules
DESIGN's standing rules: Mesh/ is game-neutral and never names TestMap/Tests types; **Static maps byte-identical**
(every new behaviour below is gated on a Dynamic map or on an op in progress); log lines one physical line, invariant,
no braces, never "Exception"; files under ~500 lines (new partials and new modules: `MeshMapNetworkManager.cs` 1,139,
`_Ghosting.cs` 3,683, `regress.py` 1,195, `ServerMeshAutoClient.cs` 1,457 lines); kit edits only as numbered CE patches
(none planned); no agents, edits or compiles while a rig chain measures; commits only when the user asks.
`ServerMeshAutoClient.cs` may be edited in slice work (new code in a partial file).

## Decisions

### Puppets
1. **Kinematic by default on Dynamic maps.** At `PuppetsOnStartServer`: `MESH_PUPPET_MOTION` wins when set; else a
   cell of a Dynamic map uses Kinematic; else the inspector field. Logged once. **The 30 fps G3-F set** (never run in
   stage 0a) runs as part of acceptance: `p0load presence --rates 30/0.02`, 66 runs (~2.3 h), on the 2b build with its
   own baseline (the block-reference canary rule), High performance, A idle; a FAIL is reported to the user, who
   decides whether the default stays.

### The split operation (central: `MeshLayoutCoordinator.cs`, new `MeshSplitOperation.cs`)
2. **Entry and validation.** `RequestSplit(baseCellId, axis, at, childSide, childId)` from a
   `Temp/ServerMesh.LayoutCommand.txt` line (`split C x 0 +x C.1`, with a sequence number) that the PlayRunner forwards
   to the central (its leg runs before the Play-return in `Tick`, tied to `playSeq`), and a public method for later.
   Validated against the live layout: the child and P∖R no narrower than `minCellSize` and the band (rule 7 binds
   both), tiling kept, and a new rule `maxCellsPerBase`. Refused with `Layout op refused: <rule>`. **One staged step
   world-wide** (the coordinator's single staged slot, plan D3 :143).
3. **Steps** (plan D4; the store is written before every step): `ChannelAdded` (the kit's live `Channels`
   dictionary) → `Spawning` (`RequestSpawnMap` to the base's spawner; the add-on removes the kit's
   `_mapSpawnResultActions[key]` after its first call and ignores any later one, which the kit fires on every later
   registration, and a callback after an abort) → `Bound` (X's first LayoutHello after the spawn binds its
   incarnation; the kit's callback fires before `_mapServerPeers` is set, so binding waits for the hello) → **e1**
   staged to X (X Pending, owned ∅, interest R) → X's ack → e1 broadcast → **e1 gate** (decision 6) → **e2** staged to
   X (X Active owning R; P owns P∖R, interest still P) → X's ack → e2 broadcast → **drain gate** (decision 8) → **e3**
   broadcast (P's interest becomes P∖R, or owned + straggler bounds) → `Done` + cooldown 30 s. `stepId` carries
   `<op>/e1|e2|e3`; an ack's pair ≥ the step's pair counts as that step's ack (the ack wire has no stepId; the pair is
   enough).
4. **Participants and acks.** A step's participants are X, P, and every cell on any map whose band-neighbour set or
   rectangles change with it (C2 for a cut that touches it). A gate needs an ack (pair ≥ step) from every participant.
   Before e2, a Rejected ack or a participant silent past `splitStepTimeoutSeconds` (60) aborts; after e2 it freezes the
   op (`Split <op>: frozen ...`; supervision is 2d). Staged messages are target-checked on the map side (today only
   HelloAnswer is).
5. **X's hello answer.** While a step is staged for X, X's hello is answered with the staged pair (Kind HelloAnswer,
   stepId set), so `CanHoldLayout` accepts it and X's gate opens. A hello from a dynamic id that is in neither the
   broadcast nor the staged layout, or that the coordinator aborted, is answered with Retire.
5a. **Abort** (before e2): the coordinator adds the id to an aborted list, removes the channel, and, if the e1
   broadcast went out, broadcasts a new version with X Retired (`StateFits` allows it). **Map-side Retire** (2b's
   share of 2c's retire): a cell that is Pending or holds no layout, and whose `LayoutCellId` and incarnation match,
   quits through the kit's quit path (`Split <op>: X retired before it owned anything`); the spawner cannot kill one
   process, so the cell ends itself.
5b. **Recovery fix.** A hello whose held pair comes from the current coordinator incarnation does not reopen recovery
   (`MeshLayoutCoordinator.cs:260-270`, `MeshRecoveryResolver.cs:62-67`); today any later re-registration freezes a
   split map. Restoring a stored layout after a central restart stays in Slice 5 (the harness deletes the store before
   each scenario, `regress.py:701-705`).
5c. **Sleep and timings.** A split family is never slept (`AwakeTick` skips dynamic cells and their base, plan :181); a
   `RequestWake` for a dynamic id is refused with a log line. `handoffAckGuardSeconds` moves into `dynamicSettings` so
   the central can compute the drain timeout.

### Pre-warm (e1 gate) — Dynamic maps only
6. **Receiver evidence.** `MeshLayoutEvidence` (30019, cell → central, each load window and on change; carries the
   cell's applied pair and the window's start): per band neighbour, the time since its inbound MeshHello (recorded on
   arrival; today only connection → zone is kept) and the datagrams received on that link since (any snapshot or the
   link's own pings: the link packet counter already exists); the pre-warm queue length; the work window's saturated
   share. The e1 gate opens when X reports, for a window at or after e1: every band neighbour's hello ≥
   `prewarmSeconds` (2) old with ≥ 1 datagram since, the queue empty, and saturated share 0 (a capped cell; an
   uncapped one uses p95 ≤ the nominal budget). The load report keeps its frozen wire.
7. **Paced puppet spawns.** b8 measured all-at-once bursts: 100 kinematic puppets cost the window 25 ms/s (steered
   48), 650 cost 46 (steered 307), with worst frames of ~0.2 s at 100 and ~2.3 s at 650; the one paced arm (650 at 50
   per frame) read worse (86 ms/s), an open oddity. So on a Dynamic map, ghost spawns from snapshots go through a queue
   served by a per-frame time budget `prewarmSpawnBudgetMs` (default 25 % of the frame budget: 4 ms at 60 fps, 8 at
   30), nearest-to-own-rect first, later snapshots for a queued id folded in. **Promotion, arrival and GhostRemove
   consume a queued entry**; an id that becomes owned is dropped from the queue (no duplicate of an owned entity).
   `split` logs the max frame ms during pre-warm.

### Drain (e2 gate) and pacing — only while an op is in progress
8. **Drain gate on P's evidence.** For a cell whose owned rect shrank, the evidence counts, by rectangle with the
   handoff hysteresis, what it still owns inside the given-away rect R: characters, pets and mounts, monsters (the
   plan's age clause: a monster younger than `drainMonsterAgeSeconds` (5) that P's spawn area just made in R is handed
   off, not counted), statics and item drops, synthetic players; **plus handoffs toward X pending an ack, rolled back,
   or reserved**. A window counts only if its pair ≥ e2 and it started after P applied e2. The gate opens at all zero
   over one window; the timeout is `N_R / drainRate + handoffAckGuardSeconds + 10 s` (N_R from the first qualifying
   window), after which e3 shrinks P's interest to owned + straggler bounds and logs the stragglers.
9. **Pacing as admission control** inside `MeshBoundaryTick` and `MonsterHandoffTick`, after their existing filters
   (`_handoffsInProgress`, `_handoffRetryAfter`; no separate queue): a **drain** handoff is one whose entity stands in
   the cell's previous owned rect and moves under 2 m/s (plan D5 :265); others are movement crossings and bypass.
   Characters: `drainRatePerSecond` 5 (the database's sustained capacity, DESIGN 0.75; one staged op world-wide, so
   one cell drains at a time and the per-cell rate is the cluster's), `maxConcurrentHandoffsPerDestination` 20 as a
   safety cap, nearest past the line first, ±20 % jitter, no re-drain of one player within 2 s. Monsters and statics,
   which do no database I/O, have their own `drainEntityRatePerSecond` (20). The plan's 4/8/12 per second test is
   replaced by this default (build sheet §6 :1181).

### Entities (new partial `MeshMapNetworkManager_StaticHandoff.cs`; link ids 124-128: 121-123 are reserved)
10. **Statics and item drops** are handed off **continuously by position** on Dynamic maps: any one a cell owns that
    stands in another cell's owned rect (a respawn from P's spawn area in R included) goes to that owner. P **demotes
    on send** (its copy becomes a puppet; interactions on it are refused while in flight) and records the loan;
    `StaticHandoff` (124) carries meshId, prefab id, pose and the state that matters (harvestable HP and respawn
    timing, item drop contents and appear time); X **dedupes by meshId**, promotes its puppet in place or spawns from
    the registered prefab, and answers `StaticHandoffAck` (125) or `StaticHandoffFailed` (126), on which P re-promotes;
    a link loss re-promotes everything still in flight after the ack guard. `LentStatic` (127) tells the spawn-area
    owner its static now lives on X, so its respawn timer and a later merge can account for it. **Reward drops**
    (exp, gold, currency; not ghosted, prefabs from GameInstance) stay with the killing cell as the plan says (D6
    :285) and are excluded from the drain counts; a stray one expires on P.
11. **Monsters.** `MonsterHandoffFailed` (128): a receiver that cannot spawn one (unknown prefab) answers and the
    sender re-promotes instead of leaking its lent record (`Ghosting.cs:2647-2658`). The new-incarnation release covers
    statics as well as monsters.
12. **Prefabs on the child.** Registration generalises the harvestable version (`Ghosting.cs:2119-2145`) to monster,
    harvestable and item-drop spawn areas (`FindObjectsInactive.Include`), so X (which disables every spawn area) can
    spawn puppets and handed-off entities. The spawn filter tests a spawn area's **base** rectangle, not the live owner.
13. **Synthetic players** (rig): TestMap's adopter lets P hand a load player to X: despawn on P, spawn at the same pose
    on X with its brain re-seeded deterministically (seed = hash(id, handoffs)) and its wander area clamped to the new
    owner's owned rect. Mesh/ gains `IMeshServerCharacterAdopter` (game-neutral) and calls it for server-owned
    characters.

### Neighbours on other maps
14. **Static neighbours of a Dynamic map** (C2 beside the test map) are bound by the coordinator without the
    mode-mismatch note, receive broadcasts, apply only the band-neighbour part (logged `Layout <pair> neighbours
    applied: <cell> band ...`, distinct from a dynamic cell's `Layout ... applied: <cell> owns`), ack, and never gate.
    `dynidle` and `layout_apply_violations` learn the new line (C2 applies no own-cell layout, as today).

### Harness and scenarios (new modules `regress_split.py`, an AutoClient partial)
15. **Auto client.** `stand` polls `ClientMigrationController.CompletedCount` (counting from its "Standing at" line),
    logs `MIGRATION n: gap=<ms> ready=<bool> jump=<m> pos=<x,z>`, adds migrations, allReady, maxGap, maxJump to its
    RESULT; `-meshObserveSeconds` sets the stand window; `-meshLineX` is the cut's x (stand logs which side it starts
    on). A **harvest variant** (`-meshHarvestAfterMigration 1`): waits for a migration, then targets local nodes with x
    ≥ lineX + 50, harvests until a yield lands in its bag, picks up an item drop (`CallCmdPickup`), and passes on the
    yield and the pickup. The PlayRunner forwards the new settings.
16. **Test map content** (`TestMap/Scenes/ServerMeshTestMap.unity`): a `HarvestableSpawnArea` near (70, -60) and an
    `ItemDropSpawnArea` near (75, -20) (40 m from the node, inside the 80 m view), both inside C's x > 50 (made
    through the Unity editor tools, not by hand); every
    scenario's baseline is re-run with them.
17. **Op visibility.** The PlayRunner mirrors the coordinator's op lines to `Temp/ServerMesh.LayoutCommandStatus.txt`
    (seq, accepted/refused + rule, each step, Done/aborted/frozen, stragglers). The harness clears both LayoutCommand
    files at every scenario start.
18. **Scenario `split`** (Dynamic mode; cut x = 0 of C, child +x `C.1`): 8 bots placed by `reset_bot_positions(2, 8,
    ...)`: 4 in x[10, 90) and 4 in x[-90, -10), at |z| 70-95 (outside `Spawn_Interior_C`'s 60 m radius, so its monsters
    reach R but not the bots); the fixture `meshbot` as the harvest bot in R; the tester parked in P's half; 40 spread
    synthetic players on C (`MESH_LOAD_PLAN C:40`). The command is sent once every bot logged "Standing at". Wait for
    the status file's Done and C's apply with interest == P∖R. Checks: R-bots migrations 1, allReady, maxGap < 1,000
    ms; P-bots migrations 0; one `Handoff START <bot> (<id>) zone C->C\.1` per R-bot; ≥ 1 `Monster promoted by handoff
    … C->C.1` and 0 `spawned`; the harvest bot's yield and pickup; X adopted the synthetics in R and P's last evidence
    reports 0 left; no abort, freeze or straggler; 0 player teleports and 0 exception lines on every discovered cell;
    the rectangle grade expects three extra applies on every cell of the map.
19. **Scenario `splitc2`**: split E at x = 200, child E.1 = [200, 300] touching C2 at 300; bots off z = 0 (E's portal,
    spawn point and interior spawn lie on z = 0 near x = 200). Checks: C2 logs `Neighbour zone E.1 linked … frame
    offset (-400`, C2 sees an R-bot's puppet at its translated position, E leaves C2's band after e3; plus `split`'s
    migration checks. Both scenarios join `ALL`, `TWO_HOST_SKIP` (B has no spawner) and `PERSIST_SKIP`.

### Acceptance (the plan's per-slice gate, :328-340)
20. EditMode tests (op state machine, gates, pacer predicate, spawn queue consumption, evidence codec, staged answer,
    map-side retire, recovery fix, static handoff dedupe); Python tests and `py_compile` for the graders; ruff; **zero
    new compiler warnings** in Assets/ServerMeshing; the stray check; **all 11 scenarios** Static and `all:Dynamic` idle,
    then `split` and `splitc2` 3/3 each (flake policy: one rerun, recorded); the 30 fps G3-F set (decision 1); DESIGN
    0.76; `ServerMeshPackageBuilder` Version and UpdateDate bumped (today 0.72.0 / 2026-09-26); the slice's docs
    checklist.

## Interfaces and log lines (exact; the writers and the graders rely on them)
- **Shared types (written first, 2026-10-16):** `Mesh/MeshLayoutEvidence.cs` (`MeshLayoutEvidenceMessage`, 30019 =
  `MeshClusterMsg.LayoutEvidence`), `Mesh/IMeshServerCharacterAdopter.cs` (`IMeshServerCharacterAdopter`,
  `MeshServerCharacterAdopters`). Link id 129 = `ServerCharacterHandoff` (kind, state bytes, pose).
- **Command file** `Temp/ServerMesh.LayoutCommand.txt`, one line: `<seq> split <baseCell> <x|z> <at> <+|-> <childId>`.
  The PlayRunner hands it to `MeshCentralNetworkManager.SubmitLayoutCommand(string line, out string error)` and mirrors
  every coordinator line starting `Split ` or `Layout op ` into `Temp/ServerMesh.LayoutCommandStatus.txt` (one line
  each, prefixed with the command's seq).
- **Coordinator** (central; op ids `split-<n>`): `Split <op>: requested <base> <axis> <at> <side> child <id>`; `Split
  <op>: <state>` for each of `channel added`, `spawning`, `bound <incarnation>`, `e1 staged`, `e1 acked`, `e1
  broadcast <pair>`, `e1 gate open`, `e2 staged`, `e2 acked`, `e2 broadcast <pair>`, `drain gate open` or `drain
  timeout, <n> straggler(s)`, `e3 broadcast <pair>`, `done`; `Split <op>: aborted <reason>`; `Split <op>: frozen
  <reason>`; `Layout op refused: <rule>`.
- **Map side:** a dynamic cell's apply line stays `Layout … applied: <cell> owns … interest …` (today's format,
  `MeshMapNetworkManager_Layout.cs:308`); a Static neighbour logs `Layout <pair> neighbours applied: <cell> band
  <ids>`; a retired child `Split retire: <cell> retired before it owned anything`; `Puppet motion: Kinematic (Dynamic
  map default)`; `Prewarm window 5.00 s: queue <n> spawned <n> | max frame <ms> ms`; statics `Static handoff <meshId>
  zone <from>-><to>: sent|promoted|spawned|failed|released`; the TestMap adopter `Synthetic adopted <name> zone
  <from>-><to>`.
- **Auto client:** `MIGRATION <n>: gap=<ms> ready=<True|False> jump=<m> pos=<x>,<z>`; RESULT gains ` migrations=<n>
  allReady=<bool> maxGap=<ms> maxJump=<m>` (and for the harvest variant ` yield=<n> pickup=<bool>`).

## Files
Mesh/: new `MeshSplitOperation.cs`, `MeshLayoutEvidence.cs`, `MeshMapNetworkManager_Pacing.cs`,
`MeshMapNetworkManager_StaticHandoff.cs`, `MeshMapNetworkManager_Prewarm.cs`, `IMeshServerCharacterAdopter.cs`;
`MeshLayoutCoordinator.cs`, `MeshRecoveryResolver.cs`, `MeshCoordinatorStore.cs`, `MeshClusterCoordinatorTransport.cs`,
`MeshCentralNetworkManager.cs` (channel add, spawn binding, AwakeTick), `MeshLayoutMessages.cs` + `MeshSleep.cs` (30019),
`MeshLayoutValidator.cs` (maxCellsPerBase), `MeshLayout.cs` (dynamicSettings fields), `MeshMapNetworkManager_Layout.cs`
(staged answer, target check, retire, evidence, static-neighbour apply), one-line hooks in `MeshMapNetworkManager.cs`
and `_Ghosting.cs` (puppet default, pacer admission, spawn queue, prefab registration, spawn filter,
MonsterHandoffFailed), `CellMeshLink.cs` (124-128). TestMap: an AutoClient partial, `Editor/ServerMeshPlayRunner.cs`,
the synthetic adopter, `Scenes/ServerMeshTestMap.unity`. Tools: new `regress_split.py`, `rigtools.py` (graders),
`regress.py` (registration of the scenarios, LayoutCommand clearing); tests in `Tests/Editor/` and `Tools/ServerMeshing/`.

## Critique log
- **Rev 0 → 1 (2026-10-16), protocol and Mesh/ review:** the abort relied on a Retire that does nothing (X orphaned,
  a late spawn callback firing Success, MAP_EXISTED on retry) → map-side retire for a Pending/layout-less cell, retire
  answers to unknown or aborted ids, a Retired version after an e1 broadcast; ack semantics were undefined and C2's
  role contradictory → participants and acks per step, abort before e2 and freeze after, staged target check; the
  drain evidence was not tied to e2 and missed in-flight handoffs → pair and window start, pending/rolled-back/reserved
  counted, the age clause back; e1 inputs not computable (no static tick from most neighbours, no hello time, no
  frames-over-budget figure) → hello time, link datagrams, saturated share; the pacer had no drain/movement rule and
  could double-start → admission control after the existing filters, the plan's predicate, monsters on their own
  rate; statics were handed only on a layout change and the demote point and failure path were open → continuous by
  position, demote on send, ack/failed, release on link loss, meshId dedupe, reward drops stay with the killer as the
  plan says; the spawn queue could duplicate owned entities and touched Static maps → consumed by promotion, arrival
  and GhostRemove, Dynamic maps only; kit facts on the spawn callback corrected; maxCellsPerBase a new rule, rule 7 on
  P, one staged step world-wide, store written every step, ack guard in dynamicSettings, split families never slept.
- **Rev 0 → 1, harness review:** the test map had no harvestable or drop inside R and harvest mode cannot loot or
  wait for a migration → scene content and a harvest variant, the fixture meshbot as the harvest bot; the e1 gate's
  static tick could never arrive (same fix as above); C2's new applies would break `dynidle` and the apply grader →
  a distinct neighbour line, graders updated; the central writes no log the harness reads → a mirrored status file;
  bots inside `Spawn_Interior_C` could be killed → placed at |z| 70-95; the spawn queue broke Static byte-identity and
  b8 was misquoted → Dynamic only, quoted from the results; the synthetic adopter was never exercised → 40 synthetics
  in `split`, re-seed and clamp rules; drain rate is cluster-wide, monsters separate; harness details (clearing,
  PlayRunner leg placement, waiting for "Standing at", the Handoff START regex, ALL/TWO_HOST_SKIP/PERSIST_SKIP, new
  modules); `splitc2` specified; acceptance completed against the plan's gate (warnings, py_compile, UpdateDate, docs,
  flake policy, 11 scenarios); the G3-F set's cost and failure handling stated.
- **Implementation deviations (2026-10-16), kept:** the drain timeout leaves P's interest at its old rectangle at e3
  (the evidence counts stragglers but does not place them); once P reports zero at or after e3, a `settle` version
  shrinks it, only while no other op runs (`Split <op>: stragglers gone, settle broadcast <pair>`). `done` waits for
  every participant's e3 ack. An e1 gate that X keeps reporting on but that stays shut aborts at the 60 s step
  timeout, naming the unmet condition. Both gates need `windowStartedAfterApplySeconds ≥ 0`; the on-apply evidence is
  sent with a negative offset so it never opens a gate alone. An uncapped cell sends `budgetMs` 0 and the central
  judges its p95 against 1000/60 ms. `maxCellsPerBase` is enforced by the coordinator, not `ValidationWorld()`. The
  "reports layout X but acknowledged Y" warning fires only when the report is ahead of the ack. The store gained
  `opId`, `opStep`, `abortedIds`. Static handoff: 125/126 share one reply struct, 127 carries `gone` (the home spawn
  area respawns it), 129 carries the meshId; only character puppets are queued; buildings are neither handed nor
  counted (Slice 4), so a building in R never drains. A Static cell banding with a Dynamic map says hello with
  `dynamicMap=false`. The owner-change line `Ghost owner changed <Kind> <name> (<id>) zone <old>-><new> at (x, y, z)`
  logs only once the cell has applied a runtime layout or a neighbours line; the `splitc2` grader takes it or a fresh
  `Ghost spawned`. The test adopter confines the wander areas of the synthetics a shrunk cell keeps to what it still
  owns (`Synthetic wander areas confined on <cell>: ...`), Dynamic maps only, else they walk into R and P never
  drains. Test map content: `HarvestSpawn_C` at (70, -60) (a copy of `HarvestSpawn_E`) and `ItemDropSpawn_C` at
  (75, -20) (first placed at (75, 40), 100 m from the node and out of the bot's view) with the new
  `TestMap/Prefabs/TestItemDrop-CE.prefab` (a copy of the kit's drop with its own asset id, one
  `DefaultWeaponItem-CE`, the harvestable's yield).
- **First live run (2026-10-16), two harness gaps:** the harvest bot was launched in `stand` mode, so it never
  harvested (it now runs `harvest` with `-meshHarvestAfterMigration 1`); and C owns no monsters on the test map (every
  monster area but `Spawn_Boundary_X100_Z60`, E's, is saved inactive), so `split` now sets
  `MESH_MONSTER_AREAS=Spawn_Interior_C` (the knob also takes a comma list of area names). Its five monsters straddle
  the cut, so about 1 run in 32 has none on the child's side; such a run fails the monster check and is re-run.
- **Live runs 3-5 (2026-10-17), graders and one log line:** `split` respawns: P keeps its spawn area and respawns
  monsters inside R, handing them over before the child has a puppet, during the drain as well as after; the monster
  check now fails only a monster that existed during the pre-warm (its number at or below the highest the child
  ghosted before e2) yet was spawned by handoff, and reports respawns as a note. A monster idling on the cut is handed
  back and forth every 5-10 s (the border hysteresis does not hold one standing on the line), as on any border.
  `splitc2`: E.1's hello reached C2 before C2 applied the e1 broadcast naming it, so the link came up with no offset;
  the apply already recomputed it (`MeshFrameOffsets.Recompute`; puppets were placed right) but said nothing, so it now
  logs `Neighbour zone <id> frame offset <offset> from layout <pair>` for each non-zero offset it sets, and the C2
  check takes that line or the link line.
