using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using MetaCodex.App;
using MetaCodex.Hands;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using ScriptingEntry = MetaCodex.Scripting.ScriptHost.Entry;

namespace MetaCodex.Shell
{
    /// The starter panels: dock, terminals, files, git, browser, scripts, settings.
    public sealed partial class MetaCodexShell
    {
        private UiButton _selectButton;
        private UiLabel _gestureMonitor;

        private void BuildPanels()
        {
            BuildDock();
            BuildTerminals();
            BuildFiles();
            BuildBrowser();
            BuildScripts();
            BuildSettings();
            BuildGestures();
            BuildAgents();
            BuildThreads();
            BuildThread();
            BuildWorkspace();
            BuildNewThread();
            BuildPicker();
            BuildVault();
            BuildVoice();
            BuildSystem();
            foreach (var p in _panels.Values) p.transform.localScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
        }

        /// Accent colour changes are baked into shape tints: rebuild every panel, keeping where they are.
        public void RebuildPanels()
        {
            UiTheme.Current.SetAccent(Settings.accent);
            var poses = _panels.ToDictionary(kv => kv.Key, kv => (kv.Value.transform.position, kv.Value.transform.rotation, kv.Value.IsOpen));
            foreach (var p in _panels.Values) { Router.Unregister(p); UiToasts.Kill(p.gameObject); }
            _panels.Clear();
            BuildPanels();
            foreach (var kv in poses)
            {
                if (!_panels.TryGetValue(kv.Key, out var p)) continue;
                p.transform.SetPositionAndRotation(kv.Value.position, kv.Value.rotation);
                if (kv.Value.IsOpen) { p.gameObject.SetActive(true); p.ForceRefresh(); }
            }
            foreach (var f in _frames.Values) f.SetHighlight(0f);
            if (app != null && app.Active != null && _frames.TryGetValue(app.Active, out var af)) af.SetHighlight(0.55f);
        }

        private void OnPanelOpened(string id)
        {
            switch (id)
            {
                case "files": FilesNavigate(_filesPath ?? Settings.filesRoot); break;
                case "scripts": RefreshScriptsPanel(); break;
                case "terminals": RefreshTerminalsPanel(); break;
                case "browser": RefreshBrowser(); ProbeLocalServers(); break;
                case "settings": RefreshDaemon(); break;
                case "gestures": RefreshGesturesPanel(); break;
                case "agents": RefreshAgents(); break;
                case "threads": RefreshThreads(); break;
                case "vault": RefreshVaultPanel(); if (_vTabs != null && _vTabs.Selected == 1) { GitRefreshRepos(); RefreshGitHubStatus(); } break;
                case "voice": EnsureVoiceService(); PollVoiceStatus(); break;
                case "system": if (_sysTabs != null && _sysTabs.Selected == 1) RefreshStorage(false); else RefreshTools(false); break;
            }
        }

        // ------------------------------------------------------------------ dock

        private void BuildDock()
        {
            var t = UiTheme.Current;
            var items = new (string label, Action act)[]
            {
                ("▶\nTERM", () => ShowOrNewTerminal()),
                ("▤\nTERMS", () => TogglePanel("terminals")),
                ("◫\nFILES", () => TogglePanel("files")),
                ("☰\nTHREADS", () => TogglePanel("threads")),
                ("◆\nAGENTS", () => TogglePanel("agents")),
                ("◎\nWEB", () => TogglePanel("browser")),
                ("ƒ\nSCRIPTS", () => TogglePanel("scripts")),
                ("✋\nGESTURE", () => TogglePanel("gestures")),
                ("⬢\nSYSTEM", () => TogglePanel("system")),
                ("◉\nTALK", () => VoiceToggle()),
                ("♫\nVOICE", () => TogglePanel("voice")),
                ("⚿\nVAULT", () => TogglePanel("vault")),
                ("⎇\nGIT", () => TogglePanel("git")),
                ("◐\nFOCUS", () => SetFocus(!Settings.focusEnabled)),
                ("⌖\nSELECT", () => SetSelectionMode(!Router.SelectionMode)),
                ("⌨\nKEYS", () => OpenKeyboard()),
                ("⟲\nCENTER", () => app?.Recenter()),
                ("⚙\nSETUP", () => TogglePanel("settings")),
            };
            const int perRow = 9;
            float bw = 0.070f, gap = 0.006f, pad = 0.012f, bh = 0.058f, handle = 0.016f;
            float w = perRow * bw + (perRow - 1) * gap + 2 * pad, h = 2 * bh + gap + 2 * pad + handle;
            var dock = UiPanel.Create<UiPanel>("dock", "", w, h, _root, header: false);
            dock.Closable = false;
            dock.AddGrabHandle(handle); // pinch the bar at the top to move the dock
            _panels["dock"] = dock;
            Router.Register(dock);
            for (int i = 0; i < items.Length; i++)
            {
                int row = i / perRow, col = i % perRow;
                var b = dock.Add(new UiButton(items[i].label, items[i].act, i == 0 || items[i].label.EndsWith("TALK") ? UiButtonStyle.Primary : UiButtonStyle.Normal)
                {
                    Rect = new Rect(pad + col * (bw + gap), handle + pad * 0.6f + row * (bh + gap), bw, bh),
                    TextSize = 0.0135f,
                });
                if (items[i].label.EndsWith("SELECT")) _selectButton = b;
                if (items[i].label.EndsWith("FOCUS")) { _focusButton = b; b.SetSelected(Settings.focusEnabled); }
            }
            dock.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ terminals

        private UiList _termList;

        private void BuildTerminals()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("terminals", "TERMINALS", 0.52f, 0.40f);
            var st = new UiStack(p.Content, t.Gap);
            var bar = st.Next(t.ButtonHeight);
            string[] names = { "+ NEW", "SHOW", "HIDE", "CLOSE", "⌨ KEYS" };
            Action[] acts =
            {
                () => TerminalNew(),
                () => { var s = SelectedTerminal(); if (s != null) ShowTerminal(s); },
                () => { var s = SelectedTerminal(); if (s != null) HideTerminal(s); },
                () => { var s = SelectedTerminal(); if (s != null) CloseOrHideTerminal(s); },
                () => OpenKeyboard(),
            };
            float bw = (bar.width - t.Gap * 4) / 5f;
            for (int i = 0; i < 5; i++)
                p.Add(new UiButton(names[i], acts[i], i == 0 ? UiButtonStyle.Primary : UiButtonStyle.Normal) { Rect = new Rect(bar.x + i * (bw + t.Gap), bar.y, bw, bar.height) });
            _termList = p.Add(new UiList(0.034f, (item, idx) => { if (item.Tag is TerminalInstance ti) ShowTerminal(ti); }) { Rect = st.Rest() });
            RefreshTerminalsPanel();
        }

