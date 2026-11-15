using System.Collections.Generic;
using MetaCodex.UI;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;

namespace MetaCodex.Shell
{
    /// Ambient Focus: a dome of radius RADIUS standing on the floor around you. Real things inside it stay, the rest
    /// of the room fades to INTENSITY (black at 100 %) or, with BLUR, to a soft image of itself; your windows always
    /// stay on top. No Space Setup scan is needed:
    ///   1. a sphere on the head (MetaCodex/AmbientFocus), drawn before everything: per pixel, the real surface's
    ///      distance from the floor under you, from Meta's live depth (EnvironmentDepthManager; the "spatial data"
    ///      permission); without depth, the floor plane alone (everything above the horizon fades);
    ///   2. the floor: the tracking floor, corrected by the depth seen under your feet once it is steady (SandyBridge's
    ///      rule: the Guardian floor can be 10-30 cm off the real one);
    ///   3. BLUR: the passthrough camera (MR Utility Kit's PassthroughCameraAccess, horizonos.permission.HEADSET_CAMERA)
    ///      copied into a small mip-mapped texture and drawn soft outside the dome; Horizon OS composites the
    ///      passthrough itself, so an app cannot blur it directly;
    ///   4. both hands are cut out (capsules on the tracked joints, MetaCodex/FocusMesh), so you always see them.
    public sealed partial class MetaCodexShell
    {
        private GameObject _focusSphere;
        private Material _focusMat;
        private float _focusShown;
        private OVRCameraRig _rig;
        private float _nextRigLookup;
        private UiButton _focusButton;
        private UiLabel _focusInfo;
        private UiToggle _focusToggle;
        private UiSlider _focusRadius, _focusIntensity, _focusSoft, _focusBlur;

        public float FocusShownForTest => _focusShown;
        /// The focus darkness or blur is (being) shown: foveation is on meanwhile (MetaCodexShell.ApplyFoveation).
        private bool FocusShownNow => _focusShown > 0f && Settings.focusEnabled && (Settings.focusIntensity > 0.004f || Settings.focusBlur > 0.001f);
        public GameObject FocusSphereForTest => _focusSphere;
        /// What the dome knows: depth, floor, camera (shown in Settings > FOCUS).
        public string FocusStatus => _depthStatus + " · " + _floorStatus + (_camStatus.Length > 0 ? " · " + _camStatus : "");

        private Material _handMat;
        private readonly Transform[,] _handParts = new Transform[2, 7];
        private static readonly int RadiusId = Shader.PropertyToID("_Radius"), SoftId = Shader.PropertyToID("_Softness"),
            IntensityId = Shader.PropertyToID("_Intensity"), FeetId = Shader.PropertyToID("_FeetPos"), HandId = Shader.PropertyToID("_Hand"),
            FloorYId = Shader.PropertyToID("_FloorY"), BlurMipId = Shader.PropertyToID("_BlurMip");

        // ---- live depth (Meta's Depth API, com.unity.xr.meta-openxr)
        private EnvironmentDepthManager _depth;
        private bool _askedScene, _depthLive, _depthTest, _renderHooked;
        private float _nextPermissionCheck;
        private string _depthStatus = "depth: off", _floorStatus = "floor: tracking", _camStatus = "";
        private readonly List<Matrix4x4> _reproj = new List<Matrix4x4>(2);
        private readonly Matrix4x4[] _depthToWorld = { Matrix4x4.identity, Matrix4x4.identity };
        private static readonly int ReprojId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices"),
            DepthTexId = Shader.PropertyToID("_EnvironmentDepthTexture"), DepthToWorldId = Shader.PropertyToID("_McxDepthToWorld");

        // ---- the floor under the feet, from depth
        private float _feetFloor = float.NaN, _nextFloorRead;
        private int _feetAgree;
        private bool _feetStable, _floorReading;
        private RenderTexture _floorRt;
        private Material _floorCopyMat;
        private const int FloorGrid = 48;

        // ---- the sharp depth (Resources/Shaders/DepthSharp) and real things in front of the UI (McxOcclusion.hlsl)
        private Material _sharpMat;
        private RenderTexture _sharpA, _sharpB;
        private bool _sharpFlip, _sharpHavePrev, _sharpLive;
        private readonly Matrix4x4[] _sharpPrevW2D = { Matrix4x4.identity, Matrix4x4.identity };
        // one matrix per eye each for DepthSharp: depth clip -> the camera's space, depth clip -> last frame's depth clip
        private readonly Matrix4x4[] _sharpD2C = { Matrix4x4.identity, Matrix4x4.identity }, _sharpD2P = { Matrix4x4.identity, Matrix4x4.identity };
        private Matrix4x4 _camW2C = Matrix4x4.identity;
        /// The sharp depth is drawn (focus shown or occlusion on); false while the depth only idles in its hold time.
        private bool _sharpWanted;
        private Texture2DArray _noDepth;
        private float _uiHide;
        private static readonly int SharpId = Shader.PropertyToID("_McxDepthSharp"), SharpPrevId = Shader.PropertyToID("_McxSharpPrev"),
            SharpSliceId = Shader.PropertyToID("_McxSlice"),
            SharpSizeId = Shader.PropertyToID("_McxDepthSize"), SharpHistoryId = Shader.PropertyToID("_McxHistory"),
            SharpD2CId = Shader.PropertyToID("_McxDepthToCam"), SharpD2PId = Shader.PropertyToID("_McxDepthToPrev"),
            OcclusionId = Shader.PropertyToID("_McxOcclusion"), UiHideId = Shader.PropertyToID("_McxUiHide"),
            ZParamsId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        /// The sharp depth's last output (self-tests read it back).
        public RenderTexture SharpDepthForTest => _sharpLive ? (_sharpFlip ? _sharpB : _sharpA) : null; // (flipped after each frame)
        /// Self-tests: the UI steps aside as for the Horizon OS menu (null = decided by the headset).
        public bool? UiStepAsideOverride;

        // ---- the passthrough camera: the blur, and the picture that sharpens the depth
        private Meta.XR.PassthroughCameraAccess _cam;
        private bool _askedCamera, _camLive, _camTest, _camRetried;
        private int _camFps;
        private System.DateTime _camStamp;
        private float _camDisabledAt = -1f;
        private static readonly int CamTexId = Shader.PropertyToID("_McxCamTex"), CamW2CId = Shader.PropertyToID("_McxCamWorldToCam"),
            CamIntrId = Shader.PropertyToID("_McxCamIntr"), CamCropId = Shader.PropertyToID("_McxCamCrop");

        // ---- BLUR: the room around the user as the camera saw it (FocusBlurPano), read by AmbientFocus by direction
        private RenderTexture _blurPano;
        private Material _panoMat;
        private CommandBuffer _panoCb;
        private Vector3 _panoOrigin;
        private bool _panoReady;
        /// 1024 x 512 over 360 x 180 degrees (half floats, 5.3 MB with its mips): ~2.8 texels per degree before the blur's mip level.
        private const int PanoW = 1024, PanoH = 512;
        /// Walking further than this from where the panorama began starts a new one (another part of the room).
        private const float PanoResetMetres = 1.5f;
        /// How much of each camera frame goes in (the rest is what was there): steady, and quick to follow.
        private const float PanoBlend = 0.35f;
        private static readonly int PanoId = Shader.PropertyToID("_McxBlurPano"), PanoSrcId = Shader.PropertyToID("_McxCamSrc"),
            PanoW2CId = Shader.PropertyToID("_PanoCamW2C"), PanoIntrId = Shader.PropertyToID("_PanoCamIntr"), PanoCropId = Shader.PropertyToID("_PanoCamCrop"),
            PanoBlendId = Shader.PropertyToID("_PanoBlend");

        public void SetFocus(bool on, bool toast = true)
        {
            Settings.focusEnabled = on;
            OnSettingsChanged();
            _focusButton?.SetSelected(on);
            _focusToggle?.Set(on);
            RefreshFocusPanel();
            if (toast) Toast(on ? $"Ambient focus on · {Settings.focusRadius:0.0} m · {Settings.focusIntensity * 100f:0}%" : "Ambient focus off", "info");
        }

