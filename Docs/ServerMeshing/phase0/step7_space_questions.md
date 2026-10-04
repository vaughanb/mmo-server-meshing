# To the space team: ship stand-in questions (Phase 0 step 7, 2026-10-04)

Our rig now has ship stand-ins modelled on your ShipMovementComponent, ShipEngine, ShipAIPilot and ShipCombatComponent
(your commit 1a3a58d9). Flight model, AI timers, combat and the 54 B record are copied as you have them. A few things
are not in your code yet, so the rig picked a value; please confirm or correct.

1. **Compact size.** Our note of your earlier reply says "~26 B"; the rig uses a 28 B layout (24-bit centimetre
   positions, smallest-three rotation in 4 B, int16 velocities, indicator in byte 0 bit 7). Which fields and
   quantisation do you plan, and where does the compact indicator bit go?
2. **Header bit order.** The rig's 2-byte header: authority bits 0-1, flightState bits 2-4, bits 5-7 free; epoch in
   byte 1. Confirm, and say what the flightState values are (CruiseMode 0-3?).
3. **Zero velocities for player ships.** The server's mirror of a player-owned ship is moved through its transform, so
   its records seem to carry zero velocity and angular velocity (SMC:338-341); observers then cannot extrapolate.
   Intended? The rig copies it by default and can send real velocities (`MESH_SHIP_MIRROR_VELOCITY=real`).
4. **Reliable forever after a teleport.** SMC:390 sends an unowned ship reliably for as long as its teleport is
   unacknowledged, which for a server-owned ship is forever. The rig acknowledges server-owned teleports after two
   logic ticks. Will you fix it the same way?
5. **Uploads.** Always the full record (54/56 B), never compact? Does the epoch ride in uploads?
6. **Server-side AI bolts** are instantiated per barrel and run an allocating `RaycastAll` every frame. Keeping that,
   or moving to pooled bolts or no server visuals? (On our rig at 100 AI ships the two cost about the same CPU.)
7. **Which hostile AI prefab** should we model: `Ship_AI` (hull 2,500, force 400) or `Ship_Enemy` (hull 1,500, force
   300, about 94 m/s)? The rig models the first.

## Answers read from the space code (2026-10-04, `mmorpg-kit-space` c2eb4154, 57 commits after 1a3a58d9)

No space team session was available, so these come from their code and their large-systems plan (revision 6.1c),
read-only. Each says what changes in the stand-ins.

1. **S1 compact: wire mode 3, 28 B with the header** (plan §C.3/§L proposal 4, P2c; not in code yet). The indicator is
   the header's 2-bit mode field = 3, not bit 7. After the header: position 3 × float32 relative to the frame's slot
   origin; smallest-three rotation in 32 bits (2 + 3 × 10); velocity 3 × half-float; angular velocity 3 × int8 scaled to
   the ship's maximum turn rate; one byte of teleport-sequence low bits and movement-state bits (split to be pinned by
   the shared golden). A record with a pending teleport or stop placement, or sent reliably, is written as mode 0. It
   waits for our agreement on mode 3. **Stand-in:** compact28 keeps its size (28) but takes this layout.
2. **S2 header: confirmed in code, but bits 0-1 are the wire MODE, not authority** (`ShipWire.PackHeader`,
   ShipMovementComponent.cs:721-723): Bubble 0, Supercruise 1, FrameChange 2 (3 = compact, above). FlightMode bits 2-4:
   Combat 0, Spooling 1, LowCruise 2, Braking 3, Landed 4 (append-only). Bits 5-7 written 0, ignored on read. Byte 1
   epoch, wrap-safe compare. **Stand-in:** header56 writes mode 0 in bits 0-1 (authority leaves the wire); the golden's
   `09 07` becomes `08 07`.
3. **S3 velocities: no longer zero** (9b1d69a1). The server's copy of a piloted ship writes the owner-reported
   velocities (`CurrentVelocity`, :94-96, :465-466). **Stand-in:** `MESH_SHIP_MIRROR_VELOCITY` defaults to `real`.
