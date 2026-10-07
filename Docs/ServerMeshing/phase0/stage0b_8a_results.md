# Phase 0 stage 0b: step 8a's blocks on machine A (draft, 2026-10-07)

Draft results of the step 8a blocks (b3-b8 virtual parts: observed, targets, syncrpc, packets, prewarm, fanout),
measured on machine A. It is not the DESIGN 0.74 row: that row is one stage-level row for all of stage 0b and waits for
step 8b's blocks (accept-b, calibrate, crossings, J-full and the sink arms, all two-machine). Figures are cell main-thread
work in ms/s (1,000 = the main thread never waits; "sat" = the run was excluded as saturated), the median of the
accepted repeats (normally 3), newest row per plan key and step. The tables come from `Logs/p0/rig_*.jsonl`.

## 1. Machine and data sets

Machine A: AMD Ryzen AI MAX+ 395 (Corsair AI Workstation 300), Windows 11 build 26200 → 26300 (KB5121794, installed with
the 2026-10-06 reboot), High performance plan, Corsair performance mode on its top setting. The canary (coarse C, 200
kinematic owners, 30/0.02) is the machine's reference; its level moved with states no setting names (HANDOFF §3):

| Data set | Row host | Player build | Canary baseline | Blocks |
|---|---|---|---|---|
| E0 | `BRENDOAIBOX` | beca795 | 68.9 / 68.4 | observed, targets, syncrpc, packets, prewarm (2026-10-04/05) |
| E1 | `BRENDOAIBOX#2026-10-06b` | beca795 | 74.5 / 75.4 | fanout (2026-10-06 14:02-21:40; 4 canaries within 1 %) |
| E1' | `BRENDOAIBOX#2026-10-06b` | 3867cc0 | 70.9 / 69.0 / 69.5 | fanout re-run of 17 runs (2026-10-06 23:49-01:47) |

Set aside, not used here: Balanced-plan rows (`Logs/p0/balanced_3d52e53/`), baselines taken on an unsettled machine
(`Logs/p0/retired/`), and the partial fanout block under epoch `2026-10-06` (its 80.8 baseline was taken while A was in
use). E1' exists because 17 fanout steps had been rejected by a false tick miss (§4); its build differs from E1's only by
another session's contract-only commit (ff25dea: a comment and an unimplemented interface). E1 and E1' cannot be pooled
by `--bridge` (their canaries differ by 7 %); the same fanout steps read a median 3.4 % lower in E1' (36 pairs, 0.90-1.02).
Comparisons below are made within one data set.

## 2. Results

### observed (b3; O, R, SA, M, S)

| Part | Point | Work (cell) | Note |
|---|---|---|---|
| O | 200 kinematic puppets, 50 eyes | E 320, C 111 | eyes and puppets on E |
| O | 200 kinematic puppets, 200 eyes | E 430, C 107 | |
| O | 200 steered puppets, 50 eyes | E 693, C 121 | steered costs ~2.2× kinematic |
| O | 200 steered puppets, 200 eyes | sat | |
| R | 200 client-driven owners, view 50 | C 384 | |
| R | 400 client-driven owners | sat | |
| SA | 200 server-authoritative owners, view 50 | C 671 | 1.75× R |
| SA | 400 | sat | |
| M | 200 monsters owned on C, 50 eyes | C 224, E 90 | |
| M | 200 monsters as kinematic puppets on E | C 224, E 166 | |
| M | 200 monsters as steered puppets on E | C 231, E 296 | kinematic puppets ~0.56× steered |
| S | AI ships 100 / 200 / 400 (scale 0.03) | 44 / 78 / 171 | grows faster than linear past 200 |
| S | dense AI, 100 ships at scale 1.0 | 42 | |
| S | mirror ships 100 / 200 / 400 / 800 | 31 / 42 / 69 / 126 | |
| S | feeder ships 100 / 200 / 400; feeder characters 200 | 30 / 39 / 62; 52 | |
| S | 200 parked ships, asleep / pinned awake | 26 / 34 | the space game never sleeps a ship |

### targets (b4)

| Target | Kit sender | Tiered sender |
|---|---|---|
| F1 at 30/0.02 | 328 | 229 (-30 %) |
| F1 at 60/0.03 | 415 | 311 (-25 %) |
| F2 at 30/0.02 | 593 | 481 (-19 %) |
| F2 at 60/0.03 | sat | 577 |
| S-T (30/0.02) | 112 | 94 (-16 %) |
| S-stress (30/0.02) | sat | sat |

S-stress does not fit one cell on A in either arm.

### syncrpc (b5, S-T at scale 0.5, kit)

| Dirty syncs D | RPC R /s | Work | Added |
|---|---|---|---|
| 0 | 0 | 111 | |
| 0 | 2,100 | 141 | +30 |
| 250 | 0 | 418 | +307 |
| 250 | 2,100 | 455 | +344 |

Dirty sync fields dominate; RPCs at 2,100/s are cheap by comparison.

