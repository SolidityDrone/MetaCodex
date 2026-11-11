using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The way back from native apps MetaCodex launched (docs/native-build-and-install.md, "Back and report").
    /// `system launch <package> [thread <id>]` gives each launch a random id (only that app gets it, in its launch
    /// intent) and remembers which thread launched it. The app's built-in menu (mcx_xr) starts MetaCodex again with that
    /// id: "Back to MetaCodex" shows "Back from <app>" and brings the thread forward; "Report to MetaCodex" also
    /// brings a picture of the app and its last log lines, which land in that thread's composer as attachments with a
    /// "Feedback on <app> <version>:" draft. Returns with an id MetaCodex did not give out (or, on Android 14+, from
    /// another package than the one launched) are ignored: other apps cannot put things into a thread.
    public sealed partial class MetaCodexShell
    {
        private JObject _launches;
        private float _returnCheckAt;
        private string _rpThread, _rpDraft, _rpLabel;
        private List<Attachment> _rpAttach;
        private float _rpUntil;

        private static string LaunchesPath => Path.Combine(Application.persistentDataPath, "launches.json");

        private JObject Launches
        {
            get
            {
                if (_launches != null) return _launches;
                try { _launches = File.Exists(LaunchesPath) ? JObject.Parse(File.ReadAllText(LaunchesPath)) : new JObject(); }
                catch (Exception) { _launches = new JObject(); }
                return _launches;
            }
        }

        /// A new launch id for `package`, remembered with the thread to come back to (the given one, else the open
        /// thread, else the last used one) for a day; at most 50 kept.
        public string RegisterLaunch(string package, string thread)
        {
            var bytes = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string id = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(thread)) thread = _threadOpen ?? LastUsedThread();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var p in Launches.Properties().ToList())
                if (now - ((long?)p.Value["t"] ?? 0) > 86400) p.Remove();
            while (Launches.Count >= 50) Launches.Properties().OrderBy(p => (long?)p.Value["t"] ?? 0).First().Remove();
            Launches[id] = new JObject { ["pkg"] = package, ["thread"] = thread ?? "", ["t"] = now };
            try { File.WriteAllText(LaunchesPath, Launches.ToString()); } catch (Exception ex) { Log("launches: " + ex.Message); }
            return id;
        }

        private string LastUsedThread()
        {
            LoadThreadUse();
            return _threadUsedAt.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        }

        /// Every frame from TickSessionContext; the intent is read once a second (and at once after a resume).
        private void TickReturn()
        {
            ApplyPendingReport();
            if (Application.platform != RuntimePlatform.Android || Time.unscaledTime < _returnCheckAt) return;
            _returnCheckAt = Time.unscaledTime + 1f;
            string json = SystemBridge.TakeReturn(Clipboard.Dir);
            if (!string.IsNullOrEmpty(json)) HandleReturn(json);
        }

        /// One return (the JSON of MetaCodexReturn.take); public for the self-test.
        public void HandleReturn(string json)
        {
            JObject j;
            try { j = JObject.Parse(json); }
            catch (Exception) { Log("return: unreadable " + json); return; }
            if (j["error"] != null) { Log("return: " + (string)j["error"]); return; }
            string from = (string)j["from"] ?? "", id = (string)j["launchId"] ?? "", label = (string)j["label"];
            string version = (string)j["version"] ?? "", png = (string)j["png"] ?? "", log = (string)j["log"] ?? "";
            string launchedFrom = (string)j["launchedFrom"] ?? "";
            bool report = (bool?)j["report"] ?? false;
            if (string.IsNullOrEmpty(label)) label = from;
            var rec = string.IsNullOrEmpty(id) ? null : Launches[id] as JObject;
            bool known = rec != null && (string)rec["pkg"] == from && (launchedFrom.Length == 0 || launchedFrom == from);
            if (!known)
            {
                Log($"return from {from}: launch id not given out by this MetaCodex{(launchedFrom.Length > 0 ? " (sent by " + launchedFrom + ")" : "")}: report ignored");
                foreach (var f in new[] { png, log }) if (f.Length > 0) try { File.Delete(f); } catch (Exception) { }
                if (!report) Toast("Back from " + label, "info");
                return;
            }
            string thread = (string)rec["thread"];
            if (string.IsNullOrEmpty(thread)) thread = LastUsedThread();
            Log($"return from {from} {version}{(report ? " with a report" : "")} -> thread {thread ?? "-"}");
            if (!report)
            {
                Toast("Back from " + label, "info");
                if (!string.IsNullOrEmpty(thread)) OpenThread(thread);
                return;
            }
            var att = new List<Attachment>();
            if (png.Length > 0 && File.Exists(png))
            {
                byte[] bytes = File.ReadAllBytes(png);
                int w = bytes.Length > 24 ? (bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19]) : 0;
                int h = bytes.Length > 24 ? (bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23]) : 0;
                var item = Clipboard.Add(bytes, w, h, "app " + label, from);
                try { File.Delete(png); } catch (Exception) { }
                att.Add(CaptureAttachment(item));
            }
            if (log.Length > 0 && File.Exists(log))
                att.Add(new Attachment { Kind = "file", LinuxPath = Clipboard.LinuxDir + "/" + Path.GetFileName(log), Name = "log " + label, Mime = "text/plain", Size = new FileInfo(log).Length });
            if (string.IsNullOrEmpty(thread))
            {
                Toast($"Report from {label}: the picture is in CLIPBOARD (no thread to put it in)", "warn");
                return;
            }
            OpenThread(thread);
            _rpThread = thread;
            _rpDraft = $"Feedback on {label} {version}: ";
            _rpLabel = label;
            _rpAttach = att;
            _rpUntil = Time.unscaledTime + 15f;
            ApplyPendingReport();
        }

        /// Once the thread is open with its composer: the attachments and the draft, then a hint to speak or type.
        private void ApplyPendingReport()
        {
            if (_rpThread == null) return;
            if (Time.unscaledTime > _rpUntil) { _rpThread = null; return; }
            if (_threadOpen != _rpThread || _thComposer == null || AttachList == null) return;
            var l = AttachList;
            foreach (var a in _rpAttach)
                if (!l.Any(x => x.LinuxPath == a.LinuxPath)) l.Add(a);
            RefreshAttachRow();
            string cur = (_thComposer.Text ?? "").Trim();
            _thComposer.Set(cur.Length == 0 ? _rpDraft : cur + "\n" + _rpDraft);
            _thComposer.OnChanged?.Invoke(_thComposer.Text);
            Toast($"Report from {_rpLabel} attached ({_rpAttach.Count}): ◉ to say what you saw, then SEND", "ok");
            _rpThread = null;
        }

        public string PendingReportThreadForTest => _rpThread;
    }
}
