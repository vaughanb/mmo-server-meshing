using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG
{
    /// <summary>
    /// Shows an MP bar as whatever the character's class keeps there (<see cref="ClassPower"/>):
    /// red "RAGE" for a warrior, orange "FOCUS" for a ranger, the prefab's own blue "MP" for a mage.
    /// The numbers are the kit's own (`UIGageValue` writes only "current/max"); this changes the
    /// fill's colour and the label beside it, and only when the class shown changes.
    ///
    /// <see cref="source"/> is the kit UI that owns the bar - a `UICharacter` (the HUD frame, the
    /// character sheet) or a `UISocialCharacter` (party frames) - and is read for the class of
    /// whoever it is showing, so a mage's party frame shows a warrior member's bar red.
    ///
    /// **The words the kit owns.** The skill tooltip's cost line ("Consume Mp: 20") and the
    /// "Not enough Mp" refusal are the kit's localised strings, shared by every skill. Both only
    /// ever describe the local player's own skills, so the bar marked <see cref="isLocalPlayer"/>
    /// (the HUD frame) rewrites them in <c>LanguageManager.Texts</c> for its class and puts the
    /// originals back for a mana class. Any language's text is replaced, not just English: the demo
    /// ships one.
    ///
    /// Wired onto the HUD, the character sheet and both party frames by `DemoHudBuilder` (Build HUD).
    /// </summary>
    public class ClassPowerBar : MonoBehaviour
    {
        [Tooltip("The kit UI whose MP bar this is: a UICharacter or a UISocialCharacter.")]
        public MonoBehaviour source;

        [Tooltip("The bar's fill. Its colour in the prefab is used for mana.")]
        public Graphic fill;

        [Tooltip("The label beside the bar, if it has one. Its text in the prefab is used for mana.")]
        public Text label;

        [Tooltip("On the local player's own frame: also rewords the skill-cost line and the out-of-power message.")]
        public bool isLocalPlayer;

        [Header("Rage")]
        public string rageLabel = "RAGE";
        public Color rageColor = new Color(0.78f, 0.12f, 0.10f, 1f);

        [Header("Focus")]
        public string focusLabel = "FOCUS";
        public Color focusColor = new Color(1f, 0.50f, 0.15f, 1f);

        [Tooltip("When on, the label's case follows the prefab's (\"MP\" -> \"RAGE\"; \"Mp\" -> \"Rage\").")]
        public bool matchLabelCase = true;

        private Color _manaColor;
        private string _manaLabel;
        private bool _captured;
        private ClassPowerType? _shown;

        private const string ConsumeKey = "UI_FORMAT_CONSUME_MP";
        private const string NotEnoughKey = "UI_ERROR_NOT_ENOUGH_MP";
        private static readonly Dictionary<string, string> s_originalTexts = new Dictionary<string, string>();
        private static ClassPowerType? s_textsFor;

        private void Awake()
        {
            Capture();
        }

        private void Capture()
        {
            if (_captured)
                return;
            _captured = true;
            if (fill != null)
                _manaColor = fill.color;
            if (label != null)
                _manaLabel = label.text;
        }

        private void LateUpdate()
        {
            ClassPowerType power = ClassPower.OfClass(ShownClassId());
            if (_shown == power)
                return;
            _shown = power;
            Apply(power);
            if (isLocalPlayer)
                ApplyTexts(power);
        }

        /// <summary>The data id of the character the source UI shows, or 0 for none.</summary>
        private int ShownClassId()
        {
            var character = source as UICharacter;
            if (character != null)
            {
                ICharacterData data = character.Data;
                return data is IPlayerCharacterData ? data.DataId : 0;
            }
            var social = source as UISocialCharacter;
            if (social != null)
                return social.Data.dataId;
            return 0;
        }

        private void Apply(ClassPowerType power)
        {
            Capture();
            if (fill != null)
            {
                switch (power)
                {
                    case ClassPowerType.Rage: fill.color = rageColor; break;
                    case ClassPowerType.Focus: fill.color = focusColor; break;
                    default: fill.color = _manaColor; break;
                }
            }
            if (label != null)
            {
                switch (power)
                {
                    case ClassPowerType.Rage: label.text = InCase(rageLabel); break;
                    case ClassPowerType.Focus: label.text = InCase(focusLabel); break;
                    default: label.text = _manaLabel; break;
                }
            }
        }

        /// <summary>"RAGE" for an all-capitals prefab label ("MP"), "Rage" for a title-case one ("Mp").</summary>
        private string InCase(string word)
        {
            if (!matchLabelCase || string.IsNullOrEmpty(_manaLabel) || string.IsNullOrEmpty(word))
                return word;
            bool allCaps = _manaLabel.ToUpperInvariant() == _manaLabel;
            if (allCaps)
                return word.ToUpperInvariant();
            return char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
        }

        private static void ApplyTexts(ClassPowerType power)
        {
            if (s_textsFor == power)
                return;
            s_textsFor = power;
            switch (power)
            {
                case ClassPowerType.Rage:
                    SetText(ConsumeKey, "Rage: {0}");
                    SetText(NotEnoughKey, "Not enough rage");
                    break;
                case ClassPowerType.Focus:
                    SetText(ConsumeKey, "Focus: {0}");
                    SetText(NotEnoughKey, "Not enough focus");
                    break;
                default:
                    RestoreText(ConsumeKey);
                    RestoreText(NotEnoughKey);
                    break;
            }
        }

        private static void SetText(string key, string value)
        {
            if (!s_originalTexts.ContainsKey(key))
                s_originalTexts[key] = LanguageManager.Texts.TryGetValue(key, out string original) ? original : null;
            LanguageManager.Texts[key] = value;
        }

        private static void RestoreText(string key)
        {
            if (!s_originalTexts.TryGetValue(key, out string original))
                return;
            // Null means the current language never had the key and the kit fell back to its
            // built-in default; taking ours out restores that fallback.
            if (original == null)
                LanguageManager.Texts.Remove(key);
            else
                LanguageManager.Texts[key] = original;
            s_originalTexts.Remove(key);
        }
    }
}
