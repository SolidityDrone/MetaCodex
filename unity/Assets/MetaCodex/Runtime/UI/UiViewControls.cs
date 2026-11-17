using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The form controls of view components (ShellViewControls builds them from a spec): checkbox, radio, slider, stepper,
    /// select box, date and time pickers, colour swatches, a selectable list and a 2D drawing canvas. Each one is a single
    /// canvas element (its shapes in the panel's mesh, its texts pooled), keeps its own value while the user changes it
    /// (LocalAt: when), and takes a new value from its plugin without a rebuild (the Set... methods, quiet: no callback).
    /// Hover and press only restyle; nothing allocates per frame.
    public abstract class UiViewControl : UiCanvas
    {
        /// When the user last changed the value here (unscaled time; -10 = never): a spec that arrives just after keeps
        /// the user's value until the plugin has caught up (ShellViewControls' bindings wait 1 s).
        public float LocalAt { get; protected set; } = -10f;
        public override bool Interactive => true;
        protected int HoverPart = -1;

        protected void Touched() => LocalAt = Time.unscaledTime;
        protected override void Relayout() => Redraw();

        /// The part under a point (a day, a button, a row, a shape), -1 for none.
        protected virtual int PartAt(Vector2 p) => -1;
        protected virtual void Activate(int part, Vector2 p) { }

        public override void OnHover(UiPointerEvent e) { int h = PartAt(e.Point); if (h != HoverPart) { HoverPart = h; Redraw(); } }
        protected override void OnInteractionChanged() { if (!Hover) HoverPart = -1; Redraw(); }

        public override void OnClick(UiPointerEvent e)
        {
            int part = PartAt(e.PressPoint);
            if (part < 0) part = PartAt(e.Point);
            if (part < 0) return;
            UiSounds.Fire("click");
            Activate(part, e.Point);
        }

        /// A small plate (a - / + key, an arrow, a swatch's frame): lit when hovered, full when pressed or on.
        protected void Key(Rect r, int part, bool on = false, bool enabled = true)
        {
            var t = Theme;
            float s = !enabled ? 0f : on ? 0.85f : Pressed && HoverPart == part ? 1f : HoverPart == part ? 0.4f : 0.08f;
            Box(r, !enabled ? t.TextDim * 0.5f : on ? t.Accent : t.AccentSoft, Mathf.Min(0.005f, r.height * 0.3f), on ? 1 : 0, s, 0.003f);
        }

        protected static string Pretty(string s) => UiTheme.Pretty(s ?? "");
        protected static string Np(string s) => UiViz.NoParse(s);
    }

    // ---------------------------------------------------------------------------------------------- checkbox

    /// A box with a tick and its label; a tap anywhere on the row flips it.
    public sealed class UiCheck : UiViewControl
    {
        public string Text;
        public bool Value;
        public Action<bool> OnChanged;
        public UiCheck(string text, bool value, Action<bool> onChanged) { Text = text; Value = value; OnChanged = onChanged; }

        public void Set(bool v) { if (Value == v) return; Value = v; Redraw(); }
        protected override int PartAt(Vector2 p) => Rect.Contains(p) ? 0 : -1;
        protected override void Activate(int part, Vector2 p) { Value = !Value; Touched(); Redraw(); OnChanged?.Invoke(Value); }

        protected override void Draw()
        {
            var t = Theme;
            float s = Mathf.Min(0.02f, Rect.height * 0.7f);
            var box = new Rect(Rect.x + 0.002f, Rect.center.y - s * 0.5f, s, s);
            Box(box, Value ? t.Accent : t.Frame, 0.003f, Value ? 1 : 0, Value ? 0.85f : HoverPart == 0 ? 0.45f : 0.1f, 0.003f);
            if (Value) Txt(box, "✓", s * 0.9f, t.Text, TextAlignmentOptions.Center);
            Txt(new Rect(box.xMax + 0.008f, Rect.y, Rect.xMax - box.xMax - 0.008f, Rect.height), Np(Text), t.BodySize * 0.82f, t.Text);
        }
    }

    // ---------------------------------------------------------------------------------------------- radio

    /// One option out of a few, as a column of rows with a dot (inline: side by side).
    public sealed class UiRadio : UiViewControl
    {
        public string[] Labels;
        public int Selected;
        public bool Inline;
        public Action<int> OnChanged;
        public UiRadio(string[] labels, int selected, Action<int> onChanged) { Labels = labels ?? new string[0]; Selected = selected; OnChanged = onChanged; }

        public void Set(int i) { if (Selected == i) return; Selected = i; Redraw(); }

        private Rect RowRect(int i)
        {
            int n = Mathf.Max(1, Labels.Length);
            if (Inline) { float w = Rect.width / n; return new Rect(Rect.x + i * w, Rect.y, w, Rect.height); }
            float h = Mathf.Min(0.027f, Rect.height / n);
            return new Rect(Rect.x, Rect.y + i * h, Rect.width, h);
        }

        protected override int PartAt(Vector2 p) { for (int i = 0; i < Labels.Length; i++) if (RowRect(i).Contains(p)) return i; return -1; }
        protected override void Activate(int part, Vector2 p) { if (part == Selected) return; Selected = part; Touched(); Redraw(); OnChanged?.Invoke(part); }

        protected override void Draw()
        {
            var t = Theme;
            for (int i = 0; i < Labels.Length; i++)
            {
                var r = RowRect(i);
                bool on = i == Selected, hov = i == HoverPart;
                Box(r, t.AccentSoft, 0.004f, 2, hov ? 0.35f : 0f, 0.002f); // a ghost: hovering only restyles
                float d = Mathf.Min(0.017f, r.height * 0.62f);
                var c = new Vector2(r.x + 0.004f + d * 0.5f, r.center.y);
                AddShape(Panel.Shape(UiShapeKind.Dot, new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d), on ? t.Accent : t.Frame, 0f, 0f, 0.002f, on ? 1f : 0f));
                if (on) Dot(c, d * 0.42f, t.Accent);
                Txt(new Rect(c.x + d * 0.5f + 0.007f, r.y, r.xMax - c.x - d * 0.5f - 0.007f, r.height), Np(Labels[i]), t.BodySize * 0.8f, on ? t.Text : t.TextDim);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- slider

    /// A number on a track: drag the thumb (or tap the track). The value snaps to Step; OnCommit runs when it is let go,
    /// OnLive while it moves (at most 5 times a second) when Live is set.
    public sealed class UiValueSlider : UiViewControl
    {
        public string Text, Unit = "";
        public float Min, Max, Step, Value;
        public bool Live;
        public Action<float> OnCommit, OnLive;
        private float _pressValue, _liveAt;

        public UiValueSlider(string text, float min, float max, float step, float value) { Text = text; Min = min; Max = max > min ? max : min + 1f; Step = step; Value = Snap(value); }

        private float LabelW => string.IsNullOrEmpty(Text) ? 0f : Mathf.Min(0.16f, Rect.width * 0.3f);
        private const float ValueW = 0.07f;
        private Rect Track => new Rect(Rect.x + LabelW + 0.008f, Rect.center.y - 0.004f, Mathf.Max(0.02f, Rect.width - LabelW - ValueW - 0.02f), 0.008f);
        private float Norm => Mathf.Clamp01((Value - Min) / (Max - Min));

        public float Snap(float v)
        {
            v = Mathf.Clamp(v, Min, Max);
            if (Step > 0f) v = Mathf.Clamp(Min + Mathf.Round((v - Min) / Step) * Step, Min, Max);
            return v;
        }

        public void Set(float v) { v = Snap(v); if (Mathf.Approximately(v, Value)) return; Value = v; Redraw(); }

        public static string Format(float v, float step)
        {
            int dec = 0;
            if (step > 0f) { float s = step; while (dec < 4 && Mathf.Abs(s - Mathf.Round(s)) > 1e-4f) { s *= 10f; dec++; } }
            else dec = Mathf.Abs(v) < 10f ? 2 : 1;
            return v.ToString("F" + dec, CultureInfo.InvariantCulture);
        }

        protected override int PartAt(Vector2 p) => Rect.Contains(p) ? 0 : -1;

        public override void OnPress(UiPointerEvent e) { _pressValue = Value; SetFrom(e.Point); }
        public override void OnDrag(UiPointerEvent e) => SetFrom(e.Point);
        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!Mathf.Approximately(_pressValue, Value)) { Touched(); UiSounds.Fire("click"); OnCommit?.Invoke(Value); }
            Redraw();
        }
        public override void OnClick(UiPointerEvent e) { }

        private void SetFrom(Vector2 p)
        {
            var tr = Track;
            float v = Snap(Min + Mathf.Clamp01((p.x - tr.x) / tr.width) * (Max - Min));
            if (Mathf.Approximately(v, Value)) return;
            Value = v;
            Touched();
            Redraw();
            if (Live && Time.unscaledTime - _liveAt >= 0.2f) { _liveAt = Time.unscaledTime; OnLive?.Invoke(Value); }
        }

        protected override void Draw()
        {
            var t = Theme;
            if (LabelW > 0f) Txt(new Rect(Rect.x, Rect.y, LabelW, Rect.height), Np(Text), t.BodySize * 0.8f, t.Text);
            var tr = Track;
            AddShape(Panel.Shape(UiShapeKind.Bar, tr, t.Accent, 0.003f, 0.003f, 0.004f, Norm));
            float tw = 0.011f, th = Mathf.Min(0.026f, Rect.height * 0.75f);
            Box(new Rect(tr.x + Norm * tr.width - tw * 0.5f, Rect.center.y - th * 0.5f, tw, th), t.Text, 0.004f, 1, Pressed ? 1f : HoverPart == 0 ? 0.55f : 0.2f, 0.004f);
            Txt(new Rect(Rect.xMax - ValueW, Rect.y, ValueW, Rect.height), Np(Format(Value, Step) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)), t.SmallSize * 1.1f, t.TextAccent, TextAlignmentOptions.MidlineRight, t.Mono);
        }
    }

    // ---------------------------------------------------------------------------------------------- stepper

    /// A number with - and + keys (min, max, step), its label on the left.
    public sealed class UiStepper : UiViewControl
    {
        public string Text, Unit = "";
        public float Min = float.NegativeInfinity, Max = float.PositiveInfinity, Step = 1f, Value;
        public Action<float> OnChanged;
        public UiStepper(string text, float value) { Text = text; Value = value; }

        public void Set(float v) { v = Mathf.Clamp(v, Min, Max); if (Mathf.Approximately(v, Value)) return; Value = v; Redraw(); }

        private const float KeyW = 0.034f, ValW = 0.06f;
        private Rect Minus => new Rect(Rect.xMax - KeyW * 2f - ValW, Rect.y + 0.002f, KeyW, Rect.height - 0.004f);
        private Rect Plus => new Rect(Rect.xMax - KeyW, Rect.y + 0.002f, KeyW, Rect.height - 0.004f);

        protected override int PartAt(Vector2 p) => Minus.Contains(p) ? 0 : Plus.Contains(p) ? 1 : -1;
        protected override void Activate(int part, Vector2 p)
        {
            float v = Mathf.Clamp(Value + (part == 0 ? -Step : Step), Min, Max);
            if (Mathf.Approximately(v, Value)) return;
            Value = (float)Math.Round(v, 4);
            Touched();
            Redraw();
            OnChanged?.Invoke(Value);
        }

        protected override void Draw()
        {
            var t = Theme;
            if (!string.IsNullOrEmpty(Text)) Txt(new Rect(Rect.x, Rect.y, Minus.x - Rect.x - 0.006f, Rect.height), Np(Text), t.BodySize * 0.82f, t.Text);
            Key(Minus, 0, false, Value > Min);
            Txt(Minus, "−", t.BodySize, Value > Min ? t.Text : t.TextDim, TextAlignmentOptions.Center);
            var vr = new Rect(Minus.xMax, Rect.y, ValW, Rect.height);
            Txt(vr, Np(UiValueSlider.Format(Value, Step) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)), t.SmallSize * 1.15f, t.TextAccent, TextAlignmentOptions.Center, t.Mono);
            Key(Plus, 1, false, Value < Max);
            Txt(Plus, "+", t.BodySize, Value < Max ? t.Text : t.TextDim, TextAlignmentOptions.Center);
        }
    }

    // ---------------------------------------------------------------------------------------------- select box

    /// A compact dropdown: its label, then a box with the chosen option and ▾. A tap asks OnOpen to show the options
    /// (the shell's list window, with a search field for long lists).
    public sealed class UiSelectBox : UiViewControl
    {
        public string Text, Shown, Placeholder = "choose…";
        public Action OnOpen;
        public UiSelectBox(string text, string shown) { Text = text; Shown = shown; }

        public void SetShown(string s) { if (s == Shown) return; Shown = s; Redraw(); }
        public void MarkTouched() => Touched();

        private float LabelW => string.IsNullOrEmpty(Text) ? 0f : Mathf.Min(0.16f, Rect.width * 0.34f);
        private Rect BoxR => new Rect(Rect.x + LabelW, Rect.y + 0.002f, Rect.width - LabelW, Rect.height - 0.004f);

        protected override int PartAt(Vector2 p) => BoxR.Contains(p) ? 0 : -1;
        protected override void Activate(int part, Vector2 p) => OnOpen?.Invoke();

        protected override void Draw()
        {
            var t = Theme;
            if (LabelW > 0f) Txt(new Rect(Rect.x, Rect.y, LabelW - 0.006f, Rect.height), Np(Text), t.BodySize * 0.82f, t.Text);
            var b = BoxR;
            Box(b, HoverPart == 0 ? t.Accent : t.Frame, 0.005f, 0, Pressed ? 0.9f : HoverPart == 0 ? 0.4f : 0.06f, 0.004f);
            bool empty = string.IsNullOrEmpty(Shown);
            Txt(new Rect(b.x + 0.008f, b.y, b.width - 0.03f, b.height), Np(UiViz.Cut(empty ? Placeholder : Shown, 40)), t.SmallSize * 1.1f, empty ? t.TextDim : t.Text);
            Txt(new Rect(b.xMax - 0.022f, b.y, 0.018f, b.height), "▾", t.BodySize, t.TextAccent, TextAlignmentOptions.Center);
        }
    }

    // ---------------------------------------------------------------------------------------------- date and time

    /// A month grid (Monday first): ◀ month year ▶, the weekday initials, one number a day; today ringed, the chosen day
    /// lit, days outside Min..Max dim and not tappable. With Time: an hour and a minute stepper below the grid. OnChanged
    /// gets "YYYY-MM-DD" (with Time: "YYYY-MM-DDTHH:MM") when a day or the time is changed; the arrows only turn the page.
    public sealed class UiDatePicker : UiViewControl
    {
        public DateTime? Value;
        public DateTime? MinDate, MaxDate;
        public bool Time;
        public int Hour = 9, Minute = 0, MinuteStep = 5;
        public Action<string> OnChanged;
        private int _year, _month;
        private DateTime _today;
        private static readonly string[] Wd = { "M", "T", "W", "T", "F", "S", "S" };
        private static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        private static readonly string[] Nums = MakeNums();
        private static string[] MakeNums() { var a = new string[60]; for (int i = 0; i < 60; i++) a[i] = i.ToString("00", CultureInfo.InvariantCulture); return a; }
        private static readonly string[] Days = MakeDays();
        private static string[] MakeDays() { var a = new string[32]; for (int i = 0; i < 32; i++) a[i] = i.ToString(CultureInfo.InvariantCulture); return a; }

        public const int PartPrev = 100, PartNext = 101, PartHourDown = 200, PartHourUp = 201, PartMinDown = 202, PartMinUp = 203;
        private const float HeadH = 0.026f, WdH = 0.017f, TimeH = 0.034f;

        public UiDatePicker(DateTime? value, bool time)
        {
            Value = value;
            Time = time;
            _today = DateTime.Today;
            var show = value ?? _today;
            _year = show.Year; _month = show.Month;
        }

        /// The plugin's value (quiet): shows its month.
        public void SetValue(DateTime? v, int hour, int minute)
        {
            bool same = v == Value && hour == Hour && minute == Minute;
            Value = v; Hour = hour; Minute = minute;
            if (v.HasValue) { _year = v.Value.Year; _month = v.Value.Month; }
            if (!same) Redraw();
        }

        public string ValueText()
        {
            var d = Value ?? _today;
            string s = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Time ? s + "T" + Nums[Mathf.Clamp(Hour, 0, 23)] + ":" + Nums[Mathf.Clamp(Minute, 0, 59)] : s;
        }

        private float GridTop => Rect.y + HeadH + WdH;
        private float CellH => Mathf.Max(0.012f, (Rect.height - HeadH - WdH - (Time ? TimeH : 0f)) / 6f);
        private float CellW => Rect.width / 7f;
        private int Offset => ((int)new DateTime(_year, _month, 1).DayOfWeek + 6) % 7;
        private int DaysIn => DateTime.DaysInMonth(_year, _month);
        private Rect Prev => new Rect(Rect.x, Rect.y + 0.002f, 0.03f, HeadH - 0.004f);
        private Rect Next => new Rect(Rect.xMax - 0.03f, Rect.y + 0.002f, 0.03f, HeadH - 0.004f);
        private Rect CellRect(int k) => new Rect(Rect.x + (k % 7) * CellW, GridTop + (k / 7) * CellH, CellW, CellH);
        private float TimeY => Rect.yMax - TimeH;
        private Rect TimeKey(int i)
        {
            // [-] HH [+]  :  [-] MM [+], centred
            float kw = 0.03f, vw = 0.036f, colon = 0.014f, total = kw * 4 + vw * 2 + colon;
            float x0 = Rect.center.x - total * 0.5f + 0.03f;
            float x = i == 0 ? x0 : i == 1 ? x0 + kw + vw : i == 2 ? x0 + 2 * kw + vw + colon : x0 + 3 * kw + 2 * vw + colon;
            return new Rect(x, TimeY + 0.004f, kw, TimeH - 0.008f);
        }

        private bool InRange(DateTime d) => (!MinDate.HasValue || d >= MinDate.Value.Date) && (!MaxDate.HasValue || d <= MaxDate.Value.Date);

        protected override int PartAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            if (Prev.Contains(p)) return PartPrev;
            if (Next.Contains(p)) return PartNext;
            if (Time && p.y >= TimeY) { for (int i = 0; i < 4; i++) if (TimeKey(i).Contains(p)) return PartHourDown + i; return -1; }
            if (p.y < GridTop) return -1;
            int col = Mathf.Clamp((int)((p.x - Rect.x) / CellW), 0, 6), row = (int)((p.y - GridTop) / CellH);
            if (row < 0 || row > 5) return -1;
            int day = row * 7 + col - Offset + 1;
            if (day < 1 || day > DaysIn || !InRange(new DateTime(_year, _month, day))) return -1;
            return row * 7 + col;
        }

        protected override void Activate(int part, Vector2 p)
        {
            if (part == PartPrev || part == PartNext)
            {
                var m = new DateTime(_year, _month, 1).AddMonths(part == PartPrev ? -1 : 1);
                _year = m.Year; _month = m.Month;
                Redraw();
                return;
            }
            if (part >= PartHourDown)
            {
                if (part == PartHourDown) Hour = (Hour + 23) % 24;
                else if (part == PartHourUp) Hour = (Hour + 1) % 24;
                else
                {
                    int st = Mathf.Clamp(MinuteStep, 1, 30);
                    Minute = part == PartMinDown ? (Minute - st + 60) % 60 : (Minute + st) % 60;
                    Minute -= Minute % st;
                }
                if (!Value.HasValue) Value = _today;
            }
            else Value = new DateTime(_year, _month, part - Offset + 1);
            Touched();
            Redraw();
            OnChanged?.Invoke(ValueText());
        }

        protected override void Draw()
        {
            var t = Theme;
            Key(Prev, PartPrev);
            Txt(Prev, "◀", t.SmallSize, t.Text, TextAlignmentOptions.Center);
            Key(Next, PartNext);
            Txt(Next, "▶", t.SmallSize, t.Text, TextAlignmentOptions.Center);
            Txt(new Rect(Prev.xMax, Rect.y, Next.x - Prev.xMax, HeadH), "<b>" + Months[_month - 1] + " " + _year.ToString(CultureInfo.InvariantCulture) + "</b>", t.BodySize * 0.85f, t.Text, TextAlignmentOptions.Center);
            for (int c = 0; c < 7; c++)
                Txt(new Rect(Rect.x + c * CellW, Rect.y + HeadH, CellW, WdH), Wd[c], t.SmallSize * 0.9f, c >= 5 ? t.TextAccent : t.TextDim, TextAlignmentOptions.Center);
            int off = Offset, n = DaysIn;
            float fs = Mathf.Min(t.SmallSize * 1.1f, CellH * 0.62f);
            for (int day = 1; day <= n; day++)
            {
                int k = day - 1 + off;
                var r = CellRect(k);
                var d = new DateTime(_year, _month, day);
                bool chosen = Value.HasValue && Value.Value.Date == d, today = d == _today, ok = InRange(d);
                var inner = new Rect(r.x + 0.0015f, r.y + 0.0015f, r.width - 0.003f, r.height - 0.003f);
                // one plate a day, always (a ghost until lit): hovering only restyles, the panel's mesh keeps its layout
                if (chosen) Box(inner, t.Accent, 0.004f, 1, 0.85f, 0.003f);
                else Box(inner, today ? t.Accent : t.AccentSoft, 0.004f, 2, k == HoverPart ? (Pressed ? 0.9f : 0.45f) : today ? 0.6f : 0f, 0.003f);
                Txt(r, Days[day], fs, !ok ? t.TextDim * 0.55f : chosen ? t.Text : today ? t.TextAccent : t.Text, TextAlignmentOptions.Center);
            }
            if (!Time) return;
            float y = TimeY;
            Txt(new Rect(Rect.x, y, 0.06f, TimeH), "Time", t.SmallSize, t.TextDim);
            string[] glyph = { "−", "+", "−", "+" };
            for (int i = 0; i < 4; i++) { var kr = TimeKey(i); Key(kr, PartHourDown + i); Txt(kr, glyph[i], t.BodySize * 0.9f, t.Text, TextAlignmentOptions.Center); }
            Txt(new Rect(TimeKey(0).xMax, y, TimeKey(1).x - TimeKey(0).xMax, TimeH), Nums[Mathf.Clamp(Hour, 0, 23)], t.BodySize, t.TextAccent, TextAlignmentOptions.Center, t.Mono);
            Txt(new Rect(TimeKey(1).xMax, y, TimeKey(2).x - TimeKey(1).xMax, TimeH), ":", t.BodySize, t.TextDim, TextAlignmentOptions.Center, t.Mono);
            Txt(new Rect(TimeKey(2).xMax, y, TimeKey(3).x - TimeKey(2).xMax, TimeH), Nums[Mathf.Clamp(Minute, 0, 59)], t.BodySize, t.TextAccent, TextAlignmentOptions.Center, t.Mono);
        }

        public static bool TryParse(string s, out DateTime date, out int hour, out int minute)
        {
            hour = -1; minute = -1; date = default;
            if (string.IsNullOrEmpty(s)) return false;
            string d = s.Length >= 10 ? s.Substring(0, 10) : s;
            if (!DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
            if (s.Length >= 16 && (s[10] == 'T' || s[10] == ' ') && UiTimePicker.TryParse(s.Substring(11, 5), out hour, out minute)) { }
            return true;
        }
    }

    /// A time of day: [-] HH [+] : [-] MM [+] (minutes in steps), its label on the left. OnChanged gets "HH:MM".
    public sealed class UiTimePicker : UiViewControl
    {
        public string Text;
        public int Hour, Minute, Step = 5;
        public Action<string> OnChanged;
        private static readonly string[] Nums = MakeNums();
        private static string[] MakeNums() { var a = new string[60]; for (int i = 0; i < 60; i++) a[i] = i.ToString("00", CultureInfo.InvariantCulture); return a; }
        public UiTimePicker(string text, int hour, int minute) { Text = text; Hour = hour; Minute = minute; }

        public string ValueText() => Nums[Mathf.Clamp(Hour, 0, 23)] + ":" + Nums[Mathf.Clamp(Minute, 0, 59)];
        public void Set(int h, int m) { if (h == Hour && m == Minute) return; Hour = h; Minute = m; Redraw(); }

        private const float KeyW = 0.03f, ValW = 0.036f, ColonW = 0.014f;
        private Rect KeyRect(int i)
        {
            float x0 = Rect.xMax - (KeyW * 4 + ValW * 2 + ColonW);
            float x = i == 0 ? x0 : i == 1 ? x0 + KeyW + ValW : i == 2 ? x0 + 2 * KeyW + ValW + ColonW : x0 + 3 * KeyW + 2 * ValW + ColonW;
            return new Rect(x, Rect.y + 0.002f, KeyW, Rect.height - 0.004f);
        }

        protected override int PartAt(Vector2 p) { for (int i = 0; i < 4; i++) if (KeyRect(i).Contains(p)) return i; return -1; }
        protected override void Activate(int part, Vector2 p)
        {
            int st = Mathf.Clamp(Step, 1, 30);
            if (part == 0) Hour = (Hour + 23) % 24;
            else if (part == 1) Hour = (Hour + 1) % 24;
            else { Minute = part == 2 ? (Minute - st + 60) % 60 : (Minute + st) % 60; Minute -= Minute % st; }
            Touched();
            Redraw();
            OnChanged?.Invoke(ValueText());
        }

        protected override void Draw()
        {
            var t = Theme;
            var k0 = KeyRect(0);
            if (!string.IsNullOrEmpty(Text)) Txt(new Rect(Rect.x, Rect.y, k0.x - Rect.x - 0.006f, Rect.height), Np(Text), t.BodySize * 0.82f, t.Text);
            for (int i = 0; i < 4; i++) { var kr = KeyRect(i); Key(kr, i); Txt(kr, (i & 1) == 0 ? "−" : "+", t.BodySize * 0.9f, t.Text, TextAlignmentOptions.Center); }
            Txt(new Rect(k0.xMax, Rect.y, ValW, Rect.height), Nums[Mathf.Clamp(Hour, 0, 23)], t.BodySize, t.TextAccent, TextAlignmentOptions.Center, t.Mono);
            Txt(new Rect(KeyRect(1).xMax, Rect.y, ColonW, Rect.height), ":", t.BodySize, t.TextDim, TextAlignmentOptions.Center, t.Mono);
            Txt(new Rect(KeyRect(2).xMax, Rect.y, ValW, Rect.height), Nums[Mathf.Clamp(Minute, 0, 59)], t.BodySize, t.TextAccent, TextAlignmentOptions.Center, t.Mono);
        }

        public static bool TryParse(string s, out int hour, out int minute)
        {
            hour = minute = -1;
            if (string.IsNullOrEmpty(s) || s.Length < 4) return false;
            int c = s.IndexOf(':');
            if (c < 1) return false;
            if (!int.TryParse(s.Substring(0, c), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            string m = s.Substring(c + 1);
            if (m.Length > 2) m = m.Substring(0, 2);
            if (!int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
            return hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59;
        }
    }

    // ---------------------------------------------------------------------------------------------- colour swatches

    /// A grid of colour swatches; the chosen one is ringed. OnChanged gets its index.
    public sealed class UiSwatches : UiViewControl
    {
        public Color[] Colors;
        public int Selected = -1;
        public Action<int> OnChanged;
        public UiSwatches(Color[] colors, int selected) { Colors = colors ?? new Color[0]; Selected = selected; }
        public void Set(int i) { if (i == Selected) return; Selected = i; Redraw(); }

        private int Cols => Mathf.Clamp(Colors.Length, 1, 8);
        private Rect Cell(int i)
        {
            int rows = Mathf.Max(1, (Colors.Length + Cols - 1) / Cols);
            float w = Rect.width / Cols, h = Mathf.Min(0.03f, Rect.height / rows), s = Mathf.Min(w, h);
            return new Rect(Rect.x + (i % Cols) * w + (w - s) * 0.5f, Rect.y + (i / Cols) * h + (h - s) * 0.5f, s, s);
        }

        protected override int PartAt(Vector2 p) { for (int i = 0; i < Colors.Length; i++) if (Cell(i).Contains(p)) return i; return -1; }
        protected override void Activate(int part, Vector2 p) { if (part == Selected) return; Selected = part; Touched(); Redraw(); OnChanged?.Invoke(part); }

        protected override void Draw()
        {
            var t = Theme;
            for (int i = 0; i < Colors.Length; i++)
            {
                var r = Cell(i);
                bool on = i == Selected;
                Box(new Rect(r.x + 0.0005f, r.y + 0.0005f, r.width - 0.001f, r.height - 0.001f), on ? t.Text : t.AccentSoft, 0.005f, 2, on ? 0.9f : i == HoverPart ? 0.45f : 0f, 0.003f);
                AddShape(Panel.Shape(UiShapeKind.Flat, new Rect(r.x + 0.004f, r.y + 0.004f, r.width - 0.008f, r.height - 0.008f), Colors[i], 0.003f, 0f, 0.0005f, 1f));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- selectable list

    /// Rows to choose from (a title, a dim second text, a tone bar), the chosen one lit; drag to scroll when there are
    /// more rows than fit. OnPick gets the row's index.
    public sealed class UiPickList : UiViewControl
    {
        public sealed class Row { public string Text, Sub, Badge; public Color? Tone; }
        public readonly List<Row> Rows = new List<Row>();
        public int Selected = -1;
        public Action<int> OnPick;
        private float _scroll, _dragStart, _pressY;
        private bool _dragged;

        public float RowH = 0.03f;
        private int Fit => Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowH));
        private float MaxScroll => Mathf.Max(0, Rows.Count - Fit);

        public void Set(int i) { if (i == Selected) return; Selected = i; Redraw(); }
        public void Restyle() => Redraw();

        protected override int PartAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            int i = Mathf.FloorToInt(_scroll) + (int)((p.y - Rect.y) / RowH);
            return i >= 0 && i < Rows.Count && (p.y - Rect.y) < Fit * RowH ? i : -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragStart = _scroll; _pressY = e.Point.y; _dragged = false; }
        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - _pressY;
            if (Mathf.Abs(dy) > 0.006f) _dragged = true;
            if (!_dragged || MaxScroll <= 0) return;
            float s = Mathf.Clamp(_dragStart - dy / RowH, 0, MaxScroll);
            if (Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll)) { _scroll = s; Redraw(); } else _scroll = s;
        }
        public override void OnRelease(UiPointerEvent e, bool inside) { if (!_dragged && inside) OnClick(e); _dragged = false; }
        protected override void Activate(int part, Vector2 p) { Selected = part; Touched(); Redraw(); OnPick?.Invoke(part); }

        protected override void Draw()
        {
            var t = Theme;
            int first = Mathf.FloorToInt(_scroll), fit = Fit;
            for (int k = 0; k < fit && first + k < Rows.Count; k++)
            {
                int i = first + k;
                var row = Rows[i];
                var r = new Rect(Rect.x, Rect.y + k * RowH, Rect.width - (MaxScroll > 0 ? 0.006f : 0f), RowH - 0.002f);
                bool on = i == Selected;
                Box(r, on ? t.Accent : t.AccentSoft, 0.004f, 0, on ? 0.75f : i == HoverPart ? 0.4f : 0.04f, 0.002f);
                if (row.Tone.HasValue) Pill(new Rect(r.x + 0.004f, r.y + r.height * 0.25f, 0.003f, r.height * 0.5f), row.Tone.Value);
                float x = r.x + 0.012f, bw = string.IsNullOrEmpty(row.Badge) ? 0f : 0.07f;
                bool sub = !string.IsNullOrEmpty(row.Sub);
                Txt(new Rect(x, r.y, r.width * (sub ? 0.5f : 1f) - (sub ? 0f : bw) - 0.014f, r.height), Np(row.Text), t.SmallSize * 1.12f, on ? t.Text : t.Text);
                if (sub) Txt(new Rect(r.x + r.width * 0.5f, r.y, r.width * 0.5f - bw - 0.006f, r.height), Np(row.Sub), t.SmallSize * 0.95f, t.TextDim, TextAlignmentOptions.MidlineRight);
                if (bw > 0f) Txt(new Rect(r.xMax - bw - 0.004f, r.y, bw, r.height), Np(row.Badge), t.SmallSize * 0.95f, row.Tone ?? t.TextAccent, TextAlignmentOptions.MidlineRight, t.Mono);
            }
            if (MaxScroll > 0)
            {
                float h = Rect.height * fit / Rows.Count, y = Rect.y + (Rect.height - h) * (_scroll / MaxScroll);
                Pill(new Rect(Rect.xMax - 0.003f, y, 0.003f, h), t.Frame);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- card

    /// A titled surface that groups the components laid out on it (drawn under them; not interactive).
    public sealed class UiCardFrame : UiCanvas
    {
        public string Title;
        public Color? Tone;
        public const float TitleH = 0.022f, Inset = 0.008f;
        protected override void Relayout() => Redraw();
        protected override void Draw()
        {
            var t = Theme;
            Box(Rect, Tone ?? t.Frame, 0.006f, 0, 0.1f, 0.003f);
            if (Tone.HasValue) Pill(new Rect(Rect.x + 0.004f, Rect.y + 0.006f, 0.0028f, Mathf.Min(0.014f, Rect.height - 0.012f)), Tone.Value);
            if (!string.IsNullOrEmpty(Title))
                Txt(new Rect(Rect.x + Inset + 0.004f, Rect.y + 0.002f, Rect.width - 2 * Inset, TitleH), "<b>" + UiViz.NoParse(Title) + "</b>", t.SmallSize * 1.05f, t.TextAccent);
        }
    }

    // ---------------------------------------------------------------------------------------------- canvas

    /// A 2D drawing in its own units (Units: width x height, origin top left, y down; a lon/lat map is projected into
    /// those units by the builder). Shapes: rect, circle, line, polyline, polygon (an outline and a soft fill made of
    /// triangles, ear clipping), text, arc. A tap gives the topmost shape with an id under it (inside a rect, circle or
    /// polygon, within a few millimetres of a line or arc, on a text) and the point in canvas units; that shape becomes
    /// the selected one (accent). Hovering a shape with an id lights it. Restyle after changing tones, labels or the
    /// selection: the geometry stays, so only colours and texts change in the panel's mesh.
    public sealed class UiDrawCanvas : UiViewControl
    {
        public enum Kind { Rect, Circle, Line, Polyline, Polygon, Text, Arc }

        public sealed class Shape
        {
            public string Id, Label, Text;
            public Kind Type;
            public Vector2[] Pts;       // canvas units: rect = 2 corners, circle / arc / text = 1 centre
            public int[] Tris;          // polygon fill: index triples into Pts
            public float R, From, To;   // circle / arc radius (units), arc angles (degrees, 0 = up, clockwise)
            public float WidthMm = 1.4f, SizeMm = 11f, Opacity = 0.28f;
            public Color Tone, Stroke, FillColor;
            public bool Fill, HasStroke = true;
            public TextAlignmentOptions Align = TextAlignmentOptions.Center;
            public Vector2 LabelAt;
        }

        public readonly List<Shape> Shapes = new List<Shape>();
        public Vector2 Units = new Vector2(100f, 100f);
        public Color? Background;
        public string Selected;
        public bool Selectable = true;
        public Action<string, Vector2> OnTap;

        public const int MaxShapes = 120, MaxPoints = 64, MaxTotalPoints = 1200, MaxTexts = 60; // texts and labels

        private float _s;
        private Vector2 _o;

        private void Map()
        {
            var r = Rect;
            float ux = Mathf.Max(1e-4f, Units.x), uy = Mathf.Max(1e-4f, Units.y);
            _s = Mathf.Min(r.width / ux, r.height / uy);
            _o = new Vector2(r.x + (r.width - ux * _s) * 0.5f, r.y + (r.height - uy * _s) * 0.5f);
        }

        public Vector2 ToPanel(Vector2 u) => _o + u * _s;
        public Vector2 ToUnits(Vector2 p) { Map(); return (p - _o) / Mathf.Max(_s, 1e-6f); }

        public void SetSelected(string id) { if (id == Selected) return; Selected = id; Redraw(); }
        public void Restyle() => Redraw();

        // ------------------------------------------------------------------ hit test

        protected override int PartAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            Map();
            var u = (p - _o) / Mathf.Max(_s, 1e-6f);
            float tolU = 0.006f / Mathf.Max(_s, 1e-6f); // 6 mm on the panel, in units
            for (int i = Shapes.Count - 1; i >= 0; i--)
            {
                var s = Shapes[i];
                if (string.IsNullOrEmpty(s.Id)) continue;
                if (Hit(s, u, tolU, _s)) return i;
            }
            return -1;
        }

        private static bool Hit(Shape s, Vector2 u, float tol, float metresPerUnit)
        {
            var pts = s.Pts;
            switch (s.Type)
            {
                case Kind.Rect:
                {
                    float x0 = Mathf.Min(pts[0].x, pts[1].x) - tol * 0.3f, x1 = Mathf.Max(pts[0].x, pts[1].x) + tol * 0.3f;
                    float y0 = Mathf.Min(pts[0].y, pts[1].y) - tol * 0.3f, y1 = Mathf.Max(pts[0].y, pts[1].y) + tol * 0.3f;
                    return u.x >= x0 && u.x <= x1 && u.y >= y0 && u.y <= y1;
                }
                case Kind.Circle: return (u - pts[0]).magnitude <= s.R + tol * 0.3f;
                case Kind.Polygon: return Inside(pts, u) || NearPath(pts, u, tol * 0.5f, true);
                case Kind.Line: case Kind.Polyline: return NearPath(pts, u, Mathf.Max(tol, s.WidthMm * 0.0005f), false);
                case Kind.Arc:
                {
                    float d = (u - pts[0]).magnitude;
                    if (Mathf.Abs(d - s.R) > tol) return false;
                    float a = Mathf.Atan2(u.x - pts[0].x, -(u.y - pts[0].y)) * Mathf.Rad2Deg; // 0 = up, clockwise
                    float from = s.From, to = s.To;
                    if (to < from) { float tmp = from; from = to; to = tmp; }
                    while (a < from) a += 360f;
                    return a <= to;
                }
                case Kind.Text:
                {
                    float h = Mathf.Clamp(s.SizeMm, 5f, 40f) * 0.001f, w = UiViz.TextWidth(s.Text, h) * 1.15f + 0.004f; // metres
                    float k = Mathf.Max(metresPerUnit, 1e-6f);
                    float x0 = s.Align == TextAlignmentOptions.MidlineLeft ? 0f : s.Align == TextAlignmentOptions.MidlineRight ? -w : -w * 0.5f;
                    float dx = (u.x - pts[0].x) * k, dy = (u.y - pts[0].y) * k;
                    return dx >= x0 - 0.003f && dx <= x0 + w + 0.003f && Mathf.Abs(dy) <= h;
                }
            }
            return false;
        }

        public static bool Inside(Vector2[] pts, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                if ((pts[i].y > p.y) != (pts[j].y > p.y) && p.x < (pts[j].x - pts[i].x) * (p.y - pts[i].y) / (pts[j].y - pts[i].y + 1e-12f) + pts[i].x) c = !c;
            return c;
        }

        private static bool NearPath(Vector2[] pts, Vector2 p, float tol, bool closed)
        {
            int n = pts.Length;
            for (int i = 0; i < n - 1 + (closed ? 1 : 0); i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n];
                var ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                if ((a + ab * t - p).magnitude <= tol) return true;
            }
            return false;
        }

        public override void OnClick(UiPointerEvent e)
        {
            if (!Rect.Contains(e.Point) && !Rect.Contains(e.PressPoint)) return;
            int part = PartAt(e.PressPoint);
            if (part < 0) part = PartAt(e.Point);
            var u = ToUnits(e.Point);
            string id = part >= 0 ? Shapes[part].Id : "";
            if (part >= 0) UiSounds.Fire("click");
            if (part >= 0 && Selectable && Selected != id) { Selected = id; Touched(); Redraw(); }
            OnTap?.Invoke(id, u);
        }

        // ------------------------------------------------------------------ drawing

        protected override void Draw()
        {
            var t = Theme;
            Map();
            if (Background.HasValue)
                AddShape(Panel.Shape(UiShapeKind.Flat, new Rect(_o.x, _o.y, Units.x * _s, Units.y * _s), Background.Value, 0.004f, 0f, 0.0005f, 1f));
            for (int i = 0; i < Shapes.Count; i++)
            {
                var s = Shapes[i];
                bool sel = !string.IsNullOrEmpty(s.Id) && s.Id == Selected, hov = i == HoverPart;
                var line = sel ? t.Accent : hov ? Color.Lerp(s.Stroke, Color.white, 0.35f) : s.Stroke;
                float op = Mathf.Clamp01(s.Opacity * (sel ? 1.9f : hov ? 1.45f : 1f));
                var fill = s.FillColor; fill.a = Mathf.Sqrt(op); // the flat kind's alpha counts twice in the shader
                if (sel) fill = new Color(Mathf.Lerp(fill.r, t.Accent.r, 0.5f), Mathf.Lerp(fill.g, t.Accent.g, 0.5f), Mathf.Lerp(fill.b, t.Accent.b, 0.5f), fill.a);
                float w = Mathf.Clamp(s.WidthMm, 0.4f, 8f) * 0.001f * (sel ? 1.4f : 1f);
                var pts = s.Pts;
                switch (s.Type)
                {
                    case Kind.Rect:
                    {
                        var a = ToPanel(pts[0]); var b = ToPanel(pts[1]);
                        var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                        if (s.Fill) AddShape(Panel.Shape(UiShapeKind.Flat, r, fill, Mathf.Min(s.R * _s, Mathf.Min(r.width, r.height) * 0.5f), 0f, 0.0005f, 1f));
                        if (s.HasStroke)
                        {
                            Stroke(new Vector2(r.x, r.y), new Vector2(r.xMax, r.y), w, line);
                            Stroke(new Vector2(r.xMax, r.y), new Vector2(r.xMax, r.yMax), w, line);
                            Stroke(new Vector2(r.xMax, r.yMax), new Vector2(r.x, r.yMax), w, line);
                            Stroke(new Vector2(r.x, r.yMax), new Vector2(r.x, r.y), w, line);
                        }
                        break;
                    }
                    case Kind.Circle:
                    {
                        var c = ToPanel(pts[0]);
                        float rr = s.R * _s;
                        if (s.Fill) AddShape(Panel.Shape(UiShapeKind.Flat, new Rect(c.x - rr, c.y - rr, rr * 2, rr * 2), fill, rr, 0f, 0.0005f, 1f));
                        if (s.HasStroke) ArcPath(c, rr, 0f, 360f, w, line);
                        break;
                    }
                    case Kind.Arc:
                        ArcPath(ToPanel(pts[0]), s.R * _s, s.From, s.To, w, line);
                        break;
                    case Kind.Line: case Kind.Polyline:
                        for (int k = 0; k < pts.Length - 1; k++) Stroke(ToPanel(pts[k]), ToPanel(pts[k + 1]), w, line);
                        break;
                    case Kind.Polygon:
                    {
                        if (s.Fill && s.Tris != null)
                            for (int k = 0; k + 2 < s.Tris.Length; k += 3) Tri(ToPanel(pts[s.Tris[k]]), ToPanel(pts[s.Tris[k + 1]]), ToPanel(pts[s.Tris[k + 2]]), fill);
                        if (s.HasStroke) for (int k = 0; k < pts.Length; k++) Stroke(ToPanel(pts[k]), ToPanel(pts[(k + 1) % pts.Length]), w, line);
                        break;
                    }
                    case Kind.Text:
                    {
                        float h = Mathf.Clamp(s.SizeMm, 5f, 40f) * 0.001f;
                        var c = ToPanel(pts[0]);
                        float tw = Mathf.Max(0.02f, UiViz.TextWidth(s.Text, h) * 1.15f + 0.004f);
                        float x = s.Align == TextAlignmentOptions.MidlineLeft ? c.x : s.Align == TextAlignmentOptions.MidlineRight ? c.x - tw : c.x - tw * 0.5f;
                        Txt(new Rect(x, c.y - h * 0.75f, tw, h * 1.5f), Np(s.Text), h, sel ? t.TextAccent : s.Tone, s.Align);
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(s.Label))
                {
                    float h = Mathf.Clamp(s.SizeMm, 5f, 30f) * 0.001f;
                    var c = ToPanel(s.LabelAt);
                    float tw = Mathf.Max(0.02f, UiViz.TextWidth(s.Label, h) * 1.15f + 0.004f);
                    Txt(new Rect(c.x - tw * 0.5f, c.y - h * 0.75f, tw, h * 1.5f), Np(s.Label), h, sel ? t.Text : Color.Lerp(s.Tone, t.Text, 0.55f), TextAlignmentOptions.Center);
                }
            }
        }

        private void Tri(Vector2 a, Vector2 b, Vector2 c, Color tint)
        {
            var la = Panel.PanelToLocal(a, -0.0002f); var lb = Panel.PanelToLocal(b); var lc = Panel.PanelToLocal(c);
            AddShape(new UiShape { Kind = UiShapeKind.Tri, Center = la, B = new Vector2(lb.x, lb.y), C = new Vector2(lc.x, lc.y), Tint = tint, State = 1f, Visible = true });
        }

        /// An arc (or a whole circle) of straight pieces about 3 mm long: 0 degrees is up, clockwise.
        private void ArcPath(Vector2 c, float r, float from, float to, float w, Color tint)
        {
            float span = Mathf.Clamp(to - from, -360f, 360f);
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(span) * Mathf.Deg2Rad * r / 0.003f), 6, 72);
            Vector2 prev = default;
            for (int k = 0; k <= n; k++)
            {
                float a = (from + span * k / n) * Mathf.Deg2Rad;
                var p = new Vector2(c.x + Mathf.Sin(a) * r, c.y - Mathf.Cos(a) * r);
                if (k > 0) Piece(prev, p, w, tint);
                prev = p;
            }
        }

        // ------------------------------------------------------------------ geometry helpers (the builder)

        /// Ear clipping: index triples covering a simple polygon (either winding; at most MaxPoints corners). A polygon that
        /// crosses itself gets a partial fill (the outline still shows all of it).
        public static int[] Triangulate(Vector2[] p)
        {
            int n = p.Length;
            if (n < 3) return new int[0];
            var idx = new List<int>(n);
            float area = 0f;
            for (int i = 0, j = n - 1; i < n; j = i++) area += p[j].x * p[i].y - p[i].x * p[j].y;
            for (int i = 0; i < n; i++) idx.Add(area > 0 ? i : n - 1 - i);
            var tris = new List<int>((n - 2) * 3);
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool cut = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    Vector2 a = p[ia], b = p[ib], c = p[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-9f) continue; // reflex or flat
                    bool empty = true;
                    for (int m = 0; m < idx.Count && empty; m++)
                    {
                        int im = idx[m];
                        if (im == ia || im == ib || im == ic) continue;
                        if (InTri(p[im], a, b, c)) empty = false;
                    }
                    if (!empty) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    idx.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut) break;
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris.ToArray();
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// Where a shape's label goes: the middle of a rect, circle or line, a polygon's centroid (its corners' mean when
        /// its area is about 0).
        public static Vector2 LabelPoint(Shape s)
        {
            var p = s.Pts;
            switch (s.Type)
            {
                case Kind.Rect: return (p[0] + p[1]) * 0.5f;
                case Kind.Polygon:
                {
                    float a = 0f, cx = 0f, cy = 0f;
                    for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
                    {
                        float f = p[j].x * p[i].y - p[i].x * p[j].y;
                        a += f; cx += (p[j].x + p[i].x) * f; cy += (p[j].y + p[i].y) * f;
                    }
                    if (Mathf.Abs(a) > 1e-6f) return new Vector2(cx / (3f * a), cy / (3f * a));
                    goto default;
                }
                case Kind.Line: case Kind.Polyline: return p.Length >= 2 ? (p[p.Length / 2 - (p.Length % 2 == 0 ? 1 : 0)] + p[p.Length / 2]) * 0.5f : p[0];
                default:
                {
                    var m = Vector2.zero;
                    foreach (var q in p) m += q;
                    return m / Mathf.Max(1, p.Length);
                }
            }
        }
    }
}
