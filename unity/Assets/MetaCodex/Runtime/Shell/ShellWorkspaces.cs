using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// WORKSPACES (owner 2026-11-11, docs/workspaces.md): which folder every thread works in, and who shares it. A
    /// project's checkout is one folder several threads can share (and then edit the same files); a worktree is a
    /// folder of its own on its own branch. MQH holds a turn while another thread works in the same folder (its
    /// workspace guard); here the user sees the groups, the clashes and what waits, and manages the worktrees: their
    /// git state against their base, a merge back with a conflict preview first, removing one, a new thread in one.
    public sealed partial class MetaCodexShell
    {
        private UiPanel _wsp;
        private UiList _wspList;
        private UiLabel _wspTitle, _wspDetail, _wspGitLine, _wspNote, _wspStatus;
        private UiButton _wspMerge, _wspRemove, _wspNewHere;
        private JArray _environments = new JArray();
        private Dictionary<string, List<WorkspaceGroup>> _spaces = new Dictionary<string, List<WorkspaceGroup>>();
        private string _wspSelKey;           // projectId|key
        private float _nextWspRefresh;
        private int _wspGitAsk;              // the newest git read (older answers are dropped)
        private (string id, string label) _ntReuseEnv;  // NEW THREAD HERE: that workspace
        private bool _ntForcedWorktree;      // NEW THREAD: WORKTREE chosen because the checkout is busy

        public IReadOnlyDictionary<string, List<WorkspaceGroup>> WorkspaceMapForTest => _spaces;

        // ------------------------------------------------------------------ the window

        private void BuildWorkspaces()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("workspaces", "WORKSPACES · WORKTREES", 0.80f, 0.60f);
            _wsp = p;
            var c = p.Content;
            float leftW = 0.33f;
            _wspStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, c.y, c.width, 0.02f), Color = t.TextDim });
            var body = new Rect(c.x, c.y + 0.02f + t.Gap, c.width, c.height - 0.02f - t.Gap);
            _wspList = p.Add(new UiList(0.034f, (item, idx) => { if (item.Tag is string tag && tag.StartsWith("w:")) { _wspSelKey = tag.Substring(2); ShowWorkspaceDetail(); } }) { Rect = new Rect(body.x, body.y, leftW, body.height), EmptyText = "No workspaces yet.\nThreads show up here with the folder they work in." });
            var r = new UiStack(new Rect(body.x + leftW + t.Gap * 2, body.y, body.width - leftW - t.Gap * 2, body.height), t.Gap);
            _wspTitle = p.Add(new UiLabel("", t.BodySize * 1.05f) { Rect = r.Next(0.03f) });
            _wspDetail = p.Add(new UiLabel("", t.SmallSize) { Rect = r.Next(0.17f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            _wspGitLine = p.Add(new UiLabel("", t.SmallSize) { Rect = r.Next(0.05f), Wrap = true, Align = TextAlignmentOptions.TopLeft, UseMono = true, Color = t.TextAccent });
            AddButtons(p, r.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("OPEN THREAD", () => { var g = SelectedWorkspace(); var th = g?.Threads.FirstOrDefault(); if (th != null) OpenThread((string)th["id"]); else Toast("No thread works here", "warn"); }),
                ("FILES", () => { var g = SelectedWorkspace(); if (g == null || g.Path.Length == 0) return; _filesPath = g.Path; OpenPanel("files"); FilesNavigate(g.Path); }),
                ("TERMINAL HERE", () => { var g = SelectedWorkspace(); if (g != null && g.Path.Length > 0) RunInNewTerminal("cd " + GitService.Quote(g.Path) + " && git status -sb"); }),
                ("⟳", () => RefreshWorkspaces()),
            });
            var row2 = r.Next(t.ButtonHeight);
            float bw = (row2.width - t.Gap * 2) / 3f;
            _wspMerge = p.Add(new UiButton("MERGE…", MergeSelectedWorkspace, UiButtonStyle.Primary) { Rect = new Rect(row2.x, row2.y, bw, row2.height) });
            _wspNewHere = p.Add(new UiButton("NEW THREAD HERE…", NewThreadInSelectedWorkspace) { Rect = new Rect(row2.x + bw + t.Gap, row2.y, bw, row2.height) });
            _wspRemove = p.Add(new UiButton("REMOVE…", RemoveSelectedWorkspace, UiButtonStyle.Danger) { Rect = new Rect(row2.x + (bw + t.Gap) * 2, row2.y, bw, row2.height) });
            _wspNote = p.Add(new UiLabel("", t.SmallSize) { Rect = r.Rest(), Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            p.Tick += _ => { if (Time.unscaledTime >= _nextWspRefresh) RefreshWorkspaces(); };
        }

        /// THREADS' rows, the window, NEW THREAD and THREAD read the same map; asked again every few seconds while the
        /// window is open.
        public void RefreshWorkspaces()
        {
            _nextWspRefresh = Time.unscaledTime + 6f;
            if (Mqh == null) return;
            Mqh.Threads(null, th =>
            {
                if (th is JArray a) _threads = a;
                Mqh.Environments(null, ev =>
                {
                    if (ev is JArray e) _environments = e;
                    RebuildWorkspaceMap();
                });
            });
        }

        private void RebuildWorkspaceMap()
        {
            _spaces = WorkspaceMap.Build(_threads, _environments);
            RenderWorkspaceList();
            if (_threadList != null && IsPanelOpen("threads")) RenderThreadGroups();
            foreach (var w in Tws.Where(WinOpen).ToList()) using (In(w)) UpdateWorkspaceChip(); // every open thread window's chip
            if (!Tws.Any(WinOpen)) UpdateWorkspaceChip();
        }

        public void SetWorkspacesForTest(JArray projects, JArray threads, JArray environments)
        {
            if (projects != null) _projects = projects;
            _threads = threads ?? new JArray();
            _environments = environments ?? new JArray();
            RebuildWorkspaceMap();
        }

        private WorkspaceGroup SelectedWorkspace()
        {
            if (_wspSelKey == null) return null;
            int bar = _wspSelKey.IndexOf('|');
            if (bar < 0) return null;
            string pid = _wspSelKey.Substring(0, bar), key = _wspSelKey.Substring(bar + 1);
            return _spaces.TryGetValue(pid, out var l) ? l.FirstOrDefault(g => g.Key == key) : null;
        }

        /// Opens the window on one workspace (a row of THREADS, voice).
        public void OpenWorkspacesOn(string projectAndKey)
        {
            _wspSelKey = projectAndKey;
            OpenPanel("workspaces");
            RenderWorkspaceList();
        }

        private void RenderWorkspaceList()
        {
            if (_wspList == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            int shared = 0, worktrees = 0, clashes = 0;
            foreach (var pr in _projects)
            {
                string pid = (string)pr["id"];
                if (!_spaces.TryGetValue(pid, out var groups) || groups.Count == 0) continue;
                items.Add(new UiListItem { Icon = "▾", IconColor = t.TextDim, Title = ((string)pr["name"] ?? "project").ToUpperInvariant(), TitleColor = t.TextDim, Tag = "p:" + pid });
                foreach (var g in groups)
                {
                    if (g.IsWorktree) worktrees++;
                    if (!g.IsWorktree && g.Threads.Count > 1) shared++;
                    if (g.Clash) clashes++;
                    items.Add(new UiListItem
                    {
                        Indent = 1,
                        Icon = g.Starting ? "◌" : g.IsWorktree ? "⎇" : "▣",
                        IconColor = g.Clash ? t.Danger : g.Working > 0 ? t.Ok : t.TextAccent,
                        Title = g.Starting ? "setting up" : g.IsWorktree ? (g.Branch.Length > 0 ? g.Branch : "worktree") : "checkout" + (g.Branch.Length > 0 ? " · " + g.Branch : ""),
                        Sub = g.Threads.Count == 0 ? "no thread" : ThreadPulse.Plural(g.Threads.Count, "thread"),
                        Badge = g.Clash ? g.Working + " WORKING ⚠" : g.Waiting > 0 ? g.Waiting + " WAITING" : g.Working > 0 ? g.Working + " WORKING" : "",
                        BadgeColor = g.Clash ? t.Danger : g.Waiting > 0 ? t.Warn : t.Ok,
                        Tag = "w:" + pid + "|" + g.Key,
                    });
                }
            }
            _wspList.SetItems(items, keepScroll: true);
            if (_wspSelKey == null || SelectedWorkspace() == null) _wspSelKey = items.Select(i => i.Tag as string).FirstOrDefault(x => x != null && x.StartsWith("w:"))?.Substring(2);
            int sel = items.FindIndex(i => i.Tag as string == "w:" + _wspSelKey);
            if (sel >= 0) _wspList.SelectIndex(sel, notify: false);
            _wspStatus?.Set(items.Count == 0 ? "No workspaces yet: threads show up here with the folder they work in." :
                $"{ThreadPulse.Plural(worktrees, "worktree")} · {ThreadPulse.Plural(shared, "shared checkout")}" + (clashes > 0 ? $" · <color=#{UiViz.Hex(t.Danger)}>{clashes} with two agents at work ⚠</color>" : "") + " · MQH holds a turn while another thread works in the same folder", t.TextDim);
            ShowWorkspaceDetail();
        }

        private string ProjectCheckoutPath(string projectId) => (string)_projects.FirstOrDefault(x => (string)x["id"] == projectId)?["sources"]?.FirstOrDefault()?["path"];
        private string ProjectName(string projectId) => (string)_projects.FirstOrDefault(x => (string)x["id"] == projectId)?["name"] ?? "project";
        private static string BaseLocal(string b) => b != null && b.StartsWith("origin/") ? b.Substring(7) : b ?? "";

        private void ShowWorkspaceDetail()
        {
            if (_wspTitle == null) return;
            var t = UiTheme.Current;
            var g = SelectedWorkspace();
            if (g == null)
            {
                _wspTitle.Set("");
                _wspDetail.Set(_wspList != null && _wspList.Items.Count == 0 ? "" : "Pick a workspace on the left.", t.TextDim); // an empty list says why itself
                _wspGitLine.Set("");
                _wspNote.Set("");
                foreach (var b in new[] { _wspMerge, _wspRemove, _wspNewHere }) b?.SetEnabled(false);
                return;
            }
            string E(string s) => UiChatView.Esc(s);
            _wspTitle.Set($"<b>{E(g.Label)}</b>  <color=#{UiViz.Hex(t.TextDim)}>{g.Kind} of {E(ProjectName(g.ProjectId))}</color>");
            var lines = new List<string>();
            if (g.Path.Length > 0) lines.Add($"<color=#7FA9BF>folder</color> {E(g.Path)}");
            if (g.IsWorktree && g.Base.Length > 0) lines.Add($"<color=#7FA9BF>from</color> {E(g.Base)}  ·  its own branch: what it changes stays here until it is merged");
            if (!g.IsWorktree && !g.Starting) lines.Add("<color=#7FA9BF>the project's own folder</color>: every thread here edits the same files");
            if (g.Threads.Count == 0) lines.Add("<color=#7FA9BF>threads</color> none (left from archived threads: REMOVE cleans it up)");
            foreach (var th in g.Threads.Take(5))
            {
                bool working = WorkspaceMap.IsWorking(th), waiting = (string)th["queuedWork"] == "waiting";
                string mark = working ? $"<color=#{UiViz.Hex(t.Ok)}>▶ working</color>" : waiting ? $"<color=#{UiViz.Hex(t.Warn)}>⏸ waiting</color>" : "<color=#7FA9BF>◆ idle</color>";
                lines.Add($"{mark}  “{E(ThreadPulse.Clip(WorkspaceMap.TitleOf(th), 46))}” <color=#7FA9BF>{E((string)th["providerId"] ?? "")}</color>");
            }
            if (g.Threads.Count > 5) lines.Add($"<color=#7FA9BF>… {g.Threads.Count - 5} more</color>");
            _wspDetail.Set(string.Join("\n", lines), t.Text);
            // what to watch: two at work in one folder, a turn held, nothing to merge yet
            string note = g.Clash ? $"<color=#{UiViz.Hex(t.Danger)}>⚠ {g.Working} threads are working in this folder at once: they edit the same files. The second one was sent anyway (SEND NOW) or MQH's workspace guard is off (mqh concurrency-limit workspace on).</color>"
                : g.Waiting > 0 ? $"<color=#{UiViz.Hex(t.Warn)}>⏸ {(g.Waiting == 1 ? "1 thread waits" : g.Waiting + " threads wait")} for the folder: the turn starts when the working one is done (or SEND NOW in the thread shares it).</color>"
                : g.IsWorktree ? "MERGE brings this branch into " + (g.Base.Length > 0 ? BaseLocal(g.Base) : "its base") + " in the project checkout, after a conflict check. REMOVE archives its threads and deletes the folder; the branch stays in git."
                : "A new thread that should not touch these files: NEW THREAD > WORKTREE gives it a folder and a branch of its own.";
            _wspNote.Set(note, t.TextDim);
            _wspMerge?.SetEnabled(g.IsWorktree && g.Branch.Length > 0 && g.Base.Length > 0);
            _wspMerge?.SetText(g.IsWorktree && g.Base.Length > 0 ? "MERGE INTO " + BaseLocal(g.Base).ToUpperInvariant() + "…" : "MERGE…");
            _wspRemove?.SetEnabled(g.IsWorktree && g.EnvironmentId != null && g.Working == 0);
            _wspNewHere?.SetEnabled(g.EnvironmentId != null);
            ReadWorkspaceGit(g);
        }

        /// Its git state, read in the userland: branch, changes, ahead / behind its base, the last commit.
        private void ReadWorkspaceGit(WorkspaceGroup g)
        {
            if (_wspGitLine == null) return;
            int ask = ++_wspGitAsk;
            if (Git == null || g.Path.Length == 0 || !Application.isPlaying && !Git.InUserland) { _wspGitLine.Set(g.Path.Length == 0 ? "" : "git: read when the window is open on the headset"); return; }
            _wspGitLine.Set("git: reading …");
            string path = g.Path, baseRef = g.Base;
            System.Threading.Tasks.Task.Run(async () =>
            {
                var st = await Git.Git(path, "status --porcelain=v2 --branch", 8000);
                (int behind, int ahead) ab = (0, 0);
                bool hasAb = false;
                if (baseRef.Length > 0)
                {
                    var lr = await Git.Git(path, "rev-list --left-right --count " + GitService.Quote(baseRef) + "...HEAD", 8000);
                    if (lr.code == 0) { ab = WorkspaceMap.ParseLeftRight(lr.output); hasAb = true; }
                }
                var last = await Git.Git(path, "log -1 --format=%h%x09%cr%x09%s", 8000);
                _main.Enqueue(() =>
                {
                    if (ask != _wspGitAsk || _wspGitLine == null) return;
                    if (st.code != 0) { _wspGitLine.Set("git: " + ThreadPulse.Clip(st.output, 120)); return; }
                    var s = WorkspaceMap.ParseStatus(st.output);
                    var parts = new List<string> { "⎇ " + (s.Branch.Length > 0 ? s.Branch : "?") };
                    if (hasAb) parts.Add($"{ab.ahead} ahead, {ab.behind} behind {BaseLocal(baseRef)}");
                    parts.Add(s.Clean ? "clean" : $"{s.Changed} changed" + (s.Untracked > 0 ? $", {s.Untracked} new" : "") + (s.Conflicts > 0 ? $", {s.Conflicts} in conflict" : ""));
                    var lp = (last.output ?? "").Trim().Split('\t');
                    string lastLine = last.code == 0 && lp.Length >= 3 ? $"\nlast: {lp[0]} {ThreadPulse.Clip(lp[2], 60)} ({lp[1]})" : "";
                    _wspGitLine.Set(UiChatView.Esc(string.Join(" · ", parts) + lastLine));
                });
            });
        }

        // ------------------------------------------------------------------ actions

        /// MERGE INTO BASE: in the project checkout, when it is on the base branch, clean and nobody works there; a
        /// conflict preview first (git merge-tree, nothing touched); conflicts go to the worktree's own agent.
        private void MergeSelectedWorkspace()
        {
            var g = SelectedWorkspace();
            if (g == null || !g.IsWorktree || g.Branch.Length == 0 || g.Base.Length == 0) return;
            string checkout = ProjectCheckoutPath(g.ProjectId), baseLocal = BaseLocal(g.Base), branch = g.Branch;
            if (string.IsNullOrEmpty(checkout)) { Toast("This project has no checkout folder to merge into", "warn"); return; }
            var busy = WorkspaceMap.CheckoutBusy(_spaces, g.ProjectId);
            if (busy.Count > 0) { Toast($"“{ThreadPulse.Clip(WorkspaceMap.TitleOf(busy[0]), 40)}” is working in the checkout: merge when it is done", "warn"); return; }
            if (g.Working > 0) { Toast("This worktree's thread is still working: merge when it is done", "warn"); return; }
            Toast("Checking the merge …", "info");
            System.Threading.Tasks.Task.Run(async () =>
            {
                var cs = await Git.Git(checkout, "status --porcelain=v2 --branch", 8000);
                var preview = await Git.Git(checkout, "merge-tree --write-tree --name-only --no-messages " + GitService.Quote(baseLocal) + " " + GitService.Quote(branch), 20000);
                var count = await Git.Git(checkout, "rev-list --count " + GitService.Quote(baseLocal) + ".." + GitService.Quote(branch), 8000);
                _main.Enqueue(() =>
                {
                    if (cs.code != 0) { Toast("git: " + ThreadPulse.Clip(cs.output, 100), "error"); return; }
                    var st = WorkspaceMap.ParseStatus(cs.output);
                    if (st.Branch != baseLocal) { Modal("Merge " + branch, $"The checkout is on {st.Branch}, not {baseLocal}. Switch it to {baseLocal} first (a terminal: git switch {baseLocal}), then MERGE again.", new[] { "OK" }, _ => { }); return; }
                    if (!st.Clean) { Modal("Merge " + branch, $"The checkout has changes not committed ({st.Changed} changed, {st.Untracked} new): commit or stash them first, so the merge cannot mix them in.", new[] { "OK" }, _ => { }); return; }
                    var mt = WorkspaceMap.ParseMergeTree(preview.code, preview.output);
                    if (!mt.ok) { Toast("Merge check failed: " + mt.error, "error"); return; }
                    string commits = count.code == 0 ? count.output.Trim() : "?";
                    if (!mt.clean)
                    {
                        string files = string.Join(", ", mt.conflicts.Take(6)) + (mt.conflicts.Count > 6 ? $" and {mt.conflicts.Count - 6} more" : "");
                        Modal($"{branch} conflicts with {baseLocal}", $"Conflicts in {files}: a merge would stop half way. The agent of this worktree can rebase it on {baseLocal} and resolve them; then MERGE again.", new[] { "ASK ITS AGENT", "CANCEL" }, i =>
                        {
                            if (i != 0) return;
                            var th = g.Threads.FirstOrDefault();
                            if (th == null) { Toast("No thread in this worktree: start one with NEW THREAD HERE", "warn"); return; }
                            Mqh.SendMessage((string)th["id"], $"Rebase this branch ({branch}) on {baseLocal} and resolve the conflicts in {files}. Run the tests, commit, and tell me when it merges cleanly.", r =>
                                Toast(r != null ? "Asked “" + ThreadPulse.Clip(WorkspaceMap.TitleOf(th), 40) + "” to rebase and resolve" : "Not sent: " + Mqh.LastError, r != null ? "ok" : "error"));
                        });
                        return;
                    }
                    Modal($"Merge {branch} into {baseLocal}?", $"{commits} commit{(commits == "1" ? "" : "s")} from this worktree; it merges cleanly (checked, nothing touched yet). The merge commit goes into the project checkout.", new[] { "MERGE", "CANCEL" }, i =>
                    {
                        if (i != 0) return;
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            var m = await Git.Git(checkout, "merge --no-ff --no-edit " + GitService.Quote(branch), 60000);
                            if (m.code != 0) await Git.Git(checkout, "merge --abort", 8000);
                            _main.Enqueue(() =>
                            {
                                Toast(m.code == 0 ? $"Merged {branch} into {baseLocal}" : "Not merged (stopped, nothing changed): " + ThreadPulse.Clip(m.output, 100), m.code == 0 ? "ok" : "error");
                                SessionMemory.Add("workspace", g.EnvironmentId ?? branch, m.code == 0 ? $"merged {branch} into {baseLocal}" : $"merge of {branch} failed");
                                RefreshWorkspaces();
                            });
                        });
                    });
                });
            });
        }

        /// REMOVE: archives the worktree's threads, then MQH deletes the folder (the branch stays in git).
        private void RemoveSelectedWorkspace()
        {
            var g = SelectedWorkspace();
            if (g == null || !g.IsWorktree || g.EnvironmentId == null) return;
            if (g.Working > 0) { Toast("A thread is working there: stop it first", "warn"); return; }
            string envId = g.EnvironmentId, branch = g.Branch;
            Modal("Remove the worktree " + branch + "?", $"Its {ThreadPulse.Plural(g.Threads.Count, "thread")} are archived and the folder goes. The branch {branch} stays in git, merged or not.", new[] { "REMOVE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                Mqh.ArchiveEnvironmentThreads(envId, a =>
                {
                    if (a == null) { Toast("Not removed: " + Mqh.LastError, "error"); return; }
                    Mqh.DeleteEnvironment(envId, d =>
                    {
                        Toast(d != null ? "Worktree " + branch + " removed" : "Threads archived; the folder is not removed yet: " + Mqh.LastError, d != null ? "ok" : "warn");
                        SessionMemory.Add("workspace", envId, "removed the worktree " + branch);
                        _wspSelKey = null;
                        RefreshWorkspaces();
                    });
                });
            });
        }

        /// NEW THREAD HERE: NEW THREAD set to this workspace (MQH holds its turn while another thread works there).
        private void NewThreadInSelectedWorkspace()
        {
            var g = SelectedWorkspace();
            if (g?.EnvironmentId == null) return;
            _ntReuseEnv = (g.EnvironmentId, g.Label);
            _projectSel = g.ProjectId;
            OpenNewThread(null);
            ShowNewThreadSummary();
        }

        // ------------------------------------------------------------------ NEW THREAD, THREAD

        /// NEW THREAD: a busy checkout makes WORKTREE the choice (said in the summary); CHECKOUT stays possible (its turn
        /// then waits for the folder). NEW THREAD HERE names its workspace instead.
        private string NewThreadWorkspaceNote()
        {
            var t = UiTheme.Current;
            if (_ntReuseEnv.id != null) return $"\n<color=#7FA9BF>IN</color> {UiChatView.Esc(_ntReuseEnv.label)} (a workspace that exists: its turn waits while another thread works there)";
            var busy = WorkspaceMap.CheckoutBusy(_spaces, _projectSel);
            if (busy.Count == 0) return "";
            string who = "“" + UiChatView.Esc(ThreadPulse.Clip(WorkspaceMap.TitleOf(busy[0]), 40)) + "”" + (busy.Count > 1 ? $" and {busy.Count - 1} more" : "");
            return Settings.workspaceMode == "worktree" || _ntForcedWorktree
                ? $"\n<color=#{UiViz.Hex(t.Warn)}>⚠ {who} is working in the checkout:</color> WORKTREE gives this thread its own folder and branch."
                : $"\n<color=#{UiViz.Hex(t.Warn)}>⚠ {who} is working in the checkout:</color> this thread's turn waits until it is done. WORKTREE would let it start now.";
        }

        /// When NEW THREAD opens: the checkout busy -> WORKTREE (for this thread only: the saved choice stays).
        private void ApplyNewThreadWorkspaceDefault()
        {
            _ntForcedWorktree = false;
            if (_ntReuseEnv.id != null || _ntWork == null) return;
            if (Settings.workspaceMode != "worktree" && WorkspaceMap.CheckoutBusy(_spaces, _projectSel).Count > 0)
            {
                _ntForcedWorktree = true;
                _ntWork.Set(1);
            }
        }

        /// THREAD's workspace chip: the branch, and a mark when another thread works in the same folder.
        private void UpdateWorkspaceChip()
        {
            if (_thWsChip == null || _threadOpen == null) return;
            var g = WorkspaceMap.GroupOf(_spaces, _threadOpen);
            if (g == null) return;
            bool sharedBusy = g.Threads.Any(x => (string)x["id"] != _threadOpen && WorkspaceMap.IsWorking(x));
            _thWsChip.SetText((sharedBusy ? "⚠ " : "") + (g.IsWorktree ? "⎇ " + ThreadPulse.Clip(g.Branch, 14) : "▣ checkout" + (g.Branch.Length > 0 ? " · " + ThreadPulse.Clip(g.Branch, 8) : "")));
            _thWsChip.SetSelected(sharedBusy);
        }

        /// The open thread's queue: a turn held by a plugin (MQH's workspace guard) shows its reason and SEND NOW.
        private void PollThreadQueue(string threadId)
        {
            if ((string)_threadInfo?["queuedWork"] != "waiting") { if (_thWait.id != null) { _thWait = default; ShowThreadStatus(); } SetSendNowVisible(false); return; }
            Mqh.QueuedMessages(threadId, Bind<JToken>(q =>
            {
                if (threadId != _threadOpen) return;
                var row = (q as JArray)?.OfType<JObject>().FirstOrDefault(x => x["waitingOn"] is JObject w && ((string)w["kind"] == "plugin" || (string)w["kind"] == "thread-busy"));
                string reason = (string)row?["waitingOn"]?["reason"] ?? ((string)row?["waitingOn"]?["kind"] == "thread-busy" ? "behind the running turn" : null);
                _thWait = row != null ? ((string)row["id"], reason ?? "waiting in the queue") : default;
                if (_threadInfo != null) _threadInfo["mcxWaitReason"] = _thWait.reason;
                SetSendNowVisible(_thWait.id != null && (string)row?["waitingOn"]?["kind"] == "plugin");
                RefreshThreadPulse();
            }));
        }


        private void SetSendNowVisible(bool on)
        {
            if (_thStatus == null) return;
            if (_thStatusFull.width <= 0f) _thStatusFull = _thStatus.Rect;
            var p = ThreadPanel; // this window's panel
            if (_thSendNow == null && on && p != null)
            {
                var r = _thStatusFull;
                _thSendNow = p.Add(new UiButton("▶ SEND NOW", Bind(SendHeldNow), UiButtonStyle.Primary) { Rect = new Rect(r.xMax - 0.11f, r.y - 0.002f, 0.11f, r.height + 0.004f), TextSize = UiTheme.Current.SmallSize * 0.95f });
            }
            _thSendNow?.SetVisible(on);
            // the status line makes room for it
            var want = on ? new Rect(_thStatusFull.x, _thStatusFull.y, _thStatusFull.width - 0.118f, _thStatusFull.height) : _thStatusFull;
            if (_thStatus.Rect != want) _thStatus.SetRect(want);
        }

        /// SEND NOW on a held turn: the user overrides the hold (both threads then work in one folder). IN A WORKTREE
        /// (owner 2026-11-14): the held message gets the worktree instruction in front (the agent makes or picks its own
        /// worktree with the worktree-per-thread skill), then it is sent now. CANCEL: it keeps waiting.
        private void SendHeldNow()
        {
            if (_threadOpen == null || _thWait.id == null) return;
            string id = _threadOpen, qid = _thWait.id;
            Modal("Start it now?", HeldModalText, new[] { "SEND NOW", "IN A WORKTREE", "CANCEL" }, Bind<int>(i =>
            {
                if (i == 1) { SendHeldInWorktree(id, qid); return; }
                if (i != 0) return;
                Mqh.SendQueuedNow(id, qid, Bind<JToken>(r =>
                {
                    Toast(r != null ? "Sent: it shares the folder now" : "Not sent: " + Mqh.LastError, r != null ? "warn" : "error");
                    _thWait = default;
                    SetSendNowVisible(false);
                    _nextThreadMeta = 0f;
                    PollThread();
                }));
            }));
        }

        public const string HeldModalText = "Another thread is working in this folder. SEND NOW: both agents edit the same files at once. " +
            "IN A WORKTREE: the agent makes or picks its own git worktree and branch and works there; the shared folder stays as it is.";

        /// What IN A WORKTREE puts in front of the held message (the skill worktree-per-thread does the rest).
        public const string WorktreeInstruction = "This project's folder is busy with another agent. Before you change any file, use the " +
            "worktree-per-thread skill: choose the worktree that fits this task, or create one, and do all edits there. Then do this task:";

        /// IN A WORKTREE: read the held row (its content and updatedAt), rewrite it with the instruction in front (PATCH,
        /// expectedUpdatedAt = the row's updatedAt), then send it now. Any failure: a toast with MQH's error, nothing sent.
        private void SendHeldInWorktree(string id, string qid)
        {
            Mqh.QueuedMessages(id, Bind<JToken>(q =>
            {
                if (q == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                var row = (q as JArray)?.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == qid);
                if (row == null) { Toast("Not sent: the held message is no longer in the queue (it may have started)", "error"); AfterHeldSent(id); return; }
                if (row["editable"]?.Type == JTokenType.Boolean && !(bool)row["editable"]) { Toast("Not sent: this queued message cannot be changed now", "error"); return; }
                var input = WorktreeInput((row["content"] ?? row["input"]) as JArray);
                if (input == null) { Toast("Not sent: the held message has no content", "error"); return; }
                long updatedAt;
                try { updatedAt = (long)Math.Round((double)row["updatedAt"]); } catch (Exception) { Toast("Not sent: the queued message has no updatedAt", "error"); return; }
                Mqh.UpdateQueuedMessage(id, qid, new JObject { ["expectedUpdatedAt"] = updatedAt, ["input"] = input }, Bind<JToken>(p =>
                {
                    if (p == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                    Mqh.SendQueuedNow(id, qid, Bind<JToken>(r =>
                    {
                        Toast(r != null ? "Sent: the agent moves to its own worktree first" : "Not sent: " + Mqh.LastError, r != null ? "info" : "error");
                        AfterHeldSent(id);
                    }));
                }));
            }));
        }

        /// After a held turn was sent (or left the queue): the window that still shows that thread drops SEND NOW and reads it again.
        private void AfterHeldSent(string id)
        {
            if (_threadOpen != id) return;
            _thWait = default;
            SetSendNowVisible(false);
            _nextThreadMeta = 0f;
            PollThread();
        }

        /// The held row's input with the worktree instruction in front: its text items become ONE text item (the
        /// instruction, then each text; their mentions move with them), the other items (images, files) stay as they are.
        /// Already rewritten (the instruction is there): the same items again. Null: nothing to send.
        public static JArray WorktreeInput(JArray content)
        {
            if (content == null || content.Count == 0) return null;
            var items = content.OfType<JObject>().ToList();
            var text = new System.Text.StringBuilder();
            var mentions = new JArray();
            bool already = items.Any(x => (string)x["type"] == "text" && ((string)x["text"] ?? "").StartsWith(WorktreeInstruction, StringComparison.Ordinal));
            if (!already) text.Append(WorktreeInstruction);
            var rest = new JArray();
            foreach (var item in items)
            {
                if ((string)item["type"] != "text") { rest.Add(item.DeepClone()); continue; }
                if (text.Length > 0) text.Append("\n\n");
                int at = text.Length;
                text.Append((string)item["text"] ?? "");
                foreach (var m in (item["mentions"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var c = (JObject)m.DeepClone();
                    if (c["start"] != null) c["start"] = (int)c["start"] + at;
                    if (c["end"] != null) c["end"] = (int)c["end"] + at;
                    mentions.Add(c);
                }
            }
            var result = new JArray { new JObject { ["type"] = "text", ["text"] = text.ToString(), ["mentions"] = mentions } };
            foreach (var r in rest) result.Add(r);
            return result;
        }

        public void SetThreadWaitForTest(string queuedId, string reason)
        {
            _thWait = (queuedId, reason);
            if (_threadInfo != null) { _threadInfo["queuedWork"] = queuedId != null ? "waiting" : "none"; _threadInfo["mcxWaitReason"] = reason; }
            SetSendNowVisible(queuedId != null);
            RefreshThreadPulse();
        }
        public UiButton SendNowForTest => _thSendNow;
        public string NewThreadSummaryForTest => _ntSummary?.Text ?? "";
        public bool NewThreadForcedWorktreeForTest => _ntForcedWorktree;
        public JObject NewThreadBodyForTest(string prompt) => NewThreadBody(_projectSel, _ntProvider ?? "codex", null, prompt);
        public string WorkspaceChipForTest => _thWsChip?.Text ?? "";
        public string WorkspaceDetailForTest => (_wspTitle?.Text ?? "") + "\n" + (_wspDetail?.Text ?? "") + "\n" + (_wspNote?.Text ?? "");
        public UiList WorkspaceListForTest => _wspList;
        public void SelectWorkspaceForTest(string projectAndKey) { _wspSelKey = projectAndKey; RenderWorkspaceList(); }
        public bool MergeEnabledForTest => _wspMerge != null && _wspMerge.Enabled;
        public bool RemoveEnabledForTest => _wspRemove != null && _wspRemove.Enabled;
    }
}
