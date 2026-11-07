# MetaCodex Core: a bb-style control plane, our own code (spec v0, Nov 6 2026)

Decided by the owner (Nov 6, late): **build something that works exactly like bb, but do not run bb and
do not fork it.** Core is written from scratch, modelled on bb's public concepts and behaviour as its
guide describes them (`bb guide`, `bb guide threads|thread-messaging|thread-inspection|thread-lifecycle`);
no bb source is copied. It runs as a daemon inside the headset's Debian userland; Unity is its spatial
client; a `mcx` CLI mirrors the `bb` verbs so agents inside threads can orchestrate other threads.

## 1. Concepts (same meaning as in bb)

| Concept | Meaning in Core v0 |
|---|---|
| Project | a named folder in the userland (`/work/<name>`); every thread belongs to one |
| Environment | where a thread runs: the project checkout (v0) or a git worktree (v1); several threads may share one |
| Machine | the host running environments; v0 has one (`quest`, the headset). The shape allows a PC later |
| Thread | one agent conversation, the unit of work; optional parent thread (manager / child tree) |
| Provider | the agent backend of a thread: `claude-code`, `opencode`, `replay` (judge builds, tests) |
| Turn | one user input and the agent's work on it, inside a thread |
| Event | an append-only, per-thread, sequence-numbered record (the timeline) |
| Interaction | a pending request from the agent to the user: `approval` (command / file_change / tool_use / plan), `user_question`, or `<plugin>/<name>` |
| Queued message | input that could not dispatch yet, with a typed `waitingOn` reason |
| Terminal | a persistent PTY session scoped to a thread, environment or machine path (v1) |

Thread status (as bb): `pending` (scheduled), `idle`, `starting`, `active`, `stopping`, `error`.

## 2. Behaviour to match (v0 subset)

- **spawn**: project + prompt (+ title, provider, model, parent thread, permission mode
  `accept-edits | auto | full`, plan flag). Creates the thread, starts the provider, sends the prompt.
- **tell** with `--mode steer | queue | auto` (default steer): steer delivers into the running turn
  (provider permitting, else queues), queue waits until idle, auto steers a live turn or starts a new one.
  A thread waiting on an interaction cannot take a prompt: the message is queued
  (`waitingOn: interaction`) and dispatched once the interaction settles. The reply reports
  `delivery: sent | queued`.
- **interactions**: list/show; `approve`, `deny`, `grant --scope turn|session`, `answer`
  (questions: choice or text), `respond` (plugin forms). A deny carries an optional reason that is
  delivered to the agent (bb's deny has no text; ours sends it as the tool result's message, which
  the Claude Code permission hook supports, and as a steer message otherwise).
- **parent notifications** (`auto | stuck | off`): a child's finished / failed / interrupted turn and
  every blocker (approval, question) becomes a system message to the parent thread.
- **stop**: interrupt the running turn, release the runtime, keep history; idle runtimes are released
  after 30 minutes. **retry** re-submits the failed turn. **archive / unarchive / delete** as bb
  (delete removes descendants).
- **log / events**: `GET events?afterSeq=N` pages the timeline; every event has `seq`, `ts`, `kind`.
- Context variables in a thread's processes: `MCX_PROJECT_ID`, `MCX_THREAD_ID`, `MCX_ENVIRONMENT_ID`,
  `MCX_CLI`, `MCX_SERVER_URL`.

## 3. Event kinds (the timeline; maps onto BUILD_NOTES §1)

`thread.created`, `status` {from, to, reason}, `user.message` {text, mode, files}, `turn.started`
{turnId}, `assistant.delta` {text} (coalesced, not stored per token), `assistant.message` {text},
`tool.call` {callId, name, input, risk}, `tool.result` {callId, ok, summary}, `file.change` {path, verb},
`interaction.requested` {interactionId, kind, payload}, `interaction.resolved` {interactionId,
decision, reason}, `usage` {tokensIn, tokensOut, costUsd}, `turn.finished` {turnId, outcome:
completed | failed | interrupted}, `error` {code, message, retryable}, `child.notice` {childThreadId,
outcome}.

