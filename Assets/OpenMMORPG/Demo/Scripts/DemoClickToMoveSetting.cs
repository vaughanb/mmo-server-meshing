using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// One of the two radio buttons behind the settings dialog's Click To Move row.
    ///
    /// Modelled on the kit's own `ShadowsSetting` and friends: a pair of toggles in a
    /// `ToggleGroup`, each carrying this component with the value it stands for, so the group does
    /// the mutual exclusion and this only has to say which one is which.
    ///
    /// **`Start`, not `OnEnable`, for the initial read.** The dialog is toggled on and off, so
    /// `OnEnable` runs every time it is opened - and setting `isOn` there would fight a change the
    /// player made while it was open in the same frame it reopened. The store is read once, when
    /// the row is first alive.
    /// </summary>
    [RequireComponent(typeof(Toggle))]
    public class DemoClickToMoveSetting : MonoBehaviour
    {
        [Tooltip("The value this toggle selects when the player picks it.")]
        [SerializeField]
        private bool value;

        private Toggle _toggle;

        /// <summary>Called by the HUD builder, which makes the pair.</summary>
        public void SetValue(bool selects)
        {
            value = selects;
        }

        private void Start()
        {
            _toggle = GetComponent<Toggle>();
            // Without the notify suppressed this would call back into OnChanged during setup and
            // write the store from whichever of the pair happened to run first.
            _toggle.SetIsOnWithoutNotify(DemoSettings.ClickToMove == value);
            _toggle.onValueChanged.AddListener(OnChanged);
        }

        private void OnDestroy()
        {
            if (_toggle != null)
                _toggle.onValueChanged.RemoveListener(OnChanged);
        }

        private void OnChanged(bool isOn)
        {
            // Only the toggle being switched *on* decides; the other one is being switched off by
            // the group in the same breath and would otherwise write the opposite value after it.
            if (isOn)
                DemoSettings.ClickToMove = value;
        }
    }
}
