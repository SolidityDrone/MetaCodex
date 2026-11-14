using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THREAD as a chat (owner 2026-11-11, docs/thread-view.md): the prompt in a bubble (pinned on top once it scrolls
    /// away), the agent's work under one header per exchange (thinking, commands, edits, reads; open while it works,
    /// closed after), the answer in full, streaming while it is written. A status line says what the agent does now,
    /// for how long and how quiet it has been (may be stuck); the header row shows the provider's usage limits beside
    /// the context ring. ◉ dictates into the composer. Timelines come as deltas (MQH's afterTag), so polling fast while
    /// the agent works stays cheap. The same model feeds the session context (ThreadPulse, ShellContext).
    public sealed partial class MetaCodexShell
    {
        private UiChatView _thTimeline; // the chat (the attach modal hides it, ShellCaptures)
        private UiLabel _thStatus, _thLimits;
        private UiButton _thNewBtn, _thMic;
        private ThreadTimeline _tlModel = new ThreadTimeline();
        private ThreadPulse _thPulse;
        private JObject _tlFields = new JObject();
        private string _tlTag;
        private bool _tlInFlight;
        private float _tlSentAt, _nextThreadMeta, _nextStatusTick;
        private readonly Dictionary<string, JObject> _usage = new Dictionary<string, JObject>();
        private readonly Dictionary<string, float> _usageAskedAt = new Dictionary<string, float>();
        private const int ChatSegments = 8; // the last 8 exchanges: enough to read back, small to parse
        private long _clockForTest;
        private static readonly string[] StatusSpinner = { "◐", "◓", "◑", "◒" };

        public ThreadPulse OpenThreadPulse => _thPulse;
        public ThreadTimeline OpenThreadModel => _tlModel;
        public UiChatView ChatForTest => _thTimeline;
        public string ThreadStatusTextForTest => _thStatus?.Text ?? "";
        public string LimitsTextForTest => _thLimits?.Text ?? "";
        /// Self-tests: a fixed "now" (ms epoch) for the pulse; 0 = the real clock.
        public void SetClockForTest(long ms) { _clockForTest = ms; RefreshThreadPulse(); }

        private long NowMs => _clockForTest > 0 ? _clockForTest : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ------------------------------------------------------------------ building

        private void BuildThreadChat(UiPanel p, Rect statusRow)
        {
            var t = UiTheme.Current;
            _thSendNow = null; // the panel is built again (RebuildPanels): SEND NOW comes back on the new one
            _thStatusFull = default;
            _thTimeline = p.Add(new UiChatView
            {
                Rect = TimelineRect(false),
                TextSize = t.SmallSize * 1.08f,
                Empty = "Nothing here yet. Write below, or ◉ to say it.",
                OnToggled = (b, open) => RefreshNewButton(),
            });
            _tlUp = p.Add(new UiButton("▲", () => { _thTimeline.ScrollBy(6); RefreshNewButton(); }, UiButtonStyle.Ghost));
            _tlDown = p.Add(new UiButton("▼", () => { _thTimeline.ScrollBy(-6); RefreshNewButton(); }, UiButtonStyle.Ghost));
            _thNewBtn = p.Add(new UiButton("↓ NEW", () => { _thTimeline.ScrollToBottom(); RefreshNewButton(); }, UiButtonStyle.Primary) { TextSize = t.SmallSize * 0.95f });
            _thStatus = p.Add(new UiLabel("", t.SmallSize * 1.04f) { Rect = statusRow, Color = t.TextDim });
            _thNewBtn.SetVisible(false);
            p.Tick += _ => { TickPanelPulse(p); TickThreadView(); };
        }

        // ------------------------------------------------------------------ "it answered": the orb says so, the window pulses

        private float _panelPulseUntil;
        private const float PanelPulseSeconds = 2.4f;
        public bool ThreadPanelPulsingForTest => _panelPulseUntil > 0f;
        public string LastThreadNoteForTest { get; private set; }

        /// A thread finished (answered, or stopped on an error) or needs you: the orb casts a callout (a tap opens the
        /// thread); when that thread is open, its window pulses three times and the answer glows (owner 2026-11-11:
        /// "so you notice the answer is fully complete"). Only on a change seen live: nothing for what was already
        /// done when the app started or the thread was first looked at.
        private void NotifyThreadChange(string id, ThreadPulse before, ThreadPulse now)
        {
            if (before == null || now == null || id == null) return;
            // nothing to say: no words built (this runs at every pulse, once a second while THREAD is open)
            if (!(before.Working && now.State == "idle") && !(now.State == "error" && before.State != "error") && !(now.State == "waiting" && before.State != "waiting")) return;
            string title = "“" + ThreadPulse.Clip(now.Title, 38) + "”";
            bool open = id == _threadOpen && IsPanelOpen("thread");
            string project = ThreadProjectName(id), name = "“" + ThreadPulse.Clip(now.Title, 40) + "”";
            string thread = project != null ? "The thread on " + project : "The thread " + name;
            string agent = project != null ? "An agent on " + project : "The agent in " + name;
            if (before.Working && now.State == "idle")
            {
                string gist = now.AnswerGist.Length > 0 ? ": " + ThreadPulse.Clip(now.AnswerGist, 64) : "";
                CastThreadNote(title + " answered" + gist, "ok", id);
                if (open) { PulseThreadPanel(); FlashAnswer(); }
                SayThreadAlert(thread + " is done.");
            }
            else if (now.State == "error" && before.State != "error")
            {
                CastThreadNote(title + " stopped on an error" + (now.ErrorText.Length > 0 ? ": " + ThreadPulse.Clip(now.ErrorText, 60) : ""), "error", id);
                if (open) PulseThreadPanel();
                SayThreadAlert(thread + " stopped on an error.");
            }
            else if (now.State == "waiting" && before.State != "waiting")
            {
                CastThreadNote(title + " needs you: " + ThreadPulse.Clip(now.Doing, 60), "warn", id);
                if (open) PulseThreadPanel();
                SayThreadAlert(agent + (now.Doing.StartsWith("waiting for your approval") ? " needs your approval."
                    : now.Doing.StartsWith("waiting for your answer") ? " is asking you something." : " needs you."));
            }
        }

        // ---- spoken alerts (owner 2026-11-13: "when some task is completed, or requires action it could actually speak:
        // 'Thread on <project> is done', 'An agent is requiring allowance'"): VOICE > "Say when a thread is done or needs
        // you" (voice.speakAlerts, on). The orb says them while it is quiet; one that comes while the user talks or the
        // orb speaks waits (the newest two are kept) and is said after, within a minute.
        private readonly List<(string text, float at)> _alertQueue = new List<(string, float)>();
        public string LastThreadAlertForTest { get; private set; }

        /// The thread's project as MQH names it (null when not known yet).
        private string ThreadProjectName(string id)
        {
            var row = _threads.FirstOrDefault(x => (string)x["id"] == id) ?? _ctxThreadRows?.FirstOrDefault(x => (string)x["id"] == id);
            string pid = (string)row?["projectId"];
            var proj = pid != null ? _projects.FirstOrDefault(x => (string)x["id"] == pid) : null;
            string name = (string)proj?["name"] ?? (string)row?["projectName"];
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// Self-tests: a thread's pulse changed (as the background poll sees it).
        public void ThreadPulseChangeForTest(string id, ThreadPulse before, ThreadPulse now) => NotifyThreadChange(id, before, now);

        private void SayThreadAlert(string text)
        {
            if (Settings == null || !Settings.voice.speakAlerts || string.IsNullOrEmpty(text)) return;
            LastThreadAlertForTest = text;
            if (SpeakForTest == null && (Qvac == null || !Qvac.IsRunning)) return; // no voice: the callout says it
            _alertQueue.Add((text, Time.unscaledTime));
            while (_alertQueue.Count > 2) _alertQueue.RemoveAt(0);
            TickThreadAlerts();
        }

        /// Called by TickVoice: says the waiting alerts once the user is not talking and the orb is quiet.
        private void TickThreadAlerts()
        {
            if (_alertQueue.Count == 0) return;
            _alertQueue.RemoveAll(a => Time.unscaledTime - a.at > 60f);
            if (_alertQueue.Count == 0 || Speaking || VoiceState != "idle") return;
            string text = string.Join(" ", _alertQueue.Select(a => a.text));
            _alertQueue.Clear();
            VoiceSay(text);
        }

        private void CastThreadNote(string text, string kind, string threadId)
        {
            LastThreadNoteForTest = text;
            Log("thread note: " + text);
            if (Toasts == null) return;
            if (Toasts.Register == null && Router != null) { Toasts.Register = Router.Register; Toasts.Unregister = Router.Unregister; }
            Toasts.Show(text + "  ›", kind, kind == "ok" ? 6f : 7f, () => { if (threadId != _threadOpen || !IsPanelOpen("thread")) OpenThread(threadId); else PulseThreadPanel(); });
        }

        private void PulseThreadPanel() => _panelPulseUntil = Time.unscaledTime + PanelPulseSeconds;

        private void FlashAnswer()
        {
            var ans = _tlModel?.Exchanges.LastOrDefault(e => e.Prompt != null)?.Answer;
            if (ans != null) _thTimeline?.Flash(ans.Id, PanelPulseSeconds);
        }

        /// The window's frame lights up three times (its highlight, as a dialog's), then goes back to rest.
        private void TickPanelPulse(UiPanel p)
        {
            if (_panelPulseUntil <= 0f) return;
            float left = _panelPulseUntil - Time.unscaledTime;
            if (left <= 0f) { _panelPulseUntil = 0f; p.SetHighlight(0f); return; }
            float u = 1f - left / PanelPulseSeconds;
            p.SetHighlight(0.95f * (0.5f - 0.5f * Mathf.Cos(u * Mathf.PI * 6f)));
        }

        private void LayoutChatExtras(Rect chat)
        {
            _thNewBtn?.SetRect(new Rect(chat.xMax - 0.082f, chat.yMax - 0.030f, 0.078f, 0.026f));
        }

        private void RefreshNewButton()
        {
            if (_thNewBtn == null || _thTimeline == null) return;
            bool show = !_thTimeline.AtBottom && !_amOpen;
            _thNewBtn.SetVisible(show);
            if (show) _thNewBtn.SetText(_thTimeline.NewLines > 0 ? "↓ NEW" : "↓ END");
        }

        /// Every frame while THREAD is open: the status line (timers, spinner) 4 times a second, the usage limits
        /// once a minute, the mic button.
        private void TickThreadView()
        {
            if (Time.unscaledTime < _nextStatusTick) return;
            _nextStatusTick = Time.unscaledTime + 0.25f;
            TickComposerChat(); // a drag of the message box: the chat catches up (at most four times a second)
            if (_threadOpen != null && _tlModel != null && (int)(Time.unscaledTime * 4f) % 4 == 0) RefreshThreadPulse();
            else ShowThreadStatus();
            if (_thMic != null)
            {
                _thMic.SetText(Dictating && _dictField == _thComposer ? "■" : "◉");
                _thMic.SetSelected(Dictating && _dictField == _thComposer);
            }
            if (_threadOpen != null) RefreshUsageLimits(false);
            RefreshNewButton();
        }

        // ------------------------------------------------------------------ the timeline: full answers and deltas

        private void ResetThreadChat()
        {
            ResetComposerHeight(); // another thread (or a new draft) opens: the message box is one row again
            _tlTag = null;
            _tlInFlight = false;
            _tlModel = new ThreadTimeline();
            _tlFields = new JObject();
            _thPulse = null;
            _thTimeline?.ResetState();
            _thTimeline?.SetBlocks(new List<ChatBlock>());
            _thStatus?.Set("");
            ShowLimits();
        }

        /// One answer of the timeline, prepared on the request's pool thread: the rows merged, the model parsed, the
        /// pulse and the chat blocks built. The main thread only moves the rows over and lays the chat out.
        private sealed class TimelineUpdate
        {
            public bool Ok, Changed, Full, UsageSet;
            public string Tag, BaseTag;
            public long MaxSeq;
            public JArray BaseRows, FullRows;
            public JObject Delta, Fields;
            public List<JToken> Merged;
            public JToken Usage;
            public ThreadTimeline Model;
            public List<ChatBlock> Blocks;
            public ThreadPulse Pulse;                          // made on the pool thread with the blocks
            public Dictionary<string, ChangedFile> Touched;     // the files the timeline shows changing (pool thread)
            public string TouchedRoot;
        }

        /// Asks for the timeline (a delta when MQH still has the last answer); one request at a time.
        private void RequestTimeline(string id)
        {
            if (_tlInFlight && Time.unscaledTime - _tlSentAt < 12f) return;
            _tlInFlight = true;
            _tlSentAt = Time.unscaledTime;
            string tag = _tlTag;
            var baseRows = _tlRaw;
            var baseFields = _tlFields;
            var row = _threadInfo;
            long now = NowMs;
            string touchRoot = _sbRoot;
            Mqh.CallThen(System.Net.Http.HttpMethod.Get, MqhClient.TimelinePath(id, tag, ChatSegments), null,
                r => PrepareTimeline(r as JObject, tag, baseRows, baseFields, row, now, touchRoot),
                u => { _tlInFlight = false; ApplyTimeline(id, u); });
        }

        /// Pool thread: reads the old rows and fields (the main thread does not change them while a request is out).
        private TimelineUpdate PrepareTimeline(JObject resp, string baseTag, JArray baseRows, JObject baseFields, JObject threadRow, long now, string touchRoot = null)
        {
            var u = new TimelineUpdate { BaseTag = baseTag, BaseRows = baseRows };
            if (resp == null) return u;
            u.Tag = (string)resp["tag"];
            u.MaxSeq = resp["maxSeq"]?.Type == JTokenType.Integer ? (long)resp["maxSeq"] : 0;
            var fields = new JObject();
            IEnumerable<JToken> rows;
            if (resp["delta"] is JObject d)
            {
                if ((string)d["afterTag"] != baseTag) return u; // an answer to an older ask: the next poll takes all
                var ups = d["upsertRows"] as JArray;
                var df = d["fields"] as JObject;
                u.Changed = ups != null && ups.Count > 0 || d["rowOrder"] is JArray || df != null && df.Count > 0;
                if (!u.Changed) { u.Ok = true; return u; }
                u.Merged = ThreadTimeline.MergeRows(baseRows, d);
                if (u.Merged == null) return u;
                u.Delta = d;
                foreach (var prop in (baseFields ?? new JObject()).Properties()) fields[prop.Name] = prop.Value; // copies (small)
                if (df != null)
                    foreach (var prop in df.Properties())
                    {
                        if (prop.Name == "contextWindowUsage") { u.UsageSet = true; u.Usage = prop.Value.Type == JTokenType.Null ? null : prop.Value; continue; }
                        if (prop.Name == "timelinePage" || prop.Name == "providerCommands") continue;
                        fields[prop.Name] = prop.Value;
                    }
                rows = u.Merged;
            }
            else
            {
                u.Full = true;
                u.Changed = true;
                u.FullRows = resp["rows"] as JArray ?? new JArray();
                foreach (var prop in resp.Properties())
                    if (prop.Name != "rows" && prop.Name != "providerCommands" && prop.Name != "timelinePage") fields[prop.Name] = prop.Value;
                u.UsageSet = true;
                u.Usage = resp["contextWindowUsage"];
                rows = u.FullRows;
            }
            u.Fields = fields;
            u.Model = ThreadTimeline.Parse(rows, fields, StatusOfRow(threadRow));
            u.Pulse = ThreadPulse.From(u.Model, threadRow, now);
            u.Blocks = ChatBlocks(u.Model, u.Pulse);
            // the files it changed, here too (0.7.19: walked over the whole timeline on the main thread at every update)
            u.TouchedRoot = touchRoot;
            u.Touched = ThreadChanges.FromTimeline(rows, touchRoot);
            u.Ok = true;
            return u;
        }

        /// Main thread: the prepared answer in (if it is still about what is open and what it was asked after).
        private void ApplyTimeline(string id, TimelineUpdate u)
        {
            if (u == null || id != _threadOpen || u.BaseTag != _tlTag) return;
            if (!u.Ok || !u.Full && u.Changed && !ReferenceEquals(u.BaseRows, _tlRaw)) { _tlTag = null; return; } // the next poll takes all
            _tlTag = u.Tag ?? _tlTag;
            if (u.UsageSet) _thUsage = u.Usage;
            ShowContextUsage();
            if (!u.Changed) return;
            bool first = _tlMaxSeq < 0;
            _tlMaxSeq = u.MaxSeq;
            if (_tlOpenSeq < 0) _tlOpenSeq = u.MaxSeq;
            // each step timed: a slow update says which part (0.7.19: 2-3 s main-thread callbacks every 5-6 s while a
            // thread worked, growing with the conversation)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _tlRaw = u.Full ? u.FullRows : ThreadTimeline.TakeRows(u.Merged, _tlRaw, u.Delta);
            _tlFields = u.Fields;
            _tlModel = u.Model;
            double tTake = sw.Elapsed.TotalMilliseconds;
            RefreshThreadPulse(u.Pulse);
            double tPulse = sw.Elapsed.TotalMilliseconds;
            _thTimeline?.SetBlocks(u.Blocks);
            double tBlocks = sw.Elapsed.TotalMilliseconds;
            RefreshNewButton();
            if (u.Touched != null && u.TouchedRoot == _sbRoot) WsTrackFound(u.Touched); else WsTrackRows(_tlRaw);
            double tWs = sw.Elapsed.TotalMilliseconds;
            ScanViews(_tlRaw, id, first);
            double tAll = sw.Elapsed.TotalMilliseconds;
            if (tAll > 40)
                Log($"[slow timeline] {tAll:F0} ms ({_tlRaw.Count} rows, {u.Blocks?.Count ?? 0} blocks, {_thTimeline?.LastRelaid ?? 0} laid out again{(u.Full ? ", full" : "")}): take {tTake:F0}, pulse {tPulse - tTake:F0}, blocks {tBlocks - tPulse:F0}, files {tWs - tBlocks:F0}, views {tAll - tWs:F0}");
            // an approval step appeared: ask for the interactions now (the ALLOW / DENY row)
            if (_tlModel.Last != null && _tlModel.Last.Steps.Any(s => (s.Kind == StepKind.Approval || s.Kind == StepKind.Question) && s.Live) && !_askShown) _nextThreadMeta = 0f;
        }

        /// Self-tests: an answer from MQH through the same two steps, at once.
        public void ApplyTimelineForTest(JObject response) => ApplyTimeline(_threadOpen, PrepareTimeline(response, _tlTag, _tlRaw, _tlFields, _threadInfo, NowMs));

        private static string StatusOfRow(JObject row) => (string)row?["runtime"]?["displayStatus"] ?? (string)row?["status"];

        private void RebuildChat()
        {
            _tlModel = ThreadTimeline.Parse(_tlRaw, _tlFields, StatusOfRow(_threadInfo));
            RefreshThreadPulse();
            _thTimeline?.SetBlocks(ChatBlocks(_tlModel, _thPulse));
            RefreshNewButton();
        }

        private void RefreshThreadPulse(ThreadPulse ready = null)
        {
            if (_threadOpen == null || _tlModel == null) return;
            _thPulse = ready ?? ThreadPulse.From(_tlModel, _threadInfo, NowMs);
            CtxPulse(_threadOpen, _thPulse);
            ShowThreadStatus();
        }

        // ------------------------------------------------------------------ the model as chat blocks

        /// The model as chat blocks (pure: runs on the pool thread too; `pulse` gives the live header its words).
        public List<ChatBlock> ChatBlocks(ThreadTimeline tl, ThreadPulse pulse)
        {
            var list = new List<ChatBlock>();
            foreach (var ex in tl.Exchanges)
            {
                string exKey = ex.Prompt?.Id ?? ex.Key;
                if (ex.Prompt != null) list.Add(new ChatBlock { Key = ex.Prompt.Id, Kind = ChatKind.Prompt, Body = ex.Prompt.Text, Exchange = exKey, BodyMaxChars = 3000 });
                var shown = ex.Steps.Where(NoticeShown).ToList();
                var outside = new List<ChatBlock>(); // errors and questions to the user stay visible when the work closes
                int inGroup = shown.Count(s => !Outside(s));
                if (inGroup > 0)
                {
                    string wk = exKey + ":work";
                    list.Add(new ChatBlock
                    {
                        Key = wk, Kind = ChatKind.Work, Collapsible = true, Expanded = ex.Live, Live = ex.Live, Exchange = exKey,
                        Head = WorkHead(ex), Right = ClockOf(ex.StartedAt),
                        Preview = ex.Live && pulse != null ? ThreadPulse.Clip(pulse.Doing, 110) : "",
                    });
                    for (int i = 0; i < shown.Count; i++)
                    {
                        var s = shown[i];
                        if (Outside(s)) { outside.Add(StepBlock(s, null, exKey)); continue; }
                        // reads and searches in a row: one line that opens to the list
                        if ((s.Kind == StepKind.Read || s.Kind == StepKind.Search) && !s.Live)
                        {
                            int j = i;
                            while (j + 1 < shown.Count && (shown[j + 1].Kind == StepKind.Read || shown[j + 1].Kind == StepKind.Search) && !shown[j + 1].Live) j++;
                            if (j > i) { list.Add(ReadsBlock(shown.GetRange(i, j - i + 1), wk, exKey)); i = j; continue; }
                        }
                        list.Add(StepBlock(s, wk, exKey));
                    }
                }
                else foreach (var s in shown) outside.Add(StepBlock(s, null, exKey));
                list.AddRange(outside);
                if (ex.Answer != null)
                    list.Add(new ChatBlock { Key = ex.Answer.Id, Kind = ChatKind.Answer, Body = StripViewBlocks(ex.Answer.Text), Live = ex.Answer.Live, Exchange = exKey });
            }
            return list;
        }

        private static bool Outside(ThreadStep s) => s.Kind == StepKind.Error || (s.Kind == StepKind.Approval || s.Kind == StepKind.Question) && s.Live;

        // provisioning is noise once it worked; other notices (compaction, interruption, warnings) stay
        private static bool NoticeShown(ThreadStep s) => s.Kind != StepKind.Notice || s.Status == "error" || !s.Title.StartsWith("Provisioned", StringComparison.Ordinal);

        private static string ClockOf(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "";

        private const string Dim = "<color=#9DBDD2>", Endc = "</color>";

        private static string WorkHead(ThreadExchange ex)
        {
            string stats = WorkStats(ex);
            string tail = stats.Length > 0 ? "  " + Dim + stats + Endc : "";
            if (ex.Live) return "◌ <color=#8FEBFF>Working</color>" + tail;
            int secs = (int)ex.Seconds;
            string verb = ex.Status == "error" ? "<color=#FF5468>Failed</color>" : ex.Status == "interrupted" ? "<color=#FFB547>Stopped</color>" : "Worked";
            return "<color=#9DBDD2>⚙</color> " + verb + (secs > 0 ? " " + ThreadPulse.Clock(secs) : "") + tail;
        }

        public static string WorkStats(ThreadExchange ex)
        {
            var parts = new List<string>();
            if (ex.Thoughts > 0) parts.Add(ThreadPulse.Plural(ex.Thoughts, "thought"));
            if (ex.Commands > 0) parts.Add(ThreadPulse.Plural(ex.Commands, "command") + (ex.Failed > 0 ? $" (<color=#FF5468>{ex.Failed} failed</color>)" : ""));
            else if (ex.Failed > 0) parts.Add($"<color=#FF5468>{ex.Failed} failed</color>");
            if (ex.Reads + ex.Searches > 0) parts.Add(ThreadPulse.Plural(ex.Reads + ex.Searches, "read"));
            if (ex.Files.Count > 0) parts.Add(ThreadPulse.Plural(ex.Files.Count, "file") + (ex.Added + ex.Removed > 0 ? $" <color=#4DFFA6>+{ex.Added}</color> <color=#FF5468>−{ex.Removed}</color>" : ""));
            int tools = ex.Tools + ex.Agents + ex.Webs;
            if (tools > 0) parts.Add(ThreadPulse.Plural(tools, "tool call"));
            return string.Join(" · ", parts);
        }

        private string StepRight(ThreadStep s)
        {
            if (s.Live) return "<color=#8FEBFF>running</color>";
            string took = s.Seconds >= 1 ? " " + ThreadPulse.Dur((int)Math.Round(s.Seconds)) : "";
            if (s.Status == "interrupted") return "<color=#FFB547>stopped</color>";
            if (s.Failed) return "<color=#FF5468>✕" + (s.ExitCode.HasValue ? " exit " + s.ExitCode.Value : "") + "</color>" + took;
            return s.Kind == StepKind.Command || s.Kind == StepKind.Tool || s.Kind == StepKind.Agent ? "<color=#4DFFA6>✓</color>" + took : took.Trim();
        }

        private ChatBlock StepBlock(ThreadStep s, string parent, string exKey)
        {
            var b = new ChatBlock { Key = s.Id.Length > 0 ? s.Id : (parent ?? exKey) + ":" + s.Kind + ":" + s.StartedAt, Kind = ChatKind.Step, Parent = parent, Indent = parent != null ? 1 : 0, Exchange = exKey, Live = s.Live };
            string E(string x) => UiChatView.Esc(x);
            switch (s.Kind)
            {
                case StepKind.Thinking:
                {
                    b.Kind = ChatKind.Thinking;
                    string headline = ThreadPulse.Clip(ThreadPulse.FirstSentence(s.Text), 96);
                    b.Head = s.Live ? "◌ <color=#B28CFF>Thinking…</color>"
                        : "<color=#B28CFF>✦ Thought</color>" + (s.Seconds >= 1 ? " " + ThreadPulse.Dur((int)Math.Round(s.Seconds)) : "") + (headline.Length > 0 ? "  " + Dim + "<i>" + E(headline) + "</i>" + Endc : "");
                    b.Body = s.Text;
                    b.Collapsible = s.Text.Trim().Length > 0;
                    b.Tint = UiTheme.Current.Violet;
                    if (s.Live) b.Preview = ThreadPulse.Clip(ThreadPulse.LastSentence(s.Text), 130);
                    break;
                }
                case StepKind.Command:
                {
                    b.Head = "<color=#FFB547>$</color> " + E(ThreadPulse.Clip(s.Title, 220));
                    b.Right = StepRight(s);
                    string outp = (s.Text ?? "").Trim();
                    b.Collapsible = outp.Length > 0 && outp != "(no output)";
                    b.Body = s.Text;
                    b.MonoBody = true;
                    b.BodyMaxChars = 2400;
                    if (s.Failed && b.Collapsible) b.Preview = ThreadPulse.Clip(LastLine(outp), 120);
                    break;
                }
                case StepKind.Edit:
                    b.Head = "<color=#FFB547>✎</color> " + E(SbRel(s.Title)) + (s.Added + s.Removed > 0 ? $"  <color=#4DFFA6>+{s.Added}</color> <color=#FF5468>−{s.Removed}</color>" : "")
                             + (s.Sub == "add" ? "  " + Dim + "new" + Endc : s.Sub == "delete" ? "  <color=#FF5468>deleted</color>" : s.Sub == "move" ? "  " + Dim + "moved" + Endc : "");
                    b.Right = s.Live ? StepRight(s) : "";
                    if (!string.IsNullOrEmpty(s.Text)) { b.Body = s.Text; b.MonoBody = true; b.Collapsible = true; b.BodyMaxChars = 3000; }
                    break;
                case StepKind.Read: b.Head = "<color=#7FA9BF>⌕</color> Read " + E(ThreadPulse.Clip(SbRel(s.Title), 160)); b.Right = s.Live ? StepRight(s) : ""; break;
                case StepKind.Search: b.Head = "<color=#7FA9BF>⌕</color> Searched " + E(ThreadPulse.Clip(s.Title, 160)); b.Right = s.Live ? StepRight(s) : ""; break;
                case StepKind.Web: b.Head = "<color=#8FEBFF>↗</color> " + E(ThreadPulse.Clip(s.Title, 160)); b.Right = StepRight(s); break;
                case StepKind.Plan:
                    b.Head = "<color=#8FEBFF>☰</color> " + E(s.Title);
                    b.Body = s.Text; b.Collapsible = s.Text.Length > 0; b.Expanded = s.Live;
                    break;
                case StepKind.Agent:
                    b.Head = "<color=#B28CFF>◆</color> " + E(ThreadPulse.Clip(s.Title, 160));
                    b.Right = StepRight(s);
                    b.Body = s.Text; b.Collapsible = s.Text.Trim().Length > 0; b.BodyMaxChars = 2400;
                    break;
                case StepKind.Approval:
                    b.Head = "<color=#FFB547>⚠ Asks your approval:</color> " + E(ThreadPulse.Clip(s.Title, 140)) + (s.Live ? "" : "  " + Dim + E(s.Sub) + Endc);
                    b.Tint = UiTheme.Current.Warn;
                    break;
                case StepKind.Question:
                    b.Head = "<color=#FFB547>? Asks you:</color> " + E(ThreadPulse.Clip(s.Title, 160));
                    b.Tint = UiTheme.Current.Warn;
                    break;
                case StepKind.Update:
                    b.Kind = ChatKind.Update;
                    b.Body = StripViewBlocks(s.Text);
                    b.Collapsible = true; // long updates show 6 lines; a tap shows all
                    break;
                case StepKind.Error:
                    b.Kind = ChatKind.Error;
                    b.Head = "⚠ " + E(ThreadPulse.Clip(s.Title, 200));
                    b.Body = s.Text;
                    b.Collapsible = s.Text.Trim().Length > 0;
                    b.Expanded = true;
                    break;
                case StepKind.Notice:
                    b.Kind = ChatKind.Notice;
                    b.Head = "· " + E(ThreadPulse.Clip(s.Title, 200));
                    break;
                default: // tools
                    b.Head = "<color=#B28CFF>⚙</color> " + E(ThreadPulse.Clip(s.Title, 60)) + (s.Sub.Length > 0 ? "  " + Dim + E(ThreadPulse.Clip(s.Sub, 120)) + Endc : "");
                    b.Right = StepRight(s);
                    b.Body = s.Text; b.MonoBody = true; b.Collapsible = s.Text.Trim().Length > 0; b.BodyMaxChars = 2000;
                    break;
            }
            return b;
        }

        private ChatBlock ReadsBlock(List<ThreadStep> steps, string parent, string exKey)
        {
            int reads = steps.Count(s => s.Kind == StepKind.Read), searches = steps.Count - reads;
            var names = steps.Where(s => s.Kind == StepKind.Read).Select(s => ThreadPulse.FileName(s.Title)).Where(n => n.Length > 0).Distinct().Take(4).ToList();
            string what = (reads > 0 ? "Read " + ThreadPulse.Plural(reads, "file") : "") + (searches > 0 ? (reads > 0 ? ", " : "") + (searches == 1 ? "1 search" : searches + " searches") : "");
            return new ChatBlock
            {
                Key = steps[0].Id + ":reads", Kind = ChatKind.Step, Parent = parent, Indent = 1, Exchange = exKey, Collapsible = true,
                Head = "<color=#7FA9BF>⌕</color> " + what + (names.Count > 0 ? "  " + Dim + UiChatView.Esc(string.Join(", ", names) + (reads > names.Count ? ", …" : "")) + Endc : ""),
                Body = string.Join("\n", steps.Select(s => (s.Kind == StepKind.Read ? "• " + SbRel(s.Title) : "• search " + s.Title))),
            };
        }

        private static string LastLine(string s)
        {
            var lines = (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count > 0 ? lines[lines.Count - 1] : "";
        }

        // ------------------------------------------------------------------ the status line

        private void ShowThreadStatus()
        {
            if (_thStatus == null) return;
            var t = UiTheme.Current;
            var p = _thPulse;
            if (p == null || _threadOpen == null) { _thStatus.Set(""); return; }
            string spin = StatusSpinner[(int)(Time.unscaledTime * 4f) % 4];
            string E(string x) => UiChatView.Esc(x);
            string text;
            Color col;
            switch (p.State)
            {
                case "working":
                    if (p.MayBeStuck)
                    {
                        col = t.Warn;
                        text = $"⚠ No news for {ThreadPulse.Clock(p.QuietSecs)} while {E(p.Doing)}: it may be stuck. ■ STOP ends the turn.";
                        break;
                    }
                    col = t.TextAccent;
                    text = $"{spin} {E(Cap(p.Doing))}" + (p.PhaseSecs > 0 && p.Phase != "between" ? $" · {ThreadPulse.Clock(p.PhaseSecs)}" : "")
                           + $"   {Dim}turn {ThreadPulse.Clock(p.TurnSecs)}" + (p.QuietSecs >= 15 ? $" · quiet {ThreadPulse.Clock(p.QuietSecs)}" : " · live") + Endc;
                    break;
                case "waiting": col = t.Warn; text = "⚠ " + E(Cap(p.Doing)); break;
                case "held": col = t.Warn; text = "⏸ " + E(Cap(p.Doing)); break; // MQH's workspace guard (ShellWorkspaces): SEND NOW beside it
                case "error": col = t.Danger; text = "⚠ " + E(p.ErrorText.Length > 0 ? p.ErrorText : "the turn failed"); break;
                case "stopped": col = t.TextDim; text = "■ Stopped before it finished"; break;
                default:
                {
                    col = t.TextDim;
                    var ex = _tlModel?.Exchanges.LastOrDefault(x => x.Prompt != null);
                    text = p.AnsweredAt > 0 ? "<color=#4DFFA6>✓</color> Answered " + ClockOf(p.AnsweredAt) + (ex != null && ex.Seconds >= 1 ? " · took " + ThreadPulse.Clock((int)ex.Seconds) : "") : "Idle";
                    break;
                }
            }
            if (p.Queued.Length > 0) text += $"   {Dim}queued: {E(p.Queued)}{Endc}";
            _thStatus.Set(text, col);
        }

        // ------------------------------------------------------------------ usage limits and the context ring

        private void RefreshUsageLimits(bool force)
        {
            string provider = (string)_threadInfo?["providerId"];
            if (provider == null || Mqh == null || !Application.isPlaying) { ShowLimits(); return; } // self-tests: SetUsageForTest
            if (!force && _usageAskedAt.TryGetValue(provider, out var at) && Time.unscaledTime - at < 60f) return;
            _usageAskedAt[provider] = Time.unscaledTime;
            EnsureHost(() => Mqh.UsageLimits(provider, _hostId, r =>
            {
                if (r?[provider] is JObject u) _usage[provider] = u;
                ShowLimits();
            }));
        }

        public void SetUsageForTest(string provider, JObject usage) { _usage[provider] = usage; ShowLimits(); }

        private void ShowLimits()
        {
            if (_thLimits == null) return;
            string provider = (string)_threadInfo?["providerId"];
            _thLimits.Set(provider != null && _usage.TryGetValue(provider, out var u) ? LimitsText(u, false) : "");
        }

        /// "OpenCode Go · 5h 0% · week 0% · month 40%" (full: with the reset times). Never the account's e-mail.
        public static string LimitsText(JObject u, bool full)
        {
            string st = (string)u?["status"];
            if (st == "unauthenticated" || st == "expired") return "<color=#FFB547>not signed in</color>";
            if (st == "error") return full ? "usage: " + UiChatView.Esc((string)u["message"] ?? "not available") : "";
            if (st != "ok") return "";
            var parts = new List<string>();
            string plan = (string)u["planLabel"];
            if (!string.IsNullOrEmpty(plan)) parts.Add(UiChatView.Esc(plan));
            foreach (var w in u["windows"] as JArray ?? new JArray())
            {
                float pct = w["usedPercent"]?.Type == JTokenType.Integer || w["usedPercent"]?.Type == JTokenType.Float ? (float)w["usedPercent"] : 0f;
                string col = pct >= 90f ? "#FF5468" : pct >= 70f ? "#FFB547" : "#E2F4FF";
                string label = ShortWindow((string)w["label"]);
                string reset = "";
                if (full && DateTimeOffset.TryParse((string)w["resetsAt"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ra))
                {
                    var local = ra.ToLocalTime();
                    reset = " (resets " + (local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("ddd d MMM", CultureInfo.InvariantCulture)) + ")";
                }
                parts.Add($"{UiChatView.Esc(label)} <color={col}>{pct:0}%</color>{reset}");
            }
            return string.Join(full ? "\n" : " · ", parts);
        }

        private static string ShortWindow(string label)
        {
            string l = (label ?? "").Trim().ToLowerInvariant();
            if (l.StartsWith("5 hour") || l == "5h" || l.StartsWith("five")) return "5h";
            if (l.StartsWith("week")) return "week";
            if (l.StartsWith("month")) return "month";
            if (l.StartsWith("day") || l.StartsWith("daily")) return "day";
            return label ?? "";
        }

        // ------------------------------------------------------------------ dictation into a text field (◉)

        private Action<string> _dictateTo;
        private UiTextField _dictField;
        private (float silence, float max, bool auto, float noSpeech) _recBefore;
        public bool Dictating => _dictateTo != null;

        /// ◉: speech to text into the field (the composer, also a draft's first message); ◉ again (or a pause of
        /// ~2 s) ends it. The text is only typed in: SEND stays the user's.
        public void DictateInto(UiTextField field)
        {
            if (field == null || Recorder == null) return;
            if (Dictating) { StopDictation(); return; }
            if (SetupBlocks(5, "Dictation")) return; // the first start's setup (ShellSetupLocks)
            if (Recorder.Recording) { Toast("The microphone is busy with a voice command: try again in a moment", "warn"); return; }
            _dictField = field;
            StartDictation(text =>
            {
                string cur = (field.Text ?? "").TrimEnd();
                field.Set((cur.Length > 0 ? cur + " " : "") + text);
                field.OnChanged?.Invoke(field.Text);
            });
        }

        private void StartDictation(Action<string> onText)
        {
            EnsureVoiceService();
            if (!VoiceRecorder.HasPermission)
            {
                VoiceRecorder.RequestPermission(ok => _main.Enqueue(() =>
                {
                    if (ok) StartDictation(onText);
                    else Toast("Microphone permission denied (Horizon settings > Apps > MetaCodex)", "error");
                }));
                return;
            }
            _recBefore = (Recorder.SilenceSeconds, Recorder.MaxSeconds, Recorder.AutoStop, Recorder.NoSpeechSeconds);
            Recorder.SilenceSeconds = 2.0f;  // a message has pauses: a command ends at 0.7 s
            Recorder.MaxSeconds = 30f;
            Recorder.NoSpeechSeconds = 8f;
            Recorder.AutoStop = true;
            if (!Recorder.Start()) { RestoreRecorder(); _dictField = null; Toast("Microphone: " + Recorder.LastError, "error"); return; }
            _dictateTo = onText;
            SetVoiceState("listening", "● DICTATING", "say your message · ◉ again to stop");
            Sounds?.Play("open");
        }

        private void StopDictation()
        {
            if (!Dictating) return;
            DictationWav(Recorder.Recording ? Recorder.Stop() : null);
        }

        private void RestoreRecorder()
        {
            if (Recorder == null || _recBefore.max <= 0f) return;
            Recorder.SilenceSeconds = _recBefore.silence;
            Recorder.MaxSeconds = _recBefore.max;
            Recorder.AutoStop = _recBefore.auto;
            Recorder.NoSpeechSeconds = _recBefore.noSpeech;
            _recBefore = default;
        }

        /// The recording of a dictation ended (◉, TALK, a pause, the time limit): speech to text, then into the field.
        private void DictationWav(byte[] wav)
        {
            var deliver = _dictateTo;
            _dictateTo = null;
            RestoreRecorder();
            void Fail(string why) { _dictField = null; SetVoiceState("error", "⚠ " + why, ""); _vHudCloseAt = Time.unscaledTime + 3f; VoiceState = "idle"; Sounds?.Play("error"); }
            if (wav == null) { Fail("nothing heard"); return; }
            if (Qvac == null || !Qvac.IsRunning) { EnsureVoiceService(); Fail("the AI service is starting: try again"); return; }
            SetVoiceState("transcribing", "◌ TRANSCRIBING", "");
            Qvac.Client.Transcribe(wav, null, (r, e) =>
            {
                string text = e != null ? null : ((string)r?["text"] ?? "").Trim();
                if (string.IsNullOrEmpty(text)) { Fail(e != null ? "speech-to-text: " + e : "nothing heard"); return; }
                deliver?.Invoke(text);
                _dictField = null;
                SetVoiceState("done", "✓ TYPED IN", "“" + Short(text, 90) + "” · SEND when ready");
                _vHudCloseAt = Time.unscaledTime + 2.5f;
                VoiceState = "idle";
                EndBoostSoon();
                Sounds?.Play("toast");
            });
        }

        /// Self-tests: what the speech-to-text would give, through the same path.
        public void DictateTextForTest(UiTextField field, string text)
        {
            _dictField = field;
            _dictateTo = s => { string cur = (field.Text ?? "").TrimEnd(); field.Set((cur.Length > 0 ? cur + " " : "") + s); };
            var deliver = _dictateTo;
            _dictateTo = null;
            deliver(text);
            _dictField = null;
        }

        public UiTextField ComposerForTest => _thComposer;
        public UiButton MicForTest => _thMic;
    }
}
