# critic

## Gaps answered
### In what exact order are the metered and phantom transports installed, relative to Awake, StartServer and the kit's own factory?
1) Scene 0 is BaseInit-CE (EditorBuildSettings.asset:9). It holds the MeshMapNetworkManager (script guid dfc19a1e…), so MeshMapNetworkManager.Awake (MeshMapNetworkManager.cs:95-99) runs first.
- Hook, when MESH_METER_SENDS is count or time:
  (a) Capture the kit's factory BEFORE adding anything: `BaseTransportFactory kit = TransportFactory ?? GetComponent<BaseTransportFactory>();`. This is the kit's own lookup (LiteNetLibManager.cs:126-127), run while no wrapper component exists yet. In the demo it returns null (MapNetworkManager-CE.prefab:141; no factory component is serialized).
  (b) AddComponent<MeshMeteredTransportFactory>(). Its Awake must be empty: it runs synchronously, before BGNM sets Singleton (BGNM:113).
  (c) Set metered.Inner = kit. A null Inner is resolved lazily in Build by AddComponent<LiteNetLibTransportFactory>(), which is exactly what LiteNetLibManager.cs:128-129 would do. Throw if the chain reaches itself.
  (d) Set TransportFactory = metered (setter at :44-48). Keep the MeshSendMeter in a field for LoadTick. Then call base.Awake.
- 'Before base.Awake' is harmless but not required. The factory is read only in PrepareTransportFactory (:100-132), which is called from PrepareServerTransport (:142-148, at StartServer :214) and PrepareClientTransport (:134-140). LiteNetLibManager has no Awake, and Start/InitTransportAndHandlers (:95-98, 150-157) never touch the factory.

2) AfterSceneLoad (after every Awake in scene 0): ServerMeshPhantomInstaller.AutoStart.
- Return unless MESH_PHANTOM parses.
- Refuse in the editor (Application.isEditor, as ServerMeshTimedAoiInstaller.cs:37-41 does).
- Require ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_MAP_SERVER) (ConfigReader.cs:167; ProcessArguments.cs:124). The sheet's 'IsServer already true' test can never fire here: IsServer is always false at AfterSceneLoad, so it cannot tell a map server from a client process.
- Find the manager with BaseGameNetworkManager.Singleton as MeshMapNetworkManager. Add a ServerMeshPhantomTransportFactory to mesh.gameObject.
- If mesh.TransportFactory is a MeshMeteredTransportFactory: phantom.Inner = metered.Inner (null resolves to the kit default); metered.Inner = phantom.
- Otherwise: phantom.Inner = mesh.TransportFactory (null resolves to the kit default); mesh.TransportFactory = phantom.
- Never resolve an Inner through GetComponent<BaseTransportFactory>() once a wrapper exists: it can return the wrapper itself, and Build then recurses.

3) MMOServerInstance.OnGameDataLoaded calls StartServers.
- StartServers awaits UniTask.NextFrame (MMOServerInstance.cs:831), and StartMapServer copies useWebSocket (:948).
- MapNetworkManager.StartServer (:955) → BGNM.StartServer runs InitPrefabs (BGNM:242-246) → LiteNetLibManager.StartServer → PrepareServerTransport (:214).
- The assigned field is kept, unless useWebSocket is on: then :113-123 silently replaces it with a websocket factory.
- metered.Build() returns new MeshMeteredTransport(phantom.Build() → new ServerMeshPhantomTransport(LiteNetLibTransportFactory.Build() → new LiteNetLibTransport("SampleConnectKey",16,16))).
- Then Server.Transport is set (:215). Server.StartServer forwards StartServer down to the inner NetManager (LiteNetLibTransport.cs:91-102). OnServerUpdate is re-added (:221-222), IsServer = true, OnStartServer (:226).
- MeshMapNetworkManager.OnStartServer (:623) calls base first. At :648-649 it enables statistics on Innermost<LiteNetLibTransport>(ServerTransport). Add an error line when an env key asked for a wrapper but the chain does not contain it (the websocket case).

4) The same factory's Build() also runs on every StartClient (:134-140).
- The editor receives every env key (ServerMeshPlayRunner.cs:266-271), so the editor's client transport gets wrapped. Build must return a fresh wrapper each call, forward every client member and meter nothing on the client side.
- Bots get no MESH_* key: launch_bot (regress.py:196-202) passes no env.

### How does a phantom connect event become a LiteNetLibPlayer, and how does the binder make it ready and the owner of a character or eye without EnterGame?
Connect chain:
- LiteNetLibServer.Update (LiteNetLibServer.cs:49-57) loops on Transport.ServerReceive.
- The phantom transport drains inner events first, then returns its queued {ConnectEvent, connectionId 1_000_000+k}.
- OnServerReceive (:94-98) runs ConnectionIds.Add and Manager.OnPeerConnected.
- That passes MeshMapNetworkManager.OnPeerConnected (:312-316, Stamp only), then BGNM.OnPeerConnected (BGNM:337-350: the IsTemporarilyClose kick and the component hooks), then LNGM.OnPeerConnected (LNGM:437-442), which does Players.Add(new LiteNetLibPlayer).

Binder (TestMap MonoBehaviour, polling in Update):
1. Wait for mesh.IsServer, OwnCell, the target cell, and mesh.IsServerReadyToInstantiateObjects() (BGNM:1384).
2. TryQueueConnect, paced by MESH_PHANTOM_ARRIVAL_PER_S.
3. Poll mesh.TryGetPlayer(id) (LNGM:142-145). Players is internal protected; TestMap must use TryGetPlayer or GetPlayers.
4. Set player.IsReady = true (public setter, LNP:11) BEFORE spawning, so NotifyNewObject subscribes the new object to everything in range (BaseInterestManager.cs:30-37).
5. Character kind: copy ServerMeshLoadGenerator.cs:166-187 (GetObjectInstance, SetupByMetaData, CloneTo), but spawn with mesh.Assets.NetworkSpawn(spawnObj, 0, id) (LiteNetLibAssets.cs:456-480).
   - Initial (LNI:597-603) dereferences Player (a NullReferenceException without step 3), self-subscribes the owner, and calls NotifyNewObject synchronously.
   - BIEMF3D.OnIdentityInitialize registers the movement handler (BIEMF3D:222-226). Its ConnectionId reads Entity.ConnectionId (:88).
   - Then ForceMakeCaches, FillEmptySlots, HP/MP, and mesh.TrackSyntheticEntity(entity) (MeshMapNetworkManager_Ghosting.cs:110; internal, reachable because TestMap and Mesh share Assembly-CSharp).
   - Id 'phantom-{cell}-{k}'. No RegisterPlayerCharacter, no PlayerCharacterDataUpdater, DoNotDestroyWhenDisconnect left false.
   - Input payload: set both ccem.movementSecure and ccem.Functions.movementSecure after GetObjectInstance and before NetworkSpawn (CCEM:23, 178; Awake copies the value once at :233; the editor re-copies it every frame at :343-345; OnSetOwnerClient at :292-296 recomputes CharacterController.enabled).
6. Eye kind: see the eye answer.

The kit's SetPlayerReady (LNGM:1013-1030) does only two things: IsReady = true, then DeserializeClientReadyData (the database spawn path). The binder replaces both.

Unbind (QueueDisconnect or a kit kick): the DisconnectEvent passes MeshMapNetworkManager.OnPeerDisconnected (:682-698), then MapNetworkManager.OnPeerDisconnected (MapNetworkManager.cs:323-328; SaveAndDespawnPlayerCharacter takes its not-registered branch, MapNetworkManager_PlayerDespawning.cs:16-102), then BGNM:352-364, then LNGM:444-452 (ClearSubscribing and DestroyObjectsWhenDisconnect, LNP:83-96). NetworkDestroy then fires onObjectDestroy, which runs UntrackOwned (Ghosting:597-601).

Pooling: BaseCharacter-CE has poolingSize 0 (prefab:2616), and limitByPoolingSize defaults to true (LiteNetLibAssets.cs:58; MapNetworkManager-CE.prefab does not override it). So PushInstanceBack destroys the instance rather than pooling it (:354-356). A ServerAuthoritative movementSecure therefore cannot leak to a later real player under demo defaults. Restore NotSecure before unbind anyway.

### How are uploads injected through the normal inbound dispatch, and does the server's movement read path accept them?
Building the frame:
- The feeder writes each due phantom's frame into its own pooled writer: TransportHandler.WritePacket(w, 119) (public static, TransportHandler.cs:90-96), PutPackedLong(ts), PutPackedUInt(objectId), then the payload.
- The payload must be hand-written: the kit writers return unless IsOwnerClient (EntityMovementFunctions.cs:78, 149), which is always false on a server.
- Transform payload, copying ClientWriteSyncTransform3D (:147-155): packed uint MovementState (17 = Forward|IsGrounded while moving, 16 idle), byte extra 0, Vector3, packed int GetCompressedAngle(yaw).
- Do NOT reuse MeshKinematicCharacterBody.WriteKitTransform: its trailing Put(0) (:113-122) is the server's force-applier count.
- Input payload: copy ClientWriteMovementInput3D (:76-103) and always set IsKeyMovement. Without it, SetMovePaths runs NavMesh sampling and pathing and allocates (BIEMF3D:1504, 1128-1140).

