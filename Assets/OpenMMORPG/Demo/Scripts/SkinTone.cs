using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A character's skin tone, applied to the body and to the skin the outfits carry.
    ///
    /// This is not the kit's body-part system, and it could not be. That system works by
    /// injecting a fake piece of equipment and recolouring the objects it instantiates for
    /// it, so it can only reach things it owns - a hairstyle, a beard. Skin is not like
    /// that: it is on the character's own five bare meshes, and it is also baked into the
    /// sleeves of three outfits, where the forearms and hands show through
    /// (PeasantSleeves, RangerBracers and WizardSleeves each carry an MI_Skin_* submesh).
    /// Tint only the body and every level-one character has a face that does not match its
    /// own arms. So the tone is applied here, across everything wearing a skin material,
    /// and re-applied through <see cref="BaseCharacterModel.onInstantiatedEquipment"/>
    /// whenever a garment appears.
    ///
    /// **Every tone is a measured correction, not a raw tint.** The body texture and the
    /// outfit texture are two different sheets whose skin areas do not average to the same
    /// colour, so one multiplier lands in two different places on them. The builder
    /// measures each material's mean skin colour - sampling its own texture at the UVs of
    /// the submesh that wears it, area-weighted, in linear space - and stores it in
    /// <see cref="skinMaterials"/>. The tint for a material is then its target divided by
    /// its own mean, which puts every skin surface on the same colour whatever sheet it
    /// came from. That is what makes the seam at the cuff invisible.
    ///
    /// **Tone 0 means "not chosen" and applies no tint at all**, so a character saved
    /// before this existed - and any character whose data does not carry the key - looks
    /// exactly as it did. The ramp is 1-based.
    ///
    /// Belongs on the entity root, beside the kit's body-part components. It is driven from
    /// three places: the create screen (<see cref="UISkinToneSlider"/>), the select screen
    /// (<see cref="UICharacterSelectAppearance"/>), and in the world from the entity's own synced
    /// PublicInts.
    /// </summary>
    [DisallowMultipleComponent]
    public class SkinTone : MonoBehaviour
    {
        /// <summary>The PublicInts key the chosen tone is saved under, beside HAIR and HAIR_COLOR.</summary>
        public const string SettingId = "SKIN";

        [System.Serializable]
        public class SkinMaterial
        {
            [Tooltip("Material name to match, without the runtime \" (Instance)\" suffix.")]
            public string materialName = string.Empty;
            [Tooltip("The mean colour of this material's skin area, in LINEAR space, measured by the builder.")]
            public Color measuredMean = Color.white;
        }

        [Tooltip("Every material that draws skin, with the mean colour the builder measured for it.")]
        public SkinMaterial[] skinMaterials = new SkinMaterial[0];

        [Tooltip("The tone ramp, darkest first, authored in sRGB. Element 0 of this array is tone 1.")]
        public Color[] tones = new Color[0];

        [Tooltip("Names for the tones, in the same order, for the slider's label.")]
        public string[] toneTitles = new string[0];

        [Tooltip("The tone the slider starts on: the one nearest the stock look.")]
        public int defaultTone = 1;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// Every body the entity can show: the main model and, on a player, the seated copy
        /// that rides the horse (see DemoMountBuilder.BuildRider). Both are painted, and both
        /// report their garments, or the rider would mount in the stock skin.
        /// </summary>
        private BaseCharacterModel[] _models = new BaseCharacterModel[0];
        private BasePlayerCharacterEntity _entity;
        private int _tone;

        /// <summary>The tone currently applied. 0 is the untinted stock look.</summary>
        public int Tone { get { return _tone; } }

        public int MaxTone { get { return tones.Length; } }

        public static int HashedSettingId { get { return SettingId.GenerateHashId(); } }

        private void Awake()
        {
            _models = GetComponentsInChildren<BaseCharacterModel>(true);
            _entity = GetComponent<BasePlayerCharacterEntity>();
            foreach (BaseCharacterModel model in _models)
                model.onInstantiatedEquipment += OnInstantiatedEquipment;
        }

        private void OnDestroy()
        {
            foreach (BaseCharacterModel model in _models)
            {
                if (model != null)
                    model.onInstantiatedEquipment -= OnInstantiatedEquipment;
            }
            if (_entity != null)
                _entity.onPublicIntsOperation -= OnPublicIntsOperation;
        }

        private void Start()
        {
            // In the world the tone arrives with the rest of the character's synced data,
            // which has not landed on the first frame - hence the subscription as well as
            // the read. On the menu screens there is no live entity and nothing updates
            // itself, so those screens call Apply directly instead.
            if (_entity == null)
                return;
            // Not on a menu preview: that is a live entity with its networking switched off,
            // whose Start runs a frame after the screen has already tinted it, and whose
            // PublicInts are the kit's own zeroed hair keys rather than the character's.
            // Reading them washed the tone straight back off, so every preview showed the
            // stock skin however the slider was set. See CharacterSize.Start, which has
            // the same shape and the longer note.
            if (!_entity.enabled)
                return;
            _entity.onPublicIntsOperation += OnPublicIntsOperation;
            ApplyFrom(_entity.PublicInts);
        }

        private void OnPublicIntsOperation(LiteNetLibSyncListOp operation, int index,
            CharacterDataInt32 oldItem, CharacterDataInt32 newItem)
        {
            if (_entity != null)
                ApplyFrom(_entity.PublicInts);
        }

        /// <summary>Applies whichever tone a character's saved data carries, or none if it carries no tone.</summary>
        public void ApplyFrom(IList<CharacterDataInt32> publicInts)
        {
            int tone = 0;
            if (publicInts != null)
            {
                int hashed = HashedSettingId;
                for (int i = 0; i < publicInts.Count; ++i)
                {
                    if (publicInts[i].hashedKey != hashed)
                        continue;
                    tone = publicInts[i].value;
                    break;
                }
            }
            Apply(tone);
        }

        /// <summary>
        /// Paints every skin surface on the character. Tone 0 is the stock look and writes
        /// white, which is not the same as doing nothing: a character whose tone is cleared
        /// has to lose the tint it was already wearing.
        /// </summary>
        public void Apply(int tone)
        {
            _tone = Mathf.Clamp(tone, 0, tones.Length);
            foreach (BaseCharacterModel model in _models)
            {
                if (model == null)
                    continue;
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                    Paint(renderer);
            }
        }

        /// <summary>
        /// The tint for one skin material: the tone divided by what that material's skin
        /// already averages, so two different sheets end up at the same colour. Both sides
        /// of the division are linear.
        ///
        /// **The result is handed back in gamma space, and that is not a rounding detail.**
        /// This project renders in Linear, and <see cref="Material.SetColor"/> treats a
        /// colour property as sRGB and converts it on the way to the shader - so a linear
        /// multiplier passed in raw arrives as roughly its own 2.2nd power. Measured on the
        /// bare arms under an unlit shader: `_BaseColor` 0.5 rendered at 0.21 of the
        /// untinted albedo, not 0.5, and 0.25 rendered at 0.05. The mid tone survived it
        /// because its tint is about 1 and 1 raised to anything is 1, which is exactly the
        /// sort of coincidence that lets this kind of bug through a spot check.
        /// </summary>
        public Color TintFor(SkinMaterial skin)
        {
            if (_tone <= 0 || _tone > tones.Length)
                return Color.white;
            Color target = tones[_tone - 1].linear;
            Color mean = skin.measuredMean;
            var linear = new Color(
                mean.r > 0.0001f ? target.r / mean.r : 1f,
                mean.g > 0.0001f ? target.g / mean.g : 1f,
                mean.b > 0.0001f ? target.b / mean.b : 1f,
                1f);
            return linear.gamma;
        }

        /// <summary>
        /// Tints each skin slot through a <see cref="MaterialPropertyBlock"/> on that slot, which
        /// keeps one character's tone off every other character wearing the same garment without
        /// copying a single material.
        ///
        /// It used to read `renderer.materials`, which clones the renderer's materials - and a
        /// clone is not freed when its renderer is: every garment the kit put on and took off
        /// again left its copies behind until the next scene load, on every client, for every
        /// player who changed gear, and again each time the character sheet dressed its paper
        /// doll (found in review, 2026-09-29). A block belongs to the renderer and goes with it.
        ///
        /// Slots that are not skin have their block cleared, so a slot the kit has since swapped
        /// to another material does not keep a stale tint.
        /// </summary>
        private void Paint(Renderer renderer)
        {
            if (renderer == null)
                return;
            if (_block == null)
                _block = new MaterialPropertyBlock();
            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; ++i)
            {
                SkinMaterial skin = Match(shared[i]);
                _block.Clear();
                if (skin != null)
                    _block.SetColor(BaseColorId, TintFor(skin));
                else if (!renderer.HasPropertyBlock())
                    continue;
                renderer.SetPropertyBlock(_block, i);
            }
        }

        private MaterialPropertyBlock _block;

        /// <summary>
        /// Matches by name prefix, because a material that has been painted once is an
        /// instance and Unity has renamed it to "MI_Skin_Male (Instance)".
        /// </summary>
        public SkinMaterial Match(Material material)
        {
            if (material == null)
                return null;
            for (int i = 0; i < skinMaterials.Length; ++i)
            {
                if (!string.IsNullOrEmpty(skinMaterials[i].materialName) &&
                    material.name.StartsWith(skinMaterials[i].materialName))
                    return skinMaterials[i];
            }
            return null;
        }

        /// <summary>
        /// A garment has just appeared on the character. Only the new object needs painting,
        /// and it needs it here rather than at the next Apply, because nothing else will ask.
        /// </summary>
        private void OnInstantiatedEquipment(
            BaseCharacterModel target,
            EquipmentModel model,
            GameObject instantiatedObject,
            BaseEquipmentEntity instantiatedEntity,
            EquipmentInstantiatedObjectGroup instantiatedObjectGroup,
            EquipmentContainer equipmentContainer)
        {
            if (instantiatedObject == null)
                return;
            foreach (Renderer renderer in instantiatedObject.GetComponentsInChildren<Renderer>(true))
                Paint(renderer);
        }
    }
}
