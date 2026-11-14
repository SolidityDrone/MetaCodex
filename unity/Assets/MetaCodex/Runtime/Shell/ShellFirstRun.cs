using System;
using System.IO;
using MetaCodex.AI;
using MetaCodex.Linux;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// FIRST START · ONE-TIME SETUP (owner 2026-11-14, docs/ready-on-install.md "First start"). The APK carries the AI
    /// models, the AI runtime, the Linux userland and MQH; the very first start lays them out (about a minute, measured on
    /// the Quest 3S: models 0.9 s, AI service 2 s, Linux 9.4 s, MQH 4 s, the voice's decision model 30-40 s). Meanwhile a
    /// panel in front of the user (placed like the other windows, PlaceSmart) shows the five steps one after another: the
    /// step on show with its name, a line on what it does, its own bar (empty at each new step) and a spinner that keeps
    /// turning; the steps done stay listed above with ✓ and their time. Steps 1-2 usually end while 3 runs: the panel still
    /// shows them one at a time, each at least SetupMinShow s, so the user reads a clean sequence. A failed step says what
    /// failed and what still works, and the setup goes on with the steps that do not need it. At the end: "Setup done in
    /// N s" and what is unlocked; the panel closes itself. Locks (ShellSetupLocks) and the tour (ShellTour) run beside it.
    /// Only on the very first start (no Linux installed yet, Settings.firstStartDone off); the flag is set at the end.
    public sealed partial class MetaCodexShell
    {
        public enum SetupState { Waiting, Running, Done, Failed, Skipped }

        /// One step of the first start: what the panel says about it and what the app reads of it.
        public sealed class SetupStep
        {
            public int Number;
            /// While it runs, once it is done, one line on what it does, what still works when it fails, what it unlocks, its
            /// short name.
            public string Doing, DoneName, Description, StillWorks, Unlocks, Label;
            /// The step this one cannot run without (0: none): when that one fails, this one is skipped.
            public int Needs;
            /// Seconds it usually takes, for steps with no measure of their own (their bar goes by time, at most 90 %).
            public float Typical;
            public SetupState State;
            /// 0..1, as the app measures it (the panel's bar eases towards it).
            public float Progress;
            /// Under the bar ("1,216 of 1,916 examples"), the error of a failed step, why a step was skipped.
            public string Note;
            public float StartedAt = -1f, EndedAt = -1f;
            public bool Resolved => State == SetupState.Done || State == SetupState.Failed || State == SetupState.Skipped;
            public float Seconds => StartedAt >= 0f && EndedAt >= StartedAt ? EndedAt - StartedAt : 0f;
        }

        public const int SetupSteps = 5;
        /// Each step is on show at least this long (a step done long ago fills its bar and gets its ✓ in this time).
        public const float SetupMinShow = 0.6f;
        /// A failed step stays on show this long: what failed and what still works can be read.
        public const float SetupFailedShow = 4f;
        /// The panel closes itself this long after "Setup done" (longer when a step failed, and then it can be closed).
        public const float SetupCloseAfter = 6f, SetupCloseAfterFailed = 20f;
        /// The voice's decision model is given this long after the AI service is up before its step is called failed.
        public const float SetupDeciderTimeout = 240f;

        public const string SetupTitle = "FIRST START · ONE-TIME SETUP";
        public const string SetupIntro = "This happens once, on the very first start, and takes about a minute. Nothing is downloaded: everything comes out of the app and stays on this headset.";

        /// The five steps, in their order (texts: English, plain; the owner asked for an inviting line per step).
        public static SetupStep[] NewSetupSteps() => new[]
        {
            new SetupStep
            {
                Number = 1, Label = "AI models", Doing = "Unpacking the AI models", DoneName = "AI models in place",
                Description = "Speech, language and voice models (about 650 MB) come out of the app onto this headset: no download.",
                StillWorks = "The voice models download the first time voice is used (CONTROL > VOICE > INSTALL); everything else works.",
                Unlocks = "", Typical = 8f,
            },
            new SetupStep
            {
                Number = 2, Label = "AI service", Needs = 1, Doing = "Starting the local AI service", DoneName = "Local AI running",
                Description = "Speech-to-text, the language model and the voice run here on the headset, on cores of their own.",
                StillWorks = "Voice is off for now (CONTROL > VOICE > START tries again); Linux, terminals, threads and agents work.",
                Unlocks = "", Typical = 3f,
            },
            new SetupStep
            {
                Number = 3, Label = "Linux", Doing = "Building your Linux workstation", DoneName = "Linux ready",
                Description = "Debian with Node.js, git and Python, unpacked from the app: no download.",
                StillWorks = "Terminals, files, MQH and agents need the Linux; WEB, CLIP and voice still work. CONTROL > SYSTEM says why.",
                Unlocks = "TERMINALS and FILES", Typical = 12f,
            },
            new SetupStep
            {
                Number = 4, Label = "MQH", Needs = 3, Doing = "Starting MQH, the agent harness", DoneName = "MQH running",
                Description = "The harness that runs your coding agents, their threads and their tools, inside your Linux.",
                StillWorks = "Threads and agents need MQH; terminals, files, WEB and voice still work. CONTROL > MQH > STATUS > RESTART tries again.",
                Unlocks = "THREADS and AGENTS", Typical = 4.5f,
            },
            new SetupStep
            {
                Number = 5, Label = "Voice commands", Needs = 2, Doing = "Teaching the voice your commands", DoneName = "Voice commands ready",
                Description = "About 1,900 ways to say the app's commands become examples TALK can match, worked out here on the headset.",
                StillWorks = "TALK still works through the language model (slower); CONTROL > VOICE shows the decision model.",
                Unlocks = "TALK", Typical = 45f,
            },
        };

        private SetupStep[] _setup;
        /// The first start's setup is going on (locks apply) or its panel still shows the end.
        private bool _setupActive, _setupFinished, _setupFake;
        private float _setupT0 = -1f, _setupEndAt = -1f;
        private bool _setupAllResolvedLogged;

        /// The first start's one-time setup is running: the SETUP panel, the locks (ShellSetupLocks), the tour at its side.
        public bool SetupActive => _setupActive;
        /// The steps as the app reads them (the panel shows them one at a time).
        public System.Collections.Generic.IReadOnlyList<SetupStep> SetupStepsNow => _setup;

        /// From Build, before the AI service starts (InitVoice): is this the very first start? Then the setup begins now.
        private void InitFirstRun()
        {
            _setup = NewSetupSteps();
            if (Settings.firstStartDone) return;
            if (Application.platform != RuntimePlatform.Android || LinuxInstalledBefore())
            {
                // an update of an installed app, the Editor, self-tests: never the first start, and never again
                Settings.firstStartDone = true;
                Settings.Save(_settingsPath);
                return;
            }
            BeginSetup();
        }

        private void BeginSetup()
        {
            _setupActive = true;
            _setupFinished = false;
            _setupT0 = Time.unscaledTime;
            _setupShown = 0;
            _setupFill = 0f;
            _setupShownSince = -1f;
            _setupEndAt = -1f;
            _setupAllResolvedLogged = false;
            // the tour shows the orb: its one-time "this is the orb" message would only repeat it
            Settings.orbTipShown = true;
            Log("setup: first start, one-time setup (the SETUP panel, the locks, the tour)");
        }

        /// No Linux was installed before: the marker LinuxHost writes after its first install (files/linux/installed.json).
        /// Read here because the shell is built before LinuxHost resolves its paths.
        private static bool LinuxInstalledBefore()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                    return File.Exists(Path.Combine(files.Call<string>("getAbsolutePath"), "linux", "installed.json"));
            }
            catch (Exception) { return true; } // unknown: no setup panel rather than one that waits for nothing
