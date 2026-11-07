using System;
using System.Collections.Generic;
using System.Text;

namespace MetaCodex.Terminal
{
    [Flags]
    public enum CellAttr : ushort
    {
        None = 0,
        Bold = 1 << 0,
        Dim = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Blink = 1 << 4,
        Inverse = 1 << 5,
        Hidden = 1 << 6,
        Strike = 1 << 7,
        WideHead = 1 << 8, // first cell of a double-width character
        WideTail = 1 << 9, // second cell of it (draws nothing)
    }

    /// One character cell. Colours: -1 default, 0..255 palette index, 0x1000000 | 0xRRGGBB true colour.
    public struct Cell
    {
        public int Rune; // Unicode scalar value, 0 = blank
        public int Fg;
        public int Bg;
        public CellAttr Attr;

        public static Cell BlankWith(int bg) => new Cell { Rune = 0, Fg = VtTerminal.DefaultColor, Bg = bg, Attr = CellAttr.None };
        public bool IsBlank => (Rune == 0 || Rune == ' ') && Bg == VtTerminal.DefaultColor && (Attr & (CellAttr.Inverse | CellAttr.Underline | CellAttr.Strike)) == 0;
    }

    public enum CursorShape { Block, Underline, Bar }

    /// An xterm-compatible terminal emulator: bytes in (UTF-8 + escape sequences), a screen of cells out,
    /// replies (device reports) queued for the program. Pure C#, no Unity: the view polls it on the main
    /// thread, tests run it on desktop .NET. Covers what shells, vim/less/htop and Ink/Bubble Tea TUIs use:
    /// cursor and erase controls, scroll regions, alternate screen, SGR with 256/true colour, DEC modes
    /// (app cursor keys, bracketed paste, mouse flags, synchronized output), device/mode/size reports.
    public sealed class VtTerminal
    {
        public const int DefaultColor = -1;
        public const int TrueColorFlag = 0x1000000;

        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public int CursorRow => _cy;
        public int CursorCol => Math.Min(_cx, Cols - 1);
        public bool CursorVisible { get; private set; } = true;
        public CursorShape CursorShape { get; private set; } = CursorShape.Block;
        public bool CursorBlink { get; private set; } = true;
        public bool AppCursorKeys { get; private set; }
        public bool AppKeypad { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool ReverseVideo { get; private set; }
        public bool AltScreen => _screen == _alt;
        public bool SyncOutput { get; private set; } // DECSET 2026: the program is mid-frame, hold the redraw
        public int MouseMode { get; private set; } // 0 off, 9, 1000, 1002, 1003
        public bool MouseSgr { get; private set; }
        public bool FocusEvents { get; private set; }
        public string Title { get; private set; } = "";
        public string WorkingDirectory { get; private set; } = "";
        public int BellCount { get; private set; }
        public long Version { get; private set; } // bumps on every visible change
        public int ScrollbackCount => _sbCount;
        public int ScrollbackCapacity { get; }
        public int DefaultFgRgb { get; set; } = 0xD8DEE9;
        public int DefaultBgRgb { get; set; } = 0x10141C;
        public int CursorRgb { get; set; } = 0xE5C07B;

        private Cell[][] _main, _alt, _screen;
        private bool[] _dirty;
        private Cell[][] _scrollback;
        private int _sbHead, _sbCount;
        private int _cx, _cy;
        private bool _wrapPending;
        private int _top, _bottom; // scroll region, inclusive
        private bool _autowrap = true, _originMode, _insertMode, _newlineMode;
        private int _fg = DefaultColor, _bg = DefaultColor;
        private CellAttr _attr;
        private bool[] _tabs;
        private readonly char[] _charsets = { 'B', 'B' };
        private int _gl; // 0 = G0, 1 = G1
        private int _lastPrinted = ' ';
        private readonly int[] _palette = new int[256];

        private struct Saved
        {
            public int X, Y, Fg, Bg, Gl;
            public CellAttr Attr;
            public bool Origin, Valid;
            public char G0, G1;
        }
        private Saved _savedMain, _savedAlt;

        private enum State { Ground, Escape, EscInter, Csi, CsiIgnore, Osc, OscEsc, Str, StrEsc }
        private State _state;
        private char _escInter;
        private readonly int[] _p = new int[32];
        private readonly bool[] _pColon = new bool[32];
        private int _pn;
        private bool _pHasDigit;
        private char _csiPrivate, _csiInter;
        private readonly StringBuilder _osc = new StringBuilder();
        private bool _oscBel;

        private int _u8Need, _u8Cp, _u8Min;
        private readonly List<byte> _replies = new List<byte>();

        public VtTerminal(int rows, int cols, int scrollback = 2000)
        {
            if (rows < 1 || cols < 2) throw new ArgumentException("terminal too small");
            ScrollbackCapacity = Math.Max(0, scrollback);
            _scrollback = new Cell[ScrollbackCapacity][];
            ResetPalette();
            Rows = rows;
            Cols = cols;
            _main = NewScreen(rows, cols);
            _alt = NewScreen(rows, cols);
            _screen = _main;
            _dirty = new bool[rows];
            ResetTabs();
            _top = 0;
            _bottom = rows - 1;
            MarkAllDirty();
        }

        // ---------------------------------------------------------------- public API

        public Cell[] GetRow(int row) => _screen[row];
        public bool IsRowDirty(int row) => _dirty[row];
        public void ClearDirty() { Array.Clear(_dirty, 0, _dirty.Length); }

        /// Scrollback line i (0 = oldest).
        public Cell[] GetScrollbackLine(int i)
        {
            if (i < 0 || i >= _sbCount) return null;
            int idx = (_sbHead - _sbCount + i + ScrollbackCapacity) % ScrollbackCapacity;
            return _scrollback[idx];
        }

        /// Takes the bytes the terminal wants to send to the program (device reports), or null.
        public byte[] TakeReplies()
        {
            if (_replies.Count == 0) return null;
            var r = _replies.ToArray();
            _replies.Clear();
            return r;
        }

        public void Feed(byte[] data) => Feed(data, 0, data.Length);

        public void Feed(byte[] data, int offset, int count)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                if (_u8Need == 0)
                {
                    if (b < 0x80) { Consume(b); continue; }
                    if ((b & 0xE0) == 0xC0) { _u8Need = 1; _u8Cp = b & 0x1F; _u8Min = 0x80; }
                    else if ((b & 0xF0) == 0xE0) { _u8Need = 2; _u8Cp = b & 0x0F; _u8Min = 0x800; }
                    else if ((b & 0xF8) == 0xF0) { _u8Need = 3; _u8Cp = b & 0x07; _u8Min = 0x10000; }
                    else Consume(0xFFFD);
                }
                else
                {
                    if ((b & 0xC0) != 0x80)
                    {
                        _u8Need = 0;
                        Consume(0xFFFD);
                        i--; // re-read this byte as a fresh start
                        continue;
                    }
                    _u8Cp = (_u8Cp << 6) | (b & 0x3F);
                    if (--_u8Need == 0)
                    {
                        int cp = _u8Cp;
                        if (cp < _u8Min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
                        Consume(cp);
                    }
                }
            }
        }

