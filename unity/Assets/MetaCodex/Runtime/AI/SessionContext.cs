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

        public void Update(string psOutput, DateTime now)
        {
            var seen = new HashSet<int>();
            foreach (var raw in (psOutput ?? "").Split('\n'))
            {
                var f = Regex.Split(raw.Trim(), @"\s+", RegexOptions.None);
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
            "where", "when", "any", "have", "has", "cosa", "che", "perche", "perché", "come", "quale", "quali", "chi", "dove", "quando", "è", "sono",
            "c'e", "c'è", "ci", "abbiamo", "hai", "ha", "posso", "devo",
        };
        private static readonly (string topic, string[] words)[] TopicWords =
        {
            ("tools", new[] { "install", "installed", "have", "toolchain", "version", "compiler", "sdk", "available", "abbiamo", "installat", "versione", "disponibil", "toolchain" }),
            ("processes", new[] { "build", "compil", "stuck", "running", "process", "hang", "frozen", "slow", "progress", "finish", "done yet", "download", "still", "bloccat", "processo", "in esecuzione", "finit", "lento", "va avanti", "sta andando" }),
            ("approval", new[] { "command", "accept", "allow", "approv", "permission", "asked", "asking", "safe", "danger", "risk", "harm", "comando", "accett", "permess", "sicur", "pericol", "rischi", "chiede", "chiedendo" }),
            ("thread", new[] { "thread", "agent", "doing", "achiev", "so far", "working on", "chat", "conversation", "claude", "codex", "opencode", "agente", "sta facendo", "ha fatto", "conversazion", "finora" }),
            ("extensions", new[] { "script", "plugin", "gesture", "skill", "error", "broken", "gesto", "gesti", "errore", "rotto" }),
            ("system", new[] { "storage", "space", "disk", "memory", "setup", "first use", "capture", "clipboard", "spazio", "memoria", "disco", "catture", "appunti" }),
            ("events", new[] { "happened", "while i was", "recently", "last", "today", "lately", "away", "successo", "mentre ero", "ultimamente", "oggi", "ultimo", "ultima" }),
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
            (new[] { "github cli", " gh " }, new[] { "gh" }),
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
            q.Deictic = Regex.IsMatch(t, @"\b(this|that|these|those|here|it)\b|\b(questo|questa|questi|quello|quella|qui)\b");
            foreach (var (topic, words) in TopicWords) if (words.Any(w => t.Contains(w))) q.Topics.Add(topic);
            foreach (var (words, tools) in ToolNames)
                if (words.Any(w => t.Contains(w))) { q.Tools.AddRange(tools.Where(x => !q.Tools.Contains(x))); q.Topics.Add("tools"); }
            if (q.Topics.Count == 0) q.Topics.Add("general");
            return q;
        }

        /// How much it asks for interpretation rather than an action, 0..1 (a question about state, not an order).
        public float InterpretationScore()
        {
            string t = " " + Text.ToLowerInvariant().Trim() + " ";
            bool action = Regex.IsMatch(t, @"\b(open|close|show|start|stop|turn|set|make|create|run|launch|hide|switch|apri(re)?|chiud(i|ere)|mostr(a|are)|avvi(a|are)|ferm(a|are)|accend(i|ere)|speg?n(i|ere)|impost(a|are)|nascond(i|ere))\b");
            // a polite order ("can you open the files?", "puoi aprire...") is a command, not a question about state
            if (action && Regex.IsMatch(t, @"^\s*((can|could|would|will) you|please|puoi|potresti|per favore)\b")) return 0.1f;
            float s = IsQuestion ? 0.6f : 0f;
            if (Topics.Contains("processes") || Topics.Contains("approval") || Topics.Contains("thread") || Topics.Contains("events")) s += 0.25f;
            if (Topics.Contains("tools") && Tools.Count > 0) s += 0.25f;
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

        /// The question answered from the facts alone, or null when it needs the model (recaps, explanations).
        public static string ExactAnswer(JObject snapshot, SessionQuestion q)
        {
            var data = snapshot?["data"] as JObject ?? new JObject();
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

        /// What the local model is asked (OpenAI-style messages); /no_think keeps Qwen3 from reasoning aloud.
        public static Newtonsoft.Json.Linq.JArray Messages(string context, string question) => new JArray
        {
            new JObject
            {
                ["role"] = "system",
                ["content"] = "/no_think You are the assistant of MetaCodex, a VR headset workspace. Answer the user's question about their " +
                              "workspace using ONLY the CONTEXT. One or two short sentences, plain spoken words, no lists, no markdown. If the " +
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
            return s;
        }
    }
}
