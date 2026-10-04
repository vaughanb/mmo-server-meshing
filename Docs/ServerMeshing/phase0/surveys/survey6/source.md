# Survey 6 (source side): handlers, kit adapter, kinematic body, sources, CE-12 check, tests

Read-only survey of the tree at commit 3659b8d plus working-tree changes (2026-10-02). Nothing in the repository was
changed. Line numbers are current; "sheet" means `7cb7243d-.../phase0_build_sheet.md`, "q4" means
`2cf88996-.../kitq_q4-send-loop.md`, "reply" means `2cf88996-.../kit_patch_9_10_reply.md`.

Paths are abbreviated: `IEMDH` = `Assets/OpenMMORPG/Core/Scripts/Gameplay/Interfaces/IEntityMovementDataHandler.cs`,
`BIEMF3D` = `.../EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs`, `NMEM` =
`.../EntityMovementSystems/NavMeshEntityMovement.cs`, `BGNM` = `Assets/OpenMMORPG/Core/Scripts/Networking/BaseGameNetworkManager.cs`,
`LNM` = `ThirdParty/LiteNetLibManager/Scripts/LiteNetLibManager.cs`, `LNGM` = `.../GameApi/LiteNetLibGameManager.cs`,
`LNP` = `.../GameApi/LiteNetLibPlayer.cs`, `KCB` = `Assets/ServerMeshing/Mesh/MeshKinematicCharacterBody.cs`,
`Slots` = `Assets/ServerMeshing/Mesh/Sync/MeshMovementHandlerSlots.cs`.

## 0. Headline findings

1. **CE-12 is in the tree, in the space team's "pass" shape, not the §0.9 stamp shape.** `IEntityMovementServerStateFlags`
   (IEMDH:24-31) and `EntityMovementServerStateFlagsPass` (IEMDH:39-66) exist; BIEMF3D and NMEM implement the interface
   (BIEMF3D:10, NMEM:13); both BGNM send loops Begin/Record/ConsumeAll (BGNM:705/737/799 and 821/868/982). Upstream:
   `Docs/ServerMeshing/upstream/README.md:15, 35-37` (patch 0006, merged into upstream **`develop`** on 2026-10-02 via PR #3;
   README:26 says the step-6 sender needs a kit carrying 0006).
2. **Consequence: the adapter's consume is now a correctness requirement.** The writes no longer clear anything
   (BIEMF3D:1373-1375, NMEM:629-631). A wrapped handler is never recorded by the kit (wrapper returns false, BGNM:735-737), so
   unless the sender Records the *inner* handler after its one write and ConsumeAll's after the fan-out, IsTeleport/IsJump/IsDash
   go out reliably on every record forever. Sheet summary item 3 ("CE-12 is not needed for the kit adapter to be correct",
   sheet:51-57) is now **wrong**; §0.9's "post-pass refused" is superseded (reply:92-94 withdrew the refusal).
3. **No step-6 type exists yet.** `MeshStatePacketWriter`, `MeshStateFanout`, `IMeshStateTransport`, `IMeshStatePositions`,
   `MeshStateSender`, `MeshKitMovementStateSource`, `MeshDistanceSendPolicy`, `MeshStateSenderStats`, `ServerMeshCe12Check`,
   `MeshEntityStatePacketParser`, `MeshFakeStampedKitHandler`/pass fake, `MeshFakeStateTransport`, `MeshFakeStatePositions`,
   `MeshFakeStateSource` are absent (grep over Assets/ServerMeshing). Present: contracts file, slots, replaceable-layer interface,
   kinematic body, `MeshTickScheduleCopy`, `MeshFakeKitHandler`, `MeshFakeServerStateHandler` (already a pass-shaped
   consume counter), `MeshFakeReplaceableLayer`, `MeshFakeLog`, `MeshFakeTransport` (an `ITransport`, not a state port).
4. **The kinematic body still always returns true to the kit** (KCB:308-316) and `WriteState` routes through it (KCB:319-322).
   Registering it with the sender without a "sender owns me" state gives two writers and breaks the teleport stamp (q4:83-84).
5. **The adapter must use `MeshMovementHandlerSlots.For(manager)`** (the same instance Puppets uses,
   `MeshMapNetworkManager_Puppets.cs:68`). If it used another instance, `EnsurePuppetMotion`'s `TryGetKitHandler`
   (Puppets.cs:113) would return the wrapper as "kit", `Refusal` (KCB:97) would answer `Handler`, and every puppet would stay
   steered.
6. **Contract text conflicts with the decided owner rule.** `MeshSendRate.Withhold` is documented as "sends nothing at all, not
   even urgent, reliable or first-send records" (`Mesh/Sync/MeshSyncContracts.cs:99-100`), and the sheet test says "Withhold
   gives nothing, even urgent"; the decided rule is "never withhold an owner pair" (reply:99; q4:159). The contract file is the
   one the space team compiles alone (`MeshSyncContractTests.cs:21`), so a doc change is a contract change to announce.

---

## 1. `IEntityMovementDataHandler` and the CE-12 additions

### 1.1 Interface members (IEMDH:8-18)

```csharp
public interface IEntityMovementDataHandler
{
    uint ObjectId { get; }                                                     // :10
    long ConnectionId { get; }                                                 // :11
    MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers);   // :13 (grid/job path)
    void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader);    // :14
    void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader);    // :15
    bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably);  // :16
    bool WriteServerState(long writeTimestamp, NetDataWriter writer,out bool shouldSendReliably);   // :17
}
```

