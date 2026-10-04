# Survey 7: space game's real ship equivalents for the Phase 0 step 7 stand-ins

Repo: `C:\dev\Game Design\mmorpg-kit-space` (`main` 1a3a58d9, read only; only read-only `git --no-optional-locks log/branch` was run).
Paths below are relative to `Assets/_Game/Scripts/` unless noted. "SMC" = `Space/Ships/ShipMovementComponent.cs`.

**The main finding:** the space repo has exactly one ship wire format, the 54 B record. There is **no header byte, no authority enum, no flight-state field, no epoch, no compact 28 B format, no SpaceGridAOI and no A0 formula** anywhere in code or docs. Grep over `*.cs` and `*.md` for `flightState|FlightMode|OwnerMirror|ServerAI|epoch|compact|SpaceGridAOI` found nothing relevant. The only hits were the unrelated "FlightModeManager" in a comment and "compact port of" as an English word. Those spec items come from the space team's chat replies (2026-09-25/26, recorded in our memory note `space-mmo-use-case.md`). They are agreed future design, not code. The memory note records the compact variant as **"~26 B"**, not 28.

## 1. Ship entity
- `Space/Ships/ShipEntity.cs:20`: `public class ShipEntity : VehicleEntity, IVehicleEntity, IKeepWhenDriverExits`.
  - It adds two `SyncFieldString`s, `ownerId` and `ownerName` (:27-28, ServerToClients :53-54).
  - It sets `boardingDistance = 15f` (:24) and allows only the owner to board (:61-70).
  - Its `RemovePassenger` override does a RaycastAll ground probe on exit (:105-170).
- Prefab `Prefabs/Entities/Ship.prefab`:
  - **one seat** (seats list, line 46405);
  - **hp baseAmount 2500** (46417-46418);
  - Rigidbody `m_Mass: 1`, damping 3/8, `m_Interpolate: 0`, discrete collision (46230-46248);
  - BoxCollider 8.4×3×11.9 at center (0, 0.4, 3.4) (46220);
  - LiteNetLibIdentity `visibleRange: 2000` (46316), `subChannelId` empty.
- Variants:
  - `Ship_AI.prefab` adds ShipAIPilot and overrides team = 1, `respawnOnDeath` = 0.
  - `Ship_Enemy.prefab` has team 1, **hp 1500**, forces 300/600, torques (8,8,10) and Rigidbody angular damping 3. That last override is dead: `ShipEngine.Awake` resets it to `angularDrag` = 8.
- `IKeepWhenDriverExits` is a vendor patch:
  - declared at `KitHooks/JumpKitHooks.cs:15`;
  - checked at `OpenMMORPG/.../VehicleEntityExtensions.cs:13-15`;
  - documented in `Docs/VENDOR_PATCHES.md:89`.
  - Our kit does not have it, which is why the spec has its "pilots never exit" workaround.

## 2. Movement / data handler: `ShipMovementComponent`
- SMC:35: `ShipMovementComponent : BaseNetworkedGameEntityComponent<BaseGameEntity>, IEntityMovementComponent, IEntityMovementDataHandler, IOriginShiftAware, IOriginRotationAware`. It has `[RequireComponent(typeof(ShipEngine))]`.
- **Registration** (SMC:92-97): `EntityMovementDataHandlers.TryRemove(ObjectId…)` then `TryAdd(ObjectId, this)` in `OnIdentityInitialize`, plus `TryRemove` in `OnNetworkDestroy` (:99-102).
- **Authority is implicit, not an enum.** SMC:110: `IsSimulating => IsOwnerClient || (IsServer && ConnectionId < 0)`.
  - Driver's client: simulates. The server then holds a kinematic mirror (= OwnerMirror).
  - Unowned on the server: simulates. AI and parked ships are told apart only by whether a ShipAIPilot is present (= ServerAI / Parked).
  - `RefreshSimulationMode` runs **every frame** in Update (:131-134). It toggles `isKinematic` and forces interpolation None (:112-125).
