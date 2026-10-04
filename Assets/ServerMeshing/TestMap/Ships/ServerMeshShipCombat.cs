// Server Meshing test rig — the stand-in ship's combat (Phase 0 step 7), modelled on the space game's
// ShipCombatComponent at commit 1a3a58d9 (read there, not copied): a shield sync field recharged every frame, a
// destroyed flag, the hull as the kit's CurrentHp, [AllRpc] fire and impact calls, two bolts per server shot, and a
// 10 s respawn. The eight load fields exist for ServerMeshSyncLoad only and never change otherwise.
using System;
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipCombat owns the ship's damage on the server: TakeShipDamage takes the shield first and the rest off
    /// the hull; at hull 0 the ship is destroyed and comes back 10 s later with full hull and shield, teleported to its
    /// spawn point with the next epoch (its pilot stays seated). ServerFire is the AI's gun. Every RPC it sends adds the
    /// sends it expects to the Ships window's rpcSends.
    /// </summary>
    public sealed class ServerMeshShipCombat : LiteNetLibBehaviour
    {
        /// <summary>ShieldCapacity is the full shield.</summary>
        public const float ShieldCapacity = 2500f;
        /// <summary>ShieldRechargeRate is the shield regained per second while recharging.</summary>
        public const float ShieldRechargeRate = 100f;
        /// <summary>ShieldRechargeDelay is how long after the last damage the shield starts recharging, in seconds.</summary>
        public const float ShieldRechargeDelay = 2f;
        /// <summary>FireIntervalSeconds is the shortest time between two server shots (unscaled time).</summary>
        public const float FireIntervalSeconds = 0.175f;
        /// <summary>RespawnSeconds is how long a destroyed ship stays destroyed.</summary>
        public const float RespawnSeconds = 10f;
        /// <summary>BoltDamage is what one bolt takes off a ship of the other team.</summary>
        public const int BoltDamage = 50;
        /// <summary>LoadFieldCount is the number of load sync fields.</summary>
        public const int LoadFieldCount = 8;
        /// <summary>ShotMemorySeconds is how long a shot record is kept beyond a bolt's flight time.</summary>
        public const float ShotMemorySeconds = 2f;

        [SerializeField]
        private SyncFieldFloat shield = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldBool destroyed = new SyncFieldBool();
        [SerializeField]
        private SyncFieldFloat load0 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load1 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load2 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load3 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load4 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load5 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load6 = new SyncFieldFloat();
        [SerializeField]
        private SyncFieldFloat load7 = new SyncFieldFloat();

        private readonly Dictionary<int, double> _shots = new Dictionary<int, double>(64);
        private readonly List<int> _staleShots = new List<int>(64);
        private ServerMeshShipEntity _ship;
        private ServerMeshShipMovement _movement;
        private SyncFieldFloat[] _loads;
        private double _lastFire = double.NegativeInfinity;
        private double _lastDamage = double.NegativeInfinity;
        private double _respawnAt;
        private int _fireSequence;
        private Vector3 _aimDirection;
        private float _aimDistance;

        /// <summary>ShipDestroyed is true from hull 0 until the respawn.</summary>
        public bool ShipDestroyed => destroyed.Value;

        /// <summary>Shield is the current shield.</summary>
        public float Shield => shield.Value;

        /// <summary>Team is the ship's team (0 or 1).</summary>
        public int Team => _ship != null ? _ship.Team : 0;

        /// <summary>CanFireNow is true on the server for a live ship whose last shot is at least FireIntervalSeconds old.</summary>
        public bool CanFireNow => IsServer && !destroyed.Value && RateAllows(Time.unscaledTimeAsDouble, _lastFire, FireIntervalSeconds);

        private void Awake()
        {
            _ship = GetComponent<ServerMeshShipEntity>();
            _movement = GetComponent<ServerMeshShipMovement>();
            _loads = new[] { load0, load1, load2, load3, load4, load5, load6, load7 };
        }

        public override void OnIdentityInitialize()
        {
            base.OnIdentityInitialize();
            _shots.Clear();
            _lastFire = _lastDamage = double.NegativeInfinity;
            _respawnAt = 0;
            _fireSequence = 0;
            _aimDirection = Vector3.zero;
            _aimDistance = 0f;
            if (!IsServer)
                return;
            shield.Value = ShieldCapacity;
            destroyed.Value = false;
            for (int i = 0; i < _loads.Length; ++i)
                _loads[i].Value = 0f;
        }

        /// <summary>SetAim sets where the next server shot goes: <paramref name="direction"/> from the ship, the aim point <paramref name="distance"/> metres out.</summary>
        public void SetAim(Vector3 direction, float distance)
        {
            _aimDirection = direction;
            _aimDistance = distance;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Time.unscaledTimeAsDouble;
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            if (destroyed.Value)
            {
                if (RespawnDue(now, _respawnAt))
                    Respawn(counters);
            }
            else
            {
                float next = RechargeShield(shield.Value, ShieldCapacity, ShieldRechargeRate, now - _lastDamage, Time.deltaTime);
                if (next != shield.Value)
                {
                    shield.Value = next;
                    counters.ShieldWrites++;
                }
            }
            counters.CombatMs += Elapsed(started);
        }

        private void Respawn(ServerMeshShipCounters counters)
        {
            if (_ship != null)
                _ship.CurrentHp = _ship.HullMax;
            shield.Value = ShieldCapacity;
            counters.ShieldWrites++;
            if (_movement != null && _ship != null)
            {
                _movement.TeleportShip(_ship.SpawnPoint, _ship.SpawnRotation);
                _movement.AdvanceEpoch();
            }
            destroyed.Value = false;
            counters.Respawns++;
        }

        /// <summary>
        /// ServerFire is a server shot: false while destroyed or within FireIntervalSeconds of the last one; otherwise it
        /// records the shot (purging records older than a bolt's flight plus ShotMemorySeconds), sends RpcFired to every
        /// subscriber and the owner, and launches one bolt per barrel along the aim, carrying the ship's velocity.
        /// </summary>
        public bool ServerFire()
        {
            if (!CanFireNow)
                return false;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            double now = Time.unscaledTimeAsDouble;
            _lastFire = now;
            int seq = _fireSequence++;
            _shots[seq] = now;
            PurgeStaleShots(now);
            counters.RpcSends += ExpectedSends();
            RPC(RpcFired, seq);
            counters.Shots++;
            LaunchBolts(counters);
            counters.CombatMs += Elapsed(started);
            return true;
        }

        private void LaunchBolts(ServerMeshShipCounters counters)
        {
            if (_ship == null || _ship.Scale == null)
                return;
            ServerMeshShipScale scale = _ship.Scale;
            Transform self = transform;
            Vector3 forward = self.forward;
            Vector3 aim = _aimDirection.sqrMagnitude > 1e-8f ? _aimDirection.normalized : forward;
            float distance = _aimDistance > 0f ? _aimDistance : scale.MaxFiringDistance;
            Vector3 aimPoint = self.position + aim * distance;
            Vector3 inherited = _movement != null ? _movement.CurrentVelocity : Vector3.zero;
            bool pooled = _ship.Settings != null && _ship.Settings.PooledProjectiles;
            for (int i = 0; i < _ship.BarrelCount; ++i)
            {
                Transform barrel = _ship.Barrel(i);
                if (barrel == null)
                    continue;
                Vector3 origin = barrel.position;
                Vector3 velocity = BoltVelocity(aimPoint - origin, scale.ProjectileSpeed, inherited);
                ServerMeshShipProjectile.Launch(this, origin, velocity, scale.ProjectileRange, self, pooled);
                counters.Bolts++;
            }
        }

        private void PurgeStaleShots(double now)
        {
            double maxAge = (_ship != null && _ship.Scale != null ? _ship.Scale.ProjectileRange / _ship.Scale.ProjectileSpeed : 1.25) + ShotMemorySeconds;
            _staleShots.Clear();
            foreach (KeyValuePair<int, double> shot in _shots)
            {
                if (now - shot.Value > maxAge)
                    _staleShots.Add(shot.Key);
            }
            for (int i = 0; i < _staleShots.Count; ++i)
                _shots.Remove(_staleShots[i]);
        }

        /// <summary>
        /// TakeShipDamage applies <paramref name="amount"/> on the server to a live ship: shield first, the rest off the hull
        /// (never below 0); at hull 0 the ship is destroyed and its respawn is due RespawnSeconds later.
        /// </summary>
        public void TakeShipDamage(int amount)
        {
            if (!IsServer || destroyed.Value || _ship == null)
                return;
            double now = Time.unscaledTimeAsDouble;
            _lastDamage = now;
            bool destroyedNow = ApplyDamage(shield.Value, _ship.CurrentHp, amount, out float newShield, out int newHull);
            if (newShield != shield.Value)
            {
                shield.Value = newShield;
                ServerMeshShipCounters.Current.ShieldWrites++;
            }
            if (newHull != _ship.CurrentHp)
                _ship.CurrentHp = newHull;
            if (!destroyedNow)
                return;
            destroyed.Value = true;
            _respawnAt = RespawnTime(now);
        }

        /// <summary>ImpactAt sends RpcImpact for a hit at <paramref name="point"/> and counts its expected sends.</summary>
        public void ImpactAt(Vector3 point)
        {
            if (!IsServer)
                return;
            ServerMeshShipCounters.Current.RpcSends += ExpectedSends();
            RPC(RpcImpact, point);
        }

        /// <summary>
        /// FireRpcForLoad sends one RpcFired for ServerMeshSyncLoad and returns the sends it expects (counted by the sync
        /// load, not by the Ships window); 0 without sending anything off the server.
        /// </summary>
        public int FireRpcForLoad(int seq)
        {
            if (!IsServer)
                return 0;
            int sends = ExpectedSends();
            RPC(RpcFired, seq);
            return sends;
        }

        /// <summary>WriteLoadField sets load field <paramref name="index"/> (0..7) on the server (ServerMeshSyncLoad only).</summary>
        public void WriteLoadField(int index, float value)
        {
            if (index < 0 || index >= LoadFieldCount)
                throw new ArgumentOutOfRangeException(nameof(index), index, "a ship has load fields 0..7");
            if (IsServer)
                _loads[index].Value = value;
        }

        [AllRpc]
        private void RpcFired(int seq)
        {
            // Headless rig processes have no visuals; a dedicated server runs this body too and returns at once.
            if (IsServer && !IsClient)
                return;
        }

        [AllRpc]
        private void RpcImpact(Vector3 point)
        {
            if (IsServer && !IsClient)
                return;
        }

        private int ExpectedSends()
        {
            LiteNetLibIdentity identity = Identity;
            if (identity == null)
                return 0;
            long owner = identity.ConnectionId;
            return ExpectedRpcSends(identity.CountSubscribers(), owner >= 0, owner >= 0 && identity.HasSubscriber(owner));
        }

        /// <summary>
        /// ApplyDamage takes <paramref name="amount"/> (negative counts as 0) from <paramref name="shield"/> first and the
        /// rest, rounded, from <paramref name="hull"/>, never below 0. True when this damage took the hull to 0.
        /// </summary>
        public static bool ApplyDamage(float shield, int hull, int amount, out float newShield, out int newHull)
        {
            float remaining = Mathf.Max(0, amount);
            float absorbed = Mathf.Min(Mathf.Max(0f, shield), remaining);
            newShield = shield - absorbed;
            remaining -= absorbed;
            if (remaining <= 0f)
            {
                newHull = hull;
                return false;
            }
            newHull = Mathf.Max(0, hull - Mathf.RoundToInt(remaining));
            return newHull <= 0 && hull > 0;
        }

        /// <summary>
        /// RechargeShield is the shield after one frame of <paramref name="deltaTime"/>: unchanged while full or until
        /// ShieldRechargeDelay has passed since the last damage, else raised by rate·dt and capped at <paramref name="capacity"/>.
        /// </summary>
        public static float RechargeShield(float shield, float capacity, float rate, double secondsSinceDamage, float deltaTime)
        {
            if (shield >= capacity || secondsSinceDamage <= ShieldRechargeDelay || deltaTime <= 0f)
                return shield;
            return Mathf.Min(capacity, shield + rate * deltaTime);
        }

        /// <summary>RespawnTime is when a ship destroyed at <paramref name="destroyedAt"/> comes back.</summary>
        public static double RespawnTime(double destroyedAt) => destroyedAt + RespawnSeconds;

        /// <summary>RespawnDue is true once <paramref name="now"/> reaches <paramref name="respawnAt"/>.</summary>
        public static bool RespawnDue(double now, double respawnAt) => now >= respawnAt;

        /// <summary>RateAllows is true when at least <paramref name="interval"/> seconds passed since <paramref name="lastFire"/>.</summary>
        public static bool RateAllows(double now, double lastFire, double interval) => now - lastFire >= interval;

        /// <summary>
        /// ExpectedRpcSends is the messages one [AllRpc] call sends from the server: one per subscriber, plus one for an owner
        /// connection that does not subscribe.
        /// </summary>
        public static int ExpectedRpcSends(int subscribers, bool hasOwner, bool ownerSubscribes)
        {
            return Math.Max(0, subscribers) + (hasOwner && !ownerSubscribes ? 1 : 0);
        }

        /// <summary>
        /// BoltVelocity is a bolt's world velocity: the shooter's velocity plus a barrel direction that cancels most of its
        /// lateral part, so the bolt flies nearly parallel to <paramref name="toAimPoint"/>. It is the space ship's
        /// velocity-compensated aim, copied with its approximation: normalizing leaves lateral·(1 − s/√(s² + lateral²)).
        /// </summary>
        public static Vector3 BoltVelocity(Vector3 toAimPoint, float projectileSpeed, Vector3 shooterVelocity)
        {
            Vector3 desired = toAimPoint.sqrMagnitude > 1e-8f ? toAimPoint.normalized : Vector3.forward;
            Vector3 lateral = Vector3.ProjectOnPlane(shooterVelocity, desired);
            Vector3 direction = (desired * projectileSpeed - lateral).normalized;
            return direction * projectileSpeed + shooterVelocity;
        }

        private static double Elapsed(long started)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
