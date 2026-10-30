// Server Meshing add-on — a monster's walk goes on through its handoff (DESIGN.md 1.28).
//
// A monster handoff carried the monster's position, health, spawn point and chase target. A monster walking over the
// line on its own (wandering, or going back to its spawn point) arrived with none of its walk: the new cell's NavMesh
// agent had no path, and its AI started from a fresh state (no wander countdown, its spawn point never reached), so the
// monster stood at the line for 0.4-3.6 s before the AI walked it on: a wandering monster always paused exactly at a
// cell border. The handoff now carries where the monster was walking to and how (walking or running), and the AI's
// own bookkeeping through IMeshMonsterAiCarrier, which the add-on's monster AI components implement.
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// IMeshMonsterAiCarrier is a monster AI component whose state crosses a monster handoff: the origin writes it as the
    /// monster leaves, the destination reads it into the promoted monster's component of the same kind.
    /// </summary>
    public interface IMeshMonsterAiCarrier
    {
        /// <summary>WriteAiState writes the AI's state for a handoff.</summary>
        void WriteAiState(NetDataWriter writer);

        /// <summary>ReadAiState reads back what WriteAiState wrote (on the same build).</summary>
        void ReadAiState(NetDataReader reader);
    }

    /// <summary>
    /// MeshMonsterAiState is the part of the kit's MonsterActivityComponent state that decides what the monster does next:
    /// whether it got back to its spawn point, its wander countdown and delay, and how long it has followed an enemy.
    /// </summary>
    public struct MeshMonsterAiState
    {
        public bool reachedSpawnPoint;
        public float wanderCountDown;
        public float wanderDelay;
        public float followEnemyElapsed;

        public void Write(NetDataWriter writer)
        {
            writer.Put(reachedSpawnPoint);
            writer.Put(wanderCountDown);
            writer.Put(wanderDelay);
            writer.Put(followEnemyElapsed);
        }

        public static MeshMonsterAiState Read(NetDataReader reader)
        {
            return new MeshMonsterAiState
            {
                reachedSpawnPoint = reader.GetBool(),
                wanderCountDown = reader.GetFloat(),
                wanderDelay = reader.GetFloat(),
                followEnemyElapsed = reader.GetFloat(),
            };
        }
    }

    /// <summary>MeshMonsterMotion reads and resumes a monster's walk across a handoff.</summary>
    public static class MeshMonsterMotion
    {
        /// <summary>Paths that end closer than this to the monster are done: nothing to carry.</summary>
        public const float ArrivedDistance = 0.2f;

        /// <summary>
        /// TryGetDestination is where <paramref name="entity"/> is walking to: its NavMesh agent's destination while the
        /// agent has a path it is following, or the last point of its character-controller path. False when it stands or
        /// is about to arrive.
        /// </summary>
        public static bool TryGetDestination(BaseGameEntity entity, out Vector3 destination)
        {
            destination = default;
            if (entity == null)
                return false;
            IEntityMovementComponent movement = entity.ActiveMovement;
            Vector3 position = entity.MovementTransform.position;
            if (movement is NavMeshEntityMovement nav)
            {
                UnityEngine.AI.NavMeshAgent agent = nav.CacheNavMeshAgent;
                if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh || agent.isStopped || (!agent.hasPath && !agent.pathPending))
                    return false;
                destination = agent.destination;
            }
            else if (movement is CharacterControllerEntityMovement controller && controller.HasNavPaths)
            {
                foreach (Vector3 point in controller.NavPaths)
                    destination = point;
            }
            else
            {
                return false;
            }
            Vector3 left = destination - position;
            left.y = 0f;
            return left.magnitude > ArrivedDistance;
        }

        /// <summary>
        /// TryGetAgentVelocity is the flat velocity of <paramref name="entity"/>'s NavMesh agent, which moves it: what its
        /// snapshots and its demoted puppet are led by. A velocity measured between snapshots trailed a monster breaking
        /// into a run by a snapshot or two. False without an active agent on the NavMesh.
        /// </summary>
        public static bool TryGetAgentVelocity(BaseGameEntity entity, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (entity == null || !(entity.ActiveMovement is NavMeshEntityMovement nav))
                return false;
            UnityEngine.AI.NavMeshAgent agent = nav.CacheNavMeshAgent;
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
                return false;
            velocity = agent.velocity;
            velocity.y = 0f;
            return !float.IsNaN(velocity.x) && !float.IsNaN(velocity.z) && velocity.sqrMagnitude < 400f;
        }

        /// <summary>WriteAi writes <paramref name="monster"/>'s AI state when its AI carries one; an empty array otherwise.</summary>
        public static byte[] WriteAi(BaseGameEntity monster)
        {
            IMeshMonsterAiCarrier carrier = monster != null ? monster.GetComponent<IMeshMonsterAiCarrier>() : null;
            if (carrier == null)
                return System.Array.Empty<byte>();
            NetDataWriter writer = new NetDataWriter();
            carrier.WriteAiState(writer);
            return writer.CopyData();
        }

        /// <summary>ReadAi gives <paramref name="ai"/> to <paramref name="monster"/>'s AI; false when either side has none.</summary>
        public static bool ReadAi(BaseGameEntity monster, byte[] ai)
        {
            if (monster == null || ai == null || ai.Length == 0)
                return false;
            IMeshMonsterAiCarrier carrier = monster.GetComponent<IMeshMonsterAiCarrier>();
            if (carrier == null)
                return false;
            carrier.ReadAiState(new NetDataReader(ai));
            return true;
        }
    }
}