        public void ApplyFocusPreset(string name)
        {
            switch (name)
            {
                case "subtle": Settings.focusRadius = 2.2f; Settings.focusIntensity = 0.45f; Settings.focusSoftness = 1.4f; break;
                case "blackout": Settings.focusRadius = 0f; Settings.focusIntensity = 1f; Settings.focusSoftness = 0.05f; break;
                default: Settings.focusRadius = 1.2f; Settings.focusIntensity = 0.8f; Settings.focusSoftness = 0.6f; break;
            }
            Settings.focusEnabled = true;
            OnSettingsChanged();
            _focusButton?.SetSelected(true);
            _focusToggle?.Set(true);
            _focusRadius?.Set(Settings.focusRadius);
            _focusIntensity?.Set(Settings.focusIntensity);
            _focusSoft?.Set(Settings.focusSoftness);
            RefreshFocusPanel();
        }

        /// Every frame: the UI steps aside for the Horizon OS menu; the depth runs while focus or "real things in front
        /// of windows" needs it, the camera while the blur or the sharp depth does (both kept running, idle, for
        /// DepthCamHold seconds after the last need: a restart costs the headset seconds of low frame rate); the sphere
        /// fades in / out and stays on the head; the hands are cut out of everything.
        private void UpdateFocus(float dt)
        {
            if (Settings == null) return;
            EnsureFocusDefaults();
            bool aside = UiShouldStepAside();
            _uiHide = Mathf.MoveTowards(_uiHide, aside ? 1f : 0f, dt / 0.15f);
            Shader.SetGlobalFloat(UiHideId, _uiHide);
            _focusShown = Mathf.MoveTowards(_focusShown, Settings.focusEnabled && !aside ? 1f : 0f, dt / (aside ? 0.15f : 0.4f));
            // on but drawing nothing (INTENSITY 0 and no BLUR): no sphere, no depth, no camera for it (0.7.7 kept all three
            // running for an invisible darkness; the owner's settings were exactly that)
            bool shown = _focusShown > 0f && (Settings.focusIntensity > 0.004f || Settings.focusBlur > 0.001f);
            ApplyFoveation(); // on while the focus is shown (FocusShownNow)
            bool mayStart = FocusInitMayStart(); // the native starts of the depth and the camera wait for a first start's install
            bool wantDepth = mayStart && (shown || Settings.occludeUi);
            _sharpWanted = wantDepth;
            EnsureDepth(Held(wantDepth, _depth != null && _depth.enabled, ref _depthUnwantedSince));
            if (_depthLive) _depthSeen = true;
            // the camera is never stopped for a voice command (2026-11-13: it was stopped while speech was turned into text
            // and the command decided, and started again after; each restart cost the headset seconds of low frame rate,
            // the owner's hard lag at every dictation). While the voice is busy it keeps running and only its frames are
            // not used (the blur keeps the room it has, the sharp depth the last picture).
            bool voiceBusy = VoiceState == "transcribing" || VoiceState == "thinking";
            bool wantCam = mayStart && ((shown && Settings.focusBlur > 0.001f) || (wantDepth && Settings.depthCamera && _depthSeen));
            _camUseFrames = wantCam && !voiceBusy;
            EnsureCamera(Held(wantCam, _cam != null && _cam.gameObject.activeSelf, ref _camUnwantedSince));
            Shader.SetGlobalFloat(OcclusionId, Settings.occludeUi && _sharpLive ? 1f : 0f);
            UpdatePanelOcclusionFlags();
            // your hands always show in front of windows (owner 2026-11-10: with "Real things in front of windows" off the
            // windows covered them); that setting is only the room's depth: people and things
            if (HandCutoutsForTest) UpdateHandCutouts(); else HideHandCutouts();
            if (!shown)
            {
                if (_focusSphere != null && _focusSphere.activeSelf) _focusSphere.SetActive(false);
                // the room's points still tell which windows something real can be in front of (and where the floor is)
                if (Settings.occludeUi) UpdateFloorFromDepth(Head);
                return;
            }
            if (!EnsureFocusSphere()) return;
            if (!_focusSphere.activeSelf) _focusSphere.SetActive(true);
            var head = Head;
            if (head != null) _focusSphere.transform.position = head.position;
            float radius = Mathf.Max(0f, Settings.focusRadius), soft = Mathf.Max(0.02f, Settings.focusSoftness), intensity = Mathf.Clamp01(Settings.focusIntensity) * _focusShown;
            float floor = FloorY();
            var feet = head != null ? new Vector4(head.position.x, floor, head.position.z, 0f) : new Vector4(0f, floor, 0f, 0f);
            _focusMat.SetFloat(RadiusId, radius);
            _focusMat.SetFloat(SoftId, soft);
            _focusMat.SetFloat(IntensityId, intensity);
            _focusMat.SetFloat(FloorYId, floor);
            _focusMat.SetVector(FeetId, feet);
            SetKeyword(_focusMat, "MCX_DEPTH", _depthLive && _sharpLive);
            bool blur = _panoReady && Settings.focusBlur > 0.001f;
            SetKeyword(_focusMat, "MCX_BLUR", blur);
            // BLUR 1 % .. 100 %: mip 1.6 (soft) .. 5.1 (only colours) of the 1024 x 512 panorama (the old 2.5 .. 6 of a
            // 512 x 384 camera copy, at the same angular softness)
            if (blur) _focusMat.SetFloat(BlurMipId, Mathf.Lerp(1.6f, 5.1f, Mathf.Clamp01(Settings.focusBlur)));
            UpdateVoid();
            UpdateFloorFromDepth(head);
        }

        // ---- the first start (perf/first-run, 0.7.19 log): starting the environment depth and the passthrough camera each stop
        // the main thread for a while (frame 403 ms at the depth's first frame, 278 ms with 158 ms in this step at the
        // camera's start, both within 1 s of the 5.9 s start freeze and right as the Linux began to install). They are
        // native calls: they cannot be spread over frames, so they wait for a quiet moment: until the Linux is ready (or
        // failed) and, when it was installed in this session, 1.5 s more so they do not land on the install's last frames,
        // the overlay and MQH starting. A start with the Linux already there waits for nothing but its Prepare (a few
        // frames). Not on the Editor. Gives up after 180 s.
        // On the very first start they also wait for the one-time setup to end (2026-11-15 log: 2 x 56 ms, focus 42.5 ms,
        // as MQH and the voice's decision model were starting, and the depth's occlusion test then added GPU time to frames
        // already at 94-97 % with the setup's windows): the setup is ~20 s, real things hide the windows from its end.
        private const float FocusInitAfterInstall = 1.5f, FocusInitMaxWait = 180f;
        private bool _focusInitOpen, _focusInitSawInstall;
        private float _focusInitReadyAt = -1f;

        private bool FocusInitMayStart()
        {
            if (_focusInitOpen) return true;
            if (Application.isEditor || Application.platform != RuntimePlatform.Android) return _focusInitOpen = true;
            var host = app != null ? app.Host : null;
            float now = Time.unscaledTime;
            if (host == null || now >= FocusInitMaxWait) return _focusInitOpen = true;
            if (_setupActive) { _focusInitReadyAt = -1f; _focusInitSawInstall = true; return false; } // the first start's setup runs (ShellFirstRun)
            var st = host.State;
            if (st == MetaCodex.Linux.LinuxHost.HostState.Installing) _focusInitSawInstall = true;
            if (st != MetaCodex.Linux.LinuxHost.HostState.Ready && st != MetaCodex.Linux.LinuxHost.HostState.Failed && st != MetaCodex.Linux.LinuxHost.HostState.Unsupported) return false;
            if (_focusInitReadyAt < 0f) _focusInitReadyAt = now;
            if (_focusInitSawInstall && now - _focusInitReadyAt < FocusInitAfterInstall) return false;
            Log($"ambient focus: depth and camera may start now (t={now:F1} s, Linux {st}{(_focusInitSawInstall ? ", installed this session" : "")})");
            return _focusInitOpen = true;
        }

