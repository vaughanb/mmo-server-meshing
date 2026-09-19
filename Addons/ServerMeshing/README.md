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

The add-on is subclasses on the kit's managers and entity prefabs plus one world-registry asset. **The kit's core is not
modified.** It relies on four small core changes that have been in OpenMMORPG master since 2026-09-15.

- One cell process holds about **270 players at a 60 fps server tick** on a modern core; size cells so no expected crowd
  exceeds 250 (250–300 m suits a sandbox where towns can appear anywhere).
- Ten cells over two maps are exercised by the regression harness in the [Server Meshing Test Rig](../ServerMeshingTestRig)
  add-on, on one machine or across two.

## Requirements

- OpenMMORPG at master from **2026-09-15 or later** — it carries the four core changes the add-on relies on (an
  owner-change movement fix, frame-granular request waits, a virtual `IsWatched()` on the monster activity component,
  and a protected pending-spawn dictionary on the map manager).
- Unity 6000.3 or newer, matching the kit.
- Your map scenes in the build settings and your map infos in the game database.

## Usage

1. Import the package, then open **Server Meshing ▸ Setup**. The window has four panels that follow this order.
2. **Create the world registry** — one asset for the whole world.
3. **Cut a map into cells**: add your map's layout from the open scene, either as a uniform grid or as explicit
   rectangles. Cell ids are channel ids and must be unique across the world.
4. Press **Validate** and use the **Fix** buttons to put the add-on's subclasses onto your manager and entity prefabs
   and to assign the registry, the gameplay rule and the server tick.
5. Run. The kit's server instance starts one process per cell; with sleeping cells on, cells start on demand.

`Documentation/SETUP.md` in the package is the full operator's guide, `Documentation/DESIGN.md` the design record with
every decision and measurement, and `Documentation/MULTIHOST.md` covers running cells across more than one machine.

## Licence

MIT, as with the rest of the Open MMORPG add-ons.
