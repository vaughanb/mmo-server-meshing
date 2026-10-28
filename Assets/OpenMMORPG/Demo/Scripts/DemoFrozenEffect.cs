using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The ice on a frozen character: crystals round its feet and frost under them, standing for
    /// exactly as long as the freeze and shattering when it ends.
    ///
    /// The kit plays `GameInstance.FreezeEffects` on any character a Freeze ailment lands on and
    /// calls <see cref="DestroyEffect"/> on them when the last such buff is gone - the only hook
    /// there is, and it is virtual. So this is a GameEffect that, told to go, breaks its crystals,
    /// stops its mist and fades its frost within its own `lifeTime` before the kit puts it away,
    /// instead of the ice simply blinking out. It is looping for the same reason: it has no end
    /// of its own; the freeze decides.
    ///
    /// Until 2026-09-25 the demo's freeze effects list was empty, and a character frozen by Frost
    /// Nova only stopped moving - nothing about it said why. Set by DemoSkillEffectBuilder.
    /// </summary>
    public class DemoFrozenEffect : GameEffect
    {
        private bool _going;

        public override void Play()
        {
            _going = false;
            base.Play();
        }

        public override void DestroyEffect()
        {
            // Once. The kit calls this every frame after whatever the effect follows is gone, and
            // each call puts the end off by another lifeTime, so the ice would never be put away.
            if (_going)
                return;
            _going = true;
            base.DestroyEffect();

            DemoIceShards[] ice = GetComponentsInChildren<DemoIceShards>();
            foreach (DemoIceShards shards in ice)
                shards.Shatter();
            // The mist and glints stop at once rather than running out their loop - but not the
            // glitter the crystals throw as they break, which is emitted by hand from here on.
            foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>())
            {
                if (!IsGlitter(system, ice))
                    system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
            // Gone a little before the effect is, so it is never cut off mid-fade.
            foreach (GroundCircle frost in GetComponentsInChildren<GroundCircle>())
                frost.FadeOut(lifeTime * 0.8f);
        }

        private static bool IsGlitter(ParticleSystem system, DemoIceShards[] ice)
        {
            foreach (DemoIceShards shards in ice)
            {
                if (shards.fragments == system)
                    return true;
            }
            return false;
        }
    }
}
