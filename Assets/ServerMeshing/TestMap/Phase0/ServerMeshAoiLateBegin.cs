// Server Meshing add-on — rig AOI timing (MESH_AOI_TIMING): the stamp taken just before the timed AOI's LateUpdate.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshAoiLateBegin stamps the start of the timed AOI's LateUpdate, the completion half of an AOI pass (job
    /// completion, subscription update, owner changes). It runs one script order below ServerMeshTimedAoi; its partner
    /// ServerMeshAoiLateEnd runs one above and marks the bracket on the work meter.
    /// </summary>
    [DefaultExecutionOrder(ServerMeshTimedAoi.ExecutionOrder - 1)]
    public class ServerMeshAoiLateBegin : MonoBehaviour
    {
        /// <summary>Stamp is the Stopwatch timestamp of this component's latest LateUpdate.</summary>
        public long Stamp { get; private set; }

        /// <summary>Frame is the Time.frameCount of that LateUpdate, -1 before the first.</summary>
        public int Frame { get; private set; } = -1;

        private void LateUpdate()
        {
            Frame = Time.frameCount;
            Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
