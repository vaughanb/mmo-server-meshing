// Server Meshing add-on — Setup window, the Validate panel's lines for the settings a smooth crossing rests on
// (DESIGN.md 1.34). The judging is MeshCrossingSetupChecks (pure, tested).
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public partial class ServerMeshSetupWindow
    {
        /// <summary>Each map manager on a prefab: a warning with a Fix for Steered puppets and for a low network tick.</summary>
        private void AddCrossingChecks(string path, GameObject prefab)
        {
            bool anyStaticMap = _registry == null || _registry.maps.Exists(map => map != null && !map.IsDynamic);
            foreach (MeshMapNetworkManager manager in prefab.GetComponentsInChildren<MeshMapNetworkManager>(true))
            {
                string capturedPath = path;
                if (MeshCrossingSetupChecks.SteeredShows(manager.puppetMotion, anyStaticMap))
                {
                    _checks.Add(new Check
                    {
                        warning = true,
                        text = $"{ShortPath(path)}: {MeshCrossingSetupChecks.SteeredText}",
                        fixLabel = "Set Kinematic",
                        fix = () => SetInt(capturedPath, typeof(MeshMapNetworkManager), nameof(MeshMapNetworkManager.puppetMotion), (int)MeshPuppetMotion.Kinematic),
                    });
                }
                if (MeshCrossingSetupChecks.RateIsLow(manager.updateFps))
                {
                    _checks.Add(new Check
                    {
                        warning = true,
                        text = $"{ShortPath(path)}: {MeshCrossingSetupChecks.LowNetworkTickText(manager.updateFps)}",
                        fixLabel = "Set 60",
                        fix = () => SetInt(capturedPath, typeof(MeshMapNetworkManager), nameof(MeshMapNetworkManager.updateFps), MeshCrossingSetupChecks.RecommendedRate),
                    });
                }
            }
        }
    }
}
