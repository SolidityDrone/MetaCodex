using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MetaCodex.Linux;
using MetaCodex.Terminal;
using TMPro;
using UnityEngine;

namespace MetaCodex.App
{
    /// One terminal: emulator, view, shell session, and (when the shell UI is up) its frame panel.
    public sealed class TerminalInstance
    {
        public int Number;
        public string Name => string.IsNullOrEmpty(Terminal?.Title) ? $"terminal {Number}" : Terminal.Title;
        public VtTerminal Terminal;
        public TerminalView View;
        public ITerminalSession Session;
        /// The frame (a UiPanel) the shell puts around the view; null until the shell wraps it.
        public Transform Frame;
        internal byte[] Carry;
        internal bool WaitingRestart;
        internal int ClipSeen; // the last OSC 52 copy taken (VtTerminal.ClipboardVersion)
    }

    /// Milestone 1, "the terminal": Linux shells running on the headset, shown and typed into from VR.
    /// Installs/starts the userland (LinuxHost), keeps one or more terminal instances (one is active: the
    /// keyboard and the key bar type into it), pumps pty output into each emulator, serves the control port.
    /// In the Editor (no Android) it connects to tools/ptyserver.py on this PC, or falls back to echo.
    public sealed class MetaCodexApp : MonoBehaviour
    {
        [SerializeField] private LinuxHost host;
        [SerializeField] private TerminalView view;
        [SerializeField] private TerminalInput input;
        [SerializeField] private KeyBar keyBar;
        [SerializeField] private HandTips hands;
        [SerializeField] private TextMeshPro statusText;
        [SerializeField] private Transform head;
        [SerializeField] private int rows = 30;
        [SerializeField] private int cols = 96;
        [SerializeField] private float distance = 0.85f;
        [SerializeField] private float dropBelowEyes = 0.12f;
        [SerializeField] private bool controlPort = true;
        [SerializeField] private string bridgeHost = "127.0.0.1";
        [SerializeField] private int bridgePort = 47900;
        [Tooltip("Max pty bytes parsed per frame (shared by all terminals); the rest waits for the next frame")]
        [SerializeField] private int feedBudget = 256 * 1024;

        public LinuxHost Host => host;
        public Transform Head => head;
        public TerminalInput Input => input;
        public KeyBar KeyBar => keyBar;
        public TextMeshPro StatusText => statusText;
        public IReadOnlyList<TerminalInstance> Terminals => _terms;
        public TerminalInstance Active => _terms.Count == 0 ? null : _terms[Mathf.Clamp(_active, 0, _terms.Count - 1)];
        public VtTerminal Terminal => Active?.Terminal;
        public ITerminalSession Session => Active?.Session;
        public float Fps => _fps;
        /// Raised for every new terminal (the shell wraps it into a frame panel) and on focus changes.
        public event Action<TerminalInstance> TerminalCreated;
        /// The shell keeps the first terminal hidden at start (Settings.terminalAtStart off): its bare view (with the key
        /// bar and status line under it) stays off until the shell wraps it in its frame.
        public bool StartHidden;
        public event Action<TerminalInstance> TerminalClosed;
        public event Action<TerminalInstance> FocusChanged;
        /// A program in a terminal put text on the clipboard (OSC 52: vim, tmux, a CLI's "copy"); the shell copies it (the
        /// system clipboard and CLIPBOARD) and says so. Without a listener the text goes straight to the system clipboard.
        public event Action<TerminalInstance, string> ProgramCopied;
        /// Extra control-port commands (the shell adds ui/script/gesture commands).
        public Func<string, string, string> ExtraCommand;
        /// Control-port commands answered later (secret approvals): (line, reply) -> handled.
        public Func<string, Action<string>, bool> AsyncCommand;

        private readonly List<TerminalInstance> _terms = new List<TerminalInstance>();
        private int _active;
        private int _numbers;
        private ControlServer _control;
        private bool _placed;
        private float _fpsTime;
        private int _fpsFrames;
        private float _fps;

