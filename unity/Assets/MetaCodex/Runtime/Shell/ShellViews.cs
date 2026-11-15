using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Agent views: an agent shows charts, diagrams, tables, checklists, choices or a small physics scene as a
    /// panel. Two ways in, one spec (skill `quest-views`):
    ///   - in a thread message, a fenced block ```mcx-view {json}``` or ```mermaid ...``` (shown when it arrives;
    ///     the THREAD workspace lists them under VIEWS);
    ///   - from Linux, `mcx-ui view show spec.json` (control port `view ...`), e.g. a script that updates a chart.
    /// A view with the same id replaces the previous one in place (live updates). Choices and inputs answer back:
    /// into the thread the view came from, or to `mcx-ui view wait <id>`.
    public sealed partial class MetaCodexShell
    {
        public sealed class AgentView
        {
            public string Id, Title, ThreadId, Source, Error, Answer;
            public JObject Spec;
            public long Seq;
            public DateTime At = DateTime.Now;
            public readonly List<Action<string>> Waiters = new List<Action<string>>();
            public string PanelId => "view-" + Id;
            /// Interactive components (choices, input, buttons, toggle, grid cells) call this with (type, id, value) instead of
            /// answering the thread. Set for plugin windows (ShellPluginUi).
            public Action<string, string, string> OnEvent;
            /// LIVE text components (a `text` with an "id") of the built panel: component id -> its label (cleared at each build).
            public readonly Dictionary<string, UiLabel> Live = new Dictionary<string, UiLabel>();
            /// Plugin windows: the open state last reported to the host (Opened / Closed events of the panel).
            public bool Shown;
        }

        private readonly List<AgentView> _viewList = new List<AgentView>();
        private readonly Dictionary<string, ViewStage> _stages = new Dictionary<string, ViewStage>();
        public IReadOnlyList<AgentView> Views => _viewList;
        public AgentView FindView(string id) => _viewList.FirstOrDefault(v => v.Id == id);
        private int _viewCounter;
        private const int MaxViewPanels = 12;

        public const string ViewSchema =
            "view: {id?, title, size?:[w,h] m, columns?:1|2, components:[...]} or one component.\n" +
            "components (type): text {text: markdown-lite} | stats {items:[{label,value,delta?,tone?}]} |\n" +
            "  chart {kind: bar|hbar|line|area|scatter|donut|pie|sparkline, labels:[..], series:[{name,data:[..]}] | data:[..], unit?, min?, max?}\n" +
            "  graph {mermaid:\"graph LR; A-->B\"} or {direction:TD|LR, nodes:[{id,label,shape:rect|round|diamond|circle,tone}], edges:[{from,to,label,style:solid|dotted|thick}]}\n" +
            "  mermaid {text} (flowchart, sequenceDiagram, pie) | table {columns:[..], rows:[[..]]} | code {text, lang?} | diff {text: unified diff}\n" +
            "  progress {items:[{label,value 0..1,tone?}]} | checklist {items:[{text,done}]} | timeline {items:[{time,text,tone}]}\n" +
            "  kanban {columns:[{title,cards:[..]}]} | kv {items:[{key,value}]} | choices {prompt?, options:[..]} | input {prompt?, placeholder?}\n" +
            "  buttons {items:[{id,label,tone?}]} | toggle {id,label,value} | grid {columns:1..7, cells:[{id?,text,sub?,tone?}]} | text {text, style?:big}\n" +
            "  image {path: /root/plot.png} | scene3d {size:[w,d], gravity:[0,-9.81,0], bodies:[{shape,size,pos,vel,mass,color,bounce,count}]}\n" +
            "tones: ok warn error accent dim, or #RRGGBB. Answers (choices/input) go to the thread or `mcx-ui view wait <id>`.";

        // ------------------------------------------------------------------ blocks in messages

        private static readonly Regex Fence = new Regex(@"```[ \t]*(mcx-view|mcxview|view|mermaid)[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

        /// Views written in a message: ```mcx-view {json}``` and ```mermaid ...``` blocks, in order.
        public static List<(JObject spec, string error)> ExtractViewBlocks(string text)
        {
            var list = new List<(JObject, string)>();
            if (string.IsNullOrEmpty(text) || text.IndexOf("```", StringComparison.Ordinal) < 0) return list;
            foreach (Match m in Fence.Matches(text))
            {
                string lang = m.Groups[1].Value.ToLowerInvariant(), body = m.Groups[2].Value.Trim();
                if (lang == "mermaid")
                {
                    list.Add((new JObject { ["title"] = MermaidTitle(body), ["components"] = new JArray { new JObject { ["type"] = "mermaid", ["text"] = body } } }, null));
                    continue;
                }
                try
                {
                    var tok = JToken.Parse(body);
                    if (tok is JObject o) list.Add((o, null));
                    else if (tok is JArray a) list.Add((new JObject { ["components"] = a }, null));
                    else list.Add((null, "a view must be a JSON object"));
                }
                catch (Exception ex) { list.Add((null, "view JSON: " + ex.Message.Split('\n')[0])); }
            }
            return list;
        }

        private static string MermaidTitle(string body)
        {
            var k = UiMermaid.Kind(body);
            var tm = Regex.Match(body, @"^\s*title\s+(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (tm.Success) return tm.Groups[1].Value.Trim();
            return k == "sequence" ? "Sequence" : k == "pie" ? "Pie" : "Diagram";
        }

        /// The message text with view blocks replaced by a short marker (the thread timeline shows that).
        public static string StripViewBlocks(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("```", StringComparison.Ordinal) < 0) return text;
            return Fence.Replace(text, m =>
            {
                string title = "view";
                if (m.Groups[1].Value.ToLowerInvariant() == "mermaid") title = MermaidTitle(m.Groups[2].Value);
                else { try { title = (string)JObject.Parse(m.Groups[2].Value)["title"] ?? "view"; } catch (Exception) { title = "view (invalid JSON)"; } }
                return "[▣ " + title + "]";
            });
        }

        /// Registers (and opens, when `open`) the views of one agent message.
        public void ViewsFromMessage(string threadId, long seq, string text, bool open)
        {
            var blocks = ExtractViewBlocks(text);
            for (int k = 0; k < blocks.Count; k++)
            {
                var (spec, error) = blocks[k];
                if (spec == null) { Log("view in thread " + threadId + ": " + error); if (open) Toast("Agent view not shown: " + error, "warn"); continue; }
                if (spec["id"] == null) spec["id"] = $"{ShortId(threadId)}-{seq}-{k}";
                var existing = FindView(SafeViewId((string)spec["id"]));
                if (existing != null && existing.ThreadId == threadId && existing.Seq == seq && !open) continue; // seen
                ShowView(spec, threadId, seq, "thread", open && Settings.viewsAutoOpen);
            }
        }

        private static string ShortId(string id) => string.IsNullOrEmpty(id) ? "x" : id.Length > 6 ? id.Substring(id.Length - 6) : id;

        public static string SafeViewId(string id)
        {
            var s = Regex.Replace((id ?? "").ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            return s.Length == 0 ? "view" : s.Length > 40 ? s.Substring(0, 40) : s;
        }

        // ------------------------------------------------------------------ show / build

        /// Shows a view spec (a whole view or a single component); returns it (Error set when the spec was bad).
        public AgentView ShowView(JObject spec, string threadId = null, long seq = 0, string source = "control", bool open = true)
        {
            spec = spec ?? new JObject();
            if (spec["components"] == null && spec["type"] != null) spec = new JObject { ["id"] = spec["id"], ["title"] = spec["title"] ?? spec["type"], ["components"] = new JArray { spec } };
            string id = SafeViewId((string)spec["id"] ?? ("v" + (++_viewCounter)));
            var v = FindView(id);
            if (v == null) { v = new AgentView { Id = id }; _viewList.Add(v); }
            v.Spec = spec;
            v.Title = (string)spec["title"] ?? id;
            v.ThreadId = threadId ?? v.ThreadId;
            v.Seq = seq;
            v.Source = source;
            v.At = DateTime.Now;
            v.Error = null;
            try { BuildViewPanel(v, open); }
            catch (Exception ex) { v.Error = ex.Message; Log("view " + id + " failed: " + ex); if (source != "plugin") Toast("Agent view failed: " + ex.Message, "error"); }
            TrimViewPanels();
            RefreshWorkspaceViews();
            return v;
        }

        /// Keeps the newest view panels; older ones are destroyed (their specs stay listed and rebuild on open).
        private void TrimViewPanels()
        {
            var built = _viewList.Where(x => _panels.ContainsKey(x.PanelId)).OrderByDescending(x => x.At).ToList();
            foreach (var old in built.Skip(MaxViewPanels))
            {
                if (_panels.TryGetValue(old.PanelId, out var p) && p.IsOpen) continue;
                DestroyViewPanel(old.PanelId);
            }
        }

        private void DestroyViewPanel(string pid)
        {
            if (!_panels.TryGetValue(pid, out var p)) return;
            Router.Unregister(p);
            _panels.Remove(pid);
            _placed.Remove(pid);
            _slots.Remove(p.transform);
            _stages.Remove(pid);
            UiToasts.Kill(p.gameObject);
        }

        public bool OpenView(string id)
        {
            var v = FindView(id);
            if (v == null) return false;
            if (!_panels.ContainsKey(v.PanelId)) BuildViewPanel(v, true);
            else OpenPanel(v.PanelId);
            return true;
        }

        private static List<JObject> ViewComponents(JObject spec) =>
            (spec["components"] as JArray ?? new JArray()).OfType<JObject>().Take(16).ToList();

        private void BuildViewPanel(AgentView v, bool open)
        {
            var t = UiTheme.Current;
            string pid = v.PanelId;
            bool had = _panels.TryGetValue(pid, out var old);
            Vector3 pos = default; Quaternion rot = default; bool wasOpen = false;
            if (had) { pos = old.transform.position; rot = old.transform.rotation; wasOpen = old.IsOpen; DestroyViewPanel(pid); }
            v.Live.Clear();
            bool plugin = v.Source == "plugin";
            var comps = ViewComponents(v.Spec);
            int columns = Mathf.Clamp(I(v.Spec["columns"], 1), 1, 3);
            var size = Fl(v.Spec["size"]);
            float w = size != null && size.Length > 0 ? Mathf.Clamp(size[0], 0.3f, 1.2f) : comps.Any(c => Is(c, "kanban", "table", "graph", "mermaid", "flowchart", "sequence")) ? 0.68f : 0.58f;
            float innerW = w - 2 * t.Padding;
            float colW = (innerW - t.Gap * (columns - 1)) / columns;
            // rows: up to `columns` components side by side; wide ones (diagrams, tables, boards, code, scenes, or
            // "wide": true) take a row of their own
            bool Wide(JObject c) => columns > 1 && (c["wide"]?.Type == JTokenType.Boolean ? (bool)c["wide"] : Is(c, "graph", "flowchart", "mermaid", "sequence", "diagram", "table", "kanban", "code", "diff", "log", "scene3d", "physics", "timeline"));
            var rows = new List<List<int>>();
            for (int i = 0; i < comps.Count; i++)
            {
                if (Wide(comps[i])) { rows.Add(new List<int> { i }); continue; }
                if (rows.Count == 0 || rows[rows.Count - 1].Count >= columns || Wide(comps[rows[rows.Count - 1][0]])) rows.Add(new List<int>());
                rows[rows.Count - 1].Add(i);
            }
            var heights = comps.Select((c, i) => DesiredHeight(c, Wide(c) ? innerW : colW)).ToList();
            var rowsH = rows.Select(rw => rw.Select(i => heights[i]).DefaultIfEmpty(0.05f).Max()).ToList();
            float contentH = rowsH.Sum() + t.Gap * Mathf.Max(0, rowsH.Count - 1);
            float h = size != null && size.Length > 1 ? Mathf.Clamp(size[1], 0.18f, 0.9f) : Mathf.Clamp(contentH + t.HeaderHeight + 2 * t.Padding, 0.2f, 0.86f);
            var p = Panel<UiPanel>(pid, plugin ? v.Title : "View · " + v.Title, w, h);
            if (plugin) HookPluginWindow(v, p); // before any open below: its Opened event reaches the plugin
            p.BaseScale = Vector3.one * Mathf.Clamp(Settings.uiScale, 0.6f, 1.8f);
            var c0 = p.Content;
            float k = contentH > c0.height ? (c0.height - t.Gap * Mathf.Max(0, rowsH.Count - 1)) / Mathf.Max(0.01f, rowsH.Sum()) : 1f;
            float y = c0.y;
            for (int r = 0; r < rows.Count; r++)
            {
                float rh = rowsH[r] * k;
                var rw = rows[r];
                bool wide = rw.Count == 1 && Wide(comps[rw[0]]);
                for (int col = 0; col < rw.Count; col++)
                {
                    int i = rw[col];
                    var rect = wide ? new Rect(c0.x, y, innerW, rh) : new Rect(c0.x + col * (colW + t.Gap), y, colW, rh);
                    try { AddViewComponent(p, v, comps[i], rect); }
                    catch (Exception ex) { p.Add(new UiLabel("⚠ " + (string)comps[i]["type"] + ": " + ex.Message, t.SmallSize) { Rect = rect, Color = t.Danger, Wrap = true }); }
                }
                y += rh + t.Gap;
            }
            if (comps.Count == 0) p.Add(new UiLabel("(this view has no components)", t.SmallSize) { Rect = c0, Color = t.TextDim, Align = TextAlignmentOptions.Center });
            if (had)
            {
                p.transform.SetPositionAndRotation(pos, rot);
                _placed.Add(pid);
                if (wasOpen || open) { p.gameObject.SetActive(true); p.ForceRefresh(); OnOpenedFloating(p); }
            }
            else if (open) OpenPanel(pid);
            if (plugin) PluginShownSync(v, p); // a rebuild that opened a panel the user had closed
        }

        private static bool Is(JObject c, params string[] types) => types.Contains(((string)c["type"] ?? "").ToLowerInvariant());

        private float DesiredHeight(JObject c, float width)
        {
            float explicitH = Fl1(c["height"], -1f);
            if (explicitH > 0) return Mathf.Clamp(explicitH, 0.03f, 0.8f);
            string type = ((string)c["type"] ?? "text").ToLowerInvariant();
            // a title row above the content (charts draw their own; choices / input use it as the prompt)
            bool titled = !string.IsNullOrEmpty((string)c["title"]) && !Is(c, "chart", "choices", "buttons", "input", "text", "markdown", "note", "scene3d", "physics");
            return BaseHeight(c, type, width) + (titled ? 0.02f : 0f);
        }

        private float BaseHeight(JObject c, string type, float width)
        {
            int Count(string key) => (c[key] as JArray)?.Count ?? 0;
            switch (type)
            {
                case "stats": case "kpi": return 0.07f * Mathf.CeilToInt(Mathf.Max(1, Count("items")) / 4f);
                case "chart": return ((string)c["kind"] ?? "") == "sparkline" ? 0.06f : ((string)c["kind"] ?? "") == "hbar" ? Mathf.Clamp(0.03f + 0.026f * Mathf.Max(1, (c["data"] as JArray)?.Count ?? (c["labels"] as JArray)?.Count ?? 4), 0.08f, 0.32f) : 0.2f;
                case "graph": case "flowchart": case "mermaid": case "sequence": case "diagram": return 0.26f;
                case "table": return Mathf.Clamp(0.024f * (Count("rows") + 1) + 0.014f, 0.06f, 0.3f);
                case "code": case "diff": case "log": return Mathf.Clamp(0.0168f * (((string)c["text"] ?? "").Split('\n').Length) + 0.022f, 0.06f, 0.3f);
                case "progress": return 0.028f * Mathf.Max(1, Count("items"));
                case "checklist": case "todo": return 0.025f * Mathf.Max(1, Count("items"));
                case "timeline": return 0.027f * Mathf.Max(1, Count("items"));
                case "kanban": return 0.22f;
                case "kv": case "list": return 0.024f * Mathf.Max(1, Count("items") > 0 ? Count("items") : (c["items"] as JObject)?.Count ?? 1);
                case "choices": case "buttons": return 0.02f + 0.04f * Mathf.CeilToInt(Mathf.Max(1, type == "buttons" && Count("items") > 0 ? Count("items") : Count("options")) / 4f);
                case "toggle": return UiTheme.Current.ButtonHeight + 0.008f;
                case "grid":
                {
                    bool anySub = (c["cells"] as JArray)?.Any(x => x is JObject o && !string.IsNullOrEmpty((string)o["sub"])) ?? false;
                    int cols = Mathf.Clamp(I(c["columns"], 1), 1, 7);
                    int rowsN = Mathf.CeilToInt(Mathf.Clamp(Count("cells"), 1, 49) / (float)cols);
                    return rowsN * (anySub ? 0.044f : 0.034f);
                }
                case "input": return 0.06f;
                case "scene3d": case "physics": return 0.04f;
                case "image": return 0.22f;
                default:
                {
                    if ((string)c["style"] == "big") return 0.05f;
                    string text = (string)c["text"] ?? (string)c["markdown"] ?? "";
                    int chars = Mathf.Max(20, (int)(width / (UiTheme.Current.SmallSize * 1.1f * 0.42f)));
                    int lines = text.Replace("\r", "").Split('\n').Sum(l => Mathf.Max(1, Mathf.CeilToInt(l.Length / (float)chars)));
                    return Mathf.Clamp(lines * 0.0158f + 0.008f, 0.024f, 0.36f);
                }
            }
        }

        private void AddViewComponent(UiPanel p, AgentView v, JObject c, Rect r)
        {
            var t = UiTheme.Current;
            string type = ((string)c["type"] ?? "text").ToLowerInvariant();
            string title = (string)c["title"];
            switch (type)
            {
                case "chart":
                {
                    var ch = new UiChart { Rect = r, Kind = ((string)c["kind"] ?? "bar").ToLowerInvariant(), Title = title ?? "", Unit = (string)c["unit"] ?? "" };
                    FillChart(ch, c);
                    p.Add(ch);
                    break;
                }
                case "graph": case "flowchart": case "mermaid": case "sequence": case "diagram":
                {
                    string src = (string)c["mermaid"] ?? (type == "mermaid" || type == "sequence" ? (string)c["text"] : null);
                    var area = r;
                    if (!string.IsNullOrEmpty(title)) { p.Add(new UiLabel("<b>" + UiViz.NoParse(title) + "</b>", 0.0115f) { Rect = new Rect(r.x, r.y, r.width, 0.018f) }); area = new Rect(r.x, r.y + 0.02f, r.width, r.height - 0.02f); }
                    if (src != null)
                    {
                        string kind = UiMermaid.Kind(src) ?? "flow";
                        string err;
                        if (kind == "sequence") { var sq = new UiSequence { Rect = area }; UiMermaid.ParseSequence(src, sq, out err); p.Add(sq); }
                        else if (kind == "pie") { var ch = new UiChart { Rect = area }; UiMermaid.ParsePie(src, ch, out err); p.Add(ch); }
                        else { var fl = new UiFlow { Rect = area }; UiMermaid.ParseFlow(src, fl, out err); p.Add(fl); }
                        if (err != null) p.Add(new UiLabel("⚠ diagram: " + err, t.SmallSize) { Rect = new Rect(area.x, area.yMax - 0.016f, area.width, 0.016f), Color = t.Warn });
                    }
                    else
                    {
                        var fl = new UiFlow { Rect = area, Direction = ((string)c["direction"] ?? "TD").ToUpperInvariant() };
                        foreach (var n in c["nodes"] as JArray ?? new JArray())
                        {
                            if (n.Type == JTokenType.String) { fl.Ensure((string)n); continue; }
                            var node = fl.Ensure((string)n["id"] ?? (string)n["label"] ?? "?", (string)n["label"], (string)n["shape"]);
                            node.Tone = (string)n["tone"] ?? (string)n["color"];
                        }
                        foreach (var e in c["edges"] as JArray ?? new JArray())
                        {
                            string a = e.Type == JTokenType.Array ? (string)e[0] : (string)e["from"];
                            string b = e.Type == JTokenType.Array ? (string)e[1] : (string)e["to"];
                            if (a == null || b == null) continue;
                            fl.Ensure(a); fl.Ensure(b);
                            fl.Edges.Add(new UiFlow.Edge { From = a, To = b, Label = e.Type == JTokenType.Object ? (string)e["label"] : null, Style = e.Type == JTokenType.Object ? (string)e["style"] ?? "solid" : "solid", Arrow = e.Type != JTokenType.Object || e["arrow"]?.Type != JTokenType.Boolean || (bool)e["arrow"] });
                        }
                        p.Add(fl);
                    }
                    break;
                }
                case "table":
                {
                    var area = TitleRow(p, r, title);
                    var tb = new UiTable { Rect = area };
                    foreach (var col in c["columns"] as JArray ?? new JArray()) tb.Columns.Add(Str(col));
                    foreach (var row in c["rows"] as JArray ?? new JArray())
                    {
                        if (row is JArray ra) tb.Rows.Add(ra.Select(Str).ToList());
                        else if (row is JObject ro)
                        {
                            if (tb.Columns.Count == 0) tb.Columns.AddRange(ro.Properties().Select(pp => pp.Name));
                            tb.Rows.Add(tb.Columns.Select(cn => Str(ro[cn])).ToList());
                        }
                    }
                    p.Add(tb);
                    break;
                }
                case "code": case "diff": case "log":
                {
                    var area = TitleRow(p, r, title ?? (string)c["lang"]);
                    var cv = new UiCodeView { Rect = area, LineNumbers = type == "code" };
                    p.Add(cv);
                    cv.SetText((string)c["text"] ?? "", type == "diff" ? "diff" : type == "log" ? "plain" : "code");
                    break;
                }
                case "stats": case "kpi": case "progress": case "checklist": case "todo": case "timeline": case "kanban": case "kv": case "list":
                    p.Add(new UiViewBlock(type, c) { Rect = TitleRow(p, r, title) });
                    break;
                case "choices": case "buttons":
                {
                    var st = new UiStack(r, t.Gap * 0.6f);
                    string prompt = (string)c["prompt"] ?? title;
                    if (!string.IsNullOrEmpty(prompt)) p.Add(new UiLabel(UiViz.NoParse(prompt), t.SmallSize * 1.05f) { Rect = st.Next(0.018f), Color = t.TextDim });
                    // `buttons` with `items` {id, label, tone?}: the event is "button" (item id, label); `choices` and the old
                    // `buttons` with `options`: "choice" (component id, value)
                    bool items = type == "buttons" && c["items"] is JArray;
                    string compId = (string)c["id"] ?? "";
                    var opts = items
                        ? ((JArray)c["items"]).OfType<JObject>().Select(o => ((string)o["label"] ?? (string)o["id"] ?? "", (string)o["label"] ?? (string)o["id"] ?? "", (string)o["id"] ?? "", (string)o["tone"])).Take(12).ToList()
                        : (c["options"] as JArray ?? new JArray()).Select(o => o.Type == JTokenType.Object ? ((string)o["label"] ?? (string)o["text"], (string)o["value"] ?? (string)o["label"], compId, (string)null) : (Str(o), Str(o), compId, (string)null)).Take(12).ToList();
                    string evType = items ? "button" : "choice";
                    for (int i = 0; i < opts.Count; i += 4)
                    {
                        var row = st.Next(t.ButtonHeight);
                        var chunk = opts.Skip(i).Take(4).ToList();
                        float bw = (row.width - t.Gap * (chunk.Count - 1)) / chunk.Count;
                        for (int j = 0; j < chunk.Count; j++)
                        {
                            var (label, value, evId, tone) = chunk[j];
                            var style = items ? (tone == "accent" ? UiButtonStyle.Primary : tone == "error" ? UiButtonStyle.Danger : UiButtonStyle.Normal)
                                : i + j == 0 ? UiButtonStyle.Primary : UiButtonStyle.Normal;
                            p.Add(new UiButton(UiViz.Cut(label, 28), () => ViewEvent(v, evType, evId, value, value), style) { Rect = new Rect(row.x + j * (bw + t.Gap), row.y, bw, row.height) });
                        }
                    }
                    break;
                }
                case "toggle":
                {
                    string tid = (string)c["id"] ?? "";
                    string label = (string)c["label"] ?? (string)c["text"] ?? tid;
                    bool on = c["value"]?.Type == JTokenType.Boolean && (bool)c["value"];
                    p.Add(new UiToggle(UiViz.NoParse(label), on, now => ViewEvent(v, "toggle", tid, now ? "true" : "false", label + ": " + (now ? "on" : "off"))) { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)) });
                    break;
                }
                case "grid":
                {
                    var area = TitleRow(p, r, title);
                    var cells = (c["cells"] as JArray ?? new JArray()).Select(x => x as JObject ?? new JObject { ["text"] = Str(x) }).Take(49).ToList();
                    int cols = Mathf.Clamp(I(c["columns"], 1), 1, 7);
                    int rowsN = Mathf.Max(1, (cells.Count + cols - 1) / cols);
                    bool anySub = cells.Any(x => !string.IsNullOrEmpty((string)x["sub"]));
                    float ch = Mathf.Min(anySub ? 0.044f : 0.034f, area.height / rowsN), cw = area.width / cols, gap = 0.003f;
                    for (int i = 0; i < cells.Count; i++)
                    {
                        var cell = cells[i];
                        string cid = (string)cell["id"] ?? "", text = Str(cell["text"]), sub = (string)cell["sub"], tone = (string)cell["tone"];
                        string label = UiViz.NoParse(text) + (string.IsNullOrEmpty(sub) ? "" : "\n<size=72%>" + UiViz.NoParse(sub) + "</size>");
                        bool tap = cid.Length > 0;
                        var gc = new UiGridCell(label, tap ? () => ViewEvent(v, "cell", cid, text, text) : (Action)null)
                        {
                            Rect = new Rect(area.x + (i % cols) * cw, area.y + (i / cols) * ch, cw - gap, ch - gap),
                            Tappable = tap,
                            TextSize = anySub ? 0.0105f : 0.0115f,
                        };
                        if (!string.IsNullOrEmpty(tone)) gc.ToneColor = UiViz.Tone(tone);
                        else if (!tap) gc.ToneColor = t.TextDim * 0.55f;
                        p.Add(gc);
                    }
                    break;
                }
                case "input":
                {
                    var st = new UiStack(r, t.Gap * 0.6f);
                    string prompt = (string)c["prompt"] ?? title;
                    string inId = (string)c["id"] ?? "";
                    if (!string.IsNullOrEmpty(prompt)) p.Add(new UiLabel(UiViz.NoParse(prompt), t.SmallSize * 1.05f) { Rect = st.Next(0.018f), Color = t.TextDim });
                    var row = st.NextSplit(t.ButtonHeight, 0.78f, out var send);
                    var field = p.Add(new UiTextField("", "", s => { if (!string.IsNullOrWhiteSpace(s)) ViewEvent(v, "input", inId, s.Trim(), s.Trim()); }, (string)c["placeholder"] ?? "type an answer…") { Rect = row, CaptionWidth = 0f });
                    p.Add(new UiButton("SEND ▶", () => { if (!string.IsNullOrWhiteSpace(field.Text)) ViewEvent(v, "input", inId, field.Text.Trim(), field.Text.Trim()); }, UiButtonStyle.Primary) { Rect = send });
                    break;
                }
                case "scene3d": case "physics":
                {
                    var stage = new GameObject("stage").AddComponent<ViewStage>();
                    stage.transform.SetParent(p.transform, false);
                    stage.Fingertip = h => { var hs = Gestures?.Get((MetaCodex.Hands.Handedness)h); return hs != null && hs.Tracked ? hs.Frame.IndexTip : (Vector3?)null; };
                    stage.Build(c);
                    // a shelf in front of the panel's bottom edge, towards you
                    stage.transform.localPosition = new Vector3(0f, -p.Height * 0.5f - 0.02f, -(stage.Depth * 0.5f + 0.03f));
                    stage.transform.localRotation = Quaternion.identity;
                    _stages[p.Id] = stage;
                    var row = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight));
                    UiButton run = null;
                    var info = p.Add(new UiLabel("", t.SmallSize) { Rect = new Rect(row.x + 0.25f, row.y, row.width - 0.25f, row.height), Color = t.TextDim });
                    void Info() => info.Set($"{stage.Count} bodies · {(stage.Running ? "running" : "paused")} · push them with a fingertip");
                    run = p.Add(new UiButton("❚❚ PAUSE", () => { stage.SetRunning(!stage.Running); run.SetText(stage.Running ? "❚❚ PAUSE" : "▶ RUN"); Info(); }, UiButtonStyle.Primary) { Rect = new Rect(row.x, row.y, 0.11f, row.height) });
                    p.Add(new UiButton("⟲ RESET", () => { stage.ResetScene(); run.SetText("❚❚ PAUSE"); Info(); }) { Rect = new Rect(row.x + 0.118f, row.y, 0.11f, row.height) });
                    Info();
                    break;
                }
                case "image":
                {
                    var area = TitleRow(p, r, title);
                    string path = (string)c["path"] ?? (string)c["src"];
                    string err = AddImage(p, path, area);
                    if (err != null) p.Add(new UiLabel("⚠ image: " + err, t.SmallSize) { Rect = area, Color = t.Warn, Wrap = true });
                    break;
                }
                default:
                {
                    string text = (string)c["text"] ?? (string)c["markdown"] ?? (c["value"]?.ToString() ?? "");
                    // style "big": one centred line in the display face (a countdown, a clock, a score)
                    bool big = (string)c["style"] == "big";
                    var lab = p.Add(new UiLabel(MdLite(text), big ? t.SmallSize * 2.8f : t.SmallSize * 1.1f) { Rect = r, Wrap = !big, UseDisplay = big, Align = big ? TextAlignmentOptions.Center : TextAlignmentOptions.TopLeft, Color = UiViz.Tone((string)c["tone"], t.Text) });
                    string liveId = (string)c["id"];
                    if (!string.IsNullOrEmpty(liveId)) v.Live[liveId] = lab; // LIVE text: a plugin window sets it without a rebuild
                    break;
                }
            }
        }

        private static Rect TitleRow(UiPanel p, Rect r, string title)
        {
            if (string.IsNullOrEmpty(title)) return r;
            p.Add(new UiLabel("<b>" + UiViz.NoParse(title) + "</b>", 0.0115f) { Rect = new Rect(r.x, r.y, r.width, 0.018f) });
            return new Rect(r.x, r.y + 0.02f, r.width, Mathf.Max(0.02f, r.height - 0.02f));
        }

        private static void FillChart(UiChart ch, JObject c)
        {
            foreach (var l in c["labels"] as JArray ?? new JArray()) ch.Labels.Add(Str(l));
            if (c["min"] != null) ch.Min = Fl1(c["min"], 0f);
            if (c["max"] != null) ch.Max = Fl1(c["max"], 1f);
            if (c["values"] is JObject map) // {"label": value, ...}
            {
                foreach (var pp in map.Properties()) ch.Labels.Add(pp.Name);
                ch.Series.Add(((string)c["name"] ?? "", map.Properties().Select(pp => Fl1(pp.Value, float.NaN)).ToList()));
                return;
            }
            if (c["data"] is JArray data) // one series
            {
                if (ch.Kind == "scatter") ch.Points.Add(((string)c["name"] ?? "", data.Select(Pt).ToList()));
                else ch.Series.Add(((string)c["name"] ?? "", data.Select(d => Fl1(d, float.NaN)).ToList()));
            }
            foreach (var s in c["series"] as JArray ?? new JArray())
            {
                string name = s.Type == JTokenType.Object ? (string)s["name"] ?? "" : "";
                var d = s.Type == JTokenType.Object ? s["data"] as JArray ?? s["points"] as JArray ?? new JArray() : s as JArray ?? new JArray();
                if (ch.Kind == "scatter") ch.Points.Add((name, d.Select(Pt).ToList()));
                else ch.Series.Add((name, d.Select(x => Fl1(x, float.NaN)).ToList()));
            }
        }

        private static Vector2 Pt(JToken t) => t is JArray a && a.Count >= 2 ? new Vector2(Fl1(a[0], 0f), Fl1(a[1], 0f)) : t is JObject o ? new Vector2(Fl1(o["x"], 0f), Fl1(o["y"], 0f)) : Vector2.zero;

        private static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.Type == JTokenType.String ? (string)t : t.Type == JTokenType.Float ? ((double)t).ToString("0.###", CultureInfo.InvariantCulture) : t.ToString(Newtonsoft.Json.Formatting.None);

        private static float Fl1(JToken t, float def)
        {
            if (t == null) return def;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            if (t.Type == JTokenType.String && float.TryParse(((string)t).Replace("%", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            return def;
        }

        private static float[] Fl(JToken t) => t is JArray a && a.Count > 0 ? a.Select(x => Fl1(x, 0f)).ToArray() : null;
        private static int I(JToken t, int def) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (int)(float)t : def;

        /// Markdown-lite for view text: # headings, - bullets, **bold**, `code`; everything else verbatim.
        public static string MdLite(string text)
        {
            var sb = new StringBuilder();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw;
                string prefix = "", suffix = "";
                var h = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
                if (h.Success) { prefix = h.Groups[1].Length == 1 ? "<size=125%><b>" : "<b>"; suffix = h.Groups[1].Length == 1 ? "</b></size>" : "</b>"; line = h.Groups[2].Value; }
                var b = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
                if (b.Success) { prefix += b.Groups[1].Value + "• "; line = b.Groups[2].Value; }
                sb.Append(prefix);
                int pos = 0;
                foreach (Match m in Regex.Matches(line, @"\*\*([^*]+)\*\*|`([^`]+)`"))
                {
                    if (m.Index > pos) sb.Append(UiViz.NoParse(line.Substring(pos, m.Index - pos)));
                    if (m.Groups[1].Success) sb.Append("<b>").Append(UiViz.NoParse(m.Groups[1].Value)).Append("</b>");
                    else sb.Append("<color=#A9C8FF>").Append(UiViz.NoParse(m.Groups[2].Value)).Append("</color>");
                    pos = m.Index + m.Length;
                }
                if (pos < line.Length) sb.Append(UiViz.NoParse(line.Substring(pos)));
                sb.Append(suffix).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static Material _imageMat;

        /// A PNG / JPG from the Linux userland (or /mnt/quest) on a quad over the panel, aspect kept.
        private string AddImage(UiPanel p, string linuxPath, Rect area)
        {
            if (string.IsNullOrEmpty(linuxPath)) return "no path";
            string host = Files.HostPath(linuxPath);
            if (!File.Exists(host)) return "not found: " + linuxPath;
            var bytes = File.ReadAllBytes(host);
            if (bytes.Length > 16 * 1024 * 1024) return "too large (16 MB max)";
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            if (!tex.LoadImage(bytes)) return "not a PNG or JPG";
            if (_imageMat == null)
            {
                var sh = Resources.Load<Shader>("Shaders/ViewImage") ?? Shader.Find("MetaCodex/ViewImage");
                if (sh == null) return "image shader missing";
                _imageMat = new Material(sh);
            }
            float aspect = tex.width / (float)Mathf.Max(1, tex.height);
            float w = area.width, h = w / aspect;
            if (h > area.height) { h = area.height; w = h * aspect; }
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "image";
            var col = quad.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            quad.transform.SetParent(p.transform, false);
            quad.transform.localPosition = p.PanelToLocal(area.center, -0.0008f);
            quad.transform.localScale = new Vector3(w, h, 1f);
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _imageMat;
            var mpb = new MaterialPropertyBlock();
            mpb.SetTexture("_MainTex", tex);
            mr.SetPropertyBlock(mpb);
            p.TagRenderer(mr, Vector3.zero); // its panel's slot: it comes and goes with the panel's transition
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return null;
        }

        // ------------------------------------------------------------------ answers

        /// An interactive component was used: the owner of the view gets (type, id, value) when it takes events (plugin
        /// windows), else the thread (or `view wait`) gets `answer`.
        private void ViewEvent(AgentView v, string type, string id, string value, string answer)
        {
            if (v.OnEvent == null) { AnswerView(v, answer); return; }
            try { v.OnEvent(type, id, value); }
            catch (Exception ex) { Log("view " + v.Id + " event " + type + " failed: " + ex.Message); }
        }

        /// A choice or input answered in VR: to the thread the view came from, else to `view wait` callers.
        public void AnswerView(AgentView v, string text)
        {
            if (v == null || string.IsNullOrEmpty(text)) return;
            v.Answer = text;
            var waiters = v.Waiters.ToList();
            v.Waiters.Clear();
            foreach (var w in waiters) { try { w("answer " + text); } catch (Exception) { } }
            if (!string.IsNullOrEmpty(v.ThreadId))
            {
                Mqh.SendMessage(v.ThreadId, text, r =>
                {
                    Toast(r != null ? "Answer sent to the thread: " + UiViz.Cut(text, 40) : "Not sent: " + Mqh.LastError, r != null ? "ok" : "error");
                    if (r != null && OpenWindowShowing(v.ThreadId) is ThreadWindow vw) using (In(vw)) PollThread(); // whichever window shows it
                });
            }
            else Toast(waiters.Count > 0 ? "Answer sent: " + UiViz.Cut(text, 40) : "Answer kept (view " + v.Id + "): " + UiViz.Cut(text, 40), "ok");
        }

        // ------------------------------------------------------------------ control port

        /// `view …` from Linux (mcx-ui view …).
        public string ViewCommand(string arg)
        {
            var parts = (arg ?? "").Trim().Split(new[] { ' ' }, 2);
            string sub = parts[0], rest = parts.Length > 1 ? parts[1].Trim() : "";
            switch (sub)
            {
                case "show":
                case "json":
                {
                    string json = rest;
                    if (sub == "show")
                    {
                        if (rest.Length == 0) return "error: view show <file.json> (a Linux path)";
                        string host = Files.HostPath(rest);
                        if (!File.Exists(host)) return "error: no file " + rest;
                        json = File.ReadAllText(host);
                    }
                    else if (rest.StartsWith("b64:")) json = Encoding.UTF8.GetString(Convert.FromBase64String(rest.Substring(4)));
                    JToken tok;
                    try { tok = JToken.Parse(json); }
                    catch (Exception ex) { return "error: JSON: " + ex.Message.Split('\n')[0]; }
                    var spec = tok as JObject ?? new JObject { ["components"] = tok as JArray ?? new JArray() };
                    var v = ShowView(spec, (string)spec["thread"], 0, "control", true);
                    return v.Error == null ? "ok " + v.Id : "error: " + v.Error;
                }
                case "mermaid":
                {
                    string src = rest;
                    if (src.StartsWith("/")) { string host = Files.HostPath(src); if (File.Exists(host)) src = File.ReadAllText(host); }
                    src = src.Replace("\\n", "\n");
                    var v = ShowView(new JObject { ["title"] = MermaidTitle(src), ["components"] = new JArray { new JObject { ["type"] = "mermaid", ["text"] = src } } });
                    return v.Error == null ? "ok " + v.Id : "error: " + v.Error;
                }
                case "list": return _viewList.Count == 0 ? "(no views)" : string.Join("\n", _viewList.Select(v => $"{v.Id}\t{v.Title}\topen={(_panels.TryGetValue(v.PanelId, out var vp) && vp.IsOpen)}\tthread={v.ThreadId ?? "-"}\tanswer={v.Answer ?? "-"}"));
                case "open": return OpenView(SafeViewId(rest)) ? "ok" : "error: no view " + rest;
                case "close": { var v = FindView(SafeViewId(rest)); if (v == null) return "error: no view " + rest; ClosePanel(v.PanelId); return "ok"; }
                case "remove": { var v = FindView(SafeViewId(rest)); if (v == null) return "error: no view " + rest; DestroyViewPanel(v.PanelId); _viewList.Remove(v); RefreshWorkspaceViews(); return "ok"; }
                case "clear": foreach (var v in _viewList.ToList()) DestroyViewPanel(v.PanelId); _viewList.Clear(); RefreshWorkspaceViews(); return "ok";
                case "answer": { var v = FindView(SafeViewId(rest)); return v == null ? "error: no view " + rest : v.Answer ?? "none"; }
                case "schema": return ViewSchema;
                default: return "error: view show <file.json> | json <json> | mermaid <file|text> | list | open|close|remove <id> | clear | answer <id> | wait <id> | schema";
            }
        }

        /// `view wait <id>`: answered when the user picks a choice / sends the input (or after the long wait).
        public bool ViewAsyncCommand(string line, Action<string> reply)
        {
            if (!line.StartsWith("view wait")) return false;
            string id = SafeViewId(line.Substring(9).Trim());
            var v = FindView(id);
            if (v == null) { reply("error: no view " + id); return true; }
            bool done = false;
            Action<string> once = s => { if (done) return; done = true; reply(s); };
            v.Waiters.Add(once);
            After(MetaCodex.App.ControlServer.LongWaitSeconds - 5, () => { if (!done) { v.Waiters.Remove(once); once("timeout"); } });
            return true;
        }
    }

    /// The simple view components drawn as one canvas: stats tiles, progress bars, checklists, timelines,
    /// kanban columns, key/value lists.
    public sealed class UiViewBlock : UiCanvas
    {
        private readonly string _type;
        private readonly JObject _spec;
        public UiViewBlock(string type, JObject spec) { _type = type; _spec = spec ?? new JObject(); }

        private IEnumerable<JToken> Items => _spec["items"] as JArray ?? (_spec["items"] is JObject o ? new JArray(o.Properties().Select(p => new JObject { ["key"] = p.Name, ["value"] = p.Value })) : new JArray());

        private static string S(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);

        protected override void Draw()
        {
            var t = Theme;
            var r = Rect;
            switch (_type)
            {
                case "stats": case "kpi":
                {
                    var items = Items.ToList();
                    int perRow = Mathf.Clamp(items.Count, 1, 4);
                    int rows = Mathf.CeilToInt(items.Count / (float)perRow);
                    float tw = (r.width - t.Gap * (perRow - 1)) / perRow, th = Mathf.Min(0.075f, rows > 0 ? (r.height - t.Gap * (rows - 1)) / rows : r.height);
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        var tr = new Rect(r.x + (i % perRow) * (tw + t.Gap), r.y + (i / perRow) * (th + t.Gap), tw, th);
                        Box(tr, t.Frame, 0.006f, 0, 0.12f, 0.003f);
                        string label = it.Type == JTokenType.Object ? S(it["label"]) : "";
                        string value = it.Type == JTokenType.Object ? S(it["value"]) : S(it);
                        string delta = it.Type == JTokenType.Object ? S(it["delta"]) : "";
                        Txt(new Rect(tr.x + 0.008f, tr.y + 0.004f, tr.width - 0.016f, th * 0.3f), UiViz.NoParse(label), 0.0102f, t.TextDim);
                        Txt(new Rect(tr.x + 0.008f, tr.y + th * 0.3f, tr.width - 0.016f, th * 0.42f), "<b>" + UiViz.NoParse(value) + "</b>", Mathf.Min(0.024f, th * 0.4f), t.Text, TextAlignmentOptions.MidlineLeft, t.Display);
                        if (delta.Length > 0)
                        {
                            bool up = !delta.TrimStart().StartsWith("-");
                            string tone = it.Type == JTokenType.Object ? S(it["tone"]) : "";
                            var col = tone.Length > 0 ? UiViz.Tone(tone) : up ? t.Ok : t.Danger;
                            Txt(new Rect(tr.x + 0.008f, tr.y + th * 0.72f, tr.width - 0.016f, th * 0.26f), (up ? "▲ " : "▼ ") + UiViz.NoParse(delta), 0.0102f, col);
                        }
                    }
                    break;
                }
                case "progress":
                {
                    var items = Items.ToList();
                    float rh = items.Count > 0 ? Mathf.Min(0.028f, r.height / items.Count) : r.height;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        float y = r.y + i * rh;
                        float v = it.Type == JTokenType.Object ? F(it["value"]) : F(it);
                        float max = it.Type == JTokenType.Object && it["max"] != null ? F(it["max"]) : v > 1f ? 100f : 1f;
                        float frac = Mathf.Clamp01(max > 0 ? v / max : 0);
                        var col = UiViz.Tone(it.Type == JTokenType.Object ? S(it["tone"]) : "", t.Accent);
                        Txt(new Rect(r.x, y, r.width * 0.36f, rh), UiViz.NoParse(it.Type == JTokenType.Object ? S(it["label"]) : ""), 0.0108f, t.Text);
                        AddShape(Panel.Shape(UiShapeKind.Bar, new Rect(r.x + r.width * 0.37f, y + rh * 0.38f, r.width * 0.5f, rh * 0.24f), col, rh * 0.12f, 0f, 0.002f, frac));
                        Txt(new Rect(r.x + r.width * 0.88f, y, r.width * 0.12f, rh), (frac * 100f).ToString("0", CultureInfo.InvariantCulture) + "%", 0.0102f, t.TextDim, TextAlignmentOptions.MidlineRight, t.Mono);
                    }
                    break;
                }
                case "checklist": case "todo":
                {
                    var items = Items.ToList();
                    float rh = items.Count > 0 ? Mathf.Min(0.025f, r.height / items.Count) : r.height;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        string text = it.Type == JTokenType.Object ? S(it["text"] ?? it["label"]) : S(it);
                        bool done = it.Type == JTokenType.Object && it["done"]?.Type == JTokenType.Boolean && (bool)it["done"];
                        var m = Regex.Match(text, @"^\s*\[( |x|X)\]\s*(.*)$");
                        if (m.Success) { done = m.Groups[1].Value != " "; text = m.Groups[2].Value; }
                        float y = r.y + i * rh;
                        Txt(new Rect(r.x, y, 0.02f, rh), done ? "☑" : "☐", 0.013f, done ? t.Ok : t.TextDim, TextAlignmentOptions.Center);
                        Txt(new Rect(r.x + 0.022f, y, r.width - 0.022f, rh), done ? "<s>" + UiViz.NoParse(text) + "</s>" : UiViz.NoParse(text), 0.0112f, done ? t.TextDim : t.Text);
                    }
                    break;
                }
                case "timeline":
                {
                    var items = Items.ToList();
                    float rh = items.Count > 0 ? Mathf.Min(0.027f, r.height / items.Count) : r.height;
                    float lineX = r.x + 0.062f;
                    if (items.Count > 1) Hair(new Vector2(lineX, r.y + rh * 0.5f), new Vector2(lineX, r.y + rh * (items.Count - 0.5f)), t.Frame, 0.4f);
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        float y = r.y + i * rh;
                        var col = UiViz.Tone(it.Type == JTokenType.Object ? S(it["tone"]) : "", t.Accent);
                        Txt(new Rect(r.x, y, 0.055f, rh), UiViz.NoParse(it.Type == JTokenType.Object ? S(it["time"] ?? it["when"]) : ""), 0.0098f, t.TextDim, TextAlignmentOptions.MidlineRight, t.Mono);
                        Dot(new Vector2(lineX, y + rh * 0.5f), 0.008f, col);
                        Txt(new Rect(lineX + 0.01f, y, r.xMax - lineX - 0.01f, rh), UiViz.NoParse(it.Type == JTokenType.Object ? S(it["text"] ?? it["label"]) : S(it)), 0.011f, t.Text);
                    }
                    break;
                }
                case "kanban":
                {
                    var cols = (_spec["columns"] as JArray ?? new JArray()).OfType<JObject>().Take(6).ToList();
                    if (cols.Count == 0) break;
                    float cw = (r.width - t.Gap * (cols.Count - 1)) / cols.Count;
                    for (int ci = 0; ci < cols.Count; ci++)
                    {
                        var col = cols[ci];
                        var cr = new Rect(r.x + ci * (cw + t.Gap), r.y, cw, r.height);
                        Box(cr, t.Frame, 0.006f, 0, 0.06f, 0.002f);
                        var cards = (col["cards"] as JArray ?? new JArray()).ToList();
                        Txt(new Rect(cr.x + 0.006f, cr.y + 0.002f, cr.width - 0.012f, 0.02f), "<b>" + UiViz.NoParse(S(col["title"])) + "</b> <color=#" + UiViz.Hex(t.TextDim) + ">" + cards.Count + "</color>", 0.0108f, t.Text);
                        float ch = 0.034f, y = cr.y + 0.024f;
                        int fit = Mathf.Max(0, Mathf.FloorToInt((cr.yMax - y - 0.004f) / (ch + 0.004f)));
                        for (int k = 0; k < cards.Count && k < fit; k++)
                        {
                            var card = cards[k];
                            string text = card.Type == JTokenType.Object ? S(card["text"] ?? card["title"]) : S(card);
                            var tone = UiViz.Tone(card.Type == JTokenType.Object ? S(card["tone"]) : "", t.Accent);
                            var kr = new Rect(cr.x + 0.004f, y, cr.width - 0.008f, ch);
                            Box(kr, tone, 0.004f, 0, 0.32f, 0.002f);
                            Txt(new Rect(kr.x + 0.005f, kr.y, kr.width - 0.01f, kr.height), UiViz.NoParse(UiViz.Cut(text, 60)), 0.0098f, t.Text, TextAlignmentOptions.MidlineLeft, null, true);
                            y += ch + 0.004f;
                        }
                        if (cards.Count > fit) Txt(new Rect(cr.x, cr.yMax - 0.014f, cr.width, 0.012f), $"+{cards.Count - fit} more", 0.009f, t.TextDim, TextAlignmentOptions.Center);
                    }
                    break;
                }
                default: // kv / list
                {
                    var items = Items.ToList();
                    float rh = items.Count > 0 ? Mathf.Min(0.024f, r.height / items.Count) : r.height;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        float y = r.y + i * rh;
                        if (it.Type == JTokenType.Object && (it["key"] != null || it["label"] != null))
                        {
                            Txt(new Rect(r.x, y, r.width * 0.38f, rh), UiViz.NoParse(S(it["key"] ?? it["label"])), 0.0108f, t.TextDim);
                            Txt(new Rect(r.x + r.width * 0.4f, y, r.width * 0.6f, rh), UiViz.NoParse(S(it["value"])), 0.0108f, UiViz.Tone(S(it["tone"]), t.Text), TextAlignmentOptions.MidlineLeft, t.Mono);
                        }
                        else Txt(new Rect(r.x, y, r.width, rh), "• " + UiViz.NoParse(S(it)), 0.011f, t.Text);
                    }
                    break;
                }
            }
        }

        private static float F(JToken t)
        {
            if (t == null) return 0f;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            float.TryParse(S(t).Replace("%", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var f);
            return f;
        }
    }
}
