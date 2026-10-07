# Shared Phase 0 build sheet: measuring what the fantasy and space games need

This work was read-only. I created, edited, moved or deleted nothing in either repo or anywhere else.

**Labels:** (C) read from code, (M) measured, (E) estimate, (A) an assumption or a choice of ours.

**Paths**

| Alias | Location |
|---|---|
| `A:` | `C:\dev\Game Design\mmo-server-meshing\` |
| `Mesh/` | `A:Assets/ServerMeshing/Mesh/` (shipped add-on) |
| `TestMap/` | `A:Assets/ServerMeshing/TestMap/` (rig; packaged as its own add-on, `ServerMeshPackageBuilder.cs:92`) |
| `Tests/` | `A:Assets/ServerMeshing/Tests/Editor/` (not packaged) |
| `Tools/` | `A:Tools/ServerMeshing/` |
| `Kit/` | `A:Assets/OpenMMORPG/` |
| `LNL/` | `Kit/ThirdParty/LiteNetLibManager/Scripts/` |
| `LNLib/` | `Kit/ThirdParty/LiteNetLibManager/Plugins/LiteNetLib/` |
| `B:` | `C:\dev\Game Design\mmorpg-kit-space\` (read only, commit 1a3a58d9) |
| BGNM | `Kit/Core/Scripts/Networking/BaseGameNetworkManager.cs` |
| BIEMF3D | `Kit/Core/Scripts/Gameplay/EntityMovementSystems/Implements/BuiltInEntityMovementFunctions3D.cs` |
| NMEM | `.../EntityMovementSystems/NavMeshEntityMovement.cs` |
| CCEM | `.../CharacterControllerEntityMovement.cs` |
| BGE | `Kit/Core/Scripts/Gameplay/BaseGameEntity.cs` |
| LNGM | `LNL/GameApi/LiteNetLibGameManager.cs` |
| LNI | `LNL/GameApi/LiteNetLibIdentity.cs` |
| LNP | `LNL/GameApi/LiteNetLibPlayer.cs` |
| SS | `LNL/GameApi/LiteNetLibGameManager.StateSyncing.cs` |
| JGAOI | `Kit/Core/Scripts/Networking/AOI/JobifiedGridSpatialPartitioningAOI.cs` |
| Ghosting | `Mesh/MeshMapNetworkManager_Ghosting.cs` |
| SMC | `B:Assets/_Game/Scripts/Space/Ships/ShipMovementComponent.cs` |
| Scratch | `C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\7cb7243d-6d65-4487-a82f-31711ade3249\scratchpad\` |

---

## Summary, including where I disagree

1. **Phase 0 comes in three stages, not one batch.**
   - **0a** needs no phantom clients. It covers:
     - the measuring instrument (rates, tick-tagged probe, AOI timing, thread CPU);
     - kinematic character puppets and the movement-handler slot owner;
     - the load generator's motion modes;
     - the Python umbrella tools.
   - 0a answers the character kill rule, the N² term and the fantasy geography first.
   - **0b** adds the transport chain with phantom clients, the state sender and kit adapter, and the ship stand-ins. It answers G1 and G2.
   - **0c** is the database burst, the fantasy two-process A/B, the isolated-spaces floor and packet loss.
2. **Effort (E).**
   - Build: about 2–3 weeks for 0a, 3–4 weeks for 0b and about 1 week for 0c.
   - Rig time: about 37 h, of which about 31 h is measurement plus about 20% re-runs, spread over five overnight chains and two attended two-machine sessions.
   - **I disagree with the proposal's "about 2–3 weeks" for all of Phase 0.** The four parts together specify roughly 6–9 weeks of code.
3. **CE-12 is not needed for the kit adapter to be correct. On this point I disagree with the task text.**
   - The kit loop only ever sees the wrapper, which returns false (BGNM:703-704).
   - The real `WriteServerState` then runs once per build from the sender, so its per-call clears (BIEMF3D:1375-1378) become per-build clears.
   - CE-12 is still a prerequisite in two places:
     - the character kit-against-sender A/B, so that both arms send the same records to the same observers;
     - any entity that stays on the kit path.
   - Its shape is settled (2026-09-30, superseding the first draft of §0.9): the space team's post-pass, which the adapter's own pass makes safe (§0.9).
4. **The space team's contract promise is kept.**
   - File `Mesh/Sync/MeshSyncContracts.cs` with its `.meta` GUID.
   - `IsAtRest` and the three Mark calls are included.
   - `MeshSendRate` keeps its 2-argument constructor and gains a 3-argument one.
   - The engine ports move out of the contract file.
   - `MarkShipChanged` becomes `MarkSourceChanged` (neutral naming). We tell the space team before they integrate.
5. **The critique is applied item by item** (§10): one phantom chain with two observer kinds, one owner of the movement-handler slot, one name and env registry, AOI-tagged tick statistics, gates in ms/s, the MeshLoadReport wire format frozen, and new defaults gated.
6. **On "far smaller" fantasy maps, geographic split/merge buys little, and the user should know this before planning around it.**
   - At the kit's 80 m band, halving pays only for cells at least about 4.2·k·B wide: about 340 m today (k ≈ 1), about 170–240 m with kinematic puppets (E).
   - The validator refuses any cell narrower than the band (C: `Mesh/MeshLayoutValidator.cs:185-190`).
   - A map under about 0.35 km gains nothing from splitting today. A map under about 0.7 km gains at most one halving.
   - Small maps are served by the rest of the shared core: the sender, rates, per-map population caps, sleeping cells, and later the add-on's isolated-spaces policy for dungeons and interiors.
   - Phase 0 measures what those need: F1/F2, the process floor and the per-space floor. §6.4 has a map-size table.
7. **New measured input (M, `Scratch/chain4_ce11_wide_crowd.log`, BRENDOSERVER).**
   - Setup: 250 players with all monsters, a 150 m crowd centred on the cut (`abload ab --load 0,0,150`), six repeats, every run accepted.
   - Result: halves/coarse was 0.75–0.81 (mean 0.78). A crowd about 2B wide straddling a cut gets under 25% relief.
   - So the planner must cut away from crowds.
8. **The Slice 2a gate has not passed yet** (`Scratch/chain4.log` at the time of reading).
   - `gate_static`: `crash` failed 50/51 ("zone E: 1 PLAYER puppet teleport(s)").
   - `gate_dynamic_idle`: `wake` failed 41/42 ("zone C2: 9 PLAYER puppet teleport(s)").
   - `gate_dynidle` was still running.
   - Phase 0 code cannot start until the user decides on these (§1, step 0).

---

## Use cases this Phase 0 serves

| Use case | Partition model | What Phase 0 measures for it | Decision it feeds |
|---|---|---|---|
| Fantasy open world (8 km × 8 km today) | Geographic X/Z cells, Dynamic per map | k and e for steered, kinematic and inert character puppets; awake monsters; N²; client-driven owners; crossings; geographic A/B with strips4 | Base cell, split depth, planner settings; slices 2b–6 re-planned (§8) |
| Fantasy small maps (the user's "far smaller") | Static single cell, or at most one halving | Process floor at 30/60 fps; F1 town and F2 boss on one process; sender tiers; the map-size table | Per-map mode (Static/Dynamic), population caps, sender tiers, visible range |
| Fantasy dungeons, interiors, instances | Isolated spaces (the add-on policy after Phase 0) | Per-space floor (B11), subscription bursts | Placement rules of the isolated-spaces policy |
| Space star systems | Bubbles, whole bubbles per process | c_pair for ships, kit residual, per-packet and network-thread cost, ship puppet e and k, G1 and G2 at 30/20/50 | Bubble directory and placement, sender Phase 1 (BubblePolicy is the space game's, built from our spec, contract §L.11); workers only if G3 passes |

---

## 0. Rules for every step

**0.1 Start and freeze.**
- No file changes while a rig chain runs: any `.cs`, `.asset` or `.prefab` change makes the player build stale.
- Phase 0 code starts only after:
  - the Slice 2a gate passes (or the user accepts its known failures);
  - CE-11 is confirmed;
  - package 0.72.0 is built.
- Prefabs (eye, ship) are made only in a build window.
- Each stage is built as one batch, then one player build.
- Any code fix means a rebuild, the acceptance runs and a canary. Rows from different builds are never pooled without a canary bridge.

**0.2 Language.**
- C# 9 on .NET Framework 4.7.1: no records, no `init`, no default interface members. `readonly struct` and `in` are allowed.
- Every public type and member gets a comment starting with its name.
- Errors are handled with context:
  - constructors throw `ArgumentOutOfRangeException` or `ArgumentNullException` naming the field;
  - logs name the object id, type and connection;
  - nothing is swallowed.
- No allocation in per-tick paths (pooled writers). `gc0` per window is logged.

**0.3 Tests.**
- NUnit EditMode tests go in `Tests/`, namespace `MultiplayerARPG.MMO.Mesh.Tests`.
- Tables use `TestCaseSource` with `TestCaseData.SetName`, failure cases first. Existing classes are extended where they exist (`MeshWorkMeterTests` and `MeshWorkHistogramTests` in `MeshMeasurementTests.cs`, and `MeshLoadReportTests.cs`).
- Tests compile into Assembly-CSharp-Editor. Kit `internal` members, such as `SendServerEntityMovementState` (BGNM:661), are not visible to them. So tests use copies: `MeshTickScheduleCopy`, and a packet parser copied from BGNM:589-618.
- Python tests use `unittest` with `cases = {name: (input, expected)}` plus `subTest`, failure first, as in `test_rigtools.py:12-24`.
- Lint and test after every step. Never commit or push unless the user asks.

**0.4 Game-neutrality and reserved words.**
- Mesh/ never names a game type, a TestMap type or a Tests type, and never uses the word "ship".
- The stray-reference check (`ServerMeshPackageBuilder.cs:225-245`) reserves, for Mesh/, every type declared in TestMap/ or Tests/. It counts comments and is case-sensitive.
- Reserved today: Mode, Placement, Callbacks, Driver (`MeshMeasurementTests.cs:56` declares `Driver`) and MeshFakeClock.
- Mesh/ writes "the mode byte" and "driver" in lower case and never has a member named `Mode`.
- Every new TestMap and Tests type in §0.15 joins that list.

**0.5 Shipped against rig code.**
- Shipped features default off. When off, they behave byte-identically, and Static maps run today's code.
- Rig code goes in TestMap/. It compiles into Assembly-CSharp and may use Mesh/ types, never Tests/ types.
- Rig type names use the existing `ServerMesh*` prefix (`ServerMeshAutoClient`, `ServerMeshLoadGenerator`), not the protocol part's `Rig*`.

**0.6 Never change demo defaults.**

| Default | Where |
|---|---|
| `updateFps` 20 | `MapNetworkManager-CE.prefab:139` |
| 60 fps when headless | `GameInstance-CE.prefab:230`; `GameInstance.cs:1461-1468` |
| Fixed step 0.03 s | `ProjectSettings/TimeManager.asset:6` |
| `visibleRange` 0 | `BaseCharacter-CE.prefab:2613` |
| `movementSecure` 0 | `BaseCharacter-CE.prefab:2510` |
| `updateAnimationAtServer` 1 | `GameInstance-CE.prefab:226` |
| `maxConnections` 1100 | `MapNetworkManager-CE.prefab:140` |
| `transportFactory` none | `MapNetworkManager-CE.prefab:141` |

- Only environment toggles change behaviour.
- Every new key goes into `regress.ENV_KEYS` (`Tools/regress.py:59-64`), because each Play request writes every key and clears the rest.
- Each toggle's default is today's behaviour.

**0.7 Rates.**
- Fantasy rig: 60 fps / 20 Hz / 0.03 s (the demo). The fantasy game's own rates are unknown, so fantasy cases run at both sets.
- Space: 30 fps / 20 Hz / 0.02 s.
- One toggle, `MESH_RATES=<fps|u>/<fixedStepSeconds>`. The step is given in seconds (critique 7d).
- The network tick stays 20 Hz for both games, so `updateFps` is never changed.
- Runs are capped by default.
- Every figure is reported in ms/s and in ms/frame at the stated rate, with a CPU label. Today that is BRENDOSERVER, a Ryzen AI 9 HX 370. These figures are not comparable with 0.53–0.69, which ran on the MAX+ 395.

**0.8 Repo B is read only.**
- Model it by reading.
- No `using Game.*`, no asset or asmdef reference, no copied source text, and nothing from Space Combat Kit.
- Each stand-in file names the commit it models (1a3a58d9).
- Layouts are kept in step by the shared golden fixture, not by their code.
- Findings go to the space team in writing (§9.2).

**0.9 CE-12 (the space team's patch; our kit carries it).**
- **Shape: the space team's post-pass** (their kit patch #9, accepted 2026-09-30; upstream patch 0006, PR #3, bb9895f).
  The first draft of this section accepted only a stamp-in-handler shape; that was superseded and the space team was told
  so again on 2026-10-05 (`space_reply_2026-10-05.md` A.3).
  - The kit's send loops `Record` each `IEntityMovementServerStateFlags` handler whose write succeeded and `ConsumeAll`
    after their last player; writes only read the flags.
  - Why it is safe with the sender: the adapter's kit-facing `WriteServerState` returns false, so the kit loop never
    records a wrapped handler. The sender runs its own `EntityMovementServerStateFlagsPass` (`MeshStateFanout`): `Begin`,
    `Record` the inner handler after its one write, `ConsumeAll` after the fan-out. The two sets never overlap.
- It includes NMEM's missing server-side `_sendingDash` clear (in its consume) and the `OnTeleport` interpolation reset.
- CE-12 is marked `// MESH-EDIT: CE-12`, with a DESIGN §17 row and an upstream patch.

