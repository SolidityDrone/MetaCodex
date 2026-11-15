using System;
using System.Collections.Generic;

namespace MetaCodex.AI
{
    /// "Hey Whisp": does a transcript start with the wake phrase, and what was said after it? One tolerant, pure
    /// function (no Unity), checked by the Editor self-test (SelfCheck, section `wake`). The word lists are how
    /// Whisper base writes the phrase and its near misses, all in this one place: add a new spelling here and a case
    /// to Cases. The service's wake check decodes with the prompt "Hey Whisp.", so the usual output is "Hey Whisp."
    public static class WakeWord
    {
        /// The word before the name: "hey" and how it comes out when it is said fast or softly ("hi" and "ok" too).
        public static readonly HashSet<string> HeyWords = new HashSet<string> { "hey", "hay", "hei", "heh", "he", "hi", "hiya", "ey", "ay", "eh", "okay", "ok" };
        /// The name, alone at the very start too ("Whisp, open the terminal").
        public static readonly HashSet<string> NameWords = new HashSet<string> { "whisp", "wisp", "whisps", "wisps" };
        /// Near misses of the name, only right after a word of HeyWords ("with", "whisk" alone start many sentences).
        public static readonly HashSet<string> NearNames = new HashSet<string>
        {
            "whisk", "whisks", "wisk", "whist", "wist", "whisper", "whispers", "wisper", "whispy", "wispy", "whiz", "whizz", "wiz",
            "with", "wish", "wiss", "wis", "whis", "wasp", "wesp", "west", "whip", "whips", "wips",
        };
        /// Words that may come before the phrase ("Oh, hey Whisp"); at most two of them.
        public static readonly HashSet<string> Fillers = new HashSet<string> { "oh", "ok", "okay", "um", "uh", "so", "and", "well", "hm", "hmm", "yeah", "right" };

        private static bool IsName(string w, bool afterHey) => NameWords.Contains(w) || afterHey && NearNames.Contains(w);

        /// True when `text` starts with the wake phrase (up to two fillers before it); `rest` is what was said after it,
        /// as written (a repeated phrase is dropped too: "Hey Whisp. Hey Whisp, open the terminal." -> "open the terminal.").
        public static bool Match(string text, out string rest)
        {
            rest = "";
            if (!MatchOnce(text, out string after)) return false;
            for (int i = 0; i < 3 && MatchOnce(after, out string again); i++) after = again;
            rest = after;
            return true;
        }

        /// The transcript without the wake phrase in front (unchanged when it has none).
        public static string Strip(string text) => Match(text, out string rest) ? rest : (text ?? "");

        private static bool MatchOnce(string text, out string rest)
        {
            rest = "";
            if (string.IsNullOrEmpty(text)) return false;
            var words = Words(text, 5);
            for (int i = 0; i < words.Count && i <= 2; i++)
            {
                if (i > 0 && !Fillers.Contains(words[i - 1].w)) break; // only fillers may come before it
                string w = words[i].w;
                int end = -1;
                // "ok" / "okay" only before the name itself: "okay, with that..." is everyday speech
                if (HeyWords.Contains(w) && i + 1 < words.Count && IsName(words[i + 1].w, w != "ok" && w != "okay")) end = words[i + 1].end;
                else if (i == 0 && NameWords.Contains(w)) end = words[i].end;
                else if (Merged(w)) end = words[i].end; // "Heywisp"
                if (end < 0) continue;
                rest = text.Substring(end).TrimStart(' ', ',', '.', '!', '?', ';', ':', '-', '…', '—').Trim();
                return true;
            }
            return false;
        }

        /// One word that is a hey-word and the name run together ("heywhisp", "hiwisp").
        private static bool Merged(string w)
        {
            if (w.Length < 5) return false;
            foreach (var h in HeyWords)
                if (h.Length >= 2 && w.Length > h.Length + 2 && w.StartsWith(h, StringComparison.Ordinal) && IsName(w.Substring(h.Length), true)) return true;
            return false;
        }

        /// The first `max` words: lower case, apostrophes dropped, with where each one ends in `text`.
        private static List<(string w, int end)> Words(string text, int max)
        {
            var list = new List<(string, int)>();
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < text.Length && list.Count < max)
            {
                while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
                if (i >= text.Length) break;
                sb.Length = 0;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'' || text[i] == '’'))
                {
                    if (text[i] != '\'' && text[i] != '’') sb.Append(char.ToLowerInvariant(text[i]));
                    i++;
                }
                list.Add((sb.ToString(), i));
            }
            return list;
        }

        /// Unit-style cases: (transcript, wakes, rest).
        public static readonly (string text, bool wake, string rest)[] Cases =
        {
            ("Hey Whisp.", true, ""),
            ("Hey, Wisp!", true, ""),
            ("hey whisk", true, ""),
            ("Hey with.", true, ""),
            ("Hey Whispers", true, ""),
            ("Hi Whisp, open the terminal.", true, "open the terminal."),
            ("Hey Whisp, open the terminal and the files.", true, "open the terminal and the files."),
            ("Okay, hey Whisp. What time is it?", true, "What time is it?"),
            ("HeyWisp", true, ""),
            ("Whisp, close all panels", true, "close all panels"),
            ("Hey Whisp. Hey Whisp.", true, ""),
            ("Hey Whisp's here", true, "here"),
            ("OK Whisp", true, ""),
            ("Okay, with that said", false, ""),
            ("Hey, what's up?", false, ""),
            ("With the new update we ship today", false, ""),
            ("Whisper it to me", false, ""),
            ("The wisp is pretty", false, ""),
            ("I said hey whisp yesterday", false, ""),
            ("A wisp of smoke", false, ""),
            ("Hey", false, ""),
            ("", false, ""),
            (null, false, ""),
        };

        /// The cases that fail (empty: all pass).
        public static List<string> SelfCheck()
        {
            var bad = new List<string>();
            foreach (var c in Cases)
            {
                bool got = Match(c.text, out string rest);
                if (got != c.wake) bad.Add($"\"{c.text}\": wake {got}, want {c.wake}");
                else if (got && rest != c.rest) bad.Add($"\"{c.text}\": rest \"{rest}\", want \"{c.rest}\"");
            }
            if (Strip("Hey Whisp, open files") != "open files") bad.Add("Strip(\"Hey Whisp, open files\")");
            if (Strip("open files") != "open files") bad.Add("Strip(\"open files\") changed it");
            return bad;
        }
    }
}
