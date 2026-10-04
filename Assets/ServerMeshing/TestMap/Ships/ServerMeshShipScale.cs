// Server Meshing test rig — the stand-in ship's scale factor (Phase 0 step 7, MESH_SHIP_SCALE): every distance and
// speed of the space game's ship (read at commit 1a3a58d9) times one factor; rates, timers, torques and bytes unscaled.
using System;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipScale holds the ship scale factor and the scaled distances and speeds derived from it; its constants
    /// are the quantities that never scale. Runners scale R0, tiers and gate distances themselves.
    /// </summary>
    public sealed class ServerMeshShipScale
    {
        /// <summary>MinFactor is the smallest factor accepted.</summary>
        public const float MinFactor = 0.001f;

        /// <summary>MaxFactor is the largest factor accepted.</summary>
        public const float MaxFactor = 10f;

        /// <summary>FireIntervalSeconds is the shortest time between two shots of one ship.</summary>
        public const float FireIntervalSeconds = 0.175f;

        /// <summary>ScanIntervalSeconds is the unscaled time between two target scans of an AI pilot.</summary>
        public const float ScanIntervalSeconds = 0.5f;

        /// <summary>RespawnSeconds is how long a destroyed load ship waits before it respawns by teleport.</summary>
        public const float RespawnSeconds = 10f;

        /// <summary>LinearDrag is the rigidbody's linear damping.</summary>
        public const float LinearDrag = 3f;

        /// <summary>AngularDrag is the rigidbody's angular damping.</summary>
        public const float AngularDrag = 8f;

        /// <summary>SteeringGain is the P gain from yaw and pitch error to steering input.</summary>
        public const float SteeringGain = 2.5f;

        /// <summary>RollLevelGain is the P gain from roll error to steering input.</summary>
        public const float RollLevelGain = 0.6f;

        /// <summary>ObserverLerpSpeed is the observer smoothing rate: t = 1 - exp(-ObserverLerpSpeed * dt).</summary>
        public const float ObserverLerpSpeed = 10f;

        /// <summary>PatrolRate is the patrol orbit's angular rate in rad/s.</summary>
        public const float PatrolRate = 0.05f;

        /// <summary>PatrolThrottle is the throttle while patrolling.</summary>
        public const float PatrolThrottle = 0.4f;

        /// <summary>AvoidThrottle is the throttle while steering away from an obstacle.</summary>
        public const float AvoidThrottle = 0.7f;

        /// <summary>ReturnThrottle is the throttle while steering back into the cell rectangle (rig only).</summary>
        public const float ReturnThrottle = 0.7f;

        /// <summary>Hull is the hull's hit points.</summary>
        public const int Hull = 2500;

        /// <summary>Shield is the shield's capacity.</summary>
        public const float Shield = 2500f;

        /// <summary>ShieldRechargeRate is the shield recharge in points per second.</summary>
        public const float ShieldRechargeRate = 100f;

        /// <summary>ShieldRechargeDelaySeconds is how long after the last damage the shield starts recharging.</summary>
        public const float ShieldRechargeDelaySeconds = 2f;

        /// <summary>BoltDamage is the damage of one bolt.</summary>
        public const int BoltDamage = 50;

        /// <summary>AimErrorDegrees is an AI pilot's largest aim error.</summary>
        public const float AimErrorDegrees = 2.5f;

        /// <summary>SteeringTorques is the largest angular acceleration per axis (pitch, yaw, roll), ForceMode.Acceleration.</summary>
        public static readonly Vector3 SteeringTorques = new Vector3(23f, 23f, 30f);

        private static readonly Vector3 BaseColliderSize = new Vector3(8.4f, 3f, 11.9f);
        private static readonly Vector3 BaseColliderCenter = new Vector3(0f, 0.4f, 3.4f);
        private static readonly Vector3 BaseBarrelPosition = new Vector3(2f, 0f, 6f);

        /// <summary>ServerMeshShipScale creates a scale; a factor that is not finite or lies outside 0.001 to 10 throws naming it.</summary>
        public ServerMeshShipScale(float factor)
        {
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < MinFactor || factor > MaxFactor)
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "the ship scale must be a finite number from 0.001 to 10");
            Factor = factor;
        }

        /// <summary>
        /// TryParse reads a scale text in the invariant culture; null or empty gives 1. Anything that is not a finite number
        /// from 0.001 to 10 returns false with an error naming the text.
        /// </summary>
        public static bool TryParse(string text, out float factor, out string error)
        {
            factor = 1f;
            error = null;
            string trimmed = text == null ? string.Empty : text.Trim();
            if (trimmed.Length == 0)
                return true;
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || float.IsNaN(value) || float.IsInfinity(value) || value < MinFactor || value > MaxFactor)
            {
                factor = 1f;
                error = $"'{Sync.MeshStateSender.CleanForLog(trimmed)}' is not a number from 0.001 to 10";
                return false;
            }
            factor = value;
            return true;
        }

        /// <summary>Factor is the scale factor.</summary>
        public float Factor { get; }

        /// <summary>VisibleRange is the ship identity's visible range: 2,000 m scaled.</summary>
        public float VisibleRange => 2000f * Factor;

        /// <summary>DetectionRange is an AI pilot's hostile-detection radius: 2,200 m scaled.</summary>
        public float DetectionRange => 2200f * Factor;

        /// <summary>TargetDropRange is the distance beyond which an AI pilot drops its target: 2,860 m scaled.</summary>
        public float TargetDropRange => 2860f * Factor;

        /// <summary>MaxFiringDistance is the farthest an AI pilot fires from: 1,000 m scaled.</summary>
        public float MaxFiringDistance => 1000f * Factor;

        /// <summary>MinEngageDistance is the distance under which an AI pilot is forced to evade: 150 m scaled.</summary>
        public float MinEngageDistance => 150f * Factor;

        /// <summary>EvadeDistance is how far ahead an evading pilot aims: 600 m scaled.</summary>
        public float EvadeDistance => 600f * Factor;

        /// <summary>AvoidanceSteerDistance is how far away an avoiding pilot aims: 200 m scaled.</summary>
        public float AvoidanceSteerDistance => 200f * Factor;

        /// <summary>AvoidanceRadius is the avoidance sphere-cast radius: 8 m scaled.</summary>
        public float AvoidanceRadius => 8f * Factor;

        /// <summary>AvoidanceMinDistance is the shortest avoidance cast: 30 m scaled.</summary>
        public float AvoidanceMinDistance => 30f * Factor;

        /// <summary>PatrolRadius is the patrol orbit's radius: 400 m scaled.</summary>
        public float PatrolRadius => 400f * Factor;

        /// <summary>ProjectileSpeed is a bolt's speed: 800 m/s scaled.</summary>
        public float ProjectileSpeed => 800f * Factor;

        /// <summary>ProjectileRange is how far a bolt flies: 1,000 m scaled.</summary>
        public float ProjectileRange => 1000f * Factor;

        /// <summary>SnapDistance is the observer snap distance: 50 m scaled.</summary>
        public float SnapDistance => 50f * Factor;

        /// <summary>Force is the engine's thrust per axis: 400 scaled.</summary>
        public float Force => 400f * Factor;

        /// <summary>BoostForce is the engine's boosted thrust per axis: 800 scaled.</summary>
        public float BoostForce => 800f * Factor;

        /// <summary>PathSemiMajorMin is the shortest flight-path semi-major axis: 200 m scaled.</summary>
        public float PathSemiMajorMin => 200f * Factor;

        /// <summary>PathSemiMajorMax is the longest flight-path semi-major axis: 280 m scaled.</summary>
        public float PathSemiMajorMax => 280f * Factor;

        /// <summary>PathSpeedMin is the lowest flight-path top speed: 50 m/s scaled.</summary>
        public float PathSpeedMin => 50f * Factor;

        /// <summary>PathSpeedMax is the highest flight-path top speed: 125 m/s scaled.</summary>
        public float PathSpeedMax => 125f * Factor;

        /// <summary>ColliderSize is the hull box's size: (8.4, 3, 11.9) m scaled.</summary>
        public Vector3 ColliderSize => BaseColliderSize * Factor;

        /// <summary>ColliderCenter is the hull box's centre: (0, 0.4, 3.4) m scaled.</summary>
        public Vector3 ColliderCenter => BaseColliderCenter * Factor;

        /// <summary>BarrelPosition is the right barrel's local position, (2, 0, 6) m scaled; the left barrel mirrors x.</summary>
        public Vector3 BarrelPosition => BaseBarrelPosition * Factor;

        /// <summary>RectangleInset is the share of the cell rectangle kept clear on each side, 0.1 (a share, never scaled).</summary>
        public float RectangleInset => 0.1f;

        /// <summary>BotPathBudget is the bot's path-distance budget: 0.5 m scaled.</summary>
        public float BotPathBudget => 0.5f * Factor;
    }
}
