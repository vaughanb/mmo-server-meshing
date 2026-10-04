# Step 7 kit survey (read-only, 2026-10-03)

Paths: `Kit/` = `Assets/OpenMMORPG/`, `LNL/` = `Kit/ThirdParty/LiteNetLibManager/Scripts/`, `B:` = `C:\dev\Game Design\mmorpg-kit-space\` (HEAD 1a3a58d9).
Abbreviations as in the build sheet (BGE, BGNM, BIEMF3D, CCEM, LNGM, LNI). "VSys" = `Kit/Core/Scripts/Gameplay/VehicleSystems/`.

## 1. VehicleEntity

- Class: `public partial class VehicleEntity : DamageableEntity, IVehicleEntity` (VSys/VehicleEntity.cs:11). `IVehicleEntity : IActivatableEntity, IEntityMovement` (Gameplay/Interfaces/IVehicleEntity.cs:5). `MountEntity` is an obsolete empty subclass (VSys/MountEntity.cs).
- Seats: `[SerializeField] protected List<VehicleSeat> seats`, "First seat is for driver" (VehicleEntity.cs:33-36). `VehicleSeat` (VSys/VehicleSeat.cs:6-23) holds `passengingTransform`, `exitTransform`, `canAttack`, `canUseSkill`, `hidePassenger` and override flags. `hidePassenger` only hides the model on clients (BaseCharacterEntity.cs:1154). It does not stop the passenger being synced.
- Occupancy: `SyncListUInt passengerIds` (VehicleEntity.cs:70). It is padded to `Seats.Count` zeros in `OnIdentityInitialize`, and only when `IsServer` (:126-141). The same method sets `Identity.DoNotDestroyWhenDisconnect = true` (:140). `onOperation` → `OnPassengerIdsOperation` (:150, :177-212) fills `_passengers` and calls `passenger.SetPassengingVehicle`. On the server it fires **synchronously** inside the list setter, but only once the list is spawned: `PrepareOperation` returns early if `!IsSpawned` (LNL/GameApi/LiteNetLibSyncList.cs:224-229). So a pilot can only be seated after the ship is spawned.
- `IsDriver`: seat 0 (VSys/VehicleEntityExtensions.cs:5-9). `HasDriver` = `_passengers.ContainsKey(0)` (VehicleEntity.cs:74).
- **Enter** (server only): `BaseGameEntity.EnterVehicle(IVehicleEntity, byte)` (Gameplay/BaseGameEntity_MountFunctions.cs:88-139):
  - checks `IsSeatAvailable` and `CanBePassenger` (always true, :240-243, not virtual);
  - for the driver seat, **`Manager.Assets.SetObjectOwner(vehicle.ObjectId, ConnectionId)`** (:106-107). A server-owned pilot (ConnectionId -1) therefore makes the ship server-owned, which overrides any phantom owner;
  - `vehicle.SetPassenger` (:110 → VehicleEntity.cs:261-266);
  - an optional enter-animation await.
  - Client path: `CallCmdEnterVehicle` → `[ServerRpc] CmdEnterVehicle` (:228-267). Mount path: `BaseCharacterEntity.SpawnMount` spawns and then enters seat 0 (CharacterEntity/BaseCharacterEntity_MountFunctions.cs:9-83).
- **Exit**: `BaseGameEntity.ExitVehicle()` (BaseGameEntity_MountFunctions.cs:146-202):
  - computes `isDestroying = PassengingVehicleEntity.IsDestroyWhenExit(seat)` (:152);
  - for the driver, sets the owner back to -1 (:181-182);
  - if `isDestroying`, calls `RemoveAllPassengers()` and **`vehicleEntity.NetworkDestroy()`** (:185-191);
  - otherwise calls `RemovePassenger` and, for the driver, `StopMove()` (:192-199).
  - `RemovePassenger` teleports the passenger to `exitTransform` (VehicleEntity.cs:268-295 → `ExitedVehicle`, MountFunctions.cs:212-216).
- `IsDestroyWhenExit` (VehicleEntityExtensions.cs:11-14): `return vehicleEntity.IsDriver(seatIndex) && !vehicleEntity.Entity.IsSceneObject;`. **The spec's claim and line numbers are correct**: a runtime (non-scene) vehicle is destroyed when its driver exits.
  - Our kit has **no opt-out**. `VehicleEntity.IsDestroyWhenDriverExit` (VehicleEntity.cs:73, virtual, returns false) is **dead code**: nothing reads it, so overriding it does nothing.
  - Repo B patched the extension with `&& !(vehicleEntity is IKeepWhenDriverExits)` (B:Assets/OpenMMORPG/.../VehicleEntityExtensions.cs:13-15; the interface is at B:_Game/Scripts/KitHooks/JumpKitHooks.cs:15). `ShipEntity : VehicleEntity, IVehicleEntity, IKeepWhenDriverExits` (B:_Game/Scripts/Space/Ships/ShipEntity.cs:20). Our stand-in cannot model that without a kit edit.
- Callers of `ExitVehicle` that the spec does not mention (each destroys a runtime ship that has a driver):
  - **`BaseGameEntity.EntityOnDestroy` → `ExitVehicleAndForget()` on the server** (BGE:256-263). Despawning or destroying a seated pilot destroys its ship. Teardown order matters: destroy the ship first, or unseat with care.
  - `SpawnMount` exits an existing vehicle first (BaseCharacterEntity_MountFunctions.cs:21-25).
  - `CharacterSkillAndBuffComponent` exits when `Mount.ShouldRemove` (CharacterSystems/CharacterSkillAndBuffComponent.cs:50-58). This is safe for a pilot seated through `EnterVehicle`, because `Mount.type` stays `None` and `ShouldRemove` returns false (CharacterData/RelatesData/CharacterMount.cs:42-70). Never seat pilots through `SpawnMount`.
  - The add-on's own ghost mount code (Mesh/MeshMapNetworkManager_Ghosting.cs:1810-1860).
- Destruction by HP: `ReceivedDamage` → `Destroy()` when dead (VehicleEntity.cs:361-371, 380-397). This kicks passengers, sends an RPC, calls `NetworkDestroy(destroyDelay=2 s)`, and respawns only scene objects. If `ServerMeshShipCombat` uses its own hull it should not route through kit damage, or should set `canBeAttacked=false` (:40, :77-78).
- `EntityAwake` forces `tag = vehicleTag` and `layer = vehicleLayer` (VehicleEntity.cs:89-95). That matters for the SphereCast and projectile layer masks.
- Kit AI targeting: `ShouldBeAttackTarget` requires `HasDriver` (:424-427). Passengers cannot damage their own vehicle (:373-378).

### Vehicle movement: how it is found and written
- `BaseGameEntity.Movement = GetComponent<IEntityMovementComponent>()` (BGE:205, in `InitialRequiredComponents` from `Awake`). `VehicleEntity.InitialRequiredComponents` calls `EntitySetting.InitialVehicleEntityComponents(this)` first (VehicleEntity.cs:97-101), and `DefaultEntitySetting` adds **nothing** for vehicles (Gameplay/EntitySettings/DefaultEntitySetting.cs:48-50). **Vehicles have no default movement component.** It is whatever `IEntityMovementComponent` the prefab carries. The only vehicle prefab in the repo, `TestMap/Prefabs/TestMount-CE.prefab`, uses `CharacterControllerEntityMovement` (script guid ac04ae93…, prefab line 968).
- A passenger's `ActiveMovement` is the vehicle's `Movement` and its `MovementTransform` is the vehicle's transform (Gameplay/BaseGameEntity_MoveFunctions.cs:23-44). Driver input reaches the vehicle that way.
- Wire path: yes, exactly as for characters. A movement component registers itself in `BGNM.EntityMovementDataHandlers` (BGNM:108-109) at `OnIdentityInitialize`, as `TryRemove(ObjectId)` then `TryAdd(ObjectId, this)`:
  - BIEMF3D:222-226 (CCEM delegates at CCEM:311-314);
  - NavMeshEntityMovement.cs:139-143;
  - RigidBodyEntityMovement2D.cs:114-118;
  - Simple* variants;
  - removal at `OnNetworkDestroy`.
- **SMC:92-97 is verified**: SMC = `B:Assets/_Game/Scripts/Space/Ships/ShipMovementComponent.cs`, and lines 92-96 are the same TryRemove/TryAdd plus `RefreshSimulationMode()`. Note that SMC also implements **`IEntityMovementComponent`** (SMC:35-36), so it *is* `entity.Movement`. The spec's `ServerMeshShipMovement : IEntityMovementDataHandler, IMeshCompactStateSource` does **not**. That leaves `ship.Movement == null`, with these consequences:
  - `StopMove` and `UpdateMovementEnabling` are null-safe (MoveFunctions.cs:286-292; BGE:363);
  - `Teleport` on the ship, or on a seated pilot, never executes: it parks `_isTeleporting` (MoveFunctions.cs:348-357), and `ApplyAuthoritativeLateUpdate` retries only when `ActiveMovement != null` (BGE:463-467).
  - So the ship's teleport must be its own API, not `BaseGameEntity.Teleport`.
- Order hazard: `LiteNetLibIdentity` calls `OnIdentityInitialize` on `GetComponentsInChildren<LiteNetLibBehaviour>()` in component order (LNL/GameApi/LiteNetLibIdentity.cs:584, 605-609), and the last TryAdd wins. **The ship prefab must not also carry a kit movement component.** Copying TestMount-CE's CCEM would make CCEM and the ship handler fight over the slot.
- Upload acceptance: the server drops client states unless `dataHandler.ConnectionId == sender` (BGNM:586). A phantom-owned mirror must therefore be owned by the phantom connection, and a seated server pilot resets that owner (see Enter above).

## 2. GameInstance.AddVehicleEntities and LiteNetLibAssets.RegisterPrefab

- `public static void AddVehicleEntities(params VehicleEntity[] vehicleEntities)` (Kit/Core/Scripts/GameInstance/GameInstance_Data.cs:834-837) and `public static void AddVehicleEntities(IEnumerable<VehicleEntity> vehicleEntities)` (:839-842) both lead to `AddManyGameEntity(VehicleEntities, …)` (:1029-1038) and then `AddGameEntity` (:1040-1056):
  - it skips null;
  - a **scene object** only gets `PrepareRelatesData` and is not stored;
  - otherwise it stores under `Identity.HashAssetId`, and **the first one wins**: an existing non-null entry is never replaced.
  - The dictionary is `static Dictionary<int, VehicleEntity> VehicleEntities` (:47). It is cleared only by `GameInstance.ClearData()` (GameInstance.cs:1671, 1705).
- `public LiteNetLibIdentity RegisterPrefab(LiteNetLibIdentity prefab)` (LNL/GameApi/LiteNetLibAssets.cs:173-183): null gives a warning and returns null; otherwise `GuidToPrefabs[prefab.HashAssetId] = prefab`, which **silently overwrites** on a hash clash. `UnregisterPrefab` is at :185-194.
- Lifecycle (this matters for "when"):
  - `BGNM.StartServer()` → `InitPrefabs()` → `base.StartServer` (BGNM:244-248). The client calls `InitPrefabs()` in `StartClient` when it is not a host (BGNM:276-282).
  - `InitPrefabs` copies `GameInstance.VehicleEntities.Values` (and the others) into `Assets.spawnablePrefabs` (BGNM:1009-1061), then calls `BaseGameNetworkManagerComponent.InitPrefabs(this)` (BGNM:1119-1122; the hook is at BaseGameNetworkManagerComponent.cs:42).
  - On every online-scene load, `Assets.Clear(true)` wipes `GuidToPrefabs` (LNGM:226-235; LiteNetLibAssets.cs:117-124). Then `Assets.Initialize()` → `RegisterPrefabs()` re-registers from `spawnablePrefabs` (LiteNetLibAssets.cs:104-115, 140-171; LNGM:375, :890 on the client). `OnStopServer` and `OnStopClient` also call `Assets.Clear()` (LNGM:511, :521).
  - **Consequences**:
    1. Call `GameInstance.AddVehicleEntities(shipPrefab)` **before** `StartServer` / `StartClient`. Then `InitPrefabs` registers it automatically on every start and every scene load, including the client's cell-to-cell migration restarts, and `RegisterPrefab` is redundant.
    2. A bare `Assets.RegisterPrefab` made at runtime works only until the next scene load or stop, and must run **after** `Assets.Initialize` (`Assets.onInitializeFinish`, LiteNetLibAssets.cs:42/113). The add-on does exactly this lazily for harvestables (Ghosting:2119-2145, 2225, 2248), and it is idempotent.
    3. The client must register too, or else the client-side `GetObjectInstance` returns null for an unknown hash (LiteNetLibAssets.cs:318-340). The server-side `NetworkSpawn(int hash…)` warns "is not registered" and returns null (:445-454).
- Pitfalls:
  - `HashAssetId` comes from the serialized string `assetId` (LNI:38, 115-140). It is auto-assigned from the asset GUID only through editor `OnValidate`, when it is empty or `"0"` (LNI:305-310, 334-339). A prefab created by a build-window script should get `identity.AssetId = AssetDatabase.AssetPathToGUID(path)` explicitly. Copying TestMount-CE.prefab's YAML would duplicate `assetId: 32fd653a…` (TestMount-CE.prefab:324), which collides with the mount: AddGameEntity keeps the mount and RegisterPrefab overwrites it.
  - Instances are pooled: `GetObjectInstance` sets `IsPooledInstance = true` (:330-336), so the ship component must reset its state in Clean or on reuse.
  - Adding the ship to `VehicleEntities` also makes it a legal `mountEntityId` for character ghosts (Ghosting:1797).

## 3. BGE:323, the unobserved early-out, and disabling a seated pilot

- BGE:318-324: `if (IsServer && IsOwnedByServer && Identity.CountSubscribers() == 0 && !NeedsServerUpdateWhenUnobserved) return;`. This **skips** `EntityUpdate`. It does not keep simulating.
- The real mechanism: `EntityUpdate` → `UpdateMovementEnabling` (BGE:343-345, 361-375) is the only thing that sets `Movement.enabled = PassengingVehicleEntity.IsNull() && !MovementDisableState.IsActive`. When it is skipped, a pilot seated while unobserved keeps its movement component **enabled**, and that component runs on its own `UpdateManager` registration (CCEM:298-309). That is the true meaning of "would otherwise keep simulating". The spec's conclusion is right, but its citation explains the cause backwards.
- The seat snap always runs, even when unobserved (`ApplyAuthoritativeLateUpdate`, BGE:443-461), so the pilot's transform follows the seat either way.
- How to disable:
  - `pilot.MovementDisableState.Add(key)` (`StateFlag`, BGE:172; Types/StateFlag.cs:9-12; cleared in BaseGameEntity_Cleanup.cs:47);
  - plus `pilot.Movement.StopMove(); pilot.Movement.enabled = false;`, as the kit does itself at BGE:369-372.
  - While seated, observed frames keep it disabled anyway, because `PassengingVehicleEntity` is non-null. The explicit `enabled = false` is the operative part. The key only guards the moment the seat clears.
  - `NeedsServerUpdateWhenUnobserved` (BGE:319, protected virtual) is the per-type opt-out.
- **Missed cost**: a seated pilot keeps its **own** movement handler in `EntityMovementDataHandlers`. The kit server loop has no passenger filter (BGNM:727-737). `GetSyncTransformEntity`, the passenger-to-vehicle redirect, exists only on the client upload path (BGNM:654, 669-690). So every seated kit pilot adds a kit character record per subscriber per tick, on top of the ship's 54/56/28 B. With `Movement.enabled = false`, the BIEMF3D write still runs (`_isStarted` gate only, BIEMF3D:1335-1377). The step-7 byte acceptance ("meter records 54, 56, 28") must account for this, or pilots must not exist on `MESH_SHIP_WIRE` byte rows.

## 4. Kit movement-state send loop and CE-12 as it stands today

- Tick: `BGNM.OnServerUpdate` → `base.OnServerUpdate`, which runs the LNGM sync pass, ping, then interest (LNGM:109-122). Then comes `SendServerEntityMovementState` **if `DefaultGridManagerComponent.Instance.IsDisabled`**, otherwise `SendServerEntityMovementStateJob` (BGNM:131-139). The grid is **disabled by default** (`isDisabled = true`, Networking/GridManager/DefaultGridManagerComponent.cs:20-22), so the non-job path is live.
- Non-job loop `SendServerEntityMovementState` (**BGNM:691-800**; the sheet's BGNM:661 has drifted to 691):
  - per player: `player.GetSubscribingObjectIds()` → `TryGetValue` → `WriteServerState(ts, StateDataWriter, out reliable)`; `false` skips the record (:727-736);
  - the record is `PutPackedUInt(objectId) + Put(int length) + data` (:741-743 and :767-769);
  - reliable goes out `ReliableOrdered` and unreliable goes out `Unreliable`, both on `BaseGameEntity.MOVEMENT_DATA_CHANNEL` (:780, :791), with a split at `MAX_UNRELIABLE_PACKET_SIZE` (:751).
- Job loop (BGNM:801-983, grid enabled only): it calls **`CreateMovementData`**, not `WriteServerState` (:867), and the grid manager serializes in kit format (:901). A ship's custom wire would **not** be used there. It also skips players without a PlayerCharacter entry (:828-829). The ship must still implement `CreateMovementData`, an interface member (IEntityMovementDataHandler.cs:13).
- CE-12 markers present today, committed in 3659b8d:
  - the interface and the pass at Gameplay/Interfaces/IEntityMovementDataHandler.cs:20-66;
  - the field at BGNM:110-111;
  - `_serverStateFlagsPass.Begin()` at :704-705 and :820-821;
  - `Record(dataHandler)` after each successful write at :737 and :868;
  - `ConsumeAll()` after the last player at :799 and :982;
  - BIEMF3D:10 (implements `IEntityMovementServerStateFlags`), :1329-1330, :1373-1386 (`ConsumeServerStateFlags` clears `_sendingJump`, `_sendingDash`, `_isTeleporting`, `_stillMoveAfterTeleport`);
  - NavMeshEntityMovement.cs:13.
- **Discrepancy with §0.9**: the kit already carries a consume-after-the-last-player **post-pass** (`EntityMovementServerStateFlagsPass`), which §0.9 says is "refused" and "until CE-12 lands". The add-on has already adapted (TestMap/Phase0/ServerMeshCe12Shape.cs:9-22, ServerMeshCe12Check.cs:106-117). For step 7 this means:
  - ship one-shot flags, if any, should implement `IEntityMovementServerStateFlags`;
  - the spec's "reliable while a teleport is unacknowledged" is an ack rule (as at SMC:390 and :366-372), not a one-shot flag, so the ship needs no Consume;
  - a handler that returns false is never Recorded, so it never gets consumed.
- Sender interplay:
  - `MeshKitWrapQueue` wraps only default-allowed handler types plus those passed to `AllowHandlerType` (Mesh/Sync/MeshKitWrapQueue.cs:27, 75-76), so the ship handler is not wrapped. That matches "never through the adapter".
  - Direct sources join through `MeshStateSender.Register(IMeshStateSource)` (Mesh/Sync/MeshStateSender.cs:305-331).
  - The ship stays in the kit dictionary, so to satisfy "the sender writes each ship once per build" its `WriteServerState` must return false while it is registered with the sender. That is the `MeshKinematicCharacterBody.SentBySender` pattern (MeshKinematicCharacterBody.cs:90-95). Without it, the kit loop sends a second copy.

## 5. SyncFieldFloat and [AllRpc]

- Declaration: a field on any `LiteNetLibBehaviour`, found by reflection on instance fields of subclasses of `LiteNetLibSyncElement` (LNL/GameApi/LiteNetLibBehaviour.cs:193-204, 215-258). The pattern is `[SerializeField] protected SyncFieldFloat shield = new SyncFieldFloat();` (B:ShipCombatComponent.cs:82). The default `syncMode = ServerToClients` (LiteNetLibSyncField.cs:14), so a server-only write is fine. `SyncFieldFloat` is declared at LiteNetLibSyncField.cs:520-538 (4-byte float, `!=` compare).
- Cost per write (`Value` setter, LiteNetLibSyncField.cs:124-159):
  - an unchanged value returns early;
  - a changed value calls `OnChange`, then `ValueChangedState` (:75-100), which sets `_currentRedundancy = 1 + redundancyCount(2)` on a non-reliable-only transport and calls `RegisterUpdating` → `RegisterServerSyncElement`. That does a **`List.Contains`, O(dirty elements)** (LNGM.StateSyncing.cs:636-640).
- Send (`ProceedServerGameStateSync`, LNGM.StateSyncing.cs:366-412, once per logic tick, LNGM:111):
  - for **each ready player × each dirty element**, `Subscribers.Contains` (LiteNetLibSyncElement.cs:9-12), then a delta append;
  - deltas go `Unreliable` on channel 0 (:457-500+);
  - a baseline every `baseLineSyncInterval` = 1 s (LNGM:40) goes `ReliableOrdered` (:434-455);
  - `Synced` decrements the redundancy and unregisters only on a baseline (LiteNetLibSyncField.cs:102-112).
  - A field written every frame while recharging is therefore resent to every subscriber every tick. Each frame's write also pays the O(n) Contains, plus the per-tick P×D loop.
- `[AllRpc]` is `AllRpcAttribute : RpcAttribute` (LNL/GameApi/Attributes/AllRpcAttribute.cs), with `canCallByEveryone` (RpcAttribute.cs:11).
  - Kit usage: `[AllRpc] private void RpcOnVehicleDestroy()` called as `RPC(RpcOnVehicleDestroy, Identity.DefaultRpcChannelId, DeliveryMethod.ReliableUnordered)` (VehicleEntity.cs:327-337). Character RPCs use `RPC(RpcOnExitVehicle)` (BaseGameEntity_MountFunctions.cs:288-297).
- How an RPC is sent:
  - `RPC(func, p1…)` defaults to **`ReliableOrdered`** on `DefaultRpcChannelId` (LiteNetLibBehaviour.cs:874-906). `LNI.defaultRpcChannelId = 0` (LNI:52, 170). Explicit overloads such as `RPC<T1>(func, byte channel, DeliveryMethod, T1)` are at :931-936+.
  - It resolves through `RPC(string, channel, delivery, params object[])` (:1151-1168): `MakeRPCId` builds a string with ZString (:1198+) and looks up `_allRpcIds`.
  - Server side, `SendCall` with `All` (LNL/GameApi/LiteNetLibRPC.cs:76-101) iterates **every connection** (`manager.GetConnectionIds()`, LiteNetLibManager.cs:331) rather than subscribers. It checks `HasSubscriberOrIsOwning` for each, and **re-serializes the packet for each receiver** (:89-92).
  - On a dedicated server it also runs the method body locally (`HookCallback`, :95-100), so `RpcFired` and `RpcImpact` must early-out on the server, as B does (B:ShipCombatComponent.cs:263-268).
  - Allocations per call: a delegate from the method group, `params object[]` with boxing, and the RPC-id string. That breaks §0.2's "no allocation per tick" for `ServerMeshSyncLoad`'s R RPCs/s, unless the cost is accepted and logged.

## 6. Spawned-object enumeration and Physics

- **`GetSpawnedObjectValues` does not exist in our kit.** It is a repo-B LNL patch: `public IEnumerable<LiteNetLibIdentity> GetSpawnedObjectValues() => SpawnedObjects.Values;` (B:Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/GameApi/LiteNetLibAssets.cs:68), used by B:ShipAIPilot.cs:117 (a 0.5 s scan, `targetScanInterval`, :27, :104-133).
  - Ours has `public Dictionary<uint, LiteNetLibIdentity>.Enumerator GetSpawnedObjects()` (LiteNetLibAssets.cs:672-675), a struct enumerator with no allocation. `SpawnedObjects` itself is `internal` (:65), and LiteNetLibManager is its own asmdef (LNL/LiteNetLibManager.asmdef; InternalsVisibleTo only Tests, LNL/AssemblyInfo.cs:3).
  - The add-on already uses the `GetSpawnedObjects()` pattern (Mesh/ClientMigrationController.cs:160, 503; Mesh/Sync/MeshStateSender.cs:570). Also available: `TryGetSpawnedObject<T>` (:660-670), and `Assets.onObjectSpawn` / `onObjectDestroy` (:55-56) for keeping a ship list instead of a full scan with `GetComponent`.
- Physics in the kit, all NonAlloc:
  - `Physics.SphereCastNonAlloc` (Gameplay/DamageEntities/MissileDamageEntity.cs:221);
  - `RaycastNonAlloc` (MissileDamageEntity.cs:213; ProjectileDamageEntity.cs:170; DefaultBuildAimController.cs:181);
  - `PhysicUtils.SortedRaycastNonAlloc3D` (RaycastDamageInfo.cs:109);
  - `OverlapSphereNonAlloc` (HarvestableSpawnArea.cs:170; NearbyEntityDetector.cs:177).
  - B's pilot uses the allocating-free `Physics.SphereCast(pos, r, dir, out hit, dist)` (B:ShipAIPilot.cs:148). The add-on has no Physics casts.

## 7. Existing ship or rigidbody vehicle mover?

- None in our repo. The kit has 3D movers `CharacterControllerEntityMovement`, `NavMeshEntityMovement` and the Simple* variants, plus only **`RigidBodyEntityMovement2D`** (Gameplay/EntityMovementSystems/RigidBodyEntityMovement2D.cs:12; 2D). It has no 3D rigidbody mover.
- The add-on has no ship types. "ship" appears only in the phantom parser placeholders, which refuse it until step 7 (TestMap/Phantoms/ServerMeshPhantomSettings.cs:218-220, 325), and as the verb "ships" in comments in Mesh/Editor/ServerMeshPackageBuilder.cs:223, 250. Strictly, the §0.4 rule "Mesh/ never uses the word 'ship'" is already broken by that comment wording, though it is harmless for the type-name check.
- The only vehicle prefab is TestMap/Prefabs/TestMount-CE.prefab (VehicleEntity, CCEM, GameEntityModel and LiteNetLibIdentity).
- The real model is B's `ShipMovementComponent` (rigidbody, `IEntityMovementComponent` plus `IEntityMovementDataHandler`, SMC:35-36, 87, 115), together with `ShipEngine` and `ShipAIPilot` (read only, §0.8).

## 8. Ghost pipeline claim (critique 13)

- The citation "Ghosting:434, 574-612" has drifted. Today:
  - `MeshGhost.entity` is `BaseCharacterEntity` (Mesh/MeshMapNetworkManager_Ghosting.cs:61-67);
  - the spawn dispatch `OnAssetObjectSpawn` (:585-600) handles monster, item drop, harvestable and building, with **no VehicleEntity branch**;
  - `TrackOwned(BaseCharacterEntity)` is at :609.
- The claim holds, with one caveat: a vehicle **rides along with a character ghost** (`MeshGhost.vehicle` :67; snapshot :789; `MountGhost` :1792-1860). If seated pilots are monsters and `ghostMonsters` is on, a pilot near a band edge is ghosted, and the neighbour spawns a ship puppet from `GameInstance.VehicleEntities`. Pilots should be excluded from ghosting, or should not be monsters.

## Spec corrections

1. **`GetSpawnedObjectValues` does not exist here** (repo-B LNL patch, B:LiteNetLibAssets.cs:68). Use `Assets.GetSpawnedObjects()` (LiteNetLibAssets.cs:672), or better, a list kept from `onObjectSpawn` / `onObjectDestroy`.
2. **BGE:323 is an early-out that skips `EntityUpdate`**. It is not "keeps simulating". Restate: unobserved, `UpdateMovementEnabling` (BGE:361-375) never runs, so a seated pilot's movement component stays enabled on its own update (CCEM:298-309). The fix (`Movement.enabled = false`, plus the key) is right.
3. VehicleEntityExtensions.cs:11-14 is correct. Add:
   - the kit has no opt-out (`IsDestroyWhenDriverExit`, VehicleEntity.cs:73, is unused, and B's `IKeepWhenDriverExits` is a repo-B kit patch);
   - **destroying or despawning a seated pilot also destroys the ship** (BGE:256-263);
   - seat pilots only through `EnterVehicle`, never `SpawnMount`.
4. `EnterVehicle` for seat 0 sets the ship's owner to the pilot's connection (BaseGameEntity_MountFunctions.cs:106-107). `MESH_LOAD_SHIP_PILOTS` together with `mirror` authority would steal phantom ownership. Forbid that combination, or seat after setting the owner and restore it.
5. `ServerMeshShipMovement` lacks `IEntityMovementComponent`, unlike SMC:35-36. Then `ship.Movement` is null: `BaseGameEntity.Teleport` silently never applies (MoveFunctions.cs:348-357; BGE:463-467), and kit drivers cannot steer it. That is acceptable for the rig, but say so, and give the ship its own teleport. The prefab must not carry a kit movement component, or the `OnIdentityInitialize` slot race decides the handler (LNI:584, 605-609).
6. Registration: `AddVehicleEntities` is GameInstance_Data.cs:834-842 and `RegisterPrefab` is LiteNetLibAssets.cs:173-183 (both correct). The order matters:
   - call `AddVehicleEntities` **before StartServer / StartClient**; `InitPrefabs` (BGNM:1009-1061) then registers it on every start and scene load, including client cell migrations;
   - a bare `RegisterPrefab` is wiped by `Assets.Clear` on each scene load and stop (LNGM:235, 511, 521) and must follow `Assets.Initialize`;
   - set `LiteNetLibIdentity.AssetId` explicitly in the build window (it is auto-assigned only by `OnValidate`, LNI:305-339), and never copy TestMount-CE's `assetId`;
   - the first `AddGameEntity` wins and `RegisterPrefab` overwrites on a hash clash.
7. Byte acceptance: seated kit pilots still emit their own kit movement record each tick (no passenger filter in BGNM:727-737; the redirect is client-only, BGNM:654, 669-690). Either keep pilots off the byte rows, or count them separately.
8. "The sender writes each ship once per build": the ship stays in the kit dictionary, so its `WriteServerState` must return false while it is registered with `MeshStateSender` (the `SentBySender` pattern, MeshKinematicCharacterBody.cs:90-95). The custom wire is used only on the non-grid path (BGNM:131-139); the job path uses `CreateMovementData` (BGNM:867).
9. CE-12 (§0.9) is stale: a consume-after-the-last-player pass is already in the kit (BGNM:704-705, 737, 799, 820-821, 868, 982; IEntityMovementDataHandler.cs:20-66; BIEMF3D:1378-1386). The ship's ack-based reliable rule needs no Consume. Other drifted citations:
   - `SendServerEntityMovementState` is BGNM:691, not :661;
   - the wrapper-false skip is BGNM:735-736, not 703-704;
   - the client-state parser is BGNM:591-624, not 589-618;
   - "Ghosting:434" is now :61-67 and :585-600.
10. `[AllRpc]`: the default `RPC(func, …)` is ReliableOrdered on channel 0. The server loop runs over **all connections**, re-serializes per receiver, also runs the body on the server, and allocates per call (delegate, `object[]`, id string). `RpcFired` and `RpcImpact` must early-out on the server, and `ServerMeshSyncLoad`'s R/s cost includes these allocations, so log them instead of claiming gc0.
11. A shield `SyncFieldFloat` written every frame costs, per changed write, an O(dirty) `List.Contains` (LNGM.StateSyncing.cs:636-640), and an unreliable delta per subscriber every tick (redundancy 3) plus a 1 s reliable baseline. State this in the D-dirty-fields model.
12. A ship whose HP reaches 0 through kit damage is destroyed after 2 s and not respawned (VehicleEntity.cs:361-397). `ServerMeshShipCombat` should own its hull (`canBeAttacked=false`) or expect that. VehicleEntity also forces `vehicleLayer` and `vehicleTag` (VehicleEntity.cs:92-93) for the cast masks.
