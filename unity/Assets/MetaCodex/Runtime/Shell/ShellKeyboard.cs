using System;
using MetaCodex.Hands;
using MetaCodex.Terminal;
using MetaCodex.Typing;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// MetaCodex's own keyboard (UiSurfaceKeyboard) for every text: fields (UiKeyboard), the terminal, web pages. The
    /// owner asked for the Experimental Surface Keyboard; apps cannot turn it on or summon it, so this one does the same
    /// (2026-11-10: "if it's not possible we need to create our very own ... I don't ever wanna see the default one").
    public sealed partial class MetaCodexShell
    {
        public UiSurfaceKeyboard Keyboard { get; private set; }
        private TerminalSink _terminalSink;

        private void InitKeyboard()
        {
            var go = new GameObject("Keyboard");
            go.transform.SetParent(_root, false);
            Keyboard = go.AddComponent<UiSurfaceKeyboard>();
            Keyboard.Init(Router, () => Head, () => Gestures?.Source, _root);
            UiKeyboard.Keyboard = Keyboard;
            // a hand over the keyboard on the table types and does nothing else: no ray, no pinch, no poke on a window,
            // no wrist menu, no gesture (owner 2026-11-11: "sometimes I click around when trying to type")
            if (Gestures != null) Gestures.Muted = h => Keyboard != null && Keyboard.HandInZone((int)h);
            _terminalSink = new TerminalSink(this);
            UiKeyboard.TerminalKeyboardRequested += ToggleTerminalKeyboard;
            if (app?.Input != null) app.Input.KeyboardShown = () => Keyboard != null && Keyboard.Visible && Keyboard.Sink == _terminalSink;
        }

        /// KEYS on the dock, ⌨ on a terminal frame, the key bar: the keyboard for the focused terminal, or away.
        public void ToggleTerminalKeyboard()
        {
            if (Keyboard == null) return;
            if (app?.Input != null) app.Input.KeyboardShown = () => Keyboard != null && Keyboard.Visible && Keyboard.Sink == _terminalSink;
            if (Keyboard.Visible && Keyboard.Sink == _terminalSink) { Keyboard.Hide(); return; }
            Keyboard.Show(_terminalSink);
        }

        /// The wrist menu's TABLE (and "open the table keyboard"): the keyboard for the terminal, with how to lay it down.
        public void OpenTableKeyboard()
        {
            if (Keyboard == null) return;
            Keyboard.Show(_terminalSink);
            if (Keyboard.Mode != UiSurfaceKeyboard.KbMode.Surface)
                Toast("Rest both hands flat on a table: the keyboard lies down under them, the touchpad on the right", "info");
        }

        /// The keys into the focused terminal (the same encoder as a Bluetooth keyboard).
        private sealed class TerminalSink : IKeySink
        {
            private readonly MetaCodexShell _s;
            public TerminalSink(MetaCodexShell s) { _s = s; }
            public string Title => "TERMINAL";
            public string Preview => null;
            public bool KeepsKeyboard => true;
            public KeyContext Context => KeyContext.Code;

            public void Type(string text, bool ctrl, bool alt)
            {
                var input = _s.app?.Input;
                if (input == null) return;
                if (ctrl) input.StickyCtrl = true;
                if (alt) input.StickyAlt = true;
                input.TypeText(text);
            }

            public void Key(KbKind key, bool ctrl, bool alt, bool shift, int fn)
            {
                var input = _s.app?.Input;
                if (input == null) return;
                var mods = (ctrl ? KeyMods.Ctrl : KeyMods.None) | (alt ? KeyMods.Alt : KeyMods.None) | (shift ? KeyMods.Shift : KeyMods.None);
                switch (key)
                {
                    case KbKind.Backspace: input.PressKey(TermKey.Backspace, mods); break;
                    case KbKind.Enter: input.PressKey(TermKey.Enter, mods); break;
                    case KbKind.Tab: input.PressKey(TermKey.Tab, mods); break;
                    case KbKind.Escape: input.PressKey(TermKey.Escape, mods); break;
                    case KbKind.Left: input.PressKey(TermKey.Left, mods); break;
                    case KbKind.Right: input.PressKey(TermKey.Right, mods); break;
                    case KbKind.Up: input.PressKey(TermKey.Up, mods); break;
                    case KbKind.Down: input.PressKey(TermKey.Down, mods); break;
                    case KbKind.Home: input.PressKey(TermKey.Home, mods); break;
                    case KbKind.End: input.PressKey(TermKey.End, mods); break;
                    case KbKind.Delete: input.PressKey(TermKey.Delete, mods); break;
                    // as on a real keyboard in a terminal: Shift + Page Up / Down scrolls back through what was printed
                    case KbKind.PageUp: if (shift) input.Scroll(10); else input.PressKey(TermKey.PageUp, mods); break;
                    case KbKind.PageDown: if (shift) input.Scroll(-10); else input.PressKey(TermKey.PageDown, mods); break;
                    case KbKind.Function: if (fn >= 1 && fn <= 12) input.PressKey(TermKey.F1 + (fn - 1), mods); break;
                    case KbKind.Paste: input.Paste(GUIUtility.systemCopyBuffer); break;
                }
            }
        }

        /// Self-tests: the sink the terminal gets.
        public IKeySink TerminalSinkForTest => _terminalSink;
    }
}
