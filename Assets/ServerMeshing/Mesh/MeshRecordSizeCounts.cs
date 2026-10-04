// Server Meshing add-on — record sizes (MESH_METER_RECORD_SIZES=1): walks EntityState (119) packets and counts each
// record's payload length and framed size, for the Send window's optional sizes group.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshRecordSizeCounts counts the records of EntityState packets in the kit's layout
    /// [packed ushort 119][packed long timestamp][int32 count] then per record [packed uint id][int32 length][payload]:
    /// how many, their payload and framed bytes, the payload lengths 0 to 255 one by one and longer ones together, the
    /// shortest and longest, and the packets that could not be walked. Nothing allocates after construction.
    /// </summary>
    public sealed class MeshRecordSizeCounts
    {
        /// <summary>EnvironmentKey turns record sizes on: 0 (the default) or 1. It is read only where a meter or sender runs.</summary>
        public const string EnvironmentKey = "MESH_METER_RECORD_SIZES";

        /// <summary>ExactLengths is how many payload lengths are counted one by one (0 to 255); longer payloads count in Over.</summary>
        public const int ExactLengths = 256;

        private const int CountBytes = 4;
        private const int LengthBytes = 4;

        private readonly long[] _lengths = new long[ExactLengths];

        /// <summary>Records is the complete records counted.</summary>
        public long Records;

        /// <summary>PayloadBytes is the summed payload length of the records.</summary>
        public long PayloadBytes;

        /// <summary>FramedBytes is the summed framed size of the records: packed id bytes, 4 length bytes and the payload.</summary>
        public long FramedBytes;

        /// <summary>Over is the records whose payload is ExactLengths bytes or longer.</summary>
        public long Over;

        /// <summary>Unreadable is the packets whose walk stopped early (truncation, a bad length, a count above the records present).</summary>
        public long Unreadable;

        /// <summary>Min is the shortest payload counted, 0 with no record.</summary>
        public int Min;

        /// <summary>Max is the longest payload counted, 0 with no record.</summary>
        public int Max;

        /// <summary>
        /// TryParseSetting reads a MESH_METER_RECORD_SIZES value, ignoring surrounding whitespace: null, empty and 0 give
        /// off, 1 gives on, each with a null <paramref name="warning"/>. Anything else returns false with off and a warning
        /// naming the key.
        /// </summary>
        public static bool TryParseSetting(string value, out bool enabled, out string warning)
        {
            enabled = false;
            warning = null;
            string trimmed = value == null ? string.Empty : value.Trim();
            if (trimmed.Length == 0 || trimmed == "0")
                return true;
            if (trimmed == "1")
            {
                enabled = true;
                return true;
            }
            warning = $"{EnvironmentKey}={Sync.MeshStateSender.CleanForLog(trimmed)} is not 0 or 1; no record sizes are counted";
            return false;
        }

        /// <summary>CountOf is the records whose payload is exactly <paramref name="length"/> bytes; 0 outside 0 to 255.</summary>
        public long CountOf(int length) => length >= 0 && length < ExactLengths ? _lengths[length] : 0;

        /// <summary>
        /// TryAddPacket walks one EntityState packet in data[offset..end), which starts at the packed message type, and
        /// counts each complete record. A negative length, a length past <paramref name="end"/>, a truncated field or a
        /// count larger than the records present stops the walk, adds one to Unreadable and returns false; the records
        /// before the break stay counted. It allocates nothing.
        /// </summary>
        public bool TryAddPacket(byte[] data, int offset, int end)
        {
            if (data == null || offset < 0 || end > data.Length || offset >= end
                || !MeshSendMeter.TryReadMessageType(data, offset, end, out _, out int typeBytes))
                return Fail();
            int position = offset + typeBytes;
            for (int part = 0; part < 2; ++part)
            {
                int length = PackedLength(data, position, end);
                if (length == 0)
                    return Fail();
                position += length;
            }
            if (end - position < CountBytes)
                return Fail();
            int count = ReadInt(data, position);
            position += CountBytes;
            if (count < 0)
                return Fail();
            for (int record = 0; record < count; ++record)
            {
                int idBytes = PackedLength(data, position, end);
                if (idBytes == 0 || end - position - idBytes < LengthBytes)
                    return Fail();
                position += idBytes;
                int payload = ReadInt(data, position);
                position += LengthBytes;
                if (payload < 0 || payload > end - position)
                    return Fail();
                position += payload;
                AddRecord(idBytes, payload);
            }
            return true;
        }

        /// <summary>
        /// TopLengths gives the three most frequent exact payload lengths with their counts, most frequent first and ties
        /// by the smaller length; an absent place reads 0 with count 0.
        /// </summary>
        public void TopLengths(out int l1, out long c1, out int l2, out long c2, out int l3, out long c3)
        {
            l1 = l2 = l3 = 0;
            c1 = c2 = c3 = 0;
            for (int length = 0; length < ExactLengths; ++length)
            {
                long count = _lengths[length];
                if (count <= 0)
                    continue;
                if (count > c1)
                {
                    l3 = l2; c3 = c2;
                    l2 = l1; c2 = c1;
                    l1 = length; c1 = count;
                }
                else if (count > c2)
                {
                    l3 = l2; c3 = c2;
                    l2 = length; c2 = count;
                }
                else if (count > c3)
                {
                    l3 = length; c3 = count;
                }
            }
        }

        /// <summary>Add adds every figure of <paramref name="other"/> to this one.</summary>
        public void Add(MeshRecordSizeCounts other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            if (other.Records > 0)
            {
                Min = Records > 0 ? Math.Min(Min, other.Min) : other.Min;
                Max = Records > 0 ? Math.Max(Max, other.Max) : other.Max;
            }
            Records += other.Records;
            PayloadBytes += other.PayloadBytes;
            FramedBytes += other.FramedBytes;
            Over += other.Over;
            Unreadable += other.Unreadable;
            for (int i = 0; i < ExactLengths; ++i)
                _lengths[i] += other._lengths[i];
        }

        /// <summary>CopyTo makes <paramref name="into"/> an exact copy of this one.</summary>
        public void CopyTo(MeshRecordSizeCounts into)
        {
            if (into == null)
                throw new ArgumentNullException(nameof(into));
            into.Records = Records;
            into.PayloadBytes = PayloadBytes;
            into.FramedBytes = FramedBytes;
            into.Over = Over;
            into.Unreadable = Unreadable;
            into.Min = Min;
            into.Max = Max;
            Array.Copy(_lengths, into._lengths, ExactLengths);
        }

        /// <summary>Clear zeroes every figure.</summary>
        public void Clear()
        {
            Records = PayloadBytes = FramedBytes = Over = Unreadable = 0;
            Min = Max = 0;
            Array.Clear(_lengths, 0, ExactLengths);
        }

        /// <summary>
        /// ToGroup formats the optional sizes group in invariant culture, for example
        /// " | sizes n 5000 min 54 max 54 top 54:5000 0:0 0:0 payload 54.0 framed 60.0 B over 0 unread 0"; the payload
        /// and framed figures are means per record (0.0 with none).
        /// </summary>
        public string ToGroup()
        {
            TopLengths(out int l1, out long c1, out int l2, out long c2, out int l3, out long c3);
            long records = Clean(Records);
            double payload = records > 0 ? Clean(PayloadBytes) / (double)records : 0d;
            double framed = records > 0 ? Clean(FramedBytes) / (double)records : 0d;
            return FormattableString.Invariant(
                $" | sizes n {records} min {Clean(Min)} max {Clean(Max)} top {l1}:{c1} {l2}:{c2} {l3}:{c3} payload {payload:0.0} framed {framed:0.0} B over {Clean(Over)} unread {Clean(Unreadable)}");
        }

        private void AddRecord(int idBytes, int payload)
        {
            if (Records == 0)
            {
                Min = payload;
                Max = payload;
            }
            else
            {
                if (payload < Min)
                    Min = payload;
                if (payload > Max)
                    Max = payload;
            }
            Records++;
            PayloadBytes += payload;
            FramedBytes += idBytes + LengthBytes + payload;
            if (payload < ExactLengths)
                _lengths[payload]++;
            else
                Over++;
        }

        private bool Fail()
        {
            Unreadable++;
            return false;
        }

        // The kit's packed ints and uints share one lead-byte rule; 0 means truncated or a lead byte no int or uint takes.
        private static int PackedLength(byte[] data, int position, int end)
        {
            if (position >= end)
                return 0;
            int lead = data[position];
            int length = lead < 241 ? 1 : lead <= 248 ? 2 : lead == 249 ? 3 : lead == 250 ? 4 : lead == 251 ? 5 : 0;
            return end - position >= length ? length : 0;
        }

        private static int ReadInt(byte[] data, int position)
        {
            return data[position] | (data[position + 1] << 8) | (data[position + 2] << 16) | (data[position + 3] << 24);
        }

        private static long Clean(long value) => value > 0 ? value : 0;
    }
}
