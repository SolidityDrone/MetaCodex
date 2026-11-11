using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// What a UiSourceView shows: a file (numbered lines with their syntax states) or one file's diff (DiffDoc), or a
    /// message (binary, empty, an error). Made off the main thread (parsing and colouring a big file takes a while).
    public sealed class SourceDoc
    {
        public string Path = "";
        public CodeLang Lang = CodeText.Text;
        public List<string> Lines;     // a file
        public int[] States;           // the tokenizer's state at the start of each line
        public DiffDoc Diff;           // a diff
        public string Message = "";    // shown when there are no lines
        public string Note;            // the last row: "first 50,000 lines of 81,200", ...
        public int MaxLen, MaxNum;
        public bool IsDiff => Diff != null;
        public int RowCount => Diff != null ? Diff.Lines.Count : Lines?.Count ?? 0;

        public static SourceDoc Text(string message) => new SourceDoc { Message = message ?? "" };

        public static SourceDoc Code(string path, string text, string note = null, int maxLines = 50000)
        {
            var d = new SourceDoc { Path = path ?? "", Lang = CodeText.LangFor(path), Note = note };
            d.Lines = CodeText.SplitLines(text ?? "", maxLines, out bool cut);
            if (cut) d.Note = (note != null ? note + " · " : "") + $"the first {maxLines:N0} lines";
            d.States = new int[d.Lines.Count];
            var spans = new List<TokSpan>();
            int st = CodeText.Normal;
            for (int i = 0; i < d.Lines.Count; i++)
            {
                d.States[i] = st;
                st = CodeText.Tokenize(d.Lang, d.Lines[i], st, spans);
                d.MaxLen = Math.Max(d.MaxLen, d.Lines[i].Length);
            }
            d.MaxNum = d.Lines.Count;
            if (d.Lines.Count == 0) d.Message = "(empty file)";
            return d;
        }

        public static SourceDoc FromDiff(string path, DiffDoc diff, string note = null)
        {
            var d = new SourceDoc { Path = path ?? diff?.Path ?? "", Diff = diff, Note = note };
            d.Lang = CodeText.LangFor(d.Path);
            if (diff == null) { d.Diff = null; d.Message = "no diff"; return d; }
            if (diff.Truncated) d.Note = (note != null ? note + " · " : "") + "the diff is cut here (too long to show)";
            diff.ComputeSyntax(d.Lang);
            void Walk(DiffLine l)
            {
                if (l.Kind == DiffKind.Fold) { foreach (var h in l.Hidden) Walk(h); return; }
                d.MaxLen = Math.Max(d.MaxLen, l.Text.Length);
                d.MaxNum = Math.Max(d.MaxNum, Math.Max(l.Old, l.New));
            }
            foreach (var l in diff.Lines) Walk(l);
            if (diff.Lines.Count == 0) d.Message = diff.Binary ? "Binary file: no text diff." : "No difference.";
            return d;
        }
    }

    /// A read-only code editor view in VS Code's dark look (THREAD's sidebar): monospace rows with line numbers,
    /// syntax colours; for diffs old and new numbers, a +/- gutter, green / red line backgrounds, unchanged runs folded
    /// (a tap opens them), hunk headers, an overview ruler of the changes. Wrap on, or scroll sideways (drag). Only the
    /// rows on screen are drawn. Keys (Key): up / down / page / home / end move a line cursor, enter opens a fold.
    public sealed class UiSourceView : UiCanvas
    {
        public float RowHeight = 0.0225f;
        public float TextSize = 0.016f;      // line height given to the text: an em of 11.5 mm on the panel, ~1° at 0.8 m
        public bool Wrap;
        public override bool Interactive => true;
        public SourceDoc Doc => _doc;
        public int Cursor => _cursor;
        public int HScroll => _hcol;
        public int Columns => _cols;
        public float ScrollRows => _scroll;
        public int VisualRowCount => _vis.Count;

        // VS Code dark (sRGB)
        public static readonly Color Bg = UiTheme.Hex(0x1E1E1E), CursorBg = UiTheme.Hex(0x2A2D2E);
        public static readonly Color AddBg = UiTheme.Hex(0x1F3A24), AddGutter = UiTheme.Hex(0x2B5A33), DelBg = UiTheme.Hex(0x4A1E22), DelGutter = UiTheme.Hex(0x6B2830);
        public static readonly Color HunkBg = UiTheme.Hex(0x1B2738), FoldBg = UiTheme.Hex(0x262B33);
        public static readonly Color Plain = UiTheme.Hex(0xD4D4D4);
        public const string NumHex = "858585", NumHiHex = "C6C6C6", AddHex = "7EE787", DelHex = "FF7B72", HunkHex = "79C0FF", FoldHex = "A0A8B4";

        public static string TokHex(Tok k)
        {
            switch (k)
            {
                case Tok.Keyword: case Tok.Tag: case Tok.Heading: return "569CD6";
                case Tok.Control: case Tok.Meta: return "C586C0";
                case Tok.Type: return "4EC9B0";
                case Tok.Func: return "DCDCAA";
                case Tok.String: return "CE9178";
                case Tok.Number: return "B5CEA8";
                case Tok.Comment: return "6A9955";
                case Tok.Attr: case Tok.Key: case Tok.Variable: return "9CDCFE";
                default: return "D4D4D4";
            }
        }

        private SourceDoc _doc;
        private readonly List<(int row, int col)> _vis = new List<(int, int)>();
        private readonly List<TokSpan> _spans = new List<TokSpan>();
        private readonly List<(float frac, Color c)> _marks = new List<(float, Color)>();
        private float _scroll, _dragScroll;
        private int _hcol, _dragCol, _cursor = -1, _cols = 80, _numW = 3;
        private int _dragAxis; // 0 undecided, 1 vertical, 2 horizontal
        private bool _dragged;
        private bool _visDirty = true;
        private float _builtWidth = -1f;
        private string _message = "";

        private int Rows => Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowHeight));
        private int MaxFirst => Mathf.Max(0, _vis.Count - Rows);

        /// One monospace character's advance at TextSize (metres on the panel), from the mono font (DejaVu Sans Mono:
        /// 0.602 em).
        public float Advance
        {
            get
            {
                float em = Theme.FontSizeFor(TextSize) * 0.1f;
                var f = Theme.Mono;
                if (f != null && f.faceInfo.pointSize > 0 && f.characterLookupTable != null && f.characterLookupTable.TryGetValue('M', out var ch) && ch.glyph != null)
                    return ch.glyph.metrics.horizontalAdvance / f.faceInfo.pointSize * f.faceInfo.scale * em;
                return em * 0.602f;
            }
        }

        private float GutterWidth => _doc != null && _doc.IsDiff ? (_numW * 2 + 3) * Advance + 0.008f : (_numW + 1) * Advance + 0.008f;

        public void SetMessage(string message) { _doc = null; _message = message ?? ""; _vis.Clear(); _marks.Clear(); _scroll = 0; _hcol = 0; _cursor = -1; Redraw(); }

        /// Shows a document. `keepPlace`: the same file again (a live refresh): the scroll and cursor stay.
        public void SetDoc(SourceDoc doc, bool keepPlace = false)
        {
            int keepRow = keepPlace && _vis.Count > 0 ? _vis[Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, _vis.Count - 1)].row : 0;
            _doc = doc;
            _message = doc?.Message ?? "";
            _numW = Mathf.Max(3, (doc?.MaxNum ?? 0).ToString().Length);
            _visDirty = true;
            if (!keepPlace) { _hcol = 0; _cursor = -1; }
            BuildVis();
            _scroll = keepPlace ? Mathf.Min(VisIndexOf(keepRow), MaxFirst) : 0f;
            if (_cursor >= (_doc?.RowCount ?? 0)) _cursor = -1;
            Redraw();
        }

        public void SetWrap(bool wrap)
        {
            if (Wrap == wrap) return;
            int top = _vis.Count > 0 ? _vis[Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, _vis.Count - 1)].row : 0;
            Wrap = wrap;
            _hcol = 0;
            _visDirty = true;
            BuildVis();
            _scroll = Mathf.Min(VisIndexOf(top), MaxFirst);
            Redraw();
        }

        private int VisIndexOf(int row)
        {
            for (int i = 0; i < _vis.Count; i++) if (_vis[i].row >= row) return i;
            return Mathf.Max(0, _vis.Count - 1);
        }

        private int RowLength(int r)
        {
            if (_doc == null) return 0;
            if (_doc.IsDiff) { var l = _doc.Diff.Lines[r]; return l.Kind == DiffKind.Fold || l.Kind == DiffKind.Hunk || l.Kind == DiffKind.Note ? 0 : l.Text.Length; }
            return _doc.Lines[r].Length;
        }

        private void BuildVis()
        {
            _builtWidth = Rect.width;
            _cols = Mathf.Max(10, Mathf.FloorToInt((Rect.width - GutterWidth - 0.012f) / Mathf.Max(1e-4f, Advance)));
            _vis.Clear();
            _marks.Clear();
            int n = _doc?.RowCount ?? 0;
            for (int r = 0; r < n; r++)
            {
                int len = RowLength(r);
                int segs = Wrap && len > _cols ? (len + _cols - 1) / _cols : 1;
                for (int s = 0; s < segs; s++) _vis.Add((r, s * _cols));
            }
            if (_doc != null && _doc.Note != null) _vis.Add((-1, 0)); // the note row
            // the overview ruler: where the changes are, in up to 64 marks
            if (_doc != null && _doc.IsDiff && _vis.Count > Rows)
            {
                const int buckets = 64;
                var kind = new int[buckets];
                for (int i = 0; i < _vis.Count; i++)
                {
                    int r = _vis[i].row;
                    if (r < 0) continue;
                    var k = _doc.Diff.Lines[r].Kind;
                    int b = Mathf.Min(buckets - 1, i * buckets / _vis.Count);
                    if (k == DiffKind.Add) kind[b] |= 1;
                    else if (k == DiffKind.Del) kind[b] |= 2;
                }
                for (int b = 0; b < buckets; b++)
                    if (kind[b] != 0) _marks.Add(((b + 0.5f) / buckets, kind[b] == 1 ? UiTheme.Hex(0x3FB950) : kind[b] == 2 ? UiTheme.Hex(0xF85149) : UiTheme.Hex(0xD29922)));
            }
            _visDirty = false;
        }

        public void ScrollBy(float rows) { _scroll = Mathf.Clamp(_scroll + rows, 0, MaxFirst); Redraw(); }
        public void ScrollToRow(int row) { _scroll = Mathf.Clamp(VisIndexOf(row), 0, MaxFirst); Redraw(); }
        public void ScrollSideways(int cols) { _hcol = Mathf.Clamp(_hcol + cols, 0, MaxHCol); Redraw(); }
        private int MaxHCol => Wrap || _doc == null ? 0 : Mathf.Max(0, _doc.MaxLen - _cols + 4);

        /// Opens the fold at a diff row (a tap on "⋯ N unchanged lines").
        public bool OpenFold(int row)
        {
            if (_doc == null || !_doc.IsDiff || !_doc.Diff.Unfold(row)) return false;
            float keep = _scroll;
            BuildVis();
            _scroll = Mathf.Clamp(keep, 0, MaxFirst);
            Redraw();
            return true;
        }

        public int FirstFoldRow() { if (_doc == null || !_doc.IsDiff) return -1; for (int i = 0; i < _doc.Diff.Lines.Count; i++) if (_doc.Diff.Lines[i].Kind == DiffKind.Fold) return i; return -1; }

        /// Keyboard: up, down, pageup, pagedown, home, end, left, right, enter. True when it did something.
        public bool Key(string key)
        {
            int n = _doc?.RowCount ?? 0;
            if (n == 0) return false;
            int page = Mathf.Max(1, Rows - 2);
            switch (key)
            {
                case "up": MoveCursor(_cursor < 0 ? TopRow() : _cursor - 1); return true;
                case "down": MoveCursor(_cursor < 0 ? TopRow() : _cursor + 1); return true;
                case "pageup": ScrollBy(-page); MoveCursor(Mathf.Max(0, (_cursor < 0 ? TopRow() : _cursor) - page)); return true;
                case "pagedown": ScrollBy(page); MoveCursor(Mathf.Min(n - 1, (_cursor < 0 ? TopRow() : _cursor) + page)); return true;
                case "home": MoveCursor(0); return true;
                case "end": MoveCursor(n - 1); return true;
                case "left": if (Wrap || _hcol == 0) return false; ScrollSideways(-8); return true;
                case "right": if (Wrap || MaxHCol == 0) return false; ScrollSideways(8); return true;
                case "enter": return _cursor >= 0 && OpenFold(_cursor);
            }
            return false;
        }

        private int TopRow() => _vis.Count == 0 ? 0 : Mathf.Max(0, _vis[Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, _vis.Count - 1)].row);

        private void MoveCursor(int row)
        {
            int n = _doc?.RowCount ?? 0;
            if (n == 0) return;
            _cursor = Mathf.Clamp(row, 0, n - 1);
            int vi = VisIndexOf(_cursor);
            if (vi < _scroll) _scroll = vi;
            else if (vi >= _scroll + Rows) _scroll = Mathf.Min(MaxFirst, vi - Rows + 1);
            Redraw();
        }

        protected override void Draw()
        {
            var t = Theme;
            AddShape(Panel.Shape(UiShapeKind.Flat, Rect, Bg, 0.003f, 0f, 0.0005f, 0f));
            if (_doc == null || _doc.RowCount == 0)
            {
                Txt(new Rect(Rect.x + 0.02f, Rect.y, Rect.width - 0.04f, Rect.height), UiViz.NoParse(_message), t.SmallSize * 1.15f, UiTheme.Hex(0xA0A8B4), TextAlignmentOptions.Center, null, true);
                return;
            }
            if (_visDirty || !Mathf.Approximately(_builtWidth, Rect.width)) BuildVis();
            float adv = Advance, gw = GutterWidth;
            int rows = Rows, first = Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, MaxFirst);
            bool diff = _doc.IsDiff;
            var sb = new StringBuilder(256);
            for (int i = 0; i < rows && first + i < _vis.Count; i++)
            {
                var (r, col) = _vis[first + i];
                float y = Rect.y + i * RowHeight;
                var rowRect = new Rect(Rect.x, y, Rect.width - 0.006f, RowHeight);
                var textRect = new Rect(Rect.x + gw, y, Rect.width - gw - 0.008f, RowHeight);
                if (r < 0) // the note row
                {
                    Txt(new Rect(Rect.x + 0.01f, y, Rect.width - 0.02f, RowHeight), UiViz.NoParse("… " + _doc.Note), TextSize * 0.85f, UiTheme.Hex(0xA0A8B4), TextAlignmentOptions.MidlineLeft, t.Mono);
                    continue;
                }
                bool cont = col > 0 && Wrap; // a wrapped line's next part
                bool cur = r == _cursor;
                if (diff)
                {
                    var l = _doc.Diff.Lines[r];
                    switch (l.Kind)
                    {
                        case DiffKind.Fold:
                            Flat(rowRect, FoldBg);
                            Txt(new Rect(Rect.x + 0.01f, y, Rect.width - 0.02f, RowHeight), $"<color=#{FoldHex}>⋯   {l.Hidden.Count} unchanged line{(l.Hidden.Count == 1 ? "" : "s")}   <size=80%>(lines {l.New}–{l.New + l.Hidden.Count - 1}: tap to show)</size></color>", TextSize * 0.9f, Plain, TextAlignmentOptions.MidlineLeft, t.Mono);
                            if (cur) Flat(new Rect(Rect.x, y, 0.003f, RowHeight), t.Accent);
                            continue;
                        case DiffKind.Hunk:
                            Flat(rowRect, HunkBg);
                            Txt(new Rect(Rect.x + 0.01f, y, Rect.width - 0.02f, RowHeight), $"<color=#{HunkHex}>@@ −{l.Old} +{l.New} @@</color>  " + Esc(l.Text), TextSize * 0.9f, UiTheme.Hex(0xA0A8B4), TextAlignmentOptions.MidlineLeft, t.Mono);
                            continue;
                        case DiffKind.Note:
                            Txt(new Rect(Rect.x + gw, y, Rect.width - gw, RowHeight), Esc("⏎ " + l.Text), TextSize * 0.85f, UiTheme.Hex(0x8B949E), TextAlignmentOptions.MidlineLeft, t.Mono);
                            continue;
                    }
                    bool add = l.Kind == DiffKind.Add, del = l.Kind == DiffKind.Del;
                    if (add || del)
                    {
                        Flat(rowRect, add ? AddBg : DelBg);
                        Flat(new Rect(Rect.x, y, gw - 0.004f, RowHeight), add ? AddGutter : DelGutter);
                    }
                    else if (cur) Flat(rowRect, CursorBg);
                    if (!cont)
                    {
                        string nh = cur ? NumHiHex : NumHex;
                        sb.Clear();
                        sb.Append("<color=#").Append(nh).Append('>').Append(l.Old > 0 ? l.Old.ToString().PadLeft(_numW) : new string(' ', _numW)).Append(' ')
                          .Append(l.New > 0 ? l.New.ToString().PadLeft(_numW) : new string(' ', _numW)).Append("</color> ");
                        sb.Append(add ? "<color=#" + AddHex + ">+</color>" : del ? "<color=#" + DelHex + ">−</color>" : " ");
                        Txt(new Rect(Rect.x + 0.004f, y, gw - 0.004f, RowHeight), sb.ToString(), TextSize, Plain, TextAlignmentOptions.MidlineLeft, t.Mono);
                    }
                    CodeText.Tokenize(_doc.Lang, l.Text, l.SyntaxState, _spans);
                    int from = Wrap ? col : _hcol;
                    Txt(textRect, Rich(l.Text, from, _cols, _spans), TextSize, Plain, TextAlignmentOptions.MidlineLeft, t.Mono);
                }
                else
                {
                    if (cur) Flat(rowRect, CursorBg);
                    if (!cont) Txt(new Rect(Rect.x + 0.004f, y, gw - 0.006f - adv, RowHeight), $"<color=#{(cur ? NumHiHex : NumHex)}>{r + 1}</color>", TextSize, Plain, TextAlignmentOptions.MidlineRight, t.Mono);
                    string line = _doc.Lines[r];
                    CodeText.Tokenize(_doc.Lang, line, _doc.States[r], _spans);
                    Txt(textRect, Rich(line, Wrap ? col : _hcol, _cols, _spans), TextSize, Plain, TextAlignmentOptions.MidlineLeft, t.Mono);
                }
            }
            // the overview ruler and the scroll thumb (right edge), the sideways thumb (bottom edge)
            if (_vis.Count > rows)
            {
                float x = Rect.xMax - 0.005f;
                foreach (var (frac, c) in _marks) Flat(new Rect(x, Rect.y + frac * Rect.height - 0.0012f, 0.005f, 0.0024f), c);
                float h = Mathf.Max(0.012f, Rect.height * rows / _vis.Count), pos = MaxFirst <= 0 ? 0f : first / (float)MaxFirst;
                Flat(new Rect(x + 0.001f, Rect.y + (Rect.height - h) * pos, 0.003f, h), new Color(t.Accent.r, t.Accent.g, t.Accent.b, 0.55f));
            }
            if (MaxHCol > 0)
            {
                float w = (Rect.width - gw) * _cols / (float)(_doc.MaxLen + 4), pos = _hcol / (float)MaxHCol;
                Flat(new Rect(Rect.x + gw + (Rect.width - gw - w - 0.006f) * pos, Rect.yMax - 0.003f, w, 0.0025f), new Color(t.Accent.r, t.Accent.g, t.Accent.b, 0.5f));
            }
        }

        private void Flat(Rect r, Color c) => AddShape(Panel.Shape(UiShapeKind.Flat, r, c, 0f, 0f, 0.0004f, 0f));

        public static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "</no​parse>") + "</noparse>";

        /// The columns [from, from + count) of a line as TMP rich text in the token colours.
        public static string Rich(string line, int from, int count, List<TokSpan> spans)
        {
            if (string.IsNullOrEmpty(line) || from >= line.Length) return "";
            int end = Math.Min(line.Length, from + count);
            var sb = new StringBuilder(line.Length + 64);
            int pos = from;
            foreach (var s in spans)
            {
                int a = Math.Max(s.Start, from), b = Math.Min(s.Start + s.Length, end);
                if (b <= a) continue;
                if (a > pos) sb.Append(Esc(line.Substring(pos, a - pos)));
                sb.Append("<color=#").Append(TokHex(s.Kind)).Append('>').Append(Esc(line.Substring(a, b - a))).Append("</color>");
                pos = b;
            }
            if (pos < end) sb.Append(Esc(line.Substring(pos, end - pos)));
            return sb.ToString();
        }

        private int VisAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            int i = Mathf.FloorToInt(_scroll) + Mathf.FloorToInt((p.y - Rect.y) / RowHeight);
            return i >= 0 && i < _vis.Count ? i : -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragScroll = _scroll; _dragCol = _hcol; _dragAxis = 0; _dragged = false; }

        public override void OnDrag(UiPointerEvent e)
        {
            var d = e.Point - e.PressPoint;
            if (_dragAxis == 0 && d.magnitude > 0.006f) _dragAxis = Mathf.Abs(d.x) > Mathf.Abs(d.y) * 1.3f && MaxHCol > 0 ? 2 : 1;
            if (_dragAxis == 0) return;
            _dragged = true;
            if (_dragAxis == 1)
            {
                float s = Mathf.Clamp(_dragScroll - d.y / RowHeight, 0, MaxFirst);
                bool changed = Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll);
                _scroll = s;
                if (changed) Redraw();
            }
            else
            {
                int c = Mathf.Clamp(_dragCol - Mathf.RoundToInt(d.x / Mathf.Max(1e-4f, Advance)), 0, MaxHCol);
                if (c != _hcol) { _hcol = c; Redraw(); }
            }
        }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragged && inside) OnClick(e);
            _dragged = false;
        }

        public override void OnClick(UiPointerEvent e)
        {
            int vi = VisAt(e.PressPoint);
            if (vi < 0 || _vis[vi].row < 0) return;
            int r = _vis[vi].row;
            if (_doc.IsDiff && _doc.Diff.Lines[r].Kind == DiffKind.Fold) { _cursor = r; OpenFold(r); return; }
            _cursor = r;
            Redraw();
        }

        /// Self-tests: the text of the visible rows (gutter and code, tags stripped).
        public List<string> VisibleTextForTest()
        {
            var list = new List<string>();
            if (_doc == null) { list.Add(_message); return list; }
            int first = Mathf.FloorToInt(_scroll);
            for (int i = 0; i < Rows && first + i < _vis.Count; i++)
            {
                var (r, col) = _vis[first + i];
                if (r < 0) { list.Add("… " + _doc.Note); continue; }
                if (col > 0 && Wrap)
                {
                    string all = _doc.IsDiff ? _doc.Diff.Lines[r].Text ?? "" : _doc.Lines[r];
                    list.Add("↪" + (col < all.Length ? all.Substring(col, Math.Min(_cols, all.Length - col)) : ""));
                    continue;
                }
                if (_doc.IsDiff)
                {
                    var l = _doc.Diff.Lines[r];
                    string g = l.Kind == DiffKind.Fold ? "⋯ " + l.Hidden.Count : l.Kind == DiffKind.Hunk ? "@@" : $"{l.Old,4} {l.New,4} {(l.Kind == DiffKind.Add ? "+" : l.Kind == DiffKind.Del ? "-" : " ")}";
                    string text = l.Text ?? "";
                    int from = Wrap ? col : _hcol;
                    list.Add(g + " " + (from < text.Length ? text.Substring(from, Math.Min(_cols, text.Length - from)) : ""));
                }
                else
                {
                    string text = _doc.Lines[r];
                    int from = Wrap ? col : _hcol;
                    list.Add($"{r + 1,4} " + (from < text.Length ? text.Substring(from, Math.Min(_cols, text.Length - from)) : ""));
                }
            }
            return list;
        }

        public DiffKind RowKindForTest(int row) => _doc != null && _doc.IsDiff && row >= 0 && row < _doc.Diff.Lines.Count ? _doc.Diff.Lines[row].Kind : DiffKind.Note;
        public int RowsOnScreen => Rows;
    }

    /// A list in the console look whose rows stay legible in the headset: an icon, a name with a dim detail after it
    /// (one line, cut with …), a right-aligned monospace text (+12 −3, sizes, times) and a coloured letter (M, A, U, D).
    /// Section headers between rows. Only the rows on screen are drawn; drag to scroll; keys move a cursor.
    public sealed class UiRows : UiCanvas
    {
        public sealed class Row
        {
            public string Icon = "", Name = "", Detail = "", Right = "", Badge = "";
            public Color? IconColor, NameColor, BadgeColor;
            public bool Header, Dim;
            public object Tag;
        }

        public float RowHeight = 0.034f;
        public float NameSize = 0.016f, DetailSize = 0.0135f;
        /// > 0: the names take this width and the details start in one column (STATUS: key / value).
        public float NameColumn;
        public string Empty = "";
        public Action<Row, int> OnPick;
        public override bool Interactive => true;
        public IReadOnlyList<Row> Items => _items;
        public int Cursor { get => _cursor; set { _cursor = value; Redraw(); } }
        private readonly List<Row> _items = new List<Row>();
        private float _scroll, _dragStart;
        private bool _dragged;
        private int _cursor = -1, _hover = -1;

        private int Rows => Mathf.Max(1, Mathf.FloorToInt(Rect.height / RowHeight));
        private int MaxFirst => Mathf.Max(0, _items.Count - Rows);

        public void SetRows(IEnumerable<Row> rows, bool keepScroll = false)
        {
            object curTag = _cursor >= 0 && _cursor < _items.Count ? _items[_cursor].Tag : null;
            _items.Clear();
            _items.AddRange(rows);
            if (!keepScroll) { _scroll = 0; _cursor = -1; }
            else
            {
                _cursor = curTag == null ? -1 : _items.FindIndex(r => Equals(r.Tag, curTag));
                _scroll = Mathf.Clamp(_scroll, 0, MaxFirst);
            }
            Redraw();
        }

        public void ScrollBy(float rows) { _scroll = Mathf.Clamp(_scroll + rows, 0, MaxFirst); Redraw(); }

        public bool Key(string key)
        {
            if (_items.Count == 0) return false;
            int page = Mathf.Max(1, Rows - 1);
            switch (key)
            {
                case "up": Move(_cursor < 0 ? Mathf.FloorToInt(_scroll) : _cursor - 1, -1); return true;
                case "down": Move(_cursor < 0 ? Mathf.FloorToInt(_scroll) : _cursor + 1, 1); return true;
                case "pageup": Move(Mathf.Max(0, _cursor - page), -1); return true;
                case "pagedown": Move(Mathf.Min(_items.Count - 1, Mathf.Max(0, _cursor) + page), 1); return true;
                case "home": Move(0, 1); return true;
                case "end": Move(_items.Count - 1, -1); return true;
                case "enter": if (_cursor < 0 || _cursor >= _items.Count || _items[_cursor].Header) return false; OnPick?.Invoke(_items[_cursor], _cursor); return true;
            }
            return false;
        }

        /// Moves the cursor to `i` (skipping section headers in direction `dir`) and scrolls it into view.
        private void Move(int i, int dir)
        {
            i = Mathf.Clamp(i, 0, _items.Count - 1);
            while (i >= 0 && i < _items.Count && _items[i].Header) i += dir;
            if (i < 0 || i >= _items.Count) { i = Mathf.Clamp(i, 0, _items.Count - 1); while (i >= 0 && i < _items.Count && _items[i].Header) i -= dir; }
            if (i < 0 || i >= _items.Count) return;
            _cursor = i;
            if (i < _scroll) _scroll = i;
            else if (i >= _scroll + Rows) _scroll = Mathf.Min(MaxFirst, i - Rows + 1);
            Redraw();
        }

        public void Pick(int i) { if (i < 0 || i >= _items.Count || _items[i].Header) return; _cursor = i; Redraw(); OnPick?.Invoke(_items[i], i); }

        protected override void Draw()
        {
            var t = Theme;
            if (_items.Count == 0)
            {
                Txt(new Rect(Rect.x + 0.02f, Rect.y, Rect.width - 0.04f, Mathf.Min(Rect.height, 0.12f)), UiViz.NoParse(Empty), t.SmallSize * 1.15f, t.TextDim, TextAlignmentOptions.Center, null, true);
                return;
            }
            int first = Mathf.Clamp(Mathf.FloorToInt(_scroll), 0, MaxFirst);
            float adv = Theme.FontSizeFor(DetailSize) * 0.1f * 0.602f;
            for (int i = 0; i < Rows && first + i < _items.Count; i++)
            {
                int idx = first + i;
                var it = _items[idx];
                float y = Rect.y + i * RowHeight;
                var r = new Rect(Rect.x, y + 0.0015f, Rect.width - 0.010f, RowHeight - 0.003f);
                if (it.Header)
                {
                    Txt(new Rect(r.x + 0.006f, y + RowHeight * 0.25f, r.width - 0.012f, RowHeight * 0.75f), UiViz.NoParse(it.Name), t.SmallSize, t.TextDim, TextAlignmentOptions.MidlineLeft, t.Display);
                    Flat(new Rect(r.x + 0.006f, y + RowHeight - 0.002f, r.width - 0.012f, 0.0008f), new Color(t.Frame.r, t.Frame.g, t.Frame.b, 0.5f));
                    continue;
                }
                bool sel = idx == _cursor, hov = idx == _hover;
                Box(r, sel ? t.Accent : t.AccentSoft, 0.004f, 3, sel ? 0.75f : hov ? 0.3f : 0f, 0.003f);
                float x = r.x + 0.008f;
                if (!string.IsNullOrEmpty(it.Icon))
                {
                    Txt(new Rect(x, r.y, 0.024f, r.height), it.Icon, NameSize, it.IconColor ?? t.TextAccent, TextAlignmentOptions.Center);
                    x += 0.028f;
                }
                float badgeW = string.IsNullOrEmpty(it.Badge) ? 0f : 0.024f;
                float rightW = string.IsNullOrEmpty(it.Right) ? 0f : Mathf.Min(r.width * 0.42f, ChatTextLength(it.Right) * adv + 0.01f);
                float nameW = r.xMax - x - badgeW - rightW - 0.010f;
                Color nameCol = it.NameColor ?? (it.Dim ? t.TextDim : t.Text);
                if (NameColumn > 0f)
                {
                    Txt(new Rect(x, r.y, NameColumn, r.height), UiViz.NoParse(it.Name), DetailSize, t.TextDim, TextAlignmentOptions.MidlineLeft, t.Display);
                    Txt(new Rect(x + NameColumn, r.y, nameW - NameColumn, r.height), UiViz.NoParse(it.Detail), NameSize, nameCol);
                }
                else
                {
                    string s = UiViz.NoParse(it.Name) + (string.IsNullOrEmpty(it.Detail) ? "" : $"<size=84%><color=#{UiViz.Hex(t.TextDim)}>   {UiViz.NoParse(it.Detail)}</color></size>");
                    Txt(new Rect(x, r.y, nameW, r.height), s, NameSize, nameCol);
                }
                if (rightW > 0f) Txt(new Rect(r.xMax - badgeW - rightW - 0.004f, r.y, rightW, r.height), it.Right, DetailSize, t.TextDim, TextAlignmentOptions.MidlineRight, t.Mono);
                if (badgeW > 0f) Txt(new Rect(r.xMax - badgeW - 0.002f, r.y, badgeW, r.height), "<b>" + UiViz.NoParse(it.Badge) + "</b>", NameSize, it.BadgeColor ?? t.TextDim, TextAlignmentOptions.Center, t.Mono);
            }
            if (_items.Count > Rows)
            {
                float h = Mathf.Max(0.012f, Rect.height * Rows / _items.Count), pos = MaxFirst <= 0 ? 0f : first / (float)MaxFirst;
                Flat(new Rect(Rect.xMax - 0.004f, Rect.y + (Rect.height - h) * pos, 0.003f, h), new Color(t.Accent.r, t.Accent.g, t.Accent.b, 0.6f));
            }
        }

        private static int ChatTextLength(string rich) => ChatText.Strip(rich ?? "").Length;

        private void Flat(Rect r, Color c) => AddShape(Panel.Shape(UiShapeKind.Flat, r, c, 0f, 0f, 0.0004f, 0f));

        private int IndexAt(Vector2 p)
        {
            if (!Rect.Contains(p)) return -1;
            int i = Mathf.FloorToInt(_scroll) + Mathf.FloorToInt((p.y - Rect.y) / RowHeight);
            return i >= 0 && i < _items.Count ? i : -1;
        }

        public override void OnPress(UiPointerEvent e) { _dragStart = _scroll; _dragged = false; }

        public override void OnDrag(UiPointerEvent e)
        {
            float dy = e.Point.y - e.PressPoint.y;
            if (Mathf.Abs(dy) > RowHeight * 0.4f) _dragged = true;
            if (_dragged)
            {
                float s = Mathf.Clamp(_dragStart - dy / RowHeight, 0, MaxFirst);
                bool changed = Mathf.FloorToInt(s) != Mathf.FloorToInt(_scroll);
                _scroll = s;
                if (changed) Redraw();
            }
        }

        public override void OnHover(UiPointerEvent e) { int h = IndexAt(e.Point); if (h != _hover) { _hover = h; Redraw(); } }
        protected override void OnInteractionChanged() { if (!Hover && _hover != -1) { _hover = -1; Redraw(); } }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragged && inside) OnClick(e);
            _dragged = false;
        }

        public override void OnClick(UiPointerEvent e) => Pick(IndexAt(e.PressPoint));
    }

    /// A breadcrumb (FILES): the project folder's name, then each folder down to the one shown; a tap on a part goes
    /// back up to it. Long paths keep the first and the last parts.
    public sealed class UiCrumbs : UiCanvas
    {
        public readonly List<string> Parts = new List<string>();
        public Action<int> OnPick;
        public float TextSize = 0.0145f;
        public override bool Interactive => true;
        private readonly List<(float x0, float x1, int index)> _hits = new List<(float, float, int)>();
        private int _hover = -1;

        public void Set(IEnumerable<string> parts) { Parts.Clear(); Parts.AddRange(parts); Redraw(); }

        protected override void Draw()
        {
            _hits.Clear();
            var t = Theme;
            if (Parts.Count == 0) return;
            float W(string s) => UiViz.TextWidth(s, TextSize) * 1.08f + 0.006f;
            float sep = W("›");
            // which parts fit: the first and as many of the last as there is room for
            var shown = new List<int>();
            float total = W(Parts[0]);
            int k = Parts.Count - 1;
            var tail = new List<int>();
            while (k > 0 && total + sep + W(Parts[k]) + (k > 1 ? sep + W("…") : 0f) <= Rect.width) { total += sep + W(Parts[k]); tail.Insert(0, k); k--; }
            shown.Add(0);
            if (k > 0) shown.Add(-1); // "…"
            shown.AddRange(tail);
            float x = Rect.x;
            for (int s = 0; s < shown.Count; s++)
            {
                int i = shown[s];
                if (s > 0) { Txt(new Rect(x, Rect.y, sep, Rect.height), "›", TextSize, t.TextDim, TextAlignmentOptions.Center); x += sep; }
                string label = i < 0 ? "…" : Parts[i];
                float w = Mathf.Min(W(label), Rect.xMax - x);
                bool last = i == Parts.Count - 1;
                if (i >= 0 && !last && _hover == i) AddShape(Panel.Shape(UiShapeKind.Flat, new Rect(x, Rect.yMax - 0.003f, w - 0.004f, 0.0015f), t.Accent, 0f, 0f, 0.0004f, 0f));
                Txt(new Rect(x, Rect.y, w, Rect.height), UiViz.NoParse(label), TextSize, last ? t.Text : i < 0 ? t.TextDim : t.TextAccent, TextAlignmentOptions.MidlineLeft, i == 0 ? t.Display : null);
                if (i >= 0 && !last) _hits.Add((x, x + w, i));
                x += w;
            }
        }

        private int At(Vector2 p) { foreach (var h in _hits) if (p.x >= h.x0 && p.x <= h.x1 && Rect.Contains(p)) return h.index; return -1; }
        public override void OnHover(UiPointerEvent e) { int h = At(e.Point); if (h != _hover) { _hover = h; Redraw(); } }
        protected override void OnInteractionChanged() { if (!Hover && _hover != -1) { _hover = -1; Redraw(); } }
        public override void OnClick(UiPointerEvent e) { int i = At(e.PressPoint); if (i >= 0) OnPick?.Invoke(i); }
        public void PickForTest(int i) => OnPick?.Invoke(i);
    }

    /// THREAD's expand handle on its right edge: a tall command-card plate with a chevron (› opens the sidebar, ‹ folds
    /// it), the word PROJECT down its length and the number of changed files.
    public sealed class UiEdgeHandle : UiCanvas
    {
        public bool Open;
        public string Count = "";
        public Action OnClickAction;
        public override bool Interactive => true;

        public void Set(bool open, string count) { if (Open == open && Count == (count ?? "")) return; Open = open; Count = count ?? ""; Redraw(); }

        protected override void Draw()
        {
            var t = Theme;
            Box(Rect, Open ? t.Accent : t.AccentSoft, 0.006f, 0, Hover ? (Pressed ? 1f : 0.55f) : Open ? 0.35f : 0.12f, 0.004f);
            float cx = Rect.x, w = Rect.width;
            Txt(new Rect(cx, Rect.y + 0.008f, w, 0.04f), Open ? "‹" : "›", 0.034f, Hover || Open ? t.Text : t.TextAccent, TextAlignmentOptions.Center);
            Txt(new Rect(cx, Rect.y + 0.05f, w, Mathf.Max(0.05f, Rect.height - 0.12f)), "P\nR\nO\nJ\nE\nC\nT", 0.0135f, Open ? t.TextAccent : t.TextDim, TextAlignmentOptions.Top, t.Display);
            if (Count.Length > 0)
            {
                var pill = new Rect(cx + 0.004f, Rect.yMax - 0.034f, w - 0.008f, 0.024f);
                AddShape(Panel.Shape(UiShapeKind.Flat, pill, t.Warn, 0.004f, 0f, 0.0005f, 0f));
                Txt(pill, Count, 0.0135f, UiTheme.Hex(0x101820), TextAlignmentOptions.Center, t.Mono);
            }
        }

        protected override void OnInteractionChanged() => Redraw();
        public override void OnClick(UiPointerEvent e) => OnClickAction?.Invoke();
    }
}
