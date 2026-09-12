# Grid-Based Seamless Server Meshing — Design

> Add-on for **MMORPG Kit Community Edition** (Unity). Goal: players cross
> cell/server boundaries with no portal disconnect, no loading screen, and
> (ultimately) the ability to see and fight across boundaries.

| | |
|---|---|
| **Status** | Draft / planning — **identity, coordinates & routing now explicit (v0.5); not yet build-ready until Spikes 0–2 pass** |
| **Doc version** | 0.10 |
| **Last updated** | 2026-06-12 |
| **Owner** | brenden.vaughan@gmail.com |
| **Scope target** | Fully seamless (Phases 1–3) on a **uniform grid of equal cells** |
| **Build constraint** | **Add-on only — extend/inherit, do not edit core.** See [§2](#2-engineering-principles--core-edit-policy) |

### Changelog

| Version | Date | Change |
|---|---|---|
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
- **Projectiles/skills** crossing the seam are handed off like players, or resolved by their owner
  against translated ghost positions.
- **Lag comp across cells** needs neighbor history → ship recent transform history with ghost
  snapshots. **Snapshot rate is a real bandwidth/CPU knob** — tunable per entity class.
- **Ghost colliders/navmesh:** cross-border hits require ghosts to carry colliders — entangled with
  *which* components survive suppression ([Q7](#18-open-questions)+[Q9](#18-open-questions)); central
  to whether P3 is possible.
- **Monsters/NPCs** near a border are owned by one cell, ghosted to the other; aggro must treat
  ghosted players as targets, routing threat across the link.

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
| **P3 Cross-border interaction** | Authority arbitration, ghost colliders, cross-cell lag comp, mob/projectile handoff | **See and fight** across |
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
| CE-5 | **Candidate** | `MapNetworkManager._pendingSpawnPlayerCharactersByUserId` (private) | Seam to inject a transferred `PlayerCharacterData` (or entity) as the pending spawn for a user, e.g. `protected virtual bool TryGetMeshPendingSpawn(...)` | The receiving cell re-reads the whole character + relates data from the DB on every hop (≈0.6–0.8 s of the measured gap); S2S transfer needs to bypass that | Reflection on the private field (spike-grade) or a 5-line upstreamable hook |

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