No default members (C# 9 / .NET 4.7.1 anyway). No `DiscreteVersion`, `IsAtRest`, `Identity` or "has pending flags" member.

### 1.2 CE-12 additions (IEMDH:20-66)

- `public interface IEntityMovementServerStateFlags { void ConsumeServerStateFlags(); }` (IEMDH:24-31). Doc: "Called by the
  server send loop once per tick, after its last player, on each handler it wrote; never from a write."
- `public sealed class EntityMovementServerStateFlagsPass` (IEMDH:39-66): private `HashSet<IEntityMovementServerStateFlags>
  _written` (:41); `int RecordedCount` (:43); `Begin()` clears without consuming (:49-52, "at least once, never lost");
  `Record(IEntityMovementDataHandler)` adds only if the handler implements the interface (:54-58; null is ignored because
  `null is X` is false — pinned by `MeshKitEditTests.cs:34-41`); `ConsumeAll()` consumes each once then clears (:60-65).
- It is an **instance** class, public, deduping (HashSet). BGNM holds a protected one per manager (BGNM:110-111), so the sender
  can and should hold **its own instance** — the two consumed sets never overlap.
- The requested `bool HasServerStateFlags { get; }` (q4:188; reply:116) was **not** added. The adapter learns urgency only
  from the write's `shouldSendReliably`, i.e. after `WriteState`. Hence "write every subscribed source every build" is
  mandatory (q4:34).
- Minor: `RecordedCount`, `Record`, `ConsumeAll` have no XML doc (kit file; not ours to fix).

### 1.3 How the two kit handlers implement it now

**BIEMF3D** (a plain partial class, not a MonoBehaviour; created by `CharacterControllerEntityMovement` at
`CharacterControllerEntityMovement.cs:230`, exposed as `Functions` :178, registered through `Functions.OnIdentityInitialize()`
:313, teleport forwarded at :601):

- Flags: `_sendingJump` :133, `_sendingDash` :134, `_isTeleporting` :170, `_stillMoveAfterTeleport` :171,
  `_isServerWaitingTeleportConfirm` :187, `_isStarted` :191 (set in `EntityStart` :205-213).
- Set sites on the server: Teleport :367-383 (`_isServerWaitingTeleportConfirm = true` only if `!IsOwnerClientOrOwnedByServer`
  :376-377; `_isTeleporting = true` :378 synchronously before the `await` on the teleport preparer); jump/dash in
  `UpdateInterpolate` when `IsServer` (:470-481) and at :708/:749.
- `CreateMovementData` :1286-1333: ORs IsJump/IsDash/IsTeleport, `shouldSendReliably` when any is set, **no clear**; CE-12
  comment at :1329-1330. It does **not** key on `LocalTick` (the §0.9 stamp idea was not adopted).
- `WriteServerState` :1335-1376: `return false` when `!_isStarted` (:1338-1339); jump → reliable + IsJump (:1341-1349); dash →
  reliable + IsDash (:1350-1358); teleport → reliable, `MovementState |= IsTeleport` if still-move else
  `MovementState = IsTeleport` (:1359-1370); `Entity.ServerWriteSyncTransform3D(_movementForceAppliers, writer)` (:1372);
  CE-12 comment (:1373-1374); `return true` (:1375). The old per-call clear (former :1375-1378) is gone.
- `ConsumeServerStateFlags` :1378-1386 clears `_sendingJump`, `_sendingDash`, `_isTeleporting`, `_stillMoveAfterTeleport`;
  leaves `_isServerWaitingTeleportConfirm` and `_isClientConfirmingTeleport` (pinned by `MeshKitEditTests.cs:99-116`).
- `OnTeleport` :1680-1697 now also restarts interpolation (CE-12, KIT_AUDIT B10, :1691-1696).
- `ObjectId => Entity.ObjectId` :87, `ConnectionId => Entity.ConnectionId` :88 (live, follows owner changes).
- Registration: `OnIdentityInitialize` :222-226 does `TryRemove(ObjectId)` then `TryAdd(ObjectId, this)` (it silently evicts a
  wrapper); `OnNetworkDestroy` :228-231 `TryRemove(ObjectId)` unconditionally. CE-9 owner-change reset :233-242.

**NMEM** (MonoBehaviour component, `BaseNetworkedGameEntityComponent<BaseGameEntity>`):

- Flags: `_sendingDash` :59, `_isTeleporting` :72, `_stillMoveAfterTeleport` :73, `_isServerWaitingTeleportConfirm` :85,
  `_isStarted` :98 (set in Awake :124, so always true after Awake). No jump flag.
- Teleport :242-259 (confirm wait only for a client-owned entity :251-252). Dash: `_sendingDash = true` every
  `UpdateMovement` while `_acceptedDash || _isDashing` (:348-350); both are reset each `ManagedUpdate` (:332-333), so a dash is
  a one-frame raise.
- `CreateMovementData` :562-599 (no clear; CE-12 comment :595-596). `WriteServerState` :601-632 (dash → reliable :606-614;
  teleport → reliable :615-626; `this.ServerWriteSyncTransform3D(...)` :628; CE-12 comment :629-630; `return true` :631).
- `ConsumeServerStateFlags` :634-642 clears `_sendingDash` (the B11 fix: stock never cleared it on a server), `_isTeleporting`,
  `_stillMoveAfterTeleport` (pinned by `MeshKitEditTests.cs:118-130`).
- Registration `OnIdentityInitialize` :139-143 (TryRemove+TryAdd), `OnNetworkDestroy` :145-148. CE-9 :87-94.

### 1.4 What a write's bytes are

`EntityMovementFunctions.ServerWriteSyncTransform3D` (`.../EntityMovementSystems/EntityMovementFunctions.cs:136-145`):

```
PutPackedUInt((uint)MovementState)        // 1 B for flags <= 240 (IsTeleport alone = 0x80); 2 B once IsDash (2048) etc.
Put((byte)ExtraMovementState)             // 1 B
PutVector3(EntityTransform.position)      // 12 B (3 floats, NetDataWriterExtension.cs:70-75)
PutPackedInt(GetCompressedAngle(yaw))     // zigzag of round(yaw*1000) (EntityMovementFunctions.cs:232-235); 1-4 B
PutList(movementForceAppliers)            // int32 count (NetDataWriterExtension.cs:121-133) + each applier:
                                          //   byte mode, Vector3, byte source, [packed int id, level], 4 packed half ushorts
                                          //   (EntityMovementForceApplier.cs:64-78)
```

- Writes nothing at all when `!Entity.IsServer` (:138-139) but the handler still returns true (zero-length record; never on a
  dedicated server).
- Typical sizes: no appliers 19-22 B (the body's golden at yaw 90 is 22 B: `11 00 | 12 B | FA 20 BF 02 | 00 00 00 00`,
  `MeshKinematicCharacterBodyTests.cs:17`); one dash applier adds roughly 19-27 B. The list is unbounded in principle, so
  "payload too big" is reachable only through force appliers.
- Client side reads the same layout, except the grid branch (`ClientReadSyncTransformMessage3D` :165-184: cell byte +
  quantized position + byte angle when the grid is enabled). That is why the sender must refuse the grid path.
- `MovementState` enum: IsJump 64, IsTeleport 128, IsDash 2048 (`.../Enums/MovementState.cs:13-18`).

### 1.5 What `shouldSendReliably` depends on

- BIEMF3D: `_sendingJump || _sendingDash || _isTeleporting` at write time (:1341-1370). NMEM: `_sendingDash || _isTeleporting`
  (:606-626). Nothing else (not position, not distance, not owner). Every kit one-shot therefore implies reliable; that is the
  basis of "urgent = sendReliably" (q4:7).
- The kinematic body: `TakeTeleport(writeTimestamp)` (KCB:312-313, 326-341).

### 1.6 `DiscreteVersion` or equivalents

- Kit handlers have none. The only equivalent signal is `shouldSendReliably` / the IsTeleport bit in the written bytes.
  Recommendation: the adapter's `DiscreteVersion` stays constant (0); bumping it on a teleport would make the fan-out resend
  to all on the next 2 builds records that no longer carry IsTeleport (consumed) — harmless but pure overhead.
- `IsAtRest`: kit handlers expose no velocity through the interface; the adapter should return false (kit parity) unless a
  later step reads `CacheNavMeshAgent.velocity` / BIEMF3D state.
- The body: `DiscreteVersion` (KCB:74-75, +1 per snap in Accept :218-226) and `IsAtRest` (KCB:77-78).

### 1.7 `ConnectionId` and `ReadClientStateAtServer` delegation

- `HandleClientEntityStateAtServer` (BGNM:581-589) drops an upload unless
  `_entityMovementDataHandlers[objectId].ConnectionId == messageHandler.ConnectionId` (:586-587), then calls
  `dataHandler.ReadClientStateAtServer` (:588). A wrapper in the slot is what the kit finds here, so the wrapper **must**
  delegate `ConnectionId` live (owner changes at promotion, `SetObjectOwnerImmediately`, Ghosting:1051/1298) and delegate
  `ReadClientStateAtServer`. Otherwise every real player's upload, and every phantom upload (step 5 phantoms go through this
  real receive path, `ServerMeshPhantomUploads.cs:13, 97-100`), is silently dropped.
