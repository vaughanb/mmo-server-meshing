# Phase 0 step 5: transport chain, send meter, phantom clients (character and eye) and the sink (implementation contract)

Repository `C:\dev\Game Design\mmo-server-meshing`. The add-on is `Assets/ServerMeshing`:
- Mesh/ ships.
- TestMap/ is the rig.
- Tests/Editor holds NUnit tests in namespace `MultiplayerARPG.MMO.Mesh.Tests`. They compile into Assembly-CSharp-Editor with no InternalsVisibleTo, so everything a test calls must be public and outside the server `#if` guard.

Read first (read-only):
1. this contract;
2. the survey synthesis `C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\2cf88996-d970-455a-bb4e-fffe6bad122c\scratchpad\survey5\critic.md`. It is the authority on WHERE things are: gaps, hazards and plan, with current line numbers;
3. as needed, `verify.md`, `transport.md`, `binding.md`, `uploads.md`, `sinkrig.md` and `tests.md` beside it;
4. for intent, the build sheet's step 5 (`...\7cb7243d-...\scratchpad\phase0_build_sheet.md` lines 625-716), §0.15 (names, :197) and §0.16 (keys, :212).

Where they disagree: THIS CONTRACT, then critic.md, then the sheet.

## Rules (the step 2/3 rules all bind; see `step2_design.md` "Rules" and `step3_design.md` "Rules")

**Language and compatibility**
- C# 9, against the overlap of netstandard 2.1 and unity-4.8-api.
- XML docs on every public type and member, starting with its name. Other comments stay minimal.
- Errors carry context. Nothing is swallowed.

