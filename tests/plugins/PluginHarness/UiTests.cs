// The window API's cheap paths (ScriptHost): ui.set and ui.shown, the same-structure fast path of ui.panel, no window
// work while a window is hidden, the ui.panel lint, and the reused runner coroutines (budget, results, allocation).
// Each line "ok   [ui] ..." or "FAIL [ui] ..."; fresh plugin folders under <run dir>/ui.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.Scripting;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;

namespace PluginHarness
{
    internal static class UiTests
    {
        private static void Check(bool cond, string msg)
        {
            Console.WriteLine((cond ? "ok   [ui] " : "FAIL [ui] ") + msg);
            if (!cond) Program.Fails++;
        }

        /// One plugin (an older plugin folder) on its own fake app and host.
        private sealed class Rig
        {
            public FakeServices Fake;
            public ScriptHost Host;
            public ScriptHost.Entry E;
            public string Win;
            public double T = 100;

            public void Step(double dt)
            {
                Fake.Pump(Host);
                T += dt;
                Fake.Clock += dt;
                Host.Tick((float)T);
            }

            public void For(double seconds, double dt = 0.1)
            {
                int n = Math.Max(1, (int)Math.Round(seconds / dt));
                for (int i = 0; i < n; i++) Step(seconds / n);
            }

            public DynValue G(string name) => E.Script.Globals.Get(name);
            public DynValue R(string key) => E.Script.Globals.Get("R").Table.Get(key);
            public DynValue Call(string fn, params object[] args) => E.Script.Call(E.Script.Globals.Get(fn), args);
            public WindowRec W => Fake.Windows.TryGetValue(Win, out var w) ? w : null;
            public bool Warned(string text) => E.Log.Any(l => l.StartsWith("warn", StringComparison.Ordinal) && l.Contains(text));
        }

        private static Rig Make(string root, string id, string lua)
        {
            string scripts = Path.Combine(root, id, "scripts"), plugins = Path.Combine(root, id, "plugins"), dir = Path.Combine(plugins, id);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\", \"kind\": \"app\" }");
            File.WriteAllText(Path.Combine(dir, "main.lua"), lua);
            var r = new Rig { Fake = new FakeServices(1.8e9), Win = "plugin-" + id + "-main" };
            r.Host = new ScriptHost(r.Fake, scripts, plugins, null);
            r.Host.Scan();
            r.Step(0.0);
            r.Step(0.1);
            r.E = r.Host.Find("plugin/" + id);
            return r;
        }

        private static string Text(JToken spec, string id)
        {
            foreach (var t in spec.SelectTokens("$..[?(@.id == '" + id + "')]"))
                if (t is JObject o && (string)o["type"] == "text") return (string)o["text"];
            return null;
        }

        private const string SetLua = @"
R = {}
R.early = ui.set('main', 'clock', 'x')                     -- before the window exists
local label = 'GO'
local clock = '--:--'
function render()
  ui.panel{ id = 'main', title = 'Set', components = {
    { type = 'text', id = 'clock', style = 'big', text = clock },
    { type = 'card', title = 'inside', components = { { type = 'text', id = 'inner', text = 'a' } } },
    { type = 'toggle', id = 'snd', label = 'Sound', value = true },
    { type = 'buttons', items = { { id = 'go', label = label } } },
  }, on = function(e) end }
end
function relabel(l) label = l; render() end
function setclock(c) clock = c; render() end
render()
menu.add{ id = 'main', label = 'SET', panel = 'main' }
R.missing = ui.set('main', 'nope', 'x')
R.notext = ui.set('main', 'snd', 'x')
R.nowin = ui.set('other', 'clock', 'x')
R.e1 = select(2, pcall(ui.set, 'main'))
R.e2 = select(2, pcall(ui.set, 'main', 'clock', nil))
R.e3 = select(2, pcall(ui.shown))
R.e4 = select(2, pcall(ui.set, 'main', 'clock', {}))
";

