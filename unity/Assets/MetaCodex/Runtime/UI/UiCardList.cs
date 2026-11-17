using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// THREADS' list (owner 2026-11-17, after two tree layouts that did not read clearly): each project is a CARD, a
    /// rounded box a step lighter than the glass, with a stripe in its type's colour down its left edge and a clear gap
    /// (CardGap) between cards. Its head (UiListKind.CardHead) has the fold triangle, the name in bold with its folder under
    /// it, a Chip (its type and kind), the Badge (the thread count; a Dot before it while folded) and an Action button (+).
    /// Its threads (CardRow) are rows set well in from the card's edge (the status dot at 31 mm, the title at 50 mm) along
    /// a thin guide line, each with a short tick to its dot: the title on one line, the Badge (a status word) on its right
    /// and a dim second line (Sub). CardSection: a folder the rows under it work in. CardNote: a hint with an action on its
    /// right; with no head before it, a card of its own (the empty states). Row items (the archived list) are plain rows
    /// with a faint plate; Gap items are blank space.
    /// Like UiList: a pool of rows reused while scrolling (drag), by whole items; a row that does not fit whole at the
    /// bottom is not drawn, a card whose head scrolled away is cut at the list's top. Nothing runs per frame: SetItems, a
    /// scroll step or a hover change draws again, and a text or a shape is only touched when it changes (UiText,
    /// UiShapeMesh).
    public sealed class UiCardList : UiElement
    {
        public Action<UiListItem, int> OnSelect;
        /// A tap on a head's Action button (THREADS' +). The rest of the head calls OnSelect.
        public Action<UiListItem, int> OnAction;
        /// The chosen row (a CardRow or a Row): lit in the accent; -1: none.
        public int SelectedIndex = -1;
        /// What the list says while it has no items (THREADS gives a CardNote instead).
        public string EmptyText = "";
        public override bool Interactive => true;
        public IReadOnlyList<UiListItem> Items => _items;

        // heights (m): a thread row is 46 mm, far above the 22 mm a pinch needs
        public const float HeadHeight = 0.056f, HeadHeightOneLine = 0.044f, RowHeight = 0.046f, SectionHeight = 0.032f,
            NoteHeight = 0.042f, NoteHeightTwoLines = 0.052f, PlainHeight = 0.040f, GapHeight = 0.012f;
        /// The space between two cards, and under a card's last row (inside it).
        public const float CardGap = 0.014f, CardPadBottom = 0.006f;
        // x from the card's left edge (m): the stripe, the guide (under the head's fold triangle), a row's plate, its dot's
        // centre and its title; the head's name; the padding on the right
        public const float StripeWidth = 0.0035f, GuideX = 0.020f, RowX = 0.028f, DotX = 0.037f, TextX = 0.050f, NameX = 0.034f, Pad = 0.010f;
        /// The head's + (drawn), its target (the head's full height, this wide), and the count's column (counts line up).
        public const float PlusSize = 0.032f, PlusHitWidth = 0.050f, CountWidth = 0.088f;
        private const float ScrollLane = 0.012f; // the scroll bar's lane on the right
        private const float DotSize = 0.011f, Hair = 0.0012f, LineH = 0.022f, SubH = 0.017f;

        // THREADS' text, 12-15 % larger than the 2026-11-16 tree (owner 2026-11-17: "a few px, it's hard to read")
        private float NameSize => Theme.BodySize * 1.14f;       // a project's name (was BodySize)
        private float CaptionSize => Theme.SmallSize * 1.13f;   // its folder (was SmallSize)
        private float TitleSize => Theme.SmallSize * 1.42f;     // a thread's title (was SmallSize * 1.25)
        private float MetaSize => Theme.SmallSize * 1.13f;      // its second line (was SmallSize)
        private float TagSize => Theme.SmallSize * 1.04f;       // chips and status words, display face (was SmallSize * 0.92)
        private float CountSize => Theme.SmallSize * 1.2f;      // the head's count (was SmallSize * 0.92)

        private readonly List<UiListItem> _items = new List<UiListItem>();
        private readonly List<float> _tops = new List<float>();         // each item's top (content metres); the last entry: the content's height
        private readonly List<int> _cardOf = new List<int>();           // the first item of the item's card (-1: not in a card)
        private readonly List<int> _cardLast = new List<int>();         // at a card's first item: its last item
        private readonly List<float> _cardBottom = new List<float>();   // at a card's first item: its bottom (with its padding)
        private float _contentH;
        private int _maxFirst;
        private float _scroll, _dragStartScroll; // items scrolled (whole items are drawn from FloorToInt(_scroll))
        private bool _dragged;
        private int _hoverRow = -1;
        private bool _hoverAction;
        private int _bar = -1;
        private UiText _empty;

        /// One pooled row: its plate (hover, open, chosen), the tick from the guide, the dot and four texts. item: the item
        /// it shows now (-1: none); shown: where; action: a head's button target (empty otherwise).
        private sealed class PoolRow
        {
            public int plate = -1, tick = -1, dot = -1;
            public UiText icon, title, sub, badge;
            public Rect shown, action;
            public int item = -1;
        }

        /// One card's box, drawn per card on screen: its fill, its stripe, the guide down its rows, the hairline under its
        /// head, and the head's chip and + (over the fill).
        private sealed class CardVis
        {
            public int fill = -1, stripe = -1, guide = -1, rule = -1, chip = -1, plus = -1;
            public UiText chipText, plusText;
        }

        private readonly List<PoolRow> _rows = new List<PoolRow>();
        private readonly List<CardVis> _cards = new List<CardVis>();

        private float CardWidth => Rect.width - ScrollLane;
        private Color CardFill => new Color(Theme.Frame.r, Theme.Frame.g, Theme.Frame.b, 0.17f);
        private Color GuideColor => new Color(Theme.TextDim.r, Theme.TextDim.g, Theme.TextDim.b, 0.42f);
        private Color RuleColor => new Color(Theme.TextDim.r, Theme.TextDim.g, Theme.TextDim.b, 0.22f);
        private Color HoverTint => new Color(Theme.TextDim.r, Theme.TextDim.g, Theme.TextDim.b, 0.10f);
        private Color SubColor => Color.Lerp(Theme.TextDim, Theme.Text, 0.12f);
        private static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        protected override void Build()
        {
            int nRows = Mathf.CeilToInt(Rect.height / SectionHeight) + 2;               // the smallest kind shows the most rows
            int nCards = Mathf.CeilToInt(Rect.height / (SectionHeight + CardGap)) + 2;  // a card on screen shows a row at least
            var r0 = new Rect(Rect.x, Rect.y, CardWidth, RowHeight);
            for (int i = 0; i < nCards; i++) // the boxes first: they draw under the rows
            {
                var c = new CardVis();
                c.fill = Panel.Shapes.Add(Flat(r0, CardFill, 0.007f));
                c.stripe = Panel.Shapes.Add(Flat(r0, Theme.Accent, 0.0016f));
                c.guide = Panel.Shapes.Add(Flat(r0, GuideColor, 0f));
                c.rule = Panel.Shapes.Add(Flat(r0, RuleColor, 0f));
                _cards.Add(c);
            }
            for (int i = 0; i < nRows; i++)
            {
                var row = new PoolRow();
                row.plate = Panel.Shapes.Add(Flat(r0, HoverTint, 0.005f));
                row.tick = Panel.Shapes.Add(Flat(r0, GuideColor, 0f));
                row.dot = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Dot, r0, Theme.Ok, 0f, 0f, 0.003f, 1f));
                row.icon = UiText.Create(Panel, r0, "", Theme.Body, Theme.BodySize, Theme.TextAccent, TextAlignmentOptions.Center);
                row.title = UiText.Create(Panel, r0, "", Theme.Body, TitleSize, Theme.Text);
                row.sub = UiText.Create(Panel, r0, "", Theme.Body, MetaSize, SubColor);
                row.badge = UiText.Create(Panel, r0, "", Theme.Display, TagSize, Theme.TextDim, TextAlignmentOptions.MidlineRight);
                _rows.Add(row);
            }
            foreach (var c in _cards) // a head's chip and button: over its box
            {
                c.chip = Panel.Shapes.Add(Flat(r0, Theme.Accent, 0.004f));
                c.plus = Panel.Shapes.Add(Flat(r0, Theme.Accent, 0.006f));
                c.chipText = UiText.Create(Panel, r0, "", Theme.Display, TagSize, Theme.Text, TextAlignmentOptions.Center);
                c.plusText = UiText.Create(Panel, r0, "", Theme.Display, Theme.TitleSize, Theme.TextAccent, TextAlignmentOptions.Center);
            }
            _bar = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Line, new Rect(Rect.xMax - 0.003f, Rect.y, 0.003f, Rect.height), Theme.Accent, 0, 0, 0.002f));
            float ey = Mathf.Min(Rect.height * 0.25f, 0.06f), eh = Mathf.Max(0.02f, Mathf.Min(Rect.height - ey, 0.09f));
            _empty = UiText.Create(Panel, new Rect(Rect.x + Rect.width * 0.08f, Rect.y + ey, Rect.width * 0.84f, eh), "",
                Theme.Body, Theme.BodySize * 0.95f, Color.Lerp(Theme.TextDim, Theme.Text, 0.15f), TextAlignmentOptions.Top, true);
            Layout();
            _scroll = Mathf.Clamp(_scroll, 0f, _maxFirst);
            Render();
        }

        private UiShape Flat(Rect r, Color tint, float radius) => Panel.Shape(UiShapeKind.Flat, r, tint, radius, 0f, 0.0015f);

        private static bool InCard(UiListKind k) => k == UiListKind.CardHead || k == UiListKind.CardRow || k == UiListKind.CardSection || k == UiListKind.CardNote;

        private static float HeightOf(UiListItem it)
        {
            switch (it.Kind)
            {
                case UiListKind.CardHead: return string.IsNullOrEmpty(it.Sub) ? HeadHeightOneLine : HeadHeight;
                case UiListKind.CardRow: return RowHeight;
                case UiListKind.CardSection: return SectionHeight;
                case UiListKind.CardNote: return string.IsNullOrEmpty(it.Sub) ? NoteHeight : NoteHeightTwoLines;
                case UiListKind.Gap: return GapHeight;
                default: return PlainHeight;
            }
        }

        /// The top of every item, the cards (where each starts and ends, its bottom), the content's height and the first
        /// item from which the rest fills the list (the most it scrolls).
        private void Layout()
        {
            _tops.Clear(); _cardOf.Clear(); _cardLast.Clear(); _cardBottom.Clear();
            int n = _items.Count, start = -1;
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                bool card = InCard(it.Kind), prevCard = i > 0 && InCard(_items[i - 1].Kind);
                bool starts = card && (it.Kind == UiListKind.CardHead || !prevCard);
                if (i > 0 && (starts || (!card && prevCard))) y += CardGap; // between cards, and after a card
                if (starts) start = i;
                _cardOf.Add(card ? start : -1);
                _cardLast.Add(-1);
                _cardBottom.Add(0f);
                _tops.Add(y);
                y += HeightOf(it);
                bool ends = card && (i == n - 1 || !InCard(_items[i + 1].Kind) || _items[i + 1].Kind == UiListKind.CardHead);
                if (ends) { y += CardPadBottom; _cardLast[start] = i; _cardBottom[start] = y; }
            }
            _tops.Add(y);
            _contentH = y;
            int f = Mathf.Max(0, n - 1);
            while (f > 0 && _contentH - _tops[f - 1] <= Rect.height) f--;
            _maxFirst = f;
        }

        /// Sets what the list says while it has no items (only when it changes).
        public void SetEmptyText(string text)
        {
            text ??= "";
            if (EmptyText == text) return;
            EmptyText = text;
            Render();
        }

        /// keepScroll: the item that was on top stays on top (found by its Tag) when it is still there, else the same index.
        public void SetItems(IEnumerable<UiListItem> items, bool keepScroll = false)
        {
            int first = Mathf.FloorToInt(_scroll);
            object anchor = keepScroll && first >= 0 && first < _items.Count ? _items[first].Tag : null;
            _items.Clear();
            _items.AddRange(items);
            Layout();
            if (!keepScroll) _scroll = 0f;
            else if (anchor != null)
                for (int i = 0; i < _items.Count; i++)
                    if (anchor.Equals(_items[i].Tag)) { _scroll = i; break; }
            _scroll = Mathf.Clamp(_scroll, 0f, _maxFirst);
            if (SelectedIndex >= _items.Count) SelectedIndex = -1;
            Render();
        }

        public void ScrollBy(float items) { _scroll = Mathf.Clamp(_scroll + items, 0f, _maxFirst); Render(); }

        /// Scrolls the least that shows item idx whole: down until it fits at the bottom, or up to it (from its card's head
        /// when both fit). Nothing when it is shown.
        public void Reveal(int idx)
        {
            if (idx < 0 || idx >= _items.Count) return;
            int f0 = Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, _items.Count - 1), f = f0;
            float bottom = _tops[idx] + HeightOf(_items[idx]);
            if (idx < f) { int s = _cardOf[idx]; f = s >= 0 && bottom - _tops[s] <= Rect.height ? s : idx; }
            else while (f < idx && bottom - _tops[f] > Rect.height) f++;
            f = Mathf.Clamp(f, 0, _maxFirst);
            if (f == f0) return;
            _scroll = f;
            Render();
        }

        private void Render()
        {
            if (Panel == null) return;
            int n = _items.Count;
            int first = Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, Mathf.Max(0, n - 1));
            float top0 = n > 0 ? _tops[first] : 0f;
            int last = first - 1;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int idx = first + i;
                row.item = -1; row.shown = default; row.action = default;
                if (!Visible || idx >= n) { HideRow(row); continue; }
                var it = _items[idx];
                var r = new Rect(Rect.x, Rect.y + _tops[idx] - top0, CardWidth, HeightOf(it));
                if (r.yMax > Rect.yMax + 0.0005f) { HideRow(row); continue; }
                last = idx;
                bool hover = i == _hoverRow;
                switch (it.Kind)
                {
                    case UiListKind.Gap: HideRow(row); continue; // blank: nothing drawn, nothing to pick
                    case UiListKind.CardHead: PlaceHead(row, it, r, hover && !_hoverAction); break;
                    case UiListKind.CardRow: PlaceThread(row, it, r, idx == SelectedIndex, hover); break;
                    case UiListKind.CardSection: PlaceSection(row, it, r, hover); break;
                    case UiListKind.CardNote: PlaceNote(row, it, r, _cardOf[idx] == idx, hover); break;
                    default: PlacePlain(row, it, r, idx == SelectedIndex, hover); break;
                }
                row.item = idx;
                row.shown = r;
            }
            // the boxes: one per card with a row on screen, from its first row shown to its last (cut at the list's top when
            // its head scrolled away, at its last whole row at the bottom)
            int used = 0;
            for (int idx = first; idx <= last; idx++)
            {
                int s = _cardOf[idx];
                if (s < 0 || (idx > first && _cardOf[idx - 1] == s)) continue;
                if (used >= _cards.Count) break;
                DrawCard(_cards[used++], s, first, last, top0);
            }
            for (int k = used; k < _cards.Count; k++) HideCard(_cards[k]);
            // the scroll bar
            float frac = _contentH <= Rect.height ? 1f : Rect.height / _contentH;
            float maxTop = _maxFirst < _tops.Count ? _tops[_maxFirst] : 0f;
            float pos = maxTop <= 0f || n == 0 ? 0f : Mathf.Clamp01(top0 / maxTop);
            float h = Rect.height * frac;
            var bar = new Rect(Rect.xMax - 0.004f, Rect.y + (Rect.height - h) * pos, 0.004f, h);
            Panel.Shapes.SetRect(_bar, Panel.PanelToLocal(bar.center), bar.size);
            Panel.Shapes.SetVisible(_bar, Visible && frac < 1f);
            if (_empty != null)
            {
                bool none = Visible && n == 0 && !string.IsNullOrEmpty(EmptyText);
                if (none) _empty.Text = EmptyText;
                _empty.SetVisible(none);
            }
        }

        private void DrawCard(CardVis c, int s, int first, int last, float top0)
        {
            var head = _items[s];
            Color type = head.BarColor ?? Theme.Accent;
            bool headed = head.Kind == UiListKind.CardHead, headShown = s >= first;
            int end = Mathf.Min(_cardLast[s], last);
            float y0 = headShown ? Rect.y + _tops[s] - top0 : Rect.y;
            float y1 = end == _cardLast[s] ? Rect.y + _cardBottom[s] - top0 : Rect.y + _tops[end] - top0 + HeightOf(_items[end]);
            y1 = Mathf.Min(y1, Rect.yMax);
            var box = new Rect(Rect.x, y0, CardWidth, Mathf.Max(0.002f, y1 - y0));
            SetShape(c.fill, box, CardFill);
            SetShape(c.stripe, new Rect(box.x, box.y, StripeWidth, box.height), type);
            // the guide: down the left of the rows under the head, from under its fold triangle to the tick of the last row shown
            int g0 = Mathf.Max(first, s + 1);
            if (headed && g0 <= end)
            {
                float gy0 = g0 == s + 1 ? Rect.y + _tops[g0] - top0 : Rect.y;
                float gy1 = Rect.y + _tops[end] - top0 + TickY(_items[end]);
                SetShape(c.guide, new Rect(box.x + GuideX - Hair * 0.5f, gy0, Hair, Mathf.Max(0.001f, gy1 - gy0)), GuideColor);
            }
            else ShowShape(c.guide, false);
            if (!headed || !headShown)
            {
                ShowShape(c.rule, false); ShowShape(c.chip, false); ShowShape(c.plus, false);
                c.chipText.SetVisible(false); c.plusText.SetVisible(false);
                return;
            }
            var hr = new Rect(Rect.x, Rect.y + _tops[s] - top0, CardWidth, HeightOf(head));
            // a hairline under a head with rows under it
            if (_cardLast[s] > s && s + 1 <= last) SetShape(c.rule, new Rect(hr.x + 0.010f, hr.yMax - Hair * 0.5f, hr.width - 0.020f, Hair), RuleColor);
            else ShowShape(c.rule, false);
            HeadSlots(hr, head, out var plus, out _, out var chip);
            if (!string.IsNullOrEmpty(head.Chip))
            {
                Color cc = head.ChipColor ?? type;
                SetShape(c.chip, chip, Alpha(cc, 0.22f));
                c.chipText.SetVisible(true);
                SetSize(c.chipText, TagSize, true);
                c.chipText.SetRect(chip);
                c.chipText.Text = head.Chip;
                c.chipText.Color = Color.Lerp(cc, Color.white, 0.4f);
            }
            else { ShowShape(c.chip, false); c.chipText.SetVisible(false); }
            if (!string.IsNullOrEmpty(head.Action))
            {
                bool hot = _hoverAction && _hoverRow == s - first;
                SetShape(c.plus, plus, Alpha(Theme.Accent, hot ? 0.45f : 0.18f));
                c.plusText.SetVisible(true);
                c.plusText.SetRect(plus);
                c.plusText.Text = head.Action;
                c.plusText.Color = hot ? Theme.Text : Theme.TextAccent;
            }
            else { ShowShape(c.plus, false); c.plusText.SetVisible(false); }
        }

        /// Where a row's tick meets the guide, from the row's top: the middle of a thread's title line, else the row's middle.
        private static float TickY(UiListItem it) => it.Kind == UiListKind.CardRow ? 0.004f + LineH * 0.5f : HeightOf(it) * 0.5f;

        /// A head's right side, from the right: the button, the count (a column of one width, so the counts line up), the chip.
        private void HeadSlots(Rect r, UiListItem it, out Rect plus, out Rect count, out Rect chip)
        {
            float cy = r.center.y, right = r.xMax - Pad;
            bool hasPlus = !string.IsNullOrEmpty(it.Action);
            plus = hasPlus ? new Rect(right - PlusSize, cy - PlusSize * 0.5f, PlusSize, PlusSize) : new Rect(right, cy, 0f, 0f);
            float cx = hasPlus ? plus.x - Theme.Gap : right;
            count = new Rect(cx - CountWidth, r.y, CountWidth, r.height);
            float chipW = string.IsNullOrEmpty(it.Chip) ? 0f : Mathf.Min(r.width * 0.34f, it.Chip.Length * TagSize * 0.62f + 0.016f);
            const float chipH = 0.021f;
            chip = new Rect(count.x - Theme.Gap - chipW, cy - chipH * 0.5f, chipW, chipH);
        }

        /// A card's head: the fold triangle over the guide, the name (bold) and its folder under it; on the right the chip
        /// (DrawCard), a dot while folded with something urgent, the count and the + (DrawCard).
        private void PlaceHead(PoolRow row, UiListItem it, Rect r, bool hover)
        {
            HeadSlots(r, it, out _, out var count, out var chip);
            row.action = string.IsNullOrEmpty(it.Action) ? default : new Rect(r.xMax - PlusHitWidth, r.y, PlusHitWidth, r.height);
            if (hover) SetShape(row.plate, new Rect(r.x + StripeWidth, r.y, r.width - StripeWidth - PlusHitWidth, r.height), HoverTint);
            else ShowShape(row.plate, false);
            ShowShape(row.tick, false);
            bool two = !string.IsNullOrEmpty(it.Sub);
            float ny = two ? r.y + 0.0085f : r.center.y - LineH * 0.5f;
            float nameW = Mathf.Max(0.04f, (string.IsNullOrEmpty(it.Chip) ? count.x : chip.x) - Theme.Gap - (r.x + NameX));
            row.icon.SetVisible(true);
            SetSize(row.icon, Theme.BodySize, false);
            row.icon.SetRect(new Rect(r.x + GuideX - 0.012f, ny, 0.024f, LineH));
            row.icon.Text = it.Icon;
            row.icon.Color = it.IconColor ?? Theme.TextAccent;
            row.title.SetVisible(true);
            SetSize(row.title, NameSize, false);
            row.title.SetRect(new Rect(r.x + NameX, ny, nameW, LineH));
            row.title.Text = it.Bold ? "<b><noparse>" + it.Title + "</noparse></b>" : "<noparse>" + it.Title + "</noparse>";
            row.title.Color = it.TitleColor ?? Theme.Text;
            if (two)
            {
                row.sub.SetVisible(true);
                SetSize(row.sub, CaptionSize, false);
                SetAlign(row.sub, TextAlignmentOptions.MidlineLeft);
                row.sub.SetRect(new Rect(r.x + NameX, ny + LineH, nameW, SubH));
                row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
                row.sub.Color = SubColor;
            }
            else row.sub.SetVisible(false);
            row.badge.SetVisible(true);
            SetSize(row.badge, CountSize, true);
            row.badge.SetRect(count);
            row.badge.Text = it.Badge;
            row.badge.Color = it.BadgeColor ?? Theme.TextDim;
            if (it.Dot.HasValue)
            {
                SetShape(row.dot, new Rect(count.x + 0.003f, r.center.y - DotSize * 0.5f, DotSize, DotSize), it.Dot.Value);
                Panel.Shapes.SetState(row.dot, it.DotLit ? 1f : 0.3f);
            }
            else ShowShape(row.dot, false);
        }

        /// A thread in a card: the plate (chosen, open in a window, hovered), the tick from the guide to its status dot, the
        /// title with its status word on the right, and the dim second line under it.
        private void PlaceThread(PoolRow row, UiListItem it, Rect r, bool sel, bool hover)
        {
            var plate = new Rect(r.x + RowX, r.y + 0.002f, r.width - RowX - Pad * 0.5f, r.height - 0.004f);
            Color a = Theme.Accent;
            if (sel) SetShape(row.plate, plate, Alpha(a, hover ? 0.34f : 0.28f));
            else if (it.Highlight) SetShape(row.plate, plate, Alpha(a, hover ? 0.20f : 0.13f));
            else if (hover) SetShape(row.plate, plate, HoverTint);
            else ShowShape(row.plate, false);
            float ty = r.y + 0.004f, mid = ty + LineH * 0.5f;
            SetShape(row.tick, new Rect(r.x + GuideX, mid - Hair * 0.5f, DotX - DotSize * 0.5f - 0.002f - GuideX, Hair), GuideColor);
            bool dot = it.Dot.HasValue;
            if (dot)
            {
                SetShape(row.dot, new Rect(r.x + DotX - DotSize * 0.5f, mid - DotSize * 0.5f, DotSize, DotSize), it.Dot.Value);
                Panel.Shapes.SetState(row.dot, it.DotLit ? 1f : 0.3f);
            }
            else ShowShape(row.dot, false);
            PlaceIcon(row, it, new Rect(r.x + DotX - 0.012f, ty, 0.024f, LineH), !dot && !string.IsNullOrEmpty(it.Icon));
            float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : it.Badge.Length * TagSize * 0.62f + 0.010f;
            float x = r.x + TextX, w = r.xMax - Pad - x;
            row.title.SetVisible(true);
            SetSize(row.title, TitleSize, false);
            row.title.SetRect(new Rect(x, ty, Mathf.Max(0.03f, w - badgeW - (badgeW > 0f ? Theme.Gap : 0f)), LineH));
            row.title.Text = "<noparse>" + it.Title + "</noparse>";
            row.title.Color = it.TitleColor ?? Theme.Text;
            row.sub.SetVisible(true);
            SetSize(row.sub, MetaSize, false);
            SetAlign(row.sub, TextAlignmentOptions.MidlineLeft);
            row.sub.SetRect(new Rect(x, ty + LineH, w, SubH));
            row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
            row.sub.Color = SubColor;
            PlaceBadge(row, it, new Rect(r.xMax - Pad - badgeW, ty, badgeW, LineH), Theme.TextDim);
        }

        /// A folder in a card (the threads under it work there): the tick, its icon in the dots' column, its name, its
        /// caption on the right, its badge.
        private void PlaceSection(PoolRow row, UiListItem it, Rect r, bool hover)
        {
            if (hover) SetShape(row.plate, new Rect(r.x + RowX, r.y + 0.002f, r.width - RowX - Pad * 0.5f, r.height - 0.004f), HoverTint);
            else ShowShape(row.plate, false);
            float mid = r.center.y;
            SetShape(row.tick, new Rect(r.x + GuideX, mid - Hair * 0.5f, DotX - 0.008f - GuideX, Hair), GuideColor);
            ShowShape(row.dot, false);
            PlaceIcon(row, it, new Rect(r.x + DotX - 0.012f, r.y, 0.024f, r.height), true);
            float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : it.Badge.Length * TagSize * 0.62f + 0.010f;
            float x = r.x + TextX, w = r.xMax - Pad - badgeW - x;
            float subW = string.IsNullOrEmpty(it.Sub) ? 0f : Mathf.Min(w * 0.45f, it.Sub.Length * MetaSize * 0.5f + 0.010f);
            row.title.SetVisible(true);
            SetSize(row.title, MetaSize * 1.04f, false);
            row.title.SetRect(new Rect(x, r.y, Mathf.Max(0.03f, w - subW - Theme.Gap), r.height));
            row.title.Text = "<noparse>" + it.Title + "</noparse>";
            row.title.Color = it.TitleColor ?? Theme.TextDim;
            row.sub.SetVisible(subW > 0f);
            SetSize(row.sub, MetaSize, false);
            SetAlign(row.sub, TextAlignmentOptions.MidlineRight);
            row.sub.SetRect(new Rect(x + w - subW, r.y, subW, r.height));
            row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
            row.sub.Color = SubColor;
            PlaceBadge(row, it, new Rect(r.xMax - Pad - badgeW, r.y, badgeW, r.height), Theme.Warn);
        }

        /// A hint with an action (Badge, in the accent, on the right): in a card, a row like a thread's; alone, a card of
        /// its own (the empty states) with its icon under where a head's fold triangle is, and its Sub as a second line.
        private void PlaceNote(PoolRow row, UiListItem it, Rect r, bool alone, bool hover)
        {
            float x0 = alone ? StripeWidth : RowX;
            if (hover) SetShape(row.plate, new Rect(r.x + x0, r.y + 0.002f, r.width - x0 - Pad * 0.5f, r.height - 0.004f), HoverTint);
            else ShowShape(row.plate, false);
            ShowShape(row.dot, false);
            bool two = alone && !string.IsNullOrEmpty(it.Sub);
            float ly = two ? r.y + 0.006f : r.center.y - LineH * 0.5f;
            if (alone) ShowShape(row.tick, false);
            else SetShape(row.tick, new Rect(r.x + GuideX, r.center.y - Hair * 0.5f, DotX - 0.008f - GuideX, Hair), GuideColor);
            PlaceIcon(row, it, new Rect(r.x + (alone ? GuideX : DotX) - 0.012f, ly, 0.024f, LineH), true);
            float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : it.Badge.Length * TagSize * 0.62f + 0.010f;
            float x = r.x + (alone ? NameX : TextX), w = r.xMax - Pad - badgeW - Theme.Gap - x;
            row.title.SetVisible(true);
            SetSize(row.title, alone ? NameSize * 0.95f : TitleSize, false);
            row.title.SetRect(new Rect(x, ly, Mathf.Max(0.03f, w), LineH));
            row.title.Text = "<noparse>" + it.Title + "</noparse>";
            row.title.Color = it.TitleColor ?? (alone ? Theme.Text : Theme.TextDim);
            row.sub.SetVisible(two);
            if (two)
            {
                SetSize(row.sub, MetaSize, false);
                SetAlign(row.sub, TextAlignmentOptions.MidlineLeft);
                row.sub.SetRect(new Rect(x, ly + LineH, Mathf.Max(0.03f, w), SubH));
                row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
                row.sub.Color = SubColor;
            }
            PlaceBadge(row, it, new Rect(r.xMax - Pad - badgeW, r.y, badgeW, r.height), Theme.Accent);
        }

        /// A plain row (the archived threads): a faint plate like a small card, the icon, the title, the sub on the right
        /// before the badge.
        private void PlacePlain(PoolRow row, UiListItem it, Rect r, bool sel, bool hover)
        {
            var plate = new Rect(r.x, r.y + 0.002f, r.width, r.height - 0.004f);
            SetShape(row.plate, plate, sel ? Alpha(Theme.Accent, 0.28f) : hover ? Alpha(Theme.Accent, 0.14f) : CardFill);
            ShowShape(row.tick, false);
            bool dot = it.Dot.HasValue;
            if (dot)
            {
                SetShape(row.dot, new Rect(r.x + GuideX - DotSize * 0.5f, r.center.y - DotSize * 0.5f, DotSize, DotSize), it.Dot.Value);
                Panel.Shapes.SetState(row.dot, it.DotLit ? 1f : 0.3f);
            }
            else ShowShape(row.dot, false);
            PlaceIcon(row, it, new Rect(r.x + GuideX - 0.012f, r.y, 0.024f, r.height), !dot);
            float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : Mathf.Min(r.width * 0.3f, it.Badge.Length * TagSize * 0.62f + 0.010f);
            float x = r.x + NameX, w = r.xMax - Pad - badgeW - x;
            float subW = string.IsNullOrEmpty(it.Sub) ? 0f : Mathf.Min(w * 0.5f, it.Sub.Length * MetaSize * 0.5f + 0.010f);
            row.title.SetVisible(true);
            SetSize(row.title, TitleSize, false);
            row.title.SetRect(new Rect(x, r.y, Mathf.Max(0.03f, w - subW - Theme.Gap), r.height));
            row.title.Text = "<noparse>" + it.Title + "</noparse>";
            row.title.Color = it.TitleColor ?? Theme.Text;
            row.sub.SetVisible(subW > 0f);
            SetSize(row.sub, MetaSize, false);
            SetAlign(row.sub, TextAlignmentOptions.MidlineRight);
            row.sub.SetRect(new Rect(x + w - subW, r.y, subW, r.height));
            row.sub.Text = "<noparse>" + it.Sub + "</noparse>";
            row.sub.Color = SubColor;
            PlaceBadge(row, it, new Rect(r.xMax - Pad - badgeW, r.y, badgeW, r.height), Theme.Ok);
        }

        private void PlaceIcon(PoolRow row, UiListItem it, Rect r, bool shown)
        {
            row.icon.SetVisible(shown);
            if (!shown) return;
            SetSize(row.icon, MetaSize * 1.15f, false);
            row.icon.SetRect(r);
            row.icon.Text = it.Icon;
            row.icon.Color = it.IconColor ?? Theme.TextAccent;
        }

        private void PlaceBadge(PoolRow row, UiListItem it, Rect r, Color fallback)
        {
            bool shown = !string.IsNullOrEmpty(it.Badge);
            row.badge.SetVisible(shown);
            if (!shown) return;
            SetSize(row.badge, TagSize, true);
            row.badge.SetRect(r);
            row.badge.Text = it.Badge;
            row.badge.Color = it.BadgeColor ?? fallback;
        }

        private void SetShape(int id, Rect r, Color tint)
        {
            if (id < 0) return;
            Panel.Shapes.SetRect(id, Panel.PanelToLocal(r.center), r.size);
            Panel.Shapes.SetTint(id, tint);
            Panel.Shapes.SetVisible(id, true);
        }

        private void ShowShape(int id, bool v) { if (id >= 0) Panel.Shapes.SetVisible(id, v); }

        private void HideRow(PoolRow row)
        {
            ShowShape(row.plate, false); ShowShape(row.tick, false); ShowShape(row.dot, false);
            row.icon.SetVisible(false); row.title.SetVisible(false); row.sub.SetVisible(false); row.badge.SetVisible(false);
        }

        private void HideCard(CardVis c)
        {
            ShowShape(c.fill, false); ShowShape(c.stripe, false); ShowShape(c.guide, false); ShowShape(c.rule, false);
            ShowShape(c.chip, false); ShowShape(c.plus, false);
            c.chipText.SetVisible(false); c.plusText.SetVisible(false);
        }

        /// The font size for a line height, only when it changes (a pooled text shows several kinds); the display face is
        /// set a little larger, as UiText.Create does.
        private void SetSize(UiText text, float lineHeight, bool display)
        {
            float v = Theme.FontSizeFor(lineHeight) * (display ? 1.12f : 1f);
            if (text.Tmp.fontSize != v) text.Tmp.fontSize = v;
        }

        private static void SetAlign(UiText text, TextAlignmentOptions a) { if (text.Tmp.alignment != a) text.Tmp.alignment = a; }

        private int RowAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].item >= 0 && _rows[i].shown.Contains(p)) return i;
            return -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragStartScroll = _scroll; _dragged = false; }

        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - e.PressPoint.y;
            if (Mathf.Abs(dy) > RowHeight * 0.4f) _dragged = true;
            if (_dragged)
            {
                float s = Mathf.Clamp(_dragStartScroll - dy / RowHeight, 0f, _maxFirst);
                bool step = Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll); // whole items: drawn again only when one scrolls by
                _scroll = s;
                if (step) Render();
            }
            OnHover(e);
        }

        public override void OnHover(UiPointerEvent e)
        {
            int hr = RowAt(e.Point);
            bool ha = hr >= 0 && _rows[hr].action.Contains(e.Point);
            if (hr != _hoverRow || ha != _hoverAction) { _hoverRow = hr; _hoverAction = ha; Render(); }
        }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragged && inside) OnClick(e);
            _dragged = false;
        }

        public override void OnClick(UiPointerEvent e)
        {
            int ri = RowAt(e.PressPoint);
            if (ri < 0) return;
            int idx = _rows[ri].item;
            if (idx < 0 || idx >= _items.Count) return;
            if (_rows[ri].action.Contains(e.PressPoint)) { OnAction?.Invoke(_items[idx], idx); return; }
            SelectIndex(idx);
        }

        /// A thread (or a plain row) stays lit as chosen; a head, a folder or a note only acts. -1: none lit.
        public void SelectIndex(int idx, bool notify = true)
        {
            if (idx < -1 || idx >= _items.Count) return;
            var it = idx >= 0 ? _items[idx] : null;
            if ((it == null || it.Kind == UiListKind.CardRow || !InCard(it.Kind)) && SelectedIndex != idx) { SelectedIndex = idx; Render(); }
            if (notify && it != null) OnSelect?.Invoke(it, idx);
        }

        protected override void OnInteractionChanged()
        {
            if (Hover || (_hoverRow == -1 && !_hoverAction)) return;
            _hoverRow = -1;
            _hoverAction = false;
            Render();
        }

        protected override void OnVisibility(bool v) => Render();

        protected override void Teardown()
        {
            foreach (var row in _rows)
            {
                HideRow(row);
                DestroyGo(row.icon.Tmp.gameObject); DestroyGo(row.title.Tmp.gameObject);
                DestroyGo(row.sub.Tmp.gameObject); DestroyGo(row.badge.Tmp.gameObject);
            }
            foreach (var c in _cards)
            {
                HideCard(c);
                DestroyGo(c.chipText.Tmp.gameObject); DestroyGo(c.plusText.Tmp.gameObject);
            }
            _rows.Clear();
            _cards.Clear();
            if (_empty != null) { DestroyGo(_empty.Tmp.gameObject); _empty = null; }
            ShowShape(_bar, false);
        }
    }

    /// A row of filter segments (THREADS' project types, owner 2026-11-17): each a label and a count, the types with a small
    /// swatch in their colour (the stripe of the cards they show). The chosen one is lit in its colour. Segments change
    /// and hide (SetSegment, then Apply once); their widths follow their labels. Dimmed: drawn faint, taps do nothing.
    public sealed class UiFilterBar : UiElement
    {
        public const int Max = 7;
        public Action<int> OnChanged;
        public int Selected { get; private set; }
        public int Count { get; private set; }
        public bool Dimmed { get; private set; }
        /// The label's line height (0: SmallSize * 1.06).
        public float TextSize;
        public override bool Interactive => true;
        private readonly string[] _label = new string[Max], _count = new string[Max], _text = new string[Max];
        private readonly Color?[] _swatch = new Color?[Max];
        private readonly int[] _shape = new int[Max], _mark = new int[Max];
        private readonly UiText[] _texts = new UiText[Max];
        private readonly Rect[] _seg = new Rect[Max];
        private int _hover = -1;
        private bool _layout = true;

        /// A segment's label and count as set (tests).
        public string LabelOf(int i) => i >= 0 && i < Count ? _label[i] : null;
        public string CountOf(int i) => i >= 0 && i < Count ? _count[i] : null;

        protected override void Build()
        {
            if (TextSize <= 0f) TextSize = Theme.SmallSize * 1.06f;
            for (int i = 0; i < Max; i++)
            {
                _shape[i] = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Button, Rect, Theme.AccentSoft, 0.006f, 0.002f, 0.005f));
                _mark[i] = Panel.Shapes.Add(Panel.Shape(UiShapeKind.Flat, Rect, Theme.Accent, 0.0015f, 0f, 0.0015f));
                _texts[i] = UiText.Create(Panel, Rect, "", Theme.Display, TextSize, Theme.TextDim, TextAlignmentOptions.Center);
            }
            _layout = true;
            Refresh();
        }

        /// Segment i: its label, its count (in countColor), its swatch (null: none, as ALL). Drawn at the next Apply.
        public void SetSegment(int i, string label, string count, Color? swatch, Color countColor)
        {
            if (i < 0 || i >= Max) return;
            label ??= ""; count ??= "";
            if (_label[i] != label || _count[i] != count || _swatch[i].HasValue != swatch.HasValue) _layout = true;
            _label[i] = label; _count[i] = count; _swatch[i] = swatch;
            string text = count.Length == 0 ? label : label + "  <color=#" + ColorUtility.ToHtmlStringRGB(countColor) + ">" + count + "</color>";
            if (_text[i] != text) _text[i] = text;
        }

        /// Shows the first `count` segments with `selected` lit; dimmed: faint, taps ignored.
        public void Apply(int count, int selected, bool dimmed)
        {
            count = Mathf.Clamp(count, 0, Max);
            if (count != Count) _layout = true;
            Count = count;
            Selected = Mathf.Clamp(selected, -1, count - 1);
            Dimmed = dimmed;
            Refresh();
        }

        private void LayoutSegments()
        {
            _layout = false;
            if (Count == 0) return;
            float total = Rect.width - Theme.Gap * (Count - 1), sum = 0f;
            for (int i = 0; i < Count; i++) sum += Want(i);
            float k = sum > 0f ? total / sum : 1f, x = Rect.x;
            for (int i = 0; i < Count; i++)
            {
                float w = Want(i) * k;
                _seg[i] = new Rect(x, Rect.y + 0.002f, w, Rect.height - 0.004f);
                x += w + Theme.Gap;
            }
        }

        /// About the room a segment's text needs (the display face's capitals), its swatch and some air.
        private float Want(int i) => ((_label[i]?.Length ?? 0) + (_count[i]?.Length ?? 0) + 2) * TextSize * 0.62f + (_swatch[i].HasValue ? 0.016f : 0.008f) + 0.012f;

        private void Refresh()
        {
            if (Panel == null) return;
            if (_layout) LayoutSegments();
            for (int i = 0; i < Max; i++)
            {
                bool on = Visible && i < Count;
                if (!on) { Panel.Shapes.SetVisible(_shape[i], false); Panel.Shapes.SetVisible(_mark[i], false); _texts[i].SetVisible(false); continue; }
                var r = _seg[i];
                bool sel = i == Selected, hot = !Dimmed && i == _hover && Hover;
                Color own = _swatch[i] ?? Theme.Accent;
                Panel.Shapes.SetRect(_shape[i], Panel.PanelToLocal(r.center), r.size);
                Panel.Shapes.SetTint(_shape[i], sel ? own : Theme.AccentSoft);
                Panel.Shapes.SetState(_shape[i], Dimmed ? 0f : sel ? 0.85f : hot ? 0.35f : 0f);
                Panel.Shapes.SetVisible(_shape[i], true);
                bool swatch = _swatch[i].HasValue;
                if (swatch)
                {
                    var m = new Rect(r.x + 0.009f, r.center.y - 0.0075f, 0.0035f, 0.015f);
                    Panel.Shapes.SetRect(_mark[i], Panel.PanelToLocal(m.center), m.size);
                    Panel.Shapes.SetTint(_mark[i], new Color(own.r, own.g, own.b, Dimmed ? 0.35f : sel ? 1f : 0.8f));
                }
                Panel.Shapes.SetVisible(_mark[i], swatch);
                _texts[i].SetVisible(true);
                _texts[i].SetRect(swatch ? new Rect(r.x + 0.015f, r.y, r.width - 0.019f, r.height) : new Rect(r.x + 0.004f, r.y, r.width - 0.008f, r.height));
                _texts[i].Text = _text[i] ?? "";
                Color tc = sel ? Theme.Text : Theme.TextDim;
                _texts[i].Color = Dimmed ? new Color(tc.r, tc.g, tc.b, 0.45f) : tc;
            }
        }

        private int IndexAt(Vector2 p)
        {
            for (int i = 0; i < Count; i++) if (_seg[i].Contains(p)) return i;
            return -1;
        }

        public override void OnDrag(UiPointerEvent e) => OnHover(e);

        public override void OnHover(UiPointerEvent e)
        {
            int i = IndexAt(e.Point);
            if (i != _hover) { _hover = i; Refresh(); }
        }

        protected override void OnInteractionChanged()
        {
            if (!Hover && _hover != -1) { _hover = -1; Refresh(); }
        }

        public override void OnClick(UiPointerEvent e)
        {
            if (Dimmed) return;
            int i = IndexAt(e.Point);
            if (i < 0) i = IndexAt(e.PressPoint);
            if (i < 0) return;
            if (i != Selected) { Selected = i; Refresh(); }
            OnChanged?.Invoke(i);
        }

        /// Picks segment i as a tap would (tests, voice).
        public void Pick(int i)
        {
            if (i < 0 || i >= Count) return;
            if (i != Selected) { Selected = i; Refresh(); }
            OnChanged?.Invoke(i);
        }

        protected override void OnVisibility(bool v) => Refresh();

        protected override void Teardown()
        {
            for (int i = 0; i < Max; i++)
            {
                if (_texts[i] == null) continue;
                Panel.Shapes.SetVisible(_shape[i], false);
                Panel.Shapes.SetVisible(_mark[i], false);
                DestroyGo(_texts[i].Tmp.gameObject);
                _texts[i] = null;
            }
        }
    }
}
