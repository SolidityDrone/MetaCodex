using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaCodex.Linux
{
    /// The Linux userland inside the app: on first run unpacks the Debian rootfs bundled in StreamingAssets
    /// into the app's private files folder, then starts programs in it through proot. proot and its loader
    /// ship as lib*.so so Android unpacks them into nativeLibraryDir, the one app folder where exec() is
    /// allowed for targetSdk >= 29; the guest binaries themselves are only mmap'ed by the loader.
    public sealed class LinuxHost : MonoBehaviour
    {
        public enum HostState { Idle, Preparing, Installing, Ready, Failed, Unsupported }

        public HostState State { get; private set; } = HostState.Idle;
        public float Progress { get; private set; }
        public string Status { get; private set; } = "";
        public string FilesDir { get; private set; }
        public string NativeLibDir { get; private set; }
        public string SharedDir { get; private set; }
        public string LinuxDir => Path.Combine(FilesDir, "linux");
        public string RootfsDir => Path.Combine(LinuxDir, "rootfs");
        public event Action<string> Log;

        [Tooltip("Bundled rootfs, relative to StreamingAssets")]
        [SerializeField] private string rootfsAsset = "linux/rootfs.tar.gz";
        [SerializeField] private string rootfsInfoAsset = "linux/rootfs.json";
        [Tooltip("Our files laid over the rootfs (scripts, the control-plane daemon), re-applied when they change")]
        [SerializeField] private string overlayAsset = "linux/overlay.tar.gz";
        [SerializeField] private string overlayInfoAsset = "linux/overlay.json";
        /// MQH for this Linux (tools/mqh-bundle.sh): /opt/mqh, replaced whole when its sha256 changes.
        public const string MqhAsset = "linux/mqh.tar.gz", MqhInfoAsset = "linux/mqh.json";
        /// The MQH bundle on disk now (its mqh.json, "" when none was laid yet: MqhHost waits for it).
        public string MqhApplied => File.Exists(Path.Combine(LinuxDir, "mqh.applied")) ? File.ReadAllText(Path.Combine(LinuxDir, "mqh.applied")) : "";

        private void Say(string s)
        {
            Status = s;
            Log?.Invoke(s);
        }

        public static bool Supported => PtyNative.IsAvailable;

        /// Resolves paths and installs the rootfs if missing. Ends in Ready, Failed or Unsupported.
        /// The headset's time zone as an IANA id ("Europe/Rome"), read once on the main thread (Prepare); null when not
        /// known. Passed to every Linux process as TZ.
        public static string TimeZoneId { get; private set; }

        private static void ReadTimeZone()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var tz = new AndroidJavaClass("java.util.TimeZone"))
                using (var d = tz.CallStatic<AndroidJavaObject>("getDefault"))
                    TimeZoneId = d.Call<string>("getID");
#else
                TimeZoneId = TimeZoneInfo.Local.Id;
#endif
                // an IANA id only (Area/City, letters, digits, + - _ /): anything else is left out
                if (TimeZoneId != null && !System.Text.RegularExpressions.Regex.IsMatch(TimeZoneId, @"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)*$")) TimeZoneId = null;
            }
            catch (Exception) { TimeZoneId = null; }
        }

        public IEnumerator Prepare()
        {
            ReadTimeZone();
            if (State == HostState.Ready) yield break;
            if (!Supported)
            {
                State = HostState.Unsupported;
                Say("Linux needs the headset (Android); in the Editor the terminal uses a bridge or echo.");
                yield break;
            }
            State = HostState.Preparing;
            try { ResolveAndroidPaths(); }
            catch (Exception ex) { Fail("paths: " + ex.Message); yield break; }
            Say($"files {FilesDir}, libs {NativeLibDir}");

            string marker = Path.Combine(LinuxDir, "installed.json");
            if (Directory.Exists(RootfsDir) && File.Exists(marker))
            {
                // the user's installed packages live in there: never reinstall over it automatically
                ApplySetupIfOld();
                yield return ApplyOverlayIfChanged();
                yield return ApplyBundleIfChanged(MqhAsset, MqhInfoAsset, "mqh.applied", "opt/mqh");
                State = HostState.Ready;
                Say("Linux ready");
                yield break;
            }

            State = HostState.Installing;
            Directory.CreateDirectory(LinuxDir);
            string info = null;
            yield return ReadStreamingText(rootfsInfoAsset, t => info = t);
            Say("installing the Linux userland" + (info != null ? $" ({ShortInfo(info)})" : ""));

            string tarPath = Path.Combine(LinuxDir, "rootfs.tar.gz");
            yield return CopyStreamingAsset(rootfsAsset, tarPath, p => Progress = p * 0.1f);
            if (State == HostState.Failed) yield break;

            string staging = Path.Combine(LinuxDir, "rootfs.new");
            var started = DateTime.UtcNow;
            var task = Task.Run(() =>
            {
                var fs = new NativeFileOps();
                // a rootfs without the marker is an interrupted install (the marker is written right after the move)
                if (Directory.Exists(RootfsDir)) NativeFileOps.DeleteTree(RootfsDir);
                if (Directory.Exists(staging)) NativeFileOps.DeleteTree(staging);
                fs.MakeDir(staging, 0x1ED);
                TarStats stats;
                using (var f = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024))
                    stats = TarGzExtractor.Extract(f, staging, fs, p => Progress = 0.1f + p * 0.85f);
                RootfsSetup.Apply(staging, AppGroups(), Environment.ProcessorCount, fs);
                return stats;
            });
            while (!task.IsCompleted) { Say($"installing the Linux userland {Progress * 100f:F0}%"); yield return null; }
            if (task.IsFaulted) { Fail("install: " + task.Exception?.GetBaseException().Message); yield break; }

            Directory.Move(staging, RootfsDir);
            File.WriteAllText(marker, info ?? "{}");
            File.WriteAllText(Path.Combine(LinuxDir, "setup.version"), RootfsSetup.Version.ToString());
            try { File.Delete(tarPath); } catch (Exception) { }
            yield return ApplyOverlayIfChanged();
            yield return ApplyBundleIfChanged(MqhAsset, MqhInfoAsset, "mqh.applied", "opt/mqh");
            Progress = 1f;
            State = HostState.Ready;
            Say($"Linux installed in {(DateTime.UtcNow - started).TotalSeconds:F1} s: {task.Result}");
        }

        private void ApplySetupIfOld()
        {
            string vf = Path.Combine(LinuxDir, "setup.version");
            if (File.Exists(vf) && File.ReadAllText(vf).Trim() == RootfsSetup.Version.ToString()) return;
            try
            {
                RootfsSetup.Apply(RootfsDir, AppGroups(), Environment.ProcessorCount, new NativeFileOps());
                File.WriteAllText(vf, RootfsSetup.Version.ToString());
                Say("Linux setup files updated");
            }
            catch (Exception ex) { Say("setup update failed: " + ex.Message); }
        }

        /// Lays the bundled overlay (mcx-setup, the scripts) over the rootfs when its sha256 changed.
        private IEnumerator ApplyOverlayIfChanged() => ApplyBundleIfChanged(overlayAsset, overlayInfoAsset, "overlay.applied", null);

        /// A bundle laid over the rootfs when its info (sha256) changed; `replaceDir` (rootfs-relative) is deleted
        /// first, so the files a new version no longer has are gone (MQH's /opt/mqh). No bundle in the APK: nothing.
        private IEnumerator ApplyBundleIfChanged(string asset, string infoAsset, string appliedName, string replaceDir)
        {
            string info = null;
            yield return ReadStreamingText(infoAsset, t => info = t);
            if (info == null) yield break;
            string applied = Path.Combine(LinuxDir, appliedName);
            if (File.Exists(applied) && File.ReadAllText(applied) == info) yield break;
            string tarPath = Path.Combine(LinuxDir, Path.GetFileName(asset));
            yield return CopyStreamingAsset(asset, tarPath, _ => { });
            if (State == HostState.Failed) yield break;
            Say("laying " + Path.GetFileName(asset) + " over the Linux");
            var task = Task.Run(() =>
            {
                if (replaceDir != null)
                {
                    string old = Path.Combine(RootfsDir, replaceDir);
                    if (Directory.Exists(old)) NativeFileOps.DeleteTree(old);
                }
                using (var f = new FileStream(tarPath, FileMode.Open, FileAccess.Read))
                    return TarGzExtractor.Extract(f, RootfsDir, new NativeFileOps());
            });
            while (!task.IsCompleted) yield return null;
            try { File.Delete(tarPath); } catch (Exception) { }
            if (task.IsFaulted) { Say(Path.GetFileName(asset) + " failed: " + task.Exception?.GetBaseException().Message); yield break; }
            File.WriteAllText(applied, info);
            Say(Path.GetFileName(asset) + " applied: " + task.Result);
        }

        /// A login shell (or `bash -lc command`) as root in the Debian rootfs, in a pty of the given size.
        public PtySession StartShell(int rows, int cols, string command = null)
        {
            var argv = ProotArgv(command, null, out var env);
            return PtySession.Start(argv[0], argv, env, FilesDir, rows, cols, s => Log?.Invoke(s));
        }

        /// The proot command line of a login shell (`bash -l`, or `bash -l -c command`) as root in the rootfs, with
        /// `guestEnv` ("NAME=value") added inside, and the host environment proot itself needs. StartShell runs it
        /// in a pty; MqhHost hands it to the AI service process (no pty, off the frame loop's cores).
        public string[] ProotArgv(string command, IEnumerable<string> guestEnv, out string[] hostEnv, string root = null)
        {
            if (State != HostState.Ready) throw new InvalidOperationException("Linux not ready: " + State);
            root = root ?? RootfsDir; // another rootfs: the first-run bench's scratch copy
            string proot = Path.Combine(NativeLibDir, "libproot.so");
            string tmp = Path.Combine(LinuxDir, "tmp");
            Directory.CreateDirectory(tmp);

            var argv = new List<string>
            {
                proot,
                "--kill-on-exit",
                "--link2symlink",
                "-0",
                "-r", root,
                "-b", "/dev",
                "-b", "/proc",
                "-b", "/sys",
                "-b", Path.Combine(root, "tmp") + ":/dev/shm",
                "-b", "/dev/urandom:/dev/random",
                "-b", "/proc/self/fd:/dev/fd",
                "-b", "/proc/self/fd/0:/dev/stdin",
                "-b", "/proc/self/fd/1:/dev/stdout",
                "-b", "/proc/self/fd/2:/dev/stderr",
            };
            argv.AddRange(RootfsSetup.ProcBinds(root, CanRead));
            if (!string.IsNullOrEmpty(SharedDir) && Directory.Exists(SharedDir))
            {
                argv.Add("-b");
                argv.Add(SharedDir + ":/mnt/quest");
            }
            argv.AddRange(new[]
            {
                "-w", "/root",
                "/usr/bin/env", "-i",
                "HOME=/root", "USER=root", "LOGNAME=root", "SHELL=/bin/bash",
                "TERM=xterm-256color", "COLORTERM=truecolor", "LANG=C.UTF-8", "TMPDIR=/tmp",
                "PATH=/root/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "METACODEX=1",
            });
            // the headset's time zone (feat/ready-on-install): without it the Linux clock reads UTC (git commit times, an
            // agent's "today"); the zone files are in the image (/usr/share/zoneinfo)
            if (!string.IsNullOrEmpty(TimeZoneId)) argv.Add("TZ=" + TimeZoneId);
            if (guestEnv != null) argv.AddRange(guestEnv);
            argv.AddRange(new[] { "/bin/bash", "-l" });
            if (!string.IsNullOrEmpty(command)) { argv.Add("-c"); argv.Add(command); }

            hostEnv = new[]
            {
                "PROOT_LOADER=" + Path.Combine(NativeLibDir, "libproot-loader.so"),
                "PROOT_TMP_DIR=" + tmp,
                "LD_LIBRARY_PATH=" + NativeLibDir,
                "HOME=" + FilesDir,
                "TMPDIR=" + tmp,
                "PATH=/system/bin:/system/xbin",
            };
            return argv.ToArray();
        }

        /// Starts over: deletes the userland (packages, /root, /work). Never follows symlinks.
        public void DeleteUserland()
        {
            if (Directory.Exists(LinuxDir)) NativeFileOps.DeleteTree(LinuxDir);
            State = HostState.Idle;
            Say("Linux userland deleted");
        }

        private static bool CanRead(string path)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read)) { f.ReadByte(); }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static int[] AppGroups()
        {
            var g = new int[64];
            int n = PtyNative.mcx_ids(out int _, out int gid, g, g.Length);
            var all = new List<int> { gid };
            for (int i = 0; i < n; i++) all.Add(g[i]);
            return all.ToArray();
        }

        private void ResolveAndroidPaths()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
            using (var appInfo = activity.Call<AndroidJavaObject>("getApplicationInfo"))
            {
                FilesDir = files.Call<string>("getAbsolutePath");
                NativeLibDir = appInfo.Get<string>("nativeLibraryDir");
            }
