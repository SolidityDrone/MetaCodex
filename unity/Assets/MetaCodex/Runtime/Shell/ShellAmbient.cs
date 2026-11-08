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

        // ---- the passthrough camera, for the blur
        private Meta.XR.PassthroughCameraAccess _cam;
        private bool _askedCamera, _camLive, _camTest;
        private RenderTexture _camRt;
        private static readonly int CamTexId = Shader.PropertyToID("_McxCamTex"), CamW2CId = Shader.PropertyToID("_McxCamWorldToCam"),
            CamIntrId = Shader.PropertyToID("_McxCamIntr"), CamCropId = Shader.PropertyToID("_McxCamCrop");

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

        /// Every frame: fades the sphere in / out, keeps it on the head with the current settings, and runs the depth
        /// and the camera only while they are used.
        private void UpdateFocus(float dt)
        {
            if (Settings == null) return;
            _focusShown = Mathf.MoveTowards(_focusShown, Settings.focusEnabled ? 1f : 0f, dt / 0.4f);
            bool shown = _focusShown > 0f;
            EnsureDepth(shown);
            EnsureCamera(shown && Settings.focusBlur > 0.001f);
            if (!shown)
            {
                if (_focusSphere != null && _focusSphere.activeSelf) _focusSphere.SetActive(false);
                HideHandCutouts();
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
            SetKeyword(_focusMat, "MCX_DEPTH", _depthLive);
            bool blur = _camLive && Settings.focusBlur > 0.001f;
            SetKeyword(_focusMat, "MCX_BLUR", blur);
            // BLUR 1 % .. 100 %: mip 1.5 (soft) .. 5 (only colours) of the 256 x 192 copy, 4 taps
            if (blur) _focusMat.SetFloat(BlurMipId, Mathf.Lerp(1.5f, 5f, Mathf.Clamp01(Settings.focusBlur)));
            if (_handMat != null) _handMat.SetFloat(IntensityId, intensity);
            UpdateFloorFromDepth(head);
            UpdateHandCutouts();
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
            RefreshFocusPanel();
        }

        // ---- live depth

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
            if (_depth == null || !_depth.enabled || !_depth.IsDepthAvailable || _focusShown <= 0f) return;
            _reproj.Clear();
            Shader.GetGlobalMatrixArray(ReprojId, _reproj);
            if (_reproj.Count < 2) return;
            for (int i = 0; i < 2; i++) _depthToWorld[i] = _reproj[i].inverse;
            Shader.SetGlobalMatrixArray(DepthToWorldId, _depthToWorld);
            _depthLive = true;
        }

        /// Self-tests: Meta's depth inputs as EnvironmentDepthManager sets them (a depth Texture2DArray holding
        /// (ndc + 1) / 2 per eye, and world -> depth clip matrices); null goes back to the live depth.
        public void SetDepthForTest(Texture depthArray, Matrix4x4[] worldToDepth)
        {
            _depthTest = depthArray != null;
            _depthLive = _depthTest;
            if (!_depthTest) return;
            var m = new[] { worldToDepth[0], worldToDepth[Mathf.Min(1, worldToDepth.Length - 1)] };
            Shader.SetGlobalTexture(DepthTexId, depthArray);
            Shader.SetGlobalMatrixArray(ReprojId, m);
            for (int i = 0; i < 2; i++) _depthToWorld[i] = m[i].inverse;
            Shader.SetGlobalMatrixArray(DepthToWorldId, _depthToWorld);
            SetFocusStatus(ref _depthStatus, "depth: test input");
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
            });
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
            if (_feetStable) SetFocusStatus(ref _floorStatus, $"floor: measured under your feet ({(_feetFloor - track) * 100f:+0;-0;0} cm from the Guardian floor)");
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
            SetFocusStatus(ref _camStatus, "blur: no passthrough camera in the Editor");
#else
            if (!Meta.XR.PassthroughCameraAccess.IsSupported) { SetFocusStatus(ref _camStatus, "blur: needs a Quest 3 / 3S with Horizon OS 74+"); return; }
#if UNITY_ANDROID
            if (!_askedCamera && !UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission))
            {
                _askedCamera = true;
                UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);
            }
#endif
            if (_cam == null)
            {
                // left camera, small and slow: it only feeds a blur (PassthroughCameraAccess waits for the permission)
                var go = new GameObject("PassthroughCamera");
                go.SetActive(false);
                go.transform.SetParent(_root, false);
                _cam = go.AddComponent<Meta.XR.PassthroughCameraAccess>();
                _cam.CameraPosition = Meta.XR.PassthroughCameraAccess.CameraPositionType.Left;
                _cam.RequestedResolution = new Vector2Int(640, 480);
                _cam.MaxFramerate = 30;
            }
            if (!_cam.gameObject.activeSelf) _cam.gameObject.SetActive(true);
            if (!_cam.IsPlaying) { _camLive = false; SetFocusStatus(ref _camStatus, _askedCamera ? "blur: waiting for the camera (permission)" : "blur: starting the camera…"); return; }
            if (_cam.IsUpdatedThisFrame || !_camLive) PushCameraFrame();
            _camLive = true;
            SetFocusStatus(ref _camStatus, "blur: camera live");