- **Wire record, `WriteState`** (SMC:329-344), one layout for every direction:

  | Field | Encoding | Bytes |
  |---|---|---|
  | position (TRUE space, `ClientOriginShift.ToTrue`) | `PutVector3` = 3 floats (`NetDataWriterExtension.cs:70-75`) | 12 |
  | rotation | 4 raw floats x,y,z,w | 16 |
  | linear velocity | `PutVector3` | 12 |
  | angular velocity | `PutVector3` | 12 |
  | movementState | `PutPackedUInt`; only `None`=0 or `Forward`=1 ever (SMC:152-154, 218-220). Values ≤ 240 take 1 byte (`NetDataWriterExtension.cs:181-184`) | 1 |
  | teleport byte | server→client: `_teleportSequence`; client→server: `_ackedTeleportSequence` | 1 |
  | **Total** | | **54 B, constant** |

  - "56 B with header" = 54 + 2 is spec arithmetic over a header that does not exist in code yet. Nothing in the repo specifies or computes "28 B compact".
- **Kit framing around the record** (`OpenMMORPG/Core/Scripts/Networking/BaseGameNetworkManager.cs`):
  - Server→client (:661-739), per receiver per tick, per record: packed objectId (1-4 B) + **int32 length (4 B)** + 54. So a typical record is about 59-60 B on the wire.
  - Server→client, per packet: msg header + packed timestamp + int32 count, sent once per delivery type.
  - `WriteServerState` is called **once per subscribed receiver**: the same bytes are re-serialized N times (:695-703).
  - Client upload (:620-637): msg header + packed timestamp + packed objectId + 54, with no length field. It is sent every logic tick.
- **WriteServerState** (SMC:383-393) always returns true on the server, with no dirty or rest check. Parked ships are written every tick to every subscriber.
  - `shouldSendReliably = _teleportSequence != _ackedTeleportSequence`, which matches the spec's "reliable while a teleport is unacknowledged".
- **WriteClientState** (SMC:357-364): only if `IsOwnerClient`, always unreliable.
- **ReadClientStateAtServer** (SMC:366-381):
  - stores the ack;
  - **drops the whole state if ack ≠ `_teleportSequence`** (stale until the owner applies the teleport);
  - otherwise caches the net state and **sets the transform directly**, with no validation and no speed check.
- **ReadServerStateAtClient** (SMC:395-413): a new teleport seq triggers `ApplyTeleport` and an ack. The owner otherwise ignores server state (:405-406). Observers cache the state.
- **Teleport** (SMC:264-285): the server bumps the byte seq. `IsWaitingClientTeleportConfirm` (:304) is true while the ack lags. `FindGroundedPosition` returns false, so ships are never ground-snapped (:287-293).
- **Smoothing on non-simulating peers** (SMC:163-178). This runs on observer clients AND on the server's mirror of a player ship, because IsSimulating is false there too:
  - `_netPosition += _netVelocity * Time.deltaTime;` is **velocity extrapolation first** (:166);
  - snap: `if (distance > observerSnapDistance)` then SetPositionAndRotation (:168-171), with `observerSnapDistance = 50f` (:49);
  - otherwise `t = 1f - Mathf.Exp(-observerLerpSpeed * Time.deltaTime)` with `observerLerpSpeed = 10f` (:47, :174), then Lerp position and Slerp rotation with the same t;
  - angular velocity is received but never used for extrapolation.
- **P-steering** (SMC:197-211): `steeringGain = 2.5f` (:41), `rollLevelGain = 0.6f` (:43). The prefab has the same values (Ship.prefab 46914-46917).
  - Yaw error = atan2(local.x, max(1e-4, local.z)) and pitch error = −asin(local.y), each × 2.5 and clamped to ±1.
  - Roll error is the auto-level toward world up, atan2(localUp.x, localUp.y) × 0.6.
  - Thrust is the world move intent converted to ship-local, with magnitude capped at 1 (:185-195).