        public static void Run(string runDir)
        {
            Console.WriteLine("== ui: ui.set, ui.shown, the LIVE fast path, hidden windows, the lint, reused coroutines");
            string root = Path.Combine(runDir, "ui");
            if (Directory.Exists(root)) ScriptHost.DeleteTree(root);
            Directory.CreateDirectory(root);
            SetAndShown(root);
            FastPathAndLint(root);
            Runners(root);
        }

        private static void SetAndShown(string root)
        {
            var r = Make(root, "uiset", SetLua);
            Check(r.E != null && r.E.Error == null, "ui.set test plugin loads" + (r.E?.Error != null ? ": " + r.E.Error : ""));
            if (r.E == null || r.E.Error != null) return;
            Check(r.R("early").Type == DataType.Boolean && !r.R("early").Boolean, "ui.set before the window exists: false, no error");
            Check(!r.R("missing").Boolean && !r.R("notext").Boolean && !r.R("nowin").Boolean,
                "ui.set on a missing id, on a component that is no text (a toggle), on another window: false");
            string e1 = r.R("e1").CastToString() ?? "", e2 = r.R("e2").CastToString() ?? "", e3 = r.R("e3").CastToString() ?? "", e4 = r.R("e4").CastToString() ?? "";
            Check(e1.Contains("ui.set(window, id, text)") && e2.Contains("text must be a string") && e4.Contains("text must be a string") && e3.Contains("ui.shown(window)"),
                "bad arguments are clear errors: " + e1 + " | " + e2 + " | " + e3);
            var w = r.W;
            Check(w != null && w.Builds == 1 && !w.Built, "the window was sent once at the load and is not built (closed)");

            // closed: ui.set goes nowhere until the window opens
            r.E.Script.DoString("function shown() return ui.shown('main') end");
            Check(!r.Call("shown").Boolean, "ui.shown is false while the window is closed");
            r.E.Script.DoString("function set(id, t) return ui.set('main', id, t) end");
            bool ok = r.Call("set", "clock", "12:00").Boolean;
            r.For(2.0);
            Check(ok && w.Builds == 1 && w.TextUpdates == 0 && Text(w.Spec, "clock") == "--:--",
                "ui.set while closed: true, and nothing reaches the app (builds " + w.Builds + ", text updates " + w.TextUpdates + ")");
            r.Host.OpenWindowOf(r.E, r.Win);
            r.Step(0.1);
            Check(w.Open && Text(w.Spec, "clock") == "12:00", "opening the window sends the set text first (clock " + Text(w.Spec, "clock") + ")");
            Check(r.Call("shown").Boolean, "ui.shown is true while the window shows");

            // open: one LIVE update, no rebuild; numbers are texts too; nested texts (in a card) too
            int b0 = w.Builds, t0 = w.TextUpdates;
            r.Call("set", "clock", "12:01");
            r.Step(0.1);
            Check(w.Builds == b0 && w.TextUpdates == t0 + 1 && Text(w.Spec, "clock") == "12:01", "ui.set while open: one LIVE text update, no rebuild");
            r.Call("set", "clock", "12:01");
            r.Step(0.1);
            Check(w.TextUpdates == t0 + 1, "the same text again sends nothing");
            Check(r.Call("set", "inner", 42).Boolean, "ui.set takes a number");
            r.Step(0.1);
            Check(Text(w.Spec, "inner") == "42" && w.Builds == b0, "a LIVE text inside a card is set in place (" + Text(w.Spec, "inner") + ")");

            // a rebuild keeps the set text while the plugin's spec still gives the old one; a new spec text wins
            r.Call("relabel", "STOP");
            r.For(1.5);
            Check(w.Builds == b0 + 1 && Text(w.Spec, "clock") == "12:01" && Text(w.Spec, "inner") == "42",
                "a full rebuild (a button label changed) keeps the set texts (clock " + Text(w.Spec, "clock") + ", inner " + Text(w.Spec, "inner") + ")");
            r.Call("setclock", "07:00");
            r.Step(0.1);
            Check(Text(w.Spec, "clock") == "07:00" && w.Builds == b0 + 1, "a new text in the plugin's spec wins over the set one (fast path, no rebuild)");
            r.Call("relabel", "GO");
            r.For(1.5);
            Check(Text(w.Spec, "clock") == "07:00", "and stays after the next rebuild (" + Text(w.Spec, "clock") + ")");

            // the text rules: a big text over 24 characters warns once
            r.Call("set", "clock", "this is far too long for a big text");
            r.Call("set", "clock", "this is far too long for a big text");
            Check(r.E.Log.Count(l => l.StartsWith("warn ui.set main clock")) == 1, "a big text over 24 characters warns once: " + r.E.Log.LastOrDefault(l => l.StartsWith("warn ui.set")));

            // the app closes it (the user): ui.shown false at once
            r.Fake.PluginWindowShow(r.Win, 0);
            r.Step(0.1);
            Check(!r.Call("shown").Boolean, "ui.shown is false after the window is closed");
            Check(r.E.Error == null, "no error in the ui.set plugin" + (r.E.Error != null ? ": " + r.E.Error : ""));
        }

