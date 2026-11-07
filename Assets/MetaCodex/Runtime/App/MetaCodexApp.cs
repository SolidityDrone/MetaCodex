using System;
using System.Collections;
using System.Text;
using MetaCodex.Linux;
using MetaCodex.Terminal;
using TMPro;
using UnityEngine;

namespace MetaCodex.App
{
    /// Milestone 1, "the terminal": a Linux shell running on the headset, shown and typed into from VR.
    /// Places the terminal panel in front of the user, installs/starts the userland (LinuxHost), pumps
    /// pty output into the emulator and keyboard/key-bar input back, and serves the control port.
    /// In the Editor (no Android) it connects to Tools/ptyserver.py on this PC, or falls back to echo.
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
        [Tooltip("Max pty bytes parsed per frame; the rest waits for the next frame")]
        [SerializeField] private int feedBudget = 256 * 1024;

        public VtTerminal Terminal { get; private set; }
        public ITerminalSession Session { get; private set; }

        private ControlServer _control;
        private bool _placed;
        private float _fpsTime;
        private int _fpsFrames;
        private float _fps;
        private byte[] _carry;
        private bool _waitingRestart;

        private IEnumerator Start()
        {
            SessionLog.Init();
            SessionLog.Log($"MetaCodex {Application.version} start ({SystemInfo.deviceModel}, {SystemInfo.operatingSystem})");
            host.Log += SessionLog.Log;
            Terminal = new VtTerminal(rows, cols, 5000);
            view.Attach(Terminal);
            keyBar.transform.localPosition = new Vector3(0f, -view.Height - 0.012f, 0f);
            keyBar.Build(view.Width, view.Font);
            keyBar.Input = input;
            keyBar.Hands = hands;
            keyBar.Action += OnKeyBarAction;
            input.Send += SendToSession;
            input.ScrollRequest += view.ScrollBy;
            input.AppCursorKeys = () => Terminal.AppCursorKeys;
            input.BracketedPaste = () => Terminal.BracketedPaste;
            if (statusText != null) statusText.font = view.Font;

            if (controlPort)
            {
                try { _control = new ControlServer(ControlServer.DefaultPort, HandleCommand); SessionLog.Log($"control port 127.0.0.1:{_control.Port}"); }
                catch (Exception ex) { SessionLog.Log("control port off: " + ex.Message); }
            }

            yield return PlaceWhenTracked();
            yield return host.Prepare();
            StartSession();
        }

        private void StartSession()
        {
            Session?.Dispose();
            Session = null;
            _waitingRestart = false;
            try
            {
                if (host.State == LinuxHost.HostState.Ready)
                {
                    Session = host.StartShell(rows, cols);
                }
                else if (host.State == LinuxHost.HostState.Unsupported)
                {
                    try { Session = new TcpTerminalSession(bridgeHost, bridgePort, rows, cols); SessionLog.Log("terminal: bridge " + Session.Description); }
                    catch (Exception) { Session = new EchoSession("No Linux here (Editor) and no Tools/ptyserver.py bridge on port " + bridgePort + ": echo mode."); }
                }
                else
                {
                    Terminal.Feed("\r\n\x1b[31m" + host.Status + "\x1b[0m\r\n");
                }
            }
            catch (Exception ex)
            {
                SessionLog.Log("shell start failed: " + ex);
                Terminal.Feed("\r\n\x1b[31mshell start failed: " + ex.Message + "\x1b[0m\r\n");
            }
        }

