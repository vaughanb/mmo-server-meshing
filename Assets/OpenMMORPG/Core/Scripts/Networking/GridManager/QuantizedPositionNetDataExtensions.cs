using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Puts a <see cref="QuantizedPosition"/> on the wire and reads it back: the cell X, Z and Y as packed
    /// ints, then the packed offsets, lowest byte first. The first of those bytes carries the mode, so the
    /// reader knows how many follow.
    /// </summary>
    public static class QuantizedPositionNetDataExtensions
    {
        public static void PutQuantizedPosition(this NetDataWriter writer, in EncodedPosition position)
        {
            writer.PutPackedInt(position.cellX);
            writer.PutPackedInt(position.cellZ);
            writer.PutPackedInt(position.cellY);
            ulong packed = position.packed;
            for (int i = 0; i < position.byteCount; ++i)
            {
                writer.Put((byte)(packed & 0xFF));
                packed >>= 8;
            }
        }

        public static EncodedPosition GetEncodedPosition(this NetDataReader reader)
        {
            EncodedPosition position = new EncodedPosition();
            position.cellX = reader.GetPackedInt();
            position.cellZ = reader.GetPackedInt();
            position.cellY = reader.GetPackedInt();
            byte firstByte = reader.GetByte();
            int mode = QuantizedPosition.GetModeFromFirstByte(firstByte);
            position.mode = (byte)mode;
            position.byteCount = (byte)QuantizedPosition.GetByteCount(mode);
            ulong packed = firstByte;
            for (int i = 1; i < position.byteCount; ++i)
            {
                packed |= (ulong)reader.GetByte() << (8 * i);
            }
            position.packed = packed;
            return position;
        }

        public static Vector3 GetQuantizedPosition(this NetDataReader reader, float cellSize)
        {
            EncodedPosition position = reader.GetEncodedPosition();
            QuantizedPosition.Decode(position, cellSize, out float x, out float y, out float z);
            return new Vector3(x, y, z);
        }
    }
}