        /// The Horizon OS menu: while a hand does the system gesture (palm up, pinch) or the app has lost input focus
        /// (the universal menu is open), the windows and the focus darkness fade out so the system's menu shows.
        private bool UiShouldStepAside()
        {
            if (UiStepAsideOverride.HasValue) return UiStepAsideOverride.Value;
            // not on the system gesture itself (palm towards you, pinch): Horizon OS reports it for as long as the palm
            // faces you, and the windows vanished until the hands were lowered (owner, 0.7.3: the gesture must not hide
            // anything). Only when a system menu has opened and taken the app's input focus.
#if !UNITY_EDITOR
            if (OVRManager.instance != null && !OVRManager.hasInputFocus) return true;
#endif
            return false;
        }

        /// Globals every UI shader reads (McxOcclusion.hlsl) get a value before any depth exists: "no reading".
        private void EnsureFocusDefaults()
        {
            if (_noDepth != null) return;
            _noDepth = new Texture2DArray(1, 1, 2, TextureFormat.RHalf, false, true) { name = "no depth", filterMode = FilterMode.Point };
            _noDepth.SetPixelData(new ushort[] { 0 }, 0, 0);
            _noDepth.SetPixelData(new ushort[] { 0 }, 0, 1);
            _noDepth.Apply(false, true);
            Shader.SetGlobalTexture(SharpId, _noDepth);
            Shader.SetGlobalFloat(OcclusionId, 0f);
            Shader.SetGlobalFloat(UiHideId, 0f);
        }

        private static void SetKeyword(Material m, string keyword, bool on)
        {
            if (m.IsKeywordEnabled(keyword) == on) return;
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        private void SetFocusStatus(ref string field, string value)
        {
            if (field == value) return;
            field = value;
            if (value.Length > 0) Log("ambient focus: " + value); // the session log tells what the headset did
            RefreshFocusPanel();
        }

        // ---- live depth

        /// Once started, the depth and the camera stay on (idle) until they have not been needed for this long: turning
        /// either on again costs the headset seconds of low frame rate (measured 2026-11-13: 17.7 fps for ~15 s).
        private const float DepthCamHold = 60f;
        private float _depthUnwantedSince = -1f, _camUnwantedSince = -1f;
        private bool _depthSeen, _camUseFrames = true;

        /// `want`, kept true for DepthCamHold seconds after it turned false while the thing is running.
        private static bool Held(bool want, bool running, ref float unwantedSince)
        {
            if (want || !running) { unwantedSince = -1f; return want; }
            if (unwantedSince < 0f) unwantedSince = Time.unscaledTime;
            return Time.unscaledTime - unwantedSince < DepthCamHold;
        }

        private void EnsureDepth(bool want)
        {
            if (_depthTest) return;
            if (!want)
            {
                if (_depth != null && _depth.enabled) _depth.enabled = false;
                _depthLive = false;
                if (_renderHooked) { RenderPipelineManager.beginContextRendering -= OnFocusBeginRendering; _renderHooked = false; }
                return;
            }
#if UNITY_EDITOR
            SetFocusStatus(ref _depthStatus, "depth: none in the Editor (floor plane)");
#else
            if (!EnvironmentDepthManager.IsSupported) { SetFocusStatus(ref _depthStatus, "depth: not available on this headset (floor plane)"); return; }
            bool permitted = true;
#if UNITY_ANDROID
            if (Time.unscaledTime >= _nextPermissionCheck || !_askedScene)
            {
                _nextPermissionCheck = Time.unscaledTime + 1f;
                permitted = UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
                if (!permitted && !_askedScene) { _askedScene = true; UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission); }
            }
            else permitted = _depth != null && _depth.IsDepthAvailable;
#endif
            if (_depth == null)
            {
                // created disabled so Meta's occlusion shaders stay off (they would add a depth pass per frame)
                var go = new GameObject("EnvironmentDepth");
                go.SetActive(false);
                go.transform.SetParent(_root, false);
                _depth = go.AddComponent<EnvironmentDepthManager>();
                _depth.OcclusionShadersMode = OcclusionShadersMode.None;
                _depth.RemoveHands = false; // hands near you are inside the dome anyway
                go.SetActive(true);
            }
            if (!_depth.enabled) _depth.enabled = true;
            if (!_renderHooked) { RenderPipelineManager.beginContextRendering += OnFocusBeginRendering; _renderHooked = true; }
            SetFocusStatus(ref _depthStatus, _depth.IsDepthAvailable ? "depth: live (walls and furniture count)"
                : !permitted ? "depth: waiting for the \"spatial data\" permission (floor plane until then)" : "depth: starting…");
#endif
        }

        /// Before each frame is drawn (after Meta's manager updated the depth in onBeforeRender): the inverse of its
        /// world -> depth matrices, so the shader can turn a depth sample back into a world position.
        private void OnFocusBeginRendering(ScriptableRenderContext ctx, List<Camera> cameras)
        {
            if (_depthTest) return;
            _depthLive = false;
            if (_depth == null || !_depth.enabled || !_depth.IsDepthAvailable) return;
            _reproj.Clear();
            Shader.GetGlobalMatrixArray(ReprojId, _reproj);
            if (_reproj.Count < 2) return;
            for (int i = 0; i < 2; i++) _depthToWorld[i] = _reproj[i].inverse;
            Shader.SetGlobalMatrixArray(DepthToWorldId, _depthToWorld);
            _depthLive = true;
            if (!_sharpWanted) return; // the depth idles in its hold time: no sharp depth drawn
            // a new sharp depth only for a new depth frame (its matrices change with it): Meta's depth comes slower than
            // the 72 Hz frames, and the same input again only cost GPU time (0.7.19: before, every frame, with a new
            // command buffer each time: garbage for the GC)
            if (_sharpLive && _sharpHavePrev && _reproj[0] == _sharpPrevW2D[0] && _reproj[1] == _sharpPrevW2D[1]) return;
            if (_sharpCb == null) _sharpCb = new CommandBuffer { name = "MetaCodex sharp depth" };
            _sharpCb.Clear();
            if (RecordSharpDepth(_sharpCb, Shader.GetGlobalTexture(DepthTexId), _reproj)) ctx.ExecuteCommandBuffer(_sharpCb);
        }

        private CommandBuffer _sharpCb;

        /// Self-tests: Meta's depth inputs as EnvironmentDepthManager sets them (a depth Texture2DArray holding
        /// (ndc + 1) / 2 per eye, and world -> depth clip matrices); null goes back to the live depth.
        public void SetDepthForTest(Texture depthArray, Matrix4x4[] worldToDepth, Vector4 zBufferParams, bool keepHistory = false)
        {
            _depthTest = depthArray != null;
            _depthLive = _depthTest;
            if (!_depthTest) { _sharpLive = false; _sharpHavePrev = false; Shader.SetGlobalTexture(SharpId, _noDepth); return; }
            EnsureFocusDefaults();
            var m = new System.Collections.Generic.List<Matrix4x4> { worldToDepth[0], worldToDepth[Mathf.Min(1, worldToDepth.Length - 1)] };
            Shader.SetGlobalTexture(DepthTexId, depthArray);
            Shader.SetGlobalMatrixArray(ReprojId, m);
            Shader.SetGlobalVector(ZParamsId, zBufferParams);
            for (int i = 0; i < 2; i++) _depthToWorld[i] = m[i].inverse;
            Shader.SetGlobalMatrixArray(DepthToWorldId, _depthToWorld);
            if (!keepHistory) _sharpHavePrev = false;
            var cb = new CommandBuffer { name = "MetaCodex sharp depth (test)" };
            if (RecordSharpDepth(cb, depthArray, m)) Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            SetFocusStatus(ref _depthStatus, "depth: test input");
        }

