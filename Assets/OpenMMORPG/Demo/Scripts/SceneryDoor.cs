using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A house door that swings when a player opens it, and shuts itself a few seconds later.
    ///
    /// The door is solid once it has stopped moving, so it has to be opened before anyone
    /// can walk through. It swings inward, away from whoever is arriving rather than into
    /// their face. The swing, its sounds and the leaf's collision are <see cref="DoorSwing"/>'s;
    /// this adds the opening and the shutting. It ticks only from being opened until it is
    /// shut again.
    ///
    /// **Every player sees the same door.** Whether it is open is the server's, held by the
    /// <see cref="SceneryDoorSync"/> under it and synced to every client in range. A press on
    /// the handle asks the server (through the player's own
    /// <see cref="PlayerCharacterInteractionComponent"/>, which checks the player is in reach),
    /// the server opens it and runs its clock, and the clients swing their leaves when the state
    /// arrives - so the leaf, its sound and its collision agree for everyone, and a door that one
    /// player opened is open for the next one through. A player who arrives later finds it as it
    /// is, without the swing.
    ///
    /// With no network half, or no game running, it is what it always was: a door that each
    /// player opens for themselves.
    /// </summary>
    [DisallowMultipleComponent]
    public class SceneryDoor : DoorSwing
    {
        [Tooltip("How long it stays open before shutting itself, in seconds.")]
        public float openSeconds = 5f;

        /// <summary>
        /// Half the size of the box that decides whether anyone is standing in the doorway,
        /// in metres. A little wider than the opening and about as tall as a character.
        /// </summary>
        private static readonly Vector3 Threshold = new Vector3(0.6f, 1.0f, 0.5f);

        /// <summary>Whether the door is open, or on its way there.</summary>
        public bool IsOpen { get; private set; }

        private readonly Collider[] _inTheWay = new Collider[8];
        private float _closeAt;
        private SceneryDoorSync _sync;
        private SceneryDoorHandle[] _handles;
        private static bool s_warnedNoRequester;

        protected override void Awake()
        {
            base.Awake();
            _sync = GetComponentInChildren<SceneryDoorSync>(true);
            _handles = GetComponentsInChildren<SceneryDoorHandle>(true);
        }

        /// <summary>Whether the door's state is the network's rather than this copy's own.</summary>
        private bool Networked
        {
            get
            {
                if (_sync == null)
                    return false;
                BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
                return manager != null && manager.IsNetworkActive;
            }
        }

        /// <summary>Whether this copy decides when the door opens and shuts: the server, or any copy that is not networked.</summary>
        private bool IsAuthority
        {
            get { return !Networked || (_sync.IsSpawned && _sync.IsServer); }
        }

        /// <summary>
        /// Opens a shut door, shuts an open one - for the player at this client. Networked, it
        /// asks the server, and the door moves when the server's answer arrives.
        /// </summary>
        public void Toggle()
        {
            if (!Networked)
            {
                SetOpen(!IsOpen);
                return;
            }
            // Not spawned to this client - out of its range, or the map still arriving.
            if (!_sync.IsSpawned)
                return;
            PlayerCharacterInteractionComponent requester = PlayerCharacterInteractionComponent.Local;
            if (requester == null)
            {
                if (!s_warnedNoRequester)
                {
                    s_warnedNoRequester = true;
                    Debug.LogWarning($"[{nameof(SceneryDoor)}] The playing character has no {nameof(PlayerCharacterInteractionComponent)}, " +
                                     "so it cannot ask the server to open a door.", this);
                }
                return;
            }
            requester.CallCmdToggleDoor(_sync.ObjectId);
        }

        /// <summary>
        /// The server's answer to a player's <see cref="Toggle"/>, once
        /// <see cref="PlayerCharacterInteractionComponent"/> has checked they are in reach. Not while
        /// the leaf is moving, or a press mid-swing reverses it.
        /// </summary>
        public void ServerToggle()
        {
            if (!IsAuthority || IsSwinging)
                return;
            SetOpen(!IsOpen);
        }

        /// <summary>
        /// Whether a player standing here can reach a handle: each handle's own reach, plus
        /// <paramref name="tolerance"/> for how far the player may have moved on their own client
        /// since they pressed.
        /// </summary>
        public bool InReach(Vector3 position, float tolerance)
        {
            if (_handles == null || _handles.Length == 0)
                return Vector3.Distance(position, transform.position) <= 3f + tolerance;
            foreach (SceneryDoorHandle handle in _handles)
            {
                if (handle != null && Vector3.Distance(position, handle.transform.position) <= handle.activateDistance + tolerance)
                    return true;
            }
            return false;
        }

        /// <summary>Opens or shuts it, by authority: through the synced state when there is one, otherwise here.</summary>
        private void SetOpen(bool open)
        {
            IsOpen = open;
            if (open)
                _closeAt = Time.time + openSeconds;
            if (Networked && _sync.IsSpawned && _sync.IsServer)
                _sync.ServerSetOpen(open);
            else
                SwingTo(open ? openAngle : 0f);
        }

        /// <summary>
        /// The state as the server has it, on every copy including the server's own: swings to
        /// it, or on the first word of it - a player arriving - goes straight there.
        /// </summary>
        internal void ShowState(bool open, bool initial)
        {
            IsOpen = open;
            if (initial)
                SetAtOnce(open ? openAngle : 0f);
            else
                SwingTo(open ? openAngle : 0f);
        }

        /// <summary>An open door has its closing time to watch, where the time is kept.</summary>
        protected override bool StayAwake => IsOpen && IsAuthority;

        protected override void Tick()
        {
            if (!IsOpen || !IsAuthority || Time.time < _closeAt)
                return;
            // Not while someone is standing in it. The leaf is solid again the moment
            // it stops, and a door that closed through a player would put a collider
            // inside them — the physics answer to which is to fire them out of it.
            if (Blocked())
            {
                _closeAt = Time.time + 1f;
                return;
            }
            SetOpen(false);
        }

        /// <summary>Whether a character is standing in the doorway.</summary>
        private bool Blocked()
        {
            int found = Physics.OverlapBoxNonAlloc(
                transform.position + Vector3.up * Threshold.y,
                Threshold,
                _inTheWay,
                transform.rotation,
                ~0,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < found; ++i)
            {
                if (_inTheWay[i] != null &&
                    _inTheWay[i].GetComponentInParent<BaseCharacterEntity>() != null)
                    return true;
            }
            return false;
        }
    }
}
