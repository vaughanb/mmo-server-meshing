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
kit file. It does build on **eight small kit changes** (bug fixes and extension seams, DESIGN §17): seven are merged in
OpenMMORPG `develop` (bb9895f, 2026-10-02), and the eighth, **CE-10** (upstream patch 0008, the building persistence
channel), is not upstream yet and has to be applied to the kit by hand (see Requirements).

- One cell process holds about **270 players at a 60 fps server tick** on a modern core; size cells so no expected crowd
  exceeds 250 (250–300 m suits a sandbox where towns can appear anywhere).
- Ten cells over two maps are exercised by the regression harness in the [Server Meshing Test Rig](../ServerMeshingTestRig)
  add-on, on one machine or across two: 25 scenarios, fourteen of them dynamic cells.

## Requirements

- OpenMMORPG `develop` at **bb9895f (2026-10-02) or later**. It carries seven of the kit changes the add-on relies on:
  upstream patches 0001-0004 (also on `master` since 2026-09-15: an owner-change movement fix, frame-granular request
  waits, a virtual `IsWatched()` on the monster activity component, a protected pending-spawn dictionary on the map
  manager), 0005 (on `master` since 2026-10-01: a cache finalizer that corrupted pooled collections under load) and
  0006-0007 (on `develop` only: one-shot movement flags sent to every receiver of a tick, entity states read to their
  length). The add-on does not compile without 0006.
- **Upstream patch 0008 (CE-10), not merged yet.** It adds one virtual to the kit's `MapNetworkManager`,
  `protected virtual string BuildingPersistenceChannelId => ChannelId;`, read at its seven building database sites
  (no behaviour change unless overridden). `MeshMapNetworkManager` overrides it, so that every cell of a Dynamic map's
  split family keeps its buildings under the base cell's channel. **The add-on does not compile without it, whether or
  not a map is Dynamic.** The patch is in the source repository at
  [`Docs/ServerMeshing/upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch`](../../Docs/ServerMeshing/upstream/0008-MapNetworkManager-let-subclasses-choose-the-building-persistence-channel.patch)
  (not in the package); from the project root, `git apply --directory=Assets/OpenMMORPG <patch>`, or make the edit by
  hand (the property and seven `ChannelId` → `BuildingPersistenceChannelId` reads in `MapNetworkManager.cs` and
  `MapNetworkManager_DatabaseFunction.cs`). It retires once upstream merges it.
- Unity 6000.3 or newer, matching the kit.
- Your map scenes in the build settings and your map infos in the game database.

## Usage

1. Import the package, then open **Server Meshing ▸ Setup**. The window has four panels that follow this order.
2. **Create the world registry** — one asset for the whole world.
3. **Cut a map into cells**: add your map's layout from the open scene, either as a uniform grid or as explicit
   rectangles. Cell ids are channel ids and must be unique across the world.
4. Press **Validate** and use the **Fix** buttons to put the add-on's subclasses onto your manager and entity prefabs
   and to assign the registry, the gameplay rule and the server tick. For a map set Dynamic it also says, before
   anything starts, which base cells could never split, whether a fully cut world would pass the 32 band neighbours or
   64 links a cell takes, and whether the hosts' served cells keep each family on its base's host.
5. Run. The kit's server instance starts one process per cell; with sleeping cells on, cells start on demand.

`Documentation/SETUP.md` in the package is the full operator's guide, `Documentation/DESIGN.md` the design record with
every decision and measurement, and `Documentation/MULTIHOST.md` covers running cells across more than one machine.
`SETUP.md` §6a is the operator's path for Dynamic maps end to end, with their known limits.

## Licence

MIT, as with the rest of the Open MMORPG add-ons.
