// Core tests without Unity: the terminal emulator against real programs (tmux as the reference), key
// encoding, and the rootfs extractor on the real bundled tarball. Build + run: tests/terminal/run-core-tests.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MetaCodex.Linux;
using MetaCodex.Terminal;

internal static class Program
{
    private static int _fails, _passes;

    private static int Main(string[] args)
    {
        string corpus = args.Length > 0 ? args[0] : "tests/terminal/corpus/cases";
        string rootfs = args.Length > 1 ? args[1] : null;
        string synthTar = args.Length > 2 ? args[2] : null;
        Unit();
        Corpus(corpus);
        if (synthTar != null && File.Exists(synthTar)) SyntheticTar(synthTar);
        if (rootfs != null && File.Exists(rootfs)) RealRootfs(rootfs);
        string overlay = args.Length > 3 ? args[3] : null;
        if (overlay != null && File.Exists(overlay)) Overlay(overlay);
        Console.WriteLine(_fails == 0 ? $"ALL OK ({_passes} checks)" : $"{_fails} FAILED, {_passes} passed");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what, string detail = null)
    {
        if (ok) { _passes++; return; }
        _fails++;
        Console.WriteLine("FAIL " + what + (detail != null ? "\n     " + detail.Replace("\n", "\n     ") : ""));
    }

    private static VtTerminal Run(string input, int rows = 24, int cols = 80)
    {
        var t = new VtTerminal(rows, cols);
        t.Feed(input);
        return t;
    }

    private static string Row(VtTerminal t, int r) => VtTerminal.RowText(t.GetRow(r));

    private static string Esc(byte[] b) => b == null ? "null" : Encoding.UTF8.GetString(b).Replace("\x1b", "\\e");

