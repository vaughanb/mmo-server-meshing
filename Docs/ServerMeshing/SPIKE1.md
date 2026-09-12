# Spike 1 — Ghosting & simulation-suppression

> Goal: prove a kit entity can be spawned as a **ghost** (read-only replica) without running its
> server logic — the gating risk for P2/P3 ([DESIGN.md risk #2](DESIGN.md#14-risks),
> [Q9](DESIGN.md#18-open-questions)). Run after Spike 0; can run before/with Spike 2.

| | |
|---|---|
| **Status** | 1a scaffolded — needs in-editor run (host). 1b deferred (needs two-server rig) |
| **Code** | `Assets/ServerMeshing/Spike1/` |
| **Resolves** | [Q9](DESIGN.md#18-open-questions); de-risks P2/P3 |
| **Result** | **1a suppression PASS (2026-06-13)** — see [Results](#0-results-2026-06-13) |

---

## 0. Results (2026-06-13)

**1a suppression: PASS.** Ghostified `BigOrcWarrior` → **4 components silenced**, `DisableMovement=True`.
Idle drift check: **`maxDrift = 0 m` over 5 s → no self-movement** (no AI wander, regen, or knockback).
The entity stayed live and spawned. → **suppression is achievable extension-only; Q9 resolved.**

The 4 silenced components are the real per-monster server-driver set (AI `MonsterActivityComponent` +
regen `CharacterRecoveryComponent` + buff `CharacterSkillAndBuffComponent` + one action component).

**External drive: confirmed (2026-06-13)** — the ghost **moves (circle) and renders**, with **no walk
animation** (slides), exactly as predicted: the crude probe sets only the transform. Fixed by feeding
movement state — see the refinement below. **1a fully closed.**

### Design refinement surfaced — ghost snapshots must carry movement *state*, not just position

The kit drives the model's animation from **movement state**, not transform deltas
(`BaseGameEntity.EntityUpdate` → `moveableModel.SetMovementState(MovementState, ExtraMovementState,
Direction2D, …)`). A transform-only ghost therefore slides. The fix is free: the kit **already
serializes** `movementState`, `extraMovementState`, and `yAngle` alongside position
(`DefaultGridManagerComponent.WriteEntityServerState`). So **CellMeshLink ghost snapshots carry
`(fullPrecisionPosition, movementState, extraMovementState, yAngle)`**, and the ghost sets those — then
its model animates correctly (walk/run/attack) instead of teleport-sliding. (1b / P2.)

---

## 1. Why this is the hard part

A ghost is spawned on a **server** (the ghosting cell). Kit entities run their server simulation
whenever `Entity.IsServer` is true — so a ghost spawned from the normal prefab would **fight the real
owner** (move via its own AI, regen HP, tick buffs). That's the double-simulation bug. Spike 1 proves
we can neuter that as an **add-on**, and finds the exact set of things to silence.

## 2. Verified mechanism (from the kit)

| Fact | Evidence |
|---|---|
| Server logic ticks via a **centralized `UpdateManager`** (`IManagedUpdate.ManagedUpdate()`), **not** Unity `Update()` | `CharacterRecoveryComponent.cs:16,25`, `MonsterActivityComponent.cs:73,146` |
| ...gated on `Entity.IsServer` (and sometimes `CountSubscribers()==0`) | `CharacterRecoveryComponent.cs:27`, `MonsterActivityComponent.cs:148` |
| → **`component.enabled = false` does NOT stop it.** Must `UpdateManager.Unregister(component)` | (managed loop ignores `enabled`) |
| AI, regen, buff ticking are all `IManagedUpdate` | `MonsterActivityComponent.cs:10`, `CharacterRecoveryComponent.cs:6`, `CharacterSkillAndBuffComponent.cs:8` |
| **Movement has a built-in disable seam:** `BaseGameEntity.MovementDisablers` (`HashSet<object>`) → `DisableMovement` → `Movement.enabled=false` + `StopMove()` | `BaseGameEntity.cs:192-193,382-396` |
| The entity itself is `IManagedUpdate`; its `EntityUpdate()` is mostly movement-enabling + animation (benign once movement is disabled) — keep it for the ghost's visual | `BaseGameEntity.cs:341-380` |

## 3. The suppression recipe (what the scaffold does)

`GhostSuppression.Ghostify(entity, key)`:

1. **Stop self-movement:** `entity.MovementDisablers.Add(key)` (built-in seam, no core edit).
2. **Silence server-drivers:** `UpdateManager.Unregister(...)` each `IManagedUpdate` on the entity whose
   type is in the suppress list (`MonsterActivityComponent`(2D), `CharacterRecoveryComponent`,
   `CharacterSkillAndBuffComponent`, the action components) — **keeping** the entity's own update,
   identity, model/animation, and collider.
3. Drive the ghost from snapshots via `ApplyGhostSnapshot(entity, pos, rot)`.

There is **no ghost flag the components honour**, so unregister/strip is the add-on-friendly path
(vs. a core edit to every component). This utility seeds the production `MeshGhostController`
(DESIGN.md §10).

## 4. Run 1a (single-server, host)

1. Compile-check the Console (paste any `Spike1`/`GhostSuppression` errors to fix first).
2. Open a scene with monsters (a kit demo gameplay scene), and start the game **as Host**
   (so `Entity.IsServer` is true and monsters spawn).
3. Drop `Spike1_GhostSuppressionProbe` on any GameObject (e.g. an empty near some monsters).
4. Use its component context menu (⋮):
   - **1) Ghostify nearest monster** — logs how many components it silenced + `DisableMovement=True`.
   - **2) Idle drift check** — watches the ghost for `driftCheckSeconds`. **PASS = it stays put**
     (no self-movement); FAIL means something still simulates it (note the name and add it to
     `GhostSuppression.DefaultSuppressTypeNames`).
   - **3) Toggle external drive (circle)** — drives the ghost on a circle via snapshots; confirm it
     **moves smoothly and still renders/animates** (proves it's drivable, not frozen-dead).
   - Optional: tick **Strip All Managed Updates** before ghostifying to find the *minimal* set
     (anything that then stops mattering can be trimmed from the suppress list).

### Pass / fail

- **Pass:** after ghostify, idle drift ≈ 0 (no AI wander, no knockback/regen twitch), and the entity
  still renders + can be driven by snapshots → suppression is achievable **extension-only**; record the
  final component list.
- **Fail:** the entity keeps moving/acting, or stripping enough to stop it also breaks rendering/NRE →
  some server logic isn't reachable via unregister; escalate Q9 to a **ghost-variant prefab** (a
  stripped-down prefab with only identity+model+collider) and note any core-edit need.

## 5. 1b — S2S ghost over CellMeshLink (deferred)

Once 1a's suppression recipe is known, prove the transport: a minimal `CellMeshLink` streams an
entity's **full-precision** state (per Spike 0's decision — never the lossy movement encoding) from
cell A to cell B, where B spawns a suppressed ghost and feeds it `ApplyGhostSnapshot`. Needs the
two-server rig (shared with [Spike 2](SPIKE2.md) / Spike 0b). Defer until 1a is green.

## 6. Outcomes to record back in DESIGN.md

- The final suppress list (resolves [Q9](DESIGN.md#18-open-questions)) and whether unregister sufficed
  or a ghost-variant prefab is needed.
- Whether the ghost collider survives suppression (it should — colliders aren't `IManagedUpdate`),
  which feeds [Q7](DESIGN.md#18-open-questions) for P3 cross-border hits.
- Any component that re-registers itself (would need a different silence strategy).
