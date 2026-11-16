using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCodex.Scripting
{
    /// Plugin projects (owner 2026-11-16): a plugin is a project of type plugins with a fixed kind, in
    /// /root/metacodex-projects/plugins/{apps,gestures,behaviours}/<id>/. The folder name is the plugin id; the marker
    /// .metacodex/project.json ({"type": "plugins", "kind": "app"}, read-only) says it was made as one. The app trusts the
    /// folder and the marker together, never plugin.json's "kind". Pure: the PC harness (tests/plugins) and the app use
    /// the same rules and the same scaffold.
    public static class PluginProject
    {
        /// The Linux path of the plugins type folder (the kinds' folders are under it).
        public const string LinuxRoot = "/root/metacodex-projects/plugins";
        /// The marker, relative to the project folder.
        public const string MarkerPath = ".metacodex/project.json";
        public const string Type = "plugins";
        /// The kinds (the marker's "kind") and their folders, in the same order.
        public static readonly string[] Kinds = { "app", "gesture", "behaviour" };
        public static readonly string[] Folders = { "apps", "gestures", "behaviours" };

        public static string FolderOf(string kind) { int i = Array.IndexOf(Kinds, kind); return i < 0 ? null : Folders[i]; }
        public static string KindOfFolder(string folder) { int i = Array.IndexOf(Folders, folder); return i < 0 ? null : Kinds[i]; }

        /// A spoken or typed kind as one of Kinds ("apps", "window" -> app; "gestures" -> gesture; "behavior" -> behaviour);
        /// null when it is none of them.
        public static string NormalizeKind(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "app": case "apps": case "window": return "app";
                case "gesture": case "gestures": return "gesture";
                case "behaviour": case "behaviours": case "behavior": case "behaviors": return "behaviour";
                default: return null;
            }
        }

        /// What the PLUGINS tab and the dialogs call a kind.
        public static string Label(string kind) => kind == "app" ? "App" : kind == "gesture" ? "Gesture" : kind == "behaviour" ? "Behaviour" : "Plugin";

        public static string MarkerJson(string kind) => "{\n  \"type\": \"" + Type + "\",\n  \"kind\": \"" + kind + "\"\n}\n";

        /// The kind in a marker's text when its type is plugins and its kind is known; null otherwise (why: in `problem`).
        public static string KindFromMarker(string json, out string problem)
        {
            problem = null;
            var t = Regex.Match(json ?? "", "\"type\"\\s*:\\s*\"([A-Za-z]+)\"");
            if (!t.Success || t.Groups[1].Value != Type) { problem = "its " + MarkerPath + " is not a plugins marker (\"type\": \"" + Type + "\")"; return null; }
            var k = Regex.Match(json, "\"kind\"\\s*:\\s*\"([A-Za-z]+)\"");
            string kind = k.Success ? k.Groups[1].Value : null;
            if (kind == null || Array.IndexOf(Kinds, kind) < 0) { problem = "its " + MarkerPath + " has no kind app, gesture or behaviour"; return null; }
            return kind;
        }

        /// A project name as a plugin id: lower case, spaces -> '-', only a-z 0-9 - _, at most 32 characters; "" when
        /// nothing is left. The id is the folder's name and the start of the plugin's window ids.
        public static string IdFor(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "").Trim().ToLowerInvariant())
            {
                if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '_' || ch == '-') sb.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '.') sb.Append('-');
            }
            string s = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-', '_');
            return s.Length > 32 ? s.Substring(0, 32).TrimEnd('-', '_') : s;
        }

        /// /root/metacodex-projects/plugins/<kind folder>/<id>; null for an unknown kind or an empty id.
        public static string LinuxDirOf(string kind, string id)
        {
            string f = FolderOf(kind);
            return f == null || string.IsNullOrEmpty(id) ? null : LinuxRoot + "/" + f + "/" + id;
        }

        /// The kind from a Linux path under LinuxRoot (…/plugins/<kind folder>/<id>[/...]); null elsewhere.
        public static string KindFromLinuxPath(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith(LinuxRoot + "/", StringComparison.Ordinal)) return null;
            var rest = linuxPath.Substring(LinuxRoot.Length + 1).Split('/');
            return rest.Length >= 2 && rest[1].Length > 0 ? KindOfFolder(rest[0]) : null;
        }

        /// The files a new plugin project starts with: plugin.json, a working main.lua of its kind, AGENTS.md (the
        /// agent's entry point) and CLAUDE.md (points to AGENTS.md). The marker is not in it (the host writes it).
        public static Dictionary<string, string> Scaffold(string kind, string id, string name)
        {
            if (FolderOf(kind) == null) throw new ArgumentException("kind: app, gesture or behaviour");
            name = CleanName(name, id);
            string desc = kind == "app" ? "A new app plugin: a small window and a menu tile (an agent changes it in this project's thread)"
                : kind == "gesture" ? "A new gesture plugin: both palms up shows a toast (an agent changes it in this project's thread)"
                : "A new behaviour plugin: a triple clap shows a toast (an agent changes it in this project's thread)";
            var files = new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"" + id + "\",\n  \"name\": \"" + JsonEscape(name) + "\",\n  \"version\": \"0.1.0\",\n  \"kind\": \"" + kind + "\",\n  \"description\": \"" + desc + "\"\n}\n",
                ["main.lua"] = kind == "app" ? AppLua(id, name) : kind == "gesture" ? GestureLua(id, name) : BehaviourLua(id, name),
            };
            foreach (var kv in AgentFiles(kind, id, name)) files[kv.Key] = kv.Value;
            return files;
        }

        /// AGENTS.md and CLAUDE.md of a plugin project. fromExample: the project began as a shipped example (Timers,
        /// Calendar): it is the user's copy, to change when asked.
        public static Dictionary<string, string> AgentFiles(string kind, string id, string name, bool fromExample = false)
        {
            name = CleanName(name, id);
            return new Dictionary<string, string>
            {
                ["AGENTS.md"] = AgentsMd(kind, id, name, fromExample),
                // Claude Code reads CLAUDE.md (the import pulls AGENTS.md in); Codex and opencode read AGENTS.md
                ["CLAUDE.md"] = "This folder is a MetaCodex plugin project. Follow AGENTS.md (imported below).\n\n@AGENTS.md\n",
            };
        }

        /// The files a plugin project adds around the plugin's own files (the examples' "untouched" check skips them).
        public static bool IsProjectFile(string relPath)
        {
            string p = (relPath ?? "").Replace('\\', '/');
            return p == "AGENTS.md" || p == "CLAUDE.md" || p.StartsWith(".git/", StringComparison.Ordinal) || p == ".git"
                   || p.StartsWith(".metacodex/", StringComparison.Ordinal) || p == ".gitignore";
        }

        private static string CleanName(string name, string id)
        {
            name = Regex.Replace((name ?? "").Trim(), "[\\r\\n\\t\"\\\\]", " ").Trim();
            if (name.Length == 0) name = id;
            return name.Length > 40 ? name.Substring(0, 40).TrimEnd() : name;
        }

        private static string JsonEscape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string LuaStr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// A menu tile's label: capitals, 8 characters at most (the first word when it fits).
        public static string TileLabel(string name)
        {
            string up = Regex.Replace((name ?? "").ToUpperInvariant(), "[^A-Z0-9 +-]", "").Trim();
            if (up.Length == 0) up = "PLUGIN";
            string first = up.Split(' ')[0];
            string l = first.Length >= 3 && first.Length <= 8 ? first : up;
            return l.Length > 8 ? l.Substring(0, 8).TrimEnd() : l;
        }

        private static string AppLua(string id, string name) =>
