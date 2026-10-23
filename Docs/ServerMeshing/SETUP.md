# Server Meshing — Setup Guide

How to split a map into cells served by separate map servers, join maps into one world, run it, and check it.
The design and its measurements are in [DESIGN.md](DESIGN.md); this document is the operator's and developer's path.

**Status:** everything below is exercised by the regression harness on the test rig (ten cells over two maps: a 3×3
grid plus one cell across a seam; one machine, or two with one cell served from the other), Dynamic maps' splits, merges,
crashes and central restarts included (§6a). Version 0.83.

---

## 1. What you get

- A kit map is cut into **cells** — rectangles served by separate map-server processes. A player walking across a
  cell edge is handed to the next server without a loading screen or a visible break (about 200–300 ms server side,
  hidden behind stand-ins on the client).
- Cells exchange **ghosts** of everything near their shared edge (players, monsters, pets, drops, harvestables,
  buildings), so players see and fight across it; hits, kills, loot, party and guild rewards, quest counters, buildings
  and pickups all resolve on the owning side.
- Maps can be placed next to each other in one world; a **seam** between two maps is crossed the same way, the
  client pre-loading the neighbouring scene behind the player's back.
- **Sleeping cells:** with nobody near it a cell does not run at all; it starts a few seconds ahead of the first
  player who approaches and exits again later. Empty land costs nothing.
- **Dynamic cells:** a map set Dynamic has a busy cell split into more cells at runtime and merged back when it quiets,
  by an operator's command or the central's own load policy; the cells survive crashes and a central restart, and
  buildings follow the cut (§6a).

Nothing in the kit's core is edited: the add-on is subclasses on the kit's manager and entity prefabs plus one
world-registry asset.

## 2. Concepts in one minute

| Term | Meaning |
|---|---|
| **Map** | A kit map: one map info, one scene. Its scene frame is the coordinate frame of every cell in it. |
| **Cell** | A rectangle of a map served by one map-server process. Its **id is the channel id** of that server, and must be unique across the whole world. |
| **Layout** | How one map is cut: a uniform grid, or explicit rectangles of any size (small where crowds are expected, large over empty land). |
| **World registry** | The one asset holding every layout and each map's placement in a shared frame. Cells whose rectangles touch — edge or corner, in the same map or another — are **neighbours**. |
| **Band** | The strip along a shared edge (80 m by default, matched to the kit's visible range) in which a cell streams ghosts to its neighbour. |
| **Seam** | The line between two *maps*. Crossing it is a map change and a frame change in one hop. |
| **Awake set** | With sleeping cells on: every cell with a player, plus a ring or two of neighbours. Only those run. |

Sizing rule of thumb from the capacity test (DESIGN 0.53): one cell process holds about **270 players at a 60 fps
server tick** on a modern core; choose the cell size so no crowd you expect exceeds **250 in one cell**; 250–300 m is
right for a sandbox where towns can appear anywhere.

## 3. Prerequisites

- The add-on folder `Assets/ServerMeshing/` (it compiles into the game assembly; no asmdef needed).
- OpenMMORPG `develop` at d077c5f (2026-10-20) or later. It carries all eight kit changes the add-on relies on
  (DESIGN.md §17, [upstream/README.md](upstream/README.md)): patches 0001-0004 (on `master` too since 2026-09-15: an
  owner-change movement fix, frame-granular request waits, a virtual `IsWatched()` on the monster activity component, a
  protected pending-spawn dictionary on the map manager), 0005 (on `master` since 2026-10-01: a cache finalizer that
  corrupted pooled collections), 0006-0007 (`develop` since 2026-10-02: movement flags sent to every receiver of a
  tick, entity states read to their length) and 0008 (`develop` since 2026-10-20, [open-mmorpg/OpenMMORPG#8](https://github.com/open-mmorpg/OpenMMORPG/pull/8): a virtual
  `BuildingPersistenceChannelId` on the kit's `MapNetworkManager`, read at its seven building database sites). The add-on
  does not compile without 0006 (the state sender calls its `IEntityMovementServerStateFlags`) or without 0008
  (`MeshMapNetworkManager` overrides the property, §6a.8), Dynamic maps or not. On an older kit, apply
  [upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch](upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch) (from the project root, `git apply
  --directory=Assets/OpenMMORPG <patch>`); `MeshKitEditTests` fails if a kit update drops it.
- Your map scenes in the build settings, your map infos in the game database.

## 4. Step by step

Open **Server Meshing ▸ Setup** in the editor. The window has four panels that follow this order.

### 4.1 Create the world registry

Panel 1: **Create registry asset** (or pick an existing `MeshWorldRegistry`). One registry for the whole world.

### 4.2 Cut a map into cells

Open the map's scene. In panel 2:

1. Pick the **map info**.
2. Set the **cell size** in metres (300 is a good default; see §2).
3. Leave **world origin** at zero for your first map. For a second map that should touch the first, set its origin so
   the two maps' cell rectangles meet in the shared frame (e.g. a 10 km map to the east gets x = 10 000).
4. **Measure open scene** — the grid is fitted to everything with a renderer, collider or terrain.
5. **Add / replace layout**. Cells are named `<prefix>-<x>-<y>`; the prefix defaults to the map id.

The registry inspector lets you refine a layout afterwards:

- **Explicit cells** (`cellRects`, each an `id`, a `min` corner and a `size`): list rectangles of any size instead of a grid — a dense area cut into 200 m cells
  next to 1 km wilderness cells. Rectangles must not overlap (the validator reports overlaps and duplicate ids).
- **Cell ids** (`cellIds`): explicit ids for a grid, row-major.

Below the layout, panel 2 sets each map's **cell mode**: **Static** (the default: the cut is the cut, exactly as before
0.72) or **Dynamic** (the cut gives the map's *base cells*, which the central may split while they are busy and merge
back when they are quiet). Dynamic maps have their own section, §6a.

The band is not part of the layout: a cell's server streams the strip inside its own edge.

The registry's own fields, for hand edits: `maps` (one layout per map), and per layout `mapId`, `worldOrigin` (where
the map's origin sits in the shared world frame), `gridOrigin`, `cellSize`, `columns`, `rows`, `cellIdFormat` (the id
pattern: `{map}` = the map id, `{x}`/`{y}` = column/row), the optional `cellRects` and `cellIds` above, `cellMode`, and
for Dynamic maps `minCellSize`, `maxCellsPerBase`, `autoSplit` and `autoMerge` (§6a.2). Registry-wide: `touchTolerance`
(how close two rectangles must come to count as neighbours across a seam, 0.5 m) and `dynamicSettings`, read only when
a map is Dynamic (§6a.11). Every mesh manager (`MeshMapNetworkManager`, `MeshLoginNetworkManager`,
`MeshCentralNetworkManager`, `MeshMapSpawnNetworkManager`) carries a `world` reference to this asset; panel 3 assigns
it.

### 4.3 Wire the prefabs

Panel 3: **Validate**. `!!` is an error and `!` a warning; a line whose fix is safe has a **Fix** button. The checks:

| Check | What Fix does |
|---|---|
| `MapNetworkManager` → `MeshMapNetworkManager` | Swaps the component's script on the prefab. Every serialized field is kept (the subclass adds fields, removes none). |
| `LoginNetworkManager` → `MeshLoginNetworkManager` | same |
| `CentralNetworkManager` → `MeshCentralNetworkManager` | same |
| `MapSpawnNetworkManager` → `MeshMapSpawnNetworkManager` | same |
| `PlayerCharacterEntity` → `MeshPlayerCharacterEntity`, `MonsterCharacterEntity` → `MeshMonsterCharacterEntity`, `MonsterActivityComponent` → `MeshMonsterActivityComponent`, `DefaultCharacterAttackComponent` → `MeshCharacterAttackComponent`, `DefaultCharacterUseSkillComponent` → `MeshCharacterUseSkillComponent`, `HarvestableEntity` → `MeshHarvestableEntity` | same, on every prefab that carries them |
| World registry assigned on each manager | Assigns the registry from panel 1 |
| Game instance gameplay rule is a `MeshGameplayRule` | Assigns one (creates the asset if none exists) |
| Server target frame rate ≤ 60 | Sets 60 |
| Map info of each layout in the game database; its scene in the build settings | reported; do it in the database asset / build settings |
| Dynamic maps: base cells that can never split, `minCellSize` under the band, the worst-case band neighbours and links, the spawners' `servedCells`, the settings the central checks at start (§6a.3) | **Raise to band** for `minCellSize`; the rest reported |

`MeshIdentity` needs no wiring: it is added to entities at runtime. The kit's client-instance prefab is skipped (its login and
central components are the client halves). A LAN-only game instance can ignore the gameplay-rule line.

### 4.4 See the cells

Panel 4: **Add cell visualizer to the open scene**. The Scene view shows every cell of the map with its id, size and
neighbour count; at runtime the lines are drawn as debug lines and the player's cell is highlighted. The
`ZoneDebugVisualizer` fields: `world`, `mapId` (which layout to draw), `lineHeight`, `showAtRuntime`, `showZoneLabels`,
`boundaryColor`, `currentZoneColor`.

### 4.5 Run

- **Editor rig:** **Server Meshing ▸ Launch Test Cluster** starts one map-server process per cell of the registry
  (plus the central, login and database servers) with the right arguments, or copies the commands to the clipboard.
