// Server Meshing add-on — rig phantom eye: the follower on an eye phantom's hidden identity, which keeps it on its host.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomEye sits on an eye phantom's runtime GameObject next to its LiteNetLibIdentity and moves it onto its
    /// host's position (offset 0) every LateUpdate. The binder hides the identity from everyone but its owner with
    /// SetIsHide before spawning it, so overlapping the host costs nothing. It has no LiteNetLibBehaviour and sets nothing up
    /// in Awake.
    /// </summary>
    public class ServerMeshPhantomEye : MonoBehaviour
    {
        private Transform _host;

        /// <summary>Host is the transform the eye follows, or null while it has none.</summary>
        public Transform Host => _host;

        /// <summary>Identity is the eye's LiteNetLibIdentity, set by <see cref="Create"/>.</summary>
        public LiteNetLibIdentity Identity { get; private set; }

        /// <summary>
        /// Create builds an eye's runtime GameObject at position, not yet spawned: a LiteNetLibIdentity hidden from everyone
        /// but its owner (SetIsHide with this follower as the setter; HideExceptions stays empty, because the kit tests it
        /// against the hidden object's own connection id and an entry there would unhide it from everyone) and the follower.
        /// The hide must be in place before the spawn, which subscribes the ready players in range at once.
        /// </summary>
        public static ServerMeshPhantomEye Create(string name, Vector3 position, HideFlags hideFlags)
        {
            GameObject go = new GameObject(name) { hideFlags = hideFlags };
            go.transform.position = position;
            LiteNetLibIdentity identity = go.AddComponent<LiteNetLibIdentity>();
            ServerMeshPhantomEye eye = go.AddComponent<ServerMeshPhantomEye>();
            identity.SetIsHide(eye, true);
            eye.Identity = identity;
            return eye;
        }

        /// <summary>Follow makes the eye follow host from now on (null stops it where it is).</summary>
        public void Follow(Transform host)
        {
            _host = host;
            if (host != null)
                transform.position = host.position;
        }

        private void LateUpdate()
        {
            if (_host != null)
                transform.position = _host.position;
        }
    }
}
