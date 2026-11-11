using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Core Session Context Protocol (docs/session-context.md): the app keeps a live picture of the workbench (what the
    /// user points at, threads, pending approvals, Linux processes, tools, scripts and plugins) and a mid-term event log,
    /// so a spoken or typed QUESTION ("is this build stuck?", "is this command safe?") is answered from it by the local
    /// model. Read-only; for the local model only: nothing of it goes to the ACP agents, to MQH or to the control port.
    public sealed partial class MetaCodexShell
    {
        public SessionMemory SessionMemory { get; } = new SessionMemory();
        public ProcessVitals Vitals { get; } = new ProcessVitals();
        private string SessionLogPath => Path.Combine(Application.persistentDataPath, "session-context.jsonl");
        private UiPanel _ctxPointed;
        private float _ctxPointedAt = -99f;
        private float _ctxNextSave, _ctxNextSample;
        private string _voiceUtterance = "";
        private readonly Dictionary<string, string> _ctxThreadStatus = new Dictionary<string, string>();
        private readonly HashSet<string> _ctxPending = new HashSet<string>();
        private readonly HashSet<string> _ctxToolsSeen = new HashSet<string>();
        private readonly HashSet<int> _ctxProcsSeen = new HashSet<int>();
        // each thread's pulse (ThreadPulse: what it does now, since when, the pattern); the open thread's comes from
        // its panel's polls, the other working threads' from a small timeline read every 15 s
        private readonly Dictionary<string, ThreadPulse> _pulses = new Dictionary<string, ThreadPulse>();
        private JArray _ctxThreadRows;
        private float _ctxNextPulses;
        public IReadOnlyDictionary<string, ThreadPulse> Pulses => _pulses;

        private void InitSessionContext()
        {
            SessionMemory.Load(SessionLogPath, DateTime.Now);
            SessionMemory.Add("system", "app", "MetaCodex started");
            Scripts.Changed += () =>
            {
                foreach (var e in Scripts.Entries.Where(x => x.Error != null))
                    SessionMemory.Add("extension", e.Kind + "/" + e.Name, $"{e.Kind} {e.Name}: error {Cut(e.Error, 120)}");
            };
            Clipboard.Changed += () => { var c = Clipboard.Items.FirstOrDefault(); if (c != null) SessionMemory.Add("capture", c.file, $"captured {c.w}x{c.h} from {c.panel}"); };
        }

        /// Every frame, cheap: which panel a hand ray is on (the window "this" means), the ps sampler, saving the log.
        private void TickSessionContext()
        {
            for (int h = 0; h < 2; h++)
            {
                var p = Router?.HandRayPanel(h);
                if (p != null && p.Id != "dock") { _ctxPointed = p; _ctxPointedAt = Time.unscaledTime; }
            }
            if (Time.unscaledTime >= _ctxNextSample) { _ctxNextSample = Time.unscaledTime + 20f; SampleProcesses(); }
            TickReturn(); // back from an app MetaCodex launched (ShellReturn)
            if (Time.unscaledTime >= _ctxNextPulses)
            {
                // every 5 s while another thread works (its "answered" callout comes soon), else every 15 s
                bool otherWorking = _pulses.Any(kv => (kv.Key != _threadOpen || !IsPanelOpen("thread")) && kv.Value.Working);
                _ctxNextPulses = Time.unscaledTime + (otherWorking ? 5f : 15f);
                CtxPollPulses();
            }
            if (Time.unscaledTime >= _ctxNextSave) { _ctxNextSave = Time.unscaledTime + 30f; if (SessionMemory.Dirty) SessionMemory.Save(SessionLogPath); }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && SessionMemory.Dirty) SessionMemory.Save(SessionLogPath);
            if (!paused) { _returnCheckAt = 0f; ResyncThread(); } // a return at once; the threads as MQH has them now
        }
        private void OnApplicationQuit() { if (SessionMemory.Dirty) SessionMemory.Save(SessionLogPath); }

        /// One `ps` in the Linux at low priority (only while it is up; never in edit-mode tests).
        private void SampleProcesses()
        {
            if (!Application.isPlaying || Git == null || !Git.InUserland) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await Git.Shell("renice -n 10 $$ >/dev/null 2>&1; ps -eo pid=,ppid=,etimes=,times=,stat=,comm=,args= --sort=-times 2>/dev/null | head -80", 15000);
                _main.Enqueue(() => OnProcessSample(r.output ?? "", DateTime.Now));
            });
        }

        /// A ps sample: vitals, and the notable processes that started or ended into the log.
        public void OnProcessSample(string ps, DateTime now)
        {
            var before = Vitals.Notable.ToDictionary(p => p.Pid, p => p);
            Vitals.Update(ps, now);
            var after = Vitals.Notable.ToList();
            foreach (var p in after)
                if (!_ctxProcsSeen.Contains(p.Pid) && p.Elapsed >= 20) { _ctxProcsSeen.Add(p.Pid); SessionMemory.Add("process", p.Pid.ToString(), "running: " + Cut(p.Args, 80), now); }
            foreach (var kv in before)
                if (!after.Any(p => p.Pid == kv.Key) && _ctxProcsSeen.Remove(kv.Key))
                    SessionMemory.Add("process", kv.Key.ToString(), $"ended after {ProcessVitals.Duration(kv.Value.Elapsed)}: {Cut(kv.Value.Args, 80)}", now);
        }

        // ------------------------------------------------------------------ thread pulses

        /// A thread's new pulse: what changed (started, a failure, fixing, quiet, answered, an error) goes into the
        /// mid-term memory; the pulse stays for questions.
        private void CtxPulse(string threadId, ThreadPulse p)
        {
            if (threadId == null || p == null) return;
            _pulses.TryGetValue(threadId, out var before);
            foreach (var e in ThreadPulse.Changes(before, p)) SessionMemory.Add("thread", threadId, e);
            _pulses[threadId] = p;
            NoteThreadDone(threadId, before, p, threadId == _threadOpen && IsPanelOpen("thread")); // the orb's cards: DONE · NEW
            NotifyThreadChange(threadId, before, p); // the orb: answered / error / needs you (ShellThreadView)
        }

        /// The other threads that work (or worked at the last look): their last two exchanges, parsed into a pulse.
        /// Small reads on loopback, only while MQH is up and the app runs.
        private void CtxPollPulses()
        {
            if (!Application.isPlaying || Mqh == null || !Mqh.Online) return;
            Mqh.Threads(null, th =>
            {
                if (!(th is JArray rows)) return;
                _ctxThreadRows = rows;
                foreach (var t in rows.OfType<JObject>())
                {
                    string id = (string)t["id"];
                    if (id == null || id == _threadOpen && IsPanelOpen("thread")) continue; // the open window polls its own
                    bool active = ThreadTimeline.IsWorking(ThreadStatus(t)) || t["hasPendingInteraction"]?.Value<bool>() == true;
                    bool wasActive = _pulses.TryGetValue(id, out var pp) && pp.Working;
                    if (!active && !wasActive) continue;
                    var row = t;
                    // parsed on the pool thread; only the pulse comes back to the main thread
                    Mqh.CallThen(System.Net.Http.HttpMethod.Get, MqhClient.TimelinePath(id, null, 2), null,
                        r => r?["rows"] is JArray tlRows ? ThreadPulse.From(ThreadTimeline.Parse(tlRows, r, ThreadStatus(row)), row, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : null,
                        p => { if (p != null) CtxPulse(id, p); });
                }
            });
        }

        /// The pulse of a thread now: the open one fresh from its timeline, the others from the last background read.
        private ThreadPulse PulseOf(string threadId)
        {
            if (threadId == null) return null;
            if (threadId == _threadOpen && IsPanelOpen("thread") && _tlModel != null && _tlModel.Exchanges.Count > 0) return ThreadPulse.From(_tlModel, _threadInfo, NowMs);
            return _pulses.TryGetValue(threadId, out var p) ? p : null;
        }

        /// The process behind the command the agent runs now (the ps sampler's view: is its CPU still moving?).
        private ProcessVitals.Proc ProcessOf(ThreadPulse p)
        {
            if (p?.CurrentCommand == null) return null;
            string key = ThreadPulse.CommandKey(p.CurrentCommand).Split(' ')[0];
            if (key.Length < 2) return null;
            return Vitals.Notable.FirstOrDefault(x => x.Comm == key || (x.Args ?? "").Contains(p.CurrentCommand.Length > 40 ? p.CurrentCommand.Substring(0, 40) : p.CurrentCommand))
                   ?? Vitals.Notable.FirstOrDefault(x => (x.Args ?? "").Split(' ').FirstOrDefault()?.EndsWith(key) == true);
        }

        // ------------------------------------------------------------------ taps (called where the app already learns things)

        private void CtxThreadsUpdated()
        {
            foreach (var t in _threads.OfType<JObject>())
            {
                string id = (string)t["id"], st = ThreadStatus(t), title = (string)t["title"] ?? id;
                if (id == null) continue;
                if (_ctxThreadStatus.TryGetValue(id, out var old) && old != st) SessionMemory.Add("thread", id, $"thread “{Cut(title, 50)}”: {old} -> {st}");
                else if (old == null && _ctxThreadStatus.Count > 0) SessionMemory.Add("thread", id, $"thread “{Cut(title, 50)}” appeared ({st})");
                _ctxThreadStatus[id] = st;
            }
        }

        private void CtxInteractionsUpdated(string threadId)
        {
            var title = ThreadTitle(threadId);
            var now = new HashSet<string>();
            foreach (var ia in _interactions.OfType<JObject>().Where(IsPending))
            {
                string key = threadId + "/" + (string)ia["id"];
                now.Add(key);
                if (_ctxPending.Add(key)) SessionMemory.Add(IaKind(ia) == "approval" ? "approval" : "question", threadId, $"asked in “{Cut(title, 40)}”: {Cut(IaWhat(ia), 100)}");
            }
            foreach (var gone in _ctxPending.Where(k => k.StartsWith(threadId + "/") && !now.Contains(k)).ToList())
            {
                _ctxPending.Remove(gone);
                SessionMemory.Add("approval", threadId, $"answered in “{Cut(title, 40)}”");
            }
        }

        private bool _ctxToolsScanned;

        private void CtxToolsUpdated()
        {
            var found = _tools.Where(x => x.Found).ToList();
            if (!_ctxToolsScanned)
            {
                _ctxToolsScanned = true;
                foreach (var t in found) _ctxToolsSeen.Add(t.Name);
                SessionMemory.Add("tools", "scan", $"{found.Count} tools installed");
                return;
            }
            foreach (var t in found) if (_ctxToolsSeen.Add(t.Name)) SessionMemory.Add("tools", t.Name, $"installed {t.Name} {t.Version}");
        }

        // tests: the state the taps would have seen
        public void SetInteractionsForTest(JArray ia) { _interactions = ia ?? new JArray(); CtxInteractionsUpdated(_threadOpen); }
        public void SetToolsForTest(IEnumerable<(string name, bool found, string version)> tools)
        {
            _tools.Clear();
            foreach (var t in tools) _tools.Add(new ToolInfo { Name = t.name, Found = t.found, Version = t.version });
            CtxToolsUpdated();
        }

        private static bool IsPending(JObject ia) => (string)ia["status"] == "pending" || ia["status"] == null;
        private static string IaKind(JObject ia) => (string)ia["payload"]?["kind"] ?? "question";
        private static string IaWhat(JObject ia)
        {
            var subj = ia["payload"]?["subject"];
            return (string)subj?["command"] ?? (string)subj?["toolName"] ?? (string)subj?["path"] ?? (string)ia["payload"]?["prompt"] ?? (string)ia["payload"]?["question"] ?? (string)subj?["kind"] ?? "something";
        }
        private static string ThreadStatus(JObject t) => (string)t["runtime"]?["displayStatus"] ?? (string)t["status"] ?? "?";
        private string ThreadTitle(string id) => (string)(_threads.FirstOrDefault(x => (string)x["id"] == id) as JObject)?["title"] ?? (string)_threadInfo?["title"] ?? id;

        // ------------------------------------------------------------------ the snapshot

        /// The window "this" means: the panel a hand ray was on in the last 15 s, else the open panel nearest the gaze.
        public UiPanel FocusedPanel()
        {
            if (_ctxPointed != null && _ctxPointed.IsOpen && Time.unscaledTime - _ctxPointedAt < 15f) return _ctxPointed;
            if (Head == null) return null;
            UiPanel best = null;
            float bestAngle = 25f;
            foreach (var p in _panels.Values)
            {
                if (p == null || !p.IsOpen || p.Id == "dock") continue;
                float a = Vector3.Angle(Head.forward, p.transform.position - Head.position);
                if (a < bestAngle) { bestAngle = a; best = p; }
            }
            return best;
        }

        public void SetPointedForTest(string panelId) { _panels.TryGetValue(panelId, out _ctxPointed); _ctxPointedAt = Time.unscaledTime; }

        /// The thread a question is about: the one in the window pointed at, else the open one, else the latest active.
        private string SubjectThread(UiPanel focus)
        {
            if (focus != null && (focus.Id == "thread" || focus.Id == SidebarId) && _threadOpen != null) return _threadOpen;
            if (focus != null && focus.Id != null && focus.Id.StartsWith("orb-thread-") && OrbCardThread(focus) != null) return OrbCardThread(focus); // the orb's card
            if (focus != null && focus.Id == "threads" && _threadSel != null) return _threadSel;
            if (_threadOpen != null) return _threadOpen;
            return (string)_threads.OfType<JObject>().OrderByDescending(t => t["updatedAt"]?.Value<long>() ?? 0).FirstOrDefault()?["id"];
        }

        public JObject BuildSessionSnapshot(SessionQuestion q)
        {
            var now = DateTime.Now;
            var sec = new JObject();
            var data = new JObject();
            void Line(string section, string text) { if (!(sec[section] is JArray a)) sec[section] = a = new JArray(); a.Add(text); }

            // focus
            var focus = FocusedPanel();
            string subject = SubjectThread(focus);
            if (focus != null) Line("focus", (Time.unscaledTime - _ctxPointedAt < 15f && focus == _ctxPointed ? "pointing at " : "looking at ") + focus.Title + " panel");
            if (subject != null) Line("focus", $"the thread in question: “{ThreadTitle(subject)}”");
            data["subjectThread"] = subject;

            // the subject thread: what it is, what was asked, what it did
            if (subject != null)
            {
                var row = _threads.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == subject) ?? (subject == _threadOpen ? _threadInfo : null);
                if (row != null) Line("thread", $"“{ThreadTitle(subject)}” · agent {(string)row["providerId"]} · status {ThreadStatus(row)}" + ((string)row["environmentPath"] != null ? " · in " + (string)row["environmentPath"] : ""));
                var pulse = PulseOf(subject);
                // which folder it works in and who shares it (docs/workspaces.md)
                var where = WorkspaceMap.Describe(WorkspaceMap.GroupOf(WorkspaceMap.Build(_ctxThreadRows != null && _ctxThreadRows.Count >= _threads.Count ? _ctxThreadRows : _threads, _environments), subject), subject);
                if (where.Length > 0) Line("thread", "workspace: " + where);
                if (pulse != null)
                {
                    // the compact state (owner 2026-11-11: "running a task since 3 min", "re-running the test after
                    // edits": the local model reads this, not the conversation)
                    foreach (var l in pulse.Lines()) Line("thread", l);
                    var proc = pulse.Phase == "running" ? ProcessOf(pulse) : null;
                    string cpu = proc != null ? ProcessVitals.Verdict(proc, now) : null;
                    if (cpu != null) Line("thread", $"its process {proc.Comm} (pid {proc.Pid}): {cpu}");
                    data["pulse"] = new JObject
                    {
                        ["speak"] = pulse.Speak() + (cpu != null ? $" Its process {proc.Comm} is {cpu}." : ""),
                        ["state"] = pulse.State, ["phase"] = pulse.Phase, ["stuck"] = pulse.MayBeStuck, ["pattern"] = pulse.Pattern,
                    };
                }
                else if (subject == _threadOpen && _tlRaw != null) foreach (var l in TimelineDigest(_tlRaw)) Line("thread", l);
            }
            // approvals and questions waiting (the open thread's are known)
            if (_threadOpen != null)
                foreach (var ia in _interactions.OfType<JObject>().Where(IsPending))
                {
                    string kind = IaKind(ia), what = IaWhat(ia);
                    var risks = kind == "approval" ? CommandRisk.Flags(what) : new List<string>();
                    Line("approval", $"{(kind == "approval" ? "approval asked" : "question asked")} in “{ThreadTitle(_threadOpen)}”: {Cut(what, 160)}" +
                                     ((string)ia["payload"]?["reason"] != null ? " · reason: " + Cut((string)ia["payload"]["reason"], 100) : "") +
                                     (risks.Count > 0 ? " · scan: it " + string.Join(", ", risks) : " · scan: nothing risky found"));
                    if (data["approval"] == null)
                        data["approval"] = new JObject { ["thread"] = ThreadTitle(_threadOpen), ["kind"] = kind == "approval" && (string)ia["payload"]?["subject"]?["command"] != null ? "command" : kind, ["what"] = what, ["risks"] = new JArray(risks) };
                }
            // the other threads
            var allThreads = _ctxThreadRows != null && _ctxThreadRows.Count >= _threads.Count ? _ctxThreadRows : _threads;
            foreach (var t in allThreads.OfType<JObject>().OrderByDescending(t => t["updatedAt"]?.Value<long>() ?? 0).Take(6))
            {
                var tp = PulseOf((string)t["id"]);
                Line("threads", $"“{Cut((string)t["title"] ?? (string)t["titleFallback"] ?? (string)t["id"], 50)}” ({(string)t["providerId"]}): " + (tp != null ? tp.OneLine() : ThreadStatus(t)));
            }
            // processes
            var procs = new JArray();
            foreach (var p in Vitals.Notable.Take(5))
            {
                string text = ProcessVitals.Describe(p, now);
                Line("processes", text);
                procs.Add(new JObject { ["pid"] = p.Pid, ["text"] = text, ["kind"] = ProcessVitals.KindOf(p), ["cmd"] = Cut(p.Args, 70), ["elapsed"] = ProcessVitals.Duration(p.Elapsed), ["verdict"] = ProcessVitals.Verdict(p, now) });
            }
            if (Vitals.LastSample == default) Line("processes", "not sampled yet");
            else if (procs.Count == 0) Line("processes", "no build, install or agent process running in the Linux");
            data["processes"] = procs;
            // tools
            var tools = new JArray(_tools.Select(t => new JObject { ["name"] = t.Name, ["installed"] = t.Found, ["version"] = t.Version ?? "" }));
            data["tools"] = tools;
            if (_tools.Count > 0)
            {
                var wanted = q.Tools.Count > 0 ? _tools.Where(t => q.Tools.Contains(t.Name)).ToList() : new List<ToolInfo>();
                foreach (var t in wanted) Line("tools", t.Found ? $"{t.Name} {t.Version} installed" : $"{t.Name} not installed");
                Line("tools", "installed: " + string.Join(", ", _tools.Where(t => t.Found).Take(24).Select(t => t.Name + (string.IsNullOrEmpty(t.Version) ? "" : " " + t.Version))));
                var missing = _tools.Where(t => !t.Found).Select(t => t.Name).ToList();
                if (missing.Count > 0) Line("tools", "not installed: " + string.Join(", ", missing.Take(20)));
            }
            // scripts and plugins
            if (Scripts != null)
            {
                foreach (var e in Scripts.Entries.Where(e => e.Error != null)) Line("extensions", $"{e.Kind} {e.Name}: ERROR {Cut(e.Error, 100)}");
                int on = Scripts.Entries.Count(e => e.Enabled && e.Error == null), off = Scripts.Entries.Count(e => !e.Enabled);
                Line("extensions", $"{on} scripts and plugins running, {off} turned off");
            }
            foreach (var m in _mqhPluginCache.OfType<JObject>().Where(m => (string)m["status"] != "running" && (string)m["status"] != "disabled"))
                Line("extensions", $"MQH plugin {(string)m["id"]}: {(string)m["status"]}");
            // system
            Line("system", "first-use setup: " + FirstUseStatus);
            var counts = new JObject();
            if (Clipboard != null)
            {
                var clips = Clipboard.Items;
                var shots = SessionMemory.Events.Where(e => e.Kind == "capture").ToList();
                string newest = clips.Count > 0 && DateTime.TryParse(clips[0].time, out var nt) ? nt.ToString("HH:mm") : null;
                counts["captures"] = clips.Count;
                counts["capturesMax"] = Clipboard.Max;
                counts["capturesTaken"] = shots.Count;
                if (newest != null) counts["capturesNewest"] = newest;
                if (shots.Count > 0) counts["capturesSince"] = shots[0].At.ToString("HH:mm");
                Line("system", $"{clips.Count} captures in the clipboard (it keeps the newest {Clipboard.Max})" + (newest != null ? ", the newest from " + newest : ""));
            }
            counts["threads"] = _threads.Count;
            counts["threadsRunning"] = _threads.OfType<JObject>().Count(x => ThreadTimeline.IsWorking(ThreadStatus(x)));
            counts["threadsWaiting"] = _threads.OfType<JObject>().Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
            counts["terminals"] = _frames.Values.Count(f => f.IsOpen);
            counts["panels"] = _panels.Values.Count(x => x.IsOpen && x.Id != "dock" && x.Id != "voice-hud") + _frames.Values.Count(f => f.IsOpen);
            if (Scripts != null)
            {
                counts["extensionsOn"] = Scripts.Entries.Count(e => e.Enabled && e.Error == null);
                counts["extensionsOff"] = Scripts.Entries.Count(e => !e.Enabled);
            }
            data["counts"] = counts;
            Line("system", $"{(int)counts["panels"]} windows open, {(int)counts["terminals"]} of them terminals");
            Line("system", "ambient focus " + (Settings.focusEnabled ? "on" : "off") + "; main menu on the " + (Settings.wristMenu ? "wrist" : "dock")
                + "; the orb " + (Settings.orb ? "on" + (Toasts != null && Toasts.History.Count > 0 ? ", its last message: " + Toasts.History[0].Text : "") : "off"));
            Line("system", "local AI service: " + (Qvac == null ? "off" : Qvac.State.ToString().ToLowerInvariant()));
            // the mid-term memory, newest first
            foreach (var e in SessionMemory.Recent(TimeSpan.FromMinutes(90), now).Reverse().Take(14))
                Line("events", e.At.ToString("HH:mm") + " " + e.Text);
            return new JObject { ["now"] = now.ToString("dddd HH:mm"), ["sections"] = sec, ["data"] = data };
        }

        /// The open thread's timeline in a few lines: what was asked, what it ran and changed, its last reply.
        private static List<string> TimelineDigest(JArray rows)
        {
            var lines = new List<string>();
            var flat = new List<JObject>();
            void Walk(JToken r) { if (!(r is JObject o)) return; if ((string)o["kind"] == "turn") { foreach (var c in o["children"] as JArray ?? new JArray()) Walk(c); return; } flat.Add(o); }
            foreach (var r in rows) Walk(r);
            rows = new JArray(flat.Select(o => (JToken)o.DeepClone()));
            var conv = rows.OfType<JObject>().Where(r => (string)r["kind"] == "conversation").ToList();
            var asks = conv.Where(r => (string)r["role"] == "user").Select(r => (string)r["text"]).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var replies = conv.Where(r => (string)r["role"] == "assistant").Select(r => (string)r["text"]).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var work = rows.OfType<JObject>().Where(r => (string)r["kind"] == "work").ToList();
            var cmds = work.Where(w => (string)w["workKind"] == "command").ToList();
            var files = work.Where(w => (string)w["workKind"] == "file-change").ToList();
            int failed = cmds.Count(c => c["exitCode"] != null && c["exitCode"].Type == JTokenType.Integer && c["exitCode"].Value<int>() != 0);
            int added = files.Sum(f => f["change"]?["diffStats"]?["added"]?.Value<int>() ?? 0), removed = files.Sum(f => f["change"]?["diffStats"]?["removed"]?.Value<int>() ?? 0);
            if (asks.Count > 0) lines.Add("first request: " + Cut(asks[0], 140));
            if (asks.Count > 1) lines.Add("latest request: " + Cut(asks[asks.Count - 1], 140));
            lines.Add($"{cmds.Count} commands run{(failed > 0 ? $" ({failed} failed)" : "")}, {files.Count} files changed (+{added} -{removed})");
            if (cmds.Count > 0) lines.Add("last command: " + Cut((string)cmds[cmds.Count - 1]["command"], 100) + " (" + ((string)cmds[cmds.Count - 1]["status"] ?? "?") + ")");
            if (files.Count > 0) lines.Add("files: " + string.Join(", ", files.Select(f => Path.GetFileName((string)f["change"]?["path"] ?? "")).Distinct().Take(6)));
            if (replies.Count > 0) lines.Add("last reply: " + Cut(System.Text.RegularExpressions.Regex.Replace(replies[replies.Count - 1], @"```[\s\S]*?```", "[diagram]"), 220));
            return lines;
        }

        // ------------------------------------------------------------------ answering

        /// A question about the session, answered from the context: exact facts first, else the local model.
        public void AnswerSessionQuestion(string question, Action<string> done)
        {
            var q = SessionQuestion.Parse(question);
            var snap = BuildSessionSnapshot(q);
            string exact = SessionContextText.ExactAnswer(snap, q);
            if (exact != null) { SessionMemory.Add("voice", "ask", "asked: " + Cut(question, 80)); done(exact); return; }
            string context = SessionContextText.Build(snap, q);
            if (Qvac == null || !Qvac.IsRunning) { done("The local AI service is off, so only the facts: " + FirstFacts(snap, q)); return; }
            var body = new JObject { ["messages"] = SessionContextText.Messages(context, question), ["max_tokens"] = 110, ["temperature"] = 0.2 };
            Qvac.Client.Chat(body, (r, e) =>
            {
                string a = e != null ? null : SessionContextText.CleanAnswer((string)r?["choices"]?[0]?["message"]?["content"]);
                SessionMemory.Add("voice", "ask", "asked: " + Cut(question, 80));
                done(string.IsNullOrWhiteSpace(a) ? "I could not put that together. The facts: " + FirstFacts(snap, q) : a);
            });
        }

        /// The first lines of the sections the question is about (when the model cannot answer).
        private static string FirstFacts(JObject snap, SessionQuestion q)
        {
            var sec = snap["sections"] as JObject ?? new JObject();
            var order = q.Topics.Where(t => t != "general").Concat(new[] { "thread", "approval", "processes", "events" }).Distinct();
            var lines = order.SelectMany(s => (sec[s] as JArray)?.Select(x => (string)x).Take(2) ?? Enumerable.Empty<string>()).Take(3).ToList();
            return lines.Count > 0 ? string.Join(". ", lines) + "." : "nothing recorded yet.";
        }

        /// What the context text for a question is (tests, and the VOICE panel later).
        public string SessionContextFor(string question) => SessionContextText.Build(BuildSessionSnapshot(SessionQuestion.Parse(question)), SessionQuestion.Parse(question));

        /// Interpretation, not a command: a question about state (routed to ask_about_session).
        public static bool IsInterpretation(string text) => SessionQuestion.Parse(text).InterpretationScore() >= 0.6f;

        /// A question about state that names a panel or a setting is answered, not run ("how many captures did we take?"
        /// opened CLIPBOARD, owner 2026-11-09; "is the focus on?"): the phrase templates and the decision model match on
        /// the nouns, so their match is set aside when the words ask rather than order and every match is one of the
        /// shell's own actions (a plugin's command, or one that answers, keeps its match).
        public static bool AskInsteadOf(IEnumerable<VoiceMatch> matches, string text) =>
            matches != null && matches.Any() && IsInterpretation(text)
            && matches.All(m => m.Command.Name != "ask_about_session" && m.Command.Owner == "shell" && !m.Command.SpeakReply);

        private void VoiceAskSession(VoiceResult res, Action<VoiceResult> done)
        {
            res.Via = res.Via ?? "context";
            res.Command = "ask_about_session";
            res.Answer = true;
            SetVoiceState("thinking", "◆ LOOKING", "“" + Short(res.Transcript, 70) + "”");
            AnswerSessionQuestion(res.Transcript, answer => { res.Reply = answer; VoiceDone(res, done); });
        }

        private void RegisterSessionVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "ask_about_session",
                Description = "Answer a question about what is going on in the workspace (threads, builds and processes, approvals asked, tools installed, scripts and plugins); not an action",
                Phrases = new[] { "what's going on", "what is going on", "(what is|what's) (this|the) (thread|agent) doing", "what happened while i was away" },
                Samples = new[]
                {
                    "is this build process stuck", "is the build still running", "did the build finish", "how is the install going", "is something running in the background",
                    "what is this thread doing", "what has this thread done so far", "what did the agent achieve", "which threads are running", "is the agent waiting for me",
                    "do we have the rust toolchain", "is node installed", "which python version do we have", "what is this command i'm asked to accept",
                    "is this command safe", "what is the agent asking me", "are my scripts working", "why did it fail", "what happened while i was away",
                    "il build è bloccato", "la compilazione sta andando avanti", "cosa sta facendo questo thread", "cosa ha fatto finora l'agente",
                    "abbiamo la toolchain di rust", "è installato node", "che comando mi chiede di accettare", "questo comando è sicuro",
                    "cosa mi sta chiedendo l'agente", "cosa sta succedendo", "cosa è successo mentre ero via", "ci sono errori negli script",
                },
                Llm = false,
                SpeakReply = true,
                RunAsync = (args, reply) => AnswerSessionQuestion(_voiceUtterance, reply),
            });
        }
    }
}
