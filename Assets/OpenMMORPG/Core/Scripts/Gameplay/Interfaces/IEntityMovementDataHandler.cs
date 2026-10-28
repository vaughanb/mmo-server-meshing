using LiteNetLib.Utils;
using Newtonsoft.Json.Bson;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    public interface IEntityMovementDataHandler
    {
        uint ObjectId { get; }
        long ConnectionId { get; }
        //Create movement data from current entity state, this is used to create movement data and server
        MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers);
        void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader);
        void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader);
        bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably);
        bool WriteServerState(long writeTimestamp, NetDataWriter writer,out bool shouldSendReliably);
    }

    /// <summary>
    /// A data handler whose client reads server states with EntityMovementFunctions.ClientReadSyncTransformMessage3D,
    /// so the quantized send path (DefaultGridManagerComponent) may send its states from CreateMovementData.
    /// That path keeps calling WriteServerState for every other handler, since their clients read their own format.
    /// </summary>
    public interface IQuantizedEntityMovementDataHandler
    {
        /// <summary>
        /// Whether the handler has a state to send yet. False wherever WriteServerState would return false.
        /// </summary>
        bool CanWriteQuantizedServerState { get; }
    }

    // The one-shot jump, dash, teleport and still-move flags.
    // The server send loops write each entity once per receiver, and the kit cleared these flags inside
    // that write, so only the first player written in a tick got them. A data handler with such flags
    // implements this: its writes only read them, and the loop consumes them once per tick.
    public interface IEntityMovementServerStateFlags
    {
        /// <summary>
        /// Clears the one-shot flags this tick's server writes carried. Called by the server send loop
        /// once per tick, after its last player, on each handler it wrote; never from a write.
        /// </summary>
        void ConsumeServerStateFlags();
    }

    /// <summary>
    /// The handlers one server send pass wrote. Begin, Record
    /// after every successful write (a handler written to many players is kept once), ConsumeAll after
    /// the last player. A handler written to no one keeps its flags for a later tick, so an owner still
    /// gets its teleport, and so does a flag raised after the pass.
    /// </summary>
    public sealed class EntityMovementServerStateFlagsPass
    {
        private readonly HashSet<IEntityMovementServerStateFlags> _written = new HashSet<IEntityMovementServerStateFlags>();

        public int RecordedCount => _written.Count;

        /// <summary>
        /// Starts a pass. Drops, unconsumed, what a pass that threw before ConsumeAll left behind, so
        /// those flags go out again next tick (at least once, never lost).
        /// </summary>
        public void Begin()
        {
            _written.Clear();
        }

        public void Record(IEntityMovementDataHandler handler)
        {
            if (handler is IEntityMovementServerStateFlags flags)
                _written.Add(flags);
        }

        public void ConsumeAll()
        {
            foreach (IEntityMovementServerStateFlags flags in _written)
                flags.ConsumeServerStateFlags();
            _written.Clear();
        }
    }
}