Injection:
- QueueUpload copies the frame into the transport's pool.
- ServerReceive returns a DataEvent with this phantom's connectionId and a pooled NetDataReader pointed at the frame by SetSource(writer) (NetDataReader.cs:133-139, no copy).
- The previous pair is recycled at the start of the next ServerReceive call, including the call that returns false. Events are consumed synchronously (LiteNetLibServer.cs:53-56), and no kit code recycles or casts readers.

Dispatch:
- OnServerReceive hands the DataEvent to TransportHandler.ReadPacket (TransportHandler.cs:63-88): the packed type, then the handler registered at BaseGameNetworkManager_FeatureHandlers.cs:76.
- HandleClientEntityStateAtServer (BGNM:579-587) reads ts and objectId, and drops the frame unless handler.ConnectionId equals the event's connection.
- It then calls ReadClientStateAtServer (BIEMF3D:1382-1393) and ReadSyncTransformAtServer (:1542-1644).

That path rejects only four things:
- uploads while it waits for a teleport confirm, unless the upload carries IsTeleport (:1555-1564);
- a timestamp older than the accepted one (:1565; an equal timestamp passes);
- x and z while !CanMove (dead, DisallowMove, vending, warping; :1601-1609);
- the wrong secure mode for the payload.
The distance accumulators are never enforced ('TODO: Speed hack detection', :1612), and MovementState is taken verbatim. UpdateInterpolate then lerps toward the upload over one tick (:468-494).

Timing:
- LiteNetLibManager.Update runs the logic tick before Server.Update (:159-167), so a frame drained in frame N applies after frame N's tick. Real clients behave the same.
- Give the feeder a negative [DefaultExecutionOrder] so its frames drain in the same frame.
- Timestamp = max(last + 1, ServerTimestamp − 50) per phantom. ServerTimestamp is UtcNow in ms (LNGM:66-74), and the server never compares it with its own clock.
- No teleport confirm is needed at spawn: CE-9 resets the wait at OnSetOwnerClient (BIEMF3D:237-241). After any server Teleport, send IsTeleport while entity.IsWaitingClientTeleportConfirm() is true (BaseGameEntity_MoveFunctions.cs:453).

### How does the eye stay hidden from everyone but its owner, while still making the kit send it state for the objects it observes?
Use a runtime GameObject; no prefab or build window is needed.
- Steps: new GameObject; AddComponent<LiteNetLibIdentity>; AddComponent<ServerMeshPhantomEye> (a plain follower MonoBehaviour); identity.SetIsHide(eyeComponent, true) BEFORE mesh.Assets.NetworkSpawn(identity, 0, id).
- NetworkSpawn(LiteNetLibIdentity) does not check prefab registration (LiteNetLibAssets.cs:456; only the hash overload at :445-454 does). LNI has no Awake, and the empty AssetId only matters if a real client were ever sent the spawn.

Why it is hidden:
- Subscribe calls ShouldSubscribe (BaseInterestManager.cs:61-68), which calls eye.IsHideFrom(subscriber) (LNI:815-860).
- The owner check (:822-826) runs first, so the owner always sees its eye.
- In the ForceHide loop, BGNM.IsHideEntity returns false because the eye has no BaseGameEntity (BGNM:153-159).
- IsHide is true (HideObjects is non-empty, LNI:179), so the eye is hidden.
- HideExceptions must stay empty. A kit quirk at LNI:845 tests the target's OWN ConnectionId, so adding the owner's id there would unhide the eye from everyone.
- Nothing else in the kit uses SetIsHide (grep), and it costs nothing per pair. The sheet's static ForceHideFunctions delegate (LNI:26) runs on every IsHideFrom call for every pair, inflates AOI timing, and must be removed at stop.
- The hide must be in place before the spawn: Initial → NotifyNewObject (LNI:603; BaseInterestManager.cs:38-53) subscribes the other ready players synchronously.

Why it still observes:
- The eye belongs to a ready player, so JGAOI puts it in the grid (JobifiedGridSpatialPartitioningAOI.cs:183-201).
- Each target queries with its OWN visible range (:209-219), then ShouldSubscribe(eye, target) runs and the eye's UpdateSubscribings is called (:301-335; LNI:769-796), which calls LiteNetLibPlayer.Subscribe (identity.AddSubscriber plus a spawn sync state, LNP:40-48).
- The kit's SendServerEntityMovementState walks every Players entry's GetSubscribingObjectIds with no IsReady gate (BGNM:674-765), so the owner gets EntityState for each subscribed mover.
- SyncBaseLine and SyncDelta go to ready players (StateSyncing.cs:381, 398).
- The eye has no movement handler, so no EntityState about the eye is ever sent. Eyes never see each other.
- Caveat: IsHideEntity does not apply to a bare identity, so an eye also sees stealthed or ForceHide entities (GameEntityExtensions.cs:112-115).

### What would the add-on's handoff, ghosting, sleep and player-count code do with phantom characters?
Handoff:
- None. MeshBoundaryTick walks only registered characters (MeshMapNetworkManager.cs:843). MonsterHandoffTick takes only BaseMonsterCharacterEntity (Ghosting:2485-2505).
- A phantom past the cut stays owned by the wrong cell. Keep the layout inside the cell.

Ghosting:
- TrackSyntheticEntity puts the character in _ownedCharacters, keyed by its character Id (Ghosting:564-570, 608-622).
- Inside ghostBandWidth of a shared edge it streams as GhostKind.Player with characterId = Id (Ghosting:806-807). Ids must be unique across cells: 'phantom-{cell}-{k}'.
- A phantom destroyed mid-run is untracked (onObjectDestroy → UntrackOwned, Ghosting:597-601), but SendGhostRemoveToAll runs only on registered unregistration (Ghosting:1256-1264), so the neighbour's puppet lingers until its stale pass.
- Keep groups at least ghostBandWidth + r inside the cell unless the band is under test.

Load report:
- Phantom characters count in Work window 'owned players' and the OwnedPlayers occupancy layer (Load.cs:219-231), and in ownedOutside when they wander. That feeds the Dynamic coordinator.
- registeredPlayers (Load.cs:210-211) and the Clients layer (:244-251) exclude them.
- Run phantom grids on Static maps, or accept that phantoms drive splits.

Sleep:
- The cell refuses while Players.Count > 0 (MeshMapNetworkManager.cs:1047), phantoms included.
- The central's wanted set counts only registered users (MapNetworkManager.cs:183 → MeshCentralNetworkManager.cs:308). With MESH_SLEEP on, a phantom-only cell is told to sleep every 10 s and logs a refusal each time.

Disconnect hooks:
- GhostingOnPeerDisconnected returns at once, because there is no userId (Ghosting:1201-1204).
- The dead-entity unregister branch is skipped, because nothing is registered (MeshMapNetworkManager.cs:686-693).
- The kit's save is skipped, and no DataUpdater exists.

Stats:
- Mesh stats players= excludes phantoms (Ghosting:152-153).
- owned= includes phantom characters but not eyes.
- clients= (Ghosting:170) is LiteNetLib ConnectedPeersCount: real and sink peers count, virtual phantoms do not.
- clientOut excludes every swallowed send.

The kit:
- No idle or never-entered-game kick exists (every KickClient caller is SetPlayerReadyRoutine, GM commands or IsTemporarilyClose).
- The 1 Hz Ping goes to every ConnectionIds entry (LNGM:578-583) and is swallowed; a missing Pong is never punished.
- The instance-map quit (MapNetworkManager.cs:169-174) applies to instance maps only.

### What do the Send window and Phantom window lines measure?
Send window: one line per probe window, built from the meter inside MeshMeteredTransport, the outermost wrapper. It sees every kit ServerSend, phantom ids included, before they are swallowed.
- Per packed message type (the top six by bytes, plus 'other'): calls/s and kB/s.
- Bytes = writer.Length: the application payload including the packed type. It excludes LiteNetLib headers, merging, acks and pings.
- µs/call in time mode only: the time spent inside the inner ServerSend.
  - For real peers that is LiteNetLib's SendInternal: pool, copy, lock, enqueue (LiteNetPeer.cs:605-690).
  - For a virtual phantom it is just the swallow, so the figure means something only with real or sink peers.
  - It never includes the kit's packet building, which happens before ServerSend, or the network thread's merge and socket work.
