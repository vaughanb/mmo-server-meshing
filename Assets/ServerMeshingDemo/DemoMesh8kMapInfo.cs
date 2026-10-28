// Server Meshing test rig — registers a map info the game database does not list, so the demo copy reaches the 8 km map
// without the demo's database changing. Sits in DemoMesh8kInit (ServerMeshDemo8kBuilder); every process of the copy
// boots that scene (editor, central, map servers), so all of them know the map.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>DemoMesh8kMapInfo adds its map infos to the GameInstance once the game data has loaded.</summary>
    [DefaultExecutionOrder(-10000)] // subscribed before the GameInstance starts loading
    public sealed class DemoMesh8kMapInfo : MonoBehaviour
    {
        public BaseMapInfo[] mapInfos = new BaseMapInfo[0];

        private void Awake()
        {
            GameInstance.OnGameDataLoadedEvent += Add;
            if (GameInstance.MapInfos.Count > 0)
                Add(); // the data loaded before this scene did
        }

        private void OnDestroy()
        {
            GameInstance.OnGameDataLoadedEvent -= Add;
        }

        private void Add()
        {
            GameInstance.AddMapInfos(mapInfos);
        }
    }
}
