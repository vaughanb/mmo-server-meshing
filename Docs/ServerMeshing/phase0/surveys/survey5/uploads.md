# uploads

## Summary
I surveyed how a synthetic phantom upload (client to server msg 119) gets into the server and what the server checks. Read only; I changed no files.

**Frame and dispatch match the sheet.**
- The kit client writes `[packed ushort 119][packed long ts][packed uint objectId][payload]` at BGNM:632-635. The server registers the handler at BaseGameNetworkManager_FeatureHandlers.cs:76.
- The server handler is BGNM:579-587. It checks only two things: the object has a movement handler, and that handler's ConnectionId equals the event's connection id.

**Path into the server.**
- Each frame, LiteNetLibManager.Update (:159-167) runs the logic tick first, then `Server.Update`.
- `Server.Update` (LiteNetLibServer.cs:49-57) loops on `Transport.ServerReceive`, calls `OnServerReceive`, then TransportHandler.ReadPacket (:63-88). ReadPacket reads the packed type and invokes the handler inside a try/catch.
- The event's reader is a plain NetDataReader and is read synchronously, so a pooled writer and reader pair works:
  - `NetDataReader.SetSource(NetDataWriter)` (NetDataReader.cs:133-139) shares the writer's buffer and copies nothing.
  - The pair can be returned at the next `ServerReceive` call.
- Nothing in the kit recycles server readers. `AutoRecycle` defaults to false (LiteNetManager.cs:228) and no `Recycle()` call exists anywhere in Assets.

**Kit helpers can't write the payload on a server.**
- `ClientWriteSyncTransform3D` and `ClientWriteMovementInput3D` return early unless `Entity.IsOwnerClient` (EntityMovementFunctions.cs:78, :149). On a dedicated server that is always false.
- So the phantom payload classes must write the bytes themselves. `EntityMovementFunctions.GetCompressedAngle` (public) can be reused.
- The kit readers are public static and tests can call them: `ServerReadSyncTransformMessage3D` (:157-163) and `ReadMovementInputMessage3D` (:90-103).

**What the server validates (NotSecure, BIEMF3D:1542-1644).**
- Drops when it is also the owner client, or when the component is ServerAuthoritative.
- An IsTeleport (128) flag clears the teleport wait. While waiting, every upload is dropped without a log.
- Drops when `_acceptedPositionTimestamp > peerTimestamp`. Equal timestamps are accepted, so the kit does not need strictly rising ones.
- MovementState and ExtraMovementState are taken as sent; nothing validates them.
- Allowed distances are computed and added up but never enforced. The code says `// TODO: Speed hack detection` (:1612), and `GetMoveablePosition` (:1646) is never called.
- When `!CanMove()` (dead, DisallowMove, vending, warping), x and z are reverted and y is accepted.
- On a dedicated server it then sets the position and turns the character immediately.
- There is no grounded check and no snap on the server. `snapThreshold` (5 m) is only used client-side (:1422).
- The server's position afterwards is interpolated, not the raw upload: `UpdateInterpolate` (:488-493) re-lerps from old to new over `LogicUpdater.DeltaTimeF` (0.05 s), so the server trails by up to one tick.

**What the server validates (ServerAuthoritative, BIEMF3D:1462-1527).**
- The same teleport and timestamp gates apply. A `!CanMove()` input is dropped whole, and the accepted timestamp is not advanced.
- A changed position calls `SetMovePaths(pos, useNavMesh: !IsKeyMovement)`, so phantom inputs must set IsKeyMovement.
- The server then simulates movement with the CharacterController at its own speed, so speed is limited by construction.

**Timestamps.** On a server, `ServerTimestamp` is `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` (LNGM:66-74, RttCalculator.cs:6). The server never compares an upload's timestamp with its own clock, so the plan's 50 ms delay changes nothing on the server.

**What a phantom needs before uploads are accepted.**
- No EnterGame or ClientReady.
- A LiteNetLibPlayer must exist before `NetworkSpawn(obj, 0, id)`, or LNI:599 throws a NullReferenceException.
- After spawn, the movement handler is registered with that ConnectionId (BIEMF3D:222-226).
- The teleport wait starts cleared: the CE-9 edit resets it at spawn (BIEMF3D:240-241).
- No confirmed teleport is needed unless the server later teleports the phantom.