- Refused sends: the inner returned false. Today this is invisible, because LiteNetLibServer.SendMessage discards the bool (LiteNetLibServer.cs:114-117).
- Too-big throws.
- Per-delivery calls and bytes (5 slots; DeliveryMethod values 0-4, NetConstants.cs:6-33).
- Real datagrams/s: the delta of Innermost<LiteNetLibTransport>.Server.Statistics.PacketsSent. That counts every datagram on the socket for every peer: merged packets, acks and pings included, phantom sends excluded.
- Request sends can be sub-typed. A response is [1][requestId] with no request type (TransportHandler.cs:228-240).

Phantom window: one line per window from ServerMeshPhantomWindow.
- bound: phantoms whose object is spawned and alive.
- ready: player.IsReady.
- V_move mean, p95 and min: for a rotating 10% sample, the count of ids in player.GetSubscribingObjectIds() (public, LNP:30-33) that have an entry in mesh.EntityMovementDataHandlers (public, BGNM:109), minus the phantom's own object. UpdateSubscribings always adds the object itself (LNI:778).
- uploads/s: frames injected.
- feeder/rig ms/s: Stopwatch time spent in feeder, binder and sampler. That main-thread work sits inside the cell's measured frame and must be subtracted from the work figures.
- Add swallowed calls/s and kB/s per delivery, so that real sends = Send window minus swallowed.
- Add a sink-bound count, so that results can fill clients{real, phantom_virtual, phantom_sink} (p0results.py:148): real = clients= minus sink bound.

### How does the sink's hello/bind protocol relate to the kit's message ids?
Id space:
- On the game transport the kit uses GameMsgTypes 0-10 (GameMsgTypes.cs:5-16) and GameNetworkingConsts 100-122 (EntityState 119, GameNetworkingConsts.cs:25). Request subtypes 104-194 live inside the 0 and 1 envelopes.
- The mesh link uses 100-120 and MeshClusterMsg uses 30010-30018 (MeshSleep.cs:36-44), but those are other transports.
- 65000 and 65001 appear nowhere in Assets.
- An unconsumed hello would be dropped silently: ReadPacket ignores unregistered types (TransportHandler.cs:76-77).

Encoding:
- Packed ushort 65000 = F9 F4 F8 and 65001 = F9 F4 F9 (NetDataWriterExtension.cs:192-197).
- The decoder must reject a lead byte of 250 or more and values above 65535, which the kit truncates silently (NetDataReaderExtension.cs:196-224).

Protocol:
1. The sink peer (a NetManager with ChannelsCount 16, key 'SampleConnectKey' per LiteNetLibTransportFactory.cs:7-11; the default of 1 channel silently drops channel ≥1 traffic, NetPeer.cs:225-230) connects to the cell's map port.
2. LNGM creates a LiteNetLibPlayer for its real small id.
3. The peer sends [65000][int32 index] ReliableOrdered on channel 0.
4. The phantom transport's ServerReceive peeks the type of each inner DataEvent at reader.Position. Position and SetPosition are absolute: a NetPacketReader starts at UserDataOffset (NetPacketReader.cs:17-26), so restore the saved position, never 0. It reads the bytes from reader.RawData up to RawDataSize.
5. On a hello it reads the index, queues (connectionId, index) for the binder through a new TryDequeueHello member, and returns the next event instead.
6. The binder, once ready, binds an index below P: it spawns the character or eye owned by the REAL peer id and sets IsReady.
7. The binder replies with mesh.ServerSendMessage(conn, 0, ReliableOrdered, [65001][packed uint objectId][float x][float z]). That reply passes the meter as type 65001.
8. The sink then uploads [119][packed long ts][packed uint objectId][payload] on channel 2 Unreliable. Any per-entity rising timestamp works: the server only compares it with the last accepted one.

The sink ignores the kit's Ping (no kick follows) and counts received bytes per packed type for its Sink window. A hello that arrives before the binder is ready waits in the queue. Peer ids are recycled (LiteNetManager.cs:681-682), so clear the id→index map on disconnect.

### Which TooBigPacketException rule and MTU should the phantom transport mirror? The readers disagree.
The MTU is 1024, not the sinkrig reader's 1164.
- InitialMtu = PossibleMtu[0] = 1024 (NetConstants.cs:89-103).
- MtuDiscovery = false and MtuOverride = 0 (LiteNetManager.cs:238, 244), so SetMtu(0) keeps every peer at 1024 (LiteNetPeer.cs:314-328).

The throw rule (LiteNetPeer.cs:634-647): length + headerSize > mtu throws for every delivery except ReliableOrdered and ReliableUnordered.
- Unreliable has a 1-byte header (NetConstants.HeaderSize), so its limit is 1023.
- Sequenced and ReliableSequenced use the 4-byte ChanneledHeaderSize, so their limit is 1020.
- Reliable sends throw only above MaxFragmentsCount fragments (:647).
- A channel at or above ChannelsCount, or a peer that is not connected, returns silently (:611-612).

Mirror it with the public constants: NetConstants.InitialMtu − HeaderSize or ChanneledHeaderSize.

Where the kit catches it: BGNM:727 and :760 (the path used when the grid is disabled, which is the default: DefaultGridManagerComponent.cs:22) and StateSyncing.cs:492, 548 and 604. The job path rethrows with `throw ex` under the editor or a development build (BGNM:885-897, 929-942), but it is unreachable while the grid is disabled.

The metered ServerSend must record the throw and rethrow it with `throw;`. TooBigPacketException derives from ArgumentException and has a public (string) constructor (NetDebug.cs:13-17). Never log its type name, because the word 'Exception' is banned in log lines.

### Should the 'clients' count include phantoms, as the sheet asks (ServerTransport.ServerPeersCount)?
I recommend NO, and disagree with the sheet here.

Why:
- No kit or add-on code reads ITransport.ServerPeersCount today. Outside the transports, only the admission check uses it, and that check reads the INNER LiteNetLibTransport (LiteNetLibTransportServerEventListener.cs:25; `this` is passed at LiteNetLibTransport.cs:98).
- loadtest.py:11 computes per-client downlink as clientOut / clients. clientOut excludes swallowed phantom bytes, so counting phantoms in clients would make that figure meaningless.
- rigtools.py:192/288, loadtest.py:24 and p0cells WINDOW_MEANS all parse clients=.

What to do:
- Keep both sites as real wire peers: Ghosting.cs:170 and Load.cs:262 become Innermost<LiteNetLibTransport>(ServerTransport).Server.ConnectedPeersCount. With no env this is byte-identical, because Innermost of a bare LiteNetLibTransport is itself.
- Report phantoms in the Phantom window (bound virtual, bound sink).
- Let ServerMeshPhantomTransport.ServerPeersCount = inner + bound for whoever wants it.

If the user prefers the sheet's version, both sites must change together. Otherwise the Work window and Mesh stats disagree, and every Python parser and the per-client figures must subtract phantoms.

### Which processes and cells install and bind phantoms?
The env reaches every cell process and the editor:
- ServerMeshPlayRunner sets env.KEY lines in the editor (ServerMeshPlayRunner.cs:266-271), and the spawner's cells inherit them.
- Bots do not: regress.launch_bot passes no env (regress.py:196-202).

Installing:
- Install the phantom transport on every -startMapServer process. It is a cheap pass-through: one extra interface call per send and receive, plus an id check.
- Never install in the editor.
- Bind only on target cells, decided in the binder after OwnCell resolves. The load generator does the same (ServerMeshLoadGenerator.cs:86-101).

Cell selector:
- §0.16's MESH_PHANTOM `<virtual|sink>:<P>:<kind>` has no cell selector, so a ten-cell rig would bind P phantoms per cell.
- Recommend a grammar change, which is a user decision: `<virtual|sink>:<P>:<kind>:<cell[,cell]|all>`, or a cell@ suffix.
- Pin the grammar in ServerMeshPhantomSettingsTests and in the p0tools mirror.

The kind 'ship' and the payloads ship56 and ship28 need step 7. Refuse them with a warning until then. Refuse input with eye.

### What does MESH_PHANTOM_REGISTER do? §0.16 lists it, but the step body never defines it.
Registering a phantom means one of two calls.

mesh.RegisterPlayerCharacter:
- writes the social map (a null UserId throws, an empty one collides; MapNetworkManager.cs:276-285);
- sends UpdateMapUser Add to the central, then Online every second (BGNM:450-488 → MapNetworkManager.cs:230-239);
- inflates the central's currentUsers (MapNetworkManager.cs:183), which drives the channel limit and the sleep wanted set;
- makes the phantom a handoff candidate (MeshMapNetworkManager.cs:843). A handoff does a WaitAndSaveCharacter database write of a fake row (:941), against §0.11, and sends a Warp.

