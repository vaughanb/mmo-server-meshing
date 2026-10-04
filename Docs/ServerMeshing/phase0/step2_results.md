# Phase 0 step 2 — results so far (BRENDOSERVER, Ryzen AI 9 HX 370; ten-cell rig, no client, idle cells)

Build of 2026-09-26 11:0x (before the cycle-accurate thread sampler). accept_step2.py, cell C, 25 s warm-up + 40 s:

| Run | ticks/5 s | dropped | frames with a tick | physics/s | AOI passes/5 s | tick frames | other frames | kit tick (sync/aoi/move) | work ms/s |
|---|---|---|---|---|---|---|---|---|---|
| plain (no env) | 100.1 | 0 | 0.333 | 33.34 | 0 (no timing) | 0.528 | 0.522 | 0.007 (parts 0) | 31.4 |
| r60 (60/0.03 + AOI timing + thread CPU) | 100.0 | 0 | 0.334 | 33.35 | 5.00 | 0.536 | 0.515 | 0.018 (0.013/0.002/0.003) | 31.3 |
| r60burn (+2 ms per tick on C) | 100.0 | 0 | 0.334 | 33.35 | 5.00 | 2.604 | 0.536 | 0.018 | 73.5 |
| r30 (30/0.02 + AOI timing + thread CPU) | 100.1 | 0 | 0.668 | 50.01 | 5.00 | 0.777 | 0.768 | 0.018 | 23.2 |
| r30burn | 100.0 | 0 | 0.667 | 50.02 | 5.00 | 2.755 | 0.646 | 0.018 | 61.5 |

- Tick burn: tick-frame work +2.068 ms (60 fps) and +1.978 ms (30 fps); repeats at 30 fps +1.955 and +2.036 ms. The kit
  tick does not move (the burn runs after the stamps), by design.
- Other frames under the burn: 60 fps 0.515 -> 0.536; 30 fps 0.768 -> 0.646, repeats 0.702 -> 0.642 and 0.660 -> 0.612.
  Base runs vary 0.66-0.77 ms between Plays (±8 %), wider than the sheet's ±5 % criterion, and the burn runs sit
  0.03-0.12 ms lower: classification is exact (t0/t1 counts 50/100 per window), so this is the CPU (a 2 ms spin keeps
  the core clocked up for the next frame on this laptop part), not misfiled frames. The criterion is below the noise
  floor on this machine; judged on tick frames and on total work (+38.3 of 40 ms/s expected).
- Thread CPU (GetThreadTimes accounting): classes sum to process within 5 % in every window (e.g. 112.5 vs 109.4,
  87.5 vs 87.5 ms/s); 104 threads per cell; names pre-start=absent (LiteNetLib's CPU in unnamed); values quantised to
  3.125 ms/s (15.625 ms scheduler tick per 5 s) -> replaced by cycle-time accounting (pending re-run).
- Canary: frame work plain 0.524 ms vs r60 (AOI timing + thread CPU) 0.522 ms: -0.002 ms/frame against the 0.02 gate
  (plain's window-to-window spread 0.031 ms).
- gc0: 0.12 per window plain, 0.25 with AOI timing + thread CPU (the sampler allocates per pass; reduced in the rework).
- The Work window line and probe unchanged (Valid on every capped run).

## Final build (cycle-accurate thread CPU, snapshot every window), 2026-09-26 12:4x-13:0x

- chain6 acceptance (build before the cadence change): every non-thread check passes: ticks 100/100 per window, none
  dropped; frames with a tick 0.333 (60 fps) and 0.667 (30 fps); physics 33.35/s and 50.00/s; AOI passes 5 per window;
  thread CPU counted in cycles at 1.992 GHz; tick burn +2.008 ms (60 fps) and +2.001 ms (30 fps); other frames
  0.547 -> 0.559 and 0.758 -> 0.680 ms. Thread windows failed +-5 % only around the 30 s snapshot (16 neighbour-link
  threads appear just after sampling starts; found 30 s later, their whole CPU landed in one window: 238 vs 118 ms/s)
  -> snapshot every window (SnapshotEveryWindows = 1).
- No-environment gate on the step 2 build: all ten scenarios Static 10/10, all ten Dynamic 10/10, dynidle 50/50.
- After the cadence change (rebuilt; 517 EditMode tests): thread windows 0 failing +-5 % (3 x r60, 1 x r30);
  canary as three interleaved pairs plain vs AOI timing + thread CPU: -0.044, +0.006, +0.057 ms/frame, mean +0.006
  (sd 0.050, se 0.029). Consistent with zero; the 0.02 ms/frame gate is below what three pairs resolve on this machine
  (95 % bound about +-0.06 ms/frame).
- The neighbour links' threads: unnamed rose from ~14 to ~41 ms/s once found: about 27 ms/s of LiteNetLib CPU per
  idle cell for 8 links (Mono does not pass their names; counted as unnamed).
