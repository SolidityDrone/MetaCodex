using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MetaCodex.Scripting
{
    /// Checks a view spec ({title, components, columns?, size?}) before a plugin window is built from it. No Unity
    /// types: the host (ui.panel) and a PC harness use the same rules. Component numbers in messages start at 1.
    public static class ViewSpecCheck
    {
        public const int MaxComponents = 16, MaxButtons = 12, MaxCells = 49, MaxGridColumns = 7;

        /// Every component type the app draws (aliases included).
        public static readonly string[] Types =
        {
            "text", "markdown", "note", "stats", "kpi", "chart", "graph", "flowchart", "mermaid", "sequence", "diagram",
            "table", "code", "diff", "log", "progress", "checklist", "todo", "timeline", "kanban", "kv", "list",
            "choices", "buttons", "toggle", "grid", "input", "image", "scene3d", "physics",
        };

        private static readonly HashSet<string> TypeSet = new HashSet<string>(Types, StringComparer.Ordinal);

        public static bool IsKnownType(string type) => type != null && TypeSet.Contains(type);

        /// A `text`-like component (its `text` can be LIVE when it has an `id`).
        public static bool IsTextType(string type) => type == "text" || type == "markdown" || type == "note";

        public static void Check(JObject spec, List<string> errors, List<string> warnings)
        {
            if (spec == null) { errors.Add("the spec is missing"); return; }

            string title = Str(spec["title"]);
            if (title != null && title.Length > 40) warnings.Add("title is " + title.Length + " characters; keep it under 40");

            var size = spec["size"];
            if (size != null && size.Type != JTokenType.Null)
            {
                if (size is JArray sa && sa.Count == 2 && IsNum(sa[0]) && IsNum(sa[1]))
                {
                    double w = (double)sa[0], h = (double)sa[1];
                    if (w < 0.3 || w > 1.2 || h < 0.18 || h > 0.9)
                        warnings.Add("size {" + Fmt(w) + ", " + Fmt(h) + "} is outside 0.3..1.2 x 0.18..0.9 metres; the app clamps it");
                }
                else errors.Add("size must be two numbers {width, height} in metres, for example size={0.6, 0.4}");
            }

            var cols = spec["columns"];
            if (cols != null && cols.Type != JTokenType.Null)
            {
                if (!IsNum(cols)) errors.Add("columns must be a number (1 or 2)");
                else if ((double)cols > 2) warnings.Add("columns > 2: the windows are narrow; 1 or 2 read best");
            }

            if (!(spec["components"] is JArray comps)) { errors.Add("components must be a list, for example components={ {type=\"text\", text=\"hello\"} }"); return; }
            if (comps.Count == 0) { errors.Add("components is empty: add at least one component"); return; }
            if (comps.Count > MaxComponents) errors.Add("components has " + comps.Count + " entries; the most is " + MaxComponents);
            else if (comps.Count > 8) warnings.Add(comps.Count + " components; more than 8 is a long window, split it");

            var compIds = new HashSet<string>(StringComparer.Ordinal);
            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < comps.Count; i++)
            {
                string at = "component " + (i + 1);
                if (!(comps[i] is JObject c)) { errors.Add(at + ": must be a table like {type=\"text\", text=\"...\"}"); continue; }
                string type = Str(c["type"]);
                if (string.IsNullOrEmpty(type)) { errors.Add(at + ": no type (one of " + string.Join(", ", Types) + ")"); continue; }
                at += " (" + type + ")";
                if (!IsKnownType(type)) { errors.Add(at + ": unknown type '" + type + "' (use " + string.Join(", ", Types) + ")"); continue; }

                string id = Str(c["id"]);
                if (!string.IsNullOrEmpty(id) && !compIds.Add(id)) errors.Add(at + ": the id '" + id + "' is used twice in this window");

                switch (type)
                {
                    case "text":
                    case "markdown":
                    case "note":
                    {
                        string text = Str(c["text"]) ?? Str(c["markdown"]);
                        if (text != null && text.Length > 600) warnings.Add(at + ": " + text.Length + " characters; more than 600 is too much to read in VR");
                        string style = Str(c["style"]);
                        if (style != null && style != "big") errors.Add(at + ": style must be \"big\" or left out");
                        if (style == "big" && text != null && (text.Length > 24 || text.Contains("\n"))) warnings.Add(at + ": a \"big\" text is one short line (24 characters at most)");
                        break;
                    }
                    case "choices":
                        CheckOptions(c, at, errors);
                        break;
                    case "buttons":
                        if (c["items"] == null && c["options"] != null) CheckOptions(c, at, errors); // the old form: an alias of choices
                        else CheckButtons(c, at, eventIds, errors, warnings);
                        break;
                    case "toggle":
                        if (string.IsNullOrEmpty(id)) errors.Add(at + ": a toggle needs an id: {type=\"toggle\", id=\"sound\", label=\"Sound\", value=true}");
                        if (string.IsNullOrEmpty(Str(c["label"]))) errors.Add(at + ": a toggle needs a label: {type=\"toggle\", id=\"sound\", label=\"Sound\", value=true}");
                        break;
                    case "grid":
                        CheckGrid(c, at, eventIds, errors);
                        break;
                }
            }
        }

        private static void CheckOptions(JObject c, string at, List<string> errors)
        {
            if (!(c["options"] is JArray o) || o.Count == 0) errors.Add(at + ": options must be a list of choices, for example options={\"yes\", \"no\"}");
        }

        private static void CheckButtons(JObject c, string at, HashSet<string> eventIds, List<string> errors, List<string> warnings)
        {
            if (!(c["items"] is JArray items) || items.Count == 0)
            {
                errors.Add(at + ": items must be a list like items={ {id=\"go\", label=\"Go\"} }");
                return;
            }
            if (items.Count > MaxButtons) errors.Add(at + ": " + items.Count + " items; the most is " + MaxButtons);
            for (int i = 0; i < items.Count; i++)
            {
                string b = at + " item " + (i + 1);
                if (!(items[i] is JObject it)) { errors.Add(b + ": must be a table like {id=\"go\", label=\"Go\"}"); continue; }
                string id = Str(it["id"]), label = Str(it["label"]);
                if (string.IsNullOrEmpty(id)) errors.Add(b + ": no id");
                else if (!eventIds.Add(id)) errors.Add(b + ": the id '" + id + "' is used twice in this window");
                if (string.IsNullOrEmpty(label)) errors.Add(b + ": no label");
                else if (label.Length > 18) warnings.Add(b + ": the label '" + label + "' is " + label.Length + " characters; keep it under 18");
            }
        }

        private static void CheckGrid(JObject c, string at, HashSet<string> eventIds, List<string> errors)
        {
            var cols = c["columns"];
            if (cols == null || !IsNum(cols) || (double)cols < 1 || (double)cols > MaxGridColumns || (double)cols != Math.Floor((double)cols))
                errors.Add(at + ": columns must be 1..7");
            if (!(c["cells"] is JArray cells) || cells.Count == 0)
            {
                errors.Add(at + ": cells must be a list like cells={ {text=\"Mon\"}, {id=\"d1\", text=\"1\"} }");
                return;
            }
            if (cells.Count > MaxCells) errors.Add(at + ": " + cells.Count + " cells; the most is " + MaxCells);
            for (int i = 0; i < cells.Count; i++)
            {
                string b = at + " cell " + (i + 1);
                if (!(cells[i] is JObject cell)) { errors.Add(b + ": must be a table like {id=\"d1\", text=\"1\"}"); continue; }
                string id = Str(cell["id"]);
                if (!string.IsNullOrEmpty(id) && !eventIds.Add(id)) errors.Add(b + ": the id '" + id + "' is used twice in this window");
            }
        }

        private static bool IsNum(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        private static string Str(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Array || t.Type == JTokenType.Object ? null : (string)t;

        private static string Fmt(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
