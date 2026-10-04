using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLibManager;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakeIdentities makes kit identities on hidden GameObjects (one each) with an object id and subscribers, sets
    /// owners by reflection (the setters are internal to the kit's networking assembly), and destroys them all on Dispose.
    /// </summary>
    internal sealed class MeshFakeIdentities : IDisposable
    {
        private static readonly PropertyInfo s_objectId = typeof(LiteNetLibIdentity).GetProperty(nameof(LiteNetLibIdentity.ObjectId));
        private static readonly PropertyInfo s_connectionId = typeof(LiteNetLibIdentity).GetProperty(nameof(LiteNetLibIdentity.ConnectionId));

        private readonly List<GameObject> _objects = new List<GameObject>();

        public LiteNetLibIdentity New(uint objectId, params long[] subscribers)
        {
            GameObject go = new GameObject("mesh-state-identity-" + objectId) { hideFlags = HideFlags.HideAndDontSave };
            _objects.Add(go);
            LiteNetLibIdentity identity = go.AddComponent<LiteNetLibIdentity>();
            s_objectId.SetValue(identity, objectId);
            foreach (long subscriber in subscribers)
                identity.AddSubscriber(subscriber);
            return identity;
        }

        public static void SetOwner(LiteNetLibIdentity identity, long connectionId)
        {
            s_connectionId.SetValue(identity, connectionId);
        }

        public static void SetObjectId(LiteNetLibIdentity identity, uint objectId)
        {
            s_objectId.SetValue(identity, objectId);
        }

        public Transform TransformOf(LiteNetLibIdentity identity)
        {
            return identity.transform;
        }

        public void Destroy(LiteNetLibIdentity identity)
        {
            Object.DestroyImmediate(identity.gameObject);
        }

        public void Dispose()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _objects.Clear();
        }
    }
}
