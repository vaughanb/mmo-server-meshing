using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Makes it look like being under water when the camera goes under the sea.
    ///
    /// The kit keeps a swimming character at the surface, but the camera orbits behind and
    /// below it, so looking down or swimming toward the shore puts the camera under the
    /// waterline while the character is still on it. Before this, nothing happened: the
    /// picture stayed a clear sunny day seen from below a transparent sheet.
    ///
    /// **It watches the camera, not the swim state.** Whether the character is swimming is
    /// beside the point - what matters is where the camera is. That also covers the cases
    /// swimming does not: wading out until the camera dips, or standing in the shallows
    /// looking down.
    ///
    /// Three things together, none of them a post-processing effect:
    ///
    ///  - **Fog**, pulled in from the island's 180-620m to a couple of dozen, and recoloured.
    ///    This is most of the effect. Fog is what makes water read as a volume you are
    ///    inside of rather than a colour laid over the picture, and the scene already has
    ///    it, so it costs nothing to reuse.
    ///  - **The camera's background**, swapped off the skybox. Fog does not touch the
    ///    skybox, so without this the sky goes on showing through at full brightness
    ///    wherever nothing solid is drawn - the horizon stays bright blue while everything
    ///    in front of it is deep green.
    ///  - **A screen tint** (<c>Demo/Underwater</c>), for the near field that fog cannot
    ///    reach, plus the darkening toward the edge of vision.
    ///
    /// Post-processing was the other way to do this and is turned off on the gameplay
    /// camera. Turning it on to get a colour filter would put the whole demo through
    /// tonemapping and shift every colour in it, which is a large change to the look of
    /// the game in exchange for an effect that is only wanted while swimming.
    ///
    /// **Fog is global state, so this borrows it rather than owning it.** The values in
    /// force are captured when the camera goes under and put back when it comes up, and
    /// also in <c>OnDisable</c> - a player who logs out or warps while submerged would
    /// otherwise leave the next scene fogged to twenty metres.
    /// <see cref="DayNightSkyCycle"/> writes the fog colour every frame from the day cycle,
    /// which is why the blend here happens in <c>LateUpdate</c>: it reads the colour the
    /// cycle has just set and tints away from it, so dusk under water is dark without the
    /// two fighting, and no execution order has to be agreed between them.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class UnderwaterCameraEffect : MonoBehaviour
    {
        [Tooltip("The colour the water fogs to. Also the camera's background while under.")]
        public Color waterColor = new Color(0.055f, 0.29f, 0.34f);

        [Tooltip("How far you can see under water, in metres.")]
        public float visibility = 22f;

        [Tooltip("Where the fog starts under water, in metres.")]
        public float fogStart = 0.5f;

        [Tooltip("How quickly the effect comes in and out when crossing the surface, in units a second.")]
        public float blendSpeed = 5f;

        [Tooltip("The Demo/Underwater material. Assigned by the builder; without it the fog still works.")]
        public Material overlayMaterial;

        [Tooltip("The loop heard while under. Assigned by the builder from the Underwater audio family.")]
        public AudioClip underwaterLoop;

        [Range(0f, 1f)]
        [Tooltip("How loud that loop is at full submersion, before the ambient volume setting.")]
        public float loopVolume = 0.8f;

        private static readonly Collider[] s_water = new Collider[4];

        private Camera _camera;
        private Renderer _overlay;
        private AmbientSoundLoop _ambience;
        private MaterialPropertyBlock _block;
        private float _at;

        // What was in force before the camera went under.
        private bool _borrowed;
        /// <summary>The fog colour of the world above the water, as last set by something other than this.</summary>
        private Color _above;
        /// <summary>The fog colour this wrote last frame, to tell it apart from a colour set by the sky.</summary>
        private Color _written;
        private bool _restFog;
        private FogMode _restMode;
        private float _restStart;
        private float _restEnd;
        private CameraClearFlags _restClear;
        private Color _restBackground;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            BuildOverlay();
            BuildAmbience();
        }

        private void OnDisable()
        {
            Release();
            _at = 0f;
            if (_overlay != null)
                _overlay.enabled = false;
            if (_ambience != null)
                _ambience.baseVolume = 0f;
        }

        /// <summary>
        /// The loop heard under water, on the camera so it follows the listener.
        ///
        /// It is a <see cref="AmbientSoundLoop"/> rather than a bare `AudioSource` so that
        /// it obeys the ambient volume slider the same way the island's nature and shore
        /// beds do - the kit's own AudioSourceSetter applies that setting once when it
        /// starts playing, which is no use to a loop whose volume is being driven every
        /// frame. Its height fade is off; this one is gated by submersion instead.
        ///
        /// **It plays from the start and is held at silence**, rather than being started
        /// and stopped with each dip. Starting a clip on submersion would replay the same
        /// opening every time the camera clipped the surface, which on a bobbing swim is
        /// often; left running, the loop is simply somewhere different each time and fades
        /// up wherever it has got to.
        ///
        /// `playOnAwake` is no use here - it only applies to a source that was already on
        /// a prefab or in a scene when it loaded, and this one is made at runtime - so
        /// playback is started explicitly.
        /// </summary>
        private void BuildAmbience()
        {
            if (underwaterLoop == null)
                return;

            var go = new GameObject("UnderwaterAmbience");
            go.transform.SetParent(transform, false);

            var source = go.AddComponent<AudioSource>();
            source.clip = underwaterLoop;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;

            _ambience = go.AddComponent<AmbientSoundLoop>();
            _ambience.baseVolume = 0f;
            _ambience.fadeWithHeight = false;
            source.Play();
        }

        /// <summary>
        /// The screen quad. Made here rather than saved in the prefab because it is one
        /// unit square with no authored content - there is nothing about it worth a mesh
        /// asset, and a generated one cannot drift out of step with the shader that
        /// assumes its corners are at +-0.5.
        ///
        /// **Layer TransparentFX**, which only the gameplay camera draws: the minimap
        /// camera renders layer MiniMap alone and the character-UI camera CharacterUI
        /// alone, so neither picks up a tint that is parented a few centimetres in front
        /// of a camera that may be sitting inside their view. The camera's wall-hit spring
        /// already ignores that layer, so it cannot push the camera about either.
        /// </summary>
        private void BuildOverlay()
        {
            if (overlayMaterial == null)
                return;

            var go = new GameObject("UnderwaterOverlay");
            go.transform.SetParent(transform, false);
            go.layer = LayerMask.NameToLayer("TransparentFX");

            var mesh = new Mesh();
            mesh.name = "UnderwaterQuad";
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            // The shader throws the transform away and writes clip space directly, so the
            // quad's real bounds are meaningless and a tight box would have it culled the
            // moment the camera turned. Big enough never to be culled.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _overlay = go.AddComponent<MeshRenderer>();
            _overlay.sharedMaterial = overlayMaterial;
            _overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _overlay.receiveShadows = false;
            _overlay.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _overlay.enabled = false;
            _block = new MaterialPropertyBlock();
        }

        /// <summary>
        /// Whether the camera is inside water. Asked of the physics scene rather than
        /// compared against a height, so it is whatever the map calls water - the demo's
        /// sea is one big trigger box on the Water layer, and a scene with no water at all
        /// (the crypt) simply never reports one.
        /// </summary>
        private bool Submerged()
        {
            int found = Physics.OverlapSphereNonAlloc(
                transform.position, 0.01f, s_water, 1 << PhysicLayers.Water,
                QueryTriggerInteraction.Collide);
            return found > 0;
        }

        private void LateUpdate()
        {
            _at = Mathf.MoveTowards(_at, Submerged() ? 1f : 0f, blendSpeed * Time.deltaTime);

            // Set every frame, including on the way out, so surfacing fades the loop down
            // rather than cutting it at the moment the effect switches off.
            if (_ambience != null)
                _ambience.baseVolume = loopVolume * _at;

            if (_at <= 0f)
            {
                Release();
                if (_overlay != null)
                    _overlay.enabled = false;
                return;
            }

            Borrow();

            // The colour the day cycle set this frame, tinted toward the water. Read each
            // frame rather than captured, so this follows the time of day - but only when
            // something else wrote it. If the fog still holds the colour written here last
            // frame, nothing did (a scene with no sky cycle), and reading it back would blend
            // the blend: it compounded to the water colour and stayed there. The distances
            // below are captured outright, because nothing else writes them.
            Color current = RenderSettings.fogColor;
            if (current != _written)
                _above = current;
            Color under = Color.Lerp(_above, waterColor, _at);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = under;
            _written = under;
            RenderSettings.fogStartDistance = Mathf.Lerp(_restStart, fogStart, _at);
            RenderSettings.fogEndDistance = Mathf.Lerp(_restEnd, visibility, _at);

            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = under;

            if (_overlay != null)
            {
                _overlay.enabled = true;
                _overlay.GetPropertyBlock(_block);
                _block.SetFloat("_Strength", _at);
                _overlay.SetPropertyBlock(_block);
            }
        }

        private void Borrow()
        {
            if (_borrowed)
                return;
            _borrowed = true;
            _restFog = RenderSettings.fog;
            _restMode = RenderSettings.fogMode;
            _above = RenderSettings.fogColor;
            // Nothing written yet, so the first frame's read is always taken as the scene's own.
            _written = new Color(-1f, -1f, -1f, -1f);
            _restStart = RenderSettings.fogStartDistance;
            _restEnd = RenderSettings.fogEndDistance;
            _restClear = _camera.clearFlags;
            _restBackground = _camera.backgroundColor;
        }

        private void Release()
        {
            if (!_borrowed)
                return;
            _borrowed = false;
            RenderSettings.fog = _restFog;
            RenderSettings.fogMode = _restMode;
            RenderSettings.fogStartDistance = _restStart;
            RenderSettings.fogEndDistance = _restEnd;
            // The fog colour goes back only if it still holds the tint written here: where
            // DayNightSkyCycle runs it has already written this frame's own, and that stands.
            if (RenderSettings.fogColor == _written)
                RenderSettings.fogColor = _above;
            if (_camera != null)
            {
                _camera.clearFlags = _restClear;
                _camera.backgroundColor = _restBackground;
            }
        }
    }
}
