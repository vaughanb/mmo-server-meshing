// Server Meshing test rig — the stand-in ship's record codecs (Phase 0 step 7, MESH_SHIP_WIRE). Legacy54 is the
// layout of the space game's ShipMovementComponent record at commit 1a3a58d9 (read there, not copied); Header56 is its
// ShipWire header at commit c2eb4154 (mode 0, Bubble) and the 54; Compact28 is our reading of their proposed mode 3
// (step 7b decision 2; rig-only until their golden pins it).
using System;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipWire writes and reads the three stand-in ship records. Legacy54: position 3 floats, rotation 4 raw
    /// floats x, y, z, w, velocity 3 floats, angular velocity 3 floats, the movement state as a packed uint (0 or 1, one
    /// byte) and the teleport byte. The header is the space game's: byte 0 = wire mode (bits 0-1) | flight state (bits
    /// 2-4) &lt;&lt; 2, bits 5-7 written 0 and ignored on read; byte 1 = epoch. Header56: the header with mode 0, then the
    /// 54. Compact28: the header with mode 3, position 3 float32, rotation smallest-three in 4 bytes, velocity 3 IEEE
    /// halves, angular velocity 3 signed bytes at CompactAngularFullScale / 127 rad/s, and a state byte = teleport
    /// sequence low 7 bits | forward &lt;&lt; 7. Writers only append; readers check the bytes left and the mode before
    /// reading anything.
    /// </summary>
    public static class ServerMeshShipWire
    {
        /// <summary>Legacy54Bytes is the length of a legacy54 record.</summary>
        public const int Legacy54Bytes = 54;

        /// <summary>Header56Bytes is the length of a header56 record.</summary>
        public const int Header56Bytes = 56;

        /// <summary>Compact28Bytes is the length of a compact28 record.</summary>
        public const int Compact28Bytes = 28;

        /// <summary>ModeBubble is the wire mode of a full record (the space game's Bubble, mode 0).</summary>
        public const byte ModeBubble = 0;

        /// <summary>ModeCompact is the wire mode of a compact28 record (the space game's proposed mode 3).</summary>
        public const byte ModeCompact = 3;

        /// <summary>CompactAngularFullScale is the angular velocity in rad/s a compact28 signed byte of 127 stands for.</summary>
        public const float CompactAngularFullScale = 4f;

        /// <summary>HalfMax is the largest finite IEEE half; compact28 velocity components are clamped to plus or minus it.</summary>
        public const float HalfMax = 65504f;

        private const int ComponentMax = 1023;
        private const double SmallestThreeRange = 0.70710678118654752;
        private const int MaxMode = 3;
        private const int MaxFlightState = 7;
        private const byte ModeMask = 0x03;
        private const byte SequenceMask = 0x7F;
        private const byte ForwardFlag = 0x80;
        private const float AngularStep = CompactAngularFullScale / 127f;

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
        /// HeaderByte composes header byte 0: <paramref name="mode"/> in bits 0-1 and <paramref name="flightState"/> in
        /// bits 2-4, bits 5-7 zero. A mode above 3 throws naming mode, a flight state above 7 throws naming FlightState.
        /// </summary>
        public static byte HeaderByte(byte mode, byte flightState)
        {
            if (mode > MaxMode)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "the wire mode must fit 2 bits (0 to 3)");
            if (flightState > MaxFlightState)
                throw new ArgumentOutOfRangeException(nameof(ServerMeshShipSample.FlightState), flightState, "the flight state must fit 3 bits (0 to 7)");
            return (byte)(mode | flightState << 2);
        }

        /// <summary>ModeOf is the wire mode in header byte <paramref name="headerByte"/> (bits 0-1).</summary>
        public static byte ModeOf(byte headerByte) => (byte)(headerByte & ModeMask);

        /// <summary>WriteLegacy54 appends the 54-byte record of <paramref name="sample"/>; a null writer throws naming it.</summary>
        public static void WriteLegacy54(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            WriteBody(writer, sample);
        }

        /// <summary>
        /// WriteHeader56 appends the header (mode 0, flight state, epoch) and the 54 bytes; a null writer throws naming it,
        /// a flight state above 7 as HeaderByte says, before anything is written.
        /// </summary>
        public static void WriteHeader56(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            byte header = HeaderByte(ModeBubble, sample.FlightState);
            writer.Put(header);
            writer.Put(sample.Epoch);
            WriteBody(writer, sample);
        }

        /// <summary>
        /// WriteCompact28 appends the 28-byte compact record (mode 3) and returns true; it returns false, writing nothing,
        /// only when a position, rotation, velocity or angular velocity component is not finite. Velocity components are
        /// clamped to plus or minus HalfMax, angular velocity components to plus or minus CompactAngularFullScale. Throws as
        /// WriteHeader56 does.
        /// </summary>
        public static bool WriteCompact28(NetDataWriter writer, in ServerMeshShipSample sample)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            byte header = HeaderByte(ModeCompact, sample.FlightState);
            if (!IsFinite(sample.Position) || !IsFinite(sample.Velocity) || !IsFinite(sample.AngularVelocity) || !TryPackRotation(sample.Rotation, out uint rotation))
                return false;
            writer.Put(header);
            writer.Put(sample.Epoch);
            writer.Put(sample.Position.x);
            writer.Put(sample.Position.y);
            writer.Put(sample.Position.z);
            writer.Put(rotation);
            writer.Put(Half(sample.Velocity.x));
            writer.Put(Half(sample.Velocity.y));
            writer.Put(Half(sample.Velocity.z));
            writer.Put(Angular(sample.AngularVelocity.x));
            writer.Put(Angular(sample.AngularVelocity.y));
            writer.Put(Angular(sample.AngularVelocity.z));
            writer.Put((byte)((sample.TeleportByte & SequenceMask) | (sample.Forward ? ForwardFlag : 0)));
            return true;
        }

        /// <summary>ReadLegacy54 reads a 54-byte record; fewer bytes left throws naming reader before anything is read.</summary>
        public static void ReadLegacy54(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Legacy54Bytes, "legacy54");
            sample = default;
            ReadBody(reader, ref sample);
        }

        /// <summary>
        /// ReadHeader56 reads a 56-byte mode 0 record; fewer bytes left, or a header whose mode is not 0, throws naming
        /// reader before anything is read.
        /// </summary>
        public static void ReadHeader56(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Header56Bytes, "header56");
            RequireMode(reader, ModeBubble, "header56");
            sample = default;
            byte header = reader.GetByte();
            sample.FlightState = (byte)((header >> 2) & MaxFlightState);
            sample.Epoch = reader.GetByte();
            ReadBody(reader, ref sample);
        }

        /// <summary>
        /// ReadCompact28 reads a 28-byte mode 3 record (the rotation renormalised; Compact true; TeleportByte the 7-bit
        /// sequence; Forward from the state byte's bit 7); fewer bytes left, or a header whose mode is not 3, throws naming
        /// reader before anything is read.
        /// </summary>
        public static void ReadCompact28(NetDataReader reader, out ServerMeshShipSample sample)
        {
            Require(reader, Compact28Bytes, "compact28");
            RequireMode(reader, ModeCompact, "compact28");
            sample = default;
            byte header = reader.GetByte();
            sample.FlightState = (byte)((header >> 2) & MaxFlightState);
            sample.Compact = true;
            sample.Epoch = reader.GetByte();
            sample.Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            sample.Rotation = UnpackRotation(reader.GetUInt());
            float vx = Mathf.HalfToFloat(reader.GetUShort());
            float vy = Mathf.HalfToFloat(reader.GetUShort());
            float vz = Mathf.HalfToFloat(reader.GetUShort());
            sample.Velocity = new Vector3(vx, vy, vz);
            float ax = reader.GetSByte() * AngularStep;
            float ay = reader.GetSByte() * AngularStep;
            float az = reader.GetSByte() * AngularStep;
            sample.AngularVelocity = new Vector3(ax, ay, az);
            byte state = reader.GetByte();
            sample.TeleportByte = (byte)(state & SequenceMask);
            sample.Forward = (state & ForwardFlag) != 0;
        }

        /// <summary>
        /// Read reads one record of a ship configured with <paramref name="configured"/>: 54 bytes for legacy54; otherwise
        /// it peeks header byte 0 and reads 56 bytes for mode 0 or 28 for mode 3. Modes 1 and 2, or too few bytes, throw
        /// naming reader before anything is read.
        /// </summary>
        public static void Read(NetDataReader reader, ServerMeshShipWireKind configured, out ServerMeshShipSample sample)
        {
            if (configured == ServerMeshShipWireKind.Legacy54)
            {
                ReadLegacy54(reader, out sample);
                return;
            }
            Require(reader, 1, "ship");
            byte mode = ModeOf(reader.PeekByte());
            if (mode == ModeBubble)
                ReadHeader56(reader, out sample);
            else if (mode == ModeCompact)
                ReadCompact28(reader, out sample);
            else
                throw new ArgumentOutOfRangeException(nameof(reader), mode, $"a ship record of wire mode {mode} is not read in Phase 0 (only modes 0 and 3)");
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

        private static void RequireMode(NetDataReader reader, byte expected, string layout)
        {
            byte mode = ModeOf(reader.PeekByte());
            if (mode != expected)
                throw new ArgumentOutOfRangeException(nameof(reader), mode, $"a {layout} record has wire mode {expected} and the reader's record has mode {mode}");
        }

        private static ushort Half(float value)
        {
            return Mathf.FloatToHalf(Math.Max(-HalfMax, Math.Min(HalfMax, value)));
        }

        private static sbyte Angular(float value)
        {
            double units = Math.Round(value / (double)AngularStep, MidpointRounding.AwayFromZero);
            return (sbyte)Math.Max(-127d, Math.Min(127d, units));
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
