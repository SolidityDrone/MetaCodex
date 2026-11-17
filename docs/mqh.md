# MQH: the Meta Quest Harness

MQH is MetaCodex's agent harness: the daemon that owns threads, plugins, skills, providers, machines
and their data, running inside the headset's Linux userland. The app is its spatial client. The overlay still carries the first
prototype of this control plane, Core v0 (`mcx-core`, `mcx`, `/opt/metacodex/core`); MQH replaced it, and the app does
not start it.

## Origin: a trimmed hard fork

MQH is a hard fork of bb, an open-source agent IDE backend (MIT, Copyright 2026 Michael Yong), frozen at upstream
`8e4208e`. Its licence and notice stay in `mqh/` and in the root `LICENSE`. The fork gives threads, interactions, the
queue, parent and child threads, plugins and their SDK, skills, typed settings, providers as plugins (with ACP),
machines, permissions and environments; the app replaces the web app as its client.

- **User-facing names changed only**: the CLI `mqh`, the data folder `~/.mqh`, the ports 38990 / 38991, the `MQH_*`
  environment, product strings and skill texts. Internal package names (`@bb/*`) stay.
- **Dropped**: the web app, desktop, mobile, connect, the demo server, the AI gateway, tunnels and cloud packages,
  and the plugins of web-UI or account features. No remote access, no telemetry, no self-update.
- **Kept**: `apps/server`, `apps/host-daemon`, `apps/cli`, the contracts, `db`, `domain`, `sdk`, `plugin-sdk`,
  `plugin-registry`, `plugin-build`, `skill-index`, `agent-runtime`, `provider-bridge-*`, and the plugins below.

## What lives where

| Concern | MQH (daemon in the userland) | The app (spatial client) |
|---|---|---|
| Threads, timeline, interactions, queue | source of truth, API + change events | renders; sends tell / approve / deny / answer / stop |
| Providers, **ACP providers**, models | settings + provider plugins | settings panels (forms from the settings schema) |
| Machines | permissions, environments, host policy | panel |
| Plugins | install / enable / run (host code) | lists them (CONTROL > MQH > PLUGINS), enables and disables them |
| Skills | index, per-agent install, create-skill flow | lists; starts "create skill" / "create plugin" threads |
| Appearance, gestures | stores them in a `quest.*` settings namespace (agents and the CLI can change them) | owns their meaning: applies appearance live, recognises gestures |
| Gestures | maps gesture ids to actions (bindings are data); custom gestures arrive as plugins (data: pose templates) | recognises hand poses at frame rate, emits gesture ids |
| Terminal | PTY sessions (bb terminals), scoped to thread / environment / machine | terminal panels attach to them; LinuxHost still boots the userland |
| Files, artifacts | project files under the userland | FILES, the side bar, agent views (charts, tables, diagrams, a 3D stage) |
| Local AI | none (an agent reaches the local coder through its own API, docs/local-coder.md) | voice capture, playback; owns the frame budget. QVAC itself runs natively in an Android service process (`docs/local-ai.md`) |

**Plugin UI in VR.** Upstream plugins ship their UI as web components, and the app's compiled C# cannot load code at
run time. MQH serves a declarative format for plugin panels (`spatial.json`, [mqh-spatial.md](mqh-spatial.md)), but the
app does not render it. Windows and menu tiles made while the app runs come from app plugins written in Lua
(`ui.panel`, `menu.add`; the skill `quest-plugins`).

**Skills for the app's side.** Agents need skills that describe the spatial surface (panel schema,
gesture-binding format, appearance keys) so a skill or plugin they write is valid on the first try. They
ship with MQH in the `metacodex-guide` plugin.

## Plugins: only what MetaCodex uses

Each plugin was checked against three questions: does the server need it to start, do the three agents (Claude Code, Codex, OpenCode) need it, does the app
call it (`MqhClient` calls only `/api/v1/plugins` and enable / disable, never a plugin route).

| Plugin | | Why |
|---|---|---|
| provider-codex, provider-acp | KEEP | the three agents: Codex through its plugin; OpenCode and Claude Code through ACP (below) |
| provider-claude-code | KEEP (source), not in the APK | MQH's own Claude plugin bundles Anthropic's proprietary Agent SDK: `tools/mqh-bundle.sh` removes it (LICENSES G8); it serves a developer's MQH on a PC only |
| environment-project-checkout, environment-git-worktree | KEEP | NEW THREAD's checkout and worktree modes |
| environment-personal-workspace | KEEP | the server's built-in personal project (`kind: personal`) makes its threads through it |
| git | KEEP | the server's environment routes and `mqh thread show` read status and diffs through it |
| concurrency-limit | KEEP | MetaCodex's workspace guard (one working thread per folder, docs/workspaces.md) |
| provider-retry | KEEP | retries turns after overload, server errors and limit resets; its retries are queued rows the app shows |
| ask-user-question | KEEP | the question tool for Codex and OpenCode (Claude Code has its own); off by default |
| custom-instructions | KEEP | the user's instructions for every agent (`mqh instructions`) |
| automations, workflows | KEEP | scheduled and multi-step agent work through `mqh`; no bb product in them (workflows off by default) |
| metacodex-guide | NEW | the agents' introduction (MetaCodex, MQH, `mqh`) and MetaCodex's skills (below) |
| bb-guide | REMOVE | introduced "bb, an agentic IDE"; its bb CLI, bb plugin authoring and marketplace skills do not apply here |
| provider-pi | REMOVE | Pi is not a MetaCodex agent (it was already left out of the APK) |
| provider-usage | REMOVE | a settings page and sidebar card of bb's web UI; the app reads limits from `/api/v1/system/usage-limits` (core) |
| secrets | REMOVE | its masked form has no renderer in the app (it answers approvals only); agents get secrets from `mcx-secret` and the machine environment |
| mcp | REMOVE | lets outside MCP clients drive bb through a bb account / connect URL; the app has its own control port |
| storage-retention | REMOVE | experimental, off by default; SYSTEM > STORAGE is the app's own; it carried an icon of unknown licence (G10) |
| github-pr-status | REMOVE (source kept) | pull request badges for bb's web UI, polling GitHub; unregistered and not bundled, the folder stays because `mqh thread show` compiles against its contract |
| github, docs, memory, tasks | REMOVE | bb's optional catalog plugins (issue browser, Markdown editor, memory, task boards) for its web UI; never installed by default and the app cannot install them |

