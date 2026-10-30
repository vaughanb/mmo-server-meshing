# Server Meshing for OpenMMORPG

Splits a map across several map-server processes ("cells") with seamless handoff, ghosting across cell edges, seams
between maps, cells of any size, on-demand ("sleeping") cells, and dynamic cells: a map set Dynamic has a busy cell
split at runtime and merged back, by command or by the central's own load policy, with supervision, buildings that
follow the cut and a central that can restart (`Documentation/SETUP.md` §6a). An add-on: subclasses on the kit's
manager and entity prefabs plus one world-registry asset; the package changes no kit file. It builds on eight small kit
changes (`Documentation/DESIGN.md` §17) and the kit's slot-indexed damage amounts (upstream 74ec05b), all of them in
OpenMMORPG v1.1.4 (`master` at 317d2b8, 2026-10-25) or later. Since 0.97.0 it also needs the kit's signed-cell
quantized positions (open-mmorpg/OpenMMORPG PR 11, `QuantizedPosition`), whose record format it writes; the add-on
does not compile on an older kit (`Documentation/SETUP.md` §3), and servers and clients must run the same kit build.

## Package

`ServerMeshing.unitypackage` holds the add-on only. The test rig (`TestMap/` and the Python harnesses in
`Tools/ServerMeshing/`) stays in the source repository, [vaughanb/mmo-server-meshing](https://github.com/vaughanb/mmo-server-meshing):
it needs that repository's layout and the kit's `BaseDemo`.

Upgrading from 0.96.0: that package also carried the repository's package builder, which adds a **Tools ▸ Open MMORPG ▸
Server Meshing ▸ Build Packages** menu. 0.96.1 leaves it out, but an import never deletes files, so delete
`Mesh/Editor/ServerMeshPackageBuilder.cs` by hand.

| Folder | Contents | Needs |
|---|---|---|
| `Mesh/`, `Documentation/` | the add-on (runtime + Editor) and its docs (the setup guide, the design record and the two-machine guide) | OpenMMORPG v1.1.4 or later with PR 11's quantized positions (all eight kit changes the add-on relies on, the damage amounts structs and `QuantizedPosition`) |

## Start here

1. Import the add-on package into a project that has OpenMMORPG installed.
2. Open **Tools ▸ Open MMORPG ▸ Server Meshing ▸ Setup** and follow `Documentation/SETUP.md`: create the world registry, add your map's layout
   from the open scene, press **Validate** and use the **Fix** buttons to put the add-on's subclasses on your manager
   and entity prefabs and to assign the registry, gameplay rule and server tick.
3. Run. The kit's server instance starts one process per cell; with sleeping cells on, cells start on demand.

`Documentation/DESIGN.md` is the design record with every decision, measurement and open question.

## Layout

```
Mesh/                       the add-on (all runtime code; Editor/ holds the Setup window)
Mesh/Editor/                Tools ▸ Open MMORPG ▸ Server Meshing ▸ Setup
Documentation/              SETUP.md, DESIGN.md and MULTIHOST.md (in the package only; the source lives in Docs/ServerMeshing)
TestMap/                    the test rig (in the repository only; not in the package)
../ServerMeshingDemo/       the 8 km test map and the kit demo copy that starts on it, Tools ▸ Open MMORPG ▸ Server Meshing ▸ Testing ▸
                            Build 8 km Test Map / Build 8 km Demo Copy / Build 8 km Demo Cells and Player (in the repository only)
Build/Editor/               the package builder, Tools ▸ Open MMORPG ▸ Server Meshing ▸ Build Packages (in the repository only)
```

In the repository, build the package with **Tools ▸ Open MMORPG ▸ Server Meshing ▸ Build Packages** (the menu is not in
the package, so an installed add-on does not show it). It lands in the repository's `Addons/` tree in the layout
the Open MMORPG Addon Manager expects — `Addons/ServerMeshing/dist/ServerMeshing.unitypackage` beside a generated
`package.json`, with the hand-written `README.md` and `screenshot.png` — and carries the empty, extensionless marker file
named after the add-on's guid that the Addon Manager uses to spot an installed add-on. In batch mode:

```
Unity.exe -batchmode -quit -projectPath <repo> -executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshPackageBuilder.BuildAll
```