"-- description: " + name + @": a new app plugin (a window and a menu tile)
-- Kind: app (fixed by the project). The app reloads this file within a second of a save.
-- Skill: quest-plugins. Change it in this project's thread: plan, the user's OK, write, mcx-ui plugin check " + id + @".

local S = { count = 0 }   -- the state: saved in store as JSON, so it survives a restart

local function load()
  local t = json.decode(store.get(""state"") or """")   -- nil when empty or broken: keep the defaults
  if type(t) == ""table"" then for k, v in pairs(t) do S[k] = v end end
end
local function save() store.set(""state"", json.encode(S)) end

local render -- below: builds the whole window from S

local function on(e)
  if e.type == ""button"" and e.id == ""add"" then S.count = S.count + 1; save(); render()
  elseif e.type == ""button"" and e.id == ""reset"" then S.count = 0; save(); render() end
end

render = function()
  ui.panel{ id = ""main"", title = " + LuaStr(name) + @", on = on, components = {
    { type = ""text"", text = ""A new plugin. Open its project in THREADS and ask an agent what it should do."" },
    { type = ""text"", id = ""count"", style = ""big"", text = tostring(S.count) },
    { type = ""buttons"", items = { { id = ""add"", label = ""+1"", tone = ""accent"" }, { id = ""reset"", label = ""RESET"" } } },
  } }
end

load(); render()
-- a tile on the DOCK and the wrist menu: a tap opens or closes the window
menu.add{ id = ""main"", label = " + LuaStr(TileLabel(name)) + @", glyph = ""◆"", icon = ""plugin"", panel = ""main"" }
";

        private static string GestureLua(string id, string name) =>
"-- description: " + name + @": a new gesture plugin (both palms up shows a toast)
-- Kind: gesture (fixed by the project). The app reloads this file within a second of a save.
-- Skills: quest-plugins, then quest-gesture-plugins. Gesture names are shared by every plugin: start yours with the id.

local PALMS_UP = """ + id + @"-palms-up""

-- a detector: true while both hands are open with the palms up (the app adds hold, cooldown, start and end)
gesture.define(PALMS_UP, { hand = ""both"", hold = 0.3, cooldown = 1.0, description = ""both hands open, palms up"" }, function(l, r)
  return l.open and r.open and l.palmFacing == ""up"" and r.palmFacing == ""up""
end)

-- the reaction: a toast only, so the new plugin changes nothing until the user says what it does
gesture.on(PALMS_UP, function(e)
  if e.phase == ""start"" then ui.toast(""Both palms up"") end
end)
";

        private static string BehaviourLua(string id, string name) =>
"-- description: " + name + @": a new behaviour plugin (a triple clap shows a toast)
-- Kind: behaviour (fixed by the project): Lua that reacts to the hands, the voice or time, with no window.
-- Skills: quest-plugins, then quest-scripting (the whole Lua API). The app reloads this file within a second of a save.

-- clap three times within 1.5 s (clap is a built-in gesture: both palms meet fast)
gesture.sequence({ ""clap"", ""clap"", ""clap"" }, 1.5, function(e)
  ui.toast(""Triple clap"")
end)
";

        private static string AgentsMd(string kind, string id, string name, bool fromExample)
        {
            string folder = FolderOf(kind);
            string dir = LinuxRoot + "/" + folder + "/" + id;
            var sb = new StringBuilder();
            sb.Append("# Plugin project: ").Append(name).Append(" (").Append(kind).Append(")\n\n");
            sb.Append("This folder is a MetaCodex plugin project. The MetaCodex app runs the plugin from here, loads it again within a\n")
              .Append("second of every save and at every start: `").Append(dir).Append("/`.\n")
              .Append("Plugin id: `").Append(id).Append("` (the folder's name). Kind: **").Append(kind).Append("**, fixed.\n\n");
            if (fromExample)
                sb.Append("This project began as the ").Append(name).Append(" example that ships with MetaCodex. It is the user's own copy now:\n")
                  .Append("change it when they ask (follow \"Change an existing plugin\" in the skill `quest-plugins`: keep its ids and\n")
                  .Append("its saved state readable).\n\n");
            sb.Append(kind == "app" ? "An app plugin has its own window (drawn by the app from a component list) and a tile on the main menu; it may\nadd voice commands, alarms and saved state.\n\n"
                : kind == "gesture" ? "A gesture plugin adds named hand gestures (pose files, Lua detectors, sequences) and what they do.\n\n"
                : "A behaviour plugin is Lua that reacts to the hands, the voice or time, with no window of its own.\n\n");

            sb.Append("## Fixed: never change\n\n")
              .Append("- `.metacodex/project.json` (`{\"type\": \"plugins\", \"kind\": \"").Append(kind).Append("\"}`, read-only) is the marker: without it the app\n")
              .Append("  does not load this folder. Never edit, delete or chmod it; never move or rename the folder.\n")
              .Append("- The kind is fixed by the marker and the folder (`plugins/").Append(folder).Append("/`). Keep `\"kind\": \"").Append(kind).Append("\"` in `plugin.json`:\n")
              .Append("  the marker wins, and `mcx-ui plugin check` warns when they differ. Another kind is another project (the user\n")
              .Append("  makes it: THREADS > + PROJECT > PLUGIN, or CONTROL > MQH > PLUGINS > + NEW PLUGIN).\n")
              .Append("- Write only in this folder. Never touch other plugins, `/mnt/quest/plugins`, the app or its settings.\n\n");

            sb.Append("## Read first\n\n")
              .Append("1. The skill `quest-plugins` (every plugin: files, the loop, checks, how to change and remove one");
            if (kind == "app") sb.Append("; its part APP:\n   windows, menu tiles, state, timers).\n");
            else if (kind == "gesture") sb.Append("), then\n   `quest-gesture-plugins` (poses, detectors, sequences, bindings, how to test without hands).\n");
            else sb.Append("; its part\n   BEHAVIOUR), then `quest-scripting` (the whole Lua API, voice commands).\n");
            sb.Append("2. `plugin.json`, all of `main.lua` and `git log --oneline` here: what the plugin does now.\n")
              .Append("The skills are in `~/.claude/skills/<name>/SKILL.md` when your agent does not list them.\n");
            if (kind == "app" && !fromExample)
                sb.Append("Two complete app plugins are models: `/root/metacodex-projects/plugins/apps/timers` and `.../apps/calendar` when\n")
                  .Append("the user has added them (`mcx-ui plugin examples` says so): read their `main.lua`; never change them from here.\n");
            sb.Append("\n");
            if (kind == "app")
                sb.Append("## What the app plugin needs (the app checks it)\n\n")
                  .Append("- `main.lua` builds its window in ONE `render()` that calls `ui.panel{ id = \"main\", ... }` again after every change,\n")
                  .Append("  and adds its tile once: `menu.add{ id = \"main\", label = \"").Append(TileLabel(name)).Append("\", glyph = \"◆\", icon = \"plugin\", panel = \"main\" }`\n")
                  .Append("  (label: capitals, 8 characters at most; icon: one of the skill's list; keep the tile's id once it is made).\n")
                  .Append("- 8 components at most; button labels short capitals (14 characters or fewer), one `accent` button at most;\n")
                  .Append("  every component id unique in the window; the app does not cut lists: render() shows the first rows (about 8)\n")
                  .Append("  and a dim \"+3 more\" line. Any rebuild empties `input` fields.\n")
                  .Append("- What must survive a restart is saved with `store.set(\"state\", json.encode(S))` after every change and read back\n")
                  .Append("  when `main.lua` loads (missing fields keep their defaults); times are `app.clock()` seconds; timers are set\n")
                  .Append("  again at load. Anything that changes every second is a LIVE text (a `text` with an `id`).\n")
                  .Append("- Every voice command is complete (description, typed params, phrases, 4+ English samples).\n\n");

            sb.Append("## The loop (every change)\n\n")
              .Append("1. **Plan, then wait for the user's OK.** Say what the user will see and do (the window, the menu tile, the\n")
              .Append("   gestures, the voice commands), what is saved, and how to undo it. Write nothing before the OK.\n")
              .Append("2. Write complete files here (no placeholders). Raise the minor number of `version` in `plugin.json`\n")
              .Append("   (0.1.0 -> 0.2.0, 1.0.0 -> 1.1.0).\n")
              .Append("3. Check: `mcx-ui plugin check ").Append(id).Append("` must say `loaded` and `warnings: 0`. Fix every `ERROR` and `warn` line,\n")
              .Append("   save, check again. `mcx-ui plugin log ").Append(id).Append("` shows the whole log.\n")
              .Append("4. Try it:\n");
            if (kind == "app")
                sb.Append("   - `mcx-ui view open plugin-").Append(id).Append("-main` opens the window; `mcx-ui ui click view-plugin-").Append(id).Append("-main <LABEL>` presses a\n")
                  .Append("     button by its label; `mcx-ui plugin event ").Append(id).Append(" input|cell|toggle|button <component id> [value]` sends\n")
                  .Append("     what a sent text or a tap sends; then `mcx-ui plugin check ").Append(id).Append("` again (it must still say `loaded`).\n");
            else if (kind == "gesture")
                sb.Append("   - `mcx-ui gesture list` shows your gesture names; `mcx-ui gesture <name> right start` (or `both start`) simulates\n")
                  .Append("     one: the reaction must happen (`mcx-ui ui list` shows which panels are open); then `mcx-ui plugin check ").Append(id).Append("`.\n");
            else
                sb.Append("   - `mcx-ui gesture clap` simulates a clap (`mcx-ui gesture <name> right start` any other gesture); the reaction must\n")
                  .Append("     happen; then `mcx-ui plugin check ").Append(id).Append("`.\n");
            sb.Append("   - A voice command you added: `mcx-ui voice decide \"<one of its samples>\"` picks it, `mcx-ui voice run \"<sample>\"` runs it.\n")
              .Append("5. Commit: `git add -A && git commit -m \"<what changed>\"`, one commit per working step.\n")
              .Append("6. Report in a few short lines: what the user does now (")
              .Append(kind == "app" ? "the tile in the menu, the buttons, what to say" : kind == "gesture" ? "the gesture in body words, what it does" : "what to do, what happens")
              .Append("), what changed, how to undo it.\n\n");

            sb.Append("## Undo\n\n")
              .Append("- One step back: `git log --oneline`, then `git revert <commit>`; the app reloads at once.\n")
              .Append("- Off and on: `mcx-ui plugin disable ").Append(id).Append("` / `mcx-ui plugin enable ").Append(id).Append("` (its windows, tiles, gestures and voice\n")
              .Append("  commands go and come back).\n")
              .Append("- Delete: the user does it (CONTROL > MQH > PLUGINS > DELETE removes this folder and its project).\n\n");

            sb.Append("## Rules\n\n")
              .Append("- Text the user sees is English, with no brand names. No secrets in files.\n")
              .Append("- Lua runs in a sandbox (no `io`, `require`, `load`, network; for time use `app.clock()` and `app.date()`). One call\n")
              .Append("  over 200,000 instructions stops the plugin.\n")
              .Append("- The app keeps 72 frames a second: nothing heavy in a callback; a countdown changes only a LIVE text (a `text` with an `id`).\n");
            if (kind == "gesture")
                sb.Append("- React only to gestures this plugin defines or to the built-ins (`pinch grab point open palm-up clap select`): the examples'\n")
                  .Append("  poses (thumbs-up, victory...) are not on every headset. Start your gesture names with `").Append(id).Append("-`.\n");
            if (kind != "app")
                sb.Append("- Only app plugins get a tile on the main menu: a ").Append(kind).Append(" plugin's `menu.add` is not shown.\n");
            return sb.ToString();
        }
    }
}
