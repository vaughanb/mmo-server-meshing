using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Brings a building's dependants down with it: pull up a foundation and its walls, roof
    /// and furniture go too, rather than being left standing on nothing.
    ///
    /// **This is the kit's own feature, and the kit's version never fires** (found in the
    /// harness, 2026-09-25). `BuildingEntity.destroyWhenParentDestroyed` is honoured in
    /// `BuildingEntity.OnNetworkDestroy`, which walks the building's `_children` - but it
    /// calls `base.OnNetworkDestroy` first, and `BaseGameEntity.OnNetworkDestroy` ends with
    /// `Clean(false)`, whose `BuildingEntity` override clears `_children`. So the list is
    /// always empty by the time it is walked, for every building in every game built on the
    /// kit. That wants fixing upstream, in Core; the demo does not patch Core, so this does
    /// the same job from outside.
    ///
    /// The seam is `BaseGameEntity.onNetworkDestroy`, which is raised in that same method
    /// *before* the clean-up. It runs on the server only, and only for a building that was
    /// asked to go (`RequestedToDestroy`) - not for one unloaded with its map, which is how the
    /// kit's own version is gated. The dependants are found by their `ParentId`, which the
    /// server sets when they are built on a socket, rather than from `_children`, which is
    /// private.
    ///
    /// Every homestead piece carries one, and <see cref="comesDownWithParent"/> says whether
    /// that piece goes when its parent does. The kit's own flag is set to match, so once the
    /// kit is fixed both paths agree - and a building destroyed twice is harmless, because
    /// `Destroy` returns early the second time.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingChainDestroy : MonoBehaviour
    {
        [Tooltip("Whether this building is destroyed when the building it was built on is.")]
        public bool comesDownWithParent = true;

        private BuildingEntity _entity;

        private void Awake()
        {
            _entity = GetComponent<BuildingEntity>();
        }

        // Subscribed on enable rather than once in Awake, because the kit pools entities and
        // nulls the event when it cleans one up.
        private void OnEnable()
        {
            if (_entity == null)
                return;
            _entity.onNetworkDestroy -= OnNetworkDestroy;
            _entity.onNetworkDestroy += OnNetworkDestroy;
        }

        private void OnDisable()
        {
            if (_entity != null)
                _entity.onNetworkDestroy -= OnNetworkDestroy;
        }

        private void OnNetworkDestroy(BaseGameEntity target, byte reasons)
        {
            if (reasons != DestroyObjectReasons.RequestedToDestroy || _entity == null || !_entity.IsServer)
                return;
            string id = _entity.Id;
            if (string.IsNullOrEmpty(id))
                return;
            foreach (BuildingEntity other in FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None))
            {
                if (other == _entity || other.IsBuildMode || other.ParentId != id)
                    continue;
                var chain = other.GetComponent<BuildingChainDestroy>();
                if (chain != null && chain.comesDownWithParent)
                    other.Destroy();
            }
        }
    }
}