        public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

        public void Resize(int rows, int cols)
        {
            if (rows < 1 || cols < 2 || (rows == Rows && cols == Cols)) return;
            // keep the cursor line on screen: lines that fall off the top go to the scrollback (main screen only)
            int shift = Math.Max(0, _cy - (rows - 1));
            bool alt = AltScreen;
            if (!alt) for (int i = 0; i < shift; i++) PushScrollback(_main[i]);
            _main = ResizeScreen(_main, rows, cols, alt ? 0 : shift);
            _alt = ResizeScreen(_alt, rows, cols, alt ? shift : 0);
            _screen = alt ? _alt : _main;
            Rows = rows;
            Cols = cols;
            _dirty = new bool[rows];
            _cy = Math.Max(0, Math.Min(rows - 1, _cy - shift));
            _cx = Math.Min(_cx, cols - 1);
            _wrapPending = false;
            _top = 0;
            _bottom = rows - 1;
            var tabs = new bool[cols];
            for (int c = 0; c < cols; c++) tabs[c] = c < _tabs.Length ? _tabs[c] : c % 8 == 0;
            _tabs = tabs;
            MarkAllDirty();
        }

        /// Plain text of the screen, one line per row, trailing blanks trimmed (tests, control port).
        public string DumpScreen()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < Rows; r++)
            {
                sb.Append(RowText(_screen[r]));
                if (r < Rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string RowText(Cell[] row)
        {
            var sb = new StringBuilder(row.Length);
            for (int c = 0; c < row.Length; c++)
            {
                if ((row[c].Attr & CellAttr.WideTail) != 0) continue;
                int r = row[c].Rune;
                AppendRune(sb, r == 0 ? ' ' : r);
            }
            int end = sb.Length;
            while (end > 0 && sb[end - 1] == ' ') end--;
            sb.Length = end;
            return sb.ToString();
        }

        public static void AppendRune(StringBuilder sb, int r)
        {
            if (r < 0x10000) sb.Append((char)r);
            else sb.Append(char.ConvertFromUtf32(r));
        }

        /// 0xRRGGBB for a cell colour (-1 = the default fg or bg).
        public int ResolveRgb(int color, bool foreground, bool bold)
        {
            if (color == DefaultColor) return foreground ? DefaultFgRgb : DefaultBgRgb;
            if ((color & TrueColorFlag) != 0) return color & 0xFFFFFF;
            if (foreground && bold && color < 8) color += 8; // bold brightens the base colours, as xterm does
            return _palette[color & 0xFF];
        }

        // ---------------------------------------------------------------- parser

        private void Consume(int c)
        {
            // controls act in any state except inside strings (OSC/DCS take BEL/ESC themselves)
            switch (_state)
            {
                case State.Ground:
                    if (c < 0x20 || c == 0x7F) { Control(c); return; }
                    if (c >= 0x80 && c < 0xA0) return; // C1 in UTF-8 mode: ignored
                    Print(c);
                    return;
                case State.Escape:
                    EscapeChar(c);
                    return;
                case State.EscInter:
                    if (c < 0x20) { Control(c); return; }
                    EscDispatch(_escInter, c);
                    _state = State.Ground;
                    return;
                case State.Csi:
                    CsiChar(c);
                    return;
                case State.CsiIgnore:
                    if (c >= 0x40 && c <= 0x7E) _state = State.Ground;
                    else if (c < 0x20) Control(c);
                    return;
                case State.Osc:
                    if (c == 0x07) { _oscBel = true; OscDispatch(); _state = State.Ground; }
                    else if (c == 0x1B) _state = State.OscEsc;
                    else if (c == 0x9C) { _oscBel = false; OscDispatch(); _state = State.Ground; }
                    else if (_osc.Length < 8192) AppendRune(_osc, c);
                    return;
                case State.OscEsc:
                    if (c == '\\') { _oscBel = false; OscDispatch(); _state = State.Ground; }
                    else { _state = State.Ground; Consume(0x1B); Consume(c); }
                    return;
                case State.Str: // DCS, SOS, PM, APC: swallowed until ST
                    if (c == 0x1B) _state = State.StrEsc;
                    else if (c == 0x07 || c == 0x9C) _state = State.Ground;
                    return;
                case State.StrEsc:
                    _state = c == '\\' ? State.Ground : State.Str;
                    return;
            }
        }

        private void Control(int c)
        {
            switch (c)
            {
                case 0x07: BellCount++; Version++; break;
                case 0x08:
                    _cx = Math.Max(0, Math.Min(_cx, Cols - 1) - 1);
                    _wrapPending = false;
                    break;
                case 0x09: TabForward(1); break;
                case 0x0A: case 0x0B: case 0x0C:
                    LineFeed();
                    if (_newlineMode) _cx = 0;
                    break;
                case 0x0D: _cx = 0; _wrapPending = false; break;
                case 0x0E: _gl = 1; break;
                case 0x0F: _gl = 0; break;
                case 0x18: case 0x1A: _state = State.Ground; break;
                case 0x1B: _state = State.Escape; _escInter = '\0'; break;
            }
        }

        private void EscapeChar(int c)
        {
            if (c < 0x20) { Control(c); return; }
            _state = State.Ground;
            switch (c)
            {
                case '[':
                    _state = State.Csi;
                    _pn = 0; _pHasDigit = false; _csiPrivate = '\0'; _csiInter = '\0';
                    _p[0] = -1; _pColon[0] = false;
                    return;
                case ']': _state = State.Osc; _osc.Clear(); return;
                case 'P': case 'X': case '^': case '_': _state = State.Str; return;
                case '(': case ')': case '*': case '+': case '#': case '%': case ' ':
                    _escInter = (char)c; _state = State.EscInter; return;
                case '7': SaveCursor(); return;
                case '8': RestoreCursor(); return;
                case 'D': LineFeed(); return;
                case 'E': _cx = 0; LineFeed(); return;
                case 'H': if (_cx < Cols) _tabs[Math.Min(_cx, Cols - 1)] = true; return;
                case 'M': ReverseIndex(); return;
                case 'c': FullReset(); return;
                case '=': AppKeypad = true; return;
                case '>': AppKeypad = false; return;
                case '\\': return; // stray ST
            }
        }

        private void EscDispatch(char inter, int c)
        {
            switch (inter)
            {
                case '(': _charsets[0] = (char)c; break;
                case ')': _charsets[1] = (char)c; break;
                case '#':
                    if (c == '8') // DECALN: fill with E
                    {
                        for (int r = 0; r < Rows; r++)
                        for (int col = 0; col < Cols; col++) _screen[r][col] = new Cell { Rune = 'E', Fg = DefaultColor, Bg = DefaultColor };
                        MarkAllDirty();
                    }
                    break;
            }
        }

        private void CsiChar(int c)
        {
            if (c >= '0' && c <= '9')
            {
                int v = _p[_pn] < 0 ? 0 : _p[_pn];
                if (v < 100000) v = v * 10 + (c - '0');
                _p[_pn] = v;
                _pHasDigit = true;
                return;
            }
            if (c == ';' || c == ':')
            {
                if (_pn < _p.Length - 1)
                {
                    _pn++;
                    _p[_pn] = -1;
                    _pColon[_pn] = c == ':';
                }
                return;
            }
            if (c >= 0x3C && c <= 0x3F) // < = > ?
            {
                if (_pn == 0 && !_pHasDigit && _csiPrivate == '\0') _csiPrivate = (char)c;
                else _state = State.CsiIgnore;
                return;
            }
            if (c >= 0x20 && c <= 0x2F) { _csiInter = (char)c; return; }
            if (c >= 0x40 && c <= 0x7E)
            {
                _state = State.Ground;
                int count = _pn + 1;
                CsiDispatch((char)c, count);
                return;
            }
            if (c < 0x20) { Control(c); return; }
            _state = State.CsiIgnore;
        }

        private int P(int i, int def) => i <= _pn && _p[i] >= 0 ? _p[i] : def;
        private int P1(int i) { int v = P(i, 1); return v == 0 ? 1 : v; }

        private void CsiDispatch(char f, int count)
        {
            if (_csiPrivate == '?')
            {
                switch (f)
                {
                    case 'h': for (int i = 0; i < count; i++) SetDecMode(P(i, 0), true); return;
                    case 'l': for (int i = 0; i < count; i++) SetDecMode(P(i, 0), false); return;
                    case 'J': EraseDisplay(P(0, 0)); return;
                    case 'K': EraseLine(P(0, 0)); return;
                    case 'n': if (P(0, 0) == 6) Reply($"\x1b[?{CursorReportRow()};{CursorCol + 1}R"); return;
                    case 'p': if (_csiInter == '$') ReplyDecMode(P(0, 0)); return;
                    case 'u': return; // kitty keyboard query: no reply = unsupported (DA1 answers first)
                    case 's': case 'r': return; // xterm save/restore modes
                }
                return;
            }
            if (_csiPrivate == '>')
            {
                switch (f)
                {
                    case 'c': Reply("\x1b[>0;10;1c"); return; // DA2
                    case 'q': Reply("\x1bP>|metacodex 0.1\x1b\\"); return; // XTVERSION
                }
                return; // modifyOtherKeys (m), kitty push (u), ...
            }
            if (_csiPrivate != '\0') return; // '<' kitty pop, '=' DA3, ...

            switch (f)
            {
                case '@': InsertChars(P1(0)); break;
                case 'A': CursorUp(P1(0)); break;
                case 'B': case 'e': CursorDown(P1(0)); break;
                case 'C': case 'a': CursorForward(P1(0)); break;
                case 'D': CursorBack(P1(0)); break;
                case 'E': CursorDown(P1(0)); _cx = 0; break;
                case 'F': CursorUp(P1(0)); _cx = 0; break;
                case 'G': case '`': _cx = Clamp(P1(0) - 1, 0, Cols - 1); _wrapPending = false; break;
                case 'H': case 'f': SetCursor(P1(0) - 1, P1(1) - 1); break;
                case 'I': TabForward(P1(0)); break;
                case 'J': EraseDisplay(P(0, 0)); break;
                case 'K': EraseLine(P(0, 0)); break;
                case 'L': InsertLines(P1(0)); break;
                case 'M': DeleteLines(P1(0)); break;
                case 'P': DeleteChars(P1(0)); break;
                case 'S': ScrollUp(_top, _bottom, P1(0)); break;
                case 'T': if (count <= 1) ScrollDown(_top, _bottom, P1(0)); break;
                case 'X': EraseChars(P1(0)); break;
                case 'Z': TabBack(P1(0)); break;
                case 'b': for (int i = 0, n = Math.Min(P1(0), 65535); i < n; i++) Print(_lastPrinted); break;
                case 'c': if (P(0, 0) == 0) Reply("\x1b[?62;22c"); break; // DA1: VT220 with ANSI colour
                case 'd': SetCursorRow(P1(0) - 1); break;
                case 'g':
                    int mode = P(0, 0);
                    if (mode == 0) _tabs[Math.Min(_cx, Cols - 1)] = false;
                    else if (mode == 3) Array.Clear(_tabs, 0, _tabs.Length);
                    break;
                case 'h': for (int i = 0; i < count; i++) SetAnsiMode(P(i, 0), true); break;
                case 'l': for (int i = 0; i < count; i++) SetAnsiMode(P(i, 0), false); break;
                case 'm': Sgr(count); break;
                case 'n':
                    if (P(0, 0) == 5) Reply("\x1b[0n");
                    else if (P(0, 0) == 6) Reply($"\x1b[{CursorReportRow()};{CursorCol + 1}R");
                    break;
                case 'p': if (_csiInter == '!') SoftReset(); break;
                case 'q':
                    if (_csiInter == ' ')
                    {
                        int s = P(0, 1);
                        CursorShape = s <= 2 ? CursorShape.Block : s <= 4 ? CursorShape.Underline : CursorShape.Bar;
                        CursorBlink = s == 0 || s % 2 == 1;
                        Version++;
                    }
                    break;
                case 'r':
                    if (_csiInter != '\0') break;
                    int top = P1(0) - 1, bottom = P(1, Rows);
                    if (bottom == 0) bottom = Rows;
                    bottom = Math.Min(bottom, Rows) - 1;
                    if (top < bottom) { _top = top; _bottom = bottom; SetCursor(0, 0); }
                    break;
                case 's': if (_csiInter == '\0') SaveCursor(); break;
                case 't': WindowOp(P(0, 0)); break;
                case 'u': if (_csiInter == '\0') RestoreCursor(); break;
            }
        }

        private void WindowOp(int op)
        {
            switch (op)
            {
                case 18: Reply($"\x1b[8;{Rows};{Cols}t"); break;
                case 14: Reply($"\x1b[4;{Rows * 24};{Cols * 12}t"); break;
                case 16: Reply("\x1b[6;24;12t"); break;
            }
        }

        private int CursorReportRow() => (_originMode ? _cy - _top : _cy) + 1;

        private void SetDecMode(int mode, bool on)
        {
            switch (mode)
            {
                case 1: AppCursorKeys = on; break;
                case 5: ReverseVideo = on; MarkAllDirty(); break;
                case 6: _originMode = on; SetCursor(0, 0); break;
                case 7: _autowrap = on; if (!on) _wrapPending = false; break;
                case 12: CursorBlink = on; Version++; break;
                case 25: CursorVisible = on; Version++; break;
                case 9: case 1000: case 1002: case 1003: MouseMode = on ? mode : 0; break;
                case 1006: MouseSgr = on; break;
                case 1004: FocusEvents = on; break;
                case 2004: BracketedPaste = on; break;
                case 2026: SyncOutput = on; if (!on) Version++; break;
                case 47: case 1047: SwitchScreen(on, clearAlt: mode == 1047 && on, saveCursor: false); break;
                case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
                case 1049: SwitchScreen(on, clearAlt: on, saveCursor: true); break;
            }
        }

        private int DecModeState(int mode)
        {
            switch (mode)
            {
                case 1: return AppCursorKeys ? 1 : 2;
                case 5: return ReverseVideo ? 1 : 2;
                case 6: return _originMode ? 1 : 2;
                case 7: return _autowrap ? 1 : 2;
                case 12: return CursorBlink ? 1 : 2;
                case 25: return CursorVisible ? 1 : 2;
                case 9: case 1000: case 1002: case 1003: return MouseMode == mode ? 1 : 2;
                case 1004: return FocusEvents ? 1 : 2;
                case 1006: return MouseSgr ? 1 : 2;
                case 47: case 1047: case 1049: return AltScreen ? 1 : 2;
                case 2004: return BracketedPaste ? 1 : 2;
                case 2026: return SyncOutput ? 1 : 2;
                default: return 0;
            }
        }

        private void ReplyDecMode(int mode) => Reply($"\x1b[?{mode};{DecModeState(mode)}$y");

        private void SetAnsiMode(int mode, bool on)
        {
            if (mode == 4) _insertMode = on;
            else if (mode == 20) _newlineMode = on;
        }

        private void Sgr(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int v = P(i, 0);
                // skip colon sub-parameters of a code we don't use (e.g. 4:3 curly underline)
                switch (v)
                {
                    case 0: _attr = CellAttr.None; _fg = DefaultColor; _bg = DefaultColor; break;
                    case 1: _attr |= CellAttr.Bold; break;
                    case 2: _attr |= CellAttr.Dim; break;
                    case 3: _attr |= CellAttr.Italic; break;
                    case 4:
                        if (i + 1 <= _pn && _pColon[i + 1]) { if (P(i + 1, 1) == 0) _attr &= ~CellAttr.Underline; else _attr |= CellAttr.Underline; i++; }
                        else _attr |= CellAttr.Underline;
                        break;
                    case 5: case 6: _attr |= CellAttr.Blink; break;
                    case 7: _attr |= CellAttr.Inverse; break;
                    case 8: _attr |= CellAttr.Hidden; break;
                    case 9: _attr |= CellAttr.Strike; break;
                    case 21: _attr |= CellAttr.Underline; break;
                    case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                    case 23: _attr &= ~CellAttr.Italic; break;
                    case 24: _attr &= ~CellAttr.Underline; break;
                    case 25: _attr &= ~CellAttr.Blink; break;
                    case 27: _attr &= ~CellAttr.Inverse; break;
                    case 28: _attr &= ~CellAttr.Hidden; break;
                    case 29: _attr &= ~CellAttr.Strike; break;
                    case 38: _fg = ExtColor(ref i, _fg); break;
                    case 39: _fg = DefaultColor; break;
                    case 48: _bg = ExtColor(ref i, _bg); break;
                    case 49: _bg = DefaultColor; break;
                    case 58: ExtColor(ref i, 0); break; // underline colour: parsed, not drawn
                    default:
                        if (v >= 30 && v <= 37) _fg = v - 30;
                        else if (v >= 40 && v <= 47) _bg = v - 40;
                        else if (v >= 90 && v <= 97) _fg = v - 90 + 8;
                        else if (v >= 100 && v <= 107) _bg = v - 100 + 8;
                        break;
                }
            }
        }

        /// 38/48 colour: "5;n", "2;r;g;b", or the colon forms "5:n", "2::r:g:b", "2:r:g:b".
        private int ExtColor(ref int i, int current)
        {
            bool colon = i + 1 <= _pn && _pColon[i + 1];
            int kind = P(i + 1, -1);
            if (kind == 5)
            {
                int n = P(i + 2, -1);
                i += 2;
                return n >= 0 && n <= 255 ? n : current;
            }
            if (kind == 2)
            {
                if (colon)
                {
                    // count colon sub-parameters after the "2": 4 = colour space id + rgb, 3 = rgb
                    int subs = 0;
                    for (int k = i + 2; k <= _pn && _pColon[k]; k++) subs++;
                    int first = subs >= 4 ? i + 3 : i + 2;
                    int r = P(first, 0), g = P(first + 1, 0), b = P(first + 2, 0);
                    i += 1 + subs;
                    return TrueColorFlag | (Clamp(r, 0, 255) << 16) | (Clamp(g, 0, 255) << 8) | Clamp(b, 0, 255);
                }
                int rr = P(i + 2, 0), gg = P(i + 3, 0), bb = P(i + 4, 0);
                i += 4;
                return TrueColorFlag | (Clamp(rr, 0, 255) << 16) | (Clamp(gg, 0, 255) << 8) | Clamp(bb, 0, 255);
            }
            if (kind >= 0) i += 1;
            return current;
        }

        private void OscDispatch()
        {
            string s = _osc.ToString();
            int semi = s.IndexOf(';');
            string code = semi < 0 ? s : s.Substring(0, semi);
            string arg = semi < 0 ? "" : s.Substring(semi + 1);
            switch (code)
            {
                case "0": case "2": Title = arg; Version++; break;
                case "7": WorkingDirectory = arg; break;
                case "4": OscPalette(arg); break;
                case "10": OscColor(10, arg); break;
                case "11": OscColor(11, arg); break;
                case "12": OscColor(12, arg); break;
                case "104": ResetPalette(); MarkAllDirty(); break;
            }
        }

        private void OscColor(int which, string arg)
        {
            if (arg == "?")
            {
                int rgb = which == 10 ? DefaultFgRgb : which == 11 ? DefaultBgRgb : CursorRgb;
                Reply($"\x1b]{which};{RgbSpec(rgb)}{(_oscBel ? "\x07" : "\x1b\\")}");
                return;
            }
            if (TryParseColor(arg, out int value))
            {
                if (which == 10) DefaultFgRgb = value;
                else if (which == 11) DefaultBgRgb = value;
                else CursorRgb = value;
                MarkAllDirty();
            }
        }

        private void OscPalette(string arg)
        {
            var parts = arg.Split(';');
            for (int k = 0; k + 1 < parts.Length; k += 2)
            {
                if (!int.TryParse(parts[k], out int n) || n < 0 || n > 255) continue;
                if (parts[k + 1] == "?") Reply($"\x1b]4;{n};{RgbSpec(_palette[n])}{(_oscBel ? "\x07" : "\x1b\\")}");
                else if (TryParseColor(parts[k + 1], out int v)) { _palette[n] = v; MarkAllDirty(); }
            }
        }

        private static string RgbSpec(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
        }

        private static bool TryParseColor(string s, out int rgb)
        {
            rgb = 0;
            try
            {
                if (s.StartsWith("#") && s.Length == 7) { rgb = Convert.ToInt32(s.Substring(1), 16); return true; }
                if (s.StartsWith("rgb:"))
                {
                    var c = s.Substring(4).Split('/');
                    if (c.Length != 3) return false;
                    int Comp(string h) { int v = Convert.ToInt32(h, 16); return h.Length > 2 ? v >> ((h.Length - 2) * 4) : h.Length == 1 ? v * 17 : v; }
                    rgb = (Comp(c[0]) << 16) | (Comp(c[1]) << 8) | Comp(c[2]);
                    return true;
                }
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
            return false;
        }

        private void Reply(string s) => _replies.AddRange(Encoding.UTF8.GetBytes(s));

        // ---------------------------------------------------------------- screen operations

        private void Print(int c)
        {
            char cs = _charsets[_gl];
            if (cs == '0' && c >= 0x5F && c <= 0x7E) c = DecGraphics[c - 0x5F];
            int w = Wcwidth.Of(c);
            if (w == 0)
            {
                // combining mark or joiner: not drawn on its own (a single code point per cell)
                return;
            }
            _lastPrinted = c;
            if (_wrapPending && _autowrap)
            {
                _cx = 0;
                LineFeed();
            }
            _wrapPending = false;
            if (w == 2 && _cx >= Cols - 1)
            {
                if (_autowrap && Cols > 1)
                {
                    if (_cx == Cols - 1) PutBlank(_cy, _cx);
                    _cx = 0;
                    LineFeed();
                }
                else _cx = Math.Max(0, Cols - 2);
            }
            var row = _screen[_cy];
            if (_insertMode) ShiftRight(row, _cx, w);
            ClearWideAt(row, _cx);
            if (w == 2) ClearWideAt(row, _cx + 1);
            row[_cx] = new Cell { Rune = c, Fg = _fg, Bg = _bg, Attr = _attr | (w == 2 ? CellAttr.WideHead : CellAttr.None) };
            if (w == 2) row[_cx + 1] = new Cell { Rune = 0, Fg = _fg, Bg = _bg, Attr = _attr | CellAttr.WideTail };
            Touch(_cy);
            _cx += w;
            if (_cx >= Cols)
            {
                _cx = Cols - 1;
                if (_autowrap) _wrapPending = true;
            }
        }

        private void PutBlank(int r, int c) { _screen[r][c] = Cell.BlankWith(_bg); Touch(r); }

        // Overwriting half of a wide character blanks its other half.
        private void ClearWideAt(Cell[] row, int c)
        {
            if (c < 0 || c >= row.Length) return;
            if ((row[c].Attr & CellAttr.WideTail) != 0 && c > 0) row[c - 1] = Cell.BlankWith(row[c - 1].Bg);
            if ((row[c].Attr & CellAttr.WideHead) != 0 && c + 1 < row.Length) row[c + 1] = Cell.BlankWith(row[c + 1].Bg);
        }

        private void ShiftRight(Cell[] row, int at, int n)
        {
            for (int c = row.Length - 1; c >= at + n; c--) row[c] = row[c - n];
            for (int c = at; c < at + n && c < row.Length; c++) row[c] = Cell.BlankWith(_bg);
        }

        private void LineFeed()
        {
            _wrapPending = false;
            if (_cy == _bottom) ScrollUp(_top, _bottom, 1);
            else if (_cy < Rows - 1) _cy++;
        }

        private void ReverseIndex()
        {
            _wrapPending = false;
            if (_cy == _top) ScrollDown(_top, _bottom, 1);
            else if (_cy > 0) _cy--;
        }

        private void ScrollUp(int top, int bottom, int n)
        {
            n = Math.Min(n, bottom - top + 1);
            bool toScrollback = _screen == _main && top == 0;
            for (int k = 0; k < n; k++)
            {
                var first = _screen[top];
                Cell[] reuse = toScrollback ? PushScrollback(first) : first;
                for (int r = top; r < bottom; r++) _screen[r] = _screen[r + 1];
                _screen[bottom] = reuse != null && reuse.Length == Cols ? FillRow(reuse, _bg) : NewRow(Cols, _bg);
            }
            for (int r = top; r <= bottom; r++) _dirty[r] = true;
            Version++;
        }

        private void ScrollDown(int top, int bottom, int n)
        {
            n = Math.Min(n, bottom - top + 1);
            for (int k = 0; k < n; k++)
            {
                var last = _screen[bottom];
                for (int r = bottom; r > top; r--) _screen[r] = _screen[r - 1];
                _screen[top] = FillRow(last, _bg);
            }
            for (int r = top; r <= bottom; r++) _dirty[r] = true;
            Version++;
        }

        /// Stores a line that scrolled off the top; returns the evicted oldest line for reuse (or null).
        private Cell[] PushScrollback(Cell[] row)
        {
            if (ScrollbackCapacity == 0) return row;
            Cell[] evicted = _sbCount == ScrollbackCapacity ? _scrollback[_sbHead] : null;
            _scrollback[_sbHead] = row;
            _sbHead = (_sbHead + 1) % ScrollbackCapacity;
            if (_sbCount < ScrollbackCapacity) _sbCount++;
            return evicted;
        }

        private void InsertLines(int n)
        {
            if (_cy < _top || _cy > _bottom) return;
            ScrollDown(_cy, _bottom, n);
            _cx = 0;
            _wrapPending = false;
        }

        private void DeleteLines(int n)
        {
            if (_cy < _top || _cy > _bottom) return;
            // deleting inside the region never feeds the scrollback
            var saved = _screen;
            n = Math.Min(n, _bottom - _cy + 1);
            for (int k = 0; k < n; k++)
            {
                var first = saved[_cy];
                for (int r = _cy; r < _bottom; r++) saved[r] = saved[r + 1];
                saved[_bottom] = FillRow(first, _bg);
            }
            for (int r = _cy; r <= _bottom; r++) _dirty[r] = true;
            Version++;
            _cx = 0;
            _wrapPending = false;
        }

        private void InsertChars(int n)
        {
            var row = _screen[_cy];
            int at = Math.Min(_cx, Cols - 1);
            n = Math.Min(n, Cols - at);
            ClearWideAt(row, at);
            ShiftRight(row, at, n);
            _wrapPending = false;
            Touch(_cy);
        }

        private void DeleteChars(int n)
        {
            var row = _screen[_cy];
            int at = Math.Min(_cx, Cols - 1);
            n = Math.Min(n, Cols - at);
            ClearWideAt(row, at);
            ClearWideAt(row, at + n - 1);
            for (int c = at; c < Cols - n; c++) row[c] = row[c + n];
            for (int c = Cols - n; c < Cols; c++) row[c] = Cell.BlankWith(_bg);
            _wrapPending = false;
            Touch(_cy);
        }

        private void EraseChars(int n)
        {
            var row = _screen[_cy];
            int at = Math.Min(_cx, Cols - 1);
            int end = Math.Min(Cols, at + n);
            ClearWideAt(row, at);
            ClearWideAt(row, end - 1);
            for (int c = at; c < end; c++) row[c] = Cell.BlankWith(_bg);
            _wrapPending = false;
            Touch(_cy);
        }

        private void EraseLine(int mode)
        {
            var row = _screen[_cy];
            int at = Math.Min(_cx, Cols - 1);
            int from = mode == 0 ? at : 0;
            int to = mode == 1 ? at + 1 : Cols;
            if (mode > 2) return;
            ClearWideAt(row, from);
            ClearWideAt(row, to - 1);
            for (int c = from; c < to; c++) row[c] = Cell.BlankWith(_bg);
            _wrapPending = false;
            Touch(_cy);
        }

        private void EraseDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseLine(0);
                    for (int r = _cy + 1; r < Rows; r++) { FillRow(_screen[r], _bg); _dirty[r] = true; }
                    break;
                case 1:
                    EraseLine(1);
                    for (int r = 0; r < _cy; r++) { FillRow(_screen[r], _bg); _dirty[r] = true; }
                    break;
                case 2:
                    for (int r = 0; r < Rows; r++) { FillRow(_screen[r], _bg); _dirty[r] = true; }
                    break;
                case 3:
                    _sbCount = 0;
                    _sbHead = 0;
                    break;
            }
            _wrapPending = false;
            Version++;
        }

