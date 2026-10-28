using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace MultiplayerARPG
{
    /// <summary>
    /// Draws a building the player is still placing as a see-through ghost: green where it
    /// can go, red where it cannot.
    ///
    /// The kit already has this, in <see cref="BuildingMaterial"/>, and it is not enough for
    /// buildings made of several models. It swaps the materials of **one** renderer, plus a
    /// list of extras that "must use the same set of materials" - it hands every renderer the
    /// same array. A wall here is a wall, a quoin and a pane of glass, with one, one and two
    /// material slots, and handing all three one array leaves slots unfilled, which Unity
    /// simply does not draw. So each piece's `BuildingMaterial` carries no renderer at all and
    /// this does the drawing, giving every renderer as many ghost slots as it has slots.
    ///
    /// It also owns the piece's navigation. A wall carves the navmesh so that click-to-move
    /// routes round it, and a ghost that carved would redraw the navmesh every frame it
    /// followed the mouse and push the player's own path about. The kit disables the
    /// obstacle on a `BuildingMaterial`'s own object; a doorway's two jambs and its
    /// threshold link are elsewhere, so this handles all of them.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingGhost : MonoBehaviour
    {
        public Material canBuildMaterial;
        public Material cannotBuildMaterial;

        private BuildingEntity _entity;
        private Renderer[] _renderers;
        private Material[][] _canBuild;
        private Material[][] _cannotBuild;
        private bool _ghosted;
        private bool _lastCanBuild;

        private void Awake()
        {
            _entity = GetComponent<BuildingEntity>();
        }

        /// <summary>
        /// The prefab ships its obstacles and links switched **off**, and a placed building
        /// turns them on here. The other way round - on in the prefab, off once the ghost is
        /// noticed - lets a ghost carve the navmesh for the frame before it is noticed.
        /// `Start` is late enough: the controller calls `SetupAsBuildMode` in the same frame
        /// it instantiates the ghost, after `Awake` and before this.
        ///
        /// A placed building is never a ghost again, so it switches this off here: only the one
        /// piece being placed runs the late update below, not every wall standing in the world.
        /// </summary>
        private void Start()
        {
            if (_entity != null && _entity.IsBuildMode)
                return;
            if (_entity != null)
                SetNavigation(true);
            enabled = false;
        }

        private void SetNavigation(bool on)
        {
            foreach (NavMeshObstacle obstacle in GetComponentsInChildren<NavMeshObstacle>(true))
                obstacle.enabled = on;
            foreach (NavMeshLink link in GetComponentsInChildren<NavMeshLink>(true))
                link.enabled = on;
        }

        private void LateUpdate()
        {
            if (_entity == null || !_entity.IsBuildMode)
                return;
            if (!_ghosted)
                Ghost();
            bool canBuild = _entity.CanBuild();
            if (canBuild == _lastCanBuild)
                return;
            _lastCanBuild = canBuild;
            for (int i = 0; i < _renderers.Length; ++i)
            {
                if (_renderers[i] != null)
                    _renderers[i].sharedMaterials = canBuild ? _canBuild[i] : _cannotBuild[i];
            }
        }

        /// <summary>
        /// Build mode is switched on after the ghost is instantiated - its `Awake` has already
        /// run - so this happens on the first frame it is seen in build mode rather than at start.
        /// </summary>
        private void Ghost()
        {
            _ghosted = true;
            var renderers = new List<Renderer>();
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer)
                    continue;
                renderers.Add(renderer);
            }
            _renderers = renderers.ToArray();
            _canBuild = new Material[_renderers.Length][];
            _cannotBuild = new Material[_renderers.Length][];
            for (int i = 0; i < _renderers.Length; ++i)
            {
                int slots = Mathf.Max(1, _renderers[i].sharedMaterials.Length);
                _canBuild[i] = Fill(canBuildMaterial, slots);
                _cannotBuild[i] = Fill(cannotBuildMaterial, slots);
                _renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderers[i].receiveShadows = false;
                _renderers[i].sharedMaterials = _cannotBuild[i];
            }
            _lastCanBuild = false;
            SetNavigation(false);
        }

        private static Material[] Fill(Material material, int count)
        {
            var materials = new Material[count];
            for (int i = 0; i < count; ++i)
                materials[i] = material;
            return materials;
        }
    }
}
