# Server Meshing — Setup Guide

How to split a map into cells served by separate map servers, join maps into one world, run it, and check it.
The design and its measurements are in [DESIGN.md](DESIGN.md); this document is the operator's and developer's path.

**Status:** everything below is exercised by the regression harness on the test rig (five cells over two maps, one
shared machine). Version 0.59.

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

- **Explicit cells** (`cellRects`): list rectangles of any size instead of a grid — a dense area cut into 200 m cells
  next to 1 km wilderness cells. Rectangles must not overlap (the validator reports overlaps and duplicate ids).
- **Cell ids** (`cellIds`): explicit ids for a grid, row-major.

The band is not part of the layout: a cell's server streams the strip inside its own edge.

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
neighbour count; at runtime the lines are drawn as debug lines and the player's cell is highlighted.

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

**Ghosting**

| Field | Default | Meaning |
|---|---|---|
| `enableBorderGhosting` | on | Stream entities near a shared edge to the neighbour. |
| `ghostBandWidth` | 80 m | Width of the band. Keep it at or above the kit's visible range; DESIGN Q1 gives the rule with speeds. |
| `ghostSnapshotInterval` | 0.1 s | Snapshot rate for characters; `staticSnapshotInterval` (1 s) for drops, harvestables, buildings. |
| `ghostLeadSeconds` | 0.1 s | Puppets are steered ahead by the owner's velocity so they do not trail by a snapshot. |
| `ghostTeleportDistance`, `ghostStaleSeconds` | 4 m, 2 s | A puppet further off than this is teleported; one without snapshots is removed. |
| `ghostMonsters`, `ghostHarvestables`, `ghostBuildings`, `forwardGhostHits`, `handoffMonsters`, `ownSpawnAreasByZone` | on | What crosses the edge. Monsters chasing across the line are handed over by position; a spawn area only spawns inside its own cell. |
| `linkAllZones` | on | Link to every cell of the map, not only neighbours (party shares and such reach any cell). |
| `meshLinkPortOffset` | 10000 | The server-to-server link listens on the map port plus this. |
| `expectedMaxSpeed`, `meshStatsIntervalSeconds` | 8 m/s, 10 s | Band sizing check at start-up; the `Mesh stats` log line cadence. |
| `databaseReconnectSeconds`, `databaseReconnectGraceSeconds`, `databaseDrainWaitSeconds` | 5 s, 15 s, 35 s | Re-open the **database** connection whenever this server has none: the kit opens it once at start-up and never again, so a map server that loses the database manager -- or starts before it exists -- runs on accepting players while every save fails. The grace period keeps the retry off the kit's own first attempt; the drain wait lets requests that were in flight expire before reconnecting clears their callbacks, then reconnects anyway and says how many it stranded. A process that *hosts* the database manager is left alone. |
| `clusterConnectRetrySeconds` | 5 s | Re-open the cluster connection when the process could not open one at all. The kit handles every later failure by itself, but not this one: with no client socket it polls no events, so no disconnect is ever raised and the server runs on serving nobody. Matters when map servers can start before the central. 0 disables. |

**Client**

| Field | Default | Meaning |
|---|---|---|
| `useVisualStandIns`, `standInBlendSeconds`, `standInMaxLifetimeSeconds`, `standInMatchDistance` | on, 0.3 s, 1.5 s, 2.5 m | The seamless swap: clones of everything visible cover the reconnect, then blend onto the real entities. |
| `fastClientReadyOnMigration` | on | Skip the kit's post-load settle and GC on a hop. |
| `migrationReadyTimeoutSeconds`, `useMaskedMigration`, `migrationMaskFadeSeconds` | 10 s, on, 0.25 s | Fallback mask if the destination is silent. |
| `seamPreloadDistance`, `seamUnloadHysteresis` | 80 m, 40 m | Pre-load a neighbouring *map's* scene this close to the seam; drop it that much further away. 0 disables (the kit then reloads the scene with a loading screen). |
| `heldRpcTimeoutSeconds` | 15 s | RPCs that arrive before the character exists are held, then replayed. |