        private void CursorUp(int n)
        {
            int min = _cy >= _top ? _top : 0;
            _cy = Math.Max(min, _cy - n);
            _wrapPending = false;
        }

        private void CursorDown(int n)
        {
            int max = _cy <= _bottom ? _bottom : Rows - 1;
            _cy = Math.Min(max, _cy + n);
            _wrapPending = false;
        }

        private void CursorForward(int n) { _cx = Math.Min(Cols - 1, Math.Min(_cx, Cols - 1) + n); _wrapPending = false; }
        private void CursorBack(int n) { _cx = Math.Max(0, Math.Min(_cx, Cols - 1) - n); _wrapPending = false; }

        private void SetCursor(int row, int col)
        {
            if (_originMode) row = Clamp(row + _top, _top, _bottom);
            _cy = Clamp(row, 0, Rows - 1);
            _cx = Clamp(col, 0, Cols - 1);
            _wrapPending = false;
        }

        private void SetCursorRow(int row)
        {
            if (_originMode) row = Clamp(row + _top, _top, _bottom);
            _cy = Clamp(row, 0, Rows - 1);
            _wrapPending = false;
        }

        private void TabForward(int n)
        {
            int c = Math.Min(_cx, Cols - 1);
            for (int k = 0; k < n; k++)
            {
                c++;
                while (c < Cols - 1 && !_tabs[c]) c++;
                if (c >= Cols - 1) { c = Cols - 1; break; }
            }
            _cx = c;
            _wrapPending = false;
        }

