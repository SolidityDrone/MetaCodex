using System;
using System.Collections.Generic;

namespace MetaCodex.AI
{
    /// "Hey Whisp": does a transcript start with the wake phrase, and what was said after it? Pure functions (no Unity),
    /// checked on the PC (tests/voicefx, WakeTests.cs) and by the Editor self-test (SelfCheck, section `wake`). The word
    /// lists are how the speech model writes the phrase and its near misses, all in this one place: add a new spelling
    /// here and a case to Cases. The service's wake check decodes with the prompt "Hey Whisp.", so the usual output is
    /// "Hey Whisp." or "Hey Whisper."; the full speech-to-text of TALK also writes "a whisp," and "a whisper, ...".
    ///
    /// Two rules, one per use:
    /// - Match (the wake check, overheard speech): a hey-word and a name, or the name itself first. A name that is
    ///   also a common word ("this", "with", "west") counts only when the phrase ends there ("Hey, this." wakes,
    ///   "Hey, this is Tom" does not); "a" only before a name that ends the phrase ("a wisp of smoke" is speech).
    /// - Command (after a wake, or any TALK): the phrase and its mis-hearings dropped from the front, what is left
    ///   is the command; only the phrase (or the noise the model writes after it) leaves nothing.
    public static class WakeWord
    {
        /// The word before the name: "hey" and how it comes out when it is said fast or softly. "ok" / "okay" only
        /// before the name itself; "a" / "ah" only before a name that ends the phrase.
        public static readonly HashSet<string> HeyWords = new HashSet<string> { "hey", "hay", "hei", "heh", "he", "hi", "hiya", "hee", "ey", "ay", "eh", "a", "ah", "okay", "ok" };
        /// The name; it may stand alone at the very start too ("Whisp, open the terminal").
        public static readonly HashSet<string> NameWords = new HashSet<string> { "whisp", "wisp", "whisps", "wisps" };
        /// Near misses of the name that are rare as words: right after a hey-word they are the name. Words one letter
        /// off "whisp" / "wisp" / "whisper" that start with w count too (IsNearName).
        public static readonly HashSet<string> NearNames = new HashSet<string>
        {
            "whisper", "whispers", "wisper", "whispy", "wispy", "whisk", "whisks", "wisk", "whist", "wist", "wis", "whis", "wiss", "wesp",
            "whesp", "wasp", "whip", "whips", "wips", "hwisp", "visp", "whisb", "wispe",
        };
        /// Mis-hearings of the name that are everyday words: only when the phrase ends there (end, or . , ! ? ; :).
        public static readonly HashSet<string> WeakNames = new HashSet<string> { "with", "wish", "wise", "west", "this", "whiz", "whizz", "wiz", "wes", "wits", "wisdom" };
        /// Words that may come before the phrase ("Oh, hey Whisp"); at most two of them.
        public static readonly HashSet<string> Fillers = new HashSet<string> { "oh", "ok", "okay", "um", "uh", "so", "and", "well", "hm", "hmm", "yeah", "right", "hey", "hi" };
        /// What the speech model writes on a breath or silence after the phrase: not a command.
        public static readonly HashSet<string> Noise = new HashSet<string> { "thank you", "thanks", "thank you for watching", "thanks for watching", "you", "bye", "bye bye", "okay", "ok", "oh", "um", "uh", "hmm", "so", "yeah", "mm" };
        /// A command made of one of these after the phrase keeps the phrase ("Hey Whisp off" is set_wake_word off).
        private static readonly HashSet<string> StateWords = new HashSet<string> { "on", "off", "on please", "off please" };

        /// A word that is the name or a near miss of it (not an everyday word).
        public static bool IsNearName(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            if (NameWords.Contains(w) || NearNames.Contains(w)) return true;
            if (WeakNames.Contains(w) || w.Length < 3 || w.Length > 8 || w[0] != 'w') return false;
            return Lev1(w, "whisp") || Lev1(w, "wisp") || Lev1(w, "whisper");
        }

