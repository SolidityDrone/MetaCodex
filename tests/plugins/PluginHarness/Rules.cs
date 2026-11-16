// The plugin project rules (PluginProject, ScriptHost): which folders load, with which kind, and what the log says.
// Each line "ok   [rules] ..." or "FAIL [rules] ..."; a fresh folder under <run dir>/rules.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.Scripting;

namespace PluginHarness
{
    internal static class Rules
    {
        private static int _fails;

        private static void Check(bool cond, string msg)
        {
            Console.WriteLine((cond ? "ok   [rules] " : "FAIL [rules] ") + msg);
            if (!cond) { _fails++; Program.Fails++; }
        }

        private const string Lua = "log('hello')\n";

        /// A project folder: projects/<folder>/<id> with plugin.json (kind: manifestKind, or none) and main.lua; the marker
        /// with markerJson (null: no marker).
        private static string Project(string projects, string folder, string id, string markerJson, string manifestKind)
        {
            string d = Path.Combine(projects, folder, id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "plugin.json"), "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\"" + (manifestKind != null ? ", \"kind\": \"" + manifestKind + "\"" : "") + " }");
            File.WriteAllText(Path.Combine(d, "main.lua"), Lua);
            if (markerJson != null)
            {
                Directory.CreateDirectory(Path.Combine(d, ".metacodex"));
                File.WriteAllText(Path.Combine(d, ".metacodex", "project.json"), markerJson);
            }
            return d;
        }