**Diagnostics:** `traceHandoffTiming` (on) writes one line per handoff step, ghost lag and replay; the `Mesh stats`
line every 10 s reports players, owned and ghost counts, link traffic per neighbour, handoffs (mean/max ms), ghost
steering error, frame time, CPU, memory and client send rate.

## 5a. How many players a cell holds

Measured on a 16-core box, five cells busy at once, three runs per step (`Tools/ServerMeshing/aggload.py`):

| players per cell | work per frame | fits 60 Hz (16.7 ms) | fits 30 Hz (33 ms) |
|---|---|---|---|
| 50 | 3.0 ms | yes | yes |
| 100 | 7.6 ms | yes | yes |
| 150 | 13.8 ms | **yes, at the edge** | yes |
| 200 | 21.3 ms | no | yes |
| 250 | 27.3 ms | no | **yes, at the edge** |

A cell is one single-threaded process, so this is a per-cell limit: more cores do not raise it, more cells do. Plan
about 150 players per cell at 60 Hz or 250 at 30 Hz, and budget **1.2 to 1.5 cores per cell** (about half of that is
outside the frame loop) and ~200 MB.

**Do not read the `Mesh stats` frame mean as saturation on a capped cell.** The frame limiter delivers ~17.4 ms
whatever the load, so a cell with nine milliseconds of headroom still reports above the 16.7 ms budget. Measure with
`aggload.py --uncapped`, where frame time is the work per frame, and compare that against the tick budget.

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

## 7. Verifying

- **Harness:** `python Tools/ServerMeshing/regress.py` runs the eight scenarios (pet, party, crash, harvest, build,
  seam, seamcold, sleep) on the rig and grades the logs; `--env KEY=VALUE` passes rig toggles.
- **Capacity:** `python Tools/ServerMeshing/loadtest.py --steps 100,200,300` puts synthetic players on one cell and
  prints work per frame, CPU, memory and per-client send rate (DESIGN 0.53 has the reference table).
- **In the logs:** `Own cell from channel id: …` (the cell resolved its rectangle), `Neighbour zone X linked`,
  `Handoff START / SENT / ACK`, `Ghost spawned …`, `Waking cell … / … told to sleep`, and `Mesh stats`.

## 8. Known behaviours and limits

- **Frames are per map.** A 10 km map keeps one scene at its own origin (the kit's quantized movement grid is off by
  default; float precision at 5 km is 0.5 mm). Only a seam between maps changes frames.
- **A seam pre-load** moves the neighbour scene's roots; its light and reflection probes and static batching stay at
  the authored origin until the swap, so lighting in the pre-loaded strip can differ briefly.
- **Crowds** are bounded by one process and by the kit's own AOI send cost (0.29 kB/s per visible entity per client),
  not by the mesh: 500 people in one place is a kit AOI/update-rate question.
- **Warp portals or spawn points exactly on a login position** do what they do in the kit: a character that stands up
  inside a portal trigger is warped. Not a mesh issue, but easy to trip over on a test map.
- **Dynamic split/merge of cells at runtime** is not implemented; sleeping cells on a fine grid cover the sandbox case.
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

**Server Meshing ▸ Build Packages** writes two Unity packages to `Build/Packages/`:

- `ServerMeshing-<version>.unitypackage` — the add-on (`Assets/ServerMeshing/Mesh`, runtime and Editor) with this guide
  and DESIGN.md under `Documentation/`, and the top-level README. The spike notes, the upstream patch series and the raw
  capacity table stay in the repository. Import it into any
  project that has OpenMMORPG installed (master at or after the four core changes of 2026-09-15), then follow §4.
- `ServerMeshing-TestRig-<version>.unitypackage` — `Assets/ServerMeshing/TestMap` (the two test maps, test items,
  auto client, launcher, play runner, load generator) plus `TestMap/Tools/` (the regression and capacity harnesses).
  It references the kit's demo (the enemy prefab, default weapon item and type), so it needs `BaseDemo` present.

Dependencies are not followed on export, so neither package carries any kit or demo asset.