        /// The sharp depth of this frame into the other buffer of the pair (both eyes), with last frame's as history;
        /// the camera's picture guides it when the camera runs. Publishes _McxDepthSharp.
        private bool RecordSharpDepth(CommandBuffer cb, Texture depth, System.Collections.Generic.List<Matrix4x4> worldToDepth)
        {
            if (depth == null || worldToDepth == null || worldToDepth.Count < 2) return false;
            if (_sharpMat == null)
            {
                var sh = Resources.Load<Shader>("Shaders/DepthSharp");
                if (sh == null) { SetFocusStatus(ref _depthStatus, "depth: sharp-depth shader missing"); return false; }
                _sharpMat = new Material(sh) { name = "sharp depth" };
            }
            // 1.5 x the sensor when the camera guides it (the picture moves edges between readings), the sensor's own
            // resolution without it (2026-11-13: always 2 x, 640 x 640 x 2: 1.8 x the pixels of 480 x 480, 4 x of 320 x 320)
            bool guide = Settings == null || Settings.depthCamera;
            float scale = guide ? 1.5f : 1f;
            int w = Mathf.Min(Mathf.RoundToInt(depth.width * scale), 480), h = Mathf.Min(Mathf.RoundToInt(depth.height * scale), 480);
            if (_sharpA == null || _sharpA.width != w || _sharpA.height != h)
            {
                foreach (var old in new[] { _sharpA, _sharpB }) if (old != null) { old.Release(); if (Application.isPlaying) Destroy(old); else DestroyImmediate(old); }
                RenderTexture Make(string n) { var rt = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf) { name = n, dimension = TextureDimension.Tex2DArray, volumeDepth = 2, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; rt.Create(); return rt; }
                _sharpA = Make("sharp depth A");
                _sharpB = Make("sharp depth B");
                _sharpHavePrev = false;
            }
            var prev = _sharpFlip ? _sharpB : _sharpA;
            var cur = _sharpFlip ? _sharpA : _sharpB;
            _sharpMat.SetVector(SharpSizeId, new Vector4(depth.width, depth.height, 1f / depth.width, 1f / depth.height));
            _sharpMat.SetTexture(SharpPrevId, prev);
            for (int i = 0; i < 2; i++)
            {
                _sharpD2C[i] = _camW2C * _depthToWorld[i];
                _sharpD2P[i] = _sharpPrevW2D[i] * _depthToWorld[i];
            }
            _sharpMat.SetMatrixArray(SharpD2CId, _sharpD2C);
            _sharpMat.SetMatrixArray(SharpD2PId, _sharpD2P);
            _sharpMat.SetFloat(SharpHistoryId, _sharpHavePrev ? 0.7f : 0f);
            SetKeyword(_sharpMat, "MCX_GUIDE", _camLive && guide);
            for (int slice = 0; slice < 2; slice++)
            {
                cb.SetRenderTarget(new RenderTargetIdentifier(cur, 0, CubemapFace.Unknown, slice));
                cb.SetGlobalFloat(SharpSliceId, slice);
                cb.DrawProcedural(Matrix4x4.identity, _sharpMat, 0, MeshTopology.Triangles, 3);
            }
            cb.SetGlobalTexture(SharpId, cur);
            _sharpPrevW2D[0] = worldToDepth[0];
            _sharpPrevW2D[1] = worldToDepth[1];
            _sharpHavePrev = true;
            _sharpFlip = !_sharpFlip;
            _sharpLive = true;
            return true;
        }

        // ---- the floor under the feet (SandyBridge's rule, light)

        /// Every 0.3 s while the depth runs: a 48 x 48 copy of the left eye's depth (Resources/Shaders/FocusDepthCopy:
        /// raw depth + its uv per texel, so row order does not matter) read back, turned into world points.
        private void UpdateFloorFromDepth(Transform head)
        {
            if (!_depthLive || _depthTest || head == null || _floorReading || Time.unscaledTime < _nextFloorRead) return;
            _nextFloorRead = Time.unscaledTime + 0.3f;
            if (_floorCopyMat == null)
            {
                var sh = Resources.Load<Shader>("Shaders/FocusDepthCopy");
                if (sh == null) return;
                _floorCopyMat = new Material(sh) { name = "focus floor depth" };
            }
            if (_floorRt == null) _floorRt = new RenderTexture(FloorGrid, FloorGrid, 0, RenderTextureFormat.ARGBFloat) { name = "focus floor depth" };
            var toWorld = _depthToWorld[0];
            Vector3 headPos = head.position;
            float track = TrackingFloorY();
            var cb = new CommandBuffer { name = "focus floor depth" };
            cb.SetRenderTarget(_floorRt);
            cb.DrawProcedural(Matrix4x4.identity, _floorCopyMat, 0, MeshTopology.Triangles, 3);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            _floorReading = true;
            AsyncGPUReadback.Request(_floorRt, 0, req =>
            {
                _floorReading = false;
                if (req.hasError || this == null) return;
                var data = req.GetData<Vector4>();
                var pts = new List<Vector3>(data.Length);
                foreach (var t in data)
                {
                    if (t.w < 0.5f || t.x >= 0.99995f) continue; // no depth there
                    var w = toWorld * new Vector4(t.y * 2f - 1f, t.z * 2f - 1f, t.x * 2f - 1f, 1f);
                    if (Mathf.Abs(w.w) < 1e-6f) continue;
                    pts.Add(new Vector3(w.x / w.w, w.y / w.w, w.z / w.w));
                }
                AcceptFloor(EstimateFloor(pts, headPos, track), track);
                SetRoomPoints(pts); // the same reading tells a panel that opens where the walls and the furniture are (ShellPlacement)
                UpdatePanelOcclusion(pts, headPos, toWorld.inverse); // ... and which windows something real can be in front of
            });
        }

        // ---- per window: can something real be in front of it? (McxOcclusion.hlsl: McxVisibilityIn)

        /// _McxPanelClear[slot] = 1: no real thing can be in front of that window, its shaders skip the per-pixel depth
        /// test; 0 (the default of every slot, and of a window that opens, moves or was not seen yet): the test runs.
        private static readonly int PanelClearId = Shader.PropertyToID("_McxPanelClear");
        private readonly float[] _panelClear = new float[UiPanel.MaxSlots];
        private readonly UiPanel[] _panelClearOwner = new UiPanel[UiPanel.MaxSlots];
        private readonly int[] _panelClearReads = new int[UiPanel.MaxSlots];
        private readonly float[] _panelHitAt = new float[UiPanel.MaxSlots];
        private readonly Vector3[] _panelClearPos = new Vector3[UiPanel.MaxSlots];
        private readonly Quaternion[] _panelClearRot = new Quaternion[UiPanel.MaxSlots];
        private bool _panelClearDirty = true;
        private float _panelReadAt = -100f;
        /// A window counts as clear after this many room readings in a row (0.3 s apart) that see all of it with nothing
        /// in front, and no sooner than this many seconds after the last thing seen in front of it.
        private const int PanelClearReads = 6;
        private const float PanelHitHold = 4f;
        /// What counts as in front: inside the window's rectangle grown by this much (metres) as seen from the eye
        /// (the 48 x 48 points are ~2 degrees apart, the head and people move between readings).
        private const float PanelMargin = 0.15f;
        /// Self-tests: true = every window is clear (no per-pixel test), false = every window is tested; null = the room.
        public bool? PanelClearOverride;
        public bool PanelClearForTest(UiPanel p) => p != null && p.Slot >= 0 && _panelClear[p.Slot] > 0.5f;

        /// Every frame: a window that opened, closed, moved or turned is tested again until readings clear it; without a
        /// room reading for 2 s every window is tested.
        private void UpdatePanelOcclusionFlags()
        {
            bool stale = Time.unscaledTime - _panelReadAt > 2f || !Settings.occludeUi;
            for (int i = 0; i < UiPanel.MaxSlots; i++)
            {
                var p = UiPanel.InSlot(i);
                bool reset = stale || p != _panelClearOwner[i] || p == null || !p.IsOpen;
                if (!reset)
                {
                    var tr = p.transform;
                    reset = (tr.position - _panelClearPos[i]).sqrMagnitude > 0.03f * 0.03f || Quaternion.Angle(tr.rotation, _panelClearRot[i]) > 3f;
                }
                if (reset)
                {
                    _panelClearOwner[i] = p;
                    _panelClearReads[i] = 0;
                    if (p != null) { _panelClearPos[i] = p.transform.position; _panelClearRot[i] = p.transform.rotation; }
                    SetPanelClear(i, false);
                }
                if (PanelClearOverride.HasValue) SetPanelClear(i, PanelClearOverride.Value);
            }
            if (_panelClearDirty) { Shader.SetGlobalFloatArray(PanelClearId, _panelClear); _panelClearDirty = false; }
        }

        private void SetPanelClear(int slot, bool clear)
        {
            float v = clear ? 1f : 0f;
            if (_panelClear[slot] == v) return;
            _panelClear[slot] = v;
            _panelClearDirty = true;
        }

