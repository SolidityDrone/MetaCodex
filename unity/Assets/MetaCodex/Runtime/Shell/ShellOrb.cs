using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.Hands;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THE ORB (UiOrb is its body; this is what it does). A sphere of energy that floats by the user:
    /// - it casts the panels: every panel that opens is drawn by a fan of light from the orb (UiPanelFx), and what
    ///   is left of a closed one flies home to it;
    /// - it is where the voice comes from: spoken answers play from its place and it pulses with every syllable; it
    ///   turns its rings to the user while it listens and spins up while it thinks;
    /// - what the app has to say appears beside it: every message is a callout in a column that rises from it
    ///   (UiToasts), the voice HUD is the first card of that column, and a touch on the orb opens its menu: the
    ///   messages so far and a few actions;
    /// - it floats: up to the right of where the user looks, about half a metre away, in a frame that follows the
    ///   user lazily (it stays where it is in the room while the user only glances round, and comes along when
    ///   they turn or walk away); it never sits in front of a window (it takes the nearest free place); pinch it
    ///   and put it anywhere: it keeps that place by the user from then on (the menu's PLACE lets it choose again).
    /// Settings > LOOK > Orb turns it off (settings key `orb`; then panels are cast from their left edge and
    /// messages float in the upper field of view as before).
    public sealed partial class MetaCodexShell
    {
        public UiOrb Orb { get; private set; }

        // candidate places in the frame that follows the user: (yaw off the frame, pitch) in degrees, best first
        private static readonly (float yaw, float pitch)[] OrbSpots =
        {
            (32f, 16f), (38f, 25f), (-32f, 16f), (-38f, 25f), (46f, 7f), (-46f, 7f), (22f, 31f), (-22f, 31f), (52f, -8f), (-52f, -8f),
        };
        public const float OrbDistance = 0.52f;
        private static readonly Vector2[] OrbProbes = { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down };

        private float _orbYaw, _orbTurnSince = -1f, _orbClock, _orbTapAt = -10f;
        private Vector3 _orbAnchor;
        private bool _orbFramed, _orbApplied;
        private int _orbSpot, _orbSpotCursor, _orbUnread;
        private readonly float[] _orbBlockedSince = new float[OrbSpots.Length], _orbFreeSince = new float[OrbSpots.Length];
        private float _orbCurYaw, _orbCurPitch, _orbCurDist = OrbDistance, _orbYawVel, _orbPitchVel, _orbDistVel;
        private int _orbGrabHand = -1;
        private Vector3 _orbGrabOffset;
        private readonly bool[] _orbWasPinch = new bool[2], _orbWasInside = new bool[2];
        private float[] _speechEnv;
        private float _speechT = -1f, _speechLen;
        private UiPanel _orbMenu;
        private UiList _orbList;
        private UiLabel _orbStatus;
        private UiButton _orbSpeakBtn;
        private Action<UiPanel, bool> _orbOnCast;
        private Action<UiPanel> _orbOnAbsorb;

        /// The orb is there (the setting, and it has ignited enough to cast).
        public bool OrbActive => Orb != null && Orb.Visible;

        private void BuildOrb()
        {
            if (Orb != null) return;
            Orb = UiOrb.Create(_root);
            Orb.Head = Head;
            // it casts the panels
            UiPanelFx.Caster = () => Orb != null && Orb.Visible && Orb.Appear > 0.35f ? Orb.Position : (Vector3?)null;
            UiPanelFx.Eye = () => Head != null ? Head.position : (Vector3?)null;
            _orbOnCast = (p, opening) =>
            {
                if (Orb == null || !Orb.Visible || p == null || p == _orbMenu || p.Id != null && p.Id.StartsWith("orb-")) return;
                if (opening) Orb.Cast(p.transform.position);
            };
            _orbOnAbsorb = p => { if (Orb != null && Orb.Visible) Orb.Absorb(); };
            UiPanelFx.Casting += _orbOnCast;
            UiPanelFx.Absorbed += _orbOnAbsorb;
            // its callouts
            Toasts.Where = OrbCalloutAnchor;
            Toasts.Shown += OnNoteShown;
            // a hand at the orb is the orb's, not the window's behind it
            Router.HandBlocked = OrbHoldsHand;
            _orbApplied = !Settings.orb;
            ApplyOrbSetting();
            // once, on the headset: what this thing is
            if (Application.isPlaying && Settings.orb && !Settings.orbTipShown)
                After(4f, () =>
                {
                    if (!Settings.orb || Settings.orbTipShown) return;
                    Settings.orbTipShown = true;
                    Settings.Save(_settingsPath);
                    Toast("This is the orb: it casts your windows and speaks. Touch it for your messages, pinch it to move it.", "info");
                });
        }

        private void OnDestroy()
        {
            if (_orbOnCast != null) UiPanelFx.Casting -= _orbOnCast;
            if (_orbOnAbsorb != null) UiPanelFx.Absorbed -= _orbOnAbsorb;
            if (Orb != null) { UiPanelFx.Caster = null; UiPanelFx.Eye = null; }
        }

        /// Settings > LOOK > Orb, voice "turn the orb off", Lua settings.set("orb", "false").
        public void SetOrb(bool on)
        {
            Settings.orb = on;
            OnSettingsChanged();
        }

        private void ApplyOrbSetting()
        {
            if (Orb == null || _orbApplied == Settings.orb) return;
            _orbApplied = Settings.orb;
            if (Settings.orb)
            {
                _orbFramed = false; // it ignites where it belongs now, not where it was
                FrameOrb(true);
                Orb.Teleport(OrbHome());
            }
            else if (_orbMenu != null && _orbMenu.IsOpen) _orbMenu.Close();
            Orb.Show(Settings.orb);
            Sounds?.Play(Settings.orb ? "orb-on" : "orb-off");
        }

        // ------------------------------------------------------------------ where it floats

        private static Vector3 OrbDir(float yawDeg, float pitchDeg)
        {
            float y = yawDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
        }

        private Vector3 OrbPlace(float yawOff, float pitch, float dist) => _orbAnchor + OrbDir(_orbYaw + yawOff, pitch) * dist;

        private Vector3 OrbHome() => OrbPlace(_orbCurYaw, _orbCurPitch, _orbCurDist);

        /// Sets the frame it floats in from the head now (and jumps the eased place there when `snap`).
        private void FrameOrb(bool snap)
        {
            var head = Head;
            Vector3 hp = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            var f = head != null ? head.forward : Vector3.forward;
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            if (_orbFramed && !snap)
            {
                // keep it where it is in the room while the frame turns under it: it then eases to its place in the new frame
                var rel = Orb.Home - hp;
                float dist = Mathf.Max(rel.magnitude, 0.2f);
                _orbCurYaw = Mathf.DeltaAngle(headYaw, Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg);
                _orbCurPitch = Mathf.Asin(Mathf.Clamp(rel.y / dist, -1f, 1f)) * Mathf.Rad2Deg;
                _orbCurDist = dist;
            }
            _orbYaw = headYaw;
            _orbAnchor = hp;
            _orbFramed = true;
            _orbTurnSince = -1f;
            PickOrbSpot(true);
            if (snap) { var s = OrbTarget(); _orbCurYaw = s.yaw; _orbCurPitch = s.pitch; _orbCurDist = s.dist; _orbYawVel = _orbPitchVel = _orbDistVel = 0f; }
        }

        private (float yaw, float pitch, float dist) OrbTarget()
        {
            if (Settings.orbPinned) return (Settings.orbYaw, Settings.orbPitch, Mathf.Clamp(Settings.orbDist, 0.25f, 1.2f));
            var s = OrbSpots[Mathf.Clamp(_orbSpot, 0, OrbSpots.Length - 1)];
            return (s.yaw, s.pitch, OrbDistance);
        }

        /// A place is taken when the orb there would sit in front of a window (seen from the head).
        private bool OrbSpotBlocked(int i)
        {
            var head = Head;
            Vector3 eye = head != null ? head.position : _orbAnchor;
            var at = OrbPlace(OrbSpots[i].yaw, OrbSpots[i].pitch, OrbDistance);
            float r = Orb.Radius * 1.6f;
            var fwd = (at - eye).normalized;
            var right = Vector3.Cross(Vector3.up, fwd).normalized;
            var up = Vector3.Cross(fwd, right);
            foreach (var probe in OrbProbes) // its centre and its four sides
            {
                var ray = new Ray(eye, (at + right * (r * probe.x) + up * (r * probe.y) - eye).normalized);
                foreach (var p in Router.Panels)
                {
                    if (p == null || !p.IsOpen || p == _orbMenu || p.Id == "toast" || p.Id == "voice-hud" || p.Id != null && p.Id.StartsWith("orb-")) continue;
                    if (p.Raycast(ray, out _, out _)) return true;
                }
            }
            return false;
        }

        /// Keeps the best free place: it leaves a place a window has covered for half a second, and goes back to a
        /// better one only after that has been free for three (no wandering about while windows move).
        private void PickOrbSpot(bool now)
        {
            if (Orb == null || Settings.orbPinned) return;
            // looking at a place costs five rays against every window: all of them when the frame was just set, else
            // one place a frame, in turn (ten frames for all: no spike)
            for (int k = 0; k < (now ? OrbSpots.Length : 1); k++)
            {
                int i = now ? k : _orbSpotCursor = (_orbSpotCursor + 1) % OrbSpots.Length;
                bool blocked = OrbSpotBlocked(i);
                if (blocked) { if (_orbBlockedSince[i] < 0f) _orbBlockedSince[i] = _orbClock; _orbFreeSince[i] = -1f; }
                else { if (_orbFreeSince[i] < 0f) _orbFreeSince[i] = _orbClock; _orbBlockedSince[i] = -1f; }
            }
            if (!now && Orb.Aim > 0.5f && Orb.State == UiOrb.Mood.Idle && Orb.Attention < 0.3f) return; // it is casting: it moves when it is done
            bool Free(int i, float forSeconds) => _orbFreeSince[i] >= 0f && (now || _orbClock - _orbFreeSince[i] >= forSeconds);
            bool mineBlocked = _orbBlockedSince[_orbSpot] >= 0f && (now || _orbClock - _orbBlockedSince[_orbSpot] >= 0.5f);
            if (mineBlocked)
            {
                for (int i = 0; i < OrbSpots.Length; i++) if (Free(i, 0f)) { _orbSpot = i; return; }
                return; // every place is in front of a window: it stays
            }
            for (int i = 0; i < _orbSpot; i++) if (Free(i, 3f)) { _orbSpot = i; return; }
        }

        private void TickOrbPlace(float dt)
        {
            var head = Head;
            if (head == null) return;
            if (!_orbFramed) { FrameOrb(true); Orb.Teleport(OrbHome()); }
            // the frame follows the user: at once when they walk away, after a moment when they turn (a glance is not a turn);
            // sooner while the orb has something to say
            var f = head.forward;
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            var d = head.position - _orbAnchor;
            bool moved = new Vector2(d.x, d.z).magnitude > 0.25f || Mathf.Abs(d.y) > 0.18f;
            bool busy = Orb.State != UiOrb.Mood.Idle || Toasts.Count > 0 || (_orbMenu != null && _orbMenu.IsOpen);
            bool turned = Mathf.Abs(Mathf.DeltaAngle(_orbYaw, headYaw)) > (busy ? 26f : 34f);
            if (turned) { if (_orbTurnSince < 0f) _orbTurnSince = _orbClock; } else _orbTurnSince = -1f;
            if (_orbGrabHand < 0 && (moved || turned && _orbClock - _orbTurnSince > (busy ? 0.15f : 0.7f))) FrameOrb(false);
            PickOrbSpot(false);
            if (_orbGrabHand >= 0) return; // in the user's fingers
            // ease round the user (never straight through their head) to its place in the frame
            var t = OrbTarget();
            _orbCurYaw = Mathf.SmoothDampAngle(_orbCurYaw, t.yaw, ref _orbYawVel, 0.30f, 400f, dt);
            _orbCurPitch = Mathf.SmoothDamp(_orbCurPitch, t.pitch, ref _orbPitchVel, 0.30f, 300f, dt);
            _orbCurDist = Mathf.SmoothDamp(_orbCurDist, t.dist, ref _orbDistVel, 0.30f, 3f, dt);
            Orb.Home = OrbHome();
        }

        // ------------------------------------------------------------------ hands: touch it, pinch it, point at it

        private (bool tracked, Vector3 tip, Ray ray, bool pinch) OrbHand(int h)
        {
            var st = Gestures?.Get((Handedness)h);
            if (st == null || !st.Tracked) return (false, default, default, false);
            return (true, st.Frame.IndexTip, st.Frame.Ray(), st.Pinching);
        }

        private bool OrbRayOn(Ray ray, out float along)
        {
            var to = Orb.Position - ray.origin;
            along = Vector3.Dot(to, ray.direction);
            if (along < 0.12f || along > 2.5f) return false;
            return (to - ray.direction * along).magnitude < Orb.Radius * 1.7f;
        }

        /// The router asks: is this hand busy with the orb (holding it, a fingertip at it, its ray on it)? Then it
        /// does not also click the window behind.
        private bool OrbHoldsHand(int h)
        {
            if (!OrbActive || Orb.Appear < 0.6f) return false;
            if (_orbGrabHand == h) return true;
            var hand = OrbHand(h);
            if (!hand.tracked) return false;
            if (Vector3.Distance(hand.tip, Orb.Position) < 0.085f) return true;
            return OrbRayOn(hand.ray, out _);
        }

        private void TickOrbHands(float dt)
        {
            float attention = 0f;
            Vector3 attendTo = Orb.Position;
            for (int h = 0; h < 2; h++)
            {
                var hand = OrbHand(h);
                if (!hand.tracked) { _orbWasPinch[h] = false; _orbWasInside[h] = false; if (_orbGrabHand == h) EndOrbGrab(); continue; }
                float dist = Vector3.Distance(hand.tip, Orb.Position);
                bool near = dist < 0.085f, rayOn = !near && OrbRayOn(hand.ray, out _);
                float a = near ? 1f : dist < 0.16f ? 1f - (dist - 0.085f) / 0.075f : rayOn ? 0.6f : 0f;
                if (a > attention) { attention = a; attendTo = hand.tip; }
                bool pinchEdge = hand.pinch && !_orbWasPinch[h];
                if (_orbGrabHand == h)
                {
                    // in the fingers: it goes where they go, until they open
                    if (hand.pinch) { Orb.Home = hand.tip + _orbGrabOffset; }
                    else EndOrbGrab();
                }
                else if (_orbGrabHand < 0 && pinchEdge && near && !Router.HandBusy(h))
                {
                    _orbGrabHand = h;
                    _orbGrabOffset = Orb.Position - hand.tip;
                    Orb.SettleTime = 0.06f;
                    Sounds?.Play("click");
                }
                else if (_orbGrabHand < 0 && pinchEdge && rayOn && !Router.HandBusy(h)) ToggleOrbMenu();
                // a touch: the fingertip goes into it (not while pinching: that is a grab)
                bool inside = dist < Orb.Radius * 1.15f;
                if (inside && !_orbWasInside[h] && !hand.pinch && _orbGrabHand < 0) ToggleOrbMenu();
                _orbWasInside[h] = inside;
                _orbWasPinch[h] = hand.pinch;
            }
            Orb.Attention = Mathf.Lerp(Orb.Attention, attention, 1f - Mathf.Exp(-dt * 10f));
            if (attention > 0.01f) Orb.AttendTo = attendTo;
        }

        /// Let go: it keeps this place by the user (yaw off the frame, pitch, distance) from now on.
        private void EndOrbGrab()
        {
            if (_orbGrabHand < 0) return;
            _orbGrabHand = -1;
            Orb.SettleTime = 0.14f;
            var head = Head;
            if (head == null) return;
            // the frame is taken from the head now: the place is "there, as I stand now"
            var f = head.forward;
            _orbYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _orbAnchor = head.position;
            var rel = Orb.Position - _orbAnchor;
            float dist = Mathf.Clamp(rel.magnitude, 0.25f, 1.2f);
            Settings.orbPinned = true;
            Settings.orbYaw = Mathf.DeltaAngle(_orbYaw, Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg);
            Settings.orbPitch = Mathf.Asin(Mathf.Clamp(rel.y / Mathf.Max(rel.magnitude, 1e-3f), -1f, 1f)) * Mathf.Rad2Deg;
            Settings.orbDist = dist;
            _orbCurYaw = Settings.orbYaw; _orbCurPitch = Settings.orbPitch; _orbCurDist = dist;
            _orbYawVel = _orbPitchVel = _orbDistVel = 0f;
            Settings.Save(_settingsPath);
            Sounds?.Play("click");
        }

        /// The orb chooses its own place again (the menu's PLACE).
        public void ResetOrbPlace()
        {
            Settings.orbPinned = false;
            Settings.Save(_settingsPath);
            _orbSpot = 0;
            FrameOrb(false);
        }

        // ------------------------------------------------------------------ the voice

        /// The loudness of a clip, 50 values a second (0..1): the orb pulses with it while the clip plays.
        public static float[] SpeechEnvelope(float[] samples, int channels, int rate)
        {
            if (samples == null || samples.Length == 0 || rate <= 0) return new float[0];
            int win = Mathf.Max(1, rate / 50) * Mathf.Max(1, channels);
            int n = Mathf.Max(1, samples.Length / win);
            var env = new float[n];
            float max = 1e-4f;
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                int end = Math.Min(samples.Length, (i + 1) * win);
                for (int k = i * win; k < end; k++) sum += samples[k] * samples[k];
                env[i] = Mathf.Sqrt((float)(sum / Math.Max(1, end - i * win)));
                if (env[i] > max) max = env[i];
            }
            for (int i = 0; i < n; i++) env[i] = Mathf.Clamp01(env[i] / max * 1.15f);
            return env;
        }

        /// A spoken answer starts (VoiceSay): the orb speaks it.
        private void OnSpeechStarted(AudioClip clip)
        {
            _speechEnv = null;
            _speechT = -1f;
            if (clip == null) return;
            try
            {
                var data = new float[clip.samples * clip.channels];
                clip.GetData(data, 0);
                _speechEnv = SpeechEnvelope(data, clip.channels, clip.frequency);
                _speechLen = clip.length;
                _speechT = 0f;
            }
            catch (Exception ex) { Log("orb: speech envelope: " + ex.Message); }
            if (_voiceAudio != null)
            {
                // the voice comes from the orb (a little of it stays in both ears: no hard pan)
                bool orb = OrbActive;
                _voiceAudio.spatialBlend = orb ? 0.8f : 0f;
                _voiceAudio.minDistance = 0.6f;
                _voiceAudio.maxDistance = 6f;
                _voiceAudio.rolloffMode = AudioRolloffMode.Linear;
                _voiceAudio.dopplerLevel = 0f;
                if (orb) _voiceAudio.transform.position = Orb.Position;
            }
        }

        /// Self-tests: the orb speaks this loudness curve (50 values a second) without any audio.
        public void OrbSpeakForTest(float[] envelope) { _speechEnv = envelope; _speechLen = envelope.Length / 50f; _speechT = 0f; }

        private void TickOrbVoice(float dt)
        {
            bool speaking = _speechT >= 0f && _speechEnv != null && _speechEnv.Length > 0;
            if (speaking)
            {
                // the audio's own clock when it plays, else our own (tests, a device without audio focus)
                if (_voiceAudio != null && _voiceAudio.isPlaying) _speechT = _voiceAudio.time; else _speechT += dt;
                if (_speechT >= _speechLen || (_voiceAudio != null && Application.isPlaying && !_voiceAudio.isPlaying && _speechT > 0.3f)) { speaking = false; _speechT = -1f; }
            }
            if (speaking)
            {
                Orb.SetMood(UiOrb.Mood.Speaking);
                Orb.Loudness = _speechEnv[Mathf.Clamp((int)(_speechT * 50f), 0, _speechEnv.Length - 1)];
                if (_voiceAudio != null) _voiceAudio.transform.position = Orb.Position;
            }
            else if (VoiceState == "listening") { Orb.SetMood(UiOrb.Mood.Listening); Orb.Loudness = Recorder != null ? Recorder.Level : 0f; }
            else if (VoiceState == "transcribing" || VoiceState == "thinking") { Orb.SetMood(UiOrb.Mood.Thinking); Orb.Loudness = 0f; }
            else { Orb.SetMood(UiOrb.Mood.Idle); Orb.Loudness = 0f; }
        }

        // ------------------------------------------------------------------ what it has to say

        /// The side its callouts and its menu grow to: towards the middle of the user's view.
        private Vector3 OrbSide(out bool left)
        {
            var head = Head;
            Vector3 eye = head != null ? head.position : _orbAnchor;
            var to = Orb.Position - eye; to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) to = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, to.normalized);
            var f = head != null ? head.forward : Vector3.forward; f.y = 0f;
            // the orb is to the right of where the user looks: its cards go left, and the other way round
            left = Vector3.Dot(to.normalized, Vector3.Cross(Vector3.up, f.sqrMagnitude < 1e-6f ? Vector3.forward : f.normalized)) >= 0f;
            return left ? -right : right;
        }

        private bool _orbSideLeft = true;
        private float _orbSideHold;

        private UiToasts.Anchor? OrbCalloutAnchor()
        {
            if (!OrbActive) return null;
            // the side does not flip while cards are up, unless the user really turned
            var side = OrbSide(out bool left);
            if (left != _orbSideLeft && (Toasts.Count == 0 || _orbClock - _orbSideHold > 1.5f)) { _orbSideLeft = left; _orbSideHold = _orbClock; }
            if (left != _orbSideLeft) side = -side;
            float dist = Head != null ? Vector3.Distance(Head.position, Orb.Position) : OrbDistance;
            return new UiToasts.Anchor { Orb = Orb.Position, Side = side, Radius = Orb.Radius, Scale = Mathf.Clamp(dist / 0.75f, 0.45f, 1.2f) * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f) / 1.2f };
        }

        private void OnNoteShown(UiToasts.Note n)
        {
            // not read until the user opens the orb's menu: it rides round the orb as a gold spark (four at most)
            if (_orbMenu == null || !_orbMenu.IsOpen) _orbUnread = Mathf.Min(_orbUnread + 1, 99);
            if (Orb != null) Orb.Unread = Mathf.Min(_orbUnread, 4);
            if (Orb != null && Orb.Visible)
            {
                var t = UiTheme.Current;
                if (n.Kind == "error") Orb.Flash(t.Danger, 1.4f);
                else if (n.Kind == "warn") Orb.Flash(t.Warn, 1.2f);
                else if (n.Kind == "ok") Orb.Flash(t.Ok, 1.0f);
                else Orb.Flash(Color.Lerp(t.Accent, Color.white, 0.4f), 0.7f);
            }
            if ((_orbMenu != null && _orbMenu.IsOpen) || (_messages != null && _messages.IsOpen)) RefreshOrbMenu();
        }

        // ------------------------------------------------------------------ its menu

        // the billboard: four small buttons on a strip of glass beside the orb, always turned to the user (owner
        // 2026-11-10: "that small menu kinda a billboard next to the orb", not a window with the messages in it)
        public const float BillboardW = 0.236f, BillboardH = 0.060f;

        private void EnsureOrbMenu()
        {
            if (_orbMenu != null) return;
            var t = UiTheme.Current;
            _orbMenu = UiPanel.Create<UiPanel>("orb-menu", "", BillboardW, BillboardH, _root, header: false);
            _orbMenu.Closable = false;
            _orbMenu.Draggable = false;
            _orbMenu.LightTransition = true;
            Router.Register(_orbMenu);
            // under it, the threads used last (ShellOrbThreads): they come and go with it
            _orbMenu.Opened += _ => OpenOrbThreads();
            _orbMenu.Closed += _ => CloseOrbThreads();
            const float pad = 0.007f, gap = 0.005f;
            float bw = (BillboardW - pad * 2f - gap * 3f) / 4f, bh = BillboardH - pad * 2f;
            UiButton B(int k, string text, Action act, UiButtonStyle style = UiButtonStyle.Normal) =>
                _orbMenu.Add(new UiButton(text, act, style) { Rect = new Rect(pad + k * (bw + gap), pad, bw, bh), TextSize = 0.0092f });
            B(0, "◉\nTALK", () => VoiceToggle(), UiButtonStyle.Primary);
            _orbSpeakBtn = B(1, "♫\nSPEAK", () => { Settings.voice.speak = !Settings.voice.speak; OnSettingsChanged(); SyncSettingsControls(); RefreshOrbMenu(); });
            B(2, "⌖\nPLACE", () => { ResetOrbPlace(); Toast("The orb chooses its own place again · pinch it to put it anywhere", "info"); });
            _orbLogBtn = B(3, "☰\nLOG", () => { if (_orbMenu.IsOpen) _orbMenu.Close(); OpenPanel("messages"); });
            _orbMenu.gameObject.SetActive(false);
        }

        private UiButton _orbLogBtn;
        private UiPanel _messages;

        /// MESSAGES: everything the app has said (the orb's callouts), newest first, with their age; CLEAR. A window like
        /// the others (placed where there is room), opened from the orb's billboard (LOG) or by voice ("open messages").
        private void BuildMessagesPanel()
        {
            var t = UiTheme.Current;
            _messages = Panel<UiPanel>("messages", "MESSAGES", 0.50f, 0.42f);
            var c = _messages.Content;
            _orbStatus = _messages.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, c.y, c.width - 0.11f, 0.024f), UseDisplay = true, Color = t.TextAccent });
            _messages.Add(new UiButton("✕ CLEAR", () => { Toasts.History.Clear(); _orbUnread = 0; if (Orb != null) Orb.Unread = 0; RefreshOrbMenu(); }) { Rect = new Rect(c.xMax - 0.10f, c.y, 0.10f, 0.026f), TextSize = 0.010f });
            float ly = c.y + 0.034f;
            _orbList = _messages.Add(new UiList(0.028f) { Rect = new Rect(c.x, ly, c.width, c.yMax - ly) });
            _messages.Opened += _ =>
            {
                _orbUnread = 0; // read now: the gold sparks round the orb go
                if (Orb != null) Orb.Unread = 0;
                RefreshOrbMenu();
            };
        }

        public UiPanel OrbMenu => _orbMenu;

        /// A touch on the orb: its menu opens beside it (the messages so far, a few actions), or closes.
        public void ToggleOrbMenu()
        {
            if (!OrbActive || _orbClock - _orbTapAt < 0.5f) return;
            _orbTapAt = _orbClock;
            EnsureOrbMenu();
            Sounds?.Play("click");
            if (_orbMenu.IsOpen) { _orbMenu.Close(); return; }
            _orbMenu.BaseScale = Vector3.one * OrbMenuScale();
            RefreshOrbMenu();
            PlaceOrbMenu(true);
            _orbMenu.Open();
        }

        private float OrbMenuScale()
        {
            float dist = Head != null ? Vector3.Distance(Head.position, Orb.Position) : OrbDistance;
            return Mathf.Clamp(dist / 0.75f, 0.45f, 1.2f) * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f) / 1.2f * 1.05f;
        }

        /// Next to the orb, on the side its callouts grow to, just below its level (the callouts rise above it), turned to
        /// the user.
        private void PlaceOrbMenu(bool snap)
        {
            if (_orbMenu == null || Orb == null) return;
            var a = OrbCalloutAnchor();
            if (!a.HasValue) return;
            float s = _orbMenu.BaseScale.x;
            var target = a.Value.Orb + a.Value.Side * (Orb.Radius * 2.15f + _orbMenu.Width * s * 0.5f + 0.012f) + Vector3.down * (Orb.Radius * 0.9f + _orbMenu.Height * s * 0.5f);
            var tr = _orbMenu.transform;
            tr.position = snap ? target : Vector3.Lerp(tr.position, target, 0.16f);
            Vector3 eye = Head != null ? Head.position : target - Vector3.forward;
            var look = tr.position - eye;
            if (look.sqrMagnitude > 1e-6f) tr.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }

        private static string Ago(DateTime at)
        {
            var d = DateTime.Now - at;
            if (d.TotalSeconds < 50) return "now";
            if (d.TotalMinutes < 60) return (int)Math.Round(d.TotalMinutes) + " min";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h";
            return at.ToString("dd MMM");
        }

        private void RefreshOrbMenu()
        {
            var t = UiTheme.Current;
            int n = Toasts.History.Count, fresh = Mathf.Min(_orbUnread, n);
            if (_orbMenu != null)
            {
                _orbSpeakBtn.SetSelected(Settings.voice.speak);
                _orbLogBtn.SetText(fresh > 0 ? "☰\n" + fresh + " NEW" : "☰\nLOG");
                _orbLogBtn.SetSelected(fresh > 0);
            }
            if (_messages == null) return;
            _orbStatus.Set((n == 0 ? "NO MESSAGES YET" : n == 1 ? "1 MESSAGE" : n + " MESSAGES") + (fresh > 0 ? " · " + fresh + " NEW" : ""));
            _orbList.SetItems(Toasts.History.Select(h => new UiListItem
            {
                Icon = h.Kind == "error" ? "⚠" : h.Kind == "warn" ? "▲" : h.Kind == "ok" ? "✓" : "◆",
                IconColor = h.Kind == "error" ? t.Danger : h.Kind == "warn" ? t.Warn : h.Kind == "ok" ? t.Ok : t.TextAccent,
                Title = h.Text, Sub = Ago(h.At),
            }), true);
        }

        // ------------------------------------------------------------------ every frame

        private void TickOrb(float dt)
        {
            if (Orb == null) return;
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            _orbClock += dt;
            ApplyOrbSetting();
            Orb.Head = Head;
            if (!Settings.orb) return;
            TickOrbPlace(dt);
            if (Gestures != null) TickOrbHands(dt);
            TickOrbVoice(dt);
            if (_orbMenu != null && _orbMenu.gameObject.activeSelf) { PlaceOrbMenu(false); TickOrbThreads(); }
        }

        /// Self-tests: one frame of the orb (the Editor has no frame loop).
        public void TickOrbForTest(float dt)
        {
            TickOrb(dt);
            if (Orb != null) Orb.Tick(dt);
            Toasts.Tick(Time.unscaledTime);
        }

        /// Self-tests: the voice pipeline's state (listening, thinking, done...) without a microphone.
        public void SetVoiceStateForTest(string state, string title, string text) => SetVoiceState(state, title, text);

        public bool OrbHoldsHandForTest(int hand) => OrbHoldsHand(hand);
        public (float yaw, float pitch) OrbSpotForTest => OrbSpots[Mathf.Clamp(_orbSpot, 0, OrbSpots.Length - 1)];
        public int OrbGrabHandForTest => _orbGrabHand;
        /// Messages shown since the orb's menu was last open.
        public int OrbUnread => _orbUnread;
    }
}
