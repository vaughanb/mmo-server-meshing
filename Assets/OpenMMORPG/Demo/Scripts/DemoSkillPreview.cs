using System.Collections.Generic;
using MultiplayerARPG.GameData.Model.Playables;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Plays a skill on a character in the editor the way the game plays it — the cast clip
    /// for the cast duration, then the activate animation with its sound and its effects,
    /// with the trigger frames marked — so a skill can be watched, scrubbed and judged
    /// without a server, a client, a target or a cooldown.
    ///
    /// Everything comes out of the skill's own game data and this character's own animation
    /// data, resolved exactly as <c>DefaultCharacterUseSkillComponent</c> and
    /// <see cref="PlayableCharacterModel"/> resolve it at runtime: the cast state and the
    /// activate animation are picked by the weapon currently in hand, a skill marked
    /// <see cref="SkillActivateAnimationType.UseAttackAnimation"/> borrows the weapon's
    /// attack instead of carrying its own, and the trigger frames are the animation's
    /// <c>triggerDurationRates</c> over its real length. So what you see is what the game
    /// will do, and the numbers to change are the ones in `DemoSkillBuilder` and
    /// `DemoAnimationSet`.
    ///
    /// **Which weapon is in hand is read off the <see cref="DemoEquipPreview"/> components
    /// beside this one**, so the bench's equipment and its skills cannot disagree. Hang a
    /// sword and Cleave swings a sword; hang a bow and Aimed Shot uses the bow's own shot,
    /// which is the whole reason that skill has no clip of its own.
    ///
    /// It runs in the editor, not in play mode, and that is not a shortcut: this bench has
    /// bare character *models* in it, and a model without an entity NREs in
    /// <see cref="PlayableCharacterModel"/>'s Start. Nothing here goes near the playable
    /// graph — the pose is sampled through <c>AnimationMode</c>, particles are stepped by
    /// hand and sounds play on a hidden `AudioSource`. Which also means it costs
    /// no domain reload, and the pose is live while you drag a grip about.
    ///
    /// What it does *not* simulate: damage, targets, cooldowns, mana, and the missiles and
    /// area entities a skill spawns at its trigger, all of which are networked entities that
    /// need a running game. The trigger frames are marked instead, which is the part that
    /// has to line up with the animation.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class DemoSkillPreview : MonoBehaviour
    {
        [Tooltip("The skill to play. Its cast and activate animations are looked up on this character.")]
        public BaseSkill skill;

        [Tooltip("Skill level. Only the cast duration scales with it, but that is enough to change the timing.")]
        public int level = 1;

        [Tooltip("Playback rate. Slow it down to see where a trigger frame really lands.")]
        [Range(0.05f, 2f)]
        public float speed = 1f;

        [Tooltip("Start again at the end, so a swing can be watched over and over without pressing anything.")]
        public bool loop;

        [Tooltip("Play the activate animation's audio clips, and any sound the effects carry.")]
        public bool playAudio = true;

        [Tooltip("Spawn the skill's cast and activate effects and step their particles along with the animation.")]
        public bool playEffects = true;

        [Tooltip("Print each phase and trigger as it happens, with its time.")]
        public bool logTimeline;

        /// <summary>What a skill turns into once the character and the weapon in hand have had their say.</summary>
        public class Shot
        {
            /// <summary>Why this skill cannot be played here, or null when it can.</summary>
            public string Problem;
            /// <summary>Where the activate animation came from, for the readout.</summary>
            public string Source = "";
            public WeaponType WeaponType;

            public AnimationClip CastClip;
            public float CastDuration;

            public AnimationClip ActivateClip;
            /// <summary>Real seconds the activate animation occupies, speed rate already applied.</summary>
            public float ActivateDuration;
            public float ActivateSpeed = 1f;
            /// <summary>Dead time after the clip, before the character is free again.</summary>
            public float ExtendDuration;

            /// <summary>
            /// Whether the animation is allowed to move the character, as the kit's
            /// <c>shouldUseRootMotion</c> says. Almost always false — a dash attack is the
            /// exception — and when it is false the bench has to pin the root down, because
            /// sampling a clip writes its baked root curve into the transform whether the
            /// game would use it or not.
            /// </summary>
            public bool RootMotion;

            /// <summary>Trigger moments in seconds from the start of the whole shot.</summary>
            public float[] TriggerTimes = new float[0];

            public AudioClip[] Audio = new AudioClip[0];
            public GameEffect[] CastEffects = new GameEffect[0];
            public GameEffect[] ActivateEffects = new GameEffect[0];

            public float Total { get { return CastDuration + ActivateDuration + ExtendDuration; } }
        }

        /// <summary>Where the playhead is, in seconds from the start of the shot.</summary>
        public float Time { get; private set; }

        public bool IsPlaying { get; private set; }

        /// <summary>The last trigger the playhead crossed, for the editor to flash. -1 when none yet.</summary>
        public int LastTrigger { get; private set; } = -1;

        // ---- resolution ------------------------------------------------------

        /// <summary>
        /// Turns the skill into the thing that actually plays on this character, or explains
        /// why it cannot.
        ///
        /// Cheap enough to call every inspector repaint, which is what keeps the readout
        /// honest when the weapon in hand changes underneath it.
        /// </summary>
        public Shot Resolve()
        {
            var shot = new Shot();

            if (skill == null)
            {
                shot.Problem = "No skill picked.";
                return shot;
            }

            PlayableCharacterModel model = GetComponentInChildren<PlayableCharacterModel>(true);
            if (model == null)
            {
                shot.Problem = $"\"{name}\" has no {nameof(PlayableCharacterModel)}, so it carries no animation data.";
                return shot;
            }

            shot.WeaponType = EquippedWeaponType();
            int weaponTypeDataId = shot.WeaponType != null ? shot.WeaponType.DataId : 0;

            shot.CastDuration = skill.GetCastDuration(Mathf.Max(1, level));

            ActionAnimation activate = null;
            if (model.TryGetSkillAnimations(skill.DataId, out SkillAnimations anims))
            {
                ActionState cast = anims.GetCastState(weaponTypeDataId);
                shot.CastClip = cast != null ? cast.clip : null;

                if (anims.activateAnimationType == SkillActivateAnimationType.UseActivateAnimation)
                {
                    activate = anims.GetActivateAnimation(weaponTypeDataId);
                    shot.Source = "the skill's own activate animation";
                }
                else
                {
                    // The bow's skills. The weapon's attack is the animation, because its
                    // trigger is measured off the loose and any other frame would put the
                    // arrow in the air while the string was still coming back.
                    activate = WeaponAttack(model, weaponTypeDataId);
                    shot.Source = shot.WeaponType != null
                        ? $"the {shot.WeaponType.name} attack animation"
                        : "the unarmed attack animation";
                }
            }
            else
            {
                shot.Problem = $"\"{skill.name}\" has no entry in this character's skillAnimations. " +
                               "Run Build Character Entities after Build Skills.";
                return shot;
            }

            if (shot.CastClip == null && model.defaultAnimations != null &&
                model.defaultAnimations.skillCastState != null)
            {
                // exactly the runtime fallback: no clip for this skill means the generic cast
                shot.CastClip = model.defaultAnimations.skillCastState.clip;
            }

            if (activate == null || activate.state == null || activate.state.clip == null)
            {
                shot.Problem = shot.Source.Length > 0
                    ? $"\"{skill.name}\" resolves to {shot.Source}, which has no clip."
                    : $"\"{skill.name}\" has no activate animation.";
                return shot;
            }

            shot.ActivateClip = activate.state.clip;
            shot.ActivateSpeed = Mathf.Max(0.01f, activate.GetAnimSpeedRate());
            shot.ActivateDuration = activate.GetClipLength() / shot.ActivateSpeed;
            shot.ExtendDuration = activate.GetExtendDuration();
            shot.RootMotion = activate.state.shouldUseRootMotion;
            shot.Audio = activate.audioClips ?? new AudioClip[0];

            float[] rates = activate.triggerDurationRates ?? new float[0];
            shot.TriggerTimes = new float[rates.Length];
            for (int i = 0; i < rates.Length; ++i)
                shot.TriggerTimes[i] = shot.CastDuration + rates[i] * shot.ActivateDuration;

            shot.CastEffects = skill.SkillCastEffects ?? new GameEffect[0];
            shot.ActivateEffects = skill.SkillActivateEffects ?? new GameEffect[0];
            return shot;
        }

        /// <summary>
        /// The weapon type of whatever a sibling <see cref="DemoEquipPreview"/> currently has
        /// on. A shield has no weapon type and is ignored; an unequipped preview is ignored
        /// too, so switching a weapon off is the same as not carrying it.
        /// </summary>
        private WeaponType EquippedWeaponType()
        {
            foreach (DemoEquipPreview preview in GetComponents<DemoEquipPreview>())
            {
                if (preview == null || !preview.equipped || preview.item == null)
                    continue;
                // WeaponType is on IWeaponItem, not on every equipment: a shield reaches
                // here as a BaseEquipmentItem with no weapon type at all, and so does armour
                var weapon = preview.item as IWeaponItem;
                if (weapon != null && weapon.WeaponType != null)
                    return weapon.WeaponType;
            }
            return null;
        }

        /// <summary>
        /// The weapon's own attack, right hand first. Bows sit in the left hand on these
        /// characters but are still the character's one weapon, so the right-hand set is
        /// where their attack lives; the left-hand lookup is the fallback for anything the
        /// demo has not got to yet.
        /// </summary>
        private static ActionAnimation WeaponAttack(PlayableCharacterModel model, int weaponTypeDataId)
        {
            ActionAnimation[] right = model.GetRightHandAttackAnimations(weaponTypeDataId);
            if (right != null && right.Length > 0 && right[0] != null)
                return right[0];
            ActionAnimation[] left = model.GetLeftHandAttackAnimations(weaponTypeDataId);
            if (left != null && left.Length > 0 && left[0] != null)
                return left[0];
            return null;
        }

#if UNITY_EDITOR
        // ---- playback --------------------------------------------------------
        //
        // All of this is editor-only. See the class comment for why there is no play-mode
        // path: the bench holds bare models, and a model without an entity cannot start its
        // playable graph.

        /// <summary>
        /// Every preview currently holding a pose.
        ///
        /// `AnimationMode` is global and, worse, it is a *set*: closing a sampling block
        /// reverts every property it knows about that was not written inside that block. Pose
        /// the male and then the female and the male snaps back to his bind pose, which looks
        /// like the first preview quietly failed. So the characters are never sampled
        /// separately — whoever moves, they all get written inside the one block, and the
        /// mode is only switched off when the last of them lets go.
        /// </summary>
        private static readonly List<DemoSkillPreview> _active = new List<DemoSkillPreview>();

        /// <summary>Whether this instance is one of the <see cref="_active"/>.</summary>
        private bool _holdsSampling;

        private Shot _shot;
        private double _lastTick;
        private Vector3 _restPosition;
        private Quaternion _restRotation;
        private readonly List<GameObject> _effects = new List<GameObject>();
        private readonly List<ParticleSystem> _particles = new List<ParticleSystem>();
        private float _effectsStarted;
        private bool _castEffectsSpawned;
        private bool _activateStarted;

        private void OnDisable()
        {
            Stop();
        }

        /// <summary>Starts the shot from the top.</summary>
        public void Play()
        {
            Stop();

            _shot = Resolve();
            if (_shot.Problem != null)
            {
                Debug.LogWarning($"[{nameof(DemoSkillPreview)}] {_shot.Problem}", this);
                return;
            }

            BeginSampling();

            Time = 0f;
            LastTrigger = -1;
            _castEffectsSpawned = false;
            _activateStarted = false;
            IsPlaying = true;
            _lastTick = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update += Tick;

            if (logTimeline)
                Log($"cast {_shot.CastDuration:0.00}s, activate {_shot.ActivateDuration:0.00}s " +
                    $"({_shot.ActivateClip.name}), {_shot.TriggerTimes.Length} trigger(s)");
        }

        /// <summary>
        /// Stops and puts the character back where it was. The pose is restored by
        /// `AnimationMode` itself, which is why the sampling has to run inside it rather than
        /// writing bone transforms directly.
        /// </summary>
        public void Stop()
        {
            UnityEditor.EditorApplication.update -= Tick;
            StopAudio();
            ClearEffects();

            IsPlaying = false;
            EndSampling();
            Time = 0f;
            LastTrigger = -1;
            UnityEditor.SceneView.RepaintAll();
        }

        /// <summary>
        /// Puts the playhead at one moment and holds it there — the scrub. Everything that
        /// fires at a moment (sound, effects) is deliberately left out: dragging a slider
        /// across a trigger should not machine-gun the swing's audio.
        /// </summary>
        public void Scrub(float time)
        {
            _shot = Resolve();
            if (_shot.Problem != null)
                return;

            // held by the scrub rather than by the clock, so the tick stays unsubscribed
            UnityEditor.EditorApplication.update -= Tick;
            IsPlaying = false;
            BeginSampling();

            Time = Mathf.Clamp(time, 0f, _shot.Total);
            SampleAll();
            UnityEditor.SceneView.RepaintAll();
        }

        private void Tick()
        {
            if (!IsPlaying || _shot == null || this == null)
            {
                UnityEditor.EditorApplication.update -= Tick;
                return;
            }

            double now = UnityEditor.EditorApplication.timeSinceStartup;
            // The editor's update rate is not a frame rate — it stalls for a compile, an
            // import or a menu, and a stall would otherwise skip straight past a trigger.
            float delta = Mathf.Min((float)(now - _lastTick), 0.1f) * speed;
            _lastTick = now;

            float was = Time;
            Time += delta;

            // A throw in here would come back every editor frame for as long as the preview
            // is subscribed, burying the one message that says what went wrong under
            // thousands of repeats. One is enough; stop and let it be read.
            try
            {
                Fire(was, Time);
                SampleAll();
                StepParticles();
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
                Stop();
                return;
            }

            if (Time >= _shot.Total)
            {
                if (loop)
                {
                    RestartLoop();
                }
                else
                {
                    Stop();
                    return;
                }
            }
            UnityEditor.SceneView.RepaintAll();
        }

        private void RestartLoop()
        {
            ClearEffects();
            Time = 0f;
            LastTrigger = -1;
            _castEffectsSpawned = false;
            _activateStarted = false;
        }

        /// <summary>
        /// Everything that happens *at* a moment rather than over one: the cast effects at
        /// the start, the activate effects and the sound as the activate animation begins,
        /// and the triggers. Driven by the interval crossed rather than by the current time,
        /// so a slow editor frame still fires what it stepped over.
        /// </summary>
        private void Fire(float from, float to)
        {
            if (!_castEffectsSpawned && to >= 0f)
            {
                _castEffectsSpawned = true;
                if (playEffects)
                    Spawn(_shot.CastEffects);
                if (logTimeline && _shot.CastDuration > 0f)
                    Log($"{to:0.00}s cast");
            }

            if (!_activateStarted && to >= _shot.CastDuration)
            {
                _activateStarted = true;
                if (playEffects)
                    Spawn(_shot.ActivateEffects);
                if (playAudio && _shot.Audio.Length > 0)
                    PlayPreviewClip(_shot.Audio[Random.Range(0, _shot.Audio.Length)]);
                if (logTimeline)
                    Log($"{to:0.00}s activate ({_shot.ActivateClip.name})");
            }

            for (int i = 0; i < _shot.TriggerTimes.Length; ++i)
            {
                float at = _shot.TriggerTimes[i];
                if (from > at || to < at)
                    continue;
                LastTrigger = i;
                if (logTimeline)
                    Log($"{at:0.00}s trigger {i}");
            }
        }

        /// <summary>
        /// Writes every holding preview's pose in one sampling block. See <see cref="_active"/>
        /// for why it cannot be done one character at a time.
        /// </summary>
        private static void SampleAll()
        {
            UnityEditor.AnimationMode.BeginSampling();
            for (int i = _active.Count - 1; i >= 0; --i)
            {
                if (_active[i] == null)
                {
                    _active.RemoveAt(i);
                    continue;
                }
                _active[i].SamplePose();
            }
            UnityEditor.AnimationMode.EndSampling();
        }

        /// <summary>
        /// Poses this character for its current moment. Called only from
        /// <see cref="SampleAll"/>, which owns the sampling block.
        ///
        /// The cast clip loops for as long as the cast lasts, which is what the runtime does
        /// too — a cast is a duration the skill decides, not a clip length, so a 1.6s cast on
        /// a 1.0s clip has to go round again.
        /// </summary>
        private void SamplePose()
        {
            if (_shot == null || _shot.ActivateClip == null)
                return;

            AnimationClip clip;
            float at;
            if (Time < _shot.CastDuration && _shot.CastClip != null)
            {
                clip = _shot.CastClip;
                at = clip.length > 0f ? Mathf.Repeat(Time, clip.length) : 0f;
            }
            else
            {
                clip = _shot.ActivateClip;
                // back into clip time: the shot's timeline is in real seconds and the clip
                // may be playing at a rate other than 1
                at = Mathf.Clamp((Time - _shot.CastDuration) * _shot.ActivateSpeed, 0f, clip.length);
            }

            UnityEditor.AnimationMode.SampleAnimationClip(gameObject, clip, at);

            // Sampling applies the clip's root curve to the transform, but the game plays
            // these with root motion off, so the swing that turns you 40 degrees here would
            // not turn you at all in play. Put the body back on its mark unless the animation
            // actually asked to move it.
            if (!_shot.RootMotion)
            {
                transform.localPosition = _restPosition;
                transform.localRotation = _restRotation;
            }
        }

        // ---- effects ---------------------------------------------------------

        /// <summary>
        /// Puts an effect on its socket the way `GameEntityModel.InstantiateEffect` does —
        /// by the name in <see cref="GameEffect.effectSocket"/>.
        ///
        /// The runtime looks that name up in the model's `effectContainers`, which on the
        /// demo's characters is empty, so a skill effect authored today would go nowhere in
        /// game and nowhere here either. Rather than hide that, the socket is also looked for
        /// among the character's transforms — the hand sockets are named the same way — and
        /// anything still unmatched is said out loud and hung on the character root, where it
        /// is at least visible.
        /// </summary>
        private void Spawn(GameEffect[] effects)
        {
            if (effects == null)
                return;
            PlayableCharacterModel model = GetComponentInChildren<PlayableCharacterModel>(true);
            _effectsStarted = Time;

            foreach (GameEffect effect in effects)
            {
                if (effect == null)
                    continue;

                Transform socket = FindEffectSocket(model, effect.effectSocket);
                if (socket == null)
                {
                    Debug.LogWarning($"[{nameof(DemoSkillPreview)}] \"{effect.name}\" wants the socket " +
                                     $"\"{effect.effectSocket}\", which \"{name}\" has not got — in game it " +
                                     "would not appear at all. Showing it on the character root instead; add " +
                                     "the socket to the model's effectContainers to fix it properly.", this);
                    socket = transform;
                }

                var spawned = Instantiate(effect.gameObject, socket);
                spawned.name = $"~Preview_{effect.name}";
                spawned.hideFlags = HideFlags.DontSave;
                spawned.transform.localPosition = Vector3.zero;
                spawned.transform.localRotation = Quaternion.identity;
                _effects.Add(spawned);

                foreach (ParticleSystem system in spawned.GetComponentsInChildren<ParticleSystem>(true))
                {
                    // only roots: Simulate walks the children itself, and simulating a child
                    // as well runs it twice as fast as the effect it belongs to
                    if (system.transform.parent == null ||
                        system.transform.parent.GetComponentInParent<ParticleSystem>() == null)
                        _particles.Add(system);
                }

                if (playAudio && effect.randomSoundEffects != null && effect.randomSoundEffects.Length > 0)
                    PlayPreviewClip(effect.randomSoundEffects[Random.Range(0, effect.randomSoundEffects.Length)]);
            }
        }

        /// <summary>
        /// The serialised `effectContainers` are read straight off the model rather than
        /// through its `CacheEffectContainers`, which looks like the obvious accessor and
        /// throws here: it forwards to `MainModel`, and that is only set in `Awake`.
        /// </summary>
        private Transform FindEffectSocket(PlayableCharacterModel model, string socket)
        {
            if (string.IsNullOrEmpty(socket))
                return transform;

            if (model != null && model.EffectContainers != null)
            {
                // EffectContainer is a struct, so there is nothing to null-check but the
                // transform it points at
                foreach (EffectContainer container in model.EffectContainers)
                {
                    if (container.effectSocket == socket && container.transform != null)
                        return container.transform;
                }
            }

            foreach (Transform candidate in GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == socket)
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Particles have no clock of their own in the editor, so they get this one. Each is
        /// re-simulated from birth to the current moment rather than stepped, because
        /// stepping drifts as soon as the editor stalls and the whole point is that the
        /// sparks line up with the frame the animation hits on.
        /// </summary>
        private void StepParticles()
        {
            float elapsed = Mathf.Max(0f, Time - _effectsStarted);
            for (int i = _particles.Count - 1; i >= 0; --i)
            {
                if (_particles[i] == null)
                {
                    _particles.RemoveAt(i);
                    continue;
                }
                _particles[i].Simulate(elapsed, true, true);
            }
        }

        private void ClearEffects()
        {
            foreach (GameObject effect in _effects)
            {
                if (effect == null)
                    continue;
                if (Application.isPlaying)
                    Destroy(effect);
                else
                    DestroyImmediate(effect);
            }
            _effects.Clear();
            _particles.Clear();
        }

        // ---- audio -----------------------------------------------------------

        /// <summary>
        /// One hidden 2D `AudioSource`, never saved, shared by every preview. An `AudioSource`
        /// plays in edit mode when it is told to; `AudioSource.PlayClipAtPoint` would not do,
        /// because the object it spawns waits for a running game to clean it up.
        /// </summary>
        private static AudioSource _audio;

        private static void PlayPreviewClip(AudioClip clip)
        {
            if (clip == null)
                return;
            if (_audio == null)
            {
                GameObject holder = UnityEditor.EditorUtility.CreateGameObjectWithHideFlags(
                    "SkillPreviewAudio", HideFlags.HideAndDontSave, typeof(AudioSource));
                _audio = holder.GetComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0f;
            }
            _audio.PlayOneShot(clip);
        }

        private static void StopAudio()
        {
            if (_audio != null)
                _audio.Stop();
        }

        /// <summary>
        /// Takes a share in the global `AnimationMode`, at most one per instance.
        /// </summary>
        private void BeginSampling()
        {
            if (_holdsSampling)
                return;
            _holdsSampling = true;
            // where the body belongs when the clip is not allowed to move it
            _restPosition = transform.localPosition;
            _restRotation = transform.localRotation;
            _active.Add(this);
            if (!UnityEditor.AnimationMode.InAnimationMode())
                UnityEditor.AnimationMode.StartAnimationMode();
        }

        /// <summary>
        /// Gives it back. The others are re-sampled on the way out, because leaving the block
        /// is what would otherwise revert them; only when nobody is left does the mode go off,
        /// which is what puts the bodies back in their bind pose.
        /// </summary>
        private void EndSampling()
        {
            if (!_holdsSampling)
                return;
            _holdsSampling = false;
            _active.Remove(this);
            if (_active.Count > 0)
            {
                SampleAll();
                return;
            }
            if (UnityEditor.AnimationMode.InAnimationMode())
                UnityEditor.AnimationMode.StopAnimationMode();
        }

        private void Log(string message)
        {
            Debug.Log($"[{nameof(DemoSkillPreview)}] {(skill != null ? skill.name : "(no skill)")} " +
                      $"on \"{name}\": {message}", this);
        }

        [ContextMenu("Play skill")]
        private void ContextPlay()
        {
            Play();
        }

        [ContextMenu("Stop")]
        private void ContextStop()
        {
            Stop();
        }
#endif
    }
}
