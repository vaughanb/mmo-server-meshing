using System.Collections.Generic;
using Insthync.UnityEditorUtils;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MultiplayerARPG
{
    /// <summary>
    /// A treasure chest that stands in the world, opens when a player activates it, hands
    /// out a few things from a loot table, and fills itself again a while after it has been
    /// emptied.
    ///
    /// **It is the kit's corpse.** `ItemsContainerEntity` is what a monster leaves behind:
    /// a networked list of items with a dialog to take them from, server-checked distance
    /// and looting rules, and all of that already wired into the demo's controller and
    /// canvas. A chest is the same thing standing still, so it reuses the lot and changes
    /// three things:
    ///
    /// - **It is never destroyed.** The base schedules `NetworkDestroy(_appearDuration)`
    ///   the moment its identity initialises, because a corpse is meant to rot. A delay
    ///   below zero is ignored by the identity, so the chest sets it to -1 before that
    ///   runs. And `PickedUp`, which the base uses to destroy an emptied container, starts
    ///   the refill timer instead.
    /// - **It fills itself** from a kit `ItemDropTable`, on the server, when it spawns and
    ///   again when the timer runs out. The roll is done here rather than through the
    ///   kit's `ItemDropManager`, whose loop keeps drawing until it has its count and can
    ///   spin forever on a short table with a no-drop weight.
    /// - **It has a lid.** The Quaternius chests are rigged and ship Open/Close clips, so
    ///   the lid is animated with those through a `PlayableGraph` on an `Animator` with no
    ///   controller - the same way the demo poses its NPCs. The lid stands open while the
    ///   chest is empty (so a looted chest reads as looted from across the room) and while
    ///   **anyone** has its loot window up, and every player sees the same: each client tells
    ///   the server when it opens or closes the window (through its
    ///   <see cref="PlayerCharacterInteractionComponent"/>), the server keeps the set of
    ///   looters - dropping any who leave, walk off or die without saying so - and syncs
    ///   whether there are any. The looter's own lid does not wait for the round trip.
    ///   Refilling closes it everywhere, because the item list arrives everywhere.
    ///
    /// Placed by DemoTreasureBuilder, which also builds the prefab this sits on and gives
    /// each chest in a scene its own scene object id - two scene objects on one id is the
    /// failure that makes every scene object after it vanish; see DemoShrineBuilder.
    /// </summary>
    public class TreasureChestEntity : ItemsContainerEntity
    {
        [Category(6, "Treasure Chest Settings")]
        [Tooltip("What the chest can hold. Each row is rolled on its own chance.")]
        public ItemDropTable lootTable;

        [Tooltip("Fewest kinds of item a fill produces. Topped up from the likeliest rows that missed their roll.")]
        [Min(0)]
        public int minItems = 2;

        [Tooltip("Most kinds of item a fill produces.")]
        [Min(1)]
        public int maxItems = 3;

        [Tooltip("Fewest gold coins a fill puts in, as a stack of the gold item. 0 with maxGold 0 is no gold.")]
        [Min(0)]
        public int minGold = 0;

        [Tooltip("Most gold coins a fill puts in. The coins are taken like any other item and credit the purse when they are.")]
        [Min(0)]
        public int maxGold = 0;

        [Tooltip("How long after it is emptied before it is full again, in seconds.")]
        [Min(1f)]
        public float refillSeconds = 300f;

        [Tooltip("How close a player has to stand to open it, in metres. The server checks the same figure when they take something.")]
        public float activateDistance = 3f;

        [Tooltip("The Animator on the model root that the lid clips drive. No controller; the clips are played through a PlayableGraph.")]
        public Animator lid;

        [Tooltip("The pack's open swing.")]
        public AnimationClip openClip;

        [Tooltip("The pack's close swing.")]
        public AnimationClip closeClip;

        [Tooltip("The pack's held-open pose, for a chest that is already open when a player arrives.")]
        public AnimationClip openedPose;

        [Tooltip("The pack's held-shut pose.")]
        public AnimationClip closedPose;

        [Tooltip("Played as the lid swings up. One picked at random; none is silent.")]
        public AudioClip[] openSounds;

        [Tooltip("Played so that its thud lands as the lid meets the box; see closeSoundLead.")]
        public AudioClip[] closeSounds;

        /// <summary>
        /// How far into the close clip its thud is, in seconds. Measured off ChestClose.wav:
        /// the thud peaks 0.05 s in. The clip starts this long before the lid lands, so the
        /// two coincide; played as the lid started to fall, it landed early.
        /// </summary>
        [Tooltip("Seconds from the start of the close sound to its thud. The sound starts this long before the lid lands.")]
        [Min(0f)]
        public float closeSoundLead = 0.05f;

        /// <summary>
        /// When the lid meets the box, in seconds from the start of the close animation.
        ///
        /// **Not the clip's length.** The pack's Close clip runs 0.47 s, but the lid is down
        /// at 0.23 s and the rest is the lid lying still - sampled frame by frame in the
        /// editor harness: it rocks back to -88 degrees at 0.08 s, falls, and reads 0 from
        /// 0.237 s on. Timing the thud to the clip's end put it a quarter of a second after
        /// the lid had stopped moving.
        /// </summary>
        [Tooltip("Seconds into the close animation at which the lid meets the box. Measured off the pack's clip; its length is not it.")]
        [Min(0f)]
        public float lidLandsAt = 0.23f;

        [Tooltip("Loudness of both, before the player's SFX setting.")]
        [Range(0f, 1f)]
        public float soundVolume = 0.8f;

        [Tooltip("Tint applied while a player is looking at it, as the village doors do. The kit draws no target UI for a container.")]
        public Color highlight = new Color(1.5f, 1.38f, 1.1f, 1f);

        [Category("Sync Fields")]
        [Tooltip("Whether any player has this chest's loot window open, as the server has it.")]
        [SerializeField]
        protected SyncFieldBool beingLooted = new SyncFieldBool();

        /// <summary>How often the server checks its looters are still there, in seconds.</summary>
        private const float LooterCheckInterval = 0.5f;

        /// <summary>
        /// How long after spawning the lid waits before trusting an empty item list. The
        /// list arrives a moment after the entity does, and acting on it in that moment
        /// would open every chest on arrival and then shut it again.
        /// </summary>
        private const float ArrivalGrace = 1f;

        /// <summary>Full volume this close to the chest, in metres: the vault, not the street outside.</summary>
        private const float SoundNear = 2f;
        private const float SoundFar = 25f;

        private bool _waitingToRefill;
        private float _refillAt;

        private readonly HashSet<uint> _looters = new HashSet<uint>();
        private readonly List<uint> _gone = new List<uint>();
        private float _nextLooterCheck;
        private bool _reportedLooting;

        private bool _lidKnown;
        private bool _lidOpen;
        private float _closeSoundAt = -1f;
        private float _spawnedAt;
        private PlayableGraph _graph;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private bool _lit;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        /// <summary>Whether the lid is up, or on its way there.</summary>
        public bool IsOpen { get { return _lidOpen; } }

        /// <summary>Seconds until an emptied chest fills again; zero while it has anything in it.</summary>
        public float SecondsToRefill
        {
            get { return _waitingToRefill ? Mathf.Max(0f, _refillAt - Time.unscaledTime) : 0f; }
        }

        protected override void EntityAwake()
        {
            base.EntityAwake();
            // The base destroys itself this long after its identity initialises - a corpse
            // is meant to go. Below zero the identity ignores the request.
            _appearDuration = -1f;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _block = new MaterialPropertyBlock();
        }

        protected override void SetupNetElements()
        {
            base.SetupNetElements();
            beingLooted.syncMode = LiteNetLibSyncFieldMode.ServerToClients;
        }

        public override void OnIdentityInitialize()
        {
            base.OnIdentityInitialize();
            _spawnedAt = Time.unscaledTime;
            if (IsServer)
                Fill();
        }

        protected override void EntityOnDestroy()
        {
            base.EntityOnDestroy();
            if (_graph.IsValid())
                _graph.Destroy();
        }

        // ---- the loot -------------------------------------------------------------

        /// <summary>
        /// Called by the kit on the server after every pickup. A corpse destroys itself
        /// here once it is empty; a chest starts counting down to being full again.
        /// </summary>
        public override void PickedUp()
        {
            if (!IsServer || Items.Count > 0 || _waitingToRefill)
                return;
            _waitingToRefill = true;
            _refillAt = Time.unscaledTime + refillSeconds;
        }

        /// <summary>Empties the chest and rolls a fresh set of items into it. Server only.</summary>
        public void Fill()
        {
            if (!IsServer)
                return;
            _waitingToRefill = false;
            Items.Clear();
            // Coins first, as a stack of the gold represent item: the kit's container pickup
            // recognises it and credits the purse instead of putting it in the bag (see
            // GoldDropToCorpse for the same route out of a monster corpse).
            int gold = maxGold > 0 ? Random.Range(Mathf.Min(minGold, maxGold), maxGold + 1) : 0;
            BaseItem goldItem = GameInstance.Singleton.GoldDropRepresentItem;
            if (gold > 0 && goldItem != null)
                Items.Add(CharacterItem.Create(goldItem, 1, gold));
            foreach (ItemDrop row in Roll())
                Items.Add(CharacterItem.Create(row.item, row.GetRandomedLevel(), row.GetRandomedAmount()));
        }

        /// <summary>
        /// One pass over the table in a shuffled order, each row kept on its own chance,
        /// stopping at <see cref="maxItems"/> kinds. Short of <see cref="minItems"/>, the
        /// likeliest of the rows that missed are added until it is met - so a chest is
        /// never found empty on a run of bad luck, and no draw is ever repeated.
        /// </summary>
        private List<ItemDrop> Roll()
        {
            var chosen = new List<ItemDrop>();
            if (lootTable == null || lootTable.randomItems == null)
                return chosen;

            var pool = new List<ItemDrop>();
            foreach (ItemDrop row in lootTable.randomItems)
            {
                if (row.item != null && row.maxAmount > 0 && row.dropRate > 0f)
                    pool.Add(row);
            }
            for (int i = pool.Count - 1; i > 0; --i)
            {
                int j = Random.Range(0, i + 1);
                ItemDrop swap = pool[i];
                pool[i] = pool[j];
                pool[j] = swap;
            }

            int most = Mathf.Clamp(Random.Range(minItems, maxItems + 1), 0, pool.Count);
            int fewest = Mathf.Min(minItems, pool.Count);
            var missed = new List<ItemDrop>();
            foreach (ItemDrop row in pool)
            {
                if (chosen.Count < most && Random.value < row.dropRate)
                    chosen.Add(row);
                else
                    missed.Add(row);
            }
            if (chosen.Count < fewest)
            {
                missed.Sort((a, b) => b.dropRate.CompareTo(a.dropRate));
                for (int i = 0; i < missed.Count && chosen.Count < fewest; ++i)
                    chosen.Add(missed[i]);
            }
            return chosen;
        }

        // ---- the lid --------------------------------------------------------------

        // ---- who is looting --------------------------------------------------------

        /// <summary>
        /// A player has the loot window up. Server only, from the player's own request; taken
        /// only from someone the kit would let take an item from here.
        /// </summary>
        public void ServerAddLooter(BasePlayerCharacterEntity looter)
        {
            if (!IsServer || looter == null || looter.IsDead() || !looter.IsGameEntityInDistance(this))
                return;
            _looters.Add(looter.ObjectId);
            beingLooted.Value = true;
        }

        /// <summary>A player has closed the loot window. Server only.</summary>
        public void ServerRemoveLooter(BasePlayerCharacterEntity looter)
        {
            if (!IsServer || looter == null)
                return;
            _looters.Remove(looter.ObjectId);
            beingLooted.Value = _looters.Count > 0;
        }

        /// <summary>
        /// Drops looters who are gone without saying so: disconnected, dead, or walked out of
        /// reach with the window still up. Twice a second, and only while there are any.
        /// </summary>
        private void PruneLooters()
        {
            if (_looters.Count == 0 || Time.unscaledTime < _nextLooterCheck)
                return;
            _nextLooterCheck = Time.unscaledTime + LooterCheckInterval;
            _gone.Clear();
            foreach (uint id in _looters)
            {
                if (!Manager.TryGetEntityByObjectId(id, out BasePlayerCharacterEntity looter) ||
                    looter.IsDead() || !looter.IsGameEntityInDistance(this))
                    _gone.Add(id);
            }
            if (_gone.Count == 0)
                return;
            foreach (uint id in _gone)
                _looters.Remove(id);
            beingLooted.Value = _looters.Count > 0;
        }

        /// <summary>Tells the server when the local player opens or closes this chest's loot window.</summary>
        private void ReportLooting(bool looting)
        {
            if (looting == _reportedLooting)
                return;
            PlayerCharacterInteractionComponent requester = PlayerCharacterInteractionComponent.Local;
            if (requester == null)
                return;
            _reportedLooting = looting;
            requester.CallCmdSetLooting(ObjectId, looting);
        }

        protected override void EntityUpdate()
        {
            base.EntityUpdate();
            if (IsServer)
            {
                if (_waitingToRefill && Time.unscaledTime >= _refillAt)
                    Fill();
                PruneLooters();
            }
            if (!IsClient)
                return;
            UpdateLid();
            UpdateCloseSound();
            UpdateHighlight();
        }

        /// <summary>Whether the local player has this chest's loot dialog open.</summary>
        private bool BeingLooted()
        {
            BaseUISceneGameplay ui = BaseUISceneGameplay.Singleton;
            return ui != null && ui.IsItemsContainerDialogVisible() && ReferenceEquals(ui.ItemsContainerEntity, this);
        }

        private void UpdateLid()
        {
            bool mine = BeingLooted();
            ReportLooting(mine);
            // Ours at once; anyone else's when the server says so.
            bool open = Items.Count == 0 || mine || beingLooted.Value;
            if (!_lidKnown)
            {
                // The prefab is saved shut, so a chest that turns out to be full needs no
                // pose at all. An empty one is trusted only once the item list has had its
                // moment to arrive.
                if (Items.Count == 0 && Time.unscaledTime - _spawnedAt < ArrivalGrace)
                    return;
                _lidKnown = true;
                _lidOpen = open;
                if (open)
                    Play(openedPose);
                return;
            }
            if (open == _lidOpen)
                return;
            _lidOpen = open;
            Play(open ? openClip : closeClip);
            // Only on a change seen, not on the pose a chest is found in: a player arriving
            // at a looted chest should not hear it open. The open clip goes with the lid
            // rising; the close clip is timed so its thud lands as the lid does.
            if (open)
            {
                _closeSoundAt = -1f;
                OneShotSound.PlayAt(openSounds, EntityTransform.position, soundVolume, SoundNear, SoundFar);
            }
            else
            {
                _closeSoundAt = Time.time + Mathf.Max(0f, lidLandsAt - closeSoundLead);
            }
        }

        private void UpdateCloseSound()
        {
            if (_closeSoundAt < 0f || Time.time < _closeSoundAt)
                return;
            _closeSoundAt = -1f;
            OneShotSound.PlayAt(closeSounds, EntityTransform.position, soundVolume, SoundNear, SoundFar);
        }

        /// <summary>
        /// Plays one clip once and holds its last frame. A fresh graph each time rather
        /// than a mixer: a chest changes state a handful of times an hour, and the
        /// Animator keeps the pose the old graph left it in until the new one evaluates.
        /// </summary>
        private void Play(AnimationClip clip)
        {
            if (lid == null || clip == null)
                return;
            if (_graph.IsValid())
                _graph.Destroy();
            AnimationPlayableUtilities.PlayClip(lid, clip, out _graph);
        }

        // ---- the highlight ----------------------------------------------------------

        private void UpdateHighlight()
        {
            BasePlayerCharacterController controller = BasePlayerCharacterController.Singleton;
            bool lit = controller != null && ReferenceEquals(controller.SelectedEntity, this);
            if (lit == _lit)
                return;
            _lit = lit;
            Light(lit);
        }

        /// <summary>
        /// Tints the chest through property blocks, so the trim materials the whole prop
        /// library shares are not touched.
        /// </summary>
        private void Light(bool on)
        {
            if (_renderers == null)
                return;
            for (int i = 0; i < _renderers.Length; ++i)
            {
                if (_renderers[i] == null)
                    continue;
                _renderers[i].GetPropertyBlock(_block);
                _block.SetColor(BaseColor, on ? highlight : Color.white);
                _renderers[i].SetPropertyBlock(_block);
            }
        }

        // ---- activation -------------------------------------------------------------

        public override float GetActivatableDistance()
        {
            return activateDistance;
        }

        /// <summary>
        /// Always, even empty: opening an empty chest and seeing nothing in it is the honest
        /// answer, and a click that did nothing would read as a chest that does nothing.
        /// </summary>
        public override bool CanActivate()
        {
            return true;
        }
    }
}
