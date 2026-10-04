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
