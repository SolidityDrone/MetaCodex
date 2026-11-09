using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MetaCodex.AI
{
    /// A Jev-style decision layer for voice commands ("System 1"; tools/voicebench measured it against the LLM).
    /// The options are the registry's commands and "none". Each command is represented by example sentences: its phrase
    /// templates expanded, its examples and samples (any language), its description. A sentence is split into clauses
    /// (English and Italian "and / then / commas"); each clause is embedded (multilingual-e5-small in the AI service) and
    /// scored against every command's examples (best cosine); the best command wins unless "none" scores higher or the best
    /// is under Threshold. Arguments are read from the clause through the command's own schema: enum values and their
    /// synonyms, numbers with their units, on / off, free text (the clause without the command's trigger words). A clause
    /// without a verb ("the files", "e la morbidezza a mezzo metro") repeats the previous command with its own values.
    /// Clauses run in the spoken order. No text is generated, so nothing is invented: a command, its values, or nothing.
    public sealed class VoiceDecider
    {
        /// Sentence vectors: texts -> one L2-normalised vector each, or an error. Answers on the main thread, maybe later.
        public delegate void EmbedFn(IList<string> texts, Action<float[][], string> done);

        /// Below this cosine the best command does not count (multilingual-e5-small: "none" examples decide most cases).
        public float Threshold = 0.30f;
        /// Commands this close to the best score are candidates too: the one that can run wins, and among those the one
        /// the sentence grounds (its own verb first, a value it names), then the higher score.
        public float Margin = 0.03f;
        /// multilingual-e5-small wants this in front of every text.
        public string Prefix = "query: ";
        public string Status { get; private set; } = "not built";
        public int ExampleCount => _labels.Count;
        /// Can decide (maybe with the command set of the last build: removed or disabled commands are skipped at once,
        /// new ones are known after the next build).
        public bool Usable => _matrix != null;
        /// Built from the registry as it is now (the shell rebuilds when this turns false).
        public bool Ready => _matrix != null && _builtRevision == _registry.Revision;
        public bool Building { get; private set; }
        /// What the last build changed in the cache (the shell saves it to disk when either is above 0).
        public int LastEmbedded { get; private set; }
        public int LastPruned { get; private set; }

        private readonly VoiceCommands _registry;
        private readonly EmbedFn _embed;
        private readonly Dictionary<string, float[]> _cache = new Dictionary<string, float[]>();
        private readonly List<string> _labels = new List<string>();
        private float[][] _matrix;
        private int _builtRevision = -1;
        private Dictionary<string, int[]> _byCommand = new Dictionary<string, int[]>();

        public VoiceDecider(VoiceCommands registry, EmbedFn embed)
        {
            _registry = registry;
            _embed = embed;
        }

        /// Cached vectors by text (persisted by the shell between runs, so a start embeds only what changed).
        public IReadOnlyDictionary<string, float[]> Cache => _cache;
        public void Seed(string text, float[] vector) { if (!string.IsNullOrEmpty(text) && vector != null) _cache[text] = vector; }

        // ------------------------------------------------------------------ examples

        private static readonly Dictionary<string, string[]> FreeFillers = new Dictionary<string, string[]>
        {
            ["query"] = new[] { "the release notes", "the crash on startup", "fixing the build" },
            ["name"] = new[] { "omega", "website" },
            ["text"] = new[] { "good morning" },
            ["command"] = new[] { "ls -la", "npm test" },
            ["value"] = new[] { "on", "off", "40 percent", "three meters", "max", "more" },
        };

        /// Sentences that are no command (English and Italian chit-chat): the "none" option.
        public static readonly string[] NoneExamples =
        {
            "what is this", "i like it", "okay thanks", "who are you", "hello",
            "come ti chiami", "va bene", "che bello", "non so", "fammi pensare",
            "tell me a story", "what do you think", "i'm hungry", "you're funny", "i'm bored", "good night", "see you later",
            "how's it going", "nice to meet you", "that was fun",
            "raccontami una storia", "cosa ne pensi", "ho fame", "sei simpatico", "mi annoio", "buonanotte", "a dopo", "come va",
            "piacere di conoscerti", "parlami di te",
        };

        /// (text, command) pairs: what the router compares a clause with.
        public List<(string text, string command)> BuildExamples(int perTemplate = 24)
        {
            var rnd = new Random(7);
            var ex = new List<(string, string)>();
            foreach (var c in _registry.All)
            {
                if (!c.Enabled) continue;
                foreach (var ph in c.Phrases ?? new string[0])
                    foreach (var e in Expand(c, ph, rnd, perTemplate)) ex.Add((e, c.Name));
                foreach (var (said, _) in c.Examples ?? new List<(string, string)>()) ex.Add((Fold(said), c.Name));
                foreach (var s in c.Samples ?? new string[0]) ex.Add((Fold(s), c.Name));
                if (!string.IsNullOrEmpty(c.Description)) ex.Add((Fold(c.Description), c.Name));
            }
            foreach (var (said, line) in _registry.GeneralExamples)
            {
                string name = line.Split(' ')[0];
                if (name == "none") ex.Add((Fold(said), "none"));
                else if (!line.Contains(";") && _registry.Find(name) is VoiceCommand gc && gc.Enabled) ex.Add((Fold(said), name));
            }
            foreach (var s in NoneExamples) ex.Add((Fold(s), "none"));
            var seen = new HashSet<string>();
            return ex.Where(x => x.Item1.Length > 0 && seen.Add(x.Item2 + "\u0001" + x.Item1)).ToList();
        }

        private static IEnumerable<string> Expand(VoiceCommand c, string template, Random rnd, int n)
        {
            var toks = VoiceCommands.TemplateTokens(template).ToList();
            var outSet = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                var words = new List<string>();
                foreach (var t in toks)
                {
                    if (t.StartsWith("{"))
                    {
                        string inner = t.Trim('{', '}');
                        if (inner.Contains(":")) continue;
                        words.Add(Filler(c, inner.Trim(), rnd));
                    }
                    else if (t.StartsWith("(")) { var alt = t.Substring(1, t.Length - 2).Split('|'); words.Add(alt[rnd.Next(alt.Length)]); }
                    else if (t.StartsWith("[")) { if (rnd.NextDouble() < 0.5) { var alt = t.Substring(1, t.Length - 2).Split('|'); words.Add(alt[rnd.Next(alt.Length)]); } }
                    else words.Add(t);
                }
                outSet.Add(Fold(string.Join(" ", words.Where(w => w.Length > 0))));
            }
            return outSet;
        }

        private static string Filler(VoiceCommand c, string param, Random rnd)
        {
            var prop = c.Properties[param] as JObject;
            if (prop?["enum"] is JArray en && en.Count > 0)
            {
                var names = en.Select(x => (string)x).ToList();
                if (c.Synonyms != null && c.Synonyms.TryGetValue(param, out var syn)) names.AddRange(syn.Keys);
                return names[rnd.Next(names.Count)];
            }
            string type = (string)prop?["type"] ?? "string";
            if (type == "number" || type == "integer") return rnd.Next(2) == 0 ? "three" : "50 percent";
            if (type == "boolean") return rnd.Next(2) == 0 ? "on" : "off";
            var f = FreeFillers.TryGetValue(param, out var ff) ? ff : new[] { "something" };
            return f[rnd.Next(f.Length)];
        }

        /// Embeds the examples not cached yet (batches of 64) and makes the router ready; again whenever the registry
        /// changes (a plugin added or removed a command, a command was disabled).
        public void Build(Action<string> done = null)
        {
            if (Building) { done?.Invoke("building"); return; }
            int rev = _registry.Revision;
            var ex = BuildExamples();
            var missing = ex.Select(e => e.text).Distinct().Where(t => !_cache.ContainsKey(t)).ToList();
            Building = true;
            Status = missing.Count > 0 ? $"embedding {missing.Count} examples…" : "building…";
            int at = 0;
            void Next()
            {
                if (at >= missing.Count) { Finish(); return; }
                var batch = missing.Skip(at).Take(64).ToList();
                _embed(batch.Select(t => Prefix + t).ToList(), (vecs, err) =>
                {
                    if (err != null || vecs == null || vecs.Length != batch.Count) { Building = false; Status = "decision model: " + (err ?? "no vectors"); done?.Invoke(Status); return; }
                    for (int i = 0; i < batch.Count; i++) _cache[batch[i]] = vecs[i];
                    at += batch.Count;
                    Status = $"embedding examples {at}/{missing.Count}…";
                    Next();
                });
            }
            void Finish()
            {
                _labels.Clear();
                var rows = new List<float[]>();
                foreach (var (t, c) in ex) { if (!_cache.TryGetValue(t, out var v)) continue; rows.Add(v); _labels.Add(c); }
                _matrix = rows.ToArray();
                _byCommand = _labels.Select((l, i) => (l, i)).GroupBy(x => x.l).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
                _builtRevision = rev;
                // the cache keeps only what the current commands use (a removed plugin's examples go with it)
                var keep = new HashSet<string>(ex.Select(e => e.text));
                var drop = _cache.Keys.Where(k => !keep.Contains(k)).ToList();
                foreach (var k in drop) _cache.Remove(k);
                LastEmbedded = missing.Count;
                LastPruned = drop.Count;
                Building = false;
                Status = $"ready: {_labels.Count} examples, {_byCommand.Count - (_byCommand.ContainsKey("none") ? 1 : 0)} commands";
                done?.Invoke(null);
            }
            Next();
        }

        // ------------------------------------------------------------------ deciding

        public sealed class Clause { public string Text; public string Command; public float Score; public string Second; public float SecondScore; public string Note; }

        public sealed class Result
        {
            public List<VoiceMatch> Matches = new List<VoiceMatch>();
            public List<Clause> Clauses = new List<Clause>();
            public string Error;
            public int EmbedMs;
            public override string ToString() => string.Join(" | ", Clauses.Select(c => $"“{c.Text}” -> {c.Command} {c.Score:0.00}" + (c.Note != null ? " (" + c.Note + ")" : "")));
            /// {matches: [{command, args}], clauses: [{text, command, score, second, second_score, note}], error}
            public JObject ToJson() => new JObject
            {
                ["matches"] = new JArray(Matches.Select(m => new JObject { ["command"] = m.Command.Name, ["args"] = m.Args.DeepClone() })),
                ["clauses"] = new JArray(Clauses.Select(c => new JObject
                {
                    ["text"] = c.Text, ["command"] = c.Command, ["score"] = Math.Round(c.Score, 3), ["second"] = c.Second, ["second_score"] = Math.Round(c.SecondScore, 3), ["note"] = c.Note,
                })),
                ["error"] = Error,
            };
        }

        /// The commands said in `text`, in order (none: an empty list).
        public void Decide(string text, Action<Result> done)
        {
            var res = new Result();
            if (_matrix == null) { res.Error = "the decision model is not ready (" + Status + ")"; done(res); return; }
            var cl = Clauses(text);
            if (cl.Count == 0) { done(res); return; }
            _embed(cl.Select(c => Prefix + c).ToList(), (vecs, err) =>
            {
                if (err != null || vecs == null || vecs.Length != cl.Count) { res.Error = "decision model: " + (err ?? "no vectors"); done(res); return; }
                Decide(cl, vecs, res);
                done(res);
            });
        }

        /// Deciding with the clause vectors at hand (self-tests).
        public void Decide(List<string> clauses, float[][] vecs, Result res)
        {
            (VoiceCommand cmd, JObject args)? prev = null;
            for (int i = 0; i < clauses.Count; i++)
            {
                string c = clauses[i];
                var info = new Clause { Text = c };
                res.Clauses.Add(info);
                var scores = Score(vecs[i]);
                // only what can run now: a command removed or disabled since the last build is not an option
                var ranked = scores.Where(kv => kv.Key == "none" || Available(kv.Key)).OrderByDescending(kv => kv.Value).ToList();
                if (ranked.Count == 0) { info.Command = "none"; continue; }
                string best = ranked[0].Key;
                float top = ranked[0].Value;
                info.Command = best;
                info.Score = top;
                if (ranked.Count > 1) { info.Second = ranked[1].Key; info.SecondScore = ranked[1].Value; }
                bool verbless = !Verbs.Any(v => HasWord(c, v));
                // an elliptic part: the previous command with this part's own values
                if (prev.HasValue && verbless && HasEnumParam(prev.Value.cmd))
                {
                    var a = ReadArgs(prev.Value.cmd, c, prev.Value.args);
                    if (a != null && Valid(prev.Value.cmd, a))
                    {
                        res.Matches.Add(new VoiceMatch { Command = prev.Value.cmd, Args = a, Via = "decide" });
                        info.Command = prev.Value.cmd.Name;
                        info.Note = "continues the previous command";
                        continue;
                    }
                }
                if (best == "none" || top < Threshold) { info.Command = "none"; continue; }
                // the best option that can run, among those within Margin of the top (none above them stops the search)
                VoiceCommand cmd = null;
                JObject args = null;
                int support = -1;
                float score = 0f;
                string why = null;
                foreach (var kv in ranked)
                {
                    if (kv.Value < top - Margin || kv.Key == "none") break;
                    var cand = _registry.Find(kv.Key);
                    if (cand == null || !cand.Enabled) continue;
                    // commands that take words verbatim (typing, shell commands) only when said with their own verb
                    if (!cand.Llm && !SaidExplicitly(cand, c)) { why = why ?? "say it with its verb: " + string.Join(" / ", TriggerWords(cand).Take(4)); continue; }
                    var a = ReadArgs(cand, c, null);
                    if (a == null || !Valid(cand, a)) { why = why ?? kv.Key + ": values missing"; continue; }
                    int sup = Support(cand, c);
                    if (sup > support) { cmd = cand; args = a; support = sup; score = kv.Value; }
                }
                if (cmd == null) { info.Command = "none"; info.Note = why; continue; }
                if (cmd.Name != best) info.Note = $"{best} {top:0.000} could not run or was less grounded";
                info.Command = cmd.Name;
                info.Score = score;
                res.Matches.Add(new VoiceMatch { Command = cmd, Args = args, Via = "decide" });
                prev = (cmd, args);
            }
        }

        public Dictionary<string, float> Score(float[] v)
        {
            var d = new Dictionary<string, float>();
            foreach (var kv in _byCommand)
            {
                float best = -1f;
                foreach (int i in kv.Value)
                {
                    var e = _matrix[i];
                    float s = 0f;
                    for (int k = 0; k < e.Length && k < v.Length; k++) s += e[k] * v[k];
                    if (s > best) best = s;
                }
                d[kv.Key] = best;
            }
            return d;
        }

        private bool Available(string name) => _registry.Find(name) is VoiceCommand c && c.Enabled;

        /// First words too common to say which command is meant.
        private static readonly HashSet<string> GenericFirst = new HashSet<string> { "let", "can", "could", "please", "want", "need", "voglio", "vorrei", "puoi", "what", "how" };

        /// How much the clause itself points at the command: its own verb first (a phrase's or sample's first word), and a
        /// closed-set value it names (a panel, a setting, a preset; on / off does not count).
        public int Support(VoiceCommand cmd, string clause)
        {
            int s = 0;
            string first = clause.Split(' ')[0];
            if (first.Length > 2 && !Stop.Contains(first) && !GenericFirst.Contains(first) && TriggerWords(cmd).Contains(first)) s++;
            foreach (var p in cmd.Properties.Properties())
            {
                if (!(p.Value["enum"] is JArray en)) continue;
                var values = en.Select(x => (string)x).ToList();
                if (values.Count == 2 && values.Contains("on") && values.Contains("off")) continue;
                if (FindName(clause, NamesOf(cmd, p.Name, values)) != null) { s++; break; }
            }
            return s;
        }

        /// The words a verbatim command starts with: the first word of each phrase template and of each sample.
        public static HashSet<string> TriggerWords(VoiceCommand cmd)
        {
            var w = new HashSet<string>();
            foreach (var ph in cmd.Phrases ?? new string[0])
            {
                var first = VoiceCommands.TemplateTokens(ph).FirstOrDefault();
                if (first == null || first.StartsWith("{") || first.StartsWith("[")) continue;
                foreach (var alt in first.Trim('(', ')').Split('|')) { var f = Fold(alt).Split(' ')[0]; if (f.Length > 0) w.Add(f); }
            }
            foreach (var sm in cmd.Samples ?? new string[0]) { var f = Fold(sm).Split(' ')[0]; if (f.Length > 0) w.Add(f); }
            return w;
        }

        private static bool SaidExplicitly(VoiceCommand cmd, string clause)
        {
            string first = clause.Split(' ')[0];
            return TriggerWords(cmd).Contains(first);
        }

        private bool Valid(VoiceCommand cmd, JObject args)
        {
            if (cmd.Required.Any(r => args[r] == null)) return false;
            return _registry.Validate(cmd, args) == null;
        }

        private static bool HasEnumParam(VoiceCommand c) => c.Properties.Properties().Any(p => p.Value["enum"] is JArray en && en.Count > 2);

        // ------------------------------------------------------------------ arguments, from the command's schema

        /// The command's parameters read from the clause; null when a required one is not there.
        public JObject ReadArgs(VoiceCommand cmd, string clause, JObject previous)
        {
            var args = new JObject();
            foreach (var p in cmd.Properties.Properties())
            {
                var prop = p.Value as JObject;
                if (prop == null) continue;
                string type = (string)prop["type"] ?? "string";
                if (prop["enum"] is JArray en && en.Count > 0)
                {
                    var values = en.Select(x => (string)x).ToList();
                    if (values.Count == 2 && values.Contains("on") && values.Contains("off"))
                    {
                        args[p.Name] = OnOff(clause, true);
                        continue;
                    }
                    string v = FindName(clause, NamesOf(cmd, p.Name, values));
                    if (v != null) args[p.Name] = v;
                    continue;
                }
                if (type == "number" || type == "integer")
                {
                    string val = ValueText(clause);
                    if (val != null) { var co = VoiceCommands.Coerce(cmd, p.Name, val); if (co != null) args[p.Name] = co; }
                    continue;
                }
                if (type == "boolean") { args[p.Name] = OnOff(clause, true) == "on"; continue; }
                // free strings: a value phrase for "value", else the clause without the command's own words
                string s = p.Name == "value" ? (ValueText(clause) ?? OnOff(clause, false)) : FreeText(cmd, clause);
                if (!string.IsNullOrEmpty(s)) args[p.Name] = s;
            }
            return args;
        }

        private static Dictionary<string, string> NamesOf(VoiceCommand cmd, string param, List<string> values)
        {
            var m = new Dictionary<string, string>();
            foreach (var v in values) m[Fold(v)] = v;
            if (cmd.Synonyms != null && cmd.Synonyms.TryGetValue(param, out var syn))
                foreach (var kv in syn) m[Fold(kv.Key)] = kv.Value;
            return m;
        }

        /// The longest known name in the clause; else a word one or two letters off a known one-word name.
        public static string FindName(string clause, Dictionary<string, string> names)
        {
            foreach (var k in names.Keys.OrderByDescending(k => k.Length)) if (k.Length > 0 && HasWord(clause, k)) return names[k];
            foreach (var w in clause.Split(' '))
            {
                if (w.Length < 4) continue;
                string best = null;
                int bestD = 9;
                foreach (var kv in names)
                {
                    // only names of 5+ letters: a 4-letter one is too close to everyday words ("what" -> "chat")
                    if (kv.Key.Contains(" ") || kv.Key.Length < 5) continue;
                    int d = VoiceCommands.Levenshtein(w, kv.Key);
                    int lim = kv.Key.Length <= 6 ? 1 : 2;
                    if (d <= lim && d < bestD) { bestD = d; best = kv.Value; }
                }
                if (best != null) return best;
            }
            return null;
        }

        private static readonly string[] OnWords = { "turn on", "switch on", "enable", "activate", "start", "show", "on", "accendi", "attiva", "avvia", "mostra", "mostrami", "darken", "dim", "oscura" };
        private static readonly string[] OffWords = { "turn off", "switch off", "turn of", "disable", "deactivate", "stop", "mute", "hide", "off", "end", "flat", "again",
            "spegni", "disattiva", "ferma", "togli", "nascondi", "muto", "di nuovo", "rivedere" };

        /// "on" / "off" from the clause's words (off wins); `fallback`: "on" when neither is said.
        public static string OnOff(string clause, bool fallback)
        {
            if (OffWords.Any(w => HasWord(clause, w))) return "off";
            if (OnWords.Any(w => HasWord(clause, w))) return "on";
            return fallback ? "on" : null;
        }

        private static readonly string[] MoreWords = { "a bit more", "a little more", "more", "up", "higher", "bigger", "larger", "stronger", "darker", "wider", "louder",
            "brighter", "increase", "raise", "di piu", "piu grandi", "piu grande", "piu", "aumenta", "alza", "ingrandisci", "scurisci" };
        private static readonly string[] LessWords = { "a bit less", "a little less", "less", "down", "lower", "smaller", "weaker", "lighter", "narrower", "quieter", "decrease",
            "reduce", "transparent", "meno", "piu piccoli", "piu piccolo", "diminuisci", "abbassa", "riduci", "rimpicciolisci", "trasparenti" };

        /// The value said in a clause, in the words the app's number reader takes: "30 percent", "2.5 m", "50 cm", "max",
        /// "half m", "quarter", "+" / "-" (more / less); null when there is none.
        public static string ValueText(string clause)
        {
            string t = clause;
            bool pct = Regex.IsMatch(t, @"%|\bpercent\b|\bper cent\b|\bper cento\b|\bpercento\b");
            string unit = Regex.IsMatch(t, @"\b(cm|centimet(er|re|ri|ro)s?)\b") ? " cm" : Regex.IsMatch(t, @"\b(meters?|metres?|metri|metro)\b") ? " m" : "";
            string suffix = pct ? " percent" : unit;
            if (Regex.IsMatch(t, @"\b(max|maximum|full|massimo|completely|completamente)\b")) return "max";
            if (Regex.IsMatch(t, @"\b(min|minimum|minimo)\b")) return "min";
            var m = Regex.Match(t, @"(\d+(?:\.\d+)?)");
            if (m.Success)
            {
                double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (Regex.IsMatch(t, @"\b(and a half|e mezzo)\b")) v += 0.5;
                return v.ToString(CultureInfo.InvariantCulture) + suffix;
            }
            if (VoiceCommands.TryNumberWords(t, out double w))
            {
                if (Regex.IsMatch(t, @"\b(and a half|e mezzo)\b")) w += 0.5;
                return w.ToString(CultureInfo.InvariantCulture) + suffix;
            }
            // "un metro e mezzo", "a meter and a half", "a metre": the article is the number one ("mezzo metro" is not)
            bool andHalf = Regex.IsMatch(t, @"\b(and a half|e mezzo)\b");
            if (unit != "" && !Regex.IsMatch(Regex.Replace(t, @"\b(and a half|e mezzo)\b", " "), @"\b(half|mezzo|meta|mezza)\b")
                && Regex.IsMatch(t, @"\b(un|uno|una|a|one)\s+(cm|centimet\w*|meters?|metres?|metri|metro)\b"))
                return (andHalf ? 1.5 : 1.0).ToString(CultureInfo.InvariantCulture) + suffix;
            if (Regex.IsMatch(t, @"\b(half|mezzo|meta|mezza)\b")) return "half" + unit;
            if (Regex.IsMatch(t, @"\b(quarter|quarto)\b")) return "quarter";
            // the most specific word wins: "più trasparenti" is less opacity, "più piccoli" smaller, "più grandi" bigger
            string more = MoreWords.Where(x => HasWord(t, x)).OrderByDescending(x => x.Length).FirstOrDefault();
            string less = LessWords.Where(x => HasWord(t, x)).OrderByDescending(x => x.Length).FirstOrDefault();
            if (more != null && (less == null || more.Length >= less.Length)) return "+";
            if (less != null) return "-";
            return null;
        }

        private static readonly HashSet<string> Stop = new HashSet<string> { "the", "a", "an", "to", "my", "me", "please", "of", "for", "il", "la", "lo", "i", "gli", "le", "l", "di",
            "del", "della", "dei", "al", "alla", "per", "favore", "un", "una", "chiamato", "chiamata", "called", "named" };

        /// The clause without the command's own words (its phrase templates' literals) and filler words.
        public static string FreeText(VoiceCommand cmd, string clause)
        {
            var trig = new HashSet<string>(Stop);
            foreach (var ph in cmd.Phrases ?? new string[0])
                foreach (var tok in VoiceCommands.TemplateTokens(ph))
                {
                    if (tok.StartsWith("{")) continue;
                    foreach (var w in Fold(tok.Trim('(', ')', '[', ']').Replace('|', ' ')).Split(' ')) if (w.Length > 0) trig.Add(w);
                }
            foreach (var w in new[] { "find", "search", "look", "thread", "threads", "conversation", "chat", "chats", "where", "which", "about", "was", "workspace", "project",
                "switch", "go", "change", "open", "trova", "cerca", "conversazione", "dove", "quale", "parlava", "sulla", "sui", "nelle", "passa", "vai", "progetto", "cambia",
                "apri", "type", "run", "execute", "scrivi", "esegui", "digita", "lancia", "nel", "sul", "terminale", "sito", "naviga", "su", "verso", "pagina", "indirizzo" })
                trig.Add(w);
            var words = clause.Split(' ').Where(w => w.Length > 0).ToList();
            // keep what follows the first non-trigger word (a query can contain "the")
            int start = words.FindIndex(w => !trig.Contains(w));
            if (start < 0) return null;
            var rest = words.Skip(start).ToList();
            if (cmd.Name == "switch_workspace") rest = rest.Where(w => !trig.Contains(w)).ToList();
            // and without the command's words at the end ("ls in the terminal", "ls nel terminale")
            while (rest.Count > 1 && trig.Contains(rest[rest.Count - 1])) rest.RemoveAt(rest.Count - 1);
            return rest.Count == 0 ? null : string.Join(" ", rest);
        }

        // ------------------------------------------------------------------ text

        private static readonly string[] Fillers = { "hey metacodex", "ok", "okay", "hey", "please", "could you", "can you", "would you", "i want to", "i would like to",
            "id like to", "lets", "now", "per favore", "puoi", "potresti", "ehi", "vorrei", "adesso" };
        private static readonly Regex Join = new Regex(@"\s*(?:[,;]|\band then\b|\band also\b|\bafter that\b|\bthen\b|\balso\b|\band\b|\be poi\b|\bpoi\b|\banche\b|\bed\b|\be\b)\s*", RegexOptions.Compiled);
        private static readonly string[] Verbs = { "open", "close", "show", "hide", "turn", "set", "make", "switch", "use", "find", "search", "look", "start", "run", "type", "put",
            "bring", "dim", "darken", "increase", "decrease", "reduce", "raise", "lower", "mute", "enable", "disable", "recenter", "go", "give", "change", "let", "pin", "clear",
            "get", "list", "take", "dismiss", "volume", "radius", "intensity", "apri", "chiudi", "mostra", "mostrami", "nascondi", "accendi", "spegni", "imposta", "metti",
            "rendi", "passa", "usa", "trova", "cerca", "avvia", "esegui", "scrivi", "porta", "portami", "riporta", "oscura", "aumenta", "diminuisci", "alza", "abbassa",
            "attiva", "disattiva", "togli", "ricentra", "vai", "cambia", "fai", "blocca", "rimpicciolisci", "scurisci", "elenca", "dammi", "pulisci", "fammi", "intensita", "raggio" };

        /// The sentence's parts, each lower-case and folded (accents off), polite fillers dropped.
        public static List<string> Clauses(string text)
        {
            string t = (text ?? "").ToLowerInvariant();
            t = Regex.Replace(t, @"(\d),(\d)", "$1.$2");
            t = Regex.Replace(t, @"\band a half\b", " andahalf ");
            t = Regex.Replace(t, @"\be mezzo\b", " emezzo ");
            var outList = new List<string>();
            foreach (var part in Join.Split(t))
            {
                // sentence punctuation off ("turn the focus off." must read "off"); a decimal point stays ("2.5")
                string p = part.Replace("andahalf", "and a half").Replace("emezzo", "e mezzo").Trim(' ', '.', '!', '?', '…');
                string f = Fold(p).Trim('.', ' ');
                for (bool changed = true; changed;)
                {
                    changed = false;
                    foreach (var fl in Fillers)
                    {
                        if (f == fl) { f = ""; break; }
                        if (f.StartsWith(fl + " ")) { f = f.Substring(fl.Length + 1); changed = true; }
                        if (f.EndsWith(" " + fl)) { f = f.Substring(0, f.Length - fl.Length - 1); changed = true; }
                    }
                }
                if (f.Length > 0) outList.Add(f);
            }
            return outList;
        }

        /// Lower case, accents off, apostrophes as spaces, letters / digits / % / . only.
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char ch in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (ch == '\'' || ch == '’') { sb.Append(' '); continue; }
                sb.Append(char.IsLetterOrDigit(ch) || ch == '%' || ch == '.' ? ch : ' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        public static bool HasWord(string t, string w) => Regex.IsMatch(" " + t + " ", @"\s" + Regex.Escape(w) + @"\s");
    }
}
