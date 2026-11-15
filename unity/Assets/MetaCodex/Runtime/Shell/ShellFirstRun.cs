using System;
using System.IO;
using MetaCodex.AI;
using MetaCodex.Linux;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// FIRST START · ONE-TIME SETUP (owner 2026-11-14, docs/ready-on-install.md "First start"; one guide card since
    /// 2026-11-15). The APK carries the AI models, the AI runtime, the Linux userland and MQH; the very first start lays
    /// them out (about 20 s after the shell is up, measured on the Quest 3S: models 2.5 s, AI service 1.6 s, Linux 12 s,
    /// MQH 3.6 s, the voice's decision model 11 s). Meanwhile ONE small card in front of the user (the guide card of
    /// ShellTour, made while the shell is built, shown once the splash is gone) is both the tour and the status of the
    /// setup: its strip says in plain words which step runs (STEP k OF 5), how far the whole setup is (one bar over the
    /// five steps, weighted by their usual seconds, never backwards) and that nothing is downloaded. A failed step says
    /// what failed and what still works (at least 4 s) and the setup goes on with the steps that do not need it. At the
    /// end the strip says "Setup done in N s" for 3.5 s, then folds away and the tour slides up into its place. Locks
    /// (ShellSetupLocks) run beside it; a step still not over after 5 minutes is put to Skipped so no lock stays. Only
    /// on the very first start (no Linux installed yet, Settings.firstStartDone off); the flag is set at the end.
    ///
    /// Frame cost: nothing is allocated per frame, a label is set only when its text changes, the spinner is restyled
    /// 12 times a second (tints only), the bar moves only when it moved by 0.2 %.
    public sealed partial class MetaCodexShell
    {
        public enum SetupState { Waiting, Running, Done, Failed, Skipped }

        /// One step of the first start: what the guide card says about it and what the app reads of it.
        public sealed class SetupStep
        {
            public int Number;
            /// While it runs (the words of the card's strip), once it is done (the log), one line on what it does, what
            /// still works when it fails, what it unlocks, its short name.
            public string Doing, DoneName, Description, StillWorks, Unlocks, Label;
            /// The step this one cannot run without (0: none): when that one fails, this one is skipped.
            public int Needs;
            /// Seconds it usually takes: its weight in the card's bar over the five steps, and what its own bar goes by
            /// when it has no measure of its own (at most 90 %).
            public float Typical;
            public SetupState State;
            /// 0..1, as the app measures it.
            public float Progress;
            /// The error of a failed step, why a step was skipped, a count ("1,216 of 1,916 examples").
            public string Note;
            public float StartedAt = -1f, EndedAt = -1f;
            /// Put to Skipped by the five-minute safety: what the app reads later does not change it.
            public bool Forced;
            public bool Resolved => State == SetupState.Done || State == SetupState.Failed || State == SetupState.Skipped;
            public float Seconds => StartedAt >= 0f && EndedAt >= StartedAt ? EndedAt - StartedAt : 0f;
        }

        public const int SetupSteps = 5;
        /// A failed step stays on the strip at least this long: what failed and what still works can be read.
        public const float SetupFailedShow = 4f;
        /// The strip shows "Setup done" this long (longer when a step failed), then folds away.
        public const float SetupEndShow = 3.5f, SetupEndShowFailed = 8f;
        /// A step still not over this long after the setup began is put to Skipped (its lock lifts anyway).
        public const float SetupGiveUpAfter = 300f;
        /// The voice's decision model is given this long after the AI service is up before its step is called failed.
        public const float SetupDeciderTimeout = 240f;
        /// Row 3 of the strip while the steps run.
        public const string SetupNote = "One-time setup: only on this first start, nothing is downloaded";
        /// The strip fades in 0.15 s and the tour slides up into its place in 0.3 s.
        private const float StripFoldSeconds = 0.3f, StripFadeSeconds = 0.15f;

        /// The five steps, in their order (texts: English, plain; `Doing` is what the card says while the step runs,
        /// `Typical` its weight in the bar).
        public static SetupStep[] NewSetupSteps() => new[]
        {
            new SetupStep
            {
                Number = 1, Label = "AI models", Doing = "Unpacking the speech and language models", DoneName = "AI models in place",
                Description = "Speech, language and voice models (about 650 MB) come out of the app onto this headset: no download.",
                StillWorks = "The voice models download the first time voice is used (CONTROL > VOICE > INSTALL); everything else works.",
                Unlocks = "", Typical = 2.5f,
            },
            new SetupStep
            {
                Number = 2, Label = "AI service", Needs = 1, Doing = "Starting the local AI on this headset", DoneName = "Local AI running",
                Description = "Speech-to-text, the language model and the voice run here on the headset, on cores of their own.",
                StillWorks = "Voice is off for now (CONTROL > VOICE > START tries again); Linux, terminals, threads and agents work.",
                Unlocks = "", Typical = 1.6f,
            },
            new SetupStep
            {
                Number = 3, Label = "Linux", Doing = "Unpacking your Linux workstation", DoneName = "Linux ready",
                Description = "Debian with Node.js, git and Python, unpacked from the app: no download.",
                StillWorks = "Terminals, files, MQH and agents need the Linux; WEB, CLIP and voice still work. CONTROL > SYSTEM says why.",
                Unlocks = "TERMINALS and FILES", Typical = 12f,
            },
            new SetupStep
            {
                Number = 4, Label = "MQH", Needs = 3, Doing = "Starting MQH, the agent harness", DoneName = "MQH running",
                Description = "The harness that runs your coding agents, their threads and their tools, inside your Linux.",
                StillWorks = "Threads and agents need MQH; terminals, files, WEB and voice still work. CONTROL > MQH > STATUS > RESTART tries again.",
                Unlocks = "THREADS and AGENTS", Typical = 3.6f,
            },
            new SetupStep
            {
                Number = 5, Label = "Voice commands", Needs = 2, Doing = "Teaching the voice your commands", DoneName = "Voice commands ready",
                Description = "About 1,900 ways to say the app's commands become examples TALK can match, worked out here on the headset.",
                StillWorks = "TALK still works through the language model (slower); CONTROL > VOICE shows the decision model.",
                Unlocks = "TALK", Typical = 11f,
            },
        };

        private SetupStep[] _setup;
        /// _setupActive: the first start's steps run (locks apply; ShellRenderScale reads it); _setupFinished: they are over
        /// and the strip still shows the end or folds away.
        private bool _setupActive, _setupFinished, _setupFake;
        private float _setupT0 = -1f;
        private bool _setupAllResolvedLogged;
        private float _testSetupClock;

        /// The first start's one-time setup is running: the guide card's strip, the locks (ShellSetupLocks), the tour.
        public bool SetupActive => _setupActive;
        /// The steps as the app reads them (the strip shows them one at a time).
        public System.Collections.Generic.IReadOnlyList<SetupStep> SetupStepsNow => _setup;

        /// The clock of the first run: Time.unscaledTime (the self-tests add their own offset, TickFirstRunForTest).
        private float FirstRunNow => Time.unscaledTime + _testSetupClock;

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
            _setupT0 = FirstRunNow;
            _setupAllResolvedLogged = false;
            _guideWanted = true;
            _guideLogged = false;
            _guideReadyAt = -1f;
            ResetStrip();
            // the tour shows the Whisp: its one-time "this is the Whisp" message would only repeat it
            Settings.orbTipShown = true;
            Log("setup: first start, one-time setup (the guide card, the locks, the tour)");
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
            catch (Exception) { return true; } // unknown: no setup card rather than one that waits for nothing
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
        /// rebuilt when a plugin adds a command: the first start is over by then); a step the safety gave up on stays
        /// skipped.
        private void Put(SetupStep s, SetupState state, float progress, string note, float now)
        {
            if (s.Forced) return;
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

        /// The safety: a step still not over SetupGiveUpAfter seconds after the setup began is skipped, so its lock lifts
        /// and the setup ends (the feature then says why it is not there, through its own paths).
        private void GiveUpOnSlowSteps(float now)
        {
            if (_setupT0 < 0f || now - _setupT0 < SetupGiveUpAfter) return;
            for (int i = 0; i < SetupSteps; i++)
            {
                var s = _setup[i];
                if (s.Resolved) continue;
                Put(s, SetupState.Skipped, 1f, "taking longer than expected: unlocked anyway", now);
                s.Forced = true;
            }
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

        private int SetupResolvedCount()
        {
            int n = 0;
            for (int i = 0; i < SetupSteps; i++) if (_setup[i].Resolved) n++;
            return n;
        }

        /// The whole setup as one number 0..1: each step counts by its usual seconds, with what the app measured of it
        /// (a step that is over counts whole, also a failed or skipped one).
        private float SetupOverall()
        {
            float sum = 0f, weight = 0f;
            for (int i = 0; i < SetupSteps; i++)
            {
                var s = _setup[i];
                float w = Mathf.Max(0.1f, s.Typical);
                weight += w;
                sum += w * (s.Resolved ? 1f : Mathf.Clamp01(s.Progress));
            }
            return weight > 0f ? sum / weight : 0f;
        }

        // ------------------------------------------------------------------ every frame

        /// Every frame (Update): reads the steps, lifts the locks as their steps end, opens the guide card on a first
        /// start and runs its strip and tour. Nothing to do (one test) once all that is over, and on every later start.
        private void TickFirstRun(float dt)
        {
            if (!_setupActive && !_setupFinished && !_guideWanted && _tourStep < 0) return;
            TickFirstRunAt(FirstRunNow, dt);
        }

        /// TickFirstRun at a given time (self-tests move their own clock).
        private void TickFirstRunAt(float now, float dt)
        {
            if (_setupActive)
            {
                ReadSetupSteps(now);
                GiveUpOnSlowSteps(now);
                if (AllSetupResolved()) EndSetup(now);
                TickSetupLocks();
            }
            TickGuideOpen(now);
            if (_stripAlive) TickStrip(now, dt);
            if (_tourStep >= 0) TickTour(now, dt);
        }

        /// All steps are over: the locks are lifted (each step lifted its own), the flag is set, the strip says so.
        private void EndSetup(float now)
        {
            _setupActive = false; // the locks go with it
            bool failed = AnySetupFailed();
            float total = 0f;
            for (int i = 0; i < SetupSteps; i++) total = Mathf.Max(total, _setup[i].EndedAt - _setupT0);
            if (!_setupAllResolvedLogged)
            {
                _setupAllResolvedLogged = true;
                Log($"setup: all steps over in {now - _setupT0:F1} s" + (failed ? " (with a failed step)" : ""));
            }
            var g = _guide;
            bool shown = _stripAlive && g != null && g.HasStrip && g.Panel != null && g.Panel.IsOpen;
            _setupFinished = shown;
            if (shown) StripShowEnd(g, now, total, failed);
            Settings.firstStartDone = true;
            OnSettingsChanged(); // saved (and given to MQH when it is quiet): never again, also not after an update
            Sounds?.Play("ping");
            Log($"setup: done in {total:F1} s" + (failed ? " (with a failed step)" : "") + (shown ? " · the guide card shows the end" : ""));
        }

        // ------------------------------------------------------------------ the guide card on a first start

        private bool _guideWanted, _guideLogged;
        private float _guideReadyAt = -1f;

        /// From the build sequence (the "guide" step): a first start makes the guide card now, inactive and not shown, so
        /// showing it later costs only its opening. No-op on every other start.
        private void PrepareFirstRunUi()
        {
            if (!_setupActive) return;
            EnsureGuideCard(true);
        }

        /// The card opens once the splash is gone and the head is tracked, 0.3 s later, and the tour starts with it.
        private void TickGuideOpen(float now)
        {
            if (!_guideWanted) return;
            if (_splash != null) { _guideReadyAt = -1f; return; }
            var head = Head;
            if (Application.isPlaying && (head == null || head.position.sqrMagnitude < 1e-4f)) { _guideReadyAt = -1f; return; }
            if (_guideReadyAt < 0f) _guideReadyAt = now + 0.3f;
            if (now < _guideReadyAt) return;
            StartTour();
        }

        // ------------------------------------------------------------------ the strip (rows 1-3 of the guide card)

        // the strip's state: what it shows now (the step, its number, a failure on show), the bar's eased value, the end
        private bool _stripAlive;
        private int _stripIdx = -1, _stripK, _stripFail = -1, _stripPct = -1, _failShownMask;
        private float _stripIdxAt, _stripFailUntil, _stripFill, _stripTarget, _stripEndAt = -1f;
        private bool _stripEndFailed;
        // the fold: 0 none, 1 the strip fades and the tour slides up, 2 the slide is over and the card waits to be swapped
        private int _foldPhase;
        private float _foldT0;
        private bool _foldHidden;
        private Color _foldC0, _foldC1, _foldC2, _foldC3;

        private static string[] _pctTexts, _stepTexts;

        /// "62 %" from a table: no string is made per change.
        private static string PctText(int pct)
        {
            if (_pctTexts == null)
            {
                _pctTexts = new string[101];
                for (int i = 0; i <= 100; i++) _pctTexts[i] = i + " %";
            }
            return _pctTexts[Mathf.Clamp(pct, 0, 100)];
        }

        /// "STEP 3 OF 5" from a table.
        private static string StepNoText(int k)
        {
            if (_stepTexts == null)
            {
                _stepTexts = new string[SetupSteps + 1];
                for (int i = 0; i <= SetupSteps; i++) _stepTexts[i] = "STEP " + i + " OF " + SetupSteps;
            }
            return _stepTexts[Mathf.Clamp(k, 0, SetupSteps)];
        }

        private static string ShortText(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        private void ResetStrip()
        {
            _stripAlive = false;
            _stripIdx = -1; _stripK = 0; _stripFail = -1; _stripPct = -1; _failShownMask = 0;
            _stripIdxAt = 0f; _stripFailUntil = 0f; _stripFill = 0f; _stripTarget = 0f; _stripEndAt = -1f;
            _stripEndFailed = false;
            _foldPhase = 0; _foldT0 = 0f; _foldHidden = false;
        }

        /// The strip shows now what the steps are at (the card was made or shown just now).
        private void StripSync(GuideCard g, float now)
        {
            if (!g.HasStrip || !_setupActive) return;
            _stripIdx = StripWantedStep();
            _stripK = Math.Min(SetupSteps, SetupResolvedCount() + 1);
            _stripIdxAt = now;
            _stripPct = -1;
            StripPaintText(g);
            g.Bar.Set(_stripFill);
        }

        /// The step the words are about: the lowest-numbered one that runs, else the next one waiting.
        private int StripWantedStep()
        {
            for (int i = 0; i < SetupSteps; i++) if (_setup[i].State == SetupState.Running) return i;
            for (int i = 0; i < SetupSteps; i++) if (!_setup[i].Resolved) return i;
            return SetupSteps - 1;
        }

        private void TickStrip(float now, float dt)
        {
            var g = _guide;
            if (g == null || !g.HasStrip || g.Panel == null || !g.Panel.IsOpen) return;
            if (_foldPhase != 0) { TickFold(g, now); return; }
            if (_setupActive) StripUpdateText(g, now);
            else if (_stripEndAt >= 0f && now - _stripEndAt >= (_stripEndFailed ? SetupEndShowFailed : SetupEndShow)) { BeginFold(g, now); return; }
            StripUpdateBar(g, dt);
            TickSpinner(g, now);
        }

        /// Rows 1 and 3: the step on show changes at most once a second (no flicker), a failure shows at least 4 s.
        private void StripUpdateText(GuideCard g, float now)
        {
            bool dirty = false;
            int idx = StripWantedStep();
            int k = Math.Min(SetupSteps, SetupResolvedCount() + 1);
            if (_stripIdx < 0 || (idx != _stripIdx || k != _stripK) && now - _stripIdxAt >= 1f)
            {
                _stripIdx = idx; _stripK = k; _stripIdxAt = now;
                dirty = true;
            }
            if (_stripFail < 0)
            {
                for (int i = 0; i < SetupSteps; i++)
                {
                    if (_setup[i].State != SetupState.Failed || (_failShownMask & (1 << i)) != 0) continue;
                    _stripFail = i; _failShownMask |= 1 << i; _stripFailUntil = now + SetupFailedShow;
                    dirty = true;
                    break;
                }
            }
            else if (now >= _stripFailUntil) { _stripFail = -1; dirty = true; }
            if (dirty) StripPaintText(g);
        }

        private void StripPaintText(GuideCard g)
        {
            var t = UiTheme.Current;
            if (_stripFail >= 0)
            {
                var f = _setup[_stripFail];
                g.Words.Set("⚠ " + f.Label + " did not start", t.Danger);
                g.Note.Set(ShortText(f.StillWorks, 150), t.TextDim);
            }
            else if (_stripIdx >= 0)
            {
                g.Words.Set(_setup[_stripIdx].Doing, t.Text);
                g.Note.Set(SetupNote, t.TextDim);
            }
            g.StepNo.Set(StepNoText(_stripK), t.TextAccent);
        }

        /// Row 2: the bar over the five steps eases towards what is measured (never backwards); "62 %" only when it changes.
        private void StripUpdateBar(GuideCard g, float dt)
        {
            float target = _setupActive ? SetupOverall() : 1f;
            if (target > _stripTarget) _stripTarget = target;
            if (_stripTarget > _stripFill)
            {
                float speed = _setupActive ? Mathf.Max(0.05f, (_stripTarget - _stripFill) * 2.5f) : 1.5f;
                _stripFill = Mathf.MoveTowards(_stripFill, _stripTarget, speed * dt);
            }
            g.Bar.Set(_stripFill);
            int pct = Mathf.RoundToInt(_stripFill * 100f);
            if (_setupActive && pct > 99) pct = 99; // 100 % only when the end is on show
            if (pct != _stripPct) { _stripPct = pct; g.Pct.Set(PctText(pct)); }
        }

        /// The end: "✓ Setup done in 21 s: everything is unlocked" (or what needs a look), the bar goes full.
        private void StripShowEnd(GuideCard g, float now, float total, bool failed)
        {
            var t = UiTheme.Current;
            _stripEndAt = now;
            _stripEndFailed = failed;
            _stripFail = -1;
            _stripTarget = 1f;
            SetCardClosable(g, true); // afterwards the card can be closed (its ✕ ends the tour)
            SetSpinner(g, false);
            g.StepNo.Set("");
            g.Words.SetRect(new Rect(g.Content.x, g.Words.Rect.y, g.Content.width, g.Words.Rect.height)); // the spinner's room is free
            if (failed)
            {
                SetupStep first = null;
                int n = 0;
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < SetupSteps; i++)
                {
                    if (_setup[i].State != SetupState.Failed) continue;
                    if (first == null) first = _setup[i];
                    if (n++ > 0) names.Append(", ");
                    names.Append(_setup[i].Label);
                }
                g.Words.Set($"Setup finished in {total:F0} s · {names} {(n > 1 ? "need" : "needs")} a look (CONTROL > SYSTEM)", t.Warn);
                g.Note.Set(ShortText(first != null ? first.StillWorks : "", 150), t.TextDim);
            }
            else
            {
                g.Words.Set($"✓ Setup done in {total:F0} s: everything is unlocked", t.Ok);
                g.Note.Set("This happens only once.", t.TextDim);
            }
        }

        // ------------------------------------------------------------------ the fold

        /// The strip folds away: its words fade, then it is hidden, while the tour section slides up into its place.
        private void BeginFold(GuideCard g, float now)
        {
            _foldPhase = 1;
            _foldT0 = now;
            _foldHidden = false;
            _foldC0 = g.Words.Label.Color; _foldC1 = g.StepNo.Label.Color; _foldC2 = g.Pct.Label.Color; _foldC3 = g.Note.Label.Color;
        }

        private void TickFold(GuideCard g, float now)
        {
            if (_foldPhase == 2) { TryFinishFold(g); return; }
            float el = now - _foldT0;
            if (!_foldHidden)
            {
                float a = Mathf.Clamp01(1f - el / StripFadeSeconds);
                FadeLabel(g.Words, _foldC0, a); FadeLabel(g.StepNo, _foldC1, a); FadeLabel(g.Pct, _foldC2, a); FadeLabel(g.Note, _foldC3, a);
                if (a <= 0f)
                {
                    _foldHidden = true;
                    g.Words.SetVisible(false); g.StepNo.SetVisible(false); g.Pct.SetVisible(false); g.Note.SetVisible(false);
                    g.Bar.SetVisible(false); g.Divider.SetVisible(false);
                }
            }
            float u = Mathf.Clamp01(el / StripFoldSeconds);
            float e = u * u * (3f - 2f * u);
            LayoutTour(g, g.ContentTop + g.StripH * (1f - e)); // the tour section comes up
            if (u >= 1f) { _foldPhase = 2; TryFinishFold(g); }
        }

        private static void FadeLabel(UiLabel l, Color c, float a)
        {
            c.a = a;
            l.Label.Color = c;
        }

        /// The slide is over: the tour goes on in a card as tall as it needs (swapped while nobody holds it), or, when the
        /// tour is over, the card closes itself.
        private void TryFinishFold(GuideCard g)
        {
            if (_tourStep >= 0 && (Router.IsDragging(g.Panel) || _moveActive)) return;
            _foldPhase = 0;
            _stripAlive = false;
            _setupFinished = false;
            Log("setup: the strip folded away");
            if (_tourStep >= 0) CompactGuideCard();
            else if (g.Panel.IsOpen) { _tourEnding = true; g.Panel.Close(); _tourEnding = false; }
        }

        // ------------------------------------------------------------------ the spinner

        private void SetSpinner(GuideCard g, bool on)
        {
            if (g.SpinOn == on) return;
            g.SpinOn = on;
            g.SpinHead = -1;
            for (int i = 0; i < g.Spin.Length; i++) g.Panel.Shapes.SetVisible(g.Spin[i], on);
        }

        /// Twelve steps a second: only then do the dots' colours change (a restyle in place, no new geometry).
        private void TickSpinner(GuideCard g, float now)
        {
            if (!g.SpinOn) return;
            int head = (int)(now * 12f) % 12;
            if (head == g.SpinHead) return;
            g.SpinHead = head;
            var t = UiTheme.Current;
            for (int i = 0; i < g.Spin.Length; i++)
            {
                int k = (head - i + 12) % 12;
                float a = k == 0 ? 1f : k < 6 ? 1f - k / 6f : 0f;
                g.Panel.Shapes.SetTint(g.Spin[i], Color.Lerp(SpinDim, t.Accent, a));
            }
        }

        private static readonly Color SpinDim = new Color(0.16f, 0.26f, 0.36f, 1f);

        // ------------------------------------------------------------------ control port, self-tests

        /// `ui setup`: one line per step (state, progress, seconds, note) and what the strip shows.
        public string SetupStatusLine()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("setup ").Append(_setupActive ? "active" : _setupFinished ? "ending" : Settings.firstStartDone ? "done" : "off")
              .Append(", shown step ").Append(Math.Max(1, _stripK)).Append(", tour ").Append(_tourStep >= 0 ? "step " + (_tourStep + 1) : "off");
            if (_setup != null)
                foreach (var s in _setup)
                    sb.Append('\n').Append(s.Number).Append(' ').Append(s.State).Append(' ').Append((s.Progress * 100f).ToString("F0")).Append("% ")
                      .Append(s.Resolved ? s.Seconds.ToString("F1") + " s" : "").Append(string.IsNullOrEmpty(s.Note) ? "" : " " + s.Note);
            return sb.ToString();
        }

        /// Self-tests: the strip of the guide card shows (it is there from the card's opening to its fold).
        public bool SetupStripShowing => _stripAlive && _guide != null && _guide.HasStrip && _guide.Panel != null && _guide.Panel.IsOpen;
        /// Self-tests: row 1 (the step's words, a failure, the end), the step number, row 3, the bar's eased value 0..1.
        public string SetupStripTextNow => _guide != null && _guide.Words != null ? _guide.Words.Text ?? "" : "";
        public string SetupStripStepNow => _guide != null && _guide.StepNo != null ? _guide.StepNo.Text ?? "" : "";
        public string SetupStripNoteNow => _guide != null && _guide.Note != null ? _guide.Note.Text ?? "" : "";
        public string SetupStripPercentNow => _guide != null && _guide.Pct != null ? _guide.Pct.Text ?? "" : "";
        public float SetupStripFillNow => _stripFill;

        /// Self-tests: a first start in the Editor, with steps the test sets (SetSetupStepForTest).
        public void BeginFirstRunForTest()
        {
            DropGuideStateForTest();
            _setup = NewSetupSteps();
            _setupFake = true;
            BeginSetup();
            _lockMask = -1;
            TickSetupLocks();
        }

        public void SetSetupStepForTest(int number, SetupState state, float progress = 0f, string note = null) =>
            Put(_setup[number - 1], state, progress, note, FirstRunNow);

        /// Self-tests: frames of the first run (the Editor's clock stands still: this one moves).
        public void TickFirstRunForTest(float seconds, float step = 0.05f)
        {
            for (float t = 0f; t < seconds; t += step)
            {
                _testSetupClock += step;
                TickFirstRunAt(FirstRunNow, step);
            }
        }

        /// Self-tests: the clock TickFirstRunForTest runs on (tour and setup times are on it).
        public float SetupClockForTest => FirstRunNow;

        /// Self-tests: ends the first run's state (the card goes, the flag stays as it is).
        public void EndFirstRunForTest()
        {
            DropGuideStateForTest();
            _setupActive = false;
            _setupFinished = false;
            _setupFake = false;
            _guideWanted = false;
            _lockMask = -1;
            TickSetupLocks();
        }
    }
}
