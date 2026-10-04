# Addendum 3 (2026-10-02): status, and one documentation change to the sender contract
- **0006/0007 are merged upstream** in open-mmorpg/OpenMMORPG `develop` (PR #3, bb9895f). They are the same code as
  your #9/#10, with neutral comments. Our kit copy carries them as CE-12/CE-13 and passed our full rig acceptance with
  them.
- **The add-on now needs a kit that carries 0006.** Our state sender (Phase 0 step 6, being built) calls
  `IEntityMovementServerStateFlags` directly. It keeps its own `EntityMovementServerStateFlagsPass`: Begin, then
  Record the inner kit handler after its one write per build, then ConsumeAll after the fan-out.
- **One documentation-only change in `Mesh/Sync/MeshSyncContracts.cs`.** The comment on `Withhold` will say that an
  owner pair is never withheld: the sender treats Withhold on the owner's own pair as Hz 0, which still delivers
  urgent and reliable records. Otherwise an owner could miss its own IsTeleport and never confirm it. No member,
  signature or value changes, so your code is unaffected. We mention it because you compile that file.

---

# Addendum 2 (2026-10-01): the teleport handshake has no sequence (your KIT_AUDIT B13)
- **Agreed, and we withdraw our re-send suggestion.** Without an echoed sequence a duplicate confirm can release a
  later wait. We are not proposing the sequence (a wire change both ways), nor a timeout, for now. If either becomes
  necessary we will tell you first. A timeout would be at least 5 s and logged.
- **Our add-on never waits on `WaitClientTeleportConfirm`.** It never teleports a client-owned character either:
  - an arriving character is teleported while it is still server-owned, and CE-9 clears any wait at the owner change;
  - puppet and monster-puppet snaps are server-owned;
  - a cell crossing is a warp to another server, which waits only on the database save.

  Our only exposure is the kit's own same-map warp and respawn, the same as any kit game. Our rig bots do use the
  kit's respawn.
- **We changed one thing on our side.** Our phantom-client feeder (Phase 0 step 5, not built yet) had planned to
  confirm on every upload while a wait was pending. It now confirms each teleport once, as a real client does.

---

# Addendum (2026-09-30): the diffs are accepted as delivered
We reviewed `kit-patch-09-teleport-flags` and `kit-patch-10-read-path` against our kit copy and the conditions below.

#9 meets every condition:
- `Record` runs only after `WriteServerState` returned true.
- `ConsumeAll` consumes only recorded handlers. Handlers without the interface are skipped. The pass is public, one per manager, and dedupes a handler written to many players.
- The consume clears exactly jump, dash (NavMesh included), teleport and still-move, and leaves the confirm wait alone.
- The reset sits in `OnTeleport`, so it covers the server and the client. Our `SetPosition` sets the transform to exactly `position`, so restarting the interpolation there is correct. It does not touch the accepted timestamp.

#10 meets every condition:
- The guard is checked before the handler lookup, so the unknown-entity branch is covered too.
- It uses the exact comparison, accepts 0 and an exact end, and warns and returns without throwing.
- The re-seek runs in `finally`, and `ReadServerEntityState` is public static.

Both apply to our copy. The one exception is #9's NavMesh class line, because ours is not `partial`; we apply that line by hand. We carry the code unchanged as CE-12 and CE-13, with our own markers, after our current measurement chain.

Optional follow-ups, none of them blocking:
- **Log read exceptions.** In `ReadServerEntityState`'s catch, log the read exception once, with the object id and handler type. A read error is now only a warning without details. We are adding the two new warning texts to our rig's log checks either way.
- **The same bug in three more movers.** `RigidBodyEntityMovement2D` and the two Simple movers keep the first-receiver bug, and so does the owner-hang case (a timeout, or re-sending to the owner while the server waits). Neither project uses those movers.

---

# Reply to the space team: kit patches #9 and #10 (2026-09-29)

We checked every claim in your note against our kit copy, which matches yours at 1a3a58d9 on these paths. We have not seen the diffs, so each answer below comes with what we need to confirm in them. On our side the patches will be marked CE-12 (#9, including the teleport reset and the dash clear) and CE-13 (#10, including the length guard). They land after our current rig measurement chains finish.

## 1. Teleport smoothing reset: mirror it, in both copies
Your analysis holds in our copy.
- **On a remote copy:** `OnTeleport` moves the entity but leaves `_startInterpPosition`, `_endInterpPosition` and `_interpElapsedTime` alone. The next `UpdateInterpolate` therefore draws it back to the last pre-teleport sample.
  - A move under `snapThreshold` (5 m) then slides.
  - A longer move snaps back and forth.
- **On the server's copy of a NotSecure, client-owned character:** the confirm is validated against that stale `_endInterpPosition`.
  - In a same-map respawn the character is still dead, so `!CanMove` clamps x/z to the death spot.
  - Every observer is then sent the death-spot and in-between points.

Conditions:
- Put it in `BuiltInEntityMovementFunctions3D.OnTeleport` after `SetPosition`, so it covers both the server `Teleport` and the client apply: `_startInterpPosition = _endInterpPosition = EntityTransform.position; _interpElapsedTime = 0;`. That is the same reset the class's own snap branch already does.
- Leave `_acceptedPositionTimestamp`, the jump and dash accept flags, and the gating of the client teleport branch untouched. Advancing the timestamp in that branch would close a late-record hole, but it should be a separate change.
- It lands no later than the flag change. With the flags reaching every receiver, every observer would draw back without it.
- Only BIEMF3D needs it:
  - NavMesh and RigidBody2D already reset their accepted position on a server teleport, and have no interpolation buffer.
  - The Simple movers use LiteNetLibTransform.
  - Mounts use BIEMF3D, so they are covered too.
- Please send the B10 diff and your teleport-observers test, so we can run the same assertion in our rig.

## 2. Length guard: mirror it, in both copies
We asked for the re-seek, but on its own it hands all the trust to the length field. The guard is what makes it safe, so your addition completes our own recommendation. Conditions:
- **Placement:** check right after `int dataLength = reader.GetInt();` and before the handler lookup, so it also covers the unknown-entity `SkipBytes(dataLength)`. Garbage headers usually decode to unknown ids, so that is the likeliest path for a bad length. Your note says the unknown-entity branch is byte-identical; does the guard cover it?
- **Comparison:** `dataLength < 0 || dataLength > reader.AvailableBytes`, read after the length field. Zero and a length exactly equal to the bytes remaining must pass, since the last record always ends at the packet end. No `start + len` arithmetic, because it overflows near `int.MaxValue`.
- **When it trips:** warn with the object id, length, bytes remaining and record index out of the count, gated by LogWarn. Return without throwing, without calling that record's handler and without the `finally` re-seek.
- **Logging:** the catch in `ReadServerEntityState` should log the exception itself (type, stack, object id, handler type), not only a warning. Today a reader's exception escapes through `async void` and is logged with a stack. Once the readers are synchronous, a warning-only catch would swallow it.
- **Optional:**
  - a clean stop when the data runs out before the record count does (warn and return when `AvailableBytes == 0` before a header). Otherwise a garbage count still ends in TransportHandler's error plus stack trace;
  - put the whole per-packet loop in a public static helper, so both teams' EditMode tests exercise the real code rather than a copy.
- **Tests:**
  - lengths of -1 and -(idBytes+4) (the self-loop);
  - a length of `AvailableBytes`+1 and of `int.MaxValue`;
  - an unknown id with a negative length;
  - a garbage count that still terminates;
  - a length of 0, and a last record ending exactly at the end;
  - a count of 0;
  - records before a bad one still applied.

## 3. NavMesh dash clear: yes, now
- The change is one line, `_sendingDash = false;`, in `NavMeshEntityMovement.ConsumeServerStateFlags()` next to `_isTeleporting` and `_stillMoveAfterTeleport`.
- `WriteServerState` and `CreateMovementData` keep reading the flag. The client-side clears in `WriteClientState` stay as they are.

Why now:
- **Nothing ever clears it on a server.** The only clears are behind `IsOwnerClient && !IsServer`, and `WriteClientState` never runs on a server.
- **The cost once set:** after one dash, every record of that entity to every observer carries IsDash and goes ReliableOrdered on every tick for the entity's life. Every remote copy then re-dashes on each record: roughly a 0.25 s sawtooth at BaseEnemy-CE's values. On our sender the entity would also stay urgent for good.
- **It changes nothing today in either project.** Only BaseEnemy-CE uses NavMeshEntityMovement, and nothing gives it a dash. So it's a free fix for a latent trap.
- **No client needs the repeat.** The owner ignores server IsDash, and BIEMF3D and RigidBody2D are already one-shot.
- Please send the exact consume body. The clear can also be covered by an EditMode test on an uninitialised NMEM.

## 4. Our send loop: it fits
We withdraw our earlier refusal of a "post-pass" for this shape.
- **What we were guarding against:** a pass that consumes handlers it did not write, clearing flags our sender has not sent yet. Yours records only handlers whose write succeeded.
- **Why the two never overlap:** our kit adapter's kit-facing `WriteServerState` returns false, so the kit loop never records or consumes a handler we wrap. Our sender then does its own pass: `Begin` at the start of its build, `Record` the inner kit handler after its single write, and `ConsumeAll` after the fan-out. The kit's consumed set and ours never overlap.
- **Our loop already meets the "never skip a pending receiver" rule:**
  - We write each source once per build and send the same bytes to every receiver.
  - Every kit one-shot sets `shouldSendReliably`, and we send that record reliably to every subscriber that build, whatever its rate tier.
- **Changes on our side** (none are in our code yet):
  - never withhold an owner pair;
  - never defer a reliable record, and always flush deferred halves before the next build;
  - send a reliable record too big for our 1,020-byte packet in its own reliable packet, as the kit does;
  - write every subscribed source on every build;
  - take receivers from `identity.GetSubscribers()` at build time;
  - skip the consume for a build in which a reliable send failed;
  - skip a new pair only if this tick's AOI pass created it, and never skip an owner pair;
  - a measuring mode that never records or consumes;
  - our kinematic puppet body returns false to the kit loop while the sender owns it, so there is one writer. It keeps its own per-handler stamp, which already meets the rule;
  - our wrapper never implements `IEntityMovementServerStateFlags`.

Please confirm from the diff:
- `Record` is called only after `WriteServerState` returned true (and in the job path, only for records actually written).
- `ConsumeAll` consumes only recorded handlers, never every entry in `EntityMovementDataHandlers`.
- A handler that does not implement the interface (our wrapper, our kinematic body, your ship) is skipped silently.
- Both types are public. Is the pass shared/static or one per send loop, and does `Record` dedupe a handler written for N players?
- Exactly which fields `ConsumeServerStateFlags` clears. It must leave `_isServerWaitingTeleportConfirm` and the force appliers alone. Do two `WriteServerState` calls in one tick write identical bytes?
- Would you add a read-only `bool HasServerStateFlags { get; }`? It would let us mark urgency before writing.
- Do you plan to send #9 upstream?

## Three more things we found
- **The owner hang can't be fully fixed on the send side.** A `Teleport` that sets the confirm wait after an ownership change, but before the owner is subscribed (up to one AOI pass), loses the flag on any kit and any sender. The server then drops that character's input while it waits. The real fix is in the kit: keep sending IsTeleport to the owner while `_isServerWaitingTeleportConfirm` is set, or give `WaitClientTeleportConfirm` a timeout. Do you want to add one to #9?
- **The same first-receiver bug is in three more movers.** Your note names BIEMF3D and NavMesh only.
  - `RigidBodyEntityMovement2D.WriteServerState` clears its flags on every call, and its `CreateMovementData` clears none.
  - `BuiltInSimpleEntityMovementFunctions3D` and `SimpleNavMeshEntityMovement` flip Requesting to WaitingForResponse on the first write, so only the first receiver gets the teleport and a non-first owner hangs.

  Does #9 cover them? Neither project uses them today.
- **Monsters are teleported before they're spawned.** `MonsterSpawnArea` calls `Teleport` before `NetworkSpawn`, so every monster spawn starts with a pending IsTeleport. That's harmless (the spawn carries the position), but it's worth knowing when reading flag counts.
