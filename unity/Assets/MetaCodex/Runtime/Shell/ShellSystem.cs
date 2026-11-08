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
        public sealed class ToolInfo { public string Name, Category, Install, Update, Remove, Path, Version, Raw; public bool Found; }

        private static readonly string[] KnownPathAdds = { "$HOME/.bun/bin", "$HOME/.cargo/bin", "$HOME/.deno/bin", "$HOME/.local/bin", "$HOME/go/bin", "/usr/local/go/bin", "/usr/local/bin" };

        // name, command line for the version, category, install, update, remove
        private static readonly (string name, string ver, string cat, string install, string update, string remove)[] ToolCatalog =
        {
            ("git", "git --version", "vcs", "apt:git", "apt:git", "apt:git"),
            ("gh", "gh --version", "vcs", "apt:gh", "apt:gh", "apt:gh"),
            ("node", "node --version", "runtime", "mcx-setup", "mcx-setup", null),
            ("npm", "npm --version", "pkg", "mcx-setup", "npm install -g npm", null),
            ("pnpm", "pnpm --version", "pkg", "npm install -g pnpm", "npm install -g pnpm", "npm uninstall -g pnpm"),
            ("bun", "bun --version", "runtime", "apt-get install -y unzip && curl -fsSL https://bun.sh/install | bash", "bun upgrade", "rm -rf ~/.bun"),
            ("deno", "deno --version", "runtime", "apt-get install -y unzip && curl -fsSL https://deno.land/install.sh | sh", "deno upgrade", "rm -rf ~/.deno"),
            ("python3", "python3 --version", "runtime", "apt:python3 python3-pip python3-venv", "apt:python3", null),
            ("pip", "pip3 --version", "pkg", "apt:python3-pip", "apt:python3-pip", "apt:python3-pip"),
            ("uv", "uv --version", "pkg", "curl -LsSf https://astral.sh/uv/install.sh | sh", "uv self update", "rm -f ~/.local/bin/uv ~/.local/bin/uvx"),
            ("rustc", "rustc --version", "compiler", "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y", "rustup update", "rustup self uninstall -y"),
            ("cargo", "cargo --version", "pkg", "curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y", "rustup update", null),
            ("go", "go version", "compiler", "apt:golang", "apt:golang", "apt:golang"),
            ("gcc", "gcc --version", "compiler", "apt:build-essential", "apt:build-essential", null),
            ("clang", "clang --version", "compiler", "apt:clang", "apt:clang", "apt:clang"),
            ("make", "make --version", "build", "apt:make", "apt:make", null),
            ("cmake", "cmake --version", "build", "apt:cmake", "apt:cmake", "apt:cmake"),
            ("java", "java -version", "runtime", "apt:default-jdk-headless", "apt:default-jdk-headless", "apt:default-jdk-headless"),
            ("ruby", "ruby --version", "runtime", "apt:ruby", "apt:ruby", "apt:ruby"),
            ("php", "php --version", "runtime", "apt:php-cli", "apt:php-cli", "apt:php-cli"),
            ("lua", "lua -v", "runtime", "apt:lua5.4", "apt:lua5.4", "apt:lua5.4"),
            ("sqlite3", "sqlite3 --version", "data", "apt:sqlite3", "apt:sqlite3", "apt:sqlite3"),
            ("jq", "jq --version", "tool", "apt:jq", "apt:jq", "apt:jq"),
            ("rg", "rg --version", "tool", "apt:ripgrep", "apt:ripgrep", "apt:ripgrep"),
            ("fd", "fdfind --version", "tool", "apt:fd-find", "apt:fd-find", "apt:fd-find"),
            ("fzf", "fzf --version", "tool", "apt:fzf", "apt:fzf", "apt:fzf"),
            ("tmux", "tmux -V", "tool", "apt:tmux", "apt:tmux", "apt:tmux"),
            ("vim", "vim --version", "editor", "apt:vim", "apt:vim", "apt:vim"),
            ("nano", "nano --version", "editor", "apt:nano", "apt:nano", null),
            ("curl", "curl --version", "net", "apt:curl", "apt:curl", null),
            ("wget", "wget --version", "net", "apt:wget", "apt:wget", "apt:wget"),
            ("ssh", "ssh -V", "net", "apt:openssh-client", "apt:openssh-client", "apt:openssh-client"),
            ("claude", "claude --version", "agent", "agents", null, null),
            ("codex", "codex --version", "agent", "agents", null, null),
            ("opencode", "opencode --version", "agent", "agents", null, null),
            ("mqh", "mqh --version", "agent", null, null, null),
        };

        private static (string glyph, int series) CategoryLook(string cat)
        {
            switch (cat)
            {
                case "runtime": return ("λ", 0);
                case "pkg": return ("▣", 2);
                case "compiler": return ("⚙", 1);
                case "build": return ("⚒", 3);
                case "vcs": return ("⎇", 4);
                case "data": return ("⛁", 6);
                case "editor": return ("✎", 5);
                case "net": return ("⇅", 7);
                case "agent": return ("◆", 6);
                default: return ("✦", 2);
            }
        }

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
        }

        // ------------------------------------------------------------------ tools

        private void LoadToolsCache()
        {
            try
            {
                if (!File.Exists(ToolsCachePath)) return;
                var j = JObject.Parse(File.ReadAllText(ToolsCachePath));
                _tools.Clear();
                foreach (var x in j["tools"] as JArray ?? new JArray()) _tools.Add(x.ToObject<ToolInfo>());
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
                string bin = tool.ver.Split(' ')[0];
                sb.Append($"p=$(command -v {bin} 2>/dev/null) && echo \"T|{tool.name}|$p|$(timeout 6 {tool.ver} 2>&1 | head -1 | tr -d '|')\"; ");
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
                found.TryGetValue(tool.name, out var hit);
                var m = hit.raw != null ? VersionRe.Match(hit.raw) : Match.Empty;
                if (hit.raw != null && !m.Success) m = Regex.Match(hit.raw, @"\b\d+\b"); // java -version: openjdk version "21" 
                _tools.Add(new ToolInfo
                {
                    Name = tool.name, Category = tool.cat, Install = tool.install, Update = tool.update, Remove = tool.remove,
                    Found = hit.path != null, Path = hit.path, Raw = hit.raw, Version = m.Success ? m.Value : hit.raw != null ? Cut(hit.raw, 16) : null,
                });
            }
        }

        private void ShowTools()
        {
            if (_toolGrid == null) return;
            var t = UiTheme.Current;
            _toolGrid.Tiles.Clear();
            foreach (var tool in _tools.OrderBy(x => x.Found ? 0 : 1).ThenBy(x => Array.FindIndex(ToolCatalog, c => c.name == x.Name)))
            {
                var (glyph, series) = CategoryLook(tool.Category);
                _toolGrid.Tiles.Add(new UiToolGrid.Tile { Name = tool.Name, Version = tool.Version, Category = tool.Category, Glyph = glyph, Tint = UiViz.Series(series), Found = tool.Found, Tag = tool, Hint = tool.Install == null ? "not found" : tool.Install == "agents" ? "+ in AGENTS" : "+ install" });
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
                if (what == "show") cmd = $"command -v {tool.Name}; {ToolCatalog.First(c => c.name == tool.Name).ver}";
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
