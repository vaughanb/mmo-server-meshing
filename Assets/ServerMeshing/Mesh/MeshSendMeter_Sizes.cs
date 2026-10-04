// Server Meshing add-on — the send meter's optional record sizes (MESH_METER_RECORD_SIZES=1): each EntityState send's
// records walked into a MeshRecordSizeCounts, handed to the window and printed as its sizes group.
using System;
using System.Threading;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshSendMeter
    {
        private MeshRecordSizeCounts _recordSizes;

        /// <summary>
        /// RecordSizes turns the record walk on (set once at install). Setting it true creates the counts; false drops
        /// them, so windows carry no sizes group.
        /// </summary>
        public bool RecordSizes
        {
            get => _recordSizes != null;
            set
            {
                if (!value)
                    _recordSizes = null;
                else if (_recordSizes == null)
                    _recordSizes = new MeshRecordSizeCounts();
            }
        }

        /// <summary>
        /// RecordEntityStateSizes walks one EntityState packet in data[offset..end) into the record sizes; it does nothing
        /// while RecordSizes is off. Called from another thread it counts only in OffThread, as Record does. It allocates
        /// nothing.
        /// </summary>
        public void RecordEntityStateSizes(byte[] data, int offset, int end)
        {
            if (_recordSizes == null)
                return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                Interlocked.Increment(ref _offThread);
                return;
            }
            _recordSizes.TryAddPacket(data, offset, end);
        }

        private MeshRecordSizeCounts TakeRecordSizes()
        {
            if (_recordSizes == null)
                return null;
            MeshRecordSizeCounts taken = new MeshRecordSizeCounts();
            _recordSizes.CopyTo(taken);
            _recordSizes.Clear();
            return taken;
        }
    }

    public sealed partial class MeshSendWindow
    {
        /// <summary>RecordSizes is the window's record sizes, or null when the meter does not count them.</summary>
        public MeshRecordSizeCounts RecordSizes { get; internal set; }
    }
}
