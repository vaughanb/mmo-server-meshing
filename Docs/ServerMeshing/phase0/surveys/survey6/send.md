# Survey for Phase 0 step 6: the SEND side (state sender, kit adapter, CE-12)

Read-only survey, 2026-10-02. Repository: `C:\dev\Game Design\mmo-server-meshing` at 3659b8d plus working tree. Nothing in the repository was changed.
Abbreviations: BGNM = `Assets/OpenMMORPG/Core/Scripts/Networking/BaseGameNetworkManager.cs`; LNM = `.../LiteNetLibManager/Scripts/LiteNetLibManager.cs`; LNGM = `.../GameApi/LiteNetLibGameManager.cs`; SS = `.../GameApi/LiteNetLibGameManager.StateSyncing.cs`; LNI = `.../GameApi/LiteNetLibIdentity.cs`; LNP = `.../GameApi/LiteNetLibPlayer.cs`; LNA = `.../GameApi/LiteNetLibAssets.cs`; LU = `.../Scripts/Utils/LogicUpdater.cs`; BIEMF3D = `Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs`; NMEM = `.../EntityMovementSystems/NavMeshEntityMovement.cs`; IEMDH = `Core/Scripts/Gameplay/Interfaces/IEntityMovementDataHandler.cs`; JAOI = `Core/Scripts/Networking/AOI/JobifiedGridSpatialPartitioningAOI.cs`; MMNM = `Assets/ServerMeshing/Mesh/MeshMapNetworkManager.cs`; Load = `Assets/ServerMeshing/Mesh/MeshMapNetworkManager_Load.cs`.

---

## 0. Headline findings (read these first)

