# Grid-Based Seamless Server Meshing — Design

> Add-on for **MMORPG Kit Community Edition** (Unity). Goal: players cross
> cell/server boundaries with no portal disconnect, no loading screen, and
> (ultimately) the ability to see and fight across boundaries.

| | |
|---|---|
| **Status** | **Working on the 2-zone rig (2026-09-12):** P0 spikes pass; P1 seamless handoff done (~200 ms, no visible break for the crossing player; observers on either cell see the same entity throughout — no swap, no blink); P2 players + monsters ghost across the edge (shared world frame); P3 hits on ghosts resolved by the owner, kill credit forwarded to the real character, edge monsters aggro on ghosts, monsters hand off by position (in place, no pop) with respawn accounting kept by the home spawn area. Open: per-cell coordinate frames (only needed beyond ±960 m), party/guild share for cross-edge kills, projectiles, dynamic cells |
| **Doc version** | 0.19 |
| **Last updated** | 2026-09-12 |
| **Owner** | brenden.vaughan@gmail.com |
| **Scope target** | Fully seamless (Phases 1–3) on a **uniform grid of equal cells** |
| **Build constraint** | **Add-on only — extend/inherit, do not edit core.** See [§2](#2-engineering-principles--core-edit-policy) |

### Changelog

| Version | Date | Change |
|---|---|---|
| 0.19 | 2026-09-12 | **Arrival promotes the ghost in place** (§7): the destination no longer spawns a second entity for an arriving character — it hands the kit its ghost as the "already spawned entity" of the ready path ([CE-8](#17-core-edit-registry): the pending-spawn map is `protected`), after Unghostify + `SetupByMetaData` + `CloneTo` + teleport to the saved position + caches; the kit then re-owns and registers that very object. Two kit-level obstacles: (1) client-authoritative movement kept the previous owner's last accepted position and lerped the re-owned entity back to it while rejecting the new client's syncs — the promoted character jumped 3.5 m back across the line and was handed off again ([CE-9](#17-core-edit-registry): reset on owner change; a kit bug that affects its own re-own path); (2) the kit's AOI defers owner changes to its 1 s pass and only then subscribes the owner, so the arriving client waited ~0.5–1 s for its entity — the mesh applies the owner immediately and calls `InterestManager.NotifyNewObject` on registration. Late snapshots from the departing cell are ignored for a promoted arrival; an unclaimed promotion is destroyed after 15 s. Measured (observer on C, bot C→E→C): **0 swaps, 0 absent frames in both directions**; the bot's own crossings **227 / 196 ms** (best so far), 0 frames without camera. |
| 0.18 | 2026-09-12 | **Observer-side seamless player handoff** (§7/§12): other players used to see a crossing character blink — the departing cell destroyed the entity at handoff and the neighbour destroyed its ghost on the departing cell's GhostRemove, ~250–350 ms before the real one spawned. Now (a) the departing cell **demotes the entity to a ghost in place** (`Assets.SetObjectOwnerImmediately(objectId, -1)` so the client's disconnect does not destroy it, then Ghostify/invincible/hit-forwarding, steered by the new owner's snapshots and removed when they stop), and (b) the destination **keeps its ghost until the real character registers** (the departing cell forgets it ever streamed that character to the destination, so no GhostRemove follows). Measured with the new observe-mode continuity metric (per other player: entity-instance swaps and frames with no entity): observer on C watching a bot cross C→E→C — departure **0 swaps, 0 absent frames**; arrival **1 swap, 0 absent frames** (ghost replaced by the real entity in the same frame at the same spot). The bot's own crossings stayed seamless (246–320 ms, 0 frames without camera). `demotePlayerOnHandoff` toggles it. |
| 0.17 | 2026-09-12 | **Monster handoff by position** (§12): a server-owned monster that walks clearly past the line (hysteresis 1 m, 2 s per-monster cooldown) is handed to the neighbour over CellMeshLink (`MonsterHandoff`: mesh id, entity/data id, level, hp, pose, spawn position, home zone, current target). The owner **demotes its entity to a ghost in place** (Ghostify, invincible, hit forwarding, steered by the new owner's snapshots) and the neighbour **promotes its puppet in place** (Unghostify, re-registers the AI, drops the hit hook) — neither cell's clients see a despawn/spawn — or spawns a fresh one if it had no puppet yet. The chase continues: the target is re-resolved as the real player or its ghost. Respawn accounting stays with the **home** spawn area: the home cell records the lent monster's area/prefab/level/delay; a borrowed monster has no local spawn area and on death the borrower sends `MonsterGone`, which makes the home area `Spawn()` the replacement after the normal respawn delay; a monster that walks home is promoted back with its spawn area reattached. Verified on the rig: E-spawned monsters that landed on C's side handed over at startup; a monster walked home (C→E, promoted in place at E, spawn area restored), chased the player's ghost back (E→C, promoted in place at C with target=the player), died on C, E respawned it 6 s later; three such kills in one run, every respawn accounted for. Cosmetic leftover: the home cell destroys its ghost copy immediately on `MonsterGone` (no death animation there). |
| 0.16 | 2026-09-12 | **P3 second slice — kill credit + aggro across the edge** (§12): (a) `MeshGameplayRule` (a `SimpleGameplayRule` subclass wired into the demo `GameInstance`) intercepts exp/gold/currency rewards the kit hands to a **ghost** on the killing cell and forwards the **raw** amounts (`GhostReward`: exp, gold, currencies, multiplier, given type, giver/source level) to the ghost's owner, which applies them through the same rule to the real character — so rates, guild bonuses and pk reductions use the real character's state and nothing is scaled twice; the ghost never keeps a reward. (b) Aggro: the kit sleeps a monster AI while it has **no network subscribers**, and ghosts never subscribe, so edge monsters froze exactly when a neighbour-cell player looked at them — **CE-7** (one `protected virtual bool IsWatched()` in `MonsterActivityComponent`) + add-on `MeshMonsterActivityComponent` (counts a player ghost within visual range + margin) fix it. Automated fight test (hold the line at x=99, zone C): zone-E monsters chased the player's ghost, 21 retaliation hits E→C landed on the real character (100→0 in the first run, 500→442 in the last); two kills on E → `Reward on ghost … forwarded` ×2 → `Forwarded reward applied … level=2 exp=20 gold=100` on C; the client saw exp+20 gold+100 level+1. Rig tuning: demo monster hp 1→6, player hp 10→500. Observed and left open: an E-owned monster chasing the ghost walked 20 m into C and stayed E-owned (its puppet on C followed fine) — monster handoff by position is a P4 item. |
| 0.15 | 2026-09-12 | **P3 first slice — cross-border hits** (§12): a hit landing on a ghost is forwarded over the CellMeshLink (raw attacker damage amounts, weapon, skill, hit box, seed, attacker's `MeshEntityId`) to the ghost's owner, which resolves it against the real entity with the kit's own `DamageableEntity.ApplyDamage` (internal, reachable) and reports the outcome back so the ghost's cell shows real combat text and hp. Automated: the zone-C client attacked a zone-E-owned monster ghost standing on C's side; E applied 1 → 0, the monster died, the ghost was removed on C. Ghosts stay invincible locally (no double damage, no duplicate loot). Not yet: kill credit/exp to the attacker's real character (the owner sees the attacker's ghost as instigator), projectiles, the local "Immune" combat text on the ghost's cell, and monster aggro against ghosts (demo monsters are passive and die in one hit). |
| 0.14 | 2026-09-12 | **Swap gap 440 → ~250 ms, input never lost** ([SPIKE2.md §0 pass 4](SPIKE2.md#0-results-2026-09-12)): handoff notice over the CellMeshLink + background relates-data loading for noticed arrivals (the core's `LoadPlayerCharacterEntityRelatesData` is `protected virtual`, so CE-5 is **not needed**); movement keys drive the local stand-in during the gap. |
| 0.13 | 2026-09-12 | **Seamless swap (P1 done)** ([SPIKE2.md §0 pass 3](SPIKE2.md#0-results-2026-09-12)): the screenshot mask is replaced by dead-reckoned visual stand-ins, a controller/camera/HUD that survives the entity swap, early input resume, a lead-predicted spawn and a 0.3 s blend; the mesh handoff no longer raises `IsWarping`. Frame captures show no visible break. **Monster ghosting**: spawn areas are owned by the zone they sit in; server-owned monsters are ghosted with birth-provenance ids; ghosts are invincible. Observer saw E's monsters (4) as ghosts from C, two of them physically on C's side of the line. |
| 0.12 | 2026-09-12 | **Spike 1b PASS / first P2 slice** ([SPIKE1.md §0b](SPIKE1.md#0b-results-1b-2026-09-12)): `CellMeshLink` (per-cell `LiteNetLibServer` + per-neighbour clients, built like the cluster link, addresses from the cluster broadcast) and border ghosting in `MeshMapNetworkManager_Ghosting.cs` — owned players within B = 60 m of a shared edge are streamed at 10 Hz with full-precision position + movement state; the neighbour spawns a "puppet" ghost (normal entity, server drivers silenced, movement kept and steered with the kit's own API). A client in zone C saw a bot owned by zone E moving as a ghost (100 distinct positions) without migrating; dedup on handoff-in works. Git repository initialised. |
| 0.11 | 2026-09-12 | **Spike 2 RUN — real A→B zone hop works end-to-end, extension-only** ([SPIKE2.md §0](SPIKE2.md#0-results-2026-09-12)). Implemented the **P1 server-side boundary tick + handoff** in `MeshMapNetworkManager` by replaying the kit's save-and-warp toward the neighbour zone's map server (zone = channel, resolved via the cluster peer broadcast). Automated rig (`ServerMeshAutoClient`, MapSpawn-launched zone servers). Masked gap 2.2 s → **450–650 ms** after (a) skipping the client's 1 s settle + forced GC during a hop (extension) and (b) removing the kit's 100 ms request-poll floor (**CE-6**, 2 lines, upstreamable). Position continuity 0.3 m, no scene reload, round trip C→E→C passes. **Q5 resolved (masking sufficient, CE-1 not needed)**, Q11 resolved for the rig. Core rename noted: `MovementDisablers` → `MovementDisableState` (OpenMMORPG). |
| 0.1 | 2026-06-12 | Initial design |
| 0.2 | 2026-06-12 | No-core-edit constraint, engineering principles, verified seams, core-edit registry; MeshCoordinator → standalone role |
| 0.3 | 2026-06-12 | Review #1: handoff trigger → boundary-tick; ghost simulation-suppression; AOI-cadence in B/H; packaging (Assembly-CSharp); citation fixes |
| 0.4 | 2026-06-12 | Spike 2 scaffolded; confirmed `OnStopClient` clears `Assets` regardless of flag; chose extension-only "mask the reload" → CE-1 conditional |
| 0.10 | 2026-06-13 | Reviewed an externally-built **Spike 2 test-map rig** (`Assets/ServerMeshing/TestMap/`: 3×3 zone grid, server-side zone bootstrap via `-zoneX/-zoneY` args, client-side `ZoneBoundaryDetector` → `MeshMapNetworkManager.DebugMigrate`, editor launcher). Applied fixes: per-zone `-channelId` (cluster keys `{channelId}_{mapName}` collided), boundary hysteresis + cooldown (anti-thrash), and a zone-filter to launch a 2-zone subset. Extension-only (partial-class `ProcessArguments`). Tests migration only — not ghosting/authority. |
| 0.9 | 2026-06-13 | **Spike 1a RUN — PASS** (ghostified a live `BigOrcWarrior`: 4 components silenced, idle drift = 0 m → no self-simulation, extension-only). Q9 resolved. **New decision:** ghost/handoff snapshots carry movement *state* (`movementState`/`extraMovementState`/`yAngle`, already serialized) so ghosts animate, not slide. |
| 0.8 | 2026-06-13 | **Spike 1 scaffolded** (`Assets/ServerMeshing/Spike1/`, [SPIKE1.md](SPIKE1.md)) + **Spike 2 setup walkthrough** added. Verified the suppression mechanism: server logic ticks via a centralized `UpdateManager` (`IManagedUpdate`) gated on `IsServer`, so `enabled=false` is insufficient — silence via `UpdateManager.Unregister` + the `MovementDisablers` seam. No core edit. |
| 0.7 | 2026-06-13 | **Spike 0 RUN — PASS** (config 256/128 on 128/15 grid: no clamping, exact identity + translation; mode-6 ~1.9 mm). Resolves Q10. **New decision:** S2S authoritative transfers use **full-precision** coords; the lossy movement quantization is client-facing only (drift otherwise). |
| 0.6 | 2026-06-12 | **Spike 0 scaffolded** (`Assets/ServerMeshing/Spike0/`, [SPIKE0.md](SPIKE0.md)). Verified exact movement serialization (byte `cellId` + 2-bit mode + quantized X/Z/Y; bit depths 10/4/10·11/10/11·14/12/14·16/16/16; representable range **±960 m** at defaults). Confirms cell+band fits with margin; mode-6 precision ~2 mm. |
| 0.5 | 2026-06-12 | **Review #2 (major):** (a) **coordinate model corrected** — kit already serializes movement as byte `cellId` + quantized local pos (`DefaultGridManagerComponent`), so we use **logical global coords (cell+local) with per-cell-local Unity transforms**, not one global frame; (b) **`GlobalObjectId` replaced** by a stable, owner-independent **`MeshEntityId`** + per-cell mapping (old packing changed on handoff/restart); (c) corrected `ObjectId` claim (getter public; `NetworkSpawn(objectId)` public); (d) new **routing** section (cells vs logical maps); (e) **Spike 0** added; **Spike 2** scope expanded (session resume, handler registration); (f) MeshCoordinator launch honesty (CE-3) |

---

## Table of contents

1. [Decisions log](#1-decisions-log)
2. [Engineering principles & core-edit policy](#2-engineering-principles--core-edit-policy)
3. [Current architecture (verified)](#3-current-architecture-verified)
4. [The core constraint](#4-the-core-constraint)
5. [Target architecture](#5-target-architecture)
6. [World & coordinate model](#6-world--coordinate-model)
7. [Authority model](#7-authority-model)
8. [Global identity](#8-global-identity)
9. [Map-server routing: cells vs logical maps](#9-map-server-routing-cells-vs-logical-maps)
10. [New modules & how they attach](#10-new-modules--how-they-attach)
11. [The seamless handoff](#11-the-seamless-handoff)
12. [Cross-border interaction (Phase 3)](#12-cross-border-interaction-phase-3)
13. [Persistence](#13-persistence)
14. [Risks](#14-risks)
15. [Phased roadmap](#15-phased-roadmap)
16. [Phase 0 spikes](#16-phase-0-spikes)
17. [Core-edit registry](#17-core-edit-registry)
18. [Open questions](#18-open-questions)
19. [Glossary](#19-glossary)

---

## 1. Decisions log

| Date | Decision | Rationale |
|---|---|---|
| 2026-06-12 | **Fully seamless (P1–P3)** on a **uniform grid of equal cells** | Matches intent; deterministic neighbors/coords/ownership |
| 2026-06-12 | **Server-side ghosting + fast handoff, single client connection** (NOT client multi-connection) | Singleton single-connection client; server-local `uint` ids make client stream-merging the worst fit |
| 2026-06-12 | **Logical global coordinates = (meshCellId, localPosition); Unity transforms stay per-cell-local** *(revised in 0.5 — was "one global Unity frame")* | Kit serializes movement as byte `cellId` + quantized local pos (`DefaultGridManagerComponent`); a giant global Unity frame overflows the movement grid and float precision |
| 2026-06-12 | **Stable `MeshEntityId`** (character id for players; coordinator/birth-provenance id for others) + per-cell `MeshEntityId↔ObjectId` map *(revised in 0.5 — replaces packed `GlobalObjectId`)* | The id must survive handoff, restart, and ghost↔real promotion; `(ownerCell<<32)|ObjectId` changed on all three |
| 2026-06-12 | **`currentMapName` stays the logical world map; cells register as map servers keyed by `cellId`** | Don't pollute saved character data with cell ids; keep non-meshed maps and tooling working (§9) |
| 2026-06-12 | **Add-on only — extend/inherit, no core edits unless unavoidable**; package as folder/namespace in `Assembly-CSharp` | Upgrade-safe; kit Core code has no asmdef (§2) |
| 2026-06-12 | **MeshCoordinator = standalone server role** | `CentralNetworkManager` creates `ClusterServer` with `private set`; a separate role avoids subclassing core. *Launch integration may still need CE-3 — see §17* |
| 2026-06-12 | **Primary handoff trigger = per-tick boundary check**; `WarpCharacter` override only intercepts explicit in-grid warps | `WarpCharacter` never fires on a walked crossing (§11) |
| 2026-06-12 | **Client migration: extension-only "mask the reload" first** | Registry preservation needs a core hook (CE-1); masking is zero-edit (§11, [SPIKE2.md](SPIKE2.md)) |
| 2026-06-13 | **Authoritative S2S transfers use full-precision coordinates; movement quantization stays client-facing** | Spike 0 showed the broadcast encoding is lossy (esp. vertical at distance); round-tripping authoritative state through it would drift (§6) |
| 2026-06-13 | **Ghost/handoff snapshots carry movement *state* (`movementState`/`extraMovementState`/`yAngle`), not just position** | The kit animates the model from movement state, not transform deltas; a transform-only ghost slides. The kit already serializes these (Spike 1a) |
| 2026-09-12 | **Cells register as map servers with `channelId = zoneId`, `mapName = logical map`; launched by the existing MapSpawn flow** (resolves Q11 for the rig) | Zero-edit: the cluster already keys peers `{channelId}_{mapName}` and broadcasts them, so a cell finds its neighbour by key. Channels double as zones |
| 2026-09-12 | **P1 baseline handoff = replay the kit's save-and-warp (DB as transfer medium); in-memory S2S transfer is the optimisation, not the prerequisite** | Measured working end-to-end with position continuity (jump 0.32 m). The DB round trips cost ≈ 0.8 s of the 2.2 s gap; S2S state transfer (§11 step 2) is the lever, adopted later |

---

## 2. Engineering principles & core-edit policy

**Hard rule:** implement by **extension and inheritance**, self-contained under `Assets/ServerMeshing/`
(namespace e.g. `MultiplayerARPG.MMO.Mesh`). **Do not edit existing core files** unless there is no
other option.

Unavoidable core edits must be (1) minimal — prefer adding a `protected virtual` hook; (2) marked
`// MESH-EDIT: CE-<n> — <reason>. See Docs/ServerMeshing/DESIGN.md §17`; (3) logged in
[§17](#17-core-edit-registry); (4) upstreamable where possible.

### Packaging reality (verified)

Kit **Core/MMO/Server** code has **no asmdef** → it compiles into **`Assembly-CSharp`**; only
ThirdParty has asmdefs. An asmdef **cannot reference `Assembly-CSharp`**, and we must subclass kit
types that live there. So the add-on is **self-contained by folder/namespace inside `Assembly-CSharp`**,
not a separate asmdef. Adding *new files* is **not a core edit**. Consequence: the add-on can see
`internal` members of MmoKitCE *Core* (same assembly) but **not** of LiteNetLibManager/LiteNetLib.

### Verified extension seams (the toolbox)

Confirmed 2026-06-12 — re-confirm line numbers before relying on them.

| Seam | How it works | Reference |
|---|---|---|
| Interest-manager swap | `GetComponent<BaseInterestManager>()`; falls back to AOI only if none present | `BaseGameNetworkManager.cs:117` |
| ManagerComponents | `GetComponents<BaseGameNetworkManagerComponent>()`, invoked at every lifecycle hook | `BaseGameNetworkManager.cs:120` |
| Dev-ext methods | `InvokeInstanceDevExtMethods("Hook", args)` reflection dispatch | `BaseGameNetworkManager.cs:193`+ |
| Manager subclassing | `MMOServerInstance` resolves via `GetComponentInChildren<MapNetworkManager>()` (matches subclasses) | `MMOServerInstance.cs:87` |
| Overridable warp | `WarpCharacter`, `HandleWarpAtClient` not sealed | `MapNetworkManager_PlayerActivity.cs:36`, `MapNetworkManager.cs:852` |
| Grid manager | `DefaultGridManagerComponent : IBaseGridManagerComponenent`, `Instance` singleton | `DefaultGridManagerComponent.cs:11-14` |
| Spawn with chosen id | `NetworkSpawn(go, uint objectId, long connectionId)` is **public** | `LiteNetLibAssets.cs:415` |
| Ghost suppression | `MovementDisableState` (`StateFlag`, was `MovementDisablers` in MmoKitCE) disables movement; `UpdateManager.Unregister(IManagedUpdate)` silences AI/regen/buff ticks (gated on `IsServer`, so `enabled=false` won't) | `BaseGameEntity.cs` (`MovementDisableState`, `UpdateMovementEnabling`), `CharacterRecoveryComponent.cs` |
| Cluster peer directory | `ClusterClient.onResponseAppServerAddress` is a plain delegate field the map manager assigns in `Start()`; a subclass appends its own listener to mirror `{channelId}_{mapName}` → address | `MapNetworkManager.cs` (`Start`, `OnResponseAppServerAddress`) |
| Save-and-warp replay | `UnregisterPlayerCharacter` (public), `DataUpdater.PlayerCharacterDataSaved` + `WaitAndSaveCharacter` (**internal**, same assembly), `NetworkDestroy`, `ServerSendPacket(..., GameNetworkingConsts.Warp, MMOWarpMessage)` — enough to re-implement the private `SaveAndWarpCharacterByPeerInfo` | `MapNetworkManager_PlayerActivity.cs`, `MapNetworkManager_DatabaseFunction.cs` |
| Per-entity data | Entities are `LiteNetLibBehaviour` stacks on prefabs | entity prefabs |

The bulk of the add-on lives in ManagerComponents, a `MeshInterestManager`, a `MeshMapNetworkManager`
subclass, a `MeshIdentity` behaviour, and standalone networking classes (CellMeshLink, MeshCoordinator).

---

## 3. Current architecture (verified)

| Mechanism | Reality | Reference |
|---|---|---|
| Client connection | **One**, singleton manager. Warp = `StopClient(); StartClient(addr,port)` | `BaseGameNetworkManager.cs:31`, `MapNetworkManager.cs:857-858` |
| Stop tears down world | `OnStopClient()` → `Players.Clear(); Assets.Clear()` (destroys all entities). `loadOfflineSceneWhenClientStopped` gates only the scene reload | `LiteNetLibGameManager.cs:516-524`, `LiteNetLibAssets.cs:111-124,216` |
| Entity identity | `ObjectId` is `uint`; **getter public**, **`internal set`**. Assignable at spawn via public `NetworkSpawn(...,objectId,...)`. Server-local, not globally stable | `LiteNetLibIdentity.cs:169`, `LiteNetLibAssets.cs:415-435` |
| **Movement serialization (grid)** | Positions sent as **byte `cellId` + compression mode + quantized local pos**; grid is finite (`gridSize ≤ 15`, `cellSize` default 128 → ~1920 m/axis) and **clamps** out-of-range coords | `DefaultGridManagerComponent.cs:100-122,137-163`, `GridData.cs` |
| Interest management (AOI) | Server-local; subscribes any nearby `LiteNetLibIdentity` by world pos; `updateInterval` default 1.0 s, `cellSize` default 64 | `JobifiedGridSpatialPartitioningAOI.cs` |
| Cluster role | Directory + social relay; broadcasts every map server's address to all map servers; relays party/guild/chat. No entity state | `ClusterServer.cs:268` *(line not re-verified)* |
| Map-server routing | Keyed `{channelId}_{refId}`, `refId = mapName`; warp/login resolve by `currentMapName` | `ClusterServer` (`MapServerPeersByKey`), `MapNetworkManager_PlayerActivity.cs` (`WarpCharacterRoutine`) |
| Central → Cluster wiring | `ClusterServer` has `private set` (`:51`); created in `Initialize()` (`:112`) | `CentralNetworkManager.cs:51,112` |

> ⚠️ **Three different grids** now exist — keep them distinct ([Glossary](#19-glossary)):
> **AOI grid** (intra-map interest, cellSize 64) · **movement grid** (intra-map position
> compression, byte `cellId`, cellSize 128) · **mesh cell grid** (our *inter-server* partition).

**Leverage:** the AOI subscribes clients to any nearby identity for free → ghosts spawned as normal
identities are seen automatically — **but** the normal prefab also runs server logic, which a ghost
must not (see [risk #2](#14-risks)).

---

## 4. The core constraint

Seamless cross-border view is possible two ways: **(A)** client merges two server streams (worst fit
— singleton client, colliding ids, two scenes), or **(B)** the server ghosts neighbor entities into
the player's current cell over a server-to-server channel, keeping the single connection. **We build
(B).**

---

## 5. Target architecture

**Uniform grid cells + server-side ghosting + fast in-memory handoff.**

```
                       ┌────────────────────────────┐
                       │      Mesh Coordinator       │  ← NEW standalone server role
                       │  adjacency + ownership +     │     (cell↔position lookup, handoff,
                       │  MeshEntityId allocation     │      MeshEntityId / cellIncarnation)
                       └───────────┬─────────────────┘
                                   │   (cells also keep their existing ClusterClient link)
        ┌──────────────┬───────────┴───────┬──────────────┐
        ▼              ▼                   ▼              ▼
   ┌─────────┐    ┌─────────┐         ┌─────────┐    ┌─────────┐
   │Cell(0,0)│≈≈≈≈│Cell(1,0)│         │Cell(0,1)│≈≈≈≈│Cell(1,1)│   each = local Unity frame
   └────┬────┘    └─────────┘         └─────────┘    └─────────┘   (cell + border band)
        │  ≈≈ = CellMeshLink (S2S ghost sync, border bands, coord-translated)
   ┌────┴─────┐
   │  Client  │ ── masked handoff ──▶ (re-points to neighbor; mask hides re-stream)
   └──────────┘
```

- Each cell = one map-server process running `MeshMapNetworkManager` + mesh ManagerComponents (which
  run the **boundary tick**), keyed `{channelId}_{cellId}`, with a **local** Unity coordinate frame.
- **Mesh Coordinator** (standalone role): cell adjacency, ownership directory, **position→cell**
  routing, and `MeshEntityId`/`cellIncarnation` allocation. Cells connect via `MeshCoordinatorClient`
  alongside the untouched cluster.
- CellMeshLink streams border-band entity snapshots between adjacent cells (with coordinate
  translation, §6).
- Ghosts = read-only replicas with authoritative behaviours disabled, spawned as normal identities.
- One simulator per entity; clients send input only to their authoritative cell.

---

## 6. World & coordinate model

The kit **already** serializes movement as a **byte `cellId` + quantized local position** within a
finite, clamping grid (`DefaultGridManagerComponent`: `cellSize` 128, `gridSize ≤ 15` → ~1920 m/axis).
A single global Unity transform space would overflow that grid and lose float precision far from
origin. So:

- **Each mesh-cell server keeps a LOCAL Unity frame.** Its world is authored near origin and spans
  **the mesh cell + border band `B`** (comfortably inside the movement grid's ~1920 m range).
- **Logical global position = `(meshCellId, localPosition)`.** This is what's persisted (for routing)
  and sent in handoff/ghost messages. The Unity transform stays local; the kit's movement grid is
  unchanged.
- **Coordinate translation IS required for ghosts** (this *removes* a benefit claimed in v0.1–0.4):
  a neighbor entity arrives as `(neighborCellId, neighborLocalPos)`; render it at
  `myLocalPos = neighborLocalPos + (neighborCellOrigin − myCellOrigin)`. For a uniform grid this
  offset is a **deterministic constant per adjacent-cell pair**. Ghosts only live in the band, so the
  translated position stays within this server's local frame and movement-grid range.
- **Sizing `B`** must budget for AOI cadence, not just visible range. AOI `updateInterval` (1.0 s)
  means ghost subscription can lag a crossing; a player at speed `v` covers `v·updateInterval`.
  Require `B ≥ maxVisibleRange + maxProjectileReach + v_max·updateInterval + H` (order of 128–192 m
  with `defaultVisibleRange = 80`), and/or add a mesh-specific faster ghost-subscription path near the
  seam ([Q1](#18-open-questions)).
- **Validate per-cell movement-grid config** (`cellSize`/`gridSize`) covers cell + band so band
  positions don't clamp ([Spike 0](#16-phase-0-spikes)). ✅ *Validated 2026-06-13 — 256/128 on the
  default 128/15 grid: no clamping, exact identity + cross-edge translation.*
- **Authoritative state uses full-precision coordinates; the movement quantization is client-facing
  only.** Spike 0 showed the kit's broadcast encoding is lossy (mm near players, but ~8.5 m vertical
  buckets at distance). So **CellMeshLink S2S transfers (handoff state, ghost authoritative position)
  carry `(meshCellId, float3 localPosition)` or a global double — never the compressed movement
  encoding** — else repeated handoffs/ghost round-trips accumulate drift. Clients keep the normal
  compressed stream; ghosts use it like any entity.

---

## 7. Authority model

```
┌─────────────────────────────────────────┐
│   border band (B)  ── ghosts in / out     │
│   ┌─────────────────────────────────┐     │
│   │   INNER region (authoritative)   │     │  ← this cell simulates
│   └─────────────────────────────────┘     │
│   handoff line = cell edge ± hysteresis H  │  ← ownership flips here, rarely
└─────────────────────────────────────────┘
```

- Inner region: fully simulated & owned here. Border band: overlap; ghosts exchanged both ways.
- Handoff line + hysteresis (`H < B`): ownership flips only once an entity is `H` m past the edge, so
  loitering on the line doesn't thrash.
- Invariant: **exactly one cell is authoritative for any entity.** The Coordinator's ownership
  directory is the source of truth; ghosts are never authoritative.

---

## 8. Global identity

The id used to track an entity across cells must be **stable across handoff, ghost↔real promotion,
and server restart.** A packed `(ownerCell << 32) | ObjectId` (v0.1–0.4) failed all three — owner
changes on handoff, `ObjectId` is per-process and reset on restart. Replaced with:

- **`MeshEntityId` — stable, owner-independent:**
  - **Players:** the persistent **character id** (already globally unique; survives everything).
  - **Server-spawned entities** (monsters, drops, projectiles): a **Coordinator-allocated id** at
    spawn, *or* a **birth-provenance** id `(spawnCellId, cellIncarnation, localCounter)` that encodes
    *where it was born* (immutable) — never the *current* owner. `cellIncarnation` (issued by the
    Coordinator at cell startup) prevents collisions after a restart resets the local counter.
- **Per-cell mapping table:** every cell maintains `MeshEntityId ↔ local uint ObjectId` for each
  entity it holds (real or ghost).
- **ObjectId access (corrected):** the `ObjectId` getter is **public**, and
  `LiteNetLibAssets.NetworkSpawn(go, objectId, connectionId)` lets a cell **assign** a local
  `ObjectId` at spawn (`LiteNetLibAssets.cs:415-435`). So we can read and choose local ids; what the
  kit lacks is a *coordinated, stable, cross-cell* namespace — which `MeshEntityId` + the mapping
  table supply. (We never mutate an existing identity's `ObjectId`, so the `internal set` is a
  non-issue.)
- **Dedup / promotion:** a snapshot for `MeshEntityId X` is ignored if this cell already owns X as a
  real entity; otherwise spawn/update a ghost. On handoff-in, replace the ghost mapping with the real
  entity under the same `MeshEntityId`.
- Carry `MeshEntityId` on a `MeshIdentity : LiteNetLibBehaviour` (no edit to `LiteNetLibIdentity`).

---

## 9. Map-server routing: cells vs logical maps

Today the cluster keys map servers `{channelId}_{refId}` with `refId = mapName`, and warp/login
resolve a character to a server via `currentMapName` (`ClusterServer.MapServerPeersByKey`,
`WarpCharacterRoutine → GetPeerInfoKey(ChannelId, mapName)`). Meshing must route by **position within a
logical map**. Plan:

- **Cells register as map servers** with `refId = cellId` (e.g. `"{worldMap}#{cx}_{cz}"`), so the
  existing peer registry/broadcast works unchanged. (Decide whether cells are spawned via the
  existing **MapSpawn** flow or launched directly — [Q11](#18-open-questions).)
- **`currentMapName` stays the logical world map** — don't overwrite saved character data with cell
  ids; keeps non-meshed maps and tooling working. The character's **persisted global position** +
  the Coordinator's cell-grid definition resolve the owning cell.
- **Login / character-enter** must consult the Coordinator: `(worldMap, position) → cellId →
  cell-server address`. This is the key routing integration point and likely touches the
  login/enter-game flow → confirm whether it's reachable via a handler/`ManagerComponent` seam or
  needs a documented core edit ([CE-4](#17-core-edit-registry)).
- **Warp into a meshed map** (from a dungeon or non-meshed map) resolves to the destination cell the
  same way.
- **Non-meshed maps** keep the classic single-map-server path untouched.

---

## 10. New modules & how they attach

| Module | Role | How it attaches | Core edit? |
|---|---|---|---|
| **MeshCoordinator** | Standalone role: adjacency, ownership directory, **position→cell routing**, `MeshEntityId`/`cellIncarnation` allocation, handoff broker | New `LiteNetLibServer` | **No if run as an independent process**; **possible (CE-3)** if wired into `MMOServerInstance`/`ProcessArguments` |
| **MeshCoordinatorClient** | Cell's link to the coordinator | `LiteNetLibClient`, started by a ManagerComponent | No |
| **CellMeshLink** | S2S border-band snapshot streaming (coord-translated) | New `LiteNetLibServer`/`Client` pair | No |
| **MeshInterestManager** | Pull neighbor ghosts near the band; optional faster-than-AOI subscription | Subclass `JobifiedGridSpatialPartitioningAOI`; attach to prefab | No |
| **MeshServerComponent(s)** | **Per-tick boundary check** (primary trigger), drive CellMeshLink, ownership, ghost sync | `BaseGameNetworkManagerComponent` on prefab | No |
| **GhostEntity + MeshGhostController** | Read-only replica; authoritative behaviours disabled ([Q9](#18-open-questions)) | ManagerComponent spawns via `NetworkSpawn`; controller neuters logic | No |
| **MeshIdentity** | Holds `MeshEntityId`; ghost/real tag | `LiteNetLibBehaviour` on entity prefabs | No |
| **MeshMapNetworkManager** | Intercept explicit in-grid `WarpCharacter` + `HandleWarpAtClient` | `MapNetworkManager` subclass on prefab | No |
| **Cell routing hook** | `(worldMap,pos)→cell` at login/enter/warp | handler/ManagerComponent if reachable | **Possible (CE-4)** |
| **HandoffController + client migration** | State transfer + masked client migration | ManagerComponent + subclass | **Maybe (CE-1)** if masking insufficient |

---

## 11. The seamless handoff

### Triggers

1. **Boundary tick (primary — walked crossings).** `WarpCharacter` is never called when a player runs
   across a cell edge (it's the explicit-teleport API: portals/items/NPC/GM/respawn). A walked
   crossing is just movement updates. So a **per-tick boundary check** in `MeshServerComponent`
   compares each owned entity's position to the cell edge ± `H` and begins the handoff.
2. **In-grid explicit warp (secondary).** `MeshMapNetworkManager.WarpCharacter` override: if the
   destination is an adjacent owned cell, run the fast handoff; else `base.WarpCharacter(...)` (real
   map changes keep the classic path).

### Sequence

1. **Pre-subscribe** — the crossing entity is already ghosted into the neighbor and the client already
   renders neighbor ghosts (why `B`/`H` budget for AOI latency, §6).
2. **State transfer (S2S)** — owner serializes live state (not a DB save) over CellMeshLink; neighbor
   instantiates the real entity (via `NetworkSpawn`) in a "received, not active" state at the
   translated transform, under the same `MeshEntityId`.
3. **Ownership flip** at the line — Coordinator records the new owner; old cell demotes its real entity
   to a ghost; new cell promotes its received entity.
4. **Client transport migration** — re-point the client to the new cell. The kit's warp path already
   re-authenticates, resumes the character, sets ready, registers handlers, and binds ownership on the
   new map server; meshing must ensure this completes **within the masked window** (the mask hides the
   re-stream). Local object-id continuity is **not** preserved (B re-spawns) — acceptable for masking;
   only the registry-preserve path (CE-1) needs continuity. See [SPIKE2.md](SPIKE2.md).
5. **Persistence** — new cell becomes owner-of-record; saves periodic + on logout, never per-crossing.

---

## 12. Cross-border interaction (Phase 3)

- **Resolution rule:** the **acting** entity's owner resolves the action and sends an authoritative
  result to the **target's** owner over CellMeshLink, which applies it. Ghosts are never trusted for
  hit detection.
  *Implemented 2026-09-12 with one refinement:* the acting cell does the **hit detection** (against the
  ghost's colliders) and forwards the **raw attacker damage** (the kit computes defence/crit/miss at the
  target); the **target's owner** resolves it with the kit's `ApplyDamage` and reports the outcome back.
  Ghosts are invincible locally, so nothing is applied twice and no loot/exp is duplicated.
- **Projectiles/skills** crossing the seam are handed off like players, or resolved by their owner
  against translated ghost positions.
- **Lag comp across cells** needs neighbor history → ship recent transform history with ghost
  snapshots. **Snapshot rate is a real bandwidth/CPU knob** — tunable per entity class.
- **Ghost colliders/navmesh:** cross-border hits require ghosts to carry colliders — entangled with
  *which* components survive suppression ([Q7](#18-open-questions)+[Q9](#18-open-questions)); central
  to whether P3 is possible.
- **Monsters/NPCs** near a border are owned by one cell, ghosted to the other; aggro must treat
  ghosted players as targets, routing threat across the link.
  *Implemented 2026-09-12:* the kit's own aggro works against a player ghost unchanged (a ghost is a normal
  player entity with colliders on the player layer, `IsEnemy` holds), once the monster is **awake**: the kit
  skips a monster's AI update while it has zero network subscribers, so [CE-7](#17-core-edit-registry)
  makes that check a virtual and `MeshMonsterActivityComponent` also counts a nearby player ghost as a
  watcher. The monster's hits land on the ghost, are forwarded to the owner and applied to the real player
  (the same GhostHit path as player→ghost hits, in the other direction).
- **Kill credit:** the kit rewards whoever is in the monster's damage records — on the killing cell that
  is the attacker's **ghost**. `MeshGameplayRule` (add-on subclass of the demo rule, swapped in on the
  `GameInstance`) intercepts `RewardExp/RewardGold/RewardCurrencies` for ghosts and forwards the **raw**
  amounts plus multiplier/given-type/levels to the ghost's owner over CellMeshLink; the owner applies them
  through the same rule to the real character (real caches, guild, pk state; nothing scaled twice). If the
  owner link is down the reward is dropped, matching what the kit does for a player who logged out mid-kill.
  Not covered yet: party/guild share (the ghost carries no party/guild id, so the killing cell shares
  nothing — the real character's party on its own cell would need the notice), quest kill counters and
  item drops for the ghost (drops spawn on the killing cell, where the real player can walk to them).
- **Monster handoff by position** *(implemented 2026-09-12)*: ownership of a monster follows its position
  like a player's does (same hysteresis, plus a per-monster cooldown against ping-pong on the line). The
  handoff is **in place on both sides** — the owner turns its entity into the ghost, the neighbour turns its
  ghost into the entity — so it is invisible to clients. The kit's respawn is owned by the spawn area, so a
  lent monster keeps a record at its **home** cell; the borrower reports its death (`MonsterGone`) and the
  home area spawns the replacement; walking home reattaches the spawn area. Summoned monsters are never
  handed off (their summoner is a player, and they follow the player's handoff instead — open).

---

## 13. Persistence

- No full save per crossing. **Owner-of-record:** exactly one cell may persist a character; transfers
  atomically with handoff. Saves periodic + on logout + clean shutdown.
- **Crash / cell death mid-handoff:** Coordinator reconstructs owner-of-record (fallback: last DB
  snapshot) **and** reconciles neighbor-side state — promoted/demoted ghosts and **pending/partial S2S
  transfers** must roll back or complete, so no stale real/ghost duplicate remains. The handoff is a
  state machine with a definite commit point.

---

## 14. Risks

1. **Singleton client migration** (no scene reload **or** session/handler loss) — biggest risk, most
   likely core edit ([CE-1](#17-core-edit-registry)). **Spike 2 first.**
2. **Ghost simulation-suppression** — normal prefab runs server logic on spawn (AI/movement/regen/
   buffs); a ghost must neuter authoritative behaviours while keeping transform/visual/collider.
   Mechanism open ([Q9](#18-open-questions)); gates P2/P3.
3. **Coordinate/movement serialization across cells** — the byte-`cellId` quantized movement grid must
   correctly represent cell + band and translate ghost positions; mis-sizing clamps positions.
   **Spike 0.**
4. **Identity stability** — `MeshEntityId` must survive handoff/restart/promotion and dedupe cleanly.
5. **Routing integration** — login/enter must resolve position→cell; possible core touch (CE-4).
6. **Monster/projectile ownership at borders** — double-spawn / duplicate-damage.
7. **DB consistency** through handoff/crash (§13).
8. **AOI subscription latency (1 s)** — fast crossings outrun ghosting; mitigate via `B`/`H` + faster
   path (§6).
9. **Partial failure** — a dead cell must fall back to the classic DB-warp path, not strand neighbors.
10. **Kit-update drift** — mitigated by no-core-edit policy + `MESH-EDIT` markers.

---

## 15. Phased roadmap

| Phase | Deliverable | Felt result |
|---|---|---|
| **P0 Foundations** | Coordinate model (Spike 0) + cell grid; `MeshIdentity`/`MeshEntityId`; routing; 2-cell harness; MeshCoordinator role | Scaffolding |
| **P1 Fast handoff** | Boundary tick + in-memory transfer + masked client migration. *2026-09-12: ✅ boundary tick, save-and-warp replay, seamless client swap (stand-ins, kept controller/camera/HUD, blend) — no loading screen, no stall, no visible break; in-memory S2S transfer still pending (CE-5)* | **No loading screen / stall** |
| **P2 Border ghosting** | CellMeshLink + GhostEntity (suppressed) + MeshInterestManager + coord translation. *2026-09-12: CellMeshLink + player "puppet" ghosts working on the rig (shared frame, no translation yet, no MeshInterestManager — plain AOI suffices at B = 60 m); monsters/NPCs and per-cell frames still open* | Players **see** across |
| **P3 Cross-border interaction** | Authority arbitration, ghost colliders, cross-cell lag comp, mob/projectile handoff. *2026-09-12: hits on ghosts are forwarded to and resolved by the owner (melee, both directions, verified); kill credit forwarded to the real character (verified); edge monsters aggro on ghosts (CE-7, verified); monsters hand off by position in place, respawn stays with the home spawn area (verified). Open: party/guild share and quest counters for cross-edge kills, projectiles, lag comp, summoned monsters following their summoner across* | **See and fight** across |
| **P4 Dynamic load** *(optional)* | Split/merge cells | Elastic scale |

---

## 16. Phase 0 spikes

- **Spike 0 — Coordinate & movement serialization across two cells. ✅ PASS (2026-06-13)** —
  `Assets/ServerMeshing/Spike0/`, [SPIKE0.md](SPIKE0.md#0-results-2026-06-13--pass). A deterministic math test (no servers)
  against the kit's real `GridUtility`: confirms the movement grid represents cell + band without
  clamping (range ±960 m at defaults), cross-edge translation round-trips, and per-mode quantization
  error is within bounds (~2 mm at mode 6). On-wire confirmation (0b: two servers + client across the
  seam) is a follow-up. **Extension-only.**
- **Spike 1 — Ghosting feasibility. 1a ✅ PASS (2026-06-13)** — `Assets/ServerMeshing/Spike1/`,
  [SPIKE1.md](SPIKE1.md#0-results-2026-06-13). Ghostified a live monster via `MovementDisablers` +
  `UpdateManager.Unregister` (4 components silenced); idle drift = 0 → no self-simulation, still a live
  entity. **1b (deferred):** S2S full-precision ghost (+ movement state) over a minimal CellMeshLink
  (needs the two-server rig). **Extension-only.**
- **Spike 2 — Client migration (make-or-break). ✅ PASS (2026-09-12)** — a real C→E→C zone hop with
  the P1 server-side boundary tick, measured on an automated rig: `playerReady` gap **450–650 ms**
  (from 2.2 s), 0.3 m position continuity, no scene reload; session resume, ready state, handler
  registration and ownership binding all complete inside the mask. Masking is sufficient; CE-1 not
  needed. One 2-line core edit was required for latency ([CE-6](#17-core-edit-registry)).
  [SPIKE2.md §0](SPIKE2.md#0-results-2026-09-12).

Order: **Spike 0 → Spike 2 → Spike 1 →** P0 proper. (Spike 0 before 2 because migration is meaningless
if positions don't survive across cells.)

---

## 17. Core-edit registry

Marked in-code `// MESH-EDIT: CE-<n> — <reason>. See Docs/ServerMeshing/DESIGN.md §17`.

| ID | Status | Location | Change | Why extension fails | Mitigation |
|---|---|---|---|---|---|
| CE-1 | **Not needed (2026-09-12)** | `LiteNetLibGameManager.OnStopClient` (`:516-524`) | `protected virtual bool ShouldClearAssetsOnStopClient()` | Subclass can't skip just the clear (no `base.base`); B re-spawns with own ids | Masking measured adequate ([SPIKE2.md §0](SPIKE2.md#0-results-2026-09-12)): gap 450–650 ms, no visual break |
| CE-2 | **Avoided** | `CentralNetworkManager.cs:51,112` | (would be) subclass `ClusterServer` | n/a | MeshCoordinator is a separate role |
| CE-3 | **Investigating** | `MMOServerInstance` / `ProcessArguments` launch | Register + launch the MeshCoordinator role | "Launch like Login/MapSpawn" goes through the kit launcher | Run as an **independent process** (zero-edit) if feasible; else a small documented launcher edit |
| CE-4 | **Investigating** | Login / enter-game routing | Resolve `(worldMap, position) → cell server` | Existing path routes by `currentMapName` only | Prefer a handler/`ManagerComponent` seam; core edit only if none reaches the routing decision. *Observed 2026-09-12:* a character saved inside zone E but logging into channel C is handed off to E by C's boundary tick right after spawn — a working (if roundabout) fallback |
| CE-6 | **Applied 2026-09-12 (2 lines, upstreamable)** | `ThirdParty/LiteNetLibManager/Scripts/LiteNetLibClient.cs` + `LiteNetLibServer.cs`, `SendRequestAsync` | `do { await UniTask.Delay(100); } while (!done);` → `await UniTask.WaitUntil(() => done);` | The 100 ms poll put a hard floor under **every** request/response in the kit (all DB calls, cluster calls); the receiving cell issues ~7 sequential DB requests per hop, so the floor alone was ≈0.7 s of the measured gap. No extension seam reaches the wait loop (would need a custom `IDatabaseClient` wrapping ~100 methods) | Marked `MESH-EDIT: CE-6`; a one-line PR to OpenMMORPG/LiteNetLibManager ("scalability") retires it |
| CE-7 | **Applied 2026-09-12 (1 virtual + 1 call site, upstreamable)** | `Core/Scripts/Gameplay/CharacterSystems/MonsterCharacterSystems/MonsterActivityComponent.cs`, `ManagedUpdate` | Inline `Entity.Identity.CountSubscribers() == 0` gate → `protected virtual bool IsWatched()` (same default) | The kit puts a monster AI to sleep while it has no network subscribers. On a meshed cell the player standing just across the edge is a ghost here, not a subscriber, so edge monsters froze exactly when a neighbour-cell player looked at or hit them (fight test: 12 forwarded hits, no retaliation). The check is the first line of a public virtual with no seam around it | Marked `MESH-EDIT: CE-7`; add-on `MeshMonsterActivityComponent` overrides `IsWatched()` to also count a player ghost within visual range + margin; a one-line PR ("make the watcher check virtual") retires it |
| CE-8 | **Applied 2026-09-12 (visibility only, upstreamable)** | `MMO/Scripts/MMOGame/Networking/Map/MapNetworkManager.cs`, `_pendingSpawnPlayerCharactersByUserId` | `private` → `protected` | The kit's `SetPlayerReadyRoutine` already has an "already spawned entity" branch (relates data, `SetOwnerClient`, register) that is taken when the pending entry is an entity; a meshed cell wants to put its ghost of the arriving character there so the arrival promotes the ghost in place (no entity swap for observers). The pending map is filled inside the non-virtual enter-game path and was private | Marked `MESH-EDIT: CE-8`; supersedes the CE-5 idea with far less surface; a one-word PR retires it |
| CE-9 | **Applied 2026-09-12 (2 lines, upstreamable — a kit bug)** | `Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs`, `OnSetOwnerClient` | Reset `_acceptedPositionTimestamp` and `_isServerWaitingTeleportConfirm` on owner change | With client-authoritative movement the server keeps the owner client's last accepted position and lerps the entity toward it while not simulating. Those fields survive an owner change, so an entity re-owned by a new client (the kit's own "already spawned entity" ready path, and the mesh's in-place promotion) snapped back to where the previous client left it and rejected the new client's first syncs as stale timestamps — observed as a promoted character jumping 3.5 m back across the line and being handed off again | Marked `MESH-EDIT: CE-9`; a two-line PR retires it |
| CE-5 | **Not needed (2026-09-12)** | `MapNetworkManager._pendingSpawnPlayerCharactersByUserId` (private) | (would have been) a seam to inject transferred spawn data | The expensive part was the awaited relates-data reload, and `LoadPlayerCharacterEntityRelatesData` is already `protected virtual` — the add-on overrides it to load in the background for noticed handoff arrivals | The remaining `GetCharacter` DB read on enter-game (~40 ms) is the only thing a true S2S data transfer would still save |

*(An entry here is a cost, not a milestone.)*

---

## 18. Open questions

| # | Question | By |
|---|---|---|
| Q1 | `S`, `B`, `H` sized for skill/projectile reach, **max speed × AOI `updateInterval`**, density. *Range/precision part validated by Spike 0 (256/128 ok); speed×cadence sizing still open.* | P0 |
| Q2 | Cell scenes authored at the same *local* origin; per-cell world offset is logical only — confirm no per-scene lightmap/navmesh issue | P0 |
| Q3 | `MeshEntityId` for non-players = Coordinator-allocated vs birth-provenance `(spawnCell,incarnation,counter)`? | P0 |
| Q4 | Target concurrency: players/cell + world size → `S`, `B`, P4 | P0 |
| Q5 | ✅ **RESOLVED (Spike 2, 2026-09-12): masking is sufficient.** First measurement 2.2 s; after skipping the client's post-scene-load settle+GC during a hop (extension) and removing the kit's 100 ms request-poll floor ([CE-6](#17-core-edit-registry)) the `playerReady` gap is **450–650 ms** with 0.3 m position continuity, no scene reload, player keeps walking. CE-1 **not needed**. Further reduction (S2S relates-data transfer) needs [CE-5](#17-core-edit-registry) or reflection | — |
| Q6 | Parties/guild/social (cluster-relayed) vs ownership at borders — conflicts? | P2 |
| Q7 | Navmesh/physics/colliders in the band — which physical components survive on a ghost? (couples Q9) — **central to P3** | P2/P3 |
| Q8 | MeshCoordinator in-process with Central, or its own process? (drives CE-3) | P0 |
| Q9 | ✅ **RESOLVED (Spike 1a, 2026-06-13):** `MovementDisablers` + `UpdateManager.Unregister` the server-driver `IManagedUpdate` components (AI + regen + buff-tick + action = 4 on a monster). Idle drift = 0, entity stays live. Extension-only; no ghost-variant prefab needed | — |
| Q10 | ✅ **RESOLVED (Spike 0, 2026-06-13):** default grid (128/15) covers cell+band with margin (±960 m vs 256 m needed); no clamping; the movement `cellId` is an independent within-server sub-cell index — no conceptual collision with mesh cell ids | — |
| Q11 | ✅ **RESOLVED for the rig (2026-09-12):** cells are spawned by the existing **MapSpawn** flow (one channel per zone, same `mapName`); each cell learns neighbour addresses from the **cluster broadcast** (`ClusterClient.onResponseAppServerAddress`, appended in `MeshMapNetworkManager.Start`). A Coordinator is not needed for adjacency/routing in the uniform-grid case | — |

---

## 19. Glossary

| Term | Meaning |
|---|---|
| **Mesh cell** | One tile of the uniform world grid = one map-server process (coarse, *inter-server*) |
| **AOI grid** | Fine intra-server interest grid (`JobifiedGridSpatialPartitioningAOI`, cellSize 64) |
| **Movement grid** | Intra-server *position-compression* grid (`DefaultGridManagerComponent`, byte `cellId`, cellSize 128, `gridSize ≤ 15`) |
| **Logical global position** | `(meshCellId, localPosition)` — persisted/routed; Unity transform stays cell-local |
| **Border band (`B`)** / **Hysteresis (`H`)** | Overlap zone for ghosts / distance past edge before ownership flips (`H < B`) |
| **Boundary tick** | Per-tick check in `MeshServerComponent` detecting walked crossings — the **primary** handoff trigger |
| **Ghost** | Read-only replica; authoritative behaviours suppressed; never simulated locally |
| **MeshEntityId** | Stable, owner-independent cross-cell id (character id for players; Coordinator/birth-provenance id otherwise) |
| **cellIncarnation** | Coordinator-issued per-cell-startup number; keeps `MeshEntityId` unique across restarts |
| **Owner / owner-of-record** | The single cell that simulates an entity / may persist it |
| **Handoff** | Transfer of authority + client connection to a neighbor |
| **CellMeshLink** | Server-to-server channel between adjacent cells |
| **Mesh Coordinator** | Standalone role: adjacency + ownership + routing + id allocation + handoff broker |
| **ManagerComponent** | `BaseGameNetworkManagerComponent` — the kit's lifecycle-hook extension seam |
| **MESH-EDIT** | In-code marker for an unavoidable core edit, logged in [§17](#17-core-edit-registry) |
