using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace MetaCodex.Scripting
{
    /// Checks a view spec ({title, components, columns?, size?}) before a plugin window is built from it. No Unity
    /// types: the host (ui.panel) and a PC harness use the same rules. Component numbers in messages start at 1.
    public static class ViewSpecCheck
    {
        public const int MaxComponents = 16, MaxButtons = 12, MaxCells = 49, MaxGridColumns = 7;
        /// The component library: every component of a window counted with those in its cards, rows and tabs; the
        /// children of one card or tab, of one row; tabs; options of a select, radio, segmented; canvas limits (the
        /// shapes, the corners of one polyline or polygon and of all shapes, the texts: each text is its own mesh).
        public const int MaxAllComponents = 48, MaxChildren = 12, MaxRowChildren = 6, MaxTabs = 6, MaxSelectOptions = 200,
            MaxRadioOptions = 8, MaxListItems = 100, MaxCanvasShapes = 120, MaxShapePoints = 64, MaxCanvasPoints = 1200, MaxCanvasTexts = 40;

        /// Every component type the app draws (aliases included).
        public static readonly string[] Types =
        {
            "text", "markdown", "note", "stats", "kpi", "chart", "graph", "flowchart", "mermaid", "sequence", "diagram",
            "table", "code", "diff", "log", "progress", "checklist", "todo", "timeline", "kanban", "kv", "list",
            "choices", "buttons", "toggle", "grid", "input", "image", "scene3d", "physics",
            // the component library (ShellViewControls)
            "checkbox", "radio", "segmented", "select", "slider", "stepper", "field", "date", "time", "color",
            "canvas", "card", "row", "tabs", "divider", "spacer",
        };

        private static readonly HashSet<string> LibraryTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "checkbox", "radio", "segmented", "select", "slider", "stepper", "field", "date", "time", "color",
            "canvas", "card", "row", "tabs", "divider", "spacer",
        };

        private static readonly HashSet<string> ShapeTypes = new HashSet<string>(StringComparer.Ordinal) { "rect", "circle", "line", "polyline", "polygon", "text", "arc" };

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
            int all = 0;
            CheckList(comps, "", 0, false, compIds, eventIds, ref all, errors, warnings);
            if (all > MaxAllComponents) errors.Add(all + " components with those in cards, rows and tabs; the most is " + MaxAllComponents);
        }

        private static void CheckList(JArray comps, string where, int depth, bool inTab, HashSet<string> compIds, HashSet<string> eventIds, ref int all, List<string> errors, List<string> warnings)
        {
            for (int i = 0; i < comps.Count; i++)
            {
                all++;
                string at = (where.Length > 0 ? where + " > " : "component ") + (i + 1);
                if (!(comps[i] is JObject c)) { errors.Add(at + ": must be a table like {type=\"text\", text=\"...\"}"); continue; }
                string type = Str(c["type"]);
                if (string.IsNullOrEmpty(type)) { errors.Add(at + ": no type (one of " + string.Join(", ", Types) + ")"); continue; }
                at += " (" + type + ")";
                if (!IsKnownType(type)) { errors.Add(at + ": unknown type '" + type + "' (use " + string.Join(", ", Types) + ")"); continue; }

                string id = Str(c["id"]);
                if (!string.IsNullOrEmpty(id) && !compIds.Add(id)) errors.Add(at + ": the id '" + id + "' is used twice in this window");
                if (depth > 0 && (type == "scene3d" || type == "physics" || type == "image"))
                    errors.Add(at + ": a " + type + " cannot be inside a card, row or tab; put it at the top level of components");

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
                    case "list":
                        CheckPickList(c, at, eventIds, errors);
                        break;
                    default:
                        if (LibraryTypes.Contains(type)) CheckLibrary(c, type, id, at, depth, inTab, compIds, eventIds, ref all, errors, warnings);
                        break;
                }
            }
        }

        private static string Form(string type)
        {
            switch (type)
            {
                case "checkbox": return "{type=\"checkbox\", id=\"done\", label=\"Done\", value=false}";
                case "radio": return "{type=\"radio\", id=\"size\", options={\"S\", \"M\", \"L\"}, value=\"M\"}";
                case "segmented": return "{type=\"segmented\", id=\"view\", options={\"Day\", \"Week\", \"Month\"}, value=\"Week\"}";
                case "select": return "{type=\"select\", id=\"city\", label=\"City\", options={\"Rome\", \"Oslo\"}, value=\"Rome\"}";
                case "slider": return "{type=\"slider\", id=\"vol\", label=\"Volume\", min=0, max=100, step=5, value=40, unit=\"%\"}";
                case "stepper": return "{type=\"stepper\", id=\"mins\", label=\"Minutes\", min=1, max=90, step=1, value=25}";
                case "field": return "{type=\"field\", id=\"name\", label=\"Name\", value=\"\", placeholder=\"your name\"}";
                case "date": return "{type=\"date\", id=\"day\", value=\"2026-11-20\"} (time=true: value=\"2026-11-20T09:30\")";
                case "time": return "{type=\"time\", id=\"at\", label=\"At\", value=\"09:30\", step=5}";
                case "color": return "{type=\"color\", id=\"tint\", value=\"#3987E5\"}";
                case "canvas": return "{type=\"canvas\", id=\"map\", width=100, height=60, shapes={ {id=\"a\", type=\"rect\", x=5, y=5, w=40, h=30, label=\"Kitchen\"} }}";
                case "card": return "{type=\"card\", title=\"Alarm\", components={ ... }}";
                case "row": return "{type=\"row\", components={ {type=\"stepper\", ...}, {type=\"buttons\", ...} }}";
                case "tabs": return "{type=\"tabs\", id=\"pages\", value=\"a\", tabs={ {id=\"a\", label=\"Today\", components={ ... }}, {id=\"b\", label=\"Week\", components={ ... }} }}";
            }
            return "";
        }

        private static bool NeedId(string type) => type != "card" && type != "row" && type != "divider" && type != "spacer";

        private static void CheckLibrary(JObject c, string type, string id, string at, int depth, bool inTab, HashSet<string> compIds, HashSet<string> eventIds, ref int all, List<string> errors, List<string> warnings)
        {
            string form = Form(type);
            if (NeedId(type) && string.IsNullOrEmpty(id)) errors.Add(at + ": needs an id (its events carry it), for example " + form);
            switch (type)
            {
                case "checkbox":
                    if (string.IsNullOrEmpty(Str(c["label"]))) errors.Add(at + ": needs a label, for example " + form);
                    if (c["value"] != null && c["value"].Type != JTokenType.Boolean) errors.Add(at + ": value must be true or false");
                    break;
                case "radio": case "segmented": case "select":
                {
                    int min = type == "select" ? 1 : 2, max = type == "radio" ? MaxRadioOptions : type == "segmented" ? 5 : MaxSelectOptions;
                    if (!(c["options"] is JArray o) || o.Count < min) { errors.Add(at + ": options must be a list of " + min + " or more, for example " + form); break; }
                    if (o.Count > max) errors.Add(at + ": " + o.Count + " options; the most is " + max + (type == "segmented" ? " (more: use a select)" : type == "radio" ? " (more: use a select)" : ""));
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var opt in o)
                    {
                        string v = opt is JObject jo ? Str(jo["value"]) ?? Str(jo["id"]) ?? Str(jo["label"]) ?? Str(jo["text"]) : Str(opt);
                        if (string.IsNullOrEmpty(v)) { errors.Add(at + ": an option is empty"); continue; }
                        if (!values.Add(v)) errors.Add(at + ": the option '" + v + "' is there twice");
                    }
                    string cur = Str(c["value"]);
                    if (cur != null && cur.Length > 0 && !values.Contains(cur)) warnings.Add(at + ": value '" + cur + "' is not one of its options");
                    break;
                }
                case "slider":
                {
                    if (!IsNum(c["min"]) || !IsNum(c["max"])) { errors.Add(at + ": min and max must be numbers, for example " + form); break; }
                    double mn = (double)c["min"], mx = (double)c["max"];
                    if (mx <= mn) errors.Add(at + ": max must be greater than min");
                    if (c["step"] != null && (!IsNum(c["step"]) || (double)c["step"] <= 0)) errors.Add(at + ": step must be a number above 0");
                    if (c["value"] != null && (!IsNum(c["value"]) || (double)c["value"] < mn || (double)c["value"] > mx)) errors.Add(at + ": value must be a number from min to max");
                    break;
                }
                case "stepper":
                    foreach (var k in new[] { "min", "max", "step", "value" })
                        if (c[k] != null && !IsNum(c[k])) errors.Add(at + ": " + k + " must be a number, for example " + form);
                    if (IsNum(c["step"]) && (double)c["step"] <= 0) errors.Add(at + ": step must be above 0");
                    if (IsNum(c["min"]) && IsNum(c["max"]) && (double)c["max"] < (double)c["min"]) errors.Add(at + ": max must not be below min");
                    break;
                case "date":
                {
                    bool time = c["time"] != null && c["time"].Type == JTokenType.Boolean && (bool)c["time"];
                    foreach (var k in new[] { "value", "min", "max" })
                    {
                        string d = Str(c[k]);
                        if (d == null) continue;
                        bool okDate = d.Length >= 10 && DateTime.TryParseExact(d.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                        bool okRest = d.Length == 10 || k == "value" && d.Length == 16 && (d[10] == 'T') && IsTime(d.Substring(11));
                        if (!okDate || !okRest) errors.Add(at + ": " + k + " must be \"YYYY-MM-DD\"" + (k == "value" ? " (with time=true: \"YYYY-MM-DDTHH:MM\")" : "") + ", not '" + d + "'");
                    }
                    if (c["minute_step"] != null && (!IsNum(c["minute_step"]) || (double)c["minute_step"] < 1 || (double)c["minute_step"] > 30)) errors.Add(at + ": minute_step must be 1..30");
                    if (!time && Str(c["value"])?.Length == 16) warnings.Add(at + ": value has a time but time is not true");
                    break;
                }
                case "time":
                    if (c["value"] != null && !IsTime(Str(c["value"]))) errors.Add(at + ": value must be \"HH:MM\" (24 hours), not '" + Str(c["value"]) + "'");
                    if (c["step"] != null && (!IsNum(c["step"]) || (double)c["step"] < 1 || (double)c["step"] > 30)) errors.Add(at + ": step (minutes) must be 1..30");
                    break;
                case "color":
                    if (c["options"] != null)
                    {
                        if (!(c["options"] is JArray co) || co.Count == 0 || co.Count > 24) errors.Add(at + ": options must be a list of 1 to 24 colours like \"#3987E5\"");
                        else foreach (var x in co) if (!IsHex(Str(x))) errors.Add(at + ": the option '" + Str(x) + "' is not a colour like \"#3987E5\"");
                    }
                    if (c["value"] != null && !IsHex(Str(c["value"]))) errors.Add(at + ": value must be a colour like \"#3987E5\"");
                    break;
                case "field":
                    if (c["value"] != null && c["value"].Type != JTokenType.String) errors.Add(at + ": value must be a text");
                    break;
                case "canvas":
                    CheckCanvas(c, at, errors, warnings);
                    break;
                case "card": case "row":
                {
                    int max = type == "row" ? MaxRowChildren : MaxChildren;
                    if (!(c["components"] is JArray kids) || kids.Count == 0) { errors.Add(at + ": components must be a list of the components inside it, for example " + form); break; }
                    if (kids.Count > max) errors.Add(at + ": " + kids.Count + " components inside; the most is " + max);
                    if (depth >= 3) { errors.Add(at + ": too deep (cards, rows and tabs nest 3 levels at most)"); break; }
                    CheckList(kids, at, depth + 1, inTab, compIds, eventIds, ref all, errors, warnings);
                    break;
                }
                case "tabs":
                {
                    if (inTab) { errors.Add(at + ": tabs cannot be inside tabs"); break; }
                    if (!(c["tabs"] is JArray tabs) || tabs.Count < 2) { errors.Add(at + ": tabs must be a list of 2 or more, for example " + form); break; }
                    if (tabs.Count > MaxTabs) errors.Add(at + ": " + tabs.Count + " tabs; the most is " + MaxTabs);
                    var tabIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int k = 0; k < tabs.Count; k++)
                    {
                        string tat = at + " tab " + (k + 1);
                        if (!(tabs[k] is JObject tb)) { errors.Add(tat + ": must be a table like {id=\"a\", label=\"Today\", components={...}}"); continue; }
                        string tid = Str(tb["id"]);
                        if (string.IsNullOrEmpty(tid)) errors.Add(tat + ": no id");
                        else if (!tabIds.Add(tid)) errors.Add(tat + ": the tab id '" + tid + "' is used twice");
                        if (string.IsNullOrEmpty(Str(tb["label"]))) errors.Add(tat + ": no label");
                        else if (Str(tb["label"]).Length > 14) warnings.Add(tat + ": the label '" + Str(tb["label"]) + "' is long; keep tab labels under 14 characters");
                        if (!(tb["components"] is JArray kids) || kids.Count == 0) { errors.Add(tat + ": components must be a list (what the tab shows)"); continue; }
                        if (kids.Count > MaxChildren) errors.Add(tat + ": " + kids.Count + " components; the most is " + MaxChildren);
                        CheckList(kids, tat, depth + 1, true, compIds, eventIds, ref all, errors, warnings);
                    }
                    string cur = Str(c["value"]);
                    if (cur != null && !tabIds.Contains(cur)) warnings.Add(at + ": value '" + cur + "' is not one of its tab ids");
                    break;
                }
            }
        }

        private static void CheckPickList(JObject c, string at, HashSet<string> eventIds, List<string> errors)
        {
            if (!(c["items"] is JArray items)) return; // the old read-only list may also be an object
            bool pick = false;
            foreach (var x in items) if (x is JObject o && o["id"] != null) { pick = true; break; }
            if (!pick) return;
            if (items.Count > MaxListItems) errors.Add(at + ": " + items.Count + " items; the most is " + MaxListItems);
            for (int i = 0; i < items.Count; i++)
            {
                string b = at + " item " + (i + 1);
                if (!(items[i] is JObject it)) { errors.Add(b + ": in a list with ids every item is a table like {id=\"milk\", text=\"Milk\"}"); continue; }
                string id = Str(it["id"]);
                if (string.IsNullOrEmpty(id)) errors.Add(b + ": no id (a list with ids needs one on every row)");
                else if (!eventIds.Add(id)) errors.Add(b + ": the id '" + id + "' is used twice in this window");
                if (string.IsNullOrEmpty(Str(it["text"]) ?? Str(it["label"]))) errors.Add(b + ": no text");
            }
        }

        private static void CheckCanvas(JObject c, string at, List<string> errors, List<string> warnings)
        {
            string fit = Str(c["fit"]);
            if (fit != null && fit != "lonlat") errors.Add(at + ": fit must be \"lonlat\" or left out");
            if (fit == "lonlat")
            {
                if (!(c["bounds"] is JArray b) || b.Count != 4 || !IsNum(b[0]) || !IsNum(b[1]) || !IsNum(b[2]) || !IsNum(b[3]))
                    errors.Add(at + ": fit=\"lonlat\" needs bounds={west, south, east, north} (degrees)");
                else if ((double)b[2] <= (double)b[0] || (double)b[3] <= (double)b[1]) errors.Add(at + ": bounds must be {west, south, east, north} with east > west and north > south");
            }
            else
                foreach (var k in new[] { "width", "height" })
                    if (c[k] != null && (!IsNum(c[k]) || (double)c[k] <= 0)) errors.Add(at + ": " + k + " is the canvas's size in its own units: a number above 0");
            if (!(c["shapes"] is JArray shapes)) { errors.Add(at + ": shapes must be a list, for example " + Form("canvas")); return; }
            if (shapes.Count > MaxCanvasShapes) errors.Add(at + ": " + shapes.Count + " shapes; the most is " + MaxCanvasShapes);
            int points = 0, texts = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < shapes.Count; i++)
            {
                string b = at + " shape " + (i + 1);
                if (!(shapes[i] is JObject s)) { errors.Add(b + ": must be a table like {type=\"rect\", x=0, y=0, w=10, h=10}"); continue; }
                string type = Str(s["type"]);
                if (type == null || !ShapeTypes.Contains(type)) { errors.Add(b + ": type must be rect, circle, line, polyline, polygon, text or arc"); continue; }
                b += " (" + type + ")";
                string id = Str(s["id"]);
                if (!string.IsNullOrEmpty(id) && !ids.Add(id)) errors.Add(b + ": the shape id '" + id + "' is used twice in this canvas");
                string[] need = type == "rect" ? new[] { "x", "y" } : type == "circle" || type == "arc" ? new[] { "x", "y", "r" } : type == "text" ? new[] { "x", "y" } : new string[0];
                foreach (var k in need) if (!IsNum(s[k])) errors.Add(b + ": " + k + " must be a number");
                if (type == "rect" && !IsNum(s["w"] ?? s["width"])) errors.Add(b + ": w and h (its size in canvas units) must be numbers");
                if (type == "text") { texts++; if (string.IsNullOrEmpty(Str(s["text"]))) errors.Add(b + ": no text"); }
                if (type == "arc" && (s["from"] != null && !IsNum(s["from"]) || s["to"] != null && !IsNum(s["to"]))) errors.Add(b + ": from and to are angles in degrees (0 = up, clockwise)");
                if (type == "line" && s["points"] == null)
                {
                    foreach (var k in new[] { "x1", "y1", "x2", "y2" }) if (!IsNum(s[k])) { errors.Add(b + ": a line needs x1, y1, x2, y2 (or points)"); break; }
                    points += 2;
                }
                else if (type == "line" || type == "polyline" || type == "polygon")
                {
                    int min = type == "polygon" ? 3 : 2;
                    if (!(s["points"] is JArray pts) || pts.Count < min) { errors.Add(b + ": points must be a list of " + min + " or more {x, y}, for example points={ {0,0}, {10,0}, {10,10} }"); continue; }
                    if (pts.Count > MaxShapePoints) errors.Add(b + ": " + pts.Count + " points; the most is " + MaxShapePoints + " a shape (simplify it)");
                    foreach (var pt in pts)
                        if (!(pt is JArray pa && pa.Count >= 2 && IsNum(pa[0]) && IsNum(pa[1])) && !(pt is JObject po && IsNum(po["x"] ?? po["lon"]) && IsNum(po["y"] ?? po["lat"])))
                        { errors.Add(b + ": every point is {x, y} (two numbers)"); break; }
                    points += pts.Count;
                }
                else points += type == "rect" ? 2 : 1;
                if (s["stroke_width"] != null && (!IsNum(s["stroke_width"]) || (double)s["stroke_width"] <= 0 || (double)s["stroke_width"] > 8)) errors.Add(b + ": stroke_width is in millimetres: 0.5 to 8");
                if (s["opacity"] != null && (!IsNum(s["opacity"]) || (double)s["opacity"] < 0 || (double)s["opacity"] > 1)) errors.Add(b + ": opacity must be 0..1");
            }
            if (points > MaxCanvasPoints) errors.Add(at + ": " + points + " points in all; the most is " + MaxCanvasPoints);
            if (texts > MaxCanvasTexts) errors.Add(at + ": " + texts + " texts; the most is " + MaxCanvasTexts + " (use labels on fewer shapes)");
            string sel = Str(c["selected"]);
            if (!string.IsNullOrEmpty(sel) && !ids.Contains(sel)) warnings.Add(at + ": selected '" + sel + "' is not the id of one of its shapes");
        }

        private static bool IsTime(string s)
        {
            if (s == null || s.Length != 5 || s[2] != ':') return false;
            return int.TryParse(s.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int h) && h <= 23
                && int.TryParse(s.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m <= 59;
        }

        private static bool IsHex(string s)
        {
            if (s == null || s.Length != 7 || s[0] != '#') return false;
            for (int i = 1; i < 7; i++) if (Uri.IsHexDigit(s[i]) == false) return false;
            return true;
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
