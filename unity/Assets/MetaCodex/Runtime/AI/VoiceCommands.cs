using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    /// One thing the user can ask for by voice, MCP-style: a name, a one-line description and a JSON schema for
    /// its arguments (what the LLM sees as a tool), plus spoken phrase templates for the fast path, which skips
    /// the LLM. Templates: words, `(a|b)` one of, `[a|b]` optional, `{param}` captures words for a parameter.
    /// Example: "open [the] {panel} [panel|page]" matches "open the git page" with panel = "git".
    public sealed class VoiceCommand
    {
        public string Name;
        public string Description = "";
        public JObject Parameters = new JObject { ["type"] = "object", ["properties"] = new JObject() };
        public string[] Phrases = new string[0];
        /// Spoken words for enum values, per parameter: {"panel": {"providers": "agents"}}.
        public Dictionary<string, Dictionary<string, string>> Synonyms = new Dictionary<string, Dictionary<string, string>>();
        /// Who registered it: "shell", "plugin/<id>", "script/<name>", "mqh/<plugin>".
        public string Owner = "shell";
        /// Ask in VR before running (commands with side effects you cannot undo, e.g. shell commands).
        public bool Confirm;
        public bool Enabled = true;
        /// The reply is an answer worth hearing ("It's 3 pm"), not a confirmation of an action ("Opened GIT"):
        /// only these are spoken (confirmations come late and annoy; the HUD and a click show them at once).
        public bool SpeakReply;
        /// Offered to the local LLM (off for commands that take free text verbatim: typing, shell commands;
        /// their spoken phrases still work).
        public bool Llm = true;
        /// Example requests and the command line they mean ("make it orange" -> "set_accent amber"): they teach
        /// the small LLM better than descriptions do.
        public List<(string said, string line)> Examples = new List<(string, string)>();
        /// Words this command needs heard right (speech-to-text prompt) and fixed when misheard ("fogus" -> "focus").
        public string[] Vocabulary = new string[0];
        /// More example requests, any language ("accendi il focus ambientale"): the decision model (VoiceDecider) compares
        /// what was said with them; not shown to the LLM (its prompt stays short). The more natural ways to ask, the better.
        public string[] Samples = new string[0];
        /// Runs on the main thread with validated arguments; returns a short reply (shown, optionally spoken),
        /// null for none, or "error: ..." when it failed.
        public Func<JObject, string> Run;
        /// For commands that wait on something (MQH): call `done(reply)` once, within ~15 s. Used instead of Run.
        public Action<JObject, Action<string>> RunAsync;
        /// Optional: whether matched arguments make sense together (null = yes, else why not). A phrase whose words
        /// fell into the wrong slots ("set the dome | radius to two meters") is skipped for the next phrase.
        public Func<JObject, string> Check;
        internal List<(Regex rx, Dictionary<string, string> constants)> Compiled = new List<(Regex, Dictionary<string, string>)>();

        public IEnumerable<string> Required => (Parameters["required"] as JArray)?.Select(x => (string)x) ?? Enumerable.Empty<string>();
        public JObject Properties => Parameters["properties"] as JObject ?? new JObject();
    }

    public sealed class VoiceMatch
    {
        /// The words after correction, when the match needed it ("open ambient fogus" -> "... focus").
        public string Corrected;
        public VoiceCommand Command;
        public JObject Args = new JObject();
        /// "fast" (a phrase template matched) or "llm" (the model picked the tool).
        public string Via;
    }

    /// The registry of voice commands (shell built-ins, Lua plugins, later MQH tools). Spoken text first goes
    /// through the fast path (phrase templates); only what no template matches goes to the local LLM, which
    /// sees every enabled command as an OpenAI-style tool and answers with one tool call.
    public sealed class VoiceCommands
    {
        private readonly List<VoiceCommand> _all = new List<VoiceCommand>();
        public IReadOnlyList<VoiceCommand> All => _all;
        /// Bumped on every change (the shell re-warms the LLM's prompt cache when it moves).
        public int Revision { get; private set; }
        public event Action Changed;

        private static readonly Regex NameRx = new Regex("^[a-z][a-z0-9_]{1,47}$");

        public void Register(VoiceCommand c)
        {
            if (c == null || string.IsNullOrEmpty(c.Name) || !NameRx.IsMatch(c.Name)) throw new ArgumentException("command name must be snake_case: " + c?.Name);
            if (c.Run == null && c.RunAsync == null) throw new ArgumentException("command " + c.Name + " has no run function");
            if (c.Parameters == null) c.Parameters = new JObject { ["type"] = "object", ["properties"] = new JObject() };
            if (c.Parameters["type"] == null) c.Parameters["type"] = "object";
            if (c.Parameters["properties"] == null) c.Parameters["properties"] = new JObject();
            c.Compiled = new List<(Regex, Dictionary<string, string>)>();
            foreach (var p in c.Phrases ?? new string[0])
            {
                try { var consts = new Dictionary<string, string>(); c.Compiled.Add((CompilePhrase(p, consts, SlotChoices(c)), consts)); }
                catch (Exception ex) { throw new ArgumentException($"phrase '{p}' of {c.Name}: {ex.Message}"); }
            }
            _all.RemoveAll(x => x.Name == c.Name);
            _all.Add(c);
            Bump();
        }

        public int RemoveOwnedBy(string owner)
        {
            int n = _all.RemoveAll(c => c.Owner == owner);
            if (n > 0) Bump();
            return n;
        }

        public VoiceCommand Find(string name) => _all.FirstOrDefault(c => c.Name == name);

        /// Removes one command if `owner` registered it.
        public bool Remove(string name, string owner)
        {
            int n = _all.RemoveAll(c => c.Name == name && c.Owner == owner);
            if (n > 0) Bump();
            return n > 0;
        }

        public void SetEnabled(string name, bool on)
        {
            var c = Find(name);
            if (c == null || c.Enabled == on) return;
            c.Enabled = on;
            Bump();
        }

        private void Bump()
        {
            Revision++;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ fast path

        private static readonly string[] LeadingFillers =
        {
            "hey metacodex", "ok metacodex", "okay metacodex", "metacodex", "hey", "ok", "okay", "so", "um", "uh", "please",
            "can you", "could you", "would you", "will you", "i want to", "i would like to", "id like to", "i would like", "id like", "i want", "lets", "now",
        };
        private static readonly string[] TrailingFillers = { "please", "for me", "now", "thanks", "thank you", "right now" };

        /// Lower case, letters and digits only (apostrophes dropped: "provider's" -> "providers"), single spaces.
        public static string Simplify(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text.ToLowerInvariant())
            {
                if (ch == '\'' || ch == '\u2019') continue;
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// Simplify, then polite fillers trimmed ("hey, could you open git please" -> "open git").
        public static string Normalize(string text)
        {
            string s = Simplify(text);
            for (bool again = true; again;)
            {
                again = false;
                foreach (var f in LeadingFillers)
                    if (s == f) { s = ""; again = false; break; }
                    else if (s.StartsWith(f + " ")) { s = s.Substring(f.Length + 1); again = true; }
                foreach (var f in TrailingFillers)
                    if (s.EndsWith(" " + f)) { s = s.Substring(0, s.Length - f.Length - 1); again = true; }
            }
            return s;
        }

        /// The first enabled command whose phrase matches and whose captured values fit the schema; when nothing
        /// matches as heard, the words are corrected against the commands' vocabulary and tried again.
        public VoiceMatch FastMatch(string text)
        {
            string n = Normalize(text);
            if (n.Length == 0) return null;
            var m = MatchNormalized(n);
            if (m != null) return m;
            string fixedText = Correct(n);
            if (fixedText != n && (m = MatchNormalized(fixedText)) != null) m.Corrected = fixedText;
            return m;
        }

        private static readonly Regex Joiners = new Regex(@"\s*(?:,\s*)?\b(?:and then|and also|and|then|also)\b\s*|\s*[,;]\s*", RegexOptions.Compiled);

        /// One command, or several said in one go ("turn on ambient focus and set the intensity to zero"): each part
        /// must match on its own. null when any part does not (the LLM then reads the whole sentence).
        public List<VoiceMatch> FastMatchAll(string text)
        {
            var one = FastMatch(text);
            if (one != null) return new List<VoiceMatch> { one };
            string n = Normalize(text);
            var parts = Joiners.Split(n).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count < 2 || parts.Count > 4) return null;
            var list = new List<VoiceMatch>();
            foreach (var p in parts)
            {
                var m = FastMatch(p);
                if (m == null) return null;
                list.Add(m);
            }
            return list;
        }

        private int _vocabRevision = -1;
        private HashSet<string> _vocab = new HashSet<string>();

        /// Every word the commands use: their phrases, synonyms, enum values and declared vocabulary.
        public HashSet<string> VocabularyWords()
        {
            if (_vocabRevision == Revision) return _vocab;
            var v = new HashSet<string>();
            void AddText(string s) { foreach (var w in Simplify(s).Split(' ')) if (w.Length > 0) v.Add(w); }
            foreach (var c in _all)
            {
                foreach (var ph in c.Phrases) AddText(Regex.Replace(ph, @"\{[^}]*\}", " ").Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' ').Replace('|', ' '));
                foreach (var syn in c.Synonyms.Values) foreach (var kv in syn) { AddText(kv.Key); AddText(kv.Value); }
                foreach (var p in c.Properties.Properties()) if (p.Value["enum"] is JArray en) foreach (var e in en) AddText((string)e);
                foreach (var w in c.Vocabulary ?? new string[0]) AddText(w);
            }
            _vocab = v;
            _vocabRevision = Revision;
            return v;
        }

        /// Misheard words replaced by the nearest command word (edit distance 1 for 4-6 letters, 2 from 7).
        public string Correct(string normalized)
        {
            var vocab = VocabularyWords();
            var words = normalized.Split(' ');
            bool changed = false;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length < 4 || vocab.Contains(w) || char.IsDigit(w[0])) continue;
                int limit = w.Length >= 7 ? 2 : 1;
                string best = null;
                int bestD = limit + 1;
                foreach (var cand in vocab)
                {
                    if (Math.Abs(cand.Length - w.Length) > limit || cand.Length < 4) continue;
                    int d = Levenshtein(w, cand);
                    if (d < bestD) { bestD = d; best = cand; }
                    else if (d == bestD && best != null && string.CompareOrdinal(cand, best) < 0) best = cand;
                }
                if (best != null && bestD <= limit) { words[i] = best; changed = true; }
            }
            return changed ? string.Join(" ", words) : normalized;
        }

        private VoiceMatch MatchNormalized(string n)
        {
            string subject = n + " ";
            foreach (var c in _all)
            {
                if (!c.Enabled) continue;
                foreach (var (rx, consts) in c.Compiled)
                {
                    var m = rx.Match(subject);
                    if (!m.Success) continue;
                    var args = new JObject();
                    foreach (var kv in consts) args[kv.Key] = kv.Value;
                    bool ok = true;
                    foreach (var g in rx.GetGroupNames())
                    {
                        if (char.IsDigit(g[0]) || !m.Groups[g].Success) continue;
                        string raw = m.Groups[g].Value.Trim();
                        var v = Coerce(c, g, raw);
                        if (v == null) { ok = false; break; }
                        args[g] = v;
                    }
                    if (ok && c.Required.All(r => args[r] != null) && (c.Check == null || c.Check(args) == null)) return new VoiceMatch { Command = c, Args = args, Via = "fast" };
                }
            }
            return null;
        }

        /// The words each enum parameter is known by (its values and synonyms), longest first.
        private static Dictionary<string, string[]> SlotChoices(VoiceCommand c)
        {
            var d = new Dictionary<string, string[]>();
            foreach (var p in c.Properties.Properties())
            {
                if (!(p.Value["enum"] is JArray en) || en.Count == 0) continue;
                var words = new HashSet<string>(en.Select(x => Simplify((string)x)));
                if (c.Synonyms != null && c.Synonyms.TryGetValue(p.Name, out var syn)) foreach (var k in syn.Keys) words.Add(Simplify(k));
                d[p.Name] = words.Where(w => w.Length > 0).OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).ToArray();
            }
            return d;
        }

        /// Template -> anchored regex over normalized text with one trailing space. `{param:value}` sets a fixed
        /// value instead of capturing ("(make|let) the panels follow me {mode:float}"). An enum parameter's slot tries
        /// its known words first, longest first ("dome radius" before "dome"), then any words (for fuzzy matching).
        public static Regex CompilePhrase(string template, Dictionary<string, string> constants = null, Dictionary<string, string[]> choices = null)
        {
            var sb = new StringBuilder("^");
            foreach (var tok in Tokens(template))
            {
                if (tok.StartsWith("{") && tok.Contains(":"))
                {
                    var kv = tok.Trim('{', '}').Split(new[] { ':' }, 2);
                    if (constants != null) constants[kv[0].Trim()] = kv[1].Trim();
                    continue;
                }
                if (tok.StartsWith("{"))
                {
                    string name = tok.Trim('{', '}').Trim();
                    if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("bad parameter name " + name);
                    sb.Append("(?<").Append(name).Append('>');
                    if (choices != null && choices.TryGetValue(name, out var known) && known.Length > 0)
                        sb.Append(string.Join("|", known.Select(k => Regex.Escape(k).Replace("\\ ", " ")))).Append('|');
                    sb.Append(".+?) ");
                }
                else if (tok.StartsWith("[")) sb.Append("(?:(?:").Append(Alternatives(tok.Substring(1, tok.Length - 2))).Append(") )?");
                else if (tok.StartsWith("(")) sb.Append("(?:").Append(Alternatives(tok.Substring(1, tok.Length - 2))).Append(") ");
                else
                {
                    string w = Simplify(tok);
                    if (w.Length > 0) sb.Append(Regex.Escape(w).Replace("\\ ", " ")).Append(' ');
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        }

        private static string Alternatives(string inner) =>
            string.Join("|", inner.Split('|').Select(a => Regex.Escape(Simplify(a)).Replace("\\ ", " ")).Where(a => a.Length > 0));

        /// A readable example of a template: the first word of each (a|b), optional [..] dropped, {p} as <p>.
        public static string Example(string template)
        {
            var parts = new List<string>();
            foreach (var tok in Tokens(template))
            {
                if (tok.StartsWith("[") || tok.StartsWith("{") && tok.Contains(":")) continue;
                if (tok.StartsWith("(")) parts.Add(tok.Substring(1, tok.Length - 2).Split('|')[0]);
                else if (tok.StartsWith("{")) parts.Add("<" + tok.Trim('{', '}') + ">");
                else parts.Add(tok);
            }
            return string.Join(" ", parts.Where(x => x.Length > 0));
        }

        /// Splits a template on spaces, keeping (..), [..] and {..} groups whole.
        internal static IEnumerable<string> TemplateTokens(string template) => Tokens(template);

        private static IEnumerable<string> Tokens(string template)
        {
            var cur = new StringBuilder();
            int depth = 0;
            foreach (char ch in template ?? "")
            {
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                if (ch == ')' || ch == ']' || ch == '}') depth--;
                if (ch == ' ' && depth == 0) { if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); } continue; }
                cur.Append(ch);
            }
            if (depth != 0) throw new ArgumentException("unbalanced brackets");
            if (cur.Length > 0) yield return cur.ToString();
        }

        // ------------------------------------------------------------------ values

        /// A spoken or model-given value -> the parameter's type: enum values through synonyms and fuzzy
        /// matching of the whole phrase, numbers parsed. null = does not fit.
        public static JToken Coerce(VoiceCommand c, string param, string raw)
        {
            var prop = c.Properties[param] as JObject;
            if (prop == null) return raw;
            string type = (string)prop["type"] ?? "string";
            if (prop["enum"] is JArray en && en.Count > 0)
            {
                string mapped = MapEnum(c, param, raw, en.Select(x => (string)x).ToArray());
                return mapped == null ? null : (JToken)mapped; // (JToken)(string)null would be a JSON null, not "no fit"
            }
            if (type == "number" || type == "integer")
            {
                double? min = prop["minimum"]?.Type == JTokenType.Float || prop["minimum"]?.Type == JTokenType.Integer ? (double?)prop["minimum"] : null;
                double? max = prop["maximum"]?.Type == JTokenType.Float || prop["maximum"]?.Type == JTokenType.Integer ? (double?)prop["maximum"] : null;
                if (!TryNumber(raw, min, max, (string)prop["x-unit"], out double d)) return null;
                return type == "integer" ? (JToken)(long)Math.Round(d) : d;
            }
            if (type == "boolean")
            {
                string r = Normalize(raw);
                if (r == "on" || r == "yes" || r == "true" || r == "enable" || r == "enabled") return true;
                if (r == "off" || r == "no" || r == "false" || r == "disable" || r == "disabled") return false;
                return null;
            }
            return raw.Trim().Length == 0 ? null : raw.Trim();
        }

        private static readonly string[] Units = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

        /// A spoken amount -> a number in [min, max]: "0.5", "0,5", "50 %", "fifty percent", "twenty five", "two point
        /// five", "half", "a quarter", "max", "minimum", "1.5 meters", "30 cm". Percent of a 0..1 range is /100; cm
        /// for a metre setting is /100. false when it is not a number.
        public static bool TryNumber(string raw, double? min, double? max, string unit, out double value)
        {
            value = 0;
            string s = VoiceDecider.Fold((raw ?? "").Replace("%", " percent ").Replace(",", "."));
            if (s.Length == 0) return false;
            bool percent = Regex.IsMatch(s, @"\b(percent|per cent|pc|per cento|percento)\b");
            bool cm = Regex.IsMatch(s, @"\b(cm|centimeters?|centimetres?|centimetri|centimetro)\b");
            bool metres = Regex.IsMatch(s, @"\b(m|meters?|metres?|metri|metro)\b");
            s = Regex.Replace(s, @"\b(percent|per cent|per cento|percento|pc|meters?|metres?|metri|metro|m|cm|centimeters?|centimetres?|centimetri|centimetro|degrees?|seconds?|units?)\b", " ").Trim();
            s = Regex.Replace(s, @"\b(to|at|of|the|a|an|by|al|a|il|la|un|una)\b", " ").Trim();
            s = Regex.Replace(s, @"\s+", " ");
            if (s == "max" || s == "maximum" || s == "full" || s == "all way" || s == "highest" || s == "top" || s == "massimo" || s == "completely" || s == "completamente") { if (max == null) return false; value = max.Value; return true; }
            if (s == "min" || s == "minimum" || s == "lowest" || s == "bottom" || s == "nothing" || s == "none" || s == "minimo") { if (min == null) return false; value = min.Value; return true; }
            // "half a metre" is 0.5 m; "half" alone is the middle of the range
            if ((s == "half" || s == "mezzo" || s == "meta") && metres) { value = 0.5; return true; }
            if (s == "half" || s == "meta") { value = min != null && max != null ? (min.Value + max.Value) / 2 : 0.5; return true; }
            if (s == "quarto") s = "quarter";
            if (s == "quarter") { value = min != null && max != null ? min.Value + (max.Value - min.Value) / 4 : 0.25; return true; }
            // digits ("0.5" or "0 5" after Simplify split the point)
            var dm = Regex.Match(s, @"^(\d+)(?:(?:\.| | point )(\d+))?$");
            double v;
            if (dm.Success) v = double.Parse(dm.Groups[1].Value + (dm.Groups[2].Success ? "." + dm.Groups[2].Value : ""), System.Globalization.CultureInfo.InvariantCulture);
            else if (!TryWords(s, out v) && !TryNumberWords(s, out v)) return false;
            if (percent && max != null && max.Value <= 1.0) v /= 100.0;
            if (cm && unit == "m") v /= 100.0;
            if (min != null) v = Math.Max(min.Value, v);
            if (max != null) v = Math.Min(max.Value, v);
            value = v;
            return true;
        }

        private static readonly Dictionary<string, int> ItalianNumbers = new Dictionary<string, int>
        {
            ["zero"] = 0, ["uno"] = 1, ["un"] = 1, ["una"] = 1, ["due"] = 2, ["tre"] = 3, ["quattro"] = 4, ["cinque"] = 5, ["sei"] = 6, ["sette"] = 7,
            ["otto"] = 8, ["nove"] = 9, ["dieci"] = 10, ["undici"] = 11, ["dodici"] = 12, ["quindici"] = 15, ["venti"] = 20, ["trenta"] = 30, ["quaranta"] = 40,
            ["cinquanta"] = 50, ["sessanta"] = 60, ["settanta"] = 70, ["ottanta"] = 80, ["novanta"] = 90, ["cento"] = 100,
        };

        /// The first group of number words in a text, English or Italian ("set it to twenty five percent" -> 25,
        /// "al cinquanta per cento" -> 50, "ottantacinque" -> 85). false when there is none.
        public static bool TryNumberWords(string text, out double v)
        {
            v = 0;
            long total = 0;
            bool any = false;
            foreach (var w in (text ?? "").Split(' '))
            {
                if (w.Length == 0) continue;
                int u = Array.IndexOf(Units, w), t = Array.IndexOf(Tens, w);
                long add = -1;
                if (u >= 0) add = u;
                else if (t >= 2) add = t * 10;
                else if (w == "hundred") { total = (total == 0 ? 1 : total) * 100; any = true; continue; }
                else if (w != "un" && w != "una" && ItalianNumbers.TryGetValue(w, out int it)) add = it;
                else
                {
                    var m = Regex.Match(w, @"^(venti|trenta|quaranta|cinquanta|sessanta|settanta|ottanta|novanta)(uno|due|tre|quattro|cinque|sei|sette|otto|nove)$");
                    if (m.Success) add = ItalianNumbers[m.Groups[1].Value] + ItalianNumbers[m.Groups[2].Value];
                }
                if (add >= 0) { total += add; any = true; }
                else if (any) break; // the number words ended
            }
            v = total;
            return any;
        }

        /// "twenty five", "two point five", "one hundred" -> the number.
        private static bool TryWords(string s, out double v)
        {
            v = 0;
            var parts = s.Split(new[] { " point " }, StringSplitOptions.None);
            if (!TryInt(parts[0], out long whole)) return false;
            v = whole;
            if (parts.Length == 2)
            {
                var frac = new StringBuilder();
                foreach (var w in parts[1].Split(' '))
                {
                    int k = Array.IndexOf(Units, w);
                    if (k < 0 || k > 9) return false;
                    frac.Append(k);
                }
                v = double.Parse(whole + "." + frac, System.Globalization.CultureInfo.InvariantCulture);
            }
            return true;
        }

        private static bool TryInt(string s, out long n)
        {
            n = 0;
            long cur = 0;
            bool any = false;
            foreach (var w in s.Split(' '))
            {
                if (w.Length == 0) continue;
                int u = Array.IndexOf(Units, w), t = Array.IndexOf(Tens, w);
                if (u >= 0) cur += u;
                else if (t >= 2) cur += t * 10;
                else if (w == "hundred") cur = (cur == 0 ? 1 : cur) * 100;
                else if (char.IsDigit(w[0]) && long.TryParse(w, out long d)) cur += d;
                else return false;
                any = true;
            }
            n = cur;
            return any;
        }

        public static string MapEnum(VoiceCommand c, string param, string raw, string[] values)
        {
            string r = Normalize(raw);
            r = Regex.Replace(r, "^(the|my|a|an) ", "");
            if (r.Length == 0) return null;
            if (c.Synonyms != null && c.Synonyms.TryGetValue(param, out var syn) && syn.TryGetValue(r, out var mapped)) return mapped;
            foreach (var v in values) if (string.Equals(v, r, StringComparison.OrdinalIgnoreCase)) return v;
            foreach (var v in values) if (string.Equals(v, r + "s", StringComparison.OrdinalIgnoreCase) || string.Equals(v + "s", r, StringComparison.OrdinalIgnoreCase)) return v;
            if (c.Synonyms != null && c.Synonyms.TryGetValue(param, out syn))
                foreach (var kv in syn) if (r.Length >= 4 && Levenshtein(r, kv.Key) <= Math.Max(1, kv.Key.Length / 4)) return kv.Value;
            string best = null;
            int bestD = int.MaxValue;
            foreach (var v in values)
            {
                int d = Levenshtein(r, v.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = v; }
            }
            return best != null && r.Length >= 3 && bestD <= Math.Max(1, best.Length / 4) ? best : null;
        }

        public static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// Checks and canonicalises arguments the LLM produced: enum values mapped, required ones present.
        /// Returns null when fine, else the problem.
        public string Validate(VoiceCommand c, JObject args)
        {
            foreach (var r in c.Required)
                if (args[r] == null || args[r].Type == JTokenType.Null || args[r].Type == JTokenType.String && ((string)args[r]).Trim().Length == 0)
                    return $"{c.Name} needs '{r}'";
            foreach (var p in args.Properties().ToList())
            {
                if (c.Properties[p.Name] == null) { p.Remove(); continue; } // the model invented it
                if (p.Value.Type == JTokenType.Null) { p.Remove(); continue; }
                var v = Coerce(c, p.Name, p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString());
                if (v == null) return $"'{p.Value}' is not a valid {p.Name} for {c.Name}";
                args[p.Name] = v;
            }
            return c.Check?.Invoke(args);
        }

        // ------------------------------------------------------------------ for the LLM: one command line

        /// "open_panel PANEL", "set_layout fixed|float": how a command is written on its line (positional arguments
        /// in the schema's order; small enums spelled out).
        public static string Usage(VoiceCommand c)
        {
            var sb = new StringBuilder(c.Name);
            foreach (var p in c.Properties.Properties())
            {
                var en = (p.Value["enum"] as JArray)?.Select(x => (string)x).ToArray();
                sb.Append(' ').Append(en != null && en.Length <= 3 ? string.Join("|", en) : p.Name.ToUpperInvariant());
            }
            return sb.ToString();
        }

        /// The system prompt: every LLM command, the shell's legend for its own values, plugin commands described.
        public string LlmSystem(string shellLegend)
        {
            var cmds = _all.Where(c => c.Enabled && c.Llm).ToList();
            var sb = new StringBuilder("You turn a request to MetaCodex, a VR desktop, into one command line. Commands: ");
            sb.Append(string.Join(", ", cmds.Where(c => c.Owner == "shell").Select(Usage))).Append(", none. ");
            sb.Append(shellLegend);
            var more = cmds.Where(c => c.Owner != "shell").ToList();
            if (more.Count > 0)
            {
                sb.Append(" More commands:");
                foreach (var c in more)
                {
                    sb.Append(' ').Append(Usage(c)).Append(" (").Append((c.Description ?? "").TrimEnd('.'));
                    foreach (var p in c.Properties.Properties())
                        if (p.Value["enum"] is JArray en && en.Count > 3) sb.Append("; ").Append(p.Name.ToUpperInvariant()).Append(": ").Append(string.Join(", ", en.Select(x => (string)x)));
                    sb.Append(").");
                }
            }
            return sb.ToString();
        }

        /// General example turns (the shell's, in a measured order, including "none" for small talk).
        public readonly List<(string said, string line)> GeneralExamples = new List<(string, string)>();

        /// Example turns (request -> command line): the general ones, then those of LLM commands.
        public IEnumerable<(string said, string line)> LlmExamples() =>
            GeneralExamples.Concat(_all.Where(c => c.Enabled && c.Llm).SelectMany(c => c.Examples));

        /// A GBNF grammar that only lets the model write a valid command line (or "none"): 1-6 tokens to decode
        /// instead of ~20 for a JSON tool call.
        public string LlmGrammar()
        {
            var alts = new List<string>();
            var rules = new StringBuilder();
            int n = 0;
            foreach (var c in _all.Where(c => c.Enabled && c.Llm))
            {
                var parts = new List<string> { Lit(c.Name) };
                var req = new HashSet<string>(c.Required);
                var props = c.Properties.Properties().ToList();
                foreach (var p in props)
                {
                    string rule = "v" + (n++);
                    var en = (p.Value["enum"] as JArray)?.Select(x => (string)x).ToArray();
                    rules.Append(rule).Append(" ::= ").Append(en != null ? string.Join(" | ", en.Select(Lit)) : "[^\\n;]+").Append('\n'); // GBNF: any text up to the end of the line or the next ";"
                    parts.Add("\" \" " + rule);
                }
                alts.Add(string.Join(" ", parts));
                // optional trailing arguments may be left out
                for (int k = props.Count - 1; k >= 0 && !req.Contains(props[k].Name); k--) alts.Add(string.Join(" ", parts.Take(k + 1)));
            }
            // up to three commands in one answer ("set_focus on ; set_setting focus_intensity 0"), or none
            return "root ::= cmd (\" ; \" cmd)? (\" ; \" cmd)? | \"none\"\ncmd ::= " + string.Join(" | ", alts.Distinct()) + "\n" + rules;
        }

        private static string Lit(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// The model's answer -> its commands (one per " ; "); null with an error when any part is unusable.
        public List<VoiceMatch> ParseLines(string text, out string error)
        {
            error = null;
            string first = (text ?? "").Trim();
            int nl = first.IndexOf('\n');
            if (nl >= 0) first = first.Substring(0, nl);
            var list = new List<VoiceMatch>();
            foreach (var part in first.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).Take(3))
            {
                var m = ParseLine(part, out error);
                if (m == null) return null;
                list.Add(m);
            }
            if (list.Count == 0) { error = "no command for that"; return null; }
            return list;
        }

        /// The model's line -> a command and its arguments (null for "none" or nothing usable).
        public VoiceMatch ParseLine(string line, out string error)
        {
            error = null;
            line = (line ?? "").Trim().Trim('`', '"', '\'').Trim();
            int nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl).Trim();
            if (line.Length == 0 || line == "none") { error = "no command for that"; return null; }
            int sp = line.IndexOf(' ');
            string name = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            var c = Find(name);
            if (c == null || !c.Enabled) { error = "the model wrote an unknown command: " + name; return null; }
            var args = new JObject();
            var props = c.Properties.Properties().ToList();
            for (int i = 0; i < props.Count && rest.Length > 0; i++)
            {
                bool last = i == props.Count - 1;
                bool isEnum = props[i].Value["enum"] is JArray;
                string value;
                if (last || !isEnum) { value = rest; rest = ""; }
                else { int s2 = rest.IndexOf(' '); value = s2 < 0 ? rest : rest.Substring(0, s2); rest = s2 < 0 ? "" : rest.Substring(s2 + 1).Trim(); }
                args[props[i].Name] = value;
            }
            return new VoiceMatch { Command = c, Args = args, Via = "llm" };
        }

        /// The registry as JSON: what agents, plugins (voice.registry()), the control port (`voice registry`) and the voice
        /// bench read. Every enabled command with all it carries: schema (enum values, ranges, units), spoken names per
        /// value, phrase templates, examples, samples, vocabulary, flags, and the lint warnings it still has. Disabled and
        /// removed commands are not in it, so nothing offers them.
        public JObject Describe()
        {
            var cmds = new JArray();
            foreach (var c in _all.Where(c => c.Enabled).OrderBy(c => c.Owner == "shell" ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
                cmds.Add(DescribeCommand(c));
            return new JObject
            {
                ["revision"] = Revision,
                ["commands"] = cmds,
                ["general_examples"] = new JArray(GeneralExamples.Select(e => new JArray(e.said, e.line))),
            };
        }

        public static JObject DescribeCommand(VoiceCommand c) => new JObject
        {
            ["name"] = c.Name, ["description"] = c.Description ?? "", ["owner"] = c.Owner, ["confirm"] = c.Confirm, ["llm"] = c.Llm, ["speak"] = c.SpeakReply,
            ["parameters"] = c.Parameters.DeepClone(),
            ["synonyms"] = JObject.FromObject(c.Synonyms ?? new Dictionary<string, Dictionary<string, string>>()),
            ["phrases"] = new JArray(c.Phrases ?? new string[0]),
            ["examples"] = new JArray((c.Examples ?? new List<(string, string)>()).Select(e => new JArray(e.said, e.line))),
            ["samples"] = new JArray(c.Samples ?? new string[0]),
            ["vocabulary"] = new JArray(c.Vocabulary ?? new string[0]),
            ["usage"] = Usage(c),
            ["warnings"] = new JArray(Lint(c)),
        };

        /// What a command still lacks to be found and used well by voice, the decision model and agents (empty = complete).
        /// Plugins hear these when they register (ScriptHost); the shell's own commands are tested to have none.
        public static List<string> Lint(VoiceCommand c)
        {
            var w = new List<string>();
            if (string.IsNullOrWhiteSpace(c.Description)) w.Add("no description (one line: what it does, its options)");
            int samples = c.Samples?.Length ?? 0;
            if ((c.Phrases?.Length ?? 0) == 0 && samples == 0) w.Add("nothing to recognise it by: add phrases and samples");
            if (samples < 4) w.Add($"{samples} samples: add 4 or more natural requests (English and Italian)");
            foreach (var p in c.Properties.Properties())
            {
                var prop = p.Value as JObject;
                if (prop == null) continue;
                if (string.IsNullOrWhiteSpace((string)prop["description"])) w.Add($"parameter {p.Name}: no description");
                string type = (string)prop["type"] ?? "string";
                if ((type == "number" || type == "integer") && prop["minimum"] == null && prop["maximum"] == null && (string)prop["description"] is string d && !Regex.IsMatch(d, @"\d"))
                    w.Add($"parameter {p.Name}: give its range (min / max) and unit");
            }
            return w;
        }

        /// Every enabled command as an OpenAI-style tool. Order and text stay stable, so the service's prompt
        /// cache keeps hitting between requests.
        public JArray ToolSpecs()
        {
            var a = new JArray();
            foreach (var c in _all.Where(c => c.Enabled).OrderBy(c => c.Owner == "shell" ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
                a.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject { ["name"] = c.Name, ["description"] = c.Description ?? "", ["parameters"] = c.Parameters.DeepClone() },
                });
            return a;
        }

        /// A JSON schema for simple commands: ("panel", "string", enumValues or null, "description", required).
        public static JObject Schema(params (string name, string type, string[] values, string description, bool required)[] ps)
        {
            var props = new JObject();
            var req = new JArray();
            foreach (var p in ps.OrderBy(p => p.required ? 0 : 1)) // the tool grammar wants required ones first
            {
                var o = new JObject { ["type"] = p.type };
                if (!string.IsNullOrEmpty(p.description)) o["description"] = p.description;
                if (p.values != null) o["enum"] = new JArray(p.values);
                props[p.name] = o;
                if (p.required) req.Add(p.name);
            }
            var s = new JObject { ["type"] = "object", ["properties"] = props };
            if (req.Count > 0) s["required"] = req;
            return s;
        }
    }
}
