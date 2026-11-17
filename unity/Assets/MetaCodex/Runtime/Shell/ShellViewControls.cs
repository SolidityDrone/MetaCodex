using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The component library of views and plugin windows beyond the basic blocks (ShellViews): form controls
    /// (checkbox, radio, segmented, select, slider, stepper, field, date, time, color, a selectable list), layout
    /// (card, row, tabs, divider, spacer) and a 2D drawing canvas. The rules every one of them keeps:
    ///   - one event per user change, to the plugin's on(e) (agent views: the thread gets "label: value", value
    ///     controls after 1.5 s of quiet, so a slider does not send ten messages);
    ///   - the control shows its new value at once; the plugin's next spec with the same layout (ShapeOf strips the
    ///     values) is applied in place by the window's watcher (UiViewWatch): no rebuild, no flash; a spec value that
    ///     arrives within 1 s of the user's own change waits, so a late spec never undoes a newer tap;
    ///   - the checks of ViewSpecCheck (shared with the PC harness) ran before the window got here.
    public sealed partial class MetaCodexShell
    {
        private static readonly HashSet<string> RichTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "checkbox", "radio", "segmented", "select", "slider", "stepper", "field", "date", "time", "color",
            "canvas", "card", "row", "tabs", "divider", "spacer",
        };

        /// A `list` whose items have ids is the selectable list (rows to tap); without ids it is the old read-only list.
        private static bool IsPickList(JObject c) =>
            c["items"] is JArray a && a.Any(x => x is JObject o && o["id"] != null && o["id"].Type == JTokenType.String);

        private static bool IsRich(JObject c, string type) => RichTypes.Contains(type) || type == "list" && IsPickList(c);

        private static bool Bool(JToken t) => t != null && (t.Type == JTokenType.Boolean ? (bool)t : t.Type == JTokenType.String && (string)t == "true");
        private static string S(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Object || t.Type == JTokenType.Array ? null : Str(t);
        private static string Num(float x) => x.ToString("0.####", CultureInfo.InvariantCulture);

        /// options = {"a", "b"} or {{label=, value=}, ...}: (label, value) pairs.
        private static List<(string label, string value)> Options(JObject c, int max)
        {
            var list = new List<(string, string)>();
            foreach (var o in c["options"] as JArray ?? new JArray())
            {
                if (list.Count >= max) break;
                if (o is JObject jo)
                {
                    string value = S(jo["value"]) ?? S(jo["id"]) ?? S(jo["label"]) ?? S(jo["text"]) ?? "";
                    list.Add((S(jo["label"]) ?? S(jo["text"]) ?? value, value));
                }
                else { string s = Str(o); list.Add((s, s)); }
            }
            return list;
        }

        private static int IndexOfValue(List<(string label, string value)> opts, string value)
        {
            if (value == null) return -1;
            for (int i = 0; i < opts.Count; i++) if (opts[i].value == value) return i;
            return -1;
        }

        // ------------------------------------------------------------------ heights

        /// The height a library component wants at a width (metres), or -1 for the basic ones (ShellViews.BaseHeight).
        private float RichHeight(JObject c, string type, float width)
        {
            if (!IsRich(c, type)) return -1f;
            var t = UiTheme.Current;
            switch (type)
            {
                case "checkbox": return 0.03f;
                case "radio": return Bool(c["inline"]) ? 0.03f : 0.027f * Mathf.Clamp((c["options"] as JArray)?.Count ?? 1, 1, 8);
                case "segmented": case "select": case "stepper": case "field": case "time": return t.ButtonHeight + 0.004f;
                case "slider": return 0.036f;
                case "date": return 0.026f + 0.017f + 6 * 0.026f + (Bool(c["time"]) ? 0.034f : 0f);
                case "color": return 0.03f * Mathf.CeilToInt(Mathf.Clamp((c["options"] as JArray)?.Count ?? DefaultSwatches.Length, 1, 24) / 8f);
                case "list": return 0.03f * Mathf.Clamp(Mathf.Min((c["items"] as JArray)?.Count ?? 1, I(c["rows"], 6)), 1, 10);
                case "canvas":
                {
                    var u = CanvasUnits(c, out _, out _);
                    float drawH = Fl1(c["draw_height"], -1f);
                    return drawH > 0 ? Mathf.Clamp(drawH, 0.05f, 0.6f) : Mathf.Clamp(width * u.y / Mathf.Max(1e-4f, u.x), 0.06f, 0.45f);
                }
                case "divider": return 0.012f;
                case "spacer": return 0.012f;
                case "card":
                {
                    bool titled = !string.IsNullOrEmpty(S(c["title"]));
                    return (titled ? UiCardFrame.TitleH : UiCardFrame.Inset) + UiCardFrame.Inset + StackHeight(c["components"] as JArray, width - 2 * UiCardFrame.Inset);
                }
                case "row":
                {
                    var kids = (c["components"] as JArray ?? new JArray()).OfType<JObject>().Take(6).ToList();
                    if (kids.Count == 0) return 0.03f;
                    float total = kids.Sum(Weight), avail = width - t.Gap * (kids.Count - 1), h = 0f;
                    foreach (var k in kids) h = Mathf.Max(h, DesiredHeight(k, avail * Weight(k) / total));
                    return h;
                }
                case "tabs":
                {
                    float h = 0.02f;
                    foreach (var tab in (c["tabs"] as JArray ?? new JArray()).OfType<JObject>().Take(6)) h = Mathf.Max(h, StackHeight(tab["components"] as JArray, width));
                    return t.ButtonHeight * 0.9f + t.Gap + h;
                }
            }
            return 0.03f;
        }

        private static float Weight(JObject c) => Mathf.Clamp(Fl1(c["weight"], 1f), 0.1f, 10f);

        private float StackHeight(JArray kids, float width)
        {
            var t = UiTheme.Current;
            if (kids == null) return 0.02f;
            float h = 0f; int n = 0;
            foreach (var k in kids.OfType<JObject>().Take(12)) { h += DesiredHeight(k, width); n++; }
            return n == 0 ? 0.02f : h + t.Gap * (n - 1);
        }

        // ------------------------------------------------------------------ layout of children

        /// Children of a card, a tab or a row: stacked top to bottom (squeezed to fit like the window's own rows), or side
        /// by side sharing the width by `weight` (align top, center or bottom).
        private void LayoutChildren(UiPanel p, AgentView v, JArray kids, Rect area, string basePath, bool horizontal, string align)
        {
            var t = UiTheme.Current;
            var list = (kids ?? new JArray()).OfType<JObject>().Take(horizontal ? 6 : 12).ToList();
            if (list.Count == 0) return;
            if (horizontal)
            {
                float total = list.Sum(Weight), avail = area.width - t.Gap * (list.Count - 1), x = area.x;
                for (int j = 0; j < list.Count; j++)
                {
                    float w = avail * Weight(list[j]) / total;
                    float h = Mathf.Min(area.height, DesiredHeight(list[j], w));
                    float y = align == "center" ? area.y + (area.height - h) * 0.5f : align == "bottom" ? area.yMax - h : area.y;
                    AddChild(p, v, list[j], new Rect(x, y, w, h), basePath + "[" + j + "]");
                    x += w + t.Gap;
                }
                return;
            }
            var hs = list.Select(k => DesiredHeight(k, area.width)).ToList();
            float sum = hs.Sum(), gaps = t.Gap * (list.Count - 1);
            float k2 = sum + gaps > area.height ? Mathf.Max(0.01f, area.height - gaps) / Mathf.Max(0.01f, sum) : 1f;
            float yy = area.y;
            for (int j = 0; j < list.Count; j++)
            {
                float h = hs[j] * k2;
                AddChild(p, v, list[j], new Rect(area.x, yy, area.width, h), basePath + "[" + j + "]");
                yy += h + t.Gap;
            }
        }

        private void AddChild(UiPanel p, AgentView v, JObject c, Rect r, string path)
        {
            try { AddViewComponent(p, v, c, r, path); }
            catch (Exception ex)
            {
                p.Add(new UiLabel("⚠ " + (string)c["type"] + ": " + ex.Message, UiTheme.Current.SmallSize) { Rect = r, Color = UiTheme.Current.Danger, Wrap = true });
            }
        }

        // ------------------------------------------------------------------ events and live values

        private readonly Dictionary<string, int> _viewSettle = new Dictionary<string, int>();
        private int _viewSettleN;

        /// A value control changed: the plugin gets (type, id, value) at once. An agent's view answers its thread with
        /// `answer`, and when `settle` only after 1.5 s without another change of the same control (a slider, a stepper).
        private void ViewValue(AgentView v, string type, string id, string value, string answer, bool settle)
        {
            if (v.OnEvent != null || !settle) { ViewEvent(v, type, id, value, answer); return; }
            string key = v.Id + "/" + id;
            int n = ++_viewSettleN;
            _viewSettle[key] = n;
            After(1.5f, () => { if (_viewSettle.TryGetValue(key, out var m) && m == n) { _viewSettle.Remove(key); AnswerView(v, answer); } });
        }

        /// Registers a built control for in-place updates: `apply` gets the component's new spec when it changed.
        private static void Bind(AgentView v, string path, JObject c, Func<float> localAt, Action<JObject> apply)
        {
            if (v.Source != "plugin" || string.IsNullOrEmpty(path)) return;
            v.Bindings.Add(new ViewBinding { Path = path, Key = c.ToString(Newtonsoft.Json.Formatting.None), LocalAt = localAt, Apply = apply });
        }

        // ------------------------------------------------------------------ the components

        private void AddRichComponent(UiPanel p, AgentView v, JObject c, Rect r, string path, string type)
        {
            var t = UiTheme.Current;
            string id = S(c["id"]) ?? "", label = S(c["label"]) ?? "", title = S(c["title"]);
            bool container = type == "card" || type == "tabs" || type == "row" || type == "divider" || type == "spacer";
            if (!container) r = TitleRow(p, r, title);
            float at = -10f; // when the user last changed a control that has no LocalAt of its own
            switch (type)
            {
                case "checkbox":
                {
                    var cb = p.Add(new UiCheck(label, Bool(c["value"]), now => ViewValue(v, "toggle", id, now ? "true" : "false", label + ": " + (now ? "on" : "off"), true))
                        { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, 0.03f)) });
                    Bind(v, path, c, () => cb.LocalAt, n => cb.Set(Bool(n["value"])));
                    break;
                }
                case "radio":
                {
                    var opts = Options(c, 8);
                    var rd = p.Add(new UiRadio(opts.Select(o => o.label).ToArray(), IndexOfValue(opts, S(c["value"])), k => ViewValue(v, "radio", id, opts[k].value, (label.Length > 0 ? label + ": " : "") + opts[k].label, true))
                        { Rect = r, Inline = Bool(c["inline"]) });
                    Bind(v, path, c, () => rd.LocalAt, n => rd.Set(IndexOfValue(opts, S(n["value"]))));
                    break;
                }
                case "segmented":
                {
                    var opts = Options(c, 5);
                    UiSegmented seg = null;
                    seg = p.Add(new UiSegmented(label, opts.Select(o => o.label).ToArray(), IndexOfValue(opts, S(c["value"])), k =>
                    {
                        at = Time.unscaledTime;
                        ViewValue(v, "segmented", id, opts[k].value, (label.Length > 0 ? label + ": " : "") + opts[k].label, true);
                    }) { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)), LabelWidth = Mathf.Min(0.16f, r.width * 0.32f) });
                    Bind(v, path, c, () => at, n => seg.Set(IndexOfValue(opts, S(n["value"]))));
                    break;
                }
                case "select":
                {
                    var opts = Options(c, 200);
                    int sel = IndexOfValue(opts, S(c["value"]));
                    var box = p.Add(new UiSelectBox(label, sel >= 0 ? opts[sel].label : null) { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)), Placeholder = S(c["placeholder"]) ?? "choose…" });
                    box.OnOpen = () => OpenViewPicker(p, label.Length > 0 ? label : title ?? "Choose", opts, sel, k =>
                    {
                        sel = k;
                        box.SetShown(opts[k].label);
                        box.MarkTouched();
                        ViewValue(v, "select", id, opts[k].value, (label.Length > 0 ? label + ": " : "") + opts[k].label, false);
                    });
                    Bind(v, path, c, () => box.LocalAt, n => { sel = IndexOfValue(opts, S(n["value"])); box.SetShown(sel >= 0 ? opts[sel].label : null); });
                    break;
                }
                case "slider":
                {
                    float min = Fl1(c["min"], 0f), max = Fl1(c["max"], 100f), step = Fl1(c["step"], 0f);
                    var sl = p.Add(new UiValueSlider(label, min, max, step, Fl1(c["value"], min)) { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, 0.036f)), Unit = S(c["unit"]) ?? "", Live = Bool(c["live"]) });
                    string unit = S(c["unit"]);
                    sl.OnCommit = x => ViewValue(v, "slider", id, Num(x), (label.Length > 0 ? label + ": " : "") + UiValueSlider.Format(x, step) + (unit != null ? " " + unit : ""), true);
                    sl.OnLive = x => { if (v.OnEvent != null) ViewEvent(v, "slider", id, Num(x), Num(x)); };
                    Bind(v, path, c, () => sl.LocalAt, n => sl.Set(Fl1(n["value"], sl.Value)));
                    break;
                }
                case "stepper":
                {
                    var st = p.Add(new UiStepper(label, Fl1(c["value"], 0f))
                    {
                        Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)), Unit = S(c["unit"]) ?? "",
                        Min = Fl1(c["min"], float.NegativeInfinity), Max = Fl1(c["max"], float.PositiveInfinity), Step = Mathf.Max(1e-4f, Fl1(c["step"], 1f)),
                    });
                    st.Value = Mathf.Clamp(st.Value, st.Min, st.Max);
                    string unit = S(c["unit"]);
                    st.OnChanged = x => ViewValue(v, "stepper", id, Num(x), (label.Length > 0 ? label + ": " : "") + Num(x) + (unit != null ? " " + unit : ""), true);
                    Bind(v, path, c, () => st.LocalAt, n => st.Set(Fl1(n["value"], st.Value)));
                    break;
                }
                case "field":
                {
                    bool clear = Bool(c["clear"]);
                    UiTextField f = null;
                    f = p.Add(new UiTextField(label, S(c["value"]) ?? "", s =>
                    {
                        at = Time.unscaledTime;
                        string val = (s ?? "").Trim();
                        ViewValue(v, "input", id, val, (label.Length > 0 ? label + ": " : "") + val, false);
                        if (clear) f.Set("");
                    }, S(c["placeholder"]) ?? "")
                    { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)), CaptionWidth = label.Length > 0 ? Mathf.Min(0.16f, r.width * 0.32f) : 0f, Secure = Bool(c["secure"]) });
                    Bind(v, path, c, () => at, n => f.Set(S(n["value"]) ?? ""));
                    break;
                }
                case "date":
                {
                    bool time = Bool(c["time"]);
                    DateTime? val = UiDatePicker.TryParse(S(c["value"]), out var d, out var hh, out var mm) ? d : (DateTime?)null;
                    var dp = new UiDatePicker(val, time) { Rect = r, MinuteStep = Mathf.Clamp(I(c["minute_step"], 5), 1, 30) };
                    if (time && hh >= 0) { dp.Hour = hh; dp.Minute = mm; }
                    if (UiDatePicker.TryParse(S(c["min"]), out var dmin, out _, out _)) dp.MinDate = dmin;
                    if (UiDatePicker.TryParse(S(c["max"]), out var dmax, out _, out _)) dp.MaxDate = dmax;
                    dp.OnChanged = s => ViewValue(v, "date", id, s, (label.Length > 0 ? label + ": " : "") + s, true);
                    p.Add(dp);
                    Bind(v, path, c, () => dp.LocalAt, n =>
                    {
                        bool ok = UiDatePicker.TryParse(S(n["value"]), out var nd, out var nh, out var nm);
                        dp.SetValue(ok ? nd : (DateTime?)null, nh >= 0 ? nh : dp.Hour, nm >= 0 ? nm : dp.Minute);
                    });
                    break;
                }
                case "time":
                {
                    UiTimePicker.TryParse(S(c["value"]) ?? "09:00", out int hh, out int mm);
                    var tp = p.Add(new UiTimePicker(label, Mathf.Max(0, hh), Mathf.Max(0, mm)) { Rect = new Rect(r.x, r.y, r.width, Mathf.Min(r.height, t.ButtonHeight)), Step = Mathf.Clamp(I(c["step"], 5), 1, 30) });
                    tp.OnChanged = s => ViewValue(v, "time", id, s, (label.Length > 0 ? label + ": " : "") + s, true);
                    Bind(v, path, c, () => tp.LocalAt, n => { if (UiTimePicker.TryParse(S(n["value"]), out int h2, out int m2)) tp.Set(h2, m2); });
                    break;
                }
                case "color":
                {
                    var hexes = (c["options"] as JArray)?.Select(o => Str(o)).Take(24).ToList() ?? DefaultSwatches.Select(x => "#" + x.ToString("X6")).ToList();
                    var cols = hexes.Select(h => UiViz.Tone(h)).ToArray();
                    int Idx(string s) { if (s == null) return -1; for (int i = 0; i < hexes.Count; i++) if (string.Equals(hexes[i], s, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
                    var sw = p.Add(new UiSwatches(cols, Idx(S(c["value"]))) { Rect = r });
                    sw.OnChanged = k => ViewValue(v, "color", id, hexes[k].ToUpperInvariant(), (label.Length > 0 ? label + ": " : "") + hexes[k], true);
                    Bind(v, path, c, () => sw.LocalAt, n => sw.Set(Idx(S(n["value"]))));
                    break;
                }
                case "list":
                {
                    var items = (c["items"] as JArray ?? new JArray()).OfType<JObject>().Take(100).ToList();
                    var pl = new UiPickList { Rect = r };
                    void Fill(List<JObject> src, UiPickList into)
                    {
                        for (int i = 0; i < src.Count; i++)
                        {
                            if (into.Rows.Count <= i) into.Rows.Add(new UiPickList.Row());
                            var row = into.Rows[i];
                            row.Text = S(src[i]["text"]) ?? S(src[i]["label"]) ?? S(src[i]["id"]) ?? "";
                            row.Sub = S(src[i]["sub"]);
                            row.Badge = S(src[i]["badge"]);
                            string tone = S(src[i]["tone"]);
                            row.Tone = string.IsNullOrEmpty(tone) ? (Color?)null : UiViz.Tone(tone);
                        }
                    }
                    Fill(items, pl);
                    string selected = S(c["selected"]);
                    pl.Selected = items.FindIndex(x => S(x["id"]) == selected);
                    pl.OnPick = k => ViewValue(v, "list", S(items[k]["id"]) ?? "", pl.Rows[k].Text, pl.Rows[k].Text, false);
                    p.Add(pl);
                    Bind(v, path, c, () => pl.LocalAt, n =>
                    {
                        var ni = (n["items"] as JArray ?? new JArray()).OfType<JObject>().Take(100).ToList();
                        if (ni.Count == items.Count) Fill(ni, pl);
                        string ns = S(n["selected"]);
                        pl.Selected = items.FindIndex(x => S(x["id"]) == ns);
                        pl.Restyle();
                    });
                    break;
                }
                case "canvas":
                    AddCanvas(p, v, c, r, path, id);
                    break;
                case "divider":
                    p.Add(new UiDivider { Rect = r });
                    break;
                case "spacer":
                    break;
                case "card":
                {
                    string tone = S(c["tone"]);
                    p.Add(new UiCardFrame { Rect = r, Title = title, Tone = string.IsNullOrEmpty(tone) ? (Color?)null : UiViz.Tone(tone) });
                    float top = string.IsNullOrEmpty(title) ? UiCardFrame.Inset : UiCardFrame.TitleH;
                    var inner = new Rect(r.x + UiCardFrame.Inset, r.y + top, r.width - 2 * UiCardFrame.Inset, Mathf.Max(0.01f, r.height - top - UiCardFrame.Inset));
                    LayoutChildren(p, v, c["components"] as JArray, inner, path + ".components", false, null);
                    break;
                }
                case "row":
                    LayoutChildren(p, v, c["components"] as JArray, r, path + ".components", true, S(c["align"]));
                    break;
                case "tabs":
                    AddTabs(p, v, c, r, path, id);
                    break;
            }
        }

        public static readonly int[] DefaultSwatches = { 0x3987E5, 0xD95926, 0x199E70, 0xC98500, 0xD55181, 0x008300, 0x9085E9, 0xE66767, 0x35D0FF, 0x46F28C, 0xFFB547, 0xE2F4FF };

        private void AddTabs(UiPanel p, AgentView v, JObject c, Rect r, string path, string id)
        {
            var t = UiTheme.Current;
            var tabs = (c["tabs"] as JArray ?? new JArray()).OfType<JObject>().Take(6).ToList();
            if (tabs.Count == 0) return;
            var ids = tabs.Select((x, i) => S(x["id"]) ?? ("tab" + (i + 1))).ToList();
            var bar = p.Add(new UiTabs(tabs.Select((x, i) => S(x["label"]) ?? ids[i]).ToArray()) { Rect = new Rect(r.x, r.y, r.width, t.ButtonHeight * 0.9f) });
            var area = new Rect(r.x, r.y + t.ButtonHeight * 0.9f + t.Gap, r.width, Mathf.Max(0.01f, r.height - t.ButtonHeight * 0.9f - t.Gap));
            var pages = new List<List<UiElement>>();
            for (int k = 0; k < tabs.Count; k++)
            {
                int before = p.Elements.Count;
                LayoutChildren(p, v, tabs[k]["components"] as JArray, area, path + ".tabs[" + k + "].components", false, null);
                pages.Add(p.Elements.GetRange(before, p.Elements.Count - before));
            }
            bool quiet = false;
            float at = -10f;
            void Show(int k) { for (int j = 0; j < pages.Count; j++) foreach (var e in pages[j]) e.SetVisible(j == k); }
            bar.OnChanged = k =>
            {
                Show(k);
                if (quiet) return;
                at = Time.unscaledTime;
                if (v.OnEvent != null) ViewEvent(v, "tab", id, ids[k], ids[k]);
            };
            int first = Mathf.Max(0, ids.IndexOf(S(c["value"]) ?? ""));
            quiet = true; bar.Select(first); quiet = false;
            Bind(v, path, c, () => at, n => { int k = ids.IndexOf(S(n["value"]) ?? ""); if (k >= 0 && k != bar.Selected) { quiet = true; bar.Select(k); quiet = false; } });
        }

        // ------------------------------------------------------------------ canvas

        /// The canvas's units: width x height, or for fit = "lonlat" the bounds' size (longitude scaled by the cosine of the
        /// middle latitude: equirectangular, north up).
        private static Vector2 CanvasUnits(JObject c, out bool lonlat, out float[] bounds)
        {
            lonlat = S(c["fit"]) == "lonlat";
            bounds = null;
            if (lonlat && c["bounds"] is JArray b && b.Count == 4)
            {
                bounds = new[] { Fl1(b[0], 0f), Fl1(b[1], 0f), Fl1(b[2], 1f), Fl1(b[3], 1f) };
                float k = Mathf.Cos(((bounds[1] + bounds[3]) * 0.5f) * Mathf.Deg2Rad);
                return new Vector2(Mathf.Max(1e-6f, (bounds[2] - bounds[0]) * k), Mathf.Max(1e-6f, bounds[3] - bounds[1]));
            }
            lonlat = false;
            return new Vector2(Mathf.Max(1e-4f, Fl1(c["width"], 100f)), Mathf.Max(1e-4f, Fl1(c["height"], 100f)));
        }

        private void AddCanvas(UiPanel p, AgentView v, JObject c, Rect r, string path, string id)
        {
            var units = CanvasUnits(c, out bool lonlat, out var bounds);
            float cosK = lonlat ? Mathf.Cos(((bounds[1] + bounds[3]) * 0.5f) * Mathf.Deg2Rad) : 1f;
            Vector2 U(float x, float y) => lonlat ? new Vector2((x - bounds[0]) * cosK, bounds[3] - y) : new Vector2(x, y);
            Vector2 Pt(JToken tok) => tok is JArray a && a.Count >= 2 ? U(Fl1(a[0], 0f), Fl1(a[1], 0f)) : tok is JObject o ? U(Fl1(o["x"] ?? o["lon"], 0f), Fl1(o["y"] ?? o["lat"], 0f)) : Vector2.zero;
            var cv = new UiDrawCanvas { Rect = r, Units = units, Selectable = c["select"] == null || Bool(c["select"]), Selected = S(c["selected"]) };
            string bg = S(c["background"]);
            if (!string.IsNullOrEmpty(bg)) { var bc = UiViz.Tone(bg); bc.a = 0.45f; cv.Background = bc; }
            int total = 0, texts = 0;
            foreach (var tok in c["shapes"] as JArray ?? new JArray())
            {
                if (cv.Shapes.Count >= UiDrawCanvas.MaxShapes) break;
                if (!(tok is JObject so)) continue;
                var s = new UiDrawCanvas.Shape { Id = S(so["id"]) };
                switch (S(so["type"]) ?? "")
                {
                    case "rect":
                    {
                        s.Type = UiDrawCanvas.Kind.Rect;
                        var a = U(Fl1(so["x"], 0f), Fl1(so["y"], 0f));
                        float w = Fl1(so["w"] ?? so["width"], 10f), h = Fl1(so["h"] ?? so["height"], 10f);
                        var b = lonlat ? U(Fl1(so["x"], 0f) + w, Fl1(so["y"], 0f) - h) : a + new Vector2(w, h);
                        s.Pts = new[] { a, b };
                        s.R = Fl1(so["r"], 0f);
                        s.Fill = true;
                        break;
                    }
                    case "circle":
                        s.Type = UiDrawCanvas.Kind.Circle; s.Pts = new[] { U(Fl1(so["x"], 0f), Fl1(so["y"], 0f)) }; s.R = Mathf.Max(0f, Fl1(so["r"], 5f)) * (lonlat ? cosK : 1f); s.Fill = true;
                        break;
                    case "arc":
                        s.Type = UiDrawCanvas.Kind.Arc; s.Pts = new[] { U(Fl1(so["x"], 0f), Fl1(so["y"], 0f)) }; s.R = Mathf.Max(0f, Fl1(so["r"], 5f)); s.From = Fl1(so["from"], 0f); s.To = Fl1(so["to"], 360f); s.WidthMm = 3f;
                        break;
                    case "line":
                        s.Type = UiDrawCanvas.Kind.Line;
                        s.Pts = so["points"] is JArray lp && lp.Count >= 2 ? lp.Take(UiDrawCanvas.MaxPoints).Select(Pt).ToArray() : new[] { U(Fl1(so["x1"], 0f), Fl1(so["y1"], 0f)), U(Fl1(so["x2"], 0f), Fl1(so["y2"], 0f)) };
                        break;
                    case "polyline": case "polygon":
                    {
                        bool poly = S(so["type"]) == "polygon";
                        s.Type = poly ? UiDrawCanvas.Kind.Polygon : UiDrawCanvas.Kind.Polyline;
                        s.Pts = (so["points"] as JArray ?? new JArray()).Take(UiDrawCanvas.MaxPoints).Select(Pt).ToArray();
                        if (s.Pts.Length < 2) continue;
                        if (poly && s.Pts.Length > 2 && s.Pts[0] == s.Pts[s.Pts.Length - 1]) s.Pts = s.Pts.Take(s.Pts.Length - 1).ToArray();
                        s.Fill = poly;
                        break;
                    }
                    case "text":
                        if (texts >= UiDrawCanvas.MaxTexts) continue;
                        texts++;
                        s.Type = UiDrawCanvas.Kind.Text; s.Pts = new[] { U(Fl1(so["x"], 0f), Fl1(so["y"], 0f)) }; s.Text = S(so["text"]) ?? "";
                        string al = S(so["align"]);
                        s.Align = al == "left" ? TextAlignmentOptions.MidlineLeft : al == "right" ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Center;
                        s.HasStroke = false;
                        break;
                    default: continue;
                }
                if (total + s.Pts.Length > UiDrawCanvas.MaxTotalPoints) break;
                total += s.Pts.Length;
                if (so["stroke_width"] != null || so["width"] != null && s.Type != UiDrawCanvas.Kind.Rect) s.WidthMm = Fl1(so["stroke_width"] ?? so["width"], s.WidthMm);
                if (so["size"] != null) s.SizeMm = Mathf.Clamp(Fl1(so["size"], 11f), 5f, 40f);
                StyleShape(s, so);
                if (s.Type == UiDrawCanvas.Kind.Polygon && s.Fill) s.Tris = UiDrawCanvas.Triangulate(s.Pts);
                s.LabelAt = UiDrawCanvas.LabelPoint(s);
                cv.Shapes.Add(s);
            }
            cv.OnTap = (sid, u) =>
            {
                float x = lonlat ? bounds[0] + u.x / Mathf.Max(1e-6f, cosK) : u.x, y = lonlat ? bounds[3] - u.y : u.y;
                string xy = Num((float)Math.Round(x, lonlat ? 5 : 2)) + "," + Num((float)Math.Round(y, lonlat ? 5 : 2));
                string what = sid.Length > 0 ? sid : xy;
                ViewValue(v, "shape", sid, xy, what, false);
            };
            p.Add(cv);
            Bind(v, path, c, () => cv.LocalAt, n =>
            {
                var shapes = n["shapes"] as JArray;
                if (shapes != null)
                {
                    int k = 0;
                    foreach (var tok in shapes)
                    {
                        if (!(tok is JObject so) || k >= cv.Shapes.Count) break;
                        string tp = S(so["type"]) ?? "";
                        if (tp != "rect" && tp != "circle" && tp != "arc" && tp != "line" && tp != "polyline" && tp != "polygon" && tp != "text") continue;
                        var s = cv.Shapes[k++];
                        if (s.Type == UiDrawCanvas.Kind.Text) s.Text = S(so["text"]) ?? "";
                        StyleShape(s, so);
                    }
                }
                cv.Selected = S(n["selected"]) ?? (cv.LocalAt > 0 ? cv.Selected : null);
                cv.Restyle();
            });
        }

        /// tone (the base colour, default accent), fill (false, true or a colour; default on for rect, circle, polygon),
        /// stroke (false or a colour; default the tone), opacity (the fill, default 0.28), label.
        private static void StyleShape(UiDrawCanvas.Shape s, JObject so)
        {
            var t = UiTheme.Current;
            string tone = S(so["tone"]) ?? S(so["color"]);
            s.Tone = UiViz.Tone(tone, t.Accent);
            var fill = so["fill"];
            bool closed = s.Type == UiDrawCanvas.Kind.Rect || s.Type == UiDrawCanvas.Kind.Circle || s.Type == UiDrawCanvas.Kind.Polygon;
            if (fill != null && fill.Type == JTokenType.Boolean) s.Fill = closed && (bool)fill;
            else if (fill != null && fill.Type == JTokenType.String) { s.Fill = closed; s.FillColor = UiViz.Tone((string)fill, s.Tone); }
            else s.Fill = closed;
            if (fill == null || fill.Type != JTokenType.String) s.FillColor = s.Tone;
            var stroke = so["stroke"];
            if (s.Type != UiDrawCanvas.Kind.Text)
            {
                s.HasStroke = !(stroke != null && stroke.Type == JTokenType.Boolean && !(bool)stroke);
                s.Stroke = stroke != null && stroke.Type == JTokenType.String ? UiViz.Tone((string)stroke, s.Tone) : s.Tone;
            }
            s.Opacity = Mathf.Clamp01(Fl1(so["opacity"], 0.28f));
            s.Label = S(so["label"]);
        }

        // ------------------------------------------------------------------ the select list window

        private UiPanel _vpPanel;
        private UiList _vpList;
        private UiTextField _vpSearch;
        private UiLabel _vpHint;
        private List<(string label, string value)> _vpAll = new List<(string, string)>();
        private int _vpCurrent = -1;
        private Action<int> _vpPick;
        private const int SearchAbove = 15;

        private void EnsureViewPicker()
        {
            if (_vpPanel != null) return;
            var t = UiTheme.Current;
            var p = Panel<UiPanel>("viewpick", "Choose", 0.42f, 0.52f);
            _vpPanel = p;
            var c = p.Content;
            var st = new UiStack(c, t.Gap);
            var top = st.Next(t.ButtonHeight);
            _vpHint = p.Add(new UiLabel("", t.SmallSize) { Rect = top, Color = t.TextDim });
            _vpSearch = p.Add(new UiTextField("", "", s => FilterViewPicker(s), "search…") { Rect = top, CaptionWidth = 0f });
            _vpSearch.OnChanged = s => FilterViewPicker(s);
            _vpList = p.Add(new UiList(0.034f, (item, idx) =>
            {
                var cb = _vpPick;
                _vpPick = null;
                p.Close();
                if (item?.Tag is int k) cb?.Invoke(k);
            }) { Rect = st.Next(c.height - t.ButtonHeight * 2 - t.Gap * 2), EmptyText = "Nothing matches: change the search" });
            p.Add(new UiButton("CANCEL", () => { _vpPick = null; p.Close(); }) { Rect = st.Next(t.ButtonHeight) });
        }

        private void FilterViewPicker(string q)
        {
            if (_vpList == null) return;
            q = (q ?? "").Trim();
            var items = new List<UiListItem>(_vpAll.Count);
            for (int k = 0; k < _vpAll.Count; k++)
            {
                if (q.Length > 0 && _vpAll[k].label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                items.Add(new UiListItem { Icon = k == _vpCurrent ? "◆" : "◇", Title = _vpAll[k].label, Tag = k, TitleColor = k == _vpCurrent ? UiTheme.Current.TextAccent : (Color?)null });
            }
            _vpList.SetItems(items);
        }

        /// The options of a `select`, in front of the window that asked: a list to tap (a search field above it when there
        /// are more than 15).
        private void OpenViewPicker(UiPanel near, string title, List<(string label, string value)> options, int current, Action<int> onPick)
        {
            EnsureViewPicker();
            _vpPanel.SetTitle(UiViz.Cut(title, 30));
            _vpAll = options;
            _vpCurrent = current;
            _vpPick = onPick;
            bool search = options.Count > SearchAbove;
            _vpSearch.Set("");
            _vpSearch.SetVisible(search);
            _vpHint.SetVisible(!search);
            _vpHint.Set(options.Count + " options: tap one");
            FilterViewPicker("");
            if (near != null)
            {
                var fwd = near.transform.forward;
                _vpPanel.transform.SetPositionAndRotation(near.transform.position - fwd * 0.09f + Vector3.down * 0.03f, near.transform.rotation);
                _placed.Add("viewpick");
            }
            OpenPanel("viewpick");
        }
    }

    /// A built control that takes its component's new spec in place (UiViewWatch).
    public sealed class ViewBinding
    {
        /// Where the component is in the window's spec (JSON path: components[2].tabs[0].components[1]).
        public string Path;
        /// The component's JSON when last seen: a new spec with the same text changes nothing.
        public string Key;
        public Func<float> LocalAt;
        public Action<JObject> Apply;
        /// A changed component waiting for the user's own change to settle (1 s).
        public JObject Pending;
    }

    /// One per plugin window with bound controls: when the window's spec object is replaced without a rebuild (the same
    /// layout, ShellPluginUi.TryUpdateInPlace), each bound component whose JSON changed gets its new spec. A reference
    /// compare per frame; the JSON is read only when the spec changed (at most once a second: the host's rate).
    public sealed class UiViewWatch : UiElement
    {
        private readonly MetaCodexShell.AgentView _v;
        private JObject _seen;
        private int _pending;

        public UiViewWatch(MetaCodexShell.AgentView v) { _v = v; _seen = v.Spec; }
        protected override void Build() { }

        public override void Update()
        {
            var spec = _v.Spec;
            if (!ReferenceEquals(spec, _seen)) { _seen = spec; Scan(spec); }
            if (_pending == 0) return;
            float now = Time.unscaledTime;
            var list = _v.Bindings;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (b.Pending == null || now - b.LocalAt() < 1f) continue;
                var c = b.Pending;
                b.Pending = null;
                _pending--;
                try { b.Apply(c); }
                catch (Exception ex) { Debug.LogWarning("[views] " + _v.Id + " " + b.Path + ": update in place failed: " + ex.Message); }
            }
        }

        private void Scan(JObject spec)
        {
            if (spec == null) return;
            var list = _v.Bindings;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                JObject c;
                try { c = spec.SelectToken(b.Path) as JObject; }
                catch (Exception) { c = null; }
                if (c == null) continue;
                string key = c.ToString(Newtonsoft.Json.Formatting.None);
                if (key == b.Key) continue;
                b.Key = key;
                if (b.Pending == null) _pending++;
                b.Pending = c;
            }
        }
    }
}
