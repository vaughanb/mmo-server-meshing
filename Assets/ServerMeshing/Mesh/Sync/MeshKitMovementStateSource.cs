// Server Meshing add-on — stands in front of a kit movement handler so the state sender writes it instead of the kit.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshKitMovementStateSource is a kit movement handler wrapped as a state source. Installed in the handler slot, it
    /// answers the kit's own send loop with false, so the kit neither sends nor records it, and lets the sender write the
    /// inner handler once per build instead. Everything else (uploads, owner, client reads) goes to the inner handler. It
    /// never implements the kit's one-shot flags interface: the sender records and consumes the inner handler itself.
    /// </summary>
    public sealed class MeshKitMovementStateSource : IEntityMovementDataHandler, IMeshStateSource, IMeshReplaceableHandlerLayer
    {
        private readonly Action<MeshKitMovementStateSource> _replaced;

        /// <summary>
        /// MeshKitMovementStateSource wraps <paramref name="inner"/>, the kit handler of <paramref name="identity"/>;
        /// <paramref name="replaced"/> runs when another layer takes the slot. The inner handler must not be a state source.
        /// </summary>
        public MeshKitMovementStateSource(IEntityMovementDataHandler inner, LiteNetLibIdentity identity, Action<MeshKitMovementStateSource> replaced)
        {
            if (inner is UnityEngine.Object innerObject ? innerObject == null : inner == null)
                throw new ArgumentNullException(nameof(inner));
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));
            if (replaced == null)
                throw new ArgumentNullException(nameof(replaced));
            if (inner is IMeshStateSource)
                throw new ArgumentException($"object {identity.ObjectId}: a {inner.GetType().Name} is a state source already and is never wrapped", nameof(inner));
            Inner = inner;
            Identity = identity;
            _replaced = replaced;
        }

        /// <summary>Inner is the wrapped kit handler.</summary>
        public IEntityMovementDataHandler Inner { get; }

        /// <summary>ObjectId is the inner handler's object id.</summary>
        public uint ObjectId => Inner.ObjectId;

        /// <summary>ConnectionId is the inner handler's owner, read live, so the kit accepts the owner's uploads.</summary>
        public long ConnectionId => Inner.ConnectionId;

        /// <summary>Identity is the wrapped object's network identity.</summary>
        public LiteNetLibIdentity Identity { get; }

        /// <summary>DiscreteVersion is always 0: a kit handler's one-shots come out of its write as reliable records instead.</summary>
        public uint DiscreteVersion => 0;

        /// <summary>IsAtRest is always false: a kit handler says nothing about its velocity.</summary>
        public bool IsAtRest => false;

        /// <summary>CreateMovementData is the inner handler's.</summary>
        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            return Inner.CreateMovementData(out forceAppliers);
        }

        /// <summary>ReadClientStateAtServer passes the owner's upload to the inner handler.</summary>
        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
            Inner.ReadClientStateAtServer(peerTimestamp, reader);
        }

        /// <summary>ReadServerStateAtClient is the inner handler's.</summary>
        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            Inner.ReadServerStateAtClient(peerTimestamp, reader);
        }

        /// <summary>WriteClientState is the inner handler's.</summary>
        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            return Inner.WriteClientState(writeTimestamp, writer, out shouldSendReliably);
        }

        /// <summary>WriteServerState answers the kit's send loop: false, nothing written, so the kit skips the object.</summary>
        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        /// <summary>WriteState is the inner handler's server write, for the sender.</summary>
        public bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)
        {
            return Inner.WriteServerState(sampleTimestamp, writer, out sendReliably);
        }

        /// <summary>OnReplaced tells the owner of this wrapper that another layer took its slot.</summary>
        public void OnReplaced()
        {
            _replaced(this);
        }

        /// <summary>IsDefaultAllowed is true for exactly the kit handlers whose writes only read their one-shot flags (no subclass).</summary>
        public static bool IsDefaultAllowed(Type handlerType)
        {
            return handlerType == typeof(BuiltInEntityMovementFunctions3D) || handlerType == typeof(NavMeshEntityMovement);
        }
    }
}
