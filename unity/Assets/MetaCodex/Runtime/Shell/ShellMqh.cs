using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.Scripting;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// MQH: one panel for what extends the agents and the app (owner 2026-11-09: "a compact area, not so many panels"):
    /// SKILLS (the skill layer MQH puts over every ACP provider: Claude Code, Codex, opencode), PLUGINS (MQH's
    /// plugins and the app's gesture / behaviour plugins), SCRIPTS (Lua behaviours), GESTURES (the studio) and STATUS
    /// (the link to MQH, from Settings; its id is still daemon). The old ids open their tab: skills, plugins, scripts,
    /// gestures, daemon.
    /// Skills that came with MetaCodex or MQH (how to write skills, plugins, scripts, native apps, the context) are
    /// read-only: skills are only added (NEW SKILL: an agent writes one; FIND / INSTALL: from the registry), and only
    /// those can be removed. Skills are for the ACP agents only: none of this goes to the local AI model.
    public sealed partial class MetaCodexShell
    {
        public const int MqhSkillsTab = 0, MqhPluginsTab = 1, MqhScriptsTab = 2, MqhGesturesTab = 3, MqhDaemonTab = 4;
        private UiTabs _mqhTabs;
        public UiTabs MqhTabsForTest => _mqhTabs;
        public UiTabs GestureTabsForTest => _gTabs;

        private void BuildMqh()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("mqh", "MQH · SKILLS · PLUGINS · SCRIPTS · GESTURES", ControlPageW, ControlPageH); // CONTROL's one size
            var c = p.Content;
            _mqhTabs = p.Add(new UiTabs("SKILLS", "PLUGINS", "SCRIPTS", "GESTURES", "STATUS") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);
            BuildSkillsTab(area);
            BuildPluginsTab(area);
            BuildScriptsTab(_mqhTabs, MqhScriptsTab, area);
            BuildGesturesTab(_mqhTabs, MqhGesturesTab, area);
            BuildDaemonTab(area);
            _mqhTabs.OnChanged = RefreshMqhTab;
            _mqhTabs.Select(MqhSkillsTab);
        }

        /// Fills the tab that is shown (on open and on every tab change).
        private void RefreshMqhTab(int tab)
        {
            if (!_panels.TryGetValue("mqh", out var p) || !p.IsOpen) return;
            switch (tab)
            {
                case MqhSkillsTab: if (_skillsRegistry) ShowSkillRows(); else RefreshSkills(); break;
                case MqhPluginsTab: RefreshMqhPlugins(); break;
                case MqhScriptsTab: RefreshScriptsPanel(); break;
                case MqhGesturesTab: RefreshGesturesPanel(); break;
                case MqhDaemonTab: RefreshDaemon(); break;
            }
        }

        // ------------------------------------------------------------------ SKILLS

        /// MetaCodex's own skills (mqh/plugins/metacodex-guide/skills, also laid into /root/.claude/skills): always read-only.
        public static readonly string[] OwnSkills = { "metacodex-context", "quest-scripting", "quest-gesture-plugins", "quest-views", "skill-creator", "worktree-per-thread" };

        private sealed class SkillRow
        {
            public string Name, Description, Group, Where;
            public bool Locked;
            public readonly List<JObject> Entries = new List<JObject>();
        }

        private readonly List<SkillRow> _skillRows = new List<SkillRow>();
        private JArray _skillResults = new JArray();
        private bool _skillsRegistry;
        private string _skillsQuery = "";
        private UiList _skillList;
        private UiLabel _skillDetail, _skillStatus;
        private HashSet<string> _shippedSkills;
        /// Bumped by every refresh: an answer that comes back after a newer one started is dropped.
        private int _skillsGen;

        public bool SkillsRegistryForTest => _skillsRegistry;
        public IReadOnlyList<(string name, string group, bool locked)> SkillRowsForTest => _skillRows.Select(r => (r.Name, r.Group, r.Locked)).ToList();

        /// Every skill MetaCodex ships (its own and the Unity / Meta ones bundled into /root/.claude/skills): the overlay
        /// lays them again at each update, so they are read-only here. The list comes with the overlay
        /// (/opt/metacodex/shipped-skills.txt); in the Editor, from the repo.
        private HashSet<string> ShippedSkills
        {
            get
            {
                if (_shippedSkills != null) return _shippedSkills;
                _shippedSkills = new HashSet<string>(OwnSkills, StringComparer.OrdinalIgnoreCase);
                try
                {
                    string f = app?.Host != null && !string.IsNullOrEmpty(app.Host.RootfsDir) ? app.Host.RootfsDir + "/opt/metacodex/shipped-skills.txt" : null;
                    if (f != null && File.Exists(f))
                        foreach (var line in File.ReadAllLines(f)) { var n = line.Trim(); if (n.Length > 0 && !n.StartsWith("#")) _shippedSkills.Add(n); }
                    else if (Application.isEditor)
                    {
                        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
                        foreach (var dir in new[] { "userland/agent-skills/unity", "userland/agent-skills/meta", "mqh/plugins/metacodex-guide/skills" })
                        {
                            var d = Path.Combine(repo, dir);
                            if (Directory.Exists(d)) foreach (var s in Directory.GetDirectories(d)) _shippedSkills.Add(Path.GetFileName(s));
                        }
                    }
                }
                catch (Exception ex) { Log("shipped skills: " + ex.Message); }
                return _shippedSkills;
            }
        }

        /// Tests: the names that count as shipped.
        public void SetShippedSkillsForTest(IEnumerable<string> names) => _shippedSkills = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        private void BuildSkillsTab(Rect area)
        {
            var t = UiTheme.Current;
            const int page = MqhSkillsTab;
            float leftW = 0.36f; // the buttons on the right keep their width in CONTROL's one size
            var left = new UiStack(new Rect(area.x, area.y, leftW, area.height), t.Gap);
            _skillStatus = _mqhTabs.AddTo(page, new UiLabel("", t.SmallSize) { Rect = left.Next(0.022f), Color = t.TextDim });
            _skillList = _mqhTabs.AddTo(page, new UiList(0.032f, (item, idx) => ShowSkillDetail()) { Rect = left.Rest() });
            var rs = new UiStack(new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height), t.Gap);
            _skillDetail = _mqhTabs.AddTo(page, new UiLabel("", t.SmallSize) { Rect = rs.Next(area.height - t.ButtonHeight * 2 - t.Gap * 3), Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            AddRow(_mqhTabs, page, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW SKILL…", NewSkill),
                ("⌕ FIND…", () => UiKeyboard.Edit(_skillsQuery, false, null, q => { if (q != null) FindSkills(q); })),
                ("⇣ INSTALL", InstallSelectedSkill),
            }, primary: 0);
            AddRow(_mqhTabs, page, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("◂ INSTALLED", () => { _skillsRegistry = false; RefreshSkills(); }),
                ("⟳ REFRESH", () => { if (_skillsRegistry) FindSkills(_skillsQuery); else RefreshSkills(); }),
                ("REMOVE", RemoveSelectedSkill),
            }, danger: 2);
        }

        /// The project MQH lists skills for (its own and every agent's): the one THREADS uses, else the last or the first.
        private void WithSkillsProject(Action<string> then)
        {
            if (_projectSel != null) { then(_projectSel); return; }
            Mqh.Projects(pr =>
            {
                var arr = pr as JArray ?? new JArray();
                string id = arr.Any(x => (string)x["id"] == Settings.lastProject) ? Settings.lastProject : (string)arr.FirstOrDefault()?["id"];
                then(id);
            });
        }

        public void RefreshSkills()
        {
            if (_skillList == null) return;
            var t = UiTheme.Current;
            _skillsRegistry = false;
            _skillStatus.Set("asking MQH …", t.TextDim);
            int gen = ++_skillsGen;
            WithSkillsProject(pid =>
            {
                if (gen != _skillsGen) return;
                if (pid == null)
                {
                    _skillStatus.Set(Mqh.Online ? "No project yet: add one in THREADS (+ PROJECT…)" : "⚠ " + (Mqh.LastError ?? "MQH not reachable: see MQH > STATUS"), Mqh.Online ? t.TextDim : t.Danger);
                    _skillRows.Clear();
                    ShowSkillRows();
                    return;
                }
                Mqh.Skills(pid, r =>
                {
                    if (gen != _skillsGen) return;
                    if (r == null) { _skillStatus.Set("⚠ " + (Mqh.LastError ?? "MQH not reachable: see MQH > STATUS"), t.Danger); return; }
                    ApplySkills(r);
                });
            });
        }

        /// Tests: a skill list as MQH returns it (answers still on their way are dropped).
        public void SetSkillsFromMqh(JToken r) { ++_skillsGen; _skillsRegistry = false; ApplySkills(r); }

        /// MQH's skill list → one row per name (the same skill can sit in several layers), grouped, locked or yours.
        private void ApplySkills(JToken r)
        {
            var shipped = ShippedSkills;
            _skillRows.Clear();
            foreach (var g in ((r?["skills"] as JArray) ?? new JArray()).OfType<JObject>().GroupBy(x => (string)x["name"] ?? "?", StringComparer.OrdinalIgnoreCase))
            {
                var row = new SkillRow { Name = g.Key };
                row.Entries.AddRange(g);
                row.Description = g.Select(x => (string)x["description"]).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";
                bool isShipped = shipped.Contains(g.Key);
                bool mqhOwn = g.Any(x => (string)x["scope"] == "bb-builtin" || (string)x["scope"] == "plugin");
                // read-only: shipped by MetaCodex, MQH's own or a plugin's, or anything MQH says it cannot manage
                row.Locked = isShipped || mqhOwn || g.All(x => x["manageable"]?.Value<bool>() != true);
                row.Group = !row.Locked ? "YOURS" : isShipped ? "METACODEX" : mqhOwn ? "MQH" : "AGENTS' OWN";
                row.Where = string.Join(" · ", g.Select(x => ScopeLabel(x)).Distinct());
                _skillRows.Add(row);
            }
            string[] order = { "YOURS", "METACODEX", "MQH", "AGENTS' OWN" };
            _skillRows.Sort((a, b) => a.Group == b.Group ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) : Array.IndexOf(order, a.Group).CompareTo(Array.IndexOf(order, b.Group)));
            ShowSkillRows();
        }

        private static string ScopeLabel(JObject s)
        {
            string prov = ProviderLabel((string)s["provider"]);
            switch ((string)s["scope"])
            {
                case "bb-builtin": return "MQH built-in";
                case "plugin": return "plugin " + ((string)s["pluginId"] ?? "");
                case "bb-user": return "MQH (every agent)";
                case "bb-project": return "MQH, this project";
                case "provider-user": return prov;
                case "provider-project": return prov + ", this project";
                case "shared-user": return "shared (.agents)";
                case "shared-project": return "shared, this project";
                default: return (string)s["scope"] ?? "";
            }
        }

        private static string ProviderLabel(string id)
        {
            if (string.IsNullOrEmpty(id)) return "every agent";
            string k = id.ToLowerInvariant();
            return k.Contains("claude") ? "Claude Code" : k.Contains("codex") ? "Codex" : k.Contains("opencode") ? "opencode" : id;
        }

        private void ShowSkillRows()
        {
            if (_skillList == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            if (_skillsRegistry)
            {
                _skillStatus.Set($"registry: {_skillResults.Count} for “{Trim(_skillsQuery, 30)}” · pick one, ⇣ INSTALL", t.TextDim);
                foreach (var s in _skillResults.OfType<JObject>())
                    items.Add(new UiListItem
                    {
                        Icon = "⇣",
                        IconColor = t.Accent,
                        Title = (string)s["name"],
                        Sub = Trim((string)s["summary"] ?? (string)s["source"], 60),
                        Badge = $"{s["installs"]?.Value<int>() ?? 0:N0}",
                        BadgeColor = t.TextDim,
                        Tag = s,
                    });
            }
            else
            {
                int yours = _skillRows.Count(r => !r.Locked), locked = _skillRows.Count - yours;
                _skillStatus.Set($"{yours} yours · {locked} read-only (came with MetaCodex or MQH)", t.TextDim);
                string group = null;
                foreach (var r in _skillRows)
                {
                    if (r.Group != group)
                    {
                        group = r.Group;
                        int n = _skillRows.Count(x => x.Group == group);
                        items.Add(new UiListItem { Icon = "", Title = $"{group}  ({n})", TitleColor = t.TextAccent, Tag = null });
                    }
                    items.Add(new UiListItem
                    {
                        Icon = r.Locked ? "▣" : "◆",
                        IconColor = r.Locked ? t.TextDim : t.Ok,
                        Title = r.Name,
                        Sub = Trim(r.Description, 60),
                        Badge = r.Locked ? "READ-ONLY" : "YOURS",
                        BadgeColor = r.Locked ? t.TextDim : t.Ok,
                        Tag = r,
                    });
                }
                if (_skillRows.Count == 0 && Mqh.Online) items.Add(new UiListItem { Icon = "", Title = "no skills listed", Tag = null });
            }
            _skillList.SetItems(items, keepScroll: true);
            ShowSkillDetail();
        }

        private object SelectedSkillTag() =>
            _skillList != null && _skillList.SelectedIndex >= 0 && _skillList.SelectedIndex < _skillList.Items.Count ? _skillList.Items[_skillList.SelectedIndex].Tag : null;

        private void ShowSkillDetail()
        {
            if (_skillDetail == null) return;
            var t = UiTheme.Current;
            var tag = SelectedSkillTag();
            if (tag is SkillRow r)
            {
                string why = !r.Locked ? "<color=#4DFFA6>YOURS</color>: added here, so it can be removed."
                    : r.Group == "METACODEX" ? "READ-ONLY: came with MetaCodex (each update lays it again)."
                    : r.Group == "MQH" ? "READ-ONLY: comes with MQH or one of its plugins."
                    : "READ-ONLY: an agent's own skill.";
                string paths = string.Join("\n", r.Entries.Select(x => "· " + Trim((string)x["filePath"], 80)).Distinct());
                _skillDetail.Set($"<b>{r.Name}</b>\n{why}\nfor: {r.Where}\n\n{r.Description}\n\n<color=#7FA9BF>{paths}</color>", t.Text);
            }
            else if (tag is JObject s)
                _skillDetail.Set($"<b>{(string)s["name"]}</b>  from {(string)s["source"]}\n{s["installs"]?.Value<int>() ?? 0:N0} installs" +
                                 (s["stars"]?.Type == JTokenType.Integer ? $" · {s["stars"]} stars" : "") + $"\n\n{(string)s["summary"]}\n\n<color=#7FA9BF>{(string)s["url"]}</color>\n\n⇣ INSTALL adds it for every agent.", t.Text);
            else
                _skillDetail.Set("Skills teach the agents (Claude Code, Codex, opencode) how to do something here. MQH gives every agent the same layer of skills on top of its own.\n\n" +
                                 "+ NEW SKILL: an agent writes one with you (skill-creator).\n⌕ FIND: search the public registry (skills.sh), then ⇣ INSTALL.\n\n" +
                                 "The skills that came with MetaCodex and MQH (writing skills, plugins, scripts, native apps, this headset) are read-only.", t.TextDim);
        }

        /// The first message of the thread that writes a new skill (the user picks the agent and model in NEW THREAD; it waits in
        /// the draft's composer).
        public static string NewSkillPrompt(string idea) =>
            "Create a new skill for the agents in MetaCodex (Claude Code, Codex and opencode, through MQH): " + idea.Trim() + "\n\n" +
            "Follow the skill-creator skill. Save it as ~/.mqh/skills/<short-name>/SKILL.md: MQH gives user skills to every agent, " +
            "no rebuild. Read metacodex-context first (where you run, the headset's rules). Never edit the skills that came with " +
            "MetaCodex or MQH; if one needs a change, tell me. Show me the SKILL.md before you write it.";

        private void NewSkill()
        {
            UiKeyboard.Edit("", false, null, idea =>
            {
                if (string.IsNullOrWhiteSpace(idea)) return;
                OpenNewThread(null);
                _ntSeed = NewSkillPrompt(idea); // in the thread's composer after START: SEND starts it
                Toast("Pick the agent and START, then SEND the brief: it writes the skill with you", "info");
            });
        }

        public void FindSkills(string query)
        {
            var t = UiTheme.Current;
            _skillsQuery = query?.Trim() ?? "";
            _skillsRegistry = true;
            _skillStatus?.Set("searching the registry …", t.TextDim);
            int gen = ++_skillsGen;
            Mqh.SearchSkills(_skillsQuery, r =>
            {
                if (gen != _skillsGen) return;
                if (r == null) { _skillStatus?.Set("⚠ registry: " + (Mqh.LastError ?? "no answer"), t.Danger); return; }
                _skillResults = r["skills"] as JArray ?? new JArray();
                ShowSkillRows();
            });
        }

        /// Tests: registry results as MQH returns them.
        public void SetSkillResultsForTest(JToken page, string query) { ++_skillsGen; _skillsQuery = query; _skillsRegistry = true; _skillResults = page?["skills"] as JArray ?? new JArray(); ShowSkillRows(); }

        private void InstallSelectedSkill()
        {
            if (!(SelectedSkillTag() is JObject s)) { Toast(_skillsRegistry ? "Pick a skill from the results" : "⌕ FIND a skill in the registry first", "warn"); return; }
            string id = (string)s["id"], name = (string)s["name"];
            Toast($"Installing {name} …", "info");
            Mqh.InstallSkill(id, r =>
            {
                if (r == null) { Toast("Install failed: " + Mqh.LastError, "error"); return; }
                Toast($"{name} installed for every agent", "ok");
                _skillsRegistry = false;
                RefreshSkills();
            });
        }

        /// What REMOVE would do with the selected row: null when it may go, else why not (tests read it).
        public string SkillRemoveRefusal(int index)
        {
            if (_skillList == null || index < 0 || index >= _skillList.Items.Count || !(_skillList.Items[index].Tag is SkillRow r)) return "pick one of your skills";
            if (r.Locked) return r.Group == "METACODEX" ? $"{r.Name} came with MetaCodex: read-only" : $"{r.Name} is read-only";
            return null;
        }

        public void SelectSkillForTest(string name)
        {
            for (int i = 0; i < _skillList.Items.Count; i++)
                if (_skillList.Items[i].Tag is SkillRow r && r.Name == name) { _skillList.SelectIndex(i); return; }
        }

        private void RemoveSelectedSkill()
        {
            int idx = _skillList?.SelectedIndex ?? -1;
            string no = SkillRemoveRefusal(idx);
            if (no != null) { Toast(no, "warn"); return; }
            var r = (SkillRow)_skillList.Items[idx].Tag;
            Modal("REMOVE SKILL", $"Remove {r.Name}? The agents stop using it.", new[] { "REMOVE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                WithSkillsProject(pid =>
                {
                    var ids = r.Entries.Where(x => x["manageable"]?.Value<bool>() == true).Select(x => (string)x["id"]).ToList();
                    int left = ids.Count;
                    foreach (var id in ids)
                        Mqh.DeleteSkill(pid, id, res =>
                        {
                            if (res == null) Toast("Remove failed: " + Mqh.LastError, "error");
                            if (--left == 0) { Toast($"{r.Name} removed", "ok"); RefreshSkills(); }
                        });
                });
            });
        }

        // ------------------------------------------------------------------ PLUGINS

        private JArray _mqhPluginCache = new JArray();

        private void BuildPluginsTab(Rect area)
        {
            var t = UiTheme.Current;
            const int page = MqhPluginsTab;
            float leftW = 0.36f;
            _gPlugins = _mqhTabs.AddTo(page, new UiList(0.032f, (item, idx) => ShowPluginDetail()) { Rect = new Rect(area.x, area.y, leftW, area.height) });
            var pr = new UiStack(new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height), t.Gap);
            _gPluginDetail = _mqhTabs.AddTo(page, new UiLabel("", t.SmallSize) { Rect = pr.Next(area.height - t.ButtonHeight * 3 - t.Gap * 4), Wrap = true, UseMono = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            // ENABLE or DISABLE: the one that applies to the picked plugin (ShowPluginDetail)
            var pt = pr.Next(t.ButtonHeight);
            float ptw = (pt.width - t.Gap) / 2f;
            _pluginToggle = _mqhTabs.AddTo(page, new UiButton("ENABLE", TogglePickedPlugin) { Rect = new Rect(pt.x, pt.y, ptw, pt.height) });
            _mqhTabs.AddTo(page, new UiButton("⟳ RELOAD", () => { Scripts.ReloadAll(); RefreshMqhPlugins(); }) { Rect = new Rect(pt.x + ptw + t.Gap, pt.y, ptw, pt.height) });
            _pluginToggle.SetEnabled(false);
            AddRow(_mqhTabs, page, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW PLUGIN…", () => UiKeyboard.Edit("my-gestures", false, null, id => { if (string.IsNullOrWhiteSpace(id)) return; var e = NewPlugin(id); Toast($"Plugin {e.Name} created in /mnt/quest/plugins/{e.Name}", "ok"); RefreshPluginList(); })),
                ("ASK AN AGENT…", () => UiKeyboard.Edit("", false, null, idea => { if (string.IsNullOrWhiteSpace(idea)) return; OpenNewThread(null); _ntSeed = NewPluginPrompt(idea); Toast("Pick the agent and START, then SEND the brief", "info"); })),
            }, primary: 0);
            AddRow(_mqhTabs, page, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("FOLDER", () =>
                {
                    var tag = SelectedPluginTag();
                    if (tag is ScriptHost.Entry e) TerminalSend($"cd {GitService.Quote("/mnt/quest/plugins/" + e.Name)} && ls -la\r");
                    else if (tag is JObject m && !string.IsNullOrEmpty((string)m["rootDir"])) TerminalSend($"cd {GitService.Quote((string)m["rootDir"])} && ls -la\r");
                }),
                ("DELETE", DeletePickedPlugin),
            }, danger: 1);
        }

        public static string NewPluginPrompt(string idea) =>
            "Make a plugin for MetaCodex: " + idea.Trim() + "\n\n" +
            "If it reacts to hands, gestures or the app's panels, make a gesture / behaviour plugin (skill quest-gesture-plugins: a folder " +
            "in /mnt/quest/plugins, applies live). If it extends MQH itself (a server part, a spatial panel), make an MQH plugin " +
            "(`mqh plugin new` makes its folder). Read metacodex-context first. Show me the plan before you write files.";

        private object SelectedPluginTag() =>
            _gPlugins != null && _gPlugins.SelectedIndex >= 0 && _gPlugins.SelectedIndex < _gPlugins.Items.Count ? _gPlugins.Items[_gPlugins.SelectedIndex].Tag : null;

        private void RefreshMqhPlugins()
        {
            RefreshPluginList();
            Mqh.Plugins(pl =>
            {
                _mqhPluginCache = pl?["plugins"] as JArray ?? new JArray();
                RefreshPluginList();
            });
        }

        /// MQH's plugins and the app's gesture / behaviour plugins, in one list.
        public void RefreshPluginList()
        {
            if (_gPlugins == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            var mqh = _mqhPluginCache.OfType<JObject>().ToList();
            items.Add(new UiListItem { Icon = "", Title = $"MQH PLUGINS  ({mqh.Count})", TitleColor = t.TextAccent, Tag = null });
            foreach (var x in mqh.OrderBy(x => (string)x["provenance"] == "builtin").ThenBy(x => (string)x["id"]))
            {
                bool on = x["enabled"]?.Value<bool>() ?? (string)x["status"] != "disabled";
                string st = (string)x["status"] ?? (on ? "enabled" : "disabled");
                items.Add(new UiListItem
                {
                    Icon = x["spatial"]?["hasSpatial"]?.Value<bool>() == true ? "◫" : "◆",
                    IconColor = !on ? t.TextDim : st == "running" ? t.Ok : t.Warn,
                    Title = (string)x["name"] ?? (string)x["id"],
                    Sub = ((string)x["provenance"] == "builtin" ? "comes with MQH · " : "") + Trim((string)x["description"] ?? (string)x["id"], 50),
                    Badge = st.ToUpperInvariant(),
                    BadgeColor = st == "running" ? t.Ok : st == "disabled" ? t.TextDim : t.Warn,
                    Tag = x,
                });
            }
            var local = Scripts != null ? Scripts.Entries.Where(e => e.Kind == "plugin").ToList() : new List<ScriptHost.Entry>();
            items.Add(new UiListItem { Icon = "", Title = $"GESTURE AND BEHAVIOUR PLUGINS  ({local.Count})", TitleColor = t.TextAccent, Tag = null });
            foreach (var e in local)
                items.Add(new UiListItem
                {
                    Icon = "▣",
                    IconColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                    Title = e.Title.Length > 0 ? e.Title : e.Name,
                    Sub = e.Name + (e.Version.Length > 0 ? " · v" + e.Version : ""),
                    Badge = e.Error != null ? "ERROR" : !e.Enabled ? "OFF" : $"{e.Poses + e.Detectors} G",
                    BadgeColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                    Tag = e,
                });
            _gPlugins.SetItems(items, keepScroll: true);
            ShowPluginDetail();
        }

        private UiButton _pluginToggle;

        private void ShowPluginDetail()
        {
            if (_gPluginDetail == null) return;
            var t = UiTheme.Current;
            var tag = SelectedPluginTag();
            bool? on = tag is ScriptHost.Entry pe ? pe.Enabled : tag is JObject pm ? (pm["enabled"]?.Value<bool>() ?? (string)pm["status"] != "disabled") : (bool?)null;
            _pluginToggle?.SetText(on == true ? "DISABLE" : "ENABLE");
            _pluginToggle?.SetEnabled(on != null);
            if (tag is JObject m)
            {
                bool builtin = (string)m["provenance"] == "builtin";
                _gPluginDetail.Set($"<b>{(string)m["name"] ?? (string)m["id"]}</b> {(string)m["version"]}  ({(string)m["id"]})\n{(string)m["description"]}\n\n" +
                                   $"MQH plugin · {(builtin ? "comes with MQH: it can be turned off, not removed" : (string)m["sourceDisplay"])}\n" +
                                   $"status {(string)m["status"]}{((string)m["statusDetail"] != null ? ": " + (string)m["statusDetail"] : "")}\n{(string)m["rootDir"]}", t.TextDim);
                return;
            }
            if (!(tag is ScriptHost.Entry e))
            {
                _gPluginDetail.Set("MQH PLUGINS extend the harness (threads, providers, panels of their own). GESTURE AND BEHAVIOUR PLUGINS live in " +
                                   "/mnt/quest/plugins/<id>/ (plugin.json, main.lua, poses/*.json) and apply live.\n\n+ NEW PLUGIN makes an empty gesture plugin; " +
                                   "ASK AN AGENT writes one with you.", t.TextDim);
                return;
            }
            var gestures = Gestures.Custom.Where(x => x.Owner == e.Key).Select(x => x.Name).ToList();
            _gPluginDetail.Set($"<b>{e.Title}</b> {e.Version}\n{e.Description}\n\n/mnt/quest/plugins/{e.Name}\n{(e.Enabled ? "enabled" : "disabled")} · {e.Handlers} handlers · {e.Poses} poses · {e.Detectors} detectors\n" +
                               (gestures.Count > 0 ? "gestures: " + string.Join(", ", gestures) + "\n" : "") +
                               (e.Error != null ? $"<color=#FF5468>ERROR {e.Error}</color>\n" : "") + "\n" + string.Join("\n", e.Log.Skip(Math.Max(0, e.Log.Count - 5))), e.Error != null ? t.Danger : t.TextDim);
        }

        private void TogglePickedPlugin()
        {
            var tag = SelectedPluginTag();
            if (tag is ScriptHost.Entry e) { Scripts.SetEnabled(e.Key, !e.Enabled); RefreshPluginList(); return; }
            if (!(tag is JObject m)) { Toast("Pick a plugin", "warn"); return; }
            string id = (string)m["id"];
            bool on = m["enabled"]?.Value<bool>() ?? (string)m["status"] != "disabled";
            Modal("PLUGIN " + id.ToUpperInvariant(), on ? "Turn this MQH plugin off?" : "Turn this MQH plugin on?", new[] { on ? "DISABLE" : "ENABLE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                Mqh.SetPluginEnabled(id, !on, r => { Toast(r != null ? $"{id} {(on ? "disabled" : "enabled")}" : "MQH: " + Mqh.LastError, r != null ? "ok" : "error"); RefreshMqhPlugins(); });
            });
        }

        private void DeletePickedPlugin()
        {
            var tag = SelectedPluginTag();
            if (tag is JObject m)
            {
                Toast((string)m["provenance"] == "builtin" ? "Comes with MQH: turn it off instead" : $"Remove it with: mqh plugin uninstall {(string)m["id"]}", "warn");
                return;
            }
            if (!(tag is ScriptHost.Entry e)) { Toast("Pick a plugin", "warn"); return; }
            Modal("DELETE PLUGIN", $"Delete the folder of plugin {e.Name}?", new[] { "DELETE", "CANCEL" }, i => { if (i == 0) { Scripts.Delete(e.Key); RefreshPluginList(); } });
        }

        // ------------------------------------------------------------------ STATUS (the tab's id: daemon)

        private UiLabel _daemonStatus;

        private void BuildDaemonTab(Rect area)
        {
            var t = UiTheme.Current;
            var s = Settings;
            const int page = MqhDaemonTab;
            float rowH = 0.032f;
            var m = new UiStack(new Rect(area.x, area.y, Mathf.Min(area.width, 0.56f), area.height), t.Gap);
            _mqhTabs.AddTo(page, new UiLabel("MQH runs in this headset's Linux, started by the app (on the cores the frame loop does not use): the threads, the skills and the plugins of the agents (opencode, Claude Code, Codex). Its data is in ~/.mqh; a URL other than 127.0.0.1:38990 (a PC's MQH) stops the local one.", t.SmallSize) { Rect = m.Next(0.04f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _mqhTabs.AddTo(page, new UiTextField("MQH URL", s.mqhUrl, v => { s.mqhUrl = v.Trim(); OnSettingsChanged(); RefreshDaemon(); }) { Rect = m.Next(rowH) });
            // a developer's own token goes into the vault (sealed, policy deny), never into settings.json
            _mqhTabs.AddTo(page, new UiTextField("TOKEN", Vault?.Has(MqhTokenOverrideName) == true ? "••••••••" : "", v => { if (v != "••••••••") SetMqhTokenOverride(v); RefreshDaemon(); }, "from the vault") { Rect = m.Next(rowH), Secure = true });
            _mqhTabs.AddTo(page, new UiToggle("Sync these settings with MQH", s.mqhSync, v => { s.mqhSync = v; OnSettingsChanged(); }) { Rect = m.Next(rowH) });
            _daemonStatus = _mqhTabs.AddTo(page, new UiLabel("not checked", t.SmallSize) { Rect = m.Next(0.024f), Color = t.TextDim });
            var mb = m.NextSplit(t.ButtonHeight, 0.5f, out var mb2);
            _mqhTabs.AddTo(page, new UiButton("⟳ RESTART", () => { MqhHost?.Restart(); RefreshDaemon(); }, UiButtonStyle.Primary) { Rect = mb });
            _mqhTabs.AddTo(page, new UiButton("PUSH SETTINGS", () => Mqh.PushQuestSettings(Settings.ToJObject(), ok => Toast(ok ? "Settings saved to MQH" : "MQH unreachable", ok ? "ok" : "error"))) { Rect = mb2 });
        }

        /// MQH answered (MqhHost: started by the app, or already running): the panels that need it read it now.
        private void OnMqhOnline()
        {
            Mqh.BaseUrl = Settings.mqhUrl;
            Mqh.Token = MqhTokenNow();
            RefreshDaemon();
            if (IsPanelOpen("agents")) RefreshAgents();
            if (IsPanelOpen("threads")) RefreshThreads();
            PushKeysToMqh(); // keys saved while MQH was not answering
            ProbeAgents();   // which agents are installed (AGENTS and NEW THREAD list them)
            // Cursor, Pi and the rest: off in MQH from the start, not only once AGENTS is opened; again after 20 s and 60 s
            // (MQH answers /health before its plugins have registered their providers: seen on the headset, 0.7.13)
            SwitchOffUnsupportedProviders();
            After(20f, SwitchOffUnsupportedProviders);
            After(60f, SwitchOffUnsupportedProviders);
            if (!_mqhOnlineToastShown) { _mqhOnlineToastShown = true; Toast("MQH is running in this headset", "ok"); }
        }

        private void SwitchOffUnsupportedProviders() =>
            Mqh.ProvidersState(st => { if (st?["providers"] is JArray all) { _providerStates = all; DisableUnsupportedProviders(); _providerStates = SupportedOnly(all); } });

        private bool _mqhOnlineToastShown;

        /// What MqhHost is doing, in a line (STATUS, AGENTS when MQH does not answer).
        private string MqhHostLine() => MqhHost == null || MqhHost.State == MqhHost.HostState.Off ? "" : MqhHost.Status;

        public void RefreshDaemon()
        {
            if (_daemonStatus == null) return;
            var t = UiTheme.Current;
            _daemonStatus.Set((MqhHostLine().Length > 0 ? MqhHostLine() + " · " : "") + "connecting to " + Settings.mqhUrl + " …", t.TextDim);
            Mqh.BaseUrl = Settings.mqhUrl;
            Mqh.Token = MqhTokenNow();
            Mqh.Version(v =>
            {
                if (v == null) { _daemonStatus.Set("⚠ " + (MqhHostLine().Length > 0 ? MqhHostLine() : Mqh.LastError ?? "MQH not reachable"), MqhHost != null && MqhHost.State == MqhHost.HostState.Starting ? t.Warn : t.Danger); return; }
                string ver = v["version"]?.ToString() ?? v["appVersion"]?.ToString() ?? v.ToString(Newtonsoft.Json.Formatting.None);
                _daemonStatus.Set($"<color=#4DFFA6>● online</color>  MQH {Trim(ver, 60)}", t.Text);
            });
        }
    }
}
