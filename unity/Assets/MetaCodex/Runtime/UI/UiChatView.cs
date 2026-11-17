using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    public enum ChatKind { Prompt, Answer, Update, Thinking, Work, Step, Notice, Error, Meta }

    /// One block of a chat: a prompt bubble, an answer, a collapsible thinking or work summary, a step line, a dim
    /// "who and when" line (Meta).
    public sealed class ChatBlock
    {
        public string Key = "";
        public ChatKind Kind;
        public string Head = "";       // the header line (rich text, already escaped by whoever builds it)
        public string Right = "";      // dim text at the right end of the header line (times, stats, a status chip)
        public string Body = "";       // Prompt / Answer / Update: markdown-lite; Thinking / Step / Error: plain
        public string Preview = "";    // shown under a closed header (the reasoning streaming now)
        public bool Collapsible, Expanded, Live, MonoBody;
        public int Indent;             // 0, or 1 inside a work group
        public string Parent;          // the work group it belongs to: hidden while that one is closed
        public string Exchange = "";   // the prompt's key: the sticky prompt of whatever is on screen
        public Color? Tint;            // the bubble / bar / header / card colour
        public int BodyMaxChars = 6000;
        // ---- the verbose chat (owner 2026-11-17)
        public bool MonoHead;          // the header line in the terminal font (a command)
        public string Tail = "";       // closed: the last lines of an output under the header (mono, dim; live while it runs)
        public int TailLines = 6;      // at most this many wrapped lines of the tail
        public Color? RightTint;       // a chip behind Right (running / ok / exit N)
        public long LiveSince;         // a live step's start (ms epoch): the view adds its running time to Right
        public bool Card;              // a tinted card: an approval or a question waiting for you, an error
        public bool Diff;              // the body is a unified diff: + lines green, − lines red
        public bool AlignRight;        // Meta lines: on the right (yours) or on the left (the agent's)
        public object Tag;             // the owner's (an edit's path: a tap opens its diff)
    }

    /// A chat drawn on a panel (owner 2026-11-11, 2026-11-17): your prompts as bubbles on the right under a dim "You ·
    /// time" line, the agent's turn under its name line: thinking and work as headers that open with a tap, commands with
    /// the last lines of their output, the answer in full; newest at the bottom. Drag to scroll, or the UiScrollbar
    /// attached to it; the prompt of what is on screen stays pinned at the top once it scrolled away; while the view
    /// sits at the bottom it follows new content, otherwise it holds still and counts what is new. A NOW strip at the
    /// bottom says what the agent does this second (SetNow).
    ///
    /// Layout: every line has a fixed slot (its font's line height and the gaps around it) and the slots of the blocks
    /// follow each other, so blocks cannot overlap as long as each text draws inside its slots. The overlap the owner saw
    /// (2026-11-17, "sometimes my message overlaps with the agent's answer") came from TextMeshPro drawing taller than
    /// the slots: a line with a glyph from a symbol fallback font (Noto Sans Symbols: 2.05 em per line, Symbols 2:
    /// 1.70 em, against Inter's 1.21 em; ⌕, ⎯, ⏳, ⛔, 🟥, ⭐ ...) moved that line and every line after it in the same
    /// text object down, and a block with a few such lines ran into the next one. Now each text object is placed by its
    /// first baseline (Baseline alignment: a tall glyph cannot push the first line down) and its lines are spaced by a
    /// fixed line height (`<line-height>`), both the values the layout used; escape sequences (`\n`, `\v` in agent text)
    /// are not parsed; control characters are cleaned (ChatText.Sanitize). A guard checks each drawn text once after its
    /// mesh is built: one that came out with more lines than its slots grows its block and the chat flows again.
    public sealed class UiChatView : UiCanvas
    {
        public float TextSize = 0.0128f;
        public override bool Interactive => true;
        public Action<ChatBlock, bool> OnToggled;  // a block was opened (true) or closed
        public Action<ChatBlock> OnLinkClicked;    // a tap on a block that is not collapsible
        public Action OnScrolled;                  // the user scrolled (drag, scrollbar, page)
        public Action OnNowClicked;                // the NOW strip was tapped (it scrolls to the end first)
        public Action<string> Log;                 // the re-flow guard reports here (session log)
        public Func<long> NowMs;                   // the clock for running times (ms epoch); null: the real one
        public string Empty = "";
        public int NewLines { get; private set; }
        public bool AtBottom => _scroll == 0;
        public IReadOnlyList<ChatBlock> Blocks => _blocks;
        public int LineCount => _lines.Count;
        public UiScrollbar Scrollbar { get; private set; }

        private struct VLine
        {
            public int Block;
            public byte Part;      // 0 header, 1 preview / tail, 2 body
            public int Index;      // the line's number inside its part
            public string Text;    // display text (rich, escaped)
            public bool Mono, Code, RightAlign, Cut, Small;
            public Color Color;
            public float H, PadTop, PadBottom, X, W; // H = the pitch + PadTop + PadBottom
        }

        private readonly List<ChatBlock> _blocks = new List<ChatBlock>();
        private readonly HashSet<string> _toggled = new HashSet<string>();
        private readonly List<VLine> _lines = new List<VLine>();
        private readonly List<float> _cum = new List<float>(); // _cum[i]: the top of line i; _cum[Count]: the content height
        private readonly Dictionary<string, (string src, float w, bool mono, List<(string text, float w, int para)> lines)> _wrapCache = new Dictionary<string, (string, float, bool, List<(string, float, int)>)>();
        private readonly List<(int line, float y0, float y1)> _drawn = new List<(int, float, float)>();
        private Rect _band;            // the sticky prompt's rect (zero when none)
        private int _bandBlock = -1;
        private int _scroll, _dragStart, _hoverLine = -1, _spin;
        private bool _dragged;
        private float _nextSpin;
        private TextMeshPro _mBody, _mMono;
        private float _pitchBody = 0.016f, _pitchMono = 0.016f, _ascBody = 0.012f, _ascMono = 0.012f;
        private string _lhBody = "", _lhMono = "", _lhSmall = "";

        private const float SmallK = 0.86f;          // Meta lines (who and when)
        private float Pitch(bool mono, bool small = false) => mono ? _pitchMono : small ? _pitchBody * SmallK : _pitchBody;
        private float Asc(bool mono, bool small) => mono ? _ascMono : small ? _ascBody * SmallK : _ascBody;
        private float Wide => Rect.width - 0.002f;   // the scroll bar lives outside (UiScrollbar)
        private const float BubblePad = 0.007f, BarX = 0.009f, IndentStep = 0.016f, CardPad = 0.004f, TurnGap = 0.022f;

        protected override void Build()
        {
            _mBody = MakeMeasurer(Theme.Body);
            _mMono = MakeMeasurer(Theme.Mono);
            (_pitchBody, _ascBody) = Metrics(Theme.Body, _mBody);
            (_pitchMono, _ascMono) = Metrics(Theme.Mono ?? Theme.Body, _mMono);
            _lhBody = LineHeightTag(_pitchBody);
            _lhMono = LineHeightTag(_pitchMono);
            _lhSmall = LineHeightTag(_pitchBody * SmallK);
            BuildNow();
            Layout();
            base.Build();
        }

        protected override void Teardown()
        {
            if (_mBody != null) DestroyGo(_mBody.gameObject);
            if (_mMono != null) DestroyGo(_mMono.gameObject);
            foreach (var t in new[] { _nowSpin, _nowMain, _nowTime }) if (t != null) DestroyGo(t.Tmp.gameObject);
            _nowSpin = _nowMain = _nowTime = null;
            base.Teardown();
        }

        // a new rect: the wrapped lines stay good while the width is the same (the ask row only takes height away)
        protected override void Relayout()
        {
            if (Mathf.Abs(Wide - _cachedWide) > 1e-5f) { ClearLayoutCaches(); _cachedWide = Wide; }
            Layout();
            Redraw();
        }

        private float _cachedWide = -1f;

        protected override void OnVisibility(bool v)
        {
            base.OnVisibility(v);
            Scrollbar?.SetVisible(v);
            if (!v) ShowNowTexts(false);
        }

        /// The scrollbar beside the chat: it follows the view, and its drags and taps scroll it.
        public void AttachScrollbar(UiScrollbar bar)
        {
            Scrollbar = bar;
            if (bar == null) return;
            bar.OnScrollTo = off => { ScrollToOffset(off); OnScrolled?.Invoke(); };
            bar.OnPage = dir => { Page(dir); OnScrolled?.Invoke(); };
            bar.SetVisible(Visible);
            Redraw();
        }

        private TextMeshPro MakeMeasurer(TMP_FontAsset font)
        {
            var ut = UiText.Create(Panel, new Rect(0, 0, 1f, 1f), "", font, TextSize, Color.clear, TextAlignmentOptions.TopLeft, true);
            var t = ut.Tmp;
            t.richText = false;
            t.parseCtrlCharacters = false; // a "\n" an agent writes is two characters, not a line break (TMP Settings parse them)
            t.overflowMode = TextOverflowModes.Overflow;
            t.gameObject.name = "chat-measure";
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false; // active (TextMeshPro lays out only when active), never drawn
            return t;
        }

        /// The line pitch and the ascender of a font at the chat's size, as TextMeshPro spaces lines of that font
        /// (face line height x its scale); a measured "Ag\nAg" when the face is not known.
        private (float pitch, float asc) Metrics(TMP_FontAsset font, TextMeshPro measurer)
        {
            if (font != null)
            {
                var fi = font.faceInfo;
                float sc = fi.scale > 0f ? fi.scale : 1f;
                if (fi.pointSize > 0 && fi.lineHeight > 0f)
                {
                    float s = Theme.FontSizeFor(TextSize) / fi.pointSize * sc * 0.1f;
                    float p = fi.lineHeight * s, a = fi.ascentLine * s;
                    if (p > 0.004f && p < 0.05f && a > 0f && a < p * 1.5f) return (p, a);
                }
            }
            float m = MeasurePitch(measurer);
            return (m, m * 0.78f);
        }

        private static float MeasurePitch(TextMeshPro t)
        {
            float one = t.GetPreferredValues("Ag", 10f, 0f).y, two = t.GetPreferredValues("Ag\nAg", 10f, 0f).y;
            float p = two - one;
            return p > 0.004f && p < 0.05f ? p : Mathf.Max(0.012f, one);
        }

        // TMP's <line-height=N> in "pixels": N x 0.1 local units for world-space text, so N = metres x 10
        private static string LineHeightTag(float pitch) => "<line-height=" + (pitch * 10f).ToString("0.######", CultureInfo.InvariantCulture) + ">";

        // ---------------------------------------------------------------- content

        /// New content. Keeps what the user looks at: at the bottom it follows; scrolled up, the line at the bottom of
        /// the view stays where it is and the lines that came below it count as new.
        public void SetBlocks(IEnumerable<ChatBlock> blocks)
        {
            var anchor = _scroll > 0 ? BottomAnchor() : null;
            int before = _lines.Count;
            _blocks.Clear();
            _blocks.AddRange(blocks);
            Layout();
            if (anchor != null) RestoreAnchor(anchor.Value, before);
            Redraw();
        }

        /// Forgets which blocks were opened or closed by hand (another thread).
        public void ResetState() { _toggled.Clear(); _scroll = 0; NewLines = 0; ClearLayoutCaches(); _grow.Clear(); }

        public bool IsExpanded(ChatBlock b) => b.Collapsible && (b.Expanded ^ _toggled.Contains(b.Key));

        /// Opens or closes a block as a tap would (self-tests, voice).
        public bool Toggle(string key)
        {
            int i = KeyIndex(key);
            if (i < 0 || !_blocks[i].Collapsible) return false;
            ToggleBlock(i);
            return true;
        }

        private void ToggleBlock(int bi)
        {
            var b = _blocks[bi];
            int head = FirstLine(bi);
            float fromBottom = head >= 0 ? DistanceFromBottom(head) : -1f;
            if (!_toggled.Remove(b.Key)) _toggled.Add(b.Key);
            Layout();
            // the header stays where it was tapped (opening pushes what follows down, not the header up); in the
            // lower half of a view that follows the end, it keeps following (the opened part shows)
            int nh = FirstLine(bi);
            if (nh >= 0 && fromBottom >= 0f && (_scroll > 0 || fromBottom > ViewHeight() * 0.5f) && ContentHeight() > ViewHeight()) KeepAt(nh, fromBottom);
            UiSounds.Fire("click");
            OnToggled?.Invoke(b, IsExpanded(b));
            Redraw();
        }

        public void ScrollBy(int lines) { _scroll = Mathf.Clamp(_scroll + lines, 0, MaxScroll()); if (_scroll == 0) NewLines = 0; Redraw(); }
        public void ScrollToBottom() { _scroll = 0; NewLines = 0; Redraw(); }

        /// Scrolls so the view starts `offset` metres below the top of the content (the scrollbar's thumb): whole lines,
        /// the bottom one where that offset puts the view's end. At the end it follows new content again.
        public void ScrollToOffset(float offset)
        {
            int n = _lines.Count;
            if (n == 0) return;
            float view = ViewHeight();
            int e = LowerBound(Mathf.Clamp(offset, 0f, Mathf.Max(0f, _contentH - view)) + view - 1e-4f);
            int s = Mathf.Clamp(n - e, 0, MaxScroll());
            if (s == _scroll) return;
            _scroll = s;
            if (_scroll == 0) NewLines = 0;
            Redraw();
        }

        /// One page up (-1) or down (+1): the view moves by most of its height (a line of the old view stays in it).
        public void Page(int dir)
        {
            int n = _lines.Count;
            if (n == 0) return;
            float view = ViewHeight();
            int end = n - Mathf.Clamp(_scroll, 0, MaxScroll());
            ScrollToOffset(_cum[end] + dir * view * 0.85f - view);
        }

        /// Scrolls so the block's first line is at the top of the view.
        public void ScrollToBlock(string key)
        {
            int li = FirstLine(KeyIndex(key));
            if (li < 0) return;
            // the last end of view that still shows the block's first line: then that line is the top one
            int best = Mathf.Clamp(UpperBound(_cum[li] + ViewHeight() + 1e-5f) - 1, li + 1, _lines.Count);
            _scroll = Mathf.Clamp(_lines.Count - best, 0, MaxScroll());
            if (_scroll == 0) NewLines = 0;
            Redraw();
        }

        // ---------------------------------------------------------------- self-tests

        /// Every line's text without tags, top to bottom (what a reader would see with an endless view).
        public string PlainText => string.Join("\n", _lines.Select(l => Strip(l.Text)));
        /// The lines on screen now.
        public string VisibleText => string.Join("\n", _drawn.Select(d => Strip(_lines[d.line].Text)));
        public string StickyText => _bandBlock >= 0 && _bandBlock < _blocks.Count ? Strip(_blocks[_bandBlock].Body) : "";
        public bool StickyShown => _bandBlock >= 0;
        public int Scroll => _scroll;
        /// The NOW strip's words ("" when it is hidden) and its running time.
        public string NowText => _nowShown ? Strip(_nowMainText) : "";
        public string NowTimeText => _nowShown && _nowTime != null ? _nowTime.Text : "";
        public bool NowShown => _nowShown;
        /// The height the NOW strip takes from the bottom of the chat (0 when hidden).
        public float FooterHeight => _nowShown ? NowH : 0f;
        /// How often the re-flow guard found a drawn text taller than its slots and made room (self-tests, logs).
        public int ReflowCount { get; private set; }
        public void ClickLineForTest(int line) { var p = new Vector2(Rect.x + 0.02f, YOfLine(line)); var e = new UiPointerEvent { Point = p, PressPoint = p }; OnPress(e); OnRelease(e, true); }
        public void ClickStickyForTest() { var p = _band.center; var e = new UiPointerEvent { Point = p, PressPoint = p }; OnPress(e); OnRelease(e, true); }
        public int FirstLineOf(string key) => FirstLine(KeyIndex(key));
        /// The top and bottom (panel metres) of a block's lines as laid out now, scrolled away or not (self-tests:
        /// blocks never overlap). (0, 0) when it is not shown.
        public (float top, float bottom) BlockSpanForTest(string key)
        {
            int bi = KeyIndex(key), a = FirstLine(bi), z = LastLine(bi);
            return a < 0 ? (0f, 0f) : (_cum[a] + _lines[a].PadTop, _cum[z + 1] - _lines[z].PadBottom);
        }
        private float YOfLine(int line) { foreach (var d in _drawn) if (d.line == line) return (d.y0 + d.y1) * 0.5f; return -1f; }
        private static string Strip(string s) => ChatText.Strip(s);

        // ---------------------------------------------------------------- layout

        private float ViewHeight() => Mathf.Max(0.01f, Rect.height - FooterHeight);
        private float ContentHeight() => _contentH;

        // ---- incremental layout (0.7.19: every timeline update laid out every block of the conversation again): each
        // block keeps the lines it was laid out as while what they are made of stays the same; only new and changed
        // blocks are wrapped again. Indexes per block (first / last line, by key) save the walks over all lines.

        private sealed class BlockLines
        {
            public ChatKind Kind;
            public string Head, Body, Preview, Parent, Tail;
            public bool Collapsible, Expanded, Live, MonoBody, MonoHead, Card, Diff, AlignRight;
            public int Indent, BodyMaxChars, TailLines, Pass;
            public float Pad, Wide;
            public UiTheme Theme;
            public readonly List<VLine> Lines = new List<VLine>();

            public bool Same(ChatBlock b, bool expanded, float pad, float wide, UiTheme theme) =>
                Kind == b.Kind && Expanded == expanded && Collapsible == b.Collapsible && Live == b.Live && MonoBody == b.MonoBody
                && MonoHead == b.MonoHead && Card == b.Card && Diff == b.Diff && AlignRight == b.AlignRight && TailLines == b.TailLines
                && Indent == b.Indent && BodyMaxChars == b.BodyMaxChars && Pad == pad && Wide == wide && ReferenceEquals(Theme, theme)
                && Parent == b.Parent && Head == b.Head && Preview == b.Preview && Body == b.Body && Tail == b.Tail;

            public void Take(ChatBlock b, bool expanded, float pad, float wide, UiTheme theme)
            {
                Kind = b.Kind; Expanded = expanded; Collapsible = b.Collapsible; Live = b.Live; MonoBody = b.MonoBody;
                MonoHead = b.MonoHead; Card = b.Card; Diff = b.Diff; AlignRight = b.AlignRight; TailLines = b.TailLines;
                Indent = b.Indent; BodyMaxChars = b.BodyMaxChars; Pad = pad; Wide = wide; Theme = theme;
                Parent = b.Parent; Head = b.Head; Preview = b.Preview; Body = b.Body; Tail = b.Tail;
            }
        }

        private readonly Dictionary<string, BlockLines> _blockLines = new Dictionary<string, BlockLines>();
        private readonly List<int> _firstLine = new List<int>(), _lastLine = new List<int>(); // per block; -1 = hidden
        private readonly Dictionary<string, int> _keyAt = new Dictionary<string, int>();     // a key's first block
        private readonly Dictionary<string, int> _promptAt = new Dictionary<string, int>();  // a prompt key's first block
        private readonly HashSet<string> _openWork = new HashSet<string>();
        private readonly List<string> _staleKeys = new List<string>();
        // the re-flow guard: extra room a drawn text needed under one of its lines (block key, part, index)
        private readonly Dictionary<(string key, byte part, int index), float> _grow = new Dictionary<(string, byte, int), float>();
        private readonly List<(string, byte, int)> _growDrop = new List<(string, byte, int)>();
        private float _contentH;
        private int _layoutPass;
        /// Blocks the last layout wrapped again (self-tests, logs): the others kept their lines.
        public int LastRelaid { get; private set; }

        private int KeyIndex(string key) => key != null && _keyAt.TryGetValue(key, out int i) ? i : -1;
        private int FirstLine(int bi) => bi >= 0 && bi < _firstLine.Count ? _firstLine[bi] : -1;
        private int LastLine(int bi) => bi >= 0 && bi < _lastLine.Count ? _lastLine[bi] : -1;

        private void ClearLayoutCaches() { _wrapCache.Clear(); _mdCache.Clear(); _blockLines.Clear(); }

        private void Layout()
        {
            _lines.Clear();
            _drawn.Clear(); // line numbers change: hit-testing waits for the next draw
            _checks.Clear();
            _runCache.Clear();
            _spinCache.Clear();
            _firstLine.Clear();
            _lastLine.Clear();
            _keyAt.Clear();
            _promptAt.Clear();
            _contentH = 0f;
            LastRelaid = 0;
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                var b = _blocks[bi];
                _firstLine.Add(-1);
                _lastLine.Add(-1);
                string k = b.Key ?? "";
                if (!_keyAt.ContainsKey(k)) _keyAt[k] = bi;
                if (b.Kind == ChatKind.Prompt && !_promptAt.ContainsKey(k)) _promptAt[k] = bi;
            }
            if (Panel == null || _mBody == null) { FillCum(); return; }
            var t = Theme;
            float wide = Wide;
            int pass = ++_layoutPass;
            string lastExchange = null;
            bool prevMeta = false;
            _openWork.Clear();
            foreach (var b in _blocks) if (b.Kind == ChatKind.Work && IsExpanded(b)) _openWork.Add(b.Key);
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                var b = _blocks[bi];
                string k = b.Key ?? "";
                _blockLines.TryGetValue(k, out var cached);
                if (cached != null) cached.Pass = pass; // still in the conversation (shown or inside a closed group)
                if (b.Parent != null && !_openWork.Contains(b.Parent)) continue;
                // the gaps: a new turn (your "You" line, or your prompt) well apart; your prompt close under its line;
                // the agent's name line, the answer and the steps a little apart
                float pad;
                if (_lines.Count == 0) pad = 0f;
                else if (b.Kind == ChatKind.Meta) pad = b.AlignRight ? (lastExchange != null ? TurnGap : 0f) : 0.010f;
                else if (b.Kind == ChatKind.Prompt) pad = prevMeta ? 0.0015f : lastExchange != null ? TurnGap : 0.0015f;
                else if (b.Kind == ChatKind.Answer) pad = prevMeta ? 0.004f : 0.008f;
                else pad = b.Parent != null ? 0.001f : prevMeta ? 0.003f : 0.005f;
                lastExchange = b.Exchange;
                prevMeta = b.Kind == ChatKind.Meta;
                bool expanded = IsExpanded(b);
                int from = _lines.Count;
                if (cached != null && cached.Same(b, expanded, pad, wide, t))
                {
                    foreach (var l in cached.Lines) { var c = l; c.Block = bi; _lines.Add(c); }
                }
                else
                {
                    if (_grow.Count > 0) DropGrows(k); // what it is made of changed: the guard looks again
                    LayoutBlock(bi, b, expanded, pad);
                    LastRelaid++;
                    if (cached == null) _blockLines[k] = cached = new BlockLines();
                    cached.Take(b, expanded, pad, wide, t);
                    cached.Pass = pass;
                    cached.Lines.Clear();
                    for (int i = from; i < _lines.Count; i++) cached.Lines.Add(_lines[i]);
                }
                if (_lines.Count > from) { _firstLine[bi] = from; _lastLine[bi] = _lines.Count - 1; }
            }
            if (_grow.Count > 0) ApplyGrows();
            FillCum();
            // blocks that left the conversation drop their lines
            _staleKeys.Clear();
            foreach (var kv in _blockLines) if (kv.Value.Pass != pass) _staleKeys.Add(kv.Key);
            foreach (var k in _staleKeys) _blockLines.Remove(k);
        }

        private void FillCum()
        {
            _cum.Clear();
            float acc = 0f;
            _cum.Add(0f);
            for (int i = 0; i < _lines.Count; i++) { acc += _lines[i].H; _cum.Add(acc); }
            _contentH = acc;
        }

        private void ApplyGrows()
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                var l = _lines[i];
                if (!_grow.TryGetValue((_blocks[l.Block].Key ?? "", l.Part, l.Index), out float g)) continue;
                l.H += g;
                l.PadBottom += g;
                _lines[i] = l;
            }
        }

        private void DropGrows(string key)
        {
            _growDrop.Clear();
            foreach (var kv in _grow) if (kv.Key.key == key) _growDrop.Add(kv.Key);
            foreach (var k in _growDrop) _grow.Remove(k);
        }

        /// One block's lines, appended to _lines (its header, preview or tail, body; markdown and wrapping are cached too).
        private void LayoutBlock(int bi, ChatBlock b, bool expanded, float pad)
        {
            var t = Theme;
            float x = b.Indent * IndentStep;
            int from = _lines.Count;
            switch (b.Kind)
            {
                case ChatKind.Meta:
                {
                    // "You · 14:05" over your bubble (its right edge), "◆ agent · 14:06" at the left
                    float w = b.AlignRight ? Wide - BubblePad : Wide;
                    _lines.Add(new VLine { Block = bi, Part = 0, Index = 0, Text = ChatText.OneLine(b.Head), Color = t.TextDim, Small = true, Cut = true, RightAlign = b.AlignRight, H = Pitch(false, true) + pad, PadTop = pad, X = 0f, W = w });
                    break;
                }
                case ChatKind.Prompt:
                {
                    float maxW = Wide * 0.80f - BubblePad * 2f;
                    var wrapped = Markdown(b.Body, t.Text, maxW, b.Key, b.BodyMaxChars);
                    float lw = 0.05f;
                    if (wrapped.Count > 0) { lw = 0f; foreach (var w in wrapped) lw = Mathf.Max(lw, w.w); }
                    float bx = Wide - lw - 0.004f - BubblePad * 2f;
                    AddLines(bi, 2, wrapped, bx + BubblePad, lw + 0.004f, pad + BubblePad, BubblePad, false, true);
                    break;
                }
                case ChatKind.Answer:
                case ChatKind.Update:
                {
                    float ax = x + (b.Kind == ChatKind.Answer ? BarX + 0.004f : IndentStep * 0.5f + 0.004f);
                    var baseCol = b.Kind == ChatKind.Answer ? t.Text : Color.Lerp(t.Text, t.TextDim, 0.45f);
                    var wrapped = Markdown(b.Body, baseCol, Wide - ax, b.Key, b.BodyMaxChars);
                    if (b.Live && wrapped.Count > 0) { var last = wrapped[wrapped.Count - 1]; wrapped[wrapped.Count - 1] = (last.text + "<color=#8FEBFF>▌</color>", last.w, last.mono, last.color, last.code); }
                    if (b.Kind == ChatKind.Update && wrapped.Count > 6 && !expanded) wrapped.RemoveRange(6, wrapped.Count - 6);
                    AddLines(bi, 2, wrapped, ax, Wide - ax, pad, 0f, false, false);
                    break;
                }
                default:
                {
                    // a header line, then (open) the body, or (closed) the output's last lines or a live preview line
                    float hx = x + (b.Card ? 0.010f : 0f);
                    string head = ChatText.OneLine(b.Head) + (b.Collapsible ? (expanded ? "  <color=#9DBDD2>▾</color>" : "  <color=#9DBDD2>▸</color>") : "");
                    var headCol = b.Kind == ChatKind.Error ? t.Danger : b.Kind == ChatKind.Notice ? t.TextDim : t.Text;
                    _lines.Add(new VLine { Block = bi, Part = 0, Index = 0, Text = head, Color = headCol, Mono = b.MonoHead, Cut = true, H = Pitch(b.MonoHead) + pad, PadTop = pad, X = hx, W = Wide - hx });
                    float bx = hx + IndentStep;
                    if (expanded && !string.IsNullOrEmpty(b.Body))
                    {
                        var col = b.Kind == ChatKind.Error ? Color.Lerp(t.Danger, t.Text, 0.4f) : b.Kind == ChatKind.Thinking ? Color.Lerp(t.TextDim, t.Violet, 0.25f) : Color.Lerp(t.TextDim, t.Text, 0.15f);
                        var wrapped = b.MonoBody ? Plain(b.Body, col, Wide - bx - 0.004f, b.Key + ":m", true, b.BodyMaxChars, b.Diff) : Markdown(b.Body, col, Wide - bx, b.Key, b.BodyMaxChars);
                        if (b.MonoBody) for (int i = 0; i < wrapped.Count; i++) { var w = wrapped[i]; wrapped[i] = (w.text, w.w, w.mono, w.color, true); } // the output on a dark panel
                        AddLines(bi, 2, wrapped, bx, Wide - bx, 0.002f, 0.002f, b.MonoBody, false);
                    }
                    else if (!expanded && !string.IsNullOrEmpty(b.Tail))
                    {
                        // the last lines of the output (a command): live while it runs; a tap shows all of it
                        var col = Color.Lerp(t.TextDim, t.Text, 0.1f);
                        var wrapped = Plain(b.Tail, col, Wide - bx - 0.004f, b.Key + ":t", true, 1600, false);
                        if (wrapped.Count > b.TailLines) wrapped.RemoveRange(0, wrapped.Count - Mathf.Max(1, b.TailLines));
                        for (int i = 0; i < wrapped.Count; i++) { var w = wrapped[i]; wrapped[i] = (w.text, w.w, true, w.color, true); }
                        AddLines(bi, 1, wrapped, bx, Wide - bx, 0.0025f, 0.002f, true, false);
                    }
                    if (!expanded && !string.IsNullOrEmpty(b.Preview))
                        _lines.Add(new VLine { Block = bi, Part = 1, Index = 1000, Text = "<i>" + Esc(ChatText.OneLine(b.Preview)) + "</i>", Color = t.TextDim, Cut = true, H = _pitchBody, PadTop = 0f, X = bx, W = Wide - bx });
                    break;
                }
            }
            // a card (an approval or a question waiting for you, an error): room inside its tinted box
            if (b.Card && _lines.Count > from)
            {
                var f = _lines[from]; f.PadTop += CardPad; f.H += CardPad; _lines[from] = f;
                var z = _lines[_lines.Count - 1]; z.PadBottom += CardPad; z.H += CardPad; _lines[_lines.Count - 1] = z;
            }
        }

        private void AddLines(int bi, byte part, List<(string text, float w, bool mono, Color color, bool code)> wrapped, float x, float w, float padTop, float padBottom, bool mono, bool right)
        {
            for (int i = 0; i < wrapped.Count; i++)
            {
                var l = wrapped[i];
                bool m = mono || l.mono;
                float pt = i == 0 ? padTop : 0f, pb = i == wrapped.Count - 1 ? padBottom : 0f;
                _lines.Add(new VLine { Block = bi, Part = part, Index = i, Text = l.text, Mono = m, Code = l.code, RightAlign = right, Color = l.color, H = Pitch(m) + pt + pb, PadTop = pt, PadBottom = pb, X = x, W = w });
            }
            if (wrapped.Count == 0)
                _lines.Add(new VLine { Block = bi, Part = part, Index = 0, Text = "", Color = Theme.Text, H = Pitch(mono) + padTop + padBottom, PadTop = padTop, PadBottom = padBottom, X = x, W = w, RightAlign = right });
        }

        // ---- text: markdown-lite and wrapping

        /// Markdown-lite for agent text: headings, bullets, numbers, quotes, rules, code blocks and tables in the
        /// terminal font; **bold**, `code` and [links](url) coloured (colour only: widths stay as measured).
        private readonly Dictionary<string, (string src, float w, Color c, List<(string, float, bool, Color, bool)> lines)> _mdCache = new Dictionary<string, (string, float, Color, List<(string, float, bool, Color, bool)>)>();

        private List<(string text, float w, bool mono, Color color, bool code)> Markdown(string src, Color baseColor, float width, string key, int maxChars)
        {
            if (_mdCache.TryGetValue(key, out var mc) && mc.src == src && Mathf.Abs(mc.w - width) < 1e-4f && mc.c == baseColor) return new List<(string, float, bool, Color, bool)>(mc.lines);
            var res = MarkdownLines(src, baseColor, width, key, maxChars);
            _mdCache[key] = (src, width, baseColor, res);
            return new List<(string, float, bool, Color, bool)>(res);
        }

        private List<(string text, float w, bool mono, Color color, bool code)> MarkdownLines(string src, Color baseColor, float width, string key, int maxChars)
        {
            var t = Theme;
            var paras = ChatText.Paragraphs(src, maxChars);
            string bold = ColorUtility.ToHtmlStringRGB(Color.white), code = ColorUtility.ToHtmlStringRGB(Color.Lerp(t.Warn, Color.white, 0.35f)), link = ColorUtility.ToHtmlStringRGB(t.Accent);
            string Hex(SpanKind k) => k == SpanKind.Code ? code : k == SpanKind.Link ? link : bold;
            Color ColorOf(ChatPara p) => p.Style == ParaStyle.Heading ? t.TextAccent : p.Style == ParaStyle.Quote || p.Style == ParaStyle.Rule ? t.TextDim
                : p.Style == ParaStyle.Code ? Color.Lerp(t.Text, t.TextAccent, 0.3f) : baseColor;
            // wrap: runs of paragraphs in one font at a time (one layout per run)
            var outl = new List<(string, float, bool, Color, bool)>();
            int i0 = 0;
            while (i0 < paras.Count)
            {
                bool mono = paras[i0].Mono;
                int i1 = i0;
                while (i1 < paras.Count && paras[i1].Mono == mono) i1++;
                var run = paras.GetRange(i0, i1 - i0);
                string joined = ChatText.Join(run);
                var starts = ChatText.Starts(run);
                int cursor = 0;
                // code blocks wrap 4 mm narrower: their dark panel reaches past the text on both sides
                foreach (var (lineText, lw, para) in Wrap(joined, mono ? width - 0.004f : width, mono, key + ":" + i0))
                {
                    var p = run[para];
                    outl.Add((ChatText.Styled(lineText, p.Spans, ChatText.LineStart(joined, lineText, starts[para], ref cursor), Hex), lw, mono, ColorOf(p), p.Style == ParaStyle.Code));
                }
                i0 = i1;
            }
            return outl;
        }

        private static readonly string DiffAdd = "<color=#4DFFA6>", DiffDel = "<color=#FF7A88>", DiffHunk = "<color=#7FA9BF>", Endc = "</color>";

        private List<(string text, float w, bool mono, Color color, bool code)> Plain(string src, Color color, float width, string key, bool mono, int maxChars, bool diff)
        {
            src = ChatText.Sanitize(src ?? "").TrimEnd();
            if (src.Length > maxChars)
            {
                // output: its end matters most; cut at a line start
                int cut = src.IndexOf('\n', src.Length - maxChars);
                src = "… " + (cut >= 0 && cut < src.Length - 1 ? src.Substring(cut + 1) : src.Substring(src.Length - maxChars));
            }
            var wrapped = Wrap(src, width, mono, key);
            var res = new List<(string, float, bool, Color, bool)>(wrapped.Count);
            string[] srcLines = diff ? src.Split('\n') : null;
            foreach (var l in wrapped)
            {
                string text = Esc(l.text);
                if (diff && l.para < srcLines.Length && text.Length > 0)
                {
                    string s = srcLines[l.para];
                    string open = s.StartsWith("+++") || s.StartsWith("---") ? DiffHunk : s.StartsWith("+") ? DiffAdd : s.StartsWith("-") ? DiffDel : s.StartsWith("@@") ? DiffHunk : null;
                    if (open != null) text = open + text + Endc;
                }
                res.Add((text, l.w, mono, color, false));
            }
            return res;
        }

        /// TextMeshPro's own line breaks for `text` at `width` (cached by key while text and width stay the same).
        private List<(string text, float w, int para)> Wrap(string text, float width, bool mono, string key)
        {
            if (_wrapCache.TryGetValue(key, out var c) && c.src == text && Mathf.Abs(c.w - width) < 1e-4f && c.mono == mono) return c.lines;
            var res = new List<(string, float, int)>();
            var tmp = mono ? _mMono : _mBody;
            if (tmp == null || string.IsNullOrEmpty(text)) { _wrapCache[key] = (text, width, mono, res); return res; }
            tmp.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.02f, width), 100f);
            var info = tmp.GetTextInfo(text);
            var ranges = new List<(int, int)>();
            var widths = new List<float>();
            for (int i = 0; i < info.lineCount; i++)
            {
                var li = info.lineInfo[i];
                if (li.characterCount <= 0) continue;
                var first = info.characterInfo[li.firstCharacterIndex];
                var last = info.characterInfo[li.lastCharacterIndex];
                ranges.Add((first.index, last.index + Mathf.Max(1, last.stringLength)));
                widths.Add(Mathf.Max(0f, li.lineExtents.max.x - li.lineExtents.min.x));
            }
            var lines = ChatText.LinesFromRanges(text, ranges);
            for (int i = 0; i < lines.Count; i++) res.Add((lines[i].text, widths[i], lines[i].para));
            _wrapCache[key] = (text, width, mono, res);
            return res;
        }

        public static string Esc(string s) => ChatText.Esc(s);

        // ---------------------------------------------------------------- scrolling (on the line tops: _cum)

        // the first index i with _cum[i] >= x (Count + 1 when none)
        private int LowerBound(float x)
        {
            int lo = 0, hi = _cum.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (_cum[mid] < x) lo = mid + 1; else hi = mid; }
            return lo;
        }

        // the first index i with _cum[i] > x
        private int UpperBound(float x)
        {
            int lo = 0, hi = _cum.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (_cum[mid] <= x) lo = mid + 1; else hi = mid; }
            return lo;
        }

        // the first line shown when the view ends at line `end` (bottom-aligned)
        private int FillStart(int end)
        {
            if (end <= 0) return 0;
            return Mathf.Clamp(LowerBound(_cum[end] - ViewHeight() - 1e-6f), 0, end);
        }

        private int MaxScroll()
        {
            int n = _lines.Count;
            if (n == 0 || _contentH <= ViewHeight() + 1e-6f) return 0;
            int k = UpperBound(ViewHeight() + 1e-6f) - 1; // lines from the top that fit
            return Mathf.Max(0, n - k);
        }

        private float DistanceFromBottom(int line)
        {
            int end = _lines.Count - _scroll;
            return end > line ? _cum[end] - _cum[line] : 0f;
        }

        private void KeepAt(int line, float fromBottom)
        {
            int end = Mathf.Clamp(UpperBound(_cum[line] + fromBottom + 0.0005f) - 1, line, _lines.Count);
            _scroll = Mathf.Clamp(_lines.Count - end, 0, MaxScroll());
        }

        private (string key, byte part, int index, float fromBottom)? BottomAnchor()
        {
            int end = _lines.Count - _scroll;
            if (end <= 0 || end > _lines.Count) return null;
            var l = _lines[end - 1];
            return (_blocks[l.Block].Key, l.Part, l.Index, l.H);
        }

        private void RestoreAnchor((string key, byte part, int index, float fromBottom) a, int linesBefore)
        {
            int bi = KeyIndex(a.key), li = -1;
            for (int i = FirstLine(bi), last = LastLine(bi); i >= 0 && i <= last; i++)
                if (_lines[i].Part == a.part && _lines[i].Index == a.index) { li = i; break; }
            if (li < 0) { _scroll = Mathf.Clamp(_scroll + _lines.Count - linesBefore, 0, MaxScroll()); return; }
            int newScroll = Mathf.Clamp(_lines.Count - (li + 1), 0, MaxScroll());
            NewLines += Mathf.Max(0, newScroll - _scroll);
            _scroll = newScroll;
        }

        // ---------------------------------------------------------------- the NOW strip

        // what the agent does this second (owner 2026-11-17: "hard to see what is running now"): a tinted strip at the
        // bottom of the chat while it works, gone when idle. Its spinner and running time are labels of their own,
        // set 4 times a second only while it shows: the chat is not drawn again for them.
        private UiText _nowSpin, _nowMain, _nowTime;
        private bool _nowShown;
        private string _nowLabel, _nowDetail, _nowMainText = "";
        private long _nowSince;
        private int _nowSecs = -1;
        private Color _nowTint;
        private Rect _nowRect;
        private float NowH => _pitchBody * 1.5f + 0.008f;

        private void BuildNow()
        {
            _nowSpin = UiText.Create(Panel, new Rect(0, 0, 0.02f, 0.02f), "", Theme.Body, TextSize, Theme.TextAccent, TextAlignmentOptions.BaselineLeft);
            _nowMain = UiText.Create(Panel, new Rect(0, 0, 0.1f, 0.02f), "", Theme.Body, TextSize, Theme.Text, TextAlignmentOptions.BaselineLeft);
            _nowTime = UiText.Create(Panel, new Rect(0, 0, 0.05f, 0.02f), "", Theme.Mono ?? Theme.Body, TextSize * 0.95f, Theme.TextDim, TextAlignmentOptions.BaselineRight);
            foreach (var t in new[] { _nowSpin, _nowMain, _nowTime }) { t.Tmp.parseCtrlCharacters = false; t.Tmp.overflowMode = TextOverflowModes.Ellipsis; }
            ShowNowTexts(false);
        }

        private void ShowNowTexts(bool v)
        {
            _nowSpin?.SetVisible(v);
            _nowMain?.SetVisible(v);
            _nowTime?.SetVisible(v);
        }

        /// The strip shows `label` (RUNNING, THINKING, EDITING ...) in `tint` and `detail` (the command, the file),
        /// with a spinner and the time since `sinceMs`. Called once a second or so: nothing changes when the words
        /// are the same; the chat is drawn again only when the strip comes or its colour changes.
        public void SetNow(string label, string detail, long sinceMs, Color tint)
        {
            label = label ?? ""; detail = detail ?? "";
            if (_nowShown && label == _nowLabel && detail == _nowDetail && sinceMs == _nowSince && tint == _nowTint) return;
            bool redraw = !_nowShown || tint != _nowTint;
            if (label != _nowLabel || detail != _nowDetail || tint != _nowTint)
                _nowMainText = "<color=#" + ColorUtility.ToHtmlStringRGB(tint) + ">" + Esc(label) + "</color>" + (detail.Length > 0 ? "   " + Esc(ChatText.OneLine(detail)) : "");
            _nowLabel = label; _nowDetail = detail; _nowSince = sinceMs; _nowTint = tint;
            _nowShown = true;
            _nowSecs = -1;
            if (_nowMain != null) _nowMain.Text = _nowMainText;
            if (redraw) Redraw();
            else TickNow();
        }

        /// The agent is idle: the strip goes.
        public void ClearNow()
        {
            if (!_nowShown) return;
            _nowShown = false;
            _nowLabel = _nowDetail = null;
            ShowNowTexts(false);
            Redraw();
        }

        private long Now() => NowMs != null ? NowMs() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void TickNow()
        {
            if (!_nowShown || _nowSpin == null) return;
            _nowSpin.Text = Spinner[_spin];
            int secs = _nowSince > 0 ? (int)Math.Max(0, (Now() - _nowSince) / 1000) : -2;
            if (secs == _nowSecs) return;
            _nowSecs = secs;
            _nowTime.Text = secs >= 0 ? Clock(secs) : "";
        }

        private void DrawNow(float view)
        {
            if (!_nowShown || !Visible || _nowSpin == null) { ShowNowTexts(false); _nowRect = default; return; }
            var r = _nowRect = new Rect(Rect.x, Rect.y + view + 0.004f, Wide, NowH - 0.004f);
            var c = _nowTint;
            Box(r, new Color(c.r, c.g, c.b, 0.15f), 0.005f, 1, 0f, 0.001f);
            Pill(new Rect(r.x + 0.004f, r.y + 0.004f, 0.003f, r.height - 0.008f), c);
            float baseline = r.y + (r.height - _pitchBody) * 0.5f + _ascBody, R = r.height + _pitchBody;
            float tw = 0.05f;
            _nowSpin.SetRect(new Rect(r.x + 0.012f, baseline - R, 0.02f, R * 2f));
            _nowSpin.Color = c;
            _nowMain.SetRect(new Rect(r.x + 0.032f, baseline - R, r.width - 0.032f - tw - 0.008f, R * 2f));
            _nowTime.SetRect(new Rect(r.xMax - tw - 0.006f, baseline - R, tw, R * 2f));
            _nowMain.Text = _nowMainText;
            ShowNowTexts(true);
            _nowSecs = -1;
            TickNow();
        }

        private static string Clock(int secs) => secs >= 3600 ? $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00}";

        // ---------------------------------------------------------------- drawing

        // ---- a block that just finished glows for a moment (the answer of a thread that is done)

        private string _flashKey;
        private float _flashUntil, _flashLength = 2.4f, _nextFlashDraw;
        public string FlashingKey => _flashKey;

        /// The block's bar and background pulse three times over `seconds`.
        public void Flash(string key, float seconds = 2.4f)
        {
            _flashKey = key;
            _flashLength = Mathf.Max(0.3f, seconds);
            _flashUntil = Time.unscaledTime + _flashLength;
            Redraw();
        }

        private float FlashLevel(string key)
        {
            if (_flashKey == null || key != _flashKey) return 0f;
            float left = _flashUntil - Time.unscaledTime;
            if (left <= 0f) return 0f;
            float u = 1f - left / _flashLength;
            return 0.5f - 0.5f * Mathf.Cos(u * Mathf.PI * 6f); // three pulses
        }

        private bool _liveShown;   // the last draw showed a live header (a spinner, a running time)

        /// Per frame: nothing unless something moves on screen (a flash, a live header, the NOW strip) or a drawn text
        /// waits for its check: a few flags.
        public override void Update()
        {
            if (_verifyDue) VerifyDrawn();
            if (_flashKey != null)
            {
                if (Time.unscaledTime >= _flashUntil) { _flashKey = null; Redraw(); }
                else if (Time.unscaledTime >= _nextFlashDraw) { _nextFlashDraw = Time.unscaledTime + 0.05f; Redraw(); }
            }
            if (!_liveShown && !_nowShown) return;
            // spinners and running times: 4 Hz, only while one is on screen
            if (Time.unscaledTime < _nextSpin) return;
            _nextSpin = Time.unscaledTime + 0.25f;
            _spin = (_spin + 1) % 4;
            TickNow();
            if (_liveShown) Redraw();
        }

        private static readonly string[] Spinner = { "◐", "◓", "◑", "◒" };

        // ---- the re-flow guard: each drawn text against the lines laid out for it
        private struct RunCheck { public UiText Text; public int Last, Lines; public float Room, Pitch; }
        private readonly List<RunCheck> _checks = new List<RunCheck>();
        private bool _verifyDue;
        private int _verifyTries;

        /// Once after a draw, when TextMeshPro has built the meshes: a text that came out with more lines than were laid
        /// out for it (a break the measuring did not see) gets room under its last line and the chat flows again, so
        /// it never runs into the block under it. Reads only the line counts of what is on screen.
        private void VerifyDrawn()
        {
            bool pending = false, grew = false;
            for (int c = 0; c < _checks.Count; c++)
            {
                var rc = _checks[c];
                var tmp = rc.Text?.Tmp;
                if (tmp == null || !tmp.gameObject.activeInHierarchy) continue;
                if (tmp.havePropertiesChanged) { pending = true; continue; }
                var ti = tmp.textInfo;
                if (ti == null) continue;
                int n = ti.lineCount;
                if (n <= rc.Lines || rc.Last < 0 || rc.Last >= _lines.Count) continue;
                float extra = n * rc.Pitch - rc.Room;
                if (extra <= 0.0005f) continue;
                var l = _lines[rc.Last];
                var key = (_blocks[l.Block].Key ?? "", l.Part, l.Index);
                _grow.TryGetValue(key, out float g);
                _grow[key] = g + extra;
                grew = true;
                ReflowCount++;
                Log?.Invoke($"chat re-flow: block {key.Item1} drew {n} lines where {rc.Lines} were laid out: +{extra * 1000f:F0} mm under it");
            }
            if (pending && ++_verifyTries < 6) return; // its mesh is built at the end of this frame: look again then
            _verifyDue = false;
            if (grew) { Layout(); Redraw(); }
        }

        // ---- run texts: the joined lines of a run, made once per layout (a draw comes 4 times a second while a header spins)
        private readonly Dictionary<long, string> _runCache = new Dictionary<long, string>();
        private readonly Dictionary<int, string[]> _spinCache = new Dictionary<int, string[]>();
        private readonly Dictionary<string, (string src, int secs, string text)> _liveRight = new Dictionary<string, (string, int, string)>();

        private string RunText(int i, int j)
        {
            long key = ((long)i << 32) | (uint)j;
            if (_runCache.TryGetValue(key, out var s)) return s;
            var l0 = _lines[i];
            _drawSb.Clear();
            // a fixed line height: a glyph from a taller fallback font cannot push the lines under it down
            _drawSb.Append(l0.Mono ? _lhMono : l0.Small ? _lhSmall : _lhBody);
            for (int k = i; k < j; k++) { if (k > i) _drawSb.Append('\n'); _drawSb.Append(_lines[k].Text); }
            s = _drawSb.ToString();
            _runCache[key] = s;
            return s;
        }

        private string LineText(int k)
        {
            var l = _lines[k];
            if (l.Part == 0 && _blocks[l.Block].Live && l.Text.Length > 0 && l.Text[0] == '◌')
            {
                if (!_spinCache.TryGetValue(k, out var v))
                {
                    v = new string[4];
                    for (int s = 0; s < 4; s++) v[s] = Spinner[s] + l.Text.Substring(1);
                    _spinCache[k] = v;
                }
                return v[_spin];
            }
            return l.Text;
        }

        /// A header's right text; a live step adds its running time (made again only when the second changes).
        private string RightText(ChatBlock b)
        {
            if (!(b.Live && b.LiveSince > 0)) return b.Right ?? "";
            int secs = (int)Math.Max(0, (Now() - b.LiveSince) / 1000);
            string k = b.Key ?? "";
            if (_liveRight.TryGetValue(k, out var c) && ReferenceEquals(c.src, b.Right) && c.secs == secs) return c.text;
            string text = (b.Right ?? "") + " <color=#AACADD>" + Clock(secs) + "</color>";
            if (_liveRight.Count > 32) _liveRight.Clear();
            _liveRight[k] = (b.Right, secs, text);
            return text;
        }

        // characters a reader sees (tags skipped): the width of a right text, no allocation
        private static int VisibleLength(string s)
        {
            int n = 0;
            bool tag = false;
            foreach (char ch in s) { if (ch == '<') tag = true; else if (tag) { if (ch == '>') tag = false; } else n++; }
            return n;
        }

        private static bool SameRun(in VLine prev, in VLine cur, in VLine first) =>
            cur.Block == first.Block && cur.Part == first.Part && !first.Cut && !cur.Cut && cur.Mono == first.Mono && cur.Small == first.Small
            && cur.Color == first.Color && cur.PadTop == 0f && prev.PadBottom == 0f && Mathf.Abs(cur.X - first.X) < 1e-4f
            && cur.Code == first.Code && cur.RightAlign == first.RightAlign;

        protected override void Draw()
        {
            var t = Theme;
            _drawn.Clear();
            _checks.Clear();
            _band = default;
            _bandBlock = -1;
            _liveShown = false;
            float view = ViewHeight();
            DrawNow(view);
            if (_lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(Empty))
                {
                    var e = Txt(new Rect(Rect.x, Rect.y, Rect.width, view), Empty, TextSize, t.TextDim, TextAlignmentOptions.Center, null, true);
                    e.Tmp.overflowMode = TextOverflowModes.Ellipsis;
                    e.Tmp.parseCtrlCharacters = false;
                }
                Scrollbar?.SetState(view, 0f, 0f, FooterHeight);
                return;
            }
            _scroll = Mathf.Clamp(_scroll, 0, MaxScroll());
            int end = _lines.Count - _scroll, start = FillStart(end);
            float used = _cum[end] - _cum[start];
            // the sticky prompt: the exchange of the top line, when its prompt scrolled away above
            if (start > 0)
            {
                string ex = _blocks[_lines[start].Block].Exchange;
                int pb = ex != null && _promptAt.TryGetValue(ex, out int at) ? at : -1;
                int pl = FirstLine(pb);
                int plEnd = pl >= 0 ? LastLine(pb) : -1;
                if (pl >= 0 && plEnd < start)
                {
                    float bandH = _pitchBody * 2f + 0.010f;
                    int s2 = start;
                    float u2 = used;
                    while (s2 < end && u2 > view - bandH) { u2 -= _lines[s2].H; s2++; }
                    // never push a prompt's first line out of view: near the end (a tap on the pinned prompt scrolls
                    // there, and the last exchange is shorter than the view) the next prompt is on screen already
                    bool hidesPrompt = false;
                    for (int k = start; k < s2 && !hidesPrompt; k++)
                        hidesPrompt = _blocks[_lines[k].Block].Kind == ChatKind.Prompt && FirstLine(_lines[k].Block) == k;
                    if (!hidesPrompt)
                    {
                        start = s2;
                        used = u2;
                        _band = new Rect(Rect.x, Rect.y, Wide - 0.004f, bandH - 0.004f);
                        _bandBlock = pb;
                    }
                }
            }
            bool overflow = start > 0 || _scroll > 0;
            float yTop = overflow ? Rect.y + view - used : Rect.y;
            DrawBackgrounds(start, end, yTop);
            DrawTexts(start, end, yTop);
            // the sticky prompt on top
            if (_bandBlock >= 0)
            {
                var c = _blocks[_bandBlock].Tint ?? t.Accent;
                Box(_band, Color.Lerp(new Color(0.02f, 0.06f, 0.1f, 0.97f), c, 0.16f), 0.006f, 1, 0f, 0.001f);
                Pill(new Rect(_band.x + 0.004f, _band.y + 0.004f, 0.0026f, _band.height - 0.008f), c);
                var bt = Txt(new Rect(_band.x + 0.012f, _band.y + 0.003f, _band.width - 0.05f, _band.height - 0.004f), BandText(_blocks[_bandBlock].Body), TextSize, t.Text, TextAlignmentOptions.TopLeft, Theme.Body, true);
                bt.Tmp.overflowMode = TextOverflowModes.Ellipsis;
                bt.Tmp.parseCtrlCharacters = false;
                Txt(new Rect(_band.xMax - 0.04f, _band.y, 0.036f, _band.height), "▲", TextSize, t.TextDim, TextAlignmentOptions.Center).Tmp.overflowMode = TextOverflowModes.Ellipsis;
            }
            Scrollbar?.SetState(view, _contentH, Mathf.Max(0f, _cum[end] - view), FooterHeight);
            _verifyDue = _checks.Count > 0;
            _verifyTries = 0;
        }

        /// Bubbles, the answer's bar, cards, the dark panel of code and output, the hover light, the turn divider.
        private void DrawBackgrounds(int start, int end, float y)
        {
            var t = Theme;
            int i = start;
            while (i < end)
            {
                int bi = _lines[i].Block;
                int j = i;
                float y0 = y, h = 0f;
                while (j < end && _lines[j].Block == bi) { h += _lines[j].H; j++; }
                var b = _blocks[bi];
                bool firstSeg = i == FirstLine(bi), lastSeg = j - 1 == LastLine(bi);
                float top = y0 + _lines[i].PadTop;
                if (b.Kind == ChatKind.Prompt)
                {
                    float lw = 0f;
                    for (int k = i; k < j; k++) lw = Mathf.Max(lw, _lines[k].W);
                    var r = new Rect(Rect.x + _lines[i].X - BubblePad, top - (firstSeg ? BubblePad : 0f), lw + BubblePad * 2f, y0 + h - top + (firstSeg ? BubblePad : 0f));
                    var c = b.Tint ?? t.Accent;
                    Box(r, new Color(c.r, c.g, c.b, 0.22f), 0.008f, 1, 0f, 0.001f);
                }
                else if (b.Kind == ChatKind.Answer)
                {
                    var c = b.Tint ?? t.Ok;
                    float f = FlashLevel(b.Key);
                    if (f > 0.01f) Box(new Rect(Rect.x + _lines[i].X - 0.012f, top - 0.002f, Wide - _lines[i].X + 0.012f, y0 + h - top + 0.002f), new Color(c.r, c.g, c.b, 0.16f * f), 0.004f, 1, 0f, 0.001f);
                    Pill(new Rect(Rect.x + _lines[i].X - 0.009f - 0.0015f * f, top + 0.002f, 0.0026f + 0.003f * f, Mathf.Max(0.004f, y0 + h - top - 0.004f)), new Color(c.r, c.g, c.b, b.Live ? 0.95f : Mathf.Lerp(0.6f, 1f, f)));
                }
                else if (b.Kind == ChatKind.Meta && b.AlignRight && firstSeg && _lines[i].PadTop >= TurnGap - 1e-4f)
                {
                    // a faint rule between turns
                    float ry = y0 + _lines[i].PadTop * 0.45f;
                    Hair(new Vector2(Rect.x + 0.01f, ry), new Vector2(Rect.x + Wide - 0.01f, ry), new Color(t.Frame.r, t.Frame.g, t.Frame.b, 0.5f));
                }
                if (b.Card)
                {
                    var c = b.Tint ?? t.Warn;
                    float cx = Rect.x + b.Indent * IndentStep;
                    float cTop = firstSeg ? top - CardPad : y0, cBot = y0 + h - (lastSeg ? _lines[j - 1].PadBottom - CardPad : 0f);
                    var r = new Rect(cx, cTop, Wide - b.Indent * IndentStep, Mathf.Max(0.004f, cBot - cTop));
                    Box(r, new Color(c.r, c.g, c.b, b.Live ? 0.20f : 0.12f), 0.005f, 1, b.Live ? 0.5f : 0f, 0.001f);
                    Pill(new Rect(cx + 0.003f, r.y + 0.003f, 0.003f, Mathf.Max(0.004f, r.height - 0.006f)), new Color(c.r, c.g, c.b, 0.95f));
                }
                // the dark panel under code blocks and command output: one box per run of code lines
                float yy = y0;
                int kk = i;
                while (kk < j)
                {
                    var l = _lines[kk];
                    if (l.Code)
                    {
                        float c0 = yy + l.PadTop, c1 = yy, maxW = 0f;
                        int k2 = kk;
                        while (k2 < j && _lines[k2].Code && Mathf.Abs(_lines[k2].X - l.X) < 1e-4f && (k2 == kk || _lines[k2].PadTop == 0f))
                        {
                            maxW = Mathf.Max(maxW, _lines[k2].W);
                            yy += _lines[k2].H;
                            c1 = yy - _lines[k2].PadBottom;
                            k2++;
                        }
                        Box(new Rect(Rect.x + l.X - 0.004f, c0 - 0.0012f, maxW + 0.006f, c1 - c0 + 0.0024f), new Color(0f, 0f, 0f, 0.30f), 0.002f, 1, 0f, 0.0004f);
                        kk = k2;
                        continue;
                    }
                    if (kk == _hoverLine && l.Part == 0 && b.Collapsible) Box(new Rect(Rect.x + l.X - 0.004f, yy + l.PadTop, Wide - l.X + 0.004f, l.H - l.PadTop - l.PadBottom), t.Accent, 0.003f, 2, 0.4f, 0.001f);
                    yy += l.H;
                    kk++;
                }
                y += h;
                i = j;
            }
        }

        /// One TextMeshPro per run of lines of one block part, font and colour, placed by its first baseline with a
        /// fixed line height: it draws exactly in the slots the layout gave its lines.
        private void DrawTexts(int start, int end, float y)
        {
            var t = Theme;
            int i = start;
            while (i < end)
            {
                var l0 = _lines[i];
                int j = i + 1;
                float h = l0.H;
                while (j < end && SameRun(_lines[j - 1], _lines[j], l0)) { h += _lines[j].H; j++; }
                var b = _blocks[l0.Block];
                float pitch = Pitch(l0.Mono, l0.Small), top = y + l0.PadTop, baseline = top + Asc(l0.Mono, l0.Small);
                float textH = h - l0.PadTop - _lines[j - 1].PadBottom, R = textH + pitch * 2f;
                float x = Rect.x + l0.X, w = l0.W;
                if (l0.Part == 0 && (!string.IsNullOrEmpty(b.Right) || b.Live && b.LiveSince > 0))
                {
                    string right = RightText(b);
                    float size = TextSize * 0.92f, tw = VisibleLength(right) * size * 0.46f;
                    float rw = Mathf.Min(w * 0.45f, tw + 0.010f), rx = Rect.x + Wide - rw;
                    if (b.RightTint.HasValue)
                    {
                        var c = b.RightTint.Value;
                        float cw = Mathf.Min(rw, tw + 0.008f);
                        Box(new Rect(Rect.x + Wide - cw - 0.002f, top + 0.0012f, cw + 0.002f, pitch - 0.0024f), new Color(c.r, c.g, c.b, 0.20f), 0.004f, 1, 0f, 0.0006f);
                    }
                    var rt = Txt(new Rect(rx, baseline - R, rw - 0.004f, R * 2f), right, size, t.TextDim, TextAlignmentOptions.BaselineRight, Theme.Body);
                    rt.Tmp.overflowMode = TextOverflowModes.Ellipsis;
                    rt.Tmp.parseCtrlCharacters = false;
                    w = Mathf.Min(w, rx - x - 0.006f);
                    if (b.Live && b.LiveSince > 0) _liveShown = true;
                }
                if (l0.Part == 0 && b.Live && l0.Text.Length > 0 && l0.Text[0] == '◌') _liveShown = true;
                string text = j == i + 1 ? LineText(i) : RunText(i, j);
                // wrapped lines already fit: Overflow (Ellipsis would drop every line after one that is a hair too long);
                // headers and other single lines are cut with …
                if (!l0.Cut) w += 0.006f;
                var align = l0.RightAlign && l0.Part == 0 ? TextAlignmentOptions.BaselineRight : TextAlignmentOptions.BaselineLeft;
                var ut = Txt(new Rect(x, baseline - R, Mathf.Max(0.01f, w), R * 2f), text, l0.Small ? TextSize * SmallK : TextSize, l0.Color, align, l0.Mono ? Theme.Mono : Theme.Body);
                ut.Tmp.overflowMode = l0.Cut ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
                ut.Tmp.parseCtrlCharacters = false;
                _checks.Add(new RunCheck { Text = ut, Last = j - 1, Lines = j - i, Room = h - l0.PadTop, Pitch = pitch });
                float yy = y;
                for (int k = i; k < j; k++) { _drawn.Add((k, yy + _lines[k].PadTop, yy + _lines[k].H)); yy += _lines[k].H; }
                y += h;
                i = j;
            }
        }

        private readonly StringBuilder _drawSb = new StringBuilder();
        private string _bandSrc, _bandText;

        // the sticky prompt's line, made again only when the prompt changes (a draw comes 4 times a second while a header spins)
        private string BandText(string body)
        {
            if (_bandText == null || body != _bandSrc) { _bandSrc = body; _bandText = "<color=#8FEBFF>▶ YOU</color>  " + Esc(FlatPrompt(body)); }
            return _bandText;
        }

        private static string FlatPrompt(string s)
        {
            s = Regex.Replace(ChatText.Sanitize(s ?? ""), @"\s+", " ").Trim();
            return s.Length > 170 ? s.Substring(0, 169) + "…" : s;
        }

        // ---------------------------------------------------------------- input

        private int LineAt(Vector2 p)
        {
            foreach (var d in _drawn) if (p.y >= d.y0 - 0.001f && p.y < d.y1 + 0.001f) return d.line;
            return -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragStart = _scroll; _dragged = false; }

        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - e.PressPoint.y;
            if (Mathf.Abs(dy) > _pitchBody * 0.6f) _dragged = true;
            if (!_dragged) return;
            int s = Mathf.Clamp(_dragStart + Mathf.RoundToInt(dy / _pitchBody), 0, MaxScroll());
            if (s != _scroll) { _scroll = s; if (_scroll == 0) NewLines = 0; Redraw(); OnScrolled?.Invoke(); }
        }

        public override void OnHover(UiPointerEvent e)
        {
            int l = _band.Contains(e.Point) || _nowRect.Contains(e.Point) ? -1 : LineAt(e.Point);
            if (l != _hoverLine) { _hoverLine = l; Redraw(); }
        }

        protected override void OnInteractionChanged() { if (!Hover && _hoverLine != -1) { _hoverLine = -1; Redraw(); } }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragged && inside) OnClick(e);
            _dragged = false;
        }

        public override void OnClick(UiPointerEvent e)
        {
            if (_nowShown && _nowRect.Contains(e.PressPoint)) { ScrollToBottom(); UiSounds.Fire("click"); OnNowClicked?.Invoke(); OnScrolled?.Invoke(); return; }
            if (_bandBlock >= 0 && _band.Contains(e.PressPoint)) { ScrollToBlock(_blocks[_bandBlock].Key); UiSounds.Fire("click"); OnScrolled?.Invoke(); return; }
            int l = LineAt(e.PressPoint);
            if (l < 0) return;
            var b = _blocks[_lines[l].Block];
            if (b.Collapsible) ToggleBlock(_lines[l].Block);
            else OnLinkClicked?.Invoke(b);
        }
    }
}
