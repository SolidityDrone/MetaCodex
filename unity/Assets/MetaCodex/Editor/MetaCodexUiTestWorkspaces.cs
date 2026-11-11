using System;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `workspaces` (owner 2026-11-11, docs/workspaces.md): which folder each thread works in, from
    /// canned MQH rows. THREADS groups a project's threads by folder once it has a worktree; WORKSPACES shows each one
    /// (who works there, what waits, what can be merged or removed); NEW THREAD picks WORKTREE when the checkout is busy
    /// and NEW THREAD HERE reuses a workspace; THREAD says when it shares its folder, why a turn waits, and offers SEND NOW;
    /// the session context knows it; voice opens it.
    public static partial class MetaCodexUiTest
    {
        private static void WorkspacesSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            string mode0 = shell.Settings.workspaceMode;
            shell.Settings.workspaceMode = "local";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray { new JObject { ["id"] = "p1", ["name"] = "app", ["sources"] = new JArray { new JObject { ["path"] = "/root/app", ["hostId"] = "h1", ["type"] = "local_path" } } } };
            JObject T(string id, string title, string env, string path, string branch, bool wt, string st, long ago, string queued = "none") => new JObject
            {
                ["id"] = id, ["projectId"] = "p1", ["title"] = title, ["providerId"] = "codex", ["environmentId"] = env, ["environmentPath"] = path, ["environmentBranchName"] = branch,
                ["environmentIsWorktree"] = wt, ["status"] = st, ["runtime"] = new JObject { ["displayStatus"] = st }, ["updatedAt"] = now - ago, ["queuedWork"] = queued, ["hasPendingInteraction"] = false,
            };
            var threads = new JArray
            {
                T("a1", "Fix the build", "env-co", "/root/app", "main", false, "active", 20000),
                T("a2", "Write the docs", "env-co", "/root/app", "main", false, "idle", 60000, "waiting"),
                T("a3", "Menu rework", "env-wt", "/root/.mqh/worktrees/app/menu", "feat/menu", true, "active", 10000),
            };
            var envs = new JArray
            {
                new JObject { ["id"] = "env-co", ["projectId"] = "p1", ["path"] = "/root/app", ["isWorktree"] = false, ["branchName"] = "main", ["defaultBranch"] = "main", ["lifecycle"] = new JObject { ["phase"] = "active" } },
                new JObject { ["id"] = "env-wt", ["projectId"] = "p1", ["path"] = "/root/.mqh/worktrees/app/menu", ["isWorktree"] = true, ["branchName"] = "feat/menu", ["baseBranch"] = "main", ["lifecycle"] = new JObject { ["phase"] = "active" }, ["updatedAt"] = now - 10000 },
                new JObject { ["id"] = "env-old", ["projectId"] = "p1", ["path"] = "/root/.mqh/worktrees/app/old", ["isWorktree"] = true, ["branchName"] = "feat/old", ["baseBranch"] = "main", ["lifecycle"] = new JObject { ["phase"] = "active" }, ["updatedAt"] = now - 900000 },
            };
            shell.SetWorkspacesForTest(projects, threads, envs);

            // THREADS: the project's folders as rows, the threads under the one they work in
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, threads);
            var rows = shell.ThreadListForTest.Items;
            var tags = rows.Select(i => i.Tag as string).ToList();
            Check(tags.SequenceEqual(new[] { "p:p1", "w:p1|dir:/root/app", "t:a1", "t:a2", "w:p1|env:env-wt", "t:a3", "w:p1|env:env-old" })
                  && rows[1].Badge == "1 WAITING" && rows[1].Sub == "shared folder" && rows[3].Badge == "WAITING" && rows[4].Title == "feat/menu" && rows[4].Sub == "from main",
                "THREADS: app's checkout (shared: one works, one waits), the worktree feat/menu with its thread, a worktree left behind: " + string.Join(" ", tags));
            shell.PanelsById["threads"].ForceRefresh();
            RenderPanel(shell.PanelsById["threads"], "ui-threads-workspaces.png");

            // WORKSPACES: each folder, who works there, what can be merged or removed
            shell.OpenWorkspacesOn("p1|env:env-wt");
            var wp = shell.PanelsById["workspaces"];
            string d = shell.WorkspaceDetailForTest;
            Check(wp.IsOpen && d.Contains("feat/menu") && d.Contains("from") && d.Contains("main") && d.Contains("Menu rework") && shell.MergeEnabledForTest && !shell.RemoveEnabledForTest,
                "WORKSPACES on feat/menu: its folder, base and thread; MERGE INTO MAIN offered; REMOVE not while its thread works");
            shell.SelectWorkspaceForTest("p1|env:env-old");
            Check(shell.RemoveEnabledForTest && shell.WorkspaceDetailForTest.Contains("left from archived threads"), "the worktree left behind: REMOVE cleans it up");
            shell.SelectWorkspaceForTest("p1|dir:/root/app");
            Check(!shell.MergeEnabledForTest && shell.WorkspaceDetailForTest.Contains("waits for the folder") && shell.WorkspaceDetailForTest.Contains("Fix the build"),
                "the checkout: who works there and who waits for it; nothing to merge");
            wp.ForceRefresh();
            RenderPanel(wp, "ui-workspaces.png");
            Check(shell.Voice.FastMatch("open the worktrees") != null && (string)shell.Voice.FastMatch("open the worktrees").Args["panel"] == "workspaces", "voice: “open the worktrees” opens WORKSPACES");
            shell.ClosePanel("workspaces");

            // NEW THREAD: the checkout is busy -> WORKTREE for this thread (said why); the saved choice stays
            shell.SetThreadsForTest(projects, threads);
            shell.PickThreadRowForTest(0); shell.PickThreadRowForTest(0); // p1 chosen (folded and back)
            shell.OpenNewThread("codex");
            var body = shell.NewThreadBodyForTest("add a settings page");
            Check(shell.NewThreadForcedWorktreeForTest && shell.NewThreadSummaryForTest.Contains("is working in the checkout") && (string)body["environment"]?["workspace"]?["type"] == "managed-worktree"
                  && shell.Settings.workspaceMode == "local",
                "NEW THREAD while “Fix the build” works in the checkout: WORKTREE for this thread (its own folder and branch), the summary says why; the saved choice stays CHECKOUT");
            shell.ClosePanel("thread-new");
            // NEW THREAD HERE: that workspace again
            shell.OpenWorkspacesOn("p1|env:env-wt");
            wp.Elements.OfType<UiButton>().First(b => b.Text.StartsWith("NEW THREAD HERE")).Click();
            var here = shell.NewThreadBodyForTest("polish the menu");
            Check((string)here["environment"]?["type"] == "reuse" && (string)here["environment"]?["environmentId"] == "env-wt" && shell.NewThreadSummaryForTest.Contains("feat/menu"),
                "NEW THREAD HERE: the new thread works in feat/menu's worktree (MQH holds its turn while “Menu rework” works there)");
            shell.ClosePanel("thread-new");
            shell.ClosePanel("workspaces");

            // THREAD: a held turn says why and offers SEND NOW; the chip says the folder is shared with a working thread
            var held = (JObject)threads[1].DeepClone();
            shell.LoadThreadForTest(held, new JArray { new JObject { ["kind"] = "conversation", ["id"] = "u1", ["role"] = "user", ["text"] = "write the docs for the menu", ["sourceSeqEnd"] = 1, ["startedAt"] = now - 70000, ["createdAt"] = now - 70000 } }, null, null, projects, false);
            shell.SetWorkspacesForTest(projects, threads, envs); // the other threads too
            shell.SetThreadWaitForTest("q1", "“Fix the build” is working in this checkout: this turn starts when it is done (Send now shares the checkout)");
            string status = shell.ThreadStatusTextForTest;
            Check(shell.OpenThreadPulse.State == "held" && status.Contains("Not started: “Fix the build” is working in this checkout") && shell.SendNowForTest != null && shell.SendNowForTest.Visible,
                "THREAD: the held turn says why (“Fix the build” works in this checkout) with SEND NOW beside it");
            Check(shell.WorkspaceChipForTest.StartsWith("⚠ ▣ checkout"), "THREAD's workspace chip: ⚠ the checkout is shared with a working thread: " + shell.WorkspaceChipForTest);
            shell.SetPointedForTest("thread");
            string ctx = shell.SessionContextFor("what is this thread doing?");
            Check(ctx.Contains("workspace: in the project checkout (main), shared with “Fix the build” working there now") && ctx.Contains("not started"), "the session context: where it works, who shares it, why it waits");
            shell.PanelsById["thread"].ForceRefresh();
            RenderPanel(shell.PanelsById["thread"], "ui-thread-held.png");
            shell.SetThreadWaitForTest(null, null);
            Check(!shell.SendNowForTest.Visible, "no wait: SEND NOW goes");
            shell.Settings.workspaceMode = mode0;
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }
    }
}
