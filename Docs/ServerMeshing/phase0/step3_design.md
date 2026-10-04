# Phase 0 step 3 — handler slot owner, kinematic character puppets, load-generator motion: implementation contract

Repository `C:\dev\Game Design\mmo-server-meshing`; add-on `Assets/ServerMeshing` (Mesh/ ships, TestMap/ rig, Tests/Editor
NUnit in namespace `MultiplayerARPG.MMO.Mesh.Tests`, compiled into Assembly-CSharp-Editor with no InternalsVisibleTo:
everything a test calls must be public and outside the server `#if` guard).

Read first (read-only): this contract; the survey synthesis
`C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\2cf88996-d970-455a-bb4e-fffe6bad122c\scratchpad\survey3\critic.md`
(gaps, hazards and plan, with current line numbers — the authority on WHERE things are) and, as needed, `verify.md`,
`puppets.md`, `kit.md`, `rig.md`, `tests.md` beside it; the build sheet's step 3 (`...\7cb7243d-...\scratchpad\phase0_build_sheet.md`
lines 494-578) for intent. Where they disagree: THIS CONTRACT, then critic.md, then the sheet.

## Rules (same as step 2; see `...\2cf88996-...\scratchpad\step2_design.md` "Rules" — all bind)
C# 9, compile against the overlap of netstandard 2.1 (runtime) and unity-4.8-api (tests): no `float.IsFinite`, no
`MathF`, no `ConcurrentDictionary.TryRemove(KeyValuePair)`, no `ConditionalWeakTable.AddOrUpdate/TryAdd/Clear`. XML docs on
every public type/member starting with its name; minimal other comments; errors with context, nothing swallowed; no
allocation per frame/tick; log lines one physical line, invariant culture, no braces, no "Exception", no capitalised
Split/Merge/Retire/Retired, fixed numeric formats. Stray-reference rule: Mesh/ never names a TestMap/Tests type (code or
comments); TestMap never names a Tests type; new TestMap/Tests type names are distinctive and prefixed. Mesh/ must also not
use the words Mode, Placement, Driver, Callbacks, SyntheticPlayerBrain, BandEnvironment. **Default off and byte-identical**:
with `MESH_PUPPET_MOTION` unset every puppet path behaves exactly as today (no Ghostify extras, no body, SteerGhost
unchanged, StopPuppetMotion == EndSteering, Unghostify extras null, no Puppets window line). Do not run Unity, regress.py,
the rig or git writes; do not hand-write `.meta` files. Compile-check with
`CSCHECK_OUT=<unique dir> python "C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\2cf88996-d970-455a-bb4e-fffe6bad122c\scratchpad\cscheck.py" --editor`.
Keep files' line endings (Mesh/ and Tests/ files are LF; check with `file`).

## Decisions (made for the user; report them)
1. Pets (summoned puppets) and mounted puppets stay **steered** (refusal reasons `Summoned`, `Mounted`). A kinematic puppet
   that mounts (snapshot `mountEntityId != 0`, `mountPending`, or `PassengingVehicleEntity != null`) ends kinematic motion
   before `SyncGhostMount`; the next snapshot after a dismount begins it again.
2. Kinematic **monster** (NavMesh-kit) bodies keep writing today's `OverrideMoveSpeed` catch-up value while observed (same
   formula as SteerGhost), so clients see monster puppets exactly as today; player (BIEMF3D-kit) bodies never write it.
   Both call `EndSteering` at End as steering does today.
3. Discontinuity := distance from the body's predicted pose to the snapshot > `ghostTeleportDistance` (today's rule).
   Logged and counted with the IDENTICAL "Ghost teleported: ..." text and counters SteerGhost uses. Player bodies never set
   IsTeleport (the client snaps by itself at >= 5 m); monster bodies write `MovementState.IsTeleport` alone (128) with
   `shouldSendReliably = true` for exactly one write timestamp (CE-12 stamp shape: stamp = -1 when set; the first write
   stores its timestamp; writes with that timestamp carry it; a write with another timestamp clears it) and only when
   observed at snap time. The owner's IsTeleport (0x80) and IsJump (0x40) bits are masked out of the snapshot flags.
