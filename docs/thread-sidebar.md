# The thread sidebar (owner 2026-11-11)

The owner: "we dont get to see the project folder where we are in but we actually see the root ... if the project folder
we are in isnt a git it shouldn't display anything, but rather have a button to init git ... File difference is weird, it
doesnt seem to show all the files that are worked, and i expand em i see a white text no wrap ... I would rather see a
sort of visual studio code style ... a button expand on the right of the thread ... like a sidebar that pop up".

It replaces the separate WORKSPACE module (the old `ShellWorkspace.cs`, now `Runtime/Shell/ShellThreadSidebar.cs`).

## Attached to the thread

- THREAD has a handle on its right edge (`UiEdgeHandle`: `›`, PROJECT down its length, the number of files the thread
  changed). A tap opens the sidebar; `‹` (on the handle or in the sidebar's header) folds it.
- The sidebar (`thread-side`, 0.62 × 0.66 m, THREAD is 0.73 m wide now) sits at the thread's right edge, tops level. Bent
  panels: on the thread's own cylinder (the pair reads as one curved screen); flat: turned 14° towards you round the
  thread's right edge. It moves, turns and bends with the thread every frame; pinch its header to carry both. In FLOAT the
  pair takes one slot on the ring. It closes with the thread; saved layouts leave it out (it comes back with its thread).
- Each thread remembers open / folded and its tab (`Settings.threadSidebar`, the newest 80 threads). A thread seen for
  the first time starts folded, on CHANGES. Opening another thread drops everything shown before (a generation counter
  drops late git answers of the old thread or folder).

## The tabs (all about the thread's project folder: `environmentPath`, never `/`)

- **CHANGES**: every file the thread changed. In a repo: one shell run (`ThreadChanges.Script`: `git status --porcelain`
  with untracked files, `git diff --numstat` and `--name-status` against the base) merged with the timeline's
  file-change rows and edit / write tool calls. The base is HEAD in a shared checkout, the merge base with the base branch
  in a worktree (its own commits count). Untracked files get their line counts read by the app. The thread's files come
  first (in a worktree every change is the thread's), then "ALSO CHANGED IN THIS FOLDER". Outside git: the timeline's
  files. Rows: name, folder, `+N −M`, VS Code's letter (M amber, A / U green, D red, ✓ committed). The summary:
  `2 files · +17 −3 · against HEAD · 4 other changes in this folder`. Live: an edit in the timeline refreshes git 0.8 s
  later; otherwise every 4 s while the agent works, 20 s when idle (only while the sidebar is open).
- **The diff** (a file tapped; `‹ ALL CHANGES`, `▲ PREV` / `▼ NEXT` file, WRAP, `@ ASK`, OPEN FILE): `UiSourceView`, VS Code
  dark: the whole file as context (`-U100000`) with unchanged runs folded (`⋯ 42 unchanged lines (lines 10–51: tap to
  show)`, 3 lines kept round each change), old and new line numbers, a `+` / `−` gutter, green / red line backgrounds,
  syntax colours (`CodeText`: C#, JS / TS, Python, shell, JSON, YAML / TOML, C / C++ / shaders, Java / Kotlin, Go, Rust,
  Lua, SQL, CSS, HTML / XML, Markdown, Makefiles), an overview ruler of the changes on the right edge. Long lines: drag
  sideways (a thumb shows where), or WRAP. Big files (> 1.5 MB or > 20,000 changed lines) diff with 3 lines of context
  only and say so; binaries are named; an untracked file shows all its lines added; a file git sees no change in shows
  the agent's last edit from the timeline. The open diff reloads in place when the agent edits that file again.
- **FILES**: the project folder with a breadcrumb (`repo › src`, a tap goes back up; never above the project folder;
  `⌂` back to it), folders first, `.git` hidden, changed files coloured with their letter, folders holding changes
  marked `●`. A file opens read-only in the code view (line numbers, syntax colours, 2 MB at most and it says so;
  binaries named): `‹ BACK`, WRAP, `@ ASK`, EDIT (nano in a terminal), DIFF (CHANGES on it).
- **GIT**: only in a repo: the branch, ahead / behind, upstream, clean or N changed (and the repo's own folder when the
  project is a subfolder of it), the status (a tap opens that file's diff), the recent commits (hash, subject, refs,
  when); FETCH, PULL, COMMIT…, PUSH, TERMINAL. Not a repo: `NOT A GIT REPOSITORY`, the path, one **INIT GIT**
  (`git -c init.defaultBranch=main init` in the project folder) and nothing else. No folder yet / git missing / the folder
  gone: that sentence and TRY AGAIN.
- **STATUS**: AGENT (provider, model, effort), ACCESS, CONTEXT (% and tokens, amber ≥ 70 %, red ≥ 90 %), STATE (working
  for how long, what it does, quiet), STUCK? when the pulse says so, RUNNING (the command and its time), THIS TURN
  (thoughts, commands, failures, files, lines), QUEUE, APPROVALS waiting, USAGE (plan and windows), FOLDER (path,
  checkout / worktree, branch), MQH (online, the thread's status, its address).
- **VIEWS**: the thread's agent views (OPEN ALL, CLOSE ALL, DEMO), as before.

## Legibility

Code rows are 22.5 mm tall with DejaVu Sans Mono (the terminal's font, no new font) at a line size of 16 mm: an em of
11.5 mm on the panel, 13.8 mm at UI scale 1.2: 0.99° at 0.8 m (Meta's guidance: about 1° or more). List names use the
body size (the same 1°), details and right-hand numbers 13.5 mm (0.84°). The code area is VS Code's `#1E1E1E` with its
token colours; the frame, tabs and buttons are MetaCodex's console look. A plain flat shape kind (`UiShapeKind.Flat`,
SciFiUI kind 9) draws the code area and line backgrounds without bevel or glow.

## Input

Ray or poke taps, pinch from afar (hand rays), drag to scroll (up / down, and sideways in a diff or file), a tap on a
fold opens it. A keyboard while the sidebar was the last panel touched (`TerminalInput.Intercept`): ↑ ↓ Page Up / Down
Home End move the cursor in the list or the code, ← → scroll sideways, Enter opens a file or a fold, Esc / Backspace go
back (diff → list, file → folder, folder → parent), Tab the next tab. Touch another panel and the keys go back to the
terminal.

## Commands (AGENTS.md)

- `thread_sidebar` (`action`: open | close, `tab`: changes | files | git | status | views): "open the sidebar on the git
  tab", "fold the sidebar away", "apri la scheda file del thread", "chiudi la barra laterale". Enabled while a thread is open.
- `init_git`: "make this folder a git repository", "inizializza git in questa cartella". Enabled only while the open
  thread's folder is known to be in no repository.
- Removed: the setting `workspace_module` (the module is gone; the sidebar is opened per thread).

## Tests

- Pure (no Unity): `tests/sidebar/run-sidebar-tests.sh`: the tokenizer, diff rows and folds, the timeline's files, and
  real git folders read through the same script and the same wsl.exe path as the Editor (a repo with an edit, a deletion,
  an untracked file and a long line; a project in a repo's subfolder; a worktree with its own commit; no repo; a missing
  folder).
- Editor self-test section `sidebar` (`Editor/MetaCodexUiTestSidebar.cs`): real folders made with git (a repo with three
  commits and C#, markdown, deleted, untracked Python, 3 MB JSON and binary changes; a folder with no repo), the handle,
  the attachment (flat and bent), every tab, the diff (folds, sideways drag, wrap, keys), big and binary files, a live
  edit, the breadcrumb, INIT GIT, another thread (nothing stale), each thread's memory, a pinch with a hand ray, the
  commands; pictures `Bench/selftest/ui-sidebar-*.png`. The section `workspace` checks the sidebar in FLOAT.
- Headset: docs/TEST_CHECKLIST.md section 33.