- The body returns `ConnectionId => -1` (KCB:68-69) on purpose: the owner moves on its own cell.
- Server-side `ReadServerStateAtClient` is never called on a dedicated server; delegate anyway (host mode reads through the same
  dictionary at BGNM:609). `WriteClientState` returns early on a server (BGNM:652-653) — delegate.
- `CreateMovementData` must delegate (only the refused grid path calls it, BGNM:867).

---

## 2. The add-on's handler-slot machinery

### 2.1 `IMeshReplaceableHandlerLayer` (`Mesh/Sync/IMeshReplaceableHandlerLayer.cs:9-16`)

One member, `void OnReplaced()`: called once after another layer took the slot; "It may install itself again later (for
example when MeshMovementHandlerSlots raises LayerRemoved for its object)" (:12-14). The installing layer must itself be the
`IEntityMovementDataHandler` (:5-7).

### 2.2 `MeshMovementHandlerSlots` (Slots:18-193)

- Per-manager instance through a `ConditionalWeakTable` (Slots:20-22, `For` :40-45); public ctor over a
  `ConcurrentDictionary` as the test seam (:28-31). Main thread only (:16).
- `TryInstall(id, layer, replaceable, out error)` :53-102:
  - throws `ArgumentNullException` on null layer; throws `ArgumentException` if `replaceable` and the layer does not implement
    `IMeshReplaceableHandlerLayer` (:57-58);
  - fails, naming the id, when there is no handler (:59-64, also drops a stale record), when the same layer is already there
    (:65-69), when the slot holds a non-replaceable Mesh layer (:80-84);
  - a stale record (kit re-registered) is dropped and the current value becomes "kit" (:75-79);
  - over a replaceable layer: keeps the **original** kit handler as `kit` (:85-89);
  - CAS write `TryUpdate(id, layer, current)` (:92-96; the "race" branch — not reachable from a single-threaded test, no test
    covers it today);
  - records then calls `replaced.OnReplaced()` **after** the record is in place (:97-100; pinned by
    `MeshMovementHandlerSlotsTests.cs:315-327`).
- `TryRemove(id, layer, out error)` :109-132: only the installer removes (:118-122); CAS back to `record.Kit` (:124-128); raises
  `LayerRemoved(id)` synchronously (:130). Note: it restores the **original kit handler**, never a replaced adapter.
- `TryGetKitHandler` :138-147 (the original kit handler when a recorded layer sits there, else the slot value). `HasLayer`
  :150-155. `Forget` :161-167 (removes the entry only if it still holds the layer, never restores). `Reset` :170-173.
- Current users: only the kinematic body (KCB:130-204) and Puppets (`_slots` Puppets.cs:29, :68, :80). `LayerRemoved` has no
  subscriber outside tests (`MeshMovementHandlerSlotsTests.cs:26, 305`).

### 2.3 `MeshKinematicCharacterBody` (KCB)

- `public sealed class MeshKinematicCharacterBody : IEntityMovementDataHandler, IMeshStateSource` (KCB:21). It does **not**
  implement `IMeshReplaceableHandlerLayer` and does not implement `IEntityMovementServerStateFlags` (deliberately; q4:85).
- `Refusal(kitHandler, mounted, summoned, gridDisabled)` KCB:95-106: `Handler` unless BIEMF3D or NMEM (:97-98), then `Grid`
  unless grid disabled (:99-100), then Mounted, Summoned.
- `WriteKitTransform` KCB:113-122 writes exactly the kit's layout with an empty applier list (`Put(0)` :121).
- `Begin(entity, slots, out error)` KCB:130-174: StopMove; `slots.TryInstall(_objectId, this, false, ...)` (:153, **not
  replaceable**); disable keys; `movement.enabled = false`. Needs a real `BaseCharacterEntity` (`entity.Movement`,
  `MovementDisableState`, `FallDamageDisableState`) — not constructible in EditMode; only the null-entity throw is tested
  (`MeshKinematicCharacterBodyTests.cs:107-115`).
- `End(entity, slots)` KCB:181-204: idempotent; clears the stamp; `Forget` if the entity is gone else `TryRemove` (→
  `LayerRemoved`); re-enables movement and look rotation. It does **not** consume the kit handler's pending flags (q4:162
  "Optional: End consumes the restored kit handler's stale flags when the interface exists").
- `WriteServerState` KCB:302-316 — always `return true`; `shouldSendReliably = TakeTeleport(writeTimestamp)`; writes
  `TeleportFlags` (128) alone when the stamp is live, else `_flags`.
- `WriteState` KCB:318-322 — `return WriteServerState(sampleTimestamp, writer, out sendReliably);`
- `TakeTeleport` KCB:326-341 — the one-timestamp stamp: an armed teleport is taken by the first write and stamped with its
  timestamp (:328-334); later writes with the same timestamp return true (:337-338); any other timestamp ends it (:339-340).
  Pinned by `MeshKinematicCharacterBodyTests.cs:324-341` and `:363-381`. Armed only for a NavMesh-kit body that is not
  known unobserved (KCB:221-225); `ApplyNow` drops it (:259-267).
