using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// SYSTEM: what is installed in the Linux userland and where the storage goes.
    ///   TOOLS    a tile per known tool (runtimes, package managers, compilers, git, editors, agent CLIs) with its
    ///            version; missing ones install from a tap (in a terminal, so you see what happens)
    ///   STORAGE  the headset's space (Linux, AI models, shared folder, free), the Linux folders by size with
    ///            one-tap cleanups, and a soft limit for Linux (Android cannot reserve or cap an app's space:
    ///            the app warns when Linux grows past it)
    public sealed partial class MetaCodexShell
    {
        public sealed class ToolInfo { public string Name, Category, Group, Install, Update, Remove, Path, Version, Raw; public bool Found; }

        private static readonly string[] KnownPathAdds = { "$HOME/.bun/bin", "$HOME/.cargo/bin", "$HOME/.local/bin", "$HOME/go/bin", "/usr/local/go/bin", "/usr/local/bin", "/opt/android-ndk" };

        /// A tool the TOOLS tab knows: how to see it (`check`: a command on PATH, or an absolute path that exists once it
        /// is installed), its version line, its group, and how to install / update / remove it ("apt:<packages>",
        /// "agents" = from the AGENTS panel, or a shell line run in a terminal).
        public sealed class ToolDef
        {
            public string Name, Group, Ver, Check, Install, Update, Remove;
            public ToolDef(string name, string group, string ver, string install, string update = null, string remove = null, string check = null)
            {
                // no update of its own: the same as installing again, except for agent CLIs (their own updaters, via AGENTS)
                Name = name; Group = group; Ver = ver; Install = install; Update = update ?? (install == "agents" ? null : install); Remove = remove; Check = check ?? ver.Split(' ')[0];
            }
        }

        /// The groups, in the order the tab shows them: what each is for, in words a beginner understands.
        public static readonly (string id, string title, string blurb, int series)[] ToolGroups =
        {
            ("agents", "AI AGENTS", "assistants that read and write code with you", 6),
            ("js", "JAVASCRIPT · TYPESCRIPT", "run web and Node apps, add packages", 2),
            ("python", "PYTHON", "scripts, data and AI tools", 0),
            ("native", "NATIVE CODE", "compilers and build tools for C, C++, Rust and Go", 1),
            ("android", "ANDROID · QUEST", "build native apps for this headset (NDK, android.jar, OpenXR, APK tools, adb, Vulkan shaders)", 3),
            ("git", "GIT", "version control: history, branches, GitHub", 4),
            ("terminal", "TERMINAL UTILITIES", "download, connect, search, sessions, data", 7),
            ("editors", "EDITORS", "edit files right in the terminal", 5),
        };

        /// The native Quest app tools come from `mcx-setup` (one place pins every download and its sha256): the NDK alone
        /// (`mcx-setup ndk`: the community arm64 build of NDK r27d, since Google ships Linux NDKs for x86_64 only), or
        /// everything an app needs (`mcx-setup quest`: + APK tools, JDK, android.jar, OpenXR; what first use runs).
        private const string NdkInstall = "mcx-setup ndk";
        private const string QuestInstall = "mcx-setup quest";

        public static readonly ToolDef[] ToolCatalog =
        {
            new ToolDef("claude", "agents", "claude --version", "agents"),
            new ToolDef("codex", "agents", "codex --version", "agents"),
            new ToolDef("opencode", "agents", "opencode --version", "agents"),
            new ToolDef("mqh", "agents", "mqh --version", null),

            new ToolDef("node", "js", "node --version", "mcx-setup"),
            new ToolDef("npm", "js", "npm --version", "mcx-setup", "npm install -g npm"),
            new ToolDef("pnpm", "js", "pnpm --version", "npm install -g pnpm", "npm install -g pnpm", "npm uninstall -g pnpm"),
            new ToolDef("bun", "js", "bun --version", "apt-get install -y unzip && curl -fsSL https://bun.sh/install | bash", "bun upgrade", "rm -rf ~/.bun"),

            new ToolDef("python3", "python", "python3 --version", "apt:python3 python3-pip python3-venv", "apt:python3"),
            new ToolDef("pip", "python", "pip3 --version", "apt:python3-pip", "apt:python3-pip", "apt:python3-pip", check: "pip3"),
            new ToolDef("uv", "python", "uv --version", "curl -LsSf https://astral.sh/uv/install.sh | sh", "uv self update", "rm -f ~/.local/bin/uv ~/.local/bin/uvx"),

            new ToolDef("gcc", "native", "gcc --version", "apt:build-essential"),
            new ToolDef("clang", "native", "clang --version", "apt:clang lld", "apt:clang lld", "apt:clang lld"),
            new ToolDef("make", "native", "make --version", "apt:make"),
            new ToolDef("cmake", "native", "cmake --version", "apt:cmake", "apt:cmake", "apt:cmake"),
            new ToolDef("ninja", "native", "ninja --version", "apt:ninja-build", "apt:ninja-build", "apt:ninja-build"),
            new ToolDef("rustc", "native", "rustc --version", "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y", "rustup update", "rustup self uninstall -y"),
            new ToolDef("cargo", "native", "cargo --version", "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y", "rustup update"),
            new ToolDef("go", "native", "go version", "apt:golang", "apt:golang", "apt:golang"),

            new ToolDef("ndk", "android", "sed -n 's/^Pkg.Revision = //p' /opt/android-ndk/source.properties", NdkInstall, NdkInstall,
                "rm -rf /opt/android-ndk-r27d /opt/android-ndk /etc/profile.d/android-ndk.sh", check: "/opt/android-ndk/source.properties"),
            new ToolDef("android-sdk", "android", "sed -n 's/^AndroidVersion.ApiLevel=/API /p' /opt/android-sdk/platforms/android-34/source.properties", QuestInstall, QuestInstall,
                "rm -rf /opt/android-sdk", check: "/opt/android-sdk/platforms/android-34/android.jar"),
            new ToolDef("openxr", "android", "cat /opt/openxr/VERSION", QuestInstall, QuestInstall, "rm -rf /opt/openxr", check: "/opt/openxr/lib/arm64-v8a/libopenxr_loader.so"),
            new ToolDef("java", "android", "java -version", "apt:default-jdk-headless", "apt:default-jdk-headless", "apt:default-jdk-headless"),
            new ToolDef("adb", "android", "adb version", "apt:adb", "apt:adb", "apt:adb"),
            new ToolDef("build-tools", "android", "aapt2 version", "apt:aapt apksigner zipalign", "apt:aapt apksigner zipalign", "apt:aapt apksigner zipalign", check: "aapt2"),
            new ToolDef("glslc", "android", "glslc --version", "apt:glslc", "apt:glslc", "apt:glslc"),

            new ToolDef("git", "git", "git --version", "apt:git", "apt:git", "apt:git"),
            new ToolDef("gh", "git", "gh --version", "apt:gh", "apt:gh", "apt:gh"),

            new ToolDef("curl", "terminal", "curl --version", "apt:curl"),
            new ToolDef("wget", "terminal", "wget --version", "apt:wget", "apt:wget", "apt:wget"),
            new ToolDef("ssh", "terminal", "ssh -V", "apt:openssh-client", "apt:openssh-client", "apt:openssh-client"),
            new ToolDef("jq", "terminal", "jq --version", "apt:jq", "apt:jq", "apt:jq"),
            new ToolDef("rg", "terminal", "rg --version", "apt:ripgrep", "apt:ripgrep", "apt:ripgrep"),
            new ToolDef("fd", "terminal", "fdfind --version", "apt:fd-find", "apt:fd-find", "apt:fd-find"),
            new ToolDef("fzf", "terminal", "fzf --version", "apt:fzf", "apt:fzf", "apt:fzf"),
            new ToolDef("tmux", "terminal", "tmux -V", "apt:tmux", "apt:tmux", "apt:tmux"),
            new ToolDef("sqlite3", "terminal", "sqlite3 --version", "apt:sqlite3", "apt:sqlite3", "apt:sqlite3"),

            new ToolDef("nano", "editors", "nano --version", "apt:nano"),
            new ToolDef("vim", "editors", "vim --version", "apt:vim", "apt:vim", "apt:vim"),
            new ToolDef("nvim", "editors", "nvim --version", "apt:neovim", "apt:neovim", "apt:neovim"),
        };

        private static int GroupSeries(string group) { foreach (var g in ToolGroups) if (g.id == group) return g.series; return 2; }

        private readonly List<ToolInfo> _tools = new List<ToolInfo>();
        private readonly Dictionary<string, string> _sysCounts = new Dictionary<string, string>();
        private UiTabs _sysTabs;
        private UiToolGrid _toolGrid;
        private UiLabel _sysToolsInfo, _sysStoreInfo;
        private UiStorageBar _storeDevice, _storeLinux;
        private UiList _storeList;
        private UiSlider _storeLimit;
        private bool _toolsBusy, _storeBusy;
        private DateTime _toolsAt, _storeAt;
        private long _linuxBytes = -1;

        public IReadOnlyList<ToolInfo> ToolsForTest => _tools;
        public bool SystemBusyForTest => _toolsBusy || _storeBusy;
        public long LinuxBytesForTest => _linuxBytes;
        private string ToolsCachePath => Path.Combine(Application.persistentDataPath, "system-tools.json");

        private void BuildSystem()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("system", "System · Linux", 0.66f, 0.60f);
            var c = p.Content;
            _sysTabs = p.Add(new UiTabs("TOOLS", "STORAGE") { Rect = new Rect(c.x, c.y, Mathf.Min(c.width, 0.3f), t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);

            var a = new UiStack(area, t.Gap);
            _sysToolsInfo = _sysTabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = a.Next(0.034f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _toolGrid = _sysTabs.AddTo(0, new UiToolGrid { Rect = a.Next(area.yMax - a.Y - t.ButtonHeight - t.Gap), OnPick = ToolPicked });
            TabButtons(_sysTabs, 0, a.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("⟳ CHECK", () => RefreshTools(true)),
                ("PACKAGES…", ShowPackages),
                ("TERMINAL", () => RunInNewTerminal("apt list --installed 2>/dev/null | wc -l; ls ~/.bun/bin ~/.cargo/bin ~/.local/bin 2>/dev/null")),
            }, primary: 0);

            var s = new UiStack(area, t.Gap);
            _storeDevice = _sysTabs.AddTo(1, new UiStorageBar { Rect = s.Next(0.064f) });
            _storeLinux = _sysTabs.AddTo(1, new UiStorageBar { Rect = s.Next(0.064f) });
            _storeLimit = _sysTabs.AddTo(1, new UiSlider("LINUX LIMIT", 0f, 64f, Settings.linuxLimitGB, v => { Settings.linuxLimitGB = Mathf.Round(v); _storeLimit.Set(Settings.linuxLimitGB); OnSettingsChanged(); ShowStorage(); }) { Rect = s.Next(0.03f), Format = "0' GB'", LabelWidth = 0.13f });
            _sysStoreInfo = _sysTabs.AddTo(1, new UiLabel("", t.SmallSize) { Rect = s.Next(0.032f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _storeList = _sysTabs.AddTo(1, new UiList(0.03f, (item, idx) => StoragePicked(item)) { Rect = s.Next(area.yMax - s.Y - t.ButtonHeight - t.Gap) });
            TabButtons(_sysTabs, 1, s.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("⟳ MEASURE", () => RefreshStorage(true)),
                ("CLEAN CACHES", CleanAllCaches),
                ("AI MODELS", () => OpenPanel("voice")),
            }, primary: 0);
            _sysTabs.OnChanged = i => { if (i == 0) RefreshTools(false); else RefreshStorage(false); };
            LoadToolsCache();
            ShowTools();
            ScheduleToolScan();
            ScheduleFirstUse();
        }

        // ------------------------------------------------------------------ tools

        private void LoadToolsCache()
        {
            try
            {
                if (!File.Exists(ToolsCachePath)) return;
                var j = JObject.Parse(File.ReadAllText(ToolsCachePath));
                _tools.Clear();
                // only tools the catalog still has (php, ruby, lua, deno left in 0.7.3), with today's groups
                foreach (var x in j["tools"] as JArray ?? new JArray())
                {
                    var info = x.ToObject<ToolInfo>();
                    var def = ToolCatalog.FirstOrDefault(c => c.Name == info.Name);
                    if (def == null) continue;
                    info.Group = def.Group; info.Category = def.Group; info.Install = def.Install; info.Update = def.Update; info.Remove = def.Remove;
                    _tools.Add(info);
                }
                foreach (var def in ToolCatalog.Where(c => _tools.All(x => x.Name != c.Name)))
                    _tools.Add(new ToolInfo { Name = def.Name, Group = def.Group, Category = def.Group, Install = def.Install, Update = def.Update, Remove = def.Remove });
                _sysCounts.Clear();
                foreach (var p in (j["counts"] as JObject ?? new JObject()).Properties()) _sysCounts[p.Name] = (string)p.Value;
                _toolsAt = (DateTime?)j["at"] ?? DateTime.MinValue;
            }
            catch (Exception ex) { Log("tools cache: " + ex.Message); }
        }

        private void SaveToolsCache()
        {
            try { File.WriteAllText(ToolsCachePath, new JObject { ["at"] = _toolsAt, ["tools"] = JArray.FromObject(_tools), ["counts"] = JObject.FromObject(_sysCounts) }.ToString()); }
            catch (Exception ex) { Log("tools cache: " + ex.Message); }
        }

        /// One shell run checks every known tool (path + version) and counts packages.
        public void RefreshTools(bool force)
        {
            if (_toolsBusy || _toolGrid == null) return;
            if (!force && _tools.Count > 0 && (DateTime.Now - _toolsAt).TotalMinutes < 10) { ShowTools(); return; }
            _toolsBusy = true;
            ShowTools();
            // no backslashes in the script: on the PC it goes through wsl.exe's command line, which mangles them
            var sb = new StringBuilder();
            sb.Append("renice -n 10 $$ >/dev/null 2>&1; "); // low priority: the app renders on the same cores
            sb.Append("export PATH=\"").Append(string.Join(":", KnownPathAdds)).Append(":$PATH\"; ");
            foreach (var tool in ToolCatalog)
            {
                // a command on PATH, or (installers that do not put one there) a file that exists once installed
                string probe = tool.Check.StartsWith("/") ? $"p={tool.Check}; [ -e \"$p\" ]" : $"p=$(command -v {tool.Check} 2>/dev/null)";
                sb.Append($"{probe} && echo \"T|{tool.Name}|$p|$(timeout 6 {tool.Ver} 2>&1 | head -1 | tr -d '|')\"; ");
            }
            sb.Append("echo \"C|apt|$(dpkg-query -W 2>/dev/null | wc -l)\"; ");
            sb.Append("echo \"C|manual|$(apt-mark showmanual 2>/dev/null | wc -l)\"; ");
            sb.Append("command -v npm >/dev/null && echo \"C|npm|$(ls \"$(npm root -g 2>/dev/null)\" 2>/dev/null | wc -l)\"; ");
            sb.Append("echo \"O|$(. /etc/os-release 2>/dev/null; echo $PRETTY_NAME) $(uname -m)\"; true");
            string script = sb.ToString();
            Task.Run(async () =>
            {
                var r = await Git.Shell(script, 120000);
                _main.Enqueue(() =>
                {
                    _toolsBusy = false;
                    ParseTools(r.output ?? "");
                    _toolsAt = DateTime.Now;
                    SaveToolsCache();
                    WriteToolsInventory();
                    ShowTools();
                });
            });
        }

        private static readonly Regex VersionRe = new Regex(@"\d+\.\d+(?:\.\d+)?(?:[-+.][0-9A-Za-z]+)*", RegexOptions.Compiled);

        public void ParseTools(string output)
        {
            var found = new Dictionary<string, (string path, string raw)>();
            _sysCounts.Clear();
            foreach (var line in output.Replace("\r", "").Split('\n'))
            {
                var f = line.Split('|');
                if (f.Length >= 4 && f[0] == "T") found[f[1]] = (f[2], string.Join("|", f.Skip(3)));
                else if (f.Length >= 3 && f[0] == "C") _sysCounts[f[1]] = f[2].Trim();
                else if (f.Length >= 2 && f[0] == "O") _sysCounts["os"] = f[1].Trim();
            }
            _tools.Clear();
            foreach (var tool in ToolCatalog)
            {
                found.TryGetValue(tool.Name, out var hit);
                var m = hit.raw != null ? VersionRe.Match(hit.raw) : Match.Empty;
                if (hit.raw != null && !m.Success) m = Regex.Match(hit.raw, @"\b\d+\b"); // java -version: openjdk version "21" 
                _tools.Add(new ToolInfo
                {
                    Name = tool.Name, Category = tool.Group, Group = tool.Group, Install = tool.Install, Update = tool.Update, Remove = tool.Remove,
                    Found = hit.path != null, Path = hit.path, Raw = hit.raw, Version = m.Success ? m.Value : hit.raw != null ? Cut(hit.raw, 16) : null,
                });
            }
        }

        private void ShowTools()
        {
            if (_toolGrid == null) return;
            var t = UiTheme.Current;
            _toolGrid.Tiles.Clear();
            if (_toolGrid.Groups.Count == 0)
                foreach (var g in ToolGroups) _toolGrid.Groups.Add(new UiToolGrid.Group { Id = g.id, Title = g.title, Blurb = g.blurb, Tint = UiViz.Series(g.series) });
            // the catalog's order inside each group: a tool keeps its place whether installed or not
            foreach (var tool in _tools.OrderBy(x => Array.FindIndex(ToolCatalog, c => c.Name == x.Name)))
            {
                string group = tool.Group ?? ToolCatalog.FirstOrDefault(c => c.Name == tool.Name)?.Group;
                _toolGrid.Tiles.Add(new UiToolGrid.Tile { Name = tool.Name, Version = tool.Version, Category = group, Group = group, Glyph = "✦", Tint = UiViz.Series(GroupSeries(group)), Found = tool.Found, Tag = tool, Hint = tool.Install == null ? "not found" : tool.Install == "agents" ? "+ in AGENTS" : "+ install" });
            }
            _toolGrid.Redraw();
            int n = _tools.Count(x => x.Found);
            string counts = string.Join(" · ", new[]
            {
                _sysCounts.TryGetValue("os", out var os) ? os : null,
                _sysCounts.TryGetValue("apt", out var apt) ? apt + " Debian packages" + (_sysCounts.TryGetValue("manual", out var man) ? $" ({man} chosen)" : "") : null,
                _sysCounts.TryGetValue("npm", out var npm) ? npm + " npm globals" : null,
            }.Where(x => !string.IsNullOrEmpty(x)));
            _sysToolsInfo.Set(_toolsBusy ? "checking the Linux userland … (versions of " + ToolCatalog.Length + " tools)" :
                _tools.Count == 0 ? "Not checked yet: ⟳ CHECK." :
                $"<b>{n}</b> of {_tools.Count} known tools installed · {counts}\nchecked {(_toolsAt == DateTime.MinValue ? "?" : _toolsAt.ToString("HH:mm"))} · tap a tile to install, update or remove it", t.TextDim);
        }

        /// What agents read instead of probing with commands: /mnt/quest/system/tools.json (the app's shared
        /// folder), rewritten after every scan; also `mcx-ui system tools`.
        public string ToolsInventoryPath
        {
            get
            {
                var host = app != null ? app.Host : null;
                string shared = host != null && !string.IsNullOrEmpty(host.SharedDir) ? host.SharedDir : Application.persistentDataPath;
                return Path.Combine(shared, "system", "tools.json");
            }
        }

        public JObject ToolsInventory() => new JObject
        {
            ["updated"] = _toolsAt == DateTime.MinValue ? null : _toolsAt.ToUniversalTime().ToString("o"),
            ["os"] = _sysCounts.TryGetValue("os", out var os) ? os : null,
            ["debianPackages"] = _sysCounts.TryGetValue("apt", out var apt) ? apt : null,
            ["debianChosen"] = _sysCounts.TryGetValue("manual", out var man) ? man : null,
            ["npmGlobals"] = _sysCounts.TryGetValue("npm", out var npm) ? npm : null,
            ["note"] = "Kept current by MetaCodex (SYSTEM > TOOLS). Install missing tools there or with the command in `install`.",
            ["tools"] = new JArray(_tools.Select(x => new JObject
            {
                ["name"] = x.Name, ["installed"] = x.Found, ["version"] = x.Version, ["path"] = x.Path, ["group"] = x.Group ?? x.Category,
                ["install"] = x.Found ? null : Cmd(x.Install, "install"),
            })),
        };

        private void WriteToolsInventory()
        {
            try
            {
                string p = ToolsInventoryPath;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, ToolsInventory().ToString());
            }
            catch (Exception ex) { Log("tools inventory: " + ex.Message); }
        }

        /// `system …` from Linux (mcx-ui system …).
        public string SystemCommand(string arg)
        {
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, 2);
            switch (parts[0])
            {
                case "tools":
                    if (parts.Length > 1 && parts[1].Trim() == "json") return ToolsInventory().ToString(Newtonsoft.Json.Formatting.None);
                    if (_tools.Count == 0) return "not scanned yet: mcx-ui system refresh";
                    return string.Join("\n", _tools.Select(x => $"{x.Name}\t{(x.Found ? x.Version ?? "?" : "-")}\t{(x.Found ? x.Path : "missing: " + (Cmd(x.Install, "install") ?? "comes with the system"))}"));
                case "refresh": RefreshTools(true); return "ok, scanning (results in a few seconds: mcx-ui system tools)";
                case "storage":
                    return _linuxBytes < 0 ? "not measured yet: open SYSTEM > STORAGE" :
                        $"linux {UiStorageBar.Size(_linuxBytes)}, free {UiStorageBar.Size(_devFree)} of {UiStorageBar.Size(_devTotal)}" + (Settings.linuxLimitGB > 0 ? $", limit {Settings.linuxLimitGB:0} GB" : "");
                case "inventory": return ToolsInventoryPath;
                // apps built in the Linux userland: Android's own installer, the user confirms (MetaCodexInstaller)
                case "install-apk":
                {
                    string linuxPath = parts.Length > 1 ? parts[1].Trim() : "";
                    if (!linuxPath.EndsWith(".apk")) return "error: system install-apk <path to an .apk in Linux>";
                    string androidPath = AndroidPathOf(linuxPath);
                    if (androidPath == null || !File.Exists(androidPath)) return "error: no file " + linuxPath + (androidPath != null ? " (" + androidPath + ")" : "");
                    Log("install-apk " + linuxPath + " -> " + androidPath);
                    return SystemBridge.InstallApk(androidPath);
                }
                case "install-status": return SystemBridge.InstallStatus();
                case "can-install": return SystemBridge.CanInstall();
                case "app": return parts.Length > 1 ? SystemBridge.AppInfo(parts[1].Trim()) : "error: system app <package>";
                case "launch": return parts.Length > 1 ? SystemBridge.LaunchApp(parts[1].Trim()) : "error: system launch <package>";
                case "first-use": return FirstUseStatus;
                default: return "error: system tools [json] | refresh | storage | inventory | install-apk <apk> | install-status | can-install | app <package> | launch <package> | first-use";
            }
        }

        /// A file of the Linux userland as the app sees it: /mnt/quest is the shared folder, the rest is in the rootfs.
        public string AndroidPathOf(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith("/") || linuxPath.Contains("/../")) return null;
            var host = app != null ? app.Host : null;
            if (linuxPath.StartsWith("/mnt/quest/"))
            {
                string shared = host != null && !string.IsNullOrEmpty(host.SharedDir) ? host.SharedDir : Application.persistentDataPath;
                return Path.Combine(shared, linuxPath.Substring("/mnt/quest/".Length));
            }
            return host != null ? host.RootfsDir + linuxPath : null;
        }

        // ------------------------------------------------------------------ first use
        /// Bump when the first-use setup installs more: headsets that ran an older one run it once more.
        public const int FirstUseVersion = 1;
        public const string FirstUseLog = "/root/.metacodex/first-use.log";
        /// What the app installs in the Linux by itself, once, at low priority on cores 0-2: base tools, Node, and
        /// everything to build native Quest apps (APK tools, JDK, NDK, android.jar, OpenXR; skill quest-native-apps).
        public const string FirstUseCommand =
            "mkdir -p /root/.metacodex && T=; taskset -c 0-2 true 2>/dev/null && T='taskset -c 0-2'; " +
            "nice -n 10 $T mcx-setup quest > " + FirstUseLog + " 2>&1; echo \"MCX_EXIT=$?\""; // taskset: only where cores 0-2 are allowed
        public string FirstUseStatus { get; private set; } = "not started";
        private bool _firstUseRunning;

        /// The marker lives in the rootfs: a new Linux install runs the setup again.
        private string FirstUseMarker => app?.Host != null ? app.Host.RootfsDir + "/root/.metacodex/first-use.done" : null;

        /// Once per Linux install (and once more when FirstUseVersion grows): `mcx-setup quest` in the background, so a
        /// new headset is ready for agents and native apps without anyone running setup commands.
        private void ScheduleFirstUse()
        {
            After(20f, () =>
            {
                if (Application.isEditor) { FirstUseStatus = "not in the Editor"; return; }
                if (!Git.InUserland) { ScheduleFirstUse(); return; } // the userland is not up yet
                string marker = FirstUseMarker;
                if (marker == null || _firstUseRunning) return;
                try { if (File.Exists(marker) && File.ReadAllText(marker).Trim() == FirstUseVersion.ToString()) { FirstUseStatus = "done"; return; } }
                catch (Exception ex) { Log("first use: " + ex.Message); }
                _firstUseRunning = true;
                FirstUseStatus = "running: mcx-setup quest (log " + FirstUseLog + ")";
                Toast("Setting up Linux in the background: Node, the Android NDK and OpenXR for native apps (about 400 MB download)", "info");
                var started = DateTime.Now;
                Task.Run(async () =>
                {
                    var r = await Git.Shell(FirstUseCommand, 90 * 60 * 1000);
                    bool ok = (r.output ?? "").Contains("MCX_EXIT=0");
                    _main.Enqueue(() =>
                    {
                        _firstUseRunning = false;
                        int min = (int)Math.Round((DateTime.Now - started).TotalMinutes);
                        if (ok)
                        {
                            try { Directory.CreateDirectory(Path.GetDirectoryName(marker)); File.WriteAllText(marker, FirstUseVersion.ToString()); }
                            catch (Exception ex) { Log("first use marker: " + ex.Message); }
                            FirstUseStatus = $"done ({min} min)";
                            Toast("Linux is set up: Node and the native Quest app tools are ready (mcx-app)", "ok");
                            RefreshTools(true);
                        }
                        else
                        {
                            FirstUseStatus = "failed: see " + FirstUseLog + " (tried again at the next start, or run mcx-setup quest in a terminal)";
                            Toast("Linux setup did not finish (offline?): it runs again at the next start", "warn");
                        }
                        Log("first use: " + FirstUseStatus);
                    });
                });
            });
        }

        /// A while after start, the inventory is refreshed in the background (agents read it), at low priority.
        private void ScheduleToolScan()
        {
            After(45f, () =>
            {
                if (!Git.InUserland && !Application.isEditor) { ScheduleToolScan(); return; } // the userland is not up yet
                if (_tools.Count == 0 || (DateTime.Now - _toolsAt).TotalMinutes > 30) RefreshTools(true);
                else WriteToolsInventory();
            });
        }

        private static string Cmd(string spec, string verb)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (!spec.StartsWith("apt:")) return spec;
            string pkgs = spec.Substring(4);
            return verb == "install" ? $"nice -n 10 apt-get update && nice -n 10 apt-get install -y {pkgs}"
                 : verb == "update" ? $"nice -n 10 apt-get update && nice -n 10 apt-get install --only-upgrade -y {pkgs}"
                 : $"apt-get remove -y {pkgs}";
        }

        private void ToolPicked(UiToolGrid.Tile tile)
        {
            if (!(tile?.Tag is ToolInfo tool)) return;
            var items = new List<UiListItem>();
            if (!tool.Found && tool.Install == "agents") items.Add(new UiListItem { Icon = "◆", Title = "Install from AGENTS", Sub = "the provider's own installer", Tag = "agents" });
            else if (!tool.Found && tool.Install != null) items.Add(new UiListItem { Icon = "⇣", Title = "Install " + tool.Name, Sub = Cut(Cmd(tool.Install, "install"), 60), Tag = "install" });
            if (tool.Found && tool.Update != null) items.Add(new UiListItem { Icon = "⟳", Title = "Update", Sub = Cut(Cmd(tool.Update, "update"), 60), Tag = "update" });
            if (tool.Found) items.Add(new UiListItem { Icon = "▶", Title = "Show in a terminal", Sub = tool.Path, Tag = "show" });
            if (tool.Found && tool.Remove != null) items.Add(new UiListItem { Icon = "✕", Title = "Remove", Sub = Cut(Cmd(tool.Remove, "remove"), 60), TitleColor = UiTheme.Current.Danger, Tag = "remove" });
            if (items.Count == 0) { Toast(tool.Name + (tool.Found ? " " + tool.Version + " · " + tool.Path : ": comes with the system"), "info"); return; }
            ShowPicker(tool.Name, tool.Found ? $"{tool.Raw} · {tool.Path}" : "Not installed. Installing runs in a new terminal at low priority; it may take a few minutes.", items, item =>
            {
                string what = item.Tag as string;
                string cmd = what == "install" ? Cmd(tool.Install, "install") : what == "update" ? Cmd(tool.Update, "update") : what == "remove" ? Cmd(tool.Remove, "remove") : null;
                if (what == "agents") { OpenPanel("agents"); return; }
                if (what == "show") { var def = ToolCatalog.First(c => c.Name == tool.Name); cmd = (def.Check.StartsWith("/") ? $"ls -la {def.Check}; " : $"command -v {def.Check}; ") + def.Ver; }
                if (cmd == null) return;
                if (what == "remove")
                {
                    Modal("REMOVE " + tool.Name.ToUpperInvariant(), "Run: " + cmd, new[] { "REMOVE", "CANCEL" }, i => { if (i == 0 && RunInNewTerminal(cmd + " ; echo; echo '(done: ⟳ CHECK in SYSTEM)'")) _toolsAt = DateTime.MinValue; });
                    return;
                }
                if (RunInNewTerminal(cmd + " ; echo; echo '(done: ⟳ CHECK in SYSTEM)'")) _toolsAt = DateTime.MinValue;
            }, PanelsById["system"]);
        }

        private void ShowPackages()
        {
            Task.Run(async () =>
            {
                var r = await Git.Shell("apt-mark showmanual 2>/dev/null | head -300; echo '--'; command -v npm >/dev/null && ls \"$(npm root -g 2>/dev/null)\" 2>/dev/null", 30000);
                _main.Enqueue(() =>
                {
                    var parts = (r.output ?? "").Replace("\r", "").Split(new[] { "--\n" }, 2, StringSplitOptions.None);
                    var apt = parts[0].Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    var npm = parts.Length > 1 ? parts[1].Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();
                    var items = npm.Select(x => new UiListItem { Icon = "▣", IconColor = UiViz.Series(2), Title = x, Sub = "npm global", Tag = "npm:" + x })
                        .Concat(apt.Select(x => new UiListItem { Icon = "▣", IconColor = UiViz.Series(0), Title = x, Sub = "Debian, chosen", Tag = "apt:" + x })).ToList();
                    ShowPicker("Packages", $"{apt.Count} Debian packages you (or setup) chose, {npm.Count} npm globals. Tap one to see it in a terminal.", items, it =>
                    {
                        string tag = it.Tag as string ?? "";
                        RunInNewTerminal(tag.StartsWith("npm:") ? $"npm ls -g --depth=0 {GitService.Quote(tag.Substring(4))}" : $"apt show {GitService.Quote(tag.Substring(4))} 2>/dev/null | head -20");
                    }, PanelsById["system"]);
                });
            });
        }

        // ------------------------------------------------------------------ storage

        private sealed class StorePart { public string Name, LinuxPath, Clean; public long Bytes; public bool Cache; }
        private readonly List<StorePart> _storeParts = new List<StorePart>();
        private long _devTotal, _devFree, _aiBytes, _sharedBytes;

        private static readonly (string name, string path, string clean)[] LinuxParts =
        {
            ("system (Debian)", "/usr", null),
            ("home: projects + tools", "/root", null),
            ("apt cache", "/var/cache/apt", "apt-get clean"),
            ("npm cache", "/root/.npm", "npm cache clean --force"),
            ("bun cache", "/root/.bun/install/cache", "rm -rf ~/.bun/install/cache"),
            ("cache (pip, uv, …)", "/root/.cache", "rm -rf ~/.cache/*"),
            ("cargo downloads", "/root/.cargo/registry", "rm -rf ~/.cargo/registry/cache ~/.cargo/registry/src"),
            ("rust toolchains", "/root/.rustup", null),
            ("MQH data", "/root/.mqh", null),
            ("temp", "/tmp", "find /tmp -mindepth 1 -mtime +1 -delete 2>/dev/null; echo cleaned"),
            ("/var (logs, lists)", "/var", null),
            ("/opt", "/opt", null),
        };

        public void RefreshStorage(bool force)
        {
            if (_storeBusy || _storeList == null) return;
            if (!force && _linuxBytes >= 0 && (DateTime.Now - _storeAt).TotalMinutes < 5) { ShowStorage(); return; }
            _storeBusy = true;
            ShowStorage();
            var host = app != null ? app.Host : null;
            string rootfs = host != null && host.State == MetaCodex.Linux.LinuxHost.HostState.Ready ? host.RootfsDir : null;
            string shared = host?.SharedDir;
            string ai = Qvac?.DataDir;
            var files = Files;
            var deviceProbe = DeviceSpace();
            // a low-priority thread: walking the rootfs is long, and the frame loop shares the cores
            var worker = new System.Threading.Thread(() =>
            {
                var parts = new List<StorePart>();
                long linux = 0;
                try
                {
                    string Base(string linuxPath) => rootfs != null ? rootfs + linuxPath : files.HostPath(linuxPath);
                    // each top-level folder once (the total), the caches inside them on their own
                    var top = new Dictionary<string, long>();
                    if (rootfs != null && Directory.Exists(rootfs))
                    {
                        foreach (var d in new DirectoryInfo(rootfs).EnumerateDirectories())
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0 || d.Name == "proc" || d.Name == "sys" || d.Name == "dev" || d.Name == "mnt") continue;
                            top["/" + d.Name] = DirSize(d.FullName);
                        }
                        foreach (var f in new DirectoryInfo(rootfs).EnumerateFiles()) linux += f.Length;
                    }
                    else foreach (var lp in LinuxParts.Where(x => x.path.Count(ch => ch == '/') == 1)) top[lp.path] = DirSize(Base(lp.path));
                    linux += top.Values.Sum();
                    var sizes = new Dictionary<string, long>();
                    foreach (var lp in LinuxParts) sizes[lp.path] = top.TryGetValue(lp.path, out long tb) ? tb : DirSize(Base(lp.path));
                    foreach (var lp in LinuxParts)
                    {
                        long b = sizes[lp.path];
                        // nested parts are shown on their own: home without its caches, /var without the apt cache
                        if (lp.path == "/root") b -= LinuxParts.Where(x => x.path.StartsWith("/root/")).Sum(x => sizes[x.path]);
                        if (lp.path == "/var") b -= sizes["/var/cache/apt"];
                        parts.Add(new StorePart { Name = lp.name, LinuxPath = lp.path, Clean = lp.clean, Bytes = Math.Max(0, b), Cache = lp.clean != null });
                    }
                    long listed = LinuxParts.Where(x => x.path.Count(ch => ch == '/') == 1).Sum(x => top.TryGetValue(x.path, out long v) ? v : 0);
                    long rest = linux - listed;
                    if (rest > 0) parts.Add(new StorePart { Name = "other system files", LinuxPath = "/etc, /lib, …", Bytes = rest });
                }
                catch (Exception ex) { _main.Enqueue(() => Log("storage: " + ex.Message)); }
                long aiBytes = ai != null ? DirSize(ai) : 0, sharedBytes = shared != null ? DirSize(shared) : 0;
                _main.Enqueue(() =>
                {
                    _storeBusy = false;
                    _storeParts.Clear();
                    _storeParts.AddRange(parts.OrderByDescending(x => x.Bytes));
                    _linuxBytes = linux;
                    _aiBytes = aiBytes;
                    _sharedBytes = sharedBytes;
                    (_devTotal, _devFree) = deviceProbe;
                    _storeAt = DateTime.Now;
                    ShowStorage();
                    CheckStorageLimits(false);
                });
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.Lowest, Name = "mcx-storage" };
            worker.Start();
        }

        /// Bytes under a folder: files only, symlinks not followed (the rootfs links into /proc and the like).
        public static long DirSize(string dir)
        {
            long total = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(dir));
            int guard = 0;
            while (stack.Count > 0 && guard++ < 400000)
            {
                var d = stack.Pop();
                try
                {
                    foreach (var fsi in d.EnumerateFileSystemInfos())
                    {
                        if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((fsi.Attributes & FileAttributes.Directory) != 0) stack.Push((DirectoryInfo)fsi);
                        else if (fsi is FileInfo fi) total += fi.Length;
                    }
                }
                catch (Exception) { }
            }
            return total;
        }

        /// The headset's user storage: total and free bytes (Android StatFs; the Editor's drive).
        private static (long total, long free) DeviceSpace()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var stat = new AndroidJavaObject("android.os.StatFs", Application.persistentDataPath))
                    return (stat.Call<long>("getTotalBytes"), stat.Call<long>("getAvailableBytes"));
            }
            catch (Exception) { return (0, 0); }