        private IEnumerator Start()
        {
            SessionLog.Init();
            SessionLog.Log($"MetaCodex {Application.version} start ({SystemInfo.deviceModel}, {SystemInfo.operatingSystem})");
            host.Log += SessionLog.Log;
            var main = new TerminalInstance { Number = ++_numbers, Terminal = new VtTerminal(rows, cols, 5000), View = view };
            view.Attach(main.Terminal);
            _terms.Add(main);
            keyBar.Build(view.Width, view.Font);
            keyBar.Input = input;
            keyBar.Hands = hands;
            keyBar.Action += OnKeyBarAction;
            input.Send += SendToSession;
            input.ScrollRequest += lines => Active?.View.ScrollBy(lines);
            input.AppCursorKeys = () => Terminal != null && Terminal.AppCursorKeys;
            input.BracketedPaste = () => Terminal != null && Terminal.BracketedPaste;
            if (statusText != null) statusText.font = view.Font;
            TerminalCreated?.Invoke(main);
            AttachKeyBar(main);
            if (StartHidden && main.Frame == null) view.gameObject.SetActive(false); // the shell shows it in its frame, or keeps it hidden

            if (controlPort)
            {
                try { _control = new ControlServer(ControlServer.DefaultPort, HandleCommand) { AsyncHandler = (line, reply) => AsyncCommand != null && AsyncCommand(line, reply) }; SessionLog.Log($"control port 127.0.0.1:{_control.Port}"); }
                catch (Exception ex) { SessionLog.Log("control port off: " + ex.Message); }
            }

            // the Linux starts preparing (on a first start: its copy and unpack, the longest step of the setup) at once, not
            // after the head is tracked and the terminal placed: it needs neither (2026-11-15 log: the unpack began 5 s
            // after the scene, behind the shell's 5.4 s start frame)
            var prepare = StartCoroutine(host.Prepare());
            yield return PlaceWhenTracked();
            yield return prepare;
            StartSession(main);
        }

        // ------------------------------------------------------------------ terminals

        /// A new terminal with its own shell. The shell frames it and places the frame where no window is (MetaCodexShell
        /// OpenTerminalFrame); without the shell (or before it is up) its bare view goes right of the active one.
        public TerminalInstance NewTerminal()
        {
            var go = new GameObject("Terminal " + (_numbers + 1));
            go.transform.SetParent(view.transform.parent, false);
            var v = go.AddComponent<TerminalView>();
            v.CopyConfigFrom(view);
            var inst = new TerminalInstance { Number = ++_numbers, Terminal = new VtTerminal(rows, cols, 5000), View = v };
            v.Attach(inst.Terminal);
            _terms.Add(inst);
            TerminalCreated?.Invoke(inst);
            if (inst.Frame == null)
            {
                var a = Active.View.transform;
                go.transform.SetPositionAndRotation(a.position + a.right * (view.Width + 0.06f), a.rotation);
            }
            StartSession(inst);
            Focus(inst);
            return inst;
        }

        public void Focus(TerminalInstance inst)
        {
            int i = _terms.IndexOf(inst);
            if (i < 0) return;
            _active = i;
            AttachKeyBar(inst);
            FocusChanged?.Invoke(inst);
        }

        public void CloseTerminal(TerminalInstance inst)
        {
            int i = _terms.IndexOf(inst);
            if (i < 0) return;
            if (_terms.Count == 1) { inst.Session?.Dispose(); inst.Session = null; StartSession(inst); return; } // the last one restarts instead
            inst.Session?.Dispose();
            _terms.RemoveAt(i);
            if (keyBar.transform.IsChildOf(inst.Frame != null ? inst.Frame : inst.View.transform)) keyBar.transform.SetParent(transform, false);
            if (statusText != null && statusText.transform.IsChildOf(inst.Frame != null ? inst.Frame : inst.View.transform)) statusText.transform.SetParent(transform, false);
            TerminalClosed?.Invoke(inst);
            if (inst.View != view) Destroy(inst.Frame != null ? inst.Frame.gameObject : inst.View.gameObject);
            else (inst.Frame != null ? inst.Frame.gameObject : inst.View.gameObject).SetActive(false);
            _active = Mathf.Clamp(_active >= i ? _active - 1 : _active, 0, _terms.Count - 1);
            Focus(Active);
        }

        /// The key bar and the status line sit under / over the active terminal.
        private void AttachKeyBar(TerminalInstance inst)
        {
            var parent = inst.View.transform;
            keyBar.transform.SetParent(parent, false);
            keyBar.transform.localPosition = new Vector3(0f, -inst.View.Height - 0.012f, 0f);
            keyBar.transform.localRotation = Quaternion.identity;
            if (statusText != null)
            {
                statusText.transform.SetParent(parent, false);
                statusText.transform.localPosition = new Vector3(0f, 0.006f, 0f);
                statusText.transform.localRotation = Quaternion.identity;
            }
        }

