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
        /// When no phrase matches, ask the local LLM which command was meant (used when the decision model is off or
        /// not ready, and as its fallback when `llmFallback` is on).
        public bool useLlm = true;
        /// Decide commands with the decision model (VoiceDecider: multilingual sentence vectors, ~10-60 ms) instead of the
        /// LLM (0.5-9 s): tools/voicebench measured 79-81 % exact against the LLM's 58 % (English and Italian).
        public bool decide = true;
        /// When the decision model finds no command, still ask the LLM (slower; for unusual phrasings).
        public bool llmFallback = false;
        /// Stop listening by itself after a pause in speech.
        public bool autoStop = true;
        /// Speech-to-text language: en, it, de, fr, es, ... or auto.
        public string language = "en";
        /// Inference threads (cores 0-2 only): 2 keeps 72 fps (measured), 3 is faster.
        public int threads = 2;
        public float maxSeconds = 12f;
        public string voice = "F1";
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
        /// The vectors on disk belong to this model (qvac/server/models.js `embed.id`) and prefix; other ones are ignored.
        private const string DeciderModelTag = "multilingual-e5-small-q8_0|query: ";
        /// idle | listening | transcribing | thinking | done | error
        public string VoiceState { get; private set; } = "idle";
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
        public static readonly string[] VoicePanels = { "terminals", "files", "git", "agents", "threads", "web", "links", "clipboard", "mqh", "skills", "plugins", "scripts", "gestures", "vault", "settings", "voice", "system", "focus", "storage", "tools", "messages" };

        private AudioSource _voiceAudio;
        private UiPanel _vHud;
        private UiLabel _vHudTitle, _vHudText;
        private UiProgress _vHudLevel;
        private float _vHudCloseAt = -1f;
        private UiLabel _vStatus, _vModels, _vHistory;
        private UiList _vCmdList;
        private UiProgress _vInstall;
        private float _nextVoicePoll;
        private int _warmedRevision = -1;
        private bool _warming;
        private string _sentConfig;

        private void InitVoice()
        {
            Voice = new VoiceCommands();
            Recorder = new VoiceRecorder();
            Recorder.Finished += OnRecorderFinished;
            RegisterShellVoiceCommands();
            RegisterSessionVoiceCommand(); // questions about the session (ShellContext)
            Decider = new VoiceDecider(Voice, (texts, done) =>
            {
                if (Qvac == null || !Qvac.IsRunning) { done(null, "the AI service is not running"); return; }
                Qvac.Client.Embed(texts, (r, e) => done(r?.Vectors, e));
            });
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
            var words = new List<string> { "MetaCodex", "ambient focus", "terminal", "workspace", "thread", "panel", "layout", "float", "fixed", "recenter", "intensity", "radius", "softness" };
            words.AddRange(VoicePanels);
            foreach (var c in Voice.All) if (c.Enabled && c.Vocabulary != null) words.AddRange(c.Vocabulary);
            words.AddRange(_projects.Select(p => (string)p["name"]).Where(n => !string.IsNullOrEmpty(n)).Take(12));
            var cfg = new JObject
            {
                ["threads"] = Mathf.Clamp(Settings.voice.threads, 1, 2), // measured: 3 and 4 are slower on the 3 cores the service has
                ["language"] = string.IsNullOrEmpty(Settings.voice.language) ? "en" : Settings.voice.language,
                ["prompt"] = "Voice commands for MetaCodex: " + string.Join(", ", words.Distinct()) + ".",
                ["voice"] = Settings.voice.voice,
                ["idleMinutes"] = new JObject { ["asr"] = 10, ["llm"] = 15, ["tts"] = 3 },
            };
            string s = cfg.ToString(Newtonsoft.Json.Formatting.None);
            if (s == _sentConfig) return;
            _sentConfig = s;
            Qvac.Client.Config(cfg, (r, e) => { if (e != null) { Log("[ai] config: " + e); _sentConfig = null; } });
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
                    _vHudText?.Set($"{Recorder.Seconds:F1} s" + (Recorder.HeardSpeech ? " · hearing you" : " · speak now") + "   (TALK again to stop)");
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
            if (Decider != null && Settings.voice.decide && Qvac != null && Qvac.IsRunning && !Decider.Ready && !Decider.Building
                && Time.unscaledTime >= _nextDeciderTry && ModelInstalled("embed"))
            {
                _nextDeciderTry = Time.unscaledTime + 15f;
                Decider.Build(err =>
                {
                    if (err == null) { SaveDeciderCache(); Log("voice: decision model " + Decider.Status); }
                    else Log("voice: " + err);
                    RefreshVoicePanel();
                });
            }
            // keep the LLM's prompt cache in step with the command set (only once the user uses voice)
            if (Qvac != null && Qvac.IsRunning && Settings.voice.useLlm && !_warming && _warmedRevision >= 0 && _warmedRevision != Voice.Revision && VoiceState == "idle")
                WarmVoiceLlm();
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

        // ------------------------------------------------------------------ the pipeline

        public void VoiceToggle()
        {
            if (Recorder.Recording) VoiceStop();
            else VoiceListen();
        }

        public void VoiceListen()
        {
            if (Recorder.Recording) return;
            EnsureVoiceService();
            if (!VoiceRecorder.HasPermission)
            {
                VoiceRecorder.RequestPermission(ok => _main.Enqueue(() =>
                {
                    if (ok) VoiceListen();
                    else VoiceDone(new VoiceResult { Error = "microphone permission denied (Horizon settings > Apps > MetaCodex)" }, null);
                }));
                return;
            }
            Recorder.AutoStop = Settings.voice.autoStop;
            Recorder.MaxSeconds = Mathf.Clamp(Settings.voice.maxSeconds, 3f, 30f);
            if (!Recorder.Start()) { VoiceDone(new VoiceResult { Error = "microphone: " + Recorder.LastError }, null); return; }
            SetVoiceState("listening", "● LISTENING", "speak now");
            Sounds?.Play("open");
            // load the LLM and its prompt cache while the user speaks (separate queue: never delays speech-to-text)
            bool llmMayAnswer = Settings.voice.useLlm && (Settings.voice.llmFallback || !Settings.voice.decide || Decider == null || !Decider.Usable);
            if (llmMayAnswer && _warmedRevision != Voice.Revision && Qvac.IsRunning) WarmVoiceLlm();
        }

        public void VoiceStop()
        {
            if (!Recorder.Recording) return;
            bool heard = Recorder.HeardSpeech;
            var wav = Recorder.Stop();
            if (wav == null) { VoiceDone(new VoiceResult { Error = heard ? "too short" : "nothing recorded" }, null); return; }
            VoiceProcessWav(wav, null);
        }

        public void VoiceCancel()
        {
            Recorder.Cancel();
            SetVoiceState("idle", "", "");
            if (_vHud != null && _vHud.IsOpen) _vHud.Close();
        }

        private void OnRecorderFinished(byte[] wav)
        {
            if (wav == null) { VoiceDone(new VoiceResult { Error = "no speech heard" }, null); return; }
            VoiceProcessWav(wav, null);
        }

        /// Speech (a WAV) -> transcript -> command -> action.
        public void VoiceProcessWav(byte[] wav, Action<VoiceResult> done)
        {
            float t0 = Time.realtimeSinceStartup;
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
                VoiceProcessText((string)r["text"] ?? "", done, asr, t0);
            });
        }

        /// Text (spoken or typed) -> command -> action. The fast path answers without the LLM.
        public void VoiceProcessText(string text, Action<VoiceResult> done, int asrMs = 0, float t0 = -1f)
        {
            var res = new VoiceResult { Transcript = (text ?? "").Trim(), AsrMs = asrMs, Started = t0 >= 0f ? t0 : Time.realtimeSinceStartup };
            if (res.Transcript.Length == 0) { res.Error = "heard nothing"; VoiceDone(res, done); return; }
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
            if (res.Ok && res.Answer && (Settings.voice.speak || sessionAnswer && Settings.voice.speakAnswers) && !string.IsNullOrEmpty(res.Reply)) VoiceSay(res.Reply);
            RefreshVoicePanel();
            done?.Invoke(res);
            VoiceState = "idle";
        }

        /// Speaks `text` with the local text-to-speech model (shows it when that is not installed).
        public void VoiceSay(string text, Action<string> done = null)
        {
            if (string.IsNullOrWhiteSpace(text)) { done?.Invoke("empty"); return; }
            if (Qvac == null || !Qvac.IsRunning) { Toast(text, "info"); done?.Invoke("the AI service is not running"); return; }
            BoostCpuFor(15f);
            Qvac.Client.Speak(text, Settings.voice.voice, (wav, e) =>
            {
                EndBoostSoon(0.2f);
                if (e != null)
                {
                    Log("[ai] speech: " + e);
                    if (e.StartsWith("409")) Toast("Spoken replies need the voice model: VOICE > INSTALL", "warn");
                    done?.Invoke(e);
                    return;
                }
                var clip = VoiceRecorder.ClipFromWav(wav);
                if (clip == null) { done?.Invoke("bad audio"); return; }
                if (_voiceAudio == null)
                {
                    var go = new GameObject("VoiceAudio");
                    go.transform.SetParent(_root, false);
                    _voiceAudio = go.AddComponent<AudioSource>();
                    _voiceAudio.spatialBlend = 0f;
                    _voiceAudio.playOnAwake = false;
                }
                _voiceAudio.volume = 0.9f;
                _voiceAudio.clip = clip;
                OnSpeechStarted(clip); // the orb speaks it: from its place, pulsing with the words
                _voiceAudio.Play();
                done?.Invoke(null);
            });
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
                ["thread"] = "threads", ["conversations"] = "threads", ["chats"] = "threads", ["sessions"] = "threads", ["projects"] = "threads", ["workspaces"] = "threads",
                ["browser"] = "web", ["internet"] = "web", ["website"] = "web", ["web browser"] = "web", ["web page"] = "web", ["page"] = "web", ["site"] = "web",
                ["bookmarks"] = "links", ["local servers"] = "links", ["dev servers"] = "links", ["bookmark"] = "links",
                ["setup"] = "settings", ["preferences"] = "settings", ["options"] = "settings", ["config"] = "settings", ["configuration"] = "settings",
                ["gesture"] = "gestures", ["hands"] = "gestures", ["gesture studio"] = "gestures",
                ["captures"] = "clipboard", ["capture"] = "clipboard", ["screenshots"] = "clipboard", ["screenshot"] = "clipboard", ["screen captures"] = "clipboard",
                ["image clipboard"] = "clipboard", ["snapshots"] = "clipboard", ["clips"] = "clipboard",
                ["skill"] = "skills", ["agent skills"] = "skills", ["plugin"] = "plugins", ["extensions"] = "mqh", ["harness"] = "mqh", ["daemon"] = "mqh", ["m q h"] = "mqh",
                ["script"] = "scripts", ["lua"] = "scripts", ["lua scripts"] = "scripts",
                ["secrets"] = "vault", ["keys"] = "vault", ["passwords"] = "vault", ["keystore"] = "vault", ["key vault"] = "vault", ["api keys"] = "vault", ["tokens"] = "vault", ["api key"] = "vault",
                ["file"] = "files", ["explorer"] = "files", ["file manager"] = "files", ["folders"] = "files", ["file browser"] = "files",
                ["repository"] = "git", ["repo"] = "git", ["source control"] = "git", ["branches"] = "git", ["commits"] = "git", ["get"] = "git", ["gift"] = "git", ["jet"] = "git",
                ["terminal list"] = "terminals", ["consoles"] = "terminals", ["shells"] = "terminals",
                ["microphone"] = "voice", ["voice control"] = "voice", ["assistant"] = "voice", ["speech"] = "voice",
                ["disk"] = "storage", ["disk space"] = "storage", ["space"] = "storage", ["installed tools"] = "tools", ["packages"] = "tools", ["linux"] = "system",
                ["ambient focus"] = "focus", ["focus mode"] = "focus", ["focus settings"] = "focus", ["ambient"] = "focus", ["the dome"] = "focus",
                ["console"] = "terminals", ["commit"] = "git", ["branch"] = "git", ["account"] = "agents", ["accounts"] = "agents", ["chat"] = "threads", ["password"] = "vault",
                // Italian (the decision model reads them; folded, so accents do not matter)
                ["terminale"] = "terminals", ["terminali"] = "terminals", ["cartella"] = "files", ["cartelle"] = "files", ["gestore dei file"] = "files", ["gestore file"] = "files",
                ["controllo versione"] = "git", ["controllo di versione"] = "git", ["rami"] = "git", ["agenti"] = "agents", ["fornitori"] = "agents", ["modelli"] = "agents",
                ["accesso"] = "agents", ["conversazioni"] = "threads", ["conversazione"] = "threads", ["discussioni"] = "threads", ["progetti"] = "threads",
                ["navigatore"] = "web", ["segnalibri"] = "links", ["collegamenti"] = "links", ["appunti"] = "clipboard", ["catture"] = "clipboard", ["schermate"] = "clipboard", ["istantanee"] = "clipboard", ["competenze"] = "skills", ["abilità"] = "skills", ["estensioni"] = "mqh", ["gesti"] = "gestures", ["gesti delle mani"] = "gestures", ["cassaforte"] = "vault", ["casa forte"] = "vault",
                ["segreti"] = "vault", ["chiavi"] = "vault", ["chiavi api"] = "vault", ["impostazioni"] = "settings", ["impostazione"] = "settings",
                ["preferenze"] = "settings", ["opzioni"] = "settings", ["configurazione"] = "settings", ["voce"] = "voice", ["controllo vocale"] = "voice",
                ["comandi vocali"] = "voice", ["microfono"] = "voice", ["assistente"] = "voice", ["sistema"] = "system", ["focus ambientale"] = "focus",
                ["modalità focus"] = "focus", ["spazio"] = "storage", ["spazio su disco"] = "storage", ["archiviazione"] = "storage", ["disco"] = "storage",
                ["memoria"] = "storage", ["strumenti"] = "tools", ["strumenti installati"] = "tools", ["pacchetti"] = "tools",
            };
            string Label(string id) => id.ToUpperInvariant();

            Voice.Register(new VoiceCommand
            {
                Name = "new_terminal",
                Description = "Open a new Linux terminal",
                Phrases = new[] { "(open|start|create|new|launch|give me) [a|another] [new] (terminal|shell|console)", "new (terminal|shell)" },
                Samples = new[]
                {
                    "i need a new terminal", "spawn another shell", "open one more console", "can i get a fresh terminal", "add a terminal window",
                    "apri un altro terminale", "crea una nuova shell", "voglio un terminale nuovo", "aggiungi una console",
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
                    "chiudi tutti i pannelli", "chiudi ogni pannello aperto", "togli tutte le finestre", "fai pulizia", "libera lo spazio",
                },
                Run = a => { int n = CloseAllPanels(); return n == 0 ? "Nothing to close" : $"Closed {n} panel{(n == 1 ? "" : "s")}"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "open_panel",
                Description = "Open a panel to see or manage something: terminals (open terminals), files (folders), git (repositories, branches, commits), " +
                    "agents (AI providers: which are signed in, logins, accounts, models), threads (agent conversations, workspaces, projects), web (the browser: web pages, localhost), links (bookmarks and local dev servers), " +
                    "clipboard (captures, screenshots of windows), mqh (skills, plugins, scripts, gestures, the harness), skills (the agents' skills), plugins (MQH and gesture plugins), scripts (Lua scripts), " +
                    "gestures (hand gestures), vault (secrets, API keys), settings (look, layout, hands), voice (voice control, AI models)",
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
                    "apri il pannello dei file", "mostrami le impostazioni", "fammi vedere i terminali", "vai alla pagina degli agenti", "apri il pannello del controllo versione",
                    "portami ai segreti", "elenca le discussioni", "voglio navigare sul web", "fammi vedere gli strumenti installati",
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
                    "chiudi il pannello dei file", "chiudi la finestra delle preferenze", "nascondi il browser", "togli la cassaforte", "basta con il controllo versione",
                },
                Run = a => { string id = (string)a["panel"]; return ClosePanel(id) ? "Closed " + Label(id) : "error: no panel " + id; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "find_thread",
                Description = "Find the agent thread (conversation) about a topic and open it",
                Parameters = VoiceCommands.Schema(("query", "string", null, "words about what was done in the thread", true)),
                Phrases = new[]
                {
                    "(find|search|look for|open|show|show me|go to) [me] [the|a|that|our] (thread|conversation|chat|session) [where|in which|about|on|for|with|that|when] {query}",
                    "(search|find|look) [in|through] [the|my] (threads|conversations|chats) [for|about] {query}",
                    "where did we {query}",
                    "(which|what) (thread|conversation|chat|session) (was|is|were) (about|on|for) {query}",
                    "[the] (thread|conversation|chat) (about|on) {query}",
                },
                Samples = new[]
                {
                    "find the conversation about the database migration", "which thread talked about docker", "look up the chat where we fixed the login",
                    "search my threads for payments", "trova la conversazione sulla migrazione del database", "cerca il thread che parla di docker",
                    "in quale chat abbiamo sistemato il login", "cerca nelle conversazioni i pagamenti",
                },
                RunAsync = (a, reply) => VoiceFindThread((string)a["query"], reply),
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
                    "passa al progetto atlas", "cambia progetto", "vai al workspace sito", "apri il progetto chiamato demo",
                },
                RunAsync = (a, reply) => VoiceSwitchWorkspace((string)a["name"], reply),
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
                        ["fisso"] = "fixed", ["fissi"] = "fixed", ["fissa"] = "fixed", ["blocca"] = "fixed", ["bloccati"] = "fixed", ["fermi"] = "fixed",
                        ["fluttuante"] = "float", ["fluttuanti"] = "float", ["segui"] = "float", ["seguimi"] = "float", ["seguire"] = "float", ["seguano"] = "float", ["carosello"] = "float",
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
                    "usa il layout fisso", "metti le finestre fluttuanti", "blocca i pannelli", "fai seguire le finestre", "voglio le finestre fisse",
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
                    "ricentra la vista", "rimetti tutto davanti a me", "centra le finestre", "riporta i pannelli qui", "riallinea la vista",
                },
                Run = a => { RunAction("recenter"); return "Recentered"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "type_text",
                Description = "Type text into the focused terminal (no Enter)",
                Parameters = VoiceCommands.Schema(("text", "string", null, "the text to type", true)),
                Phrases = new[] { "(type|write) {text}" },
                Samples = new[] { "type echo done", "write git status", "scrivi buongiorno", "digita npm install" },
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
                Samples = new[] { "run npm install", "execute make build in the terminal", "esegui npm install", "lancia il comando make", "esegui ls nel terminale" },
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
                    "voglio il focus acceso", "togli il focus", "attiva la modalità concentrazione", "oscura l'ambiente", "fammi rivedere la stanza", "basta focus",
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
                Description = "Change a setting: on / off, a number (percent, metres) or more / less",
                Parameters = VoiceCommands.Schema(("setting", "string", VoiceSettingsCatalog.Select(x => x.Id).ToArray(), "which setting", true), ("value", "string", null, "on, off, a number, + or -", true)),
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
                },
                Samples = new[]
                {
                    "set the volume to 30 percent", "silence the interface sounds", "make the text larger", "lower the opacity", "increase the blur",
                    "set the radius to 2 meters", "turn off the hand rays", "dim the room more",
                    "imposta il volume al 30 per cento", "metti il volume più alto", "abbassa la sfocatura", "attiva i suoni", "disattiva i raggi delle mani",
                    "rendi il testo più grande", "porta il raggio a tre metri", "intensità del focus al settanta per cento", "aumenta la morbidezza",
                },
                Check = a => VoiceSettingFits((string)a["setting"], (string)a["value"]) ? null : "that value does not fit " + (string)a["setting"],
                Run = a => SetSettingByVoice((string)a["setting"], (string)a["value"]),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "show_keyboard",
                Description = "Show the keyboard to type into the terminal",
                Phrases = new[] { "(open|show|bring up) [the] keyboard", "keyboard" },
                Samples = new[] { "can i have the keyboard", "pull up the keyboard", "voglio scrivere con la tastiera", "fammi vedere la tastiera", "tira fuori la tastiera" },
                Run = a => { OpenKeyboard(); return "Keyboard"; },
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
                Phrases = new[] { "(install|get|download|add) [the] {agent} [agent|cli|app]", "(installa|scarica) [il|l'] {agent}" },
                Samples = new[] { "i want opencode on the headset", "put codex in the linux", "set up claude code for me", "installa opencode", "scarica codex", "mi serve claude code" },
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
                Samples = new[] { "i have a key for opencode", "let me paste my openai key for codex", "where do i put the anthropic key", "aggiungi la chiave di opencode", "incolla la chiave per codex", "voglio mettere la mia api key" },
                Run = a => { OpenAgentKey((string)a["agent"]); return "API key for " + (string)a["agent"]; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "table_keyboard",
                Description = "The system keyboard on a table (Surface Keyboard and Touchpad): asks to open it now (both hands flat on the table) or to turn it on in Settings",
                Phrases = new[] { "(open|show|bring up) [the] (table|surface|desk) keyboard", "[the] keyboard on [the] (table|desk)", "(turn on|enable|activate) [the] (table|surface) keyboard" },
                Samples = new[] { "i want to type on the table", "put the keyboard on my desk", "can i use the surface keyboard", "voglio scrivere sul tavolo", "metti la tastiera sul tavolo", "attiva la tastiera da tavolo" },
                Vocabulary = new[] { "surface keyboard", "touchpad" },
                Run = a => { OpenTableKeyboard(); return "Table keyboard"; },
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
                        ["leggero"] = "subtle", ["leggera"] = "subtle", ["delicato"] = "subtle", ["normale"] = "focus", ["medio"] = "focus",
                        ["buio totale"] = "blackout", ["buio"] = "blackout", ["oscuramento totale"] = "blackout",
                    },
                },
                Phrases = new[] { "(use|apply|choose|pick|set|switch to) [the] {preset} [focus] preset", "{preset} [focus] preset", "(focus|ambient) preset {preset}" },
                Samples = new[]
                {
                    "use the subtle focus preset", "switch to the blackout preset", "apply the normal focus preset", "i want the gentle preset",
                    "usa il preset leggero", "attiva il preset buio totale", "metti il preset normale", "preset cinema",
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
                        ["destra"] = "right", ["sinistra"] = "left", ["avanti"] = "right", ["indietro"] = "left", ["successivo"] = "right", ["successiva"] = "right",
                        ["prossimo"] = "right", ["prossima"] = "right", ["precedente"] = "left",
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
                    "gira il carosello a destra", "ruota i pannelli a sinistra", "pannello successivo", "finestra precedente",
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
                    "attiva la selezione", "modalità selezione", "disattiva la selezione", "voglio selezionare una parte del pannello",
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
                Samples = new[] { "open the website github dot com", "browse to example dot org", "apri il sito github punto com", "vai al sito example punto org", "naviga su wikipedia punto org" },
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

        /// "github dot com slash docs" -> https://github.com/docs (English and Italian words for . / :); null when it has no dot.
        public static string SpokenUrl(string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string t = " " + said.Trim().ToLowerInvariant() + " ";
            foreach (var (w, c) in new[] { (" dot ", "."), (" punto ", "."), (" slash ", "/"), (" barra ", "/"), (" colon ", ":"), (" due punti ", ":"), (" dash ", "-"), (" trattino ", "-") })
                t = t.Replace(w, c);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", "").Trim('.', '/');
            if (!t.Contains(".") || t.Length < 4) return null;
            if (!t.StartsWith("http://") && !t.StartsWith("https://")) t = "https://" + t;
            return Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u.ToString() : null;
        }

        // ------------------------------------------------------------------ settings by voice

        private sealed class VoiceSetting
        {
            public string Id, Key, Kind, Unit, Label;
            public float Min, Max;
            /// English names: synonyms of the setting and words the speech-to-text prompt carries.
            public string[] Names;
            /// Other names (Italian, colloquial; VoiceSettingOther): synonyms too, not in the speech-to-text prompt.
            public string[] Other => VoiceSettingOther.TryGetValue(Id, out var o) ? o : new string[0];
        }

        /// More names per setting, by id (kept apart from the catalog so the English rows stay readable).
        private static readonly Dictionary<string, string[]> VoiceSettingOther = new Dictionary<string, string[]>
        {
            ["focus"] = new[] { "focus ambientale", "modalità focus", "modalità concentrazione" },
            ["focus_intensity"] = new[] { "intensità", "intensità del focus", "buia", "buio", "stanza", "room", "dark", "oscuramento" },
            ["focus_radius"] = new[] { "raggio", "raggio della cupola", "cupola", "raggio del focus", "dimensione della cupola" },
            ["focus_blur"] = new[] { "sfocatura", "sfocatura dello sfondo", "blur dello sfondo" },
            ["occlusion"] = new[] { "occlusione", "cose reali davanti", "mani davanti" },
            ["depth_camera"] = new[] { "profondità nitida", "camera di profondità" },
            ["focus_softness"] = new[] { "morbidezza", "morbidezza del bordo", "sfumatura", "sfumatura del bordo" },
            ["opacity"] = new[] { "opacità", "opacità dei pannelli", "trasparenza", "trasparenti", "transparent", "transparency" },
            ["panel_distance"] = new[] { "distanza", "distanza dei pannelli", "distanza delle finestre" },
            ["ui_scale"] = new[] { "finestre", "pannelli", "dimensione del testo", "testo", "grandezza", "text", "bigger" },
            ["carousel_radius"] = new[] { "raggio del carosello", "carosello", "dimensione del carosello" },
            ["bend"] = new[] { "curvatura", "pannelli curvi", "curva" },
            ["sounds"] = new[] { "suoni", "click", "suoni dell'interfaccia" },
            ["volume"] = new[] { "volume dei suoni" },
            ["speak"] = new[] { "risposte vocali", "risposte parlate" },
            ["clap"] = new[] { "applauso", "battito di mani" },
            ["hand_rays"] = new[] { "raggi delle mani", "raggi", "puntatori", "laser" },
            ["foveation"] = new[] { "rendering foveato", "foveazione" },
            ["views_auto_open"] = new[] { "apertura automatica delle viste" },
            ["workspace_module"] = new[] { "modulo workspace", "pannello workspace" },
            ["wrist_menu"] = new[] { "menu da polso", "menu sul polso", "menu al polso", "menu radiale" },
        };

        /// Settings that voice (and the local LLM) can change: id, the settings key, kind, range, unit, spoken names.
        /// The settings voice can change (id, key, kind, range, unit, label, spoken names), for the voice bench.
        public static JArray VoiceSettingsForBench() => new JArray(VoiceSettingsCatalog.Select(x => new JObject
        {
            ["id"] = x.Id, ["key"] = x.Key, ["kind"] = x.Kind, ["min"] = x.Min, ["max"] = x.Max, ["unit"] = x.Unit, ["label"] = x.Label, ["names"] = new JArray(x.Names), ["other"] = new JArray(x.Other),
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
            new VoiceSetting { Id = "ui_scale", Key = "uiScale", Kind = "number", Min = 0.8f, Max = 1.8f, Unit = "x", Label = "UI scale", Names = new[] { "ui scale", "scale", "panel size", "window size", "windows", "panels", "text size", "size" } },
            new VoiceSetting { Id = "carousel_radius", Key = "carouselRadius", Kind = "number", Min = 0.6f, Max = 1.6f, Unit = "m", Label = "Carousel radius", Names = new[] { "carousel radius", "ring radius", "carousel size", "carousel" } },
            new VoiceSetting { Id = "bend", Key = "bendPanels", Kind = "bool", Label = "Bend panels", Names = new[] { "bend", "bending", "bend panels", "curved panels", "curve" } },
            new VoiceSetting { Id = "sounds", Key = "sounds", Kind = "bool", Label = "Sounds", Names = new[] { "sounds", "sound", "ui sounds", "clicks" } },
            new VoiceSetting { Id = "volume", Key = "soundVolume", Kind = "number", Min = 0f, Max = 1f, Unit = "%", Label = "Volume", Names = new[] { "volume", "sound volume" } },
            new VoiceSetting { Id = "speak", Key = "voice.speak", Kind = "bool", Label = "Spoken answers", Names = new[] { "spoken answers", "speak answers", "spoken replies", "voice replies", "talking back" } },
            new VoiceSetting { Id = "clap", Key = "gestures.clapEnabled", Kind = "bool", Label = "Clap gesture", Names = new[] { "clap", "clap gesture", "clapping" } },
            new VoiceSetting { Id = "hand_rays", Key = "gestures.rayVisible", Kind = "bool", Label = "Hand rays", Names = new[] { "hand rays", "rays", "pointer rays", "lasers" } },
            new VoiceSetting { Id = "foveation", Key = "foveation", Kind = "bool", Label = "Foveated rendering", Names = new[] { "foveation", "foveated rendering" } },
            new VoiceSetting { Id = "views_auto_open", Key = "viewsAutoOpen", Kind = "bool", Label = "Agent views open by themselves", Names = new[] { "auto open views", "agent views auto open", "views auto open" } },
            new VoiceSetting { Id = "workspace_module", Key = "threadWorkspace", Kind = "bool", Label = "Thread workspace", Names = new[] { "workspace module", "thread workspace", "workspace panel" } },
            new VoiceSetting { Id = "wrist_menu", Key = "wristMenu", Kind = "bool", Label = "Main menu on the wrist", Names = new[] { "wrist menu", "menu on the wrist", "radial menu", "watch menu" } },
            new VoiceSetting { Id = "orb", Key = "orb", Kind = "bool", Label = "Orb", Names = new[] { "orb", "the orb", "energy sphere", "sphere", "companion" } },
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
            return VoiceCommands.TryNumber(raw, vs.Min, vs.Max, vs.Unit, out _);
        }

        /// set_setting: on / off / toggle, a number ("0", "50 percent", "two meters", "max"), or more / less ("+",
        /// "-", "bigger", "a bit less": a tenth of the range). Applies it, moves the matching slider or toggle, and says
        /// what it is now.
        public string SetSettingByVoice(string id, string value)
        {
            var vs = VoiceSettingsCatalog.FirstOrDefault(x => x.Id == id);
            if (vs == null) return "error: no setting " + id;
            value = MoreOrLess(value);
            string v = VoiceCommands.Normalize(value ?? "");
            if (vs.Kind == "bool")
            {
                bool cur = Settings.Get(vs.Key) == "true";
                bool on = OnWords.Contains(v) || value == "+" || (v == "toggle" || v == "switch" ? !cur : false);
                bool off = OffWords.Contains(v) || value == "-";
                if (!on && !off && v != "toggle" && v != "switch") return "error: " + vs.Label + " is on or off";
                if (vs.Id == "focus") SetFocus(on, false);
                else { Settings.Set(vs.Key, on ? "true" : "false"); OnSettingsChanged(); }
                SyncSettingsControls();
                return vs.Label + (on ? " on" : " off");
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
            var sliders = new Dictionary<string, string> { { "DISTANCE", "panelDistance" }, { "CAROUSEL RADIUS", "carouselRadius" }, { "UI SCALE", "uiScale" },
                { "PANEL OPACITY", "panelOpacity" }, { "VOLUME", "soundVolume" }, { "RADIUS", "focusRadius" }, { "INTENSITY", "focusIntensity" }, { "SOFTNESS", "focusSoftness" }, { "BLUR", "focusBlur" } };
            var toggles = new Dictionary<string, string> { { "Bend panels", "bendPanels" }, { "Sounds", "sounds" }, { "Clap gesture", "gestures.clapEnabled" }, { "Show hand rays", "gestures.rayVisible" },
                { "Foveated", "foveation" }, { "Ambient focus", "focusEnabled" }, { "Speak answers", "voice.speak" },
                { "Real things in front", "occludeUi" }, { "Sharp depth", "depthCamera" }, { "Main menu on the wrist", "wristMenu" }, { "Orb", "orb" } };
            foreach (var p in _panels.Values)
                foreach (var e in p.Elements)
                {
                    if (e is UiSlider sl && sliders.TryGetValue(sl.Text ?? "", out var key) && float.TryParse(Settings.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) sl.Set(f);
                    else if (e is UiToggle tg) { var k = toggles.FirstOrDefault(kv => (tg.Text ?? "").StartsWith(kv.Key)); if (k.Value != null) tg.Set(Settings.Get(k.Value) == "true"); }
                }
            _focusButton?.SetSelected(Settings.focusEnabled);
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

        private void VoiceFindThread(string query, Action<string> reply)
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
                OpenThread((string)best.x["id"]);
                reply("Opened “" + Short(best.title, 50) + "”");
            });
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
                    _vHud.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
                    var dt = dock.transform;
                    _vHud.transform.SetPositionAndRotation(dt.position + dt.up * (dock.Height * 0.5f + _vHud.Height * 0.5f + 0.015f) * dock.RestLossyScale.y, dt.rotation);
                }
                else if (Head != null) { _vHud.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f); _vHud.PlaceInFront(Head, 0.62f, 0.18f); }
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
            _vHud.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
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
            var p = Panel<UiPanel>("voice", "VOICE · LOCAL AI", 0.74f, 0.60f);
            var c = p.Content;
            float leftW = 0.33f;
            var ls = new UiStack(new Rect(c.x, c.y, leftW, c.height), t.Gap);
            _vStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = ls.Next(0.05f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            _vModels = p.Add(new UiLabel("", t.SmallSize) { Rect = ls.Next(0.15f), Wrap = true, Align = TextAlignmentOptions.TopLeft, UseMono = true, Color = t.TextDim });
            _vInstall = p.Add(new UiProgress(0f) { Rect = ls.Next(0.008f) });
            AddButtons(p, ls.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("INSTALL MODELS", () => { EnsureVoiceService(); After(0.1f, InstallVoiceModels); }),
                ("START / STOP", () => { if (Qvac.IsRunning || Qvac.IsBusyStarting) Qvac.StopService(); else Qvac.StartService(); RefreshVoicePanel(); }),
            }, primary: 0);
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
            p.Add(new UiToggle("Start the AI service with the app", Settings.voice.enabled, v => { Settings.voice.enabled = v; OnSettingsChanged(); }) { Rect = ls.Next(0.03f) });
            // right column: language and the reply voice on top (the left one is full), then the commands
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            const float pickLabel = 0.075f; // the same label column on every row: the buttons line up
            string[] langs = { "en", "it", "auto" };
            p.Add(new UiSegmented("LANGUAGE", new[] { "EN", "IT", "AUTO" }, Math.Max(0, Array.IndexOf(langs, Settings.voice.language)), i => { Settings.voice.language = langs[i]; OnSettingsChanged(); PushVoiceConfig(); }) { Rect = rs.Next(0.032f), LabelWidth = pickLabel });
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

            p.Add(new UiLabel("COMMANDS (say them, or anything close)", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vCmdList = p.Add(new UiList(0.034f, (item, idx) =>
            {
                var cmd = Voice.Find(item.Tag as string);
                if (cmd != null) Toast($"{cmd.Name}: {cmd.Description}" + (cmd.Phrases.Length > 0 ? " · say “" + string.Join("”, “", cmd.Phrases.Take(3).Select(VoiceCommands.Example)) + "”" : ""), "info");
            }) { Rect = rs.Next(0.21f) });
            p.Add(new UiLabel("LAST", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vHistory = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Rest(), Wrap = true, Align = TextAlignmentOptions.TopLeft, UseMono = true, Color = t.TextDim });
            RefreshVoicePanel();
        }

        private string DeciderCachePath => System.IO.Path.Combine(Application.persistentDataPath, "voice-decider-vectors.bin");

        /// Example vectors from the last run (text -> vector), so a start embeds only what changed.
        private void LoadDeciderCache()
        {
            try
            {
                if (!System.IO.File.Exists(DeciderCachePath)) return;
                using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(DeciderCachePath)))
                {
                    if (r.ReadString() != DeciderModelTag) return;
                    int n = r.ReadInt32(), dim = r.ReadInt32();
                    if (n < 0 || n > 200000 || dim < 8 || dim > 4096) return;
                    for (int i = 0; i < n; i++)
                    {
                        string t = r.ReadString();
                        var v = new float[dim];
                        for (int k = 0; k < dim; k++) v[k] = r.ReadSingle();
                        Decider.Seed(t, v);
                    }
                    _deciderSavedCount = n;
                }
            }
            catch (Exception ex) { Log("voice decider cache: " + ex.Message); }
        }

        private void SaveDeciderCache()
        {
            try
            {
                var c = Decider.Cache;
                if (c.Count == 0 || Decider.LastEmbedded == 0 && Decider.LastPruned == 0 && c.Count == _deciderSavedCount) return;
                int dim = c.Values.First().Length;
                using (var w = new System.IO.BinaryWriter(System.IO.File.Create(DeciderCachePath)))
                {
                    w.Write(DeciderModelTag);
                    w.Write(c.Count);
                    w.Write(dim);
                    foreach (var kv in c) { w.Write(kv.Key); foreach (var f in kv.Value) w.Write(f); }
                }
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
            _vStatus.Set($"AI service: <b>{svc}</b>{mem}\n{Qvac?.Status}" + (Decider != null && Settings.voice.decide ? "\ndecisions: " + Decider.Status : ""));
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

        /// voice status | list | listen | stop | cancel | install | warm | start | stop-service | threads N | lang X | speak on|off
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
                case "lang": if (rest.Length > 0) { Settings.voice.language = rest; OnSettingsChanged(); PushVoiceConfig(); } return "language=" + Settings.voice.language;
                case "speak": Settings.voice.speak = rest != "off"; OnSettingsChanged(); return "speak=" + Settings.voice.speak;
                case "llm": Settings.voice.useLlm = rest != "off"; OnSettingsChanged(); return "useLlm=" + Settings.voice.useLlm;
                default: return "error: voice status | list | tools | registry | match <text> | decide <text> | decider on|off | fallback on|off | listen | stop | cancel | start | stop-service | install | threads N | lang en|it|auto | speak on|off | llm on|off | run <text> | say <text> | wav <file> | warm";
            }
        }

        /// The control-port commands that answer later: voice run <text> | say <text> | wav <file> | warm | decide <text>.
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
                default: return false;
            }
        }

        private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;
    }
}
