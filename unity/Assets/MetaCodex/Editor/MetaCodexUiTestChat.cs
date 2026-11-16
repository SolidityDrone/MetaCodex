using System;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.Shell;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `chat` (owner 2026-11-11, docs/thread-view.md): THREAD as a chat. A finished exchange (work
    /// closed under one header, the answer in markdown-lite), a live one (work open, reads grouped, a command running,
    /// the status line), the sticky prompt, opening and closing, the view holding still while new lines come, MQH's
    /// deltas, the usage limits, dictation into the composer, the message box's grip, its fit to a long (dictated) text, voice and reset on SEND, and the session context's answer from the pulse.
    public static partial class MetaCodexUiTest
    {
        private static JObject ChatRow(string kind, string id, long start, JObject f)
        {
            var o = new JObject { ["kind"] = kind, ["id"] = id, ["threadId"] = "thr_chat", ["startedAt"] = start, ["createdAt"] = start + 400, ["sourceSeqStart"] = start / 1000 % 100000, ["sourceSeqEnd"] = start / 1000 % 100000 };
            foreach (var p in f.Properties()) o[p.Name] = p.Value;
            return o;
        }

        private static void ChatSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.ForgetSidebarMemoryForTest(); // THREAD alone (its sidebar folded)
            // the glyphs the chat draws
            string glyphs = "◌◐◓◑◒✦⌕↗☰◆⚙✎✓✕▸▾▌⚠↓◉▶▲│─•";
            UiTheme.Current.Body.HasCharacters(glyphs, out uint[] missing, true, true);
            Check(missing == null || missing.Length == 0, "the chat's glyphs are in the fonts" + (missing != null && missing.Length > 0 ? ": missing " + string.Join(" ", missing.Select(c => "U+" + c.ToString("X4"))) : ""));

            long t0 = 1_800_000_000_000;
            var row = new JObject
            {
                ["id"] = "thr_chat", ["projectId"] = "prj_chat", ["providerId"] = "codex", ["title"] = "Docs for the chat", ["status"] = "active",
                ["runtime"] = new JObject { ["displayStatus"] = "active" }, ["environmentPath"] = "/root/app", ["environmentBranchName"] = "main", ["updatedAt"] = t0,
            };
            var done = new JArray
            {
                ChatRow("system", "r1", t0 + 1000, new JObject { ["systemKind"] = "operation", ["operationKind"] = "reasoning", ["title"] = "Thought for 3s", ["detail"] = "The test fails on an off-by-one. Fix the loop bound in a.js.", ["status"] = "completed", ["completedAt"] = t0 + 4000 }),
                ChatRow("work", "c1", t0 + 5000, new JObject { ["workKind"] = "command", ["command"] = "npm test", ["output"] = "FAIL src/a.test.js\n  expected 2, got 3", ["exitCode"] = 1, ["status"] = "completed", ["completedAt"] = t0 + 9000 }),
                ChatRow("work", "e1", t0 + 10000, new JObject { ["workKind"] = "file-change", ["change"] = new JObject { ["path"] = "src/a.js", ["kind"] = "update", ["diffStats"] = new JObject { ["added"] = 3, ["removed"] = 1 } }, ["status"] = "completed" }),
                ChatRow("work", "c2", t0 + 11000, new JObject { ["workKind"] = "command", ["command"] = "npm test", ["output"] = "PASS", ["exitCode"] = 0, ["status"] = "completed", ["completedAt"] = t0 + 15000 }),
            };
            var live = new JArray
            {
                ChatRow("system", "r2", t0 + 62000, new JObject { ["systemKind"] = "operation", ["operationKind"] = "reasoning", ["title"] = "Thought for 1s", ["detail"] = "Read the panels first.", ["status"] = "completed", ["completedAt"] = t0 + 63000 }),
                ChatRow("work", "f1", t0 + 64000, new JObject { ["workKind"] = "file-read", ["path"] = "src/panel.js", ["status"] = "completed", ["completedAt"] = t0 + 64100 }),
                ChatRow("work", "f2", t0 + 64200, new JObject { ["workKind"] = "file-read", ["path"] = "src/chat.js", ["status"] = "completed", ["completedAt"] = t0 + 64300 }),
            };
            for (int i = 0; i < 40; i++)
                live.Add(ChatRow("work", "cx" + i, t0 + 65000 + i * 1000, new JObject { ["workKind"] = "command", ["command"] = "node tools/check.js part" + i, ["output"] = "ok", ["exitCode"] = 0, ["status"] = "completed", ["completedAt"] = t0 + 65500 + i * 1000 }));
            live.Add(ChatRow("work", "c9", t0 + 90000, new JObject { ["workKind"] = "command", ["command"] = "npm run docs", ["output"] = "", ["exitCode"] = null, ["status"] = "pending", ["completedAt"] = null }));
            var rows = new JArray
            {
                ChatRow("conversation", "u1", t0, new JObject { ["role"] = "user", ["text"] = "Make the build green" }),
                new JObject { ["kind"] = "turn", ["id"] = "t1:turn", ["turnId"] = "t1", ["status"] = "completed", ["startedAt"] = t0 + 500, ["createdAt"] = t0 + 16000, ["completedAt"] = t0 + 16000, ["summaryCount"] = 4, ["sourceSeqEnd"] = 16, ["children"] = done },
                ChatRow("conversation", "a1", t0 + 16000, new JObject { ["role"] = "assistant", ["text"] = "All tests pass now. **Changed** `src/a.js`:\n- fixed the off-by-one\n- added a test\n\n```js\nfor (let i = 0; i < n; i++) {}\n```" }),
                ChatRow("conversation", "u2", t0 + 60000, new JObject { ["role"] = "user", ["text"] = "Now write the docs for the chat view: how blocks open and close, what the status line says, and how the prompt stays pinned at the top when the answer grows." }),
                new JObject { ["kind"] = "turn", ["id"] = "t2:turn", ["turnId"] = "t2", ["status"] = "pending", ["startedAt"] = t0 + 61000, ["createdAt"] = t0 + 90000, ["completedAt"] = null, ["summaryCount"] = 0, ["sourceSeqEnd"] = 90, ["children"] = live },
            };
            shell.LoadThreadForTest(row, rows, new JObject { ["usedTokens"] = 30000, ["modelContextWindow"] = 200000, ["estimated"] = false }, null, new JArray { new JObject { ["id"] = "prj_chat", ["name"] = "app", ["sources"] = new JArray() } }, false);
            shell.SetClockForTest(t0 + 120000);
            var th = shell.PanelsById["thread"];
            var chat = shell.ChatForTest;
            th.ForceRefresh();
            string all = chat.PlainText;
            // the finished exchange: closed work, the answer in full (markdown-lite)
            Check(all.Contains("Worked 0:16") && all.Contains("1 thought · 2 commands (1 failed) · 1 file +3 −1") && !all.Contains("$ npm test\n"),
                "a finished exchange: one closed header “Worked 0:16 · 1 thought · 2 commands (1 failed) · 1 file +3 −1”, its steps hidden");
            Check(all.Contains("All tests pass now. Changed src/a.js:") && all.Contains("• fixed the off-by-one") && all.Contains("for (let i = 0; i < n; i++) {}") && !all.Contains("**") && !all.Contains("```"),
                "the answer in markdown-lite: bold and code marks gone, bullets, the code block in the terminal font");
            // the live exchange: open, reads grouped, the running command, the status line
            Check(all.Contains("Working") && all.Contains("Read 2 files  panel.js, chat.js") && all.Contains("$ npm run docs"), "the live exchange: work open, the two reads on one line, the running command shown");
            string status = shell.ThreadStatusTextForTest;
            var pulse = shell.OpenThreadPulse;
            Check(pulse != null && pulse.State == "working" && pulse.Phase == "running" && status.Contains("Running `npm run docs`") && status.Contains("0:30") && status.Contains("turn 1:00"),
                $"the status line: what it does, for how long, the turn's time ({UiChatView.Esc(status).Length} chars): " + Trim(status.Replace("<noparse>", "").Replace("</noparse>", ""), 140));
            // the sticky prompt: at the bottom the live exchange fills the view, its prompt scrolled away above
            Check(chat.StickyShown && chat.StickyText.StartsWith("Now write the docs for the chat view"), "the prompt of what is on screen stays pinned at the top: “" + Trim(chat.StickyText, 50) + "”");
            chat.ClickStickyForTest();
            Check(!chat.StickyShown && chat.VisibleText.Contains("Now write the docs") && chat.Scroll > 0, $"a tap on it scrolls back to the prompt (scrolled {chat.Scroll} lines; sticky {(chat.StickyShown ? "“" + Trim(chat.StickyText, 30) + "”" : "gone")}; top line “{Trim(chat.VisibleText.Split('\n').FirstOrDefault() ?? "", 40)}”)");
            // open the finished work and a thought
            chat.ScrollToBottom();
            Check(chat.Toggle("u1:work") && chat.PlainText.Contains("$ npm test") && chat.PlainText.Contains("Thought 3 s"), "a tap opens the finished work: its thought and commands");
            Check(chat.Toggle("r1") && chat.PlainText.Contains("The test fails on an off-by-one."), "a tap on a thought shows its text");
            chat.Toggle("r1");
            chat.Toggle("u1:work");
            // scrolled up, new content does not move the view; it counts as new and ↓ NEW appears
            chat.ScrollToBottom();
            chat.ScrollBy(8);
            int s0 = chat.Scroll;
            string top0 = chat.VisibleText.Split('\n').FirstOrDefault();
            var more = (JArray)rows.DeepClone();
            ((JArray)more[4]["children"]).Add(ChatRow("work", "c10", t0 + 100000, new JObject { ["workKind"] = "command", ["command"] = "npm run lint", ["output"] = "", ["exitCode"] = null, ["status"] = "pending" }));
            ((JArray)more[4]["children"])[((JArray)more[4]["children"]).Count - 2]["status"] = "completed";
            ((JArray)more[4]["children"])[((JArray)more[4]["children"]).Count - 2]["exitCode"] = 0;
            shell.TimelineTagForTest = "tag-a";
            shell.ApplyTimelineForTest(new JObject { ["maxSeq"] = 120, ["tag"] = "tag-b", ["delta"] = new JObject { ["afterTag"] = "tag-a", ["upsertRows"] = new JArray { more[4] }, ["fields"] = new JObject() } });
            th.UpdateForTest();
            Check(chat.Scroll > s0 && chat.VisibleText.Split('\n').FirstOrDefault() == top0 && chat.NewLines > 0 && shell.PanelsById["thread"].Elements.OfType<UiButton>().Any(b => b.Visible && b.Text.StartsWith("↓")),
                $"a delta from MQH (one changed turn): applied; scrolled up, the view holds still ({s0} -> {chat.Scroll}), {chat.NewLines} new lines, ↓ NEW shown");
            Check(chat.PlainText.Contains("$ npm run lint") && shell.OpenThreadPulse.Doing.Contains("npm run lint") && shell.TimelineTagForTest == "tag-b", "the delta's rows are in: now running `npm run lint`; the next poll asks after tag-b");
            int shownBlocks = chat.Blocks.Count(b => chat.FirstLineOf(b.Key) >= 0);
            Check(chat.LastRelaid > 0 && chat.LastRelaid < shownBlocks, $"the update laid out only what changed: {chat.LastRelaid} of {shownBlocks} shown blocks wrapped again, the others kept their lines");
            // the answer streams in at the end, with a caret
            chat.ScrollToBottom();
            var stream = (JArray)more.DeepClone();
            ((JArray)stream[4]["children"])[((JArray)stream[4]["children"]).Count - 1]["status"] = "completed";
            ((JArray)stream[4]["children"])[((JArray)stream[4]["children"]).Count - 1]["exitCode"] = 0;
            ((JArray)stream[4]["children"]).Add(ChatRow("conversation", "a2", t0 + 110000, new JObject { ["role"] = "assistant", ["text"] = "The docs are in docs/thread-view.md. Blocks open with a tap" }));
            shell.ApplyTimelineForTest(new JObject { ["maxSeq"] = 130, ["tag"] = "tag-c", ["delta"] = new JObject { ["afterTag"] = "tag-b", ["upsertRows"] = new JArray { stream[4] }, ["fields"] = new JObject() } });
            Check(chat.PlainText.EndsWith("Blocks open with a tap▌") && shell.OpenThreadPulse.Phase == "answering" && chat.AtBottom, "the answer streams in at the end (a caret), the view follows it");
            // a delta that does not fit (MQH restarted): everything is asked again
            shell.ApplyTimelineForTest(new JObject { ["maxSeq"] = 131, ["tag"] = "tag-d", ["delta"] = new JObject { ["afterTag"] = "tag-c", ["upsertRows"] = new JArray { new JObject { ["id"] = "nope" } }, ["rowOrder"] = new JArray { new JArray(0, 1) }, ["fields"] = new JObject() } });
            Check(shell.TimelineTagForTest == null && chat.PlainText.EndsWith("Blocks open with a tap▌"), "a delta that does not fit is dropped: the next poll takes the whole timeline");
            // usage limits beside the context ring, never the account
            shell.SetUsageForTest("codex", new JObject { ["status"] = "ok", ["planLabel"] = "Plus", ["accountEmail"] = "someone@example.com", ["windows"] = new JArray
            {
                new JObject { ["label"] = "5 hour", ["usedPercent"] = 12, ["resetsAt"] = DateTimeOffset.UtcNow.AddHours(2).ToString("o") },
                new JObject { ["label"] = "Weekly", ["usedPercent"] = 74, ["resetsAt"] = DateTimeOffset.UtcNow.AddDays(3).ToString("o") },
            } });
            string lim = shell.LimitsTextForTest;
            Check(lim.Contains("Plus") && lim.Contains("5h") && lim.Contains("12%") && lim.Contains("week") && lim.Contains("74%") && lim.Contains("#FFB547") && !lim.Contains("example.com"),
                "the meta row: plan and usage windows (74 % in amber), no e-mail: " + Trim(lim, 120));
            Check(shell.ChipsForTest.Contains("15%"), "the context ring: " + shell.ChipsForTest);
            // dictation types into the composer, SEND stays the user's
            var comp = shell.ComposerForTest;
            comp.Set("");
            shell.DictateTextForTest(comp, "add a section about the status line");
            Check(comp.Text == "add a section about the status line" && shell.MicForTest != null && shell.MicForTest.Text == "◉", "◉ dictation puts the words in the composer (not sent): “" + comp.Text + "”");
            comp.Set("");
            // the session context: “what is this thread doing?” from the pulse, no model
            shell.SetPointedForTest("thread");
            string said = null;
            shell.AnswerSessionQuestion("what is this thread doing?", a => said = a);
            string ctx = shell.SessionContextFor("what has this thread achieved so far?");
            Check(said != null && said.StartsWith("It's been working for") && said.Contains("writing the answer"), "“what is this thread doing?” from the pulse: " + said);
            Check(ctx.Contains("THREAD: asked: Now write the docs") && ctx.Contains("THREAD: state: working for") && ctx.Contains("this turn so far: 1 thought, 42 commands, 2 reads") && ctx.Length < 1800,
                $"the recap's context is the pulse, not the conversation ({ctx.Length} chars)");
            // the picture
            th.ForceRefresh();
            RenderPanel(th, "ui-thread-chat.png");
            chat.ScrollBy(12);
            th.ForceRefresh();
            RenderPanel(th, "ui-thread-chat-sticky.png");
            // nothing overlaps: status line above the attach row, mic between the composer and SEND
            var els = th.Elements.Where(e => e.Visible).ToList();
            var send = els.OfType<UiButton>().First(b => b.Text == "SEND ▶");
            var mic = shell.MicForTest;
            Check(mic.Rect.xMax <= send.Rect.x + 1e-4f && comp.Rect.xMax <= mic.Rect.x + 1e-4f && chat.Rect.yMax <= els.OfType<UiLabel>().First(l => l.Text == shell.ThreadStatusTextForTest).Rect.y + 1e-4f,
                "layout: the chat, then the status line; composer | ◉ | SEND in a row");
            // THREAD's message box (owner 2026-11-14): the grip grows it upwards and the chat shrinks by the same amount,
            // voice steps it, the most is capped, and SEND puts it back to one row
            var grip = shell.ComposerGripForTest;
            float box0 = comp.Rect.height, chat0 = chat.Rect.height, rowBottom = comp.Rect.yMax;
            void Drag(float up)
            {
                Vector2 gp = grip.Rect.center;
                grip.OnPress(new UiPointerEvent { Point = gp, PressPoint = gp });
                for (int i = 1; i <= 5; i++) grip.OnDrag(new UiPointerEvent { Point = gp + new Vector2(0f, -up * i / 5f), PressPoint = gp });
                grip.OnRelease(new UiPointerEvent { Point = gp + new Vector2(0f, -up), PressPoint = gp }, true);
            }
            bool Layered() => Mathf.Abs(comp.Rect.height - (box0 + shell.ComposerExtraForTest)) < 1e-4f
                && Mathf.Abs(chat.Rect.height - (chat0 - shell.ChatExtraForTest)) < 1e-3f && Mathf.Abs(comp.Rect.yMax - rowBottom) < 1e-4f;
            Drag(0.06f);
            Check(Mathf.Abs(shell.ComposerExtraForTest - 0.06f) < 1e-3f && Layered(),
                $"the grip dragged up 6 cm: the box {box0 * 1000f:F0} -> {comp.Rect.height * 1000f:F0} mm, the chat {chat0 * 1000f:F0} -> {chat.Rect.height * 1000f:F0} mm, the bottom row stays");
            VoiceResult vrBox = null;
            shell.VoiceProcessText("make the text box bigger", x => vrBox = x);
            Check(vrBox != null && vrBox.Command == "thread_composer" && Mathf.Abs(shell.ComposerExtraForTest - 0.12f) < 1e-3f && Layered(),
                "voice “make the text box bigger”: one more step (" + vrBox?.Reply + ")");
            shell.VoiceProcessText("reset the text box", x => vrBox = x);
            Check(vrBox != null && vrBox.Command == "thread_composer" && Mathf.Abs(shell.ComposerExtraForTest) < 1e-5f && Layered() && Mathf.Abs(chat.Rect.height - chat0) < 1e-3f,
                "voice “reset the text box”: one row, the chat its old height (" + vrBox?.Reply + ")");
            Drag(-0.3f);
            Check(Mathf.Abs(shell.ComposerExtraForTest) < 1e-5f && Layered(), "dragged down past one row: it stays at one row");
            Drag(1f);
            Check(Mathf.Abs(shell.ComposerExtraForTest - shell.ComposerMaxExtraForTest) < 1e-4f && shell.ComposerMaxExtraForTest > 0.1f && chat.Rect.height >= MetaCodexShell.ComposerChatMinimum - 1e-3f && Layered(),
                $"dragged up a metre: it stops at the most ({shell.ComposerMaxExtraForTest * 1000f:F0} mm above one row); the chat keeps {chat.Rect.height * 1000f:F0} mm");
            Drag(-1f);
            Drag(0.06f);
            comp.Set("a long message that goes into the box for the send check");
            shell.SendComposerForTest(comp.Text);
            Check(Mathf.Abs(shell.ComposerExtraForTest) < 1e-5f && Mathf.Abs(comp.Rect.height - box0) < 1e-4f && Mathf.Abs(chat.Rect.height - chat0) < 1e-3f && Layered(),
                "SEND: the message box is one row again, the chat its old height");
            comp.Set("");
            // the box fits its text (owner 2026-11-16): a long dictated text grows it until all of it shows (not sent); a
            // shorter one shrinks it, never below the grip's height; past the most it stops and shows the text's end
            string sentence = "please add a short section about the status line and its timers to the docs, with one example for each state. ";
            string longSaid = string.Concat(Enumerable.Repeat(sentence, 4)).Trim();
            shell.DictateTextForTest(comp, longSaid);
            float fit = shell.ComposerExtraForTest;
            Check(comp.Text == longSaid && fit > 0.01f && fit < shell.ComposerMaxExtraForTest - 1e-4f && comp.PageCountForTest == 1 && Layered(),
                $"a long dictated text ({longSaid.Length} chars): the box grew {fit * 1000f:F0} mm and shows all of it ({comp.PageCountForTest} page), not sent");
            comp.Set("ok");
            Check(Mathf.Abs(shell.ComposerExtraForTest) < 1e-5f && Layered(), "the text gets short: the box is one row again");
            Drag(0.06f);
            comp.Set(longSaid);
            Check(Mathf.Abs(shell.ComposerExtraForTest - Mathf.Max(0.06f, fit)) < 1e-3f && Layered(),
                $"grip 60 mm, the long text again: the box is the taller of the two ({shell.ComposerExtraForTest * 1000f:F0} mm)");
            comp.Set("ok");
            Check(Mathf.Abs(shell.ComposerExtraForTest - 0.06f) < 1e-3f && Layered(), "the text gets short: back to the grip's 60 mm, not below");
            Drag(-1f);
            comp.Set(string.Concat(Enumerable.Repeat(sentence, 40)));
            Check(Mathf.Abs(shell.ComposerExtraForTest - shell.ComposerMaxExtraForTest) < 1e-4f && comp.PageCountForTest > 1 && comp.PageShownForTest == comp.PageCountForTest && Layered(),
                $"a text past the most: the box stops at the most, the chat keeps {chat.Rect.height * 1000f:F0} mm, it shows its last lines (page {comp.PageShownForTest} of {comp.PageCountForTest})");
            shell.SendComposerForTest(comp.Text);
            Check(Mathf.Abs(shell.ComposerExtraForTest) < 1e-5f && Mathf.Abs(comp.Rect.height - box0) < 1e-4f && Layered(), "SEND with a long text: one row again at once");
            comp.Set("");
            var lintBox = VoiceCommands.Lint(shell.Voice.Find("thread_composer"));
            Check(lintBox.Count == 0, lintBox.Count == 0 ? "registry: thread_composer is complete (lint clean)" : "registry: " + string.Join("; ", lintBox));
            // the turn ends: the orb casts "answered" (a tap opens the thread), the open window pulses, the answer glows
            int notes0 = shell.Toasts.History.Count;
            var doneRow = (JObject)row.DeepClone();
            doneRow["status"] = "idle";
            doneRow["runtime"]["displayStatus"] = "idle";
            var doneRows = (JArray)stream.DeepClone();
            doneRows[4]["status"] = "completed";
            doneRows[4]["completedAt"] = t0 + 115000;
            ((JArray)doneRows[4]["children"])[((JArray)doneRows[4]["children"]).Count - 1]["text"] = "The docs are in docs/thread-view.md. Blocks open with a tap.";
            shell.LoadThreadForTest(doneRow, doneRows, null, null, null, false);
            var note = shell.Toasts.History.Count > notes0 ? shell.Toasts.History[0] : default;
            Check(note.Kind == "ok" && note.Text.StartsWith("“Docs for the chat” answered: The docs are in docs/thread-view.md.") && shell.ThreadPanelPulsingForTest && chat.FlashingKey == "a2",
                $"the turn ends: the orb says “{note.Text}”, the open window pulses ({shell.ThreadPanelPulsingForTest}), the answer glows ({chat.FlashingKey})");
            Check(!chat.PlainText.Contains("▌") && chat.PlainText.Contains("Worked") && shell.ThreadStatusTextForTest.Contains("Answered"), "done: no caret, the work closed to “Worked …”, the status line says Answered");
            int notes1 = shell.Toasts.History.Count;
            shell.SetClockForTest(t0 + 125000);
            Check(shell.Toasts.History.Count == notes1, "nothing more on the next pulses (one callout per change)");
            shell.SetClockForTest(0);
            shell.ClosePanel("thread");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }
    }
}
