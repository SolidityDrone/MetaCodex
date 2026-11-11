using System;
using System.IO;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Hands;
using MetaCodex.Shell;
using MetaCodex.Terminal;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `sidebar` (owner 2026-11-11, docs/thread-sidebar.md): THREAD's sidebar on real folders made
    /// with WSL's git under %USERPROFILE%\mcxcheck-sidebar\uitest: a repo with commits, an edited C# file (changed,
    /// removed and added lines, a 300-character line), an edited markdown file, a deleted file, a new untracked Python
    /// file, a 3 MB file changed in one line and a binary; a folder that is no repo (INIT GIT). Pictures under
    /// Bench/selftest/ui-sidebar-*.png.
    public static partial class MetaCodexUiTest
    {
        private const string SidebarMake = @"set -e
B=""$1""
rm -rf ""$B""
mkdir -p ""$B/repo/src"" ""$B/repo/docs"" ""$B/plain/notes""
cd ""$B/repo""
git init -q -b main
git config user.email selftest@metacodex.local
git config user.name selftest
cat > src/Calc.cs <<'CS'
using System;
using System.Collections.Generic;

namespace Demo
{
    /// A small calculator: sums, means and a running total.
    public static class Calc
    {
        private const int Limit = 1000; // the biggest input
        public static int Add(int a, int b) => a + b;
        public static int Sub(int a, int b) => a - b;
        public static int Mul(int a, int b) => a * b;

        public static double Mean(IList<int> xs)
        {
            if (xs == null || xs.Count == 0) return 0;
            double sum = 0;
            foreach (var x in xs) sum += x;
            return sum / xs.Count;
        }

        public static int Clamp(int v)
        {
            if (v > Limit) return Limit;
            if (v < -Limit) return -Limit;
            return v;
        }

        public static string Describe(int v)
        {
            switch (v)
            {
                case 0: return ""zero"";
                case 1: return ""one"";
                default: return $""many ({v})"";
            }
        }

        /* a block comment
           over two lines */
        public static IEnumerable<int> Range(int n)
        {
            for (int i = 0; i < n; i++) yield return i;
        }

        public static int Total;
        public static void Count(int x) { Total += x; }
        public static void Reset() { Total = 0; }
        public static int Twice(int x) => x * 2;
        public static int Thrice(int x) => x * 3;
        public static int Square(int x) => x * x;
        public static int Cube(int x) => x * x * x;
        public static bool IsEven(int x) => x % 2 == 0;
        public static bool IsOdd(int x) => x % 2 != 0;
        public static int Abs(int x) => x < 0 ? -x : x;
        public static int Sign(int x) => x > 0 ? 1 : x < 0 ? -1 : 0;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
    }
}
CS
printf '# Demo\n\nA small calculator.\n\n## Usage\n\n- `Calc.Add(1, 2)` gives **3**\n- `Calc.Mean` averages a list\n' > README.md
printf 'old notes\nto be removed\n' > docs/old.txt
printf 'line one\nline two\n' > docs/notes.txt
awk 'BEGIN { print ""[""; for (i = 1; i <= 60000; i++) printf ""  {\""id\"": %d, \""name\"": \""item %d\"", \""tags\"": [\""a\"", \""b\""]},\n"", i, i; print ""]"" }' > big.json
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01' > logo.png
git add -A
git commit -qm 'Calc, docs and data'
printf '\n## License\n\nMIT\n' >> README.md
git commit -qam 'Docs: license'
sed -i 's/Thrice(int x) => x \* 3;/Thrice(int x) => x + x + x;/' src/Calc.cs
git commit -qam 'Thrice without a product'
# the working changes the thread made
sed -i 's/if (xs == null || xs.Count == 0) return 0;/if (xs == null || xs.Count == 0) return double.NaN; \/\/ no data: not a number/' src/Calc.cs
sed -i '/case 1: return ""one"";/d' src/Calc.cs
sed -i 's/public static int Total;/public static long Total; \/\/ wider/' src/Calc.cs
L=$(printf 'x%.0s' $(seq 1 280))
sed -i ""s/^    }\$/        public static string Banner() => \""LONG-LINE-START $L LONG-LINE-END\"";\n    }/"" src/Calc.cs
sed -i 's/- `Calc.Mean` averages a list/- `Calc.Mean` averages a list (NaN when empty)/' README.md
git rm -q docs/old.txt
sed -i 's/""id"": 30000, ""name"": ""item 30000""/""id"": 30000, ""name"": ""item thirty thousand""/' big.json
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x02' > logo.png
cat > src/tool.py <<'PY'
#!/usr/bin/env python3
""""""A new helper the agent wrote.""""""
import sys