GameInstance.ServerUserHandlers.AddPlayerCharacter directly:
- refuses empty Id, UserId or CharacterName (DefaultServerUserHandlers.cs:35-43);
- still makes the phantom a handoff candidate;
- still triggers MarkOnlineCharacter every second.

Step 6 does not need registration: its anchor falls back to the first owned object (LNP:113-116). The grid/job path that needs registration (BGNM:792-793) is disabled.

Recommendation (user decision): move MESH_PHANTOM_REGISTER to DROPPED_KEYS, with the reason 'phantoms anchor through their owned object; registering sends central traffic and makes them handoff candidates'. The alternative is to implement it only through AddPlayerCharacter, with layouts kept handoffHysteresis inside the cell.

### How should V be defined and the layout built, given that the sheet's examples and the AOI rule disagree?
Inclusive range:
- The AOI range test includes the boundary (QuerySphereJob.cs:72 `<=`; BaseInterestManager.cs:67 `<=`), and R is the TARGET's range (GetVisibleRange, :56-59; defaultVisibleRange, which MESH_BAND sets at Load.cs:90-92).
- So groups exactly R + 2r apart can still see each other. Require a strict margin: nearest cross-group pair > R + ε, intra-group pairs ≤ R − ε.

V and self:
- V_move never counts the phantom's own object, so a character group of g phantoms gives V = g − 1 + filler.
- The sheet's (400,100) → 4×100 is off by one unless V is defined to include self. Pin the choice in tests, along with the remainder case (150,100).

Eyes:
- Eyes do not see each other. Any number of eyes can share one cluster of V movers, which is what makes P and V independent.

What else counts toward V:
- Monsters and NPCs in range have movement handlers, so the ±10% acceptance needs MESH_NO_MONSTERS=1 or a monster-aware expectation.

Filler:
- The sheet says 'filler comes from kinematic owned load', but the load generator places uniformly over the cell (ServerMeshLoadGenerator.cs:309-333) and cannot form per-group discs.
- Recommend that the binder spawns its own server-owned filler per group with the same data preparation, and hands it to a per-group ServerMeshKinematicWalk (Setup and AddWalking are public, ServerMeshKinematicWalk.cs:55-72) or to a SyntheticPlayerBrain (public) for steered filler.
- Area = the cell's scene rect shrunk by ghostBandWidth + r.

### What happens at StopServer?
Kit order:
- LiteNetLibManager.StopServer (:284-301) removes OnServerUpdate and sets IsServer = false.
- Server.StopServer (LiteNetLibServer.cs:80-86) calls Transport.StopServer, then OnStopServer.
- The OnStopServer chain is MeshMapNetworkManager.OnStopServer (:671-680), which calls base last, then LNGM.OnStopServer (LNGM:506-513): Players.Clear and Assets.Clear.

LiteNetLibTransport.StopServer just stops the NetManager and drops it, so real peers get NO DisconnectEvents.

The phantom transport should match:
- clear bound phantoms, pending synthetic events, hellos and pools;
- emit nothing;
- call inner.StopServer.

The binder detects !mesh.IsServer, drops every reference, and never reuses k. LiteNetLibServer never clears ConnectionIds (no Clear anywhere), so stale phantom ids survive a restart. Later pings to them return false, which counts as refused.

The meter persists across restarts, and its windows continue.

The eye needs no static cleanup when SetIsHide is used.

### Which env keys and Python tests must step 5 change?
Move all 8 keys now. The sheet defers this to step 8 (sheet line 872), which contradicts §0.6.
- The keys: MESH_METER_SENDS, MESH_PHANTOM, MESH_PHANTOM_VIEW, MESH_PHANTOM_ARRIVAL_PER_S, MESH_PHANTOM_UPLOAD_HZ, MESH_PHANTOM_PAYLOAD, MESH_PHANTOM_STEPS and MESH_PHANTOM_REGISTER (or drop REGISTER).
- From p0tools.PENDING_KEYS (p0tools.py:59-69) to ENV_REGISTRY (:27-52), and to regress.ENV_KEYS (Tools/ServerMeshing/regress.py:69-81, not Tools/regress.py:59-64).
- Otherwise request_play drops them silently (regress.py:216-219), regress --env refuses them, and rigrun.check_env (rigrun.py:190-209) refuses them. Step 5's own acceptance runs (abload.run_window, as in accept_step3.py) could not set them.

Update test_p0tools.py:
- the exact registry set (:250-254);
- the counts (10, 33, 1) → (18, 25, 1), or (17, 25, 2) if REGISTER is dropped (:265);
- the pending examples MESH_METER_SENDS and MESH_PHANTOM (:277-279) → still-pending keys.

test_rigrun.py:106-126 needs no change: it mocks PENDING_KEYS, and check_env tests pending before ENV_KEYS. Its example key just becomes stale wording.

Add p0tools parsers and tests for the Send, Phantom, Sink and Bot net window lines, and a mirror of the MESH_PHANTOM and MESH_METER_SENDS grammars.

### What is the thread model, and what does metering cost?
Thread model:
- Every ServerSend runs on the main thread. LogicUpdater ticks inside LiteNetLibManager.Update (:159-164), and PollEvents processes events on the calling thread (UnsyncedEvents false).
- Grep finds no SwitchToThreadPool, Task.Run or RunOnThreadPool in server code; only WebSocketClient, UniTask itself, the rig's thread-CPU sampler and MapSpawn use threads.
- KickClient's UniTask.Delay continues on the PlayerLoop.
- LiteNetLib's logic thread never calls ServerSend, and its Statistics are Interlocked (NetStatistics.cs).
- So a lock-free meter is safe. Store the constructing thread's ManagedThreadId and count off-thread calls in an Interlocked counter (offThread=N in the line) rather than corrupting the table.

Cost per send:
- count mode: one extra interface call, a 1-3 byte decode from writer.Data (internal buffer, no copy), a 64-slot open-addressed probe on type & 63, and a few long increments. Roughly tens of ns.
- time mode: adds two Stopwatch.GetTimestamp calls.
- At about 50k sends/s (400 phantoms × 20 Hz × 2 packets plus sync and pings) that is ≤ 1.5 ms/s.
- The window logic allocates only per window.
- Inject ticksPerSecond so tests do not depend on Stopwatch.Frequency.

### Where does the bot's 'Bot net window' go, and what does it read?
Location and reading:
- In ServerMeshAutoClient: poll every frame, or every 0.5 s, for MeshTransportUnwrap.Innermost<LiteNetLibTransport>(mesh.ClientTransport).Client.
- LiteNetLibTransport.StartClient builds a NEW NetManager on every connect and migration hop (LiteNetLibTransport.cs:49-60). When the instance changes, set EnableStatistics = true (the same pattern as CellMeshLink.cs:668-669) and restart the baseline.
- Every 5 s, log through Log() (ServerMeshAutoClient.cs:260): in and out kB/s, pkt/s, hops and RTT, in invariant culture.

Gating:
- Bots get no env, so gate the line with a command-line argument (e.g. -meshBotNetSeconds) passed in launch_bot args. Regress bot logs then stay unchanged by default.
- TestMap may use Mesh/ types (MeshTransportUnwrap).