        private const string TickLua = @"
n = 0
local mode = 'panel'
function render()
  ui.panel{ id = 'main', title = 'Tick', components = {
    { type = 'text', id = 'count', style = 'big', text = 'n ' .. n },
    { type = 'row', components = { { type = 'text', id = 'deep', text = 'd ' .. n }, { type = 'buttons', items = { { id = 'go', label = 'GO' } } } } },
    { type = 'grid', columns = 3, cells = { { id = 'a', text = 'A' }, { id = 'b', text = 'B' }, { id = 'c', text = 'C' } } },
  }, on = function(e) if e.type == 'button' then n = n + 1; render() end end }
end
render()
menu.add{ id = 'main', label = 'TICK', panel = 'main' }
app.every(1, function() n = n + 1; render() end)
";

        private static void FastPathAndLint(string root)
        {
            var r = Make(root, "uitick", TickLua);
            Check(r.E != null && r.E.Error == null, "tick test plugin loads" + (r.E?.Error != null ? ": " + r.E.Error : ""));
            if (r.E == null || r.E.Error != null) return;
            var w = r.W;
            int b0 = w.Builds;
            r.Host.Cost.Clear();
            r.Host.MeasureUi = true;
            r.For(10.0, 1.0);
            r.Host.MeasureUi = false;
            Check(r.Host.Cost.FastCalls >= 9 && w.Builds == b0 && w.TextUpdates == 0,
                "closed, 10 s of a LIVE-only ui.panel each second: " + r.Host.Cost.FastCalls + " fast calls of " + r.Host.Cost.Calls + ", nothing sent to the app (builds " + w.Builds + ")");
            Check(!r.Warned(ScriptHost.LintText), "no lint after 10 calls");
            r.Host.OpenWindowOf(r.E, r.Win);
            r.Step(0.1);
            Check(w.Open && Text(w.Spec, "count") == "n " + r.G("n").Number && Text(w.Spec, "deep") == "d " + r.G("n").Number,
                "opening sends the newest texts (top level and in a row): " + Text(w.Spec, "count") + ", " + Text(w.Spec, "deep"));
            int b1 = w.Builds, t1 = w.TextUpdates;
            r.For(10.0, 1.0);
            Check(w.Builds == b1 && w.TextUpdates >= t1 + 9 && Text(w.Spec, "count") == "n " + r.G("n").Number,
                "open, 10 s: " + (w.TextUpdates - t1) + " LIVE text updates, no rebuild (builds " + w.Builds + ")");
            r.For(60.0, 1.0);
            int lint = r.E.Log.Count(l => l.Contains(ScriptHost.LintText));
            Check(lint == 1, "a timer calling ui.panel with only LIVE texts changing: one lint line (" + lint + "): " + r.E.Log.FirstOrDefault(l => l.Contains(ScriptHost.LintText)));
            r.For(120.0, 1.0);
            Check(r.E.Log.Count(l => l.Contains(ScriptHost.LintText)) == 1, "the lint is said once a window a session");

            // user events that only change LIVE texts are no timer: no lint for them
            var q = Make(root, "uitap", TickLua.Replace("app.every(1, function() n = n + 1; render() end)", ""));
            for (int i = 0; i < 40; i++) { q.Host.WindowEvent(q.Win, "button", "go", "GO"); q.Step(0.5); }
            Check(q.E.Error == null && !q.Warned(ScriptHost.LintText), "40 taps in 20 s that change only LIVE texts: no lint (not a timer)");

            // a structural change while closed waits for the open; the app opening it by itself gets it with the open event
            var qw = q.W;
            int b2 = qw.Builds, t2 = qw.TextUpdates;
            q.E.Script.DoString("ui.panel{ id = 'main', title = 'Tick', components = { { type = 'text', id = 'count', text = 'changed' } } }");
            q.For(3.0, 1.0);
            Check(qw.Builds == b2 && qw.TextUpdates == t2, "a new structure while closed: nothing sent (builds " + qw.Builds + ")");
            q.Fake.PluginWindowShow(q.Win, 1); // the app opens it (a restored layout): its open event flushes the host
            q.Step(0.1);
            Check(qw.Builds == b2 + 1 && Text(qw.Spec, "count") == "changed", "the app's own open: the waiting spec arrives with the open event (builds " + qw.Builds + ")");
            Check(r.E.Error == null, "no error in the tick plugin" + (r.E.Error != null ? ": " + r.E.Error : ""));
        }

