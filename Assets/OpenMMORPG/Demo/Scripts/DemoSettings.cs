using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Demo-only player settings, kept in `PlayerPrefs` alongside the kit's own.
    ///
    /// **This exists because the setting and the thing it controls cannot see each other.** The
    /// toggle lives in the gameplay canvas prefab; the controller it affects is spawned at runtime
    /// with the player character. A prefab cannot hold a reference to something that does not exist
    /// yet, and the controller has no way to find one toggle among a canvas of them. A named
    /// preference is the seam: the toggle writes it, the controller reads it, neither knows the
    /// other exists.
    /// </summary>
    public static class DemoSettings
    {
        public const string ClickToMoveKey = "DEMO_CLICK_TO_MOVE";

        private static bool _loaded;
        private static bool _clickToMove;

        /// <summary>
        /// Whether a click on open ground walks the character there.
        ///
        /// **Off by default, deliberately.** Click-to-target and click-to-move are separate
        /// features that happen to share a button: the first is a WoW convention, the second is an
        /// option there that ships disabled. The demo follows suit.
        ///
        /// Cached rather than read from `PlayerPrefs` every frame - the controller asks on every
        /// input tick. Caching a *user setting* is safe where caching a live count is not: the only
        /// thing that writes it is the toggle, which writes both the cache and the store together.
        /// </summary>
        public static bool ClickToMove
        {
            get
            {
                if (!_loaded)
                {
                    _clickToMove = PlayerPrefs.GetInt(ClickToMoveKey, 0) != 0;
                    _loaded = true;
                }
                return _clickToMove;
            }
            set
            {
                _clickToMove = value;
                _loaded = true;
                PlayerPrefs.SetInt(ClickToMoveKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }
}
