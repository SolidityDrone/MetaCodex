using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    // THREAD's sidebar, CHANGES (docs/thread-sidebar.md): every file the thread changed. In a git repo: what git sees
    // against the thread's base (HEAD in a shared checkout, the merge base with its base branch in a worktree),
    // tracked and untracked, merged with the files of the timeline's file-change and edit/write tool rows; outside
    // git: the timeline's files only. Pure (no Unity objects): tested without the app (tests/sidebar).

    public sealed class ChangedFile
    {
        public string Path = "";       // relative to the project folder (shown)
        public string RepoPath;        // relative to the repo's top (for git commands); null outside git
        public string Kind = "modify"; // add | modify | delete | untracked
        public int Added, Removed;
        public bool Binary;
        public bool ByThread;          // the timeline shows the agent editing it (or the folder is the thread's own)
        public bool InGit;             // git sees a change against the base now
        public bool Committed;         // changed since the base but committed (worktree commits)
        public string GitCode = "";    // porcelain XY, or "" when git sees nothing
        public long Seq;               // the newest timeline row that touched it
        public string TimelineDiff;    // the diff the timeline carried (outside git: the only one there is)
        public long Size = -1;

        /// VS Code's source-control letter: M, A, U (untracked), D; "✓" committed (or edited back: git sees no change).
        public string Letter => Kind == "untracked" ? "U" : Kind == "add" ? "A" : Kind == "delete" ? "D" : Committed ? "✓" : "M";
        public string Name => Path.Substring(Path.LastIndexOf('/') + 1);
        public string Dir { get { int k = Path.LastIndexOf('/'); return k > 0 ? Path.Substring(0, k) : ""; } }
    }

    public sealed class GitSnapshot
    {
        public int StatusCut; // status lines left out above ThreadChanges.MaxStatus
        public bool Repo;
        public string Error;           // the folder is missing, git is not installed, ...
        public string Top = "", Prefix = "", Branch = "", Base = "HEAD", BaseLabel = "HEAD";
        public readonly List<(string code, string path)> Status = new List<(string, string)>();
        public readonly Dictionary<string, (int a, int r, bool bin)> Numstat = new Dictionary<string, (int, int, bool)>();
        public readonly Dictionary<string, string> NameStatus = new Dictionary<string, string>();
    }

    public static class ThreadChanges
    {
        public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

        /// The one shell run that reads a folder's git state for CHANGES (one process: a proot / wsl start costs more
        /// than the git calls). `baseBranch`: a worktree's base (its changes are counted from the merge base). No
        /// backslashes in it: the Editor's wsl.exe command line would double them.
        public static string Script(string root, string baseBranch)
        {
            string q = Q(root), bb = string.IsNullOrEmpty(baseBranch) ? "''" : Q(baseBranch);
            return "cd -- " + q + " 2>/dev/null || { echo '@@ERR'; echo 'The project folder does not exist: '" + q + "; exit 0; }; " +
                   "command -v git >/dev/null 2>&1 || { echo '@@ERR'; echo 'git is not installed in Linux'; exit 0; }; " +
                   "top=$(git rev-parse --show-toplevel 2>/dev/null) || { echo '@@NOREPO'; exit 0; }; " +
                   "echo '@@TOP'; echo \"$top\"; echo '@@PREFIX'; git rev-parse --show-prefix; " +
                   "echo '@@BRANCH'; git symbolic-ref --short -q HEAD || echo \"(detached at $(git rev-parse --short HEAD 2>/dev/null))\"; " +
                   "base=HEAD; label=HEAD; git rev-parse -q --verify HEAD >/dev/null || { base=" + EmptyTree + "; label='no commits yet'; }; " +
                   "bb=" + bb + "; if [ -n \"$bb\" ] && mb=$(git merge-base HEAD \"$bb\" 2>/dev/null); then base=$mb; label=\"$bb\"; fi; " +
                   "echo '@@BASE'; echo \"$base\"; echo \"$label\"; " +
                   "echo '@@STATUS'; git -c core.quotepath=off status --porcelain=v1 --untracked-files=all --no-renames -- .; " +
                   "echo '@@NUMSTAT'; git -c core.quotepath=off diff --no-ext-diff --no-relative --numstat --no-renames \"$base\" -- .; " +
                   "echo '@@NAMES'; git -c core.quotepath=off diff --no-ext-diff --no-relative --name-status --no-renames \"$base\" -- .; " +
                   "echo '@@END'";
        }

        public static string Q(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        public const int MaxStatus = 500; // a folder with thousands of untracked files (no .gitignore): the list shows 500, the summary counts all

        public static GitSnapshot ParseScript(string output)
        {
            var g = new GitSnapshot();
            string section = null;
            var baseLines = new List<string>();
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw;
                if (line.StartsWith("@@") && (line == "@@ERR" || line == "@@NOREPO" || line == "@@TOP" || line == "@@PREFIX" || line == "@@BRANCH" || line == "@@BASE" || line == "@@STATUS" || line == "@@NUMSTAT" || line == "@@NAMES" || line == "@@END"))
                {
                    section = line;
                    if (line == "@@TOP") g.Repo = true;
                    if (line == "@@ERR") g.Error = "";
                    continue;
                }
                switch (section)
                {
                    case "@@ERR": if (line.Trim().Length > 0) g.Error = (g.Error.Length > 0 ? g.Error + " " : "") + line.Trim(); break;
                    case "@@TOP": if (line.Length > 0) g.Top = line.Trim(); break;
                    case "@@PREFIX": if (line.Length > 0) g.Prefix = line.Trim(); break;
                    case "@@BRANCH": if (line.Length > 0) g.Branch = line.Trim(); break;
                    case "@@BASE": if (line.Length > 0) baseLines.Add(line.Trim()); break;
                    case "@@STATUS":
                        // at most MaxStatus entries (a folder with thousands of untracked files, e.g. node_modules
                        // without a .gitignore, made every refresh walk them all on the main thread); StatusCut says so
                        if (line.Length > 3 && line[2] == ' ') { if (g.Status.Count < MaxStatus) g.Status.Add((line.Substring(0, 2), Unquote(line.Substring(3)))); else g.StatusCut++; }
                        break;
                    case "@@NUMSTAT":
                    {
                        var f = line.Split('\t');
                        if (f.Length < 3) break;
                        bool bin = f[0] == "-" && f[1] == "-";
                        int.TryParse(f[0], out int a);
                        int.TryParse(f[1], out int r);
                        g.Numstat[Unquote(string.Join("\t", f.Skip(2)))] = (a, r, bin);
                        break;
                    }
                    case "@@NAMES":
                    {
                        var f = line.Split('\t');
                        if (f.Length < 2 || f[0].Length == 0) break;
                        g.NameStatus[Unquote(f[1])] = f[0].Substring(0, 1);
                        break;
                    }
                }
            }
            if (g.Error != null) { g.Repo = false; if (g.Error.Length == 0) g.Error = "git failed"; }
            if (baseLines.Count > 0) g.Base = baseLines[0];
            if (baseLines.Count > 1) g.BaseLabel = baseLines[1];
            return g;
        }

        /// git's C-style quoting of odd file names ("a\tb.txt") undone.
        public static string Unquote(string p)
        {
            p = p ?? "";
            if (p.Length < 2 || p[0] != '"' || p[p.Length - 1] != '"') return p;
            var sb = new StringBuilder();
            var bytes = new List<byte>();
            void Flush() { if (bytes.Count > 0) { sb.Append(Encoding.UTF8.GetString(bytes.ToArray())); bytes.Clear(); } }
            for (int i = 1; i < p.Length - 1; i++)
            {
                char c = p[i];
                if (c != '\\' || i + 1 >= p.Length - 1) { Flush(); sb.Append(c); continue; }
                char e = p[++i];
                if (e >= '0' && e <= '7' && i + 2 < p.Length - 1) { bytes.Add((byte)Convert.ToInt32(p.Substring(i, 3), 8)); i += 2; continue; }
                Flush();
                sb.Append(e == 't' ? '\t' : e == 'n' ? '\n' : e == 'r' ? '\r' : e);
            }
            Flush();
            return sb.ToString();
        }

        /// A path as the sidebar shows it: relative to the project folder.
        public static string Rel(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            path = path.Replace('\\', '/');
            string r = (root ?? "").Replace('\\', '/').TrimEnd('/');
            if (r.Length > 0 && path.StartsWith(r + "/", StringComparison.Ordinal)) return path.Substring(r.Length + 1);
            if (path.StartsWith("./")) return path.Substring(2);
            return path;
        }

        private static readonly string[] EditTools = { "write", "edit", "patch", "create", "replace", "notebook" };
        private static string S(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        private static int I(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (int)(double)t : 0;
        private static long L(JToken t) => t != null && t.Type == JTokenType.Integer ? (long)t : 0;

        /// The files the timeline shows the agent changing (file-change rows, also inside turns, and edit / write tool
        /// calls), keyed by their path relative to `root`.
        public static Dictionary<string, ChangedFile> FromTimeline(IEnumerable<JToken> rows, string root)
        {
            var found = new Dictionary<string, ChangedFile>();
            void Touch(string raw, string kind, int a, int r, string diff, long seq)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                string path = Rel(root, raw.Trim());
                if (!found.TryGetValue(path, out var f)) { f = new ChangedFile { Path = path, ByThread = true, Kind = kind }; found[path] = f; }
                else if (kind == "delete") f.Kind = "delete";
                else if (f.Kind == "delete") f.Kind = "modify"; // deleted, then written again (an add stays an add)
                f.Added += a;
                f.Removed += r;
                if (!string.IsNullOrEmpty(diff)) f.TimelineDiff = diff;
                f.Seq = Math.Max(f.Seq, seq);
            }
            void Walk(JToken t)
            {
                if (!(t is JObject r)) return;
                string kind = S(r["kind"]);
                if (kind == "turn") { foreach (var ch in r["children"] as JArray ?? new JArray()) Walk(ch); return; }
                if (kind != "work") return;
                string wk = S(r["workKind"]);
                long seq = L(r["sourceSeqEnd"]);
                if (wk == "file-change")
                {
                    var ch = r["change"] as JObject;
                    if (ch == null) return;
                    string k = Kind(S(ch["kind"]));
                    Touch(S(ch["path"]), k, I(ch["diffStats"]?["added"]), I(ch["diffStats"]?["removed"]), S(ch["diff"]), seq);
                    if (S(ch["movePath"]) is string moved && moved.Length > 0) Touch(moved, "add", 0, 0, null, seq);
                }
                else if (wk == "tool")
                {
                    string name = (S(r["toolName"]) ?? "").ToLowerInvariant();
                    if (!EditTools.Any(name.Contains)) return;
                    var args = r["toolArgs"] as JObject;
                    string p = S(args?["file_path"]) ?? S(args?["filePath"]) ?? S(args?["path"]) ?? S(args?["notebook_path"]);
                    Touch(p, "modify", 0, 0, null, seq);
                }
            }
            foreach (var r in rows ?? new JArray()) Walk(r);
            return found;
        }

        public static string Kind(string k)
        {
            switch ((k ?? "").ToLowerInvariant())
            {
                case "add": case "added": case "create": case "created": case "new": return "add";
                case "delete": case "deleted": case "remove": case "removed": return "delete";
                default: return "modify";
            }
        }

        /// Git's view merged with the timeline's. `ownFolder`: the folder is the thread's own (a worktree), so every
        /// change in it is the thread's. Untracked files get their line counts from `count` (lines, binary, size).
        public static List<ChangedFile> Merge(GitSnapshot g, Dictionary<string, ChangedFile> timeline, bool ownFolder, Func<string, (int lines, bool binary, long size)> count = null)
        {
            var byPath = new Dictionary<string, ChangedFile>();
            timeline = timeline ?? new Dictionary<string, ChangedFile>();
            if (g != null && g.Repo)
            {
                string pre = g.Prefix ?? "";
                string Shown(string repoPath) => pre.Length > 0 && repoPath.StartsWith(pre, StringComparison.Ordinal) ? repoPath.Substring(pre.Length) : repoPath;
                foreach (var (code, rp) in g.Status)
                {
                    string path = Shown(rp);
                    var f = new ChangedFile { Path = path, RepoPath = rp, GitCode = code.Trim(), InGit = true };
                    f.Kind = code == "??" ? "untracked" : code.Contains("D") ? "delete" : code.Contains("A") ? "add" : "modify";
                    if (g.Numstat.TryGetValue(rp, out var ns)) { f.Added = ns.a; f.Removed = ns.r; f.Binary = ns.bin; }
                    if (g.NameStatus.TryGetValue(rp, out var nk) && f.Kind == "modify") f.Kind = nk == "A" ? "add" : nk == "D" ? "delete" : "modify";
                    if (f.Kind == "untracked" && count != null) { var c = count(rp); f.Added = c.lines; f.Binary = c.binary; f.Size = c.size; }
                    byPath[path] = f;
                }
                // changed since the base and committed already (a worktree's own commits)
                foreach (var kv in g.NameStatus)
                {
                    string path = Shown(kv.Key);
                    if (byPath.ContainsKey(path)) continue;
                    var f = new ChangedFile { Path = path, RepoPath = kv.Key, Committed = true, Kind = kv.Value == "A" ? "add" : kv.Value == "D" ? "delete" : "modify" };
                    if (g.Numstat.TryGetValue(kv.Key, out var ns)) { f.Added = ns.a; f.Removed = ns.r; f.Binary = ns.bin; }
                    byPath[path] = f;
                }
                foreach (var f in byPath.Values)
                {
                    if (timeline.TryGetValue(f.Path, out var t)) { f.ByThread = true; f.Seq = t.Seq; f.TimelineDiff = t.TimelineDiff; }
                    else if (ownFolder) f.ByThread = true;
                }
                // the timeline's files git no longer sees as changed (committed before the base, or edited back)
                foreach (var t in timeline.Values)
                    if (!byPath.ContainsKey(t.Path))
                        byPath[t.Path] = new ChangedFile { Path = t.Path, RepoPath = pre + t.Path, ByThread = true, Kind = t.Kind == "delete" ? "delete" : "modify", Seq = t.Seq, TimelineDiff = t.TimelineDiff, Committed = true, Added = t.Added, Removed = t.Removed };
            }
            else
                foreach (var t in timeline.Values) byPath[t.Path] = t;
            // the thread's files first, then the folder's other changes; each by folder and name
            return byPath.Values.OrderBy(f => f.ByThread ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// The git arguments for one file's diff, with the whole file as context (the viewer folds unchanged runs) unless
        /// the file is big. Untracked files diff against nothing.
        public static string DiffArgs(ChangedFile f, string baseRef, bool big)
        {
            string ctx = big ? "-U3" : "-U100000";
            if (f.Kind == "untracked") return "diff --no-color --no-ext-diff --no-index " + ctx + " -- /dev/null " + Q(f.RepoPath);
            return "diff --no-color --no-ext-diff --no-relative --no-renames " + ctx + " " + Q(string.IsNullOrEmpty(baseRef) ? "HEAD" : baseRef) + " -- " + Q(f.RepoPath);
        }

        /// One line for the CHANGES header: "5 files · +120 −12 · against HEAD" (outside git: "from the agent's edits").
        public static string Summary(List<ChangedFile> files, GitSnapshot g)
        {
            int mine = files.Count(f => f.ByThread), others = files.Count - mine + (g?.StatusCut ?? 0);
            int a = files.Where(f => f.ByThread).Sum(f => f.Added), r = files.Where(f => f.ByThread).Sum(f => f.Removed);
            string where = g != null && g.Repo ? (g.BaseLabel == "HEAD" ? "against HEAD" : g.BaseLabel == "no commits yet" ? "no commits yet" : "since " + g.BaseLabel) : "from the agent's edits (not a git repo)";
            return $"{mine} file{(mine == 1 ? "" : "s")} · +{a} −{r} · {where}" + (others > 0 ? $" · {others} other change{(others == 1 ? "" : "s")} in this folder" : "");
        }
    }
}
