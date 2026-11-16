using System;
using System.Collections.Generic;
using MetaCodex.AI;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THREAD's message box grows (owner 2026-11-14, docs/thread-view.md). A grip on the composer's top edge: drag it up
    /// and the composer grows upwards while the chat above gets shorter by the same amount; drag it down and it shrinks.
    /// It never goes below one row, and it stops at ComposerShareMax of the window's content height or where the chat
    /// would keep less than ComposerChatMinimum. While dragging, the composer and its row follow at once; the chat is laid
    /// out again at most four times a second (once the box moved ComposerRelayoutMin) and when the drag ends. SEND and
    /// the attach modal put it back to one row; another thread and voice "reset" drop the grip's height.
    /// It also fits its text (owner 2026-11-16: a long or dictated text must be readable in full before SEND; it showed
    /// one overflowing line): each time the text changes (typed, dictated, pasted, attached, a return's draft) TMP
    /// measures it once at the box's width and the box shows max(the grip's height, the text's height), up to the same
    /// limit; past it the box shows the text's last lines. A shorter text shrinks it again, never below the grip's height.
    public sealed partial class MetaCodexShell
    {
        public const float ComposerShareMax = 0.60f;      // the box at most this share of the window's content height
        public const float ComposerChatMinimum = 0.12f;   // metres the chat keeps (60 % alone would leave the chat ~5 cm)
        public const float ComposerVoiceStep = 0.06f;     // one "bigger" or "smaller" by voice: about three rows
        private const float ComposerRelayoutMin = 0.003f; // the chat is laid out again once the box moved this far (metres)
        private const float GripHeight = 0.014f;


        public UiDragGrip ComposerGripForTest => _thGrip;
        public float ComposerExtraForTest => _thComposerApplied;
        public float ChatExtraForTest => _thChatApplied;
        public float ComposerMaxExtraForTest => _thComposerMax;
        /// Self-tests: SEND, as the composer's button and keyboard do it.
        public void SendComposerForTest(string text) => SendToThread(text);

        /// THREAD is built: the grip over the composer, and the composer fits its text from now on. The limits come from
        /// the window's content height and the chat's height at one row.
        private void BuildComposerGrip(UiPanel p, Rect composer, float contentH, float chatH)
        {
            var t = UiTheme.Current;
            _thComposerMax = Mathf.Max(0f, Mathf.Min(ComposerShareMax * contentH - t.ButtonHeight, chatH - ComposerChatMinimum));
            _thComposerWant = _thComposerFit = _thComposerApplied = _thChatApplied = 0f;
            _thGrip = p.Add(new UiDragGrip
            {
                Rect = GripRect(composer),
                // up: from the height shown (it may fit a long text); down: the grip's own height, so a drag never
                // pushes it below what the text needs and the box shrinks to the text when the text gets shorter
                OnDragStep = dy => SetComposerWanted((dy < 0f ? ComposerShown : _thComposerWant) - dy, false),
                OnDragEnd = () => SyncComposerChat(true),
            });
            // bound to this window: dictation, the keyboard and the attach / return flows may run in another scope
            if (_thComposer != null) _thComposer.TextChanged = Bind<UiTextField>(_ => FitComposerToText());
        }

        /// The bar sits on the composer's top edge, across the gap under the attachment row.
        private static Rect GripRect(Rect composer) => new Rect(composer.x, composer.y - 0.009f, composer.width, GripHeight);

        /// The growth above one row the box shows: the grip's (or voice's) height or its text's, whichever is taller,
        /// up to the most.
        private float ComposerShown => Mathf.Min(_thComposerMax, Mathf.Max(_thComposerWant, _thComposerFit));

        /// The grip, a voice command or a reset asked for a height. The attach modal lays out at one row: no change then.
        private void SetComposerWanted(float growth, bool now)
        {
            if (_thComposer == null || _amOpen) return;
            _thComposerWant = Mathf.Clamp(growth, 0f, _thComposerMax);
            ApplyComposerRows();
            if (now) SyncComposerChat(true);
        }

        /// One row again, the text's height dropped until the text changes: SEND (the text leaves; a send that fails
        /// fits it again), the attach modal (laid out for one row; closing it fits it again). At once.
        public void ResetComposerHeight()
        {
            if (_amOpen) return;
            _thComposerFit = 0f;
            SetComposerWanted(0f, true);
        }

        /// Another thread in this window: the grip's height is dropped; a text still in the box keeps showing in full.
        private void ResetComposerToText()
        {
            if (_amOpen) return;
            _thComposerWant = 0f;
            FitComposerToText();
        }

        /// The text changed (UiTextField.TextChanged), a send failed, the attach modal closed: TMP measures the text once
        /// at the box's width (UiTextField.PreferredBoxHeight) and the box grows or shrinks to it, never below the grip's
        /// height, at most to the limit (past it the box shows the text's last lines). The chat follows at once; nothing
        /// moves (and nothing is laid out) when the text still needs the same height.
        private void FitComposerToText()
        {
            var f = _thComposer;
            if (f == null) return;
            float one = f.Rect.height - _thComposerApplied;
            _thComposerFit = Mathf.Max(0f, f.PreferredBoxHeight(one) - one);
            if (_amOpen) return; // the modal is laid out for one row: CloseAttachModal fits it again
            ApplyComposerRows();
            SyncComposerChat(true);
        }

        /// The composer grows upwards: its top edge and the grip move, its bottom row (+, ◉, SEND) stays. The attachment
        /// row above it moves with its top. Cheap: a few rects, the text's page.
        private void ApplyComposerRows()
        {
            if (_thComposer == null) return;
            float shown = ComposerShown;
            float d = shown - _thComposerApplied;
            if (Mathf.Abs(d) < 1e-5f) return;
            _thComposerApplied = shown;
            var r = _thComposer.Rect;
            _thComposer.SetRect(new Rect(r.x, r.y - d, r.width, r.height + d));
            _thGrip?.SetRect(GripRect(_thComposer.Rect));
            foreach (var chip in _attChips) MoveY(chip, -d);
            MoveY(_attMore, -d);
            MoveY(_attHint, -d);
            MoveY(_attMenuFiles, -d);
            MoveY(_attMenuCapture, -d);
            MoveY(_attMenuClose, -d);
        }

        /// The chat area is shorter by the growth: the chat, its scroll buttons, the status line, the approval row and
        /// the ↓ NEW button follow. Not forced: only when the box moved ComposerRelayoutMin since the last layout (the
        /// caller runs it at most four times a second); forced at the end of a drag, a voice command or a reset.
        private void SyncComposerChat(bool force)
        {
            if (_thTimeline == null) return;
            float d = _thComposerApplied - _thChatApplied; // the box as it is now (the grip's or the text's height)
            if (Mathf.Abs(d) < 1e-5f || (!force && Mathf.Abs(d) < ComposerRelayoutMin)) return;
            _thChatApplied = _thComposerApplied;
            _tlArea = new Rect(_tlArea.x, _tlArea.y, _tlArea.width, _tlArea.height - d);
            _thTimeline.SetRect(TimelineRect(_askShown));
            LayoutTimelineButtons(_askShown);
            MoveY(_thStatus, -d);
            if (_thStatusFull.width > 0f) { var sf = _thStatusFull; sf.y -= d; _thStatusFull = sf; } // the status line's full width, kept for the SEND NOW case
            MoveY(_thSendNow, -d);
            MoveY(_thAsk, -d);
            foreach (var b in _thAskButtons) MoveY(b, -d);
        }

        /// Called four times a second while THREAD is open (TickThreadView): the chat catches up with a drag.
        private void TickComposerChat() => SyncComposerChat(false);

        private static void MoveY(UiElement e, float dy)
        {
            if (e == null) return;
            var r = e.Rect;
            e.SetRect(new Rect(r.x, r.y + dy, r.width, r.height));
        }

        // ------------------------------------------------------------------ voice (the command registry)

        private void RegisterComposerVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "thread_composer",
                Description = "THREAD's message box (the composer under the chat): bigger (taller, the chat above gets shorter), smaller, or reset to one row. Sending a message resets it too",
                Parameters = VoiceCommands.Schema(("action", "string", new[] { "bigger", "smaller", "reset" }, "bigger: taller by one step; smaller: shorter by one step; reset: back to one row", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["action"] = new Dictionary<string, string>
                    {
                        ["expand"] = "bigger", ["enlarge"] = "bigger", ["grow"] = "bigger", ["larger"] = "bigger", ["taller"] = "bigger", ["more room"] = "bigger",
                        ["shrink"] = "smaller", ["reduce"] = "smaller", ["shorter"] = "smaller", ["less"] = "smaller", ["compact"] = "smaller", ["collapse"] = "smaller",
                        ["default"] = "reset", ["normal"] = "reset", ["original"] = "reset", ["standard"] = "reset", ["back to normal"] = "reset",
                    },
                },
                Phrases = new[]
                {
                    "{action} [the|my|this] (text box|message box|message field|message area|composer|text field|input box|box) [please]",
                    "{action} [the|my|this] (message|text|input) (box|field|area|composer) [please]",
                    "(make|set|turn|keep|get) [the|my|this] (text box|message box|message field|message area|composer|text field|input box) [to|as|at] {action}",
                    "(return|restore|put) [the|my|this] (text box|message box|message field|message area|composer|input box) [back] [to] [the] (default|normal|original|standard) [size|height] {action:reset}",
                },
                Samples = new[]
                {
                    "expand the message box so I can read it", "make the text box bigger", "shrink the composer back down", "reset the message box to its normal size",
                    "the text box is too small, make it taller", "put the message box back to the default size",
                },
                Examples = new List<(string, string)> { ("expand the message box", "thread_composer bigger"), ("reset the text box", "thread_composer reset") },
                Vocabulary = new[] { "composer", "text box", "message box" },
                Run = a => ComposerVoice((string)a["action"]),
            });
            UpdateSidebarVoice(); // enabled only while THREAD is open
        }

        private string ComposerVoice(string action)
        {
            if (_thComposer == null || !ThreadWinOpen) return "error: open a thread first"; // the focused window
            if (_amOpen) return "error: close the attach window first";
            // steps from the height shown (it may fit a long text); it never gets smaller than its text needs
            float before = ComposerShown;
            switch (action)
            {
                case "bigger":
                    SetComposerWanted(before + ComposerVoiceStep, true);
                    return ComposerShown >= _thComposerMax ? "Message box is at its largest" : "Message box bigger";
                case "smaller":
                    SetComposerWanted(before - ComposerVoiceStep, true);
                    return ComposerShown <= 0f ? "Message box is at one row" : ComposerShown >= before - 1e-4f ? "Message box already fits its text" : "Message box smaller";
                default:
                    SetComposerWanted(0f, true); // the grip's height dropped; a long text still shows in full
                    return ComposerShown > 0f ? "Message box fits its text" : "Message box back to one row";
            }
        }
    }
}
