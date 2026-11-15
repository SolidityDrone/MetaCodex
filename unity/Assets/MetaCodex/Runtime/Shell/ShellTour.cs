using MetaCodex.AI;
using MetaCodex.Hands;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The guided tour (owner 2026-11-14: "a small guided wizard that guides you on the first steps: the radial menu, the
    /// orb, panels, settings; since TTS may not be ready, highlights and short instruction tooltips ... interactive, but
    /// you can skip; roughly the time it takes to learn the first few things, more or less a minute"). It starts beside
    /// the first start's SETUP panel (ShellFirstRun) and is there again from CONTROL > ABOUT > TOUR, the SETUP panel's
    /// TOUR, the voice command `start_tour` and the action `tour`. Five cards, each beside what it is about, with a ring
    /// of light round its target (or the target window's frame lit) and SKIP STEP / SKIP TOUR; each waits until the user
    /// has done it, says ✓ and goes on:
    ///   1 the main menu: the wrist menu opened (WRIST mode) or CLIP tapped on the DOCK;
    ///   2 the orb: its menu opened (a touch, or a pinch while pointing at it); skipped when the orb is off;
    ///   3 windows: a window moved by its top bar and let go (the SETUP window is the one lit);
    ///   4 settings: CONTROL opened from the main menu;
    ///   5 what comes next (FINISH): what the setup unlocks and where (TALK, THREADS, AGENTS > GET).
    /// It never asks for something the setup still locks (ShellSetupLocks). Per frame: a few state reads, the ring's pose
    /// and, 12 times a second, its dots' colours; no allocation.
    public sealed partial class MetaCodexShell
    {
        public const int TourSteps = 5;
        public const float TourCardW = 0.40f, TourCardH = 0.25f;
        /// After the user did a step, its ✓ shows this long before the next card.
        public const float TourDonePause = 0.9f;

        private int _tourStep = -1;
        private bool _tourOffered, _tourHooked, _tourStepDone, _tourDropped, _tourLastSetup, _tourEnding;
        private float _tourStepAt, _tourDoneAt = -1f, _tourAwaySince = -1f, _tourPulseAt;
        private UiPanel _tourCard, _tourRing, _tourLit;
        private UiLabel _tourText, _tourHint;
        private UiButton _tourNext, _tourSkip, _tourTile;
        private readonly int[] _ringDots = new int[16];
        private int _ringHead = -1;

        /// The tour is on (self-tests, `ui setup`).
        public bool TourRunning => _tourStep >= 0;
        /// The card on show (1..5), 0 when the tour is off.
        public int TourStepNow => _tourStep + 1;
        public UiPanel TourCard => _tourCard;
        public string TourTextNow => _tourText != null ? _tourText.Text : "";

        /// Starts the tour from its first card (again, when it runs).
        public void StartTour()
        {
            EnsureTourCard();
            if (!_tourHooked && Router != null) { _tourHooked = true; Router.PanelDropped += OnTourPanelDropped; }
            _tourOffered = true;
            Log("tour: started" + (_setupActive ? " (with the first start's setup)" : ""));
            ShowTourStep(0, TourClock);
        }

        /// Ends the tour (SKIP TOUR, FINISH, the card's ✕, the action `tour:stop`).
        public void EndTour(string why)
        {
            if (_tourStep < 0) return;
            Log($"tour: {why} at card {_tourStep + 1} of {TourSteps}");
            _tourStep = -1;
            TourHighlight(null);
            if (_tourRing != null) _tourRing.gameObject.SetActive(false);
            if (_tourCard != null && _tourCard.IsOpen) { _tourEnding = true; _tourCard.Close(); _tourEnding = false; }
        }

        /// Voice (and agents, through the registry): "start the tour", "show me around again". Its action is `tour`.
        private void RegisterTourVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "start_tour",
                Description = "Start the guided tour: about a minute on the main menu, the orb, moving a window and CONTROL; every card can skip its step or the whole tour",
                Phrases = new[]
                {
                    "(start|begin|play|show|run|replay|restart|open|do) [the|a] [guided] (tour|tutorial|walkthrough|introduction) [again] [please]",
                    "(show|give) me [the|a] [guided] (tour|tutorial|walkthrough) [again] [please]",
                    "(show|take|walk) me (around|through) [the app|metacodex|the basics] [again] [please]",
                },
                Samples = new[]
                {
                    "give me the guided tour again", "walk me through the basics of the app", "I'd like to see the introduction tour",
                    "replay the tutorial", "show me around the app", "start the onboarding tour",
                },
                Examples = new System.Collections.Generic.List<(string, string)> { ("show me the tour again", "start_tour"), ("play the tutorial", "start_tour") },
                Vocabulary = new[] { "tour", "tutorial", "walkthrough" },
                Run = a => { StartTour(); return "Tour started"; },
            });
        }

        /// The tour's clock: Time.unscaledTime, or the self-tests' own (TickFirstRunForTest).
        private float TourClock => Application.isPlaying ? Time.unscaledTime : SetupClockForTest;

        // ------------------------------------------------------------------ the card

        private void EnsureTourCard()
        {
            if (_tourCard != null) return;
            var t = UiTheme.Current;
            var p = UiPanel.Create<UiPanel>("tour", "TOUR", TourCardW, TourCardH, _root);
            p.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            var c = p.Content;
            _tourText = p.Add(new UiLabel("", 0.0128f) { Rect = new Rect(c.x, c.y, c.width, 0.112f), Wrap = true, Color = t.Text, Align = TextAlignmentOptions.TopLeft });
            _tourHint = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, c.y + 0.112f + t.Gap, c.width, 0.02f), Color = t.TextAccent });
            float by = c.yMax - t.ButtonHeight, bw = (c.width - t.Gap) * 0.5f;
            _tourNext = p.Add(new UiButton("SKIP STEP", () => TourNextPressed()) { Rect = new Rect(c.x, by, bw, t.ButtonHeight) });
            _tourSkip = p.Add(new UiButton("SKIP TOUR", () => EndTour("skipped")) { Rect = new Rect(c.x + bw + t.Gap, by, bw, t.ButtonHeight) });
            p.Closed += _ => { if (!_tourEnding && _tourStep >= 0) EndTour("closed"); };
            Router.Register(p);
            p.gameObject.SetActive(false);
            _tourCard = p;
        }

        private void TourNextPressed()
        {
            if (_tourStep < 0) return;
            if (_tourStep >= TourSteps - 1) { EndTour("finished"); return; }
            Log($"tour: card {_tourStep + 1} skipped");
            ShowTourStep(_tourStep + 1, TourClock);
        }

        private void ShowTourStep(int step, float now)
        {
            _tourStep = step;
            _tourStepAt = now;
            _tourStepDone = false;
            _tourDoneAt = -1f;
            _tourDropped = false;
            _tourAwaySince = -1f;
            _tourTile = null;
            // the orb is off: nothing to show there
            if (step == 1 && (!Settings.orb || Orb == null)) { ShowTourStep(2, now); return; }
            var t = UiTheme.Current;
            bool wrist = Settings.wristMenu;
            string title, text, hint = "";
            switch (step)
            {
                case 0:
                    title = "THE MAIN MENU";
                    text = wrist
                        ? "Your main menu is on your left wrist. Look at the back of your left wrist as at a watch, then touch the watch face with your right index finger: the tiles open round it."
                        : "Your main menu is the DOCK, the row of tiles in front of you, lower down. Tap CLIP: pinch it with the ray, or touch it with a fingertip.";
                    if (_setupActive) text += " Tiles with 🔒 wake up by themselves when the setup is done.";
                    hint = wrist ? "Waiting for the menu to open…" : "Waiting for CLIP…";
                    if (!wrist) _tourTile = DockTile("CLIP");
                    break;
                case 1:
                    title = "THE ORB";
                    text = "This is the orb: it casts your windows, speaks, and keeps your messages. Touch it with a fingertip, or point at it and pinch, to open its menu.";
                    hint = "Waiting for the orb's menu…";
                    break;
                case 2:
                    title = "WINDOWS";
                    text = (Settings.gestures.grabMode == 1
                        ? "Windows stay where you put them. Close your hand on a window, move it, and open your hand."
                        : "Windows stay where you put them. Pinch the bar at the top of a window, move your hand, and let go.")
                        + (SetupPanelShowing ? " Try it with the SETUP window." : " Try it with this card.");
                    hint = "Waiting for a window to move…";
                    break;
                case 3:
                    title = "SETTINGS";
                    text = "Settings live in CONTROL: display, look and sound, hands, voice, agents, the system. Open ⚙ CONTROL on your " + (wrist ? "wrist menu." : "DOCK.");
                    hint = "Waiting for CONTROL…";
                    if (!wrist) _tourTile = DockTile("CONTROL");
                    break;
                default:
                    title = "WHAT COMES NEXT";
                    text = TourLastText();
                    break;
            }
            _tourLastSetup = _setupActive;
            _tourCard.SetTitle("TOUR · " + (step + 1) + " OF " + TourSteps + " · " + title);
            _tourText.Set(text);
            _tourHint.Set(hint, t.TextAccent);
            bool last = step >= TourSteps - 1;
            _tourNext.SetText(last ? "FINISH ✓" : "SKIP STEP");
            _tourNext.SetSelected(last); // FINISH stands out
            _tourSkip.SetVisible(!last);
            PlaceTourCard(step);
            if (!_tourCard.IsOpen) _tourCard.Open();
            else _tourCard.Ping();
            TourHighlight(step == 2 ? TourWindow() : step == 0 && !wrist || step == 3 && !wrist ? PanelOrNull("dock") : null);
        }

        private string TourLastText() => _setupActive
            ? "That is the basics. The setup is still finishing: TALK, THREADS, AGENTS, TERMINALS and FILES unlock by themselves when their step is done. Then TALK takes voice commands, THREADS is where you work with coding agents, and CONTROL > AGENTS > GET adds a coding agent (its key or sign-in is yours). CONTROL > ABOUT > TOUR plays this again."
            : "You are set. TALK takes voice commands, THREADS is where you work with coding agents, TERMS and FILES open your Linux, and CONTROL > AGENTS > GET adds a coding agent (its key or sign-in is yours). CONTROL > ABOUT > TOUR plays this again.";

        private bool SetupPanelShowing => _setupPanel != null && _setupPanel.IsOpen;

        private UiPanel PanelOrNull(string id) => _panels.TryGetValue(id, out var p) && p != null && p.IsOpen ? p : null;

        /// The window step 3 lights: the SETUP window while it shows, else the card itself.
        private UiPanel TourWindow() => SetupPanelShowing ? _setupPanel : _tourCard;

        /// The dock's tile with this label (found once per card, not per frame).
        private UiButton DockTile(string label)
        {
            if (!_panels.TryGetValue("dock", out var dock) || dock == null) return null;
            foreach (var e in dock.Elements)
                if (e is UiButton b && b.Text != null && b.Text.EndsWith("\n" + label)) return b;
            return null;
        }

        /// Beside what the card is about, facing the user: above the DOCK, under the orb (its menu opens on its left, its
        /// messages on its right), right of the SETUP window; else in front, a little low.
        private void PlaceTourCard(int step)
        {
            var head = Head;
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            float s = _tourCard.BaseScale.x;
            float halfW = TourCardW * 0.5f * s, halfH = TourCardH * 0.5f * s;
            Vector3 pos = eye + fwd * 0.6f - Vector3.up * 0.08f + right * 0.05f;
            var dock = PanelOrNull("dock");
            if ((step == 0 || step == 3) && dock != null && !Settings.wristMenu)
                pos = dock.transform.position + Vector3.up * (dock.Height * 0.5f * dock.RestLossyScale.y + halfH + 0.03f);
            else if (step == 0 || step == 3)
                pos = eye + fwd * 0.55f - Vector3.up * 0.10f - right * 0.10f; // the wrist menu comes up lower left
            else if (step == 1 && OrbActive)
                pos = Orb.Position - Vector3.up * (halfH + 0.07f) - right * 0.04f;
            else if (step == 2 && SetupPanelShowing)
            {
                var sp = _setupPanel.transform;
                pos = sp.position + sp.right * (SetupW * 0.5f * _setupPanel.RestLossyScale.x + halfW + 0.04f);
            }
            // not nearer than 45 cm, not further than 90
            var to = pos - eye;
            float d = to.magnitude;
            if (d < 0.45f || d > 0.9f) pos = eye + to.normalized * Mathf.Clamp(d, 0.45f, 0.9f);
            var look = pos - eye;
            _tourCard.transform.SetPositionAndRotation(pos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : Quaternion.LookRotation(fwd, Vector3.up));
        }

        // ------------------------------------------------------------------ every frame

        private void TickTour(float now, float dt)
        {
            if (_tourCard == null) { _tourStep = -1; return; } // rebuilt away
            if (!_tourStepDone && _tourStep < TourSteps - 1 && TourStepMet())
            {
                _tourStepDone = true;
                _tourDoneAt = now;
                _tourHint.Set("✓ Done", UiTheme.Current.Ok);
                Sounds?.Play("ping");
                Log($"tour: card {_tourStep + 1} done in {now - _tourStepAt:F1} s");
            }
            if (_tourStepDone && now - _tourDoneAt >= TourDonePause) { ShowTourStep(_tourStep + 1, now); return; }
            // the last card follows the setup: "still finishing" until it is over
            if (_tourStep == TourSteps - 1 && _tourLastSetup != _setupActive) { _tourLastSetup = _setupActive; _tourText.Set(TourLastText()); }
            TickTourRing(now);
            TickTourPulse(now);
            // the user turned away for 2 s: the card comes back to where they look
            if (Head != null && _tourCard.IsOpen && !Router.IsDragging(_tourCard))
            {
                bool away = Vector3.Angle(Head.forward, _tourCard.transform.position - Head.position) > 55f;
                if (!away) _tourAwaySince = -1f;
                else if (_tourAwaySince < 0f) _tourAwaySince = now;
                else if (now - _tourAwaySince > 2f) { _tourAwaySince = -1f; PlaceTourCard(_tourStep); }
            }
        }

        /// Has the user done what the card asks?
        private bool TourStepMet()
        {
            switch (_tourStep)
            {
                case 0: return Settings.wristMenu ? Wrist != null && Wrist.Expanded : IsPanelOpen("clipboard");
                case 1: return OrbMenuOpen;
                case 2: return _tourDropped;
                case 3: return ControlRailPage != null;
                default: return false;
            }
        }

        private void OnTourPanelDropped(UiPanel p)
        {
            if (_tourStep != 2 || p == null || p == _tourRing || (p.Id ?? "").StartsWith("orb-")) return;
            _tourDropped = true;
        }

        // ------------------------------------------------------------------ the ring and the lit window

        /// Where the ring goes this frame (false: no ring): the dock's tile, the left wrist, the orb.
        private bool TourRingTarget(out Vector3 at, out float radius)
        {
            at = default;
            radius = 0.05f;
            switch (_tourStep)
            {
                case 0:
                case 3:
                    if (_tourTile != null && _tourTile.Panel != null && _tourTile.Panel.IsOpen)
                    {
                        at = _tourTile.Panel.SurfacePoint(_tourTile.Rect.center);
                        radius = 0.048f * _tourTile.Panel.RestLossyScale.x;
                        return true;
                    }
                    if (!Settings.wristMenu || Wrist == null || Wrist.Expanded) return false;
                    var st = Gestures?.Get(Handedness.Left);
                    if (st == null || !st.Tracked) return false;
                    at = st.Frame.Wrist;
                    if (Head != null) at += (Head.position - at).normalized * 0.05f; // over the wrist, towards the eyes
                    radius = 0.06f;
                    return true;
                case 1:
                    if (!OrbActive || OrbMenuOpen) return false;
                    at = Orb.Position;
                    radius = OrbOuterRadius * 1.35f;
                    return true;
                default:
                    return false;
            }
        }

        private void TickTourRing(float now)
        {
            bool show = TourRingTarget(out var at, out float radius) && !_tourStepDone;
            if (!show) { if (_tourRing != null && _tourRing.gameObject.activeSelf) _tourRing.gameObject.SetActive(false); return; }
            EnsureTourRing();
            if (!_tourRing.gameObject.activeSelf) { _tourRing.gameObject.SetActive(true); _ringHead = -1; }
            float k = radius / TourRingR;
            if (Mathf.Abs(_tourRing.BaseScale.x - k) > 0.002f) _tourRing.BaseScale = Vector3.one * k;
            Vector3 eye = Head != null ? Head.position : at - Vector3.forward;
            var look = at - eye;
            _tourRing.transform.SetPositionAndRotation(at, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : _tourRing.transform.rotation);
            // a bright arc runs round it, 12 steps a second (colours only, restyled in place)
            int head = (int)(now * 12f) % _ringDots.Length;
            if (head == _ringHead) return;
            _ringHead = head;
            var t = UiTheme.Current;
            for (int i = 0; i < _ringDots.Length; i++)
            {
                int d = (head - i + _ringDots.Length) % _ringDots.Length;
                float a = d < 5 ? 1f - d / 5f : 0.25f;
                _tourRing.Shapes.SetTint(_ringDots[i], Color.Lerp(SpinDim, t.Accent, a));
            }
        }

        private const float TourRingR = 0.05f;

        private void EnsureTourRing()
        {
            if (_tourRing != null) return;
            var t = UiTheme.Current;
            const float size = 0.13f, dot = 0.0065f;
            var p = UiPanel.Create<UiPanel>("tour-ring", "", size, size, _root, header: false, frameless: true);
            p.Closable = false;
            p.Draggable = false; // never registered with the router: it takes no input
            for (int i = 0; i < _ringDots.Length; i++)
            {
                float a = (90f - i * 360f / _ringDots.Length) * Mathf.Deg2Rad;
                var c = new Vector2(size * 0.5f + Mathf.Cos(a) * TourRingR, size * 0.5f - Mathf.Sin(a) * TourRingR);
                _ringDots[i] = p.Shapes.Add(p.Shape(UiShapeKind.Dot, new Rect(c.x - dot * 0.5f, c.y - dot * 0.5f, dot, dot), t.Accent, 0f, 0f, 0.004f, 0.8f));
            }
            p.gameObject.SetActive(false);
            _tourRing = p;
        }

        /// Lights a window's frame for the card (null: none); the one lit before goes dark.
        private void TourHighlight(UiPanel p)
        {
            if (_tourLit == p) return;
            if (_tourLit != null) _tourLit.SetHighlight(0f);
            _tourLit = p;
            _tourPulseAt = -1f;
        }

        /// The lit window's frame breathes (15 times a second at most).
        private void TickTourPulse(float now)
        {
            if (_tourLit == null) return;
            if (_tourStepDone || !_tourLit.IsOpen) { TourHighlight(null); return; }
            if (now - _tourPulseAt < 1f / 15f) return;
            _tourPulseAt = now;
            _tourLit.SetHighlight(0.35f + 0.3f * Mathf.Sin(now * 4f));
        }
    }
}