- **Hazard (two writers, q4:83-84):** once the body registers with the sender, the kit loop (through the slot) and the build
  both send it, and if the build's `sampleTimestamp` differs from the kit's `writeTimestamp`, whichever writes second ends the
  stamp and that copy never carries IsTeleport. Needed: a "registered with the sender" state in which the kit-facing
  `WriteServerState` returns false and `WriteState` writes (the stamp body moves into a private method both share).
  Alternatively the sender registration happens outside the body and the body gets a flag setter. The step-3 sheet text
  ("held for 3 timestamps", sheet:535-538, 566) is stale against KCB:326-341 (q4:86).

### 2.4 `MeshMapNetworkManager_Puppets.cs` (the body's lifecycle)

- `PuppetsOnStartServer` :44-75 (`_slots = MeshMovementHandlerSlots.For(this)` :68); `PuppetsOnStopServer` :78-82
  (`_slots?.Reset()`).
- `EnsurePuppetMotion` :105-142: `_slots.TryGetKitHandler(objectId, out kit)` (:113) → `Refusal(kit, ...)` with
  `gridDisabled = Instance == null || Instance.IsDisabled` (:118-119) → new body over `kit` (:134) → `body.Begin(entity, _slots,
  ...)` (:135). Called from Ghosting:1559 at a snapshot (lazy, on the first snapshot after demotion).
- `StopPuppetMotion` :172-177 → `EndPuppetMotion` :184-195 → `body.End` (→ `TryRemove` → `LayerRemoved`). Called at arrival
  promotion (Ghosting:1155, :1164) and `PromoteGhostInPlace` (Ghosting:2570); mounted → `EndPuppetMotion` (Ghosting:1553);
  Ghosting:976 too.
- `KinematicPuppetsUpdate` :202-217 (destroyed entities: `End` → `Forget`). `PoseKinematicPuppets` :224-241, called at the start
  of the kit's tick (`MeshMapNetworkManager_Load.cs:58-71`, line :60), i.e. after a pre-kit stamp and before the build.
- Promotion sequence that matters for flags: `StopPuppetMotion` (Ghosting:1155) then `player.Teleport(...)` (Ghosting:1191) on
  the restored kit handler, while server-owned. With a lazy rewrap at the next pre-kit stamp, the next build writes the inner
  handler with IsTeleport and consumes it. Good — provided the rewrap is not performed inside `LayerRemoved` (see 2.6).

### 2.5 How a `MeshKitMovementStateSource` wrapper would install, be replaced and be restored

Install (lazy, at the pre-kit stamp):
1. Candidate id (spawn / `LayerRemoved` / a per-build rewrap check) is queued.
2. At the pre-kit stamp: `slots.TryGetKitHandler(id, out kit)`; refuse if `kit is IMeshStateSource` (the body), if
   `slots.HasLayer(id)` (another Mesh layer), if `kit.GetType()` is not allow-listed (exact-type check; default BIEMF3D, NMEM —
   the same pair `Refusal` accepts), or if the grid path is on.
3. `slots.TryInstall(id, wrapper, replaceable: true, out error)`; on success `fanout.Register(wrapper)`.

Replaced by a kinematic body:
4. `body.Begin` → `TryInstall(id, body, false)` → the slot records the original kit (Slots:87) → `wrapper.OnReplaced()`
   (Slots:100). `OnReplaced` must unregister the wrapper from the fan-out **synchronously** so the body can register under the
   same object id without a "duplicate id" refusal. The body's kit handler argument is the original kit (Puppets.cs:113,134).
5. The body registers with the sender directly (needs new code: today nothing registers it; EnsurePuppetMotion is the natural
   place, after a successful Begin), and switches its kit-facing write off (2.3).

Restored:
6. `body.End` must unregister the body from the sender **before** `TryRemove` (or the queued rewrap must not register before
   the body is gone). `TryRemove` restores the original kit handler and raises `LayerRemoved(id)`.
7. The adapter's `LayerRemoved` handler should **only enqueue** id; the next pre-kit stamp rewraps (step 2-3). Re-installing
   inside the event (as `MeshMovementHandlerSlotsTests.cs:298-313` shows is possible) would register a new wrapper while the
   body may still be registered and while `End` is mid-way (movement still disabled).

Kit-side evictions the per-build check must handle: `OnIdentityInitialize` replaces the wrapper with the kit handler
(BIEMF3D:224-225; NMEM:141-142) — the slot's stale-record branch (Slots:75-79) then accepts a re-install; `OnNetworkDestroy`
removes the entry (BIEMF3D:230; NMEM:147) — unregister; `Clean` clears the whole dictionary at stop (BGNM:220) — `Reset`.

### 2.6 The order test the sheet asks for (spawn, lazy wrap, kinematic ghostify replaces the wrapper, body registers directly, promotion, rewrap)

Feasible in EditMode at the slot + fan-out level, **not** through `body.Begin`/`End` (they need a `BaseCharacterEntity`):

- Dictionary + `new MeshMovementHandlerSlots(dict)` (as `MeshMovementHandlerSlotsTests.cs:22`), a pass-shaped fake kit handler
  in `dict[Id]` ("spawn").
- Run the adapter's queue processing ("lazy wrap"): assert `dict[Id]` is the wrapper and the fan-out holds it.
- Construct a real `MeshKinematicCharacterBody` over `new GameObject().transform` and the fake kit (as
  `MeshKinematicCharacterBodyTests.cs:35-38`), install it with `slots.TryInstall(Id, body, false, ...)` (what Begin does at
  KCB:153): assert `OnReplaced` ran, the wrapper is unregistered, the record's kit is the fake.
- Register the body with the fan-out: assert one source for Id, the body's.
- "Promotion": unregister the body, `slots.TryRemove(Id, body, ...)`: assert `dict[Id]` is the fake kit, `LayerRemoved` queued
  Id, the wrapper is not yet registered.
- Next pre-kit stamp: assert the rewrap (wrapper back, registered once, kit pending flags reach subscribers on the next build and
  are consumed).
- To cover the real Begin/End ordering, a seam is needed (e.g. an internal-free static helper the body and the test share, or
  a slot-level "install/remove" split out of Begin/End). Tests compile without InternalsVisibleTo, so the seam must be public.

---

## 3. Which entities would be sources