        /// One room reading (left eye's depth, world points, the head when it was taken): each open window with a point in
        /// front of it is tested for PanelHitHold s at least; one seen whole and clear PanelClearReads times is not.
        private void UpdatePanelOcclusion(List<Vector3> pts, Vector3 eye, Matrix4x4 worldToDepth)
        {
            if (pts == null || pts.Count < 40) return;
            _panelReadAt = Time.unscaledTime;
            for (int i = 0; i < UiPanel.MaxSlots; i++)
            {
                var p = _panelClearOwner[i];
                if (p == null || !p.IsOpen || p != UiPanel.InSlot(i)) continue;
                var tr = p.transform;
                var toPanel = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one).inverse;
                Vector3 sc = tr.lossyScale;
                float w = p.Width * Mathf.Abs(sc.x), h = p.Height * Mathf.Abs(sc.y);
                if (MayBeInFront(pts, eye, toPanel, w, h, PanelMargin))
                {
                    _panelHitAt[i] = Time.unscaledTime;
                    _panelClearReads[i] = 0;
                    SetPanelClear(i, false);
                    continue;
                }
                if (!WholeInView(worldToDepth, tr, w, h)) continue; // not seen: no change
                if (++_panelClearReads[i] >= PanelClearReads && Time.unscaledTime - _panelHitAt[i] > PanelHitHold) SetPanelClear(i, true);
            }
        }

        /// The window's rectangle (its four corners) inside the depth's view.
        private static bool WholeInView(Matrix4x4 worldToDepth, Transform tr, float w, float h)
        {
            for (int k = 0; k < 4; k++)
            {
                var corner = tr.position + tr.rotation * new Vector3((k & 1) == 0 ? -w * 0.5f : w * 0.5f, (k & 2) == 0 ? -h * 0.5f : h * 0.5f, 0f);
                var c = worldToDepth * new Vector4(corner.x, corner.y, corner.z, 1f);
                if (c.w <= 1e-5f || Mathf.Abs(c.x / c.w) > 0.98f || Mathf.Abs(c.y / c.w) > 0.98f) return false;
            }
            return true;
        }

        /// True when a room point lies between the eye and a window (more than 2 cm in front of its plane) and, seen from
        /// the eye, inside its rectangle grown by `margin`. `worldToPanel`: rigid, the window's centre at 0, its plane z = 0.
        public static bool MayBeInFront(List<Vector3> pts, Vector3 eye, Matrix4x4 worldToPanel, float width, float height, float margin)
        {
            Vector3 e = worldToPanel.MultiplyPoint3x4(eye);
            if (Mathf.Abs(e.z) < 0.05f) return true; // the eye in the window's plane: anything can be in front
            float hw = width * 0.5f + margin, hh = height * 0.5f + margin;
            foreach (var wp in pts)
            {
                Vector3 p = worldToPanel.MultiplyPoint3x4(wp);
                // on the eye's side of the plane, more than 2 cm off it and nearer than the eye
                if (p.z * e.z <= 0f || Mathf.Abs(p.z) < 0.02f || Mathf.Abs(p.z) >= Mathf.Abs(e.z)) continue;
                float s = e.z / (e.z - p.z); // eye -> point, continued to the plane (> 1)
                float x = e.x + (p.x - e.x) * s, y = e.y + (p.y - e.y) * s;
                if (Mathf.Abs(x) <= hw && Mathf.Abs(y) <= hh) return true;
            }
            return false;
        }

        /// The floor under the user from depth points (world space): those within 1.5 m of the head horizontally,
        /// more than 0.8 m below it and within 0.35 m of the tracking floor; the lowest 4 cm level holding at least 6
        /// points and 15 % of them (a table or a patch of floor read too high is not the floor). NaN: not seen.
        public static float EstimateFloor(List<Vector3> pts, Vector3 head, float trackFloor)
        {
            const float bin = 0.02f, reach = 0.35f;
            int n = Mathf.CeilToInt(reach * 2f / bin) + 1;
            var hist = new int[n];
            int total = 0;
            foreach (var p in pts)
            {
                float dx = p.x - head.x, dz = p.z - head.z;
                if (dx * dx + dz * dz > 1.5f * 1.5f || p.y > head.y - 0.8f) continue;
                float rel = p.y - (trackFloor - reach);
                if (rel < 0f || rel > reach * 2f) continue;
                hist[Mathf.Min(n - 1, (int)(rel / bin))]++;
                total++;
            }
            if (total < 12) return float.NaN;
            int need = Mathf.Max(6, Mathf.CeilToInt(total * 0.15f));
            for (int i = 0; i < n; i++)
            {
                int next = i + 1 < n ? hist[i + 1] : 0, c = hist[i] + next;
                if (c < need) continue;
                float mean = (hist[i] * (i + 0.5f) + next * (i + 1.5f)) / c;
                return trackFloor - reach + mean * bin;
            }
            return float.NaN;
        }

        /// A floor reading counts once three in a row agree within 5 cm (SandyBridge's "feet floor": steady).
        private void AcceptFloor(float y, float track)
        {
            if (float.IsNaN(y)) return;
            if (!float.IsNaN(_feetFloor) && Mathf.Abs(y - _feetFloor) < 0.05f) { _feetFloor = Mathf.Lerp(_feetFloor, y, 0.3f); _feetAgree++; }
            else { _feetFloor = y; _feetAgree = 1; }
            _feetStable = _feetAgree >= 3;
            // in 5 cm steps: each 1 cm wobble was a log line and a Focus panel refresh, several a second (2026-11-13)
            float cm = Mathf.Round((_feetFloor - track) * 20f) * 5f;
            if (_feetStable) SetFocusStatus(ref _floorStatus, $"floor: measured under your feet ({cm:+0;-0;0} cm from the Guardian floor)");
        }

        public float FeetFloorForTest => _feetStable ? _feetFloor : float.NaN;
        public void FeedFloorForTest(List<Vector3> pts, Vector3 head, float track) => AcceptFloor(EstimateFloor(pts, head, track), track);
        public void ResetFloorForTest() { _feetFloor = float.NaN; _feetAgree = 0; _feetStable = false; _floorStatus = "floor: tracking"; }

        /// The floor: the depth under the feet when steady (and within 35 cm of the tracking floor), else the tracking
        /// floor (tracking is floor-level, so the rig's tracking space sits on it).
        /// Self-tests: a known floor height (the Editor scene's rig is not a headset's floor-level origin).
        public float? FloorYOverride;

        private float FloorY()
        {
            if (FloorYOverride.HasValue) return FloorYOverride.Value;
            float track = TrackingFloorY();
            return _feetStable && !float.IsNaN(_feetFloor) && Mathf.Abs(_feetFloor - track) < 0.35f ? _feetFloor : track;
        }

        private Transform TrackingSpace()
        {
            if (_rig == null && Time.unscaledTime >= _nextRigLookup)
            {
                _nextRigLookup = Time.unscaledTime + 5f;
                _rig = FindAnyObjectByType<OVRCameraRig>();
            }
            return _rig != null ? _rig.trackingSpace : null;
        }

        private float TrackingFloorY()
        {
            var space = TrackingSpace();
            if (space != null) return space.position.y;
            return Head != null ? Head.position.y - 1.6f : 0f;
        }

        // ---- the passthrough camera (blur)

        private void EnsureCamera(bool want)
        {
            if (_camTest) return;
            if (!want)
            {
                if (_cam != null && _cam.gameObject.activeSelf) _cam.gameObject.SetActive(false);
                _camLive = false;
                if (_camStatus.Length > 0) SetFocusStatus(ref _camStatus, "");
                return;
            }
#if UNITY_EDITOR
            SetFocusStatus(ref _camStatus, "camera: none in the Editor");
#else
            if (!Meta.XR.PassthroughCameraAccess.IsSupported) { SetFocusStatus(ref _camStatus, "camera: needs a Quest 3 / 3S with Horizon OS 74+"); return; }
#if UNITY_ANDROID
            if (!_askedCamera && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission))
            {
                _askedCamera = true;
                UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);
            }
