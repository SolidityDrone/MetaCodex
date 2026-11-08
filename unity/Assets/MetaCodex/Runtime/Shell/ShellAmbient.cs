using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Ambient Focus: the room fades out beyond a radius around you, so only your windows (and what is near you)
    /// stay. A sphere around the head is drawn before everything else and darkens the passthrough behind it by
    /// how far each pixel's view ray meets the floor (shader MetaCodex/AmbientFocus): the floor around you and
    /// your desk stay, walls and everything above the horizon fade; at full intensity and radius 0 it is black
    /// all around and only the panels remain. Horizon OS composites the passthrough itself, so it can be
    /// darkened but not blurred.
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
                return;
            }
            if (!EnsureFocusSphere()) return;
            if (!_focusSphere.activeSelf) _focusSphere.SetActive(true);
            var head = Head;
            if (head != null) _focusSphere.transform.position = head.position;
            _focusMat.SetFloat("_Radius", Mathf.Max(0f, Settings.focusRadius));
            _focusMat.SetFloat("_Softness", Mathf.Max(0.02f, Settings.focusSoftness));
            _focusMat.SetFloat("_Intensity", Mathf.Clamp01(Settings.focusIntensity) * _focusShown);
            _focusMat.SetFloat("_FloorY", FloorY());
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
        private float FloorY()
        {
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
            _focusInfo?.Set(Settings.focusEnabled
                ? $"On: the room fades from {Settings.focusRadius:0.0} m around you, over {Settings.focusSoftness:0.0} m, to {Settings.focusIntensity * 100f:0}% dark."
                : "Off. Turn it on here, with FOCUS on the dock, or say “focus mode on”.", UiTheme.Current.TextDim);
        }
    }
}
