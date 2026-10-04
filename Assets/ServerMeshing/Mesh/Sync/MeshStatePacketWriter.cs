// Server Meshing add-on — writes EntityState (119) packets in the kit's exact layout, capped so a packet never fragments.
using System;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshStatePacketWriter builds one EntityState packet at a time in the kit's layout:
    /// [packed ushort 119][packed long timestamp][int32 count] then per record [packed uint object id][int32 length][bytes].
    /// A record is appended only when the whole packet stays within the cap, so a packet never needs fragmenting. Records
    /// are byte-identical to the kit's; packet boundaries are not (the kit splits on a fixed 8-byte allowance). It owns one
    /// writer and allocates nothing after construction for records that fit the cap.
    /// </summary>
    public sealed class MeshStatePacketWriter
    {
        /// <summary>EntityStateMessageType is the kit's EntityState message id.</summary>
        public const ushort EntityStateMessageType = 119;

        /// <summary>MaxPacketBytes is the largest packet that goes out in one datagram on every delivery method (1024 minus the channeled header).</summary>
        public const int MaxPacketBytes = 1020;

        private const int MinPacketBytes = 64;
        private const int CountBytes = 4;
        private const int LengthBytes = 4;

        private readonly NetDataWriter _writer;
        private readonly int _maxPacketBytes;
        private int _countOffset;
        private bool _begun;

        /// <summary>MeshStatePacketWriter creates a writer whose packets never exceed <paramref name="maxPacketBytes"/> (64 to 1020).</summary>
        public MeshStatePacketWriter(int maxPacketBytes = MaxPacketBytes)
        {
            if (maxPacketBytes < MinPacketBytes || maxPacketBytes > MaxPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(maxPacketBytes), maxPacketBytes, $"the packet cap must be {MinPacketBytes} to {MaxPacketBytes} bytes");
            _maxPacketBytes = maxPacketBytes;
            _writer = new NetDataWriter(true, maxPacketBytes + 4);
        }

        /// <summary>Count is the number of records in the current packet.</summary>
        public int Count { get; private set; }

        /// <summary>IsEmpty is true while the current packet holds no record.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>Length is the current packet's length in bytes, header included.</summary>
        public int Length => _writer.Length;

        /// <summary>HeaderLength is the length of the current packet's header (type, timestamp and count).</summary>
        public int HeaderLength { get; private set; }

        /// <summary>Timestamp is the timestamp the current packet was begun with.</summary>
        public long Timestamp { get; private set; }

        /// <summary>Begin starts a new, empty packet stamped with <paramref name="timestamp"/>; whatever the writer held is dropped.</summary>
        public void Begin(long timestamp)
        {
            _writer.Reset();
            _writer.PutPackedUShort(EntityStateMessageType);
            _writer.PutPackedLong(timestamp);
            _countOffset = _writer.Length;
            _writer.Put(0);
            HeaderLength = _writer.Length;
            Timestamp = timestamp;
            Count = 0;
            _begun = true;
        }

        /// <summary>
        /// TryAppend adds one record whose payload is <paramref name="length"/> bytes of <paramref name="buffer"/> from
        /// <paramref name="offset"/>, and returns false, writing nothing, when the packet would exceed the cap.
        /// </summary>
        public bool TryAppend(uint objectId, byte[] buffer, int offset, int length)
        {
            CheckSpan(buffer, offset, length);
            if (!_begun)
                throw new InvalidOperationException($"object {objectId}: Begin must run before the first record");
            if (_writer.Length + RecordSize(objectId, length) > _maxPacketBytes)
                return false;
            _writer.PutPackedUInt(objectId);
            _writer.Put(length);
            _writer.Put(buffer, offset, length);
            Count++;
            return true;
        }

        /// <summary>
        /// Finish writes the record count into the header and returns the writer holding the packet, valid until the next
        /// Begin. Length stays at the packet's end.
        /// </summary>
        public NetDataWriter Finish()
        {
            if (!_begun)
                throw new InvalidOperationException("Begin must run before Finish");
            int end = _writer.SetPosition(_countOffset);
            _writer.Put(Count);
            _writer.SetPosition(end);
            return _writer;
        }

        /// <summary>MaxPayload is the largest payload of object <paramref name="objectId"/> that fits an empty packet of this writer.</summary>
        public int MaxPayload(uint objectId)
        {
            return _maxPacketBytes - HeaderLength - LengthBytes - PackedUIntSize(objectId);
        }

        /// <summary>PackedUIntSize is how many bytes the kit's packed encoding of <paramref name="value"/> takes (1 to 5).</summary>
        public static int PackedUIntSize(uint value)
        {
            if (value <= 240)
                return 1;
            if (value <= 2287)
                return 2;
            if (value <= 67823)
                return 3;
            if (value <= 16777215)
                return 4;
            return 5;
        }

        /// <summary>RecordSize is the bytes one record of object <paramref name="objectId"/> with a <paramref name="length"/>-byte payload takes.</summary>
        public static int RecordSize(uint objectId, int length) => PackedUIntSize(objectId) + LengthBytes + length;

        /// <summary>
        /// WriteSingle resets <paramref name="writer"/> and writes one uncapped packet holding the single record, for a
        /// reliable record too big for a capped packet (the transport fragments it).
        /// </summary>
        public static void WriteSingle(NetDataWriter writer, long timestamp, uint objectId, byte[] buffer, int offset, int length)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            CheckSpan(buffer, offset, length);
            writer.Reset();
            writer.PutPackedUShort(EntityStateMessageType);
            writer.PutPackedLong(timestamp);
            writer.Put(1);
            writer.PutPackedUInt(objectId);
            writer.Put(length);
            writer.Put(buffer, offset, length);
        }

        private static void CheckSpan(byte[] buffer, int offset, int length)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"the offset must lie within the {buffer.Length}-byte buffer");
            if (length < 0 || length > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(length), length, $"{length} bytes from offset {offset} run past the {buffer.Length}-byte buffer");
        }
    }
}