- **Likely defect worth knowing (verify):**
  - For a player-owned ship, the server's Rigidbody is kinematic and is moved by `transform.SetPositionAndRotation`. `WriteState` sends `_rigidbody.linearVelocity` and `angularVelocity` (:338-341), not the cached `_netVelocity`.
  - A kinematic body moved through its transform reports zero velocity, so server→observer records for player ships probably carry **zero velocities**. Observers then get no extrapolation for player ships.
  - AI and parked ships, which the server simulates, send real velocities.

## 3. Engine / physics: `Ships/ShipEngine.cs` (assembly Game.Ships)
- It is a pure rigidbody integrator, a port of SCK VehicleEngines3D (:5-29), in `FixedUpdate` and skipped when kinematic (:116-147):
  - `steeringBySpeedCurve.Evaluate(speed/topSpeed)`, linear 1→0.8 (:39, :133), or `boostSteeringCoefficient = 0.5` while boosting (:41, :125);
  - `AddRelativeTorque(mult * Scale(steer, maxSteeringTorques), ForceMode.Acceleration)` with torques (23, 23, 30) (:35, :135-137);
  - `_currentMovementForces = Vector3.Lerp(cur, target, movementInputResponseSpeed * fixedDeltaTime)` with response speed 6 (:49, :144-145);
  - boost replaces the force per axis via max (:141-143), with forces 400 and boost 800 (:45, :47);
  - `AddRelativeForce(_currentMovementForces)`, default ForceMode.Force (:146).
- Drag: `angularDrag = 8f`, `linearDrag = 3f`, applied in Awake as `angularDamping` and `linearDamping`; gravity is off (:53-55, :71-77).
- **Terminal speed** (:154-159): `(force / linearDrag - force * Time.fixedDeltaTime) / mass`, not F/drag.
  - With mass 1, F 400, drag 3, dt 0.02: **125.33 m/s**; boosted **250.67 m/s**.
  - F/drag alone gives 133.3, so the spec test "terminal speed equals F/drag" is off by 6%.
  - Ship_Enemy: 94 m/s, boosted 188.
- Rates: physics `Fixed Timestep: 0.02`, i.e. 50 Hz (`ProjectSettings/TimeManager.asset:6`).
- `ShipCruiseController.cs` ("cruise" / low cruise):
  - It is **client-only**: "Runs entirely on the PILOTING CLIENT … not a network feature" (:20-22).
  - `enum CruiseMode { Combat, Spooling, Cruise, Despooling }` (:9-15) is **never on the wire**. This is the field the agreed 3-bit flightMode would carry.
  - Thrust ×5 and damping 0.5 give about **3.96 km/s** (prefab 47075-47076).

## 4. AI pilot: `Space/Ships/ShipAIPilot.cs`
- Server-only, per-frame `Update`, active only when the rigidbody is non-kinematic and the ship is not destroyed (:81-98). Priority order: avoidance, then combat, then patrol.
- **Scan** (:104-133): every `targetScanInterval = 0.5f` (:27) of unscaled time.
  - It foreaches `Manager.Assets.GetSpawnedObjectValues()` with `identity.GetComponent<ShipCombatComponent>()` on each, keeping the nearest hostile of a different team within `detectionRange = 2200f` (:25).
  - The current target is dropped when destroyed or beyond 1.3 × detection = 2,860 m (:110-113).
- **Avoidance** (:141-157): **one `Physics.SphereCast` per frame**, radius `avoidanceProbeRadius = 8f` (:41), along the velocity, over a distance of `max(30, speed × avoidanceTimeHorizon 3)` (:42, :145). On a hit it steers to reflect+normal × 200 m at throttle 0.7.
- **Combat timers** (:159-189):
  - Attack lasts 8-10 s, Evade 5-10 s;
  - closer than `minEngageDistance = 150f`, it is forced into Evade;
  - evade re-rolls every 1-4 s with yaw ±60-120° and pitch ±30°, slerped 0.35 away from the target, then drives 600 m out at throttle 1 (:224-241).