def main(args):
    # sum the numbers given
    total = sum(int(a) for a in args)
    print(f""total: {total}"")
    return 0


if __name__ == ""__main__"":
    sys.exit(main(sys.argv[1:]))
PY
cd ""$B/plain""
printf 'a folder that is no git repository\n' > readme.txt
printf 'echo hi\n' > notes/run.sh
";

        /// Runs the main-thread queue until the sidebar's git and file reads came back (they are not MQH requests, so
        /// PumpForTest alone stops before WSL answers).
        private static void SbPump(MetaCodexShell shell, float max)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            do { shell.PumpForTest(0.4f); System.Threading.Thread.Sleep(30); } while (shell.SidebarBusyForTest && sw.Elapsed.TotalSeconds < max);
            shell.PumpForTest(0.3f);
        }

        private static void SbPic(MetaCodexShell shell, UiPanel p, string file)
        {
            shell.Toasts.Tick(float.MaxValue); // no toast over the picture
            if (shell.PanelsById.TryGetValue("thread", out var th) && th.IsOpen) th.ForceRefresh(); // its handle beside the sidebar, drawn as it is now
            RenderPanel(p, file);
        }

        private static void SidebarSection(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            bool bend0 = shell.Settings.bendPanels;
            shell.Settings.bendPanels = false;
            shell.ForgetSidebarMemoryForTest();
            string userWin = Environment.GetEnvironmentVariable("USERPROFILE") ?? @"C:\Users\pnncl";
            string dirWin = Path.Combine(userWin, "mcxcheck-sidebar");
            Directory.CreateDirectory(dirWin);
            File.WriteAllText(Path.Combine(dirWin, "uitest-make.sh"), SidebarMake.Replace("\r", ""));
            string baseLx = GitService.ToLinuxPath(Path.Combine(dirWin, "uitest"));
            var git = new GitService(null);
            var made = git.Shell("bash " + GitService.Quote(GitService.ToLinuxPath(Path.Combine(dirWin, "uitest-make.sh"))) + " " + GitService.Quote(baseLx), 120000).Result;
            string repo = baseLx + "/repo", plain = baseLx + "/plain";
            Check(made.code == 0 && Directory.Exists(Path.Combine(dirWin, "uitest", "repo", ".git")), $"sidebar: test folders made with git (exit {made.code}){(made.code != 0 ? ": " + made.output : "")}");
            if (made.code != 0) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray { new JObject { ["id"] = "prj_sb", ["name"] = "demo", ["sources"] = new JArray { new JObject { ["path"] = repo } } } };
            JObject ThreadRow(string id, string title, string path) => new JObject
            {
                ["id"] = id, ["projectId"] = "prj_sb", ["providerId"] = "opencode", ["title"] = title, ["status"] = "active",
                ["runtime"] = new JObject { ["displayStatus"] = "active" }, ["environmentPath"] = path, ["environmentBranchName"] = "main", ["environmentIsWorktree"] = false,
                ["environmentId"] = "env_" + id, ["updatedAt"] = now,
            };
            JObject Edit(int seq, string path, string kind, int a, int r, string diff = null) => Row("work", seq, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = path, ["kind"] = kind, ["movePath"] = null, ["diff"] = diff, ["diffStats"] = new JObject { ["added"] = a, ["removed"] = r } } });
            var rowsA = new JArray
            {
                Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Make Mean return NaN for no data and add a tool" }),
                Edit(3, repo + "/src/Calc.cs", "update", 3, 3),
                Edit(4, repo + "/src/tool.py", "add", 13, 0),
                Row("work", 5, new JObject { ["workKind"] = "command", ["status"] = "completed", ["command"] = "git rm docs/old.txt", ["exitCode"] = 0 }),
                Row("conversation", 6, new JObject { ["role"] = "assistant", ["text"] = "Done: Mean returns NaN when there is no data; tool.py sums its arguments." }),
            };
            var usage = new JObject { ["usedTokens"] = 84000, ["modelContextWindow"] = 200000, ["estimated"] = false };
            var exec = new JObject { ["model"] = "kimi-k2", ["reasoningLevel"] = "high", ["permissionMode"] = "accept-edits" };
            shell.LoadThreadForTest(ThreadRow("thr_sbA", "Mean returns NaN", repo), rowsA, usage, exec, projects, false);
            var th = shell.PanelsById["thread"];
            var sb = shell.PanelsById[MetaCodexShell.SidebarId];
            th.transform.SetPositionAndRotation(head.position + new Vector3(-0.32f, -0.05f, 0.85f), Quaternion.identity);
            Check(th.IsOpen && !sb.IsOpen && shell.SidebarHandleForTest != null && shell.SidebarHandleForTest.Visible && shell.SidebarHandleForTest.Rect.xMin > th.Width - 0.05f,
                "THREAD: the expand handle sits on its right edge; a new thread starts with its sidebar folded");
            // the handle opens it (a tap with a ray)
            var hr = shell.SidebarHandleForTest.Rect;
            Click(shell.Router, th, hr.center);
            shell.TickLayoutForTest(0.05f, 2);
            SbPump(shell, 8f);
            var rel = th.transform.InverseTransformPoint(sb.transform.position);
            float topTh = th.transform.TransformPoint(th.PanelToLocal(Vector2.zero)).y, topSb = sb.transform.TransformPoint(sb.PanelToLocal(Vector2.zero)).y;
            Check(sb.IsOpen && shell.SidebarTab == MetaCodexShell.TabChanges && rel.x > th.Width * 0.5f && rel.x < th.Width * 0.5f + sb.Width + 0.05f && Mathf.Abs(topTh - topSb) < 0.003f,
                $"a tap on the handle opens the sidebar at the thread's right edge, tops level ({rel.x * 100f:F0} cm right of the thread's centre, tops {Mathf.Abs(topTh - topSb) * 1000f:F1} mm apart)");
            Check(shell.SidebarRootForTest == repo && shell.SidebarSnapshotForTest != null && shell.SidebarSnapshotForTest.Repo && shell.SidebarSnapshotForTest.Branch == "main",
                $"sidebar: rooted at the thread's project folder ({shell.SidebarRootForTest}), a repo on main");

            // CHANGES: every file, the thread's first
            var files = shell.SidebarFilesForTest;
            string list = string.Join(", ", files.Select(f => $"{f.Letter} {f.Path} +{f.Added}/-{f.Removed}{(f.ByThread ? "" : "*")}{(f.Binary ? " bin" : "")}"));
            var calc = files.FirstOrDefault(f => f.Path == "src/Calc.cs");
            var tool = files.FirstOrDefault(f => f.Path == "src/tool.py");
            Check(calc != null && calc.ByThread && calc.Letter == "M" && calc.Added == 3 && calc.Removed == 3 && tool != null && tool.Letter == "U" && tool.Added == 14 &&
                  files.Any(f => f.Path == "docs/old.txt" && f.Letter == "D") && files.Any(f => f.Path == "README.md" && f.Letter == "M") && files.Any(f => f.Path == "big.json") && files.Any(f => f.Path == "logo.png" && f.Binary),
                "CHANGES: tracked + untracked against HEAD, the deletion and the binary included: " + list);
            Check(files.TakeWhile(f => f.ByThread).Count() == 2 && files.Skip(2).All(f => !f.ByThread), "CHANGES: the thread's own files first, then the folder's other changes");
            Check(shell.SidebarSummaryForTest.StartsWith("2 files · +17 −3 · against HEAD") && shell.SidebarSummaryForTest.Contains("4 other changes"), "CHANGES summary: " + shell.SidebarSummaryForTest);
            SbPic(shell, sb, "ui-sidebar-changes.png");

            // a diff, VS Code-like
            shell.PickChangedFileForTest("src/Calc.cs");
            SbPump(shell, 5f);
            var dv = shell.SidebarDiffForTest;
            var doc = dv.Doc;
            Check(doc != null && doc.IsDiff && doc.Diff.Added == 3 && doc.Diff.Removed == 3 && doc.Diff.Folds >= 2 && doc.Lang.Name == "csharp",
                $"the diff: +{doc?.Diff?.Added} −{doc?.Diff?.Removed}, {doc?.Diff?.Folds} unchanged runs folded, C# colours ({doc?.RowCount} rows)");
            var rowsText = dv.VisibleTextForTest();
            Check(rowsText.Any(l => l.Contains("double.NaN")) && rowsText.Any(l => l.StartsWith("⋯")) && rowsText.Any(l => l.Contains(" -") && l.Contains("return 0;")),
                "the diff rows: old / new numbers, − and + lines, a fold: " + string.Join(" ¦ ", rowsText.Take(8)));
            Check(shell.SidebarDiffLabelForTest.Contains("Calc.cs") && shell.SidebarDiffLabelForTest.Contains("+3 −3") && shell.SidebarDiffLabelForTest.Contains("modified"), "the diff's header: " + shell.SidebarDiffLabelForTest);
            Check(dv.TextSize >= 0.0155f && dv.Advance > 0.006f, $"code text: {dv.TextSize * 1000f:F1} mm line size (an em of {UiTheme.Current.FontSizeFor(dv.TextSize) * 100f:F1} mm, ~{Mathf.Atan(UiTheme.Current.FontSizeFor(dv.TextSize) * 0.1f * shell.Settings.uiScale / 0.8f) * Mathf.Rad2Deg:F2}° at 0.8 m and UI scale {shell.Settings.uiScale}), {dv.Columns} columns");
            SbPic(shell, sb, "ui-sidebar-diff.png");
            // a long line: scroll sideways (drag) or wrap
            int longRow = Enumerable.Range(0, doc.RowCount).FirstOrDefault(i => doc.Diff.Lines[i].Text.Contains("LONG-LINE-START"));
            dv.ScrollToRow(Mathf.Max(0, longRow - 3));
            var d0 = dv.Rect.center;
            Click(shell.Router, sb, d0, d0 + new Vector2(-0.12f, 0f));
            Check(dv.HScroll > 5, $"a sideways drag scrolls the long line ({dv.HScroll} columns)");
            SbPic(shell, sb, "ui-sidebar-diff-scrolled.png");
            int vis0 = dv.VisualRowCount;
            shell.SidebarKey("home");
            dv.SetWrap(true);
            dv.ScrollToRow(Mathf.Max(0, longRow - 3));
            var vt = dv.VisibleTextForTest();
            int at = vt.FindIndex(l => l.Contains("LONG-LINE-START"));
            string whole = at < 0 ? "" : vt[at] + string.Concat(vt.Skip(at + 1).TakeWhile(l => l.StartsWith("↪")).Select(l => l.Substring(1)));
            Check(dv.VisualRowCount >= vis0 + 3 && dv.HScroll == 0 && whole.Contains("LONG-LINE-END"), $"WRAP: the long line breaks over rows ({vis0} -> {dv.VisualRowCount} rows), all of it on screen");
            SbPic(shell, sb, "ui-sidebar-diff-wrap.png");
            dv.SetWrap(false);
            dv.ScrollToRow(0);
            // a tap on a fold opens it
            int folds0 = doc.Diff.Folds, fold = dv.FirstFoldRow();
            float foldY = dv.Rect.y + (fold - Mathf.FloorToInt(dv.ScrollRows)) * dv.RowHeight + dv.RowHeight * 0.5f;
            Click(shell.Router, sb, new Vector2(dv.Rect.center.x, foldY));
            Check(fold >= 0 && doc.Diff.Folds == folds0 - 1, $"a tap on “⋯ N unchanged lines” opens them ({folds0} -> {doc.Diff.Folds} folds)");
            // drag to scroll, keys move the cursor
            float s0 = dv.ScrollRows;
            Click(shell.Router, sb, dv.Rect.center, dv.Rect.center + new Vector2(0f, -0.12f));
            Check(dv.ScrollRows > s0 + 3, $"a drag scrolls the diff ({s0:F0} -> {dv.ScrollRows:F0} rows)");
            shell.SidebarFocusForTest(true);
            int c0 = dv.Cursor;
            bool k1 = shell.SidebarPhysicalKeyForTest(TermKey.Down), k2 = shell.SidebarPhysicalKeyForTest(TermKey.Down);
            Check(k1 && k2 && dv.Cursor >= 0 && dv.Cursor != c0, $"keyboard: ↓ moves the line cursor in the diff (row {dv.Cursor})");
            shell.SidebarPhysicalKeyForTest(TermKey.Escape);
            Check(shell.SidebarOpenDiffForTest == null, "keyboard: Escape goes back to the list");
            shell.SidebarFocusForTest(false);
            Check(!shell.SidebarPhysicalKeyForTest(TermKey.Down), "keys go to the terminal again once another panel was touched");

            // big and binary files
            shell.PickChangedFileForTest("big.json");
            SbPump(shell, 8f);
            var bd = dv.Doc;
            Check(bd != null && bd.IsDiff && bd.Diff.Added == 1 && bd.Diff.Removed == 1 && bd.RowCount < 20 && (bd.Note ?? "").Contains("big file"), $"a 3 MB file: only its changed part ({bd?.RowCount} rows, note “{bd?.Note}”)");
            SbPic(shell, sb, "ui-sidebar-diff-big.png");
            shell.PickChangedFileForTest("logo.png");
            SbPump(shell, 2f);
            Check(dv.Doc != null && dv.Doc.RowCount == 0 && dv.Doc.Message.StartsWith("Binary"), "a binary: named, no text diff: " + dv.Doc?.Message);
            shell.PickChangedFileForTest("src/tool.py");
            SbPump(shell, 4f);
            Check(dv.Doc != null && dv.Doc.Diff != null && dv.Doc.Diff.NewFile && dv.Doc.Diff.Added == 14 && dv.Doc.Lang.Name == "python", $"an untracked file: all its lines added (+{dv.Doc?.Diff?.Added}, Python colours)");
            SbPic(shell, sb, "ui-sidebar-diff-new.png");

            // live: the agent edits Calc.cs again while its diff is open
            shell.PickChangedFileForTest("src/Calc.cs");
            SbPump(shell, 4f);
            git.Shell("cd " + GitService.Quote(repo) + " && sed -i 's/public static int Square(int x) => x [*] x;/public static int Square(int x) => checked(x * x);/' src/Calc.cs", 20000).Wait();
            shell.AppendTimelineForTest(new JArray { Edit(8, repo + "/src/Calc.cs", "update", 1, 1) });
            shell.SidebarRefreshNowForTest();
            SbPump(shell, 6f);
            var calc2 = shell.SidebarFilesForTest.FirstOrDefault(f => f.Path == "src/Calc.cs");
            Check(calc2 != null && calc2.Added == 4 && calc2.Removed == 4 && dv.Doc != null && dv.Doc.Diff != null && dv.Doc.Diff.Added == 4 && shell.SidebarOpenDiffForTest == "src/Calc.cs",
                $"live: a new edit refreshes the list and the open diff (+{calc2?.Added} −{calc2?.Removed}, diff +{dv.Doc?.Diff?.Added})");

            // FILES: the project folder, a breadcrumb, a file in the code view
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabFiles);
            SbPump(shell, 1f);
            var tree = shell.SidebarTreeForTest;
            Check(shell.SidebarDirForTest == repo && tree.Items.Any(r => r.Name == "src/") && tree.Items.Any(r => r.Name == "README.md" && r.Badge == "M") && !tree.Items.Any(r => r.Name == ".git/") && shell.SidebarCrumbsForTest.Parts.SequenceEqual(new[] { "repo" }),
                "FILES: the project folder (not /), changed files marked, .git hidden, breadcrumb “repo”: " + string.Join(", ", tree.Items.Select(r => r.Name + (r.Badge.Length > 0 ? " " + r.Badge : ""))));
            tree.Pick(tree.Items.ToList().FindIndex(r => r.Name == "src/"));
            Check(shell.SidebarDirForTest == repo + "/src" && shell.SidebarCrumbsForTest.Parts.SequenceEqual(new[] { "repo", "src" }) && tree.Items[0].Name == "..", "FILES: into src/, breadcrumb repo › src, “..” on top");
            SbPic(shell, sb, "ui-sidebar-files.png");
            shell.SidebarCrumbsForTest.PickForTest(0);
            Check(shell.SidebarDirForTest == repo, "FILES: the breadcrumb's first part goes back to the project folder");
            shell.SidebarKey("back");
            Check(shell.SidebarDirForTest == repo, "FILES: never above the project folder");
            shell.SidebarOpenFileNowForTest(repo + "/src/Calc.cs");
            SbPump(shell, 3f);
            var cv = shell.SidebarCodeForTest;
            Check(cv.Doc != null && !cv.Doc.IsDiff && cv.Doc.Lines.Count >= 55 && cv.Doc.Lang.Name == "csharp" && cv.VisibleTextForTest()[0].StartsWith("   1 using System;") && shell.SidebarFileLabelForTest.Contains("lines"),
                $"FILES: a file opens read-only with line numbers and C# colours ({cv.Doc?.Lines?.Count} lines; {shell.SidebarFileLabelForTest})");
            SbPic(shell, sb, "ui-sidebar-file.png");
            shell.SidebarOpenFileNowForTest(repo + "/big.json");
            SbPump(shell, 6f);
            Check(cv.Doc != null && (cv.Doc.Note ?? "").Contains("first") && cv.Doc.Lines.Count > 1000, $"FILES: a 3 MB file opens cut, and says so ({cv.Doc?.Lines?.Count} lines, “{cv.Doc?.Note}”)");
            shell.SidebarOpenFileNowForTest(repo + "/logo.png");
            SbPump(shell, 2f);
            Check(cv.Doc != null && cv.Doc.Message.StartsWith("Binary"), "FILES: a binary is named, not shown: " + cv.Doc?.Message);
            shell.SidebarKey("back");

            // GIT
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabGit);
            SbPump(shell, 6f);
            var gs = shell.SidebarGitStatusForTest;
            var gl = shell.SidebarGitLogForTest;
            Check(shell.SidebarGitModeForTest == "repo" && gs.Visible && gl.Visible && gs.Items.Count(r => !r.Header) >= 6 && gl.Items.Count(r => !r.Header) == 3 && gl.Items.Any(r => r.Name == "Thrice without a product"),
                $"GIT: the status ({gs.Items.Count(r => !r.Header)} entries) and the 3 commits");
            SbPic(shell, sb, "ui-sidebar-git.png");

            // STATUS
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabStatus);
            var srows = shell.SidebarStatusForTest.Items;
            string S(string key) => srows.FirstOrDefault(r => r.Name == key)?.Detail ?? "";
            Check(S("AGENT").Contains("opencode") && S("CONTEXT").StartsWith("42%") && S("APPROVALS").Length > 0 && S("FOLDER").StartsWith(repo) && S("MQH").Length > 0,
                "STATUS: " + string.Join(" | ", srows.Select(r => r.Name + " " + r.Detail)));
            SbPic(shell, sb, "ui-sidebar-status.png");
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabGit); // remembered for this thread

            // another thread, in a folder that is no repo: nothing of the first one stays
            var rowsB = new JArray
            {
                Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Write the run script" }),
                Edit(2, plain + "/notes/run.sh", "add", 1, 0, "diff --git a/notes/run.sh b/notes/run.sh\nnew file mode 100644\n--- /dev/null\n+++ b/notes/run.sh\n@@ -0,0 +1 @@\n+echo hi\n"),
            };
            shell.LoadThreadForTest(ThreadRow("thr_sbB", "Run script", plain), rowsB, usage, exec, projects, false);
            Check(!sb.IsOpen && shell.SidebarRootForTest == plain && shell.SidebarFilesForTest.All(f => !f.Path.Contains("Calc")) && shell.SidebarDiffForTest.Doc == null,
                $"another thread: its own (folded) sidebar, nothing of the first thread's folder left (open {sb.IsOpen}, root {shell.SidebarRootForTest}, files {string.Join(",", shell.SidebarFilesForTest.Select(f => f.Path))}, diff {shell.SidebarDiffForTest.Doc?.Path ?? "none"})");
            shell.SetSidebar(true, MetaCodexShell.TabGit);
            SbPump(shell, 5f);
            var im = shell.Voice.FastMatch("make this folder a git repository");
            Check(shell.SidebarGitModeForTest == "norepo" && !shell.SidebarGitStatusForTest.Visible && !shell.SidebarGitLogForTest.Visible && shell.Voice.Find("init_git").Enabled && im?.Command.Name == "init_git",
                "GIT outside a repo: only the INIT GIT state; the command init_git offered (“make this folder a git repository” -> " + (im?.Command.Name ?? "none") + ")");
            SbPic(shell, sb, "ui-sidebar-norepo.png");
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabChanges);
            var fb = shell.SidebarFilesForTest;
            Check(fb.Count == 1 && fb[0].Path == "notes/run.sh" && fb[0].RepoPath == null && shell.SidebarSummaryForTest.Contains("not a git repo"), "CHANGES outside git: the timeline's edits (" + string.Join(", ", fb.Select(f => f.Path)) + ")");
            shell.PickChangedFileForTest("notes/run.sh");
            SbPump(shell, 2f);
            Check(shell.SidebarDiffForTest.Doc?.Diff?.Added == 1 && shell.SidebarDiffForTest.Doc.Diff.NewFile, "outside git: the diff the agent's edit carried");
            shell.SidebarKey("back");
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabFiles);
            Check(shell.SidebarTreeForTest.Items.Any(r => r.Name == "notes/" && r.Badge == "●") && shell.SidebarCrumbsForTest.Parts.SequenceEqual(new[] { "plain" }), "FILES outside git: the folder, the edited one marked");
            // INIT GIT
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabGit);
            shell.SidebarInitGitForTest();
            SbPump(shell, 8f);
            Check(shell.SidebarGitModeForTest == "repo" && Directory.Exists(Path.Combine(dirWin, "uitest", "plain", ".git")) && !shell.Voice.Find("init_git").Enabled && shell.SidebarFilesForTest.Any(f => f.Path == "notes/run.sh" && f.Letter == "U"),
                "INIT GIT: git init in the project folder; GIT shows the repo, CHANGES the untracked files, init_git is withdrawn");
            SbPic(shell, sb, "ui-sidebar-after-init.png");

            // back to the first thread: open, on GIT, as it was left
            shell.LoadThreadForTest(ThreadRow("thr_sbA", "Mean returns NaN", repo), rowsA, usage, exec, projects, false);
            SbPump(shell, 6f);
            Check(sb.IsOpen && shell.SidebarTab == MetaCodexShell.TabGit && shell.SidebarRootForTest == repo, $"each thread keeps its sidebar: the first one opens again on GIT (tab {shell.SidebarTab})");

            // pinch: a hand's ray on a CHANGES row, a pinch opens that file
            shell.SidebarTabsForTest.Select(MetaCodexShell.TabChanges);
            SbPump(shell, 1f);
            var cl = shell.SidebarChangesForTest;
            int ri = cl.Items.ToList().FindIndex(r => (r.Tag as ChangedFile)?.Path == "src/tool.py");
            var target = sb.SurfacePoint(new Vector2(cl.Rect.x + 0.15f, cl.Rect.y + (ri + 0.5f) * cl.RowHeight));
            var palm = head.position + head.right * 0.12f - head.up * 0.25f + head.forward * 0.2f;
            void Aim(float pinch)
            {
                hands.SetOpen(Handedness.Right, palm, (target - palm).normalized, Vector3.down, 0f, pinch);
                hands.Frames[1].PointerValid = true; hands.Frames[1].PointerOrigin = palm; hands.Frames[1].PointerDirection = (target - palm).normalized;
            }
            void Step(int n) { for (int i = 0; i < n; i++) { shell.TickGesturesForTest(0.03f); shell.Router.UpdateForTest(); } }
            Aim(0f); Step(6); Aim(1f); Step(4); Aim(0f); Step(6);
            hands.Frames[1].Tracked = false; hands.Frames[1].PointerValid = false; Step(2);
            SbPump(shell, 3f);
            Check(ri >= 0 && shell.SidebarOpenDiffForTest == "src/tool.py", $"a pinch on a row (hand ray) opens that file's diff (row {ri}, open: {shell.SidebarOpenDiffForTest ?? "none"})");
            shell.SidebarKey("back");

            // bent: the sidebar continues the thread's curve; moved with the thread
            shell.Settings.bendPanels = true;
            shell.TickLayoutForTest(0.05f, 3);
            float R = th.BendRadius;
            var axis = th.transform.TransformPoint(new Vector3(0f, 0f, -th.BendNow));
            float rs = new Vector2(sb.transform.position.x - axis.x, sb.transform.position.z - axis.z).magnitude, rt = new Vector2(th.transform.position.x - axis.x, th.transform.position.z - axis.z).magnitude;
            Check(R > 0.3f && Mathf.Abs(sb.BendRadius * sb.RestLossyScale.x - R * th.RestLossyScale.x) < 0.01f && Mathf.Abs(rs - rt) < 0.01f,
                $"bent: the sidebar bends like the thread ({sb.BendRadius:F2} / {R:F2}) and sits on the same cylinder ({rs:F3} m / {rt:F3} m from its axis)");
            foreach (var p in new[] { th, sb }) p.ForceRefresh();
            var mid = (th.transform.position + sb.transform.position) * 0.5f;
            Render(shell, "ui-sidebar-thread-bent.png", head.position + new Vector3(0.06f, 0f, -0.25f), Quaternion.LookRotation(mid - head.position), 74f, 1920, 1080);
            shell.Settings.bendPanels = false;
            th.SetBend(0f);
            shell.TickLayoutForTest(0.05f, 3);
            var relA = th.transform.InverseTransformPoint(sb.transform.position);
            th.transform.position += new Vector3(0.25f, 0.1f, 0.05f);
            th.transform.rotation = Quaternion.Euler(0f, 12f, 0f);
            shell.TickLayoutForTest(0.05f, 3);
            var relB = th.transform.InverseTransformPoint(sb.transform.position);
            Check(sb.BendRadius == 0f && (relB - relA).magnitude < 0.001f, $"flat: the sidebar moves and turns with the thread ({(relB - relA).magnitude * 1000f:F2} mm off)");
            foreach (var p in new[] { th, sb }) p.ForceRefresh();
            Render(shell, "ui-sidebar-thread-flat.png", th.transform.position + th.transform.rotation * new Vector3(0.38f, 0f, -1.15f), th.transform.rotation * Quaternion.Euler(0f, 4f, 0f), 64f, 1920, 1080);

            // the registry: its commands, and the fast path in both languages
            var lint = new[] { "thread_sidebar", "init_git" }.SelectMany(n => VoiceCommands.Lint(shell.Voice.Find(n)).Select(w => n + ": " + w)).ToList();
            Check(lint.Count == 0, lint.Count == 0 ? "registry: thread_sidebar and init_git are complete (lint clean)" : "registry: " + string.Join("; ", lint));
            string M(string said) { var m = shell.Voice.FastMatch(said); return m == null ? "none" : (m.Command.Name + " " + string.Join(" ", m.Args.Properties().OrderBy(p => p.Name).Select(p => p.Value.ToString()))).Trim(); }
            var said = new (string text, string want)[]
            {
                ("close the thread sidebar", "thread_sidebar close"), ("open the sidebar on the git tab", "thread_sidebar open git"), ("show the status tab of this thread", "thread_sidebar open status"),
                ("chiudi la barra laterale", "thread_sidebar close"), ("apri la scheda file del thread", "thread_sidebar open files"),
            };
            var bad = said.Where(x => M(x.text) != x.want).Select(x => $"“{x.text}” -> {M(x.text)} (want {x.want})").ToList();
            Check(bad.Count == 0, bad.Count == 0 ? $"fast path: {said.Length} sentences (EN, IT) run the sidebar commands" : "fast path: " + string.Join("; ", bad));
            VoiceResult vr = null;
            shell.VoiceProcessText("close the thread sidebar", x => vr = x);
            SbPump(shell, 1f);
            Check(vr != null && vr.Command == "thread_sidebar" && !sb.IsOpen, "voice: “close the thread sidebar” folds it: " + vr?.Reply);
            shell.VoiceProcessText("open the sidebar on the files tab", x => vr = x);
            SbPump(shell, 1f);
            Check(sb.IsOpen && shell.SidebarTab == MetaCodexShell.TabFiles, "voice: “open the sidebar on the files tab”: " + vr?.Reply);
            shell.ClosePanel("thread");
            Check(!sb.IsOpen && !shell.Voice.Find("thread_sidebar").Enabled && !shell.Voice.Find("init_git").Enabled, "closing the thread closes its sidebar; its commands are withdrawn while no thread is open");
            shell.Settings.bendPanels = bend0;
        }
    }
}
