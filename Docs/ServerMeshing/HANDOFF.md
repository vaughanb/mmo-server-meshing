# Handoff — 2026-10-28

Where the work stands, how to get it running on another machine, and what is left. The design record
([DESIGN.md](DESIGN.md), version 1.05) has the full reasoning and measurements; this note is the short path in.

## What was done

1. **A meshed copy of the kit's demo that starts on the 8 km test map** — `Assets/ServerMeshingDemo/`.
   - `DemoMesh8kInit.unity`: a copy of the demo's `00Init` with scene-only overrides. It uses the
     demo's prefabs, database and home scene. Its settings:
     - sleeping cells, one ring;
     - only the 8 km map's cells are started, headless, from `Build/DemoMesh8k`;
     - its own SQLite file, `./mmorpg-demomesh8k.sqlite3`;
     - always noon (`DemoMesh8k-Daytime.asset`);
     - the demo's combat rules with mesh kill credit (`MeshCombatGameplayRule`);
     - new characters start next to a cell edge (`DemoMesh8kStartMap`, which sits under the server
       instance's login manager);
     - the 8 km map info registered at runtime (`DemoMesh8kMapInfo`).
   - The 8 km map (`Scenes/ServerMeshTestMap8k.unity`, moved here from `ServerMeshing/TestMap8k`):
     - 8 × 8 cells of 1,024 m;
     - flat ground at 20 m (below 20 m the demo's footstep effects play water splashes);
     - the demo's grass, with every cell edge painted red under a see-through curtain;
     - the demo's light, sky cycle and fog;
     - no scene camera;
     - the demo's monsters: meshed copies in `Monsters/`, spawned in the demo map's mixes;
     - start point (25, 512), 25 m inside cell (4,4), facing the edge with (3,4).
   - Menus, under **Tools ▸ Open MMORPG ▸ Server Meshing ▸ Testing**:
     - **Build 8 km Test Map** regenerates the map;
     - **Build 8 km Demo Copy** regenerates `DemoMesh8kInit`;
     - **Build 8 km Demo Player** builds `Build/DemoMesh8k`, which holds the map servers.
2. **Undetectable handoff, step 1: entity continuity (CE-14).**
   - What changed: the client no longer tears everything down at a cell crossing.
     - `LiteNetLibManager.MigrateClient` moves the running client to the new cell.
     - Every object on screen is kept.
     - The new cell's spawn of each object adopts the kept one instead of creating another.
   - Result: the player's character, controller, camera, HUD, animation, sounds and other players all
     carry on.
   - Core edit (upstreamable): `LiteNetLibManager.cs`, `LiteNetLibGameManager.cs`,
     `LiteNetLibAssets.cs` and `LiteNetLibIdentity.cs`, marked `// MESH-EDIT: CE-14`.
   - Add-on:
     - `ClientMigrationController`: continuity strategy, `useEntityContinuity`, on by default; the
       stand-in swap is its fallback.
     - `MeshMapNetworkManager.OnStartMigrateClient`.
     - `MeshPlayerCharacterEntity`: no HUD or nameplate rebuild on adoption.
     - `arrivalTeleportDistance`: the arrival no longer teleports the character back.
     - `MeshStandInAnimation` and `MeshStandInFootsteps` keep the fallback's look-alikes animating and
       stepping.
3. **Undetectable handoff, step 2: the long frame (DESIGN 1.02, package 0.96.6).** Before, each crossing had one
   frame of 19–48 ms against a 5 ms median. It was the adoption itself: the new cell's spawn of a kept object
   read its initial state as a first load, so every sync field fired its change callback and every sync list was
   cleared and re-added entry by entry; the character re-marked its appearance and stat caches and every HUD
   list rebuilt. The enter-game step also re-ran the kit's asset registration and cleared every effect pool.
   - Core edit (CE-14, upstreamable): an adopted object's initial state is applied as a **diff** — a sync field
     fires only when its value differs, a sync list fires Set / Add / RemoveLast only for what differs
     (`LiteNetLibElement.IsBeingAdopted`, `LiteNetLibSyncField`, `LiteNetLibSyncList.ReadInitialAsDiff`);
     `LiteNetLibAssets.WarmUpAdoption` and `LastAdoptionMilliseconds`; the client's `SetObjectOwner … is not
     spawned` is a dev log, not a warning.
   - Add-on: a hop that stays in the active scene (`MeshMapNetworkManager.SameSceneHop`) skips
     `Assets.Initialize`, `InitPoolingQueues` and `MeshRegisterSceneEntities`; `MeshMigrationWarmup` runs the
     adoption read once over the player's character when its data is in (`warmUpMigrationCode`), so the first
     hop of a session does not compile it.
   - The per-hop log line now attributes the frame: `longest frame <ms> at f<n> (connected f<n>, adopted f<n>) |
     adoption spawn work <ms> player, <ms> all`; every `MeshTrace` stamp carries `f<frame>`.
   - Measured (patrol x −15 ↔ 15 at z 512, 9–10 crossings a run, five runs, all adopted, `maxJump=0`):
     adoption spawn work 0.15–0.19 ms (0.86 ms on a session's first hop, after the warm-up; 6.3–7.2 ms
     before it); the longest frame per hop 5.6–12.5 ms against a 3 ms median, in the connect frame or in
     the tail, no longer on the adoption or the enter-game step.
   - Tests: 5 new in `LiteNetLibManager.Tests/KeptObjectAdoptionTests`; the full EditMode suite ran clean
     (3,223: 3,222 passed, 1 skipped by design).
   - Also fixed: a stale Build Settings entry for the 8 km scene's old path (`ServerMeshingDemo/ServerMeshTestMap8k.unity`).
4. **Undetectable handoff, step 3: pre-connect (DESIGN 1.03, package 0.96.7).** The connect frame was the
   longest frame left in a hop (the client transport destroyed and rebuilt when the warp arrived) and the
   connect handshake a round trip of the gap.
   - Server: the boundary tick tells each owned player's client which neighbouring cell it is approaching
     (`preconnectDistance`, 30 m, nearest registered and linked neighbour), one `MeshPreconnectMessage`
     per change, an empty address when it leaves the band (`MeshMapNetworkManager_Preconnect.cs`).
   - Client: `ClientMigrationController` holds one warm, idle connection to that cell (built from the
     manager's transport factory, polled every frame, dropped on disconnect, on a 5 s timeout, on a hint to
     another cell, and when the client stops). A continuity hop to exactly that address takes it over.
   - Core (CE-14): `LiteNetLibClient.AdoptConnectedTransport` and
     `LiteNetLibManager.MigrateClient(address, port, connectedTransport)` — the client continues on the
     warm transport and raises its own connected event, so enter-game goes out in the hop's frame.
   - The per-hop line carries `preconnected=True|False`; `ClientMigrationController.PreconnectedHops` counts them.
   - The destination sees an ordinary connected peer that has not entered the game (one `LiteNetLibPlayer`);
     LiteNetLib keeps it alive (1 s ping, 5 s timeout). The map servers must be rebuilt for the hint to exist.
   - Tests: 3 new in `LiteNetLibManager.Tests/PreconnectedMigrationTests`; the full EditMode suite ran clean
     (3,226: 3,225 passed, 1 skipped by design).
   - Found on the way: a hint that arrived while a hop still settled was dropped and, being sent once per
     change, never came again (half the first run's hops connected cold); hints are taken at any time now.
     The client's trace stamps are buffered during a hop and written once at its end: in the editor each log
     line captures a stack trace, and five of them sat in the enter-game frame.
   - Measured (patrol x −15 ↔ 15 at z 512, ten hops a run): every hop `preconnected=True`; the warm
     connection connects 1–62 ms after the hint, long before the line; steady hops' longest frame
     6.3–8.5 ms against a 3.1–3.4 ms median with no GC in it, the session's first hop 12.6 ms (first-time
     code) — the connect frame is no longer where a hop spends its time; the gap (`playerAdopted`)
     stays 250–360 ms, which is the destination's database load and relates data, the next item.
5. **Undetectable handoff, step 4: in-memory state transfer (DESIGN 1.04, package 0.96.8).** Two thirds of
   the 250–360 ms gap was the database: the origin awaited the crossing's save (66 ms) before the warp, the
   destination validated the token and read the row (133 ms) before promoting its ghost.
   - Origin (`MeshHandoff`, `inMemoryHandoffState`): sends `HandoffStateMessage` (the character as the row
     would read, plus the client's user id and token) over the mesh link, warps the client at once, demotes
     the entity, and writes the row behind the player (`SaveHandoffRowBehind`; a failure is logged, the
     destination's own saves write the state). Falls back to the save-first path when no link is up.
   - Destination: `OnHandoffState` keeps the state with the notice; `DeserializeEnterGameData` serves a
     matching enter-game (same user id and token, within the reservation window) from it with no database
     round trip (`ServeEnterGameFromHandoffState`), then promotes the ghost as before. A request that arrives
     before its state (same-frame race with the pre-connected client) waits up to `handoffStateWaitSeconds`
     when a ghost of that player stands here.
   - Stamps: `handoff state for … received`, `enter-game served from the handoff state … (no database)`,
     `enter-game waited N frame(s) for the handoff state`, `Handoff SENT … state in memory`,
     `Handoff row … written behind the player in N ms`.
   - Measured (patrol, ten hops a run): the origin's handoff is 6 ms server-side (was 66) with the row
     written behind in ~90 ms; the destination serves enter-game in 0 ms (was 133); the gap from the warp to
     the adoption is 112–183 ms, median ~135 (was 250–360), first hop of a session 201 ms; every hop adopted,
     no jump, longest frames 6–13 ms. What is left of the gap is the cells' frame time at the kit's
     `serverTargetFrameRate` of 30: enter-game, ready and the spawn's sync tick each cost a frame.
   - Tests: the enter-game peek's token overload (`MeshMergeMapSideTests`); the full EditMode suite ran clean
     (3,226: 3,225 passed, 1 skipped by design). The row-behind save and the destination's periodic saves
     cannot cross: the kit's updater proceeds every 5 s, the behind-save takes ~90 ms and retries for 3 s.
6. **Undetectable handoff, step 5: cells at 60 fps (DESIGN 1.05).** The gap left after 1.04 was server frames
   at the kit's `serverTargetFrameRate` of 30. The demo copy's scene now overrides it to 60 on its
   GameInstance (`ServerMeshDemo8kBuilder.ServerFrameRate`, kept by **Build 8 km Demo Copy**); the kit's
   prefab and the BaseDemo-CE rig keep 30.
   - Measured (patrol, ten hops): the cells at 59.7 fps by their stamps; the gap 63–119 ms, median ~92 (was
     112–183 at 30), first hop of a session 155 ms; every hop pre-connected and served from the handoff state,
     no jump, longest client frame 6–13 ms. Cell load 5–6 % (0.9 ms of work a frame) against 3–4 % (1.1 ms)
     at 30, with one player and 13–14 monsters each. Rebuild the map servers after changing it: the cells load
     the copy's init scene.
7. **Verified on the 8 km demo** (editor client plus headless bots), all seamless, no exceptions:
   - a 750 m walk across three cells;
   - 16 quick back-and-forth crossings (adopted after 180–398 ms, none fell back);
   - a second player standing past the line: kept and adopted;
   - an observer watching a bot cross five times: one object, one id throughout.
   - Per-frame check: since the arrival fix, no step is longer than twice the move speed allows.
   - EditMode tests: 3,218 ran; the one failure, `WebSocketClientSendTests.ReusedWriter_SendsOwnedPayloadsInOrder`,
     passes on its own (timing-flaky, unrelated).

## Setting up on another machine

1. **Unity 6000.3.21f1.**
2. **The kit's demo** (`Assets/OpenMMORPG/Demo/`, CC0, generated by the
   [open-mmorpg-demo](https://github.com/vaughanb/open-mmorpg-demo) repository) is committed with this
   work, and everything in `Assets/ServerMeshingDemo/` needs it. Regenerate it from that repository
   rather than editing it here.
3. **Four demo prefabs get mesh scripts.** `ServerMeshingDemo` swaps the add-on's subclasses onto
   `MMOServerInstance`, `MapNetworkManager`, `DemoPlayerCharacterMale` and `DemoPlayerCharacterFemale`;
   the committed demo already has them. After regenerating the demo, run **Build 8 km Demo Copy** once
   to do that again.
4. **Build Settings:**
   - Enabled: the demo's scenes, `01Home` and `ServerMeshingDemo/Scenes/ServerMeshTestMap8k`. The
     editor client loads them by name, so they must be enabled.
   - The BaseDemo-CE rig scenes are disabled on this machine.
5. **Build the map servers:** **Build 8 km Demo Player** builds `Build/DemoMesh8k/`. `Build/` is
   git-ignored, so this is required on a new machine, and again after any change to the 8 km scene,
   the registry or runtime code.
6. **Play** `Assets/ServerMeshingDemo/DemoMesh8kInit.unity`. Register, log in and create a character:
   it starts 25 m from the edge with (3,4), facing it. The database file is local and git-ignored, so
   a new machine starts empty.

## How to test

- **Per-hop log line.** Every crossing logs one line in `Editor.log` (tag `Spike2`; `grep CONTINUITY`):
  `Migration to … | CONTINUITY fellBack=False kept=K adopted=A unadopted=U | connected=…ms playerAdopted=…ms …`.
  It also gives per-frame `frames` / `player step` / `camera step` spreads, `jumps:` (frames whose
  step exceeds twice the move speed), `longest frame <ms> at f<n> (connected f<n>, adopted f<n>)` and
  `adoption spawn work`. `unadopted` > 0 or any jump means something on screen popped; a longest frame on the
  adoption frame means the state read did work (compare with the `MeshTrace` stamps, same frame numbers;
  since 1.03 a client's stamps during a hop come as one `hop stamps:` entry with a `C f<n>` line each, written
  when the hop ends). `preconnected=True` says the hop used the warm connection; `Pre-connect:` lines (tag
  `Spike2` on the client, `Mesh` in a cell's log) show the hint and the connect.
- **Per-cell server logs:** `Logs/Log_Map(ServerMeshTestMap8k)-Ch(<cell>)-…info.log`. Their
  `Work window` lines carry player and monster counts.
- **Editor auto-client** (no UI). Enter Play, wait for `Sleeping cells: 64 cell(s) start on demand`, then
  add `MultiplayerARPG.MMO.Mesh.ServerMeshAutoClient` to a new DontDestroyOnLoad object. Before its
  `Start`, set:
  - `mode` (stand / patrol / walk…), `username`, `characterName`, `channelId` = "C",
    `expectedMapName` = "ServerMeshTestMap8k" (its default is the 3×3 rig's map; without it every run
    creates another character);
  - `walkTargetX`, `walkTargetZ`, and for patrol `walkReturnX` and `observeSeconds` (−15, 512, 15, 75 gives
    nine or ten crossings).

  This is how every test above ran, through the Unity MCP bridge's `execute_code` (its CodeDom compiler is
  C# 6: no local functions, and types are found by name through the loaded assemblies). The EditorPrefs
  route (`ServerMesh.AutoClient.*`) also works, but leaves prefs set: delete them afterwards.
- **Guard the Play against a recompile.** A script recompile while playing (the test framework does one
  after a run; the Bezi sidekick's embedded `Packages/com.bezi.sidekick` was touched mid-Play once) reloads
  the domain under the servers and every network manager throws every frame for the rest of the Play.
  Right after entering Play call `AssetDatabase.DisallowAutoRefresh()`, and `AllowAutoRefresh()` once
  stopped; never run tests and then enter Play without checking `isCompiling` is false.
- **Headless bot** (another player), from `Build/DemoMesh8k`:

  ```
  kit-ce-project.exe -batchmode -nographics -logFile <path> -meshAutoClient -meshAutoMode patrol -meshUser bot2 -meshChannel C -meshWalkX -15 -meshWalkZ 515 -meshReturnX 15 -meshObserveSeconds 60
  ```

  Stop it afterwards: it outlives Play.
- **Local test accounts:** meshtest, meshtest2, bot1, bot2 exist only in this machine's
  `mmorpg-demomesh8k.sqlite3`.

## Open items, in rough order

1. **What is left of the gap.** With 1.05 the gap between the warp and the adoption is 63–119 ms on
   localhost (median ~92) and all of it is server frames at 60 fps: enter-game in one, the client's ready in
   the next, the spawn on the sync tick after, plus the client's own frames. Nothing in it is the database or
   the connect. Next steps, in order:
   - holding actions pressed mid-hop (the arrival's respawn RPC still clears the owner's action states, and
     commands sent during the gap are not replayed);
   - extrapolating kept entities through the gap, and skipping the interpolation reset of an adopted entity
     whose owner did not change;
   - a server-driven arrival (the destination spawns to the pre-connected, pre-authenticated client when the
     handoff commits, without waiting for enter-game and ready) would take the gap to one round trip.
   On Mono the first hop of a session still compiles the hop's first-time code (12.6 ms, the adoption read
   is warmed up); an IL2CPP client has no JIT. Nothing has been measured with real latency yet; pre-connect
   is what keeps the connect out of a laggy hop, the gap will still scale with the round trip until the
   transfer is in memory.
2. **Monsters at a crossing:** not yet exercised. They use the same nearest-prefab rule as players, but
   no pack has chased a player over a line yet; the level 1–2 wolves did not aggro.
3. **Mount and pet crossings.** Demo characters start with no mount or pet item; add one to the test
   character first.
4. **Write the CE-14 upstream patch** into `Docs/ServerMeshing/upstream/` (as 0001–0009 were) and open
   the PR. It now covers `LiteNetLibElement`, `LiteNetLibSyncField` and `LiteNetLibSyncList` too (§17).
5. **Run the rig regression** (`python Tools/ServerMeshing/regress.py`). Continuity is now the default
   on the BaseDemo-CE rig too, and the suite has not been run since. It needs:
   - `Config/mySqlConfig.json` with MySQL credentials (only the `.example` is here);
   - Build Settings switched back to BaseDemo-CE;
   - a `Build/Server` rig build.
6. **Run the full EditMode suite** after any further core edit (3,218 tests; `WebSocketClientSendTests.
   ReusedWriter_SendsOwnedPayloadsInOrder` is timing-flaky and passes alone).

## Gotchas found on the way

- **Assets loaded before `OpenScene` go dead.** Opening a scene in an editor script unloads unreferenced
  assets, so an asset loaded earlier is dead after it; assigned, it saves as nothing. That is how every
  8 km spawn area once had no monster, and the demo copy no gameplay rule. Load after opening.
- **The demo's server prefab starts map servers windowed in the editor** (`editorNotSpawnInBatchMode: 1`).
  The demo copy overrides it in its scene.
- **The demo plays footsteps from foot plants, not the kit's timer** (`stepDelay` is set to "never").
- **The login server, not the central, creates characters** (`ILoginServerDataManager`).
