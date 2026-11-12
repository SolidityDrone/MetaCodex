using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MetaCodex.AI;
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
        private long _tlMaxSeq = -1;
        private float _nextThreadPoll;
        private string _ntProvider, _ntModel;
        private JArray _ntModels = new JArray();

        private UiList _agentList, _projectList, _threadList, _ntProviders, _ntModelList;
        private UiLabel _agentDetail, _agentHint, _threadsStatus, _thTitle, _thMeta, _thAsk, _ntSummary;
        private UiTextField _thComposer, _ntPrompt;
        private readonly List<UiButton> _thAskButtons = new List<UiButton>();
        private UiSegmented _ntPerm, _ntWork;

        public string LastLink { get; private set; }
        public string HostId => _hostId;

        // ------------------------------------------------------------------ AGENTS

        private void BuildAgents()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("agents", "AGENTS · PROVIDERS", ControlPageW, ControlPageH); // CONTROL's one size
            var c0 = p.Content;
            const float getH = 0.058f;
            BuildAgentGetRow(p, new Rect(c0.x, c0.y, c0.width, getH)); // GET: OpenCode, Claude Code, Codex, more soon
            var c = new Rect(c0.x, c0.y + getH + t.Gap * 1.5f, c0.width, c0.height - getH - t.Gap * 1.5f);
            float leftW = 0.28f;
            _agentList = p.Add(new UiList(0.034f, (item, idx) => { _agentSel = item.Tag as string; ShowAgent(); }) { Rect = new Rect(c.x, c.y, leftW, c.height) });
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            _agentDetail = p.Add(new UiLabel("", t.SmallSize * 1.1f) { Rect = rs.Next(0.20f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("SIGN IN ▶", () => SignIn(_agentSel)),
                ("⚿ API KEY", () => OpenAgentKey(AgentIdFor(_agentSel))),
                ("⟳", () => { RefreshAgents(); ProbeAgents(); }),
            }, primary: 0);
            // each button says the one thing it does now (ShowAgent): ENABLE or DISABLE; UPDATE only for an installed agent
            // (GET above is how one is installed)
            var r2 = rs.Next(t.ButtonHeight);
            float bw2 = (r2.width - t.Gap * 2) / 3f;
            _agentToggle = p.Add(new UiButton("DISABLE", ToggleAgentEnabled) { Rect = new Rect(r2.x, r2.y, bw2, r2.height) });
            p.Add(new UiButton("NEW THREAD →", () => OpenNewThread(_agentSel)) { Rect = new Rect(r2.x + bw2 + t.Gap, r2.y, bw2, r2.height) });
            _agentUpdate = p.Add(new UiButton("UPDATE", () => InstallAgent(_agentSel)) { Rect = new Rect(r2.x + (bw2 + t.Gap) * 2, r2.y, bw2, r2.height) });
            _agentUpdate.SetVisible(false);
            _agentHint = p.Add(new UiLabel("GET installs an agent here. API KEY: paste your provider's key once (sealed in the vault, given to the agents MQH starts). SIGN IN runs the provider's own login in a new terminal: ⇗ on that terminal opens its link in Horizon's browser, codes go back with ⎘ PASTE; those logins stay in the CLI's own files, MetaCodex never reads them.", t.SmallSize) { Rect = rs.Rest(), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
        }

        /// MQH's provider id (acp-opencode, codex, claude-code) -> the GET agent it is (opencode, codex, claude).
        private static string AgentIdFor(string providerId) =>
            providerId == null ? "opencode" : providerId.Contains("opencode") ? "opencode" : providerId.Contains("codex") ? "codex" : providerId.Contains("claude") ? "claude" : "opencode";

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
                if (st == null)
                {
                    bool starting = MqhHost != null && (MqhHost.State == MqhHost.HostState.Starting || MqhHost.State == MqhHost.HostState.Waiting);
                    _agentDetail.Set(starting ? "◌ " + MqhHost.Status + "\nThe list fills in by itself when MQH answers."
                        : "⚠ " + (MqhHostLine().Length > 0 ? MqhHostLine() : Mqh.LastError ?? "MQH not reachable") + "\nMQH > STATUS > RESTART tries again.",
                        starting ? UiTheme.Current.TextDim : UiTheme.Current.Danger);
                    _agentList.SetItems(new UiListItem[0]);
                    return;
                }
                _providerStates = st["providers"] as JArray ?? new JArray();
                DisableUnsupportedProviders();
                _providerStates = SupportedOnly(_providerStates);
                Mqh.Providers(cat =>
                {
                    _providerCatalog = cat as JArray ?? new JArray();
                    RenderAgentList();
                    // agents MQH shows only once installed (opencode: visibility "installed") are not in providers/state;
                    // its execution options list them after a health probe on the headset
                    EnsureHost(() => Mqh.Models(null, _hostId, eo => { if (MergeInstalledProviders(eo)) { _providerStates = SupportedOnly(_providerStates); RenderAgentList(); } }));
                });
            });
        }

        /// The agents MetaCodex supports (owner 2026-11-10: "leave Claude Code, Codex, OpenCode"; Cursor and the rest are
        /// not shown and are switched off in MQH), in this order. Claude Code runs in a terminal until its MQH provider
        /// plugin installs at first use (its bundle carries Anthropic's SDK: never in the APK).
        public static readonly (string id, string name, string agent)[] SupportedProviders =
            { ("claude-code", "Claude Code", "claude"), ("codex", "Codex", "codex"), ("acp-opencode", "OpenCode", "opencode") };

        /// MQH's providers cut to the supported three (each present: as MQH has it, or what GET knows about it).
        private JArray SupportedOnly(JArray states)
        {
            var result = new JArray();
            foreach (var (id, name, agent) in SupportedProviders)
            {
                var mqh = states.FirstOrDefault(x => (string)x["providerId"] == id) as JObject;
                if (mqh != null) { mqh["displayName"] = name; result.Add(mqh); continue; }
                bool installed = _agentVersions.TryGetValue(agent, out var v) && v != null;
                result.Add(new JObject
                {
                    ["providerId"] = id,
                    ["displayName"] = name,
                    ["status"] = !installed ? "not_installed" : id == "claude-code" ? "terminal" : "ready",
                    ["installedVersion"] = v,
                    ["loginCommand"] = id == "claude-code" ? "claude auth login" : id == "acp-opencode" ? "opencode auth login" : "codex login",
                    ["statusMessage"] = id == "claude-code" ? "runs in a terminal (MQH threads with it: soon)" : !installed ? "GET it above" : null,
                });
            }
            return result;
        }

        private readonly HashSet<string> _providersSwitchedOff = new HashSet<string>();

        /// Cursor, Pi and any other provider MQH has but MetaCodex does not support: off in MQH (once per id), so no
        /// thread, model list or probe uses them.
        private void DisableUnsupportedProviders()
        {
            foreach (var x in _providerStates)
            {
                string id = (string)x["providerId"];
                if (string.IsNullOrEmpty(id) || SupportedProviders.Any(sp => sp.id == id) || !_providersSwitchedOff.Add(id)) continue;
                Mqh.SetProviderEnabled(id, false, r => { if (r == null) _providersSwitchedOff.Remove(id); });
            }
        }

        private void RenderAgentList()
        {
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
        }

        /// The providers execution-options lists that providers/state does not (installed-only agents): added as
        /// ready, with the version GET saw and the agent's own sign-in. True when something was added.
        public bool MergeInstalledProviders(JToken executionOptions)
        {
            bool added = false;
            foreach (var p in (executionOptions?["providers"] as JArray) ?? new JArray())
            {
                string id = (string)p["id"];
                if (string.IsNullOrEmpty(id)) continue;
                if (_providerStates.FirstOrDefault(x => (string)x["providerId"] == id) is JObject known)
                {
                    // listed already, from what GET saw: MQH found it installed, so it is
                    if ((string)known["status"] == "not_installed") { known["status"] = "ready"; known["statusMessage"] = "installed here; MQH runs it through ACP"; added = true; }
                    continue;
                }
                string agent = AgentIdFor(id);
                _agentVersions.TryGetValue(agent, out var v);
                _providerStates.Add(new JObject
                {
                    ["providerId"] = id,
                    ["displayName"] = (string)p["displayName"] ?? (string)p["name"] ?? id,
                    ["status"] = "ready",
                    ["installedVersion"] = v,
                    ["loginCommand"] = id.Contains("opencode") ? "opencode auth login" : null,
                    ["statusMessage"] = "installed here; MQH runs it through ACP",
                });
                added = true;
            }
            return added;
        }

        private static string StatusIcon(string s) => s == "ready" || s == "authenticated" ? "●" : s == "terminal" ? "▶" : s == "unauthenticated" || s == "auth_required" || s == "expired" ? "○" : s == "not_installed" ? "⇣" : "⚠";
        private Color StatusColor(string s)
        {
            var t = UiTheme.Current;
            return s == "ready" || s == "authenticated" ? t.Ok : s == "terminal" ? t.Accent : s == "unauthenticated" || s == "auth_required" || s == "expired" ? t.Warn : s == "not_installed" ? t.TextDim : t.Danger;
        }
        private static string StatusBadge(string s) =>
            s == "ready" || s == "authenticated" ? "READY" : s == "terminal" ? "TERMINAL" : s == "unauthenticated" || s == "auth_required" ? "SIGN IN" : s == "expired" ? "EXPIRED" : s == "not_installed" ? "INSTALL" : (s ?? "?").ToUpperInvariant().Replace('_', ' ');

        private JToken AgentState(string id) => _providerStates.FirstOrDefault(x => (string)x["providerId"] == id);
        private JToken AgentCatalog(string id) => _providerCatalog.FirstOrDefault(x => (string)x["id"] == id);

        private UiButton _agentToggle, _agentUpdate;

        /// AGENTS' buttons show the one action that applies to the picked agent.
        private void ShowAgentButtons(JToken st, JToken cat)
        {
            bool enabled = cat?["available"]?.Value<bool>() ?? true;
            _agentToggle?.SetText(enabled ? "DISABLE" : "ENABLE");
            _agentToggle?.SetEnabled(cat != null);
            _agentUpdate?.SetVisible(st != null && (string)st["status"] != "not_installed");
        }

        private void ShowAgent()
        {
            var t = UiTheme.Current;
            var st = AgentState(_agentSel);
            var cat = AgentCatalog(_agentSel);
            ShowAgentButtons(st, cat);
            if (st == null) { _agentDetail.Set("Select a provider."); return; }
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

        private readonly HashSet<string> _collapsedProjects = new HashSet<string>();
        private string _threadOpenProject;

        /// Threads grouped by project folder, like bb's sidebar: a project row (tap: fold / unfold) and its threads
        /// under it, newest first; a tap on a thread opens it.
        private void BuildThreads()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("threads", "Threads", 0.60f, 0.60f);
            var c = p.Content;
            var st = new UiStack(c, t.Gap);
            _threadsStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.02f), Color = t.TextDim });
            _threadList = p.Add(new UiList(0.034f, (item, idx) => ThreadRowPicked(item)) { Rect = st.Next(c.height - 0.02f - t.ButtonHeight - t.Gap * 2) });
            _projectList = _threadList; // one list now (projects are its group rows)
            AddButtons(p, st.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW THREAD…", () => OpenNewThread(null)),
                ("+ PROJECT…", NewProjectPrompt),
                ("⎇ WORKSPACES", () => TogglePanel("workspaces")), // checkouts and worktrees (owner 2026-11-11)
                ("◆ AGENTS", () => OpenControl("agents")), // providers: in CONTROL now (owner 2026-11-11)
                ("⟳", RefreshThreads),
            }, primary: 0);
        }

        private void ThreadRowPicked(UiListItem item)
        {
            if (!(item.Tag is string tag)) return;
            if (tag.StartsWith("p:"))
            {
                string pid = tag.Substring(2);
                _projectSel = pid;
                Settings.lastProject = pid;
                if (!_collapsedProjects.Remove(pid)) _collapsedProjects.Add(pid);
                RenderThreadGroups();
                return;
            }
            if (tag.StartsWith("t:")) { _threadSel = tag.Substring(2); OpenThread(_threadSel); }
            if (tag.StartsWith("w:")) OpenWorkspacesOn(tag.Substring(2)); // a workspace row: the WORKSPACES window on it
        }

        public void RefreshThreads()
        {
            if (_threadList == null) return;
            _threadsStatus.Set("asking MQH …", UiTheme.Current.TextDim);
            Mqh.Projects(pr =>
            {
                if (pr == null) { _threadsStatus.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                _projects = pr as JArray ?? new JArray();
                if (_projectSel == null || !_projects.Any(x => (string)x["id"] == _projectSel))
                    _projectSel = _projects.Any(x => (string)x["id"] == Settings.lastProject) ? Settings.lastProject : (string)_projects.FirstOrDefault()?["id"];
                RefreshThreadList();
            });
        }

        private void RefreshThreadList()
        {
            if (_threadList == null) return;
            Mqh.Threads(null, th =>
            {
                if (th == null) { _threadsStatus.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                _threads = th as JArray ?? new JArray();
                CtxThreadsUpdated();
                RebuildWorkspaceMap(); // THREADS' rows, the WORKSPACES window, THREAD's chip
                RenderThreadGroups();
            });
        }

        private static string ThreadStatus(JToken x) => (string)x["runtime"]?["displayStatus"] ?? (string)x["status"] ?? "";

        private void RenderThreadGroups()
        {
            if (_threadList == null) return;
            var t = UiTheme.Current;
            if (_projects.Count == 0) { _threadsStatus.Set("No project yet: + PROJECT… adds a folder of the Linux userland (e.g. /root/myapp).", t.TextDim); _threadList.SetItems(new UiListItem[0]); return; }
            var byProject = _threads.GroupBy(x => (string)x["projectId"] ?? "").ToDictionary(g => g.Key, g => g.OrderByDescending(x => x["updatedAt"]?.Value<long>() ?? 0).ToList());
            var spaces = WorkspaceMap.Build(_threads, _environments);
            long Latest(JToken pr) => byProject.TryGetValue((string)pr["id"], out var l) && l.Count > 0 ? l[0]["updatedAt"]?.Value<long>() ?? 0 : 0;
            var items = new List<UiListItem>();
            foreach (var pr in _projects.OrderByDescending(Latest))
            {
                string pid = (string)pr["id"];
                byProject.TryGetValue(pid, out var list);
                list = list ?? new List<JToken>();
                int running = list.Count(x => ThreadTimeline.IsWorking(ThreadStatus(x)));
                int asks = list.Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
                bool folded = _collapsedProjects.Contains(pid);
                items.Add(new UiListItem
                {
                    Icon = folded ? "▸" : "▾",
                    IconColor = t.TextDim,
                    Title = ((string)pr["name"] ?? "project").ToUpperInvariant() + (IsNativeProject((string)pr["sources"]?.FirstOrDefault()?["path"]) ? "  ◆ NATIVE" : ""),
                    TitleColor = pid == _projectSel ? t.TextAccent : t.TextDim,
                    Sub = Cut((string)pr["sources"]?.FirstOrDefault()?["path"] ?? "", 34),
                    Badge = asks > 0 ? asks + " NEED YOU" : running > 0 ? running + " RUNNING" : list.Count.ToString(),
                    BadgeColor = asks > 0 ? t.Warn : running > 0 ? t.Ok : t.TextDim,
                    Tag = "p:" + pid,
                });
                if (folded) continue;
                // the folders its threads work in: when there is more than the checkout (a worktree), each is a row
                // with its threads under it, so who shares what (and two agents in one folder) is plain
                spaces.TryGetValue(pid, out var groups);
                groups = groups ?? new List<WorkspaceGroup>();
                bool showSpaces = groups.Count > 1 || groups.Any(g => g.IsWorktree);
                void ThreadRow(JToken x, int indent)
                {
                    string st = ThreadStatus(x);
                    bool working = ThreadTimeline.IsWorking(st);
                    bool pending = x["hasPendingInteraction"]?.Value<bool>() == true;
                    bool held = (string)x["queuedWork"] == "waiting" && !working;
                    string id = (string)x["id"];
                    items.Add(new UiListItem
                    {
                        Indent = indent,
                        Icon = pending ? "?" : working ? "▶" : held ? "⏸" : st == "error" ? "⚠" : "◆",
                        IconColor = pending || held ? t.Warn : working ? t.Ok : st == "error" ? t.Danger : t.Accent,
                        Title = (string)x["title"] ?? (string)x["titleFallback"] ?? id,
                        TitleColor = id == _threadOpen ? t.TextAccent : (Color?)null,
                        Sub = showSpaces ? $"{x["providerId"]} · {Ago(x["updatedAt"]?.Value<long>() ?? 0)}" : $"{x["providerId"]} · {(x["environmentIsWorktree"]?.Value<bool>() == true ? "⎇ " + x["environmentBranchName"] : "checkout")} · {Ago(x["updatedAt"]?.Value<long>() ?? 0)}",
                        Badge = pending ? "NEEDS YOU" : working ? "RUNNING" : held ? "WAITING" : st == "error" ? "ERROR" : "",
                        BadgeColor = pending || held ? t.Warn : st == "error" ? t.Danger : t.Ok,
                        Tag = "t:" + id,
                    });
                }
                if (!showSpaces) foreach (var x in list) ThreadRow(x, 1);
                else
                    foreach (var g in groups)
                    {
                        items.Add(new UiListItem
                        {
                            Indent = 1,
                            Icon = g.Starting ? "◌" : g.IsWorktree ? "⎇" : "▣",
                            IconColor = g.Clash ? t.Danger : t.TextDim,
                            Title = g.Starting ? "setting up" : g.IsWorktree ? (g.Branch.Length > 0 ? g.Branch : "worktree") : "checkout" + (g.Branch.Length > 0 ? " · " + g.Branch : ""),
                            TitleColor = t.TextDim,
                            Sub = g.IsWorktree ? (g.Base.Length > 0 ? "from " + g.Base : "worktree") : g.Threads.Count > 1 ? "shared folder" : "",
                            Badge = g.Clash ? g.Working + " WORKING ⚠" : g.Waiting > 0 ? g.Waiting + " WAITING" : "",
                            BadgeColor = g.Clash ? t.Danger : t.Warn,
                            Tag = "w:" + pid + "|" + g.Key,
                        });
                        foreach (var x in g.Threads) ThreadRow(x, 2);
                    }
                if (list.Count == 0) items.Add(new UiListItem { Indent = 1, Icon = "·", IconColor = t.TextDim, Title = "no threads yet: + NEW THREAD…", TitleColor = t.TextDim, Tag = "p:" + pid });
            }
            int total = _threads.Count, run = _threads.Count(x => ThreadTimeline.IsWorking(ThreadStatus(x))), need = _threads.Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
            _threadsStatus.Set($"{_projects.Count} projects · {total} threads" + (run > 0 ? $" · <color=#{UiViz.Hex(t.Ok)}>{run} running</color>" : "") + (need > 0 ? $" · <color=#{UiViz.Hex(t.Warn)}>{need} need you</color>" : "") + " · tap a project to fold it", t.TextDim);
            _threadList.SetItems(items, keepScroll: true);
            int sel = items.FindIndex(i => i.Tag as string == "t:" + _threadOpen);
            if (sel >= 0) _threadList.SelectIndex(sel, notify: false);
        }

        private static string Ago(long ms)
        {
            if (ms <= 0) return "";
            var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return d.TotalMinutes < 1 ? "now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalDays} d";
        }

        // a project's type (mcx-project): .metacodex/project.json, else an app folder (AndroidManifest.xml + src/) is native
        private readonly Dictionary<string, (bool native, float at)> _projectTypes = new Dictionary<string, (bool, float)>();

        private bool IsNativeProject(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath)) return false;
            if (_projectTypes.TryGetValue(linuxPath, out var c) && Time.unscaledTime - c.at < 30f) return c.native;
            bool native = false;
            try
            {
                string host = AndroidPathOf(linuxPath);
                if (host != null)
                {
                    string j = Path.Combine(host, ".metacodex", "project.json");
                    native = File.Exists(j) ? File.ReadAllText(j).Contains("\"native\"")
                        : File.Exists(Path.Combine(host, "AndroidManifest.xml")) && Directory.Exists(Path.Combine(host, "src"));
                }
            }
            catch (Exception) { }
            _projectTypes[linuxPath] = (native, Time.unscaledTime);
            return native;
        }

        private void NewProjectPrompt()
        {
            string start = !string.IsNullOrEmpty(_filesPath) && _filesPath != "/" ? _filesPath : "/root/";
            UiKeyboard.Edit(start, false, null, path =>
            {
                path = (path ?? "").Trim().TrimEnd('/');
                if (path.Length < 2) return;
                // the type decides what the project's agents get: a Native app project carries the quest-native skills and
                // rules (mcx-project), a General one none of them (a website must not get native app rules)
                Modal("PROJECT TYPE", path + "\nWhat will this project be?", new[] { "NATIVE APP (QUEST)", "GENERAL CODING" }, choice =>
                {
                    string type = choice == 0 ? "native" : "general";
                    void Create() => EnsureHost(() => Mqh.CreateProject(path.Split('/').Last(), _hostId, path, r =>
                    {
                        if (r == null) { Toast("Project not added: " + Mqh.LastError, "error"); return; }
                        _projectSel = (string)r["id"];
                        Settings.lastProject = _projectSel;
                        _collapsedProjects.Remove(_projectSel);
                        _projectTypes.Remove(path);
                        Toast("Project " + r["name"] + " added (" + (type == "native" ? "Native app" : "General coding") + ")", "ok");
                        RefreshThreads();
                    }));
                    if (Git == null || !Git.InUserland) { Create(); return; }
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var res = await Git.Shell("mkdir -p " + GitService.Quote(path) + " && mcx-project set " + GitService.Quote(path) + " " + type, 20000);
                        _main.Enqueue(() => { if (res.code != 0) Toast("Project type not set: " + Cut(res.output ?? "", 80), "warn"); Create(); });
                    });
                });
            });
        }

        // ------------------------------------------------------------------ THREAD

        private UiButton _thModelChip, _thPermChip, _thWsChip;
        /// THREAD's width: the conversation plus the strip on its right edge for the sidebar's expand handle.
        public const float ThreadW = 0.73f, ThreadHandleW = 0.03f;
        private UiRing _thContext;
        private UiButton _tlUp, _tlDown;
        private Rect _tlArea;
        private JObject _thExec;
        private JToken _thUsage;
        private JArray _tlRaw = new JArray();
        private long _tlOpenSeq = -1;
        private readonly Dictionary<string, (string model, string effort, string perm)> _thOverrides = new Dictionary<string, (string, string, string)>();
        private readonly Dictionary<string, JArray> _modelsByProvider = new Dictionary<string, JArray>();

        private void BuildThread()
        {
            _thAskButtons.Clear(); // RebuildPanels builds the panel again
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("thread", "Thread", ThreadW, 0.66f);
            var c0 = p.Content;
            var c = new Rect(c0.x, c0.y, c0.width - ThreadHandleW, c0.height); // the conversation; its right edge holds the handle
            // the expand handle on the right edge: the sidebar (changes, files, git, status, views) opens beside it
            _thHandle = p.Add(new UiEdgeHandle { Rect = new Rect(c.xMax + t.Gap * 0.6f, c.y + 0.06f, ThreadW - c.xMax - t.Gap * 0.6f - 0.004f, c.height - 0.06f - 0.12f), OnClickAction = ToggleSidebar });
            var st = new UiStack(c, t.Gap);
            var top = st.Next(0.03f);
            float bw = 0.082f;
            _thTitle = p.Add(new UiLabel("", t.BodySize * 1.1f) { Rect = new Rect(top.x, top.y, top.width - bw * 2 - t.Gap * 2, top.height) });
            p.Add(new UiButton("⑂ FORK…", ForkPrompt) { Rect = new Rect(top.xMax - bw * 2 - t.Gap, top.y, bw, top.height), TextSize = t.SmallSize * 1.05f });
            p.Add(new UiButton("⟳", () => { _tlMaxSeq = -1; _tlTag = null; _tlInFlight = false; _nextThreadMeta = 0f; PollThread(); }) { Rect = new Rect(top.xMax - bw, top.y, bw, top.height), TextSize = t.SmallSize * 1.05f });
            // the meta row: where it works (left), the provider's plan and usage limits (right)
            var metaRow = st.Next(0.02f);
            _thMeta = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(metaRow.x, metaRow.y, metaRow.width * 0.56f, metaRow.height), Color = t.TextDim, UseMono = true });
            _thLimits = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(metaRow.x + metaRow.width * 0.56f, metaRow.y, metaRow.width * 0.44f, metaRow.height), Color = t.TextDim, Align = TextAlignmentOptions.MidlineRight });
            float chipH = 0.032f, statusH = 0.022f;
            float bottom = chipH + t.ButtonHeight + t.Gap * 2 + chipH + t.Gap; // + the attachments row
            _tlArea = new Rect(c.x, st.Y, c.width, c.yMax - st.Y - bottom - t.Gap - statusH - t.Gap);
            // the chat (UiChatView), and under it the status line: what the agent does now, timers, quiet / stuck
            BuildThreadChat(p, new Rect(c.x + 0.004f, _tlArea.yMax + t.Gap * 0.5f, c.width - 0.008f, statusH));
            LayoutTimelineButtons(false);
            // approvals: at the bottom of the timeline area, only while one is pending
            var askRow = new Rect(c.x, _tlArea.yMax - t.ButtonHeight, c.width, t.ButtonHeight);
            _thAsk = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(c.x, askRow.y - t.Gap - 0.046f, c.width, 0.046f), Wrap = true, Color = t.Warn, Align = TextAlignmentOptions.TopLeft });
            var asks = new (string label, string decision)[] { ("ALLOW ONCE", "allow_once"), ("ALLOW SESSION", "allow_for_session"), ("DENY", "deny") };
            float aw = (askRow.width - t.Gap * 2) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var d = asks[i].decision;
                _thAskButtons.Add(p.Add(new UiButton(asks[i].label, () => Resolve(d), i == 0 ? UiButtonStyle.Primary : i == 2 ? UiButtonStyle.Danger : UiButtonStyle.Normal) { Rect = new Rect(askRow.x + i * (aw + t.Gap), askRow.y, aw, askRow.height) }));
            }
            // attachments (captures, Linux files) for the next message, then the composer with its + (owner 2026-11-09),
            // then the bb-like chips: model, access, workspace, context, stop
            float cy = c.yMax - chipH - t.Gap - t.ButtonHeight;
            float sendW = 0.11f, plusW = t.ButtonHeight * 1.2f, micW = t.ButtonHeight * 1.25f;
            BuildAttachRow(p, new Rect(c.x, cy - t.Gap - chipH, c.width, chipH));
            _thPlus = p.Add(new UiButton("+", ToggleAttachMenu, UiButtonStyle.Primary) { Rect = new Rect(c.x, cy, plusW, t.ButtonHeight), TextSize = t.BodySize * 1.3f });
            _thComposer = p.Add(new UiTextField("", "", s => SendToThread(s), "message the agent… (or ◉ to say it)") { Rect = new Rect(c.x + plusW + t.Gap, cy, c.width - sendW - plusW - micW - t.Gap * 3, t.ButtonHeight), CaptionWidth = 0f });
            // ◉: say the message (speech to text into the composer; SEND stays yours)
            _thMic = p.Add(new UiButton("◉", () => DictateInto(_thComposer)) { Rect = new Rect(c.xMax - sendW - t.Gap - micW, cy, micW, t.ButtonHeight), TextSize = t.BodySize * 1.15f });
            p.Add(new UiButton("SEND ▶", () => SendToThread(_thComposer.Text), UiButtonStyle.Primary) { Rect = new Rect(c.xMax - sendW, cy, sendW, t.ButtonHeight) });
            float y = c.yMax - chipH;
            float[] w = { 0.17f, 0.13f, 0.165f, 0.135f };
            float x = c.x;
            float ts = t.SmallSize * 1.02f;
            _thModelChip = p.Add(new UiButton("◇ model ▾", ModelPrompt) { Rect = new Rect(x, y, w[0], chipH), TextSize = ts }); x += w[0] + t.Gap;
            _thPermChip = p.Add(new UiButton("✎ Edits ▾", PermissionPrompt) { Rect = new Rect(x, y, w[1], chipH), TextSize = ts }); x += w[1] + t.Gap;
            _thWsChip = p.Add(new UiButton("▣ checkout", () => SetSidebar(true, TabFiles)) { Rect = new Rect(x, y, w[2], chipH), TextSize = ts }); x += w[2] + t.Gap;
            _thContext = p.Add(new UiRing { Rect = new Rect(x, y, w[3], chipH), OnClickAction = ContextPrompt }); x += w[3] + t.Gap;
            p.Add(new UiButton("■ STOP", () => { if (_threadOpen != null) Mqh.StopThread(_threadOpen, r => { Toast(r != null ? "Stopping the turn" : "MQH: " + Mqh.LastError); if (r != null) ResyncThread(1.5f); }); }, UiButtonStyle.Danger) { Rect = new Rect(x, y, c.xMax - x, chipH), TextSize = ts });
            // the attach modal: drawn over the timeline area, inside this panel (not a panel of its own)
            BuildAttachModal(p, _tlArea);
            p.Tick += _ =>
            {
                if (_resyncAt > 0f && Time.unscaledTime >= _resyncAt) { _resyncAt = -1f; ResyncThread(); return; }
                if (_threadOpen != null && Time.unscaledTime >= _nextThreadPoll) PollThread();
            };
            p.Closed += _ => { if (_sb != null && _sb.IsOpen) _sb.Close(); CloseAttachModal(); SetAttachMenu(false); UpdateSidebarVoice(); };
            p.Opened += _ => UpdateSidebarVoice();
            SetAskVisible(false);
            RefreshAttachRow();
        }

        private Rect TimelineRect(bool ask)
        {
            var t = UiTheme.Current;
            float cut = ask ? t.ButtonHeight + 0.046f + t.Gap * 2 : 0f;
            return new Rect(_tlArea.x, _tlArea.y, _tlArea.width - 0.05f, _tlArea.height - cut);
        }

        private void LayoutTimelineButtons(bool ask)
        {
            var r = TimelineRect(ask);
            var t = UiTheme.Current;
            _tlUp?.SetRect(new Rect(_tlArea.xMax - 0.042f, r.y, 0.042f, r.height * 0.5f - t.Gap));
            _tlDown?.SetRect(new Rect(_tlArea.xMax - 0.042f, r.y + r.height * 0.5f, 0.042f, r.height * 0.5f));
            LayoutChatExtras(r);
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
            NoteThreadUsed(id); // first in the orb's quick list (ShellOrbThreads)
            if (id != _threadOpen) { _thExec = null; _thUsage = null; _tlRaw = new JArray(); CloseAttachModal(); SetAttachMenu(false); _threadOpen = id; ResetThreadChat(); }
            _threadOpen = id;
            RefreshAttachRow(); // each thread keeps its own attachments for its next message
            var row = _threads.FirstOrDefault(x => (string)x["id"] == id) as JObject;
            if (row != null) { _threadInfo = row; _threadOpenProject = (string)row["projectId"]; }
            else _threadOpenProject = null;
            _tlMaxSeq = -1;
            _tlOpenSeq = -1;
            _tlTag = null; // a full timeline first
            _thTitle?.Set("loading …");
            _thContext?.Set(-1f, "context —");
            OpenPanel("thread");
            SbSetThread(id, (string)row?["environmentPath"]);
            SetSidebar(SidebarMemory(id).open, SidebarMemory(id).tab, remember: false); // as this thread had it
            UpdateChips();
            Mqh.ExecOptions(id, r => { if (id != _threadOpen || !(r is JObject jo)) return; _thExec = jo; UpdateChips(); });
            _nextThreadMeta = 0f;
            PollThread();
            RefreshUsageLimits(true);
            RenderThreadGroups();
        }

        /// Refreshes the open thread: the timeline (a delta; every 0.8 s while the agent works, so text streams in),
        /// and every 2-3 s its row and pending approvals.
        /// Reads the open thread's state and whole timeline from MQH again now (after a resume, a Stop, a reconnect), and
        /// once more after `again` seconds: the panel never keeps a state the server no longer has.
        public void ResyncThread(float again = 0f)
        {
            _tlTag = null;
            _tlInFlight = false;
            _nextThreadMeta = 0f;
            _nextThreadPoll = 0f;
            if (_threadOpen != null && _thTitle != null) PollThread();
            if (again > 0f) _resyncAt = Time.unscaledTime + again;
            RefreshThreads();
        }

        private float _resyncAt = -1f;

        public void PollThread()
        {
            if (_threadOpen == null || _thTitle == null) return;
            string id = _threadOpen;
            bool live = _thPulse != null && _thPulse.Working || ThreadTimeline.IsWorking(StatusOfRow(_threadInfo));
            _nextThreadPoll = Time.unscaledTime + (live ? 0.8f : 2.5f);
            if (Time.unscaledTime >= _nextThreadMeta)
            {
                _nextThreadMeta = Time.unscaledTime + (live ? 2f : 3f);
                Mqh.Threads(_threadOpenProject, th =>
                {
                    var row = (th as JArray)?.FirstOrDefault(x => (string)x["id"] == id) as JObject;
                    if (row == null || id != _threadOpen) return;
                    bool statusChanged = StatusOfRow(row) != StatusOfRow(_threadInfo);
                    _threadInfo = row;
                    _threadOpenProject = (string)row["projectId"];
                    // the project's rows update the workspace map (who else works in this folder)
                    foreach (var pr in (th as JArray).OfType<JObject>())
                    {
                        int at = -1;
                        for (int k = 0; k < _threads.Count; k++) if ((string)_threads[k]["id"] == (string)pr["id"]) { at = k; break; }
                        if (at >= 0) _threads[at] = pr.DeepClone(); else _threads.Add(pr.DeepClone());
                    }
                    _spaces = WorkspaceMap.Build(_threads, _environments);
                    ShowThreadHeader();
                    PollThreadQueue(id); // a turn MQH holds (the workspace guard): its reason, SEND NOW
                    // working <-> idle: parse the whole timeline again with the new status (a delta may say "no change")
                    if (statusChanged) { _tlTag = null; _tlInFlight = false; RebuildChat(); RequestTimeline(id); }
                });
                Mqh.Interactions(id, ia =>
                {
                    if (id != _threadOpen) return;
                    _interactions = ia as JArray ?? new JArray();
                    ShowAsk();
                    CtxInteractionsUpdated(id);
                });
            }
            RequestTimeline(id);
        }

        /// Agent views in the thread's messages: registered; the ones that arrive while it is open also open.
        private void ScanViews(JArray rows, string threadId, bool first)
        {
            void Walk(JToken r)
            {
                if ((string)r["kind"] == "turn") { foreach (var ch in r["children"] as JArray ?? new JArray()) Walk(ch); return; }
                if ((string)r["kind"] != "conversation" || (string)r["role"] == "user") return;
                string text = (string)r["text"];
                if (string.IsNullOrEmpty(text) || text.IndexOf("```", StringComparison.Ordinal) < 0) return;
                long seq = r["sourceSeqEnd"]?.Type == JTokenType.Integer ? (long)r["sourceSeqEnd"] : 0;
                bool fresh = !first && seq > _tlOpenSeq;
                ViewsFromMessage(threadId, seq, text, fresh);
            }
            foreach (var r in rows) Walk(r);
            if (first) _tlOpenSeq = _tlMaxSeq;
        }

        private void ShowThreadHeader()
        {
            var x = _threadInfo;
            var t = UiTheme.Current;
            if (x == null) { _thTitle.Set(_threadOpen ?? ""); return; }
            string st = (string)x["runtime"]?["displayStatus"] ?? (string)x["status"] ?? "";
            bool working = ThreadTimeline.IsWorking(st);
            string word = working && st != "waiting-for-host" ? "WORKING" : st == "waiting-for-host" ? "OFFLINE" : st.ToUpperInvariant();
            _thTitle.Set($"<b>{Esc((string)x["title"] ?? (string)x["titleFallback"] ?? "thread")}</b>  <color=#{ColorUtility.ToHtmlStringRGB(st == "error" ? t.Danger : working ? t.Ok : t.TextDim)}>{word}</color>");
            string proj = (string)_projects.FirstOrDefault(p => (string)p["id"] == (string)x["projectId"])?["name"];
            _thMeta.Set($"{x["providerId"]} · {(proj != null ? proj + " · " : "")}{x["environmentPath"] ?? "no workspace yet"} · ⎇ {x["environmentBranchName"] ?? "-"}");
            bool wt = x["environmentIsWorktree"]?.Value<bool>() == true;
            _thWsChip?.SetText("▣ " + Cut(proj ?? ((string)x["environmentPath"] ?? "workspace").Split('/').Last(), 12) + " · " + (wt ? "worktree" : "checkout"));
            SbSetThread(_threadOpen, (string)x["environmentPath"]);
            UpdateSidebarHandle();
            UpdateChips();
            ShowLimits();
            UpdateWorkspaceChip();
        }

        private void ShowContextUsage()
        {
            if (_thContext == null) return;
            var u = _thUsage;
            long used = u?["usedTokens"]?.Type == JTokenType.Integer ? (long)u["usedTokens"] : -1;
            long win = u?["modelContextWindow"]?.Type == JTokenType.Integer ? (long)u["modelContextWindow"] : -1;
            if (used < 0 || win <= 0) { _thContext.Set(-1f, "context —"); return; }
            bool est = u["estimated"]?.Type == JTokenType.Boolean && (bool)u["estimated"];
            float frac = Mathf.Clamp01(used / (float)win);
            _thContext.Set(frac, $"{(est ? "~" : "")}{frac * 100f:0}% · {K(used)}/{K(win)}");
        }

        private static string K(long n) => n >= 1000000 ? (n / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M" : n >= 1000 ? (n / 1000) + "k" : n.ToString();

        // ---- chips: model, access, context

        private (string model, string effort, string perm) Override(string id) => id != null && _thOverrides.TryGetValue(id, out var o) ? o : (null, null, null);

        private string CurrentModel() => Override(_threadOpen).model ?? (string)_thExec?["model"];
        private string CurrentEffort() => Override(_threadOpen).effort ?? (string)_thExec?["reasoningLevel"];
        private string CurrentPerm() => Override(_threadOpen).perm ?? (string)_thExec?["permissionMode"] ?? Settings.permissionMode;

        private string ModelName(string provider, string modelId)
        {
            if (modelId == null) return null;
            if (provider != null && _modelsByProvider.TryGetValue(provider, out var models))
            {
                var m = models.FirstOrDefault(x => (string)x["id"] == modelId || (string)x["model"] == modelId);
                if (m != null) return (string)m["displayName"] ?? modelId;
            }
            return modelId;
        }

        private static string PermLabel(string perm) => perm == "full" ? "⚠ Full access" : perm == "auto" ? "◐ Auto" : "✎ Edits";

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void UpdateChips()
        {
            if (_thModelChip == null) return;
            string provider = (string)_threadInfo?["providerId"];
            string model = CurrentModel(), effort = CurrentEffort();
            string name = ModelName(provider, model) ?? "default model";
            bool showEffort = !string.IsNullOrEmpty(effort) && effort != "none" && effort != "medium";
            _thModelChip.SetText("◇ " + Cut(name, showEffort ? 11 : 16) + (showEffort ? " " + Cap(effort) : "") + " ▾");
            string perm = CurrentPerm();
            _thPermChip.SetText(PermLabel(perm) + " ▾");
            _thPermChip.SetSelected(perm == "full");
            if (provider != null && !_modelsByProvider.ContainsKey(provider))
            {
                _modelsByProvider[provider] = new JArray(); // asked once
                EnsureHost(() => Mqh.Models(provider, _hostId, m => { _modelsByProvider[provider] = m?["models"] as JArray ?? new JArray(); UpdateChips(); }));
            }
        }

        private void ModelPrompt()
        {
            if (_threadOpen == null) return;
            string provider = (string)_threadInfo?["providerId"];
            if (provider == null || !_modelsByProvider.TryGetValue(provider, out var models) || models.Count == 0) { Toast("Models are still loading (or the provider lists none)", "warn"); UpdateChips(); return; }
            string cur = CurrentModel();
            var t = UiTheme.Current;
            ShowPicker("Model", $"For the next messages of this thread ({provider}).", models.Select(m => new UiListItem
            {
                Icon = (string)m["id"] == cur || (string)m["model"] == cur ? "◆" : "◇",
                Title = (string)m["displayName"] ?? (string)m["id"],
                Sub = Cut((string)m["description"], 40),
                Badge = m["isDefault"]?.Value<bool>() == true ? "DEFAULT" : "",
                BadgeColor = t.TextDim,
                Tag = m,
            }), item =>
            {
                var m = item.Tag as JToken;
                string id = (string)m?["id"];
                var efforts = (m?["supportedReasoningEfforts"] as JArray ?? new JArray()).Select(e => e.Type == JTokenType.Object ? (string)e["reasoningEffort"] : (string)e).Where(e => !string.IsNullOrEmpty(e)).ToList();
                var o = Override(_threadOpen);
                _thOverrides[_threadOpen] = (id, efforts.Contains(o.effort ?? "") ? o.effort : null, o.perm);
                UpdateChips();
                if (efforts.Count > 1)
                {
                    string curE = CurrentEffort();
                    ShowPicker("Effort", (string)m["displayName"] + ": how hard it thinks.", efforts.Select(e => new UiListItem { Icon = e == curE ? "◆" : "◇", Title = Cap(e), Tag = e }), it2 =>
                    {
                        var o2 = Override(_threadOpen);
                        _thOverrides[_threadOpen] = (o2.model, it2.Tag as string, o2.perm);
                        UpdateChips();
                    }, PanelsById["thread"]);
                }
            }, PanelsById["thread"]);
        }

        private void PermissionPrompt()
        {
            if (_threadOpen == null) return;
            var modes = (AgentCatalog((string)_threadInfo?["providerId"])?["capabilities"]?["permissionModes"] as JArray)?.Select(m => (string)m).ToList()
                        ?? new List<string> { "accept-edits", "auto", "full" };
            string cur = CurrentPerm();
            var help = new Dictionary<string, string> { { "accept-edits", "edits files; asks before commands" }, { "auto", "asks only for risky actions" }, { "full", "never asks: runs anything" } };
            ShowPicker("Access", "What the agent may do without asking, from the next message on.", modes.Select(m => new UiListItem
            {
                Icon = m == cur ? "◆" : "◇",
                Title = PermLabel(m),
                Sub = help.TryGetValue(m, out var h) ? h : m,
                TitleColor = m == "full" ? UiTheme.Current.Warn : (Color?)null,
                Tag = m,
            }), item =>
            {
                var o = Override(_threadOpen);
                _thOverrides[_threadOpen] = (o.model, o.effort, item.Tag as string);
                Settings.permissionMode = item.Tag as string;
                UpdateChips();
            }, PanelsById["thread"]);
        }

        private void ContextPrompt()
        {
            if (_threadOpen == null) return;
            var u = _thUsage;
            string what = u == null ? "MQH has not reported the context use of this thread yet." :
                $"{u["usedTokens"]} of {u["modelContextWindow"]} tokens{(u["estimated"]?.Type == JTokenType.Boolean && (bool)u["estimated"] ? " (estimated)" : "")}.";
            string provider = (string)_threadInfo?["providerId"];
            if (provider != null && _usage.TryGetValue(provider, out var lim)) { string l = LimitsText(lim, true); if (l.Length > 0) what += "\nUsage: " + l.Replace("\n", " · "); }
            ShowPicker("Context", what, new[]
            {
                new UiListItem { Icon = "⇣", Title = "Compact now", Sub = "summarise older turns to free context", Tag = "compact" },
                new UiListItem { Icon = "⑂", Title = "Fork instead…", Sub = "continue in a new thread", Tag = "fork" },
            }, item =>
            {
                if ((string)item.Tag == "fork") { ForkPrompt(); return; }
                Mqh.CompactThread(_threadOpen, r => { Toast(r != null ? "Compacting the context" : "Not compacted: " + Mqh.LastError, r != null ? "ok" : "error"); PollThread(); });
            }, PanelsById["thread"]);
        }

        // ---- fork

        private void ForkPrompt()
        {
            if (_threadOpen == null || _threadInfo == null) { Toast("Open a thread first", "warn"); return; }
            string provider = (string)_threadInfo["providerId"];
            string cur = CurrentModel();
            var items = new List<UiListItem>
            {
                new UiListItem { Icon = "⑂", Title = $"Same agent · {ModelName(provider, cur) ?? "same model"}", Sub = "the conversation continues in a copy", Tag = new[] { "same", provider, null } },
            };
            if (_modelsByProvider.TryGetValue(provider ?? "", out var models))
                foreach (var m in models.Where(m => (string)m["id"] != cur).Take(6))
                    items.Add(new UiListItem { Icon = "◇", Title = $"{provider} · {(string)m["displayName"] ?? (string)m["id"]}", Sub = "same conversation, other model", Tag = new[] { "same", provider, (string)m["id"] } });
            void Done(JToken st)
            {
                var t = UiTheme.Current;
                foreach (var x in (st?["providers"] as JArray ?? _providerStates).Where(x => (string)x["providerId"] != provider && ((string)x["status"] == "ready" || (string)x["status"] == "authenticated")))
                    items.Add(new UiListItem { Icon = "◆", IconColor = t.Violet, Title = $"{(string)x["displayName"] ?? (string)x["providerId"]} · default model", Sub = "new thread, same workspace, conversation handed over", Tag = new[] { "other", (string)x["providerId"], null } });
                string text = _thComposer?.Text?.Trim();
                ShowPicker("Fork thread", string.IsNullOrEmpty(text) ? "Branch this thread: same or another agent. Type a first message in the composer to send it with the fork." : "The fork starts with your message: “" + Cut(text, 60) + "”", items, item =>
                {
                    var tag = item.Tag as string[];
                    if (tag != null) ForkTo(tag[0], tag[1], tag[2]);
                }, PanelsById["thread"]);
            }
            if (_providerStates.Count > 0) Done(null);
            else Mqh.ProvidersState(st => { if (st != null) _providerStates = st["providers"] as JArray ?? new JArray(); Done(st); });
        }

        /// Fork the open thread: "same" = MQH's fork (same provider, the conversation copied; another model is set
        /// after), "other" = a new thread of another provider in the same workspace, the conversation handed over
        /// as hidden context.
        public void ForkTo(string kind, string provider, string model)
        {
            if (_threadOpen == null || _threadInfo == null) return;
            string src = _threadOpen;
            string title = Cut((string)_threadInfo["title"] ?? (string)_threadInfo["titleFallback"] ?? "thread", 50);
            string text = _thComposer?.Text?.Trim();
            string perm = CurrentPerm();
            if (kind == "same")
            {
                var body = new JObject { ["sourceThreadId"] = src, ["origin"] = "app", ["title"] = title + " · fork", ["permissionMode"] = perm };
                if (!string.IsNullOrEmpty(text)) body["input"] = new JArray { MqhClient.TextInput(text) };
                Toast("Forking …", "info");
                Mqh.ForkThread(body, r =>
                {
                    if (r == null)
                    {
                        string err = Mqh.LastError ?? "";
                        if (err.Contains("does not support thread forks")) { Toast(provider + " cannot fork: handing the conversation over instead", "info"); ForkTo("other", provider, model); return; }
                        Toast("Not forked: " + err, "error");
                        return;
                    }
                    string nid = (string)r["id"];
                    if (!string.IsNullOrEmpty(model)) Mqh.UpdateThread(nid, new JObject { ["model"] = model }, _ => { });
                    _thComposer?.Set("");
                    Toast("Forked: " + title, "ok");
                    RefreshThreadList();
                    OpenThreadAfterRefresh(nid);
                });
                return;
            }
            var envId = (string)_threadInfo["environmentId"];
            var input = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = HandoverContext(), ["mentions"] = new JArray(), ["visibility"] = "agent-only" },
                MqhClient.TextInput(string.IsNullOrEmpty(text) ? "Continue this work where the previous agent stopped. First say in 2-3 lines where things stand, then wait for my next message." : text),
            };
            var create = new JObject
            {
                ["projectId"] = (string)_threadInfo["projectId"],
                ["providerId"] = provider,
                ["origin"] = "app",
                ["title"] = title + " · " + provider,
                ["input"] = input,
                ["permissionMode"] = perm,
                ["sourceThreadId"] = src,
                ["environment"] = envId != null ? new JObject { ["type"] = "reuse", ["environmentId"] = envId } : NewThreadBody((string)_threadInfo["projectId"], provider, model, "x")["environment"],
            };
            if (!string.IsNullOrEmpty(model)) create["model"] = model;
            Toast("Handing over to " + provider + " …", "info");
            EnsureHost(() => Mqh.CreateThread(create, r =>
            {
                if (r == null) { Toast("Not forked: " + Mqh.LastError, "error"); return; }
                _thComposer?.Set("");
                Toast("Forked to " + provider, "ok");
                RefreshThreadList();
                OpenThreadAfterRefresh((string)r["id"]);
            }));
        }

        private void OpenThreadAfterRefresh(string id)
        {
            Mqh.Threads(null, th => { if (th is JArray a) _threads = a; RenderThreadGroups(); OpenThread(id); });
        }

        /// The conversation so far as hidden context for another provider (the newest ~8000 characters).
        public string HandoverContext()
        {
            var x = _threadInfo;
            var sb = new StringBuilder();
            sb.Append($"[Context handed over from the thread \"{(string)x?["title"] ?? (string)x?["titleFallback"]}\" of another agent ({x?["providerId"]}). ");
            sb.Append($"You continue the same work in the same workspace ({x?["environmentPath"] ?? "?"}, branch {x?["environmentBranchName"] ?? "?"}). The conversation so far, oldest first:]\n");
            var lines = new List<string>();
            void Walk(JToken r)
            {
                string kind = (string)r["kind"];
                if (kind == "turn") { foreach (var ch in r["children"] as JArray ?? new JArray()) Walk(ch); return; }
                if (kind == "conversation") lines.Add(((string)r["role"] == "user" ? "USER: " : "AGENT: ") + Cut(StripViewBlocks((string)r["text"]), 700));
                else if (kind == "work" && (string)r["workKind"] == "file-change") lines.Add("(edited " + (string)r["change"]?["path"] + ")");
                else if (kind == "work" && (string)r["workKind"] == "command") lines.Add("(ran: " + Cut((string)r["command"], 120) + ")");
            }
            foreach (var r in _tlRaw) Walk(r);
            int budget = 8000, start = lines.Count;
            int used = 0;
            while (start > 0 && used + lines[start - 1].Length < budget) { used += lines[start - 1].Length + 1; start--; }
            if (start > 0) sb.Append($"(… {start} earlier entries left out)\n");
            for (int i = start; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
            var files = _touched.Values.Where(f => f.ByThread).Take(30).ToList();
            if (files.Count > 0) sb.Append("Files touched: " + string.Join(", ", files.Select(f => $"{f.Path} (+{f.Added} -{f.Removed})")) + "\n");
            return sb.ToString();
        }

        // ---- self-tests: a thread from canned MQH data (no daemon needed)

        public void LoadThreadForTest(JObject row, JArray rows, JToken usage, JObject exec, JArray projects, bool openViews)
        {
            if (projects != null) _projects = projects;
            _threads = new JArray(row);
            OpenThread((string)row["id"]);
            _threadInfo = row;
            _thExec = exec;
            ShowThreadHeader();
            _thUsage = usage;
            ShowContextUsage();
            _tlRaw = rows;
            _tlFields = new JObject();
            RebuildChat();
            _tlMaxSeq = rows.Max(r => r["sourceSeqEnd"]?.Value<long>() ?? 0);
            _tlOpenSeq = openViews ? 0 : _tlMaxSeq;
            WsTrackRows(rows);
            ScanViews(rows, (string)row["id"], false);
        }

        /// Self-tests: more rows arrive in the open thread (as the next poll would bring them).
        public void AppendTimelineForTest(JArray more)
        {
            foreach (var r in more) _tlRaw.Add(r);
            RebuildChat();
            _tlMaxSeq = _tlRaw.Max(r => r["sourceSeqEnd"]?.Value<long>() ?? 0);
            WsTrackRows(_tlRaw);
            ScanViews(_tlRaw, _threadOpen, false);
        }

        public void SetThreadsForTest(JArray projects, JArray threads) { _projects = projects; _threads = threads; RenderThreadGroups(); }
        public UiList ThreadListForTest => _threadList;
        public void PickThreadRowForTest(int index) => ThreadRowPicked(_threadList.Items[index]);
        public string TimelineTextForTest => _thTimeline?.PlainText ?? "";
        public string TimelineTagForTest { get => _tlTag; set => _tlTag = value; }
        public string ChipsForTest => $"{_thModelChip?.Text} | {_thPermChip?.Text} | {_thWsChip?.Text} | {_thContext?.Caption}";
        public void PickChangedFileForTest(string path) { var f = _sbFiles.FirstOrDefault(x => x.Path == path); if (f != null) SbOpenDiff(f); }
        public string DiffHeadForTest => _sbDiffLabel?.Text ?? "";
        public UiSourceView DiffViewForTest => _sbDiff;
        public void ShowPermissionPickerForTest() => PermissionPrompt();

        private static string Esc(string s) => s == null ? "" : "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";
        private static string Cut(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        /// Redraws the chat (the attach modal closed over it, the ask row came or went).
        private void RenderTimeline() { _thTimeline?.Redraw(); RefreshNewButton(); }

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
                SetAskVisible(true);
                foreach (var b in _thAskButtons) b.SetVisible(false);
            }
        }

        private bool _askShown;

        private void SetAskVisible(bool v)
        {
            _askShown = v;
            if (_amOpen) return; // the attach modal covers the timeline: the row comes back when it closes
            _thAsk?.SetVisible(v);
            foreach (var b in _thAskButtons) b.SetVisible(v);
            if (_thTimeline != null && _thTimeline.Rect != TimelineRect(v)) { _thTimeline.SetRect(TimelineRect(v)); LayoutTimelineButtons(v); RenderTimeline(); }
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
            var att = AttachList;
            bool hasAtt = att != null && att.Count > 0;
            if ((string.IsNullOrWhiteSpace(text) && !hasAtt) || _threadOpen == null) return;
            string msg = string.IsNullOrWhiteSpace(text) ? AttachmentsOnlyText(att) : text.Trim();
            var sent = hasAtt ? att.ToList() : null;
            NoteThreadUsed(_threadOpen);
            var o = Override(_threadOpen);
            var extra = new JObject();
            if (o.model != null) extra["model"] = o.model;
            if (o.effort != null) extra["reasoningLevel"] = o.effort;
            if (o.perm != null) extra["permissionMode"] = o.perm;
            Mqh.SendMessage(_threadOpen, msg, extra, hasAtt ? AttachmentInputs(sent) : null, r =>
            {
                if (r == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                _thComposer.Set("");
                if (sent != null) { foreach (var a in sent) att.Remove(a); RefreshAttachRow(); }
                Toast((string)r["delivery"] == "queued" ? "Queued (the agent is busy)" : "Sent", "ok");
                _thTimeline?.ScrollToBottom(); // your message: the view follows the answer
                _nextThreadMeta = 0f;
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
            _ntWork = p.Add(new UiSegmented("WORKSPACE", new[] { "CHECKOUT", "WORKTREE" }, Settings.workspaceMode == "worktree" ? 1 : 0, i => { Settings.workspaceMode = i == 1 ? "worktree" : "local"; _ntForcedWorktree = false; _ntReuseEnv = default; ShowNewThreadSummary(); }) { Rect = r.Next(0.032f), LabelWidth = 0.1f });
            p.Add(new UiLabel("FIRST MESSAGE", t.SmallSize) { Rect = r.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            var pr = r.Next(0.034f);
            _ntPrompt = p.Add(new UiTextField("", "", null, "what should the agent do? (◉ to say it)") { Rect = new Rect(pr.x, pr.y, pr.width - 0.05f, pr.height), CaptionWidth = 0f });
            _ntMic = p.Add(new UiButton("◉", () => DictateInto(_ntPrompt)) { Rect = new Rect(pr.xMax - 0.045f, pr.y, 0.045f, pr.height), TextSize = t.BodySize * 1.1f });
            p.Tick += _ => { if (_thTimeline == null || !IsPanelOpen("thread")) TickThreadView(); };
            p.Closed += _ => { _ntReuseEnv = default; _ntForcedWorktree = false; };
            p.Add(new UiLabel("CHECKOUT works in the project folder itself; WORKTREE gives the thread its own git worktree and branch. EDITS asks before commands; FULL never asks.", t.SmallSize) { Rect = r.Next(0.06f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            var go = r.Next(t.ButtonHeight);
            p.Add(new UiButton("START ▶", StartThread, UiButtonStyle.Primary) { Rect = go });
        }

        public void OpenNewThread(string providerId)
        {
            _ntProvider = providerId ?? (string.IsNullOrEmpty(Settings.defaultProvider) ? _ntProvider : Settings.defaultProvider);
            OpenPanel("thread-new");
            // a checkout another thread works in: WORKTREE for this one (said in the summary; CHECKOUT stays possible)
            _ntWork?.Set(Settings.workspaceMode == "worktree" ? 1 : 0);
            _spaces = WorkspaceMap.Build(_threads, _environments);
            ApplyNewThreadWorkspaceDefault();
            ShowNewThreadSummary();
            Mqh.ProvidersState(st =>
            {
                if (st == null) { _ntSummary?.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable"), UiTheme.Current.Danger); return; }
                var raw = st["providers"] as JArray ?? new JArray();
                _providerStates = raw;
                DisableUnsupportedProviders();
                void Render()
                {
                    _providerStates = SupportedOnly(raw);
                    // MQH can run Codex and OpenCode once they are installed (Claude Code: in a terminal, for now)
                    var list = _providerStates.Where(x => (string)x["status"] != "not_installed" && (string)x["status"] != "terminal").ToList();
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
                }
                Render();
                // opencode is installed-only in MQH: its execution options have it (then it is in the list as ready)
                EnsureHost(() => Mqh.Models(null, _hostId, eo =>
                {
                    _providerStates = raw;
                    if (MergeInstalledProviders(eo)) { raw = _providerStates; Render(); }
                    else _providerStates = SupportedOnly(raw);
                }));
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
            bool wt = Settings.workspaceMode == "worktree" || _ntForcedWorktree;
            string where = _ntReuseEnv.id != null ? "in " + _ntReuseEnv.label : wt ? "new worktree" : "project checkout";
            _ntSummary?.Set($"<color=#7FA9BF>PROJECT</color> <b>{(string)proj?["name"] ?? "— pick one in THREADS"}</b>\n<color=#7FA9BF>AGENT</color> {_ntProvider ?? "—"} · {_ntModel ?? "default model"}\n<color=#7FA9BF>MODE</color> {Settings.permissionMode} · {UiChatView.Esc(where)}" + NewThreadWorkspaceNote());
        }

        /// The create-thread request MQH expects (also used by tests).
        public JObject NewThreadBody(string projectId, string providerId, string model, string prompt)
        {
            var workspace = Settings.workspaceMode == "worktree" || _ntForcedWorktree
                ? new JObject { ["type"] = "managed-worktree", ["baseBranch"] = new JObject { ["kind"] = "default" } }
                : new JObject { ["type"] = "unmanaged", ["path"] = null };
            var body = new JObject
            {
                ["projectId"] = projectId,
                ["providerId"] = providerId,
                ["origin"] = "app",
                ["input"] = new JArray { MqhClient.TextInput(prompt) },
                ["permissionMode"] = Settings.permissionMode,
                // NEW THREAD HERE (WORKSPACES): that workspace again; else the checkout or a new worktree on this host
                ["environment"] = _ntReuseEnv.id != null ? new JObject { ["type"] = "reuse", ["environmentId"] = _ntReuseEnv.id }
                    : new JObject { ["type"] = "host", ["hostId"] = _hostId, ["workspace"] = workspace },
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
                _ntReuseEnv = default;
                _ntForcedWorktree = false;
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
