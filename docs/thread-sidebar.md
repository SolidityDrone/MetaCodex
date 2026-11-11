# The thread sidebar (owner 2026-11-11): spec for the next piece

Not built yet. Replaces the separate WORKSPACE module (`Runtime/Shell/ShellWorkspace.cs`: tabs CHANGES / GIT / FILES /
VIEWS at line 66; `WsSetThread`, `WsTrackRows`, `WsRenderChanges`, `WsRefreshGit`, `WsRefreshTree`) with a sidebar
attached to the THREAD panel. The owner's words: "we dont get to see the project folder where we are in but we actually
see the root ... if the project folder we are in isnt a git it shouldn't display anything, but rather have a button to
init git ... File difference is weird, it doesnt seem to show all the files that are worked, and i expand em i see a
white text no wrap ... I would rather see a sort of visual studio code style ... a button expand on the right of the
thread ... like a sidebar that pop up".

- **Attach**: an expand button on THREAD's right edge; the sidebar is placed, moved, bent and closed with the thread
  panel (a child of it, its right edge); remembered open / closed and its tab per thread.
- **FILES**: rooted at the thread's project folder (`_threadInfo["environmentPath"]`, never `/`), a breadcrumb; a file
  opens read-only in a code view: line numbers, monospace (the terminal's font), syntax colours, virtualized rows.
- **CHANGES**: every file the thread changed. In git: `git status --porcelain` + `git diff --numstat` against HEAD
  (or the thread's start commit if MQH gives it), untracked included; outside git: the files of the timeline's
  file-change / tool rows (`WsTrackRows` already reads them). The diff, VS Code-like: old/new line numbers, +/-
  gutter, green/red line backgrounds, syntax colours, unchanged runs collapsed, wrap toggle or horizontal scroll, a
  header with +N / -M; binaries and huge files shown as such; only visible lines rendered.
- **GIT**: only when the project folder is a repo (`git rev-parse --show-toplevel`): branch, status, recent commits;
  otherwise an empty state with **INIT GIT** (`git init` in the project folder) and nothing else.
- **STATUS**: model, context, running tools, approvals, MQH state (what the header chips say now, in full).
- **VIEWS**: keep (agent views of the thread).
- **Commands** (AGENTS.md): open / close the sidebar, each tab, INIT GIT: EN + IT samples, lint clean; the decider
  section and the voicebench (`tools/qvac-dev.sh pc start`).
- **Tests**: a UI section `sidebar` rendering it with a real repo and real diffs (pictures to look at), the empty and
  error states, live refresh while files change, scrolling, pinch and keyboard selection.
