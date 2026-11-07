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

        private void Say(string s)
        {
            Status = s;
            Log?.Invoke(s);
        }

        public static bool Supported => PtyNative.IsAvailable;

        /// Resolves paths and installs the rootfs if missing. Ends in Ready, Failed or Unsupported.
        public IEnumerator Prepare()
        {
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

        /// Lays the bundled overlay (mcx-setup, later the daemon) over the rootfs when its sha256 changed.
        private IEnumerator ApplyOverlayIfChanged()
        {
            string info = null;
            yield return ReadStreamingText(overlayInfoAsset, t => info = t);
            if (info == null) yield break;
            string applied = Path.Combine(LinuxDir, "overlay.applied");
            if (File.Exists(applied) && File.ReadAllText(applied) == info) yield break;
            string tarPath = Path.Combine(LinuxDir, "overlay.tar.gz");
            yield return CopyStreamingAsset(overlayAsset, tarPath, _ => { });
            if (State == HostState.Failed) yield break;
            var task = Task.Run(() =>
            {
                using (var f = new FileStream(tarPath, FileMode.Open, FileAccess.Read))
                    return TarGzExtractor.Extract(f, RootfsDir, new NativeFileOps());
            });
            while (!task.IsCompleted) yield return null;
            try { File.Delete(tarPath); } catch (Exception) { }
            if (task.IsFaulted) { Say("overlay failed: " + task.Exception?.GetBaseException().Message); yield break; }
            File.WriteAllText(applied, info);
            Say("overlay applied: " + task.Result);
        }

        /// A login shell (or `bash -lc command`) as root in the Debian rootfs, in a pty of the given size.
        public PtySession StartShell(int rows, int cols, string command = null)
        {
            if (State != HostState.Ready) throw new InvalidOperationException("Linux not ready: " + State);
            string proot = Path.Combine(NativeLibDir, "libproot.so");
            string tmp = Path.Combine(LinuxDir, "tmp");
            Directory.CreateDirectory(tmp);

            var argv = new List<string>
            {
                proot,
                "--kill-on-exit",
                "--link2symlink",
                "-0",
                "-r", RootfsDir,
                "-b", "/dev",
                "-b", "/proc",
                "-b", "/sys",
                "-b", Path.Combine(RootfsDir, "tmp") + ":/dev/shm",
                "-b", "/dev/urandom:/dev/random",
                "-b", "/proc/self/fd:/dev/fd",
                "-b", "/proc/self/fd/0:/dev/stdin",
                "-b", "/proc/self/fd/1:/dev/stdout",
                "-b", "/proc/self/fd/2:/dev/stderr",
            };
            argv.AddRange(RootfsSetup.ProcBinds(RootfsDir, CanRead));
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
                "/bin/bash", "-l",
            });
            if (!string.IsNullOrEmpty(command)) { argv.Add("-c"); argv.Add(command); }

            var env = new[]
            {
                "PROOT_LOADER=" + Path.Combine(NativeLibDir, "libproot-loader.so"),
                "PROOT_TMP_DIR=" + tmp,
                "LD_LIBRARY_PATH=" + NativeLibDir,
                "HOME=" + FilesDir,
                "TMPDIR=" + tmp,
                "PATH=/system/bin:/system/xbin",
            };
            return PtySession.Start(proot, argv.ToArray(), env, FilesDir, rows, cols, s => Log?.Invoke(s));
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

        private IEnumerator CopyStreamingAsset(string rel, string dest, Action<float> progress)
        {
            using (var req = new UnityWebRequest(StreamingUrl(rel), UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone) { progress(req.downloadProgress); yield return null; }
                if (req.result != UnityWebRequest.Result.Success) Fail($"read {rel}: {req.error}");
            }
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
