# Phase 0 stage 0a report 20261001_205116

345 run rows (305 accepted, 32 excluded by canary drift), 22 prediction rows read; 193 summary, fit and gate rows of earlier reports ignored. Builds 7c99cd905738; CPU AMD Ryzen AI 9 HX 370 w/ Radeon 890M.

- 14 accepted run rows repeat a plan key on their build: only the latest row of each plan key is used

## Summary

| Game | Case | Arm | Kind | Rates | Owned / puppets / clients / V | Work ms/s (median [range]) | Non-AOI tick-frame p95 ms | Tick send p95 ms | Downlink p95 kB/s | Key metric | Gate | Verdict | n |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fantasy | b9-main [C] | coarse (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 437 / 0 / 0 / – | 492.6 [482.6–494.4] | 9.60 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | coarse (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 150 / 0 / 0 / – | 403.2 [400.1–418.5] | 8.35 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | halves (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 75 / 20 / 0 / – | 238.5 [237.9–243.3] | 4.90 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | halves (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 75 / 21 / 0 / – | 240.0 [235.8–242.2] | 5.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SW] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 19 / 0 / – | 99.7 [94.5–103.6] | 2.20 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [S] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 28 / 0 / – | 89.8 [89.0–106.2] | 2.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SE] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 16 / 0 / – | 98.4 [95.4–103.6] | 2.20 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [W] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 25 / 0 / – | 102.6 [99.1–115.3] | 2.35 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 38 / 0 / – | 91.3 [89.8–103.5] | 2.05 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 17 / 25 / 0 / – | 95.6 [91.5–106.3] | 2.05 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NW] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 16 / 16 / 0 / – | 92.0 [79.2–100.4] | 2.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [N] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 16 / 25 / 0 / – | 95.6 [86.6–97.8] | 2.15 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NE] | grid (players 150, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 16 / 16 / 0 / – | 86.2 [83.1–98.2] | 2.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | halves (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 225 / 51 / 0 / – | 297.9 [291.7–298.2] | 6.55 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | halves (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 213 / 54 / 0 / – | 296.4 [289.3–297.1] | 6.45 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SW] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 43 / 49 / 0 / – | 113.7 [106.0–117.7] | 2.55 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [S] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 52 / 89 / 0 / – | 127.3 [125.2–136.2] | 2.90 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SE] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 42 / 49 / 0 / – | 115.3 [109.9–116.2] | 2.65 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [W] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 54 / 93 / 0 / – | 139.9 [136.2–140.9] | 3.10 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 61 / 148 / 0 / – | 147.4 [144.7–161.3] | 3.30 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 50 / 83 / 0 / – | 125.0 [119.6–130.1] | 2.85 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NW] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 41 / 54 / 0 / – | 112.1 [102.1–115.2] | 2.45 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [N] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 54 / 95 / 0 / – | 128.4 [123.4–133.3] | 2.85 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NE] | grid (players 150, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 39 / 54 / 0 / – | 116.5 [115.3–123.3] | 2.70 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | coarse (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 250 / 0 / 0 / – | 688.7 [682.5–704.6] | 13.15 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | halves (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 125 / 34 / 0 / – | 393.4 [384.8–395.8] | 8.20 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | halves (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 125 / 31 / 0 / – | 395.8 [382.6–397.9] | 7.95 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SW] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 29 / 0 / – | 133.0 [127.5–139.7] | 2.90 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [S] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 43 / 0 / – | 148.6 [135.1–149.2] | 3.30 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SE] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 23 / 0 / – | 134.6 [131.7–136.4] | 2.95 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [W] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 45 / 0 / – | 141.4 [125.7–148.0] | 3.35 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 63 / 0 / – | 151.0 [144.6–151.7] | 3.35 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 37 / 0 / – | 132.7 [124.1–155.8] | 3.05 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NW] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 28 / 27 / 0 / – | 119.6 [118.9–125.4] | 2.80 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [N] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 27 / 41 / 0 / – | 137.3 [133.8–139.4] | 3.25 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NE] | grid (players 250, monsters off, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 27 / 26 / 0 / – | 133.3 [127.5–141.5] | 3.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | coarse (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 533 / 0 / 0 / – | 751.7 [750.8–759.7] | 14.55 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | halves (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 270 / 66 / 0 / – | 432.1 [430.8–444.6] | 9.40 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | halves (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 263 / 62 / 0 / – | 425.7 [421.6–443.1] | 9.70 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SW] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 53 / 60 / 0 / – | 148.1 [146.6–156.6] | 3.40 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [S] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 64 / 108 / 0 / – | 177.3 [171.3–185.7] | 4.15 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [SE] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 48 / 61 / 0 / – | 147.9 [144.8–152.2] | 3.25 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [W] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 64 / 103 / 0 / – | 169.4 [166.0–180.9] | 4.00 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [C] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 78 / 168 / 0 / – | 199.7 [194.4–200.8] | 4.50 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [E] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 58 / 94 / 0 / – | 168.6 [163.0–174.8] | 3.70 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NW] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 48 / 70 / 0 / – | 146.1 [145.3–146.7] | 3.25 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [N] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 73 / 100 / 0 / – | 183.1 [171.8–186.0] | 4.10 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-main [NE] | grid (players 250, monsters all, band 0.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 48 / 69 / 0 / – | 150.0 [143.6–152.8] | 3.35 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-bands [C] | coarse (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 533 / 0 / 0 / – | 748.4 [740.8–772.1] | 14.80 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | halves (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 269 / 25 / 0 / – | 436.2 [435.6–440.5] | 9.05 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | halves (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 264 / 18 / 0 / – | 429.5 [429.4–445.0] | 9.05 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [W] | strips4 (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 106 / 8 / 0 / – | 231.3 [216.6–235.9] | 4.75 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | strips4 (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 166 / 33 / 0 / – | 242.3 [240.1–256.0] | 5.05 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | strips4 (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 155 / 36 / 0 / – | 248.8 [248.5–250.6] | 5.50 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [NE] | strips4 (players 250, monsters all, band 20.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 105 / 8 / 0 / – | 217.3 [216.9–230.0] | 4.60 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | coarse (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 534 / 0 / 0 / – | 767.5 [761.2–773.1] | 15.15 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | halves (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 270 / 41 / 0 / – | 439.4 [437.9–443.9] | 8.95 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | halves (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 261 / 35 / 0 / – | 436.8 [434.1–444.0] | 9.05 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [W] | strips4 (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 106 / 44 / 0 / – | 244.2 [231.0–249.1] | 5.30 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | strips4 (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 168 / 67 / 0 / – | 264.8 [259.5–265.4] | 5.70 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | strips4 (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 154 / 68 / 0 / – | 256.6 [251.3–261.6] | 5.60 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [NE] | strips4 (players 250, monsters all, band 40.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 104 / 40 / 0 / – | 233.1 [227.9–235.7] | 4.85 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | coarse (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 533 / 0 / 0 / – | 755.0 [753.2–769.8] | 14.70 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | halves (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 270 / 66 / 0 / – | 448.3 [438.0–456.1] | 9.45 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | halves (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 261 / 60 / 0 / – | 448.0 [436.3–466.2] | 9.55 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [W] | strips4 (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 106 / 117 / 0 / – | 259.5 [252.6–272.6] | 5.85 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | strips4 (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 163 / 137 / 0 / – | 295.2 [281.5–295.2] | 6.45 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | strips4 (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 159 / 136 / 0 / – | 300.9 [279.6–307.5] | 7.25 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [NE] | strips4 (players 250, monsters all, band 80.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 106 / 105 / 0 / – | 255.7 [252.1–261.2] | 5.70 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | coarse (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 533 / 0 / 0 / – | 746.5 [730.3–762.1] | 14.15 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | halves (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 270 / 164 / 0 / – | 478.0 [471.7–481.4] | 10.35 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | halves (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 264 / 161 / 0 / – | 480.5 [471.1–487.2] | 10.25 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [W] | strips4 (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 105 / 166 / 0 / – | 276.2 [274.3–278.9] | 6.20 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [C] | strips4 (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 167 / 260 / 0 / – | 327.1 [319.6–340.1] | 7.60 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [E] | strips4 (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 158 / 268 / 0 / – | 326.6 [320.7–354.0] | 8.00 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-bands [NE] | strips4 (players 250, monsters all, band 150.0, load spread, puppets kinematic, rates 60/0.03) | character | 60/0.03 | 104 / 156 / 0 / – | 273.0 [265.8–284.0] | 6.45 | 0.05 | – | strips4/halves 0.65 0.62 0.70; worst \|predicted - measured\| 0.058; slope 0.358 in 0.32492 [0.064268, 0.58558] | G-GEO (a); G-GEO (b); G-GEO (c) | GO; FAIL; PASS | 3 |
| fantasy | b9-steered-b150 [C] | coarse (players 250, monsters all, band 150.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 533 / 0 / 0 / – | 744.0 [743.3–746.2] | 14.40 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b150 [C] | halves (players 250, monsters all, band 150.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 271 / 162 / 0 / – | 619.3 [596.6–629.6] | 13.50 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b150 [E] | halves (players 250, monsters all, band 150.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 262 / 165 / 0 / – | 603.5 [596.7–611.2] | 13.05 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [C] | halves (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 270 / 64 / 0 / – | 517.1 [491.2–531.1] | 10.50 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [E] | halves (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 262 / 63 / 0 / – | 502.1 [485.7–502.4] | 10.25 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [W] | strips4 (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 106 / 119 / 0 / – | 338.0 [328.7–349.1] | 7.10 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [C] | strips4 (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 170 / 135 / 0 / – | 429.8 [423.0–446.9] | 9.95 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [E] | strips4 (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 155 / 133 / 0 / – | 417.8 [402.3–427.6] | 9.05 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | b9-steered-b80 [NE] | strips4 (players 250, monsters all, band 80.0, load spread, puppets steered, rates 60/0.03) | character | 60/0.03 | 104 / 108 / 0 / – | 327.2 [310.4–332.2] | 7.40 | 0.05 | – | worst \|predicted - measured\| 0.058 | G-GEO (b) | FAIL | 3 |
| fantasy | grid [C] | base (players 100, side 600, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 270.0 [265.8–311.0] | 5.35 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 100, side 400, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 264.4 [257.5–297.5] | 5.10 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 100, side 200, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 260.0 [255.8–279.0] | 4.95 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 100, side 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 255.8 [250.1–264.5] | 4.90 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 200, side 600, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 528.0 [525.4–540.0] | 9.90 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 200, side 400, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 525.1 [511.7–536.6] | 9.75 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 200, side 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 521.7 [508.6–527.3] | 9.85 | 0.05 | – | – | – | – | 3 |
| fantasy | grid [C] | base (players 200, side 100, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 510.4 [505.5–520.9] | 9.70 | 0.05 | – | – | – | – | 3 |
| fantasy | scale [C] | base (players 50, side 70.71, rates 60/0.03) | character | 60/0.03 | 50 / 0 / 0 / – | 139.9 [137.9–142.9] | 2.75 | 0.05 | – | – | – | – | 3 |
| fantasy | scale [C] | base (players 200, side 141.42, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 517.6 [508.0–524.0] | 9.80 | 0.05 | – | – | – | – | 3 |
| fantasy | attribution [C] | inert (players 200, side 141.42, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 224.5 [223.2–230.3] | 4.35 | 0.05 | – | – | N² @ 60/0.03 | NO VERDICT | 3 |
| fantasy | attribution [C] | no-fight (players 200, side 141.42, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 512.7 [508.5–521.8] | 9.65 | 0.05 | – | – | N² @ 60/0.03 | NO VERDICT | 3 |
| fantasy | attribution [C] | kinematic (players 200, side 141.42, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 130.7 [127.2–134.8] | 2.75 | 0.05 | – | – | N² @ 60/0.03 | NO VERDICT | 3 |
| fantasy | attribution [C] | monsters-on (players 200, side 141.42, rates 60/0.03) | character | 60/0.03 | 493 / 0 / 0 / – | 610.9 [604.5–634.7] | 11.75 | 0.05 | – | – | N² @ 60/0.03 | NO VERDICT | 3 |
| fantasy | sparse [C] | inert (players 200, side 600, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 227.1 [224.5–233.7] | 4.75 | 0.05 | – | – | – | – | 3 |
| fantasy | sparse [C] | no-fight (players 200, side 600, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 545.5 [535.0–577.0] | 10.70 | 0.05 | – | – | – | – | 3 |
| fantasy | sparse [C] | kinematic (players 200, side 600, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 143.1 [142.6–150.1] | 3.25 | 0.05 | – | – | – | – | 3 |
| fantasy | baseline [C] | idle (players 0, rates 60/0.03) | character | 60/0.03 | 0 / 0 / 0 / – | 23.2 [22.7–23.8] | 0.60 | 0.05 | – | – | – | – | 3 |
| fantasy | baseline [E] | idle (players 0, rates 60/0.03) | character | 60/0.03 | 0 / 0 / 0 / – | 23.4 [22.2–23.6] | 0.60 | 0.05 | – | – | – | – | 3 |
| fantasy | main [C] | puppets-steered (players 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 87.4 [86.4–90.0] | 2.15 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-steered (players 100, rates 60/0.03) | character | 60/0.03 | 0 / 100 / 0 / – | 240.0 [236.5–245.4] | 5.55 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | puppets-steered (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 157.1 [151.5–165.7] | 3.55 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-steered (players 200, rates 60/0.03) | character | 60/0.03 | 0 / 200 / 0 / – | 488.9 [482.6–505.4] | 12.25 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | puppets-steered (players 250, rates 60/0.03) | character | 60/0.03 | 250 / 0 / 0 / – | 185.5 [184.7–192.7] | 4.65 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-steered (players 250, rates 60/0.03) | character | 60/0.03 | 0 / 250 / 0 / – | 624.2 [604.7–625.1] | 15.00 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | puppets-kinematic (players 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 85.7 [85.5–86.0] | 2.00 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-kinematic (players 100, rates 60/0.03) | character | 60/0.03 | 0 / 100 / 0 / – | 56.0 [55.6–56.1] | 1.50 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | puppets-kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 147.0 [142.8–152.1] | 3.85 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 0 / 200 / 0 / – | 88.9 [83.2–95.3] | 2.75 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | puppets-kinematic (players 400, rates 60/0.03) | character | 60/0.03 | 400 / 0 / 0 / – | 277.8 [272.4–278.6] | 5.60 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | puppets-kinematic (players 400, rates 60/0.03) | character | 60/0.03 | 0 / 400 / 0 / – | 164.3 [153.1–166.4] | 5.30 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | owners-inert (players 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 123.2 [122.9–125.0] | 2.45 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | owners-inert (players 100, rates 60/0.03) | character | 60/0.03 | 0 / 100 / 0 / – | 44.0 [43.0–44.6] | 1.35 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | owners-inert (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 236.7 [235.7–249.3] | 5.00 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | owners-inert (players 200, rates 60/0.03) | character | 60/0.03 | 0 / 200 / 0 / – | 64.9 [64.9–71.5] | 2.35 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [C] | owners-inert (players 400, rates 60/0.03) | character | 60/0.03 | 400 / 0 / 0 / – | 530.0 [516.6–533.4] | 11.30 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | main [E] | owners-inert (players 400, rates 60/0.03) | character | 60/0.03 | 0 / 400 / 0 / – | 116.6 [108.7–117.0] | 4.45 | 0.05 | – | k_hold_kinematic 0.14016 [0.12578, 0.15454]; c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | G3-F (U) @ 30/0.02; Kill, characters (U) @ 30/0.02; G3-F (U) @ 60/0.03; Kill, characters (U) @ 60/0.03 | NO VERDICT; NO VERDICT; GO; NOT KILLED | 3 |
| fantasy | additivity [C] | puppets-steered (players 100, players_E 100, rates 60/0.03) | character | 60/0.03 | 100 / 100 / 0 / – | 325.7 [319.8–332.4] | 7.55 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [E] | puppets-steered (players 100, players_E 100, rates 60/0.03) | character | 60/0.03 | 100 / 100 / 0 / – | 322.8 [318.8–324.0] | 7.30 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [C] | puppets-steered (players 200, players_E 200, rates 60/0.03) | character | 60/0.03 | 200 / 200 / 0 / – | 664.4 [660.0–676.7] | 16.30 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [E] | puppets-steered (players 200, players_E 200, rates 60/0.03) | character | 60/0.03 | 200 / 200 / 0 / – | 658.1 [656.8–685.6] | 15.80 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [C] | puppets-kinematic (players 200, players_E 200, rates 60/0.03) | character | 60/0.03 | 200 / 200 / 0 / – | 213.7 [213.3–225.5] | 5.20 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [E] | puppets-kinematic (players 200, players_E 200, rates 60/0.03) | character | 60/0.03 | 200 / 200 / 0 / – | 215.1 [211.5–230.3] | 5.15 | 0.05 | – | – | – | – | 3 |
| fantasy | additivity [C] | puppets-kinematic (players 400, players_E 400, rates 60/0.03) | character | 60/0.03 | 400 / 400 / 0 / – | 444.7 [438.5–450.9] | 12.68 | 0.05 | – | – | – | – | 2 |
| fantasy | additivity [E] | puppets-kinematic (players 400, players_E 400, rates 60/0.03) | character | 60/0.03 | 400 / 400 / 0 / – | 447.4 [436.5–458.3] | 11.23 | 0.05 | – | – | – | – | 2 |
| fantasy | solo [C] | owners-steered (players 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 269.8 [263.7–271.0] | 5.15 | 0.05 | – | – | – | – | 3 |
| fantasy | solo [C] | owners-steered (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 536.1 [531.1–538.1] | 10.20 | 0.05 | – | – | – | – | 3 |
| fantasy | solo [C] | owners-steered (players 250, rates 60/0.03) | character | 60/0.03 | 250 / 0 / 0 / – | 679.3 [661.0–683.5] | 12.90 | 0.05 | – | – | – | – | 3 |
| fantasy | solo [C] | owners-kinematic (players 100, rates 60/0.03) | character | 60/0.03 | 100 / 0 / 0 / – | 79.5 [79.3–80.7] | 1.70 | 0.05 | – | – | – | – | 3 |
| fantasy | solo [C] | owners-kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 131.8 [125.7–135.1] | 2.80 | 0.05 | – | – | – | – | 3 |
| fantasy | solo [C] | owners-kinematic (players 400, rates 60/0.03) | character | 60/0.03 | 400 / 0 / 0 / – | 241.5 [237.6–251.1] | 5.45 | 0.05 | – | – | – | – | 3 |
| fantasy | animation-off [C] | puppets-steered (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 153.4 [149.5–160.8] | 3.60 | 0.05 | – | – | – | – | 3 |
| fantasy | animation-off [E] | puppets-steered (players 200, rates 60/0.03) | character | 60/0.03 | 0 / 200 / 0 / – | 489.4 [475.4–504.6] | 12.20 | 0.05 | – | – | – | – | 3 |
| fantasy | animation-off [C] | puppets-kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 149.7 [140.9–153.9] | 3.95 | 0.05 | – | – | – | – | 3 |
| fantasy | animation-off [E] | puppets-kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 0 / 200 / 0 / – | 92.7 [83.8–98.5] | 2.85 | 0.05 | – | – | – | – | 3 |
| neutral | instrument [C] | plain | character | – | 2 / 2 / 0 / – | 31.4 [31.4–31.4] | 0.85 | 0.00 | – | – | – | – | 1 |
| neutral | instrument [C] | r60 (rates 60/0.03) | character | 60/0.03 | 1 / 3 / 0 / – | 30.0 [30.0–30.0] | 0.75 | 0.05 | – | – | – | – | 1 |
| neutral | instrument [C] | r60burn (rates 60/0.03) | character | 60/0.03 | 2 / 2 / 0 / – | 71.4 [71.4–71.4] | 2.80 | 0.05 | – | – | – | – | 1 |
| neutral | instrument [C] | r30 (rates 30/0.02) | character | 30/0.02 | 3 / 1 / 0 / – | 18.2 [18.2–18.2] | 0.95 | 0.05 | – | – | – | – | 1 |
| neutral | instrument [C] | r30burn (rates 30/0.02) | character | 30/0.02 | 3 / 1 / 0 / – | 60.2 [60.2–60.2] | 2.95 | 0.05 | – | – | – | – | 1 |
| neutral | instrument [C] | overhead-A (rates 60/0.03) | character | 60/0.03 | 1 / 3 / 0 / – | 29.5 [28.8–30.3] | 0.80 | 0.00 | – | – | – | – | 5 |
| neutral | instrument [C] | overhead-B (rates 60/0.03) | character | 60/0.03 | 1 / 3 / 0 / – | 30.4 [30.1–31.0] | 0.75 | 0.05 | – | – | – | – | 5 |
| neutral | puppets [C] | steered (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 176.6 [176.6–176.6] | 3.65 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [E] | steered (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 0 / 60 / 0 / – | 144.1 [144.1–144.1] | 3.40 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [C] | kin (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 176.0 [176.0–176.0] | 3.40 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [E] | kin (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 0 / 60 / 0 / – | 43.9 [43.9–43.9] | 1.30 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [C] | kin_kwalk (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 68.7 [68.7–68.7] | 1.60 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [E] | kin_kwalk (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 0 / 60 / 0 / – | 43.1 [43.1–43.1] | 1.20 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [C] | kin_inert (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 88.3 [88.3–88.3] | 1.85 | 0.05 | – | – | – | – | 1 |
| neutral | puppets [E] | kin_inert (players 60, area 80,0,30, rates 60/0.03) | character | 60/0.03 | 0 / 60 / 0 / – | 37.9 [37.9–37.9] | 1.15 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | r30s03 (players 0, rates 30/0.03) | character | 30/0.03 | 0 / 0 / 0 / – | 17.1 [17.1–17.1] | 0.75 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | r60s02 (players 0, rates 60/0.02) | character | 60/0.02 | 0 / 0 / 0 / – | 26.8 [26.8–26.8] | 0.60 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | uncap (players 0, rates 60/0.03) | character | 60/0.03 uncapped | 0 / 0 / 0 / – | 981.4 [981.4–981.4] | 0.10 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | nofight (players 60, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 163.3 [163.3–163.3] | 3.15 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | noanim (players 60, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 165.8 [165.8–165.8] | 3.30 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [C] | band300 (players 60, rates 60/0.03) | character | 60/0.03 | 60 / 0 / 0 / – | 64.9 [64.9–64.9] | 1.65 | 0.05 | – | – | – | – | 1 |
| neutral | first-use [E] | band300 (players 60, rates 60/0.03) | character | 60/0.03 | 0 / 60 / 0 / – | 46.9 [46.9–46.9] | 1.20 | 0.05 | – | – | – | – | 1 |
| neutral | canary [C] | baseline (players 200, rates 30/0.02) | character | 30/0.02 | 200 / 0 / 0 / – | 104.0 [103.8–104.2] | 4.20 | 0.05 | – | 6 drifted, 0 points not re-measured | Canary drift | PASS | 2 |
| neutral | canary [C] | check (players 200, rates 30/0.02) | character | 30/0.02 | 200 / 0 / 0 / – | 107.3 [103.4–123.9] | 4.65 | 0.05 | – | 6 drifted, 0 points not re-measured | Canary drift | PASS | 17 |
| neutral | cell [C] | idle (players 0, rates 30/0.02) | character | 30/0.02 | 0 / 0 / 0 / – | 17.0 [16.9–17.0] | 0.75 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 2 |
| neutral | cell [C] | idle (players 0, rates 60/0.03) | character | 60/0.03 | 0 / 0 / 0 / – | 23.8 [23.7–24.0] | 0.60 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 2 |
| neutral | cell [C] | players-150 (players 150, rates 30/0.02) | character | 30/0.02 | 150 / 0 / 0 / – | 224.4 [222.5–226.3] | 8.32 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 2 |
| neutral | cell [C] | players-150 (players 150, rates 60/0.03) | character | 60/0.03 | 150 / 0 / 0 / – | 393.4 [388.9–398.0] | 7.53 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 2 |
| neutral | linked [C] | idle (players 0, rates 30/0.02) | character | 30/0.02 | 0 / 0 / 0 / – | 15.9 [15.9–15.9] | 0.75 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 2 |
| neutral | linked [C] | idle (players 0, rates 60/0.03) | character | 60/0.03 | 0 / 0 / 0 / – | 23.2 [21.8–24.4] | 0.65 | 0.05 | – | idle C 17.0 ms/s; idle C 23.8 ms/s | Floor @ 30/0.02; Floor @ 60/0.03 | PASS; PASS | 3 |
| neutral | K [C] | idle (players 0, rates 30/0.03) | character | 30/0.03 | 0 / 0 / 0 / – | 16.1 [15.7–16.2] | 0.70 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | idle (players 0, rates 30/0.02) | character | 30/0.02 | 0 / 0 / 0 / – | 17.5 [16.4–18.0] | 0.75 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | idle (players 0, rates 60/0.03) | character | 60/0.03 | 0 / 0 / 0 / – | 23.9 [23.7–26.7] | 0.65 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | idle (players 0, rates 60/0.02) | character | 60/0.02 | 0 / 0 / 0 / – | 26.4 [25.6–27.6] | 0.60 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | steered (players 200, rates 30/0.03) | character | 30/0.03 | 200 / 0 / 0 / – | 292.2 [291.7–295.3] | 10.85 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | steered (players 200, rates 30/0.02) | character | 30/0.02 | 200 / 0 / 0 / – | 309.1 [303.3–309.1] | 11.40 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | steered (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 532.5 [526.0–559.9] | 10.00 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | steered (players 200, rates 60/0.02) | character | 60/0.02 | 200 / 0 / 0 / – | 577.6 [551.7–578.6] | 11.55 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | kinematic (players 200, rates 30/0.03) | character | 30/0.03 | 200 / 0 / 0 / – | 95.3 [84.5–95.5] | 4.20 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | kinematic (players 200, rates 30/0.02) | character | 30/0.02 | 200 / 0 / 0 / – | 107.0 [93.7–109.4] | 4.85 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | kinematic (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 147.0 [133.2–156.0] | 3.25 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | kinematic (players 200, rates 60/0.02) | character | 60/0.02 | 200 / 0 / 0 / – | 158.8 [154.0–162.4] | 3.40 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | inert (players 200, rates 30/0.03) | character | 30/0.03 | 200 / 0 / 0 / – | 125.1 [120.4–127.1] | 4.90 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | inert (players 200, rates 30/0.02) | character | 30/0.02 | 200 / 0 / 0 / – | 122.5 [122.4–123.7] | 4.75 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | inert (players 200, rates 60/0.03) | character | 60/0.03 | 200 / 0 / 0 / – | 224.4 [222.2–225.9] | 4.35 | 0.05 | – | – | – | – | 3 |
| neutral | K [C] | inert (players 200, rates 60/0.02) | character | 60/0.02 | 200 / 0 / 0 / – | 233.8 [220.9–239.3] | 4.60 | 0.05 | – | – | – | – | 3 |

## Runs not accepted

| Family | Case | Arm | Point | Repeat | Why |
|---|---|---|---|---|---|
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | C: 1 of 11 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (23:11:12: 99 of 100 ticks, 1 dropped) |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 1 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | saturated (span) on C |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 2 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | saturated (span) on C |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "off", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | grid | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | saturated (span) on C |
| abgeo | b9-main | halves | {"band": 0.0, "load": "spread", "monsters": "all", "players": 250, "puppets": "kinematic", "rates": "60/0.03"} | 3 | drift: the canary at 2026-10-01T00:21:06 read 122.9 ms/s on C against its build's baseline 104.0 ms/s (18.2%, above 5%); drift |
| abgeo | b9-main | coarse | {"band": 0.0, "load": "spread", "monsters": "all", "players": 150, "puppets": "kinematic", "rates": "60/0.03"} | 1 | C: 1 of 11 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (17:59:45: 99 of 100 ticks, 1 dropped) |
| density | bridge-0.53 | uncapped | {"players": 500, "rates": "60/0.03", "side": 200, "uncapped": "C"} | 1 | C: 3 of 11 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (21:57:06: 100 of 101 ticks, 1 dropped; 21:57:16: 100 of 101 ticks, 1 dropped; 21:57:36: 100 of 101 ticks, 1 dropped) |
| density | bridge-0.53 | uncapped | {"players": 500, "rates": "60/0.03", "side": 200, "uncapped": "C"} | 2 | C: 1 of 11 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (22:10:24: 100 of 101 ticks, 1 dropped) |
| presence | additivity | puppets-kinematic | {"players": 400, "players_E": 400, "rates": "60/0.03"} | 2 | C: 1 of 11 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (00:35:14: 90 of 100 ticks, 10 dropped); E: 1 of 12 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped (00:35:11: 90 of 100 ticks, 10 dropped) |

## Fits

| Fit | §5 name | Regressor | Value [95 % CI] | t | n | Method | Runs as groups |
|---|---|---|---|---|---|---|---|
| rates idle | rate_F | per_frame | 0.30058 [0.2671, 0.33406] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates idle | rate_Ph | per_step | 0.091517 [-0.043934, 0.22697] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates idle | rate_C | per_second | 3.8149 [-1.6035, 9.2333] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates idle | rate_S | other_per_second | 3.4542 [-1.9662, 8.8746] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates idle | rate_I | interaction | 0.47854 [-5.1299, 6.087] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered | rate_F | per_frame | 8.4822 [7.5263, 9.4381] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered | rate_Ph | per_step | 1.3297 [-0.28917, 2.9486] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered | rate_C | per_second | -9.7372 [-74.582, 55.108] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered | rate_S | other_per_second | -10.297 [-75.138, 54.544] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered | rate_I | interaction | 16.048 [-25.958, 58.055] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates kinematic | rate_F | per_frame | 1.812 [1.7413, 1.8827] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates kinematic | rate_Ph | per_step | 0.73939 [0.22836, 1.2504] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates kinematic | rate_C | per_second | 12.396 [-24.017, 48.809] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates kinematic | rate_S | other_per_second | 11.863 [-24.541, 48.267] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates kinematic | rate_I | interaction | 1.3108 [-27.677, 30.299] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates inert | rate_F | per_frame | 3.4744 [3.171, 3.7779] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates inert | rate_Ph | per_step | 0.17467 [-0.17824, 0.52759] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates inert | rate_C | per_second | 12.026 [-7.3573, 31.41] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates inert | rate_S | other_per_second | 11.407 [-7.9781, 30.793] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates inert | rate_I | interaction | 8.4534 [-20.32, 37.226] | 4.303 | 12 | ols + jackknife over repeats | – |
| rates steered per entity | rate_kind_F | kind_per_frame | 8.1816 [7.214, 9.1492] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates steered per entity | rate_kind_Ph | kind_per_step | 1.2382 [-0.41495, 2.8913] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates steered per entity | rate_kind_C | kind_per_second | -13.552 [-77.429, 50.325] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates steered per entity | rate_entity_F | entity_per_frame | 0.040908 [0.03607, 0.045746] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates steered per entity | rate_entity_Ph | entity_per_step | 0.0061909 [-0.0020748, 0.014457] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates steered per entity | rate_entity_C | entity_per_second | -0.067761 [-0.38715, 0.25163] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_kind_F | kind_per_frame | 1.5114 [1.425, 1.5979] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_kind_Ph | kind_per_step | 0.64787 [0.12178, 1.174] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_kind_C | kind_per_second | 8.5811 [-30.529, 47.691] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_entity_F | entity_per_frame | 0.0075572 [0.007125, 0.0079893] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_entity_Ph | entity_per_step | 0.0032394 [0.0006089, 0.0058698] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates kinematic per entity | rate_entity_C | entity_per_second | 0.042906 [-0.15265, 0.23846] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_kind_F | kind_per_frame | 3.1739 [2.8773, 3.4705] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_kind_Ph | kind_per_step | 0.083158 [-0.40435, 0.57066] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_kind_C | kind_per_second | 8.2113 [-16.588, 33.011] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_entity_F | entity_per_frame | 0.015869 [0.014386, 0.017352] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_entity_Ph | entity_per_step | 0.00041579 [-0.0020217, 0.0028533] | 4.303 | 24 | ols + jackknife over repeats | – |
| rates inert per entity | rate_entity_C | entity_per_second | 0.041057 [-0.082942, 0.16506] | 4.303 | 24 | ols + jackknife over repeats | – |
| density @ 60/0.03 | c0 | c0 | 13.666 [-24.357, 51.689] | 4.303 | 30 | ols + jackknife over repeats | – |
| density @ 60/0.03 | a_steered | O_steered | 2.5918 [2.479, 2.7045] | 4.303 | 30 | ols + jackknife over repeats | – |
| density @ 60/0.03 | q | Q | -0.016101 [-0.032222, 1.9288e-05] | 4.303 | 30 | ols + jackknife over repeats | – |
| floor @ 30/0.02 | c0 | c0 | 16.982 [16.289, 17.675] | 12.706 | 4 | ols + jackknife over repeats | runs as groups (assumes no between-repeat drift): 16.982 [16.769, 17.194] |
| floor @ 30/0.02 | lambda | links | -0.13906 [-0.23472, -0.043407] | 12.706 | 4 | ols + jackknife over repeats | runs as groups (assumes no between-repeat drift): -0.13906 [-0.16578, -0.11235] |
| floor @ 60/0.03 | c0 | c0 | 23.832 [22.157, 25.507] | 12.706 | 5 | ols + jackknife over repeats | runs as groups (assumes no between-repeat drift): 23.832 [23.369, 24.295] |
| floor @ 60/0.03 | lambda | links | -0.087899 [-1.383, 1.2072] | 12.706 | 5 | ols + jackknife over repeats | runs as groups (assumes no between-repeat drift): -0.087899 [-0.3833, 0.2075] |
| presence @ 60/0.03 | c0 | c0 | 15.091 [3.9907, 26.192] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | lambda | links | 0.55504 [-9.9605, 11.071] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | a_steered | O_steered | 2.615 [2.4971, 2.7329] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | a_kinematic | O_kinematic | 0.57598 [0.49593, 0.65603] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | e_steered | U_steered | 2.3859 [2.2711, 2.5007] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | e_kinematic | U_kinematic | 0.36651 [0.31224, 0.42078] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | g_player | G_player | 0.09474 [0.050333, 0.13915] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | a_inert | O_inert | 1.239 [1.1812, 1.2968] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | e_inert | U_inert | 0.24993 [0.21598, 0.28388] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | k_hold_steered | k_hold_steered | 0.91239 [0.90878, 0.91601] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | k_band_steered | k_band_steered | 0.94862 [0.93416, 0.96309] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | k_hold_kinematic | k_hold_kinematic | 0.14016 [0.12578, 0.15454] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | k_band_kinematic | k_band_kinematic | 0.17639 [0.16332, 0.18946] | 4.303 | 78 | ols + jackknife over repeats | – |
| presence @ 60/0.03 | k_hold_inert | k_hold_inert | 0.095577 [0.086934, 0.10422] | 4.303 | 78 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | c0 | c0 | 29.079 [27.062, 31.096] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | lambda | links | 2.9139 [1.3976, 4.4302] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | a_steered | O_steered | 2.6152 [2.5527, 2.6776] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | a_kinematic | O_kinematic | 0.56174 [0.5047, 0.61878] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | a_inert | O_inert | 1.2434 [1.2011, 1.2857] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | a_monster | O_monster | 0.33224 [0.27762, 0.38686] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | e_steered | U_steered | 2.3108 [2.2027, 2.4188] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | e_kinematic | U_kinematic | 0.33562 [0.31155, 0.3597] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | e_inert | U_inert | 0.19544 [0.16649, 0.22439] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | e_monster_steered | U_monster_steered | 0.53786 [0.45451, 0.62121] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | e_monster_kinematic | U_monster_kinematic | 0.41323 [0.30565, 0.52082] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | g_player | G_player | 0.11852 [0.076532, 0.1605] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | q | Q | -0.033294 [-0.039497, -0.027091] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | k_hold_steered | k_hold_steered | 0.8836 [0.8608, 0.9064] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | k_band_steered | k_band_steered | 0.92892 [0.92167, 0.93616] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | k_hold_kinematic | k_hold_kinematic | 0.12834 [0.12206, 0.13461] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | k_band_kinematic | k_band_kinematic | 0.17366 [0.16291, 0.1844] | 4.303 | 366 | ols + jackknife over repeats | – |
| refit @ 60/0.03 | k_hold_inert | k_hold_inert | 0.074735 [0.065381, 0.084088] | 4.303 | 366 | ols + jackknife over repeats | – |

- density @ 60/0.03: n2_form chose qQ (AIC 155.04 against qN2 160.50); q -0.016101 [-0.032222, 1.9288e-05] includes 0
- floor @ 30/0.02: c0 17.0 ms/s = 0.57 ms/frame (0.71: 0.60 ms/frame, information); λ -0.13906 [-0.23472, -0.043407] ms/s per link
- floor @ 60/0.03: c0 23.8 ms/s = 0.40 ms/frame (0.71: 0.60 ms/frame, information); λ -0.087899 [-1.383, 1.2072] ms/s per link
- presence @ 60/0.03: q left out: density's q interval -0.016101 [-0.032222, 1.9288e-05] includes 0
- additivity @ 60/0.03 puppets-steered {'players': 100, 'players_E': 100, 'rates': '60/0.03'} C: measured 326.0, predicted 321.3, residual 4.7 [-9.0, 18.3] ms/s (3 runs)
- additivity @ 60/0.03 puppets-steered {'players': 100, 'players_E': 100, 'rates': '60/0.03'} E: measured 321.9, predicted 321.3, residual 0.6 [-13.1, 14.2] ms/s (3 runs)
- additivity @ 60/0.03 puppets-steered {'players': 200, 'players_E': 200, 'rates': '60/0.03'} C: measured 667.0, predicted 627.0, residual 40.1 [10.9, 69.2] ms/s (3 runs)
- additivity @ 60/0.03 puppets-steered {'players': 200, 'players_E': 200, 'rates': '60/0.03'} E: measured 666.8, predicted 627.0, residual 39.9 [10.7, 69.0] ms/s (3 runs)
- additivity @ 60/0.03 puppets-kinematic {'players': 200, 'players_E': 200, 'rates': '60/0.03'} C: measured 217.5, predicted 223.1, residual -5.6 [-22.8, 11.5] ms/s (3 runs)
- additivity @ 60/0.03 puppets-kinematic {'players': 200, 'players_E': 200, 'rates': '60/0.03'} E: measured 218.9, predicted 223.1, residual -4.1 [-21.3, 13.0] ms/s (3 runs)
- additivity @ 60/0.03 puppets-kinematic {'players': 400, 'players_E': 400, 'rates': '60/0.03'} C: measured 444.7, predicted 430.5, residual 14.1 [-22.1, 50.3] ms/s (2 runs)
- additivity @ 60/0.03 puppets-kinematic {'players': 400, 'players_E': 400, 'rates': '60/0.03'} E: measured 447.4, predicted 430.5, residual 16.9 [-19.3, 53.1] ms/s (2 runs)
- animation off - on @ 60/0.03 puppets-steered C: -3.5604 [-7.2043, 0.083634] ms/s
- animation off - on @ 60/0.03 puppets-steered E: -2.4926 [-12.625, 7.6393] ms/s
- animation off - on @ 60/0.03 puppets-kinematic C: 0.85399 [-5.2462, 6.9542] ms/s
- animation off - on @ 60/0.03 puppets-kinematic E: 2.5277 [-1.6637, 6.7191] ms/s

## Gates

| Gate | Game | Case | Verdict | Key metric | Rule | Notes |
|---|---|---|---|---|---|---|
| G3-F (U) @ 30/0.02 | fantasy | main | NO VERDICT | – | k_hold_kinematic = e_kinematic / a_steered ≤ 0.5 (a ratio: no ms/s or ms/frame form) | needs p0load presence --rates 30/0.02; the O half (observed puppets) needs 0b (step 5) |
| Kill, characters (U) @ 30/0.02 | fantasy | main | NO VERDICT | – | c0 + λ + 500·e_kinematic ≥ 600 ms/s = 20.00 ms/frame at 30 fps kills | needs p0load presence --rates 30/0.02 |
| G3-F (U) @ 60/0.03 | fantasy | main | GO | k_hold_kinematic 0.14016 [0.12578, 0.15454] | k_hold_kinematic = e_kinematic / a_steered ≤ 0.5 (a ratio: no ms/s or ms/frame form) | the O half (observed puppets) needs 0b (step 5) |
| Kill, characters (U) @ 60/0.03 | fantasy | main | NOT KILLED | c0 + λ + 500·e_kin 198.9 [173.87, 223.93] ms/s | c0 + λ + 500·e_kinematic ≥ 600 ms/s = 10.00 ms/frame at 60 fps kills | – |
| N² @ 60/0.03 | fantasy | attribution | NO VERDICT | – | the best removal arm takes away ≥ 80% of the excess over linear at 200 @ 141.42 m (100 m² per character; a share: no ms/s form); the profiler half is pending | no excess over linear to remove: dense base 516.532 - sparse base 531.159 = -14.627 ms/s; profiler pending |
| G-GEO (a) | fantasy | b9-bands | GO | strips4/halves 0.65 0.62 0.70 | strips4/halves ≤ 0.75 in every repeat at B = 80, kinematic, spread (at least 2 pairs) | – |
| G-GEO (b) | fantasy | – | FAIL | worst \|predicted - measured\| 0.058 | \|predicted - measured mean\| ≤ 0.05 at every predicted point (a ratio: no ms/s form) | 22 predictions made 2026-10-01T05:07:20.552441+00:00 |
| G-GEO (c) | fantasy | b9-bands | PASS | slope 0.358 in 0.32492 [0.064268, 0.58558] | the predicted band slope lies inside the measured slope's 95 % interval, ends included (no ms/s form) | halves B 20: measured monster counts (M); coarse B 20: measured monster counts (M); halves B 40: measured monster counts (M); coarse B 40: measured monster counts (M); halves B 80: measured monster counts (M); coarse B 80: measured monster counts (M); halves B 150: measured monster counts (M); coarse B 150: measured monster counts (M) |
| Floor @ 30/0.02 | neutral | – | PASS | idle C 17.0 ms/s | idle C work ≤ 600 ms/s = 20.00 ms/frame at 30 fps | – |
| Floor @ 60/0.03 | neutral | – | PASS | idle C 23.8 ms/s | idle C work ≤ 600 ms/s = 10.00 ms/frame at 60 fps | – |
| Canary drift | neutral | – | PASS | 6 drifted, 0 points not re-measured | \|w - baseline\|/baseline ≤ 5% on C's Work ticks work ms/s (30/0.02 canary); a drifted canary excludes the runs before it, and the gate fails while an excluded point has no accepted re-run | 17 of 17 check canaries judged, 6 drifted; 32 rows excluded, 0 of their points not re-measured |

Not evaluated in stage 0a: G1-F needs 0b (steps 5-6); G2-F needs 0b (steps 5-6); G3-F (O) needs 0b (step 5); Crossings (B10) needs 0b (step 5); Database needs 0c (step 9); Phantom validity (J) needs 0b (step 5); space gates (G1-S, G2-S, G3-S, kill for ships) needs 0b (step 7)

## Map-size table

k_band_kinematic 0.17366 [0.16291, 0.1844] from refit @ 60/0.03; c0 and λ from floor @ 60/0.03; x 0.0307, link_share -0.0001, V 776.2 ms/s, c0 23.8 ms/s, λ -0.09 ms/s

| Side (m) | B (m) | k | W* (m) | Base (m) | s_min (m) | Max cells | Mode |
|---|---|---|---|---|---|---|---|
| 300 | 50 | 0.174 | 35.8 | 75 | 75 | 1 | Dynamic |
| 300 | 50 | 0.163 | 33.6 | 75 | 75 | 1 | Dynamic |
| 300 | 50 | 0.184 | 38.0 | 150 | 75 | 4 | Dynamic |
| 300 | 80 | 0.174 | 57.3 | 150 | 150 | 1 | Dynamic |
| 300 | 80 | 0.163 | 53.8 | 150 | 150 | 1 | Dynamic |
| 300 | 80 | 0.184 | 60.9 | 150 | 150 | 1 | Dynamic |
| 300 | 120 | 0.174 | 86.0 | 300 | 150 | 4 | Static unless its peak exceeds one process |
| 300 | 120 | 0.163 | 80.6 | 300 | 150 | 4 | Static unless its peak exceeds one process |
| 300 | 120 | 0.184 | 91.3 | 300 | 150 | 4 | Static unless its peak exceeds one process |
| 350 | 50 | 0.174 | 35.8 | 87.5 | 87.5 | 1 | Dynamic |
| 350 | 50 | 0.163 | 33.6 | 87.5 | 87.5 | 1 | Dynamic |
| 350 | 50 | 0.184 | 38.0 | 87.5 | 87.5 | 1 | Dynamic |
| 350 | 80 | 0.174 | 57.3 | 175 | 87.5 | 4 | Dynamic |
| 350 | 80 | 0.163 | 53.8 | 175 | 87.5 | 4 | Dynamic |
| 350 | 80 | 0.184 | 60.9 | 175 | 87.5 | 4 | Dynamic |
| 350 | 120 | 0.174 | 86.0 | 175 | 175 | 1 | Dynamic |
| 350 | 120 | 0.163 | 80.6 | 175 | 175 | 1 | Dynamic |
| 350 | 120 | 0.184 | 91.3 | 350 | 175 | 4 | Static unless its peak exceeds one process |
| 500 | 50 | 0.174 | 35.8 | 125 | 62.5 | 4 | Dynamic |
| 500 | 50 | 0.163 | 33.6 | 125 | 62.5 | 4 | Dynamic |
| 500 | 50 | 0.184 | 38.0 | 125 | 62.5 | 4 | Dynamic |
| 500 | 80 | 0.174 | 57.3 | 125 | 125 | 1 | Dynamic |
| 500 | 80 | 0.163 | 53.8 | 125 | 125 | 1 | Dynamic |
| 500 | 80 | 0.184 | 60.9 | 125 | 125 | 1 | Dynamic |
| 500 | 120 | 0.174 | 86.0 | 250 | 125 | 4 | Dynamic |
| 500 | 120 | 0.163 | 80.6 | 250 | 125 | 4 | Dynamic |
| 500 | 120 | 0.184 | 91.3 | 250 | 125 | 4 | Dynamic |
| 1000 | 50 | 0.174 | 35.8 | 125 | 62.5 | 4 | Dynamic |
| 1000 | 50 | 0.163 | 33.6 | 125 | 62.5 | 4 | Dynamic |
| 1000 | 50 | 0.184 | 38.0 | 125 | 62.5 | 4 | Dynamic |
| 1000 | 80 | 0.174 | 57.3 | 125 | 125 | 1 | Dynamic |
| 1000 | 80 | 0.163 | 53.8 | 125 | 125 | 1 | Dynamic |
| 1000 | 80 | 0.184 | 60.9 | 125 | 125 | 1 | Dynamic |
| 1000 | 120 | 0.174 | 86.0 | 250 | 125 | 4 | Dynamic |
| 1000 | 120 | 0.163 | 80.6 | 250 | 125 | 4 | Dynamic |
| 1000 | 120 | 0.184 | 91.3 | 250 | 125 | 4 | Dynamic |
| 2000 | 50 | 0.174 | 35.8 | 125 | 62.5 | 4 | Dynamic |
| 2000 | 50 | 0.163 | 33.6 | 125 | 62.5 | 4 | Dynamic |
| 2000 | 50 | 0.184 | 38.0 | 125 | 62.5 | 4 | Dynamic |
| 2000 | 80 | 0.174 | 57.3 | 125 | 125 | 1 | Dynamic |
| 2000 | 80 | 0.163 | 53.8 | 125 | 125 | 1 | Dynamic |
| 2000 | 80 | 0.184 | 60.9 | 125 | 125 | 1 | Dynamic |
| 2000 | 120 | 0.174 | 86.0 | 250 | 125 | 4 | Dynamic |
| 2000 | 120 | 0.163 | 80.6 | 250 | 125 | 4 | Dynamic |
| 2000 | 120 | 0.184 | 91.3 | 250 | 125 | 4 | Dynamic |
| 4000 | 50 | 0.174 | 35.8 | 125 | 62.5 | 4 | Dynamic |
| 4000 | 50 | 0.163 | 33.6 | 125 | 62.5 | 4 | Dynamic |
| 4000 | 50 | 0.184 | 38.0 | 125 | 62.5 | 4 | Dynamic |
| 4000 | 80 | 0.174 | 57.3 | 125 | 125 | 1 | Dynamic |
| 4000 | 80 | 0.163 | 53.8 | 125 | 125 | 1 | Dynamic |
| 4000 | 80 | 0.184 | 60.9 | 125 | 125 | 1 | Dynamic |
| 4000 | 120 | 0.174 | 86.0 | 250 | 125 | 4 | Dynamic |
| 4000 | 120 | 0.163 | 80.6 | 250 | 125 | 4 | Dynamic |
| 4000 | 120 | 0.184 | 91.3 | 250 | 125 | 4 | Dynamic |
| 8000 | 50 | 0.174 | 35.8 | 125 | 62.5 | 4 | Dynamic |
| 8000 | 50 | 0.163 | 33.6 | 125 | 62.5 | 4 | Dynamic |
| 8000 | 50 | 0.184 | 38.0 | 125 | 62.5 | 4 | Dynamic |
| 8000 | 80 | 0.174 | 57.3 | 125 | 125 | 1 | Dynamic |
| 8000 | 80 | 0.163 | 53.8 | 125 | 125 | 1 | Dynamic |
| 8000 | 80 | 0.184 | 60.9 | 125 | 125 | 1 | Dynamic |
| 8000 | 120 | 0.174 | 86.0 | 250 | 125 | 4 | Dynamic |
| 8000 | 120 | 0.163 | 80.6 | 250 | 125 | 4 | Dynamic |
| 8000 | 120 | 0.184 | 91.3 | 250 | 125 | 4 | Dynamic |
