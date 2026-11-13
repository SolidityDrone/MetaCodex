using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    // Core Session Context Protocol (docs/session-context.md): what is going on in the workbench, for the local model
    // only. The pure parts live here (no Unity objects), so they are tested without the app.

    /// One thing that happened in the session: a thread changed state, an approval was asked, a script failed, ...
    public sealed class SessionEvent
    {
        public DateTime At;
        public string Kind, Subject, Text;
    }

    /// The mid-term memory: the session's events, oldest first, at most Max and none older than Age. The same event
    /// repeated within 30 s is kept once. Saved as JSON lines so it survives an app restart within the window.
    public sealed class SessionMemory
    {
        public int Max = 400;
        public TimeSpan Age = TimeSpan.FromHours(3);
        private readonly List<SessionEvent> _events = new List<SessionEvent>();
        public IReadOnlyList<SessionEvent> Events => _events;
        public bool Dirty { get; private set; }

        public void Add(string kind, string subject, string text, DateTime? at = null)
        {
            var now = at ?? DateTime.Now;
            if (string.IsNullOrEmpty(text)) return;
            var last = _events.LastOrDefault(e => e.Kind == kind && e.Subject == subject);
            if (last != null && last.Text == text && (now - last.At).TotalSeconds < 30) return;
            _events.Add(new SessionEvent { At = now, Kind = kind ?? "", Subject = subject ?? "", Text = text });
            Prune(now);
            Dirty = true;
        }

        public void Prune(DateTime now)
        {
            _events.RemoveAll(e => now - e.At > Age);
            if (_events.Count > Max) _events.RemoveRange(0, _events.Count - Max);
        }

        public IEnumerable<SessionEvent> Recent(TimeSpan window, DateTime now, string subject = null) =>
            _events.Where(e => now - e.At <= window && (subject == null || e.Subject == subject));

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                foreach (var e in _events)
                    sb.Append(new JObject { ["at"] = e.At.ToString("o"), ["kind"] = e.Kind, ["subject"] = e.Subject, ["text"] = e.Text }.ToString(Newtonsoft.Json.Formatting.None)).Append('\n');
                File.WriteAllText(path + ".tmp", sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
                Dirty = false;
            }
            catch (Exception) { }
        }

        public void Load(string path, DateTime now)
        {
            _events.Clear();
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject o;
                    using (var rd = new Newtonsoft.Json.JsonTextReader(new StringReader(line)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                        o = JObject.Load(rd);
                    if (!DateTime.TryParse((string)o["at"], null, DateTimeStyles.RoundtripKind, out var at)) continue;
                    _events.Add(new SessionEvent { At = at, Kind = (string)o["kind"] ?? "", Subject = (string)o["subject"] ?? "", Text = (string)o["text"] ?? "" });
                }
                Prune(now);
            }
            catch (Exception) { _events.Clear(); }
            Dirty = false;
        }
    }

    /// What the Linux's processes are doing, from `ps -eo pid=,ppid=,etimes=,times=,stat=,comm=,args=` samples: the
    /// notable ones (builds, installs, agents, servers) and whether their CPU time still grows.
    public sealed class ProcessVitals
    {
        public sealed class Proc
        {
            public int Pid, PPid;
            public long Elapsed;
            public long Cpu;
            public string Stat, Comm, Args;
            public DateTime FirstSeen, LastProgress, LastSeen;
            public long CpuAtMinute = -1;
            public DateTime MinuteAt;
            public long CpuLastMinute;
        }

        private static readonly HashSet<string> NotableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cargo", "rustc", "gcc", "g++", "cc", "c++", "cc1", "cc1plus", "clang", "clang++", "ld", "lld", "ld.lld", "make", "ninja", "cmake",
            "gradle", "java", "javac", "node", "npm", "npx", "pnpm", "bun", "python", "python3", "pip", "pip3", "go", "apt", "apt-get", "dpkg",
            "tar", "xz", "unzip", "curl", "wget", "git", "aapt2", "apksigner", "zipalign", "claude", "codex", "opencode", "qvac", "uv",
        };
        private readonly Dictionary<int, Proc> _procs = new Dictionary<int, Proc>();
        public DateTime LastSample { get; private set; }

        /// The processes worth telling the user about, longest-running first.
        public IEnumerable<Proc> Notable => _procs.Values.Where(IsNotable).OrderByDescending(p => p.Elapsed);

        public static bool IsNotable(Proc p)
        {
            string a = p.Args ?? "";
            if (a.Contains("mcx-setup") || a.Contains("mcx-app") || a.Contains("build.sh")) return true;
            if (a.Contains("/mqh") && !a.Contains("mqh thread")) return false; // the harness itself: always there
            return NotableNames.Contains(p.Comm ?? "");
        }

        private static readonly char[] PsSeparators = { ' ', '\t', '\r' };

        public void Update(string psOutput, DateTime now)
        {
            var seen = new HashSet<int>();
            foreach (var raw in (psOutput ?? "").Split('\n'))
            {
                var f = raw.Split(PsSeparators, StringSplitOptions.RemoveEmptyEntries); // not Regex.Split: 88-148 ms a sample on the headset (IL2CPP), every 20 s
                if (f.Length < 6 || !int.TryParse(f[0], out int pid)) continue;
                int.TryParse(f[1], out int ppid);
                long.TryParse(f[2], out long el);
                long cpu = ParseCpu(f[3]);
                string args = f.Length > 6 ? string.Join(" ", f.Skip(6)) : f[5];
                seen.Add(pid);
                if (!_procs.TryGetValue(pid, out var p) || p.Comm != f[5])
                {
                    p = new Proc { Pid = pid, FirstSeen = now, LastProgress = now, MinuteAt = now, CpuAtMinute = cpu };
                    _procs[pid] = p;
                }
                else if (cpu > p.Cpu) p.LastProgress = now;
                p.PPid = ppid; p.Elapsed = el; p.Stat = f[4]; p.Comm = f[5]; p.Args = args; p.LastSeen = now; p.Cpu = cpu;
                if ((now - p.MinuteAt).TotalSeconds >= 60) { p.CpuLastMinute = cpu - p.CpuAtMinute; p.CpuAtMinute = cpu; p.MinuteAt = now; }
            }
            foreach (var gone in _procs.Keys.Where(k => !seen.Contains(k)).ToList()) _procs.Remove(gone);
            LastSample = now;
        }

        /// `times` is seconds; some ps print [[dd-]hh:]mm:ss.
        private static long ParseCpu(string s)
        {
            if (long.TryParse(s, out long secs)) return secs;
            long total = 0;
            var dayParts = s.Split('-');
            if (dayParts.Length == 2 && long.TryParse(dayParts[0], out long d)) { total += d * 86400; s = dayParts[1]; }
            foreach (var part in s.Split(':')) total = total * 60 + (long.TryParse(part, out long v) ? v : 0);
            return total;
        }

        /// build, install, agent or other (servers, shells of tools), for questions that name one.
        public static string KindOf(Proc p)
        {
            string c = (p.Comm ?? "").ToLowerInvariant(), a = (p.Args ?? "").ToLowerInvariant();
            if (a.Contains("build.sh") || a.Contains("mcx-app build") || new[] { "cargo", "rustc", "gcc", "g++", "cc", "c++", "cc1", "cc1plus", "clang", "clang++", "ld", "lld", "ld.lld", "make", "ninja", "cmake", "gradle", "javac", "aapt2", "apksigner", "zipalign" }.Contains(c)) return "build";
            if (a.Contains("mcx-setup") || new[] { "apt", "apt-get", "dpkg", "pip", "pip3", "tar", "xz", "unzip", "curl", "wget", "uv" }.Contains(c) || a.Contains("npm install") || a.Contains("npm i ")) return "install";
            if (new[] { "claude", "codex", "opencode" }.Contains(c)) return "agent";
            return "other";
        }

        /// In words: working, idle for a while, or possibly stuck (no CPU for minutes).
        public static string Verdict(Proc p, DateTime now)
        {
            if ((p.Stat ?? "").StartsWith("Z")) return "finished (not reaped yet)";
            if (p.Elapsed < 20) return "just started";
            double idle = (now - p.LastProgress).TotalSeconds;
            if (idle < 45) return p.CpuLastMinute > 0 ? $"working ({p.CpuLastMinute} s of CPU in the last minute)" : "working";
            if (idle >= 180) return $"no CPU for {Math.Round(idle / 60)} min: waiting for something (input, network, a lock) or stuck";
            return $"idle for {Math.Round(idle)} s";
        }

        public static string Describe(Proc p, DateTime now)
        {
            string what = Short(p.Args, 70);
            return $"{what} (pid {p.Pid}, running {Duration(p.Elapsed)}): {Verdict(p, now)}";
        }

        public static string Duration(long secs) => secs < 90 ? secs + " s" : secs < 5400 ? Math.Round(secs / 60.0) + " min" : (secs / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h";
        internal static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s.Substring(0, n) + "…" : s;
    }

    /// What a shell command does that a user should know before allowing it. Conservative and deterministic: it lists
    /// facts, it never calls a command safe.
    public static class CommandRisk
    {
        private static readonly (Regex rx, string what)[] Rules =
        {
            (new Regex(@"\brm\s+(-[a-zA-Z]*[rR][a-zA-Z]*|--recursive)\b"), "deletes files and folders recursively"),
            (new Regex(@"\brm\b"), "deletes files"),
            (new Regex(@"\b(rm|mv|cp|chmod|chown)\b[^;&|]*\s(/|~|\$HOME|\.\.)(\s|/|$)"), "touches files outside the project (/, ~ or ..)"),
            (new Regex(@"\bsudo\b|\bsu\s"), "runs as administrator"),
            (new Regex(@"\b(curl|wget)\b[^|;]*\|\s*(sh|bash|zsh|python3?|node)\b"), "downloads a script and runs it"),
            (new Regex(@"\b(curl|wget|ssh|scp|rsync|nc|ncat|ftp)\b"), "uses the network"),
            (new Regex(@"\bgit\s+push\b[^;&|]*(--force|\s-f\b)"), "force-pushes (rewrites the remote branch)"),
            (new Regex(@"\bgit\s+push\b"), "pushes to the remote repository"),
            (new Regex(@"\bgit\s+(reset\s+--hard|clean\s+-[a-zA-Z]*f|checkout\s+--\s|restore\b|stash\s+drop|branch\s+-D)"), "throws away local changes"),
            (new Regex(@"\bchmod\b[^;&|]*(777|-R)|\bchown\b[^;&|]*-R"), "changes permissions widely"),
            (new Regex(@"\b(apt|apt-get)\s+(install|remove|purge|upgrade)|\bnpm\s+(i|install)\s+(-g|--global)|\bpip3?\s+install|\bcargo\s+install"), "installs or removes software"),
            (new Regex(@"\b(dd|mkfs(\.\w+)?|fdisk|parted)\b"), "writes a disk directly"),
            (new Regex(@"\b(kill|pkill|killall)\b"), "stops processes"),
            (new Regex(@"(>\s*/(etc|usr|boot|lib)\b|\btee\s+/(etc|usr)\b)"), "writes system files"),
            (new Regex(@"\b(printenv|env)\s*($|\|)|\.ssh/|\.env\b|(?i:token|secret|password|api[_-]?key)"), "may read or show secrets"),
            (new Regex(@"\beval\b|\bbase64\s+(-d|--decode)\b"), "runs code it builds at run time"),
        };

        public static List<string> Flags(string command)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return list;
            foreach (var (rx, what) in Rules)
                if (rx.IsMatch(command) && !list.Contains(what))
                {
                    // the specific rule wins over its general one
                    if (what == "deletes files" && list.Contains("deletes files and folders recursively")) continue;
                    if (what == "pushes to the remote repository" && list.Contains("force-pushes (rewrites the remote branch)")) continue;
                    list.Add(what);
                }
            return list;
        }
    }

    /// What a question is about: its topics, whether it points ("this"), and the tools it names.
    public sealed class SessionQuestion
    {
        public string Text;
        public bool IsQuestion, Deictic;
        public readonly HashSet<string> Topics = new HashSet<string>();
        public readonly List<string> Tools = new List<string>();

        private static readonly string[] QuestionStarts =
        {
            "what", "whats", "what's", "why", "how", "is", "are", "was", "were", "do", "does", "did", "can", "could", "should", "will", "which", "who",
            "where", "when", "any", "have", "has",
        };
        private static readonly Regex CountWords = new Regex(@"\bhow many\b|\bnumber of\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly (string topic, string[] words)[] TopicWords =
        {
            ("tools", new[] { "install", "installed", "have", "toolchain", "version", "compiler", "sdk", "available", "toolchain" }),
            ("processes", new[] { "build", "compil", "stuck", "running", "process", "hang", "frozen", "slow", "progress", "finish", "done yet", "download", "still" }),
            ("approval", new[] { "command", "accept", "allow", "approv", "permission", "asked", "asking", "safe", "danger", "risk", "harm" }),
            ("thread", new[] { "thread", "agent", "doing", "achiev", "so far", "working on", "chat", "conversation", "claude", "codex", "opencode" }),
            ("extensions", new[] { "script", "plugin", "gesture", "skill", "error", "broken" }),
            ("system", new[] { "storage", "space", "disk", "memory", "setup", "first use", "capture", "screenshot", "clipboard", "terminal", "window", "panel", "focus" }),
            ("events", new[] { "happened", "while i was", "recently", "last", "today", "lately", "away" }),
        };
        // spoken names -> SYSTEM > TOOLS names
        private static readonly (string[] words, string[] tools)[] ToolNames =
        {
            (new[] { "rust", "rustc", "cargo" }, new[] { "rustc", "cargo" }),
            (new[] { "node", "nodejs", "node.js", "javascript" }, new[] { "node", "npm" }),
            (new[] { "python", "pip" }, new[] { "python3", "pip" }),
            (new[] { "java", "jdk" }, new[] { "java" }),
            (new[] { "ndk" }, new[] { "ndk" }),
            (new[] { "android sdk", "android.jar" }, new[] { "android-sdk" }),
            (new[] { "openxr" }, new[] { "openxr" }),
            (new[] { "git " , "git?" }, new[] { "git" }),
            (new[] { "golang", " go " , " go?" }, new[] { "go" }),
            (new[] { "gcc", "c compiler", "c++" }, new[] { "gcc" }),
            (new[] { "clang" }, new[] { "clang" }),
            (new[] { "cmake" }, new[] { "cmake" }),
            (new[] { "bun" }, new[] { "bun" }),
            (new[] { "pnpm" }, new[] { "pnpm" }),
            (new[] { "adb" }, new[] { "adb" }),
            (new[] { "claude" }, new[] { "claude" }),
            (new[] { "codex" }, new[] { "codex" }),
            (new[] { "opencode" }, new[] { "opencode" }),
            (new[] { "docker" }, new[] { "docker" }),
        };

        public static SessionQuestion Parse(string text)
        {
            var q = new SessionQuestion { Text = text ?? "" };
            string t = " " + q.Text.ToLowerInvariant().Replace("’", "'").Trim() + " ";
            string first = Regex.Match(t, @"^\s*([\p{L}']+)").Groups[1].Value;
            q.IsQuestion = q.Text.TrimEnd().EndsWith("?") || QuestionStarts.Contains(first);
            q.Deictic = Regex.IsMatch(t, @"\b(this|that|these|those|here|it)\b");
            foreach (var (topic, words) in TopicWords) if (words.Any(w => t.Contains(w))) q.Topics.Add(topic);
            foreach (var (words, tools) in ToolNames)
                if (words.Any(w => t.Contains(w))) { q.Tools.AddRange(tools.Where(x => !q.Tools.Contains(x))); q.Topics.Add("tools"); }
            if (q.Topics.Count == 0) q.Topics.Add("general");
            q.Counting = CountWords.IsMatch(t);
            return q;
        }

        /// "How many ...?": a count, answered from the numbers.
        public bool Counting;

        /// How much it asks for interpretation rather than an action, 0..1 (a question about state, not an order).
        public float InterpretationScore()
        {
            string t = " " + Text.ToLowerInvariant().Trim() + " ";
            bool action = Regex.IsMatch(t, @"\b(open(ing)?|clos(e|ing)|show(ing)?|start(ing)?|stop(ping)?|turn(ing)?|set(ting)?|make|create|run|launch|hide|switch|toggle|take)\b");
            // an order put politely ("can you open the files?", "can I open...", "is it possible to...")
            // is a command, not a question about state
            if (action && Regex.IsMatch(t, @"^\s*((can|could|would|will) (you|i|we)|please|is it possible to|(do|would) you mind|mind)\b")) return 0.1f;
            float s = IsQuestion ? 0.6f : 0f;
            if (Topics.Contains("processes") || Topics.Contains("approval") || Topics.Contains("thread") || Topics.Contains("events")) s += 0.25f;
            if (Topics.Contains("tools") && Tools.Count > 0) s += 0.25f;
            if (Counting) s += 0.25f;
            if (action && !IsQuestion) s -= 0.4f;
            return Math.Max(0f, Math.Min(1f, s));
        }
    }

    /// The context text the local model reads: one fact per line, the sections the question needs first, within a
    /// budget of characters (the 0.6B model answers best from a short context).
    public static class SessionContextText
    {
        public static readonly string[] Order = { "focus", "approval", "thread", "processes", "tools", "extensions", "system", "events", "threads" };

        public static string Build(JObject snapshot, SessionQuestion q, int budget = 1800)
        {
            var sections = snapshot?["sections"] as JObject ?? new JObject();
            var wanted = new List<string> { "focus" };
            foreach (var t in q.Topics) if (t != "general" && !wanted.Contains(t)) wanted.Add(t);
            if (q.Deictic && !wanted.Contains("thread")) wanted.Add("thread");
            foreach (var s in Order) if (!wanted.Contains(s)) wanted.Add(s);
            var sb = new StringBuilder();
            sb.Append("NOW ").Append((string)snapshot?["now"] ?? "").Append('\n');
            foreach (var name in wanted)
            {
                if (!(sections[name] is JArray lines) || lines.Count == 0) continue;
                foreach (var l in lines)
                {
                    string line = name.ToUpperInvariant() + ": " + (string)l;
                    if (sb.Length + line.Length + 1 > budget) return sb.ToString().TrimEnd();
                    sb.Append(line).Append('\n');
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static readonly Regex StateWords = new Regex(@"\b(stuck|still|progress|doing|working|busy|finish(ed)?|done yet|how long|waiting|status|hang(ing)?|frozen|alive)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RecapWords = new Regex(@"achiev|so far|summar|recap|what did|what has it done|what have|\bwhy\b|explain", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// A recap request ("give me a recap of the login thread", "summarize this thread", "what has it done so far"):
        /// answered from the thread's pulse, not the model ("why" / "explain" still go to the model).
        public static readonly Regex RecapOnly = new Regex(@"\b(recap|summar\w*|catch me up|(done|achieved|did) so far|what (did|has) (it|the agent|this thread) (do|done|achiev\w*)|what have you done|overview of)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// The question answered from the facts alone, or null when it needs the model (explanations).
        public static string ExactAnswer(JObject snapshot, SessionQuestion q)
        {
            var data = snapshot?["data"] as JObject ?? new JObject();
            // a recap of a thread with a pulse: its template (measured 2026-11-12: the model took 15-19 s to the first word
            // on the headset and got a working thread wrong 16 times of 16; this speaks in about 3-4 s)
            if (!q.Counting && data["pulse"] is JObject rp && (string)rp["recap"] != null && RecapOnly.IsMatch(q.Text ?? "")) return (string)rp["recap"];
            if (q.Counting) { string n = HowMany(data, q); if (n != null) return n; }
            if (q.Topics.Contains("tools") && q.Tools.Count > 0 && data["tools"] is JArray tools)
            {
                var found = new List<string>();
                var missing = new List<string>();
                foreach (var name in q.Tools)
                {
                    var t = tools.FirstOrDefault(x => (string)x["name"] == name);
                    if (t == null) continue;
                    if (t["installed"]?.Value<bool>() == true) found.Add(name + (string.IsNullOrEmpty((string)t["version"]) ? "" : " " + (string)t["version"]));
                    else missing.Add(name);
                }
                if (found.Count > 0 && missing.Count == 0) return "Yes: " + string.Join(", ", found) + ".";
                if (found.Count == 0 && missing.Count > 0) return $"No, {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} not installed. SYSTEM > TOOLS installs {(missing.Count == 1 ? "it" : "them")}.";
                if (found.Count > 0) return "Partly: " + string.Join(", ", found) + " installed; " + string.Join(", ", missing) + " missing.";
                if (q.Tools.Contains("docker")) return "No: Docker does not run on the headset (no container support in the app's Linux).";
            }
            if (q.Topics.Contains("approval"))
            {
                var ap = data["approval"] as JObject;
                if (ap == null) return q.Topics.Contains("thread") || q.Topics.Contains("processes") ? null : "Nothing is waiting for your approval right now.";
                string what = (string)ap["what"];
                var flags = (ap["risks"] as JArray)?.Select(x => (string)x).ToList() ?? new List<string>();
                string risks = flags.Count == 0 ? "The scan found nothing risky in it" : "It " + string.Join(", ", flags.Take(4));
                return $"In “{(string)ap["thread"]}” the agent asks to {((string)ap["kind"] == "command" ? "run" : "use")}: {ProcessVitals.Short(what, 120)}. {risks}. It's your call: ALLOW or DENY in the thread.";
            }
            // "what is it doing?", "is it stuck?", "is the agent still working?": the thread's pulse says it in two
            // sentences, no model (a build / install question stays with the process vitals below)
            if (data["pulse"] is JObject pulse && (q.Topics.Contains("thread") || q.Deictic) && StateWords.IsMatch(q.Text) && !RecapWords.IsMatch(q.Text)
                && !(q.Topics.Contains("processes") && Regex.IsMatch(q.Text, @"build|compil|install|download|scaric", RegexOptions.IgnoreCase)))
                return (string)pulse["speak"];
            if (q.Topics.Contains("processes") && !q.Topics.Contains("thread") && data["processes"] is JArray procs)
            {
                string t = q.Text.ToLowerInvariant();
                string want = Regex.IsMatch(t, @"build|compil") ? "build" : Regex.IsMatch(t, @"install|download|setup|scaric") ? "install" : null;
                string[] rank = { "build", "install", "agent", "other" };
                var list = procs.OfType<JObject>().Where(p => want == null || (string)p["kind"] == want)
                    .OrderBy(p => Array.IndexOf(rank, (string)p["kind"])).Take(2).ToList();
                if (list.Count == 0)
                    return want == "build" ? "No build is running in the Linux right now." : want == "install" ? "No install is running in the Linux right now." : "Nothing notable is running in the Linux right now.";
                return string.Join(" ", list.Select(p => $"{(string)p["cmd"]} (running {(string)p["elapsed"]}): {(string)p["verdict"]}."));
            }
            return null;
        }

        /// "How many ...?" from data.counts: captures, threads, terminals, windows, scripts and plugins, builds. Null when
        /// the thing counted is none of these (the model answers from the context then).
        public static string HowMany(JObject data, SessionQuestion q)
        {
            var c = data?["counts"] as JObject;
            if (c == null) return null;
            string t = q.Text.ToLowerInvariant();
            int N(string k) => c[k]?.Value<int>() ?? 0;
            string Plural(int n, string one, string many) => n == 1 ? "1 " + one : n + " " + many;
            if (Regex.IsMatch(t, @"captur|screenshot|screen shot|clipboard"))
            {
                int n = N("captures"), max = N("capturesMax"), taken = N("capturesTaken");
                string newest = (string)c["capturesNewest"], since = (string)c["capturesSince"];
                if (n == 0 && taken == 0) return "No captures yet: SELECT on a window takes one.";
                string s = n == 0 ? "The clipboard is empty" : Plural(n, "capture", "captures") + " in the clipboard" + (newest != null ? ", the newest from " + newest : "");
                // more taken than kept: the clipboard dropped the oldest (it keeps `max`), or some were deleted
                if (taken > n && since != null) s += n >= max ? $"; {taken} taken since {since}, it keeps the newest {max}" : $"; {taken} taken since {since}";
                return s + ".";
            }
            if (Regex.IsMatch(t, @"thread|chat|conversation|discussion"))
            {
                int n = N("threads"), run = N("threadsRunning"), wait = N("threadsWaiting");
                if (n == 0) return "No threads yet.";
                var parts = new List<string>();
                if (run > 0) parts.Add(run + " running");
                if (wait > 0) parts.Add(wait + " waiting for you");
                return Plural(n, "thread", "threads") + (parts.Count > 0 ? ": " + string.Join(", ", parts) : ", none running") + ".";
            }
            if (Regex.IsMatch(t, @"terminal")) return Plural(N("terminals"), "terminal", "terminals") + " open.";
            if (Regex.IsMatch(t, @"window|panel")) return Plural(N("panels"), "window", "windows") + " open.";
            if (Regex.IsMatch(t, @"script|plugin|gesture"))
                return $"{Plural(N("extensionsOn"), "script or plugin", "scripts and plugins")} running, {N("extensionsOff")} turned off.";
            if (Regex.IsMatch(t, @"build|compil|process"))
            {
                int n = (data["processes"] as JArray)?.Count(p => (string)p["kind"] == "build") ?? 0;
                return n == 0 ? "No build is running in the Linux right now." : Plural(n, "build", "builds") + " running in the Linux.";
            }
            return null;
        }

        /// What the local model is asked (OpenAI-style messages); /no_think keeps Qwen3 from reasoning aloud.
        public static Newtonsoft.Json.Linq.JArray Messages(string context, string question) => new JArray
        {
            new JObject
            {
                ["role"] = "system",
                ["content"] = "/no_think You are the assistant of MetaCodex, a VR headset workspace. Answer the user's question about their " +
                              "workspace using ONLY the CONTEXT. One or two short sentences (under 40 words), plain spoken words, no lists, no markdown. If the " +
                              "context does not say, say you don't know. Never call a command safe: say what it does.",
            },
            new JObject { ["role"] = "user", ["content"] = "CONTEXT:\n" + context + "\n\nQUESTION: " + question },
        };

        /// The model's reply, without a reasoning block or quotes.
        public static string CleanAnswer(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"<think>[\s\S]*?</think>", "").Trim();
            s = Regex.Replace(s, @"^(answer|risposta)\s*:\s*", "", RegexOptions.IgnoreCase).Trim().Trim('"');
            // cut by max_tokens in the middle of a sentence: keep the finished ones
            if (s.Length > 0 && !Regex.IsMatch(s, @"[.!?…][""')\]”’]*$"))
            {
                var m = Regex.Match(s, @"^[\s\S]*[.!?…](?=\s)");
                if (m.Success && m.Length >= 12) s = m.Value.Trim();
            }
            return s;
        }
    }
}
