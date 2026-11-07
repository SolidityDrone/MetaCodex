# mqh: Meta Quest Harness

The agent harness of MetaCodex: a trimmed hard fork of [bb](https://github.com/get-bb/bb) (MIT,
Copyright 2026 Michael Yong) at upstream `8e4208e` (see [UPSTREAM.md](UPSTREAM.md)). It runs in the
headset's Linux userland; the MetaCodex Unity app is its only client and renders its data as spatial
panels. Design: [../docs/mqh.md](../docs/mqh.md) and [../docs/mqh-spatial.md](../docs/mqh-spatial.md).

## What it is now (2026-11-07)

| Part | State |
|---|---|
| Server (`apps/server`), host daemon (`apps/host-daemon`), CLI `mqh` (`apps/cli`) | build, typecheck, run isolated in WSL (`tools/mqh-dev.sh`) |
| Bundled plugins (20: providers Claude Code / Codex / Pi / ACP, environments, git, GitHub PR status, automations, workflows, secrets, MCP, concurrency limit, ...) + 4 official (docs, github, memory, tasks) | build; all bundled ones load |
| Plugin UI | **data only**: `"spatial": "./spatial.json"` in the manifest, served at `GET /api/v1/plugins/:id/spatial`; `mqh plugin new` scaffolds server + spatial panel + skill. No React, no web app, no app bundles |
| Remote access, cloud account, telemetry, self-update | removed or hard-off (see below) |

## Changes from upstream

- **Removed** (by `tools/mqh-gut.sh`, reproducible): web/desktop/mobile/cloud apps; the web UI stack
  (`shared-ui`, `plugin-registry`, `client-core`, `query-tags`, `desktop-contract`); remote access
  (`connect-client`, `frp-client`, `tunnel-client`, `tunnel-contract`); UI-only or cloud plugins; every
  plugin's `app.tsx`/React code (`tools/mqh-strip-ui.py`); upstream CI, docs site, QA suites.
- **Plugin SDK**: no `./app` runtime or React types; server, host, provider-bridge and testing surfaces
  unchanged.
- **Plugin build**: server and host bundles only; the toolchain the server fetches is esbuild alone.
- **Server**: no web UI hosting, no plugin app-bundle routes; `spatial` state + route; no tunnel ingress
  (bb connect); no PostHog telemetry or feature flags; no background calls to bb's public marketplace
  (opt in with `MQH_MARKETPLACE_REFRESH=1`).
- **Host daemon**: no bb connect tunnel or frpc (stand-ins answer "idle, no shares"); no "open in
  editor" targets; protocol self-update hard-off.
- **Names** (`tools/mqh-rebrand.py`): CLI `mqh`, env `MQH_*`, data `~/.mqh`, ports 38990/38991, `mqh.db`.
  Internal package names (`@bb/*`, `bb-plugin-*`, the manifest field `"bb"`) stay.

## Build and run (from the repo root, WSL)

```bash
tools/mqh-build.sh                  # sync to ~/mcxwork/mqh-build, pnpm install, build server, daemon, CLI, bundled plugins
tools/mqh-dev.sh start              # isolated: own HOME, clean env, enrolls the local daemon, ports 38990/38991
tools/mqh-dev.sh cli status         # the mqh CLI against that instance
tools/mqh-dev.sh stop
```

## Known gaps (next)

- On Android every app can reach `127.0.0.1`: the API needs a token before it runs on the headset.
- Desktop-only leftovers still compiled in: desktop browser leases, web CSS themes, server-move, the
  `bb-app` launcher (it can npm-update itself: replace it with an MQH launcher before packaging).
- `mqh plugin new` pins `@get-bb/plugin-sdk` from npm (not published at this version): the SDK should
  come from the harness itself.
- Upstream test suites of the server, daemon and CLI are not all run yet; the packages changed here
  (SDK, plugin build, templates, CLI scaffold, daemon local API) pass.