        private void StartSession(TerminalInstance inst)
        {
            inst.Session?.Dispose();
            inst.Session = null;
            inst.WaitingRestart = false;
            inst.Carry = null;
            try
            {
                if (host.State == LinuxHost.HostState.Ready)
                {
                    inst.Session = host.StartShell(rows, cols);
                }
                else if (host.State == LinuxHost.HostState.Unsupported)
                {
                    try { inst.Session = new TcpTerminalSession(bridgeHost, bridgePort, rows, cols); SessionLog.Log("terminal: bridge " + inst.Session.Description); }
                    catch (Exception) { inst.Session = new EchoSession("No Linux here (Editor) and no tools/ptyserver.py bridge on port " + bridgePort + ": echo mode."); }
                }
                else
                {
                    inst.Terminal.Feed("\r\n\x1b[31m" + host.Status + "\x1b[0m\r\n");
                }
            }
            catch (Exception ex)
            {
                SessionLog.Log("shell start failed: " + ex);
                inst.Terminal.Feed("\r\n\x1b[31mshell start failed: " + ex.Message + "\x1b[0m\r\n");
            }
        }

        // ---- the head pose at the start (owner 2026-11-16: "the splash screen and stuff spawned on the ground: I was sitting
        // at the desk and it spawned at my feet"). The rig (OVRCameraRig, Floor Level) sits at the world origin; until the
        // runtime gives the head its first pose the eye anchor stays at the rig's origin, which is the floor. A place taken
        // from that pose lies on the floor, about where the user sits. The start waits for a valid pose instead
        // (HeadPoseValid, twice in a row), at most StartPoseWaitSeconds of wall time, and then uses the default eye height.

        /// How long the start waits for a valid head pose (wall time) before it places at the default eye height.
        public const float StartPoseWaitSeconds = 5f;
        /// While the start waits, a pose with the eyes lower than this over the floor origin is not trusted yet (the first
        /// poses of the runtime's start may still be in a space whose origin is at the eyes); after the wait any tracked
        /// height goes (a user lying down).
        public const float MinEyeHeight = 0.4f;
        /// The eye height over the floor origin used when the head never got a pose (a seated user).
        public const float DefaultEyeHeight = 1.2f;

        /// The camera rig gave the head a pose at least once: until then the eye anchor sits at the rig's origin (the floor of
        /// the Floor Level tracking space). Self-tests place their head away from its parent's origin.
        public static bool HeadHasPose(Transform h) => h != null && h.localPosition != Vector3.zero;