4. `MESH_LOAD_COLLIDE` is **dropped**: the kit's CollisionIgnore already ignores every character pair. `inert` = no brain,
   movement component left enabled (spawned and standing).
5. The acceptance instrument: a steer-error p95 (server-side distance from the puppet's predicted pose to each arriving
   snapshot, the same `dist` ghostSteer samples) on the Puppets window line.

## P. Pure Mesh/ types (new files) — API other parts code against

### `Mesh/MeshPuppetMotion.cs`
- `public enum MeshPuppetMotion : byte { Steered = 0, Kinematic = 1 }`
- `public static class MeshPuppetMotionSetting`: `public const string EnvironmentKey = "MESH_PUPPET_MOTION";`
  `public static bool TryParse(string text, out MeshPuppetMotion motion)` (trim, ignore case; "steered"/"kinematic" only;
  "", "fast", "1" fail); `public static IReadOnlyList<string> PuppetExtraTypeNames(MeshPuppetMotion motion)` (empty for
  Steered; `{ "PlayerCharacterItemLockAndExpireComponent" }` for Kinematic; static readonly arrays, no allocation).
- `public enum MeshPuppetFallback : byte { None = 0, Mounted, Summoned, Handler, Grid, Install }`.

### `Mesh/MeshKinematicTrack.cs` — `public struct MeshKinematicTrack`
- ctor `(float blendRate = 15f, float maxExtrapolationSeconds = 0.3f, float snapDistance = 4f)`, each <= 0 (or NaN) throws
  ArgumentOutOfRangeException naming it; `public bool IsConfigured` (false for `default`); methods on a default struct
  throw InvalidOperationException naming the struct.
- `public bool TryAccept(Vector3 position, Vector3 velocity, float yawDegrees, bool discontinuity, float receivedAt)` —
  false for NaN/Infinity anywhere or `receivedAt` older than the last accepted.
- `public Vector3 Predict(float now, float leadSeconds)` — `p + v * min(age + lead, maxExtrapolationSeconds)`.
- `public void Advance(float now, float deltaTime, float leadSeconds, ref Vector3 position, ref float yawDegrees)` — target =
  Predict(now, lead); blend `1 - exp(-blendRate * dt)`; snap when the distance exceeds snapDistance or the last accept was a
  discontinuity (consumed); yaw via `Mathf.LerpAngle` then `Mathf.Repeat(y, 360)`.
- `public bool HasSample`, `public Vector3 Velocity`.

### `Mesh/Sync/MeshMovementHandlerSlots.cs` (+ `IMeshReplaceableHandlerLayer` in `Mesh/Sync/IMeshReplaceableHandlerLayer.cs`,
never in MeshSyncContracts.cs)
- `public interface IMeshReplaceableHandlerLayer { void OnReplaced(); }`
- `public sealed class MeshMovementHandlerSlots`: public ctor over `ConcurrentDictionary<uint, IEntityMovementDataHandler>`
  (test seam, throws on null); `public static MeshMovementHandlerSlots For(BaseGameNetworkManager manager)` via a
  `ConditionalWeakTable` with a cached static callback; `public bool TryInstall(uint id, IEntityMovementDataHandler layer,
  bool replaceable, out string error)`; `public bool TryRemove(uint id, IEntityMovementDataHandler layer, out string error)`;
  `public bool TryGetKitHandler(uint id, out IEntityMovementDataHandler kit)`; `public bool HasLayer(uint id)`;
  `public void Forget(uint id)`; `public void Reset()`; `public event Action<uint> LayerRemoved`.
- Semantics exactly as critic.md "How must MeshMovementHandlerSlots interact with the kit": compare-and-swap only
  (`TryUpdate`, `ICollection<KeyValuePair>.Remove`); never write the kit handler back into an entry the kit changed;
  records keep the ORIGINAL kit handler; one Mesh layer per id; installing over a replaceable layer calls `OnReplaced()`
  once; errors name the id; any warn-once set is per instance.

