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
    ///
    /// The wrist menu only exists while it can be used (2026-11-15: its first frame cost 3 s on the Quest 3S, and the DOCK
    /// is the default): on the headset it is made when WRIST is the main menu, at the start or when the user switches to it
    /// later, and then in small steps over some frames (UiWristMenu.BuildStaged), the DOCK staying open until it is ready.
    /// In the Editor and in batch mode the self-tests read Wrist right after Build, so there it is made whole, at once.
    public sealed partial class MetaCodexShell
    {
        public UiWristMenu Wrist { get; private set; }
        private bool _wristModeApplied;
        private bool _mainMenuStarted; // BuildWrist ran: from then on the main menu mode is applied (ApplyMainMenuMode)
        private float _wristMadeAt;    // Time.realtimeSinceStartup when a staged wrist menu was made

        private void BuildWrist()
        {
            StartMark("wrist");
            if (_mainMenuStarted) return;
            _mainMenuStarted = true;
            _wristModeApplied = !Settings.wristMenu; // so that the first ApplyMainMenuMode below acts: the mode is applied once at the start
            // the Editor and the batch self-tests read Wrist right after Build: made whole, now (as it always was)
            bool whole = Application.isEditor || !Application.isPlaying || Application.isBatchMode;
            if (Wrist == null && (whole || Settings.wristMenu)) MakeWrist(!whole);
            ApplyMainMenuMode();
        }

        /// Makes the wrist menu: whole now (the Editor and batch self-tests), or staged (UiWristMenu.BuildStaged, ready a few
        /// frames later: OnWristBuilt).
        private void MakeWrist(bool staged)
        {
            Wrist = UiWristMenu.Create(_root, UiTheme.Current);
            Wrist.Head = Head;
            Wrist.Hand = h =>
            {
                var st = Gestures?.Get(h);
                return st != null && st.Frame.Tracked ? (true, st.Frame) : (false, default(HandFrame));
            };
            ApplyLookTo(Wrist, WristCfg); // its own size, text size, glass and core (CONTROL > WRIST MENU), before it is built
            FillWristItems();
            if (!staged) { Wrist.Build(); return; }
            _wristMadeAt = Time.realtimeSinceStartup;
            Wrist.Built += OnWristBuilt;
            Wrist.BuildStaged();
        }

        /// One table for the dock and the wrist menu (MainMenuItems): the same labels, order and lit states.
        private void FillWristItems()
        {
            var t = UiTheme.Current;
            Wrist.Inner.Clear();
            Wrist.Outer.Clear();
            foreach (var m in MainMenuItems())
            {
                int step = m.LockStep;
                (m.Outer ? Wrist.Outer : Wrist.Inner).Add(new UiWristMenu.Item
                {
                    Glyph = m.Glyph, Icon = m.Icon, Label = m.Label, Act = m.Act, Lit = m.Lit, LitTint = m.Tint ?? t.Ok,
                    Locked = step > 0 ? () => SetupLocked(step) : (Func<bool>)null, // the first start's setup (ShellSetupLocks)
                });
            }
        }

        /// The main menu's entries changed (a plugin's tile came or went): the wrist menu takes the new table. With no wrist
        /// menu yet (the DOCK is the main menu) there is nothing to do: it reads MainMenuItems() when it is first made.
        public void RebuildWristItems()
        {
            if (Wrist == null) return;
            FillWristItems();
            Wrist.RebuildItems();
        }

        /// A staged wrist menu is ready: when WRIST is still the main menu, the DOCK goes and the wrist menu starts to look
        /// for the wrist. (If the user went back to DOCK meanwhile, Dismiss put it away already: it stays so.)
        private void OnWristBuilt()
        {
            Log($"wrist menu: built in {Wrist.BuildFrames} frames ({(Time.realtimeSinceStartup - _wristMadeAt) * 1000f:F0} ms)");
            if (!Settings.wristMenu) return;
            Wrist.Resume();
            SetDockOpen(false);
        }

        /// One entry of the main menu. The dock and the wrist menu are built from the same table (MainMenuItems), so they
        /// keep the same labels, order and lit states; `WristOnly` entries are not on the dock.
        public sealed class MainMenuItem
        {
            /// Its id in the saved layout (ShellSettings.wrist): a system item's label in lower case ("talk", "control"), a
            /// plugin's tile "plugin:<plugin>/<tile id>".
            public string Id;
            public string Glyph, Icon, Label;   // the dock shows the glyph, the wrist a line icon of UiIconAtlas
            /// A plugin's tile: the plugin's title (the WRIST MENU page's ADD list says whose it is) and the icon the plugin
            /// asked for (before the user's pick).
            public bool Plugin;
            public string Source, OwnIcon;
            public Action Act;
            public Func<bool> Lit;              // lit (wrist) / selected (dock) while this is true
            public Color? Tint;                 // the wrist's lit colour (default: ok green)
            public bool Outer, WristOnly;
            /// The first start's setup step it waits for (ShellSetupLocks; 0: none): dimmed with 🔒 until that step is over.
            public int LockStep;
        }

        /// The main menu as the user arranged it (CONTROL > WRIST MENU; the app's default until then): the inner ring's items
        /// in order (Outer false), then the outer ring's (Outer true). The dock shows them in this order, without the
        /// wrist-only ones. Unknown ids in the saved layout (a plugin that is gone) are skipped; items the saved layout does
        /// not name (a new system item, a new plugin's tile) go to the end of the outer ring (WristLayout).
        public List<MainMenuItem> MainMenuItems() => MainMenuItemsFor(WristCfg, null);

        /// Every item the main menu can carry, in the app's order, each in its default ring (Outer). The default menu (11
        /// tiles): inner ring = what is used most (owner 2026-11-11: 19 tiles were too many; setup is in CONTROL), outer ring
        /// = quick actions and the smaller windows, then the app plugins' tiles. `ws` gives the plugin tiles' icons the
        /// user picked.
        private List<MainMenuItem> MenuCatalog(WristMenuSettings ws)
        {
            var t = UiTheme.Current;
            var list = new List<MainMenuItem>();
            void I(bool outer, string glyph, string icon, string label, Action act, Func<bool> lit = null, Color? tint = null, bool wristOnly = false, int lockStep = 0) =>
                list.Add(new MainMenuItem { Id = label.ToLowerInvariant(), Outer = outer, Glyph = glyph, Icon = icon, Label = label, Act = act, Lit = lit, Tint = tint, WristOnly = wristOnly, LockStep = lockStep });
            // lockStep: the first start's setup step each waits for (ShellSetupLocks): voice 5, MQH 4, the Linux 3
            I(false, "◉", "mic", "TALK", () => VoiceToggle(), () => VoiceState == "listening" || VoiceState == "transcribing", t.Danger, lockStep: 5);
            // the terminals: one tile (owner 2026-11-13: TERM next to TERMS was redundant); TERMINALS lists them, hidden ones
            // too (tap one to show it), + NEW opens another
            I(false, "▤", "windows", "TERMS", () => TogglePanel("terminals"), () => IsPanelOpen("terminals"), lockStep: 3);
            I(false, "◫", "folder", "FILES", () => TogglePanel("files"), () => IsPanelOpen("files"), lockStep: 3);
            I(false, "☰", "threads", "THREADS", () => TogglePanel("threads"), () => IsPanelOpen("threads"), lockStep: 4);
            I(false, "◎", "globe", "WEB", () => TogglePanel("web"), () => IsPanelOpen("web"));
            // settings, voice, agents, MQH (skills, plugins, scripts, gestures, status), vault and git, system: one place
            I(false, "⚙", "gear", "CONTROL", () => ToggleControl(), () => ControlRailPage != null);
            I(true, "▣", "photos", "CLIP", () => TogglePanel("clipboard"), () => IsPanelOpen("clipboard"));
            I(true, "◐", "aperture", "FOCUS", () => SetFocus(!Settings.focusEnabled), () => Settings.focusEnabled);
            I(true, "⌖", "select", "SELECT", () => SetSelectionMode(!Router.SelectionMode), () => Router.SelectionMode, t.Warn);
            // the system keyboard for the terminal (Horizon OS can lay it on a table: Settings > Experimental > Surface keyboard)
            I(true, "⌨", "keyboard", "KEYS", () => OpenKeyboard(), () => app?.Input != null && app.Input.SystemKeyboardOpen);
            I(true, "⟲", "recenter", "CENTER", () => app?.Recenter());
            AddPluginMenuItems(list, outer: true, ws); // the app plugins' tiles (menu.add; ShellPluginUi)
            return list;
        }

        /// DOCK (false) or WRIST (true).
        public void SetWristMenu(bool on)
        {
            Settings.wristMenu = on;
            OnSettingsChanged();
        }

        /// Brings the chosen main menu and puts the other away; runs when the setting changed, whoever changed it (and once
        /// at the start, from BuildWrist: before that it does nothing). DOCK mode needs no wrist menu. WRIST mode makes it if
        /// it does not exist yet (staged) and keeps the DOCK open until it is ready (OnWristBuilt). Every settings change
        /// passes here (OnSettingsChanged): the wrist menu's own settings are applied first (ApplyWristLook, cheap when
        /// nothing changed).
        private void ApplyMainMenuMode()
        {
            ApplyWristLook();
            if (!_mainMenuStarted || _wristModeApplied == Settings.wristMenu) return;
            _wristModeApplied = Settings.wristMenu;
            if (!Settings.wristMenu)
            {
                // the wrist menu goes with its closing animation (never at once), and comes back on the next look
                if (Wrist != null) Wrist.Dismiss();
                SetDockOpen(true);
                return;
            }
            if (Wrist == null) MakeWrist(true); // switched on later (DISPLAY, voice, a script): the DOCK stays until it is built
            else if (_wristStale) RebuildWrist(); // its table changed while the DOCK was the main menu (plugin tiles, the layout)
            Wrist.Resume(); // a staged build that was put away goes on
            if (Wrist.Ready) SetDockOpen(false);
        }

        /// The DOCK in front of the user (open: placed by default when it was closed) or put away.
        private void SetDockOpen(bool open)
        {
            if (!_panels.TryGetValue("dock", out var dock)) return;
            if (!open) { if (dock.IsOpen) dock.Close(); }
            else if (!dock.IsOpen && Head != null) { PlaceDefault("dock", dock); dock.Open(); }
        }

        /// Tests: one step of the wrist menu (the Editor has no frame loop).
        public void TickWristForTest(float dt) { if (Wrist != null && Wrist.enabled) { Wrist.Head = Head; Wrist.Tick(dt); } }
    }
}
