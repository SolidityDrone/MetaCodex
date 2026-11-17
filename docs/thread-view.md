# Thread windows: the chat, the message box, groups, held turns and archive

A thread window shows an agent thread as a chat: the prompts, the agent's work, its answer, a status line that says
whether the agent works or may be stuck, and a message box. Several windows can be open, alone or in groups. A
compact state of each thread (the pulse) lets the local model answer questions about it without reading the
conversation.

## What MQH gives (the timeline API)

- `GET /api/v1/threads/:id/timeline?includeNestedRows=true`: rows (user and agent messages; `turn` rows with their
  steps as `children`, also for finished turns, which the upstream web app shows collapsed); reasoning is a `system` row with
  `operationKind: "reasoning"` (title "Thought for 792ms", the text in `detail`); commands carry output and exit code;
  file changes carry the path and line counts (one agent CLI reports 0/0). The agent's message grows while it is written
  (buffered deltas), the reasoning streams in `activeThinking`; `pendingTodos`, `contextWindowUsage` come along.
- `&afterTag=<tag of the last answer>`: a delta (`upsertRows`, `rowOrder` as runs of old rows, changed `fields`) while
  MQH still knows that answer, else the whole timeline. The app polls every 0.8 s while the agent works, 2.5 s when
  idle, for the last 8 exchanges (`segmentLimit=8`); an unchanged timeline costs a few bytes. The merge, the parse and
  the chat blocks run on the request's pool thread (parse + pulse of 336 rows: 0.8 ms on the PC, a few ms on a Quest
  core): a frame only lays out what changed. The chat keeps each block's laid-out lines while the block's text and state
  stay the same: an update wraps only new and changed blocks (docs/perf.md).
- `GET /api/v1/system/usage-limits?providerId=`: plan and windows (for example 5 hours, a week, a month). Asked once a minute while THREAD is open; the account e-mail is never shown.

## The chat (`UI/UiChatView.cs`, `Shell/ShellThreadView.cs`)