- **Attack** (:191-222):
  - it drives at the TargetLeader intercept point with throttle `clamp(d/400, 0.35, 1)`;
  - fire cone = `min(maxFiringAngle 15, aimCorrection 4 + aimAssist 3)` = **7°**, within `maxFiringDistance 1000`;
  - **burst** toggles 1-3 s on and 0.5-2 s off;
  - it calls `_combat.ServerFire()` every frame while bursting and in the envelope, rate-limited to 0.175 s.
- **Patrol** (:243-250): orbits the spawn point at radius `patrolRadius = 400f`, 0.05 rad/s, `patrolThrottle = 0.4f`, which gives a terminal speed of about 50 m/s. There is **no stay-in-rectangle behaviour**.
- RNG: `new System.Random(ObjectId*486187739 + 1000003)` (:73). It drives through `SetLookRotation` and `KeyMovement` on the movement component (:256-265).
- Spawning: `Space/Ships/ShipWaveSpawner.cs:24`. It is dormant until `StartWaves()`; waves are 2 ships plus 2 per wave up to 6, on a ring of radius 300, with seed 20260830 (:28-41).

## 5. Combat: `Space/Ships/ShipCombatComponent.cs`
- Sync fields:
  - `protected SyncFieldFloat shield` and `SyncFieldBool destroyed` (:82-83);
  - hull is the kit `DamageableEntity.CurrentHp`.
- **Shield written every server frame while recharging** (:586-590): `shield.Value = Mathf.Min(cap, shield.Value + shieldRechargeRate * Time.deltaTime)` once 2 s have passed since the last damage. Capacity 2500, rate 100/s, delay 2 s (:58-60).
- RPCs:
  - `[ServerRpc] CmdFire(int seq)` (:192-211): rate check at 0.8 × interval, records the shot, then `RPC(RpcFired, seq)`;
  - `[AllRpc] RpcFired(int seq)` (:263-270), payload 4 B;
  - `[ServerRpc] CmdClaimHit(int seq, uint targetObjectId, Vector3 truePoint)` (:473-508), payload 20 B, **no epoch yet**. It validates range, flight time, a 40 m target error and the barrel-count cap;
  - `[AllRpc] RpcImpact(Vector3 truePoint)` (:510-521), payload 12 B.
- **`TakeShipDamage(int)`** (:527-547): the shield absorbs first, the remainder goes to `CurrentHp`, and hull 0 calls `Explode()`, which sets `destroyed`, removes all passengers and starts a 10 s respawn or despawn (:549-601).
- Guns (prefab 47040-47046): **2 barrels**, `fireInterval 0.175`, `projectileSpeed 800`, `maxRange 1000`, 50 damage, AI aim error 2.5°.
- **Projectiles are always `Instantiate`, never pooled**: `Instantiate(projectilePrefab, barrel.position, …)` per barrel (:397).
  - **This happens on the server too for AI shots.** `ServerFire` calls `SpawnShotVisuals` (:248), and only flashes and audio are gated on `IsClient` (:281).
  - Each bolt (`Space/Ships/ShipProjectileVisual.cs`) runs **`Physics.RaycastAll` every frame**, which allocates (:58).
  - Bolts end with `Destroy` on a hit (:77) or at max range (:84), and impact FX `Instantiate`+`Destroy(4f)`.
  - A firing AI produces about 11.4 bolts/s, each living about 1.25 s.
- Missiles (`ShipMissileComponent.cs`, on Ship.prefab; players only, the AI never fires them):
  - lock 3 s / 25° / `lockingRange 3000f` (:34-36);
  - server-simulated missile records (:72, :263-297);
  - `[AllRpc] RpcMissileLaunched(int, uint, Vector3, Vector3)` (:305) and `RpcMissileDetonated(int, uint, Vector3, bool)` (:325).

