// THREAD's sidebar data layer, without Unity: run by tests/sidebar/run-sidebar-tests.sh.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using MetaCodex.AI;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;

static class Program
{
    static int ok, fail;
    static void Check(bool c, string what) { if (c) ok++; else fail++; Console.WriteLine((c ? "ok   " : "FAIL ") + what); }

    static string Spans(CodeLang lang, string line, ref int state)
    {
        var spans = new List<TokSpan>();
        state = CodeText.Tokenize(lang, line, state, spans);
        return string.Join(" ", spans.Select(s => s.Kind + ":" + line.Substring(s.Start, s.Length)));
    }

    static (int code, string output) Wsl(string command)
    {
        var psi = new ProcessStartInfo("wsl.exe", "-e bash -lc \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi);
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Replace("\r", ""));
    }

    static int Main(string[] args)
    {
        // ---- syntax colours
        int st = 0;
        var cs = CodeText.LangFor("unity/Assets/X.cs");
        string a = Spans(cs, "    public static int Foo(string s) { return 42; } // hi", ref st);
        Check(cs.Name == "csharp" && a.Contains("Keyword:public") && a.Contains("Keyword:static") && a.Contains("Keyword:int") && a.Contains("Func:Foo") && a.Contains("Keyword:string") && a.Contains("Control:return") && a.Contains("Number:42") && a.Contains("Comment:// hi"), "C#: keywords, function, control, number, comment: " + a);
        st = 0;
        a = Spans(cs, "var s = \"a \\\"q\\\" b\"; /* open", ref st);
        string b = Spans(cs, "still comment */ List<int> x = @\"multi", ref st);
        string c = Spans(cs, "line\"\" end\"; #if no", ref st);
        Check(a.Contains("String:\"a \\\"q\\\" b\"") && a.Contains("Comment:/* open") && b.StartsWith("Comment:still comment */") && b.Contains("Type:List") && st == CodeText.Normal && c.StartsWith("String:line\"\" end\""), "C#: escaped strings, block comment over lines, verbatim string over lines: " + a + " | " + b + " | " + c);
        st = 0;
        Check(Spans(cs, "  #region Fields", ref st) == "Meta:#region Fields", "C#: a preprocessor line");
        var py = CodeText.LangFor("tool.py");
        st = 0;
        a = Spans(py, "def run(x):  # go", ref st);
        b = Spans(py, "    \"\"\"Docs", ref st);
        int afterOpen = st;
        c = Spans(py, "    more\"\"\" ; return None", ref st);
        Check(a.Contains("Keyword:def") && a.Contains("Func:run") && a.Contains("Comment:# go") && afterOpen == CodeText.InTriple2 && c.Contains("Control:return") && c.Contains("Keyword:None"), "Python: def, docstring over lines, comment: " + a + " | " + c);
        st = 0;
        a = Spans(CodeText.LangFor("package.json"), "  \"name\": \"demo\", \"n\": 12, \"ok\": true", ref st);
        Check(a.Contains("Key:\"name\"") && a.Contains("String:\"demo\"") && a.Contains("Number:12") && a.Contains("Keyword:true"), "JSON: keys, strings, numbers, literals: " + a);
        st = 0;
        a = Spans(CodeText.LangFor("x.sh"), "if [ -n \"$HOME\" ]; then echo $USER; fi # done", ref st);
        Check(a.Contains("Control:if") && a.Contains("Variable:$USER") && a.Contains("Comment:# done"), "shell: control words, variables, comment: " + a);
        st = 0;
        a = Spans(CodeText.LangFor("README.md"), "## Title", ref st);
        b = Spans(CodeText.LangFor("README.md"), "- a `code` and **bold**", ref st);
        Check(a == "Heading:## Title" && b.Contains("String:`code`") && b.Contains("Keyword:**bold**"), "markdown: heading, inline code, bold: " + b);
        st = 0;
        a = Spans(CodeText.LangFor("Main.ts"), "const msg = `hi ${name}", ref st);
        Check(st == CodeText.InBacktick && a.Contains("Keyword:const"), "TS: a template string left open goes on to the next line");
        Check(CodeText.LangFor("notes.txt").Plain && CodeText.LangFor("Makefile").Name == "make" && CodeText.LangFor("a.shader").Name == "c", "languages by file name: plain text, Makefile, shaders");
        Check(CodeText.LooksBinary(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 1 }, 7) && !CodeText.LooksBinary(Encoding.UTF8.GetBytes("héllo\n\tworld"), 13), "binary: a NUL means binary, UTF-8 text is not");
        Check(CodeText.ExpandTabs("\tx\ty") == "    x   y", "tabs expand to stops of 4");

