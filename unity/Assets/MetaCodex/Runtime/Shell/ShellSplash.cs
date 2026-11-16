using System;
using System.Collections.Generic;
using MetaCodex.App;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The start splash (owner 2026-11-13: "a splash when the app starts, in passthrough so the user is not blind, that
    /// disappears with a transition"): the MetaCodex logo (Resources/Brand/splash.png, transparent) about a metre in front
    /// of the eyes, over the room, on top of everything (Resources/Shaders/Splash). It stays where it appeared, upright and
    /// facing the user (owner 2026-11-13: "fixed, perpendicular to the ground, facing us; not following the camera"), comes in behind a scan line (0.7 s), holds while the shell starts, then breaks up into light (0.9 s) and is
    /// destroyed with its texture. Not in batch runs (self-tests, builds). It is the first piece of the staged start
    /// (MetaCodexShell.StartStaged), so it is made in the first frame; it shows as soon as the head has a valid pose, holds
    /// at least SplashHold and for as long as the rest of the shell is still being built behind it (at most SplashMaxExtra
    /// more), then goes.
    ///
    /// Where the start places things (owner 2026-11-16: "the splash screen and stuff spawned on the ground: I was sitting at
    /// the desk and it spawned at my feet"). Cause: since the staged start (2026-11-15) the splash is placed in the first
    /// frames, and it waited for a tracked head at most 1 s counted in summed frame times; the start's first frames are
    /// long (0.7.19 log: 156, 572, 417 ms), so the wait ran out before the runtime gave the head its first pose. The head
    /// was then the eye anchor at the rig's origin, the floor of the Floor Level tracking space, and the splash went a metre
    /// from there, on the floor (0.7.19 log: "splash on show at +820 ms", the app's first tracked head ~0.6 s later). Now:
    ///   - nothing is placed from an invalid pose: the splash waits (wall time, at most MetaCodexApp.StartPoseWaitSeconds)
    ///     for MetaCodexApp.HeadPoseValid two frames in a row; if it never comes, it goes at the default eye height
    ///     (MetaCodexApp.EyeOrDefault). Log: "start: placed at eye y=… (head tracked after N ms) · …".
    ///   - what the start placed (the splash, the windows through the app's Recenter, the guide card) is placed again once,
    ///     at the eyes, when during the start the head moves more than StartMoveM from the first place, the tracking origin
    ///     changes (a recenter, a reference space change), or the first place was the default and the head is tracked now.
    ///     Log: "start: re-placed at eye y=… (why; N ms after the first place): what". The watch ends StartWatchTail
    ///     seconds after the splash is gone and the shell is up.
    public sealed partial class MetaCodexShell
    {
        public const float SplashWidth = 0.60f, SplashDistance = 1.0f;
        private const float SplashIn = 0.7f, SplashHold = 1.4f, SplashOut = 0.9f, SplashMaxExtra = 8f;
        /// The start's placement watch: how far the head may move from the first place (m), how long a change settles
        /// before the place is taken again (s), and how long the watch goes on once the splash is gone and the shell is up
        /// (s, frame time).
        public const float StartMoveM = 0.3f, StartSettleSeconds = 0.25f, StartWatchTail = 5f;
        private GameObject _splash;
        private Material _splashMat;
        private Texture2D _splashTex;
        private float _splashT = -1f, _splashExtra;
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

        /// Every frame from Update, the staged start included: the splash, the start's placement watch, and (once the shell
        /// is up) the wrist menu's fallback.
        private void TickStart(float dt)
        {
            TickSplash(dt);
            TickStartWatch(dt);
            if (!_staging) TickWristFallback(); // a failed wrist menu build: the DOCK, once (ShellWrist)
        }

        /// Every frame until it is gone: waits for a valid head pose (StartPoseReady), then in, hold, out.
        private void TickSplash(float dt)
        {
            if (_splash == null) return;
            var head = Head;
            if (_splashT < 0f)
            {
                if (!StartPoseReady()) return; // never from an invalid pose (the floor under the rig's origin)
                _splashT = 0f;
                _splash.SetActive(true);
                BeginStartWatch(PlaceSplash(head), "splash");
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

        /// When it appears (and at most once more, ReplaceAtStart): a metre ahead of where the user faces (the yaw only), at
        /// eye level (MetaCodexApp.EyeOrDefault), upright (perpendicular to the ground) and facing them; then it stays there
        /// in the room. Returns the eye it was placed from.
        private Vector3 PlaceSplash(Transform head)
        {
            var eye = MetaCodexApp.EyeOrDefault(head);
            var f = head != null ? head.forward : Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            _splash.transform.SetPositionAndRotation(eye + f * SplashDistance, Quaternion.LookRotation(f, Vector3.up));
            return eye;
        }

        // ------------------------------------------------------------------ the start's head pose and placement watch

        private float _poseWaitFrom = -1f, _poseWaitedMs;
        private int _poseValidFrames;
        private bool _poseReady, _poseTracked;

        /// The start's head pose is ready: valid two frames in a row (MetaCodexApp.HeadPoseValid: a pose, the headset present
        /// and its position tracked, the eyes over MinEyeHeight), or StartPoseWaitSeconds of wall time passed without one
        /// (then the places use the default eye height and the watch takes them again once the head is tracked). Once ready,
        /// it stays so.
        private bool StartPoseReady()
        {
            if (_poseReady) return true;
            float now = Time.realtimeSinceStartup;
            if (_poseWaitFrom < 0f) _poseWaitFrom = now;
            var head = Head;
            _poseValidFrames = MetaCodexApp.HeadPoseValid(head) ? _poseValidFrames + 1 : 0;
            if (_poseValidFrames < 2 && now - _poseWaitFrom < MetaCodexApp.StartPoseWaitSeconds) return false;
            _poseReady = true;
            _poseTracked = MetaCodexApp.HeadPoseValid(head, true);
            _poseWaitedMs = (now - _poseWaitFrom) * 1000f;
            return true;
        }

        private bool _watchOn, _watchDone, _watchFallback;
        private Vector3 _watchEye;
        private float _watchT0, _watchTail, _watchAt;
        private int _watchRecenters, _watchOriginEvents, _watchOriginSeen;
        private string _watchWhy, _watchOriginWhat;
        private Action<OVRManager.TrackingOrigin, OVRPose?> _onRuntimeOrigin;
        private Action<UnityEngine.XR.XRInputSubsystem> _onInputOrigin;
        private List<UnityEngine.XR.XRInputSubsystem> _watchInputs;

        /// The start's first place was taken from `eye` (the splash; or, with no splash, the shell up): the log line with
        /// the pose, and the watch begins.
        private void BeginStartWatch(Vector3 eye, string what)
        {
            if (_watchOn || _watchDone || Application.isBatchMode || !Application.isPlaying) return;
            _watchOn = true;
            _watchEye = eye;
            _watchFallback = !_poseTracked;
            _watchT0 = Time.realtimeSinceStartup;
            _watchTail = 0f;
            _watchWhy = null;
            _watchRecenters = RecenterCount();
            _watchOriginEvents = _watchOriginSeen = 0;
            HookOriginEvents(true);
            var h = Head;
            Log($"start: placed at eye y={eye.y:F2} (head {(_poseTracked ? "tracked" : MetaCodexApp.HeadHasPose(Head) ? "NOT tracked, its last pose" : "NOT tracked, default height")} after {_poseWaitedMs:F0} ms) · {what} · " +
                $"origin {MetaCodexApp.TrackingOriginText()}, head {(h != null ? h.position.ToString("F2") : "none")}, {HeadStateText()}");
        }

        /// Every frame while the watch runs (a few field tests, three plugin reads): takes the start's places again once when
        /// the head moved more than StartMoveM from the first place, the tracking origin changed, or the first place was the
        /// default height and the head is tracked now; after StartSettleSeconds, from a valid pose.
        private void TickStartWatch(float dt)
        {
            if (!_watchOn)
            {
                // no splash on this start (its texture or shader is missing): the watch begins with the shell up
                if (_watchDone || _splash != null || _splashT >= 0f || _staging || Application.isBatchMode || !Application.isPlaying) return;
                if (StartPoseReady()) BeginStartWatch(MetaCodexApp.EyeOrDefault(Head), "shell up");
                return;
            }
            var head = Head;
            if (head == null) { EndStartWatch(); return; }
            // the start is over StartWatchTail after the splash is gone and the shell is up (frame time: a pause does not
            // use it up); a change that is settling gets a second more
            if (!_staging && _splash == null)
            {
                _watchTail += Mathf.Min(dt, 0.1f);
                if (_watchTail >= StartWatchTail && (_watchWhy == null || _watchTail >= StartWatchTail + 1f)) { EndStartWatch(); return; }
            }
            bool valid = MetaCodexApp.HeadPoseValid(head, true);
            if (_watchWhy == null)
            {
                if (!valid) return; // only a valid pose moves anything
                float moved2 = (head.position - _watchEye).sqrMagnitude;
                if (_watchFallback) _watchWhy = "the head is tracked now";
                else if (moved2 > StartMoveM * StartMoveM) _watchWhy = "the head moved " + Mathf.Sqrt(moved2).ToString("F2") + " m";
                else if (_watchOriginEvents > 0) _watchWhy = "the tracking origin changed: " + _watchOriginWhat;
                else if (RecenterCount() != _watchRecenters) _watchWhy = "a recenter";
                else return;
                _watchOriginSeen = _watchOriginEvents;
                _watchAt = Time.realtimeSinceStartup + StartSettleSeconds;
                return;
            }
            // one recenter may send several changes over a few frames: each one gives the settle time again
            if (_watchOriginEvents != _watchOriginSeen) { _watchOriginSeen = _watchOriginEvents; _watchAt = Time.realtimeSinceStartup + StartSettleSeconds; }
            if (!valid || Time.realtimeSinceStartup < _watchAt) return;
            ReplaceAtStart(head);
        }

        /// Once: the splash (while it shows), the windows (the app's Recenter: the terminal, the DOCK in DOCK mode, the
        /// Whisp, the others turned with them; at the end of the staged start when it still runs), and the guide card
        /// (unless the user moved it) go in front of the eyes again.
        private void ReplaceAtStart(Transform head)
        {
            var eye = head.position;
            string what = "";
            if (_splash != null && _splash.activeSelf) { PlaceSplash(head); what = "splash"; }
            if (app != null && app.Placed) { app.Recenter(); what += (what.Length > 0 ? ", " : "") + "windows"; }
            if (_guide != null && _guide.Panel != null && _guide.Panel.IsOpen && !_tourMoved)
            {
                PlaceGuide(Mathf.Max(0, _tourStep), TourClock, GuideWhy.First);
                what += (what.Length > 0 ? ", " : "") + "guide card";
            }
            Log($"start: re-placed at eye y={eye.y:F2} ({_watchWhy}; {(Time.realtimeSinceStartup - _watchT0) * 1000f:F0} ms after the first place): {(what.Length > 0 ? what : "nothing was placed yet")}");
            EndStartWatch();
        }

        private void EndStartWatch()
        {
            if (!_watchOn) return;
            _watchOn = false;
            _watchDone = true;
            HookOriginEvents(false);
        }

        /// The runtime's reference space change (a recenter, the floor found again) and the engine's tracking origin update
        /// (a new origin mode or space): either counts as a change while the watch runs. The handlers only count.
        private void HookOriginEvents(bool on)
        {
            try
            {
                if (_onRuntimeOrigin == null)
                {
                    _onRuntimeOrigin = (origin, pose) => { _watchOriginEvents++; _watchOriginWhat = "runtime space change"; };
                    _onInputOrigin = s => { _watchOriginEvents++; _watchOriginWhat = "tracking origin update"; };
                }
                if (on) OVRManager.TrackingOriginChangePending += _onRuntimeOrigin;
                else OVRManager.TrackingOriginChangePending -= _onRuntimeOrigin;
                if (on)
                {
                    _watchInputs ??= new List<UnityEngine.XR.XRInputSubsystem>();
                    _watchInputs.Clear();
                    SubsystemManager.GetSubsystems(_watchInputs);
                    foreach (var s in _watchInputs) if (s != null) s.trackingOriginUpdated += _onInputOrigin;
                }
                else if (_watchInputs != null)
                {
                    foreach (var s in _watchInputs) if (s != null) s.trackingOriginUpdated -= _onInputOrigin;
                    _watchInputs.Clear();
                }
            }
            catch (Exception ex) { Log("start: tracking origin events: " + ex.Message); }
        }

        /// The runtime's count of the user's recenters (0 off the headset).
        private static int RecenterCount()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return OVRPlugin.GetLocalTrackingSpaceRecenterCount(); }
            catch (Exception) { return 0; }
#else
            return 0;
#endif
        }

        /// For the start's log line: is the headset present and its position tracked, did the rig give the head a pose.
        private string HeadStateText()
        {
            bool pose = MetaCodexApp.HeadHasPose(Head);
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return $"present {OVRManager.isHmdPresent}, position tracked {OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head)}, pose {pose}"; }
            catch (Exception ex) { return "pose " + pose + ", plugin: " + ex.Message; }
#else
            return "pose " + pose;
#endif
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