### `Mesh/MeshKinematicCharacterBody.cs` — `public sealed class MeshKinematicCharacterBody : IEntityMovementDataHandler, IMeshStateSource`
- `public MeshKinematicCharacterBody(uint objectId, LiteNetLibIdentity identity, Transform transform,
  IEntityMovementDataHandler kitHandler, bool kitIsNavMesh, MeshKinematicTrack track)` (identity may be null in tests;
  transform and kitHandler must not be null).
- `public static MeshPuppetFallback Refusal(IEntityMovementDataHandler kitHandler, bool mounted, bool summoned, bool gridDisabled)`
  (Handler unless the handler is `BuiltInEntityMovementFunctions3D` or `NavMeshEntityMovement`; Grid when not disabled).
- `public static void WriteKitTransform(NetDataWriter writer, uint flags, byte extra, Vector3 position, float yawDegrees)` —
  exactly the kit's server bytes: `PutPackedUInt(flags)`, `Put(extra)`, `PutVector3(position)`,
  `PutPackedInt(EntityMovementFunctions.GetCompressedAngle(Mathf.Repeat(yaw, 360f)))`, `Put(0)` (empty force-applier list).
- Lifecycle (called by the manager; rig-only behaviour): `public bool Begin(BaseCharacterEntity entity,
  MeshMovementHandlerSlots slots, out string error)` — StopMove, TryInstall (non-replaceable; undo on failure), add the
  body's OWN key object to `MovementDisableState` and `FallDamageDisableState`, `entity.Movement.enabled = false`; and
  `public void End(BaseCharacterEntity entity, MeshMovementHandlerSlots slots)` — idempotent: TryRemove (or Forget if the
  entity is destroyed), remove both keys, `Movement.enabled = true`, `SetLookRotation(yaw, true)`.
- `public void Accept(Vector3 position, Vector3 velocity, float yawDegrees, byte flags, byte extra, bool discontinuity, float now)`
  (masks 0xC0 out of flags; a discontinuity on a NavMesh-kit body arms the teleport stamp when observed),
  `public void Tick(float now, float deltaTime, float leadSeconds)` (observed: Advance and write the transform),
  `public void ApplyNow(float now, float leadSeconds)` (unobserved: set the transform to Predict).
- IEntityMovementDataHandler: `ObjectId`, `ConnectionId => -1`, `CreateMovementData` (current pose), `ReadClientStateAtServer`
  (ignore), `ReadServerStateAtClient` (consume the record exactly: the kit's ServerReadSyncTransformMessage3D + GetList),
  `WriteClientState` (false), `WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)` —
  PURE except the CE-12 stamp rule of decision 3; writes WriteKitTransform from the transform's position and
  eulerAngles.y. IMeshStateSource: `Identity`, `DiscreteVersion` (+1 per discontinuity), `IsAtRest` (|v| < 0.01 and no
  armed stamp), `WriteState` = the same bytes (routes reliably when the stamp is armed).

### Tests (Tests/Editor, new): `MeshPuppetMotionSettingTests`, `MeshPuppetSuppressionTests`, `MeshKinematicTrackTests`,
`MeshMovementHandlerSlotsTests`, `MeshKinematicCharacterBodyTests`, fakes `MeshFakeKitHandler` and `MeshFakeReplaceableLayer`
(implement all 7 IEntityMovementDataHandler members). Rows as in critic.md's plan (refusal table; golden bytes: flags 17,
extra 0, p(1.5,-2.5,3.25), yaw 90 → `11 00 | 00 00 C0 3F 00 00 20 C0 00 00 50 40 | FA 20 BF 02 | 00 00 00 00`; yaw -90 →
`FA 60 3D 08`; half-to-even rounding; the track's failure rows first, convergence < 1 cm in 0.5 s at 60 fps, 359→1 yaw
+2, no drift at v = 0, snap past 4 m and on discontinuity, extrapolation capped at 0.3 s; the slots order test and races;
two writes with one timestamp identical; teleport stamp for NavMesh-kind only, one timestamp; DiscreteVersion only on a
discontinuity; ReadServerStateAtClient ends at EndOfData). Use `new GameObject().transform` and DestroyImmediate in
TearDown; never touch `DefaultGridManagerComponent`.