        // ---- diff rows
        var full = new StringBuilder("diff --git a/a.cs b/a.cs\nindex 1..2 100644\n--- a/a.cs\n+++ b/a.cs\n@@ -1,30 +1,30 @@\n");
        for (int i = 1; i <= 30; i++)
        {
            if (i == 15) { full.Append("-old 15\n+new 15\n+added\n"); continue; }
            if (i == 16) { full.Append("-gone 16\n"); continue; }
            full.Append(" line " + i + "\n");
        }
        var d = DiffDoc.Parse(full.ToString());
        var kinds = string.Join("", d.Lines.Select(l => l.Kind == DiffKind.Context ? "." : l.Kind == DiffKind.Add ? "+" : l.Kind == DiffKind.Del ? "-" : l.Kind == DiffKind.Fold ? "F" : l.Kind == DiffKind.Hunk ? "@" : "n"));
        var add = d.Lines.First(l => l.Kind == DiffKind.Add);
        var del2 = d.Lines.Last(l => l.Kind == DiffKind.Del);
        var after = d.Lines[d.Lines.IndexOf(d.Lines.Last(l => l.Kind == DiffKind.Del)) + 1];
        Check(d.Added == 2 && d.Removed == 2 && kinds == "F...-++-...F" && d.Path == "a.cs", "diff: +2 −2, unchanged runs folded around the change, 3 lines of context, no lone hunk header: " + kinds);
        Check(add.New == 15 && add.Old == 0 && del2.Old == 16 && after.Old == 17 && after.New == 17 && d.Lines[0].Hidden.Count == 11, $"diff: old / new line numbers (new 15, old 16 removed, then 17/17), the first fold hides 11 lines");
        Check(d.Unfold(0) && d.Lines.Count == 12 - 1 + 11 && d.Lines[0].Old == 1 && d.Folds == 1, "diff: a fold opens in place");
        var nf = DiffDoc.Parse("diff --git a/n.py b/n.py\nnew file mode 100644\n--- /dev/null\n+++ b/n.py\n@@ -0,0 +1,2 @@\n+a\n+b\n");
        Check(nf.NewFile && nf.Added == 2 && nf.Lines.Count == 2 && nf.Lines[0].New == 1 && nf.Lines[1].New == 2, "diff: a new file (/dev/null), numbered 1, 2");
        var bin = DiffDoc.Parse("diff --git a/i.png b/i.png\nindex 1..2\nBinary files a/i.png and b/i.png differ\n");
        Check(bin.Binary && bin.Lines.Count == 0, "diff: binary files are flagged");
        var multi = DiffDoc.Parse("--- a/x\n+++ b/x\n@@ -10,3 +10,3 @@ void F()\n a\n-b\n+c\n@@ -40,2 +40,3 @@\n d\n+e\n\\ No newline at end of file\n");
        Check(multi.Hunks == 2 && multi.Lines.Count(l => l.Kind == DiffKind.Hunk) == 2 && multi.Lines[0].Text == "void F()" && multi.Lines.Last().Kind == DiffKind.Note && multi.Lines.First(l => l.Kind == DiffKind.Add).New == 11, "diff: several hunks keep their headers (with the function), the no-newline note");
        d.ComputeSyntax(cs);
        Check(true, "diff: syntax states computed over folds");

