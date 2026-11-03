# Handoff — 2026-10-28

Where the work stands, how to get it running on another machine, and what is left. The design record
([DESIGN.md](DESIGN.md), version 1.16) has the full reasoning and measurements; this note is the short path in.

## What was done

1. **A meshed copy of the kit's demo that starts on the 8 km test map** — `Assets/ServerMeshingDemo/`.
   - `DemoMesh8kInit.unity`: a copy of the demo's `00Init` with scene-only overrides. It uses the
     demo's prefabs, database and home scene. Its settings:
     - sleeping cells, one ring;
     - only the 8 km map's cells are started, headless, from `Build/DemoMesh8kCells`;
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
     - about 10,000 monsters and 5,500 harvestables spread like a game's land (DESIGN 1.36): the demo's, on meshed
       copies in `Monsters/` and `Harvestables/`, busy and quiet stretches, harder further from the start, camps on the lines;
     - start point (25, 512), 25 m inside cell (4,4), facing the edge with (3,4): 90 m around it is clear, but for six deer
       across the line and a row of trees, mushrooms and rocks on it, north of the start. Trees need an axe and rocks a pick:
       both are in every class's starting bag, to equip. Bots and patrols stay in the clear.
   - Menus, under **Tools ▸ Open MMORPG ▸ Server Meshing ▸ Testing**:
     - **Build 8 km Test Map** regenerates the map;
     - **Build 8 km Test Map Spawns** replaces only its spawns, without a navmesh bake;
     - **Build 8 km Demo Copy** regenerates `DemoMesh8kInit`;
     - **Build 8 km Demo Cells and Player** builds `Build/DemoMesh8kCells` (the map servers, a Server build)
       and `Build/DemoMesh8k` (a player, for headless bots).
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
7. **Undetectable handoff, step 6: actions carried across the hop (DESIGN 1.06, package 0.96.9).** A swing or
   cast in flight at the line used to lose its `IsAttacking` / `IsUsingSkill` on the owner (the arrival's respawn
   RPC cleared them), and its trigger was dropped by the destination and applied by the origin's ghost instead.
   - Owner: during a hop the respawn RPC fires its event and keeps the action states
     (`MeshPlayerCharacterEntity.RpcOnRespawn`, `IsClientHopping`).
   - Origin: the in-flight action's hit validation goes with the handoff state (`HandoffStateMessage.action`,
     `MeshCarriedAction`); the demotion cancels the ghost's routines and a server ghost never applies a trigger.
   - Destination: prepares the same validation when the arrival registers (`PrepareCarried`), so the client's
     trigger is applied there and replayed to the origin's observers.
   - Puppets hold their weapon as the owner does (`GhostSnapshotMessage.isWeaponsSheathed`): before, a player who
     drew on one cell and crossed back was promoted from a sheathed ghost and the weapon was put away.
   - Measured (patrol swinging at the air, 7 hops, 60 swings): every start and trigger applied once by the
     owning cell, none by a ghost; the mid-swing hop carried its swing and the destination applied its trigger;
     no hop changed the weapon's state.
8. **Undetectable handoff, step 7: commands sent inside the gap (DESIGN 1.07, package 0.96.10).** A swing pressed
   between the warp and the adoption played on the client and never happened (7 of 7): its call named the
   origin's object id, the arriving entity had no owner yet, and the call could beat the origin's notice.
   - Destination (`MeshMapNetworkManager_GapCommands`): parks every client call from a connection with no
     character registered and replays them in order when one registers, retargeting the origin's object id
     (`HandoffStateMessage.originObjectId`) to the id here; for 5 s after, a call still naming the old id is
     retargeted too. The kit's owner check still decides every call.
   - Origin: a call for a character it just demoted is forwarded over the mesh link (`HandoffLateRpcMessage`,
     id 135) and replayed at the destination first.
   - Measured, final code (44 hops, 368 swings): none lost or doubled; 40 gap swings replayed after 17–57 ms;
     one forwarded call replayed. Against a pinned deer, every hit came from exactly one trigger, including 6
     gap swings and the forwarded one.
   - Not covered: a call still queued on the old connection when the warp closes it (up to one 15 ms LiteNetLib
     update; seen once in 15 hops).
9. **Undetectable handoff, step 8: the other characters keep walking through a hop (DESIGN 1.08, package
   0.96.11).** A character walking beside the player stood 52–108 ms at the player's hop (no states reach a kept
   entity until the new cell adopts it), then caught up at 2–4 times its speed.
   - `MeshKeptExtrapolator` moves each kept character that was walking on at the velocity it was drawn with
     (capped at 0.3 s) from the frame the kit stands it, until the new cell's first state after the adoption;
     the kit interpolates from there. `extrapolateKeptCharacters` switches it.
   - Measured (A/B, a second bot 3 m beside): off, the stand began inside the gap in 14 of 17 walking hops; on,
     in none of 16, with the new cell's first correction 0.2–7.7 cm.
   - The CONTINUITY line now ends with `others:` (`MeshKeptMotionMeter`: per walking kept character, its
     longest stand and when it began after the warp, and its fastest frame against its median speed) and
     `kept walking N, handed back M`.
