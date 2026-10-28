using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A ribbon left in the air by a swung weapon: the blade's edge, from part-way up the grip
    /// to a little past the tip, sampled every frame while the swing lasts and faded out behind.
    ///
    /// It rides on a kit <see cref="GameEffect"/> prefab and is fired as a skill's **activate
    /// effect**, which the kit instantiates the moment the skill's animation starts - exactly
    /// when a swing begins. The kit does not parent the effect to the character; it pools it at
    /// an effect socket and sets <see cref="GameEffect.FollowingTarget"/> to that socket's
    /// transform, so the character model is found through the following target, and from the
    /// model the equipped weapon through its equipment container (<see cref="equipSocket"/>).
    /// The ribbon's points come off the weapon's own mesh: the longest axis of the mesh bounds
    /// is the edge (a sword is grip-to-tip on +Y by the demo's socket convention; a shield's is
    /// its diameter), so one script serves any weapon without per-model setup, and nothing has
    /// to be added to the weapon prefabs.
    ///
    /// Samples are kept in world space and the mesh is rebuilt in this object's local space each
    /// frame, since the effect itself keeps moving with its socket. Vertex alpha carries the
    /// fade, which the URP particle shader multiplies in. Built by
    /// `DemoSkillEffectBuilder.BuildWeaponTrails` (2026-10-02, user's request: trails on the
    /// warrior's attack skills).
    ///
    /// Its late update runs from the swing until the ribbon has faded, and then lets go until the
    /// pool hands the effect out again (<see cref="WakeableLateUpdateBehaviour"/>); a weapon that
    /// cannot be found is looked for only while the swing lasts.
    /// </summary>
    [RequireComponent(typeof(GameEffect))]
    public class WeaponTrail : WakeableLateUpdateBehaviour
    {
        [Tooltip("The equipment container the swung weapon sits in: RightHand for the main hand, LeftHand for a shield.")]
        public string equipSocket = "RightHand";

        [Tooltip("How long after the effect starts the edge keeps being sampled - the length of the swing.")]
        public float emitSeconds = 0.7f;

        [Tooltip("How long a sampled slice of ribbon takes to fade out.")]
        public float fadeSeconds = 0.22f;

        [Tooltip("Tint and peak alpha of the ribbon.")]
        public Color colour = new Color(0.85f, 0.92f, 1f, 0.6f);

        [Tooltip("Where along the weapon's edge the ribbon begins, as a share of its length: skips the grip on a sword.")]
        [Range(0f, 0.9f)]
        public float startFraction = 0.3f;

        [Tooltip("How far past the end of the edge the ribbon reaches, in metres, so the tip reads as cutting air.")]
        public float overshoot = 0.1f;

        [Tooltip("Material for the ribbon: an alpha-blended, unlit URP particle material that multiplies vertex colour.")]
        public Material material;

        private struct Slice
        {
            public Vector3 Base;
            public Vector3 Tip;
            public float Time;
        }

        private GameEffect _effect;
        private Transform _ribbon;
        private Mesh _mesh;
        private Transform _edge;
        private Vector3 _localBase;
        private Vector3 _localTip;
        private bool _found;
        private float _startTime;
        private readonly List<Slice> _slices = new List<Slice>(64);
        private readonly List<Vector3> _vertices = new List<Vector3>(128);
        private readonly List<Color> _colours = new List<Color>(128);
        private readonly List<Vector2> _uvs = new List<Vector2>(128);
        private readonly List<int> _triangles = new List<int>(384);

        private void Awake()
        {
            _effect = GetComponent<GameEffect>();
            var go = new GameObject("Ribbon");
            go.transform.SetParent(transform, false);
            _ribbon = go.transform;
            _mesh = new Mesh { name = "WeaponTrail" };
            _mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void OnEnable()
        {
            // Pooled: every reuse starts a fresh swing.
            _startTime = Time.time;
            _slices.Clear();
            _found = false;
            _edge = null;
            if (_mesh != null)
                _mesh.Clear();
            Wake();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _slices.Clear();
            if (_mesh != null)
                _mesh.Clear();
        }

        public override void ManagedLateUpdate()
        {
            float now = Time.time;
            float age = now - _startTime;
            if (!_found && age <= emitSeconds)
                FindEdge();
            if (_found && age <= emitSeconds)
            {
                if (_edge == null)
                {
                    // The weapon was unequipped or the model rebuilt mid-swing.
                    _found = false;
                }
                else
                {
                    _slices.Add(new Slice
                    {
                        Base = _edge.TransformPoint(_localBase),
                        Tip = _edge.TransformPoint(_localTip),
                        Time = now,
                    });
                }
            }
            while (_slices.Count > 0 && now - _slices[0].Time > fadeSeconds)
                _slices.RemoveAt(0);
            Rebuild(now);
            // The swing is over and the last of the ribbon has faded.
            if (age > emitSeconds && _slices.Count == 0)
                Sleep();
        }

        /// <summary>
        /// Finds the swung weapon's mesh through the effect's following socket, and measures its
        /// edge once in the mesh's local space.
        /// </summary>
        private void FindEdge()
        {
            Transform follow = _effect != null ? _effect.FollowingTarget : null;
            BaseCharacterModel model = follow != null
                ? follow.GetComponentInParent<BaseCharacterModel>()
                : GetComponentInParent<BaseCharacterModel>();
            if (model == null || model.CacheEquipmentModelContainers == null)
                return;
            if (!model.CacheEquipmentModelContainers.TryGetValue(equipSocket, out EquipmentContainer container) || container.transform == null)
                return;
            // The biggest mesh under the container is the weapon; anything else there (a nocked
            // arrow, a glow) is smaller.
            MeshFilter weapon = null;
            foreach (MeshFilter filter in container.transform.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null)
                    continue;
                if (weapon == null || filter.sharedMesh.bounds.size.sqrMagnitude > weapon.sharedMesh.bounds.size.sqrMagnitude)
                    weapon = filter;
            }
            if (weapon == null)
                return;
            Bounds bounds = weapon.sharedMesh.bounds;
            int axis = 0;
            if (bounds.size.y > bounds.size[axis]) axis = 1;
            if (bounds.size.z > bounds.size[axis]) axis = 2;
            Vector3 low = bounds.center;
            Vector3 high = bounds.center;
            low[axis] = bounds.min[axis];
            high[axis] = bounds.max[axis];
            Vector3 along = (high - low).normalized;
            // Overshoot is in metres, so it has to be taken into the mesh's own scale.
            float scale = Mathf.Max(0.0001f, weapon.transform.lossyScale[axis]);
            _localBase = Vector3.Lerp(low, high, startFraction);
            _localTip = high + along * (overshoot / scale);
            _edge = weapon.transform;
            _found = true;
        }

        private void Rebuild(float now)
        {
            _vertices.Clear();
            _colours.Clear();
            _uvs.Clear();
            _triangles.Clear();
            int count = _slices.Count;
            if (count < 2)
            {
                _mesh.Clear();
                return;
            }
            for (int i = 0; i < count; ++i)
            {
                Slice slice = _slices[i];
                float alpha = fadeSeconds <= 0f ? 1f : Mathf.Clamp01(1f - (now - slice.Time) / fadeSeconds);
                Color c = colour;
                c.a *= alpha;
                float u = (float)i / (count - 1);
                _vertices.Add(_ribbon.InverseTransformPoint(slice.Base));
                _vertices.Add(_ribbon.InverseTransformPoint(slice.Tip));
                // The base edge is a little fainter, so the ribbon reads as the tip's wake.
                Color baseColour = c;
                baseColour.a *= 0.6f;
                _colours.Add(baseColour);
                _colours.Add(c);
                _uvs.Add(new Vector2(u, 0f));
                _uvs.Add(new Vector2(u, 1f));
            }
            for (int i = 0; i < count - 1; ++i)
            {
                int a = i * 2;
                _triangles.Add(a); _triangles.Add(a + 1); _triangles.Add(a + 2);
                _triangles.Add(a + 1); _triangles.Add(a + 3); _triangles.Add(a + 2);
            }
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colours);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }
    }
}
