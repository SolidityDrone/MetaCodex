using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Colours and number formats for charts and diagrams. The series palette is the validated categorical one
    /// for a dark surface (CVD-safe in this order, >= 3:1 on the panel glass): assign in order, never cycled; a
    /// ninth series folds into "other".
    public static class UiViz
    {
        public static readonly int[] SeriesHex = { 0x3987E5, 0xD95926, 0x199E70, 0xC98500, 0xD55181, 0x008300, 0x9085E9, 0xE66767 };
        public static Color Series(int i) => UiTheme.Hex(SeriesHex[Mathf.Clamp(i, 0, SeriesHex.Length - 1)]);

        /// Status / emphasis words ("ok", "warn", "error", "accent", "dim") or a colour ("#35D0FF", "red", "series3").
        public static Color Tone(string tone, Color? fallback = null)
        {
            var t = UiTheme.Current;
            switch ((tone ?? "").Trim().ToLowerInvariant())
            {
                case "": return fallback ?? t.Accent;
                case "ok": case "good": case "success": case "done": case "pass": return t.Ok;
                case "warn": case "warning": case "pending": return t.Warn;
                case "bad": case "error": case "danger": case "critical": case "fail": return t.Danger;
                case "accent": case "info": case "primary": return t.Accent;
                case "violet": return t.Violet;
                case "dim": case "muted": case "neutral": return t.TextDim;
            }
            string s = tone.Trim();
            if (s.StartsWith("series", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.Substring(6), out int k)) return Series(k - 1);
            if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
            if (ColorUtility.TryParseHtmlString("#" + s, out c)) return c;
            return fallback ?? t.Accent;
        }

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        /// Short numbers for axes and labels: 1234 -> 1.2k, 0.125 -> 0.13.
        public static string Num(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e9) return (v / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B";
            if (a >= 1e6) return (v / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e4) return (v / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k";
            if (a >= 1e3) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            if (a >= 100 || Math.Abs(v - Math.Round(v)) < 1e-9) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a >= 1) return v.ToString("0.#", CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// "Nice" axis: 0 or a round lower bound, a round upper bound, and the tick step (about `ticks` ticks).
        public static (float lo, float hi, float step) NiceRange(float min, float max, int ticks = 4)
        {
            if (float.IsNaN(min) || float.IsNaN(max)) { min = 0; max = 1; }
            if (Mathf.Approximately(min, max)) { max = min + (Mathf.Abs(min) > 0 ? Mathf.Abs(min) * 0.5f : 1f); }
            float range = NiceNum(max - min, false);
            float step = NiceNum(range / Mathf.Max(1, ticks - 1), true);
            float lo = Mathf.Floor(min / step) * step, hi = Mathf.Ceil(max / step) * step;
            return (lo, hi, step);
        }

        private static float NiceNum(float x, bool round)
        {
            if (x <= 0) return 1f;
            float exp = Mathf.Floor(Mathf.Log10(x));
            float f = x / Mathf.Pow(10, exp);
            float nf = round ? (f < 1.5f ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
            return nf * Mathf.Pow(10, exp);
        }

        /// Rough width of a text in metres for a line height (Inter / the terminal font: ~0.42 line heights per character).
        public static float TextWidth(string s, float lineHeight) => (s ?? "").Length * lineHeight * 0.42f;

        public static string Cut(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, Math.Max(1, n - 1)) + "…" : s; }

        public static string NoParse(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";
    }

    /// An element that draws itself from shapes and texts it allocates on demand (charts, diagrams, tables):
    /// Redraw reuses them in the same order and hides what is left over, so its contents can change freely
    /// without growing the panel's mesh.
    public abstract class UiCanvas : UiElement
    {
        private readonly List<int> _shapes = new List<int>();
        private readonly List<UiText> _texts = new List<UiText>();
        private int _ns, _nt;

        protected override void Build() => Redraw();

        public void Redraw()
        {
            if (Panel == null) return;
            _ns = 0; _nt = 0;
            if (Visible) Draw();
            for (int i = _ns; i < _shapes.Count; i++) Panel.Shapes.SetVisible(_shapes[i], false);
            for (int i = _nt; i < _texts.Count; i++) _texts[i].SetVisible(false);
        }

        protected abstract void Draw();

        protected int AddShape(UiShape s)
        {
            s.Visible = true;
            if (_ns < _shapes.Count) { int id = _shapes[_ns++]; Panel.Shapes.Set(id, s); return id; }
            int nid = Panel.Shapes.Add(s);
            _shapes.Add(nid);
            _ns++;
            return nid;
        }

        /// A box: fill 0 = quiet surface (state lights it), 1 = filled with the tint, 2 = ghost.
        protected int Box(Rect r, Color tint, float radius = 0.003f, int fill = 1, float state = 1f, float margin = 0.002f, float rotation = 0f)
        {
            var s = Panel.Shape(UiShapeKind.Button, r, tint, radius, fill, margin, state);
            s.Rotation = rotation;
            return AddShape(s);
        }

        /// A solid pill / dot (Bar kind, full).
        protected int Pill(Rect r, Color tint) => AddShape(Panel.Shape(UiShapeKind.Bar, r, tint, r.height * 0.5f, 0f, 0.0015f, 1f));

        protected int Dot(Vector2 c, float d, Color tint) => Pill(new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d), tint);

        protected int Stroke(Vector2 a, Vector2 b, float w, Color tint) => AddShape(Panel.Segment(a, b, w, tint));

        /// A straight piece with square ends (arcs of rings and donuts: round caps would scallop the edge).
        protected int Piece(Vector2 a, Vector2 b, float w, Color tint)
        {
            var s = Panel.Segment(a, b, w, tint);
            s.CutBig = 0f;
            s.Size = new Vector2(s.Size.x + 0.0004f, s.Size.y); // a hair longer: no seams between pieces
            return AddShape(s);
        }

        /// A recessive hairline (grid lines, dividers).
        protected int Hair(Vector2 a, Vector2 b, Color tint, float state = 0f)
        {
            var s = Panel.Segment(a, b, 0.0012f, tint, UiShapeKind.Line);
            s.State = state;
            s.Margin = 0.001f;
            return AddShape(s);
        }

        protected void Dashed(Vector2 a, Vector2 b, float w, Color tint, float dash = 0.006f, float gap = 0.004f)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1e-4f) return;
            var dir = (b - a) / len;
            for (float t = 0; t < len; t += dash + gap) Stroke(a + dir * t, a + dir * Mathf.Min(len, t + dash), w, tint);
        }

        protected void Arrowhead(Vector2 tip, Vector2 from, float size, float w, Color tint)
        {
            var d = tip - from;
            if (d.sqrMagnitude < 1e-8f) return;
            d.Normalize();
            var n = new Vector2(-d.y, d.x);
            Stroke(tip, tip - d * size + n * size * 0.55f, w, tint);
            Stroke(tip, tip - d * size - n * size * 0.55f, w, tint);
        }

        protected UiText Txt(Rect r, string text, float lineHeight, Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, TMP_FontAsset font = null, bool wrap = false)
        {
            font = font ?? Theme.Body;
            UiText t;
            if (_nt < _texts.Count) t = _texts[_nt];
            else { t = UiText.Create(Panel, r, "", font, lineHeight, color, align, wrap); _texts.Add(t); }
            _nt++;
            var tmp = t.Tmp;
            if (font != null && tmp.font != font) UiText.ApplyFont(tmp, font);
            bool display = font == Theme.Display;
            float fs = Theme.FontSizeFor(lineHeight) * (display ? 1.12f : 1f);
            if (!Mathf.Approximately(tmp.fontSize, fs)) tmp.fontSize = fs;
            tmp.characterSpacing = display ? 3f : 0f;
            tmp.color = color;
            tmp.alignment = align;
            tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.SetRect(r);
            t.Text = text;
            t.SetVisible(true);
            return t;
        }

        protected override void OnVisibility(bool v) => Redraw();

        protected override void Teardown()
        {
            foreach (var id in _shapes) Panel.Shapes.SetVisible(id, false);
            foreach (var t in _texts) DestroyGo(t.Tmp.gameObject);
            _shapes.Clear();
            _texts.Clear();
        }
    }

    // ---------------------------------------------------------------------------------------------- charts

    /// bar | hbar | line | area | scatter | donut | pie | sparkline. Series share one axis (never two scales);
    /// two or more series get a legend; hovering a mark shows its value in the readout line.
    public sealed class UiChart : UiCanvas
    {
        public string Kind = "bar";
        public string Title = "";
        public string Unit = "";
        public readonly List<string> Labels = new List<string>();
        public readonly List<(string name, List<float> data)> Series = new List<(string, List<float>)>();
        /// scatter: points per series
        public readonly List<(string name, List<Vector2> points)> Points = new List<(string, List<Vector2>)>();
        public float? Min, Max;
        public override bool Interactive => true;
        private int _hs = -1, _hi = -1;
        private Rect _plot;

        private const float Tick = 0.0098f, LegendH = 0.018f;

        public void SetHover(int series, int index) { if (_hs == series && _hi == index) return; _hs = series; _hi = index; Redraw(); }

        protected override void Draw()
        {
            var t = Theme;
            var r = Rect;
            float top = r.y;
            string readout = Readout();
            if (!string.IsNullOrEmpty(Title) || readout != null)
            {
                Txt(new Rect(r.x, top, r.width * 0.55f, LegendH), "<b>" + UiViz.NoParse(Title) + "</b>", 0.0115f, t.Text);
                if (readout != null) Txt(new Rect(r.x + r.width * 0.35f, top, r.width * 0.65f, LegendH), readout, 0.0105f, t.TextAccent, TextAlignmentOptions.MidlineRight, t.Mono);
                top += LegendH;
            }
            string kind = (Kind ?? "bar").ToLowerInvariant();
            bool circle = kind == "donut" || kind == "pie";
            int legendCount = circle ? 0 : kind == "scatter" ? Points.Count : Series.Count;
            if (legendCount >= 2) top = Legend(new Rect(r.x, top, r.width, LegendH), kind == "scatter" ? Points.Select(p => p.name).ToList() : Series.Select(s => s.name).ToList());
            _plot = new Rect(r.x, top + 0.003f, r.width, Mathf.Max(0.02f, r.yMax - top - 0.003f));
            switch (kind)
            {
                case "hbar": DrawHBar(); break;
                case "line": case "area": case "sparkline": DrawLine(kind); break;
                case "scatter": DrawScatter(); break;
                case "donut": case "pie": DrawDonut(kind == "donut"); break;
                default: DrawBars(); break;
            }
        }

        private float Legend(Rect r, List<string> names)
        {
            var t = Theme;
            float x = r.x, y = r.y;
            for (int i = 0; i < names.Count && i < UiViz.SeriesHex.Length; i++)
            {
                string n = UiViz.Cut(names[i], 18);
                float w = 0.016f + UiViz.TextWidth(n, 0.0105f);
                if (x + w > r.xMax && x > r.x) { x = r.x; y += LegendH * 0.9f; }
                Pill(new Rect(x, y + LegendH * 0.5f - 0.0025f, 0.009f, 0.005f), UiViz.Series(i));
                Txt(new Rect(x + 0.012f, y, w, LegendH), UiViz.NoParse(n), 0.0105f, t.TextDim);
                x += w + 0.01f;
            }
            return y + LegendH;
        }

        private string Readout()
        {
            if (_hs < 0 && _hi < 0) return null;
            string k = (Kind ?? "bar").ToLowerInvariant();
            if (k == "scatter")
            {
                if (_hs < 0 || _hs >= Points.Count || _hi < 0 || _hi >= Points[_hs].points.Count) return null;
                var p = Points[_hs].points[_hi];
                return $"{UiViz.NoParse(Points[_hs].name)} · {UiViz.Num(p.x)}, {UiViz.Num(p.y)}{Unit}";
            }
            if (_hs < 0 || _hs >= Series.Count || _hi < 0 || _hi >= Series[_hs].data.Count) return null;
            string label = _hi < Labels.Count ? Labels[_hi] : "#" + (_hi + 1);
            string name = Series.Count > 1 ? " · " + Series[_hs].name : "";
            if (k == "donut" || k == "pie")
            {
                float sum = Series[0].data.Where(v => v > 0).Sum();
                return $"{UiViz.NoParse(label)} · {UiViz.Num(Series[0].data[_hi])}{Unit} ({(sum > 0 ? Series[0].data[_hi] / sum * 100f : 0):0}%)";
            }
            return $"{UiViz.NoParse(label)}{UiViz.NoParse(name)}: {UiViz.Num(Series[_hs].data[_hi])}{Unit}";
        }

        private (float lo, float hi, float step) ValueRange(bool zero)
        {
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var s in Series) foreach (var v in s.data) { if (float.IsNaN(v)) continue; mn = Mathf.Min(mn, v); mx = Mathf.Max(mx, v); }
            if (mn == float.MaxValue) { mn = 0; mx = 1; }
            if (zero) { mn = Mathf.Min(0, mn); mx = Mathf.Max(0, mx); }
            if (Min.HasValue) mn = Min.Value;
            if (Max.HasValue) mx = Max.Value;
            return UiViz.NiceRange(mn, mx);
        }

        /// Left axis labels and recessive grid lines; returns the inner plot rect.
        private Rect Axes(float lo, float hi, float step, bool labelsBelow, out Func<float, float> yOf)
        {
            var t = Theme;
            float axisW = Mathf.Max(UiViz.TextWidth(UiViz.Num(hi), Tick), UiViz.TextWidth(UiViz.Num(lo), Tick)) + 0.008f;
            var inner = new Rect(_plot.x + axisW, _plot.y + 0.004f, _plot.width - axisW, _plot.height - 0.004f - (labelsBelow ? 0.017f : 0f));
            float y0 = inner.yMax, h = inner.height;
            float span = Mathf.Max(hi - lo, 1e-6f);
            yOf = v => y0 - (v - lo) / span * h;
            var f = yOf;
            for (float v = lo; v <= hi + step * 0.01f; v += step)
            {
                float y = f(v);
                Hair(new Vector2(inner.x, y), new Vector2(inner.xMax, y), t.Frame, Mathf.Approximately(v, 0f) ? 0.6f : 0f);
                Txt(new Rect(_plot.x, y - 0.007f, axisW - 0.004f, 0.014f), UiViz.Num(v), Tick, t.TextDim, TextAlignmentOptions.MidlineRight);
            }
            return inner;
        }

        private void XLabels(Rect inner, int n, Func<int, float> xOf)
        {
            var t = Theme;
            if (n == 0) return;
            float slot = inner.width / n;
            int every = Mathf.Max(1, Mathf.CeilToInt(n / Mathf.Max(1f, inner.width / 0.045f)));
            int chars = Mathf.Max(3, Mathf.FloorToInt(slot * every / (Tick * 0.42f)));
            for (int i = 0; i < n; i += every)
            {
                string l = i < Labels.Count ? Labels[i] : (i + 1).ToString();
                float cx = xOf(i);
                Txt(new Rect(cx - slot * every * 0.5f, inner.yMax + 0.002f, slot * every, 0.015f), UiViz.NoParse(UiViz.Cut(l, chars)), Tick, t.TextDim, TextAlignmentOptions.Center);
            }
        }

        private void DrawBars()
        {
            var t = Theme;
            int n = Series.Count == 0 ? 0 : Series.Max(s => s.data.Count);
            if (n == 0) { Empty(); return; }
            var (lo, hi, step) = ValueRange(true);
            var inner = Axes(lo, hi, step, true, out var yOf);
            int sc = Mathf.Min(Series.Count, UiViz.SeriesHex.Length);
            float group = inner.width / n, inGroup = group * 0.72f;
            float bw = Mathf.Max(0.002f, (inGroup - 0.0015f * (sc - 1)) / sc);
            bool labels = n * sc <= 14;
            for (int i = 0; i < n; i++)
            {
                float gx = inner.x + i * group + (group - inGroup) * 0.5f;
                for (int s = 0; s < sc; s++)
                {
                    if (i >= Series[s].data.Count || float.IsNaN(Series[s].data[i])) continue;
                    float v = Series[s].data[i];
                    float y0 = yOf(0), y1 = yOf(v);
                    var rect = new Rect(gx + s * (bw + 0.0015f), Mathf.Min(y0, y1), bw, Mathf.Max(0.001f, Mathf.Abs(y1 - y0)));
                    bool hov = s == _hs && i == _hi;
                    Box(rect, UiViz.Series(s), Mathf.Min(0.002f, bw * 0.3f), 1, hov ? 1f : _hi >= 0 ? 0.25f : 0.55f, 0.0015f);
                    if (labels) Txt(new Rect(rect.x - 0.02f, (v >= 0 ? rect.y - 0.015f : rect.yMax + 0.001f), rect.width + 0.04f, 0.014f), UiViz.Num(v), Tick, t.TextDim, TextAlignmentOptions.Center);
                }
            }
            XLabels(inner, n, i => inner.x + (i + 0.5f) * group);
        }

        private void DrawHBar()
        {
            var t = Theme;
            var s0 = Series.Count > 0 ? Series[0].data : new List<float>();
            int n = s0.Count;
            if (n == 0) { Empty(); return; }
            float max = Mathf.Max(1e-6f, Max ?? s0.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max());
            float rowH = Mathf.Min(0.026f, _plot.height / n);
            float labelW = Mathf.Min(_plot.width * 0.38f, Labels.Take(n).Select(l => UiViz.TextWidth(l, 0.0105f)).DefaultIfEmpty(0.04f).Max() + 0.006f);
            float valueW = 0.045f;
            float barX = _plot.x + labelW, barW = _plot.width - labelW - valueW;
            for (int i = 0; i < n; i++)
            {
                float y = _plot.y + i * rowH;
                float v = float.IsNaN(s0[i]) ? 0 : s0[i];
                float w = Mathf.Max(0.002f, barW * Mathf.Clamp01(v / max));
                bool hov = _hs == 0 && _hi == i;
                Txt(new Rect(_plot.x, y, labelW - 0.006f, rowH), UiViz.NoParse(UiViz.Cut(i < Labels.Count ? Labels[i] : "#" + (i + 1), Mathf.Max(4, (int)(labelW / (0.0105f * 0.42f))))), 0.0105f, hov ? t.Text : t.TextDim, TextAlignmentOptions.MidlineRight);
                Box(new Rect(barX, y + rowH * 0.2f, w, rowH * 0.6f), UiViz.Series(0), 0.002f, 1, hov ? 1f : 0.75f, 0.0015f);
                Txt(new Rect(barX + w + 0.004f, y, valueW + barW - w, rowH), UiViz.Num(v) + Unit, Tick, t.TextDim);
            }
        }

        private void DrawLine(string kind)
        {
            var t = Theme;
            int n = Series.Count == 0 ? 0 : Series.Max(s => s.data.Count);
            if (n == 0) { Empty(); return; }
            bool spark = kind == "sparkline";
            float lo, hi, step;
            Func<float, float> yOf;
            Rect inner;
            if (spark)
            {
                var vals = Series.SelectMany(s => s.data).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).ToList();
                lo = vals.Min(); hi = vals.Max(); if (Mathf.Approximately(lo, hi)) hi = lo + 1;
                inner = new Rect(_plot.x, _plot.y + 0.004f, _plot.width - 0.05f, _plot.height - 0.008f);
                float l0 = lo, h0 = hi; var ir = inner;
                yOf = v => ir.yMax - (v - l0) / (h0 - l0) * ir.height;
            }
            else
            {
                (lo, hi, step) = ValueRange(kind == "area");
                inner = Axes(lo, hi, step, true, out yOf);
            }
            float X(int i) => n <= 1 ? inner.center.x : inner.x + inner.width * i / (n - 1f);
            int sc = Mathf.Min(Series.Count, UiViz.SeriesHex.Length);
            for (int s = 0; s < sc; s++)
            {
                var d = Series[s].data;
                var col = UiViz.Series(s);
                if (kind == "area")
                {
                    // a translucent fill under the line, as narrow columns
                    int cols = Mathf.Clamp((int)(inner.width / 0.006f), 8, 96);
                    var fill = col; fill.a = 0.22f;
                    for (int c = 0; c < cols; c++)
                    {
                        float fx = (c + 0.5f) / cols * (n - 1);
                        int i0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, n - 1), i1 = Mathf.Min(n - 1, i0 + 1);
                        if (i0 >= d.Count || i1 >= d.Count) continue;
                        float v = Mathf.Lerp(d[i0], d[i1], fx - i0);
                        float y = yOf(v), y0 = yOf(Mathf.Max(lo, 0));
                        float cw = inner.width / cols;
                        AddShape(Panel.Shape(UiShapeKind.Bar, new Rect(inner.x + c * cw, Mathf.Min(y, y0), cw + 0.0004f, Mathf.Abs(y0 - y)), fill, 0f, 0f, 0.0004f, 1f));
                    }
                }
                Vector2? prev = null;
                for (int i = 0; i < d.Count && i < n; i++)
                {
                    if (float.IsNaN(d[i])) { prev = null; continue; }
                    var p = new Vector2(X(i), yOf(d[i]));
                    if (prev.HasValue) Stroke(prev.Value, p, 0.0022f, col);
                    prev = p;
                }
                bool markers = n <= 24 && !spark;
                for (int i = 0; i < d.Count && i < n; i++)
                {
                    if (float.IsNaN(d[i])) continue;
                    var p = new Vector2(X(i), yOf(d[i]));
                    bool hov = s == _hs && i == _hi;
                    if (markers || hov) Dot(p, hov ? 0.009f : 0.0055f, hov ? t.Text : col);
                }
                // direct label at the end of each line (few series), or the last value of a sparkline
                int last = Mathf.Min(d.Count, n) - 1;
                if (last >= 0 && !float.IsNaN(d[last]) && (spark || sc <= 4))
                {
                    var p = new Vector2(X(last), yOf(d[last]));
                    string lab = spark ? UiViz.Num(d[last]) + Unit : UiViz.NoParse(UiViz.Cut(Series[s].name, 10));
                    if (spark || sc >= 2) Txt(new Rect(p.x + 0.004f, p.y - 0.007f, 0.06f, 0.014f), lab, Tick, t.TextDim);
                }
            }
            if (_hi >= 0 && _hi < n) Hair(new Vector2(X(_hi), inner.y), new Vector2(X(_hi), inner.yMax), t.Text, 0.5f);
            if (!spark) XLabels(inner, n, X);
        }

        private void DrawScatter()
        {
            var t = Theme;
            var all = Points.SelectMany(p => p.points).ToList();
            if (all.Count == 0) { Empty(); return; }
            var (xlo, xhi, xstep) = UiViz.NiceRange(all.Min(p => p.x), all.Max(p => p.x));
            var (ylo, yhi, ystep) = UiViz.NiceRange(Min ?? all.Min(p => p.y), Max ?? all.Max(p => p.y));
            var inner = Axes(ylo, yhi, ystep, true, out var yOf);
            float X(float x) => inner.x + (x - xlo) / Mathf.Max(1e-6f, xhi - xlo) * inner.width;
            for (float v = xlo; v <= xhi + xstep * 0.01f; v += xstep)
                Txt(new Rect(X(v) - 0.02f, inner.yMax + 0.002f, 0.04f, 0.015f), UiViz.Num(v), Tick, t.TextDim, TextAlignmentOptions.Center);
            int sc = Mathf.Min(Points.Count, 3); // scatter: three series at most stay distinguishable
            for (int s = 0; s < sc; s++)
                for (int i = 0; i < Points[s].points.Count; i++)
                {
                    var p = Points[s].points[i];
                    bool hov = s == _hs && i == _hi;
                    Dot(new Vector2(X(p.x), yOf(p.y)), hov ? 0.01f : 0.0065f, hov ? t.Text : UiViz.Series(s));
                }
        }

        private void DrawDonut(bool hole)
        {
            var t = Theme;
            var d = Series.Count > 0 ? Series[0].data : new List<float>();
            float sum = d.Where(v => v > 0 && !float.IsNaN(v)).Sum();
            if (sum <= 0) { Empty(); return; }
            float legendW = Mathf.Min(_plot.width * 0.45f, 0.2f);
            float R = Mathf.Min((_plot.width - legendW) * 0.5f, _plot.height * 0.5f) - 0.004f;
            var c = new Vector2(_plot.x + R + 0.004f, _plot.center.y);
            float thick = hole ? R * 0.34f : R * 0.92f;
            float rm = R - thick * 0.5f;
            float a = 90f; // start at 12 o'clock, clockwise
            int count = Mathf.Min(d.Count, UiViz.SeriesHex.Length);
            for (int i = 0; i < count; i++)
            {
                if (d[i] <= 0 || float.IsNaN(d[i])) continue;
                float sweep = d[i] / sum * 360f;
                float gap = Mathf.Min(1.2f, sweep * 0.2f);
                float a0 = a - gap * 0.5f, a1 = a - sweep + gap * 0.5f;
                bool hov = _hi == i;
                Arc(c, hov ? rm + 0.002f : rm, a0, a1, hov ? thick + 0.004f : thick, UiViz.Series(i));
                a -= sweep;
            }
            string center = _hi >= 0 && _hi < d.Count ? $"{(d[_hi] / sum * 100f):0}%" : UiViz.Num(sum) + Unit;
            if (hole) Txt(new Rect(c.x - R, c.y - 0.01f, R * 2, 0.02f), "<b>" + center + "</b>", 0.016f, t.Text, TextAlignmentOptions.Center, t.Display);
            float ly = _plot.y + Mathf.Max(0f, (_plot.height - count * 0.02f) * 0.5f);
            for (int i = 0; i < count; i++)
            {
                float lx = _plot.xMax - legendW;
                Pill(new Rect(lx, ly + i * 0.02f + 0.0075f, 0.009f, 0.005f), UiViz.Series(i));
                string l = i < Labels.Count ? Labels[i] : "#" + (i + 1);
                Txt(new Rect(lx + 0.013f, ly + i * 0.02f, legendW - 0.013f, 0.02f), $"{UiViz.NoParse(UiViz.Cut(l, 16))} <color=#{UiViz.Hex(t.TextDim)}>{(d[i] > 0 ? d[i] / sum * 100f : 0):0}%</color>", 0.0105f, t.Text);
            }
        }

        /// A ring segment from angle a0 down to a1 (degrees, counter-clockwise from +x; panel y is down).
        private void Arc(Vector2 c, float r, float a0, float a1, float w, Color col)
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(a1 - a0) / 4f));
            Vector2 P(float deg) { float rad = deg * Mathf.Deg2Rad; return c + new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad)) * r; }
            var prev = P(a0);
            for (int k = 1; k <= steps; k++)
            {
                var p = P(Mathf.Lerp(a0, a1, k / (float)steps));
                Piece(prev, p, w, col);
                prev = p;
            }
        }

        private void Empty() => Txt(_plot, "no data", 0.011f, Theme.TextDim, TextAlignmentOptions.Center);

        public override void OnHover(UiPointerEvent e)
        {
            var p = e.Point;
            string k = (Kind ?? "bar").ToLowerInvariant();
            int hs = -1, hi = -1;
            if (k == "scatter")
            {
                float best = 0.02f;
                var all = Points.SelectMany(q => q.points).ToList();
                if (all.Count > 0)
                {
                    var (xlo, xhi, _) = UiViz.NiceRange(all.Min(q => q.x), all.Max(q => q.x));
                    var (ylo, yhi, _) = UiViz.NiceRange(Min ?? all.Min(q => q.y), Max ?? all.Max(q => q.y));
                    float axisW = Mathf.Max(UiViz.TextWidth(UiViz.Num(yhi), Tick), UiViz.TextWidth(UiViz.Num(ylo), Tick)) + 0.008f;
                    var inner = new Rect(_plot.x + axisW, _plot.y + 0.004f, _plot.width - axisW, _plot.height - 0.021f);
                    for (int s = 0; s < Points.Count; s++)
                        for (int i = 0; i < Points[s].points.Count; i++)
                        {
                            var q = Points[s].points[i];
                            var sp = new Vector2(inner.x + (q.x - xlo) / Mathf.Max(1e-6f, xhi - xlo) * inner.width, inner.yMax - (q.y - ylo) / Mathf.Max(1e-6f, yhi - ylo) * inner.height);
                            float dd = Vector2.Distance(sp, p);
                            if (dd < best) { best = dd; hs = s; hi = i; }
                        }
                }
            }
            else if (k == "donut" || k == "pie")
            {
                var d = Series.Count > 0 ? Series[0].data : new List<float>();
                float sum = d.Where(v => v > 0).Sum();
                float legendW = Mathf.Min(_plot.width * 0.45f, 0.2f);
                float R = Mathf.Min((_plot.width - legendW) * 0.5f, _plot.height * 0.5f) - 0.004f;
                var c = new Vector2(_plot.x + R + 0.004f, _plot.center.y);
                var v2 = p - c;
                if (sum > 0 && v2.magnitude <= R + 0.004f)
                {
                    float ang = Mathf.Atan2(-v2.y, v2.x) * Mathf.Rad2Deg; // ccw from +x
                    float from12 = Mathf.Repeat(90f - ang, 360f);       // clockwise from 12 o'clock
                    float acc = 0;
                    for (int i = 0; i < d.Count; i++)
                    {
                        if (d[i] <= 0) continue;
                        acc += d[i] / sum * 360f;
                        if (from12 <= acc) { hs = 0; hi = i; break; }
                    }
                }
            }
            else if (k == "hbar")
            {
                int n = Series.Count > 0 ? Series[0].data.Count : 0;
                float rowH = n > 0 ? Mathf.Min(0.026f, _plot.height / n) : 1f;
                int i = Mathf.FloorToInt((p.y - _plot.y) / rowH);
                if (i >= 0 && i < n) { hs = 0; hi = i; }
            }
            else
            {
                int n = Series.Count == 0 ? 0 : Series.Max(s => s.data.Count);
                if (n > 0)
                {
                    float axisW = 0.03f;
                    var inner = new Rect(_plot.x + axisW, _plot.y, _plot.width - axisW, _plot.height);
                    if (k == "bar")
                    {
                        float group = inner.width / n;
                        int i = Mathf.Clamp(Mathf.FloorToInt((p.x - inner.x) / group), 0, n - 1);
                        int sc = Mathf.Max(1, Mathf.Min(Series.Count, UiViz.SeriesHex.Length));
                        float inGroup = group * 0.72f;
                        float rel = (p.x - (inner.x + i * group + (group - inGroup) * 0.5f)) / Mathf.Max(1e-5f, inGroup);
                        hs = Mathf.Clamp(Mathf.FloorToInt(rel * sc), 0, sc - 1); hi = i;
                    }
                    else
                    {
                        int i = n <= 1 ? 0 : Mathf.Clamp(Mathf.RoundToInt((p.x - inner.x) / inner.width * (n - 1)), 0, n - 1);
                        // the series whose value is nearest the pointer at that x
                        hs = 0; hi = i;
                        if (Series.Count > 1)
                        {
                            var (lo, hi2, _) = ValueRange(k == "area");
                            float vAt = lo + (inner.yMax - p.y) / Mathf.Max(1e-5f, inner.height) * (hi2 - lo);
                            float best = float.MaxValue;
                            for (int s = 0; s < Series.Count; s++)
                                if (i < Series[s].data.Count && Mathf.Abs(Series[s].data[i] - vAt) < best) { best = Mathf.Abs(Series[s].data[i] - vAt); hs = s; }
                        }
                    }
                }
            }
            SetHover(hs, hi);
        }

        protected override void OnInteractionChanged() { if (!Hover) SetHover(-1, -1); }
    }

    // ---------------------------------------------------------------------------------------------- diagrams

    /// A flowchart: nodes in layers (top-down or left-right), edges with arrows and labels. Small graphs, laid
    /// out by longest path + barycentre ordering, scaled down to fit.
    public sealed class UiFlow : UiCanvas
    {
        public sealed class Node { public string Id, Label, Shape = "rect", Tone; public int Layer, Order; public float X, Y, W, H; public int Index; }
        public sealed class Edge { public string From, To, Label, Style = "solid"; public bool Arrow = true; public bool Back; }
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Edge> Edges = new List<Edge>();
        public string Direction = "TD";
        public override bool Interactive => true;
        private string _hover;
        private float _scale = 1f;
        private Vector2 _offset;
        private bool _laidOut;

        private const float LabelH = 0.0115f;

        public Node Get(string id) => Nodes.FirstOrDefault(n => n.Id == id);

        public Node Ensure(string id, string label = null, string shape = null)
        {
            var n = Get(id);
            if (n == null) { n = new Node { Id = id, Label = label ?? id, Shape = shape ?? "rect", Index = Nodes.Count }; Nodes.Add(n); }
            else { if (label != null) n.Label = label; if (shape != null) n.Shape = shape; }
            _laidOut = false;
            return n;
        }

        public void Layout()
        {
            _laidOut = true;
            if (Nodes.Count == 0) return;
            bool lr = Direction == "LR" || Direction == "RL";
            var byId = Nodes.ToDictionary(n => n.Id);
            // sizes from the labels (two lines for long ones)
            foreach (var n in Nodes)
            {
                var lines = Wrap(n.Label, 22);
                float w = Mathf.Clamp(lines.Max(l => UiViz.TextWidth(l, LabelH)) + 0.018f, 0.045f, 0.19f);
                float h = 0.016f + lines.Count * LabelH;
                if (n.Shape == "diamond") { w = h = Mathf.Min(w + h, 0.2f); } // a label w x h fits a diamond of diagonal w + h
                if (n.Shape == "circle") { float d = Mathf.Max(w, h); w = h = d; }
                n.W = w; n.H = h;
            }
            // break cycles: depth-first, edges back to a node on the stack are reversed for layering
            var outs = Nodes.ToDictionary(n => n.Id, n => new List<Edge>());
            foreach (var e in Edges) { e.Back = false; if (byId.ContainsKey(e.From) && byId.ContainsKey(e.To) && e.From != e.To) outs[e.From].Add(e); }
            var state = new Dictionary<string, int>();
            void Dfs(string id)
            {
                state[id] = 1;
                foreach (var e in outs[id])
                {
                    state.TryGetValue(e.To, out int st);
                    if (st == 1) e.Back = true;
                    else if (st == 0) Dfs(e.To);
                }
                state[id] = 2;
            }
            foreach (var n in Nodes) if (!state.ContainsKey(n.Id)) Dfs(n.Id);
            // layers: longest path from the sources
            foreach (var n in Nodes) n.Layer = 0;
            for (int pass = 0; pass < Nodes.Count; pass++)
            {
                bool changed = false;
                foreach (var e in Edges)
                {
                    if (e.From == e.To || !byId.ContainsKey(e.From) || !byId.ContainsKey(e.To)) continue;
                    var a = e.Back ? byId[e.To] : byId[e.From];
                    var b = e.Back ? byId[e.From] : byId[e.To];
                    if (b.Layer < a.Layer + 1) { b.Layer = a.Layer + 1; changed = true; }
                }
                if (!changed) break;
            }
            int layers = Nodes.Max(n => n.Layer) + 1;
            var rows = new List<List<Node>>();
            for (int l = 0; l < layers; l++) rows.Add(Nodes.Where(n => n.Layer == l).OrderBy(n => n.Index).ToList());
            // order inside layers: barycentre sweeps
            for (int sweep = 0; sweep < 6; sweep++)
            {
                bool down = sweep % 2 == 0;
                for (int li = down ? 1 : layers - 2; down ? li < layers : li >= 0; li += down ? 1 : -1)
                {
                    var refRow = rows[down ? li - 1 : li + 1];
                    var pos = new Dictionary<string, int>();
                    for (int i = 0; i < refRow.Count; i++) pos[refRow[i].Id] = i;
                    var bary = new Dictionary<Node, float>();
                    foreach (var n in rows[li])
                    {
                        var nb = Edges.Where(e => e.From == n.Id && pos.ContainsKey(e.To)).Select(e => (float)pos[e.To])
                            .Concat(Edges.Where(e => e.To == n.Id && pos.ContainsKey(e.From)).Select(e => (float)pos[e.From])).ToList();
                        bary[n] = nb.Count > 0 ? nb.Average() : rows[li].IndexOf(n);
                    }
                    rows[li] = rows[li].OrderBy(n => bary[n]).ToList();
                }
            }
            // coordinates (centres), unscaled
            float gapIn = 0.022f, gapLayer = lr ? 0.045f : 0.034f;
            float along = 0f;
            for (int l = 0; l < layers; l++)
            {
                var row = rows[l];
                float thick = row.Max(n => lr ? n.W : n.H);
                float total = row.Sum(n => lr ? n.H : n.W) + gapIn * (row.Count - 1);
                float c = -total * 0.5f;
                for (int i = 0; i < row.Count; i++)
                {
                    var n = row[i];
                    n.Order = i;
                    float size = lr ? n.H : n.W;
                    float across = c + size * 0.5f;
                    c += size + gapIn;
                    if (lr) { n.X = along + thick * 0.5f; n.Y = across; }
                    else { n.X = across; n.Y = along + thick * 0.5f; }
                }
                along += thick + gapLayer;
            }
            if (Direction == "RL") foreach (var n in Nodes) n.X = -n.X;
            if (Direction == "BT") foreach (var n in Nodes) n.Y = -n.Y;
        }

        private static List<string> Wrap(string s, int max)
        {
            s = (s ?? "").Replace("<br>", "\n").Replace("<br/>", "\n").Replace("\\n", "\n");
            var lines = new List<string>();
            foreach (var part in s.Split('\n'))
            {
                var words = part.Split(' ');
                string cur = "";
                foreach (var w in words)
                {
                    if (cur.Length > 0 && cur.Length + 1 + w.Length > max) { lines.Add(cur); cur = w; }
                    else cur = cur.Length == 0 ? w : cur + " " + w;
                }
                lines.Add(cur);
            }
            if (lines.Count > 3) { lines = lines.Take(3).ToList(); lines[2] = UiViz.Cut(lines[2], max - 1) + "…"; }
            return lines;
        }

        protected override void Draw()
        {
            var t = Theme;
            if (!_laidOut) Layout();
            if (Nodes.Count == 0) { Txt(Rect, "empty diagram", 0.011f, t.TextDim, TextAlignmentOptions.Center); return; }
            float minX = Nodes.Min(n => n.X - n.W / 2), maxX = Nodes.Max(n => n.X + n.W / 2);
            float minY = Nodes.Min(n => n.Y - n.H / 2), maxY = Nodes.Max(n => n.Y + n.H / 2);
            float bw = maxX - minX, bh = maxY - minY;
            _scale = Mathf.Min(1f, (Rect.width - 0.01f) / Mathf.Max(bw, 1e-4f), (Rect.height - 0.01f) / Mathf.Max(bh, 1e-4f));
            _offset = new Vector2(Rect.center.x - (minX + bw / 2) * _scale, Rect.center.y - (minY + bh / 2) * _scale);
            float s = _scale;
            var byId = Nodes.ToDictionary(n => n.Id);
            // edges under the nodes
            foreach (var e in Edges)
            {
                if (!byId.TryGetValue(e.From, out var a) || !byId.TryGetValue(e.To, out var b)) continue;
                bool hl = _hover != null && (e.From == _hover || e.To == _hover);
                var col = hl ? t.TextAccent : t.TextDim;
                float w = (e.Style == "thick" ? 0.003f : 0.0017f) * Mathf.Max(0.6f, s);
                if (a == b)
                {
                    var c0 = Pos(a); float r0 = a.H * s * 0.5f;
                    var p1 = c0 + new Vector2(a.W * s * 0.25f, -r0); var p2 = p1 + new Vector2(0, -0.012f); var p3 = p2 + new Vector2(a.W * s * 0.25f, 0); var p4 = new Vector2(p3.x, c0.y - r0);
                    Stroke(p1, p2, w, col); Stroke(p2, p3, w, col); Stroke(p3, p4, w, col);
                    if (e.Arrow) Arrowhead(p4, p3, 0.007f * Mathf.Max(0.6f, s), w, col);
                    continue;
                }
                var pa = Pos(a); var pb = Pos(b);
                var from = Clip(pa, pb, a); var to = Clip(pb, pa, b);
                if (e.Style == "dotted") Dashed(from, to, w, col); else Stroke(from, to, w, col);
                if (e.Arrow) Arrowhead(to, from, 0.0075f * Mathf.Max(0.6f, s), w, col);
                if (!string.IsNullOrEmpty(e.Label))
                {
                    var mid = (from + to) * 0.5f;
                    float lh = Mathf.Max(0.0085f, 0.0098f * s);
                    float tw = UiViz.TextWidth(e.Label, lh) + 0.008f;
                    Box(new Rect(mid.x - tw / 2, mid.y - lh * 0.75f, tw, lh * 1.5f), t.Frame, 0.003f, 0, 0f, 0.001f);
                    Txt(new Rect(mid.x - tw / 2, mid.y - lh * 0.75f, tw, lh * 1.5f), UiViz.NoParse(e.Label), lh, t.TextDim, TextAlignmentOptions.Center);
                }
            }
            foreach (var n in Nodes)
            {
                var c = Pos(n);
                float w = n.W * s, h = n.H * s;
                bool hov = n.Id == _hover;
                var tint = UiViz.Tone(n.Tone, t.Accent);
                var r = new Rect(c.x - w / 2, c.y - h / 2, w, h);
                switch (n.Shape)
                {
                    case "diamond":
                    {
                        float side = w / 1.414f;
                        Box(new Rect(c.x - side / 2, c.y - side / 2, side, side), tint, 0.003f, 0, hov ? 0.9f : 0.5f, 0.003f, Mathf.PI / 4f);
                        break;
                    }
                    case "circle": Box(r, tint, Mathf.Min(w, h) * 0.5f, 0, hov ? 0.9f : 0.5f, 0.003f); break;
                    case "round": case "stadium": Box(r, tint, h * 0.5f, 0, hov ? 0.9f : 0.5f, 0.003f); break;
                    default: Box(r, tint, 0.004f, 0, hov ? 0.9f : 0.5f, 0.003f); break;
                }
                float lh = Mathf.Max(0.008f, LabelH * s);
                var lines = Wrap(n.Label, 22);
                Txt(r, UiViz.NoParse(string.Join("\n", lines)), lh, t.Text, TextAlignmentOptions.Center, null, false);
            }
        }

        private Vector2 Pos(Node n) => _offset + new Vector2(n.X, n.Y) * _scale;

        /// Where the line from `c` towards `toward` leaves the node's box (or diamond / circle).
        private Vector2 Clip(Vector2 c, Vector2 toward, Node n)
        {
            var d = toward - c;
            if (d.sqrMagnitude < 1e-10f) return c;
            float hw = n.W * _scale * 0.5f + 0.002f, hh = n.H * _scale * 0.5f + 0.002f;
            float tt;
            if (n.Shape == "diamond") tt = 1f / (Mathf.Abs(d.x) / hw + Mathf.Abs(d.y) / hh);
            else if (n.Shape == "circle") tt = hw / d.magnitude;
            else tt = Mathf.Min(Mathf.Abs(d.x) > 1e-6f ? hw / Mathf.Abs(d.x) : float.MaxValue, Mathf.Abs(d.y) > 1e-6f ? hh / Mathf.Abs(d.y) : float.MaxValue);
            return c + d * Mathf.Min(tt, 1f);
        }

        public override void OnHover(UiPointerEvent e)
        {
            string hit = null;
            foreach (var n in Nodes)
            {
                var c = Pos(n);
                if (Mathf.Abs(e.Point.x - c.x) <= n.W * _scale / 2 && Mathf.Abs(e.Point.y - c.y) <= n.H * _scale / 2) { hit = n.Id; break; }
            }
            if (hit != _hover) { _hover = hit; Redraw(); }
        }

        protected override void OnInteractionChanged() { if (!Hover && _hover != null) { _hover = null; Redraw(); } }
    }

    /// A sequence diagram: participants across the top, lifelines, messages top to bottom.
    public sealed class UiSequence : UiCanvas
    {
        public readonly List<string> Participants = new List<string>();
        public readonly Dictionary<string, string> Aliases = new Dictionary<string, string>();
        public readonly List<(string from, string to, string text, bool dashed, bool note)> Messages = new List<(string, string, string, bool, bool)>();

        public void Participant(string id, string label = null) { if (!Participants.Contains(id)) Participants.Add(id); if (label != null) Aliases[id] = label; }

        protected override void Draw()
        {
            var t = Theme;
            int n = Participants.Count;
            if (n == 0) { Txt(Rect, "empty diagram", 0.011f, t.TextDim, TextAlignmentOptions.Center); return; }
            float colW = Rect.width / n, boxH = 0.026f;
            float rowH = Mathf.Clamp((Rect.height - boxH - 0.01f) / Mathf.Max(1, Messages.Count), 0.016f, 0.03f);
            int shown = Mathf.Min(Messages.Count, Mathf.FloorToInt((Rect.height - boxH - 0.01f) / rowH));
            float X(string id) => Rect.x + (Participants.IndexOf(id) + 0.5f) * colW;
            float bottom = Rect.y + boxH + 0.006f + shown * rowH;
            for (int i = 0; i < n; i++)
            {
                string label = Aliases.TryGetValue(Participants[i], out var al) ? al : Participants[i];
                float bw = Mathf.Min(colW - 0.008f, UiViz.TextWidth(label, 0.011f) + 0.016f);
                float cx = X(Participants[i]);
                Box(new Rect(cx - bw / 2, Rect.y, bw, boxH), UiViz.Series(0), 0.004f, 0, 0.5f, 0.003f);
                Txt(new Rect(cx - bw / 2, Rect.y, bw, boxH), UiViz.NoParse(label), 0.011f, t.Text, TextAlignmentOptions.Center);
                Dashed(new Vector2(cx, Rect.y + boxH), new Vector2(cx, bottom), 0.0011f, t.Frame, 0.004f, 0.003f);
            }
            for (int m = 0; m < shown; m++)
            {
                var msg = Messages[m];
                float y = Rect.y + boxH + 0.006f + (m + 0.75f) * rowH;
                if (msg.note)
                {
                    float x0 = X(msg.from), x1 = X(msg.to);
                    float l = Mathf.Min(x0, x1) - colW * 0.4f, r = Mathf.Max(x0, x1) + colW * 0.4f;
                    Box(new Rect(l, y - rowH * 0.7f, r - l, rowH * 0.8f), t.Warn, 0.003f, 0, 0.35f, 0.002f);
                    Txt(new Rect(l, y - rowH * 0.7f, r - l, rowH * 0.8f), UiViz.NoParse(msg.text), 0.0095f, t.Text, TextAlignmentOptions.Center);
                    continue;
                }
                var col = msg.dashed ? t.TextDim : t.TextAccent;
                float xa = X(msg.from), xb = X(msg.to);
                if (msg.from == msg.to)
                {
                    var p1 = new Vector2(xa, y - rowH * 0.3f); var p2 = new Vector2(xa + 0.02f, y - rowH * 0.3f); var p3 = new Vector2(xa + 0.02f, y); var p4 = new Vector2(xa, y);
                    Stroke(p1, p2, 0.0016f, col); Stroke(p2, p3, 0.0016f, col); Stroke(p3, p4, 0.0016f, col);
                    Arrowhead(p4, p3, 0.006f, 0.0016f, col);
                    Txt(new Rect(xa + 0.024f, y - rowH * 0.8f, colW, rowH * 0.6f), UiViz.NoParse(msg.text), 0.0095f, t.TextDim);
                    continue;
                }
                var a = new Vector2(xa, y); var b = new Vector2(xb, y);
                if (msg.dashed) Dashed(a, b, 0.0016f, col); else Stroke(a, b, 0.0016f, col);
                Arrowhead(b, a, 0.0065f, 0.0016f, col);
                Txt(new Rect(Mathf.Min(xa, xb), y - rowH * 0.75f, Mathf.Abs(xb - xa), rowH * 0.6f), UiViz.NoParse(msg.text), 0.0095f, t.Text, TextAlignmentOptions.Center);
            }
            if (shown < Messages.Count) Txt(new Rect(Rect.x, bottom, Rect.width, 0.012f), $"… {Messages.Count - shown} more", 0.0095f, t.TextDim, TextAlignmentOptions.Center);
        }
    }

    // ---------------------------------------------------------------------------------------------- table, code

    /// A table: header row, zebra rows, numbers right-aligned; drag up/down to scroll.
    public sealed class UiTable : UiCanvas
    {
        public readonly List<string> Columns = new List<string>();
        public readonly List<List<string>> Rows = new List<List<string>>();
        public float RowHeight = 0.024f;
        public override bool Interactive => true;
        private float _scroll, _dragStart;
        private int _hoverRow = -1;

        private int VisRows => Mathf.Max(1, Mathf.FloorToInt((Rect.height - RowHeight) / RowHeight));

        protected override void Draw()
        {
            var t = Theme;
            int cols = Mathf.Max(Columns.Count, Rows.Count > 0 ? Rows.Max(r => r.Count) : 0);
            if (cols == 0) { Txt(Rect, "empty table", 0.011f, t.TextDim, TextAlignmentOptions.Center); return; }
            var numeric = new bool[cols];
            var weight = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                var cells = Rows.Select(r => c < r.Count ? r[c] ?? "" : "").ToList();
                numeric[c] = cells.Count > 0 && cells.All(s => s.Length == 0 || double.TryParse(s.Replace("%", "").Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                weight[c] = Mathf.Clamp(Mathf.Max(c < Columns.Count ? Columns[c].Length : 0, cells.Select(s => s.Length).DefaultIfEmpty(0).Max()), 3, 40);
            }
            float sum = weight.Sum();
            var x = new float[cols + 1];
            x[0] = Rect.x;
            for (int c = 0; c < cols; c++) x[c + 1] = x[c] + Rect.width * weight[c] / sum;
            float lh = 0.0108f;
            for (int c = 0; c < cols; c++)
                Txt(new Rect(x[c] + 0.004f, Rect.y, x[c + 1] - x[c] - 0.008f, RowHeight), "<b>" + UiViz.NoParse(c < Columns.Count ? Columns[c] : "") + "</b>", lh, t.TextDim, numeric[c] ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft);
            Hair(new Vector2(Rect.x, Rect.y + RowHeight), new Vector2(Rect.xMax, Rect.y + RowHeight), t.Frame, 0.4f);
            int first = Mathf.FloorToInt(_scroll);
            int vis = VisRows;
            for (int i = 0; i < vis && first + i < Rows.Count; i++)
            {
                var row = Rows[first + i];
                float y = Rect.y + RowHeight * (i + 1);
                if ((first + i) % 2 == 1 || i == _hoverRow) Box(new Rect(Rect.x, y + 0.001f, Rect.width, RowHeight - 0.002f), t.Frame, 0.002f, 0, i == _hoverRow ? 0.5f : 0.12f, 0.001f);
                for (int c = 0; c < cols; c++)
                {
                    string s = c < row.Count ? row[c] ?? "" : "";
                    int chars = Mathf.Max(3, (int)((x[c + 1] - x[c] - 0.008f) / (lh * 0.42f)));
                    Txt(new Rect(x[c] + 0.004f, y, x[c + 1] - x[c] - 0.008f, RowHeight), UiViz.NoParse(UiViz.Cut(s, chars)), lh, t.Text, numeric[c] ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft, numeric[c] ? Theme.Mono : null);
                }
            }
            if (Rows.Count > vis)
                Txt(new Rect(Rect.x, Rect.yMax - 0.012f, Rect.width, 0.012f), $"{first + 1}-{Mathf.Min(Rows.Count, first + vis)} of {Rows.Count} · drag to scroll", 0.0092f, t.TextDim, TextAlignmentOptions.MidlineRight);
        }

        public override void OnPress(UiPointerEvent e) => _dragStart = _scroll;
        public override void OnDrag(UiPointerEvent e)
        {
            float max = Mathf.Max(0, Rows.Count - VisRows);
            float s = Mathf.Clamp(_dragStart - (e.Point.y - e.PressPoint.y) / RowHeight, 0, max);
            if (Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll)) { _scroll = s; Redraw(); } else _scroll = s;
        }
        public override void OnHover(UiPointerEvent e)
        {
            int r = Mathf.FloorToInt((e.Point.y - Rect.y) / RowHeight) - 1;
            if (r != _hoverRow) { _hoverRow = r; Redraw(); }
        }
        protected override void OnInteractionChanged() { if (!Hover && _hoverRow != -1) { _hoverRow = -1; Redraw(); } }
    }

    /// Lines of code, a file or a diff in the terminal font, one row per line (long lines end in …), with line
    /// numbers; diffs get green / red rows. Drag up/down (or ScrollBy) to scroll.
    public sealed class UiCodeView : UiCanvas
    {
        public float RowHeight = 0.0168f;
        public bool LineNumbers = true;
        public override bool Interactive => true;
        public int Scroll => Mathf.FloorToInt(_scroll);
        public int LineCount => _lines.Count;
        private readonly List<(string text, char kind, int num)> _lines = new List<(string, char, int)>();
        private float _scroll, _dragStart;
        private string _empty = "";

        private int Rows => Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowHeight));

        /// mode: "code" (numbered), "diff" (unified diff: + - @@ rows coloured, new-file numbers), "plain".
        public void SetText(string text, string mode = "code", string empty = "")
        {
            _lines.Clear();
            _scroll = 0;
            _empty = empty ?? "";
            var raw = (text ?? "").Replace("\r", "").Replace("\t", "    ").Split('\n');
            int n = raw.Length;
            if (n > 0 && raw[n - 1].Length == 0) n--;
            int newNum = 0;
            for (int i = 0; i < n && i < 5000; i++)
            {
                string l = raw[i];
                if (mode == "diff")
                {
                    char k = l.StartsWith("@@") ? '@' : l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("diff ") || l.StartsWith("index ") ? 'h' : l.StartsWith("+") ? '+' : l.StartsWith("-") ? '-' : ' ';
                    if (k == '@') { var m = System.Text.RegularExpressions.Regex.Match(l, @"\+(\d+)"); newNum = m.Success ? int.Parse(m.Groups[1].Value) - 1 : 0; _lines.Add((l, k, 0)); continue; }
                    if (k == '+' || k == ' ') newNum++;
                    _lines.Add((l, k, k == '+' || k == ' ' ? newNum : 0));
                }
                else _lines.Add((l, ' ', mode == "plain" ? 0 : i + 1));
            }
            Redraw();
        }

        public void ScrollBy(int lines) { _scroll = Mathf.Clamp(_scroll + lines, 0, Mathf.Max(0, _lines.Count - Rows)); Redraw(); }
        public void ScrollTo(int line) { _scroll = Mathf.Clamp(line, 0, Mathf.Max(0, _lines.Count - Rows)); Redraw(); }

        protected override void Draw()
        {
            var t = Theme;
            if (_lines.Count == 0) { Txt(Rect, UiViz.NoParse(_empty), 0.011f, t.TextDim, TextAlignmentOptions.Center, null, true); return; }
            int rows = Rows, first = Scroll;
            bool nums = LineNumbers && _lines.Any(l => l.num > 0);
            float gutter = nums ? UiViz.TextWidth(_lines.Max(l => l.num).ToString(), RowHeight * 0.68f) + 0.008f : 0f;
            float lh = RowHeight * 0.68f;
            int chars = Mathf.Max(8, (int)((Rect.width - gutter - 0.012f) / (lh * 0.43f)));
            for (int i = 0; i < rows && first + i < _lines.Count; i++)
            {
                var (text, kind, num) = _lines[first + i];
                float y = Rect.y + i * RowHeight;
                Color col = t.Text;
                if (kind == '+') { Box(new Rect(Rect.x, y, Rect.width - 0.006f, RowHeight), new Color(t.Ok.r, t.Ok.g, t.Ok.b, 0.2f), 0.001f, 1, 0f, 0.0004f); col = Color.Lerp(t.Ok, Color.white, 0.55f); }
                else if (kind == '-') { Box(new Rect(Rect.x, y, Rect.width - 0.006f, RowHeight), new Color(t.Danger.r, t.Danger.g, t.Danger.b, 0.2f), 0.001f, 1, 0f, 0.0004f); col = Color.Lerp(t.Danger, Color.white, 0.5f); }
                else if (kind == '@') col = t.Violet;
                else if (kind == 'h') col = t.TextDim;
                if (nums && num > 0) Txt(new Rect(Rect.x, y, gutter - 0.004f, RowHeight), num.ToString(), lh, t.TextDim, TextAlignmentOptions.MidlineRight, Theme.Mono);
                Txt(new Rect(Rect.x + gutter + 0.002f, y, Rect.width - gutter - 0.01f, RowHeight), UiViz.NoParse(UiViz.Cut(text, chars)), lh, col, TextAlignmentOptions.MidlineLeft, Theme.Mono);
            }
            // scroll indicator
            if (_lines.Count > rows)
            {
                float frac = (float)rows / _lines.Count, pos = (float)first / Mathf.Max(1, _lines.Count - rows);
                float h = Rect.height * frac;
                Pill(new Rect(Rect.xMax - 0.004f, Rect.y + (Rect.height - h) * pos, 0.003f, h), t.Accent);
            }
        }

        public override void OnPress(UiPointerEvent e) => _dragStart = _scroll;
        public override void OnDrag(UiPointerEvent e)
        {
            float s = Mathf.Clamp(_dragStart - (e.Point.y - e.PressPoint.y) / RowHeight, 0, Mathf.Max(0, _lines.Count - Rows));
            bool changed = Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll);
            _scroll = s;
            if (changed) Redraw();
        }
    }

    /// A small ring gauge (context window use): the filled arc turns amber at 70 %, red at 90 %.
    public sealed class UiRing : UiCanvas
    {
        public float Value = -1f;
        public string Caption = "";
        public override bool Interactive => true;
        public Action OnClickAction;

        public void Set(float value, string caption) { Value = value; Caption = caption ?? ""; Redraw(); }

        protected override void Draw()
        {
            var t = Theme;
            float d = Mathf.Min(Rect.height, 0.03f);
            var c = new Vector2(Rect.x + d * 0.5f + 0.002f, Rect.center.y);
            float r = d * 0.5f - 0.003f;
            Ring(c, r, 90f, -270f, 0.0035f, Color.Lerp(t.Frame, Color.black, 0.2f));
            if (Value >= 0f)
            {
                var col = Value >= 0.9f ? t.Danger : Value >= 0.7f ? t.Warn : t.Accent;
                if (Value > 0.005f) Ring(c, r, 90f, 90f - 360f * Mathf.Clamp01(Value), 0.0035f, col);
            }
            Txt(new Rect(Rect.x + d + 0.006f, Rect.y, Rect.width - d - 0.006f, Rect.height), Caption, 0.0108f, Hover ? t.Text : t.TextDim, TextAlignmentOptions.MidlineLeft, t.Mono);
        }

        private void Ring(Vector2 c, float r, float a0, float a1, float w, Color col)
        {
            int steps = Mathf.Max(3, Mathf.CeilToInt(Mathf.Abs(a1 - a0) / 15f));
            Vector2 P(float deg) { float rad = deg * Mathf.Deg2Rad; return c + new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad)) * r; }
            var prev = P(a0);
            for (int k = 1; k <= steps; k++) { var p = P(Mathf.Lerp(a0, a1, k / (float)steps)); Piece(prev, p, w, col); prev = p; }
        }

        protected override void OnInteractionChanged() => Redraw();
        public override void OnClick(UiPointerEvent e) => OnClickAction?.Invoke();
    }
}