        /// The head pose can place things: it has a pose, and on the headset the runtime says the headset is present and its
        /// position tracked, and the eyes are at least MinEyeHeight over the floor origin (unless `anyHeight`). In the Editor
        /// and batch runs (no headset): any head with a pose.
        public static bool HeadPoseValid(Transform h, bool anyHeight = false)
        {
            if (!HeadHasPose(h)) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (!OVRManager.isHmdPresent || !OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head)) return false;
            }
            catch (Exception) { } // no answer from the plugin: the rig's pose stands
            if (!anyHeight && h.localPosition.y < MinEyeHeight) return false;
#endif
            return true;
        }

        /// Where the eyes are for a placement: the head, or, when it never got a pose, DefaultEyeHeight over the rig's origin.
        public static Vector3 EyeOrDefault(Transform h)
        {
            if (h == null) return new Vector3(0f, DefaultEyeHeight, 0f);
            return HeadHasPose(h) ? h.position : h.position + Vector3.up * DefaultEyeHeight;
        }

        /// The tracking origin the runtime uses now, for the start's log lines ("Floor", "Device"...; "?" when unknown).
        public static string TrackingOriginText()
        {
            try
            {
                var list = new List<UnityEngine.XR.XRInputSubsystem>();
                SubsystemManager.GetSubsystems(list);
                foreach (var s in list) if (s != null && s.running) return s.GetTrackingOriginMode().ToString();
            }
            catch (Exception) { }
            return "?";
        }

        /// The main terminal was placed (the start's first Recenter ran).
        public bool Placed => _placed;

        private IEnumerator PlaceWhenTracked()
        {
            float t0 = Time.realtimeSinceStartup;
            int valid = 0;
            while (true)
            {
                valid = HeadPoseValid(head) ? valid + 1 : 0;
                if (valid >= 2 || Time.realtimeSinceStartup - t0 >= StartPoseWaitSeconds) break;
                yield return null;
            }
            bool tracked = HeadPoseValid(head, true);
            Recenter();
            SessionLog.Log($"start: terminal placed at eye y={EyeOrDefault(head).y:F2} (head {(tracked ? "tracked" : HeadHasPose(head) ? "NOT tracked, its last pose" : "NOT tracked, default height")} after {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms)");
        }

        /// Raised by Recenter; the shell repositions its panels (or the carousel) around the new pose.
        public event Action<Vector3, Quaternion> Recentered;
        /// The main window's place just before the recenter and its frame: the shell turns every window round the user by
        /// the angle the main window turned (ShellPlacement.RecenterKeepLayout), so the layout keeps its shape.
        public Vector3? RecenterFrom;
        public Transform RecenterMain;

        /// Puts the main terminal in front of where the user looks now: level, slightly below the eyes (the default eye
        /// height when the head never got a pose).
        public void Recenter()
        {
            Vector3 eye = EyeOrDefault(head);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var rot = Quaternion.LookRotation(fwd, Vector3.up); // panel +z points away from the user
            Vector3 center = eye + fwd * distance + Vector3.down * dropBelowEyes;
            var main = _terms.Count > 0 ? _terms[0] : null;
            RecenterFrom = main != null && main.Frame != null ? main.Frame.position : (Vector3?)null;
            RecenterMain = main != null ? main.Frame : null;
            if (main != null && main.Frame != null) main.Frame.SetPositionAndRotation(center, rot);
            else
            {
                Vector3 topLeft = center - rot * new Vector3(view.Width / 2f, -view.Height / 2f, 0f);
                var root = view.transform.parent != null ? view.transform.parent : view.transform;
                root.SetPositionAndRotation(topLeft, rot);
            }
            _placed = true;
            SessionLog.Log($"terminal placed at {center} ({view.Width:F2} x {view.Height:F2} m)");
            Recentered?.Invoke(center, rot);
        }

        public void SendToSession(byte[] data)
        {
            var a = Active;
            if (a == null) return;
            if (a.WaitingRestart && data.Length > 0 && (data[0] == '\r' || data[0] == ' '))
            {
                a.Terminal.Feed("\r\n");
                StartSession(a);
                return;
            }
            if (a.View.ScrollOffset != 0) a.View.ScrollBy(-a.View.ScrollOffset);
            a.Session?.Write(data);
        }

        public void SendText(string text) => SendToSession(Encoding.UTF8.GetBytes(text ?? ""));

        private void OnKeyBarAction(string id)
        {
            var a = Active;
            switch (id)
            {
                case "recenter": Recenter(); break;
                case "scroll-up": a?.View.ScrollBy(a.Terminal.Rows / 2); break;
                case "scroll-down": a?.View.ScrollBy(-a.Terminal.Rows / 2); break;
            }
        }

        private void Update()
        {
            _control?.Pump();
            int budget = feedBudget / Mathf.Max(1, _terms.Count);
            foreach (var t in _terms)
            {
                PumpSession(t, budget);
                t.View.Render();
                if (t.Terminal.ClipboardVersion != t.ClipSeen) TakeProgramCopy(t);
            }
            UpdateStatus();
        }

        private void TakeProgramCopy(TerminalInstance t)
        {
            t.ClipSeen = t.Terminal.ClipboardVersion;
            string text = t.Terminal.ClipboardText ?? "";
            SessionLog.Log($"terminal {t.Number}: a program copied {text.Length} characters (OSC 52)");
            if (ProgramCopied != null) ProgramCopied(t, text);
            else GUIUtility.systemCopyBuffer = text;
        }

        private void PumpSession(TerminalInstance t, int budget)
        {
            if (t.Session == null) return;
            if (t.Carry != null) { budget -= Feed(t, t.Carry, budget); }
            while (budget > 0 && t.Carry == null && t.Session.TryRead(out var chunk)) budget -= Feed(t, chunk, budget);
            var replies = t.Terminal.TakeReplies();
            if (replies != null) t.Session.Write(replies);
            if (!t.Session.IsRunning && !t.WaitingRestart && t.Carry == null)
            {
                t.Terminal.Feed($"\r\n\x1b[33m[{t.Session.Description} exited with {t.Session.ExitCode}: Enter restarts]\x1b[0m\r\n");
                t.WaitingRestart = true;
            }
        }

        // Feeds up to budget bytes of chunk; keeps the rest for the next frame.
        private static int Feed(TerminalInstance t, byte[] chunk, int budget)
        {
            int n = Math.Min(chunk.Length, Math.Max(1, budget));
            t.Terminal.Feed(chunk, 0, n);
            if (n < chunk.Length)
            {
                var rest = new byte[chunk.Length - n];
                Buffer.BlockCopy(chunk, n, rest, 0, rest.Length);
                t.Carry = rest;
            }
            else t.Carry = null;
            return n;
        }

        private void UpdateStatus()
        {
            _fpsFrames++;
            if (Time.unscaledTime - _fpsTime >= 1f)
            {
                _fps = _fpsFrames / (Time.unscaledTime - _fpsTime);
                _fpsFrames = 0;
                _fpsTime = Time.unscaledTime;
                var a = Active;
                if (statusText != null && a != null)
                {
                    string title = string.IsNullOrEmpty(a.Terminal.Title) ? "" : a.Terminal.Title + "   ";
                    string state = host.State == LinuxHost.HostState.Ready ? "" : host.Status + "   ";
                    statusText.text = $"<noparse>{title}{state}</noparse><color=#7f8796>{(a.Session != null ? a.Session.Description : "no session")} · {_fps:F0} fps · draw {a.View.LastRenderMs:F1} ms</color>";
                }
            }
        }

        private string HandleCommand(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line.Trim() : line.Substring(0, sp);
            string arg = sp < 0 ? "" : line.Substring(sp + 1);
            var a = Active;
            switch (cmd)
            {
                case "text": input.TypeText(ControlServer.Unescape(arg)); return "ok";
                case "paste": input.Paste(ControlServer.Unescape(arg)); return "ok";
                case "key":
                    if (Enum.TryParse(arg.Trim(), true, out TermKey k)) { input.PressKey(k); return "ok"; }
                    return "error: unknown key " + arg;
                case "press": return keyBar.PressById(arg.Trim()) ? "ok" : "error: no button " + arg;
                case "screen":
                {
                    var sb = new StringBuilder(a.Terminal.DumpScreen());
                    sb.Append($"\n-- cursor {a.Terminal.CursorCol},{a.Terminal.CursorRow} alt={a.Terminal.AltScreen} title={a.Terminal.Title}");
                    return sb.ToString();
                }
                case "status":
                    return $"host={host.State} {host.Status}\nsession={(a.Session == null ? "none" : $"{a.Session.Description} running={a.Session.IsRunning} exit={a.Session.ExitCode}")}\n" +
                           $"terminals={_terms.Count} active={a.Number} fps={_fps:F1} draw_ms={a.View.LastRenderMs:F2} placed={_placed} rows={a.Terminal.Rows} cols={a.Terminal.Cols}\n" +
                           $"files={host.FilesDir} libs={host.NativeLibDir}";
                case "log": return string.Join("\n", SessionLog.Tail(int.TryParse(arg, out int n) ? n : 40));
                case "restart": StartSession(a); return "ok";
                case "recenter": Recenter(); return "ok";
                case "new-terminal": NewTerminal(); return "ok, terminals=" + _terms.Count;
                case "reset-userland":
                    if (arg.Trim() != "yes") return "error: deletes the whole Linux userland; send 'reset-userland yes'";
                    foreach (var t in _terms) { t.Session?.Dispose(); t.Session = null; }
                    host.DeleteUserland();
                    StartCoroutine(Reinstall());
                    return "ok, reinstalling";
                default:
                    var extra = ExtraCommand?.Invoke(cmd, arg);
                    return extra ?? "error: commands: text paste key press screen status log restart recenter new-terminal reset-userland ui script gesture";
            }
        }

        private IEnumerator Reinstall()
        {
            yield return host.Prepare();
            foreach (var t in _terms) StartSession(t);
        }

        private void OnApplicationPause(bool paused) => SessionLog.Log(paused ? "paused" : "resumed");

        private void OnDestroy()
        {
            _control?.Dispose();
            foreach (var t in _terms) t.Session?.Dispose();
        }
    }
}