**0.10 The load-report wire format is frozen.**
- `MeshLoadReport` has no version, and Deserialize reads a trailing optional grid (`Mesh/MeshLoadReport.cs:97-103`).
- Every new Phase 0 number goes on log-only lines.

**0.11 Preflight.**
- RelicTestServer must be stopped, which needs admin (user action).
- The MySQL database is shared with Relics, so the provisioner writes only rig rows.
- `rigtools.build_staleness` and `regress.preflight` run before every chain.

**0.12 Records.**
- DESIGN rows (0.73 for 0a, 0.74 for 0b, and so on) are written from measured rows. Estimates keep their (E) label.

**0.13 Handler slot.**
- Only `MeshMovementHandlerSlots` (step 3) may replace an entry in `EntityMovementDataHandlers` (BGNM:108-109) for Mesh/ purposes: the kit adapter, the kinematic body and the rig ship stand-in's registration check.

**0.14 One results schema** (§7) for both teams and both games.

**0.15 Name registry.** Every name below is new. TestMap and Tests names become reserved words for Mesh/.

| Area | Names |
|---|---|
| **Mesh/** | `MeshHandoffContracts.cs` (contract only, 2026-10-05): IMeshHandoffService, MeshHandoffResult, MeshHandoffOutcome, MeshArrivalInfo |
| | `MeshSyncContracts.cs`: IMeshStateSource, IMeshCompactStateSource, IMeshStateRegistry, IMeshSendPolicy, MeshSendRate, MeshObserverContext, MeshStateVariant |
| | `Sync/`: MeshStatePacketWriter, MeshStateFanout, MeshStateFanoutSettings, MeshStateFanoutCounters, IMeshStateTransport, IMeshStatePositions, MeshStateSender, MeshKitMovementStateSource, MeshDistanceSendPolicy, MeshStateSenderStats, MeshMovementHandlerSlots, IMeshReplaceableHandlerLayer |
| | Top level: MeshPuppetMotion, MeshPuppetMotionSetting, MeshKinematicTrack, MeshKinematicCharacterBody, MeshTickWindow, MeshSendMeter, MeshSendWindow, MeshMeteredTransport, MeshMeteredTransportFactory, IMeshWrappedTransport, MeshTransportUnwrap |
| **TestMap/** | ServerMeshRates, ServerMeshRatesSetting, ServerMeshTimedAoi, ServerMeshAoiCountdown, ServerMeshThreadCpu, ServerMeshThreadClass, ServerMeshKinematicWalk, ServerMeshServerAnimation, ServerMeshCe12Check |
| | ServerMeshPhantomTransport, ServerMeshPhantomTransportFactory, ServerMeshPhantomInstaller, ServerMeshPhantomSettings, ServerMeshPhantomClients, ServerMeshPhantomUploads, IServerMeshPhantomPayload, ServerMeshPhantomTransformPayload, ServerMeshPhantomInputPayload, ServerMeshPhantomShipPayload, ServerMeshPhantomLayout, PhantomGroup, ServerMeshPhantomEye, ServerMeshPhantomSink, ServerMeshPhantomWindow |
| | ServerMeshShipEntity, ServerMeshShipMovement, ServerMeshShipAuthority, ServerMeshShipWire, ServerMeshShipEngine, ServerMeshShipPilot, ServerMeshShipCombat, ServerMeshShipProjectile, ServerMeshShipLoad, ServerMeshShipMirrorFeeder, ServerMeshShipFlightPath, ServerMeshShipScale, ServerMeshShipPuppetBody |
| | ServerMeshPuppetFeeder, ServerMeshSyncLoad, ServerMeshDbBurst, ServerMeshSpaces |
| **Tests/** | MeshFakeStateSource, MeshFakeCompactStateSource, MeshFakeKitHandler, MeshFakeStampedKitHandler, MeshFakeStateTransport, MeshFakeStatePositions, MeshFakeTransport, MeshEntityStatePacketParser, MeshTickScheduleCopy, MeshSyncGoldenFixture |

`StandIn` already exists (`Mesh/ClientMigrationController.cs`), so no rig type may use it.

**0.16 Environment registry.** Every key goes into `regress.ENV_KEYS`. The default is always "unset = today".

**Mesh/ keys (shipped, default off):**

| Key | Values | Step |
|---|---|---|
| `MESH_PUPPET_MOTION` | steered \| kinematic | 3 |
| `MESH_WORK_BINS` | 0 \| 1 (log non-empty bins) | 2 |
| `MESH_METER_SENDS` | off \| count \| time | 5 |
| `MESH_SENDER` | off \| measure \| on | 6 |
| `MESH_SENDER_SOURCES` | kit,direct | 6 |
| `MESH_SENDER_TIERS` | near/mid/far/owner Hz (default 20/20/20/20) | 6 |
| `MESH_SENDER_R0` | metres (default: kit visible range) | 6 |
| `MESH_SENDER_COMPACT` | off \| far | 6 |
| `MESH_SENDER_SPREAD` | 0 \| 1 | 6 |
| `MESH_SENDER_XZ` | 0 \| 1 | 6 |
| `MESH_SENDER_CLASSIFY_BUILDS` | n (default 4) | 6 |
| `MESH_SENDER_PACKET_BYTES` | 1020 \| 510 \| 255 | 6 |
| `MESH_SENDER_RESIDUAL_PROBE` | 0 \| 1 | 6 |
| `MESH_MONSTER_CELL` | cellId (rig use, like `MESH_NO_MONSTER_HANDOFF`) | 9 |

**TestMap/ keys (rig only):**

| Key | Values | Step |
|---|---|---|
| `MESH_RATES` | `<fps\|u>/<step s>`; `MESH_LOAD_UNCAPPED=1` stays as an alias for `u` | 2 |
| `MESH_UNCAPPED_CELLS` | cell ids | 2 |
| `MESH_AOI_TIMING` | 0 \| 1 | 2 |
| `MESH_THREAD_CPU` | 0 \| 1 | 2 |
| `MESH_PROBE_BURN_TICK_MS` | ms | 2 |
| `MESH_RIG_SERVER_ANIMATION` | 0 \| 1 | 3 |
| `MESH_LOAD_MOTION` | steered \| kinematic \| inert | 3 |
| `MESH_LOAD_FIGHT` | 0 \| 1 | 3 |
| `MESH_LOAD_COLLIDE` | 0 \| 1 | 3 |
| `MESH_PHANTOM` | `<virtual\|sink>:<P>:<character\|eye\|ship>` | 5 |
| `MESH_PHANTOM_VIEW` | V | 5 |
| `MESH_PHANTOM_ARRIVAL_PER_S` | n | 5 |
| `MESH_PHANTOM_UPLOAD_HZ` | Hz | 5 |
| `MESH_PHANTOM_PAYLOAD` | transform \| input \| ship56 \| ship28 | 5 |
| `MESH_PHANTOM_STEPS` | `<P1,P2,..>@<s>` | 5 |
| `MESH_PHANTOM_REGISTER` | 0 \| 1 | 5 |
| `MESH_CE12_CHECK` | 0 \| 1 | 6 |
| `MESH_LOAD_SHIPS` | `<cell>:<n>:<ai\|mirror\|parked>[,...]` | 7 |
| `MESH_LOAD_SHIP_PILOTS` | 0 \| 1 | 7 |
| `MESH_SHIP_WIRE` | legacy54 \| header56 \| compact28 | 7 |
| `MESH_SHIP_SCALE` | factor | 7 |
| `MESH_SHIP_PROJECTILES` | instantiate \| pooled | 7 |
| `MESH_SHIP_SELF_ACK` | 0 \| 1 (default 0: only an owner acknowledges a teleport) | 7b |
| `MESH_SHIP_AI_PREFAB` | enemy \| wave (default enemy: Ship_Enemy numbers) | 7b |
| `MESH_SHIP_PARK_SLEEP` | 0 \| 1 | 8a |
| `MESH_SHIP_JUMP_SECONDS` | s | 7 |
| `MESH_RIG_SHIPS` | 1 (bots register the prefab) | 7 |
| `MESH_PUPPET_FEEDER` | `<kind>:<N>@<Hz>` | 7 |
| `MESH_SYNC_LOAD` | `dirty:<D>,rpc:<R>` | 7 |
| `MESH_PREWARM` | `<kind>:<N>@<t>[/<perFrame>]` | 8a |
| `MESH_MONSTER_PLACE` | `<n>@<x>,<z>,<r>[:passive][;...]` | 8a |
| `MESH_DB_BURST` | `<N>@<t>` | 9 |
| `MESH_SPACES` | S | 9 |

The sink is started from the command line: `-meshPhantomSink host:port:count[:portBase]` with `-batchmode -nographics`.

---

## 1. Steps in build order

### Step 0: Preconditions (no code)

- **Goal:** a clean base to measure against.
- **Actions:**
  1. The user decides on the chain4 failures:
     - `crash`: 1 player puppet teleport in E;
     - `wake`: 9 player puppet teleports in C2.
     - Either they are fixed first, or accepted and characterised by running `crash` and `wake` five times each, which gives a teleport base rate. Step 3's acceptance compares kinematic puppets against that baseline.
  2. Finish `gate_dynidle`.
  3. Fill DESIGN 0.71 (from the draft `Scratch/design_071_draft.md`, including the 0.78 crowd-on-cut point) and 0.72. Bump the package to 0.72.0.
  4. Record the baseline build manifest.
  5. Confirm machine B, its link speed (assumed 1 GbE) and RAM, and that RelicTestServer is stopped.
- **Acceptance:** all 10 scenarios Static, all 10 with `MESH_CELL_MODE=all:Dynamic`, and `dynidle`, pass or are explicitly waived by the user.
- **Depends on:** the running chain.

### Stage 0a

#### Step 1: The contract file (delivered to the space team) and the stray-check blind spot

- **Goal:** freeze the game-neutral sender contract the reply promised. It is Phase 0's first step, and the space team integrates it before anything else.
- **Files:**
  - create `Mesh/Sync/MeshSyncContracts.cs` (Unity writes the `.meta`; its GUID goes in the delivery note);
  - create `Tests/MeshSyncContractTests.cs` and `Tests/MeshSyncGoldenFixture.cs`;
  - edit the regex at `Mesh/Editor/ServerMeshPackageBuilder.cs:227`;
  - edit `Tests/MeshPackageStrayReferenceTests.cs`.
- **Classes and members:**

```csharp
// Mesh/Sync/MeshSyncContracts.cs (proposed; not written)
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>IMeshStateSource is an entity whose server-to-client state MeshStateSender sends. The game owns every byte
    /// WriteState writes and how its client reads them; the sender decides only when WriteState runs and which
    /// subscribers receive an unchanged copy, as an EntityState (119) record.</summary>
    public interface IMeshStateSource
    {
        /// <summary>ObjectId is the network object id and the record key.</summary>
        uint ObjectId { get; }
        /// <summary>Identity is the network identity whose subscribers receive the state; the sender never changes it.</summary>
        LiteNetLibIdentity Identity { get; }
        /// <summary>DiscreteVersion changes whenever every observer must see the next sample (teleport sequence, the mode byte, epoch); never for motion.</summary>
        uint DiscreteVersion { get; }
        /// <summary>IsAtRest is true when a velocity-extrapolating observer would see no change (parked, docked, stopped).</summary>
        bool IsAtRest { get; }
        /// <summary>WriteState appends the complete payload as of sampleTimestamp. It is called at most once per build, only appends
        /// (never Reset or SetPosition), changes no state (a second call writes identical bytes), and returns false to send
        /// nothing this build. sendReliably routes the record to ReliableOrdered for every subscriber.</summary>
        bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably);
    }

    /// <summary>IMeshCompactStateSource adds an optional smaller encoding that its client tells apart by the payload's own header.</summary>
    public interface IMeshCompactStateSource : IMeshStateSource
    {
        /// <summary>WriteCompactState appends the compact payload under WriteState's rules; it is never sent reliably.</summary>
        bool WriteCompactState(long sampleTimestamp, NetDataWriter writer);
    }

    /// <summary>IMeshStateRegistry is the sender's registration surface on the network manager's GameObject.</summary>
    public interface IMeshStateRegistry
    {
        /// <summary>Register hands the source to the sender; false keeps it on the kit path (no sender, grid path enabled, duplicate id).</summary>
        bool Register(IMeshStateSource source);
        /// <summary>Unregister forgets the source (despawn, handoff).</summary>
        void Unregister(IMeshStateSource source);
        /// <summary>MarkRelevanceChanged re-classifies one pair on the next build (lock, attack, group, seat).</summary>
        void MarkRelevanceChanged(uint objectId, long observerConnectionId);
        /// <summary>MarkSourceChanged re-classifies every pair of one source on the next build.</summary>
        void MarkSourceChanged(uint objectId);
        /// <summary>MarkObserverChanged re-classifies every pair of one observer on the next build.</summary>
        void MarkObserverChanged(long observerConnectionId);
    }

    /// <summary>MeshStateVariant is the encoding one pair receives.</summary>
    public enum MeshStateVariant : byte { Full = 0, Compact = 1 }

    /// <summary>MeshObserverContext describes one (source, observer) pair to the policy.</summary>
    public readonly struct MeshObserverContext
    {
        /// <summary>MeshObserverContext creates a pair description.</summary>
        public MeshObserverContext(long observerConnectionId, float distance, bool isOwner)
        { ObserverConnectionId = observerConnectionId; Distance = distance; IsOwner = isOwner; }
        /// <summary>ObserverConnectionId is the receiving client.</summary>
        public long ObserverConnectionId { get; }
        /// <summary>Distance is metres from the observer's anchor (its character; seated: the seat) to the source, 3D or X/Z as the sender is configured; NaN when unknown.</summary>
        public float Distance { get; }
        /// <summary>IsOwner is true when the observer owns the source.</summary>
        public bool IsOwner { get; }
    }

    /// <summary>MeshSendRate is one observer's routine rate for one source.</summary>
    public readonly struct MeshSendRate
    {
        /// <summary>Withhold sends nothing at all, not even urgent, reliable or first-send records.</summary>
        public static readonly MeshSendRate Withhold = new MeshSendRate(0, 0, MeshStateVariant.Full, true);
        /// <summary>MeshSendRate creates a Full-variant rate.</summary>
        public MeshSendRate(byte hz, byte priority) : this(hz, priority, MeshStateVariant.Full, false) { }
        /// <summary>MeshSendRate creates a rate with the encoding for routine records.</summary>
        public MeshSendRate(byte hz, byte priority, MeshStateVariant variant) : this(hz, priority, variant, false) { }
        private MeshSendRate(byte hz, byte priority, MeshStateVariant variant, bool isWithheld)
        { Hz = hz; Priority = priority; Variant = variant; IsWithheld = isWithheld; }
        /// <summary>Hz is 0, 1, 2, 5, 10 or 20 (a whole divisor of the send rate); 0 still receives urgent and first-send records.</summary>
        public byte Hz { get; }
        /// <summary>Priority orders routine records when a budget is short; Phase 0 carries it without a budget.</summary>
        public byte Priority { get; }
        /// <summary>Variant is the encoding for routine records; Full whenever the source has no compact encoding.</summary>
        public MeshStateVariant Variant { get; }
        /// <summary>IsWithheld is true only for Withhold.</summary>
        public bool IsWithheld { get; }
    }

    /// <summary>IMeshSendPolicy decides routine rates; the first found on the network manager's GameObject wins. Classify must not allocate.</summary>
    public interface IMeshSendPolicy
    {
        /// <summary>Classify returns the rate for one pair: when the pair is new, at the classify rate, and after a Mark call or DiscreteVersion change.</summary>
        MeshSendRate Classify(IMeshStateSource source, in MeshObserverContext observer);
    }
}
```

- **Stray regex:** allow `readonly`, `ref`, `record` and `unsafe` among the modifiers, and add `delegate <type> <Name>(`. Today `readonly struct` and delegates in TestMap or Tests are invisible to the check (C: :227). Phase 0 adds several readonly structs, so this goes in here, even though the critique called it a separate task.
- **Tests (failures first):**
  - `MeshPackageStrayReferenceTests`: a Mesh/ script naming a TestMap `readonly struct X`, `ref struct X`, `readonly partial struct X` or `delegate void X(` is reported; the existing three cases still pass.
  - `MeshSyncContractTests`:
    - the file's `using` lines are a subset of {LiteNetLib, LiteNetLib.Utils, LiteNetLibManager} (a text check on the asset);
    - `Withhold.IsWithheld` is true and its Hz is 0;
    - the 2-argument constructor gives Variant Full and IsWithheld false;
    - the 3-argument constructor keeps Compact;
    - a NaN distance is stored unchanged.
  - `MeshSyncGoldenFixture` holds the stand-in's 54/56 B layouts, and later the team's own.
- **Acceptance:**
  - all EditMode tests pass (296 + new);
  - the package dry run lists `Mesh/Sync`;
  - the delivery note is written, naming:
    - the path, the namespace `MultiplayerARPG.MMO.Mesh.Sync` and the GUID;
    - the renames `MarkShipChanged` → `MarkSourceChanged` and `shipObjectId` → `objectId`;
    - the 3-argument constructor and `IMeshCompactStateSource`;
    - that Distance may be NaN;
    - the Phase 0 limits: no byte budget, fairness, staleness or at-rest keepalive yet; the 3-build urgent repeat and the Mark calls do work.
- **Depends on:** step 0.

#### Step 2: The measuring instrument (rates, tick-tagged probe, AOI timing, thread CPU)

- **Goal:** read tick frames separately from other frames, split the kit tick into sync pass, AOI and movement loop, apply both rate sets, and split CPU by thread.
- **Files:**
  - Mesh/ edits:
    - `MeshWorkProbe.cs`: meter members below;
    - `MeshMapNetworkManager.cs`: an `OnServerUpdate` override;
    - `MeshMapNetworkManager_Load.cs`: the `Work ticks` line after the Work window line (:117-122).
  - Mesh/ new: `MeshTickWindow.cs`.
  - TestMap/ new, in `TestMap/Phase0/`: `ServerMeshRates.cs`, `ServerMeshTimedAoi.cs`, `ServerMeshThreadCpu.cs`.
  - TestMap/ edits:
    - `ServerMeshProbeSelfTest.cs`: add `MESH_PROBE_BURN_TICK_MS`;
    - `ServerMeshLoadGenerator.cs:100-104`: defer uncapping to ServerMeshRates when `MESH_RATES` or `MESH_UNCAPPED_CELLS` is set.
- **Classes and members:**
  - **`MeshWorkMeter`:**
    - `BeginTick(long stamp)`, `EndTick(long stamp)`;
    - `MarkAoiCall(long startStamp, long endStamp, bool ranPass)`. Any interest manager may report its call, which keeps this game-neutral.
    - `AddTickWork(double ms)`, used by the sender;
    - `NotePhysicsStep()`, from a `FixedUpdate` player-loop item;
    - `MeshTickWindow TakeTickWindow(double seconds, int tickHz)`.
    - Histograms: `new MeshWorkHistogram(0.05f, 4000)` for tick frames, non-AOI tick frames, other frames and tick duration. That is a 200 ms range with 0.05 ms bins (critique 7a). The default 0.25 × 400 (`MeshWorkHistogram.cs:20`) is too coarse against a 2.5 ms gate.
  - **`MeshWorkProbe.NominalFrameRate`** (int, 0 = none) and `EffectiveBudgetMs`. The budget still reads `targetFrameRate` (`MeshWorkProbe.cs:167`) and falls back to the nominal rate when uncapped.
  - **`MeshTickWindow`** fields:
    - ticks, expected, dropped (the updater drops its backlog after 5: `LogicUpdater.cs:7, 99-102`);
    - frames with 0, 1 and 2+ ticks;
    - tick frames mean/p95/p99/max; non-AOI tick frames p95/p99; AOI tick count and p99/max; other frames mean/p95;
    - kit tick ms, split into sync+ping, AOI and movement loop;
    - tick send work (movement loop + sender) mean/p95/p99;
    - physics steps mean/max and frames with 2+ steps;
    - work ms/s and busy share; nominal budget; `ToLine()` in invariant culture; non-empty bins when `MESH_WORK_BINS=1`.
  - **Decomposition (C):** LNGM.OnServerUpdate runs the sync pass, then ping, then `InterestManager.UpdateInterestManagement` as its last statement (LNGM:109-122). BGNM then runs the movement loop (BGNM:129-137). So:
    - sync = aoiStart − tickStart;
    - AOI = aoiEnd − aoiStart;
    - movement loop = tickEnd − aoiEnd.
    - Without the rig's timed AOI, only the whole tick is reported.
  - **`MeshMapNetworkManager.OnServerUpdate(LogicUpdater updater)`:** stamps around `base.OnServerUpdate(updater)`.
  - **`ServerMeshRates`** (installed with `RuntimeInitializeOnLoadMethod(AfterSceneLoad)`, and re-applied once `mesh.IsServer` is true):
    - sets `Application.targetFrameRate`, `QualitySettings.vSyncCount = 0` and `Time.fixedDeltaTime`;
    - sets `NominalFrameRate` for `u`;
    - uncaps only the cells in `MESH_UNCAPPED_CELLS` (fixes the puppets part's "a cell that owns nothing never uncaps", `ServerMeshLoadGenerator.cs:84-104`);
    - logs `Rates: fps 30 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.02 s (project 0.03) | maxDeltaTime 0.333 | probe budget 33.33 ms | uncapped cells -`.
    - `ServerMeshRatesSetting.TryParse(string text, out ServerMeshRatesSetting setting, out string error)` exposes `FramesPerSecond` (−1 = uncapped) and `FixedStepSeconds`.
  - **`ServerMeshTimedAoi : JobifiedGridSpatialPartitioningAOI`** (`MESH_AOI_TIMING=1`):
    - overrides `UpdateInterestManagement(float)` and `UpdateInterestManagementImmediate()` (both abstract in `BaseInterestManager.cs:19-20`) and times the base call;
    - `ServerMeshAoiCountdown` mirrors JGAOI:169-178 and 163-167 to know whether the pass ran;
    - installed after `MESH_BAND` applies: add the component, copy the public fields (`cellSize`, `maxObjects`, `updateInterval`, `bufferedCells`, `defaultVisibleRange`), then `mesh.InterestManager = timed` (the public setter calls `Setup`: LNGM:81-91). The kit only substitutes the default when the interest manager is null (LNGM:119-120), so the swap sticks.
  - **`ServerMeshThreadCpu`** (`MESH_THREAD_CPU=1`, Windows only):
    - Toolhelp32 snapshot, `GetThreadTimes`, `GetThreadDescription`; the main thread id comes from `GetCurrentThreadId` at start;
    - `static ServerMeshThreadClass Classify(string description, uint id, uint mainId)` sorts threads into {Main, LiteNetLib (`LogicThread`, `ReceiveThread(...)`: `LNLib/LiteNetManager.Socket.cs:382-394`), Jobs, Other};
    - logs `Threads window 5.00 s: main X litenetlib X jobs X other X total X process X ms/s`;
    - if descriptions come back empty (E: Mono may not pass names), it prints `names=unavailable` and reports process minus main.
- **Tests (failures first):**
  - `ServerMeshRatesSettingTests`: `""`, `"abc"`, `"0/0.02"`, `"30/0"`, `"30/-0.02"`, `"30/x"` and `"30"` all fail with an error naming the part. `"30/0.02"`, `"60/0.03"`, `"u/0.02"` and `" 30 / 0.02 "` parse.
  - `MeshWorkMeterTests` (extended):
    - a window with no ticks gives zeros, not NaN;
    - dropped = expected − actual;
    - at 30 fps / 20 Hz, ticks land on 2 of 3 frames;
    - two ticks in one frame count as a 2+ frame;
    - tick and other frames get separate p95;
    - AOI-tagged ticks are left out of the non-AOI p95 and counted;
    - a 2.5 ms frame lands in bin 50, and anything above 200 ms goes to max;
    - the nominal budget is used when uncapped;
    - physics steps and frames with 2+ steps are counted;
    - work ms/s = Σ work / seconds;
    - the decomposition sums to the tick within 1 µs.
  - `MeshTickWindowLineTests`: the exact line under `de-DE` culture, with a matching `test_p0tools` parse case.
  - `ServerMeshAoiCountdownTests`: with dt 0.05 and an interval of 1 s, the pass is due 1 time in 20; Immediate forces it; dt 0 is not due without Immediate.
  - `ServerMeshThreadClassTests`: `"LogicThread"` and `"ReceiveThread(7770)"` → LiteNetLib; `"Job.Worker 3"` → Jobs; `""` → Other; the main id → Main.
- **Acceptance (rig):**
  - A tick-only burn of 2 ms at 30/0.02 and at 60/0.03: tick-frame mean rises by the burn ±5%, other frames stay within ±5%, ticks are 100/100 per 5 s, and 2 of 3 frames carry a tick at 30 fps.
  - Physics steps/s are 50 ± 1 and 33.3 ± 1.
  - AOI passes are 5 ± 1 per window.
  - The per-thread sum is within ±5% of process CPU.
  - Overhead on the canary is below 0.02 ms/frame.
  - With no env set, all 10 scenarios plus `dynidle` pass.
- **Depends on:** step 0.

#### Step 3: Handler slot owner, kinematic character puppets and the load generator's motion modes

- **Goal:** a cheap puppet for kit characters and monsters that works when unobserved (critique 2), shares the handler slot safely (critique 3), and keeps the demo default Steered.
- **Files:**
  - Mesh/ new: `MeshPuppetMotion.cs`, `MeshKinematicTrack.cs`, `MeshKinematicCharacterBody.cs`, `Sync/MeshMovementHandlerSlots.cs`, `MeshMapNetworkManager_Puppets.cs`. The last is a new partial, because the Ghosting file is already 3,622 lines.
  - Mesh/ edits in Ghosting, calls only:
    - spawn Ghostify (:1510): pass `PuppetExtraTypeNames(puppetMotion)` and call `BeginPuppetMotion`;
    - `SteerGhost` (:1897): a kinematic branch;
    - `GhostingUpdate` (:328): call `KinematicPuppetsUpdate()`;
    - demote-in-place (about :2536-2543): Begin;
    - promotion (about :2508-2521): End before Unghostify.
  - **`GhostSuppression.cs` is unchanged.** The kinematic path passes `PlayerCharacterItemLockAndExpireComponent` through the existing `extraTypeNames` argument (critique 15), so today's steered puppets keep today's deny list.
  - TestMap/ edits: `ServerMeshLoadGenerator.cs` (motion, fight and collide).
  - TestMap/ new: `Phase0/ServerMeshKinematicWalk.cs`, `Phase0/ServerMeshServerAnimation.cs`.
- **Classes and members:**
  - `enum MeshPuppetMotion : byte { Steered = 0, Kinematic = 1 }` and `MeshPuppetMotionSetting.TryParse(string, out MeshPuppetMotion)`, with `EnvironmentKey = "MESH_PUPPET_MOTION"`. A bad value keeps the inspector value and logs a warning.
  - **`struct MeshKinematicTrack`:**
    - `MeshKinematicTrack(float blendRate = 15, float maxExtrapolationSeconds = 0.3f, float snapDistance = 4)`, which throws on non-positive values;
    - `bool TryAccept(Vector3 position, Vector3 velocity, float yawDegrees, bool discontinuity, float receivedAt)`, false for non-finite values or an older arrival;
    - `void Advance(float now, float deltaTime, ref Vector3 position, ref float yawDegrees)`:
      - target = p + v·min(age, 0.3 s);
      - blend factor 1 − exp(−15·dt);
      - snap past 4 m (today's `ghostTeleportDistance`) or on a discontinuity;
      - yaw uses LerpAngle.
  - **`sealed class MeshMovementHandlerSlots`** (one per manager, `For(BaseGameNetworkManager)` using a ConditionalWeakTable):
    - wraps `ConcurrentDictionary<uint, IEntityMovementDataHandler>` (BGNM:108-109);
    - `bool TryInstall(uint id, IEntityMovementDataHandler layer, bool replaceable, out string error)`:
      - uses `TryUpdate`;
      - at most one Mesh layer per object;
      - installing over a replaceable layer calls `IMeshReplaceableHandlerLayer.OnReplaced()` and keeps the kit handler, not the wrapper, as "previous".
    - `bool TryRemove(uint id, IEntityMovementDataHandler layer, out string error)` restores the kit handler and raises `LayerRemoved(uint)`;
    - `bool TryGetKitHandler(uint id, out IEntityMovementDataHandler kit)`, `bool HasLayer(uint id)`, `void Forget(uint id)` (on destroy, never restoring into a removed entry).
  - **`sealed class MeshKinematicCharacterBody : IEntityMovementDataHandler, IMeshStateSource`:**
    - `static bool TryCreate(BaseCharacterEntity entity, MeshMovementHandlerSlots slots, out MeshKinematicCharacterBody body, out string reason)`. It refuses, with one warning per type:
      - a kit handler that is not `BuiltInEntityMovementFunctions3D` or `NavMeshEntityMovement` (both write `ServerWriteSyncTransform3D`: `EntityMovementFunctions.cs:136-145`; NMEM:628);
      - a mounted puppet;
      - an enabled grid path.
      A refused entity stays Steered.
    - `Begin()`: `MovementDisableState.Add(key)`, `entity.Movement.enabled = false` (OnDisable unregisters the update: CCEM:305-309), and install as non-replaceable. The disable key alone does nothing unobserved, because `EntityUpdate` is skipped (BGE:323, 361-375).
    - `End()`: restore, re-enable, remove the key. The CE-9 reset still applies: BIEMF3D:233-242.
    - `Accept(Vector3 p, Vector3 v, float yaw, byte flags, bool discontinuity, float now)` and `Tick(float now, float dt, bool observed)`. Unobserved puppets move only when a snapshot arrives.
    - `WriteServerState`:
      - writes exactly the kit bytes: `PutPackedUInt(flags)`, `Put(byte extra)`, `PutVector3`, `PutPackedInt(GetCompressedAngle(yaw))`, `Put(int 0)` (C: `EntityMovementFunctions.cs:136-145`, `NetDataWriterExtension.cs:121-127`);
      - bytes are cached per `writeTimestamp`;
      - the teleport bit is held for 3 distinct timestamps and is never cleared inside a write.
    - `ConnectionId` is −1, and `WriteClientState` returns false.
    - IMeshStateSource: `DiscreteVersion` increases on a discontinuity; `IsAtRest` = |v| < 0.01 and no pending bit; `WriteState` writes the same bytes. It registers with `IMeshStateRegistry` when one exists (from step 6).
    - Flags are the byte `GhostSnapshotMessage` carries today (Ghosting:805). The full `uint` and the extra state wait for the compact link after Phase 0.
  - **Manager partial:**
    - `public MeshPuppetMotion puppetMotion = MeshPuppetMotion.Steered;` with the env override;
    - `BeginPuppetMotion(MeshGhost)`, `EndPuppetMotion(MeshGhost)`, `bool FeedKinematic(MeshGhost, GhostSnapshotMessage)`, `KinematicPuppetsUpdate()` (one loop, no MonoBehaviour per puppet);
    - `internal static IEnumerable<string> PuppetExtraTypeNames(MeshPuppetMotion)`;
    - a `Puppets window` line: kinematic, steered, fallback by reason, snaps, discontinuities, ticks/s.
  - **Load generator:**
    - `MESH_LOAD_MOTION=kinematic`: no brain; disable key plus `Movement.enabled = false`; `ServerMeshKinematicWalk` moves the transforms at 0.9 × move speed, reflected at the rectangle's edges. The kit handler writes the transform position (C: `EntityMovementFunctions.cs:140`), while `MovementState` is frozen.
    - `inert`: spawned and never moved.
    - `MESH_LOAD_FIGHT=0`: the brain never attacks.
    - `MESH_LOAD_COLLIDE=0`: `Physics.IgnoreLayerCollision` on the character layer. This affects every character on that process, so it is for B4 attribution only.
    - `ServerMeshServerAnimation` sets `GameInstance.Singleton.updateAnimationAtServer` on this process only.
- **Tests (failures first):**
  - `MeshPuppetMotionSettingTests`: `""`, `"fast"` and `"1"` fail; `"steered"`, `"KINEMATIC"` and `" kinematic "` pass.
  - `MeshKinematicTrackTests`:
    - failures: each constructor argument ≤ 0 throws naming the field; NaN or ∞ position or velocity is rejected; an older arrival is ignored; a gap over 0.3 s holds at p + v·0.3 with no runaway;
    - successes: snap past 4 m; snap on discontinuity; converges to under 1 cm in 0.5 s at 60 fps; yaw 359 → 1 moves +2; zero velocity gives no drift.
  - `MeshMovementHandlerSlotsTests`:
    - failures: unknown id (the error names the id); installing over a non-replaceable layer; removal by a non-owner; a `TryUpdate` race;
    - success, the order test: spawn → wrap (replaceable) → body replaces the wrapper (`OnReplaced` once; the body's kit handler is the original) → promote (kit handler restored; `LayerRemoved` once) → wrap again → destroy (`Forget`).
  - `MeshKinematicCharacterBodyTests`:
    - failures: a non-kit handler type is refused with a warning once per type; the grid enabled is refused; mounted is refused;
    - successes:
      - the bytes equal a handwritten golden array. The kit client reader cannot be used, because it dereferences `DefaultGridManagerComponent.Instance` (`EntityMovementFunctions.cs:173`).
      - two writes with the same timestamp give identical bytes;
      - the teleport bit is present for 3 timestamps, then clear;
      - `DiscreteVersion` changes only on a discontinuity;
      - `WriteState` equals `WriteServerState`;
      - the owner's upload is ignored.
  - `MeshPuppetSuppressionTests`: `PuppetExtraTypeNames(Steered)` is empty; `Kinematic` gives the item-lock component.
  - `ServerMeshLoadMotionTests`: parse motion, fight, collide and uncapped-cell lists, including duplicates and empty entries.
- **Acceptance:**
  - All 10 scenarios plus `dynidle` pass with no env: the default is unchanged.
  - The same pass with `MESH_PUPPET_MOTION=kinematic`, especially `party`, `pvp`, `seam`, `seamcold` and `crash`. `ghostSteer` and puppet teleports stay at or below the step 0 base rate.
  - A bot's view of a kinematic puppet stays within 1.0 m of the owner at p95 while walking.
  - The Puppets window shows unobserved kinematic puppets ticking at about 10/s.
- **Depends on:** steps 1 and 2.

#### Step 4: The Phase 0 tools for stage 0a (Python)

- **Goal:** one umbrella runner, as the critique's fix for four overlapping runners.
- **Files:**
  - new `Tools/rigrun.py`: `Report`, `run_window`, `cell_window` and `run_problems` moved unchanged from abload.py (about :66-164; `run_window` :92, `cell_window` :122);
  - new `Tools/p0tools.py` (pure), `Tools/p0load.py`, `Tools/test_p0tools.py`;
  - edits:
    - `abload.py`: `--puppets steered|kinematic`, `--rates`, `--bands 20,40,80,150`, a `strips4` layout (four 150 × 600 m cells beside `halves`/`coarse`/`grid` at :55-59), a strips4/halves verdict, and JSONL rows;
    - `regress.py:59-64`: ENV_KEYS for this stage.
  - New parsers go in p0tools, not rigtools (already 513 lines).
- **Contents:**
  - `p0load.py` subcommands: `accept-a`, `canary`, `rates` (block K), `floor`, `presence` (B1-U), `density` (B4), `abgeo` (B9 through abload), `report [--import other.jsonl]`.
  - `p0tools`:
    - parsers for Work ticks, Rates, Puppets window and Threads window; the old Work window format still parses;
    - per-block acceptance checks;
    - frame-weighted aggregation and an exact bin merge;
    - least squares with named regressors and jackknife confidence intervals over repeats;
    - rate decomposition;
    - the split model and the map-size table builder (§6);
    - gate evaluation;
    - JSONL (§7) and the Markdown summary.
  - `ENV_REGISTRY` mirrors §0.16.
- **Tests (failures first):**
  - every parser on valid, malformed and incomplete lines, plus the old format;
  - each acceptance failure alone, then all passing;
  - least squares recovers known coefficients, and a singular design is refused with a message;
  - jackknife intervals;
  - the rate decomposition recovers known F, Ph and T;
  - the split model: the ratio is ½ + kB/W at x = 0; W* = 4σkB/(1−x); s_min = max(B, W*/2); the ladder; the crossing rate 4v/(πW);
  - the map-size table for known k and B;
  - gates exactly at each threshold;
  - saturation exclusion;
  - stepping order by repeat, and rotation covering every position;
  - JSONL round trip and schema version;
  - Markdown column order;
  - every `ENV_REGISTRY` key is in `regress.ENV_KEYS`.
