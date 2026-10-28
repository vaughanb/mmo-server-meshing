using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Shows a saved character on the select screen with the hair it was created with.
    ///
    /// The kit applies a saved character's body-part choices with SetupModelBodyParts,
    /// which looks for PlayerCharacterBodyPartComponent from the character *model*
    /// downward. The component itself can only live on the entity root: it reaches its
    /// entity with GetComponent on its own object, and SetModel writes the choice into
    /// that entity. On the kit's template prefabs the model sits on the root too, so the
    /// search starts where the component is. The demo keeps the model on a child (see
    /// UICharacterPreviewReveal), so the search starts one level below the component and
    /// finds nothing - nothing is logged, and every saved character simply comes up with
    /// the default hair. The create screen is unaffected, because UIBodyPartManager
    /// searches from the root instead; the two lookups disagree, which is worth raising
    /// upstream.
    ///
    /// The select screen raises eventOnSelectCharacter with the saved data just after it
    /// has shown the model, so this applies the data to the components on that model's
    /// entity and refreshes the model. Belongs on the same object as the UICharacterList
    /// (or UIMmoCharacterList) it serves; CanvasHome carries one on the select screen.
    /// </summary>
    [DisallowMultipleComponent]
    public class UICharacterSelectAppearance : MonoBehaviour
    {
        private void Awake()
        {
            // Awake rather than OnEnable: the screen loads and selects its first character
            // from OnEnable, and only drops its listeners in OnDestroy.
            UICharacterList list = GetComponent<UICharacterList>();
            if (list == null)
            {
                Debug.LogWarning($"[{nameof(UICharacterSelectAppearance)}] Nothing to listen to on \"{name}\". " +
                                 $"It belongs on the same object as a {nameof(UICharacterList)}.");
                enabled = false;
                return;
            }
            list.eventOnSelectCharacter.AddListener(data => Dress(list.SelectedModel, data as IPlayerCharacterData));
        }

        /// <summary>
        /// Applies a character's saved body parts, skin and size to a model that was built from
        /// it with <c>InstantiateModel</c>, and refreshes its equipment. Also used by the
        /// character sheet's preview (<see cref="UICharacterPaperDoll"/>), which clones the
        /// playing character the same way this screen clones a saved one.
        /// </summary>
        internal static void Dress(BaseCharacterModel model, IPlayerCharacterData data)
        {
            if (model == null || data == null)
                return;
            // The entity this model belongs to, and only that one: every character on the
            // screen hangs under the same container, so a search from the container would
            // take in the others' components as well.
            BasePlayerCharacterEntity entity = model.GetComponentInParent<BasePlayerCharacterEntity>(true);
            if (entity == null)
                return;
            PlayerCharacterBodyPartComponent[] parts = entity.GetComponents<PlayerCharacterBodyPartComponent>();
            if (parts.Length == 0)
                return;
            foreach (PlayerCharacterBodyPartComponent part in parts)
            {
                if (!part.enabled)
                    continue;
                part.SetupCharacterModelEvents(model);
                part.ApplyModelAndColorBySavedData(data.PublicInts);
            }
            // The component asks the entity to refresh, but a screen's entity is not spawned
            // and never updates, so the refresh has to be asked for here - the same way the
            // screen dressed the model in the first place.
            model.SetEquipItemsImmediately(model.EquipItems, model.SelectableWeaponSets, model.EquipWeaponSet, model.IsWeaponsSheathed);

            // Skin is the same story one layer along: it is saved in the same PublicInts and
            // it is equally unreachable from a screen, because the entity never runs. It is
            // applied after the equipment rather than before, so the garments this paints
            // are the ones the character is actually wearing. See SkinTone.
            SkinTone skin = entity.GetComponent<SkinTone>();
            if (skin != null)
                skin.ApplyFrom(data.PublicInts);

            // And so is size, for the same reason. See CharacterSize.
            CharacterSize size = entity.GetComponent<CharacterSize>();
            if (size != null)
                size.ApplyFrom(data.PublicInts);
        }
    }
}
