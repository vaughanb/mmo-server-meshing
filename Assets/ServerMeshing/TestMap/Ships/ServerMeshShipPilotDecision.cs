// Server Meshing test rig — an AI stand-in ship's decisions (Phase 0 step 7), modelled by reading the space game's
// ShipAIPilot at commit 1a3a58d9: avoidance, then combat (attack and evade timers, bursts), then patrol, with the rig's
// stay-in-rectangle Return between avoidance and combat. Pure: the pilot component senses and steers.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipPilotDecision holds one AI pilot's combat state and its seeded random stream. Decide picks the task
    /// with priority Avoid, Return, combat, Patrol. Combat alternates Attack (8 to 10 s) and Evade (5 to 10 s, its
    /// direction re-rolled every 1 to 4 s), and a target closer than MinEngageDistance forces Evade. As in the space
    /// pilot, the state starts as an expired Attack, so the first combat frame turns to Evade.
    /// </summary>
    public sealed class ServerMeshShipPilotDecision
    {
        /// <summary>FireConeDegrees is the largest aim angle a pilot fires at (aim correction 4 plus aim assist 3).</summary>
        public const float FireConeDegrees = 7f;

        private const float AttackThrottleDistance = 400f;
        private const float MinAttackThrottle = 0.35f;

        private readonly Random _random;
        private readonly ServerMeshShipScale _scale;
        private bool _attacking = true;
        private double _stateUntil;
        private double _nextEvadeRoll;
        private bool _bursting;
        private double _burstToggleAt;

        /// <summary>ServerMeshShipPilotDecision creates a pilot's decisions over <paramref name="random"/>; null arguments throw naming them.</summary>
        public ServerMeshShipPilotDecision(Random random, ServerMeshShipScale scale)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _scale = scale ?? throw new ArgumentNullException(nameof(scale));
        }

        /// <summary>TargetDropped is true when the last Decide call dropped the target (dead, or beyond TargetDropRange).</summary>
        public bool TargetDropped { get; private set; }

        /// <summary>EvadeYaw is the last rolled evade yaw in degrees: 60 to 120 either way.</summary>
        public float EvadeYaw { get; private set; }

        /// <summary>EvadePitch is the last rolled evade pitch in degrees: -30 to 30.</summary>
        public float EvadePitch { get; private set; }

        /// <summary>
        /// Decide returns the task at <paramref name="now"/> (unscaled seconds). A target that is not alive or lies beyond
        /// TargetDropRange (or at an unknown distance) is dropped first, TargetDropped then being true for this call. Then
        /// an obstacle ahead gives Avoid, a ship outside the inset rectangle gives Return, a kept target gives Attack or
        /// Evade, and nothing gives Patrol. Combat timers advance only on calls that reach combat.
        /// </summary>
        public ServerMeshShipPilotTask Decide(double now, bool obstacleAhead, bool outsideRectangle, bool hasTarget, bool targetAlive, float targetDistance)
        {
            TargetDropped = hasTarget && (!targetAlive || !(targetDistance <= _scale.TargetDropRange));
            bool target = hasTarget && !TargetDropped;
            if (obstacleAhead)
                return ServerMeshShipPilotTask.Avoid;
            if (outsideRectangle)
                return ServerMeshShipPilotTask.Return;
            if (!target)
                return ServerMeshShipPilotTask.Patrol;

            if (now >= _stateUntil)
            {
                if (_attacking)
                    StartEvade(now);
                else
                {
                    _attacking = true;
                    _stateUntil = now + Roll(8f, 10f);
                }
            }
            if (_attacking && targetDistance < _scale.MinEngageDistance)
                StartEvade(now);
            if (_attacking)
                return ServerMeshShipPilotTask.Attack;
            if (now >= _nextEvadeRoll)
            {
                _nextEvadeRoll = now + Roll(1f, 4f);
                EvadeYaw = Roll(60f, 120f) * (_random.Next(2) == 0 ? 1f : -1f);
                EvadePitch = Roll(-30f, 30f);
            }
            return ServerMeshShipPilotTask.Evade;
        }

        /// <summary>Bursting is whether an attacking pilot's trigger is down at <paramref name="now"/>: 1 to 3 s on, then 0.5 to 2 s off.</summary>
        public bool Bursting(double now)
        {
            if (now >= _burstToggleAt)
            {
                _bursting = !_bursting;
                _burstToggleAt = now + (_bursting ? Roll(1f, 3f) : Roll(0.5f, 2f));
            }
            return _bursting;
        }

        /// <summary>AttackThrottle is the attack throttle at <paramref name="distance"/>: distance / 400 m scaled, clamped to 0.35..1.</summary>
        public float AttackThrottle(float distance)
        {
            float throttle = distance / (AttackThrottleDistance * _scale.Factor);
            if (float.IsNaN(throttle) || throttle < MinAttackThrottle)
                return MinAttackThrottle;
            return throttle > 1f ? 1f : throttle;
        }

        private void StartEvade(double now)
        {
            _attacking = false;
            _stateUntil = now + Roll(5f, 10f);
            _nextEvadeRoll = 0d;
        }

        private float Roll(float min, float max) => min + (float)_random.NextDouble() * (max - min);
    }
}