## Hazards
- The metered factory can wrap itself. Copying the kit's fallback literally (LiteNetLibManager.cs:126-129) after MeshMeteredTransportFactory is on the manager GameObject makes GetComponent<BaseTransportFactory>() return the wrapper, and Build() recurses until the stack overflows. Fix: capture `TransportFactory ?? GetComponent<BaseTransportFactory>()` BEFORE adding any wrapper; a null Inner then means AddComponent<LiteNetLibTransportFactory>(); guard Inner == this and cycles. The phantom installer must take metered.Inner as its own Inner (the sheet's line 665 leaves this out), or LiteNetLibTransport drops out of the chain.
- useWebSocket silently drops every wrapper. MMOServerInstance.cs:948 copies the config onto the manager just before StartServer, and PrepareTransportFactory (:113-123) then replaces any factory that is not a websocket factory. Fix: in OnStartServer (MeshMapNetworkManager.cs:623), log an error when MESH_METER_SENDS or MESH_PHANTOM is set but the chain lacks MeshMeteredTransport or ServerMeshPhantomTransport.
- Statistics and client figures go dark under a wrapper. The three `ServerTransport is LiteNetLibTransport` tests (MeshMapNetworkManager.cs:648-649, MeshMapNetworkManager_Ghosting.cs:168 [sheet says :164], MeshMapNetworkManager_Load.cs:259 [sheet says :210, which is the registeredPlayers loop]) fail, so EnableStatistics is never set and clientOut, pkt/s and clientOutKBps read 0. Fix: Innermost<LiteNetLibTransport>, AND ServerMeshPhantomTransport must implement IMeshWrappedTransport (the sheet gives it only to MeshMeteredTransport). Leave the mesh-link casts as they are (CellMeshLink.cs:445, 652, 668-669; Ghosting.cs:222).
- Byte-identical default: with no env the only allowed change is the Innermost walk, which returns a bare LiteNetLibTransport unchanged. No factory is added, no Send window or Phantom line appears, and the Bot net window is gated by a command-line argument. The 'clients' semantics must not change. Do not switch to ServerTransport.ServerPeersCount (see gaps): loadtest.py:11 computes clientOut/clients, and clientOut excludes swallowed phantom bytes.
- Phantom ids colliding with real ids: LiteNetLib ids are small and recycled (LiteNetManager.cs:681-682; the cap is 1100 plus reject ids), so 1_000_000+k cannot collide in practice. Still, never reuse k, even across StopServer: LiteNetLibServer.ConnectionIds is never cleared (LiteNetLibServer.cs:80-86). Never forward a phantom id to LiteNetLibTransport.GetServerRtt, which throws KeyNotFoundException (:155-158). Defensive case: if an inner ConnectEvent carries a bound phantom id, emit that phantom's DisconnectEvent first and log an error. Otherwise LNGM.OnPeerConnected (437-442) hands the real peer the phantom's ready LiteNetLibPlayer and its objects. Cap phantoms so that inner peers + phantoms ≤ ServerMaxConnections (1100).
- Reliable sends piling up: virtual phantoms swallow everything (counted, returning true), so nothing queues. Sink peers are real, and the server keeps reliable packets until acked (window 64). A sink thread that stalls stops acking, the queues grow, and LiteNetLib disconnects it after DisconnectTimeout 5000 ms (LiteNetManager.cs:124). LNGM then destroys the bound character. Every manual-mode NetManager must run PollEvents and ManualUpdate well within 5 s, and unreliable sends and acks leave only at ManualUpdate.
- Kit checks against feeder steps: NotSecure has no speed check (BIEMF3D:1612 TODO). These do drop uploads silently: a timestamp below the accepted one (equal passes, :1565), a server teleport wait (:376-377, 1555-1564; the feeder must send IsTeleport while IsWaitingClientTeleportConfirm()), and death or DisallowMove (x and z frozen, :1601-1609). Monsters kill brainless phantoms, so heal with SyntheticPlayerBrain.HealWhenLow, or run MESH_NO_MONSTERS=1. Omitting IsGrounded (16) makes observers see the character airborne. Input payloads need IsKeyMovement, and a yaw that matches the heading (side and back moves run at 0.75 speed), or the server falls behind.
- Server positions trail by one tick. They are interpolated toward each upload over 0.05 s (BIEMF3D:468-494), and uploads are applied after that frame's logic tick (LiteNetLibManager.cs:161-164). For the 0.5 m p95 acceptance, compare against the last SENT position, not the live feeder; otherwise a 3 m/s walk already shows 0.3 m of lag.
- Saves, kicks and timeouts: unregistered phantoms are never saved (MapNetworkManager_PlayerDespawning.cs:16-102, the not-registered branch). Kicks come only from IsTemporarilyClose (BGNM:339-343) and the GM kick-all commands. KickClient sends a Disconnect, waits 500 ms, then calls ServerTransport.ServerDisconnect(id) (LNGM:428-435). The phantom transport must then queue one DisconnectEvent with an explicit DisconnectReason (DisconnectPeerCalled; QueueDisconnect uses RemoteConnectionClose; the default value reads as ConnectionFailed), and return false for later sends at once. If it returns false instead, BGNM:1733-1738 force-unregisters but leaves the Players entry. Phantoms never time out.
- Metering cost and correctness: the metered ServerSend must catch only TooBigPacketException, record it and rethrow with `throw;`, so the kit's catches (BGNM:727, 760; StateSyncing.cs:492, 548, 604) still run. No closures, no boxing, no allocation. An empty writer is counted as an unknown type, not thrown. A ushort decode must reject a lead byte of 250 or more and values above 65535; the kit's GetPackedUShort truncates (NetDataReaderExtension.cs:198). The 'Count-mode overhead below 1% on the canary' acceptance has no tool path: earlier steps used scratch scripts with abload.run_window.
- Thread safety: the meter and the phantom queues are touched only on the main thread, which holds today (no thread-pool sends in server code). Make it checkable by counting off-thread calls. In the sink, start every manual-mode NetManager on the main thread before handing it to a worker. Each Start subscribes a PausedSocketFix to Application.focusChanged and Application.quitting (PausedSocketFix.cs:16-26), and NetManager cannot turn that off (NetManager.cs:62, useSocketFix defaults to true). On quit, Stop() runs on the main thread and races the workers. Subscribe the sink's own quitting handler first, so it stops and joins the workers before the fix runs. Workers log nothing: they aggregate counters, and the main thread logs.
- StopServer: the phantom transport clears its state and emits no events, matching LiteNetLibTransport. LNGM.OnStopServer clears Players and Assets (LNGM:506-513). The binder must drop its references when IsServer goes false. If the sheet's ForceHideFunctions delegate were used instead of SetIsHide, it is static and would leak across plays; SetIsHide avoids that.
- Install timing and process identity: AfterSceneLoad always sees IsServer false, so 'if IsServer log an error' tests nothing. Gate on -startMapServer instead. MESH_PHANTOM reaches all ten cells plus the editor, so the grammar needs a cell selector, or every cell binds P phantoms.
- Eye hide ordering: SetIsHide must be in place before NetworkSpawn. NotifyNewObject subscribes nearby ready real bots inside the spawn call (LNI:603; BaseInterestManager.cs:38-53), and a bot would then get a spawn for an unknown asset hash. HideExceptions must stay empty (the LNI:845 quirk). Setting IsReady after the spawn skips the subscribe-to-existing loop, so the first view waits up to one AOI interval (1 s, JGAOI:18).
- Reserved words: every TestMap and Tests declaration, nested ones included, becomes a whole-word, case-sensitive reserved word for Mesh/ text, comments included (ServerMeshPackageBuilder.cs:226-248). Tests names are also reserved for TestMap. Mesh/ doc comments must never name the phantom classes or MeshFakeTransport, and must write 'mode' in lower case. Rename PhantomGroup to ServerMeshPhantomGroup (rule §0.5). Avoid nested helper types with generic names (Entry, Kind, State, Window). Today's non-prefixed reserved words: BandEnvironment, Callbacks, Driver (MeshMeasurementTests.cs:171, not :56), MeshFake*, MeshTickScheduleCopy (it already exists, so §0.15 calling it new is stale), Mode, Placement, SyntheticPlayerBrain, ZoneBoundaryDetector.
- Each MonoBehaviour needs its own file named after the class: MeshMeteredTransportFactory (the sheet folds it into MeshMeteredTransport.cs), plus the phantom factory, installer, binder, feeder, eye and sink. New .cs files and the TestMap/Phantoms folder need Unity-generated .meta files, so Unity must import them, and §0.1 allows no file change while a rig chain runs. Code, the import and the player build all happen in a build window, and the build must then be redeployed to B (remoteb deploy) for the sink.
- Testability: the kit's Logging dereferences LogManager.LoggerManager, which is set only by a BeforeSceneLoad hook (LogManager.cs:61-66; Logging.cs:16). It very likely throws NullReferenceException in EditMode. So the meter, the phantom transport, the layout, the settings and the feeder take IMeshLog (Mesh public; MeshFakeLog in Tests) or return error strings. EditMode never runs Awake, so factories must not set themselves up there. Tests must never call StartServer or StartClient on a real LiteNetLibTransport or NetManager, because the running rig holds the ports.
- Rig work inflates the measured cell cost: the feeder, binder and V_move sampler run on the cell's main thread inside the work meter's frame. Report them as rig ms/s so analysis can subtract them. Arrival needs pacing: each spawn's NotifyNewObject is O(spawned objects), so 400 binds in one frame make a spike. Default MESH_PHANTOM_ARRIVAL_PER_S to about 50/s and log it.
- Dynamic maps: phantom characters count as owned players in the occupancy grid (Load.cs:219-231), so they can drive coordinator splits. They are never handed off, so a re-cut strands them in the wrong cell. Run phantom grids on Static maps, or document this. Groups inside the ghost band double the cost onto the neighbour, and destroyed phantoms leave stale neighbour puppets.
- Sink acceptance compares unlike quantities: the server's Statistics.BytesSent counts every datagram for every peer, headers, acks and pings included (LiteNetManager.Socket.cs:646-650). Compare it with the sum of the sink NetManagers' Statistics.BytesReceived (enable EnableStatistics; it defaults to false, LiteNetManager.cs:201) in a run with only sink peers (request_play enabled=False, no bots). Do not compare it with payload bytes counted in OnNetworkReceive. Recycle readers or set AutoRecycle = true on the sink.
- Tooling dependency inversion: the 'Sink 50 on B' acceptance needs a sink launcher, but remoteb.py has none (its subcommands: remoteb.py:306). Its running() counts any kit-ce-project process (remoteb.py:77-79), so `start` refuses while a sink runs, and `stop` kills the sink. A sink process gets no kit file log (only server roles enable it, MMOServerInstance.cs:692-748), so pass -logFile <dir>\Logs\<name>.log for `fetch` to find it. The cell never logs its own port: have the binder log 'Phantoms: cell X port N mode M P n'.
- Sheet facts that are stale or wrong: Ghosting.cs:164 is now :168; Load.cs:210 is now :259/:262; TrackSyntheticEntity is at Ghosting:110, not :106; NetDataWriterExtension.cs:169-186 misses the 3-byte branch at :192-197; the TooBig rule also covers Sequenced and ReliableSequenced; 'the kit catches it' holds only on the path used when the grid is disabled; MapNetworkManager.cs:553-714 misses EnterGame (:450-539) and the end of SetPlayerReadyRoutine (:763), and the relates-data loads are at :765-847; movementSecure is not an (E): it is public on both CCEM and BIEMF3D, and both fields must be set; the eye prefab is unnecessary (NetworkSpawn(identity) does not check registration); GetServerRtt has no kit caller; IMeshStateTransport and IMeshStatePositions are not in MeshSyncContracts.cs, so step 6 creates them; regress.ENV_KEYS is at Tools/ServerMeshing/regress.py:69-81; the sinkrig reader's 'MTU 1164' is wrong (it is 1024); the reader that said test_rigrun must be updated is wrong (it mocks PENDING_KEYS).

