# Step 8 survey: MESH_PREWARM, MESH_SHIP_PARK_SLEEP and the rest of stage 0b's rig C# (read-only, 2026-10-04)

(S) = speculation; (E) = engine behaviour not verified. Mesh/ and TestMap/ compile into one assembly
(Assembly-CSharp, `step6_design.md:26`), so rig code can call Mesh/ `internal` members such as
`MeshMapNetworkManager.OnGhostSnapshot` (`Mesh/MeshMapNetworkManager_Ghosting.cs:1488`) and `TrackSyntheticEntity`
(`:110`).

**Bottom line:** two pieces of rig C# are new: `MESH_PREWARM` (only its grammar and one table row are specified) and
`MESH_SHIP_PARK_SLEEP` (cause found in the space code; meaning to decide). Range-change and group-arrival bursts have no
code. Everything else stage 0b needs exists.

## 1. MESH_PREWARM
- Grammar §0.16 `<kind>:<N>@<t>[/<perFrame>]` (`phase0_build_sheet.md:266`; its "7" step column is stale). b8:
  "B6, pre-warm and subscription bursts | N {100, 300, 650} × {steered, kinematic, ship}; 650 at 50/frame; 650 with 100
  eyes; plus range-change and group-arrival bursts (gap D) | 36 runs | 1.2 h" (`:958`); gap D = "subscription bursts →
  b8" (`:1307`). Purpose (`:1181`): "The pre-warm gate is paced from B6 (ms per puppet, frames over budget)" — slice 2b's
  split pre-warm, when a neighbour spawns many puppets at once. Outputs: **ms per puppet** and **frames over budget**.
  Step 8 tools: `p0load.py prewarm` ("B6 and bursts", `:869`) and a "Prewarm" line parser (`:873`); no line format is
  written anywhere. Step 7 decision 16: "built on the feeder" (`step7_design.md:66`). Step 5: host-mode eyes may follow
  "prewarm objects" (`step5_design.md:69`). Stale: `p0plans.py:60` `"prewarm": "stage 0b, step 7"`.
- Reading (S): kind steered | kinematic | ship; N puppets; t seconds after the cell is ready; perFrame spawns per frame
  (absent = all N in one frame).
- **Puppet feeder** (`TestMap/Phase0/ServerMeshPuppetFeeder.cs`, 446 lines; `_Window.cs` 78): `AutoStart` reads the key
  and requires `ARG_START_MAP_SERVER` (:81-102); `TryStart` waits for `IsServerReadyToInstantiateObjects` (:163-188);
  spawns all N in one frame, unpaced and unmeasured (:177-180); ship spawn `GetObjectInstance` → `Configure` →
  `NetworkSpawn` → `PuppetHeld` → `ServerMeshShipPuppetBody.Begin` (:190-237); "character" = a full server player via
  `ServerMeshShipLoad.SpawnServerPlayer` (`Ships/ServerMeshShipLoad_Pilots.cs:108`) + `MeshKinematicCharacterBody`
  (:239-285), not ghostified, no steered variant; sender retry (:287-309); teardown (:422-444). Covers {kinematic-ish,
  ship}, not steered.
- **Real ghost spawn path:** `OnGhostSnapshot` (Ghosting:1488-1561) → `SpawnGhostEntity`/`Core` (:1872-1915:
  `Assets.NetworkSpawn` + `SetupByMetaData`) → `MeshIdentity` → `GhostSuppression.Ghostify` (:1526) → hit hook → either
  `EnsurePuppetMotion` (kinematic, `Mesh/MeshMapNetworkManager_Puppets.cs:105-144`, sender `Register` :140) or
  `SteerGhost` (steered, :1928-1973); the choice is the process-wide `MESH_PUPPET_MOTION` (Puppets:44-75); every spawn
  logs a "Ghost spawned …" line (:1538), which a real 650 pre-warm pays too.
- **Option (S):** a rig pre-warmer calling `mesh.OnGhostSnapshot(fakeConnectionId, msg)` measures the exact neighbour
  path. Caveats: re-feed within `ghostStaleSeconds` = 2 s (:48, expiry :938); an unknown connection gives
  `OwnerZoneId "?"` (:1524) and a zero frame offset (:1486); hits forwarded to a fake connection (E); kind cannot differ
  from the process's `MESH_PUPPET_MOTION`.
