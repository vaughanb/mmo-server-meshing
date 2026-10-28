// Server Meshing add-on — keeps a handoff stand-in animating when its model is animated by a playable graph.
//
// ClientMigrationController's seamless handoff clones every visible model into a script-free stand-in for the swap. A
// model animated by an Animator controller keeps animating on its own; one animated by the kit's PlayableCharacterModel
// (the kit demo's characters and monsters) is driven by a script, so its clone would hold its last pose until the real
// entity is back. This component, added to such a clone, snapshots what the source's base layer was playing (each clip
// with its time, speed and blend weight) and plays the same mix on the clone's Animator with a graph of its own, which
// it destroys with the stand-in. The action layers (attacks, casts) are left out: they need their avatar masks, and a
// stand-in lives about a second.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using PlayableCharacterModel = MultiplayerARPG.GameData.Model.Playables.PlayableCharacterModel;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed class MeshStandInAnimation : MonoBehaviour
    {
        private struct ClipState
        {
            public AnimationClip clip;
            public double time;
            public double speed;
            public float weight;
        }

        private const int MaxDepth = 32;
        private readonly List<ClipState> _clips = new List<ClipState>();
        private Animator _animator;
        private PlayableGraph _graph;

        /// <summary>
        /// Attach adds the component to <paramref name="clone"/> (a copy of <paramref name="sourceModel"/>, not yet active)
        /// when the source is animated by a PlayableCharacterModel whose graph is playing something; otherwise nothing.
        /// </summary>
        public static void Attach(GameObject sourceModel, GameObject clone)
        {
            PlayableCharacterModel model = sourceModel.GetComponentInChildren<PlayableCharacterModel>(true);
            if (model == null || !model.Graph.IsValid())
                return;
            PlayableGraph graph = model.Graph;
            List<ClipState> clips = new List<ClipState>();
            Animator target = null;
            for (int i = 0; i < graph.GetOutputCount(); i++)
            {
                PlayableOutput output = graph.GetOutput(i);
                if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>())
                    continue;
                target = ((AnimationPlayableOutput)output).GetTarget();
                Collect(output.GetSourcePlayable(), 1f, 1.0, clips, 0);
                break;
            }
            if (target == null || clips.Count == 0)
                return;
            Animator cloneAnimator = Counterpart(sourceModel.transform, clone.transform, target.transform);
            if (cloneAnimator == null)
                return;
            MeshStandInAnimation animation = clone.AddComponent<MeshStandInAnimation>();
            animation._animator = cloneAnimator;
            animation._clips.AddRange(clips);
            Logging.Log("Spike2", $"Stand-in {clone.name}: {clips.Count} clip(s) kept playing ({clips[0].clip.name} first)");
        }

        /// <summary>
        /// TryGetStepCadence reads the footfall rhythm off the heaviest clip, a locomotion loop with two footfalls per cycle:
        /// a step every half cycle, and how long ago the last one was (taking a footfall at the start of each half).
        /// False when that clip is not a moving loop.
        /// </summary>
        public bool TryGetStepCadence(out float interval, out float sinceLastStep)
        {
            interval = sinceLastStep = 0f;
            int heaviest = -1;
            for (int i = 0; i < _clips.Count; i++)
            {
                if (heaviest < 0 || _clips[i].weight > _clips[heaviest].weight)
                    heaviest = i;
            }
            if (heaviest < 0)
                return false;
            ClipState state = _clips[heaviest];
            float speed = (float)System.Math.Abs(state.speed);
            if (!state.clip.isLooping || state.clip.length <= 0f || speed <= 0.01f)
                return false;
            double half = state.clip.length * 0.5;
            interval = (float)(half / speed);
            sinceLastStep = (float)((state.time % half + half) % half / speed);
            return true;
        }

        /// <summary>The clips under <paramref name="playable"/> with their effective weight and speed (base layer only).</summary>
        private static void Collect(Playable playable, float weight, double speed, List<ClipState> clips, int depth)
        {
            if (!playable.IsValid() || weight <= 0.001f || depth > MaxDepth)
                return;
            speed *= playable.GetSpeed();
            if (playable.IsPlayableOfType<AnimationClipPlayable>())
            {
                AnimationClip clip = ((AnimationClipPlayable)playable).GetAnimationClip();
                if (clip != null)
                    clips.Add(new ClipState { clip = clip, time = playable.GetTime(), speed = speed, weight = weight });
                return;
            }
            bool layerMixer = playable.IsPlayableOfType<AnimationLayerMixerPlayable>();
            bool mixer = layerMixer || playable.IsPlayableOfType<AnimationMixerPlayable>();
            int inputs = playable.GetInputCount();
            for (int i = 0; i < inputs; i++)
            {
                if (layerMixer && i > 0)
                    break; // base layer only
                float inputWeight = mixer ? playable.GetInputWeight(i) : 1f;
                Collect(playable.GetInput(i), weight * inputWeight, speed, clips, depth + 1);
            }
        }

        /// <summary>The clone's Animator at the same place in the hierarchy as <paramref name="target"/> under the source.</summary>
        private static Animator Counterpart(Transform sourceRoot, Transform cloneRoot, Transform target)
        {
            List<int> path = new List<int>();
            Transform t = target;
            while (t != null && t != sourceRoot)
            {
                path.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            if (t == null)
                return cloneRoot.GetComponentInChildren<Animator>(true); // the Animator sits above the model: take the clone's own
            Transform c = cloneRoot;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i] >= c.childCount)
                    return null;
                c = c.GetChild(path[i]);
            }
            return c.GetComponent<Animator>();
        }

        private void OnEnable()
        {
            if (_animator == null || _clips.Count == 0 || _graph.IsValid())
                return;
            float total = 0f;
            foreach (ClipState state in _clips)
                total += state.weight;
            _animator.applyRootMotion = false; // the stand-in is moved by dead reckoning
            _graph = PlayableGraph.Create("[Mesh] StandIn");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            AnimationMixerPlayable mix = AnimationMixerPlayable.Create(_graph, _clips.Count);
            for (int i = 0; i < _clips.Count; i++)
            {
                AnimationClipPlayable clip = AnimationClipPlayable.Create(_graph, _clips[i].clip);
                clip.SetTime(_clips[i].time);
                clip.SetSpeed(_clips[i].speed);
                _graph.Connect(clip, 0, mix, i);
                mix.SetInputWeight(i, total > 0f ? _clips[i].weight / total : 0f);
            }
            AnimationPlayableOutput output = AnimationPlayableOutput.Create(_graph, "StandIn", _animator);
            output.SetSourcePlayable(mix);
            _graph.Play();
        }

        private void OnDestroy()
        {
            if (_graph.IsValid())
                _graph.Destroy();
        }
    }
}
