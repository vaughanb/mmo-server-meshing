using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.Tests
{
    /// <summary>
    /// Encode and decode round trips for <see cref="QuantizedPosition"/>. Uses nothing from Unity.
    /// </summary>
    public class QuantizedPositionTests
    {
        private const float CellSize = 128f;

        private static readonly int[] s_modes = { 3, 4, 5, 6 };

        // Corners and edges of the old ±960 m grid, negative space, heights outside 0–128 m, cell
        // boundaries from both sides, and points far from the origin.
        private static readonly float[] s_coordinates =
        {
            0f, 0.001f, 63.99f, 64f, 127.9999f, 128f, 128.0001f,
            -0.0001f, -0.5f, -127.9999f, -128f, -128.0001f,
            959.9f, 960f, 960.1f, -959.9f, -960f, -960.1f,
            1919.5f, -1920.5f, 5000.25f, -5000.25f, 15360f, -15360f,
            100000.5f, -100000.5f,
        };

        private static readonly float[] s_heights =
        {
            0f, 0.25f, 64f, 127.9999f, 128f, 128.5f, 500f, 3000f,
            -0.0001f, -1f, -50f, -128f, -129f, -1000f,
        };

        private static IEnumerable<int> Modes => s_modes;

        private static double Tolerance(float value, int bits)
        {
            // Half a step from rounding, plus the float's own precision at that magnitude
            return QuantizedPosition.GetStep(CellSize, bits) * 0.5d + Math.Abs(value) * 1.2e-7d + 1e-6d;
        }

        private static void AssertRoundTrip(float x, float y, float z, int mode)
        {
            QuantizedPosition.GetBits(mode, out int xzBits, out int yBits);
            EncodedPosition encoded = QuantizedPosition.Encode(x, y, z, CellSize, mode);
            QuantizedPosition.Decode(encoded, CellSize, out float dx, out float dy, out float dz);
            string at = $"mode {mode} at ({x}, {y}, {z}) decoded ({dx}, {dy}, {dz})";
            Assert.AreEqual(x, dx, Tolerance(x, xzBits), "X " + at);
            Assert.AreEqual(y, dy, Tolerance(y, yBits), "Y " + at);
            Assert.AreEqual(z, dz, Tolerance(z, xzBits), "Z " + at);
        }

        [Test]
        public void RoundTrip_StaysWithinHalfAStep_ForEveryModeAndCoordinate([ValueSource(nameof(Modes))] int mode)
        {
            foreach (float coordinate in s_coordinates)
            {
                foreach (float height in s_heights)
                {
                    AssertRoundTrip(coordinate, height, -coordinate, mode);
                    AssertRoundTrip(-coordinate, height, coordinate * 0.5f, mode);
                }
            }
        }

        [Test]
        public void RoundTrip_StaysWithinHalfAStep_ForRandomPositions([ValueSource(nameof(Modes))] int mode)
        {
            Random random = new Random(12345 + mode);
            for (int i = 0; i < 20000; ++i)
            {
                float x = (float)((random.NextDouble() * 2d - 1d) * 20000d);
                float y = (float)((random.NextDouble() * 2d - 1d) * 2000d);
                float z = (float)((random.NextDouble() * 2d - 1d) * 20000d);
                AssertRoundTrip(x, y, z, mode);
            }
        }

        [Test]
        public void PositionsPastTheOldGrid_AreNotClamped()
        {
            // The old grid clamped X and Z to ±960 m and Y to 0–128 m, and wrapped west and south of the grid
            // to its far side.
            EncodedPosition encoded = QuantizedPosition.Encode(-1000f, -50f, 5000f, CellSize, 6);
            QuantizedPosition.Decode(encoded, CellSize, out float x, out float y, out float z);
            Assert.AreEqual(-1000f, x, 0.01f);
            Assert.AreEqual(-50f, y, 0.01f);
            Assert.AreEqual(5000f, z, 0.01f);

            encoded = QuantizedPosition.Encode(2500f, 900f, -2500f, CellSize, 3);
            QuantizedPosition.Decode(encoded, CellSize, out x, out y, out z);
            Assert.AreEqual(2500f, x, 0.07f);
            Assert.AreEqual(900f, y, 0.26f);
            Assert.AreEqual(-2500f, z, 0.07f);
        }

        [Test]
        public void GetCell_FloorsNegativeCoordinates()
        {
            Assert.AreEqual(0, QuantizedPosition.GetCell(0f, CellSize));
            Assert.AreEqual(0, QuantizedPosition.GetCell(127.9f, CellSize));
            Assert.AreEqual(1, QuantizedPosition.GetCell(128f, CellSize));
            Assert.AreEqual(-1, QuantizedPosition.GetCell(-0.0001f, CellSize));
            Assert.AreEqual(-1, QuantizedPosition.GetCell(-128f, CellSize));
            Assert.AreEqual(-2, QuantizedPosition.GetCell(-128.0001f, CellSize));
            Assert.AreEqual(-8, QuantizedPosition.GetCell(-960.1f, CellSize));
        }

        [Test]
        public void GetCell_ClampsNonFiniteAndHugeValues()
        {
            Assert.AreEqual(0, QuantizedPosition.GetCell(float.NaN, CellSize));
            Assert.AreEqual(QuantizedPosition.MaxCell, QuantizedPosition.GetCell(float.PositiveInfinity, CellSize));
            Assert.AreEqual(QuantizedPosition.MinCell, QuantizedPosition.GetCell(float.NegativeInfinity, CellSize));
            Assert.AreEqual(QuantizedPosition.MaxCell, QuantizedPosition.GetCell(float.MaxValue, CellSize));
            Assert.AreEqual(QuantizedPosition.MinCell, QuantizedPosition.GetCell(float.MinValue, CellSize));
        }

        [Test]
        public void Encode_DoesNotThrow_ForNonFiniteValues([ValueSource(nameof(Modes))] int mode)
        {
            Assert.DoesNotThrow(() => QuantizedPosition.Encode(float.NaN, float.PositiveInfinity, float.NegativeInfinity, CellSize, mode));
        }

        [Test]
        public void ByteCount_MatchesEachMode()
        {
            Assert.AreEqual(4, QuantizedPosition.GetByteCount(3));
            Assert.AreEqual(5, QuantizedPosition.GetByteCount(4));
            Assert.AreEqual(6, QuantizedPosition.GetByteCount(5));
            Assert.AreEqual(7, QuantizedPosition.GetByteCount(6));
        }

        [Test]
        public void Packed_FitsInItsByteCount_AndCarriesItsMode([ValueSource(nameof(Modes))] int mode)
        {
            // The largest offsets in every axis set every bit the mode uses
            EncodedPosition encoded = QuantizedPosition.Encode(127.9999f, 127.9999f, 127.9999f, CellSize, mode);
            Assert.AreEqual(mode, encoded.mode);
            Assert.AreEqual(QuantizedPosition.GetByteCount(mode), encoded.byteCount);
            Assert.AreEqual(0UL, encoded.packed >> (8 * encoded.byteCount), "bits past the byte count would be dropped on the wire");
            Assert.AreEqual(mode, QuantizedPosition.GetModeFromFirstByte((byte)(encoded.packed & 0xFF)));
        }

        [Test]
        public void ModeThree_HasHalfMetreHeightSteps()
        {
            // Mode 3 had 4 bits of Y before, about 8.5 m steps
            QuantizedPosition.GetBits(3, out _, out int yBits);
            Assert.Less(QuantizedPosition.GetStep(CellSize, yBits), 0.51f);
        }

        [Test]
        public void Quantize_RoundsToTheNearestValue()
        {
            float step = QuantizedPosition.GetStep(CellSize, 10);
            Assert.AreEqual(0u, QuantizedPosition.Quantize(step * 0.4d, CellSize, 10));
            Assert.AreEqual(1u, QuantizedPosition.Quantize(step * 0.6d, CellSize, 10));
            Assert.AreEqual(1023u, QuantizedPosition.Quantize(CellSize - step * 0.4d, CellSize, 10));
            Assert.AreEqual(0u, QuantizedPosition.Quantize(-1d, CellSize, 10));
            Assert.AreEqual(1023u, QuantizedPosition.Quantize(CellSize * 2d, CellSize, 10));
            Assert.AreEqual(0u, QuantizedPosition.Quantize(double.NaN, CellSize, 10));
        }

        [Test]
        public void Decode_ReadsTheModeFromPackedBits()
        {
            EncodedPosition encoded = QuantizedPosition.Encode(10f, 20f, 30f, CellSize, 5);
            // The client never sees the mode field, only the bytes
            encoded.mode = 0;
            QuantizedPosition.Decode(encoded, CellSize, out float x, out float y, out float z);
            Assert.AreEqual(10f, x, 0.01f);
            Assert.AreEqual(20f, y, 0.01f);
            Assert.AreEqual(30f, z, 0.01f);
        }

        [Test]
        public void InvalidModeOrCellSize_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QuantizedPosition.Encode(0f, 0f, 0f, CellSize, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuantizedPosition.Encode(0f, 0f, 0f, CellSize, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuantizedPosition.Encode(0f, 0f, 0f, 0f, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuantizedPosition.GetByteCount(0));
        }
    }
}
