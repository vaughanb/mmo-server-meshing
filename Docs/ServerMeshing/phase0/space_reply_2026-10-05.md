# Reply to the space team's questions of 2026-10-05

**Status: decided 2026-10-05.** The user delegated every decision and made this session the space team as well, so the
answers below are agreed on both sides and recorded in the space repo (`Docs/MESHING_ANSWERS.md`, and the status
column of `Docs/LARGE_SYSTEMS_MESHING_CONTRACT.md`). Questions: `space_questions_2026-10-05.md`; proposal texts:
`space_contract_6.1c_2026-09-26.md`; our Part 3: `meshing_reply.txt` lines 2298-3370. Checked against our code at
`3d52e53` and their repo at `b88445a6` (`origin/claude/terminal-glass`).

| | Decision | Taken |
|---|---|---|
| D1 | Who builds BubblePolicy (A.1c) | The space game, as agreed (§L.11); our rig compiles their files unchanged; build sheet corrected |
| D2 | Part 3 revision 2 and the 77 concrete cases (A.1a/b) | By 2026-10-14 |
| D3 | Game-facing handoff API (A.4) | Interface file now; the service in the slice after Phase 0 |
| D4 | Rig parts for the space team (A.6) | Yes, a copy |
| D5 | Pooled results (A.7) | `D:\Game Development\mesh-p0-results\` on machine A |

---

> **To the large-systems plan, from the server-meshing add-on (2026-10-05).**
>
> Thank you; this is clear. The answers below follow your numbering. Two of your premises have moved since the Phase 0
> document you read (26 September). **Kit patch #9 needs no rework** (A.3). **Our handoff has no game-facing signal
> yet** (A.4). Everything else is a yes, a change or a date.
>
> ### A.1 BubblePolicy
>
> **c. Who builds it: you, as agreed (§L.11).** Our Phase 0 §6 was wrong to list "BubblePolicy in the add-on"; it is
> corrected.
> - The add-on's isolated-spaces policy is a different component. It places whole spaces (dungeons, interiors,
>   instances, and your bubbles once they exist) on processes. It never decides merges, splits, roam exits or arrivals.
> - For bubbles, it takes your BubblePolicy's outputs as its input: bubble created or released, member gaps for D_nbr,
>   and D_cut,min.
> - The shipped add-on stays game-neutral and contains none of your files. Our rig compiles your `BubblePolicy` files
>   **unchanged, without a wrapper**, from a commit you name, and runs the case table against them. That is how both
>   projects run one implementation.
>
> **a. Part 3 in full.** The 25 September text is attached as it was sent (`meshing_reply.txt`, Part 3). We will send
> **Part 3 revision 2 by 2026-10-14**. It folds in A0 = 3,000 m, the proposals agreed below, and these corrections:
> - **Δ is used for two things.** It is the closing distance Δ(τ) in §3, and in B7 it is the frame translation that
>   shifts projectiles and claims. Revision 2 calls the translation **Δsim** (new sim = old sim + Δsim, exact on the
>   server).
> - **Missing report definitions.** `VetoHeld` and the straggler report are used but never defined. Revision 2 defines
>   both.
> - **The arrival rule is silent on window drops and glide entries.** Revision 2 adds your proposal 12.
>
> **b. The case table, recomputed.** The table has **77 cases**: C1-C9, F1-F9, N1-N13, T1-T7, M1-M7, S1-S5, R1-R7,
> P1-P5, A1-A6, V1-V5 and D1-D4. In the 25 September text each row is a prose specification, not a fixture.
> Revision 2 gives every case concrete inputs:
> - groups with positions and velocities as doubles, clients, Movable, and in-transit state;
> - links with their kinds;
> - bubbles with key, anchor, roam, class and host;
> - the configuration and prior state.
>
> It also gives the expected decision (the batches and their moves, or the placement) and the exact expected reports.
> It comes as one JSON file plus a C# loader for your `BubbleDecisionTests`, by 2026-10-14.
>
> **The thresholds at A0 = 3,000 m** (T_lead 1.25 s, τ = t + L_max = 2 s, a_max 50 m/s², v_boost 250 m/s):
>
> | Quantity | At A0 = 2,000 m | **At A0 = 3,000 m** |
> |---|---|---|
> | A at reach speed (both at 125 / both at 250 / parked vs 250) | 2,562.5 / 2,625 / 2,437.5 | **3,562.5 / 3,625 / 3,437.5** |
> | E, armed pair | 3,000 (set by the lock) | **3,562.5-3,625 (set by the AOI). Armed and unarmed pairs are now the same** |
> | D_merge (both at 125 / both at 250 / parked vs 250) | 3,700 / 4,000 / 3,600 (armed) | **4,262.5 / 4,625 / 4,225** |
> | D_merge,max | 4,000 | **4,625** (τ = 3 s: 5,125) |
> | D_split = J, drop-join radius | 14,000 | **14,625** |
> | D_nbr / D_cut,min | 69 / 34 km | **69.6 / 34.6 km** |
> | `AoiRangeMismatch` threshold | 2,625 | **3,625** |
> | Top low-cruise speed the policy tolerates in a bubble | 524.9 m/s | **515.8 m/s** |
> | R_out / R_in (R = 30 km), EngagementMax, physics margin (18 s), flap bound (50 s) | unchanged | unchanged |
>
> **Cases whose numbers or verdict change:**
> - C7, C9, T1-T5, T7, N7, N8, M1 and D4.
> - **T5, T7 and N7 would fail as written.**
>   - T5: a pair 2,500 m apart now has to merge.
>   - T7: 3,000 m is no longer a mismatch.
>   - N7: 4,001 m now merges.
> - **A4 (15 km against J) and N5 (20 km) still pass, with less margin.**
>
> **One consequence for you: a Deferred merge (N2) now depends on the targeted re-subscription.** Without it, the pair
> is shown about 500 m inside A0 = 3 km, which breaks your `merge-split` gate's "shown before A0". §L.11 already
> requires a targeted re-subscription for late flips. Please keep it in your P1, and count a deferred merge that flips
> without it as a failure.
>
> ### A.2 Proposals 1-14
>
> | # | Answer |
> |---|---|
> | 1 | **Yes.** Build it on `UniversePosition` / `Double3`. Please also set `noEngineReferences` on the assembly, so the cases run in a plain .NET runner as well as in EditMode. Determinism rules: no `MathF`, no FMA, no parallel loops, and ties broken by id as Part 3 says |
> | 2 | **Yes, with one change to the search.** Where the rule would hold, a `Forced` arrival does not search without limit. For each direction in the fixed order (away from the nearest member, ±X, ±Y, ±Z), it computes the shortest distance that clears D_merge of every member, **including members on other hosts**. It then takes the direction with the shortest distance, with ties going to the earlier direction. That always ends, gives the same answer on every run, and moves the arrival as little as possible. Report `DropIntoConflict` with the distance and a `Forced` flag. The planet band rule is agreed. Please report a forced arrival that creates a dynamic bubble inside the band with its own reason, so your gates can count it |
> | 3 | **Yes.** A = A0, fixed, for supercruise, stowed and limbo groups. A dropped or unstowed group gets its normal A from its first pass in the bubble |
> | 4 | **Yes.** Our ship stand-in already uses mode 3 with this layout (`ServerMeshShipWire.Compact28`). Before we fill the shared golden fixture, please pin three things. (a) The state byte: ours is the teleport sequence's low 7 bits, with a forward flag in bit 7. (b) Angular velocity scaled to the ship's maximum turn rate: ours is a fixed 4 rad/s / 127, and we will follow yours. (c) Rotation: smallest-three, with the largest component's index in bits 30-31 and 3 × 10 bits. Note that our sender **does not check** for a pending teleport before choosing the compact variant. Your `WriteCompactState` must return false while a teleport or stop placement is pending, and the sender then sends mode 0. Reliable and urgent records are always the full variant |
> | 5 | **Change.** There is no byte-budget setting yet. It comes in our sender Phase 1, after Phase 0. When it comes, it will count **per client, on the wire**: LiteNetLib's per-peer bytes sent, which include the kit loop's own sends to that client, plus 28 B of UDP/IP per datagram. It is then set to your budget of 100 kB/s, and neither side estimates the kit's share. Until then, our send meter counts framed EntityState bytes, and the "wire" figure counts LiteNetLib datagrams without UDP/IP |
> | 6 | **Yes, bring it forward. Please write it, and we will review it.** We have no version: it was scoped, never drafted. Conditions: (a) skip a seated passenger's movement write only when none of its one-shot flags is pending; (b) a skipped handler is not `Record`ed, so CE-12's pass leaves its flags alone; (c) the seat snap must not need a first record, because the spawn message carries the position; (d) the same diff goes into both kit copies, and upstream as patch 0008. We carry it as CE-14 |
> | 7 | **Change: count it apart instead of exempting it.** Report AOI ranges raised by a low cruiser as their own kind (`AoiRangeRaisedByLowCruise`), so they are counted and never hidden. **Cost:** while anyone low-cruises at 4 km/s, f raises the range of **every** group in the body bubble to as much as 3,000 + (v + 4,000)·1.25 ≈ 8.3-8.6 km. Please measure the send cost of that in a body bubble before P2c |
> | 8 | **Yes.** Client-less groups have Clients = 0, ActRange 0 and Movable = true, and they move with the batch that takes their fight. One added rule: **a component that holds only client-less groups with no AI is never given a new bubble.** It is reported `RoamBlocked` and left to your despawn rules, so loot never creates bubbles |
> | 9 | **Change.** Wing links only within D_split: yes. Damage: yes, as a new `EngagementKind.Damage` in **either direction** rather than only mutual damage, held for 10 s after the last hit, and bounded by EngagementMax like the other links. A target that flees without firing back is the case that most needs to stay with its attacker |
> | 10 | **Yes** (our B7(a)). Conditions: (a) translate only the epoch the batch has just replaced (current − 1, wrap-safe); (b) do it only until that client's ack + L_max, and reject after that; (c) validate the translated claim in the new frame against the hit-box history shifted by the same Δsim; (d) every other frame change still rejects. The agreed rule stands until your P1 has (a)-(d) |
> | 11 | **Yes** (our B7(c)). Shift the missile by Δsim in the plan frame, together with the movers. Retag its sub-channel at the batch's flip. A single-group frame change and the roam limit still destroy it |
> | 12 | **Yes, and Part 3 revision 2 writes it in.** A window drop at a station, outpost or field, and a glide entry at a planet, are arrivals whose target is the body's bubble. They enter under the persistent-bubble `Eligible` check (inside R_out,b). Body capture (R_in,b) counts as a hard link. If the arrival point is within D_merge of a group in another bubble, the normal merge applies, and the body bubble is home because its body node is in the component. Your validator keeps windows and promotion radii inside R_out,b |
> | 13 | **Yes.** Low cruisers and groups with a pending promotion are in transit: frozen, never in a batch, never roam-exited. Your separate `RoamBlocked` / `RoamOverrun` counts are agreed. A promotion still pending after the overrun bound you give, min(m/2, v·(RTT + 1 s)), counts as an ordinary `RoamOverrun`, and the game must promote or force-drop the group |
> | 14 | **Change: a batch id instead of the list.** Mode 2 carries a **2-byte batch id** (wrap-safe). After its own apply, a mover's client keeps every entity it has seen with a mode 2 record carrying its own batch id, and quarantines the rest. That is 2 B per mover record against about 3 B per group of the batch: at 64 + 64, about 0.4 kB instead of about 25 kB per build per client on the kit loop. Once our sender is in, it hides outsiders from movers on the server (Withhold, both ways), so the id is only a client-side safety net. If you see a case the id misses, tell us. The one we know of is a co-mover all of whose mode 2 records are lost before it acks. On the kit loop it would stay quarantined until its next frame change |
>
> ### A.3 Kit patch #9: keep the post-pass. No rework
>
> The §0.9 you read is out of date. **We accepted the post-pass on 2026-09-30.** It is merged upstream (open-mmorpg/OpenMMORPG `develop`, PR #3, `bb9895f`, patch 0006), together with #10 as 0007, and our kit carries both as CE-12/CE-13 after a full rig acceptance (2026-10-02). Our addenda of 09-30, 10-01 and 10-02 to the kit-patch reply say so. We attach them in case they did not reach you.
> - **Why the refusal no longer applies:** our adapter's kit-facing `WriteServerState` returns false. So the kit loop never `Record`s or consumes a handler our sender wraps. Our sender runs **its own** `EntityMovementServerStateFlagsPass`: `Begin` at the start of its build, `Record` the inner kit handler after its single write, then `ConsumeAll` after the fan-out (`Mesh/Sync/MeshStateFanout.cs`). The two consumed sets never overlap.
> - **a.** There is no stamp version, and none is needed.
> - **b.** Keep `IEntityMovementServerStateFlags` and `ConsumeServerStateFlags`. They are live code in both copies and upstream.
> - **c.** Your `_sendingDash` clear in the consume is enough.
> - **d.** Nothing is needed from you, before G1 or after it.
>
> Our build sheet's §0.9 is corrected.
>
> ### A.4 The handoff acknowledgement
>
> **What exists today:**
> - The destination process sends `HandoffAck` to the origin, server to server, reliable-ordered:
>   `{characterId, ok, reason, arrivalMs, promoted}`.
> - It is sent once the arriving character is registered on the destination.
> - It is internal: no event, callback or virtual hook is exposed, and log lines are the only outputs.
> - A handoff is started only by our boundary check, for players. **No API lets game code hand over a chosen group**
>   (our DESIGN §11, trigger 2, "planned, not built").
>
> So the signal your drop waits on does not exist yet. We propose this shape. Your `HandoffGate` stub can raise exactly
> these events now, and we will send the interface file with a `.meta` GUID, as we did for the sync contract:
>
> ```csharp
> namespace MultiplayerARPG.MMO.Mesh
> {
>     public enum MeshHandoffOutcome : byte { Committed, Failed, RolledBack, Aborted }
>     public readonly struct MeshHandoffResult   // raised on the ORIGIN process
>     {
>         public readonly ulong TicketId; public readonly string EntityId; public readonly string DestinationId;
>         public readonly MeshHandoffOutcome Outcome; public readonly string Reason; public readonly int ElapsedMs;
>     }
>     public readonly struct MeshArrivalInfo     // raised on the DESTINATION process
>     {
>         public readonly ulong TicketId; public readonly string EntityId; public readonly string OriginId; public readonly int ElapsedMs;
>     }
>     public interface IMeshHandoffService
>     {
>         ulong RequestHandoff(string entityId, string destinationId);   // origin; returns a ticket; 0 = refused
>         event System.Action<MeshHandoffResult> HandoffCompleted;        // origin: once per ticket (+1 Committed after a late ok-ack)
>         event System.Action<MeshArrivalInfo> ArrivalCommitted;          // destination: entity registered, client ready
>     }
> }
> ```
>
> **a.** Your drop readiness waits on **`ArrivalCommitted` on the destination process**. That is the process you drop
> on, and the event is raised in the same frame as the ok-ack to the origin. The origin gets `HandoffCompleted`.
>
> **b. Failure semantics today:**
> - **Save fails:** the handoff is aborted (`Aborted`) and the entity is re-registered on the origin. Our boundary
>   check retries after 2 s.
> - **No ack within 20 s, or the link to the destination down for over 3 s:** the handoff rolls back (`RolledBack`).
>   The origin's copy is held for 20 s so the client can return. A late ok-ack after a rollback still commits.
> - **The destination fails** (the reservation goes unclaimed for 15 s, or the client drops before it is ready): the
>   origin gets `Failed` and releases its copy.
> - **What we recommend for your drop:** on anything but `Committed`, abort to supercruise on the origin and retry
>   through your admission queue (`QUEUED`). Never complete the drop on the source process. The 20 s guard is ours to
>   shorten for ship handoffs if your QUEUED hold needs it; tell us your number.
>
> **c. Yes.** Raise exactly these two events with these payloads. We build the real service in the slice after our
> Phase 0, well before your P2b needs a real handoff.
>
> ### A.5 The sync contract
>
> **a. Yes: `ShipMovementComponent : IMeshStateSource`.** Add `IMeshCompactStateSource` for mode 3 (proposal 4). The
> file is `Mesh/Sync/MeshSyncContracts.cs`, namespace `MultiplayerARPG.MMO.Mesh.Sync`, and it uses only `LiteNetLib.Utils`
> and `LiteNetLibManager`.
> - **`IMeshStateSource`:**
>   - `uint ObjectId { get; }`
>   - `LiteNetLibIdentity Identity { get; }`
>   - `uint DiscreteVersion { get; }`
>   - `bool IsAtRest { get; }`
>   - `bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)`
> - **`IMeshCompactStateSource : IMeshStateSource`:** `bool WriteCompactState(long sampleTimestamp, NetDataWriter writer)`.
>   It is never sent reliably.
> - **`IMeshSenderOwnedSource : IMeshStateSource`:** `void OnSenderReleased()`. This hook was added in our step 7. It is
>   optional and additive: implement it if you want to know when our sender lets go of the ship.
> - **`IMeshStateRegistry`:**
>   - `bool Register(IMeshStateSource)`
>   - `void Unregister(IMeshStateSource)`
>   - `void MarkRelevanceChanged(uint objectId, long observerConnectionId)`
>   - `void MarkSourceChanged(uint objectId)`
>   - `void MarkObserverChanged(long observerConnectionId)`
> - **Your `IShipSendPolicy` is our `IMeshSendPolicy`:** `MeshSendRate Classify(IMeshStateSource source, in MeshObserverContext observer)`.
>   - `MeshSendRate` has `Hz`, `Priority`, `Variant` (`Full` / `Compact`) and `static Withhold`.
>   - `MeshObserverContext` has `ObserverConnectionId`, `Distance` (NaN when unknown, which counts as near) and `IsOwner`.
>   - Withhold on an owner pair counts as Hz 0, which still delivers urgent and reliable records.
>
> **b. Unchanged:** `bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)`.
>
> **c. The normative `IsAtRest` combines both definitions.** It is true only when all three hold:
> - |v| < 0.01 m/s, **and**
> - |ω| < 0.001 rad/s, **and**
> - nothing is pending: no unacknowledged teleport, and (from your P1) no mode 2 frame change awaiting its ack. A
>   flight-mode change needs no term: it bumps `DiscreteVersion`, which already forces three full sends.
>
> The reason: our sender has to decide whether to write a source **before** writing it, and `sendReliably` only exists
> after the write. Today our sender does not read `IsAtRest`, so nothing breaks either way. Our sender Phase 1 at-rest
> keepalive will lower the rate of at-rest sources, and a pending teleport must never be slowed. We take your ω term,
> and your `ShipMovementComponent.IsAtRest` now returns false while `TeleportPending` (done, with a check in
> `ShipWireTests`). Our change is to the comment only.
>
> **d.** The file is attached at `3d52e53`, with its `.meta` GUID **`86363639be9247f43a2b0919b12dd03f`** (keep that
> GUID). It is frozen except for doc comments. Any member change will reach you before it lands.
>
> ### A.6 The bot harness for S9
>
> **a. Not against your map server by 2026-12-14. Please build your own (plan §N).**
> - **Our harness is tied to our map server.** It needs our `MeshMapNetworkManager` and our cells, and spawns our ship
>   stand-in. Its phantom clients are in-process (`virtual`) or real LiteNetLib peers on a second machine (`sink`).
> - **It can't measure your L_max.** Its ship phantoms echo the teleport sequence only, with epoch and flight mode
>   always 0. They don't apply or acknowledge mode 2.
> - **The part that would bring real bot clients is not started.** Our stage 0b's real kit bots on a second machine
>   are step 8b.
>
> **b. Yes, build it on a copy of our harness.** You get:
> - the phantom transport, clients, uploads and sink;
> - the ship stand-ins;
> - the `mesh-p0/1` writers.
>
> Your work would be a ship payload that applies and acknowledges mode 2, with a latency distribution you can set, and
> the binding to your map server instead of our cell.
>
> ### A.7 Pooled results
>
> Our `Logs/` is not tracked by git. Please write your rows to **`D:\Game Development\mesh-p0-results\`** on machine A,
> outside both repos, as `space_space_<family>_<stamp>.jsonl` in `mesh-p0/1`, with `team: space` and `game: space`.
> Fill in `host`, `cpu` and `power_plan`: we never pool rows from two machines, and machine A's numbers are measured on
> the High performance plan. Our tools will read that folder. Keep a copy in your repo if you like.
>
> ### B. Your answers: received
>
> 1. CE-12: settled above (A.3).
> 2. The header layout: agreed, with mode 3 compact (proposal 4).
> 3. Reliable forever after a server teleport: thank you. Our stand-ins will model `AcknowledgeTeleport` once
>    `bcbe9754` is in your repo; it is not in the copy we read (`c2eb4154`).
> 4. Contract path, namespace, renames and NaN as near: agreed (A.5).
> 5. The four superlinear or allocating costs: we'll wait for your written answers.
> 6. Your rows: S8 first, in the shared folder (A.7).
> 7. Fight target, lock visibility and low cruise: received. At A0 = 3 km, the AOI now sets E rather than the lock (A.1b).
> 8. Patch #10: matches ours, CE-13.
> 9. `NavMeshEntityMovement` being `partial`: noted. Ours is not, which is why we apply #9's class line by hand.

---

## Follow-ups (not part of the message)

- Done: build sheet §0.9, summary item 3, the use-case row, §6 (isolated-spaces policy) and §7 questions 6 and space 1.
- Done in the space repo (branch `large-systems/meshing-answers`): `IsAtRest` pending term and its test; the answers
  recorded in their docs.
- Done 2026-10-06, after the chains: the `IsAtRest` doc comment in `MeshSyncContracts.cs`, and the contract file
  `Mesh/MeshHandoffContracts.cs` (GUID `f80cc0ff27e648a4be17cb6cddafa578`; DESIGN §11 trigger 2 and the name
  registry). Both compile.
- Done 2026-10-06 in the space repo (`main`): `IsAtRest` and its test (Space Core 1,309 pass, 0 fail).
- Part 3 revision 2 and the 77-case JSON fixture with its C# loader, by 2026-10-14 (D2).
- Create `D:\Game Development\mesh-p0-results\` and give `p0load.py` a rows-directory option (D5).
