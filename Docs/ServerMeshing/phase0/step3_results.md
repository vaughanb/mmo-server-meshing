# Phase 0 step 3 — results (BRENDOSERVER, Ryzen AI 9 HX 370; ten-cell rig, no client)

Final build (error-offset track, watched puppets posed at the kit tick, two steer histograms), chain7, 2026-09-26 14:1x.

accept_step3.py: 60 synthetic players crowded on C in x [50,100] z [-30,30] beside the C/E line, no monsters; E holds a
band of their puppets, nobody watches (no client). 30 s warm-up, 40 s measured, one run per arm. All checks pass.

| Arm | E puppets | E work ms/s | tick frames ms | other ms | steer kinematic p95 / max | snaps | poses per puppet /s |
|---|---|---|---|---|---|---|---|
| steered (today) | 60 steered | 163.7 | 2.934 | 2.629 | – | – | – |
| kin (MESH_PUPPET_MOTION=kinematic) | 60 kinematic | 47.8 | 0.847 | 0.774 | 0.15 / 0.59 m | 0 | 10.0 |
| kin_kwalk (+ MESH_LOAD_MOTION=kinematic) | 60 | 47.5 | 0.816 | 0.779 | 0.05 / 0.62 m | 0 | 10.0 |
| kin_inert (+ MESH_LOAD_MOTION=inert) | 60 | 38.2 | 0.631 | 0.642 | no moving samples | 0 | 10.0 |

- Indicative only (one run each, owned cost not measured here, idle E about 31 ms/s from step 2): unwatched puppet cost
  about 2.2 ms/s each steered against about 0.28 ms/s kinematic, roughly 8x cheaper. Stage 0a (a4, B1-U) measures k
  properly with repeats and the owned cost.
- Default output unchanged (no Puppets line with the setting unset). Kinematic bots stream (moving samples keep coming).
- Before this build (the lead-0 blend build, chain before the review fixes): all ten scenarios + dynidle passed with
  MESH_PUPPET_MOTION=kinematic.
- chain7 on the final build: no-environment gate Static 10/10, Dynamic 10/10, dynidle 50/50; with
  MESH_PUPPET_MOTION=kinematic all ten scenarios + dynidle 11/11. 728 EditMode tests.
