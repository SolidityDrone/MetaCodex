using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Voice settings (Settings > VOICE panel).
    [Serializable]
    public sealed class VoiceSettings
    {
        /// The local AI service starts with the app once models are installed.
        public bool enabled = true;
        /// Speak the reply (text-to-speech) as well as showing it.
        public bool speak = false;
        /// Speak the answers to questions about the session ("is the build stuck?") even when replies are not spoken.
        public bool speakAnswers = true;
        /// Say it when a thread is done, needs you (an approval or a question) or stops on an error (owner 2026-11-13).
        public bool speakAlerts = true;
        /// When no phrase matches, ask the local LLM which command was meant (used when the decision model is off or
        /// not ready, and as its fallback when `llmFallback` is on).
        public bool useLlm = true;
        /// Decide commands with the decision model (VoiceDecider: multilingual sentence vectors, ~10-60 ms) instead of the
        /// LLM (0.5-9 s): tools/voicebench measured 79-81 % exact against the LLM's 58 %.
        public bool decide = true;
        /// When the decision model finds no command, still ask the LLM (slower; for unusual phrasings).
        public bool llmFallback = false;
        /// Stop listening by itself after a pause in speech.
        public bool autoStop = true;
        /// Speech-to-text language: always "en" (English only since 2026-11-12). The key stays so old settings files
        /// load; a stored "it" or "auto" reads as "en" (ShellSettings.Load).
        public string language = "en";
        /// Inference threads (cores 0-2 only): 2 keeps 72 fps (measured), 3 is faster.
        public int threads = 2;
        public float maxSeconds = 12f;
        public string voice = "F1";
        /// The voice effect of spoken replies (VoiceFx): off | adjutant | starship | comm (VOICE > SOUND: OFF, AI, SHIP,
        /// COMM). Every one trims the silence around the speech and levels it; off adds nothing else.
        public string fx = "adjutant";
        /// Speech quality (VOICE > SPEECH): fast = Supertonic with 3 diffusion steps, clear = 5 (the model default).
        /// Measured 2026-11-12 (PC, 2 threads): 4 steps makes a sentence 10-18 % sooner; through speech-to-text it
        /// now and then slips a small word ("a login fix" for "the login fix", "most the project"); 5 is exact.
        /// 2026-11-13 (owner): FAST is 3 steps, the voice effect hides the small loss; the headset runs the voice
        /// at about 3x slower than it plays, so every step counts.
        public string speech = "fast";
        /// "Hey Whisp" starts TALK hands-free (WakeListener; CONTROL > VOICE, `set_wake_word`). On by default: the
        /// microphone stays open and a cheap gate sends short pieces of speech to the AI service's wake check.
        public bool wakeWord = true;
    }

    /// One voice request, start to end (VOICE panel history, control port replies).
    public sealed class VoiceResult
    {
        public string Transcript = "", Via, Command, Args, Reply, Error;
        /// The words as the matcher understood them, when it had to fix misheard ones ("fogus" -> "focus").
        public string Corrected;
        /// The reply answers something (spoken when spoken replies are on); action confirmations are not.
        public bool Answer;
        public int AsrMs, LlmMs, DecideMs, TotalMs;
        public float Started = -1f;
        public DateTime At;
        public bool Ok => Error == null;

        public JObject ToJson() => new JObject
        {
            ["transcript"] = Transcript, ["via"] = Via, ["command"] = Command, ["args"] = Args,
            ["reply"] = Reply, ["error"] = Error, ["corrected"] = Corrected, ["asrMs"] = AsrMs, ["decideMs"] = DecideMs, ["llmMs"] = LlmMs, ["totalMs"] = TotalMs,
        };

        public override string ToString() =>
            $"\"{Transcript}\" -> {(Command != null ? Command + " " + Args + " (" + Via + ")" : "-")}: {(Ok ? Reply ?? "ok" : "ERROR " + Error)} [asr {AsrMs} ms, decide {DecideMs} ms, llm {LlmMs} ms, total {TotalMs} ms]";
    }

    /// Voice control (docs/voice.md): push-to-talk -> local speech-to-text -> a command from the registry (a
    /// phrase template matches, else the local LLM picks a tool) -> the action -> HUD and optional spoken reply.
    /// All AI runs in the local AI service (QvacHost), never in this process; this side records, decides and acts.
    public sealed partial class MetaCodexShell
    {
        public QvacHost Qvac { get; private set; }
        public VoiceCommands Voice { get; private set; }
        public VoiceRecorder Recorder { get; private set; }
        /// The decision model for commands (Jev-style; built from the registry once the AI service has the model).
        public VoiceDecider Decider { get; private set; }
        private float _nextDeciderTry;
        private int _deciderSavedCount;
        private bool _embedInstallAsked;
        private bool _deciderTest;

        /// Tests: a decision model with its own embedder, used without the AI service (`on` false restores the normal rule).
        public void UseDeciderForTest(VoiceDecider d, bool on) { Decider = d; _deciderTest = on; }
        /// The vectors on disk belong to this model (local-ai/server/models.js `embed.id`) and prefix; other ones are ignored.
        public const string DeciderModelTag = "multilingual-e5-small-q8_0|query: ";
        /// idle | listening | transcribing | thinking | done | error
        public string VoiceState { get; private set; } = "idle";
        /// "Hey Whisp": the listener of the wake phrase (WakeListener; on only with Settings.voice.wakeWord).
        public WakeListener Wake { get; private set; }
        /// "Hey Whisp" is on (CONTROL > VOICE, `set_wake_word`, set_setting wake_word; the first start's guide may offer it).
        public bool WakeWordOn => Settings.voice.wakeWord;
        /// The Whisp listens for its name right now (on, and nothing holds it): a small sign on the Whisp may follow it.
        public bool WakeListening => Wake != null && Wake.On && (Wake.State == "listening" || Wake.State == "checking");
        /// The wake phrase was heard (after the Whisp's flare, before TALK listens).
        public event Action WakeHeard;
        private bool _wakeTalk;            // this TALK was started by "Hey Whisp": the phrase is dropped, no speech ends quietly
        private bool _wakeAgain;           // this wake already listened once more after a transcript of only the phrase
        private float _wakeQuietAt = -10f; // the last time TALK ran or the app spoke (the wake gate waits a moment after)
        private bool _wakePerm, _wakeWasPaused;
        private float _wakePermAt = -10f;
        private int _vHudKey = -1;         // what the listening HUD's line shows now (set only when it changes)
        // the TALK under way: 0 by the button; after "Hey Whisp": the phrase alone, the command with it, listening once more
        private const int WakeAlone = 1, WakeWithCommand = 2, WakeAgain = 3;
        /// After "Hey Whisp": how long it waits for the command to start (from the cue), the quiet that ends the command,
        /// and the wait when it listens once more (only the phrase came). TALK by the button ends after TalkSilenceSeconds.
        public const float WakeWaitSeconds = 6f, WakeSilenceSeconds = 1f, WakeAgainSeconds = 5f, TalkSilenceSeconds = 0.7f;
        // the listening HUD's line after "Hey Whisp", made once: the seconds left to start, then dots that follow the voice
        private static readonly string[] WakeWaitTexts = { "Say a command", "Say a command · 1 s", "Say a command · 2 s", "Say a command · 3 s", "Say a command · 4 s", "Say a command · 5 s", "Say a command · 6 s", "Say a command · 7 s" };
        private static readonly string[] HearingTexts = { "Hearing you  ○○○○○", "Hearing you  ●○○○○", "Hearing you  ●●○○○", "Hearing you  ●●●○○", "Hearing you  ●●●●○", "Hearing you  ●●●●●" };
        public readonly List<VoiceResult> VoiceRecent = new List<VoiceResult>();
        public JObject QvacStatus { get; private set; }

        /// The shell's legend for its own values (the small LLM picks panels by what they show).
        public const string VoiceLegend = "PANEL: terminals, files, git (repositories, commits, branches), agents (AI providers, sign-in, accounts, models), " +
            "threads (agent conversations), browser (web), scripts, gestures, vault (secrets, passwords, API keys), settings, voice.";

        /// The stable prompt prefix: the system prompt and the example turns. Measured on 10 phrasings no template
        /// covers: one command line taught by example turns 8/10 right with 1-4 tokens to decode, JSON tool calls
        /// 5/8 with ~20 (docs/voice.md). Rebuilt when the command set changes.
        private JArray VoicePrefix()
        {
            if (_voicePrefix != null && _voicePrefixRev == Voice.Revision) return _voicePrefix;
            var a = new JArray { new JObject { ["role"] = "system", ["content"] = Voice.LlmSystem(VoiceLegend) } };
            foreach (var (said, line) in Voice.LlmExamples())
            {
                a.Add(new JObject { ["role"] = "user", ["content"] = said });
                a.Add(new JObject { ["role"] = "assistant", ["content"] = line });
            }
            _voicePrefix = a;
            _voicePrefixRev = Voice.Revision;
            _voiceGrammar = Voice.LlmGrammar();
            return a;
        }

        /// The exact LLM request the pipeline sends for `said` (self-tests replay it against the service).
        public JObject VoiceLlmRequest(string said)
        {
            var msgs = (JArray)VoicePrefix().DeepClone();
            msgs.Add(new JObject { ["role"] = "user", ["content"] = said });
            return new JObject { ["messages"] = msgs, ["grammar"] = _voiceGrammar, ["max_tokens"] = 24, ["temperature"] = 0 };
        }

        private JArray _voicePrefix;
        private int _voicePrefixRev = -1;
        private string _voiceGrammar;
        public static readonly string[] VoicePanels = { "terminals", "files", "git", "agents", "threads", "web", "links", "clipboard", "mqh", "skills", "plugins", "scripts", "gestures", "vault", "settings", "voice", "system", "focus", "storage", "tools", "messages", "workspaces", "control", "display", "look", "hands", "about", WristSettingsId };

        private AudioSource _voiceAudio;
        private UiPanel _vHud;
        private UiLabel _vHudTitle, _vHudText;
        private UiProgress _vHudLevel;
        private float _vHudCloseAt = -1f;
        private UiLabel _vStatus, _vModels, _vHistory;
        private UiList _vCmdList;
        private UiButton _vServiceBtn;
        /// VOICE's service button: what a tap does now.
        private string ServiceButtonText() => Qvac != null && (Qvac.IsRunning || Qvac.IsBusyStarting) ? "■ STOP" : "▶ START";
        private UiProgress _vInstall;
        private float _nextVoicePoll;
        private int _warmedRevision = -1;
        private bool _warming;
        private bool _warmWanted;      // TALK asked for a warm-up: it runs once the voice has been quiet for WarmQuietSeconds
        private float _voiceBusyAt;    // the last time a voice turn or a spoken reply was going on
        /// The LLM warm-up (a ~23 s prompt prefill) takes the cores the voice needs: it starts only after this many
        /// quiet seconds (no recording, no transcribing or thinking, nothing spoken or queued to be spoken).
        public const float WarmQuietSeconds = 5f;
        private string _sentConfig;

        private void InitVoice()
        {
            StartMark("voice+scripts");
            Voice = new VoiceCommands();
            Recorder = new VoiceRecorder();
            Recorder.Finished += OnRecorderFinished;
            RegisterShellVoiceCommands();
            RegisterSessionVoiceCommand(); // questions about the session (ShellContext)
            RegisterLaunchVoiceCommands(); // the app an agent asked to launch (ShellReturn)
            RegisterSidebarVoiceCommands(); // THREAD's sidebar and INIT GIT (ShellThreadSidebar)
            RegisterComposerVoiceCommand(); // THREAD's message box: bigger, smaller, reset (ShellThreadComposer)
            RegisterThreadWindowVoiceCommand(); // several thread windows and their groups (ShellThreadGroups)
            RegisterArchiveVoiceCommands(); // archive_thread, restore_thread (ShellThreadArchive)
            RegisterTourVoiceCommand(); // the guided tour (ShellTour)
            RegisterWakeVoiceCommand(); // "Hey Whisp" on / off (below)
            Wake = new WakeListener { Blocked = WakeBlocked };
            Wake.Check = (wav, query, cb) => { if (Qvac?.Client == null) cb(null, "no AI service"); else Qvac.Client.WakeCheck(wav, query, cb); };
            Wake.Heard += OnWakeHeard;
            Wake.NearMiss += text => Log($"wake: near miss \"{text}\" (a word like the name, no wake)");
            Wake.SetOn(Settings.voice.wakeWord, Recorder);
            Decider = new VoiceDecider(Voice, (texts, done) =>
            {
                if (Qvac == null || !Qvac.IsRunning) { done(null, "the AI service is not running"); return; }
                Qvac.Client.Embed(texts, (r, e) => done(r?.Vectors, e));
            });
            Decider.Post = a => _main.Enqueue(a); // its examples are made off the main thread (VoiceDecider.Build)
            LoadDeciderCache();
            Qvac = new GameObject("QvacHost").AddComponent<QvacHost>();
            Qvac.transform.SetParent(transform, false);
            Qvac.Log += s => Log("[ai] " + s);
            Qvac.StateChanged += OnQvacStateChanged;
            Qvac.Init(_main, Application.persistentDataPath, Settings.voice.threads);
            if (Settings.voice.enabled && Qvac.ModelsPresent) Qvac.StartService();
            // first start of a new install: no models yet. Start the service and fetch them once by itself, so voice works
            // without anyone opening VOICE > INSTALL (the marker keeps the user's later choice to remove them)
            else if (Settings.voice.enabled && !File.Exists(VoiceAutoInstallMarker) && !Application.isEditor) { _autoInstallVoice = true; Qvac.StartService(); }
        }

        private static string VoiceAutoInstallMarker => Path.Combine(Application.persistentDataPath, "voice-models-auto-install.done");
        private bool _autoInstallVoice;

        /// The first-start download of the voice models (speech-to-text, LLM, text-to-speech, decision model), once.
        private void AutoInstallVoiceModels()
        {
            _autoInstallVoice = false;
            _embedInstallAsked = true; // the decision model comes with this request: no second one for it
            try { File.WriteAllText(VoiceAutoInstallMarker, DateTime.Now.ToString("s")); } catch (Exception ex) { Log("voice auto install marker: " + ex.Message); }
            if (ModelInstalled("asr")) return;
            Qvac.Client.Install(new[] { "asr", "llm", "tts", "embed" }, (r, e) =>
            {
                if (e != null) { Log("voice: first-start model install: " + e); return; }
                Toast("Downloading the voice models (about 660 MB) in the background: voice commands work when they are in", "info");
                After(1f, PollVoiceStatus);
            });
        }

        private void OnQvacStateChanged()
        {
            if (Qvac.State == QvacHost.HostState.Running)
            {
                _sentConfig = null;
                PushVoiceConfig();
                PollVoiceStatus();
            }
            RefreshVoicePanel();
        }

        /// Starts the AI service if it is not up (the VOICE panel, TALK and `voice` commands call this).
        public void EnsureVoiceService()
        {
            if (Qvac != null && !Qvac.IsRunning && !Qvac.IsBusyStarting) Qvac.StartService();
        }

        /// Threads, language, speech-to-text vocabulary and idle times, sent when they change.
        private void PushVoiceConfig()
        {
            if (Qvac == null || !Qvac.IsRunning) return;
            var words = new List<string> { "MetaCodex", "Whisp", "hey Whisp", "ambient focus", "terminal", "workspace", "thread", "panel", "layout", "float", "fixed", "recenter", "intensity", "radius", "softness" };
            words.AddRange(VoicePanels);
            foreach (var c in Voice.All) if (c.Enabled && c.Vocabulary != null) words.AddRange(c.Vocabulary);
            words.AddRange(_projects.Select(p => (string)p["name"]).Where(n => !string.IsNullOrEmpty(n)).Take(12));
            var cfg = new JObject
            {
                ["threads"] = Mathf.Clamp(Settings.voice.threads, 1, 2), // measured: 3 and 4 are slower on the 3 cores the service has
                ["language"] = "en", // English only since 2026-11-12
                ["prompt"] = "Voice commands for MetaCodex: " + string.Join(", ", words.Distinct()) + ".",
                ["voice"] = Settings.voice.voice,
                ["ttsLanguage"] = "en",
                // the voice model stays loaded 15 min (was 3): a reload costs 1-4 s before the first word on the headset
                ["idleMinutes"] = new JObject { ["asr"] = 10, ["llm"] = 15, ["tts"] = 15 },
                // VOICE > SPEECH: FAST = 3 diffusion steps (the voice effect hides the small loss), CLEAR = 5; a change
                // reloads the voice model once
                ["ttsSteps"] = Settings.voice.speech == "clear" ? 5 : 3,
            };
            // 3 threads on the 3 AI cores while an answer streams into speech (measured 2026-11-12, docs/voice.md
            // "Streaming answers"): the LLM decodes on 1 (it still writes ~2x faster than speech plays) and reads its
            // prompt on 2, the voice keeps 2; with 2 + 2 the LLM was 29 % and the voice 62 % slower while they overlapped
            if (Settings.voice.threads >= 2) { cfg["llmThreads"] = 1; cfg["llmBatchThreads"] = 2; cfg["ttsThreads"] = 2; }
            string s = cfg.ToString(Newtonsoft.Json.Formatting.None);
            if (s == _sentConfig) { PrepareAck(); return; }
            _sentConfig = s;
            Qvac.Client.Config(cfg, (r, e) => { if (e != null) { Log("[ai] config: " + e); _sentConfig = null; } else PrepareAck(); });
        }

        // ------------------------------------------------------------------ the acknowledgement, made ahead

        /// Said at once when a session question starts (its answer is made while this plays): owner 2026-11-12, "we
        /// need to achieve the best feedback". Made once per voice / effect / speech setting and kept as a WAV.
        public const string AckLine = "Let me look.";
        private byte[] _ackWav;
        private string _ackKey;
        private bool _ackBusy;
        private string AckKey() => Settings.voice.voice + "|" + Settings.voice.fx + "|" + Settings.voice.speech;
        public bool AckReadyForTest => _ackWav != null && _ackKey == AckKey();

        private void PrepareAck()
        {
            string key = AckKey();
            if (_ackBusy || _ackKey == key && _ackWav != null) return;
            if (SpeakForTest == null && (Qvac == null || !Qvac.IsRunning)) return;
            _ackBusy = true;
            var fx = VoiceFx.Parse(Settings.voice.fx);
            Action<byte[], string> got = (wav, e) => { _ackBusy = false; if (wav != null && e == null) { _ackWav = wav; _ackKey = key; } else Log("[ai] acknowledgement: " + e); };
            if (SpeakForTest != null) SpeakForTest(AckLine, (w, e) => got(w != null ? VoiceFx.ProcessWav(w, fx, true, false) : null, e));
            else Qvac.Client.Speak(AckLine, Settings.voice.voice, fx, true, false, got);
        }

        private bool _cpuBoosted;
        private float _boostUntil;
        private OVRManager.ProcessorPerformanceLevel _cpuBefore;

        /// Raises the CPU level while a voice request runs (measured: the cores the AI service shares go from
        /// 1.38 to 2.05 GHz; speech-to-text, LLM and speech 16-18% faster) and gives it back right after.
        private void BoostCpuFor(float seconds)
        {
            _boostUntil = Mathf.Max(_boostUntil, Time.unscaledTime + seconds);
            if (_cpuBoosted || Application.isEditor) return;
            try
            {
                _cpuBefore = OVRManager.suggestedCpuPerfLevel;
                OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.Boost;
                _cpuBoosted = true;
            }
            catch (Exception ex) { Log("[ai] cpu level: " + ex.Message); }
        }

        private void EndBoostSoon(float seconds = 0.4f) => _boostUntil = Time.unscaledTime + seconds;

        private void TickVoice()
        {
            TickSpeech(Time.unscaledTime);
            Wake?.Tick(Time.unscaledTime); // "Hey Whisp": nothing to do while off
            TickThreadAlerts(); // "the thread on X is done" waits for a quiet moment (ShellThreadView)
            if (_cpuBoosted && Time.unscaledTime > _boostUntil && (Recorder == null || !Recorder.Recording))
            {
                try { OVRManager.suggestedCpuPerfLevel = _cpuBefore; } catch (Exception) { }
                _cpuBoosted = false;
            }
            if (Recorder != null && Recorder.Recording)
            {
                Recorder.Tick();
                if (Recorder.Recording)
                {
                    _vHudLevel?.Set(Recorder.Level);
                    UpdateListeningText();
                }
            }
            if (_vHudCloseAt > 0f && Time.unscaledTime > _vHudCloseAt && (Recorder == null || !Recorder.Recording) && (VoiceState == "done" || VoiceState == "error" || VoiceState == "idle"))
            {
                _vHudCloseAt = -1f;
                if (_vHud != null && _vHud.IsOpen) _vHud.Close();
            }
            if (_panels.TryGetValue("voice", out var vp) && vp.IsOpen && Time.unscaledTime >= _nextVoicePoll)
            {
                _nextVoicePoll = Time.unscaledTime + 2f;
                PollVoiceStatus();
            }
            if (_autoInstallVoice && Qvac != null && Qvac.IsRunning && QvacStatus != null) AutoInstallVoiceModels();
            // the decision model: (re)built from the registry when the service has its model and the commands changed
            if (Decider != null && Settings.voice.decide && Qvac != null && Qvac.IsRunning && !Decider.Ready && !Decider.Building
                && Time.unscaledTime >= _nextDeciderTry && QvacStatus != null && !ModelInstalled("embed") && ModelInstalled("asr") && !_embedInstallAsked)
            {
                // voice models are installed but not the decision model (an update added it): fetch it once (132 MB)
                _embedInstallAsked = true;
                Qvac.Client.Install(new[] { "embed" }, (r, e) =>
                {
                    if (e != null) { Log("voice: decision model install: " + e); return; }
                    Toast("Downloading the decision model for voice commands (132 MB)", "info");
                    After(1f, PollVoiceStatus);
                });
            }
            if (Decider != null && Settings.voice.decide && Qvac != null && Qvac.IsRunning && !Decider.Ready && !Decider.Building && !_deciderCacheLoading
                && Time.unscaledTime >= _nextDeciderTry && ModelInstalled("embed"))
            {
                _nextDeciderTry = Time.unscaledTime + 15f;
                double buildAt = Time.realtimeSinceStartupAsDouble;
                Decider.Build(err =>
                {
                    if (err == null) { SaveDeciderCache(); Log($"voice: decision model {Decider.Status} (built in {Time.realtimeSinceStartupAsDouble - buildAt:F0} s; main-thread finish {Decider.LastFinishMs:F0} ms)"); }
                    else Log("voice: " + err);
                    RefreshVoicePanel();
                });
            }
            // keep the LLM's prompt cache in step with the command set (only once the user uses voice). The warm-up
            // shares the 3 AI cores with speech-to-text and the voice (measured 2026-11-13: first sentence 12 s while it
            // ran), so it waits until the voice is idle: a TALK press only sets _warmWanted.
            if (VoiceState != "idle" || Speaking || Recorder != null && Recorder.Recording) _voiceBusyAt = Time.unscaledTime;
            if (Qvac != null && Qvac.IsRunning && Settings.voice.useLlm && !_warming && (_warmWanted || _warmedRevision >= 0)
                && _warmedRevision != Voice.Revision && Time.unscaledTime - _voiceBusyAt >= WarmQuietSeconds)
            {
                _warmWanted = false;
                WarmVoiceLlm();
            }
        }

        public void PollVoiceStatus()
        {
            if (Qvac == null || !Qvac.IsRunning) { RefreshVoicePanel(); return; }
            Qvac.Client.Status((r, e) =>
            {
                QvacStatus = r as JObject;
                RefreshVoicePanel();
            });
        }

        private bool ModelInstalled(string kind) => QvacStatus?["models"]?[kind]?["installed"]?.Value<bool>() == true;

        /// Prefills the system prompt + every command as tools in the service (saved to disk there), so a
        /// command the fast path misses costs ~1.5 s instead of ~7 s.
        public void WarmVoiceLlm(Action<string> done = null)
        {
            if (Qvac == null || !Qvac.IsRunning || _warming) { done?.Invoke("not running"); return; }
            _warming = true;
            int rev = Voice.Revision;
            var body = new JObject { ["messages"] = VoicePrefix().DeepClone(), ["tools"] = new JArray() };
            Qvac.Client.Warmup(body, (r, e) =>
            {
                _warming = false;
                if (e == null) _warmedRevision = rev;
                Log("[ai] warm-up " + (e ?? (r?["cached"]?.Value<bool>() == true ? "cached" : r?["ms"] + " ms")));
                done?.Invoke(e);
            });
        }

        // ------------------------------------------------------------------ "Hey Whisp" (docs/voice.md)

        /// Turns "Hey Whisp" on or off (saved). On asks for the microphone first when it is not allowed yet; off closes
        /// the shared capture, and TALK is then exactly the button's TALK again.
        public void SetWakeWord(bool on)
        {
            if (on && !VoiceRecorder.HasPermission)
            {
                VoiceRecorder.RequestPermission(ok => _main.Enqueue(() =>
                {
                    if (ok) SetWakeWord(true);
                    else { Toast("Hey Whisp needs the microphone: allow it in Horizon settings > Apps > MetaCodex", "warn"); SyncSettingsControls(); }
                }));
                return;
            }
            if (Settings.voice.wakeWord != on) { Settings.voice.wakeWord = on; OnSettingsChanged(); }
            _wakePermAt = -10f; // the permission may have just been given: look again at once
            Wake?.SetOn(on, Recorder);
            SyncSettingsControls();
            Log("wake: Hey Whisp " + (on ? "on" : "off"));
        }

        /// Why the wake listener must not listen now (null: it listens). Called every frame: no allocation.
        private string WakeBlocked()
        {
            float now = Time.unscaledTime;
            if (!Application.isFocused)
            {
                // the session log gets the counters once per pause (a missed try shows as a near miss or a drop)
                if (!_wakeWasPaused && Wake != null && Wake.Checks + Wake.Dropped > 0) Log("wake: " + Wake.Describe());
                _wakeWasPaused = true;
                return "paused";
            }
            _wakeWasPaused = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRManager.instance != null && !OVRManager.instance.isUserPresent) return "off head";
            if (OVRManager.instance != null && !OVRManager.hasInputFocus) return "no focus"; // the system menu is open
#endif
            if (SetupLocked(5)) return "setup"; // the first start's setup: voice is not ready yet
            if (now >= _wakePermAt) { _wakePermAt = now + 3f; _wakePerm = VoiceRecorder.HasPermission; } // not asked here: TALK or the switch asks
            if (!_wakePerm) return "microphone permission";
            if (Recorder.Recording || Dictating || VoiceState == "listening" || VoiceState == "transcribing" || VoiceState == "thinking" || Speaking)
            {
                _wakeQuietAt = now;
                return Speaking ? "speaking" : "talk";
            }
            if (now - _wakeQuietAt < 1f) return "talk"; // the end of TALK or of a reply (its sound, the room's echo)
            if (Qvac == null || !Qvac.IsRunning) return "no AI service";
            return null;
        }

        /// The wake phrase was heard: at once the Whisp flares and turns gold, a short sound, and the HUD by the Whisp
        /// says it listens (seconds left to start, then dots that follow the voice). Nothing is spoken first. The phrase
        /// alone: TALK takes the audio from the end of the phrase and waits up to WakeWaitSeconds for the command to
        /// start, then ends after WakeSilenceSeconds of quiet. The command in the same breath ("Hey Whisp, open the
        /// terminal"): TALK takes the audio from the start of the phrase, already has the quiet after it, and ends
        /// soon; the phrase is dropped from what it heard (WakeWord.Command).
        private void OnWakeHeard(WakeHit hit)
        {
            if (Recorder.Recording) return;
            WhispFlare(); // ShellOrb; TALK's listening starts the same flare (one flare)
            bool with = hit.Continues;
            Log($"wake: heard \"{hit.Text}\" ({hit.AudioMs} ms of audio, check {hit.ServiceMs} ms, round trip {hit.RoundTripMs} ms"
                + (hit.ReactMs >= 0 ? $", {hit.ReactMs} ms after the phrase ended" : "") + ")" + (with ? ", the command came with it" : ", listening for the command"));
            WakeHeard?.Invoke();
            int from = hit.StartPos < 0 ? -1 : with ? hit.StartPos : hit.EndPos;
            _wakeTalk = true;
            _wakeAgain = false;
            // no status round trip first: the service drops a wake check while the local coder holds the cores, so
            // this one passed that check a moment ago
            VoiceListen(true, from, with ? WakeWithCommand : WakeAlone);
            if (!Recorder.Recording) _wakeTalk = false; // it did not start (an error says why)
        }

        /// The transcript was only the phrase ("a whisp,", "Hey Whisper."): listen once more for the command, from where
        /// the recording stopped (what was said while it transcribed is kept). False: it already did, or cannot.
        private bool WakeListenAgain(string heard)
        {
            if (_wakeAgain || Recorder.Recording) return false;
            _wakeAgain = true;
            Log($"wake: only the phrase came (\"{heard}\"); listening once more");
            _wakeTalk = true;
            VoiceListen(true, Recorder.SharedOpen ? Recorder.ReadPos : -1, WakeAgain);
            if (!Recorder.Recording) { _wakeTalk = false; return false; }
            return true;
        }

        /// A TALK that "Hey Whisp" started heard no command: no error sound and no red HUD; a short "Didn't catch that"
        /// (`cue` false: the user stopped it).
        private void WakeQuietEnd(string why, bool cue = true)
        {
            _wakeTalk = false;
            Log("wake: nothing followed the phrase" + why);
            if (!cue) { SetVoiceState("idle", "", ""); if (_vHud != null && _vHud.IsOpen) _vHud.Close(); return; }
            SetVoiceState("idle", "Didn't catch that", "Say \"Hey Whisp\" again, or press TALK");
            _vHudCloseAt = Time.unscaledTime + 2.2f;
            Sounds?.Play("close", 0.6f);
            _wakeQuietAt = Time.unscaledTime - 0.4f; // the gate listens again in 0.6 s (the soft sound's end), not 1 s
        }

        /// The listening HUD's second line, set only when it changes: after "Hey Whisp" the seconds left to start and
        /// then dots that follow the voice (strings made once); TALK by the button its seconds (ten changes a second).
        private void UpdateListeningText()
        {
            if (_vHudText == null) return;
            int key;
            string text = null;
            if (_wakeTalk && !Recorder.HeardSpeech)
            {
                int left = Mathf.Clamp(Mathf.CeilToInt(Recorder.NoSpeechSeconds - Recorder.Seconds), 0, WakeWaitTexts.Length - 1);
                key = 200 + left;
                if (key != _vHudKey) text = WakeWaitTexts[left];
            }
            else if (_wakeTalk)
            {
                int lv = Mathf.Clamp(Mathf.RoundToInt(Recorder.Level * 5f), 0, HearingTexts.Length - 1);
                key = 100 + lv;
                if (key != _vHudKey) text = HearingTexts[lv];
            }
            else
            {
                key = 1000 + Mathf.FloorToInt(Recorder.Seconds * 10f) * 2 + (Recorder.HeardSpeech ? 1 : 0);
                if (key != _vHudKey) text = $"{Recorder.Seconds:F1} s" + (Recorder.HeardSpeech ? " · hearing you" : " · speak now") + "   (TALK again to stop)";
            }
            if (text == null) return;
            _vHudKey = key;
            _vHudText.Set(text);
        }

        private void RegisterWakeVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "set_wake_word",
                Description = "Hey Whisp: start TALK by saying \"Hey Whisp\" (on: the microphone stays open and listens for the phrase) or by the TALK button only (off)",
                Parameters = VoiceCommands.Schema(("state", "string", new[] { "on", "off" }, "on: listen for \"Hey Whisp\"; off: TALK by its button only", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["state"] = new Dictionary<string, string> { ["enable"] = "on", ["start"] = "on", ["activate"] = "on", ["disable"] = "off", ["stop"] = "off", ["deactivate"] = "off" },
                },
                Phrases = new[]
                {
                    "[turn|switch|set] [the] (wake word|wake phrase|hey whisp|hey wisp|hands free talk|hands free) {state}",
                    "(turn|switch) {state} [the] (wake word|wake phrase|hey whisp|hey wisp|hands free talk|hands free)",
                    "(listen|wait) for (hey whisp|hey wisp|your name|the wake word|the wake phrase) {state:on}",
                    "(stop listening|do not listen|dont listen) for (hey whisp|hey wisp|your name|the wake word|the wake phrase) {state:off}",
                },
                Samples = new[]
                {
                    "let me wake you up by saying your name", "stop listening for your name all the time", "i want to talk to you hands free",
                    "enable the wake phrase", "no more hey whisp please", "can i start talking without pressing the button",
                },
                Examples = new List<(string, string)> { ("answer when I call your name", "set_wake_word on"), ("only listen when I press talk", "set_wake_word off") },
                Vocabulary = new[] { "Whisp", "hey Whisp", "wake word", "hands free" },
                Run = a =>
                {
                    bool on = (string)a["state"] == "on";
                    SetWakeWord(on);
                    return on ? "Hey Whisp on: say \"Hey Whisp\" to talk" : "Hey Whisp off: TALK by its button";
                },
            });
        }

        // ------------------------------------------------------------------ the pipeline

        public void VoiceToggle()
        {
            if (Recorder.Recording) VoiceStop();
            else VoiceListen();
        }

        public void VoiceListen() { _wakeTalk = false; _wakeAgain = false; VoiceListen(false); }

        /// The local coder holds the AI cores (an agent on the local model is working): voice waits for its turn to end
        /// instead of queueing behind it (owner 2026-11-12). The service enforces it too; this tells the user at once.
        private void VoiceListen(bool coderChecked, int fromPos = -1, int wake = 0)
        {
            if (Recorder.Recording) return;
            if (SetupBlocks(5, "TALK")) return; // the first start's setup: the decision model is not ready (ShellSetupLocks)
            if (!coderChecked && Qvac != null && Qvac.IsRunning)
            {
                Qvac.Client.Status((r, e) =>
                {
                    QvacStatus = r as JObject ?? QvacStatus;
                    if (e == null && r?["coderHold"]?.Value<bool>() == true)
                    {
                        SetVoiceState("error", "⏸ LOCAL CODER WORKING", "Voice waits until the agent's turn on the local model ends");
                        _vHudCloseAt = Time.unscaledTime + 4f;
                        Sounds?.Play("error");
                        return;
                    }
                    VoiceListen(true, fromPos, wake);
                });
                return;
            }
            EnsureVoiceService();
            if (!VoiceRecorder.HasPermission)
            {
                VoiceRecorder.RequestPermission(ok => _main.Enqueue(() =>
                {
                    if (ok) VoiceListen(true, fromPos, wake);
                    else VoiceDone(new VoiceResult { Error = "microphone permission denied (Horizon settings > Apps > MetaCodex)" }, null);
                }));
                return;
            }
            // hands-free ends by itself; after the phrase alone it waits for the command to start (from the cue: the
            // audio from the phrase's end is already ~0.4 s old) and allows a longer pause inside it
            Recorder.AutoStop = Settings.voice.autoStop || wake != 0;
            Recorder.SilenceSeconds = wake == WakeAlone || wake == WakeAgain ? WakeSilenceSeconds : TalkSilenceSeconds;
            Recorder.NoSpeechSeconds = wake == WakeAlone ? WakeWaitSeconds + 0.5f : wake == WakeAgain ? WakeAgainSeconds : 6f;
            Recorder.MaxSeconds = Mathf.Clamp(Settings.voice.maxSeconds, 3f, 30f) + (wake != 0 ? WakeWaitSeconds : 0f);
            if (!Recorder.StartAt(fromPos)) { VoiceDone(new VoiceResult { Error = "microphone: " + Recorder.LastError }, null); return; } // -1: from now (the button)
            StopSpeaking(); // TALK interrupts a reply: nothing of it plays on top of the new question
            SetVoiceState("listening", "● LISTENING", wake != 0 ? "Say a command" : "speak now");
            _vHudKey = -1;
            if (wake == WakeAlone || wake == WakeWithCommand) Sounds?.Play("wake"); // short: the user speaks right after it
            else if (wake == 0) Sounds?.Play("open");
            // load the LLM and its prompt cache once the voice is idle again (TickVoice), not while the user speaks:
            // it would run through speech-to-text and the spoken answer
            bool llmMayAnswer = Settings.voice.useLlm && (Settings.voice.llmFallback || !Settings.voice.decide || Decider == null || !Decider.Usable);
            if (llmMayAnswer && _warmedRevision != Voice.Revision && Qvac.IsRunning) _warmWanted = true;
        }

        public void VoiceStop()
        {
            if (!Recorder.Recording) { StopSpeaking(); return; }
            if (Dictating) { StopDictation(); return; } // ◉ dictation into a text field (ShellThreadView)
            bool heard = Recorder.HeardSpeech;
            var wav = Recorder.Stop();
            if (wav == null && _wakeTalk) { WakeQuietEnd(" (stopped)", false); return; }
            if (wav == null) { VoiceDone(new VoiceResult { Error = heard ? "too short" : "nothing recorded" }, null); return; }
            VoiceProcessWav(wav, null);
        }

        public void VoiceCancel()
        {
            _wakeTalk = false;
            Recorder.Cancel();
            StopSpeaking();
            if (Dictating) { _dictateTo = null; _dictField = null; RestoreRecorder(); }
            SetVoiceState("idle", "", "");
            if (_vHud != null && _vHud.IsOpen) _vHud.Close();
        }

        private void OnRecorderFinished(byte[] wav)
        {
            if (Dictating) { DictationWav(wav); return; }
            if (wav == null && _wakeTalk) { WakeQuietEnd($" (no speech in {Recorder.NoSpeechSeconds:F1} s)"); return; } // "Hey Whisp" and then nothing: harmless
            if (wav == null) { VoiceDone(new VoiceResult { Error = "no speech heard" }, null); return; }
            VoiceProcessWav(wav, null);
        }

        /// Speech (a WAV) -> transcript -> command -> action.
        public void VoiceProcessWav(byte[] wav, Action<VoiceResult> done)
        {
            float t0 = Time.realtimeSinceStartup;
            bool fromWake = _wakeTalk;
            _wakeTalk = false;
            if (Qvac == null || !Qvac.IsRunning)
            {
                EnsureVoiceService();
                VoiceDone(new VoiceResult { Started = t0, Error = "the AI service is " + (Qvac?.State.ToString().ToLowerInvariant() ?? "off") + ": " + Qvac?.Status }, done);
                return;
            }
            SetVoiceState("transcribing", "◌ TRANSCRIBING", "");
            Qvac.Client.Transcribe(wav, null, (r, e) =>
            {
                int asr = (int)((Time.realtimeSinceStartup - t0) * 1000f);
                if (e != null) { VoiceDone(new VoiceResult { Started = t0, AsrMs = asr, Error = "speech-to-text: " + e }, done); return; }
                string heard = (string)r["text"] ?? "";
                // the phrase and its mis-hearings dropped from the front ("a whisper, open the panels" -> "open the
                // panels"); only the phrase is never a command (it ran set_wake_word: session log 2026-11-16)
                string text = WakeWord.Command(heard, fromWake);
                if (text.Length == 0 && (fromWake || WakeWord.IsOnlyPhrase(heard)))
                {
                    if (done != null) { VoiceDone(new VoiceResult { Started = t0, AsrMs = asr, Transcript = heard.Trim(), Error = "only the wake phrase, no command" }, done); return; }
                    if (WakeListenAgain(heard)) return;
                    WakeQuietEnd($" (heard only \"{heard.Trim()}\")");
                    return;
                }
                if (text != heard.Trim()) Log($"wake: command \"{text}\" (heard \"{heard.Trim()}\")");
                VoiceProcessText(text, done, asr, t0);
            });
        }

        /// Text (spoken or typed) -> command -> action. The fast path answers without the LLM.
        public void VoiceProcessText(string text, Action<VoiceResult> done, int asrMs = 0, float t0 = -1f)
        {
            var res = new VoiceResult { Transcript = (text ?? "").Trim(), AsrMs = asrMs, Started = t0 >= 0f ? t0 : Time.realtimeSinceStartup };
            if (res.Transcript.Length == 0) { res.Error = "heard nothing"; VoiceDone(res, done); return; }
            res.Transcript = WithReference(res.Transcript, out var refNote); // "reopen it": the window acted on last (ShellContext)
            if (refNote != null) res.Corrected = refNote;
            var fast = Voice.FastMatchAll(res.Transcript);
            // a question that names a panel ("how many captures...") is answered, not run (AskInsteadOf)
            if (fast != null && AskInsteadOf(fast, res.Transcript)) { res.Via = "fast+context"; VoiceAskSession(res, done); return; }
            if (fast != null) { VoiceExecuteAll(fast, res, done); return; }
            if (Settings.voice.decide && Decider != null && Decider.Usable && (_deciderTest || Qvac != null && Qvac.IsRunning))
            {
                float d0 = Time.realtimeSinceStartup;
                Decider.Decide(res.Transcript, dr =>
                {
                    res.DecideMs = (int)((Time.realtimeSinceStartup - d0) * 1000f);
                    Log("voice decide: " + dr);
                    if (dr.Error == null && dr.Matches.Count > 0 && AskInsteadOf(dr.Matches, res.Transcript)) { res.Via = "decide+context"; VoiceAskSession(res, done); return; }
                    if (dr.Error == null && dr.Matches.Count > 0) { VoiceExecuteAll(dr.Matches, res, done); return; }
                    if (IsInterpretation(res.Transcript)) { res.Via = "decide+context"; VoiceAskSession(res, done); return; }
                    if (Settings.voice.useLlm && Settings.voice.llmFallback && dr.Error == null) { VoiceAskLlm(res, done); return; }
                    if (dr.Error != null && Settings.voice.useLlm) { VoiceAskLlm(res, done); return; }
                    res.Via = "decide";
                    res.Error = dr.Error ?? "no command for that";
                    VoiceDone(res, done);
                });
                return;
            }
            if (IsInterpretation(res.Transcript)) { res.Via = "context"; VoiceAskSession(res, done); return; }
            VoiceAskLlm(res, done);
        }

        /// The LLM picks the command line (grammar-bound; the decision model's fallback, or the path when it is off).
        private void VoiceAskLlm(VoiceResult res, Action<VoiceResult> done)
        {
            if (!Settings.voice.useLlm) { res.Error = "no command matches (the LLM is off)"; VoiceDone(res, done); return; }
            if (Qvac == null || !Qvac.IsRunning) { res.Error = "no command matches, and the AI service is not running"; VoiceDone(res, done); return; }
            SetVoiceState("thinking", "◆ THINKING", "“" + Short(res.Transcript, 70) + "”");
            float l0 = Time.realtimeSinceStartup;
            var msgs = (JArray)VoicePrefix().DeepClone();
            msgs.Add(new JObject { ["role"] = "user", ["content"] = res.Transcript });
            var body = new JObject { ["messages"] = msgs, ["grammar"] = _voiceGrammar, ["max_tokens"] = 48, ["temperature"] = 0 };
            Qvac.Client.Chat(body, (r, e) =>
            {
                res.LlmMs = (int)((Time.realtimeSinceStartup - l0) * 1000f);
                res.Via = "llm";
                if (e != null) { res.Error = "LLM: " + e; VoiceDone(res, done); return; }
                string line = ((string)r["choices"]?[0]?["message"]?["content"] ?? "").Trim();
                var ms = Voice.ParseLines(line, out string perr);
                if (ms == null) { res.Error = perr; VoiceDone(res, done); return; }
                res.Command = string.Join(" + ", ms.Select(x => x.Command.Name));
                foreach (var m2 in ms)
                {
                    var verr = Voice.Validate(m2.Command, m2.Args);
                    if (verr != null) { res.Args = m2.Args.ToString(Newtonsoft.Json.Formatting.None); res.Error = verr; VoiceDone(res, done); return; }
                }
                VoiceExecuteAll(ms, res, done);
            });
        }

        private void VoiceExecute(VoiceMatch m, VoiceResult res, Action<VoiceResult> done) => VoiceExecuteAll(new List<VoiceMatch> { m }, res, done);

        /// Runs one command or several said together, in order; their replies are joined. A command that needs
        /// confirming asks first (the next ones wait); the first error stops the rest.
        private void VoiceExecuteAll(List<VoiceMatch> ms, VoiceResult res, Action<VoiceResult> done)
        {
            _voiceUtterance = res.Transcript; // ask_about_session answers the whole question
            res.Via = ms[0].Via;
            res.Command = string.Join(" + ", ms.Select(x => x.Command.Name));
            res.Args = ms.Count == 1 ? ms[0].Args.ToString(Newtonsoft.Json.Formatting.None) : new JArray(ms.Select(x => (JToken)x.Args)).ToString(Newtonsoft.Json.Formatting.None);
            res.Answer = ms.Any(x => x.Command.SpeakReply);
            if (ms.Any(x => x.Corrected != null)) res.Corrected = string.Join(" · ", ms.Where(x => x.Corrected != null).Select(x => x.Corrected));
            var replies = new List<string>();
            int next = 0;
            bool finished = false;
            void Finish(string error)
            {
                if (finished) return;
                finished = true;
                if (error != null) res.Error = error;
                res.Reply = replies.Count > 0 ? string.Join(" · ", replies) : res.Reply;
                VoiceDone(res, done);
            }
            void RunNext()
            {
                if (finished) return;
                if (next >= ms.Count) { Finish(null); return; }
                var m = ms[next++];
                bool answered = false;
                void Apply(string reply)
                {
                    if (answered || finished) return;
                    answered = true;
                    if (reply != null && reply.StartsWith("error:")) { Finish(reply.Substring(6).Trim()); return; }
                    if (!string.IsNullOrEmpty(reply)) replies.Add(reply);
                    RunNext();
                }
                void Go()
                {
                    try
                    {
                        if (m.Command.RunAsync != null)
                        {
                            After(20f, () => Apply("error: " + m.Command.Name + " gave no answer in 20 s"));
                            m.Command.RunAsync(m.Args, reply => _main.Enqueue(() => Apply(reply)));
                        }
                        else Apply(m.Command.Run(m.Args));
                    }
                    catch (Exception ex) { Apply("error: " + ex.Message); }
                }
                if (m.Command.Confirm)
                {
                    string args = m.Args.ToString(Newtonsoft.Json.Formatting.None);
                    SetVoiceState("thinking", "? CONFIRM", m.Command.Name + " " + args);
                    Modal("VOICE COMMAND", $"Run <b>{m.Command.Name}</b> {args}?\n\nyou said: “{res.Transcript}”", new[] { "RUN", "CANCEL" },
                        i => { if (i == 0) Go(); else Apply("Cancelled"); });
                    return;
                }
                Go();
            }
            RunNext();
        }

        private void VoiceDone(VoiceResult res, Action<VoiceResult> done)
        {
            if (res.Started < 0f) res.Started = Time.realtimeSinceStartup;
            res.TotalMs = (int)((Time.realtimeSinceStartup - res.Started) * 1000f);
            res.At = DateTime.Now;
            VoiceRecent.Insert(0, res);
            while (VoiceRecent.Count > 20) VoiceRecent.RemoveAt(VoiceRecent.Count - 1);
            Log("voice: " + res);
            string said = res.Transcript.Length > 0 ? "“" + Short(res.Transcript, 70) + "”" : "";
            bool sessionAnswer = res.Ok && res.Command == "ask_about_session" && !string.IsNullOrEmpty(res.Reply);
            if (sessionAnswer) SetVoiceState("done", "◆ ANSWER", Short(res.Reply, 260) + $"\n<size=80%>{said} · {res.TotalMs} ms</size>");
            else if (res.Ok) SetVoiceState("done", "✓ " + Short(res.Reply ?? res.Command ?? "done", 40), said + (res.Command != null ? $"\n{res.Command} · {res.Via} · {res.TotalMs} ms" : ""));
            else SetVoiceState("error", "⚠ " + Short(res.Error, 46), said);
            Sounds?.Play(res.Ok ? "toast" : "error");
            EndBoostSoon();
            _vHudCloseAt = Time.unscaledTime + (sessionAnswer ? Mathf.Clamp(4f + res.Reply.Length / 16f, 6f, 16f) : res.Ok ? 2.5f : 4.5f);
            bool spoken = sessionAnswer && _sessionAnswerSpoken; // spoken sentence by sentence as it was written
            _sessionAnswerSpoken = false;
            if (!spoken && res.Ok && res.Answer && (Settings.voice.speak || sessionAnswer && Settings.voice.speakAnswers) && !string.IsNullOrEmpty(res.Reply)) SayReply(res.Reply);
            RefreshVoicePanel();
            done?.Invoke(res);
            VoiceState = "idle";
        }

        // ------------------------------------------------------------------ spoken replies, sentence by sentence

        /// The pause between two sentences of one reply (seconds).
        public const float SentenceGap = 0.2f;
        /// A reply speaks at most this much (the full text stays on the HUD): synthesis time grows with every character.
        public const int SpokenSentences = 2, SpokenChars = 200;
        /// The CPU Boost is held while a reply speaks, but never longer than this from its start.
        public const float SpeechBoostCap = 60f;

        private readonly List<string> _sayParts = new List<string>(); // the sentences of the reply being spoken
        private int _sayNext;                                          // the next sentence to request
        private int _saySplit;                                         // 1 when the first sentence was cut in two clips (the spoken cap counts it once)
        private bool _sayInFlight;                                     // one request at a time: Stop is quick, the service queue stays short
        private readonly Queue<AudioClip> _sayReady = new Queue<AudioClip>();
        private bool _sayPlaying;
        private float _sayNextAt, _sayBoostUntil;
        private int _sayGen;                                           // bumped by Stop / a new reply / TALK: late clips of an older one are dropped
        private Action<string> _sayDone;
        private string _sayVoice;
        private VoiceFxPreset _sayFx;
        private float _sayIdleSince = -1f; // when the last reply ended (the effect's scratch buffers go after a while)
        /// The short line VOICE > SOUND says when the effect changes.
        public const string VoiceFxSample = "Systems online. All panels ready.";
        private int _sayClips;
        /// Tests: clips played since the reply started.
        public int SpeechClipsPlayedForTest => _sayClips;

        /// Tests: speech comes from here instead of the AI service ((text, callback(wav, error))).
        public Action<string, Action<byte[], string>> SpeakForTest;
        /// A reply is being spoken: playing, waiting for the gap, or sentences still being made.
        public bool Speaking => _sayPlaying || _sayInFlight || _sayReady.Count > 0 || _sayNext < _sayParts.Count || _sayStreaming;
        /// Tests: the sentences of the reply being spoken, how many were requested, how many clips wait to play.
        public IReadOnlyList<string> SpeechPartsForTest => _sayParts;
        public int SpeechRequestedForTest => _sayNext;
        public int SpeechReadyForTest => _sayReady.Count;
        public bool SpeechPlayingForTest => _sayPlaying;
        public AudioClip SpeechClipForTest => _voiceAudio != null ? _voiceAudio.clip : null;
        public void TickSpeechForTest(float now) => TickSpeech(now);

        /// Speaks a reply: its first sentences only (SpokenSentences, about SpokenChars), the rest stays on the HUD.
        public void SayReply(string reply) => Say(SpokenText.ForSpeech(reply, SpokenSentences, SpokenChars), reply, null);

        // ---- a reply whose sentences arrive one by one (a streamed answer): the acknowledgement plays at once, each
        // finished sentence is made and queued as it comes, SayStreamEnd closes it
        private bool _sayStreaming;
        private int _sayStreamChars;

        /// Opens a streamed reply; with `ack` the acknowledgement made ahead plays at once (when it is ready).
        public void SayStreamBegin(bool ack)
        {
            StopSpeaking();
            if (SpeakForTest == null && (Qvac == null || !Qvac.IsRunning)) return;
            _sayStreaming = true;
            _sayStreamChars = 0;
            _sayDone = null;
            _sayVoice = Settings.voice.voice;
            _sayFx = VoiceFx.Parse(Settings.voice.fx);
            _sayClips = 0;
            _sayBoostUntil = Time.unscaledTime + SpeechBoostCap;
            BoostCpuFor(2f);
            if (ack && _ackWav != null && _ackKey == AckKey())
            {
                var clip = VoiceRecorder.ClipFromWav(_ackWav, "speech");
                if (clip != null) { _sayReady.Enqueue(clip); TickSpeech(Time.unscaledTime); }
            }
            else if (ack) PrepareAck(); // the next question has it
        }

        /// Adds one finished sentence to the streamed reply (the spoken cap holds: SpokenSentences, about SpokenChars).
        /// False when the cap is reached or no stream is open.
        public bool SayStreamAppend(string sentence)
        {
            if (!_sayStreaming) return false;
            string sp = SpokenText.Speakable(sentence ?? "");
            if (sp.Length == 0) return true;
            if (_sayParts.Count - _saySplit >= SpokenSentences || _sayParts.Count > 0 && _sayStreamChars + sp.Length > SpokenChars) return false;
            _sayParts.Add(sp);
            _sayStreamChars += sp.Length;
            RequestNextSentence();
            return true;
        }

        /// Closes the streamed reply: what is queued still plays.
        public void SayStreamEnd() => _sayStreaming = false;
        public bool SayStreamingForTest => _sayStreaming;

        /// Speaks `text` with the local text-to-speech model (shows it when that is not installed), one sentence at a
        /// time: the first plays as soon as it is made while the next one is made. `done` gets null when the first
        /// sentence starts playing, or the error.
        public void VoiceSay(string text, Action<string> done = null) => Say(SpokenText.Sentences(text), text, done);

        private void Say(List<string> sentences, string text, Action<string> done)
        {
            if (string.IsNullOrWhiteSpace(text) || sentences.Count == 0) { done?.Invoke("empty"); return; }
            if (SpeakForTest == null && (Qvac == null || !Qvac.IsRunning)) { Toast(text, "info"); done?.Invoke("the AI service is not running"); return; }
            StopSpeaking(); // a new reply replaces what is still being said
            foreach (var x in sentences) { string sp = SpokenText.Speakable(x); if (sp.Length > 0) _sayParts.Add(sp); }
            if (_sayParts.Count == 0) { done?.Invoke("empty"); return; }
            _sayDone = done;
            _sayVoice = Settings.voice.voice;
            _sayFx = VoiceFx.Parse(Settings.voice.fx);
            _sayClips = 0;
            _sayBoostUntil = Time.unscaledTime + SpeechBoostCap;
            BoostCpuFor(2f); // held while it speaks (TickSpeech), up to SpeechBoostCap
            RequestNextSentence();
        }

        /// Stops the reply being spoken: the clip playing, the ones waiting and the ones still being made (they are
        /// dropped when they arrive). Stop, TALK, a new reply and voice:cancel call it.
        public void StopSpeaking()
        {
            _sayGen++;
            _sayStreaming = false;
            var cts = _answerCts; // a streamed answer still being written stops with its voice
            _answerCts = null;
            try { cts?.Cancel(); } catch (Exception) { }
            _sayParts.Clear();
            _sayNext = 0;
            _saySplit = 0;
            _sayInFlight = false;
            _sayNextAt = 0f; // the next reply starts at once
            while (_sayReady.Count > 0) DestroyClip(_sayReady.Dequeue());
            if (_voiceAudio != null)
            {
                if (_voiceAudio.isPlaying) _voiceAudio.Stop();
                var c = _voiceAudio.clip;
                _voiceAudio.clip = null;
                DestroyClip(c);
            }
            if (_sayPlaying) _speechT = -1f; // the orb stops speaking with it
            _sayPlaying = false;
            var d = _sayDone;
            _sayDone = null;
            d?.Invoke("stopped");
        }

        private void RequestNextSentence()
        {
            // one request at a time and at most two clips made ahead: a Stop never waits for a long queue in the service
            if (_sayInFlight || _sayNext >= _sayParts.Count || _sayReady.Count >= 2) return;
            int gen = _sayGen, i = _sayNext++;
            if (i == 0)
            {
                // a long first sentence is made as two clips: the first one, short, decides when the answer starts
                string head = SpokenText.SplitFirstClause(_sayParts[0], out string tail);
                if (tail != null) { _sayParts[0] = head; _sayParts.Insert(1, tail); _saySplit = 1; }
            }
            string text = _sayParts[i];
            _sayInFlight = true;
            float t0 = Time.realtimeSinceStartup;
            void Fail(string e)
            {
                Log("[ai] speech: " + e);
                if (e.StartsWith("409")) Toast("Spoken replies need the voice model: VOICE > INSTALL", "warn");
                var d = _sayDone;
                _sayDone = null;
                // what was made still plays; nothing after the failed sentence does
                _sayParts.RemoveRange(i, _sayParts.Count - i);
                _sayNext = _sayParts.Count;
                d?.Invoke(e);
            }
            bool first = i == 0, last = !_sayStreaming && i == _sayParts.Count - 1; // a stream's last sentence is not known yet
            var fx = _sayFx;
            Action<byte[], string> got = (wav, e) =>
            {
                if (gen != _sayGen) return; // stopped or replaced meanwhile: nothing of it plays
                _sayInFlight = false;
                if (e != null || wav == null) { Fail(e ?? "no audio"); return; }
                var clip = VoiceRecorder.ClipFromWav(wav, "speech");
                if (clip == null) { Fail("bad audio"); return; }
                if (i == 0) Log($"[ai] speech: first sentence ready in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms ({clip.length:F1} s, {_sayParts.Count} sentence(s))");
                _sayReady.Enqueue(clip);
                TickSpeech(Time.unscaledTime); // the first one plays at once
                RequestNextSentence();
            };
            // tests: the effect runs here on the main thread; on the device it runs on the request's worker thread
            if (SpeakForTest != null) SpeakForTest(text, (w, e) => got(w != null ? VoiceFx.ProcessWav(w, fx, first, last) : null, e));
            else Qvac.Client.Speak(text, _sayVoice, fx, first, last, got);
        }

        /// Plays the next clip after the gap, holds the CPU Boost while a reply speaks, and frees each clip once played.
        private float _sayPlayStart, _sayPlayLen; // when the clip playing started and how long it is

        private void TickSpeech(float now)
        {
            // a clip is over when the source stops, or at the latest its length (+0.3 s) after it started: audio that
            // stalls (no audio device, as in the batch Editor, or a stuck output) never blocks the rest of a reply
            if (_sayPlaying && (_voiceAudio == null || !_voiceAudio.isPlaying || now - _sayPlayStart > _sayPlayLen + 0.3f))
            {
                if (_voiceAudio != null && _voiceAudio.isPlaying) _voiceAudio.Stop();
                _sayPlaying = false;
                if (_voiceAudio != null) { var c = _voiceAudio.clip; _voiceAudio.clip = null; DestroyClip(c); }
                _sayNextAt = now + SentenceGap;
                RequestNextSentence();
            }
            if (!_sayPlaying && _sayReady.Count > 0 && now >= _sayNextAt)
            {
                var next = _sayReady.Dequeue();
                _sayPlayStart = now;
                _sayPlayLen = next != null ? next.length : 0f;
                PlayClip(next);
            }
            if (Speaking && Time.unscaledTime < _sayBoostUntil) _boostUntil = Mathf.Max(_boostUntil, Time.unscaledTime + 0.5f);
            if (Speaking) _sayIdleSince = -1f;
            else if (_sayIdleSince < 0f) _sayIdleSince = now;
            else if (now - _sayIdleSince > 120f) { VoiceFx.ReleaseScratch(); _sayIdleSince = float.MaxValue; } // once, 2 min after the last reply
        }

        private void PlayClip(AudioClip clip)
        {
            if (_voiceAudio == null)
            {
                var go = new GameObject("VoiceAudio");
                go.transform.SetParent(_root, false);
                _voiceAudio = go.AddComponent<AudioSource>();
                _voiceAudio.spatialBlend = 0f;
                _voiceAudio.playOnAwake = false;
            }
            var old = _voiceAudio.clip;
            _voiceAudio.volume = 0.9f;
            _voiceAudio.clip = clip;
            if (old != null && old != clip) DestroyClip(old);
            _sayClips++;
            OnSpeechStarted(clip); // the orb speaks it: from its place, pulsing with the words (per sentence)
            _voiceAudio.Play();
            _sayPlaying = true;
            var d = _sayDone;
            _sayDone = null;
            d?.Invoke(null);
            RequestNextSentence();
        }

        private static void DestroyClip(AudioClip c)
        {
            if (c == null) return;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        /// gesture bindings, Lua app.action and the control port: voice:listen | stop | toggle | cancel | say:<text> | run:<text>
        private string VoiceAction(string arg)
        {
            int c = arg.IndexOf(':');
            string sub = (c < 0 ? arg : arg.Substring(0, c)).Trim().ToLowerInvariant();
            string rest = c < 0 ? "" : arg.Substring(c + 1);
            switch (sub)
            {
                case "": case "toggle": VoiceToggle(); return null;
                case "listen": case "start": VoiceListen(); return null;
                case "stop": VoiceStop(); return null;
                case "cancel": VoiceCancel(); return null;
                case "say": VoiceSay(rest); return null;
                case "run": VoiceProcessText(rest, null); return null;
                default: return "voice:listen|stop|toggle|cancel|say:<text>|run:<text>";
            }
        }

        // ------------------------------------------------------------------ the shell's own commands

        private void RegisterShellVoiceCommands()
        {
            var panelSyn = new Dictionary<string, string>
            {
                ["provider"] = "agents", ["providers"] = "agents", ["agent"] = "agents", ["ai"] = "agents", ["models"] = "agents", ["sign in"] = "agents", ["login"] = "agents",
                ["thread"] = "threads", ["conversations"] = "threads", ["chats"] = "threads", ["sessions"] = "threads", ["projects"] = "threads", ["workspaces"] = "workspaces", ["worktrees"] = "workspaces", ["worktree"] = "workspaces", ["checkouts"] = "workspaces",
                ["browser"] = "web", ["internet"] = "web", ["website"] = "web", ["web browser"] = "web", ["web page"] = "web", ["page"] = "web", ["site"] = "web",
                ["bookmarks"] = "links", ["local servers"] = "links", ["dev servers"] = "links", ["bookmark"] = "links",
                ["setup"] = "settings", ["preferences"] = "settings", ["options"] = "settings", ["config"] = "settings", ["configuration"] = "settings",
                ["control panel"] = "control", ["control center"] = "control", ["control centre"] = "control",
                ["display settings"] = "display", ["screen settings"] = "display", ["layout settings"] = "display", ["look and sound"] = "look", ["appearance"] = "look", ["look settings"] = "look",
                ["hand settings"] = "hands", ["hands settings"] = "hands", ["version"] = "about", ["licences"] = "about", ["licenses"] = "about", ["app info"] = "about",
                ["gesture"] = "gestures", ["gesture studio"] = "gestures", ["hand gestures"] = "gestures",
                ["captures"] = "clipboard", ["capture"] = "clipboard", ["screenshots"] = "clipboard", ["screenshot"] = "clipboard", ["screen captures"] = "clipboard",
                ["image clipboard"] = "clipboard", ["snapshots"] = "clipboard", ["clips"] = "clipboard",
                ["skill"] = "skills", ["agent skills"] = "skills", ["plugin"] = "plugins", ["extensions"] = "mqh", ["harness"] = "mqh", ["daemon"] = "mqh", ["m q h"] = "mqh",
                ["script"] = "scripts", ["lua"] = "scripts", ["lua scripts"] = "scripts",
                ["secrets"] = "vault", ["passwords"] = "vault", ["keystore"] = "vault", ["key vault"] = "vault", ["api keys"] = "vault", ["tokens"] = "vault", ["api key"] = "vault",
                ["file"] = "files", ["explorer"] = "files", ["file manager"] = "files", ["folders"] = "files", ["file browser"] = "files",
                ["repository"] = "git", ["repo"] = "git", ["source control"] = "git", ["branches"] = "git", ["commits"] = "git", ["get"] = "git", ["gift"] = "git", ["jet"] = "git",
                ["terminal list"] = "terminals", ["consoles"] = "terminals", ["shells"] = "terminals",
                ["microphone"] = "voice", ["voice control"] = "voice", ["assistant"] = "voice", ["speech"] = "voice",
                ["disk"] = "storage", ["disk space"] = "storage", ["space"] = "storage", ["installed tools"] = "tools", ["packages"] = "tools", ["linux"] = "system",
                ["ambient focus"] = "focus", ["focus mode"] = "focus", ["focus settings"] = "focus", ["ambient"] = "focus", ["the dome"] = "focus",
                ["console"] = "terminals", ["commit"] = "git", ["branch"] = "git", ["account"] = "agents", ["accounts"] = "agents", ["chat"] = "threads", ["password"] = "vault",
                // CONTROL > WRIST MENU (ShellWristSettings); a bare "wrist menu" stays the on / off setting (set_setting wrist_menu)
                ["wrist menu settings"] = WristSettingsId, ["radial menu settings"] = WristSettingsId, ["wrist settings"] = WristSettingsId,
                ["radial settings"] = WristSettingsId, ["watch menu settings"] = WristSettingsId, ["wrist menu options"] = WristSettingsId,
                ["radial menu options"] = WristSettingsId, ["wrist menu layout"] = WristSettingsId, ["radial menu layout"] = WristSettingsId,
            };
            string Label(string id) => id == WristSettingsId ? "WRIST MENU" : id.ToUpperInvariant();

            Voice.Register(new VoiceCommand
            {
                Name = "new_terminal",
                Description = "Open a new Linux terminal",
                Phrases = new[] { "(open|start|create|new|launch|give me) [a|another] [new] (terminal|shell|console)", "new (terminal|shell)" },
                Samples = new[]
                {
                    "i need a new terminal", "spawn another shell", "open one more console", "can i get a fresh terminal", "add a terminal window",
                },
                Run = a => { TerminalNew(); return "New terminal"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "close_all_panels",
                Description = "Close every open panel and window (terminals stay)",
                Phrases = new[]
                {
                    "close (all|every|everything) [of] [the|my] [windows|panels|pages|tabs|window|panel]",
                    "(hide|clear|clean) (all|everything) [the] [windows|panels]",
                    "(clear|clean) [up] [the|my] (view|space|desk|workspace|screen)",
                },
                Samples = new[]
                {
                    "get rid of all the windows", "close all of it", "shut every panel", "clean up my space", "remove all the panels",
                },
                Run = a => { int n = CloseAllPanels(); return n == 0 ? "Nothing to close" : $"Closed {n} panel{(n == 1 ? "" : "s")}"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "open_panel",
                Description = "Open a panel to see or manage something: terminals (open terminals), files (folders), git (repositories, branches, commits), " +
                    "agents (AI providers: which are signed in, logins, accounts, models), threads (agent conversations, workspaces, projects), web (the browser: web pages, localhost), links (bookmarks and local dev servers), " +
                    "clipboard (captures, screenshots of windows), mqh (skills, plugins, scripts, gestures, the harness), skills (the agents' skills), plugins (MQH and gesture plugins), scripts (Lua scripts), " +
                    "gestures (hand gestures), vault (secrets, API keys), settings (look, layout, hands), voice (voice control, AI models), workspaces (the folders threads work in: checkout, worktrees, merging), control (the control center, on its last page), display (layout, distance, scale, the wrist menu), look (look and sound: opacity, sounds, the Whisp), hands (click, pinch, ray, clap, the select tool), about (version, licences, app permissions), " +
                    "wrist-settings (the wrist menu's settings: its two rings and what is on them, its size, text size, glass, what its core shows)",
                Parameters = VoiceCommands.Schema(("panel", "string", VoicePanels, "the panel to open", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>> { ["panel"] = panelSyn },
                Phrases = new[]
                {
                    "(open|show|display|bring up|pull up|launch) [me] [the|my] {panel} [panel|page|window|tab|screen|list|app|section]",
                    "(go|switch|take me) to [the|my] {panel} [panel|page|window|tab|screen|section]",
                    "{panel} (panel|page|window|tab)",
                    "where (are|is) [the|my] {panel}",
                },
                Samples = new[]
                {
                    "let me see my files", "pull up the git history", "i want to check the agents", "take me to the settings page", "display the vault",
                    "i want to rearrange the tiles of my wrist menu",
                },
                Run = a => { string id = (string)a["panel"]; return OpenPanel(id) ? "Opened " + Label(id) : "error: no panel " + id; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "close_panel",
                Description = "Close one panel",
                Parameters = VoiceCommands.Schema(("panel", "string", VoicePanels, "the panel to close", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>> { ["panel"] = panelSyn },
                Phrases = new[] { "(close|hide|dismiss) [the|my] {panel} [panel|page|window|tab|screen|section]" },
                Samples = new[]
                {
                    "put away the files", "close the git window", "dismiss the settings", "i am done with the vault",
                },
                Run = a => { string id = (string)a["panel"]; return ClosePanel(id) ? "Closed " + Label(id) : "error: no panel " + id; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "find_thread",
                Description = "Find the agent thread (conversation) about a topic and open it (window: new = in a new window beside the others, this = in place of the focused window's thread; default: a new window when the focused one shows another thread)",
                Parameters = VoiceCommands.Schema(("query", "string", null, "words about what was done in the thread", true), ("window", "string", new[] { "new", "this" }, "where it opens: new = a new window, this = the focused window", false)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["window"] = new Dictionary<string, string> { ["another"] = "new", ["separate"] = "new", ["second"] = "new", ["same"] = "this", ["current"] = "this", ["here"] = "this" },
                },
                Phrases = new[]
                {
                    "(open|show|find) [me] [the] (thread|conversation|chat) [about|on|where] {query} in [a|another] (new|separate|second) window {window:new}",
                    "(open|show|find) [me] [the] (thread|conversation|chat) [about|on|where] {query} in (this|the same|the current) window {window:this}",
                    "(find|search|look for|open|show|show me|go to) [me] [the|a|that|our] (thread|conversation|chat|session) [where|in which|about|on|for|with|that|when] {query}",
                    "(search|find|look) [in|through] [the|my] (threads|conversations|chats) [for|about] {query}",
                    "where did we {query}",
                    "(which|what) (thread|conversation|chat|session) (was|is|were) (about|on|for) {query}",
                    "[the] (thread|conversation|chat) (about|on) {query}",
                },
                Samples = new[]
                {
                    "find the conversation about the database migration", "which thread talked about docker", "look up the chat where we fixed the login",
                    "search my threads for payments", "open the thread about the login page in a new window",
                },
                RunAsync = (a, reply) => VoiceFindThread((string)a["query"], reply, (string)a["window"]),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "switch_workspace",
                Description = "Switch the current workspace (MQH project) by name; THREADS, FILES and GIT follow it. Without a name, opens the list",
                Parameters = VoiceCommands.Schema(("name", "string", null, "the workspace (project) name", false)),
                Phrases = new[]
                {
                    "(switch|change|go|move|jump) [over] [to] [the] (workspace|project) [called|named|to] {name}",
                    "(switch|change|go|move|jump) to [the] {name} (workspace|project)",
                    "(open|use) [the] (workspace|project) [called|named] {name}",
                    "(switch|change) [the] (workspace|workspaces|project|projects)",
                },
                Samples = new[]
                {
                    "go to the project called atlas", "switch to the website workspace", "change project to backend", "open the workspace named demo",
                },
                RunAsync = (a, reply) => VoiceSwitchWorkspace((string)a["name"], reply),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "new_thread",
                Description = "Start a new agent thread in the current project with the last agent and model chosen. With a task it starts at once (the task is its first message); without one an empty thread opens and your first message starts it",
                Parameters = VoiceCommands.Schema(("task", "string", null, "what the agent should do: the thread's first message", false)),
                Phrases = new[]
                {
                    "(start|begin|create|make) [me] [a|an] [new] (thread|conversation|chat) [with an agent] (to|that|for|about|and) {task}",
                    "(new|another) (thread|conversation|chat) (to|for|about|that) {task}",
                    "(have|get) an agent [to] {task}",
                    "(start|begin|create|make|open) [me] [a|an] new (thread|conversation|chat)",
                    "(start|begin|create|make) [me] [a|an] (thread|conversation|chat)",
                    "new (thread|conversation|chat)",
                },
                Samples = new[]
                {
                    "start a thread to fix the login page", "I want a fresh conversation with an agent", "begin a chat that writes the readme",
                    "make a new thread for the settings screen", "get an agent to clean up the css",
                },
                Examples = new List<(string, string)> { ("kick off a thread that adds dark mode", "new_thread adds dark mode"), ("give me an empty thread", "new_thread") },
                Run = a => NewThreadFromVoice((string)a["task"]),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "new_project",
                Description = "Make a new project in metacodex-projects with git: type native (a Quest app), default (any other coding) or research (notes and findings); the name becomes its folder. What is not said is asked",
                Parameters = VoiceCommands.Schema(("type", "string", ProjectKinds.Types, "the project type", false), ("name", "string", null, "the project name", false)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["type"] = new Dictionary<string, string>
                    {
                        ["native app"] = "native", ["app"] = "native", ["quest"] = "native", ["quest app"] = "native", ["headset app"] = "native", ["vr app"] = "native", ["vr"] = "native",
                        ["general"] = "default", ["coding"] = "default", ["code"] = "default", ["normal"] = "default", ["standard"] = "default", ["regular"] = "default", ["plain"] = "default",
                        ["study"] = "research", ["notes"] = "research", ["investigation"] = "research", ["reading"] = "research",
                    },
                },
                Phrases = new[]
                {
                    "(create|make|add|start|set up) [me] [a|an] [new] {type} project [called|named] {name}",
                    "(create|make|add|start|set up) [me] [a|an] [new] project (called|named) {name}",
                    "(create|make|add|start|set up) [me] [a|an] [new] {type} project",
                    "(create|make|add|start|set up) [me] [a|an] [new] project",
                    "new {type} project [called|named] {name}",
                    "new project (called|named) {name}",
                    "new project",
                },
                Samples = new[]
                {
                    "create a research project called solar panels", "make a new native app project named weather", "I need a new project",
                    "set up a default project named website", "start a project called notes app",
                },
                Examples = new List<(string, string)> { ("set up a research project about battery chemistry", "new_project research battery chemistry"), ("I want to begin a fresh quest app project", "new_project native") },
                Vocabulary = new[] { "metacodex-projects" },
                Check = a => a["name"] != null && ProjectKinds.FolderName((string)a["name"]).Length == 0 ? "say a name with letters or digits" : null,
                Run = a => VoiceNewProject((string)a["type"], (string)a["name"]),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "set_layout",
                Description = "Panel layout: fixed (world-locked) or float (a carousel that follows you)",
                Parameters = VoiceCommands.Schema(("mode", "string", new[] { "fixed", "float" }, "the layout", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["mode"] = new Dictionary<string, string>
                    {
                        ["floating"] = "float", ["carousel"] = "float", ["follow"] = "float", ["follow me"] = "float", ["locked"] = "fixed", ["static"] = "fixed", ["pinned"] = "fixed",
                        ["world"] = "fixed", ["world locked"] = "fixed", ["stay"] = "fixed", ["stay put"] = "fixed", ["in place"] = "fixed", ["lock"] = "fixed", ["pin"] = "fixed",
                    },
                },
                Phrases = new[]
                {
                    "(switch|change|set|use|go) [the] layout [to] {mode} [mode|layout]", "(use|switch to|go to) [the] {mode} (layout|mode)", "{mode} (layout|mode)",
                    "(make|let) [the|my] (panels|windows|screens) (follow me|float|float around me|come with me) {mode:float}",
                    "[the|my] (panels|windows|screens) [to] (follow me|float|come with me) [around] [me] {mode:float}",
                    "(make|let) [the|my] (panels|windows|screens) (stay|stay put|stay where they are|stop following me) {mode:fixed}",
                },
                Samples = new[]
                {
                    "make the panels stay where they are", "lock the windows in place", "let the windows follow me", "switch to the carousel layout",
                },
                Run = a => { SetLayout((string)a["mode"]); return "Layout " + ((string)a["mode"]).ToUpperInvariant(); },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "recenter",
                Description = "Bring the terminal, the dock and the panels back in front of the user",
                Phrases = new[] { "recenter", "re center", "(center|centre) [the|my] [view|panels|everything|windows|screen]", "bring [the|my|all] [the] (panels|windows|everything) [back] [here|to me|in front of me]", "reset [the] (view|position|layout)", "put (everything|it all|the panels|the windows|them) back [in front of me|here]" },
                Samples = new[]
                {
                    "move everything back in front of my eyes", "center the windows again", "line the panels up in front of me",
                    "bring the panels back to where i am looking",
                },
                Run = a => { RunAction("recenter"); return "Recentered"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "type_text",
                Description = "Type text into the focused terminal (no Enter)",
                Parameters = VoiceCommands.Schema(("text", "string", null, "the text to type", true)),
                Phrases = new[] { "(type|write) {text}" },
                Samples = new[] { "type echo done", "write git status", "type cd projects", "write npm run dev" },
                Llm = false, // free text goes verbatim into a terminal: only when said as "type ..."
                Run = a => { TerminalSend((string)a["text"]); return "Typed"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "run_in_terminal",
                Description = "Run a shell command in the focused terminal (the user confirms first)",
                Parameters = VoiceCommands.Schema(("command", "string", null, "the shell command line", true)),
                Confirm = true,
                Llm = false,
                Phrases = new[] { "(run|execute) [the] [command] {command} [in the terminal]" },
                Samples = new[] { "run npm install", "execute make build in the terminal", "run git pull in the terminal", "execute npm test" },
                Run = a => { TerminalSend((string)a["command"] + "\r"); return "Running " + (string)a["command"]; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "set_focus",
                Description = "Ambient focus: darken the room around you (on) or show it again (off)",
                Parameters = VoiceCommands.Schema(("state", "string", new[] { "on", "off" }, "on or off", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["state"] = new Dictionary<string, string> { ["enable"] = "on", ["start"] = "on", ["disable"] = "off", ["stop"] = "off", ["end"] = "off" },
                },
                Samples = new[]
                {
                    "darken everything around me", "no more focus mode", "i want to see the room again", "enable focus mode", "black out my surroundings",
                },
                Phrases = new[]
                {
                    "[turn|switch|set] [the] [ambient] focus [mode] {state}", "(turn|switch) {state} [the] [ambient] focus [mode]",
                    "(darken|dim|black out|blackout) [the] (room|world|surroundings|background) {state:on}", "(show|bring back) [the] (room|world|surroundings) {state:off}",
                },
                Run = a => { bool on = (string)a["state"] == "on"; SetFocus(on, false); return on ? "Focus on" : "Focus off"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "set_setting",
                Description = "Change a setting: on / off, a number (percent, metres), more / less, the voice effect (off, adjutant, starship, comm) or the speech quality (fast, clear)",
                Parameters = VoiceCommands.Schema(("setting", "string", VoiceSettingsCatalog.Select(x => x.Id).ToArray(), "which setting", true),
                    ("value", "string", null, "on, off, a number, + or -; voice_effect: off, adjutant (AI), starship (ship) or comm (radio); speech_quality: fast or clear", true)),
                // English names first: a name two settings share keeps its first (English) meaning
                Synonyms = new Dictionary<string, Dictionary<string, string>> { ["setting"] = VoiceSettingsCatalog.SelectMany(x => x.Names.Select(n => (n, x.Id)))
                    .Concat(VoiceSettingsCatalog.SelectMany(x => x.Other.Select(n => (n, x.Id)))).GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.First().Id) },
                Phrases = new[]
                {
                    "(set|change|put|make|turn|bring) [the] {setting} [to|at|up to|down to] {value}",
                    "(turn|switch) on [the] {setting} {value:on}", "(turn|switch) off [the] {setting} {value:off}",
                    "(turn|switch) [the] {setting} on {value:on}", "(turn|switch) [the] {setting} off {value:off}",
                    "(enable|activate|start) [the] {setting} {value:on}", "(disable|deactivate|stop) [the] {setting} {value:off}",
                    "(increase|raise|more|turn up) [the] {setting} {value:+}", "(decrease|lower|reduce|less|turn down) [the] {setting} {value:-}",
                    "make [the] {setting} (bigger|larger|higher|stronger|darker|wider) {value:+}", "make [the] {setting} (smaller|lower|weaker|lighter|narrower) {value:-}",
                    "{setting} [to|at] {value}",
                },
                Vocabulary = VoiceSettingsCatalog.SelectMany(x => x.Names).ToArray(),
                Examples = new List<(string, string)>
                {
                    ("make the room completely dark", "set_setting focus_intensity 100"), ("set the dome to two meters", "set_setting focus_radius 2"),
                    ("make the windows bigger", "set_setting ui_scale +"), ("mute the clicks", "set_setting sounds off"),
                    ("make your voice sound like a radio", "set_setting voice_effect comm"),
                    ("make the wrist menu a bit bigger", "set_setting wrist_menu_size +"), ("hide the battery on my wrist", "set_setting wrist_battery off"),
                },
                Samples = new[]
                {
                    "set the volume to 30 percent", "silence the interface sounds", "make the text larger", "lower the opacity", "increase the blur",
                    "set the radius to 2 meters", "turn off the hand rays", "dim the room more",
                    "switch the voice effect to starship", "give the assistant its normal voice back",
                    "make the text on the radial menu larger", "stop showing the clock on my wrist",
                },
                Check = a => VoiceSettingFits((string)a["setting"], (string)a["value"]) ? null : "that value does not fit " + (string)a["setting"],
                Run = a => SetSettingByVoice((string)a["setting"], (string)a["value"]),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "add_gesture_example",
                Description = "Install a gesture example plugin: core-poses (thumbs-up, thumbs-down, victory, horns, call-me) or swipes (swipe-left, swipe-right, swipe-up); the user confirms first",
                Parameters = VoiceCommands.Schema(("example", "string", ExamplePlugins.Keys.ToArray(), "the example plugin to install", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["example"] = new Dictionary<string, string>
                    {
                        ["core poses"] = "core-poses", ["example poses"] = "core-poses", ["hand poses"] = "core-poses", ["thumbs up poses"] = "core-poses", ["poses"] = "core-poses",
                        ["pose"] = "core-poses",
                        ["swipe"] = "swipes", ["swipe gestures"] = "swipes", ["swipes example"] = "swipes",
                    },
                },
                Phrases = new[] { "(add|install) the {example} example", "(add|install) [the] example {example}" },
                Samples = new[]
                {
                    "add the example hand poses", "install the swipe gestures example", "i want the thumbs up and victory poses", "give me the example swipes",
                },
                Vocabulary = new[] { "core-poses", "swipes", "thumbs-up", "victory", "horns", "call-me" },
                Confirm = true,
                Run = a => { var err = InstallGestureExample((string)a["example"]); return err != null ? "error: " + err : "Added the " + (string)a["example"] + " example"; },
            });
            RegisterLocalCoderVoice();
            Voice.Register(new VoiceCommand
            {
                Name = "show_keyboard",
                Description = "Show the headset's system keyboard to type into the terminal (to lay it on a table, the user turns on Settings > Experimental > Surface keyboard; the app cannot)",
                Phrases = new[]
                {
                    "(open|show|bring up) [the] keyboard", "keyboard", "(open|show) [the] keys",
                    "(open|show|bring up) [the] (table|surface|desk) keyboard", "[the] keyboard on [the] (table|desk)", "(turn on|enable|activate) [the] (table|surface) keyboard",
                },
                Samples = new[] { "can i have the keyboard", "pull up the keyboard", "i need to type something", "bring up the keys for me", "i want to type on the table", "can i use the surface keyboard" },
                Vocabulary = new[] { "surface keyboard" },
                Run = a => { ShowTerminalKeyboard(); return "Keyboard"; },
            });
            var agentSyn = new Dictionary<string, Dictionary<string, string>>
            {
                ["agent"] = new Dictionary<string, string>
                {
                    ["open code"] = "opencode", ["opencode"] = "opencode", ["open-code"] = "opencode",
                    ["claude code"] = "claude", ["claude"] = "claude", ["cloud code"] = "claude", ["clod code"] = "claude",
                    ["codex"] = "codex", ["open ai codex"] = "codex", ["openai codex"] = "codex", ["codecs"] = "codex",
                },
            };
            Voice.Register(new VoiceCommand
            {
                Name = "install_agent",
                Description = "Install (download) an AI coding agent in this headset's Linux: opencode, claude (Claude Code) or codex; it runs in a terminal",
                Parameters = VoiceCommands.Schema(("agent", "string", GetAgents.Select(a => a.Id).ToArray(), "the agent to install", true)),
                Synonyms = agentSyn,
                Phrases = new[] { "(install|get|download|add) [the] {agent} [agent|cli|app]" },
                Samples = new[] { "i want opencode on the headset", "put codex in the linux", "set up claude code for me", "download the codex cli" },
                Vocabulary = new[] { "opencode", "codex", "claude code" },
                Run = a => { var d = GetAgents.FirstOrDefault(x => x.Id == (string)a["agent"]); if (d == null) return "error: no agent " + (string)a["agent"]; OpenPanel("agents"); StartAgentInstall(d); return "Installing " + d.Name; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "agent_key",
                Description = "Add an API key for an agent (opencode, claude, codex): opens the dialog to paste it; it is sealed in the vault and given to the agents",
                Parameters = VoiceCommands.Schema(("agent", "string", GetAgents.Select(a => a.Id).ToArray(), "the agent the key is for", true)),
                Synonyms = agentSyn,
                Phrases = new[] { "(add|set|paste|enter) [an|the|my] [api] key (for|to) {agent}", "{agent} [api] key" },
                Samples = new[] { "i have a key for opencode", "let me paste my openai key for codex", "where do i put the anthropic key", "save my api key for claude" },
                Run = a => { OpenAgentKey((string)a["agent"]); return "API key for " + (string)a["agent"]; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "focus_preset",
                Description = "Ambient focus presets (turn focus on): subtle (light dimming, wide dome), focus (normal), blackout (dark all around)",
                Parameters = VoiceCommands.Schema(("preset", "string", new[] { "subtle", "focus", "blackout" }, "the preset", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["preset"] = new Dictionary<string, string>
                    {
                        ["light"] = "subtle", ["soft"] = "subtle", ["gentle"] = "subtle", ["mild"] = "subtle", ["normal"] = "focus", ["default"] = "focus", ["standard"] = "focus",
                        ["medium"] = "focus", ["total"] = "blackout", ["black out"] = "blackout", ["cinema"] = "blackout", ["pitch black"] = "blackout", ["dark"] = "blackout",
                    },
                },
                Phrases = new[] { "(use|apply|choose|pick|set|switch to) [the] {preset} [focus] preset", "{preset} [focus] preset", "(focus|ambient) preset {preset}" },
                Samples = new[]
                {
                    "use the subtle focus preset", "switch to the blackout preset", "apply the normal focus preset", "i want the gentle preset",
                    "preset cinema",
                },
                Run = a => { string p = (string)a["preset"]; ApplyFocusPreset(p); return "Focus preset " + p.ToUpperInvariant(); },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "spin_carousel",
                Description = "Float layout: turn the ring of panels left or right, by a number of panels (default 1)",
                Parameters = VoiceCommands.Schema(("direction", "string", new[] { "left", "right" }, "which way", true), ("panels", "integer", null, "how many panels (1-6)", false)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["direction"] = new Dictionary<string, string>
                    {
                        ["next"] = "right", ["forward"] = "right", ["previous"] = "left", ["back"] = "left",
                    },
                },
                Phrases = new[]
                {
                    "(spin|turn|rotate|move) [the] (carousel|ring|panels|windows) [to the|to|towards the] {direction}",
                    "(spin|rotate) [to the] {direction}", "[show me|go to] [the] {direction} (panel|window)",
                },
                Samples = new[]
                {
                    "spin the carousel to the right", "rotate the panels left", "show me the next window", "go to the previous panel",
                },
                Run = a =>
                {
                    if (Settings.layout != "float") return "error: the ring turns in FLOAT layout (say “float layout”)";
                    int n = Mathf.Clamp(a["panels"] != null ? (int)Math.Round((double)a["panels"]) : 1, 1, 6);
                    bool right = (string)a["direction"] == "right";
                    Spin((right ? -30f : 30f) * n, 0f);
                    return "Turned " + (right ? "right" : "left");
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "select_mode",
                Description = "Select tool: pinch and drag on a panel to select an area (on), or stop selecting (off)",
                Parameters = VoiceCommands.Schema(("state", "string", new[] { "on", "off" }, "on or off", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["state"] = new Dictionary<string, string> { ["enable"] = "on", ["start"] = "on", ["disable"] = "off", ["stop"] = "off", ["exit"] = "off" },
                },
                Phrases = new[]
                {
                    "(turn|switch) {state} [the] (select|selection) (mode|tool)", "[turn|switch] [the] (select|selection) (mode|tool) {state}",
                    "(start|enable|use) [the] (select|selection) (mode|tool) {state:on}", "(stop|disable|exit) [the] (select|selection) (mode|tool) {state:off}",
                    "select (an area|a region|part of a panel|something) {state:on}", "stop selecting {state:off}",
                },
                Samples = new[]
                {
                    "i want to select part of a panel", "turn on the selection tool", "let me select an area", "stop selecting",
                },
                Run = a => { bool on = (string)a["state"] != "off"; SetSelectionMode(on); return on ? "Select tool on" : "Select tool off"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "open_url",
                Description = "Open a web address in the browser (said as “github dot com slash docs”)",
                Parameters = VoiceCommands.Schema(("url", "string", null, "the address", true)),
                Llm = false, // free words become an address: only when said as "browse to ..." / "open the website ..."
                Phrases = new[] { "(browse|navigate) to {url}", "(open|visit|go to) [the] (website|site|web page|url|address) {url}" },
                Samples = new[] { "open the website github dot com", "browse to example dot org", "go to the site wikipedia dot org", "visit the web page example dot com slash docs" },
                Check = a => SpokenUrl((string)a["url"]) != null ? null : "say the address with “dot”, e.g. github dot com",
                Run = a => { string u = SpokenUrl((string)a["url"]); if (u == null) return "error: no address in that"; OpenBrowser(u); return "Opening " + u; },
            });
            // the LLM's example turns, in the order measured (docs/voice.md): requests no phrase template covers
            Voice.GeneralExamples.AddRange(new (string, string)[]
            {
                ("show me my secrets", "open_panel vault"), ("close everything", "close_all_panels"), ("I need another shell", "new_terminal"),
                ("which chat was about the login page", "find_thread login page"), ("switch over to the alpha project", "switch_workspace alpha"),
                ("let the windows float around me", "set_layout float"), ("what's the weather like", "none"), ("good morning", "none"),
                ("which models can I use", "open_panel agents"), ("show the branches", "open_panel git"), ("hide the files", "close_panel files"),
                ("bring my stuff back here", "recenter"), ("change the look of the panels", "open_panel settings"),
                ("turn on ambient focus and set the intensity to zero", "set_focus on ; set_setting focus_intensity 0"),
                ("dim the room a bit more", "set_setting focus_intensity +"), ("open the ambient focus settings", "open_panel focus"),
                ("how much disk space is left", "open_panel storage"),
            });
            Voice.Changed += () => _main.Enqueue(RefreshVoicePanel);
        }

        /// "github dot com slash docs" -> https://github.com/docs (the spoken words for . / : -); null when it has no dot.
        public static string SpokenUrl(string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string t = " " + said.Trim().ToLowerInvariant() + " ";
            foreach (var (w, c) in new[] { (" dot ", "."), (" slash ", "/"), (" colon ", ":"), (" dash ", "-") })
                t = t.Replace(w, c);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", "").Trim('.', '/');
            if (!t.Contains(".") || t.Length < 4) return null;
            if (!t.StartsWith("http://") && !t.StartsWith("https://")) t = "https://" + t;
            return Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u.ToString() : null;
        }

        // ------------------------------------------------------------------ settings by voice

        private sealed class VoiceSetting
        {
            /// Kind: bool (on / off), number (Min..Max, Unit) or choice (one of Options; + / - step through them).
            public string Id, Key, Kind, Unit, Label;
            public float Min, Max;
            /// choice: the values in order, their spoken names and the label each one shows.
            public string[] Options, OptionLabels;
            public Dictionary<string, string> Values;
            /// English names: synonyms of the setting and words the speech-to-text prompt carries.
            public string[] Names;
            /// Other names (colloquial; VoiceSettingOther): synonyms too, not in the speech-to-text prompt.
            public string[] Other => VoiceSettingOther.TryGetValue(Id, out var o) ? o : new string[0];
        }

        /// More names per setting, by id (kept apart from the catalog so the English rows stay readable).
        private static readonly Dictionary<string, string[]> VoiceSettingOther = new Dictionary<string, string[]>
        {
            ["focus_intensity"] = new[] { "room", "dark" },
            ["opacity"] = new[] { "transparent", "transparency" },
            ["ui_scale"] = new[] { "text", "bigger" },
            ["sounds"] = new[] { "click" },
            ["hand_rays"] = new[] { "laser" },
        };

        /// speech_quality and voice_effect: what people call each value (folded like the matcher folds them).
        private static readonly Dictionary<string, string> SpeechValues = new Dictionary<string, string>
        {
            ["fast"] = "fast", ["faster"] = "fast", ["quick"] = "fast", ["quicker"] = "fast", ["sooner"] = "fast", ["speedy"] = "fast", ["low"] = "fast",
            ["clear"] = "clear", ["clearer"] = "clear", ["best"] = "clear", ["high"] = "clear", ["exact"] = "clear", ["precise"] = "clear", ["slow"] = "clear", ["quality"] = "clear",
        };
        private static readonly Dictionary<string, string> VoiceEffectValues = new Dictionary<string, string>
        {
            ["off"] = "off", ["none"] = "off", ["normal"] = "off", ["plain"] = "off", ["clean"] = "off", ["natural"] = "off", ["dry"] = "off", ["no effect"] = "off",
            ["adjutant"] = "adjutant", ["ai"] = "adjutant", ["a i"] = "adjutant", ["computer"] = "adjutant", ["sci fi"] = "adjutant", ["scifi"] = "adjutant",
            ["science fiction"] = "adjutant", ["default"] = "adjutant", ["subtle"] = "adjutant", ["light"] = "adjutant", ["assistant"] = "adjutant",
            ["starship"] = "starship", ["ship"] = "starship", ["star ship"] = "starship", ["spaceship"] = "starship", ["space ship"] = "starship", ["strong"] = "starship",
            ["heavy"] = "starship", ["robot"] = "starship", ["robotic"] = "starship",
            ["comm"] = "comm", ["com"] = "comm", ["comms"] = "comm", ["radio"] = "comm", ["channel"] = "comm", ["walkie talkie"] = "comm", ["intercom"] = "comm",
            ["transmission"] = "comm",
        };

        /// Settings that voice (and the local LLM) can change: id, the settings key, kind, range, unit, spoken names.
        /// The settings voice can change (id, key, kind, range, unit, label, spoken names), for the voice bench.
        public static JArray VoiceSettingsForBench() => new JArray(VoiceSettingsCatalog.Select(x => new JObject
        {
            ["id"] = x.Id, ["key"] = x.Key, ["kind"] = x.Kind, ["min"] = x.Min, ["max"] = x.Max, ["unit"] = x.Unit, ["label"] = x.Label, ["names"] = new JArray(x.Names), ["other"] = new JArray(x.Other),
            ["options"] = x.Options != null ? new JArray(x.Options) : null, ["values"] = x.Values != null ? JObject.FromObject(x.Values) : null,
        }));

        private static readonly VoiceSetting[] VoiceSettingsCatalog =
        {
            new VoiceSetting { Id = "focus", Key = "focusEnabled", Kind = "bool", Label = "Ambient focus", Names = new[] { "ambient focus", "focus", "focus mode", "the dome", "dome" } },
            new VoiceSetting { Id = "focus_intensity", Key = "focusIntensity", Kind = "number", Min = 0f, Max = 1f, Unit = "%", Label = "Focus intensity", Names = new[] { "intensity", "focus intensity", "ambient intensity", "ambient focus intensity", "darkness", "dimming", "dim" } },
            new VoiceSetting { Id = "focus_radius", Key = "focusRadius", Kind = "number", Min = 0f, Max = 5f, Unit = "m", Label = "Focus radius", Names = new[] { "radius", "focus radius", "dome radius", "dome size", "focus range", "range" } },
            new VoiceSetting { Id = "focus_blur", Key = "focusBlur", Kind = "number", Min = 0f, Max = 1f, Unit = "%", Label = "Focus blur", Names = new[] { "blur", "focus blur", "blurring", "the blur", "background blur" } },
            new VoiceSetting { Id = "occlusion", Key = "occludeUi", Kind = "bool", Label = "Real things in front of windows", Names = new[] { "occlusion", "real things in front", "things in front of windows", "see through windows", "hands in front" } },
            new VoiceSetting { Id = "depth_camera", Key = "depthCamera", Kind = "bool", Label = "Sharp depth", Names = new[] { "sharp depth", "depth camera", "camera depth", "sharp edges" } },
            new VoiceSetting { Id = "focus_softness", Key = "focusSoftness", Kind = "number", Min = 0.05f, Max = 2f, Unit = "m", Label = "Focus softness", Names = new[] { "softness", "focus softness", "fade", "edge softness" } },
            new VoiceSetting { Id = "opacity", Key = "panelOpacity", Kind = "number", Min = 0.5f, Max = 1f, Unit = "%", Label = "Panel opacity", Names = new[] { "opacity", "panel opacity", "window opacity" } },
            new VoiceSetting { Id = "panel_distance", Key = "panelDistance", Kind = "number", Min = 0.5f, Max = 1.4f, Unit = "m", Label = "Panel distance", Names = new[] { "distance", "panel distance", "window distance" } },
            new VoiceSetting { Id = "ui_scale", Key = "uiScale", Kind = "number", Min = MinUiScale, Max = MaxUiScale, Unit = "x", Label = "UI scale", Names = new[] { "ui scale", "scale", "panel size", "window size", "windows", "panels", "text size", "size" } },
            new VoiceSetting { Id = "carousel_radius", Key = "carouselRadius", Kind = "number", Min = 0.6f, Max = 1.6f, Unit = "m", Label = "Float radius", Names = new[] { "carousel radius", "ring radius", "carousel size", "carousel" } },
            new VoiceSetting { Id = "bend", Key = "bendPanels", Kind = "bool", Label = "Bend panels", Names = new[] { "bend", "bending", "bend panels", "curved panels", "curve" } },
            new VoiceSetting { Id = "sounds", Key = "sounds", Kind = "bool", Label = "Sounds", Names = new[] { "sounds", "sound", "ui sounds", "clicks" } },
            new VoiceSetting { Id = "volume", Key = "soundVolume", Kind = "number", Min = 0f, Max = 1f, Unit = "%", Label = "Volume", Names = new[] { "volume", "sound volume" } },
            new VoiceSetting { Id = "speak", Key = "voice.speak", Kind = "bool", Label = "Spoken answers", Names = new[] { "spoken answers", "speak answers", "spoken replies", "voice replies", "talking back" } },
            new VoiceSetting { Id = "speak_alerts", Key = "voice.speakAlerts", Kind = "bool", Label = "Spoken thread alerts", Names = new[] { "spoken alerts", "thread alerts", "spoken notifications", "voice alerts", "announcements" } },
            new VoiceSetting { Id = "clap", Key = "gestures.clapEnabled", Kind = "bool", Label = "Clap gesture", Names = new[] { "clap", "clap gesture", "clapping" } },
            new VoiceSetting { Id = "hand_rays", Key = "gestures.rayVisible", Kind = "bool", Label = "Hand rays", Names = new[] { "hand rays", "rays", "pointer rays", "lasers" } },
            new VoiceSetting { Id = "foveation", Key = "foveation", Kind = "bool", Label = "Foveated rendering", Names = new[] { "foveation", "foveated rendering" } },
            new VoiceSetting { Id = "views_auto_open", Key = "viewsAutoOpen", Kind = "bool", Label = "Agent views open by themselves", Names = new[] { "auto open views", "agent views auto open", "views auto open" } },
            new VoiceSetting { Id = "wrist_menu", Key = "wristMenu", Kind = "bool", Label = "Main menu on the wrist", Names = new[] { "wrist menu", "menu on the wrist", "radial menu", "watch menu" } },
            // the wrist menu's own settings (ShellSettings.wrist, CONTROL > WRIST MENU)
            new VoiceSetting { Id = "wrist_menu_size", Key = "wrist.size", Kind = "number", Min = WristMenuSettings.MinSize, Max = WristMenuSettings.MaxSize, Unit = "x", Label = "Wrist menu size",
                Names = new[] { "wrist menu size", "radial menu size", "size of the wrist menu", "watch menu size" } },
            new VoiceSetting { Id = "wrist_text_size", Key = "wrist.textSize", Kind = "number", Min = WristMenuSettings.MinText, Max = WristMenuSettings.MaxText, Unit = "x", Label = "Wrist menu text size",
                Names = new[] { "wrist menu text", "wrist menu text size", "radial menu text", "text on the wrist menu", "text on the radial menu", "wrist text size" } },
            new VoiceSetting { Id = "wrist_opacity", Key = "wrist.opacity", Kind = "number", Min = WristMenuSettings.MinOpacity, Max = WristMenuSettings.MaxOpacity, Unit = "%", Label = "Wrist menu glass",
                Names = new[] { "wrist menu opacity", "wrist menu glass", "radial menu opacity", "wrist glass" } },
            new VoiceSetting { Id = "wrist_battery", Key = "wrist.battery", Kind = "bool", Label = "Battery on the wrist menu",
                Names = new[] { "battery on the wrist", "battery on my wrist", "wrist battery", "battery on the wrist menu", "battery level on the wrist" } },
            new VoiceSetting { Id = "wrist_network", Key = "wrist.network", Kind = "bool", Label = "Network on the wrist menu",
                Names = new[] { "network on the wrist", "wifi on the wrist", "wifi on my wrist", "wrist network", "signal on the wrist", "wifi signal on the wrist menu" } },
            new VoiceSetting { Id = "wrist_clock", Key = "wrist.clock", Kind = "bool", Label = "Clock on the wrist menu",
                Names = new[] { "clock on the wrist", "clock on my wrist", "time on the wrist", "wrist clock", "clock on the wrist menu" } },
            new VoiceSetting { Id = "wrist_close_tap", Key = "wrist.closeAfterTap", Kind = "bool", Label = "Fold the wrist menu after a tap",
                Names = new[] { "fold after a tap", "close after a tap", "close the wrist menu after a tap", "fold the wrist menu after a tap" } },
            new VoiceSetting { Id = "orb", Key = "orb", Kind = "bool", Label = "Whisp", Names = new[] { "whisp", "the whisp", "wisp", "the wisp", "orb", "the orb", "energy sphere", "sphere", "companion" } },
            new VoiceSetting { Id = "wake_word", Key = "voice.wakeWord", Kind = "bool", Label = "Hey Whisp", Names = new[] { "hey whisp", "wake word", "wake phrase", "hands free talk", "listening for its name" } },
            new VoiceSetting { Id = "voice_effect", Key = "voice.fx", Kind = "choice", Label = "Voice effect", Options = VoiceFx.Names, OptionLabels = new[] { "off", "AI", "ship", "comm" }, Values = VoiceEffectValues,
                Names = new[] { "voice effect", "voice sound", "ai voice", "sci-fi voice", "robot voice", "voice style", "computer voice", "voice filter", "the effect on the voice" } },
            new VoiceSetting { Id = "speech_quality", Key = "voice.speech", Kind = "choice", Label = "Speech", Options = new[] { "fast", "clear" }, OptionLabels = new[] { "fast", "clear" }, Values = SpeechValues,
                Names = new[] { "speech quality", "voice quality", "speech mode", "speaking quality", "text to speech quality", "fast speech", "clear speech" } },
        };

        private static readonly HashSet<string> OnWords = new HashSet<string> { "on", "yes", "true", "enable", "enabled", "active" };
        private static readonly HashSet<string> OffWords = new HashSet<string> { "off", "no", "false", "disable", "disabled", "0" };
        private static readonly HashSet<string> MoreWords = new HashSet<string>
        {
            "more", "up", "higher", "bigger", "larger", "stronger", "darker", "wider", "louder", "brighter", "further", "farther", "increase",
            "a bit more", "a little more", "a little bit more", "even more", "much more",
        };
        private static readonly HashSet<string> LessWords = new HashSet<string>
        {
            "less", "down", "lower", "smaller", "weaker", "lighter", "narrower", "quieter", "softer", "dimmer", "closer", "decrease",
            "a bit less", "a little less", "a little bit less", "even less", "much less",
        };

        /// "+" / "-" for more / less words ("bigger", "a bit more"), else the value as said.
        private static string MoreOrLess(string value)
        {
            string raw = (value ?? "").Trim();
            if (raw == "+" || raw == "-") return raw;
            string n = VoiceCommands.Normalize(raw);
            return MoreWords.Contains(n) ? "+" : LessWords.Contains(n) ? "-" : raw;
        }

        /// Whether a spoken value can set this setting at all: on / off for a switch, a number or more / less for a
        /// slider. Lets the fast path skip a wrong split of the words ("set the dome | radius to two meters").
        private static bool VoiceSettingFits(string id, string value)
        {
            var vs = VoiceSettingsCatalog.FirstOrDefault(x => x.Id == id);
            if (vs == null) return false;
            string raw = MoreOrLess(value);
            if (raw == "+" || raw == "-") return true;
            string v = VoiceCommands.Normalize(raw);
            if (vs.Kind == "bool") return OnWords.Contains(v) || OffWords.Contains(v) || v == "toggle" || v == "switch";
            if (vs.Kind == "choice") return ChoiceValue(vs, v) != null || v == "toggle" || v == "switch";
            return VoiceCommands.TryNumber(raw, vs.Min, vs.Max, vs.Unit, out _);
        }

        /// A choice setting's value for a spoken one ("radio" -> comm, "on" -> the first after off, "off" -> the first);
        /// null when it is none of them.
        private static string ChoiceValue(VoiceSetting vs, string normalized)
        {
            string v = (normalized ?? "").Replace("-", " ").Trim();
            if (vs.Options.Contains(v)) return v;
            if (vs.Values != null && vs.Values.TryGetValue(v, out var m)) return m;
            if (vs.Values != null) foreach (var kv in vs.Values) if (VoiceCommands.Normalize(kv.Key) == v) return kv.Value;
            if (OnWords.Contains(v)) return vs.Options.Length > 1 ? vs.Options[1] : vs.Options[0];
            if (OffWords.Contains(v)) return vs.Options[0];
            return null;
        }

        /// set_setting: on / off / toggle, a number ("0", "50 percent", "two meters", "max"), or more / less ("+",
        /// "-", "bigger", "a bit less": a tenth of the range). Applies it, moves the matching slider or toggle, and says
        /// what it is now.
        public string SetSettingByVoice(string id, string value)
        {
            value = MoreOrLess(value);
            // "make the wrist menu bigger": its size (CONTROL > WRIST MENU), not the on / off switch of the main menu's mode
            if (id == "wrist_menu" && (value == "+" || value == "-")) id = "wrist_menu_size";
            var vs = VoiceSettingsCatalog.FirstOrDefault(x => x.Id == id);
            if (vs == null) return "error: no setting " + id;
            string v = VoiceCommands.Normalize(value ?? "");
            if (vs.Kind == "bool")
            {
                bool cur = Settings.Get(vs.Key) == "true";
                bool on = OnWords.Contains(v) || value == "+" || (v == "toggle" || v == "switch" ? !cur : false);
                bool off = OffWords.Contains(v) || value == "-";
                if (!on && !off && v != "toggle" && v != "switch") return "error: " + vs.Label + " is on or off";
                if (vs.Id == "focus") SetFocus(on, false);
                else if (vs.Id == "wake_word") SetWakeWord(on); // opens or closes the microphone, asks for it
                else { Settings.Set(vs.Key, on ? "true" : "false"); OnSettingsChanged(); }
                SyncSettingsControls();
                return vs.Label + (on ? " on" : " off");
            }
            if (vs.Kind == "choice")
            {
                string cur = Settings.Get(vs.Key) ?? vs.Options[0];
                int ci = Math.Max(0, Array.IndexOf(vs.Options, cur));
                string pick = value == "+" ? vs.Options[(ci + 1) % vs.Options.Length] : value == "-" ? vs.Options[(ci + vs.Options.Length - 1) % vs.Options.Length]
                    : v == "toggle" || v == "switch" ? (ci == 0 ? vs.Options[Math.Min(1, vs.Options.Length - 1)] : vs.Options[0]) : ChoiceValue(vs, v);
                if (pick == null) return "error: " + vs.Label + " is one of " + string.Join(", ", vs.Options);
                Settings.Set(vs.Key, pick);
                OnSettingsChanged();
                SyncSettingsControls();
                int pi = Array.IndexOf(vs.Options, pick);
                return vs.Label + " " + (vs.OptionLabels != null && pi >= 0 && pi < vs.OptionLabels.Length ? vs.OptionLabels[pi] : pick);
            }
            float curN = float.TryParse(Settings.Get(vs.Key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cn) ? cn : vs.Min;
            double n;
            string raw = (value ?? "").Trim();
            if (raw == "+" || raw == "-") n = curN + (raw == "+" ? 1 : -1) * (vs.Max - vs.Min) * 0.1f;
            else if (!VoiceCommands.TryNumber(raw, vs.Min, vs.Max, vs.Unit, out n))
            {
                // "set the intensity to 30" on a 0..1 setting: whole numbers above 1 are percent
                return "error: " + vs.Label + " needs a number (" + vs.Min + "-" + vs.Max + (vs.Unit == "%" ? ", or a percent" : " " + vs.Unit) + ")";
            }
            if (vs.Unit == "%" && !raw.Contains("%") && !raw.Contains("percent") && n >= vs.Max && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double whole) && whole > 1) n = Mathf.Clamp((float)whole / 100f, vs.Min, vs.Max);
            n = Mathf.Clamp((float)n, vs.Min, vs.Max);
            Settings.Set(vs.Key, ((float)n).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (vs.Id.StartsWith("focus") && !Settings.focusEnabled) SetFocus(true, false); // changing the dome means using it
            OnSettingsChanged();
            SyncSettingsControls();
            RefreshFocusPanel();
            return vs.Label + " " + (vs.Unit == "%" ? Mathf.RoundToInt((float)n * 100f) + "%" : ((float)n).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + (vs.Unit == "m" ? " m" : vs.Unit == "x" ? "x" : ""));
        }

        /// Sliders and toggles of open panels follow settings changed elsewhere (voice, scripts, MQH).
        public void SyncSettingsControls()
        {
            var sliders = new Dictionary<string, string> { { "DISTANCE", "panelDistance" }, { "FLOAT RADIUS", "carouselRadius" }, { "UI SCALE", "uiScale" },
                { "PANEL OPACITY", "panelOpacity" }, { "VOLUME", "soundVolume" }, { "RADIUS", "focusRadius" }, { "INTENSITY", "focusIntensity" }, { "SOFTNESS", "focusSoftness" }, { "BLUR", "focusBlur" } };
            var toggles = new Dictionary<string, string> { { "Bend panels", "bendPanels" }, { "Sounds", "sounds" }, { "Clap gesture", "gestures.clapEnabled" }, { "Show hand rays", "gestures.rayVisible" },
                { "Foveated", "foveation" }, { "Ambient focus", "focusEnabled" }, { "Speak answers", "voice.speak" },
                { "Real things in front", "occludeUi" }, { "Sharp depth", "depthCamera" }, { "Main menu on the wrist", "wristMenu" }, { "Orb", "orb" }, { "Whisp", "orb" }, { "Hey Whisp", "voice.wakeWord" },
                { "Say when a thread", "voice.speakAlerts" } };
            foreach (var p in _panels.Values)
                foreach (var e in p.Elements)
                {
                    if (e is UiSlider sl && sliders.TryGetValue(sl.Text ?? "", out var key) && float.TryParse(Settings.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) sl.Set(f);
                    else if (e is UiToggle tg) { var k = toggles.FirstOrDefault(kv => (tg.Text ?? "").StartsWith(kv.Key)); if (k.Value != null) tg.Set(Settings.Get(k.Value) == "true"); }
                }
            _focusButton?.SetSelected(Settings.focusEnabled);
            // VOICE > SOUND follows voice.fx (voice, MQH, the control port)
            if (_panels.TryGetValue("voice", out var vp))
                foreach (var sg in vp.Elements.OfType<UiSegmented>())
                    if (sg.Text == "SOUND") { int fi = Array.IndexOf(VoiceFx.Names, VoiceFx.Name(VoiceFx.Parse(Settings.voice.fx))); if (sg.Selected != fi) sg.Set(fi); }
                    else if (sg.Text == "SPEECH") { int si = Settings.voice.speech == "clear" ? 1 : 0; if (sg.Selected != si) sg.Set(si); }
                    else if (sg.Text == "FEMALE" || sg.Text == "MALE") { int vi = Array.IndexOf(sg.Options, Settings.voice.voice); if (sg.Selected != vi) sg.Set(vi); } // one voice lit in both rows
        }

        /// Closes every panel except the dock; returns how many were open.
        public int CloseAllPanels()
        {
            int n = 0;
            foreach (var kv in _panels.ToList())
            {
                if (kv.Key == "dock" || !kv.Value.IsOpen) continue;
                kv.Value.Close();
                n++;
            }
            return n;
        }

        private static readonly HashSet<string> ThreadStopWords = new HashSet<string>
        {
            "the", "a", "an", "we", "where", "did", "do", "does", "was", "were", "is", "are", "of", "in", "on", "for", "with", "to", "and", "that",
            "it", "i", "you", "about", "when", "how", "what", "which", "thread", "conversation", "chat", "our", "my", "some", "this", "there",
        };

        private static string[] QueryWords(string s) => VoiceCommands.Simplify(s).Split(' ').Where(w => w.Length > 1 && !ThreadStopWords.Contains(w)).ToArray();

        /// How well a thread's title (and branch) matches the spoken words: 1 per word, partial for stems.
        public static float ThreadScore(string title, string query)
        {
            var q = QueryWords(query);
            var t = QueryWords(title);
            if (q.Length == 0 || t.Length == 0) return 0f;
            float s = 0f;
            foreach (var w in q)
            {
                if (t.Contains(w)) s += 1f;
                else if (t.Any(x => x.Length >= 4 && w.Length >= 4 && (x.StartsWith(w.Substring(0, 4)) || VoiceCommands.Levenshtein(x, w) <= 1))) s += 0.7f;
            }
            return s / q.Length;
        }

        private void VoiceFindThread(string query, Action<string> reply, string window = null)
        {
            if (string.IsNullOrWhiteSpace(query)) { OpenPanel("threads"); reply("Which thread? THREADS is open"); return; }
            Mqh.Threads(null, th =>
            {
                if (!(th is JArray arr)) { reply("error: MQH is not reachable (" + (Mqh.LastError ?? "no answer") + ")"); return; }
                var best = arr.Select(x => new { x, title = (string)x["title"] ?? (string)x["titleFallback"] ?? "", score = ThreadScore(((string)x["title"] ?? (string)x["titleFallback"] ?? "") + " " + (string)x["environmentBranchName"], query) })
                    .OrderByDescending(p => p.score).ThenByDescending(p => p.x["updatedAt"]?.Value<long>() ?? 0).FirstOrDefault();
                if (best == null || best.score < 0.34f) { OpenPanel("threads"); reply($"error: no thread about “{query}” ({arr.Count} threads)"); return; }
                string pid = (string)best.x["projectId"];
                if (!string.IsNullOrEmpty(pid)) { _projectSel = pid; Settings.lastProject = pid; }
                string tid = (string)best.x["id"];
                if (window == "this") OpenThreadIn(_twFocus, tid); // in place of the focused window's thread
                else if (window == "new" && OpenWindowShowing(tid) == null && Tws.Count < MaxThreadWindows) OpenThreadIn(NewThreadWindow(), tid);
                else OpenThread(tid);
                reply("Opened “" + Short(best.title, 50) + "”");
            });
        }

        /// new_project: both parts said -> made at once; the type only -> its name on the keyboard; else NEW PROJECT asks.
        private string VoiceNewProject(string type, string name)
        {
            type = Array.IndexOf(ProjectKinds.Types, type ?? "") >= 0 ? type : null;
            if (type == null) { OpenPanel("threads"); NewProjectPrompt(string.IsNullOrWhiteSpace(name) ? null : name); return "Native app, default or research?"; }
            if (string.IsNullOrWhiteSpace(name)) { OpenPanel("threads"); NewProjectNamed(type); return "Name the " + ProjectKinds.Label(type).ToLowerInvariant() + " project on the keyboard"; }
            OpenPanel("threads");
            return CreateNewProject(type, name);
        }

        private void VoiceSwitchWorkspace(string name, Action<string> reply)
        {
            Mqh.Projects(pr =>
            {
                if (!(pr is JArray arr)) { reply("error: MQH is not reachable (" + (Mqh.LastError ?? "no answer") + ")"); return; }
                _projects = arr;
                if (string.IsNullOrWhiteSpace(name)) { OpenPanel("threads"); RefreshThreads(); reply("Pick a workspace in THREADS"); return; }
                string want = VoiceCommands.Simplify(name);
                var best = arr.Select(p =>
                {
                    string n = VoiceCommands.Simplify((string)p["name"] ?? "");
                    string path = VoiceCommands.Simplify(Path.GetFileName(((string)p["sources"]?.FirstOrDefault()?["path"] ?? "").TrimEnd('/')));
                    float s = n == want || path == want ? 3f
                        : n.Replace(" ", "") == want.Replace(" ", "") ? 2.5f
                        : n.Contains(want) || want.Contains(n) && n.Length > 2 ? 2f
                        : VoiceCommands.Levenshtein(n, want) <= Math.Max(1, want.Length / 4) ? 1.5f
                        : ThreadScore(n + " " + path, want) * 1.2f;
                    return new { p, s };
                }).OrderByDescending(x => x.s).FirstOrDefault();
                if (best == null || best.s < 0.6f)
                {
                    OpenPanel("threads");
                    reply($"error: no workspace called “{name}” ({string.Join(", ", arr.Select(p => (string)p["name"]).Take(6))})");
                    return;
                }
                _projectSel = (string)best.p["id"];
                Settings.lastProject = _projectSel;
                _collapsedProjects.Remove(_projectSel); // unfold it in THREADS
                OnSettingsChanged();
                OpenPanel("threads");
                RefreshThreads();
                string path0 = (string)best.p["sources"]?.FirstOrDefault()?["path"];
                if (!string.IsNullOrEmpty(path0)) _filesPath = path0;
                reply("Workspace " + (string)best.p["name"]);
            });
        }

        // ------------------------------------------------------------------ HUD

        private void SetVoiceState(string state, string title, string text)
        {
            VoiceState = state;
            if (state == "listening" || state == "transcribing" || state == "thinking") BoostCpuFor(30f);
            if (Router == null) return;
            EnsureVoiceHud();
            _vHudTitle.Set(title, state == "error" ? UiTheme.Current.Danger : state == "done" ? UiTheme.Current.Ok : state == "listening" ? UiTheme.Current.Warn : UiTheme.Current.Accent);
            _vHudText.Set(text ?? "");
            _vHudLevel.Set(state == "listening" ? Recorder.Level : state == "done" ? 1f : 0f);
            _vHudCloseAt = -1f;
            // done or failed: the orb says so in colour
            if (Orb != null && Orb.Visible && (state == "done" || state == "error")) Orb.Flash(state == "error" ? UiTheme.Current.Danger : UiTheme.Current.Ok, 1.0f);
            if (!_vHud.IsOpen)
            {
                if (OrbActive)
                {
                    // the first card of the orb's column (UiToasts keeps it there while it is open)
                    _vHud.BaseScale = Vector3.one * OrbMenuScale();
                    _vHud.gameObject.SetActive(true);
                    Toasts.Pinned = _vHud;
                    Toasts.LayoutNow();
                    _vHud.gameObject.SetActive(false);
                }
                else if (_panels.TryGetValue("dock", out var dock) && dock.IsOpen)
                {
                    _vHud.BaseScale = Vector3.one * UiScaleNow;
                    var dt = dock.transform;
                    _vHud.transform.SetPositionAndRotation(dt.position + dt.up * (dock.Height * 0.5f + _vHud.Height * 0.5f + 0.015f) * dock.RestLossyScale.y, dt.rotation);
                }
                else if (Head != null) { _vHud.BaseScale = Vector3.one * UiScaleNow; _vHud.PlaceInFront(Head, 0.62f, 0.18f); }
                _vHud.Open();
            }
        }

        private void EnsureVoiceHud()
        {
            if (_vHud != null) return;
            var t = UiTheme.Current;
            _vHud = UiPanel.Create<UiPanel>("voice-hud", "", 0.40f, 0.098f, _root, header: false);
            _vHud.Closable = false;
            _vHud.Draggable = false; // it has its place: beside the orb, or over the dock
            _vHud.BaseScale = Vector3.one * UiScaleNow;
            Router.Register(_vHud);
            var c = _vHud.Content;
            float bw = 0.062f;
            _vHudTitle = _vHud.Add(new UiLabel("", t.BodySize) { Rect = new Rect(c.x, c.y, c.width - bw - t.Gap, 0.022f), UseDisplay = true });
            _vHudLevel = _vHud.Add(new UiProgress(0f) { Rect = new Rect(c.x, c.y + 0.026f, c.width - bw - t.Gap, 0.008f) });
            _vHudText = _vHud.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, c.y + 0.037f, c.width - bw - t.Gap, c.height - 0.037f), Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            _vHud.Add(new UiButton("■\nSTOP", () => { if (Recorder.Recording) VoiceStop(); else VoiceCancel(); }, UiButtonStyle.Primary) { Rect = new Rect(c.x + c.width - bw, c.y, bw, c.height), TextSize = 0.012f });
            _vHud.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ VOICE panel

        private void BuildVoice()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("voice", "VOICE · LOCAL AI", ControlPageW, ControlPageH);
            var c = p.Content;
            float leftW = 0.33f;
            var ls = new UiStack(new Rect(c.x, c.y, leftW, c.height), t.Gap);
            _vStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = ls.Next(0.05f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            _vModels = p.Add(new UiLabel("", t.SmallSize) { Rect = ls.Next(0.15f), Wrap = true, Align = TextAlignmentOptions.TopLeft, UseMono = true, Color = t.TextDim });
            _vInstall = p.Add(new UiProgress(0f) { Rect = ls.Next(0.008f) });
            AddButtons(p, ls.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("INSTALL MODELS", () => { EnsureVoiceService(); After(0.1f, InstallVoiceModels); }),
                (ServiceButtonText(), () => { if (Qvac.IsRunning || Qvac.IsBusyStarting) Qvac.StopService(); else Qvac.StartService(); RefreshVoicePanel(); }),
            }, primary: 0);
            _vServiceBtn = p.Elements.OfType<UiButton>().LastOrDefault(); // says what a tap does now: ■ STOP or ▶ START
            AddButtons(p, ls.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("● TALK", VoiceToggle),
                ("TYPE…", () => UiKeyboard.Edit("", false, null, txt => { if (!string.IsNullOrWhiteSpace(txt)) VoiceProcessText(txt, null); })),
                ("SAY…", () => UiKeyboard.Edit("Hello from MetaCodex", false, null, txt => VoiceSay(txt))),
            }, primary: 0);
            p.Add(new UiToggle("Speak answers (actions are confirmed silently)", Settings.voice.speak, v => { Settings.voice.speak = v; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            p.Add(new UiToggle("Decision model for commands (fast, multilingual)", Settings.voice.decide, v => { Settings.voice.decide = v; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            p.Add(new UiToggle("Ask the LLM when nothing fits", Settings.voice.useLlm && Settings.voice.llmFallback, v => { Settings.voice.llmFallback = v; if (v) Settings.voice.useLlm = true; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            p.Add(new UiToggle("Stop listening after a pause", Settings.voice.autoStop, v => { Settings.voice.autoStop = v; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            p.Add(new UiToggle("Hey Whisp: say it to start TALK (the microphone stays on)", Settings.voice.wakeWord, v => SetWakeWord(v)) { Rect = ls.Next(0.03f) });
            p.Add(new UiToggle("Start the AI service with the app", Settings.voice.enabled, v => { Settings.voice.enabled = v; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            // right column: the reply voice on top (the left one is full), then the commands
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            const float pickLabel = 0.075f; // the same label column on every row: the buttons line up
            // the voice of spoken replies: Supertonic 3 has ten (F1-F5, M1-M5); picking one says a sample
            string[] fv = { "F1", "F2", "F3", "F4", "F5" }, mv = { "M1", "M2", "M3", "M4", "M5" };
            UiSegmented female = null, male = null;
            void PickVoice(string v)
            {
                Settings.voice.voice = v;
                female?.Set(Array.IndexOf(fv, v));
                male?.Set(Array.IndexOf(mv, v));
                OnSettingsChanged();
                PushVoiceConfig();
                VoiceSay("Hi, this is voice " + v.Substring(1) + ".");
            }
            female = p.Add(new UiSegmented("FEMALE", fv, Array.IndexOf(fv, Settings.voice.voice), i => PickVoice(fv[i])) { Rect = rs.Next(0.032f), LabelWidth = pickLabel });
            male = p.Add(new UiSegmented("MALE", mv, Array.IndexOf(mv, Settings.voice.voice), i => PickVoice(mv[i])) { Rect = rs.Next(0.032f), LabelWidth = pickLabel });
            // the voice effect (VoiceFx): picking one says a short line with it
            p.Add(new UiSegmented("SOUND", VoiceFx.Labels, (int)VoiceFx.Parse(Settings.voice.fx), i =>
            {
                Settings.voice.fx = VoiceFx.Names[i];
                OnSettingsChanged();
                if (SpeakForTest != null || Qvac != null && Qvac.IsRunning) VoiceSay(VoiceFxSample); // no service: no toast of the sample line
            }) { Rect = rs.Next(0.032f), LabelWidth = pickLabel });
            // speech quality: FAST (3 steps, sooner) or CLEAR (5 steps, every word exact); picking one says the sample line
            p.Add(new UiSegmented("SPEECH", new[] { "FAST", "CLEAR" }, Settings.voice.speech == "clear" ? 1 : 0, i =>
            {
                Settings.voice.speech = i == 1 ? "clear" : "fast";
                OnSettingsChanged();
                PushVoiceConfig();
                if (SpeakForTest != null || Qvac != null && Qvac.IsRunning) VoiceSay(VoiceFxSample);
            }) { Rect = rs.Next(0.032f), LabelWidth = pickLabel });
            p.Add(new UiToggle("Say when a thread is done or needs you", Settings.voice.speakAlerts, v => { Settings.voice.speakAlerts = v; OnSettingsChanged(); }) { Rect = rs.Next(0.03f) });

            p.Add(new UiLabel("COMMANDS (say them, or anything close)", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vCmdList = p.Add(new UiList(0.034f, (item, idx) =>
            {
                var cmd = Voice.Find(item.Tag as string);
                if (cmd != null) Toast(CommandHint(cmd), "info");
            }) { Rect = rs.Next(0.21f - 0.032f - 0.03f - t.Gap * 2) }); // rows less: SOUND and the alerts toggle sit above
            p.Add(new UiLabel("LAST", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vHistory = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Rest(), Wrap = true, Align = TextAlignmentOptions.TopLeft, UseMono = true, Color = t.TextDim });
            RefreshVoicePanel();
        }

        /// What a tap on a VOICE > COMMANDS row shows: how to say it first (two examples), then one short line of what it
        /// does; never the command's id (the toast shows two lines).
        public static string CommandHint(VoiceCommand cmd)
        {
            var phrases = (cmd.Phrases ?? new string[0]).Select(VoiceCommands.Example).ToList();
            var says = phrases.Where(x => !x.Contains("<"))
                .Concat((cmd.Examples ?? new List<(string said, string line)>()).Select(e => e.said))
                .Concat(phrases.Where(x => x.Contains("<")))
                .Concat(cmd.Samples ?? new string[0])
                .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            string d = cmd.Description ?? "";
            int cut = d.IndexOfAny(new[] { ':', ';', '(' });
            if (cut > 12) d = d.Substring(0, cut);
            int dot = d.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 12) d = d.Substring(0, dot);
            d = Short(d.Trim().TrimEnd('.', ','), 64);
            string say = says.Count > 0 ? "Say “" + string.Join("”, “", says) + "”" : "";
            return say.Length > 0 && d.Length > 0 ? say + "\n" + d : say + d;
        }

        private string DeciderCachePath => System.IO.Path.Combine(Application.persistentDataPath, "voice-decider-vectors.bin");

        /// Example vectors from the last run (text -> vector), so a start embeds only what changed. The first start has
        /// none: it takes the APK's (StreamingAssets qvac/decider-vectors.bin, written by the Editor's decision bench
        /// with the same model; feat/ready-on-install), so commands are decided by meaning from the first second instead
        /// of after embedding every example on the AI cores (15-30 s). Examples changed since are embedded as before.
        private void LoadDeciderCache()
        {
            if (!System.IO.File.Exists(DeciderCachePath)) { StartCoroutine(SeedDeciderCacheFromApk()); return; }
            ReadDeciderCache(null);
        }

        private System.Collections.IEnumerator SeedDeciderCacheFromApk()
        {
            string url = Application.streamingAssetsPath + "/qvac/decider-vectors.bin";
            if (!url.Contains("://")) url = "file://" + url;
            using (var req = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success || req.downloadHandler.data == null)
                {
                    // 2026-11-14 first-start log: no "decision vectors from the APK" line and the decision model took 52 s to embed
                    // its examples on the AI cores: the packed APK had no qvac/decider-vectors.bin. Say so.
                    Log("voice: no decision vectors in this APK (qvac/decider-vectors.bin: " + req.error + "): the examples are embedded on the headset");
                    yield break;
                }
                try { System.IO.File.WriteAllBytes(DeciderCachePath, req.downloadHandler.data); }
                catch (Exception ex) { Log("voice decider cache from the APK: " + ex.Message); yield break; }
            }
            ReadDeciderCache(() => Log($"voice: {_deciderSavedCount} decision vectors from the APK"));
        }

        /// Writes vectors in the cache's format (the shell's own cache, and the APK's copy made by the decision bench).
        public static void WriteDeciderCache(string path, IReadOnlyDictionary<string, float[]> cache)
        {
            int dim = cache.Values.First().Length;
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                w.Write(DeciderModelTag);
                w.Write(cache.Count);
                w.Write(dim);
                foreach (var kv in cache) { w.Write(kv.Key); foreach (var f in kv.Value) w.Write(f); }
            }
        }

        /// True while the cache file is being read (the build of the decision model waits for it: a build before it would embed
        /// every example again).
        private bool _deciderCacheLoading;

        /// Reads the cache file on a worker thread at low priority (735,000 BinaryReader.ReadSingle calls for 1,916 examples:
        /// tens of ms on the main thread, inside the start's one long frame, at every start that has the file); the vectors
        /// are seeded on the main thread, where the decider's dictionary lives (one insert per example).
        private void ReadDeciderCache(Action done)
        {
            string path = DeciderCachePath;
            _deciderCacheLoading = true;
            MetaCodex.Linux.LowPriority.Run(() =>
            {
                List<KeyValuePair<string, float[]>> rows = null;
                string err = null;
                try { rows = ParseDeciderCache(path, out err); }
                catch (Exception ex) { err = ex.Message; }
                _main.Enqueue(() =>
                {
                    _deciderCacheLoading = false;
                    if (rows != null)
                    {
                        foreach (var kv in rows) Decider.Seed(kv.Key, kv.Value);
                        _deciderSavedCount = rows.Count;
                    }
                    if (err != null) Log("voice decider cache: " + err);
                    done?.Invoke();
                });
            }, "mcx-decider-cache");
        }

        /// The vectors of a cache file; null (and `why`) when it is not ours (another model or an unreadable file).
        private static List<KeyValuePair<string, float[]>> ParseDeciderCache(string path, out string why)
        {
            why = null;
            if (!System.IO.File.Exists(path)) return null;
            using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
            {
                if (r.ReadString() != DeciderModelTag) { why = "the vectors file is of another model; they are embedded again"; return null; }
                int n = r.ReadInt32(), dim = r.ReadInt32();
                if (n < 0 || n > 200000 || dim < 8 || dim > 4096) { why = "the vectors file header is not valid"; return null; }
                var rows = new List<KeyValuePair<string, float[]>>(n);
                for (int i = 0; i < n; i++)
                {
                    string t = r.ReadString();
                    var v = new float[dim];
                    for (int k = 0; k < dim; k++) v[k] = r.ReadSingle();
                    rows.Add(new KeyValuePair<string, float[]>(t, v));
                }
                return rows;
            }
        }

        private void SaveDeciderCache()
        {
            try
            {
                var c = Decider.Cache;
                if (c.Count == 0 || Decider.LastEmbedded == 0 && Decider.LastPruned == 0 && c.Count == _deciderSavedCount) return;
                // written off the main thread (2.8 MB, 0.7k floats a text)
                var snapshot = c.ToDictionary(kv => kv.Key, kv => kv.Value);
                string path = DeciderCachePath;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { WriteDeciderCache(path, snapshot); }
                    catch (Exception ex) { _main.Enqueue(() => Log("voice decider cache: " + ex.Message)); }
                });
                _deciderSavedCount = c.Count;
            }
            catch (Exception ex) { Log("voice decider cache: " + ex.Message); }
        }

        private void InstallVoiceModels()
        {
            if (!Qvac.IsRunning) { Toast("The AI service is starting; press INSTALL again in a moment (" + Qvac.Status + ")", "warn"); return; }
            Qvac.Client.Install(new[] { "asr", "llm", "tts", "embed" }, (r, e) =>
            {
                if (e != null) { Toast("Install: " + e, "error"); return; }
                Toast("Downloading the voice models (about 660 MB) in the background", "info");
                PollVoiceStatus();
            });
        }

        public void RefreshVoicePanel()
        {
            if (_vStatus == null) return;
            var t = UiTheme.Current;
            var st = Qvac?.State ?? QvacHost.HostState.Idle;
            string svc = st == QvacHost.HostState.Running ? "<color=#3DFFA2>●</color> running" : st == QvacHost.HostState.Failed ? "<color=#FF5470>●</color> failed" : "○ " + st.ToString().ToLowerInvariant();
            var s = QvacStatus;
            string mem = s?["rssMB"] != null ? $" · {s["rssMB"]} MB · free {s["availableMB"]} MB" : "";
            _vStatus.Set($"AI service: <b>{svc}</b>{mem}\n{Qvac?.Status}" + (Decider != null && Settings.voice.decide ? "\ndecisions: " + Decider.Status : "")
                + (Wake != null && Wake.On ? "\nHey Whisp: " + Wake.State + (Wake.WaitReason != null ? " (" + Wake.WaitReason + ")" : "") : ""));
            if (_vServiceBtn != null && _vServiceBtn.Text != ServiceButtonText()) _vServiceBtn.SetText(ServiceButtonText());
            if (s?["models"] is JObject models)
            {
                var lines = new List<string>();
                float progSum = 0f; int prog = 0;
                foreach (var kv in models)
                {
                    var m = kv.Value;
                    string eng = (string)s["engines"]?[kv.Key]?["state"] ?? "";
                    string inst = m["installed"]?.Value<bool>() == true ? "✓" : (string)m["install"] == "downloading" ? $"{(m["progress"]?.Value<float>() ?? 0f) * 100f:F0}%" : (string)m["install"] == "failed" ? "⚠" : "–";
                    if ((string)m["install"] == "downloading" || (string)m["install"] == "verifying") { progSum += m["progress"]?.Value<float>() ?? 0f; prog++; }
                    string label = kv.Key == "asr" ? "speech→text" : kv.Key == "llm" ? "commands LLM" : kv.Key == "embed" ? "decisions" : "text→speech";
                    lines.Add($"{inst,-4} {label,-13} {m["sizeMB"],4} MB {eng}");
                    // the model itself, quantization included (the service's catalogue: "Qwen3 0.6B instruct, tool calling (q4_0)")
                    string name = (string)m["name"] ?? (string)m["id"];
                    if (!string.IsNullOrEmpty(name)) lines.Add("     " + name);
                    if ((string)m["install"] == "failed") lines.Add("     " + Short((string)m["error"] ?? "", 40));
                }
                _vModels.Set(string.Join("\n", lines));
                _vInstall.Set(prog > 0 ? progSum / prog : models.Properties().All(p => p.Value["installed"]?.Value<bool>() == true) ? 1f : 0f);
            }
            else _vModels.Set(st == QvacHost.HostState.Running ? "asking the service …" : "models: start the service to see them");
            _vCmdList?.SetItems(Voice.All.Select(cmd => new UiListItem
            {
                Icon = cmd.Owner == "shell" ? "◆" : "ƒ",
                IconColor = cmd.Enabled ? t.Accent : t.TextDim,
                Title = cmd.Name,
                Sub = cmd.Phrases.Length > 0 ? "“" + VoiceCommands.Example(cmd.Phrases[0]) + "”" : cmd.Description,
                Badge = cmd.Owner == "shell" ? "" : cmd.Owner.Replace("plugin/", ""),
                Tag = cmd.Name,
            }), keepScroll: true);
            _vHistory?.Set(VoiceRecent.Count == 0 ? "nothing yet · press TALK (dock) and say “open git”, “close all windows”, “find the thread where we fixed the keyboard”"
                : string.Join("\n", VoiceRecent.Take(6).Select(r => $"{r.At:HH:mm} “{Short(r.Transcript, 34)}” → {(r.Ok ? (r.Command ?? "") + " " + Short(r.Reply ?? "", 24) : "⚠ " + Short(r.Error, 30))} · {r.TotalMs} ms")));
        }

        // ------------------------------------------------------------------ control port

        /// voice status | list | listen | stop | cancel | install | warm | start | stop-service | threads N | speak on|off | fx <preset>
        private string VoiceControl(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0] : "status";
            string rest = parts.Length > 1 ? parts[1].Trim() : "";
            switch (sub)
            {
                case "status":
                {
                    var j = new JObject
                    {
                        ["service"] = Qvac?.State.ToString(), ["status"] = Qvac?.Status, ["state"] = VoiceState, ["recording"] = Recorder?.Recording,
                        ["commands"] = Voice.All.Count, ["warmed"] = _warmedRevision == Voice.Revision,
                        ["settings"] = JObject.FromObject(Settings.voice), ["last"] = VoiceRecent.FirstOrDefault()?.ToJson(),
                    };
                    if (QvacStatus != null) j["ai"] = QvacStatus;
                    PollVoiceStatus();
                    return j.ToString(Newtonsoft.Json.Formatting.None);
                }
                case "list": return string.Join("\n", Voice.All.Select(c => $"{c.Name} owner={c.Owner} enabled={c.Enabled} phrases={string.Join(" | ", c.Phrases)}"));
                case "tools": return Voice.ToolSpecs().ToString(Newtonsoft.Json.Formatting.None);
                case "registry": { var j = Voice.Describe(); j["settings"] = VoiceSettingsForBench(); j["decider"] = Decider?.Status; return j.ToString(Newtonsoft.Json.Formatting.None); }
                case "decider": if (rest.Length > 0) { Settings.voice.decide = rest != "off"; OnSettingsChanged(); } return "decide=" + Settings.voice.decide + ", " + (Decider?.Status ?? "-");
                case "fallback": if (rest.Length > 0) { Settings.voice.llmFallback = rest != "off"; OnSettingsChanged(); } return "llmFallback=" + Settings.voice.llmFallback;
                case "match": { var m = Voice.FastMatch(rest); return m == null ? "no match" : m.Command.Name + " " + m.Args.ToString(Newtonsoft.Json.Formatting.None); }
                case "listen": VoiceListen(); return "ok, " + VoiceState;
                case "stop": VoiceStop(); return "ok";
                case "cancel": VoiceCancel(); return "ok";
                case "start": EnsureVoiceService(); return "ok, " + Qvac.State;
                case "stop-service": Qvac.StopService(); return "ok";
                case "install": EnsureVoiceService(); After(0.2f, InstallVoiceModels); return "ok, installing in the background (voice status shows progress)";
                case "threads": if (int.TryParse(rest, out int n)) { Settings.voice.threads = Mathf.Clamp(n, 1, 2); Qvac.Threads = Settings.voice.threads; OnSettingsChanged(); PushVoiceConfig(); } return "threads=" + Settings.voice.threads + " (max 2: more is slower on the service's 3 cores)";
                case "voice": if (rest.Length > 0) { Settings.voice.voice = rest.Trim().ToUpperInvariant(); OnSettingsChanged(); PushVoiceConfig(); } return "voice=" + Settings.voice.voice + " (F1-F5, M1-M5)";
                case "fx":
                    if (rest.Length > 0)
                    {
                        if (!VoiceFx.TryParse(rest, out var preset)) return "error: voice fx off|adjutant|starship|comm";
                        Settings.voice.fx = VoiceFx.Name(preset);
                        OnSettingsChanged();
                        SyncSettingsControls();
                    }
                    return "fx=" + Settings.voice.fx + " (off, adjutant, starship, comm)";
                case "speak": Settings.voice.speak = rest != "off"; OnSettingsChanged(); return "speak=" + Settings.voice.speak;
                case "llm": Settings.voice.useLlm = rest != "off"; OnSettingsChanged(); return "useLlm=" + Settings.voice.useLlm;
                case "wake": // "Hey Whisp": voice wake [status] | on | off (the test path is async: voice wake test <file.wav>)
                    if (rest == "on" || rest == "off") SetWakeWord(rest == "on");
                    else if (rest.Length > 0 && rest != "status") return "error: voice wake [status] | on | off | test <file.wav> [audio_ctx=N] [max_tokens=N] [fire] [prompt=TEXT]";
                    return (Wake != null ? Wake.Describe() : "hey whisp: not built") + (Settings.voice.wakeWord && !VoiceRecorder.HasPermission ? " (no microphone permission yet)" : "");
                default: return "error: voice status | list | tools | registry | match <text> | decide <text> | decider on|off | fallback on|off | listen | stop | cancel | start | stop-service | install | threads N | speak on|off | fx off|adjutant|starship|comm | llm on|off | wake [status|on|off|test <file.wav>] | run <text> | say <text> | wav <file> | warm";
            }
        }

        /// The control-port commands that answer later: voice run <text> | say <text> | wav <file> | warm | decide <text> |
        /// wake test <file.wav> [audio_ctx=N] [max_tokens=N] [fire] [prompt=TEXT].
        public bool VoiceAsyncCommand(string line, Action<string> reply)
        {
            if (!line.StartsWith("voice ")) return false;
            var parts = line.Substring(6).Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0] : "";
            string rest = parts.Length > 1 ? MetaCodex.App.ControlServer.Unescape(parts[1]) : "";
            switch (sub)
            {
                case "run":
                    VoiceProcessText(rest, r => reply(r.ToJson().ToString(Newtonsoft.Json.Formatting.None)));
                    return true;
                case "say":
                    EnsureVoiceService();
                    VoiceSay(rest, e => reply(e == null ? "ok" : "error: " + e));
                    return true;
                case "warm":
                    EnsureVoiceService();
                    WarmVoiceLlm(e => reply(e == null ? "ok" : "error: " + e));
                    return true;
                case "decide":
                    // what the decision model makes of a sentence (no command runs)
                    if (Decider == null || !Decider.Usable || Qvac == null || !Qvac.IsRunning) { reply("error: the decision model is not ready (" + (Decider?.Status ?? "off") + ")"); return true; }
                    Decider.Decide(rest, r => reply(r.ToJson().ToString(Newtonsoft.Json.Formatting.None)));
                    return true;
                case "wav":
                {
                    // a WAV the userland wrote: /mnt/quest/x.wav is this app's shared folder
                    string path = rest.StartsWith("/mnt/quest/") ? Path.Combine(Application.persistentDataPath, rest.Substring(11)) : rest;
                    if (!File.Exists(path)) { reply("error: no file " + rest); return true; }
                    VoiceProcessWav(File.ReadAllBytes(path), r => reply(r.ToJson().ToString(Newtonsoft.Json.Formatting.None)));
                    return true;
                }
                case "wake":
                {
                    // "Hey Whisp" without a microphone: a WAV through the same gate and wake check, each stage timed
                    // (docs/voice.md "Hey Whisp"); `fire` also runs the wake on the first match (flare, sound, TALK)
                    if (!rest.StartsWith("test ")) return false; // voice wake [status|on|off]: VoiceControl
                    var a = rest.Substring(5).Trim();
                    string prompt = null;
                    int pi = a.IndexOf("prompt=", StringComparison.Ordinal);
                    if (pi >= 0) { prompt = a.Substring(pi + 7).Trim(); a = a.Substring(0, pi).Trim(); }
                    var words = a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0) { reply("error: voice wake test <file.wav> [audio_ctx=N] [max_tokens=N] [fire] [prompt=TEXT]"); return true; }
                    string file = words[0];
                    string path = file.StartsWith("/mnt/quest/") ? Path.Combine(Application.persistentDataPath, file.Substring(11)) : file;
                    if (!File.Exists(path)) { reply("error: no file " + file); return true; }
                    if (Wake == null || Qvac == null || !Qvac.IsRunning) { reply("error: the AI service is not running"); return true; }
                    var q = new List<string>();
                    bool fire = false;
                    for (int i = 1; i < words.Length; i++)
                    {
                        if (words[i] == "fire") fire = true;
                        else if (words[i].StartsWith("audio_ctx=") || words[i].StartsWith("max_tokens=")) q.Add(words[i]);
                    }
                    if (!string.IsNullOrEmpty(prompt)) q.Add("prompt=" + Uri.EscapeDataString(prompt));
                    Wake.TestWav(File.ReadAllBytes(path), string.Join("&", q), reply, fire ? (Action<WakeHit>)OnWakeHeard : null);
                    return true;
                }
                default: return false;
            }
        }

        private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;
    }
}
