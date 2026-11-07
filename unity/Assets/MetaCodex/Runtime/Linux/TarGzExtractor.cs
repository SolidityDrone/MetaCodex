using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MetaCodex.Linux
{
    /// File operations the extractor needs that .NET can't do on Android (modes, symlinks).
    public interface IFileOps
    {
        void MakeDir(string path, int mode);
        Stream CreateFile(string path);
        void SetMode(string path, int mode);
        void Symlink(string target, string path);
        void CopyFile(string from, string to);
        void DeleteIfExists(string path);
    }

    public sealed class TarStats
    {
        public int Files, Dirs, Symlinks, HardLinks, Skipped;
        public long Bytes;
        public override string ToString() => $"{Files} files, {Dirs} dirs, {Symlinks} symlinks, {HardLinks} hard links (copied), {Skipped} skipped, {Bytes / (1024 * 1024)} MB";
    }

    /// Streams a .tar.gz or a plain .tar (ustar, GNU long names, PAX headers; gzip is detected by its magic bytes:
    /// Android's asset packaging stores "x.tar.gz" decompressed as "x.tar") into a folder. Hard links become copies and
    /// device nodes are skipped (neither is allowed in an app's data folder); owner bits rw(x) are always set
    /// so the fake-root userland can write everywhere; paths that would escape the folder are refused.
    public static class TarGzExtractor
    {
        public static TarStats Extract(Stream compressed, string destRoot, IFileOps fs, Action<float> progress = null, Func<bool> cancelled = null)
        {
            long total = compressed.CanSeek ? compressed.Length : 0;
            var stats = new TarStats();
            var header = new byte[512];
            var buffer = new byte[64 * 1024];
            string longName = null, longLink = null;
            Dictionary<string, string> pax = null;
            int zeroBlocks = 0;
            int sinceProgress = 0;

            bool gzip = IsGzip(compressed);
            using (var tar = gzip ? (Stream)new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true) : new NonClosingStream(compressed))
            {
                while (true)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    if (!ReadFull(tar, header, 512)) break;
                    if (IsZero(header))
                    {
                        if (++zeroBlocks == 2) break;
                        continue;
                    }
                    zeroBlocks = 0;
                    if (!ChecksumOk(header)) throw new InvalidDataException("tar header checksum mismatch");

                    char type = (char)header[156];
                    long size = ParseNumber(header, 124, 12);
                    string name = longName ?? HeaderName(header);
                    string link = longLink ?? CString(header, 157, 100);
                    if (pax != null)
                    {
                        if (pax.TryGetValue("path", out var p)) name = p;
                        if (pax.TryGetValue("linkpath", out var l)) link = l;
                        if (pax.TryGetValue("size", out var s) && long.TryParse(s, out var ps)) size = ps;
                    }
                    int mode = (int)ParseNumber(header, 100, 8) & 0x1FF; // 0777: no setuid/setgid/sticky

                    switch (type)
                    {
                        case 'L': longName = ReadString(tar, size); SkipPadding(tar, size, buffer); continue;
                        case 'K': longLink = ReadString(tar, size); SkipPadding(tar, size, buffer); continue;
                        case 'x': pax = ParsePax(ReadString(tar, size)); SkipPadding(tar, size, buffer); continue;
                        case 'g': Skip(tar, size, buffer); SkipPadding(tar, size, buffer); continue;
                    }

                    string rel = SafeRelative(name);
                    longName = longLink = null;
                    pax = null;
                    if (rel == null)
                    {
                        Skip(tar, size, buffer);
                        SkipPadding(tar, size, buffer);
                        if (name.Trim('.', '/').Length > 0) stats.Skipped++;
                        continue;
                    }
                    string full = Path.Combine(destRoot, rel);

                    switch (type)
                    {
                        case '5':
                            fs.MakeDir(full, mode | 0x1C0); // u+rwx
                            stats.Dirs++;
                            break;
                        case '0': case '\0': case '7':
                            EnsureParent(full, fs);
                            fs.DeleteIfExists(full);
                            using (var output = fs.CreateFile(full)) Copy(tar, output, size, buffer);
                            fs.SetMode(full, mode | 0x180); // u+rw
                            stats.Files++;
                            stats.Bytes += size;
                            SkipPadding(tar, size, buffer); // content already consumed by Copy

                            ReportMaybe(compressed, total, progress, ref sinceProgress);
                            continue;
                        case '2':
                            EnsureParent(full, fs);
                            fs.DeleteIfExists(full);
                            fs.Symlink(link, full);
                            stats.Symlinks++;
                            break;
                        case '1':
                            string target = SafeRelative(link);
                            EnsureParent(full, fs);
                            if (target != null)
                            {
                                fs.DeleteIfExists(full);
                                fs.CopyFile(Path.Combine(destRoot, target), full);
                                fs.SetMode(full, mode | 0x180);
                                stats.HardLinks++;
                            }
                            else stats.Skipped++;
                            break;
                        default: // char/block devices, fifos, sparse files
                            stats.Skipped++;
                            break;
                    }
                    Skip(tar, size, buffer);
                    SkipPadding(tar, size, buffer);
                    ReportMaybe(compressed, total, progress, ref sinceProgress);
                }
            }
            progress?.Invoke(1f);
            return stats;
        }

        private static void ReportMaybe(Stream compressed, long total, Action<float> progress, ref int since)
        {
            if (progress == null || total <= 0 || ++since < 64) return;
            since = 0;
            progress((float)Math.Min(1.0, compressed.Position / (double)total));
        }

        private static void EnsureParent(string full, IFileOps fs)
        {
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) fs.MakeDir(dir, 0x1ED); // 0755
        }

        /// "./usr/bin/x" -> "usr/bin/x"; null for the root entry or anything that leaves the folder.
        public static string SafeRelative(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var parts = new List<string>();
            foreach (var part in name.Replace('\\', '/').Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") return null;
                parts.Add(part);
            }
            return parts.Count == 0 ? null : string.Join("/", parts);
        }

        private static string HeaderName(byte[] h)
        {
            string name = CString(h, 0, 100);
            bool ustar = h[257] == 'u' && h[258] == 's' && h[259] == 't' && h[260] == 'a' && h[261] == 'r';
            if (ustar)
            {
                string prefix = CString(h, 345, 155);
                if (prefix.Length > 0) name = prefix + "/" + name;
            }
            return name;
        }

        private static string CString(byte[] b, int offset, int len)
        {
            int end = offset;
            while (end < offset + len && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, offset, end - offset);
        }

        /// Octal ASCII, or base-256 when the high bit of the first byte is set (GNU large values).
        public static long ParseNumber(byte[] b, int offset, int len)
        {
            if ((b[offset] & 0x80) != 0)
            {
                long v = b[offset] & 0x7F;
                for (int i = 1; i < len; i++) v = (v << 8) | b[offset + i];
                return v;
            }
            long r = 0;
            for (int i = offset; i < offset + len; i++)
            {
                byte c = b[i];
                if (c == 0 || c == ' ') { if (r != 0) break; continue; }
                if (c < '0' || c > '7') break;
                r = r * 8 + (c - '0');
            }
            return r;
        }

        private static bool ChecksumOk(byte[] h)
        {
            long stored = ParseNumber(h, 148, 8);
            long sum = 0;
            for (int i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? ' ' : h[i];
            return sum == stored;
        }

        private static Dictionary<string, string> ParsePax(string data)
        {
            var d = new Dictionary<string, string>();
            int pos = 0;
            var bytes = Encoding.UTF8.GetBytes(data);
            while (pos < bytes.Length)
            {
                int sp = Array.IndexOf(bytes, (byte)' ', pos);
                if (sp < 0) break;
                if (!int.TryParse(Encoding.ASCII.GetString(bytes, pos, sp - pos), out int len) || len <= 0) break;
                int recEnd = pos + len;
                if (recEnd > bytes.Length) break;
                string rec = Encoding.UTF8.GetString(bytes, sp + 1, recEnd - sp - 2); // without trailing \n
                int eq = rec.IndexOf('=');
                if (eq > 0) d[rec.Substring(0, eq)] = rec.Substring(eq + 1);
                pos = recEnd;
            }
            return d;
        }

        private static string ReadString(Stream s, long size)
        {
            if (size > 1 << 20) throw new InvalidDataException("tar meta entry too large");
            var b = new byte[size];
            if (!ReadFull(s, b, (int)size)) throw new EndOfStreamException();
            int end = Array.IndexOf(b, (byte)0);
            return Encoding.UTF8.GetString(b, 0, end < 0 ? b.Length : end);
        }

        private static void Copy(Stream from, Stream to, long size, byte[] buffer)
        {
            while (size > 0)
            {
                int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
                if (n <= 0) throw new EndOfStreamException("truncated tar entry");
                to.Write(buffer, 0, n);
                size -= n;
            }
        }

        private static void Skip(Stream s, long size, byte[] buffer)
        {
            while (size > 0)
            {
                int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
                if (n <= 0) throw new EndOfStreamException("truncated tar entry");
                size -= n;
            }
        }

        private static void SkipPadding(Stream s, long size, byte[] buffer)
        {
            int pad = (int)((512 - size % 512) % 512);
            if (pad > 0) Skip(s, pad, buffer);
        }

        /// Peeks the gzip magic (1f 8b) and rewinds; streams that cannot seek are taken as gzip (the old format).
        public static bool IsGzip(Stream s)
        {
            if (!s.CanSeek) return true;
            long at = s.Position;
            int a = s.ReadByte(), b = s.ReadByte();
            s.Position = at;
            return a == 0x1f && b == 0x8b;
        }

        /// A plain tar is read straight from the caller's stream, which the caller closes.
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _s;
            public NonClosingStream(Stream s) { _s = s; }
            public override bool CanRead => _s.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _s.Length;
            public override long Position { get => _s.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { }
        }

        private static bool ReadFull(Stream s, byte[] b, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = s.Read(b, got, len - got);
                if (n <= 0) return got == 0 ? false : throw new EndOfStreamException("truncated tar stream");
                got += n;
            }
            return true;
        }

        private static bool IsZero(byte[] b)
        {
            for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false;
            return true;
        }
    }
}
