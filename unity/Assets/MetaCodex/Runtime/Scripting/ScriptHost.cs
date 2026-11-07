using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MetaCodex.Hands;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Scripting
{
    /// What scripts may do: everything goes through this interface (implemented by the shell), so the Lua
    /// sandbox never touches Unity objects directly.
    public interface IScriptServices
    {
        void Toast(string text, string kind);
        bool OpenPanel(string id);
        bool ClosePanel(string id);
        bool TogglePanel(string id);
        IEnumerable<string> PanelIds { get; }
        void Modal(string title, string text, string[] buttons, Action<int> onResult);
        void TerminalSend(string text);
        void TerminalNew();
        void OpenBrowser(string url);
        void OpenKeyboard();
        void SetSelectionMode(bool on);
        string GetSetting(string key);
        bool SetSetting(string key, string value);
        HandState Hand(string hand);
        GestureEngine Gestures { get; }
        /// A binding-style action ("open:git", "toggle:files", "run:ls", "url:https://…", …); null = ok, else the error.
        string RunAction(string action);
        /// The voice command registry (plugins add commands with voice.command).
        MetaCodex.AI.VoiceCommands Voice { get; }
        /// Speaks text with the local text-to-speech model.
        void VoiceSay(string text);
        /// Runs text through the voice pipeline as if spoken (fast path, else the local LLM).
        void VoiceRun(string text);
        /// Starts listening (push-to-talk).
        void VoiceListen();
        void Log(string line);
    }

    /// Behaviours in Lua (MoonSharp: pure C#, IL2CPP-safe), changed during a session without a rebuild.
    /// Two forms, both hot-reloaded within a second of a change:
    ///  - scripts: single `*.lua` files in one folder (/mnt/quest/scripts in the userland);
    ///  - plugins: folders in /mnt/quest/plugins/<id>/ with plugin.json, an optional main.lua and optional
    ///    poses/*.json (named hand poses). A plugin is enabled, disabled and reloaded as one unit.
    /// Each runs in its own sandbox (no io/os/load); every call has an instruction budget (a runaway loop
    /// stops that script, the app keeps its frame rate); enabled flags and `store` survive restarts.
    /// Gesture detectors (gesture.define) run as persistent coroutines a few dozen times per second.
    public sealed class ScriptHost
    {
        public sealed class Entry
        {
            public string Name, Path, Description = "";
            /// "script" (one .lua file) or "plugin" (a folder with plugin.json).
            public string Kind = "script";
            public string PluginDir, Title = "", Version = "";
            public JObject Manifest;
            public Script Script;
            public string Error;
            public bool Enabled = true;
            public DateTime Mtime;
            public int Handlers;
            public int Runs;
            public int Poses, Detectors;
            public readonly List<string> Log = new List<string>();
            internal Table Store;
            internal Table[] HandTables;
            /// State key: plugins and scripts may share a name.
            public string Key => Kind == "plugin" ? "plugin/" + Name : Name;
        }

        private sealed class Handler { public Entry Owner; public string Event; public DynValue Fn; }
        private sealed class Sequence { public Entry Owner; public string[] Steps; public float Window; public DynValue Fn; public int Next; public float Start; }
        private sealed class Timer { public Entry Owner; public int Id; public float At, Every; public DynValue Fn; }
        private sealed class Detector
        {
            public Entry Owner; public string Name, Hand; public float Hold, Cooldown; public DynValue Loop;
            public readonly MoonSharp.Interpreter.Coroutine[] Co = new MoonSharp.Interpreter.Coroutine[3];
            public double Micros; // average cost per call
        }

        public const int InstructionBudget = 200_000;
        public const int MaxDetectors = 32;
        public readonly string Dir;
        public readonly string PluginsDir;
        public IReadOnlyList<Entry> Entries => _entries;
        public event Action Changed;
        public float Now { get; private set; }
        /// Lua detector time in the last detection pass (ms), for the Gestures panel.
        public double DetectMs { get; private set; }
        public int DetectorCount => _detectors.Count;

        private readonly IScriptServices _svc;
        private readonly string _statePath;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<Handler> _handlers = new List<Handler>();
        private readonly List<Sequence> _sequences = new List<Sequence>();
        private readonly List<Timer> _timers = new List<Timer>();
        private readonly List<Detector> _detectors = new List<Detector>();
        private readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>();
        private readonly Dictionary<string, Dictionary<string, string>> _stores = new Dictionary<string, Dictionary<string, string>>();
        private float _nextScan, _nextDetect;
        private int _timerIds;
        private GestureEngine _hookedEngine;

        public ScriptHost(IScriptServices services, string dir, string pluginsDir = null)
        {
            _svc = services;
            Dir = dir;
            PluginsDir = pluginsDir ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir.TrimEnd('/', '\\')) ?? dir, "plugins");
            Directory.CreateDirectory(dir);
            try { Directory.CreateDirectory(PluginsDir); } catch (Exception) { }
            _statePath = System.IO.Path.Combine(dir, ".state.json");
            LoadState();
            Script.DefaultOptions.DebugPrint = s => _svc.Log("[lua] " + s);
        }

        private GestureEngine Engine
        {
            get
            {
                var g = _svc.Gestures;
                if (g != _hookedEngine)
                {
                    if (_hookedEngine != null) _hookedEngine.CustomTick -= OnCustomTick;
                    _hookedEngine = g;
                    if (g != null) g.CustomTick += OnCustomTick;
                }
                return g;
            }
        }

        // ------------------------------------------------------------------ loading

        /// Called every frame: runs due timers; rescans the folders once a second.
        public void Tick(float now)
        {
            Now = now;
            _ = Engine; // keeps the detector hook on the current engine
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            for (int i = 0; i < _timers.Count; i++)
            {
                var t = _timers[i];
                if (t.At > now || !t.Owner.Enabled || t.Owner.Error != null) continue;
                if (t.Every > 0f) t.At = now + t.Every; else { _timers.RemoveAt(i); i--; }
                Run(t.Owner, t.Fn);
            }
        }

        /// Loads new and changed scripts and plugins, drops deleted ones.
        public void Scan()
        {
            var seen = new HashSet<string>();
            bool changed = false;
            var files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.lua") : new string[0];
            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(f);
                seen.Add(name);
                var mtime = File.GetLastWriteTimeUtc(f);
                var e = _entries.FirstOrDefault(x => x.Kind == "script" && x.Name == name);
                if (e != null && e.Mtime == mtime) continue;
                Load(e ?? NewEntry("script", name, f), mtime);
                changed = true;
            }
            var dirs = Directory.Exists(PluginsDir) ? Directory.GetDirectories(PluginsDir) : new string[0];
            foreach (var d in dirs.OrderBy(x => x, StringComparer.Ordinal))
            {
                var manifest = System.IO.Path.Combine(d, "plugin.json");
                if (!File.Exists(manifest)) continue;
                string name = Sanitize(System.IO.Path.GetFileName(d));
                seen.Add("plugin/" + name);
                var mtime = PluginMtime(d);
                var e = _entries.FirstOrDefault(x => x.Kind == "plugin" && x.Name == name);
                if (e != null && e.Mtime == mtime) continue;
                Load(e ?? NewEntry("plugin", name, d), mtime);
                changed = true;
            }
            foreach (var e in _entries.Where(x => !seen.Contains(x.Key)).ToList())
            {
                Unload(e);
                _entries.Remove(e);
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }

        private Entry NewEntry(string kind, string name, string path)
        {
            var e = new Entry { Kind = kind, Name = name, Path = path, PluginDir = kind == "plugin" ? path : null };
            _entries.Add(e);
            return e;
        }

        /// A plugin changes when any of its files changes.
        private static DateTime PluginMtime(string dir)
        {
            var t = Directory.GetLastWriteTimeUtc(dir);
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var m = File.GetLastWriteTimeUtc(f);
                    if (m > t) t = m;
                }
            }
            catch (Exception) { }
            return t;
        }

        private void Load(Entry e, DateTime mtime)
        {
            Unload(e);
            e.Mtime = mtime;
            e.Error = null;
            e.Handlers = 0;
            e.Poses = 0;
            e.Detectors = 0;
            e.Enabled = !_enabled.TryGetValue(e.Key, out bool en) || en;
            string code = null, chunkName = e.Name + ".lua";
            if (e.Kind == "plugin")
            {
                try
                {
                    e.Manifest = JObject.Parse(File.ReadAllText(System.IO.Path.Combine(e.PluginDir, "plugin.json")));
                    e.Title = (string)e.Manifest["name"] ?? e.Name;
                    e.Version = (string)e.Manifest["version"] ?? "";
                    e.Description = (string)e.Manifest["description"] ?? "";
                }
                catch (Exception ex) { e.Error = "plugin.json: " + ex.Message; Say(e, "ERROR " + e.Error); return; }
                if (!e.Enabled) { Say(e, "disabled"); return; }
                LoadPoses(e);
                var main = System.IO.Path.Combine(e.PluginDir, (string)e.Manifest["main"] ?? "main.lua");
                if (File.Exists(main))
                {
                    try { code = File.ReadAllText(main); chunkName = e.Name + "/" + System.IO.Path.GetFileName(main); }
                    catch (Exception ex) { Fail(e, "read failed: " + ex.Message); return; }
                }
            }
            else
            {
                try { code = File.ReadAllText(e.Path); }
                catch (Exception ex) { e.Error = "read failed: " + ex.Message; return; }
                e.Description = FirstComment(code);
                if (!e.Enabled) { Say(e, "disabled"); return; }
            }
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.Options.DebugPrint = s => Say(e, s);
            e.Script = script;
            e.Store = new Table(script);
            Install(e, script);
            if (code == null) { Say(e, $"loaded ({e.Poses} poses)"); return; }
            try
            {
                var chunk = script.LoadString(code, null, chunkName);
                Run(e, chunk);
                if (e.Error == null) Say(e, $"loaded ({e.Handlers} handlers, {e.Poses} poses, {e.Detectors} detectors)");
            }
            catch (InterpreterException ex) { Fail(e, ex.DecoratedMessage ?? ex.Message); }
            catch (Exception ex) { Fail(e, ex.Message); }
        }

        private void LoadPoses(Entry e)
        {
            var eng = Engine;
            if (eng == null) return;
            var dir = System.IO.Path.Combine(e.PluginDir, (string)e.Manifest?["poses"] ?? "poses");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var p = PoseJson.Parse(JObject.Parse(File.ReadAllText(f)), e.Key, f);
                    if (string.IsNullOrEmpty(p.Name)) p.Name = System.IO.Path.GetFileNameWithoutExtension(f);
                    eng.AddPose(p);
                    e.Poses++;
                }
                catch (Exception ex) { Say(e, $"pose {System.IO.Path.GetFileName(f)} skipped: {ex.Message}"); }
            }
        }

        private void Unload(Entry e)
        {
            _handlers.RemoveAll(h => h.Owner == e);
            _sequences.RemoveAll(s => s.Owner == e);
            _timers.RemoveAll(t => t.Owner == e);
            _detectors.RemoveAll(d => d.Owner == e);
            _svc.Gestures?.RemoveOwnedBy(e.Key);
            _svc.Voice?.RemoveOwnedBy(e.Key);
            e.Script = null;
            e.HandTables = null;
        }

        private static string FirstComment(string code)
        {
            foreach (var raw in code.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith("--")) break;
                line = line.TrimStart('-').Trim();
                if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase)) return line.Substring(12).Trim();
                if (line.Length > 0) return line;
            }
            return "";
        }

        public Entry Find(string nameOrKey) =>
            _entries.FirstOrDefault(x => x.Key == nameOrKey) ?? _entries.FirstOrDefault(x => x.Name == nameOrKey);

        public void SetEnabled(string nameOrKey, bool enabled)
        {
            var e = Find(nameOrKey);
            _enabled[e != null ? e.Key : nameOrKey] = enabled;
            SaveState();
            if (e == null) return;
            Load(e, e.Kind == "plugin" ? PluginMtime(e.PluginDir) : File.Exists(e.Path) ? File.GetLastWriteTimeUtc(e.Path) : e.Mtime);
            Changed?.Invoke();
        }

        public void ReloadAll()
        {
            foreach (var e in _entries.ToList()) Load(e, e.Kind == "plugin" ? PluginMtime(e.PluginDir) : File.GetLastWriteTimeUtc(e.Path));
            Changed?.Invoke();
        }

        /// Writes a script file (agents, the control port and the Scripts panel use this); loads it at once.
        public Entry Save(string name, string code)
        {
            name = Sanitize(name);
            var path = System.IO.Path.Combine(Dir, name + ".lua");
            File.WriteAllText(path, code);
            var e = _entries.FirstOrDefault(x => x.Kind == "script" && x.Name == name) ?? NewEntry("script", name, path);
            Load(e, File.GetLastWriteTimeUtc(path));
            Changed?.Invoke();
            return e;
        }

        /// Creates (or overwrites files of) a plugin folder and loads it at once. `files` maps relative paths to text.
        public Entry SavePlugin(string id, IDictionary<string, string> files)
        {
            id = Sanitize(id);
            var dir = System.IO.Path.Combine(PluginsDir, id);
            Directory.CreateDirectory(dir);
            foreach (var kv in files)
            {
                var path = System.IO.Path.Combine(dir, kv.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(dir))) continue; // stays inside
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
            }
            var e = _entries.FirstOrDefault(x => x.Kind == "plugin" && x.Name == id) ?? NewEntry("plugin", id, dir);
            Load(e, PluginMtime(dir));
            Changed?.Invoke();
            return e;
        }

        public bool Delete(string nameOrKey)
        {
            var e = Find(nameOrKey) ?? Find(Sanitize(nameOrKey));
            if (e == null) return false;
            Unload(e);
            _entries.Remove(e);
            try
            {
                if (e.Kind == "plugin") Directory.Delete(e.PluginDir, true);
                else File.Delete(e.Path);
            }
            catch (Exception) { }
            Changed?.Invoke();
            return true;
        }

        public static string Sanitize(string name)
        {
            var chars = (name ?? "script").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            return chars.Length == 0 ? "script" : new string(chars);
        }

        // ------------------------------------------------------------------ running

        private void Say(Entry e, string line)
        {
            e.Log.Add(line);
            while (e.Log.Count > 40) e.Log.RemoveAt(0);
            _svc.Log($"[{e.Kind} {e.Name}] {line}");
        }

        private void Fail(Entry e, string message)
        {
            e.Error = message;
            Unload(e);
            Say(e, "ERROR " + message);
            _svc.Toast($"{(e.Kind == "plugin" ? "Plugin" : "Script")} {e.Name}: {Short(message)}", "error");
            Changed?.Invoke();
        }

        private static string Short(string s) => s.Length > 90 ? s.Substring(0, 90) + "…" : s;

        /// Runs a Lua function as a coroutine with an instruction budget. Over budget = the script is stopped.
        private DynValue Run(Entry e, DynValue fn, params DynValue[] args)
        {
            if (e.Script == null || e.Error != null || fn == null || fn.Type != DataType.Function && fn.Type != DataType.ClrFunction) return DynValue.Nil;
            try
            {
                var co = e.Script.CreateCoroutine(fn);
                co.Coroutine.AutoYieldCounter = InstructionBudget;
                var result = co.Coroutine.Resume(args);
                e.Runs++;
                if (co.Coroutine.State == CoroutineState.ForceSuspended)
                {
                    Fail(e, $"ran more than {InstructionBudget} instructions in one call (endless loop?); stopped");
                    return DynValue.Nil;
                }
                return result;
            }
            catch (InterpreterException ex) { Fail(e, ex.DecoratedMessage ?? ex.Message); }
            catch (Exception ex) { Fail(e, ex.Message); }
            return DynValue.Nil;
        }

        /// A gesture (or UI) event: runs `gesture.on` handlers and advances `gesture.sequence` matchers.
        public void Dispatch(GestureEvent ev)
        {
            foreach (var h in _handlers.ToList())
            {
                if (!h.Owner.Enabled || h.Owner.Error != null || h.Event != ev.Name && h.Event != "*") continue;
                Run(h.Owner, h.Fn, DynValue.NewTable(EventTable(h.Owner.Script, ev)));
            }
            if (ev.Phase == "end") return; // sequences count starts/taps only
            foreach (var s in _sequences.ToList())
            {
                if (!s.Owner.Enabled || s.Owner.Error != null) continue;
                if (s.Next > 0 && ev.Time - s.Start > s.Window) s.Next = 0;
                if (ev.Name == s.Steps[s.Next])
                {
                    if (s.Next == 0) s.Start = ev.Time;
                    s.Next++;
                    if (s.Next == s.Steps.Length)
                    {
                        s.Next = 0;
                        Run(s.Owner, s.Fn, DynValue.NewTable(EventTable(s.Owner.Script, ev)));
                    }
                }
                else if (ev.Name == s.Steps[0]) { s.Next = 1; s.Start = ev.Time; }
            }
        }

        private static Table EventTable(Script script, GestureEvent ev)
        {
            var t = new Table(script);
            t.Set("name", DynValue.NewString(ev.Name ?? ""));
            t.Set("hand", DynValue.NewString(ev.Hand ?? ""));
            t.Set("phase", DynValue.NewString(ev.Phase ?? ""));
            t.Set("time", DynValue.NewNumber(ev.Time));
            t.Set("x", DynValue.NewNumber(ev.Position.x));
            t.Set("y", DynValue.NewNumber(ev.Position.y));
            t.Set("z", DynValue.NewNumber(ev.Position.z));
            t.Set("detail", DynValue.NewString(ev.Detail ?? ""));
            return t;
        }

        /// Runs a snippet in a throwaway sandbox with the same API (control port, tests). Returns the result or the error.
        public string Eval(string code)
        {
            var e = new Entry { Name = "eval", Path = "", Kind = "eval" };
            var script = new Script(CoreModules.Preset_SoftSandbox);
            e.Script = script;
            e.Store = new Table(script);
            Install(e, script);
            try
            {
                var r = Run(e, script.LoadString(code, null, "eval"));
                return e.Error != null ? "error: " + e.Error : r.ToPrintString();
            }
            catch (InterpreterException ex) { return "error: " + (ex.DecoratedMessage ?? ex.Message); }
            finally { Unload(e); }
        }

        // ------------------------------------------------------------------ gesture detectors (gesture.define)

        private void OnCustomTick(float time, float dt)
        {
            if (_detectors.Count == 0) { DetectMs = 0; return; }
            var eng = _svc.Gestures;
            if (eng == null || time < _nextDetect) return;
            float rate = Mathf.Clamp(eng.Settings.detectorRate, 5f, 120f);
            _nextDetect = time + 1f / rate - 0.002f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            HashSet<Entry> filled = null;
            foreach (var d in _detectors.ToList())
            {
                var e = d.Owner;
                if (!e.Enabled || e.Error != null || e.Script == null) continue;
                if (filled == null) filled = new HashSet<Entry>();
                if (filled.Add(e))
                {
                    if (e.HandTables == null) e.HandTables = new[] { new Table(e.Script), new Table(e.Script) };
                    FillHand(e.HandTables[0], eng.Left, eng, e.Script);
                    FillHand(e.HandTables[1], eng.Right, eng, e.Script);
                }
                if (d.Hand == "both")
                {
                    bool raw = eng.Left.Tracked && eng.Right.Tracked && Detect(d, 2, e.HandTables[0], e.HandTables[1]);
                    if (e.Error != null) continue;
                    eng.Feed(d.Name, "both", raw, time, (eng.Left.Frame.Palm + eng.Right.Frame.Palm) * 0.5f, d.Hold, d.Cooldown);
                    continue;
                }
                for (int slot = 0; slot < 2; slot++)
                {
                    var s = slot == 0 ? eng.Left : eng.Right;
                    if (d.Hand != "any" && d.Hand != s.Side) continue;
                    bool raw = s.Tracked && Detect(d, slot, e.HandTables[slot], e.HandTables[1 - slot]);
                    if (e.Error != null) break;
                    eng.Feed(d.Name, s.Side, raw, time, s.Frame.Palm, d.Hold, d.Cooldown);
                }
            }
            DetectMs = sw.Elapsed.TotalMilliseconds;
        }

        private bool Detect(Detector d, int slot, Table a, Table b)
        {
            var e = d.Owner;
            try
            {
                var co = d.Co[slot];
                if (co == null || co.State == CoroutineState.Dead)
                {
                    co = e.Script.CreateCoroutine(d.Loop).Coroutine;
                    co.AutoYieldCounter = InstructionBudget;
                    d.Co[slot] = co;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var r = co.Resume(DynValue.NewTable(a), DynValue.NewTable(b));
                d.Micros = d.Micros * 0.9 + 0.1 * (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                if (co.State == CoroutineState.ForceSuspended)
                {
                    Fail(e, $"detector '{d.Name}' ran more than {InstructionBudget} instructions in one frame; stopped");
                    return false;
                }
                if (r.Type == DataType.Tuple) r = r.Tuple.Length > 0 ? r.Tuple[0] : DynValue.Nil;
                return r.CastToBool();
            }
            catch (InterpreterException ex) { Fail(e, $"detector '{d.Name}': " + (ex.DecoratedMessage ?? ex.Message)); }
            catch (Exception ex) { Fail(e, $"detector '{d.Name}': " + ex.Message); }
            return false;
        }

        /// Average cost of each detector (µs per call), for the Gestures panel.
        public IEnumerable<(string name, string owner, double micros)> DetectorCosts() => _detectors.Select(d => (d.Name, d.Owner.Name, d.Micros));

        private static void SetVec(Table parent, string key, Vector3 v, Script s)
        {
            var t = parent.Get(key);
            Table tt;
            if (t.Type == DataType.Table) tt = t.Table; else { tt = new Table(s); parent.Set(key, DynValue.NewTable(tt)); }
            tt.Set("x", DynValue.NewNumber(Math.Round(v.x, 4)));
            tt.Set("y", DynValue.NewNumber(Math.Round(v.y, 4)));
            tt.Set("z", DynValue.NewNumber(Math.Round(v.z, 4)));
        }

        /// The hand as Lua sees it (also what hands.get returns). Head-relative values use the head's yaw
        /// frame: x = the user's right, y = up, z = forward (looking down or up does not change them).
        private static void FillHand(Table t, HandState s, GestureEngine eng, Script script)
        {
            t.Set("side", DynValue.NewString(s.Side));
            t.Set("tracked", DynValue.NewBoolean(s.Tracked));
            t.Set("confident", DynValue.NewBoolean(s.Tracked && s.Frame.HighConfidence));
            t.Set("pinch", DynValue.NewNumber(Math.Round(s.Pinch, 3)));
            t.Set("pinching", DynValue.NewBoolean(s.Pinching));
            t.Set("grabbing", DynValue.NewBoolean(s.Grabbing));
            t.Set("pointing", DynValue.NewBoolean(s.Pointing));
            t.Set("open", DynValue.NewBoolean(s.Open));
            t.Set("palmUp", DynValue.NewBoolean(s.PalmUp));
            var p = s.Frame.Palm;
            t.Set("x", DynValue.NewNumber(Math.Round(p.x, 4))); t.Set("y", DynValue.NewNumber(Math.Round(p.y, 4))); t.Set("z", DynValue.NewNumber(Math.Round(p.z, 4)));
            var ct = t.Get("curl");
            Table curl;
            if (ct.Type == DataType.Table) curl = ct.Table; else { curl = new Table(script); t.Set("curl", DynValue.NewTable(curl)); }
            for (int i = 0; i < 5; i++) curl.Set(PoseSpec.FingerNames[i], DynValue.NewNumber(Math.Round(s.Fingers[i], 3)));
            var head = eng.Head;
            SetVec(t, "rel", eng.ToHeadLocal(p - head.position), script);
            SetVec(t, "vel", eng.ToHeadLocal(s.Velocity), script);
            t.Set("speed", DynValue.NewNumber(Math.Round(s.Velocity.magnitude, 3)));
            SetVec(t, "palmDir", s.PalmNormal, script);
            SetVec(t, "thumbDir", s.ThumbDir, script);
            SetVec(t, "fingerDir", s.Forward, script);
            t.Set("palmFacing", DynValue.NewString(s.Tracked ? eng.Nearest(s.PalmNormal, p) : ""));
            t.Set("thumbPointing", DynValue.NewString(s.Tracked ? eng.Nearest(s.ThumbDir, p) : ""));
            t.Set("size", DynValue.NewNumber(Math.Round(s.HandSize, 4)));
        }

        // ------------------------------------------------------------------ the Lua API

        private static string Str(CallbackArguments a, int i, string def = "") =>
            i < a.Count && !a[i].IsNil() ? (a[i].Type == DataType.String ? a[i].String : a[i].ToPrintString()) : def;

        private static double Num(CallbackArguments a, int i, double def) =>
            i < a.Count && a[i].Type == DataType.Number ? a[i].Number : def;

        private static DynValue Fn(CallbackArguments a, int i) =>
            i < a.Count && (a[i].Type == DataType.Function || a[i].Type == DataType.ClrFunction) ? a[i] : null;

        private static void Set(Table t, string name, Func<CallbackArguments, DynValue> f) =>
            t.Set(name, DynValue.NewCallback((ctx, args) => f(args), name));

        private static string TStr(Table t, string k, string def = null) { var v = t.Get(k); return v.Type == DataType.String ? v.String : def; }
        private static float? TNum(Table t, string k) { var v = t.Get(k); return v.Type == DataType.Number ? (float)v.Number : (float?)null; }

        /// voice.command{...} -> a registry entry owned by the script; `run` executes under the instruction budget.
        private MetaCodex.AI.VoiceCommand VoiceCommandFromTable(Entry e, Table t)
        {
            var fn = t.Get("run");
            if (fn.Type != DataType.Function) throw new ScriptRuntimeException("voice.command needs run=function(args) ... end");
            var cmd = new MetaCodex.AI.VoiceCommand
            {
                Name = TStr(t, "name", ""),
                Description = TStr(t, "description", ""),
                Owner = e.Key,
                Confirm = t.Get("confirm").CastToBool(),
            };
            // params: {name={type=, description=, enum={...}, required=true}} or an ordered list {{name=..., ...}, ...}
            var ps = new List<(string, string, string[], string, bool)>();
            var pt = t.Get("params");
            if (pt.Type == DataType.Table)
            {
                foreach (var pair in pt.Table.Pairs)
                {
                    if (pair.Value.Type != DataType.Table) continue;
                    var spec = pair.Value.Table;
                    string pname = pair.Key.Type == DataType.String ? pair.Key.String : TStr(spec, "name", "");
                    if (string.IsNullOrEmpty(pname)) throw new ScriptRuntimeException("voice.command params: every parameter needs a name");
                    string[] en = null;
                    if (spec.Get("enum").Type == DataType.Table) en = spec.Get("enum").Table.Values.Where(v => v.Type == DataType.String || v.Type == DataType.Number).Select(v => v.ToPrintString()).ToArray();
                    ps.Add((pname, TStr(spec, "type", "string"), en, TStr(spec, "description", ""), spec.Get("required").CastToBool()));
                }
            }
            cmd.Parameters = MetaCodex.AI.VoiceCommands.Schema(ps.ToArray());
            var ph = t.Get("phrases");
            if (ph.Type == DataType.Table) cmd.Phrases = ph.Table.Values.Where(v => v.Type == DataType.String).Select(v => v.String).ToArray();
            else if (ph.Type == DataType.String) cmd.Phrases = new[] { ph.String };
            var sy = t.Get("synonyms");
            if (sy.Type == DataType.Table)
                foreach (var pair in sy.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Table) continue;
                    var map = new Dictionary<string, string>();
                    foreach (var w in pair.Value.Table.Pairs)
                        if (w.Key.Type == DataType.String && w.Value.Type == DataType.String) map[MetaCodex.AI.VoiceCommands.Simplify(w.Key.String)] = w.Value.String;
                    cmd.Synonyms[pair.Key.String] = map;
                }
            cmd.Run = args =>
            {
                var at = new Table(e.Script);
                foreach (var p in args.Properties())
                {
                    switch (p.Value.Type)
                    {
                        case Newtonsoft.Json.Linq.JTokenType.Integer:
                        case Newtonsoft.Json.Linq.JTokenType.Float: at.Set(p.Name, DynValue.NewNumber((double)p.Value)); break;
                        case Newtonsoft.Json.Linq.JTokenType.Boolean: at.Set(p.Name, DynValue.NewBoolean((bool)p.Value)); break;
                        default: at.Set(p.Name, DynValue.NewString(p.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)p.Value : p.Value.ToString())); break;
                    }
                }
                if (!e.Enabled || e.Error != null) return "error: " + e.Name + " is " + (e.Error != null ? "stopped: " + e.Error : "disabled");
                var r = Run(e, fn, DynValue.NewTable(at));
                if (e.Error != null) return "error: " + e.Error;
                return r.Type == DataType.String ? r.String : r.IsNil() ? null : r.ToPrintString();
            };
            return cmd;
        }

        private void Install(Entry e, Script s)
        {
            var g = s.Globals;

            var gesture = new Table(s);
            Set(gesture, "on", a =>
            {
                var name = Str(a, 0); var fn = Fn(a, 1);
                if (name.Length == 0 || fn == null) throw new ScriptRuntimeException("gesture.on(name, function(e) ... end)");
                _handlers.Add(new Handler { Owner = e, Event = name, Fn = fn });
                e.Handlers++;
                return DynValue.Nil;
            });
            Set(gesture, "sequence", a =>
            {
                var steps = new List<string>();
                if (a.Count > 0 && a[0].Type == DataType.Table)
                    foreach (var v in a[0].Table.Values) if (v.Type == DataType.String) steps.Add(v.String);
                float window = (float)Num(a, 1, 0.8);
                var fn = Fn(a, 2);
                if (steps.Count == 0 || fn == null) throw new ScriptRuntimeException("gesture.sequence({\"clap\",\"clap\"}, seconds, function(e) ... end)");
                _sequences.Add(new Sequence { Owner = e, Steps = steps.ToArray(), Window = window, Fn = fn });
                e.Handlers++;
                return DynValue.Nil;
            });
            Set(gesture, "define", a =>
            {
                // gesture.define(name, {hand=, hold=, cooldown=, description=}, function(h, other) return bool end)
                var name = Str(a, 0);
                Table opts = a.Count > 1 && a[1].Type == DataType.Table ? a[1].Table : null;
                var fn = Fn(a, opts != null ? 2 : 1);
                if (name.Length == 0 || fn == null) throw new ScriptRuntimeException("gesture.define(name, {hand=\"right\", hold=0.1, cooldown=0.5}, function(h) return ... end)");
                if (_detectors.Count >= MaxDetectors) throw new ScriptRuntimeException($"too many detectors (max {MaxDetectors})");
                string hand = opts != null ? TStr(opts, "hand", "any") : "any";
                if (hand != "left" && hand != "right" && hand != "any" && hand != "both") throw new ScriptRuntimeException("hand must be left, right, any or both");
                var factory = s.LoadString("local f = ... return function(a, b) while true do a, b = coroutine.yield(f(a, b)) end end", null, "detector");
                var loop = s.Call(factory, fn);
                _detectors.RemoveAll(d => d.Owner == e && d.Name == name);
                _detectors.Add(new Detector
                {
                    Owner = e, Name = name, Hand = hand, Loop = loop,
                    Hold = Mathf.Clamp(opts != null ? TNum(opts, "hold") ?? 0.08f : 0.08f, 0f, 5f),
                    Cooldown = Mathf.Clamp(opts != null ? TNum(opts, "cooldown") ?? 0.3f : 0.3f, 0f, 30f),
                });
                Engine?.RegisterCustom(name, "lua", e.Key, opts != null ? TStr(opts, "description", "") : "", hand);
                e.Detectors++;
                return DynValue.Nil;
            });
            Set(gesture, "pose", a =>
            {
                // gesture.pose(name, {fingers={thumb=0,index=1,...}, palmFacing="up", thumbPointing="up", tolerance=0.3, hold=0.12, hand="any"})
                var name = Str(a, 0);
                if (name.Length == 0 || a.Count < 2 || a[1].Type != DataType.Table) throw new ScriptRuntimeException("gesture.pose(name, {fingers={...}, palmFacing=..., thumbPointing=...})");
                var p = PoseJson.FromTable(a[1].Table, e.Key);
                p.Name = name;
                Engine?.AddPose(p);
                e.Poses++;
                return DynValue.Nil;
            });
            Set(gesture, "emit", a =>
            {
                var name = Str(a, 0);
                if (name.Length == 0) throw new ScriptRuntimeException("gesture.emit(name, hand, phase, detail)");
                var eng = Engine;
                var hand = Str(a, 1, "both");
                var pos = hand == "left" ? eng?.Left.Frame.Palm : hand == "right" ? eng?.Right.Frame.Palm : null;
                eng?.RaiseExternal(name, hand, Str(a, 2, "tap"), Now, pos ?? Vector3.zero, Str(a, 3, "emit"));
                return DynValue.Nil;
            });
            Set(gesture, "names", a =>
            {
                var t = new Table(s);
                foreach (var n in Engine != null ? Engine.AllNames() : GestureEngine.Names) t.Append(DynValue.NewString(n));
                return DynValue.NewTable(t);
            });
            gesture.Set("list", gesture.Get("names"));
            Set(gesture, "active", a => DynValue.NewBoolean(Engine != null && Engine.IsActive(Str(a, 0), a.Count > 1 && !a[1].IsNil() ? Str(a, 1) : null)));
            g.Set("gesture", DynValue.NewTable(gesture));

            var hands = new Table(s);
            Set(hands, "get", a =>
            {
                var t = new Table(s);
                var eng = Engine;
                var st = _svc.Hand(Str(a, 0, "right"));
                if (st != null && eng != null) FillHand(t, st, eng, s);
                return DynValue.NewTable(t);
            });
            hands.Set("state", hands.Get("get"));
            g.Set("hands", DynValue.NewTable(hands));

            var head = new Table(s);
            Set(head, "pose", a =>
            {
                var t = new Table(s);
                var eng = Engine;
                var h = eng != null ? eng.Head : new Pose(Vector3.zero, Quaternion.identity);
                var f = h.rotation * Vector3.forward;
                t.Set("x", DynValue.NewNumber(h.position.x)); t.Set("y", DynValue.NewNumber(h.position.y)); t.Set("z", DynValue.NewNumber(h.position.z));
                t.Set("fx", DynValue.NewNumber(f.x)); t.Set("fy", DynValue.NewNumber(f.y)); t.Set("fz", DynValue.NewNumber(f.z));
                return DynValue.NewTable(t);
            });
            g.Set("head", DynValue.NewTable(head));

            var ui = new Table(s);
            Set(ui, "toast", a => { _svc.Toast(Str(a, 0), Str(a, 1, "info")); return DynValue.Nil; });
            Set(ui, "open", a => DynValue.NewBoolean(_svc.OpenPanel(Str(a, 0))));
            Set(ui, "close", a => DynValue.NewBoolean(_svc.ClosePanel(Str(a, 0))));
            Set(ui, "toggle", a => DynValue.NewBoolean(_svc.TogglePanel(Str(a, 0))));
            Set(ui, "select", a => { _svc.SetSelectionMode(a.Count == 0 || a[0].CastToBool()); return DynValue.Nil; });
            Set(ui, "panels", a =>
            {
                var t = new Table(s);
                foreach (var id in _svc.PanelIds) t.Append(DynValue.NewString(id));
                return DynValue.NewTable(t);
            });
            Set(ui, "modal", a =>
            {
                var buttons = new List<string>();
                if (a.Count > 2 && a[2].Type == DataType.Table)
                    foreach (var v in a[2].Table.Values) buttons.Add(v.ToPrintString());
                if (buttons.Count == 0) buttons.Add("OK");
                var fn = Fn(a, 3);
                _svc.Modal(Str(a, 0), Str(a, 1), buttons.ToArray(), i => { if (fn != null) Run(e, fn, DynValue.NewNumber(i + 1), DynValue.NewString(buttons[i])); });
                return DynValue.Nil;
            });
            g.Set("ui", DynValue.NewTable(ui));

            var terminal = new Table(s);
            Set(terminal, "send", a => { _svc.TerminalSend(Str(a, 0)); return DynValue.Nil; });
            Set(terminal, "run", a => { _svc.TerminalSend(Str(a, 0) + "\r"); return DynValue.Nil; });
            Set(terminal, "new", a => { _svc.TerminalNew(); return DynValue.Nil; });
            g.Set("terminal", DynValue.NewTable(terminal));

            var browser = new Table(s);
            Set(browser, "open", a => { _svc.OpenBrowser(Str(a, 0)); return DynValue.Nil; });
            g.Set("browser", DynValue.NewTable(browser));

            var keyboard = new Table(s);
            Set(keyboard, "open", a => { _svc.OpenKeyboard(); return DynValue.Nil; });
            g.Set("keyboard", DynValue.NewTable(keyboard));

            var settings = new Table(s);
            Set(settings, "get", a => { var v = _svc.GetSetting(Str(a, 0)); return v == null ? DynValue.Nil : DynValue.NewString(v); });
            Set(settings, "set", a => DynValue.NewBoolean(_svc.SetSetting(Str(a, 0), Str(a, 1))));
            g.Set("settings", DynValue.NewTable(settings));

            var store = new Table(s);
            Set(store, "get", a =>
            {
                var key = Str(a, 0);
                return _stores.TryGetValue(e.Key, out var d) && d.TryGetValue(key, out var v) ? DynValue.NewString(v) : DynValue.Nil;
            });
            Set(store, "set", a =>
            {
                if (!_stores.TryGetValue(e.Key, out var d)) _stores[e.Key] = d = new Dictionary<string, string>();
                d[Str(a, 0)] = Str(a, 1);
                SaveState();
                return DynValue.Nil;
            });
            g.Set("store", DynValue.NewTable(store));

            var app = new Table(s);
            Set(app, "time", a => DynValue.NewNumber(Now));
            Set(app, "after", a =>
            {
                var fn = Fn(a, 1);
                if (fn == null) throw new ScriptRuntimeException("app.after(seconds, function() ... end)");
                var t = new Timer { Owner = e, Id = ++_timerIds, At = Now + (float)Num(a, 0, 1), Fn = fn };
                _timers.Add(t);
                return DynValue.NewNumber(t.Id);
            });
            Set(app, "every", a =>
            {
                var fn = Fn(a, 1);
                float every = Mathf.Max(0.05f, (float)Num(a, 0, 1));
                if (fn == null) throw new ScriptRuntimeException("app.every(seconds, function() ... end)");
                var t = new Timer { Owner = e, Id = ++_timerIds, At = Now + every, Every = every, Fn = fn };
                _timers.Add(t);
                return DynValue.NewNumber(t.Id);
            });
            Set(app, "cancel", a => { int id = (int)Num(a, 0, -1); _timers.RemoveAll(t => t.Id == id && t.Owner == e); return DynValue.Nil; });
            Set(app, "action", a => { var err = _svc.RunAction(Str(a, 0)); return err == null ? DynValue.True : DynValue.NewString(err); });
            g.Set("app", DynValue.NewTable(app));

            var voice = new Table(s);
            Set(voice, "command", a =>
            {
                if (a.Count < 1 || a[0].Type != DataType.Table) throw new ScriptRuntimeException("voice.command{name=\"git_status\", description=\"...\", params={...}, phrases={\"show git status\"}, run=function(args) return \"reply\" end}");
                var cmd = VoiceCommandFromTable(e, a[0].Table);
                if (_svc.Voice == null) return DynValue.Nil;
                try { _svc.Voice.Register(cmd); }
                catch (ArgumentException ex) { throw new ScriptRuntimeException(ex.Message); }
                e.Handlers++;
                return DynValue.Nil;
            });
            Set(voice, "remove", a => DynValue.NewBoolean(_svc.Voice != null && _svc.Voice.Remove(Str(a, 0), e.Key)));
            Set(voice, "say", a => { _svc.VoiceSay(Str(a, 0)); return DynValue.Nil; });
            Set(voice, "run", a => { _svc.VoiceRun(Str(a, 0)); return DynValue.Nil; });
            Set(voice, "listen", a => { _svc.VoiceListen(); return DynValue.Nil; });
            Set(voice, "list", a =>
            {
                var t = new Table(s);
                if (_svc.Voice != null) foreach (var c in _svc.Voice.All) t.Append(DynValue.NewString(c.Name));
                return DynValue.NewTable(t);
            });
            g.Set("voice", DynValue.NewTable(voice));

            if (e.Kind == "plugin")
            {
                var plugin = new Table(s);
                plugin.Set("id", DynValue.NewString(e.Name));
                plugin.Set("name", DynValue.NewString(e.Title ?? e.Name));
                plugin.Set("version", DynValue.NewString(e.Version ?? ""));
                plugin.Set("dir", DynValue.NewString("/mnt/quest/plugins/" + e.Name));
                g.Set("plugin", DynValue.NewTable(plugin));
            }

            Set(g, "log", a => { Say(e, string.Join(" ", Enumerable.Range(0, a.Count).Select(i => Str(a, i)))); return DynValue.Nil; });
        }

        // ------------------------------------------------------------------ persisted state

        [Serializable] private sealed class KV { public string k, v; }
        [Serializable] private sealed class StoreDump { public string script; public List<KV> values = new List<KV>(); }
        [Serializable] private sealed class StateDump { public List<KV> enabled = new List<KV>(); public List<StoreDump> stores = new List<StoreDump>(); }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                var d = JsonUtility.FromJson<StateDump>(File.ReadAllText(_statePath));
                if (d == null) return;
                foreach (var kv in d.enabled) _enabled[kv.k] = kv.v == "true";
                foreach (var st in d.stores)
                {
                    var map = new Dictionary<string, string>();
                    foreach (var kv in st.values) map[kv.k] = kv.v;
                    _stores[st.script] = map;
                }
            }
            catch (Exception ex) { _svc.Log("script state unreadable: " + ex.Message); }
        }

        private void SaveState()
        {
            var d = new StateDump();
            foreach (var kv in _enabled) d.enabled.Add(new KV { k = kv.Key, v = kv.Value ? "true" : "false" });
            foreach (var st in _stores)
            {
                var sd = new StoreDump { script = st.Key };
                foreach (var kv in st.Value) sd.values.Add(new KV { k = kv.Key, v = kv.Value });
                d.stores.Add(sd);
            }
            try { File.WriteAllText(_statePath, JsonUtility.ToJson(d, true)); }
            catch (Exception ex) { _svc.Log("script state not saved: " + ex.Message); }
        }

        public static string Number(double d) => d.ToString(CultureInfo.InvariantCulture);
    }

    /// Pose files (poses/*.json in a plugin) and Lua pose tables to PoseSpec and back.
    public static class PoseJson
    {
        public static PoseSpec Parse(JObject j, string owner, string source)
        {
            var p = new PoseSpec { Owner = owner, Source = source };
            p.Name = (string)j["name"] ?? "";
            p.Description = (string)j["description"] ?? "";
            p.Hand = ((string)j["hand"] ?? "any").ToLowerInvariant();
            if (j["fingers"] is JObject fj)
                for (int i = 0; i < 5; i++)
                    if (fj[PoseSpec.FingerNames[i]] != null && fj[PoseSpec.FingerNames[i]].Type != JTokenType.Null) p.Fingers[i] = Mathf.Clamp01((float)fj[PoseSpec.FingerNames[i]]);
            if (j["tolerance"] != null) p.Tolerance = Mathf.Clamp((float)j["tolerance"], 0.05f, 1f);
            p.PalmFacing = Dir((string)j["palmFacing"]);
            p.ThumbPointing = Dir((string)j["thumbPointing"]);
            if (j["directionMin"] != null) p.DirectionMin = Mathf.Clamp((float)j["directionMin"], -1f, 1f);
            if (j["pinch"] is JArray pa && pa.Count == 2) { p.PinchMin = (float)pa[0]; p.PinchMax = (float)pa[1]; }
            if (j["hold"] != null) p.Hold = Mathf.Clamp((float)j["hold"], 0f, 5f);
            if (j["cooldown"] != null) p.Cooldown = Mathf.Clamp((float)j["cooldown"], 0f, 30f);
            return p;
        }

        public static PoseSpec FromTable(Table t, string owner)
        {
            var p = new PoseSpec { Owner = owner, Source = "lua" };
            var desc = t.Get("description"); if (desc.Type == DataType.String) p.Description = desc.String;
            var hand = t.Get("hand"); if (hand.Type == DataType.String) p.Hand = hand.String.ToLowerInvariant();
            var f = t.Get("fingers");
            if (f.Type == DataType.Table)
                for (int i = 0; i < 5; i++) { var v = f.Table.Get(PoseSpec.FingerNames[i]); if (v.Type == DataType.Number) p.Fingers[i] = Mathf.Clamp01((float)v.Number); }
            var tol = t.Get("tolerance"); if (tol.Type == DataType.Number) p.Tolerance = Mathf.Clamp((float)tol.Number, 0.05f, 1f);
            var pf = t.Get("palmFacing"); if (pf.Type == DataType.String) p.PalmFacing = Dir(pf.String);
            var tp = t.Get("thumbPointing"); if (tp.Type == DataType.String) p.ThumbPointing = Dir(tp.String);
            var dm = t.Get("directionMin"); if (dm.Type == DataType.Number) p.DirectionMin = Mathf.Clamp((float)dm.Number, -1f, 1f);
            var pinch = t.Get("pinch");
            if (pinch.Type == DataType.Table && pinch.Table.Length == 2) { p.PinchMin = (float)pinch.Table.Get(1).CastToNumber(); p.PinchMax = (float)pinch.Table.Get(2).CastToNumber(); }
            var hold = t.Get("hold"); if (hold.Type == DataType.Number) p.Hold = Mathf.Clamp((float)hold.Number, 0f, 5f);
            var cd = t.Get("cooldown"); if (cd.Type == DataType.Number) p.Cooldown = Mathf.Clamp((float)cd.Number, 0f, 30f);
            return p;
        }

        private static string Dir(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.ToLowerInvariant();
            if (s == "any") return null;
            if (Array.IndexOf(PoseSpec.Directions, s) < 0) throw new Exception($"unknown direction '{s}' (use {string.Join(", ", PoseSpec.Directions)})");
            return s;
        }

        public static JObject ToJson(PoseSpec p)
        {
            var fingers = new JObject();
            for (int i = 0; i < 5; i++) if (p.Fingers[i].HasValue) fingers[PoseSpec.FingerNames[i]] = Math.Round(p.Fingers[i].Value, 2);
            var j = new JObject { ["name"] = p.Name, ["description"] = p.Description ?? "", ["hand"] = p.Hand, ["fingers"] = fingers, ["tolerance"] = Math.Round(p.Tolerance, 2) };
            if (p.PalmFacing != null) j["palmFacing"] = p.PalmFacing;
            if (p.ThumbPointing != null) j["thumbPointing"] = p.ThumbPointing;
            if (p.PinchMin.HasValue || p.PinchMax.HasValue) j["pinch"] = new JArray(p.PinchMin ?? 0f, p.PinchMax ?? 1f);
            j["hold"] = Math.Round(p.Hold, 2);
            j["cooldown"] = Math.Round(p.Cooldown, 2);
            return j;
        }
    }
}