## 6. Visible range / AOI
- No custom AOI: `BaseGameNetworkManager.cs:120-122` auto-adds the kit's `JobifiedGridSpatialPartitioningAOI` (no prefab or scene references it). Defaults:
  - `cellSize = 64f`;
  - `updateInterval = 1.0f` (`OpenMMORPG/Core/Scripts/Networking/AOI/JobifiedGridSpatialPartitioningAOI.cs:15-18`);
  - in 3D mode the Y axis is disabled (:122; constructor args `disableX, disableY, disableZ`), so it is an **X/Z grid**.
- Ship range = the identity's **`visibleRange: 2000`**, fixed (Ship.prefab 46316), with no velocity term. The kit default is 80 (`BaseInterestManager.cs:10`).
- `Docs/KIT_AUDIT.md:96-98` describes this AOI. There is no SpaceGridAOI, 3 km cells or A0 = 3000 + (v_own+v_fastest)×1.25. Those are the team's reply-2 commitments (memory note).

## 7. Bubbles / supercruise
- `Space/BubbleManager.cs`, server-only:
  - `Bubble { slotIndex, slotOrigin, anchor (UniversePosition), persistent, entityObjectIds }` (:13-21);
  - body i owns slot i (:92-104);
  - dynamic bubbles are allocated by `GetOrCreateBubbleAt`, reusing any bubble within roam × 0.5 (:155-179), and released when empty (:241-248);
  - the drift check runs every `reanchorCheckInterval = 2f` (:44, :75-80) and is itself a `GetSpawnedObjectValues` sweep (:255-271);
  - pitch ≥ 2·roam + 2000 is validated (:110-114).
- `Universe/BubbleGrid.cs:17`: square-spiral slot layout on X/Z.
- Data assets (e.g. `GameData/Carina.asset:71-73`): `bubbleSlotPitch 160000`, `bubbleRoamRadius 60000`, `universeMatchesSim 1`, i.e. the collapsed model (pitch 160 km, roam 60 km).
- Supercruise is not built:
  - `Space/TransitSystem.cs` is a v1 "supercruise" stand-in: server trapezoid kinematics in universe doubles, with the entity parked in its departure bubble until arrival (:10-20). `ArrivalStandoffMargin = 3000f` (:43).
  - In-bubble "cruise" is the client-only ShipCruiseController (§3).

## 8. Rates
- `updateFps: 20` on MapNetworkManager-CE and the MMO server/client instances (prefabs). The kit default is the same (`LiteNetLibManager.cs:36`, logic tick at :155).
- Server movement send on `OnServerUpdate` and client upload on `OnClientUpdate`, both every logic tick (`BaseGameNetworkManager.cs:129-144`). So **20 Hz in both directions**.
- `serverTargetFrameRate: 30` (`Prefabs/GameInstance.prefab:230`). Physics runs at 50 Hz.

## 9. Docs on a compact format / indicator bit
- None.
  - `Docs/KIT_AUDIT.md` covers the raw-float movement path.
  - The kit's own cell quantizer (byte cellId + 10/4/10…16/16/16 bit LOD modes) is **disabled** (`isDisabled = true`), with a planned "Phase 3 rework" (:37-55).
  - `MASTER_PLAN.md`, `PROGRESS.md` and `VENDOR_PATCHES.md` have no 28 B, compact-bit, header or epoch text.
- The compact indicator bit is therefore genuinely unassigned. Keeping it as the rig-only choice (A) is correct.
- Our own contract (`mmo-server-meshing/Assets/ServerMeshing/Mesh/Sync/MeshSyncContracts.cs:36-40, 68-69`) defines `IMeshCompactStateSource` / `Compact = 1`, with the client telling variants apart "by the payload's own header".

