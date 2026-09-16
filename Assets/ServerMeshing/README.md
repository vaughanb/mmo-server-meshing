# Server Meshing for OpenMMORPG

Splits a map across several map-server processes ("cells") with seamless handoff, ghosting across cell edges, seams
between maps, cells of any size and on-demand ("sleeping") cells. An add-on: subclasses on the kit's manager and
entity prefabs plus one world-registry asset. The kit's core is not modified beyond four small changes that are in
upstream OpenMMORPG master since 2026-09-15.

## Packages

| Package | Contents | Needs |
|---|---|---|
| `ServerMeshing-<version>.unitypackage` | `Assets/ServerMeshing/Mesh` (runtime + Editor), `Assets/ServerMeshing/Documentation` (setup guide, design document, the upstream patches) | OpenMMORPG (master at or after the four core changes) |
| `ServerMeshing-TestRig-<version>.unitypackage` | `Assets/ServerMeshing/TestMap`: the two test maps, test items, auto client, launcher, play runner, load generator, and `TestMap/Tools` (the regression and capacity harnesses, Python) | the add-on package and the kit's demo (`BaseDemo`: the enemy prefab, the default weapon item and type are referenced by the test data and scenes) |

## Start here

1. Import the add-on package into a project that has OpenMMORPG installed.
2. Open **Server Meshing ▸ Setup** and follow `Documentation/SETUP.md`: create the world registry, add your map's layout
   from the open scene, press **Validate** and use the **Fix** buttons to put the add-on's subclasses on your manager
   and entity prefabs and to assign the registry, gameplay rule and server tick.
3. Run. The kit's server instance starts one process per cell; with sleeping cells on, cells start on demand.

`Documentation/DESIGN.md` is the design record with every decision, measurement and open question.

## Layout

```
Mesh/                       the add-on (all runtime code; Editor/ holds the Setup window and the package builder)
Mesh/Editor/                Server Meshing ▸ Setup, Server Meshing ▸ Build Packages
Documentation/              snapshot of the docs (in the package only; the source lives in Docs/ServerMeshing)
TestMap/                    the test rig (separate package)
Spike0/, Spike1/, Spike2/   the original spikes' notes (repository only, not packaged)
```

Build the packages with **Server Meshing ▸ Build Packages**; they land in `Build/Packages/`.