10. **Undetectable handoff, step 9: someone else's crossing, seen from the line (DESIGN 1.09, package 0.96.12).**
    With a player standing beside the line, a character on the same cell moved smoothly, but one just across it
    moved in stop-go bursts all the time (a steered puppet dashes to each 0.1 s snapshot and stands), and at its
    crossing it stood 50–150 ms and then lunged.
    - The demo copy's puppets are kinematic (`puppetMotion`, set by the builder and in `DemoMesh8kInit.unity`).
    - `MeshMapNetworkManager_ArrivalWalk`: the destination walks an arriving character along its crossing track
      (origin's position and velocity, sent with the handoff state) until its client's first state; the origin
      starts the demoted copy's kinematic body at once from that velocity.
    - Measured (150 s, ~20 crossings): stalls across the line about 139 → 20, lunge frames at the crossing 77 → 37;
      the client's first state still moves the arrival about 0.2 m.
    - `MESH_TRACE_OBSERVED=1` logs every stall and lunge of a nearby walking character on the client (tag
      `MeshObserved`).
11. **A crossing client's own state syncs (DESIGN 1.10, package 0.96.13).** The cells logged `sync element not
    found` about twice a hop: after a hop the client syncs its own fields under the origin's object id until it
    adopts the destination's, and the kit applies a client state to whatever object the id names, without an
    owner check (another player's character would have taken them silently). The destination now parks a
    one-state message from an unregistered caller and replays it at registration, retargets the origin's id
    after, and drops one naming an object that is not the caller's. Measured: none of the error in a 150 s
    two-bot run.
12. **The arriving character's last step halved (DESIGN 1.11, package 0.96.14).** The destination's puppet of a
    crossing character takes the crossing track as soon as the handoff state arrives, so it is on the track by
    the registration instead of easing onto it from ~0.5 m behind. The client's first state now moves it
    0.02–0.34 m (median ~0.14 m, was ~0.28 m); what is left is scatter in where the client is, not a bias.
13. **The demo copy's network tick is 60 Hz (DESIGN 1.12).** At the kit's 20 Hz the spawn of a crossing's character
    waited for the server's next tick and its client's first state for the client's, up to 50 ms each. The
    copy's map network manager (cells and editor client) now runs `updateFps` 60
    (`ServerMeshDemo8kBuilder.NetworkUpdateFps`); the gap's median fell to about 80 ms and the client's first
    state reaches the destination 33–67 ms after the registration (was 67–150). Rebuild the map servers after
    changing it.
14. **A hop's ready goes with its enter-game (DESIGN 1.14, package 0.97.1).** On a hop over a warm connection to
    a cell of the same map (the pre-connect hint now says so), the client sends ready right behind enter-game
    instead of after the answer; it holds the server's state messages until the answer is handled, and the
    destination holds a ready that beats its enter-game. No core change. The gap's median fell to about 47 ms
    (36–65; the session's first hop ~140). `pipelineReadyOnHop` switches it.
15. **What the client queued before the warp is not lost (DESIGN 1.15, package 0.97.2).** LiteNetLib's `Stop`
    dropped a connection's unsent queue, so a call made in the warp's frame never left the client (12 of 13 in
    the reproduction). Before the kit stops the old connection, the client now wakes its send thread and waits
    (at most 5 ms; 0.0–0.19 ms measured) until the queue is out; the origin forwards it. 19 of 19 reproduced
    swings ran once. Reproduce with the patrol's `swingAtWarp`.
16. **A click-to-move player keeps walking through a hop (DESIGN 1.16, package 0.97.3).** The adoption's owner
    change cleared the kit's path, so a player who clicked across the line stopped at it until they clicked
    again. The client now gives that click again when its character is adopted (`click-move resumed` on the
    CONTINUITY line). Seen from the line, the crossing's long stalls (median 142 ms) went to ~33 ms.
17. **Verified on the 8 km demo** (editor client plus headless bots), all seamless, no exceptions:
   - a 750 m walk across three cells;
   - 16 quick back-and-forth crossings (adopted after 180–398 ms, none fell back);
   - a second player standing past the line: kept and adopted;
   - an observer watching a bot cross five times: one object, one id throughout.
   - Per-frame check: since the arrival fix, no step is longer than twice the move speed allows.
   - EditMode tests: 3,218 ran; the one failure, `WebSocketClientSendTests.ReusedWriter_SendsOwnedPayloadsInOrder`,
     passes on its own (timing-flaky, unrelated).
18. **A second 8 km map across a seam (DESIGN 1.40, package 0.97.25).** `ServerMeshTestMap8kEast` sits one map east of
    the first in the registry (world x 8,192), cut 8 × 8 like it; the demo copy registers both maps, so the spawner
    serves 128 on-demand cells. The client pre-loads the other map's scene within 80 m of the seam and swaps into it
    at the hop; the hop itself is the usual one into the other map's cell. Each scene carries a 100 m collider apron
    past its seam edge, baked into the navmesh, and every meshed monster and harvestable prefab is registered in every
    process. Found and fixed on the way: the kit rode the swap's 8,192 m shift of the ground as a moving platform and
    put the player a map too far (`ForgetPlatforms`); the migration metrics read the frame shift as a jump; a second
    hop across the same seam cancelled the recorded shift; the pre-loaded scene's lights and scripts ran beside the
    active scene's. Verified: round trips in both directions (positions right, adopted in 230–420 ms), an observer on
    the east map seeing the crosser as a translated ghost, a bandit chasing a player over the seam and handed over
    with its target.
19. **The seam's leftovers (DESIGN 1.41, package 0.97.26).** Each 8 km map is two scenes: the map scene a client loads
    (7–8 roots) and `<map>-Server.unity` with the 2,300 spawn areas, named in the registry layout's new `serverScenes`
    and loaded additively by the cells in `OnServerOnlineSceneLoaded` (now async) before the areas are read; the seam
    pre-load dropped from one 87–101 ms frame to 45–59 ms over 2 frames. `ReplyMonsterHandoffFailed` answers on any
    map (it answered on Dynamic maps only, which is why 1.40's refused bandit was lost instead of re-promoted). The
    client pre-loads every neighbouring map's scene within the distance, not only the nearest (a corner; run in
    item 21). `ClientMigrationController.Result.frameShift` lets the auto client move its targets with a seam
    hop, so `patrol` and `flee` run across a seam: 11 crossings in 100 s, no jump, no exception. The full EditMode
    suite on this code: 4,501 passed, 2 skipped, 0 failed. Three cases of the kit copy's generic `MessageRoundTripTests`
    had failed since 1.33 (`GhostSnapshotMessage`, whose `fullRotation`, `items` and `ageSeconds` are written only for
    some kinds; `MeshLoadReportMessage` and `MeshOccupancyGrid`, an index out of range in the test's filler): the
    add-on's rows now join that test's tables from `Assets/ServerMeshing/Tests/Editor/MeshMessageRoundTripTests.cs`
    (a partial of the kit class, so the kit copy's file stays upstream's but for the `partial` keyword): the snapshot
    is sent as a container, its building-only rotation and receiver-only age listed, and the grid is shaped to
    `LayerCount` arrays of columns × rows before it is written. `MeshMessageBranchTests` round-trips every `GhostKind`
    by hand. No message changed.
30. **A vendor who leaves, a stop on either side of a sale, an answer through a sleeping cell (DESIGN 1.53, package
    0.97.38).** A buyer's word for a sale pending on a cell is settled there even after the vendor left (mailed), and a
    cell that cannot reach the cell it waits on asks the central to wake it. `regress.py vendleave buystop salestop
    sleephold` run the paths 1.51-1.52 built.
29. **Open exchanges survive a cell's clean stop (DESIGN 1.52, package 0.97.37).** A cell does not sleep or retire while
    it holds an open trade or sale; one stopped all the same writes them to a journal as it quits (after the kit saved
    every player) and its next process takes them up. An answer now goes to the cell that holds the exchange.
    `regress.py tradestop` (66 checks) stops E cleanly with a trade open. A crash still loses what the kit's last save
    did not have (every 20 s at most), exchanges included.
28. **No deadline on an exchange, and mail for a player who is gone (DESIGN 1.51, package 0.97.36).** A trade or a sale
    across a line is settled only by the other cell's answer; after a minute without one its player is released and the
    exchange stays open. A result for a player no longer on the cell (logged out, disconnected, hopped) is mailed to its
    account. `regress.py trade` (82 checks, the mail read from the database) and `market` (84) run both.
27. **A trade survives a lost answer and a trader who leaves (DESIGN 1.49, package 0.97.34).** The other trader's cell
    decides whether an exchange happened and remembers it; the escrow's cell asks instead of giving the offer back after
    10 s, and a trader who disconnects mid-exchange has the answer applied before the kit's last save. `regress.py
    trade` runs it (75 checks, the bags read from the database).
26. **A purchase survives a buyer who leaves and a lost answer (DESIGN 1.48, package 0.97.33).** The buyer's cell
    decides whether a sale happened, and the vendor is paid only on its word; without one the vendor's cell asks. A
    buyer who disconnects mid-purchase gets its escrow back before the kit saves it and the item goes back to the
    vendor. `regress.py market` runs both (83 checks, gold and bags read from the database).
25. **A purchase across a hop, a shop through a split and a merge, a workbench across a line (DESIGN 1.47, package
    0.97.32).** A buyer is no longer handed off while its purchase is on its way (at most the escrow's 10 s), and an
    answer that comes late reaches it on its new cell; before, a buyer who crossed meanwhile lost the price. Two new
    scenarios run the three paths 1.46 left: `regress.py market` (two purchases with the hop held, the second past the
    timeout, then a public workbench whose queue E's copy finishes) and `splitvend` (a shop moved by a split and back by a
    merge, bought from both times). The rig has a test workbench now, `TestBench-CE`.
24. **A shop, a crafting queue and friend requests across a line (DESIGN 1.46, package 0.97.31).** A player's shop is
    seen and bought from across a line (the sign in the ghost snapshot, the purchase in two halves with the price in
    escrow), a crafting queue crosses a hop with its crafter (a built-in handoff carrier: its own queue and its entries at
    a public workbench), and friend requests needed nothing. `regress.py social` runs all three (70 checks). It found a
    trade that stuck when both traders confirmed at once; the cell of the trader with the smaller character id now begins
    the exchange. A workbench's queue across a hop was built but not run here (item 25 runs it).
23. **Trades, duels and invitations in the rig regression (DESIGN 1.45, package 0.97.30).** `regress.py social` (the 35th
    scenario) trades, invites to a party and to a guild, and duels across the C/E line, the bot quitting 10 s into the duel.
    Writing it found two defects, fixed: a cross-line trade cancelled itself as it opened (the kit's trade window closed on
    a None that overtook the accept), and a duel never ended when a duelist disconnected (the kit's end in `OnDestroy`
    comes after the despawn delay and no longer runs; the add-on ends it as the player is unregistered, so a hop forfeits
    too). The guild invitation across a line now runs and passes.
22. **Trades, duels and party or guild invitations across a line (DESIGN 1.44, package 0.97.29).** Mesh subclasses of
    the kit's dealing and dueling components (put on the entity before the kit adds its own; they declare the kit's server
    RPCs again under the same names and forward the other side's half as ghost actions) and wrappers on the party and guild
    invitation handlers; a trade's exchange runs in two halves with an escrow on the committing cell. Measured on the 8 km
    demo with the editor client and a bot a metre apart across the start cell's west line: trade, party and duel all pass
    (`RESULT social: across=True trade=ok duel=ok party=ok`). To rerun: save `bot2Char` alive (`currentHp`), with gold for the
    kit's dealing fee, at (−0.5, 21, 515) on `ServerMeshTestMap8k` and MeshTester at (0.5, 21, 515), clear both `partyId`
    and the `party` rows; start the bot first (`-meshAutoMode stand -meshWalkX -0.5 -meshWalkZ 515 -meshSocialAccept`),
    then the editor's auto client `mode=social socialTargetName=bot2Char walkTargetX=0.5 walkTargetZ=515
    expectedMapName=ServerMeshTestMap8k`. A dead character logs in at the map start and walks to its spot, 1.5 m short.
    Also found and fixed: a client logging in beside a line did not see the player already across it (a harvestable
    puppet's unregistered prefab dropped the rest of the baseline packet; every harvestable and drop prefab is now
    spawnable in every process), and the 8 km maps had no `AOIMapBounds` (the kit's grid interest manager ran without a
    grid; the builder adds one).
21. **A corner where four maps meet (DESIGN 1.43, package 0.97.28).** Two more 8 km maps, `ServerMeshTestMap8kNorth` and
    `ServerMeshTestMap8kNorthEast`, make a four-map corner at world (4,096, 24,096) (cells west (7,7), east (0,7), north (7,0),
    north-east (0,0)); the spawner serves 256 cells on demand. `patrol` with `patrolReturnZ` runs diagonal legs. Diagonal
    east ↔ north through the point: 13 hops straight between the two cells that touch at a point, three scenes pre-loaded at
    once and two moved with every swap; straight east ↔ west beside the corner: 15 hops with three scenes held. Both `PASS`,
    no jump, step max 0.28 m, adopted 171–239 ms. To rerun: save MeshTester on `ServerMeshTestMap8kEast` at (−4086, 21, 4086),
    rotation 315, auto client `patrol expectedMapName="" walkTargetX=-4106 walkTargetZ=4106 walkReturnX=-4086 patrolReturnZ=4086`;
    the straight arm at (−4086, 21, 4090) rotation 270 with `walkTargetX=-4111 walkTargetZ=4090 walkReturnX=-4081`. Read the
    client's `Seam:` lines (`N other pre-load(s) moved`) and the corner cells' `Handoff START … East-0-7->North-7-0`.
20. **An arrival's item lists reached its client twice (DESIGN 1.42, package 0.97.27).** The rig regression on the code
    of 1.41 failed `splitbuild`: after a re-login that landed on a neighbour (the base cell was still re-registering after
    its restart) and was handed on at once, the tester's bag showed every entry twice, 38 scrolls for 19, while every cell
    and the database held 60 entries. 1.26's registration-time flush sent the player's queued spawn through the kit's
    per-player send, outside the kit's sync pass, so the operations that had filled the promoted puppet were not dropped
    and the next tick delivered them on top of the spawn's initial state. It happened on every promoted arrival; the kit's
    next whole-list assignment (a storage move) repaired it, and only the re-login path had none. The flush now runs the
    kit's whole pass (`ProceedServerGameStateSync`, `MeshMapNetworkManager_RegistrationSend.cs`). To reproduce or
    re-check: set `MESH_TEST_LOGIN_VIA=N` in the editor's environment (`execute_code`, before Play: it routes every
    login of the map through N while N is live) and run `regress.py splitbuild`; read the tester's `buildtree: bag changed`
    lines (filled slots with ids) and the cells' `Arrival bag` / `Handoff SENT … bag` lines. Before the fix it doubled
    in 2 of 2 forced runs; after, 0 doubled reads in 90. Clear the variable afterwards.

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
5. **Build the map servers:** **Build 8 km Demo Cells and Player** builds `Build/DemoMesh8kCells/` and
   `Build/DemoMesh8k/` (a player, for headless bots). `Build/` is
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
- **Actions across a crossing.** Set `MESH_TRACE_ACTIONS=1` in the editor's process before Play
  (`System.Environment.SetEnvironmentVariable` through `execute_code`; the cells inherit it) and give the patrol
  `attackWhileWalking = true`. Every attack or skill start and trigger is logged with its seed (tag `MeshAction`)
  on the client and in each cell's log, and a carried action shows as `carrying …` on the origin's
  `Handoff SENT` line and `carried …` on the destination. A swing is right when each client trigger is matched by
  exactly one `S` trigger on the cell that owned the player, and by no `S-ghost` one.
  - `swingInGap = true` also swings the moment each hop starts; the destination logs `parked` and `replayed`.
  - A target: set `MESH_TEST_MONSTER=x,z[,name]` (scene frame) before Play and the cell owning the spot spawns
    one monster there, pinned and kept alive, logging every hit (tag `MeshTestMonster`); give the patrol
    `swingFaceRange = 3` to turn to it. `-1.5,512.8` stands beside the crossing point into (3,4).
- **Headless bot** (another player), from `Build/DemoMesh8k`:

  ```
  kit-ce-project.exe -batchmode -nographics -logFile <path> -meshAutoClient -meshAutoMode patrol -meshUser bot2 -meshChannel C -meshWalkX -15 -meshWalkZ 515 -meshReturnX 15 -meshObserveSeconds 60
  ```

  Stop it afterwards: it outlives Play.
- **A seam crossing (DESIGN 1.40).** The seam is at x = 4,096 of `ServerMeshTestMap8k`, which is x = −4,096 of
  `ServerMeshTestMap8kEast` (its frame sits 8,192 m east). Save the character beside it in the database while Play is
  stopped (`characters`: `currentMapName`, `currentPositionX/Y/Z`; the lane is z = 512, the ground y = 21), e.g.
  (4081, 21, 512) on the west map facing east (`currentRotationY` 90). Auto client `walk` with
  `stopWalkOnMigration = true`, `expectedMapName = ""` (any map: the character's map changes with the hop) and the legs
  each in the frame they end in: `walkTargetX = 4111` (15 m past the seam, west frame), `walkReturnX = −4111` (15 m
  back, east frame), `walkTargetZ = 0` (keep the lane). Read `Seam:` lines (`pre-loaded … in N ms over M frames`,
  `swapped … N runtime roots carried`), the `seam:` hop stamps (the own player before and after the carry, the swap's
  timings) and the CONTINUITY line's `moved … (frame shift …)`. An observer: a bot `stand` on the east map at
  (−4093, 517) with `-meshExpectedMap ServerMeshTestMap8kEast` and `MESH_TRACE_OBSERVED=1`; its `MeshRemote` lines show
  the crosser as a ghost at x ≈ −4111 before the line. Since 1.41 the auto client's targets move with a seam's frame
  shift (`Seam: targets moved by the frame shift …` in its log), so `patrol` back and forth over the seam
  (`walkTargetX 4111, walkReturnX 4081, walkTargetZ 512`, crossing every leg) and `flee` across it run too; before, they
  stood after the hop, their target 8 km off. `MESH_TEST_MONSTER=4091,509,BanditMale,free`
  puts a bandit 5 m west of the seam (the spot is each cell's own scene frame, so the east map's cell (7,4) spawns one
  too, 8 km away); `flee` from (4084, 520) to a bait within 3 m of it (`walkTargetX 4093, walkTargetZ 511`) makes it chase.
- **Local test accounts:** meshtest, meshtest2, bot1, bot2 exist only in this machine's
  `mmorpg-demomesh8k.sqlite3`.

## Open items, in rough order

1. **What is left of the gap.** With 1.05 the gap between the warp and the adoption is 63–119 ms on
   localhost (median ~92) and all of it is server frames at 60 fps: enter-game in one, the client's ready in
   the next, the spawn on the sync tick after, plus the client's own frames. Nothing in it is the database or
   the connect. Next steps, in order:
   - an action in flight at a crossing is carried and paid for (1.35): the origin settles the triggers its cancelled
     routine had not reached, and takes a skill item; a charge crosses and shows across a line. A skill in flight has run
     twice in 11 casts of a Cleave made walkable for the test (its stock asset roots the caster): edit `moveSpeedRateWhileUsingSkill`
     and `requireItems`, build, patrol `castSkillWhileWalking`, and restore the asset. Patrol `chargeWhileWalkingSeconds` charges. A skill whose `OnAttack`
     overrides the default attack pays ammunition at a crossing that the kit would not take. The 8 km demo's warrior MP is rage (empty at login,
     10 per hit, drains out of combat): build it by swinging before casting. Game state a cell holds outside the
     kit's data crosses only through an `IMeshHandoffCarrier` (1.22; the demo's combat timer is one);
   - remote motion (1.17–1.20): clients draw other characters from their states' timestamps, an arrival's first
     snapshot carries its crossing velocity, puppets extrapolate stamped snapshots with a two-interval velocity,
     and cells send client-driven players along their clients' stamped trajectories, gliding an arrival from its
     walk onto its client's states: no stall in 180 s, 50 ms speeds within ~3.3–4.7 m/s for a 4 m/s walk on both
     sides and just after an arrival. Left: neighbours' snapshots still read the kit's transform; one hop of 107 ms
     in the last run (median unchanged at 52) was not looked into; the 30 Hz puppet stalls of 1.16 were not
     re-measured. The BaseDemo-CE rig's maps are Static and stay steered on purpose (its prefab saves Steered, as the measurement baseline), so they move in bursts across a line;
   - a cell's first player costs it one 80–95 ms frame (2026-10-29, the 8 km demo): its first registration, login or
     arrival alike (the promotion 27 ms against 5, the frame ~96 ms against a few), and its first demotion another ~80;
     after a login the same cell's first arrival costs ~22 ms. It is Mono compiling the kit's registration and spawn
     code on first use, spread wide: a warm-up over the first player ghost (its whole spawn state written, its data
     copied and a handoff state round-tripped, its caches rebuilt) took the frame only to ~83 ms and the session's
     first hop from 169 to 152 ms, and was not kept. On a long-lived cell it is once per process; on a Dynamic map's
     on-demand cell it is the hop that woke it. An IL2CPP cell build cuts it (1.31: first hop ~104 to 52 ms, a cell's worst first-window frame ~103 to 38 ms) and leaves later hops alone; the Server
     subtarget alone halves a cell's CPU, so the demo's cells use it (1.32). A spawner cannot run on IL2CPP (1.31), so IL2CPP cells mean a Mono spawner beside them;
   - what is left of the gap after 1.26 (median ~28 ms on one machine): the client's warp and connect, the
     enter-game, the registration, one transport pass and a client frame; the session's first hop is the cells'
     first-use compilation (above).
   On Mono the first hop of a session still compiles the hop's first-time code (12.6 ms, the adoption read
   is warmed up); an IL2CPP client has no JIT, but this Unity install's Windows support has no IL2CPP player, only the Server subtarget's.
   Real latency (1.30, `MESH_CLIENT_LATENCY_MS=<one way>` in the editor's environment before Play): the gap is one round
   trip plus ~25 ms, on foot and mounted, up to the 200 ms measured. An observer of a mounted rider still sees a stand or two and ~10 small steps per 100 s, away from the line.
   A real network (1.35, `MESH_CLIENT_NET=<one way>,<jitter>,<loss %>[,<outage ms>]` for the crosser in the editor's environment and for a bot in its own, a UDP relay under LiteNetLib):
   on 30/8/1 % the gap is 110 ms (the session's first hop 161) and an arrival steps at most 0.19 m; on 100/40/5 % it is 284 ms (first hop 309), max ~1.3 s, and stalls and lunges near the line are close to those away from it, in about 150 s a run.
   Compare near with away per metre, and read an arm of 150 s or more: two of 90 s differed by 2x. If a destination dies in the notice's window the crosser stands about 2.4 s and goes back 9.5 m (a crash path).
   Settings (1.34): a new map manager's puppets are kinematic; the demo's 60 fps and 60 Hz stay the project's choice, and Setup warns with the numbers (gap 55 to 29 ms, about
   40 % more cell CPU, traffic not measured). A ~510 ms hop now and then is the editor client's own GC (`with a GC` on the CONTINUITY line), not the crossing.
   A local chat reaches the players across a line, and an NPC dialog or a player or guild storage open at a hop crosses with it (1.37,
   `MeshInteractionHandlers.cs`: the kit's chat and storage handlers wrapped in `SetServerHandlersRef`, and the first built-in
   `IMeshHandoffCarrier`). The 8 km map's banker and merchant stand on the line beside the start to try it: talk to the banker from one
   side, step over, open the strongbox, step back. A chest's storage still closes at a hop; a trade, a duel and a party or
   guild invitation with a player across a line work since 1.44 (item 22, all four in `regress.py social` since 1.45), and
   friend requests, a player's shop and a crafting queue since 1.46 (item 24), with a purchase across a hop, a shop
   moved by a split or merge and a workbench's queue run since 1.47 (item 25), and a purchase or a trade that loses an
   answer or a party since 1.48 and 1.49 (items 26 and 27).
   A corpse in the band is a puppet with its items since 1.38 (`GhostKind.Container`), and a loot on it goes to its owner; a
   PK kill across a line credits the killer on its own cell (`GhostAction.PkKill`). Both measured, the loot with a pinned test
   deer (`MESH_TEST_MONSTER=-1.3,540,Deer` on the cell start) killed from across the line. A shop dialog crosses a hop and a
   purchase on the new cell is served. Passengers on a vehicle have no content: the demo's horse has one seat.
   - A seam between the two 8 km maps (1.40, 1.41): the hop is the usual one (adopted 154–202 ms, the character
     0.35–0.76 m on). The spawn areas live in a server-only scene the cells load and a client never does (1.41), so the
     pre-load is 45–59 ms over 2 frames with 7–8 roots (1.40: one 87–101 ms frame for 2,300 objects). The longest hop
     frame is now the one after the swap, 63–70 ms: the kit's `Assets.Initialize` and the pool init of its online-scene
     handling — the next lever. The session's first hop still has its 123–134 ms connect frame (first-use compilation).
     A corner where four maps meet is run (1.43): hops straight through the point between cells that touch there, three
     scenes pre-loaded at once; a corner with an empty quadrant is the edge of the world: a step into it stays on the cell
     it came from, and the world's own edge stops it, as at any map edge (1.50, a test pins it).
     A death on the east map respawns on the west start (the kit's save point).
2. **Monsters at a crossing (1.23, 1.28):** a lone monster chases a player across a Static map's line, hits
   through the ghost, and is handed over with its target; a monster that walks over a line on its own walks on
   (1.28: the handoff carries its walk and its AI's wander bookkeeping), and monster puppets are led by the NavMesh
   agent's velocity. Chase (1.28, the user: running should not lose a monster, sprinting should): the demo copy's rule
   (`DemoMesh8k-GameplayRule.asset`: `chaseSpeedOfTargetRun` 1.1, `chaseLoseRange` 10 m, `chaseLoseSeconds` 2 s)
   replaces the kit's follow timer; about 7 s of sprinting loses a bandit. Recipes in the 1.28 row (a deer on the line
   for wandering, a free bandit and auto client mode `flee` for chases; `MESH_TEST_MONSTER=<x>,<z>,<name>,free,<count>` is
   a pack, and 4 bandits crossed both ways with their target, 1.30). Left: the kit's 50 m leash still ends any chase; the
   kit's own demo and the BaseDemo-CE rig keep the kit's timer.
3. **Mount and pet crossings (1.24, 1.27, 1.28):** both run on the 8 km demo (HorseWhistle and PupCollar in MeshTester's
   bag; patrol `mountUp` / `summonPet`). A mounted rider crosses clean for itself and for an observer at the line (1.27).
   A passenger behind a driver crosses too (1.39): the demo copy's two-seat `DemoHorse2` and `HorseWhistle2` (made by
   `CreateTwoSeatHorse`, in bot7Char's bag), the driver on `patrol -meshMount` across the line, the editor's `stand` client
   at (12, 512) with `boardSeat` 1; read its `passenger:` lines, the cells' `Arrival ... mount=passenger` and `seated behind`
   lines, and the database position against the client's. A seat the new cell shows free is let go by the client within 8 s.
   Pet puppets have kinematic bodies (1.28, `kinematicPets`); an observer still sees a pet stall about once a second,
   the kit's stop-and-go pet follow seen through 10 Hz snapshots, on both sides of a line. Observer recipe: the editor
   patrols ±12 m at z 513; a bot from the build stands at (−3, 517) with `MESH_TRACE_OBSERVED=1`
   (`-meshAutoMode stand -meshWalkX -3 -meshWalkZ 517`). Read its `MeshRemote` lines as well as `MeshObserved`: the
   stall trace follows only characters within 30 m, so a character sent to a garbage position never shows there.
4. **The kit copy is upstream `master` at 493df35fc (1.33).** CE-14 is upstream (PR 28) and no `MESH-EDIT` marker is left. No
   release has it yet, so the package names `master` at 05ec55012 as its minimum: when a release does, name that. A later
   sync is a three-way merge, not a copy (`upstream/README.md`).
5. **The rig regression** is run on the paths a change reaches, not in full (the user's choice, 2026-11-02). On the
   code of 1.53: `vendleave` 60, `buystop` 61, `salestop` 62, `sleephold` 55, `trade` 82 and `tradestop` 66 checks. On the
   code of 1.52: `tradestop` 66, `trade` 82, `market` 84, `social` 70, `splitvend` 116, `sleep` 57 and `merge` 144
   checks. On the code of 1.51: `trade` 82, `market` 84 and `social` 70 checks. On the code of 1.50 (the server code of 1.49): `splitvend` 116 checks, and `pet` and `party` 109 in a full run stopped after
   them; on 1.49's `trade` 75, `social` 70 and `market` 83, all passing (2026-11-02), as on 1.48's `market` 83, `social` 70 and `splitvend` 116, and on 1.47's `market` 70, `splitvend` 116, `social` 70 and `harvest` 47 (the code
   every scenario runs changed by one lookup in the boundary tick). The full run below is the 1.46 code's. It passed all 35 scenarios on the code of 1.46, `social` at 70 checks (2026-11-02, 3,216 checks, 175 min, every scenario on its
   first run; 27 fewer per split or merge scenario than 1.45 only because that run crossed midnight and also checked the
   8 km demo map's logs of the day before for "no own-cell layout"), as it had all 35 on the code of 1.45 (3,694 checks, 230 min) and all 34 on the code of 1.44 (2026-11-01, 3,658 checks, 205 min; `party`'s flaky
   `hurtTarget` check failed in the full run, the fighter having killed its monster in one hit, and `party` passed 55 of 55 alone right
   after), and on the code of 1.42 the same day (3,146 checks, 203 min), 1.39 (3,488) and 1.35 (3,178), and the 3 h `soak` 95 of 95 on the code of 1.35. The run on the code of 1.41 (26 of 34) was the clock-change hour and the item-list defect of item 20, not the rest. To run it:
   enable the five BaseDemo and TestMap scenes in Build Settings, open BaseInit-CE, then
   `python -u Tools/ServerMeshing/regress.py --build <scenarios>` (name the scenarios: `--build` alone only builds), then
   restore Build Settings and DemoMesh8kInit. `Config/mySqlConfig.json` is here and MySQL80 runs. A full run takes ~3 h.
   To change a setting for a run pass `--env KEY=VALUE`: a variable set in the editor did not reach the cells (1.34). Confirm it in the cell logs before trusting a pass
   (`Puppet motion kinematic (from ...)`, `Puppets window`).
6. **Run the full EditMode suite** after any further core edit (4,503 tests, all green since the add-on's rows joined
   `MessageRoundTripTests` on 2026-10-31, item 19; `WebSocketClientSendTests.
   ReusedWriter_SendsOwnedPayloadsInOrder` is timing-flaky and passes alone).

## Gotchas found on the way

- **Do not run the rig regression across a daylight-saving change.** The kit stamps local time and the grader compares
  stamps (`zone_lines`), so when the clock fell back on 2026-11-01 (02:00 → 01:00) the hour 01:00–02:00 happened twice
  and every scenario in the second hour read the first hour's cell lines as its own: 6 process lifetimes for 1, layouts
  of earlier coordinator incarnations, 11 `LayoutHello`. Seven scenarios failed that way with nothing wrong in them.
- **A kit-file edit makes the player build stale for the harness** (`rigtools.build_staleness` reads the whole tree):
  `--allow-stale-build` when the change is client-only, else `--build`.
- **Assets loaded before `OpenScene` go dead.** Opening a scene in an editor script unloads unreferenced
  assets, so an asset loaded earlier is dead after it; assigned, it saves as nothing. That is how every
  8 km spawn area once had no monster, and the demo copy no gameplay rule. Load after opening.
- **The demo's server prefab starts map servers windowed in the editor** (`editorNotSpawnInBatchMode: 1`).
  The demo copy overrides it in its scene.
- **The demo plays footsteps from foot plants, not the kit's timer** (`stepDelay` is set to "never").
- **The login server, not the central, creates characters** (`ILoginServerDataManager`).