### packets (b7 virtual part)

| Arm | Point | Work |
|---|---|---|
| cap: ship-sender-28 tiered, 100 phantoms + 400 AI, V 500, scale 0.5 | packet cap 1,020 / 510 / 255 B | 400 / 410 / 437 |
| cap, 400 phantoms + 100 AI | every cap | sat |
| kit: 400 character phantoms seeing 500 movers | | sat |
| saturation probe: eyes seeing 500 movers, P 100 → 800 | P 100, 200 | 1,000 (pegged) |

A smaller packet cap costs main-thread work (+2.5 % at 510 B, +9 % at 255 B). **The saturation probe cannot do its job
on virtual phantoms:** they never open a socket, so the LiteNetLib thread read 0 ms/s and 0 datagrams/s at every step,
and only the main thread saturated. The network-thread probe moves to step 8b's sink arms (real sockets on B); the 8a
plan is left as it is so existing rows keep their keys.

### prewarm (b8; bursts at warm-up + 10 s)

| Burst | N 100 / 300 / 650 |
|---|---|
| kinematic, all at once | 25 / 48 / 46 |
| steered, all at once | 48 / 136 / 307 |
| ship, all at once | 22 / 42 / 87 |
| kinematic 650 at 50 per frame | 86 |
| kinematic 650 with 100 eyes on 200 load players | sat |
| group of eyes (100 eyes, 100 players), N 100 / 300 | 354 / sat |
| range change 80 → 160 / 80 → 300 m (100 eyes, 200 players) | 293 / 323 |

To check in the final write-up: the paced kinematic burst (86) reads above the all-at-once one (46), and kinematic 650
reads below kinematic 300; both are window means over the burst, so the window's share of the burst may explain it.

### fanout (b6; 30/0.02 unless stated; steps are added watching clients)

Largest accepted step and its work; E1 unless marked E1'.

| Arm | V 100 | V 300 | V 500 |
|---|---|---|---|
| characters, kit | 400: 450 | 100: 532 | 50: 694 |
| characters, sender (kit rules) | 400: 248 | 200: 444 (E1') | 200: 744 |
| characters, sender tiered | | 200: 447 (E1') | 200: 755 |
| ships, kit (56 B) | 400: 299 | 200: 432 | 100: 434 |
| ships, sender (56 B, kit rules) | 400: 209 (E1') | 400: 527 | 200: 471 (E1') |
| ships, sender tiered 56 B | | 400: 523 | 200: 469 (E1') |
| ships, sender tiered 28 B | | 400: 521 | 200: 469 (E1') |
| characters at 60/0.03, kit / sender / tiered | | 50: 570 / 200: 633 / 200: 636 | |
| ships classify (10 builds), sender 56 B | | 200: 274 (400 sat in 2 of 3) | |

- The state sender does 30-45 % less main-thread work than the kit's at the same load (V 100, step 400: characters 248 vs
  450, ships 224 vs 299) and fits twice the load where the kit saturates (ships V 300: step 400 vs 200).
- Tiering makes no CPU difference in these runs (within 2 %); its benefit is bytes, not work.
- The 28-byte compact ship record costs the same CPU as the 56-byte one; it saves bandwidth only.
- At 60 fps the kit fits only the first step at V 300; the sender reaches step 200.

## 3. Saturation summary on A

Saturated (one cell cannot carry it on A): O steered with 200 eyes; R and SA at 400 owners; F2 kit at 60 fps; S-stress
(both arms); packets cap at 400 phantoms and kit 400 × 500; prewarm 650 kinematic with eyes and the 300-character eye
group; fanout's top steps as in the table.

## 4. Harness corrections made during the stage

- Step windows wait out the bind ramp and end 1 s before the next step's stamp (44ef586).
- Prewarm runs tolerate two short-of-AOI windows around the burst; saturation probes skip the AOI check (44ef586).
- Tick check (f39a139): a probe window one 30 fps frame over 5 s (5.03 s) expects 101 ticks, runs 100 and prints
  "1 dropped" by arithmetic. The old check counted that as a dropped tick and rejected 17 fanout sender steps; now a
  window misses only when its ticks are off by more than one or it held a tick-cap frame. The 17 runs were re-measured
  (E1') and every such step was accepted.
- Machine epochs: rows are keyed on `COMPUTERNAME#<epoch>` when `Logs/p0/MACHINE_EPOCH` names one (44ef586).
- `p0load --keys` re-runs chosen planned runs (08c4158).
- Not yet committed: a rising stepped run stops once a step is graded saturated (later steps get a "not measured" row),
  and canary rows record `machine_state` (Windows session, mean % processor performance over the measurement).

## 5. Open for the stage 0b report

- Step 8b's blocks, including the real network-thread probe (sink phantoms on B) that replaces the virtual one.
- Why A's canary sits at ~69 at night and ~75 in the afternoon with A idle: the new `machine_state` field records the
  session and CPU performance with every canary from now on.
- The prewarm oddities above.
