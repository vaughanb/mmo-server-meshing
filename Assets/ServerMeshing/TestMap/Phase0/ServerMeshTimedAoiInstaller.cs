// Server Meshing add-on — rig AOI timing (MESH_AOI_TIMING=1): puts ServerMeshTimedAoi in place of the kit's grid AOI on a map server.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshTimedAoiInstaller swaps the map server's JobifiedGridSpatialPartitioningAOI for a ServerMeshTimedAoi,
    /// bracketed by ServerMeshAoiLateBegin and ServerMeshAoiLateEnd, once the server is up, when MESH_AOI_TIMING=1
    /// (never in the editor). The new components go on a child of the manager, because the interest manager base class
    /// allows one per GameObject. The kit's settings are copied before the assignment, whose setter builds the grid at
    /// once. The kit's component is disabled, never destroyed: the kit falls back to it when its interest manager is
    /// missing. The swap runs in this LateUpdate, at the last script order, so the kit component's own LateUpdate has
    /// already completed any query it started this frame. Afterwards this component stays and watches: if the timed
    /// component is ever destroyed, the kit's is enabled and assigned again.
    /// </summary>
    [DefaultExecutionOrder(int.MaxValue)]
    public class ServerMeshTimedAoiInstaller : MonoBehaviour
    {
        /// <summary>ENV_AOI_TIMING is the environment key; "1" installs the timed AOI on a map server.</summary>
        public const string ENV_AOI_TIMING = "MESH_AOI_TIMING";

        /// <summary>ChildName names the child GameObject of the manager that carries the timed AOI and its late stamps.</summary>
        public const string ChildName = "[Mesh] TimedAoi";

        private bool _installed;
        private MeshMapNetworkManager _mesh;
        private JobifiedGridSpatialPartitioningAOI _kitAoi;
        private ServerMeshTimedAoi _timed;
        private GameObject _child;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (System.Environment.GetEnvironmentVariable(ENV_AOI_TIMING) != "1")
                return;
            if (Application.isEditor)
            {
                Logging.LogWarning("Mesh", $"AOI timing: {ENV_AOI_TIMING}=1 is ignored in the editor; nothing installed");
                return;
            }
            if (FindFirstObjectByType<ServerMeshTimedAoiInstaller>() != null)
                return;
            GameObject go = new GameObject("[Mesh] TimedAoiInstaller");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshTimedAoiInstaller>();
        }

        private void LateUpdate()
        {
            if (!_installed)
            {
                if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer)
                    return;
                _installed = TryInstall(mesh);
                if (!_installed)
                    enabled = false; // refused, and logged: nothing to install or watch
                return;
            }
            if (_timed == null)
            {
                FallBack();
                enabled = false;
            }
        }

        private bool TryInstall(MeshMapNetworkManager mesh)
        {
            string cell = CellOf(mesh);
            BaseInterestManager current = mesh.InterestManager;
            if (current is ServerMeshTimedAoi)
            {
                Logging.LogWarning("Mesh", $"AOI timing: cell {cell}: the interest manager on {mesh.name} is already {nameof(ServerMeshTimedAoi)}; nothing installed");
                return false;
            }
            if (!(current is JobifiedGridSpatialPartitioningAOI kitAoi))
            {
                string actual = current == null ? "none" : current.GetType().FullName;
                Logging.LogWarning("Mesh", $"AOI timing: cell {cell}: the interest manager on {mesh.name} is {actual}, not {nameof(JobifiedGridSpatialPartitioningAOI)}; nothing installed");
                return false;
            }
            if (ServerMeshTimedAoi.SystemField == null)
            {
                Logging.LogError("Mesh", $"AOI timing: cell {cell}: {nameof(JobifiedGridSpatialPartitioningAOI)} has no private reference-type field {ServerMeshTimedAoi.SystemFieldName}; nothing installed");
                return false;
            }

            GameObject child = new GameObject(ChildName);
            child.transform.SetParent(mesh.transform, false);
            ServerMeshAoiLateBegin begin = child.AddComponent<ServerMeshAoiLateBegin>();
            ServerMeshTimedAoi timed = child.AddComponent<ServerMeshTimedAoi>();
            if (timed == null)
            {
                Logging.LogError("Mesh", $"AOI timing: cell {cell}: Unity refused {nameof(ServerMeshTimedAoi)} on {mesh.name}/{ChildName}; nothing installed");
                Destroy(child);
                return false;
            }
            child.AddComponent<ServerMeshAoiLateEnd>().PairWith(begin);

            CopySettings(kitAoi, timed);
            mesh.InterestManager = timed; // the setter runs Setup at once, which builds the grid from the copied settings
            kitAoi.enabled = false;

            _mesh = mesh;
            _kitAoi = kitAoi;
            _timed = timed;
            _child = child;
            Logging.Log("Mesh", InstalledLine(cell, mesh.name, timed));
            return true;
        }

        private static void CopySettings(JobifiedGridSpatialPartitioningAOI from, JobifiedGridSpatialPartitioningAOI to)
        {
            to.cellSize = from.cellSize;
            to.maxObjects = from.maxObjects;
            to.updateInterval = from.updateInterval;
            to.bufferedCells = from.bufferedCells;
            to.boundsGizmosColor = from.boundsGizmosColor;
            to.defaultVisibleRange = from.defaultVisibleRange;
        }

        private static string InstalledLine(string cell, string managerName, ServerMeshTimedAoi timed)
        {
            Vector3 buffered = timed.bufferedCells;
            string system = timed.SystemReady ? "ready" : "pending until the online scene loads";
            return System.FormattableString.Invariant(
                $"AOI timing: installed on cell {cell} ({managerName}/{ChildName}): cellSize {timed.cellSize:0.###} maxObjects {timed.maxObjects} updateInterval {timed.updateInterval:0.###} s bufferedCells {buffered.x:0.###} {buffered.y:0.###} {buffered.z:0.###} visibleRange {timed.defaultVisibleRange:0.###} | grid system {system} | kit {nameof(JobifiedGridSpatialPartitioningAOI)} disabled, kept as the fallback");
        }

        private void FallBack()
        {
            if (_kitAoi == null)
            {
                Logging.Log("Mesh", "AOI timing: the map server manager is gone; nothing to restore");
                return;
            }
            string cell = CellOf(_mesh);
            _kitAoi.enabled = true;
            _mesh.InterestManager = _kitAoi;
            if (_child != null)
                Destroy(_child); // its late stamps would time nothing now
            Logging.LogError("Mesh", $"AOI timing: cell {cell}: {nameof(ServerMeshTimedAoi)} on {_mesh.name} was destroyed; the kit's {nameof(JobifiedGridSpatialPartitioningAOI)} is enabled and assigned again");
        }

        private static string CellOf(MeshMapNetworkManager mesh)
        {
            return mesh.HasOwnZone ? mesh.OwnZoneId : "unresolved";
        }
    }
}
