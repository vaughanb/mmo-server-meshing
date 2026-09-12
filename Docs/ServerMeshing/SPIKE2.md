# Spike 2 — Client connection migration (make-or-break)

> Goal: determine whether a connected client can be moved from cell-server A to
> cell-server B **without a visible loading screen, black frame, or teleport** —
> the assumption the whole meshing design rests on. See [DESIGN.md §15](DESIGN.md#15-phase-0-spikes).

| | |
|---|---|
| **Status** | **RUN 2026-09-12 — real A→B zone hop works end-to-end, extension-only; masked gap ≈ 2.1–2.2 s (functional PASS, perceptual target not yet met)** — see [Results](#0-results-2026-09-12) |
| **Owner** | brenden.vaughan@gmail.com |
| **Code** | `Assets/ServerMeshing/Spike2/` (+ rig in `Assets/ServerMeshing/TestMap/`) |
| **Resolves** | DESIGN.md [Q5](DESIGN.md#18-open-questions) (measured), informs [CE-1](DESIGN.md#17-core-edit-registry) |
| **Runs after** | **Spike 0** (coordinate/movement serialization across two cells) — migration is meaningless if positions don't survive across cells |

---

## 0. Results (2026-09-12)

**Rig (all automated, no manual UI):** editor runs DB (SQLite) + Central + Login + MapSpawn from
`BaseInit-CE`; MapSpawn launches two zone map servers from the player build (`Build/Server/kit-ce-project.exe`)
for the *same* logical map `ServerMeshTestMap` under channels **C** (zone (1,1), port 8000) and **E**
(zone (2,1), port 8001). The editor is also the client, driven by `ServerMeshAutoClient` (login → channel C →
character → enter map → walk east). Zone servers resolve their own zone from `ChannelId == zoneId` and learn
each other's address from the cluster's map-server broadcast (no fixed ports).

**Trigger:** the **server-side boundary tick** in `MeshMapNetworkManager` (DESIGN §11 trigger #1). When the owned
player is > 1 m past the C/E edge (x = 100), server C replays the kit's private `SaveAndWarpCharacterByPeerInfo`
(unregister → `WaitAndSaveCharacter(changeMap, mapName, position)` → `NetworkDestroy` → `MMOWarpMessage` to the
peer `E_ServerMeshTestMap`). The client's `HandleWarpAtClient` override runs the masked migration.

**Measured (run 3, walk C→E):**

| Step | Time from migration start | Notes |
|---|---|---|
| mask captured | 38 ms | |
| `StopClient` returned | 52 ms | offline-scene reload suppressed; `Assets.Clear()` still runs |
| connected to E | **53 ms** | reconnect itself is negligible |
| local player back (`playerReady`) | **2228 ms** | the gap to hide |
| mask faded | 2490 ms | |
| position before → after | (101.33) → (101.01), **jump 0.32 m** | walked ~0.3 m after the server snapshot; no teleport |
| server C handoff (save + warp msg) | 102 ms (501 ms in a later run) | `WaitAndSaveCharacter` |

**Where the 2.2 s goes (ms-stamped `MeshTrace` on both sides, run 4):**

| Segment | ≈ ms | What |
|---|---|---|
| connect → enter-game validated | 194 | E: `ValidateAccessToken` + `GetCharacter` (2 DB round trips) |
| enter-game response → client `SendClientReady` | **~1020** | client: `Assets.Initialize()` (`RegisterPrefabs` + scene objects) + `InitPoolingQueues` + `OnClientOnlineSceneLoaded` — all re-done because `Assets.Clear()` wiped them |
| client ready → player registered on E | **~615** | E: `LoadPlayerCharacterEntityRelatesData` (gold, cash, user level, party/guild, storage) + `GetSummonBuffs` — ~5 sequential DB round trips |
| spawn → client sees entity | ~250 | AOI subscribe + first sync |

**Also observed:** on map disconnect the kit's `MMOClientInstance.OnMapStopped` immediately restarts the *login*
client and the enter-game success stops it again — harmless but noisy during a hop.

**Optimisation pass 1 (same day, extension-only) — round trip C→E→C, run 6:**

| Change | Effect |
|---|---|
| `fastClientReadyOnMigration`: during a hop `MeshMapNetworkManager.OnClientOnlineSceneLoaded` re-implements the core's hook without the 1 s `INSTANTIATES_OBJECTS_DELAY` settle window (back-dates the protected `_clientSceneLoadedTime`) and without `RegisterEntities`' forced `GC.Collect()` | enter-game response → `SendClientReady` went from ~1020 ms to **~10 ms** |
| Corrected `playerReady` signal: the old entity is `Destroy()`ed but survives to end of frame, so "non-null" fired at 11 ms; now requires a **new** entity instance | earlier 11 ms readings were false positives |
| Result | **C→E `playerReady` = 1107 ms, E→C = 1384 ms** (from 2228 ms); jumps 0.48 m / 0.45 m; walk continues in the new zone; round trip **PASS** |

Remaining gap ≈ 50 ms connect + ~245 ms enter-game (2 DB round trips + RTT) + **500–620 ms player-ready DB
loads** (gold, cash, user level, party/guild, storage, summon buffs) + ~200–300 ms spawn propagation.

**Optimisation pass 2 — why every DB round trip cost exactly ~100 ms.** Not the DB tick rate (`updateFps`
20→100 Hz: no change), not the server frame rate (`serverTargetFrameRate` 30→120: no change), not the editor
(~300 fps). The floor is in the kit's request API: `LiteNetLibClient/Server.SendRequestAsync` waited for the
response with `do { await UniTask.Delay(100); } while (!done);`. Replaced by `await UniTask.WaitUntil(() => done)`
— **CE-6**, a 2-line documented core edit (see DESIGN §17), the only non-extension change so far and an obvious
upstream PR.

| Run 8 (CE-6 applied), round trip | C→E | E→C |
|---|---|---|
| connected | 22 ms | 10 ms |
| server enter-game (validate + load) | 92 ms | 58 ms |
| server ready → registered (relates data) | 292 ms | 160 ms |
| server handoff save + warp msg (sending cell) | 35 ms | 43 ms |
| **`playerReady` gap** | **648 ms** | **450 ms** |
| position jump | 0.28 m | 0.35 m |

From 2228 ms to 450–650 ms with two changes: skip the client's post-scene-load settle+GC during a hop
(extension-only), and remove the 100 ms request-poll floor (CE-6). Remaining cost is real DB work plus RTTs; the
in-memory S2S state transfer (DESIGN §11 step 2) would remove most of the ~160–290 ms relates-data reload, but
the kit's pending-spawn dictionary (`_pendingSpawnPlayerCharactersByUserId`) is private, so that needs either
reflection or a small documented core seam (candidate **CE-5**).

**Pass 3 — "completely seamless" (same day).** A held screenshot plus the kit's warping indicator still read as a
blip. Replaced the mask with a **seamless strategy** (`useVisualStandIns`, default on) that never stops rendering
the live scene:

| Mechanism | How (extension-only) |
|---|---|
| World keeps rendering | The scene is never unloaded; before `StopClient()` every spawned entity's model is cloned into a script-free **stand-in** (Animator kept, kit scripts and colliders stripped) and dead-reckoned with the velocity the controller tracked every frame |
| Camera never disappears | The kit's controller (+ gameplay camera + HUD) is instantiated by the player entity and destroyed with it; we remove it from the dying entity's `InstantiatedObjects`, disable its scripts for the gap, and point the follow camera at the local stand-in. `framesWithoutCamera = 0` on every run |
| No warping UI | The mesh handoff no longer raises `IsWarping` (the entity is unregistered and destroyed within ms anyway) |
| Input resumes early | As soon as B's owned entity exists, `GameInstance.PlayingCharacter` is set and the controller scripts re-enabled — the new entity is already walking when the models swap |
| No pop | B saves `pos + velocity × handoffLeadSeconds` (0.4 s) so it spawns the character roughly where the stand-in will be; then the hidden real model is revealed only after a 0.3 s **blend** that glides the stand-in (position + model orientation) onto it |
| HUD never shows 0/0 | The old HUD's kit scripts are frozen during the gap (keeps last values); the re-bind that re-creates the HUD waits until the new entity's stats have arrived (`MaxHp > 0`, ~250 ms after spawn); notification badges are reset so the prefab's "99+" placeholder never flashes |
| Verified by eye | `debugCaptureMigrationFrames` dumps a frame every ~17 ms across the swap to `Logs/MigrationFrames`; inspected sequences show the same scene, character, camera and HUD values across the whole swap |

Run 12 (C→E): stand-ins 9 ms · connected 30 ms · new entity 564 ms · settled 878 ms · retired 1065 ms;
server-side continuity 0.92 m, stand-in vs spawn 0.76 m (absorbed by the blend); 5 stand-ins; 0 frames without a
camera; no errors. Cost: the player has no input for ~0.55 s and the stand-in dead-reckons over it.

**Pass 4 — shrink the no-input window (same day).**

| Change | Effect |
|---|---|
| **Handoff notice** over the CellMeshLink: the sending cell tells the neighbour "character X (user U) is coming" right after the save | the receiver knows an arrival is a handoff, not a fresh login |
| `LoadPlayerCharacterEntityRelatesData` is **protected virtual** in the core, so `MeshMapNetworkManager` overrides it: for a noticed arrival the relates data (gold, cash, user level, party, guild, storage) load in the **background** instead of blocking the spawn (`fastSpawnOnHandoff`) | server "client ready → player registered" **160–290 ms → 40–60 ms** |
| **Input prediction on the stand-in**: during the gap the local stand-in follows the movement keys (camera-relative, at the character's move speed), so the player never loses control; with no input it dead-reckons | the ~0.25 s server round trip is no longer felt |

Run 15 (round trip): `playerReady` **271 ms (C→E) / 238 ms (E→C)**, settled 572 / 539 ms, stand-in vs spawn
0.04 / 0.19 m, 0 frames without a camera, no errors. CE-5 is **not needed**: the seam existed.

Verdict for Q5: **seamless is achievable extension-only** (no screenshot, no loading UI, no camera gap, no
HUD flash, input never lost). CE-1 stays *not needed*. Remaining polish: `handoffLeadSeconds` (0.4 s) is now
longer than the gap and could drop to ~0.25 s; other players' stand-ins are dead-reckoned only.

**Conclusions.**
1. The make-or-break question is answered **yes**: a connected client can be moved A→B with the kit's own
   session-resume path, zero core edits, position continuity within one movement step, and the player keeps
   walking in B (also confirmed E→C at login when a character was saved inside another zone — routing by
   position works).
2. The gap is **not** a scene reload (same scene, none happened) — it is client prefab/pool re-initialisation
   (~1 s) plus DB reloads on B (~0.8 s). Both are addressable without CE-1: keep the prefab registry/pools warm
   across a migration (extension seam on `LiteNetLibAssets` to investigate), and hand relates-data over S2S
   (DESIGN §11 step 2) instead of re-reading the DB.
3. The screenshot mask hides the visual break, but ~2 s of frozen input is perceptible → **Marginal** per §5.
   Optimise the two hot spots before escalating to R/CE-1.

**Setup facts recorded:** demo map id must be set (`ServerMeshTestMap-MapInfo.id`) or map servers NRE in
`PreSpawnEntities`; new characters start on the test map via `BaseCharacter-CE.startMap`; monster spawn areas in
the test map are deactivated for this rig; a dead character is loaded dead and must be respawned first.

> **Scope note (review #2):** `StopClient()` clears `Players` *and* `Assets`, so a successful swap is
> broader than "don't reload the scene." The new cell must complete **session resume (re-auth),
> ready state, player-handler registration, and ownership binding** — all *within the masked window*.
> The kit's existing warp path already performs these on reconnect; the spike reuses it, so the test
> is whether they finish fast enough to hide, **not** whether we re-implement them. Local object-id
> continuity is intentionally **not** preserved (B re-spawns) — that's only required by the
> registry-preserve path (CE-1).

---

## 1. Verified findings (from reading the kit — already part of the spike)

| Finding | Evidence |
|---|---|
| Client warp = `StopClient(); StartClient(addr,port)` on the singleton manager | `MapNetworkManager.cs:852-859` (`HandleWarpAtClient`) |
| `StopClient()` → transport disconnect → `OnStopClient()` | `LiteNetLibManager.cs:305-317` |
| `OnStopClient()` **destroys the entire entity world**: `Players.Clear(); Assets.Clear();` | `LiteNetLibGameManager.cs:516-526` |
| `Assets.Clear()` → `ClearSpawnedObjects()` `Destroy()`s every non-scene entity (and resets ObjectId) | `LiteNetLibAssets.cs:111-124, 216-229` |
| `loadOfflineSceneWhenClientStopped` gates **only the offline-scene reload**, *not* the `Assets.Clear()` | `LiteNetLibGameManager.cs:523` |
| The kit override does `base.OnStopClient()` **then** clears — so a subclass **cannot cleanly skip just the clear** (can't call `base.base`) | `LiteNetLibGameManager.cs:516-526` |

**Consequence:** "preserve the spawned-object registry across the swap" is **not achievable as a
pure add-on** — it needs a core hook (e.g. a `protected virtual bool ShouldClearAssetsOnStopClient()`),
which would be **CE-1**. And even if preserved, server B re-spawns with its own `uint` ObjectIds →
duplicates. So registry preservation is the *expensive* path.

---

## 2. Strategy decision

Three ways to make the swap seamless, cheapest first:

| # | Approach | Core edit? | Spike order |
|---|---|---|---|
| **M — Mask the reload** | Hold a full-screen snapshot (screenshot) over a *normal* stop/start while B re-streams; fade out once the player is back. Accepts the teardown, hides it. | **None** | **Spike this first** |
| R — Reconcile registry | B is told what the client already holds (via `GlobalObjectId`) and suppresses re-spawn / transfers ownership in place. | Likely none, but large protocol | Only if M's gap is too long to mask |
| P — Preserve registry | Skip `Assets.Clear()` during migration and reuse objects. | **CE-1** (core hook) + ID reconciliation | Last resort |

**We spike M.** It is fully extension-only and directly measures the real perceptual gap. If M's
masked gap is imperceptible (target < ~400 ms, no scene reload when A and B share a scene), CE-1 is
**not needed** and the design stays 100% add-on. If M is visibly too long, we escalate to R, then P.

---

## 3. What the scaffold does

- **`MeshMapNetworkManager : MapNetworkManager`** — swapped onto the map-network-manager prefab
  (resolved by `GetComponentInChildren<MapNetworkManager>()`, no core edit). Overrides
  `HandleWarpAtClient` so an in-grid warp routes through the migration controller instead of the
  raw `StopClient(); StartClient()`.
- **`ClientMigrationController`** — captures a screenshot mask, runs the stop/start, polls for
  "connected to B" + "local player re-spawned", fades the mask, and **logs per-step timings**.

No core files are modified. Markers: none needed (extension-only). If you later add CE-1, mark it
`// MESH-EDIT: CE-1 …` and log it in DESIGN.md §16.

---

## 4. In-editor setup

> The spike reuses the kit's real warp path, so it needs the MMO server stack. Easiest rig: two
> map servers running **the same scene** so no scene reload is involved — isolating the
> connection-swap cost.

1. **Swap the manager.** On your map-network-manager prefab/scene object, replace the
   `MapNetworkManager` component with `MeshMapNetworkManager` (keep all serialized fields). Confirm
   `MMOClientInstance` still resolves it (it uses `GetComponentInChildren<MapNetworkManager>()`).
2. **Two same-scene servers.** Launch two map servers for the *same* map/scene on different ports
   (e.g. via your usual MMO server launch + a second instance on another port), both registered to
   the cluster. (Or, for a first smoke test, two standalone servers and a manual migrate call —
   see step 4 alt.)
3. **A way to cross.** Put a warp portal (or use a GM warp) whose destination is the *other*
   server's same-scene map, so walking it triggers `HandleWarpAtClient` with B's address/port.
4. **Run the client**, connect to A, walk through the portal. The controller masks the swap.
   - *Alt (no portal):* call `meshManager.DebugMigrate("127.0.0.1", portB)` from a debug key to
     drive a migration directly without server cooperation.
5. **Read the console.** Each migration logs a `[Spike2]` line with timings.

---

## 4b. Setup walkthrough (rig options)

The masked migration reuses the kit's **real** warp/enter-game path, so you need a working MMO server
stack (Central + Cluster + Login + MapSpawn + map server(s) + DB) — i.e. however you already launch the
MMO. The meshing-specific steps are small; the two rig options differ only in what they isolate.

**Common steps (both options):**

1. **Swap the manager.** On your map-network-manager prefab/scene object, replace the
   `MapNetworkManager` component with **`MeshMapNetworkManager`** (keep every serialized field value).
   `MMOServerInstance`/`MMOClientInstance` resolve it via `GetComponentInChildren<MapNetworkManager>()`,
   so the subclass is picked up automatically. Confirm `Use Masked Migration = true` in the inspector.
2. **Start your MMO server stack** as you normally do (editor MMO tools / server build / `MMOServerInstance`).
3. **Run a client**, log in, and select a character.

**Option 0 — minimal rig (recommended FIRST: self-migration, one map, no build).**
- The make-or-break question is "can the client tear down and re-establish its connection + world
  without a visible break." You can measure that by **migrating the client back to the *same* map
  server** — it exercises the full masked cycle (mask → `StopClient` clears world → reconnect →
  re-auth/resume → re-stream → fade) and gives the `playerReady` number, with **only one map server**.
- Rig: run **everything in one editor process** — on `00Init_MMO`'s `MMOServerInstance`, enable
  `startDatabaseOnAwake`, `startCentralOnAwake`, `startLoginOnAwake`, `startMapSpawnOnAwake`, and
  `startMapOnAwake` with a `startingMap` set. Press Play → all roles + one map server, **no `Build.exe`
  needed** (MapSpawn only needs the build to spawn *additional* maps).
- Run the client as a **second instance** (a client build, or a [ParrelSync](https://github.com/VeriorPies/ParrelSync)
  editor clone running `01Home_MMO`). Log in, select a character, enter the map.
- Trigger: `((MeshMapNetworkManager)BaseGameNetworkManager.Singleton).DebugMigrate(thisMapServerAddress, thisMapServerPort);`
  (the address/port the client is already connected to). Read the `[Spike2]` timings.
- If self-migration is seamless, the mechanism works; then graduate to A or B for a real A→B hop.

**Option A — easiest A→B, end-to-end (includes a scene load).**
- Use a normal two-map setup with a **warp portal** from map A to map B (your existing portal flow).
- Walk through the portal. `HandleWarpAtClient` fires → `MeshMapNetworkManager` runs the masked
  migration instead of the raw disconnect/reconnect.
- This measures *connection swap + character resume + B's scene load* together — a realistic worst case.

**Option B — cleaner measurement (isolates the connection swap).**
- Run the **same map scene under two channels** (different `channelId`, same `mapName`, different ports)
  so both register with the cluster (keys are `{channelId}_{refId}`, so identical maps on different
  channels don't collide).
- Trigger migration directly with the debug hook instead of a portal:
  `((MeshMapNetworkManager)BaseGameNetworkManager.Singleton).DebugMigrate("127.0.0.1", portOfChannelB);`
  (bind to a key, or call from a tiny debug button). Same scene ⇒ no scene reload ⇒ you measure the
  pure connection-swap + resume cost.

> Tip: do **Option A first** to confirm the whole path works visually, then **Option B** to get the
> clean `playerReady` number that decides whether masking is enough (and thus whether [CE-1](#16-core-edit-registry)
> is needed). If Option B's `playerReady` is comfortably under the target, you have your answer.

## 5. What to measure & pass/fail

The controller logs, in milliseconds from migration start:

| Metric | Meaning | Target |
|---|---|---|
| `capture` | time to grab the screen mask | < 1 frame |
| `stop` | StopClient returned | — |
| `connected` | `IsClientConnected` true on B | informational |
| `playerReady` | `GameInstance.PlayingCharacterEntity` non-null again (**the gap that matters**) | **< ~400 ms** |
| `faded` | mask fully gone | playerReady + fade |

**Pass:** `playerReady` is short enough that the screenshot mask hides it with no visible scene
reload, black frame, camera jump, or UI loading screen — **and** session resume, ready state, player
handlers, and ownership binding all complete inside that window (verify the player is controllable and
syncing after the fade) → seamless feel achieved **extension-only**; CE-1 stays "Conditional / not
needed".

**Marginal:** `playerReady` 400 ms–1 s → tune (suppress any loading UI, pre-warm B, reduce ready
criteria) or accept a slightly longer mask.

**Fail:** seconds-long, scene reload occurs even same-scene, or the mask can't hide a camera
discontinuity → escalate to approach **R** (registry reconcile) and re-scope; CE-1 likely required.

---

## 6. Outcomes to record back in DESIGN.md

- The measured `playerReady` gap and whether masking hid it.
- Whether a loading-screen UI still appeared (and what triggered it) — needs suppression if so.
- Whether same-scene migration truly skipped scene reload.
- Final call on CE-1: **not needed** (M works) / **needed** (escalate to R or P).

---

## 7. Known caveats in the scaffold (expect to adjust in-editor)

- `ScreenCapture.CaptureScreenshotAsTexture()` can be vertically flipped on some platforms — flip
  the GUI draw rect if needed.
- "Player ready" is polled as `GameInstance.PlayingCharacterEntity != null`; you may want a stricter
  signal (e.g. first transform sync received) — marked `// SPIKE-TODO`.
- Any kit loading-screen UI shown on warp is not yet suppressed — marked `// SPIKE-TODO`; the mask
  may sit under or over it depending on canvas sort order (the scaffold uses IMGUI/OnGUI, which
  draws above uGUI).
- The scaffold targets confirmed APIs (`StopClient`, `StartClient`, `IsClientConnected`,
  `loadOfflineSceneWhenClientStopped`, `HandleWarpAtClient`, `GameInstance.PlayingCharacterEntity`)
  but has not been compiled in your project — minor signature fixes may be needed.
