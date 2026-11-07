using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MetaCodex.Hands;
using MoonSharp.Interpreter;
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
        void Log(string line);
    }

    /// Behaviour scripts in Lua (MoonSharp: pure C#, IL2CPP-safe), changed during a session without a rebuild.
    /// Scripts are `*.lua` files in one folder (on the headset the userland sees it as /mnt/quest/scripts, so
    /// agents write them there); the host reloads a file within a second of a change. Each script runs in its
    /// own sandbox (no io/os/load), every call has an instruction budget (a runaway loop disables the script,
    /// the app keeps its frame rate), and its enabled flag and `store` survive restarts (state.json).
    public sealed class ScriptHost
    {
        public sealed class Entry
        {
            public string Name, Path, Description = "";
            public Script Script;
            public string Error;
            public bool Enabled = true;
            public DateTime Mtime;
            public int Handlers;
            public int Runs;
            public readonly List<string> Log = new List<string>();
            internal Table Store;
        }

        private sealed class Handler { public Entry Owner; public string Event; public DynValue Fn; }
        private sealed class Sequence { public Entry Owner; public string[] Steps; public float Window; public DynValue Fn; public int Next; public float Start; }
        private sealed class Timer { public Entry Owner; public int Id; public float At, Every; public DynValue Fn; }

        public const int InstructionBudget = 200_000;
        public readonly string Dir;
        public IReadOnlyList<Entry> Entries => _entries;
        public event Action Changed;
        public float Now { get; private set; }

        private readonly IScriptServices _svc;
        private readonly string _statePath;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<Handler> _handlers = new List<Handler>();
        private readonly List<Sequence> _sequences = new List<Sequence>();
        private readonly List<Timer> _timers = new List<Timer>();
        private readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>();
        private readonly Dictionary<string, Dictionary<string, string>> _stores = new Dictionary<string, Dictionary<string, string>>();
        private float _nextScan;
        private int _timerIds;

        public ScriptHost(IScriptServices services, string dir)
        {
            _svc = services;
            Dir = dir;
            Directory.CreateDirectory(dir);
            _statePath = System.IO.Path.Combine(dir, ".state.json");
            LoadState();
            Script.DefaultOptions.DebugPrint = s => _svc.Log("[lua] " + s);
        }

        // ------------------------------------------------------------------ loading

        /// Called every frame: runs due timers; rescans the folder once a second.
        public void Tick(float now)
        {
            Now = now;
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            for (int i = 0; i < _timers.Count; i++)
            {
                var t = _timers[i];
                if (t.At > now || !t.Owner.Enabled || t.Owner.Error != null) continue;
                if (t.Every > 0f) t.At = now + t.Every; else { _timers.RemoveAt(i); i--; }
                Run(t.Owner, t.Fn);
            }
        }

        /// Loads new and changed scripts, drops deleted ones.
        public void Scan()
        {
            var files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.lua") : new string[0];
            var seen = new HashSet<string>();
            bool changed = false;
            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(f);
                seen.Add(name);
                var mtime = File.GetLastWriteTimeUtc(f);
                var e = _entries.FirstOrDefault(x => x.Name == name);
                if (e != null && e.Mtime == mtime) continue;
                Load(f, mtime);
                changed = true;
            }
            foreach (var e in _entries.Where(x => !seen.Contains(x.Name)).ToList())
            {
                Unload(e);
                _entries.Remove(e);
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }

        private void Load(string path, DateTime mtime)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            var e = _entries.FirstOrDefault(x => x.Name == name);
            if (e == null) { e = new Entry { Name = name, Path = path }; _entries.Add(e); }
            else Unload(e);
            e.Mtime = mtime;
            e.Error = null;
            e.Handlers = 0;
            e.Enabled = !_enabled.TryGetValue(name, out bool en) || en;
            string code;
            try { code = File.ReadAllText(path); }
            catch (Exception ex) { e.Error = "read failed: " + ex.Message; return; }
            e.Description = FirstComment(code);
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.Options.DebugPrint = s => Say(e, s);
            e.Script = script;
            e.Store = new Table(script);
            Install(e, script);
            if (!e.Enabled) { Say(e, "disabled"); return; }
            try
            {
                var chunk = script.LoadString(code, null, name + ".lua");
                Run(e, chunk);
                if (e.Error == null) Say(e, $"loaded ({e.Handlers} handlers)");
            }
            catch (InterpreterException ex) { Fail(e, ex.DecoratedMessage ?? ex.Message); }
            catch (Exception ex) { Fail(e, ex.Message); }
        }

        private void Unload(Entry e)
        {
            _handlers.RemoveAll(h => h.Owner == e);
            _sequences.RemoveAll(s => s.Owner == e);
            _timers.RemoveAll(t => t.Owner == e);
            e.Script = null;
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

        public void SetEnabled(string name, bool enabled)
        {
            _enabled[name] = enabled;
            SaveState();
            var e = _entries.FirstOrDefault(x => x.Name == name);
            if (e == null) return;
            Load(e.Path, File.Exists(e.Path) ? File.GetLastWriteTimeUtc(e.Path) : e.Mtime);
            Changed?.Invoke();
        }

        public void ReloadAll()
        {
            foreach (var e in _entries.ToList()) Load(e.Path, File.GetLastWriteTimeUtc(e.Path));
            Changed?.Invoke();
        }

        /// Writes a script file (agents, the control port and the Scripts panel use this); loads it at once.
        public Entry Save(string name, string code)
        {
            name = Sanitize(name);
            var path = System.IO.Path.Combine(Dir, name + ".lua");
            File.WriteAllText(path, code);
            Load(path, File.GetLastWriteTimeUtc(path));
            Changed?.Invoke();
            return _entries.First(x => x.Name == name);
        }

        public bool Delete(string name)
        {
            var e = _entries.FirstOrDefault(x => x.Name == Sanitize(name));
            if (e == null) return false;
            Unload(e);
            _entries.Remove(e);
            try { File.Delete(e.Path); } catch (Exception) { }
            Changed?.Invoke();
            return true;
        }

        private static string Sanitize(string name)
        {
            var chars = (name ?? "script").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            return chars.Length == 0 ? "script" : new string(chars);
        }

        // ------------------------------------------------------------------ running

        private void Say(Entry e, string line)
        {
            e.Log.Add(line);
            while (e.Log.Count > 40) e.Log.RemoveAt(0);
            _svc.Log($"[script {e.Name}] {line}");
        }

        private void Fail(Entry e, string message)
        {
            e.Error = message;
            Unload(e);
            Say(e, "ERROR " + message);
            _svc.Toast($"Script {e.Name}: {Short(message)}", "error");
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
            var e = new Entry { Name = "eval", Path = "" };
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

        // ------------------------------------------------------------------ the Lua API

        private static string Str(CallbackArguments a, int i, string def = "") =>
            i < a.Count && !a[i].IsNil() ? (a[i].Type == DataType.String ? a[i].String : a[i].ToPrintString()) : def;

        private static double Num(CallbackArguments a, int i, double def) =>
            i < a.Count && a[i].Type == DataType.Number ? a[i].Number : def;

        private static DynValue Fn(CallbackArguments a, int i) =>
            i < a.Count && (a[i].Type == DataType.Function || a[i].Type == DataType.ClrFunction) ? a[i] : null;

        private static void Set(Table t, string name, Func<CallbackArguments, DynValue> f) =>
            t.Set(name, DynValue.NewCallback((ctx, args) => f(args), name));

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
            Set(gesture, "names", a =>
            {
                var t = new Table(s);
                foreach (var n in GestureEngine.Names) t.Append(DynValue.NewString(n));
                return DynValue.NewTable(t);
            });
            g.Set("gesture", DynValue.NewTable(gesture));

            var hands = new Table(s);
            Set(hands, "state", a =>
            {
                var st = _svc.Hand(Str(a, 0, "right"));
                var t = new Table(s);
                if (st != null)
                {
                    t.Set("tracked", DynValue.NewBoolean(st.Tracked));
                    t.Set("pinch", DynValue.NewNumber(st.Pinch));
                    t.Set("pinching", DynValue.NewBoolean(st.Pinching));
                    t.Set("grabbing", DynValue.NewBoolean(st.Grabbing));
                    t.Set("pointing", DynValue.NewBoolean(st.Pointing));
                    t.Set("open", DynValue.NewBoolean(st.Open));
                    t.Set("palmUp", DynValue.NewBoolean(st.PalmUp));
                    var p = st.Frame.Palm;
                    t.Set("x", DynValue.NewNumber(p.x)); t.Set("y", DynValue.NewNumber(p.y)); t.Set("z", DynValue.NewNumber(p.z));
                }
                return DynValue.NewTable(t);
            });
            g.Set("hands", DynValue.NewTable(hands));

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
                return _stores.TryGetValue(e.Name, out var d) && d.TryGetValue(key, out var v) ? DynValue.NewString(v) : DynValue.Nil;
            });
            Set(store, "set", a =>
            {
                if (!_stores.TryGetValue(e.Name, out var d)) _stores[e.Name] = d = new Dictionary<string, string>();
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
            g.Set("app", DynValue.NewTable(app));

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
}