        // ---- the timeline's files
        string root = "/root/proj";
        var rows = JArray.Parse(@"[
          {""kind"":""work"",""workKind"":""file-change"",""sourceSeqEnd"":3,""change"":{""path"":""/root/proj/src/a.cs"",""kind"":""update"",""diff"":""@@ -1 +1 @@\n-x\n+y\n"",""diffStats"":{""added"":1,""removed"":1}}},
          {""kind"":""turn"",""children"":[
             {""kind"":""work"",""workKind"":""file-change"",""sourceSeqEnd"":5,""change"":{""path"":""docs/new.md"",""kind"":""add"",""diffStats"":{""added"":4,""removed"":0}}},
             {""kind"":""work"",""workKind"":""tool"",""sourceSeqEnd"":6,""toolName"":""Write"",""toolArgs"":{""file_path"":""/root/proj/tools/gen.sh""}},
             {""kind"":""work"",""workKind"":""tool"",""sourceSeqEnd"":7,""toolName"":""Read"",""toolArgs"":{""file_path"":""/root/proj/README.md""}},
             {""kind"":""work"",""workKind"":""file-change"",""sourceSeqEnd"":8,""change"":{""path"":""/root/proj/src/a.cs"",""kind"":""update"",""diffStats"":{""added"":2,""removed"":0}}}]}]");
        var tl = ThreadChanges.FromTimeline(rows, root);
        Check(tl.Count == 3 && tl["src/a.cs"].Added == 3 && tl["src/a.cs"].Seq == 8 && tl["src/a.cs"].TimelineDiff != null && tl["docs/new.md"].Kind == "add" && tl.ContainsKey("tools/gen.sh") && !tl.ContainsKey("README.md"),
            "timeline: file-change rows (also in turns) and write tools, relative to the project, reads left out: " + string.Join(", ", tl.Keys));
        var outside = ThreadChanges.Merge(null, tl, false);
        Check(outside.Count == 3 && outside.All(f => f.ByThread && f.RepoPath == null) && ThreadChanges.Summary(outside, null).Contains("not a git repo"), "outside git: the timeline's files are the list");
        Check(ThreadChanges.Unquote("\"a\\tb \\303\\251.txt\"") == "a\tb é.txt", "git's quoted names are undone (tab, UTF-8 octal)");

