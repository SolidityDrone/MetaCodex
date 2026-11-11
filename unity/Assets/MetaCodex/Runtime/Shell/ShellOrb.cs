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
                if (Orb == null || !Orb.Visible || p == null || p.Id != null && p.Id.StartsWith("orb-")) return;
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
            else if (OrbMenuOpen) CloseOrbMenu();
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
            _avoidCheckAt = 0f;
            PickOrbSpot(true);
            if (snap) { var s = OrbTarget(); _orbCurYaw = s.yaw; _orbCurPitch = s.pitch; _orbCurDist = s.dist; _orbYawVel = _orbPitchVel = _orbDistVel = 0f; }
        }

        /// Its place in the frame now: its own place, moved aside while it would cover the window being watched
        /// (ShellOrbAvoid).
        private (float yaw, float pitch, float dist) OrbTarget()
        {
            var t = OrbTargetBase();
            return (t.yaw + _avoidYaw, t.pitch + _avoidPitch, t.dist);
        }

        private (float yaw, float pitch, float dist) OrbTargetBase()
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
                    if (p == null || !p.IsOpen || p.Id == "toast" || p.Id == "voice-hud" || p.Id != null && p.Id.StartsWith("orb-")) continue;
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
            bool busy = Orb.State != UiOrb.Mood.Idle || Toasts.Count > 0 || OrbMenuOpen;
            bool turned = Mathf.Abs(Mathf.DeltaAngle(_orbYaw, headYaw)) > (busy ? 26f : 34f);
            if (turned) { if (_orbTurnSince < 0f) _orbTurnSince = _orbClock; } else _orbTurnSince = -1f;
            if (_orbGrabHand < 0 && (moved || turned && _orbClock - _orbTurnSince > (busy ? 0.15f : 0.7f))) FrameOrb(false);
            int spot = _orbSpot;
            PickOrbSpot(false);
            if (spot != _orbSpot) _avoidCheckAt = 0f; // a new place: is the watched window clear from there?
            TickWatched();
            TickOrbAvoid();
            if (_orbGrabHand >= 0) return; // in the user's fingers
            // ease round the user (never straight through their head) to its place in the frame; gliding aside from the
            // window being watched, a little slower (it never jumps)
            var t = OrbTarget();
            float ease = _avoidYaw != 0f || _avoidPitch != 0f ? 0.42f : 0.30f;
            _orbCurYaw = Mathf.SmoothDampAngle(_orbCurYaw, t.yaw, ref _orbYawVel, ease, 400f, dt);
            _orbCurPitch = Mathf.SmoothDamp(_orbCurPitch, t.pitch, ref _orbPitchVel, ease, 300f, dt);
            _orbCurDist = Mathf.SmoothDamp(_orbCurDist, t.dist, ref _orbDistVel, ease, 3f, dt);
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
            // looking straight at it counts too (owner 2026-11-11: "less invasive but always available")
            if (Head != null)
            {
                var toOrb = Orb.Position - Head.position;
                float gazeDeg = Vector3.Angle(Head.forward, toOrb);
                if (gazeDeg < 8f) attention = Mathf.Max(attention, 0.5f * (1f - gazeDeg / 8f));
            }
            Orb.Attention = Mathf.Lerp(Orb.Attention, attention, 1f - Mathf.Exp(-dt * 10f));
            if (attention > 0.01f) Orb.AttendTo = attendTo;
            // idle (no hand, no gaze, no message, menu closed): 80 % of its size; it grows back as a hand comes near
            bool idle = Orb.Attention < 0.15f && Toasts.Count == 0 && !OrbMenuOpen && Orb.State == UiOrb.Mood.Idle;
            if (_orbBaseRadius <= 0f) _orbBaseRadius = Orb.Radius;
            Orb.Radius = Mathf.Lerp(Orb.Radius, _orbBaseRadius * (idle ? 0.8f : 1f), 1f - Mathf.Exp(-dt * 4f));
        }

        private float _orbBaseRadius;

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
            _avoidYaw = _avoidPitch = 0f; // where it is let go is its place now
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

        private UiToasts.Anchor? OrbCalloutAnchor()
        {
            if (!OrbActive) return null;
            // owner 2026-11-11: the menu always on the orb's left, the callouts always on its right, never swapped: an
            // arc round its right side, concentric with it (UiToasts lays it out)
            var head = Head;
            var to = Orb.Position - (head != null ? head.position : _orbAnchor); to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) to = Vector3.forward;
            var side = Vector3.Cross(Vector3.up, to.normalized); // the user's right, seen from the eye
            float dist = Head != null ? Vector3.Distance(Head.position, Orb.Position) : OrbDistance;
            return new UiToasts.Anchor { Orb = Orb.Position, Side = side, Radius = OrbOuterRadius / 1.75f, Scale = Mathf.Clamp(dist / 0.75f, 0.45f, 1.2f) * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f) / 1.2f * 1.12f }; // their text about a degree
        }

        private void OnNoteShown(UiToasts.Note n)
        {
            // not read until the user opens the orb's menu: it rides round the orb as a gold spark (four at most)
            if (!OrbMenuOpen) _orbUnread = Mathf.Min(_orbUnread + 1, 99);
            if (Orb != null) Orb.Unread = Mathf.Min(_orbUnread, 4);
            if (Orb != null && Orb.Visible)
            {
                var t = UiTheme.Current;
                if (n.Kind == "error") Orb.Flash(t.Danger, 1.4f);
                else if (n.Kind == "warn") Orb.Flash(t.Warn, 1.2f);
                else if (n.Kind == "ok") Orb.Flash(t.Ok, 1.0f);
                else Orb.Flash(Color.Lerp(t.Accent, Color.white, 0.4f), 0.7f);
            }
            if (OrbMenuOpen || (_messages != null && _messages.IsOpen)) RefreshOrbMenu();
        }

        // ------------------------------------------------------------------ its menu: an arc round its left side

        // owner 2026-11-11: "more like wrapped around the circle of the orb ... always on its left and notification on
        // right". In the plane through the orb that faces the user: TALK, SPEAK, PLACE, LOG on an inner arc that hugs
        // the orb's left side (from 10:30 to 7:30 on a clock), the threads used last on an outer arc, concentric with
        // the orb (each card's corner nearest the orb on one circle), every item turned to the user. Text about a degree
        // at the menu's half a metre (Meta's VR text guidance): labels 13.5 mm, card titles 14.5 mm, at a scale of ~0.9.
        public const float OrbActW = 0.060f, OrbActH = 0.054f, OrbActText = 0.0135f;
        private static readonly float[] OrbActAngles = { 135f, 165f, 195f, 225f };
        private readonly List<UiPanel> _orbActs = new List<UiPanel>();

        /// The orb's menu is up (its four actions; the thread cards come and go with it).
        public bool OrbMenuOpen => _orbActs.Count > 0 && _orbActs[0].IsOpen;
        private bool OrbMenuDrawn => _orbActs.Count > 0 && _orbActs.Any(a => a.gameObject.activeSelf);

        /// The orb's outer edge: its rings (1.75 radii), at its full size (it is smaller when idle).
        private float OrbOuterRadius => (_orbBaseRadius > 0f ? _orbBaseRadius : Orb != null ? Orb.Radius : 0.032f) * 1.75f;

        /// The arc menu's two radii at scale `s` (metres in the orb's plane): the actions' centres, and the circle the
        /// thread cards' nearest corners sit on. The actions clear the orb's rings and each other.
        public (float inner, float outer) OrbMenuRadii(float s)
        {
            float w = OrbActW * s, h = OrbActH * s, ring = OrbOuterRadius + 0.008f * s;
            float ri = ring + Mathf.Min(w, h) * 0.5f;
            for (int it = 0; it < 80; it++, ri += 0.002f)
            {
                bool ok = true;
                for (int k = 0; k < OrbActAngles.Length && ok; k++)
                {
                    float a = OrbActAngles[k] * Mathf.Deg2Rad;
                    var c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ri;
                    // the corner nearest the orb stays off its rings
                    var near = new Vector2(c.x - Mathf.Sign(c.x) * Mathf.Min(Mathf.Abs(c.x), w * 0.5f), c.y - Mathf.Sign(c.y) * Mathf.Min(Mathf.Abs(c.y), h * 0.5f));
                    if (near.magnitude < ring) ok = false;
                    if (k > 0)
                    {
                        float b = OrbActAngles[k - 1] * Mathf.Deg2Rad;
                        var d = c - new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * ri;
                        if (Mathf.Abs(d.x) < w + 0.003f * s && Mathf.Abs(d.y) < h + 0.003f * s) ok = false; // neighbours apart
                    }
                }
                if (ok) break;
            }
            float outer = ri + w * 0.5f + 0.010f * s;
            return (ri, outer);
        }

        /// What the open menu covers in the orb's plane (metres, x to the user's right, y up), at scale `s`.
        private Rect OrbMenuExtent(float s)
        {
            var (ri, ro) = OrbMenuRadii(s);
            int n = Mathf.Max(1, _orbCardsShown);
            float pitch = (OrbCardH + OrbCardGap) * s;
            float half = Mathf.Max((n - 1) * 0.5f * pitch + OrbCardH * s * 0.5f, ri * 0.71f + OrbActH * s * 0.5f);
            float left = _orbCardsShown > 0 ? ro + OrbCardW * s : ri + OrbActW * s * 0.5f;
            return new Rect(-left, -half, left - OrbOuterRadius, half * 2f);
        }

        /// The orb's plane: through it, facing the eye; `right` is the user's right (level), `up` across it.
        private void OrbPlane(Vector3 at, out Vector3 right, out Vector3 up)
        {
            Vector3 eye = Head != null ? Head.position : at - Vector3.forward;
            var fwd = at - eye;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            up = Vector3.Cross(fwd, right);
        }

        /// Puts an orb item at (x, y) of the orb's plane, turned to the user (eased unless `snap`).
        private void PutOnOrbPlane(UiPanel p, Vector2 at, bool snap)
        {
            OrbPlane(Orb.Position, out var right, out var up);
            var target = Orb.Position + right * at.x + up * at.y;
            var tr = p.transform;
            var pos = snap ? target : Vector3.Lerp(tr.position, target, 0.16f);
            Vector3 eye = Head != null ? Head.position : pos - Vector3.forward;
            var look = pos - eye;
            tr.SetPositionAndRotation(pos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : tr.rotation);
        }

        private void EnsureOrbMenu()
        {
            if (_orbActs.Count > 0) return;
            const float pad = 0.004f;
            UiButton B(int k, string text, Action act, UiButtonStyle style = UiButtonStyle.Normal)
            {
                var p = UiPanel.Create<UiPanel>("orb-act-" + k, "", OrbActW, OrbActH, _root, header: false);
                p.Closable = false;
                p.Draggable = false;
                p.LightTransition = true;
                Router.Register(p);
                p.gameObject.SetActive(false);
                _orbActs.Add(p);
                return p.Add(new UiButton(text, act, style) { Rect = new Rect(pad, pad, OrbActW - pad * 2f, OrbActH - pad * 2f), TextSize = OrbActText });
            }
            B(0, "◉\nTALK", () => VoiceToggle(), UiButtonStyle.Primary);
            _orbSpeakBtn = B(1, "♫\nSPEAK", () => { Settings.voice.speak = !Settings.voice.speak; OnSettingsChanged(); SyncSettingsControls(); RefreshOrbMenu(); });
            B(2, "⌖\nPLACE", () => { ResetOrbPlace(); Toast("The orb chooses its own place again · pinch it to put it anywhere", "info"); });
            _orbLogBtn = B(3, "☰\nLOG", () => { if (OrbMenuOpen) CloseOrbMenu(); OpenPanel("messages"); });
        }

        private UiButton _orbLogBtn;
        private UiPanel _messages;

        /// MESSAGES: everything the app has said (the orb's callouts), newest first, with their age; CLEAR. A window like
        /// the others (placed where there is room), opened from the orb's menu (LOG) or by voice ("open messages").
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

        /// The menu's four action tiles (TALK, SPEAK, PLACE, LOG), top to bottom on the inner arc.
        public IReadOnlyList<UiPanel> OrbActionsForTest => _orbActs;

        /// A touch on the orb: its menu opens round its left side, or closes.
        public void ToggleOrbMenu()
        {
            if (!OrbActive || _orbClock - _orbTapAt < 0.5f) return;
            _orbTapAt = _orbClock;
            EnsureOrbMenu();
            Sounds?.Play("click");
            if (OrbMenuOpen) { CloseOrbMenu(); return; }
            float s = OrbMenuScale();
            foreach (var a in _orbActs) a.BaseScale = Vector3.one * s;
            RefreshOrbMenu();
            PlaceOrbMenu(true);
            foreach (var a in _orbActs) a.Open();
            OpenOrbThreads(); // the threads used last, on the outer arc
            _avoidCheckAt = 0f; // look again at once: the menu may cover the window being watched
        }

        private void CloseOrbMenu()
        {
            foreach (var a in _orbActs) if (a.IsOpen) a.Close();
            CloseOrbThreads();
            _avoidCheckAt = 0f;
        }

        private float OrbMenuScale()
        {
            float dist = Head != null ? Vector3.Distance(Head.position, Orb.Position) : OrbDistance;
            // ~0.95 at half a metre: the labels' em is then about a degree (TMP's em is 0.72 of the line size)
            return Mathf.Clamp(dist / 0.75f, 0.45f, 1.2f) * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f) / 1.2f * 1.30f;
        }

        private float OrbMenuScaleNow => _orbActs.Count > 0 ? _orbActs[0].BaseScale.x : OrbMenuScale();

        /// The actions on the inner arc round the orb's left side (TALK at the top), every one turned to the user.
        private void PlaceOrbMenu(bool snap)
        {
            if (_orbActs.Count == 0 || Orb == null) return;
            float s = OrbMenuScaleNow;
            var (ri, _) = OrbMenuRadii(s);
            for (int k = 0; k < _orbActs.Count; k++)
            {
                if (!snap && !_orbActs[k].gameObject.activeSelf) continue;
                float a = OrbActAngles[k] * Mathf.Deg2Rad;
                PutOnOrbPlane(_orbActs[k], new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ri, snap);
            }
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
            if (_orbActs.Count > 0)
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
            if (OrbMenuDrawn) { PlaceOrbMenu(false); TickOrbThreads(); }
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
