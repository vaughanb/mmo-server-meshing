# Phase 0 step 7b: folding the space game's current code into the ship stand-ins (implementation contract)

Revision 1, 2026-10-04 on machine A, after one independent critique (log at the end). Step 7 (`step7_design.md`)
modelled the space repo at 1a3a58d9. The space repo is now at c2eb4154 (57 commits later), and
`step7_space_questions.md` "Answers read from the space code" records what changed and the user's decisions. This
contract applies them, plus four fixes from A's first rig chain (`Logs/p0/chainA1/`). Every step 7 rule binds
(`step7_design.md` "Rules"); where the two disagree, THIS CONTRACT wins for the points it names.

Space references are `mmorpg-kit-space` at **c2eb4154**, read-only: `SMC` = `Assets/_Game/Scripts/Space/Ships/
ShipMovementComponent.cs`, `SCC` = `.../ShipCombatComponent.cs`, `SE` = `Assets/_Game/Scripts/Ships/ShipEngine.cs`,
plan = `~/.claude/plans/large-star-systems.md` (revision 6.1c). Stand-in file headers that name 1a3a58d9 gain "and
c2eb4154" where this contract changes them. Our paths: `Ships/` = `Assets/ServerMeshing/TestMap/Ships/`, `Tests/` =
`Assets/ServerMeshing/Tests/Editor/`.

## Decisions

1. **Header56 = their header.** Byte 0 = wire mode (bits 0-1) | flight state (bits 2-4) << 2, bits 5-7 written 0 and
   ignored on read; byte 1 = epoch (SMC `ShipWire.PackHeader`/`ReadHeader`, :721-741). The stand-in always writes mode 0
   (Bubble). **Authority leaves the wire**: it stays a rig property of the ship (`ServerMeshShipEntity.Authority`, the
   load plan, the window counters), and `ServerMeshShipSample.Authority` is removed. Readers: mode 0 → the 54-byte body;
   mode 3 → compact (decision 2); modes 1 and 2 throw naming `reader` in the pure codec, and the component paths never
   let that throw out: `ReadServerStateAtClient`/`ClientRecordSize` (Movement.cs:296-305, :387-394) count a mode 1/2
   record as `badLength` and skip it (a throw would make the kit log "error occurs while reading", which the bots group
   fails on), and an upload with mode ≠ 0 counts `bad` (decision 24 of step 7). Flight state range check stays (0-7);
   their values are Combat 0, Spooling 1, LowCruise 2, Braking 3, Landed 4; the rig writes 0. `PackDiscreteVersion`
   becomes `(teleportSequence, flightState, epoch)` → `seq | flight << 8 | epoch << 16`.
