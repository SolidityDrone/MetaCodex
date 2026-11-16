using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCodex.Terminal
{
    /// Text out of a VtTerminal, without Unity (the core tests run it): the text of a selection, and the links on it.
    /// Lines are numbered as VtTerminal.LinesScrolled counts them: screen row r is line LinesScrolled + r, the scrollback's
    /// lines come before it. A number stays on its text while the text scrolls.
    public static class TerminalText
    {
        /// The line `line`: a screen row, a scrollback line, or null (gone out of the scrollback, or past the screen).
        public static Cell[] Line(VtTerminal t, long line)
        {
            long virt = line - t.LinesScrolled;
            if (virt >= 0) return virt < t.Rows ? t.GetRow((int)virt) : null;
            long sb = t.ScrollbackCount + virt;
            return sb >= 0 ? t.GetScrollbackLine((int)sb) : null;
        }

        /// The oldest line still kept (the scrollback's first, or the screen's first when it has none).
        public static long FirstLine(VtTerminal t) => t.LinesScrolled - t.ScrollbackCount;

        /// The text ran past the row's last cell (autowrap): it goes on in the next row, with no line break.
        public static bool Continues(Cell[] row) => row != null && row.Length > 0 && (row[row.Length - 1].Attr & CellAttr.Wrapped) != 0;

        /// The text from (line0, col0) to (line1, col1), both cells included, in either order, as it reads: the first line
        /// from col0 on, whole lines between, the last up to col1. Rows the terminal wrapped are joined without a break;
        /// blanks at a line's end and empty lines at the end are dropped; a wide character counts once.
        public static string Selection(VtTerminal t, long line0, int col0, long line1, int col1)
        {
            if (line1 < line0 || line1 == line0 && col1 < col0)
            {
                long l = line0; line0 = line1; line1 = l;
                int c = col0; col0 = col1; col1 = c;
            }
            var sb = new StringBuilder();
            for (long l = line0; l <= line1; l++)
            {
                var row = Line(t, l);
                if (row == null || row.Length == 0) { if (l < line1) sb.Append('\n'); continue; }
                int n = row.Length;
                int c0 = l == line0 ? Clamp(col0, 0, n - 1) : 0;
                int c1 = l == line1 ? Clamp(col1, 0, n - 1) : n - 1;
                if (c0 > 0 && (row[c0].Attr & CellAttr.WideTail) != 0) c0--; // from the wide character's first cell
                int start = sb.Length;
                for (int c = c0; c <= c1; c++)
                {
                    if ((row[c].Attr & CellAttr.WideTail) != 0) continue;
                    int r = row[c].Rune;
                    VtTerminal.AppendRune(sb, r == 0 ? ' ' : r);
                }
                bool wraps = c1 == n - 1 && Continues(row) && l < line1;
                if (!wraps)
                {
                    int end = sb.Length;
                    while (end > start && sb[end - 1] == ' ') end--;
                    sb.Length = end;
                    if (l < line1) sb.Append('\n');
                }
            }
            int keep = sb.Length;
            while (keep > 0 && sb[keep - 1] == '\n') keep--; // empty rows dragged over at the end add nothing
            sb.Length = keep;
            return sb.ToString();
        }

        // ---- links

        private static readonly Regex Url = new Regex(@"https?://[^\s""'<>`│┃║]+", RegexOptions.Compiled);
        private static readonly char[] UrlTrail = { '.', ',', ')', ']', ';', ':', '\'', '"' };
        private static readonly char[] UrlMarks = { '/', '?', '=', '&', '%', '#' };

        /// How near the right edge (columns) a link must end for the next line to be taken as its continuation when the
        /// program broke the line itself (a TUI that wraps its own text, with a margin or a box border).
        public const int EdgeSlack = 4;

        /// The newest link on the screen and the `history` scrollback lines above it, or null.
        public static string LastUrl(VtTerminal t, int history = 0)
        {
            var all = Urls(t, history);
            return all.Count > 0 ? all[all.Count - 1] : null;
        }

        /// The http(s) links on the screen (and the last `history` lines of the scrollback), oldest first. A link the
        /// terminal wrapped is whole (rows joined); a link a program broke itself (sign-in links are long: a TUI wraps them
        /// at its own width, with no wrap mark) is joined too when it sits on one row, runs to within EdgeSlack columns of
        /// the right edge, and the next line goes on with link characters: the whole of that line, or a part with
        /// / ? = & % #; never the line with the visible cursor (the shell's prompt after a link that ends at the edge).
        public static List<string> Urls(VtTerminal t, int history = 0)
        {
            var lines = Logical(t, history);
            var list = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                var ln = lines[i];
                int skipTo = i;
                foreach (Match m in Url.Matches(ln.Text))
                {
                    string url = m.Value;
                    int after = m.Index + m.Length;
                    // the link ends the line's text, near the right edge: the next lines may carry on with it
                    if (after >= ln.CoreEnd && ln.Rows == 1 && ln.EndCol >= t.Cols - 1 - EdgeSlack)
                    {
                        for (int j = i + 1; j < lines.Count; j++)
                        {
                            var next = lines[j];
                            if (next.HasCursor || next.Rows != 1) break;
                            string core = next.Text.Substring(next.CoreStart, next.CoreEnd - next.CoreStart);
                            int run = 0;
                            while (run < core.Length && UrlChar(core[run])) run++;
                            if (run == 0) break;
                            bool whole = run == core.Length;
                            if (!whole && core.Substring(0, run).IndexOfAny(UrlMarks) < 0) break;
                            url += core.Substring(0, run);
                            skipTo = j; // a piece of this link: not read again as a link of its own
                            if (!whole || next.EndCol < t.Cols - 1 - EdgeSlack) break;
                        }
                    }
                    list.Add(url.TrimEnd(UrlTrail));
                }
                i = skipTo;
            }
            return list;
        }

        private static bool UrlChar(char c) => c > ' ' && c != '"' && c != '\'' && c != '<' && c != '>' && c != '`' && c != '│' && c != '┃' && c != '║';

        private static bool Border(char c) => c == '│' || c == '┃' || c == '║' || c == '|';

        /// A line as read: rows the terminal wrapped joined. CoreStart / CoreEnd: its text without the blanks and box
        /// borders at its ends; EndCol: the column its last row's text (without a border) ends at, -1 when blank; Rows: how
        /// many rows it takes; HasCursor: the visible cursor is on it.
        private struct LogicalLine { public string Text; public int CoreStart, CoreEnd, EndCol, Rows; public bool HasCursor; }

        private static List<LogicalLine> Logical(VtTerminal t, int history)
        {
            var list = new List<LogicalLine>();
            long first = t.LinesScrolled - System.Math.Min(System.Math.Max(0, history), t.ScrollbackCount);
            long last = t.LinesScrolled + t.Rows - 1;
            var sb = new StringBuilder(t.Cols * 2);
            int endCol = -1, rows = 0;
            long cursorLine = t.CursorVisible ? t.LinesScrolled + t.CursorRow : long.MinValue;
            bool cursor = false;
            for (long l = first; l <= last; l++)
            {
                var row = Line(t, l);
                if (row == null) continue;
                rows++;
                cursor |= l == cursorLine;
                int lastCell = -1;
                for (int c = 0; c < row.Length; c++)
                {
                    if ((row[c].Attr & CellAttr.WideTail) != 0) continue;
                    int r = row[c].Rune;
                    if (r != 0 && r != ' ') lastCell = c;
                }
                // a box's right border is not text: the column the text ends at is before it (and its padding)
                endCol = lastCell;
                if (endCol >= 0 && row[endCol].Rune < 0x10000 && Border((char)row[endCol].Rune))
                {
                    endCol--;
                    while (endCol >= 0 && (row[endCol].Rune == 0 || row[endCol].Rune == ' ')) endCol--;
                }
                for (int c = 0; c <= lastCell; c++)
                {
                    if ((row[c].Attr & CellAttr.WideTail) != 0) continue;
                    int r = row[c].Rune;
                    VtTerminal.AppendRune(sb, r == 0 ? ' ' : r);
                }
                if (Continues(row) && l < last)
                {
                    for (int c = lastCell + 1; c < row.Length; c++) sb.Append(' '); // blanks inside a wrapped line are text
                    continue;
                }
                list.Add(Make(sb.ToString(), endCol, rows, cursor));
                sb.Clear();
                rows = 0;
                cursor = false;
            }
            if (rows > 0) list.Add(Make(sb.ToString(), endCol, rows, cursor));
            return list;
        }

        private static LogicalLine Make(string text, int endCol, int rows, bool cursor)
        {
            int s = 0, e = text.Length;
            while (s < e && (text[s] == ' ' || Border(text[s]))) s++;
            while (e > s && (text[e - 1] == ' ' || Border(text[e - 1]))) e--;
            return new LogicalLine { Text = text, CoreStart = s, CoreEnd = e, EndCol = endCol, Rows = rows, HasCursor = cursor };
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
