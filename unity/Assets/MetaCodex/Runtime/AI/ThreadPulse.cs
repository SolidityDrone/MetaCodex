using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    // A thread's timeline (MQH's /threads/:id/timeline) in the shape people read it: exchanges of a prompt, the
    // agent's steps (thinking, commands, edits, reads, tools, updates) and its answer. The THREAD panel draws it as a
    // chat (ShellThreadView); ThreadPulse boils it down to a few short facts for the session context
    // (docs/session-context.md, docs/thread-view.md). Pure: no Unity objects, tested without the app.

    public enum StepKind { Prompt, Thinking, Command, Edit, Read, Search, Web, Tool, Plan, Agent, Approval, Question, Update, Answer, Notice, Error }

    /// One row of the timeline as a step: what it is, one title line, the long text, its state and times (ms epoch).
    public sealed class ThreadStep
    {
        public StepKind Kind;
        public string Id = "";
        public string Title = "";   // one line: the command, the path, the tool, the heading
        public string Text = "";    // the long part: the message, the reasoning, the output, the detail
        public string Sub = "";     // a little extra: "add" / "update" for edits, the tool's argument
        public string Status = "";  // pending | completed | error | interrupted ("" = none)
        public string Cwd = "";     // a command's working folder (MQH's cwd; "" when not sent)
        public long StartedAt, EndedAt;
        public int? ExitCode;
        public int Added, Removed;
        public bool Live => Status == "pending";
        public bool Failed => Status == "error" || ExitCode.HasValue && ExitCode.Value != 0;
        public bool IsAction => Kind >= StepKind.Command && Kind <= StepKind.Question;
        public long LastAt => Math.Max(StartedAt, EndedAt);
        public double Seconds => StartedAt > 0 && EndedAt >= StartedAt ? (EndedAt - StartedAt) / 1000.0 : 0;
    }

    /// One exchange: the prompt, what the agent did for it (in order) and its answer.
    public sealed class ThreadExchange
    {
        public ThreadStep Prompt;            // null for what came before the first prompt
        public readonly List<ThreadStep> Steps = new List<ThreadStep>();
        public ThreadStep Answer;            // the agent's last message, when nothing came after it
        public string Status = "";           // the turn's: pending | completed | error | interrupted ("" = no turn yet)
        public long StartedAt, EndedAt;
        public bool Live;                    // the agent is working on it now
        public int Thoughts, Commands, Failed, Reads, Searches, Webs, Tools, Agents, Updates, Errors, Added, Removed;
        public double ThinkingSeconds;
        public readonly List<string> Files = new List<string>();
        public string Key => Prompt?.Id ?? (Steps.Count > 0 ? "pre:" + Steps[0].Id : "pre");
        public double Seconds => StartedAt > 0 && EndedAt >= StartedAt ? (EndedAt - StartedAt) / 1000.0 : 0;
        public int ActionCount => Commands + Reads + Searches + Webs + Tools + Agents + Files.Count;
    }

    public sealed class ThreadTimeline
    {
        public readonly List<ThreadExchange> Exchanges = new List<ThreadExchange>();
        public string ActiveThinking = "";   // the reasoning streaming now (MQH's activeThinking)
        public long ThinkingSince;
        public bool Running;                 // the thread works (its status) or its last turn is still open
        public long LastActivity;            // the newest time seen in the rows
        public string TodoNow = "";          // the to-do item in progress
        public int TodoDone, TodoTotal;
        public ThreadExchange Last => Exchanges.Count > 0 ? Exchanges[Exchanges.Count - 1] : null;

        /// "active", "starting", ... count as working (MQH's thread statuses; "running" is the self-tests' word).
        public static bool IsWorking(string status) =>
            status == "active" || status == "running" || status == "starting" || status == "pending" || status == "provisioning" || status == "stopping" || status == "waiting-for-host";

        private static long L(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (long)t : 0;
        private static string S(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        /// A child of an object (null for a JSON null or anything else: MQH sends nulls for absent parts).
        internal static JToken G(JToken t, string key) => t is JObject o ? o[key] : null;

        /// rows: the timeline rows (turns with their children: ask with includeNestedRows=true); fields: the rest of
        /// the response (activeThinking, pendingTodos); threadStatus: the thread's runtime status.
        public static ThreadTimeline Parse(IEnumerable<JToken> rows, JToken fields = null, string threadStatus = null)
        {
            var tl = new ThreadTimeline();
            var flat = new List<(JObject row, string turnId)>();
            var turns = new Dictionary<string, (string status, long start, long end)>();
            void Flatten(JToken r, string turnId)
            {
                if (!(r is JObject o)) return;
                string kind = S(o["kind"]);
                if (kind == "turn")
                {
                    string tid = S(o["turnId"]) ?? S(o["id"]) ?? "";
                    turns[tid] = (S(o["status"]) ?? "", L(o["startedAt"]), L(o["completedAt"])); // a turn seen twice (paged): the last wins
                    flat.Add((o, tid)); // a marker: the turn starts here
                    if (o["children"] is JArray ch) foreach (var c in ch) Flatten(c, tid);
                    else if (L(o["summaryCount"]) > 0) flat.Add((new JObject { ["kind"] = "collapsed", ["id"] = S(o["id"]) + ":collapsed", ["count"] = L(o["summaryCount"]), ["startedAt"] = L(o["startedAt"]), ["completedAt"] = L(o["completedAt"]) }, tid));
                    return;
                }
                flat.Add((o, turnId ?? S(o["turnId"])));
            }
            foreach (var r in rows ?? Enumerable.Empty<JToken>()) Flatten(r, null);

            ThreadExchange cur = null;
            var exTurns = new Dictionary<ThreadExchange, List<string>>();
            void Use(string turnId)
            {
                if (cur == null || string.IsNullOrEmpty(turnId) || !turns.ContainsKey(turnId)) return;
                if (!exTurns.TryGetValue(cur, out var l)) exTurns[cur] = l = new List<string>();
                if (!l.Contains(turnId)) l.Add(turnId);
            }
            foreach (var (row, turnId) in flat)
            {
                string kind = S(row["kind"]);
                tl.LastActivity = Math.Max(tl.LastActivity, Math.Max(L(row["startedAt"]), Math.Max(L(row["createdAt"]), L(row["completedAt"]))));
                if (kind == "turn") { if (cur != null) Use(turnId); continue; }
                if (kind == "conversation" && S(row["role"]) == "user")
                {
                    cur = new ThreadExchange { Prompt = new ThreadStep { Kind = StepKind.Prompt, Id = S(row["id"]) ?? "", Text = S(row["text"]) ?? "", StartedAt = L(row["startedAt"]), EndedAt = L(row["createdAt"]) } };
                    tl.Exchanges.Add(cur);
                    Use(turnId);
                    continue;
                }
                var step = StepOf(row);
                if (step == null) continue;
                if (cur == null) { cur = new ThreadExchange(); tl.Exchanges.Add(cur); }
                cur.Steps.Add(step);
                Use(turnId);
            }

            // the thread's state and the streaming reasoning
            var thinking = G(fields, "activeThinking") as JObject;
            tl.ActiveThinking = S(thinking?["text"]) ?? "";
            tl.ThinkingSince = L(thinking?["startedAt"]);
            tl.LastActivity = Math.Max(tl.LastActivity, L(thinking?["updatedAt"]));
            if (G(G(fields, "pendingTodos"), "items") is JArray todos)
            {
                tl.TodoTotal = todos.Count;
                tl.TodoDone = todos.Count(t => S(G(t, "status")) == "completed");
                tl.TodoNow = S(G(todos.FirstOrDefault(t => S(G(t, "status")) == "in_progress"), "text")) ?? "";
            }
            var last = tl.Last;
            string lastTurn = last != null && exTurns.TryGetValue(last, out var lt) && lt.Count > 0 ? turns[lt[lt.Count - 1]].status : "";
            // the server's thread status wins when it is known: a "pending" last turn left in a cached timeline (the turn
            // ended or was stopped while MetaCodex was paused, and no row changed after) must not keep it "working"
            tl.Running = string.IsNullOrEmpty(threadStatus) ? lastTurn == "pending" : IsWorking(threadStatus);

            foreach (var ex in tl.Exchanges)
            {
                if (exTurns.TryGetValue(ex, out var ids) && ids.Count > 0)
                {
                    var t = turns[ids[ids.Count - 1]];
                    ex.Status = t.status;
                    ex.StartedAt = ids.Select(i => turns[i].start).Where(s => s > 0).DefaultIfEmpty(0).Min();
                    ex.EndedAt = t.end;
                }
                ex.Live = ex == last && tl.Running;
                if (ex.Live && ex.Status != "pending") ex.Status = ex.Status == "" || ex.Status == "completed" ? "pending" : ex.Status;
                if (ex.Prompt != null && ex.Prompt.StartedAt > 0) ex.StartedAt = ex.StartedAt > 0 ? Math.Min(ex.StartedAt, ex.Prompt.StartedAt) : ex.Prompt.StartedAt;
                if (ex.StartedAt == 0) ex.StartedAt = ex.Steps.Select(s => s.StartedAt).Where(s => s > 0).DefaultIfEmpty(0).Min();
                ex.EndedAt = Math.Max(ex.EndedAt, ex.Steps.Select(s => s.LastAt).DefaultIfEmpty(0).Max());

                // the streaming reasoning: into the open thinking step, or a new one at the end
                if (ex.Live && tl.ActiveThinking.Length > 0)
                {
                    var open = ex.Steps.LastOrDefault(s => s.Kind == StepKind.Thinking && s.Live);
                    if (open == null) ex.Steps.Add(open = new ThreadStep { Kind = StepKind.Thinking, Id = "thinking:" + S(thinking?["id"]), Title = "Thinking", Status = "pending", StartedAt = tl.ThinkingSince });
                    if (tl.ActiveThinking.Length >= open.Text.Length) open.Text = tl.ActiveThinking;
                    open.EndedAt = Math.Max(open.EndedAt, L(thinking?["updatedAt"]));
                }
                // the answer: the last message with no work after it; while live it is being written (or an update
                // the agent goes on from)
                int iu = ex.Steps.FindLastIndex(s => s.Kind == StepKind.Update);
                bool workAfter = iu >= 0 && ex.Steps.Skip(iu + 1).Any(s => s.IsAction || s.Kind == StepKind.Thinking);
                if (iu >= 0 && !workAfter)
                {
                    ex.Answer = ex.Steps[iu];
                    ex.Answer.Kind = StepKind.Answer;
                    ex.Steps.RemoveAt(iu);
                    if (ex.Live) ex.Answer.Status = "pending";
                }
                // nothing finished stays "pending" once the turn is over
                if (!ex.Live) foreach (var s in ex.Steps.Where(s => s.Live)) s.Status = ex.Status == "error" ? "error" : "interrupted";

                foreach (var s in ex.Steps)
                {
                    switch (s.Kind)
                    {
                        case StepKind.Thinking: ex.Thoughts++; ex.ThinkingSeconds += s.Seconds; break;
                        case StepKind.Command: ex.Commands++; if (s.Failed) ex.Failed++; break;
                        case StepKind.Edit: if (!ex.Files.Contains(s.Title)) ex.Files.Add(s.Title); ex.Added += s.Added; ex.Removed += s.Removed; break;
                        case StepKind.Read: ex.Reads++; break;
                        case StepKind.Search: ex.Searches++; break;
                        case StepKind.Web: ex.Webs++; break;
                        case StepKind.Tool: case StepKind.Plan: ex.Tools++; if (s.Failed) ex.Failed++; break;
                        case StepKind.Agent: ex.Agents++; break;
                        case StepKind.Update: ex.Updates++; break;
                        case StepKind.Error: ex.Errors++; break;
                    }
                }
            }
            return tl;
        }

        private static readonly Regex Ms = new Regex(@"(\d+(?:\.\d+)?)\s*(ms|s|m)\b");

        /// A timeline row as a step (null for rows that are not shown: debug, parent changes).
        public static ThreadStep StepOf(JObject r)
        {
            string kind = S(r["kind"]);
            var s = new ThreadStep { Id = S(r["id"]) ?? "", StartedAt = L(r["startedAt"]), Status = S(r["status"]) ?? "" };
            long created = L(r["createdAt"]), completed = L(r["completedAt"]);
            s.EndedAt = completed > 0 ? completed : s.Status == "pending" ? 0 : created;
            switch (kind)
            {
                case "collapsed":
                    s.Kind = StepKind.Notice;
                    s.Title = L(r["count"]) + " steps (details not loaded)";
                    s.EndedAt = completed;
                    return s;
                case "conversation":
                    s.Kind = StepKind.Update;
                    s.Text = S(r["text"]) ?? "";
                    s.Status = "";
                    s.EndedAt = Math.Max(created, s.StartedAt);
                    return s;
                case "system":
                {
                    string sk = S(r["systemKind"]), op = S(r["operationKind"]);
                    s.Title = S(r["title"]) ?? "";
                    s.Text = S(r["detail"]) ?? "";
                    if (sk == "debug" || op == "parent-change") return null;
                    if (sk == "error") { s.Kind = StepKind.Error; s.Status = "error"; return s; }
                    if (sk == "operation" && op == "reasoning")
                    {
                        s.Kind = StepKind.Thinking;
                        // "Thought for 792ms": the seconds from the title when the times are not there
                        if (s.Seconds <= 0)
                        {
                            var m = Ms.Match(s.Title);
                            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && s.StartedAt > 0)
                                s.EndedAt = s.StartedAt + (long)(m.Groups[2].Value == "ms" ? v : m.Groups[2].Value == "m" ? v * 60000 : v * 1000);
                        }
                        return s;
                    }
                    s.Kind = s.Status == "error" ? StepKind.Error : StepKind.Notice;
                    return s;
                }
                case "work":
                    return WorkStep(r, s);
            }
            return null;
        }

        private static ThreadStep WorkStep(JObject r, ThreadStep s)
        {
            string wk = S(r["workKind"]);
            switch (wk)
            {
                case "command":
                {
                    s.Kind = StepKind.Command;
                    s.Title = OneLine(S(r["command"]) ?? "");
                    s.Text = S(r["output"]) ?? "";
                    s.Cwd = S(r["cwd"]) ?? "";
                    if (r["exitCode"] != null && (r["exitCode"].Type == JTokenType.Integer || r["exitCode"].Type == JTokenType.Float)) s.ExitCode = (int)r["exitCode"];
                    // a command that only reads or searches (cat, rg, ls) is shown as what it does
                    var intents = (r["activityIntents"] as JArray)?.OfType<JObject>().ToList();
                    if (intents != null && intents.Count > 0 && !s.Failed)
                    {
                        var types = intents.Select(i => S(i["type"])).Distinct().ToList(); // intents are objects (OfType above)
                        if (types.All(t => t == "read" || t == "list_files"))
                        {
                            s.Kind = StepKind.Read;
                            s.Sub = s.Title;
                            s.Title = string.Join(", ", intents.Select(i => S(i["path"]) ?? S(i["name"]) ?? "").Where(x => x.Length > 0).Distinct().Take(3));
                            if (s.Title.Length == 0) s.Title = s.Sub;
                        }
                        else if (types.All(t => t == "search"))
                        {
                            s.Kind = StepKind.Search;
                            s.Sub = s.Title;
                            var q = intents.Select(i => S(i["query"])).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                            s.Title = q != null ? "“" + q + "”" : s.Title;
                        }
                    }
                    return s;
                }
                case "file-change":
                {
                    s.Kind = StepKind.Edit;
                    var ch = G(r, "change");
                    s.Title = S(G(ch, "path")) ?? "file";
                    s.Sub = S(G(ch, "movePath")) != null ? "move" : (S(G(ch, "kind")) ?? "");
                    s.Added = (int)L(G(G(ch, "diffStats"), "added"));
                    s.Removed = (int)L(G(G(ch, "diffStats"), "removed"));
                    s.Text = S(G(ch, "diff")) ?? "";
                    return s;
                }
                case "file-read": s.Kind = StepKind.Read; s.Title = S(r["path"]) ?? ""; return s;
                case "image-view": s.Kind = StepKind.Read; s.Title = S(r["path"]) ?? "image"; s.Sub = "image"; return s;
                case "search":
                    s.Kind = StepKind.Search;
                    s.Title = "“" + (S(r["query"]) ?? "") + "”" + (S(r["path"]) != null ? " in " + S(r["path"]) : "");
                    return s;
                case "web-search":
                    s.Kind = StepKind.Web;
                    s.Title = "search “" + string.Join(" · ", (r["queries"] as JArray ?? new JArray()).Select(q => (string)q)) + "”";
                    return s;
                case "web-fetch": s.Kind = StepKind.Web; s.Title = S(r["url"]) ?? ""; return s;
                case "tool":
                {
                    s.Kind = StepKind.Tool;
                    s.Title = S(r["toolName"]) ?? "tool";
                    var args = r["toolArgs"] as JObject;
                    foreach (var k in new[] { "path", "file_path", "filePath", "pattern", "query", "url", "command", "description" })
                        if (S(G(args, k)) is string v && v.Length > 0) { s.Sub = OneLine(v); break; }
                    s.Text = S(r["output"]) ?? "";
                    return s;
                }
                case "plan-steps":
                {
                    s.Kind = StepKind.Plan;
                    var steps = r["steps"] as JArray ?? new JArray();
                    s.Title = "plan · " + steps.Count + " steps";
                    s.Text = string.Join("\n", steps.Select(x => (S(G(x, "status")) == "completed" ? "✓ " : S(G(x, "status")) == "in_progress" ? "▸ " : "· ") + (S(G(x, "step")) ?? S(G(x, "text")) ?? S(G(x, "title")) ?? "")));
                    return s;
                }
                case "approval":
                {
                    s.Kind = StepKind.Approval;
                    string lc = S(r["lifecycle"]) ?? "";
                    s.Title = S(G(G(r, "target"), "toolName")) ?? (S(r["approvalKind"]) == "file-edit" ? "file edit" : "permission");
                    s.Sub = lc;
                    s.Status = lc == "waiting" || lc == "pending" || lc == "resolving" ? "pending" : lc == "denied" ? "error" : "completed";
                    return s;
                }
                case "question":
                {
                    s.Kind = StepKind.Question;
                    var q = (r["questions"] as JArray)?.FirstOrDefault();
                    s.Title = S(G(q, "question")) ?? S(G(q, "header")) ?? "a question";
                    string lc = S(r["lifecycle"]) ?? "";
                    s.Status = lc == "pending" || lc == "resolving" ? "pending" : "completed";
                    return s;
                }
                case "form":
                    s.Kind = StepKind.Question;
                    s.Title = S(r["title"]) ?? "a form";
                    s.Status = S(r["lifecycle"]) == "pending" ? "pending" : "completed";
                    return s;
                case "delegation":
                    s.Kind = StepKind.Agent;
                    s.Title = S(r["description"]) ?? S(r["subagentType"]) ?? S(r["toolName"]) ?? "subagent";
                    s.Text = S(r["output"]) ?? "";
                    return s;
                case "workflow":
                    s.Kind = StepKind.Agent;
                    s.Title = S(r["workflowName"]) ?? S(r["description"]) ?? "workflow";
                    s.Text = S(r["summary"]) ?? S(r["error"]) ?? "";
                    return s;
                case "image-generation": s.Kind = StepKind.Tool; s.Title = "image"; s.Sub = OneLine(S(r["prompt"]) ?? ""); return s;
                default:
                    s.Kind = StepKind.Tool;
                    s.Title = S(G(G(r, "presentation"), "title")) ?? wk ?? "work";
                    return s;
            }
        }

        /// MQH's timeline delta (thread-timeline-delta.ts): the changed rows, and the new order as runs of old rows
        /// ([start, count]) and ids of new ones. The merged rows (the old and the new tokens, not copied, nothing
        /// changed: safe on a pool thread while the main thread reads the old rows); null when it does not fit.
        public static List<JToken> MergeRows(JArray previous, JObject delta)
        {
            var ups = new Dictionary<string, JToken>();
            foreach (var r in delta["upsertRows"] as JArray ?? new JArray()) { string id = S(G(r, "id")); if (id != null) ups[id] = r; }
            int applied = 0;
            var seen = new HashSet<string>();
            JToken Resolve(JToken row) { string id = S(G(row, "id")) ?? ""; if (ups.TryGetValue(id, out var n)) { if (seen.Add(id)) applied++; return n; } return row; }
            var outRows = new List<JToken>();
            var prev = previous ?? new JArray();
            if (!(delta["rowOrder"] is JArray order)) foreach (var r in prev) outRows.Add(Resolve(r));
            else
                foreach (var part in order)
                {
                    if (part.Type == JTokenType.String)
                    {
                        if (!ups.TryGetValue((string)part, out var r)) return null;
                        if (seen.Add((string)part)) applied++;
                        outRows.Add(r);
                        continue;
                    }
                    if (!(part is JArray run) || run.Count < 2) return null;
                    int start = (int)run[0], len = (int)run[1];
                    for (int i = start; i < start + len; i++) { if (i < 0 || i >= prev.Count) return null; outRows.Add(Resolve(prev[i])); }
                }
            return applied == ups.Count ? outRows : null;
        }

        /// The merged rows as the new array (main thread): the tokens move over from the old array and the delta.
        public static JArray TakeRows(List<JToken> merged, JArray previous, JObject delta)
        {
            previous?.Clear();
            (delta?["upsertRows"] as JArray)?.Clear();
            var res = new JArray();
            foreach (var r in merged) res.Add(r);
            return res;
        }

        public static JArray ApplyRowDelta(JArray previous, JObject delta)
        {
            var merged = MergeRows(previous, delta);
            return merged == null ? null : TakeRows(merged, previous, delta);
        }

        internal static string OneLine(string s) => Regex.Replace(s ?? "", @"\s*\n\s*", " ⏎ ").Trim();
    }

    /// A thread's state in a few short facts, for the session context and the THREAD panel's status line: what it is
    /// doing now and since when, how quiet it has been, what it did this turn, the pattern ("fixing: re-ran the failing
    /// test after edits") and its last words. Owner 2026-11-11: the local model answers fast and short from this, not
    /// from the whole conversation.
    public sealed class ThreadPulse
    {
        public string Title = "", Provider = "";
        public string State = "idle";   // working | waiting (for the user) | held (its turn waits in the queue) | idle | error | stopped
        public string Phase = "idle";   // thinking | running | editing | reading | searching | browsing | tool | agent | answering | between | starting | waiting | idle
        public string Doing = "";       // "running `npm test`"
        public long Now, TurnStart, PhaseStart, LastActivity, AnsweredAt;
        public int TurnSecs, PhaseSecs, QuietSecs;
        public bool MayBeStuck;
        public string StuckWhy = "";
        public string Pattern = "";     // fixing | errors | testing | editing | installing | exploring | planning | done | ""
        public string PatternText = "";
        public string Prompt = "", LastThought = "", LastUpdate = "", AnswerGist = "", ErrorText = "", Repeats = "", Queued = "";
        public string CurrentCommand;
        public int Thoughts, Commands, Failed, Files, Reads, Added, Removed, Tools, Webs;
        public double ThinkingSeconds;

        public bool Working => State == "working" || State == "waiting";

        private static string S(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;

        public static ThreadPulse From(ThreadTimeline tl, JObject thread, long nowMs)
        {
            var p = new ThreadPulse { Now = nowMs };
            p.Title = S(thread?["title"]) ?? S(thread?["titleFallback"]) ?? S(thread?["id"]) ?? "thread";
            p.Provider = S(thread?["providerId"]) ?? "";
            string st = S(ThreadTimeline.G(ThreadTimeline.G(thread, "runtime"), "displayStatus")) ?? S(thread?["status"]) ?? "";
            string queued = S(thread?["queuedWork"]);
            if (!string.IsNullOrEmpty(queued) && queued != "none") p.Queued = queued;
            var ex = tl?.Exchanges.LastOrDefault(e => e.Prompt != null) ?? tl?.Last;
            p.LastActivity = tl?.LastActivity ?? 0;
            p.QuietSecs = p.LastActivity > 0 && nowMs > p.LastActivity ? (int)((nowMs - p.LastActivity) / 1000) : 0;
            // its next turn waits in the queue (MQH's workspace guard, a limit): not working, held; a new thread whose
            // first message waits is "pending" all that time
            string waitReason = S(thread?["mcxWaitReason"]) ?? "";
            bool held = S(thread?["queuedWork"]) == "waiting" && st != "active" && st != "starting" && st != "stopping" && st != "running";
            string heldWords = "not started: " + (waitReason.Length > 0 ? Clip(waitReason, 150) : "its message waits in the queue");
            if (ex == null)
            {
                p.State = held ? "held" : ThreadTimeline.IsWorking(st) ? "working" : st == "error" ? "error" : "idle";
                p.Phase = held ? "held" : p.State == "working" ? "starting" : "idle";
                p.Doing = held ? heldWords : p.State == "working" ? "starting" : "";
                return p;
            }

            p.Prompt = Clip(ex.Prompt?.Text, 140);
            p.TurnStart = ex.StartedAt;
            p.TurnSecs = ex.StartedAt > 0 && nowMs > ex.StartedAt ? (int)((nowMs - ex.StartedAt) / 1000) : 0;
            p.Thoughts = ex.Thoughts; p.Commands = ex.Commands; p.Failed = ex.Failed; p.Files = ex.Files.Count; p.Reads = ex.Reads + ex.Searches;
            p.Added = ex.Added; p.Removed = ex.Removed; p.Tools = ex.Tools + ex.Agents; p.Webs = ex.Webs; p.ThinkingSeconds = ex.ThinkingSeconds;
            var thought = ex.Steps.LastOrDefault(s => s.Kind == StepKind.Thinking && s.Text.Length > 0);
            if (thought != null) p.LastThought = Clip(LastSentence(thought.Text), 120);
            var update = ex.Steps.LastOrDefault(s => s.Kind == StepKind.Update && s.Text.Length > 0);
            if (update != null) p.LastUpdate = Clip(FirstSentence(update.Text), 140);
            if (ex.Answer != null && ex.Answer.Text.Length > 0) { p.AnswerGist = Clip(FirstSentence(ex.Answer.Text), 160); p.AnsweredAt = ex.Answer.EndedAt; }
            var err = ex.Steps.LastOrDefault(s => s.Kind == StepKind.Error);
            if (err != null) p.ErrorText = Clip(err.Title + (err.Text.Length > 0 ? ": " + err.Text : ""), 140);

            bool waiting = thread?["hasPendingInteraction"]?.Type == JTokenType.Boolean && (bool)thread["hasPendingInteraction"]
                           || st.Contains("wait") && st != "waiting-for-host" || st.Contains("approval")
                           || ex.Live && ex.Steps.Any(s => (s.Kind == StepKind.Approval || s.Kind == StepKind.Question) && s.Live);
            bool working = (ex.Live || ThreadTimeline.IsWorking(st)) && !held;
            bool noHost = st == "waiting-for-host";
            held = held && !waiting;
            p.State = waiting ? "waiting" : working ? "working" : held ? "held" : st == "error" || ex.Status == "error" ? "error" : ex.Status == "interrupted" ? "stopped" : "idle";

            // what it does right now: the newest step still open, else between steps
            var openStep = ex.Steps.LastOrDefault(s => s.Live);
            if (ex.Answer != null && ex.Answer.Live) openStep = ex.Answer;
            if (p.State == "waiting")
            {
                var ask = ex.Steps.LastOrDefault(s => (s.Kind == StepKind.Approval || s.Kind == StepKind.Question) && s.Live);
                p.Phase = "waiting";
                p.Doing = ask != null ? (ask.Kind == StepKind.Question ? "waiting for your answer: " + Clip(ask.Title, 80) : "waiting for your approval (" + Clip(ask.Title, 60) + ")") : "waiting for you";
                p.PhaseStart = ask?.StartedAt ?? p.LastActivity;
            }
            else if (p.State == "working")
            {
                if (openStep != null)
                {
                    p.PhaseStart = openStep.StartedAt;
                    switch (openStep.Kind)
                    {
                        case StepKind.Thinking: p.Phase = "thinking"; p.Doing = "thinking"; break;
                        case StepKind.Command: p.Phase = "running"; p.Doing = "running `" + Clip(openStep.Title, 70) + "`"; p.CurrentCommand = openStep.Title; break;
                        case StepKind.Edit: p.Phase = "editing"; p.Doing = "editing " + FileName(openStep.Title); break;
                        case StepKind.Read: p.Phase = "reading"; p.Doing = "reading " + Clip(FileName(openStep.Title), 60); break;
                        case StepKind.Search: p.Phase = "searching"; p.Doing = "searching " + Clip(openStep.Title, 60); break;
                        case StepKind.Web: p.Phase = "browsing"; p.Doing = "on the web: " + Clip(openStep.Title, 60); break;
                        case StepKind.Agent: p.Phase = "agent"; p.Doing = "a subagent works on " + Clip(openStep.Title, 60); break;
                        case StepKind.Answer: case StepKind.Update: p.Phase = "answering"; p.Doing = "writing the answer"; break;
                        default: p.Phase = "tool"; p.Doing = "using " + Clip(openStep.Title, 50); break;
                    }
                }
                else if (noHost) { p.Phase = "starting"; p.Doing = "waiting for MQH's host daemon"; p.PhaseStart = p.LastActivity; }
                else if (ex.Steps.Count == 0 && ex.Answer == null) { p.Phase = "starting"; p.Doing = "starting"; p.PhaseStart = ex.StartedAt; }
                else
                {
                    p.Phase = "between";
                    var lastStep = ex.Steps.LastOrDefault(s => s.IsAction);
                    p.Doing = lastStep == null ? "working" : "working (last: " + Describe(lastStep) + ")";
                    p.PhaseStart = p.LastActivity;
                }
                if (!string.IsNullOrEmpty(tl?.TodoNow)) p.Doing += $" · to-do {tl.TodoDone}/{tl.TodoTotal}: " + Clip(tl.TodoNow, 60);
            }
            else if (p.State == "held")
            {
                p.Phase = "held";
                p.Doing = heldWords;
            }
            else
            {
                p.Phase = "idle";
                p.Doing = p.State == "error" ? "stopped on an error" : p.State == "stopped" ? "stopped (interrupted)" : ex.Answer != null ? "answered" : "idle";
            }
            if (p.PhaseStart > 0 && nowMs > p.PhaseStart) p.PhaseSecs = (int)((nowMs - p.PhaseStart) / 1000);

            // quiet for too long while it should be producing something: maybe stuck (a long command can be silent;
            // the process vitals then tell whether its CPU still moves)
            if (p.State == "working")
            {
                int limit = p.Phase == "running" ? 600 : p.Phase == "thinking" ? 150 : p.Phase == "answering" ? 90 : p.Phase == "agent" ? 600 : 120;
                if (p.QuietSecs >= limit)
                {
                    p.MayBeStuck = true;
                    p.StuckWhy = $"nothing new for {Dur(p.QuietSecs)} while {(p.Phase == "between" ? "between steps" : p.Doing)}";
                }
            }
            Classify(p, ex);
            return p;
        }

        /// The pattern of the turn so far, in words: fixing errors (a failing command re-run after edits), testing,
        /// editing, installing, exploring (reading, no edits), planning (only thinking), done.
        private static void Classify(ThreadPulse p, ThreadExchange ex)
        {
            var runs = new Dictionary<string, (int n, int failed, int firstIndex, int lastIndex)>();
            var edits = new List<int>();
            for (int i = 0; i < ex.Steps.Count; i++)
            {
                var s = ex.Steps[i];
                if (s.Kind == StepKind.Edit) edits.Add(i);
                if (s.Kind != StepKind.Command) continue;
                string key = CommandKey(s.Title);
                runs.TryGetValue(key, out var r);
                runs[key] = (r.n + 1, r.failed + (s.Failed ? 1 : 0), r.n == 0 ? i : r.firstIndex, i);
            }
            var repeated = runs.Where(kv => kv.Value.n >= 2).OrderByDescending(kv => kv.Value.failed).ThenByDescending(kv => kv.Value.n).ToList();
            if (repeated.Count > 0) p.Repeats = string.Join(", ", repeated.Take(2).Select(kv => $"`{kv.Key}` ×{kv.Value.n}" + (kv.Value.failed > 0 ? $" ({kv.Value.failed} failed)" : "")));
            var fixing = repeated.FirstOrDefault(kv => kv.Value.failed > 0 && edits.Any(e => e > kv.Value.firstIndex && e < kv.Value.lastIndex));
            bool done = p.State == "idle" && ex.Answer != null;
            var lastCmd = ex.Steps.LastOrDefault(s => s.Kind == StepKind.Command);
            bool installing = p.Phase == "running" && IsInstall(p.CurrentCommand);
            if (fixing.Key != null)
            {
                bool nowOk = lastCmd != null && CommandKey(lastCmd.Title) == fixing.Key && !lastCmd.Failed && !lastCmd.Live;
                p.Pattern = "fixing";
                p.PatternText = $"re-ran `{fixing.Key}` {fixing.Value.n} times ({fixing.Value.failed} failed) with edits between: " +
                                (nowOk ? "the last run passed" : done ? "it stopped while fixing" : "it is fixing the failures before it answers");
            }
            else if (installing) { p.Pattern = "installing"; p.PatternText = "installing dependencies (" + Clip(p.CurrentCommand, 60) + ")"; }
            else if (ex.Failed >= 2) { p.Pattern = "errors"; p.PatternText = $"{ex.Failed} steps failed so far" + (lastCmd != null && lastCmd.Failed ? $" (last: `{Clip(lastCmd.Title, 50)}`, exit {lastCmd.ExitCode})" : ""); }
            else if (ex.Files.Count > 0 && ex.Commands > 0 && ex.Steps.FindLastIndex(s => s.Kind == StepKind.Command) > (edits.Count > 0 ? edits[0] : -1))
            { p.Pattern = "testing"; p.PatternText = $"changed {Plural(ex.Files.Count, "file")}, then ran commands to check them"; }
            else if (ex.Files.Count > 0) { p.Pattern = "editing"; p.PatternText = $"changing code: {Plural(ex.Files.Count, "file")} ({string.Join(", ", ex.Files.Take(3).Select(FileName))})"; }
            else if (ex.Reads + ex.Searches + ex.Webs >= 3) { p.Pattern = "exploring"; p.PatternText = $"exploring: read or searched {ex.Reads + ex.Searches + ex.Webs} times, nothing changed yet"; }
            else if (ex.Thoughts > 0 && ex.ActionCount == 0 && !done) { p.Pattern = "planning"; p.PatternText = "thinking it through, no actions yet"; }
            if (done && p.Pattern != "fixing")
            {
                p.Pattern = "done";
                p.PatternText = "done" + (ex.Seconds > 0 ? " in " + Dur((int)ex.Seconds) : "") + (Stats(p).Length > 0 ? ": " + Stats(p) : "");
            }
        }

        private static bool IsInstall(string cmd) => cmd != null && Regex.IsMatch(cmd, @"\b(npm|pnpm|yarn|bun)\s+(i|install|add|ci)\b|\b(apt|apt-get)\s+install\b|\bpip3?\s+install\b|\bcargo\s+(install|fetch)\b|\buv\s+(sync|pip)\b");

        /// The command without what varies between runs of the same check: `cd x &&`, env vars, flags' values, output
        /// redirections; then its first words ("npm test", "pytest tests/x", "cargo build").
        public static string CommandKey(string cmd)
        {
            string c = (cmd ?? "").Trim();
            c = Regex.Replace(c, @"^(cd\s+\S+\s*&&\s*)+", "");
            c = Regex.Replace(c, @"^(\w+=\S*\s+)+", "");
            c = Regex.Split(c, @"\s*(\|\||&&|;|\||2>&1|>|<)\s*")[0];
            var words = c.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith("-")).Take(c.StartsWith("npx ") || c.StartsWith("npm run ") || c.StartsWith("pnpm run ") ? 3 : 2);
            string k = string.Join(" ", words);
            return k.Length > 0 ? k : Clip(cmd, 30);
        }

        private static string Describe(ThreadStep s)
        {
            switch (s.Kind)
            {
                case StepKind.Command: return "ran `" + Clip(s.Title, 50) + "`" + (s.Failed ? $", exit {s.ExitCode?.ToString() ?? "error"}" : "");
                case StepKind.Edit: return "edited " + FileName(s.Title);
                case StepKind.Read: return "read " + Clip(FileName(s.Title), 40);
                case StepKind.Search: return "searched " + Clip(s.Title, 40);
                case StepKind.Web: return "web " + Clip(s.Title, 40);
                default: return Clip(s.Title, 40);
            }
        }

        /// "3 thoughts, 6 commands (2 failed), 3 files changed (+40 -12)"
        public static string Stats(ThreadPulse p)
        {
            var parts = new List<string>();
            if (p.Thoughts > 0) parts.Add(Plural(p.Thoughts, "thought"));
            if (p.Commands > 0) parts.Add(Plural(p.Commands, "command") + (p.Failed > 0 ? $" ({p.Failed} failed)" : ""));
            else if (p.Failed > 0) parts.Add($"{p.Failed} failed");
            if (p.Reads > 0) parts.Add(Plural(p.Reads, "read"));
            if (p.Files > 0) parts.Add(Plural(p.Files, "file") + " changed" + (p.Added + p.Removed > 0 ? $" (+{p.Added} -{p.Removed})" : ""));
            if (p.Tools > 0) parts.Add(Plural(p.Tools, "tool call"));
            if (p.Webs > 0) parts.Add(Plural(p.Webs, "web lookup"));
            return string.Join(", ", parts);
        }

        /// One line for lists (THREADS, the session context's other threads): "working 3 min: running `npm test`".
        public string OneLine()
        {
            switch (State)
            {
                case "working": return $"working {Dur(TurnSecs)}: {Doing}" + (MayBeStuck ? " · may be stuck" : "");
                case "waiting": return Doing;
                case "held": return Doing;
                case "error": return "error" + (ErrorText.Length > 0 ? ": " + Clip(ErrorText, 60) : "");
                case "stopped": return "stopped";
                default: return AnsweredAt > 0 && Now > AnsweredAt ? $"idle, answered {Ago(AnsweredAt)}" : "idle";
            }
        }

        /// The facts for the session context, shortest first; within maxChars.
        public List<string> Lines(int maxChars = 700)
        {
            var l = new List<string>();
            string state = State == "working" ? $"working for {Dur(TurnSecs)}, now {Doing}" + (PhaseSecs > 0 && Phase != "between" ? $" ({Dur(PhaseSecs)})" : "")
                : State == "waiting" || State == "held" ? Doing
                : State == "error" ? "stopped on an error" + (ErrorText.Length > 0 ? ": " + ErrorText : "")
                : State == "stopped" ? "stopped (interrupted)"
                : AnsweredAt > 0 ? $"idle; answered {Ago(AnsweredAt)}" : "idle";
            if (Prompt.Length > 0) l.Add("asked: " + Prompt);
            l.Add("state: " + state);
            if (MayBeStuck) l.Add("warning: " + StuckWhy + ": it may be stuck");
            if (Queued.Length > 0) l.Add("queued messages: " + Queued);
            string stats = Stats(this);
            if (stats.Length > 0) l.Add((State == "working" || State == "waiting" ? "this turn so far: " : "that turn: ") + stats);
            if (PatternText.Length > 0 && Pattern != "done") l.Add("pattern: " + PatternText);
            if (Repeats.Length > 0 && Pattern != "fixing") l.Add("repeated: " + Repeats);
            if (LastThought.Length > 0 && Working) l.Add("last thought: " + LastThought);
            if (LastUpdate.Length > 0 && Working) l.Add("last update: " + LastUpdate);
            if (AnswerGist.Length > 0 && !Working) l.Add("answer: " + AnswerGist);
            int used = 0;
            var outl = new List<string>();
            foreach (var s in l) { if (used + s.Length > maxChars && outl.Count > 0) break; outl.Add(s); used += s.Length + 1; }
            return outl;
        }

        /// The spoken answer to "what is it doing?" / "is it stuck?": two short sentences from the facts, no model. Durations
        /// are written as words ("3 minutes", not "3 min": the voice model read that as "three men").
        public string Speak()
        {
            var sb = new StringBuilder();
            switch (State)
            {
                case "working":
                    sb.Append($"It's been working for {Said(TurnSecs)}; now {Doing}" + (PhaseSecs >= 20 && Phase != "between" ? $" for {Said(PhaseSecs)}" : "") + ".");
                    if (MayBeStuck) sb.Append($" Nothing new for {Said(QuietSecs)}: it may be stuck. STOP in the thread ends the turn.");
                    else if (Pattern == "fixing" || Pattern == "errors" || Pattern == "installing") sb.Append(" " + Cap(PatternText) + ".");
                    else if (QuietSecs >= 45) sb.Append($" Quiet for {Said(QuietSecs)}, still within normal.");
                    else if (Pattern.Length > 0 && PatternText.Length > 0) sb.Append(" So far it " + (Pattern == "testing" ? "changed code and is checking it" : Pattern == "editing" ? "is changing code" : Pattern == "exploring" ? "is reading the code, nothing changed yet" : "is thinking it through") + ".");
                    break;
                case "waiting": sb.Append("It's " + Doing + ". Answer it in the thread."); break;
                case "held": sb.Append("It has " + Doing.Replace("not started:", "not started yet:") + ". SEND NOW in the thread starts it anyway."); break;
                case "error": sb.Append("It stopped on an error" + (ErrorText.Length > 0 ? ": " + Clip(ErrorText, 100) : "") + "."); break;
                case "stopped": sb.Append("It was stopped before it finished."); break;
                default:
                    sb.Append(AnsweredAt > 0 ? $"It's idle; it answered {SaidAgo(AnsweredAt)}" : "It's idle");
                    sb.Append(AnswerGist.Length > 0 ? ": " + Clip(AnswerGist, 120).TrimEnd(':', ';', ',', ' ') : "");
                    if (!Regex.IsMatch(sb.ToString(), @"[.!?…]$")) sb.Append('.');
                    break;
            }
            return sb.ToString();
        }

        /// A recap of the thread from its pulse, no model (owner 2026-11-12: a recap must not take 10-20 s): a short first
        /// sentence with its state, so the voice starts at once, then what it is doing or answered, what was asked and the
        /// work so far. The spoken part is the first two sentences (ShellVoice.SpokenSentences); the rest stays on the HUD.
        public string Recap()
        {
            string name = Clip(Regex.Replace(Title ?? "", @"[“”""]", ""), 40).TrimEnd('…', ' ');
            string who = name.Length > 0 ? "The " + name + " thread" : "This thread";
            var sb = new StringBuilder(who);
            switch (State)
            {
                case "working": sb.Append(MayBeStuck ? " may be stuck." : " is working."); break;
                case "waiting": sb.Append(" is waiting for you."); break;
                case "held": sb.Append(" has not started yet."); break;
                case "error": sb.Append(" stopped on an error."); break;
                case "stopped": sb.Append(" was stopped."); break;
                default: sb.Append(" is idle."); break;
            }
            sb.Append(' ').Append(Speak());
            if (Prompt.Length > 0) sb.Append(" You asked: ").Append(Clip(Prompt, 90).TrimEnd('.', ' ', '…')).Append('.');
            string st = Stats(this);
            if (st.Length > 0) sb.Append(" So far: ").Append(st).Append('.');
            return sb.ToString();
        }

        /// What changed since the previous pulse, as events for the mid-term memory (few: start, a failure, the fixing
        /// pattern, quiet, the answer, an error).
        public static List<string> Changes(ThreadPulse before, ThreadPulse now)
        {
            var e = new List<string>();
            if (now == null) return e;
            string t = "“" + Clip(now.Title, 40) + "”";
            bool wasWorking = before != null && before.Working;
            if (now.Working && !wasWorking && before != null) e.Add($"{t} started working" + (now.Prompt.Length > 0 ? ": " + Clip(now.Prompt, 60) : ""));
            if (before != null && now.Failed > before.Failed && now.TurnStart == before.TurnStart) e.Add($"{t}: a step failed ({now.Failed} so far this turn)");
            if (now.Pattern == "fixing" && (before == null || before.Pattern != "fixing")) e.Add($"{t}: {now.PatternText}");
            if (now.MayBeStuck && (before == null || !before.MayBeStuck)) e.Add($"{t}: {now.StuckWhy}: may be stuck");
            if (before != null && wasWorking && now.State == "idle") e.Add($"{t} answered" + (now.TurnSecs > 0 ? $" after {Dur(now.TurnSecs)}" : "") + (Stats(now).Length > 0 ? $" ({Stats(now)})" : "") + (now.AnswerGist.Length > 0 ? ": " + Clip(now.AnswerGist, 70) : ""));
            if (now.State == "error" && (before == null || before.State != "error")) e.Add($"{t} stopped on an error" + (now.ErrorText.Length > 0 ? ": " + Clip(now.ErrorText, 70) : ""));
            if (now.State == "waiting" && (before == null || before.State != "waiting")) e.Add($"{t} is {now.Doing}");
            if (now.State == "held" && (before == null || before.State != "held")) e.Add($"{t}: {now.Doing}");
            return e;
        }

        // ---- words

        public static string Dur(int secs) => secs < 60 ? secs + " s" : secs < 3600 ? (secs / 60) + " min" + (secs < 600 && secs % 60 >= 10 ? " " + (secs % 60) + " s" : "") : (secs / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h";
        public static string Clock(int secs) => secs < 3600 ? $"{secs / 60}:{secs % 60:00}" : $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}";
        private string Ago(long ms) { int s = (int)Math.Max(0, (Now - ms) / 1000); return s < 60 ? "just now" : Dur(s) + " ago"; }
        /// Spoken forms (Speak): "3 minutes 20 seconds", "3 minutes ago".
        private static string Said(int secs) => SpokenText.Duration(secs);
        private string SaidAgo(long ms) { int s = (int)Math.Max(0, (Now - ms) / 1000); return s < 60 ? "just now" : Said(s) + " ago"; }
        public static string Plural(int n, string one) => n == 1 ? "1 " + one : n + " " + one + "s";
        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        public static string FileName(string path) { if (string.IsNullOrEmpty(path)) return ""; int i = path.TrimEnd('/').LastIndexOf('/'); return i >= 0 ? path.Substring(i + 1) : path; }
        public static string Clip(string s, int n) { s = ThreadTimeline.OneLine(s ?? "").Trim(); return s.Length > n ? s.Substring(0, Math.Max(1, n - 1)).TrimEnd() + "…" : s; }

        private static readonly Regex Marks = new Regex(@"\*\*|__|`|^\s*#+\s*|^\s*>\s*", RegexOptions.Multiline);

        /// The first sentence (or line) of a message, without markdown marks and view blocks.
        public static string FirstSentence(string text)
        {
            string t = Regex.Replace(text ?? "", @"```[\s\S]*?(```|$)", " ");
            t = Marks.Replace(t, "").Trim();
            var lines = t.Split('\n').Select(x => Regex.Replace(x.Trim(), @"^([-*+•]|\d+[.)])\s+", "")).Where(x => x.Length > 0).ToList();
            if (lines.Count == 0) return "";
            string line = lines[0];
            // "It runs:" + the next line ("- Local: http://localhost:5173/") reads as one sentence
            if (line.EndsWith(":") && lines.Count > 1) line = line + " " + lines[1];
            var m = Regex.Match(line, @"^(.{20,}?[.!?])(\s|$)");
            return m.Success ? m.Groups[1].Value : line;
        }

        /// The newest sentence of a reasoning text (what it is thinking about now).
        public static string LastSentence(string text)
        {
            string t = Marks.Replace(text ?? "", "").Trim();
            var parts = Regex.Split(t, @"(?<=[.!?])\s+|\n+").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (parts.Count == 0) return "";
            string last = parts[parts.Count - 1];
            return last.Length < 25 && parts.Count > 1 ? parts[parts.Count - 2] + " " + last : last;
        }
    }
}