## Plan
- `Assets/ServerMeshing/Mesh/MeshTransportUnwrap.cs (new)` — none (pure): Contents:
- `public interface IMeshWrappedTransport { ITransport Inner { get; } }`.
- `public static class MeshTransportUnwrap { public static T Innermost<T>(ITransport t) where T : class, ITransport }`: follows Inner at most 8 levels (cycle guard) and returns the deepest T, or null.

Document that MixTransport (MixTransport.cs:6-10) is opaque and returns null, exactly like today's `is` test.

EditMode tests (in MeshMeteredTransportTests):
- 0, 1 and 2 wrappers over a real `new LiteNetLibTransport("k",16,16)` (the constructor only allocates);
- metered over phantom over lnl;
- null input;
- a wrapper whose Inner is null;
- a self-referencing wrapper terminates.
- `Assets/ServerMeshing/Mesh/MeshSendMeter.cs (new; MeshSendWindow in the same file, or in MeshSendWindow.cs)` — none (pure); used by MeshMeteredTransport and by MeshMapNetworkManager_Load.cs LoadTick: API:
- `MeshSendMeter(long ticksPerSecond)`.
- `static bool TryReadMessageType(byte[] data, int offset, int end, out ushort type, out int typeBytes)`, plus a `(data, length)` overload. It rejects null, truncated forms, a lead byte ≥ 250, and values > 65535.
- `Record(ushort type, DeliveryMethod d, byte channel, int bytes, long elapsedTicks, bool refused)`.
- `RecordTooBig(ushort type, DeliveryMethod d, int bytes)`.
- `MeshSendWindow TakeWindow(double seconds)`, which allocates per window only.

Table: 64-slot open-addressed by type, plus 'other'. A 5-slot delivery array; an undefined delivery value goes to 'other'. A 16-slot channel array. Exact long totals for calls and bytes per type and delivery (step 6 needs them). Off-thread counter.

MeshSendWindow.ToLine(realDatagramsPerSecond) builds an invariant 'Send window 5.00 s: …' line:
- the top six types by bytes, then other, with calls/s, kB/s and µs/call (0 in count mode);
- refused, tooBig, per-delivery figures, real datagrams/s and offThread;
- no braces, no 'Exception', no NaN or negatives, and it must not start with 'Work window'.

EditMode tests (MeshSendMeterTests): golden decodes 0/119/240/241/2287/2288/65000/65535, cross-checked against PutPackedUShort; failure cases first; window reset; timed against count-only; the exact line under de-DE.
- `Assets/ServerMeshing/Mesh/MeshMeteredTransport.cs (new)` — wraps every ITransport member (ITransport.cs:8-25): `public sealed class MeshMeteredTransport : ITransport, IMeshWrappedTransport`, constructor `(ITransport inner, MeshSendMeter meter, bool timed)`. Throws ArgumentNullException naming inner or meter.

Forwards all 18 members unchanged, including IsReliableOnly (read at LiteNetLibSyncField.cs:88, 95). Meters only ServerSend:
- `long t0 = timed ? Stopwatch.GetTimestamp() : 0; bool ok; try { ok = inner.ServerSend(...); } catch (TooBigPacketException) { meter.RecordTooBig(...); throw; } meter.Record(type, d, ch, writer.Length, elapsed, !ok); return ok;`
- The type is decoded from writer.Data at offset 0: TransportHandler.WritePacket always resets the writer and writes the type first (TransportHandler.cs:90-96).
- Write System.Diagnostics.Stopwatch fully qualified.

EditMode tests (MeshMeteredTransportTests, with MeshFakeTransport):
- an 18-row forwarding table, plus a reflection guard that every ITransport method is covered;
- refused counted;
- the TooBig instance is rethrown as the same instance;
- client sends not metered;
- an empty writer goes to the unknown type.
- `Assets/ServerMeshing/Mesh/MeshMeteredTransportFactory.cs (new, own file because it is a MonoBehaviour)` — added in MeshMapNetworkManager.Awake (MeshMapNetworkManager.cs:95-99); Build() is called by LiteNetLibManager.PrepareServerTransport and PrepareClientTransport (:134-148): `public class MeshMeteredTransportFactory : BaseTransportFactory`:
- `public BaseTransportFactory Inner`, `MeshSendMeter Meter`, `bool Timed`.
- `public static BaseTransportFactory KitDefault(GameObject go)` → go.AddComponent<LiteNetLibTransportFactory>() (defaults: SampleConnectKey, 16/16). TestMap reuses it.
- `public static bool TryParseSetting(string value, out bool meter, out bool timed, out string warning)` for MESH_METER_SENDS: unset or off = no meter; count; time; anything else gives a warning and no meter. No member named Mode.
- Build(): resolve a null Inner with KitDefault; throw if Inner == this or the factory chain cycles; return a new MeshMeteredTransport(Inner.Build(), Meter, Timed). Build runs for client transports too, and that is harmless.
- Empty Awake.
- Doc comments must not name rig types.

EditMode tests:
- AddComponent on a HideAndDontSave GameObject;
- Build with no Inner adds exactly one LiteNetLibTransportFactory with the kit defaults;
- Build with Inner wraps its product;
- Inner == this throws;
- two Builds give distinct transports sharing one meter;
- the setting grammar.
- `Assets/ServerMeshing/Mesh/MeshMapNetworkManager.cs:95-99 (Awake)` — before base.Awake(): Read MESH_METER_SENDS through TryParseSetting and log once (a warning for a bad value). When on:
- capture `BaseTransportFactory kit = TransportFactory ?? GetComponent<BaseTransportFactory>()` BEFORE adding anything;
- AddComponent<MeshMeteredTransportFactory>();
- set Inner = kit, Meter = new MeshSendMeter(Stopwatch.Frequency), and Timed;
- set TransportFactory = metered (setter LiteNetLibManager.cs:44-48);
- keep `_sendMeter` for LoadTick.

With the key unset, no code path changes. Step 6 adds its MeshStateSender component in this same prologue.
- `Assets/ServerMeshing/Mesh/MeshMapNetworkManager.cs:648-649 (OnStartServer)` — after base.OnStartServer(), where the statistics enable is today: Replace the `is` test with `LiteNetLibTransport lnl = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(ServerTransport); if (lnl != null && lnl.Server != null) lnl.Server.EnableStatistics = true;`. StartServer builds a new NetManager every time (LiteNetLibTransport.cs:98), so this must run after every start, which it does here.

Add: if _sendMeter != null and ServerTransport is not a MeshMeteredTransport, LogError (useWebSocket replaced the factory). The phantom binder makes the same check for its own transport.

Baseline the real datagram counter for the Send window here.
- `Assets/ServerMeshing/Mesh/MeshMapNetworkManager_Ghosting.cs:167-171 (LogMeshStats)` — the clientBytesOut/clients block: Use Innermost<LiteNetLibTransport>(ServerTransport). Keep clients = lnl.Server.ConnectedPeersCount (real wire peers, sink included), so the line is byte-identical with no env and keeps its meaning for rigtools.py:192 and loadtest.py:24. Change this together with the Load.cs site. The line's current-culture formatting is unchanged.
- `Assets/ServerMeshing/Mesh/MeshMapNetworkManager_Load.cs:142-156 (LoadTick) and :258-263 (FillLoadRates)` — LoadTick: take the meter window right after the TakeTickWindow line (:144), BEFORE the retired-cell return at :150-151; log it after the Work ticks line and the optional bins line (:154-156). FillLoadRates: the transport block at :259-263.: Take the window even on a retired cell and discard it, so windows stay aligned; otherwise the next window spans two periods. Log 'Send window' only when _sendMeter != null. Real datagrams/s = delta of lnl.Server.Statistics.PacketsSent over the window.

