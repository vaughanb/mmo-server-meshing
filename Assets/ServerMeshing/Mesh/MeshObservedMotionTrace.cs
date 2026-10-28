// Server Meshing add-on — how other characters move on this client between its own hops (DESIGN.md 1.09).
//
// With MESH_TRACE_OBSERVED=1 a client follows every other character within Range metres of its own each frame (not
// during its own hops: MeshKeptMotionMeter covers those) and logs, tag MeshObserved, each stall of a walking character
// (it stood at least StallMs and then walked on) and each lunge (a frame several times faster than it was walking),
// with the time and where it happened. Matched against the cells' Handoff lines for that character, a stall or lunge
// at its crossing is what someone watching it cross sees. Off by default.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    internal sealed class MeshObservedMotionTrace
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("MESH_TRACE_OBSERVED") == "1";

        private const float Range = 30f;
        private const float StandStep = 0.002f;     // metres a frame: standing
        private const float WalkingSpeed = 1f;      // m/s: below this a character is not walking
        private const float StallMs = 30f;          // a stand at least this long between walking is a stall
        private const float LungeFactor = 2.5f;     // a frame this many times the walking speed is a lunge
        private const float SmoothingSeconds = 0.25f;
        private const float WarmUpMs = 1000f;       // the smoothed speed starts at 0: a second to settle

        private sealed class Watched
        {
            public string name;
            public Vector3 last;
            public float speed;                     // smoothed walking speed, m/s
            public float watchedMs;                 // how long it has been followed: nothing is judged before WarmUpMs
            public float standMs;
            public float speedBeforeStand;
            public Vector3 standAt;
        }

        private readonly Dictionary<int, Watched> _watched = new Dictionary<int, Watched>();
        private readonly List<int> _gone = new List<int>();
        private readonly HashSet<int> _seen = new HashSet<int>();

        /// <summary>One frame: every character near <paramref name="player"/> except it.</summary>
        public void Sample(LiteNetLibAssets assets, BaseGameEntity player)
        {
            if (assets == null || player == null)
                return;
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
                return;
            Vector3 center = player.EntityTransform.position;
            _seen.Clear();
            Dictionary<uint, LiteNetLibIdentity>.Enumerator it = assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                LiteNetLibIdentity identity = it.Current.Value;
                if (identity == null || !identity.TryGetComponent(out BaseCharacterEntity character) || ReferenceEquals(character, player))
                    continue;
                Vector3 position = character.EntityTransform.position;
                if ((position - center).sqrMagnitude > Range * Range)
                    continue;
                int key = character.GetInstanceID();
                _seen.Add(key);
                if (!_watched.TryGetValue(key, out Watched w))
                {
                    _watched[key] = new Watched { name = string.IsNullOrEmpty(character.CharacterName) ? character.name : character.CharacterName, last = position };
                    continue;
                }
                Vector3 delta = position - w.last;
                delta.y = 0f;
                float step = delta.magnitude;
                float speed = step / dt;
                w.last = position;
                w.watchedMs += dt * 1000f;
                bool judged = w.watchedMs >= WarmUpMs;
                if (step < StandStep)
                {
                    if (w.standMs == 0f)
                    {
                        w.speedBeforeStand = w.speed;
                        w.standAt = position;
                    }
                    w.standMs += dt * 1000f;
                    continue;
                }
                if (judged && w.standMs >= StallMs && w.speedBeforeStand >= WalkingSpeed)
                    Logging.Log("MeshObserved", $"{System.DateTime.Now:HH:mm:ss.fff} {w.name} stalled {w.standMs:0} ms at ({w.standAt.x:0.0}, {w.standAt.z:0.0}) walking {w.speedBeforeStand:0.0} m/s, then {speed:0.0} m/s");
                w.standMs = 0f;
                if (judged && w.speed >= WalkingSpeed && speed > w.speed * LungeFactor)
                    Logging.Log("MeshObserved", $"{System.DateTime.Now:HH:mm:ss.fff} {w.name} lunged {speed:0.0} m/s ({speed / w.speed:0.0}x, {step:0.###} m) at ({position.x:0.0}, {position.z:0.0})");
                w.speed = Mathf.Lerp(w.speed, speed, 1f - Mathf.Exp(-dt / SmoothingSeconds));
            }
            if (_watched.Count > _seen.Count)
            {
                _gone.Clear();
                foreach (int key in _watched.Keys)
                    if (!_seen.Contains(key)) _gone.Add(key);
                for (int i = 0; i < _gone.Count; ++i)
                    _watched.Remove(_gone[i]);
            }
        }

        /// <summary>The player's own hop moves everything: start over after it.</summary>
        public void Reset() => _watched.Clear();
    }
}