        private TerminalInstance SelectedTerminal() =>
            _termList != null && _termList.SelectedIndex >= 0 && _termList.SelectedIndex < _termList.Items.Count ? _termList.Items[_termList.SelectedIndex].Tag as TerminalInstance : app?.Active;

        private void RefreshTerminalsPanel()
        {
            if (_termList == null || app == null) return;
            var t = UiTheme.Current;
            var items = app.Terminals.Select(ti => new UiListItem
            {
                Icon = ti == app.Active ? "▶" : "▢",
                Title = ti.Name,
                Sub = ti.Session != null ? ti.Session.Description : "no session",
                Badge = (_frames.TryGetValue(ti, out var fr) && !fr.IsOpen ? "HIDDEN · " : "") + (ti == app.Active ? "ACTIVE" : ti.Session != null && ti.Session.IsRunning ? "RUN" : "IDLE"),
                BadgeColor = ti == app.Active ? t.Accent : ti.Session != null && ti.Session.IsRunning ? t.Ok : t.TextDim,
                Tag = ti,
            }).ToList();
            _termList.SetItems(items, keepScroll: true);
        }

        // ------------------------------------------------------------------ files

        private string _filesPath;
        private UiLabel _filesPathLabel, _filesInfo;
        private UiList _filesList;
        private FileEntry _filesSelected;