**Wire layouts, typical sizes.**
- NotSecure: 1 + 7 + objectId (1-3) + 18 = about 28 B. The 18 B payload is packed state (1), extra state (1), Vector3 (12), packed yaw (1-4).
- Input (moving, rotation changed): 1 + 7 + objectId + 19 B. The payload is inputState byte, packed state, extra state (omitted when stopped), Vector3 if PositionChanged, packed yaw if RotationChanged.

## Facts
- **Kit client upload frame (msg 119)** — `Assets/OpenMMORPG/Core/Scripts/Networking/BaseGameNetworkManager.cs:620-637`: SendClientEntityMovmentState returns immediately on a server (622-623), so a phantom cannot reuse it. The frame is the packed ushort 119 (1 B), then PutPackedLong(writeTimestamp), then PutPackedUInt(entity.ObjectId), then the raw payload. It goes out on channel 2 (MOVEMENT_DATA_CHANNEL, BaseGameEntity.cs:17), ReliableOrdered or Unreliable. The writers are internal statics in EntityMovementDataBuffers.cs:7-8. TestMap can see them because it shares Assembly-CSharp, but must not reuse them: the kit uses the same writers.
  > `TransportHandler.WritePacket(EntityMovementDataBuffers.StateMessageWriter, GameNetworkingConsts.EntityState);
...PutPackedLong(writeTimestamp);
...PutPackedUInt(entity.ObjectId);`
- **Server receive handler and registration** — `Assets/OpenMMORPG/Core/Scripts/Networking/BaseGameNetworkManager.cs:579-587 (registered at BaseGameNetworkManager_FeatureHandlers.cs:76)`: The only gates are that the object has a movement handler and that the handler's ConnectionId equals the event's connectionId. There is no IsReady, EnterGame or ServerUserHandlers check. EntityState = 119 is at GameNetworkingConsts.cs:25.
  > `if (!_entityMovementDataHandlers.TryGetValue(objectId, out IEntityMovementDataHandler dataHandler) || dataHandler.ConnectionId != messageHandler.ConnectionId)
    return;
dataHandler.ReadClientStateAtServer(peerTimestamp, reader);`
- **Inbound dispatch chain** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/LiteNetLibServer.cs:49-57, 90-101`: Update loops ServerReceive until it returns false, and each DataEvent is handled synchronously via ReadPacket(eventData.connectionId, eventData.reader). So a synthetic reader can be recycled on the next ServerReceive call, including the final call that returns false. OnServerReceive is public virtual and LiteNetLibManager.Server is a public getter (LiteNetLibManager.cs:16), which allows direct injection, but that skips the transport's ordering and connect bookkeeping.
  > `while (Transport.ServerReceive(out TransportEventData tempEventData))
{
    OnServerReceive(tempEventData);
}`
- **ReadPacket wraps handlers in try/catch** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/TransportHandler.cs:63-88`: Reads the packed ushort type, returns silently for unregistered types, and catches handler errors (logged with LogException), dropping only that message. A truncated phantom payload throws InvalidOperationException from NetDataReader.EnsureAvailable (NetDataReader.cs:98-115) and is logged here.
  > `ushort messageType = reader.GetPackedUShort();
...
catch (System.Exception ex)`
- **Frame order within one frame** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/LiteNetLibManager.cs:159-167`: The logic tick runs first: OnServerUpdate, which sends server movement state at BGNM:129-137. Then Server.Update drains inbound messages. Then entity ManagedUpdate runs (UpdateInterpolate). So an upload processed in frame N reaches observers at the next tick.
  > `if (IsServer || IsClient)
    _logicUpdater.Update();
