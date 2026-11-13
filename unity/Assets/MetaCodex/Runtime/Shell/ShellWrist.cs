using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// MAIN MENU modes (Settings > DISPLAY): the DOCK panel in front of the user, or the WRIST menu (UiWristMenu): look
    /// at the back of the left wrist as at a watch, touch the core with the right index finger, touch a blade. Both
    /// carry the same actions; switching closes one and brings the other (settings key `wristMenu`, voice "turn on the
    /// wrist menu", Lua settings.set("wristMenu", "true")).
    public sealed partial class MetaCodexShell
    {
        public UiWristMenu Wrist { get; private set; }
        private bool _wristModeApplied;

        private void BuildWrist()
        {
            if (Wrist != null) return;
            var t = UiTheme.Current;
            Wrist = UiWristMenu.Create(_root, t);
            Wrist.Head = Head;
            Wrist.Hand = h =>
            {
                var st = Gestures?.Get(h);
                // a hand over the table keyboard types: it neither carries the menu nor presses it
                return st != null && st.Frame.Tracked && !Gestures.IsMuted(h) ? (true, st.Frame) : (false, default(HandFrame));
            };
            // one table for the dock and the wrist menu (MainMenuItems): the same labels, order and lit states
            foreach (var m in MainMenuItems())
                (m.Outer ? Wrist.Outer : Wrist.Inner).Add(new UiWristMenu.Item { Glyph = m.Glyph, Icon = m.Icon, Label = m.Label, Act = m.Act, Lit = m.Lit, LitTint = m.Tint ?? t.Ok });
            Wrist.Build();
            Wrist.GlassOpacity = PanelOpacity; // created after ApplyMaterialSettings ran: the glass takes the setting now
            _wristModeApplied = !Settings.wristMenu; // applied below
            ApplyMainMenuMode();
        }

        /// One entry of the main menu. The dock and the wrist menu are built from the same table (MainMenuItems), so they
        /// keep the same labels, order and lit states; `WristOnly` entries are not on the dock.
        public sealed class MainMenuItem
        {
            public string Glyph, Icon, Label;   // the dock shows the glyph, the wrist a line icon of UiIconAtlas
            public Action Act;
            public Func<bool> Lit;              // lit (wrist) / selected (dock) while this is true
            public Color? Tint;                 // the wrist's lit colour (default: ok green)
            public bool Outer, WristOnly;
        }

        /// The main menu: inner ring = what is used most (owner 2026-11-11: 19 tiles were too many; setup is in CONTROL),
        /// outer ring = quick actions and the smaller windows. The dock shows them in this order, without the wrist-only ones.
        public List<MainMenuItem> MainMenuItems()
        {
            var t = UiTheme.Current;
            var list = new List<MainMenuItem>();
            void I(bool outer, string glyph, string icon, string label, Action act, Func<bool> lit = null, Color? tint = null, bool wristOnly = false) =>
                list.Add(new MainMenuItem { Outer = outer, Glyph = glyph, Icon = icon, Label = label, Act = act, Lit = lit, Tint = tint, WristOnly = wristOnly });
            I(false, "◉", "mic", "TALK", () => VoiceToggle(), () => VoiceState == "listening" || VoiceState == "transcribing", t.Danger);
            I(false, "▶", "terminal", "TERM", () => ShowOrNewTerminal());
            I(false, "◫", "folder", "FILES", () => TogglePanel("files"), () => IsPanelOpen("files"));
            I(false, "☰", "threads", "THREADS", () => TogglePanel("threads"), () => IsPanelOpen("threads"));
            I(false, "◎", "globe", "WEB", () => TogglePanel("web"), () => IsPanelOpen("web"));
            // settings, voice, agents, MQH (skills, plugins, scripts, gestures, status), vault and git, system: one place
            I(false, "⚙", "gear", "CONTROL", () => ToggleControl(), () => ControlRailPage != null);
            I(true, "▤", "windows", "TERMS", () => TogglePanel("terminals"), () => IsPanelOpen("terminals"));
            I(true, "▣", "photos", "CLIP", () => TogglePanel("clipboard"), () => IsPanelOpen("clipboard"));
            I(true, "◐", "aperture", "FOCUS", () => SetFocus(!Settings.focusEnabled), () => Settings.focusEnabled);
            I(true, "⌖", "select", "SELECT", () => SetSelectionMode(!Router.SelectionMode), () => Router.SelectionMode, t.Warn);
            I(true, "⌨", "keyboard", "KEYS", () => OpenKeyboard(), () => Keyboard != null && Keyboard.Visible && Keyboard.Sink == _terminalSink);
            // MetaCodex's own keyboard laid on a table (OpenTableKeyboard: it says how when it is not on one); the wrist
            // keeps it (owner 2026-11-10), the dock does not
            I(true, "⌨", "tablekeys", "TABLE", () => OpenTableKeyboard(), wristOnly: true);
            I(true, "⟲", "recenter", "CENTER", () => app?.Recenter());
            return list;
        }

        /// DOCK (false) or WRIST (true).
        public void SetWristMenu(bool on)
        {
            Settings.wristMenu = on;
            OnSettingsChanged();
        }

        /// Brings the chosen main menu and puts the other away; runs when the setting changed, whoever changed it.
        private void ApplyMainMenuMode()
        {
            if (Wrist == null || _wristModeApplied == Settings.wristMenu) return;
            _wristModeApplied = Settings.wristMenu;
            // the wrist menu goes with its closing animation (never at once), and comes back on the next look
            if (Settings.wristMenu) Wrist.Resume(); else Wrist.Dismiss();
            if (_panels.TryGetValue("dock", out var dock))
            {
                if (Settings.wristMenu) { if (dock.IsOpen) dock.Close(); }
                else if (!dock.IsOpen && Head != null) { PlaceDefault("dock", dock); dock.Open(); }
            }
        }

        /// Tests: one step of the wrist menu (the Editor has no frame loop).
        public void TickWristForTest(float dt) { if (Wrist != null && Wrist.enabled) { Wrist.Head = Head; Wrist.Tick(dt); } }
    }
}