        /// True when `text` starts with the wake phrase (up to two fillers before it); `rest` is what was said after it,
        /// as written. A repeated phrase is dropped too ("Hey Whisp. Hey Whisp, open the terminal." -> "open the
        /// terminal."), and a rest that is only phrase words or the model's noise is empty ("Hey Whisper. Hey Whisper.
        /// Hey" -> "", "Hey Whisp. Thank you." -> "").
        public static bool Match(string text, out string rest)
        {
            rest = "";
            if (!MatchOnce(text, false, out string after)) return false;
            for (int i = 0; i < 4 && MatchOnce(after, false, out string again); i++) after = again;
            rest = IsResidue(after) ? "" : after;
            return true;
        }

        /// The command in a transcript: the wake phrase dropped from its front. `afterWake`: this TALK was started by
        /// the phrase, so a softer rule drops what is left of it ("a whisp", "Hey this", a lone "Hey,", "Whisper,").
        /// Only the phrase (or the model's noise after it) -> "". A state word alone keeps the phrase ("Hey Whisp off").
        public static string Command(string text, bool afterWake)
        {
            string t = (text ?? "").Trim();
            string s = t;
            if (afterWake)
            {
                for (int i = 0; i < 5 && MatchOnce(s, true, out string again); i++) s = again;
                if (IsResidue(s)) return "";
            }
            else
            {
                if (!Match(t, out string rest)) return t; // TALK by the button: only a real phrase in front goes
                s = rest;
                if (s.Length == 0) return "";
            }
            if (s != t && StateWords.Contains(Norm(s))) return t;
            return s;
        }

        /// The transcript without the wake phrase in front (unchanged when it has none); the TALK that the phrase started.
        public static string Strip(string text) => Command(text, true);

        /// The transcript is the wake phrase and nothing else (a TALK that heard it must listen on, never run set_wake_word).
        public static bool IsOnlyPhrase(string text) => !string.IsNullOrWhiteSpace(text) && Command(text, true).Length == 0 && (Match(text, out _) || HasName(text));

        /// A transcript with a word that sounds like the name, that did not wake (the log keeps these: a missed try).
        public static bool NearMiss(string text)
        {
            if (string.IsNullOrEmpty(text) || Match(text, out _)) return false;
            foreach (var x in Words(text, 6)) if (IsNearName(x.w)) return true;
            return false;
        }

        private static bool HasName(string text)
        {
            foreach (var x in Words(text, 8)) if (IsNearName(x.w) || WeakNames.Contains(x.w)) return true;
            return false;
        }

        /// Only phrase words (hey-words, names, fillers) or the model's noise: nothing to run.
        private static bool IsResidue(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            var ws = Words(s, 12);
            if (ws.Count == 0) return true; // punctuation only
            if (Noise.Contains(Norm(s))) return true;
            if (ws.Count > 6) return false;
            foreach (var x in ws)
                if (!HeyWords.Contains(x.w) && !Fillers.Contains(x.w) && !IsNearName(x.w) && !WeakNames.Contains(x.w)) return false;
            return true;
        }

        /// One phrase at the start of `text`. Strict (`soft` false): the wake check's rule above. Soft (after a wake):
        /// a hey-word before any name-like word, a near name alone first, and a lone "hey" first are dropped too.
        private static bool MatchOnce(string text, bool soft, out string rest)
        {
            rest = "";
            if (string.IsNullOrEmpty(text)) return false;
            var words = Words(text, 6);
            for (int i = 0; i < words.Count && i <= 2; i++)
            {
                if (i > 0 && !Fillers.Contains(words[i - 1].w)) break; // only fillers may come before it
                string w = words[i].w;
                int end = -1;
                if (HeyWords.Contains(w) && i + 1 < words.Count && NameAfter(w, words[i + 1].w, EndsPhrase(text, words[i + 1].end), soft)) end = words[i + 1].end;
                else if (i == 0 && (NameWords.Contains(w) || soft && IsNearName(w))) end = words[i].end; // "Whisp, open the terminal"
                else if (Merged(w)) end = words[i].end; // "Heywisp"
                else if (soft && i == 0 && (w == "hey" || w == "hay" || w == "hei" || w == "hi" || w == "hiya")) end = words[i].end; // "Hey, open the threads"
                if (end < 0) continue;
                rest = text.Substring(end).TrimStart(' ', ',', '.', '!', '?', ';', ':', '-', '…', '—').Trim();
                return true;
            }
            return false;
        }

