// Server Meshing add-on — the send meter (MESH_METER_SENDS): every server send the kit makes, counted by packed message
// type, delivery method and channel, with refused and too-big sends apart, and taken once per probe window as a
// "Send window" log line next to the Work window line. Main-thread only; a send from any other thread is only counted.
using System;
using System.Text;
using System.Threading;
using LiteNetLib;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshSendMeter accumulates server sends between windows. Record allocates nothing: types live in a fixed
    /// open-addressed table of <see cref="TypeSlots"/> slots (types beyond it go to other), deliveries in
    /// <see cref="DeliverySlots"/> slots plus other, and channels in <see cref="ChannelSlots"/> slots plus other. Its
    /// state is touched only on the thread that built it; a call from another thread counts in OffThread and changes
    /// no other total.
    /// </summary>
    public sealed class MeshSendMeter
    {
        /// <summary>TypeSlots is how many distinct message types one window keeps apart; later types count in other.</summary>
        public const int TypeSlots = 64;
        /// <summary>DeliverySlots is the number of defined delivery methods (values 0 to 4); any other value counts in other.</summary>
        public const int DeliverySlots = 5;
        /// <summary>ChannelSlots is the number of channels kept apart (0 to 15); any other channel counts in other.</summary>
        public const int ChannelSlots = 16;

        private const int TypeMask = TypeSlots - 1;

        private readonly long _ticksPerSecond;
        private readonly int _ownerThreadId;
        private readonly bool[] _used = new bool[TypeSlots];
        private readonly ushort[] _types = new ushort[TypeSlots];
        private readonly long[] _typeCalls = new long[TypeSlots];
        private readonly long[] _typeBytes = new long[TypeSlots];
        private readonly long[] _typeTicks = new long[TypeSlots];
        private readonly long[] _deliveryCalls = new long[DeliverySlots + 1];
        private readonly long[] _deliveryBytes = new long[DeliverySlots + 1];
        private readonly long[] _channelCalls = new long[ChannelSlots + 1];
        private long _otherCalls, _otherBytes, _otherTicks;
        private long _refused, _tooBigCalls, _tooBigBytes;
        private int _offThread;

        /// <summary>
        /// MeshSendMeter builds an empty meter owned by the calling thread. <paramref name="ticksPerSecond"/> is the
        /// frequency of the elapsed ticks passed to Record (Stopwatch.Frequency in the game); it must be positive.
        /// </summary>
        public MeshSendMeter(long ticksPerSecond)
        {
            if (ticksPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), ticksPerSecond, "the send meter needs a positive tick frequency");
            _ticksPerSecond = ticksPerSecond;
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>TicksPerSecond is the frequency of the elapsed ticks this meter records.</summary>
        public long TicksPerSecond => _ticksPerSecond;

        /// <summary>OwnerThreadId is the managed id of the thread that built the meter, the only one whose sends are tallied.</summary>
        public int OwnerThreadId => _ownerThreadId;

        /// <summary>
        /// TryReadMessageType decodes the kit's packed ushort message type from data[offset..end). It fails on null data,
        /// an empty or out-of-range span, a truncated 2- or 3-byte form, a lead byte of 250 or more (forms a ushort never
        /// takes) and a 3-byte value above 65535, which the kit's own reader would silently truncate.
        /// <paramref name="typeBytes"/> is the encoded length (1 to 3) on success and 0 on failure.
        /// </summary>
        public static bool TryReadMessageType(byte[] data, int offset, int end, out ushort type, out int typeBytes)
        {
            type = 0;
            typeBytes = 0;
            if (data == null || offset < 0 || end > data.Length || offset >= end)
                return false;
            int lead = data[offset];
            if (lead < 241)
            {
                type = (ushort)lead;
                typeBytes = 1;
                return true;
            }
            if (lead >= 250 || end - offset < 2)
                return false;
            int second = data[offset + 1];
            if (lead <= 248)
            {
                type = (ushort)(240 + 256 * (lead - 241) + second);
                typeBytes = 2;
                return true;
            }
            if (end - offset < 3)
                return false;
            int value = 2288 + 256 * second + data[offset + 2];
            if (value > ushort.MaxValue)
                return false;
            type = (ushort)value;
            typeBytes = 3;
            return true;
        }

        /// <summary>TryReadMessageType decodes the packed message type at the start of data[0..length).</summary>
        public static bool TryReadMessageType(byte[] data, int length, out ushort type, out int typeBytes)
        {
            return TryReadMessageType(data, 0, length, out type, out typeBytes);
        }

        /// <summary>
        /// Record counts one send that returned: <paramref name="bytes"/> (the writer's length, packed type included) and
        /// <paramref name="elapsedTicks"/> (time inside the inner send, 0 when not timed) under its type, delivery method and
        /// channel. <paramref name="refused"/> marks a send the inner transport returned false for; it counts in every
        /// total and also in Refused.
        /// </summary>
        public void Record(ushort type, DeliveryMethod deliveryMethod, byte channel, int bytes, long elapsedTicks, bool refused)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                Interlocked.Increment(ref _offThread);
                return;
            }
            int slot = SlotFor(type);
            if (slot >= 0)
            {
                _typeCalls[slot]++;
                _typeBytes[slot] += bytes;
                _typeTicks[slot] += elapsedTicks;
            }
            else
            {
                AddOther(bytes, elapsedTicks);
            }
            AddRouting(deliveryMethod, channel, bytes, refused);
        }

        /// <summary>
        /// RecordUnknown counts one send that returned but whose writer holds no readable message type (an empty writer,
        /// for one): it goes to other, and to the delivery, channel and refused totals as Record would.
        /// </summary>
        public void RecordUnknown(DeliveryMethod deliveryMethod, byte channel, int bytes, long elapsedTicks, bool refused)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                Interlocked.Increment(ref _offThread);
                return;
            }
            AddOther(bytes, elapsedTicks);
            AddRouting(deliveryMethod, channel, bytes, refused);
        }

        /// <summary>
        /// RecordTooBig counts one send the inner transport threw TooBigPacketException for. It moves only TooBigCalls and
        /// TooBigBytes: the window keeps no per-type or per-delivery breakdown of these, so <paramref name="type"/> and
        /// <paramref name="deliveryMethod"/> are taken for the caller's symmetry with Record and not stored.
        /// </summary>
        public void RecordTooBig(ushort type, DeliveryMethod deliveryMethod, int bytes)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                Interlocked.Increment(ref _offThread);
                return;
            }
            _tooBigCalls++;
            _tooBigBytes += bytes;
        }

        /// <summary>
        /// TakeWindow returns everything recorded since the previous window as a window of <paramref name="seconds"/>, and
        /// starts the next one empty. It allocates the window (once per window, never per send). Call it on the owner thread.
        /// </summary>
        public MeshSendWindow TakeWindow(double seconds)
        {
            int count = 0;
            for (int slot = 0; slot < TypeSlots; ++slot)
                if (_used[slot])
                    count++;
            ushort[] types = new ushort[count];
            long[] calls = new long[count];
            long[] bytes = new long[count];
            long[] ticks = new long[count];
            int next = 0;
            for (int slot = 0; slot < TypeSlots; ++slot)
            {
                if (!_used[slot])
                    continue;
                types[next] = _types[slot];
                calls[next] = _typeCalls[slot];
                bytes[next] = _typeBytes[slot];
                ticks[next] = _typeTicks[slot];
                next++;
            }
            MeshSendWindow window = new MeshSendWindow(seconds, _ticksPerSecond, types, calls, bytes, ticks,
                _otherCalls, _otherBytes, _otherTicks, (long[])_deliveryCalls.Clone(), (long[])_deliveryBytes.Clone(),
                (long[])_channelCalls.Clone(), _refused, _tooBigCalls, _tooBigBytes, Interlocked.Exchange(ref _offThread, 0));
            Array.Clear(_used, 0, TypeSlots);
            Array.Clear(_types, 0, TypeSlots);
            Array.Clear(_typeCalls, 0, TypeSlots);
            Array.Clear(_typeBytes, 0, TypeSlots);
            Array.Clear(_typeTicks, 0, TypeSlots);
            Array.Clear(_deliveryCalls, 0, _deliveryCalls.Length);
            Array.Clear(_deliveryBytes, 0, _deliveryBytes.Length);
            Array.Clear(_channelCalls, 0, _channelCalls.Length);
            _otherCalls = _otherBytes = _otherTicks = 0;
            _refused = _tooBigCalls = _tooBigBytes = 0;
            return window;
        }

        internal static int DeliveryIndex(DeliveryMethod deliveryMethod)
        {
            int value = (int)deliveryMethod;
            return value >= 0 && value < DeliverySlots ? value : DeliverySlots;
        }

        internal static int ChannelIndex(int channel)
        {
            return channel >= 0 && channel < ChannelSlots ? channel : ChannelSlots;
        }

        private int SlotFor(ushort type)
        {
            int start = type & TypeMask;
            for (int probe = 0; probe < TypeSlots; ++probe)
            {
                int slot = (start + probe) & TypeMask;
                if (!_used[slot])
                {
                    _used[slot] = true;
                    _types[slot] = type;
                    return slot;
                }
                if (_types[slot] == type)
                    return slot;
            }
            return -1;
        }

        private void AddOther(int bytes, long elapsedTicks)
        {
            _otherCalls++;
            _otherBytes += bytes;
            _otherTicks += elapsedTicks;
        }

        private void AddRouting(DeliveryMethod deliveryMethod, byte channel, int bytes, bool refused)
        {
            int delivery = DeliveryIndex(deliveryMethod);
            _deliveryCalls[delivery]++;
            _deliveryBytes[delivery] += bytes;
            _channelCalls[ChannelIndex(channel)]++;
            if (refused)
                _refused++;
        }
    }

    /// <summary>
    /// MeshSendWindow is one window of a <see cref="MeshSendMeter"/>: exact integer totals per message type, delivery
    /// method and channel, plus the refused, too-big and off-thread counts. Calls and bytes include refused sends and
    /// exclude too-big ones.
    /// </summary>
    public sealed class MeshSendWindow
    {
        /// <summary>LineTypeGroups is how many message types ToLine names, by bytes; the rest join its other group.</summary>
        public const int LineTypeGroups = 6;

        private readonly long _ticksPerSecond;
        private readonly ushort[] _types;
        private readonly long[] _calls;
        private readonly long[] _bytes;
        private readonly long[] _ticks;
        private readonly long _otherTicks;
        private readonly long[] _deliveryCalls;
        private readonly long[] _deliveryBytes;
        private readonly long[] _channelCalls;

        internal MeshSendWindow(double seconds, long ticksPerSecond, ushort[] types, long[] calls, long[] bytes, long[] ticks,
            long otherCalls, long otherBytes, long otherTicks, long[] deliveryCalls, long[] deliveryBytes, long[] channelCalls,
            long refused, long tooBigCalls, long tooBigBytes, int offThread)
        {
            Seconds = seconds;
            _ticksPerSecond = ticksPerSecond;
            _types = types;
            _calls = calls;
            _bytes = bytes;
            _ticks = ticks;
            OtherCalls = otherCalls;
            OtherBytes = otherBytes;
            _otherTicks = otherTicks;
            _deliveryCalls = deliveryCalls;
            _deliveryBytes = deliveryBytes;
            _channelCalls = channelCalls;
            Refused = refused;
            TooBigCalls = tooBigCalls;
            TooBigBytes = tooBigBytes;
            OffThread = offThread;
            long totalCalls = otherCalls, totalBytes = otherBytes;
            for (int i = 0; i < types.Length; ++i)
            {
                totalCalls += calls[i];
                totalBytes += bytes[i];
            }
            TotalCalls = totalCalls;
            TotalBytes = totalBytes;
        }

        /// <summary>Seconds is the window's length as the caller gave it.</summary>
        public double Seconds { get; }

        /// <summary>TotalCalls is every send that returned in the window, refused ones included.</summary>
        public long TotalCalls { get; }

        /// <summary>TotalBytes is the summed writer length of every send counted in TotalCalls.</summary>
        public long TotalBytes { get; }

        /// <summary>TypeCount is the number of message types the window keeps apart (at most MeshSendMeter.TypeSlots).</summary>
        public int TypeCount => _types.Length;

        /// <summary>OtherCalls is the sends whose type found no free slot, or had no readable type.</summary>
        public long OtherCalls { get; }

        /// <summary>OtherBytes is the summed writer length of the sends in OtherCalls.</summary>
        public long OtherBytes { get; }

        /// <summary>Refused is the sends the inner transport returned false for (also counted in every other total).</summary>
        public long Refused { get; }

        /// <summary>TooBigCalls is the sends the inner transport threw TooBigPacketException for (in no other total).</summary>
        public long TooBigCalls { get; }

        /// <summary>TooBigBytes is the summed writer length of the sends in TooBigCalls.</summary>
        public long TooBigBytes { get; }

        /// <summary>OffThread is the meter calls made from a thread other than the meter's owner, which changed nothing else.</summary>
        public int OffThread { get; }

        /// <summary>Calls is the sends of message <paramref name="type"/> in the window, or 0 when it kept no slot.</summary>
        public long Calls(ushort type)
        {
            int index = IndexOf(type);
            return index >= 0 ? _calls[index] : 0;
        }

        /// <summary>Bytes is the summed writer length of the sends of message <paramref name="type"/>, or 0 when it kept no slot.</summary>
        public long Bytes(ushort type)
        {
            int index = IndexOf(type);
            return index >= 0 ? _bytes[index] : 0;
        }

        /// <summary>MeanMicroseconds is the mean time per send of message <paramref name="type"/> inside the inner transport; 0 when untimed or absent.</summary>
        public double MeanMicroseconds(ushort type)
        {
            int index = IndexOf(type);
            return index >= 0 ? Micros(_ticks[index], _calls[index]) : 0d;
        }

        /// <summary>DeliveryCalls is the sends with <paramref name="deliveryMethod"/>; an undefined value reads the other slot.</summary>
        public long DeliveryCalls(DeliveryMethod deliveryMethod) => _deliveryCalls[MeshSendMeter.DeliveryIndex(deliveryMethod)];

        /// <summary>DeliveryBytes is the summed writer length of the sends with <paramref name="deliveryMethod"/>; an undefined value reads the other slot.</summary>
        public long DeliveryBytes(DeliveryMethod deliveryMethod) => _deliveryBytes[MeshSendMeter.DeliveryIndex(deliveryMethod)];

        /// <summary>ChannelCalls is the sends on <paramref name="channel"/>; a channel outside 0 to 15 reads the other slot.</summary>
        public long ChannelCalls(int channel) => _channelCalls[MeshSendMeter.ChannelIndex(channel)];

        /// <summary>
        /// ToLine formats the window as one "Send window" log line in invariant culture: the totals, up to
        /// <see cref="LineTypeGroups"/> message types by bytes (descending, ties by type ascending) and an other group
        /// holding the rest, refused and too-big counts, the delivery groups in enum order, the non-empty channels,
        /// <paramref name="wireBytes"/> and <paramref name="wirePackets"/> (the LiteNetLib server's sent-bytes and
        /// sent-datagram deltas over the window) and the off-thread count. Integer totals come before rates; kB is 1024 B;
        /// us is the mean microseconds per send inside the inner transport (0.00 untimed). A negative or non-finite figure
        /// prints as 0.
        /// </summary>
        public string ToLine(long wireBytes, long wirePackets)
        {
            double seconds = Seconds > 0d && !double.IsInfinity(Seconds) ? Seconds : 0d;
            int[] order = new int[_types.Length];
            for (int i = 0; i < order.Length; ++i)
                order[i] = i;
            for (int i = 1; i < order.Length; ++i)
            {
                int candidate = order[i];
                int j = i - 1;
                while (j >= 0 && Ranks(candidate, order[j]))
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = candidate;
            }

            StringBuilder line = new StringBuilder(512);
            line.Append(FormattableString.Invariant($"Send window {seconds:0.00} s: calls {Clean(TotalCalls)} {Rate(TotalCalls, seconds):0.0}/s bytes {Clean(TotalBytes)} {KiloRate(TotalBytes, seconds):0.0} kB/s"));
            int shown = Math.Min(LineTypeGroups, order.Length);
            for (int i = 0; i < shown; ++i)
            {
                int index = order[i];
                line.Append(FormattableString.Invariant($" | t{_types[index]}"));
                AppendGroup(line, _calls[index], _bytes[index], _ticks[index], seconds);
            }
            long restCalls = OtherCalls, restBytes = OtherBytes, restTicks = _otherTicks;
            for (int i = shown; i < order.Length; ++i)
            {
                int index = order[i];
                restCalls += _calls[index];
                restBytes += _bytes[index];
                restTicks += _ticks[index];
            }
            line.Append(" | other");
            AppendGroup(line, restCalls, restBytes, restTicks, seconds);
            line.Append(FormattableString.Invariant($" | refused {Clean(Refused)} tooBig {Clean(TooBigCalls)} bytes {Clean(TooBigBytes)}"));
            line.Append(FormattableString.Invariant($" | delivery ru {Clean(_deliveryCalls[0])} {Clean(_deliveryBytes[0])} seq {Clean(_deliveryCalls[1])} {Clean(_deliveryBytes[1])} ro {Clean(_deliveryCalls[2])} {Clean(_deliveryBytes[2])} rs {Clean(_deliveryCalls[3])} {Clean(_deliveryBytes[3])} u {Clean(_deliveryCalls[4])} {Clean(_deliveryBytes[4])} other {Clean(_deliveryCalls[5])} {Clean(_deliveryBytes[5])}"));
            line.Append(" | channels");
            for (int channel = 0; channel < MeshSendMeter.ChannelSlots; ++channel)
                if (_channelCalls[channel] > 0)
                    line.Append(FormattableString.Invariant($" {channel}:{_channelCalls[channel]}"));
            line.Append(FormattableString.Invariant($" other:{Clean(_channelCalls[MeshSendMeter.ChannelSlots])}"));
            line.Append(FormattableString.Invariant($" | wire {Clean(wireBytes)} B {Clean(wirePackets)} pkt | offThread {Clean(OffThread)}"));
            return line.ToString();
        }

        private void AppendGroup(StringBuilder line, long calls, long bytes, long ticks, double seconds)
        {
            line.Append(FormattableString.Invariant($" calls {Clean(calls)} bytes {Clean(bytes)} {Rate(calls, seconds):0.0}/s {KiloRate(bytes, seconds):0.0} kB/s {Micros(ticks, calls):0.00} us"));
        }

        private bool Ranks(int a, int b)
        {
            if (_bytes[a] != _bytes[b])
                return _bytes[a] > _bytes[b];
            return _types[a] < _types[b];
        }

        private int IndexOf(ushort type)
        {
            for (int i = 0; i < _types.Length; ++i)
                if (_types[i] == type)
                    return i;
            return -1;
        }

        private double Micros(long ticks, long calls)
        {
            if (ticks <= 0 || calls <= 0)
                return 0d;
            return ticks * 1_000_000d / _ticksPerSecond / calls;
        }

        private static double Rate(long value, double seconds) => seconds > 0d && value > 0 ? value / seconds : 0d;

        private static double KiloRate(long bytes, double seconds) => Rate(bytes, seconds) / 1024d;

        private static long Clean(long value) => value > 0 ? value : 0;
    }
}
