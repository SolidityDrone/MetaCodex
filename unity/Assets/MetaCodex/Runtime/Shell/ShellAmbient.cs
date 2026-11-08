using System;
using System.Collections.Generic;
using MetaCodex.UI;
using Unity.Collections;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Ambient Focus: a dome of radius RADIUS standing on the floor around you. Real things inside it stay, the
    /// rest of the room fades to INTENSITY (black at 100 %), and your windows always stay on top.
    ///   1. a sphere on the head (MetaCodex/AmbientFocus), drawn before everything, darkens every direction by the
    ///      floor plane alone: the floor inside the dome stays, everything above the horizon fades;
    ///   2. the room's real geometry from Space Setup (scene anchors: the scanned global mesh, else walls, floor,
    ///      ceiling, furniture; permission "spatial data") is drawn next (MetaCodex/FocusMesh, BlendOp Min): each
    ///      real surface inside the dome clears its pixels again, so a wall or a desk 1 m away stays visible;
    ///   3. both hands are cut out (capsules on the tracked joints), so you always see them.
    /// Horizon OS composites the passthrough itself: it can be darkened but not blurred by an app.
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
        private UiSlider _focusRadius, _focusIntensity, _focusSoft;

        public float FocusShownForTest => _focusShown;
        public GameObject FocusSphereForTest => _focusSphere;
        public string RoomStatus => _roomStatus;

        // the room's real geometry and the hand cut-outs (shader MetaCodex/FocusMesh)
        private Material _roomMat, _handMat;
        private readonly List<GameObject> _roomParts = new List<GameObject>();
        private readonly Transform[,] _handParts = new Transform[2, 7];
        private bool _roomLoading, _roomLoaded, _askedScene;
        private string _roomStatus = "room: not read yet";
        private float _nextRoomTry;
        private static readonly int RadiusId = Shader.PropertyToID("_Radius"), SoftId = Shader.PropertyToID("_Softness"),
            IntensityId = Shader.PropertyToID("_Intensity"), FeetId = Shader.PropertyToID("_FeetPos"), HandId = Shader.PropertyToID("_Hand");

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

        /// Every frame: fades the sphere in / out and keeps it on the head with the current settings.
        private void UpdateFocus(float dt)
        {
            if (Settings == null) return;
            _focusShown = Mathf.MoveTowards(_focusShown, Settings.focusEnabled ? 1f : 0f, dt / 0.4f);
            if (_focusShown <= 0f)
            {
                if (_focusSphere != null && _focusSphere.activeSelf) _focusSphere.SetActive(false);
                SetFocusPartsActive(false);
                return;
            }
            if (!EnsureFocusSphere()) return;
            if (!_focusSphere.activeSelf) _focusSphere.SetActive(true);
            var head = Head;
            if (head != null) _focusSphere.transform.position = head.position;
            float radius = Mathf.Max(0f, Settings.focusRadius), soft = Mathf.Max(0.02f, Settings.focusSoftness), intensity = Mathf.Clamp01(Settings.focusIntensity) * _focusShown;
            float floor = FloorY();
            _focusMat.SetFloat(RadiusId, radius);
            _focusMat.SetFloat(SoftId, soft);
            _focusMat.SetFloat(IntensityId, intensity);
            _focusMat.SetFloat("_FloorY", floor);
            var feet = head != null ? new Vector4(head.position.x, floor, head.position.z, 0f) : new Vector4(0f, floor, 0f, 0f);
            foreach (var m in new[] { _roomMat, _handMat })
            {
                if (m == null) continue;
                m.SetFloat(RadiusId, radius);
                m.SetFloat(SoftId, soft);
                m.SetFloat(IntensityId, intensity);
                m.SetVector(FeetId, feet);
            }
            EnsureRoom();
            SetFocusPartsActive(true);
            UpdateHandCutouts();
        }

        private void SetFocusPartsActive(bool on)
        {
            foreach (var g in _roomParts) if (g != null && g.activeSelf != on) g.SetActive(on);
            for (int h = 0; h < 2; h++)
                for (int k = 0; k < _handParts.GetLength(1); k++)
                {
                    var t = _handParts[h, k];
                    if (t != null && !on && t.gameObject.activeSelf) t.gameObject.SetActive(false);
                }
        }

        private Material FocusMeshMaterial(bool hand)
        {
            var sh = Resources.Load<Shader>("Shaders/FocusMesh") ?? Shader.Find("MetaCodex/FocusMesh");
            if (sh == null) return null;
            var m = new Material(sh) { name = hand ? "focus hand cut-out" : "focus room" };
            m.SetFloat(HandId, hand ? 1f : 0f);
            return m;
        }

        // ---- the room's real geometry (scene anchors)

        private void EnsureRoom()
        {
            if (_roomLoaded || _roomLoading || Time.unscaledTime < _nextRoomTry) return;
            _nextRoomTry = Time.unscaledTime + 3f;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                if (!_askedScene) { _askedScene = true; UnityEngine.Android.Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission); }
                _roomStatus = "room: waiting for the \"spatial data\" permission (walls count as far away until then)";
                RefreshFocusPanel();
                return;
            }
            _roomLoading = true;
            LoadRoomAsync();
#else
            _roomLoaded = true;
            _roomStatus = "room: no scene data here (Editor): the floor plane only";
