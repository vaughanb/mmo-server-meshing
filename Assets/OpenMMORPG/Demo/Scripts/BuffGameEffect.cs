using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A GameEffect worn for as long as a buff or debuff lasts: the roots on a crippled leg, the
    /// hunter's mark, a bleeding wound.
    ///
    /// The kit plays a buff's `effects` when the buff lands and calls <see cref="DestroyEffect"/>
    /// when it goes, which is all this needs - except that **the kit calls it every frame** once
    /// whatever the effect follows has been destroyed, and each call puts the end off by another
    /// `lifeTime`, so an effect on a character that is cleared away while still bleeding would
    /// never go back to the pool. Handled here as DemoFrozenEffect handles it: once only.
    /// Emitters stop at once (the base clears their loop; this stops them), and anything drawn on
    /// the ground fades inside the effect's own lifetime.
    /// </summary>
    public class BuffGameEffect : GameEffect
    {
        private bool _going;

        public override void Play()
        {
            _going = false;
            base.Play();
        }

        public override void DestroyEffect()
        {
            if (_going)
                return;
            _going = true;
            base.DestroyEffect();
            foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>())
                system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            foreach (GroundCircle circle in GetComponentsInChildren<GroundCircle>())
                circle.FadeOut(lifeTime * 0.8f);
        }
    }
}