- **Acceptance:**
  - `test_rigtools` and `test_p0tools` pass;
  - after the move, abload re-grades the saved chain3 logs to the same verdicts;
  - `p0load.py <cmd> --dry-run` prints each run's plan and environment.
- **Depends on:** steps 2 and 3.

*Stage 0a runs (§2) happen here.*

### Stage 0b

#### Step 5: Transport chain, send meter, phantom clients (character and eye) and the sink

- **Goal:** one phantom design (critique 1).
  - The chain is kit → `MeshMeteredTransport` (Mesh/) → `ServerMeshPhantomTransport` (TestMap/) → `LiteNetLibTransport`.
  - Observers come in two kinds:
    - **character:** a player character with 20 Hz uploads, for the client-driven owner row, F1, F2, G2 and bot calibration;
    - **eye:** a small owned identity, for B2 and observed e(P), where P and V must vary independently.
  - Two modes:
    - **virtual** for the grids;
    - **sink:** real LiteNetLib peers from machine B, for B3 and for G1 rows under 1 GbE.
  - The protocol part's separate drop-mode wrapper is dropped.
  - The sender part's claim that "a fake connection id measures nothing" is corrected: only `peer.Send` and the network thread are skipped (`LiteNetLibTransport.cs:116-124`).
- **Files:**
  - Mesh/ new: `MeshSendMeter.cs`, `MeshMeteredTransport.cs`, `MeshTransportUnwrap.cs`.
  - Mesh/ edits (the three `is LiteNetLibTransport` sites use `Innermost<LiteNetLibTransport>`):
    - `MeshMapNetworkManager.cs:648-649`;
    - `MeshMapNetworkManager.cs:95-99`: install the metered factory before `base.Awake()` when `MESH_METER_SENDS` is set;
    - `Ghosting.cs:164`;
    - `MeshMapNetworkManager_Load.cs:210`: clients come from `ServerTransport.ServerPeersCount`; add the `Send window` line.
  - TestMap/ new (`TestMap/Phantoms/`): the phantom classes in §0.15, and `TestMap/Prefabs/ServerMeshPhantomEye.prefab` (built in a build window).
  - TestMap/ edit: `ServerMeshAutoClient.cs` gains a `Bot net window` line.