namespace MetaCodex.UI
{
    /// A thread's changed files at a glance: one tile per file, grouped in columns by top folder, sized by the
    /// lines changed, coloured by kind (added green, changed amber, deleted red, moved violet; grey: uncommitted
    /// but not touched by this thread) and lit while recently touched. Tap a tile to pick the file.
    public sealed class UiChangeMap : UiCanvas
    {
        public sealed class Item { public string Path, Kind; public int Added, Removed; public float Heat; public bool Dim; }
        public readonly List<Item> Items = new List<Item>();
        public string Selected;
        public string Empty = "No changes yet: files the agent edits show up here, live.";
        public Action<string> OnPick;
        public override bool Interactive => true;
        private readonly List<(Rect r, string path)> _hits = new List<(Rect, string)>();
        private string _hover;

        private static float Weight(Item i) => Mathf.Sqrt(i.Added + i.Removed + 1f);
        private static string Top(string p) { int k = (p ?? "").IndexOf('/'); return k > 0 ? p.Substring(0, k) + "/" : "./"; }

        private Color KindColor(Item i)
        {
            var t = Theme;
            if (i.Dim) return t.Frame;
            switch (i.Kind)
            {
                case "add": case "added": case "create": return t.Ok;
                case "delete": case "deleted": case "remove": return t.Danger;
                case "move": case "rename": return t.Violet;
                default: return t.Warn;
            }
        }