#endif
        }

        private async void LoadRoomAsync()
        {
            int tris = 0, parts = 0;
            try
            {
                if (_roomMat == null) _roomMat = FocusMeshMaterial(false);
                var space = _rig != null ? _rig.trackingSpace : FindAnyObjectByType<OVRCameraRig>()?.trackingSpace;
                // the scanned room mesh first; without one, the planes and boxes of the room's layout
                var meshes = new List<OVRAnchor>();
                await OVRAnchor.FetchAnchorsAsync(meshes, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRTriangleMesh) });
                foreach (var a in meshes)
                {
                    if (!a.TryGetComponent(out OVRTriangleMesh tm) || !tm.TryGetCounts(out int vc, out int tc) || vc == 0 || tc == 0) continue;
                    var pos = new NativeArray<Vector3>(vc, Allocator.Temp);
                    var idx = new NativeArray<int>(tc * 3, Allocator.Temp);
                    try
                    {
                        if (!tm.TryGetMesh(pos, idx)) continue;
                        var go = await RoomPart(a, space, "room mesh");
                        if (go == null) continue;
                        var mesh = new Mesh { name = "room", indexFormat = vc > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                        mesh.SetVertices(pos);
                        mesh.SetIndices(idx, MeshTopology.Triangles, 0);
                        mesh.RecalculateNormals();
                        mesh.RecalculateBounds();
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        tris += tc;
                        parts++;
                    }
                    finally { pos.Dispose(); idx.Dispose(); }
                }
                if (parts == 0)
                {
                    var planes = new List<OVRAnchor>();
                    await OVRAnchor.FetchAnchorsAsync(planes, new OVRAnchor.FetchOptions { SingleComponentType = typeof(OVRBounded2D) });
                    foreach (var a in planes)
                    {
                        if (!a.TryGetComponent(out OVRBounded2D b2) || !b2.IsEnabled) continue;
                        var go = await RoomPart(a, space, "room plane");
                        if (go == null) continue;
                        var r = b2.BoundingBox;
                        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        Destroy(q.GetComponent<Collider>());
                        go.AddComponent<MeshFilter>().sharedMesh = q.GetComponent<MeshFilter>().sharedMesh;
                        Destroy(q);
                        go.transform.localScale = new Vector3(r.width, r.height, 1f);
                        go.transform.localPosition += go.transform.localRotation * new Vector3(r.center.x, r.center.y, 0f);
                        parts++;
                    }
                }
                _roomStatus = tris > 0 ? $"room: the scanned mesh ({tris:N0} triangles): real walls and furniture count"
                    : parts > 0 ? $"room: {parts} walls / floor / ceiling planes (no scanned mesh: scan the room in Space Setup for furniture)"
                    : "room: no Space Setup found: only the floor counts (set up your space in Horizon OS settings)";
            }
            catch (Exception ex) { _roomStatus = "room: " + ex.Message; Log("ambient focus room: " + ex); }
            _roomLoading = false;
            _roomLoaded = true;
            SetFocusPartsActive(_focusShown > 0f);
            RefreshFocusPanel();
        }

        /// A child of the tracking space at the anchor's pose (the room stays put when the rig recenters), with the
        /// dome material; its mesh is added by the caller.
        private async OVRTask<GameObject> RoomPart(OVRAnchor a, Transform space, string name)
        {
            if (!a.TryGetComponent(out OVRLocatable loc)) return null;
            if (!loc.IsEnabled) await loc.SetEnabledAsync(true);
            if (!loc.TryGetSceneAnchorPose(out var pose) || !pose.Position.HasValue || !pose.Rotation.HasValue) return null;
            var go = new GameObject(name);
            go.transform.SetParent(space != null ? space : _root, false);
            go.transform.localPosition = pose.Position.Value;
            go.transform.localRotation = pose.Rotation.Value;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _roomMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _roomParts.Add(go);
            return go;
        }

        /// Self-tests: a piece of "real" room (a wall at some distance) as Space Setup would give it.
        public GameObject AddRoomMeshForTest(Mesh mesh, Vector3 position, Quaternion rotation)
        {
            if (_roomMat == null) _roomMat = FocusMeshMaterial(false);
            var go = new GameObject("room mesh (test)");
            go.transform.SetParent(_root, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _roomMat;
            _roomParts.Add(go);
            return go;
        }

        public void ClearRoomForTest()
        {
            foreach (var g in _roomParts) if (g != null) DestroyImmediate(g);
            _roomParts.Clear();
        }

        // ---- hands: capsules on the tracked joints, always clear

        private void UpdateHandCutouts()
        {
            if (Gestures == null) return;
            if (_handMat == null) { _handMat = FocusMeshMaterial(true); if (_handMat == null) return; }
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
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
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
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return true;
        }

        /// The floor: tracking is floor-level, so the rig's tracking space sits on it (else about 1.6 m below the eyes).
        /// Self-tests: a known floor height (the Editor scene's rig is not a headset's floor-level origin).
        public float? FloorYOverride;

        private float FloorY()
        {
            if (FloorYOverride.HasValue) return FloorYOverride.Value;
            if (_rig == null && Time.unscaledTime >= _nextRigLookup)
            {
                _nextRigLookup = Time.unscaledTime + 5f;
                _rig = FindAnyObjectByType<OVRCameraRig>();
            }
            if (_rig != null && _rig.trackingSpace != null) return _rig.trackingSpace.position.y;
            return Head != null ? Head.position.y - 1.6f : 0f;
        }

        private void RefreshFocusPanel()
        {
            _focusInfo?.Set((Settings.focusEnabled
                ? $"On: a dome {Settings.focusRadius:0.0} m around you, fading over {Settings.focusSoftness:0.0} m to {Settings.focusIntensity * 100f:0}% dark."
                : "Off. Turn it on here, with FOCUS on the dock, or say “turn on ambient focus”.") + "\n" + _roomStatus, UiTheme.Current.TextDim);
        }
    }
}