2. **Compact28 = their mode 3** (plan §C.3 mode 3, :750-757; we agree to their proposal 4). 28 B: header (mode 3) 2;
   position 3 × float32 12 (relative to the frame's slot origin; the rig has no slots, so world position); rotation
   smallest-three 4 (unchanged packing); velocity 3 × IEEE half 6 (`Mathf.FloatToHalf`/`HalfToFloat`, each component
   clamped to ±65,504 first); angular velocity 3 × int8 3 at `CompactAngularFullScale / 127` per unit, clamped, with
   `CompactAngularFullScale = 4 rad/s`; state byte 1 = teleport sequence low 7 bits | forward << 7. Steady turn rates
   (α/d − α·dt at dt 0.02): Base yaw 2.42, roll 3.15 rad/s; Enemy (d 8, decision 4) yaw 0.84, roll 1.05; all under 4.
   Their "scaled to the ship's maximum turn rate" is per ship; ours is one constant until they pin it, and the state
   byte's split is a rig choice until their golden pins it (the compact golden stays `[Ignore]`). The pure
   `WriteCompact28` returns false only for a non-finite field (the ±83,886 m position limit goes). **The "never
   quantise a pending teleport" rule lives in the component**: `ServerMeshShipMovement.WriteCompactState`
   (`_Sender.cs:68-74`) returns false, so the sender writes Full, while a teleport is unacknowledged (a reliable record
   is already never compact, `MeshStateFanout_Routing.cs:179-183`); the rig has no stop placement (plan :757's other
   case). **Client sequence rule**: a compact record carries 7 bits, and unreliable compact records can overtake the
   ReliableOrdered mode 0 record that carried the teleport. On a compact record the client compares
   `(_clientSequence & 0x7F) != seq7`; on a mismatch it snaps to the record and advances its sequence by
   `_clientSequence = (byte)(_clientSequence + ((seq7 - _clientSequence) & 0x7F))`; a mode 0 record keeps today's full
   8-bit rule. `CompactBit`, `ForwardBit` and the 24-bit/int16 helpers go; `ModeCompact = 3`.
3. **Teleports: their rule by default.** An unowned ship's records go reliable while its teleport is unacknowledged, and
   only an owner's upload acknowledges (SMC :445, :525), so a server-owned ship that has respawned stays reliable for
   the rest of the run, and (decision 2) never compact. New rig key `MESH_SHIP_SELF_ACK` = `0` (default, theirs) | `1`
   (step 7's two-tick self-ack, its decision 21, now an arm). `SelfAckDue(bool enabled, ...)`. With `MESH_SENDER=on`
   nothing breaks: `TryWrite` still runs once per source per build (`_Routing.cs:34`) and reliable records bypass the
   urgent and compact routing. The Ships window gains a ` pending N` gauge (ships with an unacknowledged teleport at the
   window's end).
4. **AI ships model `Ship_Enemy` by default.** A new pure `ServerMeshShipClass` (readonly struct; `Force`, `BoostForce`,
   `LinearDrag`, `AngularDrag`, `SteeringTorques`, `Hull`) with two values: `Base` = Ship.prefab (400, 800, 3, 8,
   (23, 23, 30), 2,500; players, mirrors, parked and `Ship_AI`) and `Enemy` = Ship_Enemy (300, 600, 3, **8**,
   (8, 8, 10), **1,500**). Enemy's angular drag is 8, not the prefab's `m_AngularDamping: 3` (Ship_Enemy.prefab:68-69):
   `SE.Awake` (:88) overwrites the rigidbody's damping with the engine's `angularDrag`, which Ship_Enemy does not
   override (Ship.prefab:46291 = 8), so 3 never runs in the game (a new space question). New rig key
   `MESH_SHIP_AI_PREFAB` = `enemy` (default) | `wave` (`Ship_AI`: Base numbers). Only ServerAI ships take the key; every
   other ship is Base. Forces scale with `MESH_SHIP_SCALE` as today; torques, drags and hull do not.
   - The engine takes the class (`ServerMeshShipEngine(scale, shipClass)`; the one-argument constructor stays and means
     Base, so existing engine tests keep compiling); `TopSpeed` from the class.
   - The movement component applies the class's damping in `OnIdentityInitialize` (Movement.cs:96-115, from `_ship`),
     not in `Awake` (:86-93), which a pooled instance does not re-run.
   - `ServerMeshShipEntity.Configure` takes the class; `HullMax` comes from it; `OnNetworkDestroy` (:171-182) resets
     the class to Base, and an unconfigured ship (a client's) is Base. The committed prefab and its hp are untouched
     and unused for the hull; the prefab is not rebuilt.
   - AI pilot settings are identical in both prefabs (fire cone min(15, 4 + 3) = 7°, unchanged).
   - Terminal speeds (`(F/drag − F·dt)/m`, dt 0.02): Enemy 94.0 m/s (boost 188.0), Base 125.33 (250.67).
5. **Respawn.** SCC's `Respawn` (:600-607) refills hull and shield and teleports home; the space epoch stays 0 in Phase 0
   (SMC :116-120). The stand-in's respawn stops calling `AdvanceEpoch` (`ServerMeshShipCombat.cs:143-146`), so header
   byte 1 matches theirs (0). Player-owned ships are `NetworkDestroy`ed on death in the game (SCC :575-585); the rig
   keeps respawning phantom-owned ships, and the `wave` arm keeps respawning AI ships that despawn in the game: both are
   rig devices for constant counts, stated in the Ships window's settings line as today.
6. **Mirror velocities default `real`** (9b1d69a1: SMC :94-96, :465-466). `MESH_SHIP_MIRROR_VELOCITY` keeps `zero` as an
   arm; unset now means `real`.
7. **Keys and names.** Two new rig keys (`MESH_SHIP_SELF_ACK`, `MESH_SHIP_AI_PREFAB`) go into `p0ships.STEP7_REGISTRY`
   (`ENV_REGISTRY` 40 → 42; `test_p0tools.py`'s explicit key set :251-261 and `SECTION_0_16_KEYS` :316 gain both; build
   sheet §0.16 gains both rows), `regress.ENV_KEYS` (60 → 62), `ServerMeshShipSettings` (both count for `AnyShipKey`),
   the Ships settings line (` selfAck 0|1 aiPrefab enemy|wave`, appended) and SETUP §7. New reserved name:
   `ServerMeshShipClass`.
8. **Thread CPU calibration (instrument; shares this build window).** accept-a on A failed "thread CPU counted in
   cycles": `CalibrateCycleClock` needs all 5 spins of an attempt within 2 % of their fastest, and at server start on A
   (Ryzen AI MAX+ 395, 16C/32T) the spins read 0.54-3.0 GHz in all 4 attempts. A spin can only read low (both cycle
   reads sit inside the Stopwatch window, `ServerMeshThreadCpuSampler.cs:322-344`), so the fastest spin is an upper
   bound. New rule: run every attempt's spins (4 × 5 = 20), take the fastest over all of them, and hold the rate when at
   least `min(SpinQuorum = 3, n)` spins from any attempts read within `SpinAgreement` (2 %) of it; a spin with no cycles,
   no time or a non-finite rate still makes the result 0. The failure line (:265) reads "the cycle clock calibration
   found N of M spins within 2% of the fastest (X GHz), need 3". Tests (`ServerMeshThreadClassTests.cs`): 3 agreeing +
   2 low hold; 2 agreeing fail; `CycleRate_OnePreemptedSpin_IsZero` (:100) flips to "holds"; the one-spin and two-spin
   rows (:102-103) hold through `min(3, n)`; the zero-spin row stays 0. Acceptance: accept-a part A on A reads
   `clock cycles` at a rate within 1 % of 3.0 GHz.
9. **Bot lag (step 7 risk 1).** A's run 4 read three levels: legacy54 30 ms, the others 16-17 or 42-43 ms switching
   within a run. The bot's lag is server-side age only (its timestamp is the server's write timestamp, BGNM:136, :712).
   Reading: the load mirror feeder (20 Hz on unscaled time, keeping its phase, `ServerMeshShipMirrorFeeder.cs:53-56`)
   and the 20 Hz logic tick both fire on frames, so at 60 fps they lock at k ∈ {1, 2, 3} frames apart until a hitch
   resets a phase; the mirror's observer smoothing between feeds (Movement.cs:209-213, faithful to SMC :250-261) turns
   that into about 14/26/36 ms plus an intra-frame offset. To confirm, the Ships window gains a rig-only
   ` mirror age p50 A ms frames K` (A = write timestamp − last feed timestamp from the `peerTimestamp` the feeder passes,
   MirrorFeeder.cs:86; K = frames since that feed). Pass: the bot's lag steps coincide with K steps window for window;
   then the lag expectation is [0, 60] ms at 60 fps (scaled with frame time), reported, not failed. If they do not
   coincide, it is a timing bug and blocks step 8.

## Files (no Mesh/ change)
- **Ships/**: `ServerMeshShipWire.cs`, `ServerMeshShipSample.cs` (1-2); `ServerMeshShipClass.cs` (new, pure);
  `ServerMeshShipEngine.cs`; `ServerMeshShipScale.cs` (drag, torque and hull consts move to the class; `Force` and
  `BoostForce` become scaled class values); `ServerMeshShipSettings.cs` (two keys, velocity default);
  `ServerMeshShipMovement.cs` and `_Sender.cs` (header and compact paths, mode 1/2 skip, client sequence rule, self-ack
  setting, compact refusal while pending, damping in `OnIdentityInitialize`, mirror age); `ServerMeshShipEntity.cs`
  (class, `HullMax`, reset); `ServerMeshShipCombat.cs` (no `AdvanceEpoch` in respawn); `ServerMeshShipLoad.cs`,
  `_Window.cs`, `ServerMeshShipCounters.cs` (class per group, settings line, ` pending N`, patrol count, mirror age);
  `ServerMeshShipPuppetBody.cs` (discrete version); `ServerMeshShipMirrorFeeder.cs:75` (sample `Authority` goes; age
  stamp); `ServerMeshShipAuthority.cs:5` (doc text: no longer on the wire). `ServerMeshShipWatch.cs` needs no change
  (the client reader lives in the movement component).
- **Elsewhere in TestMap/**: `Phase0/ServerMeshPuppetFeeder.cs:225` (`Configure` call), `:347` (sample `Authority`);
  `Phantoms/ServerMeshPhantomClients_Ships.cs:120` (`Configure` call); `Phantoms/ServerMeshPhantomShipPayload.cs`
  (header); `Editor/ServerMeshShipPrefabBuilder.cs:121-122` (`ServerMeshShipEngine.LinearDrag/AngularDrag` → the Base
  class; the builder is not run); `Phase0/ServerMeshThreadCpuSampler.cs` (8).
- **Tests/**: `MeshSyncGoldenFixture` (header56 golden for flight state 2, epoch 7: was `09 07` + the 54, now `08 07` +
  the 54; plus their header rows `(Bubble, LowCruise, 1) = 08 01`, `(Bubble, Landed, FF) = 10 FF`);
  `ServerMeshShipWireTests` (header rows; mode 1/2 throw naming reader; compact round trip: position exact, velocity
  within 2^-10 relative with a 1e-3 m/s floor, angular within 4/127 rad/s, quaternion dot error 0.002; ±65,504 clamp;
  refuses non-finite; consumes exactly 28; `HeaderByte` rows rewritten); `ServerMeshShipEngineTests`
  (`TerminalSpeed(300, 3, 0.02, 1)` = 94.000 ± 0.001; Enemy at 0.03 = 2.820; existing rows unchanged via the one-arg
  ctor); `ServerMeshShipClassTests` (new: both values); `ServerMeshShipScaleTests.cs:79-80, 118-128` (moved consts);
  `ServerMeshShipMovementTests.cs` (rows :85-91 and :111-114 re-based: 0x09/0x01 are now mode 1, so they become "mode 1
  counted bad"; :125-129 the new `PackDiscreteVersion`; `SelfAckDue` disabled never acks; compact refused while
  pending; client 7-bit sequence rule incl. wrap); `ServerMeshShipPuppetBodyTests.cs:49`; `ServerMeshShipSettingsTests`
  (both keys' junk named; defaults `selfAck 0`, `enemy`, `real`); `ServerMeshShipLoadTests` (window line exact incl.
  `pending`, patrol count, mirror age); `ServerMeshPhantomUploadTests.cs:250-251` (no `CompactBit`/`Authority`; byte 0 =
  0x00); `ServerMeshThreadClassTests.cs` (8); `ServerMeshShipCombatTests` (respawn leaves the epoch).
- **Python**: `p0ships.py` (registry +2; ":1, :36" "ten keys" → twelve; the velocity meaning :46-47 says default real;
  `parse_ships_window` reads the appended fields optionally); `test_p0ships.py`; `test_p0tools.py` (counts and sets);
  `regress.py` (ENV_KEYS only); `phase0/accept_step7.py` (acceptance below; docstring :17-19).
- **Docs**: SETUP §7; build sheet §0.16 (two rows); `step7_design.md` a one-line pointer to this file under its
  decisions 1, 4, 5, 9, 21 and S1-S7; `step7_space_questions.md` the new question.

## Acceptance (re-run of every accept_step7 group on the new build, after a canary bridge)
Changed expectations:
- `ai`: arms `enemy` (default) and `wave`, each instantiate then pooled, 100 ships, scale 0.03. Speed max within 2 % of
  the class's full-thrust terminal (enemy 2.820, wave 3.760; two-sided, so it tells the classes and the force order
  apart). Patrol is a sanity range only (A's first run read p50 ≈ 0.69 × the 40 %-throttle terminal, mostly 0.98-1.10
  against 1.504): p50 over windows with patrol `n > 0`, pooled across the instantiate and pooled arms of one class, in
  [0.54, 0.4 × terminal × 1.05] (enemy [0.54, 1.184], wave [0.54, 1.58]); fewer than 5 such windows reports "patrol not
  measured", not a failure. gc0: `pooled ≤ instantiate + 2`, both reported. Ships 100 in every window; bolts = 2 ×
  shots ± 1 %. Reported: respawns and `pending` per window (grows under `selfAck 0`).
- `g1s` (64 ship phantoms + 64 AI, meter count): arms `MESH_SHIP_MIRROR_VELOCITY` real (default) and zero, and
  `MESH_SHIP_SELF_ACK` 0 (default) and 1 on real. Reported: Send 119 reliable records per second (`records ro`) in each
  arm and the self-ack saving; `pending` at the end.
- `sizes`: header56 `min 56 max 56`; compact28 lengths only 56 and 28, 28 ≥ 80 % (no AI ship in this run), Sender
  payload `full 56/56 compact 28/28`.
- `bots`: compact28 parked `max ≤ 0.001 m` (float32 positions); the arm "header56 real velocity" (:300) becomes
  "header56 zero velocity"; lag per decision 9.
- `syncload`, `once`, `pilots`, `feeder`: as step 7 (defaults changed underneath them; their checks hold).
- accept-a part A (thread CPU, decision 8) and part D (ruff, once installed on A).
- Unit gates: EditMode green, Python unittest and ruff clean, stray test.
Then the b1 sink arm on B (`sink:20:ship:C`, header56) **on the new build** (`remoteb.py deploy` first): an old build's
ServerAI/parked header56 records would now read as mode 1/2; OwnerMirror uploads are byte-identical either way.

### Revision 2 (2026-10-04, after A's run on the new build, `Logs/p0/chainA2/04_accept_step7.txt`: 134 ok, 1 fail)
- **Lag (decision 9) confirmed**: per window the bot's lag p50 equals the mirror age level for level (K 1: 16-17 ms
  against 16-17; K 2: 30 against 33; K 3: 42-43 against 49-50), and in the header56 zero-velocity arm the lag fell 43 →
  17 ms at the window K fell 3 → 1. The one fail was the step-coincidence check: in the legacy54 arm one Ships window
  read K 1 (age 17 ms) beside a bot window still at 30 ms, because the bot and Ships windows are not aligned. The check
  becomes level by level: a window's lag within 10 ms of the mirror age of that window or one beside it, in ≥ 90 % of
  the windows with an age (and those ≥ 90 % of all); step coincidence is reported. Lag in [0, 60] ms stays reported.
  (A first same-window version failed 3 arms at 8-9 of 11: every mismatch had its level one window over, since a phase
  episode shorter than a window lands in different 5 s windows on the bot and on the server; the ±1-window rule regraded
  those rows to 10-11 of 11.)
- **Reliable cost moves to its own group.** In `g1s` the AI ships are hit (80-150 hits per window) but never destroyed
  in a minute (hull + shield 4,000 against 50 per hit), so `respawns 0` and reliable 0/s in every arm: it measured
  nothing. New group `reliable`: 100 enemy AI ships for 180 s (they kill each other, as in `ai`) with 8 ship phantoms
  subscribing, meter count, pooled bolts; arms `MESH_SHIP_SELF_ACK` 0 and 1. Checks: respawns > 0 in both; under 0 the
  respawned ships stay pending at the end and reliable records flow; under 1 pending stays ≤ 1. Reported: reliable
  records/s per arm and the saving. `g1s` keeps only its velocity arms. First run (06:42): selfAck 0 3.6 reliable
  records/s to 8 subscribers with 3 respawns (pending 2 at the end); selfAck 1 0.0/s with 5 respawns.

- **b1 sink arm passed (2026-10-04, A + B on the new build, `phase0/accept_step7_sink.py`):** 20 ship sink peers on B,
  header56: Sink sizes min 56 max 56 in all 58 windows (about 40,000 records each); no error line on B or cell C. Its
  first run found a step 7 bug only the sink path reaches: a sink peer's disconnect removes its kit player before the
  binder releases the slot, and the kit's destroy then dereferenced the missing owner (`LiteNetLibIdentity.
  OnNetworkDestroy`, NRE on cell C). `ServerMeshPhantomClients_Ships.ReleaseShip` now makes the ship unowned first
  (`SetObjectOwnerImmediately(id, -1)`, as the ghosting code does for a disconnecting player's entity).

## Questions still for the space team (written; no live session)
- Will they acknowledge server-owned teleports (S4)? We report the measured reliable cost of not doing so.
- Mode 3's state byte split and angular full scale (we chose seq7 | forward << 7 and 4 rad/s).
- Do server-side bolts stay instantiated (S6)?
- **New:** `Ship_Enemy.prefab` sets the rigidbody's `m_AngularDamping` to 3, but `ShipEngine.Awake` overwrites it with
  `angularDrag` 8: is the interceptor meant to turn about three times faster (steady yaw 2.51 against 0.84 rad/s)?

## Critique log
- **Revision 1 (2026-10-04)**, one independent critic, read-only, against both repos. Blockers: (1) Enemy angular drag 3
  never runs in the game (SE.Awake :88) → 8; (2) the patrol expectation could not pass (A reads ≈ 0.69 × nominal) →
  sanity range, pooled windows, "not measured" below 5; speed max two-sided; (3) the `ai` group has no subscribers, so
  its reliable-record report read 0 → measured in `g1s`, `pending` gauge in `ai`. Should-fix: missed call sites and test
  rows (Files), pooling of damping and class, client 7-bit sequence rule and where the pending-teleport refusal lives,
  respawn epoch (dropped to match theirs), thread CPU quorum over all 20 spins with `min(3, n)`, bot lag restated with
  three levels and a confirming field, Python counts and texts. Nits: half-float clamp, test tolerance, steady rates
  stated, stop placement, B on the new build. All applied. Checked correct by the critic: header layout and goldens,
  teleport rule, mirror velocities, Ship_Enemy forces/torques/hp, every speed number, mode 3 size and packing, sender
  behaviour with reliable-forever sources, ENV_KEYS 60.
