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
                return st != null && st.Frame.Tracked ? (true, st.Frame) : (false, default(HandFrame));
            };
            void I(List<UiWristMenu.Item> list, string glyph, string label, Action act, Func<bool> lit = null, Color? tint = null) =>
                list.Add(new UiWristMenu.Item { Glyph = glyph, Label = label, Act = act, Lit = lit, LitTint = tint ?? t.Ok });
            // inner ring: what is used most
            I(Wrist.Inner, "◉", "TALK", () => VoiceToggle(), () => VoiceState == "listening" || VoiceState == "transcribing", t.Danger);
            I(Wrist.Inner, "▶", "TERM", () => ShowOrNewTerminal());
            I(Wrist.Inner, "◫", "FILES", () => TogglePanel("files"), () => IsPanelOpen("files"));
            I(Wrist.Inner, "☰", "THREADS", () => TogglePanel("threads"), () => IsPanelOpen("threads"));
            I(Wrist.Inner, "◆", "AGENTS", () => TogglePanel("agents"), () => IsPanelOpen("agents"));
            I(Wrist.Inner, "◐", "FOCUS", () => SetFocus(!Settings.focusEnabled), () => Settings.focusEnabled);
            // outer ring: everything else the dock has
            I(Wrist.Outer, "◎", "WEB", () => TogglePanel("browser"), () => IsPanelOpen("browser"));
            // skills, plugins, scripts, gestures and the daemon link: one MQH panel (was SCRIPTS and GESTURE here)
            I(Wrist.Outer, "⬡", "MQH", () => TogglePanel("mqh"), () => IsPanelOpen("mqh"));
            I(Wrist.Outer, "⬢", "SYSTEM", () => TogglePanel("system"), () => IsPanelOpen("system"));
            I(Wrist.Outer, "♫", "VOICE", () => TogglePanel("voice"), () => IsPanelOpen("voice"));
            I(Wrist.Outer, "⚿", "VAULT", () => TogglePanel("vault"), () => IsPanelOpen("vault"));
            I(Wrist.Outer, "⎇", "GIT", () => TogglePanel("git"), () => IsPanelOpen("git"));
            I(Wrist.Outer, "⌖", "SELECT", () => SetSelectionMode(!Router.SelectionMode), () => Router.SelectionMode, t.Warn);
            I(Wrist.Outer, "⌨", "KEYS", () => OpenKeyboard());
            I(Wrist.Outer, "⟲", "CENTER", () => app?.Recenter());
            I(Wrist.Outer, "⚙", "SETUP", () => TogglePanel("settings"), () => IsPanelOpen("settings"));
            I(Wrist.Outer, "▤", "TERMS", () => TogglePanel("terminals"), () => IsPanelOpen("terminals"));
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
            Wrist.enabled = Settings.wristMenu;
            if (!Settings.wristMenu) Wrist.HideNow();
            if (_panels.TryGetValue("dock", out var dock))
            {
                if (Settings.wristMenu) { if (dock.IsOpen) dock.Close(); }
                else if (!dock.IsOpen && Head != null) { PlaceDefault("dock", dock); dock.Open(); }
            }
        }

        /// Tests: one step of the wrist menu (the Editor has no frame loop).
        public void TickWristForTest(float dt) { if (Wrist != null) { Wrist.Head = Head; Wrist.Tick(dt); } }
    }
}