1. **CE-12 is in, in the "pass" shape, and is merged upstream.** `IEntityMovementServerStateFlags` + `EntityMovementServerStateFlagsPass` (IEMDH:20-66); both BGNM send loops Begin/Record/ConsumeAll (BGNM:704-705, 737, 799; 820-821, 868, 982); BIEMF3D and NMEM writes only read the flags and gained `ConsumeServerStateFlags` (BIEMF3D:1380-1386, NMEM:637-642, NMEM clears `_sendingDash` too). Upstream 0006/0007 merged into `develop` 2026-10-02 (`Docs/ServerMeshing/upstream/README.md`, "0006-0007: merged"). So: Mesh/ may reference `IEntityMovementServerStateFlags` directly (no reflection adapter needed), and the §0.9 "stamp-in-handler only, post-pass refused" text plus the sheet's `ServerMeshCe12Check` shapes (`stamp|perCall|postPass|absent`) are superseded by kitq_q4: the check must classify `pass` (and `stuck`), and today's kit will report `pass`, not `perCall`.
2. **The adapter's consume is now a correctness requirement.** With CE-12 the kit's write no longer clears anything (BIEMF3D:1373-1375, NMEM:629-631). A wrapped handler is never recorded by the kit (the wrapper returns false at BGNM:735-736, Record is at :737 after the check), so unless the sender Records the INNER handler after its one write and ConsumeAlls after fan-out, every teleported/jumped/dashed character goes reliable-with-flag forever. Summary item 3 (sheet :51-57) is wrong now.
3. **The sender must own its own `EntityMovementServerStateFlagsPass`.** The kit's instance is `protected readonly` on BGNM (BGNM:110-111); a component cannot reach it and must never share it (the kit's `Begin` at BGNM:705 would drop the sender's records). The class is `public sealed` with a public parameterless constructor (IEMDH:39-66), so `new EntityMovementServerStateFlagsPass()` works; `Record` type-tests and dedupes (IEMDH:54-58).
4. **With the shipped interest manager, NO pair is created inside the kit tick.** The demo/rig use `JobifiedGridSpatialPartitioningAOI` (added by BGNM:122-124 when none is on the prefab; the rig's `ServerMeshTimedAoi` subclasses it). Its in-tick call only schedules queries (JAOI:169-246); `UpdateSubscribings`/`ClearSubscribings` run in its `LateUpdate` (JAOI:248-340, calls at :323, :328, :332), i.e. between ticks, and owner changes are deferred to the same LateUpdate (`manuallyApplyOwnerChanges = true` JAOI:174, `ApplyOwnerChanges` JAOI:339). Every new pair's spawn is therefore flushed by the next tick's sync pass (LNGM:111; SS:395-403) BEFORE that tick's send loop, and the kit's first record to it is delivered. **The sheet's "a new pair gets nothing on the build of its creation tick" (sheet :744) is not kit parity; neither is q4's "skip only if this tick's AOI pass created it" relevant for this manager.** Recommendation: never skip a new pair (sending on its first build is exactly what the kit does under both interest managers; with the synchronous `DefaultInterestManager` the client simply drops the pre-spawn record, as it does for the kit, BGNM:609-614). Keep "due on the next N builds" only as the first-send boost for rate tiers.
5. **Records/s for the kit arm cannot be measured with what exists.** Step 5's `MeshSendMeter` counts sends (packets) and bytes per message type, and delivery totals across all types (MeshSendMeter.cs:111-130, ToLine :389-414); it never reads the record count inside a 119 packet. The acceptance "EntityState records/s within ±2% of the kit arm" (sheet :812) needs a new record counter for the kit arm (see §6).
6. **Use `MeshMapNetworkManager.OnServerUpdate` (Load:58-71) as the deterministic hook, not OnTick subscriptions.** `RegisterMessages` runs once per manager life (LNM:95-98 → :150-157), so a pre-kit OnTick handler subscribed there and unsubscribed at OnStopServer is lost after a server restart (q4 already noted). The override already brackets the kit tick (`PoseKinematicPuppets` :60, `BeginTick` :62, `base.OnServerUpdate` :65, `EndTick` :69 in `finally`). Pre-kit: before `base.OnServerUpdate`. Post-kit build: after the `finally` (so the work meter's tick has ended and `AddTickWork` attributes the sender's time to the send sample, MeshWorkProbe.cs:289-295), and only if base returned normally.
7. **The kinematic body has no "registered with the sender" state yet.** `MeshKinematicCharacterBody.WriteServerState` always returns true (Mesh/MeshKinematicCharacterBody.cs:308-316) and `WriteState` goes through it (:319-322). Step 6 must add the state in which the kit-facing write returns false and `WriteState` writes (q4), or the body is sent twice and the sender's differing timestamp ends its teleport stamp (TakeTeleport :326-341).

---

## 1. The kit's server send loop with CE-12

### 1.1 Entry point and timestamp
- `BGNM.OnServerUpdate` (BGNM:131-140): `base.OnServerUpdate(updater)` (LNGM's sync, ping, AOI) at :133, then
  ```csharp
  if (DefaultGridManagerComponent.Instance.IsDisabled)
      SendServerEntityMovementState(ServerTimestamp);   // :135-136
  else
      SendServerEntityMovementStateJob(ServerTimestamp); // :137-138
  ```
- `ServerTimestamp` is a property read at call time: on a server `RttCalculator.LocalTimestamp` (LNGM:66-74) = `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` (RttCalculator.cs:6). One value per kit loop, but **the sender cannot obtain the kit's exact value**: any read of its own (pre-kit or post-kit) can differ by milliseconds. Consequences:
  - BIEMF3D/NMEM ignore `writeTimestamp` in `WriteServerState` (BIEMF3D:1335-1376; NMEM:601-632), so their bytes do not depend on it.
  - The kinematic body's stamp does (it keys the teleport on the timestamp, :326-341): one writer per body per tick (§7 hazard H3).
  - The client orders non-teleport records by it (`_acceptedPositionTimestamp <= peerTimestamp`, BIEMF3D:1444) and interpolates over the delta (:1447-1448). Read the sender's timestamp once per build; it is always later than the previous tick's kit timestamp, so an object moving between kit path and sender path stays monotonic.
  - IsTeleport is applied with no timestamp check (BIEMF3D:1437-1443), so a late/deferred teleport record snaps back (q4 2e: never defer reliable records).
- Both send methods are `internal` (BGNM:691, :801). No asmdefs exist under `Assets/ServerMeshing` or `Assets/OpenMMORPG/Core` (find returned none), so Mesh/ compiles into Assembly-CSharp with the kit and can see `internal`/`internal protected` members (e.g. `Players`, LNGM:42), while EditMode tests (Assembly-CSharp-Editor) cannot (sheet §0.3).

### 1.2 `SendServerEntityMovementState` (BGNM:691-800), the path in use (grid disabled by default)
- Shared static writers `EntityMovementDataBuffers.ReliablePacketWriter/UnreliablePacketWriter/StateDataWriter` (BGNM:693-694, :733; `EntityMovementDataBuffers.cs`, `internal static readonly`, 1024/1024/64 initial). **The sender must use its own writers**; these are reset by `TransportHandler.WritePacket` (TransportHandler.cs:90-96) on every player.
- `_serverStateFlagsPass.Begin()` (BGNM:704-705, MESH-EDIT CE-12).
- `foreach (KeyValuePair<long, LiteNetLibPlayer> playerKvp in Players)` (BGNM:706); **host skip** `if (playerKvp.Key == ClientConnectionId) continue;` (BGNM:708-709). `ClientConnectionId` is -1 on a dedicated server (LNGM:50, reset at :474 in OnStartServer).
- Per player, two packets are started (BGNM:715-726):
  ```
  [packed ushort 119][packed long writeTimestamp][int32 count placeholder]
  ```
  (`WritePacket` = Reset + `PutPackedUShort` TransportHandler.cs:90-96; `PutPackedLong` = two zigzag packed ints, NetDataWriterExtension.cs:161-165.)
- Records come from `player.GetSubscribingObjectIds()` (BGNM:727; LNP:30-33, the player-side HashSet), skipping ids with no handler (BGNM:731-732). `StateDataWriter.Reset()` (:733), then
  ```csharp
  if (!dataHandler.WriteServerState(writeTimestamp, EntityMovementDataBuffers.StateDataWriter, out bool shouldSendReliably))
      continue;                                   // :735-736  (a wrapper returning false is never recorded)
  _serverStateFlagsPass.Record(dataHandler);      // :737  MESH-EDIT: CE-12
  ```
- Record layout, both writers: `[packed uint objectId][int32 len][len bytes]` (reliable BGNM:741-743; unreliable :767-769). `len` is `StateDataWriter.Length`, exact.
- **Reliable**: appended with **no size check** (BGNM:739-745); one ReliableOrdered packet per player per tick, sent at BGNM:774-781 with **no try/catch**. LiteNetLib fragments reliable packets above MTU-4 (LiteNetPeer.cs:636-647), and throws `TooBigPacketException` only beyond `MaxFragmentsCount`.
- **Unreliable split rule** (BGNM:748-766):
  ```csharp
  const byte idAndLengthSize = 8; // Use 2 ints for object ID and state data length
  if (tempLastPosition + StateDataWriter.Length + idAndLengthSize >= MAX_UNRELIABLE_PACKET_SIZE) { patch count; send; restart after header }
  ```
  A fixed 8-byte allowance (the real id is 1-5 bytes) and `>=`; the check runs before the first record too, so a first record that does not fit sends an empty count-0 packet and is then appended anyway. Too-big sends are caught and logged `Too Big Packet {len}` (BGNM:756-763, :789-796); the record is lost.
- Final sends: reliable if count > 0 (BGNM:774-781), unreliable if count > 0 (BGNM:783-797), both on channel `BaseGameEntity.MOVEMENT_DATA_CHANNEL = 2` (BaseGameEntity.cs:17), msg id `GameNetworkingConsts.EntityState = 119` (GameNetworkingConsts.cs:25).
- `_serverStateFlagsPass.ConsumeAll()` after the last player (BGNM:799). If anything throws inside the loop (only the uncaught reliable send can), ConsumeAll is skipped; the next Begin drops the records (at-least-once, IEMDH:45-52).

### 1.3 `SendServerEntityMovementStateJob` (BGNM:801-983), the grid path (off by default)
- Begin at :820-821; **no host skip** (:824-825 commented out); only players with a registered player character (:828-829, `DefaultServerUserHandlers.PlayerCharacters`).
- Allocates per player per tick (HashSet :848-853, Native containers :855-857, Dictionary :860).
- `CreateMovementData` then `Record` (:867-868) **before** `WriteEntityServerState` can still return false (:901-902): the job path can consume a flag whose record was not written. Irrelevant while the sender refuses the grid path, but it is the one place where the "Record only after a successful write" condition is not literally met.
- Different split constant `intSize = 4` (:916-917); in editor/development builds TooBig is logged and **rethrown** (:929-933, :974-978).
- ConsumeAll at :982.
- `MeshKinematicCharacterBody.Refusal(...)` already refuses the grid path (MeshKinematicCharacterBody.cs:92-95); the sheet's "Register refuses when the grid path is enabled" applies to the adapter too (check `DefaultGridManagerComponent.Instance.IsDisabled`, BGNM:135).

### 1.4 The CE-12 API (IEMDH)
- `IEntityMovementDataHandler` (IEMDH:8-18): `ObjectId`, `ConnectionId`, `CreateMovementData(out List<EntityMovementForceApplier>)`, `ReadClientStateAtServer`, `ReadServerStateAtClient`, `WriteClientState(long, NetDataWriter, out bool)`, `WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)`.
- `IEntityMovementServerStateFlags` (IEMDH:24-31): `void ConsumeServerStateFlags()` - "Called by the server send loop once per tick, after its last player, on each handler it wrote; never from a write."
- `EntityMovementServerStateFlagsPass` (IEMDH:39-66): `RecordedCount` (:43); `Begin()` clears (:49-52); `Record(IEntityMovementDataHandler)` adds only if the handler implements the flags interface, HashSet dedupe (:54-58); `ConsumeAll()` consumes each once then clears (:60-65). Main-thread, allocation-free after warm-up.
- Implementers: `BuiltInEntityMovementFunctions3D` (BIEMF3D:10) and `NavMeshEntityMovement` (NMEM:13). Not implemented by RigidBody2D or the Simple movers (they keep the first-receiver bug; out of the adapter's allow-list).
- BIEMF3D: `CreateMovementData` reads flags (:1286-1333); `WriteServerState` (:1335-1376) returns false when `!_isStarted` (:1338-1339), ORs jump/dash/teleport into the `MovementState` property and sets `shouldSendReliably` for each (:1341-1370), writes via `Entity.ServerWriteSyncTransform3D` (:1372), clears nothing; `ConsumeServerStateFlags` clears `_sendingJump`, `_sendingDash`, `_isTeleporting`, `_stillMoveAfterTeleport` (:1380-1386), not the teleport confirm.
- NMEM: `CreateMovementData` (:562-599), `WriteServerState` (:601-632, `!_isStarted` → false at :604-605), `ConsumeServerStateFlags` clears `_sendingDash`, `_isTeleporting`, `_stillMoveAfterTeleport` (:637-642). `_sendingDash` is still set every frame while dashing (NMEM:348-350); client-side clears stay at :523, :552.
- `ServerWriteSyncTransform3D` (EntityMovementFunctions.cs:136-145) writes `[packed uint MovementState][byte Extra][Vector3][packed int angle][List forceAppliers]`, but **writes nothing when `!Entity.IsServer` (:138-139) while the caller still returns true**: a zero-length "successful" record is possible in principle (q2: treat true-with-zero-bytes as nothing to send).
- Side effect of an extra write: `WriteServerState` assigns the `MovementState` property (BIEMF3D:1344-1369). Two writes in one tick with the same flags give identical bytes, but a write AFTER the kit's ConsumeAll (e.g. measure mode, §6.1) changes the server-local `MovementState` earlier than the next kit write would (`MovementState = IsTeleport` → cleared). No wire bytes change; server-local readers (server animation, despawn) can see it one frame early.

### 1.5 Client side (for the parser copy and length rules)
- `HandleServerEntityStateAtClient` (BGNM:591-623): ts (:594), int32 count (:595); per record id (:598), len (:599); **CE-13 guard** `if (dataLength < 0 || dataLength > reader.AvailableBytes) { warn "invalid state length"; return; }` (:601-608); unknown entity → warn, skip by len (:609-615); known → `ReadServerEntityState` (:617-621).
- `public static bool ReadServerEntityState(IEntityMovementDataHandler, long, NetDataReader, int dataLength)` (BGNM:625-648): always re-seeks to start + len in `finally`. Being public static, EditMode tests can call the kit's own per-record function; only the outer loop (protected) needs copying into `MeshEntityStatePacketParser`, and it must copy the guard (q2).
- `regress.py` already fails a run on `invalid state length` / `error occurs while reading` in bot logs (DESIGN.md CE-13 row) - the natural detector for sender framing bugs.

### 1.6 Packet-size arithmetic (confirms the sheet's `1004 − idBytes`)
- `MAX_UNRELIABLE_PACKET_SIZE = 1020` (SS:12, comment "1024 - 1 (unreliable header)").
- LiteNetLib `InitialMtu = PossibleMtu[0] = 1024` (NetConstants.cs:89-103); headers: unreliable 1 B (`HeaderSize`, :57), channeled 4 B (`ChanneledHeaderSize`, :62). `ExtraPacketSizeForLayer` is 0 without a packet layer (LiteNetManager.cs:286). TooBig: `length + headerSize > mtu` and not reliable → throw (LiteNetPeer.cs:636-640). So 1020 is the largest ReliableOrdered payload that does not fragment (1020+4 = 1024) and unreliable may go to 1023.
- Header bytes: 119 packs to 1 B; the ms timestamp packs as two zigzag ints: high word ≈ 417 → zigzag 834 → 2 B (stays 2 B until ~year 2125), low word 1-5 B (usually 5); count 4 B. Header ≤ 12 B. One record fits iff `12 + idBytes + 4 + len ≤ 1020` ⇔ `len ≤ 1004 − idBytes`. CONFIRMED.
- Packed-uint boundaries 240/241, 2287/2288, 67823/67824, 16777215/16777216 match `PutPackedULong` (NetDataWriterExtension.cs:179-~230). CONFIRMED.
- Phantoms mirror LiteNetLib's rule (`ServerMeshPhantomTransport.TooBigLimit`: unreliable `InitialMtu − HeaderSize` = 1023; reliable ordered/unordered `int.MaxValue`; TestMap/Phantoms/ServerMeshPhantomTransport.cs:111-~125, thrown at :254-255).

### 1.7 Send path and TooBigPacketException
- `LNM.ServerSendMessage(conn, channel, method, writer)` (LNM:362-365) → `LiteNetLibServer.SendMessage` (LiteNetLibServer.cs:114-117) → `Transport.ServerSend` (return value discarded, so a refused send is invisible to the caller). With step 5's chain the transport is `MeshMeteredTransport` → `ServerMeshPhantomTransport` → `LiteNetLibTransport` (ServerMeshPhantomInstaller.Splice, TestMap/Phantoms/ServerMeshPhantomInstaller.cs:59-74). The sender MUST send through `ServerSendMessage` so the meter and phantoms see it.
- `LiteNetLibTransport.ServerSend` returns false unless the peer exists and is Connected (Transports/LiteNetLib/LiteNetLibTransport.cs:116-124); `LiteNetPeer.Send` copies the data synchronously, so writers may be reused right after.
- `TooBigPacketException : InvalidPacketException` (Plugins/LiteNetLib/NetDebug.cs:13-15); thrown by LiteNetPeer.cs:640 (unreliable over MTU) and :647 (reliable over `MaxFragmentsCount`). The metered transport records it and rethrows the same instance (Mesh/MeshMeteredTransport.cs:90-94). Step 5 rule: never log the type name ("Exception" fails a run).

---

## 2. Tick ordering and hook points

### 2.1 Frame and tick order
- `LNM.Update` (LNM:159-167): `_logicUpdater.Update()` first (all ticks of the frame), then `Server.Update()` (message processing), then `Client.Update()`. BGNM.Update calls `base.Update()` first (BGNM:163-166). Gameplay code that raises flags (Teleport, dash, jump, uploads) runs outside the tick.
- `LogicUpdater` (LU): `MaxTicksPerUpdate = 5` (:7); `InvokeOnTick` = plain multicast `OnTick.Invoke(this)`, no try/catch (:79-83); `Update` (:88-110) fires while the accumulator holds a period, `LocalTick++` AFTER each invoke (:104-105), and on the 6th due tick zeroes the backlog and returns (:99-103). `MeshTickScheduleCopy` (Tests/Editor/MeshTickScheduleCopy.cs) already copies this.
  - An exception escaping any OnTick subscriber stops the rest of that tick's subscribers, the remaining catch-up ticks, AND `Server.Update` for the frame (LNM:161-164). The sender must catch at top level.
- `LNM.StartServer` (LNM:207-228): `_logicUpdater.OnTick -= OnServerUpdate; += OnServerUpdate;` (:221-222), start (:223-224), `IsServer = true` (:225), `OnStartServer()` (:226). Anything subscribed in OnStartServer runs after the kit's whole OnServerUpdate (including Mesh's override and its EndTick). `StopServer` unsubscribes the kit (:290) and calls `OnStopServer` (:296). `StartClient` appends `OnClientUpdate` (:252-253) - on a host it lands after anything subscribed at OnStartServer.
- `RegisterMessages` runs once: `Start` → `InitTransportAndHandlers` (LNM:95-98, :150-157): `_logicUpdater` is created at :155 right before `RegisterMessages()` at :156, so the updater exists during component `RegisterMessages` (BGNM:201-204), but it is never called again on restart.
- `LNGM.OnServerUpdate` (LNGM:109-122): `ProceedServerGameStateSync(updater.LocalTick)` (:111; flushes queued spawns/destroys to ready players, SS:366-412, flush loop :395-403), ping (:113-118), `InterestManager.UpdateInterestManagement(updater.DeltaTimeF)` (:121). Then BGNM's movement loop (BGNM:135-138).
- `JobifiedGridSpatialPartitioningAOI`: `updateInterval = 1.0f` (JAOI:18); in-tick call schedules only (JAOI:169-246); completion and every subscription change in `LateUpdate` (JAOI:248-340); owner changes applied there too (:339). (DefaultInterestManager subscribes synchronously inside the tick, DefaultInterestManager.cs:25-51; not used by the demo or rig.)

### 2.2 The Mesh override (candidate hook)
`Load:58-71` (inside `#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE`, Load:43-334):
```csharp
protected override void OnServerUpdate(LogicUpdater updater)
{
    PoseKinematicPuppets(); // right before the kit sends movement, so watched kinematic puppets go out current
    MeshWorkMeter meter = MeshWorkProbe.Meter;
    meter.BeginTick(System.Diagnostics.Stopwatch.GetTimestamp());
    try { base.OnServerUpdate(updater); }
    finally { meter.EndTick(System.Diagnostics.Stopwatch.GetTimestamp()); }
}
```
- Pre-kit hook: before `base.OnServerUpdate` (after `PoseKinematicPuppets`, so the build reads the posed transforms). Work here: lazy-wrap queue, rewrap checks, frame-index check ("one build per frame").
- Post-kit hook: after the `finally`, guarded by "base returned" (on a throw, skip the build; flags stay pending, which is correct). Its time goes through `MeshWorkProbe.Meter.AddTickWork(ms)` (MeshWorkProbe.cs:285-295: added to that tick's send sample; the sample is recorded at the next BeginTick). `EndTick` only starts a send sample when the AOI call was marked (rig `MESH_AOI_TIMING`, TestMap/Phase0/ServerMeshTimedAoi.cs:43-49; MeshWorkProbe.cs:259-282), so "residual ms/tick" (the kit loop iterating wrappers that return false) is the Work ticks `move` part and needs `MESH_AOI_TIMING=1`.
- The override exists only on `MeshMapNetworkManager`; the sender component must be reachable from it (field set when it is added in Awake).
- If the sheet's OnTick design is kept instead: the pre-kit handler subscribed at RegisterMessages runs before the kit only because the kit re-adds itself at every StartServer; it must not be unsubscribed at OnStopServer (or must be re-subscribed at OnStartServer, before `base.OnStartServer` would not help because LNM re-adds the kit before calling OnStartServer - re-subscribing then puts it AFTER the kit). The override avoids all of this.

### 2.3 Where a component's OnStartServer runs
- `BGNM.OnStartServer` (BGNM:250-263): `SetServerHandlersRef`, dev-ext, **components' `OnStartServer` (:254-257)**, day/night, `base.OnStartServer()` (:259), grid setup (:262).
- `MMNM.OnStartServer` (MMNM:654-703) calls `base.OnStartServer()` FIRST (:656), so the sender component's OnStartServer runs before Mesh's own start code (`GhostingOnStartServer` :699, which sets `_slots = MeshMovementHandlerSlots.For(this)` at Puppets:68 and parses `MESH_PUPPET_MOTION`). The sender must get the slots itself via `MeshMovementHandlerSlots.For(manager)` (static, per manager, Mesh/Sync/MeshMovementHandlerSlots.cs:40-45) and must not depend on Mesh start state at that point.
- `BGNM.OnStopServer` (BGNM:265-274): components' `OnStopServer` (:268-271) BEFORE `Clean()` (:272, which clears `_entityMovementDataHandlers` at :220 and calls components' `Clean` at :238-241). `MMNM.OnStopServer` (MMNM:705-714) runs Ghosting/puppet stop before `base.OnStopServer` (:713), so puppets' bodies End before the sender stops.

---

## 3. `BaseGameNetworkManagerComponent` and adding the sender

- Collection: `ManagerComponents = GetComponents<BaseGameNetworkManagerComponent>();` at **BGNM:125** in `BGNM.Awake` (BGNM:113-129), property at BGNM:42 (private set). Order of Awake: `MMNM.Awake` (MMNM:96-101) → `MapNetworkManager.Awake` (MMO/.../Map/MapNetworkManager.cs:117-125) → `BGNM.Awake` → `LNGM.Awake` (LNGM:97-107).
- Hooks available (Core/Scripts/Networking/BaseGameNetworkManagerComponent.cs:10-165): `RegisterMessages`, `Clean`, `OnStartServer`, `OnStopServer`, `OnStartClient`, `OnStopClient`, `InitPrefabs`, scene-loaded, map-info extra, ready states, peer connect/disconnect, client sends/responses. **There is no tick hook**, so the build must come from OnTick or the manager's override.
- Call sites: RegisterMessages BGNM:201-204 (once, at Start); OnStartServer BGNM:254-257; OnStopServer BGNM:268-271; Clean BGNM:238-241; OnPeerDisconnected (for per-connection state cleanup) is also forwarded.
- Precedent for adding before `base.Awake()`: `InstallSendMeter()` at MMNM:99, defined MMNM:107-130 (reads env, `gameObject.AddComponent<...>()`, configures fields right after, logs one setting line). Pattern for the sender: in MMNM.Awake, after `InstallSendMeter()` and before `base.Awake()` (MMNM:100), parse `MESH_SENDER`; when `measure` or `on`, `gameObject.AddComponent<MeshStateSender>()`, keep a reference for the override. Keep the component's `Awake` empty (it runs inside AddComponent before fields are set; cf. MeshMeteredTransportFactory.cs:32-35). Only one existing kit subclass of the component (Core/Scripts/UI/Networking/UIGameNetworkManagerConnection.cs).
- `IMeshSendPolicy` discovery per contract: "the first found on the network manager's GameObject wins" (MeshSyncContracts.cs:129) → `GetComponent<IMeshSendPolicy>()` at OnStartServer, else the env-built `MeshDistanceSendPolicy` (a plain class per the sheet's constructor).
- Spawn discovery for the lazy-wrap queue: `Assets.onObjectSpawn` (LNA:55) fires after `identity.Initial` (which runs `OnIdentityInitialize`, the kit's handler registration, LNI:606-609) and after OnStartServer (LNA:417-430, :477). Ghosting already listens (Ghosting:227-228, handler :585-600, which skips `_spawningPuppet`). `onObjectDestroy` (LNA:56, :522) for unregister.

---

## 4. Subscribers and pairs

- Two mirrored relations: identity side `Subscribers` (HashSet<long>, LNI:109) and player side `Subscribings` (HashSet<uint>, LNP:13). `LNP.Subscribe` adds to the player set and, if the identity is spawned, `identity.AddSubscriber(conn)` + queues a spawn state (LNP:40-48); `Unsubscribe` mirrors with a destroy state (LNP:50-58). The kit loop walks the player side (BGNM:727); `identity.GetSubscribers()` (LNI:726-729, struct enumerator, no allocation) walks the identity side. They agree except for an id subscribed before it was spawned (player set only), so the identity side is the safer set (q4 condition).
- `AddSubscriber`/`RemoveSubscriber` raise `onSubscriberAdded`/`onSubscriberRemoved` (LNI:691-709; fields LNI:72-73), but `NetworkDestroy` clears `Subscribers` without events (LNI:986-1001) and the delegates are nulled only in `OnDestroy` (LNI:1045-1046), not on pool push-back. Per-identity delegate hooks are therefore fragile; prefer diffing at build time.
- `CountSubscribers` (LNI:716-719), `HasSubscriberOrIsOwning` (LNI:721-724).
- **Pairs at spawn** (between ticks): owner subscribes its own object (`Player.Subscribe(ObjectId)` LNI:597-601, `IsServer && ConnectionId >= 0`), then `InterestManager.NotifyNewObject(this)` (LNI:603; BaseInterestManager.cs:22-54: the new owned object subscribes every spawned object, and every ready player's objects subscribe the new object, through `ShouldSubscribe`, owner always true at :65-66), then `OnIdentityInitialize` (LNI:606-609, handler registration).
- **AOI pairs**: `UpdateSubscribings` (LNI:769-796, always keeps the own object :778) / `ClearSubscribings` (LNI:747-767), called from JAOI's LateUpdate (JAOI:323/328/332) → between ticks with the Jobified AOI.
- **Owner change**: `SetOwnerClient` (LNI:948-954) → `LNA.SetObjectOwner` (LNA:546-556) is deferred while `manuallyApplyOwnerChanges` (set by JAOI:174) and applied in JAOI's LateUpdate (LNA:558-565 from JAOI:339). `SetObjectOwnerImmediately` (LNA:567-607) sends set-owner messages and moves `SpawnedObjects`, but **never subscribes the new owner** (confirmed). The owner pair then appears at the next AOI pass (≤ 1 s) unless code calls `NotifyNewObject`, as the arrival path does (Ghosting:1048-1053, also the mount :1056-1060). So `MeshObserverContext.IsOwner` must be computed live each build (`identity.ConnectionId == conn`), and a flip should re-classify the pair.
- **Spawn-before-state**: a spawn queued at time t is flushed by the next tick's `ProceedServerGameStateSync` (LNGM:111 → SS:395-403, ready players only), which runs before that tick's send loop. The spawn goes ReliableOrdered on another channel; the 119 record is usually unreliable on channel 2, so cross-channel ordering is not guaranteed; a record that beats its spawn is dropped with "entity movement not found" (BGNM:609-614) - same for kit and sender. With the Jobified AOI every pair (spawn, NotifyNewObject, AOI, owner change) is created between ticks, so **the first build after a pair appears is deliverable** (finding 4).
- Owner pairs are never skipped by the kit; NotSecure owners receive their own record every tick (the kit sends it; the owner's client mostly ignores the position). For records/s parity the sender must include owner pairs at 20 Hz.
- Monsters are teleported before NetworkSpawn (q4 challenge, MonsterSpawnArea.cs:136-138), so every monster spawn carries a pending IsTeleport to the players subscribed at spawn; with no in-tick skip they get it on the first build, like the kit.

---

## 5. The step 1 contract: `Mesh/Sync/MeshSyncContracts.cs` (GUID 86363639be9247f43a2b0919b12dd03f)

Namespace `MultiplayerARPG.MMO.Mesh.Sync`; usings `LiteNetLib.Utils`, `LiteNetLibManager` only (pinned by `MeshSyncContractTests.ContractFile_UsesOnlyTheNetworkingNamespaces`, Tests/Editor/MeshSyncContractTests.cs:15). The engine ports (`IMeshStateTransport`, `IMeshStatePositions`) are not in it (CONFIRMED moved out).

| Member | Line | Contract text (abridged) | Step 6 obligation |
|---|---|---|---|
| `IMeshStateSource` | 11-34 | an entity whose state the sender sends; the game owns every byte | fan-out sends an unchanged copy as a 119 record |
| `.ObjectId` | 14 | record key | packed uint id |
| `.Identity` | 17 | subscribers' identity; sender never changes it | receivers = `Identity.GetSubscribers()` at build time |
| `.DiscreteVersion` | 23 | changes whenever every observer must see the next sample | urgent to all on this build and the next 2; re-classify |
| `.IsAtRest` | 26 | no change for an extrapolating observer | optional rate input (Phase 0 policy ignores) |
| `.WriteState(long sampleTimestamp, NetDataWriter, out bool sendReliably)` | 28-33 | at most once per build; append only; changes no state; false = nothing; reliable to every subscriber | append-only check (length before/after; shrink = drop source, log id); false truncates; true with 0 bytes = nothing (q2) |
| `IMeshCompactStateSource : IMeshStateSource` | 37-41 | `WriteCompactState(long, NetDataWriter)`; never reliable | lazy, at most once per build |
| `IMeshStateRegistry` | 44-60 | `bool Register(IMeshStateSource)` ("false keeps it on the kit path (no sender, grid path enabled, duplicate id)"), `void Unregister(IMeshStateSource)`, `MarkRelevanceChanged(uint, long)`, `MarkSourceChanged(uint)`, `MarkObserverChanged(long)` | `MeshStateSender` implements all five; note the public Register has no `out string error` (the sheet's `MeshStateFanout.Register(source, out string error)` is the internal engine call) |
| `MeshStateVariant` | 63-70 | `Full = 0`, `Compact = 1` | |
| `MeshObserverContext(long, float, bool)` | 73-94 | `ObserverConnectionId`, `Distance` (anchor to source, 3D or X/Z; NaN unknown), `IsOwner` | build per pair; anchor per §8 |
| `MeshSendRate` | 97-127 | `Withhold` (nothing at all, :99-100), 2-arg ctor (Full, :103), 3-arg ctor (:106), `Hz` ∈ {0,1,2,5,10,20} where 0 still receives urgent and first-send (:116-117), `Priority` (no budget in Phase 0, :119-120), `Variant` (:122-123), `IsWithheld` (:125-126) | divisor = buildHz / Hz; **owner pairs: treat Withhold as Hz 0 and log once (q4)** - conflicts with the Withhold comment; a doc-only note at the next contract revision |
| `IMeshSendPolicy.Classify(IMeshStateSource, in MeshObserverContext)` | 130-134 | on a new pair, at the classify rate, after a Mark call or a DiscreteVersion change; must not allocate | classify cadence (`MESH_SENDER_CLASSIFY_BUILDS`, default 4) |

Related existing Mesh/ types step 6 builds on:
- `MeshMovementHandlerSlots` (Mesh/Sync/MeshMovementHandlerSlots.cs): `For(manager)` :40-45; `TryInstall(id, layer, replaceable, out error)` :53-102 (CAS via `TryUpdate` :92; over a replaceable layer calls its `OnReplaced` :99-100; a replaceable layer must implement `IMeshReplaceableHandlerLayer` :57-58); `TryRemove` :109-132 (writes back the ORIGINAL kit handler, raises `LayerRemoved(id)` :130); `TryGetKitHandler` :138-147; `HasLayer` :150-155; `Forget` :161-167; `Reset` :170-173. Rule §0.13: only this class may replace dictionary entries.
- `IMeshReplaceableHandlerLayer.OnReplaced()` (Mesh/Sync/IMeshReplaceableHandlerLayer.cs:9-16).
- The kit re-registers handlers behind the slots' back: `OnIdentityInitialize` does TryRemove+TryAdd (BIEMF3D:222-226; NMEM:139-143) and `OnNetworkDestroy` removes (BIEMF3D:228-231; NMEM:145-148). Per-build "is my wrapper still in the slot" (`HasLayer`) is needed; on a lost wrapper the kit wrote (and with CE-12 recorded and consumed) that handler this tick, so the sender must skip it this build and re-wrap pre-kit next tick.
- `MeshKinematicCharacterBody : IEntityMovementDataHandler, IMeshStateSource` (class :21): `ConnectionId => -1` (:69), `Identity` (:72), `DiscreteVersion` (:75), `IsAtRest` (:78), `Refusal(...)` (:95), `Begin` installs NOT replaceable (:130-174, TryInstall at :153), `End` (:181-204), `WriteServerState` (:308-316), `WriteState` (:319-322), `TakeTeleport` stamp (:326-341). Implements no flags interface (q4: keep it that way).

---

## 6. What step 6 must emit, and how to verify it with step 5's tools

### 6.1 `MESH_SENDER=measure` changes no bytes
Required behaviour (sheet + q4): no layer installed (no wrapper), no `ServerSendMessage`, never Begin/Record/ConsumeAll, encode into private writers and count. Hazards:
- **Kit handlers**: measuring after the kit loop sees flags the kit has already consumed (BGNM:799), so measure-mode reliable/urgent counts undercount one-shots; measuring before the kit loop is byte-safe for BIEMF3D/NMEM (writes only read flags) but **not** for the kinematic body.
- **Kinematic body**: a pre-kit `WriteState` with the sender's timestamp would take the armed teleport (TakeTeleport :328-333) and the kit's write with a different ms would then end it (:337-340) → the kit would never send IsTeleport → bytes change. After the kit loop it is harmless (the kit already stamped and sent). So measure mode must build post-kit (or skip bodies pre-kit).
- `MovementState` property side effect of an extra write (§1.4) - not wire bytes.
- Verification: wire bytes cannot be compared across two runs (positions and time differ). Practical proof = construction + unit tests ("measure never sends, never wraps, never consumes") + rig: sender's own `packets sent` = 0, Send window `t119` calls/s and bytes/record (MeshSendMeter ToLine :389-396) and `ro`/`u` delivery groups (:408) match the off arm within noise, and the CE-12 check unaffected.

### 6.2 `on` at 20/20/20: EntityState records/s within ±2% of the kit arm; puppet error within 0.1 m
- **Missing: a kit-arm record counter.** `MeshSendMeter` counts calls/bytes by packed type (Record :111-130) and delivery/channel totals over all types (`AddRouting`; ToLine :408-413); `MeshMeteredTransport.ServerSend` reads only the message type (Mesh/MeshMeteredTransport.cs:81-83). Options:
  1. extend the metered transport/meter to read the int32 count of a 119 packet (`[packed ushort][packed long][int32]`, a few bytes after the type it already parses with `TryReadMessageType`, MeshSendMeter.cs:67-100) and add `records` (reliable/unreliable) to the Send window line (log-only; wire format frozen rule §0.10 is about MeshLoadReport only);
  2. count in the phantom transport (TestMap, swallowed sends only, ServerMeshPhantomTransport.cs:247-265);
  3. count at the bots (client side).
  Option 1 is cheap, covers real and phantom peers, and gives byte-identity evidence for measure mode too.
- Expected differences that are not errors: the sender caps reliable packets at 1,020 B (the kit has one uncapped reliable packet per player per tick, BGNM:739-745, :774-781) and splits unreliable packets at exactly `len ≤ 1004 − idBytes` (the kit uses `pos + len + 8 >= 1020`, BGNM:750-751, and sometimes emits empty count-0 packets) → **packet counts and header bytes differ even when records are identical**. Gate on records/s and record bytes, not on packets.
- Catch-up ticks: the kit sends on every tick, the sender builds at most once per frame. At 60 fps / 20 Hz a frame never holds two ticks normally; at 30 fps ticks fall on 2 of 3 frames (never two in one frame). Stalls (≥ 2 ticks/frame) make the kit arm send duplicate same-ms states that the sender skips → a few tenths of a percent on a canary.
- Owner pairs and the host skip must match (§4, BGNM:708-709). Players without a ready flag: the kit loop walks all `Players` (BGNM:706); the sender walks subscribers - identical for ready players.
- "Puppet error within 0.1 m": no shipped bot-side error metric for remote characters was found in my area (step 5's `posErr` is server position vs feeder, Phantom window). The step 6 design must name the metric (e.g. the kinematic puppet steer error in the Mesh stats line, or a bot-side measure).

### 6.3 The `Sender window` line
- Nothing exists yet: no `MeshStateSenderStats`, no `Sender window` string anywhere, and `MESH_SENDER*`/`MESH_CE12_CHECK` are not in `regress.ENV_KEYS` (Tools/ServerMeshing/regress.py:69-...; `grep -c MESH_SENDER regress.py` = 0). They sit in `p0tools.PENDING_KEYS` as "stage 0b, step 6" (Tools/ServerMeshing/p0tools.py:66-74; test_p0tools.py:289-304), so `rigrun.check_env` refuses any plan that sets them until step 6 moves them into `ENV_KEYS` and removes them from `PENDING_KEYS`, and adds the p0tools parser the sheet names.
- Formatting precedent: `MeshTickWindow.ToLine` (Mesh/MeshTickWindow.cs:128-131, `Clean` helpers :134-136) and `MeshSendWindow.ToLine` (MeshSendMeter.cs:370-416): invariant culture, one line, no braces, never the word "Exception", ASCII units (`us`).
- Histograms: `MeshWorkHistogram(float binMs = 0.25f, int binCount = 400)` (Mesh/MeshWorkHistogram.cs:26) → `(0.02f, 1000)` as the sheet says.
- Logged from `LoadTick` next to Work/Send lines (Load:137-171; Send window at :158/:167-168) so windows align.

---

## 7. Hazards and gaps

| # | Hazard | Evidence | What step 6 must do |
|---|---|---|---|
| H1 | Adapter forgets to consume → flags (and reliable) forever on wrapped kit characters | BIEMF3D:1373-1375 (no clear); BGNM:735-737 (wrapper never recorded) | Own pass: Begin at build start, Record the INNER handler after its one successful append-only-checked write, ConsumeAll only after a completed build with no failed reliable send; keep the 20-consecutive-reliable warning as the alarm |
| H2 | Sharing the kit's pass | BGNM:110-111 (protected), :705 (Begin clears) | `new EntityMovementServerStateFlagsPass()` per sender |
| H3 | Kinematic body has two writers once registered | MeshKinematicCharacterBody.cs:308-322 | Add the registered state (kit-facing false, `WriteState` writes); one timestamp per build |
| H4 | Wrapper must never implement `IEntityMovementServerStateFlags` | IEMDH:54-58 (Record type-tests) | otherwise the kit would record/consume it; `CreateMovementData` delegation is harmless (read-only) |
| H5 | New-pair skip rule is not parity | JAOI:248-340; LNGM:111; BGNM:609-614 | never skip new pairs; first-send boost only |
| H6 | Owner pair Withheld → owner never confirms teleport, input frozen | MeshSyncContracts.cs:99-100; BIEMF3D WaitClientTeleportConfirm (q4: :1721-1727) | treat Withhold on an owner pair as Hz 0, log once |
| H7 | Too-big reliable record skipped and consumed | sheet :726-731, :778; BGNM:739-745 (kit uncapped) | own uncapped ReliableOrdered packet for a reliable record above `1004 − idBytes`; skip only unreliable too-big ones |
| H8 | Deferred reliable record / late teleport snaps back | BIEMF3D:1437-1443 | never defer reliable; FlushDeferred(N) before Build(N+1) |
| H9 | Exception escaping the build kills the frame's remaining ticks and `Server.Update` | LU:79-83, :104; LNM:161-164 | top-level catch in the hook; per-source catch around WriteState (log id + type) |
| H10 | Pre-kit OnTick subscription lost on restart | LNM:95-98, :150-157, :221-222 | drive both hooks from Load:58-71 |
| H11 | Timestamp mismatch with the kit | LNGM:66-74; RttCalculator.cs:6 | read once per build; never compare stamps across kit and sender |
| H12 | Kit re-registers handlers behind the slots | BIEMF3D:222-226; NMEM:139-143 | per-build `HasLayer`; skip + rewrap next pre-kit |
| H13 | Body replaces the adapter: kit flags pending in the kit handler go out stale at End | MeshMovementHandlerSlots.cs:53-101, :109-132 | optional: on End/LayerRemoved, consume the restored kit handler's flags when it implements the interface (q4 hardening) |
| H14 | NMEM `_sendingDash` re-set every frame while dashing | NMEM:348-350 | the warning's justification changes (CE-12 clears it at NMEM:639); it now catches a missing consume |
| H15 | Zero-length "successful" writes | EntityMovementFunctions.cs:138-139 | treat true with 0 bytes as nothing (or an error), never emit a 0-length record |
| H16 | Measure mode and the body stamp / consumed flags | §6.1 | build post-kit; report measure-mode reliable counts as an underestimate |
| H17 | Allocation per tick | rule §0.2; ConcurrentDictionary enumeration allocates | never enumerate `EntityMovementDataHandlers` per tick for discovery; use `Assets.onObjectSpawn` + a queue; per-connection packet state from a pool keyed once per connection |
| H18 | Not-yet-spawned id in the player set only | LNP:43-46 | receivers from `Identity.GetSubscribers()`; skip `ClientConnectionId`; optionally skip connections not in `Players` (LNGM:142-145) for parity |
| H19 | Job/grid path records before a possibly failed write | BGNM:867-868 vs :901-902 | refuse registration while the grid is enabled (BGNM:135) |
| H20 | Kit-arm records/s has no counter | MeshMeteredTransport.cs:81-83; MeshSendMeter.cs:111-130 | add a 119 record counter (option 1 in §6.2) |
| H21 | Packet-count/byte differences between arms are structural | BGNM:750-751, :739-745 | gate on records and record bytes |

Things step 6 needs that do not exist yet:
- All of `Mesh/Sync`: `MeshStatePacketWriter`, `MeshStateFanout` (+ `MeshStateFanoutSettings`, `MeshStateFanoutCounters`, ports `IMeshStateTransport`, `IMeshStatePositions`, and a subscriber port if the engine stays pure, since receivers must come from `GetSubscribers()` rather than the sheet's `SetSubscribers` list), `MeshStateSender`, `MeshKitMovementStateSource`, `MeshDistanceSendPolicy`, `MeshStateSenderStats` - none present (`Mesh/Sync` holds only `IMeshReplaceableHandlerLayer.cs`, `MeshMovementHandlerSlots.cs`, `MeshSyncContracts.cs`).
- `TestMap/Phase0/ServerMeshCe12Check.cs` (shapes now `stamp|perCall|pass|absent|stuck`; today's kit should read `pass`).
- Tests: `MeshEntityStatePacketParser` (copy BGNM:591-623 with the guard; call `BaseGameNetworkManager.ReadServerEntityState` per record), `MeshFakeStateSource`, `MeshFakeCompactStateSource`, `MeshFakeStateTransport`, `MeshFakeStatePositions`. A pass-shaped kit fake already exists: `MeshFakeServerStateHandler : IEntityMovementDataHandler, IEntityMovementServerStateFlags` (Tests/Editor/MeshFakeServerStateHandler.cs:11-58, counts consumes; its `WriteServerState` writes 0 bytes and returns true - fix or extend before packet tests). `MeshFakeKitHandler` writes 1 byte and reads 0 (Tests/Editor/MeshFakeKitHandler.cs:27-43). `MeshTickScheduleCopy` exists. The §0.15 name `MeshFakeStampedKitHandler` is obsolete.
- Kinematic body "registered with sender" state (H3).
- MMNM.Awake install of the component; override hooks in Load:58-71.
- `regress.ENV_KEYS` entries, `p0tools.PENDING_KEYS` removal, a `Sender window` parser, a kit-arm record counter.

---

## 8. Observer anchor (phantoms included)
- `GameInstance.ServerUserHandlers.TryGetPlayerCharacter(conn, out ...)` (extension, Core/Scripts/Networking/Extensions/ServerUserHandlersExtensions.cs:7) for registered players; otherwise the first owned object: `TryGetPlayer` (LNGM:142-145) → `player.GetSpawnedObjects()` (LNP:113-116). Eye phantoms are hidden owned identities with no player character and no movement handler (TestMap/Phantoms/ServerMeshPhantomEye.cs:29-38; spawned with an owner at ServerMeshPhantomClients_Spawn.cs:78-79), so they need the fallback. `Players` is `internal protected` (LNGM:42), reachable from Mesh/.

---

## 9. Sheet claims in step 6 (and the step-6-relevant parts of §0.3/§0.9/summary 3/§3), checked

| Sheet claim (line) | Status | Current location / note |
|---|---|---|
| Add the component in `MeshMapNetworkManager.cs:95-99` before `base.Awake()` (:722) | STALE | Awake is MMNM:96-101; `InstallSendMeter()` :99, `base.Awake()` :100 |
| BGNM:123 collects it (:722) | STALE | `ManagerComponents = GetComponents<...>()` is BGNM:125 (:123 is the interest-manager null check) |
| Kit edit CE-12 at BIEMF3D:1337-1380 and :1290-1332, NMEM:562-632, "in the §0.9 shape only" (:723) | WRONG/superseded | Applied in the pass shape: BIEMF3D CreateMovementData :1286-1333, WriteServerState :1335-1376, Consume :1380-1386; NMEM :562-599, :601-632, :637-642; IEMDH:20-66; BGNM:110-111, :704-705, :737, :799, :820-821, :868, :982 |
| `MAX_UNRELIABLE_PACKET_SIZE` is SS:12 = 1,020, so reliable packets never fragment (:727) | CONFIRMED | SS:12; InitialMtu 1024 − channeled 4 = 1020 (NetConstants.cs:57,62,89-103; LiteNetPeer.cs:636) |
| TryAppend false above `1004 − idBytes` (:729) | CONFIRMED | header ≤ 12 B (§1.6) |
| Layout BGNM:683-694, 708-710, 734-736 (:732) | STALE | header BGNM:715-726; records :741-743 / :767-769; count patch + send :774-797 |
| Host connection skipped BGNM:676-677 (:749) | STALE | BGNM:708-709 |
| "a new pair gets nothing on the build of its creation tick ... spawn goes out at the next tick's sync pass" (:744) | WRONG for the shipped AOI | pairs are created in JAOI LateUpdate (:248-340), spawn flushed at LNGM:111 before the send loop (§4) |
| Withhold always wins (:745) | needs change | q4: not on owner pairs |
| Pre-kit stamp on `LogicUpdater.OnTick` subscribed at RegisterMessages; kit re-adds at StartServer `LiteNetLibManager.cs:221-222` (:753) | CONFIRMED (lines) / hazard | LNM:221-222; RegisterMessages once (LNM:150-157); lost after OnStopServer unsubscribes |
| Build subscribed at `OnStartServer` (BGNM:252-255) runs after the kit (:754) | STALE lines, CONFIRMED order | components' OnStartServer at BGNM:254-257 (method :250-263); LNM:221-226 |
| Observer anchor LNGM:142-145; LNP:113-116 (:757) | CONFIRMED | TryGetPlayer; GetSpawnedObjects |
| `MeshWorkProbe.Meter.AddTickWork(ms)` (:758) | CONFIRMED | MeshWorkProbe.cs:289-295 |
| `ServerSendMessage(conn, 2, method, writer)`, catching TooBigPacketException (:759) | CONFIRMED | LNM:362-365; channel 2 = BaseGameEntity.cs:17; thrown at LiteNetPeer.cs:640/:647, rethrown by MeshMeteredTransport.cs:90-94 |
| Adapter default allow-list BIEMF3D + NavMeshEntityMovement (:761) | CONFIRMED as the CE-12 implementers | BIEMF3D:10; NMEM:13 |
| 20 reliable builds warning "catches NMEM's `_sendingDash`, which is never cleared on the server (C)" (:765) | STALE | CE-12 clears it (NMEM:639); the warning now catches a missing consume. SMC:390 (repo B) not checked |
| `Register` refuses when the grid path is enabled (BGNM:133) (:766) | STALE | BGNM:135 |
| `MeshTickScheduleCopy` of `LogicUpdater.cs:88-110` (:791) | CONFIRMED | LU:88-110; Tests/Editor/MeshTickScheduleCopy.cs exists |
| CE-12 check reports `perCall` today, `stamp` after CE-12 (:814) | WRONG | CE-12 is applied; expected `pass` (q4) |
| "CE-12 is needed only for the character A/B rows" (:815) | WRONG | needed for adapter correctness (finding 2) |
| §0.3: `SendServerEntityMovementState` (BGNM:661); parser copied from BGNM:589-618 | STALE | BGNM:691; client loop BGNM:591-623 (+ public static `ReadServerEntityState` :632-648) |
| §0.9: sender runs after the kit (`LiteNetLibManager.cs:221-226`) | CONFIRMED lines; reasoning superseded | consume sets of kit and sender are disjoint (q4) |
| §0.9: `CreateMovementData` keys on `LocalTick` (LNGM:75) | CONFIRMED line, obsolete | pass shape has no stamp |
| §0.9: NMEM set :348-350, cleared only :523, :552 | CONFIRMED lines; now also cleared server-side at NMEM:639 | |
| Summary 3: wrapper returns false (BGNM:703-704); per-call clears BIEMF3D:1375-1378 | STALE / WRONG | BGNM:735-736; the per-call clears are gone (comment at BIEMF3D:1373-1374) |
| §3 G1-S stress: "kit-loop patch (BGNM:695-704)" | STALE | BGNM:727-737 |
| q4 citation `MeshMapNetworkManager_Load.cs:52-65` | STALE | Load:58-71 |
| Contract: engine ports moved out of the contract file (summary 4) | CONFIRMED | MeshSyncContracts.cs has no transport/positions ports |
| `EveryPackageOfThisProject_HasNoStrayReference` covers new files (:809) | CONFIRMED exists | Tests/Editor/MeshPackageStrayReferenceTests.cs:80 |