#endif
            // 15 fps for the blur and the sharp depth's guide (2026-11-13: 30 for the guide; the picture only moves depth edges
            // onto the picture's edges, and its pose is its own, so a still room needs no more). One rate: a different
            // rate meant a new component (MaxFramerate cannot change while it runs), a camera restart.
            const int fps = 15;
            if (_cam != null && _camFps != fps) { Object.Destroy(_cam.gameObject); _cam = null; _camLive = false; }
            if (_cam == null)
            {
                // left camera, 640 x 480: it feeds the blur and guides the sharp depth. Configured while the component is
                // disabled (MaxFramerate refuses changes while it is enabled), then started.
                var go = new GameObject("PassthroughCamera");
                go.SetActive(false);
                go.transform.SetParent(_root, false);
                _cam = go.AddComponent<Meta.XR.PassthroughCameraAccess>();
                _cam.enabled = false;
                _cam.CameraPosition = Meta.XR.PassthroughCameraAccess.CameraPositionType.Left;
                _cam.RequestedResolution = new Vector2Int(640, 480);
                _cam.MaxFramerate = fps;
                _camFps = fps;
                go.SetActive(true);
                _cam.enabled = true;
            }
            if (!_cam.gameObject.activeSelf) _cam.gameObject.SetActive(true);
            if (!_cam.enabled)
            {
                // PassthroughCameraAccess disables itself when Play fails: one retry at its default 1280 x 960
                if (_camDisabledAt < 0f) { _camDisabledAt = Time.unscaledTime; SetFocusStatus(ref _camStatus, "camera: failed to start"); }
                if (!_camRetried && Time.unscaledTime - _camDisabledAt > 3f)
                {
                    _camRetried = true;
                    _cam.RequestedResolution = new Vector2Int(1280, 960);
                    _cam.enabled = true;
                    SetFocusStatus(ref _camStatus, "camera: retrying at 1280 x 960");
                }
                _camLive = false;
                return;
            }
            _camDisabledAt = -1f;
            if (!_cam.IsPlaying) { _camLive = false; SetFocusStatus(ref _camStatus, "camera: starting…"); return; }
            // idle (the voice is busy, or nothing needs it in its hold time): running, its frames not used
            if (!_camUseFrames) return;
            // a new picture (its time stamp moved): copy it after the camera's own Update issued the native update
            if (_cam.Timestamp != _camStamp || !_camLive)
            {
                _camStamp = _cam.Timestamp;
                if (!PushCameraFrame()) { _camLive = false; return; }
            }
            _camLive = true;
            SetFocusStatus(ref _camStatus, $"camera: live {_cam.CurrentResolution.x} x {_cam.CurrentResolution.y}");