- **Subscribers ("650 with 100 eyes"):** `MESH_PHANTOM=virtual:100:eye` (`TestMap/Phantoms/ServerMeshPhantomEye.cs`,
  hidden identity following a host); with `MESH_PHANTOM_VIEW=0` (host mode) they follow movers via `PickHost`/
  `RefreshHosts` (`Phantoms/ServerMeshPhantomClients_Spawn.cs:201-246`, periodic re-scan of
  `EntityMovementDataHandlers`); the kit's spawn subscribes ready players in range at once (`ServerMeshPhantomEye.cs:27`),
  so a burst costs N × eyes spawn messages; eyes need hosts near the burst before t, e.g. `MESH_LOAD_PLAYERS`/
  `MESH_LOAD_AREA` (S).
- **Measurements today:** Work ticks line (`aoi … max`, `late … max`, `kit max`, `gc0`; `Mesh/MeshTickWindow.cs:131`);
  Work window `workMaxMs`, `workP95Ms` (`Mesh/MeshWorkProbe.cs:39-61`); Send window (spawn bytes). No "frames over
  budget" or "ms per puppet" counter: the pre-warmer times its own spawn loop and per-frame work across the burst
  against `MeshWorkProbe.EffectiveBudgetMs` (:520).
- **Burst variants:** N at once (one frame); N at 50/frame (`/50`); 650 with 100 eyes; **range-change burst — no code**
  (the AOI uses the target's range, `step7_design.md:46`; range is set only at start, `MESH_BAND` →
  `InterestManager.defaultVisibleRange`, `Mesh/MeshMapNetworkManager_Load.cs:92-106`, and per ship,
  `Ships/ServerMeshShipEntity.cs:156`; a runtime change at t is missing, S); **group-arrival burst — no code** (the load
  generator has no group or teleport, `ServerMeshLoadGenerator.cs:19-24`; phantom arrivals are paced,
  `MESH_PHANTOM_ARRIVAL_PER_S`, `ServerMeshPhantomSettings.cs:30,49`; S).

## 2. MESH_SHIP_PARK_SLEEP
- Sheet `MESH_SHIP_PARK_SLEEP | 0 | 1` (`:261`); space question 5 "parked bodies that may never sleep (E)" (`:1245`);
  b5 (`:955`) lists no parked rows; step 7 decision 16 assigns "parked-ship rows of b5/b8".
- **Why parked ships never sleep in the space game** (c2eb4154; also at 1a3a58d9): `ShipLander.FixedUpdate`
  (`mmorpg-kit-space/Assets/_Game/Scripts/Space/Ships/ShipLander.cs:280-366`) runs on the simulating peer (the server for
  unowned ships) and, `Landed`, pins `_rigidbody.position/rotation` and zeroes `linearVelocity/angularVelocity` every
  physics step (:362-365); setting pose or velocity wakes a PhysX body, so it never sleeps (E).
- **Rig today:** parked ships are dynamic on the server (`Simulates`/`RefreshSimulationMode`,
  `Ships/ServerMeshShipMovement.cs:251-264`); each FixedUpdate adds the engine's torque and force (:240-249), exactly
  zero for a parked ship (no `SetControl`; spool lerps from 0, `ServerMeshShipEngine.cs:66-88`); nothing pins the pose;
  they float 20 m + 3 m × scale up (decision 25; `ServerMeshShipLoad.cs:263-272, 426`). A zero force does not wake a
  body per Unity docs, so today's stand-ins probably sleep (E) — the opposite of the space game.
- Plausible meaning (S): one value reproduces the space pin each step (never sleeps), the other lets the body sleep (or
  `Rigidbody.Sleep()`); rows compare per-parked-ship physics and engine cost; a sleeping count (`IsSleeping()`) as an
  optional Ships window group. To decide: which value is today's behaviour (default byte-identical); whether the
  zero-force `AddRelativeForce` is skipped while asleep; `IsAtRest` (`ServerMeshShipMovement_Sender.cs:32`) unaffected.
- Code home: `ServerMeshShipSettings` (`ShipKeys`, `TryResolve`, `Ships/ServerMeshShipSettings.cs:53, 113-145`); the
  `Ships:` setting line (`ServerMeshShipLoad.cs:202`, appended only when set); `ServerMeshShipMovement.cs` is 482 lines →
  a new partial (`ServerMeshShipMovement_Park.cs`); a ship-kind pre-warm counts as a ship key, as `FeederKey` does (:49,
  :13-15).

