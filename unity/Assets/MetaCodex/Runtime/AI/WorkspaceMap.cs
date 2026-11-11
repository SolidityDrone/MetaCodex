using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    // Workspaces (owner 2026-11-11, docs/workspaces.md): which folder each thread works in. A project's checkout is
    // one folder that several threads can share (and then edit the same files); a worktree is a folder of its own on
    // its own branch. Built from MQH's thread rows and environments; pure (no Unity objects): tested without the app.

    /// One folder threads work in: the project's checkout, or a worktree.
    public sealed class WorkspaceGroup
    {
        public string Key = "", ProjectId = "", EnvironmentId, Path = "", Branch = "", Base = "";
        public bool IsWorktree, Starting;
        public readonly List<JObject> Threads = new List<JObject>();
        public int Working, Waiting, NeedsYou;
        public long LastActive;
        /// Two agents editing the same files right now (the workspace guard holds the second one; an override or an
        /// older MQH lets it through).
        public bool Clash => Working >= 2;
        public string Kind => Starting ? "starting" : IsWorktree ? "worktree" : "checkout";
        public string Label => Starting ? "◌ setting up" : IsWorktree ? "⎇ " + (Branch.Length > 0 ? Branch : "worktree") : "▣ checkout" + (Branch.Length > 0 ? " · " + Branch : "");
    }

    /// What `git status --porcelain=v2 --branch` says about a folder.
    public sealed class GitFolderStatus
    {
        public string Branch = "", Upstream = "";
        public int Ahead, Behind, Changed, Untracked, Conflicts;
        public bool Clean => Changed == 0 && Untracked == 0 && Conflicts == 0;
    }

    public static class WorkspaceMap
    {
        private static string S(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        private static string Status(JObject t) => S(ThreadTimeline.G(ThreadTimeline.G(t, "runtime"), "displayStatus")) ?? S(t?["status"]) ?? "";
        public static bool IsWorking(JObject t) => ThreadTimeline.IsWorking(Status(t));
        public static string Norm(string path) { var p = (path ?? "").Replace('\\', '/').TrimEnd('/'); return p.Length == 0 && !string.IsNullOrEmpty(path) ? "/" : p; }
        private static long Updated(JObject t) => t?["updatedAt"]?.Type == JTokenType.Integer ? (long)t["updatedAt"] : 0;

        /// Each project's workspaces: its checkout first, then the worktrees newest first, then threads still being
        /// set up; the threads in each newest first. Environments without threads (a worktree left behind) are listed
        /// too, so they can be cleaned up.
        public static Dictionary<string, List<WorkspaceGroup>> Build(JArray threads, JArray environments = null)
        {
            var envs = new Dictionary<string, JObject>();
            foreach (var e in (environments ?? new JArray()).OfType<JObject>()) { string id = S(e["id"]); if (id != null) envs[id] = e; }
            var groups = new Dictionary<string, WorkspaceGroup>();
            WorkspaceGroup GroupFor(string projectId, string key)
            {
                string k = projectId + "|" + key;
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new WorkspaceGroup { Key = key, ProjectId = projectId };
                return g;
            }
            void Describe(WorkspaceGroup g, JObject env, JObject thread)
            {
                if (env != null)
                {
                    g.EnvironmentId = g.EnvironmentId ?? S(env["id"]);
                    if (g.Path.Length == 0) g.Path = S(env["path"]) ?? "";
                    if (g.Branch.Length == 0) g.Branch = S(env["branchName"]) ?? "";
                    if (g.Base.Length == 0) g.Base = S(env["mergeBaseBranch"]) ?? S(env["baseBranch"]) ?? S(env["defaultBranch"]) ?? "";
                }
                if (thread != null)
                {
                    if (g.Path.Length == 0) g.Path = S(thread["environmentPath"]) ?? "";
                    if (g.Branch.Length == 0) g.Branch = S(thread["environmentBranchName"]) ?? "";
                }
            }
            string KeyFor(JObject env, JObject thread, out bool worktree, out bool starting)
            {
                starting = false;
                worktree = env != null ? env["isWorktree"]?.Type == JTokenType.Boolean && (bool)env["isWorktree"] : thread?["environmentIsWorktree"]?.Type == JTokenType.Boolean && (bool)thread["environmentIsWorktree"];
                string envId = S(env?["id"]) ?? S(thread?["environmentId"]);
                string path = S(env?["path"]) ?? S(thread?["environmentPath"]);
                // worktrees are their own folder each; a checkout is the folder, whichever environment row names it
                if (worktree) return envId != null ? "env:" + envId : "wt:" + (S(thread?["environmentBranchName"]) ?? path ?? "?");
                if (!string.IsNullOrEmpty(path)) return "dir:" + Norm(path);
                if (envId != null) return "env:" + envId;
                // no folder yet: a thread being set up; one that is past that (data without it) counts as the checkout
                string st = thread == null ? "" : Status(thread);
                if (st == "pending" || st == "provisioning" || st == "starting") { starting = true; return "starting"; }
                return "checkout";
            }
            foreach (var t in (threads ?? new JArray()).OfType<JObject>())
            {
                string pid = S(t["projectId"]) ?? "";
                string envId = S(t["environmentId"]);
                envs.TryGetValue(envId ?? "", out var env);
                string key = KeyFor(env, t, out bool wt, out bool starting);
                var g = GroupFor(pid, key);
                g.IsWorktree = wt;
                g.Starting = starting;
                Describe(g, env, t);
                g.Threads.Add(t);
                if (IsWorking(t)) g.Working++;
                if (S(t["queuedWork"]) == "waiting") g.Waiting++;
                if (t["hasPendingInteraction"]?.Type == JTokenType.Boolean && (bool)t["hasPendingInteraction"]) g.NeedsYou++;
                g.LastActive = Math.Max(g.LastActive, Updated(t));
            }
            foreach (var env in envs.Values)
            {
                string phase = S(ThreadTimeline.G(env["lifecycle"], "phase"));
                if (phase != null && phase != "active") continue;
                string pid = S(env["projectId"]) ?? "";
                string key = KeyFor(env, null, out bool wt, out _);
                if (!wt) continue; // a checkout with no thread is just the project folder
                var g = GroupFor(pid, key);
                g.IsWorktree = true;
                Describe(g, env, null);
                g.LastActive = Math.Max(g.LastActive, env["updatedAt"]?.Type == JTokenType.Integer ? (long)env["updatedAt"] : 0);
            }
            var result = new Dictionary<string, List<WorkspaceGroup>>();
            foreach (var g in groups.Values)
            {
                g.Threads.Sort((a, b) => Updated(b).CompareTo(Updated(a)));
                if (!result.TryGetValue(g.ProjectId, out var list)) result[g.ProjectId] = list = new List<WorkspaceGroup>();
                list.Add(g);
            }
            foreach (var list in result.Values)
                list.Sort((a, b) => a.Starting != b.Starting ? (a.Starting ? 1 : -1) : a.IsWorktree != b.IsWorktree ? (a.IsWorktree ? 1 : -1) : b.LastActive.CompareTo(a.LastActive));
            return result;
        }

        /// The group a thread is in.
        public static WorkspaceGroup GroupOf(Dictionary<string, List<WorkspaceGroup>> map, string threadId) =>
            map.Values.SelectMany(l => l).FirstOrDefault(g => g.Threads.Any(t => S(t["id"]) == threadId));

        /// The threads working in the project's checkout now (a new thread there would share their files).
        public static List<JObject> CheckoutBusy(Dictionary<string, List<WorkspaceGroup>> map, string projectId, string exceptThreadId = null) =>
            map.TryGetValue(projectId ?? "", out var list)
                ? list.Where(g => !g.IsWorktree && !g.Starting).SelectMany(g => g.Threads).Where(t => IsWorking(t) && S(t["id"]) != exceptThreadId).ToList()
                : new List<JObject>();

        public static string TitleOf(JObject t) => S(t?["title"]) ?? S(t?["titleFallback"]) ?? S(t?["id"]) ?? "thread";

        /// One line on where a thread works and who else does (THREAD, the session context).
        public static string Describe(WorkspaceGroup g, string threadId)
        {
            if (g == null) return "";
            if (g.Starting) return "its workspace is being set up";
            var others = g.Threads.Where(t => S(t["id"]) != threadId).ToList();
            var busy = others.Where(IsWorking).ToList();
            string where = g.IsWorktree ? $"in its worktree {g.Branch}" + (g.Base.Length > 0 ? $" (from {g.Base})" : "") : "in the project checkout" + (g.Branch.Length > 0 ? $" ({g.Branch})" : "");
            if (busy.Count > 0) return where + $", shared with {Quote(busy)} working there now";
            if (others.Count > 0) return where + $", shared with {others.Count} other {(others.Count == 1 ? "thread" : "threads")} (idle)";
            return where + (g.IsWorktree ? ": its own folder and branch" : "");
        }

        private static string Quote(List<JObject> ts) => string.Join(", ", ts.Take(2).Select(t => "“" + ThreadPulse.Clip(TitleOf(t), 40) + "”")) + (ts.Count > 2 ? $" and {ts.Count - 2} more" : "");

        // ---- git, read in the userland

        /// `git status --porcelain=v2 --branch`.
        public static GitFolderStatus ParseStatus(string output)
        {
            var st = new GitFolderStatus();
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                string l = raw.TrimEnd();
                if (l.StartsWith("# branch.head ")) st.Branch = l.Substring(14).Trim();
                else if (l.StartsWith("# branch.upstream ")) st.Upstream = l.Substring(18).Trim();
                else if (l.StartsWith("# branch.ab "))
                {
                    var m = Regex.Match(l, @"\+(\d+)\s+-(\d+)");
                    if (m.Success) { st.Ahead = int.Parse(m.Groups[1].Value); st.Behind = int.Parse(m.Groups[2].Value); }
                }
                else if (l.StartsWith("1 ") || l.StartsWith("2 ")) st.Changed++;
                else if (l.StartsWith("u ")) st.Conflicts++;
                else if (l.StartsWith("? ")) st.Untracked++;
            }
            return st;
        }

        /// `git rev-list --left-right --count BASE...HEAD`: (behind, ahead) of HEAD against BASE.
        public static (int behind, int ahead) ParseLeftRight(string output)
        {
            var m = Regex.Match(output ?? "", @"(\d+)\s+(\d+)");
            return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
        }

        /// `git merge-tree --write-tree --name-only --no-messages BASE BRANCH`: exit 0 = merges cleanly; 1 = conflicts,
        /// one file per line after the tree id. Anything else is an error (an old git, an unknown branch).
        public static (bool ok, bool clean, List<string> conflicts, string error) ParseMergeTree(int exitCode, string output)
        {
            var lines = (output ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            bool tree = lines.Count > 0 && Regex.IsMatch(lines[0], "^[0-9a-f]{40,64}$");
            if (exitCode == 0 && tree) return (true, true, new List<string>(), null);
            if (exitCode == 1 && tree) return (true, false, lines.Skip(1).Distinct().ToList(), null);
            return (false, false, new List<string>(), lines.Count > 0 ? ThreadPulse.Clip(string.Join(" ", lines), 160) : "git merge-tree failed (" + exitCode + ")");
        }
    }
}
