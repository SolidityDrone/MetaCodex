using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MetaCodex.AI;
using MetaCodex.App;
using MetaCodex.Scripting;
using MetaCodex.Terminal;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The harness side of the shell, all data from MQH's public API: AGENTS (providers, sign-in through the
    /// provider's own CLI in a terminal, install, enable), THREADS (project types, their projects and their threads),
    /// THREAD (the timeline, a composer, approvals, stop, and jumps to the workspace's files / git / terminal; a draft
    /// until its first message) and NEW THREAD (provider, model, permissions, workspace).
    public sealed partial class MetaCodexShell
    {
        private string _hostId;
        private JArray _providerStates = new JArray(), _providerCatalog = new JArray(), _projects = new JArray(), _threads = new JArray();
        private string _agentSel, _projectSel, _threadSel;
        private string _ntProvider, _ntModel;
        private JArray _ntModels = new JArray();

        private UiList _agentList, _projectList, _threadList, _ntProviders, _ntModelList;
        private UiLabel _agentDetail, _agentHint, _threadsStatus, _ntSummary;
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
            _agentHint = p.Add(new UiLabel("GET installs an agent here. API KEY: paste your provider's key once (sealed in the vault, given to the agents MQH starts). SIGN IN runs the provider's own login in a new terminal: ⇗ on that terminal opens its link in the system browser, codes go back with ⎘ PASTE; those logins stay in the CLI's own files, MetaCodex never reads them.", t.SmallSize) { Rect = rs.Rest(), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
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
        private readonly HashSet<string> _collapsedTypes = new HashSet<string>(); // NATIVE, DEFAULT, RESEARCH, OTHER headers folded

        /// Threads grouped like bb's sidebar, under the project types (owner 2026-11-13): a type band (NATIVE, DEFAULT,
        /// RESEARCH, PLUGINS, then OTHER when there are any; tap: fold / unfold), its projects (tap: fold / unfold, the name
        /// with its caption under it), and each project's threads under it, newest first (a status dot, the age); a tap on a
        /// thread opens it. The rows' levels are their indents (0.03 m a level); the list is a variable-height list (UiList).
        private void BuildThreads()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("threads", "Threads", 0.60f, 0.60f);
            var c = p.Content;
            var st = new UiStack(c, t.Gap);
            // the status line, and on its right the ARCHIVED toggle (owner 2026-11-14: archived threads, RESTORE / DELETE)
            var sr = st.Next(0.03f);
            float abw = 0.13f;
            _threadsStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(sr.x, sr.y, sr.width - abw - t.Gap, sr.height), Color = t.TextDim });
            _threadsArchBtn = p.Add(new UiButton(_threadsArchived ? "◂ ACTIVE" : "▤ ARCHIVED", ToggleArchivedThreads) { Rect = new Rect(sr.xMax - abw, sr.y, abw, sr.height), TextSize = t.SmallSize * 1.02f });
            _threadsArchBtn.SetSelected(_threadsArchived);
            _threadList = p.Add(new UiList(0.034f, (item, idx) => ThreadRowPicked(item)) { Rect = st.Next(c.height - 0.03f - t.ButtonHeight - t.Gap * 2), Variable = true, IndentStep = 0.03f });
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
            if (tag.StartsWith("g:"))
            {
                string type = tag.Substring(2);
                if (!_collapsedTypes.Remove(type)) _collapsedTypes.Add(type);
                RenderThreadGroups();
                return;
            }
            if (tag.StartsWith("n:")) { NewProjectNamed(tag.Substring(2)); return; } // an empty type's row: a project of that type
            if (tag.StartsWith("p:"))
            {
                string pid = tag.Substring(2);
                _projectSel = pid;
                Settings.lastProject = pid;
                if (!_collapsedProjects.Remove(pid)) _collapsedProjects.Add(pid);
                RenderThreadGroups();
                return;
            }
            if (tag.StartsWith("a:")) { ArchivedRowPicked(tag.Substring(2)); return; } // an archived thread: RESTORE or DELETE
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
                if (_threadsArchived) RefreshArchivedThreads();
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
            if (_threadsArchived) { RenderArchivedThreads(); return; } // ARCHIVED is on: the archived list instead
            var t = UiTheme.Current;
            var byProject = _threads.GroupBy(x => (string)x["projectId"] ?? "").ToDictionary(g => g.Key, g => g.OrderByDescending(x => x["updatedAt"]?.Value<long>() ?? 0).ToList());
            var spaces = WorkspaceMap.Build(_threads, _environments);
            long Latest(JToken pr) => byProject.TryGetValue((string)pr["id"], out var l) && l.Count > 0 ? l[0]["updatedAt"]?.Value<long>() ?? 0 : 0;
            List<JToken> ThreadsOf(JToken pr) => byProject.TryGetValue((string)pr["id"], out var l) ? l : new List<JToken>();
            var items = new List<UiListItem>();
            // the type of each project (its path under metacodex-projects/, else .metacodex/project.json, cached)
            var byType = _projects.GroupBy(pr => ProjectType((string)pr["sources"]?.FirstOrDefault()?["path"]) ?? "other").ToDictionary(g => g.Key, g => g.OrderByDescending(Latest).ToList());
            var order = ProjectKinds.Types.Concat(byType.ContainsKey("other") ? new[] { "other" } : new string[0]);
            foreach (var type in order)
            {
                byType.TryGetValue(type, out var projects);
                projects = projects ?? new List<JToken>();
                var all = projects.SelectMany(ThreadsOf).ToList();
                int tRun = all.Count(x => ThreadTimeline.IsWorking(ThreadStatus(x))), tAsk = all.Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
                bool typeFolded = _collapsedTypes.Contains(type);
                // the type band: its colour is the bar, the fold icon and the rule; one count (needs you, else running, else the projects)
                items.Add(new UiListItem
                {
                    Kind = UiListKind.Band,
                    BarColor = TypeColor(t, type),
                    Icon = typeFolded ? "▸" : "▾",
                    IconColor = TypeColor(t, type),
                    Title = ProjectKinds.Header(type),
                    TitleColor = t.Text,
                    Sub = type == "other" ? "outside metacodex-projects/" : "",
                    Badge = tAsk > 0 ? tAsk + " NEED YOU" : tRun > 0 ? tRun + " RUNNING" : projects.Count == 1 ? "1 project" : projects.Count + " projects",
                    BadgeColor = tAsk > 0 ? t.Warn : tRun > 0 ? t.Ok : t.TextDim,
                    Tag = "g:" + type,
                });
                if (typeFolded) continue;
                if (projects.Count == 0)
                    items.Add(new UiListItem { Kind = UiListKind.Compact, Indent = 1, Icon = "+", IconColor = t.TextDim, Title = "no " + ProjectKinds.Label(type).ToLowerInvariant() + " project yet: tap to make one", TitleColor = t.TextDim, Tag = "n:" + type });
                foreach (var pr in projects) ProjectRows(pr, ThreadsOf(pr));
            }
            void ProjectRows(JToken pr, List<JToken> list)
            {
                string pid = (string)pr["id"];
                int running = list.Count(x => ThreadTimeline.IsWorking(ThreadStatus(x)));
                int asks = list.Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
                bool folded = _collapsedProjects.Contains(pid);
                string path = (string)pr["sources"]?.FirstOrDefault()?["path"] ?? "";
                string plugin = PluginProject.KindFromLinuxPath(path); // a plugin project: its kind is the caption
                items.Add(new UiListItem
                {
                    Kind = UiListKind.Stacked,
                    Indent = 1,
                    Divider = true,
                    Icon = folded ? "▸" : "▾",
                    IconColor = t.TextDim,
                    Title = (string)pr["name"] ?? "project",
                    TitleColor = pid == _projectSel ? t.TextAccent : t.Text,
                    Sub = plugin != null ? PluginProject.Label(plugin).ToUpperInvariant() : Cut(ProjectKinds.ShortPath(path), 34),
                    Badge = list.Count.ToString(), // the threads; coloured by what they do (needs you, running)
                    BadgeColor = asks > 0 ? t.Warn : running > 0 ? t.Ok : t.TextDim,
                    Tag = "p:" + pid,
                });
                if (folded) return;
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
                        Kind = UiListKind.Compact,
                        Indent = indent,
                        // the status dot: running and needs you lit; waiting (held) and error and idle dim
                        Dot = pending || held ? t.Warn : working ? t.Ok : st == "error" ? t.Danger : t.TextDim,
                        DotLit = pending || working || st == "error",
                        Title = (string)x["title"] ?? (string)x["titleFallback"] ?? id,
                        TitleColor = id == _threadOpen || OpenWindowShowing(id) != null ? t.TextAccent : (Color?)null, // open in a window (any of them)
                        Sub = showSpaces ? $"{x["providerId"]}" : $"{x["providerId"]} · {(x["environmentIsWorktree"]?.Value<bool>() == true ? "⎇ " + x["environmentBranchName"] : "checkout")}",
                        Badge = Ago(x["updatedAt"]?.Value<long>() ?? 0), // the age, dim, on the right
                        BadgeColor = t.TextDim,
                        Tag = "t:" + id,
                    });
                }
                if (!showSpaces) foreach (var x in list) ThreadRow(x, 2);
                else
                    foreach (var g in groups)
                    {
                        items.Add(new UiListItem
                        {
                            Kind = UiListKind.Compact,
                            Indent = 2,
                            Icon = g.Starting ? "◌" : g.IsWorktree ? "⎇" : "▣",
                            IconColor = g.Clash ? t.Danger : t.TextDim,
                            Title = g.Starting ? "setting up" : g.IsWorktree ? (g.Branch.Length > 0 ? g.Branch : "worktree") : "checkout" + (g.Branch.Length > 0 ? " · " + g.Branch : ""),
                            TitleColor = t.TextDim,
                            Sub = g.IsWorktree ? (g.Base.Length > 0 ? "from " + g.Base : "worktree") : g.Threads.Count > 1 ? "shared folder" : "",
                            Badge = g.Clash ? g.Working + " WORKING ⚠" : g.Waiting > 0 ? g.Waiting + " WAITING" : "",
                            BadgeColor = g.Clash ? t.Danger : t.Warn,
                            Tag = "w:" + pid + "|" + g.Key,
                        });
                        foreach (var x in g.Threads) ThreadRow(x, 3);
                    }
                if (list.Count == 0) items.Add(new UiListItem { Kind = UiListKind.Compact, Indent = 2, Icon = "·", IconColor = t.TextDim, Title = "no threads yet: + NEW THREAD…", TitleColor = t.TextDim, Tag = "p:" + pid });
            }
            int total = _threads.Count, run = _threads.Count(x => ThreadTimeline.IsWorking(ThreadStatus(x))), need = _threads.Count(x => x["hasPendingInteraction"]?.Value<bool>() == true);
            if (_projects.Count == 0) _threadsStatus.Set("No project yet: + PROJECT… makes one in " + ProjectKinds.Root + "/ (native, default, research or a plugin).", t.TextDim);
            else _threadsStatus.Set($"{_projects.Count} projects · {total} threads" + (run > 0 ? $" · <color=#{UiViz.Hex(t.Ok)}>{run} running</color>" : "") + (need > 0 ? $" · <color=#{UiViz.Hex(t.Warn)}>{need} need you</color>" : "") + " · tap a type or a project to fold it", t.TextDim);
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

        /// A type's colour in THREADS (its band's bar, rule and fold icon): NATIVE the accent, DEFAULT violet, RESEARCH green,
        /// PLUGINS amber, OTHER steel.
        private static Color TypeColor(UiTheme t, string type) =>
            type == "native" ? t.Accent : type == "default" ? t.Violet : type == "research" ? t.Ok : type == ProjectKinds.Plugins ? t.Warn : t.Frame;

        // a project's type (mcx-project): where it is (metacodex-projects/<type>/...), else .metacodex/project.json, else an
        // app folder (AndroidManifest.xml + src/) is native; null (OTHER) when nothing says. Read at most every 30 s per
        // folder: RenderThreadGroups runs often.
        private readonly Dictionary<string, (string type, float at)> _projectTypes = new Dictionary<string, (string, float)>();

        private string ProjectType(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath)) return null;
            string byPath = ProjectKinds.TypeFromPath(linuxPath);
            if (byPath != null) return byPath;
            if (_projectTypes.TryGetValue(linuxPath, out var c) && Time.unscaledTime - c.at < 30f) return c.type;
            string type = null;
            try
            {
                string host = AndroidPathOf(linuxPath);
                if (host != null)
                {
                    string j = Path.Combine(host, ".metacodex", "project.json");
                    type = File.Exists(j) ? ProjectKinds.TypeFromJson(File.ReadAllText(j))
                        : File.Exists(Path.Combine(host, "AndroidManifest.xml")) && Directory.Exists(Path.Combine(host, "src")) ? "native" : null;
                }
            }
            catch (Exception) { }
            _projectTypes[linuxPath] = (type, Time.unscaledTime);
            return type;
        }

        /// + PROJECT…: first the type (one dialog; PLUGIN asks its kind in a second one), then the name (the keyboard);
        /// the project is made in metacodex-projects/<type>/<folder from the name> (a plugin: plugins/<kind folder>/<id>)
        /// with git. EXISTING FOLDER… adds a folder that is there.
        private void NewProjectPrompt() => NewProjectPrompt(null);

        /// name: already known (voice): made as soon as the type (and a plugin's kind) is picked.
        private void NewProjectPrompt(string name)
        {
            Modal("NEW PROJECT", (name != null ? "“" + name.Trim() + "”: what" : "What") + " will this project be? NATIVE APP: a Quest app (native skills and rules). DEFAULT: any other coding. RESEARCH: notes and findings, no app build. PLUGIN: a part of this app (a window and a menu tile, gestures or a behaviour) that runs at once.",
                new[] { "NATIVE APP", "DEFAULT", "RESEARCH", "PLUGIN", "EXISTING…" }, choice =>
                {
                    if (choice >= 0 && choice < 3) { if (name != null) CreateNewProject(ProjectKinds.Types[choice], name); else NewProjectNamed(ProjectKinds.Types[choice]); }
                    else if (choice == 3) NewPluginKindPrompt(name);
                    else if (choice == 4) ExistingProjectPrompt();
                });
        }

        /// The name of a new project of this type, on the keyboard (an empty field), then the project. A plugin asks its
        /// kind first.
        private void NewProjectNamed(string type)
        {
            if (type == ProjectKinds.Plugins) { NewPluginKindPrompt(null); return; }
            UiKeyboard.Edit("", false, null, name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return; // cancelled
                CreateNewProject(type, name);
            }, "NEW " + ProjectKinds.Header(type) + " PROJECT · its name (e.g. Weather app)");
        }

        /// NEW PROJECT > PLUGIN (and MQH > PLUGINS > + NEW PLUGIN…): the kind (APP, GESTURE, BEHAVIOUR), then the name.
        /// preset: a kind already chosen (the PLUGINS tab's SHOW filter, voice): straight to the name. name: already
        /// known (voice): made as soon as the kind is picked.
        public void NewPluginKindPrompt(string name, string preset = null)
        {
            preset = PluginProject.NormalizeKind(preset);
            if (preset != null) { if (!string.IsNullOrWhiteSpace(name)) CreateNewProject(ProjectKinds.Plugins, name, preset); else NewPluginNamed(preset); return; }
            Modal("NEW PLUGIN", (name != null ? "“" + name.Trim() + "”: what" : "What") + " kind of plugin? APP: its own window and a tile on the main menu (a timer, a tracker). GESTURE: named hand gestures and what they do. BEHAVIOUR: Lua that reacts to your hands, your voice or the time, with no window. The kind stays fixed.",
                new[] { "APP", "GESTURE", "BEHAVIOUR" }, choice =>
                {
                    if (choice < 0 || choice >= PluginProject.Kinds.Length) return;
                    string kind = PluginProject.Kinds[choice];
                    if (!string.IsNullOrWhiteSpace(name)) CreateNewProject(ProjectKinds.Plugins, name, kind); else NewPluginNamed(kind);
                });
        }

        private void NewPluginNamed(string kind) =>
            UiKeyboard.Edit("", false, null, name => { if (!string.IsNullOrWhiteSpace(name)) CreateNewProject(ProjectKinds.Plugins, name, kind); },
                "NEW " + PluginProject.Label(kind).ToUpperInvariant() + " PLUGIN · its name (e.g. " + (kind == "app" ? "Water tracker" : kind == "gesture" ? "Fist shortcuts" : "Clap sounds") + ")");

        /// Makes the folder metacodex-projects/<type>/<name as a folder>, starts git there (a local identity only when
        /// there is no global one, so the first commit works offline), sets the type (mcx-project) and adds the project
        /// to MQH. Refuses a folder that is there already. A plugin (type plugins, with its kind): CreatePluginProject.
        /// Returns what it does (voice) or an error.
        public string CreateNewProject(string type, string name, string kind = null)
        {
            if (type == ProjectKinds.Plugins) return CreatePluginProject(kind, name);
            name = (name ?? "").Trim();
            string path = ProjectKinds.PathFor(type, name);
            if (path == null) { Toast("Name the project with letters or digits", "warn"); return "error: say a name with letters or digits"; }
            if (_projects.Any(pr => (string)pr["sources"]?.FirstOrDefault()?["path"] == path)) { Toast("A project is there already: " + path, "warn"); return "error: a project is there already"; }
            string host = AndroidPathOf(path);
            if (host != null && (Directory.Exists(host) || File.Exists(host))) { Toast("That folder exists already: " + path + " (pick another name)", "warn"); return "error: the folder exists already"; }
            void Add() => EnsureHost(() => Mqh.CreateProject(name, _hostId, path, r =>
            {
                if (r == null) { Toast("Project not added: " + Mqh.LastError, "error"); return; }
                _projectSel = (string)r["id"];
                Settings.lastProject = _projectSel;
                _collapsedProjects.Remove(_projectSel);
                _collapsedTypes.Remove(type);
                Toast($"Project {r["name"]} made in {path} ({ProjectKinds.Label(type)})", "ok");
                RefreshThreads();
            }));
            if (Git == null || !Git.InUserland) { Add(); return "Adding the project " + name; } // the Editor: MQH only
            string q = GitService.Quote(path);
            string cmd = $"if [ -e {q} ]; then echo 'MCX: the folder exists already'; exit 17; fi; " +
                         $"mkdir -p {q} && git -c init.defaultBranch=main init -q {q} && " +
                         $"{{ git -C {q} config --get user.name >/dev/null || git -C {q} config user.name MetaCodex; }} && " +
                         $"{{ git -C {q} config --get user.email >/dev/null || git -C {q} config user.email metacodex@localhost; }} || exit 19; " +
                         $"mcx-project set {q} {type} || exit 18";
            System.Threading.Tasks.Task.Run(async () =>
            {
                var res = await Git.Shell(cmd, 30000);
                _main.Enqueue(() =>
                {
                    if (res.code == 17) { Toast("That folder exists already: " + path + " (pick another name)", "warn"); return; }
                    if (res.code == 18) Toast("Project type not set (its folder says it): " + Cut(res.output ?? "", 80), "warn");
                    else if (res.code != 0) { Toast("Project folder not made: " + Cut(res.output ?? "", 90), "error"); return; }
                    _projectTypes.Remove(path);
                    Add();
                });
            });
            return $"Making the {ProjectKinds.Label(type).ToLowerInvariant()} project {name}";
        }

        /// A plugin project (owner 2026-11-16): /root/metacodex-projects/plugins/<apps|gestures|behaviours>/<id>, its kind
        /// fixed by the folder and the read-only marker .metacodex/project.json. The app writes the files itself (the
        /// scaffold of the kind, or `files`: an example's, with the project's AGENTS.md and CLAUDE.md) and the plugin runs
        /// at once (ScriptHost.CreateProjectPlugin); then git starts there (the first commit holds the files) and the
        /// project is added to MQH, so THREADS lists it under PLUGINS and a thread on it lets an agent change the plugin.
        /// No kind: the kind dialog. Returns what it does (voice, the control port) or "error: ...".
        public string CreatePluginProject(string kind, string name, IReadOnlyDictionary<string, string> files = null, string id = null)
        {
            name = (name ?? "").Trim();
            string k = PluginProject.NormalizeKind(kind);
            if (k == null) { NewPluginKindPrompt(name.Length > 0 ? name : null); return "App, gesture or behaviour?"; }
            id = PluginProject.IdFor(id ?? name);
            if (id.Length == 0) { Toast("Name the plugin with letters or digits", "warn"); return "error: say a name with letters or digits"; }
            if (Scripts == null) return "error: plugins are not running";
            if (!Application.isEditor && (app?.Host == null || !Directory.Exists(AndroidPathOf("/root") ?? "")))
            {
                Toast("The Linux is not set up yet: plugins can be made once it is", "warn");
                return "error: the Linux is not set up yet";
            }
            string path = PluginProject.LinuxDirOf(k, id);
            var all = new Dictionary<string, string>();
            if (files != null) foreach (var kv in files) all[kv.Key] = kv.Value;
            else foreach (var kv in PluginProject.Scaffold(k, id, name)) all[kv.Key] = kv.Value;
            foreach (var kv in PluginProject.AgentFiles(k, id, name, fromExample: files != null)) if (!all.ContainsKey(kv.Key)) all[kv.Key] = kv.Value;
            var e = Scripts.CreateProjectPlugin(k, id, all, out var err);
            if (e == null) { Toast("Plugin not made: " + err, "warn"); return "error: " + err; }
            Log($"plugin project {id} ({k}) made in {path}" + (e.Error != null ? ", error: " + e.Error : ""));
            _projectTypes.Remove(path);
            _collapsedTypes.Remove(ProjectKinds.Plugins);
            RefreshPluginList();
            SelectPluginForTest(id);
            void Add() => EnsureHost(() => Mqh.CreateProject(name, _hostId, path, r =>
            {
                if (r == null) { Toast($"Plugin {id} made; its project is not in THREADS yet ({Mqh.LastError}): NEW THREAD ▸ in MQH > PLUGINS adds it", "warn"); return; }
                _projectSel = (string)r["id"];
                Settings.lastProject = _projectSel;
                _collapsedProjects.Remove(_projectSel);
                _pluginProjects = null; // the PLUGINS tab reads MQH's projects (and this plugin's threads) again
                _pluginThreadsFor = null;
                Toast($"Plugin {name} made ({PluginProject.Label(k).ToLowerInvariant()}): THREADS > PLUGINS has its project; a thread there lets an agent change it", "ok");
                RefreshThreads();
                ShowPluginDetail();
            }));
            if (Git == null || !Git.InUserland) { Add(); return $"Made the {k} plugin {name}"; } // the Editor: MQH only
            string q = GitService.Quote(path);
            string msg = files != null ? "The " + name + " plugin, as it ships with MetaCodex" : "A new " + k + " plugin: " + name + " (MetaCodex's scaffold)";
            string cmd = $"cd {q} && chmod 444 .metacodex/project.json && git -c init.defaultBranch=main init -q . && " +
                         "{ git config --get user.name >/dev/null || git config user.name MetaCodex; } && " +
                         "{ git config --get user.email >/dev/null || git config user.email metacodex@localhost; } && " +
                         $"git add -A && git commit -q -m {GitService.Quote(msg)}";
            System.Threading.Tasks.Task.Run(async () =>
            {
                var res = await Git.Shell(cmd, 30000);
                _main.Enqueue(() =>
                {
                    if (res.code != 0) Toast("Plugin made; git did not start there: " + Cut(res.output ?? "", 80), "warn");
                    Add();
                });
            });
            return $"Making the {k} plugin {name}";
        }

        /// EXISTING FOLDER…: a folder of the Linux userland as it is (no git init), then its type.
        private void ExistingProjectPrompt()
        {
            string start = !string.IsNullOrEmpty(_filesPath) && _filesPath != "/" ? _filesPath : "/root/";
            UiKeyboard.Edit(start, false, null, path =>
            {
                path = (path ?? "").Trim().TrimEnd('/');
                if (path.Length < 2) return;
                // the type decides what the project's agents get: a Native app project carries the quest-native skills and
                // rules (mcx-project), a Research one a research note, a Default one none of them
                Modal("PROJECT TYPE", path + "\nWhat is this project?", new[] { "NATIVE APP", "DEFAULT", "RESEARCH" }, choice =>
                {
                    if (choice < 0 || choice >= ProjectKinds.Types.Length) return;
                    string type = ProjectKinds.Types[choice];
                    void Create() => EnsureHost(() => Mqh.CreateProject(path.Split('/').Last(), _hostId, path, r =>
                    {
                        if (r == null) { Toast("Project not added: " + Mqh.LastError, "error"); return; }
                        _projectSel = (string)r["id"];
                        Settings.lastProject = _projectSel;
                        _collapsedProjects.Remove(_projectSel);
                        _projectTypes.Remove(path);
                        Toast("Project " + r["name"] + " added (" + ProjectKinds.Label(type) + ")", "ok");
                        RefreshThreads();
                    }));
                    if (Git == null || !Git.InUserland) { Create(); return; }
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var res = await Git.Shell("mkdir -p " + GitService.Quote(path) + " && mcx-project set " + GitService.Quote(path) + " " + type, 20000);
                        _main.Enqueue(() => { if (res.code != 0) Toast("Project type not set: " + Cut(res.output ?? "", 80), "warn"); Create(); });
                    });
                });
            }, "NEW PROJECT · the folder that is there (Linux path)");
        }

        // ------------------------------------------------------------------ THREAD

        /// THREAD's width: the conversation plus the strip on its right edge for the sidebar's expand handle.
        public const float ThreadW = 0.73f, ThreadHandleW = 0.03f;
        private readonly Dictionary<string, (string model, string effort, string perm)> _thOverrides = new Dictionary<string, (string, string, string)>();
        private readonly Dictionary<string, JArray> _modelsByProvider = new Dictionary<string, JArray>();

        /// Every THREAD window (ShellThreadWindows): "thread" always, the extra ones that are open; then their groups.
        private void BuildThread()
        {
            foreach (var w in Tws.ToList()) BuildThreadWindow(w);
            RebuildGroupPanels(); // RebuildPanels: the groups' panels again, their windows back in them
            RefreshWindowTitles();
        }

        private void BuildThreadWindow(ThreadWindow w)
        {
            using (In(w)) BuildThreadWindowIn(w);
        }

        private void BuildThreadWindowIn(ThreadWindow w)
        {
            _thAskButtons.Clear(); // RebuildPanels builds the panel again
            var t = UiTheme.Current;
            var p = Panel<UiPanel>(w.Id, w.Number == 1 ? "Thread" : "Thread " + w.Number, ThreadW, ThreadH);
            w.Panel = p;
            // a large ✕ at the top-right (owner 2026-11-14: easy to hit, also when the window is shrunk in a group): it
            // closes this window only; the thread stays in MQH and in THREADS
            p.HideCloseButton();
            w.CloseButton = p.Add(new UiButton("✕", () => { if (!w.Dead && w.Panel != null) w.Panel.Close(); }, UiButtonStyle.Danger)
                { Rect = new Rect(ThreadW - 0.088f, 0.004f, 0.082f, t.HeaderHeight - 0.008f), TextSize = t.BodySize * 1.35f });
            var c0 = p.Content;
            var c = new Rect(c0.x, c0.y, c0.width - ThreadHandleW, c0.height); // the conversation; its right edge holds the handle
            // the expand handle on the right edge: the sidebar (changes, files, git, status, views) opens beside it
            _thHandle = p.Add(new UiEdgeHandle { Rect = new Rect(c.xMax + t.Gap * 0.6f, c.y + 0.06f, ThreadW - c.xMax - t.Gap * 0.6f - 0.004f, c.height - 0.06f - 0.12f), OnClickAction = Bind(ToggleSidebar) });
            var st = new UiStack(c, t.Gap);
            var top = st.Next(0.03f);
            float bw = 0.082f, aw0 = 0.088f;
            _thTitle = p.Add(new UiLabel("", t.BodySize * 1.1f) { Rect = new Rect(top.x, top.y, top.width - bw * 3 - aw0 - t.Gap * 4, top.height) });
            // ARCHIVE… and DELETE… for this window's own thread (ShellThreadArchive; hidden while it shows no thread)
            w.thArchive = p.Add(new UiButton("ARCHIVE…", Bind(ArchiveThreadPrompt)) { Rect = new Rect(top.xMax - bw * 3 - aw0 - t.Gap * 3, top.y, aw0, top.height), TextSize = t.SmallSize * 1.05f });
            w.thDelete = p.Add(new UiButton("DELETE…", Bind(DeleteThreadPrompt), UiButtonStyle.Danger) { Rect = new Rect(top.xMax - bw * 3 - t.Gap * 2, top.y, bw, top.height), TextSize = t.SmallSize * 1.05f });
            p.Add(new UiButton("⑂ FORK…", Bind(ForkPrompt)) { Rect = new Rect(top.xMax - bw * 2 - t.Gap, top.y, bw, top.height), TextSize = t.SmallSize * 1.05f });
            p.Add(new UiButton("⟳", Bind(() => { _tlMaxSeq = -1; _tlTag = null; _tlInFlight = false; _nextThreadMeta = 0f; PollThread(); })) { Rect = new Rect(top.xMax - bw, top.y, bw, top.height), TextSize = t.SmallSize * 1.05f });
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
                _thAskButtons.Add(p.Add(new UiButton(asks[i].label, Bind(() => Resolve(d)), i == 0 ? UiButtonStyle.Primary : i == 2 ? UiButtonStyle.Danger : UiButtonStyle.Normal) { Rect = new Rect(askRow.x + i * (aw + t.Gap), askRow.y, aw, askRow.height) }));
            }
            // attachments (captures, Linux files) for the next message, then the composer with its + (owner 2026-11-09),
            // then the bb-like chips: model, access, workspace, context, stop
            float cy = c.yMax - chipH - t.Gap - t.ButtonHeight;
            float sendW = 0.11f, plusW = t.ButtonHeight * 1.2f, micW = t.ButtonHeight * 1.25f;
            BuildAttachRow(p, new Rect(c.x, cy - t.Gap - chipH, c.width, chipH));
            _thPlus = p.Add(new UiButton("+", Bind(ToggleAttachMenu), UiButtonStyle.Primary) { Rect = new Rect(c.x, cy, plusW, t.ButtonHeight), TextSize = t.BodySize * 1.3f });
            _thComposer = p.Add(new UiTextField("", "", Bind<string>(s => SendToThread(s)), "message the agent… (or ◉ to say it)") { Rect = new Rect(c.x + plusW + t.Gap, cy, c.width - sendW - plusW - micW - t.Gap * 3, t.ButtonHeight), CaptionWidth = 0f, Wrap = true });
            // the grip on its top edge makes the box taller (ShellThreadComposer): the chat above gets shorter
            BuildComposerGrip(p, _thComposer.Rect, c0.height, _tlArea.height);
            // ◉: say the message (speech to text into the composer; SEND stays yours)
            var composer = _thComposer;
            _thMic = p.Add(new UiButton("◉", () => DictateInto(composer)) { Rect = new Rect(c.xMax - sendW - t.Gap - micW, cy, micW, t.ButtonHeight), TextSize = t.BodySize * 1.15f });
            p.Add(new UiButton("SEND ▶", Bind(() => SendToThread(_thComposer.Text)), UiButtonStyle.Primary) { Rect = new Rect(c.xMax - sendW, cy, sendW, t.ButtonHeight) });
            float y = c.yMax - chipH;
            float[] cw = { 0.17f, 0.13f, 0.165f, 0.135f };
            float x = c.x;
            float ts = t.SmallSize * 1.02f;
            _thModelChip = p.Add(new UiButton("◇ model ▾", Bind(ModelPrompt)) { Rect = new Rect(x, y, cw[0], chipH), TextSize = ts }); x += cw[0] + t.Gap;
            _thPermChip = p.Add(new UiButton("✎ Edits ▾", Bind(PermissionPrompt)) { Rect = new Rect(x, y, cw[1], chipH), TextSize = ts }); x += cw[1] + t.Gap;
            _thWsChip = p.Add(new UiButton("▣ checkout", Bind(() => SetSidebar(true, TabFiles))) { Rect = new Rect(x, y, cw[2], chipH), TextSize = ts }); x += cw[2] + t.Gap;
            _thContext = p.Add(new UiRing { Rect = new Rect(x, y, cw[3], chipH), OnClickAction = Bind(ContextPrompt) }); x += cw[3] + t.Gap;
            p.Add(new UiButton("■ STOP", Bind(() => { if (_threadOpen != null) Mqh.StopThread(_threadOpen, Bind<JToken>(r => { Toast(r != null ? "Stopping the turn" : "MQH: " + Mqh.LastError); if (r != null) ResyncThread(1.5f); })); }), UiButtonStyle.Danger) { Rect = new Rect(x, y, c.xMax - x, chipH), TextSize = ts });
            // the attach modal: drawn over the timeline area, inside this panel (not a panel of its own)
            BuildAttachModal(p, _tlArea);
            // each window ticks in its own scope: its status line, its pulse, its polls (only while it is open)
            p.Tick += _ =>
            {
                if (w.Dead) return;
                using (In(w))
                {
                    TickPanelPulse(p);
                    TickThreadView();
                    if (_resyncAt > 0f && Time.unscaledTime >= _resyncAt) { _resyncAt = -1f; ResyncThread(); return; }
                    if (_threadOpen != null && Time.unscaledTime >= _nextThreadPoll) PollThread();
                }
            };
            p.Closed += _ => OnThreadWindowClosed(w);
            p.Opened += _ => { UpdateSidebarVoice(); UpdateThreadWindowVoice(); RefreshWindowTitles(); };
            SetAskVisible(false);
            RefreshAttachRow();
            UpdateThreadActions();
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

        // ---- recently used (moved here from the orb's thread list, which is gone: owner 2026-11-13)

        // when the user last used each thread here (opened it, wrote to it), unix ms; kept in a small file of its own
        // (not in the settings: MQH's copy of those would bring an old list back). Read by LastUsedThread (ShellReturn)
        private readonly Dictionary<string, long> _threadUsedAt = new Dictionary<string, long>();
        private bool _threadUseLoaded;

        private static string ThreadUsePath => Path.Combine(Application.persistentDataPath, "recent-threads.txt");

        private void LoadThreadUse()
        {
            if (_threadUseLoaded) return;
            _threadUseLoaded = true;
            if (!Application.isPlaying) return; // self-tests: in memory only
            try
            {
                if (!File.Exists(ThreadUsePath)) return;
                foreach (var line in File.ReadAllLines(ThreadUsePath))
                {
                    int sp = line.IndexOf(' ');
                    if (sp <= 0 || !long.TryParse(line.Substring(0, sp), out long at)) continue;
                    string id = line.Substring(sp + 1).Trim();
                    if (id.Length > 0 && (!_threadUsedAt.TryGetValue(id, out var had) || had < at)) _threadUsedAt[id] = at;
                }
            }
            catch (Exception ex) { Log("recent threads unreadable: " + ex.Message); }
        }

        /// The user used a thread here (opened it, wrote to it): it is the most recently used one.
        private void NoteThreadUsed(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            LoadThreadUse();
            _threadUsedAt[id] = NowMs;
            if (_threadUsedAt.Count > 24)
                foreach (var old in _threadUsedAt.OrderBy(kv => kv.Value).Take(_threadUsedAt.Count - 24).Select(kv => kv.Key).ToList()) _threadUsedAt.Remove(old);
            if (!Application.isPlaying) return;
            try { File.WriteAllLines(ThreadUsePath, _threadUsedAt.OrderByDescending(kv => kv.Value).Select(kv => kv.Value + " " + kv.Key)); }
            catch (Exception ex) { Log("recent threads not saved: " + ex.Message); }
        }

        /// Opens a thread (THREADS, voice, a callout, a return from an app, a fork): the window that shows it already is
        /// focused; else it opens in the focused window when that is closed or empty, else in a new window beside it
        /// (ShellThreadWindows.PickWindowForOpen). A thread of a project that has a window open joins that window's
        /// group (ShellThreadGroups.AutoGroup), unless the user pulled it out of it.
        public void OpenThread(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var shown = Tws.FirstOrDefault(x => !x.Dead && x.threadOpen == id && (WinOpen(x) || x.Panel == null || x == _twFirst));
            var w = shown ?? PickWindowForOpen();
            OpenThreadIn(w, id);
            if (shown == null) AutoGroup(w);
        }

        /// Opens the thread in this window (it replaces what the window showed).
        private void OpenThreadIn(ThreadWindow w, string id)
        {
            if (string.IsNullOrEmpty(id) || w == null || w.Dead) return;
            _twOpener = _twFocus != w && WinOpen(_twFocus) ? (_twFocus.Group?.Panel ?? _twFocus.Panel) : null;
            FocusWindow(w);
            using (In(w)) OpenThreadHere(id);
            _twOpener = null;
            RefreshWindowTitles();
        }

        private void OpenThreadHere(string id)
        {
            DiscardDraft(); // a draft never sent is dropped (nothing was made in MQH)
            NoteThreadUsed(id); // the thread used last (the way back from an app, ShellReturn)
            if (id != _threadOpen) { if (_threadOpen != null) _pulledOut.Remove(_threadOpen); _thExec = null; _thUsage = null; _threadInfo = null; _tlRaw = new JArray(); CloseAttachModal(); SetAttachMenu(false); _threadOpen = id; ResetThreadChat(); }
            _threadOpen = id;
            UpdateThreadActions();
            RefreshAttachRow(); // each thread keeps its own attachments for its next message
            var row = _threads.FirstOrDefault(x => (string)x["id"] == id) as JObject;
            if (row != null) { _threadInfo = row; _threadOpenProject = (string)row["projectId"]; }
            else _threadOpenProject = null;
            _tlMaxSeq = -1;
            _tlOpenSeq = -1;
            _tlTag = null; // a full timeline first
            _thTitle?.Set("loading …");
            _thContext?.Set(-1f, "context —");
            ShowThreadWindow();
            SbSetThread(id, (string)row?["environmentPath"]); // only when this window has the sidebar
            // the sidebar as this thread had it: open (it comes to this window), or folded when it is this window's
            var mem = SidebarMemory(id);
            if (mem.open) SetSidebar(true, mem.tab, remember: false);
            else if (_sbOwner == _tw) SetSidebar(false, mem.tab, remember: false);
            UpdateChips();
            Mqh.ExecOptions(id, Bind<JToken>(r => { if (id != _threadOpen || !(r is JObject jo)) return; _thExec = jo; UpdateChips(); }));
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


        public void PollThread()
        {
            if (_threadOpen == null || _thTitle == null) return;
            string id = _threadOpen;
            bool live = _thPulse != null && _thPulse.Working || ThreadTimeline.IsWorking(StatusOfRow(_threadInfo));
            // a window that is not the focused one polls half as often (several windows: memory, MQH, the main thread)
            float slow = _tw == _twFocus ? 1f : 2f;
            _nextThreadPoll = Time.unscaledTime + (live ? 0.8f : 2.5f) * slow;
            if (Time.unscaledTime >= _nextThreadMeta)
            {
                _nextThreadMeta = Time.unscaledTime + (live ? 2f : 3f) * slow;
                Mqh.Threads(_threadOpenProject, Bind<JToken>(th =>
                {
                    var row = (th as JArray)?.FirstOrDefault(x => (string)x["id"] == id) as JObject;
                    if (id != _threadOpen) return;
                    if (row == null) { if (th is JArray) CheckThreadGone(id); return; } // archived or deleted elsewhere: its window closes
                    _tw.goneCheckAt = 0f;
                    bool statusChanged = StatusOfRow(row) != StatusOfRow(_threadInfo);
                    _threadInfo = row;
                    _threadOpenProject = (string)row["projectId"];
                    // the project's rows update the workspace map (who else works in this folder); every 2 s while it
                    // works: a row that did not change is not copied again, and the map is made again only after a change
                    bool rowsChanged = false;
                    foreach (var pr in (th as JArray).OfType<JObject>())
                    {
                        string prId = (string)pr["id"];
                        int at = -1;
                        for (int k = 0; k < _threads.Count; k++) if ((string)_threads[k]["id"] == prId) { at = k; break; }
                        if (at >= 0 && JToken.DeepEquals(_threads[at], pr)) continue;
                        if (at >= 0) _threads[at] = pr.DeepClone(); else _threads.Add(pr.DeepClone());
                        rowsChanged = true;
                    }
                    if (rowsChanged) _spaces = WorkspaceMap.Build(_threads, _environments);
                    ShowThreadHeader();
                    PollThreadQueue(id); // a turn MQH holds (the workspace guard): its reason, SEND NOW
                    // working <-> idle: parse the whole timeline again with the new status (a delta may say "no change")
                    if (statusChanged) { _tlTag = null; _tlInFlight = false; RebuildChat(); RequestTimeline(id); }
                }));
                Mqh.Interactions(id, Bind<JToken>(ia =>
                {
                    if (id != _threadOpen) return;
                    _interactions = ia as JArray ?? new JArray();
                    ShowAsk();
                    CtxInteractionsUpdated(id);
                }));
            }
            RequestTimeline(id);
        }

        /// Agent views in the thread's messages: registered; the ones that arrive while it is open also open.
        // the last message ScanViews looked at, per thread: an update looks only at newer ones (0.7.19: every agent
        // message with a code block was parsed again at every update of a working thread)

        private void ScanViews(JArray rows, string threadId, bool first)
        {
            if (first || _viewsScanThread != threadId) { _viewsScanThread = threadId; _viewsScanSeq = -1; }
            long upTo = _viewsScanSeq;
            void Walk(JToken r)
            {
                if ((string)r["kind"] == "turn") { foreach (var ch in r["children"] as JArray ?? new JArray()) Walk(ch); return; }
                if ((string)r["kind"] != "conversation" || (string)r["role"] == "user") return;
                long seq = r["sourceSeqEnd"]?.Type == JTokenType.Integer ? (long)r["sourceSeqEnd"] : 0;
                if (seq > 0 && seq <= upTo) return; // looked at already (a message still being written keeps its end seq growing)
                if (seq > _viewsScanSeq && (string)r["status"] != "pending" && (string)r["status"] != "streaming") _viewsScanSeq = seq;
                string text = (string)r["text"];
                if (string.IsNullOrEmpty(text) || text.IndexOf("```", StringComparison.Ordinal) < 0) return;
                bool fresh = !first && seq > _tlOpenSeq;
                ViewsFromMessage(threadId, seq, text, fresh);
            }
            foreach (var r in rows) Walk(r);
            if (first) _tlOpenSeq = _tlMaxSeq;
        }

        private void ShowThreadHeader()
        {
            UpdateThreadActions();
            if (_threadOpen == null && _draft != null) { ShowDraftHeader(); return; }
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

        private string CurrentModel() => Override(ThreadKey).model ?? (string)_thExec?["model"];
        private string CurrentEffort() => Override(ThreadKey).effort ?? (string)_thExec?["reasoningLevel"];
        private string CurrentPerm() => Override(ThreadKey).perm ?? (string)_thExec?["permissionMode"] ?? Settings.permissionMode;

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
                EnsureHost(() => Mqh.Models(provider, _hostId, m =>
                {
                    _modelsByProvider[provider] = m?["models"] as JArray ?? new JArray();
                    foreach (var w in Tws.Where(WinOpen).ToList()) using (In(w)) UpdateChips(); // every window of that provider
                }));
            }
        }

        private void ModelPrompt()
        {
            string key = ThreadKey; // the open thread, or the draft (its first message uses the choice)
            if (key == null) return;
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
                var o = Override(key);
                _thOverrides[key] = (id, efforts.Contains(o.effort ?? "") ? o.effort : null, o.perm);
                UpdateChips();
                if (efforts.Count > 1)
                {
                    string curE = CurrentEffort();
                    ShowPicker("Effort", (string)m["displayName"] + ": how hard it thinks.", efforts.Select(e => new UiListItem { Icon = e == curE ? "◆" : "◇", Title = Cap(e), Tag = e }), it2 =>
                    {
                        var o2 = Override(key);
                        _thOverrides[key] = (o2.model, it2.Tag as string, o2.perm);
                        UpdateChips();
                    }, ThreadPanel);
                }
            }, ThreadPanel);
        }

        private void PermissionPrompt()
        {
            string key = ThreadKey; // the open thread, or the draft (its first message uses the choice)
            if (key == null) return;
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
                var o = Override(key);
                _thOverrides[key] = (o.model, o.effort, item.Tag as string);
                Settings.permissionMode = item.Tag as string;
                UpdateChips();
            }, ThreadPanel);
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
                Mqh.CompactThread(_threadOpen, Bind<JToken>(r => { Toast(r != null ? "Compacting the context" : "Not compacted: " + Mqh.LastError, r != null ? "ok" : "error"); PollThread(); }));
            }, ThreadPanel);
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
                }, ThreadPanel);
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
                Mqh.ForkThread(body, Bind<JToken>(r =>
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
                }));
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
            EnsureHost(Bind(() => Mqh.CreateThread(create, Bind<JToken>(r =>
            {
                if (r == null) { Toast("Not forked: " + Mqh.LastError, "error"); return; }
                _thComposer?.Set("");
                Toast("Forked to " + provider, "ok");
                RefreshThreadList();
                OpenThreadAfterRefresh((string)r["id"]);
            }))));
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
            // the files it touched: the sidebar's list when this window has the sidebar, else read from its own timeline
            var touched = _tw == _sbOwner ? _touched : ThreadChanges.FromTimeline(_tlRaw, (string)_threadInfo?["environmentPath"]);
            var files = touched.Values.Where(f => f.ByThread).Take(30).ToList();
            if (files.Count > 0) sb.Append("Files touched: " + string.Join(", ", files.Select(f => $"{f.Path} (+{f.Added} -{f.Removed})")) + "\n");
            return sb.ToString();
        }

        // ---- self-tests: a thread from canned MQH data (no daemon needed)

        public void LoadThreadForTest(JObject row, JArray rows, JToken usage, JObject exec, JArray projects, bool openViews)
        {
            if (projects != null) _projects = projects;
            _threads = new JArray(row);
            OpenThreadIn(_twFocus, (string)row["id"]); // the focused window (the tests' one window)
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
            if (_tw == _sbOwner) WsTrackRows(rows);
            ScanViews(rows, (string)row["id"], false);
        }

        /// Self-tests: more rows arrive in the open thread (as the next poll would bring them).
        public void AppendTimelineForTest(JArray more)
        {
            foreach (var r in more) _tlRaw.Add(r);
            RebuildChat();
            _tlMaxSeq = _tlRaw.Max(r => r["sourceSeqEnd"]?.Value<long>() ?? 0);
            if (_tw == _sbOwner) WsTrackRows(_tlRaw);
            ScanViews(_tlRaw, _threadOpen, false);
        }

        public void SetThreadsForTest(JArray projects, JArray threads) { _projects = projects; _threads = threads; RenderThreadGroups(); }
        public UiList ThreadListForTest => _threadList;
        public void PickThreadRowForTest(int index) => ThreadRowPicked(_threadList.Items[index]);
        public void PickThreadRowForTest(string tag) => ThreadRowPicked(_threadList.Items.First(i => i.Tag as string == tag));
        /// Self-tests: START in NEW THREAD (THREAD opens as a draft), and the draft's create request for a first message.
        public void StartThreadForTest() => StartThread();
        public JObject DraftBodyForTest(string prompt) => _draft == null ? null : NewThreadBody(_draft.ProjectId, _draft.Provider, Override(DraftKey).model ?? _draft.Model, prompt, _draft.Worktree, _draft.ReuseEnvId, Override(DraftKey).perm ?? _draft.Perm);
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
            Mqh.ResolveApproval(_threadOpen, (string)pending["id"], decision, Bind<JToken>(r =>
            {
                Toast(r != null ? "Answered: " + decision.Replace('_', ' ') : "MQH: " + Mqh.LastError, r != null ? "ok" : "error");
                SetAskVisible(false);
                PollThread();
            }));
        }

        private void SendToThread(string text)
        {
            var att = AttachList;
            bool hasAtt = att != null && att.Count > 0;
            if ((string.IsNullOrWhiteSpace(text) && !hasAtt) || ThreadKey == null) return;
            ResetComposerHeight(); // sent: the message box goes back to one row (owner 2026-11-14)
            string msg = string.IsNullOrWhiteSpace(text) ? AttachmentsOnlyText(att) : text.Trim();
            var sent = hasAtt ? att.ToList() : null;
            if (_threadOpen == null) { SendDraft(msg, sent, att); return; } // the draft's first message makes the thread
            NoteThreadUsed(_threadOpen);
            var o = Override(_threadOpen);
            var extra = new JObject();
            if (o.model != null) extra["model"] = o.model;
            if (o.effort != null) extra["reasoningLevel"] = o.effort;
            if (o.perm != null) extra["permissionMode"] = o.perm;
            Mqh.SendMessage(_threadOpen, msg, extra, hasAtt ? AttachmentInputs(sent) : null, Bind<JToken>(r =>
            {
                if (r == null) { Toast("Not sent: " + Mqh.LastError, "error"); return; }
                _thComposer.Set("");
                if (sent != null) { foreach (var a in sent) att.Remove(a); RefreshAttachRow(); }
                Toast((string)r["delivery"] == "queued" ? "Queued (the agent is busy)" : "Sent", "ok");
                _thTimeline?.ScrollToBottom(); // your message: the view follows the answer
                _nextThreadMeta = 0f;
                PollThread();
            }));
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
            // no agent yet: the list says so and GET AN AGENT opens CONTROL > AGENTS, where GET adds one
            var provRect = a.Rest();
            provRect.height -= t.ButtonHeight + t.Gap;
            _ntProviders = p.Add(new UiList(0.032f, (item, idx) => { _ntProvider = item.Tag as string; LoadModels(); }) { Rect = provRect, EmptyText = "No agent yet.\nGET AN AGENT below: CONTROL › AGENTS adds one, or turns on the local coder." });
            _ntGetAgent = p.Add(new UiButton("GET AN AGENT ▸", OpenGetAgent, UiButtonStyle.Primary) { Rect = new Rect(provRect.x, provRect.yMax + t.Gap, provRect.width, t.ButtonHeight) });
            _ntGetAgent.SetVisible(false);
            var b = new UiStack(new Rect(c.x + colW + t.Gap * 2, c.y, colW, c.height), t.Gap);
            p.Add(new UiLabel("MODEL", t.SmallSize) { Rect = b.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _ntModelList = p.Add(new UiList(0.032f, (item, idx) => { _ntModel = item.Tag as string; ShowNewThreadSummary(); }) { Rect = b.Rest(), EmptyText = "Pick a provider: its models show here." });
            var r = new UiStack(new Rect(c.x + (colW + t.Gap * 2) * 2, c.y, c.width - (colW + t.Gap * 2) * 2, c.height), t.Gap);
            _ntSummary = p.Add(new UiLabel("", t.SmallSize) { Rect = r.Next(0.07f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            int perm = Math.Max(0, Array.IndexOf(new[] { "accept-edits", "auto", "full" }, Settings.permissionMode));
            _ntPerm = p.Add(new UiSegmented("PERMISSIONS", new[] { "EDITS", "AUTO", "FULL" }, perm, i => { Settings.permissionMode = new[] { "accept-edits", "auto", "full" }[i]; ShowNewThreadSummary(); }) { Rect = r.Next(0.032f), LabelWidth = 0.1f });
            _ntWork = p.Add(new UiSegmented("WORKSPACE", new[] { "CHECKOUT", "WORKTREE" }, Settings.workspaceMode == "worktree" ? 1 : 0, i => { Settings.workspaceMode = i == 1 ? "worktree" : "local"; _ntForcedWorktree = false; _ntReuseEnv = default; ShowNewThreadSummary(); }) { Rect = r.Next(0.032f), LabelWidth = 0.1f });
            p.Closed += _ => { _ntReuseEnv = default; _ntForcedWorktree = false; _ntSeed = null; };
            p.Add(new UiLabel("CHECKOUT works in the project folder itself; WORKTREE gives the thread its own git worktree and branch. EDITS asks before commands; FULL never asks. START opens the thread: your first message there (typed or ◉ said) starts it and names it; a thread closed before that is not kept.", t.SmallSize) { Rect = r.Next(0.112f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            var go = r.Next(t.ButtonHeight);
            p.Add(new UiButton("START ▶", StartThread, UiButtonStyle.Primary) { Rect = go });
        }

        private UiButton _ntGetAgent;

        /// No agent to start a thread with: CONTROL > AGENTS, where GET installs one (its key or sign-in is the user's).
        private void OpenGetAgent()
        {
            Toast("No agent yet: GET adds one here", "info");
            OpenControl("agents");
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
                    _ntGetAgent?.SetVisible(list.Count == 0);
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
                // the local coder (docs/local-coder.md: "local-coder", OpenCode "metacodex/local-coder") is the first row, set apart
                bool IsLocal(JToken x) { string mid = (string)x["id"] ?? ""; return mid == "local-coder" || mid.EndsWith("/local-coder", StringComparison.Ordinal); }
                var all = m?["models"] as JArray ?? new JArray();
                _ntModels = new JArray(all.Where(IsLocal).Concat(all.Where(x => !IsLocal(x))).Select(x => x.DeepClone()));
                // never fall back to the slow local model: MQH's default, else the first other one
                if (!_ntModels.Any(x => (string)x["id"] == _ntModel)) _ntModel = _ntModels.Any(x => (string)x["id"] == Settings.defaultModel) ? Settings.defaultModel
                    : (string)(_ntModels.FirstOrDefault(x => x["isDefault"]?.Type == JTokenType.Boolean && (bool)x["isDefault"] && !IsLocal(x)) ?? _ntModels.FirstOrDefault(x => !IsLocal(x)) ?? _ntModels.FirstOrDefault())?["id"];
                var t = UiTheme.Current;
                _ntModelList.SetItems(_ntModels.Select(x => IsLocal(x)
                    ? new UiListItem
                    {
                        Icon = "⌂", IconColor = t.Accent, TitleColor = t.Accent,
                        Title = (string)x["displayName"] ?? (string)x["id"],
                        Sub = "on this headset · offline · slow",
                        Badge = "LOCAL", BadgeColor = t.Accent,
                        Tag = (string)x["id"],
                    }
                    : new UiListItem
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

        /// The create-thread request MQH expects (also used by tests), with NEW THREAD's choices as they are now.
        public JObject NewThreadBody(string projectId, string providerId, string model, string prompt) =>
            NewThreadBody(projectId, providerId, model, prompt, Settings.workspaceMode == "worktree" || _ntForcedWorktree, _ntReuseEnv.id, Settings.permissionMode);

        /// The create-thread request: worktree = a new managed worktree (else the checkout); reuseEnvId = that workspace
        /// (NEW THREAD HERE). MQH names the thread from the prompt (its titleFallback, then a generated title).
        public JObject NewThreadBody(string projectId, string providerId, string model, string prompt, bool worktree, string reuseEnvId, string permissionMode)
        {
            var workspace = worktree
                ? new JObject { ["type"] = "managed-worktree", ["baseBranch"] = new JObject { ["kind"] = "default" } }
                : new JObject { ["type"] = "unmanaged", ["path"] = null };
            var body = new JObject
            {
                ["projectId"] = projectId,
                ["providerId"] = providerId,
                ["origin"] = "app",
                ["input"] = new JArray { MqhClient.TextInput(prompt) },
                ["permissionMode"] = permissionMode ?? Settings.permissionMode,
                // NEW THREAD HERE (WORKSPACES): that workspace again; else the checkout or a new worktree on this host
                ["environment"] = reuseEnvId != null ? new JObject { ["type"] = "reuse", ["environmentId"] = reuseEnvId }
                    : new JObject { ["type"] = "host", ["hostId"] = _hostId, ["workspace"] = workspace },
            };
            if (!string.IsNullOrEmpty(model)) body["model"] = model;
            return body;
        }

        // ---- the draft: THREAD before its first message (owner 2026-11-13: no empty threads are kept)

        /// What NEW THREAD chose, held until the first message makes the thread in MQH.
        private sealed class ThreadDraft
        {
            public string ProjectId, ProjectName, ProjectPath, Provider, Model, Perm, ReuseEnvId, ReuseEnvLabel;
            public bool Worktree, Sending;
        }

        private string _ntSeed; // a first message prepared for the draft's composer (NEW SKILL, ASK AN AGENT)
        private string DraftKey => "draft:" + _tw.Id; // the window's draft: its attachments and chip choices until it is a thread
        /// The open thread, or the draft (its attachments and chips); null when neither.
        private string ThreadKey => _threadOpen ?? (_draft != null ? DraftKey : null);
        public bool DraftOpenForTest => _draft != null && _threadOpen == null;
        public string ThreadTitleForTest => _thTitle?.Text ?? "";

        /// START: THREAD opens as a draft with NEW THREAD's choices; nothing is made in MQH until the first message.
        private void StartThread()
        {
            if (_projectSel == null) { Toast("Pick or add a project in THREADS first", "warn"); OpenPanel("threads"); return; }
            if (_ntProvider == null)
            {
                if (_ntProviders != null && _ntProviders.Items.Count == 0) { OpenGetAgent(); return; }
                Toast("Pick a provider", "warn");
                return;
            }
            var proj = _projects.FirstOrDefault(x => (string)x["id"] == _projectSel);
            var d = new ThreadDraft
            {
                ProjectId = _projectSel,
                ProjectName = (string)proj?["name"] ?? "project",
                ProjectPath = (string)proj?["sources"]?.FirstOrDefault()?["path"],
                Provider = _ntProvider,
                Model = _ntModel,
                Perm = Settings.permissionMode,
                Worktree = Settings.workspaceMode == "worktree" || _ntForcedWorktree,
                ReuseEnvId = _ntReuseEnv.id,
                ReuseEnvLabel = _ntReuseEnv.label,
            };
            string seed = _ntSeed;
            Settings.defaultProvider = _ntProvider;
            Settings.defaultModel = _ntModel ?? "";
            OnSettingsChanged();
            ClosePanel("thread-new"); // its choices are in the draft now
            OpenDraft(d, seed);
        }

        /// THREAD as a draft: the title says so, the chips show the choices, the composer waits for the first message.
        private void OpenDraft(ThreadDraft d, string text, bool focus = true)
        {
            var w = PickWindowForOpen();
            _twOpener = _twFocus != w && WinOpen(_twFocus) ? (_twFocus.Group?.Panel ?? _twFocus.Panel) : null;
            FocusWindow(w);
            using (In(w)) OpenDraftHere(d, text, focus);
            _twOpener = null;
            RefreshWindowTitles();
            AutoGroup(w);
        }

        private void OpenDraftHere(ThreadDraft d, string text, bool focus)
        {
            DiscardDraft();
            CloseAttachModal();
            SetAttachMenu(false);
            _draft = d;
            _threadOpen = null;
            _threadOpenProject = d.ProjectId;
            _threadInfo = new JObject { ["providerId"] = d.Provider, ["projectId"] = d.ProjectId, ["status"] = "draft" };
            _thExec = new JObject { ["model"] = d.Model, ["permissionMode"] = d.Perm };
            _thUsage = null;
            _tlRaw = new JArray();
            _tlMaxSeq = -1;
            _tlOpenSeq = -1;
            ResetThreadChat();
            if (_sbOwner == _tw && _sb != null && _sb.IsOpen) _sb.Close();
            ShowThreadWindow();
            _thContext?.Set(-1f, "context —");
            ShowDraftHeader();
            UpdateChips();
            RefreshAttachRow();
            RefreshUsageLimits(true);
            RenderThreadGroups();
            _thComposer?.Set(text ?? "");
            // the composer focused: the keyboard opens on it (not in the Editor's self-tests, whose keyboard answers at once)
            if (focus && _thComposer != null && SystemKeyboard.Supported && UiKeyboard.EditorProvider == null) _thComposer.OnClick(default);
        }

        private void ShowDraftHeader()
        {
            UpdateThreadActions();
            var d = _draft;
            if (d == null || _thTitle == null) return;
            var t = UiTheme.Current;
            _thTitle.Set($"<b>{Esc(ProjectKinds.DraftTitle(d.ProjectName))}</b>  <color=#{UiViz.Hex(t.TextDim)}>NOT STARTED</color>");
            string where = d.ReuseEnvId != null ? "in " + d.ReuseEnvLabel : d.Worktree ? "a new worktree" : "the checkout";
            _thMeta?.Set($"{d.Provider} · {d.ProjectName} · {where} · your first message starts it");
            _thWsChip?.SetText("▣ " + Cut(d.ProjectName, 12) + " · " + (d.ReuseEnvId != null ? "here" : d.Worktree ? "worktree" : "checkout"));
            ShowLimits();
        }

        /// Drops the draft (closed, another thread opened, a new draft): nothing was made, nothing is kept.
        private void DiscardDraft()
        {
            if (_draft == null) return;
            _draft = null;
            _attach.Remove(DraftKey);
            _thOverrides.Remove(DraftKey);
            if (_threadOpen == null) { _threadInfo = null; _thExec = null; _thComposer?.Set(""); }
        }

        /// The draft's first message: the thread is made in MQH with it (and its attachments) as the first prompt, then
        /// THREAD shows that thread. MQH names it from the prompt.
        private void SendDraft(string msg, List<Attachment> sent, List<Attachment> att)
        {
            var d = _draft;
            if (d == null) return;
            if (d.Sending) { Toast("Starting the thread …", "info"); return; }
            d.Sending = true;
            var o = Override(DraftKey);
            Toast("Starting the thread …", "info");
            EnsureHost(Bind(() =>
            {
                var body = NewThreadBody(d.ProjectId, d.Provider, o.model ?? d.Model, msg, d.Worktree, d.ReuseEnvId, o.perm ?? d.Perm);
                if (o.effort != null) body["reasoningLevel"] = o.effort;
                if (sent != null) foreach (var a in AttachmentInputs(sent)) ((JArray)body["input"]).Add(a);
                Mqh.CreateThread(body, Bind<JToken>(r =>
                {
                    d.Sending = false;
                    if (r == null)
                    {
                        Toast("Thread not started: " + Mqh.LastError, "error");
                        if (_draft == d && _thComposer != null && string.IsNullOrWhiteSpace(_thComposer.Text)) _thComposer.Set(msg); // to send again
                        return;
                    }
                    string id = (string)r["id"];
                    bool still = _draft == d;
                    if (still)
                    {
                        // the draft's chip choices and the attachments it did not send go on with the thread
                        if (sent != null) foreach (var a in sent) att.Remove(a);
                        if (_thOverrides.TryGetValue(DraftKey, out var ov)) _thOverrides[id] = ov;
                        if (att.Count > 0) _attach[id] = att.ToList();
                        _thComposer?.Set("");
                    }
                    RefreshThreadList();
                    if (still) OpenThreadIn(_tw, id); // in the draft's own window (drops the draft)
                    else Toast("Thread started: " + Cut(msg, 40), "ok");
                }));
            }));
        }

        /// Voice, a gesture or another panel: a thread in the current project with the last agent and model chosen.
        /// With a task it is made at once (the task is its first message); without one THREAD opens as a draft. No
        /// agent chosen yet: NEW THREAD opens (the task waits in the draft's composer).
        public string NewThreadFromVoice(string task)
        {
            task = (task ?? "").Trim();
            if (_projectSel == null) { OpenPanel("threads"); return "error: add or pick a project first (THREADS > + PROJECT…)"; }
            string provider = string.IsNullOrEmpty(Settings.defaultProvider) ? null : Settings.defaultProvider;
            if (provider == null)
            {
                OpenNewThread(null);
                if (task.Length > 0) _ntSeed = task;
                return "Pick the agent and START";
            }
            var proj = _projects.FirstOrDefault(x => (string)x["id"] == _projectSel);
            _spaces = WorkspaceMap.Build(_threads, _environments);
            var d = new ThreadDraft
            {
                ProjectId = _projectSel,
                ProjectName = (string)proj?["name"] ?? "project",
                ProjectPath = (string)proj?["sources"]?.FirstOrDefault()?["path"],
                Provider = provider,
                Model = string.IsNullOrEmpty(Settings.defaultModel) ? null : Settings.defaultModel,
                Perm = Settings.permissionMode,
                // a checkout another thread works in: a worktree for this one (as NEW THREAD chooses)
                Worktree = Settings.workspaceMode == "worktree" || WorkspaceMap.CheckoutBusy(_spaces, _projectSel).Count > 0,
            };
            OpenDraft(d, null, focus: task.Length == 0);
            if (task.Length == 0) return "New thread in " + d.ProjectName + ": say or type the first message";
            SendDraft(task, null, new List<Attachment>());
            return "Starting a thread in " + d.ProjectName + ": " + Cut(task, 50);
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

    /// Where MetaCodex makes projects and what type each is (owner 2026-11-13): /root/metacodex-projects/<type>/<folder>,
    /// type native (a Quest app), default (any coding), research (notes and findings) or plugins (owner 2026-11-16: a
    /// plugin of this app, in plugins/<apps|gestures|behaviours>/<id>, its kind fixed: PluginProject). Pure: the
    /// self-tests check it.
    public static class ProjectKinds
    {
        public const string Root = "/root/metacodex-projects";
        public const string Plugins = "plugins";
        /// The types in THREADS' order (and the NEW PROJECT dialog's).
        public static readonly string[] Types = { "native", "default", "research", Plugins };

        public static string Header(string type) => type == "native" ? "NATIVE" : type == "default" ? "DEFAULT" : type == "research" ? "RESEARCH" : type == Plugins ? "PLUGINS" : "OTHER";
        public static string Label(string type) => type == "native" ? "Native app" : type == "default" ? "Default" : type == "research" ? "Research" : type == Plugins ? "Plugin" : "Other";

        /// A project name as a folder: lower case, spaces -> '-', only a-z 0-9 . _ - (the rest dropped), no leading dot;
        /// "" when nothing is left.
        public static string FolderName(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "").Trim().ToLowerInvariant())
            {
                if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '.' || ch == '_' || ch == '-') sb.Append(ch);
                else if (char.IsWhiteSpace(ch)) sb.Append('-');
            }
            string s = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-', '.');
            return s.Length > 64 ? s.Substring(0, 64).TrimEnd('-', '.') : s;
        }

        /// root/<type>/<folder>; a plugin: root/plugins/<kind folder>/<id> (kind app, gesture or behaviour); null for an
        /// unknown type or kind, or a name with no letters or digits.
        public static string PathFor(string type, string name, string kind = null)
        {
            if (type == Plugins) return PluginProject.LinuxDirOf(PluginProject.NormalizeKind(kind), PluginProject.IdFor(name));
            string f = FolderName(name);
            return f.Length == 0 || Array.IndexOf(Types, type) < 0 ? null : Root + "/" + type + "/" + f;
        }

        /// The type from where the folder is (root/<type>/<name>[/...]; plugins: root/plugins/<kind folder>/<id>); null
        /// outside the root.
        public static string TypeFromPath(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith(Root + "/", StringComparison.Ordinal)) return null;
            var rest = linuxPath.Substring(Root.Length + 1).Split('/');
            if (rest.Length > 0 && rest[0] == Plugins) return PluginProject.KindFromLinuxPath(linuxPath) != null ? Plugins : null;
            return rest.Length >= 2 && rest[1].Length > 0 && Array.IndexOf(Types, rest[0]) >= 0 ? rest[0] : null;
        }

        /// The type in .metacodex/project.json (mcx-project); "general" is the old name of default; null when none.
        public static string TypeFromJson(string json)
        {
            var m = Regex.Match(json ?? "", "\"type\"\\s*:\\s*\"([a-z]+)\"");
            if (!m.Success) return null;
            string t = m.Groups[1].Value == "general" ? "default" : m.Groups[1].Value;
            return Array.IndexOf(Types, t) >= 0 ? t : null;
        }

        /// A project's folder as THREADS shows it: …/<type>/<folder> under the root, ~/... in /root.
        public static string ShortPath(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath)) return "";
            if (linuxPath.StartsWith(Root + "/", StringComparison.Ordinal)) return "…/" + linuxPath.Substring(Root.Length + 1);
            return linuxPath.StartsWith("/root/", StringComparison.Ordinal) ? "~/" + linuxPath.Substring(6) : linuxPath;
        }

        /// THREAD's title for a draft (before its first message).
        public static string DraftTitle(string projectName) =>
            "New thread · " + (string.IsNullOrWhiteSpace(projectName) ? "no project" : projectName.Trim());
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
