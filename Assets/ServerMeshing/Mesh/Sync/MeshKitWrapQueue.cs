// Server Meshing add-on — decides which kit movement handlers the state sender wraps, wraps them between kit ticks and
// hands each one back to the kit whenever wrapping is refused, lost or undone.
using System;
using System.Collections.Generic;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshKitWrapQueue wraps queued objects' kit movement handlers in MeshKitMovementStateSource and registers them with
    /// the fan-out. Objects are queued on spawn, when another layer hands a slot back, and when the kit evicted a wrapper;
    /// Drain runs before the kit's tick, so an object is always sent by exactly one of the kit and the sender. A refusal or
    /// a failed install leaves the kit sending the object. When not installing (measure mode) wrappers are registered but
    /// never put in the slot. Main thread only.
    /// </summary>
    public sealed class MeshKitWrapQueue
    {
        private readonly MeshMovementHandlerSlots _slots;
        private readonly MeshStateFanout _fanout;
        private readonly IMeshLog _log;
        private readonly bool _install;
        private readonly bool _gridDisabled;
        private readonly bool _wrapKit;

        private readonly HashSet<Type> _allowed = new HashSet<Type>();
        private readonly List<QueuedIdentity> _queue = new List<QueuedIdentity>();
        private readonly HashSet<uint> _queuedIds = new HashSet<uint>();
        private readonly Dictionary<uint, MeshKitMovementStateSource> _wrappers = new Dictionary<uint, MeshKitMovementStateSource>();
        private readonly List<uint> _wrappedIds = new List<uint>();
        private readonly HashSet<uint> _brokeAppendOnly = new HashSet<uint>();
        private readonly Dictionary<Type, Refusal> _loggedRefusals = new Dictionary<Type, Refusal>();
        private readonly Action<MeshKitMovementStateSource> _onReplaced;
        private bool _removing;

        /// <summary>
        /// MeshKitWrapQueue creates a queue over the manager's slots and the fan-out. <paramref name="install"/> false is
        /// measure mode; <paramref name="gridDisabled"/> false refuses every object (the kit's grid send path calls handlers
        /// itself); <paramref name="wrapKit"/> false refuses every object (kit sources are not sent by the sender).
        /// </summary>
        public MeshKitWrapQueue(MeshMovementHandlerSlots slots, MeshStateFanout fanout, IMeshLog log, bool install, bool gridDisabled, bool wrapKit)
        {
            _slots = slots ?? throw new ArgumentNullException(nameof(slots));
            _fanout = fanout ?? throw new ArgumentNullException(nameof(fanout));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _install = install;
            _gridDisabled = gridDisabled;
            _wrapKit = wrapKit;
            _onReplaced = OnReplaced;
        }

        [Flags]
        private enum Refusal
        {
            None = 0,
            KitSourcesOff = 1,
            GridPath = 2,
            DirectSource = 4,
            OtherLayer = 8,
            TypeNotAllowed = 16,
            BrokeAppendOnly = 32,
        }

        /// <summary>QueuedCount is the objects waiting for the next Drain.</summary>
        public int QueuedCount => _queue.Count;

        /// <summary>Wrapped is the wrappers registered now.</summary>
        public int Wrapped => _wrappedIds.Count;

        /// <summary>Rewraps is the wrappers lost to the kit's re-registration since the last TakeCounts.</summary>
        public int Rewraps { get; private set; }

        /// <summary>Refusals is the objects with a kit handler left on the kit path since the last TakeCounts.</summary>
        public int Refusals { get; private set; }

        /// <summary>AllowHandlerType lets handlers of exactly <paramref name="handlerType"/> be wrapped, besides the default ones.</summary>
        public void AllowHandlerType(Type handlerType)
        {
            if (handlerType == null)
                throw new ArgumentNullException(nameof(handlerType));
            _allowed.Add(handlerType);
        }

        /// <summary>IsAllowed is true for a handler type that may be wrapped: a default one or one allowed here, matched exactly.</summary>
        public bool IsAllowed(Type handlerType)
        {
            return handlerType != null && (MeshKitMovementStateSource.IsDefaultAllowed(handlerType) || _allowed.Contains(handlerType));
        }

        /// <summary>Enqueue queues an object for the next Drain, once per object id; null is ignored.</summary>
        public void Enqueue(LiteNetLibIdentity identity)
        {
            if (identity == null)
                return;
            uint objectId = identity.ObjectId;
            if (_queuedIds.Add(objectId))
                _queue.Add(new QueuedIdentity(identity, objectId));
        }

        /// <summary>
        /// Drain wraps every queued object that qualifies. Objects destroyed, re-identified, already registered or without
        /// a movement handler are skipped silently; the rest are wrapped or refused (logged once per reason and type).
        /// </summary>
        public void Drain()
        {
            int count = _queue.Count;
            for (int i = 0; i < count; ++i)
            {
                QueuedIdentity queued = _queue[i];
                _queuedIds.Remove(queued.ObjectId);
                Wrap(queued.Identity, queued.ObjectId);
            }
            _queue.RemoveRange(0, count);
        }

        /// <summary>
        /// CheckHeld finds the wrappers the kit evicted since the last build (its re-registration writes its own handler
        /// back), takes them out of the fan-out and queues their objects again. It returns how many it found.
        /// </summary>
        public int CheckHeld()
        {
            int lost = 0;
            for (int i = _wrappedIds.Count - 1; i >= 0; --i)
            {
                uint objectId = _wrappedIds[i];
                MeshKitMovementStateSource wrapper = _wrappers[objectId];
                bool held = _install
                    ? _slots.Holds(objectId, wrapper)
                    : _slots.TryGetKitHandler(objectId, out IEntityMovementDataHandler kit) && ReferenceEquals(kit, wrapper.Inner);
                if (held)
                    continue;
                Forget(i, objectId);
                _fanout.Unregister(wrapper);
                Enqueue(wrapper.Identity);
                Rewraps++;
                lost++;
            }
            return lost;
        }

        /// <summary>OnReplaced takes a wrapper out of the fan-out at once, so the layer that replaced it can register under the same id.</summary>
        public void OnReplaced(MeshKitMovementStateSource wrapper)
        {
            if (wrapper == null || !TryForget(wrapper, out _))
                return;
            _fanout.Unregister(wrapper);
        }

        /// <summary>OnLayerRemoved queues an object whose slot another layer handed back to the kit; nothing is wrapped until the next Drain.</summary>
        public void OnLayerRemoved(LiteNetLibIdentity identity)
        {
            if (!_removing)
                Enqueue(identity);
        }

        /// <summary>Forget drops the wrapper of a destroyed object, and its slot record when installed.</summary>
        public void Forget(uint objectId)
        {
            _brokeAppendOnly.Remove(objectId);
            if (!_wrappers.TryGetValue(objectId, out MeshKitMovementStateSource wrapper))
                return;
            Forget(_wrappedIds.IndexOf(objectId), objectId);
            _fanout.Unregister(wrapper);
            if (_install)
                _slots.Forget(objectId);
        }

        /// <summary>
        /// OnDropped is the fan-out's drop callback. A dropped wrapper gives its slot back to the kit handler (one that broke
        /// append-only is never wrapped again); a dropped sender-owned source goes back to being written by the kit.
        /// </summary>
        public void OnDropped(IMeshStateSource source, string reason)
        {
            if (source is IMeshSenderOwnedSource owned)
            {
                owned.OnSenderReleased();
                return;
            }
            if (!(source is MeshKitMovementStateSource wrapper) || !TryForget(wrapper, out uint objectId))
                return;
            bool brokeAppendOnly = reason == MeshStateFanout.DropReasonAppendOnly;
            if (brokeAppendOnly)
                _brokeAppendOnly.Add(objectId);
            if (!_install)
                return;
            if (!brokeAppendOnly)
            {
                _slots.Forget(objectId);
                return;
            }
            _removing = true;
            try
            {
                if (!_slots.TryRemove(objectId, wrapper, out string error))
                    _log.Warning($"State sender: object {objectId} was dropped but its slot was not handed back: {error}");
            }
            finally
            {
                _removing = false;
            }
        }

        /// <summary>Clear forgets everything at server stop; the slots and the kit's dictionary are reset by their owners, so nothing is written back.</summary>
        public void Clear()
        {
            _queue.Clear();
            _queuedIds.Clear();
            _wrappers.Clear();
            _wrappedIds.Clear();
            _brokeAppendOnly.Clear();
            _loggedRefusals.Clear();
            Rewraps = 0;
            Refusals = 0;
        }

        /// <summary>TakeCounts returns Rewraps and Refusals and zeroes them.</summary>
        public void TakeCounts(out int rewraps, out int refusals)
        {
            rewraps = Rewraps;
            refusals = Refusals;
            Rewraps = 0;
            Refusals = 0;
        }

        private void Wrap(LiteNetLibIdentity identity, uint objectId)
        {
            if (identity == null || identity.IsDestroyed || identity.ObjectId != objectId || _fanout.IsRegistered(objectId))
                return;
            if (!_slots.TryGetKitHandler(objectId, out IEntityMovementDataHandler kit) || (kit is UnityEngine.Object kitObject ? kitObject == null : kit == null))
                return;
            Refusal refusal = RefusalOf(objectId, kit);
            if (refusal != Refusal.None)
            {
                Refusals++;
                LogRefusal(objectId, kit.GetType(), refusal);
                return;
            }

            MeshKitMovementStateSource wrapper = new MeshKitMovementStateSource(kit, identity, _onReplaced);
            if (_install && !_slots.TryInstall(objectId, wrapper, true, out string installError))
            {
                _log.Warning($"State sender: object {objectId} not wrapped: {installError}");
                return;
            }
            if (!_fanout.Register(wrapper, out string registerError))
            {
                _log.Error($"State sender error: object {objectId} ({kit.GetType().Name}) was not registered and stays on the kit path: {registerError}");
                if (_install)
                    HandBack(objectId, wrapper);
                return;
            }
            _wrappers.Add(objectId, wrapper);
            _wrappedIds.Add(objectId);
        }

        private Refusal RefusalOf(uint objectId, IEntityMovementDataHandler kit)
        {
            if (!_wrapKit)
                return Refusal.KitSourcesOff;
            if (!_gridDisabled)
                return Refusal.GridPath;
            if (kit is IMeshStateSource)
                return Refusal.DirectSource;
            if (_slots.HasLayer(objectId))
                return Refusal.OtherLayer;
            if (!IsAllowed(kit.GetType()))
                return Refusal.TypeNotAllowed;
            if (_brokeAppendOnly.Contains(objectId))
                return Refusal.BrokeAppendOnly;
            return Refusal.None;
        }

        private void LogRefusal(uint objectId, Type handlerType, Refusal refusal)
        {
            _loggedRefusals.TryGetValue(handlerType, out Refusal logged);
            if ((logged & refusal) != 0)
                return;
            _loggedRefusals[handlerType] = logged | refusal;
            _log.Warning($"State sender: object {objectId} ({handlerType.Name}) stays on the kit path: {Describe(refusal)}");
        }

        private static string Describe(Refusal refusal)
        {
            switch (refusal)
            {
                case Refusal.KitSourcesOff: return "kit sources are not sent by the sender";
                case Refusal.GridPath: return "the kit's grid send path is enabled";
                case Refusal.DirectSource: return "its handler is a state source of its own";
                case Refusal.OtherLayer: return "another Mesh layer holds its slot";
                case Refusal.TypeNotAllowed: return "its handler type is not allowed";
                default: return "it broke append-only earlier";
            }
        }

        private void HandBack(uint objectId, MeshKitMovementStateSource wrapper)
        {
            _removing = true;
            try
            {
                if (!_slots.TryRemove(objectId, wrapper, out string error))
                    _log.Warning($"State sender: object {objectId} could not be handed back to its kit handler: {error}");
            }
            finally
            {
                _removing = false;
            }
        }

        private bool TryForget(MeshKitMovementStateSource wrapper, out uint objectId)
        {
            for (int i = 0; i < _wrappedIds.Count; ++i)
            {
                objectId = _wrappedIds[i];
                if (ReferenceEquals(_wrappers[objectId], wrapper))
                {
                    Forget(i, objectId);
                    return true;
                }
            }
            objectId = 0;
            return false;
        }

        private void Forget(int index, uint objectId)
        {
            _wrappers.Remove(objectId);
            int last = _wrappedIds.Count - 1;
            _wrappedIds[index] = _wrappedIds[last];
            _wrappedIds.RemoveAt(last);
        }

        private readonly struct QueuedIdentity
        {
            public QueuedIdentity(LiteNetLibIdentity identity, uint objectId)
            {
                Identity = identity;
                ObjectId = objectId;
            }

            public LiteNetLibIdentity Identity { get; }
            public uint ObjectId { get; }
        }
    }
}
