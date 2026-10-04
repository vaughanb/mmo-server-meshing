// Server Meshing add-on — rig phantom view error (Phase 0 step 6): what a rotating tenth of the virtual phantoms last
// received for each object they see, compared once per frame with where the server holds that object now.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomViewSampler keeps, for each sampled phantom connection, the newest record position per object it
    /// was sent (decoded from the swallowed EntityState packets) and the frame that record arrived on. Sample adds the X/Z
    /// distance between such a position and the object's server position now, for entries refreshed within the age limit
    /// only, to a phantom window as viewErr. Connections are tracked by id (bind and leave are warm-up); each has a fixed
    /// open-addressed table of <see cref="TableSize"/> entries that overflows by counting, never by growing. A table is
    /// cleared when its connection enters the sample. Main thread only; nothing allocates per call.
    /// </summary>
    public sealed class ServerMeshPhantomViewSampler
    {
        /// <summary>TableSize is how many objects one connection's table holds.</summary>
        public const int TableSize = 1024;
        /// <summary>MinRecordBytes is the shortest kit-layout record read: one flags byte, the extra byte and the position.</summary>
        public const int MinRecordBytes = 14;

        private const ushort EntityStateType = 119;
        private const int TableMask = TableSize - 1;

        private readonly int _maxObservers;
        private readonly int _seed;
        private readonly Dictionary<long, int> _indexOf;
        private readonly List<long> _tracked;
        private readonly Stack<int> _free;
        private readonly bool[] _sampled;
        private readonly bool[] _wasSampled;
        private readonly bool[] _occupied;
        private readonly uint[] _ids;
        private readonly float[] _x;
        private readonly float[] _z;
        private readonly int[] _frames;
        private int _cursorObserver;
        private int _cursorEntry;

        /// <summary>
        /// ServerMeshPhantomViewSampler preallocates tables for <paramref name="maxObservers"/> connections (at least 1).
        /// <paramref name="seed"/> offsets the rotating sample so runs with different seeds start on different phantoms.
        /// </summary>
        public ServerMeshPhantomViewSampler(int maxObservers, int seed)
        {
            if (maxObservers < 1)
                throw new ArgumentOutOfRangeException(nameof(maxObservers), maxObservers, "the view sampler needs room for at least one connection");
            _maxObservers = maxObservers;
            _seed = seed;
            _indexOf = new Dictionary<long, int>(maxObservers);
            _tracked = new List<long>(maxObservers);
            _free = new Stack<int>(maxObservers);
            for (int i = maxObservers - 1; i >= 0; --i)
                _free.Push(i);
            _sampled = new bool[maxObservers];
            _wasSampled = new bool[maxObservers];
            int entries = maxObservers * TableSize;
            _occupied = new bool[entries];
            _ids = new uint[entries];
            _x = new float[entries];
            _z = new float[entries];
            _frames = new int[entries];
        }

        /// <summary>TrackedCount is how many connections are tracked.</summary>
        public int TrackedCount => _tracked.Count;
        /// <summary>Overflow is how many record positions found their connection's table full.</summary>
        public long Overflow { get; private set; }
        /// <summary>Truncated is how many swallowed EntityState packets ended inside their header or a record.</summary>
        public long Truncated { get; private set; }
        /// <summary>TrackRefused is how many Track calls found every table taken.</summary>
        public int TrackRefused { get; private set; }

        /// <summary>Track starts following a bound phantom connection; a second call for it, or one with every table taken, changes nothing.</summary>
        public void Track(long connectionId)
        {
            if (_indexOf.ContainsKey(connectionId))
                return;
            if (_free.Count == 0)
            {
                TrackRefused++;
                return;
            }
            int index = _free.Pop();
            _indexOf.Add(connectionId, index);
            _tracked.Add(connectionId);
            _sampled[index] = false;
        }

        /// <summary>Untrack stops following a connection that left; unknown ids are ignored.</summary>
        public void Untrack(long connectionId)
        {
            if (!_indexOf.TryGetValue(connectionId, out int index))
                return;
            _indexOf.Remove(connectionId);
            _tracked.Remove(connectionId);
            _sampled[index] = false;
            _free.Push(index);
        }

        /// <summary>
        /// BeginWindow chooses sample number <paramref name="round"/>: a tenth of the tracked connections (at least one),
        /// rotating through all of them over successive rounds. A connection entering the sample starts with an empty table.
        /// </summary>
        public void BeginWindow(int round)
        {
            int count = _tracked.Count;
            int size = ServerMeshPhantomWindow.SampleSize(count);
            int start = ServerMeshPhantomWindow.SampleStart(Math.Max(0, round) + PositiveSeed(), count);
            Array.Copy(_sampled, _wasSampled, _maxObservers);
            Array.Clear(_sampled, 0, _maxObservers);
            for (int n = 0; n < size; ++n)
            {
                int index = _indexOf[_tracked[(start + n) % count]];
                if (!_wasSampled[index])
                    Array.Clear(_occupied, index * TableSize, TableSize);
                _sampled[index] = true;
            }
            _cursorObserver = 0;
            _cursorEntry = 0;
        }

        /// <summary>IsSampled is true for a tracked connection in the current sample.</summary>
        public bool IsSampled(long connectionId) => _indexOf.TryGetValue(connectionId, out int index) && _sampled[index];

        /// <summary>
        /// OnSwallowed reads one swallowed packet sent to <paramref name="connectionId"/>: for an EntityState packet of a
        /// sampled connection it stores, for every record of at least <see cref="MinRecordBytes"/> bytes, the record's kit
        /// position with <paramref name="frame"/>. Decoding stops at a record length the packet cannot hold. Anything else
        /// is ignored. Ship records (step 7) are not in the kit layout and decode as garbage positions here; they are never
        /// sampled, because the truth knows no position for an object whose handler is not a kit movement.
        /// </summary>
        public void OnSwallowed(long connectionId, byte[] data, int length, int frame)
        {
            if (data == null || !_indexOf.TryGetValue(connectionId, out int index) || !_sampled[index])
                return;
            int end = Math.Min(length, data.Length);
            if (!MeshSendMeter.TryReadMessageType(data, 0, end, out ushort type, out int typeBytes) || type != EntityStateType)
                return;
            int at = typeBytes;
            if (!SkipPacked(data, end, ref at) || !SkipPacked(data, end, ref at) || end - at < 4)
            {
                Truncated++;
                return;
            }
            int count = ReadInt(data, at);
            at += 4;
            int table = index * TableSize;
            for (int r = 0; r < count; ++r)
            {
                if (!TryReadPacked(data, end, ref at, out uint objectId) || end - at < 4)
                {
                    Truncated++;
                    return;
                }
                int recordLength = ReadInt(data, at);
                at += 4;
                if (recordLength < 0 || recordLength > end - at)
                {
                    Truncated++;
                    return;
                }
                int recordEnd = at + recordLength;
                if (recordLength >= MinRecordBytes)
                {
                    int field = at;
                    if (TryReadPacked(data, recordEnd, ref field, out _) && recordEnd - field >= 13)
                        Store(table, objectId, ReadFloat(data, field + 1), ReadFloat(data, field + 9), frame);
                }
                at = recordEnd;
            }
        }

        /// <summary>
        /// Sample adds to <paramref name="window"/> up to <paramref name="maxEntries"/> viewErr samples, round-robin over the
        /// sampled connections' entries: the X/Z distance between an entry's position and <paramref name="truth"/>'s
        /// position of its object, for entries at most <paramref name="maxAgeFrames"/> frames older than
        /// <paramref name="frame"/> whose object the truth knows. It visits every entry at most once per call and returns
        /// how many samples it added.
        /// </summary>
        public int Sample(int maxEntries, IServerMeshPhantomTruth truth, ServerMeshPhantomWindow window, int frame, int maxAgeFrames)
        {
            if (truth == null || window == null || maxEntries <= 0)
                return 0;
            int added = 0;
            int total = _maxObservers * TableSize;
            for (int visited = 0; visited < total && added < maxEntries; ++visited)
            {
                if (_cursorEntry >= TableSize)
                {
                    _cursorEntry = 0;
                    _cursorObserver = _cursorObserver + 1 >= _maxObservers ? 0 : _cursorObserver + 1;
                }
                if (!_sampled[_cursorObserver])
                {
                    visited += TableSize - _cursorEntry - 1;
                    _cursorEntry = TableSize;
                    continue;
                }
                int slot = _cursorObserver * TableSize + _cursorEntry++;
                if (!_occupied[slot] || frame - _frames[slot] > maxAgeFrames)
                    continue;
                if (!truth.TryGetPosition(_ids[slot], out Vector3 position))
                    continue;
                float dx = position.x - _x[slot], dz = position.z - _z[slot];
                window.AddViewErr(Mathf.Sqrt(dx * dx + dz * dz));
                added++;
            }
            return added;
        }

        private int PositiveSeed() => _seed & int.MaxValue;

        private void Store(int table, uint objectId, float x, float z, int frame)
        {
            int start = (int)(objectId * 2654435761u) & TableMask;
            for (int probe = 0; probe < TableSize; ++probe)
            {
                int slot = table + ((start + probe) & TableMask);
                if (_occupied[slot] && _ids[slot] != objectId)
                    continue;
                _occupied[slot] = true;
                _ids[slot] = objectId;
                _x[slot] = x;
                _z[slot] = z;
                _frames[slot] = frame;
                return;
            }
            Overflow++;
        }

        private static int ReadInt(byte[] data, int at) => data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);

        private static float ReadFloat(byte[] data, int at) => BitConverter.ToSingle(data, at);

        private static bool SkipPacked(byte[] data, int end, ref int at) => TryReadPacked(data, end, ref at, out _);

        private static bool TryReadPacked(byte[] data, int end, ref int at, out uint value)
        {
            value = 0;
            if (at >= end)
                return false;
            int lead = data[at];
            int length = lead < 241 ? 1 : lead <= 248 ? 2 : lead == 249 ? 3 : lead == 250 ? 4 : lead == 251 ? 5 : 0;
            if (length == 0 || end - at < length)
                return false;
            switch (length)
            {
                case 1:
                    value = (uint)lead;
                    break;
                case 2:
                    value = (uint)(240 + 256 * (lead - 241) + data[at + 1]);
                    break;
                case 3:
                    value = (uint)(2288 + 256 * data[at + 1] + data[at + 2]);
                    break;
                case 4:
                    value = (uint)(data[at + 1] | (data[at + 2] << 8) | (data[at + 3] << 16));
                    break;
                default:
                    value = (uint)(data[at + 1] | (data[at + 2] << 8) | (data[at + 3] << 16) | (data[at + 4] << 24));
                    break;
            }
            at += length;
            return true;
        }
    }
}