        protected override void Draw()
        {
            _hits.Clear();
            var t = Theme;
            if (Items.Count == 0) { Txt(Rect, UiViz.NoParse(Empty), 0.011f, t.TextDim, TextAlignmentOptions.Center, null, true); return; }
            var groups = Items.GroupBy(i => Top(i.Path)).Select(g => (name: g.Key, items: g.OrderByDescending(Weight).ToList())).OrderByDescending(g => g.items.Sum(Weight)).ToList();
            int maxCols = Mathf.Max(1, Mathf.FloorToInt(Rect.width / 0.075f));
            if (groups.Count > maxCols)
            {
                var rest = groups.Skip(maxCols - 1).SelectMany(g => g.items).OrderByDescending(Weight).ToList();
                groups = groups.Take(maxCols - 1).ToList();
                groups.Add(("other/", rest));
            }
            const float gap = 0.004f, labelH = 0.015f, minTile = 0.016f;
            float total = groups.Sum(g => g.items.Sum(Weight));
            float avail = Rect.width - gap * (groups.Count - 1);
            // column widths: proportional, at least 0.06 m
            var widths = groups.Select(g => Mathf.Max(0.06f, avail * g.items.Sum(Weight) / total)).ToList();
            float k = avail / widths.Sum();
            float x = Rect.x;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                float cw = widths[gi] * k;
                Txt(new Rect(x, Rect.y, cw, labelH), UiViz.NoParse(UiViz.Cut(g.name, Mathf.Max(4, (int)(cw / (0.0092f * 0.42f))))), 0.0092f, t.TextDim);
                float colH = Rect.height - labelH;
                int fit = Mathf.Max(1, Mathf.FloorToInt(colH / (minTile + 0.002f)));
                var shown = g.items.Count > fit ? g.items.Take(fit - 1).ToList() : g.items;
                int hidden = g.items.Count - shown.Count;
                int slots = shown.Count + (hidden > 0 ? 1 : 0);
                float free = colH - slots * minTile - (slots - 1) * 0.002f;
                float wsum = shown.Sum(Weight) + (hidden > 0 ? 1f : 0f);
                float y = Rect.y + labelH;
                foreach (var it in shown)
                {
                    float th = minTile + Mathf.Max(0f, free) * Weight(it) / wsum;
                    var r = new Rect(x, y, cw, th);
                    bool sel = it.Path == Selected, hov = it.Path == _hover;
                    Box(new Rect(r.x + 0.001f, r.y + 0.001f, r.width - 0.002f, r.height - 0.002f), KindColor(it), 0.003f, 0,
                        sel ? 1f : hov ? 0.8f : it.Dim ? 0.08f : 0.25f + 0.6f * Mathf.Clamp01(it.Heat), 0.0015f);
                    string name = it.Path.Substring(it.Path.LastIndexOf('/') + 1);
                    float lh = Mathf.Min(0.0102f, th * 0.62f);
                    int chars = Mathf.Max(3, (int)((cw - 0.008f) / (lh * 0.42f)));
                    bool two = th >= 0.03f && cw >= 0.05f;
                    Txt(new Rect(r.x + 0.004f, r.y + (two ? 0.003f : 0f), r.width - 0.008f, two ? th * 0.5f : th), UiViz.NoParse(UiViz.Cut(name, chars)), lh, it.Dim ? t.TextDim : t.Text);
                    if (two) Txt(new Rect(r.x + 0.004f, r.y + th * 0.5f, r.width - 0.008f, th * 0.45f),
                        $"<color=#{UiViz.Hex(t.Ok)}>+{it.Added}</color> <color=#{UiViz.Hex(t.Danger)}>−{it.Removed}</color>", 0.0092f, t.TextDim, TextAlignmentOptions.MidlineLeft, t.Mono);
                    _hits.Add((r, it.Path));
                    y += th + 0.002f;
                }
                if (hidden > 0)
                {
                    float th = minTile + Mathf.Max(0f, free) / wsum;
                    Txt(new Rect(x, y, cw, th), $"+{hidden} more", 0.0092f, t.TextDim, TextAlignmentOptions.Center);
                }
                x += cw + gap;
            }
        }

