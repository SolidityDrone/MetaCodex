using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THREAD's sidebar (owner 2026-11-11, docs/thread-sidebar.md): the expand handle on THREAD's right edge opens a panel
    /// attached to the thread's right edge, placed, moved, bent and closed with it (pinch its header to carry both);
    /// each thread remembers whether it was open and on which tab. Everything is about the thread's project folder
    /// (its environment path, never "/"):
    ///   CHANGES  every file the thread changed (git against HEAD, or since the base branch in a worktree; outside git
    ///            the timeline's edits); a file opens in a VS Code-like diff (old / new numbers, +/- gutter, green / red
    ///            lines, syntax colours, unchanged runs folded, wrap or scroll sideways); live while the agent edits
    ///   FILES    the folder with a breadcrumb; a file opens read-only in the code view
    ///   GIT      branch, status, recent commits when the folder is in a repo; else only INIT GIT
    ///   STATUS   model, access, context, what runs now, approvals, the queue, usage, MQH
    ///   VIEWS    the agent views of this thread
    public sealed partial class MetaCodexShell
    {
        public const string SidebarId = "thread-side";
        public const float SbW = 0.62f, SbH = 0.66f, SbGap = 0.006f, SbYaw = 14f;
        public const int TabChanges = 0, TabFiles = 1, TabGit = 2, TabStatus = 3, TabViews = 4;
        public static readonly string[] SidebarTabs = { "changes", "files", "git", "status", "views" };

        private UiPanel _sb;
        private UiTabs _sbTabs;
        private UiEdgeHandle _thHandle;
        private readonly List<(UiElement el, int tab, Func<bool> when)> _sbEls = new List<(UiElement, int, Func<bool>)>();
        private string _sbThread, _sbRoot;
        private int _sbGen;                 // bumped when the thread or its folder changes: late answers are dropped
        private bool _sbDragged, _sbKeyFocus, _sbHooked;
        private int _sbPending; // background reads (git, files) whose answer has not come back yet
        private readonly Dictionary<string, (bool open, int tab)> _sbMem = new Dictionary<string, (bool, int)>();

        // CHANGES
        private Dictionary<string, ChangedFile> _touched = new Dictionary<string, ChangedFile>(); // from the timeline
        private List<ChangedFile> _sbFiles = new List<ChangedFile>();
        private GitSnapshot _sbSnap;
        private bool _sbChBusy, _sbChAgain;
        private float _sbNextChanges, _sbChangesAt = -1f;
        private long _sbTouchSeq;
        private UiLabel _sbChSummary, _sbDiffLabel;
        private UiRows _sbChList;
        private UiSourceView _sbDiff;
        private UiButton _sbDiffWrap;
        private string _sbDiffPath, _sbDiffSig;
        private int _sbDiffGen;
        // FILES
        private string _sbDir, _sbFile;
        private UiCrumbs _sbCrumbs;
        private UiRows _sbTree;
        private UiLabel _sbFileLabel;
        private UiSourceView _sbCode;
        private UiButton _sbCodeWrap;
        private float _sbNextTree;
        // GIT
        private GitStatus _sbGit;
        private bool _sbGitBusy;
        private float _sbNextGit;
        private UiLabel _sbGitHead, _sbGitMsg, _sbNoRepoTitle, _sbNoRepoText;
        private UiRows _sbGitStatus, _sbGitLog;
        // STATUS, VIEWS
        private UiRows _sbStatus, _sbViews;
        private float _sbNextStatus;

        public IReadOnlyDictionary<string, ChangedFile> Touched => _touched;
        public UiPanel Sidebar => _sb;
        public bool SidebarOpen => _sb != null && _sb.IsOpen;
        public int SidebarTab => _sbTabs?.Selected ?? 0;
        private bool SbLinked => _sb != null && _sb.IsOpen && _panels.TryGetValue("thread", out var th) && th.IsOpen;

        // ------------------------------------------------------------------ build

        private T SbAdd<T>(int tab, Func<bool> when, T el) where T : UiElement
        {
            _sb.Add(el);
            _sbEls.Add((el, tab, when));
            el.SetVisible(false);
            return el;
        }

        private void SbButtons(int tab, Func<bool> when, Rect row, params (string label, Action act, float weight)[] items)
        {
            var t = UiTheme.Current;
            float total = items.Sum(i => i.weight), unit = (row.width - t.Gap * (items.Length - 1)) / total, x = row.x;
            foreach (var it in items)
            {
                float w = unit * it.weight;
                if (it.act != null) SbAdd(tab, when, new UiButton(it.label, it.act) { Rect = new Rect(x, row.y, w, row.height), TextSize = Mathf.Min(t.SmallSize * 1.05f, row.height * 0.5f) });
                x += w + t.Gap;
            }
        }

        private void BuildSidebar()
        {
            _sbEls.Clear();
            var t = UiTheme.Current;
            var p = Panel<UiPanel>(SidebarId, "Project", SbW, SbH);
            _sb = p;
            p.HideCloseButton(); // ‹ folds it back into THREAD (it closes with the thread)
            p.Add(new UiButton("‹", () => SetSidebar(false), UiButtonStyle.Ghost) { Rect = new Rect(SbW - 0.044f, 0.006f, 0.036f, t.HeaderHeight - 0.010f), TextSize = t.BodySize * 1.3f });
            var c = p.Content;
            _sbTabs = p.Add(new UiTabs("CHANGES", "FILES", "GIT", "STATUS", "VIEWS") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);
            float bh = t.ButtonHeight, g = t.Gap, labelH = 0.028f;

            // CHANGES: the list, or one file's diff
            Func<bool> chList = () => _sbDiffPath == null, chFile = () => _sbDiffPath != null;
            _sbChSummary = SbAdd(TabChanges, chList, new UiLabel("", 0.0135f) { Rect = new Rect(area.x + 0.004f, area.y, area.width - 0.054f, labelH), Color = t.TextDim });
            SbAdd(TabChanges, chList, new UiButton("⟳", () => SbRefreshChanges(true)) { Rect = new Rect(area.xMax - 0.046f, area.y, 0.046f, labelH), TextSize = t.BodySize });
            _sbChList = SbAdd(TabChanges, chList, new UiRows { Rect = new Rect(area.x, area.y + labelH + g, area.width, area.height - labelH - g), OnPick = (row, i) => SbOpenDiff(row.Tag as ChangedFile) });
            SbButtons(TabChanges, chFile, new Rect(area.x, area.y, area.width, bh),
                ("‹ ALL CHANGES", () => SbCloseDiff(), 1.6f), ("▲ PREV", () => SbStepFile(-1), 1f), ("▼ NEXT", () => SbStepFile(1), 1f),
                ("WRAP", () => SbToggleWrap(_sbDiff, _sbDiffWrap), 0.9f), ("@ ASK", () => { if (_sbDiffPath != null) MentionInComposer(_sbDiffPath); }, 0.9f),
                ("OPEN FILE", () => { if (_sbDiffPath != null) { _sbTabs.Select(TabFiles); SbOpenFile(SbAbs(_sbDiffPath)); } }, 1.2f));
            _sbDiffWrap = (UiButton)_sbEls[_sbEls.Count - 3].el;
            _sbDiffLabel = SbAdd(TabChanges, chFile, new UiLabel("", 0.015f) { Rect = new Rect(area.x + 0.004f, area.y + bh + g, area.width - 0.008f, labelH), UseMono = true });
            _sbDiff = SbAdd(TabChanges, chFile, new UiSourceView { Rect = new Rect(area.x, area.y + bh + labelH + g * 2, area.width, area.height - bh - labelH - g * 2) });

            // FILES: the folder, or one file
            Func<bool> fList = () => _sbFile == null, fFile = () => _sbFile != null;
            _sbCrumbs = SbAdd(TabFiles, fList, new UiCrumbs { Rect = new Rect(area.x + 0.004f, area.y, area.width - 0.104f, labelH), OnPick = SbCrumbPicked });
            SbAdd(TabFiles, fList, new UiButton("⌂", () => { _sbDir = _sbRoot; SbRefreshTree(); }) { Rect = new Rect(area.xMax - 0.096f, area.y, 0.046f, labelH), TextSize = t.BodySize });
            SbAdd(TabFiles, fList, new UiButton("⟳", () => SbRefreshTree()) { Rect = new Rect(area.xMax - 0.046f, area.y, 0.046f, labelH), TextSize = t.BodySize });
            _sbTree = SbAdd(TabFiles, fList, new UiRows { Rect = new Rect(area.x, area.y + labelH + g, area.width, area.height - labelH - g), OnPick = (row, i) => SbTreePicked(row.Tag as FileEntry) });
            SbButtons(TabFiles, fFile, new Rect(area.x, area.y, area.width, bh),
                ("‹ BACK", () => { _sbFile = null; SbShow(); SbRefreshTree(); }, 1.3f), ("WRAP", () => SbToggleWrap(_sbCode, _sbCodeWrap), 0.9f),
                ("@ ASK", () => { if (_sbFile != null) MentionInComposer(ThreadChanges.Rel(_sbRoot, _sbFile)); }, 0.9f),
                ("EDIT", () => { if (_sbFile != null) RunInNewTerminal("nano " + GitService.Quote(_sbFile)); }, 0.9f),
                ("DIFF", () => SbDiffOfOpenFile(), 0.9f));
            _sbCodeWrap = (UiButton)_sbEls[_sbEls.Count - 4].el;
            _sbFileLabel = SbAdd(TabFiles, fFile, new UiLabel("", 0.015f) { Rect = new Rect(area.x + 0.004f, area.y + bh + g, area.width - 0.008f, labelH), UseMono = true });
            _sbCode = SbAdd(TabFiles, fFile, new UiSourceView { Rect = new Rect(area.x, area.y + bh + labelH + g * 2, area.width, area.height - bh - labelH - g * 2) });

            // GIT: a repo; or no repo (INIT GIT, nothing else); or an error / no folder yet
            Func<bool> repo = () => SbGitMode == "repo", norepo = () => SbGitMode == "norepo", msg = () => SbGitMode == "msg";
            float y = area.y;
            _sbGitHead = SbAdd(TabGit, repo, new UiLabel("", t.BodySize) { Rect = new Rect(area.x + 0.004f, y, area.width - 0.054f, 0.032f) });
            SbAdd(TabGit, repo, new UiButton("⟳", () => SbRefreshGit(true)) { Rect = new Rect(area.xMax - 0.046f, y, 0.046f, 0.032f), TextSize = t.BodySize });
            y += 0.032f + g;
            float statusH = 0.034f * 6f;
            _sbGitStatus = SbAdd(TabGit, repo, new UiRows { Rect = new Rect(area.x, y, area.width, statusH + 0.034f), OnPick = (row, i) => SbGitFilePicked(row.Tag as string), Empty = "Clean: nothing to commit." });
            y += statusH + 0.034f + g;
            _sbGitLog = SbAdd(TabGit, repo, new UiRows { Rect = new Rect(area.x, y, area.width, area.yMax - y - bh - g), Empty = "No commits yet." });
            SbButtons(TabGit, repo, new Rect(area.x, area.yMax - bh, area.width, bh),
                ("FETCH", () => SbGitRun("fetch --all --prune"), 1f), ("PULL", () => SbGitRun("pull --ff-only"), 1f), ("COMMIT…", SbCommitPrompt, 1.1f),
                ("PUSH", () => SbGitRun("push"), 1f), ("TERMINAL", () => { if (SbTop != null) RunInNewTerminal("cd " + GitService.Quote(SbTop) + " && git status -sb"); }, 1.2f));
            float my = area.y + area.height * 0.2f;
            SbAdd(TabGit, norepo, new UiLabel("⎇", 0.06f) { Rect = new Rect(area.x, my, area.width, 0.07f), Align = TextAlignmentOptions.Center, Color = t.TextDim });
            _sbNoRepoTitle = SbAdd(TabGit, norepo, new UiLabel("NOT A GIT REPOSITORY", t.TitleSize) { Rect = new Rect(area.x, my + 0.08f, area.width, 0.036f), Align = TextAlignmentOptions.Center, UseDisplay = true, Color = t.Text });
            _sbNoRepoText = SbAdd(TabGit, norepo, new UiLabel("", 0.0145f) { Rect = new Rect(area.x + 0.04f, my + 0.125f, area.width - 0.08f, 0.08f), Align = TextAlignmentOptions.Top, Wrap = true, Color = t.TextDim });
            SbAdd(TabGit, norepo, new UiButton("INIT GIT", () => SbInitGit(), UiButtonStyle.Primary) { Rect = new Rect(area.center.x - 0.1f, my + 0.22f, 0.2f, 0.046f), TextSize = t.BodySize });
            _sbGitMsg = SbAdd(TabGit, msg, new UiLabel("", 0.0145f) { Rect = new Rect(area.x + 0.03f, area.y + area.height * 0.3f, area.width - 0.06f, 0.12f), Align = TextAlignmentOptions.Top, Wrap = true, Color = t.TextDim });
            SbAdd(TabGit, msg, new UiButton("⟳ TRY AGAIN", () => SbRefreshChanges(true)) { Rect = new Rect(area.center.x - 0.08f, area.y + area.height * 0.3f + 0.13f, 0.16f, bh) });

            // STATUS
            _sbStatus = SbAdd(TabStatus, null, new UiRows { Rect = area, NameColumn = 0.105f, RowHeight = 0.036f, Empty = "Open a thread." });

            // VIEWS
            SbAdd(TabViews, null, new UiLabel("Agents draw here: charts, diagrams, tables, checklists, choices you answer, small physics scenes. Ask in the thread, e.g. \"show the architecture as a diagram\".", 0.0135f) { Rect = new Rect(area.x + 0.004f, area.y, area.width - 0.008f, 0.05f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _sbViews = SbAdd(TabViews, null, new UiRows { Rect = new Rect(area.x, area.y + 0.05f + g, area.width, area.height - 0.05f - bh - g * 2), OnPick = (row, i) => { if (row.Tag is string id) OpenView(id); }, Empty = "No views from this thread yet." });
            SbButtons(TabViews, null, new Rect(area.x, area.yMax - bh, area.width, bh),
                ("OPEN ALL", () => { foreach (var x in WsViews()) OpenView(x.Id); }, 1f),
                ("CLOSE ALL", () => { foreach (var x in WsViews()) ClosePanel(x.PanelId); RefreshWorkspaceViews(); }, 1f),
                ("DEMO", ShowDemoView, 1f));

            _sbTabs.OnChanged = SbTabChanged;
            p.Tick += _ => SbTick();
            p.Opened += _ => SbRefreshTab(_sbTabs.Selected);
            LoadSidebarMemory();
            // built again (RebuildPanels): the data stays, the lists and the views are drawn again; a file view starts over
            _sbDiffPath = null;
            _sbFile = null;
            SbShow();
            SbRenderChanges();
            SbShowGit();
            RefreshWorkspaceViews();
            if (!_sbHooked)
            {
                _sbHooked = true;
                Router.Clicked += (panel, el) => _sbKeyFocus = panel != null && panel == _sb;
                MetaCodex.App.TerminalInput.Intercept = SidebarPhysicalKey;
            }
        }

        /// Shows the elements of the chosen tab in its current mode (list or file, repo or not).
        private void SbShow()
        {
            if (_sbTabs == null) return;
            int sel = _sbTabs.Selected;
            foreach (var (el, tab, when) in _sbEls) el.SetVisible(tab == sel && (when == null || when()));
        }

        /// A tab chosen (by the user, a command): remembered for the thread, then shown.
        private void SbTabChanged(int i)
        {
            if (_sbThread != null && _sb != null && _sb.IsOpen) { _sbMem[_sbThread] = (true, i); SaveSidebarMemory(); }
            SbRefreshTab(i);
        }

        /// Shows a tab and refreshes what it shows (nothing is remembered: a new thread or folder calls this too).
        private void SbRefreshTab(int i)
        {
            SbShow();
            if (_sb == null || !_sb.IsOpen) return;
            switch (i)
            {
                case TabChanges: SbRenderChanges(); if (Time.unscaledTime > _sbNextChanges - 1.5f) SbRefreshChanges(); break;
                case TabFiles: SbRefreshTree(); break;
                case TabGit: SbShowGit(); SbRefreshGit(true); break;
                case TabStatus: SbRenderStatus(); break;
                case TabViews: RefreshWorkspaceViews(); break;
            }
        }

        // ------------------------------------------------------------------ open / close, per thread

        /// Opens or folds the sidebar of the open thread (a tab: changes, files, git, status, views). Remembered per thread.
        public bool SetSidebar(bool open, int tab = -1, bool remember = true)
        {
            if (_sb == null) return false;
            bool thread = _threadOpen != null && _panels.TryGetValue("thread", out var th) && th.IsOpen;
            if (open && !thread) return false;
            if (tab >= 0) _sbTabs.Select(tab);
            if (open && !_sb.IsOpen)
            {
                _sb.gameObject.SetActive(true);
                UpdateSidebarLink(true);
                _sb.Open();
                if (Floating && _carousel != null && _panels.TryGetValue("thread", out var t2)) Slot(t2.transform); // the pair needs more room on the ring
                else if (!Floating) CentreThreadPair(); // FIXED: the pair centred where the user faces, when there is room
            }
            else if (!open && _sb.IsOpen)
            {
                _sb.Close();
                if (Floating && _carousel != null && _panels.TryGetValue("thread", out var t3) && t3.IsOpen) Slot(t3.transform);
                else if (!Floating) UncentreThreadPair(); // back where it was, unless the user moved it
            }
            if (remember && _sbThread != null) { _sbMem[_sbThread] = (open, _sbTabs.Selected); SaveSidebarMemory(); }
            UpdateSidebarHandle();
            UpdateSidebarVoice();
            return true;
        }

        private void ToggleSidebar() => SetSidebar(!(_sb != null && _sb.IsOpen));

        /// What this thread had: open or folded, and the tab (new threads start folded, on CHANGES).
        private (bool open, int tab) SidebarMemory(string threadId) => threadId != null && _sbMem.TryGetValue(threadId, out var m) ? m : (false, TabChanges);

        private void LoadSidebarMemory()
        {
            _sbMem.Clear();
            foreach (var s in Settings.threadSidebar ?? new List<string>())
            {
                var f = s.Split('|');
                if (f.Length == 3 && int.TryParse(f[2], out int tab)) _sbMem[f[0]] = (f[1] == "1", Mathf.Clamp(tab, 0, SidebarTabs.Length - 1));
            }
        }

        private void SaveSidebarMemory()
        {
            var list = Settings.threadSidebar ?? (Settings.threadSidebar = new List<string>());
            string prefix = _sbThread + "|";
            list.RemoveAll(x => x.StartsWith(prefix, StringComparison.Ordinal));
            if (_sbThread != null && _sbMem.TryGetValue(_sbThread, out var m)) list.Add($"{_sbThread}|{(m.open ? 1 : 0)}|{m.tab}");
            while (list.Count > 80) list.RemoveAt(0);
            Settings.Save(_settingsPath);
        }

        private void UpdateSidebarHandle()
        {
            int n = _sbFiles.Count(f => f.ByThread);
            _thHandle?.Set(_sb != null && _sb.IsOpen, n > 0 ? (n > 99 ? "99+" : n.ToString()) : "");
        }

        /// The open thread (and its folder) whose sidebar this is. A new thread or folder drops everything shown before.
        private void SbSetThread(string threadId, string root)
        {
            root = string.IsNullOrEmpty(root) ? null : root.Replace('\\', '/').TrimEnd('/');
            if (root != null && root.Length == 0) root = "/";
            bool newThread = threadId != _sbThread, newRoot = root != _sbRoot;
            if (!newThread && !newRoot) return;
            _sbGen++;
            if (newThread)
            {
                _sbThread = threadId;
                _touched = new Dictionary<string, ChangedFile>();
                _sbTouchSeq = 0;
                var m = SidebarMemory(threadId);
                if (_sbTabs != null && _sbTabs.Selected != m.tab) { var keep = _sbTabs.OnChanged; _sbTabs.OnChanged = null; _sbTabs.Select(m.tab); _sbTabs.OnChanged = keep; }
                RefreshWorkspaceViews();
            }
            _sbRoot = root;
            _sbFiles = new List<ChangedFile>();
            _sbSnap = null;
            _sbGit = null;
            _sbChBusy = _sbGitBusy = false;
            _sbDiffPath = null;
            _sbDiffSig = null;
            _sbFile = null;
            _sbDir = root;
            _sbDiff?.SetMessage("");
            _sbCode?.SetMessage("");
            _sbChList?.SetRows(new UiRows.Row[0]);
            _sbTree?.SetRows(new UiRows.Row[0]);
            _sbNextChanges = 0f;
            if (_sb != null) _sb.SetTitle("Project · " + (root == null ? "no folder yet" : root == "/" ? "/" : root.Split('/').Last()));
            SbShow();
            SbRenderChanges();
            SbShowGit();
            UpdateSidebarHandle();
            UpdateSidebarVoice();
            if (root != null) SbRefreshChanges(true);
            if (_sb != null && _sb.IsOpen) SbRefreshTab(_sbTabs.Selected);
        }

        /// A path as the thread's chat and sidebar show it: relative to the project folder when inside it.
        private string SbRel(string path) => ThreadChanges.Rel(_sbRoot, path);
        private string SbAbs(string rel) => rel == null ? null : rel.StartsWith("/") || _sbRoot == null ? rel : _sbRoot.TrimEnd('/') + "/" + rel;
        private string SbTop => _sbSnap != null && _sbSnap.Repo && _sbSnap.Top.Length > 0 ? _sbSnap.Top : null;
        private bool SbOwnFolder => _threadInfo?["environmentIsWorktree"]?.Type == JTokenType.Boolean && (bool)_threadInfo["environmentIsWorktree"];
        private string SbBaseBranch => SbOwnFolder ? WorkspaceMap.GroupOf(_spaces, _sbThread)?.Base : null;
        private bool SbWorking => _thPulse != null && _thPulse.Working || ThreadTimeline.IsWorking(StatusOfRow(_threadInfo));

        // ------------------------------------------------------------------ CHANGES

        /// The timeline's file-change and edit rows: the thread's own files. New edits refresh the list (and the open diff) soon.
        private void WsTrackRows(JArray rows) => WsTrackFound(ThreadChanges.FromTimeline(rows, _sbRoot));

        /// The files the timeline shows changing (made on the pool thread with the timeline, PrepareTimeline).
        private void WsTrackFound(Dictionary<string, ChangedFile> found)
        {
            long seq = found.Values.Select(f => f.Seq).DefaultIfEmpty(0).Max();
            bool fresh = seq > _sbTouchSeq || found.Count != _touched.Count;
            _touched = found;
            if (!fresh) return;
            _sbTouchSeq = seq;
            if (_sbRoot == null || _sbSnap == null || !_sbSnap.Repo) { SbMergeFiles(); SbRenderChanges(); }
            _sbChangesAt = Time.unscaledTime + 0.8f; // git again once the edit settled
        }

        /// git's view of the folder (one shell run) merged with the timeline's files; then the list and the open diff.
        private void SbRefreshChanges(bool now = false)
        {
            if (_sbRoot == null) { SbMergeFiles(); SbRenderChanges(); return; }
            if (_sbChBusy) { _sbChAgain = true; return; }
            if (!now && Time.unscaledTime < _sbNextChanges) return;
            _sbChBusy = true;
            _sbChAgain = false;
            _sbNextChanges = Time.unscaledTime + (SbWorking ? 4f : 20f);
            int gen = _sbGen;
            string root = _sbRoot, baseBranch = SbBaseBranch;
            var files = Files;
            _sbPending++;
            Task.Run(async () =>
            {
                GitSnapshot snap;
                try { snap = ThreadChanges.ParseScript((await Git.Shell(ThreadChanges.Script(root, baseBranch), 20000)).output); }
                catch (Exception ex) { snap = new GitSnapshot { Error = ex.Message }; }
                if (snap.Repo && snap.Status.Any(s => s.code == "??"))
                {
                    // untracked files: their lines, read here (no git process per file)
                    var counts = new Dictionary<string, (int, bool, long)>();
                    foreach (var (code, rp) in snap.Status.Where(s => s.code == "??").Take(400)) counts[rp] = CountLines(files?.HostPath(snap.Top.TrimEnd('/') + "/" + rp));
                    snap.Numstat.Remove("");
                    foreach (var kv in counts) snap.Numstat[kv.Key] = (kv.Value.Item1, 0, kv.Value.Item2);
                }
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    _sbChBusy = false;
                    if (gen != _sbGen) return; // another thread or folder by now
                    // each step timed (0.7.19 on the headset: this callback took 4.8-6.8 s every ~13 s)
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _sbSnap = snap;
                    SbMergeFiles();
                    double tMerge = sw.Elapsed.TotalMilliseconds;
                    // the lists only while the sidebar shows (it rebuilt them every 4 s, hidden too); opening it renders them
                    bool shown = _sb != null && _sb.IsOpen;
                    if (shown) SbRenderChanges();
                    double tRender = sw.Elapsed.TotalMilliseconds;
                    if (shown) SbShowGit();
                    double tGit = sw.Elapsed.TotalMilliseconds;
                    UpdateSidebarVoice();
                    double tVoice = sw.Elapsed.TotalMilliseconds;
                    if (shown && _sbDiffPath != null) SbRefreshOpenDiff();
                    double tDiff = sw.Elapsed.TotalMilliseconds;
                    if (_sbTabs != null && _sbTabs.Selected == TabGit && _sb.IsOpen) SbRefreshGit();
                    if (_sbChAgain) SbRefreshChanges(true);
                    double tAll = sw.Elapsed.TotalMilliseconds;
                    if (tAll > 40)
                        Log($"[slow sidebar] {tAll:F0} ms ({snap?.Status?.Count ?? 0} status, {_sbFiles.Count} files, {_touched.Count} by the thread): merge {tMerge:F0}, render {tRender - tMerge:F0}, git {tGit - tRender:F0}, voice {tVoice - tGit:F0}, diff {tDiff - tVoice:F0}, rest {tAll - tDiff:F0}");
                });
            });
        }

        /// Lines of a text file (binary: 0), up to 4 MB read.
        private static (int lines, bool binary, long size) CountLines(string hostPath)
        {
            try
            {
                var fi = new FileInfo(hostPath ?? "");
                if (!fi.Exists) return (0, false, -1);
                using (var fs = fi.OpenRead())
                {
                    var buf = new byte[65536];
                    int n, lines = 0, total = 0;
                    byte last = (byte)'\n';
                    bool first = true;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0 && total < 4 << 20)
                    {
                        if (first && CodeText.LooksBinary(buf, n)) return (0, true, fi.Length);
                        first = false;
                        for (int i = 0; i < n; i++) if (buf[i] == '\n') lines++;
                        last = buf[n - 1];
                        total += n;
                    }
                    return (lines + (total > 0 && last != '\n' ? 1 : 0), false, fi.Length);
                }
            }
            catch (Exception) { return (0, false, -1); }
        }

        private void SbMergeFiles()
        {
            var snap = _sbRoot == null ? null : _sbSnap;
            _sbFiles = ThreadChanges.Merge(snap, _touched, SbOwnFolder);
            UpdateSidebarHandle();
        }

        private static Color LetterColor(string letter, UiTheme t)
        {
            switch (letter)
            {
                case "A": case "U": return UiTheme.Hex(0x73C991);
                case "D": return UiTheme.Hex(0xF14C4C);
                case "M": return UiTheme.Hex(0xE2C08D);
                default: return t.TextDim;
            }
        }

        private void SbRenderChanges()
        {
            if (_sbChList == null) return;
            var t = UiTheme.Current;
            bool repo = _sbSnap != null && _sbSnap.Repo;
            string empty = _sbRoot == null ? "This thread has no folder yet: MQH makes it when the first turn starts." :
                _sbSnap == null && _sbChBusy ? "Reading the folder …" :
                _sbSnap?.Error != null ? "⚠ " + _sbSnap.Error :
                repo ? "No changes against " + (_sbSnap.BaseLabel == "HEAD" ? "the last commit" : _sbSnap.BaseLabel) + ": files the agent edits show up here, live." :
                "No edits yet: files the agent changes show up here, live (this folder is not a git repository).";
            string summary = _sbRoot == null ? "no folder yet" : _sbSnap == null && _sbChBusy && _sbFiles.Count == 0 ? "reading git …" : ThreadChanges.Summary(_sbFiles, _sbRoot == null ? null : _sbSnap);
            // the rows again only when what they show changed (each git refresh came here: every 4 s while the agent works)
            int sig = ChangesSig(_sbFiles, empty, out int nMine, out int nOthers);
            int expect = nMine + nOthers + (nMine > 0 && nOthers > 0 ? 1 : 0) + (nOthers > 0 ? 1 : 0);
            if (ReferenceEquals(_sbChSigList, _sbChList) && sig == _sbChSig && _sbChList.Items.Count == expect && _sbChList.Empty == empty)
            {
                _sbChSummary?.Set(summary, t.TextDim);
                return;
            }
            _sbChSigList = _sbChList;
            _sbChSig = sig;
            var rows = new List<UiRows.Row>(expect);
            var mine = _sbFiles.Where(f => f.ByThread).ToList();
            var others = _sbFiles.Where(f => !f.ByThread).ToList();
            UiRows.Row R(ChangedFile f) => new UiRows.Row
            {
                Icon = f.Kind == "delete" ? "−" : f.Kind == "add" || f.Kind == "untracked" ? "+" : "●",
                IconColor = LetterColor(f.Letter, t),
                Name = f.Name,
                Detail = f.Dir,
                Right = f.Binary ? "binary" : $"<color=#{UiViz.Hex(t.Ok)}>+{f.Added}</color> <color=#{UiViz.Hex(t.Danger)}>−{f.Removed}</color>",
                Badge = f.Letter,
                BadgeColor = LetterColor(f.Letter, t),
                Dim = !f.ByThread || f.Committed && !f.InGit,
                Tag = f,
            };
            if (mine.Count > 0 && others.Count > 0) rows.Add(new UiRows.Row { Header = true, Name = "THIS THREAD" });
            rows.AddRange(mine.Select(R));
            if (others.Count > 0) { rows.Add(new UiRows.Row { Header = true, Name = "ALSO CHANGED IN THIS FOLDER" }); rows.AddRange(others.Select(R)); }
            _sbChList.Empty = empty;
            _sbChList.SetRows(rows, keepScroll: true);
            _sbChSummary?.Set(summary, t.TextDim);
        }

        private UiRows _sbChSigList;
        private int _sbChSig;

        /// Every field a row of CHANGES shows or opens (a hash: no strings built).
        private static int ChangesSig(List<ChangedFile> files, string empty, out int mine, out int others)
        {
            mine = others = 0;
            unchecked
            {
                int h = 17 * 31 + (empty?.GetHashCode() ?? 0);
                foreach (var f in files)
                {
                    if (f.ByThread) mine++; else others++;
                    h = h * 31 + (f.Path?.GetHashCode() ?? 0);
                    h = h * 31 + (f.RepoPath?.GetHashCode() ?? 0);
                    h = h * 31 + (f.Kind?.GetHashCode() ?? 0);
                    h = h * 31 + (f.GitCode?.GetHashCode() ?? 0);
                    h = h * 31 + (f.TimelineDiff?.GetHashCode() ?? 0);
                    h = h * 31 + f.Added;
                    h = h * 31 + f.Removed;
                    h = h * 31 + f.Seq.GetHashCode();
                    h = h * 31 + f.Size.GetHashCode();
                    h = h * 31 + (f.Binary ? 1 : 0) + (f.ByThread ? 2 : 0) + (f.InGit ? 4 : 0) + (f.Committed ? 8 : 0);
                }
                return h;
            }
        }

        private void SbOpenDiff(ChangedFile f)
        {
            if (f == null) return;
            _sbDiffPath = f.Path;
            _sbDiffSig = DiffSig(f);
            SbShow();
            SbLabelDiff(f);
            _sbDiff.SetMessage("loading the diff …");
            SbLoadDiff(f, false);
        }

        private void SbCloseDiff()
        {
            _sbDiffPath = null;
            _sbDiffGen++;
            SbShow();
            SbRenderChanges();
        }

        private void SbLabelDiff(ChangedFile f)
        {
            var t = UiTheme.Current;
            string state = f.Kind == "untracked" ? "untracked" : f.Kind == "add" ? "added" : f.Kind == "delete" ? "deleted" : f.Committed && !f.InGit ? "committed" : "modified";
            _sbDiffLabel?.Set($"<b>{UiViz.NoParse(f.Name)}</b>  <color=#{UiViz.Hex(t.TextDim)}>{UiViz.NoParse(f.Dir.Length > 0 ? f.Dir + "/" : "")}</color>  " +
                (f.Binary ? "binary" : $"<color=#{UiViz.Hex(t.Ok)}>+{f.Added}</color> <color=#{UiViz.Hex(t.Danger)}>−{f.Removed}</color>") + $"  <color=#{UiViz.Hex(LetterColor(f.Letter, t))}>{state}</color>", t.Text);
        }

        /// The open file's diff: git's (the whole file as context, folded) when git sees a change, else the timeline's,
        /// else the file as it is now.
        private void SbLoadDiff(ChangedFile f, bool keepPlace)
        {
            int gen = ++_sbDiffGen, tgen = _sbGen;
            string top = SbTop, baseRef = _sbSnap?.Base, path = f.Path, root = _sbRoot;
            bool viaGit = top != null && f.RepoPath != null && (f.InGit || f.Committed && _sbSnap?.NameStatus.ContainsKey(f.RepoPath) == true);
            var files = Files;
            _sbPending++;
            Task.Run(async () =>
            {
                SourceDoc doc;
                try
                {
                    if (f.Binary) doc = SourceDoc.Text($"Binary file{(f.Size > 0 ? " · " + FileService.Size(f.Size) : "")}: no text diff.");
                    else if (viaGit)
                    {
                        long size = f.Size >= 0 ? f.Size : FileSize(files?.HostPath(top.TrimEnd('/') + "/" + f.RepoPath));
                        bool big = f.Added + f.Removed > 20000 || size > 1_500_000;
                        var r = await Git.Git(top, ThreadChanges.DiffArgs(f, baseRef, big), 30000);
                        var d = DiffDoc.Parse(r.output);
                        doc = d.Lines.Count == 0 && !d.Binary && !string.IsNullOrEmpty(f.TimelineDiff)
                            ? SourceDoc.FromDiff(path, DiffDoc.Parse(f.TimelineDiff), "the agent's last edit (git sees no change now)")
                            : SourceDoc.FromDiff(path, d, big ? "a big file: only the changed parts" : null);
                        if (d.Lines.Count == 0 && !d.Binary && r.output.Trim().Length > 0 && !r.output.Contains("diff ")) doc = SourceDoc.Text("⚠ " + r.output.Trim());
                    }
                    else if (!string.IsNullOrEmpty(f.TimelineDiff)) doc = SourceDoc.FromDiff(path, DiffDoc.Parse(f.TimelineDiff), root != null && top == null ? "the agent's edit (no git here)" : "the agent's edit");
                    else
                    {
                        var (text, note, err) = ReadText(files?.HostPath(SbAbsFor(root, path)));
                        doc = err != null ? SourceDoc.Text(err) : SourceDoc.Code(path, text, "no diff recorded for this edit: the file as it is now" + (note != null ? " · " + note : ""));
                    }
                }
                catch (Exception ex) { doc = SourceDoc.Text("⚠ " + ex.Message); }
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    if (gen != _sbDiffGen || tgen != _sbGen || _sbDiffPath != path) return;
                    _sbDiff.SetDoc(doc, keepPlace);
                    var cur = _sbFiles.FirstOrDefault(x => x.Path == path);
                    if (cur != null) SbLabelDiff(cur);
                });
            });
        }

        private static string SbAbsFor(string root, string rel) => rel.StartsWith("/") || root == null ? rel : root.TrimEnd('/') + "/" + rel;

        private static long FileSize(string host) { try { var fi = new FileInfo(host ?? ""); return fi.Exists ? fi.Length : -1; } catch (Exception) { return -1; } }

        /// After a refresh: the open diff again when its numbers changed (the agent edited it), keeping the place.
        private void SbRefreshOpenDiff()
        {
            var f = _sbFiles.FirstOrDefault(x => x.Path == _sbDiffPath);
            if (f == null) { SbLabelDiffGone(); return; }
            string sig = DiffSig(f);
            if (sig == _sbDiffSig) return;
            _sbDiffSig = sig;
            SbLoadDiff(f, true);
        }

        private static string DiffSig(ChangedFile f) => $"{f.Added}/{f.Removed}/{f.Seq}/{f.GitCode}/{f.Kind}";

        private void SbLabelDiffGone() => _sbDiffLabel?.Set($"<b>{UiViz.NoParse(_sbDiffPath)}</b>  <color=#{UiViz.Hex(UiTheme.Current.TextDim)}>no longer changed</color>");

        private void SbStepFile(int dir)
        {
            if (_sbFiles.Count == 0) return;
            int i = _sbFiles.FindIndex(f => f.Path == _sbDiffPath);
            i = i < 0 ? 0 : (i + dir + _sbFiles.Count) % _sbFiles.Count;
            SbOpenDiff(_sbFiles[i]);
        }

        private void SbToggleWrap(UiSourceView v, UiButton b)
        {
            if (v == null) return;
            v.SetWrap(!v.Wrap);
            b?.SetSelected(v.Wrap);
        }

        private void MentionInComposer(string path)
        {
            if (_thComposer == null) return;
            string cur = (_thComposer.Text ?? "").TrimEnd();
            _thComposer.Set((cur.Length > 0 ? cur + " " : "") + path + " ");
            Toast("Added to the message: " + path, "info");
        }

        // ------------------------------------------------------------------ FILES

        private void SbRefreshTree()
        {
            if (_sbTree == null) return;
            var t = UiTheme.Current;
            _sbNextTree = Time.unscaledTime + 5f;
            if (_sbRoot == null) { _sbCrumbs.Set(new[] { "no folder yet" }); _sbTree.Empty = "This thread has no folder yet: MQH makes it when the first turn starts."; _sbTree.SetRows(new UiRows.Row[0]); return; }
            if (_sbDir == null || !(_sbDir == _sbRoot || _sbDir.StartsWith(_sbRoot.TrimEnd('/') + "/"))) _sbDir = _sbRoot; // never above the project folder
            string rootName = _sbRoot == "/" ? "/" : _sbRoot.Split('/').Last();
            var parts = new List<string> { rootName };
            string rel = ThreadChanges.Rel(_sbRoot, _sbDir);
            if (_sbDir != _sbRoot) parts.AddRange(rel.Split('/').Where(x => x.Length > 0));
            _sbCrumbs.Set(parts);
            var entries = Files.List(_sbDir, out string err);
            var rows = new List<UiRows.Row>();
            var changed = _sbFiles.ToDictionary(f => f.Path, f => f);
            if (_sbDir != _sbRoot) rows.Add(new UiRows.Row { Icon = "↰", Name = "..", Detail = "up to " + (parts.Count > 1 ? parts[parts.Count - 2] : rootName), Tag = new FileEntry { Name = "..", LinuxPath = FileService.Parent(_sbDir), IsDir = true } });
            foreach (var e in entries)
            {
                if (e.Name == ".git") continue;
                string r = ThreadChanges.Rel(_sbRoot, e.LinuxPath);
                changed.TryGetValue(r, out var cf);
                bool inside = e.IsDir && _sbFiles.Any(f => f.Path.StartsWith(r + "/", StringComparison.Ordinal));
                rows.Add(new UiRows.Row
                {
                    Icon = e.IsDir ? "▸" : "·",
                    IconColor = e.IsDir ? t.TextAccent : t.TextDim,
                    Name = e.Name + (e.IsDir ? "/" : ""),
                    NameColor = cf != null ? LetterColor(cf.Letter, t) : e.IsDir ? t.TextAccent : (Color?)null,
                    Dim = e.Name.StartsWith("."),
                    Right = e.IsDir ? "" : FileService.Size(e.Size),
                    Badge = cf != null ? cf.Letter : inside ? "●" : "",
                    BadgeColor = cf != null ? LetterColor(cf.Letter, t) : t.Warn,
                    Tag = e,
                });
            }
            _sbTree.Empty = err != null ? "⚠ " + err : "(an empty folder)";
            _sbTree.SetRows(rows, keepScroll: true);
        }

        private void SbCrumbPicked(int depth)
        {
            if (_sbRoot == null) return;
            var parts = ThreadChanges.Rel(_sbRoot, _sbDir).Split('/').Where(x => x.Length > 0).ToList();
            _sbDir = depth <= 0 || _sbDir == _sbRoot ? _sbRoot : _sbRoot.TrimEnd('/') + "/" + string.Join("/", parts.Take(depth));
            SbRefreshTree();
        }

        private void SbTreePicked(FileEntry e)
        {
            if (e == null) return;
            if (e.IsDir) { _sbDir = e.LinuxPath; SbRefreshTree(); return; }
            SbOpenFile(e.LinuxPath);
        }

        /// A file in the read-only code view (read off the main thread; big ones cut at 2 MB, binaries named).
        private void SbOpenFile(string linuxPath)
        {
            if (linuxPath == null) return;
            _sbFile = linuxPath;
            SbShow();
            var t = UiTheme.Current;
            string name = linuxPath.Split('/').Last();
            _sbFileLabel.Set($"<b>{UiViz.NoParse(name)}</b>  <color=#{UiViz.Hex(t.TextDim)}>{UiViz.NoParse(ThreadChanges.Rel(_sbRoot, FileService.Parent(linuxPath)))}</color>", t.Text);
            _sbCode.SetMessage("opening …");
            int tgen = _sbGen;
            string host = Files.HostPath(linuxPath);
            _sbPending++;
            Task.Run(() =>
            {
                SourceDoc doc;
                long size = FileSize(host);
                var (text, note, err) = ReadText(host);
                doc = err != null ? SourceDoc.Text(err) : SourceDoc.Code(linuxPath, text, note);
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    if (tgen != _sbGen || _sbFile != linuxPath) return;
                    _sbCode.SetDoc(doc);
                    int lines = doc.Lines?.Count ?? 0;
                    _sbFileLabel.Set($"<b>{UiViz.NoParse(name)}</b>  <color=#{UiViz.Hex(t.TextDim)}>{(size >= 0 ? FileService.Size(size) : "")}{(lines > 0 ? $" · {lines:N0} lines" : "")} · {doc.Lang.Name}</color>", t.Text);
                });
            });
        }

        /// A text file's content (up to 2 MB), or why it is not shown.
        private static (string text, string note, string error) ReadText(string host)
        {
            try
            {
                var fi = new FileInfo(host ?? "");
                if (!fi.Exists) return (null, null, "This file does not exist now (deleted or moved).");
                const int max = 2 << 20;
                int n = (int)Math.Min(fi.Length, max);
                var buf = new byte[n];
                using (var fs = fi.OpenRead()) { int read = 0; while (read < n) { int k = fs.Read(buf, read, n - read); if (k <= 0) break; read += k; } n = read; }
                if (CodeText.LooksBinary(buf, n)) return (null, null, $"Binary file · {FileService.Size(fi.Length)}: no preview.");
                string text = Encoding.UTF8.GetString(buf, 0, n);
                return (text, fi.Length > max ? $"the first {FileService.Size(max)} of {FileService.Size(fi.Length)}" : null, null);
            }
            catch (Exception ex) { return (null, null, "⚠ " + ex.Message); }
        }

        /// DIFF from a file: CHANGES on it when the thread (or git) changed it.
        private void SbDiffOfOpenFile()
        {
            if (_sbFile == null) return;
            string rel = ThreadChanges.Rel(_sbRoot, _sbFile);
            var f = _sbFiles.FirstOrDefault(x => x.Path == rel);
            if (f == null) { Toast("No change in this file", "info"); return; }
            _sbTabs.Select(TabChanges);
            SbOpenDiff(f);
        }

        // ------------------------------------------------------------------ GIT

        private string SbGitMode => _sbRoot == null || _sbSnap == null || _sbSnap.Error != null ? "msg" : _sbSnap.Repo ? "repo" : "norepo";

        private void SbShowGit()
        {
            if (_sbGitHead == null) return;
            var t = UiTheme.Current;
            string mode = SbGitMode;
            if (mode == "msg")
                _sbGitMsg.Set(_sbRoot == null ? "This thread has no folder yet: MQH makes it when the first turn starts." :
                    _sbSnap == null ? "reading git …" : "⚠ " + _sbSnap.Error, _sbSnap?.Error != null ? t.Danger : t.TextDim);
            else if (mode == "norepo")
                _sbNoRepoText.Set($"<color=#{UiViz.Hex(t.TextAccent)}>{UiViz.NoParse(_sbRoot)}</color>\nis not under version control. INIT GIT starts a repository here (git init): changes, branches and commits then show up in this tab.", t.TextDim);
            else
            {
                var st = _sbGit;
                string pre = string.IsNullOrEmpty(_sbSnap.Prefix) ? "" : $"   <color=#{UiViz.Hex(t.TextDim)}>repo {UiViz.NoParse(_sbSnap.Top.Split('/').Last())}/, this folder: {UiViz.NoParse(_sbSnap.Prefix)}</color>";
                if (st == null) _sbGitHead.Set($"⎇ <b>{UiViz.NoParse(_sbSnap.Branch)}</b>  <color=#{UiViz.Hex(t.TextDim)}>reading …</color>" + pre, t.Text);
                else if (st.Error != null) _sbGitHead.Set("⚠ " + UiViz.NoParse(Cut(st.Error, 120)), t.Danger);
                else
                {
                    string ab = (st.Ahead > 0 ? $"  <color=#{UiViz.Hex(t.Ok)}>↑{st.Ahead}</color>" : "") + (st.Behind > 0 ? $"  <color=#{UiViz.Hex(t.Warn)}>↓{st.Behind}</color>" : "");
                    string up = string.IsNullOrEmpty(st.Upstream) ? "no upstream" : UiViz.NoParse(st.Upstream);
                    _sbGitHead.Set($"⎇ <b>{UiViz.NoParse(st.Branch)}</b>{ab}  <color=#{UiViz.Hex(t.TextDim)}>{up}</color>  " +
                        (st.Entries.Count == 0 ? $"<color=#{UiViz.Hex(t.Ok)}>clean</color>" : $"<color=#{UiViz.Hex(t.Warn)}>{st.Entries.Count} changed</color>") + pre, t.Text);
                    var rows = new List<UiRows.Row> { new UiRows.Row { Header = true, Name = st.Entries.Count == 0 ? "STATUS" : $"STATUS · {st.Entries.Count}" } };
                    foreach (var e in st.Entries)
                    {
                        string letter = e.Code == "??" ? "U" : e.Code.Contains("D") ? "D" : e.Code.Contains("A") ? "A" : e.Code.Contains("U") ? "!" : e.Code.Contains("R") ? "R" : "M";
                        int k = e.Path.LastIndexOf('/');
                        rows.Add(new UiRows.Row
                        {
                            Icon = letter == "D" ? "−" : letter == "U" || letter == "A" ? "+" : "●", IconColor = LetterColor(letter, t),
                            Name = k >= 0 ? e.Path.Substring(k + 1) : e.Path, Detail = k >= 0 ? e.Path.Substring(0, k) : "",
                            Right = e.Code == "??" ? "untracked" : e.Code.Length == 2 && e.Code[0] != ' ' ? "staged" : "", Badge = letter, BadgeColor = LetterColor(letter, t), Tag = e.Path,
                        });
                    }
                    if (st.Entries.Count == 0) rows.Add(new UiRows.Row { Icon = "✓", IconColor = t.Ok, Name = "Clean", Detail = "nothing to commit", Dim = true });
                    _sbGitStatus.SetRows(rows, keepScroll: true);
                    var log = new List<UiRows.Row> { new UiRows.Row { Header = true, Name = "RECENT COMMITS" } };
                    foreach (var cm in st.Log.Take(40))
                        log.Add(new UiRows.Row
                        {
                            Icon = "●", IconColor = cm.Refs.Contains("HEAD") ? t.Accent : t.Frame,
                            Name = cm.Subject, Detail = cm.Refs.Replace("HEAD -> ", "").Trim(),
                            Right = $"<color=#{UiViz.Hex(t.TextAccent)}>{cm.Hash}</color> {ShortWhen(cm.When)}",
                            Tag = cm.Hash,
                        });
                    if (st.Log.Count == 0) log.Add(new UiRows.Row { Icon = "·", Name = "No commits yet", Detail = "COMMIT… makes the first one", Dim = true });
                    _sbGitLog.SetRows(log, keepScroll: true);
                }
            }
            SbShow();
        }

        private static string ShortWhen(string w) => (w ?? "").Replace(" seconds", " s").Replace(" second", " s").Replace(" minutes", " min").Replace(" minute", " min").Replace(" hours", " h").Replace(" hour", " h").Replace(" days", " d").Replace(" day", " d").Replace(" weeks", " wk").Replace(" week", " wk").Replace(" months", " mo").Replace(" month", " mo").Replace(" years", " y").Replace(" year", " y");

        private void SbRefreshGit(bool now = false)
        {
            string top = SbTop;
            if (top == null || _sbGitBusy) return;
            if (!now && Time.unscaledTime < _sbNextGit) return;
            _sbGitBusy = true;
            _sbNextGit = Time.unscaledTime + (SbWorking ? 8f : 30f);
            int gen = _sbGen;
            _sbPending++;
            Task.Run(async () =>
            {
                GitStatus st;
                try { st = await Git.Status(top); }
                catch (Exception ex) { st = new GitStatus { Repo = top, Error = ex.Message }; }
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    _sbGitBusy = false;
                    if (gen != _sbGen) return;
                    _sbGit = st;
                    SbShowGit();
                });
            });
        }

        private void SbGitFilePicked(string repoPath)
        {
            if (repoPath == null) return;
            string pre = _sbSnap?.Prefix ?? "";
            string rel = pre.Length > 0 && repoPath.StartsWith(pre) ? repoPath.Substring(pre.Length) : repoPath;
            var f = _sbFiles.FirstOrDefault(x => x.Path == rel || x.RepoPath == repoPath);
            if (f == null) { Toast("Outside this project folder: " + repoPath, "info"); return; }
            _sbTabs.Select(TabChanges);
            SbOpenDiff(f);
        }

        private void SbGitRun(string args)
        {
            string top = SbTop;
            if (top == null) { Toast("Not a git repository", "warn"); return; }
            string verb = args.Split(' ')[0];
            Toast("git " + verb + " …", "info");
            _sbPending++;
            Task.Run(async () =>
            {
                var r = await Git.Git(top, args, 90000);
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    Toast(r.code == 0 ? "git " + verb + " done" : "git " + verb + ": " + Last(r.output), r.code == 0 ? "ok" : "error");
                    SbRefreshGit(true);
                    SbRefreshChanges(true);
                });
            });
        }

        private void SbCommitPrompt()
        {
            string top = SbTop;
            if (top == null) return;
            UiKeyboard.Edit("", false, null, msg =>
            {
                if (string.IsNullOrWhiteSpace(msg)) { Toast("Commit cancelled (empty message)", "warn"); return; }
                SbGitRun("add -A && git -C " + GitService.Quote(top) + " commit -m " + GitService.Quote(msg.Trim()));
            }, "COMMIT MESSAGE");
        }

        /// INIT GIT: `git init` in the thread's project folder (only offered when it is in no repository).
        public string SbInitGit()
        {
            string root = _sbRoot;
            if (root == null) return "error: this thread has no folder yet";
            if (_sbSnap == null || _sbSnap.Repo || _sbSnap.Error != null) return _sbSnap != null && _sbSnap.Repo ? "error: the folder is a git repository already" : "error: the folder's git state is not known yet";
            int gen = _sbGen;
            Toast("git init in " + root.Split('/').Last() + " …", "info");
            _sbPending++;
            Task.Run(async () =>
            {
                var r = await Git.Git(root, "-c init.defaultBranch=main init", 30000);
                _main.Enqueue(() =>
                {
                    _sbPending--;
                    Toast(r.code == 0 ? "Git repository started in " + root : "git init: " + Last(r.output), r.code == 0 ? "ok" : "error");
                    if (gen != _sbGen) return;
                    _sbSnap = null;
                    SbShowGit();
                    SbRefreshChanges(true);
                });
            });
            return "Starting a git repository in " + root;
        }

        // ------------------------------------------------------------------ STATUS

        private void SbRenderStatus()
        {
            if (_sbStatus == null) return;
            var t = UiTheme.Current;
            _sbNextStatus = Time.unscaledTime + 1f;
            var x = _threadInfo;
            if (_threadOpen == null || x == null) { _sbStatus.SetRows(new UiRows.Row[0]); return; }
            var rows = new List<UiRows.Row>();
            void Add(string icon, string key, string value, Color? col = null) => rows.Add(new UiRows.Row { Icon = icon, Name = key, Detail = value ?? "", NameColor = col });
            string provider = (string)x["providerId"] ?? "agent";
            string model = ModelName(provider, CurrentModel()) ?? CurrentModel() ?? "default model", effort = CurrentEffort();
            Add("◇", "AGENT", provider + " · " + model + (string.IsNullOrEmpty(effort) || effort == "none" ? "" : " · effort " + effort));
            Add("✎", "ACCESS", PermLabel(CurrentPerm()).TrimStart('⚠', '◐', '✎', ' '));
            var u = _thUsage;
            long used = u?["usedTokens"]?.Type == JTokenType.Integer ? (long)u["usedTokens"] : -1, win = u?["modelContextWindow"]?.Type == JTokenType.Integer ? (long)u["modelContextWindow"] : -1;
            float frac = used >= 0 && win > 0 ? used / (float)win : -1f;
            Add("◔", "CONTEXT", frac < 0 ? "not known yet" : $"{frac * 100f:0}% · {K(used)} of {K(win)} tokens" + (u["estimated"]?.Type == JTokenType.Boolean && (bool)u["estimated"] ? " (estimated)" : ""), frac >= 0.9f ? t.Danger : frac >= 0.7f ? t.Warn : (Color?)null);
            var p = _thPulse;
            string st = StatusOfRow(x) ?? "";
            if (p != null)
            {
                string doing = p.Doing.StartsWith("working") ? p.Doing.Substring(7).Trim().Trim('(', ')') : p.Doing;
                string state = p.State == "working" ? $"working {Clock(p.TurnSecs)}" + (doing.Length > 0 ? " · " + doing : "") : p.State == "waiting" ? "waiting for you" : p.State == "held" ? "not started (held in the queue)" : p.State;
                Add(p.Working ? "◐" : p.State == "error" ? "⚠" : "✓", "STATE", state + (p.Working && p.QuietSecs >= 30 ? $" · quiet {Clock(p.QuietSecs)}" : ""), p.MayBeStuck || p.State == "error" ? t.Warn : p.Working ? t.Ok : (Color?)null);
                if (p.MayBeStuck) Add("⚠", "STUCK?", p.StuckWhy.Length > 0 ? p.StuckWhy : "no news for a while", t.Warn);
                Add("$", "RUNNING", !string.IsNullOrEmpty(p.CurrentCommand) ? $"{p.CurrentCommand} ({Clock(p.PhaseSecs)})" : p.Working && p.Phase != "idle" ? (p.Phase == "between" ? "between steps" : p.Phase) + $" ({Clock(p.PhaseSecs)})" : "nothing");
                Add("Σ", "THIS TURN", $"{p.Thoughts} thought{(p.Thoughts == 1 ? "" : "s")} · {p.Commands} command{(p.Commands == 1 ? "" : "s")}{(p.Failed > 0 ? $" ({p.Failed} failed)" : "")} · {p.Files} file{(p.Files == 1 ? "" : "s")} +{p.Added} −{p.Removed}" + (p.Tools > 0 ? $" · {p.Tools} tools" : ""));
                if (!string.IsNullOrEmpty(p.Queued)) Add("⏸", "QUEUE", p.Queued);
            }
            else Add("◌", "STATE", st.Length > 0 ? st : "unknown");
            var pending = (_interactions ?? new JArray()).Where(ia => (string)ia["status"] == "pending" || ia["status"] == null).ToList();
            if (pending.Count == 0) Add("✓", "APPROVALS", "none waiting");
            foreach (var ia in pending.Take(3))
            {
                var subj = ia["payload"]?["subject"];
                string what = (string)subj?["command"] ?? (string)subj?["toolName"] ?? (string)subj?["path"] ?? (string)ia["payload"]?["kind"] ?? "an action";
                Add("◆", "APPROVAL", Cut(what, 90) + " · answer it in THREAD", t.Warn);
            }
            string limits = provider != null && _usage.TryGetValue(provider, out var lim) ? ChatText.Strip(LimitsText(lim, false)) : "";
            if (limits.Length > 0) Add("▤", "USAGE", limits);
            bool wt = x["environmentIsWorktree"]?.Type == JTokenType.Boolean && (bool)x["environmentIsWorktree"];
            Add("▣", "FOLDER", ((string)x["environmentPath"] ?? "no folder yet") + " · " + (wt ? "worktree" : "checkout") + ((string)x["environmentBranchName"] is string br && br.Length > 0 ? " · ⎇ " + br : ""));
            Add(Mqh.Online ? "●" : "○", "MQH", (Mqh.Online ? "online · " + Mqh.BaseUrl : "offline: " + (Mqh.LastError ?? "not reachable at " + Mqh.BaseUrl)) + " · thread " + (st.Length > 0 ? st : "?"), Mqh.Online ? (Color?)null : t.Danger);
            _sbStatus.SetRows(rows, keepScroll: true);
        }

        private static string Clock(int secs) => secs < 0 ? "0:00" : secs >= 3600 ? $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00}";

        // ------------------------------------------------------------------ VIEWS

        private IEnumerable<AgentView> WsViews() => _viewList.Where(v => _sbThread == null || v.ThreadId == _sbThread || v.ThreadId == null).OrderByDescending(v => v.At);

        public void RefreshWorkspaceViews()
        {
            if (_sbViews == null) return;
            var t = UiTheme.Current;
            _sbViews.SetRows(WsViews().Select(v => new UiRows.Row
            {
                Icon = "▣",
                IconColor = v.Error != null ? t.Danger : t.Accent,
                Name = v.Title,
                Detail = (v.ThreadId == null ? "from Linux" : "from this thread") + " · " + v.At.ToString("HH:mm"),
                Badge = _panels.TryGetValue(v.PanelId, out var p) && p.IsOpen ? "●" : v.Answer != null ? "✓" : "",
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

        // ------------------------------------------------------------------ live

        private void SbTick()
        {
            if (_sb == null || !_sb.IsOpen) return;
            float now = Time.unscaledTime;
            if (_sbChangesAt > 0f && now >= _sbChangesAt) { _sbChangesAt = -1f; SbRefreshChanges(true); }
            else if (_sbRoot != null) SbRefreshChanges(); // no folder: nothing to ask git (it merged and drew the list every frame);
                                                          // the timeline's files come through WsTrackFound
            int tab = _sbTabs.Selected;
            if (tab == TabStatus && now >= _sbNextStatus) SbRenderStatus();
            if (tab == TabGit) SbRefreshGit();
            if (tab == TabFiles && _sbFile == null && now >= _sbNextTree && SbWorking) SbRefreshTree();
        }

        // ------------------------------------------------------------------ the link to the thread panel

        /// Right of the thread, tops level. Bent: on the thread's own cylinder (the pair reads as one curved screen);
        /// flat: turned a little towards the user round the thread's right edge.
        private (Vector3 pos, Quaternion rot) SidebarPose(UiPanel th)
        {
            float k = _sb.RestLossyScale.x / Mathf.Max(1e-5f, th.RestLossyScale.x); // the sidebar's units in the thread's
            float halfTh = th.Width * 0.5f, halfSb = _sb.Width * 0.5f * k;
            float y = th.Height * 0.5f - _sb.Height * 0.5f * k;
            float bend = th.BendNow;
            Vector3 local;
            Quaternion lrot;
            if (bend > 0.05f)
            {
                float a = (halfTh + SbGap + halfSb) / bend;
                local = new Vector3(bend * Mathf.Sin(a), y, -bend + bend * Mathf.Cos(a));
                lrot = Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f);
            }
            else
            {
                lrot = Quaternion.Euler(0f, SbYaw, 0f);
                local = new Vector3(halfTh, y, 0f) + lrot * new Vector3(SbGap + halfSb, 0f, 0f);
            }
            return (th.transform.TransformPoint(local), th.transform.rotation * lrot);
        }

        /// Every frame: the sidebar sits at the thread's right edge with the thread's bend; carried by its header, the
        /// thread comes along. It closes with the thread.
        private void UpdateSidebarLink(bool force = false)
        {
            if (_sb == null || !_sb.IsOpen && !force) return;
            if (!_panels.TryGetValue("thread", out var th) || !th.IsOpen) { if (_sb.IsOpen) _sb.Close(); return; }
            float k = _sb.RestLossyScale.x / Mathf.Max(1e-5f, th.RestLossyScale.x);
            if (Router != null && Router.IsDragging(_sb))
            {
                var want = (_sb.transform.position, _sb.transform.rotation);
                var (_, fr) = SidebarPose(th);
                th.transform.rotation = want.rotation * Quaternion.Inverse(fr) * th.transform.rotation;
                var (fp, _) = SidebarPose(th);
                th.transform.position += want.position - fp;
                _sbDragged = true;
                return;
            }
            if (_sbDragged)
            {
                _sbDragged = false;
                if (Floating && _carousel != null) Slot(th.transform); // the pair stays where it was dropped
            }
            _sb.SetBend(th.BendRadius > 0.05f ? th.BendRadius / k : 0f);
            if (force) _sb.SnapBend();
            var (pos, rot) = SidebarPose(th);
            _sb.transform.SetPositionAndRotation(pos, rot);
        }

        // ------------------------------------------------------------------ keys (a keyboard while the sidebar has the focus)

        /// The sidebar was the last panel touched: arrows, page keys, Enter and Backspace / Escape work in it.
        private bool SidebarPhysicalKey(MetaCodex.Terminal.TermKey key)
        {
            if (!_sbKeyFocus || _sb == null || !_sb.IsOpen) return false;
            string k;
            switch (key)
            {
                case MetaCodex.Terminal.TermKey.Up: k = "up"; break;
                case MetaCodex.Terminal.TermKey.Down: k = "down"; break;
                case MetaCodex.Terminal.TermKey.PageUp: k = "pageup"; break;
                case MetaCodex.Terminal.TermKey.PageDown: k = "pagedown"; break;
                case MetaCodex.Terminal.TermKey.Home: k = "home"; break;
                case MetaCodex.Terminal.TermKey.End: k = "end"; break;
                case MetaCodex.Terminal.TermKey.Left: k = "left"; break;
                case MetaCodex.Terminal.TermKey.Right: k = "right"; break;
                case MetaCodex.Terminal.TermKey.Enter: k = "enter"; break;
                case MetaCodex.Terminal.TermKey.Escape: case MetaCodex.Terminal.TermKey.Backspace: k = "back"; break;
                case MetaCodex.Terminal.TermKey.Tab: k = "tab"; break;
                default: return false;
            }
            return SidebarKey(k);
        }

        /// Keys for the sidebar (the physical keyboard, self-tests): up / down / pageup / pagedown / home / end / left /
        /// right / enter / back / tab (the next tab).
        public bool SidebarKey(string k)
        {
            if (_sb == null || !_sb.IsOpen) return false;
            if (k == "tab") { _sbTabs.Select((_sbTabs.Selected + 1) % SidebarTabs.Length); return true; }
            switch (_sbTabs.Selected)
            {
                case TabChanges:
                    if (_sbDiffPath != null) { if (k == "back") { SbCloseDiff(); return true; } return _sbDiff.Key(k); }
                    return _sbChList.Key(k);
                case TabFiles:
                    if (_sbFile != null) { if (k == "back") { _sbFile = null; SbShow(); SbRefreshTree(); return true; } return _sbCode.Key(k); }
                    if (k == "back") { if (_sbDir == _sbRoot) return false; _sbDir = FileService.Parent(_sbDir); SbRefreshTree(); return true; }
                    return _sbTree.Key(k);
                case TabGit: return _sbGitLog.Key(k);
                case TabStatus: return _sbStatus.Key(k);
                case TabViews: return _sbViews.Key(k);
            }
            return false;
        }

        // ------------------------------------------------------------------ voice / agents (the command registry)

        private void UpdateSidebarVoice()
        {
            if (Voice == null) return;
            bool thread = _threadOpen != null && _panels.TryGetValue("thread", out var th) && th.IsOpen;
            Voice.SetEnabled("thread_sidebar", thread);
            Voice.SetEnabled("init_git", thread && _sbRoot != null && _sbSnap != null && !_sbSnap.Repo && _sbSnap.Error == null);
        }

        private void RegisterSidebarVoiceCommands()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "thread_sidebar",
                Description = "The open thread's sidebar (its project folder): open it on a tab (changes: the files the agent changed and their diffs; files: the folder; git: branch, status, commits; status: model, context, what runs, approvals; views: the agent's charts) or close it",
                Parameters = VoiceCommands.Schema(("action", "string", new[] { "open", "close" }, "open (on a tab) or close the sidebar", true), ("tab", "string", SidebarTabs, "the tab to show", false)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["action"] = new Dictionary<string, string>
                    {
                        ["show"] = "open", ["expand"] = "open", ["display"] = "open", ["pull out"] = "open", ["hide"] = "close", ["collapse"] = "close", ["fold"] = "close", ["shut"] = "close", ["dismiss"] = "close",
                    },
                    ["tab"] = new Dictionary<string, string>
                    {
                        ["diff"] = "changes", ["diffs"] = "changes", ["differences"] = "changes", ["changed files"] = "changes", ["edits"] = "changes", ["modifications"] = "changes",
                        ["file"] = "files", ["explorer"] = "files", ["folder"] = "files", ["project folder"] = "files", ["tree"] = "files",
                        ["repository"] = "git", ["repo"] = "git", ["branch"] = "git", ["commits"] = "git", ["version control"] = "git",
                        ["state"] = "status", ["model"] = "status", ["context"] = "status", ["approvals"] = "status",
                        ["view"] = "views", ["charts"] = "views", ["diagrams"] = "views",
                    },
                },
                Phrases = new[]
                {
                    "{action} [the] [thread] sidebar",
                    "{action} [the] sidebar of [the|this] thread",
                    "{action} [the] [thread] sidebar on [the] {tab} [tab]",
                    "(show|open) [me] [the] {tab} (tab|page) [of|in] [the|this] (thread|sidebar) {action:open}",
                    "(show|open) [me] [the] thread's {tab} {action:open}",
                },
                Samples = new[]
                {
                    "pull out the thread sidebar", "show me what the agent changed in this thread", "fold the sidebar away", "show the git tab of this thread", "open the sidebar on the files",
                },
                Examples = new List<(string, string)> { ("what did the agent change in this thread", "thread_sidebar open changes"), ("hide the thread's side panel", "thread_sidebar close") },
                Vocabulary = new[] { "sidebar", "diff" },
                Run = a =>
                {
                    bool open = (string)a["action"] != "close";
                    string tabName = (string)a["tab"];
                    int tab = tabName == null ? -1 : Array.IndexOf(SidebarTabs, tabName);
                    if (!SetSidebar(open, open ? tab : -1)) return "error: open a thread first";
                    return open ? "Sidebar: " + SidebarTabs[_sbTabs.Selected].ToUpperInvariant() : "Sidebar folded";
                },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "init_git",
                Description = "Start a git repository (git init) in the open thread's project folder; only when that folder is not in a repository yet",
                Phrases = new[]
                {
                    "(init|initialize|initialise|start|create|set up) [a] git [repository|repo] [here|in this folder|in the project|for this project]",
                    "(make|turn) (this|the) (folder|project) [into] a git (repo|repository)",
                },
                Samples = new[]
                {
                    "put this project under git", "make this folder a git repository", "set up version control for this project", "start a repo here",
                },
                Vocabulary = new[] { "git init", "repository" },
                Run = a => SbInitGit(),
            });
            UpdateSidebarVoice();
        }

        // ------------------------------------------------------------------ self-tests

        public bool SidebarBusyForTest => _sbPending > 0;
        public UiSourceView SidebarDiffForTest => _sbDiff;
        public UiSourceView SidebarCodeForTest => _sbCode;
        public UiRows SidebarChangesForTest => _sbChList;
        public UiRows SidebarTreeForTest => _sbTree;
        public UiRows SidebarGitStatusForTest => _sbGitStatus;
        public UiRows SidebarGitLogForTest => _sbGitLog;
        public UiRows SidebarStatusForTest => _sbStatus;
        public UiCrumbs SidebarCrumbsForTest => _sbCrumbs;
        public UiTabs SidebarTabsForTest => _sbTabs;
        public UiEdgeHandle SidebarHandleForTest => _thHandle;
        public IReadOnlyList<ChangedFile> SidebarFilesForTest => _sbFiles;
        public GitSnapshot SidebarSnapshotForTest => _sbSnap;
        public string SidebarGitModeForTest => SbGitMode;
        public string SidebarSummaryForTest => ChatText.Strip(_sbChSummary?.Text ?? "");
        public string SidebarDiffLabelForTest => ChatText.Strip(_sbDiffLabel?.Text ?? "");
        public string SidebarFileLabelForTest => ChatText.Strip(_sbFileLabel?.Text ?? "");
        public string SidebarDirForTest => _sbDir;
        public string SidebarRootForTest => _sbRoot;
        public string SidebarOpenDiffForTest => _sbDiffPath;
        public string SidebarOpenFileForTest => _sbFile;
        public bool SidebarElementShownForTest(UiElement e) => e != null && e.Visible;
        public void SidebarOpenDiffForTest_(ChangedFile f) => SbOpenDiff(f);
        public void SidebarOpenFileNowForTest(string linuxPath) => SbOpenFile(linuxPath);
        public void SidebarRefreshNowForTest() => SbRefreshChanges(true);
        public void SidebarInitGitForTest() => SbInitGit();
        public void SidebarTickForTest() => SbTick();
        public void SidebarFocusForTest(bool on) => _sbKeyFocus = on;
        public bool SidebarPhysicalKeyForTest(MetaCodex.Terminal.TermKey k) => SidebarPhysicalKey(k);
        public void UpdateSidebarLinkForTest() => UpdateSidebarLink();
        public (Vector3 pos, Quaternion rot) SidebarPoseForTest() => _panels.TryGetValue("thread", out var th) ? SidebarPose(th) : (Vector3.zero, Quaternion.identity);
        public void ForgetSidebarMemoryForTest() { _sbMem.Clear(); Settings.threadSidebar?.Clear(); }

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
