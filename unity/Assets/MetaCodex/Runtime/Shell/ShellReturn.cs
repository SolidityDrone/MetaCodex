using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using MetaCodex.AI;
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
            if (Time.unscaledTime >= _returnCheckAt) TickLaunchRequests();
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

        // ------------------------------------------------------------------ launch requests (owner 2026-11-11)
        // Agents never take the user out of MetaCodex by themselves, whatever the thread's approval mode: a launch, or a
        // first install (Android's dialog), arrives here as a request; the user taps LAUNCH / INSTALL (dialog, voice) or
        // LATER. Builds, checks and silent updates of installed apps go ahead (they do not interrupt).

        private sealed class LaunchRequest { public string Id, Kind, Pkg, Label, Version, Thread, Apk, State; public long T; }
        private readonly List<LaunchRequest> _launchReqs = new List<LaunchRequest>();

        /// kind "launch" or "install" (apk = the Android path of the APK). Returns at once.
        public string RequestLaunch(string kind, string pkg, string label, string version, string thread, string apk)
        {
            _launchReqs.RemoveAll(x => x.Pkg == pkg && x.State == "pending");
            var b = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            var r = new LaunchRequest
            {
                Id = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(), Kind = kind, Pkg = pkg,
                Label = string.IsNullOrEmpty(label) ? pkg : label, Version = version ?? "", Apk = apk,
                Thread = string.IsNullOrEmpty(thread) ? _threadOpen ?? LastUsedThread() : thread, State = "pending",
                T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            _launchReqs.Add(r);
            while (_launchReqs.Count > 20) _launchReqs.RemoveAt(0);
            UpdateLaunchVoice();
            Toast($"App ready: {r.Label} {r.Version}: {(kind == "install" ? "INSTALL" : "LAUNCH")} or LATER (or say \"launch the app\")", "ok");
            AskLaunch(r);
            Log($"launch request {r.Id}: {kind} {pkg} for thread {r.Thread ?? "-"}");
            return $"requested {kind} of {pkg} ({r.Id}): waiting for the user's tap in MetaCodex";
        }

        private void AskLaunch(LaunchRequest r)
        {
            bool install = r.Kind == "install";
            Modal("APP READY", $"{r.Label} {r.Version}\n" + (install
                    ? "Install it? Android asks once more; then MetaCodex offers to open it."
                    : "Open it now? MetaCodex waits in the background; the app's menu (left menu button or left palm pinch) brings you back."),
                new[] { install ? "INSTALL" : "LAUNCH", "LATER" },
                i => { if (i == 0) DoLaunchRequest(r); else Toast($"{r.Label} waits: say \"launch the app\" or ask again when you are ready", "info"); });
        }

        private string DoLaunchRequest(LaunchRequest r)
        {
            if (r.State != "pending") return r.Label + " is " + r.State;
            string res;
            if (r.Kind == "install")
            {
                res = SystemBridge.InstallApk(r.Apk);
                r.State = res.StartsWith("installing") ? "installing" : "failed: " + res;
            }
            else
            {
                res = SystemBridge.LaunchApp(r.Pkg, RegisterLaunch(r.Pkg, r.Thread));
                r.State = res.StartsWith("ok") ? "launched" : "failed: " + res;
            }
            r.T = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            UpdateLaunchVoice();
            Log($"launch request {r.Id}: {r.State}");
            return r.State;
        }

        /// From TickReturn: an install the user allowed turns into a launch offer when Android is done.
        private void TickLaunchRequests()
        {
            foreach (var r in _launchReqs.Where(x => x.State == "installing").ToList())
            {
                string st = SystemBridge.InstallStatus();
                if (st.StartsWith("success")) { r.Kind = "launch"; r.State = "pending"; UpdateLaunchVoice(); AskLaunch(r); }
                else if (st.StartsWith("failure")) { r.State = "failed: " + st; UpdateLaunchVoice(); }
            }
        }

        /// `system launch-status <package>`: pending | launched | declined | installing | failed: ... | none, with its age.
        public string LaunchStatus(string pkg)
        {
            var r = _launchReqs.LastOrDefault(x => x.Pkg == pkg);
            if (r == null) return "none: no request for " + pkg;
            return $"{r.State} ({r.Kind} {r.Label} {r.Version}, {DateTimeOffset.UtcNow.ToUnixTimeSeconds() - r.T} s ago)";
        }

        private LaunchRequest PendingLaunch => _launchReqs.LastOrDefault(x => x.State == "pending");

        private void UpdateLaunchVoice()
        {
            bool any = PendingLaunch != null;
            Voice?.SetEnabled("launch_pending_app", any);
            Voice?.SetEnabled("decline_pending_app", any);
        }

        private void RegisterLaunchVoiceCommands()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "launch_pending_app",
                Description = "Open (or install) the app an agent built and asked to launch: the newest waiting one",
                Phrases = new[] { "(launch|open|start|run) [the] [new] app", "(launch|open|start) it", "(install) [the] [new] app" },
                Samples = new[]
                {
                    "ok launch the app now", "let me try the new app", "open the app the agent made", "go ahead and start it",
                },
                Run = a => { var r = PendingLaunch; return r == null ? "No app is waiting" : r.Label + ": " + DoLaunchRequest(r); },
            });
            Voice.Register(new VoiceCommand
            {
                Name = "decline_pending_app",
                Description = "Do not open the app an agent asked to launch (the agent sees it declined)",
                Phrases = new[] { "(do not|don't|no) (launch|open|start) [the|it] [app]", "(decline|skip|cancel) [the] [app] launch" },
                Samples = new[]
                {
                    "not now, don't open the app", "skip launching it", "cancel the app launch", "no i don't want to open it",
                },
                Run = a => { var r = PendingLaunch; if (r == null) return "No app is waiting"; r.State = "declined"; r.T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); UpdateLaunchVoice(); return r.Label + ": not opened"; },
            });
            UpdateLaunchVoice();
        }

        public int PendingLaunchCountForTest => _launchReqs.Count(x => x.State == "pending");
        public string LaunchPendingForTest() { var r = PendingLaunch; return r == null ? "none" : DoLaunchRequest(r); }
    }
}
