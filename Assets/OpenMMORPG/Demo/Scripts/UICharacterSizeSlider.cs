using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG
{
    /// <summary>
    /// The size slider on the character create screen.
    ///
    /// The kit has no UI for this. <see cref="UIBodyPartManager"/> only builds grids of
    /// option tiles, and size is not an option tile anyway - see
    /// <see cref="CharacterSize"/> for why it is a scale on the whole model rather than
    /// a body part. So the slider is the demo's own, and it drives
    /// <see cref="CharacterSize"/> on the previewed character directly.
    ///
    /// **The choice has to be written into the screen's own PublicInts list, not just onto
    /// the entity.** <see cref="UICharacterCreate"/> keeps its own list and hands *that* to
    /// the server when the character is created; it never reads the preview entity back. A
    /// size applied only to the entity would look right on the create screen and arrive in
    /// the world as nothing at all.
    ///
    /// **And it has to be written again every time the body changes.** The screen clears
    /// that list in `SetSelectCharacter`, which runs on every switch between Male and
    /// Female, and the kit's own managers repopulate it from their re-selected options
    /// immediately afterwards. This listens to the same event those managers are wired on,
    /// so the size is put back the moment it is dropped - and the player keeps the size they
    /// picked when they try the other body, rather than being reset to the default.
    ///
    /// The two bodies do not share a ramp - each carries its own multipliers, measured
    /// against its own height - but they share the **stop**, which is what keeps a switch
    /// between them honest: the slider stays on "1.75 m" and both bodies stand 1.75 metres
    /// tall there.
    ///
    /// Belongs on the same object as the <see cref="UICharacterCreate"/> it serves, beside
    /// <see cref="UISkinToneSlider"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class UICharacterSizeSlider : MonoBehaviour
    {
        public Slider slider;
        [Tooltip("Shows the height the current stop measures, under the slider. Optional.")]
        public Text label;

        private UICharacterCreate _create;
        private CharacterSize _size;
        /// <summary>What the player has picked, kept across body switches. 0 until the first preview.</summary>
        private int _chosen;

        private void Awake()
        {
            // Awake rather than OnEnable, matching UICharacterPreviewReveal: the screen
            // loads and selects its first character from OnEnable, and only drops its
            // listeners in OnDestroy.
            _create = GetComponent<UICharacterCreate>();
            if (_create == null)
            {
                Debug.LogWarning($"[{nameof(UICharacterSizeSlider)}] Nothing to listen to on \"{name}\". " +
                                 $"It belongs on the same object as a {nameof(UICharacterCreate)}.");
                enabled = false;
                return;
            }
            _create.eventOnShowInstantiatedCharacter.AddListener(OnShowCharacter);
            if (slider != null)
                slider.onValueChanged.AddListener(OnSliderChanged);
        }

        private void OnShowCharacter(BaseCharacterModel model)
        {
            _size = null;
            if (model != null)
            {
                BasePlayerCharacterEntity entity = model.GetComponentInParent<BasePlayerCharacterEntity>(true);
                if (entity != null)
                    _size = entity.GetComponent<CharacterSize>();
            }

            bool available = _size != null && _size.MaxSize > 0;
            // A body with no sizes configured hides the window rather than showing a slider
            // that does nothing - the same thing the kit does with the beard on a female.
            if (slider != null && slider.transform.parent != null)
                slider.transform.parent.gameObject.SetActive(available);
            if (!available)
                return;

            if (_chosen <= 0)
                _chosen = Mathf.Clamp(_size.defaultSize, 1, _size.MaxSize);
            _chosen = Mathf.Clamp(_chosen, 1, _size.MaxSize);

            if (slider != null)
            {
                slider.wholeNumbers = true;
                slider.minValue = 1;
                slider.maxValue = _size.MaxSize;
                // Without the guard the assignment raises onValueChanged, which would write
                // to a PublicInts list the screen is in the middle of rebuilding.
                slider.SetValueWithoutNotify(_chosen);
            }
            ApplyChosen();
        }

        private void OnSliderChanged(float value)
        {
            _chosen = Mathf.RoundToInt(value);
            ApplyChosen();
        }

        private void ApplyChosen()
        {
            if (_size == null)
                return;
            _size.Apply(_chosen);
            if (_create != null && _create.PublicInts != null)
                _create.PublicInts.SetValue(CharacterSize.HashedSettingId, _chosen);
            ShowChoice();
        }

        private void ShowChoice()
        {
            if (label == null)
                return;
            int index = _chosen - 1;
            label.text = _size != null && index >= 0 && index < _size.sizeTitles.Length
                ? _size.sizeTitles[index]
                : string.Empty;
        }
    }
}
