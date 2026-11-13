using System;
using MetaCodex.Terminal;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MetaCodex.App
{
    /// Keyboard input for the terminal: a Bluetooth/USB keyboard through the Input System (text + special
    /// keys + Ctrl/Alt chords) and the headset's system keyboard (SystemKeyboard, typed with the hands). Sticky
    /// Ctrl/Alt from the key bar apply to the next key from either.
    public sealed class TerminalInput : MonoBehaviour
    {
        public event Action<byte[]> Send;
        public event Action<int> ScrollRequest; // lines, + = back in history
        public Func<bool> AppCursorKeys = () => false;
        public Func<bool> BracketedPaste = () => false;
        public bool StickyCtrl { get; set; }
        public bool StickyAlt { get; set; }
        /// The system keyboard is open for the terminal (the key bar and the dock light their ⌨).
        public bool SystemKeyboardOpen => MetaCodex.UI.SystemKeyboard.IsOpenFor(this);

        /// A panel that takes the navigation keys while it has the focus (THREAD's sidebar): true = used, not typed.
        public static Func<TermKey, bool> Intercept;

        private Keyboard _kb;
        private char _pendingHigh;
        private string _sysText = "";
        /// The system keyboard delivers its text twice: in its text box (read by PollSystemKeyboard) and again as text
        /// input / key events on Keyboard.current ("ls" arrived as "lsls"). While it is open, and 1 s after, only its
        /// text box counts.
        private bool SystemKeyboardBusy => MetaCodex.UI.SystemKeyboard.Busy;

        private static readonly (Key key, TermKey term)[] Specials =
        {
            (Key.Enter, TermKey.Enter), (Key.NumpadEnter, TermKey.Enter), (Key.Backspace, TermKey.Backspace),
            (Key.Tab, TermKey.Tab), (Key.Escape, TermKey.Escape), (Key.UpArrow, TermKey.Up), (Key.DownArrow, TermKey.Down),
            (Key.LeftArrow, TermKey.Left), (Key.RightArrow, TermKey.Right), (Key.Home, TermKey.Home), (Key.End, TermKey.End),
            (Key.PageUp, TermKey.PageUp), (Key.PageDown, TermKey.PageDown), (Key.Insert, TermKey.Insert), (Key.Delete, TermKey.Delete),
            (Key.F1, TermKey.F1), (Key.F2, TermKey.F2), (Key.F3, TermKey.F3), (Key.F4, TermKey.F4), (Key.F5, TermKey.F5),
            (Key.F6, TermKey.F6), (Key.F7, TermKey.F7), (Key.F8, TermKey.F8), (Key.F9, TermKey.F9), (Key.F10, TermKey.F10),
            (Key.F11, TermKey.F11), (Key.F12, TermKey.F12),
        };

        private void OnDisable() => Hook(null);

        private void Hook(Keyboard kb)
        {
            if (_kb == kb) return;
            if (_kb != null) _kb.onTextInput -= OnText;
            _kb = kb;
            if (_kb != null) _kb.onTextInput += OnText;
        }

        private bool Ctrl => (_kb != null && _kb.ctrlKey.isPressed);
        private bool Alt => (_kb != null && _kb.altKey.isPressed);
        private bool Shift => (_kb != null && _kb.shiftKey.isPressed);

        private void OnText(char ch)
        {
            // a UI text field has focus: a physical keyboard types there, not into the shell (the headset keyboard
            // already fills that field through its own text box)
            if (MetaCodex.UI.UiKeyboard.Active) { if (!MetaCodex.UI.UiKeyboard.SystemKeyboardBusy) MetaCodex.UI.UiKeyboard.TypePhysical(ch.ToString()); return; }
            if (SystemKeyboardBusy) return;
            if (ch < 0x20 || ch == 0x7F) return; // Enter, Tab, Backspace arrive as keys
            if (Ctrl) return; // chords are handled from key presses in Update
            if (char.IsHighSurrogate(ch)) { _pendingHigh = ch; return; }
            string s = char.IsLowSurrogate(ch) && _pendingHigh != 0 ? new string(new[] { _pendingHigh, ch }) : ch.ToString();
            _pendingHigh = '\0';
            TypeText(s, fromPhysical: true);
        }

        /// Text typed by any source (keyboard, system keyboard, voice, control port).
        public void TypeText(string s, bool fromPhysical = false)
        {
            if (string.IsNullOrEmpty(s)) return;
            bool alt = StickyAlt || (fromPhysical && Alt);
            if (StickyCtrl && s.Length == 1)
            {
                Emit(KeyEncoder.Ctrl(s[0], alt));
                StickyCtrl = StickyAlt = false;
                return;
            }
            Emit(KeyEncoder.Text(s, alt));
            StickyAlt = false;
        }

        public void Paste(string s)
        {
            if (!string.IsNullOrEmpty(s)) Emit(KeyEncoder.Paste(s, BracketedPaste()));
        }

        public void PressKey(TermKey key, KeyMods extra = KeyMods.None)
        {
            var mods = extra;
            if (StickyCtrl) mods |= KeyMods.Ctrl;
            if (StickyAlt) mods |= KeyMods.Alt;
            StickyCtrl = StickyAlt = false;
            var b = KeyEncoder.Key(key, mods, AppCursorKeys());
            if (b != null) Emit(b);
        }

        private void Emit(byte[] b) => Send?.Invoke(b);

        /// Scrolls back (+) or forward (-) through what was printed (the keyboard's Shift + Page Up / Page Down).
        public void Scroll(int lines) => ScrollRequest?.Invoke(lines);

        private void Update()
        {
            Hook(Keyboard.current);
            if (MetaCodex.UI.UiKeyboard.Active)
            {
                if (MetaCodex.UI.UiKeyboard.SystemKeyboardBusy) return;
                if (_kb != null && _kb.backspaceKey.wasPressedThisFrame) MetaCodex.UI.UiKeyboard.TypePhysical("\b");
                if (_kb != null && (_kb.enterKey.wasPressedThisFrame || _kb.numpadEnterKey.wasPressedThisFrame)) MetaCodex.UI.UiKeyboard.TypePhysical("\n");
                return;
            }
            if (_kb != null && !SystemKeyboardBusy) PollPhysical();
            PollSystemKeyboard();
        }

        private void PollPhysical()
        {
            var mods = (Shift ? KeyMods.Shift : 0) | (Ctrl ? KeyMods.Ctrl : 0) | (Alt ? KeyMods.Alt : 0);
            foreach (var (key, term) in Specials)
            {
                if (!_kb[key].wasPressedThisFrame) continue;
                if (Intercept != null && mods == KeyMods.None && Intercept(term)) continue;
                if (Shift && (term == TermKey.PageUp || term == TermKey.PageDown)) { ScrollRequest?.Invoke(term == TermKey.PageUp ? 10 : -10); continue; }
                PressKey(term, mods);
            }
            if (!Ctrl) return;
            if (Shift && _kb.vKey.wasPressedThisFrame) { Paste(GUIUtility.systemCopyBuffer); return; }
            for (Key k = Key.A; k <= Key.Z; k++)
                if (_kb[k].wasPressedThisFrame) Emit(KeyEncoder.Ctrl((char)('a' + (k - Key.A)), Alt));
            if (_kb.spaceKey.wasPressedThisFrame) Emit(KeyEncoder.Ctrl(' ', Alt));
            if (_kb.leftBracketKey.wasPressedThisFrame) Emit(KeyEncoder.Ctrl('[', Alt));
            if (_kb.rightBracketKey.wasPressedThisFrame) Emit(KeyEncoder.Ctrl(']', Alt));
            if (_kb.backslashKey.wasPressedThisFrame) Emit(KeyEncoder.Ctrl('\\', Alt));
            if (_kb.slashKey.wasPressedThisFrame) Emit(KeyEncoder.Ctrl('_', Alt));
        }

        /// The system keyboard for the terminal (dock KEYS, the frame's ⌨, the key bar): open, or closed when it is open.
        public void ToggleSystemKeyboard()
        {
            if (SystemKeyboardOpen) { MetaCodex.UI.SystemKeyboard.Close(); return; }
            ShowSystemKeyboard();
        }

        /// The system keyboard for the terminal; one that is open stays open ("show the keyboard" is never a toggle).
        public void ShowSystemKeyboard()
        {
            if (SystemKeyboardOpen) return;
            if (MetaCodex.UI.SystemKeyboard.Open(this, "", false, false, "Type into the terminal")) _sysText = "";
        }

        /// Typed text is diffed against what was already sent, so each key reaches the shell at once (tab
        /// completion, vim keys) instead of when the keyboard is closed. Done (its Enter key) presses Enter.
        private void PollSystemKeyboard()
        {
            if (!SystemKeyboardOpen) return;
            var status = MetaCodex.UI.SystemKeyboard.Poll(this, out var t);
            if (t != null && t != _sysText)
            {
                int p = 0;
                while (p < t.Length && p < _sysText.Length && t[p] == _sysText[p]) p++;
                for (int i = p; i < _sysText.Length; i++) Emit(new byte[] { 0x7F });
                if (p < t.Length) TypeText(t.Substring(p));
                _sysText = t;
            }
            if (status == TouchScreenKeyboard.Status.Done) PressKey(TermKey.Enter);
        }
    }
}
