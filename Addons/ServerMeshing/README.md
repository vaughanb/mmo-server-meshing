# Server Meshing

![Server Meshing](dist/screenshot.png)

Splits one map across several map-server processes — **cells** — with seamless handoff. A player walking over a cell
edge is handed to the next server without a loading screen and without a visible break (200–300 ms server side, hidden
behind stand-ins on the client). Neighbouring cells exchange **ghosts** of everything within 80 m of their shared edge —
players, monsters, pets, drops, harvestables, buildings — so players see and fight across it, and hits, kills, loot,
party and guild rewards, quest counters, buildings and pickups all resolve on the owning side.

Maps can be placed next to each other in one world; a **seam** between two maps is crossed the same way, with the client
pre-loading the neighbouring scene behind the player's back. Cells can be any size — small where crowds are expected,
large over empty land — and with **sleeping cells** on, a cell with nobody near it does not run at all: it starts a few
seconds ahead of the first player who approaches, and exits again later. Empty land costs nothing.

A map set **Dynamic** has its cells cut and merged at runtime: by an operator's command, or by the central's own
policy, which reads every cell's load report, splits a cell that stays hot along the cut its cost model says relieves
it most (only when that is worth a process), merges a pair that would be cool together, measures every operation it
made, and restarts or collapses a child that dies. A cell cut off from the cluster fences itself so two processes
never own one piece of ground, buildings follow the cut (a chest and its contents move with the ground they stand
on), and a central that restarts adopts the cut layout the cells hold and finishes the operation it was running.

The add-on is subclasses on the kit's managers and entity prefabs plus one world-registry asset; the package changes no
kit file. It does build on **eight small kit changes** (bug fixes and extension seams, DESIGN §17), all of them merged
in OpenMMORPG `develop` (d077c5f, 2026-10-20).

- One cell process holds about **270 players at a 60 fps server tick** on a modern core; size cells so no expected crowd
  exceeds 250 (250–300 m suits a sandbox where towns can appear anywhere).
