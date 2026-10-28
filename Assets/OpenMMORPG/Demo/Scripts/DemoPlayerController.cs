using Insthync.CameraAndInput;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The kit's default controller with the mouse conventions most MMO players already
    /// know.
    ///
    /// The kit's controller already has most of the scheme: WASD moves relative to the
    /// camera and turns the character where it walks, Tab picks the nearest enemy, a
    /// chosen target is attacked and followed until it dies, the wheel zooms, and there
    /// is no crosshair. What it lacks is the mouse. In the kit only the right button
    /// orbits the camera and a click on the ground walks the character there. Here:
    ///
    /// - either button dragged orbits the camera, and the cursor is hidden while it does;
    /// - the right button dragged also turns the character to face where the camera looks;
    /// - a right click on an enemy targets it and starts attacking; a left click on
    ///   anything only targets it (the kit's second click attacks, which is left alone);
    /// - while a ground-targeted skill is being aimed, a right click (or Escape, see
    ///   <see cref="DemoEscapeKey"/>) takes the aim back, as in WoW;
    /// - a click on the ground does nothing - **unless the player turns Click To Move on in the
    ///   settings dialog**, which is off by default. Click-to-target and click-to-move are
    ///   separate features that happen to share a button: the first is a WoW convention, the
    ///   second is an option there that ships disabled, and the demo follows suit.
    ///
    /// Everything else is the kit's, so this is a thin layer over
    /// <see cref="PlayerCharacterController"/>: it reads the mouse after the base has,
    /// and only adds or undoes. Built by DemoControllerBuilder.
    /// </summary>
    public class DemoPlayerController : PlayerCharacterController
    {
        [Tooltip("How many colliders the enemy detector may consider. The kit's 128 is " +
                 "fewer than the village holds within targeting range, and monsters get " +
                 "crowded out of the results by scenery.")]
        public int enemyDetectorResults = 1024;

        [Header("Targeting")]
        [Tooltip("The cone in front of the character, in degrees, that Tab picks enemies from. " +
                 "Enemies outside it are never selected, however close. 360 is the kit's own " +
                 "behaviour: nearest first, in any direction.")]
        [Range(10f, 360f)]
        public float tabTargetFov = 120f;

        [Header("Mouse")]
        [Tooltip("How far the pointer travels, in pixels, before a held button is a drag rather than a click.")]
        public float dragThreshold = 6f;
        [Tooltip("Hide and lock the cursor while a button is dragging the camera.")]
        public bool lockCursorWhileDragging = true;
        [Tooltip("Rolling the wheel forward pulls the camera in, as in most games. Off is the kit's " +
                 "own way round, where forward pushes it out. The mouse wheel only: a touch " +
                 "screen's pinch keeps its own direction either way.")]
        public bool wheelForwardZoomsIn = true;

        private struct Button
        {
            public bool Down;
            public bool Drag;
            public bool OverUi;
            public Vector3 DownAt;
        }

        private Button _left;
        private Button _right;

        private DemoWeaponSheathing _sheathing;

        /// <summary>A skill pressed while the weapon was still being drawn: see <see cref="HoldForDraw"/>.</summary>
        private struct HeldHotkey
        {
            public HotkeyType Type;
            public string RelateId;
            public AimPosition Aim;
            public float At;
        }

        private HeldHotkey? _heldHotkey;

        /// <summary>
        /// Drops a target that has been destroyed, before the base controller can dereference it.
        ///
        /// **Works around an upstream bug**, from `Demo/` rather than by editing Core. The kit
        /// keeps the target as `ITargetableEntity`, and `SetTarget` guards it with `entity != null
        /// &amp;&amp; SelectedEntity != null`. On an **interface-typed** reference that is plain
        /// reference equality - Unity's destroyed-object `==` overload only applies to variables
        /// typed as `Object` - so a destroyed entity passes the guard and the next line throws
        /// `MissingReferenceException` off `EntityGameObject`
        /// (`PlayerCharacterController_Inputs.cs:438`).
        ///
        /// The kit has the right helper and uses it elsewhere in the same file:
        /// `IsNull()` does `obj is Object unityObj ? unityObj == null : obj == null`. It is simply
        /// not used on that line. Clearing the two properties here - both have `protected set` -
        /// means the guard sees a genuine null and short-circuits, which fixes every use of the
        /// stale reference that frame rather than just the one that happened to throw.
        ///
        /// **The corpses are what made this reachable.** Setting `monsterDeadDropItemMode` to
        /// corpse looting means every kill leaves an `ItemsContainerEntity` that you click, loot
        /// and then watch despawn - a targetable entity that is destroyed while still selected,
        /// which little else in the demo does.
        ///
        /// Worth reporting upstream: the fix there is one call to `IsNull()`.
        /// </summary>
        private void ForgetDestroyedTargets()
        {
            if (SelectedEntity.IsNull())
                SelectedEntity = null;
            if (TargetEntity.IsNull())
                TargetEntity = null;
        }


        /// <summary>
        /// Widens the enemy detector's result buffer, without which Tab targeting finds
        /// nothing wherever the scenery is dense.
        ///
        /// **Works around an upstream limit**, from `Demo/` rather than by editing Core.
        /// `NearbyEntityDetector` looks for enemies with
        ///
        /// <code>
        /// Physics.OverlapSphereNonAlloc(position, detectingRadius, colliders)
        /// </code>
        ///
        /// with **no layer mask**, into a buffer of `resultAllocSize` - 128 by default.
        /// The radius is the attack distance plus `distanceToLockActionTarget`, which for
        /// this demo is about 32m, and every collider in that sphere competes for the 128
        /// places: walls, fences, barrels, tree trunks, rocks, the terrain. The results
        /// come back in no particular order, so once the sphere holds more than 128
        /// things, whether any monster is among them is luck.
        ///
        /// Measured on the island: **374 colliders** within 32m of the village green,
        /// against a cap of 128. The bandit camp is 44 and the wood spawn 26, so it is the
        /// dressed areas that break, which is also where a player spends their time.
        ///
        /// The mask is what ought to be fixed, and it cannot be from out here - the
        /// overlap call is Core's. Enlarging the buffer costs an `ArrayPool` rent of this
        /// size and a walk of the hits on each detection tick, which is cheap next to the
        /// sphere query itself, and it is the only lever this side of the fence.
        ///
        /// Set in `Awake` because that is where the base class makes the detector.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            if (EnemyEntityDetector != null)
                EnemyEntityDetector.resultAllocSize = enemyDetectorResults;
            // Tab is handled here (see TabTarget); a key that never fires takes the kit's slot.
            _findEnemyInput = new InputStateManager(KeyCode.None);
            OrientWheelZoom();
        }

        public override void ManagedUpdate()
        {
            base.ManagedUpdate();
            // Every frame, not in UpdateInput: that one only runs while there is a character to
            // drive, and the prompt has to go away when there is not.
            UpdateLadderHint();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_ladderHint != null)
                Destroy(_ladderHint.gameObject);
        }

        /// <summary>
        /// Turns the scroll wheel round, so that rolling it forward pulls the camera in - the way
        /// nearly every game does it, and the way the user's hand expected (reported 2026-09-24).
        ///
        /// The kit's camera adds the "Mouse ScrollWheel" axis to its distance as it comes
        /// (`FollowCameraControls`: `zoomDistance += axis * zoomSpeed`), and the wheel reads
        /// positive rolled forward, so forward pushed the camera out. The component has no option
        /// for it; the sign of `zoomSpeed` is the only lever, and nothing else reads that field.
        ///
        /// **Only the wheel, which is why this is not in the camera prefab.** The same axis
        /// carries the kit's pinch on a touch screen (`MobilePinchArea`), which is already the
        /// right way round: closing the fingers reads positive, and closing them is zooming out.
        /// A negative speed in the prefab would have reversed the pinch to fix the wheel. The
        /// wheel is read only while desktop input is in use, so the sign is flipped only then.
        ///
        /// Set again every frame, so the inspector toggle works while playing.
        /// </summary>
        private void OrientWheelZoom()
        {
            var cameraController = CacheGameplayCameraController as DefaultGameplayCameraController;
            FollowCameraControls controls = cameraController != null ? cameraController.CameraControls : null;
            if (controls == null)
                return;
            float speed = Mathf.Abs(controls.zoomSpeed);
            controls.zoomSpeed = wheelForwardZoomsIn && !InputManager.IsUseMobileInput() ? -speed : speed;
        }

        private BaseSkill _lastQueuedSkill;
        /// <summary>The enemy an attacking skill was used on, to auto-attack once it is done.</summary>
        private BaseCharacterEntity _resumeTarget;

        /// <summary>Whether a skill was queued last frame, for <see cref="YieldChaseToNewDirection"/>.</summary>
        private bool _hadQueuedSkill;
        /// <summary>The movement keys held last frame while a skill was queued, each axis snapped to -1, 0 or 1.</summary>
        private Vector2 _moveInputWhileQueued;

        /// <summary>
        /// Lets a movement key take the character back from a queued skill's chase.
        ///
        /// **Works around an upstream ordering bug**, from `Demo/` rather than by editing Core.
        /// A skill used on an enemy out of reach is queued, and the kit walks the character in:
        /// `UpdateQueuedSkill` raises the follow flag and `UpdateFollowTarget` issues a
        /// point-click move toward the target. Both run every frame until the skill goes off.
        /// The kit's WASD step does lower the flag when a key is held - but it runs *before*
        /// those two in the same `UpdateInput`, so the flag is back up and the path re-issued
        /// before the frame is out, and a nav path always wins over key movement in the
        /// movement component. Measured 2026-10-02 with Cleave queued on a target 15m off:
        /// sixty frames of a held movement key and the character never left its line.
        /// Plain auto-attack has no such loop - its chase gives way to the first key - so it
        /// is only the skill chase that holds the player.
        ///
        /// A **new** press is what cancels it: a key that was already held when the skill
        /// was queued is the player running at the enemy and asking for the skill on
        /// arrival, and that is left alone - the kit keeps steering the character in, as it
        /// always has. Pressing a key from rest, re-pressing one, or changing direction while
        /// the chase is on is the player changing their mind, and the queued skill is
        /// dropped along with the follow and turn states that go with it, so the base's own
        /// WASD step then moves the character the way it would with nothing queued. The
        /// recorded skill and its target go too, or <see cref="ResumeAfterSkill"/> would start
        /// an auto-attack the player just walked away from.
        ///
        /// Nothing is touched once the skill has actually begun: a cast under way is the
        /// kit's business, and its queue is already empty by then.
        /// </summary>
        private void YieldChaseToNewDirection()
        {
            if (_queueUsingSkill.skill == null)
            {
                _hadQueuedSkill = false;
                return;
            }
            // Typing in chat is not steering.
            if (GenericUtils.IsFocusInputField())
                return;
            Vector2 input = SnappedMoveInput();
            if (!_hadQueuedSkill)
            {
                _hadQueuedSkill = true;
                _moveInputWhileQueued = input;
                return;
            }
            bool newPress = input.sqrMagnitude > 0f && input != _moveInputWhileQueued;
            _moveInputWhileQueued = input;
            if (!newPress || PlayingCharacterEntity.IsUsingSkill)
                return;
            ClearQueueUsingSkill();
            _lastQueuedSkill = null;
            _resumeTarget = null;
            _isFollowingTarget = false;
            _hadQueuedSkill = false;
            if (_targetActionType == TargetActionType.UseSkill)
                _targetActionType = TargetActionType.ClickActivate;
            if (_turnToTargetActionType == TargetActionType.UseSkill)
                _turnToTargetActionType = TargetActionType.None;
        }

        /// <summary>The movement axes as the kit reads them, snapped so a held key compares equal frame to frame.</summary>
        private static Vector2 SnappedMoveInput()
        {
            return new Vector2(
                Mathf.Round(Mathf.Clamp(InputManager.GetAxis("Horizontal", true), -1f, 1f)),
                Mathf.Round(Mathf.Clamp(InputManager.GetAxis("Vertical", true), -1f, 1f)));
        }

        /// <summary>
        /// Auto-attacks the target after an attacking skill, the way WoW does, and the way
        /// the kit means to.
        ///
        /// **Works around an upstream ordering bug**, from `Demo/` rather than by editing Core.
        /// A skill from the hotbar is queued; `UpdateQueuedSkill` targets the enemy with the
        /// action "use skill", and once the character is in range `UseSkillOrMoveToEntity`
        /// sets the *next* action to "attack" for an attacking skill - the kit's own intent,
        /// commented "Set next frame target action type". But `UpdateQueuedSkill` runs again
        /// every frame until the skill actually goes off, and each time it sets "use skill"
        /// back. When the skill fires the queue empties with the action still "use skill",
        /// and `TryGetUsingSkillEntity` refuses an empty queue - so nothing ever moves the
        /// action on, and the character stands there. Measured 2026-09-23 with a mage: two
        /// Arcane Bolts from the hotbar mid-fight, and no staff swing after either until the
        /// enemy was clicked again. It stops every class the same way - a warrior's Cleave
        /// ended his auto-attack too.
        ///
        /// **Setting the action back was not enough** (2026-09-24). It needs the kit's follow
        /// flag still up, and three paths take it down: a ground-aimed skill (Volley, Frost
        /// Nova, Meteor) goes through `UpdateQueuedSkill`'s aim branch, which lowers it
        /// outright; a skill on a target that was only selected never raised it; and neither
        /// did one the kit fired in the same frame it was queued, which this used to miss
        /// entirely because it only ever looked at the queue after the base had emptied it.
        /// So the skill and its target are now recorded as the hotkey is pressed
        /// (<see cref="UseHotkey"/>), and once it has *finished* - not merely been requested:
        /// a 1.4s Meteor would otherwise have the character setting off after the target
        /// mid-cast - an attacking skill starts auto-attack on that target outright, through
        /// <see cref="AttackTarget"/>, the same call a right click makes.
        ///
        /// Only on the enemy the player still has selected and alive: clicking the ground, or
        /// another target, while the skill was going off is the player changing their mind.
        /// A skill that was not an attack goes back to whatever the player was doing.
        /// </summary>
        private void ResumeAfterSkill()
        {
            if (_queueUsingSkill.skill != null)
            {
                RecordQueuedSkill();
                return;
            }
            if (_lastQueuedSkill == null)
                return;
            if (PlayingCharacterEntity.IsUsingSkill || PlayingCharacterEntity.IsPlayingAttackOrUseSkillAnimation())
                return;

            bool attack = _lastQueuedSkill.IsAttack;
            BaseCharacterEntity target = _resumeTarget;
            _lastQueuedSkill = null;
            _resumeTarget = null;
            if (attack && target != null && !target.IsDead() && SelectedGameEntity == target)
            {
                AttackTarget(target);
                return;
            }
            if (_targetActionType == TargetActionType.UseSkill)
                _targetActionType = attack ? TargetActionType.Attack : _previousTargetActionType;
        }

        /// <summary>Notes the queued skill, and the enemy it is aimed at if there is one.</summary>
        private void RecordQueuedSkill()
        {
            _lastQueuedSkill = _queueUsingSkill.skill;
            if (TryGetSelectedTargetAsAttackingEntity(out BaseCharacterEntity enemy) && !enemy.IsDead())
                _resumeTarget = enemy;
        }

        /// <summary>
        /// True while the character is auto-attacking: following a live target to hit it, or
        /// in the middle of a skill that will go back to doing so.
        /// </summary>
        public bool IsAutoAttacking
        {
            get
            {
                if (_lastQueuedSkill != null && _lastQueuedSkill.IsAttack && _resumeTarget != null)
                    return true;
                if (!_isFollowingTarget || _targetActionType != TargetActionType.Attack)
                    return false;
                var target = TargetGameEntity as BaseCharacterEntity;
                return target != null && !target.IsDead();
            }
        }

        /// <summary>
        /// The hotbar, with the Attack button (<see cref="DemoAutoAttackSkill"/>) taken out of
        /// the kit's skill path and every other skill recorded for <see cref="ResumeAfterSkill"/>.
        /// </summary>
        public override bool UseHotkey(HotkeyType type, string relateId, AimPosition aimPosition)
        {
            if (type == HotkeyType.Skill && IsAutoAttackSkill(relateId))
            {
                ToggleAutoAttack();
                return true;
            }
            if (type == HotkeyType.Skill && HoldForDraw(type, relateId, aimPosition))
                return true;
            bool used = base.UseHotkey(type, relateId, aimPosition);
            if (_queueUsingSkill.skill != null)
            {
                _resumeTarget = null;
                RecordQueuedSkill();
            }
            return used;
        }

        private static bool IsAutoAttackSkill(string relateId)
        {
            return GameInstance.Skills.TryGetValue(BaseGameData.MakeDataId(relateId), out BaseSkill skill)
                && skill is DemoAutoAttackSkill;
        }

        /// <summary>
        /// WoW's Attack button. Pressed while auto-attacking, it stops; otherwise it starts on
        /// the selected enemy, or - with no enemy selected - the nearest one in reach, the same
        /// pick the kit's own Attack key makes. With nobody in reach it does nothing.
        /// </summary>
        public void ToggleAutoAttack()
        {
            if (PlayingCharacterEntity == null || PlayingCharacterEntity.IsDead())
                return;
            if (IsAutoAttacking)
            {
                StopAutoAttack();
                return;
            }
            if (SelectedGameEntity is HarvestableEntity node && node.CurrentHp > 0)
            {
                AttackNode(node);
                return;
            }
            if (!TryGetSelectedTargetAsAttackingEntity(out BaseCharacterEntity enemy) || enemy.IsDead())
            {
                enemy = PlayingCharacterEntity.FindNearestAliveEntity<BaseCharacterEntity>(
                    PlayingCharacterEntity.GetAttackDistance(false) + distanceToLockActionTarget,
                    false, true, false,
                    CurrentGameInstance.playerLayer.Mask | CurrentGameInstance.monsterLayer.Mask);
            }
            if (enemy != null)
                AttackTarget(enemy);
        }

        /// <summary>
        /// Stops swinging and chasing, and keeps the target selected - WoW's Attack toggled off.
        /// A skill already under way still goes off; it just no longer leads back into attacking.
        /// </summary>
        public void StopAutoAttack()
        {
            _resumeTarget = null;
            _isFollowingTarget = false;
            if (_targetActionType == TargetActionType.Attack)
                _targetActionType = TargetActionType.ClickActivate;
            if (_turnToTargetActionType == TargetActionType.Attack)
                _turnToTargetActionType = TargetActionType.None;
            if (_moveDirection.sqrMagnitude <= 0f && !_destination.HasValue)
                PlayingCharacterEntity.StopMove();
        }

        /// <summary>
        /// Finishes the kit's "turn to the target, then act" step when the height of the target
        /// is the only thing stopping it.
        ///
        /// **Works around an upstream bug**, from `Demo/` rather than by editing Core.
        /// `UpdateTurnToTargetToDoAction` turns the character toward the target and only fires
        /// the queued attack or skill once it faces it within 30 degrees - but it measures that
        /// against the full 3D direction, height included, while a character only ever turns
        /// about its vertical axis. A target more than 30 degrees above or below can never be
        /// faced, and the action waits forever: Meteor aimed at the top of the boulder by the
        /// western beach (5.5m across, 6.9m up, 52 degrees) sat queued and never cast; a wolf
        /// pushed up against the caster on a slope (0.6m away, 0.4m higher) did the same to
        /// Frost Nova. Found 2026-09-23.
        ///
        /// That method is not virtual, and correcting the point it turns toward beforehand does
        /// not hold: for a ground-aimed skill `UpdateQueuedSkill` writes the point again every
        /// frame, just before the check. So this runs after the base: when the character faces
        /// the target on the level (within the same 30 degrees) and the kit's own measure,
        /// height included, is what refused, it does the kit's step exactly as the kit would -
        /// the same request, the same hook, the same next state. When the kit acted, or the
        /// character is still turning, it does nothing.
        ///
        /// Worth reporting upstream: the fix there is to zero the look direction's `y` in that
        /// one method.
        /// </summary>
        private void FinishTurnOnTheLevel()
        {
            TargetActionType pending = _turnToTargetActionType;
            if (pending == TargetActionType.None ||
                GameInstance.Singleton == null || GameInstance.Singleton.DimensionType == DimensionType.Dimension2D)
                return;
            Vector3 target;
            if (_turnToTargetPosition.HasValue)
                target = _turnToTargetPosition.Value;
            else if (TargetGameEntity != null)
                target = TargetGameEntity.EntityTransform.position;
            else
                return;

            Vector3 toTarget = target - EntityTransform.position;
            Vector3 forward = EntityTransform.forward;
            if (toTarget.sqrMagnitude < 0.0001f)
                return;
            // The kit's measure: forward against the direction with its height left in.
            if (Quaternion.Angle(Quaternion.LookRotation(forward), Quaternion.LookRotation(toTarget.normalized)) <= TurnedAngle)
                return;
            // Ours: the same, on the level. Straight overhead there is no turning to do at all.
            Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
            Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude > 0.0001f && flatForward.sqrMagnitude > 0.0001f && Vector3.Angle(flatForward, flat) > TurnedAngle)
                return;

            switch (pending)
            {
                case TargetActionType.Attack:
                    RequestAttack();
                    OnAttackOnEntity();
                    _turnToTargetActionType = TargetActionType.ActionRequested;
                    break;
                case TargetActionType.UseSkill:
                    bool turnWhileCasting = _queueUsingSkill.skill != null && _queueUsingSkill.skill.TurnToTargetWhileCasting;
                    RequestUsePendingSkill();
                    OnUseSkillOnEntity();
                    _turnToTargetActionType = turnWhileCasting
                        ? TargetActionType.ActionRequested
                        : TargetActionType.ActionRequestedWithoutAnimationAwaiting;
                    break;
                case TargetActionType.ActionRequested:
                    if (!PlayingCharacterEntity.IsPlayingAttackOrUseSkillAnimation())
                    {
                        _turnToTargetActionType = TargetActionType.None;
                        _turnToTargetPosition = null;
                    }
                    break;
                case TargetActionType.ActionRequestedWithoutAnimationAwaiting:
                    _turnToTargetActionType = TargetActionType.None;
                    _turnToTargetPosition = null;
                    break;
            }
        }

        /// <summary>The kit's "close enough to face" limit, in degrees.</summary>
        private const float TurnedAngle = 30f;

        /// <summary>
        /// Casts an area skill that can only land at the caster's own feet the moment its key is
        /// pressed, instead of waiting for a click that decides nothing.
        ///
        /// The kit treats every area skill as ground-targeted: `BaseAreaSkill.HasCustomAimControls`
        /// is true for all of them, so the hotbar starts aiming and waits for a left click. A skill
        /// with no reach - Frost Nova, whose `castDistance` is 0 - is clamped back onto the caster
        /// wherever the cursor is, so for it that click chose nothing. What the player saw was a key
        /// that did nothing visible (the skill has no aiming circle, there being nowhere to aim),
        /// then a click on the ground, then a nova at their own feet wherever they had clicked.
        ///
        /// So the kit's aiming is ended as a cancel, which puts it away without using the skill,
        /// and the skill is used straight away through the same hotkey - so the bar's own "used"
        /// event still fires - aimed at the only place it can land. It needs no setting: any area
        /// skill with no reach at the character's level fires on the key, and every other one
        /// still aims. The same holds for a cast from the skills window, which aims through the
        /// kit's hidden hotkey.
        ///
        /// Accepted under the same conditions as the kit's own confirming click
        /// (`UICharacterHotkeys.UpdateHotkeyInputs`), and run before the base's input, so the kit
        /// queues and fires the skill in this same frame.
        /// </summary>
        private void CastSelfCentredWithoutAiming()
        {
            UICharacterHotkey aiming = UICharacterHotkeys.UsingHotkey;
            if (aiming == null || PlayingCharacterEntity == null || PlayingCharacterEntity.IsDead())
                return;
            if (UIBlockController.IsBlockController() || UIBlockActionController.IsBlockController())
                return;
            if (!aiming.GetAssignedSkill(out BaseSkill skill, out int level) || !(skill is BaseAreaSkill area) ||
                area.castDistance.GetAmount(level) > 0f)
                return;
            UICharacterHotkeys.FinishHotkeyAimControls(true);
            aiming.Use(AimPosition.CreatePosition(AtOwnFeet(area)));
        }

        /// <summary>
        /// Where a skill that lands on its caster is aimed: the ground under the character, a
        /// hand's breadth along the way it already faces.
        ///
        /// **Not the feet exactly, or the character turns to face north.** Before firing, the kit
        /// turns a character to its aim point (`UpdateTurnToTargetToDoAction`), and a point at the
        /// feet leaves no direction to turn to: the ground found under a character sits a couple
        /// of centimetres below its root, so the look is straight down, and the rotation looking
        /// straight down has a yaw of zero. Every Frost Nova swung the mage round to face world
        /// north before it went off - measured 63 to 1 degrees in the 0.4s after the click
        /// (2026-09-24). A point just ahead gives the kit the facing the character already has, so
        /// it fires at once without turning. 0.2m is nothing against a 4.5m nova.
        /// </summary>
        private Vector3 AtOwnFeet(BaseAreaSkill area)
        {
            Vector3 feet = PhysicUtils.FindGroundedPosition(EntityTransform.position, _groundHits, GroundSearchDistance,
                                                            area.GroundDetectionLayerMask, EntityTransform);
            Vector3 facing = EntityTransform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.0001f)
                facing = Vector3.forward;
            return feet + facing.normalized * SelfAimLead;
        }

        /// <summary>How far ahead of the feet a self-centred skill is aimed. See <see cref="AtOwnFeet"/>.</summary>
        private const float SelfAimLead = 0.2f;

        /// <summary>
        /// Takes back a skill that is being aimed, without using it: WoW's right click or
        /// Escape while the targeting circle is up. True if there was one to take back.
        ///
        /// The kit's own way out was only to press the skill's key a second time. This ends the
        /// aiming exactly as that does - `FinishHotkeyAimControls` as a cancel, which puts the
        /// circle away and uses nothing. The right click in <see cref="UpdateInput"/> and
        /// <see cref="DemoEscapeKey"/> both come through here.
        /// </summary>
        public static bool CancelAiming()
        {
            if (UICharacterHotkeys.UsingHotkey == null)
                return false;
            UICharacterHotkeys.FinishHotkeyAimControls(true);
            return true;
        }

        /// <summary>The kit's own, from `DefaultAreaSkillAimController.GROUND_DETECTION_DISTANCE`.</summary>
        private const float GroundSearchDistance = DefaultAreaSkillAimController.GROUND_DETECTION_DISTANCE;

        private readonly RaycastHit[] _groundHits = new RaycastHit[32];

        public override void UpdateInput()
        {
            ForgetDestroyedTargets();
            OrientWheelZoom();
            CastSelfCentredWithoutAiming();
            YieldChaseToNewDirection();
            DrawForAttackKey();
            base.UpdateInput();
            if (PlayingCharacterEntity == null)
                return;
            FinishTurnOnTheLevel();
            ResumeAfterSkill();
            ReleaseHeldHotkey();
            DrawForChase();
            NoteCombat();

            bool focus = GenericUtils.IsFocusInputField();
            bool overUi = UISceneGameplay.IsPointerOverUIObject();
            UpdateCrouch(!focus && !IsControllerBlocked());
            Track(ref _left, 0, overUi);
            Track(ref _right, 1, overUi);

            bool leftOrbit = _left.Down && _left.Drag && !_left.OverUi;
            bool rightOrbit = _right.Down && !_right.OverUi;
            if (!focus && (leftOrbit || rightOrbit))
                CacheGameplayCameraController.UpdateRotation = true;

            // The right button steers: the character turns with the camera, unless it is
            // mid-swing and the kit is aiming it.
            var cameraController = CacheGameplayCameraController as DefaultGameplayCameraController;
            if (rightOrbit && _right.Drag && !focus && cameraController != null &&
                !PlayingCharacterEntity.IsPlayingAttackOrUseSkillAnimation())
            {
                PlayingCharacterEntity.SetLookRotation(Quaternion.Euler(0f, cameraController.CameraControls.yRotation, 0f), false);
            }

            if (lockCursorWhileDragging)
            {
                bool dragging = leftOrbit || (rightOrbit && _right.Drag);
                Cursor.lockState = dragging ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !dragging;
            }

            if (focus)
                return;

            UpdateStrafe(rightOrbit && _right.Drag);
            UpdateClimbInput();

            if (InputManager.GetButtonDown(DemoWeaponSheathing.KeyName) && !IsControllerBlocked())
                ToggleSheathed();

            if (InputManager.GetButtonDown("FindEnemy") && !IsControllerBlocked() && !PlayingCharacterEntity.IsDead())
                TabTarget();

            // A right click, not a drag: while a skill is being aimed it takes the aim back;
            // otherwise, on an enemy, it targets it and attacks. Never both - the click that
            // cancels an aim is spent on it, so it does not also start a fight with whatever
            // stood under the circle. A right drag still only turns the camera, so the player
            // can look around for a spot without losing the aim.
            if (InputManager.GetMouseButtonUp(1) && !_right.Drag && !_right.OverUi)
            {
                if (!CancelAiming())
                {
                    BaseCharacterEntity enemy = EnemyUnderPointer();
                    if (enemy != null)
                        AttackTarget(enemy);
                    else
                        AttackNode(EntityUnderPointer() as HarvestableEntity);
                }
            }

            // A left click on nothing: the base has just told the character to walk there. Take
            // that back unless the player asked for it - the ground is not a destination by
            // default. Note this *undoes* the kit rather than preventing it, so turning the
            // setting on needs nothing else: leaving the base's order alone is the whole feature.
            if (!DemoSettings.ClickToMove
                && InputManager.GetMouseButtonUp(0) && !_left.Drag && !_left.OverUi && EntityUnderPointer() == null)
            {
                _destination = null;
                PlayingCharacterEntity.StopMove();
            }
        }

        /// <summary>
        /// Plays the sideways and backward strides when the character is moving somewhere
        /// other than where it is facing.
        ///
        /// The Quaternius library ships a full eight-way jog and `DemoAnimationSet` already
        /// wires it, but the kit's controller can never reach any of it: whenever WASD is
        /// pressed it turns the body to face the way it is going and then reports the state
        /// as `MovementState.Forward`, flat, with no regard for the facing it just set. Face
        /// and travel therefore always agree, so the forward stride is the only one that can
        /// ever be chosen — and on the occasions when the kit itself decouples the two, the
        /// character slides sideways still playing it.
        ///
        /// There are three of those occasions, and this covers all of them rather than only
        /// the new one:
        ///
        /// - the right button is steering, the demo's own gesture: the body holds the
        ///   camera's heading (see <see cref="UpdateInput"/>) while WASD moves it about
        ///   freely, which is the strafe every MMO player expects from that grip;
        /// - the kit is turning the character to a target, so it circles an enemy;
        /// - the character is mid-swing, when the kit pins the facing to the blow.
        ///
        /// In each the base has already decided the facing and merely mislabels the
        /// movement, so the fix is to relabel it — the same call, re-issued in the same
        /// frame with the direction measured against where the body actually points.
        /// <see cref="GameplayUtils.GetMovementStateByDirection"/> is the kit's own
        /// classifier, the one the shooter controller uses, so the eight arcs land exactly
        /// where the animation set expects them.
        ///
        /// Measured against `MovementTransform.forward` rather than the heading being turned
        /// towards, because the kit turns smoothly (`turnSmoothSpeed`): during the swing to a
        /// new heading the body genuinely is side-on to its travel, and taking the live
        /// facing lets the stride follow it round instead of snapping at the end.
        ///
        /// Where the base's own rule applies — facing follows travel — this leaves its
        /// `Forward` alone, so ordinary running is untouched.
        /// </summary>
        private void UpdateStrafe(bool steering)
        {
            if (PlayingCharacterEntity == null || _moveDirection.sqrMagnitude <= 0.0001f)
                return;
            // On a ladder the only directions are up and down (see UpdateClimbInput); the
            // ground state re-issued here used to wipe them whenever the right button was held.
            if (IsClimbing)
                return;
            // A horse has no sideways stride. Steered, it would slide across the ground still
            // playing the gallop, so the mount gets its own rule (see DriveMountAlongItsFacing).
            // Unsteered, the kit's own rule - the body turns to face where it is going - leaves
            // nothing to relabel.
            if (IsDrivingMount)
            {
                if (steering)
                    DriveMountAlongItsFacing();
                else
                    KeepMountOutOfWater(_moveDirection);
                return;
            }

            bool facingFollowsTravel = !steering
                && _turnToTargetActionType == TargetActionType.None
                && !PlayingCharacterEntity.IsPlayingAttackOrUseSkillAnimation();
            if (facingFollowsTravel)
                return;

            MovementState state = GameplayUtils.GetMovementStateByDirection(_moveDirection, MovementTransform.forward);
            AddNonDirectionalBits(ref state);
            PlayingCharacterEntity.KeyMovement(_moveDirection, state);
        }

        /// <summary>
        /// The jump, dash and swim bits are not directional and would be dropped by
        /// re-issuing a movement. Read the same buttons the base reads, in the same order:
        /// within a frame `GetButtonDown` answers the same both times, so this cannot swallow
        /// a press or invent one.
        /// </summary>
        private void AddNonDirectionalBits(ref MovementState state)
        {
            if (PlayingCharacterEntity.MovementState.Has(MovementState.IsUnderWater))
            {
                if (InputManager.GetButton("SwimUp"))
                    state |= MovementState.Up;
                else if (InputManager.GetButton("SwimDown"))
                    state |= MovementState.Down;
            }
            else if (PlayingCharacterEntity.MovementState.Has(MovementState.IsGrounded))
            {
                if (InputManager.GetButtonDown("Jump"))
                    state |= MovementState.IsJump;
                else if (InputManager.GetButtonDown("Dash"))
                    state |= MovementState.IsDash;
            }
        }

        #region Crouch

        /// <summary>Whether the player has crouched and not yet stood up.</summary>
        private bool _crouching;

        /// <summary>
        /// Crouching on the Crouch key (X), as a toggle.
        ///
        /// The kit has all of crouching except the key. The movement slows a crouching
        /// character (the gameplay rule's crouch rate, then a rate per direction), the animation
        /// set plays the crouch idle and the eight-way crouch walk, and the state travels to the
        /// other players with the rest of the movement. But only the shooter controller ever asks
        /// for it: the default controller this one extends sets sprint, walk, sit or nothing at
        /// the end of every `UpdateInput`, never crouch, so X - which GameInstance binds to
        /// "Crouch" - did nothing.
        ///
        /// The toggle is kept here and written over the base's choice every frame, after the
        /// base has made it. The character stands up on:
        ///
        /// - X again;
        /// - any other gait the player asks for - sprint, the walk toggle, sitting. The base
        ///   raises its own flag for those, and this clears all three while crouched, so a
        ///   raised one is a fresh request;
        /// - jump and dash, which spring out of the crouch and land standing;
        /// - water, a ladder, a saddle or death, which the kit would refuse a crouch in anyway.
        ///
        /// A fall from a ledge keeps it. The movement drops crouch while the character is in the
        /// air and takes it up again on landing, and the grounded flag flickers on rough ground,
        /// so standing up whenever it went false would stand the player up on every bump.
        ///
        /// Standing asks the kit first (`AllowToStand`), as the shooter controller does, so that
        /// if the player prefabs are ever given a smaller crouching capsule, a player under a low
        /// roof stays down and is told why. Today the capsule does not change and the answer is
        /// always yes.
        /// </summary>
        private void UpdateCrouch(bool readKeys)
        {
            BasePlayerCharacterEntity entity = PlayingCharacterEntity;
            if (_crouching && MustStand(entity))
                _crouching = false;

            if (readKeys)
            {
                if (InputManager.GetButtonDown("Crouch"))
                {
                    if (_crouching)
                        TryStand(entity);
                    else if (!MustStand(entity))
                        TryCrouch(entity);
                }
                else if (_crouching && (_isSprinting || _isWalking || _isSitting ||
                         InputManager.GetButtonDown("Jump") || InputManager.GetButtonDown("Dash")))
                {
                    TryStand(entity);
                }
            }

            if (_crouching)
            {
                _isSprinting = _isWalking = _isSitting = false;
                entity.SetExtraMovementState(ExtraMovementState.IsCrouching);
            }
        }

        private bool MustStand(BasePlayerCharacterEntity entity)
        {
            return entity.IsDead() || IsClimbing || entity.PassengingVehicleEntity != null
                || entity.MovementState.Has(MovementState.IsUnderWater);
        }

        private void TryCrouch(BasePlayerCharacterEntity entity)
        {
            if (entity.CanCrouch() && entity.AllowToCrouch())
                _crouching = true;
            else
                ClientGenericActions.ClientReceiveGameMessage(UITextKeys.UI_ERROR_UNABLE_TO_CROUCH);
        }

        private void TryStand(BasePlayerCharacterEntity entity)
        {
            if (entity.AllowToStand())
                _crouching = false;
            else
                ClientGenericActions.ClientReceiveGameMessage(UITextKeys.UI_ERROR_UNABLE_TO_STAND);
        }

        #endregion

        #region Mounts

        /// <summary>
        /// How much of the push must lie along the horse before it counts as forward or back.
        /// Above the 0.0 of a pure sideways key, below the 0.7 of a diagonal, so W+A still
        /// walks ahead and A on its own does nothing.
        /// </summary>
        private const float MountAlongThreshold = 0.3f;

        private bool IsDrivingMount
        {
            get
            {
                IVehicleEntity vehicle = PlayingCharacterEntity != null ? PlayingCharacterEntity.PassengingVehicleEntity : null;
                return vehicle != null && vehicle.IsDriver(PlayingCharacterEntity.PassengingVehicleSeatIndex);
            }
        }

        /// <summary>
        /// Holds a steered horse to its own axis: the keys can send it forward or back along
        /// the way it faces and nowhere else.
        ///
        /// Steering pins the body to the camera's heading (see <see cref="UpdateInput"/>) while
        /// WASD is camera-relative, so on foot A and D slide the character sideways. That is the
        /// demo's strafe and the horse inherited it - a 2.4 m animal skating across the grass in
        /// its gallop, or galloping backwards at three quarters speed. Here the push is measured
        /// against the horse's facing: ahead of it is forward, behind it is back, and what is
        /// left over (a sideways key) is dropped. Back is slow (the mount's own reverse rate) and
        /// the animator plays the walk in reverse, see <see cref="MountAnimator"/>.
        /// </summary>
        private void DriveMountAlongItsFacing()
        {
            Vector3 facing = MovementTransform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.0001f)
                return;
            facing.Normalize();

            float along = Vector3.Dot(_moveDirection, facing);
            Vector3 direction = Vector3.zero;
            MovementState state = MovementState.None;
            if (along > MountAlongThreshold)
            {
                direction = facing;
                state = MovementState.Forward;
            }
            else if (along < -MountAlongThreshold)
            {
                direction = -facing;
                state = MovementState.Backward;
            }
            if (direction != Vector3.zero && MountWouldEnterWater(direction))
            {
                direction = Vector3.zero;
                state = MovementState.None;
            }
            AddNonDirectionalBits(ref state);
            PlayingCharacterEntity.KeyMovement(direction, state);
        }

        /// <summary>
        /// Takes back the base's movement when it would carry the mount into the water.
        /// The base has already turned the horse to face where it was sent, so it stands at
        /// the shore looking at the sea rather than refusing to turn.
        /// </summary>
        private void KeepMountOutOfWater(Vector3 direction)
        {
            if (MountWouldEnterWater(direction))
                PlayingCharacterEntity.KeyMovement(Vector3.zero, MovementState.None);
        }

        /// <summary>The water a horse will wade into, in metres: a few steps into the shallows, not the sea.</summary>
        private const float MountWadeDepth = 0.3f;

        /// <summary>
        /// How far ahead of its centre the horse looks for water. Its chest is about 1.2 m
        /// out, so this stops it with the forehooves a little short of the water's edge.
        /// </summary>
        private const float MountWaterProbeReach = 1.6f;

        private const float MountProbeLift = 3f;
        private const float MountProbeLength = 80f;

        /// <summary>
        /// True if a step in <paramref name="direction"/> would take the mount into water
        /// deeper than <see cref="MountWadeDepth"/>, and deeper than where it already stands -
        /// a horse already in the water (mounted there, or knocked in) is always allowed to
        /// walk out, in any direction that is not deeper.
        /// </summary>
        private bool MountWouldEnterWater(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return false;
            Vector3 here = MovementTransform.position;
            float ahead = WaterDepthAt(here + direction.normalized * MountWaterProbeReach);
            return ahead > MountWadeDepth && ahead > WaterDepthAt(here) + 0.01f;
        }

        /// <summary>
        /// Depth of the water over the ground at a point, 0 where there is none. The sea is a
        /// trigger volume on the Water layer whose top is the surface, so a ray down from above
        /// finds the surface and a second, ignoring triggers, finds the bed beneath it.
        /// </summary>
        private static float WaterDepthAt(Vector3 point)
        {
            Vector3 from = point + Vector3.up * MountProbeLift;
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit water, MountProbeLength,
                    1 << PhysicLayers.Water, QueryTriggerInteraction.Collide))
                return 0f;
            float bed = Physics.Raycast(from, Vector3.down, out RaycastHit ground, MountProbeLength,
                    GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask(), QueryTriggerInteraction.Ignore)
                ? ground.point.y
                : water.point.y - MountProbeLength;
            return Mathf.Max(0f, water.point.y - bed);
        }

        #endregion

        #region Ladders

        /// <summary>
        /// The cone, in degrees, about the direction to the ladder's end within which pushing
        /// toward it from inside an entrance gets on. The kit's own test is 15 degrees of the
        /// actual travel direction, with keyboard travel quantised to eight directions: the
        /// player had to line the camera up on the rungs before W did anything, and until
        /// then walked into the wall beside them.
        /// </summary>
        private const float EnterCone = 60f;
        private const float AxisDeadZone = 0.1f;
        /// <summary>
        /// How long a request to get on may wait for the server before it is given up, so a
        /// refusal (the kit sends none) cannot leave the character unable to climb for good.
        /// </summary>
        private const float ConfirmTimeout = 1.5f;

        private bool _wasClimbing;
        private LadderEntranceType _climbEntryEnd;
        private bool _climbKeyReleased = true;
        private float _confirmSince = -1f;

        private bool IsClimbing
        {
            get
            {
                CharacterLadderComponent climb = PlayingCharacterEntity != null ? PlayingCharacterEntity.LadderComponent : null;
                return climb != null && climb.ClimbingLadder != null;
            }
        }

        /// <summary>
        /// Ladder controls, after the base has had its say: W climbs up and S climbs down,
        /// whichever way the camera looks, and Space lets go.
        ///
        /// The kit reads the climb off the camera-relative travel direction: pushing toward
        /// the wall is up and away from it is down. Face the ladder and it agrees with this;
        /// swing the camera side-on and both keys read as the same direction, and at the top
        /// the natural key to climb down with (S) was in fact up, which put the player straight
        /// back onto the deck. Up is up.
        ///
        /// Two guards. The end the character got on at is held shut until the climb key has been
        /// let go once, because the key that got them on - W from a deck, S from the ground, for
        /// a player who has not read the prompt - would otherwise take them straight off again
        /// the same frame. And a request to get on that the server never answers is dropped
        /// after a moment; the kit's flag for it has no timeout.
        /// </summary>
        private void UpdateClimbInput()
        {
            CharacterLadderComponent climb = PlayingCharacterEntity.LadderComponent;
            if (climb == null)
                return;
            bool raw = !GameInstance.IsMobileTestInEditor() && !Application.isMobilePlatform
                && !GameInstance.IsConsoleTestInEditor() && !Application.isConsolePlatform;
            float vertical = InputManager.GetAxis("Vertical", raw);
            bool climbing = climb.ClimbingLadder != null;
            if (climbing && !_wasClimbing)
            {
                Ladder ladder = climb.ClimbingLadder;
                Vector3 at = PlayingCharacterEntity.EntityTransform.position;
                _climbEntryEnd = (at - ladder.topTransform.position).sqrMagnitude < (at - ladder.bottomTransform.position).sqrMagnitude
                    ? LadderEntranceType.Top : LadderEntranceType.Bottom;
                _climbKeyReleased = false;
            }
            _wasClimbing = climbing;
            if (Mathf.Abs(vertical) < AxisDeadZone)
                _climbKeyReleased = true;

            if (!climbing)
            {
                DropStaleConfirm(climb);
                TryEnterLadder(climb);
                return;
            }

            if (climb.EnterExitState != EnterExitState.None)
            {
                PlayingCharacterEntity.KeyMovement(Vector3.zero, MovementState.None);
                return;
            }

            if (InputManager.GetButtonDown("Jump"))
            {
                climb.EnterExitState = EnterExitState.ConfirmAwaiting;
                climb.CallCmdExitLadder(LadderEntranceType.Middle);
                PlayingCharacterEntity.KeyMovement(Vector3.zero, MovementState.None);
                return;
            }

            MovementState state = MovementState.None;
            if (vertical > AxisDeadZone)
                state = MovementState.Up;
            else if (vertical < -AxisDeadZone)
                state = MovementState.Down;
            bool towardEntryEnd = (state == MovementState.Up && _climbEntryEnd == LadderEntranceType.Top)
                || (state == MovementState.Down && _climbEntryEnd == LadderEntranceType.Bottom);
            if (!_climbKeyReleased && towardEntryEnd)
                state = MovementState.None;
            PlayingCharacterEntity.KeyMovement(Vector3.zero, state);
        }

        /// <summary>
        /// Gets on when the character is inside an entrance and pushing broadly toward the
        /// ladder. The kit's own narrower test still runs in the movement update and may get
        /// there first; both set the awaiting flag before asking, so only one asks.
        /// </summary>
        private void TryEnterLadder(CharacterLadderComponent climb)
        {
            if (climb.EnterExitState != EnterExitState.None || !_climbKeyReleased)
                return;
            LadderEntrance entrance = climb.TriggeredLadderEntry;
            if (entrance == null || entrance.TipTransform == null || _moveDirection.sqrMagnitude <= 0.0001f)
                return;
            if (IsControllerBlocked() || PlayingCharacterEntity.IsDead())
                return;
            Vector3 toTip = entrance.TipTransform.position - PlayingCharacterEntity.EntityTransform.position;
            toTip.y = 0f;
            Vector3 travel = _moveDirection;
            travel.y = 0f;
            if (toTip.sqrMagnitude <= 0.0001f || Vector3.Angle(travel, toTip) > EnterCone)
                return;
            climb.EnterExitState = EnterExitState.ConfirmAwaiting;
            climb.CallCmdEnterLadder();
            _confirmSince = Time.unscaledTime;
        }

        private void DropStaleConfirm(CharacterLadderComponent climb)
        {
            if (climb.EnterExitState != EnterExitState.ConfirmAwaiting)
            {
                _confirmSince = -1f;
                return;
            }
            if (_confirmSince < 0f)
                _confirmSince = Time.unscaledTime;
            else if (Time.unscaledTime - _confirmSince > ConfirmTimeout)
            {
                climb.EnterExitState = EnterExitState.None;
                _confirmSince = -1f;
            }
        }

        private const string LadderHintAtLadder = "Walk into the ladder to climb";
        private const string LadderHintClimbing = "W  climb up      S  climb down      Space  let go";
        /// <summary>How long the "at a ladder" prompt outlives the trigger's last report.</summary>
        private const float LadderHintHold = 0.25f;

        private UnityEngine.UI.Text _ladderHint;
        private float _lastAtLadder = -10f;

        /// <summary>
        /// A line of text above the hotbar that says what the keys do at a ladder: how to get
        /// on when standing at one, and up, down and let go while climbing. The kit gives no
        /// prompt at all, and a player who has just been turned to face a wall has no way to
        /// know that it is W and S from here.
        ///
        /// Lives with the controller, so no HUD prefab needs rebuilding: the label is made
        /// under the gameplay canvas on first use. (It was a component of its own, added to
        /// the controller at runtime, with an Update of its own.) The "at a ladder" prompt is
        /// held for a moment after the entrance trigger last reported the character, because
        /// the kit forgets the trigger every frame and the physics step that refreshes it does
        /// not run every frame.
        /// </summary>
        private void UpdateLadderHint()
        {
            BasePlayerCharacterEntity player = PlayingCharacterEntity;
            CharacterLadderComponent climb = player != null ? player.LadderComponent : null;
            string text = string.Empty;
            if (climb != null && !player.IsDead())
            {
                if (climb.ClimbingLadder != null)
                {
                    if (climb.EnterExitState == EnterExitState.None)
                        text = LadderHintClimbing;
                }
                else
                {
                    if (climb.TriggeredLadderEntry != null && climb.EnterExitState == EnterExitState.None)
                        _lastAtLadder = Time.unscaledTime;
                    if (Time.unscaledTime - _lastAtLadder < LadderHintHold)
                        text = LadderHintAtLadder;
                }
            }
            ShowLadderHint(text);
        }

        private void ShowLadderHint(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_ladderHint != null && _ladderHint.gameObject.activeSelf)
                    _ladderHint.gameObject.SetActive(false);
                return;
            }
            if (_ladderHint == null && !MakeLadderHint())
                return;
            if (_ladderHint.text != text)
                _ladderHint.text = text;
            if (!_ladderHint.gameObject.activeSelf)
                _ladderHint.gameObject.SetActive(true);
        }

        private bool MakeLadderHint()
        {
            BaseUISceneGameplay ui = BaseUISceneGameplay.Singleton;
            Canvas canvas = ui != null ? ui.GetComponentInChildren<Canvas>(true) : null;
            if (canvas == null)
                return false;
            canvas = canvas.rootCanvas;
            var go = new GameObject("LadderHint", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 150f);
            rect.sizeDelta = new Vector2(700f, 30f);
            _ladderHint = go.AddComponent<UnityEngine.UI.Text>();
            _ladderHint.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _ladderHint.fontSize = 18;
            _ladderHint.alignment = TextAnchor.MiddleCenter;
            _ladderHint.horizontalOverflow = HorizontalWrapMode.Overflow;
            _ladderHint.color = new Color(1f, 0.95f, 0.8f);
            _ladderHint.raycastTarget = false;
            var outline = go.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
            return true;
        }

        #endregion

        #region Sheathing

        /// <summary>
        /// The player's draw/sheathe component. `DemoEntitySetting` puts it on every player entity as
        /// it wakes (it has to be there before the spawn, for the sheathed start), so this normally
        /// just finds it; the add is the fallback for an entity that came some other way. Only the
        /// player at this keyboard ever drives it - everyone else's weapons follow the synced flag
        /// on their own copy of the model.
        /// </summary>
        private DemoWeaponSheathing Sheathing
        {
            get
            {
                if (PlayingCharacterEntity == null)
                    return null;
                if (_sheathing == null)
                    _sheathing = PlayingCharacterEntity.gameObject.GetOrAddComponent<DemoWeaponSheathing>();
                return _sheathing;
            }
        }

        /// <summary>
        /// The Z key. Putting the weapons away also ends an auto-attack and drops a queued skill:
        /// a character that is sheathed cannot swing, and would otherwise run at its target with
        /// empty hands for as long as the chase lasted. A press mid-swing is kept and played when
        /// the swing ends (<see cref="DemoWeaponSheathing.RequestToggle"/>) - it used to be dropped.
        /// </summary>
        private void ToggleSheathed()
        {
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing == null)
                return;
            bool puttingAway = !sheathing.IsSheathed;
            if (!sheathing.RequestToggle() || !puttingAway)
                return;
            StopAutoAttack();
            ClearQueueUsingSkill();
            _heldHotkey = null;
        }

        /// <summary>
        /// A weapon in the hand before a fight. Called wherever the controller is told to attack
        /// something, so a sheathed character draws and then swings instead of doing nothing.
        /// The swing itself is held until the weapon is out by the sheathing component (an
        /// attack that is refused is simply asked for again next frame by the chase). A Z press
        /// still waiting to put the weapons away is dropped: the player has asked for a fight since.
        /// </summary>
        private void DrawIfSheathed()
        {
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing == null)
                return;
            sheathing.ForgetRequest();
            if (sheathing.IsSheathed)
                sheathing.Draw();
        }

        /// <summary>
        /// Draws for a chase the controller did not start itself. The kit's own ways into a fight -
        /// a second left click on an enemy or a tree, which locks on and closes in - set the chase
        /// without asking for the weapon, and a sheathed character walked up to its target and stood
        /// there: the swing is refused while sheathed, and nothing drew. Found 2026-10-04.
        /// </summary>
        private void DrawForChase()
        {
            if (!_isFollowingTarget || _targetActionType != TargetActionType.Attack || TargetGameEntity == null)
                return;
            if (TargetGameEntity is DamageableEntity damageable && damageable.CurrentHp <= 0)
                return;
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing != null && sheathing.IsSheathed && !PlayingCharacterEntity.IsDead())
                DrawIfSheathed();
        }

        /// <summary>
        /// Tells the sheathing component the character is still fighting, so it does not put its
        /// weapons away in the middle of it. The component hears about the swings, the skills
        /// and the damage taken itself; what it cannot see is what only the controller knows -
        /// a chase on its way to a target (a tree as much as a wolf), a skill queued or held back
        /// for the draw, a ground-targeted skill being aimed - none of which has struck yet.
        /// </summary>
        private void NoteCombat()
        {
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing == null || sheathing.IsSheathed)
                return;
            bool chasing = _isFollowingTarget &&
                           (_targetActionType == TargetActionType.Attack || _targetActionType == TargetActionType.UseSkill);
            if (chasing || _queueUsingSkill.skill != null || _heldHotkey.HasValue || UICharacterHotkeys.UsingHotkey != null)
                sheathing.NoteActivity();
        }

        /// <summary>
        /// The kit's Attack key reads the key held, and asks the entity to swing at once - which a
        /// sheathed character refuses. Draw on the press, ahead of the base's own read of it.
        /// </summary>
        private void DrawForAttackKey()
        {
            if (PlayingCharacterEntity == null || PlayingCharacterEntity.IsDead() || GenericUtils.IsFocusInputField())
                return;
            if (InputManager.GetButtonDown("Attack"))
                DrawIfSheathed();
        }

        /// <summary>
        /// A skill pressed on a sheathed character draws the weapon first and goes off when it
        /// is in the hand: true if this took the press and will use it later.
        ///
        /// **Skills cannot simply be refused until then** the way a swing can. The kit clears its
        /// skill queue on the very request it refuses (`RequestUsePendingSkill` calls
        /// `ClearQueueUsingSkill` whether or not the cast went off), so a skill vetoed during the
        /// draw would be thrown away. The press is held here and replayed through this same method
        /// at <see cref="DemoWeaponSheathing.ReadyAt"/>, with the aim it was given.
        /// A second press while one is held takes its place.
        /// </summary>
        private bool HoldForDraw(HotkeyType type, string relateId, AimPosition aimPosition)
        {
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing == null)
                return false;
            sheathing.ForgetRequest();
            if (sheathing.IsSheathed)
            {
                // Nothing to draw: let the kit give its own answer to the skill.
                if (!sheathing.Draw())
                    return false;
            }
            else if (!sheathing.IsDrawing)
            {
                return false;
            }
            _heldHotkey = new HeldHotkey { Type = type, RelateId = relateId, Aim = aimPosition, At = sheathing.ReadyAt };
            return true;
        }

        /// <summary>Plays a held skill press once the weapon is in the hand; drops it if the character cannot use it any more.</summary>
        private void ReleaseHeldHotkey()
        {
            if (!_heldHotkey.HasValue)
                return;
            DemoWeaponSheathing sheathing = Sheathing;
            if (sheathing == null || PlayingCharacterEntity.IsDead() || sheathing.IsSheathed)
            {
                _heldHotkey = null;
                return;
            }
            if (Time.unscaledTime < _heldHotkey.Value.At)
                return;
            HeldHotkey held = _heldHotkey.Value;
            _heldHotkey = null;
            UseHotkey(held.Type, held.RelateId, held.Aim);
        }

        #endregion

        /// <summary>
        /// Target an enemy and start attacking it: the kit's second-click behaviour, but
        /// in one call. SetTarget selects on the first call and locks on the second, so it
        /// is called twice on purpose; the kit's click handler then also raises the follow
        /// flag, which is what makes UpdateFollowTarget close in and swing - without it the
        /// target is merely held.
        /// </summary>
        public void AttackTarget(BaseCharacterEntity enemy)
        {
            if (enemy == null || enemy.IsDead())
                return;
            DrawIfSheathed();
            SetTarget(enemy, TargetActionType.Attack);
            SetTarget(enemy, TargetActionType.Attack);
            _isFollowingTarget = true;
        }

        /// <summary>
        /// <see cref="AttackTarget"/> for a tree, boulder or vein: close in and swing. Right
        /// click and the Attack button only ever took characters, so a node could be harvested
        /// by left-clicking it twice (select, then lock) and no other way (2026-10-03). The
        /// weapon still decides whether the swing does anything - see `demo-iron-and-tools`.
        /// </summary>
        public void AttackNode(HarvestableEntity node)
        {
            if (node == null || node.CurrentHp <= 0)
                return;
            DrawIfSheathed();
            SetTarget(node, TargetActionType.Attack);
            SetTarget(node, TargetActionType.Attack);
            _isFollowingTarget = true;
        }

        private void Track(ref Button button, int index, bool pointerOverUi)
        {
            if (InputManager.GetMouseButtonDown(index))
            {
                button.Down = true;
                button.Drag = false;
                button.OverUi = pointerOverUi;
                button.DownAt = InputManager.MousePosition();
            }
            if (button.Down && !button.Drag &&
                (InputManager.MousePosition() - button.DownAt).sqrMagnitude > dragThreshold * dragThreshold)
                button.Drag = true;
            // Kept through the frame the button comes up, so the click handlers can still
            // ask whether it was a drag; cleared the frame after.
            if (!InputManager.GetMouseButton(index) && !InputManager.GetMouseButtonUp(index))
            {
                button.Down = false;
                button.Drag = false;
            }
        }

        private BaseGameEntity EntityUnderPointer()
        {
            int count = FindClickObjects(out _);
            for (int i = 0; i < count; ++i)
            {
                Transform hit = _physicFunctions.GetRaycastTransform(i);
                if (hit == null)
                    continue;
                BaseGameEntity entity = hit.GetComponentInParent<BaseGameEntity>();
                if (entity != null && entity != PlayingCharacterEntity)
                    return entity;
            }
            return null;
        }

        /// <summary>
        /// Tab: selects the nearest living enemy in reach that stands inside
        /// <see cref="tabTargetFov"/> in front of the character, and cycles outward through
        /// the others on each further press.
        ///
        /// **Replaces the kit's Tab**, from `Demo/` rather than by editing Core. The kit keeps
        /// a counter into its enemy detector's list, which is sorted by distance alone, so a
        /// press takes the nearest enemy wherever it stands - behind the character as readily
        /// as ahead - and the next press the next-nearest, again in any direction. Found
        /// 2026-10-01. The kit's block sits inline in `UpdateInput` and reads a field whose
        /// press state cannot be cleared, so `Awake` rebinds that field to a key that never
        /// fires and the press is read here instead; the detector's list is still the pool,
        /// so reach is what it was - the attack distance plus the lock distance.
        ///
        /// Direction is the character's facing, not the camera's: right-drag turns both, a
        /// left-drag orbit leaves the character where it was, and that is the direction the
        /// player is told it looks. Height is ignored, as it is for the kit's own attack cone.
        ///
        /// The cycle starts after whatever is selected, so a press with the nearest already
        /// selected moves on to the next, and wraps. What it does with the pick is exactly
        /// what the kit's own block did: select it, mark the action as attack, and turn to
        /// face it without moving or swinging.
        /// </summary>
        private void TabTarget()
        {
            if (EnemyEntityDetector == null)
                return;
            List<BaseCharacterEntity> candidates = TabTargetCandidates();
            if (candidates.Count == 0)
                return;
            int index = SelectedEntity is BaseCharacterEntity selected ? candidates.IndexOf(selected) : -1;
            BaseCharacterEntity target = candidates[(index + 1) % candidates.Count];
            SetTarget(null, TargetActionType.Attack);
            SetTarget(target, TargetActionType.Attack);
            if (SelectedGameEntity != null)
                TurnCharacterToEntity(SelectedGameEntity);
        }

        /// <summary>
        /// The living enemies in the detector's reach that lie within <see cref="tabTargetFov"/>
        /// of the character's facing, nearest first.
        /// </summary>
        private List<BaseCharacterEntity> TabTargetCandidates()
        {
            List<BaseCharacterEntity> candidates = new List<BaseCharacterEntity>();
            Vector3 origin = PlayingCharacterEntity.EntityTransform.position;
            Vector3 forward = PlayingCharacterEntity.EntityTransform.forward;
            List<BaseCharacterEntity> nearby = EnemyEntityDetector.characters;
            for (int i = 0; i < nearby.Count; ++i)
            {
                BaseCharacterEntity enemy = nearby[i];
                if (enemy == null || enemy.IsDeadOrHideFrom(PlayingCharacterEntity))
                    continue;
                if (tabTargetFov < 360f && !origin.IsPositionInFov3D(tabTargetFov, enemy.EntityTransform.position, forward))
                    continue;
                candidates.Add(enemy);
            }
            candidates.Sort((a, b) =>
                (a.EntityTransform.position - origin).sqrMagnitude
                .CompareTo((b.EntityTransform.position - origin).sqrMagnitude));
            return candidates;
        }

        /// <summary>
        /// The kit's own "is the UI holding the controller" test, as `UpdateInput` applies it
        /// on each platform, for the input read after the base has run.
        /// </summary>
        private bool IsControllerBlocked()
        {
            if (!UISceneGameplay.IsBlockController())
                return false;
            if (Application.isMobilePlatform || GameInstance.IsMobileTestInEditor())
                return !uiNotBlockForMobile;
            if (Application.isConsolePlatform || GameInstance.IsConsoleTestInEditor())
                return !uiNotBlockForConsole;
            return !uiNotBlockForStandalone;
        }

        private BaseCharacterEntity EnemyUnderPointer()
        {
            var character = EntityUnderPointer() as BaseCharacterEntity;
            if (character == null || character.IsDead())
                return null;
            return PlayingCharacterEntity.IsEnemy(character.GetInfo()) ? character : null;
        }
    }
}