- **Classes and members:**
  - **`MeshSendMeter`:**
    - `static bool TryReadMessageType(byte[] data, int length, out ushort type)`, reading the packed ushort: 1 B up to 240, 2 B up to 2,287, 3 B up to 67,823 (`NetDataWriterExtension.cs:169-186`);
    - `Record(ushort type, DeliveryMethod delivery, byte channel, int bytes, long elapsedTicks, bool refused)`, using a 64-slot open-addressed table plus "other";
    - `MeshSendWindow TakeWindow(double seconds)`.
  - **`MeshMeteredTransport : ITransport, IMeshWrappedTransport`:**
    - constructor `(ITransport inner, MeshSendMeter meter, bool timed)`;
    - forwards every member (`ITransport.cs:8-25`) and meters only `ServerSend`.
  - **`MeshMeteredTransportFactory : BaseTransportFactory`:**
    - `Inner`, which defaults to an added `LiteNetLibTransportFactory` exactly as the kit would add one (`LiteNetLibManager.cs:126-129`);
    - `Build()`.
  - **`MeshTransportUnwrap.Innermost<T>(ITransport)`** returns T or null.
  - **`ServerMeshPhantomTransport`:**
    - ids are `1_000_000 + k`;
    - `TryQueueConnect(out long id)`, `QueueUpload(long id, NetDataWriter frame)`, `QueueDisconnect(long id)`, `IsPhantom(long id)`;
    - `ServerReceive` drains the inner transport first, then synthetic events, and consumes the sink hello `[65000][int index]`;
    - `ServerSend` to a phantom counts and swallows, and throws `TooBigPacketException` for an unreliable payload above MTU−1, as LiteNetLib does (`LNLib/LiteNetPeer.cs:640`), because the kit catches it (BGNM:727, 760);
    - `GetServerRtt` for a phantom returns a fixed value;
    - `StopServer` clears the phantoms.
  - **Installation:** `ServerMeshPhantomInstaller` runs at AfterSceneLoad. If the manager's factory is a `MeshMeteredTransportFactory`, it sets `metered.Inner = phantomFactory`; otherwise it assigns `mesh.TransportFactory` (setter at `LiteNetLibManager.cs:42-48`). If `mesh.IsServer` is already true, it logs an error and does nothing.
  - **`ServerMeshPhantomClients` (binder).** It waits for `IsServerReadyToInstantiateObjects()`, `OwnCell` and `TryGetPlayer(id)` (LNGM:142-145). Then:
    1. set `player.IsReady = true` before the spawn (`BaseInterestManager.cs:30-37`);
    2. character: spawn as the load generator does, but with `Assets.NetworkSpawn(obj, 0, id)` (`LiteNetLibAssets.cs:456`), then `TrackSyntheticEntity` (Ghosting:106). There is no brain: the upload feeder moves it. `MESH_PHANTOM_PAYLOAD=input` sets the instance's `movementSecure` to ServerAuthoritative for the server-authoritative row (critique gap H; the field's accessibility is (E));
    3. eye: spawn `ServerMeshPhantomEye` owned by id. It follows a host entity chosen by `ServerMeshPhantomLayout`, and a `LiteNetLibIdentity.ForceHideFunctions` delegate hides it from everyone except its owner (LNI:26, 822-838; (E) verified in acceptance).
    - It never runs EnterGame, ClientReady, the database or the central (`MapNetworkManager.cs:553-714`).
    - V_move = subscribed objects that have a movement handler, sampled over 10% of phantoms per window (critique 17).
  - **`ServerMeshPhantomUploads`:**
    - frame `[packed ushort 119][packed long ts][packed uint objectId][payload]`, as the kit client writes it (BGNM:632-635), injected as a DataEvent so the inbound dispatch (BGNM:579-587) runs;
    - timestamp = `ServerTimestamp` minus a 50 ms delay, strictly rising per phantom;
    - a random phase per phantom, and only the phantoms that are due are sent each frame;
    - pooled readers;
    - feeder time is logged separately.
  - **`ServerMeshPhantomLayout.Build(int phantoms, int view, float range, Rect area, int seed)`:**
    - groups of radius r ≤ R/2, set at least R + 2r apart;
    - filler comes from kinematic owned load (step 3).
  - **`ServerMeshPhantomSink`:**
    - K threads of about 100 NetManagers each in manual mode (`StartInManualMode`, `PollEvents`, `ManualUpdate`: `LNLib/LiteNetManager.cs:600-617, 1232-1259`; untested on the rig, (E));
    - hello, then the bind reply `[65001][packed uint objectId][x][z]`, then 20 Hz uploads for the character kind;
    - a `Sink window` line per message type.
  - Lines: `Send window` (calls/s, kB/s and µs/call for the top six types plus "other", refused sends, real datagrams/s) and `Phantom window` (bound, ready, V_move mean/p95/min, uploads/s, feeder ms/s).
