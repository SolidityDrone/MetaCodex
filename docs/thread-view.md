# THREAD as a chat, and the thread pulse

Owner, 2026-11-11: threads showed only commands and a bare "Thinking"; it was not clear the agent worked or was stuck.
Wanted: a normal Q&A chat, the last prompt pinned on top once the answers push it away, thinking (opened with a tap),
actions and the answer told apart, streaming text, context and usage limits, a clear "working / stuck" signal, voice
input, and a compact state for the local model ("running a task since 3 min", "re-running the test after edits") so it
can answer short and fast without reading the conversation.

## What MQH gives (bb's timeline, measured on the headset)

- `GET /api/v1/threads/:id/timeline?includeNestedRows=true`: rows (user and agent messages; `turn` rows with their
  steps as `children`, also for finished turns, which bb shows collapsed); reasoning is a `system` row with
  `operationKind: "reasoning"` (title "Thought for 792ms", the text in `detail`); commands carry output and exit code;
  file changes carry the path and line counts (OpenCode reports 0/0). The agent's message grows while it is written
  (buffered deltas), the reasoning streams in `activeThinking`; `pendingTodos`, `contextWindowUsage` come along.
- `&afterTag=<tag of the last answer>`: a delta (`upsertRows`, `rowOrder` as runs of old rows, changed `fields`) while
  MQH still knows that answer, else the whole timeline. The app polls every 0.8 s while the agent works, 2.5 s when
  idle, for the last 8 exchanges (`segmentLimit=8`); an unchanged timeline costs a few bytes. The merge, the parse and
  the chat blocks run on the request's pool thread (parse + pulse of 336 rows: 0.8 ms on the PC, a few ms on a Quest
  core): a frame only lays out what changed. The chat keeps each block's laid-out lines while the block's text and state
  stay the same: an update wraps only new and changed blocks (docs/perf.md).
- `GET /api/v1/system/usage-limits?providerId=`: plan and windows (OpenCode Go on the headset: 5 hour 0 %, weekly 0 %,
  monthly 40 %). Asked once a minute while THREAD is open; the account e-mail is never shown.

## The chat (`UI/UiChatView.cs`, `Shell/ShellThreadView.cs`)

- An exchange = your prompt (a bubble on the right), the agent's work under one header, the answer (full width, a
  green bar; markdown-lite: headings, bullets, quotes, code blocks and tables in the terminal font, **bold** / `code`
  / links coloured).
- The work header: `◐ Working  2 thoughts · 4 commands (1 failed) · 3 files +40 −12` while it works (open: every
  step on its own line), `⚙ Worked 4:31 …` once done (closed; a tap opens it). Steps: `✦ Thought 2 s  <its first
  sentence>` (a tap shows the reasoning; while thinking, its newest sentence streams under the header), `$ command`
  with ✓ / ✕ exit and its time (a tap shows the output; a failed one shows its last output line), `✎ path +3 −1`,
  reads and searches in a row as one line (`⌕ Read 6 files  a.cs, b.cs, …`), web, tools, plans, subagents, the
  agent's updates in dim text. Errors and questions to you stay outside the closed header.
- The sticky prompt: when the prompt of what is on screen scrolled away above, it is pinned on top (two lines); a tap
  scrolls back to it.
- Scrolling: drag, or ▲ ▼. At the bottom the view follows new text; scrolled up it holds still and `↓ NEW` appears.
- The status line under the chat: `◐ Running npm test · 0:45   turn 3:12 · live` (or `quiet 0:40`); amber
  `⚠ No news for 2:30 while thinking: it may be stuck. ■ STOP ends the turn.`; `⚠ Waiting for your approval (…)`;
  `✓ Answered 14:05 · took 4:31`.
- The meta row: provider · project · path · branch, and on the right the plan and usage windows (amber ≥ 70 %, red
  ≥ 90 %). The CONTEXT ring stays in the chips; its picker lists the usage windows with their reset times.
- `◉` beside SEND (also in a draft, for its first message): speech to text into the field (a pause of 2 s or ◉ again
  ends it; up to 30 s); SEND stays yours.

