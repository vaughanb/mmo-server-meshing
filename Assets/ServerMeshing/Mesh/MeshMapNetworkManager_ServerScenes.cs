// Server Meshing add-on — server-only scenes (DESIGN 1.41). A map's layout may name scenes its cells load additively
// beside the map scene and a client never loads. Content a client has no use for goes there, spawn areas above all: an
// 8 km map's 2,300 of them were the whole cost of a client's seam pre-load (one 87-101 ms frame integrating objects the
// client never reads, DESIGN 1.40). The cells load them before anything reads the map's spawn areas (the ownership
// filter, the prefab registration, the kit's SpawnFirstTime), so an area in a server scene is one of the map's like any
// other. A map without server scenes is loaded as before, in the same frame.
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>
        /// LoadServerScenes loads the own map's server-only scenes (MeshMapLayout.serverScenes) additively, on a server
        /// only; the caller awaits it before anything reads the scene. One already loaded is kept; one not in the build is
        /// skipped with a warning. Without server scenes it completes at once, and the caller goes on in the same frame.
        /// </summary>
        private async UniTask LoadServerScenes()
        {
            if (!IsServer || world == null)
                return;
            MeshMapLayout layout = world.GetLayout(OwnMapId);
            if (layout == null || layout.serverScenes == null || layout.serverScenes.Length == 0)
                return;
            for (int i = 0; i < layout.serverScenes.Length; ++i)
            {
                string sceneName = layout.serverScenes[i];
                if (string.IsNullOrWhiteSpace(sceneName))
                    continue;
                Scene loaded = SceneManager.GetSceneByName(sceneName);
                if (loaded.IsValid() && loaded.isLoaded)
                    continue;
                if (!Application.CanStreamedLevelBeLoaded(sceneName))
                {
                    Logging.LogWarning("Mesh", $"Server scene {sceneName} of map {OwnMapId} is not in the build; its content is missing on this cell");
                    continue;
                }
                float began = Time.realtimeSinceStartup;
                AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                while (op != null && !op.isDone)
                    await UniTask.Yield();
                loaded = SceneManager.GetSceneByName(sceneName);
                Logging.Log("Mesh", $"Server scene {sceneName} loaded beside {SceneManager.GetActiveScene().name} in {(Time.realtimeSinceStartup - began) * 1000f:0} ms ({(loaded.IsValid() ? loaded.rootCount : 0)} roots)");
            }
        }
    }
}
