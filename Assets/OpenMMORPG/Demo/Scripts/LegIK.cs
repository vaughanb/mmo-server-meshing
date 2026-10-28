using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The pieces the two foot IK components share: when to run, the sole lock, the two-bone
    /// solve, and laying a dead body on the ground. <see cref="HumanoidFootIK"/> is the humanoid one, found through the
    /// Animator's human bones; <see cref="QuadrupedFootIK"/> is the four-legged one, wired
    /// by bone name. See those for how each is put together.
    /// </summary>
    public static class LegIK
    {
        /// <summary>
        /// A leg bent less than this is treated as straight, and bends toward its pole rather
        /// than toward whatever sliver of bend the animation left in it - which on a leg
        /// locked out, or a hair past straight, can point the wrong way and fold the knee
        /// backwards.
        /// </summary>
        private const float StraightDegrees = 3f;

        /// <summary>
        /// Whether feet should be planted at all: standing or moving on the ground, alive,
        /// not riding, and near enough to the camera to be worth the rays.
        /// </summary>
        public static bool ShouldBeActive(BaseGameEntity entity, BaseCharacterEntity character, Transform model, float maxCameraDistance)
        {
            if (!entity.PassengingVehicleEntity.IsNull())
                return false;
            if (character != null && character.IsDead())
                return false;
            if (OnLadder(character))
                return false;
            MovementState state = entity.MovementState;
            if ((state & MovementState.IsGrounded) == 0)
                return false;
            if ((state & (MovementState.IsUnderWater | MovementState.IsClimbing | MovementState.IsJump)) != 0)
                return false;
            Camera camera = Camera.main;
            if (camera != null && (camera.transform.position - model.position).sqrMagnitude > maxCameraDistance * maxCameraDistance)
                return false;
            return true;
        }

        /// <summary>
        /// Whether the character has left the ground: the frame a jump is raised (the kit still
        /// flags it grounded for that one frame) and every frame it is in the air after, by a
        /// jump or a fall. Swimming and climbing are not this - they have no
        /// <see cref="MovementState.IsGrounded"/> either, and keep the fade they always had.
        ///
        /// Feet that are in the air have no ground to be planted on, so the IK is cut at once
        /// rather than faded: the fade runs 0.2 s with the root already rising at jump speed, and
        /// the hips follow an eased *world* height (<see cref="Follow"/>) that lags the root. The
        /// lag read as the ground falling away, so the hips were pulled 0.29 m down through the
        /// start of every running jump and then let go in one frame (measured 2026-10-04: hips
        /// 0.78, 0.75 ... 0.57 with the IK off; 0.40 ... 0.28, then 0.57 with it on).
        /// </summary>
        public static bool IsAirborne(BaseGameEntity entity)
        {
            MovementState state = entity.MovementState;
            if ((state & (MovementState.IsUnderWater | MovementState.IsClimbing)) != 0)
                return false;
            return (state & MovementState.IsJump) != 0 || (state & MovementState.IsGrounded) == 0;
        }

        /// <summary>
        /// Whether the character is on a ladder, by the ladder component rather than the
        /// movement state: the kit raises <see cref="MovementState.IsClimbing"/> only while
        /// the climber is moving, and reports a climber hanging still, or being carried on
        /// or off the rungs, as plainly grounded. Planted that way, the feet were pulled to
        /// whatever the rays found below and the hips went with them.
        /// </summary>
        private static bool OnLadder(BaseCharacterEntity character)
        {
            return character != null && character.LadderComponent != null && character.LadderComponent.ClimbingLadder != null;
        }

        /// <summary>
        /// Whether a dead body should be laid on the ground under it (see <see cref="LayOnGround"/>):
        /// dead, lying on the ground rather than in water or on a ladder, not riding, and near
        /// enough to the camera to be seen.
        /// </summary>
        public static bool ShouldGroundCorpse(BaseGameEntity entity, BaseCharacterEntity character, Transform model, float maxCameraDistance)
        {
            if (character == null || !character.IsDead())
                return false;
            if (!entity.PassengingVehicleEntity.IsNull())
                return false;
            if (OnLadder(character))
                return false;
            MovementState state = entity.MovementState;
            if ((state & MovementState.IsGrounded) == 0)
                return false;
            if ((state & (MovementState.IsUnderWater | MovementState.IsClimbing | MovementState.IsJump)) != 0)
                return false;
            Camera camera = Camera.main;
            if (camera != null && (camera.transform.position - model.position).sqrMagnitude > maxCameraDistance * maxCameraDistance)
                return false;
            return true;
        }

        /// <summary>
        /// Lays a dead body on the ground actually under it. The death clips drop a body onto
        /// the root's plane, but the root need not be on the ground: a navmesh agent's stands on
        /// the navmesh, which sat a median 15cm above the terrain across the island's monster
        /// grounds (measured 2026-09-24), and the feet that made up for it while alive are no
        /// longer planted. So the ground under <paramref name="bone"/> - the one the whole
        /// skeleton hangs from - is found within <paramref name="up"/> above and
        /// <paramref name="down"/> below the root's plane; the bone is tipped to lie along it,
        /// about itself, and carried down (or up) onto it, and every other bone follows.
        ///
        /// Probed under the bone rather than the root because a body falls away from where it
        /// stood - a humanoid's hips end up most of a metre behind its feet - and on a slope the
        /// ground there is not the ground under the root. Both are eased, the height as a world
        /// height (see <see cref="Follow"/>), so a body still falling does not jerk.
        /// </summary>
        public static void LayOnGround(Transform bone, float rootY, float up, float down, float maxTilt, int mask,
            float blend, float weight, bool fresh, ref float groundY, ref Vector3 normal)
        {
            Vector3 at = bone.position;
            float target = rootY;
            Vector3 targetNormal = Vector3.up;
            if (Physics.Raycast(new Vector3(at.x, rootY + up, at.z), Vector3.down, out RaycastHit hit, up + down, mask, QueryTriggerInteraction.Ignore))
            {
                target = hit.point.y;
                targetNormal = hit.normal;
            }
            groundY = Follow(groundY, target, blend, fresh);
            normal = fresh ? targetNormal : Vector3.Slerp(normal, targetNormal, blend);
            bone.rotation = Tilt(normal, maxTilt, weight) * bone.rotation;
            bone.position += Vector3.up * ((groundY - rootY) * weight);
        }

        /// <summary>A jump this big is a teleport or a respawn, not ground: taken at once.</summary>
        private const float SnapDistance = 0.5f;

        /// <summary>
        /// Eases a smoothed world height toward this frame's, or takes it at once when
        /// <paramref name="snap"/> is set or the two are more than <see cref="SnapDistance"/>
        /// apart. Ground heights are smoothed as world heights, never as offsets from the
        /// entity's root: a navmesh agent's root jumps as it crosses polygon edges, and an
        /// offset from it would read every such jump as the ground moving.
        /// </summary>
        public static float Follow(float current, float target, float blend, bool snap)
        {
            if (snap || float.IsNaN(current) || Mathf.Abs(target - current) > SnapDistance)
                return target;
            return Mathf.Lerp(current, target, blend);
        }

        /// <summary>
        /// The foot lock: a sole's clearance above the ground as the animation has it, made
        /// never negative (out of the floor), and eased onto the ground inside
        /// <paramref name="range"/> (c²/range, which meets the animation at the top of the
        /// range and the ground at the bottom). Above the range it is left alone.
        /// </summary>
        public static float Lock(float clearance, float range)
        {
            if (clearance <= 0f)
                return 0f;
            if (clearance < range)
                return clearance * clearance / range;
            return clearance;
        }

        /// <summary>
        /// Analytic two-bone IK: set the middle joint's angle so the chain is as long as the
        /// distance to the target, then swing the upper bone so the end lies on it. The joint
        /// bends in the plane it already bends in, so the animation's knee direction is kept;
        /// <paramref name="pole"/> - the way the joint should bulge - only decides it when the
        /// chain is (nearly) straight and there is no bend to read.
        /// </summary>
        public static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            const float eps = 0.001f;
            Vector3 a = upper.position;
            Vector3 b = lower.position;
            Vector3 c = end.position;
            float lab = (b - a).magnitude;
            float lcb = (c - b).magnitude;
            if (lab < eps || lcb < eps)
                return;
            float lat = Mathf.Clamp((target - a).magnitude, Mathf.Abs(lab - lcb) + eps, lab + lcb - eps);

            Vector3 ac = c - a;
            Vector3 axis = Vector3.Cross(ac, b - a);
            if (axis.sqrMagnitude < 1e-8f || Angle(b - a, c - b) < StraightDegrees)
                axis = Vector3.Cross(ac, pole);
            if (axis.sqrMagnitude < 1e-8f)
                return;
            axis.Normalize();

            float upperNow = Angle(ac, b - a);
            float jointNow = Angle(a - b, c - b);
            float upperWanted = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f)) * Mathf.Rad2Deg;
            float jointWanted = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f)) * Mathf.Rad2Deg;
            // A straight chain read off the pole has no signed bend yet: measure the current
            // angles as if it bulged that way, which they are to within StraightDegrees.
            upper.rotation = Quaternion.AngleAxis(upperWanted - upperNow, axis) * upper.rotation;
            lower.rotation = Quaternion.AngleAxis(jointWanted - jointNow, axis) * lower.rotation;
            upper.rotation = Quaternion.FromToRotation(end.position - a, target - a) * upper.rotation;
        }

        /// <summary>
        /// The rotation that tips a planted foot onto ground with this normal, capped at
        /// <paramref name="maxDegrees"/> and scaled by how planted it is.
        /// </summary>
        public static Quaternion Tilt(Vector3 normal, float maxDegrees, float amount)
        {
            Quaternion tilt = Quaternion.FromToRotation(Vector3.up, normal);
            float angle = Quaternion.Angle(Quaternion.identity, tilt);
            if (angle > maxDegrees)
                tilt = Quaternion.Slerp(Quaternion.identity, tilt, maxDegrees / angle);
            return Quaternion.Slerp(Quaternion.identity, tilt, amount);
        }

        private static float Angle(Vector3 from, Vector3 to)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(from.normalized, to.normalized), -1f, 1f)) * Mathf.Rad2Deg;
        }
    }
}