        private string At(Vector2 p) { foreach (var h in _hits) if (h.r.Contains(p)) return h.path; return null; }

        public override void OnHover(UiPointerEvent e) { var h = At(e.Point); if (h != _hover) { _hover = h; Redraw(); } }
        protected override void OnInteractionChanged() { if (!Hover && _hover != null) { _hover = null; Redraw(); } }
        public override void OnClick(UiPointerEvent e)
        {
            var h = At(e.PressPoint) ?? At(e.Point);
            if (h == null) return;
            Selected = h;
            UiSounds.Fire("click");
            Redraw();
            OnPick?.Invoke(h);
        }
    }
}

namespace MetaCodex.UI
{
    /// SYSTEM > TOOLS as a plain list in groups, without logos or pictures (owner, 2026-11-10: cleaner, and no brand
    /// marks in the app): a header per group (its name and what it is for, so a beginner knows what to pick), then
    /// one row per tool: a status dot (lit = installed), the name, what it does, and its version or how to get it.
    /// One column, drag to scroll; every row spans the whole width, an easy target for a ray or a fingertip.
    public sealed class UiToolList : UiCanvas
    {
        public sealed class Row { public string Name, About, Version, Group, Hint = "+ install"; public bool Found; public object Tag; }
        public sealed class Group { public string Id, Title, Blurb; public Color Tint; }
        public readonly List<Row> Rows = new List<Row>();
        public readonly List<Group> Groups = new List<Group>();
        public float RowH = 0.025f, HeaderH = 0.028f;
        public Action<Row> OnPick;
        public override bool Interactive => true;
        private readonly List<(Rect r, Row row)> _hits = new List<(Rect, Row)>();
        private Row _hover;
        private float _scroll, _dragStart, _contentH;
        private bool _dragged;
        private const float Spacing = 0.002f, GroupGap = 0.008f;
        private const float NameX = 0.022f, AboutX = 0.112f, RightW = 0.15f, AboutH = 0.0094f;