#endif
        }

        private void PushCameraFrame()
        {
            var tex = _cam.GetTexture();
            if (tex == null) return;
            if (_camRt == null)
            {
                _camRt = new RenderTexture(256, 192, 0, RenderTextureFormat.ARGB32)
                { name = "focus blur", useMipMap = true, autoGenerateMips = true, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
                _camRt.Create();
            }
            Graphics.Blit(tex, _camRt);
            var ints = _cam.Intrinsics;
            SetCameraUniforms(_camRt, _cam.GetCameraPose(), ints.FocalLength, ints.PrincipalPoint, ints.SensorResolution, _cam.CurrentResolution, true);
        }

        /// The camera for the shader: world -> camera, intrinsics (sensor pixels) and the sensor region the image
        /// covers (as PassthroughCameraAccess.CalcSensorCropRegion). Its pose is in tracking space: into world space
        /// through the rig.
        private void SetCameraUniforms(Texture tex, Pose pose, Vector2 focal, Vector2 principal, Vector2Int sensor, Vector2Int current, bool trackingSpace)
        {
            var camToWorld = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
            var space = trackingSpace ? TrackingSpace() : null;
            if (space != null) camToWorld = space.localToWorldMatrix * camToWorld;
            Shader.SetGlobalTexture(CamTexId, tex);
            Shader.SetGlobalMatrix(CamW2CId, camToWorld.inverse);
            Shader.SetGlobalVector(CamIntrId, new Vector4(focal.x, focal.y, principal.x, principal.y));
            Vector2 s = sensor, k = new Vector2(current.x / Mathf.Max(1f, s.x), current.y / Mathf.Max(1f, s.y));
            k /= Mathf.Max(k.x, k.y);
            Shader.SetGlobalVector(CamCropId, new Vector4(s.x * (1f - k.x) * 0.5f, s.y * (1f - k.y) * 0.5f, s.x * k.x, s.y * k.y));
        }

        /// Self-tests: a camera image as PassthroughCameraAccess would give it (a world-space pose); null clears it.
        public void SetCameraForTest(Texture image, Pose worldPose, Vector2 focal, Vector2 principal, Vector2Int sensor, Vector2Int current)
        {
            _camTest = image != null;
            _camLive = _camTest;
            if (_camTest) SetCameraUniforms(image, worldPose, focal, principal, sensor, current, false);
        }

        // ---- hands: capsules on the tracked joints, always clear

        private Material HandMaterial()
        {
            var sh = Resources.Load<Shader>("Shaders/FocusMesh") ?? Shader.Find("MetaCodex/FocusMesh");
            if (sh == null) return null;
            var m = new Material(sh) { name = "focus hand cut-out" };
            m.SetFloat(HandId, 1f);
            return m;
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
                bool on = st != null && st.Tracked;
                var f = on ? st.Frame : default;
                // palm, then each finger from its knuckle to its tip (the thumb from its knuckle), and the wrist
                Seg(h, 0, on, f.Wrist, f.Palm, 0.040f);
                Seg(h, 1, on, f.ThumbKnuckle, f.ThumbTip, 0.014f);
                Seg(h, 2, on, f.IndexKnuckle, f.IndexTip, 0.013f);
                Seg(h, 3, on, f.MiddleKnuckle, f.MiddleTip, 0.013f);
                Seg(h, 4, on, f.RingKnuckle, f.RingTip, 0.013f);
                Seg(h, 5, on, f.LittleKnuckle, f.LittleTip, 0.012f);
                Seg(h, 6, on, f.Palm, (f.IndexKnuckle + f.LittleKnuckle) * 0.5f, 0.042f);
            }
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

        /// Self-tests: run the fade for a while.
        public void TickFocusForTest(float seconds) { for (float t = 0; t < seconds; t += 0.05f) UpdateFocus(0.05f); }

        private bool EnsureFocusSphere()
        {
            if (_focusSphere != null) return true;
            var sh = Resources.Load<Shader>("Shaders/AmbientFocus") ?? Shader.Find("MetaCodex/AmbientFocus");
            if (sh == null) { Log("ambient focus: shader missing"); Settings.focusEnabled = false; _focusShown = 0f; return false; }
            _focusMat = new Material(sh) { name = "ambient focus" };
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