    private static void Unit()
    {
        var t = Run("hello\r\nworld");
        Check(Row(t, 0) == "hello" && Row(t, 1) == "world" && t.CursorRow == 1 && t.CursorCol == 5, "print + CRLF");

        t = Run("\x1b[31mred\x1b[0m");
        Check(t.GetRow(0)[0].Fg == 1 && t.GetRow(0)[3].Fg == VtTerminal.DefaultColor, "SGR 31 / reset");

        t = Run("\x1b[38;2;10;20;30mX\x1b[38:2::1:2:3mY\x1b[38:5:196mZ\x1b[48;5;21mW");
        var r0 = t.GetRow(0);
        Check(r0[0].Fg == (VtTerminal.TrueColorFlag | 0x0A141E), "truecolor ;", $"{r0[0].Fg:X}");
        Check(r0[1].Fg == (VtTerminal.TrueColorFlag | 0x010203), "truecolor ::", $"{r0[1].Fg:X}");
        Check(r0[2].Fg == 196 && r0[3].Bg == 21, "256 colours");

        t = Run("\x1b[5;10HX");
        Check(t.GetRow(4)[9].Rune == 'X', "CUP");

        t = Run("abc\x1b[2K");
        Check(Row(t, 0) == "", "EL 2");

        t = Run("");
        for (int i = 0; i < 30; i++) t.Feed($"line {i}\r\n");
        Check(t.ScrollbackCount == 7 && Row(t, 0) == "line 7" && VtTerminal.RowText(t.GetScrollbackLine(0)) == "line 0", "scroll into scrollback", $"sb={t.ScrollbackCount} row0={Row(t, 0)}");

        t = Run("main\x1b[?1049halt screen\x1b[?1049l");
        Check(Row(t, 0) == "main" && t.CursorCol == 4 && !t.AltScreen, "alt screen 1049 restore");

        t = Run("\x1b[c");
        Check(Esc(t.TakeReplies()) == "\\e[?62;22c", "DA1");
        t = Run("\x1b[3;4H\x1b[6n");
        Check(Esc(t.TakeReplies()) == "\\e[3;4R", "DSR 6");
        t = Run("\x1b[?2026$p\x1b[?2026h\x1b[?2026$p");
        Check(Esc(t.TakeReplies()) == "\\e[?2026;2$y\\e[?2026;1$y", "DECRQM 2026");
        t = Run("\x1b]11;?\x07");
        Check(Esc(t.TakeReplies()).StartsWith("\\e]11;rgb:"), "OSC 11 query");
        t = Run("\x1b[18t");
        Check(Esc(t.TakeReplies()) == "\\e[8;24;80t", "size report");

        t = Run("中文x");
        var row = t.GetRow(0);
        Check((row[0].Attr & CellAttr.WideHead) != 0 && (row[1].Attr & CellAttr.WideTail) != 0 && row[4].Rune == 'x' && Row(t, 0) == "中文x", "wide chars");

        t = Run("\x1b(0lqk\x1b(B");
        Check(Row(t, 0) == "┌─┐", "DEC graphics");

        t = Run(new string('a', 80) + "b");
        Check(Row(t, 0) == new string('a', 80) && Row(t, 1) == "b", "autowrap");

        t = Run("abcdef\x1b[3D\x1b[2@");
        Check(Row(t, 0) == "abc  def", "ICH");
        t = Run("abcdef\x1b[4D\x1b[2P");
        Check(Row(t, 0) == "abef", "DCH");

        t = Run("\x1b]0;my title\x1b\\");
        Check(t.Title == "my title", "OSC 0 title");

        t = new VtTerminal(24, 80);
        var bytes = Encoding.UTF8.GetBytes("é✻");
        t.Feed(bytes, 0, 1);
        t.Feed(bytes, 1, bytes.Length - 1);
        Check(Row(t, 0) == "é✻", "UTF-8 split across reads");

        t = Run("1\r\n2\r\n3\r\n4\r\n5\x1b[2;4r\x1b[4;1H\n\n");
        Check(Row(t, 0) == "1" && Row(t, 1) == "4" && Row(t, 2) == "" && Row(t, 3) == "" && Row(t, 4) == "5", "scroll region LF",
            string.Join("|", Row(t, 0), Row(t, 1), Row(t, 2), Row(t, 3), Row(t, 4)));

        t = new VtTerminal(5, 10);
        t.Feed("ab\r\ncd\r\nef\r\ngh\r\nij");
        t.Resize(3, 12);
        Check(Row(t, 2) == "ij" && t.CursorRow == 2 && t.ScrollbackCount == 2, "resize keeps cursor line", $"{Row(t, 0)}|{Row(t, 1)}|{Row(t, 2)} sb={t.ScrollbackCount}");

        Check(KeyEncoder.KeyString(TermKey.Up, KeyMods.None, false) == "\x1b[A" && KeyEncoder.KeyString(TermKey.Up, KeyMods.None, true) == "\x1bOA", "keys: arrows");
        Check(KeyEncoder.KeyString(TermKey.Left, KeyMods.Ctrl, false) == "\x1b[1;5D", "keys: ctrl+left");
        Check(KeyEncoder.KeyString(TermKey.Tab, KeyMods.Shift, false) == "\x1b[Z", "keys: shift+tab");
        Check(KeyEncoder.KeyString(TermKey.F5, KeyMods.None, false) == "\x1b[15~", "keys: F5");
        Check(KeyEncoder.Ctrl('c')[0] == 3 && KeyEncoder.Ctrl('[')[0] == 27, "keys: ctrl+c, ctrl+[");
        Check(Encoding.UTF8.GetString(KeyEncoder.Paste("a\nb", true)) == "\x1b[200~a\rb\x1b[201~", "paste bracketed");

        Check(Wcwidth.Of('a') == 1 && Wcwidth.Of(0x4E2D) == 2 && Wcwidth.Of(0x0301) == 0 && Wcwidth.Of(0x1F680) == 2 && Wcwidth.Of(0x2500) == 1, "wcwidth");
        Check(TarGzExtractor.SafeRelative("./usr/bin/x") == "usr/bin/x" && TarGzExtractor.SafeRelative("../x") == null && TarGzExtractor.SafeRelative("/etc/p") == "etc/p" && TarGzExtractor.SafeRelative("./") == null, "tar path safety");
    }