p0tools.pair_work_lines ignores other lines (p0tools.py:322-359), so the extra line does not disturb the Work window/ticks/bins pairing.

FillLoadRates: Innermost; keep report.clients = ConnectedPeersCount. The MeshLoadReport wire format is frozen (§0.10), and the value stays identical with no env.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomSettings.cs (new)` — none (pure): Public static TryParse methods that return the value and an error string:
- MESH_PHANTOM `<virtual|sink>:<P>:<character|eye|ship>[:<cells>]` (the cell field is the proposed addition);
- VIEW ≥ 1;
- ARRIVAL_PER_S > 0 (default about 50);
- UPLOAD_HZ (default 20, range 1-60);
- PAYLOAD: transform | input | ship56 | ship28 (the ship values refused until step 7; input with eye refused);
- STEPS `<P1,P2,..>@<s>`;
- REGISTER (if kept).
Also the `-meshPhantomSink host:port:count[:portBase]` grammar. Warnings have braces turned into parentheses.

EditMode tests: ServerMeshPhantomSettingsTests, failure cases first.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomTransport.cs (new)` — built by ServerMeshPhantomTransportFactory.Build(); sits between MeshMeteredTransport and LiteNetLibTransport: `public sealed class ServerMeshPhantomTransport : ITransport, IMeshWrappedTransport`, constructor `(ITransport inner, IMeshLog log)`.

Ids and members:
- Ids are 1_000_000 + k, monotonic and never reused.
- TryQueueConnect(out long id): false when the server is not started, or inner peers + phantoms ≥ ServerMaxConnections.
- QueueUpload(long id, NetDataWriter frame) copies the frame into a pool.
- QueueDisconnect(long id) emits once, with Reason RemoteConnectionClose.
- IsPhantom(long id). TryDequeueHello(out long conn, out int index). Swallowed counters.

ServerReceive:
- Recycle the previous synthetic pair first.
- Drain inner events first. Peek each inner DataEvent for 65000 at reader.Position, using RawData and RawDataSize with an absolute position restore. Consume a hello; for an inner ConnectEvent carrying a bound phantom id, emit the phantom's disconnect first and log an error.
- Then deliver synthetic events in FIFO order.

ServerSend:
- For a bound phantom: mirror the TooBig rule (Unreliable above NetConstants.InitialMtu − HeaderSize = 1023; Sequenced and ReliableSequenced above InitialMtu − ChanneledHeaderSize = 1020), count per delivery, return true with no allocation.
- For an unbound id in the phantom range: return false.
- Otherwise forward to the inner.

Other members:
- ServerDisconnect(phantom) unbinds at once, queues a DisconnectPeerCalled event and returns true.
- GetServerRtt(phantom) returns 0 without touching the inner.
- ServerPeersCount = inner + bound.
- StopServer clears everything, emits nothing, then calls inner.StopServer.
- Client members, IsReliableOnly and ServerMaxConnections are forwarded.

EditMode tests: ServerMeshPhantomTransportTests (failure cases first, the oversize table, ordering, a hello read through an offset-4 reader, StopServer, Innermost through metered over phantom over lnl).
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomTransportFactory.cs and ServerMeshPhantomInstaller.cs (new, separate files)` — [RuntimeInitializeOnLoadMethod(AfterSceneLoad)], after MeshMapNetworkManager.Awake has run in scene 0 and before MMOServerInstance.StartServers (MMOServerInstance.cs:831): The factory is a BaseTransportFactory with an Inner (resolved with MeshMeteredTransportFactory.KitDefault) and a Build() that wraps.

The installer:
- returns unless MESH_PHANTOM parses;
- refuses in the editor;
- requires -startMapServer (ConfigReader.IsArgsProvided);
- requires Singleton to be a MeshMapNetworkManager;
- if the factory is a MeshMeteredTransportFactory, sets phantom.Inner = metered.Inner and metered.Inner = phantom; otherwise phantom.Inner = mesh.TransportFactory and mesh.TransportFactory = phantom;
- creates the binder GameObject (DontDestroyOnLoad) and logs one setting line.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomLayout.cs (new; ServerMeshPhantomGroup replaces PhantomGroup)` — none (pure); called by the binder once OwnCell is known: `Build(int phantoms, int view, float range, Rect area, int seed, kind, out string error)` → groups {centre, radius r ≤ R/2, phantom count, filler count, positions}.
- Strict margins: nearest cross-group pair > R + ε, intra-group pairs ≤ R − ε.
- Pin the V-includes-self decision and the remainder rule.
- Eye kind: P eyes share clusters of V movers.
- Area = the cell's scene rect shrunk by ghostBandWidth + r.
- Deterministic per seed.

EditMode tests: ServerMeshPhantomLayoutTests.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomUploads.cs, IServerMeshPhantomPayload.cs, ServerMeshPhantomTransformPayload.cs, ServerMeshPhantomInputPayload.cs (new)` — MonoBehaviour Update with a negative [DefaultExecutionOrder], so frames drain in the same frame's Server.Update (LiteNetLibManager.cs:159-167): Scheduler:
- A pure due-calculation (serverTimestampMs, frame time and a seeded phase as parameters): at most one upload per phantom per frame, Hz ± 1 per second, no burst after a stall.
- ts = max(last + 1, ServerTimestamp − 50).

Frame and payload:
- WritePacket(119), PutPackedLong, PutPackedUInt, payload.
- The transform payload copies ClientWriteSyncTransform3D (EntityMovementFunctions.cs:147-155): state 17 moving, 16 idle; extra 0.
- The input payload copies ClientWriteMovementInput3D (:76-103) with IsKeyMovement set; it sends nothing while idle except one reliable stop frame.
- Set IsTeleport while IsWaitingClientTeleportConfirm().

Motion: steps through ServerMeshKinematicWalk.StepWithin inside the group disc.

Timing: Stopwatch time goes to the window.

EditMode tests: ServerMeshPhantomUploadTests.
- The golden 23-byte frame '77 00 F7 E0 2A 11 00 00 00 C0 3F 00 00 20 C0 00 00 50 40 FA 20 BF 02'.
- The round trip through a copy of TransportHandler.cs:65 plus BGNM:582-583, then ServerReadSyncTransformMessage3D or ReadMovementInputMessage3D, ending at EndOfData.
- The scheduler at 1/30 s and 1/60 s, and with a 0.15 s stall.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomClients.cs (binder, new)` — MonoBehaviour Update. Waits for mesh.IsServer, OwnCell, the target cell and IsServerReadyToInstantiateObjects (BGNM:1384); finds the transport with MeshTransportUnwrap.Innermost<ServerMeshPhantomTransport>(mesh.ServerTransport) and logs an error if it is missing: Order per phantom: TryQueueConnect (paced) → poll mesh.TryGetPlayer(id) → player.IsReady = true → spawn.

Character spawn:
- data preparation copied from ServerMeshLoadGenerator.cs:166-187, with id 'phantom-{cell}-{k}';
- input payload: set ccem.movementSecure and ccem.Functions.movementSecure;
- NetworkSpawn(obj, 0, id), ForceMakeCaches, FillEmptySlots, HP/MP, TrackSyntheticEntity;
- heal with SyntheticPlayerBrain.HealWhenLow.

Eye spawn: runtime GameObject with LiteNetLibIdentity and ServerMeshPhantomEye; SetIsHide BEFORE NetworkSpawn(identity, 0, id).

Filler: server-owned characters per group, given to a per-group ServerMeshKinematicWalk (AddWalking) or to SyntheticPlayerBrain.

Sink: bind each TryDequeueHello index < P to the real connection id, then send the [65001][packed uint objectId][x][z] reply through mesh.ServerSendMessage(conn, 0, ReliableOrdered, w).

STEPS: QueueDisconnect the surplus phantoms. Reset all state when IsServer goes false. Log 'Phantoms: cell X port N mode … P n'. Exactly one owned object per phantom (step 6's anchor). No registration.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomEye.cs (new; replaces the sheet's prefab)` — LateUpdate: A plain MonoBehaviour that follows its host's transform position plus an offset. It has no LiteNetLibBehaviour, so the eye identity has no behaviours.

EditMode tests (ServerMeshPhantomEyeHideTests):
- two AddComponent identities, ConnectionId set through its internal setter by reflection (precedent: MeshKitEditTests.cs:11);
- after SetIsHide, a non-owner is hidden and the owner is visible (LNI:822);
- HideExceptions stays empty;
- DestroyImmediate in TearDown.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomWindow.cs (new)` — called by the binder once per probe window (5 s): A pure V_move sampler: it takes a HashSet<uint>.Enumerator (player.GetSubscribingObjectIds), the handler dictionary (mesh.EntityMovementDataHandlers) and the own object id, over a rotating 10% sample.

