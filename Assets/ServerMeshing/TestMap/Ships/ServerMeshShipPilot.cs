// Server Meshing test rig — the stand-in ship's AI pilot (Phase 0 step 7), modelled on the space game's ShipAIPilot at
// commit 1a3a58d9 (read there, not copied): a 0.5 s scan of every spawned object, one avoidance sphere cast per frame,
// attack and evade timers, burst fire inside a 7 degree cone, and a patrol orbit of the spawn point. The stay-in-rectangle
// Return task is a rig-only addition (cells need bounded ships); the decisions themselves are ServerMeshShipPilotDecision.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipPilot flies a ServerAI ship on the server. It sits on every ship and is enabled only by Configure for
    /// ServerAI; Begin gives it its seeded random streams and the cell rectangle. While its ship is destroyed it idles with
    /// zero control. The intercept, aim-error and rectangle maths are public and static.
    /// </summary>
    public sealed class ServerMeshShipPilot : MonoBehaviour
    {
        /// <summary>ScanIntervalSeconds is the target scan period (unscaled time).</summary>
        public const float ScanIntervalSeconds = 0.5f;
        /// <summary>AvoidanceHorizonSeconds is how far ahead the avoidance cast looks, in seconds of travel.</summary>
        public const float AvoidanceHorizonSeconds = 3f;
        /// <summary>ReturnHorizonSeconds is how far ahead a ship counts as leaving the rectangle.</summary>
        public const float ReturnHorizonSeconds = 3f;
        /// <summary>AvoidThrottle is the throttle while avoiding (and returning).</summary>
        public const float AvoidThrottle = 0.7f;
        /// <summary>PatrolThrottle is the patrol throttle.</summary>
        public const float PatrolThrottle = 0.4f;
        /// <summary>PatrolRateRadiansPerSecond is the patrol orbit's angular rate.</summary>
        public const float PatrolRateRadiansPerSecond = 0.05f;
        /// <summary>AimErrorDegrees is the AI's largest aim error.</summary>
        public const float AimErrorDegrees = 2.5f;
        /// <summary>EvadeAwayShare is how far an evade direction is turned away from the target.</summary>
        public const float EvadeAwayShare = 0.35f;
        /// <summary>PilotSalt and AimSalt key the pilot's two random streams off MESH_LOAD_SEED.</summary>
        public const int PilotSalt = 0x5348, AimSalt = 0x534A;

        private ServerMeshShipEntity _ship;
        private ServerMeshShipMovement _movement;
        private ServerMeshShipCombat _combat;
        private Rigidbody _rigidbody;
        private ServerMeshShipPilotDecision _decision;
        private System.Random _aimRandom;
        private ServerMeshShipScale _scale;
        private Rect _rectangle;
        private float _flightY;
        private Vector3 _patrolCentre;
        private float _patrolAngle;
        private double _nextScan;
        private ServerMeshShipCombat _target;
        private float _lastEvadeYaw = float.NaN, _lastEvadePitch = float.NaN;
        private Vector3 _evadeDirection;

        /// <summary>CurrentTask is the task the last frame chose.</summary>
        public ServerMeshShipPilotTask CurrentTask { get; private set; }

        /// <summary>OutsideRectangle is true when the last frame found the ship outside, or about to leave, its rectangle.</summary>
        public bool OutsideRectangle { get; private set; }

        /// <summary>IsFlying is true between Begin and the ship's despawn.</summary>
        public bool IsFlying => _decision != null;

        private void Awake()
        {
            _ship = GetComponent<ServerMeshShipEntity>();
            _movement = GetComponent<ServerMeshShipMovement>();
            _combat = GetComponent<ServerMeshShipCombat>();
            _rigidbody = GetComponent<Rigidbody>();
        }

        private void OnDisable()
        {
            _decision = null;
            _target = null;
        }

        /// <summary>
        /// Begin starts the pilot of AI ship <paramref name="index"/>: decisions from System.Random(MixSeed(seed ^ PilotSalt,
        /// index)), aim errors from System.Random(MixSeed(seed ^ AimSalt, index)), patrol around its current position, and
        /// <paramref name="cellRectangle"/> (X/Z, y is z) inset by the scale's share as the rectangle it returns into, at
        /// <paramref name="flightY"/>.
        /// </summary>
        public void Begin(int seed, int index, Rect cellRectangle, float flightY, ServerMeshShipScale scale)
        {
            _scale = scale ?? throw new System.ArgumentNullException(nameof(scale));
            _decision = new ServerMeshShipPilotDecision(new System.Random(ServerMeshShipFlightPath.MixSeed(seed ^ PilotSalt, index)), scale);
            _aimRandom = new System.Random(ServerMeshShipFlightPath.MixSeed(seed ^ AimSalt, index));
            _rectangle = InsetRectangle(cellRectangle, scale.RectangleInset);
            _flightY = flightY;
            _patrolCentre = transform.position;
            _patrolAngle = 0f;
            _nextScan = 0;
            _target = null;
            _lastEvadeYaw = _lastEvadePitch = float.NaN;
            CurrentTask = ServerMeshShipPilotTask.Patrol;
            OutsideRectangle = false;
        }

        private void Update()
        {
            if (_decision == null || _ship == null || !_ship.IsServer || _movement == null || _combat == null)
                return;
            if (_combat.ShipDestroyed)
            {
                _movement.SetControl(Vector3.zero, Vector3.zero, false);
                return;
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            double now = Time.unscaledTimeAsDouble;
            Transform self = transform;
            Vector3 position = self.position;
            Vector3 velocity = _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanIntervalSeconds;
                Scan(position);
                counters.Scans++;
            }
            bool obstacle = ObstacleAhead(position, velocity, out Vector3 away);
            counters.Casts++;
            OutsideRectangle = IsOutsideRectangle(_rectangle, position, velocity, ReturnHorizonSeconds);
            float targetDistance = _target != null ? Vector3.Distance(_target.transform.position, position) : float.MaxValue;
            CurrentTask = _decision.Decide(now, obstacle, OutsideRectangle, _target != null, _target != null && !_target.ShipDestroyed, targetDistance);
            if (_decision.TargetDropped)
                _target = null;
            bool fire = false;
            Vector3 aim = Vector3.zero;
            float aimDistance = 0f;
            switch (CurrentTask)
            {
                case ServerMeshShipPilotTask.Avoid:
                    Drive(position, position + away * _scale.AvoidanceSteerDistance, AvoidThrottle);
                    break;
                case ServerMeshShipPilotTask.Return:
                    Drive(position, new Vector3(_rectangle.center.x, _flightY, _rectangle.center.y), AvoidThrottle);
                    break;
                case ServerMeshShipPilotTask.Attack:
                    if (_target != null)
                        fire = Attack(now, position, self.forward, targetDistance, out aim, out aimDistance);
                    break;
                case ServerMeshShipPilotTask.Evade:
                    Evade(position, self.forward);
                    break;
                default:
                    _patrolAngle += Time.deltaTime * PatrolRateRadiansPerSecond;
                    Vector3 orbit = new Vector3(Mathf.Cos(_patrolAngle), 0f, Mathf.Sin(_patrolAngle)) * _scale.PatrolRadius;
                    Drive(position, _patrolCentre + orbit, PatrolThrottle);
                    break;
            }
            counters.PilotMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (fire && _combat.CanFireNow)
            {
                _combat.SetAim(ApplyAimError(aim, _aimRandom, AimErrorDegrees), aimDistance);
                _combat.ServerFire();
            }
        }

        private void Scan(Vector3 position)
        {
            if (_target != null && (_target.ShipDestroyed || Vector3.Distance(_target.transform.position, position) > _scale.TargetDropRange))
                _target = null;
            ServerMeshShipCombat best = null;
            float bestDistance = _scale.DetectionRange;
            uint own = _ship.ObjectId;
            var spawned = _ship.Manager.Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity identity = spawned.Current.Value;
                if (identity == null || identity.ObjectId == own)
                    continue;
                ServerMeshShipCombat combat = identity.GetComponent<ServerMeshShipCombat>();
                if (combat == null || combat.ShipDestroyed || combat.Team == _combat.Team)
                    continue;
                float distance = Vector3.Distance(identity.transform.position, position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = combat;
                }
            }
            if (best != null)
                _target = best;
        }

        private bool ObstacleAhead(Vector3 position, Vector3 velocity, out Vector3 away)
        {
            away = Vector3.zero;
            float speed = velocity.magnitude;
            Vector3 direction = speed > 1f * _scale.Factor ? velocity / speed : transform.forward;
            float distance = Mathf.Max(_scale.AvoidanceMinDistance, speed * AvoidanceHorizonSeconds);
            if (!Physics.SphereCast(position, _scale.AvoidanceRadius, direction, out RaycastHit hit, distance))
                return false;
            if (hit.collider.transform.IsChildOf(transform) || (_rigidbody != null && hit.collider.attachedRigidbody == _rigidbody))
                return false;
            away = (Vector3.Reflect(direction, hit.normal) + hit.normal).normalized;
            return true;
        }

        private bool Attack(double now, Vector3 position, Vector3 forward, float distance, out Vector3 aim, out float aimDistance)
        {
            ServerMeshShipMovement targetMovement = _target.GetComponent<ServerMeshShipMovement>();
            Vector3 targetVelocity = targetMovement != null ? targetMovement.CurrentVelocity : Vector3.zero;
            InterceptPoint(position, _scale.ProjectileSpeed, _target.transform.position, targetVelocity, out Vector3 lead);
            Drive(position, lead, _decision.AttackThrottle(distance));
            Vector3 toLead = lead - position;
            aim = Vector3.RotateTowards(forward, toLead.sqrMagnitude > 1e-8f ? toLead.normalized : forward, ServerMeshShipPilotDecision.FireConeDegrees * Mathf.Deg2Rad, 0f);
            aimDistance = Mathf.Clamp(toLead.magnitude, 50f * _scale.Factor, _scale.MaxFiringDistance);
            bool inEnvelope = Vector3.Angle(forward, toLead) < ServerMeshShipPilotDecision.FireConeDegrees && distance < _scale.MaxFiringDistance;
            return _decision.Bursting(now) && inEnvelope;
        }

        private void Evade(Vector3 position, Vector3 forward)
        {
            float yaw = _decision.EvadeYaw, pitch = _decision.EvadePitch;
            if (yaw != _lastEvadeYaw || pitch != _lastEvadePitch)
            {
                _lastEvadeYaw = yaw;
                _lastEvadePitch = pitch;
                Vector3 direction = Quaternion.Euler(pitch, yaw, 0f) * forward;
                if (_target != null)
                    direction = Vector3.Slerp(direction.normalized, (position - _target.transform.position).normalized, EvadeAwayShare);
                _evadeDirection = direction.normalized;
            }
            Drive(position, position + _evadeDirection * _scale.EvadeDistance, 1f);
        }

        private void Drive(Vector3 position, Vector3 worldTarget, float throttle)
        {
            Vector3 toTarget = worldTarget - position;
            Vector3 direction = toTarget.sqrMagnitude < 1e-4f ? transform.forward : toTarget.normalized;
            _movement.SetControl(direction, direction * Mathf.Clamp01(throttle), false);
        }

        /// <summary>
        /// InterceptPoint is where a bolt of <paramref name="projectileSpeed"/> from <paramref name="shooter"/> meets a target
        /// at <paramref name="targetPosition"/> moving at <paramref name="targetVelocity"/> (the earliest non-negative time).
        /// False, with <paramref name="point"/> the target's position, when there is no solution (the target outruns the
        /// bolt, or the speeds are equal) or the speed is not positive and finite.
        /// </summary>
        public static bool InterceptPoint(Vector3 shooter, float projectileSpeed, Vector3 targetPosition, Vector3 targetVelocity, out Vector3 point)
        {
            point = targetPosition;
            if (float.IsInfinity(projectileSpeed) || float.IsNaN(projectileSpeed) || projectileSpeed <= 0f)
                return false;
            Vector3 toTarget = targetPosition - shooter;
            float a = Vector3.Dot(targetVelocity, targetVelocity) - projectileSpeed * projectileSpeed;
            float b = 2f * Vector3.Dot(toTarget, targetVelocity);
            float c = Vector3.Dot(toTarget, toTarget);
            if (Mathf.Abs(a) < 0.001f)
                return false;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
                return false;
            float root = Mathf.Sqrt(discriminant);
            float t = (-b - root) / (2f * a);
            if (t < 0f)
                t = (-b + root) / (2f * a);
            if (t < 0f)
                return false;
            point = targetPosition + targetVelocity * t;
            return true;
        }

        /// <summary>
        /// ApplyAimError turns <paramref name="direction"/> by an angle drawn in [0, <paramref name="maxDegrees"/>) about an
        /// axis rolled uniformly around it (two draws from <paramref name="random"/>); the result is a unit vector.
        /// </summary>
        public static Vector3 ApplyAimError(Vector3 direction, System.Random random, float maxDegrees)
        {
            if (random == null)
                throw new System.ArgumentNullException(nameof(random));
            Vector3 unit = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
            float error = (float)random.NextDouble() * maxDegrees;
            float roll = (float)random.NextDouble() * 360f;
            Vector3 axis = Vector3.Cross(unit, Mathf.Abs(Vector3.Dot(unit, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            return (Quaternion.AngleAxis(roll, unit) * (Quaternion.AngleAxis(error, axis) * unit)).normalized;
        }

        /// <summary>InsetRectangle shrinks <paramref name="rect"/> by <paramref name="share"/> of its width on each X side and of its height on each Z side.</summary>
        public static Rect InsetRectangle(Rect rect, float share)
        {
            float dx = rect.width * share, dz = rect.height * share;
            return Rect.MinMaxRect(rect.xMin + dx, rect.yMin + dz, rect.xMax - dx, rect.yMax - dz);
        }

        /// <summary>
        /// IsOutsideRectangle is true when <paramref name="position"/>'s X/Z lies outside <paramref name="rect"/>, or will
        /// within <paramref name="horizonSeconds"/> at <paramref name="velocity"/>.
        /// </summary>
        public static bool IsOutsideRectangle(Rect rect, Vector3 position, Vector3 velocity, float horizonSeconds)
        {
            Vector3 ahead = position + velocity * horizonSeconds;
            return !Inside(rect, position) || !Inside(rect, ahead);
        }

        private static bool Inside(Rect rect, Vector3 point)
        {
            return point.x >= rect.xMin && point.x <= rect.xMax && point.z >= rect.yMin && point.z <= rect.yMax;
        }
    }
}
