// Server Meshing test rig — registers the demo copy's meshed monster and harvestable prefabs with every process, since
// the demo's database does not list them (DESIGN 1.40). A cell registers the prefabs its own scene's spawn areas use, so
// two maps with different populations knew different monsters: a bandit chasing a player over the seam reached a cell
// that had never heard of its prefab (`unknown monster entity id`), the handoff was refused and the bandit was lost. Any
// entity that can cross a seam (as a monster handed over, or as a puppet in the band) must be known to both sides; a
// game whose entities are in its database has that already. Sits in DemoMesh8kInit beside DemoMesh8kMapInfo
// (ServerMeshDemo8kBuilder lists every prefab under Monsters/ and Harvestables/); every process of the copy boots that scene.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>DemoMesh8kEntities adds its monster and harvestable prefabs to the GameInstance once the game data has loaded.</summary>
    [DefaultExecutionOrder(-10000)] // subscribed before the GameInstance starts loading
    public sealed class DemoMesh8kEntities : MonoBehaviour
    {
        public BaseMonsterCharacterEntity[] monsters = new BaseMonsterCharacterEntity[0];
        public HarvestableEntity[] harvestables = new HarvestableEntity[0];

        private void Awake()
        {
            GameInstance.OnGameDataLoadedEvent += Add;
            if (GameInstance.Items.Count > 0)
                Add(); // the data loaded before this scene did
        }

        private void OnDestroy()
        {
            GameInstance.OnGameDataLoadedEvent -= Add;
        }

        private void Add()
        {
            GameInstance.AddMonsterCharacterEntities(monsters);
            GameInstance.AddHarvestableEntities(harvestables);
        }
    }
}
