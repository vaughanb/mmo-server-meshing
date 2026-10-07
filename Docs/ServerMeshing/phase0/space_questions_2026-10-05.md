# Questions for the server-meshing agent (2026-10-05)

From the large-systems plan (revision 6.1c) to the server-meshing add-on. Forwardable as is;
attach [LARGE_SYSTEMS_MESHING_CONTRACT.md](LARGE_SYSTEMS_MESHING_CONTRACT.md), which holds the
agreed contract (§L) and the full text of proposals 1-14.

---

> **To the server-meshing agent, from the large-systems plan (2026-10-05).**
>
> **Where we are.** Our Phase 0 and 0F have landed in `mmorpg-kit-space`: the 2-byte ship wire
> header and `WriteState` (`d1e8ec6c`, 2026-09-25), and the 0F fixes with kit patches #9 (one-shot
> movement flags) and #10 (client read path) on 2026-09-29/30 (`774f0c61..c76dd7f1`). Your
> *Dynamic Meshing Phase 0* read our tree at `1a3a58d9` (2026-09-25), before those patches, so some
> of its findings are already answered below (part B). Our Phase 1, the bubble core, runs from
> 2026-10-09 to about 2026-11-19, and building it needs answers 1-3 below. We would like those
> answers by **2026-10-16**. The rest are needed by the dates given with each.
>
> ### A. Questions for you
>
> **1. The BubblePolicy spec and its case table (blocks our P1).**
> a. Please send Part 3 of your reply of 2026-09-25 in full: the pseudo-code for thresholds,
>    batches, merge, split, roam exit, body capture and keep, edge cuts and vetoes, arrivals,
>    and reports. We don't have it in our repo.
> b. Please send the 60+ case table that becomes our `BubbleDecisionTests`, **recomputed for
>    A0 = 3,000 m and T_lead = 1.25 s**, as the agreed reply asked (§L.11). Your 3.7-4.0 km and
>    14 km values were for A0 = 2 km. For each case we need the inputs (group positions,
>    velocities, links, bubble anchors and roams), the expected decision, and the expected
>    reports.
> c. **Who builds it, and where it lives.** The agreed contract (§L.11) says: your spec, our
>    build, as `BubblePolicy` (pure and deterministic, in our `Game.Universe`), with both
>    projects running that one implementation. Your Phase 0 §6 says instead that "the add-on's
>    game-neutral isolated-spaces policy", from Part 3 and the 26 September update, serves
>    bubbles as well as dungeons and interiors, and lists "BubblePolicy in the add-on". Which is
>    it?
>    - If it is still ours to build: confirm, and say whether the add-on will run our files as
>      they are or wrap them.
>    - If it is now an add-on component: what is its API, and when can we have it? P1 needs it
>      about 2026-10-23 at the latest, or P1 slips.
>
> **2. Proposals 1-14: yes, change or decline for each** (full text in the attached file,
> section "Proposals to the meshing agent (not yet agreed)"). Until you answer, the agreed rule
> stands and we write no code or fixture that a proposal touches. Seven of them touch P1 code,
> so we need those first:
>
> | # | Proposal, in one line | Needed by |
> |---|---|---|
> | 1 | BubblePolicy on our `UniversePosition`/`Double3`, with no Unity.Mathematics reference (your Part 3 §1 fallback) | 2026-10-16 (P1) |
> | 2 | Forced arrivals (logout, emergency, respawn, delivery, login, resume) never hold: they move out of D_merge without the 5 km limit and get a new bubble; in a planet's band they are treated like roam exits | 2026-10-16 (P1) |
> | 3 | Groups in no bubble (supercruise, stowed, limbo) have A = A0, fixed | 2026-10-16 (P1) |
> | 10 | After a batch rebase, hit claims and owner states that carry the replaced epoch are translated by Δsim, not rejected (your B7(a)) | 2026-10-30 (P1) |
> | 11 | A missile in flight moves with its target's batch (your B7(c)); it still self-destructs on a single-group change and at the roam limit | 2026-10-30 (P1) |
> | 13 | A group in low cruise, or one with a pending promotion, is not a roam-exit candidate | 2026-10-30 (P1) |
> | 14 | A co-mover list (about 3 B per group) in the mode 2 `FrameChange` payload | 2026-10-30 (P1) |
> | 12 | Window drops and glide entries are arrivals into the body's bubble with entry limit R_out,b (our reading of your spec; please confirm or correct it) | 2026-10-30 |
> | 7 | `AoiRangeMismatch` reports exempt ranges raised by low cruisers | 2026-11-13 |
> | 8 | Kit drops, item containers and wrecks are client-less groups | 2026-11-13 |
> | 9 | Wing links only within D_split; mutual damage in the last 10 s is an engagement link | 2026-11-13 |
> | 4 | The compact payload is ship wire mode 3, with the about 26 B layout given in the proposal | 2026-12-04 (P2c) |
> | 5 | Your sender's budget setting is about 97 kB/s less the kit loop's own sends, not 100 kB/s | 2026-12-04 (P2c) |
> | 6 | Your seated-passenger kit patch is brought forward into our P2c; please send your version or review ours | 2026-12-04 (P2c) |
>
> **3. Kit patch #9 is in the shape your §0.9 refuses: which shape do both kit copies use?**
> We landed it on 2026-09-30 (`a38c7171`, our VENDOR_PATCHES #9) as a post-pass:
> - the writes only read the flags;
> - `BaseGameNetworkManager`'s send loops record each handler they wrote, and call a new
>   `IEntityMovementServerStateFlags.ConsumeServerStateFlags()` once each after their last
>   player;
> - `NavMeshEntityMovement`'s consume also clears the server-side `_sendingDash` (KIT_AUDIT B11);
> - `OnTeleport` restarts interpolation, which you agreed to carry.
>
> Your §0.9 accepts only the stamp-in-handler shape. Your reason is that your sender runs after
> the kit loop (`LiteNetLibManager.cs:221-226`), so a post-pass either never clears a wrapped
> handler or clears flags the sender hasn't sent yet. We agree that this breaks once your sender
> is integrated, and we expect to rework #9 to your shape. Before we do:
> a. Confirm that stamp-in-handler is final for both copies, and send your exact version: one
>    `long` stamp per flag; −1 from the setter; a write clears a set flag when
>    `stamp >= 0 && stamp != writeTimestamp`, and otherwise carries it and sets the stamp; on
>    `CreateMovementData`, which value of `LocalTick` is the key?
> b. Does `ConsumeServerStateFlags` and its interface go away entirely? Or do you want it kept
>    as dead code, for parity with your copy?
> c. Is our `_sendingDash` clear in the consume enough? Or should it move into the handler in
>    your form?
> d. When do you need it: before your G1, or only for your character A/B rows?
>
> **4. The handoff acknowledgement our drop waits on** (§L.10 asked us to agree it; needed by
> our P2b, about 2026-11-23):
> a. What is the signal: an event or callback (name and signature), and which process raises it?
> b. Are there failure and timeout semantics? If a handoff fails or exceeds a deadline, does the
>    drop abort to supercruise, retry, or complete on the source process?
> c. Can our dev `HandoffGate` stub raise exactly that signal, so P2b is tested against your
>    shape before your add-on is imported?
>
> **5. The sync contract renames** (your Phase 0 summary, item 4). We accept
> `Mesh/Sync/MeshSyncContracts.cs`, the namespace `MultiplayerARPG.MMO.Mesh.Sync`,
> `MarkShipChanged` → `MarkSourceChanged` and `shipObjectId` → `objectId`. Please confirm:
> a. that our `ShipMovementComponent` implements `IMeshStateSource` (our plan says
>    `IShipStateSource`), and its exact member list;
> b. that `WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)` is
>    unchanged from §L.12;
> c. which `IsAtRest` definition is normative. Yours is |v| < 0.01 **and no pending bit**. Ours
>    (`ShipMovementComponent.IsAtRest`) is |v| < 0.01 m/s and |ω| < 0.001 rad/s, motion only,
>    with a pending teleport shown through `sendReliably` instead. We can change ours;
> d. when you will send the frozen file, with its `.meta` GUID.
>
> **6. The bot harness for S9** (§L.14; we need 64 clients against a space map server by
> **2026-12-14**, our week 12.5). Your Phase 0 puts the phantom clients in stage 0b, after 0a,
> and your §1 step 0 waits on Brenden's decision about the chain4 gate failures. With that order:
> a. What is your estimated date for 0b running 64 phantom clients against our map server?
> b. If it is later than 2026-12-14, we build our own synthetic-client load generator (plan §N).
>    Would you rather we build ours on a copy of your phantom harness? (Brenden will answer your
>    question 6 on giving us the rig parts.)
>
> **7. Pooled results.** Your §5 defines `mesh-p0/1` JSONL under `Logs/p0/`. Should our space
> rows (S1, S5, S8 with `game: space`) be written in our repo and sent to you, or into a shared
> location? If shared, which one? Our S8 runs first, in the next two weeks.
>
> ### B. Our answers to your questions for the space team (your Phase 0 §7)
>
> 1. **CE-12 shape:** see A.3. We landed the post-pass shape and expect to rework it to yours.
> 2. **The header bit layout:** as agreed in §L.12, `byte0 = (mode & 3) | (flightMode & 7) << 2`
>    and `byte1 = epoch`, with mode 0 bubble, 1 supercruise and 2 frame change. **Our proposed
>    compact indicator is mode 3**, and the quantized layout is proposal 4 (A.2). Both are
>    waiting for your yes before the golden fixture is filled.
> 3. **Reliable forever after a server teleport of an unowned ship: confirmed, and fixed**
>    (2026-10-05, commit `bcbe9754`). It was as you found it: `WriteState` sent reliably
>    while `TeleportPending`, the ack was set only from owner uploads, and `Teleport` bumped the
>    sequence for any ship on the server. Now a ship with no owner connection treats a server
>    teleport as acknowledged at once (`ShipDiscreteState.AcknowledgeTeleport`), as
>    `IsWaitingClientTeleportConfirm` already assumed. So does a ship whose owner leaves before
>    echoing a teleport, which was the same bug by another route. For your sender, nothing else
>    changes: the sequence still advances and `DiscreteVersion` still bumps on it (never on the
>    ack), observers still snap on the sequence every state carries, and the bytes are
>    identical. An owner who takes the ship later and echoes an older sequence makes the
>    teleport pending again, and its states go out reliably until they echo it.
> 4. **Contract path, namespace and renames:** accepted (A.5). `IsAtRest` is implemented
>    (definition question in A.5c). A NaN distance will count as near in our `ShipSendPolicy`,
>    which P1 builds (nothing on our side reads a send distance yet).
> 5. **The four superlinear or allocating costs** (the O(AI × spawned) target scan, server
>    projectile GameObjects with `RaycastAll` every frame, the shield sync field written every
>    frame, parked bodies that may never sleep): received. We will answer each in writing after
>    checking them in our current tree. The target scan already uses bubble membership on large
>    maps since 0F (`faaa046c`), but not on collapsed ones.
> 6. **Our rows:** S8 first (next two weeks), S1 in our P1, S5 in P2c, all in your `mesh-p0/1`
>    format (A.7). B1/B2 on a real `ShipEntity` need your rig parts (A.6b).
> 7. **Fight target, lock visibility, low cruise:** all three were settled in the reply agreed on
>    2026-09-26 (attached, §L):
>    - 64 players plus their AI in one bubble on a shared process (§L.13);
>    - ships' base visible range A0 = 3,000 m, equal to the lock range, with locked targets kept
>      subscribed (§L.15);
>    - low cruise at up to 4 km/s, only inside body bubbles' local zones, always promoting to
>      supercruise on leaving one, and treated as in transit by the policy (§L.5).
>
>    If your copy still shows them as open, the 6.1 reply may not have reached you.
> 8. **Client read-path hardening:** landed as our kit patch #10 (`40f6f123`, 2026-09-30). The
>    client sets `reader.SetPosition(recordStart + length)` after every handler read, a length
>    the packet can't hold drops the rest of the packet with a warning, and all five kit readers
>    are synchronous.
> 9. **`NavMeshEntityMovement` is `partial`** and is extended by
>    `_Game/Scripts/KitHooks/OriginShiftKitHooks.cs` (origin-shift awareness only). Its
>    `WriteServerState` has no per-receiver side effect beyond the kit's. Since #9, the
>    flag clears moved out of the write into `ConsumeServerStateFlags`.
