using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        /// Seconds since 1970 UTC (a test harness fakes it).
        double WallClock();
        /// Makes or replaces a plugin window (full window id, e.g. "plugin-timers-main") from a validated view spec (JSON
        /// text). open: show it now. A window that is not open and not built yet only keeps the spec (built on first open).
        void PluginWindow(string windowId, string specJson, bool open);
        /// Sets LIVE texts (component id -> text) of a built window without a rebuild. False when the window is not built
        /// (the host then sends the whole spec with PluginWindow(..., open: false)).
        bool PluginWindowText(string windowId, IDictionary<string, string> texts);
        /// mode 1 = open, 0 = close, 2 = toggle. Returns the open state after the call.
        bool PluginWindowShow(string windowId, int mode);
        bool PluginWindowIsOpen(string windowId);
        void PluginWindowRemove(string windowId);
        /// The whole list of plugin tiles now (in order). The app rebuilds the DOCK and the wrist menu once, soon after.
        void SetPluginTiles(IReadOnlyList<PluginTile> tiles);
        void PlaySound(string name);
        /// A callout by the Whisp. onTap may be null.
        void Notify(string text, string tone, string sound, string speak, Action onTap);
        /// The app's own path of /root/metacodex-projects/plugins (plugin projects, PluginProject) for the host whose
        /// scripts are in scriptsDir; null: no plugin projects.
        string PluginProjectsDir(string scriptsDir);
        /// Starts one HTTP request of a plugin (net.get / net.post) and returns at once; never blocks the frame. The host
        /// has checked the URL, the plugin's declared hosts and the limits. The fetcher calls request.Done once, from any
        /// thread (the host runs the plugin's callback in a later Tick). It reads at most request.MaxBytes + 1 bytes of
        /// the body (then TooLarge), gives up after request.TimeoutSeconds, stops on request.Cancel, stores and sends no
        /// cookies and no credentials of its own, and follows a redirect only when request.CheckRedirect(next) is null.
        void NetFetch(NetRequest request);
    }

    /// One plugin HTTP request as the host hands it to the fetcher (IScriptServices.NetFetch). No Unity types.
    public sealed class NetRequest
    {
        /// The plugin's id (logs, the default User-Agent).
        public string Plugin = "", Method = "GET", Url = "";
        /// The plugin's own headers (checked by the host; Content-Type is the body's type).
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        /// The body (POST) or null; ContentType is its type.
        public byte[] Body;
        public string ContentType;
        public int TimeoutSeconds = ScriptHost.NetDefaultTimeout, MaxBytes = ScriptHost.NetMaxBytes;
        public System.Threading.CancellationToken Cancel;
        /// null: the fetcher may follow a redirect to this URL; else why not (thread-safe: a pure check).
        public Func<Uri, string> CheckRedirect;
        /// Called once, from any thread, with the outcome.
        public Action<NetReply> Done;
    }

    /// What the fetcher got: a status and a body, or an error (no reply).
    public sealed class NetReply
    {
        /// The HTTP status; 0 when no reply came.
        public int Status;
        /// The body bytes (Length of them are valid; at most MaxBytes when TooLarge).
        public byte[] Body;
        public int Length;
        public string ContentType;
        /// null: a reply came (any status); else what went wrong ("timeout", "cancelled", a network error).
        public string Error;
        /// The body was longer than MaxBytes: the transfer was stopped there.
        public bool TooLarge;
    }

    /// A tile a plugin put in the menu (DOCK and wrist). Window = the full window id it toggles, or null (a `run` tile).
    public sealed class PluginTile
    {
        public string Owner, Id, Label, Glyph, Icon, Window;
    }

    /// Behaviours in Lua (MoonSharp: pure C#, IL2CPP-safe), changed during a session without a rebuild.
    /// Two forms, both hot-reloaded within a second of a change:
    ///  - scripts: single `*.lua` files in one folder (/mnt/quest/scripts in the userland);
    ///  - plugins: folders with plugin.json, an optional main.lua and optional poses/*.json (named hand poses), either
    ///    plugin projects (/root/metacodex-projects/plugins/{apps,gestures,behaviours}/<id>/ with the marker
    ///    .metacodex/project.json: their kind is fixed, PluginProject) or older folders in /mnt/quest/plugins/<id>/.
    ///    The id is the folder's name, unique across both: on a clash the project wins. A plugin is enabled, disabled
    ///    and reloaded as one unit.
    /// Each runs in its own sandbox (no io/os/load); every call has an instruction budget (a runaway loop
    /// stops that script, the app keeps its frame rate); enabled flags and `store` survive restarts.
    /// The network: a plugin's net.get / net.post reach only the https hosts its plugin.json "network" lists (plain http
    /// only to 127.0.0.1 / localhost), 4 in flight and 60 a minute at most, replies cut at 1 MB; the fetch runs off the
    /// main thread (IScriptServices.NetFetch) and the callback in a later Tick, never after the plugin unloaded.
    /// Gesture detectors (gesture.define) run as persistent coroutines a few dozen times per second.
    public sealed class ScriptHost
    {
        public sealed class Entry
        {
            public string Name, Path, Description = "";
            /// "script" (one .lua file) or "plugin" (a folder with plugin.json).
            public string Kind = "script";
            public string PluginDir, Title = "", Version = "";
            /// A plugin project's kind (app, gesture, behaviour: its folder and marker, fixed); "" for an older plugin folder
            /// in /mnt/quest/plugins (the shell then reads plugin.json's "kind" or guesses).
            public string PluginKind = "";
            /// True for a plugin project (/root/metacodex-projects/plugins/...).
            public bool Project;
            /// The plugin's folder as the Linux sees it (what `plugin.dir` and the panels show).
            public string LinuxDir = "";
            public JObject Manifest;
            public Script Script;
            public string Error;
            public bool Enabled = true;
            public DateTime Mtime;
            public int Handlers;
            public int Runs;
            public int Poses, Detectors;
            public readonly List<string> Log = new List<string>();
            /// The hosts plugin.json "network" declares (lower case; "*.example.com" = its subdomains); empty: no network.
            public string[] NetworkHosts = new string[0];
            internal Table Store;
            internal Table[] HandTables;
            /// net: the start times (session seconds) of the requests of the last minute; what the log said when (throttle).
            internal readonly Queue<float> NetStarts = new Queue<float>();
            internal readonly Dictionary<string, float> NetSaid = new Dictionary<string, float>();
            internal bool NetReplied;
            /// State key: plugins and scripts may share a name.
            public string Key => Kind == "plugin" ? "plugin/" + Name : Name;
        }

        private sealed class Handler { public Entry Owner; public string Event; public DynValue Fn; }
        private sealed class Sequence { public Entry Owner; public string[] Steps; public float Window; public DynValue Fn; public int Next; public float Start; }
        private sealed class Timer { public Entry Owner; public int Id; public float At, Every; public DynValue Fn; }
        private sealed class WallTimer { public Entry Owner; public int Id; public double At; public DynValue Fn; }
        /// One window of a plugin (ui.panel): the spec the plugin made, what the app has of it, and the flush state.
        private sealed class PlugWin
        {
            public Entry Owner; public string LocalId, FullId; public JObject Spec; public DynValue On;
            public string Sig, LastSig, Warned;                 // Sig: of the pending spec; LastSig: of the one the app has
            public Dictionary<string, string> Live, LastLive;   // live texts: pending, and what the app has
            public float LastBuild; public int Builds;
            public bool Dirty, EverSent, OpenRequested;
        }
        private sealed class TileEntry { public Entry Owner; public PluginTile Tile; public DynValue Run; }
        private sealed class Detector
        {
            public Entry Owner; public string Name, Hand; public float Hold, Cooldown; public DynValue Loop;
            public readonly MoonSharp.Interpreter.Coroutine[] Co = new MoonSharp.Interpreter.Coroutine[3];
            public double Micros; // average cost per call
        }

        /// A request of net.get / net.post: in flight (Started) or refused (its error waits for the next Tick).
        private sealed class NetPending
        {
            public Entry Owner; public Script Script; public DynValue Fn;
            public int Id, Timeout; public string Method, Url;
            /// Session time the watchdog gives up at; set in the first Tick after the start (a request made while the host
            /// loads, before its first Tick, has no session time yet).
            public float Deadline = float.NaN;
            public bool Started, Dropped;
            public int Finished; // 1 once an outcome is taken (the reply, the watchdog, a refusal); Interlocked
            public System.Threading.CancellationTokenSource Cts;
        }
        /// An outcome for the main thread: text decoded and JSON parsed already (off the main thread for a real reply).
        private sealed class NetDone { public NetPending P; public int Status; public string Body = "", Error; public JToken Json; }

        public const int InstructionBudget = 200_000;
        public const int MaxDetectors = 32;
        /// net limits: requests in flight and started in a minute per plugin, reply (and request) body bytes, timeout
        /// seconds (default and most), declared hosts.
        public const int NetMaxInFlight = 4, NetMaxPerMinute = 60, NetMaxBytes = 1 << 20, NetDefaultTimeout = 15, NetMaxTimeout = 30, NetMaxHosts = 16;
        /// Outcomes run a tick at most (a burst of replies is spread over frames).
        private const int NetDeliverPerTick = 8;
        public readonly string Dir;
        public readonly string PluginsDir;
        /// The app's path of /root/metacodex-projects/plugins (plugin projects); null: none.
        public readonly string ProjectsDir;
        /// Why folders were not loaded at the last scan (no marker, a kind that does not fit its folder, an id clash).
        public IReadOnlyList<string> ScanNotes => _notes;
        public IReadOnlyList<Entry> Entries => _entries;
        public event Action Changed;
        /// Runs at the end of every Tick (the app's per-frame work for plugin tiles).
        public event Action AfterTick;
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
        private readonly List<WallTimer> _wallTimers = new List<WallTimer>();
        private readonly List<PlugWin> _windows = new List<PlugWin>();
        private readonly List<TileEntry> _tileEntries = new List<TileEntry>();
        private readonly List<NetPending> _net = new List<NetPending>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<NetDone> _netDone = new System.Collections.Concurrent.ConcurrentQueue<NetDone>();
        private int _netIds;
        private bool _anyDirty;
        public const int MaxWindowsPerPlugin = 4, MaxPluginTiles = 6;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static readonly string[] SoundNames = { "click", "open", "close", "ping", "toast", "alert", "error" };
        private readonly List<Detector> _detectors = new List<Detector>();
        private readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>();
        private readonly Dictionary<string, Dictionary<string, string>> _stores = new Dictionary<string, Dictionary<string, string>>();
        private float _nextScan, _nextDetect;
        private int _timerIds;
        private GestureEngine _hookedEngine;

        /// projectsDir: where plugin projects are; null = the services' (PluginProjectsDir) for a host with the default
        /// plugins folder, none for a host given its own plugins folder (tests).
        public ScriptHost(IScriptServices services, string dir, string pluginsDir = null, string projectsDir = null)
        {
            _svc = services;
            Dir = dir;
            PluginsDir = pluginsDir ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir.TrimEnd('/', '\\')) ?? dir, "plugins");
            ProjectsDir = projectsDir ?? (pluginsDir == null ? services?.PluginProjectsDir(dir) : null);
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
            if (now >= _nextScan && !_scanOut)
            {
                _nextScan = now + 1f;
                if (!Application.isPlaying) Scan(); // edit-mode self-tests: at once
                else
                {
                    // the folder walk (every plugin file's time: 2-3 ms each second on the headset in 0.7.19) on a pool
                    // thread; this thread only loads what changed
                    _scanOut = true;
                    string dir = Dir, plugins = PluginsDir, projects = ProjectsDir;
                    int gen = _scanGen;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        var r = new ScanResult { Gen = gen };
                        try { r.Found = Gather(dir, plugins, projects); } catch (Exception) { r.Found = null; }
                        System.Threading.Volatile.Write(ref _scanReady, r);
                    });
                }
            }
            var ready = System.Threading.Interlocked.Exchange(ref _scanReady, null);
            if (ready != null) { _scanOut = false; if (ready.Found != null && ready.Gen == _scanGen) Apply(ready.Found); }
            for (int i = 0; i < _timers.Count; i++)
            {
                var t = _timers[i];
                if (t.At > now || !t.Owner.Enabled || t.Owner.Error != null) continue;
                if (t.Every > 0f) t.At = now + t.Every; else { _timers.RemoveAt(i); i--; }
                Run(t.Owner, t.Fn);
            }
            if (_wallTimers.Count > 0)
            {
                double wc = _svc.WallClock();
                for (int i = 0; i < _wallTimers.Count; i++)
                {
                    var t = _wallTimers[i];
                    if (t.At > wc || !t.Owner.Enabled || t.Owner.Error != null) continue;
                    _wallTimers.RemoveAt(i); i--;
                    Run(t.Owner, t.Fn);
                }
            }
            if (_net.Count > 0 || !_netDone.IsEmpty) TickNet();
            if (_anyDirty) FlushWindows(now);
            AfterTick?.Invoke();
        }

        /// What a scan found: a script or a plugin (Kind "script" / "plugin"), or a note (Note set: why a folder is not loaded).
        private sealed class Found
        {
            public string Kind, Name, Path, PluginKind = "", Note;
            public bool Project;
            public DateTime Mtime;
        }
        private sealed class ScanResult { public int Gen; public List<Found> Found; }
        private ScanResult _scanReady; // written by the pool thread, taken by Tick
        private bool _scanOut;
        private int _scanGen;          // a scan done at once makes the one still out stale

        /// Loads new and changed scripts and plugins, drops deleted ones (at once).
        public void Scan()
        {
            _scanGen++;
            Apply(Gather(Dir, PluginsDir, ProjectsDir));
        }

        /// What is in the folders: scripts, then plugin projects (apps, gestures, behaviours), then the older plugin
        /// folders, each in name order, with the newest time of any of a plugin's files; and a note for each folder that is
        /// not loaded (no marker, a marker that does not fit its folder, an id another plugin has). Runs on a pool thread.
        private static List<Found> Gather(string dir, string pluginsDir, string projectsDir)
        {
            var found = new List<Found>();
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.lua") : new string[0];
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) found.Add(new Found { Kind = "script", Name = System.IO.Path.GetFileNameWithoutExtension(f), Path = f, Mtime = File.GetLastWriteTimeUtc(f) });
            var ids = new Dictionary<string, string>(); // plugin id -> where it was found first
            if (!string.IsNullOrEmpty(projectsDir) && Directory.Exists(projectsDir))
            {
                for (int k = 0; k < PluginProject.Kinds.Length; k++)
                {
                    string kind = PluginProject.Kinds[k], folder = PluginProject.Folders[k];
                    string kdir = System.IO.Path.Combine(projectsDir, folder);
                    if (!Directory.Exists(kdir)) continue;
                    var pdirs = Directory.GetDirectories(kdir);
                    Array.Sort(pdirs, StringComparer.Ordinal);
                    foreach (var d in pdirs)
                    {
                        string fname = System.IO.Path.GetFileName(d), at = "plugins/" + folder + "/" + fname;
                        if (fname.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (!File.Exists(System.IO.Path.Combine(d, "plugin.json"))) { found.Add(new Found { Note = at + ": not loaded: no plugin.json" }); continue; }
                        string marker = System.IO.Path.Combine(d, ".metacodex", "project.json"), problem = null, mkind = null;
                        if (!File.Exists(marker)) problem = "no marker .metacodex/project.json (a plugin project is made by + NEW PLUGIN or mcx-project new plugins, not by a folder alone)";
                        else
                        {
                            try { mkind = PluginProject.KindFromMarker(File.ReadAllText(marker), out problem); }
                            catch (Exception ex) { problem = "its marker is unreadable: " + ex.Message; }
                            if (mkind != null && mkind != kind) problem = "its marker says kind " + mkind + " but its folder is plugins/" + folder + " (the kind is fixed: make a new project of that kind)";
                        }
                        if (problem != null) { found.Add(new Found { Note = at + ": not loaded: " + problem }); continue; }
                        string id = Sanitize(fname);
                        if (ids.TryGetValue(id, out var first)) { found.Add(new Found { Note = at + ": not loaded: the id " + id + " is taken by " + first }); continue; }
                        ids[id] = at;
                        found.Add(new Found { Kind = "plugin", Name = id, Path = d, Mtime = PluginMtime(d), PluginKind = kind, Project = true });
                    }
                }
            }
            var dirs = Directory.Exists(pluginsDir) ? Directory.GetDirectories(pluginsDir) : new string[0];
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var d in dirs)
            {
                if (!File.Exists(System.IO.Path.Combine(d, "plugin.json"))) continue;
                string id = Sanitize(System.IO.Path.GetFileName(d));
                if (ids.TryGetValue(id, out var first))
                {
                    found.Add(new Found { Note = "/mnt/quest/plugins/" + System.IO.Path.GetFileName(d) + ": not loaded: the plugin project " + first + " has the same id (the project wins)" });
                    continue;
                }
                ids[id] = "/mnt/quest/plugins/" + id;
                found.Add(new Found { Kind = "plugin", Name = id, Path = d, Mtime = PluginMtime(d) });
            }
            return found;
        }

        private readonly List<string> _notes = new List<string>();

        private void Apply(List<Found> found)
        {
            var seen = new HashSet<string>();
            bool changed = false;
            var notes = new List<string>();
            foreach (var f in found)
            {
                if (f.Note != null) { notes.Add(f.Note); continue; }
                seen.Add(f.Kind == "plugin" ? "plugin/" + f.Name : f.Name);
                var e = _entries.FirstOrDefault(x => x.Kind == f.Kind && x.Name == f.Name);
                // the same id from another folder (an older folder replaced by its project): load it from there
                if (e != null && e.Mtime == f.Mtime && e.Path == f.Path) continue;
                if (e == null) e = NewEntry(f.Kind, f.Name, f.Path);
                SetPlace(e, f.Path, f.Project, f.PluginKind);
                Load(e, f.Mtime);
                changed = true;
            }
            foreach (var e in _entries.Where(x => !seen.Contains(x.Key)).ToList())
            {
                Unload(e);
                _entries.Remove(e);
                changed = true;
            }
            // a note is said once, when it appears (the scan runs every second)
            foreach (var n in notes) if (!_notes.Contains(n)) _svc.Log("[plugins] " + n);
            if (notes.Count != _notes.Count || notes.Any(n => !_notes.Contains(n))) { _notes.Clear(); _notes.AddRange(notes); changed = true; }
            if (changed) Changed?.Invoke();
        }

        private Entry NewEntry(string kind, string name, string path)
        {
            var e = new Entry { Kind = kind, Name = name, Path = path, PluginDir = kind == "plugin" ? path : null };
            if (kind == "plugin") e.LinuxDir = "/mnt/quest/plugins/" + name;
            _entries.Add(e);
            return e;
        }

        private static void SetPlace(Entry e, string path, bool project, string pluginKind)
        {
            e.Path = path;
            if (e.Kind != "plugin") return;
            e.PluginDir = path;
            e.Project = project;
            e.PluginKind = project ? pluginKind ?? "" : "";
            e.LinuxDir = project ? PluginProject.LinuxDirOf(pluginKind, System.IO.Path.GetFileName(path)) : "/mnt/quest/plugins/" + e.Name;
        }

        /// A plugin changes when any of its files changes (a project's .git folder is not looked at: it only grows).
        private static DateTime PluginMtime(string dir)
        {
            var t = Directory.GetLastWriteTimeUtc(dir);
            try { NewestIn(dir, ref t); }
            catch (Exception) { }
            return t;
        }

        private static void NewestIn(string dir, ref DateTime t)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                var m = File.GetLastWriteTimeUtc(f);
                if (m > t) t = m;
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                string n = System.IO.Path.GetFileName(d);
                if (n == ".git" || n == "node_modules") continue;
                NewestIn(d, ref t);
            }
        }

        private void Load(Entry e, DateTime mtime)
        {
            Unload(e);
            e.Mtime = mtime;
            e.Error = null;
            e.Handlers = 0;
            e.Poses = 0;
            e.Detectors = 0;
            e.NetworkHosts = new string[0];
            e.NetSaid.Clear();
            e.NetReplied = false;
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
                // a project's kind is its folder and marker's, never what plugin.json says later
                string said = e.Manifest["kind"]?.Type == JTokenType.String ? (string)e.Manifest["kind"] : null;
                if (e.Project && said != null && PluginProject.NormalizeKind(said) != e.PluginKind)
                    Say(e, "warn plugin.json: \"kind\": \"" + said + "\" is not this project's kind; the kind is " + e.PluginKind + " (fixed by .metacodex/project.json and the folder plugins/" + PluginProject.FolderOf(e.PluginKind) + "): set \"kind\": \"" + e.PluginKind + "\"");
                var netWarnings = new List<string>();
                e.NetworkHosts = ParseNetworkHosts(e.Manifest["network"], netWarnings);
                foreach (var w in netWarnings) Say(e, "warn plugin.json \"network\": " + w);
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
                // a tile that opens a window main.lua never made
                if (e.Error == null)
                    foreach (var te in _tileEntries)
                        if (te.Owner == e && te.Tile.Window != null && FindWindow(te.Tile.Window) == null)
                            Say(e, "warn menu.add " + te.Tile.Id + ": panel " + te.Tile.Window + " is not a window of this plugin; make it with ui.panel{id=...} in main.lua");
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
                catch (Exception ex) { Say(e, $"warn pose {System.IO.Path.GetFileName(f)} skipped: {ex.Message}"); }
            }
        }

        private void Unload(Entry e)
        {
            _handlers.RemoveAll(h => h.Owner == e);
            _sequences.RemoveAll(s => s.Owner == e);
            _timers.RemoveAll(t => t.Owner == e);
            DropNet(e);
            RemoveOwnedUi(e);
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

        /// Drops the saved enabled flag and store of a script or plugin that no longer exists (the gesture examples'
        /// cleanup), so a later install of the same id starts clean.
        public void ForgetState(string key)
        {
            bool changed = _enabled.Remove(key) | _stores.Remove(key);
            if (changed) SaveState();
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
        /// A plugin project of that id gets the files in its own folder; otherwise the folder is /mnt/quest/plugins/<id>.
        public Entry SavePlugin(string id, IDictionary<string, string> files)
        {
            id = Sanitize(id);
            var have = _entries.FirstOrDefault(x => x.Kind == "plugin" && x.Name == id);
            var dir = have != null && have.Project ? have.PluginDir : System.IO.Path.Combine(PluginsDir, id);
            WriteFiles(dir, files);
            var e = have ?? NewEntry("plugin", id, dir);
            Load(e, PluginMtime(dir));
            Changed?.Invoke();
            return e;
        }

        private static void WriteFiles(string dir, IEnumerable<KeyValuePair<string, string>> files)
        {
            Directory.CreateDirectory(dir);
            string root = System.IO.Path.GetFullPath(dir).TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar;
            foreach (var kv in files)
            {
                var path = System.IO.Path.Combine(dir, kv.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)) continue; // stays inside
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
            }
        }

        /// The app's folder of a plugin project of this kind and id (null without ProjectsDir or for an unknown kind).
        public string ProjectDirFor(string kind, string id)
        {
            string f = PluginProject.FolderOf(kind);
            return f == null || string.IsNullOrEmpty(ProjectsDir) || string.IsNullOrEmpty(id) ? null : System.IO.Path.Combine(ProjectsDir, f, id);
        }

        /// Makes a plugin project's folder (ProjectsDir/<kind folder>/<id>): the files, then the marker
        /// .metacodex/project.json, read-only; loads it at once. Refuses an id that is taken (by a project or an older
        /// folder) and a folder that is there already. The shell then starts git there and adds the MQH project.
        public Entry CreateProjectPlugin(string kind, string id, IDictionary<string, string> files, out string error)
        {
            error = null;
            kind = PluginProject.NormalizeKind(kind);
            if (kind == null) { error = "the kind is app, gesture or behaviour"; return null; }
            if (string.IsNullOrEmpty(id) || id != PluginProject.IdFor(id)) { error = "the id '" + id + "' must be lower case letters, digits, - or _"; return null; }
            string dir = ProjectDirFor(kind, id);
            if (dir == null) { error = "no plugin projects here (the Linux is not set up)"; return null; }
            var have = Find("plugin/" + id);
            if (have != null) { error = "a plugin with the id " + id + " is there already (" + have.LinuxDir + ")"; return null; }
            foreach (var f in PluginProject.Folders)
                if (Directory.Exists(System.IO.Path.Combine(ProjectsDir, f, id))) { error = "the folder plugins/" + f + "/" + id + " is there already"; return null; }
            if (File.Exists(System.IO.Path.Combine(PluginsDir, id, "plugin.json"))) { error = "/mnt/quest/plugins/" + id + " has that id already"; return null; }
            try
            {
                WriteFiles(dir, files);
                string marker = System.IO.Path.Combine(dir, ".metacodex", "project.json");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker));
                File.WriteAllText(marker, PluginProject.MarkerJson(kind));
                try { File.SetAttributes(marker, FileAttributes.ReadOnly); } catch (Exception) { } // chmod a-w on Linux and Android
            }
            catch (Exception ex) { error = "the folder was not made: " + ex.Message; return null; }
            var e = NewEntry("plugin", id, dir);
            SetPlace(e, dir, true, kind);
            Load(e, PluginMtime(dir));
            _scanGen++; // a scan still out does not know this folder yet
            Changed?.Invoke();
            return e;
        }

        /// Deletes a folder with everything in it, read-only files too (the marker; git's objects on Windows).
        public static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
            Directory.Delete(dir, true);
        }

        public bool Delete(string nameOrKey)
        {
            var e = Find(nameOrKey) ?? Find(Sanitize(nameOrKey));
            if (e == null) return false;
            Unload(e);
            _entries.Remove(e);
            try
            {
                if (e.Kind == "plugin") DeleteTree(e.PluginDir);
                else File.Delete(e.Path);
            }
            catch (Exception ex) { _svc.Log("[plugins] " + e.Name + ": not all of " + e.PluginDir + " was deleted: " + ex.Message); }
            _scanGen++;
            // deleted means gone: its saved on/off flag and its store go too, so installing the same id again starts clean
            // (on the headset a deleted Timers came back disabled with its old store)
            ForgetState(e.Key);
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
            Set(script.Globals, "check", a => DynValue.NewString(Check(Str(a, 0))));
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

        // ------------------------------------------------------------------ plugin windows, tiles, wall-clock time

        /// The id part of a window id: lower case, [a-z0-9_-] only, at most 32 characters.
        public static string SafeId(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-') sb.Append(c);
                if (sb.Length >= 32) break;
            }
            return sb.ToString();
        }

        public static bool IsSoundName(string n) => n != null && Array.IndexOf(SoundNames, n) >= 0;

        private PlugWin OwnWindow(Entry e, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string safe = SafeId(id);
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (w.Owner == e && (w.FullId == id || w.LocalId == id || w.LocalId == safe)) return w;
            }
            return null;
        }

        /// Whether this plugin has made a window or a menu tile now (the APPS category of plugins).
        public bool OwnsUi(Entry e)
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].Owner == e) return true;
            for (int i = 0; i < _tileEntries.Count; i++) if (_tileEntries[i].Owner == e) return true;
            return false;
        }

        private PlugWin FindWindow(string fullId)
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].FullId == fullId) return _windows[i];
            return null;
        }

        /// The full window ids of a plugin's windows (ui.panel): `main` first, then the others in the order they were made.
        public IReadOnlyList<string> WindowsOf(Entry e)
        {
            var ids = new List<string>();
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (w.Owner != e) continue;
                if (w.LocalId == "main") ids.Insert(0, w.FullId); else ids.Add(w.FullId);
            }
            return ids;
        }

        /// Opens one of a plugin's windows as ui.open does (its spec is sent first when the app has not got it yet).
        public bool OpenWindowOf(Entry e, string fullId)
        {
            var w = FindWindow(fullId);
            return w != null && w.Owner == e && ShowOwn(w, 1);
        }

        private void RemoveOwnedUi(Entry e)
        {
            _wallTimers.RemoveAll(t => t.Owner == e);
            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                if (_windows[i].Owner != e) continue;
                var w = _windows[i];
                _windows.RemoveAt(i);
                _svc.PluginWindowRemove(w.FullId);
            }
            if (_tileEntries.RemoveAll(t => t.Owner == e) > 0) PushTiles();
        }

        private void PushTiles()
        {
            var list = new List<PluginTile>(_tileEntries.Count);
            for (int i = 0; i < _tileEntries.Count; i++) list.Add(_tileEntries[i].Tile);
            _svc.SetPluginTiles(list);
        }

        /// The spec as JSON text with the `text` of LIVE text components (type text/markdown/note with an id) blanked,
        /// and those texts by component id.
        private static string Signature(JObject spec, out Dictionary<string, string> live)
        {
            live = new Dictionary<string, string>();
            var c = (JObject)spec.DeepClone();
            if (c["components"] is JArray comps)
                foreach (var item in comps)
                {
                    if (!(item is JObject co)) continue;
                    string type = co["type"]?.Type == JTokenType.String ? (string)co["type"] : null;
                    string id = co["id"]?.Type == JTokenType.String ? (string)co["id"] : null;
                    if (string.IsNullOrEmpty(id) || !ViewSpecCheck.IsTextType(type)) continue;
                    string text = co["text"] != null && co["text"].Type != JTokenType.Null ? (string)co["text"]
                        : co["markdown"] != null && co["markdown"].Type != JTokenType.Null ? (string)co["markdown"] : "";
                    live[id] = text ?? "";
                    if (co["text"] != null) co["text"] = "";
                    if (co["markdown"] != null) co["markdown"] = "";
                }
            return c.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void FlushWindows(float now)
        {
            bool pending = false;
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (!w.Dirty) continue;
                if (!FlushWindow(w, now, false)) pending = true;
            }
            _anyDirty = pending;
        }

        /// Sends a dirty window to the app: only the changed LIVE texts when nothing else changed, else the whole spec
        /// (at most once a second a window, the first one at once). False = still dirty (waiting for the rate limit).
        private bool FlushWindow(PlugWin w, float now, bool force)
        {
            if (w.Sig == null) w.Sig = Signature(w.Spec, out w.Live);
            if (w.EverSent && w.Sig == w.LastSig)
            {
                Dictionary<string, string> changed = null;
                foreach (var kv in w.Live)
                    if (w.LastLive == null || !w.LastLive.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        (changed ??= new Dictionary<string, string>()).Add(kv.Key, kv.Value);
                w.Dirty = false;
                w.LastLive = w.Live;
                bool open = w.OpenRequested;
                w.OpenRequested = false;
                if (changed != null)
                {
                    if (!_svc.PluginWindowText(w.FullId, changed))
                    {
                        w.Builds++;
                        w.LastBuild = now;
                        _svc.PluginWindow(w.FullId, w.Spec.ToString(Newtonsoft.Json.Formatting.None), false);
                    }
                }
                if (open) _svc.PluginWindowShow(w.FullId, 1);
                return true;
            }
            if (w.EverSent && !force && now - w.LastBuild < 1f) return false;
            w.Dirty = false;
            w.EverSent = true;
            w.LastBuild = now;
            w.LastSig = w.Sig;
            w.LastLive = w.Live;
            w.Builds++;
            bool show = w.OpenRequested;
            w.OpenRequested = false;
            _svc.PluginWindow(w.FullId, w.Spec.ToString(Newtonsoft.Json.Formatting.None), show);
            return true;
        }

        /// A window the app has never got (ui.panel and ui.open in the same frame): sends it now.
        private void EnsureSent(PlugWin w)
        {
            if (!w.EverSent && w.Dirty) FlushWindow(w, Now, true);
        }

        private bool ShowOwn(PlugWin w, int mode)
        {
            EnsureSent(w);
            return _svc.PluginWindowShow(w.FullId, mode);
        }

        /// A tile was tapped (DOCK or wrist menu): a panel tile toggles its window, a run tile runs its function.
        public void TileTapped(string owner, string tileId)
        {
            for (int i = 0; i < _tileEntries.Count; i++)
            {
                var te = _tileEntries[i];
                if (te.Tile.Owner != owner || te.Tile.Id != tileId) continue;
                if (te.Tile.Window != null)
                {
                    var w = FindWindow(te.Tile.Window);
                    if (w != null) ShowOwn(w, 2);
                    else { Say(te.Owner, "warn menu.add " + te.Tile.Id + ": its window " + te.Tile.Window + " was never made (ui.panel{id=...})"); _svc.Toast(te.Tile.Label + ": this plugin has no such window", "warn"); }
                }
                else Run(te.Owner, te.Run);
                return;
            }
        }

        /// An event of a plugin window (a button, choice, input, toggle, cell, or the window opened or closed by the
        /// user): runs the `on` function of ui.panel with e = {type, id, value, window}. value is a boolean for toggle.
        /// The plugin that owns this window (full id, "plugin-<id>-<local>"), or null when no plugin has it.
        public Entry WindowOwner(string windowId) => FindWindow(windowId)?.Owner;

        public void WindowEvent(string windowId, string type, string id, string value)
        {
            var w = FindWindow(windowId);
            if (w == null || w.On == null || w.Owner.Script == null) return;
            var t = new Table(w.Owner.Script);
            t.Set("type", DynValue.NewString(type ?? ""));
            t.Set("id", DynValue.NewString(id ?? ""));
            t.Set("value", type == "toggle" ? DynValue.NewBoolean(value == "true") : DynValue.NewString(value ?? ""));
            t.Set("window", DynValue.NewString(w.LocalId));
            Run(w.Owner, w.On, DynValue.NewTable(t));
        }

        /// What `mcx-ui plugin check <id>` prints: state, error, windows, tiles, voice commands, timers, warnings.
        public string Check(string pluginId)
        {
            var e = Find("plugin/" + pluginId) ?? Find(pluginId);
            if (e == null)
            {
                // a folder that is there but not loaded says why (no marker, a kind that does not fit, an id clash)
                var why = _notes.Where(n => n.Contains("/" + pluginId + ":")).ToList();
                return "plugin " + pluginId + ": " + (why.Count > 0 ? string.Join("\n", why) : "not found (mcx-ui plugin list)");
            }
            var sb = new StringBuilder();
            sb.Append("plugin ").Append(e.Name).Append(": ").Append(e.Error != null ? "error" : !e.Enabled ? "disabled" : "loaded");
            if (e.Kind == "plugin")
                sb.Append("\nkind: ").Append(e.Project ? e.PluginKind + " (fixed: a plugin project)" : ((string)e.Manifest?["kind"] ?? "not set") + " (an older folder, not a project)")
                  .Append("\nfolder: ").Append(e.LinuxDir);
            if (e.Error != null) sb.Append("\nerror: ").Append(e.Error);
            var wins = new List<string>();
            foreach (var w in _windows)
                if (w.Owner == e) wins.Add(w.LocalId + "(builds=" + w.Builds + (_svc.PluginWindowIsOpen(w.FullId) ? ", open" : "") + ")");
            sb.Append("\nwindows: ").Append(wins.Count == 0 ? "none" : string.Join(", ", wins));
            var tiles = new List<string>();
            foreach (var te in _tileEntries) if (te.Owner == e) tiles.Add(te.Tile.Label);
            sb.Append("\ntiles: ").Append(tiles.Count == 0 ? "none" : string.Join(", ", tiles));
            var voice = new List<string>();
            if (_svc.Voice != null) foreach (var c in _svc.Voice.All) if (c.Owner == e.Key) voice.Add(c.Name);
            sb.Append("\nvoice: ").Append(voice.Count == 0 ? "none" : string.Join(", ", voice));
            int timers = 0;
            foreach (var t in _timers) if (t.Owner == e) timers++;
            foreach (var t in _wallTimers) if (t.Owner == e) timers++;
            sb.Append("\ntimers: ").Append(timers);
            if (e.NetworkHosts.Length > 0)
                sb.Append("\nnetwork: ").Append(string.Join(", ", e.NetworkHosts)).Append(" (").Append(NetInFlight(e)).Append(" in flight, ")
                  .Append(e.NetStarts.Count(t => t > Now - 60f)).Append(" in the last minute)");
            else sb.Append("\nnetwork: none");
            var bad = new List<string>();
            foreach (var line in e.Log) if (line.StartsWith("warn", StringComparison.Ordinal) || line.StartsWith("ERROR", StringComparison.Ordinal) || line.StartsWith("voice.command ", StringComparison.Ordinal)) bad.Add(line); // voice.command lines are the registry's lint warnings
            foreach (var n in _notes) if (n.Contains("/" + e.Name + ":")) bad.Add("warn " + n); // an older folder this project hides
            sb.Append("\nwarnings: ").Append(bad.Count);
            for (int i = Math.Max(0, bad.Count - 10); i < bad.Count; i++) sb.Append('\n').Append(bad[i]);
            return sb.ToString();
        }

        /// The gesture names a plugin or script reacts to (gesture.on, gesture.sequence steps; "*" left out).
        public List<string> HandledGestures(Entry e)
        {
            var names = new List<string>();
            foreach (var h in _handlers) if (h.Owner == e && h.Event != "*" && !names.Contains(h.Event)) names.Add(h.Event);
            foreach (var s in _sequences) if (s.Owner == e) foreach (var st in s.Steps) if (!names.Contains(st)) names.Add(st);
            return names;
        }

        /// The gesture names a plugin or script defines with gesture.define (Lua detectors).
        public List<string> DetectorNames(Entry e) => _detectors.Where(d => d.Owner == e).Select(d => d.Name).Distinct().ToList();

        /// The gesture sequences of a plugin or script (their steps, in order, and the time window).
        public List<(string[] steps, float window)> SequencesOf(Entry e) => _sequences.Where(s => s.Owner == e).Select(s => (s.Steps, s.Window)).ToList();

        private static double LocalToEpoch(int year, int month, int day, int hour, int min, double sec)
        {
            var dt = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(month - 1).AddDays(day - 1).AddHours(hour).AddMinutes(min).AddSeconds(sec);
            return (dt.ToUniversalTime() - Epoch).TotalSeconds;
        }

        // ------------------------------------------------------------------ network (net.get / net.post)

        /// The hosts of plugin.json "network": host names ("api.example.com"), "*.example.com" for the subdomains of a
        /// domain, "127.0.0.1" / "localhost" for this headset's own servers. An entry that is not a host name (a scheme,
        /// a path, a port, "*") is left out with a warning.
        public static string[] ParseNetworkHosts(JToken tok, List<string> warnings)
        {
            var hosts = new List<string>();
            if (tok == null || tok.Type == JTokenType.Null) return hosts.ToArray();
            IEnumerable<JToken> items;
            if (tok.Type == JTokenType.String) { warnings.Add("write a list: \"network\": [\"" + (string)tok + "\"]"); items = new[] { tok }; }
            else if (tok is JArray arr) items = arr;
            else { warnings.Add("must be a list of host names, for example \"network\": [\"api.example.com\"]"); return hosts.ToArray(); }
            foreach (var it in items)
            {
                if (it.Type != JTokenType.String) { warnings.Add(it.ToString(Newtonsoft.Json.Formatting.None) + " is not a host name (a text like \"api.example.com\")"); continue; }
                string raw = (string)it, h = raw.Trim().TrimEnd('.').ToLowerInvariant();
                string why = HostEntryProblem(h);
                if (why != null) { warnings.Add("'" + raw + "': " + why); continue; }
                if (hosts.Contains(h)) continue;
                if (hosts.Count >= NetMaxHosts) { warnings.Add("more than " + NetMaxHosts + " hosts; the rest are left out"); break; }
                hosts.Add(h);
            }
            return hosts.ToArray();
        }

        private static readonly System.Text.RegularExpressions.Regex HostRx =
            new System.Text.RegularExpressions.Regex(@"^(\*\.)?([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]*[a-z0-9])?$");

        private static string HostEntryProblem(string h)
        {
            if (h.Length == 0) return "empty";
            if (h.Contains("://") || h.Contains("/")) return "write the host name only, without https:// or a path (for example api.example.com)";
            if (h.Contains(":")) return "write the host name only, without a port (any port of a declared host is allowed)";
            if (h == "*" || h == "*.") return "every host is not allowed: list the hosts the plugin needs";
            if (h.Length > 253 || !HostRx.IsMatch(h)) return "not a host name (letters, digits, - and dots: api.example.com, or *.example.com for its subdomains)";
            if (h.StartsWith("*.", StringComparison.Ordinal) && h.IndexOf('.', 2) < 0) return "a wildcard needs a domain with a dot (*.example.com, not *.com)";
            if (h.IndexOf('.') < 0 && h != "localhost") return "not a full host name (for example api.example.com)";
            return null;
        }

        /// True when the host (lower case) is a declared one: the same name, or a subdomain (any depth) of a "*.domain"
        /// entry. "*.example.com" does not cover example.com itself: that needs its own entry.
        public static bool HostDeclared(string host, IReadOnlyList<string> hosts)
        {
            if (string.IsNullOrEmpty(host) || hosts == null) return false;
            for (int i = 0; i < hosts.Count; i++)
            {
                string h = hosts[i];
                if (h.StartsWith("*.", StringComparison.Ordinal))
                {
                    if (host.Length > h.Length - 1 && host.EndsWith(h.Substring(1), StringComparison.Ordinal)) return true;
                }
                else if (host == h) return true;
            }
            return false;
        }

        /// null when a plugin that declared these hosts may fetch the URL; else why not (the text its log and res.error show).
        public static string NetCheckUrl(string url, IReadOnlyList<string> hosts)
        {
            if (string.IsNullOrEmpty(url)) return "net: the url is empty";
            if (url.Length > 8192) return "net: the url is longer than 8192 characters";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "net: not a full url like https://api.example.com/path: " + Short(url);
            return NetCheckUri(u, hosts);
        }

        /// The same for a parsed URL (also each redirect the fetcher is asked to follow). Thread-safe.
        public static string NetCheckUri(Uri u, IReadOnlyList<string> hosts)
        {
            if (u == null || !u.IsAbsoluteUri) return "net: not a full url";
            string scheme = u.Scheme.ToLowerInvariant();
            if (scheme != "https" && scheme != "http") return "net: only https urls (not " + scheme + ":)";
            if (!string.IsNullOrEmpty(u.UserInfo)) return "net: no user name or password in the url (a key the user typed goes in a header)";
            string host = HostOf(u);
            bool loopback = host == "127.0.0.1" || host == "localhost";
            if (scheme == "http" && !loopback) return "net: http is refused, use https (plain http only to 127.0.0.1 and localhost)";
            if (!HostDeclared(host, hosts)) return "net: host not declared in plugin.json \"network\": " + host;
            return null;
        }

        private static string HostOf(Uri u)
        {
            string h;
            try { h = u.IdnHost; } catch (Exception) { h = u.Host; }
            return (h ?? "").TrimEnd('.').ToLowerInvariant();
        }

        /// The URL as logs show it: scheme, host and path, never the query (it may hold a key).
        private static string LogUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return Short(url ?? "");
            string s = u.Scheme + "://" + u.Authority + u.AbsolutePath + (u.Query.Length > 1 ? "?…" : "");
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        /// Headers the app's HTTP stack sets itself (framing, hop by hop, proxy): a plugin may not set them.
        private static readonly string[] AppHeaders = { "host", "content-length", "connection", "transfer-encoding", "keep-alive", "upgrade", "te", "trailer", "expect" };

        private static string HeaderProblem(string name, string value)
        {
            if (name.Length == 0 || name.Length > 64) return "a name of 1 to 64 characters";
            foreach (char c in name)
                if (!(c < 128 && char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0)) return "a header name has letters, digits and - only";
            string low = name.ToLowerInvariant();
            if (Array.IndexOf(AppHeaders, low) >= 0 || low.StartsWith("proxy-", StringComparison.Ordinal)) return "the app sets it, not a plugin";
            if (value.Length > 4096) return "a value of 4096 characters at most";
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "no line breaks in a header value";
            return null;
        }

        /// Requests of this plugin in flight now (started, no outcome run yet).
        public int NetInFlight(Entry e)
        {
            int n = 0;
            for (int i = 0; i < _net.Count; i++) if (_net[i].Owner == e && _net[i].Started) n++;
            return n;
        }

        /// A log line about the network, at most once in 10 s for the same key (a plugin that loops on a refusal).
        private void NetSay(Entry e, string line, string key)
        {
            if (e.NetSaid.TryGetValue(key, out var at) && Now - at < 10f && Now >= at) return;
            e.NetSaid[key] = Now;
            Say(e, line);
        }

        /// net.get(url, [opts], fn) / net.post(url, body, [opts], fn). Returns the request id, or nil and why when it is
        /// refused (the callback then gets the same error in the next tick: every request gets exactly one callback).
        private DynValue NetCall(Entry e, Script s, bool post, CallbackArguments a)
        {
            string form = post ? "net.post(url, body, { headers = {...}, timeout = 15 }, function(res) ... end)"
                               : "net.get(url, { headers = {...}, timeout = 15 }, function(res) ... end)";
            if (a.Count < 1 || a[0].Type != DataType.String) throw new ScriptRuntimeException(form + ": the url is a text");
            string url = a[0].String;
            int i = 1;
            DynValue body = DynValue.Nil;
            if (post) { if (a.Count > 1) body = a[1]; i = 2; }
            Table opts = null;
            if (i < a.Count && a[i].Type == DataType.Table) { opts = a[i].Table; i++; }
            else if (i + 1 < a.Count && a[i].IsNil()) i++;
            var fn = Fn(a, i);
            if (fn == null) throw new ScriptRuntimeException(form + ": the last argument is the function that gets the reply (a net call never waits)");
            var req = new NetRequest { Plugin = e.Name, Method = post ? "POST" : "GET", Url = url };
            bool ua = false;
            if (opts != null)
            {
                var hv = opts.Get("headers");
                if (!hv.IsNil())
                {
                    if (hv.Type != DataType.Table) throw new ScriptRuntimeException(form + ": headers is a table like { Accept = \"application/json\" }");
                    foreach (var pair in hv.Table.Pairs)
                    {
                        if (pair.Value.IsNil()) continue;
                        if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.String && pair.Value.Type != DataType.Number)
                            throw new ScriptRuntimeException("net: a header is name = \"text\", for example headers = { Accept = \"application/json\" }");
                        string name = pair.Key.String, value = pair.Value.Type == DataType.String ? pair.Value.String : Number(pair.Value.Number);
                        string bad = HeaderProblem(name, value);
                        if (bad != null) throw new ScriptRuntimeException("net: header " + name + ": " + bad);
                        if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) { req.ContentType = value; continue; }
                        if (name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) ua = true;
                        if (req.Headers.Count >= 32) throw new ScriptRuntimeException("net: 32 headers at most");
                        req.Headers.Add(new KeyValuePair<string, string>(name, value));
                    }
                }
                var tv = opts.Get("timeout");
                if (!tv.IsNil())
                {
                    if (tv.Type != DataType.Number) throw new ScriptRuntimeException(form + ": timeout is a number of seconds (1 to " + NetMaxTimeout + ")");
                    req.TimeoutSeconds = (int)Math.Ceiling(Math.Max(1.0, Math.Min(NetMaxTimeout, tv.Number)));
                }
            }
            if (!ua) req.Headers.Add(new KeyValuePair<string, string>("User-Agent", "MetaCodex-plugin/1.0 (" + e.Name + ")"));
            if (post)
            {
                string text = null, type = "text/plain; charset=utf-8";
                if (body.Type == DataType.Table)
                {
                    try { text = LuaJson.Encode(body); }
                    catch (ScriptRuntimeException ex) { throw new ScriptRuntimeException("net.post: the body table: " + ex.Message); }
                    type = "application/json";
                }
                else if (body.Type == DataType.String) text = body.String;
                else if (!body.IsNil()) text = body.ToPrintString();
                req.Body = text == null ? new byte[0] : Encoding.UTF8.GetBytes(text);
                if (req.ContentType == null) req.ContentType = type;
            }
            string why = e.Kind != "plugin" ? "net: only plugins can use the network (a plugin lists its hosts in plugin.json \"network\")"
                       : req.Body != null && req.Body.Length > NetMaxBytes ? "net: the body is larger than 1 MB"
                       : NetCheckUrl(url, e.NetworkHosts);
            if (why == null)
            {
                while (e.NetStarts.Count > 0 && (e.NetStarts.Peek() <= Now - 60f || e.NetStarts.Peek() > Now)) e.NetStarts.Dequeue();
                if (NetInFlight(e) >= NetMaxInFlight) why = "net: " + NetMaxInFlight + " requests in flight already (the most for a plugin); wait for a reply";
                else if (e.NetStarts.Count >= NetMaxPerMinute) why = "net: " + NetMaxPerMinute + " requests in the last minute (the most for a plugin); wait";
            }
            var p = new NetPending { Owner = e, Script = s, Fn = fn, Id = ++_netIds, Method = req.Method, Url = url };
            _net.Add(p);
            if (why != null)
            {
                p.Finished = 1;
                NetSay(e, "warn " + why, why);
                _netDone.Enqueue(new NetDone { P = p, Error = why });
                return DynValue.NewTuple(DynValue.Nil, DynValue.NewString(why));
            }
            p.Started = true;
            p.Timeout = req.TimeoutSeconds;
            p.Cts = new System.Threading.CancellationTokenSource();
            e.NetStarts.Enqueue(Now);
            req.MaxBytes = NetMaxBytes;
            req.Cancel = p.Cts.Token;
            var hosts = e.NetworkHosts;
            req.CheckRedirect = u => NetCheckUri(u, hosts);
            req.Done = r => NetReplied(p, r);
            try { _svc.NetFetch(req); }
            catch (Exception ex) { NetReplied(p, new NetReply { Error = "the request did not start: " + ex.Message }); }
            return DynValue.NewNumber(p.Id);
        }

        /// The fetcher's outcome, on any thread: the first one wins; the text is decoded and JSON parsed here, so the main
        /// thread only makes the Lua table.
        private void NetReplied(NetPending p, NetReply r)
        {
            if (r == null || System.Threading.Interlocked.CompareExchange(ref p.Finished, 1, 0) != 0) return;
            var d = new NetDone { P = p, Status = r.Status, Error = r.Error };
            try
            {
                int len = r.Body == null ? 0 : Math.Max(0, Math.Min(r.Length, r.Body.Length));
                bool tooLarge = r.TooLarge || len > NetMaxBytes;
                if (len > NetMaxBytes) len = NetMaxBytes;
                if (len > 0)
                {
                    d.Body = Encoding.UTF8.GetString(r.Body, 0, len);
                    if (d.Body.Length > 0 && d.Body[0] == '﻿') d.Body = d.Body.Substring(1);
                }
                if (tooLarge) d.Error = "too large";
                else if (d.Error == null && LooksJson(r.ContentType, d.Body)) d.Json = ParseJson(d.Body);
            }
            catch (Exception ex) { d.Error = d.Error ?? "unreadable reply: " + ex.Message; }
            _netDone.Enqueue(d);
        }

        private static bool LooksJson(string type, string body)
        {
            if (type != null && type.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                return c == '{' || c == '[';
            }
            return false;
        }

        private static JToken ParseJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (var sr = new StringReader(text))
                using (var r = new Newtonsoft.Json.JsonTextReader(sr) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None, MaxDepth = LuaJson.MaxDepth + 1 })
                {
                    var token = JToken.ReadFrom(r);
                    return r.Read() ? null : token;
                }
            }
            catch (Exception) { return null; }
        }

        /// Runs the outcomes that came (a few a tick) and times out requests the fetcher never answered.
        private void TickNet()
        {
            for (int n = 0; n < NetDeliverPerTick && _netDone.TryDequeue(out var d); n++) NetDeliver(d.P, d);
            for (int i = _net.Count - 1; i >= 0; i--)
            {
                if (i >= _net.Count) continue; // a callback below changed the list
                var p = _net[i];
                if (!p.Started) continue;
                // the fetcher's own timeout comes first; this one is for a fetcher that hangs (a DNS lookup cannot be stopped)
                if (float.IsNaN(p.Deadline)) p.Deadline = Now + p.Timeout + 1f;
                if (Now < p.Deadline) continue;
                if (System.Threading.Interlocked.CompareExchange(ref p.Finished, 1, 0) != 0) continue; // its outcome is queued
                try { p.Cts?.Cancel(); } catch (Exception) { }
                NetDeliver(p, new NetDone { P = p, Error = "timeout" });
            }
        }

        /// Runs a request's callback with res = {ok, status, body, json, error}, under the instruction budget. Nothing
        /// runs for a request that was cancelled or whose plugin unloaded (or reloaded: a new Lua state) since.
        private void NetDeliver(NetPending p, NetDone d)
        {
            if (p.Dropped) return;
            p.Dropped = true;
            _net.Remove(p);
            var e = p.Owner;
            if (e.Script == null || e.Script != p.Script || e.Error != null || !e.Enabled) return;
            bool ok = d.Error == null && d.Status >= 200 && d.Status < 300;
            string err = d.Error ?? (ok ? null : "status " + d.Status);
            if (p.Started)
            {
                if (!ok) NetSay(e, "net: " + p.Method + " " + LogUrl(p.Url) + " -> " + err, "failed");
                else if (!e.NetReplied)
                {
                    e.NetReplied = true;
                    Say(e, "net: " + p.Method + " " + LogUrl(p.Url) + " -> " + d.Status + " (" + Math.Round((d.Body?.Length ?? 0) / 1024.0, 1).ToString(CultureInfo.InvariantCulture) + " KB)");
                }
            }
            var t = new Table(p.Script);
            t.Set("ok", DynValue.NewBoolean(ok));
            t.Set("status", DynValue.NewNumber(d.Status));
            t.Set("body", DynValue.NewString(d.Body ?? ""));
            if (d.Json != null)
            {
                try { t.Set("json", LuaJson.FromJson(p.Script, d.Json)); }
                catch (Exception) { }
            }
            if (err != null) t.Set("error", DynValue.NewString(err));
            Run(e, p.Fn, DynValue.NewTable(t));
        }

        /// net.cancel(id): the request is dropped; its callback never runs.
        private bool NetCancel(Entry e, int id)
        {
            for (int i = 0; i < _net.Count; i++)
                if (_net[i].Id == id && _net[i].Owner == e) { DropNetAt(i); return true; }
            return false;
        }

        /// Unload: every request of the plugin is cancelled and dropped; no callback of it runs after this.
        private void DropNet(Entry e)
        {
            for (int i = _net.Count - 1; i >= 0; i--) if (_net[i].Owner == e) DropNetAt(i);
        }

        private void DropNetAt(int i)
        {
            var p = _net[i];
            p.Dropped = true;
            System.Threading.Interlocked.Exchange(ref p.Finished, 1);
            _net.RemoveAt(i);
            try { p.Cts?.Cancel(); } catch (Exception) { }
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
                Llm = t.Get("llm").IsNil() || t.Get("llm").CastToBool(),
                SpeakReply = t.Get("speak").CastToBool(),
            };
            // examples = {{"make it orange", "set_accent amber"}, ...}: teach the local LLM this command
            var ex = t.Get("examples");
            if (ex.Type == DataType.Table)
                foreach (var v in ex.Table.Values)
                    if (v.Type == DataType.Table && v.Table.Get(1).Type == DataType.String && v.Table.Get(2).Type == DataType.String)
                        cmd.Examples.Add((v.Table.Get(1).String, v.Table.Get(2).String));
            // params: {name={type=, description=, enum={...}, required=true}} or an ordered list {{name=..., ...}, ...}
            var ps = new List<(string, string, string[], string, bool)>();
            var ranges = new Dictionary<string, (double? min, double? max, string unit)>();
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
                    // numbers: min / max / unit, so "fifty percent", "max" or "30 cm" are understood
                    if (spec.Get("min").Type == DataType.Number || spec.Get("max").Type == DataType.Number || spec.Get("unit").Type == DataType.String)
                        ranges[pname] = (spec.Get("min").Type == DataType.Number ? spec.Get("min").Number : (double?)null,
                            spec.Get("max").Type == DataType.Number ? spec.Get("max").Number : (double?)null, TStr(spec, "unit", null));
                }
            }
            cmd.Parameters = MetaCodex.AI.VoiceCommands.Schema(ps.ToArray());
            foreach (var kv in ranges)
            {
                var prop = cmd.Parameters["properties"]?[kv.Key] as Newtonsoft.Json.Linq.JObject;
                if (prop == null) continue;
                if (kv.Value.min.HasValue) prop["minimum"] = kv.Value.min.Value;
                if (kv.Value.max.HasValue) prop["maximum"] = kv.Value.max.Value;
                if (kv.Value.unit != null) prop["x-unit"] = kv.Value.unit;
            }
            // vocabulary = {"ambient", "focus"}: words to hear right (speech-to-text prompt) and fix when misheard
            var vo = t.Get("vocabulary");
            if (vo.Type == DataType.Table) cmd.Vocabulary = vo.Table.Values.Where(v => v.Type == DataType.String).Select(v => v.String).ToArray();
            var ph = t.Get("phrases");
            if (ph.Type == DataType.Table) cmd.Phrases = ph.Table.Values.Where(v => v.Type == DataType.String).Select(v => v.String).ToArray();
            else if (ph.Type == DataType.String) cmd.Phrases = new[] { ph.String };
            // samples = {"show git status", "what changed in the repo"}: natural requests (English); the decision model
            // compares what was said with them (the more ways to ask, the better it finds the command)
            var sa = t.Get("samples");
            if (sa.Type == DataType.Table) cmd.Samples = sa.Table.Values.Where(v => v.Type == DataType.String).Select(v => v.String).ToArray();
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
            Set(ui, "open", a => { var w = OwnWindow(e, Str(a, 0)); return DynValue.NewBoolean(w != null ? ShowOwn(w, 1) : _svc.OpenPanel(Str(a, 0))); });
            Set(ui, "close", a => { var w = OwnWindow(e, Str(a, 0)); if (w == null) return DynValue.NewBoolean(_svc.ClosePanel(Str(a, 0))); _svc.PluginWindowShow(w.FullId, 0); return DynValue.True; });
            Set(ui, "toggle", a => { var w = OwnWindow(e, Str(a, 0)); return DynValue.NewBoolean(w != null ? ShowOwn(w, 2) : _svc.TogglePanel(Str(a, 0))); });
            Set(ui, "panel", a =>
            {
                const string form = "ui.panel{id=\"main\", title=\"Timer\", components={ {type=\"text\", text=\"hello\"} }, on=function(e) end}";
                if (a.Count < 1 || a[0].Type != DataType.Table) throw new ScriptRuntimeException(form);
                var t = a[0].Table;
                var idv = t.Get("id");
                string local = SafeId(idv.IsNil() ? "main" : idv.ToPrintString());
                if (local.Length == 0) throw new ScriptRuntimeException("ui.panel: id needs letters or digits, for example id=\"main\"");
                var spec = new JObject { ["id"] = local };
                try
                {
                    foreach (var key in new[] { "title", "components", "columns", "size" })
                    {
                        var v = t.Get(key);
                        if (!v.IsNil()) spec[key] = LuaJson.ToJson(v, key);
                    }
                }
                catch (ScriptRuntimeException ex) { throw new ScriptRuntimeException("ui.panel: " + ex.Message); }
                if (spec["title"] == null) spec["title"] = string.IsNullOrEmpty(e.Title) ? e.Name : e.Title;
                var errors = new List<string>();
                var warnings = new List<string>();
                ViewSpecCheck.Check(spec, errors, warnings);
                if (errors.Count > 0) throw new ScriptRuntimeException("ui.panel: " + string.Join("; ", errors.Take(3)) + "  (form: " + form + ")");
                string full = "plugin-" + e.Name + "-" + local;
                var w = OwnWindow(e, full);
                if (w == null)
                {
                    int mine = 0;
                    foreach (var x in _windows) if (x.Owner == e) mine++;
                    if (mine >= MaxWindowsPerPlugin) throw new ScriptRuntimeException("ui.panel: at most " + MaxWindowsPerPlugin + " windows a plugin");
                    if (FindWindow(full) != null) throw new ScriptRuntimeException("ui.panel: the window id " + full + " belongs to another plugin or script");
                    w = new PlugWin { Owner = e, LocalId = local, FullId = full };
                    _windows.Add(w);
                }
                string warned = string.Join("; ", warnings);
                if (warned.Length > 0 && warned != w.Warned) foreach (var m in warnings) Say(e, "warn ui.panel " + local + ": " + m);
                w.Warned = warned;
                var on = t.Get("on");
                w.On = on.Type == DataType.Function || on.Type == DataType.ClrFunction ? on : null;
                w.Spec = spec;
                w.Sig = null;
                w.Dirty = true;
                if (t.Get("open").CastToBool()) w.OpenRequested = true;
                _anyDirty = true;
                return DynValue.NewString(full);
            });
            Set(ui, "sound", a =>
            {
                string n = Str(a, 0).ToLowerInvariant();
                if (!IsSoundName(n)) { Say(e, "warn ui.sound: unknown sound '" + Str(a, 0) + "' (click, open, close, ping, toast, alert, error)"); return DynValue.Nil; }
                _svc.PlaySound(n);
                return DynValue.Nil;
            });
            Set(ui, "notify", a =>
            {
                const string form = "ui.notify{text=\"Timer done\", tone=\"ok\", sound=\"ping\", panel=\"main\"}";
                if (a.Count < 1 || a[0].Type != DataType.Table) throw new ScriptRuntimeException(form);
                var t = a[0].Table;
                string text = TStr(t, "text", "");
                if (text.Length == 0) throw new ScriptRuntimeException("ui.notify: text is required, for example " + form);
                string tone = TStr(t, "tone", "info");
                if (tone != "info" && tone != "ok" && tone != "warn" && tone != "error") { Say(e, "warn ui.notify: unknown tone '" + tone + "' (info, ok, warn, error)"); tone = "info"; }
                var sv = t.Get("sound");
                string sound = sv.Type == DataType.Boolean && !sv.Boolean ? null : sv.Type == DataType.String ? sv.String.ToLowerInvariant() : "ping";
                if (sound != null && !IsSoundName(sound)) { Say(e, "warn ui.notify: unknown sound '" + sound + "' (click, open, close, ping, toast, alert, error)"); sound = null; }
                var sp = t.Get("speak");
                string speak = sp.Type == DataType.Boolean ? (sp.Boolean ? text : null) : sp.Type == DataType.String ? sp.String : null;
                Action onTap = null;
                var pv = t.Get("panel");
                if (!pv.IsNil())
                {
                    var w = OwnWindow(e, pv.ToPrintString());
                    if (w == null) Say(e, "warn ui.notify: panel '" + pv.ToPrintString() + "' is not a window of this plugin (make it with ui.panel first); the tap does nothing");
                    else onTap = () => { if (_windows.Contains(w)) ShowOwn(w, 1); };
                }
                _svc.Notify(text, tone, sound, speak, onTap);
                return DynValue.Nil;
            });
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

            var menu = new Table(s);
            Set(menu, "add", a =>
            {
                const string form = "menu.add{id=\"timer\", label=\"TIMER\", panel=\"main\"} or menu.add{id=\"go\", label=\"GO\", run=function() end}";
                if (a.Count < 1 || a[0].Type != DataType.Table) throw new ScriptRuntimeException(form);
                var t = a[0].Table;
                string id = TStr(t, "id", "");
                string label = TStr(t, "label", "");
                if (id.Length == 0 || label.Length == 0) throw new ScriptRuntimeException("menu.add: id and label are required, for example " + form);
                var run = t.Get("run");
                bool hasRun = run.Type == DataType.Function || run.Type == DataType.ClrFunction;
                var pv = t.Get("panel");
                bool hasPanel = !pv.IsNil();
                if (hasRun == hasPanel) throw new ScriptRuntimeException("menu.add " + id + ": give exactly one of panel=<window id> or run=function() end, for example " + form);
                // panel: one of this plugin's windows, made before or after (main.lua may add the tile first); a window that
                // still does not exist when main.lua has run is a warning (Load), and the tap says so
                string tileWindow = null;
                if (hasPanel)
                {
                    var w = OwnWindow(e, pv.ToPrintString());
                    string want = pv.ToPrintString();
                    string prefix = "plugin-" + e.Name + "-";
                    tileWindow = w != null ? w.FullId : want.StartsWith(prefix, StringComparison.Ordinal) ? want : prefix + SafeId(want);
                }
                label = label.ToUpperInvariant();
                if (label.Length > 8)
                {
                    Say(e, "warn menu.add " + id + ": the label is cut to 8 characters (" + label.Substring(0, 8) + ")");
                    label = label.Substring(0, 8);
                }
                string glyph = TStr(t, "glyph", "");
                glyph = glyph.Length == 0 ? "\u25C6" : glyph.Substring(0, char.IsHighSurrogate(glyph[0]) && glyph.Length > 1 ? 2 : 1);
                var tile = new PluginTile { Owner = e.Name, Id = id, Label = label, Glyph = glyph, Icon = TStr(t, "icon", "mark"), Window = tileWindow };
                int at = _tileEntries.FindIndex(x => x.Owner == e && x.Tile.Id == id);
                if (at < 0 && _tileEntries.Count >= MaxPluginTiles)
                {
                    Say(e, "menu.add " + id + ": the menu is full (" + MaxPluginTiles + " plugin tiles)");
                    return DynValue.False;
                }
                var entry = new TileEntry { Owner = e, Tile = tile, Run = hasRun ? run : null };
                if (at >= 0) _tileEntries[at] = entry; else _tileEntries.Add(entry);
                PushTiles();
                return DynValue.True;
            });
            Set(menu, "remove", a =>
            {
                string id = Str(a, 0);
                bool removed = _tileEntries.RemoveAll(x => x.Owner == e && x.Tile.Id == id) > 0;
                if (removed) PushTiles();
                return DynValue.NewBoolean(removed);
            });
            g.Set("menu", DynValue.NewTable(menu));

            var json = new Table(s);
            Set(json, "encode", a =>
            {
                try { return DynValue.NewString(LuaJson.Encode(a.Count > 0 ? a[0] : DynValue.Nil)); }
                catch (ScriptRuntimeException ex) { throw new ScriptRuntimeException("json.encode: " + ex.Message); }
            });
            Set(json, "decode", a => LuaJson.Decode(s, Str(a, 0)));
            g.Set("json", DynValue.NewTable(json));

            var terminal = new Table(s);
            Set(terminal, "send", a => { _svc.TerminalSend(Str(a, 0)); return DynValue.Nil; });
            Set(terminal, "run", a => { _svc.TerminalSend(Str(a, 0) + "\r"); return DynValue.Nil; });
            Set(terminal, "new", a => { _svc.TerminalNew(); return DynValue.Nil; });
            g.Set("terminal", DynValue.NewTable(terminal));

            var browser = new Table(s);
            Set(browser, "open", a => { _svc.OpenBrowser(Str(a, 0)); return DynValue.Nil; });
            g.Set("browser", DynValue.NewTable(browser));

            // net: https requests to the hosts plugin.json "network" declares; the reply comes to a callback in a later tick
            var net = new Table(s);
            Set(net, "get", a => NetCall(e, s, false, a));
            Set(net, "post", a => NetCall(e, s, true, a));
            Set(net, "cancel", a => DynValue.NewBoolean(NetCancel(e, (int)Num(a, 0, -1))));
            g.Set("net", DynValue.NewTable(net));

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
            Set(app, "cancel", a =>
            {
                int id = (int)Num(a, 0, -1);
                _timers.RemoveAll(t => t.Id == id && t.Owner == e);
                _wallTimers.RemoveAll(t => t.Id == id && t.Owner == e);
                return DynValue.Nil;
            });
            Set(app, "clock", a =>
            {
                if (a.Count == 0 || a[0].Type != DataType.Table) return DynValue.NewNumber(_svc.WallClock());
                const string form = "app.clock{year=2026, month=11, day=15, hour=9, min=30}";
                var t = a[0].Table;
                double? year = TNum(t, "year"), month = TNum(t, "month"), day = TNum(t, "day");
                if (year == null || month == null || day == null) throw new ScriptRuntimeException("app.clock needs year, month and day: " + form);
                double hour = TNum(t, "hour") ?? 0, min = TNum(t, "min") ?? 0, sec = TNum(t, "sec") ?? 0;
                if (Math.Abs(month.Value) > 1e5 || Math.Abs(day.Value) > 1e6 || Math.Abs(hour) > 1e7 || Math.Abs(min) > 1e8) throw new ScriptRuntimeException("app.clock: a field is far out of range: " + form);
                try { return DynValue.NewNumber(LocalToEpoch((int)year.Value, (int)month.Value, (int)day.Value, (int)hour, (int)min, sec)); }
                catch (ArgumentException) { throw new ScriptRuntimeException("app.clock: that date does not exist (year 1..9999): " + form); }
            });
            Set(app, "date", a =>
            {
                double secs = Num(a, 0, _svc.WallClock());
                if (Math.Abs(secs) > 2e11) throw new ScriptRuntimeException("app.date(t): t is seconds since 1970 (use app.clock())");
                var d = Epoch.AddSeconds(Math.Floor(secs)).ToLocalTime();
                var t = new Table(s);
                t.Set("year", DynValue.NewNumber(d.Year)); t.Set("month", DynValue.NewNumber(d.Month)); t.Set("day", DynValue.NewNumber(d.Day));
                t.Set("hour", DynValue.NewNumber(d.Hour)); t.Set("min", DynValue.NewNumber(d.Minute)); t.Set("sec", DynValue.NewNumber(d.Second));
                t.Set("wday", DynValue.NewNumber((int)d.DayOfWeek + 1)); t.Set("yday", DynValue.NewNumber(d.DayOfYear));
                return DynValue.NewTable(t);
            });
            Set(app, "at", a =>
            {
                var fn = Fn(a, 1);
                if (fn == null || a.Count < 1 || a[0].Type != DataType.Number) throw new ScriptRuntimeException("app.at(t, function() ... end): t is seconds since 1970, for example app.at(app.clock() + 60, fn)");
                var t = new WallTimer { Owner = e, Id = ++_timerIds, At = a[0].Number, Fn = fn };
                _wallTimers.Add(t);
                return DynValue.NewNumber(t.Id);
            });
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
                // what voice, the decision model and agents still lack to use it well (docs/scripting.md "Voice commands")
                foreach (var w in MetaCodex.AI.VoiceCommands.Lint(cmd)) Say(e, "voice.command " + cmd.Name + ": " + w);
                return DynValue.Nil;
            });
            Set(voice, "remove", a => DynValue.NewBoolean(_svc.Voice != null && _svc.Voice.Remove(Str(a, 0), e.Key)));
            Set(voice, "say", a => { _svc.VoiceSay(Str(a, 0)); return DynValue.Nil; });
            Set(voice, "run", a => { _svc.VoiceRun(Str(a, 0)); return DynValue.Nil; });
            Set(voice, "listen", a => { _svc.VoiceListen(); return DynValue.Nil; });
            Set(voice, "list", a =>
            {
                var t = new Table(s);
                if (_svc.Voice != null) foreach (var c in _svc.Voice.All) if (c.Enabled) t.Append(DynValue.NewString(c.Name));
                return DynValue.NewTable(t);
            });
            // the registry as JSON text: every enabled command with its schema, names, phrases, samples and warnings
            Set(voice, "registry", a => DynValue.NewString(_svc.Voice == null ? "{}" : _svc.Voice.Describe().ToString(Newtonsoft.Json.Formatting.None)));
            g.Set("voice", DynValue.NewTable(voice));

            if (e.Kind == "plugin")
            {
                var plugin = new Table(s);
                plugin.Set("id", DynValue.NewString(e.Name));
                plugin.Set("name", DynValue.NewString(e.Title ?? e.Name));
                plugin.Set("version", DynValue.NewString(e.Version ?? ""));
                plugin.Set("dir", DynValue.NewString(e.LinuxDir));
                plugin.Set("kind", DynValue.NewString(e.PluginKind));
                var hostList = new Table(s);
                foreach (var h in e.NetworkHosts) hostList.Append(DynValue.NewString(h));
                plugin.Set("network", DynValue.NewTable(hostList));
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