- **Tests (failures first):**
  - `MeshSendMeterTests`:
    - failures: null, empty, a truncated 2-byte form, a truncated 3-byte form;
    - decodes 0, 119, 240, 241, 2,287, 2,288 and 65,000;
    - per-type and per-delivery counts; the window reset; timed against count-only.
  - `MeshMeteredTransportTests` (with `MeshFakeTransport`): each member forwarded once with its arguments and return value; a refused send counted as refused; client calls not metered; `Innermost` through 0, 1 and 2 wrappers, and null when absent.
  - `ServerMeshPhantomTransportTests`:
    - failures: a send before connect returns false; disconnecting an unknown id returns false; an inner id inside the phantom range is logged and the real connection wins; an oversize unreliable send throws;
    - successes: inner events before synthetic ones; the peer count includes phantoms; a disconnect is emitted once; `StopServer` clears; the hello is consumed.
  - `ServerMeshPhantomUploadTests`:
    - the bytes parse back through the kit transform reader and a header reader copied from BGNM:581-583;
    - timestamps rise strictly;
    - at 33.3 ms frames, each phantom is due 20 ± 1 times per second and never twice in one frame;
    - steps are at most speed × dt and stay inside the area.
  - `ServerMeshPhantomLayoutTests`:
    - failures: P = 0; V < 1; groups that do not fit;
    - successes: (400, 100) → 4 × 100; (50, 500) → 50 + 450; (100, 300) → 100 + 200; every pair within R; groups at least R + 2r apart.
  - `ServerMeshPhantomSettingsTests`: grammar for every phantom key.
  - `ServerMeshPhantomEyeHideTests`: hidden from a non-owner, visible to the owner.
- **Acceptance:**
  - With no env, the Mesh stats line is identical and all scenarios plus `dynidle` pass.
  - Virtual, 200 characters:
    - every phantom bound and ready;
    - V_move within ±10% of the layout;
    - server positions track the feeder within 0.5 m at p95;
    - no exception.
  - A real bot in an eye group spawns no eye.
  - Sink 50 on machine B: every peer bound; sink bytes within ±3% of LiteNetLib `BytesSent`.
  - Count-mode meter overhead is below 1% on the canary.
- **Depends on:** step 2; the eye prefab needs a build window.

#### Step 6: The state sender, the kit adapter and CE-12

- **Goal:** build once per tick and fan out, for kit characters (through the adapter), for the kinematic body and for rig ships, in the kit's exact msg-119 layout.
- **Files:**
  - Mesh/Sync new: `MeshStatePacketWriter.cs`, `MeshStateFanout.cs` (the engine plus its ports `IMeshStateTransport` and `IMeshStatePositions`, moved out of the contract file), `MeshStateSender.cs`, `MeshKitMovementStateSource.cs`, `MeshDistanceSendPolicy.cs`, `MeshStateSenderStats.cs`.
  - Mesh/ edit: in `MeshMapNetworkManager.cs:95-99`, add the component when `MESH_SENDER` is set, before `base.Awake()`, so that BGNM:123 collects it.
  - Kit edit, when received: CE-12 at BIEMF3D:1337-1380 and :1290-1332, and NMEM:562-632, in the §0.9 shape only.
  - TestMap/ new: `Phase0/ServerMeshCe12Check.cs`.
