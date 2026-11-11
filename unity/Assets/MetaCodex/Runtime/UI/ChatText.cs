using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCodex.UI
{
    public enum ParaStyle { Base, Heading, Quote, Rule, Code, Table, Blank }
    public enum SpanKind { Bold, Code, Link }

    /// One paragraph of agent text after markdown-lite: its plain text (marks removed), coloured spans in it, its style.
    public sealed class ChatPara
    {
        public string Plain = "";
        public List<(int s, int e, SpanKind k)> Spans = new List<(int, int, SpanKind)>();
        public ParaStyle Style;
        public bool Mono => Style == ParaStyle.Code || Style == ParaStyle.Table;
    }

    /// The text side of UiChatView, without Unity (tested outside the Editor): markdown-lite to paragraphs, wrapped
    /// lines back to their paragraph and place, and a line's rich text with its spans (colour only, so widths stay as
    /// measured).
    public static class ChatText
    {
        private static readonly Regex Heading = new Regex(@"^\s{0,3}(#{1,6})\s+(.*)$"), Bullet = new Regex(@"^(\s*)[-*+]\s+(.*)$"), Numbered = new Regex(@"^(\s*)(\d+)[.)]\s+(.*)$"),
            Quote = new Regex(@"^\s*>\s?(.*)$"), Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$"), Inline = new Regex(@"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)");

        /// Headings, bullets, numbers, quotes, rules, code blocks and tables (those two in the terminal font);
        /// **bold**, `code` and [links](url) become spans. Blank lines collapse to one; trailing ones go.
        public static List<ChatPara> Paragraphs(string src, int maxChars = 6000)
        {
            src = (src ?? "").Replace("\r", "").Replace("\t", "    ");
            if (src.Length > maxChars) src = src.Substring(0, maxChars) + " …";
            var paras = new List<ChatPara>();
            bool inCode = false, blank = false;
            foreach (var raw in src.Split('\n'))
            {
                if (raw.TrimStart().StartsWith("```")) { inCode = !inCode; continue; }
                if (inCode) { paras.Add(new ChatPara { Plain = raw.Length == 0 ? " " : raw, Style = ParaStyle.Code }); blank = false; continue; }
                if (raw.Trim().Length == 0) { if (!blank && paras.Count > 0) paras.Add(new ChatPara { Style = ParaStyle.Blank }); blank = true; continue; }
                blank = false;
                if (Rule.IsMatch(raw)) { paras.Add(new ChatPara { Plain = "────────────", Style = ParaStyle.Rule }); continue; }
                if (raw.TrimStart().StartsWith("|")) { paras.Add(new ChatPara { Plain = raw.Trim(), Style = ParaStyle.Table }); continue; }
                var p = new ChatPara { Style = ParaStyle.Base };
                string text = raw, prefix = "";
                Match m;
                if ((m = Heading.Match(raw)).Success) { text = m.Groups[2].Value; p.Style = ParaStyle.Heading; }
                else if ((m = Bullet.Match(raw)).Success) { prefix = new string(' ', Math.Min(8, m.Groups[1].Value.Length)) + "• "; text = m.Groups[2].Value; }
                else if ((m = Numbered.Match(raw)).Success) { prefix = new string(' ', Math.Min(8, m.Groups[1].Value.Length)) + m.Groups[2].Value + ". "; text = m.Groups[3].Value; }
                else if ((m = Quote.Match(raw)).Success) { prefix = "│ "; text = m.Groups[1].Value; p.Style = ParaStyle.Quote; }
                var sb = new StringBuilder(prefix);
                int at = 0;
                foreach (Match im in Inline.Matches(text))
                {
                    sb.Append(text, at, im.Index - at);
                    string inner = im.Groups[1].Success ? im.Groups[1].Value : im.Groups[2].Success ? im.Groups[2].Value : im.Groups[3].Success ? im.Groups[3].Value : im.Groups[4].Value;
                    var kind = im.Groups[3].Success ? SpanKind.Code : im.Groups[4].Success ? SpanKind.Link : SpanKind.Bold;
                    p.Spans.Add((sb.Length, sb.Length + inner.Length, kind));
                    sb.Append(inner);
                    at = im.Index + im.Length;
                }
                sb.Append(text, at, text.Length - at);
                p.Plain = sb.ToString();
                paras.Add(p);
            }
            while (paras.Count > 0 && paras[paras.Count - 1].Style == ParaStyle.Blank) paras.RemoveAt(paras.Count - 1);
            return paras;
        }

        /// Paragraphs joined for one layout pass (a blank paragraph is one space, so it keeps its line).
        public static string Join(IList<ChatPara> run) => string.Join("\n", run.Select(p => p.Plain.Length == 0 ? " " : p.Plain));

        /// Where each paragraph starts in Join(run).
        public static List<int> Starts(IList<ChatPara> run)
        {
            var starts = new List<int>();
            int acc = 0;
            foreach (var p in run) { starts.Add(acc); acc += (p.Plain.Length == 0 ? 1 : p.Plain.Length) + 1; }
            return starts;
        }

        /// Wrapped lines from the layout's character ranges [a, b) over `text`: each line's text (without its break
        /// and trailing spaces) and the paragraph it is in (counted by the explicit breaks passed).
        public static List<(string text, int para)> LinesFromRanges(string text, IEnumerable<(int a, int b)> ranges)
        {
            var res = new List<(string, int)>();
            int para = 0, lastEnd = 0;
            foreach (var (a0, b0) in ranges)
            {
                int a = Math.Max(0, Math.Min(a0, text.Length)), b = Math.Max(a, Math.Min(b0, text.Length));
                for (int k = lastEnd; k < a; k++) if (text[k] == '\n') para++;
                lastEnd = Math.Max(lastEnd, a);
                string s = text.Substring(a, b - a);
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(0, nl); // the break belongs to the line it ends
                res.Add((s.TrimEnd(), para));
            }
            return res;
        }

        /// Where a wrapped line starts inside its paragraph (lines come in order: a cursor over the joined text).
        public static int LineStart(string joined, string line, int paraStart, ref int cursor)
        {
            int at = cursor >= paraStart ? cursor : paraStart;
            int f = line.Length == 0 ? at : joined.IndexOf(line, at, StringComparison.Ordinal);
            if (f < 0) f = at;
            cursor = f + line.Length;
            return f - paraStart;
        }

        /// A line's rich text: the plain parts escaped, the spans that fall in it coloured (`hex` per kind, "RRGGBB").
        public static string Styled(string line, List<(int s, int e, SpanKind k)> spans, int start, Func<SpanKind, string> hex)
        {
            if (spans == null || spans.Count == 0) return Esc(line);
            var sb = new StringBuilder();
            int pos = 0, end = line.Length;
            foreach (var (s0, e0, k) in spans)
            {
                int s = Math.Max(0, Math.Min(end, s0 - start)), e = Math.Max(0, Math.Min(end, e0 - start));
                if (e <= s || s < pos) continue;
                if (s > pos) sb.Append(Esc(line.Substring(pos, s - pos)));
                sb.Append("<color=#").Append(hex(k)).Append('>').Append(Esc(line.Substring(s, e - s))).Append("</color>");
                pos = e;
            }
            if (pos < end) sb.Append(Esc(line.Substring(pos)));
            return sb.ToString();
        }

        /// Text shown as it is (no rich-text tags from agents or users).
        public static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";

        /// Rich text back to what a reader sees (self-tests).
        public static string Strip(string s) => Regex.Replace(s ?? "", @"</?noparse>|<color=[^>]*>|</color>|<alpha=[^>]*>|<i>|</i>|<b>|</b>|<size=[^>]*>|</size>", "");
    }
}