        private void BuildFiles()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("files", "FILES · LINUX", 0.60f, 0.52f);
            var st = new UiStack(p.Content, t.Gap);
            var bar = st.Next(t.ButtonHeight);
            var nav = new (string, Action)[]
            {
                ("↑ UP", () => FilesNavigate(FileService.Parent(_filesPath))),
                ("⌂ ROOT", () => FilesNavigate("/root")),
                ("QUEST", () => FilesNavigate("/mnt/quest")),
                ("/", () => FilesNavigate("/")),
                ("⟳", () => FilesNavigate(_filesPath)),
            };
            float bw = (bar.width - t.Gap * (nav.Length - 1)) / nav.Length;
            for (int i = 0; i < nav.Length; i++) p.Add(new UiButton(nav[i].Item1, nav[i].Item2) { Rect = new Rect(bar.x + i * (bw + t.Gap), bar.y, bw, bar.height) });
            _filesPathLabel = p.Add(new UiLabel("/", t.SmallSize * 1.1f) { Rect = st.Next(0.022f), UseMono = true, Color = t.TextAccent });
            var listRect = st.Next(p.Content.yMax - st.Y - t.ButtonHeight - 0.03f - t.Gap * 2);
            _filesList = p.Add(new UiList(0.030f, (item, idx) => FilesSelect(item.Tag as FileEntry)) { Rect = listRect });
            _filesInfo = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.022f), Color = t.TextDim });
            var acts = st.Next(t.ButtonHeight);
            var actions = new (string, Action)[]
            {
                ("CD HERE", () => TerminalSend($"cd {GitService.Quote(_filesSelected != null && _filesSelected.IsDir ? _filesSelected.LinuxPath : _filesPath)}\r")),
                ("VIEW", () => { if (_filesSelected != null && !_filesSelected.IsDir) TerminalSend($"less {GitService.Quote(_filesSelected.LinuxPath)}\r"); }),
                ("EDIT", () => { if (_filesSelected != null && !_filesSelected.IsDir) TerminalSend($"nano {GitService.Quote(_filesSelected.LinuxPath)}\r"); }),
                ("PATH ⇥", () => TerminalSend(GitService.Quote(_filesSelected?.LinuxPath ?? _filesPath))),
            };
            bw = (acts.width - t.Gap * (actions.Length - 1)) / actions.Length;
            for (int i = 0; i < actions.Length; i++) p.Add(new UiButton(actions[i].Item1, actions[i].Item2) { Rect = new Rect(acts.x + i * (bw + t.Gap), acts.y, bw, acts.height) });
        }

        public void FilesNavigate(string path)
        {
            if (_filesList == null) return;
            var t = UiTheme.Current;
            _filesPath = FileService.Normalize(path ?? Settings.filesRoot);
            _filesSelected = null;
            var entries = Files.List(_filesPath, out string error);
            _filesPathLabel.Set(_filesPath);
            _filesInfo.Set(error != null ? "⚠ " + error : $"{entries.Count(e => e.IsDir)} folders · {entries.Count(e => !e.IsDir)} files", error != null ? t.Danger : t.TextDim);
            _filesList.SelectedIndex = -1;
            _filesList.SetItems(entries.Select(e => new UiListItem
            {
                Icon = e.IsLink ? "↪" : e.IsDir ? "▸" : "·",
                Title = e.Name + (e.IsDir ? "/" : ""),
                TitleColor = e.IsDir ? t.TextAccent : e.Name.StartsWith(".") ? t.TextDim : (Color?)null,
                Sub = e.IsDir ? "" : FileService.Size(e.Size),
                Badge = e.Modified.ToString("MM-dd HH:mm"),
                BadgeColor = t.TextDim,
                Tag = e,
            }));
        }

        private void FilesSelect(FileEntry e)
        {
            if (e == null) return;
            if (e.IsDir && _filesSelected == e) { FilesNavigate(e.LinuxPath); return; } // second tap opens
            if (e.IsDir && !e.IsLink) { FilesNavigate(e.LinuxPath); return; }
            _filesSelected = e;
            _filesInfo.Set($"{e.LinuxPath} · {(e.IsDir ? "folder" : FileService.Size(e.Size))} · {e.Modified:yyyy-MM-dd HH:mm}");
        }

        // ------------------------------------------------------------------ git

        private UiList _gitRepos, _gitChanges, _gitBranches;
        private UiLabel _gitHead;
        private UiGitGraph _gitGraph;
        private string _gitRepo;
        private GitStatus _gitStatus;

        private string GitRoot()
        {
            if (Git.InUserland) return "/root";
            // Editor: the repos of this machine's MetaCodex checkout, seen from WSL
            return GitService.ToLinuxPath(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../..")));
        }

        public void GitRefreshRepos()
        {
            if (_gitRepos == null) return;
            string root = GitRoot();
            _gitHead.Set("Scanning " + root + " …", UiTheme.Current.TextDim);
            Task.Run(async () =>
            {
                var repos = await Git.FindRepos(root, 2);
                _main.Enqueue(() =>
                {
                    _gitRepos.SetItems(repos.Select(r => new UiListItem { Icon = "⎇", Title = r.Split('/').Last(), Sub = "", Tag = r }));
                    if (repos.Count == 0) _gitHead.Set("No git repositories under " + root, UiTheme.Current.TextDim);
                    else if (_gitRepo == null || !repos.Contains(_gitRepo)) { _gitRepos.SelectIndex(0); }
                    else GitOpen(_gitRepo);
                });
            });
        }

        public void GitOpen(string repo)
        {
            if (repo == null) return;
            _gitRepo = repo;
            Task.Run(async () =>
            {
                var st = await Git.Status(repo);
                _main.Enqueue(() => GitShow(st));
            });
        }

        public GitStatus LastGitStatus => _gitStatus;

        private void GitShow(GitStatus st)
        {
            var t = UiTheme.Current;
            _gitStatus = st;
            if (st.Error != null) { _gitHead.Set("⚠ " + st.Error, t.Danger); return; }
            string ab = (st.Ahead > 0 ? $"  <color=#4DFFA6>↑{st.Ahead}</color>" : "") + (st.Behind > 0 ? $"  <color=#FFB547>↓{st.Behind}</color>" : "");
            string up = string.IsNullOrEmpty(st.Upstream) ? "<color=#7FA9BF>no upstream</color>" : $"<color=#7FA9BF>{st.Upstream}</color>";
            int changed = st.Entries.Count;
            _gitHead.Set($"⎇ <b>{st.Branch}</b>{ab}   {up}   " + (changed == 0 ? "<color=#4DFFA6>clean</color>" : $"<color=#FFB547>{changed} changed</color>"), t.Text);
            _gitChanges.SetItems(st.Entries.Select(e => new UiListItem
            {
                Icon = e.Code.Contains("?") ? "+" : e.Code.Contains("D") ? "−" : "●",
                Title = e.Path,
                Badge = e.Code,
                BadgeColor = e.Code.Contains("?") ? t.TextDim : e.Code.Contains("D") ? t.Danger : e.Code.Contains("A") ? t.Ok : t.Warn,
            }));
            _gitBranches.SetItems(st.Branches.Select(b => new UiListItem
            {
                Icon = b.Current ? "◆" : "◇",
                Title = b.Name,
                TitleColor = b.Current ? t.TextAccent : (Color?)null,
                Sub = b.When,
                Badge = string.IsNullOrEmpty(b.Track) ? (string.IsNullOrEmpty(b.Upstream) ? "local" : "synced") : b.Track.Trim('[', ']'),
                BadgeColor = string.IsNullOrEmpty(b.Track) ? (string.IsNullOrEmpty(b.Upstream) ? t.TextDim : t.Ok) : t.Warn,
                Tag = b,
            }));
            _gitGraph.SetCommits(st.Log);
        }

        private void GitAction(string args)
        {
            if (_gitRepo == null) return;
            Toast("git " + args.Split(' ')[0] + " …", "info");
            Task.Run(async () =>
            {
                var r = await Git.Git(_gitRepo, args, 60000);
                _main.Enqueue(() =>
                {
                    Toast(r.code == 0 ? "git " + args.Split(' ')[0] + " done" : "git: " + Last(r.output), r.code == 0 ? "ok" : "error");
                    GitOpen(_gitRepo);
                });
            });
        }

        private static string Last(string s) => (s ?? "").Trim().Split('\n').LastOrDefault() ?? "";

        private void GitCommitPrompt()
        {
            if (_gitRepo == null) return;
            UiKeyboard.Edit("", false, null, msg =>
            {
                if (string.IsNullOrWhiteSpace(msg)) { Toast("Commit cancelled (empty message)", "warn"); return; }
                GitAction("commit -a -m " + GitService.Quote(msg));
            });
        }

        private void GitBranchPicked(GitBranch b)
        {
            if (b == null || b.Current || _gitRepo == null) return;
            Modal("SWITCH BRANCH", $"Check out '{b.Name}' in {_gitRepo.Split('/').Last()}?", new[] { "CHECKOUT", "CANCEL" }, i =>
            {
                if (i == 0) GitAction("checkout " + GitService.Quote(b.Name));
            });
        }

        // ------------------------------------------------------------------ browser

        private UiTextField _url;
        private UiList _bookmarks, _localServers;

        private void BuildBrowser()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("browser", "BROWSER · HORIZON", 0.56f, 0.50f);
            var st = new UiStack(p.Content, t.Gap);
            var row = st.NextSplit(t.ButtonHeight, 0.80f, out var go);
            _url = p.Add(new UiTextField("", "https://", u => OpenBrowser(u), "type a URL…") { Rect = row });
            p.Add(new UiButton("GO ▶", () => OpenBrowser(_url.Text), UiButtonStyle.Primary) { Rect = go });
            p.Add(new UiLabel("BOOKMARKS", t.SmallSize) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _bookmarks = p.Add(new UiList(0.028f, (item, idx) => OpenBrowser(item.Tag as string)) { Rect = st.Next(0.112f) });
            var bm = st.Next(t.ButtonHeight);
            float half = (bm.width - t.Gap) / 2f;
            p.Add(new UiButton("+ ADD URL", () =>
            {
                var u = _url.Text?.Trim();
                if (string.IsNullOrEmpty(u) || u == "https://" || Settings.bookmarks.Contains(u)) return;
                Settings.bookmarks.Add(u);
                OnSettingsChanged();
                RefreshBrowser();
            }) { Rect = new Rect(bm.x, bm.y, half, bm.height) });
            p.Add(new UiButton("− REMOVE", () =>
            {
                if (_bookmarks.SelectedIndex < 0 || _bookmarks.SelectedIndex >= Settings.bookmarks.Count) return;
                Settings.bookmarks.RemoveAt(_bookmarks.SelectedIndex);
                OnSettingsChanged();
                RefreshBrowser();
            }) { Rect = new Rect(bm.x + half + t.Gap, bm.y, half, bm.height) });
            p.Add(new UiLabel("LOCAL SERVERS (127.0.0.1)", t.SmallSize) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _localServers = p.Add(new UiList(0.028f, (item, idx) => OpenBrowser(item.Tag as string)) { Rect = st.Next(0.084f) });
            var sys = st.Next(t.ButtonHeight);
            var sysActs = new (string, Action)[]
            {
                ("⟳ SCAN", ProbeLocalServers),
                ("HANDS ⚙", () => Toast(SystemBridge.OpenHandSettings() ? "Opening hand settings" : SystemBridge.LastResult)),
                ("APP ⚙", () => Toast(SystemBridge.OpenAppSettings() ? "Opening app settings" : SystemBridge.LastResult)),
                ("FILES APP", () => Toast(SystemBridge.OpenFilesApp() ? "Opening Files" : SystemBridge.LastResult)),
            };
            float bw = (sys.width - t.Gap * 3) / 4f;
            for (int i = 0; i < 4; i++) p.Add(new UiButton(sysActs[i].Item1, sysActs[i].Item2) { Rect = new Rect(sys.x + i * (bw + t.Gap), sys.y, bw, sys.height) });
            RefreshBrowser();
        }

        private void RefreshBrowser()
        {
            _bookmarks?.SetItems(Settings.bookmarks.Select(b => new UiListItem { Icon = "◎", Title = b, Tag = b }));
        }

        public static readonly int[] DevPorts = { 3000, 3001, 4173, 4200, 5000, 5173, 5500, 8000, 8080, 8888, 38990 };

        private void ProbeLocalServers()
        {
            Task.Run(async () =>
            {
                var open = new List<int>();
                foreach (var port in DevPorts)
                {
                    try
                    {
                        using (var c = new TcpClient())
                        {
                            var task = c.ConnectAsync("127.0.0.1", port);
                            if (await Task.WhenAny(task, Task.Delay(150)) == task && c.Connected) open.Add(port);
                        }
                    }
                    catch (Exception) { }
                }
                _main.Enqueue(() => _localServers?.SetItems(open.Select(port => new UiListItem
                {
                    Icon = "▣",
                    Title = $"http://127.0.0.1:{port}",
                    Sub = port == 38990 ? "MQH" : port == 5173 || port == 4173 ? "Vite" : port == 3000 ? "dev server" : "",
                    Badge = "OPEN",
                    BadgeColor = UiTheme.Current.Ok,
                    Tag = $"http://127.0.0.1:{port}",
                })));
            });
        }

        // ------------------------------------------------------------------ scripts

        private UiList _scriptList;
        private UiLabel _scriptDetail;
        private UiButton _scriptToggle;

        private void BuildScripts()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("scripts", "SCRIPTS · LIVE BEHAVIOURS", 0.60f, 0.56f);
            var st = new UiStack(p.Content, t.Gap);
            _scriptList = p.Add(new UiList(0.032f, (item, idx) => ScriptPicked()) { Rect = st.Next(0.17f) });
            _scriptDetail = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.075f), Wrap = true, UseMono = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            var row = st.Next(t.ButtonHeight);
            var acts = new (string, Action)[]
            {
                ("ENABLE", () => { var e = SelectedScript(); if (e != null) Scripts.SetEnabled(e.Key, !e.Enabled); }),
                ("⟳ RELOAD", () => { Scripts.ReloadAll(); Toast("Scripts reloaded", "ok"); }),
                ("+ EXAMPLE", () => { Scripts.Save("double-clap", ExampleScript); Toast("Example script: double clap opens TERMINALS", "ok"); }),
                ("FOLDER", () => TerminalSend("cd /mnt/quest/scripts && ls -la\r")),
                ("DELETE", () => { var e = SelectedScript(); if (e != null) Modal(e.Kind == "plugin" ? "DELETE PLUGIN" : "DELETE SCRIPT", e.Kind == "plugin" ? $"Delete the plugin folder {e.Name}?" : $"Delete {e.Name}.lua?", new[] { "DELETE", "CANCEL" }, i => { if (i == 0) Scripts.Delete(e.Key); }); }),
            };
            float bw = (row.width - t.Gap * (acts.Length - 1)) / acts.Length;
            for (int i = 0; i < acts.Length; i++)
            {
                var b = p.Add(new UiButton(acts[i].Item1, acts[i].Item2, i == 4 ? UiButtonStyle.Danger : UiButtonStyle.Normal) { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height) });
                if (i == 0) _scriptToggle = b;
            }
            p.Add(new UiDivider { Rect = st.Next(0.008f) });
            p.Add(new UiLabel("GESTURE MONITOR", t.SmallSize) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gestureMonitor = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Rest(), Wrap = true, UseMono = true, Color = t.TextAccent, Align = TextAlignmentOptions.TopLeft });
            RefreshScriptsPanel();
        }

        public const string ExampleScript =