| Entity | Handler in slot | Prefab evidence | Notes |
|---|---|---|---|
| Player characters (real) | BIEMF3D via `CharacterControllerEntityMovement` | `Assets/BaseDemo/Prefabs/GamePlay/CharacterEntities/BaseCharacter-CE.prefab` (CCEM guid `ac04ae93...`) | Owned by a client; `ConnectionId` live; jumps set `_sendingJump` on the server (:470-474). |
| Test mount | BIEMF3D | `Assets/ServerMeshing/TestMap/Prefabs/TestMount-CE.prefab` | Vehicles sync through the driver (BGNM:669-690); the body refuses mounted; the adapter can wrap (delegation only). |
| Monsters / pets | NMEM | `Assets/BaseDemo/.../BaseEnemy-CE.prefab` (NMEM guid `7fc9c1cb...`) | `MonsterSpawnArea.cs:136-138` calls Teleport before NetworkSpawn, so a fresh monster carries a pending IsTeleport. |
| Phantom characters (step 5) | BIEMF3D | spawned from the player entity (`ServerMeshPhantomClients_Spawn.cs:109-145`, `NetworkSpawn(spawnObj, 0, owner)` :138) | Owner = phantom connection (`ServerMeshPhantomTransport.IdBase = 1_000_000`, `ServerMeshPhantomTransport.cs:21`); **not** in `ServerUserHandlers` (no AddPlayerCharacter), so the observer anchor needs the "first owned object" fallback (LNP:113-116). `input` payload switches `movementSecure` to ServerAuthoritative (:132, :147-157). Fillers are server-owned (owner -1, :168) and may be moved by `ServerMeshKinematicWalk` with the movement component disabled (`TestMap/Phase0/ServerMeshKinematicWalk.cs:8-19, 189-192`) — BIEMF3D still writes from the transform. |
| Phantom eyes (step 5) | none | `ServerMeshPhantomEye.cs:29-35` (bare identity, `SetIsHide`) | Not a source; an observer only (owned object for the anchor). |
| Kinematic puppets | `MeshKinematicCharacterBody` | — | Register directly as `IMeshStateSource`; never wrapped (adapter refuses `IMeshStateSource`; slot refuses over a non-replaceable layer). |
| Steered puppets | the kit handler (BIEMF3D/NMEM) | — | Teleport through the kit (Ghosting:1960, monsters :2636); wrapped like any kit entity; server-owned so no confirm wait. |
| Other kit movers | RigidBody2D, BuiltInSimple3D, SimpleNavMesh | not used by any prefab found | Not allow-listed; they still clear per call / state-machine (reply:122-125). Must stay on the kit path. |

- Allow-list default: exact types `BuiltInEntityMovementFunctions3D`, `NavMeshEntityMovement` — matches `KCB.Refusal`
  (KCB:97). Use exact-type comparison (`GetType() ==`) if subclasses must be opted in explicitly; `is` would admit subclasses
  whose writes may differ.
- **Grid path refusal:** BGNM:135-138 chooses `SendServerEntityMovementState` only if
  `DefaultGridManagerComponent.Instance.IsDisabled` (`.../GridManager/DefaultGridManagerComponent.cs:14, 23`). On the grid path
  the kit calls `CreateMovementData` on whatever is in the slot (BGNM:867) and records it (BGNM:868): a wrapper would be sent by
  the kit anyway and never consumed (the wrapper has no interface) → stuck flags. So `Register` must refuse when the grid is on.
  Use the same null-guard as Puppets.cs:118 (`Instance == null || Instance.IsDisabled` means disabled). Note the job path
  records on `CreateMovementData`, before the job decides what is written (BGNM:867-868 vs :901) — the reply's "only for records
  actually written" (reply:111) is not what the code does; irrelevant to us while the grid is refused.

---

## 4. `ServerMeshCe12Check` (rig check, `MESH_CE12_CHECK=1`)

Status: does not exist. `MESH_CE12_CHECK` is in `PENDING_KEYS` (`Tools/ServerMeshing/p0tools.py:74`), so a plan setting it is
refused today; it is not in `regress.ENV_KEYS` (`Tools/ServerMeshing/regress.py:69`). Same for all `MESH_SENDER*` keys
(p0tools.py:70-73).

### 4.1 What it must classify now (q4:27-28, 160)

Sequence: Teleport; write at T; write at T; write at T+50; `ConsumeServerStateFlags()` if the handler implements the interface;
write at T+100. Record per write the IsTeleport bit (first packed uint & 128) and `shouldSendReliably`.

| Shape | first (T) | second (T) | next (T+50) | consume available | after (T+100) |
|---|---|---|---|---|---|
| perCall (stock kit, pre-0006) | 1 | 0 | 0 | no | 0 |
| stamp (§0.9 shape, also our body) | 1 | 1 | 0 | n/a | 0 |
| **pass (CE-12 as delivered)** | 1 | 1 | 1 | yes | 0 |
| stuck | 1 | 1 | 1 | yes or no | 1 |
| absent | 0 | – | – | – | – |

- Expected on this tree: **`pass`**. The sheet's acceptance "perCall on today's kit and stamp after CE-12" (sheet:813) and its
  log line `shape=stamp|perCall|postPass|absent first=1 second=1 next=0` (sheet:775) are stale: rename `postPass` → `pass`, add
  `stuck`, and add an `after=` (post-consume) field and `consume=yes|no`.
