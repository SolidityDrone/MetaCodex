using System.IO;
using System.Linq;
using MetaCodex.App;
using MetaCodex.Shell;
using MetaCodex.Terminal;
using MetaCodex.UI;
using UnityEngine;
namespace MetaCodex.EditorTools
{
    /// Self-test section `termframes` (owner 2026-11-16: "avoid them stacking on top of one another ... I get the link for
    /// the auth but I can't copy it ... make the terminal smaller ... it doesn't scale like the UI ... select, and copy"):
    /// new terminals and a hidden one shown again take free places (no overlap); the frame is THREAD's width at UI SCALE
    /// 1.0 and follows UI SCALE, the grid keeps 96 x 30 and its hit area; a pinch-drag selects a known text, COPY puts it on
    /// the clipboards, a tap drops it; a sign-in link broken by the program lights ⇗ and COPY LINK and copies whole.
    public static partial class MetaCodexUiTest
    {
        private static void TerminalFramesSection(MetaCodexShell shell, Transform head, TerminalView view, string tmp)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.Router.SelectionMode = false;
            shell.SystemClipboardForTest = false; // never the PC's own clipboard
            shell.SetClipboardDirForTest(Path.Combine(tmp, "term-clip"));
            string scale0 = shell.GetSetting("uiScale");
            shell.SetSetting("uiScale", "1");
            float k = MetaCodexShell.TerminalGridScale;
            TerminalInstance Make(int n)
            {
                var go = new GameObject("Terminal " + n);
                go.transform.SetParent(shell.transform.parent, false);
                var v = go.AddComponent<TerminalView>();
                v.CopyConfigFrom(view);
                var inst = new TerminalInstance { Number = n, Terminal = new VtTerminal(30, 96, 500), View = v };
                v.Attach(inst.Terminal);
                return inst;
            }
            float refYaw = Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg;
            bool Overlap(UiPanel p, UiPanel q) =>
                MetaCodexShell.ViewRectOf(p, head.position, refYaw, out var a) && MetaCodexShell.ViewRectOf(q, head.position, refYaw, out var b) && a.Overlaps(b, -0.5f);

            // ---- placement: two new terminals beside FILES (and the test's own terminal) cover nothing
            shell.OpenPanel("files");
            var files = shell.PanelsById["files"];
            var ta = Make(1001);
            var tb = Make(1002);
            var fa = shell.NewTerminalFrameForTest(ta);
            var fb = shell.NewTerminalFrameForTest(tb);
            string[] notWindows = { "dock", "toast", "voice-hud", "modal", "tour", "tour-ring" };
            var open = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.IsOpen && !notWindows.Contains(x.Id) && !(x.Id ?? "").StartsWith("orb-")).ToList();
            int pairs = 0;
            string which = "";
            for (int i = 0; i < open.Count; i++)
                for (int j = i + 1; j < open.Count; j++)
                    if ((open[i] == fa || open[i] == fb || open[j] == fa || open[j] == fb) && Overlap(open[i], open[j])) { pairs++; which += $" {open[i].Id}/{open[j].Id}"; }
            Check(fa != null && fb != null && fa.IsOpen && fb.IsOpen && pairs == 0,
                $"two new terminals open where no window is ({pairs} overlaps{which}; {open.Count} windows open)");
            // hidden, its place taken meanwhile, shown again: a free place, not on the window that took it
            var bPos = fb.transform.position;
            shell.HideTerminal(tb);
            fa.transform.SetPositionAndRotation(bPos, fb.transform.rotation);
            shell.ShowTerminal(tb);
            Check(fb.IsOpen && !Overlap(fa, fb) && !Overlap(files, fb), $"a hidden terminal shown again, its place taken: it opens elsewhere ({(fb.transform.position - bPos).magnitude * 100f:F0} cm away)");
            // hidden and shown again with its place still free: back where it was
            var bNow = fb.transform.position;
            shell.HideTerminal(tb);
            shell.ShowTerminal(tb);
            Check(fb.IsOpen && (fb.transform.position - bNow).magnitude < 0.01f, "a hidden terminal shown again, its place free: back where it was");

