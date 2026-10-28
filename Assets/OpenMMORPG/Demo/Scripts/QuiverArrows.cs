using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The arrows standing in a quiver: one for each arrow the character has left, up to as many as the
    /// quiver holds, so it empties over the last dozen shots and is bare when the arrows run out.
    ///
    /// The quiver rides with the bow - it is a second model on each bow item, in the hand set and the
    /// sheathed set alike, on the back socket (see <c>DemoSheathBuilder.TryQuiverCarry</c>) - so the kit
    /// shows and hides it with the bow and this only decides how many arrows stand in it. The count is
    /// <see cref="QuiverArrowCount"/>'s: the player's own bag for their own quiver, the server's synced
    /// figure for anyone else's, because the bag is synced to its owner only. Until a count has arrived
    /// the quiver shows full.
    ///
    /// **An arrow leaves the quiver when the hand takes it, not when the server counts it gone.** The bow
    /// (<see cref="BowEquipmentEntity"/>) carries an arrow in the fist from the start of a shot, and the
    /// server spends it only at the loose; showing the count alone would leave the arrow in the quiver
    /// and in the hand at once. So one arrow is held back from the quiver while the bow has one out, and
    /// for a moment after the loose until the new count arrives - but only while the count is still the
    /// one the shot began with, so the arrow is never taken off twice. A shot let down without loosing
    /// puts it straight back.
    ///
    /// A plain component, **never an <see cref="BaseEquipmentEntity"/>**: the quiver is listed under the
    /// bow's right-hand slot, and the kit makes the first equipment entity it meets there the hand's -
    /// the one it tells about launches - which would deafen the bow.
    ///
    /// On a character nobody spawned - the create and select screens' previews - the quiver is full; on
    /// the character sheet's copy of the player it shows the player's own count.
    /// </summary>
    [DisallowMultipleComponent]
    public class QuiverArrows : MonoBehaviour
    {
        [Tooltip("The arrows in the quiver, in the order they are kept: the first is the last to go. Written by the weapon builder.")]
        public GameObject[] arrows = new GameObject[0];

        [Tooltip("Seconds after a loose that the arrow stays off the quiver while the server's new count is on its way.")]
        public float countGrace = 1f;

        private QuiverArrowCount _count;
        private BaseCharacterModel _model;
        private bool _drawn;
        private int _drawnFrom;
        private float _heldUntil;
        private int _shown = -1;

        private void Awake()
        {
            _count = GetComponentInParent<QuiverArrowCount>();
            _model = GetComponentInParent<BaseCharacterModel>();
        }

        private void OnEnable()
        {
            // At once rather than on the next Update: the kit swaps this quiver for a fresh copy every
            // time the bow is drawn or put away, and a frame of the prefab's full quiver would flicker.
            _drawn = false;
            _heldUntil = 0f;
            _shown = -1;
            Refresh();
        }

        private void Update()
        {
            Refresh();
        }

        private void Refresh()
        {
            // A character that keeps no count - a monster or an NPC, whose quiver is for show - is full,
            // and is not borrowing anybody else's: the playing character's count is only for the
            // character sheet's copy of the player, which keeps none either but is a player.
            if (_count == null)
            {
                Show(arrows.Length);
                return;
            }
            int count = Available();
            BowEquipmentEntity bow = _model != null ? _model.CacheRightHandEquipmentEntity as BowEquipmentEntity : null;
            if (bow != null && bow.HasArrowInHand)
            {
                if (!_drawn)
                {
                    _drawn = true;
                    _drawnFrom = count;
                }
                _heldUntil = float.PositiveInfinity;
            }
            else if (_drawn)
            {
                _drawn = false;
                _heldUntil = bow != null && bow.HasLoosed ? Time.time + countGrace : 0f;
            }
            if (Time.time < _heldUntil && count > 0 && count >= _drawnFrom)
                --count;
            Show(count);
        }

        /// <summary>How many arrows the character has, before any is taken out.</summary>
        private int Available()
        {
            if (_count.IsLive)
                return _count.HasCount ? _count.Count : arrows.Length;
            // The character sheet films a copy of the player, which nobody spawned; it should still show
            // the player's quiver. A menu preview has no playing character and shows it full.
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            QuiverArrowCount live = player != null ? player.GetComponent<QuiverArrowCount>() : null;
            return live != null && live.IsLive && live.HasCount ? live.Count : arrows.Length;
        }

        private void Show(int count)
        {
            count = Mathf.Clamp(count, 0, arrows.Length);
            if (count == _shown)
                return;
            _shown = count;
            for (int i = 0; i < arrows.Length; ++i)
            {
                if (arrows[i] != null && arrows[i].activeSelf != (i < count))
                    arrows[i].SetActive(i < count);
            }
        }
    }
}
