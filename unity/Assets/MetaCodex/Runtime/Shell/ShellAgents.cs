using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MetaCodex.App;
using MetaCodex.Terminal;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The harness side of the shell, all data from MQH's public API: AGENTS (providers, sign-in through the
    /// provider's own CLI in a terminal, install, enable), THREADS (projects and their threads), THREAD (the
    /// timeline, a composer, approvals, stop, and jumps to the workspace's files / git / terminal) and NEW
    /// THREAD (provider, model, permissions, workspace, first prompt).
    public sealed partial class MetaCodexShell
    {
        private string _hostId;
        private JArray _providerStates = new JArray(), _providerCatalog = new JArray(), _projects = new JArray(), _threads = new JArray();
        private string _agentSel, _projectSel, _threadSel, _threadOpen;
        private JObject _threadInfo;
        private JArray _interactions = new JArray();
        private readonly List<string> _tlLines = new List<string>();
        private int _tlScroll;
        private long _tlMaxSeq = -1;
        private float _nextThreadPoll;
        private string _ntProvider, _ntModel;
        private JArray _ntModels = new JArray();

        private UiList _agentList, _projectList, _threadList, _ntProviders, _ntModelList;
        private UiLabel _agentDetail, _agentHint, _threadsStatus, _thTitle, _thMeta, _thTimeline, _thAsk, _ntSummary;
        private UiTextField _thComposer, _ntPrompt;
        private readonly List<UiButton> _thAskButtons = new List<UiButton>();
        private UiSegmented _ntPerm, _ntWork;

        public string LastLink { get; private set; }
        public string HostId => _hostId;

        // ------------------------------------------------------------------ AGENTS

        private void BuildAgents()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("agents", "AGENTS · PROVIDERS", 0.70f, 0.54f);
            var c = p.Content;
            float leftW = 0.28f;
            _agentList = p.Add(new UiList(0.034f, (item, idx) => { _agentSel = item.Tag as string; ShowAgent(); }) { Rect = new Rect(c.x, c.y, leftW, c.height) });
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            _agentDetail = p.Add(new UiLabel("", t.SmallSize * 1.1f) { Rect = rs.Next(0.20f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("SIGN IN ▶", () => SignIn(_agentSel)),
                ("INSTALL / UPDATE", () => InstallAgent(_agentSel)),
                ("⟳", RefreshAgents),
            }, primary: 0);
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("ENABLE / DISABLE", ToggleAgentEnabled),
                ("NEW THREAD →", () => OpenNewThread(_agentSel)),
                ("⇗ OPEN LINK", () => { if (LastLink != null) OpenBrowser(LastLink); else Toast("No link seen in a terminal yet", "warn"); }),
            });
            _agentHint = p.Add(new UiLabel("SIGN IN runs the provider's own login in a new terminal. When it prints a link, tap ⇗ on that terminal (or OPEN LINK here): the Horizon browser opens it. Codes go back with ⎘ PASTE. Logins stay in the CLI's own files in the app's private storage; MetaCodex never reads them.", t.SmallSize) { Rect = rs.Rest(), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
        }

        private void EnsureHost(Action then)
        {
            if (_hostId != null) { then(); return; }
            Mqh.Hosts(h =>
            {
                var arr = h as JArray;
                var host = arr?.FirstOrDefault(x => (string)x["status"] == "connected") ?? arr?.FirstOrDefault();
                _hostId = (string)host?["id"];
                if (_hostId == null) { Toast("MQH: " + (Mqh.LastError ?? "no host connected"), "error"); return; }
                then();
            });
        }

        public void RefreshAgents()
        {
            if (_agentList == null) return;
            _agentDetail.Set("asking MQH at " + Settings.mqhUrl + " …", UiTheme.Current.TextDim);
            Mqh.ProvidersState(st =>
            {
                if (st == null) { _agentDetail.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable") + "\nStart MQH (Settings > DAEMON) and try ⟳.", UiTheme.Current.Danger); _agentList.SetItems(new UiListItem[0]); return; }
                _providerStates = st["providers"] as JArray ?? new JArray();
                Mqh.Providers(cat =>
                {
                    _providerCatalog = cat as JArray ?? new JArray();
                    var t = UiTheme.Current;
                    _agentList.SetItems(_providerStates.Select(x => new UiListItem
                    {
                        Icon = StatusIcon((string)x["status"]),
                        IconColor = StatusColor((string)x["status"]),
                        Title = (string)x["displayName"] ?? (string)x["providerId"],
                        Sub = (string)x["accountEmail"] ?? (string)x["installedVersion"] ?? "",
                        Badge = StatusBadge((string)x["status"]),
                        BadgeColor = StatusColor((string)x["status"]),
                        Tag = (string)x["providerId"],
                    }), keepScroll: true);
                    if (_agentSel == null && _providerStates.Count > 0) _agentSel = (string)_providerStates[0]["providerId"];
                    ShowAgent();
                });
            });
        }

        private static string StatusIcon(string s) => s == "ready" || s == "authenticated" ? "●" : s == "unauthenticated" || s == "auth_required" || s == "expired" ? "○" : s == "not_installed" ? "⇣" : "⚠";
        private Color StatusColor(string s)
        {
            var t = UiTheme.Current;
            return s == "ready" || s == "authenticated" ? t.Ok : s == "unauthenticated" || s == "auth_required" || s == "expired" ? t.Warn : s == "not_installed" ? t.TextDim : t.Danger;
        }
        private static string StatusBadge(string s) =>
            s == "ready" || s == "authenticated" ? "READY" : s == "unauthenticated" || s == "auth_required" ? "SIGN IN" : s == "expired" ? "EXPIRED" : s == "not_installed" ? "INSTALL" : (s ?? "?").ToUpperInvariant().Replace('_', ' ');

        private JToken AgentState(string id) => _providerStates.FirstOrDefault(x => (string)x["providerId"] == id);
        private JToken AgentCatalog(string id) => _providerCatalog.FirstOrDefault(x => (string)x["id"] == id);

        private void ShowAgent()
        {
            var t = UiTheme.Current;
            var st = AgentState(_agentSel);
            if (st == null) { _agentDetail.Set("Select a provider."); return; }
            var cat = AgentCatalog(_agentSel);
            string status = (string)st["status"] ?? "?";
            var sb = new StringBuilder();
            sb.Append($"<size=125%><b>{st["displayName"]}</b></size>  <color=#{ColorUtility.ToHtmlStringRGB(StatusColor(status))}>{StatusBadge(status)}</color>\n");
            if (st["installedVersion"]?.Type == JTokenType.String) sb.Append($"version {st["installedVersion"]}" + (st["minimumSupportedVersion"]?.Type == JTokenType.String ? $" (needs ≥ {st["minimumSupportedVersion"]})" : "") + "\n");
            if (st["accountEmail"]?.Type == JTokenType.String) sb.Append($"account {st["accountEmail"]}" + (st["planLabel"]?.Type == JTokenType.String ? $" · {st["planLabel"]}" : "") + "\n");
            if (st["statusMessage"]?.Type == JTokenType.String) sb.Append($"<color=#FFB547>{st["statusMessage"]}</color>\n");
            sb.Append($"sign in: <color=#8FEBFF>{(string)st["loginCommand"] ?? (string)st["localLoginCommand"] ?? "—"}</color>\n");
            if (cat != null)
            {
                var modes = cat["capabilities"]?["permissionModes"] as JArray;
                if (modes != null) sb.Append("permissions: " + string.Join(", ", modes.Select(m => (string)m)) + "\n");
                sb.Append((cat["available"]?.Value<bool>() ?? true) ? "enabled in MQH" : "<color=#7FA9BF>disabled in MQH</color>");
                var hint = (string)cat["strings"]?["signInHint"];
                if (hint != null) sb.Append("\n<color=#7FA9BF>" + hint + "</color>");
            }
            _agentDetail.Set(sb.ToString(), t.Text);
        }

        /// The provider's own login, in a new terminal: the CLI owns its credentials (MetaCodex never reads them).
        public void SignIn(string providerId)
        {
            var st = AgentState(providerId);
            string cmd = (string)st?["loginCommand"] ?? (string)st?["localLoginCommand"];
            if (string.IsNullOrEmpty(cmd)) { Toast("No sign-in command for " + (providerId ?? "?"), "warn"); return; }
            if (!RunInNewTerminal(cmd)) return;
            Toast("Sign-in started: tap ⇗ on the terminal when a link shows", "info");
        }

        /// Opens a new terminal and types a command into it (sign-in flows, scaffolds).
        public bool RunInNewTerminal(string command)
        {
            if (app == null) { Toast("No terminal here (Editor): " + command, "warn"); return false; }
            var inst = app.NewTerminal();
            if (inst?.Session == null) { Toast("Terminal not ready", "error"); return false; }
            inst.Session.Write(Encoding.UTF8.GetBytes(command + "\r"));
            return true;
        }

        private void InstallAgent(string providerId)
        {
            var st = AgentState(providerId);
            if (st == null) return;
            bool update = (string)st["status"] != "not_installed";
            EnsureHost(() =>
            {
                Toast($"{(update ? "Updating" : "Installing")} {st["displayName"]} … (a few minutes)", "info");
                Mqh.InstallProviderCli(_hostId, providerId, update, r =>
                {
                    Toast(r != null ? $"{st["displayName"]}: {(update ? "update" : "install")} finished" : "Install failed: " + Mqh.LastError + " (or run mcx-setup in a terminal)", r != null ? "ok" : "error");
                    RefreshAgents();
                });
            });
        }

        private void ToggleAgentEnabled()
        {
            var cat = AgentCatalog(_agentSel);
            if (cat == null) return;
            bool on = !(cat["available"]?.Value<bool>() ?? true);
            Mqh.SetProviderEnabled(_agentSel, on, r => { Toast(r != null ? $"{_agentSel} {(on ? "enabled" : "disabled")}" : "MQH: " + Mqh.LastError, r != null ? "ok" : "error"); RefreshAgents(); });
        }

        // ------------------------------------------------------------------ THREADS

        private void BuildThreads()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("threads", "THREADS · MQH", 0.74f, 0.56f);
            var c = p.Content;
            float leftW = 0.24f;
            var ls = new UiStack(new Rect(c.x, c.y, leftW, c.height), t.Gap);
            p.Add(new UiLabel("PROJECTS", t.SmallSize) { Rect = ls.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _projectList = p.Add(new UiList(0.032f, (item, idx) => { _projectSel = item.Tag as string; Settings.lastProject = _projectSel; RefreshThreadList(); }) { Rect = ls.Next(c.height - 0.018f - t.ButtonHeight - t.Gap * 2) });
            p.Add(new UiButton("+ PROJECT…", NewProjectPrompt) { Rect = ls.Next(t.ButtonHeight) });
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            _threadsStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Next(0.02f), Color = t.TextDim });
            _threadList = p.Add(new UiList(0.036f, (item, idx) => { var id = item.Tag as string; if (id == _threadSel) OpenThread(id); _threadSel = id; }) { Rect = rs.Next(c.height - 0.02f - t.ButtonHeight - t.Gap * 2) });
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW THREAD…", () => OpenNewThread(null)),
                ("OPEN", () => OpenThread(_threadSel)),
                ("STOP", () => { if (_threadSel != null) Mqh.StopThread(_threadSel, r => { Toast(r != null ? "Stopping" : "MQH: " + Mqh.LastError); RefreshThreadList(); }); }),
                ("⟳", RefreshThreads),
            }, primary: 0, danger: 2);
        }

        public void RefreshThreads()
        {
            if (_projectList == null) return;
            _threadsStatus.Set("asking MQH …", UiTheme.Current.TextDim);
            Mqh.Projects(pr =>
            {
                if (pr == null) { _threadsStatus.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                _projects = pr as JArray ?? new JArray();
                var t = UiTheme.Current;
                _projectList.SetItems(_projects.Select(x => new UiListItem
                {
                    Icon = "▣",
                    Title = (string)x["name"],
                    Sub = (string)x["sources"]?.FirstOrDefault()?["path"] ?? "",
                    Tag = (string)x["id"],
                }), keepScroll: true);
                if (_projectSel == null || !_projects.Any(x => (string)x["id"] == _projectSel))
                    _projectSel = _projects.Any(x => (string)x["id"] == Settings.lastProject) ? Settings.lastProject : (string)_projects.FirstOrDefault()?["id"];
                int idx = _projects.ToList().FindIndex(x => (string)x["id"] == _projectSel);
                if (idx >= 0) _projectList.SelectIndex(idx, notify: false);
                RefreshThreadList();
            });
        }

        private void RefreshThreadList()
        {
            if (_threadList == null) return;
            if (_projectSel == null) { _threadsStatus.Set("No project yet: + PROJECT… adds a folder of the Linux userland (e.g. /root/myapp).", UiTheme.Current.TextDim); _threadList.SetItems(new UiListItem[0]); return; }
            Mqh.Threads(_projectSel, th =>
            {
                var t = UiTheme.Current;
                _threads = th as JArray ?? new JArray();
                _threadsStatus.Set($"{_threads.Count} threads · tap twice (or OPEN) to open", t.TextDim);
                _threadList.SetItems(_threads.Select(x =>
                {
                    string st = (string)x["runtime"]?["displayStatus"] ?? (string)x["status"] ?? "";
                    bool pending = x["hasPendingInteraction"]?.Value<bool>() == true;
                    return new UiListItem
                    {
                        Icon = pending ? "?" : st == "running" || st == "starting" ? "▶" : st == "error" ? "⚠" : "◆",
                        IconColor = pending ? t.Warn : st == "running" || st == "starting" ? t.Ok : st == "error" ? t.Danger : t.Accent,
                        Title = (string)x["title"] ?? (string)x["titleFallback"] ?? (string)x["id"],
                        Sub = $"{x["providerId"]} · {x["environmentBranchName"] ?? ""} · {Ago(x["updatedAt"]?.Value<long>() ?? 0)}",
                        Badge = pending ? "NEEDS YOU" : st.ToUpperInvariant(),
                        BadgeColor = pending ? t.Warn : st == "error" ? t.Danger : st == "running" ? t.Ok : t.TextDim,
                        Tag = (string)x["id"],
                    };
                }), keepScroll: true);
            });
        }

        private static string Ago(long ms)
        {
            if (ms <= 0) return "";
            var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return d.TotalMinutes < 1 ? "now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalDays} d";
        }

        private void NewProjectPrompt()
        {
            string start = !string.IsNullOrEmpty(_filesPath) && _filesPath != "/" ? _filesPath : "/root/";
            UiKeyboard.Edit(start, false, null, path =>
            {
                path = (path ?? "").Trim().TrimEnd('/');
                if (path.Length < 2) return;
                EnsureHost(() => Mqh.CreateProject(path.Split('/').Last(), _hostId, path, r =>
                {
                    if (r == null) { Toast("Project not added: " + Mqh.LastError, "error"); return; }
                    _projectSel = (string)r["id"];
                    Settings.lastProject = _projectSel;
                    Toast("Project " + r["name"] + " added", "ok");
                    RefreshThreads();
                }));
            });
        }

        // ------------------------------------------------------------------ THREAD

        private void BuildThread()
        {
            _thAskButtons.Clear(); // RebuildPanels (accent, themes) builds the panel again
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("thread", "THREAD", 0.74f, 0.66f);
            var c = p.Content;
            var st = new UiStack(c, t.Gap);
            _thTitle = p.Add(new UiLabel("", t.BodySize * 1.1f) { Rect = st.Next(0.026f) });
            _thMeta = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.02f), Color = t.TextDim, UseMono = true });
            var tlRect = st.Next(c.height - 0.026f - 0.02f - 0.05f - t.ButtonHeight * 3 - t.Gap * 7);
            _thTimeline = p.Add(new UiLabel("", t.SmallSize * 1.05f) { Rect = new Rect(tlRect.x, tlRect.y, tlRect.width - 0.05f, tlRect.height), Wrap = true, Align = TextAlignmentOptions.BottomLeft });
            p.Add(new UiButton("▲", () => { _tlScroll = Mathf.Min(_tlScroll + 4, Math.Max(0, _tlLines.Count - 1)); RenderTimeline(); }, UiButtonStyle.Ghost) { Rect = new Rect(tlRect.xMax - 0.042f, tlRect.y, 0.042f, tlRect.height * 0.5f - t.Gap) });
            p.Add(new UiButton("▼", () => { _tlScroll = Mathf.Max(0, _tlScroll - 4); RenderTimeline(); }, UiButtonStyle.Ghost) { Rect = new Rect(tlRect.xMax - 0.042f, tlRect.y + tlRect.height * 0.5f, 0.042f, tlRect.height * 0.5f) });
            _thAsk = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.05f), Wrap = true, Color = t.Warn, Align = TextAlignmentOptions.TopLeft });
            var askRow = st.Next(t.ButtonHeight);
            var asks = new (string label, string decision)[] { ("ALLOW ONCE", "allow_once"), ("ALLOW SESSION", "allow_for_session"), ("DENY", "deny") };
            float aw = (askRow.width - t.Gap * 2) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var d = asks[i].decision;
                _thAskButtons.Add(p.Add(new UiButton(asks[i].label, () => Resolve(d), i == 0 ? UiButtonStyle.Primary : i == 2 ? UiButtonStyle.Danger : UiButtonStyle.Normal) { Rect = new Rect(askRow.x + i * (aw + t.Gap), askRow.y, aw, askRow.height) }));
            }
            var comp = st.NextSplit(t.ButtonHeight, 0.80f, out var send);
            _thComposer = p.Add(new UiTextField("", "", s => SendToThread(s), "message the agent…") { Rect = comp, CaptionWidth = 0f });
            p.Add(new UiButton("SEND ▶", () => SendToThread(_thComposer.Text), UiButtonStyle.Primary) { Rect = send });
            AddButtons(p, st.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("■ STOP", () => { if (_threadOpen != null) Mqh.StopThread(_threadOpen, r => Toast(r != null ? "Stopping the turn" : "MQH: " + Mqh.LastError)); }),
                ("FILES", () => { var path = ThreadPath(); if (path != null) { OpenPanel("files"); FilesNavigate(path); } }),
                ("GIT", () => { var path = ThreadPath(); if (path != null) { OpenPanel("git"); GitOpen(path); } }),
                ("TERMINAL", () => { var path = ThreadPath(); if (path != null) RunInNewTerminal("cd " + GitService.Quote(path) + " && git status -sb"); }),
                ("⟳", () => { _tlMaxSeq = -1; PollThread(); }),
            }, danger: 0);
            p.Tick += _ => { if (_threadOpen != null && Time.unscaledTime >= _nextThreadPoll) PollThread(); };
            SetAskVisible(false);
        }

        private string ThreadPath()
        {
            var path = (string)_threadInfo?["environmentPath"] ?? (string)_threads.FirstOrDefault(x => (string)x["id"] == _threadOpen)?["environmentPath"];
            if (path == null) Toast("This thread has no workspace yet", "warn");
            return path;
        }

        public void OpenThread(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _threadOpen = id;
            _tlLines.Clear();
            _tlScroll = 0;
            _tlMaxSeq = -1;
            _thTitle?.Set("loading …");
            _thTimeline?.Set("");
            OpenPanel("thread");
            PollThread();
        }

        /// Refreshes the open thread: its row, the timeline (when it grew) and pending approvals.
        public void PollThread()
        {
            if (_threadOpen == null || _thTitle == null) return;
            string id = _threadOpen;
            _nextThreadPoll = Time.unscaledTime + 1.5f;
            Mqh.Threads(_projectSel, th =>
            {
                var row = (th as JArray)?.FirstOrDefault(x => (string)x["id"] == id) as JObject;
                if (row != null) _threadInfo = row;
                ShowThreadHeader();
            });
            Mqh.Timeline(id, tl =>
            {
                if (tl == null || id != _threadOpen) return;
                long max = tl["maxSeq"]?.Value<long>() ?? 0;
                if (max == _tlMaxSeq) return;
                _tlMaxSeq = max;
                _tlLines.Clear();
                foreach (var r in tl["rows"] as JArray ?? new JArray()) AddTimelineRow(r, 0);
                RenderTimeline();
            });
            Mqh.Interactions(id, ia =>
            {
                if (id != _threadOpen) return;
                _interactions = ia as JArray ?? new JArray();
                ShowAsk();
            });
        }

        private void ShowThreadHeader()
        {
            var x = _threadInfo;
            if (x == null) { _thTitle.Set(_threadOpen ?? ""); return; }
            string st = (string)x["runtime"]?["displayStatus"] ?? (string)x["status"] ?? "";
            _thTitle.Set($"<b>{Esc((string)x["title"] ?? (string)x["titleFallback"] ?? "thread")}</b>  <color=#{ColorUtility.ToHtmlStringRGB(st == "error" ? UiTheme.Current.Danger : st == "running" ? UiTheme.Current.Ok : UiTheme.Current.TextDim)}>{st.ToUpperInvariant()}</color>");
            _thMeta.Set($"{x["providerId"]} · {x["environmentPath"] ?? "no workspace yet"} · ⎇ {x["environmentBranchName"] ?? "-"}{(x["environmentIsWorktree"]?.Value<bool>() == true ? " (worktree)" : "")}");
        }

        private static string Esc(string s) => s == null ? "" : "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";
        private static string Cut(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        private void AddTimelineRow(JToken r, int depth)
        {
            string kind = (string)r["kind"];
            string pad = depth > 0 ? "   " : "";
            switch (kind)
            {
                case "conversation":
                {
                    bool user = (string)r["role"] == "user";
                    _tlLines.Add(pad + (user ? "<color=#8FEBFF><b>▶ YOU</b></color>  " : "<color=#4DFFA6><b>◆ AGENT</b></color>  ") + Esc(Cut((string)r["text"], 900)));
                    break;
                }
                case "system":
                {
                    bool err = (string)r["status"] == "error" || (string)r["systemKind"] == "error";
                    _tlLines.Add(pad + (err ? "<color=#FF5468>⚠ " : "<color=#7FA9BF>· ") + Esc(Cut((string)r["title"], 200)) + "</color>");
                    break;
                }
                case "work":
                {
                    string wk = (string)r["workKind"];
                    string status = (string)r["status"] ?? "";
                    string tail = status == "running" || status == "pending" ? " <color=#FFB547>…</color>" : status == "error" || status == "failed" ? " <color=#FF5468>✕</color>" : "";
                    if (wk == "command") _tlLines.Add(pad + "<color=#FFB547>$</color> " + Esc(Cut((string)r["command"], 160)) + (r["exitCode"]?.Type == JTokenType.Integer ? $" <color=#7FA9BF>→ {r["exitCode"]}</color>" : "") + tail);
                    else if (wk == "tool") _tlLines.Add(pad + "<color=#B28CFF>⚙ " + Esc(Cut((string)r["toolName"], 60)) + "</color>" + tail);
                    else if (wk == "file-change") _tlLines.Add(pad + "<color=#FFB547>✎ " + Esc(Cut((string)r["change"]?["path"] ?? "file", 120)) + "</color>" + tail);
                    else _tlLines.Add(pad + "<color=#7FA9BF>· " + Esc(wk ?? "work") + "</color>" + tail);
                    break;
                }
                case "turn":
                {
                    var children = r["children"] as JArray;
                    if (children != null && children.Count > 0) { foreach (var ch in children) AddTimelineRow(ch, depth + 1); }
                    else _tlLines.Add(pad + $"<color=#7FA9BF>· {r["summaryCount"]} steps ({r["status"]})</color>");
                    break;
                }
            }
        }

        /// Shows the newest lines that fit (or older ones after ▲), measured with TextMeshPro.
        private void RenderTimeline()
        {
            if (_thTimeline?.Label == null) return;
            if (_tlLines.Count == 0) { _thTimeline.Set("<color=#7FA9BF>(empty)</color>"); return; }
            var tmp = _thTimeline.Label.Tmp;
            float w = _thTimeline.Rect.width, h = _thTimeline.Rect.height;
            int end = Mathf.Clamp(_tlLines.Count - _tlScroll, 1, _tlLines.Count);
            var shown = new List<string>();
            for (int i = end - 1; i >= 0; i--)
            {
                shown.Insert(0, _tlLines[i]);
                if (shown.Count > 1 && tmp.GetPreferredValues(string.Join("\n", shown), w, 0f).y > h) { shown.RemoveAt(0); break; }
            }
            string more = end - shown.Count > 0 ? $"<color=#7FA9BF>▲ {end - shown.Count} earlier</color>\n" : "";
            _thTimeline.Set(more + string.Join("\n", shown));
        }

        private void ShowAsk()
        {
            var pending = _interactions.FirstOrDefault(x => (string)x["status"] == "pending" || x["status"] == null);
            var payload = pending?["payload"];
            if (pending == null || payload == null) { SetAskVisible(false); return; }
            string kind = (string)payload["kind"];
            if (kind == "approval")
            {
                var subj = payload["subject"];
                string what = (string)subj?["command"] ?? (string)subj?["toolName"] ?? (string)subj?["path"] ?? (string)subj?["kind"] ?? "an action";
                _thAsk.Set($"APPROVAL: {Esc(Cut(what, 220))}" + (payload["reason"]?.Type == JTokenType.String ? $"\n<color=#7FA9BF>{Esc(Cut((string)payload["reason"], 160))}</color>" : ""));
                SetAskVisible(true);
            }
            else
            {
                _thAsk.Set($"The agent asks something ({kind}): answer it in a terminal with `mqh thread` or in the thread later.");
                _thAsk.SetVisible(true);
                foreach (var b in _thAskButtons) b.SetVisible(false);
            }
        }

        private void SetAskVisible(bool v)
        {
            _thAsk?.SetVisible(v);
            foreach (var b in _thAskButtons) b.SetVisible(v);
        }

        private void Resolve(string decision)
        {
            var pending = _interactions.FirstOrDefault(x => (string)x["status"] == "pending" || x["status"] == null);
            if (pending == null || _threadOpen == null) return;
            Mqh.ResolveApproval(_threadOpen, (string)pending["id"], decision, r =>
            {
                Toast(r != null ? "Answered: " + decision.Replace('_', ' ') : "MQH: " + Mqh.LastError, r != null ? "ok" : "error");
                SetAskVisible(false);
                PollThread();
            });
        }

        private void SendToThread(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _threadOpen == null) return;
            Mqh.SendMessage(_threadOpen, text.Trim(), r =>
            {
                if (r == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                _thComposer.Set("");
                Toast((string)r["delivery"] == "queued" ? "Queued (the agent is busy)" : "Sent", "ok");
                _tlScroll = 0;
                PollThread();
            });
        }

        // ------------------------------------------------------------------ NEW THREAD

        private void BuildNewThread()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("thread-new", "NEW THREAD", 0.76f, 0.56f);
            var c = p.Content;
            float colW = 0.22f;
            var a = new UiStack(new Rect(c.x, c.y, colW, c.height), t.Gap);
            p.Add(new UiLabel("PROVIDER", t.SmallSize) { Rect = a.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _ntProviders = p.Add(new UiList(0.032f, (item, idx) => { _ntProvider = item.Tag as string; LoadModels(); }) { Rect = a.Rest() });
            var b = new UiStack(new Rect(c.x + colW + t.Gap * 2, c.y, colW, c.height), t.Gap);
            p.Add(new UiLabel("MODEL", t.SmallSize) { Rect = b.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _ntModelList = p.Add(new UiList(0.032f, (item, idx) => { _ntModel = item.Tag as string; ShowNewThreadSummary(); }) { Rect = b.Rest() });
            var r = new UiStack(new Rect(c.x + (colW + t.Gap * 2) * 2, c.y, c.width - (colW + t.Gap * 2) * 2, c.height), t.Gap);
            _ntSummary = p.Add(new UiLabel("", t.SmallSize) { Rect = r.Next(0.07f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            int perm = Math.Max(0, Array.IndexOf(new[] { "accept-edits", "auto", "full" }, Settings.permissionMode));
            _ntPerm = p.Add(new UiSegmented("PERMISSIONS", new[] { "EDITS", "AUTO", "FULL" }, perm, i => { Settings.permissionMode = new[] { "accept-edits", "auto", "full" }[i]; ShowNewThreadSummary(); }) { Rect = r.Next(0.032f), LabelWidth = 0.1f });
            _ntWork = p.Add(new UiSegmented("WORKSPACE", new[] { "CHECKOUT", "WORKTREE" }, Settings.workspaceMode == "worktree" ? 1 : 0, i => { Settings.workspaceMode = i == 1 ? "worktree" : "local"; ShowNewThreadSummary(); }) { Rect = r.Next(0.032f), LabelWidth = 0.1f });
            p.Add(new UiLabel("FIRST MESSAGE", t.SmallSize) { Rect = r.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _ntPrompt = p.Add(new UiTextField("", "", null, "what should the agent do?") { Rect = r.Next(0.034f), CaptionWidth = 0f });
            p.Add(new UiLabel("CHECKOUT works in the project folder itself; WORKTREE gives the thread its own git worktree and branch. EDITS asks before commands; FULL never asks.", t.SmallSize) { Rect = r.Next(0.06f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            var go = r.Next(t.ButtonHeight);
            p.Add(new UiButton("START ▶", StartThread, UiButtonStyle.Primary) { Rect = go });
        }

        public void OpenNewThread(string providerId)
        {
            _ntProvider = providerId ?? (string.IsNullOrEmpty(Settings.defaultProvider) ? _ntProvider : Settings.defaultProvider);
            OpenPanel("thread-new");
            Mqh.ProvidersState(st =>
            {
                if (st == null) { _ntSummary?.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                _providerStates = st["providers"] as JArray ?? new JArray();
                var t = UiTheme.Current;
                var list = _providerStates.Where(x => (string)x["status"] != "not_installed").ToList();
                _ntProviders.SetItems(list.Select(x => new UiListItem
                {
                    Icon = StatusIcon((string)x["status"]),
                    IconColor = StatusColor((string)x["status"]),
                    Title = (string)x["displayName"],
                    Badge = StatusBadge((string)x["status"]),
                    BadgeColor = StatusColor((string)x["status"]),
                    Tag = (string)x["providerId"],
                }));
                if (_ntProvider == null || !list.Any(x => (string)x["providerId"] == _ntProvider)) _ntProvider = (string)list.FirstOrDefault()?["providerId"];
                int i = list.FindIndex(x => (string)x["providerId"] == _ntProvider);
                if (i >= 0) _ntProviders.SelectIndex(i, notify: false);
                LoadModels();
            });
        }

        private void LoadModels()
        {
            if (_ntProvider == null) { ShowNewThreadSummary(); return; }
            string prov = _ntProvider;
            EnsureHost(() => Mqh.Models(prov, _hostId, m =>
            {
                if (prov != _ntProvider) return;
                _ntModels = m?["models"] as JArray ?? new JArray();
                if (!_ntModels.Any(x => (string)x["id"] == _ntModel)) _ntModel = _ntModels.Any(x => (string)x["id"] == Settings.defaultModel) ? Settings.defaultModel : (string)_ntModels.FirstOrDefault()?["id"];
                _ntModelList.SetItems(_ntModels.Select(x => new UiListItem
                {
                    Icon = "◇",
                    Title = (string)x["displayName"] ?? (string)x["id"],
                    Sub = Cut((string)x["description"], 60),
                    Tag = (string)x["id"],
                }));
                int i = _ntModels.ToList().FindIndex(x => (string)x["id"] == _ntModel);
                if (i >= 0) _ntModelList.SelectIndex(i, notify: false);
                var err = m?["modelLoadError"];
                if (err != null && err.Type == JTokenType.Object) Toast("Models: " + Cut((string)err["detail"], 120), "warn");
                ShowNewThreadSummary();
            }));
        }

        private void ShowNewThreadSummary()
        {
            var proj = _projects.FirstOrDefault(x => (string)x["id"] == _projectSel);
            _ntSummary?.Set($"<color=#7FA9BF>PROJECT</color> <b>{(string)proj?["name"] ?? "— pick one in THREADS"}</b>\n<color=#7FA9BF>AGENT</color> {_ntProvider ?? "—"} · {_ntModel ?? "default model"}\n<color=#7FA9BF>MODE</color> {Settings.permissionMode} · {(Settings.workspaceMode == "worktree" ? "new worktree" : "project checkout")}");
        }

        /// The create-thread request MQH expects (also used by tests).
        public JObject NewThreadBody(string projectId, string providerId, string model, string prompt)
        {
            var workspace = Settings.workspaceMode == "worktree"
                ? new JObject { ["type"] = "managed-worktree", ["baseBranch"] = new JObject { ["kind"] = "default" } }
                : new JObject { ["type"] = "unmanaged", ["path"] = null };
            var body = new JObject
            {
                ["projectId"] = projectId,
                ["providerId"] = providerId,
                ["origin"] = "app",
                ["input"] = new JArray { MqhClient.TextInput(prompt) },
                ["permissionMode"] = Settings.permissionMode,
                ["environment"] = new JObject { ["type"] = "host", ["hostId"] = _hostId, ["workspace"] = workspace },
            };
            if (!string.IsNullOrEmpty(model)) body["model"] = model;
            return body;
        }

        private void StartThread()
        {
            string prompt = _ntPrompt?.Text?.Trim();
            if (string.IsNullOrEmpty(prompt)) { Toast("Type the first message", "warn"); return; }
            if (_projectSel == null) { Toast("Pick or add a project in THREADS first", "warn"); OpenPanel("threads"); return; }
            if (_ntProvider == null) { Toast("Pick a provider", "warn"); return; }
            EnsureHost(() => Mqh.CreateThread(NewThreadBody(_projectSel, _ntProvider, _ntModel, prompt), r =>
            {
                if (r == null) { Toast("Thread not started: " + Mqh.LastError, "error"); return; }
                Settings.defaultProvider = _ntProvider;
                Settings.defaultModel = _ntModel ?? "";
                OnSettingsChanged();
                _ntPrompt.Set("");
                ClosePanel("thread-new");
                RefreshThreadList();
                OpenThread((string)r["id"]);
            }));
        }

        private static void AddButtons(UiPanel p, Rect row, (string label, Action act)[] items, int primary = -1, int danger = -1)
        {
            var t = UiTheme.Current;
            float bw = (row.width - t.Gap * (items.Length - 1)) / items.Length;
            for (int i = 0; i < items.Length; i++)
                p.Add(new UiButton(items[i].label, items[i].act, i == primary ? UiButtonStyle.Primary : i == danger ? UiButtonStyle.Danger : UiButtonStyle.Normal)
                { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height) });
        }

        // ------------------------------------------------------------------ links in terminals

        private float _nextLinkScan;
        private readonly Dictionary<TerminalInstance, long> _linkVersion = new Dictionary<TerminalInstance, long>();
        private readonly Dictionary<TerminalInstance, UiButton> _linkButtons = new Dictionary<TerminalInstance, UiButton>();
        private readonly Dictionary<TerminalInstance, string> _links = new Dictionary<TerminalInstance, string>();

        /// Twice a second: the newest http(s) link on each changed terminal screen (sign-in flows print one).
        private void ScanTerminalLinks()
        {
            if (app == null || Time.unscaledTime < _nextLinkScan) return;
            _nextLinkScan = Time.unscaledTime + 0.5f;
            foreach (var inst in app.Terminals)
            {
                if (inst.Terminal == null) continue;
                long v = inst.Terminal.Version;
                if (_linkVersion.TryGetValue(inst, out var seen) && seen == v) continue;
                _linkVersion[inst] = v;
                var url = TerminalLinks.Last(inst.Terminal);
                if (url == null || _links.TryGetValue(inst, out var old) && old == url) continue;
                _links[inst] = url;
                LastLink = url;
                if (_linkButtons.TryGetValue(inst, out var b)) { b.SetEnabled(true); b.SetSelected(true); }
                Toast("Link in " + inst.Name + ": tap ⇗ to open it", "info");
            }
        }

        private void OpenTerminalLink(TerminalInstance inst)
        {
            if (inst != null && _links.TryGetValue(inst, out var url)) OpenBrowser(url);
            else Toast("No link on this terminal yet", "warn");
        }
    }

    /// Finds URLs on a terminal screen, joining rows the terminal wrapped (a full row continues on the next).
    public static class TerminalLinks
    {
        private static readonly Regex Url = new Regex(@"https?://[^\s""'<>`│┃]+", RegexOptions.Compiled);

        public static string Last(VtTerminal t)
        {
            var text = ScreenText(t);
            string last = null;
            foreach (Match m in Url.Matches(text)) last = m.Value;
            return last?.TrimEnd('.', ',', ')', ']', ';', ':', '\'', '"');
        }

        public static List<string> All(VtTerminal t)
        {
            var list = new List<string>();
            foreach (Match m in Url.Matches(ScreenText(t))) list.Add(m.Value.TrimEnd('.', ',', ')', ']', ';', ':', '\'', '"'));
            return list;
        }

        public static string ScreenText(VtTerminal t)
        {
            var sb = new StringBuilder(t.Rows * (t.Cols + 1));
            for (int r = 0; r < t.Rows; r++)
            {
                var row = t.GetRow(r);
                int last = -1;
                for (int c = 0; c < row.Length; c++) if (row[c].Rune != 0 && row[c].Rune != ' ') last = c;
                for (int c = 0; c <= last; c++)
                {
                    int rune = row[c].Rune;
                    if (rune == 0) sb.Append(' ');
                    else if (rune < 0x10000) sb.Append((char)rune);
                    else sb.Append(char.ConvertFromUtf32(rune));
                }
                bool full = last == row.Length - 1; // wrapped: the text continues on the next row
                if (!full) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
