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
        public GitService Git { get; private set; }
        public FileService Files { get; private set; }
        public UiToasts Toasts { get; private set; }
        public SimulatedHands SimHands { get; private set; }
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
            Gestures.OnGesture += OnGesture;
            Router.Gestures = Gestures;
            Router.Selected += OnSelected;

            Toasts = new GameObject("Toasts").AddComponent<UiToasts>();
            Toasts.transform.SetParent(_root, false);
            Toasts.Head = Head;

            Mqh = new MqhClient(Settings.mqhUrl, Settings.mqhToken, _main);
            var host = app != null ? app.Host : null;
            Git = new GitService(host);
            Files = new FileService(host, Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "../..")) : null);

            string dir = scriptsDir ?? DefaultScriptsDir(host);
            Scripts = new ScriptHost(this, dir);
            Scripts.Changed += () => RefreshScriptsPanel();
            UiKeyboard.TerminalKeyboardRequested += () => app?.Input?.ToggleSystemKeyboard();

            BuildPanels();
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

        private void ApplyMaterialSettings()
        {
            if (shapeMaterial != null) shapeMaterial.SetFloat("_Scanlines", Settings.scanlines ? 1f : 0f);
        }

        private void Update()
        {
            while (_main.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { Log("main-thread callback failed: " + ex.Message); } }
            Scripts?.Tick(Time.unscaledTime);
            if (Settings != null && Settings.mqhSync && Time.unscaledTime >= _nextMqhPoll) { _nextMqhPoll = Time.unscaledTime + 5f; PullRemoteSettings(); }
            UpdateLayout();
        }

        // ------------------------------------------------------------------ panels

        private T Panel<T>(string id, string title, float w, float h) where T : UiPanel
        {
            var p = UiPanel.Create<T>(id, title, w, h, _root);
            _panels[id] = p;
            Router.Register(p);
            p.Closed += _ => SaveLayout();
            p.gameObject.SetActive(false);
            return p;
        }

        public IEnumerable<string> PanelIds => _panels.Keys;

        public bool OpenPanel(string id)
        {
            if (!_panels.TryGetValue(id ?? "", out var p)) return false;
            if (!p.IsOpen)
            {
                if (!_placed.Contains(id) || Head != null && Vector3.Distance(p.transform.position, Head.position) > 3f) PlaceDefault(id, p);
                p.Open();
                _placed.Add(id);
            }
            else p.Open(); // re-plays the open glow so it is easy to spot
            OnOpenedFloating(p);
            OnPanelOpened(id);
            return true;
        }

        public bool ClosePanel(string id) { if (!_panels.TryGetValue(id ?? "", out var p)) return false; p.Close(); return true; }

        public bool TogglePanel(string id)
        {
            if (!_panels.TryGetValue(id ?? "", out var p)) return false;
            if (p.IsOpen) p.Close(); else OpenPanel(id);
            return true;
        }

        private readonly HashSet<string> _placed = new HashSet<string>();

        /// Opens where the user looks: level, at the panel distance; if that direction is taken by another open
        /// panel (or a terminal), steps sideways (right, left, right, ...) to the nearest free angle.
        private void PlaceDefault(string id, UiPanel p)
        {
            var head = Head;
            if (id == "dock") { p.PlaceInFront(head, 0.55f, 0.42f); TiltTowards(p, head); return; }
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            var f = head != null ? head.forward : Vector3.forward;
            float yaw0 = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float dist = Settings.panelDistance * 0.95f;
            float Half(UiPanel q, float d) => Mathf.Atan2(q.Width * 0.5f * q.transform.lossyScale.x, d) * Mathf.Rad2Deg;
            var others = _panels.Values.Where(q => q != p && q.IsOpen && q.Id != "dock").Concat(_frames.Values.Where(q => q.IsOpen)).ToList();
            float mine = Half(p, dist);
            float chosen = yaw0;
            for (int k = 0; k < 12; k++)
            {
                float step = (k + 1) / 2 * (mine * 2f + 4f) * (k % 2 == 1 ? 1f : -1f);
                float yaw = yaw0 + (k == 0 ? 0f : step);
                bool free = true;
                foreach (var q in others)
                {
                    var d = q.transform.position - eye;
                    float qyaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    float qd = new Vector2(d.x, d.z).magnitude;
                    if (Mathf.Abs(Mathf.DeltaAngle(qyaw, yaw)) < mine + Half(q, Mathf.Max(qd, 0.1f)) + 2f) { free = false; break; }
                }
                if (free) { chosen = yaw; break; }
            }
            float a = chosen * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            p.transform.SetPositionAndRotation(eye + dir * dist + Vector3.down * 0.06f, Quaternion.LookRotation(dir, Vector3.up));
        }

        private static void TiltTowards(UiPanel p, Transform head)
        {
            if (head == null) return;
            var dir = p.transform.position - head.position;
            p.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        private void OnRecentered(Vector3 center, Quaternion rot)
        {
            foreach (var kv in _panels)
            {
                if (kv.Key == "dock") { PlaceDefault("dock", kv.Value); if (!kv.Value.IsOpen) kv.Value.Open(); continue; }
                if (kv.Value.IsOpen) PlaceDefault(kv.Key, kv.Value);
            }
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
            frame.Add(new UiButton("✕", () => app?.CloseTerminal(inst), UiButtonStyle.Ghost) { Rect = new Rect(bx, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("+", () => app?.NewTerminal(), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.042f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            frame.Add(new UiButton("⌨", () => app.Input?.ToggleSystemKeyboard(), UiButtonStyle.Ghost) { Rect = new Rect(bx - 0.084f, 0.006f, 0.036f, t.HeaderHeight - 0.010f) });
            // a tap on the terminal area focuses it
            frame.Add(new UiHitArea(() => app?.Focus(inst)) { Rect = new Rect(bezel, t.HeaderHeight + bezel, v.Width, v.Height) });
            _frames[inst] = frame;
            Router.Register(frame);
            frame.Open();
            OnFocusChanged(app != null ? app.Active : inst);
            RefreshTerminalsPanel();
        }

        /// Self-tests: frame a terminal that no MetaCodexApp owns.
        public UiPanel WrapForTest(TerminalInstance inst) { WrapTerminal(inst); return _frames.TryGetValue(inst, out var f) ? f : null; }

        private void OnTerminalClosed(TerminalInstance inst)
        {
            if (_frames.TryGetValue(inst, out var f)) { Router.Unregister(f); _frames.Remove(inst); }
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
            if (_gestureMonitor != null) RefreshGestureMonitor();
        }

        private void OnSelected(UiPanel panel, Rect r)
        {
            Toast($"Selected {r.width * 100f:F0}×{r.height * 100f:F0} cm on {panel.Title}", "ok");
            LastSelection = (panel.Id, r);
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

        public void Modal(string title, string text, string[] buttons, Action<int> onResult) =>
            UiModal.Show(Router, Head, title, text, buttons, onResult, _root);

        public void TerminalSend(string text) => app?.SendText(text);
        public void TerminalNew() => app?.NewTerminal();

        public void OpenBrowser(string url)
        {
            bool ok = SystemBridge.OpenBrowser(url);
            Toast(ok ? "Opening " + url : SystemBridge.LastResult, ok ? "info" : "warn");
        }

        public void OpenKeyboard() => UiKeyboard.OpenForTerminal();

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
            Mqh.Token = Settings.mqhToken;
            ApplyMaterialSettings();
            foreach (var p in _panels.Values) p.transform.localScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.6f);
            if (rebuild) RebuildPanels();
            if (Settings.mqhSync) Mqh.PushQuestSettings(Settings.ToJObject(), ok => { if (ok) _remoteRevision = -2; });
        }

        /// Agents can change quest.settings in MQH; poll and apply (keeps local edits when MQH is down).
        private void PullRemoteSettings()
        {
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
                    string before = JsonUtility.ToJson(Settings);
                    JsonUtility.FromJsonOverwrite(value.ToString(), Settings);
                    if (JsonUtility.ToJson(Settings) != before)
                    {
                        Settings.Save(_settingsPath);
                        Router.Settings = Settings.gestures;
                        Gestures.Settings = Settings.gestures;
                        RebuildPanels();
                        Toast("Settings updated from MQH", "info");
                    }
                }
                catch (Exception ex) { Log("quest.settings from MQH rejected: " + ex.Message); }
            });
        }

        // ------------------------------------------------------------------ control port: ui / script / gesture

        private string ControlCommand(string cmd, string arg)
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
                    // simulate a recognised gesture (tests scripts without hands): gesture clap | gesture pinch right start
                    var parts = arg.Split(' ');
                    var e = new GestureEvent
                    {
                        Name = parts.Length > 0 ? parts[0] : "clap",
                        Hand = parts.Length > 1 ? parts[1] : "both",
                        Phase = parts.Length > 2 ? parts[2] : "tap",
                        Time = Time.unscaledTime,
                    };
                    OnGesture(e);
                    return "ok";
                }
                default: return null;
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
