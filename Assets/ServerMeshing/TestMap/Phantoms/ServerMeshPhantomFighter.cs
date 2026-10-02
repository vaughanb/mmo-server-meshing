// Server Meshing add-on — rig phantom fighter (MESH_PHANTOM_FIGHT): fighting phantom characters attack through the kit's own server-side handling of a client's attack.
using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomFighter makes the fighting share of phantom characters fight. It never moves them (their uploads do)
    /// and heals them when low, as the load generator does. Target choice is SyntheticPlayerBrain's (the nearest live monster
    /// in reach, on the brain's think schedule). A phantom character is owned by its phantom connection, so the kit's
    /// Attack and SetTargetEntity return at once on the server; the fighter therefore runs, on the server, exactly what the
    /// server runs when a real client attacks: the attack component's CmdAttack (the swing, broadcast to observers), at each
    /// trigger its CmdSimulateActionTrigger, and for a monster in melee reach the hit-registration validation that applies
    /// the damage. The two command methods are protected and are bound once per fighter by reflection; when they are missing
    /// the fighter logs one error and nobody fights.
    /// </summary>
    public class ServerMeshPhantomFighter : MonoBehaviour
    {
        /// <summary>CmdAttackName is the kit's server handler of a client's attack request.</summary>
        public const string CmdAttackName = "CmdAttack";
        /// <summary>CmdTriggerName is the kit's server handler of a client's attack trigger.</summary>
        public const string CmdTriggerName = "CmdSimulateActionTrigger";

        private static readonly MethodInfo s_cmdAttack = typeof(DefaultCharacterAttackComponent).GetMethod(CmdAttackName, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(long), typeof(WeaponHandlingState) }, null);
        private static readonly MethodInfo s_cmdTrigger = typeof(DefaultCharacterAttackComponent).GetMethod(CmdTriggerName, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SimulateActionTriggerData) }, null);

        private sealed class PhantomFighterBinding
        {
            public BasePlayerCharacterEntity entity;
            public DefaultCharacterAttackComponent attack;
            public Action<long, WeaponHandlingState> cmdAttack;
            public Action<SimulateActionTriggerData> cmdTrigger;
            public Action<int> onTrigger;
            public System.Random random;
            public float nextThink;
            public float nextAttack;
            public int simulateSeed;
            public BaseCharacterEntity target;
        }

        private readonly List<PhantomFighterBinding> _fighters = new List<PhantomFighterBinding>();
        private MeshMapNetworkManager _mesh;
        private ServerMeshPhantomWindow _window;
        private IMeshLog _log;
        private bool _refused;

        /// <summary>Fighting is how many phantoms fight now.</summary>
        public int Fighting => _fighters.Count;

        private long _attacks, _triggers, _validations, _kills, _thinks, _dead, _busy, _noTarget, _outOfReach;

        /// <summary>TakeLine is the fighters' line for the last window (attacks started, triggers, hits validated, monsters
        /// that died at a validated hit) and resets the counts.</summary>
        public string TakeLine(double seconds)
        {
            string line = System.FormattableString.Invariant(
                $"Phantom fighters {seconds:0.00} s: fighting {_fighters.Count} attacks {_attacks} triggers {_triggers} validated {_validations} kills {_kills} | thinks {_thinks} dead {_dead} busy {_busy} noTarget {_noTarget} outOfReach {_outOfReach}");
            _attacks = _triggers = _validations = _kills = _thinks = _dead = _busy = _noTarget = _outOfReach = 0;
            return line;
        }

        /// <summary>Begin gives the fighter the server it runs on, the window its time goes to and its log.</summary>
        internal void Begin(MeshMapNetworkManager mesh, ServerMeshPhantomWindow window, IMeshLog log)
        {
            _mesh = mesh;
            _window = window;
            _log = log;
        }

        /// <summary>Add makes a spawned phantom character fight from now on; false (one error logged) when the kit's handlers cannot be bound.</summary>
        internal bool Add(BasePlayerCharacterEntity entity, int seed)
        {
            if (entity == null || _refused)
                return false;
            DefaultCharacterAttackComponent attack = entity.AttackComponent as DefaultCharacterAttackComponent;
            if (s_cmdAttack == null || s_cmdTrigger == null || attack == null)
            {
                _refused = true;
                _log.Error($"Phantoms error: {ServerMeshPhantomSettings.ENV_FIGHT} needs {nameof(DefaultCharacterAttackComponent)}.{CmdAttackName} and {CmdTriggerName} on the player character; none fight");
                return false;
            }
            PhantomFighterBinding fighter = new PhantomFighterBinding
            {
                entity = entity,
                attack = attack,
                cmdAttack = (Action<long, WeaponHandlingState>)Delegate.CreateDelegate(typeof(Action<long, WeaponHandlingState>), attack, s_cmdAttack),
                cmdTrigger = (Action<SimulateActionTriggerData>)Delegate.CreateDelegate(typeof(Action<SimulateActionTriggerData>), attack, s_cmdTrigger),
                random = new System.Random(seed),
            };
            fighter.nextThink = Time.unscaledTime + (float)fighter.random.NextDouble() * 3f;
            fighter.onTrigger = index => OnTrigger(fighter, index);
            attack.OnAttackTrigger += fighter.onTrigger;
            _fighters.Add(fighter);
            return true;
        }

        /// <summary>Remove stops a phantom character fighting.</summary>
        internal void Remove(BasePlayerCharacterEntity entity)
        {
            for (int i = _fighters.Count - 1; i >= 0; --i)
            {
                if (_fighters[i].entity == entity)
                    RemoveAt(i);
            }
        }

        /// <summary>Clear stops everyone fighting (the server stopped).</summary>
        internal void Clear()
        {
            for (int i = _fighters.Count - 1; i >= 0; --i)
                RemoveAt(i);
            _mesh = null;
        }

        private void Update()
        {
            if (_fighters.Count == 0 || _mesh == null)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            float now = Time.unscaledTime;
            for (int i = _fighters.Count - 1; i >= 0; --i)
            {
                PhantomFighterBinding fighter = _fighters[i];
                if (fighter.entity == null || fighter.entity.IsDestroyed || fighter.attack == null)
                {
                    RemoveAt(i);
                    continue;
                }
                if (now < fighter.nextThink)
                    continue;
                fighter.nextThink = now + 2f + (float)fighter.random.NextDouble() * 4f;
                SyntheticPlayerBrain.HealWhenLow(fighter.entity);
                Think(fighter, now);
            }
            if (_window != null)
                _window.BinderMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void Think(PhantomFighterBinding fighter, float now)
        {
            BasePlayerCharacterEntity entity = fighter.entity;
            ++_thinks;
            if (entity.IsDead()) { ++_dead; return; }
            if (fighter.attack.IsAttacking || now < fighter.nextAttack) { ++_busy; return; }
            BaseCharacterEntity target = SyntheticPlayerBrain.FindMonsterInReach(entity, entity.EntityTransform.position, out float distance);
            if (target == null) { ++_noTarget; return; }
            if (distance > SyntheticPlayerBrain.MeleeReach) { ++_outOfReach; return; }
            fighter.nextAttack = now + SyntheticPlayerBrain.AttackIntervalSeconds;
            long timestamp = _mesh.ServerTimestamp;
            fighter.simulateSeed = (int)(timestamp % 16384);
            fighter.target = target;
            fighter.cmdAttack(timestamp, WeaponHandlingState.None);
            ++_attacks;
        }

        private void OnTrigger(PhantomFighterBinding fighter, int triggerIndex)
        {
            BasePlayerCharacterEntity entity = fighter.entity;
            if (_mesh == null || entity == null || entity.IsDestroyed || triggerIndex < 0 || triggerIndex > byte.MaxValue)
                return;
            ++_triggers;
            fighter.cmdTrigger(new SimulateActionTriggerData
            {
                simulateSeed = fighter.simulateSeed,
                triggerIndex = (byte)triggerIndex,
                aimPosition = entity.AimPosition,
            });
            BaseCharacterEntity target = fighter.target;
            if (target == null || target.IsDead() || target.HitBoxes == null || target.HitBoxes.Length == 0)
                return;
            Vector3 origin = entity.EntityTransform.position;
            Vector3 hitOrigin = target.HitBoxes[0].CacheTransform.position;
            if ((hitOrigin - origin).sqrMagnitude > SyntheticPlayerBrain.MeleeReach * SyntheticPlayerBrain.MeleeReach * 4f)
                return;
            long now = _mesh.ServerTimestamp;
            _mesh.HitRegistrationManager.PerformValidation(entity, new HitRegisterData
            {
                SimulateSeed = fighter.simulateSeed,
                TriggerIndex = (byte)triggerIndex,
                SpreadIndex = 0,
                LaunchTimestamp = now,
                Origin = origin,
                Direction = (hitOrigin - origin).normalized,
                HitTimestamp = now,
                HitObjectId = target.ObjectId,
                HitBoxIndex = 0,
                HitOrigin = hitOrigin,
            });
            ++_validations;
            if (target.IsDead())
                ++_kills;
        }

        private void RemoveAt(int index)
        {
            PhantomFighterBinding fighter = _fighters[index];
            if (fighter.attack != null)
                fighter.attack.OnAttackTrigger -= fighter.onTrigger;
            int last = _fighters.Count - 1;
            _fighters[index] = _fighters[last];
            _fighters.RemoveAt(last);
        }
    }
}