        private const string RunnerLua = @"
R = { calls = 0, after = 0 }
function busy() local x = 0 for i = 1, 8000 do x = x + i end R.calls = R.calls + 1 return 'done', x end
app.every(0.5, busy)
app.after(1.2, function() coroutine.yield(1) end)                 -- the plugin's own yield: that call stops there
app.after(2.5, function() R.after = R.after + 1 end)
voice.command{ name = 'runner_echo', description = 'Echo a word back (word: any word)', params = { word = { type = 'string', description = 'a word', required = true } },
  phrases = { 'echo {word}' }, samples = { 'Echo apple', 'Echo the word pear', 'Please echo lemon', 'Echo banana now' },
  run = function(a) return 'echo ' .. (a.word or '') end }
";

        private static void Runners(string root)
        {
            var r = Make(root, "uirun", RunnerLua);
            Check(r.E != null && r.E.Error == null, "runner test plugin loads" + (r.E?.Error != null ? ": " + r.E.Error : ""));
            if (r.E == null || r.E.Error != null) return;
            r.For(30.0, 0.25);
            Check(r.E.Error == null && r.R("calls").Number >= 55,
                "60 calls of ~60000 instructions each (millions in all) on reused coroutines: the budget counts a call, not the coroutine's life (" + r.R("calls").Number + " calls" + (r.E.Error != null ? ", " + r.E.Error : "") + ")");
            Check(r.R("after").Number == 1, "a callback that yields by itself does not break the next ones (after = " + r.R("after").Number + ")");
            var cmd = r.Fake.Voice.All.FirstOrDefault(c => c.Name == "runner_echo");
            string said = cmd?.Run?.Invoke(new JObject { ["word"] = "apple" });
            Check(said == "echo apple", "a voice command's result comes back through Run: " + said);

            // a trivial timer: what a call allocates now (a new coroutine a call was 2048 KB)
            var z = Make(root, "uialloc", "k = 0\napp.every(0.25, function() k = k + 1 end)\n");
            z.For(2.0, 0.25);
            double k0 = z.G("k").Number;
            long m0 = GC.GetAllocatedBytesForCurrentThread();
            z.For(20.0, 0.25);
            double calls = z.G("k").Number - k0;
            double perCall = (GC.GetAllocatedBytesForCurrentThread() - m0) / Math.Max(1.0, calls) / 1024.0;
            Check(z.E.Error == null && calls >= 70 && perCall < 64,
                "a trivial timer call allocates " + perCall.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB, the harness's own folder scan included (" + calls + " calls; a new coroutine a call was 2048 KB)");

            r.E.Script.DoString("app.after(0.1, function() while true do end end)");
            r.For(0.5);
            Check(r.E.Error != null && r.E.Error.Contains("instructions"), "an endless loop still stops the plugin: " + r.E.Error);
        }
    }
}
