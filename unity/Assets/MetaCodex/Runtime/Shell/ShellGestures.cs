using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.Hands;
using MetaCodex.Scripting;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// A no-code link from a gesture to an action ("thumbs-up" -> "open:git"). Kept in the shell settings, so
    /// it persists, syncs to MQH (quest.settings) and agents can edit it there.
    [Serializable]
    public sealed class GestureBinding
    {
        public string gesture = "";
        /// any | left | right | both
        public string hand = "any";
        /// start (also taps) | end | any
        public string phase = "start";
        public string action = "";
        public bool enabled = true;
    }

    /// The Gestures panel (studio for builders) and the gesture plumbing of the shell: bindings, actions,
    /// recording poses, plugin scaffolds and the starter plugins.
    public sealed partial class MetaCodexShell
    {
        /// BIND's presets: what a hand is good for (owner 2026-11-12: "clean up a bit scripts/gestures"). Opening the
        /// other panels, layouts and spins stay reachable through CUSTOM ACTION… and RunAction keeps every verb, so
        /// bindings saved with an older preset (toggle:browser, spin:40, …) still run.
        public static readonly (string label, string action)[] PresetActions =
        {
            ("TALK", "voice:toggle"), ("TERMINALS", "toggle:terminals"), ("NEW TERMINAL", "terminal:new"),
            ("THREADS", "toggle:threads"), ("FILES", "toggle:files"), ("WEB", "toggle:web"), ("GIT", "toggle:git"),
            ("KEYBOARD", "keyboard"), ("SELECT", "select"), ("RECENTER", "recenter"), ("FOCUS", "focus"),
            ("CTRL-C", "type:\u0003"),
        };

        private UiTabs _gTabs;
        private UiList _gList, _gBindings, _gActions, _gPlugins;
        private UiLabel _gDetail, _gBindTarget, _gRecordStatus, _gEvents, _gPluginDetail, _gCost;
        private UiButton _gTestBtn, _gBindBtn, _gSourceBtn, _gDeleteBtn, _gAddBtn;
        /// The example row picked in ALL (null: a gesture row is picked), and whether EXAMPLES is unfolded.
        private string _gExample;
        private bool _gExamplesOpen;
        private readonly UiLabel[] _gHandText = new UiLabel[2];
        private readonly UiProgress[,] _gBars = new UiProgress[2, 6];
        private UiTextField _gRecordName;
        private string _gSelected = "clap";
        private int _gBindHand, _gRecordHand = 1;
        private float _gTolerance = 0.3f, _gHold = 0.15f;
        private (string label, string action) _gAction = PresetActions[0];
        private readonly List<(float at, Action act)> _later = new List<(float, Action)>();

        /// Runs an action after a delay (main thread, from Update).
        public void After(float seconds, Action act) => _later.Add((Time.unscaledTime + seconds, act));

        private void RunLater()
        {
            if (_later.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = 0; i < _later.Count; i++)
            {
                if (_later[i].at > now) continue;
                var a = _later[i].act;
                _later.RemoveAt(i--);
                try { a(); } catch (Exception ex) { Log("delayed action failed: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ actions and bindings

        /// One action string, the shared vocabulary of bindings, scripts (app.action) and the control port.
        public string RunAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return "empty action";
            int c = action.IndexOf(':');
            string verb = (c < 0 ? action : action.Substring(0, c)).Trim().ToLowerInvariant();
            string arg = c < 0 ? "" : action.Substring(c + 1);
            switch (verb)
            {
                case "open": return OpenPanel(arg.Trim()) ? null : "no panel " + arg;
                case "close": return ClosePanel(arg.Trim()) ? null : "no panel " + arg;
                case "toggle": return TogglePanel(arg.Trim()) ? null : "no panel " + arg;
                case "toast": Toast(arg, "info"); return null;
                case "run": TerminalSend(arg + "\r"); return null;
                case "type": TerminalSend(MetaCodex.App.ControlServer.Unescape(arg)); return null;
                case "url": OpenBrowser(arg.Trim()); return null;
                case "layout": SetLayout(arg.Trim()); return null;
                case "spin": Spin(float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var deg) ? deg : 30f, 0f); return null;
                case "select": SetSelectionMode(arg.Trim() == "" ? !Router.SelectionMode : arg.Trim() != "off"); return null;
                case "keyboard": OpenKeyboard(); return null;
                case "terminal": if (arg.Trim() == "new") { TerminalNew(); return null; } return "terminal:new";
                case "recenter": app?.Recenter(); OnRecentered(Vector3.zero, Quaternion.identity); return null;
                case "lua": { var r = Scripts.Eval(arg); return r.StartsWith("error:") ? r : null; }
                case "gesture": Gestures.RaiseExternal(arg.Trim(), "both", "tap", Time.unscaledTime, Vector3.zero, "action"); return null;
                case "voice": return VoiceAction(arg);
                case "focus": { string a = arg.Trim().ToLowerInvariant(); SetFocus(a == "on" ? true : a == "off" ? false : !Settings.focusEnabled); return null; }
                case "view": return OpenView(MetaCodexShell.SafeViewId(arg.Trim())) ? null : "no view " + arg;
                default: return "unknown action '" + verb + "' (open/close/toggle/toast/run/type/url/layout/spin/select/keyboard/terminal/recenter/lua/gesture/voice/focus/view)";
            }
        }

        /// A pose's start fires its bindings only once it is held this long (owner 2026-11-13: "panels open randomly";
        /// the log: a victory binding opened VAULT 3 times in 40 s while the hands worked on windows).
        public const float BindingHold = 0.35f;
        private readonly Dictionary<string, (GestureEvent e, float at)> _bindingHeld = new Dictionary<string, (GestureEvent, float)>();

        private void ApplyBindings(GestureEvent e)
        {
            if (Settings?.bindings == null || Settings.bindings.Count == 0) return;
            string key = e.Name + "/" + e.Hand;
            if (e.Phase == "end") _bindingHeld.Remove(key);
            // a pose's start waits until it is held (TickBindings); taps (swipes, the clap) and ends go at once
            if (e.Phase == "start") { _bindingHeld[key] = (e, e.Time); return; }
            FireBindings(e);
        }

        private void FireBindings(GestureEvent e)
        {
            var matches = Settings.bindings.Where(b => b.enabled && b.gesture == e.Name && (b.hand == "any" || b.hand == e.Hand)
                && (b.phase == "any" || (b.phase == "end" ? e.Phase == "end" : e.Phase == "start" || e.Phase == "tap"))).ToList();
            if (matches.Count == 0) return;
            if (HandOnUiFor(e.Hand)) { Log($"binding {e.Name}: not run, the {e.Hand} hand is on a window"); return; }
            foreach (var b in matches)
            {
                var err = RunAction(b.action);
                Log($"binding {b.gesture} -> {b.action}: {err ?? "ok"}");
                if (err != null) Toast($"Binding {b.gesture}: {err}", "warn");
            }
        }

        private bool HandOnUiFor(string hand) => Router != null && (hand == "left" ? Router.HandOnUi(0) : hand == "right" ? Router.HandOnUi(1) : Router.HandOnUi(0) || Router.HandOnUi(1));

        /// Every frame (and every test gesture step), on the gestures' clock: the poses held long enough fire their
        /// bindings (once each).
        private void TickBindings(float now)
        {
            if (_bindingHeld.Count == 0) return;
            foreach (var kv in _bindingHeld.ToList())
            {
                if (now - kv.Value.at < BindingHold) continue;
                _bindingHeld.Remove(kv.Key);
                FireBindings(kv.Value.e);
            }
        }

        public void AddBinding(string gesture, string action, string hand = "any", string phase = "start")
        {
            Settings.bindings.RemoveAll(b => b.gesture == gesture && b.hand == hand && b.action == action);
            Settings.bindings.Add(new GestureBinding { gesture = gesture, action = action, hand = hand, phase = phase });
            OnSettingsChanged();
            RefreshGesturesPanel();
        }

        // ------------------------------------------------------------------ gesture examples (opt-in)

        /// The marker of the one-time cleanup of the old starter copies (RetireStarterExamples). It also stops the
        /// cleanup from ever touching an example the user adds later. `.starter-installed` stays where it is: an
        /// older build then never seeds the starters again after a downgrade.
        public const string ExamplesMarker = ".examples-v2";

        private static string PoseFileV1(string name, string desc, string fingers, string extra = "") =>
            "{\n  \"name\": \"" + name + "\",\n  \"description\": \"" + desc + "\",\n  \"hand\": \"any\",\n  \"fingers\": " + fingers + ",\n  \"tolerance\": 0.35," + extra + "\n  \"hold\": 0.15,\n  \"cooldown\": 0.5\n}\n";

        /// FROZEN: the exact files the builds up to 2026-11-12 seeded into /mnt/quest/plugins on first run. Only the
        /// cleanup compares against them, to tell an untouched copy from one the owner edited. Never change a byte
        /// here (an edit would make old untouched copies look edited, or worse, edited ones look untouched); change
        /// ExamplePlugins instead.
        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StarterV1 = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["core-poses"] = new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"core-poses\",\n  \"name\": \"Core poses\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"Thumbs up / down, victory, horns and call-me as named gestures (data only: poses/*.json)\"\n}\n",
                ["poses/thumbs-up.json"] = PoseFileV1("thumbs-up", "fist, thumb pointing up", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 1 }", "\n  \"thumbPointing\": \"up\","),
                ["poses/thumbs-down.json"] = PoseFileV1("thumbs-down", "fist, thumb pointing down", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 1 }", "\n  \"thumbPointing\": \"down\","),
                ["poses/victory.json"] = PoseFileV1("victory", "index and middle up, the rest curled", "{ \"index\": 0, \"middle\": 0, \"ring\": 1, \"little\": 1 }"),
                ["poses/horns.json"] = PoseFileV1("horns", "index and little up, middle and ring curled", "{ \"index\": 0, \"middle\": 1, \"ring\": 1, \"little\": 0 }"),
                ["poses/call-me.json"] = PoseFileV1("call-me", "thumb and little out, the rest curled", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 0 }"),
            },
            ["swipes"] = new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"swipes\",\n  \"name\": \"Swipes\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"swipe-left / swipe-right / swipe-up: a fast move of an open hand (Lua detectors)\"\n}\n",
                ["main.lua"] =
@"-- description: open-hand swipes, detected from the hand's head-relative velocity
-- h.vel is in metres per second in the head's frame: x = right, y = up, z = forward.
local SPEED = 1.1      -- m/s along the swipe
local CROSS = 0.7      -- m/s allowed across it

gesture.define(""swipe-left"", { hand = ""any"", hold = 0, cooldown = 0.7, description = ""open hand moves fast to the left"" }, function(h)
  return h.open and h.vel.x < -SPEED and math.abs(h.vel.y) < CROSS
end)

gesture.define(""swipe-right"", { hand = ""any"", hold = 0, cooldown = 0.7, description = ""open hand moves fast to the right"" }, function(h)
  return h.open and h.vel.x > SPEED and math.abs(h.vel.y) < CROSS
end)

gesture.define(""swipe-up"", { hand = ""any"", hold = 0, cooldown = 0.7, description = ""open hand, palm up, moves fast upwards"" }, function(h)
  return h.open and h.palmFacing == ""up"" and h.vel.y > SPEED and math.abs(h.vel.x) < CROSS
end)
",
            },
        };

        /// The gesture examples: nothing installs them by itself. GESTURES > ALL > EXAMPLES (+ ADD), the control
        /// port (`plugin example <id>`) and voice install one with InstallGestureExample. Today they are the
        /// starter files; change them here freely (StarterV1 keeps the old ones for the cleanup).
        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ExamplePlugins = StarterV1;

        /// Example ids in the order the list shows them.
        public static IEnumerable<string> GestureExampleIds => ExamplePlugins.Keys;

        /// The gesture names a plugin's files define: poses/<name>.json and gesture.define("<name>", …).
        public static List<string> GestureNamesIn(IReadOnlyDictionary<string, string> files)
        {
            var names = new List<string>();
            foreach (var kv in files)
            {
                if (kv.Key.StartsWith("poses/") && kv.Key.EndsWith(".json")) names.Add(kv.Key.Substring(6, kv.Key.Length - 11));
                else if (kv.Key.EndsWith(".lua"))
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(kv.Value, "gesture\\.define\\(\\s*[\"']([^\"']+)[\"']"))
                        names.Add(m.Groups[1].Value);
            }
            return names;
        }

        /// Installs one example plugin (GESTURES > EXAMPLES > + ADD, `plugin example <id>`, voice). null = ok, else
        /// the error. An existing folder of that name is never overwritten: it is only enabled again.
        public string InstallGestureExample(string id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            if (!ExamplePlugins.TryGetValue(id, out var files)) return $"no example '{id}' (examples: {string.Join(", ", GestureExampleIds)})";
            try
            {
                Directory.CreateDirectory(Scripts.PluginsDir);
                var marker = Path.Combine(Scripts.PluginsDir, ExamplesMarker);
                if (!File.Exists(marker)) File.WriteAllText(marker, "gesture examples are opt-in; the old starter copies were checked " + DateTime.UtcNow.ToString("o") + "\n");
                if (Directory.Exists(Path.Combine(Scripts.PluginsDir, id)))
                {
                    Scripts.SetEnabled("plugin/" + id, true);
                    Log($"gesture example {id}: already in /mnt/quest/plugins, enabled");
                }
                else
                {
                    var e = Scripts.SavePlugin(id, files.ToDictionary(kv => kv.Key, kv => kv.Value));
                    if (e.Error != null) return e.Error;
                    Log($"gesture example {id} installed: {e.Poses} poses, {e.Detectors} detectors");
                }
            }
            catch (Exception ex) { return ex.Message; }
            RefreshGesturesPanel();
            return null;
        }

        /// True when the plugin folder holds exactly one of the given file sets (same files, same text; CRLF = LF).
        public static bool SameFiles(string dir, IReadOnlyDictionary<string, string> files)
        {
            if (!Directory.Exists(dir)) return false;
            var onDisk = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(dir.Length).TrimStart('/', '\\').Replace('\\', '/')).ToList();
            if (onDisk.Count != files.Count || onDisk.Any(f => !files.ContainsKey(f))) return false;
            foreach (var kv in files)
            {
                string text;
                try { text = File.ReadAllText(Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception) { return false; }
                if (text.Replace("\r\n", "\n") != kv.Value.Replace("\r\n", "\n")) return false;
            }
            return true;
        }

        /// Runs once per plugins folder (marker ExamplesMarker), in place of the old first-run seeding: deletes the
        /// starter copies (core-poses, swipes) that are untouched (StarterV1, byte for byte) AND unused (no binding
        /// names one of their gestures, no other script or plugin's Lua quotes one). Edited or used copies stay
        /// (logged with the reason); my-poses and every other folder are never touched. Returns what it did.
        public static List<string> RetireStarterCopies(string pluginsDir, string scriptsDir, IEnumerable<string> boundGestures)
        {
            var log = new List<string>();
            if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir)) return log;
            var marker = Path.Combine(pluginsDir, ExamplesMarker);
            if (File.Exists(marker)) return log;
            var bound = new HashSet<string>(boundGestures ?? Enumerable.Empty<string>());
            foreach (var starter in StarterV1)
            {
                var dir = Path.Combine(pluginsDir, starter.Key);
                if (!Directory.Exists(dir)) continue;
                if (!SameFiles(dir, starter.Value)) { log.Add($"kept {starter.Key}: edited"); continue; }
                var names = GestureNamesIn(starter.Value);
                string usedBy = names.Where(bound.Contains).Select(n => "binding " + n).FirstOrDefault()
                                ?? UsedInLua(pluginsDir, scriptsDir, dir, names);
                if (usedBy != null) { log.Add($"kept {starter.Key}: used ({usedBy})"); continue; }
                try { Directory.Delete(dir, true); log.Add($"removed {starter.Key}: untouched, unused"); }
                catch (Exception ex) { log.Add($"kept {starter.Key}: {ex.Message}"); }
            }
            try { File.WriteAllText(marker, "gesture examples are opt-in; the old starter copies were checked " + DateTime.UtcNow.ToString("o") + "\n" + string.Join("\n", log) + "\n"); }
            catch (Exception ex) { log.Add("marker not written: " + ex.Message); }
            return log;
        }

        /// The first Lua file outside `self` that quotes one of the names ("thumbs-up" or 'thumbs-up'), or null.
        private static string UsedInLua(string pluginsDir, string scriptsDir, string self, List<string> names)
        {
            var files = new List<string>();
            if (!string.IsNullOrEmpty(scriptsDir) && Directory.Exists(scriptsDir)) files.AddRange(Directory.GetFiles(scriptsDir, "*.lua"));
            foreach (var d in Directory.GetDirectories(pluginsDir))
                if (!string.Equals(Path.GetFullPath(d).TrimEnd('/', '\\'), Path.GetFullPath(self).TrimEnd('/', '\\'), StringComparison.Ordinal))
                    files.AddRange(Directory.GetFiles(d, "*.lua", SearchOption.AllDirectories));
            foreach (var f in files)
            {
                string text;
                try { text = File.ReadAllText(f); } catch (Exception) { continue; }
                foreach (var n in names)
                    if (text.Contains("\"" + n + "\"") || text.Contains("'" + n + "'")) return Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f) + " names " + n;
            }
            return null;
        }

        /// Shell start: the one-time cleanup, with the user's bindings (on or off); forgets the removed plugins'
        /// enabled flags.
        private void RetireStarterExamples()
        {
            try
            {
                var log = RetireStarterCopies(Scripts.PluginsDir, Scripts.Dir, Settings?.bindings?.Select(b => b.gesture));
                foreach (var l in log)
                {
                    Log("gesture examples: " + l);
                    if (l.StartsWith("removed ")) Scripts.ForgetState("plugin/" + l.Substring(8).Split(':')[0]);
                }
            }
            catch (Exception ex) { Log("gesture examples: cleanup skipped: " + ex.Message); }
        }

        /// Installed = the folder exists. Untouched = it still holds the shipped files (it shows under EXAMPLES, not
        /// YOURS). Cached per plugin change time: the list refreshes on every scan.
        private readonly Dictionary<string, (DateTime mtime, bool untouched)> _exampleState = new Dictionary<string, (DateTime, bool)>();

        private bool IsUntouchedExample(string id)
        {
            if (!ExamplePlugins.ContainsKey(id)) return false;
            var e = Scripts.Find("plugin/" + id);
            if (e == null) return false;
            if (_exampleState.TryGetValue(id, out var c) && c.mtime == e.Mtime) return c.untouched;
            var dir = Path.Combine(Scripts.PluginsDir, id);
            bool same = SameFiles(dir, ExamplePlugins[id]) || SameFiles(dir, StarterV1[id]);
            _exampleState[id] = (e.Mtime, same);
            return same;
        }

        public const string PluginTemplateLua =
@"-- description: my gesture plugin (edit me: the app reloads this file within a second)
-- Pose files in poses/*.json define named hand shapes; gesture.define adds Lua detectors;
-- gesture.on / gesture.sequence react to any gesture. Skill for agents: quest-gesture-plugins.

-- a detector: both palms facing up for a moment
gesture.define(""both-palms-up"", { hand = ""both"", hold = 0.3, cooldown = 1.0, description = ""both hands open, palms up"" }, function(l, r)
  return l.open and r.open and l.palmFacing == ""up"" and r.palmFacing == ""up""
end)

-- the reaction: a toast only, so a new plugin changes nothing until you say what it does
-- (e.g. app.action(""toggle:threads""); actions: the skill quest-gesture-plugins)
gesture.on(""both-palms-up"", function(e)
  if e.phase == ""start"" then
    ui.toast(""Both palms up"")
  end
end)
";

        /// Scaffolds a plugin folder from the template (MQH > PLUGINS > + NEW PLUGIN…, control port `plugin new`).
        public ScriptHost.Entry NewPlugin(string id)
        {
            id = ScriptHost.Sanitize(id);
            return Scripts.SavePlugin(id, new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"" + id + "\",\n  \"name\": \"" + id + "\",\n  \"version\": \"0.1.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"both palms up shows a toast (a template: edit main.lua)\"\n}\n",
                ["main.lua"] = PluginTemplateLua,
            });
        }

        // ------------------------------------------------------------------ recording

        /// Saves what the hand does now as a named pose in the "my-poses" plugin (loaded at once).
        public PoseSpec RecordPoseNow(Handedness hand, string name)
        {
            name = ScriptHost.Sanitize(string.IsNullOrWhiteSpace(name) ? "pose-" + DateTime.Now.ToString("HHmmss") : name.Trim().ToLowerInvariant());
            var p = Gestures.Capture(hand, name, _gTolerance);
            if (p == null) return null;
            p.Hold = _gHold;
            p.Hand = "any";
            var files = new Dictionary<string, string>
            {
                ["poses/" + name + ".json"] = PoseJson.ToJson(p).ToString(),
            };
            if (!File.Exists(Path.Combine(Scripts.PluginsDir, "my-poses", "plugin.json")))
                files["plugin.json"] = "{\n  \"id\": \"my-poses\",\n  \"name\": \"My poses\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"poses recorded in the Gestures panel\"\n}\n";
            Scripts.SavePlugin("my-poses", files);
            _gSelected = name;
            RefreshGesturesPanel();
            return p;
        }

        private void StartRecording()
        {
            var hand = _gRecordHand == 0 ? Handedness.Left : Handedness.Right;
            string name = _gRecordName?.Text;
            for (int i = 3; i >= 1; i--)
            {
                int k = i;
                After(3 - i, () => _gRecordStatus?.Set($"Hold the pose with your {(hand == Handedness.Left ? "LEFT" : "RIGHT")} hand… {k}", UiTheme.Current.Warn));
            }
            After(3f, () =>
            {
                var p = RecordPoseNow(hand, name);
                if (p == null) { _gRecordStatus?.Set("⚠ that hand is not tracked; try again", UiTheme.Current.Danger); return; }
                _gRecordStatus?.Set($"<color=#4DFFA6>● saved '{p.Name}'</color>\n{p.Summary()}", UiTheme.Current.Text);
                Toast($"Pose '{p.Name}' recorded", "ok");
            });
        }

        // ------------------------------------------------------------------ the panel

        /// GESTURES (a tab of MQH): the studio, with its own tabs. Its plugin list is MQH > PLUGINS now.
        private void BuildGesturesTab(UiTabs outer, int outerPage, Rect c)
        {
            var t = UiTheme.Current;
            _gTabs = outer.AddTo(outerPage, new UiTabs("ALL", "LIVE", "BIND", "RECORD") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);
            float leftW = 0.30f;
            var right = new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height);

            // ALL: YOURS / SYSTEM / EXAMPLES (folded), like SKILLS; the list refreshes by itself on every scan
            _gList = _gTabs.AddTo(0, new UiList(0.030f, (item, idx) => GestureRowPicked(item)) { Rect = new Rect(area.x, area.y, leftW, area.height) });
            var rs = new UiStack(right, t.Gap);
            _gDetail = _gTabs.AddTo(0, new UiLabel("", t.SmallSize * 1.1f) { Rect = rs.Next(area.height - t.ButtonHeight * 2 - t.Gap * 3), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            var row1 = AddRow(_gTabs, 0, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("TEST ▶", () => { if (_gExample == null) TestGesture(_gSelected); }),
                ("BIND →", () => { if (_gExample != null) return; _gTabs.Select(2); RefreshGesturesPanel(); }),
                ("SOURCE", ShowGestureSource),
            });
            var row2 = AddRow(_gTabs, 0, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("DELETE POSE", DeleteSelectedPose),
                ("+ ADD", AddPickedExample),
            }, primary: 1, danger: 0);
            (_gTestBtn, _gBindBtn, _gSourceBtn) = (row1[0], row1[1], row1[2]);
            (_gDeleteBtn, _gAddBtn) = (row2[0], row2[1]);

            // LIVE: both hands, finger by finger
            float colW = (area.width - t.Gap * 2) / 2f;
            string[] rowNames = { "THUMB", "INDEX", "MIDDLE", "RING", "LITTLE", "PINCH" };
            for (int h = 0; h < 2; h++)
            {
                var col = new UiStack(new Rect(area.x + h * (colW + t.Gap * 2), area.y, colW, area.height * 0.62f), t.Gap * 0.6f);
                _gTabs.AddTo(1, new UiLabel(h == 0 ? "LEFT HAND" : "RIGHT HAND", t.SmallSize) { Rect = col.Next(0.02f), UseDisplay = true, Color = t.TextAccent });
                for (int i = 0; i < 6; i++)
                {
                    var r = col.NextSplit(0.018f, 0.24f, out var bar);
                    _gTabs.AddTo(1, new UiLabel(rowNames[i], t.SmallSize * 0.9f) { Rect = r, UseDisplay = true, Color = t.TextDim });
                    _gBars[h, i] = _gTabs.AddTo(1, new UiProgress(0f) { Rect = bar, Tint = i == 5 ? t.Warn : (Color?)null });
                }
                _gHandText[h] = _gTabs.AddTo(1, new UiLabel("", t.SmallSize) { Rect = col.Rest(), UseMono = true, Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.Text });
            }
            var bottom = new Rect(area.x, area.y + area.height * 0.64f, area.width, area.height * 0.36f);
            _gEvents = _gTabs.AddTo(1, new UiLabel("", t.SmallSize) { Rect = new Rect(bottom.x, bottom.y, bottom.width * 0.62f, bottom.height), UseMono = true, Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextAccent });
            _gCost = _gTabs.AddTo(1, new UiLabel("", t.SmallSize) { Rect = new Rect(bottom.x + bottom.width * 0.64f, bottom.y, bottom.width * 0.36f, bottom.height), UseMono = true, Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            // the live bars refresh while MQH shows GESTURES > LIVE
            _panels["mqh"].Tick += _ => { if (_gTabs != null && _gTabs.Selected == 1 && outer.Selected == outerPage) RefreshGestureLive(); };

            // BIND: gesture -> action
            var bs = new UiStack(new Rect(area.x, area.y, leftW + 0.08f, area.height), t.Gap);
            _gBindTarget = _gTabs.AddTo(2, new UiLabel("", t.BodySize) { Rect = bs.Next(0.026f) });
            _gTabs.AddTo(2, new UiSegmented("HAND", new[] { "ANY", "LEFT", "RIGHT", "BOTH" }, 0, i => _gBindHand = i) { Rect = bs.Next(0.032f), LabelWidth = 0.07f });
            _gTabs.AddTo(2, new UiLabel("ACTION", t.SmallSize) { Rect = bs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gActions = _gTabs.AddTo(2, new UiList(0.027f, (item, idx) => { if (item.Tag is ValueTuple<string, string> a) _gAction = a; RefreshBindTarget(); }) { Rect = bs.Next(area.height - 0.026f - 0.032f - 0.018f - t.ButtonHeight * 2 - t.Gap * 6) });
            _gActions.SetItems(PresetActions.Select(a => new UiListItem { Icon = "▸", Title = a.label, Sub = a.action, Tag = a }));
            AddRow(_gTabs, 2, bs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ ADD BINDING", () => AddBinding(_gSelected, _gAction.action, new[] { "any", "left", "right", "both" }[_gBindHand])),
            }, primary: 0);
            AddRow(_gTabs, 2, bs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("CUSTOM ACTION…", () => UiKeyboard.Edit("run: ", false, null, s => { if (!string.IsNullOrWhiteSpace(s)) { _gAction = ("custom", s.Trim()); RefreshBindTarget(); } })),
            });
            var bindRight = new UiStack(new Rect(area.x + leftW + 0.08f + t.Gap * 2, area.y, area.width - leftW - 0.08f - t.Gap * 2, area.height), t.Gap);
            _gTabs.AddTo(2, new UiLabel("BINDINGS", t.SmallSize) { Rect = bindRight.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gBindings = _gTabs.AddTo(2, new UiList(0.028f) { Rect = bindRight.Next(area.height - 0.018f - t.ButtonHeight - t.Gap * 2) });
            AddRow(_gTabs, 2, bindRight.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("ON / OFF", () => { if (_gBindings.SelectedIndex >= 0 && _gBindings.SelectedIndex < Settings.bindings.Count) { var b = Settings.bindings[_gBindings.SelectedIndex]; b.enabled = !b.enabled; OnSettingsChanged(); RefreshGesturesPanel(); } }),
                ("DELETE", () => { if (_gBindings.SelectedIndex >= 0 && _gBindings.SelectedIndex < Settings.bindings.Count) { Settings.bindings.RemoveAt(_gBindings.SelectedIndex); OnSettingsChanged(); RefreshGesturesPanel(); } }),
            }, danger: 1);

            // RECORD: capture a pose from the hand
            var rec = new UiStack(new Rect(area.x, area.y, area.width * 0.55f, area.height), t.Gap);
            _gTabs.AddTo(3, new UiLabel("Make the pose, keep it still, and press RECORD: three seconds later the hand's finger curls and palm / thumb directions become a named gesture (plugin my-poses). Bind it in BIND.", t.SmallSize) { Rect = rec.Next(0.06f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _gTabs.AddTo(3, new UiSegmented("HAND", new[] { "LEFT", "RIGHT" }, _gRecordHand, i => _gRecordHand = i) { Rect = rec.Next(0.032f), LabelWidth = 0.09f });
            _gRecordName = _gTabs.AddTo(3, new UiTextField("NAME", "", null, "e.g. finger-gun") { Rect = rec.Next(0.032f), CaptionWidth = 0.09f });
            _gTabs.AddTo(3, new UiSlider("TOLERANCE", 0.1f, 0.6f, _gTolerance, v => _gTolerance = v) { Rect = rec.Next(0.032f), LabelWidth = 0.12f });
            _gTabs.AddTo(3, new UiSlider("HOLD", 0f, 1f, _gHold, v => _gHold = v) { Rect = rec.Next(0.032f), LabelWidth = 0.12f, Format = "0.00 s" });
            AddRow(_gTabs, 3, rec.Next(t.ButtonHeight), new (string, Action)[] { ("● RECORD IN 3 s", StartRecording) }, primary: 0);
            _gRecordStatus = _gTabs.AddTo(3, new UiLabel("", t.SmallSize) { Rect = rec.Rest(), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            // plugins have one home (MQH > PLUGINS: + NEW PLUGIN…, FOLDER); here only where to find it
            var tpl = new UiStack(new Rect(area.x + area.width * 0.57f, area.y, area.width * 0.43f, area.height), t.Gap);
            _gTabs.AddTo(3, new UiLabel("Plugins: MQH > PLUGINS (+ NEW, or ask an agent)", t.SmallSize) { Rect = tpl.Next(0.04f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });

            _gTabs.OnChanged = _ => RefreshGesturesPanel();
            RefreshGesturesPanel();
        }

        private static UiButton[] AddRow(UiTabs tabs, int page, Rect row, (string label, Action act)[] items, int primary = -1, int danger = -1)
        {
            var t = UiTheme.Current;
            float bw = (row.width - t.Gap * (items.Length - 1)) / items.Length;
            var buttons = new UiButton[items.Length];
            for (int i = 0; i < items.Length; i++)
                buttons[i] = tabs.AddTo(page, new UiButton(items[i].label, items[i].act, i == primary ? UiButtonStyle.Primary : i == danger ? UiButtonStyle.Danger : UiButtonStyle.Normal)
                { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height) });
            return buttons;
        }

        /// A group header row in ALL (YOURS, SYSTEM, EXAMPLES); EXAMPLES folds and unfolds on a tap.
        private sealed class GestureGroupRow { public string Name; }
        /// An example plugin's row in ALL > EXAMPLES.
        private sealed class GestureExampleRow { public string Id; }

        /// Short roles of the built-ins in ALL > SYSTEM.
        private static string BuiltInRole(string n)
        {
            switch (n)
            {
                case "pinch": return "click";
                case "grab": return "move · spin";
                case "point": return "ray";
                case "open": return "open hand";
                case "palm-up": return "palm up";
                case "clap": return "two hands";
                case "select": return "select tool";
                default: return "";
            }
        }

        /// The example a gesture comes from when that example is installed and untouched, else null (then it is YOURS).
        private string ExampleOf(GestureEngine.CustomInfo c)
        {
            if (c?.Owner == null || !c.Owner.StartsWith("plugin/")) return null;
            string id = c.Owner.Substring(7);
            return IsUntouchedExample(id) ? id : null;
        }

        /// The rows of ALL. Public for the self-test.
        public List<UiListItem> GestureRows()
        {
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            var custom = Gestures.Custom.OrderBy(x => x.Name).ToList();
            var yours = custom.Where(c => ExampleOf(c) == null).ToList();
            UiListItem Header(string name, string title) => new UiListItem { Icon = "", Title = title, TitleColor = t.TextAccent, Tag = new GestureGroupRow { Name = name } };
            UiListItem Row(GestureEngine.CustomInfo c, int indent) => new UiListItem
            {
                Icon = c.Kind == "pose" ? "✋" : "ƒ",
                IconColor = c.Kind == "pose" ? t.Violet : t.Warn,
                Title = c.Name,
                Sub = (c.Kind == "pose" ? "pose · " : "lua · ") + c.Owner.Replace("plugin/", ""),
                Badge = BindingBadge(c.Name),
                BadgeColor = t.Ok,
                Indent = indent,
                Tag = c.Name,
            };

            items.Add(Header("YOURS", $"YOURS  ({yours.Count})"));
            if (yours.Count == 0) items.Add(new UiListItem { Icon = "", Title = "none · RECORD a pose or ASK AN AGENT", TitleColor = t.TextDim, Tag = null });
            foreach (var c in yours) items.Add(Row(c, 0));

            items.Add(Header("SYSTEM", $"SYSTEM  ({GestureEngine.Names.Length})"));
            foreach (var n in GestureEngine.Names)
                items.Add(new UiListItem { Icon = "◆", IconColor = t.Accent, Title = n, Sub = BuiltInRole(n), Badge = BindingBadge(n), BadgeColor = t.Ok, Tag = n });

            var ids = GestureExampleIds.ToList();
            var exHeader = Header("EXAMPLES", $"EXAMPLES  ({ids.Count})");
            exHeader.Icon = _gExamplesOpen ? "▾" : "▸"; // folds like THREADS' projects
            items.Add(exHeader);
            if (_gExamplesOpen)
                foreach (var id in ids)
                {
                    bool installed = Scripts.Find("plugin/" + id) != null;
                    var names = GestureNamesIn(ExamplePlugins[id]);
                    items.Add(new UiListItem
                    {
                        Icon = installed ? "▣" : "+",
                        IconColor = installed ? t.Ok : t.TextDim,
                        Title = id,
                        Sub = string.Join(" ", names),
                        Badge = installed ? "ADDED" : "+ ADD",
                        BadgeColor = installed ? t.Ok : t.Accent,
                        Tag = new GestureExampleRow { Id = id },
                    });
                    if (installed)
                        foreach (var c in custom.Where(c => ExampleOf(c) == id)) items.Add(Row(c, 1));
                }
            return items;
        }

        private void GestureRowPicked(UiListItem item)
        {
            switch (item?.Tag)
            {
                case string name: _gSelected = name; _gExample = null; break;
                case GestureExampleRow ex: _gExample = ex.Id; break;
                case GestureGroupRow g when g.Name == "EXAMPLES": _gExamplesOpen = !_gExamplesOpen; RefreshGesturesPanel(); return;
                default: RestoreGesturePick(); return; // a header or the hint row: the pick stays where it was
            }
            ShowGestureDetail();
            RefreshBindTarget();
        }

        /// Fold or unfold ALL > EXAMPLES (also the self-test).
        public void SetGestureExamplesOpen(bool open) { _gExamplesOpen = open; RefreshGesturesPanel(); }

        private void AddPickedExample()
        {
            if (_gExample == null) return;
            string id = _gExample;
            var err = InstallGestureExample(id);
            if (err != null) { Toast($"Example {id}: {err}", "error"); return; }
            Toast($"Example {id} added: {string.Join(", ", GestureNamesIn(ExamplePlugins[id]))}", "ok");
        }

        /// Highlights the row of the current pick after the list changed (headers move rows around).
        private void RestoreGesturePick()
        {
            if (_gList == null) return;
            int idx = -1;
            for (int i = 0; i < _gList.Items.Count && idx < 0; i++)
            {
                var tag = _gList.Items[i].Tag;
                if (_gExample != null ? tag is GestureExampleRow ex && ex.Id == _gExample : tag is string n && n == _gSelected) idx = i;
            }
            if (idx >= 0) _gList.SelectIndex(idx, notify: false);
            else _gList.SelectedIndex = -1;
        }

        /// True when the gesture is one of YOURS (from a plugin or script that is not an untouched example).
        private bool IsYoursGesture(string name)
        {
            var c = Gestures.Custom.FirstOrDefault(x => x.Name == name);
            return c != null && !Gestures.IsBuiltIn(name) && ExampleOf(c) == null;
        }

        /// The ALL buttons follow the pick: TEST / BIND for any gesture, SOURCE and DELETE POSE only for YOURS,
        /// + ADD only for an example that is not installed yet.
        private void UpdateGestureButtons()
        {
            if (_gTestBtn == null) return;
            bool gesture = _gExample == null && !string.IsNullOrEmpty(_gSelected);
            bool yours = gesture && IsYoursGesture(_gSelected);
            var pose = yours ? Gestures.GetPose(_gSelected) : null;
            _gTestBtn.SetEnabled(gesture);
            _gBindBtn.SetEnabled(gesture);
            _gSourceBtn.SetEnabled(yours);
            _gDeleteBtn.SetEnabled(pose != null && !string.IsNullOrEmpty(pose.Source) && File.Exists(pose.Source));
            _gAddBtn.SetEnabled(_gExample != null && Scripts.Find("plugin/" + _gExample) == null);
        }

        public void RefreshGesturesPanel()
        {
            if (_gList == null || Gestures == null) return;
            var t = UiTheme.Current;
            if (_gExample != null && !_gExamplesOpen) _gExample = null;
            _gList.SetItems(GestureRows(), keepScroll: true);
            RestoreGesturePick();
            ShowGestureDetail();
            RefreshBindTarget();
            _gBindings?.SetItems(Settings.bindings.Select(b => new UiListItem
            {
                Icon = b.enabled ? "●" : "○",
                IconColor = b.enabled ? t.Ok : t.TextDim,
                Title = $"{b.gesture}  →  {b.action}",
                Sub = b.hand == "any" ? "" : b.hand,
                Badge = b.enabled ? "ON" : "OFF",
                BadgeColor = b.enabled ? t.Ok : t.TextDim,
            }), keepScroll: true);
            RefreshPluginList();
        }

        private string BindingBadge(string gesture)
        {
            int n = Settings.bindings.Count(b => b.gesture == gesture && b.enabled);
            return n == 0 ? "" : n + " ⇢";
        }

        private void ShowGestureDetail()
        {
            if (_gDetail == null) return;
            var t = UiTheme.Current;
            UpdateGestureButtons();
            if (_gExample != null) { ShowExampleDetail(_gExample); return; }
            string name = _gSelected ?? "";
            string what;
            var pose = Gestures.GetPose(name);
            var info = Gestures.Custom.FirstOrDefault(x => x.Name == name);
            if (Gestures.IsBuiltIn(name)) what = BuiltInHelp(name);
            else if (pose != null) what = $"<b>pose</b> from {pose.Owner.Replace("plugin/", "plugin ")}\n{pose.Summary()}" + (string.IsNullOrEmpty(pose.Description) ? "" : "\n" + pose.Description);
            else if (info != null) what = $"<b>Lua detector</b> ({info.Hand} hand) from {info.Owner.Replace("plugin/", "plugin ")}\n{info.Description}";
            else what = "unknown gesture";
            var binds = Settings.bindings.Where(b => b.gesture == name).Select(b => $"  {(b.enabled ? "●" : "○")} {b.hand} {b.phase} → {b.action}").ToList();
            bool active = Gestures.IsActive(name);
            _gDetail.Set($"<size=130%><b>{name}</b></size>  {(active ? "<color=#4DFFA6>● ACTIVE</color>" : "")}\n{what}\n\n<color=#7FA9BF>BINDINGS</color>\n" + (binds.Count == 0 ? "  none: BIND → to add one" : string.Join("\n", binds)) +
                "\n\n<color=#7FA9BF>LUA</color>\n  gesture.on(\"" + name + "\", function(e) … end)", t.Text);
        }

        private void ShowExampleDetail(string id)
        {
            var t = UiTheme.Current;
            var files = ExamplePlugins[id];
            string desc = "";
            try { desc = (string)Newtonsoft.Json.Linq.JObject.Parse(files["plugin.json"])["description"] ?? ""; } catch (Exception) { }
            bool installed = Scripts.Find("plugin/" + id) != null;
            _gDetail.Set($"<size=130%><b>{id}</b></size>  <color=#7FA9BF>EXAMPLE</color>\n{desc}\n\n<color=#7FA9BF>GESTURES</color>\n  " + string.Join("\n  ", GestureNamesIn(files)) + "\n\n" +
                (installed ? "<color=#4DFFA6>● added</color> in /mnt/quest/plugins/" + id + ": bind its gestures in BIND; remove it in MQH > PLUGINS."
                           : "Not installed: nothing runs. + ADD puts it in /mnt/quest/plugins/" + id + "."), t.Text);
        }

        private static string BuiltInHelp(string n)
        {
            switch (n)
            {
                case "pinch": return "<b>built-in</b>: thumb and index tips together (OVR pinch strength, hysteresis). Clicks with the hand ray.";
                case "grab": return "<b>built-in</b>: all four fingers curled (fist). Moves panels (closed-hand mode) and spins the carousel.";
                case "point": return "<b>built-in</b>: index straight, the other fingers curled.";
                case "open": return "<b>built-in</b>: all four fingers straight.";
                case "palm-up": return "<b>built-in</b>: open hand with the palm facing up.";
                case "clap": return "<b>built-in</b>, two hands: palms meet fast (phase tap). Sequences: gesture.sequence({\"clap\",\"clap\"}, 0.8, fn).";
                case "select": return "<b>built-in</b>: the select tool drew a rectangle (detail: panel x y w h).";
                default: return "<b>built-in</b>";
            }
        }

        private void RefreshBindTarget()
        {
            _gBindTarget?.Set($"<color=#7FA9BF>GESTURE</color> <b>{_gSelected}</b>  →  <color=#7FA9BF>ACTION</color> <b>{_gAction.action}</b>");
        }

        private void TestGesture(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            float now = Time.unscaledTime;
            if (name == "clap") { OnGesture(new GestureEvent { Name = name, Hand = "both", Phase = "tap", Time = now }); return; }
            OnGesture(new GestureEvent { Name = name, Hand = "right", Phase = "start", Time = now, Detail = "test" });
            After(0.4f, () => OnGesture(new GestureEvent { Name = name, Hand = "right", Phase = "end", Time = Time.unscaledTime, Detail = "test" }));
            Toast("Simulated " + name, "info");
        }

        private void ShowGestureSource()
        {
            var pose = Gestures.GetPose(_gSelected);
            var info = Gestures.Custom.FirstOrDefault(x => x.Name == _gSelected);
            string owner = pose?.Owner ?? info?.Owner;
            if (owner == null || !IsYoursGesture(_gSelected)) return; // SOURCE is for YOURS only
            var e = Scripts.Find(owner);
            if (e == null) return;
            string linux = e.Kind == "plugin" ? "/mnt/quest/plugins/" + e.Name : "/mnt/quest/scripts/" + e.Name + ".lua";
            TerminalSend(e.Kind == "plugin" ? $"cd {GitService.Quote(linux)} && ls -R\r" : $"less {GitService.Quote(linux)}\r");
        }

        private void DeleteSelectedPose()
        {
            var pose = _gExample == null && IsYoursGesture(_gSelected) ? Gestures.GetPose(_gSelected) : null;
            if (pose == null || string.IsNullOrEmpty(pose.Source) || !File.Exists(pose.Source)) return; // YOURS poses only (the button is off otherwise)
            Modal("DELETE POSE", $"Delete the pose file of '{pose.Name}'?", new[] { "DELETE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                try { File.Delete(pose.Source); } catch (Exception ex) { Toast(ex.Message, "error"); }
                Scripts.Scan();
                RefreshGesturesPanel();
            });
        }

        private float _gLiveTextAt;

        /// LIVE tab: finger curls, palm and thumb directions, active gestures, recent events, detector cost.
        public void RefreshGestureLive()
        {
            if (_gHandText[0] == null || Gestures == null) return;
            // the bars every frame; the texts (strings built, LINQ over the recent gestures) 10 times a second
            bool texts = !Application.isPlaying || Time.unscaledTime >= _gLiveTextAt;
            if (texts) _gLiveTextAt = Time.unscaledTime + 0.1f;
            for (int h = 0; h < 2; h++)
            {
                var s = h == 0 ? Gestures.Left : Gestures.Right;
                for (int i = 0; i < 5; i++) _gBars[h, i]?.Set(s.Tracked ? 1f - s.Fingers[i] : 0f);
                _gBars[h, 5]?.Set(s.Tracked ? s.Pinch : 0f);
                if (!texts) continue;
                if (!s.Tracked) { _gHandText[h].Set("not tracked"); continue; }
                var v = Gestures.ToHeadLocal(s.Velocity);
                var act = Gestures.ActiveOn(s);
                _gHandText[h].Set($"palm {Gestures.Nearest(s.PalmNormal, s.Frame.Palm)} · thumb {Gestures.Nearest(s.ThumbDir, s.Frame.Palm)}\nvel x{v.x:+0.0;-0.0} y{v.y:+0.0;-0.0} z{v.z:+0.0;-0.0} m/s\n<color=#4DFFA6>{string.Join(" ", act)}</color>");
            }
            if (!texts) return;
            _gEvents?.Set("RECENT\n" + string.Join("\n", Gestures.Recent.Reverse().Take(7).Select(g => $"{g.Time,7:F2} {g.Name,-12} {g.Hand,-5} {g.Phase}")));
            _gCost?.Set($"bars: 1 = straight\nLua detectors: {Scripts.DetectorCount}\nlast pass {Scripts.DetectMs:F3} ms\n" +
                        string.Join("\n", Scripts.DetectorCosts().Take(5).Select(d => $"{d.name} {d.micros:F0} µs")));
        }
    }
}
