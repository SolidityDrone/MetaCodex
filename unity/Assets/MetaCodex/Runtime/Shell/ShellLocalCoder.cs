using System;
using System.IO;
using MetaCodex.AI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The local coder (docs/local-coder.md): a small coding model (Qwen2.5 Coder 0.5B, 430 MB, optional; a demonstration that agents run offline) in the local AI service,
    /// usable by the agent harnesses MQH runs. Installing it sets three variables in MQH's machine environment:
    /// MCX_LOCAL_CODER_URL + MCX_LOCAL_CODER_KEY (MQH's own Claude plugin, in a PC's MQH only, then lists "Local coder
    /// (this headset, slow)", and only a thread on that model uses it; the headset's Claude Code threads run through the
    /// ACP adapter, which does not list it) and OPENCODE_CONFIG (an extra OpenCode config with the provider
    /// "MetaCodex (this headset)": OpenCode lists the model first under that name). While it works, voice waits (the
    /// service holds the interface AI; TALK says so).
    public sealed partial class MetaCodexShell
    {
        public const string LocalCoderUrl = "http://127.0.0.1:47810";
        private bool _coderEnvSynced;
        private float _coderNextCheck;

        public bool LocalCoderInstalled => ModelInstalled("coder");
        public string LocalCoderInstallState => (string)QvacStatus?["models"]?["coder"]?["install"];
        public float LocalCoderProgress => QvacStatus?["models"]?["coder"]?["progress"]?.Value<float>() ?? 0f;

        private string SharedPath(string name)
        {
            var host = app != null ? app.Host : null;
            string shared = host != null && !string.IsNullOrEmpty(host.SharedDir) ? host.SharedDir : Application.persistentDataPath;
            return Path.Combine(shared, name);
        }

        /// Voice / LLM / agents (AGENTS.md rule 1): install and remove the local coder.
        private void RegisterLocalCoderVoice()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "install_local_coder",
                Description = "Download the local coding model (Qwen2.5 Coder 0.5B, 430 MB) so Claude Code, Codex and OpenCode threads can run offline on this headset (slow); the user confirms first",
                Phrases = new[] { "(install|download|get|add) [the] local (coder|coding model|model)", "(use|set up) [a] local model for (coding|the agents)" },
                Samples = new[] { "i want an offline coding model", "put a coding model on the headset", "can the agents run without internet", "get me the small local coder" },
                Vocabulary = new[] { "local coder" },
                Confirm = true,
                Run = a => { var err = InstallLocalCoder(); return err != null ? "error: " + err : LocalCoderInstalled ? "The local coder is installed" : "Downloading the local coder"; },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "remove_local_coder",
                Description = "Remove the local coding model and its set-up from the agents (frees 430 MB); the user confirms first",
                Phrases = new[] { "(remove|delete|uninstall) [the] local (coder|coding model|model)" },
                Samples = new[] { "get rid of the offline coding model", "free the space of the local coder", "i don't need the local model any more", "drop the small coding model" },
                Confirm = true,
                Run = a => { RemoveLocalCoder(); return "Removed the local coder"; },
            });
        }

        /// The tile's text in AGENTS' GET row.
        public string LocalCoderTileText()
        {
            if (LocalCoderInstalled) return "LOCAL CODER\n✓ ON HEADSET";
            if (LocalCoderInstallState == "downloading" || LocalCoderInstallState == "verifying") return $"LOCAL CODER\n{LocalCoderProgress * 100f:F0} %";
            return "LOCAL CODER\nGET 430 MB ⇣";
        }

        /// GET: downloads the model into the AI service (it checks the sha256), then sets the harnesses up.
        public string InstallLocalCoder()
        {
            if (Qvac == null || !Qvac.IsRunning) return "the local AI service is not running (VOICE > ▶ START)";
            if (LocalCoderInstalled) { SyncLocalCoder(true); return null; }
            Qvac.Client.Install(new[] { "coder" }, (r, e) =>
            {
                if (e != null) { Toast("Local coder: " + e, "error"); return; }
                Toast("Downloading the local coder (Qwen2.5 Coder 0.5B, 430 MB): a small demonstration model. Its threads are slow and voice waits while it works.", "info");
            });
            _coderEnvSynced = false;
            return null;
        }

        /// REMOVE: the model, the MQH variables and the OpenCode config go.
        public void RemoveLocalCoder()
        {
            Qvac?.Client.Remove(new[] { "coder" }, (r, e) => { if (e != null) Log("[coder] remove: " + e); PollVoiceStatus(); });
            foreach (var name in new[] { "MCX_LOCAL_CODER_URL", "MCX_LOCAL_CODER_KEY" })
                Mqh?.DeleteMachineEnvironment(name, _ => { });
            _coderEnvSynced = false; // the OpenCode config stays (no snapshots), written again without the provider
            Toast("Local coder removed", "ok");
        }

        /// Once installed: the OpenCode config file and MQH's machine environment (again after MQH restarts or the key
        /// changes). Called from the AGENTS tick, at most every 5 s.
        public void SyncLocalCoder(bool force = false)
        {
            if (!force && (_coderEnvSynced || Time.unscaledTime < _coderNextCheck)) return;
            _coderNextCheck = Time.unscaledTime + 5f;
            if (Mqh == null || !Mqh.Online) return;
            // every OpenCode thread reads this config: no snapshots (2026-11-13 on the headset: before each step OpenCode
            // snapshotted the thread's folder with its own git; a thread in /root ran git over the whole home for
            // minutes at 70 % CPU and looked idle). The local coder's provider joins it once installed.
            string key = null;
            if (LocalCoderInstalled)
            {
                try { key = File.ReadAllText(SharedPath("local-coder.key")).Trim(); }
                catch (Exception) { return; } // written when the AI service starts
                if (key.Length == 0) return;
            }
            string config = SharedPath("local-coder.opencode.json");
            try
            {
                var cfg = new JObject { ["$schema"] = "https://opencode.ai/config.json", ["snapshot"] = false };
                if (key != null)
                    cfg["provider"] = new JObject
                    {
                        ["metacodex"] = new JObject
                        {
                            ["npm"] = "@ai-sdk/openai-compatible",
                            ["name"] = "MetaCodex (this headset)",
                            ["options"] = new JObject { ["baseURL"] = LocalCoderUrl + "/v1", ["apiKey"] = "{env:MCX_LOCAL_CODER_KEY}" },
                            ["models"] = new JObject { ["local-coder"] = new JObject { ["name"] = "Local coder (this headset, slow)", ["limit"] = new JObject { ["context"] = 16384, ["output"] = 1024 } } },
                        },
                    };
                File.WriteAllText(config, cfg.ToString());
            }
            catch (Exception ex) { Log("[coder] opencode config: " + ex.Message); return; }
            int pending = key != null ? 3 : 1;
            bool ok = true;
            void Done(JToken r) { if (r == null) ok = false; if (--pending == 0) { _coderEnvSynced = ok; if (ok) Log(key != null ? "[coder] MQH environment set: OpenCode lists the local coder" : "[coder] OpenCode config set (no snapshots)"); } }
            if (key != null)
            {
                Mqh.SetMachineEnvironment("MCX_LOCAL_CODER_URL", LocalCoderUrl, "MetaCodex local coder (AI service on this headset)", Done);
                Mqh.SetMachineEnvironment("MCX_LOCAL_CODER_KEY", key, "MetaCodex local coder key (opens only the coder's routes)", Done);
            }
            Mqh.SetMachineEnvironment("OPENCODE_CONFIG", "/mnt/quest/local-coder.opencode.json", "MetaCodex OpenCode config: no snapshots, the local coder's provider", Done);
        }
    }
}
