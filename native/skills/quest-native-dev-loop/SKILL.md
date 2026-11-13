---
name: quest-native-dev-loop
description: The fast loop for native Quest apps in MetaCodex - module apps run in the Runner app (mcx-app new --module, mcx-app runner, mcx-app dev) so an edit is visible in the headset about 2 seconds later with no APK build and no install, and the same module ships as its own APK with mcx-app run. Use when the user wants to iterate quickly on how a native app looks or moves, asks for "live" or "hot reload" changes, or when you decide whether a module app or a standalone app fits. Parked feature - works, prefer standalone apps for anything using controllers or many Quest features.
---

# The Runner: edit -> running in about 2 s (module apps)

**Status: parked, working** (measured on the Quest 3S: compile 1.15 s + link 0.1 s, delivered and loaded 2.2 s after
the command; reload 2.4 ms with the OpenXR session kept). Standalone apps on `mcx::App` stay the default
(quest-native-apps); use this when seeing many small visual changes quickly matters more than features.

## What a module app is

- `src/app.cpp` implements the module entry points of `/opt/metacodex/quest-native/runner/mcx_module.h`:
  `mcx_app_init(const McxHost*)` (GL context current; 0 = ok), `mcx_app_frame(const McxFrame*, McxFrameOut*)`,
  `mcx_app_draw(const McxEye*)` (required: one eye, framebuffer bound, not cleared), `mcx_app_event`,
  `mcx_app_shutdown()` (free GL objects, stop threads), and `MCX_MODULE_ABI_EXPORT` once.
- The host (`libmcxhost.so`) owns the activity, EGL, the OpenXR session and the frame loop. It hands the module
  `host->instance`, `host->session`, `host->localSpace`, `host->stageSpace`, `host->getProcAddr`,
  `host->hasExtension(name)`, `host->log(...)`, `host->assets`, `host->dataPath` (survives reloads).
- `McxFrameOut`: up to 4 `underlays` (e.g. an `XrCompositionLayerPassthroughFB`) and `projectionFlags`.
- Quest features: `mcx::Passthrough`, `mcx::Depth`, `mcx::Hands`, `mcx::Spatial` work in a module with
  `Init(host->instance, host->session)` (and `Spatial::Init(instance, session, ext)` needs an `mcx::Extensions` you
  fill from `host->hasExtension`). **Controllers do not** (the host owns the action sets), and `mcx::App` is not for
  modules. A reload restarts the module: keep state in `host->dataPath` if it must survive.
- `build.conf` says `TYPE=module`; the manifest's `android.app.lib_name` is `mcxhost`.

## The loop

1. `mcx-app new /root/metacodex-projects/native/<name> --module --label "<Label>"` (Hello Module: three cubes).
2. Once per headset: `mcx-app runner` builds the Runner (`com.metacodex.runner`, signed with the same key) and
   installs it: the user taps Install.
3. `mcx-app check /root/metacodex-projects/native/<name>`, then `mcx-app dev /root/metacodex-projects/native/<name>`: compiles only `libapp.so`
   (`mcx-build --module-only`), hands it to the dev server in `mcx-buildd` (127.0.0.1:47951), starts the Runner when
   it is not polling, waits for the delivery: `RESULT: running in the Runner: module <sha> delivered N s after ...`.
4. Tell the user what changed; repeat 3. Logs: `mcx-app logs com.metacodex.runner` (`module loaded: ... in N ms`).
5. Ship it: `mcx-app build /root/metacodex-projects/native/<name>` packs the same module with the same host into the app's own APK
   (its package, label, icon); then `mcx-app install` / `launch` as for any app.

## Limits and failures

- `RESULT: the Runner is not installed: mcx-app runner (once)`: do step 2.
- `RESULT: the Runner did not fetch the module in 20 s`: the Runner is not in front (ask the user to open "Runner"
  from the Library) or MQH (and so `mcx-buildd`) is not running (`mcx-buildd status`).
- `dev channel: REFUSED a module that is not signed with this headset's key`: something else answered on the port,
  or the Runner was built with another key: rebuild it with `mcx-app runner`.
- A crashing module ends the Runner: fix the code and `mcx-app dev` again (it restarts the Runner).
- Runtime permissions (USE_SCENE) of the Runner: Settings > Apps > Runner > Permissions (no dialog: no Java code).
- `mcx-app dev` and `mcx-app runner` need the MetaCodex build that ships this overlay (control port launch).
