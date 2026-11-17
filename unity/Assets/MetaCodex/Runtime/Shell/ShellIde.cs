using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MetaCodex.AI;
using MetaCodex.Linux;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// CODE EDITOR (owner 2026-11-16: "an actual IDE ... to work for real without using the agents too"): an open-source
    /// browser IDE server (docs/ide.md; its name and licence in docs/LICENSES.md) in the headset's Linux, shown in the
    /// WEB window. `mcx-ide` (userland) installs, runs and stops it. Nothing is downloaded or started by itself: the user
    /// installs it (SYSTEM > TOOLS > code editor, the CODE tile, voice; about IdeDownloadMB, in a terminal) and opens it.
    ///   OPEN    when it is not running: `exec mcx-ide run` through the AI service process, like MQH (cores 0-2, away from
    ///           the frame loop; a server started by the hidden shell runner would die with it: proot --kill-on-exit);
    ///           then /healthz until it answers (and its own log says it listens on that port: no other app's server).
    ///   SIGN-IN it always asks for a password (every app on the headset can reach 127.0.0.1, and the editor is a shell in
    ///           the Linux). mcx-ide makes a random one per install (a file in the rootfs, the app's private storage) and
    ///           starts the server with its sha256 as HASHED_PASSWORD; the server accepts a session cookie equal to that
    ///           value, so the app sets that cookie for the editor's address in its WebView's cookie store
    ///           (MetaCodexIde.java: host-only, HttpOnly, SameSite=Strict) and opens the page signed in. Nobody types it;
    ///           `mcx-ide password` shows it for another browser.
    ///   SHOW    OpenBrowser(url) in the WEB window, made larger while it shows the editor.
    ///   STOP    `mcx-ide stop`, then the service drops its child: its memory comes back.
    /// Voice: open_code_editor (a project's folder too), stop_code_editor. Control port: `system ide start|open|stop|status`
    /// (`mcx-ide start` in a terminal asks the app, so the server runs in the service there too).
    public sealed partial class MetaCodexShell
    {
        /// The TOOLS row's name (ShellSystem's catalog) and what the user reads.
        public const string IdeToolName = "code editor";
        /// The release mcx-ide installs (keep VERSION in userland/overlay/usr/local/bin/mcx-ide the same).
        public const string IdeVersion = "4.145.2";
        /// The download (233,639,045 bytes) and the program unpacked (675,891,723 bytes), in MB.
        public const int IdeDownloadMB = 225, IdeDiskMB = 645;
        public const int IdePort = 47820;
        public const string IdeMarker = "/opt/ide/VERSION";
        public const string IdeDataDir = "/root/.local/share/mcx-ide";
        public const string IdeDefaultFolder = "/root/metacodex-projects";
        /// The server's session cookie (its src/common/http.ts CookieKeys.Session).
        public const string IdeCookieName = "code-server-session";
        /// The WEB window's size while it shows the editor (x the UI scale): code wants room.
        public const float IdeWebScale = 1.25f;
        private const string IdeServiceName = "ide";
        private const string IdeServiceClass = "com.pnncl.metacodex.ai.MetaCodexAiService";
        private static readonly HttpClient IdeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        /// off, installing, starting, running, failed.
        public string IdeState { get; private set; } = "off";
        private string _ideUrl;            // its address while it runs ("http://127.0.0.1:47820/")
        private bool _ideBusy;             // a start or a sign-in is under way
        private bool _ideOpenAfterInstall; // the user asked to open it: it opens when the install is done
        private string _ideFolderAfterInstall;
        private string _ideFileNext, _ideFileFor; // a file to show when the editor's page opens next on that folder (OpenIdeAt)
        private bool _ideInstallWatch;
        private UiPanel _ideWebHooked;     // the WEB window whose Closed puts its size back

        private string IdePath(string linuxPath) => AndroidPathOf(linuxPath);

        /// Installed: mcx-ide writes /opt/ide/VERSION last.
        public bool IdeInstalled { get { var p = IdePath(IdeMarker); return p != null && File.Exists(p); } }

        /// The installed version ("" when none).
        public string IdeInstalledVersion
        {
            get { try { var p = IdePath(IdeMarker); return p != null && File.Exists(p) ? File.ReadAllText(p).Trim() : ""; } catch (Exception) { return ""; } }
        }

        /// The WEB window shows the editor (the CODE tile is lit). Cheap: every frame while the wrist menu shows.
        public bool IdeShowing => _ideUrl != null && IsPanelOpen("web") && (WebUrlForTest ?? "").StartsWith(_ideUrl, StringComparison.Ordinal);

        /// The CODE tile: shows the editor, or puts the WEB window away when it shows it.
        public void ToggleIde()
        {
            if (IdeShowing) { ClosePanel("web"); return; }
            OpenIde(null);
        }

        /// Opens the editor in the WEB window (`folder`: a Linux folder to open in it; null: the last one). Not installed:
        /// asks to install it first. Returns what happened, for voice ("error: ..." when it cannot).
        public string OpenIde(string folder) => IdeEnsure(true, folder);

        /// Opens the editor on `folder` with `file` (a Linux path; null: only the folder) open in it: THREAD's sidebar FILES.
        public string OpenIdeAt(string folder, string file)
        {
            _ideFileNext = file; _ideFileFor = folder;
            return IdeEnsure(true, folder);
        }

        /// Starts it (the control port's `system ide start`, from `mcx-ide start`) or opens it.
        private string IdeEnsure(bool show, string folder)
        {
            if (SetupBlocks(3, "CODE EDITOR")) return "error: the Linux is still being set up";
            var host = app != null ? app.Host : null;
            if (Application.isEditor || host == null)
            {
                Toast("The code editor runs in the headset's Linux", "info");
                return "error: the code editor runs on the headset";
            }
            if (host.State != LinuxHost.HostState.Ready) { Toast("The Linux is not ready yet: the code editor needs it", "warn"); return "error: the Linux is not ready yet"; }
            if (!IdeInstalled)
            {
                if (!show) return "error: not installed: mcx-ide install";
                AskIdeInstall(folder);
                return "The code editor is not installed yet: install it?";
            }
            if (_ideBusy) return show ? "The code editor is starting" : "ok starting"; // mcx-ide start waits for it, never starts a second one
            StartCoroutine(IdeOpenRoutine(host, show, folder));
            return show ? "Opening the code editor" : "ok starting";
        }

        private IEnumerator IdeOpenRoutine(LinuxHost host, bool show, string folder)
        {
            _ideBusy = true;
            int port = IdeReadPort();
            var alive = IdeAlive(port);
            while (!alive.IsCompleted) yield return null;
            if (!alive.Result)
            {
                IdeState = "starting";
                if (show) Toast("Starting the code editor: the first start takes up to a minute", "info");
                string err = IdeServiceStart(host);
                if (err != null) { IdeFailed("could not start it: " + err); yield break; }
                float until = Time.unscaledTime + 150f, started = Time.unscaledTime;
                yield return new WaitForSecondsRealtime(1f);
                while (true)
                {
                    port = IdeReadPort();
                    alive = IdeAlive(port);
                    while (!alive.IsCompleted) yield return null;
                    if (alive.Result) break;
                    if (Time.unscaledTime > until) { IdeFailed("it did not answer in 150 s (mcx-ide log in a terminal)"); yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                Log($"code editor: up on port {port} in {Time.unscaledTime - started:F1} s");
            }
            IdeState = "running";
            _ideUrl = $"http://127.0.0.1:{port}/";
            if (!show) { _ideBusy = false; yield break; }

            // signed in: the session cookie for the editor's address, then the page
            string session = IdeSessionValue();
            if (session == null) { IdeFailed("no password file (" + IdeDataDir + "/password)"); yield break; }
            string signIn = IdeSetCookie(_ideUrl, IdeCookieName + "=" + session + "; Path=/; HttpOnly; SameSite=Strict");
#if UNITY_ANDROID && !UNITY_EDITOR
            float limit = Time.unscaledTime + 3f;
            while (signIn == "pending" && Time.unscaledTime < limit) { yield return null; signIn = IdeCookieState(); }
#endif
            if (signIn != "ok") Log("code editor: the sign-in cookie was not set (" + signIn + "): its login page asks for the password (mcx-ide password)");
            _ideBusy = false;
            ShowIdePage(folder);
        }

        private void IdeFailed(string why)
        {
            _ideBusy = false;
            IdeState = "failed";
            Log("code editor: " + why);
            Toast("Code editor: " + why, "error");
        }

        /// The editor's page in the WEB window, larger than a web page (put back when the window closes).
        private void ShowIdePage(string folder)
        {
            if (_ideUrl == null) return;
            string url = _ideUrl + (string.IsNullOrEmpty(folder) ? "" : "?folder=" + Uri.EscapeDataString(folder));
            string file = folder != null && folder == _ideFileFor ? _ideFileNext : null; // a stale request (cancelled install) never opens elsewhere
            _ideFileNext = _ideFileFor = null;
            if (!string.IsNullOrEmpty(file) && file.StartsWith("/", StringComparison.Ordinal))
            {
                // the workbench's own way to open a file from the address (its payload "openFile"): a remote URI on the
                // server's host:port, the authority it gives the folder too
                string fileUri = "vscode-remote://" + new Uri(_ideUrl).Authority + string.Join("/", file.Split('/').Select(Uri.EscapeDataString));
                string payload = new JArray(new JArray("openFile", fileUri)).ToString(Newtonsoft.Json.Formatting.None);
                url += (url.Contains("?") ? "&" : "?") + "payload=" + Uri.EscapeDataString(payload);
            }
            if (_panels.TryGetValue("web", out var web) && web != null)
            {
                web.BaseScale = Vector3.one * UiScaleNow * IdeWebScale; // before it opens: it is placed at its new size
                if (_ideWebHooked != web)
                {
                    _ideWebHooked = web;
                    // after its closing animation (it shrinks from the size it had), unless it opened again meanwhile
                    web.Closed += p => After(0.6f, () => { if (p != null && !p.IsOpen) p.BaseScale = Vector3.one * UiScaleNow; });
                }
            }
            OpenBrowser(url);
        }

        /// The port it listens on (mcx-ide writes it when it starts; the default before that).
        private int IdeReadPort()
        {
            try
            {
                var p = IdePath(IdeDataDir + "/run/port");
                if (p != null && File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out int port) && port > 1024 && port < 65536) return port;
            }
            catch (Exception) { }
            return IdePort;
        }

        /// Ours and up: its process is alive, its log of this run says it listens on that port (so the answer is not
        /// another app's server holding the port: the cookie must never go there), and /healthz answers.
        private Task<bool> IdeAlive(int port)
        {
            string pidPath = IdePath(IdeDataDir + "/run/pid"), logPath = IdePath(IdeDataDir + "/run/server.log");
            return Task.Run(async () =>
            {
                try
                {
                    if (pidPath == null || !File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath).Trim(), out int pid)) return false;
                    if (!Directory.Exists("/proc/" + pid)) return false;
                    if (logPath == null || !File.Exists(logPath)) return false;
                    string mark = "listening on http://127.0.0.1:" + port;
                    bool listening = false;
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var r = new StreamReader(fs))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null) if (line.Contains(mark)) { listening = true; break; }
                    }
                    if (!listening) return false;
                    using (var res = await IdeHttp.GetAsync($"http://127.0.0.1:{port}/healthz").ConfigureAwait(false))
                        return res.IsSuccessStatusCode && (await res.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("\"status\"");
                }
                catch (Exception) { return false; }
            });
        }

        /// The session cookie's value: the sha256 (hex) of the password mcx-ide made; null when there is none.
        public string IdeSessionValue()
        {
            try
            {
                var p = IdePath(IdeDataDir + "/password");
                if (p == null || !File.Exists(p)) return null;
                string pass = File.ReadAllText(p).Replace("\n", "").Replace("\r", "");
                return pass.Length == 0 ? null : IdeHash(pass);
            }
            catch (Exception ex) { Log("code editor: password: " + ex.Message); return null; }
        }

        /// Lower-case hex sha256 of the text (UTF-8): what `sha256sum` prints and the server compares the cookie with.
        public static string IdeHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// Sets the cookie in the WebView's store (on Android's UI thread, MetaCodexIde.java): "pending", then "ok" or
        /// "error: ...". The Editor has no WebView: "error: no WebView".
        private static string IdeSetCookie(string url, string cookie)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var c = new AndroidJavaClass("com.pnncl.metacodex.ide.MetaCodexIde"))
                {
                    c.CallStatic("setSession", url, cookie);
                    return c.CallStatic<string>("state");
                }
            }
            catch (Exception ex) { return "error: " + ex.Message; }
