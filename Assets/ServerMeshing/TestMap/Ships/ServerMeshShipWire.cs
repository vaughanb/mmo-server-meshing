// Server Meshing test rig — the stand-in ship's record codecs (Phase 0 step 7, MESH_SHIP_WIRE). Legacy54 is the
// layout of the space game's ShipMovementComponent record at commit 1a3a58d9 (read there, not copied); Header56 adds
// the space team's agreed two-byte header (bit order ours until they confirm); Compact28 is rig-only.
using System;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipWire writes and reads the three stand-in ship records. Legacy54: position 3 floats, rotation 4 raw
    /// floats x, y, z, w, velocity 3 floats, angular velocity 3 floats, the movement state as a packed uint (0 or 1, one
    /// byte) and the teleport byte. Header56: byte 0 = authority (bits 0-1) | flight state (bits 2-4) &lt;&lt; 2, byte 1 =
    /// epoch, then the 54. Compact28: byte 0 = authority | flight state &lt;&lt; 2 | forward &lt;&lt; 5 | 1 &lt;&lt; 7, byte 1 =
    /// epoch, position 3 x signed 24-bit centimetres, rotation smallest-three in 4 bytes, velocity 3 x int16 at
    /// 0.02 m/s, angular velocity 3 x int16 at 0.001 rad/s, the teleport byte. Writers only append; readers check the
    /// bytes left before reading anything.
    /// </summary>
    public static class ServerMeshShipWire
    {
        /// <summary>Legacy54Bytes is the length of a legacy54 record.</summary>
        public const int Legacy54Bytes = 54;

        /// <summary>Header56Bytes is the length of a header56 record.</summary>
        public const int Header56Bytes = 56;

        /// <summary>Compact28Bytes is the length of a compact28 record.</summary>
        public const int Compact28Bytes = 28;

        /// <summary>CompactBit is byte 0's bit 7, set only in a compact28 record.</summary>
        public const byte CompactBit = 0x80;

        /// <summary>ForwardBit is byte 0's bit 5 in a compact28 record: the movement state.</summary>
        public const byte ForwardBit = 0x20;

        /// <summary>CompactPositionLimit is the largest position component in metres a compact28 record holds (2^23 - 1 cm).</summary>
        public const float CompactPositionLimit = 83886.07f;

        /// <summary>CompactVelocityStep is a compact28 velocity unit in m/s.</summary>
        public const float CompactVelocityStep = 0.02f;

        /// <summary>CompactAngularStep is a compact28 angular velocity unit in rad/s.</summary>
        public const float CompactAngularStep = 0.001f;

        private const int MaxCentimetres = 8388607;
        private const int MaxShort = 32767;
        private const int ComponentMax = 1023;
        private const double SmallestThreeRange = 0.70710678118654752;
        private const int MaxAuthority = 2;
        private const int MaxFlightState = 7;

        /// <summary>SizeOf is the record length of <paramref name="kind"/>; an undefined kind throws naming it.</summary>
        public static int SizeOf(ServerMeshShipWireKind kind)
        {
            switch (kind)
            {
                case ServerMeshShipWireKind.Legacy54: return Legacy54Bytes;
                case ServerMeshShipWireKind.Header56: return Header56Bytes;
                case ServerMeshShipWireKind.Compact28: return Compact28Bytes;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a ship wire kind");
            }
        }

        /// <summary>
        /// HeaderByte composes byte 0: authority in bits 0-1, the flight state in bits 2-4, <paramref name="forward"/> in
        /// bit 5 (meaningful in compact28 only; header56 passes false) and <paramref name="compact"/> in bit 7. An
        /// authority above 2 throws naming Authority, a flight state above 7 throws naming FlightState.
        /// </summary>
        public static byte HeaderByte(ServerMeshShipAuthority a, byte flightState, bool compact, bool forward)
        {
            if ((byte)a > MaxAuthority)
                throw new ArgumentOutOfRangeException(nameof(ServerMeshShipSample.Authority), a, "the ship authority must be 0, 1 or 2 (3 is reserved)");
            if (flightState > MaxFlightState)
                throw new ArgumentOutOfRangeException(nameof(ServerMeshShipSample.FlightState), flightState, "the flight state must fit 3 bits (0 to 7)");
            int value = (byte)a | flightState << 2;
            if (forward)
                value |= ForwardBit;
            if (compact)
                value |= CompactBit;
            return (byte)value;
        }

        /// <summary>WriteLegacy54 appends the 54-byte record of <paramref name="sample"/>; a null writer throws naming it.</summary>
        public static void WriteLegacy54(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            WriteBody(writer, sample);
        }

        /// <summary>
        /// WriteHeader56 appends the header (authority, flight state, epoch) and the 54 bytes; a null writer throws naming
        /// it, a bad authority or flight state as HeaderByte says, before anything is written.
        /// </summary>
        public static void WriteHeader56(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            byte header = HeaderByte(sample.Authority, sample.FlightState, false, false);
            writer.Put(header);
            writer.Put(sample.Epoch);
            WriteBody(writer, sample);
        }

        /// <summary>
        /// WriteCompact28 appends the 28-byte compact record and returns true; it returns false, writing nothing, for a
        /// position component beyond CompactPositionLimit or any non-finite field. Velocities beyond the int16 range are
        /// clamped. Throws as WriteHeader56 does.
        /// </summary>
        public static bool WriteCompact28(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            byte header = HeaderByte(sample.Authority, sample.FlightState, true, sample.Forward);
            if (!TryCentimetres(sample.Position.x, out int x) || !TryCentimetres(sample.Position.y, out int y) || !TryCentimetres(sample.Position.z, out int z))
                return false;
            if (!IsFinite(sample.Velocity) || !IsFinite(sample.AngularVelocity) || !TryPackRotation(sample.Rotation, out uint rotation))
                return false;
            writer.Put(header);
            writer.Put(sample.Epoch);
            PutInt24(writer, x);
            PutInt24(writer, y);
            PutInt24(writer, z);
            writer.Put(rotation);
            writer.Put(Quantise(sample.Velocity.x, CompactVelocityStep));
            writer.Put(Quantise(sample.Velocity.y, CompactVelocityStep));
            writer.Put(Quantise(sample.Velocity.z, CompactVelocityStep));
            writer.Put(Quantise(sample.AngularVelocity.x, CompactAngularStep));
            writer.Put(Quantise(sample.AngularVelocity.y, CompactAngularStep));
            writer.Put(Quantise(sample.AngularVelocity.z, CompactAngularStep));
            writer.Put(sample.TeleportByte);
            return true;
        }

        /// <summary>ReadLegacy54 reads a 54-byte record; fewer bytes left throws naming reader before anything is read.</summary>
        public static void ReadLegacy54(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Legacy54Bytes, "legacy54");
            sample = default;
            ReadBody(reader, ref sample);
        }

        /// <summary>ReadHeader56 reads a 56-byte record; fewer bytes left throws naming reader before anything is read.</summary>
        public static void ReadHeader56(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Header56Bytes, "header56");
            sample = default;
            byte header = reader.GetByte();
            sample.Authority = (ServerMeshShipAuthority)(header & 0x03);
            sample.FlightState = (byte)((header >> 2) & 0x07);
            sample.Epoch = reader.GetByte();
            ReadBody(reader, ref sample);
        }

        /// <summary>
        /// ReadCompact28 reads a 28-byte compact record (the rotation renormalised); fewer bytes left throws naming reader
        /// before anything is read.
        /// </summary>
        public static void ReadCompact28(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Compact28Bytes, "compact28");
            sample = default;
            byte header = reader.GetByte();
            sample.Authority = (ServerMeshShipAuthority)(header & 0x03);
            sample.FlightState = (byte)((header >> 2) & 0x07);
            sample.Forward = (header & ForwardBit) != 0;
            sample.Compact = true;
            sample.Epoch = reader.GetByte();
            float x = GetInt24(reader) / 100f;
            float y = GetInt24(reader) / 100f;
            float z = GetInt24(reader) / 100f;
            sample.Position = new Vector3(x, y, z);
            sample.Rotation = UnpackRotation(reader.GetUInt());
            sample.Velocity = new Vector3(reader.GetShort() * CompactVelocityStep, reader.GetShort() * CompactVelocityStep, reader.GetShort() * CompactVelocityStep);
            sample.AngularVelocity = new Vector3(reader.GetShort() * CompactAngularStep, reader.GetShort() * CompactAngularStep, reader.GetShort() * CompactAngularStep);
            sample.TeleportByte = reader.GetByte();
        }

        /// <summary>
        /// Read reads one record of a ship configured with <paramref name="configured"/>: 54 bytes for legacy54; otherwise
        /// it peeks byte 0 and reads 28 bytes when the compact bit is set, else 56. Too few bytes throw naming reader.
        /// </summary>
        public static void Read(NetDataReader reader, ServerMeshShipWireKind configured, out ServerMeshShipSample sample)
        {
            if (configured == ServerMeshShipWireKind.Legacy54)
            {
                ReadLegacy54(reader, out sample);
                return;
            }
            Require(reader, 1, "ship");
            if ((reader.PeekByte() & CompactBit) != 0)
                ReadCompact28(reader, out sample);
            else
                ReadHeader56(reader, out sample);
        }

        private static void WriteBody(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            writer.Put(sample.Position.x);
            writer.Put(sample.Position.y);
            writer.Put(sample.Position.z);
            writer.Put(sample.Rotation.x);
            writer.Put(sample.Rotation.y);
            writer.Put(sample.Rotation.z);
            writer.Put(sample.Rotation.w);
            writer.Put(sample.Velocity.x);
            writer.Put(sample.Velocity.y);
            writer.Put(sample.Velocity.z);
            writer.Put(sample.AngularVelocity.x);
            writer.Put(sample.AngularVelocity.y);
            writer.Put(sample.AngularVelocity.z);
            writer.PutPackedUInt(sample.Forward ? 1u : 0u);
            writer.Put(sample.TeleportByte);
        }

        private static void ReadBody(NetDataReader reader, ref ServerMeshShipSample sample)
        {
            sample.Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            sample.Rotation = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            sample.Velocity = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            sample.AngularVelocity = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            sample.Forward = reader.GetByte() != 0;
            sample.TeleportByte = reader.GetByte();
        }

        private static void Require(NetDataReader reader, int bytes, string layout)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (reader.AvailableBytes < bytes)
                throw new ArgumentOutOfRangeException(nameof(reader), reader.AvailableBytes, $"a {layout} record needs {bytes} bytes and the reader has {reader.AvailableBytes} left");
        }

        private static bool TryCentimetres(float metres, out int centimetres)
        {
            centimetres = 0;
            if (float.IsNaN(metres) || float.IsInfinity(metres))
                return false;
            double value = Math.Round(metres * 100d);
            if (value > MaxCentimetres || value < -MaxCentimetres)
                return false;
            centimetres = (int)value;
            return true;
        }

        private static void PutInt24(NetDataWriter writer, int value)
        {
            writer.Put((byte)(value & 0xFF));
            writer.Put((byte)((value >> 8) & 0xFF));
            writer.Put((byte)((value >> 16) & 0xFF));
        }

        private static int GetInt24(NetDataReader reader)
        {
            int value = reader.GetByte() | reader.GetByte() << 8 | reader.GetByte() << 16;
            return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
        }

        private static short Quantise(float value, float step)
        {
            double units = Math.Round(value / (double)step);
            return (short)Math.Max(-MaxShort, Math.Min(MaxShort, units));
        }

        private static bool TryPackRotation(Quaternion rotation, out uint packed)
        {
            packed = 0;
            double x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            if (double.IsNaN(x + y + z + w) || double.IsInfinity(x + y + z + w))
                return false;
            double length = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (length < 1e-6)
            {
                x = y = z = 0d;
                w = 1d;
            }
            else
            {
                x /= length;
                y /= length;
                z /= length;
                w /= length;
            }
            int largest = 0;
            double largestAbs = Math.Abs(x);
            if (Math.Abs(y) > largestAbs) { largest = 1; largestAbs = Math.Abs(y); }
            if (Math.Abs(z) > largestAbs) { largest = 2; largestAbs = Math.Abs(z); }
            if (Math.Abs(w) > largestAbs) { largest = 3; }
            double sign = Component(largest, x, y, z, w) < 0d ? -1d : 1d;
            uint value = (uint)largest << 30;
            int shift = 20;
            for (int i = 0; i < 4; ++i)
            {
                if (i == largest)
                    continue;
                double scaled = (sign * Component(i, x, y, z, w) / SmallestThreeRange + 1d) * 0.5d * ComponentMax;
                uint q = (uint)Math.Max(0d, Math.Min(ComponentMax, Math.Round(scaled)));
                value |= q << shift;
                shift -= 10;
            }
            packed = value;
            return true;
        }

        private static Quaternion UnpackRotation(uint packed)
        {
            int largest = (int)(packed >> 30);
            double a = Decode(packed >> 20), b = Decode(packed >> 10), c = Decode(packed);
            double rest = Math.Sqrt(Math.Max(0d, 1d - a * a - b * b - c * c));
            double x, y, z, w;
            switch (largest)
            {
                case 0: x = rest; y = a; z = b; w = c; break;
                case 1: x = a; y = rest; z = b; w = c; break;
                case 2: x = a; y = b; z = rest; w = c; break;
                default: x = a; y = b; z = c; w = rest; break;
            }
            double length = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length));
        }

        private static double Decode(uint bits) => ((bits & ComponentMax) / (double)ComponentMax * 2d - 1d) * SmallestThreeRange;

        private static double Component(int index, double x, double y, double z, double w)
        {
            switch (index)
            {
                case 0: return x;
                case 1: return y;
                case 2: return z;
                default: return w;
            }
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
