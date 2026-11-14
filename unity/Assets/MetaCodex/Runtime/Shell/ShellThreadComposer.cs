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
    /// out again at most four times a second (once the box moved ComposerRelayoutMin) and when the drag ends. SEND,
    /// another thread, the attach modal and the voice command put it back to one row.
    public sealed partial class MetaCodexShell
    {
        public const float ComposerShareMax = 0.60f;      // the box at most this share of the window's content height
        public const float ComposerChatMinimum = 0.12f;   // metres the chat keeps (60 % alone would leave the chat ~5 cm)
        public const float ComposerVoiceStep = 0.06f;     // one "bigger" or "smaller" by voice: about three rows
        private const float ComposerRelayoutMin = 0.003f; // the chat is laid out again once the box moved this far (metres)
        private const float GripHeight = 0.014f;

        private UiDragGrip _thGrip;
        private float _thComposerMax;     // the most the box grows above one row (metres)
        private float _thComposerWant;    // the growth the grip or the voice asked for (metres above one row)
        private float _thComposerApplied; // the growth the composer and its row use now
        private float _thChatApplied;     // the growth the chat area uses now

        public UiDragGrip ComposerGripForTest => _thGrip;
        public float ComposerExtraForTest => _thComposerApplied;
        public float ChatExtraForTest => _thChatApplied;
        public float ComposerMaxExtraForTest => _thComposerMax;
        /// Self-tests: SEND, as the composer's button and keyboard do it.
        public void SendComposerForTest(string text) => SendToThread(text);

        /// THREAD is built: the grip over the composer. The limits come from the window's content height and the chat's
        /// height at one row.
        private void BuildComposerGrip(UiPanel p, Rect composer, float contentH, float chatH)
        {
            var t = UiTheme.Current;
            _thComposerMax = Mathf.Max(0f, Mathf.Min(ComposerShareMax * contentH - t.ButtonHeight, chatH - ComposerChatMinimum));
            _thComposerWant = _thComposerApplied = _thChatApplied = 0f;
            _thGrip = p.Add(new UiDragGrip
            {
                Rect = GripRect(composer),
                OnDragStep = dy => SetComposerWanted(_thComposerWant - dy, false),
                OnDragEnd = () => SyncComposerChat(true),
            });
        }

        /// The bar sits on the composer's top edge, across the gap under the attachment row.
        private static Rect GripRect(Rect composer) => new Rect(composer.x, composer.y - 0.009f, composer.width, GripHeight);

        /// The grip, a voice command or a reset asked for a height. The attach modal lays out at one row: no change then.
        private void SetComposerWanted(float growth, bool now)
        {
            if (_thComposer == null || _amOpen) return;
            _thComposerWant = Mathf.Clamp(growth, 0f, _thComposerMax);
            ApplyComposerRows();
            if (now) SyncComposerChat(true);
        }

        /// One row again: SEND, another thread, the attach modal. At once.
        public void ResetComposerHeight() => SetComposerWanted(0f, true);

        /// The composer grows upwards: its top edge and the grip move, its bottom row (+, ◉, SEND) stays. The attachment
        /// row above it moves with its top. Cheap: a few rects, the text's page.
        private void ApplyComposerRows()
        {
            if (_thComposer == null) return;
            float d = _thComposerWant - _thComposerApplied;
            if (Mathf.Abs(d) < 1e-5f) return;
            _thComposerApplied = _thComposerWant;
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
            float d = _thComposerWant - _thChatApplied;
            if (Mathf.Abs(d) < 1e-5f || (!force && Mathf.Abs(d) < ComposerRelayoutMin)) return;
            _thChatApplied = _thComposerWant;
            _tlArea = new Rect(_tlArea.x, _tlArea.y, _tlArea.width, _tlArea.height - d);
            _thTimeline.SetRect(TimelineRect(_askShown));
            LayoutTimelineButtons(_askShown);
            MoveY(_thStatus, -d);
            if (_thStatusFull.width > 0f) _thStatusFull.y -= d; // the status line's full width, kept for the SEND NOW case
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
            if (_thComposer == null || !(_panels.TryGetValue("thread", out var th) && th.IsOpen)) return "error: open a thread first";
            if (_amOpen) return "error: close the attach window first";
            switch (action)
            {
                case "bigger":
                    SetComposerWanted(_thComposerWant + ComposerVoiceStep, true);
                    return _thComposerWant >= _thComposerMax ? "Message box is at its largest" : "Message box bigger";
                case "smaller":
                    SetComposerWanted(_thComposerWant - ComposerVoiceStep, true);
                    return _thComposerWant <= 0f ? "Message box is at one row" : "Message box smaller";
                default:
                    ResetComposerHeight();
                    return "Message box back to one row";
            }
        }
    }
}
