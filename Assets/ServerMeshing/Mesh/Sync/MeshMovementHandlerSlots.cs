// Server Meshing add-on — the one owner of Mesh layers in the kit's movement-handler slots: puts a layer in front of an
// object's kit handler and hands the slot back, never overwriting an entry the kit changed underneath it.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshMovementHandlerSlots installs Mesh movement-handler layers into the kit's handler dictionary
    /// (BaseGameNetworkManager.EntityMovementDataHandlers), at most one layer per object id, and gives the slot back to the
    /// kit's handler when the layer leaves. The kit rewrites entries by itself (it registers on spawn, removes on destroy and
    /// clears everything on stop) and an object id can be reused, so every write is a compare-and-swap: an entry the kit
    /// changed is never overwritten, and a kit handler is never written back into it. A record keeps the kit's original
    /// handler, even after one layer replaced another. Main thread only.
    /// </summary>
    public sealed class MeshMovementHandlerSlots
    {
        private static readonly ConditionalWeakTable<BaseGameNetworkManager, MeshMovementHandlerSlots> s_byManager =
            new ConditionalWeakTable<BaseGameNetworkManager, MeshMovementHandlerSlots>();
        private static readonly ConditionalWeakTable<BaseGameNetworkManager, MeshMovementHandlerSlots>.CreateValueCallback s_create = CreateFor;

        private readonly ConcurrentDictionary<uint, IEntityMovementDataHandler> _handlers;
        private readonly Dictionary<uint, LayerRecord> _records = new Dictionary<uint, LayerRecord>();

        /// <summary>MeshMovementHandlerSlots manages the layers of one handler dictionary.</summary>
        public MeshMovementHandlerSlots(ConcurrentDictionary<uint, IEntityMovementDataHandler> handlers)
        {
            _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        }

        /// <summary>
        /// LayerRemoved is raised once, with the object id, after TryRemove handed a slot back to its kit handler. Forget,
        /// Reset and a TryRemove that finds the entry changed by the kit never raise it.
        /// </summary>
        public event Action<uint> LayerRemoved;

        /// <summary>For is the manager's slots, created on first use and kept for as long as the manager lives.</summary>
        public static MeshMovementHandlerSlots For(BaseGameNetworkManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            return s_byManager.GetValue(manager, s_create);
        }

        /// <summary>
        /// TryInstall puts <paramref name="layer"/> in the slot of object <paramref name="id"/>. It fails, naming the id, when
        /// the object has no handler (not spawned yet, or destroyed), when the layer is already there, or when the slot holds
        /// a Mesh layer installed as not replaceable. Over a replaceable layer it installs and then calls that layer's
        /// OnReplaced once. A layer installed as <paramref name="replaceable"/> must implement IMeshReplaceableHandlerLayer.
        /// </summary>
        public bool TryInstall(uint id, IEntityMovementDataHandler layer, bool replaceable, out string error)
        {
            if (layer == null)
                throw new ArgumentNullException(nameof(layer));
            if (replaceable && !(layer is IMeshReplaceableHandlerLayer))
                throw new ArgumentException($"a replaceable layer for object {id} must implement IMeshReplaceableHandlerLayer, and {layer.GetType().Name} does not", nameof(layer));
            if (!_handlers.TryGetValue(id, out IEntityMovementDataHandler current) || current == null)
            {
                _records.Remove(id);
                error = $"object {id} has no movement handler in its slot (not spawned yet, or destroyed)";
                return false;
            }
            if (ReferenceEquals(current, layer))
            {
                error = $"object {id} already has this {layer.GetType().Name} in its slot";
                return false;
            }

            IEntityMovementDataHandler kit = current;
            IEntityMovementDataHandler replaced = null;
            if (_records.TryGetValue(id, out LayerRecord record))
            {
                if (!ReferenceEquals(record.Layer, current))
                {
                    // The kit re-registered or replaced the entry since: its current value is the kit's handler now.
                    _records.Remove(id);
                }
                else if (!record.Replaceable)
                {
                    error = $"object {id} already has a Mesh layer {current.GetType().Name} that cannot be replaced";
                    return false;
                }
                else
                {
                    kit = record.Kit;
                    replaced = current;
                }
            }

            if (!_handlers.TryUpdate(id, layer, current))
            {
                error = $"object {id} changed its movement handler while {layer.GetType().Name} was being installed; nothing was written";
                return false;
            }
            _records[id] = new LayerRecord(kit, layer, replaceable);
            error = null;
            if (replaced != null)
                ((IMeshReplaceableHandlerLayer)replaced).OnReplaced();
            return true;
        }

        /// <summary>
        /// TryRemove takes <paramref name="layer"/> out of the slot of object <paramref name="id"/> and puts the kit's original
        /// handler back, then raises LayerRemoved. It fails, naming the id, when the object has no layer or another layer.
        /// When the kit removed or replaced the entry meanwhile, it forgets the layer, writes nothing and returns false.
        /// </summary>
        public bool TryRemove(uint id, IEntityMovementDataHandler layer, out string error)
        {
            if (layer == null)
                throw new ArgumentNullException(nameof(layer));
            if (!_records.TryGetValue(id, out LayerRecord record))
            {
                error = $"object {id} has no Mesh layer to remove";
                return false;
            }
            if (!ReferenceEquals(record.Layer, layer))
            {
                error = $"object {id} holds another Mesh layer {record.Layer.GetType().Name}, which only its installer removes";
                return false;
            }
            _records.Remove(id);
            if (!_handlers.TryUpdate(id, record.Kit, layer))
            {
                error = $"object {id} had its movement handler removed or replaced by the kit under its {layer.GetType().Name}; nothing was written back";
                return false;
            }
            error = null;
            LayerRemoved?.Invoke(id);
            return true;
        }

        /// <summary>
        /// TryGetKitHandler is the kit's own handler for object <paramref name="id"/>: the one a layer stands in front of, or
        /// the slot's value when no layer does. False when the object has no handler.
        /// </summary>
        public bool TryGetKitHandler(uint id, out IEntityMovementDataHandler kit)
        {
            if (!_handlers.TryGetValue(id, out IEntityMovementDataHandler current) || current == null)
            {
                kit = null;
                return false;
            }
            kit = _records.TryGetValue(id, out LayerRecord record) && ReferenceEquals(record.Layer, current) ? record.Kit : current;
            return true;
        }

        /// <summary>HasLayer is true while a layer this instance installed still sits in the slot of object <paramref name="id"/>.</summary>
        public bool HasLayer(uint id)
        {
            return _records.TryGetValue(id, out LayerRecord record)
                && _handlers.TryGetValue(id, out IEntityMovementDataHandler current)
                && ReferenceEquals(record.Layer, current);
        }

        /// <summary>
        /// Forget drops the layer record of object <paramref name="id"/> (its entity is gone) and removes the slot's entry only
        /// if it still holds that layer. It never writes a kit handler back, so a reused id's new handler stays.
        /// </summary>
        public void Forget(uint id)
        {
            if (!_records.TryGetValue(id, out LayerRecord record))
                return;
            _records.Remove(id);
            ((ICollection<KeyValuePair<uint, IEntityMovementDataHandler>>)_handlers).Remove(new KeyValuePair<uint, IEntityMovementDataHandler>(id, record.Layer));
        }

        /// <summary>Reset drops every layer record without touching the dictionary (the kit clears it when the server stops).</summary>
        public void Reset()
        {
            _records.Clear();
        }

        private static MeshMovementHandlerSlots CreateFor(BaseGameNetworkManager manager)
        {
            return new MeshMovementHandlerSlots(manager.EntityMovementDataHandlers);
        }

        private readonly struct LayerRecord
        {
            public LayerRecord(IEntityMovementDataHandler kit, IEntityMovementDataHandler layer, bool replaceable)
            {
                Kit = kit;
                Layer = layer;
                Replaceable = replaceable;
            }

            public IEntityMovementDataHandler Kit { get; }
            public IEntityMovementDataHandler Layer { get; }
            public bool Replaceable { get; }
        }
    }
}
