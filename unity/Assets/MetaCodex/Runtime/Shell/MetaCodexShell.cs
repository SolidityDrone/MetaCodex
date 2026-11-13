using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.App;
using MetaCodex.Hands;
using MetaCodex.Scripting;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The spatial shell around the terminals: the dock, the panels (terminals, files, git, browser, scripts,
    /// settings), the hand gestures, the Lua behaviour scripts and the link to the MQH daemon. Everything is
    /// built in code from the theme, so agents can extend it with data and scripts instead of rebuilds.
    public sealed partial class MetaCodexShell : MonoBehaviour, IScriptServices
    {
        [SerializeField] private MetaCodexApp app;
        [SerializeField] private OVRHand leftHand;
        [SerializeField] private OVRHand rightHand;
        [SerializeField] private Font displayFont;
        [SerializeField] private Font bodyFont;
        [SerializeField] private Font[] symbolFonts;
        [SerializeField] private Material shapeMaterial;
        [SerializeField] private Material beamMaterial;

        public ShellSettings Settings { get; private set; }
        public UiInputRouter Router { get; private set; }
        public GestureEngine Gestures { get; private set; }
        public ScriptHost Scripts { get; private set; }
        public MqhClient Mqh { get; private set; }
        /// MQH in the headset's Linux, started and watched by the app (MqhHost).
        public MqhHost MqhHost { get; private set; }
        public GitService Git { get; private set; }
        public FileService Files { get; private set; }
        public UiToasts Toasts { get; private set; }
        public SimulatedHands SimHands { get; private set; }
        public UiSounds Sounds { get; private set; }
        public IReadOnlyDictionary<string, UiPanel> PanelsById => _panels;
        public MetaCodexApp App => app;
        /// Used when there is no app (Editor self-tests): stands in for the user's head.
        public Transform HeadOverride;
        public Transform Head => app != null && app.Head != null ? app.Head : HeadOverride;
        public readonly List<string> LogLines = new List<string>();

        private readonly Dictionary<string, UiPanel> _panels = new Dictionary<string, UiPanel>();
        private readonly Dictionary<TerminalInstance, UiPanel> _frames = new Dictionary<TerminalInstance, UiPanel>();
        private readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        private Transform _root;
        private bool _built;
        private string _settingsPath;
        private float _nextMqhPoll;
        private int _remoteRevision = -1;

        /// Editor/self-test entry: everything wired but nothing tracked; `hands` replaces OVR hands.
        public void Configure(MetaCodexApp a, Font display, Font body, Font[] symbols, Material shapes, Material beam, string settingsPath = null)
        {
            app = a; displayFont = display; bodyFont = body; symbolFonts = symbols; shapeMaterial = shapes; beamMaterial = beam;
            _settingsPath = settingsPath;
        }

        private void Awake()
        {
            if (app != null)
            {
                app.TerminalCreated += WrapTerminal;
                app.TerminalClosed += OnTerminalClosed;
                app.FocusChanged += OnFocusChanged;
                app.ExtraCommand = ControlCommand;
                app.AsyncCommand = (line, reply) => VoiceAsyncCommand(line, reply) || SecretCommand(line, reply) || ViewAsyncCommand(line, reply);
                app.Recentered += OnRecentered;
            }
        }

        private void Start() => Build();

        /// Creates theme, router, gestures, scripts and panels. Safe to call once; Start calls it.
        public void Build(string scriptsDir = null, IHandSource hands = null)
        {
            if (_built) return;
            _built = true;
            Settings = ShellSettings.Load(_settingsPath);
            _hideFirstTerminal = !Settings.terminalAtStart;
            if (Settings.settingsVersion < 2)
            {
                // readability (first headset test: text too small and soft): bigger panels, supersampling
                Settings.uiScale = Mathf.Max(Settings.uiScale, 1.2f);
                Settings.renderScale = Mathf.Max(Settings.renderScale, 1.2f);
                Settings.foveation = false;
                Settings.settingsVersion = 2;
                Settings.Save(_settingsPath);
                _localSettingsWin = true; // MQH's copy is older: push ours instead of taking it at the first sync
            }
            if (Settings.settingsVersion < 3)
            {
                // one standard appearance (owner, 0.5): no themes, accents, scanlines or grid; the terminal starts
                // hidden; voice on 2 threads (3 and 4 measured slower on the 3 cores the AI service has)
                Settings.accent = "blue";
                Settings.scanlines = false;
                Settings.grid = false;
                Settings.motion = 0f;
                Settings.panelOpacity = Mathf.Max(Settings.panelOpacity, 0.92f);
                Settings.foveation = false;
                Settings.terminalAtStart = false;
                Settings.voice.threads = 2;
                Settings.settingsVersion = 3;
                Settings.Save(_settingsPath);
                _localSettingsWin = true;
            }
            if (Settings.settingsVersion < 4)
            {
                // sharpness is no longer a setting (owner, 0.7.3): always the sharpest render scale
                Settings.renderScale = RenderScale;
                Settings.settingsVersion = 4;
                Settings.Save(_settingsPath);
                _localSettingsWin = true;
            }
            var theme = new UiTheme();
            theme.SetAccent(Settings.accent);
            theme.Body = UiTheme.MakeFont(bodyFont, symbolFonts);
            theme.Display = UiTheme.MakeFont(displayFont, symbolFonts);
            theme.Mono = FindMonoFont() ?? theme.Body;
            theme.ShapeMaterial = shapeMaterial;
            theme.BeamMaterial = beamMaterial;
            UiTheme.Current = theme;
            ApplyMaterialSettings();

            _root = new GameObject("Shell").transform;
            _root.SetParent(transform, false);
            Router = new GameObject("UiInputRouter").AddComponent<UiInputRouter>();
            Router.transform.SetParent(_root, false);
            Router.Head = Head;
            Router.Settings = Settings.gestures;
            Router.Init();
            if (hands == null && (leftHand != null || rightHand != null)) hands = new OvrHandSource(leftHand, rightHand);
            if (hands == null) { SimHands = new SimulatedHands(); hands = SimHands; }
            else SimHands = hands as SimulatedHands;
            Gestures = new GestureEngine(hands, Settings.gestures);
            Gestures.HeadPose = () => Head != null ? new Pose(Head.position, Head.rotation) : new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            Gestures.OnGesture += OnGesture;
            Router.Gestures = Gestures;
            Router.Selected += OnSelected;

            Toasts = new GameObject("Toasts").AddComponent<UiToasts>();
            Toasts.transform.SetParent(_root, false);
            Toasts.Head = Head;
            Sounds = new GameObject("UiSounds").AddComponent<UiSounds>();
            Sounds.transform.SetParent(_root, false);

            Mqh = new MqhClient(Settings.mqhUrl, Settings.mqhToken, _main);
            InitVault();
            MqhHost = new MqhHost(() => app != null ? app.Host : null, () => Settings.mqhUrl, MqhTokenNow,
                () => Vault?.Rearm(MqhEnvKeyName), Log, _main);
            MqhHost.Online += OnMqhOnline;
            var host = app != null ? app.Host : null;
            Git = new GitService(host);
            Files = new FileService(host, Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "../..")) : null);
            InitClipboard();

            InitVoice(); // before the scripts: plugins register voice commands while they load
            string dir = scriptsDir ?? DefaultScriptsDir(host);
            Scripts = new ScriptHost(this, dir);
            Scripts.Changed += () => { RefreshScriptsPanel(); RefreshGesturesPanel(); };
            RetireWindowShortcuts(); // once: no gesture opens or closes a window by default (owner 2026-11-13)
            RetireStarterExamples(); // no seeding: gesture examples are opt-in (GESTURES > ALL > EXAMPLES); old untouched copies go once
            InitSessionContext(); // the session's context for the local model (docs/session-context.md)
            UiKeyboard.TerminalKeyboardRequested += () => app?.Input?.ToggleSystemKeyboard();
            SystemKeyboard.Opened += KeyboardTip;

            BuildPanels();
            BuildWrist();
            BuildOrb();
            BuildSplash(); // the logo over the room while the rest starts (ShellSplash)
            if (app != null) foreach (var t in app.Terminals) WrapTerminal(t);
            Log($"shell up: {_panels.Count} panels, scripts in {dir}");
        }

        private TMP_FontAsset FindMonoFont()
        {
            if (app == null) return null;
            var v = app.GetComponentInChildren<MetaCodex.Terminal.TerminalView>(true);
            if (v != null) return v.Font;
            var any = FindAnyObjectByType<MetaCodex.Terminal.TerminalView>();
            return any != null ? any.Font : null;
        }

        /// On the headset: the shared folder the userland sees as /mnt/quest, so agents can write scripts.
        public static string DefaultScriptsDir(MetaCodex.Linux.LinuxHost host)
        {
            string shared = host != null && !string.IsNullOrEmpty(host.SharedDir) ? host.SharedDir : Application.persistentDataPath;
            return Path.Combine(shared, "scripts");
        }

        /// PANEL OPACITY as the shaders get it: the panels' `_Opacity` and the wrist menu's glass (ShellWrist).
        private float PanelOpacity => Mathf.Clamp(Settings.panelOpacity, 0.3f, 1f);

        private void ApplyMaterialSettings()
        {
            if (shapeMaterial != null)
            {
                shapeMaterial.SetFloat("_Scanlines", Settings.scanlines ? 1f : 0f);
                shapeMaterial.SetFloat("_Opacity", PanelOpacity);
                shapeMaterial.SetFloat("_Motion", Mathf.Clamp(Settings.motion, 0f, 2f));
                shapeMaterial.SetFloat("_Grid", Settings.grid ? 1f : 0f);
                if (UiTheme.Current != null) shapeMaterial.SetVector("_Accent", UiTheme.Lin(UiTheme.Current.Accent)); // conduits, the cast's heat, the pointers' light
            }
            if (Wrist != null) Wrist.GlassOpacity = PanelOpacity; // the wrist menu's glass has its own material: the same value
            if (Sounds != null) { Sounds.Enabled = Settings.sounds; Sounds.Volume = Mathf.Clamp01(Settings.soundVolume); }
            ApplyDisplaySettings();
        }

        /// Render resolution x on the headset: text stays sharp on the curved panels (owner, 0.7.3: fixed, no slider).
        public const float RenderScale = 1.6f;

        /// Render resolution and foveation on the headset (the Editor keeps its own: the URP asset is a file there).
        private void ApplyDisplaySettings()
        {
            if (Application.isEditor) return;
            try
            {
                // only what changed (0.7.19 log: OnSettingsChanged re-applied the render scale and the foveation level at
                // every settings change, several times a minute while talking)
                if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp
                    && !Mathf.Approximately(urp.renderScale, RenderScale))
                    urp.renderScale = RenderScale;
                if (!_displayApplied)
                {
                    _displayApplied = true;
                    _foveationOn = !WantFoveation; // the first time: apply it whatever it is
                    // the GPU at its sustained high level (0.7.19, measured with VrApi: 98-99 % busy at level 1, 350 MHz, the
                    // whole session: 61-63 of 72 fps). Without a request Horizon OS kept it low.
                    try { OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh; Log("GPU level: sustained high"); }
                    catch (Exception ex) { Log("GPU level: " + ex.Message); }
                    Log("GC: " + (UnityEngine.Scripting.GarbageCollector.isIncremental ? "incremental" : "not incremental (PlayerSettings.gcIncremental is off)"));
                }
                ApplyFoveation();
            }
            catch (Exception ex) { Log("display settings: " + ex.Message); }
        }

        private bool _foveationOn, _displayApplied;

        /// Foveation is on with FOVEATED, and while Ambient Focus is shown (0.7.19, owner: "performance seems quite bad
        /// during ambient focus"): its full-view pass shades every eye-buffer pixel at RenderScale, and the edges of the
        /// view are dark or blurred by it then, so lower shading there costs nothing visible.
        private bool WantFoveation => Settings.foveation || FocusShownNow;

        /// Applies WantFoveation when it changed (called every frame by UpdateFocus; a no-op in the Editor).
        private void ApplyFoveation()
        {
            bool on = WantFoveation;
            if (Application.isEditor || on == _foveationOn) return;
            _foveationOn = on;
            try
            {
                var displays = new List<UnityEngine.XR.XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                foreach (var d in displays) d.foveatedRenderingLevel = on ? 0.5f : 0f;
                // the rig's OVRManager turns fixed foveation on (High) by itself; it is the switch that works. Dynamic
                // (owner 2026-11-12: stable fps even in the hardest tasks): Horizon OS raises foveation only as the GPU
                // gets busy, up to High, and keeps the edges sharp while it is idle (docs: unity-fixed-foveated-rendering)
                OVRManager.useDynamicFoveatedRendering = on;
                OVRManager.foveatedRenderingLevel = on ? OVRManager.FoveatedRenderingLevel.High : OVRManager.FoveatedRenderingLevel.Off;
                Log("foveation " + (on ? "on (dynamic, up to High)" + (Settings.foveation ? "" : ": Ambient Focus") : "off"));
            }
            catch (Exception ex) { Log("foveation: " + ex.Message); }
        }

        /// Self-tests: one frame of the shell's own Update (voice, focus, layout, the orb...).
        public void UpdateForTest() => Update();

        private void Update()
        {
            HitchCheck();
            while (_main.TryDequeue(out var a))
            {
                double c0 = _hitchSw.Elapsed.TotalMilliseconds;
                try { a(); } catch (Exception ex) { Log("main-thread callback failed: " + ex.Message); }
                double cms = _hitchSw.Elapsed.TotalMilliseconds - c0;
                // a slow one says where it comes from (the compiler's name of a lambda carries its method's name)
                if (cms > 50 && _slowCallbackLines < 300) { _slowCallbackLines++; Log($"[slow callback] {cms:F0} ms {a.Method.DeclaringType?.Name}.{a.Method.Name}"); }
            }
            HitchMark(0);
            Scripts?.Tick(Time.unscaledTime);
            HitchMark(1);
            MqhHost?.Tick();
            HitchMark(2);
            TickVoice();
            TickBindings(Time.unscaledTime); // gesture bindings: poses held BindingHold (ShellGestures)
            SyncLocalCoder(); // the OpenCode config (no snapshots; the local coder when installed): at most every 5 s, once set
            HitchMark(3);
            TickSessionContext();
            HitchMark(4);
            UpdateFocus(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            HitchMark(5);
            FlushPendingRebuild();
            HitchMark(6);
            RunLater();
            HitchMark(7);
            ScanTerminalLinks();
            PushSettingsWhenQuiet();
            if (Settings != null && Settings.mqhSync && Time.unscaledTime >= _nextMqhPoll) { _nextMqhPoll = Time.unscaledTime + 5f; PullRemoteSettings(); }
            HitchMark(8);
            UpdateLayout();
            HitchMark(9);
            TickOrb(Time.unscaledDeltaTime);
            TickSplash(Time.unscaledDeltaTime);
            HitchMark(10);
        }

        // ---- hitches (0.7.19, owner: "stuttering in the first 10 s"): a frame that took more than 25 ms (72 Hz is 13.9)
        // goes into the session log with what the shell's Update spent in that frame, per step, and whether the GC ran.
        // At most 4 lines a second and 400 a session; the Update itself costs ~20 stopwatch reads a frame.
        private static readonly string[] HitchSteps = { "callbacks", "scripts", "mqh", "voice", "context", "focus", "rebuild", "later", "sync", "layout", "orb" };
        private readonly double[] _hitchMs = new double[11]; // the last frame's steps (HitchCheck reads them before this frame's)
        private readonly System.Diagnostics.Stopwatch _hitchSw = System.Diagnostics.Stopwatch.StartNew();
        private double _hitchAt;
        private int _hitchLines, _hitchGc = -1, _slowCallbackLines;
        private float _hitchLogAt;

        private void HitchMark(int step)
        {
            double now = _hitchSw.Elapsed.TotalMilliseconds;
            _hitchMs[step] = now - _hitchAt;
            _hitchAt = now;
        }

        private void HitchCheck()
        {
            float dt = Time.unscaledDeltaTime;
            int gc = GC.CollectionCount(0);
            if (dt > 0.025f && Time.frameCount > 2 && _hitchLines < 400 && Time.unscaledTime - _hitchLogAt > 0.25f)
            {
                _hitchLines++;
                _hitchLogAt = Time.unscaledTime;
                var sb = new System.Text.StringBuilder(160);
                double shell = 0;
                for (int i = 0; i < _hitchMs.Length; i++) shell += _hitchMs[i];
                sb.Append("[hitch] t=").Append(Time.realtimeSinceStartup.ToString("F1")).Append(" s, frame ").Append((dt * 1000f).ToString("F0"))
                  .Append(" ms, shell ").Append(shell.ToString("F1")).Append(" ms");
                for (int i = 0; i < _hitchMs.Length; i++)
                    if (_hitchMs[i] >= 1.5) sb.Append(", ").Append(HitchSteps[i]).Append(' ').Append(_hitchMs[i].ToString("F1"));
                if (_hitchGc >= 0 && gc != _hitchGc) sb.Append(", GC");
                Log(sb.ToString());
            }
            _hitchGc = gc;
            _hitchAt = _hitchSw.Elapsed.TotalMilliseconds;
        }

        // ------------------------------------------------------------------ panels

        private T Panel<T>(string id, string title, float w, float h) where T : UiPanel
        {
            var p = UiPanel.Create<T>(id, title, w, h, _root);
            _panels[id] = p;
            Router.Register(p);
            p.Closed += _ => { SaveLayout(); NoteClosedRef(id); };
            p.gameObject.SetActive(false);
            return p;
        }

        public IEnumerable<string> PanelIds => _panels.Keys;

        /// Panels that moved inside another one: GIT is the vault's GIT tab now (ui.open("git") still works), and SCRIPTS,
        /// GESTURES and the daemon link (MQH > STATUS) are tabs of MQH with the new SKILLS and PLUGINS. CONTROL's SETTINGS
        /// pages have their category's id: display, look, focus, hands, about (SETTINGS' tabs 1, 3, 2, 0, 4).
        public static readonly Dictionary<string, (string panel, int tab)> PanelAliases = new Dictionary<string, (string, int)>
        {
            { "display", ("settings", 1) }, { "look", ("settings", 3) }, { "focus", ("settings", 2) }, { "hands", ("settings", 0) }, { "about", ("settings", 4) },
            { "git", ("vault", 1) }, { "storage", ("system", 1) }, { "tools", ("system", 0) },
            { "skills", ("mqh", MqhSkillsTab) }, { "plugins", ("mqh", MqhPluginsTab) }, { "scripts", ("mqh", MqhScriptsTab) },
            { "gestures", ("mqh", MqhGesturesTab) }, { "daemon", ("mqh", MqhDaemonTab) },
            { "links", ("browser", -1) }, // WEB's bookmarks and local servers (the panel kept its old id: saved layouts name it)
        };

        private static bool Alias(ref string id, out int tab)
        {
            tab = -1;
            if (id == null || !PanelAliases.TryGetValue(id, out var a)) return false;
            id = a.panel;
            tab = a.tab;
            return true;
        }

        private UiTabs PanelTabs(string id) => id == "vault" ? _vTabs : id == SidebarId ? _sbTabs : id == "system" ? _sysTabs : id == "settings" ? _settingsTabs : id == "mqh" ? _mqhTabs : null;

        /// Open (an alias counts when its tab is the one shown).
        public bool IsPanelOpen(string id)
        {
            bool alias = Alias(ref id, out int tab);
            if (!_panels.TryGetValue(id ?? "", out var p) || !p.IsOpen) return false;
            return !alias || tab < 0 || PanelTabs(id)?.Selected == tab;
        }

        public bool OpenPanel(string id)
        {
            NoteRef(id, "opened"); // what "it" means next (ShellContext)
            _refDepth++;
            try { return OpenPanelCore(id); }
            finally { _refDepth--; }
        }

        private bool OpenPanelCore(string id)
        {
            if (id == ControlId) return OpenControl(null); // the rail alone is not a window: CONTROL on its last category
            if (id == SidebarId) return SetSidebar(true); // it opens beside the open thread, never alone
            if (Alias(ref id, out int tab))
            {
                bool ok = OpenPanel(id);
                if (tab >= 0) PanelTabs(id)?.Select(tab);
                return ok;
            }
            if (!_panels.TryGetValue(id ?? "", out var p)) return false;
            // CONTROL shows one category at a time: another one takes the shown window's place (ShellControl)
            var swapFrom = IsControlPage(id) ? ShownControlPage(id) : null;
            if (!p.IsOpen)
            {
                // back where the user left it when that place is still free and in view, else the best free place:
                // beside the window it is opened from (or the one it belongs to), or in front (ShellPlacement)
                if (swapFrom != null) TakePlaceOf(p, swapFrom);
                else if (!_placed.Contains(id) || !OldPlaceStillGood(p)) PlaceSmart(id, p, OpenerNow() ?? ContextOpener(id));
                else FaceUser(p); // back where it was, turned to where the user stands now
                p.Open();
                _placed.Add(id);
            }
            else p.Open(); // re-plays the open glow so it is easy to spot
            if (IsControlPage(id)) { CloseOtherControlPages(id); AttachRail(id); }
            if (swapFrom == null || !Floating) OnOpenedFloating(p);
            OnPanelOpened(id);
            return true;
        }

        public bool ClosePanel(string id)
        {
            NoteRef(id, "closed");
            _refDepth++;
            try { return ClosePanelCore(id); }
            finally { _refDepth--; }
        }

        private bool ClosePanelCore(string id)
        {
            if (id == ControlId)
            {
                // CONTROL is its window and the rail: both go
                ShownControlPage(null)?.Close();
                if (_rail != null && _rail.IsOpen) _rail.Close();
                return _rail != null;
            }
            Alias(ref id, out _);
            if (!_panels.TryGetValue(id ?? "", out var p)) return false;
            p.Close();
            return true;
        }

        public bool TogglePanel(string id)
        {
            if (id == ControlId) { ToggleControl(); return true; } // CONTROL is its window and the rail, never the rail alone
            if (Alias(ref id, out int tab))
            {
                if (!_panels.TryGetValue(id, out var ap)) return false;
                if (ap.IsOpen && (tab < 0 || PanelTabs(id)?.Selected == tab)) ap.Close();
                else { OpenPanel(id); if (tab >= 0) PanelTabs(id)?.Select(tab); }
                return true;
            }
            if (!_panels.TryGetValue(id ?? "", out var p)) return false;
            if (p.IsOpen) p.Close(); else OpenPanel(id);
            return true;
        }

        private readonly HashSet<string> _placed = new HashSet<string>();

        /// The best free place for a panel that is about to open (ShellPlacement: in front of where the user faces,
        /// else the nearest free place round it).
        private void PlaceDefault(string id, UiPanel p) => PlaceSmart(id, p);

        /// Half the angle a panel covers seen from `distance` away (degrees).
        public static float HalfAngle(UiPanel q, float distance) => Mathf.Atan2(q.Width * 0.5f * q.RestLossyScale.x, distance) * Mathf.Rad2Deg;

        /// The angle nearest `preferred` (searching outwards both ways up to `limit` degrees) where an interval of
        /// +-half fits between the occupied intervals with a small margin; NaN when there is no room.
        public static float FindFreeYaw(float preferred, float half, List<(float center, float half)> occupied, float limit, float margin = 2f)
        {
            for (float off = 0f; off <= limit; off += 1f)
            {
                foreach (float sign in off == 0f ? new[] { 1f } : new[] { 1f, -1f })
                {
                    float yaw = preferred + off * sign;
                    bool free = true;
                    foreach (var o in occupied)
                        if (Mathf.Abs(Mathf.DeltaAngle(o.center, yaw)) < half + o.half + margin) { free = false; break; }
                    if (free) return yaw;
                }
            }
            return float.NaN;
        }

        /// Turns a window about the vertical so its face looks at the user (its tilt kept).
        private void FaceUser(UiPanel p)
        {
            var head = Head;
            if (head == null || p == null) return;
            var d = p.transform.position - head.position;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return;
            var e = p.transform.rotation.eulerAngles;
            p.transform.rotation = Quaternion.Euler(e.x, Quaternion.LookRotation(d.normalized, Vector3.up).eulerAngles.y, 0f);
        }

        private static void TiltTowards(UiPanel p, Transform head)
        {
            if (head == null) return;
            var dir = p.transform.position - head.position;
            p.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        private void OnRecentered(Vector3 center, Quaternion rot)
        {
            var from = app != null ? app.RecenterFrom : null;
            var main = app != null ? app.RecenterMain : null;
            if (app != null) { app.RecenterFrom = null; app.RecenterMain = null; }
            foreach (var kv in _panels)
            {
                if (kv.Key != "dock") continue;
                // the WRIST main menu replaces the dock
                if (Settings.wristMenu) { if (kv.Value.IsOpen) kv.Value.Close(); continue; }
                PlaceDefault("dock", kv.Value);
                if (!kv.Value.IsOpen) kv.Value.Open();
            }
            // FIXED: the windows keep their places round the user (ShellPlacement); FLOAT lays its ring out again below
            if (!Floating) RecenterKeepLayout(from, main != null ? main.GetComponent<UiPanel>() : null);
            if (Orb != null && Settings.orb) { FrameOrb(true); Orb.Teleport(OrbHome()); } // the orb comes along
            ResetCarousel();
        }

        private void SaveLayout() { }

        // ------------------------------------------------------------------ terminal frames

        private void WrapTerminal(TerminalInstance inst)
        {
            if (!_built || _frames.ContainsKey(inst) || inst.View == null) return;
            var t = UiTheme.Current;
            float bezel = 0.012f;
            var v = inst.View;
            float w = v.Width + 2 * bezel, h = v.Height + t.HeaderHeight + bezel * 2;
            var frame = UiPanel.Create<UiPanel>("term-" + inst.Number, "TERMINAL " + inst.Number, w, h, _root);
            frame.Closable = false;
            frame.HideCloseButton(); // ✕ below ends the terminal (or hides the last one); – hides it
            // reparent: keep the view's world pose -> derive the frame pose from it
            var viewTr = v.transform;
            Vector3 topLeftWorld = viewTr.position;
            var rot = viewTr.rotation;
            frame.transform.rotation = rot;
            var localTopLeft = frame.PanelToLocal(new Vector2(bezel, t.HeaderHeight + bezel), -0.004f);
            frame.transform.position = topLeftWorld - rot * localTopLeft;
            viewTr.SetParent(frame.transform, true);
            viewTr.localPosition = localTopLeft;
            viewTr.localRotation = Quaternion.identity;
            inst.Frame = frame.transform;
            float bx = w - 0.044f;
            frame.Add(new UiButton("✕", () => CloseOrHideTerminal(inst), UiButtonStyle.Ghost) { Rect = new Rect(bx, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("–", () => HideTerminal(inst), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.042f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("+", () => app?.NewTerminal(), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.084f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("⌨", () => app.Input?.ToggleSystemKeyboard(), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.126f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("⎘", () => { app?.Focus(inst); app?.Input?.Paste(GUIUtility.systemCopyBuffer); }, UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.168f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            var link = frame.Add(new UiButton("⇗", () => OpenTerminalLink(inst), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.210f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            _linkButtons[inst] = link;
            // a tap on the terminal area focuses it
            frame.Add(new UiHitArea(() => app?.Focus(inst)) { Rect = new Rect(bezel, t.HeaderHeight + bezel, v.Width, v.Height) });
            _frames[inst] = frame;
            Router.Register(frame);
            if (_hideFirstTerminal) { _hideFirstTerminal = false; frame.gameObject.SetActive(false); } // runs hidden: TERMS lists it, a tap shows it
            else frame.Open();
            OnFocusChanged(app != null ? app.Active : inst);
            RefreshTerminalsPanel();
        }

        private bool _hideFirstTerminal;

        /// Shows a hidden terminal where the user looks (and focuses it).
        public void ShowTerminal(TerminalInstance inst)
        {
            if (inst == null || !_frames.TryGetValue(inst, out var f)) return;
            if (!f.IsOpen) { PlaceDefault(f.Id, f); f.Open(); OnOpenedFloating(f); }
            app?.Focus(inst);
        }

        public void HideTerminal(TerminalInstance inst)
        {
            if (inst != null && _frames.TryGetValue(inst, out var f)) f.Close();
            RefreshTerminalsPanel();
        }

        /// ✕ on a terminal: ends it, except the last one, which keeps running hidden.
        private void CloseOrHideTerminal(TerminalInstance inst)
        {
            if (app == null || app.Terminals.Count <= 1) { HideTerminal(inst); Toast("The last terminal keeps running hidden · TERMS on the menu shows it", "info"); return; }
            app.CloseTerminal(inst);
        }

        /// A hidden terminal comes back; when all are shown, a new one opens (voice "show the terminal", the frame's actions).
        public void ShowOrNewTerminal()
        {
            if (app == null) return;
            var hidden = app.Terminals.FirstOrDefault(x => _frames.TryGetValue(x, out var f) && !f.IsOpen);
            if (hidden != null) ShowTerminal(hidden);
            else TerminalNew();
        }

        /// Self-tests: frame a terminal that no MetaCodexApp owns.
        public TerminalInstance FrameOwnerForTest(UiPanel frame) => _frames.FirstOrDefault(kv => kv.Value == frame).Key;

        public UiPanel WrapForTest(TerminalInstance inst) { _hideFirstTerminal = false; WrapTerminal(inst); return _frames.TryGetValue(inst, out var f) ? f : null; }

        private void OnTerminalClosed(TerminalInstance inst)
        {
            if (_frames.TryGetValue(inst, out var f)) { Router.Unregister(f); _frames.Remove(inst); }
            _linkButtons.Remove(inst); _links.Remove(inst); _linkVersion.Remove(inst);
            RefreshTerminalsPanel();
        }

        private void OnFocusChanged(TerminalInstance inst)
        {
            foreach (var kv in _frames) kv.Value.SetHighlight(kv.Key == inst ? 0.55f : 0f);
            RefreshTerminalsPanel();
        }

        // ------------------------------------------------------------------ gestures, selection

        private void OnGesture(GestureEvent e)
        {
            Scripts?.Dispatch(e);
            ApplyBindings(e);
        }

        private void OnSelected(UiPanel panel, Rect r)
        {
            LastSelection = (panel.Id, r);
            // the frame goes as the picture is taken: it is not left on the window, nor drawn into the capture (owner
            // 2026-11-09: "the orange section we drew doesn't disappear")
            Router.ClearSelection(panel);
            // one selection per SELECT: the tool never stays on by surprise
            Router.SelectionMode = false;
            if (_selectButton != null) _selectButton.SetSelected(false);
            // the area becomes a capture in the image clipboard (the window alone, opaque: nothing of the room)
            string what = $"Selected {r.width * 100f:F0}×{r.height * 100f:F0} cm on {panel.Title}";
            try
            {
                CapturePanelAreaAsync(panel, r, item => Toast(item != null
                    ? $"{what}: captured {item.w}×{item.h} to CLIPBOARD ({Clipboard.Items.Count}/{Clipboard.Max})"
                    : what + " (not captured) · select tool off", item != null ? "ok" : "warn"));
            }
            catch (Exception ex) { Log("capture: " + ex.Message); Toast(what + " · select tool off", "ok"); }
        }

        public (string panel, Rect rect) LastSelection { get; private set; }

        public void SetSelectionMode(bool on)
        {
            Router.SelectionMode = on;
            if (_selectButton != null) _selectButton.SetSelected(on);
            Toast(on ? "Select mode: pinch and drag on a panel" : "Select mode off", "info");
        }

        // ------------------------------------------------------------------ IScriptServices

        public void Toast(string text, string kind = "info")
        {
            Log("toast: " + text);
            if (Toasts != null) Toasts.Show(text, kind);
        }

        /// A dialog; asked from a panel's button it opens over that panel (UiModal: parallel to it, in front of it).
        public void Modal(string title, string text, string[] buttons, Action<int> onResult) =>
            UiModal.Show(Router, Head, title, text, buttons, onResult, _root, Router.ActivePanel);

        void IScriptServices.VoiceSay(string text) => VoiceSay(text);
        void IScriptServices.VoiceRun(string text) => VoiceProcessText(text, null);

        public void TerminalSend(string text) => app?.SendText(text);
        public void TerminalNew() => app?.NewTerminal();

        public void OpenKeyboard() => UiKeyboard.OpenForTerminal();

        /// "Show the keyboard" (voice): the system keyboard for the focused terminal; it stays when it is already up
        /// (never a toggle: said twice, it must not go away).
        public void ShowTerminalKeyboard() => app?.Input?.ShowSystemKeyboard();

        private bool _keyboardTipShown;

        /// The first time in a session the system keyboard opens: Horizon OS can lay it on a table (Settings >
        /// Experimental > Surface keyboard). Apps can neither place the system keyboard nor turn that mode on.
        private void KeyboardTip()
        {
            if (_keyboardTipShown || !Settings.surfaceKeyboardTip) return;
            _keyboardTipShown = true;
            KeyboardTipCountForTest++;
            Toast(KeyboardTipText, "info");
        }

        /// Self-tests: how many times the tip was shown.
        public int KeyboardTipCountForTest { get; private set; }

        public const string KeyboardTipText = "Tip: the headset can lay this keyboard on a table. Settings > Experimental > Surface keyboard";

        /// Self-tests: the tip as at the start of a session.
        public void ResetKeyboardTipForTest() => _keyboardTipShown = false;


        public string GetSetting(string key) => Settings.Get(key);

        public bool SetSetting(string key, string value)
        {
            if (!Settings.Set(key, value)) return false;
            OnSettingsChanged(rebuild: key == "accent");
            return true;
        }

        public HandState Hand(string hand) => Gestures?.Get(hand == "left" ? Handedness.Left : Handedness.Right);

        public void Log(string line)
        {
            LogLines.Add(line);
            while (LogLines.Count > 200) LogLines.RemoveAt(0);
            MetaCodex.App.SessionLog.Log(line);
        }

        // ------------------------------------------------------------------ settings

        public void OnSettingsChanged(bool rebuild = false)
        {
            Settings.Save(_settingsPath);
            Router.Settings = Settings.gestures;
            Gestures.Settings = Settings.gestures;
            Mqh.BaseUrl = Settings.mqhUrl;
            Mqh.Token = MqhTokenNow();
            ApplyMaterialSettings();
            foreach (var p in _panels.Values) p.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            ApplyMainMenuMode();
            ApplyOrbSetting();
            // FIXED: the frame loop bends panels only while "Bend panels" is on, so turning it off any way (voice, a
            // script, MQH's settings, not only DISPLAY's toggle) must flatten them here, open and closed ones
            UpdateFixedBend();
            if (rebuild) _rebuildPending = true; // next frame: never destroy panels inside the click that asked for it
            // sliders call this on every step: push to MQH once things are quiet for a moment
            if (Settings.mqhSync) _settingsPushDue = _clock.Elapsed.TotalSeconds + 1.5;
        }

        private bool _rebuildPending;

        /// Runs a requested panel rebuild (accent, theme); Update calls it, tests may too.
        public void FlushPendingRebuild()
        {
            if (!_rebuildPending) return;
            _rebuildPending = false;
            RebuildPanels();
        }

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _settingsPushDue = -1;

        private void PushSettingsWhenQuiet()
        {
            if (_settingsPushDue < 0 || _clock.Elapsed.TotalSeconds < _settingsPushDue) return;
            _settingsPushDue = -1;
            if (Settings.mqhSync) Mqh.PushQuestSettings(Settings.ToJObject(), ok => { if (ok) _remoteRevision = -2; });
        }

        /// Agents can change quest.settings in MQH; poll and apply (keeps local edits when MQH is down).
        private bool _localSettingsWin;

        private void PullRemoteSettings()
        {
            if (_localSettingsWin)
            {
                _localSettingsWin = false;
                Mqh.PushQuestSettings(Settings.ToJObject(), ok => { if (ok) _remoteRevision = -2; });
                return;
            }
            Mqh.Preferences(prefs =>
            {
                var entry = prefs?["preferences"]?["quest.settings"];
                if (entry == null) return;
                int rev = entry["revision"]?.Value<int>() ?? 0;
                if (_remoteRevision == -2) { _remoteRevision = rev; return; } // our own write
                if (rev == _remoteRevision) return;
                bool first = _remoteRevision == -1;
                _remoteRevision = rev;
                var value = entry["value"] as Newtonsoft.Json.Linq.JObject;
                if (value == null || value.Count == 0) return;
                if (first && rev == 0) return;
                try
                {
                    string before = JsonUtility.ToJson(Settings), accent = Settings.accent;
                    JsonUtility.FromJsonOverwrite(value.ToString(), Settings);
                    if (JsonUtility.ToJson(Settings) != before)
                    {
                        // applied in place: the controls show the new values. Rebuilding every panel (as before 0.7.19)
                        // froze the frame loop for a moment, often a few seconds after start (owner: "stuttering in the
                        // first 10 s"); only an accent change, baked into the shapes' tints, still needs it
                        OnSettingsChanged(Settings.accent != accent);
                        _settingsPushDue = -1; // MQH has these already
                        SyncSettingsControls();
                        Toast("Settings updated from MQH", "info");
                    }
                }
                catch (Exception ex) { Log("quest.settings from MQH rejected: " + ex.Message); }
            });
        }

        // ------------------------------------------------------------------ control port: ui / script / gesture

        public string ControlCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "ui":
                {
                    var parts = arg.Split(new[] { ' ' }, 3);
                    string sub = parts.Length > 0 ? parts[0] : "";
                    string id = parts.Length > 1 ? parts[1] : "";
                    switch (sub)
                    {
                        case "list": return string.Join("\n", _panels.Select(kv => $"{kv.Key} open={kv.Value.IsOpen} elements={kv.Value.Elements.Count}"));
                        case "open": return OpenPanel(id) ? "ok" : "error: no panel " + id;
                        case "close": return ClosePanel(id) ? "ok" : "error: no panel " + id;
                        case "click":
                        {
                            if (!_panels.TryGetValue(id, out var p)) return "error: no panel " + id;
                            string label = parts.Length > 2 ? parts[2] : "";
                            var el = p.Elements.OfType<UiButton>().FirstOrDefault(b => string.Equals(b.Text, label, StringComparison.OrdinalIgnoreCase));
                            if (el == null) return "error: no button '" + label + "' on " + id;
                            el.Click();
                            return "ok";
                        }
                        case "toast": Toast(arg.Length > 6 ? arg.Substring(6) : "", "info"); return "ok";
                        case "select": SetSelectionMode(id != "off"); return "ok";
                        case "layout": SetLayout(id); return "ok, layout=" + Settings.layout;
                        case "spin": Spin(float.TryParse(id, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var deg) ? deg : 30f, 0f); return "ok, spin=" + CarouselSpin.ToString("F1");
                        default: return "error: ui list | open <id> | close <id> | click <id> <button> | toast <text> | select on|off | layout fixed|float | spin <deg>";
                    }
                }
                case "orb":
                {
                    // the orb (ShellOrb): orb on | off | menu | place | status
                    if (Orb == null) return "error: no orb";
                    switch ((arg ?? "").Trim().ToLowerInvariant())
                    {
                        case "on": SetOrb(true); return "ok";
                        case "off": SetOrb(false); return "ok";
                        case "menu": ToggleOrbMenu(); return "ok, menu " + (OrbMenuOpen ? "open" : "closed");
                        case "place": ResetOrbPlace(); return "ok";
                        case "": case "status":
                            return $"orb {(Settings.orb ? "on" : "off")} mood={Orb.State} at={Orb.Position.x:F2},{Orb.Position.y:F2},{Orb.Position.z:F2} pinned={Settings.orbPinned} messages={Toasts.History.Count}";
                        default: return "error: orb on | off | menu | place | status";
                    }
                }
                case "script":
                {
                    var parts = arg.Split(new[] { ' ' }, 2);
                    string sub = parts.Length > 0 ? parts[0] : "";
                    string rest = parts.Length > 1 ? parts[1] : "";
                    switch (sub)
                    {
                        case "list": return string.Join("\n", Scripts.Entries.Select(e => $"{e.Name} enabled={e.Enabled} handlers={e.Handlers} error={e.Error ?? "-"}"));
                        case "reload": Scripts.ReloadAll(); return "ok";
                        case "eval": return Scripts.Eval(ControlServer.Unescape(rest));
                        case "log":
                        {
                            var e = Scripts.Entries.FirstOrDefault(x => x.Name == rest.Trim());
                            return e == null ? "error: no script " + rest : string.Join("\n", e.Log);
                        }
                        case "dir": return Scripts.Dir;
                        default: return "error: script list | reload | eval <lua> | log <name> | dir";
                    }
                }
                case "gesture":
                {
                    var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string sub = parts.Length > 0 ? parts[0] : "clap";
                    switch (sub)
                    {
                        case "list":
                            return string.Join("\n", GestureEngine.Names.Select(n => $"{n} built-in") .Concat(Gestures.Custom.Select(c => $"{c.Name} {c.Kind} {c.Owner} active={Gestures.IsActive(c.Name)}")));
                        case "bindings":
                            return string.Join("\n", Settings.bindings.Select(b => $"{b.gesture} {b.hand} {b.phase} -> {b.action} {(b.enabled ? "on" : "off")}"));
                        case "bind":
                        {
                            // gesture bind <gesture> <action...>
                            if (parts.Length < 3) return "error: gesture bind <gesture> <action>";
                            AddBinding(parts[1], string.Join(" ", parts.Skip(2)));
                            return "ok";
                        }
                        case "unbind":
                        {
                            if (parts.Length < 2) return "error: gesture unbind <gesture>";
                            int n = Settings.bindings.RemoveAll(b => b.gesture == parts[1]);
                            OnSettingsChanged(); RefreshGesturesPanel();
                            return "ok, removed " + n;
                        }
                        case "record":
                        {
                            // gesture record left|right <name>: the hand's pose now (tests: after `hand pose`)
                            if (parts.Length < 3) return "error: gesture record left|right <name>";
                            var p = RecordPoseNow(parts[1] == "left" ? Handedness.Left : Handedness.Right, parts[2]);
                            return p == null ? "error: hand not tracked" : "ok " + p.Summary();
                        }
                        case "tick":
                        {
                            // gesture tick [seconds]: advance the engine (Editor tests without a frame loop)
                            float secs = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : 0.1f;
                            TickGesturesForTest(secs);
                            return "ok";
                        }
                        default:
                        {
                            // simulate a recognised gesture (tests scripts without hands): gesture clap | gesture pinch right start
                            var e = new GestureEvent
                            {
                                Name = sub,
                                Hand = parts.Length > 1 ? parts[1] : "both",
                                Phase = parts.Length > 2 ? parts[2] : "tap",
                                Time = Time.unscaledTime,
                            };
                            OnGesture(e);
                            return "ok";
                        }
                    }
                }
                case "hand":
                {
                    // hand pose left|right T I M R L [thumbDir] | hand hide left|right  (simulated hands only)
                    if (SimHands == null) return "error: real hands are tracked; simulated poses work in the Editor";
                    var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == "hide") { SimHands.Hide(parts[1] == "left" ? Handedness.Left : Handedness.Right); return "ok"; }
                    if (parts.Length < 7 || parts[0] != "pose") return "error: hand pose left|right thumb index middle ring little [up|down|left|right|forward|back] | hand hide left|right";
                    var h = parts[1] == "left" ? Handedness.Left : Handedness.Right;
                    float F(int i) => float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
                    Vector3? dir = parts.Length > 7 ? Gestures.Direction(parts[7], Vector3.zero) : (Vector3?)null;
                    PoseHand(h, F(2), F(3), F(4), F(5), F(6), dir);
                    return "ok";
                }
                case "action": { var err = RunAction(arg); return err == null ? "ok" : "error: " + err; }
                case "voice": return VoiceControl(arg);
                case "view": return ViewCommand(arg);
                case "system": return SystemCommand(arg);
                case "capture": return CaptureCommand(arg);
                case "plugin":
                {
                    var parts = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    string sub = parts.Length > 0 ? parts[0] : "list";
                    string id = parts.Length > 1 ? parts[1].Trim() : "";
                    switch (sub)
                    {
                        case "list": return string.Join("\n", Scripts.Entries.Where(e => e.Kind == "plugin").Select(e => $"{e.Name} v{e.Version} enabled={e.Enabled} poses={e.Poses} detectors={e.Detectors} handlers={e.Handlers} error={e.Error ?? "-"}"));
                        case "new": { var e = NewPlugin(id.Length > 0 ? id : "my-gestures"); return "ok, /mnt/quest/plugins/" + e.Name; }
                        case "examples": return string.Join("\n", GestureExampleIds.Select(x => $"{x} installed={Scripts.Find("plugin/" + x) != null} gestures={string.Join(",", GestureNamesIn(ExamplePlugins[x]))}"));
                        case "example": { var err = InstallGestureExample(id); return err == null ? "ok, /mnt/quest/plugins/" + id.Trim().ToLowerInvariant() : "error: " + err; }
                        case "enable": Scripts.SetEnabled("plugin/" + id, true); return "ok";
                        case "disable": Scripts.SetEnabled("plugin/" + id, false); return "ok";
                        case "reload": Scripts.ReloadAll(); return "ok";
                        case "log": { var e = Scripts.Find("plugin/" + id); return e == null ? "error: no plugin " + id : string.Join("\n", e.Log); }
                        case "delete": return Scripts.Delete("plugin/" + id) ? "ok" : "error: no plugin " + id;
                        case "dir": return Scripts.PluginsDir;
                        default: return "error: plugin list | new <id> | examples | example <id> | enable <id> | disable <id> | reload | log <id> | delete <id> | dir";
                    }
                }
                default: return null;
            }
        }
    }

    public sealed partial class MetaCodexShell
    {
        /// Simulated hands: a pose in front of the head (palm facing the other hand's side, fingers forward).
        public void PoseHand(Handedness h, float thumb, float index, float middle, float ring, float little, Vector3? thumbDir = null)
        {
            if (SimHands == null) return;
            var head = Gestures.Head;
            Gestures.HeadFrame(out var right, out var up, out var fwd);
            float side = h == Handedness.Left ? -1f : 1f;
            var palm = head.position + fwd * 0.35f + right * (0.15f * side) - up * 0.25f;
            SimHands.SetPose(h, palm, fwd, -right * side, thumb, index, middle, ring, little, thumbDir);
        }

        /// Editor tests: advance the gesture engine (and Lua detectors) without a frame loop.
        public void TickGesturesForTest(float seconds, float step = 1f / 36f)
        {
            float t0 = _testClock;
            for (float t = step; t <= seconds + 1e-4f; t += step) { _testClock = t0 + t; Gestures.Tick(_testClock, step); TickBindings(_testClock); }
        }

        private float _testClock = 1000f;

        /// Editor tests: run queued main-thread callbacks (MQH replies) until no request is in flight and the
        /// queue stayed empty for a moment (callbacks may start new requests), or `seconds` passed.
        public void PumpForTest(float seconds = 3f)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int idle = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                bool any = false;
                while (_main.TryDequeue(out var a)) { any = true; try { a(); } catch (Exception ex) { Log("callback failed: " + ex.Message); } }
                FlushPendingRebuild();
                RunLater();
                PushSettingsWhenQuiet();
                idle = any || Mqh.InFlight > 0 || (Qvac != null && Qvac.Client != null && Qvac.Client.InFlight > 0) ? 0 : idle + 1;
                if (idle >= 10) break; // 200 ms with nothing in flight and nothing queued
                System.Threading.Thread.Sleep(20);
            }
        }
    }

    /// An invisible clickable area (the terminal surface: a tap focuses that terminal).
    public sealed class UiHitArea : UiElement
    {
        private readonly Action _onClick;
        public override bool Interactive => true;
        public UiHitArea(Action onClick) { _onClick = onClick; }
        protected override void Build() { }
        public override void OnClick(UiPointerEvent e) => _onClick?.Invoke();
    }
}
