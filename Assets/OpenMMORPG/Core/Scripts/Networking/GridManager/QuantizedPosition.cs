using System;

namespace MultiplayerARPG
{
    /// <summary>
    /// One position as the quantized server state carries it: the signed cell holding it, then its offset
    /// inside that cell, quantized to the bits of its compression mode.
    /// </summary>
    public struct EncodedPosition
    {
        public int cellX;
        public int cellY;
        public int cellZ;
        /// <summary>The compression mode, <see cref="QuantizedPosition.MinMode"/> to <see cref="QuantizedPosition.MaxMode"/>.</summary>
        public byte mode;
        /// <summary>How many bytes of <see cref="packed"/> go on the wire, lowest first.</summary>
        public byte byteCount;
        /// <summary>The mode in bits 0-1, then the X, Z and Y offsets.</summary>
        public ulong packed;
    }

    /// <summary>
    /// Cell-based position quantization for the server's entity states.
    /// The world is cut into cubes of a cell size (128 m by default) that start at the world origin and go on
    /// in every direction. A position is sent as its cell's signed X, Y and Z (packed ints: one byte each
    /// within about 15 km of the origin) and its offset inside the cell, quantized by compression mode.
    /// There is no grid edge, so any map size, negative coordinates and any height work.
    /// The server's movement job encodes with this class and the client decodes with it, so the bit layout
    /// is defined only here. It uses nothing from Unity, so it can be tested on its own.
    /// </summary>
    public static class QuantizedPosition
    {
        public const int MinMode = 3;
        public const int MaxMode = 6;
        /// <summary>Cells are clamped to this many cells from the origin, about 1.4e11 m with 128 m cells.</summary>
        public const int MaxCell = 1 << 30;
        public const int MinCell = -MaxCell;
        private const int ModeBitCount = 2;

        public static bool IsValidMode(int mode)
        {
            return mode >= MinMode && mode <= MaxMode;
        }

        /// <summary>
        /// The bits each offset gets in a mode. X and Z share a width. Every mode packs into the same byte
        /// count it had when Y had fewer bits, so the extra Y precision costs nothing.
        /// </summary>
        public static void GetBits(int mode, out int xzBits, out int yBits)
        {
            switch (mode)
            {
                case 3: xzBits = 10; yBits = 8; return;   // 30 bits with the mode, 4 bytes
                case 4: xzBits = 11; yBits = 11; return;  // 35 bits, 5 bytes
                case 5: xzBits = 14; yBits = 14; return;  // 44 bits, 6 bytes
                case 6: xzBits = 16; yBits = 16; return;  // 50 bits, 7 bytes
                default: throw new ArgumentOutOfRangeException(nameof(mode), mode, "Compression mode must be 3 to 6.");
            }
        }

        public static int GetByteCount(int mode)
        {
            GetBits(mode, out int xzBits, out int yBits);
            return (ModeBitCount + xzBits + xzBits + yBits + 7) / 8;
        }

        /// <summary>The mode stored in the first byte on the wire. Every 2-bit value is a valid mode.</summary>
        public static int GetModeFromFirstByte(byte firstByte)
        {
            return (firstByte & 0b11) + MinMode;
        }

        /// <summary>The step between two neighbouring quantized values, in metres.</summary>
        public static float GetStep(float cellSize, int bits)
        {
            return cellSize / ((1 << bits) - 1);
        }

        /// <summary>
        /// The cell holding a coordinate. Floors, so negative coordinates get negative cells rather than
        /// wrapping. NaN gives cell 0 and values past <see cref="MaxCell"/> cells are clamped.
        /// </summary>
        public static int GetCell(float value, float cellSize)
        {
            double cell = Math.Floor((double)value / cellSize);
            if (double.IsNaN(cell))
                return 0;
            if (cell < MinCell)
                return MinCell;
            if (cell > MaxCell)
                return MaxCell;
            return (int)cell;
        }

        /// <summary>Rounds an offset inside a cell to the nearest of 2^bits evenly spaced values.</summary>
        public static uint Quantize(double localOffset, float cellSize, int bits)
        {
            uint maxValue = (1u << bits) - 1;
            double normalized = localOffset / cellSize;
            // Also catches NaN, which fails every comparison
            if (!(normalized > 0d))
                return 0;
            if (normalized >= 1d)
                return maxValue;
            return (uint)(normalized * maxValue + 0.5d);
        }

        public static double Dequantize(uint value, float cellSize, int bits)
        {
            uint maxValue = (1u << bits) - 1;
            return (double)value / maxValue * cellSize;
        }

        public static EncodedPosition Encode(float x, float y, float z, float cellSize, int mode)
        {
            if (!(cellSize > 0f))
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
            GetBits(mode, out int xzBits, out int yBits);

            EncodedPosition result = new EncodedPosition();
            result.cellX = GetCell(x, cellSize);
            result.cellY = GetCell(y, cellSize);
            result.cellZ = GetCell(z, cellSize);
            result.mode = (byte)mode;
            result.byteCount = (byte)GetByteCount(mode);

            // The offset is taken in double, so it stays exact far from the origin
            uint qx = Quantize(x - (double)result.cellX * cellSize, cellSize, xzBits);
            uint qy = Quantize(y - (double)result.cellY * cellSize, cellSize, yBits);
            uint qz = Quantize(z - (double)result.cellZ * cellSize, cellSize, xzBits);

            int shift = 0;
            ulong packed = (ulong)(mode - MinMode);
            shift += ModeBitCount;
            packed |= (ulong)qx << shift;
            shift += xzBits;
            packed |= (ulong)qz << shift;
            shift += xzBits;
            packed |= (ulong)qy << shift;
            result.packed = packed;
            return result;
        }

        /// <summary>
        /// Decodes a position. Reads the mode from <see cref="EncodedPosition.packed"/>, as the client does,
        /// not from <see cref="EncodedPosition.mode"/>.
        /// </summary>
        public static void Decode(in EncodedPosition position, float cellSize, out float x, out float y, out float z)
        {
            int mode = GetModeFromFirstByte((byte)(position.packed & 0xFF));
            GetBits(mode, out int xzBits, out int yBits);

            int shift = ModeBitCount;
            uint qx = (uint)((position.packed >> shift) & ((1UL << xzBits) - 1));
            shift += xzBits;
            uint qz = (uint)((position.packed >> shift) & ((1UL << xzBits) - 1));
            shift += xzBits;
            uint qy = (uint)((position.packed >> shift) & ((1UL << yBits) - 1));

            x = (float)((double)position.cellX * cellSize + Dequantize(qx, cellSize, xzBits));
            y = (float)((double)position.cellY * cellSize + Dequantize(qy, cellSize, yBits));
            z = (float)((double)position.cellZ * cellSize + Dequantize(qz, cellSize, xzBits));
        }
    }
}
