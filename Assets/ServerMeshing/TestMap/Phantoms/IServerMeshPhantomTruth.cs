// Server Meshing add-on — rig phantom view error: where the server holds an object now, for the view sampler's comparison.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>IServerMeshPhantomTruth gives the server position of an object whose records the view sampler compares.</summary>
    public interface IServerMeshPhantomTruth
    {
        /// <summary>
        /// TryGetPosition is the object's server position now; false for an object that is gone or whose records are not
        /// in the kit's movement layout (so they are never compared).
        /// </summary>
        bool TryGetPosition(uint objectId, out Vector3 position);
    }
}
