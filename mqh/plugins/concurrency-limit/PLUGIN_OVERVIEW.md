Keep bb from starting more agent threads than your machines can handle. When a limit is reached, new turns wait in line and start as soon as a running thread goes idle.

## What you get

- An automatic limit for each host: one running thread per available processor.
- A per-host override. Set a fixed number, or set it back to `auto`.
- An overall limit across all hosts, or `unlimited`.
- A Settings section that lists every host with its capacity, its automatic limit, and its effective limit.

## How it works

Before bb sends a message to an idle thread, the plugin counts the threads that are running. If the count meets the limit for that host, or the overall limit, the message waits. The thread shows why it waits. A message to a thread that is already running is not held. When a thread goes idle, fails, or is archived, the plugin rechecks the line.

## One working thread per workspace folder (MQH)

Two agents in one folder (the project checkout, or a worktree a fork reuses) edit the same files and see each other's
half-done changes. Before a turn starts, the plugin checks whether another thread is working in the same folder (the
same environment, or the same path on the same host; a new thread's project checkout counts too). If so the turn
waits, and the queued row names the thread it waits for. A fresh worktree is the thread's own folder and never waits;
a message that joins a running turn is never held; Send now on the queued row overrides the wait. Turn it off with
`mqh concurrency-limit workspace off`.

## For agents and scripts

Use the `bb concurrency-limit` command:

- `bb concurrency-limit status` shows the effective limits.
- `bb concurrency-limit global unlimited|<limit>` sets the overall limit.
- `bb concurrency-limit host <host-id> auto|<limit>` sets one host.
- `mqh concurrency-limit workspace on|off` turns the one-thread-per-folder rule on or off (on by default).

Add `--json` for machine-readable output. Limits go from 0 to 10000. A limit of 0 holds every new turn in that scope.