#else
            try { var d = new DriveInfo(Path.GetPathRoot(Application.dataPath)); return (d.TotalSize, d.AvailableFreeSpace); }
            catch (Exception) { return (0, 0); }
#endif
        }

        private void ShowStorage()
        {
            if (_storeList == null) return;
            var t = UiTheme.Current;
            long gb = 1L << 30;
            long limit = (long)(Settings.linuxLimitGB * gb);
            _storeDevice.Parts.Clear();
            if (_linuxBytes >= 0)
            {
                _storeDevice.Parts.Add(("Linux", _linuxBytes));
                _storeDevice.Parts.Add(("AI models", _aiBytes));
                _storeDevice.Parts.Add(("shared folder", _sharedBytes));
                long other = Math.Max(0, _devTotal - _devFree - _linuxBytes - _aiBytes - _sharedBytes);
                if (_devTotal > 0) _storeDevice.Parts.Add(("apps + system", other));
            }
            _storeDevice.Total = _devTotal;
            _storeDevice.Limit = 0;
            _storeDevice.Caption = _devTotal > 0 ? $"<b>Headset</b>  {UiStorageBar.Size(_devFree)} free of {UiStorageBar.Size(_devTotal)}" : "<b>Headset</b>  measuring …";
            _storeDevice.Redraw();
            _storeLinux.Parts.Clear();
            foreach (var p in _storeParts.Where(x => x.Bytes > 0).Take(8)) _storeLinux.Parts.Add((p.Name, p.Bytes));
            _storeLinux.Total = Math.Max(_linuxBytes, limit);
            _storeLinux.Limit = limit;
            _storeLinux.Caption = _linuxBytes < 0 ? (_storeBusy ? "<b>Linux</b>  measuring the userland …" : "<b>Linux</b>  ⟳ MEASURE") : $"<b>Linux</b>  {UiStorageBar.Size(_linuxBytes)}" + (limit > 0 ? $" of a {Settings.linuxLimitGB:0} GB limit" : "");
            _storeLinux.Redraw();
            bool over = limit > 0 && _linuxBytes > limit, low = _devTotal > 0 && _devFree < 2L * gb;
            _sysStoreInfo.Set(_storeBusy ? "measuring …" :
                over ? $"<color=#{UiViz.Hex(t.Warn)}>Linux is past your limit:</color> clean the caches below, or remove tools / projects." :
                low ? $"<color=#{UiViz.Hex(t.Warn)}>The headset is almost full ({UiStorageBar.Size(_devFree)} free).</color>" :
                "The limit is a soft one: Android cannot reserve or cap an app's space, so the app warns you when Linux grows past it. Tap a cache to clean it.", t.TextDim);
            _storeList.SetItems(_storeParts.Select(p => new UiListItem
            {
                Icon = p.Cache ? "⇣" : "▣",
                IconColor = p.Cache ? t.Warn : t.TextDim,
                Title = p.Name,
                Sub = p.LinuxPath,
                Badge = UiStorageBar.Size(p.Bytes) + (p.Cache && p.Bytes > 0 ? " · CLEAN" : ""),
                BadgeColor = p.Cache && p.Bytes > 64L << 20 ? t.Warn : t.TextDim,
                Tag = p,
            }), keepScroll: true);
        }

        private void StoragePicked(UiListItem item)
        {
            if (!(item.Tag is StorePart p)) return;
            if (p.Clean == null) { RunInNewTerminal($"du -h -d 1 {GitService.Quote(p.LinuxPath)} 2>/dev/null | sort -h | tail -15"); return; }
            Modal("CLEAN " + p.Name.ToUpperInvariant(), $"Free about {UiStorageBar.Size(p.Bytes)}: {p.Clean}", new[] { "CLEAN", "CANCEL" }, i =>
            {
                if (i == 0 && RunInNewTerminal(p.Clean + " ; echo '(done: ⟳ MEASURE in SYSTEM)'")) _storeAt = DateTime.MinValue;
            });
        }

        private void CleanAllCaches()
        {
            var cmds = LinuxParts.Where(x => x.clean != null).Select(x => x.clean).ToList();
            long sum = _storeParts.Where(x => x.Cache).Sum(x => x.Bytes);
            Modal("CLEAN CACHES", $"Package and download caches{(sum > 0 ? " (about " + UiStorageBar.Size(sum) + ")" : "")}: they refill when needed. Projects, tools and settings stay.", new[] { "CLEAN", "CANCEL" }, i =>
            {
                if (i == 0 && RunInNewTerminal(string.Join(" ; ", cmds) + " ; echo '(done: ⟳ MEASURE in SYSTEM)'")) _storeAt = DateTime.MinValue;
            });
        }

        /// A toast when Linux passed the soft limit or the headset is almost full (also checked once after start).
        private void CheckStorageLimits(bool atStart)
        {
            long gb = 1L << 30;
            if (Settings.linuxLimitGB > 0 && _linuxBytes > Settings.linuxLimitGB * gb) Toast($"Linux uses {UiStorageBar.Size(_linuxBytes)}, past your {Settings.linuxLimitGB:0} GB limit · SYSTEM > STORAGE", "warn");
            else if (_devTotal > 0 && _devFree < 2L * gb) Toast($"The headset is almost full: {UiStorageBar.Size(_devFree)} free · SYSTEM > STORAGE", "warn");
            else if (atStart) Log($"storage: Linux {UiStorageBar.Size(_linuxBytes)}, free {UiStorageBar.Size(_devFree)}");
        }
    }
}
