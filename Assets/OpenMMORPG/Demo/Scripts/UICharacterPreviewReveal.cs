using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Lets the character screens show an entity whose model hangs off a child.
    ///
    /// The create and select screens instantiate a whole character entity under their
    /// model container, then address it only through its CharacterModel. They hide
    /// everything with characterModelContainer.SetChildrenActive(false), which switches
    /// off the entity, and show the chosen one with SelectedModel.gameObject.SetActive
    /// (true), which switches on the model. On the kit's template prefabs the model
    /// component sits on the entity's own root, so those are the same object and it
    /// works. The demo entities keep the model on a child - DemoEntityBuilder does that
    /// deliberately, so a character can change model without the entity being rebuilt -
    /// and the model is then switched on underneath a parent that was just switched off.
    /// It ends up active, in front of the camera, and invisible, with nothing logged and
    /// no exception thrown.
    ///
    /// Both screens raise eventOnShowInstantiatedCharacter immediately after that
    /// activation, which is where this comes in: the branch is switched back on from
    /// here, so nothing in the kit itself has to change.
    ///
    /// It also silences the preview, which is a second bug with the same cause - these
    /// screens put a whole live entity on a menu. See <see cref="Silence"/>.
    ///
    /// Belongs on the same object as the screen it serves. CanvasHome carries one on
    /// each of the character create and character select screens.
    /// </summary>
    [DisallowMultipleComponent]
    public class UICharacterPreviewReveal : MonoBehaviour
    {
        private void Awake()
        {
            // Awake rather than OnEnable, because the screens load their characters from
            // OnEnable and select the first one as part of that load. The screens drop
            // their listeners in OnDestroy only, so subscribing once here is enough.
            bool found = false;

            UICharacterCreate create = GetComponent<UICharacterCreate>();
            if (create != null)
            {
                create.eventOnShowInstantiatedCharacter.AddListener(
                    model => Reveal(model, create.characterModelContainer));
                found = true;
            }

            UICharacterList list = GetComponent<UICharacterList>();
            if (list != null)
            {
                list.eventOnShowInstantiatedCharacter.AddListener(
                    model => Reveal(model, list.characterModelContainer));
                found = true;
            }

            if (!found)
            {
                Debug.LogWarning($"[{nameof(UICharacterPreviewReveal)}] Nothing to listen to on \"{name}\". " +
                                 $"It belongs on the same object as a {nameof(UICharacterCreate)} or a " +
                                 $"{nameof(UICharacterList)}.");
                enabled = false;
            }
        }

        /// <summary>
        /// Switches on everything between the container and the model.
        /// </summary>
        private static void Reveal(BaseCharacterModel model, Transform container)
        {
            if (model == null || container == null)
                return;
            // Checked before anything is switched on, because the walk stops at the
            // container: a model from somewhere else would send it up into the rest of
            // the canvas, putting a screen on display that was deliberately hidden.
            if (!model.transform.IsChildOf(container))
                return;
            Transform branch = model.transform;
            while (branch != null && branch != container)
            {
                branch.gameObject.SetActive(true);
                branch = branch.parent;
            }

            Silence(model);
        }

        /// <summary>
        /// Takes the sound components off a character that is only being looked at.
        ///
        /// The character screens instantiate a whole entity, not a mannequin, and an
        /// entity that has never been in a game has <c>CurrentHp == 0</c>.
        /// <c>IsDead()</c> is <c>CurrentHp &lt;= 0</c>, so every preview on those screens
        /// is, as far as the kit is concerned, a corpse.
        ///
        /// <c>CharacterDeathSoundComponent</c> then does this, once, on its first update:
        /// <code>
        ///     if (_dirtyIsDead != Entity.IsDead()) {
        ///         _dirtyIsDead = Entity.IsDead();
        ///         if (_dirtyIsDead) PlaySound();
        ///     }
        /// </code>
        /// <c>_dirtyIsDead</c> starts <c>false</c> and is never seeded from the entity, so
        /// a character that was dead all along still reads as a transition into death -
        /// and the create screen shrieks every time you change body or class. (The real
        /// fix is to seed that flag in <c>Start</c>, which is a kit change; raise it
        /// upstream.)
        ///
        /// Destroyed rather than disabled so there is no ordering to get wrong: the
        /// component registers for updates in <c>OnEnable</c>, which has already run by
        /// the time this event fires. The hurt sound goes with it on the same reasoning -
        /// it listens for a drop in health, which a preview cannot have, but nothing on a
        /// menu should be able to make a noise about its own condition.
        /// </summary>
        internal static void Silence(BaseCharacterModel model)
        {
            Transform root = model.transform;
            BaseCharacterEntity entity = model.GetComponentInParent<BaseCharacterEntity>();
            if (entity != null)
                root = entity.transform;

            foreach (CharacterDeathSoundComponent death in root.GetComponentsInChildren<CharacterDeathSoundComponent>(true))
                Destroy(death);
            foreach (CharacterHurtSoundComponent hurt in root.GetComponentsInChildren<CharacterHurtSoundComponent>(true))
                Destroy(hurt);
        }
    }
}
