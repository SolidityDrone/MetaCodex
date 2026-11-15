# Core Session Context Protocol (CSCP)

A parallel layer that knows what is going on in the workbench, so the user can **ask** about it ("is this build stuck?", "what did this thread achieve?", "do we have the rust toolchain?",
"what is this command I'm asked to accept, is it safe?") instead of only giving orders ("open settings").

## Rules

- **For the local model only.** The context never goes to the agent CLIs, to MQH or off the headset, and no
  control-port command gives it out. Only the local AI service reads it. To keep the events across a restart, the app
  saves them in `session-context.jsonl` in the shared folder; everything in the Linux can read that file.
- **Read-only.** It observes; it never runs, approves or changes anything. Answers end with what the user can do.
- **Cheap.** Event taps on what the app already does, one low-priority `ps` every 20 s while the Linux is up.
  Nothing on the render thread beyond reading fields.
- **Mid-term memory.** The session's events for about 3 hours (at most 400), kept across an app restart within that
  window; older ones go. Not long-term memory.
- **Facts first.** The local LLM is small (0.6B): what can be answered exactly (a tool's version, a process's CPU
  progress, a command's risks) is answered from the data; the LLM phrases recaps from a compact context (about 2 KB).
- **Never "safe".** For approvals the answer lists what the command does and its risks (deterministic scan), and that
  the user decides (ALLOW / DENY in the thread). Voice never approves through this path.

## What it collects

| Section | From | Example |
|---|---|---|
| focus | the panel under a hand ray (last 15 s), else the panel nearest the gaze; the open thread; the selected thread | pointing at THREAD "Fix the build" |
| threads | MQH thread list (title, provider, status, updated) and each working thread's pulse (docs/thread-view.md: what it does now and since when, quiet / may be stuck, this turn's counts, the pattern, its last thought / answer) | working 3 min, running `npm test`; re-ran it 3 times (2 failed) with edits between: fixing before it answers |
| approvals | pending interactions of the open thread (approval: command / tool / path, reason; questions) | `rm -rf build && cargo build` |
| processes | `ps` samples: notable processes (compilers, package managers, build tools, node, python, ...), elapsed, CPU seconds and when CPU last grew | cargo: no CPU for 4 min |
| tools | SYSTEM > TOOLS inventory | rustc 1.91.0, node 24.11 |
| extensions | Lua scripts and plugins (enabled, errors), MQH plugins | script double-clap: ERROR |
| system | first-use setup, storage, captures, the AI service, how many windows are open | first use done |
| events | the mid-term log: thread status changes, approvals asked / answered, tool installs, script errors, captures, processes that ran 20 s or more, merges and removed worktrees | 14:02 approval asked in "Fix the build" |
| focus (short-term) | the window the user last opened or closed, for 2 minutes: what "it" means | last acted on: the settings window (closed 5 s ago) |

Not in the context: what the user said (questions are not events), the Whisp's callouts and where it floats, the main menu's place, Ambient Focus on or off, and other UI settings.
The model answers about the work, the threads and the windows the user acts on.

## "It"

The window the user last opened or closed (by voice, a button or its ✕) is kept for two minutes. Before a phrase is
matched, "reopen it", "open it again", "bring it back", "show that", "close it", "hide that one" become that window's
command ("open settings"); "reopen the files" becomes "open the files". VOICE > LAST shows the rewrite. Only windows
voice can name count (`VoicePanels`); a CONTROL page keeps the name it was opened by (DISPLAY, not SETTINGS).

## Interpretation or command

1. The phrase templates and the decision model run as before. The registry gets `ask_about_session` with many
   question samples (EN + IT), so "is the build stuck?" is scored against "open settings" by the same model.
2. If nothing matched and the utterance is a question (question word or "?"), it goes to `ask_about_session` too.
3. `ask_about_session` builds the context for the question (sections ranked by its topic; "this" resolved from focus,
   else the open thread, else the newest pending approval) and answers: exact facts when the question is about tools
   or processes or an approval's risks; otherwise the LLM from the context. The answer is spoken (VOICE setting
   "Speak answers to questions", on) and shown on the HUD.

Thread state questions ("what is it doing?", "is it stuck?") are answered from the pulse without
the model; recaps get the pulse lines (a few hundred characters) instead of the conversation: fast, short and
accurate.

## Limits

The context does not list subagents, and it knows the pending approvals of the open thread only.

## Code

- `Runtime/AI/SessionContext.cs`: the pure parts (event log, process vitals, command risk scan, question topics,
  context text within a budget, exact answers). Tested without the app.
- `Runtime/Shell/ShellContext.cs`: the collectors (taps, `ps` sampler, focus), the snapshot, the voice command and
  the answer path (QVAC chat).
- Self-test section `context`.
