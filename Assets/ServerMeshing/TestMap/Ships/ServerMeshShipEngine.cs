// Server Meshing test rig — the stand-in ship's engine (Phase 0 step 7): the space game's ShipEngine at commits
// 1a3a58d9 and c2eb4154 (read there, not copied) as a plain class, its numbers from a ship class (step 7b). The movement
// component applies its output in FixedUpdate.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipEngine turns a pilot's steering and thrust inputs into a relative torque (ForceMode.Acceleration,
    /// the class's torques, never scaled) and a relative force (the class's thrust times the scale factor) for one physics
    /// step. Steering authority falls linearly from 1 at rest to 0.8 at top speed, or is 0.5 while boosting; the applied
    /// force chases its target at ResponseSpeed per second. Boost is forward boost: it raises the forward thrust to the
    /// class's scaled BoostForce.
    /// </summary>
    public sealed class ServerMeshShipEngine
    {
        /// <summary>ResponseSpeed is how fast the applied force chases its target, per second.</summary>
        public const float ResponseSpeed = 6f;

        /// <summary>BoostSteering is the steering multiplier while boosting (it replaces the speed curve).</summary>
        public const float BoostSteering = 0.5f;

        /// <summary>SteeringAtRest is the speed curve's multiplier at rest.</summary>
        public const float SteeringAtRest = 1f;

        /// <summary>SteeringAtTopSpeed is the speed curve's multiplier at top speed and beyond.</summary>
        public const float SteeringAtTopSpeed = 0.8f;

        /// <summary>Mass is the rigidbody's mass the engine assumes for its top speed.</summary>
        public const float Mass = 1f;

        private readonly float _force;
        private readonly float _boostForce;
        private Vector3 _forces;

        /// <summary>ServerMeshShipEngine creates a Base-class engine at <paramref name="scale"/>; a null scale throws naming it.</summary>
        public ServerMeshShipEngine(ServerMeshShipScale scale)
            : this(scale, ServerMeshShipClass.Base)
        {
        }

        /// <summary>
        /// ServerMeshShipEngine creates an engine of <paramref name="shipClass"/> at <paramref name="scale"/>; a null scale
        /// throws ArgumentNullException naming it, a default (unnamed) class ArgumentOutOfRangeException naming it.
        /// </summary>
        public ServerMeshShipEngine(ServerMeshShipScale scale, ServerMeshShipClass shipClass)
        {
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            if (shipClass.Name == null)
                throw new ArgumentOutOfRangeException(nameof(shipClass), "a default ship class has no numbers; use ServerMeshShipClass.Base or Enemy");
            ShipClass = shipClass;
            _force = shipClass.Force * scale.Factor;
            _boostForce = shipClass.BoostForce * scale.Factor;
        }

        /// <summary>ShipClass is the class whose force, drag and torques the engine uses.</summary>
        public ServerMeshShipClass ShipClass { get; }

        /// <summary>
        /// Step computes one physics step: <paramref name="steer"/> and <paramref name="move"/> are clamped to -1..1 per
        /// axis, <paramref name="speed"/> is the body's current speed in m/s. A fixed delta time that is not positive throws
        /// naming it.
        /// </summary>
        public void Step(Vector3 steer, Vector3 move, bool boost, float speed, float fixedDeltaTime, out Vector3 relativeTorque, out Vector3 relativeForce)
        {
            if (!(fixedDeltaTime > 0f) || float.IsInfinity(fixedDeltaTime))
                throw new ArgumentOutOfRangeException(nameof(fixedDeltaTime), fixedDeltaTime, "the fixed delta time must be positive");
            float multiplier;
            if (boost)
            {
                multiplier = BoostSteering;
            }
            else
            {
                float top = TopSpeed(fixedDeltaTime, false);
                float amount = top > 0.0001f ? Mathf.Clamp01(speed / top) : 0f;
                multiplier = Mathf.Lerp(SteeringAtRest, SteeringAtTopSpeed, amount);
            }
            relativeTorque = multiplier * Vector3.Scale(Clamp(steer), ShipClass.SteeringTorques);

            Vector3 target = Clamp(move) * _force;
            if (boost)
                target.z = Mathf.Max(target.z, _boostForce);
            _forces = Vector3.Lerp(_forces, target, ResponseSpeed * fixedDeltaTime);
            relativeForce = _forces;
        }

        /// <summary>Reset zeroes the applied force (respawn, teleport, a pooled ship reused).</summary>
        public void Reset()
        {
            _forces = Vector3.zero;
        }

        /// <summary>
        /// TopSpeed is the terminal speed of the class's scaled thrust, boosted or not, under the class's linear drag at
        /// <paramref name="fixedDeltaTime"/>.
        /// </summary>
        public float TopSpeed(float fixedDeltaTime, bool boost)
        {
            return TerminalSpeed(boost ? _boostForce : _force, ShipClass.LinearDrag, fixedDeltaTime, Mass);
        }

        /// <summary>
        /// TerminalSpeed is the steady speed under a sustained force when PhysX integrates the force before damping:
        /// (force / drag - force * dt) / mass. Every argument must be positive; one that is not throws naming it.
        /// </summary>
        public static float TerminalSpeed(float force, float drag, float fixedDeltaTime, float mass)
        {
            Positive(force, nameof(force));
            Positive(drag, nameof(drag));
            Positive(fixedDeltaTime, nameof(fixedDeltaTime));
            Positive(mass, nameof(mass));
            return (force / drag - force * fixedDeltaTime) / mass;
        }

        /// <summary>
        /// SteadySpeed iterates the damping step v = (v + force / mass * dt) * (1 - drag * dt) from rest for
        /// <paramref name="steps"/> steps. Arguments are checked as TerminalSpeed does; negative steps throw naming them.
        /// </summary>
        public static float SteadySpeed(float force, float drag, float fixedDeltaTime, float mass, int steps)
        {
            Positive(force, nameof(force));
            Positive(drag, nameof(drag));
            Positive(fixedDeltaTime, nameof(fixedDeltaTime));
            Positive(mass, nameof(mass));
            if (steps < 0)
                throw new ArgumentOutOfRangeException(nameof(steps), steps, "the step count must not be negative");
            double speed = 0d;
            double gain = (double)force / mass * fixedDeltaTime;
            double keep = 1d - (double)drag * fixedDeltaTime;
            for (int i = 0; i < steps; ++i)
                speed = (speed + gain) * keep;
            return (float)speed;
        }

        private static void Positive(float value, string name)
        {
            if (!(value > 0f) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, "must be a positive finite number");
        }

        private static Vector3 Clamp(Vector3 v)
        {
            return new Vector3(Mathf.Clamp(v.x, -1f, 1f), Mathf.Clamp(v.y, -1f, 1f), Mathf.Clamp(v.z, -1f, 1f));
        }
    }
}