        public float MaxScroll => Mathf.Max(0f, _contentH - Rect.height);
        /// Tests: the rows and group headers drawn last time (only what is in view), where they went.
        public readonly List<(Row row, Rect rect)> DrawnRows = new List<(Row, Rect)>();
        public readonly List<(Group group, Rect rect)> DrawnHeaders = new List<(Group, Rect)>();

        /// The layout in content coordinates (y down from the top, unscrolled): a group's header (row null), then its rows.
        private List<(Group g, Rect r, Row row)> Layout()
        {
            var items = new List<(Group, Rect, Row)>();
            float y = 0f;
            var known = new HashSet<string>(Groups.Select(x => x.Id));
            var order = Groups.Concat(Rows.Any(x => x.Group == null || !known.Contains(x.Group)) ? new[] { new Group { Id = null, Title = "OTHER", Tint = Theme.Frame } } : new Group[0]);
            foreach (var g in order)
            {
                var rows = Rows.Where(x => g.Id == null ? x.Group == null || !known.Contains(x.Group) : x.Group == g.Id).ToList();
                if (rows.Count == 0) continue;
                if (items.Count > 0) y += GroupGap;
                items.Add((g, new Rect(0f, y, Rect.width, HeaderH), null));
                y += HeaderH + Spacing;
                foreach (var row in rows) { items.Add((g, new Rect(0f, y, Rect.width, RowH), row)); y += RowH + Spacing; }
            }
            _contentH = Mathf.Max(0f, y - Spacing);
            return items;
        }