    private static void Corpus(string dir)
    {
        if (!Directory.Exists(dir)) { Check(false, "corpus folder " + dir); return; }
        foreach (var bin in Directory.GetFiles(dir, "*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(bin);
            var t = new VtTerminal(30, 100);
            var data = File.ReadAllBytes(bin);
            // feed in uneven chunks, as reads from a pty arrive
            for (int off = 0, k = 0; off < data.Length; k++)
            {
                int n = Math.Min(data.Length - off, 1 + (k * 37) % 509);
                t.Feed(data, off, n);
                off += n;
            }
            var expected = File.ReadAllText(Path.ChangeExtension(bin, ".screen")).Replace("\r", "").TrimEnd('\n').Split('\n');
            var got = t.DumpScreen().Split('\n');
            var diffs = new List<string>();
            for (int r = 0; r < 30; r++)
            {
                string e = r < expected.Length ? expected[r].TrimEnd() : "";
                string g = r < got.Length ? got[r] : "";
                if (e != g) diffs.Add($"row {r}: expected [{e}]\n           got [{g}]");
            }
            var cur = File.ReadAllText(Path.ChangeExtension(bin, ".cursor")).Trim().Split(',');
            int cx = int.Parse(cur[0]), cy = int.Parse(cur[1]);
            bool cursorOk = cy == t.CursorRow && (cx == t.CursorCol || (cx >= 100 && t.CursorCol == 99));
            Check(diffs.Count == 0 && cursorOk, "corpus " + name,
                (diffs.Count > 0 ? string.Join("\n", diffs.GetRange(0, Math.Min(4, diffs.Count))) + (diffs.Count > 4 ? $"\n... {diffs.Count} rows differ" : "") : "") +
                (cursorOk ? "" : $"\ncursor expected {cx},{cy} got {t.CursorCol},{t.CursorRow}"));
        }
    }

    private sealed class RecordingOps : IFileOps
    {
        public readonly Dictionary<string, string> Links = new Dictionary<string, string>();
        public readonly Dictionary<string, int> Modes = new Dictionary<string, int>();
        public readonly HashSet<string> Files = new HashSet<string>(), Dirs = new HashSet<string>(), Copies = new HashSet<string>();
        private readonly string _root;
        public RecordingOps(string root) { _root = root; }
        private string Rel(string p) => p.Substring(_root.Length).TrimStart('/', '\\').Replace('\\', '/');
        public void MakeDir(string path, int mode) { Dirs.Add(Rel(path)); Modes[Rel(path)] = mode; }
        public Stream CreateFile(string path) { Files.Add(Rel(path)); return Stream.Null; }
        public void SetMode(string path, int mode) { Modes[Rel(path)] = mode; }
        public void Symlink(string target, string path) { Links[Rel(path)] = target; }
        public void CopyFile(string from, string to) { Copies.Add(Rel(to)); }
        public void DeleteIfExists(string path) { }
    }

    private static void SyntheticTar(string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "mcx-tar-null");
        var ops = new RecordingOps(root);
        TarStats st;
        using (var f = File.OpenRead(path)) st = TarGzExtractor.Extract(f, root, ops);
        string longName = "deep/" + new string('d', 120) + "/file-with-a-long-name.txt";
        Check(ops.Files.Contains(longName), "tar: PAX long path", string.Join(", ", ops.Files));
        Check(ops.Links.TryGetValue("deep/link", out var target) && target == "../plain.txt", "tar: symlink");
        Check(ops.Copies.Contains("plain-hardlink.txt"), "tar: hard link copied");
        Check(!ops.Files.Contains("evil.txt") && st.Skipped >= 1, "tar: ../ escape refused", st.ToString());
        Check(ops.Modes.TryGetValue("ro-dir", out int dm) && (dm & 0x1C0) == 0x1C0, "tar: dir keeps u+rwx");
        Check(ops.Modes.TryGetValue("setuid-bin", out int sm) && (sm & 0xE00) == 0 && (sm & 0x40) != 0, "tar: setuid stripped, exec kept", Convert.ToString(sm, 8));
    }

    private static void Overlay(string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "mcx-overlay-null");
        var ops = new RecordingOps(root);
        using (var f = File.OpenRead(path)) TarGzExtractor.Extract(f, root, ops);
        Check(ops.Files.Contains("usr/local/bin/mcx-setup") && ops.Modes.TryGetValue("usr/local/bin/mcx-setup", out int m) && (m & 0x1FF) == 0x1ED,
            "overlay: mcx-setup, mode 0755");
        Check(ops.Modes.TryGetValue("usr", out int um) && um == 0x1ED, "overlay: dirs keep 0755 (never 0777 from NTFS)");
    }

    private static void RealRootfs(string path)
    {
        string root = Path.Combine(Path.GetTempPath(), "mcx-rootfs-null");
        var ops = new RecordingOps(root);
        var t0 = DateTime.UtcNow;
        TarStats st;
        float last = 0;
        using (var f = File.OpenRead(path)) st = TarGzExtractor.Extract(f, root, ops, p => last = p);
        Console.WriteLine($"rootfs parse: {st} in {(DateTime.UtcNow - t0).TotalSeconds:F1} s");
        Check(st.Files == 2677 && st.Dirs == 414 && st.Symlinks == 173 && st.HardLinks == 1 && last == 1f, "rootfs: entry counts (tar tv: 2677 files, 415 dirs incl. ./, 173 links, 1 hard link)", st.ToString());
        Check(ops.Links.TryGetValue("bin", out var b) && b == "usr/bin", "rootfs: /bin -> usr/bin");
        Check(ops.Files.Contains("usr/bin/bash") && ops.Files.Contains("usr/lib/aarch64-linux-gnu/libc.so.6"), "rootfs: bash and glibc present");
        RootfsSetup.Apply(root, new[] { 3003, 9997, 20123 }, 6, ops);
        Check(ops.Files.Contains("etc/resolv.conf") && ops.Files.Contains("etc/apt/apt.conf.d/99metacodex") && ops.Files.Contains("proc/.stat"), "rootfs setup files");
        var binds = RootfsSetup.ProcBinds("/r", p => p == "/proc/stat"); // everything unreadable but /proc/stat
        Check(binds.Count == 2 * (RootfsSetup.ProcFiles.Count - 1) && binds[1].EndsWith(":/proc/loadavg") && !string.Join(" ", binds).Contains(":/proc/stat"), "proc stand-in binds", string.Join(" ", binds));
    }
}