## Spec claim → actual value in space repo → match?

| Spec claim | Actual (space repo) | Match? |
|---|---|---|
| Legacy record 54 B | 12+16+12+12+1+1 = 54 B constant (SMC:329-344) | **Yes** |
| 56 B with 2-byte header | No header in code; 54+2 is arithmetic over the agreed reply-2 header | Not in code (design only) |
| Header byte0 = authority 2b \| flightState 3b<<2, byte1 = epoch | Not in code. Memory: "mode (2 bits) and flightMode (3 bits) in one byte, plus an epoch byte"; bit order unspecified | Not in code; bit order is the spec's choice |
| Compact 28 B | Nothing in code or docs; memory says team reply "~26 B" | **No source; 28 vs ~26 unresolved** |
| Authorities OwnerMirror/ServerAI/Parked | Implicit `IsSimulating` (SMC:110) + presence of ShipAIPilot | Semantics yes, enum no |
| flightState (3 bits) | `CruiseMode` 4 values, client-only, not on wire (Cruise:9-15) | Not on wire today |
| Smoothing 1 − exp(−10·dt) | `observerLerpSpeed = 10f`, same formula (SMC:47, 174) | **Yes**, plus velocity extrapolation first |
| Snap at 50 m | `observerSnapDistance = 50f` (SMC:49) | **Yes** |
| P-steering gains 2.5 / 0.6 | `steeringGain 2.5`, `rollLevelGain 0.6` (SMC:41-43) | **Yes** (0.6 is roll auto-level) |
| Reliable while teleport unacked | `shouldSendReliably = seq != acked` (SMC:390) | **Yes** |
| Engine: curve evaluate, relative torque, force lerp, relative force | ShipEngine:133-146 exactly | **Yes** |
| Drag 3 and 8 | linear 3, angular 8 (ShipEngine:53-55) | **Yes** |
| Engine test "terminal = F/drag" | `(F/drag − F·dt)/mass` = 125.33 at 50 Hz (ShipEngine:154-159) | **No**: use their formula |
| Pilot scan every 0.5 s, GetSpawnedObjectValues + GetComponent | ShipAIPilot:27, 117-121 | **Yes** |
| SphereCast radius 8 per frame | radius 8, distance max(30, 3·speed) (AIPilot:41-42, 148) | **Yes** (add the distance rule) |
| Attack/evade timers | Attack 8-10 s, Evade 5-10 s, re-roll 1-4 s, <150 m forces evade | Yes (values unstated in spec) |
| Burst fire | 1-3 s on / 0.5-2 s off, 7° cone, 1 km | Yes (values unstated) |
| Stay-in-rectangle priority | **None**; patrol = 400 m orbit of spawn at throttle 0.4 | **No**: rig-only addition |
| Shield SyncFieldFloat written every frame while recharging | Combat:82, 586-590 (+100/s after 2 s) | **Yes** |
| RpcFired / RpcImpact [AllRpc] | Combat:263 (int), 510 (Vector3) | **Yes** |
| TakeShipDamage | Combat:527-547 | **Yes** |
| Hull 2,500 | hp 2500 (Ship.prefab); Ship_Enemy 1500 | **Yes** (AI enemy variant 1500) |
| One seat | 1 seat (Ship.prefab 46405) | **Yes** |
| Projectiles instantiate vs pooled | Always Instantiate + RaycastAll per bolt per frame; no pool | Instantiate **yes**; pooled = rig-only |
| Speeds 50-125 m/s | Patrol about 50, full 125.3, boost 250.7, cruise about 3,960 (client only) | **Yes** within the fighting envelope |
| Visible range A0 = 3000 + (v_own+v_fastest)×1.25 | Fixed `visibleRange 2000`, kit X/Z grid AOI (64 m cells, 1 s) | **No**: design only |
| SpaceGridAOI | Does not exist | **No**: design only |
| 20 Hz owner uploads | `updateFps 20`, uploads every logic tick | **Yes** |
| Registers at OnIdentityInitialize like SMC:92-97 | SMC:92-97 | **Yes** |