- **Classes and members:**
  - **`MeshStatePacketWriter`:**
    - constructor `(int maxPacketBytes = 1020)`, which throws below 64 or above 1,020. 1,020 is `MAX_UNRELIABLE_PACKET_SIZE` (SS:12), so reliable packets never fragment;
    - `Begin(long conn, DeliveryMethod m, long ts)`;
    - `bool TryAppend(uint id, byte[] buf, int off, int len)`, false above `1004 − idBytes`;
    - `Flush(IMeshStateTransport)`;
    - `static int RecordSize(uint id, int len)`.
    - Layout: `[packed ushort 119][packed long ts][int32 count]{[packed uint id][int32 len][bytes]}` (BGNM:683-694, 708-710, 734-736).
  - **`MeshStateFanout`** (pure):
    - constructor `(IMeshStateTransport, IMeshStatePositions, IMeshSendPolicy, MeshStateFanoutSettings)`;
    - `Register(IMeshStateSource, out string error)`, `Unregister`, `SetSubscribers(IMeshStateSource, IReadOnlyList<long>)`, the three Mark calls;
    - `Build(long sampleTimestamp, int frameIndex)` and `FlushDeferred(int frameIndex)`;
    - `Counters`.
    - Rules:
      - at most one build per Unity frame; catch-up ticks count as skipped;
      - each subscribed source is built once per build;
      - `divisor = max(1, round(buildHz / Hz))`, with a per-pair phase;
      - classify on a new pair, then every `classifyEveryBuilds` builds (default 4, which gives 5 Hz re-tiering; reply §7 item 4 gets a 10-build arm);
      - **urgent** = `sendReliably` (reliable to all this build) or a `DiscreteVersion` change (all subscribers on this build and the next 2);
      - **a new pair** gets nothing on the build of its creation tick, then is due on the next 3 builds (reply Part 2 §6, because the spawn goes out at the next tick's sync pass);
      - Withhold always wins;
      - urgent and reliable records are always Full;
      - compact is written lazily, at most once per build;
      - optional two-half spreading keeps the build's timestamp;
      - host connection skipped (BGNM:676-677);
      - append-only violations are logged, and the source is unregistered.
  - **`MeshStateSender : BaseGameNetworkManagerComponent, IMeshStateRegistry`:**
    - hooks:
      - a pre-kit stamp on `LogicUpdater.OnTick`, subscribed at RegisterMessages. The kit removes and re-adds its own handler at every StartServer (`LiteNetLibManager.cs:221-222`), so the stamp always runs first;
      - the build is subscribed at `OnStartServer` (BGNM:252-255), so it runs after the kit;
      - both are unsubscribed at `OnStopServer`.
    - The pre-kit stamp also processes the adapter's lazy-wrap queue. That fixes the critique 3 race: ghosts are already marked by then.
    - Observer anchor: `GameInstance.ServerUserHandlers.TryGetPlayerCharacter`, otherwise the first owned object (LNGM:142-145; LNP:113-116). That is how phantoms get a position.
    - It calls `MeshWorkProbe.Meter.AddTickWork(ms)`.
    - Sends go through `ServerSendMessage(conn, 2, method, writer)`, catching `TooBigPacketException`.
  - **`MeshKitMovementStateSource : IEntityMovementDataHandler, IMeshStateSource, IMeshReplaceableHandlerLayer`:**
    - `static void AllowHandlerType(Type)`; the default allow-list is `BuiltInEntityMovementFunctions3D` and `NavMeshEntityMovement`;
    - it never wraps an `IMeshStateSource` or a slot that already has a layer;
    - its kit-facing `WriteServerState` returns false; every other member delegates;
    - rewrap and unregister checks run per build;
    - one warning after 20 consecutive reliable builds. That catches NMEM's `_sendingDash`, which is never cleared on the server (C), and SMC:390 for unowned ships;
    - `Register` refuses when the grid path is enabled (BGNM:133).
  - **`MeshDistanceSendPolicy(float r0, byte nearHz, byte midHz, byte farHz, byte ownerHz, MeshStateVariant farVariant)`:**
    - throws naming the field for r0 ≤ 0, NaN or a Hz above 20;
    - near is d ≤ 0.5·R0, mid is d ≤ R0, NaN counts as near;
    - the defaults are 20/20/20/20, which is kit parity (ownerHz stays 20 for kit characters);
    - `TryParseTiers`.
  - **`MeshStateSenderStats`:** histograms `(0.02f, 1000)` and an invariant-culture `Sender window` line with sources, pairs, clients, builds, skipped, rewraps, writes, compact writes, errors, too-big, stuck-reliable, records and packets (unreliable and reliable), client kB/s and packets/s, build/fanout/send ms/s, µs/packet, residual ms/tick and `gc0`.
  - **`ServerMeshCe12Check`** (`MESH_CE12_CHECK=1` only; critique 15):
    - on a hidden rig character: Teleport, write twice at T, once at T+50;
    - logs `CE-12 check: shape=stamp|perCall|postPass|absent first=1 second=1 next=0`.
- **Tests (failures first):**
  - `MeshStatePacketTests`:
    - failures: a payload above `1004 − idBytes` (skipped, counted, logged once; others sent); `WriteState` false (nothing written, writer truncated); a shrunk writer (source dropped, the error names the id); `WriteState` throws (logged with id and type; others unaffected);
    - successes: byte-exact round trip through `MeshEntityStatePacketParser` (ends exactly at the packet end); packed-id boundaries 240/241, 2287/2288, 67823/67824, 16777215/16777216; every packet ≤ 1,020 B with straddling records and payloads of 21, 28, 56 and max; reliable and unreliable in separate packets; host skipped; 510 and 255 caps.
  - `MeshStateFanoutTests`:
    - failures: no subscribers (`WriteState` never called); Withhold gives nothing, even urgent;
    - successes:
      - at most one write per source per build for P×V of 1×1, 3×2, 50×100 and 400×500, with and without compact;
      - 5 catch-up ticks give one build;
      - reliable goes to all subscribers;
      - a `DiscreteVersion` change goes to all on 3 builds;
      - a new pair skips its creation build, then gets 3 builds;
      - an unsubscribed pair gets nothing, including the deferred half;
      - Mark calls re-classify on the next build;
      - with ownerHz = 1, the owner gets urgent records plus 1 Hz.
  - `MeshStateCadenceTests` (with `MeshTickScheduleCopy` of `LogicUpdater.cs:88-110`, 60 s):
    - at 30 fps, builds on 2 of 3 frames with 33/67 ms gaps, and 20/10/5 Hz pairs get 1,200/600/300 records (±1);
    - at 60 fps, the same counts;
    - a 150 ms stall gives 1 build and 2 skipped;
    - divisor 4 over 400 pairs gives per-build counts within 1.
  - `MeshKitMovementStateSourceTests`:
    - failures: grid enabled (refused, dictionary unchanged, the error names the id); a type not allow-listed (logged once); an `IMeshStateSource` handler; a `TryUpdate` race;
    - successes:
      - delegation;
      - a teleport gives one real call, a reliable record to all 3 subscribers including the owner, and none on the next build;
      - re-registration gives a skipped build and a rewrap;
      - 20 reliable builds give one warning;
      - Unregister restores;
      - the order test with step 3's slots (spawn, lazy wrap, kinematic ghostify replaces the wrapper, the body registers directly, promotion, rewrap);
      - against `MeshFakeStampedKitHandler` (CE-12 shape), the flags reach all subscribers on one build and are clear on the next.
  - `MeshDistanceSendPolicyTests`: throws on r0 ≤ 0, NaN or Hz > 20; exactly at 0.5·R0 and R0, and just above each; owner; NaN distance; far variant.
  - `MeshStateSenderStatsTests`: the exact line under `de-DE`, and the p0tools parse.
  - `ServerMeshCe12CheckTests`: the shape classification from four recorded flag sequences.
  - The existing `EveryPackageOfThisProject_HasNoStrayReference` covers the new files.
- **Acceptance:**
  - `MESH_SENDER=measure` changes no bytes.
  - With 2 bots observing 50 kit characters, `on` 20/20/20 gives EntityState records/s within ±2% of the kit arm, and puppet error within 0.1 m of it.
  - All scenarios plus `dynidle` pass with `MESH_SENDER=on`, and with `on` plus `MESH_PUPPET_MOTION=kinematic`.
  - The CE-12 check reports `perCall` on today's kit and `stamp` after CE-12.
- **Depends on:** steps 1, 3 and 5. CE-12 is needed only for the character A/B rows.

#### Step 7: Ship stand-ins, in-process puppets, sync/RPC load and the scale factor

- **Goal:** one ship family (critique 6), taken from the puppets part and corrected to 54/56/28 B (critique 18), plus in-process ship puppets, because the ghost pipeline carries only `BaseCharacterEntity` (Ghosting:434, 574-612) (critique 13).
- **Files:**
  - `TestMap/Ships/*` (§0.15) and `TestMap/Prefabs/ServerMeshShip.prefab` (primitive mesh, rigidbody, box hull, identity, two barrel transforms; built in a build window);
  - `TestMap/Phase0/ServerMeshPuppetFeeder.cs`, `ServerMeshSyncLoad.cs`;
  - `TestMap/Phantoms/ServerMeshPhantomShipPayload.cs`, which adds the `ship` phantom kind: a phantom-owned mirror ship fed 20 Hz uploads.
- **Classes and members:**
  - **`ServerMeshShipEntity : VehicleEntity`:** one seat, hull 2,500.
  - **`ServerMeshShipMovement : IEntityMovementDataHandler, IMeshCompactStateSource`:**
    - registers as the kit handler at `OnIdentityInitialize`, as SMC:92-97 does, and never through the adapter;
    - `ServerMeshShipAuthority { OwnerMirror, ServerAI, Parked }` comes from the spawner or the phantom binder;
    - mirror smoothing with factor 1 − exp(−10·dt) and a snap at 50 m×scale;
    - P-steering at gains 2.5 and 0.6;
    - reliable while a teleport is unacknowledged.
  - **`ServerMeshShipWire`** (static): `WriteLegacy54`, `WriteHeader56`, `WriteCompact28` and their readers. Header byte 0 = authority (2 bits) | flightState (3 bits) << 2; byte 1 = epoch. The compact indicator bit is **(A)**, a rig-only choice, until the space team sets it.
  - **`ServerMeshShipEngine`:** curve evaluate, relative torque, force lerp and relative force at each physics step; drag 3 and 8.
  - **`ServerMeshShipPilot`:**
    - a scan every 0.5 s over `GetSpawnedObjectValues` with a `GetComponent` each;
    - one SphereCast per frame (radius 8×scale);
    - attack and evade timers;
    - burst fire;
    - a stay-in-rectangle priority.
  - **`ServerMeshShipCombat`:** shield `SyncFieldFloat` written every frame while recharging, `RpcFired` and `RpcImpact` as `[AllRpc]`, `TakeShipDamage`. `MESH_SHIP_PROJECTILES=instantiate|pooled`; pooled uses `RaycastNonAlloc`.
  - **`ServerMeshShipScale`:** one factor applied to positions, speeds, detection, weapon range, projectile speed, R0 and tiers, visible range, and gate distances (critique 12). Rates and bytes stay unchanged.
  - **`ServerMeshShipLoad`** (`MESH_LOAD_SHIPS`, seeded by `MESH_LOAD_SEED`):
    - teams 50/50;
    - seated pilots never exit, and have movement disabled explicitly (disable key plus `Movement.enabled = false`). The kit destroys a runtime vehicle when its driver leaves (`VehicleEntityExtensions.cs:11-14`), and an unobserved pilot would otherwise keep simulating (BGE:323).
    - Registration: `GameInstance.AddVehicleEntities` (`GameInstance_Data.cs:834-839`) plus `Assets.RegisterPrefab` (`LiteNetLibAssets.cs:173`), only under a ship env key.
  - **`ServerMeshShipFlightPath`:** seeded paths, including the sender part's ellipse (≤ 280 m×scale, 50–125 m/s).
  - **`ServerMeshPuppetFeeder`** (`MESH_PUPPET_FEEDER=ship|character:N@Hz`): spawns N kinematic puppets (`ServerMeshShipPuppetBody` or `MeshKinematicCharacterBody`) and feeds them serialized snapshots in-process, which isolates the link receive cost ℓ.
  - **`ServerMeshSyncLoad`:** D dirty sync fields per tick and R AllRpc per second.
- **Tests (failures first):**
  - `ServerMeshShipWireTests`: authority > 3, flight state > 7 and a short buffer throw `ArgumentOutOfRangeException` naming the field; golden 54 and 56; every variant's reader consumes exactly the written length; the compact bit layout is `[Ignore]` until the spec arrives.
  - `ServerMeshShipLoadPlanTests`: a missing or negative count, an unknown authority and a duplicate cell each give null plus an error; valid multi-cell plans parse.
  - `ServerMeshShipEngineTests`: terminal speed equals F/drag at 50 Hz, at scale 1 and 0.03.
  - `ServerMeshShipPilotDecisionTests`: seeded transitions; a destroyed target is dropped; no target gives patrol.
  - `ServerMeshShipFlightPathTests`: deterministic by seed; stays inside the rectangle.
  - `ServerMeshShipScaleTests`: every scaled quantity is multiplied, rates are not.
  - Feeder and sync-load setting parsers.
- **Acceptance:**
  - A bot with `MESH_RIG_SHIPS=1` sees ships within 0.5 m×scale of their flight path.
  - The meter and sink measure record sizes of 54, 56 and 28 B.
  - The sender writes each ship once per build.
  - Every scenario passes with no ship env.
- **Depends on:** steps 5 and 6, and a build window.

#### Step 8: Stage 0b tools

- **Files:**
  - `p0load.py` subcommands: `accept-b`, `calibrate`, `observed` (B1-O/R/SA/M/S), `fanout` (B2), `targets`, `syncrpc`, `packets` (B3), `prewarm` (B6 and bursts), `crossings` (B10 and wake);
  - `twohost.py bots|sink` and `remoteb.py bots|sink` (today they have only up/walk/status/down: `twohost.py:1-15`);
  - a one-off registration pass for meshbot2..N, outside the measurement;
  - new `Tools/p0geom.py`, an offline geometric script for reply §7 item 5: V per client, the share within 1 km, pilots' neighbours within 80 m in X/Z, and lock subscriptions. It uses the A0 = 3 km visible-range formula, 3,000 + (v_own + v_fastest) × 1.25;
  - p0tools parsers for Send, Sender, Phantom, Sink, Bot net, CE-12, Prewarm and Feeder lines;
  - the ENV_KEYS additions.
- **Tests:**
  - new-line parse cases (valid, malformed, incomplete);
  - p0geom's exact counts on hand-built geometry;
  - bots/sink argument parsing.
- **Acceptance:** dry runs, and a one-point smoke run per subcommand.
- **Depends on:** steps 5–7.

### Stage 0c

#### Step 9: Database burst, fantasy worker A/B, isolated-spaces floor and loss

- **Files:**
  - `provision.py --burst N` creates meshburst001..N rig rows only, with a guard test in `test_provision.py`;
  - `TestMap/Phase0/ServerMeshDbBurst.cs` (`MESH_DB_BURST`) runs the save → `ValidateAccessTokenAsync` → `GetCharacterAsync` → arrival reads sequence (`Kit/MMO/.../Map/MapNetworkManager.cs:498, 590`);
  - `MESH_MONSTER_CELL` in `MeshMapNetworkManager.cs`, next to `MESH_NO_MONSTER_HANDOFF` (:650-655), with spawn-area gating (Ghosting:38-39);
  - `TestMap/Phase0/ServerMeshSpaces.cs` (`MESH_SPACES=S`) gives each load group its own SubChannelId, so S isolated spaces run in one process with a shared physics scene;
  - sink-side packet loss if LiteNetLib's simulation compiles into the player ((E): it needs `DEBUG` or `SIMULATE_NETWORK`). If not, the loss block is skipped and the report says so.
- **Tests:** settings parsers; the provision guard never touches non-rig rows.
- **Acceptance:**
  - burst accounts reset idempotently;
  - worker arm: E owns every monster and C owns none; hits are forwarded (count > 0); no exception.
- **Depends on:** stage 0b.

**Deferred beyond Phase 0 (critique 13):**
- the compact link (messages 121–123), the puppet body seam, seat references, body verbs and event replay;
- the ship two-process A/B;
- CE-13 (a per-entity server-animation gate), only if the observed rows show server animation dominates;
- the packet blaster from the sender part, because the B3 packet-cap method measures the same cost with real sends.

---

## 2. Measurement run order and rig time (E)

Each run follows the abload protocol:
- 45 s warmup, then 60 s measured;
- frame-weighted windows, at least 80% of windows logged;
- arms in rotated order, one seed per repeat;
- P stepped up in odd repeats and down in even repeats;
- 3 repeats for points that decide a gate, 2 elsewhere (a third when two differ by more than 10% or straddle a threshold);
- a canary every 2 h (cell C alone, 200 owned kinematic characters, 30/0.02, no clients). Drift above 5% marks the rows `drift` and they are re-run.

**Stage 0a**

| # | Block | What | Runs | Time |
|---|---|---|---|---|
| a1 | Acceptance-a | Tick burn at 30 and 60; rates; AOI count; thread-sum check; overhead; all scenarios plus `dynidle`, default and kinematic | ~30 | 1.5 h (attended) |
| a2 | Canary baseline | | 2 | 0.1 h |
| a3 | K, rate decomposition | fps {30, 60} × step {0.03, 0.02} × {steered, kinematic, inert} at N 200 × 2 | 24 | 0.9 h |
| a4 | B1-U, character presence and k | See below | ~66 | 2.6 h |
| a5 | Kill rule, characters | From a4 | – | – |
| a6 | B4, N² | See below | 38 | 1.8 h + 0.5 h Development build |
| a7 | F, process floor | Idle cell and 150 players at 30 and 60 fps: main ms/s, all-thread CPU, memory, cores per cell (critique gap B) | 8 | 0.3 h |
| a8 | B9, geographic A/B | Needs the refit predictions from a4 and a6; see below | 84 | 2.8 h |
| – | Canaries | | 5 | 0.25 h |

- **a4, B1-U:** `halves`, `MESH_BAND=300`, no clients.
  - Kinds {steered, kinematic, inert} × N {100, 200, 400} × 3, plus baseline 3.
  - Additivity: 200/200 and 400/400 × 2 pairings × 3.
  - **A coarse arm for the owned cost a**, N {100, 200, 400} × 3. The halves arm's a includes streaming to E (critique 17).
  - Server animation off, 6 runs.
- **a6, B4:** density N {200, 400} × side {100, 200, 400, 600} m.
  - Scale at about 100 m² per character, N {100, 200, 400}.
  - Attribution at 400 in 200 m: inert, no fight, no self-collision, kinematic, monsters on.
  - Bridge to 0.53: N = 500 in 200 m, uncapped, 60 fps.
  - Two profiler runs on a Development build (the rig build uses `BuildOptions.None`: `ServerMeshRigBuild.cs:43`).
- **a8, B9:**
  - `abload ab --puppets kinematic --players 150,250 --monsters off,all --layouts coarse,halves,grid --repeats 3 --rates 60/0.03` (36 runs);
  - `--players 250 --monsters all --layouts coarse,halves,strips4 --bands 20,40,80,150 --repeats 3` (36 runs);
  - steered controls: B = 150 on coarse/halves, and strips4 at B = 80 (12 runs).

Stage 0a total: about 10.8 h, or about 13 h with re-runs. That is two overnight chains plus a1 attended.

**Stage 0b**

| # | Block | What | Runs | Time |
|---|---|---|---|---|
| b1 | Acceptance-b | Virtual against sink; eye hiding; sender byte-exact with bots; ship seen by a bot; CE-12 check; meter overhead | ~20 | 1.0 h (attended, machine B) |
| b2 | J-lite | 50 real bots against 50 **character** phantoms with uploads, sink on B; kit and char-sender arms | 4 | 0.85 h (attended) |
| b3 | B1-O / R / SA | Observed e(P): eyes {50, 200} × {steered, kinematic} × N 200 × 3. R: client-driven owners {200, 400} × 3. SA: server-authoritative owners {200, 400} × 3 (gap H) | 24 | 1.0 h |
| b4 | B1-M, awake monsters (critique 11) | Eyes next to 200 monsters: owned awake against steered and kinematic puppets, both rate sets × 3 | 18 | 0.75 h |
| b5 | B1-S, ships | 30/0.02, scale 0.03. AI {100, 200, 400}; mirror {100, 200, 400, 800}; feeder ship puppets {100, 200, 400}; feeder characters 200 (ℓ); dense row at scale 1.0; × 3 | ~36 | 1.4 h |
| b6 | B2, fan-out | See below | ~98 | 9.0 h |
| b7 | B3, packets and threads | Ship-sender-28, P {100, 400}, V 500, packet cap 1,020/510/255 × 3, sink on B; kit 400 × 500 characters virtual; thread and datagram saturation | 16 | 1.0 h |
| b8 | B6, pre-warm and subscription bursts | N {100, 300, 650} × {steered, kinematic, ship}; 650 at 50/frame; 650 with 100 eyes; plus range-change and group-arrival bursts (gap D) | 36 | 1.2 h |
| b9 | B10, crossings and wake (gaps A, I) | 20 and 50 bots on B walking across the halves cut: handoff_ready p50/p95/p99, ms/s per crossing on both cells; a sleeping neighbour woken by a crossing | 12 | 0.8 h (attended) |
| b10 | J-full | P {50, 100} | 8 | 0.9 h (attended) |
| – | Canaries | | 8 | 0.4 h |

- **b6, B2:** 7 arms:
  - char-kit;
  - char-sender at parity (20/20/20; critique 17);
  - char-sender tiered (20/10/5, owner 20);
  - ship-kit-56;
  - ship-sender-56 at parity;
  - ship-sender-56 tiered;
  - ship-sender-28 tiered.

  Each arm runs at V {100, 300, 500} with P stepped 50 → 100 → 200 → 400 inside the run (90 s per step), 3 repeats; the tiered arms skip V 100. The block also covers:
  - targets S-T, F1 and F2, with character or ship phantoms;
  - a classify-every-10-builds arm;
  - sync/RPC D {0, 250} × R {0, 2,100/s};
  - a 60 fps subset for characters.

  Points above 1 GbE run virtual only (critique 8).

Stage 0b total: about 18.3 h, or about 22 h with re-runs. That is two overnight chains (b3–b8), one attended session (b1, b2, b9) and b10 late.

**Stage 0c**

| # | Block | What | Runs | Time |
|---|---|---|---|---|
| c1 | B5, database burst | 50/100/200 simultaneous × 3; 25 real logins as calibration; sustained 5, 10 and 20 arrivals/s | ~15 | 0.75 h |
| c2 | B7, fantasy worker A/B | 200 players with 100 character phantoms + about 290 monsters: `coarse` against `halves` with `MESH_MONSTER_CELL=E`; 2 load points × 3 | 12 | 0.6 h |
| c3 | B11, isolated-spaces floor (gap C) | S {1, 4, 16} × total load {0, 100} × 2 | 12 | 0.5 h |
| c4 | B3-loss (gap F) | 1% and 5% at P 400 × 3, if simulation is available | 6 | 0.4 h |

Stage 0c total: about 2.3 h, or 2.7 h with re-runs.

**Total:** about 31 h of measurement, about 37 h with re-runs (E). That is five overnight chains plus two attended sessions.

It is more than the protocol part's 22–26 h, because of the added blocks: parity arm, awake monsters, crossings, floor, spaces and the 60 fps subset. Dropping V = 100, the 60 fps subset and J-full saves about 3 h.

**Why this order:**
- B1 comes first because the kill rule can end W1 at once, and k drives the geography.
- N² runs before any grid, because it fixes the fit form.
- K runs before the grid, so the grid needs one rate set.
- Calibration runs before the 9 h fan-out block.
- The geographic confirmation runs last in 0a, because it needs the refit.

---

## 3. Gates and thresholds per game

**Evaluation rules for every gate:**
- The tick statistic is the p95 over **non-AOI tick frames**, from merged 0.05 ms bins over the run (`MESH_WORK_BINS=1`). AOI ticks are one tick in twenty, exactly at the 5% boundary, so they are reported separately as p99 and max (critique 7a).
- **"Tick send work"** is the reply's G1 metric: the kit movement loop (the residual, on the sender arms) plus the sender's time. The full kit tick is reported beside it (critique 7e).
- Every threshold is stated in ms/s and in ms/frame at the stated rate (critique 7b).
- Saturated windows are excluded: over 20% of frames never waited, utilisation at or above 0.95, or (fan-out) over 2% of frames with 2+ ticks.
- A G1 verdict comes from sink rows under 1 GbE, or from virtual rows plus B3's per-packet cost with its confidence interval (critique 8).
- The floor limit is 600 ms/s (60% of budget at any fps). c0 is 0.60 ms/frame (M, 0.71), which is 18 ms/s at 30 fps and 36 ms/s at 60 fps.

**Space (30 fps / 20 Hz / 0.02 s; distances × `MESH_SHIP_SCALE`)**

| Gate | Case | Pass | Fail leads to |
|---|---|---|---|
| G1-S target | 64 phantom-owned mirror ships × V 128 (64 mirrors + 64 AI), shared process | Tick send work p95 ≤ 3.0 ms and ≤ 30 ms/s; wire downlink p95 ≤ 100 kB/s per client; every pair within 1 km gets ≥ 10 Hz | Compact payload, tiers |
| G1-S stress | 400 × V 500 (400 mirrors + 100 AI), dedicated process | ≤ 6.0 ms p95 and ≤ 360 ms/s; downlink p95 ≤ 100 kB/s; ≥ 10 Hz for ≥ 99% of pairs within 1 km; ≥ 1 Hz for every subscribed pair | A lower target V, admission, or the kit-loop patch (BGNM:695-704) |
| G2-S | The team's target on one process, with the arm that passed G1 | The refitted model **and** a measured run both give mean ≤ 800 ms/s (80%) and tick-frame p95 ≤ 33.3 ms | Pass means no workers or shards |
| G3-S (Phase 0: e and k only) | Feeder ship puppets against AI owners | c0·30 + e·n_target + ℓ ≤ 600 ms/s, and k_A ≤ 0.5. The two-process part is deferred | No workers |
| Kill, ships | 500 kinematic copies | A floor of ≥ 600 ms/s ends workers and shards for ships; the ladder and admission are the answer | – |

**Fantasy (both rate sets, 60/0.03 and 30/0.02; visible range 80 m (A); budget 100 kB/s (A))**

| Gate | Case | Pass | Fail leads to |
|---|---|---|---|
| G1-F | F1 town square: 150 character phantoms + 30 awake NPCs in 100 × 100 m, 70% standing. F2 field boss: 100 character phantoms + 100 awake monsters within a 50 m radius, all fighting | Tick send work p95 ≤ 15% of the frame (2.5 ms at 60, 5.0 at 30; 150 ms/s) (A); downlink p95 ≤ 100 kB/s; tiered arm ≥ 10 Hz within 40 m and ≥ 5 Hz for all | Tiers, a per-cell population cap, a smaller visible range |
| G2-F | F2 on one process with the arm that passed | Mean ≤ 80%, tick-frame p95 ≤ budget | Cap, or a geographic split if wider than 2B |
| G3-F | Kinematic character puppets, unobserved (U) and observed (O) | k ≤ 0.5 | Kinematic stays an option only; minCellSize stays 250 m |
| G-GEO | B9: strips4/halves at B = 80, kinematic | ≤ 0.75 is the go for 125 m cells; the refit predicts every ratio within ±0.05; the band slope equals k/(1+x) within its confidence interval | minCellSize 250 m |
| Kill, characters | 500 kinematic character copies | A floor ≥ 600 ms/s ends workers and sharding for characters | – |
| Crossings | B10 | handoff_ready p95 ≤ 1.0 s (A) with 50 bots; the recommended geography's crossing rate ≤ 50% of B5's sustained handoff rate | A larger minCellSize, or metered crossings |

**Shared**

| Gate | Pass |
|---|---|
| Database | 200 simultaneous arrivals ready within 5 s at p95 with 0 errors; sustained capacity ≥ 2× the crossing load. Fail: meter arrivals at the directory |
| N² | A named change removes ≥ 80% of the excess over linear at the densest point, and the profiler agrees. The fit form (q·Q or q·N²) is fixed before any grid is fitted |
| Phantom validity (J) | Phantom tick work within ±15% of the bot run, after adding the measured upload cost; bytes within ±5% |

---

## 4. The geographic re-fit, the map-size rule and the 8 km recommendation

**4.1 Model.** Per-cell work in ms/s at a stated rate, fitted with block K's split into per-frame, per-step and per-second parts:

`w = c0 + Σ a_kind·O_kind + Σ k_kind·a_kind·U_kind + a_R·R + u·uploads/s + s·Σ_clients V + q·Q + ℓ·L + λ·n_links`

| Symbol | Meaning |
|---|---|
| kinds | Steered and kinematic characters; owned and awake monsters; sleeping monsters |
| R | Client-driven owners |
| Q | Local pairs, Σ n_b² over the occupancy buckets |
| L | Snapshots received per second |
| λ | Per neighbour link: about 0.12 ms/frame (M, DESIGN 0.69) |

- The fit uses every accepted window from B1, B4, B7 and B9, with confidence intervals labelled by CPU.
- The 0.71 fit is kept as a comparison: a different build, no clients, sleeping monsters.

**4.2 Halving a cell of width W with band B:**
- Peak child / parent = (x + ½ + σ·k·B/W)/(1 + x).
  - x = floor / variable load (about 0.05 for a loaded cell).
  - σ = the simulation share of variable load. Fan-out halves with the clients, so σ < 1.
- The break-even width at the 0.75 benefit gate is **W\* = 4σkB/(1−x) ≈ 4.2·k·B** at σ = 1. At k = 1 and B = 80 that is about 337 m, matching 0.71's 330 m.
- The measured band series already fits 0.555 + 1.0·B/W (M: 0.59 at B = 20, 0.62 at 40, 0.69 at 80).

**4.3 Smallest useful cell.**
- A cell may split only if W ≥ W\* **and** each child is at least B wide. The second rule is code, not an estimate (C: `MeshLayoutValidator.cs:185-190`).
- The map's `minCellSize` is also enforced (rule 7, :194-203), and so is the band-neighbour cap (rule 8, :207-217).
- So s_min = max(B, W\*/2), rounded up the halving ladder from the base. The puppets part's "32–80 m" is corrected to this (critique 10).

| k \ B | 50 m | 80 m | 120 m |
|---|---|---|---|
| 1.0 (today) | W\* 210 → 125 m | W\* 337 → **250 m** | W\* 505 → 500 m |
| 0.7 | 147 → 125 m | 236 → **125 m** | 354 → 250 m |
| 0.5 | 105 → 62.5 m | 168 → 125 m | 253 → 250 m |
| 0.25 | 53 → 62.5 m (band-limited) | 84 → 125 m (band-limited) | 126 → 125 m |

(Ladder from a 1,000 m base, E.)

**4.4 Total CPU of a split map, with the floor and links (critique 10):**

`T(W)/T(S) = [ (S/W)²·(c0 + n_nb·λ) + L·(1 + k·(4B/W + 4B²/W²)) ] / (c0 + L)`

- n_nb ≤ min(8, maxNeighbours).
- Without the floor and link terms, at B = 80: 1.35 / 1.74 / 2.69 / 5.20 for W = 1,000 / 500 / 250 / 125 m at k = 1, and 1.17 / 1.37 / 1.84 / 3.10 at k = 0.5 (E).
- Host cores cap the busy cell count first: about 8–10 busy cells on a 12-core host (E; DESIGN 0.68 found about 1.5 cores per 150-player cell).

**4.5 Crossings.**
- Each moving player crosses an edge 4v/(πW) times per second. At 5 m/s (A): 0.0064/s at 1,000 m, 0.026/s at 250 m and 0.051/s at 125 m.
- About 7 database requests per crossing (E; B5 and B10 measure it).
- Example: 300 players, 30% moving, in 125 m cells make about 4.6 crossings/s, about 32 requests/s. That is checked against B5.

**4.6 Recommendation method for the 8 km map.**
1. From the refit, take k_char (kinematic if G3-F passes and the user adopts it), σ at F-case client density, x from c0, and B = the fantasy visible range (A: 80 m).
2. Compute W\* and s_min.
3. Choose the base cell: the smallest power-of-two fraction of the map that meets all of:
   - W_base ≥ 2·W\*;
   - the off-peak population of one base stays under about half of one process's capacity;
   - the floor cost from F is acceptable when empty bases sleep.
4. Planner settings:
   - `minCellSize = s_min`;
   - `maxCellsPerBase = min((W_base/s_min)², core budget)`;
   - validator `maxNeighbours`;
   - weights k_p and k_m (awake);
   - cut placement away from crowds (the 0.78 crowd-on-cut point);
   - refuse crowds narrower than 2B.
5. Check the crossing rate against B5 and B10.
6. Confirm with B9: strips4/halves ≤ 0.75 is the go for 125 m.

**Today's answer (E, before measurement):**
- A 1 km base cell, so 64 bases.
- A busy base splits to 250 m today (k ≈ 1, up to 16 cells), or to 125 m with kinematic puppets at k ≤ 0.7.
- The band rule stops a further split to 62.5 m.
- The alternative is a 2 km base (16 cells) when off-peak population per 2 km stays under about half a process (150 players at 60 fps: DESIGN 0.68, labelled with the old CPU).

**4.7 Map-size table for the fantasy game** (critique gap E; the refit fills it with measured k and B; values below are E at B = 80)

| Map side | k ≈ 1 (today) | k ≤ 0.7 (kinematic) | Suggested mode |
|---|---|---|---|
| ≤ 0.35 km | No split pays (W < W\*) | One split if W ≥ 236 m | **Static.** Levers: sender tiers, visible range, population cap |
| 0.5 km | One halving to 250 m (ratio about 0.68); 250 m does not split | To 125 m | Static unless its peak exceeds one process |
| 1 km | Base = map → 250 m (≤ 16 cells) | → 125 m (≤ 64) | Dynamic if busy |
| 2 km | 4 × 1 km bases, or 1 × 2 km | As above | Dynamic |
| 4 km | 16 bases | | Dynamic |
| 8 km | 64 bases | | Dynamic |

Ratios, at x = 0.05 and σ = 1: 0.60 at W = 1,000, 0.68 at 500, 0.83 at 250 when k = 1.

The band test only uses B/W, so the rig's 600 m ground carries to 8 km through B/W. Synthetic players never cross a cut, so handoff cost comes from B10 and B5, not from the A/Bs.

---

## 5. Results format (one format for both teams and both games)

**JSONL**, schema `mesh-p0/1`, written to `Logs/p0/<team>_<game>_<family>_<stamp>.jsonl`. There is one row per accepted run or step, plus a `summary` row per (case, arm) with median and range.

- **Header fields:**
  - `schema`, `team` (rig | space), **`game` (fantasy | space | neutral)** (gap J), `model` (geographic | spaces | none);
  - `family`: presence, observed, fanout, targets, syncrpc, packets, density, rates, floor, prewarm, workerab, dbburst, abgeo, crossings, spaces, calibrate, fit; S1, S5 or S8 for the space team;
  - `case`, `arm`, `stage`, `repeat`, `seed`, `order`;
  - `started_utc`, `host`, `cpu`, `cores`, `os`, `power_plan`, `build` (manifest sha1), `kit_patches` (CE-11, CE-12 shape);
  - `rates` {fps, fixed_step_s, tick_hz}, `capped`;
  - `accepted`, `problems[]`, `excluded_reason`.
- **Load fields:**
  - `kind`, `owned{kind:n}`, `puppets{kind:n}`, `awake_monsters`;
  - `clients{real, phantom_virtual, phantom_sink, observer_kind}`;
  - `v_move_mean`, `v_move_p95`;
  - `density`, `visible_range_m`, `band_m`, `payload_B`, `budget_kBps`, `scale`.
- **Metrics:** a list of `{name, stat, value, unit, label: M|E|A}` from a fixed vocabulary:

| Group | Names |
|---|---|
| Work and ticks | `work_ms_per_s`, `work_ms_per_frame`, `tick_frame_ms`, `nonaoi_tick_frame_ms`, `aoi_tick_ms`, `other_frame_ms`, `tick_send_ms`, `kit_sync_ms`, `kit_aoi_ms`, `kit_move_ms`, `multi_tick_per_1000`, `sat_pct` |
| Per-unit costs | `c_pair_ns`, `residual_ns`, `pkt_main_us`, `pkt_net_us`, `sync_scan_ns`, `rpc_send_ns`, `upload_in_us` |
| Traffic | `downlink_wire_kBps_per_client`, `entity_state_kBps_per_client`, `datagrams_per_s_per_client`, `rate_hz_by_tier`, `reliable_records_per_s` |
| Process | `thread_ms_per_s{class}`, `process_cores`, `mem_MB`, `link_kBps_in`, `link_kBps_out` |
| Latency (p50/p95/p99/p99.9/max/n) | `latency_ms{handoff_ready, db_ready, forwarded_hit, wake, frame_change_apply, frame_change_ack, frame_change_flip}` |
| Space S5 and S8 | as the protocol part lists them |
| Fit rows | `c0`, `a_kind`, `e_kind`, `k_kind`, `a_R`, `u`, `x_P`, `x_AI`, `s`, `q`, `ell`, `lambda`, each with a confidence interval and a fit id |

**Markdown summary** (`p0load.py report --import` merges both teams' files):

| Game | Case | Arm | Kind | Rates | Owned / puppets / clients / V | Work ms/s (median [range]) | Non-AOI tick-frame p95 ms | Tick send p95 ms | Downlink p95 kB/s | Key metric | Gate | Verdict | n |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|

**Map-size table** (a report output): map side × B × k gives W\*, s_min, the base, the maximum cells and the suggested mode.

---

## 6. What comes after Phase 0

**Geographic model (the fantasy game): slices 2b–6 re-planned with the measured k.**

| Slice | Change from Phase 0 |
|---|---|
| 2b, manual split | Kinematic puppets available to Dynamic maps (if G3-F passes and the user adopts them). The pre-warm gate is paced from B6 (ms per puppet, frames over budget). The drain pacing default comes from B10, replacing the planned 4/8/12 per second test |
| 2c, merge and drain | Drain rate from B5 and B10 |
| 2d, supervision | Unchanged |
| 3, automatic policy | The planner uses the refit (c0, a_kind, k_kind with awake monsters, s, the q·Q or N² form, ℓ, λ), W\*, s_min on the ladder, maxCellsPerBase, cuts away from crowds, refusal below 2B, and host headroom from F. The benefit run predicts within ±15% |
| 4, buildings | Unchanged |
| 5, central restart | Unchanged |
| 6, operator path | Setup checks gain the map-size guidance ("maps below W\*: keep Static") and a per-map population cap field |

- Small maps: Static by default, with sender tiers and a cap.

**Bubble model (the space game):**
- The bubble directory is built on Slice 2a's versioned ownership map (coordinator, store, recovery), with entries per bubble.
- The add-on's game-neutral **isolated-spaces policy** places whole spaces on processes: fantasy dungeons, interiors and instances, and the space game's bubbles. It never decides merges, splits, roam exits or arrivals: for bubbles that is the space game's `BubblePolicy` (built by them from our Part 3, contract §L.11), whose outputs (bubble created or released, member gaps for D_nbr, D_cut,min) are this policy's input. Its placement rules use B11 and F.
- Sender Phase 1 in production adds:
  - byte budget, fairness and staleness;
  - the at-rest keepalive;
  - two-way Withhold driven by the policy through the Mark calls;
  - the compact variant for stress;
  - the kit-loop patch, if G1's residual requires it.
- Conditional on the gates:
  - M2 AI budget; M3 hot host; ladder L0–L3 (L4 replaced by "shrink V");
  - admission;
  - W1 workers only if G3-S passes. That needs the compact link (121–123), the puppet body seam, seats, body verbs, event replay and a new lend/borrow schema;
  - W2 shards only on a measured need.

**Shared:**
- The kinematic puppet option ships (0.73+), default off.
- A load report v2 gains a version field and the new counts.
- Arrivals are metered at the directory if the database gate fails.

---

## 7. Open questions

**For the user**
1. The chain4 gate failures: fix them before Phase 0, or accept them and measure the base rate?
   - `crash`: 1 player puppet teleport in E;
   - `wake`: 9 player puppet teleports in C2.
2. Fantasy inputs:
   - server fps and physics step, visible range, and downlink budget;
   - population per map and per hotspot (town, boss, siege);
   - movement mode (NotSecure or ServerAuthoritative);
   - how small the "far smaller" maps are, and how many there are;
   - whether dungeons or instances run in numbers.
   F1 and F2 are our assumptions until then.
3. Accept the 0a/0b/0c staging and about 6–8 weeks plus about 37 h of rig time? Or trim, for example by dropping 0c, V = 100 and J-full?
4. Are the kinematic puppet trade-offs acceptable as an option?
   - Puppets no longer body-block owned server-simulated characters.
   - Server animation on puppets is frozen.
   - Mounted puppets fall back to Steered.
5. Send the contract as `MultiplayerARPG.MMO.Mesh.Sync`, with `MarkSourceChanged`?
6. ~~May the space team get a copy of the rig parts?~~ Yes (2026-10-05): they build their S9 load generator on a copy (`space_reply_2026-10-05.md` A.6).
7. Machine B for two attended sessions; the link speed; the RAM available for bots.
8. May we create meshburst001..200 rows in the MySQL database shared with Relics?

**For the space team**
1. ~~CE-12 in the stamp-in-handler shape.~~ Settled: their post-pass shape (§0.9).
2. The header bit layout (mode and flightMode packing, the compact indicator) and the quantized layout, so the golden fixture can be filled.
3. SMC:390 sends reliably forever for an unowned ship after `Teleport`, `AssignEntity` or `Respawn`, because the acknowledgement only changes on owner uploads (SMC:366-369).
4. Accept the contract path, namespace and renames; implement `IsAtRest`; treat a NaN distance as near.
5. Four costs that grow superlinearly or allocate:
   - the O(AI × spawned) target scan;
   - server projectile GameObjects that each run `RaycastAll` every frame;
   - the shield sync field written every frame;
   - parked bodies that may never sleep (E).
6. Their S1, S5 and S8 rows in `mesh-p0/1` with `game: space`, and B1/B2 rows on real `ShipEntity`.
7. The target fight numbers, the lock-visibility decision and the low-cruise choice (still open from the reply).
8. Kit hardening: `reader.SetPosition(recordStart + length)` after BGNM:609.
9. Their NMEM is `partial`, extended by `_Game/Scripts/KitHooks/OriginShiftKitHooks.cs`. Confirm that its `WriteServerState` has no per-receiver side effect beyond the kit's, for the adapter's allow-list.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Slice 2a gate failures delay the start; puppet teleports make the kinematic acceptance baseline noisy | Step 0 base-rate runs |
| Phase 0 effort (6–9 weeks) is well above the proposal's 2–3 weeks | Staging: 0a answers the kill rule and the geography first |
| Phantom mechanics unverified (E): ForceHide ordering, a ready player served without EnterGame, the factory before Start, the AOI swap | b1 and a1 acceptance, before any grid |
| LiteNetLib manual mode untested; thread names under Mono unknown | Fallbacks in §1 steps 2 and 5 |
| Sink capacity (one thread cannot take 270k datagrams/s) and the 1 GbE limit | 4 sink threads; points above 1 GbE run virtual only |
| Three writers on one handler slot | One owner (`MeshMovementHandlerSlots`) plus the order test |
| CE-12 late, or in a post-pass shape | Adapter proven without it; the shape is refused; rows flagged |
| Kinematic puppet gameplay drift (hits on a stale pose ≤ 0.8 m, inside the 2 m buffer, E) | Every scenario must pass with it |
| Ship stand-ins are (A); the scale factor keeps rates and bytes exact, but physics-query CPU under scaling is (E) | A dense scale-1.0 row; the space team re-runs on `ShipEntity` |
| Machine drift, thermals, background processes | Canaries, CPU labels, the build manifest in each row |
| The stray-reference check fails the package build | Registry §0.15, regex fix in step 1 |
| The 0.71 open `NullReferenceException` ("Collecton is null") may reappear under dense load | CE-11 should have removed it; exception lines fail any run |
| The small-map conclusion may disappoint | Stated plainly in the summary and in §4.7 |

---

## 9. Critique fixes: where each one is applied

| # | Fix | Where |
|---|---|---|
| 1 | One phantom chain, two observer kinds, virtual and sink modes; drop-mode wrapper dropped; sender claim corrected; bots calibrated against character phantoms | Step 5; b2; b10 |
| 2 | Kinematic puppets and kinematic/inert load disable movement explicitly | Step 3 |
| 3 | One slot owner, lazy wrap, order test | Steps 3 and 6 |
| 4 | Contract frozen as promised: path, IsAtRest, Mark calls, 2-argument constructor plus overload, ports moved out, limits documented, NaN noted | Step 1 |
| 5 | CE-12 stamp shape only, NMEM dash clear, RigCe12Check tests the shape, adapter test against a patched handler, reliable counts separated | §0.9; step 6 |
| 6 | One registry for names, env keys (all in ENV_KEYS), rates (`MESH_RATES` fps/step, send rate fixed), thread CPU (in-process), tick tagging (override plus one line), one ship family, one runner | §0.15, §0.16; steps 2, 4, 7 |
| 7a | AOI ticks tagged; gates on non-AOI p95; 0.05 ms bins | Steps 2; §3 |
| 7b | Every gate in ms/s at a stated rate | §3 |
| 7c | Capped by default; ms/s with block K | §0.7; a3 |
| 7d | Physics step given in seconds | §0.16 |
| 7e | G1 metric is the reply's (movement loop plus sender); full tick reported | Steps 2 and 6; §3 |
| 8 | G1 from sink rows under 1 GbE, or virtual plus B3 | §3; b6 |
| 9 | Staged 0a/0b/0c; puppets matrix merged into B1 | §1, §2 |
| 10 | Band rule cited as code; s_min = max(B, W\*/2); c0, links and cap added to the total-CPU formula | §4 |
| 11 | Awake monster sub-block | b4 |
| 12 | One scale factor across positions, speeds, ranges, tiers and gate distances | Step 7 |
| 13 | Only the kinematic character body ships in Phase 0; ship puppets via the feeder; G3-S covers e and k only; ship worker A/B deferred | Steps 3 and 7; §3; step 9 |
| 14 | Load report wire format frozen | §0.10 |
| 15 | Deny-list entry only through Kinematic's `extraTypeNames`; CE-12 check behind a toggle | Steps 3 and 6 |
| 16 | Kit bench dropped; residual probe (wrappers only) plus regression on P·V | Step 6; b6 |
| 17 | a from a coarse arm; V_move defined; parity arm added | a4; step 5; b6 |
| 18 | Payloads 54/56/28 B; the compact bit is (A) until the team decides | Step 7 |
| 19 | `run_window`/`cell_window` taken from abload.py; parsers in p0tools; NMEM partial noted | Step 4; §7 |
| 20 | Stray regex extended | Step 1 (done inside Phase 0, because it adds about 40 rig and test types) |

| Gap | Where |
|---|---|
| A, handoff under load | b9 |
| B, process CPU and memory floor | a7 |
| C, isolated-spaces floor | c3 |
| D, subscription bursts | b8 |
| E, map-size rule | §4.7 |
| F, packet loss | c4, or skipped with a stated reason |
| G, reply §7 items 4, 5, 7, 8 | Classify-rate arm and 119-only framing in b6 (the 3-byte framing option is computed analytically, because it needs a client reader); p0geom.py (step 8); dirty-field and RPC counters from the send meter and `ServerMeshSyncLoad` |
| H, server-authoritative owners | b3 SA row |
| I, wake on a crossing | b9 |
| J, `game` field | §5 |

**Files read for this sheet** (all read only):
- `Scratch/bubble_proposal.md` (§3, §6)
- `Scratch/reply_combined.md` (update, summary, Part 1 §6-7, Part 2 §5-10, Part 3 §0)
- `Scratch/design_071_draft.md`
- `Scratch/chain4.log`, `chain4_ce11_wide_crowd.log`, `chain4_gate_static.log`, `chain4_gate_dynamic_idle.log`, `chain3_ab_crowd_wide_on_cut.log`
- `C:\Users\brend\.claude\plans\i-would-like-you-woolly-locket.md` (slices 2a–6)
- `A:Docs/ServerMeshing/DESIGN.md` (rows 0.53, 0.68, 0.69)
- The code sites cited above in `Mesh/`, `TestMap/`, `Tests/`, `Tools/`, `Kit/` and `LNL/`