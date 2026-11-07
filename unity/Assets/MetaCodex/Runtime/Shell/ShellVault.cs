using System;
using System.Linq;
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
        public const string MqhEnvKeyName = "mqh.machine-env-key";

        private UiList _vList;
        private UiLabel _vInfo, _vDetail, _vAudit;
        private UiSegmented _vPolicy;
        private string _vSel;
        private static readonly Regex EnvName = new Regex("^[A-Z_][A-Z0-9_]*$");
        private string _vaultMqhToken;

        /// The MQH token: the one typed in Settings > DAEMON wins (developer override), else the vault's.
        private string MqhTokenNow() => !string.IsNullOrEmpty(Settings?.mqhToken) ? Settings.mqhToken : _vaultMqhToken;

        private void InitVault()
        {
            try
            {
                Vault = SecretVault.OpenDefault(Application.persistentDataPath);
                Vault.Changed += () => RefreshVaultPanel();
                if (!Vault.Has(MqhTokenName))
                    Vault.Ensure(MqhTokenName, 32, "token", "allow", "MQH API token: the app sends it as Bearer; the MQH launcher passes it to MQH (MQH_API_TOKEN)");
                _vaultMqhToken = Vault.Reveal(MqhTokenName);
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
                $"Something in Linux ('{Trim(requester, 40)}') asks for '{name}' ({e?.kind}). Allow it?", new[] { "ALLOW ONCE", "ALWAYS", "DENY" }, i =>
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

        private void BuildVault()
        {
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("vault", "VAULT · SECRETS", 0.72f, 0.58f);
            var c = p.Content;
            float leftW = 0.26f;
            var ls = new UiStack(new Rect(c.x, c.y, leftW, c.height), t.Gap);
            _vList = p.Add(new UiList(0.034f, (item, idx) => { _vSel = item.Tag as string; ShowVaultDetail(); }) { Rect = ls.Next(c.height - t.ButtonHeight - t.Gap) });
            p.Add(new UiButton("+ ADD SECRET…", AddSecretPrompt, UiButtonStyle.Primary) { Rect = ls.Next(t.ButtonHeight) });
            var rs = new UiStack(new Rect(c.x + leftW + t.Gap * 2, c.y, c.width - leftW - t.Gap * 2, c.height), t.Gap);
            _vInfo = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Next(0.036f), Wrap = true, Color = t.TextDim, Align = TextAlignmentOptions.TopLeft });
            _vDetail = p.Add(new UiLabel("", t.SmallSize * 1.05f) { Rect = rs.Next(0.10f), Wrap = true, Align = TextAlignmentOptions.TopLeft });
            _vPolicy = p.Add(new UiSegmented("POLICY", new[] { "ASK", "ONCE", "ALLOW", "DENY" }, 0, i => { if (_vSel != null) { Vault.SetPolicy(_vSel, SecretVault.Policies[i]); ShowVaultDetail(); } }) { Rect = rs.Next(0.032f), LabelWidth = 0.08f });
            AddButtons(p, rs.Next(t.ButtonHeight), new (string, Action)[]
            {
                ("NEW VALUE…", () => { if (_vSel != null) SecretValuePrompt(_vSel, Vault.Find(_vSel)?.kind ?? "api-key"); }),
                ("→ MQH ENV", PushSecretToMqh),
                ("RE-ARM", () => { Vault.Rearm(_vSel); Toast("Re-armed: 'once' secrets can be handed out again", "info"); }),
                ("DELETE", () => { if (_vSel != null) Modal("DELETE SECRET", $"Delete '{_vSel}' from the vault?", new[] { "DELETE", "CANCEL" }, i => { if (i == 0) { Vault.Delete(_vSel); _vSel = null; RefreshVaultPanel(); } }); }),
            }, danger: 3);
            p.Add(new UiLabel("ACCESS LOG", t.SmallSize) { Rect = rs.Next(0.018f), UseDisplay = true, Color = t.TextDim });
            _vAudit = p.Add(new UiLabel("", t.SmallSize) { Rect = rs.Rest(), UseMono = true, Wrap = true, Align = TextAlignmentOptions.TopLeft, Color = t.TextDim });
            RefreshVaultPanel();
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
            _vInfo.Set($"key: <b>{(Vault.Sealer.Kind == "android-keystore" ? "Android Keystore" : "dev file (Editor)")}</b> · {Vault.Sealer.Level} · {Vault.Entries.Count} secrets\nstored sealed in {(Vault.Sealer.Kind == "android-keystore" ? "the app's no-backup folder (outside Linux)" : Vault.FilePath)}");
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
