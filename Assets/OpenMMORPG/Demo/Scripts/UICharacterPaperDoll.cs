using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace MultiplayerARPG
{
    /// <summary>
    /// The character sheet's 3D preview: a copy of the playing character, filmed by its own camera
    /// into a RenderTexture shown on this RawImage. Drag it to turn it.
    ///
    /// **A copy, not the live entity.** Filming the real player would show whatever they are doing
    /// - running, swimming, fighting, on a horse, standing in the dark - with the world behind
    /// them. The copy is built the way the character screens build theirs
    /// (<c>InstantiateModel</c>), dressed by <see cref="UICharacterSelectAppearance.Dress"/> so hair, skin
    /// and size match, and parked on a stage far below the world where nothing else is in shot.
    ///
    /// **Its own lighting.** The camera is rendered by hand, once a frame while the sheet is open,
    /// and for the length of that one render the world's directional lights and fog are switched
    /// off and the stage's key and fill lights switched on. So the preview looks the same at noon
    /// and at midnight, and the stage lights never touch the world.
    ///
    /// **It follows the character, but always shows the weapons drawn.** The sheet is about what is
    /// equipped, and characters spend most of their time sheathed (see DemoWeaponSheathing), which
    /// would leave the doll standing there with empty hands and the sword out of sight on its back.
    /// Equipment, weapon set and appearance are compared
    /// against a signature every frame while open, and the copy is redressed when it changes -
    /// drag a sword onto the main-hand slot and it appears in the preview. Polling rather than the
    /// entity's sync-list events because the sheet opens and closes, the playing character can be
    /// replaced underneath it, and a comparison of a dozen integers cannot miss an update.
    ///
    /// Placed by <c>DemoCharacterSheetBuilder</c> on <c>UICharacterDialog/Window/Info/Doll/Preview/Render</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage))]
    public class UICharacterPaperDoll : MonoBehaviour, IDragHandler, IPointerClickHandler
    {
        [Tooltip("Where the copy stands. Far enough from the world that the world cameras never see it and it sees nothing else.")]
        public Vector3 stagePosition = new Vector3(0f, -5000f, 0f);

        [Tooltip("The layer the copy is moved to and the only one the preview camera draws. 31 is unnamed and unused in the demo.")]
        [Range(0, 31)]
        public int stageLayer = 31;

        [Tooltip("Colour behind the character.")]
        public Color background = new Color(0.10f, 0.11f, 0.12f, 1f);

        [Tooltip("Vertical field of view. Narrow, so the figure is not distorted by perspective.")]
        public float fieldOfView = 22f;

        [Tooltip("Room left around the figure, as a multiple of its size.")]
        public float padding = 1.1f;

        [Tooltip("Degrees the character turns per pixel dragged.")]
        public float turnDegreesPerPixel = 0.6f;

        [Header("Lighting")]
        public Color keyColor = new Color(1f, 0.96f, 0.9f);
        public float keyIntensity = 1.3f;
        public Vector3 keyAngles = new Vector3(30f, 150f, 0f);
        public Color fillColor = new Color(0.75f, 0.82f, 1f);
        public float fillIntensity = 0.45f;
        public Vector3 fillAngles = new Vector3(10f, 230f, 0f);
        public Color ambient = new Color(0.42f, 0.42f, 0.45f);

        private RawImage _image;
        private RenderTexture _texture;
        private GameObject _stage;
        private Transform _turntable;
        private Camera _camera;
        private Light _key;
        private Light _fill;

        private BasePlayerCharacterEntity _source;
        private BaseCharacterModel _model;
        private GameObject _copy;
        private int _signature;
        private float _yaw;

        private readonly List<Light> _worldLights = new List<Light>();
        /// <summary>The world lights one render switched off, to switch back on after it.</summary>
        private readonly List<Light> _switchedOff = new List<Light>();

        private void Awake()
        {
            _image = GetComponent<RawImage>();
        }

        private void OnEnable()
        {
            _yaw = 0f;
            _source = null;
            _signature = 0;
            // Found once per opening: only directional lights matter (a torch 5km away cannot reach
            // the stage), and the sky's sun is the only one the demo has.
            _worldLights.Clear();
            foreach (Light light in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional && light != _key && light != _fill)
                    _worldLights.Add(light);
            }
        }

        private void OnDisable()
        {
            DestroyCopy();
            if (_image != null)
                _image.enabled = false;
        }

        private void OnDestroy()
        {
            DestroyCopy();
            if (_stage != null)
                Destroy(_stage);
            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }

        private void LateUpdate()
        {
            BasePlayerCharacterEntity source = GameInstance.PlayingCharacterEntity;
            if (source != _source || (source != null && _copy == null))
                Rebuild(source);
            if (_model == null)
                return;

            int signature = Signature(source);
            if (signature != _signature)
                Dress();

            _turntable.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (!EnsureTexture())
                return;
            RenderStage();
        }

        public void OnDrag(PointerEventData eventData)
        {
            _yaw -= eventData.delta.x * turnDegreesPerPixel;
        }

        /// <summary>A double-click squares the character back up to the camera.</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.clickCount >= 2)
                _yaw = 0f;
        }

        private void EnsureStage()
        {
            if (_stage != null)
                return;
            _stage = new GameObject("DemoCharacterPaperDollStage");
            _stage.transform.position = stagePosition;

            _turntable = new GameObject("Turntable").transform;
            _turntable.SetParent(_stage.transform, false);

            _camera = new GameObject("Camera").AddComponent<Camera>();
            _camera.transform.SetParent(_stage.transform, false);
            // Rendered by hand in RenderStage, never by the pipeline's own loop.
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = background;
            _camera.cullingMask = 1 << stageLayer;
            _camera.fieldOfView = fieldOfView;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 50f;
            _camera.allowHDR = false;
            _camera.allowMSAA = true;

            _key = MakeLight("Key", keyColor, keyIntensity, keyAngles);
            _fill = MakeLight("Fill", fillColor, fillIntensity, fillAngles);
        }

        private Light MakeLight(string lightName, Color color, float intensity, Vector3 angles)
        {
            var light = new GameObject(lightName).AddComponent<Light>();
            light.transform.SetParent(_stage.transform, false);
            light.transform.localRotation = Quaternion.Euler(angles);
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << stageLayer;
            // Off except during the preview's own render, or it would light the whole world.
            light.enabled = false;
            return light;
        }

        private void Rebuild(BasePlayerCharacterEntity source)
        {
            DestroyCopy();
            _source = source;
            if (source == null)
                return;
            EnsureStage();

            _model = source.InstantiateModel(_turntable);
            if (_model == null)
                return;
            BaseCharacterEntity entity = _model.GetComponentInParent<BaseCharacterEntity>(true);
            _copy = entity != null ? entity.gameObject : _model.gameObject;
            _copy.name = "PaperDoll";
            _copy.transform.localPosition = Vector3.zero;
            _copy.transform.localRotation = Quaternion.identity;

            // A copy is a whole entity with none of its state: it has no health, so it would
            // announce its own death (see UICharacterPreviewReveal.Silence), and it has colliders
            // and triggers that have no business existing anywhere in the world.
            UICharacterPreviewReveal.Silence(_model);
            foreach (Collider col in _copy.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (Rigidbody body in _copy.GetComponentsInChildren<Rigidbody>(true))
                body.isKinematic = true;

            Dress();
        }

        private void Dress()
        {
            if (_model == null || _source == null)
                return;
            // Never sheathed: see the class note.
            _model.SetEquipItemsImmediately(_source.EquipItems, _source.SelectableWeaponSets, _source.EquipWeaponSet, false);
            UICharacterSelectAppearance.Dress(_model, _source);
            // Equipment is instantiated by the dressing, on whatever layer its prefab had.
            _copy.SetLayerRecursively(stageLayer, true);
            _signature = Signature(_source);
            Frame();
        }

        /// <summary>
        /// Stands the camera back far enough to fit the figure from the floor to the crown, so the
        /// size slider cannot push it out of frame.
        ///
        /// **Measured from the head bone, not from renderer bounds.** The body parts ship with
        /// deliberately oversized bounds (a 3m box, so they are never culled mid-animation) and a
        /// garment's bounds are its bind pose, arms out - framing on either put the character in
        /// the bottom half of an empty frame.
        /// </summary>
        private void Frame()
        {
            float top = 1.85f;
            Animator animator = _copy.GetComponentInChildren<Animator>();
            Transform head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head != null)
            {
                // The head bone sits at the base of the skull; the crown is an eighth higher again,
                // proportional so a small character is not framed for a tall one.
                top = Mathf.Max((head.position.y - _turntable.position.y) * 1.13f, 0.5f);
            }

            float height = top * padding;
            float aspect = _texture != null ? (float)_texture.width / _texture.height : 0.85f;
            float halfV = fieldOfView * 0.5f * Mathf.Deg2Rad;
            float distanceForHeight = height * 0.5f / Mathf.Tan(halfV);
            // Arms and a weapon held out need about 1.2m across whatever the aspect.
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * aspect);
            float distanceForWidth = 0.6f * padding / Mathf.Tan(halfH);
            float distance = Mathf.Max(distanceForHeight, distanceForWidth);

            // The entity faces +Z; the camera stands in front of it looking back.
            Vector3 target = new Vector3(0f, top * 0.5f, 0f);
            _camera.transform.localPosition = target + Vector3.forward * distance;
            _camera.transform.localRotation = Quaternion.LookRotation(Vector3.back);
            _camera.fieldOfView = fieldOfView;
        }

        private bool EnsureTexture()
        {
            RectTransform rect = (RectTransform)transform;
            Canvas canvas = _image.canvas;
            float scale = canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
            int width = Mathf.Clamp(Mathf.RoundToInt(rect.rect.width * scale), 0, 2048);
            int height = Mathf.Clamp(Mathf.RoundToInt(rect.rect.height * scale), 0, 2048);
            if (width < 8 || height < 8)
                return false;
            if (_texture == null || _texture.width != width || _texture.height != height)
            {
                if (_texture != null)
                {
                    _texture.Release();
                    Destroy(_texture);
                }
                _texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                _texture.name = "UICharacterPaperDoll";
                _texture.antiAliasing = 4;
                _texture.Create();
                _camera.targetTexture = _texture;
                _image.texture = _texture;
                Frame();
            }
            // Switched on here on every opening, not only when the texture is new: closing the
            // sheet hides the image (so a reopen never flashes the last character's frame), and a
            // reopen at the same size keeps the old texture - so if this lived with the creation,
            // the second opening would render into a texture nobody could see.
            _image.enabled = true;
            return true;
        }

        /// <summary>
        /// Renders the stage with its own lights: the world's directional lights and fog are
        /// switched off for exactly the length of this one render and restored straight after.
        /// </summary>
        private void RenderStage()
        {
            bool fog = RenderSettings.fog;
            AmbientMode ambientMode = RenderSettings.ambientMode;
            Color ambientLight = RenderSettings.ambientLight;
            SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;
            Light sun = RenderSettings.sun;

            // Kept between renders: this runs every frame the sheet is open.
            List<Light> switchedOff = _switchedOff;
            switchedOff.Clear();
            foreach (Light light in _worldLights)
            {
                if (light != null && light.enabled)
                {
                    light.enabled = false;
                    switchedOff.Add(light);
                }
            }
            try
            {
                RenderSettings.fog = false;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = ambient;
                var flat = new SphericalHarmonicsL2();
                flat.AddAmbientLight(ambient);
                RenderSettings.ambientProbe = flat;
                RenderSettings.sun = _key;
                _key.enabled = true;
                _fill.enabled = true;

                _camera.Render();
            }
            finally
            {
                _key.enabled = false;
                _fill.enabled = false;
                foreach (Light light in switchedOff)
                    light.enabled = true;
                RenderSettings.sun = sun;
                RenderSettings.ambientProbe = ambientProbe;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.fog = fog;
            }
        }

        private void DestroyCopy()
        {
            if (_copy != null)
                Destroy(_copy);
            _copy = null;
            _model = null;
            _source = null;
        }

        /// <summary>Everything that changes how the character looks, folded into one number.</summary>
        private static int Signature(BasePlayerCharacterEntity source)
        {
            if (source == null)
                return 0;
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + source.EntityId;
                hash = hash * 31 + source.EquipWeaponSet;
                foreach (CharacterItem item in source.EquipItems)
                    hash = hash * 31 + ItemHash(item);
                foreach (EquipWeapons set in source.SelectableWeaponSets)
                {
                    hash = hash * 31 + ItemHash(set.rightHand);
                    hash = hash * 31 + ItemHash(set.leftHand);
                }
                foreach (CharacterDataInt32 entry in source.PublicInts)
                    hash = hash * 31 + entry.hashedKey * 7 + entry.value;
                return hash;
            }
        }

        private static int ItemHash(CharacterItem item)
        {
            if (item.IsEmptySlot())
                return 1;
            unchecked
            {
                return (item.dataId * 397) ^ (item.level * 31) ^ item.equipSlotIndex;
            }
        }
    }
}