- **Several hosts:** give each host's `MeshMapSpawnNetworkManager` a `servedCells` list of base cells (or
  `MESH_SPAWN_CELLS=C,N`) so two machines never start the same cell; see [MULTIHOST.md](MULTIHOST.md), and §6a.4 for
  Dynamic maps (every host serving a Dynamic base runs a spawner; a family runs on its base's host).
- **Production:** the kit's `MMOServerInstance` runs unchanged. Its map spawner (`MeshMapSpawnNetworkManager`) starts
  one process per cell for every map that has a layout (maps without a layout keep the kit's single-server behaviour);
  the central (`MeshCentralNetworkManager`) lists every cell as a channel, so no channel has to be typed into
  `serverConfig.json`. A map server needs only the kit's own arguments (`-startMapServer -mapName <map> -channelId
  <cell> -mapPort <port> -centralAddress … -centralPort …`); it resolves its cell from its channel id.

### 4.6 Sleeping cells

On `MeshCentralNetworkManager` (the server-instance prefab):

| Field | Meaning | Default |
|---|---|---|
| `sleepCells` | Cells start on demand and exit when unwanted. Off = every cell of every meshed map runs from boot. | off |
| `awakeRings` | Rings of neighbours kept running around any cell with a player. 2 wakes a cell a full cell ahead of a player heading anywhere. | 2 |
| `sleepAfterSeconds` | How long an unwanted cell stays up before it is told to exit. | 300 |
| `awakeTickSeconds` | How often the awake set is evaluated. | 2 |
| `wakeHoldSeconds` | How long a login or warp keeps its destination wanted while the player arrives. | 60 |
| `spawnRequestTimeoutSeconds` | A start request that produced no registration is repeated after this. | 20 |

With sleeping on, `MeshMapSpawnNetworkManager.sleepCells` must be on too (the validator checks that the two agree). A cell takes 2–4 s from launch to serving on the rig machine; a player crossing a 250 m cell on a mount
takes 15 s or more, so the rings hide the start entirely. A login into a sleeping cell waits behind the normal loading
screen (up to 20 s). A cell told to sleep refuses while a player is on it.

## 5. Reference: the map-server settings

All on `MeshMapNetworkManager` (the map-manager prefab). Defaults are the values the rig was verified with.

**Handoff**

| Field | Default | Meaning |
|---|---|---|
| `enableServerBoundaryHandoff` | on | Hand a player to the neighbour when it crosses the line. |
| `boundaryCheckInterval` | 0.1 s | How often positions are checked against the line. |
| `handoffHysteresis` | 1 m | A player must be this far past the line before the handoff starts (no thrashing on the line). |
| `handoffLeadSeconds`, `handoffMaxLeadSpeed` | 0.3 s, 15 m/s | The saved position is led by the velocity so the arrival spawns where the player will be. |
| `handoffRetrySeconds` | 2 s | Retry after a refused or failed handoff. |
| `demotePlayerOnHandoff`, `promoteGhostOnArrival`, `fastSpawnOnHandoff` | on | The departing cell keeps a puppet, the arriving cell promotes its puppet in place: no pop on either side. |
| `handoffAckGuardSeconds`, `handoffRollbackHoldSeconds`, `handoffLinkDownGraceSeconds` | 20 / 20 / 3 s | Timeouts of the notice/ack protocol and its rollback when the destination dies mid-handoff. |
| `reservedArrivalTimeout` | 15 s | An announced arrival whose client never becomes ready releases its promoted puppet after this. |
| `monsterHandoffCooldown` | 2 s | Minimum time between two handoffs of the same monster (a chase back and forth across the line would otherwise ping-pong every tick). |
| `clusterRegisterRetrySeconds` | 3 s | A restarted map server is refused by the cluster while its dead predecessor is still in the peer table (`MAP_EXISTED`) and the kit never retries; the add-on retries at this interval. |

**Ghosting**

| Field | Default | Meaning |
|---|---|---|
| `enableBorderGhosting` | on | Stream entities near a shared edge to the neighbour. |
| `ghostBandWidth` | 80 m | Width of the band. Keep it at or above the kit's visible range; DESIGN Q1 gives the rule with speeds. |
| `ghostSnapshotInterval` | 0.1 s | Snapshot rate for characters; `staticSnapshotInterval` (1 s) for drops, harvestables, buildings. |
| `ghostLeadSeconds` | 0.1 s | Puppets are steered ahead by the owner's velocity so they do not trail by a snapshot. |
| `ghostTeleportDistance`, `ghostStaleSeconds` | 4 m, 2 s | A puppet further off than this is teleported; one without snapshots is removed. |
| `puppetMotion` | Steered | How a character puppet follows its owner. Steered: the kit's movement component walks it toward each snapshot (today's behaviour). Kinematic (measurement, Phase 0): its pose is set from the snapshots, extrapolated to now, and sent to clients in the kit's own movement bytes, and an unobserved puppet moves only when a snapshot arrives; pets and mounted puppets stay steered. `MESH_PUPPET_MOTION=steered\|kinematic` overrides it at server start. |
| `ghostMonsters`, `ghostHarvestables`, `ghostBuildings`, `forwardGhostHits`, `handoffMonsters`, `ownSpawnAreasByZone` | on | What crosses the edge. Monsters chasing across the line are handed over by position; a spawn area only spawns inside its own cell. |
| `partyMemberOnlineWindowSeconds` | 5 s | A party member on another cell counts as online when the kit's online table saw it within this long (the cluster relays every online player once a second). |
| `linkAllZones` | on | Link to every cell of the map, not only neighbours (party shares and such reach any cell). A cell's link server takes 64 connections, so a map with more than about 64 live cells needs on-demand links instead (the map's `onDemandLinks` in the world registry, which overrides this for its cells). |
| `linkIdleSeconds`, `linkOpenGraceSeconds` | 30 s, 10 s | Maps with `onDemandLinks` only: a link no longer needed closes after this long without a message either way (never under the handoff ack guard or the arrival reservation plus 5 s, 25 s by default); a link being opened is not taken for a dead cell before it has been tried this long. Rig `MESH_LINK_IDLE`. |
| `linkSuspectTimeoutSeconds` | 2 s | A link a handoff came back over unclaimed is doubted (no handoff goes that way) until the neighbour is heard from; silent this long, it is reconnected — a restarted neighbour is reached at once instead of after the transport's timeout. |
| `handoffLinkSettleSeconds` | 0.5 s | No handoff across a link that has been up for less than this, so the destination has a snapshot or two of the character and promotes its puppet instead of spawning fresh (which re-summons pets — a visible swap for observers). |
| `meshLinkPortOffset` | 10000 | The server-to-server link listens on the map port plus this. |
| `expectedMaxSpeed`, `meshStatsIntervalSeconds` | 8 m/s, 10 s | Band sizing check at start-up; the `Mesh stats` log line cadence. |
| `databaseReconnectSeconds`, `databaseReconnectGraceSeconds`, `databaseDrainWaitSeconds` | 5 s, 15 s, 35 s | Re-open the **database** connection whenever this server has none: the kit opens it once at start-up and never again, so a map server that loses the database manager -- or starts before it exists -- runs on accepting players while every save fails. The grace period keeps the retry off the kit's own first attempt. Requests still waiting when the connection is re-opened can never be answered, and the kit's reconnect would clear their callbacks and leave whoever awaits them waiting for ever, so the reconnect answers each with the kit's own timeout first (`Database connection lost; reconnecting to …, timing out N request(s) it can no longer answer`; DESIGN 0.87). The drain wait is only the fallback for a kit whose request table the add-on cannot reach: it lets those requests time out on their own for up to 35 s, then reconnects anyway and says how many it stranded. A process that *hosts* the database manager is left alone. |
| `resendStrandedDatabaseReads` | on | A database request is sent again, under its own request id, once the reconnect is up when that is safe: a **read** (`Get*`, `Find*`, `Validate*`, `MailList`) that was on its way when the connection dropped, or **any** request the transport refused while the connection was down or still connecting (it never left the process). Lines: `... and keeping N to send again` at the reconnect, then `Database connection open again: sent R stranded read(s) again and U request(s) it had refused while down`. The kit's caller -- an arrival's access-token check, its character or its gold, a handoff's save -- then gets the database's answer instead of a timeout. A write that went out is never sent twice (it may have been applied before the drop) and still ends in a timeout. Each request's kit 30 s timer still bounds it (DESIGN 0.89). |
| `arrivalLoadRetrySeconds` | 90 s | A handoff arrival's relates data (gold, cash, user level) loads in the background; the kit kicks the player on the first failed query. When the query failed because the database connection dropped under it, it is asked again once the connection is back, for up to this long (`Arrival <name> conn=<id>: <what> failed with the database connection lost; asking again once it is back (retry n)`); a failure over a live connection, or running out of time, ends in the kit's kick. 0 restores the kit's kick. |
| `loadReportSeconds`, `occupancyBucketSize` | 5 s, 20 m | Every this many seconds the cell sends the central a **load report**: the work probe's window (main-thread work per frame without the frame limiter's wait, its p95, the share of frames that did not wait at all), what it owns and holds as puppets, and an occupancy grid of where they stand in buckets of this size. The central keeps the latest per cell and logs a one-line summary every `loadSummarySeconds` (30 s). 0 disables. |
| `clusterConnectRetrySeconds` | 5 s | Re-open the cluster connection when the process could not open one at all. The kit handles every later failure by itself, but not this one: with no client socket it polls no events, so no disconnect is ever raised and the server runs on serving nobody. Matters when map servers can start before the central. 0 disables. |

**Client**

| Field | Default | Meaning |
|---|---|---|
| `useVisualStandIns`, `standInBlendSeconds`, `standInMaxLifetimeSeconds`, `standInMatchDistance` | on, 0.3 s, 1.5 s, 2.5 m | The seamless swap: clones of everything visible cover the reconnect, then blend onto the real entities. |
| `fastClientReadyOnMigration` | on | Skip the kit's post-load settle and GC on a hop. |
| `migrationReadyTimeoutSeconds`, `useMaskedMigration`, `migrationMaskFadeSeconds` | 10 s, on, 0.25 s | Fallback mask if the destination is silent. |
| `seamPreloadDistance`, `seamUnloadHysteresis` | 80 m, 40 m | Pre-load a neighbouring *map's* scene this close to the seam; drop it that much further away. 0 disables (the kit then reloads the scene with a loading screen). |
| `heldRpcTimeoutSeconds` | 15 s | RPCs that arrive before the character exists are held, then replayed. |
| `entityDataWaitSeconds` | 1 s | After the blend, wait up to this long for the new entity's stats before re-binding the HUD, so it never shows 0/0. |
| `debugCaptureMigrationFrames` | off | Editor only: save a screenshot every few frames during a seamless swap to `Logs/MigrationFrames`. |

**Other components.** `MeshMonsterActivityComponent`: `ghostWatchMargin` (10 m) is the range beyond the monster's visual
range in which a player *ghost* keeps it awake (the kit sleeps a monster nobody subscribes to, and ghosts never
subscribe), re-evaluated every `ghostWatchInterval` (0.5 s). `MeshLoginNetworkManager`: `routeByPosition` (on) sends a
selected character to the cell that owns its saved position instead of the client's chosen channel.

**Diagnostics:** `traceHandoffTiming` (on) writes one line per handoff step, ghost lag and replay; the `Mesh stats`
line every 10 s reports players, owned and ghost counts, link traffic, handoffs (mean/max ms), ghost steering error,
frame time, CPU, memory and client send rate, then the work probe (`work mean … p95 … max … wait … util … sat …
probe=Valid|Unknown|Invalid|Uncapped`) and the link traffic per neighbour (`links E=… N(down)=…`). The stats line
shows only the latest probe window; every window (every 5 s) is also logged on its own as `Work window 5.00 s: frames …
work mean … budget … sat …% probe=… | owned players … monsters … | puppets players … monsters … | clients … cpu …%`,
in invariant culture, which is what the load tools read. Right after it comes `Work ticks 5.00 s: …`, which splits the
same frames by what ran in them: the kit's network ticks (20 Hz, stamped around the kit's server tick) against the
frames without one, ticks expected and dropped, frames with 0, 1 and 2+ ticks, tick-frame and other-frame work
(mean/p95/p99/max, 50 µs bins), the tick itself, physics steps per second, work ms/s, the budget the cell is modelled
at, and gen-0 collections. With the rig's AOI timing on, the tick is also decomposed into sync+ping, the interest
manager's scheduling and the movement send, AOI-pass frames are kept apart from the rest, and the interest manager's
completion work outside the tick is counted as `late`; without it those fields read 0 (`parts 0`).

## 5a. How many players a cell holds

**A cell's cost depends on how busy its NEIGHBOURS are, not only on its own occupancy.** It simulates its own
players and also holds a puppet of every entity standing in the band on the other side of each shared edge, so a
cell surrounded by full cells does far more work than the same cell surrounded by empty ones. Measured on a 16-core
box at 100 players per cell, varying only how many neighbours carried load:

| loaded neighbours | work per frame |
|---|---|
| 0 | 3.2 ms |
| 1 | 6.4 ms |
| 3 | 7.6 ms |
| 8 | 12.8 ms |

One loaded neighbour doubles it. **Size a cell for the world you expect to be full, not for a cell measured on its
own**, and treat any single-cell benchmark as a lower bound.

With three loaded neighbours, five cells busy, three runs per step:

| players per cell | work per frame | fits 60 Hz (16.7 ms) | fits 30 Hz (33 ms) |
|---|---|---|---|
| 50 | 3.0 ms | yes | yes |
| 100 | 7.6 ms | yes | yes |
| 150 | 13.8 ms | **yes, at the edge** | yes |
| 200 | 21.3 ms | no | yes |
| 250 | 27.3 ms | no | **yes, at the edge** |

A cell is one single-threaded process, so this is a per-cell limit: more cores do not raise it, more cells do. Plan
about 150 players per cell at 60 Hz or 250 at 30 Hz **in a partly-populated world**, expect less when every
neighbour is full, and budget ~1.2 to 1.5 cores per cell and ~200 MB.

The band is what drives the neighbour cost, and it is wide relative to these cells: 80 m of an 800 m perimeter on a
200 m cell leaves only a 40x40 m interior that is in nobody's band. Doubling the cell edge to 400 m halves the
fraction of a neighbour's players you have to hold, which is the lever to reach if ghosting dominates.

**Do not read the `Mesh stats` frame mean as saturation on a capped cell.** The frame limiter delivers ~17.4 ms
whatever the load, so a cell with nine milliseconds of headroom still reports above the 16.7 ms budget. Read the
**work probe** instead (`work mean … util …` at the end of the line, DESIGN 0.71): it times the frame from the end of
the limiter's wait to the start of the next one, so it is the work per frame on a capped cell, and `util` is that over
the budget. `probe=Valid` means the process has been seen to wait at its cap, which proves the timing brackets the
wait; `aggload.py --uncapped` remains as the older way to measure work.

## 6. The database

The add-on stores nothing of its own: characters, buildings and items go through the kit's database manager as usual, so
any backend the kit supports works. One kit edit touches persistence (CE-10, DESIGN §17): the kit keeps buildings per
channel and map, and on a Dynamic map every cell of a split family reads and writes them under the **base cell's
channel**, so a building placed on a child keeps its row when the child merges back; a cell loads only the trees whose
root stands in its own rectangle, once the central has answered its layout. Storage rows are keyed by the building's
id and need nothing. The rig runs **MySQL**, which matters for two reasons: several map-server processes
and, later, several hosts all reach one database, and SQLite is a single file on one machine.

- **Choosing the backend.** `MMOServerInstance` reads `Config/serverConfig.json` only when it is *not* running in the
  editor, so for a rig driven from the editor the backend is the server prefab's `databaseOptionIndex` field. On the demo
  prefab 0 is `MySQLDatabase` and 1 is `SQLiteDatabase`. Built servers read the config file, so keep the two in step.
- **Credentials** live in `Config/mySqlConfig.json`, which every backend reads directly regardless of the editor. That
  file is gitignored; copy `Config/mySqlConfig.example.json` over it and fill in your own.
- **Schema.** Load the kit's `Assets/OpenMMORPG/MMO/SQLs/mysql_main.sql` into an empty database; the server applies its
  own migrations at start-up from there.
- **The harness** reads the same config file and talks to the database with `pymysql`, so `pip install pymysql` once.
- **Test accounts.** `python Tools/ServerMeshing/provision.py` creates what the scenarios play, with the editor out of
  Play: accounts `meshtest` and `meshbot` (password `meshtest123`), each with one character (MeshTester2 and
  meshbotChar) holding the test bow, the skill scroll stack, the pet and the mount (and, for MeshTester2, three
  building items), both test skills, the kill quest, and party 1. `--check` reports what is missing without changing
  anything. Every scenario puts the two characters back to this state first, so a scenario never depends on what the
  previous one left behind.
  It writes only those two accounts and their characters, and stops before touching anything if a fixture's name or
  party 1 belongs to another account, so it can share a database with other work. `--create-schema NAME` creates an
  empty database from the kit's `mysql_main.sql` (every migration included) for a rig that wants its own, `--remove`
  takes the rig accounts out again, and `--database NAME` works on another database without touching the config.
- **Where the editor starts cells from.** In the editor the kit's spawner starts every cell from the server prefab's
  `overrideExePath`, an absolute path saved with the prefab. When that file does not exist on this machine (a clone
  elsewhere, another drive), `MeshMapSpawnNetworkManager` starts them from this project's own player build,
  `Build/Server/<product name>.exe`, and logs a warning naming both paths. The same goes for the prefab's
  `publicAddress`, which every cell advertises to clients and to its neighbours' links: a private-network address
  that no interface of this machine has is another computer on the LAN (the prefab was saved for a two-machine run
  there), so cells started here advertise this machine's address on that subnet, or loopback, instead.
- **Ports.** The rig's cluster uses the kit's defaults (central 7000, cluster 6010, map spawner 6001, database manager 6100,
  login 7500, cells from 8000 with their links 10000 above; the checks cover 8000-8063 and 18000-18063, room for 64 cells on one host). Another kit server on the same machine holding those ports makes the editor's
  servers fail to bind with `Only one usage of each socket address`, and its login server then joins the *other*
  cluster: the scenarios fail with no cell logging at all. `regress.py` and `abload.py` check the ports first and
  refuse to start, naming the process that holds them; stop it for the run.

## 6a. Dynamic cells

A **Static** map keeps the cut you drew. A **Dynamic** map treats that cut as its **base cells**: the central may cut a
busy base cell into more cells (its **family**: the base and its **children**) while players crowd it and merge them
back when they leave, by an operator's command or by its own policy. Players are handed across the new line like any
other (one migration, 0.26-0.42 s on the rig, no visible jump); nobody is kicked by a split or a merge. This section is
the whole operator's path; the design record behind each step is DESIGN 0.72 and 0.76-0.82.

### 6a.1 When to choose it

- Choose Dynamic for land whose crowds you cannot place in advance (a sandbox where towns appear anywhere, an event
  ground). A Static grid fine enough for the worst crowd costs a process per cell all the time; sleeping cells (§4.6)
  are the cheaper answer when the load is spread thin and moves.
- A cut relieves a cell only when the crowd spreads over more than about twice the band: a crowd narrower than 2 x the
  band, or one standing on a base cell's edge, gets little or nothing from a split (both sides hold most of it as
  puppets). Per-client traffic does not change either way.
- What Dynamic costs at all times: every server of the map waits for the central's answer at start (`Layout gate open
  on …` within a second on the rig); character puppets on the map are kinematic (cheaper, DESIGN 0.76); the central
  runs a layout coordinator and keeps its store file; a cut family's cells never sleep.
- Choose the mode before starting the servers: it is read at start and the panel is disabled in Play.

### 6a.2 Setting up a Dynamic map

In panel 2 set the map's **cell mode** to Dynamic; its four fields and the registry's `dynamicSettings` appear below it.
Size the base cells first: a base cell must be at least twice the minimum cell on one axis (and the minimum on the
other) to be cut at all. The policy's cuts fall on the load report's bucket (`occupancyBucketSize`, 20 m), a command's
anywhere, so a base just over twice the minimum may split by command and never by the policy.

| Per map | Default | What it decides |
|---|---|---|
| `minCellSize` | 0 = `defaultMinCellSize` (100 m) | The narrowest a cut may leave a cell, on either axis. Never below the band: the central uses the band instead. |
| `maxCellsPerBase` | 0 = `defaultMaxCellsPerBase` (4) | The most live cells one base cell may become. With the band neighbours (§6a.3) this is what bounds a world. |
| `autoSplit`, `autoMerge` | off | Let the central split hot cells and merge cool ones by itself (§6a.6). Off: only operator commands cut. |
| `onDemandLinks` | off | Any map, Static or Dynamic: its cells link only to the cells they need (band neighbours, and the cells a handoff, ghost, lent or borrowed record names), open a link the moment one is needed and close it once it has been unneeded and silent for `linkIdleSeconds`; `linkAllZones` is ignored for them, and a party share for a member no link reaches goes through the central. Needed past about 64 live cells on a map (§6a.12). In the logs: `Mesh links on demand: …` at start, a `Mesh links: …` line after every `Mesh stats` line, `opened on demand (<reason>)` and `closed on demand` lines. |

The registry-wide settings that matter most (all of them are in §6a.11):

| `dynamicSettings` | Default | What it decides |
|---|---|---|
| `bandWidth` | 80 m | The band every process computes band neighbours with, and the narrowest cell. Keep each map manager's `ghostBandWidth` at or under it (a wider one logs an error). It must be above 0 (Validate says so). |
| `coordinatorStorePath` | `Temp/ServerMesh.Coordinator.json` | Where the central keeps the layout it sent and the operation it runs. A restarted central reads it back (§6a.9): keep it where the central's next start finds it (an absolute path on a disk that outlives the process). |
| `splitUtil`, `splitDwellSeconds` | 0.8, 45 s | How hot (work over the frame budget) and for how long before the policy plans a split. |
| `mergeUtil`, `mergeDwellSeconds` | 0.5, 300 s | How cool two cells must be predicted together, and for how long, before they merge. |
| `opCooldownSeconds` | 120 s | After any operation on a family before the policy plans another there. |
| `drainRatePerSecond` | 5 | Character handoffs a draining cell starts per second: the database's sustained rate (DESIGN 0.75). A split of 250 players takes about 50 s. |
| `childDownGraceSeconds` | 20 s | How long a dynamic cell may be gone before the central restarts it. |
| `recoveryCeilingSeconds` | 60 s | How long a restarted central waits for the cells to say what layout they hold. |

On `MeshLoginNetworkManager`, `recoveryWaitSeconds` (15) is how long a login waits for a restarting central's recovery
before it routes by the layout as it stands.

### 6a.3 Validate

Panel 3's **Validate** judges every Dynamic map before anything starts (`!!` is an error, `!` a warning):

| Line | Meaning | Fix |
|---|---|---|
| `minCellSize … is under the … m band` (error) | The central uses the band in its place; the number you set is not the one that runs. | **Raise to band** sets the map's `minCellSize` to the band: nothing that runs changes. |
| `… base cell(s) can never split` (warning) | No cut of those base cells leaves both sides at least the minimum cell (a cut needs twice the minimum on one axis and the minimum on the other), or `maxCellsPerBase` is 1. A `maxCellsPerBase` of 0 or less gets its own line: commands meet no limit and the policy never splits. | none: make the cell bigger or the minimum smaller. |
| `up to <n> band neighbours … over the 32` (warning) | A cell streams to at most 32 band neighbours. If every base near this one is cut to `maxCellsPerBase` (never more cells than its area holds in minimum cells), one of its cells could pass that, and a layout change that would (a split, or a merge whose survivor reaches more cells) is refused. The count is an upper bound for a world started under these settings. | none: lower `maxCellsPerBase`, or cut the map coarser. |
| `with linkAllZones a cell links to every live cell of its map … over the 64` (warning) | With `linkAllZones` on (the default) every cell links to every live cell of its map plus its band neighbours on other maps, and a cell's link server takes 64. Cut to `maxCellsPerBase` the map could pass it. A map with `onDemandLinks` is not judged by this count. | none: turn the map's `onDemandLinks` on (its cells then link to their band neighbours and the cells they have business with, and a party share for a far member goes through the central), lower `maxCellsPerBase`, or turn `linkAllZones` off on the map managers (party shares then reach only linked cells). |
| `servedCells of … leave out the Dynamic base cell(s) …` (warning) | Several hosts (§6a.4), judged over every spawner prefab's list together: a child, every supervised restart, a kill and the policy's host-headroom check go to the spawner that advertises the base, never to another host. With no spawner listing a base (and none with an empty list), the central refuses the family's splits and restarts; a host that runs its cells by hand cannot serve a Dynamic map's cells. | none: run a spawner on every host serving Dynamic bases, and list them. |
| `an empty servedCells starts every cell at boot, the cells … list too` (warning) | One spawner prefab lists its cells and another lists none: the second starts every cell at boot, the others' too (the cluster keeps the first registration of each), and takes the family of every Dynamic base no list names. | none: give every host its list. |
| `… is listed by more than one of …` (warning) | Two spawner prefabs list the same cell: both hosts start it at boot, and its family goes to whichever spawner registered last. | none: list each base on one host. |
| `servedCells names …, which is no cell of the registry` (warning) | A typo, or a child id: children are never listed (they start on their base's spawner). | none |
| `Registry: dynamicSettings.…` (error) | A setting the central would reject at start (the supervision grace, the policy's ranges, the cost model's weights). | none |

A Dynamic map with none of these gets one `OK` line naming its minimum cell and its worst band-neighbour count. The
checks read the asset: a `MESH_BAND` or `MESH_SPAWN_CELLS` set for a run is not seen.

### 6a.4 Running it

- Nothing to start by hand: the central runs the layout coordinator whenever a map is Dynamic, every cell of the map
  says hello once it has registered and holds every handoff and building placement until the answer
  (`LayoutHello sent`, `Layout <pair> applied: <cell> owns …`, `Layout gate open on …`; a warning every
  `layoutGateWarnSeconds` while it waits).
- The map spawner starts the base cells at boot and never a child: the central starts a child, and has a dead child
  (or, with sleeping cells, a dead base) restarted, **through the spawner that advertises the base cell**, so a family
  runs on its base's host; with sleeping cells off the kit's own auto-restart brings a base back on the spawner that
  started it. Each child is a new map-server process and a new channel (its id the cell id, e.g. `C.1`) on the host's
  next free map port from `spawnStartPort` (a port is reused once its process has exited), its mesh link
  `meshLinkPortOffset` above. A host's range must cover every process it runs at once: base cells, children, other maps,
  and a restart that overlaps its predecessor's exit. The rig opens 64 of each (`8000-8063`, `18000-18063`, MULTIHOST
  §4): that is a per-host limit. Per map, with `linkAllZones` a map passes 64 live cells only at the cost of refused
  links (§6a.3); turn the map's `onDemandLinks` on and a cell takes only the links of its band neighbours and of the
  cells it has business with.
- **Several hosts** (MULTIHOST §6b, DESIGN 0.90): every host that serves a Dynamic map's base cells runs a map-spawn
  server, with those bases in `servedCells` (never a child's id); a host that starts its cells by hand can serve Static
  cells only. Every spawner tells the central what it serves when it registers (an empty list: every cell no other
  spawner lists), and a family's children, restarts, kills and headroom go to the spawner that listed its base and
  **never to another host**: while that spawner is away (its host down, or restarting), a split of the family is refused
  (`no registered map-spawn server serves C`, `the map-spawn server that serves C ... is not registered`) and a restart
  fails and is retried, up to the supervisor's limit. A family does not move because the kill check, the fence and the
  headroom rule are keyed on its host's clock and load (MULTIHOST §6b). Cells on different hosts link, ghost, hand off
  and place buildings as on one; keep the hosts' clocks within 2 s (on-demand links compare a restarted neighbour's
  start with their own clock). Each cell of a spawner reads the database manager's address from
  `./Config/serverConfig.json` of the spawner's working directory, not from the spawner's command line.
- **The production shape** is the split-process one: the central, login and database manager in one process, a
  spawner per host (MULTIHOST §6b, §9). The central's process can then be restarted alone, with cells on every host
  (§6a.9).

### 6a.5 Splitting and merging by hand

An operation is one command line handed to the central: `MeshCentralNetworkManager.SubmitLayoutCommand(line, out
error)` from your own admin tool (it returns false with the reason, also logged `Layout op refused: <reason>`), and the
central raises `LayoutOpLine` with every `Split …`, `Merge …` and `Layout op refused: …` line for a tool to show. The
test rig feeds the same lines from a file (§7).

- `<seq> split <cell> <x|z> <at> <+|-> <childId>`: cut `<cell>` at world x (or z) = `<at>` and give the `+` (or `-`)
  side to a new cell `<childId>` (e.g. `7 split C x 0 + C.1`; the policy names children `<base>.<n>`). Refused while
  another operation runs anywhere (one at a time world-wide), inside the family's cooldown (`splitCooldownSeconds`,
  30 s), past `maxCellsPerBase`, when a side would be under the minimum cell, or when an id is unsafe (no `_ : | ; @ , =`,
  no whitespace, at most 50 characters). The central adds the channel, has the spawner start the child, **pre-warms**
  it (its band neighbours linked and streaming for `prewarmSeconds`), **hands over** the half (the parent drains its
  players at `drainRatePerSecond`, monsters, statics, drops and buildings faster) and **settles** (`Split <op>: done`).
  A failure before the hand-over aborts (the child retired); after it the split freezes rather than guess (`unfreeze`
  below resolves it).
- `<seq> merge <a> <b>`: join two cells of one family that share a full edge. The base survives when it is one of them,
  else the one with more players. The retiring cell drains everything to the survivor, quits through the kit's quit
  path once it holds nothing, and leaves the layout; a family back to its asset cut drops out of the layout altogether.
- `<seq> kill <cell>`: have the cell's spawner kill its process (an escape for a hung cell; it is then restarted like
  any dead cell).
- `<seq> unfreeze <base>`: the escape from a freeze (§6a.9), named by the family's base cell. For a family frozen after a
  central restart it declares the children that never said hello dead, fences every process of them started before the
  central, collapses each one that owns a rectangle into the base (or a live sibling sharing a full edge) and retires one
  that owns nothing; the live cells keep what they own. For an operation frozen after its hand-over (`Split <op>: frozen
  …`, `Merge <op>: frozen …`) it does what a restarted central would: the hand-over re-sent if its target applied it, the
  operation aborted if not, else resumed at the gate it froze at. Refused when nothing of that family is frozen (the
  refusal names what holds the world-wide slot), or while a missing child's process is registered with the cluster.
  **Before unfreezing a frozen family, make sure the missing child's process is gone on its host** (or kill it there):
  a process the central cannot see does not fence itself after a central outage, and would keep serving its rectangle
  beside the cell that takes it over.
- Status lines: `Split <op>: …`, `Merge <op>: …`, `Layout op refused: …`, `Supervisor: …`, `Policy: …`, `Unfreeze <base>: …`.

### 6a.6 The automatic policy

With `autoSplit` and/or `autoMerge` on, the central reads every cell's load report (every 5 s: the work probe's
utilisation, what it owns and holds, an occupancy grid) once a second:

- A cell at `splitUtil` for `splitDwellSeconds` (or saturated for `fastPathSeconds`) is planned: every cut on a 20 m step
  that leaves both sides the minimum is priced with the cost model, and the best is taken only when its busier side is
  predicted at most `splitBenefitRatio` of the cell's utilisation now (`Policy: split of <cell> planned: …`). Otherwise
  `Policy: split of <cell> refused: best peak … over 0.75 x util …`, once a minute (also a `split-refused` note in the
  cell's log). A split is deferred while the spawner's host would pass `hostHeadroomShare` of its cores.
- Two cells of a family predicted under the merge threshold together for `mergeDwellSeconds` merge (`Policy: merge of
  <a> <b> planned: …`), two children of a family as well as a child and its base.
- Every operation it made is measured two load windows after its done (`Policy: split <op> measured: predicted peak …,
  measured …, relief … %`). A family whose split relieved under `flapReliefShare`, or that splits within
  `flapWindowSeconds` of a merge, has its dwells and cooldown doubled, up to `flapMaxMultiplier`.
- The cost model's weights are the rig's measured fits (DESIGN 0.71, 0.73); the planner uses their ratios only, so it
  carries to another machine. Refit them for your game's entities if its puppets cost differently.

### 6a.7 Supervision and fencing

- A dynamic cell (or a cut family's base with sleeping on) gone for `childDownGraceSeconds` is restarted with the same id
  and rectangle; after `childMaxRestarts` failed restarts a child collapses into its base (or, sharing no full edge with
  it, into a sibling that does; one with neither is left down). A registered cell that sends no
  load report for `childHungSeconds` is killed by its spawner and then restarted; so is a merge's retiring cell still
  there after `retireKillAfterSeconds`. Lines start `Supervisor:`.
- An operation whose member dies before the hand-over aborts at once; one that dies after it pauses and resumes when the
  member is back.
- **Fencing:** a cell of a Dynamic map cut off from the cluster for `childFenceSeconds`, while a neighbour that never lost
  it still reports itself registered, saves its players (within `fenceSaveSeconds`), kicks them (they log in again,
  routed by position) and exits, so two processes never own one rectangle. With the central itself down nobody has such a
  witness and nobody fences.

### 6a.8 Buildings

Every cell of a family keeps its buildings under the base cell's channel (one core edit, CE-10), so nothing is lost when
a child merges back. A building tree follows its root's ground: at a split's hand-over and at a merge the holder hands
each tree to the new owner (`buildingTransfersInFlight` at a time, chests closed for `buildingStorageCloseSeconds`
first), a placement lands on the cell that owns its position, and both drain gates wait for the trees. Lines:
`Buildings loaded on <cell>: …`, `Building transfer <root> zone <from>-><to>: …`.

### 6a.9 A central restart

The cells keep running while the central is down. A restarted central waits (up to `recoveryCeilingSeconds`) for every
registered cell and every dynamic id its store or the cells name to say hello, adopts the newest consistent layout among
them (`Coordinator recovery: map <m> adopted layout <pair> from <the store|cell <c>> with <n> entries (<ids>)`), gives the
children their channels back, and answers everyone with it; logins wait up to `recoveryWaitSeconds` for this. The
operation the old central was running is aborted, re-sent or resumed at its gate (`<Kind> <op>: resumed at <step> after
a central restart`); the policy's cooldowns start fresh. A dynamic id that never says hello by the ceiling freezes **its
family** only (`Coordinator recovery: family <base> of map <m> frozen: no hello from <ids> after <s> s`): no operation,
supervision or policy on it, until that cell says hello or the operator sends `unfreeze <base>` (§6a.5): a child that
died while the central was down never comes back by itself, since the kit's spawner does not restart a child it was
asked to start. While the old central's operation waits for such a family, it holds the world-wide operation slot (`<op>:
holds the world-wide operation slot until family <base> is unfrozen`): no split, merge or collapse runs anywhere until
then. The cells' saves
fail while the database manager is down, so host it apart from the central where you can (a central process that hosts
it costs every cell up to about 40 s of failed saves).

### 6a.10 In the logs

On every cell: `LayoutHello sent`, `Layout <incarnation>/<version> applied: <cell> owns …`, `Layout gate open on …`,
`Coordinator: <code>: …` (a note the central sent the cell), `Drain on <cell>: …`, `Building channel on <cell>: …`. On
the central: the operation, policy, supervisor and recovery lines above and a load summary every `loadSummarySeconds`.

### 6a.11 Settings reference

Every field of the registry's `dynamicSettings`, read only when a map is Dynamic:

| Field | Default | Meaning |
|---|---|---|
| `bandWidth` | 80 m | See §6a.2. Rig override `MESH_BAND`. |
| `defaultMinCellSize`, `defaultMaxCellsPerBase` | 100 m, 4 | For maps that set no `minCellSize`/`maxCellsPerBase`. |
| `layoutGateWarnSeconds` | 10 s | Between warnings while a cell waits for its layout answer. |
| `recoveryCeilingSeconds` | 60 s | See §6a.9. |
| `coordinatorStorePath` | empty | See §6a.2. Rig override `MESH_COORDINATOR_STORE`. |
| `handoffAckGuardSeconds` | 20 s | A handoff's ack wait (as the map servers'); added to a split's drain timeout. |
| `drainRatePerSecond`, `drainEntityRatePerSecond` | 5, 20 | Character handoffs, and monster, static and drop handoffs, a draining cell starts per second. |
| `maxConcurrentHandoffsPerDestination` | 20 | Handoffs in flight toward one cell while draining. |
| `prewarmSeconds`, `prewarmSpawnBudgetShare` | 2 s, 0.25 | How long a new cell's band neighbours must stream to it before it takes its half; the share of a frame for queued puppet spawns. |
| `splitStepTimeoutSeconds` | 60 s | A silent participant aborts a split before its hand-over and freezes it after. |
| `splitCooldownSeconds` | 30 s | After any operation on a family before a command may cut it again. |
| `drainMonsterAgeSeconds` | 5 s | A monster the shrunk parent spawned in the given-away half this recently is handed off, not counted. |
| `retireMaxSeconds` | 90 s | A merged-away cell still draining after this is logged as straggling (players are never kicked). |
| `childDownGraceSeconds` | 20 s | See §6a.7; must exceed `childFenceSeconds + fenceSaveSeconds + 3`. |
| `childMaxRestarts`, `childRestartIntervalSeconds`, `childRestartStableSeconds` | 3, 10 s, 60 s | Failed restarts before a collapse; between attempts; up this long for a restart to count. |
| `childHungSeconds`, `childFenceSeconds`, `fenceSaveSeconds`, `retireKillAfterSeconds` | 30, 10, 5, 270 s | See §6a.7. |
| `splitUtil`, `splitDwellSeconds` | 0.8, 45 s | See §6a.6. Rig `MESH_SPLIT_UTIL`, `MESH_SPLIT_DWELL`. |
| `fastPathSaturatedShare`, `fastPathUtil`, `fastPathSeconds` | 0.2, 0.95, 10 s | A saturated cell is planned sooner. |
| `mergeUtil`, `mergeDwellSeconds` | 0.5, 300 s | The merge threshold is the smaller of `mergeUtil` and 0.6 x `splitUtil`. Rig `MESH_MERGE_UTIL`, `MESH_MERGE_AFTER`. |
| `opCooldownSeconds` | 120 s | Never under `splitCooldownSeconds`. Rig `MESH_OP_COOLDOWN`. |
| `splitBenefitRatio` | 0.75 | Rig `MESH_SPLIT_BENEFIT`. |
| `reportFreshSeconds`, `measureAfterSeconds` | 15 s, 10 s | An older load report is no evidence; the relief is read this long after done. |
| `flapWindowSeconds`, `flapReliefShare`, `flapMaxMultiplier` | 900 s, 0.1, 8 | See §6a.6; a relief at twice the share resets the multiplier. |
| `hostHeadroomShare` | 0.8 | See §6a.6. Rig `MESH_HOST_CORES` sets the reported cores. |
| `modelFloorPlayers`, `puppetPlayerWeight`, `ownedMonsterWeight`, `puppetMonsterWeight` | 9.1, 0.174, 0.126, 0.1 | The cost model in owned-player equivalents (DESIGN 0.71, 0.73). Rig `MESH_PLANNER_WEIGHTS`, `MESH_MONSTER_WEIGHT`. |
| `buildingTransfersInFlight`, `buildingStorageCloseSeconds` | 2, 5 s | See §6a.8. |

### 6a.12 Known limits

- **One operation at a time, world-wide**: the drain is paced to the database's rate (DESIGN 0.75), not the hosts'.
- **What a cut relieves**: a crowd narrower than twice the band, or on a base cell's edge, gets little; the kinematic cost
  model accepts a crowd narrower than the band that only a steered fit would refuse; the weights are one machine's fits.
- **Scale**: with `linkAllZones` every cell of a map links to every other, and a link server takes 64 connections, so
  about 64 live cells is that map's ceiling; turn the map's `onDemandLinks` on to lift it (§6a.2). A cell streams to at
  most 32 band neighbours, and one host's port range (64 map ports and 64 link ports on the rig) caps what one host runs.
- **On-demand links**: a party share for a member that is neither on the killer's cell nor a ghost there goes through
  the central (lost while the central is down, beyond the band neighbours); it reaches the cells of the killer's map
  only, where `linkAllZones` also reached other maps' cells near the killer. A reply to the first request from a cell
  that had no link to us can be lost while the link back comes up (the request's own guard retries it). A home notice
  (a lent monster's or static's new owner, its death) waits up to 60 s for its link.
- **Hosts**: a family runs on its base's host and never moves, so a host that is down leaves its families unsplittable
  and their dead cells unrestarted until it is back (DESIGN 0.90); a host without a spawner cannot serve a Dynamic map's
  cells; sleeping cells have not been run across two hosts.
- **Settings for a run**: `MESH_BAND` changes the band the map servers and the layout rules use, but the central's
  policy plans with the asset's `bandWidth` (and its minimum cell clamps to it); a base just over twice the minimum
  cell splits by command but not by the policy (its cuts fall on the report's bucket).
- **During an operation**: a new child's first window has one 150-200 ms frame (the pre-warm waits it out); a monster
  idling on the cut is handed back and forth every 5-10 s, as on any border; after a drain timeout the parent watches its
  old half until it is empty, then a settle shrinks it; between a merged cell's exit and the next layout its neighbours
  retry its link (log noise); a hung quit holds the world-wide operation until `retireKillAfterSeconds`.
- **Buildings**: scene-placed buildings and chests stay per-channel copies (their ids embed the channel), so a child's
  scene rows accumulate under the base channel and a chest's contents depend on the serving cell; `buildLimit` is counted
  per process; a building placed while its owner restarts has two owners for about a second; a drain counts a tree as one
  entity though its release takes a few seconds; the kit's own race between a handoff and an item move in flight is
  unchanged; an item moved into a chest on a cell where nobody has opened it trips the kit's `NotifyStorageItemsUpdated`
  (the kit's UI always opens first); upstream patch 0008 (CE-10) is in `develop` since 2026-10-20 ([open-mmorpg/OpenMMORPG#8](https://github.com/open-mmorpg/OpenMMORPG/pull/8)).
- **Unfreeze**: it asserts that a frozen family's missing children are gone; one still running out of the central's
  sight keeps its rectangle beside the survivor until its next hello is fenced. A central restart before the collapse's
  hand-over freezes the family again (unfreeze again). A dead child whose rectangle makes one with no live cell's is left
  down, or waits for the family's other collapses; a live retiring cell of a merge dropped at the unfreeze stays
  Draining (kill it). A resumed operation whose cause persists freezes again.
- **A central restart**: a family whose child never says hello stays frozen until `unfreeze` (§6a.5); a store lost
  together with a partitioned cell can leave that cell holding a newer layout nobody adopts (it is answered with the
  current one); the policy's multipliers and measurements and the supervisor's restart counts do not survive, and a
  resumed operation is not measured; a staged commit held by its target alone lasts the whole outage; a frozen operation
  is dropped. (The rig's first-login timeouts on its split-process topology, listed here until 0.84, were not a central
  restart limit: the test client sent its login before the login server had accepted its peer, which the kit drops
  silently. A client of your own that logs in by code should wait for the transport's `IsClientStarted`, not the kit's
  `IsClientConnected`, before its first request.)

## 7. Verifying

- **Harness:** `python Tools/ServerMeshing/regress.py` runs 34 scenarios: the eleven of the mesh itself (pet, party,
  crash, harvest, build, seam, seamcold, sleep, pvp, wake, dynidle; the first eight by default), the twenty-one of
  dynamic cells below and the two of on-demand links (`linkcap`, `partyfar`), on the rig and grades the logs of every cell that
  logged, whatever its id; `--env KEY=VALUE` passes rig toggles, `--remote-cells E` serves a cell from a second machine
  (started by hand for a Static scenario; by the second machine's own map-spawn server, its children too, for every
  Dynamic one and with `--remote-spawner`) and `--impair` degrades the link to it ([MULTIHOST.md](MULTIHOST.md) §8b,
  §8e, §8g). It refuses to run on a player build
  that does not match the working tree (the rig build records a SHA-1 of every player-side script and asset in
  `Build/Server/rigbuild-manifest.txt`, and any change, addition or removal since then makes it stale), on an editor
  with compile errors, and it fails a scenario when a cell that should have run logged nothing or when a rig process
  outlives Play. Only processes of `Build/Server/kit-ce-project.exe` are ever killed.
- **Driving the editor from the harness:** `--refresh` makes the open editor import and compile changed scripts (it
  only does so when it has focus), `--unit` runs the add-on's EditMode tests (`Assets/ServerMeshing/Tests/Editor`)
  there (it fails when none ran, and refuses while a scene has unsaved changes), `--build` rebuilds the player into
  `Build/Server` (`Server Meshing ▸ Test Rig ▸ Build Player` does the same by hand). All three go through
  `ServerMeshPlayRunner`'s request files in `Temp/`, so they work while the editor is in the background; an editor
  that does not answer (a modal dialog, say) ends the run with exit code 3. `python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"` tests the harness's own helpers and the
  provisioner.
- **Measuring cells:** `python Tools/ServerMeshing/abload.py probe` checks the work probe against a known burn on one
  idle cell; `abload.py ab` puts the same synthetic load on the test ground cut as one 600 m cell, two 300 m halves
  and the 3x3 grid, and prints how much the busiest cell gains from being cut, per repeat, with a go/no-go (DESIGN
  0.71). Each run starts only the layout's cells, with no client logged in, and counts only when every cell started
  once with the layout's rectangle, spawned its planned players, logged no exception and enough work windows (none
  Invalid); repeats run the layouts in rotated order, and a saturated cell is left out of the verdict. The rig toggles
  behind it: `MESH_LAYOUT_OVERRIDE` (re-cut a map for a run: `Map=C@minX,minZ,sizeX,sizeZ;E@…`), `MESH_LOAD_PLAN`
  (synthetic players per cell: `C:150,E:150`; every cell then draws the same world placement and keeps its own share,
  so a player present in two layouts stands at the same place in both), `MESH_LOAD_SEED` (that placement's seed),
  `MESH_LOAD_AREA` (crowd them into a square `x,z,r`), `MESH_NO_MONSTERS=1`, `MESH_MONSTER_AREAS=all` (also switch on
  the monster spawn areas the test maps save inactive, about 294 monsters, each on the cell that owns it; the
  scenarios use the one area that is on; or a comma list of area names, e.g. `Spawn_Interior_C` for `split`, to
  switch on just those; `only:<names>` switches on those and off every other monster area, for an exact total),
  `MESH_FAULT_DIE_ON_PREWARM=<cell>` and `MESH_FAULT_DIE_ON_DRAIN=<cell>` (Slice 2d, one-shot like
  `MESH_FAULT_DIE_ON_NOTICE`: the cell dies on its split's e1 broadcast, or on its first incoming handoff after it owns
  its rectangle), `MESH_FAULT_DIE_ON_HELLO=<cell>:<n>` (Slice 5: the cell dies right after its n-th `LayoutHello` of the
  process, e.g. `C:2` for a base's hello to a restarted central), `MESH_EDITOR_SERVERS=0` (Slice 5: the editor's Play
  hosts no server, set by the harness for the split-process topology), `MESH_SPAWN_CELLS` (which cells the spawner starts), `MESH_BAND` (ghost band
  and visible range together), `MESH_PROBE_BURN_MS` (`[cell:]ms` of busy work per frame, or a comma list such as `C:14,E:7`, where an entry naming
  the cell wins over one naming none; `@<path>` reads the same text from a file at every load window), and, on a bare player with
  no rig at all, `MESH_PROBE_SELFTEST=0,5,10,24`.
- **Measuring a tick (Phase 0):** the Work ticks line is always on; these rig toggles sharpen it (all unset = today):
  `MESH_RATES=<fps>/<step>` sets the frame cap and physics step for every cell (`30/0.02` for a space server,
  `60/0.03` is the demo's own; `u/<step>` or `u<fps>/<step>` uncaps and budgets the line at the kit's cap or at
  `<fps>`), `MESH_UNCAPPED_CELLS=C,E` uncaps just those cells (budgeted at the MESH_RATES rate), `MESH_AOI_TIMING=1`
  swaps in a timed copy of the kit's interest manager, `MESH_THREAD_CPU=1` logs `Threads window …` every 5 s (CPU per
  thread class: main, LiteNetLib, jobs, unnamed, other, against the process total; under Unity's Mono, LiteNetLib's
  threads carry no name and are counted as unnamed), `MESH_PROBE_BURN_TICK_MS=[cell:]ms` burns that long in every
  kit tick (never together with `MESH_PROBE_BURN_MS`), and `MESH_WORK_BINS=1` adds a `Work bins …` line with every
  non-empty bin. Their lines: `Rates: fps … | tick … Hz | physics … s | … | probe budget … ms | uncapped cells …`,
  `AOI timing: installed …`, `Tick burn: … ms per tick on …`, `Thread CPU: sampling …`. The rig refuses to apply
  rates inside the editor. `Tools/ServerMeshing/p0tools.py` parses all of these lines.
- **Cheaper puppets (Phase 0):** `MESH_PUPPET_MOTION=kinematic` runs character puppets kinematic (see `puppetMotion`
  above) and logs, next to the Mesh stats line, `Puppets window … kinematic N steered N | fallback mounted … summoned …
  handler … grid … install … | snaps N | observed ticks N/s unobserved poses N/s | steer kinematic N mean … m p95 … m
  max … m | steer steered N mean … m p95 … m max … m` (the steer figures are the distance from the pose each puppet
  shows to its owner's arriving snapshot, kinematic and steered puppets apart; a kinematic pose is the snapshot
  extrapolated to now plus a decaying offset, so it glides onto each new snapshot without trailing, and a watched
  one is posed at every kit tick, right before the tick sends movement).
  `MESH_LOAD_MOTION=steered|kinematic|inert` chooses how synthetic players move (kinematic: a transform walk with the kit's
  movement component switched off once it has started; inert: spawned and standing), `MESH_LOAD_FIGHT=0` stops them
  attacking, and `MESH_RIG_SERVER_ANIMATION=0|1` sets the kit's server-side animation on a cell.
- **The state sender (Phase 0):** `MESH_SENDER=measure|on` (default off: no component is added, no line appears and
  the kit sends movement as today) adds the add-on's own entity-state sender to a map server. `measure` writes and
  encodes every record after the kit's own send and only counts it, so the wire bytes do not change; `on` sends
  movement state in place of the kit's loop for the wrapped kit handlers (`BuiltInEntityMovementFunctions3D`,
  `NavMeshEntityMovement`) and for kinematic puppets, at a rate per observer chosen by distance. It stays inactive,
  with one warning, while the kit's grid path is on. Its keys: `MESH_SENDER_SOURCES=kit|direct|kit,direct` (default
  both), `MESH_SENDER_TIERS=near/mid/far[/owner]` (each 0, 1, 2, 5, 10 or 20 Hz; the owner tier defaults to 20, unset
  is 20/20/20/20), `MESH_SENDER_R0` (the tier radius in metres: near within half of it, mid within it; default the
  visible range), `MESH_SENDER_COMPACT=off|far`, `MESH_SENDER_SPREAD=0|1` (every second source's routine records go
  out on the next frame), `MESH_SENDER_XZ=0|1` (tier distances on the ground plane), `MESH_SENDER_CLASSIFY_BUILDS`
  (builds between re-classifications of a pair, default 4) and `MESH_SENDER_PACKET_BYTES=1020|510|255`. It logs
  `State sender: on | sources … | tiers … r0 … m … | compact … spread … classify … packet … B build … Hz` once
  configured, and with the Mesh stats lines `Sender window … s: on sources N kit N direct N pairs N clients N | builds
  … | writes … | records u N r N deferred N | packets u N r N … | stuckReliable N withheldOwner N | hz 20:N 10:N 5:N
  2:N 1:N 0:N withheld N clamped N | out … kB/s … pkt/s per client … | ms/s build … fanout … send … total … |
  us/packet … | build p95 … max … ms | gc0 N` (`measure` in place of `on`); any `State sender error:` line is a fault.
  With `MESH_METER_SENDS` on, a Send window that counted EntityState messages gains `| records ro N u N other N
  unread N`, the records they carried by delivery. In measure mode the extra write after the kit's sets the 3D
  mover's server-local movement state one frame earlier than the kit's next write would; no wire bytes change.
  `MESH_METER_RECORD_SIZES=1` (default 0; read only where the meter or the sender runs, so with both off it does
  nothing, and junk logs one warning and counts nothing) adds the record sizes: the Send window gains `| sizes n N min
  N max N top L:N L:N L:N payload … framed … B over N unread N` after the records group (the payload length of every
  EntityState record sent, the three most frequent lengths, and the mean payload and framed size per record, framed
  being the packed object id, the 4-byte length and the payload), and the Sender window gains `| payload full n N min
  N max N compact n N min N max N` after `gc0 N` (the Full and Compact payloads the sender wrote). A direct source
  that is also its object's kit movement handler implements `IMeshSenderOwnedSource`: the sender calls its
  `OnSenderReleased` when it lets go of the source unasked (server stop, object destroyed, dropped by the fan-out),
  never on an explicit `Unregister`, and the source then goes back to being written by the kit; `OnSenderReleased`
  clears only the source's own state and never calls the registry. `MeshKinematicCharacterBody` is one.
  `MESH_CE12_CHECK=1` makes each serving cell spawn one hidden, server-owned character, classify how its kit handler
  sends one-shot movement flags, and log one `CE-12 check: cell C shape=pass first=1 second=1 next=1 consume=yes
  after=0 reliable=1/1/1/0 handler=…` line (`pass` is the shape a kit with patch 0006 gives).
  `Tools/ServerMeshing/p0sender.py` parses the Sender window and CE-12 check lines.