- Ten cells over two maps are exercised by the regression harness of the [test rig](#the-test-rig) the package
  includes, on one machine or across two: 35 scenarios, twenty-two of them dynamic cells (two run with links on demand, one
  is the operator's `unfreeze` of a family a central restart left frozen, and one a three-hour soak of automatic splits
  and merges under kills and central restarts) and two on-demand links. Across two
  machines the dynamic scenarios run with the cut family on the second machine's own map spawner.

## Requirements

- OpenMMORPG `develop` at **d077c5f (2026-10-20) or later**. It carries all eight kit changes the add-on relies on:
  upstream patches 0001-0004 (also on `master` since 2026-09-15: an owner-change movement fix, frame-granular request
  waits, a virtual `IsWatched()` on the monster activity component, a protected pending-spawn dictionary on the map
  manager), 0005 (on `master` since 2026-10-01: a cache finalizer that corrupted pooled collections under load),
  0006-0007 (one-shot movement flags sent to every receiver of a tick, entity states read to their length) and 0008
  ([open-mmorpg/OpenMMORPG#8](https://github.com/open-mmorpg/OpenMMORPG/pull/8): a virtual `BuildingPersistenceChannelId` on the kit's `MapNetworkManager`, which
  `MeshMapNetworkManager` overrides so a Dynamic map's split family keeps its buildings under the base cell's channel).
  The add-on does not compile without 0006 or 0008. On an older kit, the patch is in the source repository at
  [`Docs/ServerMeshing/upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch`](https://github.com/vaughanb/mmo-server-meshing/blob/master/Docs/ServerMeshing/upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch)
  (`git apply --directory=Assets/OpenMMORPG <patch>` from the project root).
- Unity 6000.3 or newer, matching the kit.
- Your map scenes in the build settings and your map infos in the game database.
- For the test rig only: the kit's **BaseDemo** (the rig's test data and scenes reference its enemy prefab and its
  default weapon item and type), and Python 3 with `pymysql` for the harnesses.

## Usage

1. Import the package, then open **Server Meshing ▸ Setup**. The window has four panels that follow this order.
2. **Create the world registry** — one asset for the whole world.
3. **Cut a map into cells**: add your map's layout from the open scene, either as a uniform grid or as explicit
   rectangles. Cell ids are channel ids and must be unique across the world.
4. Press **Validate** and use the **Fix** buttons to put the add-on's subclasses onto your manager and entity prefabs
   and to assign the registry, the gameplay rule and the server tick. For a map set Dynamic it also says, before
   anything starts, which base cells could never split, whether a fully cut world would pass the 32 band neighbours or
   the 64 links a cell takes when every cell links to every other (a map with `onDemandLinks` links each cell only to
   the cells it needs, and has no such ceiling), and whether the hosts' served cells keep each family on its base's host.
5. Run. The kit's server instance starts one process per cell; with sleeping cells on, cells start on demand.

`Documentation/SETUP.md` in the package is the full operator's guide, `Documentation/DESIGN.md` the design record with
every decision and measurement, and `Documentation/MULTIHOST.md` covers running cells across more than one machine.
`SETUP.md` §6a is the operator's path for Dynamic maps end to end, with their known limits.

## The test rig

`TestMap/` in the package is the test rig, and the reference for what a meshed world looks like set up. Two test maps
are cut into **ten cells** — a 3×3 grid on one map plus a tenth cell across a seam on the other — with each zone
colour-coded so you can see which server owns the ground you are standing on.

The rig ships the content needed to exercise every path that crosses a cell edge: test items, skills (heal and debuff),
a kill quest, a bow and missile, a mount, a pet, a harvestable and a placeable building. It adds an **auto client** that
walks characters over edges on its own, an editor **launcher** and **play runner** that bring up all ten cell processes,
a **load generator**, and the Python regression and capacity harnesses under `TestMap/Tools`. The regression harness
runs 35 scenarios: eleven of the mesh itself (handoff, ghosts, combat, parties, buildings, seams, sleeping cells),
twenty-two of dynamic cells (split, merge, a three-cell family's merge and sibling collapse, crashes mid-operation, the
automatic policy with one or two hot cells and a host without headroom, buildings across a cut, a central restart, the
last with the central run as a build process of its own so it can be killed alone, and a three-hour soak of automatic
splits and merges under child kills and central restarts; two run with links on demand) and two of on-demand links (a
map of more cells than a capped link server takes, and a party share for a far member). With `--remote-cells` the
second machine serves cells too: started by hand for a Static scenario, by its own map spawner (children, restarts and
kills included) for a Dynamic one.

To try it:

1. Add `ServerMeshTestMap` and `ServerMeshTestMap2` to the build settings and their map infos to the game database;
   **Server Meshing ▸ Setup ▸ Validate** reports anything still missing.
2. Assign `ServerMeshWorld` as the world registry — it already holds the ten-cell layout and both maps' placement.
3. **Server Meshing ▸ Launch Test Cluster** starts one map-server process per cell (plus the central, login and
   database servers) from a server build, or copies the commands to the clipboard.
4. **Server Meshing ▸ Auto Client ▸ Enable (walk C→E)** makes the editor client walk across a cell edge on its own
   when you enter Play; the other modes cover self-migration and enter-only.

`TestMap/Tools/` is a reference copy of `regress.py` (the 35 regression scenarios), `loadtest.py` (the capacity sweep)
and their helpers. They are written for the repository layout, not for a location inside `Assets`: they find the
project root two directories above themselves and expect `Config/mySqlConfig.json`, a server build at
`Build/Server/kit-ce-project.exe` and the editor's play runner watching `Temp/`. To run them, copy the folder to
`<project>/Tools/ServerMeshing/` and adjust the build path to your product name. `Documentation/SETUP.md` §7 and
`MULTIHOST.md` cover what they do and how to run the rig across two machines.

## Licence

MIT, as with the rest of the Open MMORPG add-ons.
