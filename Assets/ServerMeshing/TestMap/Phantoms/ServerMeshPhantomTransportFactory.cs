// Server Meshing add-on — rig phantom transport factory: builds a ServerMeshPhantomTransport around the factory it wraps, and keeps phantom numbers across server restarts.
using System;
using System.Collections.Generic;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomTransportFactory builds a fresh <see cref="ServerMeshPhantomTransport"/> on every Build (the kit
    /// builds a new transport on every StartServer and StartClient), around a transport from <see cref="Inner"/>. A null
    /// Inner becomes the kit's default factory once. Phantom numbers continue past every number a transport it built has
    /// given (client transports included), so an id is never reused while the process lives. It sets nothing up in Awake:
    /// the installer configures it right after adding it.
    /// </summary>
    public class ServerMeshPhantomTransportFactory : BaseTransportFactory
    {
        /// <summary>Inner is the factory whose transports are wrapped; when null, Build adds the kit's default factory and keeps it here.</summary>
        public BaseTransportFactory Inner;

        private IMeshLog _log;
        private readonly List<ServerMeshPhantomTransport> _built = new List<ServerMeshPhantomTransport>();
        private ServerMeshPhantomTransport _last;
        private bool _building;

        /// <summary>Log is where built transports log; the kit's log when none was set.</summary>
        public IMeshLog Log
        {
            get => _log ?? (_log = new MeshKitLog());
            set => _log = value;
        }

        /// <summary>NextK is the number the next phantom of the next built transport gets: past every number any transport it built has given.</summary>
        public long NextK
        {
            get
            {
                long next = 0;
                for (int i = 0; i < _built.Count; ++i)
                    next = Math.Max(next, _built[i].NextK);
                return next;
            }
        }

        /// <summary>LastBuilt is the transport this factory built most recently, or null.</summary>
        public ServerMeshPhantomTransport LastBuilt => _last;

        /// <summary>
        /// Build returns a new ServerMeshPhantomTransport around Inner's product, numbering from <see cref="NextK"/>. It
        /// throws InvalidOperationException when Inner is this factory or the factory chain comes back to this one.
        /// </summary>
        public override ITransport Build()
        {
            if (_building)
                throw new InvalidOperationException($"the transport factory chain under {gameObject.name} cycles back to the phantom factory");
            if (Inner == this)
                throw new InvalidOperationException($"the phantom transport factory under {gameObject.name} is its own inner factory");
            if (Inner == null)
                Inner = MeshMeteredTransportFactory.KitDefault(gameObject);
            _building = true;
            try
            {
                ServerMeshPhantomTransport built = new ServerMeshPhantomTransport(Inner.Build(), Log, NextK);
                _built.Add(built);
                _last = built;
                return built;
            }
            finally
            {
                _building = false;
            }
        }
    }
}
