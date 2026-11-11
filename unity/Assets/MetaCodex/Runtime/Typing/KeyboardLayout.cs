using System.Collections.Generic;

namespace MetaCodex.Typing
{
    public enum KbKind
    {
        Char, Backspace, Enter, Tab, Escape, Shift, Ctrl, Alt, Caps, Space, Left, Right, Up, Down,
        Home, End, PageUp, PageDown, Delete, Function, Paste, Hide,
    }

    /// One key of the keyboard. Places are in metres from the key block's top-left corner (x to the right, y towards
    /// the user when the keyboard lies on a table): the key's cell is its whole share of the block (no gap between
    /// cells); the drawn cap is the cell less a gap.
    public sealed class KeyDef
    {
        /// "a", "1", "esc", "f5", "lshift", "space", ...
        public string Id;
        /// The legend: "A", "1", "esc"; Top is the second legend of a two-symbol key ("!" over "1").
        public string Label, Top;
        /// What a character key types, plain and with Shift.
        public string Normal, Shifted;
        public KbKind Kind;
        /// 1..12 for a function key.
        public int Fn;
        public int Row;
        /// In key units from the left edge of the block.
        public float UnitX, UnitW;
        public float X, Y, W, H;
        public float Cx => X + W * 0.5f;
        public float Cy => Y + H * 0.5f;
        public bool Contains(float x, float y) => x >= X && x < X + W && y >= Y && y < Y + H;
        /// The character it types for the letter model ('\0': not a character key).
        public char Letter => Kind == KbKind.Space ? ' ' : Kind == KbKind.Char && Normal.Length == 1 ? Normal[0] : '\0';
    }

    /// A normal keyboard: the ANSI layout of a compact (75 %) board, with a real one's proportions. 19.05 mm from key
    /// to key, 1.5 u Tab, 1.75 u Caps Lock, 2.25 u Shift and Enter, 2 u Backspace, a 6.25 u space bar; Esc and F1-F12
    /// in their groups of four above a small gap; the arrows as an inverted T at the bottom right with Page Up / Page
    /// Down / Home / End in the column over them (owner 2026-11-11: "the exact layout of a normal keyboard", "normal
    /// shape ... same proportions too"). Two keys are MetaCodex's own, where a board has its system keys: paste (left
    /// of the left Alt) and hide (right of the right Alt). English (US) only.
    public sealed class KeyboardLayout
    {
        /// One key unit: the pitch of a real keyboard.
        public const float Unit = 0.01905f;
        /// The gap between the function row and the number row, in units.
        public const float FunctionGap = 0.25f;
        public const float UnitsWide = 16f, UnitsHigh = 6f + FunctionGap;

        public readonly List<KeyDef> Keys = new List<KeyDef>();
        public float Width => UnitsWide * Unit;
        public float Height => UnitsHigh * Unit;
        private readonly Dictionary<string, KeyDef> _byId = new Dictionary<string, KeyDef>();

        public KeyDef Find(string id) => id != null && _byId.TryGetValue(id, out var k) ? k : null;

        /// The key whose cell holds the point, or null (outside the block, or in a gap of the function row).
        public KeyDef At(float x, float y)
        {
            foreach (var k in Keys) if (k.Contains(x, y)) return k;
            return null;
        }

        private float _x;
        private int _row;

        private static float RowY(int row) => row == 0 ? 0f : row + FunctionGap;

        private void StartRow(int row, float x = 0f) { _row = row; _x = x; }

        private KeyDef Add(string id, KbKind kind, string label, float w = 1f)
        {
            var k = new KeyDef
            {
                Id = id, Kind = kind, Label = label, Row = _row, UnitX = _x, UnitW = w,
                X = _x * Unit, Y = RowY(_row) * Unit, W = w * Unit, H = Unit,
            };
            _x += w;
            Keys.Add(k);
            _byId[id] = k;
            return k;
        }

        private void Letter(char c)
        {
            var k = Add(c.ToString(), KbKind.Char, char.ToUpperInvariant(c).ToString());
            k.Normal = c.ToString();
            k.Shifted = char.ToUpperInvariant(c).ToString();
        }

        private void Symbol(string normal, string shifted, float w = 1f)
        {
            var k = Add(normal, KbKind.Char, normal, w);
            k.Top = shifted;
            k.Normal = normal;
            k.Shifted = shifted;
        }

        private void Skip(float w) => _x += w;

        public static KeyboardLayout Ansi()
        {
            var l = new KeyboardLayout();
            l.StartRow(0);
            l.Add("esc", KbKind.Escape, "esc");
            l.Skip(1f);
            for (int i = 1; i <= 12; i++)
            {
                l.Add("f" + i, KbKind.Function, "F" + i).Fn = i;
                if (i == 4 || i == 8) l.Skip(0.5f);
            }
            l.Add("del", KbKind.Delete, "del");

            l.StartRow(1);
            l.Symbol("`", "~");
            string digits = "1234567890", over = "!@#$%^&*()";
            for (int i = 0; i < digits.Length; i++) l.Symbol(digits[i].ToString(), over[i].ToString());
            l.Symbol("-", "_"); l.Symbol("=", "+");
            l.Add("backspace", KbKind.Backspace, "backspace", 2f);
            l.Add("pgup", KbKind.PageUp, "pg up");

            l.StartRow(2);
            l.Add("tab", KbKind.Tab, "tab", 1.5f);
            foreach (char c in "qwertyuiop") l.Letter(c);
            l.Symbol("[", "{"); l.Symbol("]", "}");
            l.Symbol("\\", "|", 1.5f);
            l.Add("pgdn", KbKind.PageDown, "pg dn");

            l.StartRow(3);
            l.Add("caps", KbKind.Caps, "caps lock", 1.75f);
            foreach (char c in "asdfghjkl") l.Letter(c);
            l.Symbol(";", ":"); l.Symbol("'", "\"");
            l.Add("enter", KbKind.Enter, "enter", 2.25f);
            l.Add("home", KbKind.Home, "home");

            l.StartRow(4);
            l.Add("lshift", KbKind.Shift, "shift", 2.25f);
            foreach (char c in "zxcvbnm") l.Letter(c);
            l.Symbol(",", "<"); l.Symbol(".", ">"); l.Symbol("/", "?");
            l.Add("rshift", KbKind.Shift, "shift", 1.75f);
            l.Add("up", KbKind.Up, "↑");
            l.Add("end", KbKind.End, "end");

            l.StartRow(5);
            l.Add("lctrl", KbKind.Ctrl, "ctrl", 1.25f);
            l.Add("paste", KbKind.Paste, "paste", 1.25f);
            l.Add("lalt", KbKind.Alt, "alt", 1.25f);
            l.Add("space", KbKind.Space, "", 6.25f);
            l.Add("ralt", KbKind.Alt, "alt");
            l.Add("hide", KbKind.Hide, "hide");
            l.Add("rctrl", KbKind.Ctrl, "ctrl");
            l.Add("left", KbKind.Left, "←");
            l.Add("down", KbKind.Down, "↓");
            l.Add("right", KbKind.Right, "→");
            return l;
        }
    }
}