## 3. Other stage 0b needs
| Need | Status |
|---|---|
| b7 thread and datagram saturation | Instruments exist: `MESH_THREAD_CPU` (`Phase0/ServerMeshThreadCpu.cs:46`); server sent datagrams in the Send window (`Mesh/MeshMapNetworkManager_Load.cs:183`, `MeshSendMeter.cs:462`); Sink window (`Phantoms/ServerMeshPhantomSink.cs:128-156`); Bot net datagrams (`ServerMeshAutoClient.cs:231,309`); packet cap `MESH_SENDER_PACKET_BYTES` (`p0tools.py:77`). "Saturation" is analysis in `p0load packets` (S). |
| b9 sleeping neighbour woken by a crossing | Exists: `MESH_SLEEP`, `_RINGS`, `_AFTER` (`Mesh/MeshSleep.cs:14-16`); boundary wake request (`MeshMapNetworkManager.cs:948, 1127-1135`); central logs it (`MeshCentralNetworkManager.cs:272`); regress `wake` (:839-858); "Handoff ACK … ms" (Ghosting:3412). No wake-latency line: `latency_ms{wake}` from log stamps (S). Phantom cells refuse to sleep (`step5_design.md:386`), so b9 uses bots. |
| classify-every-10-builds | Exists: `MESH_SENDER_CLASSIFY_BUILDS` (`Mesh/Sync/MeshStateSender.cs:51, 235`). |
| sync/RPC D {0,250} × R {0, 2100/s} | Exists: `TestMap/Phase0/ServerMeshSyncLoad.cs` (287 lines; D ≤ 10,000, R ≤ 100,000); loads only the cell's load ships (:16-20), so needs `MESH_LOAD_SHIPS`. |
| 60 fps subset | Exists: `MESH_RATES` (`Phase0/ServerMeshRates.cs`). |
| Eye hiding | Exists: `ServerMeshPhantomEye.cs`; `Tests/Editor/ServerMeshPhantomEyeHideTests.cs`. |
| Meter overhead | Exists as an A/B of `MESH_METER_SENDS` off/count/time (`Mesh/MeshMeteredTransportFactory.cs:19, 49`). |
| Range-change, group-arrival bursts | **Missing** (§1). |
| Prewarm line | **Missing** (no C#, no format). |

## 4. Constraints for new rig C#
- Size (`step7_design.md:24`): under ~500 lines. Over and not to grow: `ServerMeshPhantomSettings.cs` (605),
  `ServerMeshPhantomClients.cs` (530), `MeshSendMeter.cs` (557), `ServerMeshAutoClient.cs` (1457, not edited at all),
  `p0tools.py` (518), `regress.py` (1188, ENV_KEYS only). `MeshStateSender.cs` (665), `MeshStateFanout_Routing.cs`
  (510): only lines a contract names. Not edited: `ServerMeshLoadGenerator.cs` (499; private `Spawn`/seed helpers — copy
  the recipe, :25). Near the limit (new partials): `ServerMeshShipMovement.cs` 482, `ServerMeshPuppetFeeder.cs` 446,
  `ServerMeshShipLoad.cs` 440. `MeshMapNetworkManager_Ghosting.cs` (3682) is shipped: any hook game-neutral, default
  off, no "ship".
- Partials: `ServerMeshPuppetFeeder` (`sealed partial` + `_Window.cs`), `ServerMeshShipLoad` (`_Pilots`, `_Window`),
  `ServerMeshShipMovement` (`_Sender`); one MonoBehaviour per file named after the class.
- Settings pattern: no separate `…Setting` class; `public static bool TryParseSetting(string text, out …, out string
  error)` on the component (`ServerMeshPuppetFeeder.cs:108-144`: `EnvironmentKey`, `MaxCount`, `MaxHz`;
  `NumberStyles.None`, invariant; errors name the key and part; `ServerMeshSyncLoad.cs:81-130`); tests
  `ServerMeshPuppetFeederSettingTests.cs`, `ServerMeshSyncLoadSettingTests.cs`. Install
  `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)] AutoStart`: silent when unset, requires `ARG_START_MAP_SERVER`, logs
  `"<Prefix> error: …; <consequence>"` on bad input, makes `DontDestroyOnLoad` `"[Mesh] <Name>"`
  (`ServerMeshPuppetFeeder.cs:81-102`).
- Lines: one-off setting line (`"Feeder: ship 200 @ 20 Hz on cell C"`) and a window line when
  `_mesh.WorkWindowCount` changes (`_Window.cs:22-40`; `"Feeder window 5.00 s: … | ms/s … | us/snapshot … | gc0 N"`),
  StringBuilder, invariant fixed formats, negative/non-finite → 0. One physical line, no braces, never "Exception"
  (`MeshStateSender.CleanForLog`); `<X> error:` fails acceptance; pure code logs through `IMeshLog`; `== null`.
- Default off: keys unset → no component, no line; Ships/Feeder/Send/Sender lines byte-identical.
- Python: step 7's meanings and parsers went in `p0ships.py` (`STEP7_REGISTRY`, merged at `p0tools.py:81`); step 8's
  belong in a similar new module (S). `PENDING_KEYS` at `p0tools.py:86-89`; `test_p0tools.py:305-327` pins both keys as
  pending and lists §0.16 keys literally.
