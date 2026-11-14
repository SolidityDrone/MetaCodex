using System.Collections.Generic;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The first start's locks (owner 2026-11-14: "if a user clicks TALK by mistake he is prevented, instead of clicking
    /// while the model is down: no errors, no UX uncertainty"). While the one-time setup runs (ShellFirstRun), what needs
    /// a step that is not over yet is shown dimmed with 🔒 and, pressed, does nothing but one short callout by the orb:
    /// "🔒 TALK is ready after step 5 of 5 · one-time setup". Each lock lifts by itself when its step ends (done, failed
    /// or skipped: a failed feature then says why through its own paths, as before).
    ///   step 5 (the voice's decision model): TALK on the dock, the wrist menu and the orb's menu; every other way to
    ///     start listening goes through VoiceListen (gesture bindings `voice:*`, Lua voice.listen, the control port
    ///     `voice listen`), and dictation into a text field (DictateInto);
    ///   step 4 (MQH): THREADS, THREAD, NEW THREAD, the thread sidebar, WORKSPACES, CONTROL > AGENTS, the agents' API KEY;
    ///   step 3 (the Linux): TERMINALS (the window, + NEW, showing a hidden one, voice "new terminal"), FILES, GIT.
    /// The checks sit where every way in passes (OpenPanel, OpenControl, VoiceListen, TerminalNew, ShowTerminal), so the
    /// dock, the wrist menu, the orb, voice, gesture bindings, Lua and the control port are all covered. Settings
    /// (CONTROL), WEB, CLIP, FOCUS, SELECT, KEYS, CENTER and the tour stay usable. The voice registry is left as it is:
    /// disabling commands for a minute would change the decision model's examples while it is being built.
    public sealed partial class MetaCodexShell
    {
        /// Bit n-1 set: step n is not over (what needs it is locked); -1 before the first look.
        private int _lockMask = -1;
        private string _lockCalloutFor;
        private float _lockCalloutAt = -10f;
        private readonly List<(UiButton button, MainMenuItem item)> _dockLocks = new List<(UiButton, MainMenuItem)>();
        private UiButton _orbTalkBtn;

        /// Step `step` (1..5) of the first start's setup is not over: what needs it is locked.
        public bool SetupLocked(int step) => _setupActive && _setup != null && step >= 1 && step <= SetupSteps && !_setup[step - 1].Resolved;

        /// The setup step a window needs (0: none): the Linux (3), MQH (4).
        public static int PanelNeedsStep(string id)
        {
            switch (id)
            {
                case "terminals": case "files": case "git": return 3;
                case "threads": case "thread": case "thread-new": case "agents": case "agent-key": case "workspaces": case SidebarId: return 4;
                default: return 0;
            }
        }

        private static string PanelLockName(string id)
        {
            switch (id)
            {
                case "terminals": return "TERMINALS";
                case "files": return "FILES";
                case "git": return "GIT";
                case "threads": return "THREADS";
                case "thread": return "THREAD";
                case "thread-new": return "NEW THREAD";
                case "agents": return "AGENTS";
                case "agent-key": return "API KEY";
                case "workspaces": return "WORKSPACES";
                case SidebarId: return "THREAD's sidebar";
                default: return (id ?? "").ToUpperInvariant();
            }
        }

        /// The callout for a locked feature.
        public static string LockText(int step, string what) => $"🔒 {what} is ready after step {step} of {SetupSteps} · one-time setup";

        /// True when `step` is not over yet: the orb says so once (the same feature again within 2.5 s: nothing) and the
        /// caller does nothing else.
        public bool SetupBlocks(int step, string what)
        {
            if (!SetupLocked(step)) return false;
            float now = Time.unscaledTime;
            if (what != _lockCalloutFor || now - _lockCalloutAt > 2.5f)
            {
                _lockCalloutFor = what;
                _lockCalloutAt = now;
                Toast(LockText(step, what), "info");
            }
            return true;
        }

        /// Every frame of the setup: when a step ends, its locks lift (the tiles, the orb's TALK, the rail, the panel's note).
        private void TickSetupLocks()
        {
            int mask = 0;
            if (_setupActive && _setup != null)
                for (int i = 0; i < SetupSteps; i++) if (!_setup[i].Resolved) mask |= 1 << i;
            if (mask == _lockMask) return;
            _lockMask = mask;
            ApplySetupLocks();
        }

        /// The lock marks now: the dock's tiles (🔒 in place of the glyph, dimmed), the orb's TALK, CONTROL's rail. The
        /// wrist menu reads its items' Locked each frame it draws.
        private void ApplySetupLocks()
        {
            ApplyDockLocks();
            ApplyOrbTalkLock();
            if (_rail != null && _rail.IsOpen) RefreshRail();
            RefreshSetupLocksLabel();
        }

        private void ApplyDockLocks()
        {
            foreach (var (b, m) in _dockLocks)
            {
                bool locked = SetupLocked(m.LockStep);
                if (b.Dimmed == locked) continue;
                b.SetDimmed(locked);
                b.SetText((locked ? "🔒" : m.Glyph) + "\n" + m.Label);
            }
        }

        private void ApplyOrbTalkLock()
        {
            if (_orbTalkBtn == null) return;
            bool locked = SetupLocked(5);
            if (_orbTalkBtn.Dimmed == locked) return;
            _orbTalkBtn.SetDimmed(locked);
            _orbTalkBtn.SetText(locked ? "🔒\nTALK" : "◉\nTALK");
        }

        /// The SETUP panel's footer: what waits for which step (made only when a lock changes).
        private void RefreshSetupLocksLabel()
        {
            if (_setupLocksLabel == null || _setup == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < SetupSteps; i++)
            {
                if (string.IsNullOrEmpty(_setup[i].Unlocks) || !SetupLocked(i + 1)) continue;
                sb.Append(sb.Length == 0 ? "🔒 Waiting for their step: " : " · ").Append(_setup[i].Unlocks).Append(" (step ").Append(i + 1).Append(')');
            }
            if (sb.Length == 0) sb.Append(_setupActive ? "Nothing waits any more: the last steps finish in the background." : "Everything is unlocked.");
            _setupLocksLabel.Set(sb.ToString());
        }
    }
}
