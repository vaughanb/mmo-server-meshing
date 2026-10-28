using System.Collections.Generic;
using Cysharp.Text;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Tidies what the kit's <see cref="UICharacter"/> writes into a sheet of one-line stat rows,
    /// straight after it writes it (<c>onUpdateData</c>), and again when the window opens.
    ///
    /// **Damage on one line.** The kit prints one line per hand - right, then left - into the one
    /// damage text, and prints the left even when nothing is in it: a ranger, whose bow the kit
    /// counts as a right-hand weapon, got "69~116" over "0~0", and a one-line row best-fitted the
    /// pair down past reading. Here a hand whose damage is all zeros is dropped and what is left
    /// is joined on one line, so a two-weapon character still sees both.
    ///
    /// **Zero, not nothing.** The kit hides a stat's value text when the stat is zero
    /// (<c>CharacterStatsTextGenerateData.GetSingleStatsText</c>), which in a sheet of labelled rows
    /// leaves a label with no number beside it - Block Rate on anyone without a shield read as
    /// broken. Those values are written back as zero, in the row's own format.
    ///
    /// **What armour is worth.** Armour is a flat number and resistance a percentage, so a
    /// fully armoured character read "Physical 29 / 0%" and looked unprotected. Each armour
    /// value is followed by the share of a hit it takes off, e.g. "29  (-22%)".
    ///
    /// Belongs beside the dialog's <see cref="UICharacter"/>; the demo's character sheet builder
    /// puts it there.
    /// </summary>
    [RequireComponent(typeof(UICharacter))]
    [DisallowMultipleComponent]
    public class UICharacterStatsReadable : MonoBehaviour
    {
        [Tooltip("Placed between the hands' damage when both have some.")]
        public string handSeparator = "  /  ";

        [Tooltip("Number format for a rate shown as zero, before the row's own format (\"{0}%\") is applied. The kit's rates use N2.")]
        public string zeroRateFormat = "N2";

        [Tooltip("Put after a non-zero armour value: how much of a hit of that element the armour takes off, asked of the gameplay rule. {0} = the percentage. Empty leaves armour as the bare number.")]
        public string armorReductionFormat = "  (-{0}%)";

        private UICharacter _ui;
        private readonly Dictionary<DamageElement, float> _noResistances = new Dictionary<DamageElement, float>();
        private readonly Dictionary<DamageElement, float> _oneArmor = new Dictionary<DamageElement, float>();

        private void Awake()
        {
            _ui = GetComponent<UICharacter>();
        }

        private void OnEnable()
        {
            if (_ui == null)
                return;
            _ui.onUpdateData += OnUpdated;
            // The kit writes the sheet whenever the character changes, shown or not, and opening
            // the window does not write it again - so what it wrote while the window was shut
            // (equipping from the bag, a level-up) is tidied here.
            if (_ui.Data != null)
                OnUpdated(_ui.Data);
        }

        private void OnDisable()
        {
            if (_ui != null)
                _ui.onUpdateData -= OnUpdated;
        }

        private void OnUpdated(ICharacterData data)
        {
            OneDamageLine(_ui.uiTextAllDamages);
            ShowArmorReduction(_ui.uiCharacterArmors);
            UICharacterStats stats = _ui.uiCharacterStats;
            if (stats == null)
                return;
            ShowZero(stats.uiTextAccuracy, stats.formatKeyAccuracyStats, false);
            ShowZero(stats.uiTextEvasion, stats.formatKeyEvasionStats, false);
            ShowZero(stats.uiTextCriRate, stats.formatKeyCriRateStats, true);
            ShowZero(stats.uiTextCriDmgRate, stats.formatKeyCriDmgRateStats, true);
            ShowZero(stats.uiTextBlockRate, stats.formatKeyBlockRateStats, true);
            ShowZero(stats.uiTextBlockDmgRate, stats.formatKeyBlockDmgRateStats, true);
        }

        private void OneDamageLine(TextWrapper text)
        {
            if (text == null)
                return;
            string all = text.text;
            if (string.IsNullOrEmpty(all) || all.IndexOf('\n') < 0)
                return;
            string[] hands = all.Split('\n');
            using (Utf16ValueStringBuilder line = ZString.CreateStringBuilder())
            {
                foreach (string hand in hands)
                {
                    if (!HasNonZeroDigit(hand))
                        continue;
                    if (line.Length > 0)
                        line.Append(handSeparator);
                    line.Append(hand);
                }
                // No damage anywhere still reads as damage: the first hand's zeros.
                text.text = line.Length > 0 ? line.ToString() : hands[0];
            }
        }

        private static bool HasNonZeroDigit(string text)
        {
            foreach (char c in text)
            {
                if (c >= '1' && c <= '9')
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Each armour value followed by the share of a hit it takes off. The rule asked is the
        /// game's own, with no resistance, for a hit of 100 - so the figure follows whatever
        /// formula the rule uses (the kit's: 100 / (100 + armour)).
        /// </summary>
        private void ShowArmorReduction(UIArmorAmounts armors)
        {
            if (armors == null || armors.Data == null || string.IsNullOrEmpty(armorReductionFormat) || GameInstance.Singleton == null)
                return;
            BaseGameplayRule rule = GameInstance.Singleton.GameplayRule;
            if (rule == null)
                return;
            foreach (KeyValuePair<DamageElement, float> armor in armors.Data)
            {
                if (armor.Key == null || armor.Value <= 0f)
                    continue;
                UIArmorTextPair pair;
                if (!armors.CacheTextAmounts.TryGetValue(armor.Key, out pair) || pair.uiText == null)
                    continue;
                _oneArmor.Clear();
                _oneArmor[armor.Key] = armor.Value;
                float reduced = 100f - rule.GetDamageReducedByResistance(_noResistances, _oneArmor, 100f, armor.Key);
                if (reduced < 0.5f)
                    continue;
                string suffix = ZString.Format(armorReductionFormat, reduced.ToString("0"));
                // Opening the window tidies a text the kit may not have rewritten since.
                if (!pair.uiText.text.EndsWith(suffix))
                    pair.uiText.text += suffix;
            }
        }

        /// <summary>A value text the kit hid because its stat is zero, shown again saying so.</summary>
        private void ShowZero(TextWrapper text, UILocaleKeySetting format, bool rate)
        {
            if (text == null || text.gameObject.activeSelf)
                return;
            text.text = ZString.Format(LanguageManager.GetText(format), rate ? 0f.ToString(zeroRateFormat) : "0");
            text.gameObject.SetActive(true);
        }
    }
}
