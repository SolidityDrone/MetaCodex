# Workspaces: several threads, one project, no clashes

Owner, 2026-11-11: with more than one thread working on the same branch, how are worktrees managed and shown, and do
two agents on different tasks conflict? Wanted in the daemon (MQH) and in the UI: clear grouping and tracking.

## The facts (MQH = bb's model)

- A thread works in an **environment**: the project **checkout** (the project's own folder; several threads can share
  it, and then they edit the same files, see each other's half-done changes in builds and tests, and commit each
  other's work) or a **worktree** (`git worktree`: a folder of its own, on its own branch from a base, created by the
  `git-worktree` provider; git never checks one branch out in two worktrees).
- MQH already refused a branch switch in a checkout another live thread occupies, and lists environments
  (`GET /api/v1/environments`: path, branch, base / merge base, worktree or not, lifecycle). It did not stop two threads
  from *working* in one folder at the same time. Now it does.

## The daemon: MQH's workspace guard (`mqh/plugins/concurrency-limit`)

- In the `message.dispatch` hook, before a turn starts: when another thread is **working** (active / starting) in the
  same folder (same environment, or the same canonical path on the same host; a new thread's project checkout is
  known from its start intent), the turn **waits** in the thread's queue. Its row says why: `“Fix the build” is
  working in this checkout: this turn starts when it is done (Send now shares the checkout)`.
- When the holder goes idle, fails or is archived, the queue is checked again and the turn starts.
- A fresh worktree is the thread's own folder and never waits; a fork that reuses a worktree another thread works in
  does. A message that joins a running turn (steer) is never held.
- The user's **Send now** on the queued row overrides it (core skips hooks for it by design).
- `mqh concurrency-limit workspace on|off` (on by default). Tests: `server.test.ts`, "workspace guard" (6 cases).

## The app

- **THREADS**: a project with a worktree shows its folders as rows (the checkout first, then the worktrees newest
  first, a worktree left behind too), each thread under the folder it works in. Badges: `1 WAITING`, `2 WORKING ⚠`
  (two agents in one folder: an override or the guard turned off); a held thread shows ⏸ WAITING. A folder row opens
  WORKSPACES on it. ⎇ WORKSPACES opens the window.
- **WORKSPACES** (`Shell/ShellWorkspaces.cs`, voice "open the worktrees"): every folder of every project, who works
  there, who waits; its git state read in the userland (branch, ahead / behind its base, changes, the last commit).
  - **MERGE INTO <base>…** (worktrees): in the project checkout, only when it is on the base branch, clean and nobody
    works there; first `git merge-tree --write-tree` (a dry run: nothing is touched). Clean: MERGE (`--no-ff`; on
    any failure `merge --abort`). Conflicts: the files are listed and **ASK ITS AGENT** sends the worktree's thread
    "rebase on <base> and resolve the conflicts in …".
  - **NEW THREAD HERE…**: a thread in that workspace (MQH holds its turn while another one works there). START opens
    the draft; its first message makes the thread in that workspace.
  - **REMOVE…** (worktrees, nobody working): archives its threads, then MQH deletes the folder; the branch stays in git.
  - OPEN THREAD, FILES, TERMINAL HERE.
- **NEW THREAD**: when a thread works in the project's checkout, WORKTREE is chosen for the new one (the summary says
  who works there and why; the saved choice is not changed). CHECKOUT stays possible: its turn then waits.
- **THREAD**: the workspace chip names the branch (`⎇ feat/x`, `▣ checkout · main`) and gets ⚠ when another thread
  works in the same folder. A held turn: the status line says `⏸ Not started: “…” is working in this checkout` with
  **▶ SEND NOW** beside it (asks first: both agents would edit the same files).
- **Session context**: `workspace: in the project checkout (main), shared with “Fix the build” working there now` /
  `in its worktree feat/x (from main): its own folder and branch`; a held thread's state is `not started: …`.

## Tests

- Pure (`C:\Users\pnncl\mcxcheck-chat\test\build.sh`): grouping, clashes, the busy checkout, descriptions, git status /
  rev-list / merge-tree parsing (checked against real git 2.43: an unknown branch exits 1 as well), the held state.
- MQH: `cd ~/mcxwork/mqh-build/plugins/concurrency-limit && corepack pnpm exec vitest run` (35 ok).
- Editor self-test section `workspaces` (`Editor/MetaCodexUiTestWorkspaces.cs`); `threadgroups` updated.
- Headset: docs/TEST_CHECKLIST.md section 27. MQH reaches the headset with the next `tools/mqh-build.sh` +
  `tools/mqh-bundle.sh` and APK.
