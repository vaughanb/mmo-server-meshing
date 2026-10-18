// Server Meshing add-on — the mesh link waits for the server scene, on every map (DESIGN.md 0.79, after Slice 2d).
// A map server listens on its mesh link in OnStartServer, before its scene is loaded, and the kit's Assets.Initialize
// (RegisterPrefabs, then RegisterSceneObjects, which takes every root identity of the loaded scenes for a scene object)
// runs only once the scene is. A neighbour that still holds this cell's address from its last incarnation (a restart on
// the same port, a cell of another machine that outlived a Play) links within a second of the listen, and its snapshots
// then spawn puppets the kit captures as scene objects and re-spawns under ids no client has. Slice 2d held the link on
// Dynamic maps only; the September two-machine logs show the same early puppets on a Static map's cells.
// MeshMapNetworkManager_Readiness wires this to the server.

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshLinkHold says whether a map server reads its mesh link yet: not between a server start and the moment its
    /// server scene is ready (OnServerOnlineSceneLoaded, after the kit's Assets.Initialize), whatever the map's mode.
    /// Nothing is dropped meanwhile: the link's events wait in its transport and are read once the hold ends.
    /// </summary>
    public sealed class MeshLinkHold
    {
        /// <summary>A hold this long gets one warning (the link stays held: the scene is still not ready).</summary>
        public const float WarnAfterSeconds = 30f;

        private bool _warned;

        /// <summary>IsReady is whether this start's server scene is ready (the link is read).</summary>
        public bool IsReady { get; private set; }
        /// <summary>When this start began (unscaled seconds).</summary>
        public float StartedAt { get; private set; }
        /// <summary>The frames held since this start (the release line reports them).</summary>
        public int HeldFrames { get; private set; }

        /// <summary>Start: a server start, before the kit's; its scene is not ready yet.</summary>
        public void Start(float now)
        {
            IsReady = false;
            StartedAt = now;
            HeldFrames = 0;
            _warned = false;
        }

        /// <summary>SceneReady: the server scene is ready; true only the first time per start (the release line).</summary>
        public bool SceneReady()
        {
            if (IsReady)
                return false;
            IsReady = true;
            return true;
        }

        /// <summary>
        /// Tick: true while a server holds its link this frame. <paramref name="warn"/> is set once per start, the first
        /// frame the hold has lasted WarnAfterSeconds. A client is never held.
        /// </summary>
        public bool Tick(bool isServer, float now, out bool warn)
        {
            warn = false;
            if (IsReady || !isServer)
                return false;
            HeldFrames++;
            if (!_warned && now - StartedAt >= WarnAfterSeconds)
            {
                _warned = true;
                warn = true;
            }
            return true;
        }
    }
}