## The message box (owner 2026-11-14)

Owner: sometimes a message is too long to read in the one-row composer. The composer is now a message box that grows.

- A grip (a bar with three dots) sits on the top edge of the message box. It lights up when hovered.
- Drag it up (pinch it with the ray, or poke it and push up): the box grows upwards. The chat above gets shorter by the
  same amount. Drag it down: the box gets smaller. It never goes below one row. The bottom row (`+`, `◉`, `SEND ▶`)
  does not move.
- The text wraps over the box's lines. When the text is longer than the box, the box shows the last lines, where the
  caret is. The box does not scroll: it grows instead, up to its limit. Before this change the box showed one line and
  cut the rest with `…`.
- The limit is 60 % of the window's content height, and the chat keeps at least 12 cm. At 60 % alone the chat would
  keep about 5 cm. Both numbers are constants in `Shell/ShellThreadComposer.cs` (`ComposerShareMax`,
  `ComposerChatMinimum`).
- While you drag, the box and its grip follow at once. The chat is laid out again at most four times a second, and
  when the drag ends. Its text is not laid out again for each small move.
- Back to one row: SEND (the button, or Enter on the keyboard), a thread that another one replaces (opening another
  thread, or a new draft), and the attach window (`+` then ATTACH FILES or REFERENCE CAPTURE: its layout is for one row).
  The grip does nothing while the attach window is open. The height is not kept after a restart.
- Voice, command `thread_composer` (only while THREAD is open): "make the text box bigger" or "expand the message box"
  (one step: 6 cm); "shrink the composer" (one step smaller); "reset the text box" (one row).

## When a thread finishes (owner 2026-11-11)

- The orb casts a callout: `✓ “Fix the build” answered: <its first sentence> ›` (green), `⚠ … stopped on an error`
  (red), `◆ … needs you: waiting for your approval (…)` (amber). A tap on it opens that thread. It also goes to the
  orb's MESSAGES and its unread sparks.
- When that thread is open, its window's frame pulses three times (2.4 s) and the answer's green bar and background
  glow with it, so the end is clear even while looking elsewhere in it.
- Only changes seen live raise it (nothing for threads already done at start); the other threads are read every 5 s
  while one of them works (15 s otherwise), so their callout comes within seconds. The open window's own polls
  catch its thread's end at once.

## The pulse (`AI/ThreadPulse.cs`, for the session context)

`ThreadTimeline.Parse` turns the rows into exchanges; `ThreadPulse.From` boils the last one down:

| Fact | Example |
|---|---|
| state, phase, since | working 3 min, now running `npm test` (45 s) |
| quiet / may be stuck | no news for 2 min 30 s while thinking: it may be stuck (limits: thinking 150 s, answering 90 s, between steps 120 s, a command 10 min) |
| asked | the tests fail, fix them |
| this turn | 1 thought, 3 commands (2 failed), 2 files changed (+8 -3) |
| pattern | fixing: re-ran `npm test` 3 times (2 failed) with edits between: it is fixing the failures before it answers; also errors, testing, editing, installing, exploring, planning, done |
| last thought / update / answer | its newest sentence, first sentence |

- The open thread's pulse comes from its panel's polls; other threads that work get a small read (last 2 exchanges)
  every 15 s. Changes go into the mid-term memory (started, a step failed, fixing, may be stuck, waiting, answered
  after 4 min, an error).
