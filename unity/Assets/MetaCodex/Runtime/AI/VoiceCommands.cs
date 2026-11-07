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
        /// Offered to the local LLM (off for commands that take free text verbatim: typing, shell commands;
        /// their spoken phrases still work).
        public bool Llm = true;
        /// Example requests and the command line they mean ("make it orange" -> "set_accent amber"): they teach
        /// the small LLM better than descriptions do.
        public List<(string said, string line)> Examples = new List<(string, string)>();
        /// Runs on the main thread with validated arguments; returns a short reply (shown, optionally spoken),
        /// null for none, or "error: ..." when it failed.
        public Func<JObject, string> Run;
        /// For commands that wait on something (MQH): call `done(reply)` once, within ~15 s. Used instead of Run.
        public Action<JObject, Action<string>> RunAsync;
        internal List<(Regex rx, Dictionary<string, string> constants)> Compiled = new List<(Regex, Dictionary<string, string>)>();

        public IEnumerable<string> Required => (Parameters["required"] as JArray)?.Select(x => (string)x) ?? Enumerable.Empty<string>();
        public JObject Properties => Parameters["properties"] as JObject ?? new JObject();
    }

    public sealed class VoiceMatch
    {
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
                try { var consts = new Dictionary<string, string>(); c.Compiled.Add((CompilePhrase(p, consts), consts)); }
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

        /// The first enabled command whose phrase matches and whose captured values fit the schema.
        public VoiceMatch FastMatch(string text)
        {
            string n = Normalize(text);
            if (n.Length == 0) return null;
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
                    if (ok && c.Required.All(r => args[r] != null)) return new VoiceMatch { Command = c, Args = args, Via = "fast" };
                }
            }
            return null;
        }

        /// Template -> anchored regex over normalized text with one trailing space. `{param:value}` sets a fixed
        /// value instead of capturing ("(make|let) the panels follow me {mode:float}").
        public static Regex CompilePhrase(string template, Dictionary<string, string> constants = null)
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
                    sb.Append("(?<").Append(name).Append(">.+?) ");
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
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return type == "integer" ? (JToken)(long)Math.Round(d) : d;
                int w = Array.IndexOf(new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" }, raw.Trim().ToLowerInvariant());
                return w >= 0 ? (JToken)w : null;
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
            return null;
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
                    rules.Append(rule).Append(" ::= ").Append(en != null ? string.Join(" | ", en.Select(Lit)) : "[^\\n]+").Append('\n'); // GBNF: any text up to the end of the line
                    parts.Add("\" \" " + rule);
                }
                alts.Add(string.Join(" ", parts));
                // optional trailing arguments may be left out
                for (int k = props.Count - 1; k >= 0 && !req.Contains(props[k].Name); k--) alts.Add(string.Join(" ", parts.Take(k + 1)));
            }
            alts.Add(Lit("none"));
            return "root ::= " + string.Join(" | ", alts.Distinct()) + "\n" + rules;
        }

        private static string Lit(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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