#endif
        }

        private bool PushCameraFrame()
        {
            var tex = _cam.GetTexture();
            if (tex == null) { SetFocusStatus(ref _camStatus, "camera: no picture yet"); return false; }
            var ints = _cam.Intrinsics;
            var pose = _cam.GetCameraPose();
            float qn = pose.rotation.x * pose.rotation.x + pose.rotation.y * pose.rotation.y + pose.rotation.z * pose.rotation.z + pose.rotation.w * pose.rotation.w;
            if (qn < 0.5f || float.IsNaN(qn) || ints.FocalLength.x < 1f || ints.SensorResolution.x < 1)
            {
                SetFocusStatus(ref _camStatus, $"camera: no pose or intrinsics yet (|q|² {qn:F2}, f {ints.FocalLength.x:F0})");
                return false;
            }
            // the camera's own texture guides the sharp depth (no copy); the blur paints it into the room panorama
            var w2c = SetCameraUniforms(tex, pose, ints.FocalLength, ints.PrincipalPoint, ints.SensorResolution, _cam.CurrentResolution, true);
            if (Settings.focusBlur > 0.001f) UpdateBlurPano(tex, w2c);
            return true;
        }

        /// The camera for the shader: world -> camera, intrinsics (sensor pixels) and the sensor region the image
        /// covers (as PassthroughCameraAccess.CalcSensorCropRegion). Its pose is in tracking space: into world space
        /// through the rig.
        private Matrix4x4 SetCameraUniforms(Texture tex, Pose pose, Vector2 focal, Vector2 principal, Vector2Int sensor, Vector2Int current, bool trackingSpace)
        {
            var camToWorld = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
            var space = trackingSpace ? TrackingSpace() : null;
            if (space != null) camToWorld = space.localToWorldMatrix * camToWorld;
            var w2c = camToWorld.inverse;
            _camW2C = w2c;
            Shader.SetGlobalTexture(CamTexId, tex);
            Shader.SetGlobalMatrix(CamW2CId, w2c);
            Shader.SetGlobalVector(CamIntrId, new Vector4(focal.x, focal.y, principal.x, principal.y));
            Vector2 s = sensor, k = new Vector2(current.x / Mathf.Max(1f, s.x), current.y / Mathf.Max(1f, s.y));
            k /= Mathf.Max(k.x, k.y);
            Shader.SetGlobalVector(CamCropId, new Vector4(s.x * (1f - k.x) * 0.5f, s.y * (1f - k.y) * 0.5f, s.x * k.x, s.y * k.y));
            _lastCamTex = tex;
            return w2c;
        }

        private Texture _lastCamTex;

        /// One camera frame into the room panorama (FocusBlurPano), then its mip chain (the blur reads a high mip).
        /// Uses the uniforms SetCameraUniforms just set. Cheap: one pass over 1024 x 512 per camera frame (15 or 30 Hz).
        private void UpdateBlurPano(Texture camTex, Matrix4x4 w2c)
        {
            if (camTex == null) return;
            if (_panoMat == null)
            {
                var sh = Resources.Load<Shader>("Shaders/FocusBlurPano") ?? Shader.Find("Hidden/MetaCodex/FocusBlurPano");
                if (sh == null) return;
                _panoMat = new Material(sh) { name = "focus blur panorama" };
            }
            Vector3 origin = w2c.inverse.GetColumn(3);
            bool fresh = _blurPano == null || (origin - _panoOrigin).sqrMagnitude > PanoResetMetres * PanoResetMetres;
            if (_blurPano == null)
            {
                // half floats (0.7.19): the frosted fill reads the room's colours from the top mips, where what the camera
                // has seen is a small average coverage; 8 bits rounded it to nothing there
                _blurPano = new RenderTexture(PanoW, PanoH, 0, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32)
                { name = "focus blur panorama", useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
                _blurPano.Create();
            }
            if (_panoCb == null) _panoCb = new CommandBuffer { name = "focus blur panorama" };
            _panoCb.Clear();
            _panoCb.SetRenderTarget(_blurPano, 0);
            if (fresh) { _panoCb.ClearRenderTarget(false, true, Color.clear); _panoOrigin = origin; }
            _panoMat.SetTexture(PanoSrcId, camTex);
            _panoMat.SetMatrix(PanoW2CId, w2c);
            _panoMat.SetVector(PanoIntrId, Shader.GetGlobalVector(CamIntrId));
            _panoMat.SetVector(PanoCropId, Shader.GetGlobalVector(CamCropId));
            _panoMat.SetFloat(PanoBlendId, PanoBlend);
            _panoCb.DrawProcedural(Matrix4x4.identity, _panoMat, 0, MeshTopology.Triangles, 3);
            _panoCb.GenerateMips(_blurPano);
            Graphics.ExecuteCommandBuffer(_panoCb);
            Shader.SetGlobalTexture(PanoId, _blurPano);
            _panoReady = true;
        }

        /// Self-tests: how much of the room in a world direction the panorama has seen (0..1), read back at mip 0.
        public float BlurCoverageForTest(Vector3 worldDir)
        {
            if (_blurPano == null) return 0f;
            var d = worldDir.normalized;
            float u = Mathf.Atan2(d.x, d.z) / (2f * Mathf.PI) + 0.5f, v = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) / Mathf.PI + 0.5f;
            int x = Mathf.Clamp(Mathf.FloorToInt(u * PanoW), 0, PanoW - 1), y = Mathf.Clamp(Mathf.FloorToInt(v * PanoH), 0, PanoH - 1);
            var prev = RenderTexture.active;
            RenderTexture.active = _blurPano;
            var px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            // rows counted from the bottom, as the texture's uv (tests pick directions that do not depend on it anyway:
            // the horizon is the middle row, corners are checked in mirrored pairs)
            px.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
            px.Apply();
            RenderTexture.active = prev;
            float a = px.GetPixel(0, 0).a;
            Object.DestroyImmediate(px);
            return a;
        }

        /// Self-tests: a camera image as PassthroughCameraAccess would give it (a world-space pose); null clears it.
        public void SetCameraForTest(Texture image, Pose worldPose, Vector2 focal, Vector2 principal, Vector2Int sensor, Vector2Int current)
        {
            _camTest = image != null;
            _camLive = _camTest;
            // a new panorama for each test camera, filled by as many frames as a second of the real one
            if (_blurPano != null) { _blurPano.Release(); Object.DestroyImmediate(_blurPano); _blurPano = null; }
            _panoReady = false;
            if (!_camTest) return;
            var w2c = SetCameraUniforms(image, worldPose, focal, principal, sensor, current, false);
            for (int i = 0; i < 12; i++) UpdateBlurPano(image, w2c);
        }

        /// Self-tests: one more test-camera frame into the panorama (the head turned; the camera sees elsewhere).
        public void AddCameraFrameForTest(Texture image, Pose worldPose, Vector2 focal, Vector2 principal, Vector2Int sensor, Vector2Int current, int frames = 12)
        {
            var w2c = SetCameraUniforms(image, worldPose, focal, principal, sensor, current, false);
            for (int i = 0; i < frames; i++) UpdateBlurPano(image, w2c);
        }

        // ---- hands: capsules on the tracked joints, always clear

        private Material HandMaterial()
        {
            var sh = Resources.Load<Shader>("Shaders/FocusMesh") ?? Shader.Find("MetaCodex/FocusMesh");
            if (sh == null) return null;
            var m = new Material(sh) { name = "focus hand cut-out" };
            m.SetFloat(HandId, 1f);
            m.SetFloat(IntensityId, 1f); // fully clear in the hand's core, a soft fade to its outline (FocusMesh)
            return m;
        }

        /// Self-tests: false hides the hand cut-outs (pictures that must not show them).
        public bool HandCutoutsForTest { get; set; } = true;

        /// Self-tests: how many hand cut-outs are drawn now.
        public int HandCutoutsShownForTest
        {
            get { int n = 0; foreach (var t in _handParts) if (t != null && t.gameObject.activeSelf) n++; return n; }
        }

        private void HideHandCutouts()
        {
            for (int h = 0; h < 2; h++)
                for (int k = 0; k < _handParts.GetLength(1); k++)
                {
                    var t = _handParts[h, k];
                    if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
                }
        }

        private void UpdateHandCutouts()
        {
            if (Gestures == null) return;
            if (_handMat == null) { _handMat = HandMaterial(); if (_handMat == null) return; }
            for (int h = 0; h < 2; h++)
            {
                var st = Gestures.Get((MetaCodex.Hands.Handedness)h);
                // the raw tracking: during the system gesture (the Horizon OS menu) the gesture engine marks the hand
                // untracked, but it must stay visible then most of all
                bool on = st != null && st.Frame.Tracked;
                var f = on ? st.Frame : default;
                // palm, then each finger from its knuckle to its tip (the thumb from its knuckle), and the wrist
                // fingers about as thick as real ones, their round cap ending 2.5 mm past the tracked tip: 13 mm capsules
                // reaching a whole radius past the tip cut a halo of room round the finger over the wrist menu and windows
                // (owner 2026-11-09)
                Seg(h, 0, on, f.Wrist, f.Palm, 0.040f);
                Seg(h, 1, on, f.ThumbKnuckle, FingerEnd(f.ThumbKnuckle, f.ThumbTip, 0.0115f), 0.0115f);
                Seg(h, 2, on, f.IndexKnuckle, FingerEnd(f.IndexKnuckle, f.IndexTip, 0.0100f), 0.0100f);
                Seg(h, 3, on, f.MiddleKnuckle, FingerEnd(f.MiddleKnuckle, f.MiddleTip, 0.0100f), 0.0100f);
                Seg(h, 4, on, f.RingKnuckle, FingerEnd(f.RingKnuckle, f.RingTip, 0.0100f), 0.0100f);
                Seg(h, 5, on, f.LittleKnuckle, FingerEnd(f.LittleKnuckle, f.LittleTip, 0.0090f), 0.0090f);
                Seg(h, 6, on, f.Palm, (f.IndexKnuckle + f.LittleKnuckle) * 0.5f, 0.042f);
            }
        }

        /// A finger capsule's end: back from the tracked tip so its round cap ends 2.5 mm past it.
        private static Vector3 FingerEnd(Vector3 knuckle, Vector3 tip, float radius)
        {
            var d = tip - knuckle;
            float len = d.magnitude;
            if (len < 1e-4f) return tip;
            return tip - d / len * Mathf.Clamp(radius - 0.0025f, 0f, len * 0.5f);
        }

        private void Seg(int h, int k, bool on, Vector3 a, Vector3 b, float radius)
        {
            var t = _handParts[h, k];
            if (t == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"focus hand {h}.{k}";
                var col = go.GetComponent<Collider>();
                if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
                go.transform.SetParent(_root, false);
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _handMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                t = go.transform;
                _handParts[h, k] = t;
            }
            float len = Vector3.Distance(a, b);
            on = on && len > 1e-4f;
            if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
            if (!on) return;
            // a capsule is 2 units tall and 1 wide: stretch it from a to b, `radius` thick
            t.position = (a + b) * 0.5f;
            t.rotation = Quaternion.FromToRotation(Vector3.up, (b - a) / len);
            t.localScale = new Vector3(radius * 2f, len * 0.5f + radius, radius * 2f);
        }

        /// Self-tests: the focus material (the void's effects on / off).
        public Material FocusMaterialForTest => _focusMat;

        /// Self-tests: run the fade for a while.
        public void TickFocusForTest(float seconds) { for (float t = 0; t < seconds; t += 0.05f) UpdateFocus(0.05f); }

        private bool EnsureFocusSphere()
        {
            if (_focusSphere != null) return true;
            var sh = Resources.Load<Shader>("Shaders/AmbientFocus") ?? Shader.Find("MetaCodex/AmbientFocus");
            if (sh == null) { Log("ambient focus: shader missing"); Settings.focusEnabled = false; _focusShown = 0f; return false; }
            _focusMat = new Material(sh) { name = "ambient focus" };
            // the void outside the dome (AmbientFocus): the UI's accent colour; its sky is drawn on a worker thread
            _focusMat.SetColor("_Accent", UiTheme.Current.Accent);
            VoidSky(!Application.isPlaying);
            _focusSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _focusSphere.name = "AmbientFocus";
            var col = _focusSphere.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            _focusSphere.transform.SetParent(_root, false);
            _focusSphere.transform.localScale = Vector3.one * 0.7f; // 35 cm around the eyes, past the near clip
            var r = _focusSphere.GetComponent<MeshRenderer>();
            r.sharedMaterial = _focusMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return true;
        }

        // ---- the void's sky

        private static Texture2D _voidSky;
        private static System.Threading.Tasks.Task<byte[]> _voidSkyJob;
        private static bool _voidSkyFailed;
        private const int VoidSkySize = 1024;
        private static readonly int VoidSkyId = Shader.PropertyToID("_McxVoidSky"), VoidRotId = Shader.PropertyToID("_VoidRot"), VoidTimeId = Shader.PropertyToID("_VoidTime");

        /// The void's sky (AmbientFocus): deep space drawn ONCE into an octahedral map (1024 x 1024, sRGB): the nebula
        /// (two layers of a tiling noise) and the stars (alpha = a star's twinkle phase, 0 = none). Computed per pixel it
        /// cost the GPU far too much at render scale 1.6 (0.7.7 lagged with focus on, owner 2026-11-09); now the shader
        /// reads one texel. Built on a worker thread (in the Editor at once, for the self-tests); null until ready, and the
        /// void shows its plain deep blue meanwhile.
        private Texture2D VoidSky(bool now = false)
        {
            if (_voidSky != null || _voidSkyFailed) return _voidSky;
            byte[] px = null;
            if (now) px = BuildVoidSky(VoidSkySize);
            else
            {
                if (_voidSkyJob == null) { _voidSkyJob = System.Threading.Tasks.Task.Run(() => BuildVoidSky(VoidSkySize)); return null; }
                if (!_voidSkyJob.IsCompleted) return null;
                if (_voidSkyJob.IsFaulted) { _voidSkyFailed = true; Log("ambient focus: the void's sky failed: " + _voidSkyJob.Exception?.GetBaseException().Message); return null; }
                px = _voidSkyJob.Result;
                _voidSkyJob = null;
            }
            _voidSky = new Texture2D(VoidSkySize, VoidSkySize, TextureFormat.RGBA32, false, false)
            {
                name = "mcx void sky", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
            };
            _voidSky.SetPixelData(px, 0);
            _voidSky.Apply(false, true);
            if (_focusMat != null) _focusMat.SetTexture(VoidSkyId, _voidSky);
            return _voidSky;
        }

        /// The void's moving parts, each frame it shows: the sky turning slowly round the vertical, the stars' clock.
        private void UpdateVoid()
        {
            var sky = VoidSky();
            if (sky != null && _focusMat.GetTexture(VoidSkyId) != sky) _focusMat.SetTexture(VoidSkyId, sky);
            float t = Time.unscaledTime % 3600f, ang = t * 0.0016f;
            _focusMat.SetVector(VoidRotId, new Vector4(Mathf.Cos(ang), Mathf.Sin(ang), 0f, 0f));
            _focusMat.SetFloat(VoidTimeId, t);
        }

        /// The pixels of the sky (RGBA32, rows from the bottom): plain System.Math only, it runs on a worker thread.
        private static byte[] BuildVoidSky(int n)
        {
            var noise = VoidNoiseValues(128);
            var px = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var d = VoidOctDecode((x + 0.5f) / n, (y + 0.5f) / n);
                    double lat = System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, d.y)));
                    float su = (float)(System.Math.Atan2(d.x, d.z) / (2.0 * System.Math.PI) + 0.5), sv = (float)(lat / System.Math.PI + 0.5);
                    // the nebula: two layers, the second tints it between violet and the accent's blue
                    float n1 = VoidNoiseAt(noise, 128, su * 4f, sv * 2f), n2 = VoidNoiseAt(noise, 128, su * 9f + 0.37f, sv * 4.5f + 0.61f);
                    float neb = VoidClamp01(n1 * 0.68f + n2 * 0.32f);
                    neb = neb * neb * (3f - 2f * neb);
                    float k = (float)System.Math.Pow(neb, 2.6) * 0.9f;
                    float r = VoidLerp(0.016f, 0.21f * 0.022f, n2) * k, g = VoidLerp(0.004f, 0.82f * 0.022f, n2) * k, b = VoidLerp(0.032f, 1.0f * 0.022f, n2) * k;
                    // the stars: one in some cells of a grid on the sky (fewer towards the poles), a soft point
                    float cx = su * 240f, cy = sv * 120f;
                    float ix = (float)System.Math.Floor(cx), iy = (float)System.Math.Floor(cy);
                    float h = VoidHash21(ix, iy);
                    float a = 0f;
                    if (h >= 0.915f + 0.08f * (1f - (float)System.Math.Cos(lat)))
                    {
                        float ox = (VoidHash21(ix + 17.3f, iy + 17.3f) - 0.5f) * 0.7f, oy = (VoidHash21(ix + 41.9f, iy + 41.9f) - 0.5f) * 0.7f;
                        float fx = cx - ix - 0.5f - ox, fy = cy - iy - 0.5f - oy;
                        float star = (float)System.Math.Exp(-(fx * fx + fy * fy) * 160.0) * (0.12f + 0.28f * VoidFrac(h * 13.7f));
                        r += 0.55f * star; g += 0.75f * star; b += 1.0f * star;
                        if (star > 0.004f) a = 0.05f + 0.95f * VoidFrac(h * 7.3f);
                    }
                    int i = (y * n + x) * 4;
                    px[i] = VoidSrgb(r); px[i + 1] = VoidSrgb(g); px[i + 2] = VoidSrgb(b); px[i + 3] = (byte)System.Math.Round(VoidClamp01(a) * 255f);
                }
            return px;
        }

        /// The octahedral map's inverse (y up): uv 0..1 -> a direction. AmbientFocus's OctUv is the forward one.
        private static Vector3 VoidOctDecode(float u, float v)
        {
            float fx = u * 2f - 1f, fz = v * 2f - 1f, y = 1f - System.Math.Abs(fx) - System.Math.Abs(fz);
            if (y < 0f) { float ox = fx, oz = fz; fx = (1f - System.Math.Abs(oz)) * (ox >= 0f ? 1f : -1f); fz = (1f - System.Math.Abs(ox)) * (oz >= 0f ? 1f : -1f); }
            float len = (float)System.Math.Sqrt(fx * fx + y * y + fz * fz);
            return new Vector3(fx / len, y / len, fz / len);
        }

        /// A tiling cloud noise (four octaves of value noise, n x n, seamless), values 0..1.
        private static float[] VoidNoiseValues(int n)
        {
            var rnd = new System.Random(7);
            var acc = new float[n * n];
            float amp = 1f, total = 0f;
            for (int oct = 0; oct < 4; oct++)
            {
                int cells = 4 << oct; // whole cells across: it tiles
                var g = new float[cells * cells];
                for (int k = 0; k < g.Length; k++) g[k] = (float)rnd.NextDouble();
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float fx = x * cells / (float)n, fy = y * cells / (float)n;
                        int x0 = (int)fx, y0 = (int)fy, x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
                        float tx = fx - x0, ty = fy - y0;
                        tx = tx * tx * (3f - 2f * tx);
                        ty = ty * ty * (3f - 2f * ty);
                        acc[y * n + x] += VoidLerp(VoidLerp(g[y0 * cells + x0], g[y0 * cells + x1], tx), VoidLerp(g[y1 * cells + x0], g[y1 * cells + x1], tx), ty) * amp;
                    }
                total += amp;
                amp *= 0.5f;
            }
            for (int k = 0; k < acc.Length; k++) acc[k] /= total;
            return acc;
        }

        /// The tiling noise at (u, v) in tiles (bilinear, wrapping).
        private static float VoidNoiseAt(float[] t, int n, float u, float v)
        {
            float fx = (u - (float)System.Math.Floor(u)) * n, fy = (v - (float)System.Math.Floor(v)) * n;
            int x0 = (int)fx % n, y0 = (int)fy % n, x1 = (x0 + 1) % n, y1 = (y0 + 1) % n;
            float tx = fx - (int)fx, ty = fy - (int)fy;
            return VoidLerp(VoidLerp(t[y0 * n + x0], t[y0 * n + x1], tx), VoidLerp(t[y1 * n + x0], t[y1 * n + x1], tx), ty);
        }

        private static float VoidHash21(float x, float y)
        {
            float px = VoidFrac(x * 123.34f), py = VoidFrac(y * 456.21f);
            float dd = px * (px + 45.32f) + py * (py + 45.32f);
            px += dd; py += dd;
            return VoidFrac(px * py);
        }

        private static float VoidFrac(float v) => v - (float)System.Math.Floor(v);
        private static float VoidClamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static float VoidLerp(float a, float b, float t) => a + (b - a) * t;
        private static byte VoidSrgb(float lin)
        {
            double c = lin <= 0.0031308 ? 12.92 * lin : 1.055 * System.Math.Pow(lin, 1.0 / 2.4) - 0.055;
            return (byte)System.Math.Round(System.Math.Max(0.0, System.Math.Min(1.0, c)) * 255.0);
        }

        private void RefreshFocusPanel()
        {
            if (Settings == null) return;
            string outside = Settings.focusBlur > 0.001f ? $"blurred ({Settings.focusBlur * 100f:0}%) and {Settings.focusIntensity * 100f:0}% dark" : $"{Settings.focusIntensity * 100f:0}% dark";
            _focusInfo?.Set((Settings.focusEnabled
                ? $"On: a dome {Settings.focusRadius:0.0} m around you; beyond it the room fades over {Settings.focusSoftness:0.0} m to {outside}."
                : "Off. Turn it on here, with FOCUS on the dock, or say “turn on ambient focus”.") + "\n" + FocusStatus, UiTheme.Current.TextDim);
        }
    }
}
