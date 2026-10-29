// Server Meshing add-on — the settings of the remote characters' motion buffer (MeshRemoteMotionBuffer, DESIGN.md 1.17).
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Client: draw other characters from their states' server timestamps instead of the kit's one-tick lerp per state (DESIGN 1.17). MESH_REMOTE_BUFFER=0 turns it off.")]
        public bool bufferRemoteMotion = true;

        [Tooltip("Client: how far behind the newest state other characters are drawn, in sample intervals (at least 20 ms).")]
        public float remoteMotionDelayIntervals = 1.5f;

        [Tooltip("Client: the longest another character is walked on past its newest state, in seconds; past it, it holds.")]
        public float remoteMotionMaxExtrapolationSeconds = 0.1f;
    }
}
