# Server Meshing — Spike 0 (coordinate & movement serialization)

Deterministic test proving the v0.5 coordinate model — **logical global coords `(meshCellId,
localPosition)` with per-cell-local Unity frames** — survives the kit's real movement serialization
(byte `cellId` + quantized local position), and that cross-edge translation round-trips.

**Extension-only, no networking, no servers.** Run it before Spike 2.

- `CoordinateSerializationTest.cs` — attach to any GameObject, set params, right-click → **Run Spike 0**
  (or enable `runOnStart`). Reads the `[Spike0]` console report (clamp fit, identity round-trip,
  cross-edge translation, per-mode quantization error).

**Runbook, verified serialization facts, and pass/fail:**
[`Docs/ServerMeshing/SPIKE0.md`](../../../Docs/ServerMeshing/SPIKE0.md).

> Uses the kit's real `GridUtility`/grid math, so 0a should compile and run as-is. The full on-wire
> path (0b) — two adjacent cell servers + a client walking the seam — is a follow-up once 0a is green.
