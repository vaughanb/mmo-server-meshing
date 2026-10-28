using Cysharp.Threading.Tasks;
using MultiplayerARPG.GameData.Model.Playables;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MultiplayerARPG
{
    /// <summary>
    /// The kit's ladder component with the getting on and off done properly, and a climb
    /// that moves at a speed the climbing animation can keep up with.
    ///
    /// Three things about the kit's own version made climbing feel wrong, and all three are
    /// fixed here without touching it:
    ///
    /// - **It snapped on and off.** The move to the rungs (and from them) is meant to be a
    ///   lerp over the enter or exit clip, but the lerp's factor is written
    ///   `time - start / duration` - precedence and all - so after the first few seconds of
    ///   play it is always 1 and the character is teleported to the destination on the first
    ///   frame, then stands there while the clip plays. This drives the position itself,
    ///   eased over the clip, and feeds the kit the same point as both ends of its lerp so
    ///   whatever its factor comes to, the answer is the eased position.
    /// - **It climbed at running pace.** A climb takes the entity's ordinary move speed (4 m/s
    ///   here), while the climb loop at its own pace rises about half a metre a second: the
    ///   hands and feet slid eight rungs for every one they took. The server sets
    ///   <see cref="BaseGameEntity.OverrideMoveSpeed"/> to <see cref="climbSpeed"/> for the
    ///   climb; the loop's playback rate (see DemoAnimationSet) is set so that the two match.
    /// - **It could not get over a parapet.** The character is moved with the character
    ///   controller, which collides, so the top of the climb line had to sit above the kerb
    ///   the climber hangs beside, and the character stood on air next to the deck. The
    ///   enter and exit paths here are placed directly, so they can arc over the kerb, and
    ///   the top of the line is a metre down the wall where the hands are on the top rung.
    ///
    /// Letting go is an exit of type <see cref="LadderEntranceType.Middle"/>: no clip and no
    /// move, the kit releases the ladder and the character falls. The kit's version left the
    /// destination unset for that type and lerped to wherever the last climb ended.
    ///
    /// **Getting off at the ends works at any frame rate.** The kit decides a climber has
    /// reached an end when a frame's climb carries it more than five centimetres past it. Each
    /// frame it pulls the climber back onto the ladder's line and then moves it its speed times
    /// the frame's length along it, so how far past the end it gets IS speed times frame length:
    /// at the kit's running-pace climb that was seven centimetres at 60 frames a second and three
    /// at 144, so above about 80 the character reached the top and hung there, climbing on the
    /// spot. At <see cref="climbSpeed"/> it is two centimetres at 60 and the kit's test never
    /// passes at all. So the owner asks the same question with the frame length taken out of it
    /// (<see cref="LeaveAtTheEnds"/>): at an end and still pushing past it means leaving. Both
    /// set the same flag first, so whichever notices first has it and the other stands down.
    ///
    /// The position is driven only where the kit simulates movement - the owner client, or
    /// the server for a server-authoritative character; everyone else is synced as usual.
    /// Added to the player entities by DemoEntityBuilder in place of the kit's component.
    /// </summary>
    [DisallowMultipleComponent]
    public class EasedCharacterLadderComponent : CharacterLadderComponent
    {
        /// <summary>
        /// The climb speed the demo's climb loop is rated for, in metres a second: six metres of
        /// tower in five seconds, the loop at about two and a half times its authored pace to
        /// match, which reads as brisk. DemoAnimationSet reads this to set that rate, so a
        /// different <see cref="climbSpeed"/> wants the loop's rate set to match.
        /// </summary>
        public const float DefaultClimbSpeed = 1.2f;

        [Tooltip("How fast a character climbs, in metres a second. The server holds the entity's move speed to this for the climb; " +
                 "set the climb loop's playback rate to match, or the hands and feet slide on the rungs.")]
        public float climbSpeed = DefaultClimbSpeed;

        /// <summary>
        /// How far above the higher end of a top transition the path peaks, so the capsule
        /// clears the kerb (the demo's watchtower: 0.11 m) with room to spare.
        /// </summary>
        [Tooltip("How far above the higher end of getting on or off at the top the path peaks, in metres: enough for the capsule to clear the kerb at the ladder's head.")]
        public float kerbClearance = 0.3f;

        /// <summary>How long a transition takes when there is no clip to time it by.</summary>
        private const float DefaultTransition = 0.6f;

        /// <summary>
        /// Where the entity must be when the top-enter clip ends, relative to the top of
        /// the climb line: up, and toward the wall. The user's Ladder_Climb_Down_Start is not
        /// an in-place clip - its pose carries the body down and out over the edge - so at
        /// its last frame the hips hang 0.90 m below and 0.91 m behind the entity, where the
        /// climb idle's sit 0.88 m above and 0.05 m behind it. For the hips not to jump when
        /// the idle takes over, the entity ends the clip that far up and in from where it
        /// will then be put, and is moved the rest of the way as the model fades the clip
        /// out. Measured 2026-10-02 in the game (the hips bone against the entity, in the
        /// ladder's frame, on the clip's last frame and in the settled climb idle, with the
        /// foot IK off - on, it shifted the hips and the numbers came out wrong); remeasure
        /// if the clip is re-authored. The hands and feet of the two poses are within a
        /// tenth of a metre of the same offsets, so the handover is all but seamless.
        /// </summary>
        [Tooltip("Where the entity stands when the top-enter clip ends, up from the top of the climb line, in metres. " +
                 "Measured off the clip: the clip-end hips against the climb idle's. Remeasure if the clip changes.")]
        public float topEnterPoseUp = 1.78f;

        [Tooltip("Where the entity stands when the top-enter clip ends, in toward the wall from the top of the climb line, in metres.")]
        public float topEnterPoseIn = 0.86f;

        private float _speedBeforeClimb = -1f;
        private bool _overridingSpeed;
        private bool _sheathedBeforeClimb;
        private bool _sheathedForClimb;

        private enum Path
        {
            /// <summary>A straight ease from one point to the other.</summary>
            Straight,
            /// <summary>From a deck down onto the rungs: up and out over the edge, then down.</summary>
            OverTheEdgeDown,
            /// <summary>From the rungs up onto a deck: up over the edge, then in and down.</summary>
            OverTheEdgeUp,
        }

        public override async UniTask PlayEnterLadderAnimation(LadderEntranceType entranceType)
        {
            Ladder ladder = ClimbingLadder;
            if (ladder == null)
                return;
            EnterExitState = EnterExitState.Enter;
            Vector3 from = Entity.EntityTransform.position;
            Vector3 to = ladder.ClosestPointOnLadderSegment(from, Entity.Movement.GetMovementBounds().extents.z, out _);
            // Face the rungs from the first frame, at once. The top-enter clip's pose starts
            // turned away from the wall and turns to it over its first half second, so the
            // entity's own turn must not show; the clip is cut in over a single frame (see
            // DemoAnimationSet.TopEnter) so the base pose, now facing the wall, shows for no
            // longer than that. The kit's movement turns a climber to the ladder whenever it
            // is asked to look anywhere, so the direction is nominal.
            Entity.SetLookRotation(Quaternion.LookRotation(-ladder.ForwardWithYAngleOffsets), true);
            if (IsServer && !_overridingSpeed)
            {
                _speedBeforeClimb = Entity.OverrideMoveSpeed;
                _overridingSpeed = true;
                Entity.OverrideMoveSpeed = climbSpeed;
            }
            // Both hands on the rungs: a drawn sword climbed with the character. The owner
            // sets the sheathed flag, as the kit's own SheathWhileUnderWater does.
            if (Entity.IsOwnerClient && !_sheathedForClimb)
            {
                _sheathedBeforeClimb = Entity.IsWeaponsSheathed;
                _sheathedForClimb = true;
                Entity.IsWeaponsSheathed = true;
            }
            float duration = 0f;
            if (Entity.Model is ILadderEnterExitModel model)
            {
                duration = model.GetEnterLadderAnimationDuration(entranceType);
                if (duration > 0f)
                    model.PlayEnterLadderAnimation(entranceType);
            }
            if (entranceType == LadderEntranceType.Top && duration > 0f)
            {
                // The clip's pose does the lowering; the entity only makes up the difference
                // between the pose's travel and the real drop, then slides the last bit while
                // the climb idle blends in.
                Vector3 poseEnd = to + Vector3.up * topEnterPoseUp - ladder.ForwardWithYAngleOffsets * topEnterPoseIn;
                await TopEnterTravel(from, poseEnd, to, duration);
                return;
            }
            await Travel(from, to, duration > 0f ? duration : DefaultTransition,
                entranceType == LadderEntranceType.Top ? Path.OverTheEdgeDown : Path.Straight);
        }

        public override async UniTask PlayExitLadderAnimation(LadderEntranceType entranceType)
        {
            Ladder ladder = ClimbingLadder;
            if (ladder == null)
                return;
            EnterExitState = EnterExitState.Exit;
            Vector3 from = Entity.EntityTransform.position;
            Vector3 to = from;
            Path path = Path.Straight;
            float duration = 0f;
            switch (entranceType)
            {
                case LadderEntranceType.Bottom:
                    to = ladder.bottomExitTransform.position;
                    break;
                case LadderEntranceType.Top:
                    to = ladder.topExitTransform.position;
                    path = Path.OverTheEdgeUp;
                    break;
            }
            if (entranceType != LadderEntranceType.Middle)
            {
                if (Entity.Model is ILadderEnterExitModel model)
                {
                    duration = model.GetExitLadderAnimationDuration(entranceType);
                    if (duration > 0f)
                        model.PlayExitLadderAnimation(entranceType);
                }
                if (duration <= 0f)
                    duration = DefaultTransition;
            }
            await Travel(from, to, duration, path);
            RestoreSpeed();
            RestoreSheathing();
        }

        /// <summary>
        /// Carries the character from one point to the other over the duration, and hands
        /// the kit the same point as both ends of its lerp each frame, then releases the
        /// state two frames later as the kit does.
        ///
        /// Time is accumulated frame by frame from <see cref="Time.unscaledDeltaTime"/>,
        /// the clock the model evaluates its playable graph with, so the path and the clip
        /// keep step.
        /// </summary>
        private async UniTask Travel(Vector3 from, Vector3 to, float duration, Path path)
        {
            EnterOrExitTime = Time.unscaledTime;
            EnterOrExitDuration = duration;
            float elapsed = 0f;
            bool first = true;
            while (this != null && Entity != null)
            {
                if (!first)
                    elapsed += Time.unscaledDeltaTime;
                first = false;
                float t = duration > 0f ? Mathf.Clamp01(elapsed / duration) : 1f;
                Hold(PointOnPath(from, to, t, path));
                if (t >= 1f)
                    break;
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
            await Release();
        }

        /// <summary>
        /// The top enter, whose clip carries the body in its pose. The entity eases to
        /// <paramref name="poseEnd"/> over the clip, and then, as the model fades the clip
        /// out and the climb idle in, slides to <paramref name="to"/> by the fade's own
        /// weight: the body is the weighted mix of the two poses, so an entity at the same
        /// mix of the two points keeps it still. Read off the model rather than timed, so
        /// a frame's disagreement between the two clocks cannot show as a jump.
        /// </summary>
        private async UniTask TopEnterTravel(Vector3 from, Vector3 poseEnd, Vector3 to, float duration)
        {
            EnterOrExitTime = Time.unscaledTime;
            EnterOrExitDuration = duration;
            float elapsed = 0f;
            bool first = true;
            bool clipFullyIn = false;
            while (this != null && Entity != null)
            {
                if (!first)
                    elapsed += Time.unscaledDeltaTime;
                first = false;
                float weight = ActionLayerWeight();
                if (weight >= 0.999f)
                    clipFullyIn = true;
                bool fadingOut = clipFullyIn && weight < 0.999f;
                if (fadingOut || weight < 0f && elapsed >= duration)
                {
                    Hold(Vector3.Lerp(to, poseEnd, Mathf.Clamp01(weight)));
                    if (weight <= 0f)
                        break;
                }
                else
                {
                    Hold(PointOnPath(from, poseEnd, duration > 0f ? Mathf.Clamp01(elapsed / duration) : 1f, Path.Straight));
                }
                // The fade is a tenth of a second; well past that the clip has been cut short
                // by something else (a hit, death) and waiting on it would hang the climb.
                if (elapsed > duration + 0.5f)
                    break;
                // After the entity's update, which is where the model evaluates its graph and
                // sets this frame's weight; read in Update the weight was last frame's, and
                // the body wobbled through the fade a frame behind the pose.
                await UniTask.Yield(PlayerLoopTiming.PreLateUpdate);
            }
            Hold(to);
            await Release();
        }

        /// <summary>
        /// The weight the model is currently giving the action layer the enter and exit
        /// clips play on (1 while a clip plays, ramping over the model's transition at
        /// either end; 0 once the model has taken a finished action off the mixer, whose
        /// empty input would otherwise read as 1), or -1 when the model is not the kit's
        /// playable one.
        /// </summary>
        private float ActionLayerWeight()
        {
            var model = Entity.Model as PlayableCharacterModel;
            if (model == null || model.Behaviour == null || !model.Behaviour.LayerMixer.IsValid())
                return -1f;
            AnimationLayerMixerPlayable mixer = model.Behaviour.LayerMixer;
            if (mixer.GetInputCount() <= AnimationPlayableBehaviour.ACTION_LAYER || !mixer.GetInput(AnimationPlayableBehaviour.ACTION_LAYER).IsValid())
                return 0f;
            return mixer.GetInputWeight(AnimationPlayableBehaviour.ACTION_LAYER);
        }

        /// <summary>Puts the character at the point and hands the kit the same point as both ends of its lerp.</summary>
        private void Hold(Vector3 point)
        {
            EnterOrExitFromPosition = point;
            EnterOrExitToPosition = point;
            if (DrivesPosition)
                Place(point);
        }

        private async UniTask Release()
        {
            await UniTask.DelayFrame(2);
            if (this != null)
                EnterExitState = EnterExitState.None;
        }

        private Vector3 PointOnPath(Vector3 from, Vector3 to, float t, Path path)
        {
            switch (path)
            {
                case Path.OverTheEdgeDown:
                {
                    // Rise and step out over the kerb first, then lower onto the rungs: by the
                    // time the feet are below the deck the body is outside the wall.
                    float peak = Mathf.Max(from.y, to.y) + kerbClearance;
                    float y = t < 0.3f
                        ? Mathf.Lerp(from.y, peak, Smooth(t / 0.3f))
                        : Mathf.Lerp(peak, to.y, Smooth((t - 0.3f) / 0.7f));
                    Vector3 flat = Vector3.Lerp(from, to, Smooth(Mathf.Clamp01(t / 0.6f)));
                    return new Vector3(flat.x, y, flat.z);
                }
                case Path.OverTheEdgeUp:
                {
                    // Pull up the face first, then over the kerb and in onto the boards.
                    float peak = Mathf.Max(from.y, to.y) + kerbClearance;
                    float y = t < 0.6f
                        ? Mathf.Lerp(from.y, peak, Smooth(t / 0.6f))
                        : Mathf.Lerp(peak, to.y, Smooth((t - 0.6f) / 0.4f));
                    Vector3 flat = Vector3.Lerp(from, to, Smooth(Mathf.Clamp01((t - 0.55f) / 0.45f)));
                    return new Vector3(flat.x, y, flat.z);
                }
                default:
                    return Vector3.Lerp(from, to, Smooth(t));
            }
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Whether this instance is the one moving the character: the kit simulates
        /// movement on the owner client, and on the server too when movement is
        /// server-authoritative. Everyone else interpolates what they are sent.
        /// </summary>
        private bool DrivesPosition
        {
            get
            {
                if (Entity.IsOwnerClientOrOwnedByServer)
                    return true;
                var movement = Entity.Movement as CharacterControllerEntityMovement;
                return IsServer && movement != null && movement.movementSecure == MovementSecure.ServerAuthoritative;
            }
        }

        /// <summary>Sets the position outright, past the character controller's collisions.</summary>
        private void Place(Vector3 position)
        {
            if (Entity.Movement is IBuiltInEntityMovement3D movement)
                movement.SetPosition(position);
            else
                Entity.EntityTransform.position = position;
        }

        private void RestoreSpeed()
        {
            if (!IsServer || !_overridingSpeed)
                return;
            _overridingSpeed = false;
            Entity.OverrideMoveSpeed = _speedBeforeClimb;
        }

        private void RestoreSheathing()
        {
            if (!Entity.IsOwnerClient || !_sheathedForClimb)
                return;
            _sheathedForClimb = false;
            Entity.IsWeaponsSheathed = _sheathedBeforeClimb;
        }

        private void OnEnable()
        {
            if (Entity != null)
                Entity.onUpdate += Tick;
        }

        private void OnDisable()
        {
            if (Entity != null)
                Entity.onUpdate -= Tick;
        }

        /// <summary>
        /// On the entity's own update. Off a ladder this is insurance for the speed override
        /// and the sheathing: a climb that ends any way but through the exit (the entity dies,
        /// or the ladder is destroyed under it) must not leave the character walking at
        /// climbing pace with its weapons put away. On one, the owner watches for the ends.
        /// </summary>
        private void Tick(BaseGameEntity entity)
        {
            if (ClimbingLadder != null)
            {
                if (EnterExitState == EnterExitState.None && Entity.IsOwnerClient)
                    LeaveAtTheEnds(ClimbingLadder);
                return;
            }
            if (_overridingSpeed && IsServer)
                RestoreSpeed();
            if (_sheathedForClimb && Entity.IsOwnerClient)
                RestoreSheathing();
        }

        /// <summary>How near an end counts as at it, in metres.</summary>
        private const float EndTolerance = 0.02f;

        /// <summary>
        /// Leaves the ladder at an end the climber is at and still climbing past. See the class
        /// summary for why the kit's own test cannot be relied on.
        /// </summary>
        private void LeaveAtTheEnds(Ladder ladder)
        {
            Vector3 bottom = ladder.bottomTransform.position;
            float length = Vector3.Distance(bottom, ladder.topTransform.position);
            float along = Vector3.Dot(Entity.EntityTransform.position - bottom, ladder.Up);
            MovementState state = Entity.MovementState;
            if (state.Has(MovementState.Up) && along >= length - EndTolerance)
                Leave(LadderEntranceType.Top);
            else if (state.Has(MovementState.Down) && along <= EndTolerance)
                Leave(LadderEntranceType.Bottom);
        }

        private void Leave(LadderEntranceType end)
        {
            // The same two steps the kit takes: flag the request so nothing else is sent
            // while it is in flight, then ask the server.
            EnterExitState = EnterExitState.ConfirmAwaiting;
            CallCmdExitLadder(end);
        }
    }
}