- An exchange (owner 2026-11-17: clearly yours and the agent's): a dim `You · 14:05` line, your prompt in a bubble on
  the right (accent tint); then a dim `◆ <agent> · 14:06` line on the left, the agent's work under one header and the
  answer (full width, a green bar; markdown-lite: headings, bullets, quotes, code blocks and tables in the terminal
  font on a dark panel, **bold** / `code` / links coloured, long lines wrapped). A faint rule and a wider gap separate
  the turns. The chat text is 13 % larger than before (`ChatTextScale` 1.22 x the theme's small size; it was 1.08),
  the terminal font in proportion; line heights are the fonts' own at that size.
- The work header: `◐ Working  2 thoughts · 4 commands (1 failed) · 3 files +40 −12` while it works (open: every
  step on its own line), `⚙ Worked 4:31 …` once done (closed; a tap opens it). Steps:
  - `✦ Thought 2 s  <its first sentence>`: the agent's thinking when its provider sends it, dim, a tap shows it all;
    while thinking, its newest sentence streams under the header.
  - A command is a block: `$ npm test` in the terminal font, `in src/` (dim) when it ran outside the thread's folder,
    a status chip on the right (`● running 0:12` with its live time, `✓ ok 4 s`, `✕ exit 1 4 s`, `■ stopped`), and
    under it the last lines of its output on a dark panel: 6 while it runs, when it failed and for the newest command
    of the turn, 3 for older ones that passed. The output follows the polls (0.8 s while the agent works, so at most
    about one update a second). A tap shows the whole output (its last 8000 characters); a tap again closes it.
  - `✎ Edited src/app.ts (+3 −1)` (`Created`, `Deleted`, `Moved`): a tap opens its diff in place (+ lines green, −
    lines red); an edit without a diff of its own opens the file's diff in the sidebar's CHANGES.
  - Reads and searches: one compact line each (`⌕ Read src/panel.js`), several in a row as one line
    (`⌕ Read 6 files  a.cs, b.cs, …`, a tap lists them). Web, tools, plans, subagents, the agent's updates in dim text.
  - Waiting for you: `⚠ NEEDS YOUR APPROVAL  <tool>` or `? THE AGENT ASKS YOU …` as an amber card with "Answer with
    ALLOW ONCE, ALLOW SESSION or DENY under the chat." Errors are red cards. Both stay outside the closed header.
- NOW RUNNING: while the agent works, a tinted strip at the bottom of the chat (above the status line and the
  composer) says what it does this second, with a spinner and how long it has been at it: `RUNNING  $ npm test 0:45`,
  `THINKING…  <its newest sentence>`, `EDITING src/app.ts`, `READING …`, `SEARCHING …`, `ON THE WEB …`,
  `USING <tool>`, `SUBAGENT …`, `WRITING THE ANSWER`, `NEEDS YOU …` (amber), `WORKING  last: $ npm test`. It goes when
  the thread is idle. A tap on it scrolls to the end. Its spinner and time are two labels set 4 times a second while it
  shows; the chat is not drawn again for them, and nothing runs while the thread is idle.
- The sticky prompt: when the prompt of what is on screen scrolled away above, it is pinned on top (two lines); a tap
  scrolls back to it.
- Scrolling: drag the chat, or the scrollbar on its right edge (`UI/UiScrollbar.cs`, it replaced ▲ ▼): a thin track
  and a thumb as tall as the part on screen. Pinch (ray) or poke the thumb and drag it; a tap on the track above or
  below the thumb pages up or down. At the bottom the view follows new text; scrolled up it holds still and
  `↓ NEW` (new lines came) or `↓ LATEST` appears; a tap goes back to the end and following. The bar draws only when the
  view or its hover changes. `UiScrollbar` is a plain element (`SetState(view, content, offset)`, `OnScrollTo`,
  `OnPage`); only the chat uses it now.
- The status line under the chat: `◐ turn 3:12 · live · to-do 2/5: … · <pattern>` while it works (the step is in the
  NOW strip), amber `⚠ No news for 2:30 while thinking: it may be stuck. ■ STOP ends the turn.`,
  `⚠ Waiting for your approval (…)`, `✓ Answered 14:05 · took 4:31`.
- The meta row: provider · project · path · branch, and on the right the plan and usage windows (amber ≥ 70 %, red
  ≥ 90 %). The CONTEXT ring stays in the chips; its picker lists the usage windows with their reset times.
- `◉` beside SEND (also in a draft, for its first message): speech to text into the field (a pause of 2 s or ◉ again
  ends it; up to 30 s); SEND stays yours.

### How the chat lays out (and why blocks never overlap)

- Every line has a fixed slot: its font's line height at the chat's size (the font face's line height, as
  TextMeshPro spaces lines) plus the gaps around it. Blocks are laid out one after the other from these slots; the
  line tops are kept as running sums, so scrolling and the scrollbar find lines by binary search.
- Each block keeps its laid-out lines while what it is made of stays the same: an update (a token of the answer, a
  line of output) wraps only the blocks that changed; the others are copied.
- The overlap the owner saw (2026-11-17, "sometimes my message overlaps with the agent's answer"): TextMeshPro drew
  some texts taller than their slots. A line with a glyph from a symbol fallback font (Noto Sans Symbols: 2.05 em per
  line, Symbols 2: 1.70 em, Inter: 1.21 em; for example ⌕ ⎯ ⏳ ⏸ ⛔ 🟥 🟩 ⭐) pushed that line and every line after it
  in the same text object down, and a text whose first line had one moved down as a whole. A few such lines in an
  answer ran into the next block, often your next prompt. The same made one-line headers with ⌕ (the Read and Search
  rows) taller than their rect, and TextMeshPro's ellipsis then dropped their whole text. TextMeshPro also turned
  `\n` written in agent text into a line break (escape parsing is on in TMP Settings), and \v, \f, U+2028 and NUL
  broke or cut lines that the measuring had not counted.
- The fix: each drawn text is placed by its first baseline (Baseline alignment, so a tall glyph cannot move the first
  line) and its lines are spaced with a fixed `<line-height>` equal to the layout's line height; escape parsing is off
  for the chat's texts and its measurer; `ChatText.Sanitize` turns \v, \f, U+2028/9 into breaks, drops other control
  characters and terminal colour codes, keeps the last version of a line rewritten with \r (progress bars); headers
  are made one line (`ChatText.OneLine`).
- The guard: once after each draw, when TextMeshPro has built the meshes, the chat compares each drawn text's line
  count with the lines laid out for it. One with more lines gets the room it needs under its last line and the chat
  flows again; the session log says `chat re-flow: block <key> drew N lines where M were laid out: +X mm under it`.
  It reads only the texts on screen, and only after a draw.

## The message box

The composer is a message box that grows.

- A grip (a bar with three dots) sits on the top edge of the message box. It lights up when hovered.
- Drag it up (pinch it with the ray, or poke it and push up): the box grows upwards. The chat above gets shorter by the
  same amount. Drag it down: the box gets smaller. It never goes below one row. The bottom row (`+`, `◉`, `SEND ▶`)
  does not move.
- The text wraps over the box's lines. The box fits its text (owner 2026-11-16): each time the text changes (typed,
  said with `◉`, pasted, attached, a return's draft), it is measured once and the box grows upwards to show all of it,
  up to its limit, so you can read it all before `SEND ▶`. A shorter text makes it smaller again, but never smaller
  than the height you set with the grip. Past the limit the box shows the last lines, where the caret is.
- The limit is 60 % of the window's content height, and the chat keeps at least 12 cm. At 60 % alone the chat would
  keep about 5 cm. Both numbers are constants in `Shell/ShellThreadComposer.cs` (`ComposerShareMax`,
  `ComposerChatMinimum`).
- While you drag, the box and its grip follow at once. The chat is laid out again at most four times a second, and
  when the drag ends. Its text is not laid out again for each small move.
- Back to one row: SEND (the button, or Enter on the keyboard; a send that fails shows the text in full again), and the
  attach window (`+` then ATTACH FILES or REFERENCE CAPTURE: its layout is for one row; when it closes, the box fits its
  text again). A thread that another one replaces (opening another thread, or a new draft) drops the grip's height; a
  text still in the box keeps showing in full. The grip does nothing while the attach window is open. The height is not
  kept after a restart.
- Voice, command `thread_composer` (only while THREAD is open): "make the text box bigger" or "expand the message box"
  (one step: 6 cm); "shrink the composer" (one step smaller, never smaller than its text); "reset the text box" (one
  row, or the height its text needs).

## When a thread finishes

- The Whisp casts a callout: `✓ “Fix the build” answered: <its first sentence> ›` (green), `⚠ … stopped on an error`
  (red), `◆ … needs you: waiting for your approval (…)` (amber). A tap on it opens that thread. It also goes to the
  Whisp's MESSAGES, and up to four of the Whisp's motes turn gold while messages are unread.
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

## Thread windows and groups

Each thread opens in a full window, and windows can be grouped. A group can hold thread windows and web windows
(browser pages and the code editor) together.

### Several windows (`Shell/ShellThreadWindows.cs`)

- Up to 6 THREAD windows are open at the same time. Their ids are `thread`, then `thread-2` to `thread-6`.
- Each window has its own thread, chat, status line, composer, chips, approvals, attachments and draft.
- The FOCUSED window is the last window that you touched or opened. Its title has a dot (● PROJECT · THREAD TITLE) when more than
  one window is open. A window's title is its project and its thread's title (`UpdateWindowTitle`). Voice commands, the clipboard's TO THE THREAD and the session context use the focused window.
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

- A group is a window that holds windows in a grid: 2 windows in 2x1, 3 or 4 in 2x2, 5 or 6 in 3x2. The windows can
  be thread windows and web windows (a page, the code editor), in any mix: for example a thread beside the code
  editor of its project, or the editor beside a video (owner 2026-11-17).
- A cell is as wide as the widest window of the group and as tall as the tallest (a thread window is 0.73 x 0.66, a
  web window 0.80 x 0.61). Each window is centred across its cell, with its top at the top of the cell.
- The windows in a group are smaller, but they keep all they have: a thread its full chat and composer, a page its
  toolbar, touch, scroll, hover, keyboard, dropdown chooser, dialogs and full screen. They work as free windows do.
  A page's full screen fills its page area inside its cell (as in a free window); the group does not change.
- A web window that is large when free (the code editor, a large page: 1.25 times) has the same scale as the other
  windows in the group. It gets its larger size again when it comes out.
- Make a group: pinch the title bar of a window, move it onto another window and release it. The target window
  lights up while the carried window is over it. Any project and any kind: a drop groups windows of different
  projects too, and thread windows with web windows.
- Automatic groups (the only rule by project, thread windows only): when a thread opens and a window of the same
  project is open, the new window joins that group. Two free windows of one project make a new group. A web window
  never joins a group by itself.
- In a group:
  - Release a window on another window of the same group: the two windows change places.
  - Release a window on a window of another group: it moves to that group, at that place.
  - Release a window outside its group: it comes out at its full size. That thread does not join a group again by
    itself while its window stays open.
  - The ✕ of a window closes that window only. A group with one window left goes, and that window is free. A web
    window `web-2` or `web-3` that closes frees its page, as a free one does. The code editor's window closes, and the
    editor's server continues to run.
  - ❐ NEW WINDOW on a web window in a group: the new window opens free, beside the group. A page that the app opens
    in a web window that is in a group (a link, a bookmark, voice) loads there, and the window stays in its group.
- The title bar of the group moves the group. ⇲ UNGROUP sets all its windows free, side by side. The ✕ of the group
  closes all its windows.
- RESIZE (bottom right of the group): pinch and pull. The group and its windows get larger or smaller. The top-left
  corner stays where it is. The scale is between 0.4 and 1.2.
- The sidebar of a window in a group opens at the right edge of the group, not inside the grid.
- Placement, RECENTER and the FLOAT ring move a group as one window.
- With "Bend panels around you" on, a group bends like any window. Its thread windows have no curve of their own:
  they lie on the curve of the group, and a ray or a fingertip hits the element that is drawn under it. A web window
  never bends: it stays flat, on the plane that touches the curve at the centre of its cell, a little nearer to you,
  so that its side edges are in front of the group. Its page and the pointer agree everywhere on it.
- Moves are smooth: a window that joins, leaves or changes place moves to its new place in about 0.25 s.
- Voice: "put the open threads side by side" (`thread_windows group`), "break up the group" (`ungroup`), "close the
  whole group of threads" (`close_group`). These act on thread windows and on the group of the focused thread window
  (which can hold web windows). No voice command groups web windows yet: the command needs the registry bench.
- Groups are not kept when the app restarts.

### How the code works

- The per-window state is a `ThreadWindow` object. The thread code reads it through properties of the current
  window `_tw`.
- Outside a scope, `_tw` is the focused window. `using (In(w))` makes `w` the current window for a block. Each
  window's frame tick, its build and `OpenThreadIn` use this.
- A press on a panel focuses its window first (`UiInputRouter.Touched`). Buttons, the composer and every MQH answer
  are bound to their window (`Bind`). A window that closed drops the late answers.
- There is one sidebar. It belongs to the window that opened it last (`_sbOwner`), and it sits beside that window or
  its group (`SbAnchor`).
- A window that a group can hold is a `GroupMember` (`Shell/ShellThreadGroups.cs`): its id, panel, group, drag and
  glide, its size when free (`FreeScale`), `Flat` (never bends: web windows) and `Gone`. `ThreadWindow` and
  `WebWindow` (`Shell/ShellWeb.cs`) are members. A group is a `WindowGroup` (id `tgroup-N`).
- A group's windows are children of the group's panel. They are not in the layout lists (`IsGroupedPanel`).
  `TickMember` runs every frame for each thread window and each web window: the drag and the drop, the eased size
  and the glide (two loops over the lists, no allocation). A web window's size eases only in a group or just out of
  one; the web code keeps its own scale (a large page, the code editor) otherwise.
- Bent group: `CellPose` puts each window on the arc of the group at the angle of its cell, 1 cm in front (`CellDepth`),
  turned to the axis of the group. `FitMemberBend` gives it the radius of the group in its own units, so the two
  surfaces have the same axis. `FitGroupMembers` runs every frame and on `UiPanel.BendChanged` (the same frame the
  bend of the group eases). A window that leaves the group gets its own bend again. A flat window (web) gets the
  centre and the turn of its cell from `CellPose` and no bend; it is moved towards the axis of the group until its
  side edges are `CellDepth` in front of the curve.
- Session log: `window group: <window> (<project> | web page | code editor) let go on <window or group> (...)` for
  each drop.
- Self-test section `threadwindows` (`Editor/MetaCodexUiTestThreadWindows.cs`).

## Held turns and archive

### A held turn: SEND NOW, IN A WORKTREE, CANCEL (`Shell/ShellWorkspaces.cs`)

- MQH holds a turn while another thread works in the same folder (docs/workspaces.md). The window shows
  **▶ SEND NOW**. It asks "Start it now?" with three choices:
  - **SEND NOW**: the turn starts in the shared folder. Both agents then edit the same files.
  - **IN A WORKTREE**: the app reads the held message (`GET /threads/:id/queued-messages`), puts the worktree
    instruction in front of its text, saves it (`PATCH .../queued-messages/:qid` with the row's `updatedAt` as
    `expectedUpdatedAt`), and only then sends it (`POST .../send`). The agent makes or picks its own git worktree and
    branch with the skill `worktree-per-thread` and works there. The shared folder stays as it is.
  - **CANCEL**: the turn keeps waiting.
- The message's text items become one text item (the instruction, then the texts; their mentions move with them).
  Images and files stay as they are. If any step fails, a toast shows MQH's error and nothing is sent.
- The skill (`mqh/plugins/metacodex-guide/skills/worktree-per-thread`) tells the agent: never edit the shared
  checkout, reuse a worktree whose branch fits or `git worktree add -b agent/<name> <checkout>-wt-<name> <base>`,
  commit there, and report the path, the branch, the base and "MERGE in WORKSPACES when done".
- Limit: a worktree that an agent makes with git is not an MQH environment. WORKSPACES may not list it, and MQH
  still counts the thread in the checkout. The skill then tells the user to ask the agent to merge.

### Archive and delete (`Shell/ShellThreadArchive.cs`)

- Each thread window has **ARCHIVE…** and **DELETE…** at the top. They act on the thread of that window (not on the
  focused one). They show only while the window shows a thread.
  - ARCHIVE… asks first, then archives the thread and its child threads (`POST /threads/:id/archive-all`). Every
    window that shows one of them closes.
  - DELETE… reads the child count (`GET /threads/:id/child-summary`), asks with that count, then deletes
    (`DELETE /threads/:id`, `childThreadsConfirmed` true only when the question named child threads). The window
    closes.
- **THREADS > ▤ ARCHIVED** shows the archived threads (`GET /threads?archived=true`, one request when it is switched
  on, after a change and on ⟳). Tap a row: **RESTORE** (`POST /threads/:id/unarchive`, that thread only) or
  **DELETE…**. **◂ ACTIVE** goes back to the normal list.
- A thread archived or deleted elsewhere (another client, an agent): when it is missing from MQH's list, the window
  asks MQH for that thread (at most every 10 s). If it is archived or deleted, the window closes. Archived threads are
  not in MQH's normal list, so they never count as working and are not in the session context's open windows.
- Voice: `archive_thread` ("archive this chat", "archive the thread about <topic>") and `restore_thread`
  ("bring back the chat about <topic>", "show my archived chats"). There is no delete by voice.
- Self-test section `archive` (`Editor/MetaCodexUiTestThreadArchive.cs`) and three checks in `workspaces` (the
  worktree instruction).

## Tests

- Editor self-test sections `chat` (`Editor/MetaCodexUiTestChat.cs`; the NOW strip's words and time), `threadwindows`, `threadgroups` and `archive`;
  `context` and `workspace` check the thread state too.
- The chat's parsing was checked outside the repository with the engine's own .NET 8 and Roslyn: a real thread from
  the headset, synthetic turns, deltas, the chat's markdown and line mapping (30 checks).
- Headset: [TEST_CHECKLIST.md](TEST_CHECKLIST.md#threads), area "Threads".