        /// The name after a hey-word `hey`: "ok" only before the name itself, "a" only before a name that ends the
        /// phrase, an everyday word only when the phrase ends there (soft: always).
        private static bool NameAfter(string hey, string w, bool endsHere, bool soft)
        {
            if (hey == "ok" || hey == "okay") return NameWords.Contains(w);
            if (hey == "a" || hey == "ah") return IsNearName(w) && (endsHere || soft);
            if (IsNearName(w)) return true;
            return WeakNames.Contains(w) && (endsHere || soft);
        }

        /// After `end` the phrase ends: nothing more, or a sentence break or comma before the next word.
        private static bool EndsPhrase(string text, int end)
        {
            for (int i = end; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ') continue;
                return c == '.' || c == ',' || c == '!' || c == '?' || c == ';' || c == ':' || c == '…' || c == '—' || c == '-';
            }
            return true;
        }

        /// One word that is a hey-word and the name run together ("heywhisp", "hiwisp").
        private static bool Merged(string w)
        {
            if (w.Length < 5) return false;
            foreach (var h in HeyWords)
                if (h.Length >= 2 && h != "ok" && w.Length > h.Length + 2 && w.StartsWith(h, StringComparison.Ordinal) && IsNearName(w.Substring(h.Length))) return true;
            return false;
        }

        /// a and b differ by at most one letter (changed, added or dropped).
        private static bool Lev1(string a, string b)
        {
            if (a == b) return true;
            int la = a.Length, lb = b.Length;
            if (Math.Abs(la - lb) > 1) return false;
            int i = 0, j = 0, diff = 0;
            while (i < la && j < lb)
            {
                if (a[i] == b[j]) { i++; j++; continue; }
                if (++diff > 1) return false;
                if (la > lb) i++;
                else if (lb > la) j++;
                else { i++; j++; }
            }
            return diff + (la - i) + (lb - j) <= 1;
        }

        /// Lower case, the words only, single spaces ("Thank you." -> "thank you").
        private static string Norm(string s)
        {
            var ws = Words(s, 12);
            var parts = new string[ws.Count];
            for (int i = 0; i < ws.Count; i++) parts[i] = ws[i].w;
            return string.Join(" ", parts);
        }

