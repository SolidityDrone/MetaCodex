---
name: worktree-per-thread
description: Work in your own git worktree and branch when the project folder is busy with another agent, so two agents never edit the same files at once. Use it when the message says the folder is busy or shared, when the user says "in a worktree", "on its own branch", "in parallel", "do not touch the main folder", or when another thread already works in this checkout. Covers how to find a worktree that fits, how to make one, where to do the edits, and what to tell the user at the end.
---

# Worktree per thread

Another agent can work in the same project folder (the shared checkout) at the same time as you.
Then you both edit the same files, and each of you sees the other's half-done changes in builds,
tests and commits. A git worktree is a second folder of the same repository, on its own branch.
In a worktree your changes stay apart until the user merges them.

## Rules

- Do not change files in the shared checkout. Do not commit there.
- Do not switch the branch of the shared checkout (`git checkout`, `git switch`), and do not run
  `git stash`, `git reset` or `git clean` there. The other agent works in it.
- Reading files in the shared checkout is fine.
- Do all edits, builds and tests in your worktree.
- Do not merge, rebase onto the base, push or delete the worktree unless the user asks for it.
  The user merges the work when it is ready.

## Steps

1. Find the shared checkout and its branch (the base of your worktree):

   ```sh
   CHECKOUT=$(git rev-parse --show-toplevel)
   BASE=$(git -C "$CHECKOUT" branch --show-current)
   git -C "$CHECKOUT" status --short
   ```

   If `BASE` is empty (a detached HEAD), use `main`, or `master` when there is no `main`.
   The `status` output shows changes nobody committed yet. They do not go into a new worktree.
   If your task needs them, tell the user and ask before you continue.

2. List the worktrees that exist:

   ```sh
   git -C "$CHECKOUT" worktree list
   ```

3. Choose a worktree that fits, when there is one. It fits when its branch name matches this
   task (for example `agent/fix-login` for a login fix) and its folder is not the shared checkout.
   Check that nobody has work in progress there:

   ```sh
   git -C <path> status --short
   git -C <path> log --oneline -3
   ```

   If it has changes that are not yours, or you are not sure, do not use it. Make a new one.

4. If no worktree fits, make one. Use a short name for the task (lowercase, words joined by `-`).
   The folder goes next to the checkout, and the branch starts from `BASE`:

   ```sh
   NAME=fix-login
   BRANCH=agent/$NAME
   WT="$CHECKOUT-wt-$NAME"
   git -C "$CHECKOUT" show-ref --verify --quiet "refs/heads/$BRANCH" && echo "branch exists"
   git -C "$CHECKOUT" worktree add -b "$BRANCH" "$WT" "$BASE"
   ```

   If the branch exists already, choose another name, or reuse it without `-b`
   (`git -C "$CHECKOUT" worktree add "$WT" "$BRANCH"`) only when that branch is for this task.
   If `$WT` exists, add a number (`-2`, `-3`).

5. Go into the worktree and work only there:

   ```sh
   cd "$WT"
   ```

   Files that git does not track (dependencies such as `node_modules`, build output, local
   settings such as `.env`) are not in a new worktree. Install or copy what the task needs.
   Give every command a path inside `$WT`. Be careful with paths from earlier messages: they
   can point into the shared checkout.

6. Do the task. When it is done, commit on your branch so the work can be merged:

   ```sh
   git -C "$WT" add -A
   git -C "$WT" commit -m "<what changed>"
   ```

   Do not commit if the user said not to. Then say that the changes are not committed.

## The final message

Tell the user, in short lines:

- Worktree: the full path (`$WT`).
- Branch: `$BRANCH`, made from `$BASE` (or "reused").
- What you changed, and whether it is committed.
- How to merge: "MERGE in WORKSPACES when done." If WORKSPACES does not list this worktree, the
  user can ask you to merge. Then, in the shared checkout, only when it is on `$BASE`, has no
  changes, and no other agent works there: `git -C "$CHECKOUT" merge --no-ff "$BRANCH"`.
- How to remove it later: `git -C "$CHECKOUT" worktree remove "$WT"` (the branch stays).

## Later messages in the same thread

Keep working in the same worktree. Run `git -C "$WT" status --short` first to see where you are.
If the user tells you to work in the shared checkout again, do that only when the other agent is
done.