- `perCall` cannot be observed by code that names `IEntityMovementServerStateFlags` at compile time on a kit without 0006 —
  TestMap would not compile there. If the check must still diagnose a stock kit, look the interface up by name at runtime
  (`Type.GetType("MultiplayerARPG.IEntityMovementServerStateFlags, Assembly-CSharp")` + `GetMethod("ConsumeServerStateFlags")`);
  otherwise accept that only pass/stamp/stuck/absent are reachable here. The same compile dependency applies to Mesh/ (the
  sender's own pass): upstream has 0006 only on `develop` (README:35-37).
- Classification should be a pure static function (so `ServerMeshCe12CheckTests` can feed it the recorded sequences —
  feasible in EditMode).

### 4.2 Driving it on a hidden, server-owned rig character

- Spawn: reuse the phantom spawn recipe (`ServerMeshPhantomClients_Spawn.cs:109-145`: `Assets.GetObjectInstance(...)`,
  `SetupByMetaData`, `CloneTo`, `NetworkSpawn(spawnObj, 0, -1)`), owner -1 so `IsOwnedByServer` and no confirm wait
  (BIEMF3D:376-377).
- Hide before spawning, exactly as the eye does: `identity.SetIsHide(setter, true)` with empty HideExceptions
  (`ServerMeshPhantomEye.cs:24-35`). Hidden ⇒ no subscriber ⇒ the kit's pass never writes/records/consumes it, and the sender
  (if on and it wraps the character) never writes it either.
- Wait until the handler is started: BIEMF3D `WriteServerState` returns false until `EntityStart` (:1338-1339, :205-208) — retry
  the first write over a few frames; a persistent false is "absent".
- Teleport through `entity.Teleport(...)` (`BaseGameEntity_MoveFunctions.cs:348-...`); if `ActiveMovement` is null it defers
  (:350-357), so do not disable the movement component. `_isTeleporting` is set synchronously even with a teleport preparer
  (BIEMF3D:378-381).
- Get the real kit handler with `MeshMovementHandlerSlots.For(manager).TryGetKitHandler(id, out kit)` (works whether or not the
  adapter wrapped it). Write into a scratch `NetDataWriter`, read the first packed uint with a `NetDataReader`.
- Do all five steps synchronously in one call (no kit tick in between), then `NetworkDestroy` the character.
- Log line must be invariant culture; TestMap may say "ship"/use rig names, Mesh/ may not.

---

## 5. Test feasibility (EditMode, Assembly-CSharp-Editor, no InternalsVisibleTo)

General facts:
- Tests are `public class ...Tests` in `MultiplayerARPG.MMO.Mesh.Tests`; fakes are `internal sealed` in the same assembly
  (e.g. `MeshFakeKitHandler.cs:6`). There is no asmdef in `Assets/ServerMeshing` (find returned none) — tests see every
  **public** kit and Mesh type; kit `internal` members (e.g. `BGNM.SendServerEntityMovementState` :691) are not visible.
- `LiteNetLib.Utils.NetDataWriter/Reader` work in EditMode (used throughout).
- `LiteNetLibIdentity` can be `AddComponent`ed to a `HideAndDontSave` GameObject and given subscribers with `AddSubscriber`
  (`MeshKinematicCharacterBodyTests.cs:40-47`); `GetSubscribers()` returns a non-allocating enumerator
  (`LiteNetLibIdentity.cs:726`).
- `FormatterServices.GetUninitializedObject` is used for BIEMF3D/NMEM field-level tests (`MeshKitEditTests.cs:102, 121`;
  `MeshKinematicCharacterBodyTests.cs:73-75`). Calling their `WriteServerState` is **not** feasible (needs `Entity`/transform),
  so adapter tests use fakes; the allow-list test can use uninitialized instances as *kit handlers that are never written*.
- Culture tests use `[SetCulture("de-DE")]` (`MeshTickWindowLineTests.cs:11, 31, 41, 102`; `MeshSendMeterTests.cs:354`).

| Test class | Feasible? | Reuse / notes |
|---|---|---|
| `MeshStatePacketTests` | Yes | Writer + a fake `IMeshStateTransport` that copies bytes per send. Parser copy `MeshEntityStatePacketParser` must mirror **current** BGNM:591-623 including the CE-13 guard `dataLength < 0 \|\| dataLength > reader.AvailableBytes` (:604-608). Packed-id boundaries match `PutPackedULong` exactly (`NetDataWriterExtension.cs:179-215`): ≤240 → 1 B; ≤2287 → 2 B (241..248 lead); ≤67823 → 3 B (249 lead); ≤16777215 → 4 B (250 lead, LE 3 bytes); ≤4294967295 → 5 B (251 lead). Header: `PutPackedUShort(119)` = 1 B (0x77, `TransportHandler.cs:90-96`), `PutPackedLong(ts)` = two zigzag packed ints (high, low) (`NetDataWriterExtension.cs:161-165`) — 1+2..5 B for current ms timestamps, int32 count. Worst header 12 B → max single payload `1020 − 12 − 4 − idBytes = 1004 − idBytes`, consistent with the sheet. Example golden header for ts=1000: `77 00 F7 E0` + count. Kit cap rule for comparison: BGNM:750-751 (`idAndLengthSize = 8`, `>= MAX_UNRELIABLE_PACKET_SIZE`), SS:12 (`MAX_UNRELIABLE_PACKET_SIZE = 1020`). Decided change: reliable records too big for 1,020 go in their own reliable packet (reply:101; q4:159) — the sheet's "reliable packets never fragment" needs that exception. |
| `MeshStateFanoutTests` | Yes | Pure engine with fake transport/positions/policy. P×V 400×500 = 200k pairs per build; fine for a handful of builds if fakes count rather than store every packet. Add the decided rows (q4:161): throwing build leaves flags and the next build reaches all; Withhold on the owner still delivers reliable; Withhold on a non-owner delivers nothing and still consumes; too-big reliable in its own packet; flagged record to 3 subscribers at 20/5/1 Hz when one is due; second half never holds a reliable record; measure mode never consumes; new-pair rule only for AOI-created pairs, never owner (q4:196). Withhold vs owner conflicts with `MeshSyncContracts.cs:99-100` (see §0.6). |
| `MeshStateCadenceTests` | Yes | `MeshTickScheduleCopy` exists: `Assets/ServerMeshing/Tests/Editor/MeshTickScheduleCopy.cs:10-47` (public, ctor `(int tickHz, long frequency)`, `Update(long elapsedTicks)` returns ticks; cap 5 then zero the backlog). It copies `LogicUpdater.Update` (`.../GameApi/LogicUpdater.cs:88-110`) — CONFIRMED identical logic. Used today in `MeshMeasurementTests.cs:387-388, 394, 499, 534`. Drive Build(n times per frame, same frameIndex) and count builds/skips. Period is `(long)(1/20*freq)`; with freq 10,000,000 it is exact (500,000). |
| `MeshKitMovementStateSourceTests` | Yes with fakes | Pass-shaped fake: either extend `MeshFakeServerStateHandler` (`Tests/Editor/MeshFakeServerStateHandler.cs:11-58`, already `IEntityMovementServerStateFlags` with a `consumes` counter; its `WriteServerState` writes nothing and is never reliable today — add pending flags/bytes without breaking `MeshKitEditTests`), or add `MeshFakePassKitHandler` (q4:161; new reserved word). `MeshFakeStampedKitHandler` (§0.15, sheet:805) is obsolete. Slots via `new MeshMovementHandlerSlots(dict)`; `MeshFakeReplaceableLayer` for "slot already has a layer"; `MeshFakeLog` (`MeshFakeCoordinatorParts.cs:11-20`) with `IMeshLog` (`Mesh/MeshLayoutCoordinator.cs:30-35`) for the once-only warnings; uninitialized BIEMF3D/NMEM for allow-list checks. "TryUpdate race" cannot be produced through `MeshMovementHandlerSlots` in one thread (Slots:59-96 has no window); test it at the adapter level (queued kit handler ≠ current slot value → refuse/requeue). Grid-enabled refusal needs a seam (a `Func<bool> gridDisabled` or bool parameter) — tests must never touch `DefaultGridManagerComponent` (step 3 rule). |
| `MeshDistanceSendPolicyTests` | Yes | Pure. |
| `MeshStateSenderStatsTests` | Yes | `MeshWorkHistogram(float binMs, int binCount)` (`Mesh/MeshWorkHistogram.cs:26`); de-DE pattern as above; the p0tools half goes in `Tools/ServerMeshing/test_p0tools.py`. |
| `ServerMeshCe12CheckTests` | Yes if the classifier is a pure static | TestMap type used from Tests is fine (Tests are not packaged). |

Reserved names (stray-reference rule): `ServerMeshPackageBuilder.FindStrayReferences` (`Mesh/Editor/ServerMeshPackageBuilder.cs:226-248`;
regex :229-230; whole-word match :245) treats every type **declared** (at line start, nested included) in a script the package
does not carry as reserved for shipped scripts. The Mesh package carries only `Mesh/`, `Documentation`, `README.md`
(:73); the rig package carries `TestMap/` plus Mesh by dependency (:90-92). Comments count; case-sensitive. Reserved today
(non-prefixed): `Mode` (`TestMap/ServerMeshAutoClient.cs`), `Placement` (`ServerMeshLoadGenerator.cs`), `Callbacks`
(`TestMap/Editor/ServerMeshTestRunner.cs`), `Driver` (`Tests/Editor/MeshMeasurementTests.cs:171`), `MeshFakeClock`
(`MeshFakeCoordinatorParts.cs`), plus `BandEnvironment`, `PendingPhantomEvent`, `PhantomFighterBinding`, `PhantomLayoutEdges`,
`SyntheticPlayerBrain`, `ZoneBoundaryDetector`, `IServerMeshPhantomPayload`, and every `Mesh*Tests`/`MeshFake*`/`MeshTickScheduleCopy`.
No current Mesh/ file uses any of them (checked). Step-6 implications: Mesh/ comments must not say `MeshFakeServerStateHandler`,
`MeshTickScheduleCopy`, `ServerMeshCe12Check`, "ship" (game-neutrality, sheet §0.4), and no member named `Mode`.
`EveryPackageOfThisProject_HasNoStrayReference` lives in `MeshPackageStrayReferenceTests.cs` and will cover new files.

---

## 6. Sheet claims in step 6 (and the rules it leans on) — verified

| # | Sheet claim | Verdict | Current location / note |
|---|---|---|---|
| 1 | `MeshMapNetworkManager.cs:95-99`, add before `base.Awake()` | STALE (shift) | `Awake` :96-101, `base.Awake()` :100; `InstallSendMeter()` already adds a component at :99/:123 the same way. |
| 2 | "so that BGNM:123 collects it" | STALE | `ManagerComponents = GetComponents<BaseGameNetworkManagerComponent>()` at BGNM:125. |
| 3 | CE-12 at BIEMF3D:1337-1380 and :1290-1332, NMEM:562-632, "in the §0.9 shape only" | WRONG (already applied, other shape) | Applied as patch #9 pass shape: BIEMF3D CreateMovementData :1286-1333, WriteServerState :1335-1376, Consume :1378-1386; NMEM :562-599, :601-632, :634-642. |
| 4 | `MAX_UNRELIABLE_PACKET_SIZE` (SS:12) = 1020 | CONFIRMED | `LiteNetLibGameManager.StateSyncing.cs:12`. |
| 5 | Layout per BGNM:683-694, 708-710, 734-736 | STALE | Headers BGNM:715-725; reliable record :741-743; unreliable record :767-769. Layout itself CONFIRMED. |
| 6 | Kit removes/re-adds its handler at every StartServer (`LiteNetLibManager.cs:221-222`) | CONFIRMED | LNM:221-222; `OnStartServer()` at :226. |
| 7 | Pre-kit stamp "subscribed at RegisterMessages" runs first | CONFIRMED with a caveat | `RegisterMessages` runs once from `Awake → InitTransportAndHandlers` (LNM:97, 150-157; BGNM:192-205 calls `component.RegisterMessages(this)` :201-204). **Caveat:** the sheet also says both hooks are unsubscribed at OnStopServer; `RegisterMessages` is never called again, so after a stop/start the stamp is gone, and re-adding it in `OnStartServer` would put it *after* the kit. Keep the stamp subscribed for the manager's life (or re-add it before the kit's re-add, which no hook allows). |
| 8 | Build subscribed at `OnStartServer` (BGNM:252-255) runs after the kit | CONFIRMED (shift) | BGNM:250-259, component loop :254-257. |
| 9 | Observer anchor `TryGetPlayerCharacter` else first owned object (LNGM:142-145; LNP:113-116) | CONFIRMED | `IServerUserHandlers.TryGetPlayerCharacter` (`IServerUserHandlers.cs:28`, impl `DefaultServerUserHandlers.cs:40`); LNGM:142-145 `TryGetPlayer`; LNP:113-116 `GetSpawnedObjects()`. Phantoms need the fallback (not in ServerUserHandlers). |
| 10 | `MeshWorkProbe.Meter.AddTickWork(ms)` | CONFIRMED | `Mesh/MeshWorkProbe.cs:289` (`AddTickWork(double ms)`), `Meter` :484. |
| 11 | `ServerSendMessage(conn, 2, method, writer)` catching `TooBigPacketException` | CONFIRMED | `LNM.cs:362`; channel `BaseGameEntity.MOVEMENT_DATA_CHANNEL = 2` (`BaseGameEntity.cs:17`); `EntityState = 119` (`GameNetworkingConsts.cs:25`); kit catches at BGNM:760, 793. |
| 12 | Adapter: kit-facing `WriteServerState` returns false; kit skips it | CONFIRMED | BGNM:735-736 (`continue` on false, before Record :737). |
| 13 | "NMEM's `_sendingDash`, which is never cleared on the server (C)" | STALE | CE-12 clears it in `ConsumeServerStateFlags` (NMEM:639) — but only when someone consumes; for a wrapped NMEM that someone is our sender. The 20-reliable warning now catches a forgotten consume. ("SMC:390 for unowned ships" is repo B; not verifiable here, and must not appear in Mesh/ text.) |
| 14 | `Register` refuses when the grid path is enabled (BGNM:133) | STALE (shift) | BGNM:135-138. |
| 15 | Host connection skipped (BGNM:676-677) | STALE (shift) | BGNM:708-709 (`playerKvp.Key == ClientConnectionId`). Job path does not skip (commented out :824-825). |
| 16 | `MeshTickScheduleCopy` of `LogicUpdater.cs:88-110` | CONFIRMED | Copy exists (`Tests/Editor/MeshTickScheduleCopy.cs`); LogicUpdater.Update :88-110, cap :7. |
| 17 | Default allow-list BIEMF3D + NMEM | CONFIRMED (consistent) | Same pair as `KCB.Refusal` :97. |
| 18 | Adapter "never wraps an `IMeshStateSource` or a slot that already has a layer" | CONFIRMED as enforceable | Body is `IMeshStateSource` (KCB:21); `Slots.HasLayer` :150-155; a non-replaceable body refuses an install anyway (Slots:80-84). |
| 19 | CE-12 check classifies `stamp\|perCall\|postPass\|absent`; acceptance "perCall today, stamp after CE-12" | STALE | Now `stamp\|perCall\|pass\|absent\|stuck`, expected `pass` (q4:27-28, 160). |
| 20 | `MeshFakeStampedKitHandler` (CE-12 shape) test | STALE | Replace with a pass-shaped fake (q4:29, 161); `MeshFakeServerStateHandler` is a ready base. |
| 21 | §0.3: `SendServerEntityMovementState` (BGNM:661), parser copied from BGNM:589-618 | STALE | BGNM:691; `HandleServerEntityStateAtClient` :591-623 now includes the CE-13 length guard (:601-608) and `ReadServerEntityState` (:632-648, public static). |
| 22 | §0.4: `MeshMeasurementTests.cs:56` declares `Driver` | STALE | :171. |
| 23 | §0.4: stray check `ServerMeshPackageBuilder.cs:225-245` | CONFIRMED (shift ≤3) | `FindStrayReferences` :226-248. |
| 24 | §0.13: `EntityMovementDataHandlers` at BGNM:108-109 | CONFIRMED | BGNM:108-109. |
| 25 | §0.9: `CreateMovementData` keys on `LocalTick` (LNGM:75) | WRONG for the code as merged | `LocalTick` is at LNGM:75 (CONFIRMED) but CE-12 does not key anything on it. |
| 26 | §0.9: NMEM dash set at :348-350, cleared only at :523 and :552 | STALE | Set :348-350 (CONFIRMED); cleared also by Consume :639. |
| 27 | §0.9: sender runs after the kit (`LiteNetLibManager.cs:221-226`) | CONFIRMED | — |
| 28 | Summary item 3 (sheet:51-57): BGNM:703-704, per-call clears BIEMF3D:1375-1378 | WRONG / STALE | Skip is at BGNM:735-736; per-call clears no longer exist; the adapter must consume. |
| 29 | q4: body `WriteServerState` always true KCB:308-316, WriteState through it :318-322, stamp :326-341 | CONFIRMED | — |
| 30 | q4: tests pin one-timestamp stamp `MeshKinematicCharacterBodyTests.cs:325-341, 364-381` | CONFIRMED | :324-341, :363-381. |
| 31 | q4: `MeshMovementHandlerSlots.cs:53-101` OnReplaced semantics | CONFIRMED | :53-102. |
| 32 | q4: BIEMF3D sets reliable at 1343-1368, NMEM 606-622 | CONFIRMED | — |
| 33 | q4: client applies IsTeleport without timestamp check BIEMF3D:1410-1416 | STALE | Now :1437-1443 (moved by CE-13). |
| 34 | q4: input dropped while waiting BIEMF3D:1480-1484, NMEM:720 | STALE | BIEMF3D:1507-1511 (also :1585-1588); NMEM:749-755 (also :809-811). |
| 35 | q4: `WaitClientTeleportConfirm` BIEMF3D:1721-1727, NMEM:918-924 | STALE | BIEMF3D:1754-1760; NMEM:949-955. |
| 36 | q4: steered puppets teleport through the kit, Ghosting:1952-1960 | CONFIRMED | Ghosting:1960. |
| 37 | q4: `MonsterSpawnArea.cs:136-138` Teleport before NetworkSpawn | CONFIRMED | :136, :138. |
| 38 | q4: `RttCalculator.cs:6` wall-clock ms | CONFIRMED | — |
| 39 | q4: `LNP:40-47` Subscribe queues a spawn | CONFIRMED | — |
| 40 | q4: LNGM:109-122 sync then AOI; BGNM:129-137 send after base | CONFIRMED / STALE shift | LNGM:109-122; BGNM:131-140. |

