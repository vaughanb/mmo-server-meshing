# Server Meshing — Setup Guide

How to split a map into cells served by separate map servers, join maps into one world, run it, and check it.
The design and its measurements are in [DESIGN.md](DESIGN.md); this document is the operator's and developer's path.

**Status:** everything below is exercised by the regression harness on the test rig (ten cells over two maps: a 3×3
grid plus one cell across a seam; one machine, or two with one cell served from the other). Version 0.72.

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
- OpenMMORPG at master from 2026-09-15 or later: it carries the four small core changes the add-on relies on (an
  owner-change movement fix, frame-granular request waits, a virtual `IsWatched()` on the monster activity component and a
  protected pending-spawn dictionary on the map manager; DESIGN.md §17).
- A kit carrying upstream patch 0006 (OpenMMORPG `develop` bb9895f or later); the state sender calls its
  `IEntityMovementServerStateFlags`, and the add-on does not compile without it ([upstream/README.md](upstream/README.md)).
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

Below the layout, panel 2 sets each map's **cell mode**:

- **Static** (the default): the cut is the cut, exactly as before 0.72.
- **Dynamic**: the cut gives the map's *base cells*, which the central may later split into more cells while they are
  busy and merge back when they are quiet (the splitting itself is not in this version: a Dynamic map runs its base
  cells as they are). Every server of a Dynamic map says hello to the central once registered and hands nothing over,
  and routes no building, until the central answers with the layout; it logs `Layout gate open on …` when that
  happens and warns every `layoutGateWarnSeconds` while it waits. Choose the mode before starting the servers: it is
  read at start and the panel is disabled in Play.

The band is not part of the layout: a cell's server streams the strip inside its own edge.

The registry's own fields, for hand edits: `maps` (one layout per map), and per layout `mapId`, `worldOrigin` (where
the map's origin sits in the shared world frame), `gridOrigin`, `cellSize`, `columns`, `rows`, `cellIdFormat` (the id
pattern: `{map}` = the map id, `{x}`/`{y}` = column/row), the optional `cellRects` and `cellIds` above, `cellMode`,
and for Dynamic maps `minCellSize` (the narrowest a cut may leave a cell, never below the band; 0 = the registry's
default), `maxCellsPerBase` (0 = the default), `autoSplit` and `autoMerge` (both off until automatic splitting
exists). Registry-wide: `touchTolerance` (how close two rectangles must come to count as neighbours across a seam,
0.5 m) and `dynamicSettings`, read only when a map is Dynamic: `bandWidth` (the band every process computes band
neighbours with, and the narrowest cut; each map server logs an error if its own `ghostBandWidth` is wider),
`defaultMinCellSize` (100 m), `defaultMaxCellsPerBase` (4), `layoutGateWarnSeconds` (10), `recoveryCeilingSeconds`
(60: how long the central waits after a restart for the cells to say what layout they hold) and
`coordinatorStorePath` (where the central keeps the layout it has sent; empty = `Temp/ServerMesh.Coordinator.json`
under its working directory). Every mesh manager (`MeshMapNetworkManager`, `MeshLoginNetworkManager`,
`MeshCentralNetworkManager`, `MeshMapSpawnNetworkManager`) carries a `world` reference to this asset; panel 3 assigns
it.

### 4.3 Wire the prefabs