if (IsServer)
    Server.Update();`
- **TransportEventData and the real server listener** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/Transports/LiteNetLib/LiteNetLibTransportServerEventListener.cs:23-29, 45-53`: A DataEvent carries the NetPacketReader as a plain NetDataReader (TransportEventData.cs:8-17, field `reader`). No code casts it back to NetPacketReader. Admission (23-29) compares the inner LiteNetLibTransport's own ServerPeersCount, because `this` is passed at LiteNetLibTransport.cs:98. A wrapper that counts phantoms therefore does not affect admission.
  > `type = ENetworkEvent.DataEvent,
connectionId = peer.Id,
reader = reader,`
- **No one recycles server readers** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Plugins/LiteNetLib/NetPacketReader.cs:37-42 (and LiteNetManager.cs:228, 472-476)`: AutoRecycle is a public field that defaults to false, so the manager never recycles after the callback. A grep finds no `.Recycle()` call and no `AutoRecycle =` in Assets. Kit readers are left to the GC. A phantom pool is its own matter.
  > `public void Recycle()
{
    if (_manager.AutoRecycle)
        return;
    RecycleInternal();
}`
- **Allocation-free synthetic reader** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Plugins/LiteNetLib/Utils/NetDataReader.cs:133-139, 167-173`: SetSource(NetDataWriter) shares the writer's byte[] and length with no copy, so a pooled (NetDataWriter, NetDataReader) pair per in-flight upload is enough. _dataSize is an end index, not a count.
  > `public void SetSource(NetDataWriter dataWriter)
{
    _data = dataWriter.Data;
    _position = 0;
    _offset = 0;
    _dataSize = dataWriter.Length;`
- **ServerTimestamp source** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/GameApi/LiteNetLibGameManager.cs:66-74 (RttCalculator.cs:6-7)`: On a server this is wall-clock UTC in Unix milliseconds. A real client uses PeerTimestamp, its estimate of the server clock from ping and pong. The server never compares an upload's timestamp with its own clock, only with the last accepted one.
  > `if (IsServer)
    return RttCalculator.LocalTimestamp;
...
public long LocalTimestamp { get => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }`
- **Packed encodings and byte counts** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/Extensions/NetDataWriterExtension.cs:156-165, 179-259 (reader: NetDataReaderExtension.cs:183-192)`: A packed long is two zigzag packed ints: the high 32 bits, then the low 32 bits. For a 2026 timestamp the high word is about 416, which zigzags to 832 and takes 2 B. The low word takes 5 B, or 4 B when its absolute value is at most 8,388,607, so the timestamp is usually 7 B. Packed uint sizes: up to 240 is 1 B, up to 2,287 is 2 B, up to 67,823 is 3 B, up to 16,777,215 is 4 B, larger is 5 B.
  > `PutPackedInt(writer, (int)(value >> 32));
PutPackedInt(writer, (int)(value & uint.MaxValue));`
- **NotSecure payload layout (what the kit client writes)** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/EntityMovementFunctions.cs:147-163, 232-240`: Payload: packed uint MovementState (17 = Forward|IsGrounded is 1 B; with IsTeleport 145 is still 1 B; adding IsDash 2048 makes it 2 B). Then 1 byte ExtraMovementState. Then a Vector3 of 3 little-endian float32 (12 B). Then a packed int of GetCompressedAngle(yaw) = round(yaw*1000): yaw in [0,360) zigzags to at most 720,000, so 4 B for yaw above 33.9 degrees, 3 B down to 1.14, 2 B down to 0.12. Typical payload 18 B, so a full frame is about 1+7+2+18 = 28 B for a 2-byte object id. The writer exits unless IsOwnerClient (149), so phantoms must hand-write these bytes.
  > `if (!movement.Entity.IsOwnerClient)
    return;
writer.PutPackedUInt((uint)movement.MovementState);
writer.Put((byte)movement.ExtraMovementState);
writer.PutVector3(movement.Entity.EntityTransform.position);
writer.PutPackedInt(GetCompressedAngle(movement.Entity.EntityTransform.eulerAngles.y));`
- **ServerAuthoritative input payload layout** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/EntityMovementFunctions.cs:76-103`: Payload: a byte EntityMovementInputState (IsKeyMovement=1, PositionChanged=2, RotationChanged=4, IsStopped=8, Other=16; EntityMovementInputState.cs:4-12). Then a packed uint MovementState (direction flags; Forward=1 is 1 B). Then 1 byte ExtraMovementState, omitted when IsStopped. Then a Vector3 (12 B) if PositionChanged. Then a packed int yaw (1-4 B) if RotationChanged. A moving key-movement frame with state 0x07 is 1+1+1+12+4 = 19 B; without a rotation change it is 15 B. Also guarded by IsOwnerClient (78), so phantoms must hand-write it.
  > `writer.Put((byte)inputState);
writer.PutPackedUInt((uint)movementInput.MovementState);
if (!inputState.Has(EntityMovementInputState.IsStopped))
    writer.Put((byte)movementInput.ExtraMovementState);`
