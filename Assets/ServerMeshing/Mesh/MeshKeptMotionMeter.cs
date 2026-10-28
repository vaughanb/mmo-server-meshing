// Server Meshing add-on — how the other characters on screen move through a hop (DESIGN.md 1.08).
//
// The client keeps every entity through a hop (CE-14) and the new cell's spawn adopts it, but a kept entity gets no
// state from the warp until the new cell sends one: a walking character reaches its last target and stands, then
// catches up. This meter follows the kept characters other than the player frame by frame, from the hop until shortly
// after it settles, and reports for each one that was walking the longest stand (frames that did not move it) and its
// fastest frame against its median speed: a freeze shows as a stand, a lunge as a speed many times the median. Speed,
// not step: the editor's frames run 2-16 ms, and a long frame's step alone reads as a lunge.
using System.Collections.Generic;
using System.Text;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    internal sealed class MeshKeptMotionMeter
    {
        private sealed class Track
        {
            public BaseCharacterEntity entity;
            public string name;
            public Vector3 last;
            public readonly List<float> speeds = new List<float>(); // m/s, one a frame
            public float standMs, longestStandMs;
            public float standFromMs, longestStandFromMs; // when the stand began, ms after the hop began
        }

        private float _elapsedMs;

        /// <summary>A step shorter than this (metres) is standing still.</summary>
        public const float StandStep = 0.002f;
        /// <summary>A character whose median speed is lower than this (m/s) was not walking: not reported.</summary>
        public const float WalkingMedian = 1f;

        private readonly List<Track> _tracks = new List<Track>();

        public MeshKeptMotionMeter(IEnumerable<LiteNetLibIdentity> kept, BaseGameEntity player)
        {
            foreach (LiteNetLibIdentity identity in kept)
            {
                if (identity == null || !identity.TryGetComponent(out BaseCharacterEntity character) || ReferenceEquals(character, player))
                    continue;
                _tracks.Add(new Track { entity = character, name = string.IsNullOrEmpty(character.CharacterName) ? character.name : character.CharacterName, last = character.EntityTransform.position });
            }
        }

        public int Count => _tracks.Count;

        /// <summary>One frame of <paramref name="deltaMs"/> milliseconds.</summary>
        public void Sample(float deltaMs)
        {
            if (deltaMs <= 0f)
                return;
            _elapsedMs += deltaMs;
            for (int i = 0; i < _tracks.Count; ++i)
            {
                Track track = _tracks[i];
                if (track.entity == null)
                    continue;
                Vector3 position = track.entity.EntityTransform.position;
                Vector3 delta = position - track.last;
                delta.y = 0f;
                float step = delta.magnitude;
                track.last = position;
                track.speeds.Add(step * 1000f / deltaMs);
                if (step < StandStep)
                {
                    if (track.standMs == 0f)
                        track.standFromMs = _elapsedMs - deltaMs;
                    track.standMs += deltaMs;
                    if (track.standMs > track.longestStandMs)
                    {
                        track.longestStandMs = track.standMs;
                        track.longestStandFromMs = track.standFromMs;
                    }
                }
                else
                    track.standMs = 0f;
            }
        }

        /// <summary>` | others: <name> stand <ms> at +<ms> speed max <m/s> (median <m/s>, <n>x)` per walking character, or ` | others: none walking`.</summary>
        public string Summary()
        {
            StringBuilder sb = new StringBuilder(" | others:");
            int reported = 0;
            for (int i = 0; i < _tracks.Count; ++i)
            {
                Track track = _tracks[i];
                if (track.speeds.Count < 3)
                    continue;
                List<float> sorted = new List<float>(track.speeds);
                sorted.Sort();
                float median = sorted[sorted.Count / 2], max = sorted[sorted.Count - 1];
                if (median < WalkingMedian)
                    continue;
                sb.Append($" {track.name} stand {track.longestStandMs:0}ms at +{track.longestStandFromMs:0}ms speed max {max:0.#}m/s (median {median:0.#}m/s, {max / median:0.#}x);");
                reported++;
            }
            if (reported == 0)
                sb.Append(" none walking");
            return sb.ToString();
        }
    }
}
