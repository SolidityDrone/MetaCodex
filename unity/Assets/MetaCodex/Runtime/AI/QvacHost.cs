using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MetaCodex.Linux;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaCodex.AI
{
    /// The local AI service (docs/qvac.md): QVAC speech-to-text, LLM and text-to-speech on the Bare runtime, in
    /// its own process. On the headset the app starts it the way it starts proot: `libbare.so` (the Bare
    /// executable, shipped in nativeLibraryDir where exec is allowed) runs the service JS that
    /// tools/qvac-bundle.sh packs into StreamingAssets/qvac, pinned to cores 0-2 (the cores Android gives
    /// services; Unity keeps 3-5 to itself) at nice 10, CPU only. HTTP on 127.0.0.1:47810 with a token made at
    /// every start (the userland reads it from /mnt/quest/qvac.token). Restarted if it dies (3 times in 5 min).
    /// In the Editor it uses a dev service started by tools/qvac-dev.sh (unity/Bench/qvac-dev.json).
    public sealed class QvacHost : MonoBehaviour
    {
        public enum HostState { Idle, NotBundled, Preparing, Starting, Running, Stopped, Failed }
        public const int DefaultPort = 47810;

        public HostState State { get; private set; } = HostState.Idle;
        public string Status { get; private set; } = "not started";
        public float Progress { get; private set; }
        public QvacClient Client { get; private set; }
        /// Editor: a service started by tools/qvac-dev.sh (in WSL or on the headset over adb).
        public bool IsDev { get; private set; }
        public string RuntimeDir { get; private set; }
        public string DataDir { get; private set; }
        public int Threads { get; set; } = 2;
        public int Port { get; private set; } = DefaultPort;
        public event Action<string> Log;
        public event Action StateChanged;

        private string _filesDir, _nativeLibDir, _sharedDir, _tokenFile;
        private PtySession _proc;
        private bool _wantRunning;
        private int _restarts;
        private float _restartWindowStart;
        private float _restartAt = -1f;
        private readonly StringBuilder _line = new StringBuilder();

        public bool IsRunning => State == HostState.Running;
        public bool IsBusyStarting => State == HostState.Preparing || State == HostState.Starting;

        public void Init(ConcurrentQueue<Action> main, string sharedDir, int threads)
        {
            _sharedDir = sharedDir;
            Threads = Mathf.Clamp(threads, 1, 6);
            Client = new QvacClient("http://127.0.0.1:" + Port, "", main);
            if (Application.platform != RuntimePlatform.Android) { IsDev = true; return; }
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                using (var appInfo = activity.Call<AndroidJavaObject>("getApplicationInfo"))
                {
                    _filesDir = files.Call<string>("getAbsolutePath");
                    _nativeLibDir = appInfo.Get<string>("nativeLibraryDir");
                }
            }
            catch (Exception ex) { Fail("paths: " + ex.Message); return; }
            RuntimeDir = Path.Combine(_filesDir, "qvac", "runtime");
            DataDir = Path.Combine(_filesDir, "qvac", "data");
            _tokenFile = Path.Combine(_filesDir, "qvac", "token");
        }

        /// Model files installed by an earlier session (then the service starts with the app).
        public bool ModelsPresent
        {
            get
            {
                if (IsDev) return DevConfig() != null;
                try { var d = Path.Combine(DataDir ?? "", "models"); return Directory.Exists(d) && Directory.GetFiles(d, "*.sha256").Length > 0; }
                catch (Exception) { return false; }
            }
        }

        public void StartService()
        {
            _wantRunning = true;
            if (State == HostState.Running || IsBusyStarting) return;
            if (IsDev) { if (Application.isPlaying) StartCoroutine(ConnectDev()); else ConnectDevNow(); return; }
            if (State == HostState.Failed && _nativeLibDir == null) return;
            StartCoroutine(StartRoutine());
        }

        public void StopService()
        {
            _wantRunning = false;
            _restartAt = -1f;
            if (IsDev) { Set(HostState.Stopped, "dev service left running (tools/qvac-dev.sh … stop)"); return; }
            if (_proc != null)
            {
                Client.Shutdown();
                var p = _proc;
                _proc = null;
                Task.Run(async () => { await Task.Delay(1500); p.Dispose(); });
            }
            Set(HostState.Stopped, "stopped");
        }

        private void OnApplicationQuit() => StopService();

        private void OnDestroy()
        {
            if (_proc != null) { try { _proc.Dispose(); } catch (Exception) { } _proc = null; }
        }

        private void Update()
        {
            if (_proc == null)
            {
                if (_restartAt > 0f && Time.unscaledTime >= _restartAt && _wantRunning) { _restartAt = -1f; StartCoroutine(StartRoutine()); }
                return;
            }
            while (_proc.TryRead(out var chunk))
            {
                foreach (char ch in Encoding.UTF8.GetString(chunk))
                {
                    if (ch == '\n') { var l = _line.ToString().TrimEnd('\r'); _line.Clear(); if (l.Length > 0) Say("ai: " + l); }
                    else if (_line.Length < 2000) _line.Append(ch);
                }
            }
            if (!_proc.IsRunning)
            {
                int code = _proc.ExitCode;
                _proc.Dispose();
                _proc = null;
                if (!_wantRunning) { Set(HostState.Stopped, "stopped"); return; }
                if (Time.unscaledTime - _restartWindowStart > 300f) { _restartWindowStart = Time.unscaledTime; _restarts = 0; }
                if (++_restarts > 3) { Fail($"the AI service exited with {code} 4 times in 5 min; see files/qvac/data/service.log"); return; }
                Set(HostState.Starting, $"the AI service exited with {code}; restarting ({_restarts}/3)");
                _restartAt = Time.unscaledTime + 2f * _restarts;
            }
        }

        // ------------------------------------------------------------------ headset

        private IEnumerator StartRoutine()
        {
            Set(HostState.Preparing, "preparing the AI runtime");
            string info = null;
            yield return ReadStreaming("qvac/runtime.json", t => info = t);
            if (info == null) { Set(HostState.NotBundled, "this build has no AI runtime (run tools/qvac-bundle.sh before building)"); yield break; }
            string hash;
            try { hash = (string)JObject.Parse(info)["hash"] ?? "?"; }
            catch (Exception ex) { Fail("runtime.json: " + ex.Message); yield break; }
            string bare = Path.Combine(_nativeLibDir, "libbare.so");
            if (!File.Exists(bare)) { Set(HostState.NotBundled, "libbare.so missing from " + _nativeLibDir); yield break; }

            string hashFile = Path.Combine(RuntimeDir, ".hash");
            if (!File.Exists(hashFile) || File.ReadAllText(hashFile).Trim() != hash)
            {
                string qdir = Path.Combine(_filesDir, "qvac");
                Directory.CreateDirectory(qdir);
                string tar = Path.Combine(qdir, "runtime.tar");
                bool copied = false;
                foreach (var name in new[] { "qvac/runtime.tar.gz", "qvac/runtime.tar" }) // Android packs .tar.gz as .tar
                {
                    string err = null;
                    yield return CopyStreaming(name, tar, p => Progress = p * 0.3f, e => err = e);
                    if (err == null) { copied = true; break; }
                }
                if (!copied) { Fail("could not read qvac/runtime.tar(.gz) from the APK"); yield break; }
                string staging = RuntimeDir + ".new";
                var task = Task.Run(() =>
                {
                    var fs = new NativeFileOps();
                    if (Directory.Exists(staging)) NativeFileOps.DeleteTree(staging);
                    fs.MakeDir(staging, 0x1ED);
                    using (var f = new FileStream(tar, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024))
                        TarGzExtractor.Extract(f, staging, fs, p => Progress = 0.3f + p * 0.65f);
                    if (Directory.Exists(RuntimeDir)) NativeFileOps.DeleteTree(RuntimeDir);
                    Directory.Move(staging, RuntimeDir);
                    File.WriteAllText(hashFile, hash);
                    File.Delete(tar);
                });
                while (!task.IsCompleted) { Status = $"unpacking the AI runtime {Progress * 100f:F0}%"; yield return null; }
                if (task.IsFaulted) { Fail("unpack: " + task.Exception?.GetBaseException().Message); yield break; }
                Say("AI runtime " + hash + " unpacked");
            }

            KillStale();
            string token = NewToken();
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(_tokenFile, token);
                if (!string.IsNullOrEmpty(_sharedDir)) File.WriteAllText(Path.Combine(_sharedDir, "qvac.token"), token);
            }
            catch (Exception ex) { Fail("token: " + ex.Message); yield break; }
            Client.Token = token;

            string tmp = Path.Combine(DataDir, "tmp");
            Directory.CreateDirectory(tmp);
            var argv = new[]
            {
                "/system/bin/taskset", "7", // cores 0-2
                "/system/bin/nice", "-n", "10",
                bare, Path.Combine(RuntimeDir, "server", "main.js"),
                "--data", DataDir, "--token-file", _tokenFile, "--port", Port.ToString(), "--threads", Threads.ToString(),
            };
            var env = new[]
            {
                "LD_LIBRARY_PATH=" + Path.Combine(RuntimeDir, "lib") + ":" + _nativeLibDir,
                "HOME=" + DataDir,
                "TMPDIR=" + tmp,
                "PATH=/system/bin",
            };
            try { _proc = PtySession.Start("/system/bin/taskset", argv, env, RuntimeDir, 24, 160, Say); }
            catch (Exception ex) { Fail("start: " + ex.Message); yield break; }
            Set(HostState.Starting, "starting the AI service");
            yield return WaitHealthy(30f);
        }

        private IEnumerator WaitHealthy(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until)
            {
                bool done = false, ok = false;
                Client.Health((r, e) => { ok = r != null && e == null; done = true; });
                while (!done) yield return null;
                if (ok) { Set(HostState.Running, $"running on 127.0.0.1:{Port}" + (IsDev ? " (dev)" : "")); _restarts = Math.Max(0, _restarts - 1); yield break; }
                if (!IsDev && _proc == null) yield break; // exited: Update restarts it
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Fail(IsDev ? "no dev AI service answers (tools/qvac-dev.sh pc start)" : "the AI service did not answer in time; see files/qvac/data/service.log");
        }

        private void KillStale()
        {
            try
            {
                string pidFile = Path.Combine(DataDir, "service.pid");
                if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return;
                string cmdline = $"/proc/{pid}/cmdline";
                if (!File.Exists(cmdline) || !File.ReadAllText(cmdline).Contains("libbare")) return;
                PtyNative.mcx_pty_kill(pid, 15);
                for (int i = 0; i < 20 && File.Exists(cmdline); i++) System.Threading.Thread.Sleep(100);
                if (File.Exists(cmdline)) PtyNative.mcx_pty_kill(pid, 9);
                Say("stopped a leftover AI service (pid " + pid + ")");
            }
            catch (Exception ex) { Say("stale check: " + ex.Message); }
        }

        private static string NewToken()
        {
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder(48);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ Editor (dev service)

        public static string DevConfigPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Bench", "qvac-dev.json"));

        private static JObject DevConfig()
        {
            try { return File.Exists(DevConfigPath) ? JObject.Parse(File.ReadAllText(DevConfigPath)) : null; }
            catch (Exception) { return null; }
        }

        /// Editor and self-tests (no player loop for coroutines): connects to the dev service now, blocking up to ~6 s.
        public bool ConnectDevNow()
        {
            IsDev = true;
            var j = DevConfig();
            if (j == null) { Set(HostState.NotBundled, "no dev AI service: tools/qvac-dev.sh pc start (or device start)"); return false; }
            Client.BaseUrl = (string)j["url"] ?? Client.BaseUrl;
            Client.Token = (string)j["token"] ?? "";
            try
            {
                var t = Client.Json(System.Net.Http.HttpMethod.Get, "/status", null, 5);
                if (!t.Wait(6000)) throw new TimeoutException("no answer in 6 s");
                Set(HostState.Running, $"running at {Client.BaseUrl} (dev service, {(string)j["where"]})");
                return true;
            }
            catch (Exception ex) { Set(HostState.Failed, "dev AI service at " + Client.BaseUrl + ": " + ex.GetBaseException().Message); return false; }
        }

        private IEnumerator ConnectDev()
        {
            var j = DevConfig();
            if (j == null) { Set(HostState.NotBundled, "no dev AI service: tools/qvac-dev.sh pc start (or device start)"); yield break; }
            Client.BaseUrl = (string)j["url"] ?? Client.BaseUrl;
            Client.Token = (string)j["token"] ?? "";
            Set(HostState.Starting, "connecting to the dev AI service (" + (string)j["where"] + ")");
            yield return WaitHealthy(5f);
        }

        // ------------------------------------------------------------------ helpers

        private static string StreamingUrl(string rel)
        {
            string p = Application.streamingAssetsPath + "/" + rel;
            return p.Contains("://") ? p : "file://" + p;
        }

        private static IEnumerator ReadStreaming(string rel, Action<string> done)
        {
            using (var req = UnityWebRequest.Get(StreamingUrl(rel)))
            {
                yield return req.SendWebRequest();
                done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
            }
        }

        private static IEnumerator CopyStreaming(string rel, string dest, Action<float> progress, Action<string> error)
        {
            using (var req = new UnityWebRequest(StreamingUrl(rel), UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone) { progress(req.downloadProgress); yield return null; }
                if (req.result != UnityWebRequest.Result.Success) error(req.error ?? "failed");
            }
        }

        private void Set(HostState s, string status)
        {
            bool changed = s != State;
            State = s;
            Status = status;
            Say(status);
            if (changed) StateChanged?.Invoke();
        }

        private void Fail(string message) => Set(HostState.Failed, message);

        private void Say(string s) => Log?.Invoke(s);
    }
}
