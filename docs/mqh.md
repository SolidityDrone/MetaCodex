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
| Local AI (QVAC) | client of the local AI API (plugin `qvac`: clean-up before providers, tools for agents) | voice capture, playback; owns the frame budget. QVAC itself runs natively in an Android service process (`docs/qvac.md`) |

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

1. **Done (Nov 7).** Import the trimmed fork into `mqh/` at `8e4208e`; build server + host-daemon + CLI +
   bundled plugins in WSL; run headless isolated; smoke test with the CLI (no Unity).
2. **Done (Nov 7).** Rebrand (`mqh`, `MQH_*`, `~/.mqh`, 38990/38991); no React or web UI anywhere (plugin
   UI = `spatial.json` + `GET /api/v1/plugins/:id/spatial`, `mqh plugin new` scaffolds it); no remote
   access, telemetry, self-update or background marketplace calls; typecheck 61/61; tests of the changed
   packages green. Details and remaining gaps: `mqh/README.md`.
3. **Done (Nov 10).** `tools/mqh-bundle.sh` packs the server, host daemon and CLI bundles (esbuild, no
   node_modules) and the bundled plugins (without provider-claude-code: its bundle contains Anthropic's Agent
   SDK, never shipped) with the packages the bundles keep outside (`userland/mqh/runtime`: better-sqlite3
   from its Node 24 linux-arm64 release, sha256-pinned; node-pty's own linux-arm64 prebuild;
   @parcel/watcher-linux-arm64-glibc; the pino logger) into `StreamingAssets/linux/mqh.tar.gz` (12.6 MB, 71 MB
   unpacked; generated, gitignored). LinuxHost lays it at `/opt/mqh` (replaced whole when its sha256 changes)
   and links `/usr/local/bin/mqh`. `MqhHost` starts `/opt/mqh/bin/mqh-run` (server, /health, the daemon's
   one-time enrollment, the daemon; foreground) through the app's AI service process, so it runs on cores 0-2,
   away from the frame loop, watches /health and restarts it; an MQH still running from before is taken over.
   The API token (the vault's `mqh.api-token`) is enforced: `apps/server/src/api-token.ts` guards `/api/v1/*`,
   `/ws*` and the enrollment (plugin HTTP routes, e.g. the agents' MCP endpoint, keep their own credentials);
   the CLI and the SDK's sockets send it to loopback only (`MQH_API_TOKEN`, else `~/.mqh/api-token`). On the
   Quest 3S: server up in about 6 s, the daemon enrolled and connected, 401 without the token.
4. Unity client v0: threads list, timeline, approvals and questions, settings panels.
5. Spatial plugin surface, gesture bindings, Quest settings namespace, replay provider plugin.

## Later: multimodal intent, with QVAC on the device (owner's direction, Nov 7; nothing to build yet)

Interaction will mix **gestures, voice and pointing at what you see**: the user points at a window, or a
part of it, and says "this is crooked, see?". The pipeline has to understand the request, capture what is
pointed at, and hand it to an agent. bb has nothing like this; MQH must grow it, and QVAC (local models in
the userland) is how:

| Step | Where | With |
|---|---|---|
| hear the request | Unity mic -> MQH | QVAC speech-to-text |
| know what is pointed at | Unity: hand ray / fingertip hit on a panel, region, timestamp | Unity (hand tracking) |
| capture it | Unity renders the pointed panel or region to an image; later passthrough camera frames | Unity |
| understand it | MQH: request + image + panel metadata (which thread, file, line) | QVAC vision / OCR / small LLM |
| clean it | MQH: anonymise and strip secrets before anything leaves the headset | QVAC local LLM + rules |
| act | MQH: a message (with image) to the right thread, through the provider or ACP agent | providers |
| answer | spoken back | QVAC text-to-speech |

Design consequences now: Unity must be able to name what is under a ray (panel id, thread id, region),
MQH must accept multimodal messages (text + image + pointer context), and a "local pre-send" stage must
exist between MQH and every provider (where redaction runs). Before any of this: QVAC proven on the
headset (models chosen, timings, RAM) at gate G1.
