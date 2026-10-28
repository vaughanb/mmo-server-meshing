using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The kit's harvestable spawn area, except that a node is only ever put on a spot
    /// where it fits.
    ///
    /// The kit chooses first and looks second. It takes the next baked spot in order, or
    /// one at random, stands the node there and only then checks for anything within the
    /// node's detection radius; if something is there it logs two warnings, a stack trace
    /// each, and tries again `respawnPendingEntitiesDelay` later at whatever spot comes up
    /// next. The demo's areas are its forest as designed - one spot per tree, every spot
    /// filled - so once a wood is standing the only free spot is the one a felled tree
    /// left, and the kit walked the ring of taken spots to find it: up to one attempt per
    /// tree in the area, five seconds and two warnings apiece.
    ///
    /// This looks first. It runs the kit's own test - the node's detection radius against
    /// the eight layers <see cref="HarvestableSpawnArea.IsOverlapSomethingNearby"/> will not
    /// spawn over - across the spots, and hands the kit one that will pass. When none will,
    /// because a player is standing on the one spot left or a campfire has been built over
    /// it, it returns nothing and says nothing, and the kit asks again after the delay as
    /// it would have anyway.
    ///
    /// The builder keeps the other half of the bargain: an area never holds more nodes than
    /// its spots can stand at once. See DemoSceneBuilder.ThinNodeSpots.
    /// </summary>
    public class FreeSpotHarvestableSpawnArea : HarvestableSpawnArea
    {
        /// <summary>
        /// How many times running an area may find no room before it says so. The kit comes
        /// back to a pending node every `respawnPendingEntitiesDelay`, five seconds, so for a
        /// single node this is a minute of looking.
        /// </summary>
        private const int MissesBeforeReport = 12;

        // Set while the kit places a node, so its own call for a position is answered with
        // the spot already found to be free.
        private bool _placing;
        private Vector3 _freeSpot;
        private int _missesInARow;
        private bool _reportedMisses;

        public override bool GetRandomPosition(out Vector3 randomedPosition)
        {
            if (_placing)
            {
                randomedPosition = _freeSpot;
                return true;
            }
            return base.GetRandomPosition(out randomedPosition);
        }

        protected override HarvestableEntity SpawnInternal(HarvestableEntity prefab
#if !DISABLE_ADDRESSABLES
            , AddressablePrefab addressablePrefab
#endif
            , int level, float destroyRespawnDelay)
        {
            // Only a prefab can say how much room its node needs before one exists. An
            // addressable node, a 2D game or an area with no baked spots is left to the kit.
            if (prefab != null && randomedPosition3Ds.Count > 0 &&
                CurrentGameInstance.DimensionType == DimensionType.Dimension3D)
            {
                if (!FindFreeSpot(prefab.ColliderDetectionRadius, out _freeSpot))
                {
                    NoteMiss();
                    return null;
                }
                _missesInARow = 0;
                _placing = true;
            }
            try
            {
                return base.SpawnInternal(prefab
#if !DISABLE_ADDRESSABLES
                    , addressablePrefab
#endif
                    , level, destroyRespawnDelay);
            }
            finally
            {
                _placing = false;
            }
        }

        /// <summary>
        /// A baked spot the kit's overlap test will pass, or false when there is none.
        ///
        /// An area that walks its spots in order takes the first free one from where it left
        /// off, as the kit would have; one that draws them at random takes any of the free
        /// ones, each as likely as the next.
        /// </summary>
        private bool FindFreeSpot(float radius, out Vector3 spot)
        {
            int layers = BlockingLayers();
            int count = randomedPosition3Ds.Count;
            bool random = randomPositionMode == GameAreaRandomPositionMode.FullyRandom;
            int start = random ? 0 : _indexOfRandomPosition % count;
            int chosen = -1;
            int free = 0;
            for (int i = 0; i < count; ++i)
            {
                int index = (start + i) % count;
                if (Physics.CheckSphere(GetRandomPosition3D(index), radius, layers))
                    continue;
                if (!random)
                {
                    chosen = index;
                    break;
                }
                // Kept with a chance of one in however many have been free so far, which
                // leaves every free spot equally likely in a single pass.
                if (Random.Range(0, ++free) == 0)
                    chosen = index;
            }

            if (chosen < 0)
            {
                spot = transform.position;
                return false;
            }
            if (!random)
                _indexOfRandomPosition = (chosen + 1) % count;
            spot = GetRandomPosition3D(chosen);
            return true;
        }

        /// <summary>The layers <see cref="HarvestableSpawnArea.IsOverlapSomethingNearby"/> will not stand a node over.</summary>
        private int BlockingLayers()
        {
            GameInstance game = CurrentGameInstance;
            return game.playerLayer.Mask | game.playingLayer.Mask | game.monsterLayer.Mask |
                   game.npcLayer.Mask | game.vehicleLayer.Mask | game.itemDropLayer.Mask |
                   game.buildingLayer.Mask | game.harvestableLayer.Mask;
        }

        /// <summary>
        /// Says so once, in the editor, when an area has gone a good while without finding
        /// room. That is either something parked on the spots it has left or an area holding
        /// more nodes than its spots can stand, and the second is a builder fault worth hearing
        /// about - but once, not every five seconds.
        /// </summary>
        private void NoteMiss()
        {
            if (_reportedMisses || ++_missesInARow < MissesBeforeReport)
                return;
            _reportedMisses = true;
#if UNITY_EDITOR || DEBUG_SPAWN_AREA
            Debug.LogWarning($"[{nameof(FreeSpotHarvestableSpawnArea)}] {name} has found no room for a node {MissesBeforeReport} times " +
                             "running. Something is standing on the spots it has left - a player, a monster, something built - or it " +
                             "holds more nodes than its spots can stand at once. It keeps trying, and will not say so again.", this);
#endif
        }
    }
}
