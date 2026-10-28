using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A one-shot sound played where something happens in the world: the ice forming round a
    /// frozen character, and breaking.
    ///
    /// Not the kit's `GameEffect.randomSoundEffects`, which goes through `PlayClipAtPoint` and so
    /// takes Unity's default rolloff - full volume within one metre, a quarter at four. A spell's
    /// sounds come from everything it caught, anywhere across its 4.5 metres, and at that rolloff
    /// the far ones all but vanished. This keeps full volume out to <c>near</c> metres, as the
    /// meteor's does, and varies the pitch a little, so three enemies freezing at once do not
    /// play one clip three times in unison.
    /// </summary>
    public static class OneShotSound
    {
        /// <param name="pitch">
        /// Where the spread is centred. Below 1 to make a borrowed clip read as something heavier or duller
        /// than it was recorded as - Volley's rain plays the arrow-in-a-body thunks lower, as arrows into earth.
        /// </param>
        public static void PlayAt(AudioClip[] clips, Vector3 position, float volume = 1f, float near = 6f,
                                  float far = 60f, float pitchSpread = 0.06f, float pitch = 1f)
        {
            if (clips == null || clips.Length == 0 || Application.isBatchMode || AudioListener.pause)
                return;
            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip == null)
                return;
            var go = new GameObject(clip.name);
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = near;
            source.maxDistance = far;
            source.dopplerLevel = 0f;
            source.pitch = pitch * (1f + Random.Range(-pitchSpread, pitchSpread));
            source.volume = volume * (AudioManager.Singleton != null ? AudioManager.Singleton.GetSfxVolume() : 1f);
            source.Play();
            Object.Destroy(go, clip.length / source.pitch + 0.1f);
        }
    }
}
