# Spike 0 — Coordinate & movement serialization across two cells

> Goal: prove the v0.5 coordinate model — **logical global coords `(meshCellId, localPosition)` with
> per-cell-local Unity frames** — survives the kit's existing movement serialization, and that a
> position translated across a shared cell edge reconstructs correctly. Run **before** Spike 2.
> See [DESIGN.md §6](DESIGN.md#6-world--coordinate-model), [§16](DESIGN.md#16-phase-0-spikes), [Q10](DESIGN.md#18-open-questions).

| | |
|---|---|
| **Status** | Scaffolded — needs in-editor run |
| **Code** | `Assets/ServerMeshing/Spike0/CoordinateSerializationTest.cs` |
| **Needs servers?** | **No** — 0a is a deterministic math test against the kit's real `GridUtility` |
| **Result** | **0a PASS (2026-06-13)** — see [Results](#0-results-2026-06-13--pass) |

---

## 0. Results (2026-06-13) — PASS

Config: `cellSize=128`, `gridSize=15`, `meshCellSize=256`, `B=128`.

- **[1] range fit:** `localHalf=256 < representableHalf=960` → **PASS** (huge margin).
- **[2] identity round-trip:** `maxErr=0`, `clamped=0` over 16,641 samples → **PASS** (no clamping, exact reconstruction).
- **[3] cross-edge translation:** `maxOffsetErr=0`, in range → **PASS** (constant `meshCellSize` offset is exact).
- **[4] quantization (client-facing stream), worst case over band:**

  | mode | x / z | y\* |
  |---|---|---|
  | 6 (nearest) | ~1.9 mm | ~1.9 mm |
  | 5 | ~7.6 mm | ~31 mm |
  | 4 | ~61 mm | ~124 mm |
  | 3 (farthest) | ~121 mm | bucketed\* |

  \* The `y` numbers reflect the **sampled height (1.5 m)**, not a worst case. The 4-bit vertical at
  mode 3 has ~8.5 m buckets (so `1.5` → `0`); vertical precision is coarse for *distant* entities **by
  design**. Ghosts near a player use high-precision modes.

**Conclusion:** the v0.5 coordinate model is validated. **Resolves [Q10]; resolves the range/precision
portion of [Q1]** (max-speed × AOI-cadence sizing of `B` is still open).

### Design refinement surfaced (now in DESIGN.md §6)

The movement quantization is **lossy and client-facing**, so **authoritative state must never
round-trip through it**:

- **CellMeshLink S2S transfers** (handoff state, ghost authoritative position) carry **full-precision**
  coordinates — `(meshCellId, float3 localPosition)` or a global double — *not* the compressed
  movement encoding. Otherwise repeated handoffs / ghost round-trips accumulate drift (cm horizontally,
  up to metres vertically at distance).
- Clients keep receiving the normal compressed stream; ghosts use it like any other entity. Only
  server-authoritative position uses full precision.

---

## 1. Verified serialization facts (from the kit)

The kit serializes entity movement as a **byte `cellId` + 2-bit compression mode + quantized X/Z/Y**
(`EntityMovementJob.cs:40-86`, `NetDataReaderExtension.GetQuantizedVector3` `:69-113`,
`DefaultGridManagerComponent.cs`):

- `GetCellLocalPosition(world, cellSize, gridSize, out cellId)` → `(cellId, local∈[0,cellSize))`.
  **It clamps `cellX/cellZ` to `[0, gridSize-1]`** (`:154-155`) — out-of-range world positions are
  silently snapped to the edge sub-cell, corrupting the position.
- **Representable world range per axis = `±(gridSize·cellSize)/2`.** At defaults (`cellSize=128`,
  `gridSize=15`) that is **±960 m**.
- Per-axis quantization bit depths by compression mode (chosen by distance to the receiving player):

  | Mode | bx | by | bz | When |
  |---|---|---|---|---|
  | 3 | 10 | 4 | 10 | farthest |
  | 4 | 11 | 10 | 11 | far |
  | 5 | 14 | 12 | 14 | near |
  | 6 | 16 | 16 | 16 | nearest |

- Reconstruction: `Dequantize(v, cellSize, bits) = v/(2^bits − 1) · cellSize`. So worst-case step
  (error bound) per axis = `cellSize/(2^bits − 1)`. At `cellSize=128`:

  | bits | step (error bound) |
  |---|---|
  | 16 | ~1.95 mm |
  | 14 | ~7.8 mm |
  | 12 | ~31 mm |
  | 11 | ~62 mm |
  | 10 | ~125 mm |
  | 4 | **~8.53 m** (vertical, far only) |

> The `cellId` here is the **movement-grid sub-cell**, *not* the mesh cell — keep them distinct
> (Glossary). The mesh cell is the *server*; the movement grid sub-partitions each server's local
> frame for compression.

---

## 2. What this implies for the mesh coordinate model

- Each mesh-cell server's **local** world (mesh cell + border band, centered at local origin) must fit
  inside `±960 m`: require **`meshCellSize/2 + B < gridSize·cellSize/2`**. At `meshCellSize=256`,
  `B=128` → `256 < 960` ✓ (huge margin). The spike fails loudly if a chosen config would clamp.
- Cross-edge translation is a **constant per-edge offset**: with each cell centered at its own local
  origin, a +x neighbor's local frame is offset by `meshCellSize` in x, so `localA = localB +
  meshCellSize` for points near the shared edge. Ghosts only live in the band, so translated
  positions stay in-range.
- High-precision (mode 6) is ~2 mm — ample. The coarse `mode 3` vertical (~8.5 m) only applies to
  distant entities; ghosts near a player use high-precision modes.

---

## 3. What the scaffold tests (0a — math, runnable now)

`CoordinateSerializationTest` sweeps positions across one cell + its border band and checks:

1. **No clamping** — every band position maps to a non-edge-clamped `cellId`, and
   `reconstruct(GetCellLocalPosition(p)) == p` exactly (identity round-trip).
2. **Cross-edge translation** — a point expressed in neighbor B's local frame, translated into A's
   frame by the constant offset, stays in range and round-trips back to the same global point.
3. **Quantization precision** — for all four real bit-depth modes, `Dequantize(Quantize(local))`
   error stays within the step bounds above across the whole band (reports max error per mode/axis).

It logs a single `[Spike0]` PASS/FAIL report with the worst-case numbers.

### 0b — on-wire confirmation (follow-up, after 0a passes)

Stand up two **same-axis-adjacent** cell servers (local frames) + a client, walk an entity across the
shared edge, and confirm the client renders a **continuous** position with no jump at the seam. This
exercises the kit's real `WriteEntityServerState` → `GetQuantizedVector3` end to end (including its
byte-packing), which 0a deliberately does not replicate. Defer until 0a is green.

---

## 4. Run (0a)

1. Add `CoordinateSerializationTest` to any GameObject in any scene (no networking needed).
2. Set `cellSize`/`gridSize` to match your `DefaultGridManagerComponent`, and `meshCellSize`/`borderBand`
   to your intended mesh config.
3. Right-click the component → **Run Spike 0** (or enable `runOnStart` and press Play).
4. Read the `[Spike0]` console report.

**Pass:** clamp check PASS, identity error ≈ 0, translation PASS, and per-mode quantization errors
within the §1 bounds → the v0.5 coordinate model is sound for this config; proceed to Spike 2.

**Fail:** any clamping, non-zero identity error, or translation mismatch → the chosen
`meshCellSize/B/cellSize/gridSize` don't fit; shrink the mesh cell/band or enlarge the movement grid,
or rethink the local-frame anchoring.

---

## 5. Outcomes to record back in DESIGN.md

- The config that passes (`cellSize`, `gridSize`, `meshCellSize`, `B`) → fills [Q1](DESIGN.md#18-open-questions)/[Q10](DESIGN.md#18-open-questions).
- Worst-case quantization error at the band edge for the modes you expect ghosts to use.
- Whether 0b (on-wire) is still needed or 0a is sufficient confidence to proceed.
