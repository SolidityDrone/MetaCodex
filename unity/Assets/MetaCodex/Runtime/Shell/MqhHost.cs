using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using MetaCodex.Linux;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Runs MQH in the headset's Linux by itself (owner 2026-11-10: "I'd rather not have to connect it manually"):
    /// once the Linux is ready and the MQH bundle is laid (/opt/mqh, LinuxHost), it starts /opt/mqh/bin/mqh-run
    /// through the app's AI service process (Android places that one on cores 0-2, away from the frame loop's 3-5;
    /// the same reason the local AI runs there), with the vault's API token, watches /health and starts it again if
    /// it stops (a few tries, then it says so). A developer URL other than the local one (Settings > DAEMON, e.g. a
    /// PC's MQH through adb reverse) turns this off. In the Editor there is no Linux: it stays Off.
    public sealed class MqhHost
    {
        public enum HostState { Off, Waiting, Starting, Running, Failed }

        public const string LocalUrl = "http://127.0.0.1:38990";
        private const string ServiceClass = "com.pnncl.metacodex.ai.MetaCodexAiService";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public HostState State { get; private set; } = HostState.Off;
        public string Status { get; private set; } = "";
        /// Raised on the main thread when MQH answers (first time, and after each restart).
        public event Action Online;

        private readonly Func<LinuxHost> _linux;
        private readonly Func<string> _url, _token;
        private readonly Action<string> _log;
        private readonly ConcurrentQueue<Action> _main;
        private readonly Action _beforeStart;
        private float _nextCheck, _startedAt, _restartAt = -1f;
        private int _fails, _restarts;
        private float _restartWindow;
        private bool _checking, _adoptProbed;

        public MqhHost(Func<LinuxHost> linux, Func<string> url, Func<string> token, Action beforeStart, Action<string> log, ConcurrentQueue<Action> main)
        {
            _linux = linux; _url = url; _token = token; _beforeStart = beforeStart; _log = log; _main = main;
        }

        /// Ours to run: the headset, the local URL.
        public bool Manages => Application.platform == RuntimePlatform.Android && IsLocal(_url());

        public static bool IsLocal(string url)
        {
            string u = (url ?? "").Trim().TrimEnd('/');
            return u.Length == 0 || u == LocalUrl || u == "http://localhost:38990";
        }

        private void Set(HostState s, string status)
        {
            if (State == s && Status == status) return;
            State = s;
            Status = status;
            _log?.Invoke("mqh: " + status);
        }

        /// Every frame from the shell (cheap: a timer).
        public void Tick()
        {
            float now = Time.unscaledTime;
            if (!Manages) { if (State != HostState.Off) Set(HostState.Off, "not started here (a developer MQH URL is set)"); return; }
            var host = _linux();
            if (host == null || host.State != LinuxHost.HostState.Ready) { Set(HostState.Waiting, "waiting for the Linux"); return; }
            if (string.IsNullOrEmpty(host.MqhApplied)) { Set(HostState.Failed, "MQH is not in this build (tools/mqh-bundle.sh)"); return; }
            if (State == HostState.Off || State == HostState.Waiting)
            {
                // an MQH the service process kept running (the app was restarted) is taken over, not started again
                if (!_adoptProbed) { if (!_checking) { _adoptProbed = true; Set(HostState.Waiting, "looking for a running MQH"); Check(now); } return; }
                Start(host);
                return;
            }
            if (State == HostState.Failed) return;
            if (_restartAt >= 0f && now >= _restartAt) { _restartAt = -1f; Start(host); return; }
            if (now < _nextCheck || _checking) return;
            _nextCheck = now + (State == HostState.Running ? 5f : 1.5f);
            Check(now);
        }

        /// The user asked (DAEMON > RESTART): try again, also after it gave up.
        public void Restart()
        {
            _restarts = 0;
            _fails = 0;
            var host = _linux();
            if (Manages && host != null && host.State == LinuxHost.HostState.Ready) Start(host);
        }

        private void Start(LinuxHost host)
        {
            try
            {
                _beforeStart?.Invoke(); // the vault lets MQH fetch its environment key once more
                string token = _token() ?? "";
                var guest = new[]
                {
                    "MQH_API_TOKEN=" + token,
                    "MQH_MACHINE_ENV_KEY_COMMAND=mcx-secret ensure mqh.machine-env-key 32 mqh",
                };
                var argv = host.ProotArgv("exec /opt/mqh/bin/mqh-run", guest, out var env);
                string log = System.IO.Path.Combine(host.LinuxDir, "mqh-run.log");
                Service(argv, env, host.FilesDir, log);
                _startedAt = Time.unscaledTime;
                _nextCheck = _startedAt + 2f;
                _fails = 0;
                Set(HostState.Starting, "starting MQH in this headset's Linux…");
            }
            catch (Exception ex) { Set(HostState.Failed, "could not start MQH: " + ex.Message); }
        }

        private void Check(float now)
        {
            _checking = true;
            string url = (_url() ?? LocalUrl).TrimEnd('/') + "/health";
            Task.Run(async () =>
            {
                bool ok;
                try { using (var r = await Http.GetAsync(url).ConfigureAwait(false)) ok = r.IsSuccessStatusCode; }
                catch (Exception) { ok = false; }
                _main.Enqueue(() => { _checking = false; OnHealth(ok); });
            });
        }

        private void OnHealth(bool ok)
        {
            float now = Time.unscaledTime;
            if (State == HostState.Waiting)
            {
                if (ok) { _startedAt = now; Set(HostState.Running, "running in this headset's Linux (already up)"); try { Online?.Invoke(); } catch (Exception) { } }
                else { var host = _linux(); if (host != null && host.State == LinuxHost.HostState.Ready) Start(host); }
                return;
            }
            if (ok)
            {
                _fails = 0;
                if (State != HostState.Running)
                {
                    Set(HostState.Running, $"running in this headset's Linux (up in {now - _startedAt:F0} s)");
                    try { Online?.Invoke(); } catch (Exception ex) { _log?.Invoke("mqh online handler: " + ex.Message); }
                }
                return;
            }
            if (State == HostState.Starting && now - _startedAt < 90f) return; // node and the first enrollment take a while
            if (State == HostState.Running && ++_fails < 3) return;
            if (now - _restartWindow > 600f) { _restartWindow = now; _restarts = 0; }
            if (++_restarts > 4)
            {
                Set(HostState.Failed, "MQH stopped 5 times in 10 min: ~/.mqh/logs and mqh-run.log say why; DAEMON > RESTART tries again");
                return;
            }
            float wait = 3f * _restarts * _restarts;
            Set(HostState.Starting, $"MQH is not answering: starting it again in {wait:F0} s ({_restarts}/4)");
            _restartAt = now + wait;
        }

        private static void Service(string[] argv, string[] env, string cwd, string log)
        {
            if (Application.platform != RuntimePlatform.Android) return;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = new AndroidJavaObject("android.content.Intent"))
            {
                intent.Call<AndroidJavaObject>("setClassName", activity, ServiceClass).Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "name", "mqh").Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "argv", argv).Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "env", env).Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "cwd", cwd).Dispose();
                intent.Call<AndroidJavaObject>("putExtra", "log", log).Dispose();
                using (var cn = activity.Call<AndroidJavaObject>("startService", intent))
                    if (cn == null) throw new InvalidOperationException("startService returned null (" + ServiceClass + " missing from the manifest?)");
            }
        }

        /// Self-tests: the URL rule.
        public static bool IsLocalForTest(string url) => IsLocal(url);
    }
}
