using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    /// <summary>
    /// Lets a knockback actually move a monster that is standing still.
    ///
    /// **The kit's knockback on a standing monster moves nothing you can see.** A monster on
    /// `NavMeshEntityMovement` is stopped with `StopMoveFunction`, which sets the agent's
    /// `updatePosition` to false - and the monster AI stops it exactly when it closes to attack,
    /// which is where a player's blow lands. A force then goes through `NavMeshAgent.Move`, which
    /// shifts the agent's *simulation* position; with `updatePosition` off the body stays put.
    /// Worse, the agent keeps the offset: measured 2026-10-01, three test shoves left a wolf's
    /// agent 9.8 m from its body, and the next walk order (which turns `updatePosition` back on)
    /// would have snapped the body across to it. Shield Bash's measured 0.85 m shove on
    /// 2026-09-23 must have landed on a wolf that was still walking in.
    ///
    /// **What this does.** On the server, while a knockback force is on the entity (the kit
    /// applies them with source None / 0), it syncs the agent to the body and turns
    /// `updatePosition` on, so `Move` carries the body; once the force has run out it puts the
    /// flag back the way the kit had it. Reads the force through the movement's public
    /// `FindForceByActionKey`, so the order of the kit's update and this one does not matter:
    /// at worst the first frame of a shove goes to the agent alone and is then synced away.
    ///
    /// Ticks on the entity's own update (<see cref="BaseGameEntity.onUpdate"/>) rather than a
    /// Unity one of its own, and lets go of it on a pure client, where there is nothing to do:
    /// there is one of these on every monster.
    ///
    /// The demo puts it on every monster from <c>DemoEntitySetting</c>. Upstream-worthy as a kit bug.
    /// </summary>
    public class MonsterKnockbackUpkeep : MonoBehaviour
    {
        private BaseCharacterEntity _entity;
        private NavMeshEntityMovement _movement;
        private NavMeshAgent _agent;
        private bool _enabledUpdatePosition;
        private bool _subscribed;

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            if (_entity == null)
                enabled = false;
        }

        private void OnEnable()
        {
            if (_entity == null || _subscribed)
                return;
            _entity.onUpdate += Tick;
            _subscribed = true;
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            _subscribed = false;
            _entity.onUpdate -= Tick;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (!_entity.IsServer)
            {
                // Before the entity spawns it is neither; once it is a client and not the
                // server it never will be, and this has no work there.
                if (_entity.IsClient)
                    Unsubscribe();
                return;
            }
            if (_movement == null)
            {
                _movement = _entity.ActiveMovement as NavMeshEntityMovement;
                if (_movement == null)
                    return;
                _agent = GetComponent<NavMeshAgent>();
            }
            if (_agent == null)
                return;

            bool pushed = _movement.FindForceByActionKey(ApplyMovementForceSourceType.None, 0) != null;
            if (pushed && !_agent.updatePosition)
            {
                // The agent may have drifted from the body already (an earlier shove, a
                // teleport): bring it to the body BEFORE the flag goes on, or the body jumps.
                if (_agent.isOnNavMesh)
                    _agent.nextPosition = transform.position;
                _agent.updatePosition = true;
                _enabledUpdatePosition = true;
            }
            else if (!pushed && _enabledUpdatePosition)
            {
                _enabledUpdatePosition = false;
                _agent.updatePosition = false;
            }
        }
    }
}