        protected override void Draw()
        {
            _hits.Clear();
            DrawnRows.Clear();
            DrawnHeaders.Clear();
            var t = Theme;
            var items = Layout();
            _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll);
            foreach (var (g, lr, row) in items)
            {
                // only what is wholly in view (a canvas cannot clip)
                if (lr.y < _scroll - 0.0005f || lr.yMax > _scroll + Rect.height + 0.0005f) continue;
                var r = new Rect(Rect.x + lr.x, Rect.y + lr.y - _scroll, lr.width, lr.height);
                if (row == null) { DrawHeader(r, g); DrawnHeaders.Add((g, r)); continue; }
                DrawRow(r, row, g);
                DrawnRows.Add((row, r));
            }
            if (MaxScroll > 0f)
            {
                float frac = Rect.height / Mathf.Max(Rect.height, _contentH), pos = _scroll / MaxScroll;
                float h = Rect.height * frac;
                Pill(new Rect(Rect.xMax + 0.002f, Rect.y + (Rect.height - h) * pos, 0.003f, h), t.Accent);
            }
        }

        private void DrawHeader(Rect r, Group g)
        {
            var t = Theme;
            Pill(new Rect(r.x, r.y + r.height * 0.28f, 0.004f, r.height * 0.44f), g.Tint);
            string title = "<b>" + UiViz.NoParse(g.Title ?? "") + "</b>";
            float tw = UiViz.TextWidth(g.Title ?? "", 0.0108f) + 0.012f;
            Txt(new Rect(r.x + 0.008f, r.y, tw, r.height), title, 0.0108f, t.Text, TextAlignmentOptions.MidlineLeft, t.Display);
            if (!string.IsNullOrEmpty(g.Blurb))
                Txt(new Rect(r.x + 0.008f + tw, r.y, r.width - tw - 0.01f, r.height), UiViz.NoParse(g.Blurb), 0.0090f, t.TextDim, TextAlignmentOptions.MidlineLeft);
            Stroke(new Vector2(r.x, r.yMax - 0.0006f), new Vector2(r.xMax, r.yMax - 0.0006f), 0.0007f, new Color(g.Tint.r, g.Tint.g, g.Tint.b, 0.35f));
        }

