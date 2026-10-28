using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// What a tool landing on a tree, a boulder or an iron vein looks and sounds like: the chop or
    /// the strike, a burst of chips and dust at the struck face, and a bigger one when the node is
    /// felled.
    ///
    /// A node played nothing on a hit. The kit's hit effects (`DamageableEntity.PlayHitEffects`) go
    /// through the entity's `Model`, and a harvestable has none, so the generic impact the demo
    /// plays on characters never reached a node; the weapons' melee impact effects are keyed by tag
    /// and the demo defines none. (Called `DemoHarvestImpactSound` until the particles joined it.)
    ///
    /// Heard and seen on every client that sees the node, from three events that all arrive on
    /// clients: the kit's `onNormalDamageHit` (raised for every landed blow, **including a wrong tool
    /// that does 0 damage** - the clang tells the player the swing connected and nothing came of it;
    /// but it travels unreliably), the node's HP dropping (a synced field, reliable, but silent for
    /// a 0-damage hit), and the node's destroy event. The first two report the same blow a frame or
    /// a few tens of milliseconds apart, so a short gap after each hit drops the second. A node felled
    /// by the last blow may be gone before either hit event arrives, so HP reaching 0 and the destroy
    /// event both fell it, and only the first to arrive does.
    ///
    /// Played through <see cref="OneShotSound"/> and the kit's effect pool rather than through the
    /// entity, so the sound and the debris outlive a node that is destroyed the instant it falls.
    /// Clips and effect prefabs are written by DemoAudioWiring (Wire Audio) and
    /// DemoSkillEffectBuilder (Build Harvest Effects); an empty slot is simply skipped.
    /// </summary>
    [RequireComponent(typeof(HarvestableEntity))]
    public class HarvestImpactEffects : MonoBehaviour
    {
        [Header("Sound")]
        public AudioClip[] clips = new AudioClip[0];

        [Range(0f, 1f)]
        public float volume = 0.9f;

        [Tooltip("Metres out to which it plays at full volume.")]
        public float near = 6f;

        [Tooltip("Metres beyond which it is inaudible.")]
        public float far = 50f;

        [Tooltip("Seconds after a hit during which another is taken to be the same blow. Well under a swing's length.")]
        public float gap = 0.3f;

        [Header("Particles")]
        [Tooltip("Chips and dust at the struck face, on every blow that does not fell the node.")]
        public GameEffect hitEffect;

        [Tooltip("The finisher, where the node stood.")]
        public GameEffect fellEffect;

        [Tooltip("Where the crown is, as a share of the node's height. A child of the fell effect called Foliage is moved there (leaves fall from the top of a tree, not its foot). 0 leaves it alone.")]
        [Range(0f, 1f)]
        public float crownShare;

        private HarvestableEntity _entity;
        private CapsuleCollider _body;
        private float _last = -10f;
        private bool _felled;

        private void Awake()
        {
            _entity = GetComponent<HarvestableEntity>();
            bool nothingToDo = (clips == null || clips.Length == 0) && hitEffect == null && fellEffect == null;
            if (Application.isBatchMode || _entity == null || nothingToDo)
            {
                enabled = false;
                return;
            }
            _body = GetComponent<CapsuleCollider>();
            _entity.onNormalDamageHit.AddListener(Hit);
            _entity.onCurrentHpChange += OnHpChange;
            _entity.OnHarvestableDestroy.AddListener(Fell);
        }

        private void OnDestroy()
        {
            if (_entity == null)
                return;
            // The kit clears and NULLS these when the entity is destroyed
            // (DamageableEntity_Cleanup), and that runs before this does - so a bare
            // `.RemoveListener` threw on every node when play mode stopped.
            _entity.onNormalDamageHit?.RemoveListener(Hit);
            _entity.onCurrentHpChange -= OnHpChange;
            _entity.OnHarvestableDestroy?.RemoveListener(Fell);
        }

        private void OnHpChange(DamageableEntity entity, int oldHp, int newHp)
        {
            // A drop only: the first read on spawn and the refill on respawn both climb.
            if (newHp >= oldHp)
                return;
            if (newHp <= 0)
                Fell();
            else
                Hit();
        }

        private void Hit()
        {
            if (!enabled || _entity == null || !_entity.IsClient || _felled || Time.time - _last < gap)
                return;
            _last = Time.time;
            Sound();
            if (hitEffect != null)
                Spawn(hitEffect, false);
        }

        private void Fell()
        {
            if (!enabled || _entity == null || !_entity.IsClient || _felled)
                return;
            _felled = true;
            // The felling blow's own sound, unless the hit events already played it.
            if (Time.time - _last >= gap)
            {
                _last = Time.time;
                Sound();
            }
            if (fellEffect != null)
                Spawn(fellEffect, true);
        }

        private void Sound()
        {
            OneShotSound.PlayAt(clips, AimPoint(), volume, near, far);
        }

        /// <summary>Where the blow lands: the aim point, not the foot - a tall tree's foot is a few metres from the axe.</summary>
        private Vector3 AimPoint()
        {
            Transform at = _entity.OpponentAimTransform != null ? _entity.OpponentAimTransform : transform;
            return at.position;
        }

        private void Spawn(GameEffect effect, bool felling)
        {
            Vector3 aim = AimPoint();
            Vector3 toPlayer = Vector3.zero;
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player != null)
            {
                toPlayer = player.EntityTransform.position - transform.position;
                toPlayer.y = 0f;
            }
            if (toPlayer.sqrMagnitude < 0.0001f)
                toPlayer = Vector3.forward;
            toPlayer.Normalize();

            // A blow's debris leaves the face the tool struck - the side toward whoever swung - and
            // flies back out of it; a felled node's is thrown from where it stood, all round.
            Vector3 at;
            if (felling)
            {
                at = transform.position;
            }
            else
            {
                float reach = _body != null ? _body.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.z) : 0.5f;
                at = aim + toPlayer * reach;
            }
            GameEffect instance = PoolSystem.GetInstance(effect, at, Quaternion.LookRotation(toPlayer, Vector3.up));
            if (instance == null || !felling || crownShare <= 0f || _body == null)
                return;
            Transform foliage = instance.transform.Find("Foliage");
            if (foliage != null)
            {
                float height = _body.height * transform.lossyScale.y;
                foliage.position = transform.position + Vector3.up * (height * crownShare);
            }
        }
    }
}
