// Server Meshing add-on — Spike 0 (coordinate & movement serialization across two cells).
// See Docs/ServerMeshing/SPIKE0.md and Docs/ServerMeshing/DESIGN.md §6.
//
// EXTENSION-ONLY, no networking. A deterministic test against the kit's REAL movement-grid math
// (GridUtility, the same DefaultGridManagerComponent grid the wire uses). Attach to any GameObject,
// set the params to your intended config, then right-click -> "Run Spike 0" (or enable runOnStart).
//
// It answers the v0.5 coordinate question: does the logical-global / per-cell-local model survive the
// kit's byte-cellId + quantized-local serialization, and does cross-edge translation round-trip?

using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class CoordinateSerializationTest : MonoBehaviour
    {
        [Header("Movement grid (match your DefaultGridManagerComponent)")]
        public ushort cellSize = 128;
        [Range(1, 15)] public ushort gridSize = 15;

        [Header("Mesh layout")]
        [Tooltip("Edge length of one mesh cell (metres). Each cell server is centered at its own local origin.")]
        public float meshCellSize = 256f;
        [Tooltip("Border band width B (metres) — the overlap into the neighbour where ghosts live.")]
        public float borderBand = 128f;

        [Header("Test")]
        [Tooltip("Sample step (metres) for the position sweeps.")]
        public float sampleStep = 4f;
        [Tooltip("Max acceptable identity (no-quantization) round-trip error — should be ~0.")]
        public float identityTolerance = 0.001f;
        public bool runOnStart = false;

        // Per-mode bit depths, copied verbatim from EntityMovementJob.cs / NetDataReaderExtension.cs.
        private static readonly int[] s_modes = { 3, 4, 5, 6 };
        private static readonly int[] s_bx = { 10, 11, 14, 16 };
        private static readonly int[] s_by = { 4, 10, 12, 16 };
        private static readonly int[] s_bz = { 10, 11, 14, 16 };

        private void Start()
        {
            if (runOnStart)
                RunSpike0();
        }

        [ContextMenu("Run Spike 0")]
        public void RunSpike0()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ [Spike0] Coordinate & movement serialization ================");
            sb.AppendLine($"cellSize={cellSize}  gridSize={gridSize}  meshCellSize={meshCellSize}  borderBand(B)={borderBand}");

            bool pass = true;

            // --- Check 1: the local world (cell + band) fits the representable range, no clamping. ---
            float representableHalf = gridSize * cellSize * 0.5f;   // GetCellLocalPosition clamps beyond +/- this.
            float localHalf = meshCellSize * 0.5f + borderBand;     // furthest a ghost can sit from local origin.
            bool fits = localHalf < representableHalf;
            pass &= fits;
            sb.AppendLine($"[1] range fit: localHalf={localHalf:0.##}  <  representableHalf={representableHalf:0.##}  -> {(fits ? "PASS" : "FAIL (would clamp)")}");

            // --- Check 2: identity round-trip (GetCellLocalPosition -> reconstruct) across cell+band. ---
            float maxIdentityErr = 0f;
            int clampedSamples = 0;
            int samples = 0;
            for (float x = -localHalf; x <= localHalf; x += sampleStep)
            {
                for (float z = -localHalf; z <= localHalf; z += sampleStep)
                {
                    samples++;
                    float3 p = new float3(x, 1.5f, z);
                    float3 local = GridUtility.GetCellLocalPosition(p, cellSize, gridSize, out byte cellId);
                    float3 recon = Reconstruct(cellId, local);
                    float err = math.length(p - recon);
                    if (err > maxIdentityErr) maxIdentityErr = err;
                    if (err > identityTolerance) clampedSamples++;
                }
            }
            bool identityOk = maxIdentityErr <= identityTolerance;
            pass &= identityOk;
            sb.AppendLine($"[2] identity round-trip over {samples} samples: maxErr={maxIdentityErr:0.######} m  clamped={clampedSamples}  -> {(identityOk ? "PASS" : "FAIL")}");

            // --- Check 3: cross-edge translation (A frame <-> B frame, B is the +x neighbour). ---
            // Global edge at x=0; A center at x=-meshCellSize/2, B center at x=+meshCellSize/2.
            // pA = global + meshCellSize/2 ; pB = global - meshCellSize/2 ; so pA - pB = meshCellSize (constant).
            float maxOffsetErr = 0f;
            bool translationInRange = true;
            for (float gx = 0f; gx <= borderBand; gx += sampleStep)              // B-owned band (just across the edge)
            {
                for (float gz = -meshCellSize * 0.5f; gz <= meshCellSize * 0.5f; gz += sampleStep)
                {
                    float3 pA = new float3(gx + meshCellSize * 0.5f, 1.5f, gz);  // in A's local frame
                    float3 pB = new float3(gx - meshCellSize * 0.5f, 1.5f, gz);  // in B's local frame
                    maxOffsetErr = math.max(maxOffsetErr, math.abs((pA.x - pB.x) - meshCellSize));

                    // Both must stay representable and non-clamping in their own frames.
                    translationInRange &= NoClamp(pA) && NoClamp(pB);
                }
            }
            bool translationOk = maxOffsetErr <= identityTolerance && translationInRange;
            pass &= translationOk;
            sb.AppendLine($"[3] cross-edge translation: maxOffsetErr={maxOffsetErr:0.######}  inRange={translationInRange}  -> {(translationOk ? "PASS" : "FAIL")}");

            // --- Check 4: quantization precision per compression mode across cell+band. ---
            sb.AppendLine("[4] quantization error bounds (Dequantize(Quantize(local)) - local), worst case over band:");
            for (int m = 0; m < s_modes.Length; m++)
            {
                int bx = s_bx[m], by = s_by[m], bz = s_bz[m];
                float ex = 0f, ey = 0f, ez = 0f;
                for (float x = -localHalf; x <= localHalf; x += sampleStep)
                {
                    for (float z = -localHalf; z <= localHalf; z += sampleStep)
                    {
                        float3 p = new float3(x, 1.5f, z);
                        float3 local = GridUtility.GetCellLocalPosition(p, cellSize, gridSize, out _);
                        ex = math.max(ex, QuantErr(local.x, bx));
                        ey = math.max(ey, QuantErr(local.y, by));
                        ez = math.max(ez, QuantErr(local.z, bz));
                    }
                }
                sb.AppendLine($"     mode {s_modes[m]} (bx{bx}/by{by}/bz{bz}): maxErr  x={ex * 1000f:0.##}mm  y={ey * 1000f:0.##}mm  z={ez * 1000f:0.##}mm");
            }

            sb.AppendLine($"================ [Spike0] RESULT: {(pass ? "PASS" : "FAIL")} ================");
            if (pass)
                Debug.Log(sb.ToString());
            else
                Debug.LogError(sb.ToString());
        }

        // Mirrors DefaultGridManagerComponent.GetWorldPosition: world = gridCell*cellSize - offset + local.
        private float3 Reconstruct(byte cellId, float3 local)
        {
            int gx = cellId % gridSize;
            int gz = cellId / gridSize;
            float offset = gridSize * cellSize * 0.5f;
            return new float3(
                gx * cellSize - offset + local.x,
                local.y,
                gz * cellSize - offset + local.z);
        }

        // True if p maps through GetCellLocalPosition without clamping (i.e. identity reconstructs it).
        private bool NoClamp(float3 p)
        {
            float3 local = GridUtility.GetCellLocalPosition(p, cellSize, gridSize, out byte cellId);
            float3 recon = Reconstruct(cellId, local);
            return math.length(p - recon) <= identityTolerance;
        }

        // Quantize one axis with the kit's real Quantize, then dequantize and return abs error.
        private float QuantErr(float local, int bits)
        {
            ushort q = GridUtility.Quantize(local, cellSize, bits);
            float maxInt = (1 << bits) - 1;
            float deq = (q / maxInt) * cellSize; // mirrors NetDataReaderExtension.Dequantize
            return math.abs(deq - math.clamp(local, 0f, cellSize - 0.0001f));
        }
    }
}