        private static string Legacy(string plugins, string id)
        {
            string d = Path.Combine(plugins, id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "plugin.json"), "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\" }");
            File.WriteAllText(Path.Combine(d, "main.lua"), Lua);
            return d;
        }

        public static void Run(string runDir)
        {
            Console.WriteLine("== rules: plugin projects (folder + marker, kinds, ids, reload, create, delete)");
            string root = Path.Combine(runDir, "rules");
            if (Directory.Exists(root)) ScriptHost.DeleteTree(root);
            string scripts = Path.Combine(root, "scripts"), plugins = Path.Combine(root, "plugins"), projects = Path.Combine(root, "projects");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(plugins);

            Project(projects, "apps", "good", PluginProject.MarkerJson("app"), "app");
            Project(projects, "apps", "nomarker", null, "app");
            Project(projects, "gestures", "wrongfolder", PluginProject.MarkerJson("app"), "app");
            Project(projects, "apps", "liar", PluginProject.MarkerJson("app"), "gesture");
            Project(projects, "behaviours", "bare", PluginProject.MarkerJson("behaviour"), null);
            Project(projects, "apps", "notplugins", "{ \"type\": \"default\" }", "app");
            Project(projects, "apps", "dup", PluginProject.MarkerJson("app"), "app");
            Project(projects, "gestures", "dup", PluginProject.MarkerJson("gesture"), "gesture");
            Project(projects, "behaviours", "clash", PluginProject.MarkerJson("behaviour"), "behaviour");
            Legacy(plugins, "clash");
            Legacy(plugins, "old");

            var fake = new FakeServices(1.8e9);
            var host = new ScriptHost(fake, scripts, plugins, projects);
            host.Scan();
            host.Tick(0f);
            string Notes() => string.Join(" | ", host.ScanNotes);

            var good = host.Find("plugin/good");
            Check(good != null && good.Error == null && good.Project && good.PluginKind == "app" && good.LinuxDir == "/root/metacodex-projects/plugins/apps/good",
                "folder + marker agree: loaded as a project of kind app, dir " + good?.LinuxDir);
            Check(host.Eval("return plugin == nil") == "true", "the eval sandbox has no plugin table (it is no plugin)");
            Check(host.Find("plugin/nomarker") == null && host.ScanNotes.Any(n => n.StartsWith("plugins/apps/nomarker: not loaded: no marker")),
                "a project folder without the marker is not loaded, and a note says why: " + host.ScanNotes.FirstOrDefault(n => n.Contains("nomarker")));
            Check(host.Check("nomarker").Contains("not loaded: no marker"), "plugin check on it says why: " + host.Check("nomarker").Replace("\n", " / "));
            Check(host.Find("plugin/wrongfolder") == null && host.ScanNotes.Any(n => n.Contains("wrongfolder") && n.Contains("marker says kind app") && n.Contains("plugins/gestures")),
                "a marker whose kind is not its folder's: not loaded (the kind is fixed)");
            var liar = host.Find("plugin/liar");
            Check(liar != null && liar.PluginKind == "app" && liar.Log.Any(l => l.StartsWith("warn plugin.json: \"kind\": \"gesture\"")),
                "plugin.json says another kind: the marker wins (kind " + liar?.PluginKind + ") and the log warns");
            Check(host.Check("liar").Contains("warnings: 1") && host.Check("liar").Contains("kind: app (fixed"), "plugin check on it: kind app (fixed), 1 warning");
            var bare = host.Find("plugin/bare");
            Check(bare != null && bare.PluginKind == "behaviour" && !bare.Log.Any(l => l.StartsWith("warn")), "no kind in plugin.json: the project's kind, no warning");
            Check(host.Find("plugin/notplugins") == null && host.ScanNotes.Any(n => n.Contains("notplugins") && n.Contains("not a plugins marker")), "a marker of another type: not loaded");
            var dup = host.Find("plugin/dup");
            Check(dup != null && dup.PluginKind == "app" && host.Entries.Count(e => e.Name == "dup") == 1 && host.ScanNotes.Any(n => n.StartsWith("plugins/gestures/dup: not loaded: the id dup is taken by plugins/apps/dup")),
                "the same id in two kinds' folders: the first (apps) loads, the other gets a note");
            var clash = host.Find("plugin/clash");
            Check(clash != null && clash.Project && host.Entries.Count(e => e.Name == "clash") == 1 && host.ScanNotes.Any(n => n == "/mnt/quest/plugins/clash: not loaded: the plugin project plugins/behaviours/clash has the same id (the project wins)"),
                "an older folder with a project's id: the project wins, the log says why");
            Check(fake.LogLines.Count(l => l.Contains("/mnt/quest/plugins/clash: not loaded")) == 1, "the note is logged once");
            host.Scan(); host.Scan();
            Check(fake.LogLines.Count(l => l.Contains("/mnt/quest/plugins/clash: not loaded")) == 1, "and not again at every scan");
            Check(host.Check("clash").Contains("warn /mnt/quest/plugins/clash"), "plugin check on the project shows the older folder it hides");
            var old = host.Find("plugin/old");
            Check(old != null && !old.Project && old.PluginKind == "" && old.LinuxDir == "/mnt/quest/plugins/old", "an older folder in /mnt/quest/plugins still loads (not a project)");

            // hot reload: main.lua changed -> loaded again; a change under .git -> not
            int Loads(ScriptHost.Entry e) => e.Log.Count(l => l.StartsWith("loaded"));
            int before = Loads(good);
            string gm = Path.Combine(projects, "apps", "good", "main.lua");
            File.WriteAllText(gm, Lua + "log('second')\n");
            File.SetLastWriteTimeUtc(gm, DateTime.UtcNow.AddSeconds(5));
            host.Scan();
            Check(Loads(good) == before + 1 && good.Log.Contains("second"), "an edit of main.lua in the project reloads it");
            before = Loads(good);
            string obj = Path.Combine(projects, "apps", "good", ".git", "objects", "ab");
            Directory.CreateDirectory(obj);
            File.WriteAllText(Path.Combine(obj, "cdef"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(obj, "cdef"), DateTime.UtcNow.AddSeconds(60));
            Directory.SetLastWriteTimeUtc(Path.Combine(projects, "apps", "good"), File.GetLastWriteTimeUtc(gm));
            host.Scan();
            Check(Loads(good) == before, "a change under .git does not reload it");

            // enable / disable survive a restart (the same key as an older folder: plugin/<id>)
            host.SetEnabled("plugin/good", false);
            var host2 = new ScriptHost(new FakeServices(1.8e9), scripts, plugins, projects);
            host2.Scan();
            Check(host2.Find("plugin/good")?.Enabled == false, "disabled stays disabled after a restart");
            host2.SetEnabled("plugin/good", true);

            // create: files + marker (read-only) + loaded at once; refused when the id is taken
            var files = PluginProject.Scaffold("app", "made", "Made here");
            var made = host2.CreateProjectPlugin("app", "made", files, out var err);
            string mdir = Path.Combine(projects, "apps", "made");
            string marker = Path.Combine(mdir, ".metacodex", "project.json");
            Check(made != null && err == null && made.Error == null && made.Project && made.PluginKind == "app" && File.Exists(marker)
                  && File.ReadAllText(marker).Contains("\"kind\": \"app\"") && (File.GetAttributes(marker) & FileAttributes.ReadOnly) != 0,
                "CreateProjectPlugin: the scaffold, the marker (read-only), loaded at once" + (err != null ? ": " + err : made?.Error != null ? ": " + made.Error : ""));
            Check(host2.CreateProjectPlugin("gesture", "made", PluginProject.Scaffold("gesture", "made", "x"), out err) == null && err != null && err.Contains("there already"), "the same id again (another kind): refused: " + err);
            Check(host2.CreateProjectPlugin("app", "old", files, out err) == null && err != null, "the id of an older folder: refused: " + err);
            var win = host2.CreateProjectPlugin("window", "x", PluginProject.Scaffold("app", "x", "X"), out err);
            Check(win != null && win.PluginKind == "app" && Directory.Exists(Path.Combine(projects, "apps", "x")), "another name of a kind is understood (window = app)" + (err != null ? ": " + err : ""));
            if (win != null) host2.Delete("plugin/x");
            Check(host2.CreateProjectPlugin("bogus", "y", files, out err) == null && err.Contains("app, gesture or behaviour"), "an unknown kind: refused: " + err);
            Check(host2.CreateProjectPlugin("app", "Bad Id", files, out err) == null && err.Contains("lower case"), "an id that is not lower case letters, digits, - or _: refused");
            Check(PluginProject.IdFor("Water Tracker 2.0!") == "water-tracker-2-0" && PluginProject.IdFor("  ") == "" && PluginProject.TileLabel("Water tracker") == "WATER"
                  && PluginProject.TileLabel("Pomodoro timer") == "POMODORO" && PluginProject.TileLabel("go") == "GO",
                "ids from names (water-tracker-2-0) and tile labels (WATER, POMODORO, GO)");
            Check(PluginProject.KindFromLinuxPath("/root/metacodex-projects/plugins/gestures/fist") == "gesture" && PluginProject.KindFromLinuxPath("/root/metacodex-projects/plugins/apps") == null
                  && PluginProject.KindFromLinuxPath("/root/metacodex-projects/native/x") == null, "the kind from a project's Linux path");

            // delete: the whole folder goes (the read-only marker too), and the entry
            Check(host2.Delete("plugin/made") && !Directory.Exists(mdir) && host2.Find("plugin/made") == null, "Delete removes the project folder with its read-only marker");

            // the scaffolds of the three kinds load without an error or a warning
            foreach (var k in PluginProject.Kinds)
            {
                var e = host2.CreateProjectPlugin(k, "new-" + k, PluginProject.Scaffold(k, "new-" + k, "New " + k), out err);
                Check(e != null && e.Error == null && !e.Log.Any(l => l.StartsWith("warn") || l.StartsWith("ERROR")) && e.PluginKind == k,
                    "scaffold " + k + ": loads clean (" + (e == null ? err : string.Join("; ", e.Log)) + ")");
            }
            ScriptHost.DeleteTree(root);
            Console.WriteLine(_fails == 0 ? "PASS [rules]" : "FAILED (" + _fails + ") [rules]");
        }
    }
}