        private void DrawRow(Rect r, Row row, Group g)
        {
            var t = Theme;
            var tint = g?.Tint ?? t.Accent;
            if (row == _hover) Box(r, tint, 0.004f, 0, 0.5f, 0.001f);
            // lit dot = installed, a faint one = not there yet
            Dot(new Vector2(r.x + 0.011f, r.center.y), 0.0072f, row.Found ? tint : new Color(t.TextDim.r, t.TextDim.g, t.TextDim.b, 0.35f));
            Txt(new Rect(r.x + NameX, r.y, AboutX - NameX - 0.004f, r.height), "<b>" + UiViz.NoParse(row.Name) + "</b>", 0.0112f, row.Found ? t.Text : t.TextDim);
            float aboutW = r.width - AboutX - RightW - 0.008f;
            if (!string.IsNullOrEmpty(row.About) && aboutW > 0.02f)
                Txt(new Rect(r.x + AboutX, r.y, aboutW, r.height), UiViz.NoParse(UiViz.Cut(row.About, Mathf.FloorToInt(aboutW / (AboutH * 0.46f)))), AboutH, t.TextDim);
            var right = new Rect(r.xMax - RightW - 0.006f, r.y, RightW, r.height);
            if (row.Found) Txt(right, UiViz.NoParse(UiViz.Cut(row.Version ?? "", 18)), AboutH, t.TextDim, TextAlignmentOptions.MidlineRight, t.Mono);
            else Txt(right, UiViz.NoParse(row.Hint ?? ""), AboutH, t.TextAccent, TextAlignmentOptions.MidlineRight);
            Stroke(new Vector2(r.x + NameX, r.yMax + Spacing * 0.5f), new Vector2(r.xMax, r.yMax + Spacing * 0.5f), 0.0004f, new Color(t.Frame.r, t.Frame.g, t.Frame.b, 0.25f));
            _hits.Add((r, row));
        }

