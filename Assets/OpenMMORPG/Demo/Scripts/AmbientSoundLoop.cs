using System.Collections.Generic;
using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A looping ambience bed that follows the ambient volume setting, and can be shaped
    /// by where the listener is: by how far inland it stands from the water's edge, and by
    /// how high above the sea it has climbed. The sea is loud on the beach, half-heard in
    /// the village and a murmur in the middle of the island.
    ///
    /// The kit's AudioSourceSetter applies the setting once when it starts to play; this
    /// keeps applying it, so the slider works while the loop runs. The listener is the
    /// owning character's, which only exists once the player is in the world, so it is
    /// looked up again until found. Built into the island scene by DemoSceneBuilder.
    ///
    /// **Distance to the shore is the rule that does the work; height alone was not
    /// enough.** The island is a low plateau - most of the walkable ground sits between
    /// 3.5 m and 7.5 m above the sea, the beach itself at 1.5 m - so a fade keyed to
    /// height had nothing to fade across: the village heard the waves at about seven
    /// tenths of the beach's volume, near enough to sound the same everywhere. The height
    /// rule is kept for the real hills, where it does mean something, and its thresholds
    /// are set above the plateau so it stays out of the way on the flat.
    ///
    /// The shoreline is read off the scene's Terrain, not authored: the heightmap is
    /// sampled on a coarse grid once, and every place a land cell meets a water cell
    /// (ground below <see cref="seaLevel"/>) becomes a point on the coast. Distance to
    /// the shore is then the distance to the nearest of those points, so a hand-sculpted
    /// coast is followed as faithfully as a generated one, and an inland pool would count
    /// as shore too. A listener standing in the water, or off the terrain altogether, is
    /// at distance zero.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AmbientSoundLoop : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float baseVolume = 1f;

        /// <summary>
        /// A multiplier on top of everything else, for something that turns this bed up or down while the
        /// game runs: the weather ducking the birdsong under rain, and fading the rain loop itself.
        ///
        /// A property, so it is never serialised. The obvious way to do those jobs is to write
        /// <see cref="baseVolume"/>, and that is saved into the scene with everything else: a game that
        /// stopped mid-shower, or an edit-mode test, left the birds at a fraction of their volume in the
        /// file, and the next run captured that as the volume to duck from.
        /// </summary>
        public float RuntimeGain { get; set; } = 1f;

        [Header("Distance from the shore")]
        [Tooltip("Fade the loop as the listener moves inland from the water's edge. Needs a Terrain in the scene.")]
        public bool fadeWithShoreDistance;
        [Tooltip("Within this many metres of the waterline the loop is at full volume.")]
        public float fullWithinShoreDistance = 8f;
        [Tooltip("This many metres inland the loop is down to its far volume.")]
        public float quietBeyondShoreDistance = 60f;
        [Range(0f, 1f)]
        [Tooltip("How loud the loop is far inland, before the height rule and the ambient setting.")]
        public float shoreQuietVolume = 0.1f;

        [Header("Height above the sea")]
        [Tooltip("Fade the loop out as the listener climbs above the sea.")]
        public bool fadeWithHeight;
        public float seaLevel = 0f;
        [Tooltip("Up to this height above the sea the loop is at full volume.")]
        public float fullBelowHeight = 8f;
        [Tooltip("At this height above the sea the loop is down to its quiet volume.")]
        public float quietAboveHeight = 24f;
        [Range(0f, 1f)]
        public float quietVolume = 0.4f;

        /// <summary>
        /// Spacing of the heightmap samples the coast is traced from. Two metres puts the
        /// coast within a metre of where the water actually meets the sand, which is well
        /// inside what a fade over tens of metres can tell apart, and keeps the trace to a
        /// few thousand samples on a 260 m island.
        /// </summary>
        public const float ShoreScanCell = 2f;

        private AudioSource _source;
        private AudioListener _listener;
        private Terrain _terrain;
        private Vector2[] _shore;
        private float _nextSearch;
        /// <summary>The shore fade, and where the listener stood when it was worked out; -1 until it has been.</summary>
        private float _shoreFactor = -1f;
        private Vector2 _shoreMeasuredAt;

        /// <summary>
        /// How far the listener moves before the distance to the coast is measured again, in metres.
        /// The fade runs over tens of metres, so a metre of it is inaudible, and the measure walks
        /// every point of the coast.
        /// </summary>
        private const float ShoreRemeasure = 1f;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.loop = true;
            _source.spatialBlend = 0f;
        }

        private void Update()
        {
            float factor = 1f;
            if (fadeWithShoreDistance || fadeWithHeight)
            {
                bool needListener = _listener == null || !_listener.isActiveAndEnabled;
                bool needTerrain = fadeWithShoreDistance && _terrain == null;
                if ((needListener || needTerrain) && Time.time >= _nextSearch)
                {
                    _nextSearch = Time.time + 1f;
                    if (needListener)
                        _listener = FindListener();
                    if (needTerrain)
                        RebuildShoreMap();
                }
                if (_listener != null)
                {
                    Vector3 at = _listener.transform.position;
                    if (fadeWithShoreDistance && _terrain != null)
                    {
                        var here = new Vector2(at.x, at.z);
                        if (_shoreFactor < 0f || (here - _shoreMeasuredAt).sqrMagnitude > ShoreRemeasure * ShoreRemeasure)
                        {
                            _shoreMeasuredAt = here;
                            _shoreFactor = Mathf.Lerp(1f, shoreQuietVolume, Mathf.InverseLerp(fullWithinShoreDistance, quietBeyondShoreDistance, DistanceToShore(at)));
                        }
                        factor *= _shoreFactor;
                    }
                    if (fadeWithHeight)
                        factor *= Mathf.Lerp(1f, quietVolume, Mathf.InverseLerp(fullBelowHeight, quietAboveHeight, at.y - seaLevel));
                }
            }
            AudioManager manager = AudioManager.Singleton;
            float level = manager == null ? 1f : manager.GetVolumeLevel(manager.ambientVolumeSetting.id);
            _source.volume = baseVolume * factor * level * RuntimeGain;
            _source.mute = level <= 0f;
        }

        /// <summary>
        /// Horizontal distance from a world position to the nearest point of the coast,
        /// zero in the water or anywhere off the terrain. Traces the coast first if it
        /// has not been yet; returns zero when there is no terrain to trace.
        /// </summary>
        public float DistanceToShore(Vector3 worldPosition)
        {
            if (_terrain == null)
                RebuildShoreMap();
            if (_terrain == null || _shore == null)
                return 0f;

            Vector3 origin = _terrain.transform.position;
            Vector3 size = _terrain.terrainData.size;
            if (worldPosition.x < origin.x || worldPosition.x > origin.x + size.x ||
                worldPosition.z < origin.z || worldPosition.z > origin.z + size.z)
                return 0f;
            if (_terrain.SampleHeight(worldPosition) + origin.y < seaLevel)
                return 0f;

            Vector2 at = new Vector2(worldPosition.x, worldPosition.z);
            float best = float.MaxValue;
            for (int i = 0; i < _shore.Length; i++)
            {
                float d = (_shore[i] - at).sqrMagnitude;
                if (d < best)
                    best = d;
            }
            return best == float.MaxValue ? 0f : Mathf.Sqrt(best);
        }

        /// <summary>How many points the traced coast has; zero until a terrain has been found.</summary>
        public int ShorePointCount
        {
            get { return _shore == null ? 0 : _shore.Length; }
        }

        /// <summary>
        /// Traces the coast off the active terrain: samples its height on a grid of
        /// <see cref="ShoreScanCell"/> and records the midpoint of every edge where land
        /// meets water. Runs again on demand, so a terrain edited at runtime can be re-read.
        /// </summary>
        public void RebuildShoreMap()
        {
            _terrain = Terrain.activeTerrain;
            _shore = null;
            _shoreFactor = -1f;
            if (_terrain == null)
                return;

            Vector3 origin = _terrain.transform.position;
            Vector3 size = _terrain.terrainData.size;
            int nx = Mathf.FloorToInt(size.x / ShoreScanCell) + 1;
            int nz = Mathf.FloorToInt(size.z / ShoreScanCell) + 1;
            bool[] water = new bool[nx * nz];
            Vector3 sample = Vector3.zero;
            for (int j = 0; j < nz; j++)
            {
                sample.z = origin.z + j * ShoreScanCell;
                for (int i = 0; i < nx; i++)
                {
                    sample.x = origin.x + i * ShoreScanCell;
                    water[j * nx + i] = _terrain.SampleHeight(sample) + origin.y < seaLevel;
                }
            }

            var points = new List<Vector2>();
            for (int j = 0; j < nz; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    bool here = water[j * nx + i];
                    if (i + 1 < nx && here != water[j * nx + i + 1])
                        points.Add(new Vector2(origin.x + (i + 0.5f) * ShoreScanCell, origin.z + j * ShoreScanCell));
                    if (j + 1 < nz && here != water[(j + 1) * nx + i])
                        points.Add(new Vector2(origin.x + i * ShoreScanCell, origin.z + (j + 0.5f) * ShoreScanCell));
                }
            }
            _shore = points.ToArray();
        }

        private static AudioListener FindListener()
        {
            foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener.isActiveAndEnabled)
                    return listener;
            }
            return null;
        }
    }
}
