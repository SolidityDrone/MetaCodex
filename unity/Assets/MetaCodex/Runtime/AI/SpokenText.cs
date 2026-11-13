using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCodex.AI
{
    /// The text side of spoken replies (docs/voice.md "Spoken replies, sentence by sentence"): a reply is split into
    /// sentences that are synthesized one by one (the first one plays while the next is made), shortened to what is
    /// worth hearing, and written the way the voice model reads it well ("3 minutes", not "3 min"). Pure C#: the core
    /// tests (tests/voicefx) run it without Unity.
    public static class SpokenText
    {
        /// A sentence shorter than this goes with the next one ("Done." + "Nothing needs you."): a request per tiny clip
        /// costs a fixed overhead and leaves a gap while the next one is made.
        public const int ShortSentence = 20;
        /// A sentence longer than this is split at a comma, semicolon or colon (it would hold the first audio back).
        public const int LongSentence = 180;

        private static readonly HashSet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "st", "vs", "approx", "cf", "ca", "e.g", "i.e", "eg", "ie",
            "ver", "rev", "inc", "ltd", "jr", "sr",
        };
        /// Abbreviations only in front of a number ("No. 5", "fig. 3"); "No. It works." still ends a sentence.
        private static readonly HashSet<string> NumberAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "no", "nr", "n", "fig", "art", "vol", "p", "pp" };

        /// The sentences of `text`, in order: split after . ! ? … when a space and a word that is not lower case follow,
        /// but not inside "foo.cs", "3.5", "e.g.", "Dr. Brown", "J. Smith" or "wait... then"; line breaks, list items
        /// and " · " (several replies joined) also end one. Very short ones join the next, very long ones are cut at a comma.
        public static List<string> Sentences(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;
            foreach (var block in Regex.Split(Clean(text), @"\n+|\s+·\s+"))
            {
                string b = Regex.Replace(block, @"\s+", " ").Trim();
                if (b.Length == 0) continue;
                int start = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    char ch = b[i];
                    if (!IsEnd(ch)) continue;
                    int j = i;
                    while (j + 1 < b.Length && IsEnd(b[j + 1])) j++; // "?!", "...", "…"
                    int k = j;
                    while (k + 1 < b.Length && IsCloser(b[k + 1])) k++; // ." .) .”
                    if (k + 1 < b.Length && b[k + 1] != ' ') { i = k; continue; } // foo.cs, 3.5, v1.2, e.g.x
                    char next = k + 2 < b.Length ? b[k + 2] : '\0';
                    if (next != '\0' && char.IsLower(next)) { i = k; continue; } // "e.g. the", "wait... then", "approx. three"
                    if (ch == '.' && j == i && next != '\0' && IsAbbreviation(b, start, i, char.IsDigit(next))) { i = k; continue; } // "Dr. Brown", "No. 5"
                    string s = b.Substring(start, k + 1 - start).Trim();
                    if (s.Length > 0) res.Add(s);
                    start = k + 1;
                    i = k;
                }
                if (start < b.Length)
                {
                    string rest = b.Substring(start).Trim();
                    if (rest.Length > 0) res.Add(rest);
                }
            }
            return JoinShort(SplitLong(res));
        }

        /// The part of a reply that is spoken: at most `maxSentences` sentences and about `maxChars` characters (the
        /// whole reply stays on the HUD). The first sentence is always kept (cut at a word when it alone is far longer).
        public static List<string> ForSpeech(string reply, int maxSentences = 2, int maxChars = 200)
        {
            var all = Sentences(reply);
            var o = new List<string>();
            int used = 0;
            foreach (var s in all)
            {
                if (o.Count >= maxSentences) break;
                if (o.Count > 0 && used + s.Length > maxChars) break;
                o.Add(s);
                used += s.Length + 1;
            }
            if (o.Count == 1 && o[0].Length > maxChars + 20) o[0] = CutAtWord(o[0], maxChars);
            return o;
        }

        private static readonly Regex Units = new Regex(@"(?<![\w.,])(\d+(?:[.,]\d+)?)(\s?)(ms|mins?|secs?|s|hrs?|h)\b(?!['’])", RegexOptions.CultureInvariant);

        /// The text as the voice model reads it well: "3 min 20 s" -> "3 minutes 20 seconds",
        /// "1 h" -> "1 hour", "250 ms" -> "250 milliseconds"; arrows and the reply separator become words / commas.
        public static string Speakable(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = Units.Replace(text, m =>
            {
                string num = m.Groups[1].Value, unit = m.Groups[3].Value.ToLowerInvariant();
                // "the 1990s": a bare s glued to a big number is no unit
                if (unit == "s" && m.Groups[2].Value.Length == 0 && num.Length >= 3) return m.Value;
                bool one = num == "1";
                string word;
                switch (unit)
                {
                    case "ms": word = one ? "millisecond" : "milliseconds"; break;
                    case "min": case "mins": word = one ? "minute" : "minutes"; break;
                    case "h": case "hr": case "hrs": word = one ? "hour" : "hours"; break;
                    default: word = one ? "second" : "seconds"; break;
                }
                return num + " " + word;
            });
            t = t.Replace(" · ", ", ").Replace("·", ", ").Replace(" → ", " to ").Replace(" -> ", " to ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        /// A duration in spoken words: 40 -> "40 seconds", 200 -> "3 minutes 20 seconds", 900 -> "15 minutes",
        /// 5400 -> "1.5 hours".
        public static string Duration(int secs)
        {
            secs = Math.Max(0, secs);
            string U(int n, string en1, string en) => n + " " + (n == 1 ? en1 : en);
            if (secs < 60) return U(secs, "second", "seconds");
            if (secs < 3600)
            {
                string m = U(secs / 60, "minute", "minutes");
                return secs < 600 && secs % 60 >= 10 ? m + " " + U(secs % 60, "second", "seconds") : m;
            }
            double h = secs / 3600.0;
            string hs = h.ToString("0.#", CultureInfo.InvariantCulture);
            return hs + " " + (hs == "1" ? "hour" : "hours");
        }

        // ---- helpers

        private static bool IsEnd(char c) => c == '.' || c == '!' || c == '?' || c == '…';
        private static bool IsCloser(char c) => c == '"' || c == '\'' || c == ')' || c == ']' || c == '”' || c == '’' || c == '»';

        /// Markdown and code out, list items one per line.
        private static string Clean(string text)
        {
            string t = Regex.Replace(text, @"```[\s\S]*?(```|$)", "\n");
            t = Regex.Replace(t, @"\*\*|__|`|^\s*#+\s*|^\s*>\s*", "", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*([-*+•]|\d+[.)])\s+", "", RegexOptions.Multiline);
            return t.Replace("\r", "");
        }

        /// The word in front of the dot at `dot` is an abbreviation ("e.g", "Dr", "No") or an initial ("J").
        private static bool IsAbbreviation(string b, int start, int dot, bool numberNext)
        {
            int w = dot - 1;
            while (w >= start && b[w] != ' ' && b[w] != '(' && b[w] != '"' && b[w] != '“') w--;
            string word = b.Substring(w + 1, dot - w - 1);
            if (word.Length == 0) return false;
            if (word.Length == 1 && char.IsUpper(word[0])) return true; // "J. Smith"
            if (word.Length <= 5 && word.IndexOf('.') > 0) return true; // "U.S", "a.m", "p.m"
            return Abbreviations.Contains(word) || numberNext && NumberAbbreviations.Contains(word);
        }

        /// A first sentence longer than this is cut at its first clause boundary (SplitFirstClause).
        public const int LongFirstSentence = 45;
        /// Each part of such a cut keeps at least this many characters.
        public const int MinClause = 12;

        /// The first clip decides when the user hears anything, so a long first sentence is made in two clips. Cuts
        /// `sentence` (longer than LongFirstSentence) at its first ; : , " - " or " and " that leaves at least MinClause
        /// characters on each side: returns the head and puts the rest in `rest` (it starts with "and" for " and ").
        /// Returns the sentence itself with `rest` null when there is no such place. Never cuts inside backticks,
        /// double quotes or a number ("1,200", "3, 4, 5", "12:30").
        public static string SplitFirstClause(string sentence, out string rest)
        {
            rest = null;
            string s = (sentence ?? "").Trim();
            if (s.Length <= LongFirstSentence) return s;
            bool inCode = false, inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '`') { inCode = !inCode; continue; }
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == '“') { inQuote = true; continue; }
                if (c == '”') { inQuote = false; continue; }
                if (inCode || inQuote) continue;
                int headEnd, tailStart; // the head is s[0..headEnd), the rest s[tailStart..]
                if ((c == ';' || c == ':' || c == ',') && i + 1 < s.Length && s[i + 1] == ' ')
                {
                    if (i > 0 && char.IsDigit(s[i - 1]) && i + 2 < s.Length && char.IsDigit(s[i + 2])) continue; // "3, 4, 5"
                    headEnd = i + 1;
                    tailStart = i + 2;
                }
                else if (c == ' ' && string.CompareOrdinal(s, i, " - ", 0, 3) == 0) { headEnd = i; tailStart = i + 3; }
                else if (c == ' ' && string.Compare(s, i, " and ", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { headEnd = i; tailStart = i + 1; }
                else continue;
                string head = s.Substring(0, headEnd).Trim(), tail = s.Substring(tailStart).Trim();
                if (head.Length < MinClause || tail.Length < MinClause) continue;
                rest = tail;
                return head;
            }
            return s;
        }

        private static List<string> SplitLong(List<string> ss)
        {
            var o = new List<string>();
            foreach (var s0 in ss)
            {
                string s = s0;
                while (s.Length > LongSentence)
                {
                    int cut = -1;
                    for (int i = Math.Min(s.Length - 2, 160); i >= 60; i--)
                        if ((s[i] == ',' || s[i] == ';' || s[i] == ':') && s[i + 1] == ' ') { cut = i; break; }
                    if (cut < 0) break;
                    o.Add(s.Substring(0, cut + 1).Trim());
                    s = s.Substring(cut + 1).Trim();
                }
                if (s.Length > 0) o.Add(s);
            }
            return o;
        }

        private static List<string> JoinShort(List<string> ss)
        {
            var o = new List<string>();
            var pending = new StringBuilder();
            for (int i = 0; i < ss.Count; i++)
            {
                if (pending.Length > 0) pending.Append(' ');
                pending.Append(ss[i]);
                bool last = i == ss.Count - 1;
                if (!last && pending.Length < ShortSentence && pending.Length + 1 + ss[i + 1].Length <= 160) continue;
                o.Add(pending.ToString());
                pending.Clear();
            }
            return o;
        }

        private static string CutAtWord(string s, int max)
        {
            if (s.Length <= max) return s;
            int sp = s.LastIndexOf(' ', Math.Min(max, s.Length - 1));
            string cut = (sp > max / 2 ? s.Substring(0, sp) : s.Substring(0, max)).TrimEnd(',', ';', ':', ' ');
            return cut + "…";
        }
    }
}