- **Ship stand-ins (Phase 0):** a rig-only stand-in for the space game's ship (a vehicle with its own 54-byte movement
  record, AI pilot, engine and guns) to measure ship load on a cell. Default off: with no ship key no ship component is
  added, nothing is registered with the kit and no line appears. The keys: `MESH_LOAD_SHIPS=<cell>:<n>:<ai|mirror|
  parked>[,…]` (load ships per cell, n 1 to 2000, one group per cell and authority; `ai` ships patrol, fight and
  respawn by teleport 10 s after being destroyed, `mirror` ships fly a seeded flight path fed in-process through
  their own upload reader at 20 Hz, `parked` ships sit 20 m plus 3 m scaled above the path plane),
  `MESH_LOAD_SHIP_PILOTS=0|1` (default 0; 1 seats a server pilot character in each load ship, never in a phantom
  ship), `MESH_SHIP_WIRE=legacy54|header56|compact28` (default legacy54: the space game's 54-byte record; header56
  is the space game's two-byte header before it, byte 0 the wire mode in bits 0-1 and the flight state in bits 2-4
  (the rig always writes mode 0, flight state 0) and byte 1 the epoch (0, as in the game; respawns leave it), with
  authority no longer on the wire; compact28 is the space game's mode 3, 28 bytes: header, float32 position,
  smallest-three rotation, half-float velocity, int8 angular velocity at 4/127 rad/s and a state byte, for far
  observers sent by the state sender with `MESH_SENDER_COMPACT=far`, never while a teleport is unacknowledged, whose
  Full record is header56), `MESH_SHIP_SCALE` (0.001 to
  10, default 1: every distance and speed of the ship times this factor; rates, timers, torques and bytes never scale,
  and the runner scales `MESH_SENDER_R0` itself), `MESH_SHIP_PROJECTILES=instantiate|pooled` (default instantiate: the
  space game's server bolts, each instantiated and casting `RaycastAll` every frame; pooled reuses bolts with
  `RaycastNonAlloc`), `MESH_SHIP_MIRROR_VELOCITY=real|zero` (default real: a player ship's server mirror sends the
  last upload's velocities, as the space game's does since 9b1d69a1; zero sends zeros), `MESH_SHIP_SELF_ACK=0|1`
  (default 0: an unowned ship stays reliable after a server teleport until an owner acknowledges it, as in the space
  game, so a respawned AI ship stays reliable and never compact; 1: the rig acknowledges its own teleport after two
  logic ticks), `MESH_SHIP_AI_PREFAB=enemy|wave` (default enemy: AI load ships take Ship_Enemy's numbers, force
  300, angular drag 8, torques 8/8/10, hull 1,500, terminal 94 m/s at scale 1; wave takes Ship_AI's, which are
  Ship.prefab's, the numbers every other ship uses), `MESH_RIG_SHIPS=1` (the runner passes the bot
  argument), `MESH_PUPPET_FEEDER=<ship|character>:<N>@<Hz>` (N untracked in-process puppets, 1 to 2000, fed at 1 to
  60 Hz) and `MESH_SYNC_LOAD=dirty:<D>,rpc:<R>` (D sync-field writes per kit tick, 0 to 10,000, and R RPCs per second,
  0 to 100,000, over the cell's load ships, which it needs). `MESH_PHANTOM=<route>:<n>:ship:<cell>` binds ship
  phantoms that upload along their own flight paths (kind ship takes no `MESH_PHANTOM_VIEW`, `_AREA`, `_PAYLOAD`,
  `_STAND`, `_FIGHT` or `_STEPS`; the upload length follows `MESH_SHIP_WIRE`). With a ship key set,
  `MESH_LOAD_SEED` (default 12345) seeds every flight path and AI pilot, and `MESH_LOAD_AREA=x,z,r` also confines load
  ships (AI and parked spawn points, mirror path centres) to that square inside the cell; phantom ships always use
  the whole cell, and a junk seed or area is then an error. Bots get `-meshRigShips <wire>:<seed>:<scale>` (the seed
  and scale texts verbatim, so server and bot parse the same numbers) and check every ship record against its flight
  path; a sink started with `-meshPhantomSinkSizes 1` (`remoteb.py sink-start --sink-sizes`) adds the sizes group to
  its Sink window. The lines: `Ships: cell C ai 50 mirror 0 parked 0 pilots 0 | wire header56 scale 0.03 projectiles
  instantiate mirrorVelocity real selfAck 0 aiPrefab enemy | area none | prefab hash N` once and, beside Mesh stats,
  `Ships window 5.00 s: cell C ships N ai N mirror N parked N phantom N pilots N seated N outside N notInSlot N
  pending N | speed p50 … max … patrol n N p50 … m/s | scans N casts N shots N bolts N hits N respawns N | shield
  writes N | rpcSends N | uploads N stale N bad N | ms/s pilot … engine … combat … bolts … mirror … | mirror age n N
  p50 … ms frames K | gc0 N` (pending: ships with an unacknowledged teleport at the window's end; patrol n: the
  patrol speeds sampled, p50 `none` when n is 0; mirror age, rig only: a mirror record's write time minus its last
  feed's time and K the frames since that feed, both `none` when n is 0); `Feeder: ship 200 @ 20 Hz on cell C` and
  `Feeder window
  5.00 s: ship N observed N | snapshots N | ms/s encode … decode … accept … pose … total … | us/snapshot … | gc0 N`;
  `Sync load: dirty 100/tick rpc 200/s over 50 ships` and `Sync load window 5.00 s: ships N | writes N rpc N rpcSends
  N | ms/s write … rpc … | alloc … B/rpc gc0 N`; on bots `Bot ships 5.00 s: seen N records N pathed N parked N
  unpathed N | path n N p50 … p95 … p99 … max … m lag p50 … ms | parked max … m | unknownPath N badLength N`. Any
  `Ships error:`, `Feeder error:`, `Sync load error:` or `Bot ships error:` line is a fault. The ship prefab is made in
  the editor by `Server Meshing ▸ Test Rig ▸ Build Ship Prefab` (or `-executeMethod
  MultiplayerARPG.MMO.Mesh.Editor.ServerMeshShipPrefabBuilder.BuildFromCommandLine`), which saves
  `TestMap/Prefabs/ServerMeshShip.prefab` with its own GUID as the asset id (refusing a clash with any other prefab)
  and `TestMap/Resources/ServerMeshShipPrefabRef.asset` pointing at it; the prefab itself stays out of every Resources
  folder, so no game database collects it, and a process registers it as a vehicle only under a ship key.
  `MESH_SHIP_PARK_SLEEP=0|1` (step 8a; unset or 0 is today: parked ships are dynamic bodies with zero force, which
  PhysX may put to sleep; 1 pins every parked ship to its parked pose each FixedUpdate, velocities zeroed, as the
  space game's landed ships, so none sleeps): with the key set either way the Ships window adds ` | parked sleeping S
  of P` before ` | gc0 N` (`Rigidbody.IsSleeping()` at the window's end) and the settings line adds ` parkSleep 0|1`
  after `aiPrefab`; unset, both lines are unchanged. `MESH_SHIP_JUMP_SECONDS` stays pending until stage 0c.
  `Tools/ServerMeshing/p0ships.py` parses the Ships, Feeder, Sync load and Bot ships lines.
- **Pre-warm bursts and placed monsters (Phase 0 step 8a):** default off; with neither key no component is added and
  no line appears. `MESH_PREWARM=<kind>:<N>@<t>[/<perFrame>]` makes one burst t seconds (at least 0) after the server
  can instantiate: `steered` or `kinematic` (N synthetic Player ghosts, 1 to 10,000, fed through the real neighbour
  path at 10 Hz in a 50 m disc at `MESH_LOAD_AREA`'s centre, else the cell's; the kind must match `MESH_PUPPET_MOTION`),
  `ship` (N stand-in ships posed as the puppet feeder's, fed at 20 Hz; a ship key), `range` (the default visible
  range goes to N metres, above 0 and at most 10,000; no perFrame) or `group` (N owned server characters, spawned
  out of view 10 s before, teleported into the burst disc); perFrame (1 to N) paces the spawns, absent all in one
  frame. Lines: `Prewarm: kind K n N at T s perFrame F on cell C` once (F 0: all at once) and `Prewarm burst: cell C
  kind K n N perFrame F frames S | spawn ms T per puppet P | after frames W over budget O budget B ms worst X ms |
  gc0 G` once per burst (T and P `n/a` for range and group; B `uncapped` without a frame cap; over budget counts the
  frames whose period exceeded 1.05 x the probe budget). `MESH_MONSTER_PLACE=<n>@<x>,<z>,<r>[:passive][;…]` places n
  monsters (the test map's first monster prefab, registered on every cell) at seeded points (`MESH_LOAD_SEED`) in each
  disc, on the cell that owns the disc's centre; a placed monster below half health is healed to full and one that is
  destroyed is placed again at its point after 10 s; `:passive` monsters never target anyone. The total is at most
  2,000, r above 0 and n at least 1, a disc must lie inside its owning cell, and every run that places monsters sets
  `MESH_NO_MONSTER_HANDOFF=1` (a placed monster has no spawn area to hand off); it works with `MESH_NO_MONSTERS=1`.
  Lines: `Monsters: placed N in <discs> on cell C` once and, per work window, `Monsters window 5.00 s: cell C placed N
  alive A awake W healed H respawned R | gc0 G` (alive: placed monsters that are not ghosts; awake: those with a
  subscriber). Any `Prewarm error:` or `Monsters error:` line is a fault. `Tools/ServerMeshing/p0stage0b.py` parses
  these lines (and the phantom `Phantoms: cell C port N …` and `Phantom step i/n: P a -> b` lines) and mirrors the
  three grammars; `python Tools/ServerMeshing/p0geom.py` computes the offline fight geometry (V per client in 3D and
  X/Z for A0 2 km and 3 km, ships within 1 km, pilots within 80 m, lock subscriptions) into `Logs/p0/geom/`.
- **On-demand links on the rig** (Slice 7): `MESH_LINKS=<map|all>:<OnDemand|Off>[,…]` sets maps' `onDemandLinks` for a
  run (as `MESH_CELL_MODE` sets modes), `MESH_LINK_CAP=<n>` every cell's link server capacity (64), `MESH_LINK_IDLE=<s>`
  the idle time (25 s floor). `linkcap` cuts the test map into a Static 4x4 of 150 m cells beside C2 with every map on
  demand and a cap of 10 (all-zones links would need 15 or 16 per cell): every cell links with each touching neighbour,
  refuses nobody and keeps its links near its neighbour count while the bot walks across four lines. `partyfar` is
  `party` with a third member standing far off on W: its share arrives as a remote member's, through the central with
  `MESH_LINKS`, by broadcast without. Every scenario run with `MESH_LINKS` checks that no cell refused a link and notes
  the links opened and closed on demand (`split` checks that E closed its link to C after the cut); every scenario run
  without it checks that no cell logged an on-demand line.
- **Dynamic cells on the rig** (what they do is §6a): `MESH_CELL_MODE=<map|all>:<Static|Dynamic>[,…]` sets maps' cell
  modes for a run without touching the asset (`all` first, a map's own entry wins; e.g. `all:Dynamic`, or
  `ServerMeshTestMap:Dynamic`), and `MESH_COORDINATOR_STORE` moves the central's store. `regress.py` deletes the store
  before every scenario, grades every scenario's cells for the layout rules (a Dynamic map's cells apply the central's
  layout once per process, before any player registers; a Static map's apply none; no cell splits, merges, retires or
  warns at the layout gate unless the scenario does), fails every scenario but the policy's on any `Policy:` line, and
  checks after each Play that the registry asset is unchanged on disk. **Commands:** while the editor is in Play, one
  line in `Temp/ServerMesh.LayoutCommand.txt` (§6a.5's grammar) goes to the central through the play runner, which
  mirrors every `Split`, `Merge`, `Layout op refused`, `Supervisor:` and `Policy:` line to
  `Temp/ServerMesh.LayoutCommandStatus.txt` under that seq (the automatic operations' under `auto` while no command is
  live); the harness clears both before each scenario. **Toggles:** `MESH_AUTO_OPS=split|merge|split,merge` (the policy
  on every Dynamic map of the run), `MESH_SPLIT_UTIL`, `MESH_SPLIT_DWELL`, `MESH_MERGE_UTIL`, `MESH_MERGE_AFTER`,
  `MESH_OP_COOLDOWN`, `MESH_SPLIT_BENEFIT`, `MESH_MONSTER_WEIGHT`,
  `MESH_PLANNER_WEIGHTS=<puppetPlayer>,<ownedMonster>,<puppetMonster>,<floor>`, `MESH_HOST_CORES`,
  `MESH_PROBE_BURN_MS=@<absolute path>` (the burn read from a file at every load window: `[cell:]ms`, empty = off), the
  faults above and `MESH_EDITOR_SERVERS=0`. **The split-process topology** (`--topology split`, the default for the
  `splitcentral` scenarios): the central, login and database manager as one build process and the spawner as another,
  both started from `Build/Server` with the project root as working directory and every port on the command line
  (`-spawnMaps "ServerMeshTestMap|ServerMeshTestMap2"`: one pipe-separated token), the editor's Play a client only
  (`ServerMeshEditorRole` honours `MESH_EDITOR_SERVERS=0`), the command and status files served by the central process
  (`ServerMeshCentralCommandWatcher`, `MESH_RIG_COMMAND_DIR`), and the central killed and restarted alone (its kit log
  `Logs/Log_Database_Central_Login_<day>.<sev>.log`). **Scenarios** (all but `autodefer` run with `--remote-cells`, in
  spawner mode, since DESIGN 0.90; every one skipped with `--remote-persist`): `dynidle` (the test map Dynamic, a walk from C
  to E, handed over in place); `split` (C cut at
  x = 0: eight standing bots, a harvest-and-pickup bot and 40 synthetic players, `MESH_MONSTER_AREAS=Spawn_Interior_C`)
  and `splitc2` (E cut at x = 200, the child touching C2 across the seam); `merge` and `mergec2` (the same, then merged
  back: the bots migrate back, the child drains, quits and stays gone); `splitcrash_prewarm` (the child dies at e1: the
  split aborts, the child stays gone), `splitcrash_drain` (it dies mid-drain: restarted, the split finishes) and
  `splitcrash_base` (C killed with the child live: C returns without R, the monster totals return to the area's 5, C's
  players re-log in); `autosplit` (a burn makes C hot, the policy splits it, measures the relief and merges C.1 back once
  the bots are gone), `autobenefit` (the 600 m Coarse cut, 200 synthetic players inside C's inner 200 m square, no
  client: the measured relief within 15 % of the prediction; the test map's outer ground has navmesh holes that snap
  steered synthetics) and `autorefuse` (a 60 m crowd on the centre with steered puppets and the steered fit: refused once
  a minute, nothing splits); `splitbuild` (a wall, a chest and a door in the child's half, scrolls moved in and out
  across the split and the merge, the rows all under C, C killed and loading exactly the tree; the tester mode
  `buildtree`, test content `TestStorage-CE` and `TestDoor-CE`); `splitcentral` (the split, the central killed and
  restarted: the store adopted, every hello answered, a late login routed to C.1, nobody kicked), `splitcentral_merge`
  (killed the moment the merge's e2 goes out: the merge resumes and finishes), `splitcentral_base` (C dies on its hello
  to the restarted central, `MESH_FAULT_DIE_ON_HELLO=C:2`, and comes back without R) and `splitcentral_unfreeze` (C.1
  killed while the central is down, the family frozen at the restart's ceiling, `unfreeze C` collapsing C.1 into C, a
  late login into R routed to C; every bot in the kept half). DESIGN 0.88 closed the paths only EditMode tests had run:
  `mergechildren` (C split at x = 0, then C.1 at z = 0 into C.2, the two children sharing a full edge; `merge C.1 C.2`
  keeps the child with more owned players, C.2 with three bots against C.1's two, and C.1's bots migrate once more,
  C.1 drains and stays gone, the store keeps C and C.2), `collapsesibling` (the same family with C.2 empty; C.2 killed
  while its restarts die on their first hello, `MESH_FAULT_DIE_ON_HELLO=C.2:1` behind a fault marker the harness turns
  into a directory: three failed restarts, then the collapse into its sibling C.1, since C shares no full edge with it),
  `autoqueue` (C and E hot at once with the burn list `C:14,E:14`: one is split, the other waits for the one op slot and
  is planned only after that split's done, never refused), `autodefer` (`MESH_HOST_CORES` set so the host's headroom
  lies between C hot alone and C with the eight other cells burning 24 ms: C is deferred until their burn is removed,
  then split), and `autosplit_links` and `splitcentral_links` (`autosplit` and `splitcentral` with
  `MESH_LINKS=all:OnDemand`). The headless bots log in again
  after a lost session (`Session lost …; re-login <n>`; RESULT ` relogins=<n>`).
- **The play runner and the environment:** every Play request carries an id that the status file echoes as
  `playSeq=` while that Play runs, so the harness never mistakes a Play an earlier, abandoned request started late for
  its own; a stop that arrives while Play is starting is carried out once it has started. The environment variables a
  request sets are put back as they were when Play ends, so a manual Play afterwards does not inherit a run's load.
- **Capacity:** `python Tools/ServerMeshing/loadtest.py --steps 100,200,300` puts synthetic players on one cell and
  prints work per frame, CPU, memory and per-client send rate (DESIGN 0.53 has the reference table).
- **In the logs:** `Own cell from channel id: …` (the cell resolved its rectangle), `Neighbour zone X linked`,
  `Handoff START / SENT / ACK`, `Ghost spawned …`, `Waking cell … / … told to sleep`, and `Mesh stats`; on a Dynamic
  map also `LayoutHello sent`, `Layout <incarnation>/<version> applied: <cell> owns …` and `Layout gate open on …`,
  and any `Coordinator: <code>: …` note the central sent that cell.

## 8. Known behaviours and limits

- **Frames are per map.** A 10 km map keeps one scene at its own origin (the kit's quantized movement grid is off by
  default; float precision at 5 km is 0.5 mm). Only a seam between maps changes frames.
- **A seam pre-load** moves the neighbour scene's roots; its light and reflection probes and static batching stay at
  the authored origin until the swap, so lighting in the pre-loaded strip can differ briefly.
- **Crowds** are bounded by one process and by the kit's own AOI send cost (0.29 kB/s per visible entity per client),
  not by the mesh: 500 people in one place is a kit AOI/update-rate question.
- **Warp portals or spawn points exactly on a login position** do what they do in the kit: a character that stands up
  inside a portal trigger is warped. Not a mesh issue, but easy to trip over on a test map.
- **Dynamic cells** have their own list of limits: §6a.12.
- **One shared world frame per registry**: two maps meant to touch must be placed by their world origins; the
  registry does not derive placement from content.

## 9. Troubleshooting

| Symptom | Look at |
|---|---|
| A cell logs `No layout for map` and runs single-server | The map has no layout in the registry, or the registry is not assigned on `MeshMapNetworkManager`. |
| `No map server registered for zone peer key` | The neighbour is not up (asleep, crashed, or its channel id does not match a cell id). With sleeping on, the central logs `Waking cell …`. |
| Players see nobody across the line | Band width, `enableBorderGhosting`, or the link port (map port + `meshLinkPortOffset`) blocked. |
| Character selection fails for a cell | The central must list the cell as a channel: use `MeshCentralNetworkManager`. |
| Two servers both spawn a monster area | `ownSpawnAreasByZone` off, or the layout does not match the scene. |
| Puppets lag or jitter | `Mesh stats … ghostSteer` — mean above ~0.3 m: check `ghostLeadSeconds` and the link traffic. |
| A login right after a central restart (split-process topology) waits 30 s, then the client sits on the map server with no character | Before 0.87: the cell's database reconnect sat out a 35 s drain after a failed attempt (`Database connection lost with N request(s) in flight`), so the enter-game never got its character, and the kit's client threw on the unanswered request instead of disconnecting. Now the reconnect times those requests out at once (and, since 0.89, sends the stranded reads again) and the client takes an unanswered enter-game as a refusal (`Enter-game Timeout without an answer from the map server`); select the character again. |
| Players handed over during a central restart are kicked (`Session lost (... UI_ERROR_KICKED_FROM_SERVER)`), or a merge's drain never empties the retiring cell | Before 0.89: the arrival's access-token check or relates data was on its way when the database connection dropped, the reconnect answered it with a timeout, and the kit refused or kicked. Look for `keeping N to send again`, `Arrival ... asking again` and `Handoff ABORTED with its client gone`; a `kicked as the kit does` line names a failure that was not the drop's. |

## 10. Packaging

**Server Meshing ▸ Build Packages** writes two add-ons into the repository's `Addons/` tree, in the layout the Open
MMORPG Addon Manager expects (`Addons/<AddOn>/dist/<AddOn>.unitypackage` beside a generated `package.json`, with a
hand-written `README.md` and `screenshot.png`; `Addons/README.md` has the rebuild and submission notes):

- `ServerMeshing.unitypackage` — the add-on (`Assets/ServerMeshing/Mesh`, runtime and Editor) with this guide,
  DESIGN.md and MULTIHOST.md under `Documentation/`, and the top-level README. The spike notes, the upstream patch
  series and the raw capacity table stay in the repository. Import it into any project that has OpenMMORPG installed
  (`develop` at d077c5f or later: §3), then follow §4.
- `ServerMeshingTestRig.unitypackage` — `Assets/ServerMeshing/TestMap` (the two test maps, test items, auto client,
  launcher, play runner, load generator) plus a reference copy of the harnesses under `TestMap/Tools/`. It references
  the kit's demo (the enemy prefab, default weapon item and type), so it needs `BaseDemo` present, and it declares the
  add-on as a dependency. The harnesses assume the repository layout (`Tools/ServerMeshing/`, `Build/Server/`,
  `Config/`), so copy them there rather than running them from inside `Assets`.

From a script, `python Tools/ServerMeshing/regress.py --package` asks the open editor to build both (a package
request to the play runner, outside Play).

Each package carries an empty, extensionless file named after the add-on's guid; the Addon Manager uses it to spot an
installed add-on, so the guid never changes once published. Version, update date, guids and descriptions live in
`Mesh/Editor/ServerMeshPackageBuilder.cs`. Batch mode:

```
Unity.exe -batchmode -quit -projectPath <repo> -executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshPackageBuilder.BuildAll
```
