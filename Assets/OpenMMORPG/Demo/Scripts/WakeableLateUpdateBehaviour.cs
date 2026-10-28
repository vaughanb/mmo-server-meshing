using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A late update that runs only while there is something for it to do.
    ///
    /// Effects are the case it is for. A pooled effect that plays a sound, aims a burst or
    /// animates something for a second or two has nothing to do for the rest of the time it is
    /// out of the pool - and an ordinary <c>LateUpdate</c> is called every frame regardless, on
    /// every copy. Here the work is registered with the kit's update manager when it starts
    /// (<see cref="Wake"/>, usually from <c>OnEnable</c>), and let go when it is finished
    /// (<see cref="Sleep"/>). Switching the component or its object off lets go too.
    ///
    /// Sleeping, not disabling: a component's own `enabled = false` survives the pool's
    /// `SetActive` cycles, so an effect that disabled itself when done would never wake again.
    /// </summary>
    public abstract class WakeableLateUpdateBehaviour : MonoBehaviour, IManagedLateUpdate
    {
        private bool _awake;

        /// <summary>Whether the late update is running.</summary>
        protected bool IsAwake => _awake;

        /// <summary>Starts the late update, if it is not running already.</summary>
        protected void Wake()
        {
            if (_awake || !isActiveAndEnabled)
                return;
            _awake = true;
            UpdateManager.Register(this);
        }

        /// <summary>Stops the late update until the next <see cref="Wake"/>.</summary>
        protected void Sleep()
        {
            if (!_awake)
                return;
            _awake = false;
            UpdateManager.Unregister(this);
        }

        protected virtual void OnDisable()
        {
            Sleep();
        }

        public abstract void ManagedLateUpdate();
    }
}
