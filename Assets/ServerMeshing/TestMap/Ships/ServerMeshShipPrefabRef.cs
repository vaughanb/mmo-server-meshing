// Server Meshing test rig — the one Resources asset that reaches the stand-in ship prefab (Phase 0 step 7). Modelled
// on nothing in the space game (commit 1a3a58d9 has no such asset): a player build includes only referenced assets,
// and the prefab itself must stay out of Resources, where the kit's ResourcesFolderGameDatabase would register it.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipPrefabRef holds the stand-in ship prefab. The prefab builder saves one at
    /// TestMap/Resources/ServerMeshShipPrefabRef.asset; ServerMeshShipEntity.LoadPrefab reads it only under a ship key.
    /// </summary>
    public sealed class ServerMeshShipPrefabRef : ScriptableObject
    {
        /// <summary>prefab is the saved ServerMeshShip prefab (its root carries ServerMeshShipEntity).</summary>
        public GameObject prefab;
    }
}