        private void TabBack(int n)
        {
            int c = Math.Min(_cx, Cols - 1);
            for (int k = 0; k < n && c > 0; k++)
            {
                c--;
                while (c > 0 && !_tabs[c]) c--;
            }
            _cx = c;
            _wrapPending = false;
        }

        private void SaveCursor()
        {
            var s = new Saved { X = _cx, Y = _cy, Fg = _fg, Bg = _bg, Attr = _attr, Origin = _originMode, Gl = _gl, G0 = _charsets[0], G1 = _charsets[1], Valid = true };
            if (AltScreen) _savedAlt = s; else _savedMain = s;
        }

        private void RestoreCursor()
        {
            var s = AltScreen ? _savedAlt : _savedMain;
            if (!s.Valid) { SetCursor(0, 0); return; }
            _cx = Math.Min(s.X, Cols - 1);
            _cy = Math.Min(s.Y, Rows - 1);
            _fg = s.Fg; _bg = s.Bg; _attr = s.Attr; _originMode = s.Origin; _gl = s.Gl;
            _charsets[0] = s.G0; _charsets[1] = s.G1;
            _wrapPending = false;
        }

        private void SwitchScreen(bool toAlt, bool clearAlt, bool saveCursor)
        {
            if (toAlt == AltScreen) return;
            if (toAlt)
            {
                if (saveCursor) SaveCursor();
                _screen = _alt;
                if (clearAlt) for (int r = 0; r < Rows; r++) FillRow(_alt[r], DefaultColor);
            }
            else
            {
                _screen = _main;
                if (saveCursor) RestoreCursor();
            }
            MarkAllDirty();
        }

