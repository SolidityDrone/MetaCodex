# MQH (Meta Quest Harness): instructions for coding agents

MQH is a trimmed hard fork of bb (see `UPSTREAM.md`, MIT, credited) that runs inside the headset's Linux
userland; the MetaCodex Unity app is its only client (no web UI). Design: `../docs/mqh.md` (what we keep,
what lives in Unity vs here) and `../docs/mqh-spatial.md` (plugin panels, gestures, appearance for VR).

## Build and run (from the repo root, WSL)

```bash
tools/mqh-build.sh            # sync mqh/ -> ~/mcxwork/mqh-build, pnpm install, turbo build (server, host-daemon, CLI, bundled plugins)
tools/mqh-build.sh build @bb/server     # rebuild one package (and its workspace deps)
tools/mqh-dev.sh start        # run server + host daemon isolated (own HOME, clean env, ports 38990/38991)
tools/mqh-dev.sh cli status   # the mqh CLI against that instance
tools/mqh-dev.sh stop
```

Typecheck and tests run in the mirror (after `tools/mqh-build.sh`):

```bash
cd ~/mcxwork/mqh-build
corepack pnpm --filter @get-bb/plugin-sdk run build:types     # declaration bundle the typecheck resolves
corepack pnpm exec turbo run typecheck --filter=@bb/server... --filter=@bb/host-daemon... --filter=@bb/cli... --filter='./plugins/*' --concurrency=2 --continue
(cd packages/plugin-sdk && npx vitest run)                     # tests of the package you touched, not the whole suite
```

Edit sources in `mqh/` (the repo); never in the mirror (the next sync overwrites it). pnpm on `/mnt/c`
is too slow, which is the only reason the mirror exists.

## Safety on a development machine

The real bb runs on this machine (data in `~/.bb`, ports 38886/38887, `BB_*` variables in agent shells,
`mqh` on PATH). MQH must never touch it: data dir `~/.mqh`, ports 38990/38991, env prefix `MQH_`, CLI
`mqh`, and every test run goes through `tools/mqh-dev.sh` (isolated HOME and environment). Never start
`apps/server` or `apps/host-daemon` directly from an agent shell.

## Changing the fork

- Keep internal names (`@bb/*` packages, `bb-plugin-*`, the `"bb"` manifest field): they are not
  user-facing and keep a re-sync with upstream possible. User-facing names are MQH: run
  `python3 -I tools/mqh-rebrand.py mqh` after any change that adds `bb ...` texts or `BB_*` names
  (idempotent; code places that name the CLI binary are in its `CODE_RENAMES` table).
- Removing a part: add it to the lists in `tools/mqh-gut.sh` (and unregister it in
  `tools/mqh-gut-patch.py` if the server lists it), so a re-sync reproduces the cut.
- No web UI: never add React, `app.tsx` or an `"app"` manifest entry. A plugin's UI is `spatial.json`
  (data the Unity client renders), its logic is server/host code.
- Re-sync with upstream: run `tools/mqh-gut.sh <fresh bb checkout>` on a branch from the import
  commit, commit, merge it into main (our adaptation commits replay on top), then run the rebrand.
- Anything the Unity client needs goes through the public API in `packages/server-contract`; add routes
  there, not private side channels.