## G. Manager integration (Mesh/)
### `Mesh/MeshMapNetworkManager_Puppets.cs` (new partial)
- Before the server guard: `[Tooltip(...)] public MeshPuppetMotion puppetMotion = MeshPuppetMotion.Steered;`
- Inside the guard: `PuppetsOnStartServer()` (env override via MeshPuppetMotionSetting.TryParse; a bad value logs a warning
  and keeps the inspector value, like MESH_GHOST_LEAD; logs one line when Kinematic; `_slots = MeshMovementHandlerSlots.For(this)`),
  `PuppetsOnStopServer()` (after ghosts are destroyed: `_slots.Reset()`, clear the list), `EnsurePuppetMotion(MeshGhost, GhostSnapshotMessage)`,
  `StopPuppetMotion(MeshGhost)` (= End if any, then EndSteering), `EndPuppetMotion(MeshGhost, MeshPuppetFallback)`,
  `KinematicPuppetsUpdate()` (one backwards loop over its own `List<MeshGhost>`; drop destroyed entries via Forget; observed =
  `Identity.CountSubscribers() > 0` → Tick; unobserved → nothing), and the Puppets window (counters + a steer-error
  `MeshWorkHistogram(0.05f, 400)` used as 5 cm bins over 20 m) logged next to LogMeshStats only when Kinematic:
  `Puppets window 10.00 s: kinematic N steered N | fallback mounted N summoned N handler N grid N install N | snaps N | observed ticks N/s unobserved poses N/s | steer mean 0.00 m p95 0.00 m max 0.00 m`.
- MeshGhost gains: `public MeshKinematicCharacterBody body;`, `public IReadOnlyList<string> suppressedExtras;`,
  `public MeshPuppetFallback refusal;` (a cached permanent refusal: Handler/Grid).
### `Mesh/MeshMapNetworkManager_Ghosting.cs` (edits at the sites critic.md lists; line numbers there)
- GhostingOnStartServer → PuppetsOnStartServer; GhostingOnStopServer → PuppetsOnStopServer after the destroy loop;
  GhostingUpdate → `KinematicPuppetsUpdate()` right after the link pumps, before the early return; the Puppets line next to
  LogMeshStats.
- Spawn Ghostify and demotion Ghostify pass `extras` = PuppetExtraTypeNames(puppetMotion) only for player kinds with the
  setting Kinematic (null otherwise, so Steered is byte-identical); both record them on the MeshGhost; the static-puppet
  Ghostify is untouched. Unghostify in PromoteGhostInPlace passes `ghost.suppressedExtras`.
- OnGhostSnapshot: before SyncGhostMount, End with `Mounted` when a body exists and the puppet is (becoming) mounted; after
  SyncGhostAffiliation and before SteerGhost, `EnsurePuppetMotion` (only when Kinematic and no body; refuse Mounted/Summoned/
  cached Handler/Grid; Begin starts with EndSteering; seed the track from the transform).
- SteerGhost: keep the sample and counters; for a kinematic ghost compute `current` from `body` prediction; extract the
  teleport note (:1940-1947) into `NoteGhostTeleport(...)` and the HP sync (:1960-1965) into `SyncGhostHp(...)`; the kinematic
  branch notes a snap with the identical text, calls `body.Accept(...)`, `ApplyNow` when unobserved, writes the monster
  OverrideMoveSpeed per decision 2 when observed, syncs HP, and returns — never Teleport/PointClickMovement/StopMove. The
  steered path stays byte-for-byte as today.
