using System;
using System.Text;

namespace MetaCodex.Terminal
{
    public enum TermKey
    {
        None, Enter, Backspace, Tab, Escape, Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    }

    [Flags]
    public enum KeyMods { None = 0, Shift = 1, Alt = 2, Ctrl = 4 }

    /// Keys and text to the bytes an xterm sends (what bash, vim and TUIs expect with TERM=xterm-256color).
    public static class KeyEncoder
    {
        public static byte[] Key(TermKey key, KeyMods mods, bool appCursorKeys)
        {
            string s = KeyString(key, mods, appCursorKeys);
            return s == null ? null : Encoding.UTF8.GetBytes(s);
        }

        public static string KeyString(TermKey key, KeyMods mods, bool appCursorKeys)
        {
            int m = 1 + ((mods & KeyMods.Shift) != 0 ? 1 : 0) + ((mods & KeyMods.Alt) != 0 ? 2 : 0) + ((mods & KeyMods.Ctrl) != 0 ? 4 : 0);
            bool alt = (mods & KeyMods.Alt) != 0;
            switch (key)
            {
                case TermKey.Enter: return alt ? "\x1b\r" : "\r";
                case TermKey.Backspace:
                    if ((mods & KeyMods.Ctrl) != 0) return alt ? "\x1b\x08" : "\x08";
                    return alt ? "\x1b\x7f" : "\x7f";
                case TermKey.Tab: return (mods & KeyMods.Shift) != 0 ? "\x1b[Z" : alt ? "\x1b\t" : "\t";
                case TermKey.Escape: return alt ? "\x1b\x1b" : "\x1b";
                case TermKey.Up: return Cursor('A', m, appCursorKeys);
                case TermKey.Down: return Cursor('B', m, appCursorKeys);
                case TermKey.Right: return Cursor('C', m, appCursorKeys);
                case TermKey.Left: return Cursor('D', m, appCursorKeys);
                case TermKey.Home: return Cursor('H', m, appCursorKeys);
                case TermKey.End: return Cursor('F', m, appCursorKeys);
                case TermKey.Insert: return Tilde(2, m);
                case TermKey.Delete: return Tilde(3, m);
                case TermKey.PageUp: return Tilde(5, m);
                case TermKey.PageDown: return Tilde(6, m);
                case TermKey.F1: return m == 1 ? "\x1bOP" : $"\x1b[1;{m}P";
                case TermKey.F2: return m == 1 ? "\x1bOQ" : $"\x1b[1;{m}Q";
                case TermKey.F3: return m == 1 ? "\x1bOR" : $"\x1b[1;{m}R";
                case TermKey.F4: return m == 1 ? "\x1bOS" : $"\x1b[1;{m}S";
                case TermKey.F5: return Tilde(15, m);
                case TermKey.F6: return Tilde(17, m);
                case TermKey.F7: return Tilde(18, m);
                case TermKey.F8: return Tilde(19, m);
                case TermKey.F9: return Tilde(20, m);
                case TermKey.F10: return Tilde(21, m);
                case TermKey.F11: return Tilde(23, m);
                case TermKey.F12: return Tilde(24, m);
                default: return null;
            }
        }

        private static string Cursor(char final, int m, bool app)
        {
            if (m != 1) return $"\x1b[1;{m}{final}";
            return app ? "\x1bO" + final : "\x1b[" + final;
        }

        private static string Tilde(int code, int m) => m == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{m}~";

        /// Ctrl+<char>: letters and @[\]^_ map to C0 controls, ? to DEL, space to NUL.
        public static byte[] Ctrl(char c, bool alt = false)
        {
            int code;
            if (c >= 'a' && c <= 'z') code = c - 'a' + 1;
            else if (c >= 'A' && c <= 'Z') code = c - 'A' + 1;
            else if (c == '@' || c == ' ' || c == '2') code = 0;
            else if (c == '[' || c == '3') code = 27;
            else if (c == '\\' || c == '4') code = 28;
            else if (c == ']' || c == '5') code = 29;
            else if (c == '^' || c == '6') code = 30;
            else if (c == '_' || c == '-' || c == '7') code = 31;
            else if (c == '?' || c == '8') code = 127;
            else return Text(c.ToString(), alt);
            return alt ? new byte[] { 0x1b, (byte)code } : new[] { (byte)code };
        }

        public static byte[] Text(string text, bool alt = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (!alt) return bytes;
            var r = new byte[bytes.Length + 1];
            r[0] = 0x1b;
            Array.Copy(bytes, 0, r, 1, bytes.Length);
            return r;
        }

        /// Pasted or dictated text: newlines become Enter (\r), wrapped in bracketed-paste markers when the
        /// program asked for them, so shells and editors don't execute line by line.
        public static byte[] Paste(string text, bool bracketed)
        {
            string t = text.Replace("\r\n", "\r").Replace('\n', '\r');
            if (bracketed) t = "\x1b[200~" + t.Replace("\x1b[201~", "") + "\x1b[201~";
            return Encoding.UTF8.GetBytes(t);
        }
    }
}
