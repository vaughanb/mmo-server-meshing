// Server Meshing add-on — the other characters keep walking through a hop (DESIGN.md 1.08).
//
// A kept entity gets no state between the warp and the new cell's first one for it (CE-14 keeps the object, the new
// cell's spawn adopts it, and only then do its states flow again). The kit's remote movement finishes its last
// interpolation and pins the character there every frame; the adoption resets its accepted timestamp, and the first
// state after it closes the distance in one interpolation tick. A character walking beside the player stood 45-114 ms
// and then lunged (2.8-10.4 times its frame step) at every hop.
//
// This keeps each kept character that was walking on screen moving at the velocity it was drawn with before the warp
// (the controller's tracker), from the frame the kit stands it, capped at maxSeconds. Before its adoption the old
// connection is closed, so nothing the kit does to it is news: the position is written each frame after the kit's
// update. After the adoption the kit leaves it where this put it, and the first move it makes away from there is the
// new cell's state, which the kit interpolates from here: that is the hand-back.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    internal sealed class MeshKeptExtrapolator
    {
        private sealed class Walker
        {
            public BaseCharacterEntity entity;
            public LiteNetLibIdentity identity;
            public Vector3 velocity;     // m/s on the ground plane, as drawn before the warp
            public Vector3 last;         // where it was at the last sample
            public bool walking;         // being moved by this
            public bool done;            // handed back
            public Vector3 anchor;       // where the kit stood it
            public float since;          // when it was stood
            public Vector3 written;      // what this wrote last
        }

        /// <summary>A step shorter than this (metres) did not move the character.</summary>
        private const float Still = 0.0005f;
        /// <summary>Slower than this (m/s) a character was not walking.</summary>
        private const float MinSpeed = 0.3f;

        private readonly List<Walker> _walkers = new List<Walker>();
        private readonly List<float> _corrections = new List<float>();
        private readonly float _maxSeconds;

        public int Walked { get; private set; }
        public int HandedBack { get; private set; }

        public MeshKeptExtrapolator(IEnumerable<LiteNetLibIdentity> kept, BaseGameEntity player, System.Func<BaseGameEntity, Vector3> velocityOf, float maxSeconds)
        {
            _maxSeconds = maxSeconds;
            foreach (LiteNetLibIdentity identity in kept)
            {
                if (identity == null || !identity.TryGetComponent(out BaseCharacterEntity character) || ReferenceEquals(character, player) || character.IsDead())
                    continue;
                Vector3 velocity = velocityOf(character);
                velocity.y = 0f;
                if (velocity.magnitude < MinSpeed)
                    continue;
                float speed = character.GetMoveSpeed();
                if (speed > 0f && velocity.magnitude > speed * 1.2f)
                    velocity = velocity.normalized * speed * 1.2f; // a catch-up the tracker saw is not a walking speed
                _walkers.Add(new Walker { entity = character, identity = identity, velocity = velocity, last = character.EntityTransform.position });
            }
        }

        /// <summary>One frame, after the kit's update: start, carry on, or hand back each walker.</summary>
        public void Sample()
        {
            for (int i = 0; i < _walkers.Count; ++i)
            {
                Walker w = _walkers[i];
                if (w.done || w.entity == null)
                    continue;
                Transform t = w.entity.EntityTransform;
                Vector3 position = t.position;
                if (!w.walking)
                {
                    // The kit is still finishing its last interpolation; start once it stands.
                    if (Flat(position - w.last) < Still)
                    {
                        w.walking = true;
                        w.anchor = position;
                        w.since = Time.unscaledTime - Time.unscaledDeltaTime; // this frame's step too: the kit stood it in it
                        Walked++;
                        Write(w, t);
                    }
                    w.last = position;
                    continue;
                }
                if (w.identity.IsKept || Flat(position - w.written) < Still)
                {
                    Write(w, t); // not adopted yet (the kit only pins it), or adopted and left alone
                    continue;
                }
                // Taken over by the new cell, and the kit moved it: that cell's first state, interpolated from where this left it.
                w.done = true;
                HandedBack++;
                _corrections.Add(Flat(position - w.written));
            }
        }

        private void Write(Walker w, Transform t)
        {
            float elapsed = Mathf.Min(Time.unscaledTime - w.since, _maxSeconds);
            Vector3 target = w.anchor + w.velocity * elapsed;
            target.y = t.position.y;
            t.position = target;
            w.written = target;
        }

        /// <summary>Hands every walker back where it stands (the hop is over).</summary>
        public void Release()
        {
            for (int i = 0; i < _walkers.Count; ++i)
                _walkers[i].done = true;
        }

        /// <summary>` (first correction max <m>)`: the kit's first step after each hand-back, or empty.</summary>
        public string CorrectionSummary()
        {
            if (_corrections.Count == 0)
                return string.Empty;
            float max = 0f;
            for (int i = 0; i < _corrections.Count; ++i)
                max = Mathf.Max(max, _corrections[i]);
            return $" (first correction max {max:0.###}m)";
        }

        private static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }
    }
}
