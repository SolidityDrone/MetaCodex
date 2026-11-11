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
            // each with a line icon of UiIconAtlas (the glyph is the dock's)
            void I(List<UiWristMenu.Item> list, string glyph, string icon, string label, Action act, Func<bool> lit = null, Color? tint = null) =>
                list.Add(new UiWristMenu.Item { Glyph = glyph, Icon = icon, Label = label, Act = act, Lit = lit, LitTint = tint ?? t.Ok });
            // inner ring: what is used most (owner 2026-11-11: 19 tiles were too many; setup is in CONTROL now)
            I(Wrist.Inner, "◉", "mic", "TALK", () => VoiceToggle(), () => VoiceState == "listening" || VoiceState == "transcribing", t.Danger);
            I(Wrist.Inner, "▶", "terminal", "TERM", () => ShowOrNewTerminal());
            I(Wrist.Inner, "◫", "folder", "FILES", () => TogglePanel("files"), () => IsPanelOpen("files"));
            I(Wrist.Inner, "☰", "threads", "THREADS", () => TogglePanel("threads"), () => IsPanelOpen("threads"));
            I(Wrist.Inner, "◎", "globe", "WEB", () => TogglePanel("web"), () => IsPanelOpen("web"));
            // settings, voice, agents, MQH (skills, plugins, scripts, gestures, daemon), vault and git, system: one place
            I(Wrist.Inner, "⚙", "gear", "CONTROL", () => ToggleControl(), () => ControlRailPage != null);
            // outer ring: quick actions and the smaller windows
            I(Wrist.Outer, "▤", "windows", "TERMS", () => TogglePanel("terminals"), () => IsPanelOpen("terminals"));
            I(Wrist.Outer, "▣", "photos", "CLIP", () => TogglePanel("clipboard"), () => IsPanelOpen("clipboard"));
            I(Wrist.Outer, "◐", "aperture", "FOCUS", () => SetFocus(!Settings.focusEnabled), () => Settings.focusEnabled);
            I(Wrist.Outer, "⌖", "select", "SELECT", () => SetSelectionMode(!Router.SelectionMode), () => Router.SelectionMode, t.Warn);
            I(Wrist.Outer, "⌨", "keyboard", "KEYS", () => OpenKeyboard());
            // the system's keyboard on a table (Surface Keyboard and Touchpad): bring it now, or turn it on (owner 2026-11-10)
            I(Wrist.Outer, "⌨", "tablekeys", "TABLE", () => OpenTableKeyboard());
            I(Wrist.Outer, "⟲", "recenter", "CENTER", () => app?.Recenter());
            Wrist.Build();
            _wristModeApplied = !Settings.wristMenu; // applied below
            ApplyMainMenuMode();
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
