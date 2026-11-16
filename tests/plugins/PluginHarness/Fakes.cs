// The fake app: IScriptServices that records every call a plugin makes, plus the window and tile bookkeeping the
// shell does (open set, built windows, deferred open/close events). The scenario in Program.cs drives it.
using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Hands;
using MetaCodex.Scripting;
using Newtonsoft.Json.Linq;

namespace PluginHarness
{
    /// One plugin window as the app has it.
    public sealed class WindowRec
    {
        public string Id, Json;
        public JObject Spec;
        /// The app has built it (it was sent with open = true, or opened since): only then LIVE texts can be set.
        public bool Built, Open;
        /// Full specs received (PluginWindow calls) and LIVE text updates accepted.
        public int Builds, TextUpdates, OpenEvents, CloseEvents;
    }

    public sealed class Notice
    {
        public string Text, Tone, Sound, Speak;
        public Action OnTap;
    }

    public sealed class FakeServices : IScriptServices
    {
        /// Wall clock, seconds since 1970 UTC. The scenario advances it with the app time.
        public double Clock;
        public int Events;                      // window events delivered to the host (scenario and deferred)
        public readonly Dictionary<string, WindowRec> Windows = new Dictionary<string, WindowRec>();
        public readonly List<string> Removed = new List<string>();
        public List<PluginTile> Tiles = new List<PluginTile>();
        public readonly List<(string text, string kind)> Toasts = new List<(string, string)>();
        public readonly List<Notice> Notices = new List<Notice>();
        public readonly List<string> Sounds = new List<string>();
        public readonly List<string> Said = new List<string>();
        public readonly List<string> VoiceRuns = new List<string>();
        public readonly List<string> Actions = new List<string>();
        public readonly List<string> PanelCalls = new List<string>();
        public readonly List<string> LogLines = new List<string>();
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
        /// Things the harness found wrong while the host called in (invalid specs): the scenario turns them into FAIL lines.
        public readonly List<string> Problems = new List<string>();

        private readonly HashSet<string> _problemKeys = new HashSet<string>();
        private readonly HashSet<string> _checked = new HashSet<string>();
        private readonly Queue<(string window, string type)> _pending = new Queue<(string, string)>();

        public FakeServices(double clock) { Clock = clock; }

        private void Problem(string text)
        {
            if (_problemKeys.Add(text)) Problems.Add(text);
        }

        // ------------------------------------------------------------------ the app's deferred events

        /// Delivers the open / close events the app would send on its next frame (the host never makes them itself).
        public void Pump(ScriptHost host)
        {
            int n = _pending.Count;
            for (int i = 0; i < n; i++)
            {
                var (window, type) = _pending.Dequeue();
                if (!Windows.TryGetValue(window, out var w)) continue;
                if (type == "open") w.OpenEvents++; else w.CloseEvents++;
                Events++;
                host.WindowEvent(window, type, "", "");
            }
        }

        private void SetOpen(WindowRec w, bool want)
        {
            if (w.Open == want) return;
            w.Open = want;
            if (want) w.Built = true;
            _pending.Enqueue((w.Id, want ? "open" : "close"));
        }

        // ------------------------------------------------------------------ plugin windows and tiles

        public double WallClock() => Clock;

        public void PluginWindow(string windowId, string specJson, bool open)
        {
            JObject spec;
            try { spec = JObject.Parse(specJson); }
            catch (Exception ex) { Problem("window " + windowId + ": the spec is not JSON: " + ex.Message); return; }
            if (_checked.Add(specJson))
            {
                var errors = new List<string>();
                var warnings = new List<string>();
                ViewSpecCheck.Check(spec, errors, warnings);
                foreach (var m in errors) Problem("window " + windowId + ": spec error: " + m);
                foreach (var m in warnings) Problem("window " + windowId + ": spec warning: " + m);
                int n = (spec["components"] as JArray)?.Count ?? 0;
                if (n > ViewSpecCheck.MaxComponents) Problem("window " + windowId + ": " + n + " components (at most " + ViewSpecCheck.MaxComponents + ")");
            }
            if (!Windows.TryGetValue(windowId, out var w)) { w = new WindowRec { Id = windowId }; Windows[windowId] = w; }
            w.Spec = spec;
            w.Json = specJson;
            w.Builds++;
            if (open) { w.Built = true; SetOpen(w, true); }
        }

        public bool PluginWindowText(string windowId, IDictionary<string, string> texts)
        {
            if (!Windows.TryGetValue(windowId, out var w) || !w.Built) return false;
            w.TextUpdates++;
            if (w.Spec["components"] is JArray comps)
                foreach (var item in comps)
                {
                    if (!(item is JObject co)) continue;
                    string id = (string)co["id"];
                    if (id == null || !texts.TryGetValue(id, out var text)) continue;
                    if (co["markdown"] != null && co["text"] == null) co["markdown"] = text; else co["text"] = text;
                }
            return true;
        }

        public bool PluginWindowShow(string windowId, int mode)
        {
            if (!Windows.TryGetValue(windowId, out var w)) return false;
            SetOpen(w, mode == 1 || mode == 2 && !w.Open);
            return w.Open;
        }

        public bool PluginWindowIsOpen(string windowId) => Windows.TryGetValue(windowId, out var w) && w.Open;

        public void PluginWindowRemove(string windowId)
        {
            Windows.Remove(windowId);
            Removed.Add(windowId);
        }

        public void SetPluginTiles(IReadOnlyList<PluginTile> tiles) => Tiles = tiles.ToList();

        public void PlaySound(string name) => Sounds.Add(name);

        public void Notify(string text, string tone, string sound, string speak, Action onTap) =>
            Notices.Add(new Notice { Text = text, Tone = tone, Sound = sound, Speak = speak, OnTap = onTap });

        // ------------------------------------------------------------------ the rest of the interface

        public void Toast(string text, string kind) => Toasts.Add((text, kind));
        public bool OpenPanel(string id) { PanelCalls.Add("open " + id); return true; }
        public bool ClosePanel(string id) { PanelCalls.Add("close " + id); return true; }
        public bool TogglePanel(string id) { PanelCalls.Add("toggle " + id); return true; }
        public IEnumerable<string> PanelIds => new string[0];
        public void Modal(string title, string text, string[] buttons, Action<int> onResult) => PanelCalls.Add("modal " + title);
        public void TerminalSend(string text) => Actions.Add("terminal " + text);
        public void TerminalNew() => Actions.Add("terminal new");
        public void OpenBrowser(string url) => Actions.Add("browser " + url);
        public void OpenKeyboard() => Actions.Add("keyboard");
        public void SetSelectionMode(bool on) => Actions.Add("selection " + on);
        public string GetSetting(string key) => Settings.TryGetValue(key, out var v) ? v : null;
        public bool SetSetting(string key, string value) { Settings[key] = value; return true; }
        public HandState Hand(string hand) => hand == "left" ? Engine.Left : Engine.Right;
        /// The stand-in gesture engine (Stubs.cs): the scenario sets the hands and ticks it.
        public readonly GestureEngine Engine = new GestureEngine();
        public GestureEngine Gestures => Engine;
        /// The scenario passes the projects folder to the host itself.
        public string PluginProjectsDir(string scriptsDir) => null;
        public string RunAction(string action) { Actions.Add(action); return null; }
        public VoiceCommands Voice { get; } = new VoiceCommands();
        public void VoiceSay(string text) => Said.Add(text);
        public void VoiceRun(string text) => VoiceRuns.Add(text);
        public void VoiceListen() => Actions.Add("listen");
        public void Log(string line) => LogLines.Add(line);
    }
}
