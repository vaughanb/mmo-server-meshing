# Server Meshing for OpenMMORPG

Splits a map across several map-server processes ("cells") with seamless handoff, ghosting across cell edges, seams
between maps, cells of any size, on-demand ("sleeping") cells, and dynamic cells: a map set Dynamic has a busy cell
split at runtime and merged back, by command or by the central's own load policy, with supervision, buildings that
follow the cut and a central that can restart (`Documentation/SETUP.md` §6a). An add-on: subclasses on the kit's
manager and entity prefabs plus one world-registry asset; the package changes no kit file. It builds on eight small kit
changes (`Documentation/DESIGN.md` §17), all of them in OpenMMORPG `develop` at d077c5f (2026-10-20) or later; the
add-on does not compile on an older kit (`Documentation/SETUP.md` §3).

## Packages

| Package | Contents | Needs |
|---|---|---|
| `ServerMeshing.unitypackage` | `Assets/ServerMeshing/Mesh` (runtime + Editor), `Assets/ServerMeshing/Documentation` (the setup guide, the design record and the two-machine guide) | OpenMMORPG `develop` at d077c5f or later (all eight kit changes the add-on relies on) |
| `ServerMeshingTestRig.unitypackage` | `Assets/ServerMeshing/TestMap`: the two test maps, test items, auto client, launcher, play runner, load generator, and `TestMap/Tools` (the regression and capacity harnesses, Python) | the add-on package and the kit's demo (`BaseDemo`: the enemy prefab, the default weapon item and type are referenced by the test data and scenes) |

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
Documentation/              SETUP.md, DESIGN.md and MULTIHOST.md (in the package only; the source lives in Docs/ServerMeshing)
TestMap/                    the test rig (separate package)
```

Build the packages with **Server Meshing ▸ Build Packages**. They land in the repository's `Addons/` tree in the layout
the Open MMORPG Addon Manager expects — `Addons/<AddOn>/dist/<AddOn>.unitypackage` beside a generated `package.json`,
with the hand-written `README.md` and `screenshot.png` — and each package carries the empty, extensionless marker file
named after the add-on's guid that the Addon Manager uses to spot an installed add-on. In batch mode:

```
Unity.exe -batchmode -quit -projectPath <repo> -executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshPackageBuilder.BuildAll
```
