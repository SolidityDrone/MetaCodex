using UnityEngine;

namespace MetaCodex.Shell
{
    /// The start splash (owner 2026-11-13: "a splash when the app starts, in passthrough so the user is not blind, that
    /// disappears with a transition"): the MetaCodex logo (Resources/Brand/splash.png, transparent) about a metre in front
    /// of the eyes, over the room, on top of everything (Resources/Shaders/Splash). It stays where it appeared, upright and
    /// facing the user (owner 2026-11-13: "fixed, perpendicular to the ground, facing us; not following the camera"), comes in behind a scan line (0.7 s), holds while the shell starts, then breaks up into light (0.9 s) and is
    /// destroyed with its texture. Not in batch runs (self-tests, builds). It is the first piece of the staged start
    /// (MetaCodexShell.StartStaged), so the first frame shows it; it holds at least SplashHold and for as long as the rest
    /// of the shell is still being built behind it (at most SplashMaxExtra more), then goes.
    public sealed partial class MetaCodexShell
    {
        public const float SplashWidth = 0.60f, SplashDistance = 1.0f;
        private const float SplashIn = 0.7f, SplashHold = 1.4f, SplashOut = 0.9f, SplashMaxExtra = 8f;
        private GameObject _splash;
        private Material _splashMat;
        private Texture2D _splashTex;
        private float _splashT = -1f, _splashWait, _splashExtra;
        private static readonly int SplashFadeId = Shader.PropertyToID("_Fade"), SplashScanId = Shader.PropertyToID("_Scan"),
            SplashDissolveId = Shader.PropertyToID("_Dissolve");

        private void BuildSplash()
        {
            StartMark("splash");
            if (Application.isBatchMode) return;
            _splashTex = Resources.Load<Texture2D>("Brand/splash");
            var sh = Resources.Load<Shader>("Shaders/Splash") ?? Shader.Find("MetaCodex/Splash");
            if (_splashTex == null || sh == null) { Log("splash: texture or shader missing"); return; }
            _splashMat = new Material(sh) { name = "splash", mainTexture = _splashTex };
            _splashMat.SetFloat(SplashFadeId, 0f);
            _splash = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _splash.name = "Splash";
            Destroy(_splash.GetComponent<Collider>());
            var mr = _splash.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _splashMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _splash.transform.localScale = new Vector3(SplashWidth, SplashWidth * _splashTex.height / _splashTex.width, 1f);
            _splash.SetActive(false);
        }

        /// Every frame until it is gone: waits for a tracked head (up to 1 s), then in, hold, out.
        private void TickSplash(float dt)
        {
            if (_splash == null) return;
            var head = Head;
            if (_splashT < 0f)
            {
                _splashWait += dt;
                bool tracked = head != null && head.position.sqrMagnitude > 1e-4f;
                if (!tracked && _splashWait < 1f) return;
                _splashT = 0f;
                _splash.SetActive(true);
                PlaceSplash(head, 1f);
                NoteSplashShown(); // its time goes into the [start] line (ShellStartTiming)
                RequestStartPermissions();
            }
            float step = Mathf.Min(dt, 0.1f); // a long frame does not skip the transition
            // the shell is still being built behind it (the staged start): it holds, at most SplashMaxExtra more
            if (_staging && _splashT >= SplashIn + SplashHold && _splashExtra < SplashMaxExtra) _splashExtra += step;
            else _splashT += step;
            float t = _splashT;
            float fadeIn = Mathf.SmoothStep(0f, 1f, t / SplashIn);
            float scan = Mathf.Clamp01(t / SplashIn);
            float outT = Mathf.Clamp01((t - SplashIn - SplashHold) / SplashOut);
            _splashMat.SetFloat(SplashFadeId, fadeIn * (1f - Mathf.SmoothStep(0.55f, 1f, outT)));
            _splashMat.SetFloat(SplashScanId, scan);
            _splashMat.SetFloat(SplashDissolveId, outT);
            float grow = 1f + 0.05f * Mathf.SmoothStep(0f, 1f, outT);
            _splash.transform.localScale = new Vector3(SplashWidth * grow, SplashWidth * grow * _splashTex.height / _splashTex.width, 1f);
            if (outT >= 1f) EndSplash();
        }

        /// Once, when it appears: a metre ahead of where the user faces (the yaw only), at eye level, upright (perpendicular
        /// to the ground) and facing them; then it stays there in the room.
        private void PlaceSplash(Transform head, float k)
        {
            if (head == null) return;
            var f = head.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            _splash.transform.SetPositionAndRotation(head.position + f * SplashDistance, Quaternion.LookRotation(f, Vector3.up));
        }

        private void EndSplash()
        {
            if (_splash != null) Destroy(_splash);
            if (_splashMat != null) Destroy(_splashMat);
            if (_splashTex != null) Resources.UnloadAsset(_splashTex);
            _splash = null; _splashMat = null; _splashTex = null;
        }

        /// Every runtime permission the app uses, asked once, together, while the splash shows (feat/ready-on-install,
        /// owner 2026-11-13: "give all the permissions during startup"): the microphone (TALK), spatial data (the room's
        /// depth: Ambient Focus, real things in front of windows, panels kept off walls) and the headset camera (BLUR,
        /// the sharp depth). Horizon OS shows its dialogs one after another; a refused one is asked again by its feature
        /// when used (VoiceRecorder, ShellAmbient), as before. Installing apps is a system setting, opened when a native
        /// app is first installed.
        private void RequestStartPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var want = new System.Collections.Generic.List<string>();
                foreach (var perm in new[] { UnityEngine.Android.Permission.Microphone, OVRPermissionsRequester.ScenePermission, OVRPermissionsRequester.PassthroughCameraAccessPermission })
                    if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm)) want.Add(perm);
                if (want.Count == 0) return;
                Log("permissions asked at start: " + string.Join(", ", want));
                UnityEngine.Android.Permission.RequestUserPermissions(want.ToArray());
            }
            catch (System.Exception ex) { Log("permissions at start: " + ex.Message); }
#endif
        }

        /// The splash is up (self-tests and the control port's state).
        public bool SplashShowing => _splash != null;
    }
}
