using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    public enum ChatKind { Prompt, Answer, Update, Thinking, Work, Step, Notice, Error }

    /// One block of a chat: a prompt bubble, an answer, a collapsible thinking or work summary, a step line.
    public sealed class ChatBlock
    {
        public string Key = "";
        public ChatKind Kind;
        public string Head = "";       // the header line (rich text, already escaped by whoever builds it)
        public string Right = "";      // dim text at the right end of the header line (times, stats)
        public string Body = "";       // Prompt / Answer / Update: markdown-lite; Thinking / Step / Error: plain
        public string Preview = "";    // shown under a closed header (the reasoning streaming now)
        public bool Collapsible, Expanded, Live, MonoBody;
        public int Indent;             // 0, or 1 inside a work group
        public string Parent;          // the work group it belongs to: hidden while that one is closed
        public string Exchange = "";   // the prompt's key: the sticky prompt of whatever is on screen
        public Color? Tint;            // the bubble / bar / header colour
        public int BodyMaxChars = 6000;
    }

    /// A chat drawn on a panel (owner 2026-11-11): prompts as bubbles on the right, answers in full on the left,
    /// thinking and the agent's work as one-line headers that open with a tap, newest at the bottom. Drag to scroll;
    /// the prompt of what is on screen stays pinned at the top once it scrolled away; while the view sits at the
    /// bottom it follows new content, otherwise it holds still and counts what is new. Text is wrapped with
    /// TextMeshPro's own line breaking and drawn as whole lines (no clipping shader needed).
    public sealed class UiChatView : UiCanvas
    {
        public float TextSize = 0.0128f;
        public override bool Interactive => true;
        public Action<ChatBlock, bool> OnToggled;  // a block was opened (true) or closed
        public Action<ChatBlock> OnLinkClicked;    // reserved: a tap on a block that is not collapsible
        public string Empty = "";
        public int NewLines { get; private set; }
        public bool AtBottom => _scroll == 0;
        public IReadOnlyList<ChatBlock> Blocks => _blocks;
        public int LineCount => _lines.Count;

        private struct VLine
        {
            public int Block;
            public byte Part;      // 0 header, 1 preview, 2 body
            public int Index;      // the line's number inside its part
            public string Text;    // display text (rich, escaped)
            public bool Mono, Code, RightAlign;
            public Color Color;
            public float H, PadTop, PadBottom, X, W; // H = the pitch + PadTop + PadBottom
        }

        private readonly List<ChatBlock> _blocks = new List<ChatBlock>();
        private readonly HashSet<string> _toggled = new HashSet<string>();
        private readonly List<VLine> _lines = new List<VLine>();
        private readonly Dictionary<string, (string src, float w, bool mono, List<(string text, float w, int para)> lines)> _wrapCache = new Dictionary<string, (string, float, bool, List<(string, float, int)>)>();
        private readonly List<(int line, float y0, float y1)> _drawn = new List<(int, float, float)>();
        private Rect _band;            // the sticky prompt's rect (zero when none)
        private int _bandBlock = -1;
        private int _scroll, _dragStart, _hoverLine = -1, _spin;
        private bool _dragged;
        private float _nextSpin;
        private TextMeshPro _mBody, _mMono;
        private float _pitchBody = 0.016f, _pitchMono = 0.016f;

        private float Pitch(bool mono) => mono ? _pitchMono : _pitchBody;
        private float Wide => Rect.width - 0.010f; // room for the scroll bar
        private const float BubblePad = 0.007f, BarX = 0.009f, IndentStep = 0.016f;

        protected override void Build()
        {
            _mBody = MakeMeasurer(Theme.Body);
            _mMono = MakeMeasurer(Theme.Mono);
            _pitchBody = MeasurePitch(_mBody);
            _pitchMono = MeasurePitch(_mMono);
            Layout();
            base.Build();
        }

        protected override void Teardown()
        {
            if (_mBody != null) DestroyGo(_mBody.gameObject);
            if (_mMono != null) DestroyGo(_mMono.gameObject);
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

        private TextMeshPro MakeMeasurer(TMP_FontAsset font)
        {
            var ut = UiText.Create(Panel, new Rect(0, 0, 1f, 1f), "", font, TextSize, Color.clear, TextAlignmentOptions.TopLeft, true);
            var t = ut.Tmp;
            t.richText = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.gameObject.name = "chat-measure";
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false; // active (TextMeshPro lays out only when active), never drawn
            return t;
        }

        private static float MeasurePitch(TextMeshPro t)
        {
            float one = t.GetPreferredValues("Ag", 10f, 0f).y, two = t.GetPreferredValues("Ag\nAg", 10f, 0f).y;
            float p = two - one;
            return p > 0.004f && p < 0.05f ? p : Mathf.Max(0.012f, one);
        }

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
        public void ResetState() { _toggled.Clear(); _scroll = 0; NewLines = 0; ClearLayoutCaches(); }

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

        /// Scrolls so the block's first line is at the top of the view.
        public void ScrollToBlock(string key)
        {
            int li = FirstLine(KeyIndex(key));
            if (li < 0) return;
            // the last end of view that still shows the block's first line: then that line is the top one
            int best = Mathf.Min(_lines.Count, li + 1);
            for (int e = li + 1; e <= _lines.Count; e++) { if (FillStart(e) <= li) best = e; else break; }
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
        public void ClickLineForTest(int line) { var p = new Vector2(Rect.x + 0.02f, YOfLine(line)); var e = new UiPointerEvent { Point = p, PressPoint = p }; OnPress(e); OnRelease(e, true); }
        public void ClickStickyForTest() { var p = _band.center; var e = new UiPointerEvent { Point = p, PressPoint = p }; OnPress(e); OnRelease(e, true); }
        public int FirstLineOf(string key) => FirstLine(KeyIndex(key));
        private float YOfLine(int line) { foreach (var d in _drawn) if (d.line == line) return (d.y0 + d.y1) * 0.5f; return -1f; }
        private static string Strip(string s) => ChatText.Strip(s);

        // ---------------------------------------------------------------- layout

        private float ViewHeight() => Rect.height;
        private float ContentHeight() => _contentH;

        // ---- incremental layout (0.7.19: every timeline update laid out every block of the conversation again): each
        // block keeps the lines it was laid out as while what they are made of stays the same; only new and changed
        // blocks are wrapped again. Indexes per block (first / last line, by key) save the walks over all lines.

        private sealed class BlockLines
        {
            public ChatKind Kind;
            public string Head, Body, Preview, Parent;
            public bool Collapsible, Expanded, Live, MonoBody;
            public int Indent, BodyMaxChars, Pass;
            public float Pad, Wide;
            public UiTheme Theme;
            public readonly List<VLine> Lines = new List<VLine>();

            public bool Same(ChatBlock b, bool expanded, float pad, float wide, UiTheme theme) =>
                Kind == b.Kind && Expanded == expanded && Collapsible == b.Collapsible && Live == b.Live && MonoBody == b.MonoBody
                && Indent == b.Indent && BodyMaxChars == b.BodyMaxChars && Pad == pad && Wide == wide && ReferenceEquals(Theme, theme)
                && Parent == b.Parent && Head == b.Head && Preview == b.Preview && Body == b.Body;

            public void Take(ChatBlock b, bool expanded, float pad, float wide, UiTheme theme)
            {
                Kind = b.Kind; Expanded = expanded; Collapsible = b.Collapsible; Live = b.Live; MonoBody = b.MonoBody;
                Indent = b.Indent; BodyMaxChars = b.BodyMaxChars; Pad = pad; Wide = wide; Theme = theme;
                Parent = b.Parent; Head = b.Head; Preview = b.Preview; Body = b.Body;
            }
        }

        private readonly Dictionary<string, BlockLines> _blockLines = new Dictionary<string, BlockLines>();
        private readonly List<int> _firstLine = new List<int>(), _lastLine = new List<int>(); // per block; -1 = hidden
        private readonly Dictionary<string, int> _keyAt = new Dictionary<string, int>();     // a key's first block
        private readonly Dictionary<string, int> _promptAt = new Dictionary<string, int>();  // a prompt key's first block
        private readonly HashSet<string> _openWork = new HashSet<string>();
        private readonly List<string> _staleKeys = new List<string>();
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
            if (Panel == null || _mBody == null) return;
            var t = Theme;
            float wide = Wide;
            int pass = ++_layoutPass;
            string lastExchange = null;
            _openWork.Clear();
            foreach (var b in _blocks) if (b.Kind == ChatKind.Work && IsExpanded(b)) _openWork.Add(b.Key);
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                var b = _blocks[bi];
                string k = b.Key ?? "";
                _blockLines.TryGetValue(k, out var cached);
                if (cached != null) cached.Pass = pass; // still in the conversation (shown or inside a closed group)
                if (b.Parent != null && !_openWork.Contains(b.Parent)) continue;
                bool newExchange = b.Kind == ChatKind.Prompt && lastExchange != null;
                float pad = _lines.Count == 0 ? 0f : newExchange ? 0.016f : b.Kind == ChatKind.Answer ? 0.008f : b.Parent != null ? 0.001f : 0.005f;
                lastExchange = b.Exchange;
                bool expanded = IsExpanded(b);
                int from = _lines.Count;
                if (cached != null && cached.Same(b, expanded, pad, wide, t))
                {
                    foreach (var l in cached.Lines) { var c = l; c.Block = bi; _lines.Add(c); }
                }
                else
                {
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
            for (int i = 0; i < _lines.Count; i++) _contentH += _lines[i].H;
            // blocks that left the conversation drop their lines
            _staleKeys.Clear();
            foreach (var kv in _blockLines) if (kv.Value.Pass != pass) _staleKeys.Add(kv.Key);
            foreach (var k in _staleKeys) _blockLines.Remove(k);
        }

        /// One block's lines, appended to _lines (its header, preview, body; markdown and wrapping are cached too).
        private void LayoutBlock(int bi, ChatBlock b, bool expanded, float pad)
        {
            var t = Theme;
            float x = b.Indent * IndentStep;
            {
                switch (b.Kind)
                {
                    case ChatKind.Prompt:
                    {
                        float maxW = Wide * 0.80f - BubblePad * 2f;
                        var wrapped = Markdown(b.Body, t.Text, maxW, b.Key, b.BodyMaxChars);
                        float lw = wrapped.Count == 0 ? 0.05f : wrapped.Max(w => w.w);
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
                        if (b.Live && wrapped.Count > 0) wrapped[wrapped.Count - 1] = (wrapped[wrapped.Count - 1].text + "<color=#8FEBFF>▌</color>", wrapped[wrapped.Count - 1].w, wrapped[wrapped.Count - 1].mono, wrapped[wrapped.Count - 1].color, wrapped[wrapped.Count - 1].code);
                        if (b.Kind == ChatKind.Update && wrapped.Count > 6 && !expanded) wrapped = wrapped.Take(6).ToList();
                        AddLines(bi, 2, wrapped, ax, Wide - ax, pad, 0f, false, false);
                        break;
                    }
                    default:
                    {
                        // a header line, then (open) the body or (closed, live) a preview line
                        _lines.Add(new VLine { Block = bi, Part = 0, Index = 0, Text = b.Head + (b.Collapsible ? (expanded ? "  <color=#9DBDD2>▾</color>" : "  <color=#9DBDD2>▸</color>") : ""), Color = b.Kind == ChatKind.Error ? t.Danger : b.Kind == ChatKind.Notice ? t.TextDim : t.Text, H = _pitchBody + pad, PadTop = pad, X = x, W = Wide - x });
                        if (expanded && !string.IsNullOrEmpty(b.Body))
                        {
                            float bx = x + IndentStep;
                            var col = b.Kind == ChatKind.Error ? Color.Lerp(t.Danger, t.Text, 0.4f) : Color.Lerp(t.TextDim, t.Text, 0.15f);
                            var wrapped = b.MonoBody ? Plain(b.Body, col, Wide - bx, b.Key + ":m", true, b.BodyMaxChars) : Markdown(b.Body, col, Wide - bx, b.Key, b.BodyMaxChars);
                            AddLines(bi, 2, wrapped, bx, Wide - bx, 0.002f, 0.002f, b.MonoBody, false);
                        }
                        else if (!expanded && !string.IsNullOrEmpty(b.Preview))
                            _lines.Add(new VLine { Block = bi, Part = 1, Index = 0, Text = "<i>" + Esc(b.Preview) + "</i>", Color = t.TextDim, H = _pitchBody, PadTop = 0f, X = x + IndentStep, W = Wide - x - IndentStep });
                        break;
                    }
                }
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
                foreach (var (lineText, lw, para) in Wrap(joined, width, mono, key + ":" + i0))
                {
                    var p = run[para];
                    outl.Add((ChatText.Styled(lineText, p.Spans, ChatText.LineStart(joined, lineText, starts[para], ref cursor), Hex), lw, mono, ColorOf(p), p.Style == ParaStyle.Code));
                }
                i0 = i1;
            }
            return outl;
        }

        private List<(string text, float w, bool mono, Color color, bool code)> Plain(string src, Color color, float width, string key, bool mono, int maxChars)
        {
            src = (src ?? "").Replace("\r", "").Replace("\t", "    ").TrimEnd();
            if (src.Length > maxChars) src = "… " + src.Substring(src.Length - maxChars); // output: its end matters most
            return Wrap(src, width, mono, key).Select(l => (Esc(l.text), l.w, mono, color, false)).ToList();
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

        // ---------------------------------------------------------------- scrolling

        // the first line shown when the view ends at line `end` (bottom-aligned)
        private int FillStart(int end)
        {
            float used = 0f, view = ViewHeight();
            int start = end;
            while (start > 0 && used + _lines[start - 1].H <= view) { start--; used += _lines[start].H; }
            return start;
        }

        private int MaxScroll()
        {
            float h = 0f, view = ViewHeight();
            int k = 0;
            while (k < _lines.Count && h + _lines[k].H <= view) { h += _lines[k].H; k++; }
            return Mathf.Max(0, _lines.Count - k);
        }

        private float DistanceFromBottom(int line)
        {
            int end = _lines.Count - _scroll;
            float d = 0f;
            for (int i = line; i < end; i++) d += _lines[i].H;
            return d;
        }

        private void KeepAt(int line, float fromBottom)
        {
            float d = 0f;
            int end = line;
            while (end < _lines.Count && d + _lines[end].H <= fromBottom + 0.0005f) { d += _lines[end].H; end++; }
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

        public override void Update()
        {
            if (_flashKey != null)
            {
                if (Time.unscaledTime >= _flashUntil) { _flashKey = null; Redraw(); }
                else if (Time.unscaledTime >= _nextFlashDraw) { _nextFlashDraw = Time.unscaledTime + 0.05f; Redraw(); }
            }
            // live headers spin (4 Hz, only while one is on screen)
            if (Time.unscaledTime < _nextSpin) return;
            _nextSpin = Time.unscaledTime + 0.25f;
            bool live = false;
            foreach (var d in _drawn) { var b = _blocks[_lines[d.line].Block]; if (b.Live && _lines[d.line].Part == 0) { live = true; break; } }
            if (!live) return;
            _spin = (_spin + 1) % 4;
            Redraw();
        }

        private static readonly string[] Spinner = { "◐", "◓", "◑", "◒" };

        protected override void Draw()
        {
            var t = Theme;
            _drawn.Clear();
            _band = default;
            _bandBlock = -1;
            if (_lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(Empty)) Txt(Rect, Empty, TextSize, t.TextDim, TextAlignmentOptions.Center, null, true).Tmp.overflowMode = TextOverflowModes.Ellipsis;
                return;
            }
            float view = ViewHeight();
            _scroll = Mathf.Clamp(_scroll, 0, MaxScroll());
            int end = _lines.Count - _scroll, start = end;
            float used = 0f;
            while (start > 0 && used + _lines[start - 1].H <= view) { start--; used += _lines[start].H; }
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
                        _band = new Rect(Rect.x, Rect.y, Rect.width - 0.006f, bandH - 0.004f);
                        _bandBlock = pb;
                    }
                }
            }
            bool overflow = start > 0 || _scroll > 0;
            float y = overflow ? Rect.yMax - used : Rect.y;
            // backgrounds first: bubbles, the answer bar, code, hover
            int i = start;
            while (i < end)
            {
                int bi = _lines[i].Block;
                int j = i;
                float y0 = y, h = 0f;
                while (j < end && _lines[j].Block == bi) { h += _lines[j].H; j++; }
                var b = _blocks[bi];
                float top = y0 + _lines[i].PadTop;
                if (b.Kind == ChatKind.Prompt)
                {
                    float lw = 0f;
                    for (int k = i; k < j; k++) lw = Mathf.Max(lw, _lines[k].W);
                    var r = new Rect(Rect.x + _lines[i].X - BubblePad, top - (i == FirstLine(bi) ? BubblePad : 0f), lw + BubblePad * 2f, y0 + h - top + (i == FirstLine(bi) ? BubblePad : 0f));
                    var c = b.Tint ?? t.Accent;
                    Box(r, new Color(c.r, c.g, c.b, 0.20f), 0.007f, 1, 0f, 0.001f);
                }
                else if (b.Kind == ChatKind.Answer)
                {
                    var c = b.Tint ?? t.Ok;
                    float f = FlashLevel(b.Key);
                    if (f > 0.01f) Box(new Rect(Rect.x + _lines[i].X - 0.012f, top - 0.002f, Wide - _lines[i].X + 0.012f, y0 + h - top + 0.002f), new Color(c.r, c.g, c.b, 0.16f * f), 0.004f, 1, 0f, 0.001f);
                    Pill(new Rect(Rect.x + _lines[i].X - 0.009f - 0.0015f * f, top + 0.002f, 0.0026f + 0.003f * f, Mathf.Max(0.004f, y0 + h - top - 0.004f)), new Color(c.r, c.g, c.b, b.Live ? 0.95f : Mathf.Lerp(0.6f, 1f, f)));
                }
                float yy = y0;
                for (int k = i; k < j; k++)
                {
                    var l = _lines[k];
                    if (l.Code) Box(new Rect(Rect.x + l.X - 0.003f, yy + l.PadTop, l.W + 0.003f, l.H - l.PadTop), new Color(0f, 0f, 0f, 0.28f), 0.001f, 1, 0f, 0.0004f);
                    if (k == _hoverLine && l.Part == 0 && b.Collapsible) Box(new Rect(Rect.x + l.X - 0.004f, yy + l.PadTop, Wide - l.X + 0.004f, l.H - l.PadTop), t.Accent, 0.003f, 2, 0.4f, 0.001f);
                    yy += l.H;
                }
                y += h;
                i = j;
            }
            // text: one TextMeshPro per run of lines of one block part, font and colour
            y = overflow ? Rect.yMax - used : Rect.y;
            i = start;
            while (i < end)
            {
                var l0 = _lines[i];
                int j = i + 1;
                float h = l0.H;
                while (j < end && _lines[j].Block == l0.Block && _lines[j].Part == l0.Part && _lines[j].Mono == l0.Mono && _lines[j].Color == l0.Color
                       && _lines[j].PadTop == 0f && _lines[j - 1].PadBottom == 0f && Mathf.Abs(_lines[j].X - l0.X) < 1e-4f && _lines[j].Code == l0.Code)
                { h += _lines[j].H; j++; }
                string text;
                if (j == i + 1) text = LineText(i); // one line: its own string, nothing built
                else
                {
                    _drawSb.Clear();
                    for (int k = i; k < j; k++) { if (k > i) _drawSb.Append('\n'); _drawSb.Append(LineText(k)); }
                    text = _drawSb.ToString();
                }
                float top = y + l0.PadTop;
                var b = _blocks[l0.Block];
                var r = new Rect(Rect.x + l0.X, top, l0.W, h - l0.PadTop - _lines[j - 1].PadBottom + 0.004f);
                bool header = l0.Part != 2;
                if (l0.Part == 0 && !string.IsNullOrEmpty(b.Right))
                {
                    float rw = Mathf.Min(r.width * 0.45f, 0.01f + Strip(b.Right).Length * TextSize * 0.42f);
                    var rt = Txt(new Rect(Rect.x + Wide - rw, top, rw, l0.H - l0.PadTop), b.Right, TextSize * 0.92f, t.TextDim, TextAlignmentOptions.TopRight, Theme.Body);
                    rt.Tmp.overflowMode = TextOverflowModes.Ellipsis;
                    r.width -= rw + 0.004f;
                }
                // wrapped lines already fit: Overflow (Ellipsis would drop every line after one that is a hair too long);
                // headers are single lines cut with …
                if (!header) r.width += 0.006f;
                var ut = Txt(r, text, TextSize, l0.Color, TextAlignmentOptions.TopLeft, l0.Mono ? Theme.Mono : Theme.Body);
                ut.Tmp.overflowMode = header ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
                float yy = y;
                for (int k = i; k < j; k++) { _drawn.Add((k, yy + _lines[k].PadTop, yy + _lines[k].H)); yy += _lines[k].H; }
                y += h;
                i = j;
            }
            // the sticky prompt on top
            if (_bandBlock >= 0)
            {
                var c = _blocks[_bandBlock].Tint ?? t.Accent;
                Box(_band, Color.Lerp(new Color(0.02f, 0.06f, 0.1f, 0.97f), c, 0.16f), 0.006f, 1, 0f, 0.001f);
                Pill(new Rect(_band.x + 0.004f, _band.y + 0.004f, 0.0026f, _band.height - 0.008f), c);
                Txt(new Rect(_band.x + 0.012f, _band.y + 0.003f, _band.width - 0.05f, _band.height - 0.004f), BandText(_blocks[_bandBlock].Body), TextSize, t.Text, TextAlignmentOptions.TopLeft, Theme.Body, true).Tmp.overflowMode = TextOverflowModes.Ellipsis;
                Txt(new Rect(_band.xMax - 0.04f, _band.y, 0.036f, _band.height), "▲", TextSize, t.TextDim, TextAlignmentOptions.Center).Tmp.overflowMode = TextOverflowModes.Ellipsis;
            }
            // scroll bar
            if (_lines.Count > 0 && (start > 0 || _scroll > 0))
            {
                float total = ContentHeight();
                float frac = Mathf.Clamp01(view / Mathf.Max(view, total));
                int max = MaxScroll();
                float pos = max <= 0 ? 1f : 1f - _scroll / (float)max;
                float bh = Mathf.Max(0.02f, Rect.height * frac);
                Pill(new Rect(Rect.xMax - 0.004f, Rect.y + (Rect.height - bh) * pos, 0.003f, bh), new Color(t.Accent.r, t.Accent.g, t.Accent.b, 0.7f));
            }
        }

        private string LineText(int k)
        {
            var l = _lines[k];
            if (l.Part == 0 && _blocks[l.Block].Live && l.Text.Length > 0 && l.Text.StartsWith("◌")) return Spinner[_spin] + l.Text.Substring(1);
            return l.Text;
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
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
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
            if (s != _scroll) { _scroll = s; if (_scroll == 0) NewLines = 0; Redraw(); }
        }

        public override void OnHover(UiPointerEvent e)
        {
            int l = _band.Contains(e.Point) ? -1 : LineAt(e.Point);
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
            if (_bandBlock >= 0 && _band.Contains(e.PressPoint)) { ScrollToBlock(_blocks[_bandBlock].Key); UiSounds.Fire("click"); return; }
            int l = LineAt(e.PressPoint);
            if (l < 0) return;
            var b = _blocks[_lines[l].Block];
            if (b.Collapsible) ToggleBlock(_lines[l].Block);
            else OnLinkClicked?.Invoke(b);
        }
    }
}
