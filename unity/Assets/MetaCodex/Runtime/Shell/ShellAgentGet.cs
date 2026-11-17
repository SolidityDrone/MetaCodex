using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// AGENTS > GET (owner 2026-11-10: "a way to download Opencode, Claude Code, Codex, More (coming soon) disabled"),
    /// and AGENTS > API KEY ("I will provide my key but I need an easy way to paste it in, avoiding the terminal").
    /// GET installs the agent's CLI into this headset's Linux from its official npm package, in a terminal you can
    /// watch (npm 11 runs install scripts only when allowed: --allow-scripts names the package). API KEY seals a
    /// key in the vault (policy allow: agents use it without asking) and puts it into MQH's machine environment
    /// (encrypted there), so the agents MQH starts find it: OpenCode reads ANTHROPIC_API_KEY, OPENAI_API_KEY,
    /// OPENROUTER_API_KEY, OPENCODE_API_KEY, GEMINI_API_KEY, XAI_API_KEY and DEEPSEEK_API_KEY from the environment
    /// (seen with `opencode auth list` on the headset). These are API keys the user owns; Claude subscription
    /// logins stay with Claude Code's own `claude auth login` (MetaCodex never reads or keeps them).
    ///
    /// Claude Code threads in MQH (2026-11-17): MQH's own Claude provider plugin bundles Anthropic's proprietary Agent
    /// SDK, so it is never in the APK (tools/mqh-bundle.sh removes it). MQH runs Claude Code through its ACP provider
    /// instead, the way it runs OpenCode: the ACP adapter `@agentclientprotocol/claude-agent-acp` (Apache-2.0, bin
    /// `claude-agent-acp`) is installed BY THE USER from the official npm registry in a terminal they watch (GET installs
    /// it with the CLI; ENABLE THREADS adds it to a CLI installed before), so npm downloads Anthropic's SDK straight to the
    /// user's headset and MetaCodex ships none of it. The adapter runs on the CLI's own login (`claude auth login`, its own
    /// files: MetaCodex never reads them). Once the probe finds the bin, EnsureClaudeThreads adds it to provider-acp's
    /// own `customAgents` setting (MQH's plugin settings API): provider `acp-claude-code`, display name "Claude Code",
    /// command `claude-agent-acp` from the PATH (never `npx -y`, which would fetch it again at every start).
    public sealed partial class MetaCodexShell
    {
        public sealed class AgentDef
        {
            public string Id, Name, Command, Package, Note;
            /// The npm package (pinned) and bin that let MQH run this agent's threads (Claude Code: its ACP adapter); null
            /// when MQH runs the CLI itself.
            public string ThreadsPackage, ThreadsCommand;
            public (string label, string env)[] Keys;
        }

        /// The ACP adapter version GET and ENABLE THREADS install (pinned: checked 2026-11-17, npm latest; Node >= 22,
        /// the userland has Node 24; its Agent SDK 0.3.325 has a linux-arm64 glibc build).
        public const string ClaudeAdapterVersion = "0.107.3";
        /// MQH's provider id for Claude Code threads: provider-acp's `acp-<id>` of the customAgents entry below.
        public const string ClaudeThreadsProvider = "acp-claude-code";
        private const string ClaudeThreadsAgentId = "claude-code", ClaudeThreadsBin = "claude-agent-acp";

        public static readonly AgentDef[] GetAgents =
        {
            new AgentDef { Id = "opencode", Name = "OPENCODE", Command = "opencode", Package = "opencode-ai",
                Note = "threads in MQH (ACP) and the terminal",
                Keys = new[] { ("OpenCode Zen / Go", "OPENCODE_API_KEY"), ("Anthropic", "ANTHROPIC_API_KEY"), ("OpenAI", "OPENAI_API_KEY"),
                    ("OpenRouter", "OPENROUTER_API_KEY"), ("Google Gemini", "GEMINI_API_KEY"), ("xAI", "XAI_API_KEY"), ("DeepSeek", "DEEPSEEK_API_KEY") } },
            new AgentDef { Id = "claude", Name = "CLAUDE CODE", Command = "claude", Package = "@anthropic-ai/claude-code",
                Note = "threads in MQH (through its thread adapter) and the terminal",
                ThreadsPackage = "@agentclientprotocol/claude-agent-acp@" + ClaudeAdapterVersion, ThreadsCommand = ClaudeThreadsBin,
                Keys = new[] { ("Anthropic Console", "ANTHROPIC_API_KEY") } },
            new AgentDef { Id = "codex", Name = "CODEX", Command = "codex", Package = "@openai/codex",
                Note = "threads in MQH and the terminal",
                Keys = new[] { ("OpenAI", "OPENAI_API_KEY") } },
        };

        /// GET (and UPDATE): the CLI from its official npm package; for Claude Code also its thread adapter, pinned, in the
        /// same npm command (the adapter and its SDK run no install script).
        public static string InstallCommand(AgentDef a) => a.ThreadsPackage == null
            ? $"npm i -g --allow-scripts={a.Package} {a.Package}@latest && {a.Command} --version && echo '✓ {a.Name} installed: back to AGENTS'"
            : $"npm i -g --allow-scripts={a.Package} {a.Package}@latest {a.ThreadsPackage} && {a.Command} --version && {a.ThreadsCommand} --version && echo '✓ {a.Name} installed, with its threads: back to AGENTS'";

        /// ENABLE THREADS: only the thread adapter, for a CLI installed before it was part of GET.
        public static string ThreadsInstallCommand(AgentDef a) =>
            $"npm i -g {a.ThreadsPackage} && {a.ThreadsCommand} --version && echo '✓ {a.Name} threads enabled: back to AGENTS'";

        private readonly Dictionary<string, string> _agentVersions = new Dictionary<string, string>();
        /// The thread adapters found by the probe (agent id -> version; null: not installed).
        private readonly Dictionary<string, string> _agentAdapters = new Dictionary<string, string>();
        private readonly Dictionary<string, UiButton> _getTiles = new Dictionary<string, UiButton>();
        private readonly HashSet<string> _agentInstalling = new HashSet<string>();
        private float _agentProbeUntil, _agentNextProbe;
        private bool _agentProbing;
        /// Self-tests: replaces the probe of the Linux (`command -v` + `--version`) with a fixed answer.
        public Func<string> AgentProbeForTest;

        /// The GET row: one tile per agent (GET, INSTALLING…, ✓ version) and MORE (coming soon, not clickable).
        private void BuildAgentGetRow(UiPanel p, Rect row)
        {
            var t = UiTheme.Current;
            int n = GetAgents.Length + 2; // + the local coder + MORE
            float w = (row.width - t.Gap * (n - 1)) / n;
            _getTiles.Clear();
            for (int i = 0; i < GetAgents.Length; i++)
            {
                var a = GetAgents[i];
                var b = p.Add(new UiButton(a.Name + "\nGET ⇣", () => OnGetTile(a))
                    { Rect = new Rect(row.x + i * (w + t.Gap), row.y, w, row.height), TextSize = 0.0125f });
                _getTiles[a.Id] = b;
            }
            // the local coder (docs/local-coder.md): a model in the AI service any of the agents above can use
            _coderTile = p.Add(new UiButton(LocalCoderTileText(), OnLocalCoderTile)
                { Rect = new Rect(row.x + GetAgents.Length * (w + t.Gap), row.y, w, row.height), TextSize = 0.0125f });
            var more = p.Add(new UiButton("MORE\nCOMING SOON", null, UiButtonStyle.Ghost)
                { Rect = new Rect(row.x + (GetAgents.Length + 1) * (w + t.Gap), row.y, w, row.height), TextSize = 0.0125f });
            more.SetEnabled(false);
            p.Tick += _ => { TickAgentProbe(); TickLocalCoderTile(); };
        }

        private void RefreshGetTiles()
        {
            foreach (var a in GetAgents)
            {
                if (!_getTiles.TryGetValue(a.Id, out var b)) continue;
                bool installed = _agentVersions.TryGetValue(a.Id, out var v) && v != null;
                string state = installed ? "✓ " + Trim(v, 14) : _agentInstalling.Contains(a.Id) ? "INSTALLING…" : "GET ⇣";
                b.SetText(a.Name + "\n" + state);
                b.SetSelected(installed);
            }
        }

        private void OnGetTile(AgentDef a)
        {
            bool installed = _agentVersions.TryGetValue(a.Id, out var v) && v != null;
            if (installed && NeedsThreads(a))
            {
                Modal(a.Name, $"{a.Name} {v} is installed and runs in a terminal.\nENABLE THREADS installs its thread adapter (its official npm package, about 400 MB with its engine) in a terminal you watch, so MQH runs its threads; it uses the sign-in you already made.",
                    new[] { "ENABLE THREADS", "UPDATE", "API KEY", "CLOSE" }, i =>
                    {
                        if (i == 0) StartThreadsInstall(a);
                        else if (i == 1) StartAgentInstall(a);
                        else if (i == 2) OpenAgentKey(a.Id);
                    });
                return;
            }
            if (installed)
            {
                Modal(a.Name, $"{a.Name} {v} is installed ({a.Note}).\nUPDATE installs the newest version; API KEY adds a key for it.",
                    new[] { "UPDATE", "API KEY", "CLOSE" }, i =>
                    {
                        if (i == 0) StartAgentInstall(a);
                        else if (i == 1) OpenAgentKey(a.Id);
                    });
                return;
            }
            StartAgentInstall(a);
        }

        private void StartAgentInstall(AgentDef a)
        {
            if (!RunInNewTerminal(InstallCommand(a))) return;
            _agentInstalling.Add(a.Id);
            _agentProbeUntil = Time.unscaledTime + 420f;
            _agentNextProbe = Time.unscaledTime + 8f;
            RefreshGetTiles();
            Toast($"Installing {a.Name} in a terminal ({(a.ThreadsPackage != null ? "a few minutes: its thread adapter too" : "about a minute")})", "info");
        }

        /// An agent whose CLI is installed but whose thread adapter is not (Claude Code installed before GET added it).
        private bool NeedsThreads(AgentDef a) => a.ThreadsCommand != null && _agentVersions.TryGetValue(a.Id, out var v) && v != null
            && !(_agentAdapters.TryGetValue(a.Id, out var t) && t != null);

        /// ENABLE THREADS: the thread adapter alone, in a terminal you watch; the probe sees it and MQH gets the agent.
        public void StartThreadsInstall(AgentDef a)
        {
            if (a?.ThreadsPackage == null) return;
            if (!RunInNewTerminal(ThreadsInstallCommand(a))) return;
            _agentInstalling.Add(a.Id + ThreadsSuffix);
            _agentProbeUntil = Time.unscaledTime + 600f;
            _agentNextProbe = Time.unscaledTime + 8f;
            Toast($"Enabling {a.Name} threads: its adapter installs in a terminal (a few minutes)", "info");
        }

        private const string ThreadsSuffix = "+threads";

        /// While an install runs (and once when AGENTS opens): which agents are on the PATH, with their versions.
        private UiButton _coderTile;
        private float _coderPollAt;

        private void OnLocalCoderTile()
        {
            if (LocalCoderInstalled)
            {
                SyncLocalCoder(true);
                // OpenCode lists it (MQH's ACP bridge, first, under this name); Claude Code's threads run through its ACP
                // adapter, which does not list it yet; Codex is not offered it (docs/local-coder.md)
                Toast("Local coder ready: in NEW THREAD pick OpenCode, then the model “Local coder (this headset, slow)”. Slow: minutes per turn; voice waits while it works.", "info");
                return;
            }
            var err = InstallLocalCoder();
            if (err != null) Toast("Local coder: " + err, "warn");
        }

        private float _coderTileAt;

        private void TickLocalCoderTile()
        {
            if (_coderTile == null || Application.isPlaying && Time.unscaledTime < _coderTileAt) return;
            _coderTileAt = Time.unscaledTime + 0.25f; // 4 times a second (status lookups and the % text were every frame)
            bool downloading = LocalCoderInstallState == "downloading" || LocalCoderInstallState == "verifying";
            if (Time.unscaledTime >= _coderPollAt) { _coderPollAt = Time.unscaledTime + (downloading ? 2f : 10f); PollVoiceStatus(); }
            string text = LocalCoderTileText();
            if (text != _coderTile.Text) { _coderTile.SetText(text); _coderTile.SetSelected(LocalCoderInstalled); }
            SyncLocalCoder();
        }

        private void TickAgentProbe()
        {
            if (_agentProbing || Time.unscaledTime < _agentNextProbe || Time.unscaledTime > _agentProbeUntil) return;
            _agentNextProbe = Time.unscaledTime + 6f;
            ProbeAgents();
        }

        public void ProbeAgents()
        {
            if (_agentProbing) return;
            if (AgentProbeForTest != null) { OnAgentProbe(AgentProbeForTest()); return; }
            if (Git == null || !Git.InUserland) return;
            _agentProbing = true;
            string cmd = string.Join("; ", GetAgents.Select(a =>
                $"if command -v {a.Command} >/dev/null 2>&1; then echo \"{a.Id} $({a.Command} --version 2>/dev/null | head -1)\"; else echo \"{a.Id} -\"; fi"
                + (a.ThreadsCommand == null ? "" : $"; if command -v {a.ThreadsCommand} >/dev/null 2>&1; then echo \"{a.Id}{ThreadsSuffix} $({a.ThreadsCommand} --version 2>/dev/null | head -1)\"; else echo \"{a.Id}{ThreadsSuffix} -\"; fi")));
            System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await Git.Shell(cmd, 30000);
                _main.Enqueue(() => { _agentProbing = false; OnAgentProbe(r.output ?? ""); });
            });
        }

        private void OnAgentProbe(string output)
        {
            var before = new Dictionary<string, string>(_agentVersions);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ' }, 2);
                if (parts.Length < 1) continue;
                string v = parts.Length > 1 ? parts[1].Trim() : "-";
                v = v == "-" || v.Length == 0 ? null : v;
                if (parts[0].EndsWith(ThreadsSuffix, StringComparison.Ordinal))
                {
                    string id = parts[0].Substring(0, parts[0].Length - ThreadsSuffix.Length);
                    if (GetAgents.Any(a => a.Id == id && a.ThreadsCommand != null)) _agentAdapters[id] = v;
                    continue;
                }
                if (!GetAgents.Any(a => a.Id == parts[0])) continue;
                _agentVersions[parts[0]] = v;
            }
            foreach (var a in GetAgents)
            {
                bool now = _agentVersions.TryGetValue(a.Id, out var v) && v != null;
                bool was = before.TryGetValue(a.Id, out var b) && b != null;
                if (now && _agentInstalling.Remove(a.Id))
                {
                    Toast($"{a.Name} {v} installed · add an API key or SIGN IN", "ok");
                    if (IsPanelOpen("agents")) RefreshAgents(); // MQH lists it now (opencode only when installed)
                }
                bool adapter = _agentAdapters.TryGetValue(a.Id, out var av) && av != null;
                if (adapter && _agentInstalling.Remove(a.Id + ThreadsSuffix))
                    Toast($"{a.Name} threads enabled (adapter {av}) · NEW THREAD can use it once MQH lists it", "ok");
            }
            if (_agentInstalling.Count == 0) _agentProbeUntil = 0f;
            RefreshGetTiles();
            EnsureClaudeThreads();
        }

        // ------------------------------------------------------------------ Claude Code threads (MQH, ACP)

        private bool _claudeThreadsBusy, _claudeThreadsWarned;
        private int _claudeThreadsTries;

        /// The thread adapter is installed: make sure provider-acp's `customAgents` setting has Claude Code (provider
        /// acp-claude-code, the global bin). Reads the setting and writes it only when the entry is missing or differs, so
        /// a call after every probe costs one GET. MQH answers before its plugins load (404 "not running"): tried again
        /// after 20 s, four times at most per app start.
        private void EnsureClaudeThreads()
        {
            if (_claudeThreadsBusy || Mqh == null) return; // not gated on Mqh.Online: a failed call (a 404) clears it
            if (!(_agentAdapters.TryGetValue("claude", out var adapter) && adapter != null)) return;
            _claudeThreadsBusy = true;
            const string path = "/api/v1/plugins/provider-acp/settings";
            void Failed(string why)
            {
                _claudeThreadsBusy = false;
                Log($"[agents] Claude Code threads: could not register {ClaudeThreadsProvider} in MQH ({why})" + (_claudeThreadsTries < 3 ? ": again in 20 s" : ""));
                if (++_claudeThreadsTries < 4) After(20f, EnsureClaudeThreads);
            }
            Mqh.Call(HttpMethod.Get, path, null, r =>
            {
                if (r == null || r["values"] == null) { Failed(Mqh.LastError ?? "no answer"); return; }
                var cur = r["values"]["customAgents"];
                string next = WithClaudeThreadsAgent(cur?.Type == JTokenType.String ? (string)cur : "", out bool changed, out string err);
                if (err != null)
                {
                    _claudeThreadsBusy = false;
                    if (!_claudeThreadsWarned) { _claudeThreadsWarned = true; Log("[agents] Claude Code threads: " + err); Toast("Claude Code threads: " + err, "warn"); }
                    return;
                }
                if (!changed) { _claudeThreadsBusy = false; _claudeThreadsTries = 0; return; }
                Mqh.Call(HttpMethod.Put, path, new JObject { ["values"] = new JObject { ["customAgents"] = next } }, w =>
                {
                    if (w == null) { Failed(Mqh.LastError ?? "no answer"); return; }
                    _claudeThreadsBusy = false;
                    _claudeThreadsTries = 0;
                    Log($"[agents] Claude Code threads: {ClaudeThreadsProvider} registered in MQH (provider-acp customAgents, command {ClaudeThreadsBin}, adapter {adapter})");
                    Toast("Claude Code threads are ready in MQH: NEW THREAD lists it", "ok");
                    if (IsPanelOpen("agents")) After(2f, RefreshAgents); // MQH registers the provider when the setting changes
                });
            });
        }

        /// provider-acp's customAgents (a JSON array as a string) with Claude Code's entry added, or put right when it
        /// differs; the user's other entries stay as they are. err: the setting is not a JSON array (left untouched).
        public static string WithClaudeThreadsAgent(string setting, out bool changed, out string err)
        {
            changed = false;
            err = null;
            JArray entries;
            try { entries = string.IsNullOrWhiteSpace(setting) ? new JArray() : JToken.Parse(setting) as JArray; }
            catch (Exception) { entries = null; }
            if (entries == null) { err = "the ACP provider's custom agents setting is not a JSON array: left as it is (fix it in MQH)"; return setting; }
            // Claude Code reads ~/.claude/skills itself: MQH does not hand it a second copy of those skills
            JObject Root(bool ancestors) { var o = new JObject { ["path"] = ".claude/skills", ["skipIfManifest"] = ".claude-plugin/plugin.json" }; if (ancestors) o["ancestors"] = true; return o; }
            var want = new JObject
            {
                ["id"] = ClaudeThreadsAgentId,
                ["displayName"] = "Claude Code",
                ["command"] = ClaudeThreadsBin,
                ["args"] = new JArray(),
                ["env"] = new JObject(),
                ["nativeSkillRoots"] = new JObject { ["user"] = new JArray { Root(false) }, ["project"] = new JArray { Root(true) } },
            };
            int at = -1;
            for (int i = 0; i < entries.Count; i++) if (entries[i] is JObject e && (string)e["id"] == ClaudeThreadsAgentId) { at = i; break; }
            if (at >= 0 && JToken.DeepEquals(entries[at], want)) return setting;
            if (at >= 0) entries[at] = want; else entries.Add(want);
            changed = true;
            return entries.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        // ------------------------------------------------------------------ API KEY

        private UiPanel _agentKey;
        private UiList _akList;
        private UiTextField _akField;
        private UiLabel _akInfo, _akStatus;
        private string _akEnv, _akAgent;
        private readonly HashSet<string> _keysForMqh = new HashSet<string>();

        /// The key dialog for an agent (its providers and their variables), opened from AGENTS or a GET tile.
        public void OpenAgentKey(string agentId)
        {
            var a = GetAgents.FirstOrDefault(x => x.Id == agentId) ?? GetAgents.FirstOrDefault(x => agentId != null && agentId.Contains(x.Id)) ?? GetAgents[0];
            _akAgent = a.Id;
            EnsureAgentKeyPanel();
            _akList.SetItems(a.Keys.Select(k => new UiListItem
            {
                Icon = Vault != null && Vault.Has(k.env) ? "●" : "○", Title = k.label, Sub = k.env,
                Badge = Vault != null && Vault.Has(k.env) ? "SAVED" : "", BadgeColor = UiTheme.Current.Ok, Tag = k.env,
            }));
            SelectAgentKey(a.Keys[0].env);
            _agentKey.SetTitle("API KEY · " + a.Name);
            OpenPanel("agent-key");
        }

        private void EnsureAgentKeyPanel()
        {
            if (_agentKey != null) return;
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("agent-key", "API KEY", 0.50f, 0.48f);
            _agentKey = p;
            var st = new UiStack(p.Content, t.Gap);
            p.Add(new UiLabel("Pick the provider, paste your key (⎘: what you copied) or type it, SAVE. It is sealed in the vault and handed to the agents MQH starts, never written in a file.", t.SmallSize)
                { Rect = st.Next(0.036f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            p.Add(new UiLabel("SUPPORTED PROVIDERS", t.SmallSize) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _akList = p.Add(new UiList(0.028f, (item, idx) => SelectAgentKey(item.Tag as string)) { Rect = st.Next(0.17f) });
            _akInfo = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Next(0.018f), Color = t.TextAccent });
            var row = st.Next(t.ButtonHeight);
            float pasteW = 0.09f;
            _akField = p.Add(new UiTextField("", "", _ => SaveAgentKey(), "paste or type the key") { Rect = new Rect(row.x, row.y, row.width - pasteW - t.Gap, row.height), Secure = true });
            p.Add(new UiButton("⎘ PASTE", PasteAgentKey) { Rect = new Rect(row.xMax - pasteW, row.y, pasteW, row.height) });
            p.Add(new UiButton("SAVE ✓", SaveAgentKey, UiButtonStyle.Primary) { Rect = st.Next(t.ButtonHeight) });
            p.Tick += _ => TickKeyRetry();
            _akStatus = p.Add(new UiLabel("", t.SmallSize) { Rect = st.Rest(), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
        }

        private void SelectAgentKey(string env)
        {
            _akEnv = env;
            _akField?.Set("");
            bool saved = Vault != null && env != null && Vault.Has(env);
            _akInfo?.Set($"→ {env}" + (saved ? "  (saved: SAVE replaces it)" : ""));
            if (_akList != null) for (int i = 0; i < _akList.Items.Count; i++) if (_akList.Items[i].Tag as string == env) _akList.SelectedIndex = i;
            _akStatus?.Set("");
        }

        private void PasteAgentKey()
        {
            string clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (clip.Length == 0) { _akStatus.Set("The clipboard is empty: copy the key first (in the browser, select it and copy).", UiTheme.Current.Warn); return; }
            _akField.Set(clip);
            _akStatus.Set($"Pasted {clip.Length} characters ({Mask(clip)}).", UiTheme.Current.TextDim);
        }

        public static string Mask(string key) => string.IsNullOrEmpty(key) ? "" : key.Length <= 8 ? new string('•', key.Length) : key.Substring(0, 4) + "…" + key.Substring(key.Length - 4);

        private void SaveAgentKey()
        {
            string key = (_akField?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(_akEnv)) return;
            if (key.Length < 8 || key.Contains(" ")) { _akStatus.Set("That does not look like a key (no spaces, at least 8 characters).", UiTheme.Current.Warn); return; }
            if (Vault == null || !Vault.Usable) { _akStatus.Set("The vault is not available: " + (Vault?.Sealer?.LastError ?? "?"), UiTheme.Current.Danger); return; }
            string err = Vault.Set(_akEnv, key, "api-key", "allow", $"API key for agents ({_akAgent}), added in AGENTS > API KEY");
            if (err != null) { _akStatus.Set("Could not seal it: " + err, UiTheme.Current.Danger); return; }
            _akField.Set("");
            _keysForMqh.Add(_akEnv);
            _akStatus.Set($"{_akEnv} sealed in the vault ({Mask(key)}). Giving it to MQH…", UiTheme.Current.TextDim);
            PushKeysToMqh();
            OpenAgentKey(_akAgent); // refresh the SAVED marks
        }

        /// Keys saved while MQH was not answering go there when it does (OnMqhOnline calls this too).
        private void PushKeysToMqh()
        {
            foreach (var env in _keysForMqh.ToList())
            {
                var v = Vault?.Reveal(env);
                if (v == null) { _keysForMqh.Remove(env); continue; }
                string name = env;
                Mqh.SetMachineEnvironment(name, v, "API key from the MetaCodex vault", r =>
                {
                    Vault?.Deliver(name, "MQH machine environment", r != null, r != null ? "→MQH" : "MQH-ERR");
                    if (r == null) { _akStatus?.Set($"{name} is sealed here; MQH did not take it yet ({Mqh.LastError}): trying again by itself.", UiTheme.Current.Warn); return; }
                    _keysForMqh.Remove(name);
                    _akStatus?.Set($"✓ {name} is set for the agents MQH starts. New threads use it; a running agent needs a new thread.", UiTheme.Current.Ok);
                    Toast($"{name} ready for the agents", "ok");
                });
            }
        }

        private float _keyRetryAt;

        /// A key MQH did not take yet (MQH starting, an error): tried again every 20 s while the dialog is open, and
        /// whenever MQH comes up (OnMqhOnline).
        private void TickKeyRetry()
        {
            if (_keysForMqh.Count == 0 || Time.unscaledTime < _keyRetryAt) return;
            _keyRetryAt = Time.unscaledTime + 20f;
            PushKeysToMqh();
        }

        /// Self-tests.
        public IReadOnlyDictionary<string, string> AgentVersionsForTest => _agentVersions;
        public IReadOnlyDictionary<string, string> AgentAdaptersForTest => _agentAdapters;
        public string AgentKeyEnvForTest => _akEnv;
        public void SetAgentKeyForTest(string key) { _akField?.Set(key); SaveAgentKey(); }
        public void PasteAgentKeyForTest() => PasteAgentKey();
        public void SelectAgentKeyForTest(string env) => SelectAgentKey(env);
        public Newtonsoft.Json.Linq.JArray ProviderStatesForTest { get => _providerStates; set => _providerStates = value; }
        public Newtonsoft.Json.Linq.JArray SupportedOnlyForTest(Newtonsoft.Json.Linq.JArray states) => SupportedOnly(states);
        public string GetTileTextForTest(string id) => _getTiles.TryGetValue(id, out var b) ? b.Text : null;
        public bool MoreTileEnabledForTest => _panels.TryGetValue("agents", out var p) && p.Elements.OfType<UiButton>().Any(b => b.Text.StartsWith("MORE") && b.Enabled);
    }
}
