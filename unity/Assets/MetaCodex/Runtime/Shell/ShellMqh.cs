using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.AI;
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
        public static readonly string[] OwnSkills = { "metacodex-context", "quest-scripting", "quest-gesture-plugins", "quest-plugins", "quest-views", "skill-creator", "worktree-per-thread" };

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
            }
            // nothing listed: the list itself says so and what to do (the lists' empty state), no fake row
            _skillList.SetEmptyText(_skillsRegistry ? "Nothing found for this search." :
                _skillRows.Count == 0 && Mqh.Online ? "No skills yet.\n+ NEW SKILL: an agent writes one with you. FIND: search the public registry." : "");
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

        /// Where plugin projects are (ScriptHost scans them): on the headset the Linux's /root/metacodex-projects/plugins
        /// in the app's rootfs; in the Editor (and its tests) a folder beside the scripts folder, so a test's projects stay
        /// in its own temporary folder.
        string IScriptServices.PluginProjectsDir(string scriptsDir)
        {
            if (!Application.isEditor && app?.Host != null) return AndroidPathOf(PluginProject.LinuxRoot);
            string parent = Path.GetDirectoryName((scriptsDir ?? "").TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "projects");
        }

        private JArray _mqhPluginCache = new JArray();
        /// MQH's projects as the PLUGINS tab last read them (which project is a plugin's: its folder); null: read again.
        private JArray _pluginProjects;
        private UiList _pluginThreads;
        private UiLabel _pluginThreadsHead;
        private UiButton _pluginThreadsArch, _pluginNewThread, _pluginDelete;
        private bool _pluginThreadsArchived;
        /// The project whose threads the list shows, and a counter that drops answers for an earlier pick.
        private string _pluginThreadsFor;
        private int _pluginThreadsGen;

        /// MQH > PLUGINS (owner 2026-11-16): the list by kind on the left; on the right the picked plugin's detail, its
        /// THREADS (the threads of its project, newest first; a tap opens one like THREADS does), and the buttons:
        /// ENABLE / DISABLE, ⟳ RELOAD, DELETE; + NEW PLUGIN… (the NEW PROJECT flow at the kind question, or straight to
        /// the name when SHOW is APPS, GESTURES or BEHAVIOURS) and NEW THREAD ▸ (a thread on the plugin's project).
        private void BuildPluginsTab(Rect area)
        {
            var t = UiTheme.Current;
            const int page = MqhPluginsTab;
            float leftW = 0.36f;
            // the kinds of plugin: APPS (a window, a menu tile), GESTURES (poses, detectors: the GESTURES tab is their
            // studio), BEHAVIOURS (Lua that reacts to the app), MQH (the harness's own)
            float filterH = 0.032f;
            _pluginFilter = _mqhTabs.AddTo(page, new UiSegmented("SHOW", PluginKinds, 0, i => { _pluginKind = i; RefreshPluginList(); }) { Rect = new Rect(area.x, area.y, area.width, filterH), LabelWidth = 0.07f });
            area = new Rect(area.x, area.y + filterH + t.Gap * 2, area.width, area.height - filterH - t.Gap * 2);
            _gPlugins = _mqhTabs.AddTo(page, new UiList(0.032f, (item, idx) => ShowPluginDetail()) { Rect = new Rect(area.x, area.y, leftW, area.height) });
            var pr = new UiStack(new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height), t.Gap);
            float rest = area.height - t.ButtonHeight * 2 - t.Gap * 4;
            float headH = 0.024f;
            _gPluginDetail = _mqhTabs.AddTo(page, new UiLabel("", t.SmallSize) { Rect = pr.Next(rest * 0.46f), Wrap = true, UseMono = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            // THREADS of the picked plugin's project, with a small ARCHIVED switch on the right of its title
            var hr = pr.Next(headH);
            float abw = 0.12f;
            _pluginThreadsHead = _mqhTabs.AddTo(page, new UiLabel("THREADS", t.SmallSize) { Rect = new Rect(hr.x, hr.y, hr.width - abw - t.Gap, hr.height), Color = t.TextAccent });
            _pluginThreadsArch = _mqhTabs.AddTo(page, new UiButton("▤ ARCHIVED", () => { _pluginThreadsArchived = !_pluginThreadsArchived; _pluginThreadsArch.SetSelected(_pluginThreadsArchived); _pluginThreadsFor = null; ShowPluginDetail(); }) { Rect = new Rect(hr.xMax - abw, hr.y, abw, hr.height), TextSize = t.SmallSize * 0.95f });
            _pluginThreads = _mqhTabs.AddTo(page, new UiList(0.028f, (item, idx) => PluginThreadPicked(item)) { Rect = pr.Next(rest * 0.54f - headH - t.Gap) });
            var row1 = AddRow(_mqhTabs, page, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("ENABLE", TogglePickedPlugin),
                ("⟳ RELOAD", () => { Scripts.ReloadAll(); RefreshMqhPlugins(); }),
                ("DELETE", DeletePickedPlugin),
            }, danger: 2);
            // ENABLE or DISABLE (or + ADD for an example): the one that applies to the picked row (ShowPluginDetail)
            _pluginToggle = row1[0];
            _pluginDelete = row1[2];
            _pluginToggle.SetEnabled(false);
            var row2 = AddRow(_mqhTabs, page, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW PLUGIN…", NewPluginFromTab),
                ("NEW THREAD ▸", NewThreadOnPickedPlugin),
            }, primary: 0);
            _pluginNewThread = row2[1];
        }

        /// + NEW PLUGIN…: the NEW PROJECT flow at the kind question; SHOW on APPS, GESTURES or BEHAVIOURS picks the kind.
        private void NewPluginFromTab()
        {
            string only = _pluginKind > 0 ? PluginKinds[_pluginKind] : null;
            NewPluginKindPrompt(null, only == KindApps ? "app" : only == KindGestures ? "gesture" : only == KindBehaviours ? "behaviour" : null);
        }

        private object SelectedPluginTag() =>
            _gPlugins != null && _gPlugins.SelectedIndex >= 0 && _gPlugins.SelectedIndex < _gPlugins.Items.Count ? _gPlugins.Items[_gPlugins.SelectedIndex].Tag : null;

        /// Picks a plugin's row (after NEW PLUGIN, tests); false when it is not listed.
        public bool SelectPluginForTest(string id)
        {
            if (_gPlugins == null) return false;
            for (int i = 0; i < _gPlugins.Items.Count; i++)
                if (_gPlugins.Items[i].Tag is ScriptHost.Entry e && e.Name == id) { _gPlugins.SelectIndex(i, notify: false); ShowPluginDetail(); return true; }
            return false;
        }

        private void RefreshMqhPlugins()
        {
            _pluginProjects = null; // MQH's projects again (a plugin's threads)
            _pluginThreadsFor = null;
            RefreshPluginList();
            Mqh.Plugins(pl =>
            {
                _mqhPluginCache = pl?["plugins"] as JArray ?? new JArray();
                RefreshPluginList();
            });
        }

        /// MQH's plugins and the app's plugins (projects of the three kinds, and older folders), in one list.
        public void RefreshPluginList()
        {
            if (_gPlugins == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            string only = _pluginKind > 0 ? PluginKinds[_pluginKind] : null; // ALL: every kind, one section each
            var local = Scripts != null ? Scripts.Entries.Where(e => e.Kind == "plugin").ToList() : new List<ScriptHost.Entry>();
            foreach (var kind in new[] { KindApps, KindGestures, KindBehaviours })
            {
                if (only != null && only != kind) continue;
                var mine = local.Where(e => PluginKindOf(e) == kind).ToList();
                items.Add(new UiListItem { Icon = "", Title = $"{kind}  ({mine.Count})", TitleColor = t.TextAccent, Tag = null });
                foreach (var e in mine)
                    items.Add(new UiListItem
                    {
                        Icon = kind == KindApps ? "◫" : kind == KindGestures ? "✋" : "▣",
                        IconColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                        Title = e.Title.Length > 0 ? e.Title : e.Name,
                        // the kind: the project's (its marker), fixed; an older folder says so
                        Sub = (e.Project ? e.PluginKind : "older folder") + " · " + e.Name + (e.Version.Length > 0 ? " · v" + e.Version : ""),
                        Badge = e.Error != null ? "ERROR" : !e.Enabled ? "OFF" : e.Poses + e.Detectors > 0 ? $"{e.Poses + e.Detectors} G" : "ON",
                        BadgeColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                        Tag = e,
                    });
            }
            var mqh = only == null || only == KindMqh ? _mqhPluginCache.OfType<JObject>().ToList() : new List<JObject>();
            if (only == null || only == KindMqh) items.Add(new UiListItem { Icon = "", Title = $"MQH  ({mqh.Count})", TitleColor = t.TextAccent, Tag = null });
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
            // the examples that ship in the app and are not installed yet: a tap picks one, + ADD makes it a plugin
            // project (apps first: timers, calendar; then the gesture examples)
            var examples = GestureExampleIds.Where(id => (Scripts == null || Scripts.Find("plugin/" + id) == null) && (only == null || ExampleKindOf(id) == only))
                .OrderBy(id => IsAppPluginExample(id) ? 0 : 1).ToList();
            if (examples.Count > 0) items.Add(new UiListItem { Icon = "", Title = $"EXAMPLES TO ADD  ({examples.Count})", TitleColor = t.TextAccent, Tag = null });
            foreach (var id in examples)
                items.Add(new UiListItem
                {
                    Icon = "+",
                    IconColor = t.Accent,
                    Title = id,
                    Sub = ExampleKindOf(id).ToLowerInvariant() + " · " + Trim(ExampleDescription(ExamplePlugins[id]), 40),
                    Badge = "+ ADD",
                    BadgeColor = t.Accent,
                    Tag = new GestureExampleRow { Id = id },
                });
            // nothing at all: the list says what to do
            _gPlugins.SetEmptyText(items.Count == 0 ? "No plugins yet.\n+ NEW PLUGIN makes one." : "");
            _gPlugins.SetItems(items, keepScroll: true);
            ShowPluginDetail();
        }

        private UiButton _pluginToggle;
        private UiSegmented _pluginFilter;
        private int _pluginKind;

        /// The kinds of plugin, as PLUGINS' SHOW filter lists them (0 = ALL).
        public const string KindApps = "APPS", KindGestures = "GESTURES", KindBehaviours = "BEHAVIOURS", KindMqh = "MQH";
        private static readonly string[] PluginKinds = { "ALL", KindApps, KindGestures, KindBehaviours, KindMqh };

        /// A plugin's kind: a plugin project's (its folder and marker: fixed); an older folder's plugin.json "kind" ("app",
        /// "gesture", "behaviour"), else what it does now (a window or a tile: APPS; poses or detectors: GESTURES; else
        /// BEHAVIOURS).
        public string PluginKindOf(ScriptHost.Entry e)
        {
            string kind = e.Project ? e.PluginKind : PluginProject.NormalizeKind((string)e.Manifest?["kind"]);
            if (kind == "app") return KindApps;
            if (kind == "gesture") return KindGestures;
            if (kind == "behaviour") return KindBehaviours;
            if (Scripts != null && Scripts.OwnsUi(e)) return KindApps;
            return e.Poses + e.Detectors > 0 ? KindGestures : KindBehaviours;
        }

        private static string ExampleKindOf(string id)
        {
            if (IsAppPluginExample(id)) return KindApps;
            string k = ExampleKind(id);
            return k == "app" ? KindApps : k == "behaviour" ? KindBehaviours : KindGestures;
        }

        /// An example's kind (app, gesture, behaviour) from its plugin.json; gesture when it says none.
        internal static string ExampleKind(string id)
        {
            if (IsAppPluginExample(id)) return "app";
            try { return PluginProject.NormalizeKind((string)JObject.Parse(ExamplePlugins[id]["plugin.json"])["kind"]) ?? "gesture"; }
            catch (Exception) { return "gesture"; }
        }

        /// PLUGINS with one kind shown (0 ALL, 1 APPS, 2 GESTURES, 3 BEHAVIOURS, 4 MQH): voice, tests.
        public void ShowPluginKind(int i)
        {
            _pluginKind = Mathf.Clamp(i, 0, PluginKinds.Length - 1);
            _pluginFilter?.Set(_pluginKind);
            RefreshPluginList();
        }

        private void ShowPluginDetail()
        {
            if (_gPluginDetail == null) return;
            var t = UiTheme.Current;
            var tag = SelectedPluginTag();
            bool? on = tag is ScriptHost.Entry pe ? pe.Enabled : tag is JObject pm ? (pm["enabled"]?.Value<bool>() ?? (string)pm["status"] != "disabled") : (bool?)null;
            _pluginNewThread?.SetEnabled(tag is ScriptHost.Entry pp && pp.Project);
            _pluginDelete?.SetEnabled(tag is ScriptHost.Entry || tag is JObject);
            if (tag is GestureExampleRow ex)
            {
                _pluginToggle?.SetText("+ ADD");
                _pluginToggle?.SetEnabled(true);
                string k = ExampleKind(ex.Id);
                _gPluginDetail.Set($"<b>{ex.Id}</b> · example ({k})\n{ExampleDescription(ExamplePlugins[ex.Id])}\n\n" +
                                   (k == "app" ? "An app: its own window and a tile on the main menu. " : "A gesture plugin: named gestures (the GESTURES tab is their studio). ") +
                                   "+ ADD makes it a plugin project in …/plugins/" + PluginProject.FolderOf(k) + "/" + ex.Id + "/ (with git) and runs it at once; " +
                                   "a thread on its project lets an agent change it. DELETE removes it again.", t.TextDim);
                ShowPluginThreads(null, "Not added yet: its threads show here once + ADD made its project.");
                return;
            }
            _pluginToggle?.SetText(on == true ? "DISABLE" : "ENABLE");
            _pluginToggle?.SetEnabled(on != null);
            if (tag is JObject m)
            {
                bool builtin = (string)m["provenance"] == "builtin";
                _gPluginDetail.Set($"<b>{(string)m["name"] ?? (string)m["id"]}</b> {(string)m["version"]}  ({(string)m["id"]})\n{(string)m["description"]}\n\n" +
                                   $"MQH plugin · {(builtin ? "comes with MQH: it can be turned off, not removed" : (string)m["sourceDisplay"])}\n" +
                                   $"status {(string)m["status"]}{((string)m["statusDetail"] != null ? ": " + (string)m["statusDetail"] : "")}\n{(string)m["rootDir"]}", t.TextDim);
                ShowPluginThreads(null, "A plugin of MQH itself: no threads here.");
                return;
            }
            if (!(tag is ScriptHost.Entry e))
            {
                _gPluginDetail.Set("A plugin is a project of type PLUGINS with a fixed kind:\n" +
                                   "APP: a window and a tile on the main menu (Timers, Calendar).\n" +
                                   "GESTURE: named hand gestures and what they do (GESTURES tab: the studio).\n" +
                                   "BEHAVIOUR: Lua that reacts to your hands, voice or time.\n" +
                                   "MQH: plugins of the agent harness.\n\n" +
                                   "+ NEW PLUGIN makes one (also THREADS > + PROJECT > PLUGIN). Pick one: its threads show below; NEW THREAD ▸ starts an agent on it.", t.TextDim);
                ShowPluginThreads(null, "");
                return;
            }
            var gestures = Gestures.Custom.Where(x => x.Owner == e.Key).Select(x => x.Name).ToList();
            string where = e.Project ? $"{PluginProject.Label(e.PluginKind).ToLowerInvariant()} plugin (kind fixed) · {ProjectKinds.ShortPath(e.LinuxDir)}"
                                     : $"an older plugin folder: {e.LinuxDir} (no project)";
            _gPluginDetail.Set($"<b>{e.Title}</b> {e.Version}\n{e.Description}\n\n{where}\n{(e.Enabled ? "enabled" : "disabled")} · {e.Handlers} handlers · {e.Poses} poses · {e.Detectors} detectors\n" +
                               (gestures.Count > 0 ? "gestures: " + string.Join(", ", gestures) + "\n" : "") +
                               (e.Error != null ? $"<color=#FF5468>ERROR {e.Error}</color>\n" : "") + string.Join("\n", e.Log.Skip(Math.Max(0, e.Log.Count - 3))), e.Error != null ? t.Danger : t.TextDim);
            if (!e.Project) { ShowPluginThreads(null, "An older plugin folder: no project, so no threads. + NEW PLUGIN makes a project an agent can work in."); return; }
            ShowPluginThreads(e, null);
        }

        // ---- the picked plugin's threads (its MQH project's): read when a plugin is picked, ARCHIVED flips, the tab
        // refreshes or a thread is made from here; never per frame

        /// The MQH project of a plugin project (its folder is the project's source), from the cache; null when none (yet).
        private string PluginProjectId(ScriptHost.Entry e) =>
            e == null || _pluginProjects == null ? null
                : (string)_pluginProjects.FirstOrDefault(pr => (pr["sources"] as JArray)?.Any(src => (string)src["path"] == e.LinuxDir) == true)?["id"];

        private void ShowPluginThreads(ScriptHost.Entry e, string emptyText)
        {
            if (_pluginThreads == null) return;
            var t = UiTheme.Current;
            _pluginThreadsArch?.SetEnabled(e != null);
            if (e == null)
            {
                _pluginThreadsFor = null;
                _pluginThreadsHead.Set("THREADS", t.TextDim);
                _pluginThreads.SetEmptyText(emptyText ?? "");
                _pluginThreads.SetItems(new List<UiListItem>());
                return;
            }
            string key = e.LinuxDir + (_pluginThreadsArchived ? "|archived" : "");
            if (key == _pluginThreadsFor) return; // shown already (the list refreshes on every scan: no new request)
            _pluginThreadsFor = key;
            int gen = ++_pluginThreadsGen;
            _pluginThreadsHead.Set("THREADS · asking MQH …", t.TextDim);
            _pluginThreads.SetEmptyText("");
            _pluginThreads.SetItems(new List<UiListItem>());
            void WithProject(Action<string> then)
            {
                if (_pluginProjects != null) { then(PluginProjectId(e)); return; }
                Mqh.Projects(pr =>
                {
                    if (gen != _pluginThreadsGen) return;
                    if (pr == null) { _pluginThreadsHead.Set("THREADS · ⚠ " + (Mqh.LastError ?? "MQH not reachable"), t.Danger); _pluginThreadsFor = null; return; }
                    _pluginProjects = pr as JArray ?? new JArray();
                    then(PluginProjectId(e));
                });
            }
            WithProject(pid =>
            {
                if (gen != _pluginThreadsGen) return;
                if (pid == null)
                {
                    _pluginThreadsHead.Set("THREADS (0)", t.TextDim);
                    _pluginThreads.SetEmptyText("No project in THREADS yet: NEW THREAD ▸ adds it and starts an agent on this plugin.");
                    return;
                }
                Action<JToken> show = th =>
                {
                    if (gen != _pluginThreadsGen) return;
                    if (th == null) { _pluginThreadsHead.Set("THREADS · ⚠ " + (Mqh.LastError ?? "MQH not reachable"), t.Danger); _pluginThreadsFor = null; return; }
                    var arr = th as JArray ?? new JArray();
                    if (_pluginThreadsArchived) _archivedThreads = arr; // ARCHIVED rows open THREADS' RESTORE / DELETE dialog (ArchivedRowPicked)
                    var mine = arr.Where(x => (string)x["projectId"] == pid).OrderByDescending(x => x["updatedAt"]?.Value<long>() ?? 0).ToList();
                    var items = new List<UiListItem>();
                    foreach (var x in mine)
                    {
                        string st = ThreadStatus(x);
                        bool working = ThreadTimeline.IsWorking(st), pending = x["hasPendingInteraction"]?.Value<bool>() == true;
                        string id = (string)x["id"];
                        items.Add(new UiListItem
                        {
                            Icon = _pluginThreadsArchived ? "▤" : pending ? "?" : working ? "▶" : st == "error" ? "⚠" : "◆",
                            IconColor = _pluginThreadsArchived ? t.TextDim : pending ? t.Warn : working ? t.Ok : st == "error" ? t.Danger : t.Accent,
                            Title = (string)x["title"] ?? (string)x["titleFallback"] ?? id,
                            Sub = $"{x["providerId"]} · {(_pluginThreadsArchived ? "archived" : st.Length > 0 ? st : "idle")} · {Ago(x["updatedAt"]?.Value<long>() ?? 0)}",
                            Badge = _pluginThreadsArchived ? "RESTORE ▸" : pending ? "NEEDS YOU" : working ? "RUNNING" : st == "error" ? "ERROR" : "",
                            BadgeColor = pending ? t.Warn : st == "error" ? t.Danger : _pluginThreadsArchived ? t.Accent : t.Ok,
                            Tag = (_pluginThreadsArchived ? "a:" : "t:") + id,
                        });
                    }
                    _pluginThreadsHead.Set((_pluginThreadsArchived ? "ARCHIVED THREADS" : "THREADS") + $" ({items.Count})" + (items.Count > 0 ? " · tap one to open it" : ""), t.TextAccent);
                    _pluginThreads.SetEmptyText(_pluginThreadsArchived ? "No archived threads on this plugin." : "No threads yet: NEW THREAD ▸ starts an agent on this plugin.");
                    _pluginThreads.SetItems(items);
                };
                if (_pluginThreadsArchived) Mqh.ThreadsArchived(show); else Mqh.Threads(pid, show);
            });
        }

        /// A thread row: opens it as THREADS does (its own window and group); an archived one: RESTORE or DELETE.
        private void PluginThreadPicked(UiListItem item)
        {
            if (!(item?.Tag is string tag)) return;
            if (tag.StartsWith("t:")) OpenThread(tag.Substring(2));
            else if (tag.StartsWith("a:"))
            {
                ArchivedRowPicked(tag.Substring(2));
                After(3f, () => { _pluginThreadsFor = null; ShowPluginDetail(); }); // a RESTORE moves it back
            }
        }

        /// NEW THREAD ▸: NEW THREAD with the plugin's project chosen (its project is added to MQH first when it is not there).
        private void NewThreadOnPickedPlugin()
        {
            if (!(SelectedPluginTag() is ScriptHost.Entry e) || !e.Project) { Toast("Pick a plugin project", "warn"); return; }
            WithPluginProject(e, pid => Mqh.Projects(pr =>
            {
                if (pr is JArray all) _projects = all; // THREADS' projects first, so the draft knows this one
                _projectSel = pid;
                Settings.lastProject = pid;
                _collapsedProjects.Remove(pid);
                OpenNewThread(null);
                RefreshThreads();
                _pluginThreadsFor = null; // its list again when it comes back
                Toast($"NEW THREAD on {e.Title}: pick the agent and START, then say what to make or change", "info");
            }));
        }

        /// The MQH project of a plugin project; added to MQH when it is not there (made while MQH was down, by an agent).
        private void WithPluginProject(ScriptHost.Entry e, Action<string> then)
        {
            void Find()
            {
                string pid = PluginProjectId(e);
                if (pid != null) { then(pid); return; }
                EnsureHost(() => Mqh.CreateProject(e.Title.Length > 0 ? e.Title : e.Name, _hostId, e.LinuxDir, r =>
                {
                    if (r == null) { Toast("Its project could not be added to MQH: " + Mqh.LastError, "error"); return; }
                    _pluginProjects = null;
                    then((string)r["id"]);
                }));
            }
            if (_pluginProjects != null) { Find(); return; }
            Mqh.Projects(pr =>
            {
                if (pr == null) { Toast("MQH: " + (Mqh.LastError ?? "not reachable"), "error"); return; }
                _pluginProjects = pr as JArray ?? new JArray();
                Find();
            });
        }

        private void TogglePickedPlugin()
        {
            var tag = SelectedPluginTag();
            if (tag is GestureExampleRow ex)
            {
                string err = InstallGestureExample(ex.Id);
                Toast(err == null ? $"{ex.Id} added as a plugin project" + (IsAppPluginExample(ex.Id) ? ": its tile is on the main menu" : "") : "Not added: " + err, err == null ? "ok" : "error");
                RefreshPluginList();
                if (err == null) SelectPluginForTest(ex.Id);
                return;
            }
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
            string text = e.Project
                ? $"Delete the plugin {e.Title}? Its project folder {ProjectKinds.ShortPath(e.LinuxDir)} goes (its files and git history), and its project in THREADS with its threads. This cannot be undone."
                : $"Delete the plugin {e.Title}? Its folder {e.LinuxDir} goes.";
            Modal("DELETE PLUGIN", text, new[] { "DELETE", "CANCEL" }, i => { if (i == 0) Toast(DeletePlugin(e.Name), "info"); });
        }

        /// Deletes a plugin: its folder (a project's with its .git), its saved state, and for a project its MQH project
        /// (with its threads). Returns what was done (the PLUGINS tab, the control port).
        public string DeletePlugin(string id)
        {
            var e = Scripts?.Find("plugin/" + id);
            if (e == null) return "error: no plugin " + id;
            bool project = e.Project;
            string dir = e.LinuxDir, title = e.Title.Length > 0 ? e.Title : e.Name;
            void Gone(string pid, bool mqhAnswered)
            {
                Scripts.Delete(e.Key);
                RefreshPluginList();
                if (project && !mqhAnswered) Toast($"{title}: folder deleted; MQH did not answer, so its project stays in THREADS", "warn");
                if (pid == null) return;
                Mqh.Call(System.Net.Http.HttpMethod.Delete, "/api/v1/projects/" + Uri.EscapeDataString(pid), null, r =>
                {
                    if (r == null) Toast($"{title}: folder deleted; its project stays in THREADS ({Mqh.LastError})", "warn");
                    _pluginProjects = null;
                    RefreshThreads();
                });
            }
            if (!project) { Gone(null, true); return $"Deleted {title} ({dir})"; }
            Mqh.Projects(pr =>
            {
                _pluginProjects = pr as JArray;
                Gone(PluginProjectId(e), pr != null);
            });
            return $"Deleting {title}: {ProjectKinds.ShortPath(dir)} and its project";
        }

        // ---- control port `plugin ...` (mcx-ui plugin ..., mcx-project new plugins)

        /// plugin list | new <name> app|gesture|behaviour | examples | example <id> | enable | disable | reload | log | delete <id> |
        /// event <window> <type> <id> [value] | dir
        public string PluginCommand(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0] : "list";
            string id = parts.Length > 1 ? parts[1].Trim() : "";
            switch (sub)
            {
                case "list":
                    return string.Join("\n", Scripts.Entries.Where(e => e.Kind == "plugin").Select(e =>
                        $"{e.Name} v{e.Version} enabled={e.Enabled} poses={e.Poses} detectors={e.Detectors} handlers={e.Handlers} error={e.Error ?? "-"} kind={(e.Project ? e.PluginKind : "-")} dir={e.LinuxDir}"));
                case "new":
                {
                    // new <name words> <kind> | new --kind <kind> <name words>: a plugin project (the kind is fixed)
                    var words = id.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    string kind = null;
                    int ki = words.IndexOf("--kind");
                    if (ki >= 0 && ki + 1 < words.Count) { kind = words[ki + 1]; words.RemoveRange(ki, 2); }
                    else if (words.Count > 1 && PluginProject.NormalizeKind(words[words.Count - 1]) != null) { kind = words[words.Count - 1]; words.RemoveAt(words.Count - 1); }
                    else if (words.Count > 1 && PluginProject.NormalizeKind(words[0]) != null) { kind = words[0]; words.RemoveAt(0); }
                    kind = PluginProject.NormalizeKind(kind);
                    string name = string.Join(" ", words);
                    if (kind == null || name.Length == 0) return "error: plugin new <name> app|gesture|behaviour (a plugin project in /root/metacodex-projects/plugins/<apps|gestures|behaviours>/<id>)";
                    string r = CreatePluginProject(kind, name);
                    if (r.StartsWith("error")) return r;
                    var e = Scripts.Find("plugin/" + PluginProject.IdFor(name));
                    return "ok, " + (e?.LinuxDir ?? PluginProject.LinuxDirOf(kind, PluginProject.IdFor(name))) + " (kind " + kind + ", fixed; git and its THREADS project follow in a few seconds)" +
                           (e?.Error != null ? "\nerror: " + e.Error : "");
                }
                case "examples": return string.Join("\n", GestureExampleIds.Select(x => $"{x} installed={Scripts.Find("plugin/" + x) != null} kind={ExampleKind(x)} gestures={string.Join(",", GestureNamesIn(ExamplePlugins[x]))}"));
                case "example":
                {
                    var err = InstallGestureExample(id);
                    var e = Scripts.Find("plugin/" + id.Trim().ToLowerInvariant());
                    return err == null ? "ok, " + (e?.LinuxDir ?? id) : "error: " + err;
                }
                case "enable": Scripts.SetEnabled("plugin/" + id, true); return "ok";
                case "disable": Scripts.SetEnabled("plugin/" + id, false); return "ok";
                case "reload": Scripts.ReloadAll(); return "ok";
                case "log": { var e = Scripts.Find("plugin/" + id); return e == null ? "error: no plugin " + id : string.Join("\n", e.Log); }
                case "delete": { var r = DeletePlugin(id); return r.StartsWith("error") ? r : "ok, " + r; }
                case "event":
                {
                    // event <window> <type> <id> [value]: what a tap or a sent text does in a plugin window (agents test
                    // inputs, toggles, cells and buttons with it); <window>: the full id or the plugin id (its main window)
                    var a = id.Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                    const string usage = "error: plugin event <window> button|input|toggle|cell|choice|open|close <component id> [value]";
                    if (a.Length < 2) return usage;
                    string win = a[0].StartsWith("plugin-") ? a[0] : "plugin-" + a[0] + "-main";
                    string type = a[1], cid = a.Length > 2 ? a[2] : "", val = a.Length > 3 ? a[3] : "";
                    if (Array.IndexOf(new[] { "button", "input", "toggle", "cell", "choice", "open", "close" }, type) < 0) return usage;
                    var owner = Scripts.WindowOwner(win);
                    if (owner == null) return "error: no plugin window " + win + " (plugin check <id> lists the windows)";
                    Scripts.WindowEvent(win, type, cid, val);
                    return owner.Error != null ? "error: the plugin stopped: " + owner.Error : "ok";
                }
                case "dir": return Scripts.PluginsDir;
                default: return "error: plugin list | new <name> app|gesture|behaviour | examples | example <id> | enable <id> | disable <id> | reload | log <id> | delete <id> | event <window> <type> <id> [value] | dir";
            }
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
