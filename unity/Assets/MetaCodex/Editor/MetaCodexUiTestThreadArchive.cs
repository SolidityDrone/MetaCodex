using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using Newtonsoft.Json.Linq;

namespace MetaCodex.EditorTools
{
    /// Self-test section `archive` (owner 2026-11-14, docs/thread-view.md "Held turns and archive"): a thread window shows
    /// ARCHIVE… / DELETE… only while it shows a thread; an archived thread's window closes and the thread leaves the list
    /// (the other window stays); THREADS > ARCHIVED lists archived rows with RESTORE; the voice commands are clean and
    /// fast-match. No MQH call is answered here (canned rows only).
    public static partial class MetaCodexUiTest
    {
        private static void ThreadArchiveSection(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray
            {
                new JObject { ["id"] = "pa1", ["name"] = "alpha", ["sources"] = new JArray { new JObject { ["path"] = "/root/metacodex-projects/default/alpha" } } },
                new JObject { ["id"] = "pa2", ["name"] = "beta", ["sources"] = new JArray { new JObject { ["path"] = "/root/metacodex-projects/default/beta" } } },
            };
            JObject T(string id, string pid, string title) => new JObject
            {
                ["id"] = id, ["projectId"] = pid, ["title"] = title, ["providerId"] = "codex", ["status"] = "idle",
                ["runtime"] = new JObject { ["displayStatus"] = "idle" }, ["updatedAt"] = now - 1000, ["environmentPath"] = "/root/metacodex-projects/default/" + (pid == "pa1" ? "alpha" : "beta"),
            };
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, new JArray { T("ta_1", "pa1", "Login form"), T("ta_2", "pa2", "Physics demo") });
            shell.PickThreadRowForTest("t:ta_1");
            shell.PickThreadRowForTest("t:ta_2");
            var wins = shell.ThreadWindowsForTest;
            string w2 = wins.FirstOrDefault(kv => kv.Value == "ta_2").Key;
            Check(wins.Count == 2 && w2 != null && shell.ThreadActionsVisibleForTest("thread") && shell.ThreadActionsVisibleForTest(w2),
                "archive: each window that shows a thread has ARCHIVE… and DELETE…");

            // archived (here, or by another client): that window closes and lets the thread go; the other window stays
            shell.ThreadsGoneForTest("ta_2");
            wins = shell.ThreadWindowsForTest;
            Check(wins.Count == 1 && wins.TryGetValue("thread", out var t1) && t1 == "ta_1" && !shell.ThreadListForTest.Items.Any(i => i.Tag as string == "t:ta_2"),
                "archive: the archived thread's window closed, it left THREADS, the other window kept its thread: " + string.Join(", ", wins.Select(kv => kv.Key + "=" + kv.Value)));
            shell.ThreadsGoneForTest("ta_1");
            Check(shell.ThreadWindowCount == 0 && !shell.ThreadActionsVisibleForTest("thread"), "archive: THREAD closed and empty: no ARCHIVE… / DELETE… on it");

            // THREADS > ARCHIVED: the archived rows with RESTORE; ◂ ACTIVE goes back
            shell.SetArchivedThreadsForTest(new JArray { new JObject { ["id"] = "ta_2", ["projectId"] = "pa2", ["title"] = "Physics demo", ["providerId"] = "codex", ["archivedAt"] = now - 60000 } });
            var rows = shell.ThreadListForTest.Items;
            Check(shell.ArchivedShownForTest && rows.Count == 1 && rows[0].Tag as string == "a:ta_2" && rows[0].Badge.Contains("RESTORE") && rows[0].Sub.Contains("beta"),
                "THREADS > ARCHIVED: the archived thread with its project and RESTORE");
            shell.ToggleArchivedThreadsForTest();
            Check(!shell.ArchivedShownForTest && !shell.ThreadListForTest.Items.Any(i => (i.Tag as string ?? "").StartsWith("a:")), "◂ ACTIVE: the normal list again");

            // voice: registered, lint clean, the fast path knows them
            var lint = new[] { "archive_thread", "restore_thread" }.SelectMany(n => shell.Voice.Find(n) == null ? new List<string> { n + ": missing" } : VoiceCommands.Lint(shell.Voice.Find(n)).Select(x => n + ": " + x)).ToList();
            Check(lint.Count == 0, "voice: archive_thread and restore_thread registered, lint clean" + (lint.Count > 0 ? ": " + string.Join("; ", lint) : ""));
            var m1 = shell.Voice.FastMatch("archive this chat");
            var m2 = shell.Voice.FastMatch("archive the thread about the login form");
            var m3 = shell.Voice.FastMatch("bring back the chat about physics");
            Check(m1?.Command.Name == "archive_thread" && m2?.Command.Name == "archive_thread" && ((string)m2.Args["query"] ?? "").Contains("login form") && m3?.Command.Name == "restore_thread",
                $"voice fast path: “archive this chat”, “archive the thread about …” ({m2?.Args?["query"]}), “bring back the chat about …” -> {m1?.Command.Name}, {m2?.Command.Name}, {m3?.Command.Name}");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }
    }
}