4. **S4 teleport: unchanged, and it does hit their AI.** `WriteState` sends reliably while `TeleportPending` (:445) and
   only an owner's upload acks (:525), so an unowned ship stays reliable for good after its first server teleport.
   `Ship_Enemy` (the standing "Pirate Interceptor" at `ShipSpawnPoint`s) has `ShipAIPilot` and inherits
   `respawnOnDeath: 1` from Ship.prefab, and `Respawn` teleports home (ShipCombatComponent.cs:575, :604). Step 7's
   decision 21 assumed their AI never teleports; that was true only of `Ship_AI` (waves, despawn). Still for the team:
   will they ack server-owned teleports?
5. **S5 uploads: always mode 0 (56 B), epoch included** (`WriteClientState` → `WriteState`, :501-509); the server drops
   an upload whose epoch differs from its own (:530). Matches the stand-in.
6. **S6 bolts: unchanged** (one `Instantiate` per barrel, :400; `RaycastAll` sweep, ShipProjectileVisual.cs:68; damage
   stays on the server). The stand-in's `instantiate` default matches. Whether they keep it is still for the team.
7. **S7 prefab: both are live.** `Ship_Enemy` (hull 1,500, forces 300/600, torques 8/8/10, respawns by teleport) is what
   the systems place at spawn points; `Ship_AI` (hull 2,500, force 400, no respawn) is what `ShipWaveSpawner` spawns.
   The stand-in models `Ship_AI`'s numbers with `Ship_Enemy`'s respawn. **Open (new):** Ship_Enemy sets the
   rigidbody's `m_AngularDamping` to 3, but `ShipEngine.Awake` (`Scripts/Ships/ShipEngine.cs:88`) overwrites it with
   the engine's `angularDrag` (8, not overridden), so the interceptor actually turns at drag 8 (steady yaw 0.84 against
   2.51 rad/s at 3). Intended? The stand-in models what runs: drag 8 (step7b_design.md decision 4).

**User decisions (2026-10-04)** for the stand-ins:
- Teleports: default = theirs (an unowned ship's records stay reliable after a server teleport); the two-tick self-ack
  becomes an arm, reported to the team as a likely bug with its measured saving.
- AI ships: default = `Ship_Enemy`'s numbers and respawn; `Ship_AI`'s are an arm.
- Compact: we agree to wire mode 3 (their proposal 4); compact28 takes their layout. The golden stays `[Ignore]`d until
  they pin the state byte's split.
- Also folded in (no decision needed): header56 bits 0-1 = mode 0; mirror velocities default `real`.

Both findings below are still open in their tree (`_lastGetIdentityFrame = 0`; the aim formula at :395-399).

Two findings you may want:

- **Kit bug (also in your copy, LiteNetLibBehaviour.cs).** The `Identity` getter's per-frame cache starts at
  `_lastGetIdentityFrame = 0`, and `Time.frameCount` is 0 on the first frame of Play, so the first read on that frame
  returns null without looking the identity up. Anything that reads `Identity` on frame 0 (for example
  `GameInstance.AddVehicleEntities` from a `RuntimeInitializeOnLoadMethod`) throws a NullReferenceException. A
  one-line fix is to start the field at -1. We worked around it on our side and can send it upstream if you agree.
- **Aim compensation is approximate (harmless).** ShipCombatComponent.cs:395-396 normalises
  `desiredDir * projectileSpeed - lateral`, which leaves a residual sideways velocity of
  `lateral * (1 - s / sqrt(s^2 + lateral^2))`: about 0.135 m/s on an 800 m/s bolt from a ship moving 56 m/s sideways,
  0.01 degrees. Exact compensation would be `desiredDir * sqrt(s^2 - lateral^2) - lateral`. Our stand-in copies your
  version.
