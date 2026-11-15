using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `threadwindows` (owner 2026-11-14, docs/thread-view.md "Thread windows and groups"): several
    /// THREAD windows at once, each with its own thread, composer and state; a thread of a project that has a window open
    /// joins that window's group; a group's grid (2x1, 2x2), its scale, the sidebar on the group's edge, a swap by drop,
    /// a pull-out (not pulled back in for that thread), a window's ✕, the group's ✕, the session context naming the
    /// windows, the voice command. Leaves one free THREAD window closed at the end. Not run on the owner's PC (heavy).
    public static partial class MetaCodexUiTest
    {
        private static void ThreadWindowsSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.ForgetSidebarMemoryForTest();
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray
            {
                new JObject { ["id"] = "pw1", ["name"] = "project1", ["sources"] = new JArray { new JObject { ["path"] = "/root/metacodex-projects/default/project1" } } },
                new JObject { ["id"] = "pw2", ["name"] = "project2", ["sources"] = new JArray { new JObject { ["path"] = "/root/metacodex-projects/default/project2" } } },
            };
            JObject T(string id, string pid, string title, long ago) => new JObject
            {
                ["id"] = id, ["projectId"] = pid, ["title"] = title, ["providerId"] = "codex", ["status"] = "idle",
                ["runtime"] = new JObject { ["displayStatus"] = "idle" }, ["updatedAt"] = now - ago, ["environmentPath"] = "/root/metacodex-projects/default/" + (pid == "pw1" ? "project1" : "project2"),
            };
            var threads = new JArray { T("tw_a", "pw1", "Login form", 1000), T("tw_b", "pw1", "Signup page", 2000), T("tw_c", "pw2", "Physics demo", 3000), T("tw_d", "pw1", "Password reset", 4000) };
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, threads);

            // 1. one thread: one free window; a second thread of the same project: a new window, grouped with the first (2x1)
            shell.PickThreadRowForTest("t:tw_a");
            var wins = shell.ThreadWindowsForTest;
            Check(wins.Count == 1 && wins.TryGetValue("thread", out var t1) && t1 == "tw_a" && shell.GroupCountForTest == 0, "thread windows: the first thread opens in THREAD, free");
            shell.PickThreadRowForTest("t:tw_b");
            wins = shell.ThreadWindowsForTest;
            var groups = shell.GroupsForTest;
            Check(wins.Count == 2 && wins["thread"] == "tw_a" && wins.TryGetValue("thread-2", out var t2) && t2 == "tw_b",
                "thread windows: a second thread opens in a new window (thread-2), the first one stays: " + string.Join(", ", wins.Select(kv => kv.Key + "=" + kv.Value)));
            Check(groups.Count == 1 && groups.Values.First().SequenceEqual(new[] { "thread", "thread-2" }) && shell.FocusedThreadWindowId == "thread-2",
                "auto-group by project: the second thread of project1 joins the first one's window as a group (2x1), it is focused");
            var gp = shell.GroupPanelForTest("tgroup-1");
            float w2 = 2 * MetaCodexShell.ThreadW + MetaCodexShell.GroupGap + 2 * MetaCodexShell.GroupPad;
            Check(gp != null && gp.IsOpen && Mathf.Abs(gp.Width - w2) < 1e-4f && shell.GroupOfWindowForTest("thread") == "tgroup-1",
                $"the group's window: 2x1 grid, {gp?.Width:F3} m wide in its units (want {w2:F3}), its windows are its children");

            // 2. another project's thread: its own free window
            shell.PickThreadRowForTest("t:tw_c");
            wins = shell.ThreadWindowsForTest;
            Check(wins.Count == 3 && wins["thread-3"] == "tw_c" && shell.GroupOfWindowForTest("thread-3") == null && shell.GroupCountForTest == 1,
                "auto-group by project: project2's thread opens as its own free window (thread-3)");

            // 3. a thread already open: its window is focused, nothing new opens
            shell.PickThreadRowForTest("t:tw_a");
            Check(shell.ThreadWindowCount == 3 && shell.FocusedThreadWindowId == "thread", "a thread already open: its window is focused, no new window");

            // 4. each window keeps its own composer: typing in one leaves the others empty
            shell.FocusThreadWindowForTest("thread-3");
            var c3 = shell.ComposerForTest;
            c3.Set("for physics");
            shell.FocusThreadWindowForTest("thread");
            var c1 = shell.ComposerForTest;
            Check(c1 != c3 && string.IsNullOrEmpty(c1.Text) && c3.Text == "for physics", "each window has its own composer: the text stays in the window it was typed in");
            c3.Set("");

            // 5. the third thread of project1 joins the group: 2x2, the default scale for 3-4 windows
            shell.PickThreadRowForTest("t:tw_d");
            groups = shell.GroupsForTest;
            gp = shell.GroupPanelForTest("tgroup-1");
            Check(groups["tgroup-1"].Count == 3 && gp != null && Mathf.Abs(gp.Height - (UiTheme.Current.HeaderHeight + 2 * MetaCodexShell.GroupPad + 2 * MetaCodexShell.ThreadH + MetaCodexShell.GroupGap + MetaCodexShell.GroupFooter)) < 1e-4f
                  && Mathf.Abs(shell.GroupScaleForTest("tgroup-1") - MetaCodexShell.DefaultGroupScale(3)) < 1e-4f,
                "a third window of project1 joins the group: the grid is 2x2 and the group a little smaller: " + string.Join(" ", groups["tgroup-1"]));
            shell.TickThreadGroupsForTest(0.05f, 40); // the windows glide into their cells
            var cellOk = groups["tgroup-1"].Select((id, i) => (shell.ThreadWindowPanelForTest(id), i)).All(x => x.Item1 != null && x.Item1.transform.parent == gp.transform
                && Vector3.Distance(x.Item1.transform.localPosition, gp.PanelToLocal(new Vector2(MetaCodexShell.GroupPad + (x.i % 2) * (MetaCodexShell.ThreadW + MetaCodexShell.GroupGap) + MetaCodexShell.ThreadW * 0.5f,
                    UiTheme.Current.HeaderHeight + MetaCodexShell.GroupPad + (x.i / 2) * (MetaCodexShell.ThreadH + MetaCodexShell.GroupGap) + MetaCodexShell.ThreadH * 0.5f), -0.01f)) < 0.002f
                && (x.Item1.BaseScale - Vector3.one).magnitude < 0.01f);
            Check(cellOk, "the group's windows ease into their cells at the group's scale");

            // 6. resize: the group's scale changes, its windows scale with it (clamped)
            shell.SetGroupScaleForTest("tgroup-1", 0.9f);
            var any = shell.ThreadWindowPanelForTest("thread");
            float ui = shell.ThreadWindowPanelForTest("thread-3").BaseScale.x;
            Check(Mathf.Abs(gp.BaseScale.x - ui * 0.9f) < 1e-3f && Mathf.Abs(any.RestLossyScale.x - gp.RestLossyScale.x) < 1e-3f, "RESIZE: the group's scale 0.9, its windows follow it");
            shell.SetGroupScaleForTest("tgroup-1", 5f);
            Check(Mathf.Abs(shell.GroupScaleForTest("tgroup-1") - MetaCodexShell.GroupScaleMax) < 1e-4f, "RESIZE is clamped (at most " + MetaCodexShell.GroupScaleMax + ")");
            shell.SetGroupScaleForTest("tgroup-1", 0.6f);

            // 7. the sidebar of a grouped window opens on the group's edge
            shell.FocusThreadWindowForTest("thread-2");
            bool sbOpen = shell.SetSidebar(true, MetaCodexShell.TabStatus);
            shell.UpdateSidebarLinkForTest();
            Check(sbOpen && shell.SidebarAnchorForTest == gp, "a grouped window's sidebar sits beside the group, not inside the grid");
            shell.SetSidebar(false);

            // 8. a drop on a window of the same group swaps the two
            var pa = shell.ThreadWindowPanelForTest("thread");
            var pd = shell.ThreadWindowPanelForTest(groups["tgroup-1"][2]);
            var before = shell.GroupsForTest["tgroup-1"].ToList();
            pd.transform.position = pa.transform.position + (head.position - pa.transform.position).normalized * 0.05f;
            string target = shell.DropTargetForTest(pd.Id);
            shell.DropThreadWindowForTest(pd.Id);
            var after = shell.GroupsForTest["tgroup-1"];
            Check(target == "thread" && after.Count == 3 && after.IndexOf(pd.Id) == before.IndexOf("thread") && after.IndexOf("thread") == before.IndexOf(pd.Id),
                $"a window let go on a window of its group: the two swap places (target {target}; {string.Join(" ", before)} -> {string.Join(" ", after)})");
            shell.TickThreadGroupsForTest(0.05f, 40);

            // 9. pull out: it leaves at its full size; opening its thread again does not pull it back in
            shell.PullOutForTest("thread-2");
            shell.TickThreadGroupsForTest(0.05f, 40);
            Check(shell.GroupOfWindowForTest("thread-2") == null && shell.GroupsForTest["tgroup-1"].Count == 2 && Mathf.Abs(shell.ThreadWindowPanelForTest("thread-2").BaseScale.x - ui) < 0.01f,
                "pull out: the window leaves the group, back to its full size; the group keeps 2 (2x1)");
            shell.PickThreadRowForTest("t:tw_b");
            Check(shell.GroupOfWindowForTest("thread-2") == null, "a thread pulled out by hand is not grouped again while its window is open");

            // 10. the session context names the windows; "this thread" is the focused one
            var snap = shell.BuildSessionSnapshot(SessionQuestion.Parse("what is this thread doing"));
            var winsData = snap?["data"]?["windows"] as JArray;
            Check(winsData != null && winsData.Count == shell.ThreadWindowCount && winsData.Any(x => (bool?)x["focused"] == true && (string)x["id"] == shell.FocusedThreadWindowId)
                  && winsData.Any(x => (int?)x["group"] == 1) && shell.ThreadWindowsForTest.ContainsValue((string)snap["data"]["subjectThread"] ?? "-"),
                "the session context lists the open thread windows by id (focused, group); the subject is the thread of a window (looked at, else the focused one)");

            // 11. the voice command is registered, clean, and enabled while a window is open
            var cmd = shell.Voice.Find("thread_windows");
            var lint = cmd != null ? VoiceCommands.Lint(cmd) : new List<string> { "missing" };
            Check(cmd != null && cmd.Enabled && lint.Count == 0, "voice: thread_windows registered and enabled, lint clean" + (lint.Count > 0 ? ": " + string.Join("; ", lint) : ""));

            // 12. a window's ✕ closes it alone (the group keeps the others or is set free); the group's ✕ closes its windows
            shell.ThreadWindowPanelForTest("thread-3").Close();
            Check(!shell.ThreadWindowsForTest.ContainsKey("thread-3") && shell.ThreadWindowPanelForTest("thread-3") == null, "a window's ✕: that window goes, the thread stays in MQH");
            var gid = shell.GroupsForTest.Keys.First();
            var members = shell.GroupsForTest[gid].ToList();
            shell.GroupPanelForTest(gid).Close();
            Check(shell.GroupCountForTest == 0 && members.All(m => !shell.ThreadWindowsForTest.ContainsKey(m)), "the group's ✕ closes its windows: " + string.Join(" ", members));

            // tidy: every thread window closed, THREAD focused again
            foreach (var id in shell.ThreadWindowsForTest.Keys.ToList()) shell.ThreadWindowPanelForTest(id)?.Close();
            shell.FocusThreadWindowForTest("thread");
            Check(shell.ThreadWindowCount == 0 && shell.GroupCountForTest == 0, "tidy: no thread window or group left open");
        }
    }
}
