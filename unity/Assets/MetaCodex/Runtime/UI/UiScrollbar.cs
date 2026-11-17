using System;
using UnityEngine;

namespace MetaCodex.UI
{
    /// A vertical scrollbar (owner 2026-11-17: "the scrollbar is just 2 buttons; I'd like an actual scrollable
    /// scrollbar"): a thin track and a thumb sized to the visible part. Pinch (ray) or poke the thumb and drag it: the
    /// input router keeps the press on it like any other drag, so the drag goes on outside its rect. A tap on the track
    /// pages up or down. The owner pushes its state (SetState: view, content and offset in metres) after it draws; the
    /// bar draws again only when that state or its hover changes (nothing per frame). Reusable: it knows nothing of
    /// what it scrolls (OnScrollTo, OnPage).
    public sealed class UiScrollbar : UiCanvas
    {
        /// The thumb was dragged: the new offset from the top, in metres of content (clamped to the content).
        public Action<float> OnScrollTo;
        /// The track was tapped: -1 one page up, +1 one page down.
        public Action<int> OnPage;
        public float TrackWidth = 0.0035f, ThumbWidth = 0.009f, MinThumb = 0.026f;
        public override bool Interactive => true;

        private float _view, _content, _offset, _insetBottom;
        private bool _dragging, _moved;
        private float _pressY, _pressOffset;

        public bool Scrollable => _content > _view + 1e-4f;
        public float Fraction => _content - _view > 1e-5f ? Mathf.Clamp01(_offset / (_content - _view)) : 1f;
        public bool Dragging => _dragging;

        /// What the owner shows now: the height of its view, of all its content, how far down the view starts (all in
        /// metres), and how much of the bar's bottom the track leaves free (a strip under the view). Draws only on a change.
        public void SetState(float view, float content, float offset, float insetBottom = 0f)
        {
            offset = Mathf.Clamp(offset, 0f, Mathf.Max(0f, content - view));
            if (Mathf.Abs(view - _view) < 2e-4f && Mathf.Abs(content - _content) < 2e-4f && Mathf.Abs(offset - _offset) < 2e-4f
                && Mathf.Abs(insetBottom - _insetBottom) < 2e-4f) return;
            _view = view; _content = content; _offset = offset; _insetBottom = insetBottom;
            Redraw();
        }

        private Rect Track => new Rect(Rect.x, Rect.y + 0.003f, Rect.width, Mathf.Max(0.01f, Rect.height - _insetBottom - 0.006f));

        /// The thumb's top and height on the track.
        private (float y, float h) Thumb()
        {
            var tr = Track;
            float frac = _content > 1e-5f ? Mathf.Clamp01(_view / _content) : 1f;
            float h = Mathf.Min(tr.height, Mathf.Max(MinThumb, tr.height * frac));
            return (tr.y + (tr.height - h) * Fraction, h);
        }

        protected override void Relayout() => Redraw();
        protected override void OnInteractionChanged() => Redraw();

        protected override void Draw()
        {
            if (!Scrollable) return; // everything fits: no bar
            var t = Theme;
            var tr = Track;
            float cx = Rect.center.x;
            bool lit = Hover || _dragging;
            Pill(new Rect(cx - TrackWidth * 0.5f, tr.y, TrackWidth, tr.height), new Color(t.Frame.r, t.Frame.g, t.Frame.b, lit ? 0.55f : 0.32f));
            var (y, h) = Thumb();
            float w = lit ? ThumbWidth * 1.35f : ThumbWidth;
            var c = t.Accent;
            Box(new Rect(cx - w * 0.5f, y, w, h), new Color(c.r, c.g, c.b, _dragging ? 1f : Hover ? 0.85f : 0.6f), w * 0.5f, 1, _dragging ? 1f : Hover ? 0.5f : 0f, 0.001f);
        }

        public override void OnPress(UiPointerEvent e)
        {
            _pressY = e.Point.y;
            _pressOffset = _offset;
            _moved = false;
            var (y, h) = Thumb();
            _dragging = Scrollable && e.Point.y >= y - 0.006f && e.Point.y <= y + h + 0.006f; // a little slack around the thumb
            Redraw();
        }

        public override void OnDrag(UiPointerEvent e)
        {
            if (!_dragging) return;
            float dy = e.Point.y - _pressY;
            if (Mathf.Abs(dy) > 0.0015f) _moved = true;
            var (_, h) = Thumb();
            float free = Track.height - h;
            if (free <= 1e-4f) return;
            float off = Mathf.Clamp(_pressOffset + dy / free * (_content - _view), 0f, Mathf.Max(0f, _content - _view));
            OnScrollTo?.Invoke(off);
        }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            bool wasThumb = _dragging;
            _dragging = false;
            if (!wasThumb && inside && Scrollable)
            {
                var (y, _) = Thumb();
                OnPage?.Invoke(e.PressPoint.y < y ? -1 : 1);
                UiSounds.Fire("click");
            }
            else if (wasThumb && !_moved) UiSounds.Fire("click");
            Redraw();
        }
    }
}
