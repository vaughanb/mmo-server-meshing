using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// How big a character is: the size chosen on the create screen, applied to every body
    /// the entity can show.
    ///
    /// This is not the kit's body-part system and could not be. That system swaps and
    /// recolours objects it instantiates as fake equipment, one mesh at a time; size is not
    /// a mesh, it is the whole character - the body, the hair hanging off it, the sword in
    /// its hand, and the seated copy that rides the horse. So it is applied here, as a scale
    /// on each <see cref="BaseCharacterModel"/> the entity owns. Everything a model carries
    /// hangs underneath it, so equipment, hair and particle sockets come along for free, and
    /// a garment that appears later needs nothing done to it.
    ///
    /// **The ramp is authored in metres, not in percentages, and each body gets its own
    /// multipliers.** The two bodies are not the same height to start with - the male's
    /// crown measures 1.81m and the female's 1.77m - so one shared multiplier would put the
    /// two ends of the slider in two different places for them and the readout would be a
    /// lie for one of the two. The builder measures each body and divides, which is what
    /// makes "1.75 m" mean 1.75 metres whichever body is on screen. See
    /// <c>DemoCharacterSizeBuilder</c>.
    ///
    /// **Only the look changes. The capsule does not.** The character controller, the
    /// collider and the navmesh agent all keep the one height the island is built and baked
    /// for, so size costs nothing in reach, in what can be hit, or in where a character can
    /// walk - a tall character has no advantage over a short one, and neither of them can
    /// get wedged in a doorway the stock body fits through. What does move is the row of
    /// anchors the nameplate, the chat bubble and the damage numbers hang from
    /// (<see cref="anchors"/>): those are scaled with the body, or a short character wears
    /// their name half a metre above their head.
    ///
    /// **Size 0 means "not chosen" and scales nothing**, so a character saved before this
    /// existed - and any character whose data does not carry the key - stands exactly as it
    /// did. The ramp is 1-based.
    ///
    /// Belongs on the entity root, beside <see cref="SkinTone"/>. It is driven from
    /// three places: the create screen (<see cref="UICharacterSizeSlider"/>), the select screen
    /// (<see cref="UICharacterSelectAppearance"/>), and in the world from the entity's own synced
    /// PublicInts.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterSize : MonoBehaviour
    {
        /// <summary>The PublicInts key the chosen size is saved under, beside SKIN and HAIR.</summary>
        public const string SettingId = "SIZE";

        [Tooltip("The multiplier for each size, measured for THIS body. Element 0 of this array is size 1.")]
        public float[] scales = new float[0];

        [Tooltip("Names for the sizes, in the same order, for the slider's label.")]
        public string[] sizeTitles = new string[0];

        [Tooltip("The size the slider starts on: the one nearest this body's own height.")]
        public int defaultSize = 1;

        [Tooltip("The measured height of this body at scale one, in metres. For the readout only.")]
        public float standingHeight = 1.8f;

        [Tooltip("The nameplate, chat bubble and damage-number anchors, which ride up and down with the body.")]
        public Transform[] anchors = new Transform[0];

        /// <summary>
        /// Every body the entity can show: the main model and, on a player, the seated copy
        /// that rides the horse (see DemoMountBuilder.BuildRider). Both are scaled, or a
        /// character would change size the moment it climbed onto the horse. The seated
        /// copy is scaled about the entity's own origin like the standing one, so a tall
        /// rider sits a little higher in the saddle and a short one a little lower - a
        /// couple of centimetres at the ends of the ramp, which is the right direction
        /// anyway.
        /// </summary>
        private BaseCharacterModel[] _models;
        /// <summary>What each model was built at. Scaling is relative to this, never an assignment of it.</summary>
        private Vector3[] _modelScales;
        private Vector3[] _anchorPositions;
        private BasePlayerCharacterEntity _entity;
        private int _size;

        /// <summary>The size currently applied. 0 is the untouched stock body.</summary>
        public int Size { get { return _size; } }

        public int MaxSize { get { return scales.Length; } }

        public static int HashedSettingId { get { return SettingId.GenerateHashId(); } }

        private void Awake()
        {
            Cache();
        }

        private void OnDestroy()
        {
            if (_entity != null)
                _entity.onPublicIntsOperation -= OnPublicIntsOperation;
        }

        private void Start()
        {
            // In the world the size arrives with the rest of the character's synced data,
            // which has not landed on the first frame - hence the subscription as well as
            // the read. On the menu screens there is no live entity and nothing updates
            // itself, so those screens call Apply directly instead.
            if (_entity == null)
                return;
            // **Not on a menu preview, and the difference is not cosmetic.** The create and
            // select screens put a whole live entity on screen - `InstantiateModel` builds
            // the prefab and then switches off every `LiteNetLibBehaviour` on it, the entity
            // component included - so this Start runs there too, a frame after the screen
            // has already applied the slider's size to it. The entity's own PublicInts are
            // not that character's: they are whatever the kit's body-part components wrote
            // onto the preview, four zeroed hair and beard keys with no size among them.
            // Reading them here put every preview straight back to the stock body, and the
            // player only saw their choice once the character was created. Nothing on a
            // screen ever raises the event either, so there is nothing to subscribe to.
            //
            // The screens drive Apply themselves: UICharacterSizeSlider on create,
            // UICharacterSelectAppearance on select.
            if (!_entity.enabled)
                return;
            _entity.onPublicIntsOperation += OnPublicIntsOperation;
            ApplyFrom(_entity.PublicInts);
        }

        private void Cache()
        {
            if (_models != null)
                return;
            _models = GetComponentsInChildren<BaseCharacterModel>(true);
            _modelScales = new Vector3[_models.Length];
            for (int i = 0; i < _models.Length; ++i)
                _modelScales[i] = _models[i].transform.localScale;
            _anchorPositions = new Vector3[anchors.Length];
            for (int i = 0; i < anchors.Length; ++i)
                _anchorPositions[i] = anchors[i] != null ? anchors[i].localPosition : Vector3.zero;
            _entity = GetComponent<BasePlayerCharacterEntity>();
        }

        private void OnPublicIntsOperation(LiteNetLibSyncListOp operation, int index,
            CharacterDataInt32 oldItem, CharacterDataInt32 newItem)
        {
            if (_entity != null)
                ApplyFrom(_entity.PublicInts);
        }

        /// <summary>Applies whichever size a character's saved data carries, or none if it carries no size.</summary>
        public void ApplyFrom(IList<CharacterDataInt32> publicInts)
        {
            int size = 0;
            if (publicInts != null)
            {
                int hashed = HashedSettingId;
                for (int i = 0; i < publicInts.Count; ++i)
                {
                    if (publicInts[i].hashedKey != hashed)
                        continue;
                    size = publicInts[i].value;
                    break;
                }
            }
            Apply(size);
        }

        /// <summary>
        /// Sizes every body on the character. Size 0 is the stock body and writes scale one,
        /// which is not the same as doing nothing: a character whose size is cleared has to
        /// lose the scale it was already wearing.
        /// </summary>
        public void Apply(int size)
        {
            // The screens can reach a character before it has ever been enabled, and a
            // component that has not run Awake would otherwise scale by a base of zero.
            Cache();
            _size = Mathf.Clamp(size, 0, scales.Length);
            float scale = ScaleFor(_size);
            for (int i = 0; i < _models.Length; ++i)
            {
                if (_models[i] != null)
                    _models[i].transform.localScale = _modelScales[i] * scale;
            }
            for (int i = 0; i < anchors.Length && i < _anchorPositions.Length; ++i)
            {
                if (anchors[i] != null)
                    anchors[i].localPosition = _anchorPositions[i] * scale;
            }
        }

        /// <summary>The multiplier for a size, or one for the stock body.</summary>
        public float ScaleFor(int size)
        {
            if (size <= 0 || size > scales.Length)
                return 1f;
            return scales[size - 1];
        }

        /// <summary>What a size actually measures, in metres. What the slider's label reads out.</summary>
        public float HeightFor(int size)
        {
            return standingHeight * ScaleFor(size);
        }
    }
}