        private IEnumerator PlaceWhenTracked()
        {
            float until = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < until && (head == null || head.position == Vector3.zero)) yield return null;
            yield return null;
            Recenter();
        }

        /// Puts the terminal in front of where the user looks now: level, slightly below the eyes.
        public void Recenter()
        {
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.2f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var rot = Quaternion.LookRotation(fwd, Vector3.up); // panel +z points away from the user
            Vector3 center = eye + fwd * distance + Vector3.down * dropBelowEyes;
            Vector3 topLeft = center - rot * new Vector3(view.Width / 2f, -view.Height / 2f, 0f);
            var root = view.transform.parent != null ? view.transform.parent : view.transform;
            root.SetPositionAndRotation(topLeft, rot);
            _placed = true;
            SessionLog.Log($"terminal placed at {topLeft} ({view.Width:F2} x {view.Height:F2} m)");
        }

        private void SendToSession(byte[] data)
        {
            if (_waitingRestart && data.Length > 0 && (data[0] == '\r' || data[0] == ' '))
            {
                Terminal.Feed("\r\n");
                StartSession();
                return;
            }
            if (view.ScrollOffset != 0) view.ScrollBy(-view.ScrollOffset);
            Session?.Write(data);
        }

        private void OnKeyBarAction(string id)
        {
            switch (id)
            {
                case "recenter": Recenter(); break;
                case "scroll-up": view.ScrollBy(Terminal.Rows / 2); break;
                case "scroll-down": view.ScrollBy(-Terminal.Rows / 2); break;
            }
        }

        private void Update()
        {
            _control?.Pump();
            PumpSession();
            view.Render();
            UpdateStatus();
        }

        private void PumpSession()
        {
            if (Session == null) return;
            int budget = feedBudget;
            if (_carry != null) { budget -= Feed(_carry, budget); }
            while (budget > 0 && _carry == null && Session.TryRead(out var chunk)) budget -= Feed(chunk, budget);
            var replies = Terminal.TakeReplies();
            if (replies != null) Session.Write(replies);
            if (!Session.IsRunning && !_waitingRestart && _carry == null)
            {
                Terminal.Feed($"\r\n\x1b[33m[{Session.Description} exited with {Session.ExitCode}: Enter restarts]\x1b[0m\r\n");
                _waitingRestart = true;
            }
        }

        // Feeds up to budget bytes of chunk; keeps the rest for the next frame.
        private int Feed(byte[] chunk, int budget)
        {
            int n = Math.Min(chunk.Length, Math.Max(1, budget));
            Terminal.Feed(chunk, 0, n);
            if (n < chunk.Length)
            {
                var rest = new byte[chunk.Length - n];
                Buffer.BlockCopy(chunk, n, rest, 0, rest.Length);
                _carry = rest;
            }
            else _carry = null;
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
                if (statusText != null)
                {
                    string title = string.IsNullOrEmpty(Terminal.Title) ? "" : Terminal.Title + "   ";
                    string state = host.State == LinuxHost.HostState.Ready ? "" : host.Status + "   ";
                    statusText.text = $"<noparse>{title}{state}</noparse><color=#7f8796>{(Session != null ? Session.Description : "no session")} · {_fps:F0} fps · draw {view.LastRenderMs:F1} ms</color>";
                }
            }
        }

        private string HandleCommand(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line.Trim() : line.Substring(0, sp);
            string arg = sp < 0 ? "" : line.Substring(sp + 1);
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
                    var sb = new StringBuilder(Terminal.DumpScreen());
                    sb.Append($"\n-- cursor {Terminal.CursorCol},{Terminal.CursorRow} alt={Terminal.AltScreen} title={Terminal.Title}");
                    return sb.ToString();
                }
                case "status":
                    return $"host={host.State} {host.Status}\nsession={(Session == null ? "none" : $"{Session.Description} running={Session.IsRunning} exit={Session.ExitCode}")}\n" +
                           $"fps={_fps:F1} draw_ms={view.LastRenderMs:F2} placed={_placed} rows={Terminal.Rows} cols={Terminal.Cols}\n" +
                           $"files={host.FilesDir} libs={host.NativeLibDir}";
                case "log": return string.Join("\n", SessionLog.Tail(int.TryParse(arg, out int n) ? n : 40));
                case "restart": StartSession(); return "ok";
                case "recenter": Recenter(); return "ok";
                case "reset-userland":
                    if (arg.Trim() != "yes") return "error: deletes the whole Linux userland; send 'reset-userland yes'";
                    Session?.Dispose();
                    Session = null;
                    host.DeleteUserland();
                    StartCoroutine(Reinstall());
                    return "ok, reinstalling";
                default: return "error: commands: text paste key press screen status log restart recenter reset-userland";
            }
        }

        private IEnumerator Reinstall()
        {
            yield return host.Prepare();
            StartSession();
        }

        private void OnApplicationPause(bool paused) => SessionLog.Log(paused ? "paused" : "resumed");

        private void OnDestroy()
        {
            _control?.Dispose();
            Session?.Dispose();
        }
    }
}
