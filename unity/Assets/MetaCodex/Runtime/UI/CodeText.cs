using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCodex.UI
{
    // The code viewer's text model (THREAD's sidebar, docs/thread-sidebar.md): syntax colours by a small line
    // tokenizer per language, and a unified diff turned into editor rows (old / new line numbers, +/- kinds, unchanged
    // runs folded). Pure C# (no Unity types): tested without the app (tests/sidebar).

    /// Token kinds; UiSourceView paints them in VS Code's dark palette.
    public enum Tok : byte { Text, Keyword, Control, Type, Func, String, Number, Comment, Meta, Tag, Attr, Key, Heading, Variable }

    public struct TokSpan
    {
        public int Start, Length;
        public Tok Kind;
        public TokSpan(int start, int length, Tok kind) { Start = start; Length = length; Kind = kind; }
    }

    public sealed class CodeLang
    {
        public string Name = "text";
        public HashSet<string> Keywords = new HashSet<string>(), Control = new HashSet<string>(), Types = new HashSet<string>();
        public string[] LineComments = new string[0];
        public string BlockOpen, BlockClose;
        public string Quotes = "";
        public bool Backtick;        // `...` strings that may span lines (JS template literals, Go raw strings)
        public bool TripleQuotes;    // Python's """ and '''
        public bool Verbatim;        // C#'s @"..." (may span lines; "" escapes a quote)
        public bool HashMeta;        // '#' first on a line: a preprocessor line (C, C#, shaders)
        public bool AtMeta;          // @decorator / [Attribute] style marks (Python, Java, Kotlin)
        public bool UpperTypes;      // Identifiers starting upper-case are types (C#, Java, TS, Rust, Go, Swift)
        public bool Markup;          // <tags attr="..."> (HTML, XML)
        public bool Markdown;
        public bool KeyValue;        // JSON / YAML / TOML / INI: keys before ':' or '='
        public bool Dollar;          // $VAR, ${VAR} (shell, makefiles)
        public bool IgnoreCase;      // SQL
        public bool Plain => Name == "text";
    }

    public static class CodeText
    {
        private static HashSet<string> W(string words) => new HashSet<string>(words.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        private const string CControl = "if else switch case default for while do break continue return goto";
        public static readonly CodeLang Text = new CodeLang();

        private static readonly CodeLang CSharp = new CodeLang
        {
            Name = "csharp",
            Keywords = W("abstract as base bool byte char checked class const decimal delegate double enum event explicit extern false fixed float implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref sbyte sealed short sizeof stackalloc static string struct this true typeof uint ulong unchecked unsafe ushort using virtual void volatile var async dynamic get set value partial where nameof record init with global not and or when"),
            Control = W(CControl + " foreach throw try catch finally yield await"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", Verbatim = true, HashMeta = true, UpperTypes = true,
        };
        private static readonly CodeLang JavaScript = new CodeLang
        {
            Name = "javascript",
            Keywords = W("var let const function class extends new this super import export from as of in instanceof typeof void delete null undefined true false async static get set interface type enum implements private public protected readonly declare namespace module abstract keyof infer is any unknown never string number boolean symbol object bigint satisfies"),
            Control = W(CControl + " throw try catch finally yield await"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", Backtick = true, UpperTypes = true, AtMeta = true,
        };
        private static readonly CodeLang Python = new CodeLang
        {
            Name = "python",
            Keywords = W("def class lambda None True False and or not in is import from as global nonlocal pass with async del assert self cls print"),
            Control = W("if elif else for while break continue return try except finally raise yield await match case"),
            Types = W("int float str bool list dict set tuple bytes object type"),
            LineComments = new[] { "#" }, Quotes = "\"'", TripleQuotes = true, AtMeta = true, UpperTypes = true,
        };
        private static readonly CodeLang Shell = new CodeLang
        {
            Name = "shell",
            Keywords = W("function in local export readonly declare alias echo printf set unset source cd exit eval exec shift trap test true false"),
            Control = W("if then else elif fi for while until do done case esac return break continue select"),
            LineComments = new[] { "#" }, Quotes = "\"'", Dollar = true,
        };
        private static readonly CodeLang Json = new CodeLang { Name = "json", Keywords = W("true false null"), LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"", KeyValue = true };
        private static readonly CodeLang Yaml = new CodeLang { Name = "yaml", Keywords = W("true false null yes no on off True False"), LineComments = new[] { "#" }, Quotes = "\"'", KeyValue = true };
        private static readonly CodeLang Toml = new CodeLang { Name = "toml", Keywords = W("true false"), LineComments = new[] { "#", ";" }, Quotes = "\"'", KeyValue = true };
        private static readonly CodeLang CLike = new CodeLang
        {
            Name = "c",
            Keywords = W("auto bool char class const constexpr double enum extern false float inline int long namespace new delete nullptr NULL operator private protected public register short signed sizeof static struct template this true typedef typename union unsigned using virtual void volatile override final noexcept auto mutable explicit friend"),
            Control = W(CControl + " throw try catch"),
            Types = W("size_t uint8_t uint16_t uint32_t uint64_t int8_t int16_t int32_t int64_t float2 float3 float4 half half2 half3 half4 float4x4 sampler2D fixed4 string vector map"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", HashMeta = true, UpperTypes = true,
        };
        private static readonly CodeLang Java = new CodeLang
        {
            Name = "java",
            Keywords = W("abstract boolean byte char class const double enum extends final float implements import instanceof int interface long native new null package private protected public short static super synchronized this transient true false var void volatile fun val object companion data sealed open override lateinit internal is in as typealias suspend"),
            Control = W(CControl + " throw throws try catch finally when"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", AtMeta = true, UpperTypes = true,
        };
        private static readonly CodeLang Go = new CodeLang
        {
            Name = "go",
            Keywords = W("package import func var const type struct interface map chan go defer select range nil true false iota make new len cap append"),
            Control = W(CControl + " fallthrough"),
            Types = W("int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 float32 float64 string bool byte rune error any"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", Backtick = true, UpperTypes = true,
        };
        private static readonly CodeLang Rust = new CodeLang
        {
            Name = "rust",
            Keywords = W("as const crate dyn enum extern false fn impl in let mod move mut pub ref self Self static struct super trait true type unsafe use where async macro_rules"),
            Control = W("if else match for while loop break continue return await"),
            Types = W("i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str String Vec Option Result Box"),
            LineComments = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "\"", UpperTypes = true, HashMeta = false,
        };
        private static readonly CodeLang Lua = new CodeLang
        {
            Name = "lua",
            Keywords = W("and function in local not or nil true false self"),
            Control = W("if then else elseif end for while do repeat until break return goto"),
            LineComments = new[] { "--" }, BlockOpen = "--[[", BlockClose = "]]", Quotes = "\"'",
        };
        private static readonly CodeLang Sql = new CodeLang
        {
            Name = "sql", IgnoreCase = true,
            Keywords = W("select from where insert into values update set delete create table index view drop alter add column primary key foreign references not null default and or in is like join left right inner outer on group by order asc desc limit offset having union all distinct as case when then else end begin commit rollback"),
            LineComments = new[] { "--" }, BlockOpen = "/*", BlockClose = "*/", Quotes = "'\"",
        };
        private static readonly CodeLang Css = new CodeLang { Name = "css", Keywords = W("important inherit initial none auto"), BlockOpen = "/*", BlockClose = "*/", Quotes = "\"'", KeyValue = true };
        private static readonly CodeLang Html = new CodeLang { Name = "html", Markup = true, BlockOpen = "<!--", BlockClose = "-->", Quotes = "\"'" };
        private static readonly CodeLang Markdown = new CodeLang { Name = "markdown", Markdown = true };
        private static readonly CodeLang Make = new CodeLang { Name = "make", Keywords = W("ifeq ifneq ifdef ifndef else endif include define endef export"), LineComments = new[] { "#" }, Quotes = "\"'", Dollar = true };

        /// The language for a file name ("unity/Assets/X.cs" -> C#); plain text when unknown.
        public static CodeLang LangFor(string path)
        {
            string name = (path ?? "").Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            string lower = name.ToLowerInvariant();
            if (lower == "makefile" || lower == "gnumakefile") return Make;
            if (lower == "dockerfile" || lower.StartsWith(".bashrc") || lower == ".profile" || lower.StartsWith(".env")) return Shell;
            int dot = lower.LastIndexOf('.');
            string ext = dot >= 0 ? lower.Substring(dot + 1) : "";
            switch (ext)
            {
                case "cs": return CSharp;
                case "js": case "jsx": case "mjs": case "cjs": case "ts": case "tsx": case "mts": case "cts": case "vue": case "svelte": return JavaScript;
                case "py": case "pyw": case "pyi": return Python;
                case "sh": case "bash": case "zsh": case "fish": case "ksh": return Shell;
                case "json": case "jsonc": case "json5": case "jsonl": case "asmdef": case "asmref": return Json;
                case "yml": case "yaml": case "unity": case "prefab": case "asset": case "mat": case "meta": case "anim": case "controller": return Yaml;
                case "toml": case "ini": case "cfg": case "conf": case "properties": case "editorconfig": case "gitconfig": return Toml;
                case "c": case "h": case "cpp": case "cc": case "cxx": case "hpp": case "hh": case "hxx": case "m": case "mm": case "ino":
                case "shader": case "hlsl": case "cginc": case "glsl": case "vert": case "frag": case "compute": case "metal": case "wgsl": case "zig": return CLike;
                case "java": case "kt": case "kts": case "scala": case "groovy": case "gradle": case "swift": case "dart": return Java;
                case "go": return Go;
                case "rs": return Rust;
                case "lua": return Lua;
                case "sql": return Sql;
                case "css": case "scss": case "sass": case "less": case "uss": return Css;
                case "html": case "htm": case "xml": case "svg": case "xaml": case "uxml": case "csproj": case "props": case "targets": case "plist": case "xhtml": return Html;
                case "md": case "markdown": case "mdx": return Markdown;
                case "mk": return Make;
            }
            return Text;
        }

        // multi-line states carried from one line to the next
        public const int Normal = 0, InBlock = 1, InTriple2 = 2, InTriple1 = 3, InBacktick = 4, InVerbatim = 5, InFence = 6;

        private static bool IsId(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool At(string s, int i, string what) => what != null && what.Length > 0 && i + what.Length <= s.Length && string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

        /// Colours one line: `state` is what the line before left open (a block comment, a multi-line string); the
        /// returned state goes to the next line. Spans cover the coloured parts only (the rest is plain text).
        public static int Tokenize(CodeLang lang, string line, int state, List<TokSpan> spans)
        {
            spans.Clear();
            if (string.IsNullOrEmpty(line) || lang == null || lang.Plain) return state == InFence || state == InBlock || state >= InTriple2 ? state : Normal;
            if (lang.Markdown) return Md(line, state, spans);
            int n = line.Length, i = 0;
            // continue what the line before left open
            if (state == InBlock)
            {
                int end = line.IndexOf(lang.BlockClose ?? "\u0001", StringComparison.Ordinal);
                if (end < 0) { spans.Add(new TokSpan(0, n, Tok.Comment)); return InBlock; }
                i = end + lang.BlockClose.Length;
                spans.Add(new TokSpan(0, i, Tok.Comment));
            }
            else if (state == InTriple2 || state == InTriple1)
            {
                string q = state == InTriple2 ? "\"\"\"" : "'''";
                int end = line.IndexOf(q, StringComparison.Ordinal);
                if (end < 0) { spans.Add(new TokSpan(0, n, Tok.String)); return state; }
                i = end + 3;
                spans.Add(new TokSpan(0, i, Tok.String));
            }
            else if (state == InBacktick || state == InVerbatim)
            {
                int end = state == InBacktick ? FindClose(line, 0, '`', true) : FindVerbatimClose(line, 0);
                if (end < 0) { spans.Add(new TokSpan(0, n, Tok.String)); return state; }
                i = end + 1;
                spans.Add(new TokSpan(0, i, Tok.String));
            }
            // a preprocessor line
            if (lang.HashMeta && i == 0)
            {
                int k = 0;
                while (k < n && (line[k] == ' ' || line[k] == '\t')) k++;
                if (k < n && line[k] == '#') { spans.Add(new TokSpan(k, n - k, Tok.Meta)); return Normal; }
            }
            bool lineStart = true; // only spaces so far (YAML keys, shell commands)
            while (i < n)
            {
                char c = line[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                // comments
                bool comment = false;
                foreach (var lc in lang.LineComments)
                    if (At(line, i, lc) && !(lc == "#" && lang.Dollar && i > 0 && line[i - 1] == '$')) { comment = true; break; }
                if (comment) { spans.Add(new TokSpan(i, n - i, Tok.Comment)); return Normal; }
                if (lang.BlockOpen != null && At(line, i, lang.BlockOpen))
                {
                    int end = line.IndexOf(lang.BlockClose, i + lang.BlockOpen.Length, StringComparison.Ordinal);
                    if (end < 0) { spans.Add(new TokSpan(i, n - i, Tok.Comment)); return InBlock; }
                    int len = end + lang.BlockClose.Length - i;
                    spans.Add(new TokSpan(i, len, Tok.Comment));
                    i += len;
                    continue;
                }
                // markup tags
                if (lang.Markup && c == '<')
                {
                    int j = i + 1;
                    if (j < n && (line[j] == '/' || line[j] == '!' || line[j] == '?')) j++;
                    int ns = j;
                    while (j < n && (IsId(line[j]) || line[j] == '-' || line[j] == ':' || line[j] == '.')) j++;
                    if (j > ns) { spans.Add(new TokSpan(ns, j - ns, Tok.Tag)); i = j; lineStart = false; continue; }
                }
                // strings
                if (lang.TripleQuotes && (At(line, i, "\"\"\"") || At(line, i, "'''")))
                {
                    string q = line.Substring(i, 3);
                    int end = line.IndexOf(q, i + 3, StringComparison.Ordinal);
                    if (end < 0) { spans.Add(new TokSpan(i, n - i, Tok.String)); return q == "\"\"\"" ? InTriple2 : InTriple1; }
                    spans.Add(new TokSpan(i, end + 3 - i, Tok.String));
                    i = end + 3;
                    lineStart = false;
                    continue;
                }
                if (lang.Verbatim && (c == '@' || c == '$') && i + 1 < n && (line[i + 1] == '"' || (line[i + 1] == '@' || line[i + 1] == '$') && i + 2 < n && line[i + 2] == '"'))
                {
                    bool verbatim = c == '@' || line[i + 1] == '@';
                    int q = line[i + 1] == '"' ? i + 1 : i + 2;
                    int end = verbatim ? FindVerbatimClose(line, q + 1) : FindClose(line, q + 1, '"', true);
                    if (end < 0) { spans.Add(new TokSpan(i, n - i, Tok.String)); return verbatim ? InVerbatim : Normal; }
                    spans.Add(new TokSpan(i, end + 1 - i, Tok.String));
                    i = end + 1;
                    lineStart = false;
                    continue;
                }
                if (lang.Backtick && c == '`')
                {
                    int end = FindClose(line, i + 1, '`', true);
                    if (end < 0) { spans.Add(new TokSpan(i, n - i, Tok.String)); return InBacktick; }
                    spans.Add(new TokSpan(i, end + 1 - i, Tok.String));
                    i = end + 1;
                    lineStart = false;
                    continue;
                }
                if (lang.Quotes.IndexOf(c) >= 0 && !(c == '\'' && lang == Rust))
                {
                    int end = FindClose(line, i + 1, c, c != '\'' || lang.Name != "shell");
                    int stop = end < 0 ? n : end + 1;
                    Tok kind = Tok.String;
                    if (lang.KeyValue)
                    {
                        int k = stop;
                        while (k < n && line[k] == ' ') k++;
                        if (k < n && line[k] == ':') kind = Tok.Key;
                    }
                    spans.Add(new TokSpan(i, stop - i, kind));
                    i = stop;
                    lineStart = false;
                    continue;
                }
                // numbers
                if (char.IsDigit(c) || c == '.' && i + 1 < n && char.IsDigit(line[i + 1]) && (i == 0 || !IsId(line[i - 1])))
                {
                    if (i > 0 && IsId(line[i - 1])) { i++; continue; }
                    int j = i + 1;
                    while (j < n && (IsId(line[j]) || line[j] == '.' && j + 1 < n && char.IsDigit(line[j + 1]))) j++;
                    spans.Add(new TokSpan(i, j - i, Tok.Number));
                    i = j;
                    lineStart = false;
                    continue;
                }
                // shell / make variables
                if (lang.Dollar && c == '$' && i + 1 < n)
                {
                    int j = i + 1;
                    if (line[j] == '{' || line[j] == '(') { char close = line[j] == '{' ? '}' : ')'; int e = line.IndexOf(close, j); j = e < 0 ? n : e + 1; }
                    else while (j < n && (IsId(line[j]) || j == i + 1 && "@*#?$!0123456789-".IndexOf(line[j]) >= 0)) j++;
                    if (j > i + 1) { spans.Add(new TokSpan(i, j - i, Tok.Variable)); i = j; lineStart = false; continue; }
                }
                // decorators / annotations
                if (lang.AtMeta && c == '@' && i + 1 < n && char.IsLetter(line[i + 1]))
                {
                    int j = i + 1;
                    while (j < n && (IsId(line[j]) || line[j] == '.')) j++;
                    spans.Add(new TokSpan(i, j - i, Tok.Meta));
                    i = j;
                    continue;
                }
                // words
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1;
                    while (j < n && (IsId(line[j]) || lang.Markup && (line[j] == '-' || line[j] == ':') || lang.Name == "css" && line[j] == '-')) j++;
                    string word = line.Substring(i, j - i);
                    string key = lang.IgnoreCase ? word.ToLowerInvariant() : word;
                    int k = j;
                    while (k < n && line[k] == ' ') k++;
                    char next = k < n ? line[k] : '\0';
                    Tok kind = Tok.Text;
                    if (lang.Markup) kind = next == '=' ? Tok.Attr : Tok.Text;
                    else if (lang.KeyValue && (lineStart || lang.Name == "css") && (next == ':' || next == '=' && lang.Name != "css")) kind = Tok.Key;
                    else if (lang.Control.Contains(key)) kind = Tok.Control;
                    else if (lang.Keywords.Contains(key)) kind = Tok.Keyword;
                    else if (lang.Types.Contains(key)) kind = Tok.Type;
                    else if (next == '(' && !lang.KeyValue) kind = Tok.Func;
                    else if (lang.UpperTypes && char.IsUpper(c) && word.Length > 1 && HasLower(word)) kind = Tok.Type;
                    if (kind != Tok.Text) spans.Add(new TokSpan(i, j - i, kind));
                    i = j;
                    lineStart = false;
                    continue;
                }
                if (c == '-' && lineStart && lang.Name == "yaml") { i++; continue; } // a list item: its key follows
                lineStart = false;
                i++;
            }
            return Normal;
        }

        private static bool HasLower(string w) { foreach (char ch in w) if (char.IsLower(ch)) return true; return false; }

        /// Index of the closing quote from `from` (backslash escapes when `escapes`), -1 when the line ends first.
        private static int FindClose(string s, int from, char q, bool escapes)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (escapes && s[i] == '\\') { i++; continue; }
                if (s[i] == q) return i;
            }
            return -1;
        }

        private static int FindVerbatimClose(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] != '"') continue;
                if (i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
                return i;
            }
            return -1;
        }

        private static int Md(string line, int state, List<TokSpan> spans)
        {
            int n = line.Length;
            string t = line.TrimStart();
            int lead = n - t.Length;
            if (t.StartsWith("```") || t.StartsWith("~~~")) { spans.Add(new TokSpan(0, n, Tok.Meta)); return state == InFence ? Normal : InFence; }
            if (state == InFence) { spans.Add(new TokSpan(0, n, Tok.String)); return InFence; }
            if (t.StartsWith("#")) { spans.Add(new TokSpan(0, n, Tok.Heading)); return Normal; }
            if (t.StartsWith(">")) { spans.Add(new TokSpan(0, n, Tok.Comment)); return Normal; }
            int i = 0;
            if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ")) { spans.Add(new TokSpan(lead, 1, Tok.Keyword)); i = lead + 1; }
            else
            {
                var m = Regex.Match(t, @"^\d+[.)] ");
                if (m.Success) { spans.Add(new TokSpan(lead, m.Length - 1, Tok.Keyword)); i = lead + m.Length; }
            }
            while (i < n)
            {
                char c = line[i];
                if (c == '`')
                {
                    int e = line.IndexOf('`', i + 1);
                    if (e < 0) break;
                    spans.Add(new TokSpan(i, e + 1 - i, Tok.String));
                    i = e + 1;
                    continue;
                }
                if (c == '*' && i + 1 < n && line[i + 1] == '*')
                {
                    int e = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (e < 0) { i += 2; continue; }
                    spans.Add(new TokSpan(i, e + 2 - i, Tok.Keyword));
                    i = e + 2;
                    continue;
                }
                if (c == '[')
                {
                    int close = line.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int end = close < 0 ? -1 : line.IndexOf(')', close + 2);
                    if (end > 0) { spans.Add(new TokSpan(i, close + 1 - i, Tok.Func)); spans.Add(new TokSpan(close + 1, end - close, Tok.Variable)); i = end + 1; continue; }
                }
                i++;
            }
            return Normal;
        }

        /// A file's first bytes hold a NUL (or are mostly not text): show it as binary.
        public static bool LooksBinary(byte[] head, int count)
        {
            int n = Math.Min(count, Math.Min(head?.Length ?? 0, 8192)), odd = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = head[i];
                if (b == 0) return true;
                if (b < 7 || b > 13 && b < 32 && b != 27) odd++;
            }
            return n > 0 && odd * 10 > n;
        }

        /// Tabs to spaces (tab stops of 4), the line's text as the viewer draws it.
        public static string ExpandTabs(string s)
        {
            if (s == null || s.IndexOf('\t') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\t') { int k = 4 - sb.Length % 4; sb.Append(' ', k); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static List<string> SplitLines(string text, int max, out bool cut)
        {
            var lines = new List<string>();
            cut = false;
            if (string.IsNullOrEmpty(text)) return lines;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                if (i == text.Length && start == text.Length) break; // a final newline
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                if (lines.Count >= max) { cut = true; break; }
                lines.Add(ExpandTabs(text.Substring(start, end - start)));
                start = i + 1;
            }
            return lines;
        }
    }

    // ---------------------------------------------------------------------------------------------- diffs

    public enum DiffKind : byte { Context, Add, Del, Hunk, Fold, Note }

    public sealed class DiffLine
    {
        public DiffKind Kind;
        public int Old, New;           // line numbers (0: none)
        public string Text = "";       // without the +/-/space prefix (tabs expanded)
        public List<DiffLine> Hidden;  // a fold: the unchanged lines it hides
        public int SyntaxState;        // the tokenizer's state at the start of this line (new side for context/add)
    }

    /// One file's unified diff as editor rows. Long unchanged runs fold into one row ("⋯ 42 unchanged lines") that
    /// opens on a tap, keeping `keep` lines of context around every change.
    public sealed class DiffDoc
    {
        public readonly List<DiffLine> Lines = new List<DiffLine>();
        public int Added, Removed, Hunks;
        public bool Binary, NewFile, Deleted, Truncated;
        public string Path = "", OldPath = "";

        private static readonly Regex HunkRx = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$", RegexOptions.Compiled);

        public static DiffDoc Parse(string unified, int keep = 3, int minFold = 4, int maxLines = 60000)
        {
            var d = new DiffDoc();
            if (string.IsNullOrEmpty(unified)) return d;
            int o = 0, nn = 0;
            bool inHunk = false;
            var raw = unified.Replace("\r\n", "\n").Split('\n');
            int count = raw.Length;
            if (count > 0 && raw[count - 1].Length == 0) count--;
            for (int i = 0; i < count; i++)
            {
                string l = raw[i];
                if (d.Lines.Count >= maxLines) { d.Truncated = true; break; }
                if (!inHunk || l.StartsWith("diff --git ") || l.StartsWith("@@"))
                {
                    if (l.StartsWith("diff --git ")) { inHunk = false; continue; }
                    if (l.StartsWith("--- ")) { d.OldPath = StripAB(l.Substring(4)); continue; }
                    if (l.StartsWith("+++ ")) { d.Path = StripAB(l.Substring(4)); continue; }
                    if (l.StartsWith("new file")) { d.NewFile = true; continue; }
                    if (l.StartsWith("deleted file")) { d.Deleted = true; continue; }
                    if (l.StartsWith("Binary files ") || l.StartsWith("GIT binary patch")) { d.Binary = true; continue; }
                    var m = HunkRx.Match(l);
                    if (m.Success)
                    {
                        o = int.Parse(m.Groups[1].Value);
                        nn = int.Parse(m.Groups[3].Value);
                        // `-0,0` / `+0,0`: an empty side starts at line 1 when lines come
                        if (o == 0) o = 1;
                        if (nn == 0) nn = 1;
                        d.Hunks++;
                        d.Lines.Add(new DiffLine { Kind = DiffKind.Hunk, Old = o, New = nn, Text = m.Groups[5].Value.Trim() });
                        inHunk = true;
                        continue;
                    }
                    if (!inHunk) continue; // index, mode, similarity lines
                }
                if (l.StartsWith("\\")) { d.Lines.Add(new DiffLine { Kind = DiffKind.Note, Text = l.TrimStart('\\', ' ') }); continue; }
                char k = l.Length > 0 ? l[0] : ' ';
                string text = CodeText.ExpandTabs(l.Length > 0 ? l.Substring(1) : "");
                if (k == '+') { d.Lines.Add(new DiffLine { Kind = DiffKind.Add, New = nn++, Text = text }); d.Added++; }
                else if (k == '-') { d.Lines.Add(new DiffLine { Kind = DiffKind.Del, Old = o++, Text = text }); d.Removed++; }
                else if (k == ' ' || l.Length == 0) d.Lines.Add(new DiffLine { Kind = DiffKind.Context, Old = o++, New = nn++, Text = text });
                else inHunk = false; // something else: the hunk ended
            }
            if (d.Path == "/dev/null") { d.Deleted = true; d.Path = d.OldPath; }
            if (d.OldPath == "/dev/null") d.NewFile = true;
            d.DropLeadingHunk();
            d.Fold(keep, minFold);
            return d;
        }

        private static string StripAB(string p)
        {
            p = p.Trim();
            int tab = p.IndexOf('\t');
            if (tab >= 0) p = p.Substring(0, tab);
            if (p.Length > 1 && p[0] == '"' && p[p.Length - 1] == '"') p = p.Substring(1, p.Length - 2);
            return p.StartsWith("a/") || p.StartsWith("b/") ? p.Substring(2) : p;
        }

        /// A single hunk that starts at the top says nothing: its header goes (full-context diffs, new files).
        private void DropLeadingHunk()
        {
            if (Hunks == 1 && Lines.Count > 0 && Lines[0].Kind == DiffKind.Hunk && Lines[0].Old <= 1 && Lines[0].New <= 1) Lines.RemoveAt(0);
        }

        /// Runs of unchanged lines longer than keep * 2 + minFold fold (at the file's start and end only one side keeps
        /// lines). The fold keeps the hidden lines to open them again.
        public void Fold(int keep, int minFold)
        {
            var outRows = new List<DiffLine>(Lines.Count);
            int i = 0;
            while (i < Lines.Count)
            {
                if (Lines[i].Kind != DiffKind.Context) { outRows.Add(Lines[i++]); continue; }
                int j = i;
                while (j < Lines.Count && Lines[j].Kind == DiffKind.Context) j++;
                bool atStart = i == 0 || Lines[i - 1].Kind == DiffKind.Hunk;
                bool atEnd = j == Lines.Count || Lines[j].Kind == DiffKind.Hunk || Lines[j].Kind == DiffKind.Note && j + 1 == Lines.Count;
                int keepBefore = atStart ? 0 : keep, keepAfter = atEnd ? 0 : keep;
                int len = j - i, hide = len - keepBefore - keepAfter;
                if (hide >= minFold && !(atStart && atEnd))
                {
                    for (int k = i; k < i + keepBefore; k++) outRows.Add(Lines[k]);
                    outRows.Add(new DiffLine { Kind = DiffKind.Fold, Old = Lines[i + keepBefore].Old, New = Lines[i + keepBefore].New, Hidden = Lines.GetRange(i + keepBefore, hide) });
                    for (int k = j - keepAfter; k < j; k++) outRows.Add(Lines[k]);
                }
                else for (int k = i; k < j; k++) outRows.Add(Lines[k]);
                i = j;
            }
            Lines.Clear();
            Lines.AddRange(outRows);
        }

        /// Opens the fold at row `index` (its lines take its place); false when that row is no fold.
        public bool Unfold(int index)
        {
            if (index < 0 || index >= Lines.Count || Lines[index].Kind != DiffKind.Fold) return false;
            var hidden = Lines[index].Hidden;
            Lines.RemoveAt(index);
            Lines.InsertRange(index, hidden);
            return true;
        }

        public int Folds { get { int n = 0; foreach (var l in Lines) if (l.Kind == DiffKind.Fold) n++; return n; } }

        /// Syntax states down the rows: the new side's lines (context, added) carry one state, removed lines another.
        public void ComputeSyntax(CodeLang lang)
        {
            var spans = new List<TokSpan>();
            int stNew = CodeText.Normal, stOld = CodeText.Normal;
            void Walk(DiffLine l)
            {
                switch (l.Kind)
                {
                    case DiffKind.Context: l.SyntaxState = stNew; stNew = CodeText.Tokenize(lang, l.Text, stNew, spans); stOld = stNew; break;
                    case DiffKind.Add: l.SyntaxState = stNew; stNew = CodeText.Tokenize(lang, l.Text, stNew, spans); break;
                    case DiffKind.Del: l.SyntaxState = stOld; stOld = CodeText.Tokenize(lang, l.Text, stOld, spans); break;
                    case DiffKind.Fold: foreach (var h in l.Hidden) Walk(h); break;
                    case DiffKind.Hunk: stNew = stOld = CodeText.Normal; break;
                }
            }
            foreach (var l in Lines) Walk(l);
        }
    }
}
