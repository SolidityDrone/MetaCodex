using MetaCodex.AI;
using MetaCodex.Hands;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The guide card and the guided tour (owner 2026-11-14: "a small guided wizard that guides you on the first steps ...
    /// interactive, but you can skip; roughly the time it takes to learn the first few things, more or less a minute";
    /// 2026-11-15: ONE compact card, the Whisp as the guide, smooth, clear status). On the first start the card is also the
    /// status of the one-time setup (its strip, ShellFirstRun); the same tour plays again from CONTROL > ABOUT > TOUR, the
    /// voice command `start_tour` and the action `tour`, without the strip.
    ///
    /// Six beats, each with one action, a hint while it waits, a done line (0.9 s, with a ping) and then the next beat; the
    /// text and the hint fade out and in between beats (label alpha only), the dots advance, the card pings and moves
    /// smoothly to its next place:
    ///   1 HELLO         the Whisp says hi; NEXT, or by itself after 12 s;
    ///   2 THE MAIN MENU the wrist menu opened (WRIST mode) or CLIP opened on the DOCK (a ring round its tile);
    ///   3 THE WHISP     its menu opened (a ring round the Whisp); skipped when the Whisp is off;
    ///   4 WINDOWS       a window moved by its bar and let go: the card itself (its frame breathes);
    ///   5 CONTROL       CONTROL opened from the main menu (a ring round its tile);
    ///   6 YOUR TURN     TALK pressed and a command said: the card shows what voice heard (a ring round the TALK tile).
    /// It never asks for something the setup still locks (ShellSetupLocks): while TALK is locked the last beat says so and
    /// changes to the real one when the lock lifts. The card never covers its target, the DOCK, the Whisp or an open
    /// window (it asks the views of the open windows where it may sit). Per frame: a few state reads, the card's ease, the
    /// ring's pose and, 12 times a second, its dots' colours; no allocation.
    public sealed partial class MetaCodexShell
    {
        public const int TourSteps = 6;
        /// The guide card's width before the UI scale (its height is what its strip and tour need).
        public const float GuideW = 0.40f;
        /// After the user did a beat's action, its done line shows this long before the next beat.
        public const float TourDonePause = 0.9f;
        /// The finale adds this line to its text when it is not empty: the integrator fills it (for example "Or just say
        /// “Hey Whisp”.") once the wake phrase has landed.
        public const string TourWakeLine = "";

        private const string GuideId = "tour";
        private const float GuideStripH = 0.075f, GuideTextH = 0.052f, GuideHintH = 0.016f;
        private const float HelloAutoSeconds = 12f, FadeOutSeconds = 0.12f, FadeInSeconds = 0.18f, GuideMoveSeconds = 0.35f;
        /// How far (degrees, in the user's view) the card keeps from what it must not cover.
        private const float GuideMargin = 1.5f;
        private const float TourRingR = 0.065f;

        /// The one card: the strip (first start only, rows 1-3), the divider, the tour (text, hint, dots and two buttons).
        private sealed class GuideCard
        {
            public UiPanel Panel;
            public bool HasStrip;
            public Rect Content;
            /// Where the content starts in the panel, the strip's height, where the tour section starts now (it slides
            /// up while the strip folds away).
            public float ContentTop, StripH, TourTop;
            public UiLabel Words, StepNo, Pct, Note;
            public UiProgress Bar;
            public UiDivider Divider;
            public readonly int[] Spin = new int[12];
            public bool SpinOn = true;
            public int SpinHead = -1;
            public UiLabel Text, Hint, Dots;
            public UiButton Next, Skip, Close;
        }

        private GuideCard _guide;
        private int _tourStep = -1;
        private bool _tourHooked, _tourStepDone, _tourDropped, _tourEnding, _tourFirst, _tourMoved;
        private float _tourStepAt, _tourDoneAt = -1f, _tourAwaySince = -1f, _tourPulseAt, _guideCheckAt;
        private UiPanel _tourRing, _tourLit;
        private UiButton _tourTile;
        private readonly int[] _ringDots = new int[12];
        private int _ringHead = -1;
        // the fade between two beats: 1 the old text goes, 2 the new one comes
        private int _fadePhase, _fadeNext;
        private float _fadeAt;
        private bool _fadeRefresh;
        private Color _textColor, _hintColor;
        // the locks the shown text was made for, and the last voice result the finale has seen
        private int _beatLocks;
        private VoiceResult _tourVoiceSeen;
        // the card's eased move
        private bool _moveActive;
        private float _moveT0;
        private Vector3 _moveFromPos, _moveToPos;
        private Quaternion _moveFromRot, _moveToRot;

        /// The tour is on (self-tests, `ui setup`).
        public bool TourRunning => _tourStep >= 0;
        /// The beat on show (1..6), 0 when the tour is off.
        public int TourStepNow => _tourStep + 1;
        /// The guide card (null before it is made).
        public UiPanel TourCard => _guide != null ? _guide.Panel : null;
        public string TourTextNow => _guide != null && _guide.Text != null ? _guide.Text.Text ?? "" : "";
        public string TourHintNow => _guide != null && _guide.Hint != null ? _guide.Hint.Text ?? "" : "";
        public string TourDotsNow => _guide != null && _guide.Dots != null ? _guide.Dots.Text ?? "" : "";

        /// Starts the tour from its first beat (again, when it runs). The card is the first start's (with the strip) while
        /// the setup runs or its end shows, else a card with the tour alone.
        public void StartTour()
        {
            float now = TourClock;
            bool strip = _setupActive || _stripAlive;
            var g = EnsureGuideCard(strip);
            if (!_tourHooked && Router != null) { _tourHooked = true; Router.PanelDropped += OnTourPanelDropped; }
            _guideWanted = false;
            _tourFirst = _setupActive;
            _tourMoved = false;
            _tourEnding = false;
            _fadePhase = 0;
            _moveActive = false;
            g.Panel.BaseScale = Vector3.one * UiScaleNow;
            if (g.HasStrip)
            {
                _stripAlive = true;
                if (_setupActive) StripSync(g, now);
                if (!_guideLogged) { _guideLogged = true; Log("setup: guide card shown"); }
            }
            Log("tour: started" + (_setupActive ? " (with the first start's setup)" : ""));
            ApplyBeat(0, now, false);
            SetBeatAlpha(1f);
            PlaceGuide(0, now, GuideWhy.First);
            if (!g.Panel.IsOpen) g.Panel.Open();
            else g.Panel.Ping();
        }

        /// Ends the tour (SKIP TOUR, FINISH, the card's ✕, the action `tour:stop`). While the strip still shows (the setup
        /// runs, its end shows) the card stays with the strip and says so; else it closes.
        public void EndTour(string why)
        {
            if (_tourStep < 0) return;
            Log($"tour: {why} at card {TourShownNo(_tourStep)} of {TourShownTotal()}");
            _tourStep = -1;
            _fadePhase = 0;
            _moveActive = false;
            _tourStepDone = false;
            TourHighlight(null);
            if (_tourRing != null) _tourRing.gameObject.SetActive(false);
            var g = _guide;
            if (g == null || g.Panel == null) return;
            SetBeatAlpha(1f);
            if (why != "closed" && _stripAlive && g.HasStrip && g.Panel.IsOpen) ShowTourEnded(g, why == "finished");
            else if (g.Panel.IsOpen) { _tourEnding = true; g.Panel.Close(); _tourEnding = false; }
        }

        /// The tour part of a card that stays for the strip, after the tour: what happened and how to play it again.
        private void ShowTourEnded(GuideCard g, bool finished)
        {
            var t = UiTheme.Current;
            _textColor = t.TextDim;
            _hintColor = t.TextAccent;
            g.Text.Set(finished ? "That's the tour. CONTROL > ABOUT > TOUR plays it again." : "Tour skipped. CONTROL > ABOUT > TOUR plays it again.", _textColor);
            g.Hint.Set("", _hintColor);
            g.Dots.Set("");
            g.Next.SetVisible(false);
            g.Skip.SetVisible(false);
            g.Panel.SetTitle("ONE-TIME SETUP");
        }

        /// Voice (and agents, through the registry): "start the tour", "show me around again". Its action is `tour`.
        private void RegisterTourVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "start_tour",
                Description = "Start the guided tour: about a minute on the main menu, the Whisp, moving a window, CONTROL and TALK; every card can skip its step or the whole tour",
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
        private float TourClock => FirstRunNow;

        // ------------------------------------------------------------------ the card

        /// Heights of the card's rows (panel units): the tour section is text, hint and the bottom row.
        private static void TourRects(Rect c, float top, UiTheme t, out Rect text, out Rect hint, out Rect dots, out Rect next, out Rect skip)
        {
            text = new Rect(c.x, top, c.width, GuideTextH);
            hint = new Rect(c.x, top + GuideTextH + 0.003f, c.width, GuideHintH);
            float by = top + GuideTextH + 0.003f + GuideHintH + 0.005f;
            const float bw = 0.104f;
            skip = new Rect(c.xMax - bw, by, bw, t.ButtonHeight);
            next = new Rect(skip.x - t.Gap - bw, by, bw, t.ButtonHeight);
            dots = new Rect(c.x, by, next.x - t.Gap - c.x, t.ButtonHeight);
        }

        /// The card with the layout asked for; made if it is not there (or is the other layout), inactive.
        private GuideCard EnsureGuideCard(bool strip)
        {
            if (_guide != null && _guide.Panel == null) _guide = null; // destroyed under us
            if (_guide != null && _guide.HasStrip != strip)
            {
                DisposeGuideCard(_guide); // the first start's card has the strip, a replay's has none: each is as tall as it needs
                _guide = null;
            }
            if (_guide == null) _guide = BuildGuideCard(strip);
            return _guide;
        }

        private GuideCard BuildGuideCard(bool strip)
        {
            var t = UiTheme.Current;
            float tourH = GuideTextH + 0.003f + GuideHintH + 0.005f + t.ButtonHeight;
            float stripH = strip ? GuideStripH : 0f;
            float h = t.HeaderHeight + 2f * t.Padding + stripH + tourH;
            var p = UiPanel.Create<UiPanel>(GuideId, "TOUR", GuideW, h, _root);
            p.BaseScale = Vector3.one * UiScaleNow;
            var g = new GuideCard { Panel = p, HasStrip = strip, StripH = stripH };
            var c = p.Content;
            g.Content = c;
            g.ContentTop = c.y;
            g.TourTop = c.y + stripH;
            foreach (var e in p.Elements) if (e is UiButton cb && cb.Text == "✕") g.Close = cb;
            if (strip)
            {
                float y0 = c.y;
                // row 1: the spinner (twelve dots on a circle, a bright head runs round), the step's words, "STEP 3 OF 5"
                const float spinR = 0.0078f, dot = 0.0034f;
                var c0 = new Vector2(c.x + 0.010f, y0 + 0.010f);
                for (int i = 0; i < g.Spin.Length; i++)
                {
                    float a = (90f - i * 30f) * Mathf.Deg2Rad;
                    var s = c0 + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * spinR;
                    g.Spin[i] = p.Shapes.Add(p.Shape(UiShapeKind.Dot, new Rect(s.x - dot * 0.5f, s.y - dot * 0.5f, dot, dot), t.AccentSoft, 0f, 0f, 0.0015f, 0.6f));
                }
                g.Words = p.Add(new UiLabel("", 0.0138f) { Rect = new Rect(c.x + 0.024f, y0, c.width - 0.024f, 0.020f), Color = t.Text });
                g.StepNo = p.Add(new UiLabel("", 0.0105f) { Rect = new Rect(c.xMax - 0.085f, y0, 0.085f, 0.020f), UseDisplay = true, Color = t.TextAccent, Align = TextAlignmentOptions.MidlineRight });
                // row 2: the bar over the five steps and "62 %"
                g.Bar = p.Add(new UiProgress(0f) { Rect = new Rect(c.x, y0 + 0.0275f, c.width - 0.052f, 0.007f) });
                g.Pct = p.Add(new UiLabel("", 0.0105f) { Rect = new Rect(c.xMax - 0.046f, y0 + 0.024f, 0.046f, 0.014f), Color = t.TextAccent, Align = TextAlignmentOptions.MidlineRight });
                // row 3: what this is (two lines at most: a failed step's "what still works" goes here)
                g.Note = p.Add(new UiLabel(SetupNote, 0.0115f) { Rect = new Rect(c.x, y0 + 0.042f, c.width, 0.022f), Wrap = true, Color = t.TextDim });
                g.Divider = p.Add(new UiDivider { Rect = new Rect(c.x, y0 + 0.067f, c.width, 0.003f) });
            }
            TourRects(c, g.TourTop, t, out var rText, out var rHint, out var rDots, out var rNext, out var rSkip);
            g.Text = p.Add(new UiLabel("", 0.0128f) { Rect = rText, Wrap = true, Color = t.Text, Align = TextAlignmentOptions.TopLeft });
            g.Hint = p.Add(new UiLabel("", t.SmallSize) { Rect = rHint, Color = t.TextAccent });
            g.Dots = p.Add(new UiLabel("", 0.0142f) { Rect = rDots, Color = t.TextAccent });
            g.Next = p.Add(new UiButton("SKIP STEP", () => TourNextPressed()) { Rect = rNext });
            g.Skip = p.Add(new UiButton("SKIP TOUR", () => EndTour("skipped")) { Rect = rSkip });
            // while the setup runs the card cannot be closed (no ✕); afterwards it can, and its ✕ ends the tour
            p.Closable = !strip;
            if (strip && g.Close != null) g.Close.SetVisible(false);
            p.Closed += OnGuideClosed;
            Router.Register(p);
            p.gameObject.SetActive(false);
            return g;
        }

        private void SetCardClosable(GuideCard g, bool on)
        {
            g.Panel.Closable = on;
            if (g.Close != null) g.Close.SetVisible(on);
        }

        private void OnGuideClosed(UiPanel p)
        {
            if (_guide == null || _guide.Panel != p) return; // an old card
            if (!_tourEnding && _tourStep >= 0) EndTour("closed");
            _stripAlive = false;
            _foldPhase = 0;
            if (!_setupActive) _setupFinished = false;
        }

        private void DisposeGuideCard(GuideCard g)
        {
            if (g == null || g.Panel == null) return;
            if (_tourLit == g.Panel) _tourLit = null;
            Router?.Unregister(g.Panel);
            g.Panel.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(g.Panel.gameObject); else DestroyImmediate(g.Panel.gameObject);
            g.Panel = null;
        }

        /// Self-tests: the card and everything of the first run's display goes (the flag and the steps stay as they are).
        private void DropGuideStateForTest()
        {
            _tourStep = -1;
            _fadePhase = 0;
            _moveActive = false;
            _tourStepDone = false;
            _tourEnding = false;
            TourHighlight(null);
            if (_tourRing != null) _tourRing.gameObject.SetActive(false);
            DisposeGuideCard(_guide);
            _guide = null;
            ResetStrip();
            _setupFinished = false;
        }

        /// Moves the tour section (text, hint, dots, buttons) to start at `top` in the panel: while the strip folds away.
        private void LayoutTour(GuideCard g, float top)
        {
            if (Mathf.Abs(top - g.TourTop) < 0.0002f) return;
            g.TourTop = top;
            TourRects(g.Content, top, UiTheme.Current, out var rText, out var rHint, out var rDots, out var rNext, out var rSkip);
            g.Text.SetRect(rText); g.Hint.SetRect(rHint); g.Dots.SetRect(rDots); g.Next.SetRect(rNext); g.Skip.SetRect(rSkip);
        }

        /// The strip is gone: the tour goes on in a card as tall as it needs (no empty area). The same words, buttons,
        /// top edge and facing; the card pings so the change reads as meant.
        private void CompactGuideCard()
        {
            var old = _guide;
            if (old == null || old.Panel == null || !old.HasStrip) return;
            var nu = BuildGuideCard(false);
            nu.Panel.BaseScale = old.Panel.BaseScale;
            nu.Text.Set(old.Text.Text, old.Text.Label.Color);
            nu.Hint.Set(old.Hint.Text, old.Hint.Label.Color);
            nu.Dots.Set(old.Dots.Text);
            nu.Next.SetText(old.Next.Text);
            nu.Next.SetSelected(old.Next.Selected);
            nu.Next.SetVisible(old.Next.Visible);
            nu.Skip.SetText(old.Skip.Text);
            nu.Skip.SetVisible(old.Skip.Visible);
            nu.Panel.SetTitle(old.Panel.Title);
            var tr = old.Panel.transform;
            float s = old.Panel.RestLossyScale.y;
            nu.Panel.transform.SetPositionAndRotation(tr.position + tr.up * ((old.Panel.Height - nu.Panel.Height) * 0.5f * s), tr.rotation);
            if (_tourLit == old.Panel) _tourLit = nu.Panel;
            DisposeGuideCard(old);
            _guide = nu;
            nu.Panel.gameObject.SetActive(true); // at once: no opening, it is the same card
            nu.Panel.Ping();
        }

        // ------------------------------------------------------------------ the script

        private bool WhispBeatOn => Settings.orb && Orb != null;
        private int TourShownTotal() => WhispBeatOn ? TourSteps : TourSteps - 1;
        private int TourShownNo(int beat) => beat + 1 - (!WhispBeatOn && beat > 2 ? 1 : 0);

        private static string BeatTitle(int beat)
        {
            switch (beat)
            {
                case 0: return "HELLO";
                case 1: return "THE MAIN MENU";
                case 2: return "THE WHISP";
                case 3: return "WINDOWS";
                case 4: return "CONTROL";
                default: return "YOUR TURN";
            }
        }

        /// Which of the Linux (1), MQH (2) and the voice (4) are still locked: the text is made again when this changes.
        private int LockBits() => (SetupLocked(3) ? 1 : 0) | (SetupLocked(4) ? 2 : 0) | (SetupLocked(5) ? 4 : 0);

        private string BeatText(int beat)
        {
            bool wrist = Settings.wristMenu;
            switch (beat)
            {
                case 0:
                    return _tourFirst
                        ? "Hi, I'm the Whisp, your guide! While I set things up for you, about 20 seconds and only this once, let me show you around."
                        : "Hi again! Here are the basics, in about a minute.";
                case 1:
                {
                    string menu = wrist
                        ? "Your main menu lives on your left wrist. Look at the back of your wrist like a watch, then touch it with your right index finger."
                        : "This row of tiles is your main menu, the DOCK. Tap CLIP: point at it and pinch.";
                    return LockBits() != 0 ? menu + " Tiles with 🔒 unlock by themselves in a few seconds." : menu;
                }
                case 2:
                    return "That light is me. Touch me with a fingertip, or point at me and pinch, to open my menu.";
                case 3:
                    return "Windows stay where you put them. Pinch the bar on top of this card, move it, and let go.";
                case 4:
                    return "Settings, voice, agents and the system all live in CONTROL. Open ⚙ CONTROL on " + (wrist ? "your wrist menu." : "the DOCK.");
                default:
                {
                    if (SetupLocked(5))
                        return "Almost done: TALK unlocks as soon as the strip above says so. Meanwhile, CONTROL > AGENTS > GET adds a coding agent (the key or sign-in is yours).";
                    string ready = "Everything is ready. Press TALK and say a command, like “open files”: I'll show you here what I heard. To add a coding agent, open CONTROL > AGENTS > GET (the key or sign-in is yours).";
                    return TourWakeLine.Length > 0 ? ready + " " + TourWakeLine : ready;
                }
            }
        }

        private string BeatHint(int beat)
        {
            switch (beat)
            {
                case 0: return "Pinch NEXT ▸ to begin";
                case 1: return Settings.wristMenu ? "Waiting for the wrist menu…" : "Waiting for CLIP…";
                case 2: return "Waiting for my menu…";
                case 3: return "Waiting for the card to move…";
                case 4: return "Waiting for CONTROL…";
                default: return SetupLocked(5) ? "Waiting for the setup…" : "Press TALK, then speak…";
            }
        }

        private string BeatDone(int beat)
        {
            switch (beat)
            {
                case 1: return Settings.wristMenu ? "✓ Nice! That's your main menu." : "✓ Nice! CLIP keeps your captures.";
                case 2: return "✓ That's my menu. I keep your messages there too.";
                case 3: return "✓ It stays right there.";
                case 4: return "✓ That's CONTROL. Close it with ✕ when you're done.";
                default: return "";
            }
        }

        /// "●  ●  ◉  ○  ○  ○": done, now, next (one beat less when the Whisp is off).
        private string BeatDots(int beat)
        {
            int total = TourShownTotal(), cur = TourShownNo(beat) - 1;
            var sb = new System.Text.StringBuilder(total * 3);
            for (int i = 0; i < total; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append(i < cur ? "●" : i == cur ? "◉" : "○");
            }
            return sb.ToString();
        }

        /// Has the user done what the beat asks? (HELLO and the finale have no action: NEXT / FINISH.)
        private bool TourStepMet()
        {
            switch (_tourStep)
            {
                case 1: return Settings.wristMenu ? Wrist != null && Wrist.Expanded : IsPanelOpen("clipboard");
                case 2: return OrbMenuOpen;
                case 3: return _tourDropped;
                case 4: return ControlRailPage != null;
                default: return false;
            }
        }

        /// The dock's tile with this label (found once per beat, not per frame).
        private UiButton DockTile(string label)
        {
            if (!_panels.TryGetValue("dock", out var dock) || dock == null) return null;
            foreach (var e in dock.Elements)
                if (e is UiButton b && b.Text != null && b.Text.EndsWith("\n" + label)) return b;
            return null;
        }

        private UiPanel PanelOrNull(string id) => _panels.TryGetValue(id, out var p) && p != null && p.IsOpen ? p : null;

        // ------------------------------------------------------------------ beats

        private void TourNextPressed()
        {
            if (_tourStep < 0 || _fadePhase != 0) return;
            float now = TourClock;
            if (_tourStep >= TourSteps - 1) { EndTour("finished"); return; }
            if (_tourStep == 0) Log($"tour: card 1 done in {now - _tourStepAt:F1} s");
            else Log($"tour: card {TourShownNo(_tourStep)} skipped");
            GoToBeat(_tourStep + 1, now);
        }

        /// The next beat comes after a fade: the text and the hint go out, change, come in.
        private void GoToBeat(int beat, float now)
        {
            if (beat == 2 && !WhispBeatOn) beat = 3; // the Whisp is off: nothing to show there
            if (beat >= TourSteps) { EndTour("finished"); return; }
            _fadeNext = beat;
            _fadeRefresh = false;
            _fadePhase = 1;
            _fadeAt = now;
        }

        /// Shows a beat: its title, text, hint, dots, buttons, target and place. `refresh`: the same beat with a new text
        /// (the finale when TALK unlocks): only the text and the hint.
        private void ApplyBeat(int beat, float now, bool refresh)
        {
            var g = _guide;
            var t = UiTheme.Current;
            if (!refresh)
            {
                _tourStep = beat;
                _tourStepAt = now;
                _tourStepDone = false;
                _tourDoneAt = -1f;
                _tourDropped = false;
                _tourAwaySince = -1f;
                _tourTile = null;
                if (!Settings.wristMenu)
                {
                    if (beat == 1) _tourTile = DockTile("CLIP");
                    else if (beat == 4) _tourTile = DockTile("CONTROL");
                    else if (beat == 5) _tourTile = DockTile("TALK");
                }
                _tourVoiceSeen = beat == 5 && VoiceRecent.Count > 0 ? VoiceRecent[0] : null;
                bool last = beat >= TourSteps - 1;
                g.Panel.SetTitle("TOUR · " + BeatTitle(beat));
                g.Dots.Set(BeatDots(beat));
                g.Next.SetText(beat == 0 ? "NEXT ▸" : last ? "FINISH ✓" : "SKIP STEP");
                g.Next.SetSelected(last); // FINISH stands out
                g.Next.SetVisible(true);
                g.Skip.SetVisible(!last);
                TourHighlight(beat == 3 ? g.Panel : null); // WINDOWS: the card's own frame breathes
            }
            _beatLocks = LockBits();
            _textColor = t.Text;
            _hintColor = t.TextAccent;
            g.Text.Set(BeatText(beat), _textColor);
            g.Hint.Set(BeatHint(beat), _hintColor);
        }

        /// The beat text and hint fade (label alpha only; set during a fade, never otherwise).
        private void SetBeatAlpha(float a)
        {
            var g = _guide;
            if (g == null || g.Panel == null) return;
            var c = _textColor; c.a = a; g.Text.Label.Color = c;
            c = _hintColor; c.a = a; g.Hint.Label.Color = c;
        }

        private void SetHint(string text, Color color)
        {
            _hintColor = color;
            _guide.Hint.Set(text, color);
        }

        private void TickFade(float now)
        {
            if (_fadePhase == 1)
            {
                float u = (now - _fadeAt) / FadeOutSeconds;
                if (u < 1f) { SetBeatAlpha(1f - u); return; }
                // out: the beat changes under the fade
                ApplyBeat(_fadeNext, now, _fadeRefresh);
                if (!_fadeRefresh)
                {
                    PlaceGuide(_fadeNext, now, GuideWhy.Beat);
                    _guide.Panel.Ping();
                }
                SetBeatAlpha(0f);
                _fadePhase = 2;
                _fadeAt = now;
                return;
            }
            float v = (now - _fadeAt) / FadeInSeconds;
            if (v < 1f) { SetBeatAlpha(v); return; }
            SetBeatAlpha(1f);
            _fadePhase = 0;
        }

        // ------------------------------------------------------------------ every frame

        private void TickTour(float now, float dt)
        {
            var g = _guide;
            if (g == null || g.Panel == null) { _tourStep = -1; return; } // rebuilt away
            if (_fadePhase != 0) TickFade(now);
            bool still = _fadePhase == 0;
            if (still)
            {
                if (_tourStep == 0)
                {
                    if (now - _tourStepAt >= HelloAutoSeconds)
                    {
                        Log($"tour: card 1 went on by itself after {HelloAutoSeconds:F0} s");
                        GoToBeat(1, now);
                        still = false;
                    }
                }
                else if (!_tourStepDone && _tourStep < TourSteps - 1 && TourStepMet())
                {
                    _tourStepDone = true;
                    _tourDoneAt = now;
                    SetHint(BeatDone(_tourStep), UiTheme.Current.Ok);
                    Sounds?.Play("ping");
                    Log($"tour: card {TourShownNo(_tourStep)} done in {now - _tourStepAt:F1} s");
                }
                else if (_tourStepDone && now - _tourDoneAt >= TourDonePause)
                {
                    GoToBeat(_tourStep + 1, now);
                    still = false;
                }
            }
            if (still) TickTourFollow(now);
            TickGuideMove(now);
            TickTourRing(now, still && !_tourStepDone);
            TickTourPulse(now);
            TickGuideKeepClear(now);
        }

        /// What follows the setup and the voice while a beat waits: the text is made again when a lock lifts; the finale
        /// says what voice heard.
        private void TickTourFollow(float now)
        {
            int bits = LockBits();
            if (bits != _beatLocks)
            {
                if (_tourStep == 1) { _beatLocks = bits; _guide.Text.Set(BeatText(1), _textColor); }
                else if (_tourStep == TourSteps - 1 && ((bits ^ _beatLocks) & 4) != 0)
                {
                    // TALK unlocked (or locked): the other text, with a crossfade
                    _fadeNext = _tourStep;
                    _fadeRefresh = true;
                    _fadePhase = 1;
                    _fadeAt = now;
                    return;
                }
                else _beatLocks = bits;
            }
            if (_tourStep != TourSteps - 1 || SetupLocked(5)) return;
            var latest = VoiceRecent.Count > 0 ? VoiceRecent[0] : null;
            if (ReferenceEquals(latest, _tourVoiceSeen)) return;
            _tourVoiceSeen = latest;
            if (latest != null) ShowHeard(latest);
        }

        /// The finale's hint says what voice heard, once per result.
        private void ShowHeard(VoiceResult r)
        {
            var t = UiTheme.Current;
            string said = (r.Transcript ?? "").Trim();
            if (said.Length == 0) SetHint("I didn't catch that. Press TALK and try again.", t.Warn);
            else if (r.Ok) SetHint("I heard “" + ShortText(said, 50) + "” ✓", t.Ok);
            else SetHint("I heard “" + ShortText(said, 20) + "”, but that's not a command I know. Try again?", t.Warn);
        }

        private void OnTourPanelDropped(UiPanel p)
        {
            if (p == null) return;
            if (_guide != null && p == _guide.Panel) { _tourMoved = true; _moveActive = false; } // the user's place stays
            if (_tourStep != 3 || p == _tourRing || (p.Id ?? "").StartsWith("orb-")) return;
            _tourDropped = true;
        }

        // ------------------------------------------------------------------ where the card sits

        private enum GuideWhy { First, Beat, Away, Cover }

        /// What a panel (or a disc) covers of the user's view: yaw and pitch in degrees (yaw from the way the user faces).
        private struct GuideBox { public float Y0, Y1, P0, P1; }

        private readonly GuideBox[] _gobs = new GuideBox[48];
        private int _gobsN;
        // where the card may sit instead, in card widths sideways and in 7 cm steps up (down for the Whisp's beat)
        private static readonly Vector2[] GuideSlots =
        {
            new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 2f), new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(1f, 1f),
            new Vector2(-1f, 1f), new Vector2(0f, 3f), new Vector2(2f, 1f), new Vector2(-2f, 1f), new Vector2(0f, -1f),
        };

        private static GuideBox NewBox() => new GuideBox { Y0 = float.MaxValue, Y1 = float.MinValue, P0 = float.MaxValue, P1 = float.MinValue };

        private static bool GrowBox(ref GuideBox b, Vector3 w, Vector3 eye, float refYaw)
        {
            var d = w - eye;
            if (d.sqrMagnitude < 0.01f) return false;
            float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            float yaw = Mathf.DeltaAngle(refYaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            float pitch = Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg;
            if (yaw < b.Y0) b.Y0 = yaw;
            if (yaw > b.Y1) b.Y1 = yaw;
            if (pitch < b.P0) b.P0 = pitch;
            if (pitch > b.P1) b.P1 = pitch;
            return true;
        }

        private void AddGuideObstacle(UiPanel q, Vector3 eye, float refYaw)
        {
            if (q == null || !q.IsOpen || _gobsN >= _gobs.Length) return;
            var b = NewBox();
            float w = q.Width, h = q.Height;
            bool ok = GrowBox(ref b, q.SurfacePoint(new Vector2(0f, 0f)), eye, refYaw);
            ok &= GrowBox(ref b, q.SurfacePoint(new Vector2(w, 0f)), eye, refYaw);
            ok &= GrowBox(ref b, q.SurfacePoint(new Vector2(0f, h)), eye, refYaw);
            ok &= GrowBox(ref b, q.SurfacePoint(new Vector2(w, h)), eye, refYaw);
            ok &= GrowBox(ref b, q.SurfacePoint(new Vector2(w * 0.5f, 0f)), eye, refYaw);
            ok &= GrowBox(ref b, q.SurfacePoint(new Vector2(w * 0.5f, h)), eye, refYaw);
            if (ok) _gobs[_gobsN++] = b;
        }

        private void AddGuideDisc(Vector3 centre, float radius, Vector3 eye, float refYaw)
        {
            if (_gobsN >= _gobs.Length) return;
            var d = centre - eye;
            float dist = d.magnitude;
            if (dist < 0.05f) return;
            float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            float yaw = Mathf.DeltaAngle(refYaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            float pitch = Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg;
            float rad = Mathf.Atan2(radius, dist) * Mathf.Rad2Deg;
            float radYaw = rad / Mathf.Max(0.3f, Mathf.Cos(pitch * Mathf.Deg2Rad));
            _gobs[_gobsN++] = new GuideBox { Y0 = yaw - radYaw, Y1 = yaw + radYaw, P0 = pitch - rad, P1 = pitch + rad };
        }

        /// What the card would cover of the view at this place.
        private static bool GuideCardBox(Vector3 pos, Vector3 eye, float refYaw, float halfW, float halfH, out GuideBox b)
        {
            b = NewBox();
            var look = pos - eye;
            if (look.sqrMagnitude < 1e-4f) return false;
            var rot = Quaternion.LookRotation(look.normalized, Vector3.up);
            Vector3 r = rot * Vector3.right * halfW, u = rot * Vector3.up * halfH;
            bool ok = GrowBox(ref b, pos - r + u, eye, refYaw);
            ok &= GrowBox(ref b, pos + r + u, eye, refYaw);
            ok &= GrowBox(ref b, pos - r - u, eye, refYaw);
            ok &= GrowBox(ref b, pos + r - u, eye, refYaw);
            return ok;
        }

        /// How many of the collected windows (and the Whisp) the card's view overlaps, with a margin in degrees.
        private int GuideOverlaps(GuideBox a, float margin)
        {
            int n = 0;
            for (int i = 0; i < _gobsN; i++)
            {
                var o = _gobs[i];
                if (a.Y0 < o.Y1 + margin && o.Y0 < a.Y1 + margin && a.P0 < o.P1 + margin && o.P0 < a.P1 + margin) n++;
            }
            return n;
        }

        /// Puts the card where the beat asks, clear of the DOCK, the Whisp and the open windows, with an eased move:
        ///   the DOCK's beats and HELLO, WINDOWS, YOUR TURN: above the DOCK (its bottom edge 5 cm above the DOCK's top edge in
        ///     world up, facing the user, 0.55-0.75 m away), else in front, a little below eye level;
        ///   THE WHISP: below it, 5 cm clear of its outer radius (its menu opens on its left, its messages on its right).
        /// First: at once. Beat: the user's own place stays unless it covers something now. Away: the user turned away.
        /// Cover: a window opened on the card (checked every 0.6 s).
        private void PlaceGuide(int beat, float now, GuideWhy why)
        {
            var g = _guide;
            if (g == null || g.Panel == null) return;
            var head = Head;
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            Vector3 hf = head != null ? head.forward : Vector3.forward;
            Vector3 fwd = hf;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float refYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            var tr = g.Panel.transform;
            var sc = g.Panel.RestLossyScale;
            float halfW = g.Panel.Width * 0.5f * sc.x, halfH = g.Panel.Height * 0.5f * sc.y;

            // what the card has to stay clear of: the open windows (the DOCK among them) and the Whisp
            _gobsN = 0;
            foreach (var q in _panels.Values) AddGuideObstacle(q, eye, refYaw);
            foreach (var q in _frames.Values) AddGuideObstacle(q, eye, refYaw);
            if (OrbActive) AddGuideDisc(Orb.Position, OrbOuterRadius * 1.35f + 0.03f, eye, refYaw);

            if (why == GuideWhy.Beat || why == GuideWhy.Cover)
            {
                bool covered = GuideCardBox(tr.position, eye, refYaw, halfW, halfH, out var cur) && GuideOverlaps(cur, GuideMargin) > 0;
                if (why == GuideWhy.Cover && !covered) return;
                if (why == GuideWhy.Beat && _tourMoved && !covered) return; // the place the user chose stays
            }

            Vector3 pos;
            bool whisp = beat == 2 && OrbActive;
            var dock = PanelOrNull("dock");
            bool dockInView = dock != null && !Settings.wristMenu && head != null && Vector3.Angle(hf, dock.transform.position - eye) < 55f;
            if (whisp)
            {
                pos = Orb.Position - Vector3.up * (OrbOuterRadius + 0.05f + halfH);
                var to = pos - eye;
                float d = to.magnitude;
                if (d > 1e-3f && d < 0.45f) pos = eye + to / d * 0.45f;
            }
            else if (dockInView)
            {
                var a = dock.SurfacePoint(new Vector2(0f, 0f));
                var b = dock.SurfacePoint(new Vector2(dock.Width, 0f));
                var m = dock.SurfacePoint(new Vector2(dock.Width * 0.5f, 0f));
                float minY = Mathf.Max(a.y, Mathf.Max(b.y, m.y)) + 0.05f + halfH; // the bottom edge 5 cm over the DOCK's top edge
                pos = new Vector3(m.x, minY, m.z);
                var to = pos - eye;
                float d = to.magnitude;
                if (d > 1e-3f) pos = eye + to / d * Mathf.Clamp(d, 0.55f, 0.75f);
                pos.y = Mathf.Max(pos.y, minY);
            }
            else
            {
                bool wristBeat = Settings.wristMenu && (beat == 1 || beat == 4);
                pos = eye + fwd * 0.6f - Vector3.up * 0.08f + right * (wristBeat ? 0.12f : 0.03f); // the wrist menu comes up lower left
                var to = pos - eye;
                float d = to.magnitude;
                if (d > 1e-3f && (d < 0.45f || d > 0.9f)) pos = eye + to / d * Mathf.Clamp(d, 0.45f, 0.9f);
            }

            // the first of its places that covers nothing
            float rise = whisp ? -0.07f : 0.07f;
            Vector3 best = pos;
            int bestN = int.MaxValue;
            for (int i = 0; i < GuideSlots.Length; i++)
            {
                var cand = pos + right * (GuideSlots[i].x * (2f * halfW + 0.04f)) + Vector3.up * (GuideSlots[i].y * rise);
                if (!GuideCardBox(cand, eye, refYaw, halfW, halfH, out var box)) continue;
                float yawMid = (box.Y0 + box.Y1) * 0.5f, pitchMid = (box.P0 + box.P1) * 0.5f;
                if (i > 0 && (Mathf.Abs(yawMid) > 75f || pitchMid > 55f || pitchMid < -50f)) continue; // out of the user's view
                int n = GuideOverlaps(box, GuideMargin);
                if (n < bestN) { bestN = n; best = cand; }
                if (n == 0) break;
            }
            pos = best;
            var look = pos - eye;
            var rot = look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : Quaternion.LookRotation(fwd, Vector3.up);
            MoveGuideTo(pos, rot, now, why == GuideWhy.First);
        }

        /// The card goes there over 0.35 s (or at once: the first time, or when it is not shown).
        private void MoveGuideTo(Vector3 pos, Quaternion rot, float now, bool snap)
        {
            var g = _guide;
            if (g == null || g.Panel == null) return;
            var tr = g.Panel.transform;
            if (snap || !g.Panel.IsOpen) { tr.SetPositionAndRotation(pos, rot); _moveActive = false; return; }
            if ((tr.position - pos).sqrMagnitude < 0.0004f && Quaternion.Angle(tr.rotation, rot) < 2f) { _moveActive = false; return; } // under 2 cm: it is there
            _moveFromPos = tr.position; _moveFromRot = tr.rotation;
            _moveToPos = pos; _moveToRot = rot;
            _moveT0 = now;
            _moveActive = true;
        }

        private void TickGuideMove(float now)
        {
            if (!_moveActive) return;
            var g = _guide;
            if (g == null || g.Panel == null || Router.IsDragging(g.Panel)) { _moveActive = false; return; } // the user took it
            float u = Mathf.Clamp01((now - _moveT0) / GuideMoveSeconds);
            float e = u * u * (3f - 2f * u);
            g.Panel.transform.SetPositionAndRotation(Vector3.Lerp(_moveFromPos, _moveToPos, e), Quaternion.Slerp(_moveFromRot, _moveToRot, e));
            if (u >= 1f) _moveActive = false;
        }

        /// The user turned away for 2 s: the card comes back to where they look. A window opened on it: it moves aside.
        private void TickGuideKeepClear(float now)
        {
            var g = _guide;
            if (g == null || g.Panel == null || !g.Panel.IsOpen || _fadePhase != 0 || Router.IsDragging(g.Panel)) return;
            var head = Head;
            if (head != null && !_moveActive)
            {
                bool away = Vector3.Angle(head.forward, g.Panel.transform.position - head.position) > 55f;
                if (!away) _tourAwaySince = -1f;
                else if (_tourAwaySince < 0f) _tourAwaySince = now;
                else if (now - _tourAwaySince > 2f) { _tourAwaySince = -1f; PlaceGuide(_tourStep, now, GuideWhy.Away); return; }
            }
            if (now < _guideCheckAt || _moveActive) return;
            _guideCheckAt = now + 0.6f;
            PlaceGuide(_tourStep, now, GuideWhy.Cover);
        }

        // ------------------------------------------------------------------ the ring and the lit window

        /// Where the ring goes this frame (false: no ring): round the dock's tile (half its diagonal and 1 cm), the left
        /// wrist, the Whisp. `lift`: how far towards the eye it sits off its target (so it is never in the DOCK's plane).
        private bool TourRingTarget(out Vector3 at, out float radius, out float lift)
        {
            at = default;
            radius = 0.06f;
            lift = 0f;
            switch (_tourStep)
            {
                case 1:
                case 4:
                case 5:
                    if (_tourStep == 5 && SetupLocked(5)) return false; // TALK is not asked for while it is locked
                    if (_tourTile != null && _tourTile.Panel != null && _tourTile.Panel.IsOpen)
                    {
                        var r = _tourTile.Rect;
                        at = _tourTile.Panel.SurfacePoint(r.center);
                        radius = 0.5f * Mathf.Sqrt(r.width * r.width + r.height * r.height) * _tourTile.Panel.RestLossyScale.x + 0.01f;
                        lift = 0.015f;
                        return true;
                    }
                    if (_tourStep == 5 || !Settings.wristMenu || Wrist == null || Wrist.Expanded) return false;
                    var st = Gestures?.Get(Handedness.Left);
                    if (st == null || !st.Tracked) return false;
                    at = st.Frame.Wrist;
                    lift = 0.05f; // over the wrist, towards the eyes
                    radius = 0.06f;
                    return true;
                case 2:
                    if (!OrbActive || OrbMenuOpen) return false;
                    at = Orb.Position;
                    radius = OrbOuterRadius * 1.35f;
                    return true;
                default:
                    return false;
            }
        }

        private void TickTourRing(float now, bool want)
        {
            bool show = TourRingTarget(out var at, out float radius, out float lift) && want;
            if (!show) { if (_tourRing != null && _tourRing.gameObject.activeSelf) _tourRing.gameObject.SetActive(false); return; }
            EnsureTourRing();
            if (!_tourRing.gameObject.activeSelf) { _tourRing.gameObject.SetActive(true); _ringHead = -1; }
            Vector3 eye = Head != null ? Head.position : at - Vector3.forward;
            if (lift > 0f)
            {
                var toEye = eye - at;
                float d = toEye.magnitude;
                if (d > 1e-4f) at += toEye / d * lift;
            }
            // a slow breathe of the ring's scale (+-5 %)
            _tourRing.BaseScale = Vector3.one * (radius / TourRingR * (1f + 0.05f * Mathf.Sin(now * 2.4f)));
            var look = at - eye;
            _tourRing.transform.SetPositionAndRotation(at, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : _tourRing.transform.rotation);
            // a bright arc runs round it, 12 steps a second (colours only, restyled in place)
            int head = (int)(now * 12f) % _ringDots.Length;
            if (head == _ringHead) return;
            _ringHead = head;
            var bright = RingBright();
            var dim = new Color(bright.r * 0.5f, bright.g * 0.5f, bright.b * 0.5f, 1f);
            for (int i = 0; i < _ringDots.Length; i++)
            {
                int k = (head - i + _ringDots.Length) % _ringDots.Length;
                float a = k < 5 ? 1f - k / 5f : 0f;
                _tourRing.Shapes.SetTint(_ringDots[i], Color.Lerp(dim, bright, a));
            }
        }

        /// Warm and bright: it must not look like the DOCK's own cyan.
        private static Color RingBright() => Color.Lerp(UiTheme.Current.Warn, Color.white, 0.35f);

        /// The ring: twelve dots of 9 mm on a circle, no disc. It is put 1.5 cm in front of its target (towards the eye) and
        /// faces the eye, so it is never in the plane of the DOCK, whose shader writes depth: the dots lost that test there.
        private void EnsureTourRing()
        {
            if (_tourRing != null) return;
            const float dot = 0.009f, size = 2f * (TourRingR + 0.02f);
            var p = UiPanel.Create<UiPanel>("tour-ring", "", size, size, _root, header: false, frameless: true);
            p.Closable = false;
            p.Draggable = false; // never registered with the router: it takes no input
            var bright = RingBright();
            for (int i = 0; i < _ringDots.Length; i++)
            {
                float a = (90f - i * 360f / _ringDots.Length) * Mathf.Deg2Rad;
                var c = new Vector2(size * 0.5f + Mathf.Cos(a) * TourRingR, size * 0.5f - Mathf.Sin(a) * TourRingR);
                _ringDots[i] = p.Shapes.Add(p.Shape(UiShapeKind.Dot, new Rect(c.x - dot * 0.5f, c.y - dot * 0.5f, dot, dot), bright, 0f, 0f, 0.004f, 0.8f));
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
