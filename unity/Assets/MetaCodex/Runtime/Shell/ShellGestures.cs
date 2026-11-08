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
        public static readonly (string label, string action)[] PresetActions =
        {
            ("Open terminals", "toggle:terminals"), ("Open files", "toggle:files"), ("Open git", "toggle:git"),
            ("Open agents", "toggle:agents"), ("Open threads", "toggle:threads"), ("Open web", "toggle:browser"),
            ("Open scripts", "toggle:scripts"), ("Open gestures", "toggle:gestures"), ("Open settings", "toggle:settings"),
            ("Open vault", "toggle:vault"), ("New terminal", "terminal:new"), ("System keyboard", "keyboard"),
            ("Select tool on/off", "select"), ("Recenter", "recenter"), ("Float layout", "layout:float"),
            ("Fixed layout", "layout:fixed"), ("Spin carousel left", "spin:-40"), ("Spin carousel right", "spin:40"),
            ("Ctrl-C in terminal", "type:\u0003"), ("git status in terminal", "run:git status"), ("Toast hello", "toast:Hello from a gesture"),
        };

        private UiTabs _gTabs;
        private UiList _gList, _gBindings, _gActions, _gPlugins;
        private UiLabel _gDetail, _gBindTarget, _gRecordStatus, _gEvents, _gPluginDetail, _gCost;
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

        private void ApplyBindings(GestureEvent e)
        {
            if (Settings?.bindings == null || Settings.bindings.Count == 0) return;
            foreach (var b in Settings.bindings.ToList())
            {
                if (!b.enabled || b.gesture != e.Name) continue;
                if (b.hand != "any" && b.hand != e.Hand) continue;
                bool phaseOk = b.phase == "any" || (b.phase == "end" ? e.Phase == "end" : e.Phase == "start" || e.Phase == "tap");
                if (!phaseOk) continue;
                var err = RunAction(b.action);
                Log($"binding {b.gesture} -> {b.action}: {err ?? "ok"}");
                if (err != null) Toast($"Binding {b.gesture}: {err}", "warn");
            }
        }

        public void AddBinding(string gesture, string action, string hand = "any", string phase = "start")
        {
            Settings.bindings.RemoveAll(b => b.gesture == gesture && b.hand == hand && b.action == action);
            Settings.bindings.Add(new GestureBinding { gesture = gesture, action = action, hand = hand, phase = phase });
            OnSettingsChanged();
            RefreshGesturesPanel();
        }

        // ------------------------------------------------------------------ starter plugins

        /// First run: two example plugins builders can read and copy (data-only poses; Lua detectors).
        private void EnsureStarterPlugins()
        {
            try
            {
                var marker = Path.Combine(Scripts.PluginsDir, ".starter-installed");
                if (File.Exists(marker)) return;
                Directory.CreateDirectory(Scripts.PluginsDir);
                foreach (var plugin in StarterPlugins)
                {
                    var dir = Path.Combine(Scripts.PluginsDir, plugin.Key);
                    if (Directory.Exists(dir)) continue;
                    foreach (var f in plugin.Value)
                    {
                        var path = Path.Combine(dir, f.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, f.Value);
                    }
                }
                File.WriteAllText(marker, "starter plugins installed " + DateTime.UtcNow.ToString("o") + "\nDelete a plugin folder to remove it; this file stops it from coming back.\n");
            }
            catch (Exception ex) { Log("starter plugins not installed: " + ex.Message); }
        }

        private static string PoseFile(string name, string desc, string fingers, string extra = "") =>
            "{\n  \"name\": \"" + name + "\",\n  \"description\": \"" + desc + "\",\n  \"hand\": \"any\",\n  \"fingers\": " + fingers + ",\n  \"tolerance\": 0.35," + extra + "\n  \"hold\": 0.15,\n  \"cooldown\": 0.5\n}\n";

        public static readonly Dictionary<string, Dictionary<string, string>> StarterPlugins = new Dictionary<string, Dictionary<string, string>>
        {
            ["core-poses"] = new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"core-poses\",\n  \"name\": \"Core poses\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"Thumbs up / down, victory, horns and call-me as named gestures (data only: poses/*.json)\"\n}\n",
                ["poses/thumbs-up.json"] = PoseFile("thumbs-up", "fist, thumb pointing up", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 1 }", "\n  \"thumbPointing\": \"up\","),
                ["poses/thumbs-down.json"] = PoseFile("thumbs-down", "fist, thumb pointing down", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 1 }", "\n  \"thumbPointing\": \"down\","),
                ["poses/victory.json"] = PoseFile("victory", "index and middle up, the rest curled", "{ \"index\": 0, \"middle\": 0, \"ring\": 1, \"little\": 1 }"),
                ["poses/horns.json"] = PoseFile("horns", "index and little up, middle and ring curled", "{ \"index\": 0, \"middle\": 1, \"ring\": 1, \"little\": 0 }"),
                ["poses/call-me.json"] = PoseFile("call-me", "thumb and little out, the rest curled", "{ \"thumb\": 0, \"index\": 1, \"middle\": 1, \"ring\": 1, \"little\": 0 }"),
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

        public const string PluginTemplateLua =
@"-- description: my gesture plugin (edit me: the app reloads this file within a second)
-- Pose files in poses/*.json define named hand shapes; gesture.define adds Lua detectors;
-- gesture.on / gesture.sequence react to any gesture. Skill for agents: quest-gesture-plugins.

-- a detector: both palms facing up for a moment
gesture.define(""both-palms-up"", { hand = ""both"", hold = 0.3, cooldown = 1.0, description = ""both hands open, palms up"" }, function(l, r)
  return l.open and r.open and l.palmFacing == ""up"" and r.palmFacing == ""up""
end)

gesture.on(""both-palms-up"", function(e)
  if e.phase == ""start"" then
    ui.toast(""Both palms up"")
    ui.toggle(""threads"")
  end
end)
";

        /// Scaffolds a plugin folder from the template (Gestures panel > NEW PLUGIN, control port).
        public ScriptHost.Entry NewPlugin(string id)
        {
            id = ScriptHost.Sanitize(id);
            return Scripts.SavePlugin(id, new Dictionary<string, string>
            {
                ["plugin.json"] = "{\n  \"id\": \"" + id + "\",\n  \"name\": \"" + id + "\",\n  \"version\": \"0.1.0\",\n  \"kind\": \"gesture\",\n  \"description\": \"both palms up toggles the threads panel\"\n}\n",
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

        private void BuildGestures()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("gestures", "GESTURES · STUDIO", 0.80f, 0.62f);
            var c = p.Content;
            _gTabs = p.Add(new UiTabs("GESTURES", "LIVE", "BIND", "RECORD", "PLUGINS") { Rect = new Rect(c.x, c.y, c.width, t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);
            float leftW = 0.30f;
            var right = new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height);

            // GESTURES: every gesture the engine knows, with its source and live state
            _gList = _gTabs.AddTo(0, new UiList(0.030f, (item, idx) => { _gSelected = item.Tag as string; ShowGestureDetail(); }) { Rect = new Rect(area.x, area.y, leftW, area.height) });
            var rs = new UiStack(right, t.Gap);
            _gDetail = _gTabs.AddTo(0, new UiLabel("", t.SmallSize * 1.1f) { Rect = rs.Next(area.height - t.ButtonHeight * 2 - t.Gap * 3), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            var row1 = rs.Next(t.ButtonHeight);
            var acts1 = new (string, Action)[]
            {
                ("TEST ▶", () => TestGesture(_gSelected)),
                ("BIND →", () => { _gTabs.Select(2); RefreshGesturesPanel(); }),
                ("SOURCE", ShowGestureSource),
            };
            AddRow(_gTabs, 0, row1, acts1);
            var row2 = rs.Next(t.ButtonHeight);
            AddRow(_gTabs, 0, row2, new (string, Action)[]
            {
                ("DELETE POSE", DeleteSelectedPose),
                ("⟳ REFRESH", RefreshGesturesPanel),
            }, danger: 0);

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
            p.Tick += _ => { if (_gTabs != null && _gTabs.Selected == 1) RefreshGestureLive(); };

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
            var tpl = new UiStack(new Rect(area.x + area.width * 0.57f, area.y, area.width * 0.43f, area.height), t.Gap);
            _gTabs.AddTo(3, new UiLabel("BUILD A PLUGIN", t.SmallSize) { Rect = tpl.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gTabs.AddTo(3, new UiLabel("A plugin is a folder in /mnt/quest/plugins: plugin.json, main.lua (detectors, handlers) and poses/*.json. Ask your agent: “make a gesture plugin that …” (skill quest-gesture-plugins).", t.SmallSize) { Rect = tpl.Next(0.09f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            AddRow(_gTabs, 3, tpl.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("+ NEW PLUGIN…", () => UiKeyboard.Edit("my-gestures", false, null, id => { if (string.IsNullOrWhiteSpace(id)) return; var e = NewPlugin(id); Toast($"Plugin {e.Name} created in /mnt/quest/plugins/{e.Name}", "ok"); RefreshGesturesPanel(); })),
            });
            AddRow(_gTabs, 3, tpl.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("OPEN IN TERMINAL", () => TerminalSend("cd /mnt/quest/plugins && ls -la\r")),
            });

            // PLUGINS
            _gPlugins = _gTabs.AddTo(4, new UiList(0.032f, (item, idx) => ShowPluginDetail()) { Rect = new Rect(area.x, area.y, leftW + 0.06f, area.height) });
            var pr = new UiStack(new Rect(area.x + leftW + 0.06f + t.Gap * 2, area.y, area.width - leftW - 0.06f - t.Gap * 2, area.height), t.Gap);
            _gPluginDetail = _gTabs.AddTo(4, new UiLabel("", t.SmallSize) { Rect = pr.Next(area.height - t.ButtonHeight * 2 - t.Gap * 3), Wrap = true, UseMono = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            AddRow(_gTabs, 4, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("ENABLE / DISABLE", () => { var e = SelectedPlugin(); if (e != null) Scripts.SetEnabled(e.Key, !e.Enabled); RefreshGesturesPanel(); }),
                ("⟳ RELOAD", () => { Scripts.ReloadAll(); RefreshGesturesPanel(); }),
            });
            AddRow(_gTabs, 4, pr.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("FOLDER", () => { var e = SelectedPlugin(); if (e != null) TerminalSend($"cd {GitService.Quote("/mnt/quest/plugins/" + e.Name)} && ls -la\r"); }),
                ("DELETE", () => { var e = SelectedPlugin(); if (e != null) Modal("DELETE PLUGIN", $"Delete the folder of plugin {e.Name}?", new[] { "DELETE", "CANCEL" }, i => { if (i == 0) { Scripts.Delete(e.Key); RefreshGesturesPanel(); } }); }),
            }, danger: 1);
            _gTabs.OnChanged = _ => RefreshGesturesPanel();
            RefreshGesturesPanel();
        }

        private static void AddRow(UiTabs tabs, int page, Rect row, (string label, Action act)[] items, int primary = -1, int danger = -1)
        {
            var t = UiTheme.Current;
            float bw = (row.width - t.Gap * (items.Length - 1)) / items.Length;
            for (int i = 0; i < items.Length; i++)
                tabs.AddTo(page, new UiButton(items[i].label, items[i].act, i == primary ? UiButtonStyle.Primary : i == danger ? UiButtonStyle.Danger : UiButtonStyle.Normal)
                { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height) });
        }

        private ScriptHost.Entry SelectedPlugin() =>
            _gPlugins != null && _gPlugins.SelectedIndex >= 0 && _gPlugins.SelectedIndex < _gPlugins.Items.Count ? _gPlugins.Items[_gPlugins.SelectedIndex].Tag as ScriptHost.Entry : null;

        public void RefreshGesturesPanel()
        {
            if (_gList == null || Gestures == null) return;
            var t = UiTheme.Current;
            var items = new List<UiListItem>();
            foreach (var n in GestureEngine.Names)
                items.Add(new UiListItem { Icon = "◆", IconColor = t.Accent, Title = n, Sub = "built-in", Badge = BindingBadge(n), BadgeColor = t.Ok, Tag = n });
            foreach (var cinfo in Gestures.Custom.OrderBy(x => x.Name))
                items.Add(new UiListItem
                {
                    Icon = cinfo.Kind == "pose" ? "✋" : "ƒ",
                    IconColor = cinfo.Kind == "pose" ? t.Violet : t.Warn,
                    Title = cinfo.Name,
                    Sub = (cinfo.Kind == "pose" ? "pose · " : "lua · ") + cinfo.Owner.Replace("plugin/", ""),
                    Badge = BindingBadge(cinfo.Name),
                    BadgeColor = t.Ok,
                    Tag = cinfo.Name,
                });
            _gList.SetItems(items, keepScroll: true);
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
            _gPlugins?.SetItems(Scripts.Entries.Where(e => e.Kind == "plugin").Select(e => new UiListItem
            {
                Icon = "▣",
                IconColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                Title = e.Title.Length > 0 ? e.Title : e.Name,
                Sub = e.Name + (e.Version.Length > 0 ? " · v" + e.Version : ""),
                Badge = e.Error != null ? "ERROR" : !e.Enabled ? "OFF" : $"{e.Poses + e.Detectors} G",
                BadgeColor = e.Error != null ? t.Danger : e.Enabled ? t.Ok : t.TextDim,
                Tag = e,
            }), keepScroll: true);
            ShowPluginDetail();
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
            if (owner == null) { Toast("Built-in gesture: see Gestures.cs", "info"); return; }
            var e = Scripts.Find(owner);
            if (e == null) return;
            string linux = e.Kind == "plugin" ? "/mnt/quest/plugins/" + e.Name : "/mnt/quest/scripts/" + e.Name + ".lua";
            TerminalSend(e.Kind == "plugin" ? $"cd {GitService.Quote(linux)} && ls -R\r" : $"less {GitService.Quote(linux)}\r");
        }

        private void DeleteSelectedPose()
        {
            var pose = Gestures.GetPose(_gSelected);
            if (pose == null || string.IsNullOrEmpty(pose.Source) || !File.Exists(pose.Source)) { Toast("Only poses from files can be deleted here", "warn"); return; }
            Modal("DELETE POSE", $"Delete the pose file of '{pose.Name}'?", new[] { "DELETE", "CANCEL" }, i =>
            {
                if (i != 0) return;
                try { File.Delete(pose.Source); } catch (Exception ex) { Toast(ex.Message, "error"); }
                Scripts.Scan();
                RefreshGesturesPanel();
            });
        }

        private void ShowPluginDetail()
        {
            var e = SelectedPlugin();
            if (_gPluginDetail == null) return;
            if (e == null) { _gPluginDetail.Set("Select a plugin.\n\nPlugins live in /mnt/quest/plugins/<id>/ (plugin.json, main.lua, poses/*.json). Agents write them; the app reloads them within a second."); return; }
            var t = UiTheme.Current;
            var gestures = Gestures.Custom.Where(x => x.Owner == e.Key).Select(x => x.Name).ToList();
            _gPluginDetail.Set($"<b>{e.Title}</b> {e.Version}\n{e.Description}\n\n/mnt/quest/plugins/{e.Name}\n{(e.Enabled ? "enabled" : "disabled")} · {e.Handlers} handlers · {e.Poses} poses · {e.Detectors} detectors\n" +
                               (gestures.Count > 0 ? "gestures: " + string.Join(", ", gestures) + "\n" : "") +
                               (e.Error != null ? $"<color=#FF5468>ERROR {e.Error}</color>\n" : "") + "\n" + string.Join("\n", e.Log.Skip(Math.Max(0, e.Log.Count - 5))), e.Error != null ? t.Danger : t.TextDim);
        }

        /// LIVE tab: finger curls, palm and thumb directions, active gestures, recent events, detector cost.
        public void RefreshGestureLive()
        {
            if (_gHandText[0] == null || Gestures == null) return;
            for (int h = 0; h < 2; h++)
            {
                var s = h == 0 ? Gestures.Left : Gestures.Right;
                for (int i = 0; i < 5; i++) _gBars[h, i]?.Set(s.Tracked ? 1f - s.Fingers[i] : 0f);
                _gBars[h, 5]?.Set(s.Tracked ? s.Pinch : 0f);
                if (!s.Tracked) { _gHandText[h].Set("not tracked"); continue; }
                var v = Gestures.ToHeadLocal(s.Velocity);
                var act = Gestures.ActiveOn(s);
                _gHandText[h].Set($"palm {Gestures.Nearest(s.PalmNormal, s.Frame.Palm)} · thumb {Gestures.Nearest(s.ThumbDir, s.Frame.Palm)}\nvel x{v.x:+0.0;-0.0} y{v.y:+0.0;-0.0} z{v.z:+0.0;-0.0} m/s\n<color=#4DFFA6>{string.Join(" ", act)}</color>");
            }
            _gEvents?.Set("RECENT\n" + string.Join("\n", Gestures.Recent.Reverse().Take(7).Select(g => $"{g.Time,7:F2} {g.Name,-12} {g.Hand,-5} {g.Phase}")));
            _gCost?.Set($"bars: 1 = straight\nLua detectors: {Scripts.DetectorCount}\nlast pass {Scripts.DetectMs:F3} ms\n" +
                        string.Join("\n", Scripts.DetectorCosts().Take(5).Select(d => $"{d.name} {d.micros:F0} µs")));
        }
    }
}