- Questions: "what is it doing?", "is it stuck?" are answered from the pulse in two sentences,
  no model (a long command adds the ps sampler's CPU verdict); "is the build stuck?" stays with the process vitals;
  recaps ("what did it achieve?") go to the local model with the pulse lines (a few hundred characters), not the
  conversation.
- Like the rest of the session context: only for the local model, never sent to MQH, the agents or the control port.

## Thread windows and groups (owner 2026-11-14)

Owner: "we shall be able to open multiple threads in the first place, and then merge or pull out from the grouping".
The thread cards of before are gone. Each thread now has a full window, and windows can be grouped.

### Several windows (`Shell/ShellThreadWindows.cs`)

- Up to 6 THREAD windows are open at the same time. Their ids are `thread`, then `thread-2` to `thread-6`.
- Each window has its own thread, chat, status line, composer, chips, approvals, attachments and draft.
- The FOCUSED window is the last window that you touched or opened. Its title has a dot (● THREAD 2) when more than
  one window is open. Voice commands, the clipboard's TO THE THREAD and the session context use the focused window.
- Where a thread opens:
  - A thread that is open already: its window gets the focus.
  - Else the focused window, when it is closed or empty.
  - Else THREAD, when it is closed.
  - Else a new window, beside the focused window.
  - When 6 windows are open, the thread replaces the thread of the focused window.
- A thread of a project that has a window open joins the group of that window (see below). A thread of another
  project opens as a free window.
- The large ✕ at the top right closes that window only. The thread stays in MQH and in THREADS.
- A window that does not have the focus polls MQH half as often.
- Voice: "open the thread about the login page in a new window" (`find_thread`, `window: new`), "... in this window"
  (`window: this`), "close this thread window", "switch to the other thread window" (`thread_windows`).

### Groups (`Shell/ShellThreadGroups.cs`)

- A group is a window that holds thread windows in a grid: 2 windows in 2x1, 3 or 4 in 2x2, 5 or 6 in 3x2.
- The windows in a group are smaller, but they keep their full chat and composer. They work as free windows do.
- Make a group: pinch the title bar of a window, move it onto another window and release it. The target window
  lights up while the carried window is over it.
- Automatic groups: when a thread opens and a window of the same project is open, the new window joins that group.
  Two free windows of one project make a new group.
- In a group:
  - Release a window on another window of the same group: the two windows change places.
  - Release a window on a window of another group: it moves to that group, at that place.
  - Release a window outside its group: it comes out at its full size. That thread does not join a group again by
    itself while its window stays open.
  - The ✕ of a window closes that window only. A group with one window left goes, and that window is free.
- The title bar of the group moves the group. ⇲ UNGROUP sets all its windows free, side by side. The ✕ of the group
  closes all its windows.
- RESIZE (bottom right of the group): pinch and pull. The group and its windows get larger or smaller. The top-left
  corner stays where it is. The scale is between 0.4 and 1.2.
- The sidebar of a window in a group opens at the right edge of the group, not inside the grid.
- Placement, RECENTER and the FLOAT ring move a group as one window.
- Moves are smooth: a window that joins, leaves or changes place moves to its new place in about 0.25 s.
- Voice: "put the open threads side by side" (`thread_windows group`), "break up the group" (`ungroup`), "close the
  whole group of threads" (`close_group`).
- Groups are not kept when the app restarts.

### How the code works

- The per-window state was a set of fields of `MetaCodexShell`. It is now a `ThreadWindow` object. The old field names
  are properties of the current window `_tw`, so the thread code did not change much.
- Outside a scope, `_tw` is the focused window. `using (In(w))` makes `w` the current window for a block. Each
  window's frame tick, its build and `OpenThreadIn` use this.
- A press on a panel focuses its window first (`UiInputRouter.Touched`). Buttons, the composer and every MQH answer
  are bound to their window (`Bind`). A window that closed drops the late answers.
- There is one sidebar. It belongs to the window that opened it last (`_sbOwner`), and it sits beside that window or
  its group (`SbAnchor`).
- A group's windows are children of the group's panel. They are not in the layout lists (`IsGroupedPanel`).
- Self-test section `threadwindows` (`Editor/MetaCodexUiTestThreadWindows.cs`). It was not run on the owner's PC.

## Tests

- Pure checks (no Unity, Unity's own .NET 8 and Roslyn): `C:\Users\pnncl\mcxcheck-chat\test\build.sh` on the
  real OpenCode thread from the headset, synthetic turns, deltas, the chat's markdown and line mapping: 30 ok.
- Editor self-test section `chat` (`Editor/MetaCodexUiTestChat.cs`), plus `context` and `workspace` updated:
  `~/mcxwork/tools/uirun.sh chat context workspace captures` (not run yet on 2026-11-11: Windows had 1.6-1.9 GB free).
- Headset: docs/TEST_CHECKLIST.md section 25.