The invariant 'Phantom window' line: bound (virtual and sink), ready, V_move mean/p95/min, uploads/s, swallowed calls/s and kB/s, rig ms/s.

EditMode tests: the exact line under de-DE, and the sampler on fixture sets.
- `Assets/ServerMeshing/TestMap/Phantoms/ServerMeshPhantomSink.cs (new)` — AfterSceneLoad on -meshPhantomSink (with -batchmode -nographics, no server argument): Sockets and threads:
- Create all NetManagers (ChannelsCount 16, key SampleConnectKey, IPv6Enabled false, EnableStatistics true, AutoRecycle true) and call StartInManualMode on the MAIN thread.
- Subscribe its own Application.quitting handler before them, so it stops and joins the workers first.
- K workers of about 100 managers each loop PollEvents and ManualUpdate, paced well inside the 5 s DisconnectTimeout.

Protocol: hello [65000][int32 index] ReliableOrdered on channel 0; on [65001], uploads at Hz on channel 2.

Output: per-type received bytes and records, plus Statistics.BytesReceived, aggregated by the main thread into a 'Sink window' line. Use -logFile under Logs.

EditMode tests: only the pure parts (argument grammar, hello and bind encoding, line). The networking is rig-only.
- `Assets/ServerMeshing/TestMap/ServerMeshAutoClient.cs (edit)` — Start() (:213-219): add a coroutine or Update gated by a new argument such as -meshBotNetSeconds: Each frame, compare Innermost<LiteNetLibTransport>(mesh.ClientTransport).Client by reference. On a new instance, set EnableStatistics = true and reset the baseline: StartClient builds a new NetManager each hop (LiteNetLibTransport.cs:57).

Every N s, log 'Bot net window' (in and out kB/s, pkt/s, hops, RTT) through Log() (:260), in invariant culture. Off by default, so bot logs stay unchanged.
- `Assets/ServerMeshing/Tests/Editor/MeshFakeTransport.cs plus MeshSendMeterTests, MeshMeteredTransportTests, ServerMeshPhantomTransportTests, ServerMeshPhantomUploadTests, ServerMeshPhantomLayoutTests, ServerMeshPhantomSettingsTests, ServerMeshPhantomEyeHideTests, ServerMeshPhantomSinkTests, ServerMeshPhantomWindowTests (new)` — NUnit EditMode, namespace MultiplayerARPG.MMO.Mesh.Tests: MeshFakeTransport is an internal sealed class implementing all 18 members plus IMeshWrappedTransport:
- a call log;
- settable return values;
- queued server and client events;
- an optional throwOnServerSend.

Conventions:
- TestCaseSource with SetName, failure cases first.
- Assert.That constraints only; NUnit is 3.5 (com.unity.ext.nunit 2.0.5), so there is no Assert.Multiple.
- No nested helper types with generic names.
- Never start a real socket.
- Use MeshFakeLog for IMeshLog.
- `Tools/ServerMeshing/p0tools.py, regress.py:69-81, test_p0tools.py (edits)` — ENV_REGISTRY (p0tools.py:27-52), PENDING_KEYS (:59-69), ENV_KEYS (regress.py:69-81): Keys:
- Move the 8 step-5 keys (or 7, with REGISTER moved to DROPPED_KEYS) into ENV_REGISTRY, each with a one-line meaning, and into ENV_KEYS under a 'Phase 0 step 5' comment.
- Update test_p0tools.py :250-254 (the registry set), :265 (the counts) and :277-279 (the pending examples).
- test_rigrun needs nothing.

New parsers, each with valid, malformed and incomplete cases: parse_send_window, parse_phantom_window, parse_sink_window and parse_bot_net_window, plus a mirror of the MESH_PHANTOM grammar. Run `python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"`.
- `Tools/ServerMeshing/remoteb.py (minimal edit) or a scratchpad acceptance script` — a new 'sink start|stop' path reusing ssh(), the job-object escape and deploy: Step 5's sink acceptance cannot wait for step 8.
- Launch `<exe> -batchmode -nographics -meshPhantomSink A:port:50 -logFile <dir>\Logs\sink.log`.
- Distinguish the sink from cells in running() and stop: today both treat any kit-ce-project process as a cell (remoteb.py:77-79, 197-207).
- `fetch` already collects Logs/*.log.
- Deploy the new build to B first.

## Step 6 needs
- The full chain must already be in place: kit → MeshMeteredTransport → ServerMeshPhantomTransport → LiteNetLibTransport. Step 6's IMeshStateTransport adapter is then just mesh.ServerSendMessage(conn, 2, method, writer) (LiteNetLibManager.cs:362), and phantom observers cost exactly the kit-side build and fanout, with no network.
- A phantom ServerSend for any delivery must count and swallow, return true, and allocate nothing. TooBigPacketException must be emulated with LiteNetLib's real rule: Unreliable above 1023, Sequenced and ReliableSequenced above 1020, never for ReliableOrdered or ReliableUnordered (LiteNetPeer.cs:634-647; MTU 1024). The metered transport must rethrow the same instance, so the sender's catch runs.
- Exact integer totals per message type (119) and per delivery in each MeshSendWindow, plus refused and too-big counts. Those let 'MESH_SENDER=measure changes no bytes' and the ±2% records and packets acceptance be checked against the Send window.
- MeshTransportUnwrap.Innermost<LiteNetLibTransport> must see through both wrappers (the phantom transport implements IMeshWrappedTransport), for the Sender window's real datagrams/s and client kB/s. IsPhantom(id), or the Phantom window's bound counts, lets the Sender window split real and phantom clients.
- Phantoms must be ready entries in LNGM.Players, created by a synthetic ConnectEvent (LNGM:437-442). Each must own exactly one object, a character or an eye, spawned through NetworkSpawn(obj, 0, id). TryGetPlayerCharacter misses unregistered phantoms, so the sender's anchor falls back to the first owned object (LiteNetLibPlayer.GetSpawnedObjects, LNP:113-116). Their subscriptions fill through the AOI exactly as a real client's do, so identity.GetSubscribers() needs no phantom special case.
- Phantom character handlers must remain kit BIEMF3D handlers (through CCEM), so MeshKitMovementStateSource can wrap them. The adapter must delegate ConnectionId and ReadClientStateAtServer; otherwise HandleClientEntityStateAtServer (BGNM:584) drops every phantom and real upload, as it already does for a layer whose ConnectionId is -1 (MeshKinematicCharacterBody.cs:68-69).
- Upload-driven positions reach observers one tick late: uploads apply after the logic tick (LiteNetLibManager.cs:161-164), and WriteServerState writes the interpolated transform (BIEMF3D:468-494). Feeders must not set IsJump or IsDash unless a row tests them, because uploaded flags set _sendingJump and _sendingDash (BIEMF3D:470-481) and pollute the stuck-reliable warning. The CE-12 check must use a server-owned character, or the phantom feeder must answer teleport confirms (BIEMF3D:376-377).
- ServerMeshPhantomClients and the metered factory install alongside step 6's MeshStateSender, which is added in MeshMapNetworkManager.Awake (:95-99) before base.Awake, so that BGNM:123 collects it. The sender's own Awake runs before Singleton is set (BGNM:113) and must not touch the manager. BGNM.OnPeerConnected and OnPeerDisconnected notify ManagerComponents for phantoms too (BGNM:337-364).
- The kit arm iterates every Players entry with no IsReady gate (BGNM:674-765), and Players order (phantoms interleaved with bots) decides which observer receives the per-call flags. The sender's observer set must match it for byte identity. The grid stays disabled (BGNM:133; DefaultGridManagerComponent.cs:22), or unregistered phantoms get no kit EntityState (BGNM:792-793).
- Keys: the step-5 keys must already be in ENV_REGISTRY and regress.ENV_KEYS, so step 6 runs can combine MESH_PHANTOM with MESH_SENDER. The step-5 TestMap and Tests type names become reserved words for step 6's Mesh/Sync code, comments included. IMeshStateTransport and IMeshStatePositions are new types in MeshStateFanout.cs, not moved; MeshSyncContracts.cs never declared them. MeshFakeStateTransport stays separate from MeshFakeTransport.
- The IMeshLog injection and return-an-error-string pattern established in step 5 (the kit's Logging likely throws in EditMode) is the pattern step 6's MeshStateFanout and adapter tests follow. The Hex golden-byte helper and the 119 header layout carry over to MeshStatePacketTests (the server packet adds int32 count and {packed id, int32 len, bytes} records, BGNM:683-736).