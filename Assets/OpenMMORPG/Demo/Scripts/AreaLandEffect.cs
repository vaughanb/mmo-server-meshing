using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Sets off an effect where an area skill lands, the moment it lands: Frost Nova's burst.
    ///
    /// The area entity appears on the skill's trigger, which is when its damage is decided, so
    /// this is the frame the nova should go off - not when the caster starts the animation, which
    /// is when the kit plays a skill's own activate effects, a third of a second early. And the
    /// burst is fetched from the kit's pool as its own GameEffect rather than parented here,
    /// because a burst patch lives half a second: the kit plays every particle system under a
    /// damage entity as it spawns and puts them all away with it, and frost and crystals that
    /// took half a second to form and vanished at the same moment would be most of the problem
    /// this replaced.
    ///
    /// On the first frame rather than in OnEnable, because a pooled area is not guaranteed to be
    /// at its new spot at that moment, and only that frame: the late update lets go once it has
    /// fired (<see cref="WakeableLateUpdateBehaviour"/>). Added by DemoSkillBuilder for skills
    /// with a LandEffect.
    /// </summary>
    public class AreaLandEffect : WakeableLateUpdateBehaviour
    {
        public GameEffect effect;

        private bool _pending;

        private void Awake()
        {
            // A dedicated server spawns the area to do its damage and draws nothing.
            if (Application.isBatchMode)
                enabled = false;
        }

        private void OnEnable()
        {
            _pending = true;
            Wake();
        }

        public override void ManagedLateUpdate()
        {
            Sleep();
            if (!_pending)
                return;
            _pending = false;
            if (effect != null)
                PoolSystem.GetInstance(effect, transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        }
    }
}