        private void SoftReset()
        {
            CursorVisible = true;
            _originMode = false;
            _autowrap = true;
            _insertMode = false;
            AppCursorKeys = false;
            AppKeypad = false;
            _top = 0;
            _bottom = Rows - 1;
            _attr = CellAttr.None;
            _fg = _bg = DefaultColor;
            _charsets[0] = _charsets[1] = 'B';
            _gl = 0;
            _savedMain = _savedAlt = default;
            _wrapPending = false;
            Version++;
        }

        private void FullReset()
        {
            SoftReset();
            _screen = _main;
            for (int r = 0; r < Rows; r++) { FillRow(_main[r], DefaultColor); FillRow(_alt[r], DefaultColor); }
            _cx = _cy = 0;
            ReverseVideo = false;
            BracketedPaste = false;
            MouseMode = 0;
            MouseSgr = false;
            FocusEvents = false;
            SyncOutput = false;
            _newlineMode = false;
            CursorShape = CursorShape.Block;
            CursorBlink = true;
            ResetTabs();
            ResetPalette();
            MarkAllDirty();
        }

        private void ResetTabs()
        {
            _tabs = new bool[Cols];
            for (int c = 0; c < Cols; c++) _tabs[c] = c % 8 == 0 && c > 0;
        }

        private void ResetPalette()
        {
            // a dark theme close to common terminal defaults, readable over the panel background
            int[] basic =
            {
                0x1D2129, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
                0x5C6370, 0xFF7B86, 0xB5E890, 0xFFD58A, 0x8CC8FF, 0xDE9BF0, 0x7FD8E2, 0xFFFFFF,
            };
            for (int i = 0; i < 16; i++) _palette[i] = basic[i];
            int[] steps = { 0, 95, 135, 175, 215, 255 };
            for (int i = 0; i < 216; i++)
                _palette[16 + i] = (steps[i / 36] << 16) | (steps[(i / 6) % 6] << 8) | steps[i % 6];
            for (int i = 0; i < 24; i++)
            {
                int v = 8 + i * 10;
                _palette[232 + i] = (v << 16) | (v << 8) | v;
            }
        }