        /// Tests and the panel: scroll so the group's header is at the top.
        public void ScrollTo(string group)
        {
            var hit = Layout().FirstOrDefault(x => x.row == null && x.g?.Id == group);
            if (hit.g == null) return;
            _scroll = Mathf.Clamp(hit.r.y, 0f, MaxScroll);
            Redraw();
        }

        private Row At(Vector2 p) { foreach (var h in _hits) if (h.r.Contains(p)) return h.row; return null; }
        public override void OnHover(UiPointerEvent e) { var h = At(e.Point); if (h != _hover) { _hover = h; Redraw(); } }
        protected override void OnInteractionChanged() { if (!Hover && _hover != null) { _hover = null; Redraw(); } }
        public override void OnPress(UiPointerEvent e) { _dragStart = _scroll; _dragged = false; }
        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - e.PressPoint.y;
            if (Mathf.Abs(dy) > RowH * 0.4f) _dragged = true;
            if (!_dragged) return;
            float s = Mathf.Clamp(_dragStart - dy, 0f, MaxScroll);
            // redraw in small steps (a whole canvas each time)
            if (Mathf.Abs(s - _scroll) > 0.006f || s == 0f || s == MaxScroll) { _scroll = s; Redraw(); }
        }
        public override void OnRelease(UiPointerEvent e, bool inside) { if (!_dragged && inside) OnClick(e); _dragged = false; }
        public override void OnClick(UiPointerEvent e) { var h = At(e.PressPoint) ?? At(e.Point); if (h == null) return; UiSounds.Fire("click"); OnPick?.Invoke(h); }
    }

    /// One stacked bar of where storage goes (segments in series colours) with a legend under it, and an
    /// optional limit marker.
    public sealed class UiStorageBar : UiCanvas
    {
        public readonly List<(string name, long bytes)> Parts = new List<(string, long)>();
        public long Total;          // the whole bar (e.g. the device's total space)
        public long Limit;          // 0: none
        public string Caption = "";

        public static string Size(long b) => b < 1024 * 1024 ? (b / 1024f).ToString("0") + " KB" : b < 1L << 30 ? (b / 1048576f).ToString("0") + " MB" : (b / 1073741824f).ToString("0.0") + " GB";

        protected override void Draw()
        {
            var t = Theme;
            Txt(new Rect(Rect.x, Rect.y, Rect.width, 0.018f), Caption, 0.0112f, t.Text);
            var bar = new Rect(Rect.x, Rect.y + 0.021f, Rect.width, 0.016f);
            Box(bar, t.Frame, 0.004f, 0, 0.1f, 0.002f);
            long total = Math.Max(1, Total > 0 ? Total : Parts.Sum(p => p.bytes));
            float x = bar.x;
            for (int i = 0; i < Parts.Count; i++)
            {
                float w = bar.width * Mathf.Clamp01(Parts[i].bytes / (float)total);
                if (w < 0.0015f) continue;
                AddShape(Panel.Shape(UiShapeKind.Bar, new Rect(x, bar.y + 0.002f, Mathf.Max(0.002f, w - 0.001f), bar.height - 0.004f), UiViz.Series(i), 0.002f, 0f, 0.001f, 1f));
                x += w;
            }
            if (Limit > 0 && Limit < total)
            {
                float lx = bar.x + bar.width * Limit / (float)total;
                Stroke(new Vector2(lx, bar.y - 0.004f), new Vector2(lx, bar.yMax + 0.004f), 0.0022f, t.Warn);
            }
            float ly = bar.yMax + 0.005f, lxx = Rect.x;
            for (int i = 0; i < Parts.Count; i++)
            {
                string label = $"{Parts[i].name} {Size(Parts[i].bytes)}";
                float w = 0.016f + UiViz.TextWidth(label, 0.0098f);
                if (lxx + w > Rect.xMax && lxx > Rect.x) { lxx = Rect.x; ly += 0.016f; }
                Pill(new Rect(lxx, ly + 0.005f, 0.008f, 0.005f), UiViz.Series(i));
                Txt(new Rect(lxx + 0.011f, ly, w, 0.015f), UiViz.NoParse(label), 0.0098f, t.TextDim);
                lxx += w + 0.008f;
            }
        }
    }
}
