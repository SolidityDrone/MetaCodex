using System;
using System.Collections.Generic;
using MetaCodex.App;
using MetaCodex.Terminal;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The terminals' frames (owner 2026-11-16: "avoid them stacking on top of one another ... make the terminal smaller:
    /// it's way too big and it doesn't scale like the UI ... we must be able to select, and copy"):
    /// - size: the grid (96 x 30 cells) is drawn at TerminalGridScale in its frame, so the frame is THREAD's width at UI SCALE
    ///   1.0 and its letters are the windows' body text size; the frame takes the UI SCALE like every window (BaseScale).
    ///   The key bar and the status line hang on the view and follow it; the pty keeps its columns and rows.
    /// - place: a new terminal (a frame's +, TERMS > + NEW, voice, a command run in a new terminal) and a hidden one shown
    ///   again open through OpenTerminalFrame: back where the user left it when that place is still free and in view, else
    ///   the free place that costs least (ShellPlacement: beside the window whose button made it, else in front), never on
    ///   another window.
    /// - select and copy: a pinch (or a fingertip) dragged over the text selects from cell to cell (UiTerminalSurface),
    ///   letting go keeps it, COPY in the title bar copies it: to the system clipboard (the system keyboard, WEB and every
    ///   PASTE read it) and to CLIPBOARD. A tap focuses the terminal and drops the selection. Rows scrolled back into the
    ///   history (⇞ on the key bar) can be selected too; a drag does not scroll.
    /// - a program's own copy (OSC 52: vim, tmux, a CLI's "copy") lands on both clipboards the same way.
    public sealed partial class MetaCodexShell
    {
        /// The grid's scale in its frame: at 1.0 the 96 x 30 cells of 21 mm rows made a frame 1.16 m wide (owner: "way too
        /// big"); at 0.62 it is 0.73 m (THREAD's width) and a row is 13 mm, the glyphs the size of the windows' body text.
        public const float TerminalGridScale = 0.62f;

        private const float TermPasteW = 0.085f, TermCopyW = 0.064f;

        /// Frames that were open once: shown again, they may go back where they were.
        private readonly HashSet<UiPanel> _termPlaced = new HashSet<UiPanel>();
        private readonly Dictionary<TerminalInstance, UiButton> _termCopy = new Dictionary<TerminalInstance, UiButton>();
        private bool _termSelectTipShown;

        /// Self-tests: copies go to CLIPBOARD and LastCopiedForTest only, not to the PC's own clipboard.
        public bool SystemClipboardForTest = true;
        public string LastCopiedForTest { get; private set; }

        /// The title bar, right to left: ✕ – + ⌨ ⎘ PASTE, COPY (the selection), then ⇗ (the newest link).
        /// COPY looks off until there is a selection; a press then says what to do.
        private void AddTerminalButtons(UiPanel frame, TerminalInstance inst, float w)
        {
            var t = UiTheme.Current;
            float y = 0.006f, h = t.HeaderHeight - 0.010f, x = w - 0.044f;
            frame.Add(new UiButton("✕", () => CloseOrHideTerminal(inst), UiButtonStyle.Danger) { Rect = new Rect(x, y, 0.036f, h) });
            x -= 0.042f;
            frame.Add(new UiButton("–", () => HideTerminal(inst), UiButtonStyle.Ghost) { Rect = new Rect(x, y, 0.036f, h) });
            x -= 0.042f;
            frame.Add(new UiButton("+", TerminalNew, UiButtonStyle.Ghost) { Rect = new Rect(x, y, 0.036f, h) });
            x -= 0.042f;
            frame.Add(new UiButton("⌨", () => app?.Input?.ToggleSystemKeyboard(), UiButtonStyle.Ghost) { Rect = new Rect(x, y, 0.036f, h) });
            x -= 0.006f + TermPasteW;
            frame.Add(new UiButton("⎘ PASTE", () => { app?.Focus(inst); app?.Input?.Paste(GUIUtility.systemCopyBuffer); }, UiButtonStyle.Ghost) { Rect = new Rect(x, y, TermPasteW, h) });
            x -= 0.006f + TermCopyW;
            _termCopy[inst] = frame.Add(new UiButton("COPY", () => CopyTerminalSelection(inst), UiButtonStyle.Ghost) { Rect = new Rect(x, y, TermCopyW, h), Dimmed = true });
            x -= 0.018f + 0.036f;
            _linkButtons[inst] = frame.Add(new UiButton("⇗", () => OpenTerminalLink(inst), UiButtonStyle.Ghost) { Rect = new Rect(x, y, 0.036f, h) });
        }

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

        // ------------------------------------------------------------------ select and copy

        /// A drag over the text ended (UiTerminalSurface).
        private void OnTerminalSelected(TerminalInstance inst)
        {
            RefreshTerminalCopy(inst);
            if (inst.View == null || !inst.View.HasSelection || _termSelectTipShown) return;
            _termSelectTipShown = true;
            Toast("Selected · COPY in the terminal's title bar copies it", "info");
        }

        /// COPY lights up while the terminal has a selection.
        private void RefreshTerminalCopy(TerminalInstance inst)
        {
            if (inst == null || !_termCopy.TryGetValue(inst, out var b) || b == null) return;
            bool on = inst.View != null && inst.View.HasSelection;
            b.SetDimmed(!on);
            b.SetSelected(on);
        }

        /// COPY (the frame's title bar, Ctrl+Shift+C on a keyboard): the selected text to both clipboards.
        public void CopyTerminalSelection(TerminalInstance inst)
        {
            string text = inst?.View != null ? inst.View.SelectedText() : "";
            if (string.IsNullOrEmpty(text)) { Toast("Nothing selected: pinch and drag over the terminal's text, then COPY", "info"); return; }
            CopyText(text, inst.Name, "term-" + inst.Number);
            Toast($"Copied {text.Length} characters", "ok");
        }

        /// Text to the clipboards: the system one (the system keyboard, WEB and every PASTE in the app read it) and
        /// CLIPBOARD's text list.
        public void CopyText(string text, string from, string fromId)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (SystemClipboardForTest) GUIUtility.systemCopyBuffer = text;
            LastCopiedForTest = text;
            try { Clipboard?.AddText(text, from, fromId); }
            catch (Exception ex) { Log("clipboard: " + ex.Message); }
        }

        /// A program in a terminal copied text (OSC 52).
        private void OnProgramCopied(TerminalInstance inst, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            CopyText(text, inst.Name, "term-" + inst.Number);
            Toast($"Copied {text.Length} characters from {inst.Name}", "ok");
        }

        /// Self-tests: a terminal frame's buttons and its text surface.
        public UiButton TerminalCopyButtonForTest(TerminalInstance inst) => _termCopy.TryGetValue(inst, out var b) ? b : null;

        /// Self-tests: a terminal framed for a test goes away again (its frame destroyed, as a closed terminal's).
        public void DropTerminalForTest(TerminalInstance inst)
        {
            if (inst == null || !_frames.TryGetValue(inst, out var f)) return;
            OnTerminalClosed(inst);
            UiToasts.Kill(f.gameObject);
        }
    }

    /// A terminal's text on its frame (Rect = the grid as the frame draws it, `scale` times the view's own size). A tap
    /// focuses that terminal and drops its selection; a pinch (or a fingertip) that moves 1.5 cells from where it pressed
    /// starts a selection at that cell and drags its other end, cell to cell; letting go keeps it.
    public sealed class UiTerminalSurface : UiElement
    {
        private readonly TerminalView _view;
        private readonly float _scale;
        private readonly Action _focus, _selected;
        private bool _dragging;
        public override bool Interactive => true;
        public bool Dragging => _dragging;

        public UiTerminalSurface(TerminalView view, float scale, Action focus, Action selected)
        {
            _view = view; _scale = Mathf.Max(0.05f, scale); _focus = focus; _selected = selected;
        }

        protected override void Build() { }

        /// A panel point -> the view's own metres from its top-left corner (y down).
        public Vector2 ViewPoint(Vector2 panelPoint) => (panelPoint - Rect.position) / _scale;

        public override void OnPress(UiPointerEvent e) => _dragging = false;

        public override void OnDrag(UiPointerEvent e)
        {
            if (_view == null || _view.Terminal == null) return;
            if (!_dragging)
            {
                if ((e.Point - e.PressPoint).magnitude < _view.CellWidth * _scale * 1.5f) return; // a tap's jitter is no drag
                _dragging = true;
                _view.BeginSelection(_view.CellAtClamped(ViewPoint(e.PressPoint)));
            }
            _view.ExtendSelection(_view.CellAtClamped(ViewPoint(e.Point)));
        }

        public override void OnRelease(UiPointerEvent e, bool inside)
        {
            if (!_dragging) { if (inside) OnClick(e); return; }
            _dragging = false;
            _focus?.Invoke();
            _selected?.Invoke();
        }

        public override void OnClick(UiPointerEvent e)
        {
            _view?.ClearSelection();
            _focus?.Invoke();
        }
    }
}
