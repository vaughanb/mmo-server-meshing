# Survey 7 (add-on side): what Phase 0 step 7 plugs into

Read-only survey, 2026-10-03, working tree at 3659b8d plus the uncommitted step 6b edits. Paths are relative to
`Assets/ServerMeshing/` unless they start with `Tools/` or `OpenMMORPG/`. All rig and Mesh code sits in namespace
`MultiplayerARPG.MMO.Mesh` (Sync engine: `MultiplayerARPG.MMO.Mesh.Sync`; tests: `MultiplayerARPG.MMO.Mesh.Tests`).

## 1. Step 6 sender: what a ship source must be

**Contract** (`Mesh/Sync/MeshSyncContracts.cs`)
- `IMeshStateSource` :11-34: `ObjectId`, `Identity`, `DiscreteVersion`, `IsAtRest`, and `WriteState(long sampleTimestamp, NetDataWriter, out bool sendReliably)`. WriteState runs "at most once per build, only appends (never Reset or SetPosition), changes no state (a second call writes identical bytes)" (:29-31).
- `IMeshCompactStateSource : IMeshStateSource` :37-41: `bool WriteCompactState(long, NetDataWriter)`, "never sent reliably".
- `IMeshStateRegistry` :44-60: `Register`, `Unregister`, `MarkRelevanceChanged`, `MarkSourceChanged`, `MarkObserverChanged`. It is implemented by `MeshStateSender` (a component on the map manager's GameObject), so rig code reaches it with `mesh.GetComponent<IMeshStateRegistry>()`. The manager's own field `_stateSender` is private (`Mesh/MeshMapNetworkManager.cs:95`).
- `MeshStateVariant` :63-70 (Full=0, Compact=1). `MeshObserverContext` :73-94 has a NaN distance when unknown. `MeshSendRate` :97-130. `IMeshSendPolicy` :133-137.
- Nothing in this file is ship-specific. "ship" appears nowhere in Mesh/Sync.

**Discovery: a ship handler registered as the kit handler is NOT picked up automatically**
- `MeshStateSender.Configure` :570-572 enqueues every spawned identity. `OnObjectSpawn` :603-607 enqueues new ones. Both go to `MeshKitWrapQueue`.
- `MeshKitWrapQueue.RefusalOf` :261-262 refuses any slot handler that `is IMeshStateSource` (`Refusal.DirectSource`). The warning "its handler is a state source of its own" is logged once per type (:272-279), and `Refusals++` runs on every such spawn (:233). Every ship spawn therefore adds one to the Sender window's `refusals`.
- The handler type is not allowed either: the default allow list is exactly `BuiltInEntityMovementFunctions3D` and `NavMeshEntityMovement` (`MeshKitMovementStateSource.cs:100-103`). `MeshKitMovementStateSource`'s constructor throws if the inner handler is a state source (:31-32).
- So the ship must call `IMeshStateRegistry.Register(this)` itself. `Register` (`MeshStateSender.cs:305-331`) returns false and logs once per type+reason when:
  - the sender is not `IsSending` (setting off or measure, not started, or not yet configured; it configures on the first `BeforeKitTick` after start, :406-409);
  - `MESH_SENDER_SOURCES` excludes direct;
  - the source is the kit adapter;
  - the fan-out refuses it (duplicate id or no identity; `MeshStateFanout.Register` :127).
  A ship spawned before the first configured tick is refused, so it needs a retry.
- **"Written once per build" means** registered directly AND its kit-facing `WriteServerState` returns false while registered. Otherwise the kit loop writes it once per subscribed player (OpenMMORPG `BaseGameNetworkManager.cs:726-735`, a write per player per handler) and the sender writes it as well. The precedent is `MeshKinematicCharacterBody.SentBySender` (:95, :319-329): WriteServerState returns false while set, WriteState always writes.
- The fan-out writes each registered source once per build (`MeshStateFanout_Routing.cs:53-128`, one scratch write behind a 4-byte object-id guard, `MeshStateFanout.cs:46`). The compact payload is written at most once per build, and only when some pair needs it (`HasCompact` :371-378). Urgent, boosted and first-send records always use Full (:184-185). Reliable records go to every subscriber (`AppendReliable`).
- **Two-writer cleanup gap.** The sender clears the "sent by sender" flag only for `MeshKinematicCharacterBody`, by type check: `OnStopServer` :499-501, `OnObjectDestroy` :619-620, `OnSourceDropped` :640-641, `MeshKitWrapQueue.OnDropped` :173-177. Mesh/ cannot name a TestMap type. A dropped ship source (append-only break, `MeshStateFanout.cs:44`; identity gone, :41) would keep returning false to the kit and go silent. There is no public `IsRegistered` on the sender (`MeshStateFanout.IsRegistered` :163 sits behind the private `_fanout`).
- `MESH_SENDER_COMPACT=far` (`MeshStateSender.cs:197-211`) makes `MeshDistanceSendPolicy` give the far tier `Compact` only to `IMeshCompactStateSource` sources (`MeshDistanceSendPolicy.cs:78`). The owner gets ownerHz (:71). Its default is off.
- A direct source may stay reliable with no warning: `stuckReliable` watches adapter sources only (`MeshStateFanout_Routing.cs:141-146`; step6_design decision 24).
- Sender window (`MeshStateSenderStats`, parsed by `Tools/ServerMeshing/p0sender.py:29-46`, regex ends in `$`) carries `sources kit direct`, `builds`, `writes`, `compact`, `refusals`. The step 6 design defines "once per build" as **Writes = Builds × Sources** (step6_design.md:250). The line has no per-source-type split: with characters wrapped too, the ship count must come from `direct`, or `MESH_SENDER_SOURCES=direct`.
- Observer anchor: the player's character, else the first owned object (`MeshStateSenderPorts.cs` `TryGetObserverAnchor`). So a phantom that owns only a ship is anchored at its ship, which is good.
- `MeshMovementHandlerSlots` (`Mesh/Sync/MeshMovementHandlerSlots.cs`):
  - `For(manager)` :40-45;
  - `TryInstall(id, layer, replaceable, out error)` :53-102 (a replaceable layer must implement `IMeshReplaceableHandlerLayer`);
  - `TryRemove` :109-132, `TryGetKitHandler` :138-147, `HasLayer` :150-155, `Holds(id, layer)` :161-168, `Forget` :174-180, `Reset` :183-186, event `LayerRemoved` :37.
  - §0.13: only this class may replace a slot entry for Mesh purposes. A ship that registers itself at `OnIdentityInitialize` (like the kit's `BuiltInEntityMovementFunctions3D.cs:222-226`: TryRemove then TryAdd) is the kit handler, not a layer. The "registration check" §0.13 names is `TryGetKitHandler(id)` being the ship component.

## 2. Kinematic puppets and the ghost pipeline

- `MeshKinematicCharacterBody` (`Mesh/MeshKinematicCharacterBody.cs`), `IEntityMovementDataHandler, IMeshStateSource`:
  - ctor `(objectId, identity, transform, kitHandler, kitIsNavMesh, MeshKinematicTrack)` :48-63.
  - `Refusal(kitHandler, mounted, summoned, gridDisabled)` :103-114 accepts only BuiltIn3D or NavMesh handlers.
  - `Begin(BaseCharacterEntity, slots, out error)` :138-182: StopMove, `TryInstall(..., false)`, both disable keys, `movement.enabled=false`. `End` :190-214.
  - `Accept(pos, vel, yaw, flags, extra, discontinuity, now)` :222-238, `Tick(now)` :256-262, `ApplyNow` :269-277.
  - `WriteKitTransform` :121-130 writes the kit layout (yaw only).
  - `SentBySender` :95, `WriteServerState` :319-329, `WriteState` :335-340.
  - It is character-only: it needs a `BaseCharacterEntity` with an `IEntityMovementComponent`.
- `MeshKinematicTrack` (`Mesh/MeshKinematicTrack.cs`): a mutable struct with ctor `(blendRate=15, maxExtrapolationSeconds=0.3, snapDistance=4)` :40. `TryAccept(pos, vel, yawDegrees, discontinuity, receivedAt)` :86, `Pose(now, lead, out yaw)` :143, `DropOffset` :156. **Yaw only, no quaternion and no angular velocity**, so it cannot pose a ship. `ServerMeshShipPuppetBody` needs its own rotation track; reuse is limited to position, velocity and blend.
- Manager wiring (`Mesh/MeshMapNetworkManager_Puppets.cs`):
  - `EnsurePuppetMotion` :105-144 builds the body. It registers with the sender (`_stateSender.Register(body)` then `SentBySender=true`, :140-141).
  - `LeaveStateSender` :204-208. `PoseKinematicPuppets` :240-257 ticks observed puppets at the start of each kit tick. `FeedKinematicPuppet` :267-290.
  - All of these are private and operate on private `MeshGhost` (`Ghosting.cs:61-67`). A feeder cannot call them and must reproduce Accept/Tick/ApplyNow itself.
- **Ghost pipeline is character-only (confirmed; cites drifted):**
  - `MeshGhost.entity` is a `BaseCharacterEntity` (`Ghosting.cs:63`). `vehicle` (:67) exists only as a ghost's mount.
  - The owned table is `OwnedTable<BaseCharacterEntity> _ownedCharacters` at **:445** (sheet: :434, which is now a comment).
  - `OnAssetObjectSpawn` **:586-604** tracks monsters, drops, harvestables and buildings, never a `VehicleEntity`. `TrackOwned(BaseCharacterEntity)` :609-612.
  - A ship spawned on a cell is therefore never ghosted, which is good for isolation.
- **Hazard (pilots):** a tracked character that rides a vehicle is snapshotted with `mountEntityId` (`Ghosting.cs:789`). The neighbour then spawns `GameInstance.VehicleEntities[mountEntityId]` as a mount (`SyncGhostMount`/`MountGhost` :1788-1830). If ship pilots are spawned like load players through `mesh.TrackSyntheticEntity` (`Ghosting.cs:110`, internal; `ServerMeshLoadGenerator.cs:186`), every band neighbour spawns real ship prefabs as mounts. Pilots must not be tracked.
- `GhostSnapshotMessage` (`Mesh/CellMeshLink.cs:353`, public, `INetSerializable` with `Serialize`/`Deserialize`) is what a character feeder can serialize and deserialize in-process to cost ℓ. No ship snapshot type exists. The ship feeder's "serialized snapshot" would be `ServerMeshShipWire` Header56 bytes.
- An in-process character puppet for the feeder needs:
  - a spawned player entity, **not** tracked (so it is not ghosted);
  - `MeshMovementHandlerSlots.For(mesh)`, `TryGetKitHandler`, and the `Refusal` check;
  - a `new MeshKinematicTrack(snapDistance: mesh.ghostTeleportDistance)`;
  - `Begin`, then optionally `Register` + `SentBySender=true`;
  - per snapshot: `Accept`, then `Tick` (observed) or `ApplyNow` (unobserved), as the manager does;
  - and at the end: Unregister, then `End`.

## 3. Load generator (TestMap)

- `TestMap/ServerMeshLoadGenerator.cs`:
  - keys `MESH_LOAD_PLAYERS`/`_CELL`/`_UNCAPPED`/`_AREA`/`_PLAN`/`_SEED` :19-24, `DefaultSeed = 12345` :25;
  - `AutoStart` uses `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` :37-61 and adds `[Mesh] LoadGenerator`;
  - `Update` polls once a second until `mesh.IsServer && OwnCell != null && IsServerReadyToInstantiateObjects()` :81-122;
  - `ParsePlan` (`cell:count,...`, OrdinalIgnoreCase, null on error, logs) :65-79. It is private, untested, and returns a dictionary, so duplicate cells silently overwrite. The model for `ServerMeshShipLoadPlanTests` has to be a new pure parser with error strings.
- Spawn path :140-198:
  - `Assets.GetObjectInstance(hash, pos, rot)`, then `GameInstance.SetupByMetaData`, `data.CloneTo`, `Assets.NetworkSpawn(spawnObj)`, `ForceMakeCaches`/`FillEmptySlots`/HP;
  - then `mesh.TrackSyntheticEntity(entity)` (step 7 pilots must skip this);
  - then `StartMotion`.
- Seeds:
  - `ReadSeed` :242-251 (bad value: warn, use the default);
  - `BrainSeed(seed,k) = (seed*486187739) ^ (k*16777619+97)` :254-257. The phantom binder uses the same mix (`ServerMeshPhantomClients_Spawn.cs:97`);
  - shared placement draws `System.Random(seed)` over the map ground and keeps the points inside the own cell (:278-327); single-cell placement is :330-356.
  - Ship placement and flight paths should derive from `MESH_LOAD_SEED` with their own salt.
- `TestMap/Phase0/ServerMeshLoadMotion.cs`:
  - `ResolveX(text, out warning)` / `ReadX()` pattern :66-108;
  - `Printable` turns braces into parentheses :127.
  - This is the C# env-reading convention: there is **no shared settings helper**. Each class calls `System.Environment.GetEnvironmentVariable` and has a pure `TryParse`/`Resolve` with an error string. `ServerMeshPhantomSettings.TryResolve(Func<string,string>)` :495 is the testable multi-key form.
- `TestMap/Phase0/ServerMeshKinematicWalk.cs` disables movement explicitly with `entity.MovementDisableState.Add(this); entity.Movement.enabled = false;` :168-169, undone at :220. This is the precedent for "disable key plus Movement.enabled = false" on seated pilots.

## 4. Phantoms: adding the `ship` kind

**Settings** (`TestMap/Phantoms/ServerMeshPhantomSettings.cs`)
- `ServerMeshPhantomKind { Character, Eye }` :19-25. `ServerMeshPhantomPayloadKind { Transform, Input }` :28-34.
- `ParsePhantom` refuses `ship` with "the kind ship needs step 7" (:218-222). The kind-error text "is not character or eye" is at :225 and :194.
- `TryParsePayload` refuses `ship56|ship28` with "needs step 7" (:325-328).
- `KindName` and `PayloadName` are two-valued (:154, :157). The install line is at :147.
- `TryParseView`: least V is 1 for character and 0 for eye (:257-263). Area/stand/fight are character-only (:523, :575-581). Steps are eye-only (:345).
- The grammar has **4 fields**: `<virtual|sink>:<P>:<kind>:<cell[,cell..]|all>` (:43, :194). §0.16 shows only 3.
- Tests that will flip:
  - `ServerMeshPhantomSettingsTests.cs:21` `TryParsePhantom_ShipKind_RefusedUntilStep7`;
  - `:90-91` `TryParsePayload_Ship56/Ship28_RefusedUntilStep7`;
  - `:30` expects "not character or eye".
- Python mirror: `p0phantom.phantom_setting` refuses ship ("refused until step 7", p0phantom.py:285-286), plus `PHANTOM_KINDS` and the docstring; there are also `test_p0phantom` cases.

**Payload interface** (`IServerMeshPhantomPayload.cs:12-21`)
- `TryWrite(writer, moving, teleport, Vector3 position, float yawDegrees, ref bool stopSent, out bool reliable)`. **It carries position and yaw only.** A 56 or 28 B ship upload needs a rotation quaternion, velocity, angular velocity, the flight state and the epoch.
- So `ServerMeshPhantomShipPayload` either widens this interface (it has three implementers: Transform :51, Input :91, and the sink at `ServerMeshPhantomSinkPeer.cs:190-191`) or becomes a second interface or a static writer used beside it.
- Upload frame: `WriteFrame` = `[packed ushort 119][packed long ts][packed uint objectId]` (`ServerMeshPhantomUploads.cs:133-138`). `NextTimestamp` :76.
- The kit routes the upload to `dataHandler.ReadClientStateAtServer` only when `dataHandler.ConnectionId == sender connection` (OpenMMORPG `BaseGameNetworkManager.cs:586-588`). So the mirror ship's `ConnectionId` must be the phantom's connection: spawn with `NetworkSpawn(identity, 0, owner)`, as `SpawnCharacter` does at `_Spawn.cs:140`.

**Binder / feeder are character-typed**
- `ServerMeshPhantomSlot` (`ServerMeshPhantomSlot.cs:8-64`, internal) holds `BasePlayerCharacterEntity entity`, `ServerMeshPhantomEye eye`, walk state, and `FedHere => bound && !sink && entity != null` (:44). It needs a ship field and the `Release` reset.
- `ServerMeshPhantomClients.Plan` :154-252:
  - character/eye branches (`eyes` bool);
  - `ServerMeshPhantomLayout.Build(..., bool eyes, ...)` (`ServerMeshPhantomLayout.cs:91-101`) places groups of size V+1 and spawns **character filler** for V. For ships, V must come from load ships, or ship filler would be needed;
  - picks the payload singleton at :238.
- `TrySpawn` (`ServerMeshPhantomClients_Spawn.cs:63-109`) has eye/character branches; `SpawnCharacter` is :111-147. `Unbind`/`Release` are at `Clients.cs:423-441`.
- **`VehicleEntity.OnIdentityInitialize` sets `Identity.DoNotDestroyWhenDisconnect = true`** (OpenMMORPG `VehicleEntity.cs:139`). Unbinding a ship phantom (disconnect) leaves the ship alive, so `Unbind`/`Release`/`ResetAll` must `NetworkDestroy` it.
- `ServerMeshPhantomUploads.Feed` :183-225 is player-only (`slot.entity`, `IsWaitingClientTeleportConfirm`, `GetMoveSpeed`). The ship feeder needs its own branch, which could reuse `TakeDue`, `FirstDue`, `Unit` and `InShare`.
- Truth/viewErr: `TryGetPosition` (`Uploads.cs:61-73`) answers only for BuiltIn3D/NavMesh kit handlers, so ship records are never compared. `ServerMeshPhantomViewSampler.OnSwallowed` (:134-172) still decodes every record as kit layout and stores garbage positions under ship ids. This is harmless because it is never sampled, but the comment should say so.
- Sink:
  - `ServerMeshPhantomBindReply` kind byte "0 character 1 eye", payload "0 transform 1 input" (:10, :22-25, write :52-53, read :87-88);
  - `ServerMeshPhantomSinkPeer` feeds only `kind == Character` (:172) and picks Transform/Input (:190).
  - Sink ship uploads need a reply kind 2 and a ship payload in the peer.
- Eye precedent: `ServerMeshPhantomEye.Create` builds a **runtime GameObject with a LiteNetLibIdentity, no prefab** (:29-38). Step 5's planned eye prefab was never made. A ship cannot copy this, because real bots must instantiate it, so it needs a registered prefab with a stable HashAssetId.

## 5. Meter, sink and parsers: "record sizes of 54, 56 and 28 B"

- **Nothing measures record sizes today.**
  - `MeshSendMeter` (`Mesh/MeshSendMeter.cs`, shipped) counts calls and bytes per message type, delivery and channel (`Record` :187).
  - It counts EntityState records by count only: `TryReadEntityStateCount` :113-136 (skips type + 2 packed + int32 count); `RecordEntityStateRecords` :157-165; Send window `| records ro u other unread` printed only when counted (:506-509, decision 25).
  - `MeshMeteredTransport.cs:101-106` calls it per 119 send.
- Sink:
  - `ServerMeshPhantomSinkPeer.OnNetworkReceive` :219-223 records type and packet bytes only (`Record(known,type,bytes)` :28);
  - Sink window `t<type> calls bytes` (`ServerMeshPhantomSink.cs:103-135`; p0phantom `SINK_WINDOW` :71).
- A record's size here means the int32 length field in `[packed uint id][int32 length][payload]` (`MeshStatePacketWriter.cs:8-9`, `RecordSize` :122). 54/56/28 are payload lengths. On the wire a record is packed-id + 4 + payload.
- Options:
  - (a) A Mesh/ meter extension: per-record length histogram/min-max, printed only when counted. Game-neutral, but shipped code, and the Send window must stay byte-identical when off. The `MeshSendMeterTests` exact lines must stay.
  - (b) Rig-side counting in the phantom transport's swallow path (`ServerMeshPhantomTransport.cs:265`, where the view sampler already walks records) and in the sink peer's receive.
  - Either way add an optional parser group in p0phantom (Send/Phantom/Sink windows). p0sender's Sender regex is `$`-anchored (p0sender.py:46), so a new group must be optional and placed before `$`.
- The fan-out's `BytesOut` is packets incl. headers (`MeshStateFanoutCounters.cs:71`); there is no per-record size.

## 6. Environment registry

- `Tools/ServerMeshing/p0tools.py`:
  - `ENV_REGISTRY` :33-74 has **30** keys (6 step 2, 4 step 3, 10 step 5, 10 step 6);
  - `PENDING_KEYS` :80-90 has **14** (11 step 7 + 3 step 9);
  - `DROPPED_KEYS` :91-98 has **3**.
  - `test_p0tools.py:288` pins `(30, 14, 3)`, :251 pins the registry set, :295-304 the pending meanings.
- **Step 7 keys already pending** (`_STAGE_0B_STEP_7`, p0tools.py:84-88): `MESH_LOAD_SHIPS`, `MESH_LOAD_SHIP_PILOTS`, `MESH_SHIP_WIRE`, `MESH_SHIP_SCALE`, `MESH_SHIP_PROJECTILES`, `MESH_SHIP_PARK_SLEEP`, `MESH_SHIP_JUMP_SECONDS`, `MESH_RIG_SHIPS`, `MESH_PUPPET_FEEDER`, `MESH_SYNC_LOAD`, `MESH_PREWARM`. Each one that step 7 implements moves to ENV_REGISTRY and into `regress.ENV_KEYS`.
- `Tools/ServerMeshing/regress.py:69-93` `ENV_KEYS` has 50 keys, all registry keys plus older ones such as `MESH_LOAD_SEED` (:72). `request_play` writes every key and clears the rest (:258-259).
- `rigrun.check_env` (rigrun.py:190-209) refuses pending, dropped and unknown keys.
- `MESH_PHANTOM_PAYLOAD`'s registry text "transform or input" (p0tools.py:55) and `MESH_PHANTOM`'s text (:52) must gain ship.
- **Bots get no environment**:
  - `ServerMeshAutoClient.cs:48` reads "bots get no environment, so this is an argument";
  - bots launch at regress.py:240 (`-meshAutoClient -meshUser .. -meshChannel C` + args).
  - So `MESH_RIG_SHIPS=1` cannot reach a bot as env. It needs a bot argument (for example `-meshRigShips`) or registration from another source.

## 7. Prefab and registration conventions

- `TestMap/Prefabs/` holds TestBuilding/TestHarvestable/TestMissile/TestMount-CE prefabs. There is no phantom eye prefab (see §4). `TestMap/GameData/TestVehicleType-CE.asset` and `TestMount-CE.prefab` are the only vehicle assets. There is **no ship/vehicle movement or physics code in the add-on** (no Rigidbody, AddForce or SphereCast anywhere under ServerMeshing). There is no spike folder any more (`Assets/` holds only BaseDemo, OpenMMORPG, ServerMeshing, …).
- `GameInstance.AddVehicleEntities(params/IEnumerable)` is at OpenMMORPG `GameInstance/GameInstance_Data.cs:834-842`, inside `#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES` (:833). Standalone defines `DISABLE_ADDRESSABLES` (`ProjectSettings.asset:850`), so it compiles.
- `LiteNetLibAssets.RegisterPrefab(LiteNetLibIdentity)` is at `ThirdParty/LiteNetLibManager/Scripts/GameApi/LiteNetLibAssets.cs:173-183` (the sheet's path omits `GameApi/`). It writes `GuidToPrefabs[HashAssetId]`.
- `VehicleEntity` (OpenMMORPG `Gameplay/VehicleSystems/VehicleEntity.cs`):
  - `seats` :35, `hp` IncrementalInt :43 (hull 2,500 goes here), `InitStats` :103-111;
  - `OnIdentityInitialize` :126-141 sets `DoNotDestroyWhenDisconnect`;
  - `InitialVehicleEntityComponents` is empty (`DefaultEntitySetting.cs:48-50`), so the kit adds no movement component;
  - `BaseGameEntity.InitialRequiredComponents` caches `Movement = GetComponent<IEntityMovementComponent>()` (`BaseGameEntity.cs:205`). A ship whose movement implements only `IEntityMovementDataHandler` has `Movement == null`. Riders' `ActiveMovement` then returns the vehicle's null Movement (`BaseGameEntity_MoveFunctions.cs:36-43`), which is null-guarded.
- `OnIdentityInitialize` is a `LiteNetLibBehaviour` override (the kit's movement does `Functions.OnIdentityInitialize()` at `CharacterControllerEntityMovement.cs:311-313`). So `ServerMeshShipMovement` must be a `LiteNetLibBehaviour` on the prefab to get that call.
- `VehicleEntityExtensions.IsDestroyWhenExit` at :11-14 is confirmed.
- **BGE:323 is the opposite of the sheet's reading.** `ManagedUpdate` returns early for a server-owned entity with 0 subscribers unless `NeedsServerUpdateWhenUnobserved` (`BaseGameEntity.cs:319-323`; also :180 and :403). So an unobserved *ship* (a `VehicleEntity`) stops running `EntityUpdate` unless it overrides that property. ServerAI pilots and the engine must override it, or live in FixedUpdate or another component.
- `GetSpawnedObjectValues` **does not exist** in our kit. Use `Assets.GetSpawnedObjects()` (an Enumerator over `Dictionary<uint, LiteNetLibIdentity>`, `LiteNetLibAssets.cs:672`) or `TryGetSpawnedObject` (:655).
- `[AllRpc]` comes from `LiteNetLibManager/Scripts/GameApi/Attributes/AllRpcAttribute.cs:6`. `SyncFieldFloat` is in `LiteNetLibSyncField.cs:521`.
- Visible range: `LiteNetLibIdentity.VisibleRange` (settable, `LiteNetLibIdentity.cs:172`). When it is 0 the interest manager's `defaultVisibleRange` (80) applies (`BaseInterestManager.cs:10, 58`). A scaled ship visible range belongs on the ship identity; the manager default is a demo default and must not change (§0.6).
- Installer convention: every rig feature is a `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` static that adds a GameObject or component when its env key is set (LoadGenerator :38, PhantomInstaller :19, Ce12Check :38, Rates :154, TickBurn :27, …).
- The package: the TestRig add-on exports `Assets/ServerMeshing/TestMap` wholesale (`Mesh/Editor/ServerMeshPackageBuilder.cs:92`), so `TestMap/Ships/` ships with it automatically.

## 8. Tests, logging, reserved words

- NUnit EditMode tests in `Tests/Editor/`, namespace `...Mesh.Tests` (Assembly-CSharp-Editor):
  - tables are `IEnumerable<TestCaseData>` + `[TestCaseSource]` + `.SetName("Member_Case_Result")`, failures first (`ServerMeshPhantomSettingsTests.cs:18-47`);
  - there are about 788 test attributes or sources in total.
  - Fakes are plain classes (`MeshFakeLog` in `MeshFakeCoordinatorParts.cs:11-20`; `MeshFakeCompactStateSource.cs:8-26`).
  - A MonoBehaviour declared in Tests can never be AddComponent'ed (step 5 rule).
- `MeshSyncGoldenFixture` (reserved in §0.15; step 1 said it "holds the stand-in's 54/56 B layouts") **does not exist** yet, so step 7 creates it in Tests.
- `IMeshLog { Info, Warning, Error }` is at `Mesh/MeshLayoutCoordinator.cs:30-35`. `MeshKitLog` (:38-43) wraps the kit's `Logging`, which NREs in EditMode, so pure parts take `IMeshLog`.
- Log rules: one line, invariant culture, no braces, never the word "Exception". Use `MeshStateSender.CleanForLog` (:278-284) or the local `Printable`. Phantom error prefix `Phantoms error:`, sender `State sender error:`. accept scripts fail on these and on "Exception".
- Stray check (`Mesh/Editor/ServerMeshPackageBuilder.cs:225-247`):
  - every class/struct/enum/interface/record/delegate declared at line start in TestMap or Tests, **nested ones included**, is reserved, by whole-word and case-sensitive match against Mesh/ text (comments count);
  - `MeshPackageStrayReferenceTests.EveryPackageOfThisProject_HasNoStrayReference` (:80-83) runs it on the tree.
  - Step 7's nested helpers must avoid words Mesh/ already uses (Plan, Path, Wire, Engine, State, Entry, Kind and the like).

## Gaps / spec corrections

1. **Cites drifted.**
   - Ghosting:434 is now :445 (`_ownedCharacters`), and 574-612 is now 586-612.
   - `LiteNetLibAssets.cs:173` lives under `GameApi/`.
   - `GameInstance_Data.cs:834-839` is right but sits under an `#if` at :833.
   - `GetSpawnedObjectValues` does not exist; use `GetSpawnedObjects()`/`TryGetSpawnedObject`.
2. **BGE:323 is misread.** It stops unobserved server-owned entities rather than keeping them simulating. The real risk is that unobserved ServerAI ships freeze unless `NeedsServerUpdateWhenUnobserved` is overridden. Whether a seated pilot keeps simulating depends on its movement component's own Update; disabling it stays correct.
3. **Discovery is not automatic.** A ship that is its own kit handler is refused by the wrap queue (`RefusalOf` :261) and counted as a refusal on each spawn. It must `Register` directly with a retry until `IsSending`, and gate its kit-facing `WriteServerState` on a sent-by-sender flag. Acceptance "once per build" is Writes = Builds × direct sources. Expect `refusals` = ship spawns, or exempt them.
4. **The sent-by-sender flag cannot be cleared by Mesh/ for a rig type.** The sender's drop, destroy and stop paths special-case `MeshKinematicCharacterBody`. Choose one:
   - (a) a small game-neutral Mesh/ hook (an interface the sender calls on drop/unregister, or a public `IsRegistered(objectId)`), which needs a §0.15 name and a delivery-note line; or
   - (b) the rig ship re-checks registration every build through a sender query that does not exist today.
   This is a shipped-API decision.
5. **`MeshKinematicTrack` is yaw-only.** `ServerMeshShipPuppetBody` needs its own quaternion track. `MeshKinematicCharacterBody.Begin` needs a `BaseCharacterEntity` with Movement, so ship puppets cannot reuse it. The manager's feed and pose methods are private, so the feeder reproduces Accept/Tick/ApplyNow itself.
6. **Pilots must not be tracked** (`TrackSyntheticEntity`). Otherwise ghosts carry `mountEntityId` and neighbours spawn ship prefabs as mounts (`Ghosting.cs:789, 1788-1830`).
7. **Phantom ship kind is wider than "add a payload class":**
   - `IServerMeshPhantomPayload` lacks rotation, velocity, angular velocity, flight state and epoch;
   - slot, feeder, binder, layout (character filler for V), bind reply (kind 2, payload values) and sink peer (character-only :172) all change;
   - `VehicleEntity.DoNotDestroyWhenDisconnect` means `Unbind` must destroy the ship;
   - flip three C# tests, `p0phantom.phantom_setting`, and its tests and registry texts.
8. **MESH_PHANTOM payload values.** §0.16 lists `ship56|ship28` for uploads but `legacy54|header56|compact28` for `MESH_SHIP_WIRE`. The sheet does not say which payloads are valid with which kind (ship payloads only with kind ship? is the default for kind ship header56?).
9. **"Meter and sink measure record sizes" needs new instrumentation.** No record-length measurement exists anywhere. Pick shipped (MeshSendMeter group, byte-identical when off) or rig (phantom transport swallow + sink peer), plus optional p0phantom groups.
10. **`MESH_RIG_SHIPS` cannot reach bots as env** (`ServerMeshAutoClient.cs:48`). It needs a bot argument added at regress.py:240 or rigrun. The 0.5 m×scale acceptance also needs the bot to know each ship's seeded path, so seed, scale and ship-to-path mapping must also reach the bot (arguments, or a log the Python side compares).
11. **Keys in the step 7 body vs the registry.**
    - Pending keys `MESH_LOAD_SHIP_PILOTS`, `MESH_SHIP_PARK_SLEEP`, `MESH_SHIP_JUMP_SECONDS` and `MESH_PREWARM` have no behaviour in the step 7 body.
    - `ServerMeshShipMirrorFeeder` and `ServerMeshShipProjectile` are reserved in §0.15 but unspecified.
    - `MESH_LOAD_SHIPS` mode `mirror` needs an owner, so does a load-spawned mirror ship need the mirror feeder?
    - Decide each key: implement it (move it to ENV_REGISTRY + ENV_KEYS) or keep it pending. The `(30, 14, 3)` test changes either way.
12. **The scale factor on R0 must be applied by the runner** (env `MESH_SENDER_R0` = R0·scale). The sender (Mesh/) cannot read a rig scale, and its tiers are Hz with near = R0/2. Gate distances are Python-side as well. Visible range scales on the ship identity only.
13. **`MeshSyncGoldenFixture` is missing** although step 1 promised it, so step 7 must create it with the golden 54/56 layouts.
14. **Prefab vs runtime object.** The step 5 eye prefab was replaced by a runtime object. The ship cannot do that: bots instantiate it from a registered HashAssetId, so the prefab must be built in a build window and registered only under a ship env key, on server and bot alike.
15. **Reserved words.** Every nested type in the new `TestMap/Ships/*` files becomes a reserved word for Mesh/ (whole word, comments included). Avoid generic nested names.
