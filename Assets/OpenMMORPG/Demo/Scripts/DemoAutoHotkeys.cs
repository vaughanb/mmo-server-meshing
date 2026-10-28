using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Puts every skill the character can cast on the first free slot of the hotkey bar.
    ///
    /// The kit leaves the bar empty and expects the player to drag skills onto it from the skills
    /// window. That is the right default for a game somebody will play for weeks; it is the wrong
    /// one for a demo, where a new class's abilities should be one keypress away the moment they
    /// unlock rather than behind a window and a drag nobody has been told about.
    ///
    /// **Only `Active` skills.** Passives cannot be pressed, and the kit's own bar filters to
    /// `SkillType.Active` for exactly that reason - putting a passive on a key would give the
    /// player a button that does nothing.
    ///
    /// **The skills are read from the character's *cache*, not from its `Skills` list.** That list
    /// holds only what the player has explicitly learned, and a class's own abilities are not in
    /// it: `PlayerCharacter` carries a `skills` array and hands them out through `GetSkillLevels`
    /// by level, so a brand new Warrior has Cleave without a `CharacterSkill` for it anywhere.
    /// `GetCaches().Skills` is the **effective** set - class, learned and equipment combined -
    /// which is the same thing the skills window and the bar itself show. Reading the list instead
    /// was this component's first version, and it did nothing at all on a new character.
    ///
    /// **Assignment is a server request, not a local edit.** `CallCmdAssignHotkey` sends a command
    /// and the result arrives later on the synced list, which is why this keeps its own record of
    /// what it has asked for: without it, every poll between the request and the reply would see
    /// the skill still missing and spend another slot on it. That record is per character, and
    /// thrown away when the character changes, so logging in as somebody else starts clean.
    ///
    /// Polled rather than driven by an event because the kit raises none for "a skill was
    /// learned"; the set simply changes. Twice a second is far below anything a player notices
    /// and costs a walk of two short collections, into sets kept from poll to poll.
    ///
    /// **The Attack button is the exception to "first free slot"**: it is pinned to key 1, as
    /// in WoW - see <see cref="PinAutoAttack"/>.
    ///
    /// **And it glows while the character is auto-attacking** - a pulsing frame round it, the
    /// way WoW flashes its own. Without it the button is a toggle with no visible state: press it
    /// mid-fight and there is nothing to say whether that started the swinging or stopped it.
    /// The slot is found by what it holds rather than by position, so the frame follows the
    /// button if the player drags it elsewhere; it is looked for again only when the slot it was
    /// on stops holding the button. The frame is a generated nine-sliced border (the demo has no
    /// UI sprite for one) laid over the slot as its last child, and never a raycast target, so it
    /// cannot swallow the click or the drag meant for the button under it. (The glow was a
    /// component of its own, `DemoAutoAttackGlow`, which this added at runtime and which looked
    /// the slot up every frame.)
    /// </summary>
    public class DemoAutoHotkeys : MonoBehaviour
    {
        [Tooltip("Hotkey ids in the order they should be filled. These are the kit's own slot ids.")]
        [SerializeField]
        private string[] slots = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

        [Tooltip("Seconds between checks for a newly learned skill.")]
        [SerializeField]
        private float checkInterval = 0.5f;

        [Header("Auto-attack glow")]
        [SerializeField]
        private Color glowColour = new Color(1f, 0.82f, 0.32f, 1f);
        [Tooltip("Pulses a second.")]
        [SerializeField]
        private float glowRate = 1.4f;
        [Tooltip("How far the frame reaches past the slot's edge, in pixels.")]
        [SerializeField]
        private float glowOutset = 3f;

        private readonly HashSet<string> _requested = new HashSet<string>();
        private readonly HashSet<string> _usedSlots = new HashSet<string>();
        private readonly HashSet<string> _placedSkills = new HashSet<string>();
        private string _forCharacter;
        private float _next;

        private UICharacterHotkey[] _hotkeySlots;
        private UICharacterHotkey _attackSlot;
        private string _attackSkillId;
        private Image _frame;
        private static Sprite s_frameSprite;

        private void Update()
        {
            if (Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + Mathf.Max(0.1f, checkInterval);
                Assign();
            }
            UpdateGlow();
        }

        private void Assign()
        {
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null)
                return;

            if (_forCharacter != player.Id)
            {
                _forCharacter = player.Id;
                _requested.Clear();
            }

            // A hotkey entry whose type is None is an empty slot, not a used one - the kit keeps
            // the row and blanks it rather than removing it.
            HashSet<string> usedSlots = _usedSlots;
            HashSet<string> placedSkills = _placedSkills;
            usedSlots.Clear();
            placedSkills.Clear();
            foreach (CharacterHotkey hotkey in player.Hotkeys)
            {
                if (hotkey.type == HotkeyType.None || string.IsNullOrEmpty(hotkey.hotkeyId))
                    continue;
                usedSlots.Add(hotkey.hotkeyId);
                if (hotkey.type == HotkeyType.Skill && !string.IsNullOrEmpty(hotkey.relateId))
                    placedSkills.Add(hotkey.relateId);
            }

            Dictionary<BaseSkill, int> known = player.GetCaches().Skills;
            if (known == null)
                return;

            PinAutoAttack(player, known, usedSlots, placedSkills);

            foreach (KeyValuePair<BaseSkill, int> entry in known)
            {
                BaseSkill skill = entry.Key;
                if (skill == null || entry.Value <= 0)
                    continue;
                if (skill.SkillType != SkillType.Active)
                    continue;
                if (placedSkills.Contains(skill.Id) || _requested.Contains(skill.Id))
                    continue;

                string slot = FirstFree(usedSlots);
                if (slot == null)
                {
                    // Bar full. Nothing to report every half second about it - the player can see
                    // the bar, and a skill they have to place themselves is the kit's own default.
                    return;
                }
                // The id form rather than the `CharacterSkill` overload, because a class-granted
                // skill has no `CharacterSkill` to hand over - it exists only in the cache.
                if (player.CallCmdAssignHotkey(slot, HotkeyType.Skill, skill.Id))
                {
                    usedSlots.Add(slot);
                    _requested.Add(skill.Id);
                }
            }
        }

        /// <summary>
        /// Puts the Attack button (<see cref="DemoAutoAttackSkill"/>) on the first slot - key 1,
        /// where WoW keeps it - whatever else the bar holds.
        ///
        /// A character made before the button existed already has its first skill on key 1, so
        /// taking the first free slot would put Attack somewhere arbitrary. Instead whatever sits
        /// on the first slot moves to the first free one, and Attack takes its place. Only while
        /// Attack is not on the bar at all: once it is, wherever the player drags it is where it
        /// stays.
        /// </summary>
        private void PinAutoAttack(BasePlayerCharacterEntity player, Dictionary<BaseSkill, int> known,
                                   HashSet<string> usedSlots, HashSet<string> placedSkills)
        {
            if (slots.Length == 0)
                return;
            BaseSkill attack = null;
            foreach (KeyValuePair<BaseSkill, int> entry in known)
            {
                if (entry.Key is DemoAutoAttackSkill && entry.Value > 0)
                {
                    attack = entry.Key;
                    break;
                }
            }
            if (attack == null || placedSkills.Contains(attack.Id) || _requested.Contains(attack.Id))
                return;

            string first = slots[0];
            if (usedSlots.Contains(first))
            {
                foreach (CharacterHotkey hotkey in player.Hotkeys)
                {
                    if (hotkey.hotkeyId != first || hotkey.type == HotkeyType.None)
                        continue;
                    // Both commands name different slots, so the order the server takes them in
                    // (they are sent unordered) cannot matter. A full bar has nowhere to move the
                    // occupant to; it drops off the bar and stays in the skills window.
                    string free = FirstFree(usedSlots);
                    if (free != null && player.CallCmdAssignHotkey(free, hotkey.type, hotkey.relateId))
                        usedSlots.Add(free);
                    break;
                }
            }
            if (player.CallCmdAssignHotkey(first, HotkeyType.Skill, attack.Id))
            {
                usedSlots.Add(first);
                _requested.Add(attack.Id);
            }
        }

        private string FirstFree(HashSet<string> usedSlots)
        {
            foreach (string slot in slots)
            {
                if (!usedSlots.Contains(slot))
                    return slot;
            }
            return null;
        }

        /// <summary>The frame round the Attack button: pulsing while auto-attacking, hidden otherwise.</summary>
        private void UpdateGlow()
        {
            var controller = BasePlayerCharacterController.Singleton as DemoPlayerController;
            UICharacterHotkey slot = controller != null && controller.IsAutoAttacking ? AttackSlot() : null;
            if (slot == null)
            {
                if (_frame != null && _frame.enabled)
                    _frame.enabled = false;
                return;
            }

            Image frame = FrameOn(slot);
            frame.enabled = true;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * glowRate * Mathf.PI * 2f);
            Color c = glowColour;
            c.a *= Mathf.Lerp(0.35f, 1f, pulse);
            frame.color = c;
        }

        /// <summary>
        /// The slot holding the Attack button: the one found last time while it still holds it
        /// (a string compare), otherwise looked for again across the bar.
        /// </summary>
        private UICharacterHotkey AttackSlot()
        {
            if (_attackSlot != null && _attackSlot.Data.type == HotkeyType.Skill && _attackSlot.Data.relateId == _attackSkillId)
                return _attackSlot;
            _attackSlot = null;
            _attackSkillId = null;
            if (_hotkeySlots == null || _hotkeySlots.Length == 0)
                _hotkeySlots = GetComponentsInChildren<UICharacterHotkey>(true);
            foreach (UICharacterHotkey slot in _hotkeySlots)
            {
                if (slot == null || slot.Data.type != HotkeyType.Skill || string.IsNullOrEmpty(slot.Data.relateId))
                    continue;
                if (GameInstance.Skills.TryGetValue(BaseGameData.MakeDataId(slot.Data.relateId), out BaseSkill skill)
                    && skill is DemoAutoAttackSkill)
                {
                    _attackSlot = slot;
                    _attackSkillId = slot.Data.relateId;
                    return slot;
                }
            }
            return null;
        }

        private Image FrameOn(UICharacterHotkey slot)
        {
            if (_frame == null)
            {
                var go = new GameObject("AutoAttackGlow", typeof(RectTransform), typeof(Image));
                _frame = go.GetComponent<Image>();
                _frame.sprite = FrameSprite();
                _frame.type = Image.Type.Sliced;
                _frame.raycastTarget = false;
            }
            if (_frame.transform.parent != slot.transform)
            {
                var rect = (RectTransform)_frame.transform;
                rect.SetParent(slot.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(-glowOutset, -glowOutset);
                rect.offsetMax = new Vector2(glowOutset, glowOutset);
            }
            // Last, so it draws over the icon the slot instantiates as its first child.
            if (_frame.transform.GetSiblingIndex() != slot.transform.childCount - 1)
                _frame.transform.SetAsLastSibling();
            return _frame;
        }

        /// <summary>A white 32px square border, 4px thick with a soft inner edge, nine-sliced.</summary>
        private static Sprite FrameSprite()
        {
            if (s_frameSprite != null)
                return s_frameSprite;
            const int size = 32;
            const float thickness = 4f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AutoAttackGlowFrame",
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; ++y)
            {
                for (int x = 0; x < size; ++x)
                {
                    float edge = Mathf.Min(Mathf.Min(x + 0.5f, size - x - 0.5f), Mathf.Min(y + 0.5f, size - y - 0.5f));
                    float a = Mathf.Clamp01(thickness + 1f - edge);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = thickness + 2f;
            s_frameSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f,
                                          0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            return s_frameSprite;
        }
    }
}