#else
            return "error: no WebView";
#endif
        }

        private static string IdeCookieState()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { using (var c = new AndroidJavaClass("com.pnncl.metacodex.ide.MetaCodexIde")) return c.CallStatic<string>("state"); }
            catch (Exception ex) { return "error: " + ex.Message; }
#else
            return "error: no WebView";
#endif
        }

        /// `exec mcx-ide run` in the AI service process (name "ide"; a new start replaces its child). Null when sent.
        private string IdeServiceStart(LinuxHost host)
        {
            try
            {
                var argv = host.ProotArgv("exec mcx-ide run", null, out var env);
                IdeService(argv, env, host.FilesDir, Path.Combine(host.LinuxDir, "ide-run.log"), null);
                Log("code editor: starting (mcx-ide run in the service process)");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void IdeService(string[] argv, string[] env, string cwd, string log, string action)
        {
            if (Application.platform != RuntimePlatform.Android) return;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = new AndroidJavaObject("android.content.Intent"))
            {
                intent.Call<AndroidJavaObject>("setClassName", activity, IdeServiceClass).Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "name", IdeServiceName).Dispose();
                if (action != null) intent.Call<AndroidJavaObject>("putExtra", "action", action).Dispose();
                if (argv != null)
                {
                    intent.Call<AndroidJavaObject>("putExtra", "argv", argv).Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "env", env).Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "cwd", cwd).Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "log", log).Dispose();
                }
                using (var cn = activity.Call<AndroidJavaObject>("startService", intent))
                    if (cn == null) throw new InvalidOperationException("startService returned null (" + IdeServiceClass + " missing from the manifest?)");
            }
        }

        /// STOP: the server ends (mcx-ide stop: TERM, then KILL), then the service drops its child. Its WEB page closes.
        public string StopIde()
        {
            if (Application.isEditor || app?.Host == null) return "error: the code editor runs on the headset";
            bool showing = IdeShowing;
            _ideUrl = null;
            IdeState = "off";
            if (showing) ClosePanel("web");
            Task.Run(async () =>
            {
                var r = await Git.Shell("mcx-ide stop", 30000);
                _main.Enqueue(() =>
                {
                    try { IdeService(null, null, null, null, "stop"); } catch (Exception ex) { Log("code editor: service stop: " + ex.Message); }
                    Log("code editor: stopped (" + (r.output ?? "").Trim() + ")");
                });
            });
            Toast("Code editor stopped: its memory is free again (your files stay)", "ok");
            return "Stopped the code editor";
        }

        // ------------------------------------------------------------------ install

        private void AskIdeInstall(string folder)
        {
            Modal("CODE EDITOR", $"A full code editor in the WEB window: files, search, git, a terminal, extensions. Install it? " +
                $"About {IdeDownloadMB} MB to download (needs internet), {IdeDiskMB} MB on the headset. It installs in a terminal and opens when it is done.",
                new[] { "INSTALL", "CANCEL" }, i => { if (i == 0) InstallIde(true, folder); });
        }

        /// `mcx-ide install` in a new terminal (the download's progress shows there); the app watches for the marker and
        /// opens the editor when it is there (`open`).
        public bool InstallIde(bool open, string folder)
        {
            if (!RunInNewTerminal("mcx-ide install ; echo; echo '(the code editor opens by itself when it is installed)'")) return false;
            IdeState = "installing";
            _ideOpenAfterInstall = open;
            _ideFolderAfterInstall = folder;
            _toolsAt = DateTime.MinValue; // TOOLS checks again when it opens
            if (!_ideInstallWatch) StartCoroutine(IdeInstallWatch());
            return true;
        }

        private IEnumerator IdeInstallWatch()
        {
            _ideInstallWatch = true;
            float until = Time.unscaledTime + 60f * 60f;
            var wait = new WaitForSecondsRealtime(2f);
            // this app's version (an update replaces an older one: its marker is there until the new one is written)
            while (Time.unscaledTime < until && IdeInstalledVersion != IdeVersion) yield return wait;
            _ideInstallWatch = false;
            if (!IdeInstalled) { if (IdeState == "installing") IdeState = "off"; yield break; }
            IdeState = "off";
            Log("code editor: installed " + IdeInstalledVersion);
            RefreshTools(true);
            if (_ideOpenAfterInstall) { _ideOpenAfterInstall = false; OpenIde(_ideFolderAfterInstall); }
            else Toast("Code editor installed: open it from SYSTEM > TOOLS or the CODE tile", "ok");
        }

        // ------------------------------------------------------------------ SYSTEM > TOOLS

        /// The TOOLS row "code editor" (ShellSystem.ToolPicked): install, open, stop, remove.
        private void IdePicked(ToolInfo tool)
        {
            var t = UiTheme.Current;
            bool installed = IdeInstalled;
            string have = IdeInstalledVersion;
            var items = new List<UiListItem>();
            if (!installed)
                items.Add(new UiListItem { Icon = "⇣", Title = "Install the code editor", Sub = $"about {IdeDownloadMB} MB download, needs internet · {IdeDiskMB} MB on the headset", Tag = "install" });
            else
            {
                items.Add(new UiListItem { Icon = "▶", Title = "Open", Sub = "in the WEB window, signed in", Tag = "open" });
                if (IdeState == "running" || IdeState == "starting") items.Add(new UiListItem { Icon = "■", Title = "Stop", Sub = "its memory comes back; your files stay", Tag = "stop" });
                if (have.Length > 0 && have != IdeVersion) items.Add(new UiListItem { Icon = "⟳", Title = "Update to " + IdeVersion, Sub = $"about {IdeDownloadMB} MB download; settings and extensions stay", Tag = "update" });
                items.Add(new UiListItem { Icon = "▤", Title = "Status and log", Sub = "mcx-ide status, mcx-ide log (in a terminal)", Tag = "log" });
                items.Add(new UiListItem { Icon = "✕", Title = "Remove", Sub = "mcx-ide uninstall: its settings and extensions stay; your projects stay", TitleColor = t.Danger, Tag = "remove" });
            }
            string hint = installed
                ? $"Version {have} · a full code editor from the Linux, in the WEB window. A Bluetooth keyboard is best; the system keyboard (⌨ in WEB) works for short edits."
                : "A full code editor from the Linux, in the WEB window: files, search, git, a terminal, extensions. Not installed.";
            ShowPicker(IdeToolName, hint, items, item =>
            {
                switch (item.Tag as string)
                {
                    case "install": InstallIde(true, null); break;
                    case "update": StopIde(); InstallIde(false, null); break;
                    case "open": OpenIde(null); break;
                    case "stop": StopIde(); break;
                    case "log": RunInNewTerminal("mcx-ide status ; mcx-ide log"); break;
                    case "remove":
                        Modal("REMOVE THE CODE EDITOR", "Run mcx-ide uninstall: the program goes (about " + IdeDiskMB + " MB); its settings and extensions stay, and so do your projects.",
                            new[] { "REMOVE", "CANCEL" }, i =>
                            {
                                if (i != 0) return;
                                _ideUrl = null;
                                IdeState = "off";
                                try { IdeService(null, null, null, null, "stop"); } catch (Exception) { }
                                if (RunInNewTerminal("mcx-ide uninstall ; echo; echo '(done: ⟳ CHECK in SYSTEM)'")) _toolsAt = DateTime.MinValue;
                            });
                        break;
                }
            }, PanelsById["system"]);
        }

        /// `system ide …` on the control port (mcx-ui system ide …; `mcx-ide start` in a terminal sends `start`).
        private string IdeCommand(string arg)
        {
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, 2);
            switch (parts[0])
            {
                case "start": return IdeEnsure(false, null);
                case "open": return IdeEnsure(true, parts.Length > 1 ? parts[1].Trim() : null);
                case "stop": return StopIde();
                case "status": return $"{IdeState} installed={(IdeInstalled ? IdeInstalledVersion : "no")} url={_ideUrl ?? "-"}";
                default: return "error: system ide start | open [folder] | stop | status";
            }
        }

        // ------------------------------------------------------------------ voice

        /// Registered with the shell's commands (RegisterShellVoiceCommands).
        private void RegisterIdeVoice()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "open_code_editor",
                Description = "Open the code editor (a full IDE from this headset's Linux: files, search, git, a terminal) in the web window, signed in. " +
                    "With a project: that project's folder (its name, or current for the workspace open now). Not installed: asks to install it first",
                Parameters = VoiceCommands.Schema(("project", "string", null, "the project (workspace) whose folder opens in the editor: its name, or current; leave out for the last folder", false)),
                Phrases = new[]
                {
                    "(open|show|start|launch|bring up|pull up) [the|my] (code editor|ide|editor|programming editor) [window|app]",
                    "(open|edit|load|show) [the|my] {project} [project|workspace|folder|repo] in [the|my] (code editor|ide|editor)",
                    "(work on|code|edit) [the|my] {project} [project|workspace|folder] in [the|my] (code editor|ide|editor)",
                    "(open|show|start|launch) [the] (vs code|vscode|visual studio code)",
                    "(code editor|ide)",
                },
                Samples = new[]
                {
                    "i want to write some code myself", "let me edit the files in a real ide", "bring up the programming editor",
                    "open the weather project in the code editor", "start the ide so i can work without the agents", "edit my current project in the editor",
                },
                Examples = new List<(string, string)> { ("open the notes app project in the ide", "open_code_editor notes app"), ("i would like to code by hand for a while", "open_code_editor") },
                Vocabulary = new[] { "IDE", "code editor" },
                RunAsync = (a, reply) => VoiceOpenIde((string)a["project"], reply),
            });
            Voice.Register(new VoiceCommand
            {
                Name = "stop_code_editor",
                Description = "Stop the code editor's server in the Linux to free its memory (your files stay); it starts again when it is opened",
                Phrases = new[] { "(stop|quit|shut down|turn off|exit) [the|my] (code editor|ide|editor|programming editor) [server]" },
                Samples = new[] { "shut the ide down", "i am done with the code editor, free its memory", "turn off the programming editor", "quit the ide server" },
                Run = a => StopIde(),
            });
        }

        private static readonly string[] IdeCurrentWords = { "current", "this", "this one", "the current one", "current project", "this project", "my current project", "here" };

        /// open_code_editor: no project, the last folder; "current", the workspace open now; else the project (workspace) of
        /// that name (MQH's list, as switch_workspace matches it), or a folder of that name in metacodex-projects.
        private void VoiceOpenIde(string project, Action<string> reply)
        {
            if (string.IsNullOrWhiteSpace(project)) { reply(OpenIde(null)); return; }
            string want = VoiceCommands.Simplify(project);
            if (IdeCurrentWords.Contains(want))
            {
                var cur = _projects?.FirstOrDefault(p => (string)p["id"] == _projectSel);
                string path = (string)cur?["sources"]?.FirstOrDefault()?["path"];
                reply(path != null ? OpenIde(path) : "error: no workspace is open: say its name");
                return;
            }
            if (Mqh == null) { reply(OpenIde(IdeProjectFolder(project))); return; }
            Mqh.Projects(pr =>
            {
                string path = null;
                if (pr is JArray arr)
                {
                    var best = arr.Select(p =>
                    {
                        string n = VoiceCommands.Simplify((string)p["name"] ?? "");
                        string pp = (string)p["sources"]?.FirstOrDefault()?["path"] ?? "";
                        string leaf = VoiceCommands.Simplify(Path.GetFileName(pp.TrimEnd('/')));
                        float s = n == want || leaf == want ? 3f
                            : n.Replace(" ", "") == want.Replace(" ", "") || leaf.Replace(" ", "") == want.Replace(" ", "") ? 2.5f
                            : n.Length > 2 && (n.Contains(want) || want.Contains(n)) ? 2f
                            : VoiceCommands.Levenshtein(n, want) <= Math.Max(1, want.Length / 4) ? 1.5f : 0f;
                        return (path: pp, s);
                    }).OrderByDescending(x => x.s).FirstOrDefault();
                    if (best.s > 0f && best.path.Length > 0) path = best.path;
                }
                path = path ?? IdeProjectFolder(project);
                if (path == null) { reply($"error: no project called “{project}”: opening the editor on its last folder"); OpenIde(null); return; }
                reply(OpenIde(path));
            });
        }

        /// /root/metacodex-projects/<folder name> when it exists; null otherwise.
        private string IdeProjectFolder(string name)
        {
            string leaf = ProjectKinds.FolderName(name);
            if (leaf.Length == 0) return null;
            string linux = IdeDefaultFolder + "/" + leaf;
            var p = IdePath(linux);
            return p != null && Directory.Exists(p) ? linux : null;
        }
    }
}