@"-- description: double clap opens the terminals list
-- An example behaviour. Edit or replace it: the app reloads it within a second.
gesture.sequence({""clap"", ""clap""}, 0.8, function(e)
  ui.toast(""Double clap: terminals"")
  ui.toggle(""terminals"")
end)
";

        private ScriptingEntry SelectedScript() =>
            _scriptList != null && _scriptList.SelectedIndex >= 0 && _scriptList.SelectedIndex < _scriptList.Items.Count ? _scriptList.Items[_scriptList.SelectedIndex].Tag as ScriptingEntry : null;

        private void ScriptPicked()
        {
            var e = SelectedScript();
            if (e == null || _scriptDetail == null) return;
            var t = UiTheme.Current;
            _scriptDetail.Set(e.Error != null ? "ERROR " + e.Error : string.Join("\n", e.Log.Skip(Math.Max(0, e.Log.Count - 4))), e.Error != null ? t.Danger : t.TextDim);
            _scriptToggle?.SetText(e.Enabled ? "DISABLE" : "ENABLE");
        }

        private void RefreshScriptsPanel()
        {
            if (_scriptList == null || Scripts == null) return;
            var t = UiTheme.Current;
            _scriptList.SetItems(Scripts.Entries.Select(e => new UiListItem
            {
                Icon = e.Kind == "plugin" ? "▣" : "●",
                IconColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                Title = e.Kind == "plugin" ? e.Name + " (plugin)" : e.Name,
                TitleColor = e.Error != null ? t.Danger : e.Enabled ? (Color?)null : t.TextDim,
                Sub = e.Description,
                Badge = e.Error != null ? "ERROR" : !e.Enabled ? "OFF" : e.Handlers + " ON",
                BadgeColor = e.Error != null ? t.Danger : !e.Enabled ? t.TextDim : t.Ok,
                Tag = e,
            }), keepScroll: true);
            ScriptPicked();
        }

        private void RefreshGestureMonitor()
        {
            if (_gestureMonitor == null || Gestures == null) return;
            var lines = Gestures.Recent.Reverse().Take(6).Select(g => $"{g.Time,7:F2}  {g.Name,-8} {g.Hand,-5} {g.Phase}");
            string l = HandLine(Gestures.Left, "L"), r = HandLine(Gestures.Right, "R");
            _gestureMonitor.Set(l + "\n" + r + "\n" + string.Join("\n", lines));
        }

        private static string HandLine(HandState s, string tag) =>
            !s.Tracked ? $"{tag}: not tracked" : $"{tag}: pinch {s.Pinch:F2} {(s.Pinching ? "PINCH " : "")}{(s.Grabbing ? "GRAB " : "")}{(s.Pointing ? "POINT " : "")}{(s.Open ? "OPEN " : "")}{(s.PalmUp ? "PALM-UP" : "")}";

        // ------------------------------------------------------------------ settings

        private UiLabel _daemonStatus, _gestureLive;
        private UiTabs _settingsTabs;
        private UiList _pluginList;

        private void BuildSettings()
        {
            var t = UiTheme.Current;
            var s = Settings;
            var p = Panel<UiPanel>("settings", "SETTINGS", 0.62f, 0.58f);
            var c = p.Content;
            var tabs = p.Add(new UiTabs("GESTURES", "DISPLAY", "FOCUS", "LOOK", "DAEMON", "ABOUT") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            _settingsTabs = tabs;
            const int TabDisplay = 1, TabFocus = 2, TabLook = 3, TabDaemon = 4, TabAbout = 5;
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);
            float rowH = 0.034f;

            // GESTURES
            var g = new UiStack(area, t.Gap);
            tabs.AddTo(0, new UiSegmented("CLICK", new[] { "PINCH", "POKE", "BOTH" }, s.gestures.clickMode, i => { s.gestures.clickMode = i; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiSegmented("MOVE PANELS", new[] { "PINCH HEADER", "CLOSED HAND", "BOTH" }, s.gestures.grabMode, i => { s.gestures.grabMode = i; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiSegmented("POINT RAY", new[] { "ALWAYS", "POINTING POSE" }, s.gestures.pointMode, i => { s.gestures.pointMode = i; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiToggle("Show hand rays", s.gestures.rayVisible, v => { s.gestures.rayVisible = v; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiSlider("PINCH AT", 0.6f, 0.98f, s.gestures.pinchOn, v => { s.gestures.pinchOn = v; s.gestures.pinchOff = Mathf.Min(v - 0.15f, 0.8f); OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiToggle("Clap gesture (two hands)", s.gestures.clapEnabled, v => { s.gestures.clapEnabled = v; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            tabs.AddTo(0, new UiSlider("CLAP DISTANCE", 0.04f, 0.14f, s.gestures.clapDistance, v => { s.gestures.clapDistance = v; OnSettingsChanged(); }) { Rect = g.Next(rowH), Format = "0.000" });
            tabs.AddTo(0, new UiToggle("Custom gestures from plugins (poses, Lua detectors)", s.gestures.customEnabled, v => { s.gestures.customEnabled = v; OnSettingsChanged(); }) { Rect = g.Next(rowH) });
            var gb = g.NextSplit(t.ButtonHeight, 0.5f, out var gb2);
            tabs.AddTo(0, new UiButton("SELECT AREA ⌖", () => SetSelectionMode(!Router.SelectionMode)) { Rect = gb });
            tabs.AddTo(0, new UiButton("HORIZON HAND SETTINGS", () => Toast(SystemBridge.OpenHandSettings() ? "Opening hand settings" : SystemBridge.LastResult)) { Rect = gb2 });
            _gestureLive = tabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = g.Rest(), UseMono = true, Color = t.TextAccent, Wrap = true, Align = TextAlignmentOptions.TopLeft });
            p.Tick += _ => { if (_gestureLive != null && _gestureLive.Visible && Gestures != null) _gestureLive.Set(HandLine(Gestures.Left, "L") + "\n" + HandLine(Gestures.Right, "R")); };

            // DISPLAY: where panels go
            var d = new UiStack(area, t.Gap);
            tabs.AddTo(TabDisplay, new UiSegmented("LAYOUT", new[] { "FIXED", "FLOAT" }, s.layout == "float" ? 1 : 0, i => SetLayout(i == 1 ? "float" : "fixed")) { Rect = d.Next(rowH) });
            tabs.AddTo(TabDisplay, new UiSlider("DISTANCE", 0.5f, 1.4f, s.panelDistance, v => { s.panelDistance = v; OnSettingsChanged(); }) { Rect = d.Next(rowH), Format = "0.00 m" });
            tabs.AddTo(TabDisplay, new UiSlider("CAROUSEL RADIUS", 0.6f, 1.6f, s.carouselRadius, v => { s.carouselRadius = v; OnSettingsChanged(); }) { Rect = d.Next(rowH), Format = "0.00 m" });
            tabs.AddTo(TabDisplay, new UiSlider("UI SCALE", 0.8f, 1.8f, s.uiScale, v => { s.uiScale = v; OnSettingsChanged(); }) { Rect = d.Next(rowH), Format = "0.00" });
            tabs.AddTo(TabDisplay, new UiToggle("Bend panels around you", s.bendPanels, v => { s.bendPanels = v; OnSettingsChanged(); UpdateFixedBend(); }) { Rect = d.Next(rowH) });
            var lr = d.NextSplit(t.ButtonHeight, 0.5f, out var lr2);
            tabs.AddTo(TabDisplay, new UiButton("SAVE LAYOUT…", () => UiKeyboard.Edit("desk", false, null, n => { if (!string.IsNullOrWhiteSpace(n)) { SaveNamedLayout(n.Trim()); Toast($"Layout '{n.Trim()}' saved", "ok"); } })) { Rect = lr });
            tabs.AddTo(TabDisplay, new UiButton("LOAD NEXT LAYOUT", LoadNextLayout) { Rect = lr2 });
            _layoutsLabel = tabs.AddTo(TabDisplay, new UiLabel("", t.SmallSize) { Rect = d.Next(0.024f), Color = t.TextDim });
            tabs.AddTo(TabDisplay, new UiButton("⟲ RECENTER EVERYTHING", () => app?.Recenter(), UiButtonStyle.Primary) { Rect = d.Next(t.ButtonHeight) });
            RefreshLayoutsLabel();

            // FOCUS: Ambient Focus, the room fades out beyond a radius around you
            var fo = new UiStack(area, t.Gap);
            _focusToggle = tabs.AddTo(TabFocus, new UiToggle("Ambient focus: fade the room beyond a radius", s.focusEnabled, v => { SetFocus(v, false); RefreshFocusPanel(); }) { Rect = fo.Next(rowH) });
            _focusRadius = tabs.AddTo(TabFocus, new UiSlider("RADIUS", 0f, 5f, s.focusRadius, v => { s.focusRadius = v; OnSettingsChanged(); RefreshFocusPanel(); }) { Rect = fo.Next(rowH), Format = "0.0 m" });
            _focusIntensity = tabs.AddTo(TabFocus, new UiSlider("INTENSITY", 0f, 1f, s.focusIntensity, v => { s.focusIntensity = v; OnSettingsChanged(); RefreshFocusPanel(); }) { Rect = fo.Next(rowH), Format = "0%" });
            _focusSoft = tabs.AddTo(TabFocus, new UiSlider("SOFTNESS", 0.05f, 2f, s.focusSoftness, v => { s.focusSoftness = v; OnSettingsChanged(); RefreshFocusPanel(); }) { Rect = fo.Next(rowH), Format = "0.0 m" });
            _focusBlur = tabs.AddTo(TabFocus, new UiSlider("BLUR", 0f, 1f, s.focusBlur, v => { s.focusBlur = v; OnSettingsChanged(); RefreshFocusPanel(); }) { Rect = fo.Next(rowH), Format = "0%" });
            var fp = fo.Next(t.ButtonHeight);
            float fpw = (fp.width - t.Gap * 2) / 3f;
            string[] presets = { "subtle", "focus", "blackout" };
            for (int i = 0; i < 3; i++)
            {
                string preset = presets[i];
                tabs.AddTo(TabFocus, new UiButton(preset.ToUpperInvariant(), () => { ApplyFocusPreset(preset); }) { Rect = new Rect(fp.x + i * (fpw + t.Gap), fp.y, fpw, fp.height) });
            }
            _focusInfo = tabs.AddTo(TabFocus, new UiLabel("", t.SmallSize) { Rect = fo.Next(0.046f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            tabs.AddTo(TabFocus, new UiLabel("A dome standing on the floor around you, from the live depth of the room (no Space Setup scan): walls, furniture and your hands inside it stay, the rest fades to dark or, with BLUR, to a soft camera image (asks for the camera once). Your windows always stay. BLACKOUT leaves only the windows and your hands. FOCUS on the dock turns it on and off.", t.SmallSize) { Rect = fo.Rest(), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            RefreshFocusPanel();

            // LOOK: one standard appearance; what is left are comfort settings
            var k = new UiStack(area, t.Gap);
            tabs.AddTo(TabLook, new UiSlider("SHARPNESS", 1f, 1.6f, s.renderScale, v => { s.renderScale = v; OnSettingsChanged(); }) { Rect = k.Next(rowH), Format = "0.00 x" });
            tabs.AddTo(TabLook, new UiSlider("PANEL OPACITY", 0.5f, 1f, s.panelOpacity, v => { s.panelOpacity = v; OnSettingsChanged(); }) { Rect = k.Next(rowH), Format = "0.00" });
            tabs.AddTo(TabLook, new UiSlider("SHADOWS", 0f, 2f, s.glow, v => { s.glow = v; OnSettingsChanged(); }) { Rect = k.Next(rowH), Format = "0.00" });
            tabs.AddTo(TabLook, new UiToggle("Foveated rendering (saves GPU, blurs the edges of the view)", s.foveation, v => { s.foveation = v; OnSettingsChanged(); }) { Rect = k.Next(rowH) });
            var sg = k.NextSplit(rowH, 0.4f, out var sg2);
            tabs.AddTo(TabLook, new UiToggle("Sounds", s.sounds, v => { s.sounds = v; OnSettingsChanged(); if (v) Sounds?.Play("toast"); }) { Rect = sg });
            tabs.AddTo(TabLook, new UiSlider("VOLUME", 0f, 1f, s.soundVolume, v => { s.soundVolume = v; OnSettingsChanged(); }) { Rect = sg2, LabelWidth = 0.08f, Format = "0.00" });

            // DAEMON (MQH)
            var m = new UiStack(area, t.Gap);
            tabs.AddTo(TabDaemon, new UiTextField("MQH URL", s.mqhUrl, v => { s.mqhUrl = v.Trim(); OnSettingsChanged(); RefreshDaemon(); }) { Rect = m.Next(rowH) });
            tabs.AddTo(TabDaemon, new UiTextField("TOKEN", s.mqhToken, v => { s.mqhToken = v.Trim(); OnSettingsChanged(); RefreshDaemon(); }, "from the vault") { Rect = m.Next(rowH), Secure = true });
            tabs.AddTo(TabDaemon, new UiToggle("Sync these settings with MQH", s.mqhSync, v => { s.mqhSync = v; OnSettingsChanged(); }) { Rect = m.Next(rowH) });
            _daemonStatus = tabs.AddTo(TabDaemon, new UiLabel("not checked", t.SmallSize) { Rect = m.Next(0.024f), Color = t.TextDim });
            _pluginList = tabs.AddTo(TabDaemon, new UiList(0.027f, (item, idx) => PluginPicked(item)) { Rect = m.Next(area.yMax - m.Y - t.ButtonHeight - t.Gap) });
            var mb = m.NextSplit(t.ButtonHeight, 0.5f, out var mb2);
            tabs.AddTo(TabDaemon, new UiButton("⟳ CONNECT", RefreshDaemon, UiButtonStyle.Primary) { Rect = mb });
            tabs.AddTo(TabDaemon, new UiButton("PUSH SETTINGS", () => Mqh.PushQuestSettings(Settings.ToJObject(), ok => Toast(ok ? "Settings saved to MQH" : "MQH unreachable", ok ? "ok" : "error"))) { Rect = mb2 });

            // ABOUT
            var a2 = new UiStack(area, t.Gap);
            tabs.AddTo(TabAbout, new UiLabel($"<b>MetaCodex</b> {Application.version} · {SystemInfo.deviceModel}", t.BodySize) { Rect = a2.Next(0.026f) });
            tabs.AddTo(TabAbout, new UiLabel("Agents and a real Linux userland in VR. Panels, gestures and behaviours are data, Lua and plugins: agents change them live.", t.SmallSize) { Rect = a2.Next(0.04f), Wrap = true, Color = t.TextDim });
            tabs.AddTo(TabAbout, new UiLabel("Third-party: MoonSharp (BSD-3-Clause, Marco Mastropaolo) · Inter, Noto (SIL OFL 1.1) · DejaVu fonts · QVAC (Apache-2.0, Tether) · Whisper, Qwen3, Supertonic models · bb (MIT, Michael Yong) for MQH · tool logos: Simple Icons (CC0; trademarks of their owners).", t.SmallSize) { Rect = a2.Next(0.05f), Wrap = true, Color = t.TextDim });
            tabs.AddTo(TabAbout, new UiLabel("", t.SmallSize) { Rect = a2.Rest(), UseMono = true, Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft }).Set(string.Join("\n", LogLines.Skip(Math.Max(0, LogLines.Count - 8))));
        }

        private UiLabel _layoutsLabel;
        private int _layoutCursor = -1;

        /// Remembers where the open panels are, relative to the head (yaw frame), under a name.
        public void SaveNamedLayout(string name)
        {
            var head = Gestures.Head;
            Gestures.HeadFrame(out var r, out var u, out var f);
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            var lay = new SavedLayout { name = name, mode = Settings.layout };
            foreach (var kv in _panels)
            {
                if (!kv.Value.IsOpen || kv.Key == "dock") continue;
                var d = kv.Value.transform.position - head.position;
                var fw = kv.Value.transform.forward;
                lay.panels.Add(new SavedPanel
                {
                    id = kv.Key,
                    offset = new Vector3(Vector3.Dot(d, r), Vector3.Dot(d, u), Vector3.Dot(d, f)),
                    yaw = Mathf.DeltaAngle(headYaw, Mathf.Atan2(fw.x, fw.z) * Mathf.Rad2Deg),
                    bend = kv.Value.BendRadius,
                });
            }
            Settings.layouts.RemoveAll(x => x.name == name);
            Settings.layouts.Add(lay);
            OnSettingsChanged();
            RefreshLayoutsLabel();
        }

        /// Puts the panels of a saved layout back around where the user is now.
        public bool LoadNamedLayout(string name)
        {
            var lay = Settings.layouts.FirstOrDefault(x => x.name == name);
            if (lay == null) return false;
            if (lay.mode != Settings.layout) SetLayout(lay.mode);
            var head = Gestures.Head;
            Gestures.HeadFrame(out var r, out var u, out var f);
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            foreach (var kv in _panels) if (kv.Key != "dock" && kv.Value.IsOpen && lay.panels.All(x => x.id != kv.Key)) kv.Value.Close();
            foreach (var sp in lay.panels)
            {
                if (!_panels.TryGetValue(sp.id, out var p)) continue;
                OpenPanel(sp.id);
                if (Settings.layout == "float") continue; // the carousel places them
                p.transform.SetPositionAndRotation(head.position + r * sp.offset.x + u * sp.offset.y + f * sp.offset.z, Quaternion.Euler(0f, headYaw + sp.yaw, 0f));
                _placed.Add(sp.id);
            }
            return true;
        }

        private void LoadNextLayout()
        {
            if (Settings.layouts.Count == 0) { Toast("No saved layouts yet: SAVE LAYOUT… first", "warn"); return; }
            _layoutCursor = (_layoutCursor + 1) % Settings.layouts.Count;
            var n = Settings.layouts[_layoutCursor].name;
            LoadNamedLayout(n);
            Toast($"Layout '{n}'", "ok");
            RefreshLayoutsLabel();
        }

        private void RefreshLayoutsLabel() =>
            _layoutsLabel?.Set(Settings.layouts.Count == 0 ? "saved layouts: none" : "saved layouts: " + string.Join(" · ", Settings.layouts.Select(x => x.name)));

        public void RefreshDaemon()
        {
            if (_daemonStatus == null) return;
            var t = UiTheme.Current;
            _daemonStatus.Set("connecting to " + Settings.mqhUrl + " …", t.TextDim);
            Mqh.BaseUrl = Settings.mqhUrl;
            Mqh.Token = MqhTokenNow();
            Mqh.Version(v =>
            {
                if (v == null) { _daemonStatus.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), t.Danger); _pluginList.SetItems(new UiListItem[0]); return; }
                string ver = v["version"]?.ToString() ?? v["appVersion"]?.ToString() ?? v.ToString(Newtonsoft.Json.Formatting.None);
                _daemonStatus.Set($"<color=#4DFFA6>● online</color>  MQH {Trim(ver, 60)}", t.Text);
                Mqh.Plugins(pl =>
                {
                    var arr = pl?["plugins"] as JArray;
                    if (arr == null) return;
                    _pluginList.SetItems(arr.Select(x => new UiListItem
                    {
                        Icon = x["spatial"]?["hasSpatial"]?.Value<bool>() == true ? "◫" : "◆",
                        Title = (string)x["id"],
                        Sub = (string)x["name"] ?? "",
                        Badge = ((string)x["status"] ?? "").ToUpperInvariant(),
                        BadgeColor = (string)x["status"] == "running" ? t.Ok : (string)x["status"] == "disabled" ? t.TextDim : t.Warn,
                        Tag = (string)x["id"],
                    }));
                });
            });
        }

        private static string Trim(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n) + "…" : s;

        private void PluginPicked(UiListItem item)
        {
            var id = item.Tag as string;
            if (id == null) return;
            bool running = item.Badge == "RUNNING";
            Modal("PLUGIN " + id.ToUpperInvariant(), running ? "Disable this plugin?" : "Enable this plugin?", new[] { running ? "DISABLE" : "ENABLE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                Mqh.SetPluginEnabled(id, !running, r => { Toast(r != null ? $"{id} {(running ? "disabled" : "enabled")}" : "MQH: " + Mqh.LastError, r != null ? "ok" : "error"); RefreshDaemon(); });
            });
        }
    }

    /// The git history as "metro lines": one vertical line per lane, a dot per commit, refs and subject.
    public sealed class UiGitGraph : UiElement
    {
        public float RowHeight = 0.022f;
        private readonly List<int> _lanes = new List<int>();
        private readonly List<(int dot, UiText text)> _rows = new List<(int, UiText)>();
        private const int MaxLanes = 6;
        private const float LaneW = 0.012f;
        private static readonly int[] LaneColors = { 0x35D0FF, 0x4DFFA6, 0xFFB547, 0xB28CFF, 0xFF5468, 0x8FEBFF };

        protected override void Build()
        {
            for (int l = 0; l < MaxLanes; l++)
                _lanes.Add(Panel.Shapes.Add(Panel.Shape(UiShapeKind.Line, new Rect(Rect.x + 0.008f + l * LaneW, Rect.y, 0.003f, Rect.height), UiTheme.Hex(LaneColors[l]), 0, 0, 0.002f)));
            int n = Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowHeight));
            for (int i = 0; i < n; i++)
            {
                int dot = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Dot, new Rect(Rect.x, Rect.y + i * RowHeight, 0.011f, 0.011f), Theme.Accent, 0, 0, 0.003f, 0.6f));
                var text = UiText.Create(Panel, new Rect(Rect.x + 0.02f + MaxLanes * LaneW, Rect.y + i * RowHeight, Rect.width - 0.02f - MaxLanes * LaneW, RowHeight), "", Theme.Mono, Theme.SmallSize, Theme.Text);
                _rows.Add((dot, text));
            }
            SetCommits(new List<GitCommit>());
        }

        public void SetCommits(List<GitCommit> log)
        {
            if (Panel == null) return;
            int lanes = log.Count > 0 ? Mathf.Min(MaxLanes, log[0].Lanes) : 0;
            for (int l = 0; l < MaxLanes; l++) Panel.Shapes.SetVisible(_lanes[l], Visible && l < lanes);
            for (int i = 0; i < _rows.Count; i++)
            {
                bool has = i < log.Count && Visible;
                Panel.Shapes.SetVisible(_rows[i].dot, has);
                _rows[i].text.SetVisible(has);
                if (!has) continue;
                var c = log[i];
                int lane = Mathf.Min(c.Lane, MaxLanes - 1);
                var center = new Vector2(Rect.x + 0.0095f + lane * LaneW, Rect.y + i * RowHeight + RowHeight * 0.5f);
                var s = Panel.Shapes.Get(_rows[i].dot);
                s.Center = Panel.PanelToLocal(center, -0.0004f);
                s.Tint = UiTheme.Hex(LaneColors[lane]);
                s.State = c.Parents.Length > 1 ? 1f : 0.5f;
                Panel.Shapes.Set(_rows[i].dot, s);
                string refs = string.IsNullOrEmpty(c.Refs) ? "" : $" <color=#FFB547>({c.Refs})</color>";
                _rows[i].text.Text = $"<color=#8FEBFF>{c.Hash}</color>{refs} <noparse>{c.Subject}</noparse> <color=#7FA9BF>{c.When}</color>";
            }
        }

        protected override void OnVisibility(bool v)
        {
            foreach (var l in _lanes) Panel.Shapes.SetVisible(l, v);
            foreach (var r in _rows) { Panel.Shapes.SetVisible(r.dot, v); r.text.SetVisible(v); }
        }
    }
}