        private void Touch(int row) { _dirty[row] = true; Version++; }

        private void MarkAllDirty()
        {
            for (int r = 0; r < _dirty.Length; r++) _dirty[r] = true;
            Version++;
        }

        private static Cell[][] NewScreen(int rows, int cols)
        {
            var s = new Cell[rows][];
            for (int r = 0; r < rows; r++) s[r] = NewRow(cols, DefaultColor);
            return s;
        }

        private static Cell[] NewRow(int cols, int bg)
        {
            var row = new Cell[cols];
            var blank = Cell.BlankWith(bg);
            for (int c = 0; c < cols; c++) row[c] = blank;
            return row;
        }

        private static Cell[] FillRow(Cell[] row, int bg)
        {
            var blank = Cell.BlankWith(bg);
            for (int c = 0; c < row.Length; c++) row[c] = blank;
            return row;
        }

        private static Cell[][] ResizeScreen(Cell[][] old, int rows, int cols, int shift)
        {
            var s = new Cell[rows][];
            for (int r = 0; r < rows; r++)
            {
                var row = NewRow(cols, DefaultColor);
                int src = r + shift;
                if (src < old.Length)
                {
                    var o = old[src];
                    int n = Math.Min(cols, o.Length);
                    Array.Copy(o, row, n);
                    if (n > 0 && (row[n - 1].Attr & CellAttr.WideHead) != 0) row[n - 1] = Cell.BlankWith(DefaultColor);
                }
                s[r] = row;
            }
            return s;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        // DEC Special Graphics for 0x5F..0x7E (ESC ( 0): line drawing used by curses programs.
        private static readonly int[] DecGraphics =
        {
            0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518,
            0x2510, 0x250C, 0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534,
            0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
        };
    }
}