---

## 7. Hazards and missing pieces

1. **Forgotten consume = permanent reliable-to-all.** On this kit the adapter's sender pass (`Begin` at build start, `Record(inner)`
   after its one append-only-checked successful write, `ConsumeAll` after the fan-out of a completed build with no failed
   reliable send; measure mode never records/consumes) is mandatory. The 20-consecutive-reliable warning is the only alarm.
2. **Two writers for the body** unless the body gets a sender-owned state (2.3).
3. **Shared slots instance** (`For(manager)`), or every kinematic puppet is refused (§0.5).
4. **Re-entrancy at `LayerRemoved`/`OnReplaced`:** unregister synchronously in `OnReplaced`; only enqueue in `LayerRemoved`; the
   body unregisters before `TryRemove`.
5. **Stale flags behind a body:** a pending kit teleport at `Begin` stays in the kit handler and is replayed (stale) at `End`;
   consider consuming the restored kit handler's flags in `End` when it implements the interface, or let the adapter's first
   rewrap build send them deliberately (decide).
6. **Owner pairs:** the owner must receive one-shots (teleport confirm). Contract doc says Withhold blocks everything
   (`MeshSyncContracts.cs:99-100`); the decided rule says never withhold an owner pair, and never skip an owner pair on its
   first build (q4:196; reply:99, 105). Fix the contract text and the sheet's Withhold rows together. Even so, a Teleport between
   an ownership change and the owner's subscription is lost on any kit (q4:230; reply:120) — kit-side fix outside step 6.
