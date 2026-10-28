// Server Meshing test rig — a day/night clock that never moves: the demo copy (DemoMesh8kInit) is always at `hour`, so
// the demo's sky cycle on the 8 km map stays in daylight. Every process of the copy boots that scene and agrees; a time
// sent by a server running another clock is ignored.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>FixedTimeOfDayUpdater holds the time of day at one hour.</summary>
    [CreateAssetMenu(fileName = "FixedTimeOfDay", menuName = "Open MMORPG/Server Meshing/Fixed Time Of Day")]
    public class FixedTimeOfDayUpdater : BaseDayNightTimeUpdater
    {
        [Range(0f, 24f)]
        public float hour = 12f;

        public override void InitTimeOfDay(BaseGameNetworkManager manager)
        {
            TimeOfDay = hour;
        }

        public override void UpdateTimeOfDay(float deltaTime)
        {
            TimeOfDay = hour;
        }

        public override void SetTimeOfDay(float timeOfDay)
        {
            TimeOfDay = hour;
        }
    }
}