        /// The first `max` words: lower case, apostrophes dropped, with where each one ends in `text`.
        private static List<(string w, int end)> Words(string text, int max)
        {
            var list = new List<(string, int)>();
            if (string.IsNullOrEmpty(text)) return list;
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

        /// The match cases: (transcript, wakes, rest). The first block is how the speech model wrote the phrase on the
        /// headset (session log 2026-11-16) and its usual mis-hearings; the second is everyday speech that must not wake.
        public static readonly (string text, bool wake, string rest)[] Cases =
        {
            ("Hey Whisp.", true, ""),
            ("Hey, Wisp!", true, ""),
            ("hey whisk", true, ""),
            ("Hey with.", true, ""),
            ("Hey Whispers", true, ""),
            ("Hey Whisper.", true, ""),
            ("Hey Whisper. Hey Whisper. Hey", true, ""),
            ("Hey Whisp. Hey Whisp.", true, ""),
            ("Hey Whisp. Thank you.", true, ""),
            ("a whisp,", true, ""),
            ("A whisper, open the panels.", true, "open the panels."),
            ("Hey wisp", true, ""),
            ("Hey whist.", true, ""),
            ("Hey West.", true, ""),
            ("Hey wis", true, ""),
            ("Hey, this.", true, ""),
            ("Hey wish.", true, ""),
            ("Hey whispy", true, ""),
            ("Hey, Wesp.", true, ""),
            ("He whisp.", true, ""),
            ("Hey hey Whisp", true, ""),
            ("Hey whisk, open the terminal.", true, "open the terminal."),
            ("Hey wisp open the threads", true, "open the threads"),
            ("Hi Whisp, open the terminal.", true, "open the terminal."),
            ("Hey Whisp, open the terminal and the files.", true, "open the terminal and the files."),
            ("Okay, hey Whisp. What time is it?", true, "What time is it?"),
            ("Oh, hey Whisp.", true, ""),
            ("HeyWisp", true, ""),
            ("Heywhisper", true, ""),
            ("Whisp", true, ""),
            ("Whisp, close all panels", true, "close all panels"),
            ("Hey Whisp's here", true, "here"),
            ("OK Whisp", true, ""),
            ("Okay, with that said", false, ""),
            ("Hey, what's up?", false, ""),
            ("With the new update we ship today", false, ""),
            ("Whisper it to me", false, ""),
            ("The wisp is pretty", false, ""),
            ("I said hey whisp yesterday", false, ""),
            ("A wisp of smoke", false, ""),
            ("A whisper of wind came through the window", false, ""),
            ("Hey, this is my friend Tom.", false, ""),
            ("Hey this is great", false, ""),
            ("Hey, wish you were here", false, ""),
            ("Hey with the music on", false, ""),
            ("Hey, west coast is better", false, ""),
            ("Hey, wise guy.", false, ""),
            ("Hey, which one?", false, ""),
            ("This is it.", false, ""),
            ("Wish me luck.", false, ""),
            ("Whisk the eggs.", false, ""),
            ("Whiskey, please.", false, ""),
            ("West.", false, ""),
            ("He wished for more.", false, ""),
            ("He said this.", false, ""),
            ("A wish.", false, ""),
            ("Okay, this is fine", false, ""),
            ("Hey you.", false, ""),
            ("Hey there.", false, ""),
            ("Hi, how are you?", false, ""),
            ("Okay.", false, ""),
            ("Hey", false, ""),
            ("", false, ""),
            (null, false, ""),
        };

        /// The command cases: (transcript, after a wake, command).
        public static readonly (string text, bool afterWake, string command)[] CommandCases =
        {
            ("a whisp,", true, ""),
            ("a whisper, open the panels", true, "open the panels"),
            ("Hey Whisp, open the terminal.", true, "open the terminal."),
            ("Hey this open the panels", true, "open the panels"),
            ("Hey, open the threads", true, "open the threads"),
            ("Whisper, close all panels", true, "close all panels"),
            ("Wisp, open settings", true, "open settings"),
            ("Okay, hey whisp, what time is it", true, "what time is it"),
            ("Hey Whisper. Hey Whisper. Hey", true, ""),
            ("Hey Whisp. Thank you.", true, ""),
            ("Hey Whisp, turn off hey whisp", true, "turn off hey whisp"),
            ("Hey Whisp off", true, "Hey Whisp off"),
            ("Open the threads", true, "Open the threads"),
            ("This panel should close", true, "This panel should close"),
            ("With the files open", true, "With the files open"),
            ("A new terminal", true, "A new terminal"),
            ("Thank you.", true, ""),
            ("Hey Whisp, open the panels", false, "open the panels"),
            ("a whisper, open the panels", false, "open the panels"),
            ("A wisp of smoke", false, "A wisp of smoke"),
            ("hey whisp off", false, "hey whisp off"),
            ("open files", false, "open files"),
            ("Okay.", false, "Okay."),
            ("a whisp", false, ""),
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
            foreach (var c in CommandCases)
            {
                string got = Command(c.text, c.afterWake);
                if (got != c.command) bad.Add($"Command(\"{c.text}\", {(c.afterWake ? "after a wake" : "TALK")}): \"{got}\", want \"{c.command}\"");
            }
            if (Strip("Hey Whisp, open files") != "open files") bad.Add("Strip(\"Hey Whisp, open files\")");
            if (Strip("open files") != "open files") bad.Add("Strip(\"open files\") changed it");
            if (!IsOnlyPhrase("a whisp,") || !IsOnlyPhrase("Hey Whisper.") || IsOnlyPhrase("Hey Whisp, open files") || IsOnlyPhrase("open files") || IsOnlyPhrase("") || IsOnlyPhrase("Thank you."))
                bad.Add("IsOnlyPhrase");
            if (!NearMiss("Hello whisp") || NearMiss("Hey, what's up?") || NearMiss("Hey Whisp.")) bad.Add("NearMiss");
            return bad;
        }
    }
}
