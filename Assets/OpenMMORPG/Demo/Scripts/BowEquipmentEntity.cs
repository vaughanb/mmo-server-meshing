using Insthync.ManagedUpdating;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The bow's own behaviour while it is being shot: the string bends back to the
    /// drawing hand, the limbs flex with it, and an arrow is carried, nocked and loosed.
    ///
    /// It is the kit's <see cref="EquipmentEntity"/> rather than a loose MonoBehaviour so
    /// that it receives <see cref="PlayLaunch"/> - the one moment the kit already tells a
    /// weapon about, raised on every client at the exact frame the missile is spawned
    /// (<c>DefaultCharacterAttackComponent</c> and <c>DefaultCharacterUseSkillComponent</c>
    /// both call it). That is the loose, so the string needs no timing constants of its
    /// own: it follows the hand until the kit says the arrow has gone, then snaps.
    ///
    /// **Everything else is read off the hand, not off a clip.** The drawing hand's socket
    /// is sampled each frame and the string's apex is put where it is. That is what makes
    /// this work for a player, a monster and a skill shot alike, at whatever speed the
    /// attack-speed stat happens to be playing the clip, without a single per-animation
    /// number. See <see cref="Draw"/> for the measurements behind the thresholds.
    ///
    /// The string and the limbs are found in the mesh rather than authored: the Malagen bow
    /// models the string as a **separate connected component** (80 vertices against the
    /// body's 1742), so a union-find over the triangles separates them exactly. See
    /// <see cref="Resolve"/>.
    ///
    /// Runs in the kit's post-IK slot (<see cref="DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK"/>),
    /// once the drawing hand is where the animation and the IK have finally put it, rather than in
    /// a LateUpdate of its own that came before or after them as Unity happened to order it.
    /// </summary>
    public class BowEquipmentEntity : EquipmentEntity, IManagedLateUpdate
    {
        /// <summary>Where the arrow is between leaving the quiver and leaving the string.</summary>
        private enum Phase
        {
            /// <summary>Not shooting. String at brace, no arrow.</summary>
            Rest,

            /// <summary>Shooting, but the drawing hand has not reached the string yet: the
            /// arrow rides in the fist, which is what the first half of the draw clip is.</summary>
            Carried,

            /// <summary>The arrow is on the string, and the string follows the hand.</summary>
            Nocked,

            /// <summary>Loosed. The string is ringing on its own and the arrow is gone.</summary>
            Released,
        }

        [Header("Archery")]
        [Tooltip("Arrow model to carry and nock. The demo's Arrow.prefab: length on +Y, nock at -Y, origin at the middle.")]
        public GameObject arrowPrefab;

        [Tooltip("Furthest the nock may be pulled from the braced string, in metres.")]
        public float maxDraw = 0.65f;

        [Tooltip("How near the string the drawing hand must come before the arrow is nocked, in metres.")]
        public float nockRadius = 0.16f;

        [Tooltip("How far out of the bow's own plane the drawing hand may stray before the string is let go of, in metres.")]
        public float planeTolerance = 0.3f;

        [Tooltip("Seconds the arrow takes to pass from the fist to the string once nocked.")]
        public float nockBlend = 0.1f;

        [Tooltip("How far a limb tip is pulled toward the archer at full draw, in metres.")]
        public float limbFlex = 0.09f;

        [Header("Loose")]
        [Tooltip("Seconds the string rings for after the arrow leaves it.")]
        public float releaseTime = 0.3f;

        [Tooltip("Oscillations per second while the string rings.")]
        public float releaseFrequency = 13f;

        [Tooltip("How fast the ringing dies away. Larger is deader.")]
        public float releaseDamping = 20f;

        [Tooltip("Furthest the string may swing past brace on the rebound, in metres.")]
        public float ringAmplitude = 0.035f;

        [Tooltip("Seconds the string takes to return to brace when a shot is abandoned rather than loosed.")]
        public float relaxTime = 0.25f;

        [Header("Hands")]
        [Tooltip("The equipment socket of the right hand. The drawing hand is whichever of the two the bow does not hang from.")]
        public string rightHandSocket = "RightHand";

        [Tooltip("The equipment socket of the left hand.")]
        public string leftHandSocket = "LeftHand";

        [Header("Held")]
        [Tooltip("The item this bow is, when it was put straight into a hand rather than equipped - a monster's. " +
                 "Empty on the prefab: the kit tells a bow it equips what it is.")]
        public BaseItem heldItem;

        [Header("Skills")]
        [Tooltip("What each bow skill sets off at the loose. Written by the weapon builder; see SkillReleaseEffects.")]
        public SkillReleaseEffects releaseEffects;

        /// <summary>True for a bow nobody equipped, which introduced itself to its character. See <see cref="Start"/>.</summary>
        private bool _held;

        /// <summary>
        /// The string and body of one bow mesh, worked out once and shared by every bow
        /// using it.
        ///
        /// All of it is in the mesh's own local space, which for these models is about a
        /// hundred times smaller than a metre - the Malagen weapons are authored in
        /// centimetres and the import scale lives on the model root. Callers convert with
        /// <see cref="_perMetre"/> rather than baking a scale in here, because the same mesh
        /// is shared between bows on differently-sized characters.
        /// </summary>
        private class Geometry
        {
            public Vector3[] baseVertices;
            public int[] stringVertices;
            public int[] bodyVertices;

            /// <summary>Unit vector up the string, from one tip to the other.</summary>
            public Vector3 along;

            /// <summary>Unit vector the string is pulled along: away from the limbs, toward the archer.</summary>
            public Vector3 pull;

            /// <summary>Midpoint of the braced string, and the origin every offset below is measured from.</summary>
            public Vector3 centre;

            /// <summary>Extent of the string either side of <see cref="centre"/>, along <see cref="along"/>.</summary>
            public float lowerTip;

            public float upperTip;

            /// <summary>Half the bow's length, used to taper the limb flex away from the grip.</summary>
            public float halfLength;

            public Bounds bounds;
        }

        private static readonly Dictionary<int, Geometry> _geometryCache = new Dictionary<int, Geometry>();

        private MeshFilter _meshFilter;
        private Transform _meshTransform;
        private Mesh _mesh;
        private Geometry _geometry;
        private Vector3[] _working;
        private bool _deformed;

        private BaseCharacterEntity _character;
        private Transform _drawHand;
        private ICharacterAttackComponent _attack;
        private ICharacterUseSkillComponent _skill;

        private Phase _phase = Phase.Rest;
        private bool _spent;
        private float _pull;
        private float _along;
        private float _blend;
        private float _releasedAt;
        private float _releasedFrom;

        private GameObject _arrow;
        private Transform _arrowTransform;
        private float _arrowHalfLength = 0.36f;

        /// <summary>Mesh-local units to a metre, refreshed each frame because characters differ in size.</summary>
        private float _perMetre = 1f;

        /// <summary>True while an arrow is out of the quiver and in the drawing hand or on the string. See <see cref="QuiverArrows"/>.</summary>
        public bool HasArrowInHand { get { return isActiveAndEnabled && (_phase == Phase.Carried || _phase == Phase.Nocked); } }

        /// <summary>True for the moment after the loose, while the string rings: the arrow has gone, not gone back.</summary>
        public bool HasLoosed { get { return isActiveAndEnabled && _phase == Phase.Released; } }

        protected override void Awake()
        {
            base.Awake();
            // Nothing here is seen on a headless map server, which would otherwise copy a mesh
            // per bow and deform it every frame of every shot - and, where the build strips mesh
            // data, log that it cannot read it. The same way out as a bow with no usable mesh,
            // which everything below already allows for; the kit's own launch still runs.
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            _meshFilter = GetComponentInChildren<MeshFilter>(true);
            if (_meshFilter == null || _meshFilter.sharedMesh == null)
            {
                enabled = false;
                return;
            }
            _meshTransform = _meshFilter.transform;
            _geometry = Resolve(_meshFilter.sharedMesh);
            if (_geometry == null)
            {
                enabled = false;
                return;
            }

            _mesh = Instantiate(_meshFilter.sharedMesh);
            _mesh.name = _meshFilter.sharedMesh.name + " (drawn)";
            _mesh.MarkDynamic();
            // Fixed once rather than recalculated per frame: the apex travels most of a
            // metre, so bounds left at the braced mesh's would cull a drawn bow early.
            Bounds drawn = _geometry.bounds;
            drawn.Encapsulate(_geometry.bounds.center + _geometry.pull * (maxDraw * 2f));
            _mesh.bounds = drawn;
            _meshFilter.mesh = _mesh;
            _working = (Vector3[])_geometry.baseVertices.Clone();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Deafen();
            if (_mesh != null)
                Destroy(_mesh);
            if (_arrow != null)
                Destroy(_arrow);
            _mesh = null;
            _arrow = null;
            _arrowTransform = null;
            _character = null;
            _drawHand = null;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // Equipment objects can be pooled, so nothing may be assumed about the state a
            // bow was put away in.
            _phase = Phase.Rest;
            _pull = 0f;
            _blend = 0f;
            _spent = false;
            ShowArrow(false);
            if (_geometry != null && _mesh != null)
                UpdateManager.Register(DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK, this);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            UpdateManager.Unregister(DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK, this);
        }

        public override void Setup(BaseCharacterModel characterModel, string equipSocket, string equipPosition, CharacterItem item)
        {
            base.Setup(characterModel, equipSocket, equipPosition, item);
            // A bow slung on the back (DemoWeaponSheathing) is not being shot. Left listening it
            // would nock and draw whenever its owner swung anything else - a sword from the other
            // weapon set, say.
            if (DemoWeaponSheathing.IsStowedSocket(equipSocket))
            {
                enabled = false;
                return;
            }
            _character = characterModel != null ? characterModel.Entity as BaseCharacterEntity : null;
            _drawHand = FindDrawHand(characterModel, equipSocket);
            Listen();
        }

        /// <summary>
        /// Introduces a bow nobody equipped to the character holding it.
        ///
        /// **A monster's bow is not equipment.** The kit's monsters have no weapon slot, so
        /// the entity builder puts the bow straight into the hand, and the kit neither calls
        /// <see cref="Setup"/> on it nor counts it as the character's weapon: the string would
        /// never find the drawing hand, and the arrow on it would never learn it had gone. So
        /// it sets itself up against the model holding it, and registers as that model's
        /// right-hand weapon - where the kit sends <see cref="PlayLaunch"/>, since a bow is a
        /// right-hand weapon to the kit wherever its mesh hangs. A bow the kit equipped has
        /// been set up by the time this runs, and is left alone.
        /// </summary>
        private void Start()
        {
            if (CharacterModel != null)
                return;
            BaseCharacterModel model = GetComponentInParent<BaseCharacterModel>();
            if (model == null)
                return;
            _held = true;
            Setup(model, transform.parent != null ? transform.parent.name : string.Empty,
                  GameDataConst.EQUIP_POSITION_RIGHT_HAND,
                  heldItem != null ? CharacterItem.Create(heldItem) : CharacterItem.Empty);
            if (model.CacheRightHandEquipmentEntity == null)
                model.CacheRightHandEquipmentEntity = this;
        }

        /// <summary>
        /// Takes the start of each shot from the character rather than inferring it.
        ///
        /// Polling "is this character attacking" is enough to know a shot is *under way*, but
        /// not enough to know a *new* one has begun: a bow attack runs for half a second past
        /// the loose while the archer recovers, so a component that only watched the flag
        /// would nock a fresh arrow into a hand that is still flying back from the last one.
        /// These two are raised once per shot, at the top of the routine, on every peer.
        /// </summary>
        private void Listen()
        {
            Deafen();
            if (_character == null)
                return;
            _attack = _character.AttackComponent;
            _skill = _character.UseSkillComponent;
            if (_attack != null)
                _attack.OnAttackStart += BeginShot;
            if (_skill != null)
            {
                _skill.OnUseSkillStart += BeginShot;
                _skill.OnUseSkillTrigger += SkillTriggered;
            }
        }

        private void Deafen()
        {
            if (_attack != null)
                _attack.OnAttackStart -= BeginShot;
            if (_skill != null)
            {
                _skill.OnUseSkillStart -= BeginShot;
                _skill.OnUseSkillTrigger -= SkillTriggered;
            }
            _attack = null;
            _skill = null;
        }

        private void BeginShot()
        {
            _spent = false;
            if (_phase == Phase.Rest || _phase == Phase.Released)
            {
                _phase = Phase.Carried;
                _blend = 0f;
                ShowArrow(true);
            }
        }

        /// <summary>
        /// The socket of the hand that is not holding the bow.
        ///
        /// The demo's bow model hangs off <c>LeftHand</c> - a bow belongs in the off hand, even
        /// though the kit counts it as a right-hand weapon (see <c>DemoItemBuilder</c>) - so
        /// the drawing hand is whichever of the two sockets the bow is not in. Resolved by
        /// socket name (<see cref="rightHandSocket"/>, <see cref="leftHandSocket"/>), because
        /// that is what the containers are keyed by and what the character's socket objects
        /// are named after.
        /// </summary>
        private Transform FindDrawHand(BaseCharacterModel characterModel, string equipSocket)
        {
            if (characterModel == null)
                return null;
            string wanted = equipSocket == rightHandSocket ? leftHandSocket : rightHandSocket;
            if (characterModel.CacheEquipmentModelContainers != null &&
                characterModel.CacheEquipmentModelContainers.TryGetValue(wanted, out EquipmentContainer container) &&
                container.transform != null)
                return container.transform;
            foreach (Transform child in characterModel.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == wanted)
                    return child;
            }
            return null;
        }

        /// <summary>
        /// True while the character is in the middle of a shot, by any route: an ordinary
        /// attack, a skill, or a held charge. The attack routine runs on every peer, so this
        /// reads the same for the local player and for somebody else's archer.
        /// </summary>
        private bool IsShooting
        {
            get
            {
                if (_character == null)
                    return false;
                return _character.IsAttacking || _character.IsUsingSkill || _character.IsCharging;
            }
        }

        public override void PlayLaunch()
        {
            base.PlayLaunch();
            // The kit plays the loose from the equipped item, and a held bow's character has
            // none - a monster attacks with a stand-in weapon of the kit's own - so it plays
            // its own item's. See Start.
            if (_held && heldItem is IWeaponItem weapon && CharacterModel != null)
                weapon.LaunchClip?.Play(CharacterModel.GenericAudioSource);
            Loose();
        }

        /// <summary>The arrow leaves the string: it is hidden, and the string rings on its own.</summary>
        private void Loose()
        {
            _spent = true;
            if (_phase == Phase.Rest)
                return;
            _releasedFrom = _pull;
            _releasedAt = Time.time;
            _phase = Phase.Released;
            ShowArrow(false);
        }

        /// <summary>
        /// A skill's trigger: the loose, for a skill with an animation of its own, and the moment its
        /// release effects go off (see <see cref="SkillReleaseEffects"/>).
        ///
        /// The kit sends <see cref="PlayLaunch"/> only for a skill that borrows the weapon's attack
        /// animation. Aimed Shot and Volley play their own - a held draw into a release - so without
        /// this their arrow would stay on the string after the missile had gone. A skill that does
        /// borrow the attack has had its PlayLaunch already, earlier in the same call, so the arrow is
        /// gone and this looses nothing twice.
        /// </summary>
        private void SkillTriggered(int triggerIndex)
        {
            if (_phase == Phase.Carried || _phase == Phase.Nocked)
                Loose();
            if (releaseEffects == null || _skill == null || CharacterModel == null)
                return;
            if (_character != null && !_character.IsClient)
                return;
            GameEffect[] effects = releaseEffects.For(_skill.UsingSkill);
            if (effects != null && effects.Length > 0)
                CharacterModel.InstantiateEffect(effects);
        }

        public override void PlayCharge()
        {
            base.PlayCharge();
            if (_phase == Phase.Rest)
            {
                _phase = Phase.Carried;
                _blend = 0f;
                ShowArrow(true);
            }
        }

        public void ManagedLateUpdate()
        {
            if (_geometry == null || _mesh == null)
                return;

            float scale = _meshTransform.lossyScale.x;
            _perMetre = Mathf.Approximately(scale, 0f) ? 1f : 1f / scale;

            Advance();
            Apply();
            PlaceArrow();
        }

        /// <summary>Moves the draw on by one frame, and the arrow with it.</summary>
        private void Advance()
        {
            bool shooting = IsShooting;

            switch (_phase)
            {
                case Phase.Rest:
                    if (!shooting)
                        _spent = false;
                    // The fallback for a shot whose start was never announced. Held off
                    // until the last one has been let go of, or the recovery half of an
                    // attack would put a fresh arrow in the hand.
                    else if (!_spent)
                    {
                        _phase = Phase.Carried;
                        _blend = 0f;
                        ShowArrow(true);
                    }
                    Relax();
                    break;

                case Phase.Carried:
                    if (!shooting)
                    {
                        Abandon();
                        break;
                    }
                    if (DistanceToString() <= nockRadius && Draw(out float carriedPull, out float carriedAlong))
                    {
                        _phase = Phase.Nocked;
                        _blend = 0f;
                        _pull = carriedPull;
                        _along = carriedAlong;
                        break;
                    }
                    Relax();
                    break;

                case Phase.Nocked:
                    if (!shooting)
                    {
                        Abandon();
                        break;
                    }
                    // A hand that has left the bow's plane is not on the string any more,
                    // whatever the clip thinks it is doing. Through a real draw it stays
                    // within 11cm of the plane; through the raise that starts a shot it is
                    // 56cm out of it. Without this, an animation that renocks - a skill
                    // whose cast clip draws and whose attack clip then draws again - would
                    // drag the string sideways out of the bow rather than let it down.
                    if (Mathf.Abs(PlaneOffset()) > planeTolerance)
                    {
                        _phase = Phase.Carried;
                        _blend = 0f;
                        ShowArrow(true);
                        Relax();
                        break;
                    }
                    _blend = nockBlend > 0f ? Mathf.Min(1f, _blend + Time.deltaTime / nockBlend) : 1f;
                    if (Draw(out float pull, out float along))
                    {
                        _pull = pull;
                        _along = along;
                    }
                    break;

                case Phase.Released:
                    // The string rings on its own from here: the hand is already flying back
                    // past the ear and is no longer holding it.
                    float since = Time.time - _releasedAt;
                    if (since >= releaseTime)
                    {
                        _pull = 0f;
                        _blend = 0f;
                        _phase = Phase.Rest;
                        break;
                    }
                    // A damped cosine, but only backwards. Forwards it is clamped, because a
                    // string is not free that way: a plain decaying oscillation off a 0.58m
                    // draw swings 27cm past brace on its first rebound, which is straight
                    // through the riser. A real string clears the bow's belly by the brace
                    // height and no more.
                    float amplitude = _releasedFrom * Mathf.Exp(-releaseDamping * since);
                    float wave = Mathf.Cos(2f * Mathf.PI * releaseFrequency * since);
                    _pull = wave >= 0f
                        ? amplitude * wave
                        : Mathf.Max(amplitude * wave, -ringAmplitude * _perMetre);
                    break;
            }
        }

        /// <summary>Hides the arrow and lets the string down, for a shot that never loosed.</summary>
        private void Abandon()
        {
            _phase = Phase.Rest;
            _blend = 0f;
            ShowArrow(false);
            Relax();
        }

        /// <summary>Eases the string back to brace when nothing is holding it.</summary>
        private void Relax()
        {
            if (Mathf.Approximately(_pull, 0f))
            {
                _pull = 0f;
                return;
            }
            float rate = maxDraw * _perMetre / Mathf.Max(0.0001f, relaxTime);
            _pull = Mathf.MoveTowards(_pull, 0f, rate * Time.deltaTime);
        }

        /// <summary>
        /// Where the drawing hand puts the nock, in mesh-local units.
        ///
        /// Measured against the demo's own archery clips, sampled with the bow on its tuned
        /// grip: through the drawn hold in <c>Bow_Release</c> the hand sits **0.45m to 0.56m**
        /// behind the string and stays within **2cm of the string's plane**. That is why only
        /// the pull and the height are taken and the sideways component is dropped - keeping
        /// it would buy a couple of centimetres of fidelity and risk a string bent out of its
        /// own plane by any clip that is less tidy.
        ///
        /// The pull is clamped at <see cref="maxDraw"/> because the hand does not stop at the
        /// anchor: 0.175s after the loose it is 0.92m back, and a string that followed it
        /// there would tear through the bow. <see cref="PlayLaunch"/> normally takes over
        /// before that, and the clamp covers the case where it does not.
        /// </summary>
        private bool Draw(out float pull, out float along)
        {
            pull = 0f;
            along = 0f;
            if (_drawHand == null)
                return false;
            Vector3 hand = _meshTransform.InverseTransformPoint(_drawHand.position) - _geometry.centre;
            pull = Mathf.Clamp(Vector3.Dot(hand, _geometry.pull), 0f, maxDraw * _perMetre);
            // Keep the apex on the string, a tenth of the way in from each tip: a nock at the
            // very tip would fold the string flat along the limb.
            float margin = (_geometry.upperTip - _geometry.lowerTip) * 0.1f;
            along = Mathf.Clamp(Vector3.Dot(hand, _geometry.along),
                                _geometry.lowerTip + margin,
                                _geometry.upperTip - margin);
            return true;
        }

        /// <summary>
        /// How far the drawing hand is off the bow's own plane - the plane holding the
        /// string and both limbs - in metres. Signed, but only its size is used.
        /// </summary>
        private float PlaneOffset()
        {
            if (_drawHand == null)
                return 0f;
            Vector3 hand = _meshTransform.InverseTransformPoint(_drawHand.position) - _geometry.centre;
            return Vector3.Dot(hand, Vector3.Cross(_geometry.along, _geometry.pull)) / _perMetre;
        }

        /// <summary>How far the drawing hand is from the braced string, in metres.</summary>
        private float DistanceToString()
        {
            if (_drawHand == null)
                return float.MaxValue;
            Vector3 hand = _meshTransform.InverseTransformPoint(_drawHand.position) - _geometry.centre;
            float along = Mathf.Clamp(Vector3.Dot(hand, _geometry.along), _geometry.lowerTip, _geometry.upperTip);
            return (hand - _geometry.along * along).magnitude / _perMetre;
        }

        /// <summary>
        /// Bends the mesh: the string into a V with its apex at the nock, and the limbs
        /// toward the archer with it.
        ///
        /// Normals are deliberately left alone. The bow is flat shaded off split vertices,
        /// so recalculating them would cost more than the bend itself *and* soften the facets
        /// the model is drawn with, all to chase a change of a few degrees.
        /// </summary>
        private void Apply()
        {
            if (Mathf.Approximately(_pull, 0f))
            {
                if (!_deformed)
                    return;
                System.Array.Copy(_geometry.baseVertices, _working, _working.Length);
                _mesh.SetVertices(_working);
                _deformed = false;
                return;
            }

            Vector3[] baseVertices = _geometry.baseVertices;
            Vector3 pullAxis = _geometry.pull;
            Vector3 alongAxis = _geometry.along;
            Vector3 centre = _geometry.centre;

            foreach (int i in _geometry.stringVertices)
            {
                float a = Vector3.Dot(baseVertices[i] - centre, alongAxis);
                float span = a >= _along ? _geometry.upperTip - _along : _along - _geometry.lowerTip;
                float fraction = span > 0f ? Mathf.Clamp01(1f - Mathf.Abs(a - _along) / span) : 0f;
                _working[i] = baseVertices[i] + pullAxis * (_pull * fraction);
            }

            float flex = limbFlex * _perMetre * Mathf.Clamp01(_pull / Mathf.Max(0.0001f, maxDraw * _perMetre));
            if (_geometry.halfLength > 0f && !Mathf.Approximately(flex, 0f))
            {
                foreach (int i in _geometry.bodyVertices)
                {
                    float a = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(baseVertices[i] - centre, alongAxis)) / _geometry.halfLength);
                    // Squared, so the grip stays put and the tips take the whole bend.
                    _working[i] = baseVertices[i] + pullAxis * (flex * a * a);
                }
            }
            else if (_deformed)
            {
                foreach (int i in _geometry.bodyVertices)
                    _working[i] = baseVertices[i];
            }

            _mesh.SetVertices(_working);
            _deformed = true;
        }

        private void ShowArrow(bool visible)
        {
            if (!visible)
            {
                if (_arrow != null && _arrow.activeSelf)
                    _arrow.SetActive(false);
                return;
            }
            // The shot's events still reach a bow on a headless server (see Awake); there is
            // nobody there to see an arrow on the string.
            if (Application.isBatchMode)
                return;
            if (arrowPrefab == null)
                return;
            if (_arrow == null)
            {
                _arrow = Instantiate(arrowPrefab, transform);
                _arrow.name = "NockedArrow";
                _arrowTransform = _arrow.transform;
                _arrowTransform.localPosition = Vector3.zero;
                _arrowTransform.localRotation = Quaternion.identity;
                _arrow.SetLayerRecursively(gameObject.layer, true);
                _arrowHalfLength = HalfLength(_arrow);
            }
            if (!_arrow.activeSelf)
                _arrow.SetActive(true);
        }

        /// <summary>Half the arrow's length along its own +Y, in metres.</summary>
        private static float HalfLength(GameObject arrow)
        {
            Transform root = arrow.transform;
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (Renderer renderer in arrow.GetComponentsInChildren<Renderer>(true))
            {
                Bounds bounds = renderer.bounds;
                for (int corner = 0; corner < 8; ++corner)
                {
                    Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                    float y = root.InverseTransformPoint(point).y;
                    min = Mathf.Min(min, y);
                    max = Mathf.Max(max, y);
                }
            }
            return max > min ? (max - min) * 0.5f : 0.36f;
        }

        /// <summary>
        /// Puts the arrow where it belongs this frame: in the fist on the way to the string,
        /// on the string once nocked, and blended between the two across the nock itself so
        /// that it does not jump out of the hand.
        /// </summary>
        private void PlaceArrow()
        {
            if (_arrowTransform == null || !_arrow.activeSelf)
                return;

            Quaternion inverse = Quaternion.Inverse(transform.rotation);
            Vector3 carriedPosition = _arrowTransform.localPosition;
            Quaternion carriedRotation = _arrowTransform.localRotation;
            if (_drawHand != null)
            {
                // The hand sockets are built so that a weapon laid along +Y sits in the fist,
                // and an arrow is exactly that: carried like any other haft.
                carriedPosition = transform.InverseTransformPoint(_drawHand.position);
                carriedRotation = inverse * _drawHand.rotation;
            }

            if (_phase != Phase.Nocked || _blend <= 0f)
            {
                _arrowTransform.localPosition = carriedPosition;
                _arrowTransform.localRotation = carriedRotation;
                return;
            }

            Vector3 apex = _geometry.centre + _geometry.along * _along + _geometry.pull * _pull;
            Vector3 apexWorld = _meshTransform.TransformPoint(apex);
            Vector3 shootWorld = _meshTransform.TransformDirection(-_geometry.pull).normalized;
            Vector3 alongWorld = _meshTransform.TransformDirection(_geometry.along).normalized;

            Vector3 nockedPosition = transform.InverseTransformPoint(apexWorld + shootWorld * _arrowHalfLength);
            Quaternion nockedRotation = inverse * Quaternion.LookRotation(alongWorld, shootWorld);

            _arrowTransform.localPosition = Vector3.Lerp(carriedPosition, nockedPosition, _blend);
            _arrowTransform.localRotation = Quaternion.Slerp(carriedRotation, nockedRotation, _blend);
        }

        /// <summary>
        /// Separates the string from the body of the bow, and works out which way it runs and
        /// which way it is pulled.
        ///
        /// The string is found rather than named: a union-find over the triangles splits the
        /// mesh into connected components, and the string is the one shaped like a string -
        /// the largest ratio of longest side to next longest. On the Malagen bow that is 92
        /// against the body's 10, which is not a close call. Everything else is body.
        ///
        /// The pull direction comes out of the same split. A braced bow's limbs bow away from
        /// the archer, so the vector from the body's centre to the string's points at the
        /// archer, and that is the way the nock travels.
        ///
        /// Cached per shared mesh: this is the only expensive thing the component does, and
        /// every bow in the world is the same mesh.
        /// </summary>
        private static Geometry Resolve(Mesh mesh)
        {
            if (_geometryCache.TryGetValue(mesh.GetInstanceID(), out Geometry cached))
                return cached;

            if (!mesh.isReadable)
            {
                Debug.LogError($"[{nameof(BowEquipmentEntity)}] \"{mesh.name}\" is not readable, so its string cannot be bent. " +
                               "Turn Read/Write on for the bow model (the demo's Open MMORPG > Demo > Build Weapon Prefabs does).");
                _geometryCache[mesh.GetInstanceID()] = null;
                return null;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            // Weld by position first: split vertices at a hard edge are one point of the
            // surface, and left unwelded they would scatter the string across components.
            var welded = new Dictionary<Vector3, int>(vertices.Length);
            var owner = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; ++i)
            {
                if (!welded.TryGetValue(vertices[i], out int id))
                {
                    id = welded.Count;
                    welded[vertices[i]] = id;
                }
                owner[i] = id;
            }

            var parent = new int[welded.Count];
            for (int i = 0; i < parent.Length; ++i)
                parent[i] = i;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Union(parent, owner[triangles[t]], owner[triangles[t + 1]]);
                Union(parent, owner[triangles[t + 1]], owner[triangles[t + 2]]);
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < vertices.Length; ++i)
            {
                int root = Find(parent, owner[i]);
                if (!groups.TryGetValue(root, out List<int> group))
                {
                    group = new List<int>();
                    groups[root] = group;
                }
                group.Add(i);
            }

            if (groups.Count < 2)
            {
                Debug.LogError($"[{nameof(BowEquipmentEntity)}] \"{mesh.name}\" is one piece, so it has no separable string.");
                _geometryCache[mesh.GetInstanceID()] = null;
                return null;
            }

            List<int> stringGroup = null;
            float bestAspect = 0f;
            Bounds stringBounds = default;
            foreach (KeyValuePair<int, List<int>> group in groups)
            {
                Bounds bounds = Measure(vertices, group.Value);
                Vector3 size = bounds.size;
                float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                float shortest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                float second = size.x + size.y + size.z - longest - shortest;
                float aspect = second > 0f ? longest / second : float.MaxValue;
                if (aspect > bestAspect)
                {
                    bestAspect = aspect;
                    stringGroup = group.Value;
                    stringBounds = bounds;
                }
            }

            var body = new List<int>();
            foreach (KeyValuePair<int, List<int>> group in groups)
            {
                if (group.Value != stringGroup)
                    body.AddRange(group.Value);
            }

            Bounds bodyBounds = Measure(vertices, body);
            Vector3 stringSize = stringBounds.size;
            Vector3 along =
                stringSize.x >= stringSize.y && stringSize.x >= stringSize.z ? Vector3.right :
                stringSize.y >= stringSize.z ? Vector3.up : Vector3.forward;

            Vector3 pull = stringBounds.center - bodyBounds.center;
            pull -= along * Vector3.Dot(pull, along);
            if (pull.sqrMagnitude < 1e-12f)
            {
                Debug.LogError($"[{nameof(BowEquipmentEntity)}] \"{mesh.name}\": the string sits on the bow's own axis, " +
                               "so there is no direction to pull it in.");
                _geometryCache[mesh.GetInstanceID()] = null;
                return null;
            }
            pull.Normalize();

            var geometry = new Geometry
            {
                baseVertices = vertices,
                stringVertices = stringGroup.ToArray(),
                bodyVertices = body.ToArray(),
                along = along,
                pull = pull,
                centre = stringBounds.center,
                bounds = mesh.bounds,
            };
            geometry.lowerTip = Vector3.Dot(stringBounds.min - geometry.centre, along);
            geometry.upperTip = Vector3.Dot(stringBounds.max - geometry.centre, along);
            geometry.halfLength = Mathf.Max(
                Mathf.Abs(Vector3.Dot(bodyBounds.min - geometry.centre, along)),
                Mathf.Abs(Vector3.Dot(bodyBounds.max - geometry.centre, along)));

            _geometryCache[mesh.GetInstanceID()] = geometry;
            return geometry;
        }

        private static Bounds Measure(Vector3[] vertices, List<int> indices)
        {
            var bounds = new Bounds(vertices[indices[0]], Vector3.zero);
            for (int i = 1; i < indices.Count; ++i)
                bounds.Encapsulate(vertices[indices[i]]);
            return bounds;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }
    }
}
