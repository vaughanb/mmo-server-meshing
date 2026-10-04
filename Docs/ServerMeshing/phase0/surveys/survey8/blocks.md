# Step 8 survey: p0geom, blocks b1-b10, targets, calibrate and accept-b (read-only, 2026-10-04)

## 1. p0geom
- **Source.** The meshing reply the sheet cites as `Scratch/reply_combined.md` (`phase0_build_sheet.md:1317`) was not on
  A; the sent text was found in the space session's scratchpad and is now `Docs/ServerMeshing/phase0/meshing_reply.txt`.
  Part 1 "§7. What Phase 0 must measure" starts at :1136; **item 5 (:1160-1172):** "Scripted 64+64 and 400+100 fights
  with their visible-range formula, measuring: real V per client, mean and p95; the fraction of ships within 1 km;
  pilots' neighbours within 80 m in X/Z; extra subscriptions from locks beyond the visible range." Item 4 (:1146-1157):
  sender prototype costs (matches the sheet's "reply §7 item 4 gets a 10-build arm", :744).
- **Fights:** (a) 64 players + 64 AI, V = 128, shared process (reply :725); (b) 400 + 100, V = 500 (reply :783) =
  G1-S target/stress (sheet :1020-1021). The reply assumed 40 % within 1 km for (a), 25 % for (b) (reply :700) and 1-4
  pilot neighbours "depending on formation spacing" (reply :555). **No positions or velocity distribution is specified
  anywhere** (the rig's only geometry is the sender part's ellipse ≤ 280 m × scale at 50-125 m/s, sheet :848; the space
  plan's S9 is "64 bot clients … flying the scripted E2E pilot", `large-star-systems.md:1520`): the scenario generator is
  a decision.
- **A formula:** `large-star-systems.md:1008` (§C.15) A = A0 + (|v_ship| + f) · 1.25 s, f = fastest group in the same
  bubble, capped at the bubble roam, A0 = 3,000 m (also Terms :177, decision 10 :221, agreed contract
  `large-star-systems-meshing-contract.md:54`); a group in no bubble gets A = A0 (`:1009`). The reply used 2 km +
  (v_own + v_fastest) × 1.25 (reply :1730-1737); A0 became 3 km on 2026-09-26.
- **Subscription semantics:** the range belongs to the target (the AOI queries a sphere of the target's range, reply
  :547-549; `step7_design.md:46`); the kit grid is X/Z only, the space plan's SpaceGridAOI 3D (`large-star-systems.md:
  1013`): p0geom must state its metric or report both. The rig uses a fixed 2,000 m × scale (`p0ships.py:34`); step 7
  decision 6: the 3 km formula "belongs to step 8's p0geom only".
- **Pilots within 80 m X/Z:** kit default range 80 m on an X/Z grid; a pilot is seen by its owner plus players within
  80 m X/Z (reply :555). The plan since decided seated characters carry their ship's range (`large-star-systems.md:1010`):
  then every client subscribes every pilot. Report both counts.
- **Locks:** 3 s, 25° cone, 3,000 m (`surveys/survey7/space.md:131`); locked targets stay subscribed beyond A
  (`large-star-systems.md:1011`); extra lock subscriptions = lock pairs with distance > A (0 in 3D with A0 = 3 km = lock
  range unless A is capped below; report for A0 2 km and 3 km).
- **Tests** ("exact counts on hand-built geometry", sheet :877): 1 km and 80 m boundary pairs incl. exactly at the
  boundary (kit AOI is inclusive `<=`, `step5_design.md:75`); an X/Z-near Y-far pair (1 km X/Z, 20 km Y,
  `large-star-systems.md:1539`); A at rest, combat speed, boost (reply :1733-1734 recomputed for A0 3,000); the roam cap;
  a lock beyond A vs inside A; mean and p95 of V over clients.
- `Tools/ServerMeshing/p0geo.py` is unrelated (step 4's fantasy geography: halving_ratio, W*, s_min, layout_loads,
  map_size_table). Path: use `Tools/ServerMeshing/p0geom.py` (the sheet's `Tools/p0geom.py` predates the folder;
  `test_imports.py` enumerates `Tools/ServerMeshing`).

## 2. Blocks (sheet :947-980)
- Refusals today: `p0plans.STAGE_LATER` (`p0plans.py:60-62`) fanout, packets, prewarm, crossings, calibrate;
  `presence --observed` (`p0load.py:215`, `p0plans.py:63`); accept-b, observed, targets, syncrpc are not subcommands;
  `p0results` hard-wired to 0a (`STAGE = "0a"`, `FAMILIES_0A`, `p0results.py:26,31`); §5's family list (:1144) lacks
  observed, targets, syncrpc, accept-b.
- Keys: all exist in `regress.ENV_KEYS` (`regress.py:69-98`) and `ENV_REGISTRY` except `MESH_PREWARM` and
  `MESH_SHIP_PARK_SLEEP` (pending, `p0tools.py:86-89`; no C# reads them; `check_env` refuses pending keys,
  `rigrun.py:190-209`). Prewarm grammar `<kind>:<N>@<t>[/<perFrame>]` (:266), "built on the feeder" (step 7 d16).
- Lines: parsers exist for Send, Phantom, Sink, Bot net (`p0phantom.py:44-107`), Sender, CE-12 (`p0sender.py:29-100`),
  Ships, Feeder, Sync load, Bot ships (`p0ships.py`). Missing: Prewarm; **no handoff_ready line or parser** anywhere
  (§5 names `latency_ms{handoff_ready,…}` at :1160).
- **b1 Acceptance-b** (~20, attended, B): virtual against sink, eye hiding, sender byte-exact with bots, ship seen by a
  bot, CE-12, meter overhead. Keys MESH_PHANTOM, MESH_METER_SENDS, MESH_SENDER, MESH_RIG_SHIPS, MESH_CE12_CHECK. Lines
  Send, Sink, Phantom (eyeSeen), Sender, Bot net, Bot ships, CE-12. Trust step for "G1 verdict from sink rows" (:1013).
- **b2 J-lite** (4, attended): 50 real bots vs 50 character phantoms with uploads, sink on B, kit and char-sender arms
  (`MESH_PHANTOM=sink:50:character:C`, `MESH_SENDER=on`, `MESH_SENDER_TIERS=20/20/20`). Gate J (:1043). Needs bots on B
  and the meshbot2..N registration pass.
- **b3 B1-O/R/SA** (24): O eyes {50, 200} × {steered, kinematic} × N 200 × 3 (`MESH_PHANTOM=virtual:P:eye:C`,
  `MESH_PHANTOM_VIEW=0` host mode, `step5_design.md:69`; `MESH_PUPPET_MOTION`; halves/`MESH_BAND` as a4); R client-driven
  owners {200, 400} → character phantoms; SA server-authoritative owners {200, 400} → `MESH_LOAD_PLAYERS`/
  `MESH_LOAD_MOTION` (inferred; the sheet does not name SA's mechanism). Lines Work ticks, Phantom, Puppets. Gates G3-F
  (O, :1032), §4 refit (a_R, gap H).
- **b4 B1-M** (18): eyes next to 200 monsters, owned awake, vs steered/kinematic puppets, both rate sets. Awake =
  `CountSubscribers() > 0` (`MonsterActivityComponent.cs:149-154`, CE-7). Monsters come only from scene spawn areas
  (`MESH_MONSTER_AREAS=all` ≈ 294); **no key sets exactly 200 or places them**. Feeds k_m (:1109).
- **b5 B1-S** (~36): 30/0.02, scale 0.03; `MESH_LOAD_SHIPS=C:N:ai|mirror` (AI {100, 200, 400}, mirror {100…800}),
  `MESH_PUPPET_FEEDER=ship:N@20` and `character:200@20`, `MESH_SHIP_SCALE` + `p0ships.scaled_env`, `MESH_RATES`. Lines
  Ships, Feeder, Work ticks. Gates G3-S (e, k_A, ℓ), Kill ships, G2-S (:1022-1024). Parked rows want
  `MESH_SHIP_PARK_SLEEP` (missing). Unclear whether `observed` covers B1-S (:869 lists S under observed).
- **b6 B2 fan-out** (~98, 9 h): arms char-kit; char-sender parity 20/20/20; char tiered 20/10/5/20; ship-kit-56
  (`MESH_SHIP_WIRE=header56`); ship-sender-56 parity; ship-sender-56 tiered; ship-sender-28 tiered (compact28 +
  `MESH_SENDER_COMPACT=far`). Each at V {100, 300, 500} with P stepped 50/100/200/400 at 90 s per step
  (`MESH_PHANTOM_STEPS=50,100,200,400@90`, eyes only, `step5_design.md:113`); tiered arms skip V 100. **Ship arms:** ship
  phantoms refuse VIEW, STEPS and AREA (`p0phantom.py:105`), so stepped ship arms need eye host mode on load ships
  (`ServerMeshShipMovement` is an `IEntityMovementDataHandler`, :20); V then "as measured" from ships in the 2 km × scale
  range — needs design. Sub-arms: classify `MESH_SENDER_CLASSIFY_BUILDS=10` (exists); sync/RPC
  `MESH_SYNC_LOAD=dirty:{0,250},rpc:{0,2100}` (exists, needs load ships); 60 fps `MESH_RATES=60/0.03`. Lines Sender (hz
  gauges for "≥ 10 Hz within 1 km", decision 26), Phantom down p95, Work ticks `send` with `MESH_AOI_TIMING=1`. Gates
  G1-S target/stress, G1-F, the s term for G2. Caveat: "pairs within 1 km get ≥ 10 Hz" is readable from hz gauges only
  if R0/tiers map 1 km × scale to near/mid; no per-distance bucket.
- **b7 B3** (16): ship-sender-28, P {100, 400}, V 500, packet cap 1020/510/255 × 3, sink on B; kit 400 × 500 characters
  virtual. Keys `MESH_SENDER_PACKET_BYTES`, `MESH_PHANTOM=sink:P:ship:C` (`remoteb sink-start --sink-count P`),
  `MESH_THREAD_CPU=1`. Send window `wire B / pkt` gives datagrams. "Thread and datagram saturation" has instruments
  but **no defined arm**. Feeds the per-packet cost with CI for G1 (:1013).
- **b8 B6** (36): N {100, 300, 650} × {steered, kinematic, ship}; 650 at 50/frame; 650 with 100 eyes (eye phantoms,
  VIEW 0). **MESH_PREWARM missing; range-change and group-arrival bursts (gap D) have no rig feature or key.** No §3
  gate; feeds gap D.
- **b9 B10 crossings and wake** (12, attended): 20/50 bots on B walking across the halves cut. **Missing:** bots on B; a
  handoff_ready line/parser. Wake can borrow `MESH_SLEEP=1` and regress `wake` (`regress.py:839-860`). Gate Crossings
  (:1035).
- **b10 J-full** (8): P {50, 100}, real bots vs phantoms. Same missing bots and registration as b2. Gate J.

## 3. targets (S-T, F1, F2)
- **S-T** (likely G1-S target): `MESH_PHANTOM=virtual:64:ship:C` + `MESH_LOAD_SHIPS=C:64:ai`, scale 0.03, 30/0.02 —
  already `accept_step7.py` `g1s`. Stress: 400 ship phantoms + `C:100:ai`.
- **F1:** `MESH_PHANTOM=virtual:150:character:C`, `MESH_PHANTOM_AREA=0,0,100,100`, `MESH_PHANTOM_STAND=0.7` (all exist;
  AREA not with VIEW, `ServerMeshPhantomSettingsTests.cs:234`). **30 awake NPCs have no mechanism** (only stand-in:
  `MESH_LOAD_PLAYERS=30` + `MESH_LOAD_AREA=x,z,50`, server characters not NPCs). Decision needed.
- **F2:** 100 character phantoms on `MESH_PHANTOM_AREA=disc:cx,cz,50` with `MESH_PHANTOM_FIGHT=1` (exists). **No key
  places 100 monsters within 50 m** (`MESH_MONSTER_AREAS` only switches scene areas on, `MeshMapNetworkManager_Ghosting.cs:
  276-316`); they would be awake once phantoms subscribe.
- Rig view: cell range from `MESH_BAND` (80 m, :1026).

## 4. calibrate and accept-b
- **calibrate** = phantom validity J: bots calibrated against character phantoms (critique 1, :1277); covers b2 (and
  b10); before the 9 h fan-out (:1001); graded by J (:1043): phantom tick work within ±15 % of the bot run after adding
  the measured upload cost; bytes within ±5 %. Needs bots on B, the registration pass, a sink. Old mapping to step 5
  (`step4_design.md:736`, `p0plans.py:61`) is stale.
- **accept-b** (b1) consolidates: virtual against sink (step 5 sink acceptance, ±3 % of BytesSent, :715; its script was
  scratch-only; `phase0/accept_step7_sink.py` has the threaded run_window + remoteb sink pattern); eye hiding
  (`accept_step5.py` "eyes", eyeSeen 0; the sheet also wants "a real bot in an eye group spawns no eye", :714); sender
  byte-exact with bots (`accept_step6.py` gate 6 "bots", records/s ±2 %, its own `run_window_with_bots`, :98); ship seen
  by a bot (`accept_step7.py` "bots"); CE-12 (`accept_step6.py` "ce12", expected `pass`, `step6_design.md:70`; sheet §0.9
  stale); meter overhead (`accept_step5.py` "overhead" via `p0accept.overhead`, < 1 % CI; a canary-based version has no
  tool path, `surveys/survey5/sinkrig.md:130`).

## Missing for step 8
1. `MESH_PREWARM`, `MESH_SHIP_PARK_SLEEP` (pending, no C#). 2. Range-change and group-arrival bursts. 3. Bots on B + the
meshbot2..N registration pass. 4. A handoff_ready line and parser. 5. Placing N monsters/NPCs in an area (F1, F2, b4's
200). 6. Stepped P and V for ship arms. 7. A "thread/datagram saturation" arm definition. 8. Stage 0b support in
p0results. 9. A fight-geometry spec for p0geom. Ambiguity: the A0 metric, X/Z (rig) or 3D (space plan).