#else
            FilesDir = Application.persistentDataPath;
            NativeLibDir = Path.GetFullPath(".");
#endif
            SharedDir = Application.persistentDataPath; // /sdcard/Android/data/<pkg>/files: adb and the user can reach it
            if (!File.Exists(Path.Combine(NativeLibDir, "libproot.so")))
                throw new FileNotFoundException("libproot.so not in " + NativeLibDir + " (extractNativeLibs / useLegacyPackaging off?)");
        }

        private IEnumerator ReadStreamingText(string rel, Action<string> done)
        {
            using (var req = UnityWebRequest.Get(StreamingUrl(rel)))
            {
                yield return req.SendWebRequest();
                done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
            }
        }

        /// Copies a StreamingAssets file out of the APK. Android's asset packaging stores "x.tar.gz" decompressed
        /// as "x.tar" (verified in the 0.3 APK: assets/linux/rootfs.tar, overlay.tar), so ".gz" names fall back
        /// to the name without it; the extractor reads both formats.
        /// The first start, measured without touching the installed userland (feat/ready-on-install): the shipped rootfs
        /// copied out of the APK, unpacked and set up into a scratch folder, its shipped Debian packages installed there
        /// with dpkg (offline) and Node and git run from it; then the folder is deleted. `report` gets one line per step
        /// and a total. Control port: `system bench-first-run`.
        public IEnumerator BenchFirstRun(Action<string> report)
        {
            if (State != HostState.Ready) { report("bench: Linux not ready"); yield break; }
            string dir = Path.Combine(FilesDir, "bench-firstrun"), tar = Path.Combine(dir, "rootfs.tar.gz"), root = Path.Combine(dir, "rootfs");
            var total = System.Diagnostics.Stopwatch.StartNew();
            var step = System.Diagnostics.Stopwatch.StartNew();
            var clean = Task.Run(() => { if (Directory.Exists(dir)) NativeFileOps.DeleteTree(dir); Directory.CreateDirectory(dir); });
            while (!clean.IsCompleted) yield return null;
            step.Restart();
            yield return CopyStreamingAsset(rootfsAsset, tar, _ => { }, failHost: false);
            if (!File.Exists(tar)) { report("bench: the rootfs did not copy out of the APK"); yield break; }
            report($"rootfs out of the APK: {step.Elapsed.TotalSeconds:F1} s ({new FileInfo(tar).Length / 1048576} MB)");
            step.Restart();
            var unpack = Task.Run(() =>
            {
                var fs = new NativeFileOps();
                fs.MakeDir(root, 0x1ED);
                TarStats st;
                using (var f = new FileStream(tar, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024)) st = TarGzExtractor.Extract(f, root, fs, null);
                RootfsSetup.Apply(root, AppGroups(), Environment.ProcessorCount, fs);
                File.Delete(tar);
                return st;
            });
            while (!unpack.IsCompleted) yield return null;
            if (unpack.IsFaulted) { report("bench: unpack failed: " + unpack.Exception?.GetBaseException().Message); yield break; }
            report($"unpacked and set up: {step.Elapsed.TotalSeconds:F1} s ({unpack.Result})");
            step.Restart();
            string cmd = "if ls /var/cache/metacodex/base-debs/*.deb >/dev/null 2>&1; then nice -n 10 dpkg --force-confdef --force-confold -i /var/cache/metacodex/base-debs/*.deb >/dev/null 2>&1 && echo DPKG_OK || echo DPKG_FAILED; else echo NO_DEBS; fi; node -v; git --version; python3 --version";
            string output = null;
            var run = Task.Run(() =>
            {
                var argv = ProotArgv(cmd, null, out var env, root);
                var sb = new System.Text.StringBuilder();
                using (var s = PtySession.Start(argv[0], argv, env, FilesDir, 24, 200, null))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalMinutes < 10)
                    {
                        while (s.TryRead(out var chunk)) sb.Append(System.Text.Encoding.UTF8.GetString(chunk));
                        if (!s.IsRunning) { while (s.TryRead(out var chunk)) sb.Append(System.Text.Encoding.UTF8.GetString(chunk)); break; }
                        System.Threading.Thread.Sleep(20);
                    }
                }
                output = sb.ToString().Replace("\r", "").Trim();
            });
            while (!run.IsCompleted) yield return null;
            report($"shipped packages (dpkg, offline) + node/git/python3: {step.Elapsed.TotalSeconds:F1} s: " + (output ?? run.Exception?.GetBaseException().Message ?? "").Replace("\n", " | "));
            var del = Task.Run(() => NativeFileOps.DeleteTree(dir));
            while (!del.IsCompleted) yield return null;
            report($"first start of the Linux: {total.Elapsed.TotalSeconds:F1} s in all (scratch copy deleted)");
        }

        private IEnumerator CopyStreamingAsset(string rel, string dest, Action<float> progress, bool failHost = true)
        {
            var names = rel.EndsWith(".gz") ? new[] { rel, rel.Substring(0, rel.Length - 3) } : new[] { rel };
            string error = null;
            foreach (var name in names)
            {
                using (var req = new UnityWebRequest(StreamingUrl(name), UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                    var op = req.SendWebRequest();
                    while (!op.isDone) { progress(req.downloadProgress); yield return null; }
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        if (name != rel) Say($"{rel} is packed as {name}");
                        yield break;
                    }
                    error = req.error;
                }
            }
            if (failHost) Fail($"read {rel}: {error}");
            else Say($"read {rel}: {error}");
        }

        private static string StreamingUrl(string rel)
        {
            string p = Application.streamingAssetsPath + "/" + rel;
            return p.Contains("://") ? p : "file://" + p;
        }

        private static string ShortInfo(string json)
        {
            var d = JsonUtility.FromJson<RootfsInfo>(json);
            return d == null ? "" : $"{d.distro} {d.tag} {d.arch}, {d.sizeBytes / (1024 * 1024)} MB";
        }

        [Serializable]
        private class RootfsInfo
        {
            public string distro, tag, arch, layerDigest;
            public long sizeBytes;
        }

        private void Fail(string message)
        {
            State = HostState.Failed;
            Say("Linux failed: " + message);
        }
    }
}
