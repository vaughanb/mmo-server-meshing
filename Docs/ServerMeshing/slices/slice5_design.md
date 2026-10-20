# Dynamic cells, Slice 5: a central restart with a cut map (implementation contract)

Revision 1, 2026-10-19 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end) of revision 0, itself from three surveys (central, map side, harness; the notes `slice5-surveys.md` in
the session's shared folder) of HEAD 1117333 plus the uncommitted Slice 4 tree (DESIGN 0.81). 2a-4 run a Dynamic
map's cells under one central: a central that restarts with any runtime layout anywhere freezes that map (recovery
adopts only the asset cut; a stored or held dynamic entry is "Slice 5"). 5 makes the central replaceable: **a
restarted central adopts the layout its store and the cells hold, re-adds the dynamic cells' channels, answers every
hello with it and routes logins by it; an operation it was running is retired, resumed at its gate or carried to its
end from what the store says, never regressed; the policy starts with fresh cooldowns; a base that crashes while the
central recovers stays gated until the answer and never re-owns a child's rectangle; and the rig gains the
split-process topology the plan asks for, so the harness can kill and restart just the central.** The operator path
(6) stays the last slice.

Read first: this contract; `slices/slice2b_design.md`, `slice2c_design.md`, `slice2d_design.md`, `slice3_design.md`,
`slice4_design.md` (their decisions hold unless changed here); DESIGN 0.72 (2a's recovery), 0.77-0.81 and §17; the
plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D3 "Recovery after a central restart" (:156-166), D7 (:306-316), D8
(:317-326), Slice 5 (:447-451). Where they disagree: THIS CONTRACT, then 4's, 3's, 2d's, 2c's, 2b's contracts, then the
plan.

User decisions: proceed with Slice 5 (2026-10-19, through the session that started this one). Choices the user has
not seen are marked (D).

## Rules
As 2b-4: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical** but for
two start-up details named below (a Static neighbour of a Dynamic map is answered once the Dynamic maps recovered, not at
once; and the login waits while a Dynamic map recovers); log lines one physical line, invariant, no braces, never
"Exception"; files under ~500 lines: `MeshMapNetworkManager.cs` (1,147) and `_Ghosting.cs` (3,719) get one-line hooks
only, `MeshLayoutCoordinator.cs` (455) loses its recovery to a new partial and takes hooks only, `_Split.cs` (484) one
line (the frozen-family refusal; `Answers(outcome)` lives beside the resolver), `_Layout.cs` (445) two lines; **kit
edits: none planned** (an implementer that finds one necessary stops and reports); no agents, edits or compiles while a
rig chain measures; commits only when the user asks.

## Settings
No new `MeshDynamicSettings` field: `recoveryCeilingSeconds` (60) is the ceiling. `MeshLoginNetworkManager` gains the
inspector field `recoveryWaitSeconds` (15; D: under the client's 30 s request timeout with the despawn confirm and the
database read after it): how long a login waits for a recovering central before it routes.

## Kit facts the design rests on (2026-10-19 surveys and critiques)
- The kit's cluster server keys a map server by `{channelId}_{mapName}` and **never checks the central's channel list**
  (`ClusterServer.cs:166-240`), so a child registers with a restarted central before its channel exists; the login's
  select-character and the channel-limit check read `Channels` (`UI_ERROR_INVALID_CHANNEL_ID` for one the central
  lacks), which a central rebuilds from its config and the registry's cells at start (the runtime-added children are gone).
- A map server's cluster client reconnects by itself (a 5 s countdown, `ClusterClient.cs:117-133`) and re-registers (the
  add-on retries a refusal); the hello goes out on every accepted registration with the whole layout it holds; the gate
  never closes again; the social mirror is purged on the drop (0.70). The database connection is re-opened by the add-on
  only, after up to `databaseDrainWaitSeconds` (35 s) plus `databaseReconnectSeconds` (5) (0.63): a killed central that
  hosts the database manager costs every cell up to ~40 s of failed saves, during which character handoffs fail and retry.
- A restarted base's hello holds no layout and the cell stays gated until answered (2d decision 11). **The kit's spawner
  restarts a dead cell only while it is registered** (`MapSpawnNetworkManager.Update` drains `_restartingScenes` under
  `IsAppRegistered`, `:220-230`; the exit handler only enqueues, `:486-495`): a base killed while the central is down comes
  back only after the spawner re-registered with the new central.
- `SaveStore` is called **before** `EnterStep` today (`StageE1`, `StageE2`, `BroadcastStep`, `StageMergeE2`), so the file's
  `opStep` is one step behind the snapshot it carries, and `e1Broadcast` is set after the broadcast's save; `Freeze` saves
  the staged snapshot; `TrySettleStragglers` saves before it sets `settled` (decision 6 fixes the order).
- Once a split's parent applied e3, its `_splitOpInProgress` is false and `_splitShrunk` cleared (`_Pacing.cs:236-263`):
  its evidence no longer qualifies a drain window, so a drain gate re-entered after e3 can never open (decision 7).
- `OnLayoutAck` and `OnLayoutEvidence` call `Advance()` with no Phase check (`_Split.cs:236, 349`); `OpTick` checks it.
- The kit's spawner starts cells with no `WorkingDirectory` (`MapSpawnNetworkManager.cs:395`): a spawner started with cwd
  = the project root makes the cells log into the project's `Logs/` and read the project's `Config/`; `-spawnExePath` is
  honoured in a build and must be absolute. A build reads `./Config/serverConfig.json` and `./Config/mySqlConfig.json`
  from its cwd, arguments winning; the project's `serverConfig.json` differs from the build's (centralPort 6010 against
  7000, mapPort 8000 against 7770, publicAddress), so every port the central and the spawner bind goes on the command line;
  **the cells read `clusterPort` 6010 and `databaseManagerAddress/Port` 127.0.0.1:6100 from the project's
  `Config/serverConfig.json`** (the spawner's `-centralPort` sets a cell's `CentralNetworkManager.networkPort` only). The kit's
  file logs are `Logs/Log_Database_Central_Login_<day>.<sev>.log` (the name's order is fixed in code whatever the argument
  order) and `Logs/Log_MapSpawn_<day>.<sev>.log`, one file per day shared by every lifetime, the cells' stamp format.
  **`-spawnMaps` takes ONE token split on `|`** (`ConfigReader.cs:153-165`): `-spawnMaps "ServerMeshTestMap|ServerMeshTestMap2"`;
  absent, the cwd config's `["BaseMap-CE"]` wins and no test cell starts.
- In the editor `MMOServerInstance.Awake` starts the servers the scene's four `start*OnAwake` flags name
  (`MMOServerInstance.cs:750-773`); nothing reads them before `Awake`; Unity honours `DefaultExecutionOrder` for the Awake
  of scene components (the add-on relies on it already); `StartServers` runs after the game data loaded, a frame later.
