# Dynamic cells, Slice 4: buildings across a cut (implementation contract)

Revision 1, 2026-10-18 on machine A, after two independent critiques (protocol and Mesh/; harness and scenarios; log
at the end) of revision 0, itself from three surveys (the kit's building persistence, the add-on's building and
static-handoff code, the harness) of HEAD 7678e93 (Slice 3, DESIGN 0.80). 2b-3 move players, monsters, statics and
drops across a cut and leave buildings where they were placed: a building in a given-away rectangle never drains
(2b), a child's buildings are saved under the child's channel and a merge leaves them there (2c), a reused child id
loads them back (2c decision 7). 4 makes buildings follow the cut: **every cell of a split family persists its
buildings under the base cell's channel (one core edit, CE-10); a cell loads buildings only once the central has
answered its layout, and only the trees whose root stands in its owned rectangle; at a split's commit and a merge's
drain the holder releases each tree in order (handlers dropped, storage closed, saved, demoted in place) and hands it
to the owner of its root's position, which promotes its puppet or creates the building without a database write; a
placement lands on the cell that owns its position; the drain gates count buildings.** Central-restart recovery (5)
and the operator path (6) stay later slices.

Read first: this contract; `slices/slice2b_design.md`, `slice2c_design.md`, `slice2d_design.md`, `slice3_design.md`
(their decisions hold unless changed here); the plan `DYNAMIC_MESHING_PLAN.md` (the user's copy) D6 Buildings
(:291-304), D8 (:317-326), Slice 4 (:437-445); DESIGN §13 (persistence), §17 (the core-edit registry). Where they
disagree: THIS CONTRACT, then 3's, 2d's, 2c's, 2b's contracts, then the plan.

User decisions: proceed with Slice 4 and its one core edit CE-10 (2026-10-18, through the session that started this
one). Choices the user has not seen are marked (D).

## Rules
As 2b-3: Mesh/ game-neutral (never TestMap/Tests types, never the word "ship"); **Static maps byte-identical**
(everything below is gated on a cell of a Dynamic map: `IsDynamicMap`; the one virtual CE-10 adds returns the kit's
own `ChannelId` on a Static map); log lines one physical line, invariant, no braces, never "Exception"; files under
~500 lines: `MeshMapNetworkManager.cs` (1,147) and `_Ghosting.cs` (3,719) get one-line hooks only, `_StaticHandoff.cs`
(517), `_Layout.cs` (433), `_Evidence.cs` (270), `_Drain.cs` (339) take hooks and small rule changes only (the building
code is its own partials and pure classes, Files); **kit edits: CE-10 only**, marked `// MESH-EDIT: CE-10`, logged in
DESIGN §17 with upstream patch 0008 (**an implementer that finds a second one necessary stops and reports**); no
agents, edits or compiles while a rig chain measures; commits only when the user asks.

## Settings (new `MeshDynamicSettings` fields)
`buildingTransfersInFlight` 2 (D: how many building trees one cell releases at a time; each release is two to four
database round trips, so the entity pacer's 20/s is the wrong unit), `buildingStorageCloseSeconds` 5 (how long a
holder waits for a storage's users and in-flight item moves before it goes on). The retry back-off is the inspector
field `staticHandoffRetrySeconds` (10); the ack guard is 2b's `handoffAckGuardSeconds`. Validation: both new fields
`> 0`.

## Kit facts the design rests on (2026-10-18 surveys and critiques)
- `ChannelId` is a plain property (`BaseGameNetworkManager.cs:45`); the kit's seven building database sites set
  `ChannelId = ChannelId` (`MapNetworkManager.cs:263` quit save, `:366` load, `:413` scene create;
  `_DatabaseFunction.cs:342` save, `:367` force save, `:445` create, `:470` delete). Storage rows are keyed by
  `(storageType, storageOwnerId)` with **no channel**: a building's storage follows its id. No other kit site uses
  `ChannelId` for building or storage persistence (critique).
- The kit loads buildings in `PreSpawnEntities` (`MapNetworkManager.cs:340-430`), **before** `PostSpawnEntities`
  starts the cluster client (`:434`), and the layout gate opens only after the registration's hello is answered.
  **Players enter from `IsServerReadyToInstantiateObjects()`** (`MapNetworkManager.cs:583`), true shortly after the
  scene load and before any of this; `_isServerReadyToInstantiatePlayers` is set after `PostSpawnEntities` but read by
  nothing on the MMO path (critique: rev 0's decision 5 rested on it). A refused enter-game is **not retried** by the
  kit client (`BaseGameNetworkManager.cs:427-428`: the disconnect dialog and `StopClient`), and the auto client's first
  login does not survive one. `CreateBuildingEntity(data, initialize)`: `initialize == false` is a new placement (a
  `CreateBuilding` row first), `true` a load (no write); the MMO override adds a `BuildingDataUpdater`.
- **Every `NetworkDestroy()` of a building** (reason `RequestedToDestroy`, the default) runs
  `DestroyBuildingEntity(Id, IsSceneObject)` → `DeleteBuilding(ChannelId, map, id)` (`BuildingEntity.cs:680-695`,
  `_DatabaseFunction.cs:459-474`), **after** chain-destroying its `destroyWhenParentDestroyed` children (`Destroy()`:
  hp 0, the building's drop items, another `NetworkDestroy`). Today a puppet's despawn deletes nothing because the
  channels differ; **with a shared channel it would delete the owner's row** (decision 7).
- The database manager caches building rows per `{channel}_{map}` key, **cache-first** on `GetBuildings`, and
  `UpdateBuilding`/`CreateBuilding` **create the key** when it is missing (`DatabaseNetworkManager_RequestHandlers.cs:306-335,
  1297-1310`; `DisabledDatabaseCache.cs:262-281` is the live cache). **A building write under a channel before that
  key's first `GetBuildings` makes every later load of it return the written rows only** (critique: the invariant in
  decision 3). The saver writes every frame it may (`MapNetworkManager.cs:150-167`), and a new `BuildingDataUpdater`
  is dirty from its first `ProceedSaving` (`BuildingDataUpdater.cs:33-38`, `MapNetworkManagerDataUpdater.cs:92-116`).
- The periodic saver: `BuildingDataUpdater` sits in the static set `MapNetworkManagerDataUpdater.BuildingDataUpdaters`
  until its `OnDestroy` (end of frame); the queue holds a clone taken at enqueue; `DataUpdater.BuildingDataSaved(id)`
  drops a queued entry; `savingBuildings` holds an id while its write is in flight; `WaitAndForceSaveBuilding` waits
  that out. All `internal`, reachable (one assembly).
- Storage access: an open or an item move needs the building **in `ServerBuildingHandlers`** and the player to be
  its creator (or `CanUseByEveryone`) (`ServerStorageHandlersExtensions.cs:55-77`); an open is distance-checked
  **strictly** against `GetActivatableDistance()` (`conversationDistance` 3 m on this project when the prefab's
  `activatableDistance` is 0) and the kit's sweep closes a storage past it every second (`MMOServerStorageHandlers.cs:100-131,
  262`); an item move needs no open storage and no distance, holds `MapNetworkManager.storageUsers` for its database
  round trip, mutates the cached list in place and writes storage and character in one request
  (`MMOServerStorageMessageHandlers.cs:205-323`); `UnregisterPlayerCharacter` drops a departing player's
  `storageUsers` entry (`MapNetworkManager.cs:316`). `CloseStorage(connectionId, storageId)` notifies the connection
  whether or not it had it open; `GetAllStorageItems()` is the live cache (`Remove` evicts); `LoadStorageRoutine` is
  cache-first on the database manager too.
- Children: the only persistent link is `ParentId`; positions are world; a child finds its parent lazily through the
  handlers and the parent's private `_children` set is cleared only by its own destroy (`BuildingEntity.cs:352-361,
  680-695`); `CmdConstructBuildingBody` sets `ParentId` from `parentObjectId` without any area check
  (`PlayerCharacterBuildingComponent.cs:119-120`). An in-scene building's `Id` embeds `ChannelId` (`BuildingEntity.cs:153`).
- Add-on today: placements route by `world.OwnerOf(OwnCell, position)` (the **live** layout, `MeshWorldRegistry.cs:333-347`,
  which falls back to any map's or the nearest cell, as `ConsiderStatic` guards), held while gated (`_Layout.cs:136-173`,
  flushed on the first open tick, created here after 30 s); building puppets (`SpawnBuildingPuppet`, `_Ghosting.cs:2283`)
  carry no handler entry, no updater, `CreatorId` empty and `RemainsLifeTime` MaxValue; `ExpireGhostStatics` destroys a
  static puppet 4 s after its last snapshot (`_Ghosting.cs:2336-2350`) and the static tick refreshes loans in flight
  (`_StaticHandoff.cs:74-78`); `_ownedStatics` holds buildings but `StaticHandoffTick` scans `is HarvestableEntity`
  only (`_StaticHandoff.cs:90`), as the split's `CountDrain` does (`_Evidence.cs:210`); `MeshDrainState.buildings` is
  counted (scene buildings included) and excluded from `IsDrained`.
- The project has **no storage or door prefab**; the test building is a wall with `activatableDistance` 0; only the
  tester carries building items; the editor tester's `[MeshAuto]` lines go to `Debug.Log` only (the harness reads
  the status file's `phase=`, `finished=`, `result=` and the cell logs); the client has no cell id (only its map
  address and port); `BuildingEntity.id` is a synced field.

## Decisions

### Persistence channel (CE-10 and its override)
1. **CE-10.** `MapNetworkManager.cs` gains `protected virtual string BuildingPersistenceChannelId => ChannelId;
   // MESH-EDIT: CE-10 — ... See Docs/ServerMeshing/DESIGN.md §17`, and the seven sites above read it instead of
   `ChannelId` (each marked `// MESH-EDIT: CE-10`). Nothing else in the kit changes. Upstream patch
   `Docs/ServerMeshing/upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch`
   (branch `mesh-core-fixes`), `PR.md` and `README.md` updated. An EditMode test (`MeshKitEditTests`) asserts by
   reflection that `MapNetworkManager` declares a `protected virtual string BuildingPersistenceChannelId` and that
   `MeshMapNetworkManager` overrides it, so a kit update that drops the edit fails the suite.
2. **The override** (new partial `MeshMapNetworkManager_Buildings.cs`): on a Dynamic map the **base cell's id**;
   else `ChannelId`. The base id is `OwnCell.baseId` once the cell is resolved by a layout (`ApplyEntries` sets it,
   `MeshWorldRegistry.cs:628`), else pure `MeshBuildingChannel.BaseOf(LayoutCellId)` (strips one `.<digits>` suffix,
   the form `FreeChildId` mints; an id without one is its own base). A base cell's value equals its `ChannelId`, so a
   Dynamic map that never splits writes the rows it writes today. Logged once at the first building database call:
   `Building channel on <cell>: <channel>`. Consequence: **a child's buildings live under the base channel**, a
   merge leaves nothing behind, and 2c decision 7's "a reused id loads the buildings saved under its channel" is
   closed (it loads by rectangle now, decision 4).

### Load (Dynamic maps; new partial `MeshMapNetworkManager_BuildingLoad.cs`, pure `MeshBuildingTrees`)
3. **`PreSpawnEntities`** on a Dynamic map does not call the kit's: with the kit's `IsAllocate` and `IsInstanceMap()`
   branches kept, it only **remembers** the in-scene `BuildingEntity`s by id (`_sceneBuildings`) — it adds **no
   updater** yet (critique: an updater's first dirty save would create the cache key before this cell's load and
   hide every placed row). **Invariant: a Dynamic cell makes no building write before its own `GetBuildings`
   returned.** A Static map calls `base` (byte-identical).
4. **`PostSpawnEntities`** on a Dynamic map is the kit's (`await base`: the cluster connect) and returns at once;
   player readiness is **unchanged** (rev 0's decision 5 withdrawn). **The load starts when the layout gate opens**
   (one hook in `LayoutTick`'s first open tick, before the held placements are released) as an async routine, once per
   process (`_buildingLoad`: NotStarted, Loading, Done; every failure logged): `GetBuildings(BuildingPersistenceChannelId)`
   retried until it succeeds, as the kit does; the rows go through `MeshBuildingTrees.Partition(rows, sceneIds, ownedIds)`:
   - an `IsSceneObject` row whose id matches an in-scene building: the kit's treatment (`CurrentHp <= 0` →
     `NetworkDestroy`, else the hp, `AddBuilding`, its storage) — a Dynamic cell's own copies;
   - an `IsSceneObject` row matching nothing: **skipped** (another cell of the family wrote it: in-scene ids embed the
     writer's `ChannelId`, so scene buildings stay per-channel copies, the plan's known limit; the kit's else-branch
     that would spawn a prefab copy of it never runs);
   - a row whose id is **already in `ServerBuildingHandlers`** (placed here while the load ran): skipped and counted;
   - a placed row: a node of a **tree** (`MeshBuildingTrees.Build`: children by `ParentId`; a node whose parent is not
     in the row set is a root — the kit would spawn it anyway; a cycle is broken at its lowest id); a tree is **ours**
     when its root's position is owned by this cell in the live layout (`world.OwnerOf(OwnCell, root.Position)` is
     `OwnCell`); the others are counted and left to their owners. Ours are created **parent before child**
     (breadth-first from the root), each `StorageEntity` node's storage loaded with `LoadStorageRoutine` **before** its
     entity exists (the kit loads after, which leaves a window in which an open sees an empty storage), then
     `CreateBuildingEntity(row, true)`;
   - the in-scene buildings no row matched: the kit's tail (`InitSceneObject`, `AddBuilding`, `CreateBuilding` under
     the persistence channel, the scene storage written); **then** every in-scene building gets its
     `BuildingDataUpdater` (the kit's first loop, moved after the read).
   One line: `Buildings loaded on <cell>: <n> placed in <t> tree(s) and <s> scene, <o> tree(s) owned elsewhere, <x>
   scene row(s) of other cells skipped, <a> already here (channel <ch>, <r> row(s))`. A Pending child whose first
   layout owns nothing loads no tree and receives its buildings by transfer at e2 (decision 8); a restarted child
   (2d) loads its rectangle's trees; a base restarted with children live loads its kept rectangle only (plan D8).
   **There is no static fallback**: a cell whose gate never opens loads nothing.
5. **While the load runs (D).** Players enter during the gate and the load as today (the window is about a second
   after registration on the rig). Placements are **held** until the load is done: `TryHoldGatedPlacement` holds while
   `IsLayoutGated || !BuildingsLoaded` (the same queue, the same 30 s deadline and lines), so a placement can never
   precede the read that would return its row; transfers toward a loading cell are refused `loading` (decision 10)
   and the tick starts no release before the load is done. No building entity exists here before the load, so no
   storage can be opened or moved on. **Residual (recorded):** a cell that outlives the database manager (machine B's
   cells across a Play restart) meets the kit's own cache-key hazard, as today.
6. **Drain state and scene buildings.** `MeshDrainState.buildings` counts owned, **non-scene** buildings in
   `ServerBuildingHandlers` plus transfers in flight (decision 8); scene buildings are per-channel copies and never
   move. `IsDrained` **includes** `buildings == 0` (2c decision 9's D reversed). The `Merge retire: <cell> leaves <n>
   building(s) saved under its channel` warning goes: a Draining cell that cannot release a tree straggles like any
   other count, through 2c's lines.

### Despawn without delete
7. **`DestroyBuildingEntity(id, isSceneObject)` override** (in `_Buildings.cs`): on a Dynamic map, an id that is
   **not in `ServerBuildingHandlers`** (a puppet, a demoted copy, a building released to another cell) skips the
   delete and the handler removal and returns; an owned building goes to the kit (its row deleted under the
   persistence channel). Static maps: the kit's path (the delete misses, as today). This is what makes a shared
   channel safe against the puppet despawns the add-on already does (`DestroyGhostStatic`, stale expiry, link loss,
   the prune, a neighbour's new incarnation). **Demoted trees despawn leaf first** (critique: a demoted root's
   `OnNetworkDestroy` would `Destroy()` its demoted `destroyWhenParentDestroyed` children — hp 0, their drop items
   spawned by a non-owner, a second destroy): `DestroyGhostStatic` gets one hook, `DeferDemotedRootDestroy(meshId)`,
   which for a root this cell demoted destroys the children's puppets now (deepest first) and the root itself on the
   next frame (Unity's `== null` is true only after the frame's destroys), so the chain finds no child. A demoted copy
   also has its `CreatorId` cleared (as `SpawnBuildingPuppet` leaves it: the kit's creator-only commands — destroy,
   repair, lock, password — are refused on it with `UI_ERROR_NOT_ALLOWED`); the clone restores it on re-promote.

### Transfer (new partial `MeshMapNetworkManager_BuildingTransfer.cs`; link ids 131, 132)
8. **Unit and trigger.** The unit is a **tree**: a root (a non-scene owned building whose `ParentId` is empty or
   names no building in the handlers) and every descendant found through the handlers. `StaticHandoffTick` (once a
   second, Dynamic maps, gate open) gets one hook, `BuildingTransferTick(now)`: nothing before the load is done; every
   root whose position another cell **of the same map** owns in the live layout (`world.OwnerOf` and the owner's
   `ContainsWorld`, as `ConsiderStatic` guards; **no hysteresis** — a building never moves, the half-open rectangles
   give one owner, and a hysteresis would leave a building just past the line undrainable) is a candidate when that
   cell is linked and settled (`IsNeighbourLinked`, `IsNeighbourSettled`: registered with its gate open, 2d), the
   root is not in back-off, and fewer than `buildingTransfersInFlight` releases are running; nearest the cell's edge
   first. The tick also **refreshes `lastSeen`** of every demoted node of every tree in flight (as the static tick does
   for loans), so a copy awaiting its reply never expires. This one rule serves the split's commit (P's roots in R),
   the merge's drain (owned ∅: every root), a base crash's return and a placement that landed on the wrong side
   (decision 11).
9. **The holder's release**, an async routine per tree, every step logged on failure, order fixed (it deviates from
   the plan's D6 order: the handlers go first so no user can start a move after the storage save, the updater is
   removed before the save, and the receiver loads a storage before it creates the entity — critique log):
   1. **Refuse new users:** `ServerBuildingHandlers.RemoveBuilding(id)` for every node (the kit then refuses opens and
      item moves with `UI_ERROR_CANNOT_ACCESS_STORAGE`). For each `StorageEntity` node: `CloseStorage(connectionId,
      storageId)` for every player within `GetActivatableDistance()` + 5 m (the kit itself closes a storage farther
      than that within a second; a close notice to a player who had it shut is a no-op on the client), then **wait**
      until `IsStorageEntityOpen` is false and no `storageUsers` set holds the `StorageId` (an item move in flight
      holds it across its database write; a move already past its access check mutates the cached list in place and
      writes the same content the holder saves next, so nothing duplicates), at most `buildingStorageCloseSeconds`;
      past it `Building transfer <root> zone <from>-><to>: storage <id> still in use after <s> s` (Warning) and the
      release goes on.
   2. **Stop saving, then save:** for every node `MapNetworkManagerDataUpdater.BuildingDataUpdaters.Remove(updater)`
      and `Destroy(updater)` (the static set keeps a destroyed component until the frame's end, and the saver may
      enqueue and write in the same frame), `DataUpdater.BuildingDataSaved(id)`, the **clone** `node.CloneTo(new
      BuildingSaveData())` kept for the message and for a re-promote, `WaitAndForceSaveBuilding(TransactionUpdateBuildingState.Building,
      clone)` (it waits out an in-flight write of the same id itself), then `BuildingDataSaved(id)` **again** (a save
      the saver enqueued between the two); a storage node: `WaitAndSaveStorage(storageId, GetStorageItems(storageId),
      false)`, then `GetAllStorageItems().Remove(storageId)` (evicted; a late `SetStorageItems` from a move that was in
      flight re-adds the same content, harmless). A save that fails (false) abandons the release: step 5's failure
      path, `... : save failed`, back-off.
   3. **Demote in place** every node as `DemoteStaticOnSend` does a harvestable (untracked, `IsGhost`, `OwnerZoneId` =
      the target, ghostified, invincible with the forwarded-hit hook, a `_ghostStatics` record), plus
      `RemainsLifeTime = float.MaxValue` and `CreatorId` cleared (a puppet's shape; the clone holds the real values).
      The tree's node ids are recorded as a demoted tree (decision 7's leaf-first despawn). Observers see no flicker;
      a hit on the copy goes to the receiver; an open or a move on it is refused (step 1).
   4. **Send** `BuildingTransfer` (131) `{rootId, nodes: BuildingSaveData[] parent before child}` (the clones) on the
      link; the tree joins the in-flight set with the ack guard: `Building transfer <root> zone <from>-><to>: sent (<n>
      node(s))`.
   5. **On the reply** (132) ok: settled, `... : done`; the demoted copies are ordinary puppets now (the receiver's
      snapshots keep them, or they expire stale beyond its band — decision 7 keeps their rows). Failed, or no reply
      within `handoffAckGuardSeconds`, or the link lost: **re-promote** every node **from its clone** (unghostified as
      `PromoteStaticPuppet`, every save-data field restored — `RemainsLifeTime` and `CreatorId` included —, `AddBuilding`,
      the updater added back, a storage evicted and reloaded with `LoadStorageRoutine`; a node whose entity is gone
      meanwhile is re-created with `CreateBuildingEntity(clone, true)`), `... : released (<reason>)`, back-off
      `staticHandoffRetrySeconds`. Because the rows are current before the message goes and the receiver only reads,
      a crash of either side mid-release loses nothing: the next load gives the tree to the owner of its root's position.
10. **The receiver** (`OnBuildingTransfer`): positions translated by the link's frame offset; a gated or still-loading
    receiver, or one that is not the owner of the root's position in its own layout, replies failed (`gated` /
    `loading` / `not the owner here`: the sender's tick tries again when the layouts agree); a node whose `EntityId`
    names no registered building prefab fails the **whole tree** before anything is created (`unknown building
    <entityId>`). Otherwise, per node in order: a storage node's storage is **evicted and `LoadStorageRoutine`d
    first** (the sender saved just before sending: the database is the truth, also for a node this cell already holds);
    a node already in the handlers is **owned already**: its save-data fields re-applied, nothing made; else, with a
    puppet of the id in `_ghostStatics`, **promote in place** (the hit hook and the record removed, unghostified,
    `IsGhost` false, `OwnerZoneId`, `LastHandoffTime`, every field from the node, `AddBuilding`, a `BuildingDataUpdater`,
    `TrackOwnedStatic`), else `CreateBuildingEntity(node, true)` (no database write; the kit adds the updater;
    `OnAssetObjectSpawn` tracks it; the add-on's routing is bypassed by `initialize`). Reply ok: `Building transfer
    <root> zone <from>-><to>: promoted <p>, spawned <s>, owned already <o>`. A tree arriving twice (a resend after a
    lost reply) is all "owned already" and acked again.
11. **Placement routing** keeps its rule (the owner of the position in the live layout; a child placed on a puppet
    parent goes to the parent's owner) and, **on a Dynamic map only** (a Static link's first half-second must not
    change), gains the target check the statics have: the target must be linked **and settled** (`IsNeighbourSettled`),
    else `Building <id> placed at <pos> belongs to zone <z>, which is not ready: kept here` (the existing warning's text
    widened) and the building is created here, under the persistence channel, and moved by the tick once the owner is
    ready. **Residual (recorded):** a building kept here while its owner restarts is loaded by that owner's rectangle
    load too; the two owners last until the tick hands it over (decision 10's "owned already" reconciles from the
    database), and an item moved on the restarted owner in that second is overwritten. A Draining cell (owned ∅)
    routes every placement to the survivor. `BuildingPlace`'s receiver (`CreateBuildingHere`) is unchanged: never
    forwarded again.

### Drain gates (central reads unchanged in shape)
12. **Split (P's evidence).** `MeshDrainCounts` gains `buildings` — roots owned here (in the handlers) whose position
    lies in the given-away rectangle (`Contains`, **no hysteresis**, matching decision 8), plus building transfers in
    flight toward the child — and `Total` sums it; `MeshLayoutEvidenceMessage.WireVersion` becomes **3** (the reader
    refuses other versions; map and central are one build); `MeshSplitGates.Drained`'s `why` names `building(s)`. The
    drain timeout's `N_R` counts them at the character rate. **Residual (recorded):** a tree's release costs
    `buildingStorageCloseSeconds` plus three round trips, two at a time, so many trees with open chests can outrun the
    formula and straggle (then 2b's settle shrinks P once they are gone).
13. **Merge (B's drain state).** Decision 6: `IsDrained` holds until B owns no building and has none in flight; with
    owned ∅ every root is a candidate for the survivor (decision 8). 2c's straggling lines and notes cover a tree the
    survivor keeps refusing.

### Rig: content, tester, scenario (`Tools/ServerMeshing/regress_building.py`, pure `rigbuilding.py`)
14. **Test content** (TestMap; made through the Unity editor tools: a copy of `TestBuilding-CE.prefab` with its
    `BuildingEntity` replaced): `Prefabs/TestStorage-CE.prefab` (a `StorageEntity`: a 1 x 1 x 1 chest, `storage`
    weight 0 and 10 slots, `canUseByEveryone` false — the creator uses it, `lockable` false, **`activatableDistance`
    6** (the kit's storage open and its sweep are strict against it; the walls' 0 means 3 m), `maxHp` 40, `lifeTime`
    0, `destroyWhenParentDestroyed` true, `canBuildOnAnySurface`), `Prefabs/TestDoor-CE.prefab` (a `DoorEntity`:
    1 x 2 x 0.2, `lockable` false, otherwise as the chest), each with its own `LiteNetLibIdentity.assetId` (checked
    against the wall's); items `GameData/TestStorageItem-CE.asset` and `TestDoorItem-CE.asset` (`BuildingItem`, `id`
    exactly the file name, `maxStack` 20) listed in `BaseDemo/GameData/GameDatabase-CE.asset` as the wall's item is
    (the items are what fill `GameInstance.BuildingEntities`, so every map server can spawn the rows); `provision_rows.py`
    gives the tester one of each **after** the wall stack (`build` picks the first building item in the bag), with
    `ITEM_STORAGE`, `ITEM_DOOR` and `test_provision`'s hash and bag checks.
15. **Auto client mode `buildtree`** (an AutoClient partial `ServerMeshAutoClient_Buildings.cs`; `request_play` gains
    `observe_seconds=` and `line_x=`, two request lines the runner already forwards). Phases are written to the status
    file (`phase=`; a re-login prefixes `relogin-`), which is how the harness synchronises; everything graded is in the
    RESULT or the cell logs:
    1. walk to (walkX, walkZ); place the **wall** at (walkX + 3, walkZ) as `build` does; wait for its entity (by
       position, 30 s); place the **chest** at the wall + (0, 0, 1.5) and the **door** at the wall − (0, 0, 1.5), both
       with `parentObjectId` = the wall's (a tree of three); wait for both (`BuildingEntity.Id` from the synced field):
       `buildtree: placed wall <id> chest <id> door <id>`, phase `buildtree-placed`.
    2. walk to within 1.5 m of the chest (x = walkX + 1.5, the chest's z), open it (`CallCmdOpenStorage(chest.ObjectId,
       "")`), wait for `ClientStorageActions.onNotifyStorageOpened` then the items (`onNotifyStorageItemsUpdated` /
       `GameInstance.OpenedStorages`, 5 s): `buildtree: storage opened at <address:port> items=<n>`; move **one skill
       scroll** (the bag's `ITEM_SKILL` stack, 20 at provision) into it (`RequestMoveItemToStorage {storageType Building,
       storageOwnerId chest.Id, inventoryType NonEquipItems, inventoryItemIndex = the first slot with that dataId, found
       each time, inventoryItemAmount 1, storageItemIndex -1}`, wait for the response); phase `buildtree-ready`.
    3. **the mover:** from now until 20 s after the first migration (or the window's end), every 2 s one scroll move —
       into the storage on even turns, out on odd (`RequestMoveItemFromStorage {storageItemIndex 0, storageItemAmount 1,
       inventoryItemIndex -1}`; the direction keeps the storage from running dry) — plus a **burst** of one move every
       0.25 s for the 10 s after the first migration completes (the transfer happens around e2, as the migration does);
       **a move only while the chest is open on this client** (the kit's own UI opens before it moves; a move on a server
       where nobody has opened that storage trips the kit's `NotifyStorageItemsUpdated`, live run 1): when the chest is
       closed (the holder closed it for the transfer, or this is a new cell) the mover re-opens it first and counts the
       attempts (`reopens=<ok>/<tried>`; a refused re-open is the transfer in progress), and the client's storage hooks
       are re-attached before every open (the kit's `ClientStorageActions.Clean()` drops them on each new map
       connection, live run 1); each outcome logged `buildtree: move <n> <to|from> -> ok|<UITextKeys>`;
       `UI_ERROR_CANNOT_ACCESS_STORAGE` counts as `movesRefused`, any other refusal as `movesFailed`.
    4. after the first migration, on the new cell: find the chest and the door again by `Id` (new objects; the
       `ObjectId`s re-read), walk within 1.5 m of the chest, open the storage (notify within 5 s) and read its items
       **after the mover ended**: `buildtree: storage opened at <address:port> items=<n>`; walk within 1.5 m of the door,
       open it (`CallCmdOpenDoor`), wait for `IsOpen` (5 s), close it: `buildtree: door opened`; phase `buildtree-checked`.
    5. stand until the window ends (MigrationSuffix as `stand`); after a lost session the re-login path runs and the
       tester logs `buildtree: after re-login buildings near=<n>` (the tree within 10 m: the first poll within 10 s of
       re-entering the map that reaches 3, else the last) and opens the storage once more (`storage opened` as above:
       the rows survived the restart).
    RESULT: `RESULT buildtree: placed=3 root=<wallId> storageBefore=<cell> storageAfter=<cell> doorOpened=<bool>
    movesOk=<n> movesRefused=<m> movesFailed=<f> scrollsBag=<b> scrollsStorage=<s> total=<b+s> buildingsAfterRelogin=<k>
    storageAfterRelogin=<s2> reopens=<ok>/<tried>` plus the migration and re-login suffixes; PASS = placed 3, both
    storage opens, the door, `movesFailed == 0`, `total == 20` (**nothing duplicated or lost by the moves across the
    transfer**; `scrollsBag` is the sum over the bag's slots of that dataId, `scrollsStorage` the fresh open's count
    after the mover) and, when the re-login happened inside the window, `k == 3` and `s2 == s`. The cell is the serving
    map server's channel id as the client knows it (the kit sends it with its server info, `ReadServerInfo`), not an
    address and port: ports are reused across lifetimes (live run 2: the restarted C took the retired C.1's port).
16. **Scenario `splitbuild`** (Dynamic, `split`'s spec: C at x = 0, child C.1 takes +x; the eight standing bots as
    `split` places them; no harvest bot (`plan=stand_plan()`), no synthetics, `MESH_NO_MONSTERS=1` (D: the building is
    the subject; no monster check); `start_split` gains `tester=(mode, x, z)` (both `db_reset`'s placement and the
    Play), `clear_buildings=`, `observe_seconds=`, `line_x=` passed through to `request_play`, and **`before_command=`**
    (a callable run after every bot stands and before the command; the stand timeout and its wait are additive), each
    passed to the rig only when set (the FakeRigs keep their shapes); the tester in R at (60, 70) in mode `buildtree`
    with line x 0 and a window of **720 s** for the tester and the bots. Flow:
    1. `db_reset(..., clear_buildings=True)`: the `storageitem` rows of the map's buildings (`storageType` 3, owner in
       their ids) **then** the `buildings` rows, on every channel — the plan's "clear buildings on every channel";
    2. Play up, bots standing, `before_command` waits for phase `buildtree-ready` (120 s; a timeout fails the scenario
       and sends nothing); then `<seq> split C x 0 + C.1`; wait for `done` and C's e3 apply (as `split`);
    3. grade the split's status as `split` does (accepted, done, no abort or freeze, the drain gate **open** with no
       stragglers: the tree drained) plus: C logs `Building transfer <root> zone C->C.1: sent (3 node(s))` and `... :
       done`, C.1 logs `... : promoted <p>, spawned <s>, owned already 0` with p + s = 3; no `released`, no `storage ...
       still in use`, no `Building ... kept here`, no `failed`; statics as `split` grades them (`HarvestSpawn_C` lies in
       R); no synthetic or monster check;
    4. wait for phase `buildtree-checked` (120 s after e3);
    5. the merge, composed from `regress_merge`'s pieces (`wait_before_merge`, `send_merge` with its window guard,
       `grade_status`, `wait_tidy`, `grade_exit`, `grade_store`, `grade_retiring`, `grade_prunes`; `MergeSpec("split",
       720, False)`); plus: C.1 logs `Building transfer <root> zone C.1->C: sent (3 node(s))` and `done`, C logs
       `promoted|spawned` summing to 3, `Merge retire: C.1 drained, quitting` with no warning-level `Merge retire` line
       (`grade_retiring`); no monster or synthetic check;
    6. **the database after the merge** (through `rig.db_rows(sql, params)`, a reader the FakeRig fakes; the kit's
       manager writes MySQL before its cache): exactly three `buildings` rows for the map, all `channel = 'C'`, ids =
       the three the RESULT and C's transfer lines name;
    7. **restart of P**: a second window guard (240 s left on the earliest bot, else FAIL `window too short` and no
       kill), then 10 s after the merge's `done` kill C's process (`rigsplitcrash.kill_script`, exactly 1), wait for its
       second lifetime (`regress_splitcrash.wait_answer`, 120 s) and its line `Buildings loaded on C: 3 placed in 1
       tree(s) and 0 scene, 0 tree(s) owned elsewhere, 0 scene row(s) of other cells skipped, 0 already here (channel
       C, 3 row(s))`; the database still holds three rows (a duplicate would be a fourth);
    8. RESULTs (after the kill every bot and the tester re-log in onto C): R-bots `migrations` in {2, 3} (a hop after
       the re-login allowed, as `splitcrash_base` allows) and `relogins=1`, P-bots {0, 1} and `relogins=1`
       (`rigsplitcrash.bot_problems`), `allReady`, `maxGap < 1000`, one `Handoff START` each way per R-bot; the tester's
       RESULT PASS with `storageBefore=C`, `storageAfter=C.1`, `buildingsAfterRelogin=3` and
       `storageAfterRelogin == scrollsStorage` (a note when the re-login did not finish inside the window); the chest's
       `storageitem` rows after Play sum to `scrollsStorage`. Also: 0 exceptions, 0 teleports, no `Layout op refused`,
       no `Policy:` line, the layout grade through `rigsplitcrash.lifetime_apply_violations` with
       `rigbuilding.expected_lives` (every cell: `rigmerge.merge_expected_applies` as lifetime 1; C adds lifetime 2 =
       the base rectangle at a pair the status cannot know), ops filtered by `rigmerge.disallowed_op_lines`, and C's
       second lifetime logging no Split, Merge or Retire line. It joins `ALL`, `TWO_HOST_SKIP` and `PERSIST_SKIP`;
       `test_registration` lists it.
17. **Other scenarios.** `build` (Static) is unchanged in behaviour (every path above is Dynamic-only) and keeps
    passing; `split`, `merge`, `splitc2`, `mergec2`, `splitcrash_*`, `auto*` place no building and see no new line but
    the load line; `dynidle` and the eleven base scenarios in `all:Dynamic` see each Dynamic cell's `Buildings loaded
    on <cell>: 0 placed ...` line (allowed everywhere; `grade_common` does not read it).

### Acceptance
18. EditMode tests: `MeshBuildingTrees` (build: roots, orphan child as root, parent-before-child order, a cycle
    broken; partition: scene matched, scene skipped, already here, placed; ownership by a `MeshFakeWorlds` layout after
    `SplitC`), `MeshBuildingChannel.BaseOf` (`C` → `C`, `C.1` → `C`, `C.12` → `C`, `C2` → `C2`, `C.x` → `C.x`),
    `MeshBuildingTransferRules` (candidates: roots only, another owner of the same map, linked and settled, the
    in-flight cap, back-off, nearest first, not before the load; the receiver's receipt: gated, loading, not the owner,
    unknown prefab fails the tree, owned already, promote, spawn; the release's wait predicate; the re-promote applies
    the clone), the destroy guard predicate (owned, puppet, demoted; Static map), the leaf-first order of a demoted
    tree, `MeshDrainCounts` with `buildings` and the v3 codec round trip (v2 refused), `MeshDrainState.IsDrained` with
    buildings (the 2c assertions flipped), the new `MeshDynamicSettings` validation, the `BuildingTransfer` codec, the
    kit-edit reflection test (decision 1); all 2b-3 tests unchanged but for the texts named here. Python tests:
    `rigbuilding` parsers (transfer lines, the load line, the RESULT fields, `expected_lives`),
    `regress_building` flow with a FakeRig (a status writer advancing `phase=`/`finished=`/`result=`, `ps` for the
    counts and the kill, `db_rows`, the window guards, the `before_command` timeout), `test_provision` hashes and bag
    order, `test_registration`, `test_imports`; ruff.
19. Rig: zero new compiler warnings; the stray check; `splitbuild` 3/3 (flake policy: one rerun, recorded); `split`,
    `merge`, `splitc2`, `mergec2`, `splitcrash_prewarm`, `splitcrash_drain`, `splitcrash_base` 1/1 each (3/3 on any
    that fails once); the eleven base scenarios Static 11/11 and `all:Dynamic` 11/11 (`build` in both); DESIGN 0.81
    (the row, §13 persistence, §17 CE-10, the known limits: scene rows of children accumulating under the base channel,
    the drain-timeout unit, the kit's handoff-versus-move race behind `total == 20`, `BuildLimit` counted per process,
    the two-owner second of decision 11, the database manager's cache key across a Play), SETUP (CE-10 in the database
    section, the settings, `buildtree`, `splitbuild`, the held placements), `ServerMeshPackageBuilder` 0.81.0 and
    `UpdateDate`, `regress.py --package`.

## Interfaces and log lines (exact)
- **Kit (CE-10):** `protected virtual string BuildingPersistenceChannelId => ChannelId;` in `MapNetworkManager.cs`.
- **Map side:** `Building channel on <cell>: <channel>`; `Buildings loaded on <cell>: <n> placed in <t> tree(s) and <s>
  scene, <o> tree(s) owned elsewhere, <x> scene row(s) of other cells skipped, <a> already here (channel <ch>, <r>
  row(s))`; `Building transfer <root> zone <from>-><to>: sent (<n> node(s))` / `done` / `released (<reason>)` / `save
  failed` / `storage <id> still in use after <s> s` (Warning) / `promoted <p>, spawned <s>, owned already <o>` /
  `failed (<reason>)`; `Building <id> placed at <pos> belongs to zone <z>, which is not ready: kept here` (Warning).
  Removed: `Merge retire: <cell> leaves <n> building(s) saved under its channel`.
- **Wire:** link 131 `BuildingTransfer` {rootId, int count, BuildingSaveData[] nodes}; 132 `BuildingTransferReply`
  {rootId, bool ok, string reason, int promoted, int spawned, int ownedAlready}. `MeshLayoutEvidenceMessage.WireVersion
  = 3` with `MeshDrainCounts.buildings` appended after `pendingHandoffs`. `MeshDrainState.buildings` keeps its slot.
- **Settings:** `MeshDynamicSettings.buildingTransfersInFlight = 2`, `buildingStorageCloseSeconds = 5f`.
- **Auto client:** mode `buildtree`; phases `buildtree-placed`, `buildtree-ready`, `buildtree-checked`; lines and
  RESULT as decision 15.
- **Harness:** `request_play(..., observe_seconds=None, line_x=None)`; `db_reset(..., clear_buildings=True)` clears the
  storage rows first; `db_rows(sql, params=())`; `regress_split.start_split(..., tester=None, clear_buildings=False,
  observe_seconds=None, line_x=None, before_command=None)`.

## Files
Kit: `MapNetworkManager.cs`, `MapNetworkManager_DatabaseFunction.cs` (CE-10); `Docs/ServerMeshing/upstream/0008-*.patch`,
`PR.md`, `README.md`. Mesh/: new `MeshMapNetworkManager_Buildings.cs` (channel override, destroy guard, demoted
trees and the leaf-first despawn), `_BuildingLoad.cs` (Pre/PostSpawnEntities, the load, the held-placement
condition), `_BuildingTransfer.cs` (tick, release, receive, reply), pure `MeshBuildingTrees.cs` (trees, partition,
`MeshBuildingChannel`, `MeshBuildingTransferRules`, `MeshBuildingReceipt`); edits: `CellMeshLink.cs` (131, 132, the
structs, handlers), `MeshLayoutEvidence.cs` (v3, `buildings`), `MeshSplitOperation.cs` (the why text),
`MeshDrainState.cs` (`IsDrained`), `MeshLayout.cs` (settings), `MeshLayoutValidator.cs`, `_StaticHandoff.cs` (one hook
in the tick), `_Evidence.cs` (the count), `_Drain.cs` (the count, the warning removed), `_Layout.cs` (the load hook,
the hold condition: two lines), `_Ghosting.cs` (the placement target check and the despawn hook: one line each).
TestMap: `Prefabs/TestStorage-CE.prefab`, `TestDoor-CE.prefab`, `GameData/TestStorageItem-CE.asset`,
`TestDoorItem-CE.asset`, `BaseDemo/GameData/GameDatabase-CE.asset`, `ServerMeshAutoClient_Buildings.cs`,
`ServerMeshAutoClient.cs` (the mode, one dispatch line). Tools: new `regress_building.py`, `rigbuilding.py`, their
tests (each under 500 lines); `regress.py` (registration, `request_play`, `db_reset`, `db_rows`), `regress_split.py`
(`start_split` kwargs), `provision_rows.py`, `test_provision.py`, `test_regress.py`. Docs: DESIGN, SETUP, this contract.

## Critique log
- **Acceptance (2026-10-19, BRENDOAIBOX):** 2904 EditMode tests (one skipped, as before; 26 new), 808 harness tests, ruff
  clean, no compiler warning; `splitbuild` 3/3 on the final build (141/141 each; 54-60 scroll moves per run across the
  transfer, 0 failed, the scrolls summing to 20); `merge`, `splitc2`, `mergec2`, `splitcrash_prewarm`, `splitcrash_drain`,
  `splitcrash_base` 1/1 each; `split` 3/3 (120/120) after one run failed on a kit client UI exception in a P-bot's log
  (a monster's `DataId` null while its UI instantiated at login, unrelated); the eleven base scenarios Static 11/11 and
  `all:Dynamic` 11/11; package 0.81.0.
- **Live runs (2026-10-19):** run 1 (139/141): the tree drained at the split (drain gate open), C.1 promoted its three
  puppets, the merge handed them back, the rows stayed under C, the restarted C loaded exactly the tree, every bot
  re-logged in; the tester's second and third storage opens were never seen because the kit's `ClientStorageActions.Clean()`
  drops every client storage event on a new map connection (the hooks are re-attached before each open now), and its
  first move on C.1, before any open there, tripped the kit's `MMOServerStorageHandlers.NotifyStorageItemsUpdated`
  (`usingStorageClients[storageId]` with no entry: an exception line on C.1 and a stuck `storageUsers` entry that
  refused every later move) → the mover opens before it moves and re-opens after a close, as the kit's UI does (a kit
  robustness limit, recorded). Run 2 (140/141): everything but the tester's cell check; the RESULT named the serving
  cell by address and port and the restarted C had taken the retired C.1's port → the client reports the channel id
  the kit's server info gives it.
- **Rev 0 → 1 (2026-10-18), protocol and Mesh/ review:** rev 0 tied player readiness to the gate through a flag the
  MMO kit never reads, and the load's duplicate hazard (a placement before the read, then the read creating the same
  id) was open → the load runs when the gate opens, placements are held until it is done, rows already here are
  skipped, and readiness is untouched; the updater on in-scene buildings before the read would have created the
  database manager's cache key and hidden every placed row → updaters after the read, the invariant stated; a transfer
  into a loading receiver, or a tree two cells hold (a placement kept while its owner restarts), could duplicate →
  `loading` refused, storage evicted and reloaded first, "owned already" re-applied, the two-owner second recorded; the
  re-promote restored nothing (a `MaxValue` lifetime would persist) → the clone restores every field; demoted copies
  in flight expired after 4 s → `lastSeen` refreshed, a gone node re-created from its clone; a demoted root's despawn
  chain-destroyed its demoted children (duplicate drops) → leaf-first despawn, `CreatorId` cleared on a demoted copy;
  the updater's same-frame enqueue after `BuildingDataSaved` → removed from the static set, `BuildingDataSaved` before
  and after the force save; the settled check ran on Static maps → Dynamic-gated; the split count used the hysteresis
  → `Contains`; a late move's `SetStorageItems` re-adds the evicted entry (same content, noted); the handoff-versus-move
  race, the children's scene rows, the timeout unit, `IsAllocate`/`IsInstanceMap`, the promote-in-place fields, the
  same-map guard on `OwnerOf`, the v3 touch list; the plan's D6 order deviations named above.
- **Rev 0 → 1, harness review:** the readiness change would have bounced the kit client's first login (the same fix as
  above); the storage open is strict at 3 m and the sweep closes it, so the tester never saw a notify → `activatableDistance`
  6 on both prefabs, the tester within 1.5 m, `scrollsStorage` from a fresh open after the mover; `start_split` sent the
  command the moment the bots stood → `before_command`; `grade_entities` of `split` and `merge` need synthetics and
  the merge flow is monolithic → composed from its pieces, no synthetic or monster check, `MESH_NO_MONSTERS`; after
  the kill every bot re-logs in → the migration and re-login table through `rigsplitcrash.bot_problems`; one process
  lifetime per cell in the apply grades → `rigbuilding.expected_lives` with C's second lifetime; the client has no cell
  id → `address:port` mapped through C's peer lines; the window → 720 s and a second guard before the kill;
  `start_split`'s hardcoded tester and `db_reset` → the kwargs, passed only when set; database reads through the rig
  (`db_rows`) with the storage rows deleted first; the content details (the items fill `GameInstance.BuildingEntities`,
  the bag order, the ids); the move message fields and `movesFailed`; the re-login count's timing and the `relogin-`
  phase prefix; the editor lines are not a file the harness reads; the file sizes.
