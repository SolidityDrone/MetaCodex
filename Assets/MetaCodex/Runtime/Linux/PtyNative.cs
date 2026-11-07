using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MetaCodex.Linux
{
    /// P/Invoke into libmcxpty.so (Native/pty/mcx_pty.c). Android only; calls throw DllNotFoundException
    /// elsewhere, so callers check IsAvailable first.
    public static class PtyNative
    {
        private const string Lib = "mcxpty";

        public static bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return Environment.OSVersion.Platform == PlatformID.Unix && File.Exists("libmcxpty.so");
#endif
            }
        }

        [DllImport(Lib)] public static extern int mcx_pty_spawn([MarshalAs(UnmanagedType.LPStr)] string path,
            byte[] argvBlob, int argvLen, int argc, byte[] envBlob, int envLen, int envc,
            [MarshalAs(UnmanagedType.LPStr)] string cwd, int rows, int cols, out int pid);
        [DllImport(Lib)] public static extern int mcx_pty_read(int fd, byte[] buf, int len, int timeoutMs);
        [DllImport(Lib)] public static extern int mcx_pty_write(int fd, byte[] buf, int len);
        [DllImport(Lib)] public static extern int mcx_pty_resize(int fd, int rows, int cols);
        [DllImport(Lib)] public static extern int mcx_pty_wait(int pid, int nohang);
        [DllImport(Lib)] public static extern int mcx_pty_kill(int pid, int sig);
        [DllImport(Lib)] public static extern int mcx_pty_close(int fd);
        [DllImport(Lib)] public static extern int mcx_symlink([MarshalAs(UnmanagedType.LPStr)] string target, [MarshalAs(UnmanagedType.LPStr)] string linkpath);
        [DllImport(Lib)] public static extern int mcx_chmod([MarshalAs(UnmanagedType.LPStr)] string path, int mode);
        [DllImport(Lib)] public static extern int mcx_lstat_type([MarshalAs(UnmanagedType.LPStr)] string path);
        [DllImport(Lib)] public static extern int mcx_ids(out int uid, out int gid, int[] groups, int maxGroups);
        [DllImport(Lib)] public static extern int mcx_errno_text(int err, byte[] buf, int len);

        public static string ErrorText(int err)
        {
            try
            {
                var b = new byte[128];
                mcx_errno_text(err, b, b.Length);
                int end = Array.IndexOf(b, (byte)0);
                return $"{Encoding.UTF8.GetString(b, 0, end < 0 ? b.Length : end)} ({-err})";
            }
            catch (Exception) { return $"errno {-err}"; }
        }

        /// Strings joined with NULs, the format mcx_pty_spawn takes.
        public static byte[] Blob(string[] items, out int length)
        {
            var ms = new MemoryStream();
            foreach (var s in items)
            {
                var b = Encoding.UTF8.GetBytes(s);
                ms.Write(b, 0, b.Length);
                ms.WriteByte(0);
            }
            length = (int)ms.Length;
            return ms.ToArray();
        }
    }

    /// IFileOps on Android through libmcxpty (chmod, symlink), plain .NET for the rest.
    public sealed class NativeFileOps : IFileOps
    {
        public void MakeDir(string path, int mode)
        {
            Directory.CreateDirectory(path);
            Check(PtyNative.mcx_chmod(path, mode), "chmod", path);
        }

        public Stream CreateFile(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

        public void SetMode(string path, int mode) => Check(PtyNative.mcx_chmod(path, mode), "chmod", path);

        public void Symlink(string target, string path) => Check(PtyNative.mcx_symlink(target, path), "symlink", path);

        public void CopyFile(string from, string to)
        {
            if (File.Exists(from)) File.Copy(from, to, true);
        }

        public void DeleteIfExists(string path) => DeleteTree(path);

        /// Deletes a file, link or directory tree without ever following a symlink: a guest link such as
        /// "x -> /sdcard" resolves to the real shared storage from the app's point of view, so a recursive
        /// delete that followed it would wipe the user's files.
        public static void DeleteTree(string path)
        {
            int type = PtyNative.mcx_lstat_type(path);
            if (type == 0) return;
            if (type != 2)
            {
                File.Delete(path); // unlink: removes the link itself
                return;
            }
            foreach (var entry in Directory.GetFileSystemEntries(path)) DeleteTree(entry);
            Directory.Delete(path, false);
        }

        private static void Check(int r, string what, string path)
        {
            if (r < 0) throw new IOException($"{what} {path}: {PtyNative.ErrorText(r)}");
        }
    }
}
