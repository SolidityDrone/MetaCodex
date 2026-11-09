using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THREAD's workspace module: a panel linked to the right of the conversation. It follows the thread panel;
    /// drag it and it keeps that place relative to the thread (in FLOAT it rides next to the thread on the ring).
    /// Four tabs on the thread's workspace folder:
    ///   CHANGES  a live map of the files this thread touched (tile size = lines changed, colour = kind, light =
    ///            touched recently; grey = uncommitted but not from this thread), the list, the selected file's diff
    ///   GIT      branch, ahead/behind, status, branches, the history graph; fetch / pull / commit / push
    ///   FILES    the folder tree, a preview of the selected file, edit it in a terminal, mention it to the agent
    ///   VIEWS    the agent views (charts, diagrams, choices) of this thread
    public sealed partial class MetaCodexShell
    {
        public sealed class TouchedFile
        {
            public string Path;              // relative to the workspace root when inside it
            public string Kind = "modify";   // add | modify | delete | move
            public int Added, Removed, Edits;
            public long Seq;                 // newest timeline seq that touched it
            public float Time = -999f;       // when the app saw that touch (recency light)
            public string Diff;              // the newest diff the timeline carried (null: ask git)
            public bool ByThread, Dirty;
            public string GitCode;
        }

        private UiPanel _ws;
        private UiTabs _wsTabs;
        private UiLabel _wsSummary, _wsDiffHead, _wsGitHead, _wsFilesHead, _wsPreviewHead;
        private UiChangeMap _wsMap;
        private UiList _wsChangeList, _wsGitChanges, _wsGitBranches, _wsTree, _wsViewList;
        private UiCodeView _wsDiff, _wsPreview;
        private UiGitGraph _wsGraph;
        private readonly Dictionary<string, TouchedFile> _touched = new Dictionary<string, TouchedFile>();
        private readonly Dictionary<string, long> _touchSeen = new Dictionary<string, long>();
        private readonly HashSet<string> _wsOpenDirs = new HashSet<string>();
        private Dictionary<string, (int a, int r)> _wsNumstat = new Dictionary<string, (int, int)>();
        private string _wsRoot, _wsThread, _wsSel, _wsFileSel;
        private GitStatus _wsGitStatus;
        private bool _wsGitBusy, _wsFirstRows = true;
        private float _wsNextGit, _wsNextMap;
        // the link to the thread panel: the workspace's pose in the thread panel's local space
        private bool _wsHasOffset, _wsWasDragged;
        private Vector3 _wsOffPos;
        private Quaternion _wsOffRot = Quaternion.identity;
        private const float WsYaw = 16f;

        public UiChangeMap ChangeMapForTest => _wsMap;
        public IReadOnlyDictionary<string, TouchedFile> Touched => _touched;

        private void BuildWorkspace()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("thread-ws", "Workspace", 0.58f, 0.66f);
            _ws = p;
            var c = p.Content;
            _wsTabs = p.Add(new UiTabs("CHANGES", "GIT", "FILES", "VIEWS") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);

            // CHANGES
            var s = new UiStack(area, t.Gap);
            _wsSummary = _wsTabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = s.Next(0.02f), Color = t.TextDim });
            _wsMap = _wsTabs.AddTo(0, new UiChangeMap { Rect = s.Next(0.17f), OnPick = WsPick });
            _wsChangeList = _wsTabs.AddTo(0, new UiList(0.026f, (item, idx) => WsPick(item.Tag as string)) { Rect = s.Next(0.104f) });
            var dh = s.Next(0.024f);
            float sb = 0.052f;
            _wsDiffHead = _wsTabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = new Rect(dh.x, dh.y, dh.width - sb * 4 - t.Gap * 4, dh.height), UseMono = true });
            WsButtons(0, new Rect(dh.xMax - sb * 4 - t.Gap * 3, dh.y, sb * 4 + t.Gap * 3, dh.height), new (string, Action)[]
            {
                ("@ ASK", () => { if (_wsSel != null) MentionInComposer(_wsSel); }),
                ("OPEN", () => { if (_wsSel != null) { _wsTabs.Select(2); WsPreview(WsAbs(_wsSel)); } }),
                ("▲", () => _wsDiff.ScrollBy(-8)),
                ("▼", () => _wsDiff.ScrollBy(8)),
            });
            _wsDiff = _wsTabs.AddTo(0, new UiCodeView { Rect = s.Rest() });

            // GIT
            var g = new UiStack(area, t.Gap);
            _wsGitHead = _wsTabs.AddTo(1, new UiLabel("", t.BodySize) { Rect = g.Next(0.026f) });
            var lists = g.NextSplit(0.16f, 0.58f, out var brRect);
            _wsGitChanges = _wsTabs.AddTo(1, new UiList(0.025f, (item, idx) => { if (item.Tag is string path) { _wsTabs.Select(0); WsPick(path); } }) { Rect = lists, Monospace = true });
            _wsGitBranches = _wsTabs.AddTo(1, new UiList(0.025f, (item, idx) => WsBranchPicked(item.Tag as GitBranch)) { Rect = brRect });
            _wsTabs.AddTo(1, new UiLabel("HISTORY", t.SmallSize) { Rect = g.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _wsGraph = _wsTabs.AddTo(1, new UiGitGraph { Rect = g.Next(area.yMax - g.Y - t.ButtonHeight - t.Gap) });
            WsButtons(1, g.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("⟳", () => WsRefreshGit(true)),
                ("FETCH", () => WsGit("fetch --all --prune")),
                ("PULL", () => WsGit("pull --ff-only")),
                ("COMMIT…", WsCommitPrompt),
                ("PUSH", () => WsGit("push")),
                ("TERMINAL", () => { if (_wsRoot != null) RunInNewTerminal("cd " + GitService.Quote(_wsRoot) + " && git status -sb"); }),
            });

            // FILES
            var f = new UiStack(area, t.Gap);
            _wsFilesHead = _wsTabs.AddTo(2, new UiLabel("", t.SmallSize) { Rect = f.Next(0.02f), UseMono = true, Color = t.TextAccent });
            _wsTree = _wsTabs.AddTo(2, new UiList(0.026f, (item, idx) => WsTreePicked(item.Tag as FileEntry)) { Rect = f.Next(0.22f) });
            _wsPreviewHead = _wsTabs.AddTo(2, new UiLabel("", t.SmallSize) { Rect = f.Next(0.02f), Color = t.TextDim });
            _wsPreview = _wsTabs.AddTo(2, new UiCodeView { Rect = f.Next(area.yMax - f.Y - t.ButtonHeight - t.Gap) });
            WsButtons(2, f.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("EDIT", () => { if (_wsFileSel != null) RunInNewTerminal("nano " + GitService.Quote(_wsFileSel)); }),
                ("@ ASK", () => { if (_wsFileSel != null) MentionInComposer(WsRel(_wsFileSel)); }),
                ("▲", () => _wsPreview.ScrollBy(-12)),
                ("▼", () => _wsPreview.ScrollBy(12)),
                ("⟳", WsRefreshTree),
                ("TERMINAL", () => { if (_wsRoot != null) RunInNewTerminal("cd " + GitService.Quote(_wsRoot) + " && ls"); }),
            });

            // VIEWS
            var v = new UiStack(area, t.Gap);
            _wsTabs.AddTo(3, new UiLabel("Agents draw here: charts, diagrams (mermaid), tables, checklists, timelines, choices you answer, and small physics scenes. Ask in the thread, e.g. \"show the architecture as a diagram\".", t.SmallSize) { Rect = v.Next(0.05f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _wsViewList = _wsTabs.AddTo(3, new UiList(0.032f, (item, idx) => { if (item.Tag is string id) OpenView(id); }) { Rect = v.Next(area.yMax - v.Y - t.ButtonHeight - t.Gap) });
            WsButtons(3, v.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("OPEN ALL", () => { foreach (var x in WsViews()) OpenView(x.Id); }),
                ("CLOSE ALL", () => { foreach (var x in WsViews()) ClosePanel(x.PanelId); RefreshWorkspaceViews(); }),
                ("DEMO", ShowDemoView),
            });

            _wsTabs.OnChanged = i => { if (i == 1) WsRefreshGit(true); if (i == 2) WsRefreshTree(); if (i == 3) RefreshWorkspaceViews(); };
            p.Tick += _ => WsTick();
            p.Opened += _ => { WsRenderChanges(); WsRefreshGit(true); };
            WsRenderChanges();
        }

        private void WsButtons(int page, Rect row, (string label, Action act)[] items)
        {
            var t = UiTheme.Current;
            float bw = (row.width - t.Gap * (items.Length - 1)) / items.Length;
            for (int i = 0; i < items.Length; i++)
                _wsTabs.AddTo(page, new UiButton(items[i].label, items[i].act) { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height), TextSize = Mathf.Min(t.BodySize, row.height * 0.55f) });
        }

        public void OpenWorkspace()
        {
            if (_ws == null) return;
            if (!_ws.IsOpen) OpenPanel("thread-ws");
            UpdateWorkspaceLink(true);
        }

        private void ToggleWorkspace()
        {
            Settings.threadWorkspace = !(_ws != null && _ws.IsOpen);
            Settings.Save(_settingsPath);
            if (Settings.threadWorkspace) OpenWorkspace(); else ClosePanel("thread-ws");
        }

        /// The thread whose workspace is shown, and its folder (null until MQH made one).
        private void WsSetThread(string threadId, string root)
        {
            if (threadId != _wsThread)
            {
                _wsThread = threadId;
                _touched.Clear();
                _touchSeen.Clear();
                _wsNumstat.Clear();
                _wsGitStatus = null;
                _wsSel = null;
                _wsFileSel = null;
                _wsFirstRows = true;
                _wsDiff?.SetText("", "diff", "Pick a file above to see its diff.");
                _wsPreview?.SetText("", "code", "Pick a file in the tree.");
                RefreshWorkspaceViews();
            }
            if (root != _wsRoot)
            {
                _wsRoot = root;
                _wsOpenDirs.Clear();
                if (_ws != null) _ws.SetTitle("Workspace · " + (root == null ? "no folder yet" : root.Split('/').Last()));
                WsRefreshGit(true);
                WsRefreshTree();
            }
        }

        private string WsRel(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace('\\', '/');
            if (_wsRoot != null && path.StartsWith(_wsRoot.TrimEnd('/') + "/")) return path.Substring(_wsRoot.TrimEnd('/').Length + 1);
            return path.TrimStart('.', '/').Length > 0 && !path.StartsWith("/") ? path.TrimStart('.', '/') : path;
        }

        private string WsAbs(string rel) => rel == null ? null : rel.StartsWith("/") || _wsRoot == null ? rel : _wsRoot.TrimEnd('/') + "/" + rel;

        // ------------------------------------------------------------------ changes

        /// The timeline's file-change rows (also inside turns) -> what this thread touched, with recency.
        private void WsTrackRows(JArray rows)
        {
            float now = Time.unscaledTime;
            var found = new Dictionary<string, TouchedFile>();
            void Walk(JToken r)
            {
                string kind = (string)r["kind"];
                if (kind == "turn") { foreach (var ch in r["children"] as JArray ?? new JArray()) Walk(ch); return; }
                if (kind != "work" || (string)r["workKind"] != "file-change") return;
                var change = r["change"];
                string raw = (string)change?["path"];
                if (string.IsNullOrEmpty(raw)) return;
                string path = WsRel(raw);
                long seq = r["sourceSeqEnd"]?.Type == JTokenType.Integer ? (long)r["sourceSeqEnd"] : 0;
                if (!found.TryGetValue(path, out var tf)) { tf = new TouchedFile { Path = path, ByThread = true }; found[path] = tf; }
                tf.Edits++;
                tf.Added += change["diffStats"]?["added"]?.Type == JTokenType.Integer ? (int)change["diffStats"]["added"] : 0;
                tf.Removed += change["diffStats"]?["removed"]?.Type == JTokenType.Integer ? (int)change["diffStats"]["removed"] : 0;
                string k = ((string)change["kind"] ?? "").ToLowerInvariant();
                if (change["movePath"]?.Type == JTokenType.String) k = "move";
                tf.Kind = tf.Edits == 1 || k == "delete" || k == "move" ? NormalizeKind(k) : tf.Kind == "add" ? "add" : NormalizeKind(k);
                if (change["diff"]?.Type == JTokenType.String) tf.Diff = (string)change["diff"];
                tf.Seq = Math.Max(tf.Seq, seq);
            }
            foreach (var r in rows ?? new JArray()) Walk(r);
            foreach (var tf in found.Values)
            {
                if (_touchSeen.TryGetValue(tf.Path, out long seen)) tf.Time = seen < tf.Seq ? now : _touched.TryGetValue(tf.Path, out var old) ? old.Time : now - 600f;
                else tf.Time = _wsFirstRows ? now - 600f : now; // what was there when the thread opened is not "new"
                _touchSeen[tf.Path] = tf.Seq;
            }
            _wsFirstRows = false;
            foreach (var key in _touched.Keys.Where(k => _touched[k].ByThread && !found.ContainsKey(k)).ToList()) _touched.Remove(key);
            foreach (var tf in found.Values) _touched[tf.Path] = tf;
            WsMergeGit();
            WsRenderChanges();
            if (_wsSel != null && found.TryGetValue(_wsSel, out var sel) && sel.Time >= now - 0.01f) WsPick(_wsSel); // the open diff changed
        }

        private static string NormalizeKind(string k)
        {
            switch (k)
            {
                case "add": case "added": case "create": case "created": case "new": return "add";
                case "delete": case "deleted": case "remove": case "removed": return "delete";
                case "move": case "rename": case "renamed": return "move";
                default: return "modify";
            }
        }

        /// git status + numstat -> uncommitted files (dimmed when this thread did not touch them).
        private void WsMergeGit()
        {
            foreach (var tf in _touched.Values) { tf.Dirty = false; tf.GitCode = null; }
            foreach (var key in _touched.Keys.Where(k => !_touched[k].ByThread).ToList()) _touched.Remove(key);
            if (_wsGitStatus == null || _wsGitStatus.Error != null) return;
            foreach (var e in _wsGitStatus.Entries)
            {
                string path = e.Path;
                if (!_touched.TryGetValue(path, out var tf))
                {
                    tf = new TouchedFile { Path = path, ByThread = false, Kind = e.Code.Contains("?") || e.Code.Contains("A") ? "add" : e.Code.Contains("D") ? "delete" : e.Code.Contains("R") ? "move" : "modify" };
                    _touched[path] = tf;
                }
                tf.Dirty = true;
                tf.GitCode = e.Code;
                if (_wsNumstat.TryGetValue(path, out var ns) && !tf.ByThread) { tf.Added = ns.a; tf.Removed = ns.r; }
            }
        }

        private void WsRenderChanges()
        {
            if (_wsMap == null) return;
            var t = UiTheme.Current;
            float now = Time.unscaledTime;
            var files = _touched.Values.OrderByDescending(f => f.ByThread ? f.Time : -1e9f).ThenBy(f => f.Path).ToList();
            _wsMap.Items.Clear();
            foreach (var f in files)
                _wsMap.Items.Add(new UiChangeMap.Item { Path = f.Path, Kind = f.Kind, Added = f.Added, Removed = f.Removed, Heat = f.ByThread ? Mathf.Exp(-(now - f.Time) / 90f) : 0f, Dim = !f.ByThread });
            _wsMap.Selected = _wsSel;
            _wsMap.Empty = _wsRoot == null && _threadOpen == null ? "Open a thread: its changed files show up here, live." : "No changes yet: files the agent edits show up here, live.";
            _wsMap.Redraw();
            _wsChangeList?.SetItems(files.Select(f => new UiListItem
            {
                Icon = f.Kind == "add" ? "+" : f.Kind == "delete" ? "−" : f.Kind == "move" ? "→" : "●",
                IconColor = !f.ByThread ? t.TextDim : f.Kind == "add" ? t.Ok : f.Kind == "delete" ? t.Danger : f.Kind == "move" ? t.Violet : t.Warn,
                Title = f.Path,
                TitleColor = f.ByThread ? (Color?)null : t.TextDim,
                Sub = $"+{f.Added} −{f.Removed}",
                Badge = !f.ByThread ? "UNCOMMITTED" : now - f.Time < 60f ? "NOW" : f.Dirty ? "UNCOMMITTED" : "COMMITTED",
                BadgeColor = !f.ByThread ? t.TextDim : now - f.Time < 60f ? t.Ok : f.Dirty ? t.Warn : t.TextDim,
                Tag = f.Path,
            }), keepScroll: true);
            int byThread = files.Count(f => f.ByThread), dirty = files.Count(f => f.Dirty);
            var last = files.Where(f => f.ByThread).Select(f => now - f.Time).DefaultIfEmpty(-1f).Min();
            _wsSummary?.Set(files.Count == 0 ? (_wsRoot ?? "no workspace yet") :
                $"<b>{byThread}</b> files from this thread · <color=#{UiViz.Hex(t.Ok)}>+{files.Where(f => f.ByThread).Sum(f => f.Added)}</color> <color=#{UiViz.Hex(t.Danger)}>−{files.Where(f => f.ByThread).Sum(f => f.Removed)}</color> · {dirty} uncommitted" +
                (last >= 0 && last < 3600 ? $" · last change {(last < 60 ? (int)last + " s" : (int)(last / 60) + " min")} ago" : ""), t.TextDim);
        }

        /// A file picked on the map or in a list: its diff (the timeline's, else git's).
        private void WsPick(string path)
        {
            if (path == null) return;
            _wsSel = path;
            if (_wsMap != null) { _wsMap.Selected = path; _wsMap.Redraw(); }
            int idx = _wsChangeList?.Items.ToList().FindIndex(i => i.Tag as string == path) ?? -1;
            if (idx >= 0) _wsChangeList.SelectIndex(idx, notify: false);
            _touched.TryGetValue(path, out var tf);
            _wsDiffHead?.Set($"{UiViz.NoParse(path)}  <color=#{UiViz.Hex(UiTheme.Current.Ok)}>+{tf?.Added ?? 0}</color> <color=#{UiViz.Hex(UiTheme.Current.Danger)}>−{tf?.Removed ?? 0}</color>");
            if (!string.IsNullOrEmpty(tf?.Diff)) _wsDiff.SetText(tf.Diff, "diff");
            else _wsDiff.SetText("", "diff", "loading the diff …");
            if (_wsRoot == null) return;
            string root = _wsRoot, rel = path;
            bool untracked = tf?.GitCode == "??";
            Task.Run(async () =>
            {
                var r = untracked ? await Git.Git(root, "diff --no-index -- /dev/null " + GitService.Quote(rel), 15000)
                                  : await Git.Git(root, "diff HEAD -- " + GitService.Quote(rel), 15000);
                _main.Enqueue(() =>
                {
                    if (_wsSel != rel || root != _wsRoot) return;
                    string text = r.output ?? "";
                    if (text.Contains("@@")) _wsDiff.SetText(text.Substring(Math.Max(0, text.IndexOf("diff ", StringComparison.Ordinal))), "diff");
                    else if (string.IsNullOrEmpty(tf?.Diff)) _wsDiff.SetText("", "diff", r.code == 0 && text.Trim().Length == 0 ? "No uncommitted difference (committed already, or unchanged)." : text.Trim());
                });
            });
        }

        private void MentionInComposer(string path)
        {
            if (_thComposer == null) return;
            string cur = (_thComposer.Text ?? "").TrimEnd();
            _thComposer.Set((cur.Length > 0 ? cur + " " : "") + path + " ");
            Toast("Added to the message: " + path, "info");
        }

        // ------------------------------------------------------------------ git

        private void WsRefreshGit(bool now = false)
        {
            if (_wsRoot == null || _wsGitBusy) return;
            if (!now && Time.unscaledTime < _wsNextGit) return;
            _wsGitBusy = true;
            string root = _wsRoot;
            bool running = ((string)_threadInfo?["runtime"]?["displayStatus"] ?? (string)_threadInfo?["status"]) == "running";
            _wsNextGit = Time.unscaledTime + (running ? 8f : 30f);
            Task.Run(async () =>
            {
                GitStatus st = null;
                (int code, string output) ns = (0, "");
                try
                {
                    st = await Git.Status(root);
                    if (st.Error == null) ns = await Git.Git(root, "diff --numstat HEAD", 15000);
                }
                catch (Exception ex) { st = new GitStatus { Repo = root, Error = ex.Message }; }
                _main.Enqueue(() =>
                {
                    _wsGitBusy = false;
                    if (root != _wsRoot) return;
                    _wsGitStatus = st;
                    _wsNumstat = ParseNumstat(ns.output);
                    WsShowGit();
                    WsMergeGit();
                    WsRenderChanges();
                });
            });
        }

        public static Dictionary<string, (int, int)> ParseNumstat(string output)
        {
            var d = new Dictionary<string, (int, int)>();
            foreach (var line in (output ?? "").Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                int.TryParse(f[0], out int a);
                int.TryParse(f[1], out int r);
                d[f[2].Trim()] = (a, r);
            }
            return d;
        }

        private void WsShowGit()
        {
            if (_wsGitHead == null) return;
            var t = UiTheme.Current;
            var st = _wsGitStatus;
            if (st == null) { _wsGitHead.Set(_wsRoot == null ? "No workspace yet" : "reading git …", t.TextDim); return; }
            if (st.Error != null) { _wsGitHead.Set(st.Error.Contains("not a git repository") ? "Not a git repository: " + _wsRoot : "⚠ " + Cut(st.Error, 120), st.Error.Contains("not a git") ? t.TextDim : t.Danger); return; }
            string ab = (st.Ahead > 0 ? $"  <color=#{UiViz.Hex(t.Ok)}>↑{st.Ahead}</color>" : "") + (st.Behind > 0 ? $"  <color=#{UiViz.Hex(t.Warn)}>↓{st.Behind}</color>" : "");
            string up = string.IsNullOrEmpty(st.Upstream) ? $"<color=#{UiViz.Hex(t.TextDim)}>no upstream</color>" : $"<color=#{UiViz.Hex(t.TextDim)}>{UiViz.NoParse(st.Upstream)}</color>";
            _wsGitHead.Set($"⎇ <b>{UiViz.NoParse(st.Branch)}</b>{ab}   {up}   " + (st.Entries.Count == 0 ? $"<color=#{UiViz.Hex(t.Ok)}>clean</color>" : $"<color=#{UiViz.Hex(t.Warn)}>{st.Entries.Count} changed</color>"), t.Text);
            _wsGitChanges.SetItems(st.Entries.Select(e => new UiListItem
            {
                Icon = e.Code.Contains("?") ? "+" : e.Code.Contains("D") ? "−" : "●",
                Title = e.Path,
                Badge = e.Code,
                BadgeColor = e.Code.Contains("?") ? t.TextDim : e.Code.Contains("D") ? t.Danger : e.Code.Contains("A") ? t.Ok : t.Warn,
                Tag = e.Path,
            }), keepScroll: true);
            _wsGitBranches.SetItems(st.Branches.Select(b => new UiListItem
            {
                Icon = b.Current ? "◆" : "◇",
                Title = b.Name,
                TitleColor = b.Current ? t.TextAccent : (Color?)null,
                Sub = b.When,
                Badge = string.IsNullOrEmpty(b.Track) ? (string.IsNullOrEmpty(b.Upstream) ? "local" : "synced") : b.Track.Trim('[', ']'),
                BadgeColor = string.IsNullOrEmpty(b.Track) ? (string.IsNullOrEmpty(b.Upstream) ? t.TextDim : t.Ok) : t.Warn,
                Tag = b,
            }), keepScroll: true);
            _wsGraph.SetCommits(st.Log);
        }

        private void WsGit(string args)
        {
            if (_wsRoot == null) { Toast("This thread has no workspace yet", "warn"); return; }
            string root = _wsRoot, verb = args.Split(' ')[0];
            Toast("git " + verb + " …", "info");
            Task.Run(async () =>
            {
                var r = await Git.Git(root, args, 90000);
                _main.Enqueue(() =>
                {
                    Toast(r.code == 0 ? "git " + verb + " done" : "git " + verb + ": " + Last(r.output), r.code == 0 ? "ok" : "error");
                    WsRefreshGit(true);
                });
            });
        }

        private void WsCommitPrompt()
        {
            if (_wsRoot == null) return;
            UiKeyboard.Edit("", false, null, msg =>
            {
                if (string.IsNullOrWhiteSpace(msg)) { Toast("Commit cancelled (empty message)", "warn"); return; }
                WsGit("add -A && git -C " + GitService.Quote(_wsRoot) + " commit -m " + GitService.Quote(msg.Trim()));
            });
        }

        private void WsBranchPicked(GitBranch b)
        {
            if (b == null || b.Current || _wsRoot == null) return;
            Modal("SWITCH BRANCH", $"Check out '{b.Name}' in {_wsRoot.Split('/').Last()}? The agent works in this folder too.", new[] { "CHECKOUT", "CANCEL" }, i =>
            {
                if (i == 0) WsGit("checkout " + GitService.Quote(b.Name));
            });
        }

        // ------------------------------------------------------------------ files

        private void WsRefreshTree()
        {
            if (_wsTree == null) return;
            var t = UiTheme.Current;
            if (_wsRoot == null) { _wsTree.SetItems(new UiListItem[0]); _wsFilesHead.Set("no workspace yet"); return; }
            var items = new List<UiListItem>();
            void Add(string dir, int depth)
            {
                var entries = Files.List(dir, out string err);
                if (err != null) { items.Add(new UiListItem { Icon = "⚠", Title = err, Indent = depth, TitleColor = t.Danger }); return; }
                foreach (var e in entries)
                {
                    if (e.Name == ".git" || items.Count > 600) continue;
                    bool open = e.IsDir && _wsOpenDirs.Contains(e.LinuxPath);
                    string rel = WsRel(e.LinuxPath);
                    _touched.TryGetValue(rel, out var tf);
                    bool inDir = e.IsDir && _touched.Keys.Any(k => k.StartsWith(rel + "/"));
                    items.Add(new UiListItem
                    {
                        Icon = e.IsDir ? (open ? "▾" : "▸") : "·",
                        Title = e.Name + (e.IsDir ? "/" : ""),
                        TitleColor = tf != null ? (tf.ByThread ? t.Warn : t.TextDim) : e.IsDir ? t.TextAccent : e.Name.StartsWith(".") ? t.TextDim : (Color?)null,
                        Sub = e.IsDir ? "" : FileService.Size(e.Size),
                        Badge = tf != null ? (tf.ByThread ? "EDITED" : tf.GitCode ?? "") : inDir ? "●" : "",
                        BadgeColor = tf != null && tf.ByThread ? t.Warn : t.TextDim,
                        Indent = depth,
                        Tag = e,
                    });
                    if (open && depth < 8) Add(e.LinuxPath, depth + 1);
                }
            }
            Add(_wsRoot, 0);
            _wsTree.SetItems(items, keepScroll: true);
            _wsFilesHead.Set(_wsRoot + (_touched.Count > 0 ? $"   <color=#{UiViz.Hex(t.TextDim)}>{_touched.Values.Count(x => x.ByThread)} edited by the agent</color>" : ""));
        }

        private void WsTreePicked(FileEntry e)
        {
            if (e == null) return;
            if (e.IsDir)
            {
                if (!_wsOpenDirs.Remove(e.LinuxPath)) _wsOpenDirs.Add(e.LinuxPath);
                WsRefreshTree();
                return;
            }
            WsPreview(e.LinuxPath);
        }

        private void WsPreview(string linuxPath)
        {
            _wsFileSel = linuxPath;
            var t = UiTheme.Current;
            string host = Files.HostPath(linuxPath);
            try
            {
                var fi = new FileInfo(host);
                if (!fi.Exists) { _wsPreviewHead.Set("not found: " + linuxPath, t.Warn); _wsPreview.SetText("", "code", ""); return; }
                byte[] head;
                using (var fs = fi.OpenRead())
                {
                    int n = (int)Math.Min(fi.Length, 256 * 1024);
                    head = new byte[n];
                    int read = 0;
                    while (read < n) { int k = fs.Read(head, read, n - read); if (k <= 0) break; read += k; }
                }
                bool binary = head.Take(8192).Any(b => b == 0);
                string text = binary ? "" : Encoding.UTF8.GetString(head);
                int lines = binary ? 0 : text.Count(ch => ch == '\n') + 1;
                _wsPreviewHead.Set($"<b>{UiViz.NoParse(fi.Name)}</b> · {FileService.Size(fi.Length)}{(binary ? " · binary" : $" · {lines} lines")}{(fi.Length > head.Length ? " · first 256 KB" : "")}", t.TextDim);
                _wsPreview.SetText(text, "code", binary ? "Binary file: no preview." : "(empty file)");
            }
            catch (Exception ex) { _wsPreviewHead.Set("⚠ " + ex.Message, t.Danger); _wsPreview.SetText("", "code", ""); }
        }

        // ------------------------------------------------------------------ views

        private IEnumerable<AgentView> WsViews() => _viewList.Where(v => _wsThread == null || v.ThreadId == _wsThread || v.ThreadId == null).OrderByDescending(v => v.At);

        public void RefreshWorkspaceViews()
        {
            if (_wsViewList == null) return;
            var t = UiTheme.Current;
            _wsViewList.SetItems(WsViews().Select(v => new UiListItem
            {
                Icon = "▣",
                IconColor = v.Error != null ? t.Danger : t.Accent,
                Title = v.Title,
                Sub = (v.ThreadId == null ? "from Linux" : "from this thread") + " · " + v.At.ToString("HH:mm"),
                Badge = _panels.TryGetValue(v.PanelId, out var p) && p.IsOpen ? "OPEN" : v.Answer != null ? "ANSWERED" : "",
                BadgeColor = t.Ok,
                Tag = v.Id,
            }), keepScroll: true);
        }

        /// A sample view: what agents can draw (also a quick check that views work on this headset).
        public void ShowDemoView()
        {
            var spec = JObject.Parse(@"{
  ""id"": ""demo"", ""title"": ""What agents can show"", ""columns"": 2,
  ""components"": [
    {""type"":""stats"",""items"":[{""label"":""Tests"",""value"":""128"",""delta"":""+6""},{""label"":""Build"",""value"":""41 s"",""delta"":""-12 s"",""tone"":""ok""}]},
    {""type"":""chart"",""kind"":""line"",""title"":""Frame time (ms)"",""labels"":[""1"",""2"",""3"",""4"",""5"",""6"",""7"",""8""],""series"":[{""name"":""app"",""data"":[11.2,11.8,12.9,11.4,11.1,13.6,12.2,11.5]},{""name"":""budget"",""data"":[13.9,13.9,13.9,13.9,13.9,13.9,13.9,13.9]}]},
    {""type"":""mermaid"",""text"":""graph LR\n  U[You] -->|voice| S[Speech-to-text]\n  S --> L{Command?}\n  L -->|yes| A[Action]\n  L -->|no| T[Thread agent]\n  T --> V[View]""},
    {""type"":""chart"",""kind"":""donut"",""title"":""Where time goes"",""labels"":[""Render"",""Scripts"",""Physics"",""Idle""],""data"":[46,22,8,24]},
    {""type"":""checklist"",""items"":[{""text"":""Parse the spec"",""done"":true},{""text"":""Lay out the graph"",""done"":true},{""text"":""Answer back"",""done"":false}]},
    {""type"":""choices"",""prompt"":""Which one next?"",""options"":[""Charts"",""Diagrams"",""Physics""]},
    {""type"":""scene3d"",""size"":[0.5,0.3],""bodies"":[{""shape"":""sphere"",""size"":0.035,""pos"":[-0.15,0.25,0],""vel"":[0.4,0,0.1],""count"":6,""spread"":0.12,""bounce"":0.7},{""shape"":""box"",""size"":0.05,""pos"":[0.1,0.1,0],""count"":3,""spread"":0.1,""color"":""#D95926""}]}
  ]}");
            ShowView(spec, null, 0, "demo", true);
        }

        // ------------------------------------------------------------------ the link to the thread panel

        private void WsTick()
        {
            if (_ws == null || !_ws.IsOpen) return;
            WsRefreshGit();
            if (Time.unscaledTime >= _wsNextMap)
            {
                _wsNextMap = Time.unscaledTime + 2f;
                float now = Time.unscaledTime;
                if (_touched.Values.Any(f => f.ByThread && now - f.Time < 240f)) WsRenderChanges(); // recency fades
            }
        }

        private void DefaultWsOffset(UiPanel thread)
        {
            var head = Head;
            if (head != null)
            {
                // the module continues the thread's arc around you: on the circle through the thread, right next
                // to it, facing you; with "Bend panels" on both bend on that circle and read as one curved screen
                var d = thread.transform.position - head.position;
                float r = Mathf.Max(0.45f, new Vector2(d.x, d.z).magnitude);
                float halfTh = thread.Width * 0.5f * thread.RestLossyScale.x, halfWs = _ws.Width * 0.5f * _ws.RestLossyScale.x;
                float yaw = Mathf.Atan2(d.x, d.z) + (halfTh + 0.015f + halfWs) / r;
                var dir = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
                var pos = new Vector3(head.position.x, thread.transform.position.y, head.position.z) + dir * r;
                _wsOffPos = thread.transform.InverseTransformPoint(pos);
                _wsOffRot = Quaternion.Inverse(thread.transform.rotation) * Quaternion.LookRotation(dir, Vector3.up);
                _wsHasOffset = true;
                return;
            }
            float a = WsYaw * Mathf.Deg2Rad;
            float halfW = _ws.Width * 0.5f * (_ws.RestLossyScale.x / Mathf.Max(1e-4f, thread.RestLossyScale.x));
            _wsOffPos = new Vector3(thread.Width * 0.5f + 0.012f + halfW * Mathf.Cos(a), 0f, -halfW * Mathf.Sin(a));
            _wsOffRot = Quaternion.Euler(0f, WsYaw, 0f);
            _wsHasOffset = true;
        }

        /// Every frame (fixed layout): the workspace sits where its offset says, relative to the thread panel;
        /// dragged and dropped, it keeps the new offset. It closes with the thread.
        private void UpdateWorkspaceLink(bool force = false)
        {
            if (_ws == null || !_ws.IsOpen) return;
            if (!_panels.TryGetValue("thread", out var th) || !th.IsOpen) { _ws.Close(); return; }
            if (Floating && !force) return; // the carousel places it beside the thread
            if (Router != null && Router.IsDragging(_ws)) { _wsWasDragged = true; return; }
            if (_wsWasDragged)
            {
                _wsWasDragged = false;
                _wsOffPos = th.transform.InverseTransformPoint(_ws.transform.position);
                _wsOffRot = Quaternion.Inverse(th.transform.rotation) * _ws.transform.rotation;
                _wsHasOffset = true;
                return;
            }
            if (!_wsHasOffset) DefaultWsOffset(th);
            if (Floating) return;
            _ws.transform.SetPositionAndRotation(th.transform.TransformPoint(_wsOffPos), th.transform.rotation * _wsOffRot);
        }

        /// Self-tests: the workspace's pose relative to the thread panel.
        public (Vector3 pos, Quaternion rot) WorkspaceOffsetForTest => (_wsOffPos, _wsOffRot);

        // ------------------------------------------------------------------ picker

        private UiPanel _picker;
        private UiList _pickerList;
        private UiLabel _pickerHint;
        private Action<UiListItem> _pickerCb;

        private void BuildPicker()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("picker", "Choose", 0.42f, 0.46f);
            _picker = p;
            var c = p.Content;
            var st = new UiStack(c, t.Gap);
            _pickerHint = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.034f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _pickerList = p.Add(new UiList(0.038f, (item, idx) =>
            {
                var cb = _pickerCb;
                _pickerCb = null;
                p.Close();
                cb?.Invoke(item);
            }) { Rect = st.Next(c.height - 0.034f - t.ButtonHeight - t.Gap * 2) });
            p.Add(new UiButton("CANCEL", () => { _pickerCb = null; p.Close(); }) { Rect = st.Next(t.ButtonHeight) });
        }

        /// A short list to choose from, in front of the panel that asked (model, access, fork target, ...).
        public void ShowPicker(string title, string hint, IEnumerable<UiListItem> items, Action<UiListItem> onPick, UiPanel near)
        {
            if (_picker == null) return;
            _picker.SetTitle(title);
            _pickerHint.Set(hint ?? "");
            _pickerList.SelectedIndex = -1;
            _pickerList.SetItems(items);
            _pickerCb = onPick;
            if (near != null)
            {
                var fwd = near.transform.forward;
                _picker.transform.SetPositionAndRotation(near.transform.position - fwd * 0.09f + Vector3.down * 0.03f, near.transform.rotation);
                _placed.Add("picker");
            }
            OpenPanel("picker");
        }

        public UiList PickerListForTest => _pickerList;
    }
}
