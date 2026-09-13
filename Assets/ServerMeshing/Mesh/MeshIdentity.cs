// Server Meshing add-on — MeshIdentity (DESIGN.md §8 "Global identity").
//
// EXTENSION-ONLY. A plain (non-networked) tag added at runtime to entities the mesh cares about:
//  * MeshEntityId — stable, owner-independent id: the character id for players; for server-spawned
//    entities a birth-provenance id "{zoneId}:{cellIncarnation}:{localObjectId}" that encodes WHERE
//    it was born (immutable), never the current owner;
//  * IsGhost — this instance is a read-only replica driven by a neighbour cell.
// Kept as a MonoBehaviour rather than a LiteNetLibBehaviour so no entity prefab has to change; clients
// never need it (they see ghosts as ordinary entities).

using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshIdentity : MonoBehaviour
    {
        public string MeshEntityId;
        public bool IsGhost;
        public string OwnerZoneId;
        /// <summary>For monsters: the zone whose spawn area respawns it (set when it is handed to another cell).</summary>
        public string HomeZoneId;
        /// <summary>Unscaled time of the last ownership change (monster handoff), for the ping-pong cooldown.</summary>
        public float LastHandoffTime = -999f;

        public static MeshIdentity Get(Component entity)
        {
            return entity != null ? entity.GetComponent<MeshIdentity>() : null;
        }

        public static MeshIdentity GetOrAdd(Component entity)
        {
            MeshIdentity id = entity.GetComponent<MeshIdentity>();
            if (id == null)
                id = entity.gameObject.AddComponent<MeshIdentity>();
            return id;
        }

        public static bool IsGhostEntity(Component entity)
        {
            MeshIdentity id = Get(entity);
            return id != null && id.IsGhost;
        }
    }
}