- The spawner keeps its cells alive when the central dies (a cluster drop only reconnects; `Clean()` kills children on
  `OnStopServer`/`OnDestroy`); on re-registration the Mesh spawner returns before the kit's handler (`MeshSpawnOnce`), so
  nothing is respawned. `File.AppendAllText` opens with `FileShare.Read`: two processes appending to one status file throw
  `IOException` at each other (the runner swallows it).

## Decisions

### Recovery (central; new partial `MeshLayoutCoordinator_Recovery.cs`, pure `MeshRecoveryResolver` extended)
1. **Start and the wait set.** As 2a: the store loads (unreadable → moved aside, untrusted), the incarnation is taken and
   saved, the fenced set and aborted ids load. New: `_broadcast` is the **stored broadcast** (its old pair and entries)
   when there is one, else the empty `(Incarnation, 1)` snapshot; nothing is applied to the registry yet; `_ownBroadcastSent`
   is false until this central's first `SendBroadcast` (a step, an abort's Retired or Reverted version, a settle). Per
   Dynamic map the outcome is Waiting until **every registered map server of the map and every dynamic id named for the
   map** has said hello: the ids with a live entry (Pending, Active or Draining) in the stored broadcast or staged snapshot
   and every live dynamic id a hello's held layout names (`MeshRecoveryInput.namedIds`, grown as hellos arrive), **except
   the stored operation's own dynamic id when its absence is the step's expected outcome**: a split's child while the op is
   before its commit broadcast (steps ChannelAdded to E2Staged: its absence aborts), and a merge's retiring cell from the
   commit broadcast on (DrainGate, Retiring, E3Broadcast, TidyBroadcast: its absence is the exit the step waits for). The
   base of a cut family is a registered server or nothing (a crashed base is waited for only once it registers again).
   Nobody of a Waiting map is answered. **A hello from a Static map's cell is answered only once Phase is Running (D):** such
   hellos pend on a coordinator-level list (`_pendingStatic`, by connection), because answering C2 at once with the empty
   `(Incarnation, 1)` snapshot would make it apply a newer, empty layout, prune its link to C.1 and reject the adopted layout
   as stale. Their held snapshots take part in decision 2 (they carry the Dynamic maps' entries too). The first Dynamic
   start answers C2 a few seconds later than today; its gate never closes, so nothing else changes.
2. **Candidates and the choice.** The candidates for the world's runtime layout: the stored broadcast (when trusted),
   every hello's held snapshot that holds entries of **any** Dynamic map (any map's hello, any coordinator incarnation;
   `HeldEntriesOf` counts the Dynamic maps' entries, not the hello's own map's), and the stored staged snapshot **only when
   some hello holds its pair** (its first applier applied it). The **newest pair wins**; on a tie the store is named as the
   source. Two candidates at one pair with different entries (`MeshLayoutSnapshot.SameEntries` false) freeze the map with
   an error naming the two sources; the chosen snapshot must validate against the asset (`MeshLayoutValidator.Validate`, no
   previous) or the map freezes as today. Nothing stored, nothing held and no stored operation → **AdoptStatic** as today.
   Otherwise the new outcome **AdoptStored**: the central applies the chosen snapshot to its own registry (the old pair; a
   failure freezes as today), `_broadcast` is it, **every live dynamic id gets its channel back before anyone is answered**
   (`SplitHost.AddChannel`, skipped when present), every pending hello (the map's and the Static list) is answered with it
   (the cells already at that pair ack "already at pair"), the operation is resolved **once Phase turns Running** (decision
   7), the policy is told (decision 8). Line `Coordinator recovery: map <m> adopted layout <pair> from <the store|cell <c>>
   with <n> entries (<ids>)`. The snapshot is one for the world: the first map to finish waiting applies it; a map that
   finishes later with a newer consistent candidate re-applies the newer one, which is allowed while `_ownBroadcastSent`
   is false (decision 4). Every place that read `outcome == AdoptStatic` as "recovered" (`CheckOpCommon`, the supervisor's
   `CollectWatched`, the policy's participation) reads `MeshRecoveryOutcomes.Answers(outcome)` = AdoptStatic or AdoptStored.
3. **The ceiling and frozen families.** At `recoveryCeilingSeconds`: a registered map server with no hello freezes the
   map as today. A **named dynamic id with no process registered** freezes its **family** (D; the plan's "the family stays
   frozen: no ops, and no static answer"): the map adopts, with the family's entries as the layout has them, and
   `Coordinator recovery: family <base> of map <m> frozen: no hello from <ids> after <s> s` (Error). Frozen means **no
   operation** (`CheckOpCommon` refuses `family <base> is frozen: <reason>`), **no supervision and no policy** on its cells
   (both skip the family); **the family's live cells are answered with the adopted layout like everyone else** (critique: a
   gated base with a dead child would otherwise stay gated for good; their gates and 2d's registration witness already stop
   handoffs toward the missing cell). The missing id's hello **unfreezes** the family (`Coordinator recovery: family <base>
   of map <m> unfrozen: <id> said hello`, Info) and the family's stored operation, if it waits (decision 7), is resolved
   then. The alternative, adopting and letting 2d's supervisor restart the missing child, is recorded for the operator
   path: the central never saw the missing incarnation and cannot fence it by name, so the plan's freeze stands.
4. **Re-open.** A hello holding entries of a Dynamic map from **another** coordinator incarnation re-opens the map's
   recovery (Waiting, Phase Recovering, every hello of the map pending again, the choice run again over the grown candidate
   set) when **no runtime layout was adopted** (the map adopted the asset cut: the stored-less restart, the untrusted store's
   ceiling) **or the held pair is newer than the adopted one**, in both cases **only while `_ownBroadcastSent` is false**.
   The flag is explicit (critique: inferring it from `_broadcast.pair`'s incarnation made the empty `(Incarnation, 1)` look
   like an own broadcast and left a lost store a live split-brain). After the central's first own layout the hello is
   logged `Cell <c> holds layout <p> newer than the adopted <q> after the central moved on; answered with the current
   layout` (Error) and answered with the current broadcast (newer, since the incarnation is newer); a dynamic id such a
   hello names that the current layout does not is answered with Retire by decision 5 (known limit: a store lost together
   with a partitioned cell). The 2a rules stay: a held pair from this incarnation never re-opens; a held pair at or below
   the adopted one is simply answered. `MeshRecoveryResolver.HeldReopensRecovery(heldEntries, held, coordinatorIncarnation,
   adoptedRuntimePair, ownBroadcastSent)` (`adoptedRuntimePair` None when the asset cut was adopted).
5. **Retire during and after recovery.** `HelloGetsRetire` never retires a dynamic id named for a Waiting map or an id in
   decision 1's exclusion (today "not in the layout" would make a restarted child quit); fenced incarnations and aborted ids
   are answered with Retire as today. **Any Retire sent to a hello whose held layout gives its own id a live entry owning a
   non-empty rectangle (Active or Draining) carries a `fenced:` reason** (`fenced: not in the layout`, `fenced: aborted by
   <op>`; 2d decision 5: the cell saves, kicks and exits instead of staying up as a second owner); a Pending or layout-less
   cell keeps the plain reason (it quits). This covers a late child of an aborted E2Staged split and an id that only a
   losing candidate named.

### The stored operation and its resolution (store format 1, optional fields; pure resolver)
6. **The store** gains `hasOperation` and `operation` (`MeshStoredOperation`, `[Serializable]`): `kind` (split/merge),
   `opId`, `step` (the enum's text **of the step being entered**: `SaveStore` is called after the step is set and before the
   send, in `StageE1`, `StageE2`, `BroadcastStep`, `StageMergeE2` — the pre-send durability is kept, the file no longer lags),
   `baseId`, `mapId`; a split's `parentId`, `childId`, `axis`, `at`, `childOnPositiveSide`, `parentOwned`,
   `parentInterest`, `given`, `kept`, `stragglers`, `settled`; a merge's `survivorId`, `retiringId`, `survivorOwned`,
   `retiringOwned`, `union`, `collapse`, `hasSurvivorEntry` + `survivorEntry`, `hasRetiringEntry` + `retiringEntry`; the
   pairs `e1Pair`, `e2Pair`, `e3Pair`, `tidyPair`. `e1Broadcast` is **derived** on load as `stored broadcast pair >= e1Pair`
   (not stored: it was set after the save). `SaveStore` writes it whenever the operation is in progress **or Frozen**, and
   keeps a **Done split with stragglers unsettled** (its settle is pending; `TrySettleStragglers` saves after it marks the op
   settled); `hasOperation` is false for Done, Aborted and Ended (`opId`/`opStep` stay as today for an operator). A file
   without the field reads as no operation (`MeshCoordinatorStoreTests` round trip). `_opCounter` and `_mergeCounter`
   continue from the stored op's number, so op ids stay unique in the status file. `SaveFencedSet` keeps the fields (it
   writes the same data object).
7. **The resolver** (`MeshRecoveryResolver.ResolveOperation(stored, chosenPair, heldByCell)` → `MeshOperationVerdict`
   {Drop, Abort, Resume(step), ResendStaged, ResumeSettle, reason}; table-tested) decides from the stored step, the chosen
   pair and whether the staged target's hello holds the staged pair. **It runs when the map's Phase turns Running**, never
   while another map waits (critique: an "already at pair" ack would otherwise `Advance` a rebuilt op into an own broadcast
   while a map still waits; `Advance` is gated on Running too). A verdict whose outcome is Abort or an exit (the merge's
   retiring cell gone) runs even when the op's family froze at the ceiling (it repairs the family); a Resume verdict on a
   frozen family **waits**: the op stays in the store untouched and is resolved at the unfreeze.

   | stored step | verdict |
   |---|---|
   | split ChannelAdded, Spawning | **Abort** `the central restarted before e1`: aborted id, channel removed, no layout change (the child's hello, if any, gets `aborted by <op>` Retire and quits: Pending or layout-less) |
   | split E1Staged | child holds e1 → **ResendStaged**: e1 counts as acked, broadcast e1 → E1Gate; else **Abort** (as above) |
   | split E1Gate | **Abort** with the Retired broadcast (2b's abort path: the Pending child is retired) |
   | split E2Staged | child holds e2 → **ResendStaged**: broadcast e2 → DrainGate; else **Abort** with the Retired broadcast and a `fenced: aborted by <op>` Retire to the child when it is bound (its later hello: decision 5) |
   | split DrainGate, E3Broadcast with chosen == e2Pair | **Resume at DrainGate**: the drain gate re-opens on fresh evidence at or after e2 (`drainInitial` −1, deadline from the first qualifying window) |
   | split E3Broadcast with chosen == e3Pair | **Resume at E3Broadcast**: the acks come from the hello answers → Done (the family's cooldown), then `ResumeSettle` when `stragglers > 0` (the parent cannot be told to shrink again: re-entering the drain gate after e3 would freeze it, critique) |
   | split Done with stragglers unsettled | **ResumeSettle**: `TrySettleStragglers` with the stored `e3Pair` and rectangles |
   | split Frozen | staged held by the child → **ResendStaged** → DrainGate (a restart deliberately clears a freeze); else **Drop** with a warning `Split <op>: frozen before the restart, dropped: <endReason>` (the layout stays as the chosen snapshot has it; a settle pending shows in the policy's `familySettlePending`) |
   | merge E1Gate | **Abort**: the Reverted broadcast from the stored original entries |
   | merge E2Staged | survivor holds e2 → **ResendStaged**: broadcast e2 → DrainGate; else **Abort** (Reverted) |
   | merge DrainGate | **Resume at DrainGate** (`e2AckedAt` −1: the drained window must arrive a load window after the new acks) |
   | merge Retiring | **Resume at Retiring**: Retire re-sent at once and every 10 s; a retiring cell already gone counts as exited 5 s after the first tick |
   | merge E3Broadcast, TidyBroadcast | **Resume at that step** (the acks come from the hello answers) |
   | merge Frozen | staged held → **ResendStaged** → DrainGate; else **Drop** with the warning |
   | any step whose pairs are not the chosen snapshot's (a held pair newer than the store won, decision 2) | **Drop** with a warning: the store's op is stale |

   **Resume mechanics:** the op object is rebuilt from the store with `stepStartedAt = now`, the members' connections and
   incarnations from their **current** bindings (a member that came back as a new incarnation while the central was down is
   simply the member now: no pause, no abort, and 2d's new-incarnation rules apply to the next change), `pausedMembers`
   empty, `participants` of the resumed step = **every bound cell that said hello** (a superset: each acks the hello answer
   with a pair at or above the step's), `prewarmNeighbours` recomputed. Lines `<Kind> <op>: resumed at <step> after a central
   restart`, `<Kind> <op>: aborted the central restarted before <step>` (through `Abort`/`AbortMerge`; `Abort` sends the
   child's Retire to its current binding when it has one), `Split <op>: settle resumed after a central restart`. A collapse
   merge resumes as one (`collapse` stored); a child that was `CollapseQueued` (memory only) and never says hello freezes its
   family at the ceiling (the operator path). A resumed op is **not measured** by the policy (its prediction died with the
   old central); its end starts the family's cooldown as any op's does.

### Policy, login, supervision after a restart
8. **Cooldowns start fresh (D).** At AdoptStored every cut family (`FamilyOf(base).Count > 1`, or the stored op's family)
   starts the manual guard `splitCooldownSeconds` and the policy's cooldown `opCooldownSeconds` (multiplier 1) from the
   adoption moment; the flap multipliers, the measurement records, the dwells and **the supervisor's restart counts and
   stable windows** did not survive. One line: `Coordinator policy: cooldowns start fresh for <bases> after the restart;
   flap multipliers, measurements and restart counts forgotten`. (`PolicyStart` already makes a fresh `MeshLayoutPolicy`
   before `ResolveAll`; AdoptStored cannot happen inside `Start` under decision 1's wait set, so the order is safe.) An
   untrusted store whose ceiling adopts the asset cut leaves no cut family and no cooldown: a slow child still holding the
   old layout then meets decision 5's fenced Retire (2d fencing covers the rest).
9. **The login waits for recovery (D).** With a Dynamic map on the central, `HandleRequestSelectCharacterByPosition` waits
   up to `recoveryWaitSeconds` for the coordinator to be Running (200 ms polls, inside the kit's already-asynchronous
   handler) before it routes: `Login routing: waited <s> s for the layout recovery` (Info) when it waited, `Login routing:
   the layout recovery is not done after <s> s; routing by the layout as it stands` (Warning) when it gave up (then the
   registry holds the asset cut and routes to a base, whose channel the central has). After adoption the channels precede
   the answers (decision 2), so a route to a child never meets a missing channel. A frozen family's cells are live map
   servers: the routing is by the adopted layout and the kit's fallback, as today.
10. **Supervision** starts from the adopted layout (`CollectWatched` over Active cells of cut, unfrozen families). A
    cell with no binding (no hello to this central) is not watched, as today: decision 3 covers it (a named id that never
    says hello freezes its family at the ceiling).
11. **A base crash while the central is Recovering** needs nothing new on the map side (2d decision 11): the base's pending
    hello dies with its binding (unregistered), recovery goes on without it (its process is neither registered nor a dynamic
    id), the kit's spawner restarts it once it is registered with the new central (kit fact above), its new incarnation's
    hello holds no layout and is answered with its kept rectangle (Running, or pending if the map still waits); until then
    it is gated and hands nothing off, so it never re-owns R; the children release the old incarnation's borrowed monsters
    and statics on its new-incarnation hello; the central's supervisor sees its current incarnation only. The rig makes the
    crash land inside the Recovering window with a fault (decision 16).
12. **The map side's two lines (`_Layout.cs`).** An answer to a hello whose pair the cell already applied logs `Layout
    answer <pair> on <cell>: already applied` (Info) besides the ack, so a restarted central's answers are visible in the
    cells' logs (today a gate already open logs nothing). A rig fault `MESH_FAULT_DIE_ON_HELLO=<cell>:<n>` (through
    `MeshFault.FireOnce`, the marker scheme and `FAULT: dying on <what> (<key>)` line of 2d; `ENV_KEYS`) kills the cell right
    after it sent its n-th LayoutHello (`what` = `layout hello <n>`): one hook after `SendLayoutHello`'s send, the rule in
    `_Fence.cs` beside the prewarm fault.

### Rig: the split-process topology (`regress.py` plumbing in new modules; TestMap runtime)
13. **Topology.** `regress.py` learns a per-scenario topology: `editor` (today's: the editor hosts everything) for every
    existing scenario, `split` for the new ones (`SPLIT_TOPOLOGY`); `--topology split` forces it for any scenario (D),
    refused together with `--remote-cells` (the hold and the remote start assume the editor's cluster). The plumbing lives
    in new modules under the size rule: pure `rigprocs.py` (the argument builders `central_args(logs, scenario, n)` and
    `spawner_args(exe, logs, scenario)`, the PowerShell snippets `kill_by_argument_script(exe, '-startCentralServer')` and
    the port check, `central_log_path(logs, day, sev)`, the lifetimes split in `rigcentral.lifetimes` (opening at each lifetime's start-up run, see the deviations)
    `Coordinator <inc>: Dynamic maps ...`, the readiness predicates) and impure `regress_procs.py` (`start_central(n)`,
    `start_spawner()`, `kill_central()`, `teardown()`, the waits; `Popen` with `cwd = ROOT`, the Play's env plus
    `MESH_RIG_COMMAND_DIR=<abs Temp>`, each `-logFile` deleted first as `launch_bot` does). `regress.py` keeps
    `topology_of(name)`, `central_lines(start, day, suffixes)` (all three severities by default) and the call sites. In the
    split topology `request_play` itself, after the database rows are written and before the Play request, starts:
    - the **central process**: `-batchmode -nographics -startDatabaseServer -startCentralServer -startLoginServer -centralPort
      7000 -clusterPort 6010 -loginPort 7500 -databaseManagerAddress 127.0.0.1 -databaseManagerPort 6100 -publicAddress
      127.0.0.1 -logFile Logs/regress_<scenario>_central<n>.log` (n = 1, 2, ... per lifetime); ready when UDP 7500 is bound
      (`Get-NetUDPEndpoint`, 60 s: the login, central and cluster start in one frame and the coordinator with them, so a
      bound login port means the coordinator exists);
    - then the **spawner process**: `-batchmode -nographics -startMapSpawnServer -centralAddress 127.0.0.1 -clusterPort
      6010 -mapSpawnPort 6001 -spawnExePath <EXE> -spawnStartPort 8000 -publicAddress 127.0.0.1 -spawnMaps
      "ServerMeshTestMap|ServerMeshTestMap2" -logFile Logs/regress_<scenario>_spawner.log`; it inherits the environment
      (`MESH_CELL_MODE`, `MESH_MONSTER_AREAS`, the faults, ...) and the cells inherit it from the spawner, as the editor's
      cells do today (`MESH_RIG_COMMAND_DIR` reaches them too, harmlessly); the cells are up when every cell expected at boot
      (all ten, or none under `MESH_SLEEP`) logged `Own cell from channel id` (120 s);
    - then the editor's Play as a **client only** (decision 14), the tester as the scenario says.
    `kill_central()` stops the build process whose command line holds `-startCentralServer` (exactly one, by exe path);
    `start_central(n)` starts it again (the store `Temp/ServerMesh.Coordinator.json` is untouched between the two: it is
    deleted at the scenario's start only, as today; adoption is expected 25-40 s after the kill: the 8 s down, the start,
    the cells' 5 s reconnect cadence). **Teardown** (`teardown()`, in place of `stop_play`'s count): stop the editor's Play,
    kill the bots, **the spawner first** (so nothing re-queues a cell), then the central, then every rig process, then count;
    the scenario's "outlived Play" check becomes "survived the teardown". `db_reset`'s and `reset_bot_positions`' guard
    ("the editor is in Play") becomes, in the split topology, "no build process with `-startDatabaseServer` is running".
14. **The editor as a client only.** `request_play` derives it from the topology (no new parameter: the FakeRigs keep their
    shapes): the Play request gains `servers=0`, which the runner turns into the environment variable
    `MESH_EDITOR_SERVERS=0` (an `ENV_KEYS` entry, cleared like the others) and **skips its layout-command leg** in that Play
    (no central here; the mirroring under `auto` stays harmless). A new TestMap runtime component `ServerMeshEditorRole` on
    BaseInit-CE's `MMOServerInstance-CE` object (`[DefaultExecutionOrder(-1000)]`, added through the editor tools) clears
    the kit's four `start*OnAwake` flags in its `Awake` when the editor runs with that variable at `0`, logging `Editor role:
    client only (MESH_EDITOR_SERVERS=0)`; in a build and without the variable it does nothing. The editor's client logs in at
    the prefab's localhost:7500 as today; the auto client waits only for `MMOClientInstance.Singleton`.
15. **Commands and status in a build.** `ServerMeshLayoutCommandFile` moves from `TestMap/Editor` to `TestMap` (runtime;
    namespace `MultiplayerARPG.MMO.Mesh`, guid kept, the runner and its tests updated). A new TestMap runtime
    `ServerMeshCentralCommandWatcher` (a `[RuntimeInitializeOnLoadMethod]` creates it when `MESH_RIG_COMMAND_DIR` is set; it
    **polls** until a `MeshCentralNetworkManager` has `IsServer` and a `Coordinator`, since nothing has started at
    `AfterSceneLoad`) polls `<dir>/ServerMesh.LayoutCommand.txt` once a second, hands the line to `SubmitLayoutCommand`,
    deletes the file, and mirrors `MeshCentralNetworkManager.LayoutOpLine` into `<dir>/ServerMesh.LayoutCommandStatus.txt`
    under the latest seq or `auto` (the runner's format, so `rigsplit`, `rigmerge` and `rigsplitcrash` read it unchanged);
    an `IOException` on the append (the editor appending at the same moment) is retried once next tick, never logged. A
    restarted central's watcher knows no seq: the lines of a resumed op land under `auto`, and the graders of the new
    scenarios read them from the central's kit log (`rigcentral`), not the status file. Line `Rig command watcher on <dir>`
    once.
16. **Scenarios** (`regress_central.py`; graders in `rigcentral.py`; split topology; `split`'s stand plan: meshbot2..5
    in R, meshbot6..9 in the kept half, no harvest bot, no synthetics, `MESH_MONSTER_AREAS=only:Spawn_Interior_C`, the
    tester `stand` at (60, 70) in R; `MESH_CELL_MODE=ServerMeshTestMap:Dynamic`; a 600 s stand window; `start_split` with
    `grade_status=None` and the status graded by the flow; the central's lines through `central_lines` split into
    lifetimes by `rigcentral.lifetimes`):
    - **`splitcentral`**: split `C x 0 + C.1`, `done` and C's e3 apply (as `split`); 10 s later **kill the central**; wait
      `CENTRAL_DOWN_S` (8 s: past LiteNetLib's 5 s disconnect timeout, so every cell sees the drop; D), **start it again**;
      wait for `Coordinator recovery: map ServerMeshTestMap adopted layout <pair> from the store with 2 entries (C, C.1)` in
      the central's second lifetime (90 s), where `<pair>` is the split's e3 (or settle) pair; then launch the fixture bot
      `meshbot` (`stand` at (60, −60); its row sits at the tester's point (60, 70), in R, since `start_split` places it
      there without a harvest bot; 120 s window): its login routes to the child. Grades: the adoption line as above, the
      summary line `Dynamic maps ServerMeshTestMap Waiting` (or `AdoptStored`) in the second lifetime, no `recovery-frozen`, `frozen` or
      `re-opens recovery` line, no `Split`/`Merge`/`Layout op refused` line in the second lifetime; `Login routing:
      meshbotChar ... -> channel C.1` in the second lifetime and `player registered meshbotChar` on C.1 (never on C); every
      cell of the test map and C2 logs `LayoutHello sent` twice (one per central incarnation) and `Layout answer <pair> on
      <cell>: already applied` once for the adopted pair; every cell logs `Database connection lost; reconnecting` and
      `Database connection to 127.0.0.1:6100 is open` after the kill (within 60 s of the restart; the add-on's 35 + 5 s wait
      lands at ~37 s); exactly one process lifetime per cell (`rigsplit.lifetimes`); R-bots and the tester `migrations=1
      relogins=0`, P-bots `0/0`, the late bot `0/0`, no `Session lost` in any bot log; the status file: the split as `split`
      grades it (accepted, done, drain gate open, no stragglers, no supervision); the store after the run holds the adopted
      pair with C and C.1 and no operation; the layout grade: `split`'s applies per cell, one lifetime, through
      `rigcentral.lifetime_apply_violations` (incarnations in order, versions rising across the lifetime; here the second
      adds no apply); 0 exceptions, 0 teleports, no `Layout gate` warning, no `Fence:`.
    - **`splitcentral_merge`**: the same split, the cooldown wait and the merge sent through a new
      `regress_merge.guard_merge` (the window guard, the positive control and the cooldown retry factored out of
      `send_merge`) and `write_command`; `rigmerge.parse_merge_status` polled every 0.5 s until `pairs["e2"]` appears, then
      kill the central, wait `CENTRAL_DOWN_S`, start it again; wait for the adoption line (`... with 2 entries (C, C.1)`:
      e2's entries, C Active owning the union and C.1 Draining, from the store or a cell) and `Merge merge-1: resumed at
      <DrainGate|Retiring> after a central restart` (the kill lands 0.5-1.5 s after e2: either step), then the merge's end in
      the second lifetime: `drain gate open` (when resumed at DrainGate), `retire sent`, `C.1 exited`, `channel C.1 removed`,
      `e3 broadcast`, `tidy broadcast`, `done` (300 s: the database outage stalls the drain's handoffs for up to 40 s);
      straggling lines are notes. Grades: the lines above in order; R-bots `migrations=2 relogins=0`, P-bots `0/0`, the
      tester `2/0`; a `fellBack=True` hop (a handoff rolled back during the database outage) is a **note** on this variant
      (flake policy stated: the drain under a 40 s database outage); C.1's process gone and staying gone
      (`regress_merge.grade_exit`); the prune line on every cell (`grade_prunes`); the store holds no entry for C's family
      after `done` (`grade_store`); applies: C's one lifetime holds the split's four, the merge's e1 and e2 from the first
      incarnation, then e3 and tidy from the second (versions rising: `NextPair` continues from the adopted version); 0
      exceptions, 0 teleports.
    - **`splitcentral_base`**: the split as `splitcentral`, no tester (`enabled=False`), `MESH_FAULT_DIE_ON_HELLO=C:2`
      (critique: a C killed while the central is down is restarted by the kit only after the spawner re-registers, which is
      long after the other cells said hello, so the crash would never land in the Recovering window); 10 s after `done`
      kill the central, wait `CENTRAL_DOWN_S`, start it again: C registers with the new central, sends its second hello
      (pending while the map waits) and dies on it; the spawner, registered by then, restarts C at once (a new port); its
      new incarnation's hello holds no layout. Wait for the adoption line (`... (C, C.1)`, from the store or C.1) and C's
      second lifetime's apply `Layout <pair> applied: C owns <kept> interest <kept>` (120 s). Grades: one `FAULT: dying on
      layout hello 2 (MESH_FAULT_DIE_ON_HELLO)` on C; C two lifetimes, its second's only apply (kept, kept) at the adopted
      pair, **no apply of its second lifetime owns P** and no `Handoff START <R-bot> ... C.1->C` in C.1's log after the
      kill (the base never re-owned R); `Spawn areas of C: 5 monster(s) planned in the base rectangle` after the kill; C.1
      released C's old incarnation's monsters within 30 s of the kill (a note when it held none); the monster sum C + C.1
      returns to 5 within 90 s of the answer (`rigsplitcrash.monster_pairs`); R-bots `migrations=1 relogins=0`, P-bots
      `relogins=1`, `0 or 1` migration (2d's allowance: the re-login may fall back to the nearest live cell while C is
      down, `Login routing: no server for cell C` is allowed in the second lifetime) and ending on C's second lifetime
      (`Re-login routed to`, `player registered` there); the second lifetime holds the adoption line and no `frozen`; 0
      exceptions, 0 teleports.
    All three join `ALL`, `TWO_HOST_SKIP` (the spawner is the build's, on this host) and `PERSIST_SKIP`; `test_registration`
    lists them. Every other scenario keeps the editor topology and its graders unchanged (`regress.grade_layout` still fails
    on any op or gate line).

### Acceptance
17. EditMode tests: the resolver's choice (newest pair over store and hellos; the staged snapshot counted only when held;
    inconsistent entries at one pair → Frozen; untrusted store with held layouts adopts them; nothing anywhere →
    AdoptStatic; a named id missing → Waiting, then at the ceiling the family frozen and the map adopted; the exclusions of
    decision 1; a Static neighbour's held snapshot as a candidate) and the op table (every row, held and not, chosen e2
    against e3, Frozen, stale); the coordinator: a store with a child adopts it (the test that froze flips), applies the old
    pair to its registry, re-adds the channel before answering, answers the pending hellos with it and logs the adoption; a
    Static neighbour's hello pends until Running; the named id's hello awaited; the frozen family's refusal, its answered
    cells and the unfreeze; **no store, AdoptStatic at Start, a late hello holding (A, 7) → AdoptStored (A, 7)**; re-open
    before and not after an own broadcast (the flag, not the pair); a resumed split at the drain gate reaching done on fresh
    evidence; a resumed split at E3Broadcast reaching done from the hello answers; a resumed merge at Retiring with the
    retiring cell gone reaching done; the retiring cell's absence excluded from the wait; E2Staged held → e2 broadcast, not
    held → abort with the fenced Retire and the Retired broadcast; E1Gate → the Retired broadcast (`e1Broadcast` derived);
    merge E1Gate → Reverted; a Frozen op's staged step held → resumed; the settle resume; the op counter continuing; the
    fenced reason for an owning cell's Retire; the policy's fresh-cooldown line and the family's cooldown; `SaveStore`'s step
    (the file names the step entered) and the operation round trip, a file without it; the login wait predicate (pure); the
    map-side answer line and the hello fault's predicate; `ServerMeshLayoutCommandFile` tests unchanged after the move;
    `ServerMeshEditorRole`'s predicate; all 2a-4 tests unchanged but for the texts named here. Python tests: `rigprocs`
    (arguments, snippets, log path, lifetimes), `rigcentral` parsers (the adoption line, the summary line, `Login routing`,
    the resumed-op lines, `lifetime_apply_violations`), `regress_central` flows with a FakeRig (process starts, kills and
    restarts recorded; central log lines; the status file; the bots), `regress_merge.guard_merge`, `test_registration`,
    `test_imports`; ruff.
18. Rig: zero new compiler warnings; the stray check; `splitcentral` 3/3 (flake policy: one rerun, recorded);
    `splitcentral_merge` and `splitcentral_base` 1/1 each (3/3 on a failure); `split`, `merge`, `splitc2`, `mergec2`,
    `splitcrash_prewarm`, `splitcrash_drain`, `splitcrash_base`, `splitbuild` 1/1 each (unchanged code paths but the
    recovery and the store; 3/3 on any that fails once); the eleven base scenarios Static 11/11 and `all:Dynamic` 11/11;
    DESIGN 0.82 (the row, §17 unchanged, the known limits: a frozen family needs the operator path, the store lost together
    with a partitioned cell, the database outage during a restart, a resumed op is not measured, the staged-commit window
    lasting the outage, a frozen op dropped by a restart), SETUP (recovery, the frozen family, the login wait, the
    topology, `servers=0`, the hello fault, the scenarios), MULTIHOST (a paragraph: the split-process topology is the
    production shape; the central process can be restarted alone; the cells' config dependency), `ServerMeshPackageBuilder`
    0.82.0 and `UpdateDate`, `regress.py --package`.

## Interfaces and log lines (exact)
- **Central:** `Coordinator recovery: map <m> adopted layout <pair> from <the store|cell <c>> with <n> entries (<ids>)`;
  `Coordinator recovery: family <base> of map <m> frozen: no hello from <ids> after <s> s` (Error); `Coordinator recovery:
  family <base> of map <m> unfrozen: <id> said hello`; `Coordinator recovery: map <m> frozen: cells <a> and <b> hold
  different layouts at <pair>` / `map <m> frozen: the layout <pair> does not fit the asset: <reason>` (Error); `Cell <c>
  holds layout <p> of map <m> newer than the adopted <q>: map <m> re-opens recovery` (Warning; the 2a text `from an
  earlier coordinator: map <m> re-opens recovery` stays for the no-runtime-layout case); `Cell <c> holds layout <p> newer
  than the adopted <q> after the central moved on; answered with the current layout` (Error); `<Kind> <op>: resumed at
  <step> after a central restart`; `<Kind> <op>: aborted the central restarted before <step>`; `Split <op>: settle resumed
  after a central restart`; `<Kind> <op>: frozen before the restart, dropped: <reason>` (Warning); `Coordinator policy:
  cooldowns start fresh for <bases> after the restart; flap multipliers, measurements and restart counts forgotten`; the
  summary line's outcome `AdoptStored`; `Layout op refused: family <base> is frozen: <reason>`; `Layout hello from
  dynamic cell <id> (fenced: not in the layout) answered with Retire`.
- **Login:** `Login routing: waited <s> s for the layout recovery`; `Login routing: the layout recovery is not done after
  <s> s; routing by the layout as it stands` (Warning).
- **Map side:** `Layout answer <pair> on <cell>: already applied`; `FAULT: dying on layout hello <n> (MESH_FAULT_DIE_ON_HELLO)`.
- **Rig:** `Editor role: client only (MESH_EDITOR_SERVERS=0)`; `Rig command watcher on <dir>`; env `MESH_EDITOR_SERVERS`,
  `MESH_FAULT_DIE_ON_HELLO` (both in `ENV_KEYS`), `MESH_RIG_COMMAND_DIR` (the build processes only); Play request key
  `servers=0`.
- **Store:** `MeshCoordinatorStoreData.hasOperation`, `operation` (`MeshStoredOperation`); format 1; the step stored is
  the step entered.
- **Resolver:** `MeshRecoveryOutcome.AdoptStored`; `MeshRecoveryOutcomes.Answers(outcome)`; `MeshRecoveryInput.namedIds`,
  `.excludedIds`, `.candidates`; `MeshRecoveryDecision.chosen`, `.chosenSource`, `.frozenFamilies`;
  `MeshRecoveryResolver.HeldReopensRecovery(int, MeshLayoutPair, long, MeshLayoutPair adoptedRuntimePair, bool
  ownBroadcastSent)`; `MeshRecoveryResolver.ResolveOperation(MeshStoredOperation, MeshLayoutPair chosen,
  IReadOnlyDictionary<string, MeshLayoutPair> heldByCell)` → `MeshOperationVerdict`.
- **Harness:** `regress.py` `--topology editor|split`, `SPLIT_TOPOLOGY`, `topology_of(name)`, `central_lines(start, day,
  suffixes)`; `regress_procs.start_central(n)`, `kill_central()`, `start_spawner()`, `teardown()`; `rigprocs.central_args`,
  `spawner_args`, `kill_by_argument_script`, `central_log_path`; `regress_merge.guard_merge(rep, rig,
  r, m)`; `rigcentral.lifetimes`, `adoption`, `lifetime_apply_violations`.

## Files
Mesh/: new `MeshLayoutCoordinator_Recovery.cs` (Start's recovery, the wait set, the choice, adopt, freeze and the frozen
families, re-open, the Static list; moved out of `MeshLayoutCoordinator.cs`), `MeshLayoutCoordinator_Resume.cs` (the
stored op's rebuild and the verdicts), `MeshStoredOperation` in `MeshCoordinatorStore.cs` (+ the two fields);
`MeshRecoveryResolver.cs` (the choice, the op table, `AdoptStored`, `Answers`); edits: `MeshLayoutCoordinator.cs` (hooks,
`_ownBroadcastSent`), `_SplitSteps.cs` (`SaveStore` writes the operation; the save after the step is entered; `Advance`
gated on Running), `_Split.cs` (the frozen-family line, the fenced reason, `Abort`'s Retire to the current binding),
`_Merge.cs`/`_MergeSteps.cs` (the save order), `_Collapse.cs`/`_Supervision.cs`/`_Policy.cs` (`Answers(outcome)`, the
frozen-family skip, the fresh-cooldown line), `MeshLoginNetworkManager.cs` (the wait), `_Layout.cs` (two lines),
`_Fence.cs` (the hello fault). TestMap: `ServerMeshLayoutCommandFile.cs` (moved to runtime), new `ServerMeshEditorRole.cs`,
`ServerMeshCentralCommandWatcher.cs`; `Editor/ServerMeshPlayRunner.cs` (`servers`, the skipped leg); the scene
`BaseDemo/Scenes/BaseInit-CE.unity` (the role component). Tools: new `regress_central.py`, `rigcentral.py`, `rigprocs.py`,
`regress_procs.py`, their tests; `regress.py` (topology, `ENV_KEYS`, registration, the teardown call), `regress_merge.py`
(`guard_merge`), `test_regress.py`. Docs: DESIGN, SETUP, MULTIHOST, this contract.

## Implementation deviations (2026-10-19), kept
- **Candidates and the adopted pair (decisions 2, 4).** Every layout a cell holds is a candidate, an empty one too: a
  cell's empty layout at a newer pair (a tidy) says there was no runtime layout at that pair, and it must outrank an
  older cut a zombie still holds (the first test of the re-open rule adopted a retired child back). When the newest
  candidate is empty the map adopts the asset cut, but its pair is remembered as the adopted pair (`AdoptedPair`;
  `AdoptedRuntimeLayout` says whether entries or a stored operation came with it), so a held older cut re-opens nothing
  and gets its (fenced) Retire. **The asset-cut adoption applies nothing to the central's registry any more** (2a applied
  the empty `(Incarnation, 1)`; it would outrank the cut a cell still has to tell the central about, and the first
  lost-store test froze on it); the registry takes the first layout this central or recovery actually applies.
- **The wait set and the op's child (decisions 1, 3).** A staged step's target is waited for after all: its hello decides
  whether the step is re-sent or dropped, and the first implementation (child excluded at E1Staged/E2Staged) aborted a
  split whose child was a second behind its parent in reconnecting. Excluded are a split's child before anything was
  staged to it or once e1 went out to everyone (ChannelAdded, Spawning, E1Gate: its hello changes nothing) and a merge's
  retiring cell from the commit broadcast on. A missing id the verdict retires or reverts anyway (`AbortableId`: the op's
  dynamic cell before the commit broadcast, or frozen) freezes no family at the ceiling.
- **A resumed step's participants** are every bound cell of a Dynamic map (not the Static neighbours: their acks come
  from the hello answer later, and a resumed merge's e3 would wait on them for nothing).
- **The staged e2 held by its target is the layout adopted** (the newest consistent candidate), not the broadcast before
  it; the re-sent step then applies "already applied" on the central and goes out to everyone.
- **The editor as a client only** needs no Play request key: `regress.py` sets `MESH_EDITOR_SERVERS=0` through the
  existing `env.*` lines (the key is in `ENV_KEYS`), and the runner skips its layout-command leg while that variable is
  `0`.
- **The second lifetime's summary line says `Waiting`, not `AdoptStored`.** It is written at Start, before any hello
  has arrived, so a map with a stored cut is always still waiting there; the adoption line is what proves the adoption
  (the first passing live run failed only on this check and the next one). The late bot's stand wait is 120 s: on the
  split topology about half the bots' first user login goes unanswered for one kit request timeout (30 s), in the first
  lifetime as in the second (the editor topology shows none), and the auto client's retry gets in; the check reports
  the retries. It is the build-hosted database manager's first answers, not the recovery (the login's recovery wait was
  never entered, the cells' database links re-opened at the adoption).
- **Lifetimes open at their start-up, not at the summary line.** `rigcentral.lifetimes` first split the central's log at
  each summary stamp, and lines before the first summary formed a lifetime of their own. A process logs its start-up a
  moment before that line; when it falls a second earlier (run 5, 17:36:04 against 17:36:05) those lines became a phantom
  first lifetime and "the second lifetime" was the first, which holds no adoption: the check failed although the central
  had adopted the store's layout a second after its start. A lifetime now opens at the first line of the unbroken run
  (no gap of 3 s; the central is down 8 s) ending at its summary line. The split could only fail a run, never pass one.
- **The tester stands as long as the bots** (`OBSERVE_S`, 600 s, passed as the Play request's `observeSeconds`). The
  runner's default is 100 s; in the merge variant it ended before the resumed merge's drain handed the tester back, so
  its RESULT counted one migration though C and C.1 logged both handoffs (the first merge run, 126/127).
- **`splitcrash_base`'s release check forgives a hand-back after the kill** (Slice 2d's grader, `rigsplitcrash.unreleased`):
  C.1 handed one of C's monsters back to C a second after C was killed (its link had not timed out yet), so the monster
  left C.1 before any release and C's restart spawned it again; the grader forgave a hand-back stamped in the kill second
  only and failed the family run once. A hand-back or death up to the link's 5 s disconnect timeout now counts as gone;
  the pair-sum check still counts the monsters. Slice 5 does not touch that path.
- **`MeshRecoveryInput`** carries `storedBroadcast`, `storedStaged`, `storedOperation`, `held`, `dynamicMaps`,
  `excludedIds`, `abortableIds` instead of 2a's two counts; `MeshRecoveryDecision.chosen` is set for AdoptStatic too (the
  empty newest candidate). The stored operation's conversions live in `Mesh/MeshStoredOperation.cs` (pure); the hello
  fault's rule is `MeshFault.HelloFires`.

## Critique log
- **Rev 0 → 1 (2026-10-19), protocol and Mesh/ review:** the re-open predicate read "own broadcast sent" off
  `_broadcast.pair`'s incarnation, so a lost store adopted the asset cut at Start and a later hello holding the old layout
  was simply answered with an empty newer layout: C re-owned P, C.1 refused it and kept R → an explicit `_ownBroadcastSent`
  flag and a re-open whenever no runtime layout was adopted or the held pair is newer; the wait set named a merge's
  Draining cell and a split's Pending child whose absence is the step's expected outcome, so the common case (the retiring
  cell quit during the outage) stalled 60 s and froze the family → the exclusions of decision 1, and an Abort or exit
  verdict runs on a frozen family while a Resume waits; the store's `opStep` is one step behind the snapshot it carries and
  `e1Broadcast` was saved false after the broadcast → the save after the step is entered, `e1Broadcast` derived, Frozen
  ops stored, the settle saved after it is marked; a split resumed at the drain gate after e3 could never open it (the
  parent's shrunk state is gone) → the split rows keyed on the chosen pair, E3Broadcast resumed as such; a late or losing
  cell that owns something got a plain Retire and stayed up → the `fenced:` reason for a hello whose own entry owns
  something; the Static-neighbour rule had no mechanism and its held snapshot no role → the `_pendingStatic` list and
  candidates from any Dynamic map's entries; `Advance` ran on acks while another map waited → the op resolved and
  `Advance` gated on Running; frozen families withheld every answer → their live cells are answered (frozen = no ops,
  supervision or policy); the login budget (15 s), the tie-break (the store), the forgotten supervisor counts, the
  staged-commit window as a known limit, file sizes.
- **Rev 0 → 1, harness review:** `-spawnMaps` with two tokens starts the test map only (`ReadArgs` splits one token on
  `|`) → the pipe form; the kit's spawner restarts a dead cell only while registered, so a C killed with the central came
  back long after recovery ended and the base case never ran in the Recovering window → the `MESH_FAULT_DIE_ON_HELLO=C:2`
  fault (C dies on its second hello, while the new central waits); `stop_play`'s count failed every split scenario and a
  cell killed before its spawner could be respawned → a topology-aware `teardown` (spawner first) and "survived the
  teardown"; `send_merge` blocks until the merge ends → `guard_merge` and a 0.5 s poll for e2, the resumed step read from
  the log (DrainGate or Retiring); the apply graders fail on two incarnations → `rigcentral.lifetime_apply_violations`;
  the central's kit log is one file per day for every lifetime → `central_lifetimes` on the summary line, all severities;
  the database guard "editor in Play" is meaningless with the database manager in a build → "no `-startDatabaseServer`
  process", the processes started inside `request_play` after the rows; the late bot's row is at the tester's point;
  readiness 60 s; the watcher cannot find a started central at `AfterSceneLoad` → it polls, and swallows the shared file's
  `IOException`; `request_play(servers=)` would break the FakeRigs → derived from the topology; `--topology split` refused
  with `--remote-cells`, the readiness set under `MESH_SLEEP`; the plumbing in `rigprocs`/`regress_procs`; the cells'
  cluster port and database address come from the project's `Config/serverConfig.json`; the merge variant's `fellBack` a
  note under the database outage.
- **Live runs and acceptance (2026-10-19, BRENDOAIBOX).** `splitcentral`: run 1 failed (96/97: the restarted central was
  started bare, without `MESH_CELL_MODE`, so it had no coordinator → `regress_procs` keeps the cluster's environment for
  the restart); run 2 failed two grades (160/162: the summary line is written at Start, so a cut map says `Waiting` there;
  the late bot's first login waited out one 30 s request timeout and stood 64 s after launch); runs 3 and 4 passed
  (162/162); run 5 failed the adoption grade (96/97) although the central adopted the store's e3 a second after its start:
  its start-up lines fell a second before the summary and opened a phantom lifetime → lifetimes open at their start-up
  run (replayed on run 5's log: the adoption is found); then 3/3 on the final harness (162/162 each, the adoption 12 s
  after the kill, 0-1 login retries for the late bot). `splitcentral_merge`: 126/127 (the tester's 100 s window ended
  before the drain handed it back; C and C.1 logged both handoffs) → the tester stands `OBSERVE_S`; then 127/127.
  `splitcentral_base` 146/146 twice. Families (editor topology): `split` 120/120, `merge` 145/145, `splitc2` 112/112,
  `mergec2` 139/139, `splitcrash_prewarm` 115/115, `splitcrash_drain` 105/105, `splitbuild` 143/143; `splitcrash_base`
  106/107 once (a monster handed to the dying base a second after the kill counted as unreleased) → the grader's
  tolerance is the link timeout; then 3/3 (107/107). The eleven base scenarios Static 11/11 and `all:Dynamic` 11/11.
  EditMode 3001 (3000 passed, 1 skipped; 2960 in the Mesh group), harness 827 tests, ruff clean, no new compiler
  warning (the one ServerMeshing warning, CS0414 in `ServerMeshAutoClient_Buildings.cs`, is Slice 4's). No kit edit.
  Package 0.82.0. **Accepted.**