#else
            return true;
#endif
        }

        // ------------------------------------------------------------------ what the app reads, every frame of the setup

        private void ReadSetupSteps(float now)
        {
            if (_setupFake) return; // self-tests set the steps themselves
            var q = Qvac;
            var host = app != null ? app.Host : null;
            bool voiceOn = Settings.voice.enabled && q != null;

            // 1 the models (the AI runtime is unpacked first: the first half of the bar)
            var s = _setup[0];
            if (!voiceOn) Put(s, SetupState.Skipped, 1f, "voice is off in CONTROL > VOICE", now);
            else if (q.ModelsSeeded) Put(s, SetupState.Done, 1f, null, now);
            else if (q.State == QvacHost.HostState.Failed || q.State == QvacHost.HostState.NotBundled) Put(s, SetupState.Failed, s.Progress, q.Status, now);
            else if (q.State == QvacHost.HostState.Preparing)
                Put(s, SetupState.Running, q.ModelsProgress >= 0f ? 0.5f + 0.5f * q.ModelsProgress : 0.5f * Mathf.Clamp01(q.Progress), null, now);
            else Put(s, SetupState.Waiting, 0f, null, now);

            // 2 the local AI service (no measure of its own: by time)
            s = _setup[1];
            if (!SetupBlocked(s, now))
            {
                if (q.IsRunning) Put(s, SetupState.Done, 1f, null, now);
                else if (q.State == QvacHost.HostState.Failed || q.State == QvacHost.HostState.NotBundled
                         || q.State == QvacHost.HostState.Stopped && !q.IsBusyStarting) Put(s, SetupState.Failed, s.Progress, q.Status, now);
                else if (q.ModelsSeeded) Put(s, SetupState.Running, ByTime(s, now), null, now);
                else Put(s, SetupState.Waiting, 0f, null, now);
            }

            // 3 the Linux (its own progress: the copy out of the APK, the unpack, the overlay and MQH's bundle)
            s = _setup[2];
            if (host == null || host.State == LinuxHost.HostState.Unsupported) Put(s, SetupState.Skipped, 1f, "no Linux here", now);
            else if (host.State == LinuxHost.HostState.Ready) Put(s, SetupState.Done, 1f, null, now);
            else if (host.State == LinuxHost.HostState.Failed) Put(s, SetupState.Failed, s.Progress, host.Status, now);
            else if (host.State == LinuxHost.HostState.Installing) Put(s, SetupState.Running, Mathf.Clamp01(host.Progress), null, now);
            else Put(s, SetupState.Waiting, 0f, null, now);

            // 4 MQH (by time)
            s = _setup[3];
            var m = MqhHost;
            if (!SetupBlocked(s, now))
            {
                if (m == null || m.State == MqhHost.HostState.Off && !m.Manages) Put(s, SetupState.Skipped, 1f, "a developer MQH is set (MQH > STATUS)", now);
                else if (m.State == MqhHost.HostState.Running) Put(s, SetupState.Done, 1f, null, now);
                else if (m.State == MqhHost.HostState.Failed) Put(s, SetupState.Failed, s.Progress, m.Status, now);
                else if (m.State == MqhHost.HostState.Starting) Put(s, SetupState.Running, ByTime(s, now), null, now);
                else Put(s, SetupState.Waiting, 0f, null, now);
            }

            // 5 the voice's decision model (its examples embedded of the total; by time while it has none to count)
            s = _setup[4];
            if (!SetupBlocked(s, now))
            {
                var d = Decider;
                if (!Settings.voice.decide || d == null)
                {
                    if (_setup[1].State == SetupState.Done) Put(s, SetupState.Skipped, 1f, "voice commands go through the language model (CONTROL > VOICE)", now);
                    else Put(s, SetupState.Waiting, 0f, null, now);
                }
                else if (d.Ready) Put(s, SetupState.Done, 1f, null, now);
                else if (_setup[1].State == SetupState.Done)
                {
                    if (s.StartedAt >= 0f && now - s.StartedAt > SetupDeciderTimeout)
                        Put(s, SetupState.Failed, s.Progress, "the decision model is not ready after " + (int)(SetupDeciderTimeout / 60f) + " min: " + d.Status, now);
                    else if (d.Building && d.EmbedTotal > 0)
                        Put(s, SetupState.Running, 0.97f * d.EmbedDone / d.EmbedTotal, EmbedNote(d.EmbedDone, d.EmbedTotal), now);
                    else Put(s, SetupState.Running, ByTime(s, now), null, now);
                }
                else Put(s, SetupState.Waiting, 0f, null, now);
            }
        }

        private int _embedNoteDone = -1, _embedNoteTotal = -1;
        private string _embedNote;

        /// "1,216 of 1,916 examples", made only when the numbers change (a batch of 64 at a time).
        private string EmbedNote(int done, int total)
        {
            if (done != _embedNoteDone || total != _embedNoteTotal)
            {
                _embedNoteDone = done; _embedNoteTotal = total;
                _embedNote = done.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of " + total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " examples";
            }
            return _embedNote;
        }

        /// A step that needs a failed or skipped one is skipped (true: nothing else to read for it).
        private bool SetupBlocked(SetupStep s, float now)
        {
            if (s.Needs <= 0) return false;
            var dep = _setup[s.Needs - 1];
            if (dep.State != SetupState.Failed && dep.State != SetupState.Skipped) return false;
            Put(s, SetupState.Skipped, 1f, "needs step " + s.Needs, now);
            return true;
        }

        /// A bar by time for steps with no measure of their own: 90 % at their typical duration, never more.
        private static float ByTime(SetupStep s, float now) => s.StartedAt < 0f ? 0f : 0.9f * Mathf.Clamp01((now - s.StartedAt) / Mathf.Max(0.1f, s.Typical));

        /// The step's state now; a change is logged, and times its start and end. Done stays done (the decision model is
        /// rebuilt when a plugin adds a command: the first start is over by then).
        private void Put(SetupStep s, SetupState state, float progress, string note, float now)
        {
            if (s.State == SetupState.Done && state != SetupState.Done) return;
            s.Progress = Mathf.Max(state == SetupState.Running ? s.Progress : 0f, Mathf.Clamp01(progress));
            if (s.State == state) { if (state == SetupState.Running) s.Note = note; return; }
            var before = s.State;
            s.State = state;
            s.Note = note;
            if (state != SetupState.Waiting && s.StartedAt < 0f)
            {
                var dep = s.Needs > 0 ? _setup[s.Needs - 1] : null;
                if (dep != null && dep.EndedAt >= 0f) s.StartedAt = dep.EndedAt;
                else if (s.Number == 3) s.StartedAt = now; // the Linux starts installing after the shell is up
                else s.StartedAt = _setupT0 >= 0f ? _setupT0 : now;
            }
            if (s.Resolved) s.EndedAt = now;
            Log($"setup: step {s.Number} {before} -> {state}" + (s.Resolved && s.StartedAt >= 0f ? $" ({s.Seconds:F1} s)" : "") + (string.IsNullOrEmpty(note) ? "" : ": " + note));
        }

        private bool AllSetupResolved()
        {
            for (int i = 0; i < SetupSteps; i++) if (!_setup[i].Resolved) return false;
            return true;
        }

        private bool AnySetupFailed()
        {
            for (int i = 0; i < SetupSteps; i++) if (_setup[i].State == SetupState.Failed) return true;
            return false;
        }

        // ------------------------------------------------------------------ every frame

        /// Every frame (Update): reads the steps, lifts the locks as their steps end, runs the panel. Nothing to do (one
        /// test) once the setup is over and its panel closed, and on every later start.
        private void TickFirstRun(float dt)
        {
            if (!_setupActive && !_setupFinished && _tourStep < 0) return;
            TickFirstRunAt(Time.unscaledTime, dt);
        }

        /// TickFirstRun at a given time (self-tests move their own clock).
        private void TickFirstRunAt(float now, float dt)
        {
            if (_setupActive)
            {
                ReadSetupSteps(now);
                if (AllSetupResolved())
                {
                    _setupActive = false; // the locks go with it (each step lifted its own already)
                    _setupFinished = true;
                    if (!_setupAllResolvedLogged) { _setupAllResolvedLogged = true; Log($"setup: all steps over in {now - _setupT0:F1} s" + (AnySetupFailed() ? " (with a failed step)" : "")); }
                }
                TickSetupLocks();
            }
            if (_setupActive || _setupFinished) TickSetupPanel(now, dt);
            if (_tourStep >= 0) TickTour(now, dt);
        }

        // ------------------------------------------------------------------ the panel

        public const float SetupW = 0.60f, SetupH = 0.47f;
        public const string SetupPanelId = "setup";
        private UiPanel _setupPanel;
        private UiLabel _setupStepLabel, _setupName, _setupDesc, _setupPct, _setupLocksLabel;
        private readonly UiLabel[] _setupRows = new UiLabel[SetupSteps];
        private UiProgress _setupBar;
        private UiButton _setupTourBtn;
        private readonly int[] _spinDots = new int[12];
        private int _spinHead = -1;
        private bool _spinVisible = true;
        private int _setupShown;                 // the step on show (0..4); SetupSteps: the end is on show
        private float _setupFill, _setupShownSince = -1f;
        private int _setupPctShown = -1;
        private string _setupNoteShown;
        private readonly SetupState[] _rowState = new SetupState[SetupSteps];
        private int _rowShown = -1;
        private bool _setupPanelOpened, _setupClosedByEnd;

        /// The SETUP panel (self-tests; null before it shows).
        public UiPanel SetupPanel => _setupPanel;
        /// The step the panel shows now (1..5), 6 at the end.
        public int SetupShownStep => _setupShown + 1;

        private void BuildSetupPanel()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>(SetupPanelId, SetupTitle, SetupW, SetupH);
            p.Closable = false;
            p.HideCloseButton(); // it closes itself at the end (and can be closed then, if a step failed)
            p.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            var st = new UiStack(p.Content, t.Gap);
            p.Add(new UiLabel(SetupIntro, t.SmallSize) { Rect = st.Next(0.032f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            for (int i = 0; i < SetupSteps; i++)
                _setupRows[i] = p.Add(new UiLabel("", 0.0142f) { Rect = st.Next(0.024f) });
            p.Add(new UiDivider { Rect = st.Next(0.008f) });
            _setupStepLabel = p.Add(new UiLabel("", 0.0115f) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextAccent });
            var nameRow = st.Next(0.028f);
            // the spinner: twelve dots on a circle left of the step's name; a bright head runs round (12 steps a second)
            const float spinR = 0.0105f, dot = 0.0042f;
            var c0 = new Vector2(nameRow.x + 0.013f, nameRow.center.y);
            for (int i = 0; i < _spinDots.Length; i++)
            {
                float a = (90f - i * 30f) * Mathf.Deg2Rad;
                var c = c0 + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * spinR;
                _spinDots[i] = p.Shapes.Add(p.Shape(UiShapeKind.Dot, new Rect(c.x - dot * 0.5f, c.y - dot * 0.5f, dot, dot), t.AccentSoft, 0f, 0f, 0.002f, 0.6f));
            }
            _spinHead = -1;
            _spinVisible = true;
            _setupName = p.Add(new UiLabel("", 0.019f) { Rect = new Rect(nameRow.x + 0.034f, nameRow.y, nameRow.width - 0.034f, nameRow.height), Color = t.Text });
            _setupDesc = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.034f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _setupBar = p.Add(new UiProgress(0f) { Rect = st.Next(0.010f) });
            _setupPct = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.018f), Color = t.TextAccent, Align = TextAlignmentOptions.MidlineRight });
            p.Add(new UiDivider { Rect = st.Next(0.006f) });
            var foot = st.NextSplit(0.034f, 0.76f, out var tourRect);
            _setupLocksLabel = p.Add(new UiLabel("", 0.0112f) { Rect = foot, Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.MidlineLeft });
            _setupTourBtn = p.Add(new UiButton("TOUR ▸", () => StartTour()) { Rect = tourRect });
            _setupPanel = p;
            _rowShown = -1;
            _setupPctShown = -1;
            _setupNoteShown = null;
            for (int i = 0; i < SetupSteps; i++) _rowState[i] = (SetupState)(-1);
            RefreshSetupRows(true);
            ShowSetupStepText();
            RefreshSetupLocksLabel();
        }

        /// Shows the panel once the splash is gone and the head is tracked (the splash holds the first seconds), then runs it.
        private void TickSetupPanel(float now, float dt)
        {
            if (_setupClosedByEnd) return;
            if (_setupPanel == null)
            {
                if (_setupPanelOpened)
                {
                    // rebuilt with every panel (RebuildPanels: an accent change): made again where the user looks; the end
                    // was on show: it is not shown twice
                    if (_setupShown >= SetupSteps) { _setupClosedByEnd = true; _setupFinished = false; return; }
                    BuildSetupPanel();
                    PlaceSmart(SetupPanelId, _setupPanel);
                    _setupPanel.Open();
                    return;
                }
                if (_splash != null) return;
                var head = Head;
                if (Application.isPlaying && (head == null || head.position.sqrMagnitude < 1e-4f)) return;
                BuildSetupPanel();
                PlaceSmart(SetupPanelId, _setupPanel);
                _placed.Add(SetupPanelId);
                _setupPanel.Open();
                _setupPanelOpened = true;
                _setupShownSince = now;
                Log("setup: SETUP panel shown");
                if (_tourStep < 0 && !_tourOffered) { _tourOffered = true; After(1.5f, () => { if (_tourStep < 0 && _setupPanel != null) StartTour(); }); }
                return;
            }
            if (_setupShownSince < 0f) _setupShownSince = now;
            TickSpinner(now);
            if (_setupShown < SetupSteps)
            {
                var s = _setup[_setupShown];
                float target = s.Resolved ? 1f : s.Progress;
                // a done step fills in ~0.4 s; a running one eases towards what is measured (never backwards)
                float speed = s.Resolved ? 2.5f : Mathf.Max(0.15f, (target - _setupFill) * 2.5f);
                if (target > _setupFill) _setupFill = Mathf.MoveTowards(_setupFill, target, speed * dt);
                _setupBar.Set(_setupFill);
                ShowSetupPct(s);
                if (s.Resolved && _setupFill >= 0.999f && now - _setupShownSince >= (s.State == SetupState.Failed ? SetupFailedShow : SetupMinShow))
                {
                    _setupShown++;
                    _setupFill = 0f;
                    _setupShownSince = now;
                    _setupBar.Set(0f);
                    if (_setupShown >= SetupSteps) EndSetupDisplay(now);
                    else ShowSetupStepText();
                }
                else if (s.State == SetupState.Failed && !_setupShownFailed) ShowSetupStepText(); // failed while on show
                RefreshSetupRows(false);
                return;
            }
            // the end is on show: it closes itself
            if (_setupEndAt >= 0f && now - _setupEndAt >= (AnySetupFailed() ? SetupCloseAfterFailed : SetupCloseAfter))
            {
                _setupClosedByEnd = true;
                _setupFinished = false;
                if (_setupPanel.IsOpen) _setupPanel.Close();
                Log("setup: SETUP panel closed");
            }
        }

        private static string FailedName(SetupStep s) => "⚠ " + s.Doing + ": failed";

        /// The step on show: its number, its name (an action while it runs), its line.
        private void ShowSetupStepText()
        {
            if (_setupPanel == null) return;
            if (_setupShown >= SetupSteps) return;
            var t = UiTheme.Current;
            var s = _setup[_setupShown];
            _setupStepLabel.Set("STEP " + s.Number + " OF " + SetupSteps + " · ONE-TIME SETUP");
            if (s.State == SetupState.Failed)
            {
                _setupName.Set(FailedName(s), t.Danger);
                _setupDesc.Set(ShortText(s.Note, 90) + (string.IsNullOrEmpty(s.Note) ? "" : " · ") + s.StillWorks);
            }
            else
            {
                _setupName.Set(s.Doing, t.Text);
                _setupDesc.Set(s.Description);
            }
            _setupShownFailed = s.State == SetupState.Failed;
            _setupPctShown = -1;
            _setupNoteShown = null;
            ShowSpinner(true);
        }

        private bool _setupShownFailed;

        private static string ShortText(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        /// "62 %", or the step's own count; set only when it changes.
        private void ShowSetupPct(SetupStep s)
        {
            if (s.State == SetupState.Skipped) { if (_setupNoteShown != s.Note) { _setupNoteShown = s.Note; _setupPct.Set("skipped: " + s.Note); } return; }
            if (s.State == SetupState.Running && !string.IsNullOrEmpty(s.Note))
            {
                if (!ReferenceEquals(_setupNoteShown, s.Note)) { _setupNoteShown = s.Note; _setupPct.Set(s.Note); }
                return;
            }
            int pct = Mathf.RoundToInt(_setupFill * 100f);
            if (pct == _setupPctShown && _setupNoteShown == null) return;
            _setupPctShown = pct;
            _setupNoteShown = null;
            _setupPct.Set(pct + " %");
        }

        /// The five rows: those already shown with ✓ (or ⚠, or skipped) and their time, the one on show, the ones to come.
        /// Rebuilt only when the step on show or a shown step's state changed.
        private void RefreshSetupRows(bool force)
        {
            if (_setupPanel == null) return;
            bool changed = force || _rowShown != _setupShown;
            for (int i = 0; i < SetupSteps && !changed; i++) if (i < _setupShown && _rowState[i] != _setup[i].State) changed = true;
            if (!changed) return;
            _rowShown = _setupShown;
            for (int i = 0; i < SetupSteps; i++)
            {
                var s = _setup[i];
                _rowState[i] = s.State;
                string line;
                if (i < _setupShown)
                {
                    line = s.State == SetupState.Done ? $"<color=#46F28C>✓</color>  {s.DoneName} · {s.Seconds:F1} s"
                        : s.State == SetupState.Failed ? $"<color=#FF4D5E>⚠  {s.Doing}: failed</color>"
                        : $"<color=#9DBDD2>–  {s.Doing}: skipped</color>";
                }
                else if (i == _setupShown) line = $"<color=#8FEBFF>▸  {s.Doing}</color>";
                else line = $"<color=#9DBDD2>○  {s.Doing}</color>";
                _setupRows[i].Set(line);
            }
        }

        /// The end: "Setup done in N s" and what is unlocked now (or what failed and what still works).
        private void EndSetupDisplay(float now)
        {
            var t = UiTheme.Current;
            _setupEndAt = now;
            float total = 0f;
            for (int i = 0; i < SetupSteps; i++) total = Mathf.Max(total, _setup[i].EndedAt - _setupT0);
            bool failed = AnySetupFailed();
            _setupStepLabel.Set("ONE-TIME SETUP · DONE");
            _setupName.Set(failed ? $"Setup finished in {total:F0} s · a step needs a look" : $"Setup done in {total:F0} s", failed ? t.Warn : t.Ok);
            var sb = new System.Text.StringBuilder();
            if (failed)
            {
                for (int i = 0; i < SetupSteps; i++)
                    if (_setup[i].State == SetupState.Failed) sb.Append(_setup[i].Label).Append(": ").Append(_setup[i].StillWorks).Append(' ');
            }
            else sb.Append("Ready now: TALK, THREADS, AGENTS, TERMINALS and FILES. This window closes by itself.");
            _setupDesc.Set(sb.ToString().Trim());
            _setupBar.Set(1f);
            _setupPct.Set("");
            ShowSpinner(false);
            RefreshSetupRows(true);
            RefreshSetupLocksLabel();
            if (failed) { _setupPanel.Closable = true; _setupTourBtn.SetText("CLOSE"); _setupTourBtn.OnClickAction = () => { _setupClosedByEnd = true; _setupFinished = false; _setupPanel.Close(); }; }
            Settings.firstStartDone = true;
            OnSettingsChanged(); // saved (and given to MQH when it is quiet): never again, also not after an update
            Sounds?.Play("ping");
            Log($"setup: done in {total:F1} s" + (failed ? " (with a failed step)" : "") + " · SETUP panel shows the end");
        }

        // ------------------------------------------------------------------ the spinner

        private void ShowSpinner(bool on)
        {
            if (_setupPanel == null || _spinVisible == on) return;
            _spinVisible = on;
            foreach (int id in _spinDots) _setupPanel.Shapes.SetVisible(id, on);
            _spinHead = -1;
        }

        /// Twelve steps a second: only then do the dots' colours change (a restyle in place, no new geometry).
        private void TickSpinner(float now)
        {
            if (!_spinVisible) return;
            int head = (int)(now * 12f) % 12;
            if (head == _spinHead) return;
            _spinHead = head;
            var t = UiTheme.Current;
            for (int i = 0; i < _spinDots.Length; i++)
            {
                int k = (head - i + 12) % 12;
                float a = k == 0 ? 1f : k < 6 ? 1f - k / 6f : 0f;
                _setupPanel.Shapes.SetTint(_spinDots[i], Color.Lerp(SpinDim, t.Accent, a));
            }
        }

        private static readonly Color SpinDim = new Color(0.16f, 0.26f, 0.36f, 1f);

        // ------------------------------------------------------------------ control port, self-tests

        /// `ui setup`: one line per step (state, progress, seconds, note) and what the panel shows.
        public string SetupStatusLine()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("setup ").Append(_setupActive ? "active" : _setupFinished ? "ending" : Settings.firstStartDone ? "done" : "off")
              .Append(", shown step ").Append(_setupShown + 1).Append(", tour ").Append(_tourStep >= 0 ? "step " + (_tourStep + 1) : "off");
            if (_setup != null)
                foreach (var s in _setup)
                    sb.Append('\n').Append(s.Number).Append(' ').Append(s.State).Append(' ').Append((s.Progress * 100f).ToString("F0")).Append("% ")
                      .Append(s.Resolved ? s.Seconds.ToString("F1") + " s" : "").Append(string.IsNullOrEmpty(s.Note) ? "" : " " + s.Note);
            return sb.ToString();
        }

        /// Self-tests: a first start in the Editor, with steps the test sets (SetSetupStepForTest).
        public void BeginFirstRunForTest()
        {
            _setup = NewSetupSteps();
            _setupFake = true;
            _setupClosedByEnd = false;
            _setupPanelOpened = false;
            _tourOffered = true; // the test starts the tour itself
            if (_setupPanel != null)
            {
                _panels.Remove(SetupPanelId);
                Router.Unregister(_setupPanel);
                if (Application.isPlaying) Destroy(_setupPanel.gameObject); else DestroyImmediate(_setupPanel.gameObject);
                _setupPanel = null;
            }
            BeginSetup();
            _lockMask = -1;
            TickSetupLocks();
        }

        public void SetSetupStepForTest(int number, SetupState state, float progress = 0f, string note = null) =>
            Put(_setup[number - 1], state, progress, note, Time.unscaledTime + _testSetupClock);

        /// Self-tests: frames of the first run (the Editor's clock stands still: this one moves).
        public void TickFirstRunForTest(float seconds, float step = 0.05f)
        {
            for (float t = 0f; t < seconds; t += step)
            {
                _testSetupClock += step;
                TickFirstRunAt(Time.unscaledTime + _testSetupClock, step);
            }
        }

        private float _testSetupClock;

        /// Self-tests: the clock TickFirstRunForTest runs on (tour and setup times are on it).
        public float SetupClockForTest => Time.unscaledTime + _testSetupClock;

        /// Self-tests: ends the first run's state (the panel goes, the flag stays as it is).
        public void EndFirstRunForTest()
        {
            _setupActive = false;
            _setupFinished = false;
            _setupFake = false;
            _setupClosedByEnd = true;
            if (_setupPanel != null)
            {
                _panels.Remove(SetupPanelId);
                Router.Unregister(_setupPanel);
                if (Application.isPlaying) Destroy(_setupPanel.gameObject); else DestroyImmediate(_setupPanel.gameObject);
                _setupPanel = null;
            }
            _lockMask = -1;
            TickSetupLocks();
        }
    }
}
