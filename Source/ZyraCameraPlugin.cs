// BepInEx 5 / Unity Mono / Gorilla Tag PCVR.
// Compile into a plugin DLL; do not attach this class to the asset-bundle prefab.
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using GorillaLocomotion;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace IsaiahCameraMod
{
    // Independent of Unity so input transitions can be checked without a VR runtime.
    internal enum ButtonPhase { Idle, Pressed, Held, Released }
    internal enum ButtonSource { None, GamePoller, UnityXR }

    internal readonly struct ControllerButtonState
    {
        public readonly bool Available, Down;
        public readonly ButtonSource Source;

        private ControllerButtonState(bool available, bool down, ButtonSource source)
        {
            Available = available;
            Down = down;
            Source = source;
        }

        public static ControllerButtonState Select(bool gameAvailable, bool gameDown,
            bool xrAvailable, bool xrDown)
        {
            // The game's state includes its SteamVR action bindings. An available
            // game RELEASE must win over a stale/contradictory XR press; do not OR.
            if (gameAvailable) return new ControllerButtonState(true, gameDown, ButtonSource.GamePoller);
            if (xrAvailable) return new ControllerButtonState(true, xrDown, ButtonSource.UnityXR);
            return new ControllerButtonState(false, false, ButtonSource.None);
        }
    }

    internal sealed class XRButtonEdges
    {
        private bool previousDown;
        private bool armed = true;
        private bool seenAvailable;

        public ButtonPhase Sample(bool available, bool down)
        {
            if (!available)
            {
                bool wasDown = previousDown;
                previousDown = false;
                if (seenAvailable) armed = false;
                return wasDown ? ButtonPhase.Released : ButtonPhase.Idle;
            }
            seenAvailable = true;
            // After an actual disconnect, require neutral before re-grabbing.
            // The first connection can arrive with A already pressed.
            if (!armed)
            {
                if (!down) armed = true;
                return ButtonPhase.Idle;
            }
            ButtonPhase phase = down ? (previousDown ? ButtonPhase.Held : ButtonPhase.Pressed)
                : (previousDown ? ButtonPhase.Released : ButtonPhase.Idle);
            previousDown = down;
            return phase;
        }
    }

    // Pure pose calculation, independently checked with translated/rotated heads
    // and off-center model pivots. The rig itself always has unit scale.
    internal static class CameraSpawnMath
    {
        public static Pose FromHead(Vector3 headPosition, Quaternion headRotation,
            Vector3 localBodyCenter, Quaternion opticalToModelRotation)
        {
            Quaternion rotation = headRotation * opticalToModelRotation;
            Vector3 bodyCenter = headPosition + headRotation * Vector3.forward * 0.5f;
            return new Pose(bodyCenter - rotation * localBodyCenter, rotation);
        }
    }

    internal enum AAction { None, Show, Follow, Freeze, Hide }

    // A tap toggles visibility. A hold recalls/follows; its release ALWAYS hovers.
    internal sealed class CameraAGesture
    {
        private bool active, startedHidden, holding;
        private float pressedAt;
        public AAction Sample(ButtonPhase phase, bool visible, bool inputAvailable, float now)
        {
            if (phase == ButtonPhase.Pressed)
            {
                active = true;
                startedHidden = !visible;
                holding = startedHidden;
                pressedAt = now;
                return startedHidden ? AAction.Show : AAction.None;
            }
            if (!active) return AAction.None;
            if (phase == ButtonPhase.Held)
            {
                if (now - pressedAt >= 0.3f) holding = true;
                return holding ? AAction.Follow : AAction.None;
            }
            if (phase == ButtonPhase.Released || !inputAvailable)
            {
                active = false;
                // Losing tracking cannot accidentally toggle the camera off.
                return startedHidden || holding || now - pressedAt >= 0.3f || !inputAvailable ? AAction.Freeze : AAction.Hide;
            }
            return AAction.None;
        }
    }

    internal static class CameraTouchMath
    {
        // Sweep the hand sphere through a button's local box. Unlike trigger
        // callbacks this works at the render rate, even when either object moves
        // between physics steps, and never enters Gorilla locomotion's physics.
        public static bool SweepBox(Vector3 start, Vector3 end, Vector3 half, float radius, out float hit)
        {
            half += Vector3.one * radius;
            float enter = 0f, leave = 1f;
            Vector3 direction = end - start;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = start[axis], delta = direction[axis], extent = half[axis];
                if (Math.Abs(delta) < 0.000001f)
                {
                    if (origin < -extent || origin > extent) { hit = 0f; return false; }
                    continue;
                }
                float a = (-extent - origin) / delta, b = (extent - origin) / delta;
                if (a > b) { float swap = a; a = b; b = swap; }
                enter = Math.Max(enter, a);
                leave = Math.Min(leave, b);
                if (enter > leave) { hit = 0f; return false; }
            }
            hit = enter;
            return true;
        }
    }

    internal static class CameraViewMath
    {
        // Exponential response for a time constant in seconds. CameraMotionMath
        // maps the 0..1 UI strength to a bounded time constant before calling it.
        public static float DampingAlpha(float smoothness, float deltaTime)
            => smoothness <= 0f ? 1f : (float)(1d - Math.Exp(-Math.Max(0f, deltaTime) / smoothness));

        public static Vector2Int CaptureSize(int windowWidth, int windowHeight, int maxWidth, int maxHeight)
        {
            windowWidth = Math.Max(1, windowWidth); windowHeight = Math.Max(1, windowHeight);
            double scale = Math.Min(1d, Math.Min((double)Math.Max(1, maxWidth) / windowWidth,
                (double)Math.Max(1, maxHeight) / windowHeight));
            return new Vector2Int(Math.Max(1, (int)Math.Round(windowWidth * scale)),
                Math.Max(1, (int)Math.Round(windowHeight * scale)));
        }

        // Fill the differently shaped tablet with undistorted video. The PC
        // output always uses the complete frame and never uses this UV crop.
        public static Vector4 CoverUv(float sourceAspect, float panelAspect)
        {
            float x = Math.Min(1f, panelAspect / sourceAspect);
            float y = Math.Min(1f, sourceAspect / panelAspect);
            return new Vector4(x, y, (1f - x) * 0.5f, (1f - y) * 0.5f);
        }

        public static int CaptureMask(int headsetMask, int mirrorMask, bool firstPerson, int faceLayer)
        {
            if (firstPerson) return headsetMask; // Native first-person visibility, without changing any mesh.
            // Mirrors can omit world layers used by other maps. Preserve the
            // headset's loaded-world layers and add the external avatar layers.
            int mask = headsetMask | mirrorMask;
            mask |= 1 << (int)UnityLayer.MirrorOnly;
            // Layer names are not ownership: loaded world geometry can share
            // these layers. Filter first-person cosmetics ONLY within the local
            // rig during our render, never remove a whole world layer here.
            if (faceLayer >= 0 && faceLayer < 32) mask |= 1 << faceLayer;
            return mask;
        }
    }

    // Small opaque bitmap labels avoid TextMesh's always-on-top font shader.
    // They use normal depth testing and back-face culling, so the rear shell
    // cannot display reversed text. All lettering stays outside the preview.
    internal sealed class CameraLabel : IDisposable
    {
        private static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
        {
            ['A']="0E11111F111111", ['B']="1E11111E11111E", ['C']="0E11101010110E",
            ['D']="1E11111111111E", ['E']="1F10101E10101F", ['F']="1F10101E101010",
            ['G']="0E11101711110F", ['H']="1111111F111111", ['I']="0E04040404040E",
            ['J']="0702020202120C", ['K']="11121418141211", ['L']="1010101010101F",
            ['M']="111B1515111111", ['N']="11191513111111", ['O']="0E11111111110E",
            ['P']="1E11111E101010", ['Q']="0E11111115120D", ['R']="1E11111E141211",
            ['S']="0F10100E01011E", ['T']="1F040404040404", ['U']="1111111111110E",
            ['V']="11111111110A04", ['W']="11111115151B11", ['X']="11110A040A1111",
            ['Y']="11110A04040404", ['Z']="1F01020408101F",
            ['0']="0E11131519110E", ['1']="040C040404040E", ['2']="0E11010204081F",
            ['3']="1E01010E01011E", ['4']="02060A121F0202", ['5']="1F10101E01011E",
            ['6']="0E10101E11110E", ['7']="1F010204080808", ['8']="0E11110E11110E",
            ['9']="0E11110F01010E", ['-']="0000001F000000", ['+']="0004041F040400",
            ['.']="00000000000606", [':']="00060600060600", ['/']="01010204081010",
            [' ']="00000000000000"
        };
        public Texture2D Texture { get; private set; }
        private readonly int columns;
        private readonly Action<string> authoredText;
        private string previous;
        private Color background = new Color(6f/255f,8f/255f,10f/255f,1);

        public void SetBackground(Color value)
        {
            if (background == value) return;
            background = value; string text = previous; previous = null; Set(text);
        }

        public CameraLabel(int columns)
        {
            this.columns = columns;
            Texture = new Texture2D(columns * 6 + 4, 11, TextureFormat.RGBA32, false)
            {
                name = "Isaiah control label", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            Set("");
        }

        public CameraLabel(Action<string> authoredText) { this.authoredText = authoredText; }

        public void Set(string value)
        {
            value = (value ?? "").ToUpperInvariant();
            if (value == previous) return;
            previous = value;
            if (authoredText != null) { authoredText(value); return; }
            if (value.Length > columns) value = value.Substring(0, columns);
            int width = Texture.width;
            var pixels = new Color32[width * 11];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;
            var ink = new bool[pixels.Length];
            int start = (width - value.Length * 6 + 1) / 2;
            for (int c = 0; c < value.Length; c++)
            {
                if (!Glyphs.TryGetValue(value[c], out string rows)) continue;
                for (int row = 0; row < 7; row++)
                {
                    int bits = Convert.ToInt32(rows.Substring(row * 2, 2), 16);
                    for (int col = 0; col < 5; col++)
                        if ((bits & (1 << (4 - col))) != 0)
                            ink[(8 - row) * width + start + c * 6 + col] = true;
                }
            }
            // One-pixel dark outline on the same color as the physical key.
            // This preserves the authored pixel-lettering style without black bars.
            for (int y = 1; y < 10; y++) for (int x = 1; x < width - 1; x++)
                if (ink[y * width + x])
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        pixels[(y + dy) * width + x + dx] = new Color32(6,8,10,255);
            for (int i = 0; i < pixels.Length; i++) if (ink[i]) pixels[i] = new Color32(235,239,245,255);
            Texture.SetPixels32(pixels);
            Texture.Apply(false, false);
        }

        public void Dispose()
        {
            if (Texture) UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }
    }

    [BepInPlugin("com.isaiah.handheldcamera", "Isaiah Handheld Camera", "2.9.0")]
    [DefaultExecutionOrder(10000)]
    public sealed partial class IsaiahCameraPlugin : BaseUnityPlugin
    {
        public const string PrefabPath = "assets/isaiahcamera/isaiahcamera.prefab";
        private static readonly string[] ButtonNames =
        {
            "Btn_Flip", "Btn_FirstPerson", "Btn_ThirdPerson",
            "Btn_FOV_Up", "Btn_FOV_Down", "Btn_Smooth_Up", "Btn_Smooth_Down",
            "Btn_Near_Clipping_Up", "Btn_Near_Clipping_Down", "Btn_More"
        };

        private enum ViewMode { Handheld, FirstPerson, ThirdPerson, FollowPlayer }
        private enum PlacementMode { Hidden, HeadFollow, HeldInHand, Hover }
        private sealed class Button
        {
            public string Name;
            public Transform Visual;
            public Vector3 HalfSize;
            public Renderer Face;
            public Transform Activation;
            public MaterialPropertyBlock FeedbackBlock = new MaterialPropertyBlock();
            public float FlashUntil;
            public float NextPress;
            public int FeedbackState = -1, FeedbackAppearance = -1;
        }

        private sealed class TouchHand
        {
            public Collider Collider;
            public Vector3 Previous;
            public bool HasPrevious, Latched;
            public void Reset() { HasPrevious = false; Latched = false; }
        }

        private ConfigEntry<bool> allowHoldingHand;
        private ConfigEntry<Vector3> leftTouchOffset, rightTouchOffset;
        private ConfigEntry<Vector3> thirdPersonOffset;
        private ConfigEntry<float> touchRadius, grabRadius;
        private ConfigEntry<float> modelScale, assemblySize;
        private ConfigEntry<bool> desktopOutput;
        private ConfigEntry<int> textureWidth, textureHeight, screenMaterialSlot;
        private GTPlayer player;
        private Transform leftHand, rightHand;
        private Camera nativeCamera, captureCamera;
        private AssetBundle bundle;
        private GameObject modelRig, model;
        private readonly TouchHand leftTouch = new TouchHand(), rightTouch = new TouchHand();
        private Transform center, forwardMarker, morePanel;
        private Quaternion opticalToModelRotation;
        private Vector3 heldPositionOffset, hoverPosition;
        private Quaternion heldRotationOffset, hoverRotation;
        private bool heldByLeftHand;
        private PlacementMode placement = PlacementMode.Hidden;
        private Rigidbody[] rigidbodies;
        private readonly List<Vector3> grabAnchors = new List<Vector3>();
        private XRButtonEdges aButton = new XRButtonEdges();
        private XRButtonEdges leftGrip = new XRButtonEdges();
        private XRButtonEdges rightGrip = new XRButtonEdges();
        private InputDevice leftController, rightController;
        private readonly List<InputDevice> controllerCandidates = new List<InputDevice>();
        private ButtonPhase aPhase, leftGripPhase, rightGripPhase;
        private bool spawnRequested, loggedController, inputAvailable;
        private CameraAGesture aGesture = new CameraAGesture();
        private AAction aAction;
        private ButtonSource lastInputSource;
        private float nextInputWarning;
        private Material screenMaterial, desktopMaterial;
        private readonly List<Material> convertedMaterials = new List<Material>();
        private RenderTexture texture;
        private Renderer[] modelRenderers;
        private bool[] previousForceOff;
        private CameraLabel smoothReadout, fovReadout, nearReadout, extraReadout;
        private float previewAspect;
        private int captureWindowWidth, captureWindowHeight, lastCaptureMask = int.MinValue;
        private Transform controls;
        private Material activeKeyMaterial;
        private readonly List<CameraLabel> labels = new List<CameraLabel>();
        private readonly List<CameraLabel> buttonLabels = new List<CameraLabel>();
        private Camera desktopCamera;
        private GameObject desktopRoot;
        private Transform desktopQuad;
        private readonly Dictionary<Camera, bool> savedDesktopCameras = new Dictionary<Camera, bool>();
        private readonly Dictionary<XRDisplaySubsystem, int> savedMirrorModes = new Dictionary<XRDisplaySubsystem, int>();
        private readonly List<XRDisplaySubsystem> xrDisplays = new List<XRDisplaySubsystem>();
        private bool desktopOwned, haveLiveFrame, viewSessionActive;
        private GameViewRenderMode previousMirrorMode;
        private int desktopWidth, desktopHeight;
        private float nextDesktopDiscovery;
        private readonly List<Camera> desktopDiscoveryCameras = new List<Camera>();
        private AudioSource buttonAudio;
        private readonly List<Button> buttons = new List<Button>();
        private Coroutine startup;
        private Coroutine renderLoop;
        private readonly WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
        private readonly UniversalRenderPipeline.SingleCameraRequest urpRequest = new UniversalRenderPipeline.SingleCameraRequest();
        private readonly RenderPipeline.StandardRequest standardRequest = new RenderPipeline.StandardRequest();
        private float nextRenderAttempt;
        private bool renderFailureLogged;
        private bool ready, snap = true, selfie;
        private ViewMode mode;
        private float fov = 70f, smoothness = 0.10f, nearClip = 0.03f;
        private const float ButtonCooldown = 0.20f;

        private void Awake()
        {
            Config.SaveOnConfigSet = false;
            clickVolume = Config.Bind("Audio", "ButtonVolume", .65f, "Volume of the supplied button-click recording, 0 to 1.");
            textureWidth = Config.Bind("Desktop", "MaxCaptureWidth", 1920, "Maximum capture width. Actual resolution follows the PC window's aspect ratio without bars.");
            textureHeight = Config.Bind("Desktop", "MaxCaptureHeight", 1080, "Maximum capture height. Window resizing updates the capture and PC output automatically.");
            screenMaterialSlot = Config.Bind("Display", "ScreenMaterialSlot", 0, "CameraScreen material slot; restart after changing.");
            allowHoldingHand = Config.Bind("Hands", "AllowHoldingHandButtons", false, "Allow the holding hand to press buttons too.");
            grabRadius = Config.Bind("Hands", "GrabRadius", 0.12f, "Maximum distance in world metres from a hand to a handle/GripPoint to pick up the camera.");
            leftTouchOffset = Config.Bind("Hands", "LeftTouchOffset", Vector3.zero, "Local offset from left hand follower to virtual fingertip.");
            rightTouchOffset = Config.Bind("Hands", "RightTouchOffset", Vector3.zero, "Local offset from right hand follower to virtual fingertip.");
            touchRadius = Config.Bind("Hands", "TouchRadius", 0.012f, "Virtual finger sphere radius in world metres, clamped to 0.003..0.05.");
            modelScale = Config.Bind("Appearance", "ExactModelScale", 1.08f, "Uniform scale of the complete supplied Blender assembly. Baseline body width 0.44 m; default 1.08 = 8 percent larger. Restart to apply.");
            desktopOutput = Config.Bind("Desktop", "Enabled", true, "Show the custom live feed in the PC game window from initialization. A recalls the tablet into Selfie. MONITOR turns desktop output off. Does not redirect the headset.");
            thirdPersonOffset = Config.Bind("View", "ThirdPersonOffset", new Vector3(0f, 0.5f, -2f), "Head-yaw-relative offset in metres.");
            assemblySize = Config.Bind("Appearance", "AssemblySizeMultiplier", 1.18f, "Uniform multiplier for the complete camera, screen, original buttons and added menus. Default 1.18 is 18 percent larger than 2.1. Restart to apply.");
            LoadPreferences();
            InitializePresets();
        }

        private void OnEnable()
        {
            Logger.LogInfo("Camera plugin 2.9.0 enabled. Embedded exact model, original font and click audio; spectator smoothing, local Time/Rain, presets and public lobby hop.");
            Application.onBeforeRender += BeforeHeadsetRender;
            InitializeCaptureCulling();
            startup = StartCoroutine(InitializeWhenReady());
            renderLoop = StartCoroutine(RenderAfterFrame());
        }

        private IEnumerator InitializeWhenReady()
        {
            // Defer one frame so the coroutine handle is assigned before completion.
            yield return null;
            float nextWaitMessage = 0f;
            string lastError = null;
            while (!ready)
            {
                try
                {
                    if (TryBindPlayer())
                    {
                        InitializeCamera();
                        ready = true;
                        Logger.LogInfo("Camera ready. Tap right A to show/hide; hold A for 0.3 seconds to recall/follow. Release A or a handle to hover. Touch the larger controls below the preview.");
                    }
                    else if (Time.unscaledTime >= nextWaitMessage)
                    {
                        nextWaitMessage = Time.unscaledTime + 10f;
                        Logger.LogInfo("Waiting for the local player's active headset camera; A input is already being monitored.");
                    }
                }
                catch (Exception ex)
                {
                    if (lastError != ex.ToString())
                    {
                        lastError = ex.ToString();
                        Logger.LogError("Camera setup failed; retrying in 2 seconds: " + ex);
                    }
                    ReleaseResources();
                }
                if (!ready) yield return new WaitForSecondsRealtime(2f);
            }
            startup = null;
        }

        private bool TryBindPlayer()
        {
            // These members were checked against the installed Assembly-CSharp.dll.
            // Only the LOCAL locomotion singleton supplies interaction hands.
            GTPlayer next = GTPlayer.Instance;
            if (!next || !next.mainCamera) return false;
            Transform nextLeft = next.LeftHand.handFollower;
            Transform nextRight = next.RightHand.handFollower;
            if (player != next || leftHand != nextLeft || rightHand != nextRight || nativeCamera != next.mainCamera)
            { snap = true; nextDesktopDiscovery = 0f; nextCullingDiscovery = 0f; }
            player = next;
            leftHand = nextLeft;
            rightHand = nextRight;
            nativeCamera = next.mainCamera;
            GorillaTagger tagger = GorillaTagger.Instance;
            if (tagger)
            {
                BindTouchCollider(leftTouch, tagger.leftHandTriggerCollider, "left");
                BindTouchCollider(rightTouch, tagger.rightHandTriggerCollider, "right");
            }
            // Spawning needs the headset only. Hand followers may initialize later.
            return nativeCamera.isActiveAndEnabled;
        }

        private void BindTouchCollider(TouchHand touch, GameObject source, string side)
        {
            if (!source || (touch.Collider && touch.Collider.gameObject == source)) return;
            touch.Collider = source.GetComponent<Collider>();
            touch.Reset();
            if (touch.Collider) Logger.LogInfo("Button touch source: " + side + " = " + touch.Collider.name + " (" + touch.Collider.GetType().Name + ").");
        }

        private void InitializeCamera()
        {
            Logger.LogInfo("Loading camera with render pipeline: " +
                (GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.GetType().FullName : "Built-in"));
            bundle = EmbeddedCameraAssets.Load();
            GameObject prefab = bundle.LoadAsset<GameObject>(PrefabPath);
            if (!prefab) throw new InvalidOperationException("Bundle is missing " + PrefabPath);

            modelRig = new GameObject("IsaiahCamera_Rig");
            modelRig.SetActive(false); // Never briefly display it at world origin.
            DontDestroyOnLoad(modelRig);
            Transform assembly = new GameObject("CompleteCameraAssembly").transform;
            assembly.SetParent(modelRig.transform, false);
            model = Instantiate(prefab, assembly, false);
            model.name = "IsaiahCamera";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale *= Mathf.Clamp(modelScale.Value, 1f, 3f);
            model.SetActive(true);
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
                ConvertModelMaterialsForUrp();
            // Decorative colliders must not participate in Gorilla locomotion queries.
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            modelRig.AddComponent<Rigidbody>();
            rigidbodies = modelRig.GetComponentsInChildren<Rigidbody>(true);
            MakeBodiesKinematic();
            foreach (Camera c in model.GetComponentsInChildren<Camera>(true)) c.enabled = false;
            foreach (AudioListener listener in model.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;

            // The actual supplied FBX's body is "Main Frame" (a symmetric box mesh).
            // Compute its mesh-bounds midpoint in body-local coordinates, NOT the
            // FBX origin, lens position, world AABB, or whole-model bounds.
            Transform body = FindUnique("Main Frame", true);
            Bounds bodyBounds = body.GetComponent<MeshFilter>().sharedMesh.bounds;
            center = new GameObject("CenterRenderPoint").transform;
            center.SetParent(modelRig.transform, false);
            center.position = body.TransformPoint(bodyBounds.center);
            forwardMarker = FindUnique("CameraForward", false);
            Quaternion forwardLocalRotation = forwardMarker ?
                Quaternion.Inverse(modelRig.transform.rotation) * forwardMarker.rotation : Quaternion.identity;
            opticalToModelRotation = Quaternion.Inverse(forwardLocalRotation);

            Transform grip = FindUnique("GripPoint", false);
            grabAnchors.Add(grip ? modelRig.transform.InverseTransformPoint(grip.position) : center.localPosition);
            AddHandleAnchor("LeftHandle");
            AddHandleAnchor("Sphere.001");

            Transform screen = FindUnique("CameraScreen", true);
            Renderer screenRenderer = screen.GetComponent<Renderer>();
            if (!screenRenderer) throw new InvalidOperationException("CameraScreen needs a Renderer on the named object.");
            Material[] materials = screenRenderer.sharedMaterials;
            int slot = screenMaterialSlot.Value;
            if (slot < 0 || slot >= materials.Length || !materials[slot])
                throw new InvalidOperationException("Invalid CameraScreen material slot.");
            // URP material conversion preserves the existing bundle; no rebuild needed.
            screenMaterial = new Material(materials[slot]);
            if (!screenMaterial.HasProperty("_BaseMap") && !screenMaterial.HasProperty("_MainTex"))
                throw new InvalidOperationException("CameraScreen needs an unlit material with _BaseMap or _MainTex.");
            texture = CreateFeed(CurrentCaptureSize());
            if (screenMaterial.HasProperty("_BaseMap")) screenMaterial.SetTexture("_BaseMap", texture);
            if (screenMaterial.HasProperty("_MainTex")) screenMaterial.SetTexture("_MainTex", texture);
            materials[slot] = screenMaterial;
            screenRenderer.sharedMaterials = materials;
            // Project video onto the original Blender screen mesh. Keep its
            // edge/back faces opaque so the rear shell never shows mirrored text.
            SetMaterialColor(screenMaterial, Color.white);
            if (screenMaterial.HasProperty("_Cull")) screenMaterial.SetFloat("_Cull", (float)CullMode.Back);
            if (screenMaterial.HasProperty("_ZWrite")) screenMaterial.SetFloat("_ZWrite", 1f);
            BuildAuthoredControls(body, screen);
            ApplyAssemblySize(assembly);
            EnsureModelVisibleToHead();
            InitializeButtonAudio();

            captureCamera = new GameObject("IsaiahCamera_Capture").AddComponent<Camera>();
            DontDestroyOnLoad(captureCamera.gameObject);
            captureCamera.enabled = false; // Render explicitly after moving the camera.
            ConfigureMonoCamera(captureCamera);
            captureCamera.targetTexture = texture;
            captureCamera.aspect = (float)texture.width / texture.height;
            captureCamera.allowHDR = false;
            captureCamera.allowMSAA = false;
            captureCamera.useOcclusionCulling = false;
            captureCamera.clearFlags = CameraClearFlags.Skybox;
            captureCamera.backgroundColor = Color.black;
            captureCamera.farClipPlane = 500f;
            captureCamera.nearClipPlane = nearClip;
            captureCamera.fieldOfView = fov;
            urpRequest.destination = texture;
            standardRequest.destination = texture;
            nextRenderAttempt = 0f;
            renderFailureLogged = false;
            CreateDesktopOutput();
            EnsureCaptureAspect();
            modelRenderers = modelRig.GetComponentsInChildren<Renderer>(true);
            previousForceOff = new bool[modelRenderers.Length];
            FollowHead(true);
            RememberManualPlacement();
            ApplyStartupPreset();
            RefreshText();
            // Rendering begins independently of summoning or model visibility.
            viewSessionActive = true;
            InitializeModePresentation();
            if (mode == ViewMode.Handheld) { placement = PlacementMode.Hidden; modelRig.SetActive(false); }
            snap = true;
            Logger.LogInfo("Created camera GameObjects: " + modelRig.name + " / " + model.name +
                "; renderers=" + modelRenderers.Length + "; embedded resource=" + EmbeddedCameraAssets.ResourceName);
            Logger.LogInfo("Structural render center in rig coordinates: " + center.localPosition.ToString("F5"));
        }

        private void ConfigureMonoCamera(Camera camera)
        {
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
            {
                var data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderType = CameraRenderType.Base;
                data.allowXRRendering = false; // One mono image for the camera screen.
                data.renderPostProcessing = false;
                data.requiresColorOption = CameraOverrideOption.Off;
                data.requiresDepthOption = CameraOverrideOption.Off;
                data.SetRenderer(-1);
            }
        }

        private void EnsureModelVisibleToHead()
        {
            int visibleLayer = 0;
            if ((nativeCamera.cullingMask & 1) == 0)
            {
                for (int i = 1; i < 32; i++)
                    if ((nativeCamera.cullingMask & (1 << i)) != 0) { visibleLayer = i; break; }
            }
            foreach (Renderer renderer in modelRig.GetComponentsInChildren<Renderer>(true))
                renderer.gameObject.layer = visibleLayer;
        }

        private void ConvertModelMaterialsForUrp()
        {
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (!unlit || !unlit.isSupported)
                throw new InvalidOperationException("The game's URP Unlit shader is not available yet.");
            var cache = new Dictionary<Material, Material>();
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                // TextMesh's font shader needs its own atlas and alpha blending.
                if (renderer.GetComponent<TextMesh>()) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (!source) continue;
                    if (source.shader.name == "Isaiah/OriginalMeshScreen" || source.shader.name == "Isaiah/OriginalLit" || source.GetTag("RenderPipeline", false, "") == "UniversalPipeline") continue;
                    if (!cache.TryGetValue(source, out Material replacement))
                    {
                        bool isLit = source.shader.name == "Standard" || source.shader.name == "Isaiah/OriginalLit";
                        Shader shader = isLit ? Shader.Find("Universal Render Pipeline/Lit") : unlit;
                        if (!shader) throw new InvalidOperationException("Missing URP material shader");
                        replacement = new Material(shader) { name = source.name };
                        if (isLit) {
                            replacement.SetFloat("_Smoothness", source.GetFloat("_Glossiness")); replacement.SetFloat("_Metallic", source.GetFloat("_Metallic"));
                            replacement.SetFloat("_Cull", source.HasProperty("_Cull") ? source.GetFloat("_Cull") : 2f);
                        }
                        Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") :
                            source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
                        replacement.SetColor("_BaseColor", color);
                        string map = source.HasProperty("_BaseMap") ? "_BaseMap" :
                            source.HasProperty("_MainTex") ? "_MainTex" : null;
                        if (map != null)
                        {
                            replacement.SetTexture("_BaseMap", source.GetTexture(map));
                            replacement.SetTextureScale("_BaseMap", source.GetTextureScale(map));
                            replacement.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
                        }
                        cache.Add(source, replacement);
                        convertedMaterials.Add(replacement);
                    }
                    materials[i] = replacement;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private Material MakeMaterial(string name, Color color)
        {
            // This shader is a standalone bundle asset. Load it explicitly;
            // Shader.Find alone only sees shaders already loaded by other assets.
            Shader shader = bundle ? bundle.LoadAsset<Shader>("assets/isaiahcamera/cameraui.shader") : Shader.Find("Isaiah/UI");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("Unlit display shader is unavailable.");
            Material result = new Material(shader) { name = name, enableInstancing = true };
            SetMaterialColor(result, color);
            if (result.HasProperty("_Cull")) result.SetFloat("_Cull", (float)CullMode.Back);
            if (result.HasProperty("_ZWrite")) result.SetFloat("_ZWrite", 1f);
            convertedMaterials.Add(result);
            return result;
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        private static void SetMaterialTexture(Material material, Texture value)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", value);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", value);
        }

        private static void SetMaterialUv(Material material, Vector4 uv)
        {
            foreach (string map in new[] { "_BaseMap", "_MainTex" })
                if (material.HasProperty(map))
                {
                    material.SetTextureScale(map, new Vector2(uv.x, uv.y));
                    material.SetTextureOffset(map, new Vector2(uv.z, uv.w));
                }
        }

        private Vector2Int CurrentCaptureSize()
        {
            int width = Screen.width > 1 ? Screen.width : 1920;
            int height = Screen.height > 1 ? Screen.height : 1080;
            return CameraViewMath.CaptureSize(width, height, Mathf.Clamp(textureWidth.Value, 128, 4096),
                Mathf.Clamp(textureHeight.Value, 128, 4096));
        }

        private static RenderTexture CreateFeed(Vector2Int size)
        {
            var result = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32)
            {
                name = "IsaiahCamera_LiveView", antiAliasing = 1,
                useMipMap = false, autoGenerateMips = false,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            if (!result.Create()) { Destroy(result); throw new InvalidOperationException("Could not allocate the live camera texture."); }
            return result;
        }

        private void EnsureCaptureAspect()
        {
            if (Screen.width <= 1 || Screen.height <= 1) return; // Minimized window: keep the last valid feed.
            Vector2Int size = CurrentCaptureSize();
            bool lost = !texture || !texture.IsCreated();
            if (!lost && captureCamera.targetTexture != texture) captureCamera.targetTexture = texture;
            if (!lost && captureWindowWidth == Screen.width && captureWindowHeight == Screen.height &&
                texture.width == size.x && texture.height == size.y) return;
            if (lost || texture.width != size.x || texture.height != size.y)
            {
                RenderTexture next = CreateFeed(size); // Allocate before releasing the working texture.
                RenderTexture previous = texture;
                texture = next;
                captureCamera.targetTexture = texture;
                urpRequest.destination = standardRequest.destination = texture;
                SetMaterialTexture(screenMaterial, texture);
                SetMaterialTexture(desktopMaterial, texture);
                haveLiveFrame = false;
                if (previous) { previous.Release(); Destroy(previous); }
            }
            captureWindowWidth = Screen.width; captureWindowHeight = Screen.height;
            float aspect = (float)captureWindowWidth / captureWindowHeight;
            captureCamera.rect = new Rect(0f, 0f, 1f, 1f);
            captureCamera.aspect = aspect;
            captureCamera.ResetProjectionMatrix();
            SetMaterialUv(screenMaterial, CameraViewMath.CoverUv(aspect, previewAspect));
            SetMaterialUv(desktopMaterial, new Vector4(1f, 1f, 0f, 0f));
            desktopWidth = desktopHeight = 0;
            Logger.LogInfo("Full-window capture: window=" + captureWindowWidth + "x" + captureWindowHeight +
                "; texture=" + size.x + "x" + size.y + "; aspect=" + aspect.ToString("F4") + "; desktop bars=none.");
        }

        private static GameObject MakePrimitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Collider collider = go.GetComponent<Collider>();
            if (collider) { collider.enabled = false; Destroy(collider); }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            Renderer renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private static Bounds BoundsInFrame(Transform source, Transform frame)
        {
            Bounds sourceBounds = MeshBoundsIn(source);
            Bounds result = new Bounds(frame.InverseTransformPoint(source.TransformPoint(Corner(sourceBounds, 0))), Vector3.zero);
            for (int i = 1; i < 8; i++) result.Encapsulate(frame.InverseTransformPoint(source.TransformPoint(Corner(sourceBounds, i))));
            return result;
        }

        private void PollInput()
        {
            // Run in LateUpdate after Gorilla Tag's poller updates SteamVR actions.
            // Pokruks' provided InputManager also reads these public game fields.
            // CommonUsages remains available for XR providers exposing the button.
            ControllerInputPoller poller = ControllerInputPoller.instance;
            bool pollerActive = poller && poller.isActiveAndEnabled;
            bool gameRightAvailable = pollerActive && poller.RightHandValid;
            bool gameLeftAvailable = pollerActive && poller.LeftHandValid;
            if (pollerActive)
            {
                if (poller.rightControllerDevice.isValid) rightController = poller.rightControllerDevice;
                if (poller.leftControllerDevice.isValid) leftController = poller.leftControllerDevice;
            }
            bool xrAvailable = TryReadControllerFeature(XRNode.RightHand, CommonUsages.primaryButton,
                ref rightController, out bool xrDown);
            ControllerButtonState input = ControllerButtonState.Select(gameRightAvailable,
                gameRightAvailable && poller.rightControllerPrimaryButton, xrAvailable, xrDown);
            inputAvailable = input.Available;
            aPhase = aButton.Sample(input.Available, input.Down);
            // Decide from physical visibility, independently of the recording mode.
            aAction = aGesture.Sample(aPhase, modelRig && modelRig.activeInHierarchy, inputAvailable, Time.unscaledTime);
            if (input.Available)
            {
                if (!loggedController || lastInputSource != input.Source)
                {
                    loggedController = true;
                    lastInputSource = input.Source;
                    Logger.LogInfo("Right A input available: source=" + input.Source +
                        "; game field=rightControllerPrimaryButton; XR primaryButton supported=" + xrAvailable + ".");
                }
            }
            else if (Time.unscaledTime >= nextInputWarning)
            {
                nextInputWarning = Time.unscaledTime + 10f;
                loggedController = false;
                Logger.LogWarning("Right A input waiting: game poller active=" + pollerActive +
                    ", right hand valid=" + gameRightAvailable + ", XR primaryButton supported=" + xrAvailable + ".");
            }
            leftGripPhase = ReadGrip(XRNode.LeftHand, ref leftController, leftGrip,
                gameLeftAvailable, gameLeftAvailable && poller.leftGrab);
            rightGripPhase = ReadGrip(XRNode.RightHand, ref rightController, rightGrip,
                gameRightAvailable, gameRightAvailable && poller.rightGrab);
            if (aPhase == ButtonPhase.Pressed)
            {
                if (!ready || !nativeCamera || !nativeCamera.isActiveAndEnabled) spawnRequested = true;
                Logger.LogInfo("Right A pressed. Source=" + input.Source +
                    (ready ? "; tap/hold gesture started." : "; spawn queued until camera setup is ready."));
            }
        }

        private bool TryReadControllerFeature(XRNode node, InputFeatureUsage<bool> feature,
            ref InputDevice cached, out bool down)
        {
            if (cached.isValid && cached.TryGetFeatureValue(feature, out down)) return true;
            InputDevice nodeDevice = InputDevices.GetDeviceAtXRNode(node);
            if (nodeDevice.isValid && nodeDevice.TryGetFeatureValue(feature, out down))
            {
                cached = nodeDevice;
                return true;
            }
            // Some XR providers expose the controller before assigning the hand node.
            controllerCandidates.Clear();
            InputDeviceCharacteristics side = node == XRNode.RightHand ?
                InputDeviceCharacteristics.Right : InputDeviceCharacteristics.Left;
            InputDevices.GetDevicesWithCharacteristics(side | InputDeviceCharacteristics.Controller, controllerCandidates);
            foreach (InputDevice candidate in controllerCandidates)
                if (candidate.isValid && candidate.TryGetFeatureValue(feature, out down))
                {
                    cached = candidate;
                    return true;
                }
            down = false;
            return false;
        }

        private ButtonPhase ReadGrip(XRNode node, ref InputDevice device, XRButtonEdges edges,
            bool gameAvailable, bool gameDown)
        {
            bool available = TryReadControllerFeature(node, CommonUsages.gripButton, ref device, out bool down);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked)
            {
                available = false;
                gameAvailable = false;
            }
            ControllerButtonState input = ControllerButtonState.Select(gameAvailable, gameDown, available, down);
            return edges.Sample(input.Available, input.Down);
        }

        private void LateUpdate()
        {
            // Input must run even when the model is hidden or setup is waiting.
            PollInput();
            if (!ready) return;
            try
            {
                if (!modelRig || !model || !captureCamera)
                    throw new InvalidOperationException("Camera objects were destroyed; recreating them.");
                if (!TryBindPlayer())
                {
                    if (placement == PlacementMode.HeadFollow || placement == PlacementMode.HeldInHand) FreezeInPlace();
                    modelRig.SetActive(false);
                    leftTouch.Reset(); rightTouch.Reset();
                    RestoreDesktopOutput();
                    snap = true;
                    return;
                }
                TickButtonConfirmation();
                UpdatePlacement();
                UpdateLocalTimePresentation();
                ApplyPhysicalVisibility();
                if (!viewSessionActive) return;
                if (modelRig.activeInHierarchy) PollPhysicalButtons();
                EnsureCaptureAspect();
                UpdateView();
                SyncPhysicalPresentation();
                TickCameraNetwork();
                UpdateDesktopOutput();
                snap = false;
            }
            catch (Exception ex)
            {
                Logger.LogError("Camera update failed; rebuilding its objects: " + ex);
                spawnRequested |= placement != PlacementMode.Hidden;
                ReleaseResources();
                if (startup == null) startup = StartCoroutine(InitializeWhenReady());
            }
        }

        private void ApplyAssemblySize(Transform assembly)
        {
            float scale = CameraPreferencesPolicy.ValidFloat(assemblySize.Value, 1.18f, .75f, 1.5f);
            assembly.localScale = Vector3.one * scale;
            currentAssemblyFactor = Mathf.Clamp(modelScale.Value, 1f, 3f) * scale;
            center.localPosition *= scale;
            for (int i = 0; i < grabAnchors.Count; i++) grabAnchors[i] *= scale;
        }

        private void UpdatePlacement()
        {
            if (aAction == AAction.Hide)
            {
                CancelButtonConfirmation();
                placement = PlacementMode.Hidden;
                modeModelHidden = true; controlsRecalled = false;
                modelRig.SetActive(false);
                leftTouch.Reset(); rightTouch.Reset();
                Logger.LogInfo("Tablet hidden by right A tap; selected capture mode and PC feed continue.");
                PlayButtonClick();
                Feedback(false);
                return;
            }
            if (spawnRequested || aAction == AAction.Show)
            {
                // A single tap summons immediately, without waiting for a hold timer.
                // No preset, color, network or smoothness setting is written by recall.
                CancelButtonConfirmation();
                mode = ViewMode.Handheld;
                selfie = true; // A recall/hold enters the existing front/selfie view
                automaticViewFlipped = false;
                viewPoseInitialized = false;
                controlsRecalled = true; modeModelHidden = false;
                placement = PlacementMode.HeadFollow;
                viewSessionActive = true;
                spawnRequested = false;
                snap = true;
                EnsureModelVisibleToHead();
                model.SetActive(true);
                modelRig.SetActive(true);
                FollowHead(true);
                RefreshText();
                PlayButtonClick();
                // A owns HeadFollow until release; one-time setup runs only on entry.
                leftTouch.Latched = rightTouch.Latched = true;
                leftTouch.HasPrevious = rightTouch.HasPrevious = false;
                Logger.LogInfo("Camera spawned. Active=" + modelRig.activeInHierarchy +
                    "; head=" + nativeCamera.transform.position.ToString("F3") +
                    "; body center=" + center.position.ToString("F3") +
                    "; distance=" + Vector3.Distance(nativeCamera.transform.position, center.position).ToString("F3") + " m.");
                Feedback(false);
            }
            else if (aAction == AAction.Follow)
            {
                if (placement != PlacementMode.HeadFollow)
                {
                    // Enter through the same one-time recall path used from hidden.
                    aAction = AAction.Show;
                    UpdatePlacement();
                    return;
                }
                FollowHead(true);
            }
            else if (aAction == AAction.Freeze && placement == PlacementMode.HeadFollow)
                FreezeInPlace();
            else if (placement == PlacementMode.HeadFollow)
            {
                if (aPhase == ButtonPhase.Held) FollowHead(true);
                else FreezeInPlace(); // RELEASE FIRST: never advance toward a new head pose.
            }
            else if (placement == PlacementMode.HeldInHand)
            {
                ButtonPhase grip = heldByLeftHand ? leftGripPhase : rightGripPhase;
                if (grip == ButtonPhase.Held) FollowHoldingHand();
                else FreezeInPlace(); // Letting go of the physical camera freezes its last pose.
            }
            else if (placement == PlacementMode.Hover)
            {
                if (leftGripPhase == ButtonPhase.Pressed && CanGrab(leftHand)) BeginHandGrab(true);
                else if (rightGripPhase == ButtonPhase.Pressed && CanGrab(rightHand)) BeginHandGrab(false);
            }

            if (placement == PlacementMode.Hover && (mode == ViewMode.Handheld || controlsRecalled))
            {
                // World-space lock: no parenting, Lerp, gravity, or leftover motion.
                modelRig.transform.SetPositionAndRotation(hoverPosition, hoverRotation);
                RememberManualPlacement();
            }
        }

        private void FollowHead(bool immediate)
        {
            Transform head = nativeCamera.transform;
            // Align the MODEL's normal forward direction with the HMD so its back
            // screen faces the player, even if the imported FBX axes differ.
            Pose target = CameraSpawnMath.FromHead(head.position, head.rotation, center.localPosition, opticalToModelRotation);
            if (immediate)
            {
                modelRig.transform.SetPositionAndRotation(target.position, target.rotation);
                return;
            }
            Vector3 bodyCenter = target.position + target.rotation * center.localPosition;
            // Summoning follows the head quickly; the footage damping setting
            // must not make the physical tablet trail behind its owner.
            float t = CameraViewMath.DampingAlpha(0.025f, Time.unscaledDeltaTime);
            Quaternion smoothedRotation = Quaternion.Slerp(modelRig.transform.rotation, target.rotation, t);
            Vector3 smoothedCenter = Vector3.Lerp(center.position, bodyCenter, t);
            modelRig.transform.SetPositionAndRotation(
                smoothedCenter - smoothedRotation * center.localPosition, smoothedRotation);
        }

        private void BeginHandGrab(bool left)
        {
            controlsRecalled = true; modeModelHidden = false;
            heldByLeftHand = left;
            Transform hand = left ? leftHand : rightHand;
            // Preserve the pose at pickup instead of snapping the handle to the palm.
            heldPositionOffset = hand.InverseTransformPoint(modelRig.transform.position);
            heldRotationOffset = Quaternion.Inverse(hand.rotation) * modelRig.transform.rotation;
            placement = PlacementMode.HeldInHand;
            viewPoseInitialized = false;
            snap = true;
            RefreshText();
            Feedback(left);
        }

        private void FollowHoldingHand()
        {
            Transform hand = heldByLeftHand ? leftHand : rightHand;
            if (!hand) { FreezeInPlace(); return; }
            Vector3 position = hand.TransformPoint(heldPositionOffset);
            Quaternion rotation = hand.rotation * heldRotationOffset;
            // LateUpdate runs after tracked hand updates. A held prop uses the
            // current pose directly, with no interpolation or Rigidbody delay.
            modelRig.transform.SetPositionAndRotation(position, rotation);
        }

        private bool CanGrab(Transform hand)
        {
            if (!hand) return false;
            float radius = Mathf.Clamp(grabRadius.Value, 0.03f, 0.3f);
            foreach (Vector3 anchor in grabAnchors)
                if ((hand.position - modelRig.transform.TransformPoint(anchor)).sqrMagnitude <= radius * radius)
                    return true;
            return false;
        }

        private void AddHandleAnchor(string name)
        {
            Transform handle = FindUnique(name, false);
            if (handle)
                grabAnchors.Add(modelRig.transform.InverseTransformPoint(handle.TransformPoint(MeshBoundsIn(handle).center)));
        }

        private void FreezeInPlace()
        {
            // Capture the pose that was actually displayed, NOT a new target pose.
            hoverPosition = modelRig.transform.position;
            hoverRotation = modelRig.transform.rotation;
            placement = PlacementMode.Hover;
            if (mode == ViewMode.Handheld || mode == ViewMode.FollowPlayer)
            { viewPoseInitialized = false; snap = true; }
            MakeBodiesKinematic();
            Logger.LogInfo("Camera hovering at " + hoverPosition.ToString("F3") + "; gravity off, kinematic, world pose locked.");
        }

        private void MakeBodiesKinematic()
        {
            foreach (Rigidbody rb in rigidbodies)
            {
                if (!rb) continue;
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                rb.useGravity = false;
                rb.isKinematic = true;
                rb.constraints = RigidbodyConstraints.FreezeAll;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.detectCollisions = false;
            }
        }

        private void UpdateView()
        {
            UpdateViewPose(Time.unscaledDeltaTime);
            captureCamera.fieldOfView = fov;
            captureCamera.nearClipPlane = nearClip;
            captureCamera.farClipPlane = Mathf.Max(500f, nativeCamera.farClipPlane);
            UpdateCaptureVisibility();
            UpdatePreviewOrientation();
        }

        private void UpdateCaptureVisibility()
        {
            GorillaTagger tagger = GorillaTagger.Instance;
            VRRig local = tagger ? tagger.offlineVRRig : null;
            Renderer face = local ? local.faceSkin : null;
            int mirrorMask = tagger && tagger.MirrorCameraCullingMask != null ?
                tagger.MirrorCameraCullingMask.value : 0;
            int mask = CameraViewMath.CaptureMask(nativeCamera.cullingMask, mirrorMask,
                mode == ViewMode.FirstPerson, face ? face.gameObject.layer : -1);
            captureCamera.cullingMask = mask;
            if (mask != lastCaptureMask)
            {
                lastCaptureMask = mask;
                Logger.LogInfo("Capture visibility: mode=" + mode + "; MirrorOnly=" +
                    ((mask & (1 << (int)UnityLayer.MirrorOnly)) != 0) + "; face=" +
                    (face ? face.name + ", layer=" + face.gameObject.layer + ", enabled=" + face.enabled +
                        ", active=" + face.gameObject.activeInHierarchy : "waiting for local face") +
                    ". No avatar mesh, cosmetic or mouth-animation state is modified.");
            }
        }

        private IEnumerator RenderAfterFrame()
        {
            while (true)
            {
                yield return endOfFrame;
                if (!ready || !viewSessionActive || !modelRig || !captureCamera || !nativeCamera ||
                    !nativeCamera.isActiveAndEnabled || Time.unscaledTime < nextRenderAttempt) continue;
                try
                {
                    RenderView();
                    haveLiveFrame = true;
                    UpdateDesktopOutput();
                    if (renderFailureLogged) Logger.LogInfo("Camera screen rendering recovered.");
                    renderFailureLogged = false;
                }
                catch (Exception ex)
                {
                    // A screen failure must never destroy the prop or disable A.
                    nextRenderAttempt = Time.unscaledTime + 2f;
                    haveLiveFrame = false;
                    RestoreDesktopOutput();
                    if (!renderFailureLogged) Logger.LogError("Camera screen render failed; keeping the prop and A input active: " + ex);
                    renderFailureLogged = true;
                }
            }
        }

        private void CreateDesktopOutput()
        {
            // A mono camera presents the SAME texture to display 0. Its tiny scene
            // is far outside the playable world, with a 2 m far clip. It cannot
            // replace, move, disable or retarget the player's stereo camera.
            RestoreDesktopOutput();
            if (desktopRoot) Destroy(desktopRoot);
            if (desktopMaterial) Destroy(desktopMaterial);
            desktopRoot = new GameObject("IsaiahCamera_DesktopOutput");
            desktopRoot.SetActive(false);
            DontDestroyOnLoad(desktopRoot);
            desktopRoot.transform.position = new Vector3(0f, -100000f, 0f);
            desktopCamera = desktopRoot.AddComponent<Camera>();
            desktopCamera.enabled = false;
            ConfigureMonoCamera(desktopCamera);
            desktopCamera.orthographic = true;
            desktopCamera.orthographicSize = 1f;
            desktopCamera.nearClipPlane = 0.1f;
            desktopCamera.farClipPlane = 2f;
            desktopCamera.depth = 32760f;
            desktopCamera.targetDisplay = 0;
            desktopCamera.targetTexture = null; // PC backbuffer, not the headset.
            desktopCamera.clearFlags = CameraClearFlags.SolidColor;
            desktopCamera.backgroundColor = Color.black;
            desktopCamera.allowHDR = false;
            desktopCamera.allowMSAA = false;
            desktopCamera.useOcclusionCulling = false;
            desktopCamera.cullingMask = 1 << 31;
            // The authored screen shader projects UVs from that mesh's coordinates.
            // A display quad needs ordinary 0..1 UVs, not a copy of that shader.
            desktopMaterial = MakeMaterial("Isaiah full-frame desktop", Color.white);
            convertedMaterials.Remove(desktopMaterial); // Separately owned/released.
            SetMaterialTexture(desktopMaterial, texture);
            SetMaterialUv(desktopMaterial, new Vector4(1f, 1f, 0f, 0f));
            GameObject quad = MakePrimitive("DesktopFeed", PrimitiveType.Quad, desktopRoot.transform,
                Vector3.forward, Vector3.one, desktopMaterial);
            quad.layer = 31;
            desktopQuad = quad.transform;
            desktopWidth = desktopHeight = 0;
            haveLiveFrame = false;
        }

        private void UpdateDesktopOutput()
        {
            bool wanted = desktopOutput.Value && ready && viewSessionActive && haveLiveFrame &&
                nativeCamera && nativeCamera.isActiveAndEnabled;
            if (!wanted) { RestoreDesktopOutput(); return; }
            if (!desktopCamera || !desktopRoot || !desktopQuad || !desktopMaterial)
            { CreateDesktopOutput(); haveLiveFrame = true; }
            // Reassert only our presentation objects; never alter the stereo HMD.
            desktopCamera.targetTexture = null;
            desktopCamera.targetDisplay = 0;
            desktopCamera.depth = 32760f;
            desktopCamera.rect = new Rect(0,0,1,1);
            desktopCamera.stereoTargetEye = StereoTargetEyeMask.None;
            SetMaterialTexture(desktopMaterial, texture);
            if (!desktopOwned)
            {
                previousMirrorMode = XRSettings.gameViewRenderMode;
                desktopOwned = true;
                nextDesktopDiscovery = 0f;
                Logger.LogInfo("Desktop output active: display 0, mono, shared " + texture.width + "x" + texture.height +
                    " feed. Headset camera unchanged. Saved mirror mode=" + previousMirrorMode + ".");
            }
            // This setting controls the PC mirror only. XRSettings.enabled and
            // nativeCamera's stereoTargetEye/targetTexture are NEVER changed.
            if (Time.unscaledTime >= nextDesktopDiscovery)
            {
                nextDesktopDiscovery = Time.unscaledTime + 1f;
                // Unity 6 URP asks XRDisplaySubsystem directly for its mirror
                // mode, so save/suppress that path as well as the legacy setting.
                xrDisplays.Clear();
                SubsystemManager.GetSubsystems(xrDisplays);
                foreach (XRDisplaySubsystem display in xrDisplays)
                    if (display.running && !savedMirrorModes.ContainsKey(display))
                        savedMirrorModes.Add(display, display.GetPreferredMirrorBlitMode());
                GorillaTagger tagger = GorillaTagger.Instance;
                if (tagger && tagger.thirdPersonCamera)
                {
                    desktopDiscoveryCameras.Clear();
                    tagger.thirdPersonCamera.GetComponentsInChildren(true, desktopDiscoveryCameras);
                    foreach (Camera camera in desktopDiscoveryCameras)
                    {
                        if (!camera || camera == nativeCamera || camera == captureCamera || camera == desktopCamera ||
                            camera.transform.IsChildOf(nativeCamera.transform) || camera.stereoEnabled) continue;
                        if (!savedDesktopCameras.ContainsKey(camera))
                        {
                            savedDesktopCameras.Add(camera, camera.enabled);
                            Logger.LogInfo("Temporarily replacing native PC camera: " + camera.name);
                        }
                    }
                }
            }
            XRSettings.gameViewRenderMode = GameViewRenderMode.None;
            foreach (var item in savedMirrorModes)
                if (item.Key.running) item.Key.SetPreferredMirrorBlitMode(XRMirrorViewBlitMode.None);
            foreach (var item in savedDesktopCameras)
                if (item.Key && item.Key != nativeCamera) item.Key.enabled = false;

            int width = Mathf.Max(1, Screen.width), height = Mathf.Max(1, Screen.height);
            if (desktopWidth != width || desktopHeight != height)
            {
                desktopWidth = width; desktopHeight = height;
                float windowAspect = (float)width / height;
                desktopCamera.aspect = windowAspect;
                desktopCamera.rect = new Rect(0f, 0f, 1f, 1f);
                // Capture projection/texture now match the window, so the whole
                // frame fills it exactly. There is no letterbox calculation.
                desktopQuad.localScale = new Vector3(2f * windowAspect, 2f, 1f);
            }
            if (!desktopRoot.activeSelf) desktopRoot.SetActive(true);
            desktopCamera.enabled = true;
        }

        private void RestoreDesktopOutput()
        {
            if (desktopCamera) desktopCamera.enabled = false;
            if (desktopRoot) desktopRoot.SetActive(false);
            if (!desktopOwned) return;
            desktopOwned = false;
            XRSettings.gameViewRenderMode = previousMirrorMode;
            foreach (var item in savedMirrorModes)
                if (item.Key.running) item.Key.SetPreferredMirrorBlitMode(item.Value);
            savedMirrorModes.Clear();
            xrDisplays.Clear();
            foreach (var item in savedDesktopCameras)
                if (item.Key && item.Key != nativeCamera) item.Key.enabled = item.Value;
            savedDesktopCameras.Clear();
            Logger.LogInfo("Normal PC spectator/mirror output restored.");
        }

        [BeforeRenderOrder(10000)]
        private void BeforeHeadsetRender()
        {
            if (ready && viewSessionActive) { SyncHeldPose(); SyncFirstPersonPose(); UpdateDesktopOutput(); }
        }

        private void SyncFirstPersonPose()
        {
            if (mode != ViewMode.FirstPerson || !nativeCamera || !captureCamera) return;
            UpdateFirstPersonPose(Time.unscaledDeltaTime);
        }

        private void RenderView()
        {
            // XR can refresh hands/HMD after LateUpdate. Read those live poses again.
            SyncHeldPose();
            SyncFirstPersonPose();
            // Hide just this asset during its own render, including the display.
            // The main VR camera sees it normally once the render finishes.
            // This avoids both shell occlusion from the CENTER and video feedback.
            RefreshCaptureRendererCache();
            hiddenModelRenderers.Clear();
            try
            {
                if(modelRig && modelRig.activeInHierarchy) foreach(Renderer renderer in modelRenderers)
                {
                    if(renderer && renderer.enabled && renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff)
                    {
                        hiddenModelRenderers.Add(renderer);
                        renderer.forceRenderingOff = true;
                    }
                }
                PrepareCaptureCulling();
                if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
                {
                    if (!RenderPipeline.SupportsRenderRequest(captureCamera, urpRequest))
                        throw new NotSupportedException("URP has not enabled SingleCameraRequest rendering yet.");
                    RenderPipeline.SubmitRenderRequest(captureCamera, urpRequest);
                }
                else if (GraphicsSettings.currentRenderPipeline)
                {
                    if (!RenderPipeline.SupportsRenderRequest(captureCamera, standardRequest))
                        throw new NotSupportedException("The active SRP does not support StandardRequest.");
                    RenderPipeline.SubmitRenderRequest(captureCamera, standardRequest);
                }
                else captureCamera.Render();
            }
            finally
            {
                RestoreCaptureVisibility();
                if (captureCamera) captureCamera.ResetCullingMatrix();
                foreach(Renderer renderer in hiddenModelRenderers) if(renderer)renderer.forceRenderingOff=false;
                hiddenModelRenderers.Clear();
            }
        }

        private void PollPhysicalButtons()
        {
            if (placement == PlacementMode.HeadFollow)
            {
                // A hold or either grip blocks BOTH hands. Preserve a latch until
                // each hand leaves all keys after release; no deferred presses.
                leftTouch.Latched = rightTouch.Latched = true;
                leftTouch.HasPrevious = rightTouch.HasPrevious = false;
                foreach (Button button in buttons) UpdateButtonFeedback(button);
                return;
            }
            bool useLeft = leftHand && !(placement == PlacementMode.HeldInHand && heldByLeftHand);
            bool useRight = rightHand && !(placement == PlacementMode.HeldInHand && !heldByLeftHand);
            // The gripping hand cannot press its own camera. The other hand may
            // deliberately press FLIP; the existing guard remains for other keys.
            PollTouchHand(leftTouch, leftHand, leftTouchOffset.Value, useLeft, true);
            PollTouchHand(rightTouch, rightHand, rightTouchOffset.Value, useRight, false);
            if (placement == PlacementMode.HeldInHand)
            {
                TouchHand gripping = heldByLeftHand ? leftTouch : rightTouch;
                gripping.Latched = true;
                gripping.HasPrevious = false;
            }
            foreach (Button button in buttons)
                UpdateButtonFeedback(button);
        }

        private void PollTouchHand(TouchHand touch, Transform hand, Vector3 offset, bool enabled, bool left)
        {
            if (!enabled || !hand) { touch.Reset(); return; }
            Vector3 point = hand.TransformPoint(offset);
            float radius = Mathf.Max(0.025f, Mathf.Clamp(touchRadius.Value, 0.003f, 0.05f));
            // Native hand triggers may be disabled between the game's physics
            // passes. Read their geometry directly; Collider.bounds would be empty.
            if (touch.Collider is SphereCollider sphere)
            {
                Vector3 scale = sphere.transform.lossyScale;
                point = sphere.transform.TransformPoint(sphere.center) + hand.TransformVector(offset);
                radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                radius = Mathf.Clamp(radius, 0.01f, 0.08f);
            }
            else if (touch.Collider && touch.Collider.enabled && touch.Collider.gameObject.activeInHierarchy)
            {
                Bounds bounds = touch.Collider.bounds;
                point = bounds.center + hand.TransformVector(offset);
                radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z), 0.01f, 0.08f);
            }
            // Keep the previous point in camera coordinates: moving the prop into
            // a stationary hand must sweep just like moving a hand into the prop.
            Vector3 current = controls.InverseTransformPoint(point);
            Vector3 previous = touch.HasPrevious ? touch.Previous : current;
            if ((current - previous).sqrMagnitude > 0.25f) previous = current; // teleport, not a button press path
            Button chosen = null;
            float best = float.MaxValue;
            bool insideAny = false;
            foreach (Button button in buttons)
            {
                if (!button.Visual.gameObject.activeInHierarchy || (button.Activation && !button.Activation.gameObject.activeInHierarchy)) continue;
                Vector3 now = button.Visual.InverseTransformPoint(point);
                Vector3 before = button.Visual.InverseTransformPoint(controls.TransformPoint(previous));
                float worldScale = Mathf.Max(.0001f, Mathf.Abs(button.Visual.lossyScale.x));
                float localRadius = (radius + .004f) / worldScale;
                if (now.z > button.HalfSize.z + localRadius && before.z > button.HalfSize.z + localRadius) continue;
                bool inside = CameraTouchMath.SweepBox(now, now, button.HalfSize, localRadius, out _);
                insideAny |= inside;
                if (!CameraTouchMath.SweepBox(before, now, button.HalfSize, localRadius, out float time)) continue;
                Vector3 contact = Vector3.Lerp(before, now, time);
                // Resolve overlapping palm targets to ONE closest key per hand.
                float score = contact.x * contact.x + contact.y * contact.y + time * 0.00001f;
                if (score < best) { best = score; chosen = button; }
            }
            if (!touch.Latched && chosen != null)
            {
                touch.Latched = true;
                bool activated;
                dispatchingPhysicalButton = true;
                try { activated = ActivateButton(chosen.Name); }
                finally { dispatchingPhysicalButton = false; }
                if (activated)
                {
                    Feedback(left);
                    Logger.LogInfo("Touch " + (left ? "left: " : "right: ") + chosen.Name +
                        "; FOV=" + fov.ToString("0") + "; smoothness=" + smoothness.ToString("F2") + "; near=" + nearClip.ToString("F2") + "; mode=" + mode);
                }
            }
            // Sliding across a row while touching it cannot fire every button.
            // Withdraw fully, then touch again; a quick through-pass also latches.
            else if (!insideAny && chosen == null) touch.Latched = false;
            touch.Previous = current;
            touch.HasPrevious = true;
        }

        private bool IsSelected(string name)
        {
            return (selectedPreset != null && name.Length == 12 &&
                    name.StartsWith("Btn_Preset_", StringComparison.Ordinal) && name[11] - '0' == selectedPreset.Value) ||
                (name == "Btn_Network" && networkEnabled) ||
                (name == "Btn_hide_face_Hat" && hideFaceHat) ||
                (name == "Btn_Rain" && rainEnabled) ||
                (name == "Btn_FirstPerson" && mode == ViewMode.FirstPerson) ||
                (name == "Btn_ThirdPerson" && mode == ViewMode.ThirdPerson) ||
                (name == "Btn_Follow_Player" && mode == ViewMode.FollowPlayer) ||
                (name == "Btn_Roll_Lock" && rollLockEnabled) ||
                (name == "Btn_Flip" && OpticalFlipActive) ||
                (name == "Btn_More.002" && morePanel && morePanel.gameObject.activeSelf) ||
                (name == "Btn_Desktop" && desktopOutput.Value);
        }

        private bool Press(string name)
        {
            if (IsButtonBlockedByGrab(name)) return false;
            if (IsProtectedButton(name) && !ConfirmButtonAction(name)) return true;
            if (PressExtended(name)) { FinishButtonAction(); return true; }
            switch (name)
            {
                case "Btn_Flip":
                    if (mode == ViewMode.Handheld) selfie = !selfie;
                    else automaticViewFlipped = !automaticViewFlipped;
                    // Flip is a discrete optical transform AFTER stabilization.
                    // Its unflipped filter history stays valid; no 180-degree
                    // target ever enters Slerp, and position/mode remain intact.
                    if (captureCamera)
                    {
                        captureCamera.transform.rotation *= Quaternion.Euler(0f, 180f, 0f);
                        UpdatePreviewOrientation();
                        SyncPhysicalPresentation();
                    }
                    break;
                case "Btn_FirstPerson":
                    SelectViewMode(mode == ViewMode.FirstPerson ? ViewMode.Handheld : ViewMode.FirstPerson, false);
                    break;
                case "Btn_ThirdPerson":
                    SelectViewMode(ViewMode.ThirdPerson, true);
                    break;
                case "Btn_FollowPlayer": ToggleFollowPlayer(); break;
                case "Btn_FOV_Up": fov = Mathf.Clamp(fov + 5f, 30f, 120f); break;
                case "Btn_FOV_Down": fov = Mathf.Clamp(fov - 5f, 30f, 120f); break;
                case "Btn_Smooth_Up": smoothness = Mathf.Clamp(Mathf.Round((smoothness + 0.05f) * 100f) / 100f, 0f, 1f); break;
                case "Btn_Smooth_Down": smoothness = Mathf.Clamp(Mathf.Round((smoothness - 0.05f) * 100f) / 100f, 0f, 1f); break;
                case "Btn_Near_Clipping_Up": nearClip = Mathf.Clamp(Mathf.Round((nearClip + 0.01f) * 100f) / 100f, 0.01f, 0.5f); break;
                case "Btn_Near_Clipping_Down": nearClip = Mathf.Clamp(Mathf.Round((nearClip - 0.01f) * 100f) / 100f, 0.01f, 0.5f); break;
                case "Btn_More":
                    if (!morePanel) return false;
                    SetMoreMenu(!morePanel.gameObject.activeSelf);
                    break;
                case "Btn_Desktop": desktopOutput.Value = !desktopOutput.Value; break;
                case "Btn_Reset":
                    fov = 70f; smoothness = 0.10f; nearClip = 0.03f;
                    mode = ViewMode.Handheld; selfie = false; automaticViewFlipped = false; snap = true;
                    rollLockEnabled = false;
                    break;
                default: return false;
            }
            FinishButtonAction();
            return true;
        }

        private bool ButtonsBlockedByGrab => placement == PlacementMode.HeldInHand || placement == PlacementMode.HeadFollow;
        private bool IsButtonBlockedByGrab(string name)
        {
            return ButtonsBlockedByGrab && !(placement == PlacementMode.HeldInHand && name == "Btn_Flip");
        }

        private void FinishButtonAction()
        {
            if (snap && captureCamera && nativeCamera) { UpdateView(); SyncPhysicalPresentation(); snap = false; }
            RefreshText();
            SavePreferences();
            PlayButtonClick();
        }

        private void RefreshText()
        {
            // Only the control strip and the optional BELOW-screen panel have
            // readouts. Nothing is ever drawn over the live preview or PC feed.
            smoothReadout?.Set(smoothness.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            fovReadout?.Set(fov.ToString("0"));
            nearReadout?.Set(nearClip.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "M");
            string view = mode == ViewMode.Handheld ? (selfie ? "SELFIE" : "HANDHELD") : mode.ToString().ToUpperInvariant();
            extraReadout?.Set(string.IsNullOrEmpty(lobbyStatus) ? "LOBBY HOP READY" : lobbyStatus);

            RefreshAuthoredValues();
            RefreshPreferenceLabels();
            rainLabel?.Set("RAIN: " + RainCaption);
            networkLabel?.Set("NETWORK: " + (networkEnabled ? "ON" : "OFF"));
        }

        private void Feedback(bool left)
        {
            GorillaTagger tagger = GorillaTagger.Instance;
            if (tagger) { tagger.StartVibration(left, 0.25f, 0.035f); return; }
            InputDevice device = left ? leftController : rightController;
            HapticCapabilities caps;
            if (device.isValid && device.TryGetHapticCapabilities(out caps) && caps.supportsImpulse && caps.numChannels > 0)
                device.SendHapticImpulse(0u, 0.25f, 0.035f);
        }

        private Transform FindUnique(string name, bool required)
        {
            Transform found = null;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != name) continue;
                if (found) throw new InvalidOperationException("Duplicate prefab object name: " + name);
                found = t;
            }
            if (!found && required) throw new InvalidOperationException("Missing prefab object: " + name);
            return found;
        }

        private static Bounds MeshBoundsIn(Transform frame)
        {
            bool found = false;
            Bounds result = new Bounds();
            foreach (MeshFilter filter in frame.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh) continue;
                Bounds bounds = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = frame.InverseTransformPoint(filter.transform.TransformPoint(Corner(bounds, i)));
                    if (!found) { result = new Bounds(p, Vector3.zero); found = true; }
                    else result.Encapsulate(p);
                }
            }
            if (!found) throw new InvalidOperationException("No mesh geometry under " + frame.name);
            return result;
        }

        private static Vector3 Corner(Bounds b, int i)
        {
            return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        private void OnDisable()
        {
            SavePreferences();
            RestoreLocalTimePresentation();
            Application.onBeforeRender -= BeforeHeadsetRender;
            ReleaseCaptureCulling();
            CancelLobbyHop();
            if (startup != null) StopCoroutine(startup);
            if (renderLoop != null) StopCoroutine(renderLoop);
            startup = null;
            renderLoop = null;
            ReleaseResources();
            spawnRequested = false;
            loggedController = false;
            lastInputSource = ButtonSource.None;
            aButton = new XRButtonEdges();
            aGesture = new CameraAGesture();
            aAction = AAction.None;
            leftGrip = new XRButtonEdges();
            rightGrip = new XRButtonEdges();
            aPhase = leftGripPhase = rightGripPhase = ButtonPhase.Idle;
        }

        private void OnDestroy() { SavePreferences(); RestoreLocalTimePresentation(); ReleaseResources(); }
        private void OnApplicationQuit() { SavePreferences(); RestoreLocalTimePresentation(); }

        private void ReleaseResources()
        {
            StopCameraNetwork();
            ready = false;
            viewSessionActive = false;
            viewPoseInitialized = false;
            ReleaseButtonAudio();
            ReleaseAuthoredControls();
            RestoreDesktopOutput();
            if (desktopRoot) { Destroy(desktopRoot); desktopRoot = null; }
            desktopCamera = null;
            desktopQuad = null;
            haveLiveFrame = false;
            if (captureCamera)
            {
                captureCamera.targetTexture = null;
                Destroy(captureCamera.gameObject);
                captureCamera = null;
            }
            if (modelRig) { modelRig.SetActive(false); Destroy(modelRig); modelRig = null; }
            foreach (CameraLabel label in labels) label.Dispose();
            labels.Clear(); buttonLabels.Clear();
            smoothReadout = fovReadout = nearReadout = extraReadout = null;
            leftTouch.Reset(); rightTouch.Reset();
            if (texture) { texture.Release(); Destroy(texture); texture = null; }
            if (screenMaterial) { Destroy(screenMaterial); screenMaterial = null; }
            if (desktopMaterial) { Destroy(desktopMaterial); desktopMaterial = null; }
            captureWindowWidth = captureWindowHeight = 0;
            lastCaptureMask = int.MinValue;
            foreach (Material material in convertedMaterials) if (material) Destroy(material);
            convertedMaterials.Clear();
            foreach (Mesh mesh in panelMeshes) if (mesh) Destroy(mesh);
            panelMeshes.Clear();
            networkLabel = presetStatus = null;
            urpRequest.destination = null;
            standardRequest.destination = null;
            if (bundle) { bundle.Unload(true); bundle = null; }
            buttons.Clear();
            grabAnchors.Clear();
            rigidbodies = null;
            placement = PlacementMode.Hidden;
        }
    }
}

