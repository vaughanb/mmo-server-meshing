// Server Meshing add-on — rig AOI timing (MESH_AOI_TIMING): the stamp taken just after the timed AOI's LateUpdate.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshAoiLateEnd closes the bracket its ServerMeshAoiLateBegin opened around the timed AOI's LateUpdate and
    /// marks it on the work meter as AOI completion work. It runs one script order above ServerMeshTimedAoi, and only
    /// in a frame whose begin stamp was taken.
    /// </summary>
    [DefaultExecutionOrder(ServerMeshTimedAoi.ExecutionOrder + 1)]
    public class ServerMeshAoiLateEnd : MonoBehaviour
    {
        private ServerMeshAoiLateBegin _begin;

        /// <summary>PairWith sets the begin stamp this component closes.</summary>
        public void PairWith(ServerMeshAoiLateBegin begin)
        {
            if (begin == null)
                throw new ArgumentNullException(nameof(begin));
            _begin = begin;
        }

        private void LateUpdate()
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_begin == null || _begin.Frame != Time.frameCount)
                return; // no bracket opened this frame
            MeshWorkProbe.Meter.MarkAoiComplete(_begin.Stamp, end);
        }
    }
}
