using System.IO;
using System.Linq;
using MetaCodex.Shell;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace MetaCodex.EditorTools
{
    /// Self-test section `returns` (owner 2026-11-11, docs/native-build-and-install.md "Back and report"): a built app's
    /// menu comes back to MetaCodex. A report with a launch id MetaCodex gave out lands in the launching thread (the
    /// picture in CLIPBOARD, picture + log as chips, the "Feedback on" draft); a forged id or another package is ignored
    /// and its files deleted; a plain Back keeps the thread open.
    public static partial class MetaCodexUiTest
    {
        private static void ReturnsSection(MetaCodexShell shell)
        {
            var row = new JObject { ["id"] = "thr_ret", ["projectId"] = "prj_ret", ["providerId"] = "claude-code", ["title"] = "Native app", ["status"] = "idle", ["runtime"] = new JObject { ["displayStatus"] = "idle" }, ["environmentPath"] = "/userland", ["updatedAt"] = 0 };
            var rows = new JArray { Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Make me an app" }) };
            shell.LoadThreadForTest(row, rows, null, null, new JArray { new JObject { ["id"] = "prj_ret", ["name"] = "ret", ["sources"] = new JArray() } }, false);
            shell.OpenPanel("thread");
            string id = shell.RegisterLaunch("com.metacodex.user.t2", "thr_ret");
            string dir = shell.Clipboard.Dir;
            Directory.CreateDirectory(dir);
            string Files(string tag)
            {
                var tex = new Texture2D(8, 6, TextureFormat.RGB24, false);
                string png = Path.Combine(dir, "report-test-" + tag + ".png"), log = Path.Combine(dir, "report-test-" + tag + ".log.txt");
                File.WriteAllBytes(png, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                File.WriteAllText(log, "12:00:00 menu: Report to MetaCodex\n");
                return png + "|" + log;
            }
            JObject Ret(string launchId, string from, bool report, string files) => new JObject
            {
                ["from"] = from, ["launchId"] = launchId, ["label"] = "T2", ["version"] = "1.0 (1)", ["report"] = report,
                ["png"] = files?.Split('|')[0] ?? "", ["log"] = files?.Split('|')[1] ?? "", ["launchedFrom"] = "",
            };
            int clips0 = shell.Clipboard.Items.Count, att0 = shell.AttachmentsForTest.Count;
            // forged: an id nobody gave out, then a real id from another package: nothing lands, the files go
            string f1 = Files("forged"), f2 = Files("other");
            shell.HandleReturn(Ret("0123456789abcdef0123456789abcdef", "com.metacodex.user.t2", true, f1).ToString());
            shell.HandleReturn(Ret(id, "com.evil.app", true, f2).ToString());
            bool gone = f1.Split('|').Concat(f2.Split('|')).All(p => !File.Exists(p));
            Check(shell.Clipboard.Items.Count == clips0 && shell.AttachmentsForTest.Count == att0 && gone,
                $"returns with a forged launch id or another package are ignored, their files deleted ({shell.Clipboard.Items.Count - clips0} captures, {shell.AttachmentsForTest.Count - att0} chips, files gone {gone})");
            // the real report
            shell.HandleReturn(Ret(id, "com.metacodex.user.t2", true, Files("ok")).ToString());
            var att = shell.AttachmentsForTest;
            string draft = shell.ComposerForTest?.Text ?? "";
            bool img = att.Any(a => a.Kind == "image"), log = att.Any(a => a.Kind == "file" && a.Name == "log T2");
            Check(shell.Clipboard.Items.Count == clips0 + 1 && img && log && draft.Contains("Feedback on T2 1.0 (1):") && shell.PendingReportThreadForTest == null,
                $"a report with its launch id: picture in CLIPBOARD ({shell.Clipboard.Items.Count - clips0}), chips image {img} + log {log}, draft \"{draft.Trim()}\"");
            // a plain Back: nothing added, the thread stays
            int att1 = shell.AttachmentsForTest.Count;
            shell.HandleReturn(Ret(id, "com.metacodex.user.t2", false, null).ToString());
            Check(shell.AttachmentsForTest.Count == att1 && shell.IsPanelOpen("thread"), "Back without a report: the thread is open, no new chips");
            foreach (var p in Directory.GetFiles(dir, "report-test-*")) File.Delete(p);
        }
    }
}