Panel 3: **Validate**. Every line with `!!` has a **Fix** button. The checks:

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
- **Several hosts:** give each host's `MeshMapSpawnNetworkManager` a `servedCells` list (or `MESH_SPAWN_CELLS=C,N`) so two
  machines never start the same cell; see [MULTIHOST.md](MULTIHOST.md).
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
| `linkAllZones` | on | Link to every cell of the map, not only neighbours (party shares and such reach any cell). |
| `linkSuspectTimeoutSeconds` | 2 s | A link a handoff came back over unclaimed is doubted (no handoff goes that way) until the neighbour is heard from; silent this long, it is reconnected — a restarted neighbour is reached at once instead of after the transport's timeout. |
| `handoffLinkSettleSeconds` | 0.5 s | No handoff across a link that has been up for less than this, so the destination has a snapshot or two of the character and promotes its puppet instead of spawning fresh (which re-summons pets — a visible swap for observers). |
| `meshLinkPortOffset` | 10000 | The server-to-server link listens on the map port plus this. |
| `expectedMaxSpeed`, `meshStatsIntervalSeconds` | 8 m/s, 10 s | Band sizing check at start-up; the `Mesh stats` log line cadence. |
| `databaseReconnectSeconds`, `databaseReconnectGraceSeconds`, `databaseDrainWaitSeconds` | 5 s, 15 s, 35 s | Re-open the **database** connection whenever this server has none: the kit opens it once at start-up and never again, so a map server that loses the database manager -- or starts before it exists -- runs on accepting players while every save fails. The grace period keeps the retry off the kit's own first attempt; the drain wait lets requests that were in flight expire before reconnecting clears their callbacks, then reconnects anyway and says how many it stranded. A process that *hosts* the database manager is left alone. |
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
any backend the kit supports works. The rig runs **MySQL**, which matters for two reasons: several map-server processes
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
  login 7500, cells from 8000 with their links 10000 above). Another kit server on the same machine holding those ports makes the editor's
  servers fail to bind with `Only one usage of each socket address`, and its login server then joins the *other*
  cluster: the scenarios fail with no cell logging at all. `regress.py` and `abload.py` check the ports first and
  refuse to start, naming the process that holds them; stop it for the run.

## 7. Verifying

- **Harness:** `python Tools/ServerMeshing/regress.py` runs the ten scenarios (pet, party, crash, harvest, build,
  seam, seamcold, sleep, pvp, wake; all but pvp and wake by default) on the rig and grades the logs of every cell that
  logged, whatever its id; `--env KEY=VALUE` passes rig toggles, `--remote-cells E` serves a cell from a second machine
  and `--impair` degrades the link to it ([MULTIHOST.md](MULTIHOST.md) §8b, §8e). It refuses to run on a player build
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
  scenarios use the one area that is on), `MESH_SPAWN_CELLS` (which cells the spawner starts), `MESH_BAND` (ghost band
  and visible range together), `MESH_PROBE_BURN_MS` (`[cell:]ms` of busy work per frame), and, on a bare player with
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
- **Dynamic cells on the rig:** `MESH_CELL_MODE=<map|all>:<Static|Dynamic>[,…]` sets maps' cell modes for a run
  without touching the asset (`all` first, a map's own entry wins; e.g. `all:Dynamic`, or
  `ServerMeshTestMap:Dynamic`), and `MESH_COORDINATOR_STORE` moves the central's store. `regress.py` deletes the store
  before every scenario, grades every scenario's cells for the layout rules (a Dynamic map's cells apply the central's
  layout once per process, before any player registers; a Static map's apply none; no cell splits, merges, retires or
  warns at the layout gate), and checks after each Play that the registry asset is unchanged on disk. The `dynidle`
  scenario runs the test map Dynamic with a walk from C to E; `--env MESH_CELL_MODE=all:Dynamic` runs any other
  scenario that way.
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
- **Dynamic split/merge of cells at runtime** is being built: a map can be set Dynamic, and its servers already take
  their layout from the central, but nothing splits or merges yet. Sleeping cells on a fine grid cover the sandbox
  case meanwhile.
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

## 10. Packaging

**Server Meshing ▸ Build Packages** writes two add-ons into the repository's `Addons/` tree, in the layout the Open
MMORPG Addon Manager expects (`Addons/<AddOn>/dist/<AddOn>.unitypackage` beside a generated `package.json`, with a
hand-written `README.md` and `screenshot.png`; `Addons/README.md` has the rebuild and submission notes):

- `ServerMeshing.unitypackage` — the add-on (`Assets/ServerMeshing/Mesh`, runtime and Editor) with this guide,
  DESIGN.md and MULTIHOST.md under `Documentation/`, and the top-level README. The spike notes, the upstream patch
  series and the raw capacity table stay in the repository. Import it into any project that has OpenMMORPG installed
  (master at or after the four core changes of 2026-09-15), then follow §4.
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
