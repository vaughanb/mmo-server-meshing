// Server Meshing test rig — the kit demo's LineOfSightMonsterActivityComponent with MeshMonsterActivityComponent's rule:
// a player ghost near the monster (a player standing across the cell edge) keeps its AI awake, as a subscribed player
// would. The demo's monsters keep their line-of-sight targeting on the 8 km map (ServerMeshLargeMapBuilder puts this on
// its meshed copies of them).
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshLineOfSightMonsterActivityComponent : LineOfSightMonsterActivityComponent
    {
        [Tooltip("Extra range beyond the monster visual range in which a player ghost keeps this monster awake.")]
        public float ghostWatchMargin = 10f;
        [Tooltip("How often the ghost proximity is re-evaluated (seconds).")]
        public float ghostWatchInterval = 0.5f;

        private float _nextGhostCheck;
        private bool _ghostWatched;

        protected override bool IsWatched()
        {
            if (base.IsWatched())
                return true;
            if (Time.unscaledTime >= _nextGhostCheck)
            {
                _nextGhostCheck = Time.unscaledTime + ghostWatchInterval;
                MeshMapNetworkManager manager = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                float range = (CharacterDatabase != null ? CharacterDatabase.VisualRange : 0f) + ghostWatchMargin;
                _ghostWatched = manager != null && manager.IsPlayerGhostNear(Entity.EntityTransform.position, range);
            }
            return _ghostWatched;
        }
    }
}
