// The generic plugin scenario: loads a plugin with the REAL ScriptHost against FakeServices and checks what every
// plugin must do (see run-plugin-tests.sh for the list). No plugin-specific code here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MetaCodex.AI;
using MetaCodex.Scripting;
using Newtonsoft.Json.Linq;

namespace PluginHarness
{
    public static class Program
    {
        public static int Fails;
        public static bool Verbose;

        public static int Main(string[] args)
        {
            string runDir = null;
            bool rules = false, net = false, specs = false;
            string examplesDir = null;
            var folders = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--verbose") Verbose = true;
                else if (args[i] == "--run" && i + 1 < args.Length) runDir = args[++i];
                else if (args[i] == "--rules") rules = true;
                else if (args[i] == "--net" && i + 1 < args.Length) { net = true; examplesDir = args[++i]; } // the API and the weather example
                else if (args[i] == "--fixtures" && i + 1 < args.Length)
                {
                    // canned network replies (tests/plugins/fixtures/net/*.json): every fake app answers from them
                    foreach (var bad in NetFixture.LoadDir(args[++i])) { Fails++; Console.WriteLine("FAIL [fixtures] " + bad); }
                }
                else if (args[i] == "--specs") specs = true;
                else if (args[i] == "--scaffold" && i + 3 < args.Length)
                {
                    // --scaffold <kind> <id> <dest> [name]: a new plugin project's files, as the app makes them
                    string kind = args[i + 1], id = args[i + 2], dest = args[i + 3];
                    string name = i + 4 < args.Length ? args[i + 4] : id;
                    // an example added as a project: its own files are in dest already; the project's agent files join them
                    bool example = File.Exists(Path.Combine(dest, "plugin.json"));
                    var files = example ? PluginProject.AgentFiles(kind, id, name, fromExample: true) : PluginProject.Scaffold(kind, id, name);
                    foreach (var kv in files)
                    {
                        var path = Path.Combine(dest, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, kv.Value);
                    }
                    Directory.CreateDirectory(Path.Combine(dest, ".metacodex"));
                    File.WriteAllText(Path.Combine(dest, ".metacodex", "project.json"), PluginProject.MarkerJson(kind));
                    Console.WriteLine("scaffold " + kind + " " + id + " -> " + dest);
                    return 0;
                }
                else folders.Add(args[i]);
            }
            if (specs)
            {
                try { Specs.Run(); }
                catch (Exception ex) { Fails++; Console.WriteLine("FAIL [specs] the harness itself crashed: " + ex); }
                if (runDir == null) { Console.WriteLine(Fails == 0 ? "PASS: specs" : "FAILED: " + Fails + " check(s)"); return Math.Min(Fails, 120); }
            }
            if (runDir == null || folders.Count == 0 && !rules && !net && !specs)
            {
                Console.WriteLine("usage: PluginTests.dll --run <run dir> [--verbose] [--rules] [--net <examples dir>] [--fixtures <dir>] <plugin folder name>... | --scaffold <kind> <id> <dest> [name]");
                return 2;
            }
            if (rules)
            {
                try { Rules.Run(runDir); }
                catch (Exception ex) { Fails++; Console.WriteLine("FAIL [rules] the harness itself crashed: " + ex); }
            }
            if (net)
            {
                try { NetTests.Run(runDir, examplesDir); }
                catch (Exception ex) { Fails++; Console.WriteLine("FAIL [net] the harness itself crashed: " + ex); }
            }
            foreach (var name in folders)
            {
                try { new PluginRun(runDir, name).Run(); }
                catch (Exception ex) { Fails++; Console.WriteLine("FAIL [" + name + "] the harness itself crashed: " + ex); }
            }
            Console.WriteLine(Fails == 0 ? "PASS: all plugins" : "FAILED: " + Fails + " check(s)");
            return Math.Min(Fails, 120);
        }
    }

    internal sealed class PluginRun
    {
        /// Full rebuilds a window may average per second of app time. The host's own rate limit is one a second, so a
        /// limit of exactly 1 could never fire; half of that means "rebuilt every other second or more often".
        private const double MaxRebuildsPerSecond = 0.5;
        private const int RunSeconds = 3 * 3600;
        private const string InputText = "test 1 in 5 minutes";

        private readonly string _raw, _id, _key, _scripts, _plugins, _projects, _prefix;
        /// The plugin's kind: a project's (its marker), else plugin.json's "kind", else app.
        private string _kind = "app";
        private FakeServices _fake;
        private ScriptHost _host;
        private ScriptHost.Entry _entry;
        private double _t = 100;
        private bool _errorReported;
        private int _fails;
        private string _summary = "";
        private List<string> _loadWindowIds = new List<string>(), _loadTileIds = new List<string>();
        private readonly List<string> _badLog = new List<string>();
        private readonly HashSet<string> _badLogSeen = new HashSet<string>();
        private readonly Regex _badLogRx;

        public PluginRun(string runDir, string folderName)
        {
            // "<n>.<id>": the run folder's slot; the plugin id is after the first dot
            _raw = folderName;
            int dot = folderName.IndexOf('.');
            _id = ScriptHost.Sanitize(dot >= 0 ? folderName.Substring(dot + 1) : folderName);
            _key = "plugin/" + _id;
            _prefix = "plugin-" + _id + "-";
            string root = Path.Combine(runDir, folderName);
            _scripts = Path.Combine(root, "scripts");
            _plugins = Path.Combine(root, "plugins");
            _projects = Path.Combine(root, "projects"); // a plugin project: projects/<apps|gestures|behaviours>/<id>
            _badLogRx = new Regex("^\\[(plugin|script) " + Regex.Escape(_id) + "\\] (ERROR|warn)");
        }

        // ------------------------------------------------------------------ output

        private void Ok(string msg) => Console.WriteLine("ok   [" + _id + "] " + msg);

        private void Fail(string msg)
        {
            _fails++;
            Program.Fails++;
            Console.WriteLine("FAIL [" + _id + "] " + msg);
        }

        private bool Check(bool cond, string msg)
        {
            if (cond) Ok(msg); else Fail(msg);
            return cond;
        }

        /// False when the plugin has stopped with an error; the first time it is reported as a FAIL.
        private bool NoError(string when)
        {
            if (_entry == null || _entry.Error == null) return true;
            if (!_errorReported)
            {
                _errorReported = true;
                Fail(when + ": the plugin stopped with an error: " + _entry.Error);
            }
            return false;
        }

        private static string Cut(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "..." : s ?? "";

        private void PrintSpec(WindowRec w, string tag)
        {
            if (Program.Verbose && w != null && w.Json != null) Console.WriteLine("     " + tag + " " + w.Id + ": " + Cut(w.Spec.ToString(Newtonsoft.Json.Formatting.None), 600));
        }

        // ------------------------------------------------------------------ the fake app's time

        private static double StartClock() =>
            new DateTimeOffset(new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

        /// One app frame: the app's deferred events, then the host's Tick. dt seconds of app time and of wall clock.
        private void Step(double dt)
        {
            _fake.Pump(_host);
            _t += dt;
            _fake.Clock += dt;
            _host.Tick((float)_t);
            DrainProblems();
        }

        private void StepFor(double seconds)
        {
            int n = Math.Max(1, (int)Math.Round(seconds / 0.1));
            for (int i = 0; i < n; i++) Step(seconds / n);
        }

        private void DrainProblems()
        {
            if (_fake.Problems.Count == 0) return;
            foreach (var p in _fake.Problems.ToList()) Fail(p);
            _fake.Problems.Clear();
        }

        private List<WindowRec> OwnWindows() =>
            _fake.Windows.Values.Where(w => w.Id.StartsWith(_prefix, StringComparison.Ordinal)).OrderBy(w => w.Id, StringComparer.Ordinal).ToList();

        private List<PluginTile> OwnTiles() => _fake.Tiles.Where(t => t.Owner == _id).ToList();

        private List<VoiceCommand> OwnVoice() => _fake.Voice.All.Where(c => c.Owner == _key).ToList();

        // ------------------------------------------------------------------ the scenario

        public void Run()
        {
            Console.WriteLine("== plugin " + _raw + (_raw == _id ? "" : " (id " + _id + ")"));
            Directory.CreateDirectory(_scripts);
            _fake = new FakeServices(StartClock());
            _host = NewHost();
            if (Load())
            {
                Made();
                Taps();
                Interact();
                Gestures();
                LongRun();
                Voice();
                Restart();
                if (_entry != null && _entry.Error == null) RestartWhileRunning();
                Unload();
            }
            LogCheck();
            Console.WriteLine((_fails == 0 ? "PASS" : "FAILED (" + _fails + ")") + " [" + _id + "] " + _summary);
        }

        private ScriptHost NewHost() => new ScriptHost(_fake, _scripts, _plugins, Directory.Exists(_projects) ? _projects : null);

        // 1. Load
        private bool Load()
        {
            _host.Scan();
            Step(0.0);
            Step(0.1);
            Step(0.1);
            _entry = _host.Find(_key);
            if (_entry == null)
            {
                Fail("load: the host did not load the plugin" + (_host.ScanNotes.Count > 0 ? ": " + string.Join("; ", _host.ScanNotes) : " (no plugin.json?)"));
                return false;
            }
            _kind = _entry.Project ? _entry.PluginKind : PluginProject.NormalizeKind((string)_entry.Manifest?["kind"]) ?? "app";
            if (_entry.Error != null) { _errorReported = true; Fail("load: the plugin stopped with an error: " + _entry.Error); return false; }
            Ok("loaded without error (" + _entry.Title + (_entry.Version.Length > 0 ? " " + _entry.Version : "") + "), kind " + _kind +
               (_entry.Project ? " (a plugin project: " + _entry.LinuxDir + ")" : " (an older plugin folder)"));
            return true;
        }

        // 2. Windows and tiles
        private void Made()
        {
            var wins = OwnWindows();
            var tiles = OwnTiles();
            _loadWindowIds = wins.Select(w => w.Id).ToList();
            _loadTileIds = tiles.Select(t => t.Id).ToList();
            string tileList = string.Join(", ", tiles.Select(t => t.Label + (t.Window != null ? " -> " + t.Window : " (runs a function)")));
            if (_kind == "app")
            {
                Check(wins.Count >= 1, "made " + wins.Count + " window(s): " + string.Join(", ", _loadWindowIds));
                Check(tiles.Count >= 1, "made " + tiles.Count + " tile(s): " + tileList);
            }
            else
            {
                Ok("a " + _kind + " plugin: " + wins.Count + " window(s), " + tiles.Count + " tile(s) (none needed)");
                if (tiles.Count > 0 && _entry.Project)
                    Fail("tile(s) " + tileList + ": only app plugins get tiles on the main menu, a " + _kind + " plugin's menu.add is never shown; remove it (or make an app plugin)");
            }
            foreach (var t in tiles.Where(t => t.Window != null))
                Check(wins.Any(w => w.Id == t.Window), "tile " + t.Label + " points to one of the plugin's windows (" + t.Window + ")");
            foreach (var w in wins) PrintSpec(w, "spec");
            if (_entry.NetworkHosts.Length > 0 || _fake.NetRequests.Count > 0)
                Ok("network: declares " + (_entry.NetworkHosts.Length == 0 ? "no host" : string.Join(", ", _entry.NetworkHosts)) + "; " + _fake.NetRequests.Count + " request(s) while loading" +
                   (_fake.NetRequests.Count > 0 ? " (" + string.Join(", ", _fake.NetRequests.Select(r => r.Method + " " + Cut(r.Url, 70)).Take(3)) + ")" : ""));
        }

        // 3. Tap each tile
        private void Taps()
        {
            foreach (var tile in OwnTiles())
            {
                if (tile.Window == null)
                {
                    _host.TileTapped(tile.Owner, tile.Id);
                    StepFor(0.3);
                    if (NoError("tile " + tile.Label + " run")) Ok("tile " + tile.Label + " ran its function without error");
                    continue;
                }
                bool wasOpen = _fake.PluginWindowIsOpen(tile.Window);
                int opensBefore = _fake.Windows.TryGetValue(tile.Window, out var w0) ? w0.OpenEvents : 0;
                _host.TileTapped(tile.Owner, tile.Id);
                StepFor(0.3);
                if (wasOpen)
                {
                    // it was open already (main.lua opened it): the tap closed it; tap again to open it
                    _host.TileTapped(tile.Owner, tile.Id);
                    StepFor(0.3);
                }
                if (!NoError("tile " + tile.Label + " tap")) continue;
                _fake.Windows.TryGetValue(tile.Window, out var w);
                bool open = _fake.PluginWindowIsOpen(tile.Window);
                Check(open && w != null && w.OpenEvents > opensBefore,
                    "tile " + tile.Label + ": window " + tile.Window + " is open after the tap and got an open event" + (w != null ? " (" + w.OpenEvents + " so far)" : ""));
            }
        }

        // 4. Use every interactive thing in each window's last spec
        private void Interact()
        {
            var done = new HashSet<string>();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int round = 0; round < 3 && _entry.Error == null; round++)
            {
                int sent = 0;
                foreach (var win in OwnWindows())
                {
                    if (!(win.Spec["components"] is JArray comps)) continue;
                    sent += UseAll(comps, win, win.Id + "#", done, counts);
                }
                StepFor(1.2); // lets the host send the rebuilt spec (one full rebuild a second a window)
                if (sent == 0) break;
            }
            int notices = 0;
            foreach (var n in _fake.Notices.Take(5).ToList())
            {
                if (n.OnTap == null || _entry.Error != null) continue;
                try { n.OnTap(); }
                catch (Exception ex) { Fail("notification tap threw: " + ex.Message); }
                StepFor(0.3);
                notices++;
            }
            if (notices > 0) counts["notification tap"] = notices;
            if (NoError("interactions"))
                Ok("used " + (counts.Count > 0 ? string.Join(", ", counts.Select(kv => kv.Value + " " + kv.Key)) : OwnWindows().Count == 0 ? "nothing (no window)" : "nothing (the windows have no interactive component)") + "; no error");
            if (Program.Verbose) foreach (var w in OwnWindows()) PrintSpec(w, "last spec");
        }

        /// Uses every interactive component of a list, and those inside cards, rows and tabs (the component library):
        /// what a tap, a pick or a drag would send. Returns the events sent.
        private int UseAll(JArray comps, WindowRec win, string at, HashSet<string> done, SortedDictionary<string, int> counts)
        {
            int sent = 0;
            var list = comps.ToList();
            for (int ci = 0; ci < list.Count; ci++)
            {
                if (!(list[ci] is JObject comp)) continue;
                string type = (string)comp["type"] ?? "";
                string cid = (string)comp["id"] ?? "";
                string prefix = at + ci + "|";
                if (type == "buttons" && comp["items"] is JArray items)
                {
                    foreach (var it in items.OfType<JObject>())
                        sent += Send(done, counts, win, prefix + "button|" + (string)it["id"], "button", "button", (string)it["id"] ?? "", (string)it["label"] ?? "");
                }
                else if (type == "toggle" || type == "checkbox")
                {
                    bool cur = comp["value"] != null && comp["value"].Type == JTokenType.Boolean && (bool)comp["value"];
                    sent += Send(done, counts, win, prefix + "toggle", type, "toggle", cid, cur ? "false" : "true");
                }
                else if (type == "grid" && comp["cells"] is JArray cells)
                {
                    int taps = 0;
                    foreach (var cell in cells.OfType<JObject>())
                    {
                        string cellId = (string)cell["id"];
                        if (string.IsNullOrEmpty(cellId)) continue;
                        if (taps++ >= 3) break;
                        sent += Send(done, counts, win, prefix + "cell|" + cellId, "cell", "cell", cellId, (string)cell["text"] ?? "");
                    }
                }
                else if ((type == "choices" || type == "buttons") && comp["options"] is JArray opts && opts.Count > 0)
                {
                    string v = OptionValue(opts[0]);
                    sent += Send(done, counts, win, prefix + "choice", "choice", "choice", cid, v);
                }
                else if (type == "input" || type == "field")
                {
                    sent += Send(done, counts, win, prefix + "input", type, "input", cid, InputText);
                }
                else if ((type == "radio" || type == "segmented" || type == "select") && comp["options"] is JArray o2 && o2.Count > 0)
                {
                    // the first option that is not the current one, then the last
                    string cur = (string)comp["value"];
                    var vals = o2.Select(OptionValue).ToList();
                    string pick = vals.FirstOrDefault(x => x != cur) ?? vals[0];
                    sent += Send(done, counts, win, prefix + type + "|1", type, type, cid, pick);
                    if (vals.Count > 1 && vals[vals.Count - 1] != pick) sent += Send(done, counts, win, prefix + type + "|2", type, type, cid, vals[vals.Count - 1]);
                }
                else if (type == "slider")
                {
                    double mn = Num(comp["min"], 0), mx = Num(comp["max"], 100), st = Num(comp["step"], 0);
                    double v = mn + (mx - mn) * 0.5;
                    if (st > 0) v = mn + Math.Round((v - mn) / st) * st;
                    sent += Send(done, counts, win, prefix + "slider|mid", "slider", "slider", cid, N(v));
                    sent += Send(done, counts, win, prefix + "slider|max", "slider", "slider", cid, N(mx));
                }
                else if (type == "stepper")
                {
                    double st = Num(comp["step"], 1), v = Num(comp["value"], 0), mx = Num(comp["max"], double.MaxValue), mn = Num(comp["min"], double.MinValue);
                    sent += Send(done, counts, win, prefix + "stepper|up", "stepper", "stepper", cid, N(Math.Min(mx, v + st)));
                    sent += Send(done, counts, win, prefix + "stepper|down", "stepper", "stepper", cid, N(Math.Max(mn, v - st)));
                }
                else if (type == "date")
                {
                    bool time = comp["time"] != null && comp["time"].Type == JTokenType.Boolean && (bool)comp["time"];
                    var d = DateTimeOffset.FromUnixTimeSeconds((long)_fake.Clock).LocalDateTime.Date.AddDays(3);
                    string v = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (time ? "T18:30" : "");
                    sent += Send(done, counts, win, prefix + "date", "date", "date", cid, v);
                }
                else if (type == "time")
                {
                    sent += Send(done, counts, win, prefix + "time", "time", "time", cid, "18:30");
                }
                else if (type == "color")
                {
                    string v = comp["options"] is JArray co && co.Count > 0 ? ((string)co[co.Count - 1] ?? "#3987E5").ToUpperInvariant() : "#199E70";
                    sent += Send(done, counts, win, prefix + "color", "color", "color", cid, v);
                }
                else if (type == "list" && comp["items"] is JArray li && li.Any(x => x is JObject xo && xo["id"] != null))
                {
                    int taps = 0;
                    foreach (var it in li.OfType<JObject>())
                    {
                        string rid = (string)it["id"];
                        if (string.IsNullOrEmpty(rid)) continue;
                        if (taps++ >= 3) break;
                        sent += Send(done, counts, win, prefix + "list|" + rid, "list row", "list", rid, (string)it["text"] ?? (string)it["label"] ?? "");
                    }
                }
                else if (type == "canvas" && comp["shapes"] is JArray shapes)
                {
                    int taps = 0;
                    foreach (var sh in shapes.OfType<JObject>())
                    {
                        string sid = (string)sh["id"];
                        if (string.IsNullOrEmpty(sid)) continue;
                        if (taps++ >= 5) break;
                        sent += Send(done, counts, win, prefix + "shape|" + sid, "canvas shape", "shape", sid, ShapePoint(sh));
                    }
                    sent += Send(done, counts, win, prefix + "shape|-", "canvas background", "shape", "", "1,1"); // a tap on no shape
                }
                if (type == "tabs" && comp["tabs"] is JArray tabs)
                {
                    var tl = tabs.OfType<JObject>().ToList();
                    for (int k = 0; k < tl.Count; k++)
                    {
                        sent += Send(done, counts, win, prefix + "tab|" + (string)tl[k]["id"], "tab", "tab", cid, (string)tl[k]["id"] ?? "");
                        if (tl[k]["components"] is JArray kids) sent += UseAll(kids, win, prefix + "t" + k + "/", done, counts);
                    }
                }
                if (comp["components"] is JArray inner) sent += UseAll(inner, win, prefix + "/", done, counts);
            }
            return sent;
        }

        private static double Num(JToken t, double def) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (double)t : def;
        private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// A point inside a canvas shape (what a tap on it sends as the value): a rect's middle, a circle's centre, the
        /// first point of a line or polygon.
        private static string ShapePoint(JObject s)
        {
            double x = Num(s["x"], 0), y = Num(s["y"], 0);
            if ((string)s["type"] == "rect") { x += Num(s["w"] ?? s["width"], 0) / 2; y += Num(s["h"] ?? s["height"], 0) / 2; }
            else if (s["points"] is JArray pts && pts.Count > 0)
            {
                var p0 = pts[0];
                if (p0 is JArray pa && pa.Count >= 2) { x = Num(pa[0], 0); y = Num(pa[1], 0); }
                else if (p0 is JObject po) { x = Num(po["x"] ?? po["lon"], 0); y = Num(po["y"] ?? po["lat"], 0); }
            }
            else if ((string)s["type"] == "line") { x = Num(s["x1"], 0); y = Num(s["y1"], 0); }
            return N(x) + "," + N(y);
        }

        private static string OptionValue(JToken o)
        {
            if (o.Type == JTokenType.String) return (string)o;
            if (o is JObject jo) return (string)jo["value"] ?? (string)jo["label"] ?? (string)jo["text"] ?? (string)jo["id"] ?? "";
            return o.ToString();
        }

        /// One window event, then 0.1 s of app time and a check that the plugin is still fine. Returns 1 when sent.
        private int Send(HashSet<string> done, SortedDictionary<string, int> counts, WindowRec win, string key, string what, string type, string id, string value)
        {
            if (_entry.Error != null || !done.Add(key)) return 0;
            _fake.Events++;
            _host.WindowEvent(win.Id, type, id, value);
            Step(0.1);
            counts[what] = (counts.TryGetValue(what, out var c) ? c : 0) + 1;
            NoError(what + " " + (id.Length > 0 ? id : value) + " in " + win.Id);
            return 1;
        }

        // 4b. Gestures: what it defines and reacts to; each reaction simulated; canonical hand shapes through the engine
        private static readonly string[] BuiltIns = { "pinch", "grab", "point", "open", "palm-up", "clap", "select" };

        private (int toasts, int actions, int panels, int notices, int said, int runs, int sounds) Snap() =>
            (_fake.Toasts.Count, _fake.Actions.Count, _fake.PanelCalls.Count, _fake.Notices.Count, _fake.Said.Count, _fake.VoiceRuns.Count, _fake.Sounds.Count);

        private List<string> Effects((int toasts, int actions, int panels, int notices, int said, int runs, int sounds) s)
        {
            var l = new List<string>();
            l.AddRange(_fake.Toasts.Skip(s.toasts).Select(t => "toast \"" + Cut(t.text, 40) + "\""));
            l.AddRange(_fake.Actions.Skip(s.actions).Select(a => "action " + Cut(a, 40)));
            l.AddRange(_fake.PanelCalls.Skip(s.panels));
            l.AddRange(_fake.Notices.Skip(s.notices).Select(n => "notify \"" + Cut(n.Text, 40) + "\""));
            l.AddRange(_fake.Said.Skip(s.said).Select(x => "say \"" + Cut(x, 40) + "\""));
            l.AddRange(_fake.VoiceRuns.Skip(s.runs).Select(x => "voice run \"" + Cut(x, 40) + "\""));
            l.AddRange(_fake.Sounds.Skip(s.sounds).Select(x => "sound " + x));
            return l;
        }

        private void DispatchAll(List<MetaCodex.Hands.GestureEvent> evs)
        {
            foreach (var ev in evs)
            {
                if (_entry.Error != null) return;
                _host.Dispatch(ev);
            }
        }

        private void Gestures()
        {
            if (_entry.Error != null) return;
            var eng = _fake.Engine;
            var own = eng.Custom.Where(c => c.owner == _key).ToList();
            var ownNames = own.Select(c => c.name).Distinct().ToList();
            var handled = _host.HandledGestures(_entry);
            if (own.Count == 0 && handled.Count == 0)
            {
                if (_kind == "gesture") Fail("a gesture plugin defines or reacts to at least one gesture (poses/<name>.json, gesture.pose, gesture.define, gesture.on)");
                return;
            }
            Ok("gestures: defines " + (own.Count == 0 ? "none" : string.Join(", ", own.Select(c => c.name + " (" + c.kind + ")"))) +
               "; reacts to " + (handled.Count == 0 ? "none" : string.Join(", ", handled)));
            foreach (var n in handled)
                if (Array.IndexOf(BuiltIns, n) < 0 && !ownNames.Contains(n))
                    Fail("reacts to '" + n + "', which neither this plugin nor the built-ins define (a fresh headset has no example poses: define it in this plugin, poses/" + n + ".json or gesture.define)");
            foreach (var n in own.Select(c => c.name).Distinct())
                if (Array.IndexOf(BuiltIns, n) >= 0) Fail("defines '" + n + "', a built-in gesture's name: pick another name");
            // each reaction, simulated as the app would send it
            float t = (float)_t;
            foreach (var n in handled.Where(n => Array.IndexOf(BuiltIns, n) >= 0 || ownNames.Contains(n)))
            {
                var snap = Snap();
                string hand = n == "clap" ? "both" : "right";
                if (n == "clap" || n == "select") DispatchAll(new List<MetaCodex.Hands.GestureEvent> { new MetaCodex.Hands.GestureEvent { Name = n, Hand = "both", Phase = "tap", Time = t += 0.05f, Detail = "harness" } });
                else
                {
                    DispatchAll(new List<MetaCodex.Hands.GestureEvent> { new MetaCodex.Hands.GestureEvent { Name = n, Hand = hand, Phase = "start", Time = t += 0.05f, Detail = "harness" } });
                    DispatchAll(new List<MetaCodex.Hands.GestureEvent> { new MetaCodex.Hands.GestureEvent { Name = n, Hand = hand, Phase = "end", Time = t += 0.3f, Detail = "harness" } });
                }
                Step(0.1);
                if (!NoError("gesture " + n)) return;
                var fx = Effects(snap);
                Ok("gesture " + n + " (" + (n == "clap" || n == "select" ? "both tap" : hand + " start, end") + ") -> " + (fx.Count == 0 ? "nothing visible" : string.Join(", ", fx.Take(4))));
            }
            foreach (var (steps, window) in _host.SequencesOf(_entry))
            {
                if (steps.Any(n => Array.IndexOf(BuiltIns, n) < 0 && !ownNames.Contains(n))) continue;
                var snap = Snap();
                float gap = Math.Min(0.25f, window / (steps.Length + 1));
                foreach (var n in steps)
                    DispatchAll(new List<MetaCodex.Hands.GestureEvent> { new MetaCodex.Hands.GestureEvent { Name = n, Hand = n == "clap" ? "both" : "right", Phase = n == "clap" || n == "select" ? "tap" : "start", Time = t += gap, Detail = "harness" } });
                Step(0.1);
                if (!NoError("sequence " + string.Join(" ", steps))) return;
                var fx = Effects(snap);
                Check(fx.Count > 0, "sequence " + string.Join(", ", steps) + " within " + window.ToString("0.0#", CultureInfo.InvariantCulture) + " s -> " + (fx.Count == 0 ? "nothing visible (its function did nothing the app can see)" : string.Join(", ", fx.Take(4))));
            }
            _t = t;
            // the hands: each shape held 1 s (36 detector passes), both hands, then the right hand alone
            if (own.Count == 0) return;
            var shapes = HandShapes();
            var startedAny = new HashSet<string>();
            foreach (var both in new[] { true, false })
                foreach (var (shape, set) in shapes)
                {
                    eng.Clear((float)_t);
                    DispatchAll(eng.Events.ToList()); eng.Events.Clear();
                    Reset(eng.Left); Reset(eng.Right);
                    StepFor(0.2);
                    var snap = Snap();
                    var started = new List<string>();
                    set(eng.Right); eng.Right.Tracked = true;
                    if (both) { set(eng.Left); eng.Left.Tracked = true; }
                    for (int i = 0; i < 36 && _entry.Error == null; i++)
                    {
                        _t += 1.0 / 36; _fake.Clock += 1.0 / 36;
                        eng.Tick((float)_t);
                        eng.RunCustomTick((float)_t, 1f / 36f);
                        var evs = eng.Events.ToList(); eng.Events.Clear();
                        foreach (var ev in evs) if (ev.Phase == "start" && ownNames.Contains(ev.Name)) started.Add(ev.Name + "(" + ev.Hand + ")");
                        DispatchAll(evs);
                        _host.Tick((float)_t);
                        DrainProblems();
                    }
                    if (!NoError("hands " + shape)) { Reset(eng.Left); Reset(eng.Right); return; }
                    var fx = Effects(snap);
                    if (started.Count == 0) continue;
                    foreach (var x in started) startedAny.Add(x);
                    Ok("hands " + shape + (both ? " (both hands)" : " (right hand)") + ": " + string.Join(", ", started.Distinct()) + " started -> " + (fx.Count == 0 ? "nothing visible" : string.Join(", ", fx.Distinct().Take(4))));
                }
            eng.Clear((float)_t); DispatchAll(eng.Events.ToList()); eng.Events.Clear();
            Reset(eng.Left); Reset(eng.Right);
            if (startedAny.Count == 0) Ok("hands: none of its gestures started with the harness's shapes (" + string.Join(", ", shapes.Select(x => x.name)) + "); that proves nothing about real hands");
            foreach (var d in _host.DetectorCosts().Where(d => d.owner == _id))
                Ok("detector " + d.name + ": " + d.micros.ToString("0", CultureInfo.InvariantCulture) + " us a call on this PC");
        }

        private static void Reset(MetaCodex.Hands.HandState h)
        {
            h.Tracked = false; h.Pinching = h.Grabbing = h.Pointing = h.Open = h.PalmUp = false; h.Pinch = 0f;
            for (int i = 0; i < 5; i++) h.Fingers[i] = 0f;
            h.ThumbDir = h.PalmNormal = h.Forward = h.Velocity = UnityEngine.Vector3.zero;
            h.Frame = new MetaCodex.Hands.HandFrame { Tracked = false, HighConfidence = false, Palm = UnityEngine.Vector3.zero };
        }

        /// Hand shapes as the engine reports them (curls 0 straight .. 1 curled; x right, y up, z forward).
        private static List<(string name, Action<MetaCodex.Hands.HandState> set)> HandShapes()
        {
            var up = new UnityEngine.Vector3(0, 1, 0); var down = new UnityEngine.Vector3(0, -1, 0); var fwd = new UnityEngine.Vector3(0, 0, 1);
            void Base(MetaCodex.Hands.HandState h, float t, float i, float m, float r, float l)
            {
                h.Fingers[0] = t; h.Fingers[1] = i; h.Fingers[2] = m; h.Fingers[3] = r; h.Fingers[4] = l;
                h.Frame = new MetaCodex.Hands.HandFrame { Tracked = true, HighConfidence = true, Palm = new UnityEngine.Vector3(h.Side == "left" ? -0.15f : 0.15f, 1.2f, 0.35f) };
                h.PalmNormal = down; h.ThumbDir = fwd; h.Forward = fwd;
            }
            return new List<(string, Action<MetaCodex.Hands.HandState>)>
            {
                ("open palm down", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; }),
                ("open palm up", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; h.PalmUp = true; h.PalmNormal = up; }),
                ("open palm away", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; h.PalmNormal = fwd; }),
                ("fist", h => { Base(h, 1, 1, 1, 1, 1); h.Grabbing = true; }),
                ("point", h => { Base(h, 1, 0, 1, 1, 1); h.Pointing = true; }),
                ("pinch", h => { Base(h, 0.4f, 0.4f, 0, 0, 0); h.Pinch = 1f; h.Pinching = true; }),
                ("thumbs up", h => { Base(h, 0, 1, 1, 1, 1); h.ThumbDir = up; }),
                ("thumbs down", h => { Base(h, 0, 1, 1, 1, 1); h.ThumbDir = down; }),
                ("victory", h => { Base(h, 1, 0, 0, 1, 1); }),
                ("open hand moving left fast", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; h.PalmNormal = new UnityEngine.Vector3(-1, 0, 0); h.Velocity = new UnityEngine.Vector3(-1.6f, 0, 0); }),
                ("open hand moving right fast", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; h.PalmNormal = new UnityEngine.Vector3(1, 0, 0); h.Velocity = new UnityEngine.Vector3(1.6f, 0, 0); }),
                ("open hand pushed away fast", h => { Base(h, 0, 0, 0, 0, 0); h.Open = true; h.PalmNormal = fwd; h.Velocity = new UnityEngine.Vector3(0, 0, 1.4f); }),
            };
        }

        // 5. Three hours of time
        private void LongRun()
        {
            // the main action first (the first `accent` button of each window: START, +1 ...), so something runs
            // during the 3 hours (the interactions above may have left it stopped)
            var main = new List<string>();
            foreach (var win in OwnWindows())
            {
                if (!(win.Spec["components"] is JArray comps)) continue;
                var it = comps.OfType<JObject>().Where(c => (string)c["type"] == "buttons" && c["items"] is JArray)
                    .SelectMany(c => ((JArray)c["items"]).OfType<JObject>()).FirstOrDefault(x => (string)x["tone"] == "accent");
                if (it == null || _entry.Error != null) continue;
                _fake.Events++;
                _host.WindowEvent(win.Id, "button", (string)it["id"] ?? "", (string)it["label"] ?? "");
                Step(0.1);
                NoError("main action " + (string)it["id"] + " in " + win.Id);
                main.Add((string)it["label"]);
            }
            if (main.Count > 0) Ok("pressed the main action before the long run: " + string.Join(", ", main));
            StepFor(1.2);
            int net0 = _fake.NetRequests.Count;
            var builds0 = _fake.Windows.ToDictionary(kv => kv.Key, kv => kv.Value.Builds);
            var texts0 = _fake.Windows.ToDictionary(kv => kv.Key, kv => kv.Value.TextUpdates);
            int notices0 = _fake.Notices.Count;
            // the worst minute: a window that rebuilds more than 3 times in a minute with nobody touching it redraws
            // the whole window for a countdown (use a LIVE text: a text component with an id)
            var minuteStart = _fake.Windows.ToDictionary(kv => kv.Key, kv => kv.Value.Builds);
            var worst = new Dictionary<string, int>();
            for (int i = 0; i < RunSeconds; i++)
            {
                Step(1.0);
                if (_entry.Error != null) { NoError("after " + (i + 1) + " s of app time"); break; }
                if ((i + 1) % 60 == 0)
                {
                    foreach (var kv in _fake.Windows)
                    {
                        int d = kv.Value.Builds - (minuteStart.TryGetValue(kv.Key, out var m0) ? m0 : 0);
                        if (!worst.TryGetValue(kv.Key, out var wv) || d > wv) worst[kv.Key] = d;
                        minuteStart[kv.Key] = kv.Value.Builds;
                    }
                }
            }
            foreach (var kv in worst)
                if (kv.Key.StartsWith(_prefix, StringComparison.Ordinal))
                    Check(kv.Value <= 3, "window " + kv.Key + ": at most " + kv.Value + " full rebuilds in any minute with nobody touching it (limit 3)" +
                        (kv.Value <= 3 ? "" : "; a countdown must change only a LIVE text (a text component with an id) and keep the rest of the spec the same"));
            if (_entry.Error != null) return;
            int netRun = _fake.NetRequests.Count - net0;
            if (netRun > 0 || _entry.NetworkHosts.Length > 0)
                Check(netRun <= RunSeconds / 60, "network in 3 h with nobody touching it: " + netRun + " request(s) (limit " + RunSeconds / 60 + ", one a minute on average)" +
                    (netRun <= RunSeconds / 60 ? "" : "; refresh less often (keep the last reply in store and show its age)"));
            var parts = new List<string>();
            foreach (var w in OwnWindows())
            {
                int b = w.Builds - (builds0.TryGetValue(w.Id, out var b0) ? b0 : 0);
                int tx = w.TextUpdates - (texts0.TryGetValue(w.Id, out var t0) ? t0 : 0);
                double rate = b / (double)RunSeconds;
                parts.Add(w.Id.Substring(_prefix.Length) + ": " + b + " rebuilds, " + tx + " live text updates");
                Check(rate <= MaxRebuildsPerSecond,
                    "window " + w.Id + " in 3 h: " + b + " full rebuilds (" + rate.ToString("0.000", CultureInfo.InvariantCulture) + " a second, limit " +
                    MaxRebuildsPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + "), " + tx + " live text updates" +
                    (rate <= MaxRebuildsPerSecond ? "" : "; it redraws the whole window too often, use a text component with an id for the countdown"));
            }
            if (parts.Count == 0 && _loadWindowIds.Count > 0) Fail("no window left after 3 h of app time");
            Ok("3 h of app time and wall clock in 1 s steps, no error; " + (_fake.Notices.Count - notices0) + " notification(s) in that time");
        }

        // 6. Voice commands
        private void Voice()
        {
            var cmds = OwnVoice();
            try { _fake.Voice.Describe(); }
            catch (Exception ex) { Fail("voice registry Describe() threw: " + ex.Message); }
            if (cmds.Count == 0) Ok("no voice commands registered");
            foreach (var c in cmds)
            {
                var warns = VoiceCommands.Lint(c);
                if (warns.Count == 0) Ok("voice " + c.Name + ": lint is clean");
                else foreach (var w in warns) Fail("voice " + c.Name + ": lint: " + w);
                if (_entry.Error != null) continue;

                var args = new JObject();
                string how = "empty arguments";
                var candidates = new List<string>();
                if (c.Samples != null && c.Samples.Length > 0) candidates.Add(c.Samples[0]);
                if (c.Phrases != null && c.Phrases.Length > 0)
                {
                    try { candidates.Add(VoiceCommands.Example(c.Phrases[0])); } catch (Exception) { }
                }
                foreach (var text in candidates)
                {
                    VoiceMatch m = null;
                    try { m = string.IsNullOrEmpty(text) ? null : _fake.Voice.FastMatch(text); } catch (Exception) { }
                    if (m == null || m.Command != c) continue;
                    args = (JObject)m.Args.DeepClone();
                    how = "arguments from \"" + text + "\"";
                    break;
                }
                string bad = _fake.Voice.Validate(c, args);
                if (bad != null) { Ok("voice " + c.Name + ": not run (" + bad + "; " + how + ")"); continue; }
                string reply = null;
                try
                {
                    if (c.Run != null) reply = c.Run(args);
                    else c.RunAsync?.Invoke(args, r => reply = r);
                }
                catch (Exception ex) { Fail("voice " + c.Name + ": threw " + ex.Message); continue; }
                Step(0.1);
                if (!NoError("voice " + c.Name)) continue;
                if (reply != null && reply.StartsWith("error", StringComparison.OrdinalIgnoreCase)) Fail("voice " + c.Name + " (" + how + ") answered: " + Cut(reply, 120));
                else Ok("voice " + c.Name + " ran with " + how + (reply != null ? " -> " + Cut(reply, 80) : ""));
            }
            var wins = OwnWindows();
            _summary = "windows=" + wins.Count + " tiles=" + OwnTiles().Count + " voice=" + cmds.Count +
                       " builds=" + wins.Sum(w => w.Builds) + " text-updates=" + wins.Sum(w => w.TextUpdates) +
                       " notifications=" + _fake.Notices.Count + " events=" + _fake.Events + (_fake.NetRequests.Count > 0 ? " net=" + _fake.NetRequests.Count : "");
        }

        // 7. Restart: a new app and a new host on the same folders
        private SortedDictionary<string, string> ReadStore()
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string path = Path.Combine(_scripts, ".state.json");
            if (!File.Exists(path)) return d;
            var j = JObject.Parse(File.ReadAllText(path));
            foreach (var st in (j["stores"] as JArray) ?? new JArray())
            {
                if ((string)st["script"] != _key) continue;
                foreach (var kv in (st["values"] as JArray) ?? new JArray()) d[(string)kv["k"]] = (string)kv["v"];
            }
            return d;
        }

        private void Restart()
        {
            var storeBefore = ReadStore();
            CollectLog();
            _fake = new FakeServices(_fake.Clock);
            _host = NewHost();
            _errorReported = false;
            _host.Scan();
            Step(0.0);
            Step(0.1);
            Step(0.1);
            _entry = _host.Find(_key);
            if (_entry == null) { Fail("restart: the host did not find the plugin"); return; }
            if (!NoError("restart")) return;
            var winIds = OwnWindows().Select(w => w.Id).ToList();
            var tileIds = OwnTiles().Select(t => t.Id).ToList();
            Check(winIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(_loadWindowIds.OrderBy(x => x, StringComparer.Ordinal)),
                "restart: the same windows come back (" + string.Join(", ", winIds) + ")" + (winIds.Count == _loadWindowIds.Count ? "" : "; first start made " + string.Join(", ", _loadWindowIds)));
            Check(tileIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(_loadTileIds.OrderBy(x => x, StringComparer.Ordinal)),
                "restart: the same tiles come back (" + string.Join(", ", tileIds) + ")");
            var storeAfter = ReadStore();
            var diff = new List<string>();
            foreach (var k in storeBefore.Keys.Union(storeAfter.Keys))
            {
                storeBefore.TryGetValue(k, out var a);
                storeAfter.TryGetValue(k, out var b);
                if (a != b) diff.Add(k + ": " + Cut(a ?? "(none)", 40) + " -> " + Cut(b ?? "(none)", 40));
            }
            Check(diff.Count == 0, "restart: the store keeps its values (" + storeBefore.Count + " key(s))" + (diff.Count == 0 ? "" : "; changed: " + string.Join("; ", diff.Take(5))));
        }

        // 7b. Restart while the main action runs (a countdown): it must go on by itself after the restart
        private int Activity() => OwnWindows().Sum(w => w.Builds + w.TextUpdates);

        private void PressMain()
        {
            foreach (var win in OwnWindows())
            {
                if (!(win.Spec["components"] is JArray comps)) continue;
                var it = comps.OfType<JObject>().Where(c => (string)c["type"] == "buttons" && c["items"] is JArray)
                    .SelectMany(c => ((JArray)c["items"]).OfType<JObject>()).FirstOrDefault(x => (string)x["tone"] == "accent");
                if (it == null || _entry.Error != null) continue;
                _host.WindowEvent(win.Id, "button", (string)it["id"] ?? "", (string)it["label"] ?? "");
                Step(0.1);
            }
        }

        private void RestartWhileRunning()
        {
            PressMain();
            StepFor(5.0);
            int a0 = Activity();
            StepFor(5.0);
            int before = Activity() - a0;
            if (before < 3) { Ok("restart while running: skipped (the window does not update by itself after the main action)"); return; }
            CollectLog();
            _fake = new FakeServices(_fake.Clock);
            _host = NewHost();
            _errorReported = false;
            _host.Scan();
            Step(0.0);
            _entry = _host.Find(_key);
            if (_entry == null) { Fail("restart while running: the host did not find the plugin"); return; }
            int b0 = Activity();
            StepFor(5.0);
            int after = Activity() - b0;
            if (!NoError("restart while running")) return;
            Check(after >= 3, "restart while running: the window goes on updating by itself (" + before + " updates in 5 s before, " + after + " after)" +
                (after >= 3 ? "" : "; save the END time in store and start the countdown again when main.lua loads"));
        }

        // 8. Unload
        private void Unload()
        {
            if (_entry == null) return;
            var winIds = OwnWindows().Select(w => w.Id).ToList();
            _host.SetEnabled(_key, false);
            StepFor(0.3);
            Check(OwnWindows().Count == 0 && winIds.All(id => _fake.Removed.Contains(id)), "unload: " + winIds.Count + " window(s) removed from the app");
            Check(OwnTiles().Count == 0, "unload: the plugin's tiles are gone");
            Check(OwnVoice().Count == 0, "unload: no voice command of the plugin is left");
            if (_entry.NetworkHosts.Length > 0) Check(_host.NetInFlight(_entry) == 0, "unload: no request of the plugin is left in flight");
        }

        // 9. Log lines
        private void CollectLog()
        {
            foreach (var line in _fake.LogLines)
                if (_badLogRx.IsMatch(line) && _badLogSeen.Add(line)) _badLog.Add(line);
            if (_entry != null)
                foreach (var line in _entry.Log)
                    if ((line.StartsWith("ERROR", StringComparison.Ordinal) || line.StartsWith("warn", StringComparison.Ordinal)) && _badLogSeen.Add("[plugin " + _id + "] " + line))
                        _badLog.Add("[plugin " + _id + "] " + line);
        }

        private void LogCheck()
        {
            CollectLog();
            if (_badLog.Count == 0) { Ok("the plugin log has no ERROR or warn line"); return; }
            foreach (var line in _badLog.Take(10)) Fail("log: " + Cut(line, 300));
            if (_badLog.Count > 10) Fail("log: and " + (_badLog.Count - 10) + " more ERROR or warn lines");
        }
    }
}
