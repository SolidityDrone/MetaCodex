using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using MetaCodex.Security;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The VAULT panel and the secret broker on the control port. Secrets are typed in VR (secure system
    /// keyboard), sealed with the Android Keystore key, kept outside the Linux rootfs, and handed to the
    /// userland one request at a time (`mcx-secret get NAME`), as each secret's policy says. API keys for
    /// agents can be pushed into MQH's machine environment (encrypted there with MQH's key, which itself is a
    /// "once" secret of this vault: see docs/secrets.md).
    public sealed partial class MetaCodexShell
    {
        public SecretVault Vault { get; private set; }
        public const string MqhTokenName = "mqh.api-token";
        /// A developer's own MQH token (MQH > STATUS > TOKEN): sealed here, never in settings.json.
        public const string MqhTokenOverrideName = "mqh.api-token.override";
        public const string MqhEnvKeyName = "mqh.machine-env-key";

        private UiList _vList;
        private UiLabel _vInfo, _vDetail, _vAudit;
        private UiSegmented _vPolicy;
        private string _vSel;
        private static readonly Regex EnvName = new Regex("^[A-Z_][A-Z0-9_]*$");
        private string _vaultMqhToken;

        /// The MQH token: the one typed in MQH > STATUS wins (developer override, sealed in the vault), else the vault's.
        private string MqhTokenNow() => !string.IsNullOrEmpty(_mqhTokenOverride) ? _mqhTokenOverride : _vaultMqhToken;
        private string _mqhTokenOverride;
        /// Tests: the settings file (to prove no secret is written there).
        public string SettingsPathForTest => _settingsPath;

        /// MQH > STATUS > TOKEN: sealed in the vault (deny: it never leaves the app), not written to settings.json.
        public void SetMqhTokenOverride(string value)
        {
            value = (value ?? "").Trim();
            if (Vault == null || !Vault.Usable) { Toast("Vault locked: the token is not stored", "error"); return; }
            if (value.Length == 0) { Vault.Delete(MqhTokenOverrideName); _mqhTokenOverride = null; }
            else
            {
                var err = Vault.Set(MqhTokenOverrideName, value, "token", "deny", "MQH token typed in MQH > STATUS (developer override)");
                if (err != null) { Toast("Not stored: " + err, "error"); return; }
                _mqhTokenOverride = value;
            }
            Mqh.Token = MqhTokenNow();
        }

        private void InitVault()
        {
            StartMark("vault+services");
            try
            {
                Vault = SecretVault.OpenDefault(Application.persistentDataPath);
                Vault.Changed += () => RefreshVaultPanel();
                if (!Vault.Has(MqhTokenName))
                    Vault.Ensure(MqhTokenName, 32, "token", "allow", "MQH API token: the app sends it as Bearer; the MQH launcher passes it to MQH (MQH_API_TOKEN)");
                _vaultMqhToken = Vault.Reveal(MqhTokenName);
                // a token typed in older versions sat in settings.json as plain text: seal it, blank it there
                if (!string.IsNullOrEmpty(Settings.mqhToken) && Vault.Usable)
                {
                    if (Vault.Set(MqhTokenOverrideName, Settings.mqhToken.Trim(), "token", "deny", "MQH token typed in MQH > STATUS (developer override)") == null)
                    {
                        Settings.mqhToken = "";
                        Settings.Save(_settingsPath);
                        Log("vault: the MQH token override moved from settings.json into the vault");
                    }
                }
                _mqhTokenOverride = Vault.Has(MqhTokenOverrideName) ? Vault.Reveal(MqhTokenOverrideName) : null;
                Mqh.Token = MqhTokenNow();
                Log($"vault: {Vault.Entries.Count} secrets, key {Vault.Sealer.Kind} ({Vault.Sealer.Level})");
            }
            catch (Exception ex) { Log("vault unavailable: " + ex.Message); }
        }

        // ------------------------------------------------------------------ broker (control port)

        /// `secret …` lines from the userland. Answers later when the user must approve.
        public bool SecretCommand(string line, Action<string> reply)
        {
            if (!line.StartsWith("secret ")) return false;
            var parts = line.Substring(7).Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0] : "";
            if (Vault == null) { reply("error: vault unavailable"); return true; }
            switch (sub)
            {
                case "status":
                    reply($"key={Vault.Sealer.Kind} level={Vault.Sealer.Level} secrets={Vault.Entries.Count}");
                    return true;
                case "list":
                    reply(string.Join("\n", Vault.Entries.Select(e => $"{e.name} kind={e.kind} policy={e.policy} uses={e.uses}")));
                    return true;
                case "get":
                case "ensure":
                {
                    if (parts.Length < 2) { reply("error: secret get NAME [who] | secret ensure NAME BYTES [who]"); return true; }
                    string name = parts[1];
                    string who = parts.Length > 2 ? parts[2] : "userland";
                    if (sub == "ensure")
                    {
                        var rest = who.Split(new[] { ' ' }, 2);
                        int bytes = int.TryParse(rest[0], out var b) ? b : 32;
                        who = rest.Length > 1 ? rest[1] : "userland";
                        if (!Vault.Has(name)) { Vault.Ensure(name, bytes, "key", "once", "created on request by " + who); Toast($"Vault: new key '{name}' for {who}", "info"); }
                    }
                    RequestSecret(name, who, reply);
                    return true;
                }
                // no "rearm" here on purpose: a "once" secret must not be re-armable from Linux (an agent could
                // then fetch MQH's key); the app re-arms when it restarts MQH, or the user does in VAULT.
                default:
                    reply("error: secret status | list | get NAME [who] | ensure NAME BYTES [who]  (values are added in VR only)");
                    return true;
            }
        }

        /// Policy check, then (for "ask") a modal in VR; the reply carries the value or an error line.
        public void RequestSecret(string name, string requester, Action<string> reply)
        {
            var d = Vault.Check(name, out string reason);
            if (d == SecretVault.Decision.Deny)
            {
                Vault.Deliver(name, requester, false, "DENY");
                reply("error: denied: " + reason);
                return;
            }
            if (d == SecretVault.Decision.Allow)
            {
                var v = Vault.Deliver(name, requester, true);
                reply(v != null ? "value " + v : "error: unseal failed: " + Vault.Sealer.LastError);
                return;
            }
            bool answered = false;
            var e = Vault.Find(name);
            var modal = UiModal.Show(Router, Head, "SECRET REQUEST",
                $"Something in Linux, calling itself '{Trim(requester, 40)}' (unverified), asks for '{name}' ({e?.kind}). Allow it?", new[] { "ALLOW ONCE", "ALWAYS", "DENY" }, i =>
                {
                    if (answered) return;
                    answered = true;
                    if (i == 1) Vault.SetPolicy(name, "allow");
                    if (i == 2) { Vault.Deliver(name, requester, false, "DENY"); reply("error: denied by the user"); return; }
                    var v = Vault.Deliver(name, requester, true, i == 1 ? "ALWAYS" : "ONCE");
                    reply(v != null ? "value " + v : "error: unseal failed: " + Vault.Sealer.LastError);
                }, _root);
            Sounds?.Play("alert");
            After(MetaCodex.App.ControlServer.LongWaitSeconds - 5, () =>
            {
                if (answered) return;
                answered = true;
                Vault.Deliver(name, requester, false, "TIMEOUT");
                reply("error: nobody answered in VR");
                if (modal != null) { Router.Unregister(modal); UiToasts.Kill(modal.gameObject); }
            });
        }

        // ------------------------------------------------------------------ panel

        private UiTabs _vTabs;
        public UiTabs VaultTabsForTest => _vTabs;
        public UiTabs SystemTabsForTest => _sysTabs;
        public UiTabs WorkspaceTabsForTest => _sbTabs;
        public string GitHubStatusForTest => _ghStatus?.Text ?? "";
        private UiLabel _ghStatus;
        private string _ghLogin, _ghName, _ghScopes, _gitUser, _gitEmail, _gitHelper;
        public const string GitHubTokenName = "GITHUB_TOKEN";
        public const string GitHelperValue = "!mcx-secret git-credential " + GitHubTokenName;

        /// VAULT: SECRETS (API keys, tokens: sealed, handed out by policy) and GIT (GitHub account for git and
        /// agents, git identity, and the repositories of this headset: status, branches, history).
        private void BuildVault()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("vault", "Vault", 0.76f, 0.66f);
            var c = p.Content;
            _vTabs = p.Add(new UiTabs("SECRETS", "GIT") { Rect = new Rect(c.x, c.y, Mathf.Min(c.width, 0.32f), t.ButtonHeight) });
            var area = new Rect(c.x, c.y + t.ButtonHeight + t.Gap * 2, c.width, c.height - t.ButtonHeight - t.Gap * 2);

            // SECRETS
            float leftW = 0.26f;
            var ls = new UiStack(new Rect(area.x, area.y, leftW, area.height), t.Gap);
            _vList = _vTabs.AddTo(0, new UiList(0.034f, (item, idx) => { _vSel = item.Tag as string; ShowVaultDetail(); }) { Rect = ls.Next(area.height - t.ButtonHeight - t.Gap) });
            _vTabs.AddTo(0, new UiButton("+ ADD SECRET…", AddSecretPrompt, UiButtonStyle.Primary) { Rect = ls.Next(t.ButtonHeight) });
            var rs = new UiStack(new Rect(area.x + leftW + t.Gap * 2, area.y, area.width - leftW - t.Gap * 2, area.height), t.Gap);
            _vInfo = _vTabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = rs.Next(0.036f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _vDetail = _vTabs.AddTo(0, new UiLabel("", t.SmallSize * 1.05f) { Rect = rs.Next(0.10f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            _vPolicy = _vTabs.AddTo(0, new UiSegmented("POLICY", new[] { "ASK", "ONCE", "ALLOW", "DENY" }, 0, i => { if (_vSel != null) { Vault.SetPolicy(_vSel, SecretVault.Policies[i]); ShowVaultDetail(); } }) { Rect = rs.Next(0.032f), LabelWidth = 0.08f });
            TabButtons(_vTabs, 0, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("NEW VALUE…", () => { if (_vSel != null) SecretValuePrompt(_vSel, Vault.Find(_vSel)?.kind ?? "api-key"); }),
                ("→ MQH ENV", PushSecretToMqh),
                ("RE-ARM", () => { Vault.Rearm(_vSel); Toast("Re-armed: 'once' secrets can be handed out again", "info"); }),
                ("DELETE", () => { if (_vSel != null) Modal("DELETE SECRET", $"Delete '{_vSel}' from the vault?", new[] { "DELETE", "CANCEL" }, i => { if (i == 0) { Vault.Delete(_vSel); _vSel = null; RefreshVaultPanel(); } }); }),
            }, danger: 3);
            _vTabs.AddTo(0, new UiLabel("ACCESS LOG", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vAudit = _vTabs.AddTo(0, new UiLabel("", t.SmallSize) { Rect = rs.Rest(), UseMono = true, Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });

            // GIT
            BuildGitTab(_vTabs, 1, area);
            _vTabs.OnChanged = i => { if (i == 1) { GitRefreshRepos(); RefreshGitHubStatus(); } else RefreshVaultPanel(); };
            RefreshVaultPanel();
        }

        private static void TabButtons(UiTabs tabs, int page, Rect row, (string label, Action act)[] items, int primary = -1, int danger = -1)
        {
            var t = UiTheme.Current;
            float bw = (row.width - t.Gap * (items.Length - 1)) / items.Length;
            for (int i = 0; i < items.Length; i++)
                tabs.AddTo(page, new UiButton(items[i].label, items[i].act, i == primary ? UiButtonStyle.Primary : i == danger ? UiButtonStyle.Danger : UiButtonStyle.Normal)
                { Rect = new Rect(row.x + i * (bw + t.Gap), row.y, bw, row.height) });
        }

        private void BuildGitTab(UiTabs tabs, int page, Rect area)
        {
            var t = UiTheme.Current;
            var st = new UiStack(area, t.Gap);
            tabs.AddTo(page, new UiLabel("GITHUB ACCOUNT", t.SmallSize) { Rect = st.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _ghStatus = tabs.AddTo(page, new UiLabel("", t.SmallSize * 1.05f) { Rect = st.Next(0.036f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            TabButtons(tabs, page, st.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("TOKEN…", GitHubTokenPrompt),
                ("TEST", GitHubTest),
                ("IDENTITY…", GitIdentityPrompt),
                ("GIT USES IT", GitHelperInstall),
                ("→ AGENTS", GitHubToAgents),
            }, primary: 0);
            tabs.AddTo(page, new UiDivider { Rect = st.Next(0.006f) });
            var rest = st.Rest();
            float leftW = 0.19f;
            var left = new UiStack(new Rect(rest.x, rest.y, leftW, rest.height), t.Gap);
            tabs.AddTo(page, new UiLabel("REPOSITORIES", t.SmallSize) { Rect = left.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gitRepos = tabs.AddTo(page, new UiList(0.03f, (item, idx) => GitOpen(item.Tag as string)) { Rect = left.Next(rest.height - 0.018f - t.ButtonHeight - t.Gap * 2) });
            tabs.AddTo(page, new UiButton("⟳ SCAN", GitRefreshRepos) { Rect = left.Next(t.ButtonHeight) });
            float rx = rest.x + leftW + t.Gap * 2;
            var right = new UiStack(new Rect(rx, rest.y, rest.xMax - rx, rest.height), t.Gap);
            _gitHead = tabs.AddTo(page, new UiLabel("Select a repository", t.BodySize) { Rect = right.Next(0.026f) });
            var lists = right.NextSplit(0.11f, 0.56f, out var brRect);
            _gitChanges = tabs.AddTo(page, new UiList(0.024f) { Rect = lists, Monospace = true });
            _gitBranches = tabs.AddTo(page, new UiList(0.024f, (item, idx) => GitBranchPicked(item.Tag as GitBranch)) { Rect = brRect });
            tabs.AddTo(page, new UiLabel("HISTORY", t.SmallSize) { Rect = right.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _gitGraph = tabs.AddTo(page, new UiGitGraph { Rect = right.Next(rest.yMax - right.Y - t.ButtonHeight - t.Gap) });
            TabButtons(tabs, page, right.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("⟳", () => GitOpen(_gitRepo)),
                ("FETCH", () => GitAction("fetch --all --prune")),
                ("PULL", () => GitAction("pull --ff-only")),
                ("COMMIT…", GitCommitPrompt),
                ("PUSH", () => GitAction("push")),
                ("TERMINAL", () => { if (_gitRepo != null) RunInNewTerminal("cd " + GitService.Quote(_gitRepo) + " && git status -sb"); }),
            });
        }

        // ---- GitHub account: the token lives in the vault; git asks for it through mcx-secret (credential helper)

        public void RefreshGitHubStatus()
        {
            if (_ghStatus == null) return;
            var t = UiTheme.Current;
            bool has = Vault != null && Vault.Has(GitHubTokenName);
            ShowGitHubStatus();
            Task.Run(async () =>
            {
                var r = await Git.Shell("git config --global --get user.name; echo '|'; git config --global --get user.email; echo '|'; git config --global --get-all credential.https://github.com.helper", 15000);
                var parts = (r.output ?? "").Split('|');
                _main.Enqueue(() =>
                {
                    _gitUser = parts.Length > 0 ? parts[0].Trim() : "";
                    _gitEmail = parts.Length > 1 ? parts[1].Trim() : "";
                    _gitHelper = parts.Length > 2 ? parts[2].Trim() : "";
                    ShowGitHubStatus();
                });
            });
            _ = has; _ = t;
        }

        private void ShowGitHubStatus()
        {
            if (_ghStatus == null) return;
            var t = UiTheme.Current;
            bool has = Vault != null && Vault.Has(GitHubTokenName);
            bool helper = (_gitHelper ?? "").Contains("mcx-secret git-credential");
            string who = _ghLogin != null ? $"<color=#{UiViz.Hex(t.Ok)}>● {UiViz.NoParse(_ghLogin)}</color>{(string.IsNullOrEmpty(_ghName) ? "" : " (" + UiViz.NoParse(_ghName) + ")")}" + (string.IsNullOrEmpty(_ghScopes) ? "" : $" <color=#{UiViz.Hex(t.TextDim)}>scopes {UiViz.NoParse(_ghScopes)}</color>")
                : has ? "token in the vault · TEST checks it" : $"<color=#{UiViz.Hex(t.Warn)}>no token yet</color> · TOKEN… to add one (github.com > Settings > Developer settings > tokens)";
            string id = string.IsNullOrEmpty(_gitUser) ? $"<color=#{UiViz.Hex(t.Warn)}>no git identity</color>" : $"{UiViz.NoParse(_gitUser)} &lt;{UiViz.NoParse(_gitEmail)}&gt;".Replace("&lt;", "‹").Replace("&gt;", "›");
            _ghStatus.Set($"{who}\ngit: {id} · https pushes {(helper ? $"<color=#{UiViz.Hex(t.Ok)}>use the vault token</color>" : "do not use it yet (GIT USES IT)")}", t.Text);
        }

        private void GitHubTokenPrompt()
        {
            UiKeyboard.Edit("", true, null, value =>
            {
                value = (value ?? "").Trim();
                if (value.Length == 0) { Toast("Nothing stored (empty token)", "warn"); return; }
                var existing = Vault.Find(GitHubTokenName);
                var err = Vault.Set(GitHubTokenName, value, "token", existing?.policy ?? "allow", existing?.note ?? "GitHub token: git over https (credential helper), gh, agents. VAULT > GIT");
                if (err != null) { Toast("Not stored: " + err, "error"); return; }
                Toast("GitHub token sealed in the vault", "ok");
                _ghLogin = null;
                RefreshVaultPanel();
                GitHubTest();
            });
        }

        /// Asks GitHub who the token belongs to (api.github.com/user), from the app (the token stays in the app).
        public void GitHubTest()
        {
            var token = Vault?.Has(GitHubTokenName) == true ? Vault.Reveal(GitHubTokenName) : null;
            if (string.IsNullOrEmpty(token)) { Toast("No GitHub token in the vault yet: TOKEN…", "warn"); return; }
            Vault.Deliver(GitHubTokenName, "VAULT > GIT test (api.github.com)", true, "TEST");
            Task.Run(async () =>
            {
                string login = null, name = null, scopes = null, error = null;
                try
                {
                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.github.com/user"))
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        req.Headers.TryAddWithoutValidation("User-Agent", "MetaCodex");
                        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                        using (var res = await http.SendAsync(req))
                        {
                            string body = await res.Content.ReadAsStringAsync();
                            if (res.Headers.TryGetValues("X-OAuth-Scopes", out var sc)) scopes = string.Join(",", sc);
                            if (!res.IsSuccessStatusCode) error = (int)res.StatusCode + " " + (res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "the token is not valid (expired or revoked)" : Trim(body, 80));
                            else { var j = JObject.Parse(body); login = (string)j["login"]; name = (string)j["name"]; }
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                _main.Enqueue(() =>
                {
                    if (error != null) { _ghLogin = null; Toast("GitHub: " + error, "error"); }
                    else { _ghLogin = login; _ghName = name; _ghScopes = scopes; Toast("GitHub: signed in as " + login, "ok"); }
                    ShowGitHubStatus();
                });
            });
        }

        private void GitIdentityPrompt()
        {
            UiKeyboard.Edit(string.IsNullOrEmpty(_gitUser) ? (_ghName ?? _ghLogin ?? "") : _gitUser, false, null, name =>
            {
                name = (name ?? "").Trim();
                if (name.Length == 0) return;
                UiKeyboard.Edit(string.IsNullOrEmpty(_gitEmail) ? (_ghLogin != null ? _ghLogin + "@users.noreply.github.com" : "") : _gitEmail, false, null, email =>
                {
                    email = (email ?? "").Trim();
                    if (email.Length == 0) return;
                    GitGlobal($"git config --global user.name {GitService.Quote(name)} && git config --global user.email {GitService.Quote(email)}", "Git identity: " + name);
                });
            });
        }

        /// git over https to github.com takes the vault's token, asked for through mcx-secret at each use.
        private void GitHelperInstall()
        {
            if (Vault?.Has(GitHubTokenName) != true) { Toast("Add the GitHub token first (TOKEN…)", "warn"); return; }
            GitGlobal("git config --global --replace-all credential.https://github.com.helper '' && git config --global --add credential.https://github.com.helper " + GitService.Quote(GitHelperValue),
                "git uses the vault's GitHub token for github.com");
        }

        private void GitGlobal(string command, string done)
        {
            if (!Git.InUserland) { Toast("(Editor) not changing this PC's git config: " + Trim(command, 60), "warn"); return; }
            Task.Run(async () =>
            {
                var r = await Git.Shell(command, 15000);
                _main.Enqueue(() => { Toast(r.code == 0 ? done : "git config failed: " + Last(r.output), r.code == 0 ? "ok" : "error"); RefreshGitHubStatus(); });
            });
        }

        /// Agents (gh, git in their own shells) get the token as GH_TOKEN / GITHUB_TOKEN in MQH's environment.
        private void GitHubToAgents()
        {
            var v = Vault?.Has(GitHubTokenName) == true ? Vault.Reveal(GitHubTokenName) : null;
            if (v == null) { Toast("Add the GitHub token first (TOKEN…)", "warn"); return; }
            Modal("GITHUB TOKEN FOR AGENTS", "Agents' commands (gh, git) will get it as GH_TOKEN and GITHUB_TOKEN. They can then push and open pull requests as you. Go on?", new[] { "GIVE IT", "CANCEL" }, i =>
            {
                if (i != 0) return;
                Mqh.SetMachineEnvironment("GH_TOKEN", v, "GitHub token from the MetaCodex vault", r1 =>
                    Mqh.SetMachineEnvironment(GitHubTokenName, v, "GitHub token from the MetaCodex vault", r2 =>
                    {
                        bool ok = r1 != null && r2 != null;
                        Vault.Deliver(GitHubTokenName, "MQH machine environment", ok, ok ? "→MQH" : "MQH-ERR");
                        Toast(ok ? "Agents now have GH_TOKEN / GITHUB_TOKEN" : "MQH: " + Mqh.LastError, ok ? "ok" : "error");
                    }));
            });
        }

        public void RefreshVaultPanel()
        {
            if (_vList == null || Vault == null) return;
            var t = UiTheme.Current;
            _vList.SetItems(Vault.Entries.Select(e => new UiListItem
            {
                Icon = e.kind == "key" ? "⚿" : "◈",
                IconColor = e.policy == "deny" ? t.TextDim : e.policy == "allow" ? t.Ok : e.policy == "once" ? t.Violet : t.Warn,
                Title = e.name,
                Sub = e.kind,
                Badge = e.policy.ToUpperInvariant(),
                BadgeColor = e.policy == "allow" ? t.Ok : e.policy == "deny" ? t.TextDim : t.Warn,
                Tag = e.name,
            }), keepScroll: true);
            string keyName = Vault.Sealer.Kind == "android-keystore" ? "Android Keystore" : Vault.Sealer.Kind == "locked" ? "<color=#FF6B6B>LOCKED</color>" : "dev file (Editor)";
            string where = Vault.Sealer.Kind == "android-keystore" ? "the app's no-backup folder (outside Linux)" : Vault.Sealer.Kind == "locked" ? Vault.Sealer.LastError : Vault.FilePath;
            var warn = new List<string>();
            if (Vault.LoadError != null) warn.Add("the vault file was unreadable and is kept aside: " + Trim(Vault.LoadError, 60));
            // a debuggable APK lets anyone with adb run code as this app (run-as) and so use its keystore key
            if (Debug.isDebugBuild && !Application.isEditor) warn.Add("development build: with adb, `run-as` can use this app's key; build a release APK (MCX_RELEASE=1) for real keys");
            _vInfo.Set($"key: <b>{keyName}</b> · {Vault.Sealer.Level} · {Vault.Entries.Count} secrets\nstored sealed in {where}" + (warn.Count > 0 ? "\n<color=#FFB84D>⚠ " + string.Join(" · ", warn) + "</color>" : ""));
            ShowVaultDetail();
            _vAudit.Set(Vault.Audit.Count == 0 ? "no requests yet · from Linux: mcx-secret get NAME" : string.Join("\n", Vault.Audit.AsEnumerable().Reverse().Take(8).Select(a => a.ToString())));
        }

        private void ShowVaultDetail()
        {
            if (_vDetail == null) return;
            var e = _vSel != null ? Vault.Find(_vSel) : null;
            if (e == null) { _vDetail.Set("Select a secret, or + ADD SECRET… (API keys for agents, git tokens). Values are typed with the secure keyboard and never shown again."); return; }
            string When(long ms) => ms <= 0 ? "never" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string policyHelp = e.policy == "ask" ? "asks you in VR on every request" : e.policy == "once" ? "handed out once per app session (bootstrap keys)" : e.policy == "allow" ? "handed out on request, logged" : "never leaves the app";
            _vDetail.Set($"<b>{e.name}</b> · {e.kind}\n{e.note}\npolicy <b>{e.policy}</b>: {policyHelp}\nused {e.uses}× · last {When(e.lastUsed)} · updated {When(e.updated)}");
            _vPolicy?.Set(Math.Max(0, Array.IndexOf(SecretVault.Policies, e.policy)));
        }

        private void AddSecretPrompt()
        {
            UiKeyboard.Edit("OPENAI_API_KEY", false, null, name =>
            {
                name = (name ?? "").Trim();
                if (name.Length == 0) return;
                if (!SecretVault.NamePattern.IsMatch(name)) { Toast("Name: letters, digits, . _ - (max 64)", "warn"); return; }
                string kind = name.ToUpperInvariant().Contains("TOKEN") ? "token" : name.ToUpperInvariant().Contains("PASS") ? "password" : "api-key";
                SecretValuePrompt(name, kind);
            });
        }

        private void SecretValuePrompt(string name, string kind)
        {
            UiKeyboard.Edit("", true, null, value =>
            {
                if (string.IsNullOrEmpty(value)) { Toast("Nothing stored (empty value)", "warn"); return; }
                var existing = Vault.Find(name);
                var err = Vault.Set(name, value.Trim(), kind, existing?.policy ?? "ask", existing?.note ?? "added in VR");
                Toast(err == null ? $"'{name}' sealed in the vault" : "Not stored: " + err, err == null ? "ok" : "error");
                _vSel = name;
                RefreshVaultPanel();
            });
        }

        /// Copies the selected secret into MQH's machine environment (agents get it as an env variable).
        private void PushSecretToMqh()
        {
            if (_vSel == null) return;
            if (!EnvName.IsMatch(_vSel)) { Toast("MQH env names look like OPENAI_API_KEY (A-Z, 0-9, _)", "warn"); return; }
            var v = Vault.Reveal(_vSel);
            if (v == null) { Toast("Unseal failed: " + Vault.Sealer.LastError, "error"); return; }
            Mqh.SetMachineEnvironment(_vSel, v, "from the MetaCodex vault", r =>
            {
                Vault.Deliver(_vSel, "MQH machine environment", r != null, r != null ? "→MQH" : "MQH-ERR");
                Toast(r != null ? $"{_vSel} set for agents in MQH (stored encrypted there)" : "MQH: " + Mqh.LastError, r != null ? "ok" : "error");
            });
        }
    }
}
