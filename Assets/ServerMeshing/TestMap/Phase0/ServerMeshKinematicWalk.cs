// Server Meshing add-on — rig kinematic walk (MESH_LOAD_MOTION=kinematic or inert): moves the load generator's kinematic synthetic players by their transforms and heals every synthetic player it holds.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshKinematicWalk is the one loop over the load generator's synthetic players that have no brain.
    /// A walking (kinematic) player first waits until the kit's movement has started: its movement state reports grounded
    /// at least <see cref="StartFrames"/> frames after it was handed over, so the movement component's deferred Start has
    /// run and the kit's handler sends its state (switched off earlier, the player would send no movement at all). It then
    /// gets this loop's own movement-disable key, its movement component switched off, and its transform moved at
    /// <see cref="SpeedRate"/> times its move speed along a heading that changes on the brain's schedule and reflects off
    /// the wander area's edges; its height is kept. A player whose movement has not started after
    /// <see cref="StartTimeoutSeconds"/> is handed to the brain instead and stays steered. A standing (inert) player is
    /// never moved. Every player held is healed below a third of its health on the brain's schedule, so the load does not
    /// die to monsters and stop streaming.
    /// </summary>
    public class ServerMeshKinematicWalk : MonoBehaviour
    {
        /// <summary>SpeedRate is the share of its move speed a walking player moves at.</summary>
        public const float SpeedRate = 0.9f;
        /// <summary>StartTimeoutSeconds is how long a walking player may wait for the kit's movement to start before it is steered instead.</summary>
        public const float StartTimeoutSeconds = 10f;
        /// <summary>StartFrames is how many frames after the hand-over the kit's movement is first trusted to have started.</summary>
        public const int StartFrames = 2;

        private const float MinThinkSeconds = 2f;
        private const float ThinkSpreadSeconds = 4f;
        private const float FirstThinkSpreadSeconds = 3f;

        private sealed class ServerMeshWalkingBot
        {
            public BasePlayerCharacterEntity entity;
            public System.Random random;
            public int seed;
            public bool walks;
            public bool started;
            public int readyFrame;
            public float startDeadline;
            public float nextThink;
            public Vector2 heading;
            public Quaternion rotation;
        }

        private readonly List<ServerMeshWalkingBot> _bots = new List<ServerMeshWalkingBot>();
        private Rect _area;
        private bool _fight = ServerMeshLoadMotion.DefaultFight;
        private int _waiting;
        private int _walking;
        private int _steered;

        /// <summary>Setup gives the loop the wander area (scene frame, y is z) and the fight flag a player that falls back to the brain gets.</summary>
        public void Setup(Rect area, bool fight)
        {
            _area = area;
            _fight = fight;
        }

        /// <summary>AddWalking hands over a kinematic player: it walks by transform once the kit's movement has started, and is healed.</summary>
        public void AddWalking(BasePlayerCharacterEntity entity, int seed)
        {
            ServerMeshWalkingBot bot = Add(entity, seed, true);
            bot.readyFrame = Time.frameCount + StartFrames;
            bot.startDeadline = Time.unscaledTime + StartTimeoutSeconds;
            Turn(bot);
            ++_waiting;
        }

        /// <summary>AddStanding hands over an inert player: it keeps its movement component and stands, and is healed.</summary>
        public void AddStanding(BasePlayerCharacterEntity entity, int seed)
        {
            Add(entity, seed, false);
        }

        /// <summary>
        /// StepWithin moves position by distance along heading (x, z) and folds it back into area (scene frame, y is z) with
        /// the brain's Reflect, turning the heading's component back inside on the axis that left; the height is kept.
        /// </summary>
        public static Vector3 StepWithin(Vector3 position, ref Vector2 heading, float distance, Rect area)
        {
            position.x += heading.x * distance;
            position.z += heading.y * distance;
            if (position.x < area.xMin || position.x > area.xMax)
            {
                heading.x = position.x < area.xMin ? Mathf.Abs(heading.x) : -Mathf.Abs(heading.x);
                position.x = SyntheticPlayerBrain.Reflect(position.x, area.xMin, area.xMax);
            }
            if (position.z < area.yMin || position.z > area.yMax)
            {
                heading.y = position.z < area.yMin ? Mathf.Abs(heading.y) : -Mathf.Abs(heading.y);
                position.z = SyntheticPlayerBrain.Reflect(position.z, area.yMin, area.yMax);
            }
            return position;
        }

        /// <summary>StartedLine is the line logged once every walking player has started or fallen back to the brain; negative or NaN values print as 0.</summary>
        public static string StartedLine(int walking, int steered, float timeoutSeconds)
        {
            float timeout = timeoutSeconds > 0f && !float.IsInfinity(timeoutSeconds) ? timeoutSeconds : 0f;
            return System.FormattableString.Invariant(
                $"Kinematic walk: {System.Math.Max(0, walking)} synthetic players walk by transform at {SpeedRate:0.00} x move speed; {System.Math.Max(0, steered)} stay steered (no grounded kit movement within {timeout:0.0} s)");
        }

        private ServerMeshWalkingBot Add(BasePlayerCharacterEntity entity, int seed, bool walks)
        {
            if (entity == null)
                throw new System.ArgumentNullException(nameof(entity));
            ServerMeshWalkingBot bot = new ServerMeshWalkingBot { entity = entity, random = new System.Random(seed), seed = seed, walks = walks };
            bot.nextThink = Time.unscaledTime + (float)bot.random.NextDouble() * FirstThinkSpreadSeconds;
            _bots.Add(bot);
            return bot;
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            float deltaTime = Time.deltaTime;
            int frame = Time.frameCount;
            for (int i = _bots.Count - 1; i >= 0; --i)
            {
                ServerMeshWalkingBot bot = _bots[i];
                if (bot.entity == null || bot.entity.IsDestroyed)
                {
                    if (bot.walks && !bot.started)
                        StopWaiting();
                    RemoveAt(i);
                    continue;
                }
                if (now >= bot.nextThink)
                    Think(bot, now);
                if (!bot.walks)
                    continue;
                if (!bot.started)
                {
                    if (!TryStart(bot, frame, now))
                        RemoveAt(i);
                    continue;
                }
                if (!bot.entity.IsDead())
                    Walk(bot, deltaTime);
            }
        }

        private void Think(ServerMeshWalkingBot bot, float now)
        {
            bot.nextThink = now + MinThinkSeconds + (float)bot.random.NextDouble() * ThinkSpreadSeconds;
            SyntheticPlayerBrain.HealWhenLow(bot.entity);
            if (bot.walks)
                Turn(bot);
        }

        private static void Turn(ServerMeshWalkingBot bot)
        {
            float angle = (float)bot.random.NextDouble() * Mathf.PI * 2f;
            bot.heading = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            bot.rotation = Quaternion.LookRotation(new Vector3(bot.heading.x, 0f, bot.heading.y));
        }

        /// <summary>TryStart switches a waiting player to the walk once the kit's movement has started; false when it was handed to the brain instead.</summary>
        private bool TryStart(ServerMeshWalkingBot bot, int frame, float now)
        {
            BasePlayerCharacterEntity entity = bot.entity;
            if (frame >= bot.readyFrame && !entity.Movement.IsNull() && entity.MovementState.Has(MovementState.IsGrounded))
            {
                entity.StopMove();
                entity.MovementDisableState.Add(this);
                entity.Movement.enabled = false;
                bot.started = true;
                ++_walking;
                StopWaiting();
                return true;
            }
            if (now < bot.startDeadline)
                return true;
            entity.gameObject.AddComponent<SyntheticPlayerBrain>().Setup(entity, _area, bot.seed, _fight);
            if (++_steered == 1)
                Logging.LogWarning("Mesh", Printable(System.FormattableString.Invariant(
                    $"Kinematic walk: {entity.CharacterName} has no grounded kit movement after {StartTimeoutSeconds:0.0} s; it stays steered with the brain")));
            StopWaiting();
            return false;
        }

        private void Walk(ServerMeshWalkingBot bot, float deltaTime)
        {
            Transform entityTransform = bot.entity.EntityTransform;
            Vector2 heading = bot.heading;
            Vector3 position = StepWithin(entityTransform.position, ref bot.heading, SpeedRate * bot.entity.GetMoveSpeed() * deltaTime, _area);
            if (bot.heading != heading)
                bot.rotation = Quaternion.LookRotation(new Vector3(bot.heading.x, 0f, bot.heading.y));
            entityTransform.SetPositionAndRotation(position, bot.rotation);
        }

        private void StopWaiting()
        {
            if (--_waiting > 0)
                return;
            string line = StartedLine(_walking, _steered, StartTimeoutSeconds);
            if (_steered > 0)
                Logging.LogWarning("Mesh", line);
            else
                Logging.Log("Mesh", line);
        }

        private void RemoveAt(int index)
        {
            int last = _bots.Count - 1;
            _bots[index] = _bots[last];
            _bots.RemoveAt(last);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _bots.Count; ++i)
            {
                ServerMeshWalkingBot bot = _bots[i];
                if (!bot.started || bot.entity == null || bot.entity.IsDestroyed)
                    continue;
                bot.entity.MovementDisableState.Remove(this);
                if (!bot.entity.Movement.IsNull())
                    bot.entity.Movement.enabled = true;
            }
            _bots.Clear();
        }

        private static string Printable(string text) => text.Replace('{', '(').Replace('}', ')');
    }
}
