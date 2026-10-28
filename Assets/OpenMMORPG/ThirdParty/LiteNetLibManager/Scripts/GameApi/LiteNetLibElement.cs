using Cysharp.Text;
using UnityEngine;

namespace LiteNetLibManager
{
    public abstract class LiteNetLibElement
    {
        private bool _isSetup;

        [ReadOnly, SerializeField]
        protected LiteNetLibBehaviour _behaviour;
        public LiteNetLibBehaviour Behaviour
        {
            get { return _behaviour; }
        }

        public LiteNetLibIdentity Identity
        {
            get { return Behaviour.Identity; }
        }

        public bool IsSpawned
        {
            get { return _isSetup && Identity.IsSpawned; }
        }

        // MESH-EDIT: CE-14 — the element belongs to a kept object a migrating client's new server is adopting (its
        // initial state is then applied as a diff). False for an element with no behaviour or identity behind it.
        public bool IsBeingAdopted
        {
            get { return _behaviour != null && _behaviour.Identity != null && _behaviour.Identity.IsBeingAdopted; }
        }

        public bool IsDestroyed
        {
            get { return _isSetup && Identity.IsDestroyed; }
        }

        public long ConnectionId
        {
            get { return !_isSetup ? -1 : Identity.ConnectionId; }
        }

        public uint ObjectId
        {
            get { return !_isSetup ? 0 : Identity.ObjectId; }
        }

        public byte SyncChannelId
        {
            get { return !_isSetup ? (byte)0 : Identity.SyncChannelId; }
        }

        public LiteNetLibGameManager Manager
        {
            get { return !_isSetup ? null : Identity.Manager; }
        }

        public LiteNetLibPlayer Player
        {
            get { return !_isSetup ? null : Identity.Player; }
        }

        public bool IsServer
        {
            get { return _isSetup && Identity.IsServer; }
        }

        public bool IsClient
        {
            get { return _isSetup && Identity.IsClient; }
        }

        public bool IsOwnerClient
        {
            get { return _isSetup && Identity.IsOwnerClient; }
        }

        public bool IsOwnerHost
        {
            get { return _isSetup && Identity.IsOwnerHost; }
        }

        public bool IsOwnedByServer
        {
            get { return _isSetup && Identity.IsOwnedByServer; }
        }

        public bool IsOwnerClientOrOwnedByServer
        {
            get { return _isSetup && Identity.IsOwnerClientOrOwnedByServer; }
        }

        public bool IsSceneObject
        {
            get { return _isSetup && Identity.IsSceneObject; }
        }

        [ReadOnly, SerializeField]
        protected int _elementId;
        public int ElementId
        {
            get { return _elementId; }
        }

        public string logTagName = null;
        public virtual string LogTag
        {
            get
            {
                using (var stringBuilder = ZString.CreateStringBuilder(false))
                {
                    if (_isSetup)
                    {
                        stringBuilder.Append(Behaviour.LogTag);
                    }
                    else
                    {
                        stringBuilder.Append(LiteNetLibBehaviour.TAG_NULL);
                    }
                    stringBuilder.Append('.');
                    stringBuilder.Append('E');
                    stringBuilder.Append('_');
                    stringBuilder.Append(GetType().Name);
                    stringBuilder.Append('_');
                    stringBuilder.Append(logTagName);
                    return stringBuilder.ToString();
                }
            }
        }

        public bool isDebug = false;

        public LiteNetLibElementInfo GetInfo()
        {
            return new LiteNetLibElementInfo()
            {
                objectId = ObjectId,
                elementId = ElementId,
            };
        }

        internal virtual void Setup(LiteNetLibBehaviour behaviour, int elementId)
        {
            _behaviour = behaviour;
            _elementId = elementId;
            _isSetup = true;
        }
    }
}