            // ---- size: THREAD's width at UI SCALE 1.0, rows of ~13 mm, 96 x 30 cells kept; the frame follows UI SCALE
            var va = ta.View;
            float widthM = fa.Width * fa.BaseScale.x, rowMm = va.CellHeight * va.transform.lossyScale.y * 1000f;
            Check(Mathf.Abs(widthM - MetaCodexShell.ThreadW) < 0.04f && rowMm > 12f && rowMm < 14f && ta.Terminal.Cols == 96 && ta.Terminal.Rows == 30,
                $"a terminal at UI SCALE 1.0 is THREAD's size ({widthM:F2} m wide, THREAD {MetaCodexShell.ThreadW:F2}; rows {rowMm:F1} mm; {ta.Terminal.Cols} x {ta.Terminal.Rows} cells)");
            shell.HideTerminal(tb);
            shell.SetSetting("uiScale", "1.2");
            bool follows = Mathf.Abs(fa.BaseScale.x - 1.2f) < 1e-3f && Mathf.Abs(fa.transform.localScale.x - 1.2f) < 1e-3f && Mathf.Abs(fb.BaseScale.x - 1.2f) < 1e-3f;
            shell.ShowTerminal(tb);
            Check(follows && Mathf.Abs(fb.transform.localScale.x - 1.2f) < 1e-3f, $"the frame follows UI SCALE, a hidden one too ({fa.BaseScale.x:F2}, {fb.BaseScale.x:F2})");
            shell.SetSetting("uiScale", "1");
            // the text area: the grid as drawn, a cell's middle maps back to that cell
            var surf = fa.Elements.OfType<UiTerminalSurface>().FirstOrDefault();
            float pad = (va.Width - va.Terminal.Cols * va.CellWidth) * 0.5f;
            Vector2 Cell(int c, int r) => surf.Rect.position + new Vector2(pad + (c + 0.5f) * va.CellWidth, pad + (r + 0.5f) * va.CellHeight) * k;
            bool mapped = surf != null && va.CellAtClamped(surf.ViewPoint(Cell(10, 5))) == new Vector2Int(10, 5) && va.CellAtClamped(surf.ViewPoint(Cell(95, 29))) == new Vector2Int(95, 29);
            Check(surf != null && Mathf.Abs(surf.Rect.width - va.Width * k) < 1e-4f && Mathf.Abs(surf.Rect.height - va.Height * k) < 1e-4f && mapped,
                "the tap and select area covers the drawn grid; a cell's middle maps to that cell");

            // ---- select and copy: a known text, a pinch dragged from "beta" to "second", COPY
            var copy = shell.TerminalCopyButtonForTest(ta);
            bool offBefore = copy != null && copy.Dimmed && !copy.Selected;
            ta.Terminal.Feed("alpha beta gamma\r\nsecond line here\r\nthird");
            va.Render();
            Click(shell.Router, fa, Cell(6, 0), Cell(5, 1));
            va.Render();
            string sel = va.SelectedText();
            bool cells = va.SelectionCells(out var from, out var to) && from == new Vector2Int(6, 0) && to == new Vector2Int(5, 1);
            Check(offBefore && va.HasSelection && sel == "beta gamma\nsecond" && cells && copy.Selected && !copy.Dimmed,
                $"a pinch dragged over the text selects cell to cell (\"{sel.Replace("\n", "\\n")}\"); COPY lights up (off before: {offBefore})");
            shell.CopyTerminalSelection(ta);
            var top = shell.Clipboard.Texts.FirstOrDefault();
            string clip = top != null ? shell.Clipboard.ReadText(top) : null;
            Check(shell.LastCopiedForTest == "beta gamma\nsecond" && clip == "beta gamma\nsecond" && CaptureClipboard.IsText(top) && shell.Clipboard.Items.All(x => !CaptureClipboard.IsText(x)),
                $"COPY: the selected text to the clipboard and CLIPBOARD's texts (\"{shell.LastCopiedForTest?.Replace("\n", "\\n")}\"), the pictures list untouched");
            RenderPanel(fa, "ui-terminal-select.png");
            Click(shell.Router, fa, Cell(2, 2));
            Check(!va.HasSelection && copy.Dimmed && !copy.Selected, "a tap on the text drops the selection (and focuses the terminal)");
            Click(shell.Router, fa, Cell(2, 2), Cell(2, 2) + new Vector2(va.CellWidth * k * 0.4f, 0f));
            Check(!va.HasSelection, "a tap that jitters less than a cell and a half selects nothing");

            // ---- links: a sign-in link the program broke at its own width (no wrap marks): found whole, ⇗ and COPY LINK lit
            string url = "https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback&scope=user%3Ainference&state=Zx81kq";
            var openLink = shell.TerminalOpenLinkButtonForTest(tb);
            var copyLink = shell.TerminalCopyLinkButtonForTest(tb);
            bool dimBefore = openLink != null && copyLink != null && openLink.Dimmed && copyLink.Dimmed;
            var sb = new System.Text.StringBuilder("\r\n Browser didn't open? Use the url below to sign in (c to copy)\r\n\r\n");
            for (int i = 0; i < url.Length; i += 94) sb.Append(' ').Append(url.Substring(i, Mathf.Min(94, url.Length - i))).Append("\r\n");
            sb.Append("\r\n Paste code here if prompted > ");
            tb.Terminal.Feed(sb.ToString());
            shell.ScanTerminalLinksForTest();
            bool lit = openLink.Selected && !openLink.Dimmed && copyLink.Selected && !copyLink.Dimmed;
            shell.CopyTerminalLink(tb);
            Check(dimBefore && lit && shell.LastCopiedForTest == url,
                $"a sign-in link broken over {Mathf.CeilToInt(url.Length / 94f)} rows: ⇗ and COPY LINK light up ({dimBefore} -> {lit}), COPY LINK copies it whole ({shell.LastCopiedForTest?.Length} of {url.Length} chars)");

            shell.DropTerminalForTest(ta);
            shell.DropTerminalForTest(tb);
            shell.ClosePanel("files");
            shell.SetSetting("uiScale", scale0);
            shell.SetClipboardDirForTest(null);
            shell.SystemClipboardForTest = true;
        }
    }
}