**Performance and logging**
- No allocation per frame, tick or send.
- Log lines are one physical line, in invariant culture, with fixed numeric formats. They contain no braces and never the word "Exception" (TooBigPacketException's type name is never logged). Units are ASCII (`us`, not µs).
- Every step-5 error line starts with `Phantoms error:` (installer, phantom transport, binder, sink) or `Send meter error:` (MeshMapNetworkManager). The rig fails a run only on "Exception" (regress.py:574-575, :893-894), so these prefixes are what accept_step5.py checks.

**Names**
- Mesh/ never names a TestMap or Tests type, in code or comments. TestMap never names a Tests type.
- New names come from §0.15, with `ServerMeshPhantomGroup` replacing `PhantomGroup`.
- No nested helper types with generic names (Entry, Kind, State, Window).
- Mesh/ must not use the words Mode, Placement, Driver, Callbacks, SyntheticPlayerBrain or BandEnvironment. Write "mode" in lower case.

**Default off and byte-identical.** With `MESH_METER_SENDS` and `MESH_PHANTOM` unset:
- no factory is added and no new line appears;
- the only code change on the default path is the `Innermost<LiteNetLibTransport>` walk, which returns a bare LiteNetLibTransport unchanged;
- the Bot net window is off unless its command-line argument is given.

**The kit's Logging.** It dereferences LogManager.LoggerManager, which is set only by a BeforeSceneLoad hook, so it cannot be used from code that EditMode tests reach. The pure parts take `IMeshLog` (Mesh/MeshLayoutCoordinator.cs:30; `MeshFakeLog` in Tests) or return error strings.

**Unity null.** Never use `??`, `??=`, `?.` or `is null` on a UnityEngine.Object; use `== null`. In the editor an unassigned serialized field or a missing GetComponent is a fake null that `??` treats as an object (LiteNetLibManager.cs:126-128 uses `==`).

**Process**
- Never start a real socket in a test: the running rig holds the ports.
- Do not run Unity, regress.py, the rig or git writes. Do not hand-write `.meta` files.
- Compile-check with `CSCHECK_OUT=<unique dir> python "...\2cf88996-...\scratchpad\cscheck.py" --editor`, ONLY while no rig chain runs.
- Keep each file's line endings.
- Each MonoBehaviour lives in its own file, named after the class.

## Decisions (made for the user; report them)
1. **The eye is a runtime GameObject.**
   - It is a LiteNetLibIdentity plus `ServerMeshPhantomEye`, hidden with `identity.SetIsHide(eye, true)` BEFORE `Assets.NetworkSpawn(identity, 0, id)`.
   - There is no prefab and no build window, and the sheet's static `ForceHideFunctions` delegate is not used: it runs on every pair, inflates AOI timing and leaks across plays.
   - `HideExceptions` stays empty (the quirk at LNI:845).
2. **`MESH_PHANTOM` gets a cell selector:** `<virtual|sink>:<P>:<character|eye>:<cell[,cell..]|all>`.
   - P phantoms bind on each listed cell.
   - `ship` (step 7) and an omitted cell field are refused with a warning, because without the field all ten cells would bind.
3. **`MESH_PHANTOM_REGISTER` moves to `p0tools.DROPPED_KEYS`.** Reason: phantoms anchor through their owned object, and registering sends central traffic and makes them handoff candidates whose handoff writes a fake row.
4. **`clients=` stays real wire peers.** It is the innermost LiteNetLibTransport's `Server.ConnectedPeersCount`, at both sites (Mesh stats and FillLoadRates).
   - This rejects the sheet's ServerPeersCount: per-client downlink (loadtest.py:11) divides clientOut, which excludes swallowed phantom bytes, by clients.
   - Phantoms are reported in the Phantom window.
5. **V counts subscribed movers except the phantom's own object.** A mover is any subscribed object with an entry in `EntityMovementDataHandlers`.
   - **Character kind:**
     - G = ceil(P / (V+1)) groups;
     - phantoms spread evenly over the groups;
     - filler per group = V + 1 − its phantom count (never below 0).
     - Pinned cases: (400, 100) → 4 × (100 phantoms + 1 filler); (50, 500) → 50 + 451; (100, 300) → 100 + 201; (150, 100) → 2 × (75 + 26).
   - **Eye kind:** one cluster of V filler movers centred on the area's centre, with all P eyes inside it (eyes are hidden from each other). Each eye follows a filler host (round-robin by seed) with offset 0.
   - **Eye kind, host mode (`MESH_PHANTOM_VIEW=0`):** no filler and no layout. Hosts are the cell's existing movers that no phantom owns (load-generator characters, monsters, puppets, prewarm objects: `EntityMovementDataHandlers` entries), picked round-robin by seed; an eye whose host is destroyed takes the next one. V_move is reported as measured. This is the sheet's "follows a host entity" (:669), needed by b3, b4 and b8 (:951-956).
   - **Filler** is server-owned characters spawned by the binder before its group's phantoms, paced at `MESH_PHANTOM_ARRIVAL_PER_S`.
     - Ids and names `phantomfill-{cell}-{j}` (j never reused); tracked with `TrackSyntheticEntity`, so Work window owned players = phantom characters + filler + load-generator players.
     - Motion follows `MESH_LOAD_MOTION`: kinematic (the default) = the group's `ServerMeshKinematicWalk.AddWalking`, after `Setup(region, false)`; steered = `SyntheticPlayerBrain.Setup(entity, region, seed, MESH_LOAD_FIGHT)`; inert = `AddStanding`.
     - Filler walks log `Kinematic walk:` lines; p0checks.check_motion reads them only under `MESH_LOAD_MOTION=kinematic`, where a filler fallback is a real fault.
   - **Geometry.** Each group is a disc of radius r. Its motion **region** is the disc's inscribed square (side r·√2), because StepWithin and `ServerMeshKinematicWalk.Setup` take a Rect (ServerMeshKinematicWalk.cs:55, 81-96). Spawn points, the feeder's StepWithin and the walk's Setup all use that Rect (`ServerMeshPhantomGroup.Region`). Every pair in a region is ≤ 2r ≤ R − 1 m apart. Centres sit on a near-square grid (columns = ceil(√G), rows = ceil(G / columns)) centred in the area, at pitch > R + 1 + 2r, so the nearest cross-group pair is > R + 1 m. R is the cell's visible range (`MESH_BAND` sets it); the AOI test is inclusive (`<=`).
   - **Area and r.** Discs lie inside the cell's scene rect shrunk by r on every edge, and by max(ghostBandWidth, R) + 1 + r on edges shared with a band neighbour (`OwnCell.bandNeighbours`), so no phantom ghosts onto a neighbour or sees its puppets. r is the largest value, in 0.01 m steps, ≤ (R − 1)/2 at which G groups fit; when even r = 1 m does not fit, Build errors naming G and the most that fit. Pinned:
     - coarse, R 80, no band edges: r 39.5, 16 groups fit (521 m centre square, 4 per axis), so (400, 23) errors with "needs 17 groups, 16 fit" and (400, 24) builds;
     - grid C, R 80, four band edges, one group: r = 19;
     - coarse, R 300, (400, 100): 4 groups at r = 74.74.
   - **Placement area (`MESH_PHANTOM_AREA`, character kind; F1 and F2, sheet :630, :1028):** no groups and no filler. Phantoms start uniformly by seed inside the rect or disc and walk inside the rect (a disc's inscribed square). V_move is reported as measured.
6. **Uploads.**
   - Transform payload: state 17 when moving, 16 when idle or standing; extra 0.
   - Input payload: always IsKeyMovement; one reliable stop frame when idle, then nothing.
   - Timestamp = max(last + 1, ServerTimestamp − 50) per phantom.
   - Teleport: confirm each server teleport ONCE, as a real client does. When the feeder first sees `entity.IsWaitingClientTeleportConfirm()` for a destination it has not confirmed yet, that phantom's next frame carries IsTeleport with `entity.EntityTransform.position` and its yaw, the server's destination; the feeder remembers that destination and sends no further IsTeleport frame while the wait still shows it. A new destination while waiting (a second teleport) gets its own single confirm. Reason (space team, KIT_AUDIT B13, 2026-10-01): the kit's handshake has no sequence, so any owner state with IsTeleport clears `_isServerWaitingTeleportConfirm` whichever teleport it answers, and a repeated confirm can release a later wait early. The server takes that frame's position as the new position (BIEMF3D:1555-1564, 1616), so the walk position would undo the teleport. The feeder then re-seats that phantom's walk there; a destination outside its region logs one warning per phantom and walks back in at move speed.
   - No IsJump or IsDash.
   - Default 20 Hz, a seeded phase per phantom, at most one upload per phantom per frame. When Hz exceeds the frame rate the feeder warns once (`upload Hz h above the frame rate f: at most f delivered`).
   - `MESH_PHANTOM_STAND=<share 0..1>` (default 0): that share of phantoms, chosen by seed, stands. Transform sends state 16 at the upload Hz; input sends one reliable stop frame, then nothing.
   - Sink-bound phantoms are fed by their sink, never by the server's feeder.
   - `MESH_PHANTOM_FIGHT=<share 0..1>` (default 0; character kind only; the user's decision 2026-10-01, for F2's "all
     fighting"): that share of phantoms, chosen by seed, fights through the load generator's own server-side fight code.
     Target choice, attacks and skills go through the same calls SyntheticPlayerBrain's fight mode makes, issued on the
     server. Movement stays with the uploads: the fighter never moves the character. A fighting phantom that also stands
     fights whatever comes into range; one that walks keeps its walk.
     - Reason: the cost under test is the downstream traffic fights create (damage, HP sync, effects to observers), which
       is identical whether an attack starts from a client request or on the server. The client attack requests it
       skips are a few bytes per second.
     - Implementation: a fight-only mode of SyntheticPlayerBrain (no movement commands), or a small
       `ServerMeshPhantomFighter` (a new TestMap name) calling the same helpers. Either way it heals when low, as the load
       generator does.
     - The Phantom window adds `fighting n`.
7. **Rate defaults.** Arrival pacing defaults to 50 binds/s. MESH_METER_SENDS is `off|count|time`. Expected meter load: the kit packs 35 EntityState records per packet (a 12 B header and 28 B records against the 1020 limit, BGNM:683-736), so V 100/300/500 means 3/9/15 packets per observer per tick, and P 400 at 20 Hz gives 24,000/72,000/120,000 ServerSend/s plus 400 pings/s. The count mode is budgeted at ≤ 50 ns per send (≤ 6 ms/s at the maximum). Every metered row reports Send window calls/s, so the meter's share can be stated.
8. **The step 5 env keys move into `p0tools.ENV_REGISTRY` and `regress.ENV_KEYS` now,** not at step 8. Otherwise this step's own acceptance runs could not set them. That is 10 keys: the sheet's 7 once REGISTER is dropped, plus `MESH_PHANTOM_AREA`, `MESH_PHANTOM_STAND` and `MESH_PHANTOM_FIGHT`, which join §0.16.
9. **The sink acceptance compares like with like.** The server's `Statistics.BytesSent` (every datagram, headers, acks and pings) is compared with the sum of the sink managers' `Statistics.BytesReceived`, in a sink-only run with no bots. The server figure is the Send window's `wire` bytes and the sink figure is the Sink window's `in` bytes, both integer window deltas.
10. **Bot net window.** It is gated by the bot argument `-meshBotNetSeconds <s>`, since bots get no env.
11. **Departures from the sheet.**
    - A packed ushort ends at 65,535 (`[F9 F7 0F]`; 67,823 is the varint's 3-byte limit), so TryReadMessageType refuses larger values.
    - IsServer is always false at AfterSceneLoad, so the installer gates on `-startMapServer` instead of "if IsServer log an error" (:665).
    - The load generator places uniformly and cannot form group discs, so the binder spawns the filler (:680).
    - LiteNetPeer.cs:634-647 also throws for the 4-byte-header deliveries (limit 1020), so the TooBig mirror covers Sequenced and ReliableSequenced (:662).
    - The new signatures of TryReadMessageType and Layout.Build are as in P and T.
    - CE-13 in this step is the space team's patch #10 with its length guard (kit_patch_9_10_reply.md:25). The sheet's deferred server-animation gate (:899) takes the next free CE number.
    - The meter-overhead gate runs on a phantom-loaded point, not the canary. The canary has no clients (p0plans.py:153-161; rigrun.py:98 asks for Play with enabled=False), so it never calls ServerSend and the gate could not fail.
    - `MESH_PHANTOM_STEPS` is refused with the character kind, where V would change with P. b6's stepped arms observe with eyes (:631, :970).
    - On a Dynamic map the binder binds nothing, with one warning. Phantom characters count as owned players in the occupancy grid (Load.cs:219-223) but are never handed off, so a re-cut would strand them.

## P. Pure Mesh/ types (new files)
Contents and tests are in critic.md "Plan".
- **`Mesh/MeshTransportUnwrap.cs`**
  - `public interface IMeshWrappedTransport { ITransport Inner { get; } }`;
  - `public static class MeshTransportUnwrap { public static T Innermost<T>(ITransport t) where T : class, ITransport }`. It follows at most 8 levels and returns the deepest T or null. MixTransport is opaque, which is today's behaviour.
- **`Mesh/MeshSendMeter.cs`** (MeshSendWindow may share the file).
  - `MeshSendMeter(long ticksPerSecond)`.
  - `static bool TryReadMessageType(byte[] data, int offset, int end, out ushort type, out int typeBytes)`, plus a `(data, length)` overload. It rejects null, truncated forms, a lead byte ≥ 250 and values > 65535.
  - `Record(ushort type, DeliveryMethod d, byte channel, int bytes, long elapsedTicks, bool refused)` and `RecordTooBig(ushort type, DeliveryMethod d, int bytes)`.
  - `MeshSendWindow TakeWindow(double seconds)`: a 64-slot open-addressed table plus other; 5 delivery slots plus other (an undefined value goes there); 16 channels plus other. It keeps exact long totals and an off-thread counter (owner thread id, Interlocked).
  - **Accounting.** Every ServerSend that returns, true or false, counts in the type, delivery and channel totals; a false return also counts in Refused. A send that throws TooBigPacketException counts only in TooBigCalls and TooBigBytes.
  - `MeshSendWindow` exposes public read-only totals: `Seconds`, `Calls(ushort type)`, `Bytes(ushort type)`, `OtherCalls`, `OtherBytes`, `DeliveryCalls(DeliveryMethod)`, `DeliveryBytes(DeliveryMethod)`, `ChannelCalls(int)`, `Refused`, `TooBigCalls`, `TooBigBytes`, `OffThread`. Step 6 checks "measure changes no bytes" against the integer totals in the line.
  - `MeshSendWindow.ToLine(long wireBytes, long wirePackets)` returns exactly this shape (illustrative values):
    ```
    Send window 5.00 s: calls 1250 250.0/s bytes 512000 100.0 kB/s | t119 calls 1000 bytes 501760 200.0/s 98.0 kB/s 0.00 us | t2 calls 250 bytes 10240 50.0/s 2.0 kB/s 0.00 us | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 250 10240 rs 0 0 u 1000 501760 other 0 0 | channels 0:250 2:1000 other:0 | wire 530000 B 1300 pkt | offThread 0
    ```
    Up to six `t<type>` groups by bytes, descending (ties by type ascending), then `other`. Integer totals before rates; rates `0.0`; kB = 1024 B; `us` = mean µs per call inside the inner ServerSend, `0.00` in count mode. Delivery groups in enum order (ru seq ro rs u), then other, each `calls bytes`. `channels` lists only non-empty channels as `index:calls`, then `other:n`. `wire` is the innermost LiteNetLib server's BytesSent and PacketsSent window deltas. No NaN or negatives, and the line never starts with `Work window`.
- **`Mesh/MeshMeteredTransport.cs`.** `public sealed class MeshMeteredTransport : ITransport, IMeshWrappedTransport`, constructed as `(ITransport inner, MeshSendMeter meter, bool timed)`. Null arguments throw, naming the argument.
  - All 18 members (ITransport.cs:8-25) are forwarded unchanged, IsReliableOnly included.
  - Only `ServerSend` is metered. The type is decoded from `writer.Data` at offset 0, and only TooBigPacketException is caught: `RecordTooBig`, then `throw;` (the same instance).
  - `System.Diagnostics.Stopwatch` is written fully qualified.
- **`Mesh/MeshMeteredTransportFactory.cs`**, a MonoBehaviour: `public class MeshMeteredTransportFactory : BaseTransportFactory`.
  - Fields `Inner`, `Meter` and `Timed`, and an empty Awake.
  - `public static BaseTransportFactory KitDefault(GameObject go)` returns `AddComponent<LiteNetLibTransportFactory>()`, which is the kit's own fallback.
  - `public static bool TryParseSetting(string value, out bool meter, out bool timed, out string warning)`. Null, empty and `off` give no meter and no warning.
  - `Build()`:
    - it throws when Inner == this;
    - it tests Inner with Unity's `== null`, and stores KitDefault's result in Inner, so repeated Builds add one component;
    - it guards re-entry with a private bool, set on entry and cleared in `finally`. A re-entry throws InvalidOperationException ("the transport factory chain under <gameObject name> cycles back to the metered factory"). Mesh/ cannot walk the chain itself: BaseTransportFactory has no Inner, and the rig's factory may not be named;
    - it returns a fresh `new MeshMeteredTransport(Inner.Build(), Meter, Timed)`.
    - Build also runs for client transports, which is harmless.
- **Tests** (Tests/Editor, new): `MeshFakeTransport`, `MeshSendMeterTests`, `MeshMeteredTransportTests` (the latter covers the Innermost and factory rows). Each lists failure cases first; the rows from critic.md Plan, plus:
  - MeshSendMeterTests:
    - golden decodes 0/119/240/241/2287/2288/65000/65535, cross-checked with PutPackedUShort; failures: null, empty, truncated 2- and 3-byte forms, a lead byte ≥ 250, `[F9 FF FF]` (above 65535);
    - exact per-type and per-delivery totals through every public accessor;
    - a 65th distinct type goes to other, with exact OtherCalls and OtherBytes; colliding types t, t+64 and t+128 stay apart;
    - `(DeliveryMethod)7` goes to the delivery other slot; channel 16 goes to the channel other slot;
    - Record on a second Thread gives OffThread 1 and leaves every total unchanged;
    - RecordTooBig moves only the TooBig totals;
    - window reset; timed against count-only;
    - Record allocates nothing in either mode (`UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory()`);
    - the exact Send window line under de-DE.
  - MeshMeteredTransportTests:
    - an 18-row forwarding table plus a reflection guard that every ITransport method is covered;
    - refused sends counted;
    - TooBig rethrown as the same instance and counted only as TooBig;
    - client calls not metered;
    - an empty writer counted as an unknown type;
    - ServerSend allocates nothing (with MeshFakeTransport);
    - Innermost through 0, 1 and 2 wrappers, with a null Inner, a self-reference, and `new OfflineTransport()` giving null.
  - The factory:
    - no Inner adds exactly one LiteNetLibTransportFactory with the kit defaults, and a second Build adds none;
    - Inner == this throws; A.Inner = B with B.Inner = A throws;
    - two Builds give distinct transports sharing one meter;
    - TryParseSetting rows, including null and "" (no meter, no warning).

## G. Manager integration (Mesh/; sites with current lines are in critic.md Plan)
- **`MeshMapNetworkManager.Awake` (:95-99), before `base.Awake()`.** Parse MESH_METER_SENDS. Unset, empty or `off` logs nothing; `count` and `time` log one setting line; any other value logs one warning and installs nothing. When it is on:
  1. capture the kit's factory BEFORE adding anything: `BaseTransportFactory kit = TransportFactory; if (kit == null) kit = GetComponent<BaseTransportFactory>(); if (kit == null) kit = null;` (the last statement turns a Unity fake null into a real null);
  2. AddComponent<MeshMeteredTransportFactory>();
  3. set Inner = kit, Meter = new MeshSendMeter(Stopwatch.Frequency) and Timed;
  4. set `TransportFactory = metered`;
  5. keep `_sendMeter`.
- **`OnStartServer` (:648-649).**
  - Enable statistics through `Innermost<LiteNetLibTransport>(ServerTransport)`.
  - Baseline the wire counters (BytesSent, PacketsSent).
  - Log `Send meter error: ...` when `_sendMeter != null` but ServerTransport is not a MeshMeteredTransport. useWebSocket silently replaces the factory (LiteNetLibManager.cs:113-123).
- **`Ghosting.LogMeshStats` (:167-171) and `Load.FillLoadRates` (:258-263).** Both read through `Innermost<LiteNetLibTransport>` and keep `clients = ConnectedPeersCount`. The two sites change together, and the line stays byte-identical with no env.
- **`Load.LoadTick` (:142-156).**
  - After `_hasWorkWindow = true;` (:148) and before the retired-cell return (:150), take the meter window and advance the BytesSent and PacketsSent baselines together, also on a retired cell, so windows and wire deltas stay aligned and the window's own allocation is not filed in this window's gc0.
  - Log `Send window` (with those wire deltas) after the Work ticks line and the optional bins line, only when `_sendMeter != null`; a retired cell has already returned.
  - The MeshLoadReport wire format is untouched (§0.10).
- **Mesh-link casts** (CellMeshLink.cs:445, 652, 668-669; Ghosting.cs:222) stay as they are.

## T. Rig phantoms (TestMap/Phantoms/, new)
Members and order are in critic.md Plan.
- **`ServerMeshPhantomSettings`.** Pure TryParse for:
  - MESH_PHANTOM with the cell field (Decision 2);
  - MESH_PHANTOM_VIEW, required when MESH_PHANTOM is set (missing: refused with a warning naming the key, and nothing binds), unless MESH_PHANTOM_AREA is set (then it must be unset). The character kind needs ≥ 1; the eye kind takes ≥ 0, where 0 means host mode;
  - ARRIVAL_PER_S > 0 (default 50);
  - UPLOAD_HZ 1-60 (default 20);
  - PAYLOAD `transform|input` (default transform), with ship56/ship28 refused until step 7 and input with eye refused;
  - STEPS `<P1,..,Pn>@<s>`: every Pi ≤ the P of MESH_PHANTOM, otherwise refused; refused with the character kind (Decision 11). The schedule is a pure static `StepTarget(steps, holdSeconds, secondsSinceStepReached, boundCount)` returning the step index and target;
  - AREA `<cx>,<cz>,<w>,<h>` (centre and size) or `disc:<cx>,<cz>,<r>`, character kind only;
  - STAND `<share 0..1>`, character kind only;
  - FIGHT `<share 0..1>`, character kind only;
  - the sink argument `-meshPhantomSink host:port:count[:portBase]`.

  An empty value is unset, with no warning. Warnings turn braces into parentheses.
- **`ServerMeshPhantomTransport : ITransport, IMeshWrappedTransport`**, constructed as `(ITransport inner, IMeshLog log, long firstK)`.
  - **Ids:** 1_000_000 + k. The kit builds a new transport on every StartServer (LiteNetLibManager.cs:142-148), so k lives in ServerMeshPhantomTransportFactory. The factory passes its next k to each transport it builds and continues from the last transport's `public long NextK`, so k is never reused while the process lives. After a restart the kit's 1 Hz pings to stale phantom ids, which ConnectionIds keeps (LiteNetLibServer.cs:80-86), count as refused.
  - **Members:** `TryQueueConnect(out long id)` (false when not started, or when inner peers + phantoms ≥ ServerMaxConnections), `QueueUpload(long id, NetDataWriter frame)` (copied into a pool), `QueueDisconnect(long id)` (once, Reason RemoteConnectionClose), `IsPhantom(long)`, `TryDequeueHello(out long conn, out int index)`, swallowed counters per delivery, and swallowed payload bytes per bound phantom in a preallocated long[] indexed by a slot assigned at bind. A phantom is bound when its ConnectEvent is delivered.
  - **`ServerReceive`:**
    1. recycle the previous synthetic reader;
    2. drain inner events first, peeking each inner DataEvent for the 65000 hello at `reader.Position` (absolute restore) and consuming it; a truncated hello passes through unconsumed. An inner ConnectEvent that carries a bound phantom id emits that phantom's disconnect first and logs `Phantoms error: ...`;
    3. deliver synthetic events in FIFO order.
  - **`ServerSend`:**
    - to a bound phantom: mirror LiteNetLib's TooBig rule (Unreliable > 1023, Sequenced and ReliableSequenced > 1020, using NetConstants), and otherwise count and return true without allocating. A send counts as swallowed only when it returns true, so an emulated TooBig is never swallowed. Per window and delivery, the Send window's calls = swallowed + forwarded, refused included;
    - to an unbound id in the phantom range: false;
    - otherwise forward.
  - **Other members:** `ServerDisconnect(phantom)` unbinds, queues DisconnectPeerCalled and returns true. `GetServerRtt(phantom)` returns 0. `ServerPeersCount` = inner + bound. `StopServer` clears everything, emits nothing, then calls inner.StopServer.
- **`ServerMeshPhantomTransportFactory`** is a BaseTransportFactory with `Inner` resolved through `MeshMeteredTransportFactory.KitDefault` (Unity `== null`), and the k counter above.
- **`ServerMeshPhantomInstaller`** runs at `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`. It returns unless MESH_PHANTOM parses, refuses in the editor, requires `-startMapServer` (ConfigReader.IsArgsProvided) and requires the Singleton to be a MeshMapNetworkManager.
  - The splice is a public static `Splice(BaseTransportFactory root, ServerMeshPhantomTransportFactory phantom)` that returns the new root:
    - with a metered root: `phantom.Inner = metered.Inner; metered.Inner = phantom`, and it returns the root;
    - otherwise: `phantom.Inner = root`, and it returns phantom.

    The installer assigns the result to `mesh.TransportFactory`.
  - It never resolves through GetComponent once a wrapper exists, creates the binder (DontDestroyOnLoad) and logs one setting line.
- **`ServerMeshPhantomLayout` and `ServerMeshPhantomGroup`.** A pure, deterministic-per-seed `Build(int phantoms, int view, float range, Rect area, float bandWidth, bool bandWest, bool bandEast, bool bandSouth, bool bandNorth, int seed, bool eyes, out string error)` (Decision 5). Each group has a centre, r, `Region` (Rect), phantom count, filler count and seeded positions. Errors: P = 0, V < 1 for the character kind, and groups that do not fit. Eyes with view 0 return 0 groups and no error. A second pure method places the AREA mode's positions.
- **`ServerMeshPhantomUploads`**, a MonoBehaviour with a negative `[DefaultExecutionOrder]`, plus `IServerMeshPhantomPayload`, `ServerMeshPhantomTransformPayload` and `ServerMeshPhantomInputPayload`.
  - The due calculation is pure: timestamp, frame time and a seeded phase are parameters, with no burst after a stall.
  - Frame: `WritePacket(119)`, `PutPackedLong(ts)`, `PutPackedUInt(objectId)`, payload. The payloads are hand-written, copying ClientWriteSyncTransform3D and ClientWriteMovementInput3D; never reuse the kinematic body's server writer, whose trailing Put(0) is the force count.
  - Motion goes through `ServerMeshKinematicWalk.StepWithin` inside the group's Region (or the AREA rect), at the character's move speed. Sink-bound phantoms are skipped.
  - Before queuing this frame's uploads it feeds the window's posErr sample. Feeder time is timed for the window.
- **`ServerMeshPhantomClients` (the binder).**
  - **Waits for:** IsServer, OwnCell, a target cell and `IsServerReadyToInstantiateObjects()`. The transport is found with `MeshTransportUnwrap.Innermost<ServerMeshPhantomTransport>`, again after every restart. If it is missing, that is a `Phantoms error:` line.
  - **Dynamic map:** if `mesh.OwnCell.OnDynamicMap`, it binds nothing and logs one warning: `Phantoms: cell X is on a Dynamic map; phantoms count as owned players and are never handed off, so none are bound`.
  - **Per phantom:** paced `TryQueueConnect` → poll `TryGetPlayer(id)` → `player.IsReady = true` → spawn. Filler is spawned first, at the same pace.
  - **Character spawn:** the data preparation copies ServerMeshLoadGenerator.cs:166-187, with id `phantom-{cell}-{k}`. The input payload sets both `ccem.movementSecure` and `ccem.Functions.movementSecure`. Then `NetworkSpawn(obj, 0, id)`, ForceMakeCaches, FillEmptySlots, HP/MP and `TrackSyntheticEntity`, healed when low. NotSecure is restored before unbinding.
  - **Eye:** Decision 1; hosts per Decision 5.
  - **Filler:** Decision 5.
  - **Sink:**
    - Each dequeued hello with index < P binds to its real connection id. A hello for an index bound to a live connection is ignored, with one warning. The binder frees an index when `TryGetPlayer(conn)` fails or the bound object is destroyed, and a later hello rebinds it.
    - The reply goes through `mesh.ServerSendMessage(conn, 0, ReliableOrdered, w)`: `[65001][packed uint objectId][float x][float z][byte kind 0 character 1 eye][byte payload 0 transform 1 input][byte hz][byte stand 0|1][float speed][float cx][float cz][float halfSide]`. Here (x, z) is the spawn point, speed is the character's move speed, and (cx, cz, halfSide) is the motion square.
  - **STEPS:**
    - the layout is built once for P, and phantoms take slots in index order;
    - step 1 binds P1, and its clock starts when all P1 are bound; step i starts after step i−1 has held s seconds;
    - stepping up binds the next slots, paced at ARRIVAL_PER_S; stepping down QueueDisconnects the highest indices;
    - each change logs `Phantom step i/n: P a -> b`.
  - **Lines:** `Phantoms: cell X port N mode M kind K P n V v groups G`, then one `Phantom group g: centre x z side s phantoms p filler f` per group (`0.0`).
  - **Other:** reset everything when IsServer goes false. Exactly one owned object per phantom, and no registration.
- **`ServerMeshPhantomEye`.** A plain follower MonoBehaviour (LateUpdate: its host's position with offset 0; it is hidden, so overlapping its host costs nothing). It has no LiteNetLibBehaviour.
- **`ServerMeshPhantomWindow`.** A pure sampler, called once per 5 s window, over a rotating 10% sample.
  - **V_move:** taken from the subscribing ids, the handler dictionary and the own id.
  - **posErr:** for sampled virtual character phantoms that are not waiting for a teleport confirm. It is the XZ distance between `entity.EntityTransform.position` and the last upload queued in an EARLIER frame, so that upload has been applied.
  - **eyeSeen:** the number of eyes whose identity has a subscriber other than their owner (`CountSubscribers`, `HasSubscriber`).
  - **down:** per bound virtual phantom, swallowed payload kB/s. Payload is writer.Length, with LiteNetLib headers excluded.

  `ToLine` returns exactly this shape (illustrative values; every field always printed, zeros when empty):
  ```
  Phantom window 5.00 s: bound virtual 200 sink 0 ready 200 filler 2 eyeSeen 0 | step 0/0 target 200 | vmove n 20 mean 100.0 p95 100 min 99 | uploads 4000.0/s | posErr n 3000 p50 0.12 p95 0.24 max 0.41 m | down n 200 mean 55.3 p95 56.0 max 57.2 kB/s | swallowed ru 0 0 seq 0 0 ro 1000 56000 rs 0 0 u 60000 56000000 other 0 0 | frames 150 rig feeder 1.20 binder 0.10 sampler 0.05 ms/s 0.045 ms/frame
  ```
  Swallowed groups are `calls bytes`; `step 0/0` when STEPS is unset.

  **Rig time.** p0tools pairs each Phantom window with the Work window the same cell logged most recently before it. Analysis subtracts rig ms/frame from per-frame means only: the Work window work mean, and the Work ticks tick-frame and other means. Tick durations (kit, send) exclude Update. p95, p99 and max are reported unadjusted, with rig ms/frame beside them. Windows before bound reaches the target are excluded.
- **`ServerMeshPhantomSink`.** It runs at AfterSceneLoad on `-meshPhantomSink` (batchmode, nographics, no server argument).
  - All NetManagers (16 channels, key SampleConnectKey, statistics on, AutoRecycle, IPv6 off) are created and started in manual mode on the MAIN thread, after its own `Application.quitting` handler, which stops and joins the workers first.
  - K workers of about 100 managers each run PollEvents and ManualUpdate, well inside the 5 s timeout.
  - **Protocol:**
    - hello `[65000][int32 index]` ReliableOrdered on channel 0;
    - after the 65001 reply, a peer uploads only for kind 0 (character), at hz, on channel 2. It walks with `ServerMeshKinematicWalk.StepWithin` inside the reply's square, at the reply's speed, through the same payload writers as the virtual feeder, with a per-peer rising ms timestamp;
    - stand = 1 follows Decision 6's standing rule;
    - each upload is due at an absolute Stopwatch time (next += 1/hz). There is at most one per peer per poll pass, and a late peer skips to its next due time with no burst. A fixed sleep would turn 50 ms into about 62.5 ms at Windows' 15.6 ms granularity.
  - **Output:** a `Sink window` line from the main thread through `Logging.Log("Mesh", ...)`, so it carries the logger's stamp in `-logFile` (DefaultLogger.cs:73):
    ```
    Sink window 5.00 s: peers 50 connected 50 bound 50 | in 2621440 B 6000 pkt 512.0 kB/s | down n 50 mean 10.2 p95 10.5 max 10.9 kB/s | t119 calls 5000 bytes 2500000 | t65001 calls 0 bytes 0 | other calls 1000 bytes 30000 | out 5000 uploads 1000.0/s 30.0 kB/s | disconnects 0 | workers 1 maxPassMs 3.2
    ```
    - `in`: the summed BytesReceived and PacketsReceived window deltas of every manager.
    - `down`: per-peer BytesReceived kB/s (wire).
    - Received types: up to six by bytes, then other.
    - `out`: uploads sent, with BytesSent kB/s.
    - `maxPassMs`: the worst worker poll pass.
- **`TestMap/ServerMeshAutoClient.cs` (edit), gated by `-meshBotNetSeconds`.**
  - It re-detects `Innermost<LiteNetLibTransport>(mesh.ClientTransport).Client` by reference; each migration hop builds a new NetManager. On a new one it enables statistics and resets the baseline.
  - Every N s it logs, through Log(), the line from a public static `BotNetWindowLine(...)`:
    ```
    Bot net window 5.00 s: in 52.3 kB/s 120 pkt/s out 3.1 kB/s 22 pkt/s | hops 0 | rtt 1 ms | players seen 200 | at 2026-10-01T12:00:05Z
    ```
    `players seen` counts the BasePlayerCharacterEntity objects other than its own when the line is logged. `at` is UTC, because Log() lines carry no stamp.
- **Tests** (Tests/Editor), failure cases first:
  - `ServerMeshPhantomTransportTests`:
    - failures:
      - a send before the connect is delivered returns false, and so does an unbound phantom-range id, with no inner call;
      - disconnecting an unknown id returns false;
      - TryQueueConnect is false before StartServer, and at inner peers + phantoms == ServerMaxConnections;
      - an inner ConnectEvent carrying a bound phantom id gives that phantom's DisconnectEvent, then the ConnectEvent, and one MeshFakeLog error;
      - a truncated hello passes through unconsumed;
      - the oversize table per delivery, where an emulated TooBig is not swallowed;
    - successes:
      - inner events come before synthetic ones;
      - the hello is read through an offset-4 reader;
      - ServerPeersCount includes phantoms;
      - ServerDisconnect(phantom) gives exactly one DisconnectEvent with DisconnectPeerCalled, and a later send returns false with no inner call;
      - QueueDisconnect twice gives one event with RemoteConnectionClose;
      - GetServerRtt(phantom) is 0 with no inner call;
      - a swallowed send allocates nothing;
      - event N's reader yields the frame bytes to EndOfData until call N+1;
      - per-phantom swallowed bytes are counted;
      - StopServer drops queued events and hellos and emits none;
      - k continues across StopServer and a second factory Build (connect 3, Build again, and the next id is 1_000_003);
      - Innermost works through metered over phantom over lnl;
      - Splice on HideAndDontSave GameObjects, with and without a metered root.
  - `ServerMeshPhantomUploadTests`:
    - the golden 23-byte frame `77 00 F7 E0 2A 11 00 00 00 C0 3F 00 00 20 C0 00 00 50 40 FA 20 BF 02`, from ts 1000, objectId 42, state 17 (Forward|IsGrounded), extra 0, position (1.5, −2.5, 3.25) and yaw 90°. A second case uses ts 1,790,000,000,000: 27 bytes with the same round trip;
    - round trips through the kit's header and transform readers, and through ReadMovementInputMessage3D (a stop frame omits the extra byte), each ending at EndOfData;
    - ts = max(last+1, now−50) rises strictly when the server clock steps back, repeats or jumps;
    - IsTeleport is set only while waiting, exactly once per destination (a wait that stays on gives one IsTeleport frame, then plain frames; a second destination while waiting gives one more), and the frame carries the given server position;
    - input frames always carry IsKeyMovement; idle gives exactly one reliable stop frame, then none; IsJump and IsDash are never set;
    - the scheduler at 1/30 s and 1/60 s: 20 ± 1 per 1 s window, never twice in one frame, and 1200 ± 1 over 60 s. A 0.15 s stall frame gives at most 1, with no burst after. 60 Hz at 1/30 s frames gives 30 ± 1 per s;
    - feeder steps are ≤ speed·dt + 1e-4 and stay inside the Region.
  - `ServerMeshPhantomLayoutTests`:
    - failures: P = 0; V < 1 for the character kind; groups that do not fit, with the message's counts;
    - successes:
      - Decision 5's pinned cases, and its r cases;
      - the corners within one Region are ≤ R − 1 apart, and the nearest corners of two Regions are > R + 1 apart;
      - each Region lies inside its disc and inside the shrunk area;
      - the same seed gives the same layout, and a different seed a different one;
      - the eye kind gives one cluster of V filler centred on the area, with all P eyes inside;
      - eyes with view 0 give 0 groups and no error;
      - AREA positions stay inside the rect or disc.
  - `ServerMeshPhantomSettingsTests`:
    - the grammar of every key;
    - VIEW missing is refused, and VIEW 0 with the character kind is refused;
    - a STEPS value above P is refused, and STEPS with the character kind is refused;
    - StepTarget steps up and down;
    - an empty value is unset, with no warning.
  - `ServerMeshPhantomEyeHideTests`.
  - `ServerMeshPhantomWindowTests`:
    - the exact Phantom window and Bot net window lines under de-DE, including the zeros line;
    - the sampler on fixture sets; posErr on fixture positions; eyeSeen; the per-phantom down distribution.
  - `ServerMeshPhantomSinkTests` (pure parts only):
    - the argument grammar;
    - the hello and 65001 reply encodings as round trips;
    - the upload due function: 20 ± 1 per s with a 15.6 ms poll quantum, and no burst after a stall;
    - the exact Sink window line.

## Y. Python (Tools/ServerMeshing)
- **`p0tools.py`.**
  - Move 7 keys from PENDING_KEYS into ENV_REGISTRY, each with its meaning: MESH_METER_SENDS, MESH_PHANTOM, MESH_PHANTOM_VIEW, MESH_PHANTOM_ARRIVAL_PER_S, MESH_PHANTOM_UPLOAD_HZ, MESH_PHANTOM_PAYLOAD and MESH_PHANTOM_STEPS. Add MESH_PHANTOM_AREA, MESH_PHANTOM_STAND and MESH_PHANTOM_FIGHT, which makes 10.
  - Set `DROPPED_KEYS["MESH_PHANTOM_REGISTER"] = "dropped in step 5 (decision 3): phantoms anchor through their owned object, and registering sends central traffic and makes them handoff candidates whose handoff writes a fake row"`. rigrun prints `<key> was <reason>` (rigrun.py:204).
  - In the docstring, :21 becomes "the ones it no longer needs", and the comment at :29 becomes "(six from step 2, four from step 3, nine from step 5)".
  - Add `parse_send_window`, `parse_phantom_window`, `parse_sink_window` and `parse_bot_net_window`, written from the exact lines in P and T.
  - Add a mirror of the MESH_PHANTOM, MESH_PHANTOM_AREA and MESH_METER_SENDS grammars.
- **`regress.py` ENV_KEYS (:69-81).** Add the 10 keys under a "Phase 0 step 5" comment. Add "invalid state length" and "error occurs while reading" to the bot-log check (:891-894), each failing the run when its count is above 0. "error occurs while reading" exists today (BGNM:613). "invalid state length" exists only once CE-13 lands. Both checks land in the same build window as CE-13 (kitq_q2-length-guard.md:227, :255).
- **Tests.**
  - `test_p0tools.py`:
    - the registry set (:250-254), with the test renamed `test_the_step_two_three_and_five_keys`;
    - the counts (:265 → registry 19, pending 25, dropped 2);
    - SECTION_0_16_KEYS gains MESH_PHANTOM_AREA, MESH_PHANTOM_STAND and MESH_PHANTOM_FIGHT (its comment becomes 14 Mesh/ keys and 33 TestMap/ keys);
    - the pending examples (:277-279).
  - Parser tests (valid, malformed, incomplete) and grammar-mirror tests.
  - `test_rigrun.py` CheckEnvTests:
    - why it changes: test_check_env_accepts (:138) sends every regress.ENV_KEYS key with PENDING mocked, and MESH_PHANTOM becomes a regress key in this step;
    - use fictitious pending keys, so no later step breaks it again: `PENDING = {"MESH_LATER_B": "stage 0b, step 7", "MESH_LATER_C": "stage 0c, step 9"}` (:106);
    - the "stage 0b key" case (:115) becomes `({"MESH_LATER_B": "1"}, "MESH_LATER_B is a stage 0b key (step 7) that no code reads yet")`;
    - the "stage 0c key" case (:116) uses MESH_LATER_C (step 9);
    - the "unknown and pending" case (:124-126) uses MESH_LATER_B.
  - `test_remoteb.py` (new): sink_arguments for counts 50, 100 and 400, with and without a port base.
- **`remoteb.py`.**
  - New commands `sink-start` and `sink-stop`, with `--sink-port` (A's cell port, required, read from the `Phantoms: cell C port N` line), `--sink-count` (default 50) and `--sink-base`.
  - `sink-start` writes `start-sink.cmd` with a pure `sink_arguments(a_ip, port, count, base)`: `-batchmode -nographics -meshPhantomSink <a_ip>:<port>:<count>[:<base>] -logFile <dir>\Logs\sink.log`. It launches that file through the Win32_Process.Create escape.
  - `sink-stop` stops only processes whose CommandLine matches `*-meshPhantomSink*` or `*start-sink*`, excluding its own ssh session as cmd_stop does.
  - `running()` and `stop` count and kill only processes without `-meshPhantomSink` (:77-79, 197-207), so `start` refuses only while a cell runs.
  - The new build is deployed to B first.
- **`accept_step5.py`** (scratchpad, as steps 2 and 3 did). rigrun.run_window has no hook for a bot or a sink, so it drives Play itself:
  1. kill_bots, db_reset, `request_play("none", 0, 0, env=..., enabled=False)`, wait_play;
  2. poll the cell log for the `Phantoms:` line and for a Phantom window with bound = P;
  3. launch the bot (acceptance 3) through launch_bot's args, or `remoteb.py sink-start` (acceptance 5) with the logged port;
  4. warm up and measure;
  5. stop the sink or kill the bot, fetch and stop_play.

  It fails acceptance 1c-5 on any zone-log line starting with `Phantoms error:` or `Send meter error:`, or containing `Too Big Packet` (BGNM:729, 762).
- The p0load `accept-b` block and the stage 0b families come at step 8.

## Acceptance (rig; build window after chain 2; together with CE-12/CE-13, then accept-a again and a bridge canary)
"Coarse C" means `MESH_LAYOUT_OVERRIDE` coarse with `MESH_SPAWN_CELLS=C`.
1. **The default gate.** No env: Mesh stats and Work lines are byte-identical in form, and all ten scenarios Static and Dynamic plus `dynidle` pass. They also pass with `MESH_PUPPET_MOTION=kinematic`.
   - **1b. Keys set on real scenarios:** `regress.py --env MESH_METER_SENDS=time --env MESH_PHANTOM=virtual:20:character:SW --env MESH_PHANTOM_VIEW=19` (one group of 20 phantoms, V 19).
     - SW is a cell that no scenario reads or walks into.
     - pet, party, crash, harvest, build, seam, seamcold, pvp and dynidle pass. In dynidle the map is Dynamic, so the binder binds nothing and warns. sleep and wake are left out, because a phantom cell refuses to sleep by design.
     - Every map server installs the phantom transport, the editor's client runs through the metered factory, and every serving cell logs one Send window per probe window.
   - **1c. Input payload:** coarse C, `MESH_NO_MONSTERS=1`, `MESH_PHANTOM=virtual:200:character:C`, `MESH_PHANTOM_VIEW=100`, `MESH_PHANTOM_PAYLOAD=input`, `MESH_METER_SENDS=count`. Pass: every phantom bound and ready; uploads/s within ±10% of 20 × moving phantoms; posErr reported, not gated.
2. **Virtual, 200 characters:** coarse C, `MESH_NO_MONSTERS=1`, `MESH_PHANTOM=virtual:200:character:C`, `MESH_PHANTOM_VIEW=100` (2 × (100 phantoms + 1 filler), so V = 99 + 1 = 100), `MESH_METER_SENDS=count`:
   - every phantom is bound and ready;
   - V_move mean is within ±10% of 100;
   - posErr p95 ≤ 0.50 m in every window after all are bound;
   - uploads/s within ±10% of 4000;
   - no exception or step-5 error lines;
   - Phantom and Send window lines parse.
   - Reported, not gated: one run at 400 × V 100 records the Work ticks send p95 and the Send window t119 calls. The DESIGN 0.74 row states that kit-arm figures include CE-12's per-write Record (one HashSet add per written record, upstream patch 0006), which step 6's sender arm does not pay.
3. **Eyes, 50 at V 200:** coarse C, `MESH_NO_MONSTERS=1`, `MESH_PHANTOM=virtual:50:eye:C`, `MESH_PHANTOM_VIEW=200`, `MESH_METER_SENDS=count`.
   - accept_step5.py reads the cluster centre from the `Phantom group 0:` line, which is the area's centre (0, 0). It launches one bot with `launch_bot("step5eye", "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", cx, "-meshWalkZ", cz, "-meshBotNetSeconds", "5"])`.
   - Pass:
     - every eye is bound;
     - eyeSeen is 0 in every Phantom window;
     - the bot log has no line containing `is not registered`, `Unable to spawn object` or `Exception`;
     - a Bot net window parses at least once per 5 s after login, with in kB/s > 0, hops ≥ 0 and players seen ≥ 180 while standing (the positive control);
     - the eyes' V_move mean is within ±10% of 201 (200 filler plus the bot).
4. **Meter overhead in count mode:** 5 interleaved pairs (AB BA AB BA AB) on coarse C, with `MESH_RATES=30/0.02`, `MESH_NO_MONSTERS=1`, `MESH_PHANTOM=virtual:400:eye:C` and `MESH_PHANTOM_VIEW=100` in both arms. Arm B adds `MESH_METER_SENDS=count`.
   - The verdict is `p0accept.overhead(pairs, gate_ms = 0.01 × arm A's mean C work_ms_per_frame)`; only NO-GO fails, as in accept-a.
   - A pair is void when arm B's Send window shows fewer than 20,000 calls/s; about 24,400 are expected (3 EntityState packets per eye per tick × 20 Hz × 400, plus pings).
   - Report the implied ns/send = Δwork ms/s ÷ calls/s.
   - One time-mode run is reported (its us/call), not gated.
5. **Sink 50 on B** (attended, the user's machine B): coarse C, `MESH_NO_MONSTERS=1`, `MESH_PHANTOM=sink:50:character:C`, `MESH_PHANTOM_VIEW=49`, `MESH_METER_SENDS=count`, enabled=False and no bots; started with `remoteb.py sink-start --sink-port N --sink-count 50`.
   - Every peer is bound.
   - Over a 60 s measurement after the last bind, the mean Send window wire rate (wire B / seconds) over windows wholly inside it is within ±3% of the mean Sink window `in` rate over the sink's windows inside the same span. Each side is read on its own stamps; both rates are steady.
6. **Unit gates.** EditMode is all green. Python unittest and ruff are clean. The stray-reference test passes with the new reserved names.

## Critic changes (2026-10-01)
Applied:
- A1: Acceptance 4 now runs on a phantom-loaded point (400 eyes at V 100, about 24k sends/s), not the client-less canary. V 100 rather than 300 keeps arm A unsaturated (120k kit writes per tick at V 300). The verdict and void rule come from B2 and C1. Decision 11 records the departure.
- A2: Added posErr to the Phantom window (sampled against the last upload queued in an earlier frame). Acceptance 2 gates posErr p95 ≤ 0.50 m.
- A3: The 65001 reply now carries kind, payload, hz, a stand flag, speed and the motion square. The sink uploads only for characters, on an absolute-time scheduler. The server feeder skips sink-bound phantoms.
- A4: Added eye host mode (VIEW 0) following existing movers. VIEW ≥ 0 for eyes.
- A5: Filler motion follows MESH_LOAD_MOTION. Filler is tracked, paced and counted in the Phantom window.
- A6: Defined r (the largest fit in 0.01 m steps), the edge-aware shrink and the new Build signature, with pinned cases. With C18's 1 m margin the grid C case is r = 19 (A6 had 20).
- A7: STEPS steps both ways. Layout for P, step clock, highest indices leave, StepTarget, step logging and the window field are defined. The character kind is refused.
- A8: Added acceptance 1b (real scenarios with the keys set, phantoms on SW; sleep and wake excluded with a reason) and 1c (input payload).
- A9: Added the missing meter, transport and upload test rows.
- A10: Acceptance 3 is rewritten: MESH_NO_MONSTERS, a centred and logged cluster, the eyeSeen instrument, and an expected V_move of 201.
- A11: Added MESH_PHANTOM_AREA and MESH_PHANTOM_STAND (keys 9, counts (19, 25, 2)). The user's F2 answer (2026-10-01) added MESH_PHANTOM_FIGHT: keys 10, counts (20, 25, 2).
- A12: Motion region = the disc's inscribed square (ServerMeshPhantomGroup.Region), with layout tests on its corners.
- A13: The eye follows its host with offset 0.
- A14: Decision 7 states the packing arithmetic, the expected send rates and a ≤ 50 ns per send budget.
- A15: VIEW is required. PAYLOAD defaults to transform. Acceptance 2 states V 100 and its layout.
- A16: A Hz above the frame rate warns once. Added a scheduler test for 60 Hz at 30 fps.
- A17: sink-start takes the port and count. Added test_remoteb. Merged with B17.
- A18: The Sink window format is pinned with integer `in` bytes. Acceptance 5 uses Send window wire against Sink in (with B9 and C7), not Mesh stats clientOut.
- A19: Acceptance 3's bot passes -meshBotNetSeconds 5, and its Bot net window is checked.
- A20: Added Decision 11 (departures from the sheet, CE-13 naming).
- A21: Gave the golden frame's inputs and added the 27-byte realistic-timestamp case (decode recomputed).
- B1: test_rigrun switches to fictitious pending keys (verified: the "every regress key" case would raise on MESH_PHANTOM).
- B2: Merged into acceptance 4 (absolute gate_ms = 0.01 × arm A's work, matching p0accept.overhead's signature).
- B3: Merged into posErr (A2).
- B4: Acceptance 2 sets MESH_METER_SENDS=count and V. Acceptance 3 sets MESH_NO_MONSTERS and expects 201. VIEW is required.
- B5: accept_step5.py drives Play, launches the bot or sink from the logged centre and port, and checks the bot log's unknown-asset strings.
- B6: Applied through A12's inscribed square. Its tighter square-pitch spacing was not adopted, so A6's pinned counts stay valid.
- B7: Added the Unity-null rule. Awake normalises a fake null. Build tests Inner with `== null`.
- B8: Pinned exact Send, Phantom, Sink and Bot net lines (ASCII `us`, a stamped Sink line, UTC `at` on the Bot net line).
- B9: Acceptance 5 is defined on Send window wire and Sink window in.
- B10: Merged into A7.
- B11: k moved to the factory. The binder re-finds the transport after every restart.
- B12: Build guards re-entry instead of walking the chain.
- B13: Both CE-13 log checks land with CE-13. The wording is corrected.
- B14: The DROPPED_KEYS reason reads after "was". The docstring, comment and test name are updated.
- B15: The wire baselines advance with the meter window, also on retired cells (with C17).
- B16: MESH_METER_SENDS logs only when set (with C16).
- B17: Added sink-start and sink-stop with their own arguments and process filters. Cells and the sink are told apart.
- C1: Merged into acceptance 4.
- C2: Added public total accessors and integer totals in the Send window line.
- C3: Added the accounting rule for refused and TooBig sends on both sides.
- C4: k is held by the factory, with a test (next id 1_000_003 after a rebuild).
- C5: Inscribed-square motion region. Noted the filler `Kinematic walk:` lines against p0checks.
- C6: Merged into posErr (an earlier-frame upload; teleport-waiting phantoms excluded).
- C7: Merged into the integer wire and in fields and acceptance 5.
- C8: Added per-phantom swallowed bytes and the down distribution (Phantom and Sink windows). Whether that is an acceptable G1 proxy is a question.
- C9: Rig time gains frames and ms/frame, stamp pairing and the subtraction rules.
- C10: Merged into acceptance 3.
- C11: Added the error-line prefixes rule and accept_step5.py's check, including `Too Big Packet`.
- C12: The IsTeleport frame carries the server's destination. The walk is re-seated.
- C13: On a Dynamic map the binder binds nothing, with a warning (Decision 11).
- C15: Merged with A3. Added the rebind rules for sink indexes.
- C16: Merged with B16.
- C17: The meter window is taken after :148 and before :150, with the wire baselines.
- C18: Band edges shrink by max(ghostBandWidth, R) + 1 + r.
- C19: Filler ids, names and tracking, and what Work window owned players counts.
- C20: Added a reported 400 × V 100 run and the DESIGN 0.74 note on CE-12's per-write Record.
- T1: Added the off-thread, TooBig, overflow, undefined-delivery, channel-overflow, no-allocation, two-factory-cycle, repeated-Build and empty-setting rows.
- T2: Added the transport rows (k continuity, caps, disconnect reasons, collision order, RTT, reader lifetime, truncated hello, StopServer).
- T3: Added the upload, layout and settings rows and a public static installer Splice with tests. The 400→200 STEPS row is replaced by the character-STEPS refusal row.

Skipped:
- A22: Aligning the binder to LoadTick needs a public counter on MeshMapNetworkManager, a second default-path change that the default-off rule forbids. C9's stamp pairing is applied instead, and the counter is a question.
- C2 (part): A `LastSendWindow` property has no step-5 consumer. Step 6 can add it when its sender needs the window in process.
- C14: STEPS is refused with the character kind (A7), so V never moves with P and no filler refill is needed.

## Questions for the user
1. ANSWERED (2026-10-01): F2's fighting phantoms use the load generator's server-side fight code (`MESH_PHANTOM_FIGHT`, Decision 6); with it, step 5 adds three keys to §0.16 and test_p0tools' counts become (20, 25, 2).
2. Phantom/Work window alignment: the contract pairs each Phantom window with the latest Work window by log stamp, and subtracts rig time from per-frame means only. Do you prefer an exact alignment instead? That would add a public read-only window counter to MeshMapNetworkManager, incremented in LoadTick: a second code change on the default path, though no log line changes.
3. Per-client downlink for the G1 gates on virtual rows: is payload bytes per phantom (writer.Length, no LiteNetLib headers) an acceptable stand-in for "downlink p95 ≤ 100 kB/s per client"? Or must G1 downlink verdicts come only from sink rows (wire bytes)?