`metacodex-guide` gives every MQH thread `metacodex-context`, `quest-gesture-plugins`, `quest-plugins`,
`quest-scripting`, `quest-views`, `skill-creator` and `worktree-per-thread` (Codex too, which does not read
`~/.claude/skills`); the overlay also lays all but `skill-creator` in `/root/.claude/skills` for agents started in a terminal (Claude Code skips the duplicate copy). `bb-cli`,
`bb-plugin-authoring` and `submit-a-plugin` are gone. Re-syncs reproduce the cut: the lists in `tools/mqh-gut.sh`.

## Claude Code through ACP

Claude Code threads run on `provider-acp`, the way OpenCode does, with no change to MQH's code:

- **The adapter**: `@agentclientprotocol/claude-agent-acp` (Apache-2.0, bin `claude-agent-acp`), an ACP agent built on
  Anthropic's Agent SDK. The user installs it on the headset from the official npm registry, in a terminal they watch:
  AGENTS > GET CLAUDE CODE installs it with the CLI, ENABLE THREADS installs it alone (`ShellAgentGet.cs`, version
  pinned there). npm brings the SDK and its linux-arm64 Claude Code binary with it; MetaCodex ships none of it
  ([LICENSES.md](LICENSES.md#third-party-coding-agents-not-included)). Node >= 22 (the image has Node 24).
- **The login**: the adapter runs on the Claude Code login the user made with `claude auth login` (the CLI's own files,
  `~/.claude`), or `ANTHROPIC_API_KEY` from MQH's machine environment (AGENTS > API KEY). MetaCodex never reads them.
  Not signed in: MQH's health says READY anyway (it only checks the bin), and the first turn fails with the adapter's
  sign-in message; SIGN IN in AGENTS runs `claude auth login`.
- **The registration**: provider-acp's own `customAgents` setting (the mechanism `mqh acp add` writes to). When the
  app's probe finds `claude-agent-acp`, `EnsureClaudeThreads` reads `GET /api/v1/plugins/provider-acp/settings` and,
  only when the entry is missing or different, writes it with `PUT` (`{"values": {"customAgents": "<JSON array>"}}`),
  keeping the user's other entries:

  ```json
  { "id": "claude-code", "displayName": "Claude Code", "command": "claude-agent-acp", "args": [], "env": {},
    "nativeSkillRoots": { "user":    [{ "path": ".claude/skills", "skipIfManifest": ".claude-plugin/plugin.json" }],
                          "project": [{ "path": ".claude/skills", "ancestors": true, "skipIfManifest": ".claude-plugin/plugin.json" }] } }
  ```

  The plugin registers it at once as provider `acp-claude-code` (custom agents are `acp-<id>`, always listed, no
  fork). The command is the global bin from the PATH; the registry's way (`mqh acp add claude-acp`) would run
  `npx -y @agentclientprotocol/claude-agent-acp@<version>` and fetch the package again at every start. Its health:
  `not_installed` without the bin, else `ready` with the adapter's `--version`. The native skill roots tell MQH that
  Claude Code reads `~/.claude/skills` itself (the overlay's copies), so MQH does not hand it a second copy.
- **What threads get**: the generic ACP dialect: text, thinking, tool calls, plans, approvals (EDITS asks through MQH's
  interactions; FULL answers the adapter's permission requests itself), stop (`session/cancel`), the adapter's model
  list. Not there: the local coder (OpenCode only, [local-coder.md](local-coder.md)), fork, MQH-side usage limits.

## Wiring

```
App (C#)  --HTTP JSON, 127.0.0.1, token-->  MQH server  <-->  MQH host daemon  <-->  agent CLIs / ACP agents
     ^  boots userland, starts + supervises MQH (LinuxHost, proot)        token file in the shared folder
```

The public API of the upstream web app (`server-contract` routes) is the app's contract. The app has a small C#
client for the routes it needs, written by hand from `server-contract`, and polls them: the open thread every 0.8 s
while the agent works (2.5 s idle), thread metadata every 2 s or 3 s, other threads every 5 s or 15 s. It does not
use the server's WebSocket.