- **WriteClientState: when a real client uploads and how reliably** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:1198-1283`: NotSecure (1203-1233) returns true on every tick, even when idle. It is Unreliable except jump, dash and teleport-confirm, which are ReliableOrdered. ServerAuthoritative (1235-1281) sends only when DifferInputEnoughToSend is true (EntityMovementInput.cs:147-174: position changed more than 0.01, yaw more than 0.01, stop, jump or teleport, extra state changed). It is reliable for point-click, stop, jump, dash and teleport-confirm. An idle input client sends nothing.
  > `if (movementSecure == MovementSecure.NotSecure && IsOwnerClient && !IsServer)
...
if (Entity.DifferInputEnoughToSend(_oldInput, _currentInput, out EntityMovementInputState inputState))`
- **ReadClientStateAtServer dispatch** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:1382-1393`: The instance's movementSecure picks the reader. A transform payload sent to a ServerAuthoritative instance, or an input payload sent to a NotSecure one, returns before reading anything and does nothing.
  > `case MovementSecure.NotSecure:
    ReadSyncTransformAtServer(peerTimestamp, reader);
...
case MovementSecure.ServerAuthoritative:
    ReadMovementInputAtServer(peerTimestamp, reader);`
- **ReadSyncTransformAtServer validation (NotSecure)** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:1542-1644`: Order of checks:
- returns if IsOwnerClient (1544), or if ServerAuthoritative (1549);
- reads the payload (1554); IsTeleport clears the teleport wait (1555-1559);
- returns silently while waiting (1560-1564), or when _acceptedPositionTimestamp > peerTimestamp (1565-1569); an equal timestamp is accepted;
- takes MovementState and ExtraMovementState verbatim (1574-1575), with no ValidateExtraMovementState;
- adds up the excess distance (1596-1600) but never acts on it (TODO at 1612);
- if !CanMove, reverts x and z (1601-1609);
- on a dedicated server (!IsClient), SetPosition(newPos) and TurnImmediately (1613-1619);
- jump and dash set accepted flags (1630-1638);
- stores the timestamp and interpolation endpoints (1640-1643).
GetMoveablePosition (1646-1651) is never called.
  > `if (_acceptedPositionTimestamp > peerTimestamp)
{
    // Invalid timestamp
    return;
}
...
// TODO: Speed hack detection
if (!IsClient)
{
    // Allow to move to the position
    EntityMovement.SetPosition(newPos);`
- **ReadMovementInputAtServer validation (ServerAuthoritative)** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:1462-1527`: Order of checks:
- returns if IsOwnerClient, or if NotSecure;
- IsTeleport clears the wait; returns while waiting;
- if !CanMove, returns before the timestamp check, so the accepted timestamp is not advanced (1485-1489);
- returns when ts < accepted (1490-1494);
- sets _tempMovementState and _simulatingKeyMovement from IsKeyMovement (1500);
- if PositionChanged, SetMovePaths(position, !_simulatingKeyMovement) (1504). Without IsKeyMovement this runs NavMesh.SamplePosition and CalculatePath and allocates NavMeshPath and Queue (1128-1140);
- if RotationChanged, turns immediately on a dedicated server;
- IsStopped calls StopMoveFunction.
The server then moves the body itself with physics (UpdateGenericMovement 598-1050). Speed is GetMoveSpeed times a direction rate: 0.75 backward or diagonal-backward, chosen from the angle between the move direction and the transform's forward. So yaw must match the heading.
  > `if (!Entity.CanMove())
{
    // It can't move, so don't move
    return;
}
if (_acceptedPositionTimestamp > peerTimestamp)`
- **Server-side position after an upload is interpolated** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:468-494 (called from CharacterControllerEntityMovement.cs:396-397)`: A client-owned NotSecure character on the server has CanSimulateMovement false (244-253), so ManagedUpdate calls UpdateInterpolate. That lerps from oldPos to newPos over LogicUpdater.DeltaTimeF (0.05 s at updateFps 20) and overrides the SetPosition from the upload. Net effect: the server trails the uploads by up to one tick. AfterMovementUpdate does not overwrite MovementState here because CanPredictMovement is false (1064, 1666-1669), so the uploaded state, including IsGrounded, is what observers get.
  > `if (_acceptedPositionTimestamp > 0)
{
    _interpElapsedTime += deltaTime;
    Vector3 newPosition = Vector3.Lerp(_startInterpPosition, _endInterpPosition, _interpElapsedTime / Entity.Manager.LogicUpdater.DeltaTimeF);
    EntityMovement.SetPosition(newPosition);`
- **Teleport-confirm wait: when it is set and how it is cleared** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs:233-242, 367-383, 205-213, 1653-1664`: A server Teleport on a client-owned character sets the wait (376-377). The CE-9 edit in OnSetOwnerClient clears both the accepted timestamp and the wait (240-241); NetworkSpawn calls it (LiteNetLibAssets.cs:469), so a fresh phantom needs no confirm. Real clients send IsTeleport on their first upload (EntityStart sets _isClientConfirmingTeleport at 207) and after every server teleport (1660-1661). The wait can be read with BaseGameEntity.IsWaitingClientTeleportConfirm() (BaseGameEntity_MoveFunctions.cs:453-456).
  > `if (IsServer && !IsOwnerClientOrOwnedByServer)
    _isServerWaitingTeleportConfirm = true;
...
// MESH-EDIT: CE-9 ...
_acceptedPositionTimestamp = 0;
_isServerWaitingTeleportConfirm = false;`
- **movementSecure is public but copied once at Awake** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/EntityMovementSystems/CharacterControllerEntityMovement.cs:23, 178, 230-233, 292-303, 343-345`: CCEM.movementSecure (23) and BIEMF3D.movementSecure (BIEMF3D:20) are public fields, and Functions is a public getter (178). In a player build the value is copied into Functions once, in Awake (233). Only the Editor re-copies every frame (345, inside #if UNITY_EDITOR). CharacterController.enabled = CanSimulateMovement() is set only at OnEnable (301) and OnSetOwnerClient (295). GetObjectInstance instantiates the prefab, so Awake has already run (LiteNetLibAssets.cs:318-339). To switch to ServerAuthoritative, set both cc.movementSecure and cc.Functions.movementSecure after GetObjectInstance and before NetworkSpawn. The demo character uses CCEM with movementSecure 0 (BaseCharacter-CE.prefab:2505-2510).
  > `Functions = new BuiltInEntityMovementFunctions3D(Entity, CacheAnimator, this)
{
    stoppingDistance = stoppingDistance,
    movementSecure = movementSecure,`
- **A Player must exist before an owned spawn** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/GameApi/LiteNetLibIdentity.cs:568-609 (esp. 597-601), 243-261`: Initial dereferences Player (TryGetPlayer(ConnectionId)) for any connectionId of 0 or more, so a missing Player throws a NullReferenceException. It also self-subscribes the owner. IsOwnerClient needs IsClient, so it is false on a dedicated server, and the IsOwnerClient early returns in both readers never fire.
  > `if (IsServer && ConnectionId >= 0)
{
    Player.SpawnedObjects.Add(ObjectId, this);
    Player.Subscribe(ObjectId);
}`
- **Player creation on connect; pings to phantoms** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/GameApi/LiteNetLibGameManager.cs:437-442, 113-118, 838-843, 1013-1029`: A ConnectEvent through OnServerReceive adds the id to ConnectionIds (LiteNetLibServer.cs:96) and creates the LiteNetLibPlayer. The server pings every connection once a second (pingDuration 1, line 35), phantoms included. Phantoms never answer, so player.Rtt stays 0. A real client also sends its own 1 Hz Ping and gets a Pong back. SetPlayerReady is the kit path to IsReady; the phantom binder sets IsReady directly instead.
  > `if (!Players.ContainsKey(connectionId))
    Players.Add(connectionId, new LiteNetLibPlayer(this, connectionId));`
- **CanMove gates position application** — `Assets/OpenMMORPG/Core/Scripts/Gameplay/CharacterEntity/BaseCharacterEntity_MoveFunctions.cs:101-108 (player: BasePlayerCharacterEntity_MoveFunctions.cs:9-16)`: Dead (CurrentHp <= 0, DamageableEntityExtensions.cs:7), DisallowMove, vending or IsWarping blocks movement. For NotSecure x and z are frozen; for input the whole upload is dropped. The load generator sets CurrentHp = MaxHp after NetworkSpawn (ServerMeshLoadGenerator.cs:183-187), and phantoms must do the same and keep healing.
  > `if (this.IsDead())
    return false;
if (CachedData.DisallowMove)
    return false;`
- **LiteNetLibTransport ServerSend and GetServerRtt for unknown ids** — `Assets/OpenMMORPG/ThirdParty/LiteNetLibManager/Scripts/Transports/LiteNetLib/LiteNetLibTransport.cs:104-124, 155-158`: ServerSend returns false for an id that is not a live peer. GetServerRtt indexes _serverPeers directly and would throw KeyNotFoundException for a phantom id, though no code in Assets calls GetServerRtt today. ServerReceive polls and then dequeues one event per call.
  > `public long GetServerRtt(long connectionId)
{
    return _serverPeers[connectionId].RoundTripTime;
}`
- **Reusable feeder step helper** — `Assets/ServerMeshing/TestMap/Phase0/ServerMeshKinematicWalk.cs:81-96, 185-193`: StepWithin(position, ref heading, distance, area) is public static. It reflects off the area's edges and keeps y, which covers the 'at most speed x dt and inside the area' rule. The walk uses SpeedRate 0.9 times GetMoveSpeed. The demo player's base moveSpeed is 3 (BaseCharacter-CE.asset:43).
  > `public static Vector3 StepWithin(Vector3 position, ref Vector2 heading, float distance, Rect area)`
- **TrackSyntheticEntity location (sheet line is stale)** — `Assets/ServerMeshing/Mesh/MeshMapNetworkManager_Ghosting.cs:110`: The sheet cites Ghosting:106. It is internal, so TestMap (same Assembly-CSharp) can call it.
  > `internal void TrackSyntheticEntity(BaseCharacterEntity entity) => TrackOwned(entity);`
- **Phantoms are never handed off** — `Assets/ServerMeshing/Mesh/MeshMapNetworkManager.cs:843-861`: Handoff candidates come only from ServerUserHandlers.GetPlayerCharacters(). Phantoms never register, so a phantom crossing the edge triggers no handoff and just stays owned.
  > `foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())`
- **Kinematic body would drop uploads** — `Assets/ServerMeshing/Mesh/MeshKinematicCharacterBody.cs:68-69, 283-284`: A layer whose ConnectionId is -1 makes BGNM:584 drop the upload before ReadClientStateAtServer runs. Today the body is installed only on puppets (MeshMapNetworkManager_Puppets.cs:134), so phantoms are not affected. Step 6's wrapper must delegate ConnectionId for the same reason.
  > `public long ConnectionId => -1;`
- **Other is-LiteNetLibTransport sites (outside my area, lines checked)** — `Assets/ServerMeshing/Mesh/MeshMapNetworkManager_Ghosting.cs:168 (sheet: 164); MeshMapNetworkManager_Load.cs:259 (sheet: 210); MeshMapNetworkManager.cs:648-649 (sheet correct)`: These are the three `ServerTransport is LiteNetLibTransport` checks that a wrapper breaks. MeshMapNetworkManager.cs:648 enables Statistics in OnStartServer and would silently skip it under a wrapper. _Load.cs:210 is the registeredPlayers loop, not the transport check.
  > `if (ServerTransport is LiteNetLibManager.LiteNetLibTransport transport && transport.Server != null && transport.Server.Statistics != null)`
- **Message ids 65000/65001 are free** — `Assets/ServerMeshing/Mesh/MeshSleep.cs:36-44`: The highest mesh ids are 30010-30018 and no 6xxxx id is declared in Assets. 65000 packs to 3 B (249 prefix). The wrapper must peek the type at reader.Position and SetPosition back for anything other than the hello.
  > `public const ushort Sleep = 30010;`

## contradictions
- Sheet line 668 says TrackSyntheticEntity is at Ghosting:106; it is at MeshMapNetworkManager_Ghosting.cs:110.
- Sheet line 668 says the input row sets the instance's movementSecure to ServerAuthoritative and marks the field's accessibility as (E). The fields are public (CCEM:23, BIEMF3D:20; Functions getter CCEM:178). But setting CCEM.movementSecure after Awake does nothing in a player build: it is copied once at CCEM:233, and only the Editor re-copies it every frame (CCEM:345). CharacterController.enabled is computed only at OnEnable (CCEM:301) and OnSetOwnerClient (CCEM:295). So the binder must set both cc.movementSecure and cc.Functions.movementSecure after GetObjectInstance and before NetworkSpawn. Setting it after spawn leaves the CharacterController disabled: GroundCheck is false, gravity accumulates, and the character sinks.
- Sheet line 674 asks for timestamp = ServerTimestamp minus a 50 ms delay, strictly rising. The kit accepts equal timestamps (`_acceptedPositionTimestamp > peerTimestamp` at BIEMF3D:1565 and :1490) and never compares upload timestamps with server time, so the 50 ms offset has no effect on the server. What matters is never going backwards, because a backwards upload is dropped silently. That argues for max(last+1, ServerTimestamp-50) to survive UtcNow stepping back.
- Sheet line 673 says the frame is written 'as the kit client writes it (BGNM:632-635)' and implies the kit's own payload code can be used. Both kit writers return early unless IsOwnerClient (EntityMovementFunctions.cs:78 and :149), which is always false on a server, so the payload classes must hand-write the layout. The kit readers (ServerReadSyncTransformMessage3D, ReadMovementInputMessage3D) are public and fine for the round-trip tests.
- Sheet line 630 describes the character kind as 20 Hz uploads. That holds only for the transform (NotSecure) payload: a real NotSecure client uploads every tick even when idle (BIEMF3D:1203-1233). An input client uploads only when its input changed (EntityMovementInput.cs:147-174), so an idle input phantom should send nothing, plus one reliable stop frame. MESH_PHANTOM_UPLOAD_HZ should be read with that in mind.
- Sheet line 699 tests that steps are at most speed x dt. Nothing on the server enforces that for NotSecure: the accumulators are unused and the code says 'TODO: Speed hack detection' at BIEMF3D:1612. It is a realism guard, not a requirement for acceptance. For input payloads the server walks at its own speed, so the feeder speed must be at or below the entity's speed times the forward rate, with yaw matching the heading, or the server falls behind.
- Sheet line 710's acceptance compares 'server positions track the feeder within 0.5 m at p95' against the live feeder. The server's position is interpolated over one tick after each upload (BIEMF3D:488-493), and uploads are applied after the logic tick (LiteNetLibManager.cs:161-164). Against the live feeder the gap reaches roughly speed x 0.1 s. That is 0.3 m at the base walk speed of 3 m/s, but sprinting or a faster feeder can break 0.5 m. Comparing against the last sent position, or the feeder position one tick earlier, avoids a false failure.
- Sheet line 643 cites MeshMapNetworkManager_Load.cs:210 and line 642 cites Ghosting.cs:164 as is-LiteNetLibTransport sites. The actual sites are _Load.cs:259 and Ghosting.cs:168. MeshMapNetworkManager.cs:648-649 is correct.

## risks
- If the server ever teleports a phantom, every later upload is dropped silently until one carries IsTeleport (128). Server teleports include arrival promotion (Ghosting:1190), a warp (MapNetworkManager_PlayerActivity.cs:47) and skill warps; the wait is set at BIEMF3D:376-377 and checked at :1560 and :1480. Mitigation: send IsTeleport on the first frame, as a kit client does, and whenever entity.IsWaitingClientTeleportConfirm() is true (BaseGameEntity_MoveFunctions.cs:453).
- A NotSecure payload without IsGrounded (16) gives observers a falling or airborne state, and CanSprint/CanWalk return false, because the server takes MovementState as sent and AfterMovementUpdate does not run for a client-owned NotSecure character (BIEMF3D:1574, 1064, 1666-1669). Send 17 (Forward|IsGrounded) while moving and 16 when idle, with ExtraMovementState None.
- A dead or disallowed phantom freezes in x and z (NotSecure) or has its whole input dropped (ServerAuthoritative). Monsters can kill phantoms, so heal them the way ServerMeshKinematicWalk.Think does (SyntheticPlayerBrain.HealWhenLow).
- Input payloads without IsKeyMovement make the server run NavMesh.SamplePosition and CalculatePath on every upload, with new NavMeshPath and Queue allocations (BIEMF3D:1128-1140). This skews the ServerAuthoritative CPU row and fails silently where there is no NavMesh. Even with IsKeyMovement, SetMovePaths allocates a Queue per upload (BIEMF3D:1144); that is kit garbage and will appear in gc0.
- Input payloads whose yaw differs from the heading get side or backward speed rates of 0.75 (BIEMF3D:812-931), because the server does not auto-turn a client-owned key-movement character (:653). The server then lags the feeder.
- The NotSecure server has the CharacterController disabled and does no ground snapping, so the uploaded y is used as is. The feeder must keep a valid ground height; StepWithin keeps y, which only works on flat ground.
- Calling NetworkSpawn(obj, 0, id) before the phantom's LiteNetLibPlayer exists throws a NullReferenceException at LiteNetLibIdentity.cs:599. The binder's TryGetPlayer wait is required, not optional.
- Pooled reader lifetime: a reader may only be reused after OnServerReceive returns. Recycle on the next ServerReceive call, including the one that returns false. Do not alias a writer that the feeder rewrites within the same drain.
- Wall-clock resolution: ServerTimestamp is DateTimeOffset.UtcNow in milliseconds. Under Unity Mono on Windows the granularity may be coarser than 1 ms (unverified estimate), and the clock can step back. Clamping per phantom to max(last+1, now-50) keeps timestamps non-decreasing.
- Fidelity gap: phantoms omit the client's 1 Hz Ping and Pong traffic (LNGM:128-134 client side, 830-843 server side), and the server's 1 Hz Ping to them is swallowed. That is small, but the Send-window counts include about 1 Ping per phantom per second that a sink peer would actually receive.

## step6_needs
- MeshKitMovementStateSource must delegate ConnectionId and ReadClientStateAtServer to the kit handler. Otherwise BGNM:584 drops phantom and real uploads, as it does for MeshKinematicCharacterBody's ConnectionId of -1. Add a test that an upload passing BGNM's header check reaches the wrapped kit handler.
- Phantom ServerSend must behave like LiteNetLib for the sender's ServerSendMessage(conn, 2, method, writer). It returns true for phantom ids and false for unknown ids. It throws TooBigPacketException for an unreliable payload above MTU minus 1 (LiteNetPeer.cs:636-640), so the sender's catch path runs. It records bytes per (packed message type, delivery), so the Sender window can be checked against the Send window.
- Observer anchor: phantoms are never in ServerUserHandlers, so the sender's TryGetPlayerCharacter fails. The fallback is the first owned object from LiteNetLibPlayer.GetSpawnedObjects (LiteNetLibPlayer.cs:113-116). That is the character for a character phantom and the eye identity for an eye phantom. The binder must set player.IsReady = true before NetworkSpawn so the interest manager subscribes them (BaseInterestManager.cs:30-37).
- Phantom ids (1,000,000 + k) never equal ClientConnectionId, which is -1 on a server (LNGM:474). So the host skip (BGNM:676-677), copied into the fanout, never drops phantom sends.
- Upload-driven positions reach observers one tick late. Uploads are applied in Server.Update after the logic tick (LiteNetLibManager.cs:161-164). WriteServerState reads EntityTransform.position (EntityMovementFunctions.cs:142), which is the interpolated position from UpdateInterpolate, not the raw upload. Puppet-error and owner-row comparisons must allow for that.
- Owner tier: for NotSecure the owner client ignores server positions unless at least snapThreshold (5 m) away (BIEMF3D:1422-1429). Owner records matter for teleport, jump and dash flags and for snapping. That supports the ownerHz tier, but urgent and teleport records must still reach the owner.
- WriteServerState returns false until _isStarted, which is set at EntityStart after the component's Start runs (BIEMF3D:1340-1341, 205-208). The first build after a phantom spawn may legitimately write nothing for that source.
- Flags from uploads: an uploaded IsJump or IsDash sets _acceptedJump or _acceptedDash. On the server, UpdateInterpolate then sets _sendingJump or _sendingDash (BIEMF3D:470-481), which makes the next WriteServerState reliable. Phantom feeders should leave these flags unset unless a row tests them, or the stuck-reliable warning and the CE-12 rows will be polluted.
- A phantom switched to ServerAuthoritative is simulated with full server physics (CanSimulateMovement true, CharacterController enabled). The sender's adapter must keep wrapping it like any BIEMF3D handler; its type is still on the default allow-list.