- DestroyGhost: StopPuppetMotion before UnlinkSummonPuppets/NetworkDestroy. Reservation (`EndSteering` at :1143 and :1152) and
  promotion (:2526): replace with StopPuppetMotion. Demotion: record extras; Begin is lazy (first snapshot).
- Also feed the steer histogram from the existing steer sample for steered ghosts when Kinematic is on (so the window
  compares both kinds).

## L. Load generator motion (TestMap/Phase0 + ServerMeshLoadGenerator)
- `TestMap/Phase0/ServerMeshLoadMotion.cs`: `public enum ServerMeshLoadMotionKind { Steered, Kinematic, Inert }` and `public
  static class ServerMeshLoadMotion` with env keys `MESH_LOAD_MOTION` (steered|kinematic|inert) and `MESH_LOAD_FIGHT` (0|1,
  default 1) and public static TryParse helpers (bad value → warning, default kept).
- `TestMap/Phase0/ServerMeshKinematicWalk.cs`: one MonoBehaviour loop over the kinematic bots: wait until each bot's kit
  movement has started (MovementState has IsGrounded, with a timeout that logs and leaves the bot steered), then add its own
  MovementDisableState key and `Movement.enabled = false`, move the transform at 0.9 x GetMoveSpeed() within the wander
  area reflecting at the edges (reuse the brain's public static Reflect), keep the heal below one third HP.
- `TestMap/Phase0/ServerMeshServerAnimation.cs`: `MESH_RIG_SERVER_ANIMATION=0|1` sets
  `GameInstance.Singleton.updateAnimationAtServer` once `mesh.IsServer`; logs once; never in the editor.
- `ServerMeshLoadGenerator.cs`: add the brain only for Steered (passing the fight flag to its Setup; fight off skips the
  OverlapSphere entirely); hand Kinematic bots to the walker; Inert bots get no brain. Keep the file under 500 lines.
- Tools: add `MESH_PUPPET_MOTION`, `MESH_RIG_SERVER_ANIMATION`, `MESH_LOAD_MOTION`, `MESH_LOAD_FIGHT` to `regress.ENV_KEYS`,
  to `p0tools.ENV_REGISTRY`, and update `test_p0tools` (the exact-set test); add a `parse_puppets_window` to p0tools with a
  test for the exact line above.
- Tests: `ServerMeshLoadMotionTests` (parsers, failures first).

## Amendments after implementation and review (2026-09-26)
- Accepted from the implementers: Accept returns bool; extra members Pose/LastAcceptSnapped/KitHandler/KitIsNavMesh/
  IsBegun; DestroyGhost calls EndPuppetMotion only when a body exists (StopPuppetMotion would add an OverrideMoveSpeed
  write to every steered destroy); ApplySteerMoveSpeed helper shared by steered and kinematic-monster paths; refusal
  precedence Handler > Grid > Mounted > Summoned, Install also cached; a null identity counts as observed and ApplyNow
  drops an armed teleport; Predict clamps its horizon at 0; inert load bots are healed.
- The track is an ERROR-OFFSET model (review R3-LC-1/W1/W2/W3/L1): Pose(now) = Predict(now, 0) + offset * exp(-15 * age);
  each accepted snapshot folds the shown pose's miss into the offset (no jump, zero steady lag at any frame rate); a miss
  past the snap distance or an accepted discontinuity clears it (LastAcceptSnapped: the ONE snap decision; the manager
  logs "Ghost teleported" and counts snaps from it). No steering lead for kinematic bodies.
- Watched kinematic puppets are posed at the start of every kit tick (PoseKinematicPuppets from OnServerUpdate, before
  BeginTick), so the pose the tick sends is current; unwatched ones are posed at their snapshots (ApplyNow).
- The steer sample for a kinematic puppet is |Pose(now) - snapshot| at arrival (the pose it shows), for a steered one
  |transform - snapshot| as before; the Puppets line keeps them apart:
  `| steer kinematic N mean … p95 … max … | steer steered N mean … p95 … max …`.
- The logged MESH_PUPPET_MOTION value is trimmed and brace-free.