## 4. API (HTTP JSON on 127.0.0.1, bb-shaped paths under /api/v1)

Auth: `Authorization: Bearer <token>`; the token is in `/root/.metacodex/token` (only the userland and
the app can read it) and is handed to Unity by the app. Errors: `{ok:false, error:{code, message, hint}}`.

| Method + path | What |
|---|---|
| `GET /api/v1/status` | server version, machine, providers and their availability |
| `GET/POST /api/v1/projects`, `GET/PATCH/DELETE /api/v1/projects/:id` | projects |
| `GET /api/v1/threads?projectId&parentThreadId&status&archived` | list |
| `POST /api/v1/threads` | spawn |
| `GET /api/v1/threads/:id` | show (with `pendingInteractions`, `queuedWork`) |
| `PATCH /api/v1/threads/:id` | title, parent, model, parentNotifications |
| `GET /api/v1/threads/:id/events?afterSeq&limit` | timeline page |
| `POST /api/v1/threads/:id/send` | tell {text, mode} -> {delivery} |
| `POST /api/v1/threads/:id/stop` \| `/retry` \| `/archive` \| `/unarchive` | lifecycle |
| `DELETE /api/v1/threads/:id` | delete (descendants too) |
| `GET /api/v1/threads/:id/interactions` | pending + past |
| `POST /api/v1/threads/:id/interactions/:iid/resolve` | {decision: allow_once \| allow_for_session \| deny, reason?} or {answers} or {value} |
| `GET /api/v1/threads/:id/queue`, `DELETE .../queue/:mid`, `POST .../queue/:mid/send` | queued messages |
| `GET /api/v1/ws` (WebSocket) | change notifications `{type:"thread.changed", threadId, seq, status}`; clients re-read with `events?afterSeq` |

## 5. Providers (drivers)

| Provider | How | Approvals |
|---|---|---|
| `claude-code` | `claude -p --input-format stream-json --output-format stream-json --verbose` as a child process per running thread; session resume with `--resume <sessionId>` | a `PreToolUse`/`PermissionRequest` HTTP hook (or `--permission-prompt-tool` MCP tool) that blocks on Core until the user decides; the exact contract is verified on the headset before building on it |
| `opencode` | `opencode serve` on loopback; sessions via its HTTP API, events via its SSE stream | its permission routes |
| `replay` | plays a recorded event tape with real timing; used by judge builds and tests | recorded interactions wait for a real decision |

Credentials: Core never reads or stores subscription tokens; the CLIs keep their own logins.

## 6. Implementation

- Node 24 LTS (installed by `mcx-setup`), **TypeScript run directly** by Node's type stripping
  (erasable syntax only), **no npm dependencies**: `node:http`, `node:sqlite`, `node:child_process`,
  a small WebSocket server of our own. Shipped in the overlay at `/opt/metacodex/core`; started by the
  app (`mcx-core` through `LinuxHost.StartShell(command)`) and restartable from the terminal.
- One process = server + local machine host. The host interface stays separate so a second machine can
  attach later.
- Storage: SQLite (WAL) in `/root/.metacodex/core.db`: `projects`, `environments`, `threads`, `events`,
  `interactions`, `queued_messages`, `settings`.
- `mcx` CLI: `status`, `project list|create`, `thread spawn|list|show|log|tell|stop|retry|archive|
  delete|wait`, `thread interactions list|approve|deny|answer`, all with `--json`.

## 7. Milestones

1. **v0 (G2)**: store + API + WS + `mcx` CLI + `replay` provider + `claude-code` provider (stream-json,
   no approvals yet); tests run in WSL (`node --test`).
2. **v0.1**: approvals through the hook, parent notifications, queue semantics, OpenCode driver.
3. **v0.2 (G3)**: Unity client (threads panel, timeline, approval cards poked by hand), voice input via
   QVAC STT.
