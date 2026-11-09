using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MetaCodex.Linux
{
    /// The Android-specific files a stock Debian rootfs needs before it runs under proot inside an app,
    /// the same fixes Termux's proot-distro applies at install: DNS (Android has no /etc/resolv.conf),
    /// apt without its _apt sandbox user, names for the app's Android groups, and stand-ins for /proc
    /// files that apps may not read. Idempotent; rerun after an app update with Apply().
    public static class RootfsSetup
    {
        public const int Version = 2; // 2: the welcome text says what first use installs

        public static void Apply(string root, int[] groups, int cpuCount, IFileOps fs)
        {
            Write(root, "etc/resolv.conf", "nameserver 1.1.1.1\nnameserver 8.8.8.8\n", fs);
            Write(root, "etc/hosts", "127.0.0.1   localhost metacodex\n::1         localhost ip6-localhost ip6-loopback\n", fs);
            Write(root, "etc/hostname", "metacodex\n", fs);
            Write(root, "etc/apt/apt.conf.d/99metacodex",
                "APT::Sandbox::User \"root\";\nAcquire::Retries \"3\";\nDpkg::Use-Pty \"0\";\n", fs);
            Write(root, "etc/profile.d/metacodex.sh", Profile, fs);
            Write(root, "etc/metacodex/motd", Motd, fs);
            Write(root, "etc/metacodex/release", $"metacodex-rootfs-setup {Version}\n", fs);
            AddGroups(root, groups, fs);
            WriteProcStandIns(root, Math.Max(1, cpuCount), fs);
            foreach (var dir in new[] { "tmp", "var/tmp", "root", "mnt/quest", "work" })
                fs.MakeDir(Path.Combine(root, dir), 0x1FF); // 0777: owner is the app uid either way
        }

        /// Bind arguments for proot: the stand-in for every /proc file the app cannot read.
        public static List<string> ProcBinds(string root, Func<string, bool> readable)
        {
            var binds = new List<string>();
            foreach (var pair in ProcFiles)
            {
                if (readable(pair.Key)) continue;
                binds.Add("-b");
                binds.Add(Path.Combine(root, pair.Value) + ":" + pair.Key);
            }
            return binds;
        }

        // real path -> stand-in inside the rootfs
        public static readonly Dictionary<string, string> ProcFiles = new Dictionary<string, string>
        {
            { "/proc/loadavg", "proc/.loadavg" },
            { "/proc/stat", "proc/.stat" },
            { "/proc/uptime", "proc/.uptime" },
            { "/proc/version", "proc/.version" },
            { "/proc/vmstat", "proc/.vmstat" },
            { "/proc/sys/kernel/cap_last_cap", "proc/.sysctl_entry_cap_last_cap" },
            { "/proc/sys/fs/inotify/max_user_watches", "proc/.sysctl_inotify_max_user_watches" },
        };

        private static void WriteProcStandIns(string root, int cpus, IFileOps fs)
        {
            Write(root, "proc/.loadavg", "0.12 0.07 0.02 2/165 765\n", fs);
            Write(root, "proc/.uptime", "124.08 932.80\n", fs);
            Write(root, "proc/.version", "Linux version 6.1.0-metacodex (proot@metacodex) (clang) #1 SMP PREEMPT\n", fs);
            Write(root, "proc/.sysctl_entry_cap_last_cap", "40\n", fs);
            Write(root, "proc/.sysctl_inotify_max_user_watches", "65536\n", fs);
            var stat = new StringBuilder();
            stat.Append("cpu  1957 0 2877 93280 262 342 254 87 0 0\n");
            for (int i = 0; i < cpus; i++) stat.Append($"cpu{i} 31 0 226 12027 82 10 4 9 0 0\n");
            stat.Append("intr 63361 0 9 0 0 0 0 3 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            stat.Append("ctxt 38014093\nbtime 1694292441\nprocesses 26442\nprocs_running 1\nprocs_blocked 0\n");
            stat.Append("softirq 75663 0 5903 6 25375 10774 0 243 11685 0 21677\n");
            Write(root, "proc/.stat", stat.ToString(), fs);
            Write(root, "proc/.vmstat", VmStat, fs);
        }

        private static void AddGroups(string root, int[] groups, IFileOps fs)
        {
            string path = Path.Combine(root, "etc/group");
            string text = File.Exists(path) ? File.ReadAllText(path) : "root:x:0:\n";
            var known = new HashSet<string>();
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split(':');
                if (f.Length >= 3) known.Add(f[2]);
            }
            var sb = new StringBuilder(text);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            foreach (int g in groups)
            {
                string id = g.ToString();
                if (g <= 0 || known.Contains(id)) continue;
                sb.Append($"aid_{id}:x:{id}:root\n");
                known.Add(id);
            }
            Write(root, "etc/group", sb.ToString(), fs);
        }

        private static void Write(string root, string rel, string content, IFileOps fs)
        {
            string path = Path.Combine(root, rel);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) fs.MakeDir(dir, 0x1ED);
            fs.DeleteIfExists(path); // may be a dangling symlink (resolv.conf in some images)
            using (var s = fs.CreateFile(path))
            {
                var b = new UTF8Encoding(false).GetBytes(content);
                s.Write(b, 0, b.Length);
            }
            fs.SetMode(path, 0x1A4); // 0644
        }

        private const string Profile =
            "# MetaCodex: Debian userland inside the headset app (proot). Managed by the app.\n" +
            "export TERM=\"${TERM:-xterm-256color}\"\n" +
            "export COLORTERM=truecolor\n" +
            "export LANG=C.UTF-8\n" +
            "export TMPDIR=/tmp\n" +
            "export METACODEX=1\n" +
            "case \":$PATH:\" in *:/root/.local/bin:*) ;; *) export PATH=\"/root/.local/bin:$PATH\" ;; esac\n" +
            "if [ -n \"$PS1\" ]; then\n" +
            "  PS1='\\[\\e[38;5;114m\\]\\u@metacodex\\[\\e[0m\\]:\\[\\e[38;5;75m\\]\\w\\[\\e[0m\\]\\$ '\n" +
            "  if [ -z \"$METACODEX_MOTD_SHOWN\" ] && [ -f /etc/metacodex/motd ]; then cat /etc/metacodex/motd; export METACODEX_MOTD_SHOWN=1; fi\n" +
            "fi\n";

        private const string Motd =
            "\x1b[1mMetaCodex Linux\x1b[0m: Debian 13 (arm64) inside the headset app.\n" +
            "  set up:      MetaCodex installs base tools, Node and the native app tools by itself (~/.metacodex/first-use.log)\n" +
            "  native apps: mcx-app new|build|install|launch (skill quest-native-apps)\n" +
            "  your files:  /root, /work (private)   /mnt/quest (shared Android/data folder)\n\n";

        private const string VmStat =
            "nr_free_pages 1743136\nnr_zone_inactive_anon 179281\nnr_zone_active_anon 7183\nnr_zone_inactive_file 22858\n" +
            "nr_zone_active_file 51328\nnr_zone_unevictable 642\nnr_zone_write_pending 0\nnr_mlock 0\nnr_bounce 0\n" +
            "nr_zspages 0\nnr_free_cma 0\nnuma_hit 1259626\nnuma_miss 0\nnuma_foreign 0\nnuma_interleave 720\n" +
            "numa_local 1259626\nnuma_other 0\nnr_inactive_anon 179281\nnr_active_anon 7183\nnr_inactive_file 22858\n" +
            "nr_active_file 51328\nnr_unevictable 642\nnr_slab_reclaimable 8091\nnr_slab_unreclaimable 7804\n" +
            "nr_isolated_anon 0\nnr_isolated_file 0\nworkingset_nodes 0\nnr_anon_pages 7723\nnr_mapped 8905\n" +
            "nr_file_pages 253569\nnr_dirty 0\nnr_writeback 0\nnr_shmem 178741\nnr_kernel_stack 2720\n" +
            "nr_page_table_pages 343\npgpgin 198715\npgpgout 69228\npswpin 0\npswpout 0\npgalloc_normal 1350498\n" +
            "pgfree 3116432\npgactivate 46431\npgdeactivate 0\npgfault 1213932\npgmajfault 2262\n";
    }
}
