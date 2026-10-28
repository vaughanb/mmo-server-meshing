namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The Attack button: WoW's auto-attack, as a skill every class has from level one.
    ///
    /// The kit has auto-attack - a target that is attacked and followed until it dies - but no
    /// way to start it from the keyboard. Its Attack key swings only while it is held, and a
    /// right click on an enemy (or the kit's second left click) is the only thing that starts
    /// it and keeps it going. WoW puts it on the action bar instead, on key 1, as a toggle.
    ///
    /// **A skill because that is what the hotbar holds.** A hotkey is a skill, an item or a
    /// guild skill, and making this a skill gets an icon, a slot, a tooltip, drag-and-drop and
    /// the skills window for nothing. It is never *cast*: <see cref="DemoPlayerController"/>
    /// intercepts the hotkey before the kit queues it and turns auto-attack on or off itself.
    /// This type is how the controller recognises it - by type, not by an id string that a
    /// rename would silently break.
    ///
    /// **It refuses to be used.** Should anything ever get it as far as the kit's own skill
    /// path - on the server, or a controller that does not know it - <see cref="CanUse"/> says
    /// no, so it does nothing rather than playing the default skill animation for no effect.
    /// No UI reads `CanUse`, so the button is not greyed out by it.
    ///
    /// Built by DemoSkillBuilder as `Skills/AutoAttack.asset` and granted to every class by
    /// `WriteClassSkills`; DemoAutoHotkeys pins it to key 1.
    /// </summary>
    public class DemoAutoAttackSkill : Skill
    {
        public override bool CanUse(BaseCharacterEntity character, int level, bool isLeftHand, uint targetObjectId, out UITextKeys gameMessage, bool isItem = false)
        {
            gameMessage = UITextKeys.UI_ERROR_INVALID_DATA;
            return false;
        }
    }
}
