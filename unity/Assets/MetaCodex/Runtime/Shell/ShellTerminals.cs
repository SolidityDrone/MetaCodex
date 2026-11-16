using System.Collections.Generic;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The terminals' frames (owner 2026-11-16: "avoid them stacking on top of one another ... make the terminal smaller:
    /// it's way too big and it doesn't scale like the UI"):
    /// - size: the grid (96 x 30 cells) is drawn at TerminalGridScale in its frame, so the frame is THREAD's width at UI SCALE
    ///   1.0 and its letters are the windows' body text size; the frame takes the UI SCALE like every window (BaseScale).
    ///   The key bar and the status line hang on the view and follow it; the pty keeps its columns and rows.
    /// - place: a new terminal (a frame's +, TERMS > + NEW, voice, a command run in a new terminal) and a hidden one shown
    ///   again open through OpenTerminalFrame: back where the user left it when that place is still free and in view, else
    ///   the free place that costs least (ShellPlacement: beside the window whose button made it, else in front), never on
    ///   another window.
    public sealed partial class MetaCodexShell
    {
        /// The grid's scale in its frame: at 1.0 the 96 x 30 cells of 21 mm rows made a frame 1.16 m wide (owner: "way too
        /// big"); at 0.62 it is 0.73 m (THREAD's width) and a row is 13 mm, the glyphs the size of the windows' body text.
        public const float TerminalGridScale = 0.62f;

        /// Frames that were open once: shown again, they may go back where they were.
        private readonly HashSet<UiPanel> _termPlaced = new HashSet<UiPanel>();

        /// The UI SCALE on every terminal frame, open or hidden (OnSettingsChanged).
        private void ApplyTerminalScales()
        {
            foreach (var f in _frames.Values) if (f != null) f.BaseScale = Vector3.one * UiScaleNow;
        }

        /// Every terminal frame that opens comes here (a new terminal, a hidden one shown again), as any window does
        /// (OpenPanelCore): back where the user left it when that place is still in view and free, else the free place that
        /// costs least (PlaceSmart: beside the window whose button made it, else in front of the user); never on another window.
        private void OpenTerminalFrame(UiPanel f)
        {
            f.BaseScale = Vector3.one * UiScaleNow;
            if (!_termPlaced.Contains(f) || !OldPlaceStillGood(f)) PlaceSmart(f.Id, f, OpenerNow());
            else FaceUser(f);
            f.Open();
            _termPlaced.Add(f);
            OnOpenedFloating(f);
        }
    }
}
