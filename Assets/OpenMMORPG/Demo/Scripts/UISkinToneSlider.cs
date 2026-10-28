using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG
{
    /// <summary>
    /// The skin tone slider on the character create screen.
    ///
    /// The kit has no UI for this. <see cref="UIBodyPartManager"/> only builds grids of
    /// option tiles, and it cannot serve skin anyway - see <see cref="SkinTone"/> for
    /// why the body-part system cannot reach the skin baked into the outfit sleeves. So the
    /// slider is the demo's own, and it drives <see cref="SkinTone"/> on the previewed
    /// character directly.
    ///
    /// **The choice has to be written into the screen's own PublicInts list, not just onto
    /// the entity.** <see cref="UICharacterCreate"/> keeps its own list and hands *that* to
    /// the server when the character is created; it never reads the preview entity back. A
    /// tone applied only to the entity would look perfect on the create screen and arrive
    /// in the world as nothing at all.
    ///
    /// **And it has to be written again every time the body changes.** The screen clears
    /// that list in `SetSelectCharacter`, which runs on every switch between Male and
    /// Female, and the kit's own managers repopulate it from their re-selected options
    /// immediately afterwards. This listens to the same event those managers are wired on,
    /// so the tone is put back the moment it is dropped - and the player keeps the tone they
    /// picked when they try the other body, rather than being reset to the default.
    ///
    /// Belongs on the same object as the <see cref="UICharacterCreate"/> it serves, beside
    /// <see cref="UICharacterPreviewReveal"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class UISkinToneSlider : MonoBehaviour
    {
        public Slider slider;
        [Tooltip("Shows the name of the tone under the slider. Optional.")]
        public Text label;
        [Tooltip("Shows the tone itself as a colour chip. Optional.")]
        public Image swatch;

        private UICharacterCreate _create;
        private SkinTone _tone;
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
                Debug.LogWarning($"[{nameof(UISkinToneSlider)}] Nothing to listen to on \"{name}\". " +
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
            _tone = null;
            if (model != null)
            {
                BasePlayerCharacterEntity entity = model.GetComponentInParent<BasePlayerCharacterEntity>(true);
                if (entity != null)
                    _tone = entity.GetComponent<SkinTone>();
            }

            bool available = _tone != null && _tone.MaxTone > 0;
            // A body with no tones configured hides the window rather than showing a slider
            // that does nothing - the same thing the kit does with the beard on a female.
            if (slider != null && slider.transform.parent != null)
                slider.transform.parent.gameObject.SetActive(available);
            if (!available)
                return;

            if (_chosen <= 0)
                _chosen = Mathf.Clamp(_tone.defaultTone, 1, _tone.MaxTone);
            _chosen = Mathf.Clamp(_chosen, 1, _tone.MaxTone);

            if (slider != null)
            {
                slider.wholeNumbers = true;
                slider.minValue = 1;
                slider.maxValue = _tone.MaxTone;
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
            if (_tone == null)
                return;
            _tone.Apply(_chosen);
            if (_create != null && _create.PublicInts != null)
                _create.PublicInts.SetValue(SkinTone.HashedSettingId, _chosen);
            ShowChoice();
        }

        private void ShowChoice()
        {
            int index = _chosen - 1;
            if (label != null)
            {
                label.text = _tone != null && index >= 0 && index < _tone.toneTitles.Length
                    ? _tone.toneTitles[index]
                    : string.Empty;
            }
            if (swatch != null && _tone != null && index >= 0 && index < _tone.tones.Length)
                swatch.color = _tone.tones[index];
        }
    }
}
