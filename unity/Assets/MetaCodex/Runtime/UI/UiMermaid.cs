using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MetaCodex.UI
{
    /// A subset of Mermaid for agent views: flowcharts (graph / flowchart TD|LR|BT|RL: node shapes, edge labels,
    /// chains, &), sequence diagrams (participants, ->> / -->> messages, notes) and pie charts. Styling lines
    /// (classDef, style, click, subgraph frames) are accepted and ignored.
    public static class UiMermaid
    {
        public static string Kind(string src)
        {
            var first = FirstLine(src).ToLowerInvariant();
            if (first.StartsWith("sequencediagram")) return "sequence";
            if (first.StartsWith("pie")) return "pie";
            if (first.StartsWith("graph") || first.StartsWith("flowchart")) return "flow";
            return null;
        }

        private static string FirstLine(string src)
        {
            foreach (var l in (src ?? "").Replace("\r", "").Split('\n'))
            {
                var t = l.Trim();
                if (t.Length == 0 || t.StartsWith("%%")) continue;
                return t;
            }
            return "";
        }

        /// Statements: lines, and ';' outside brackets / quotes.
        private static IEnumerable<string> Statements(string src)
        {
            foreach (var raw in (src ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw;
                int c = line.IndexOf("%%", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c);
                int depth = 0; bool q = false;
                var cur = new System.Text.StringBuilder();
                foreach (char ch in line)
                {
                    if (ch == '"') q = !q;
                    if (!q && (ch == '[' || ch == '(' || ch == '{')) depth++;
                    if (!q && (ch == ']' || ch == ')' || ch == '}')) depth = Math.Max(0, depth - 1);
                    if (ch == ';' && depth == 0 && !q) { if (cur.ToString().Trim().Length > 0) yield return cur.ToString().Trim(); cur.Clear(); continue; }
                    cur.Append(ch);
                }
                if (cur.ToString().Trim().Length > 0) yield return cur.ToString().Trim();
            }
        }

        // a node reference: id plus an optional shape with its label
        private static readonly Regex NodeRe = new Regex(
            @"^\s*([A-Za-z0-9_\u00C0-\uFFFF][\w\.\u00C0-\uFFFF]*)\s*(\[\[(.*?)\]\]|\[\((.*?)\)\]|\(\[(.*?)\]\)|\(\(\((.*?)\)\)\)|\(\((.*?)\)\)|\{\{(.*?)\}\}|\[/(.*?)/\]|\[\\(.*?)\\\]|\[(.*?)\]|\((.*?)\)|\{(.*?)\}|>(.*?)\])?(?=\s*(?:$|&|-|=|~|\.|<|\|))",
            RegexOptions.Compiled);

        private static readonly Regex LinkRe = new Regex(
            @"^\s*(<)?(-{2,}>|={2,}>|-\.+-?>|-{3,}|={3,}|-\.+-|~~~|--[ox]|==[ox])\s*(?:\|([^|]*)\|)?",
            RegexOptions.Compiled);

        // "A -- text --> B", "A == text ==> B", "A -. text .-> B" -> the |text| form
        private static readonly Regex TextLink1 = new Regex(@"--\s*([^->|][^>]*?)\s*-->", RegexOptions.Compiled);
        private static readonly Regex TextLink2 = new Regex(@"==\s*([^=>|][^>]*?)\s*==>", RegexOptions.Compiled);
        private static readonly Regex TextLink3 = new Regex(@"-\.\s*([^.>|][^>]*?)\s*\.->", RegexOptions.Compiled);

        public static bool ParseFlow(string src, UiFlow flow, out string error)
        {
            error = null;
            bool first = true;
            foreach (var st0 in Statements(src))
            {
                var st = st0;
                if (first)
                {
                    first = false;
                    var m = Regex.Match(st, @"^(graph|flowchart)\s*(TD|TB|LR|RL|BT)?", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        var dir = m.Groups[2].Value.ToUpperInvariant();
                        flow.Direction = dir == "LR" || dir == "RL" || dir == "BT" ? dir : "TD";
                        st = st.Substring(m.Length).Trim();
                        if (st.Length == 0) continue;
                    }
                }
                string low = st.ToLowerInvariant();
                if (low == "end" || low.StartsWith("subgraph") || low.StartsWith("classdef") || low.StartsWith("class ") || low.StartsWith("style ") ||
                    low.StartsWith("linkstyle") || low.StartsWith("click ") || low.StartsWith("direction ") || low.StartsWith("acctitle") || low.StartsWith("accdescr")) continue;
                st = TextLink1.Replace(st, "-->|$1|");
                st = TextLink2.Replace(st, "==>|$1|");
                st = TextLink3.Replace(st, "-.->|$1|");
                ParseChain(st, flow);
            }
            if (flow.Nodes.Count == 0) error = "no nodes found";
            return error == null;
        }

        private static void ParseChain(string st, UiFlow flow)
        {
            int pos = 0;
            var prev = ParseGroup(st, ref pos, flow);
            if (prev.Count == 0) return;
            while (pos < st.Length)
            {
                var m = LinkRe.Match(st.Substring(pos));
                if (!m.Success) break;
                pos += m.Length;
                string arrow = m.Groups[2].Value;
                string label = m.Groups[3].Success ? Unquote(m.Groups[3].Value) : null;
                var next = ParseGroup(st, ref pos, flow);
                if (next.Count == 0) break;
                string style = arrow.StartsWith("=") ? "thick" : arrow.Contains(".") ? "dotted" : arrow == "~~~" ? "invisible" : "solid";
                bool head = arrow.EndsWith(">") || arrow.EndsWith("o") || arrow.EndsWith("x");
                foreach (var a in prev)
                    foreach (var b in next)
                        if (style != "invisible") flow.Edges.Add(new UiFlow.Edge { From = a, To = b, Label = label, Style = style, Arrow = head });
                prev = next;
            }
        }

        private static List<string> ParseGroup(string st, ref int pos, UiFlow flow)
        {
            var ids = new List<string>();
            while (pos < st.Length)
            {
                var m = NodeRe.Match(st.Substring(pos));
                if (!m.Success || m.Length == 0) break;
                string id = m.Groups[1].Value;
                string label = null, shape = null;
                if (m.Groups[2].Success && m.Groups[2].Length > 0)
                {
                    string g = m.Groups[2].Value;
                    label = Unquote(FirstGroup(m, 3, 16));
                    shape = g.StartsWith("((") ? "circle" : g.StartsWith("([") ? "stadium" : g.StartsWith("{") ? "diamond" : g.StartsWith("(") || g.StartsWith("[(") ? "round" : "rect";
                }
                flow.Ensure(id, label, shape);
                ids.Add(id);
                pos += m.Length;
                var amp = Regex.Match(st.Substring(pos), @"^\s*&\s*");
                if (!amp.Success) break;
                pos += amp.Length;
            }
            return ids;
        }

        private static string FirstGroup(Match m, int from, int to)
        {
            for (int i = from; i <= to && i < m.Groups.Count; i++) if (m.Groups[i].Success) return m.Groups[i].Value;
            return "";
        }

        private static string Unquote(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("#quot;", "\"");
        }

        private static readonly Regex MsgRe = new Regex(@"^([^\s\-:>+)]+?)\s*(-->>|->>|-->|->|--x|-x|--\)|-\))\s*[+\-]?\s*([^\s:]+)\s*:\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex NoteRe = new Regex(@"^note\s+(over|left of|right of)\s+([^:]+):\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool ParseSequence(string src, UiSequence seq, out string error)
        {
            error = null;
            foreach (var st in Statements(src))
            {
                string low = st.ToLowerInvariant();
                if (low.StartsWith("sequencediagram") || low.StartsWith("autonumber") || low.StartsWith("activate") || low.StartsWith("deactivate") ||
                    low.StartsWith("loop") || low.StartsWith("alt") || low.StartsWith("else") || low.StartsWith("opt") || low.StartsWith("par") ||
                    low.StartsWith("and") || low.StartsWith("rect") || low.StartsWith("critical") || low.StartsWith("break") || low == "end" || low.StartsWith("title")) continue;
                var pm = Regex.Match(st, @"^(participant|actor)\s+(\S+)(?:\s+as\s+(.+))?$", RegexOptions.IgnoreCase);
                if (pm.Success) { seq.Participant(pm.Groups[2].Value, pm.Groups[3].Success ? pm.Groups[3].Value.Trim() : null); continue; }
                var nm = NoteRe.Match(st);
                if (nm.Success)
                {
                    var who = nm.Groups[2].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    if (who.Count == 0) continue;
                    foreach (var w in who) seq.Participant(w);
                    seq.Messages.Add((who[0], who[who.Count - 1], nm.Groups[3].Value.Trim(), false, true));
                    continue;
                }
                var mm = MsgRe.Match(st);
                if (mm.Success)
                {
                    string a = mm.Groups[1].Value, b = mm.Groups[3].Value;
                    seq.Participant(a); seq.Participant(b);
                    seq.Messages.Add((a, b, mm.Groups[4].Value.Trim(), mm.Groups[2].Value.StartsWith("--"), false));
                }
            }
            if (seq.Participants.Count == 0) error = "no participants found";
            return error == null;
        }

        public static bool ParsePie(string src, UiChart chart, out string error)
        {
            error = null;
            chart.Kind = "donut";
            var data = new List<float>();
            foreach (var st in Statements(src))
            {
                var tm = Regex.Match(st, @"^pie(?:\s+showData)?(?:\s+title\s+(.+))?$", RegexOptions.IgnoreCase);
                if (tm.Success) { if (tm.Groups[1].Success) chart.Title = tm.Groups[1].Value.Trim(); continue; }
                var ti = Regex.Match(st, @"^title\s+(.+)$", RegexOptions.IgnoreCase);
                if (ti.Success) { chart.Title = ti.Groups[1].Value.Trim(); continue; }
                var m = Regex.Match(st, "^\"?([^\":]+)\"?\\s*:\\s*([-0-9.eE]+)");
                if (m.Success && float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) { chart.Labels.Add(m.Groups[1].Value.Trim()); data.Add(v); }
            }
            chart.Series.Clear();
            chart.Series.Add(("", data));
            if (data.Count == 0) error = "no slices found";
            return error == null;
        }
    }
}
