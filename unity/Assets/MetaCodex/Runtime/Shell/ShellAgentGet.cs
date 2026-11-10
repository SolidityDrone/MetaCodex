using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.UI;
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
    public sealed partial class MetaCodexShell
    {
        public sealed class AgentDef
        {
            public string Id, Name, Command, Package, Note;
            public (string label, string env)[] Keys;
        }

        public static readonly AgentDef[] GetAgents =
        {
            new AgentDef { Id = "opencode", Name = "OPENCODE", Command = "opencode", Package = "opencode-ai",
                Note = "threads in MQH (ACP) and the terminal",
                Keys = new[] { ("OpenCode Zen / Go", "OPENCODE_API_KEY"), ("Anthropic", "ANTHROPIC_API_KEY"), ("OpenAI", "OPENAI_API_KEY"),
                    ("OpenRouter", "OPENROUTER_API_KEY"), ("Google Gemini", "GEMINI_API_KEY"), ("xAI", "XAI_API_KEY"), ("DeepSeek", "DEEPSEEK_API_KEY") } },
            new AgentDef { Id = "claude", Name = "CLAUDE CODE", Command = "claude", Package = "@anthropic-ai/claude-code",
                Note = "in a terminal (MQH threads with it: its provider plugin installs at first use, soon)",
                Keys = new[] { ("Anthropic Console", "ANTHROPIC_API_KEY") } },
            new AgentDef { Id = "codex", Name = "CODEX", Command = "codex", Package = "@openai/codex",
                Note = "threads in MQH and the terminal",
                Keys = new[] { ("OpenAI", "OPENAI_API_KEY") } },
        };

        public static string InstallCommand(AgentDef a) =>
            $"npm i -g --allow-scripts={a.Package} {a.Package}@latest && {a.Command} --version && echo '✓ {a.Name} installed: back to AGENTS'";

        private readonly Dictionary<string, string> _agentVersions = new Dictionary<string, string>();
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
            int n = GetAgents.Length + 1;
            float w = (row.width - t.Gap * (n - 1)) / n;
            _getTiles.Clear();
            for (int i = 0; i < GetAgents.Length; i++)
            {
                var a = GetAgents[i];
                var b = p.Add(new UiButton(a.Name + "\nGET ⇣", () => OnGetTile(a))
                    { Rect = new Rect(row.x + i * (w + t.Gap), row.y, w, row.height), TextSize = 0.0125f });
                _getTiles[a.Id] = b;
            }
            var more = p.Add(new UiButton("MORE\nCOMING SOON", null, UiButtonStyle.Ghost)
                { Rect = new Rect(row.x + GetAgents.Length * (w + t.Gap), row.y, w, row.height), TextSize = 0.0125f });
            more.SetEnabled(false);
            p.Tick += _ => TickAgentProbe();
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
            Toast($"Installing {a.Name} in a terminal (about a minute)", "info");
        }

        /// While an install runs (and once when AGENTS opens): which agents are on the PATH, with their versions.
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
                $"if command -v {a.Command} >/dev/null 2>&1; then echo \"{a.Id} $({a.Command} --version 2>/dev/null | head -1)\"; else echo \"{a.Id} -\"; fi"));
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
                if (parts.Length < 1 || !GetAgents.Any(a => a.Id == parts[0])) continue;
                string v = parts.Length > 1 ? parts[1].Trim() : "-";
                _agentVersions[parts[0]] = v == "-" || v.Length == 0 ? null : v;
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
            }
            if (_agentInstalling.Count == 0) _agentProbeUntil = 0f;
            RefreshGetTiles();
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