        // ---- real repos (made by the runner)
        string R = args.Length > 0 ? args[0].Replace('\\', '/') : null;
        if (R == null || !R.StartsWith("/")) { Console.WriteLine("(no repos given: the git checks are skipped)"); }
        else
        {
            var r1 = Wsl(ThreadChanges.Script(R + "/repo", null));
            var g = ThreadChanges.ParseScript(r1.output);
            var codes = string.Join(" ", g.Status.Select(s => s.code.Trim() + ":" + s.path));
            Check(g.Repo && g.Branch == "main" && g.Base == "HEAD" && g.Prefix == "" && codes.Contains("M:Calc.cs") && codes.Contains("D:notes.txt") && codes.Contains("??:src/new_tool.py"), "git: a repo with an edit, a deletion and an untracked file: " + codes);
            Check(g.Numstat.TryGetValue("Calc.cs", out var ns) && ns.a == 2 && ns.r == 2 && g.Numstat["notes.txt"].r == 2, $"git: numstat against HEAD (Calc.cs +{ns.a} −{ns.r})");
            var tlr = ThreadChanges.FromTimeline(JArray.Parse(@"[{""kind"":""work"",""workKind"":""file-change"",""sourceSeqEnd"":4,""change"":{""path"":""" + R + @"/repo/Calc.cs"",""kind"":""update"",""diffStats"":{""added"":2,""removed"":2}}},
                {""kind"":""work"",""workKind"":""file-change"",""sourceSeqEnd"":6,""change"":{""path"":""" + R + @"/repo/src/app.py"",""kind"":""update"",""diffStats"":{""added"":1,""removed"":1}}}]"), R + "/repo");
            var files = ThreadChanges.Merge(g, tlr, false, p => (3, false, 40));
            string list = string.Join(", ", files.Select(f => $"{f.Letter} {f.Path} +{f.Added} −{f.Removed}{(f.ByThread ? "" : " (other)")}"));
            Check(files.Count == 4 && files[0].Path == "Calc.cs" && files[0].ByThread && files.First(f => f.Path == "src/app.py").Committed && files.First(f => f.Path == "src/new_tool.py").Letter == "U" && files.First(f => f.Path == "src/new_tool.py").Added == 3 && !files.First(f => f.Path == "notes.txt").ByThread,
                "merge: the thread's files first (one committed / unchanged now), the folder's other changes after: " + list);
            Check(ThreadChanges.Summary(files, g).StartsWith("2 files · +3 −3 · against HEAD · 2 other changes"), "summary: " + ThreadChanges.Summary(files, g));
            var calc = files[0];
            var dr = Wsl("git -C '" + R + "/repo' " + ThreadChanges.DiffArgs(calc, g.Base, false));
            var dd = DiffDoc.Parse(dr.output);
            Check(dd.Added == 2 && dd.Removed == 2 && dd.Folds >= 2 && dd.Lines.Any(l => l.Kind == DiffKind.Add && l.Text.Length > 300), $"git: the whole-file diff of Calc.cs folds its unchanged runs ({dd.Lines.Count} rows, {dd.Folds} folds, the 300-character line kept)");
            var nt = files.First(f => f.Path == "src/new_tool.py");
            var ur = Wsl("git -C '" + R + "/repo' " + ThreadChanges.DiffArgs(nt, g.Base, false));
            var ud = DiffDoc.Parse(ur.output);
            Check(ud.NewFile && ud.Added == 3 && ud.Lines.Count == 3, $"git: an untracked file diffs against nothing (+{ud.Added}, exit {ur.code})");
            var dl = files.First(f => f.Path == "notes.txt");
            var ddel = DiffDoc.Parse(Wsl("git -C '" + R + "/repo' " + ThreadChanges.DiffArgs(dl, g.Base, false)).output);
            Check(ddel.Deleted && ddel.Removed == 2 && dl.Letter == "D", "git: a deleted file shows its old lines removed");
            // a subfolder of the repo is the project
            var gs = ThreadChanges.ParseScript(Wsl(ThreadChanges.Script(R + "/repo/src", null)).output);
            var fs = ThreadChanges.Merge(gs, new Dictionary<string, ChangedFile>(), false);
            Check(gs.Repo && gs.Prefix == "src/" && fs.Count == 1 && fs[0].Path == "new_tool.py" && fs[0].RepoPath == "src/new_tool.py", "git: a project in a repo's subfolder sees only its own files, shown relative to it");
            // a worktree: changes since the merge base with its base branch, its own commit included
            var gw = ThreadChanges.ParseScript(Wsl(ThreadChanges.Script(R + "/wt", "main")).output);
            var fw = ThreadChanges.Merge(gw, new Dictionary<string, ChangedFile>(), true);
            Check(gw.Repo && gw.Branch == "feat" && gw.BaseLabel == "main" && fw.Any(f => f.Path == "Calc.cs" && f.Committed && f.Added == 1 && f.ByThread) && fw.Any(f => f.Path == "README.md" && f.Letter == "U"),
                "git worktree: its committed change and its untracked file, both the thread's: " + string.Join(", ", fw.Select(f => f.Letter + " " + f.Path)));
            var gp = ThreadChanges.ParseScript(Wsl(ThreadChanges.Script(R + "/plain", null)).output);
            Check(!gp.Repo && gp.Error == null, "git: a folder that is no repo says so (no error)");
            var gm = ThreadChanges.ParseScript(Wsl(ThreadChanges.Script(R + "/missing folder", null)).output);
            Check(!gm.Repo && gm.Error != null && gm.Error.Contains("does not exist"), "git: a missing folder is an error: " + gm.Error);
        }
        Console.WriteLine($"sidebar tests: {ok} ok, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
