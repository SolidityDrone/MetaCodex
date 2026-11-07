using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    public struct UiPointerEvent
    {
        public Vector2 Point;       // panel coordinates now
        public Vector2 PressPoint;  // where the press started
        public UiPointer Pointer;
    }

    /// Base of everything placed on a panel. Rect is in panel coordinates (metres, top-left origin, y down).
    public abstract class UiElement
    {
        public string Id;
        public Rect Rect;
        public bool Enabled = true;
        public bool Visible { get; private set; } = true;
        public virtual bool Interactive => false;
        public UiPanel Panel { get; private set; }
        public bool Hover { get; private set; }
        public bool Pressed { get; private set; }
        protected UiTheme Theme => UiTheme.Current;

        internal void Attach(UiPanel p) { Panel = p; Build(); OnVisibility(Visible); }
        internal void Detach() { Teardown(); Panel = null; }
        protected abstract void Build();
        protected virtual void Teardown() { }
        public virtual void Update() { }

        public void SetVisible(bool v)
        {
            if (Visible == v) return;
            Visible = v;
            if (Panel != null) OnVisibility(v);
            if (!v) { Hover = false; Pressed = false; }
        }

        protected virtual void OnVisibility(bool v) { }

        public void SetRect(Rect r) { Rect = r; if (Panel != null) Relayout(); }
        protected virtual void Relayout() { }

        internal void SetHover(bool h) { if (Hover == h) return; Hover = h; OnInteractionChanged(); }
        internal void SetPressed(bool p) { if (Pressed == p) return; Pressed = p; OnInteractionChanged(); }
        protected virtual void OnInteractionChanged() { }

        public virtual void OnPress(UiPointerEvent e) { }
        public virtual void OnDrag(UiPointerEvent e) { }
        public virtual void OnRelease(UiPointerEvent e, bool inside) { if (inside) OnClick(e); }
        public virtual void OnClick(UiPointerEvent e) { }

        /// Simulated click at the element centre (control port, scripts, self-tests).
        public void Click()
        {
            var e = new UiPointerEvent { Point = Rect.center, PressPoint = Rect.center };
            OnPress(e);
            OnRelease(e, true);
        }

        protected static void DestroyGo(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
        }
    }

    public sealed class UiLabel : UiElement
    {
        public string Text;
        public float LineHeight;
        public Color? Color;
        public TextAlignmentOptions Align = TextAlignmentOptions.MidlineLeft;
        public bool Wrap;
        public bool UseMono, UseDisplay;
        public UiText Label { get; private set; }

        public UiLabel(string text, float lineHeight = 0f) { Text = text; LineHeight = lineHeight; }

        protected override void Build()
        {
            var font = UseMono ? Theme.Mono : UseDisplay ? Theme.Display : Theme.Body;
            Label = UiText.Create(Panel, Rect, Text, font, LineHeight > 0 ? LineHeight : Theme.BodySize, Color ?? Theme.Text, Align, Wrap);
        }

        public void Set(string text, Color? color = null)
        {
            Text = text;
            if (Label == null) return;
            Label.Text = text;
            if (color.HasValue) Label.Color = color.Value;
        }

        protected override void Relayout() => Label?.SetRect(Rect);
        protected override void OnVisibility(bool v) => Label?.SetVisible(v);
        protected override void Teardown() => DestroyGo(Label?.Tmp.gameObject);
    }

    public enum UiButtonStyle { Normal, Primary, Ghost, Danger, Tab }

    public class UiButton : UiElement
    {
        public string Text;
        public Action OnClickAction;
        public UiButtonStyle Style;
        public bool Selected;
        public float TextSize;
        public override bool Interactive => true;
        protected int _shape = -1;
        protected UiText _label;
        private float _flash;

        public UiButton(string text, Action onClick = null, UiButtonStyle style = UiButtonStyle.Normal)
        { Text = text; OnClickAction = onClick; Style = style; }

        protected Color Tint => !Enabled ? Theme.TextDim * 0.6f
            : Style == UiButtonStyle.Primary ? Theme.Accent
            : Style == UiButtonStyle.Danger ? Theme.Danger
            : Style == UiButtonStyle.Ghost ? Theme.Frame
            : Selected ? Theme.Accent : Theme.AccentSoft;

        protected override void Build()
        {
            _shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, Rect, Tint, Theme.CutBig * 0.5f, 0.002f, 0.006f));
            float size = TextSize > 0 ? TextSize : Mathf.Min(Theme.BodySize, Rect.height * 0.62f);
            _label = UiText.Create(Panel, Rect, Text, Style == UiButtonStyle.Tab || Style == UiButtonStyle.Primary ? Theme.Display : Theme.Body,
                size, Theme.Text, TextAlignmentOptions.Center);
            if (Style == UiButtonStyle.Tab || Style == UiButtonStyle.Primary) _label.Tmp.characterSpacing = 4f;
            Refresh();
        }

        public void SetText(string t) { Text = t; if (_label != null) _label.Text = t; }

        public void SetSelected(bool s) { if (Selected == s) return; Selected = s; Refresh(); }

        public void SetEnabled(bool e) { if (Enabled == e) return; Enabled = e; Refresh(); }

        protected override void OnInteractionChanged() => Refresh();

        protected void Refresh()
        {
            if (Panel == null || _shape < 0) return;
            Panel.Shapes.SetTint(_shape, Tint);
            float s = Pressed ? 1f : Selected ? 0.7f : Hover ? 0.38f : 0f;
            Panel.Shapes.SetState(_shape, Mathf.Max(s, _flash));
            _label.Color = Enabled ? (Selected || Style == UiButtonStyle.Primary ? Theme.Text : Color.Lerp(Theme.Text, Theme.TextDim, 0.25f)) : Theme.TextDim * 0.8f;
        }

        public override void Update()
        {
            if (_flash > 0f) { _flash = Mathf.Max(0f, _flash - Time.unscaledDeltaTime * 3f); Refresh(); }
        }

        public override void OnClick(UiPointerEvent e)
        {
            if (!Enabled) return;
            _flash = 1f;
            Refresh();
            OnClickAction?.Invoke();
        }

        protected override void Relayout()
        {
            if (_shape >= 0) Panel.Shapes.SetRect(_shape, Panel.PanelToLocal(Rect.center), Rect.size);
            _label?.SetRect(Rect);
        }

        protected override void OnVisibility(bool v)
        {
            if (_shape >= 0) Panel.Shapes.SetVisible(_shape, v);
            _label?.SetVisible(v);
        }

        protected override void Teardown()
        {
            if (_shape >= 0) Panel.Shapes.SetVisible(_shape, false);
            DestroyGo(_label?.Tmp.gameObject);
        }
    }

    /// A labelled on/off switch.
    public sealed class UiToggle : UiElement
    {
        public string Text;
        public bool Value;
        public Action<bool> OnChanged;
        public override bool Interactive => true;
        private int _track, _knob;
        private UiText _label, _state;

        public UiToggle(string text, bool value, Action<bool> onChanged) { Text = text; Value = value; OnChanged = onChanged; }

        private Rect TrackRect => new Rect(Rect.xMax - 0.070f, Rect.y + Rect.height * 0.22f, 0.066f, Rect.height * 0.56f);

        protected override void Build()
        {
            _label = UiText.Create(Panel, new Rect(Rect.x, Rect.y, Rect.width - 0.11f, Rect.height), Text, Theme.Body, Theme.BodySize, Theme.Text);
            _state = UiText.Create(Panel, new Rect(Rect.xMax - 0.112f, Rect.y, 0.04f, Rect.height), "", Theme.Display, Theme.SmallSize, Theme.TextDim, TextAlignmentOptions.MidlineRight);
            _track = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Bar, TrackRect, Theme.Ok, 0.004f, 0.004f, 0.004f));
            _knob = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, KnobRect(), Theme.Accent, 0.003f, 0.002f, 0.004f));
            Refresh();
        }

        private Rect KnobRect()
        {
            var t = TrackRect;
            float w = t.height * 1.1f;
            return new Rect(Value ? t.xMax - w - 0.002f : t.x + 0.002f, t.y - 0.002f, w, t.height + 0.004f);
        }

        public void Set(bool v, bool notify = false)
        {
            if (Value == v) return;
            Value = v;
            Refresh();
            if (notify) OnChanged?.Invoke(v);
        }

        private void Refresh()
        {
            if (Panel == null) return;
            var s = Panel.Shapes.Get(_track);
            s.State = Value ? 1f : 0f;
            s.Tint = Value ? Theme.Ok : Theme.Frame;
            Panel.Shapes.Set(_track, s);
            var k = KnobRect();
            Panel.Shapes.SetRect(_knob, Panel.PanelToLocal(k.center), k.size);
            Panel.Shapes.SetState(_knob, Hover ? 0.6f : 0.25f);
            _state.Text = Value ? "ON" : "OFF";
            _state.Color = Value ? Theme.Ok : Theme.TextDim;
        }

        protected override void OnInteractionChanged() => Refresh();

        public override void OnClick(UiPointerEvent e)
        {
            Value = !Value;
            Refresh();
            OnChanged?.Invoke(Value);
        }

        protected override void OnVisibility(bool v)
        {
            Panel.Shapes.SetVisible(_track, v);
            Panel.Shapes.SetVisible(_knob, v);
            _label.SetVisible(v);
            _state.SetVisible(v);
        }

        protected override void Teardown()
        {
            Panel.Shapes.SetVisible(_track, false);
            Panel.Shapes.SetVisible(_knob, false);
            DestroyGo(_label.Tmp.gameObject);
            DestroyGo(_state.Tmp.gameObject);
        }
    }

    /// One choice out of a few, as a row of segments ("PINCH | POKE | BOTH").
    public sealed class UiSegmented : UiElement
    {
        public string Text;
        public string[] Options;
        public int Selected;
        public Action<int> OnChanged;
        public float LabelWidth = 0.17f;
        public override bool Interactive => true;
        private readonly List<int> _shapes = new List<int>();
        private readonly List<UiText> _texts = new List<UiText>();
        private UiText _label;
        private int _hoverIndex = -1;

        public UiSegmented(string text, string[] options, int selected, Action<int> onChanged)
        { Text = text; Options = options; Selected = selected; OnChanged = onChanged; }

        private Rect SegRect(int i)
        {
            float x0 = Rect.x + (string.IsNullOrEmpty(Text) ? 0f : LabelWidth);
            float w = (Rect.xMax - x0 - Theme.Gap * (Options.Length - 1)) / Options.Length;
            return new Rect(x0 + i * (w + Theme.Gap), Rect.y + 0.002f, w, Rect.height - 0.004f);
        }

        protected override void Build()
        {
            if (!string.IsNullOrEmpty(Text))
                _label = UiText.Create(Panel, new Rect(Rect.x, Rect.y, LabelWidth - 0.006f, Rect.height), Text, Theme.Body, Theme.BodySize, Theme.Text);
            for (int i = 0; i < Options.Length; i++)
            {
                var r = SegRect(i);
                _shapes.Add(Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, r, Theme.AccentSoft, 0.006f, 0.002f, 0.005f)));
                _texts.Add(UiText.Create(Panel, r, Options[i], Theme.Display, Mathf.Min(Theme.SmallSize, r.height * 0.6f), Theme.Text, TextAlignmentOptions.Center));
            }
            Refresh();
        }

        public void Set(int index, bool notify = false)
        {
            index = Mathf.Clamp(index, 0, Options.Length - 1);
            if (Selected == index) return;
            Selected = index;
            Refresh();
            if (notify) OnChanged?.Invoke(index);
        }

        private int IndexAt(Vector2 p)
        {
            for (int i = 0; i < Options.Length; i++) if (SegRect(i).Contains(p)) return i;
            return -1;
        }

        private void Refresh()
        {
            if (Panel == null) return;
            for (int i = 0; i < _shapes.Count; i++)
            {
                bool sel = i == Selected;
                Panel.Shapes.SetTint(_shapes[i], sel ? Theme.Accent : Theme.AccentSoft);
                Panel.Shapes.SetState(_shapes[i], sel ? 0.85f : (Hover && i == _hoverIndex ? 0.35f : 0f));
                _texts[i].Color = sel ? Theme.Text : Theme.TextDim;
            }
        }

        public override void Update()
        {
            // hover index follows the pointer through the router's last event
            if (!Hover && _hoverIndex != -1) { _hoverIndex = -1; Refresh(); }
        }

        public override void OnDrag(UiPointerEvent e) { int i = IndexAt(e.Point); if (i != _hoverIndex) { _hoverIndex = i; Refresh(); } }

        public override void OnClick(UiPointerEvent e)
        {
            int i = IndexAt(e.Point);
            if (i < 0) i = IndexAt(e.PressPoint);
            if (i < 0) return;
            Selected = i;
            Refresh();
            OnChanged?.Invoke(i);
        }

        protected override void OnVisibility(bool v)
        {
            foreach (var s in _shapes) Panel.Shapes.SetVisible(s, v);
            foreach (var t in _texts) t.SetVisible(v);
            _label?.SetVisible(v);
        }

        protected override void Teardown()
        {
            foreach (var s in _shapes) Panel.Shapes.SetVisible(s, false);
            foreach (var t in _texts) DestroyGo(t.Tmp.gameObject);
            if (_label != null) DestroyGo(_label.Tmp.gameObject);
        }
    }

    public sealed class UiSlider : UiElement
    {
        public string Text;
        public float Min, Max, Value;
        public string Format = "0.00";
        public Action<float> OnChanged;
        public float LabelWidth = 0.17f;
        public override bool Interactive => true;
        private int _track, _thumb;
        private UiText _label, _value;

        public UiSlider(string text, float min, float max, float value, Action<float> onChanged)
        { Text = text; Min = min; Max = max; Value = value; OnChanged = onChanged; }

        private Rect TrackRect => new Rect(Rect.x + LabelWidth, Rect.y + Rect.height * 0.36f, Rect.width - LabelWidth - 0.06f, Rect.height * 0.28f);

        protected override void Build()
        {
            _label = UiText.Create(Panel, new Rect(Rect.x, Rect.y, LabelWidth - 0.006f, Rect.height), Text, Theme.Body, Theme.BodySize, Theme.Text);
            _value = UiText.Create(Panel, new Rect(Rect.xMax - 0.055f, Rect.y, 0.055f, Rect.height), "", Theme.Mono, Theme.SmallSize, Theme.TextAccent, TextAlignmentOptions.MidlineRight);
            _track = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Bar, TrackRect, Theme.Accent, 0.003f, 0.003f, 0.004f));
            _thumb = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, ThumbRect(), Theme.Accent, 0.003f, 0.002f, 0.005f));
            Refresh();
        }

        private float Norm => Max > Min ? Mathf.Clamp01((Value - Min) / (Max - Min)) : 0f;

        private Rect ThumbRect()
        {
            var t = TrackRect;
            float w = 0.010f;
            return new Rect(t.x + Norm * t.width - w / 2f, Rect.y + Rect.height * 0.2f, w, Rect.height * 0.6f);
        }

        public void Set(float v, bool notify = false)
        {
            Value = Mathf.Clamp(v, Min, Max);
            Refresh();
            if (notify) OnChanged?.Invoke(Value);
        }

        private void Refresh()
        {
            if (Panel == null) return;
            Panel.Shapes.SetState(_track, Norm);
            var th = ThumbRect();
            Panel.Shapes.SetRect(_thumb, Panel.PanelToLocal(th.center), th.size);
            Panel.Shapes.SetState(_thumb, Pressed ? 1f : Hover ? 0.5f : 0.2f);
            _value.Text = Value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
        }

        protected override void OnInteractionChanged() => Refresh();

        private void SetFrom(Vector2 p)
        {
            var t = TrackRect;
            float n = Mathf.Clamp01((p.x - t.x) / Mathf.Max(t.width, 1e-4f));
            float v = Min + n * (Max - Min);
            if (Mathf.Approximately(v, Value)) return;
            Value = v;
            Refresh();
            OnChanged?.Invoke(Value);
        }

        public override void OnPress(UiPointerEvent e) => SetFrom(e.Point);
        public override void OnDrag(UiPointerEvent e) => SetFrom(e.Point);

        protected override void OnVisibility(bool v)
        {
            Panel.Shapes.SetVisible(_track, v);
            Panel.Shapes.SetVisible(_thumb, v);
            _label.SetVisible(v);
            _value.SetVisible(v);
        }

        protected override void Teardown()
        {
            Panel.Shapes.SetVisible(_track, false);
            Panel.Shapes.SetVisible(_thumb, false);
            DestroyGo(_label.Tmp.gameObject);
            DestroyGo(_value.Tmp.gameObject);
        }
    }

    /// A one-line text field. A tap opens the Horizon OS system keyboard (TouchScreenKeyboard overlay);
    /// a Bluetooth keyboard types into it while it has focus.
    public sealed class UiTextField : UiElement
    {
        public string Text;
        public string Placeholder;
        public string Caption;
        public bool Secure;
        public Action<string> OnSubmit;
        public Action<string> OnChanged;
        public float CaptionWidth = 0.17f;
        public override bool Interactive => true;
        private int _box;
        private UiText _text, _caption;
        private bool _editing;

        public UiTextField(string caption, string text, Action<string> onSubmit, string placeholder = "")
        { Caption = caption; Text = text ?? ""; OnSubmit = onSubmit; Placeholder = placeholder; }

        private Rect BoxRect => string.IsNullOrEmpty(Caption) ? Rect : new Rect(Rect.x + CaptionWidth, Rect.y, Rect.width - CaptionWidth, Rect.height);

        protected override void Build()
        {
            if (!string.IsNullOrEmpty(Caption))
                _caption = UiText.Create(Panel, new Rect(Rect.x, Rect.y, CaptionWidth - 0.006f, Rect.height), Caption, Theme.Body, Theme.BodySize, Theme.Text);
            _box = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, BoxRect, Theme.Frame, 0.006f, 0.002f, 0.005f));
            var tr = BoxRect;
            _text = UiText.Create(Panel, new Rect(tr.x + 0.008f, tr.y, tr.width - 0.016f, tr.height), "", Theme.Mono, Theme.SmallSize * 1.1f, Theme.Text);
            Refresh();
        }

        public void Set(string text) { Text = text ?? ""; Refresh(); }

        private void Refresh()
        {
            if (Panel == null) return;
            bool empty = string.IsNullOrEmpty(Text);
            string shown = empty ? Placeholder : Secure ? new string('•', Mathf.Min(Text.Length, 24)) : Text;
            bool caret = _editing && (Time.unscaledTime % 1f) < 0.55f;
            _text.Text = "<noparse>" + shown + "</noparse>" + (caret ? "<color=#8FEBFF>▌</color>" : "");
            _text.Color = empty ? Theme.TextDim : Theme.Text;
            Panel.Shapes.SetTint(_box, _editing ? Theme.Accent : Theme.Frame);
            Panel.Shapes.SetState(_box, _editing ? 0.6f : Hover ? 0.3f : 0f);
        }

        public override void Update() { if (_editing) Refresh(); }

        protected override void OnInteractionChanged() => Refresh();

        public override void OnClick(UiPointerEvent e)
        {
            _editing = true;
            Refresh();
            UiKeyboard.Edit(Text, Secure, t => { Text = t; Refresh(); OnChanged?.Invoke(t); }, t =>
            {
                Text = t;
                _editing = false;
                Refresh();
                OnSubmit?.Invoke(t);
            });
        }

        protected override void OnVisibility(bool v)
        {
            Panel.Shapes.SetVisible(_box, v);
            _text.SetVisible(v);
            _caption?.SetVisible(v);
        }

        protected override void Teardown()
        {
            Panel.Shapes.SetVisible(_box, false);
            DestroyGo(_text.Tmp.gameObject);
            if (_caption != null) DestroyGo(_caption.Tmp.gameObject);
        }
    }

    public sealed class UiListItem
    {
        public string Icon = "";
        public string Title = "";
        public string Sub = "";
        public string Badge = "";
        public Color? BadgeColor;
        public Color? TitleColor;
        public int Indent;
        public object Tag;
    }

    /// A scrolling list with a fixed row height: a pool of rows reused while scrolling (drag up/down, or the
    /// arrows). Tap selects a row.
    public sealed class UiList : UiElement
    {
        public float RowHeight;
        public Action<UiListItem, int> OnSelect;
        public int SelectedIndex = -1;
        public bool Monospace;
        public override bool Interactive => true;
        public IReadOnlyList<UiListItem> Items => _items;
        private readonly List<UiListItem> _items = new List<UiListItem>();
        private readonly List<(int shape, UiText icon, UiText title, UiText sub, UiText badge)> _rows = new List<(int, UiText, UiText, UiText, UiText)>();
        private int _bar;
        private float _scroll;      // rows scrolled
        private float _dragStartScroll;
        private bool _dragged;
        private int _hoverRow = -1;

        public UiList(float rowHeight = 0f, Action<UiListItem, int> onSelect = null) { RowHeight = rowHeight; OnSelect = onSelect; }

        private int VisibleRows => Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowHeight));
        private float MaxScroll => Mathf.Max(0, _items.Count - VisibleRows);

        protected override void Build()
        {
            if (RowHeight <= 0f) RowHeight = Theme.RowHeight;
            for (int i = 0; i < VisibleRows; i++)
            {
                var r = RowRect(i);
                int shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, r, Theme.AccentSoft, 0.004f, 0.001f, 0.003f));
                var icon = UiText.Create(Panel, new Rect(r.x + 0.006f, r.y, 0.024f, r.height), "", Theme.Body, Theme.BodySize, Theme.TextAccent, TextAlignmentOptions.Center);
                var title = UiText.Create(Panel, new Rect(r.x + 0.032f, r.y, r.width * 0.62f, r.height), "", Monospace ? Theme.Mono : Theme.Body,
                    Monospace ? Theme.SmallSize * 1.1f : Theme.BodySize, Theme.Text);
                var sub = UiText.Create(Panel, new Rect(r.x + r.width * 0.55f, r.y, r.width * 0.30f, r.height), "", Theme.Body, Theme.SmallSize, Theme.TextDim, TextAlignmentOptions.MidlineRight);
                var badge = UiText.Create(Panel, new Rect(r.xMax - 0.075f, r.y, 0.070f, r.height), "", Theme.Display, Theme.SmallSize * 0.85f, Theme.Ok, TextAlignmentOptions.MidlineRight);
                _rows.Add((shape, icon, title, sub, badge));
            }
            _bar = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Line, new Rect(Rect.xMax - 0.003f, Rect.y, 0.003f, Rect.height), Theme.Accent, 0, 0, 0.002f));
            Render();
        }

        private Rect RowRect(int i) => new Rect(Rect.x, Rect.y + i * RowHeight, Rect.width - 0.010f, RowHeight - 0.003f);

        public void SetItems(IEnumerable<UiListItem> items, bool keepScroll = false)
        {
            _items.Clear();
            _items.AddRange(items);
            if (!keepScroll) _scroll = 0f;
            _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll);
            if (SelectedIndex >= _items.Count) SelectedIndex = -1;
            Render();
        }

        public void ScrollBy(float rows) { _scroll = Mathf.Clamp(_scroll + rows, 0f, MaxScroll); Render(); }

        private void Render()
        {
            if (Panel == null) return;
            int first = Mathf.FloorToInt(_scroll);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int idx = first + i;
                bool has = idx < _items.Count && Visible;
                Panel.Shapes.SetVisible(row.shape, has);
                row.icon.SetVisible(has); row.title.SetVisible(has); row.sub.SetVisible(has); row.badge.SetVisible(has);
                if (!has) continue;
                var it = _items[idx];
                float indent = it.Indent * 0.016f;
                var r = RowRect(i);
                row.icon.SetRect(new Rect(r.x + 0.006f + indent, r.y, 0.024f, r.height));
                row.title.SetRect(new Rect(r.x + 0.032f + indent, r.y, r.width * 0.62f - indent, r.height));
                row.icon.Text = it.Icon;
                row.title.Text = "<noparse>" + it.Title + "</noparse>";
                row.title.Color = it.TitleColor ?? Theme.Text;
                row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
                row.badge.Text = it.Badge;
                row.badge.Color = it.BadgeColor ?? Theme.Ok;
                bool sel = idx == SelectedIndex;
                Panel.Shapes.SetTint(row.shape, sel ? Theme.Accent : Theme.AccentSoft);
                Panel.Shapes.SetState(row.shape, sel ? 0.75f : (i == _hoverRow ? 0.3f : 0f));
            }
            // scroll indicator
            float frac = _items.Count <= VisibleRows ? 1f : (float)VisibleRows / _items.Count;
            float pos = MaxScroll <= 0 ? 0f : _scroll / MaxScroll;
            float h = Rect.height * frac;
            var bar = new Rect(Rect.xMax - 0.004f, Rect.y + (Rect.height - h) * pos, 0.004f, h);
            Panel.Shapes.SetRect(_bar, Panel.PanelToLocal(bar.center), bar.size);
            Panel.Shapes.SetVisible(_bar, Visible && frac < 1f);
        }

        private int RowAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            int i = Mathf.FloorToInt((p.y - Rect.y) / RowHeight);
            return i >= 0 && i < _rows.Count ? i : -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragStartScroll = _scroll; _dragged = false; }

        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - e.PressPoint.y;
            if (Mathf.Abs(dy) > RowHeight * 0.4f) _dragged = true;
            if (_dragged)
            {
                _scroll = Mathf.Clamp(_dragStartScroll - dy / RowHeight, 0f, MaxScroll);
                Render();
            }
            int hr = RowAt(e.Point);
            if (hr != _hoverRow) { _hoverRow = hr; Render(); }
        }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragged && inside) OnClick(e);
            _dragged = false;
        }

        public override void OnClick(UiPointerEvent e)
        {
            int r = RowAt(e.PressPoint);
            if (r < 0) return;
            int idx = Mathf.FloorToInt(_scroll) + r;
            if (idx >= _items.Count) return;
            SelectIndex(idx);
        }

        public void SelectIndex(int idx, bool notify = true)
        {
            if (idx < 0 || idx >= _items.Count) return;
            SelectedIndex = idx;
            Render();
            if (notify) OnSelect?.Invoke(_items[idx], idx);
        }

        protected override void OnInteractionChanged()
        {
            if (!Hover && _hoverRow != -1) _hoverRow = -1;
            Render();
        }

        protected override void OnVisibility(bool v) => Render();

        protected override void Teardown()
        {
            foreach (var row in _rows)
            {
                Panel.Shapes.SetVisible(row.shape, false);
                DestroyGo(row.icon.Tmp.gameObject); DestroyGo(row.title.Tmp.gameObject);
                DestroyGo(row.sub.Tmp.gameObject); DestroyGo(row.badge.Tmp.gameObject);
            }
            Panel.Shapes.SetVisible(_bar, false);
        }
    }

    /// A row of tab buttons; each tab owns a page of elements shown only while it is selected.
    public sealed class UiTabs : UiElement
    {
        public string[] Names;
        public int Selected { get; private set; }
        public Action<int> OnChanged;
        private readonly List<UiButton> _buttons = new List<UiButton>();
        private readonly List<List<UiElement>> _pages = new List<List<UiElement>>();

        public UiTabs(params string[] names) { Names = names; foreach (var _ in names) _pages.Add(new List<UiElement>()); }

        protected override void Build()
        {
            float w = (Rect.width - Theme.Gap * (Names.Length - 1)) / Names.Length;
            for (int i = 0; i < Names.Length; i++)
            {
                int k = i;
                var b = Panel.Add(new UiButton(Names[i], () => Select(k), UiButtonStyle.Tab)
                {
                    Rect = new Rect(Rect.x + i * (w + Theme.Gap), Rect.y, w, Rect.height),
                    TextSize = Theme.SmallSize,
                });
                _buttons.Add(b);
            }
            Select(0);
        }

        public T AddTo<T>(int page, T element) where T : UiElement
        {
            Panel.Add(element);
            _pages[page].Add(element);
            element.SetVisible(page == Selected && Visible);
            return element;
        }

        public void Select(int i)
        {
            Selected = Mathf.Clamp(i, 0, Names.Length - 1);
            for (int b = 0; b < _buttons.Count; b++) _buttons[b].SetSelected(b == Selected);
            for (int p = 0; p < _pages.Count; p++)
                foreach (var e in _pages[p]) e.SetVisible(p == Selected && Visible);
            OnChanged?.Invoke(Selected);
        }

        protected override void OnVisibility(bool v)
        {
            foreach (var b in _buttons) b.SetVisible(v);
            for (int p = 0; p < _pages.Count; p++) foreach (var e in _pages[p]) e.SetVisible(v && p == Selected);
        }
    }

    public sealed class UiProgress : UiElement
    {
        public float Value;
        public Color? Tint;
        private int _shape;
        public UiProgress(float value) { Value = value; }
        protected override void Build() => _shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Bar, Rect, Tint ?? Theme.Accent, 0.003f, 0.003f, 0.004f, Value));
        public void Set(float v) { Value = Mathf.Clamp01(v); if (Panel != null) Panel.Shapes.SetState(_shape, Value); }
        protected override void OnVisibility(bool v) => Panel.Shapes.SetVisible(_shape, v);
        protected override void Teardown() => Panel.Shapes.SetVisible(_shape, false);
    }

    /// A thin glowing separator.
    public sealed class UiDivider : UiElement
    {
        private int _shape;
        protected override void Build() => _shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Line, new Rect(Rect.x, Rect.center.y - 0.001f, Rect.width, 0.002f), Theme.Frame, 0, 0, 0.002f));
        protected override void OnVisibility(bool v) => Panel.Shapes.SetVisible(_shape, v);
        protected override void Teardown() => Panel.Shapes.SetVisible(_shape, false);
    }

    /// Lays elements out top to bottom inside a rect.
    public sealed class UiStack
    {
        private readonly Rect _area;
        private float _y;
        private readonly float _gap;
        public UiStack(Rect area, float gap) { _area = area; _y = area.y; _gap = gap; }
        public Rect Next(float height) { var r = new Rect(_area.x, _y, _area.width, height); _y += height + _gap; return r; }
        public Rect NextSplit(float height, float fraction, out Rect right)
        {
            float w = _area.width * fraction;
            var r = new Rect(_area.x, _y, w - _gap * 0.5f, height);
            right = new Rect(_area.x + w + _gap * 0.5f, _y, _area.width - w - _gap * 0.5f, height);
            _y += height + _gap;
            return r;
        }
        public Rect Rest() => new Rect(_area.x, _y, _area.width, Mathf.Max(0.01f, _area.yMax - _y));
        public float Y => _y;
        public void Skip(float h) => _y += h;
    }

    /// The headset keyboard for UI text fields: the Horizon OS system keyboard overlay (TouchScreenKeyboard;
    /// needs "Require System Keyboard", set by the project setup). In the Editor a test hook supplies the text.
    public static class UiKeyboard
    {
        public static bool Active => _onDone != null;
        /// Editor / self-test: returns what the "user" types for a given initial text (null: keep it).
        public static Func<string, string> EditorProvider;
        /// The terminal asked for the keyboard (dock button): the terminal input owns that one.
        public static event Action TerminalKeyboardRequested;
        private static TouchScreenKeyboard _kb;
        private static Action<string> _onChanged, _onDone;
        private static string _last;

        public static void Edit(string initial, bool secure, Action<string> onChanged, Action<string> onDone)
        {
            Finish(_last); // one field at a time
            _onChanged = onChanged;
            _onDone = onDone;
            _last = initial ?? "";
            if (TouchScreenKeyboard.isSupported && Application.platform == RuntimePlatform.Android)
            {
                _kb = TouchScreenKeyboard.Open(_last, TouchScreenKeyboardType.Default, false, false, secure, false, "");
                return;
            }
            var typed = EditorProvider?.Invoke(_last);
            if (typed != null) { _last = typed; _onChanged?.Invoke(typed); }
            Finish(_last);
        }

        public static void OpenForTerminal() => TerminalKeyboardRequested?.Invoke();

        /// Called every frame (by the input router).
        public static void Pump()
        {
            if (_kb == null) return;
            if (_kb.text != _last) { _last = _kb.text; _onChanged?.Invoke(_last); }
            switch (_kb.status)
            {
                case TouchScreenKeyboard.Status.Done:
                case TouchScreenKeyboard.Status.LostFocus:
                    Finish(_kb.text);
                    break;
                case TouchScreenKeyboard.Status.Canceled:
                    Finish(_last);
                    break;
            }
        }

        /// A physical keyboard typed while a field is focused.
        public static void TypePhysical(string text)
        {
            if (!Active) return;
            foreach (char c in text)
            {
                if (c == '\b') { if (_last.Length > 0) _last = _last.Substring(0, _last.Length - 1); }
                else if (c == '\r' || c == '\n') { Finish(_last); return; }
                else if (!char.IsControl(c)) _last += c;
            }
            _onChanged?.Invoke(_last);
        }

        private static void Finish(string text)
        {
            var done = _onDone;
            _kb = null;
            _onDone = null;
            _onChanged = null;
            done?.Invoke(text ?? "");
        }
    }
}
