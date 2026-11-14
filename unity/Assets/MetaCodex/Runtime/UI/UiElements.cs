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
        /// Only while pressed (pinch held or fingertip pushed in): sliders and drag-scrolling follow it.
        public virtual void OnDrag(UiPointerEvent e) { }
        /// The pointer moves over the element without pressing (hover highlights only; never change a value here).
        public virtual void OnHover(UiPointerEvent e) { }
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
            // fill style for the shader: 1 = filled with its colour (primary, danger), 2 = ghost (only when hovered), 0 = normal
            float fillStyle = Style == UiButtonStyle.Primary || Style == UiButtonStyle.Danger ? 1f : Style == UiButtonStyle.Ghost ? 2f : 0f;
            _shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, Rect, Tint, Mathf.Min(Theme.CutBig * 0.55f, Rect.height * 0.5f), fillStyle, 0.006f));
            float size = TextSize > 0 ? TextSize : Mathf.Min(Theme.BodySize, Rect.height * 0.62f);
            // console look: every button label in the display font (capitals, letter-spaced)
            _label = UiText.Create(Panel, Rect, UiTheme.Pretty(Text), Theme.Display, size, Theme.Text, TextAlignmentOptions.Center);
            Refresh();
        }

        public void SetText(string t) { Text = t; if (_label != null) _label.Text = UiTheme.Pretty(t); }

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
            UiSounds.Fire("click");
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
            _knob = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, KnobRect(), Theme.Text, 0.05f, 1f, 0.004f)); // a round, filled knob
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
            s.Tint = Value ? Theme.Accent : Theme.Frame;
            Panel.Shapes.Set(_track, s);
            var k = KnobRect();
            Panel.Shapes.SetRect(_knob, Panel.PanelToLocal(k.center), k.size);
            Panel.Shapes.SetState(_knob, Hover ? 0.6f : 0.25f);
            _state.Text = Value ? "ON" : "OFF";
            _state.Color = Value ? Theme.TextAccent : Theme.TextDim;
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
                _label = UiText.Create(Panel, new Rect(Rect.x, Rect.y, LabelWidth - 0.006f, Rect.height), UiTheme.Pretty(Text), Theme.Body, Theme.BodySize, Theme.Text);
            for (int i = 0; i < Options.Length; i++)
            {
                var r = SegRect(i);
                _shapes.Add(Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, r, Theme.AccentSoft, 0.006f, 0.002f, 0.005f)));
                _texts.Add(UiText.Create(Panel, r, UiTheme.Pretty(Options[i]), Theme.Display, Mathf.Min(Theme.SmallSize, r.height * 0.6f), Theme.Text, TextAlignmentOptions.Center));
            }
            Refresh();
        }

        /// Selects option `index`; -1 selects none (the voice picker's other row: F2 picked leaves MALE with none lit).
        public void Set(int index, bool notify = false)
        {
            index = Mathf.Clamp(index, -1, Options.Length - 1);
            if (Selected == index) return;
            Selected = index;
            Refresh();
            if (notify && index >= 0) OnChanged?.Invoke(index);
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

        public override void OnDrag(UiPointerEvent e) => OnHover(e);
        public override void OnHover(UiPointerEvent e) { int i = IndexAt(e.Point); if (i != _hoverIndex) { _hoverIndex = i; Refresh(); } }

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
        /// A short word under the label while the slider is dimmed (e.g. "FLOAT only": it does nothing in this layout).
        public string Note;
        /// Dimmed: drawn quietly (it still works, so it can be set ahead); see SetDimmed.
        public bool Dimmed { get; private set; }
        public override bool Interactive => true;
        private int _track, _thumb;
        private UiText _label, _value;

        public UiSlider(string text, float min, float max, float value, Action<float> onChanged)
        { Text = text; Min = min; Max = max; Value = value; OnChanged = onChanged; }

        private Rect TrackRect => new Rect(Rect.x + LabelWidth, Rect.y + Rect.height * 0.36f, Rect.width - LabelWidth - 0.06f, Rect.height * 0.28f);

        protected override void Build()
        {
            _label = UiText.Create(Panel, new Rect(Rect.x, Rect.y, LabelWidth - 0.006f, Rect.height), UiTheme.Pretty(Text), Theme.Body, Theme.BodySize, Theme.Text);
            _value = UiText.Create(Panel, new Rect(Rect.xMax - 0.055f, Rect.y, 0.055f, Rect.height), "", Theme.Mono, Theme.SmallSize, Theme.TextAccent, TextAlignmentOptions.MidlineRight);
            _track = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Bar, TrackRect, Theme.Accent, 0.003f, 0.003f, 0.004f));
            _thumb = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, ThumbRect(), Theme.Text, 0.05f, 1f, 0.005f));
            Refresh();
            if (Dimmed) ApplyDim();
        }

        /// Dims the slider (label, track, thumb, value; the Note under the label) or brings it back: colours only, no
        /// rebuild of the panel's mesh.
        public void SetDimmed(bool dim)
        {
            if (Dimmed == dim) return;
            Dimmed = dim;
            ApplyDim();
        }

        private void ApplyDim()
        {
            if (Panel == null || _label == null) return;
            _label.Text = UiTheme.Pretty(Text) + (Dimmed && !string.IsNullOrEmpty(Note) ? "\n<size=62%>" + Note + "</size>" : "");
            _label.Color = Dimmed ? Theme.TextDim * 0.8f : Theme.Text;
            _value.Color = Dimmed ? Theme.TextDim * 0.8f : Theme.TextAccent;
            Panel.Shapes.SetTint(_track, Dimmed ? Theme.Frame : Theme.Accent);
            Panel.Shapes.SetTint(_thumb, Dimmed ? Theme.TextDim : Theme.Text);
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

    /// A one-line text field. A tap opens the headset's system keyboard (UiKeyboard -> SystemKeyboard);
    /// a Bluetooth keyboard types into it while it has focus.
    public sealed class UiTextField : UiElement
    {
        public string Text;
        public string Placeholder;
        public string Caption;
        public bool Secure;
        /// The system keyboard opens with Enter for new lines (a text that can hold several lines).
        public bool Multiline;
        /// Wraps the text over its lines and shows the last lines (where the caret is). Off: one line, cut at its end.
        /// A box taller than one row shows its text from the top (THREAD's composer: its grip makes the box taller).
        public bool Wrap;
        public Action<string> OnSubmit;
        public Action<string> OnChanged;
        public float CaptionWidth = 0.17f;
        public override bool Interactive => true;
        private int _box;
        private UiText _text, _caption;
        private bool _editing;
        private string _pagedText;
        private Rect _pagedRect = new Rect(0f, 0f, -1f, -1f);

        public UiTextField(string caption, string text, Action<string> onSubmit, string placeholder = "")
        { Caption = caption; Text = text ?? ""; OnSubmit = onSubmit; Placeholder = placeholder; }

        private Rect BoxRect => string.IsNullOrEmpty(Caption) ? Rect : new Rect(Rect.x + CaptionWidth, Rect.y, Rect.width - CaptionWidth, Rect.height);

        protected override void Build()
        {
            if (!string.IsNullOrEmpty(Caption))
                _caption = UiText.Create(Panel, new Rect(Rect.x, Rect.y, CaptionWidth - 0.006f, Rect.height), UiTheme.Pretty(Caption), Theme.Body, Theme.BodySize, Theme.Text);
            _box = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, BoxRect, Theme.Frame, 0.006f, 0.002f, 0.005f));
            var tr = BoxRect;
            _text = UiText.Create(Panel, new Rect(tr.x + 0.008f, tr.y, tr.width - 0.016f, tr.height), "", Theme.Mono, Theme.SmallSize * 1.1f, Theme.Text, TextAlignmentOptions.MidlineLeft, Wrap);
            if (Wrap) _text.Tmp.overflowMode = TextOverflowModes.Page; // pages of whole lines; ShowLastPage picks the last
            Refresh();
        }

        public void Set(string text) { Text = text ?? ""; Refresh(); }

        /// The box moved or changed height (THREAD's composer grip): the box and its text follow. A box taller than
        /// one row shows its text from the top.
        protected override void Relayout()
        {
            if (Panel == null || _text == null) return;
            var br = BoxRect;
            Panel.Shapes.SetRect(_box, Panel.PanelToLocal(br.center), br.size);
            if (_caption != null) _caption.SetRect(new Rect(Rect.x, Rect.y, CaptionWidth - 0.006f, Rect.height));
            bool tall = Wrap && br.height > Theme.ButtonHeight * 1.5f;
            _text.Tmp.alignment = tall ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            _text.SetRect(tall ? new Rect(br.x + 0.008f, br.y + 0.004f, br.width - 0.016f, br.height - 0.008f) : new Rect(br.x + 0.008f, br.y, br.width - 0.016f, br.height));
            Refresh();
        }

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
            if (Wrap) ShowLastPage();
        }

        /// Shows the last page (the last lines, with the caret). Only when the text or the box changed: the mesh is
        /// built twice then (TMP needs the page count first).
        private void ShowLastPage()
        {
            var tmp = _text.Tmp;
            if (tmp.text == _pagedText && _pagedRect == Rect) return;
            _pagedText = tmp.text;
            _pagedRect = Rect;
            tmp.pageToDisplay = 1;
            tmp.ForceMeshUpdate();
            tmp.pageToDisplay = Mathf.Max(1, tmp.textInfo.pageCount);
        }

        public override void Update() { if (_editing) Refresh(); }

        protected override void OnInteractionChanged() => Refresh();

        public override void OnClick(UiPointerEvent e)
        {
            _editing = true;
            Refresh();
            string what = !string.IsNullOrEmpty(Caption) ? Caption : !string.IsNullOrEmpty(Placeholder) ? Placeholder : "TEXT";
            UiKeyboard.Edit(Text, Secure, t => { Text = t; Refresh(); OnChanged?.Invoke(t); }, t =>
            {
                Text = t;
                _editing = false;
                Refresh();
                OnSubmit?.Invoke(t);
            }, (Panel != null && !string.IsNullOrEmpty(Panel.Title) ? Panel.Title + " · " : "") + what, Multiline);
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

    /// A slim bar to drag (pinch, or poke and push) to resize what it sits on. OnDragStep gets the vertical move since
    /// the last step in panel metres (y grows downwards: a move up is negative); OnDragEnd runs when it is let go.
    /// Hover lights it. Drawn as a bar with three dots.
    public sealed class UiDragGrip : UiCanvas
    {
        public Action<float> OnDragStep;
        public Action OnDragEnd;
        public override bool Interactive => true;
        private float _lastY;

        protected override void Relayout() => Redraw();
        protected override void OnInteractionChanged() => Redraw();

        protected override void Draw()
        {
            var t = Theme;
            var c = Rect.center;
            Box(new Rect(c.x - 0.036f, c.y - 0.0025f, 0.072f, 0.005f), Hover || Pressed ? t.Accent : t.AccentSoft, 0.0025f, 1, Pressed ? 1f : Hover ? 0.5f : 0.2f, 0.001f);
            for (int i = -1; i <= 1; i++) Dot(new Vector2(c.x + i * 0.012f, c.y), 0.0028f, t.Text);
        }

        public override void OnPress(UiPointerEvent e) => _lastY = e.Point.y;

        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - _lastY;
            _lastY = e.Point.y;
            if (dy != 0f) OnDragStep?.Invoke(dy);
        }

        public override void OnRelease(UiPointerEvent e, bool inside) => OnDragEnd?.Invoke();
    }

    public sealed class UiListItem
    {
        public string Icon = "";
        public string Title = "";
        public string Sub = "";
        public string Badge = "";
        public Color? BadgeColor;
        public Color? TitleColor;
        public Color? IconColor;
        public int Indent;
        public object Tag;
        /// A small action at the row's right end (THREADS: ⊞ pins the thread as a card); a tap there calls the list's
        /// OnAction instead of selecting the row. Empty: none.
        public string Action = "";
    }

    /// A scrolling list with a fixed row height: a pool of rows reused while scrolling (drag up/down, or the
    /// arrows). Tap selects a row.
    public sealed class UiList : UiElement
    {
        public float RowHeight;
        public Action<UiListItem, int> OnSelect;
        public int SelectedIndex = -1;
        public bool Monospace;
        /// A tap on a row's Action (set before the list is added: its texts are made then).
        public Action<UiListItem, int> OnAction;
        public const float ActionWidth = 0.05f;
        public override bool Interactive => true;
        public IReadOnlyList<UiListItem> Items => _items;
        private readonly List<UiListItem> _items = new List<UiListItem>();
        private readonly List<(int shape, UiText icon, UiText title, UiText sub, UiText badge)> _rows = new List<(int, UiText, UiText, UiText, UiText)>();
        private readonly List<UiText> _actions = new List<UiText>(); // only when OnAction is set
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
                // fill style 3: a list row (quiet glass, a notch, the accent floods in when chosen)
                int shape = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, r, Theme.AccentSoft, 0.004f, 3f, 0.005f));
                var icon = UiText.Create(Panel, new Rect(r.x + 0.006f, r.y, 0.024f, r.height), "", Theme.Body, Theme.BodySize, Theme.TextAccent, TextAlignmentOptions.Center);
                var title = UiText.Create(Panel, new Rect(r.x + 0.032f, r.y, r.width * 0.62f, r.height), "", Monospace ? Theme.Mono : Theme.Body,
                    Monospace ? Theme.SmallSize * 1.1f : Theme.BodySize, Theme.Text);
                var sub = UiText.Create(Panel, new Rect(r.x + r.width * 0.55f, r.y, r.width * 0.30f, r.height), "", Theme.Body, Theme.SmallSize, Theme.TextDim, TextAlignmentOptions.MidlineRight);
                var badge = UiText.Create(Panel, new Rect(r.xMax - 0.075f, r.y, 0.070f, r.height), "", Theme.Display, Theme.SmallSize * 0.85f, Theme.Ok, TextAlignmentOptions.MidlineRight);
                _rows.Add((shape, icon, title, sub, badge));
                if (OnAction != null) _actions.Add(UiText.Create(Panel, new Rect(r.xMax - ActionWidth, r.y, ActionWidth, r.height), "", Theme.Display, Theme.SmallSize, Theme.TextAccent, TextAlignmentOptions.Center));
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
                var act = i < _actions.Count ? _actions[i] : null;
                bool hasAct = has && act != null && !string.IsNullOrEmpty(_items[idx].Action);
                act?.SetVisible(hasAct);
                if (!has) continue;
                var it = _items[idx];
                float indent = it.Indent * 0.016f;
                var r = RowRect(i);
                if (hasAct)
                {
                    act.SetRect(new Rect(r.xMax - ActionWidth, r.y, ActionWidth, r.height));
                    act.Text = it.Action;
                    r = new Rect(r.x, r.y, r.width - ActionWidth, r.height); // the rest of the row as before, left of it
                }
                row.icon.SetRect(new Rect(r.x + 0.006f + indent, r.y, 0.024f, r.height));
                float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : Mathf.Min(0.075f, r.width * 0.3f);
                float subW = string.IsNullOrEmpty(it.Sub) ? 0f : Mathf.Min(r.width * 0.32f, r.width - badgeW - 0.05f - indent);
                row.title.SetRect(new Rect(r.x + 0.032f + indent, r.y, r.width - 0.036f - indent - subW - badgeW, r.height));
                row.sub.SetRect(new Rect(r.xMax - badgeW - subW - 0.004f, r.y, subW, r.height));
                row.badge.SetRect(new Rect(r.xMax - badgeW - 0.004f, r.y, badgeW, r.height));
                row.icon.Text = it.Icon;
                row.icon.Color = it.IconColor ?? Theme.TextAccent;
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
            OnHover(e);
        }

        public override void OnHover(UiPointerEvent e)
        {
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
            if (OnAction != null && !string.IsNullOrEmpty(_items[idx].Action) && e.PressPoint.x >= RowRect(r).xMax - ActionWidth)
            {
                UiSounds.Fire("click");
                OnAction(_items[idx], idx);
                return;
            }
            SelectIndex(idx);
        }

        /// A tap on a row's action (self-tests, the control port).
        public void ActionIndex(int idx) { if (OnAction != null && idx >= 0 && idx < _items.Count && !string.IsNullOrEmpty(_items[idx].Action)) OnAction(_items[idx], idx); }

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
            foreach (var a in _actions) DestroyGo(a.Tmp.gameObject);
            _actions.Clear();
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

    /// Horizon OS's system keyboard (TouchScreenKeyboard; the manifest asks for it: oculus.software.overlay_keyboard,
    /// "Require System Keyboard"). One helper for every user (UI text fields, the terminal): one keyboard is open at a
    /// time and the user that opened it is its owner. Horizon OS decides how it shows it (with Settings > Experimental >
    /// Surface keyboard on, it can lie on a table); an app cannot place it or turn that mode on.
    public static class SystemKeyboard
    {
        private static TouchScreenKeyboard _kb;
        private static object _owner;
        private static bool _fakeOpen;
        private static float _closedAt = -10f;

        /// Each time it opens (the shell shows its tip, once a session).
        public static event Action Opened;
        /// Self-tests: stands in for the headset's keyboard in the Editor; the test sets FakeText and FakeStatus.
        public static bool FakeForTest;
        public static string FakeText = "";
        public static TouchScreenKeyboard.Status FakeStatus = TouchScreenKeyboard.Status.Visible;

        public static bool Supported => FakeForTest || (TouchScreenKeyboard.isSupported && Application.platform == RuntimePlatform.Android);
        public static bool IsOpen => _kb != null || _fakeOpen;
        public static bool IsOpenFor(object owner) => IsOpen && ReferenceEquals(_owner, owner);
        /// The headset keyboard delivers its text twice: in its text box (read by Poll) and again as text input / key
        /// events on Keyboard.current (seen on Quest 3S: "ls" arrived as "lsls"). While it is open, and for a moment
        /// after it closes, only the text box counts.
        public static bool Busy => IsOpen || Time.unscaledTime - _closedAt < 1f;

        /// Opens it for `owner` with `text` in its box (an open one closes first). False: there is no system keyboard here.
        public static bool Open(object owner, string text, bool secure, bool multiline, string placeholder)
        {
            Close();
            if (!Supported) return false;
            _owner = owner;
            if (FakeForTest) { _fakeOpen = true; FakeText = text ?? ""; FakeStatus = TouchScreenKeyboard.Status.Visible; }
            else _kb = TouchScreenKeyboard.Open(text ?? "", TouchScreenKeyboardType.Default, false, multiline, secure, false, placeholder ?? "");
            Opened?.Invoke();
            return true;
        }

        public static void Close()
        {
            if (!IsOpen) return;
            if (_kb != null) _kb.active = false;
            Closed();
        }

        private static void Closed()
        {
            _kb = null;
            _fakeOpen = false;
            _owner = null;
            _closedAt = Time.unscaledTime;
        }

        /// Once a frame, by its owner: the text in its box now (null when it is not this owner's) and its status.
        /// Done, Canceled or LostFocus: it is closed now. Taken by another owner: LostFocus.
        public static TouchScreenKeyboard.Status Poll(object owner, out string text)
        {
            text = null;
            if (!IsOpenFor(owner)) return TouchScreenKeyboard.Status.LostFocus;
            text = (_fakeOpen ? FakeText : _kb.text) ?? "";
            var status = _fakeOpen ? FakeStatus : _kb.status;
            if (status != TouchScreenKeyboard.Status.Visible) Closed();
            return status;
        }
    }

    /// The keyboard for UI text fields: Horizon OS's system keyboard (SystemKeyboard), opened with the field's text
    /// (masked for secret fields). Done keeps what was typed, Cancel puts the old text back. In the Editor a test hook
    /// (EditorProvider) supplies a field's text at once.
    public static class UiKeyboard
    {
        public static bool Active => _onDone != null;
        /// The system keyboard is open (or just closed): its text box is the only source then; the same text also
        /// arrives as key/text events, which must not be typed a second time.
        public static bool SystemKeyboardBusy => SystemKeyboard.Busy;
        /// Editor / self-test: returns what the "user" types for a given initial text (null: keep it).
        public static Func<string, string> EditorProvider;
        /// The terminal asked for the keyboard (dock KEYS, the frame's ⌨, the key bar): the terminal input owns that one.
        public static event Action TerminalKeyboardRequested;
        private static readonly object Owner = new object();
        private static Action<string> _onChanged, _onDone;
        private static string _last, _initial;
        private static bool _sys;

        public static void Edit(string initial, bool secure, Action<string> onChanged, Action<string> onDone) =>
            Edit(initial, secure, onChanged, onDone, null);

        /// Edits a field: the system keyboard opens with its text (`caption` is the hint in its empty box).
        public static void Edit(string initial, bool secure, Action<string> onChanged, Action<string> onDone, string caption, bool multiline = false)
        {
            Finish(_last); // one field at a time
            _onChanged = onChanged;
            _onDone = onDone;
            _last = _initial = initial ?? "";
            if (EditorProvider == null && SystemKeyboard.Open(Owner, _last, secure, multiline, caption ?? "")) { _sys = true; return; }
            var typed = EditorProvider?.Invoke(_last);
            if (typed != null) { _last = typed; _onChanged?.Invoke(typed); }
            Finish(_last);
        }

        public static void OpenForTerminal() => TerminalKeyboardRequested?.Invoke();

        /// Called every frame (by the input router): the system keyboard's text into the field, and its end.
        public static void Pump()
        {
            if (!_sys || !Active) return;
            var status = SystemKeyboard.Poll(Owner, out var text);
            if (text != null && text != _last) { _last = text; _onChanged?.Invoke(_last); }
            switch (status)
            {
                case TouchScreenKeyboard.Status.Visible: return;
                case TouchScreenKeyboard.Status.Canceled: // Cancel: the old text back
                    _sys = false;
                    if (_last != _initial) { _last = _initial; _onChanged?.Invoke(_last); }
                    Finish(_last);
                    return;
                default: // Done, or it closed otherwise: what was typed
                    _sys = false;
                    Finish(_last);
                    return;
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
            if (_sys) { _sys = false; if (SystemKeyboard.IsOpenFor(Owner)) SystemKeyboard.Close(); }
            var done = _onDone;
            _onDone = null;
            _onChanged = null;
            done?.Invoke(text ?? "");
        }

        /// Self-tests: the field being edited now ("" when none).
        public static string EditingTextForTest => Active ? _last : "";
    }
}
