# MQH: the Meta Quest Harness

MQH is MetaCodex's agent harness: the daemon that owns threads, plugins, skills, providers, machines
and their data, running inside the headset's Linux userland. Unity is its spatial client. (Before Nov 7
this was "Core" / "the bb-like daemon"; `docs/core-v0-spec.md` is that first prototype.)

## Decision (Nov 7): a trimmed hard fork of bb, rebranded, with Unity in place of the web app

| | Fork bb | Rewrite (extend Core v0) |
|---|---|---|
| What exists on day 1 | threads, interactions, queue, parent/child, plugins + plugin SDK + marketplace, skills index, typed settings, providers as plugins (Claude Code, Codex, **ACP**, Pi), machines + permissions, environments (checkout, worktree, personal), terminals, MCP, CLI, SDK, create-plugin / create-skill skills; 2,334 test files | Core v0: threads, events, steer/queue, approvals, parent notices, replay + cloud-agent drivers, CLI (2k lines, 10 tests) |
| Size we carry | harness part ≈ 300k lines TS (server 102k, host-daemon 25k, CLI 21k, db/domain/contracts/SDK/plugin-SDK ≈ 85k, provider bridges 23k, provider plugins) | small, fully ours |
| Time to the feature list | days (trim, rebrand, package for arm64, run on the headset) | weeks (ACP alone is a protocol client; plugins + marketplace + machines more) |
| Unity coupling | the web app already talks only through the public contract (`server-contract`, `sdk`): Unity replaces it | designed for Unity from the start |
| Risks | size/RAM on the Quest (gate G2), foreign code to debug, upstream drift, contest originality wording | not enough time for the spatial UI |

**Call: fork.** 11 days remain and what judges see is the spatial UI; the harness must not eat them.
bb is MIT (Copyright 2026 Michael Yong): keep its licence and notice in `mqh/`, credit it in the README
and the submission.

- **Hard fork, frozen** at upstream `8e4208e` (2026-11-06). Re-sync by hand if ever needed.
- **Rebrand user-facing surfaces only**: CLI `mqh`, data dir `~/.mqh`, port, env prefix, product
  strings, skill texts. Internal package names (`@bb/*`) stay, which keeps the diff small and any future
  re-sync possible.
- **Drop**: `apps/app` (web UI), `desktop`, `mobile`, `web`, `connect*`, `demo-server`, `ai-gateway`,
  tunnel/frp/cloud packages, and plugins that are web-UI or account features (browser, monaco, pdf
  preview, inline-vis, push notifications, keep-awake, bb-account, bb-ai, github-star, mobile).
- **Keep**: `apps/server`, `apps/host-daemon`, `apps/cli`, the contracts, `db`, `domain`, `sdk`,
  `plugin-sdk` (host + provider-bridge + environment-provider surfaces), `plugin-registry`,
  `plugin-build`, `skill-index`, `agent-runtime`, `provider-bridge-*`, and the plugins provider-claude-code,
  provider-codex, provider-acp, provider-pi, environment-project-checkout, environment-git-worktree,
  environment-personal-workspace, ask-user-question, mcp, custom-instructions, concurrency-limit,
  prompt-library, automations, memory.
- **Add**: a spatial UI surface for plugins (below), a Quest settings namespace, a replay provider plugin
  (judge builds with no keys, ported from Core v0), later a local-model provider (QVAC).

**Contest caveat (owner's decision):** the rules ask that the project be "ORIGINAL to each Entrant".
Open-source components are normal, but a rebranded fork as the harness is a grey zone: credit bb plainly,
keep the judged experience (spatial UI, hands, the userland inside the APK, local AI) ours, and ask
Devpost support if in doubt. Core v0 stays in git history as the fallback engine.

## What lives where

| Concern | MQH (daemon in the userland) | Unity (spatial client) |
|---|---|---|
| Threads, timeline, interactions, queue | source of truth, API + change events | renders; sends tell / approve / deny / answer / stop |
| Providers, **ACP providers**, models | settings + provider plugins | settings panels (forms from the settings schema) |
| Machines | permissions, environments, host policy | panel |
| Plugins | install / enable / run (host code), marketplace | lists; renders each plugin's **spatial contributions** |
| Skills | index, per-agent install, create-skill flow | lists; starts "create skill" / "create plugin" threads |
| Appearance, gestures | stores them in a `quest.*` settings namespace (agents and the CLI can change them) | owns their meaning: applies appearance live, recognises gestures |
| Gestures | maps gesture ids to actions (bindings are data); custom gestures arrive as plugins (data: pose templates) | recognises hand poses at frame rate, emits gesture ids |
| Terminal | PTY sessions (bb terminals), scoped to thread / environment / machine | terminal panels attach to them; LinuxHost still boots the userland |
| Files, artifacts | project files under the userland | previews (later: 3D, charts, docs) |
| Local AI (QVAC) | provider / service in the userland (`qvac serve`) | voice capture to text, TTS playback |

**Plugin UI in VR.** bb plugins ship UI as React (`plugin-sdk/app`); IL2CPP cannot load code at runtime.
MQH adds a declarative surface: a plugin contributes JSON panels (list, card, form, buttons, text,
status) bound to its own RPC/data; Unity renders them with prebuilt widgets. The "create plugin" skill
teaches agents this format, so plugins written on the headset show up in VR without a rebuild.

**Skills for the Unity side.** Agents need skills that describe the spatial surface (panel schema,
gesture-binding format, appearance keys) so a skill or plugin they write is valid on the first try. They
ship with MQH, next to bb's plugin-authoring and skill-creator skills.

## Wiring

```
Unity app (C#)  --HTTP JSON + WebSocket, 127.0.0.1, token-->  MQH server  <-->  MQH host daemon  <-->  agent CLIs / ACP agents
     ^  boots userland, starts + supervises MQH (LinuxHost, proot)        token file in the shared folder
```

The same public API the bb web app used (`server-contract` routes, change notifications on the
WebSocket, re-read with the SDK's paging) is the Unity contract. Unity gets a small C# client for the
routes it needs, generated by hand from `server-contract`.

## Steps

1. Import the trimmed fork into `mqh/` at `8e4208e`; build server + host-daemon + CLI in WSL; run headless;
   smoke test with the CLI (no Unity).
2. Rebrand the user-facing surfaces; remove the dropped apps and plugins; tests green.
3. Package a linux-arm64 bundle (Node 24 + native addons: better-sqlite3, node-pty, parcel watcher)
   into the overlay at `/opt/mqh`; LinuxHost starts it. **G2 on the headset**: RAM, start time, one thread.
4. Unity client v0: threads list, timeline, approvals and questions, settings panels.
5. Spatial plugin surface, gesture bindings, Quest settings namespace, replay provider plugin.