## What the stand-in must copy that the spec omits
1. **Velocity extrapolation before smoothing.** Do `_netPosition += _netVelocity*dt` and then the lerp/snap. Rotation uses Slerp with the same t. Angular velocity is unused.
2. **The server mirror runs the same smoothing.** IsSimulating is false there, so Update runs it, after `ReadClientStateAtServer` has set the transform outright. That is a per-mirror per-frame cost.
3. **Stale-upload rejection.** Drop the whole client state while the ack ≠ the teleport seq. The teleport byte is the seq server→client and the ack client→server. The owner ignores server state except teleports.
4. **Kit framing.** Each server→client record carries packed objectId + int32 length, about 59-60 B per record. Uploads carry packed timestamp + packed objectId and no length. Meters measuring "54 B" must say whether framing is included.
5. **Per-receiver serialization.** `WriteServerState` is called for every subscriber, and on the server it always returns true: no rest/dirty suppression, and parked ships are sent every tick.
6. **`RefreshSimulationMode` every frame,** with interpolation forced to None. Non-simulating peers call `ClearAllInputs`.
7. **Probably zero velocities for player ships** on the server→observer path (kinematic mirror; §2). Copy this faithfully or as an explicit option, because it changes observer error and compressibility.
8. **Mass 1, box collider 8.4×3×11.9 @ (0,0.4,3.4), discrete collision, no interpolation, gravity off.** Also boost (800, steer × 0.5) and steering torques (23,23,30) with ForceMode.Acceleration.
9. **AI details.**
   - Drop the target at 1.3 × detection; detection is 2,200 m.
   - Attack throttle = clamp(d/400, 0.35, 1); the TargetLeader intercept lead uses projectile speed 800.
   - Fire cone = min(15, 4+3) = 7°.
   - Evade = yaw ±60-120°, pitch ±30°, slerp 0.35 away, 600 m at throttle 1.
   - Avoidance steers 200 m at throttle 0.7.
   - Per-pilot RNG seed `ObjectId*486187739+1000003`.
   - Timers use `Time.unscaledTime`.
10. **Server-side AI projectiles.**
    - 2 barrels × 1/0.175 s gives about 11.4 `Instantiate` calls per second.
    - Each bolt runs `RaycastAll` every frame (allocating), then `Destroy`.
    - Hits call `TakeShipDamage` + `RpcImpact` directly.
    - The aim bend uses a ±2.5° random error from a second RNG (`ObjectId*7919+17`).
11. **Player-shot path.**
    - `CmdFire` is rate-checked at 0.8 × interval, records the shot and sends `RpcFired`.
    - `CmdClaimHit(int, uint, Vector3)` validation: range+60, flight distance+120, 40 m target error, claims ≤ barrels.
    - The shot dictionary is purged at 1000/800 + 2 s.
12. **Other sync traffic per ship.**
    - `destroyed` SyncFieldBool, `ownerId`/`ownerName` SyncFieldStrings, kit `CurrentHp`.
    - Respawn/despawn after 10 s; AI wave ships have `respawnOnDeath = 0` and despawn.
    - Explosion FX come from the `destroyed` flag, not an RPC.
13. **Kit AOI as the baseline.** 2,000 m fixed range on an X/Z grid (64 m cells, 1 s update). An A0 = 3 km row is a forward-looking arm, not today's game.
14. **The Ship_Enemy variant** (hp 1500, forces 300/600, torques 8/8/10, top speed about 94 m/s) is the hostile AI prefab alongside Ship_AI. Its angular-damping override is ineffective.
15. **Rates.** 30 fps logic, 20 Hz send and upload, 50 Hz physics. The AI and shield run at frame rate (30), not tick rate.