7. **New-pair rule:** skipping a pair on its creation build while consuming loses the flag for that pair; acceptable only for
   AOI-created pairs (spawn written post-teleport). Monsters spawn with a pending teleport (MonsterSpawnArea:136-138).
8. **Compile dependency on 0006:** Mesh/ referencing `IEntityMovementServerStateFlags`/`EntityMovementServerStateFlagsPass` needs
   a kit with 0006; upstream has it on `develop` only (README:35-37). Decide whether the package states the requirement or
   finds the interface by name (q4:151).
9. **Pre-kit stamp lifetime** across server restarts (§6 row 7).
10. **`DefaultGridManagerComponent.Instance` may be null** in some setups; BGNM:135 would NRE there anyway, but Mesh/ code must
    guard like Puppets.cs:118.
11. **Payload size**: force appliers make kit payloads variable and unbounded; reliable records (always the ones with appliers on
    a dash) need the own-packet path.
12. **Test seams needed:** grid flag, clock/timestamp, transport, positions and log must be injectable; body Begin/End need a
    public seam for the full order test; the slots CAS race branch has no test and cannot get one single-threaded.
13. **Env keys:** all step-6 keys sit in `p0tools.PENDING_KEYS` (p0tools.py:70-74); they must move to the active table and into
    `regress.ENV_KEYS` (regress.py:69) when step 6 lands.
14. **Allow-list by exact type** vs subclass: decide; `Refusal` uses `is` (subclasses pass).
15. **`IMeshLog` lives in `MeshLayoutCoordinator.cs:30-35`** with undocumented members — reusable, but a shared logging port might
    warrant its own file if the sender depends on it.
