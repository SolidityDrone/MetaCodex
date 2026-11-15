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
            var folders = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--verbose") Verbose = true;
                else if (args[i] == "--run" && i + 1 < args.Length) runDir = args[++i];
                else folders.Add(args[i]);
            }
            if (runDir == null || folders.Count == 0)
            {
                Console.WriteLine("usage: PluginTests.dll --run <run dir> [--verbose] <plugin folder name>...");
                return 2;
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

        private readonly string _raw, _id, _key, _scripts, _plugins, _prefix;
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
            _host = new ScriptHost(_fake, _scripts, _plugins);
            if (Load())
            {
                Made();
                Taps();
                Interact();
                LongRun();
                Voice();
                Restart();
                if (_entry != null && _entry.Error == null) RestartWhileRunning();
                Unload();
            }
            LogCheck();
            Console.WriteLine((_fails == 0 ? "PASS" : "FAILED (" + _fails + ")") + " [" + _id + "] " + _summary);
        }

        // 1. Load
        private bool Load()
        {
            _host.Scan();
            Step(0.0);
            Step(0.1);
            Step(0.1);
            _entry = _host.Find(_key);
            if (_entry == null) { Fail("load: the host did not find the plugin (no plugin.json in " + _plugins + "?)"); return false; }
            if (_entry.Error != null) { _errorReported = true; Fail("load: the plugin stopped with an error: " + _entry.Error); return false; }
            Ok("loaded without error (" + _entry.Title + (_entry.Version.Length > 0 ? " " + _entry.Version : "") + ")");
            return true;
        }

        // 2. Windows and tiles
        private void Made()
        {
            var wins = OwnWindows();
            var tiles = OwnTiles();
            _loadWindowIds = wins.Select(w => w.Id).ToList();
            _loadTileIds = tiles.Select(t => t.Id).ToList();
            Check(wins.Count >= 1, "made " + wins.Count + " window(s): " + string.Join(", ", _loadWindowIds));
            Check(tiles.Count >= 1, "made " + tiles.Count + " tile(s): " + string.Join(", ", tiles.Select(t => t.Label + (t.Window != null ? " -> " + t.Window : " (runs a function)"))));
            foreach (var t in tiles.Where(t => t.Window != null))
                Check(wins.Any(w => w.Id == t.Window), "tile " + t.Label + " points to one of the plugin's windows (" + t.Window + ")");
            foreach (var w in wins) PrintSpec(w, "spec");
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
                    var list = comps.ToList();
                    for (int ci = 0; ci < list.Count; ci++)
                    {
                        if (!(list[ci] is JObject comp)) continue;
                        string type = (string)comp["type"] ?? "";
                        string cid = (string)comp["id"] ?? "";
                        string prefix = win.Id + "#" + ci + "|";
                        if (type == "buttons" && comp["items"] is JArray items)
                        {
                            foreach (var it in items.OfType<JObject>())
                                sent += Send(done, counts, win, prefix + "button|" + (string)it["id"], "button", "button", (string)it["id"] ?? "", (string)it["label"] ?? "");
                        }
                        else if (type == "toggle")
                        {
                            bool cur = comp["value"] != null && comp["value"].Type == JTokenType.Boolean && (bool)comp["value"];
                            sent += Send(done, counts, win, prefix + "toggle", "toggle", "toggle", cid, cur ? "false" : "true");
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
                        else if (type == "input")
                        {
                            sent += Send(done, counts, win, prefix + "input", "input", "input", cid, InputText);
                        }
                    }
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
                Ok("used " + (counts.Count == 0 ? "nothing (the windows have no interactive component)" : string.Join(", ", counts.Select(kv => kv.Value + " " + kv.Key))) + "; no error");
            if (Program.Verbose) foreach (var w in OwnWindows()) PrintSpec(w, "last spec");
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
            if (parts.Count == 0) Fail("no window left after 3 h of app time");
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
                       " notifications=" + _fake.Notices.Count + " events=" + _fake.Events;
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
            _host = new ScriptHost(_fake, _scripts, _plugins);
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
            _host = new ScriptHost(_fake, _scripts, _plugins);
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
