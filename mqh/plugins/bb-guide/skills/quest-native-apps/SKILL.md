---
name: quest-native-apps
description: Build small native Meta Quest apps (C++, OpenXR with Meta's extensions, OpenGL ES; no Unity, no Gradle, no PC) inside MetaCodex's Linux, check and run them in seconds in the Runner without an APK (mcx-app dev), and install them on this headset with Android's own installer while the user confirms in VR (mcx-app; "path B"). Use when the user wants a separate app on the headset (its own icon in the Library, full screen in VR) - a tool, toy, demo or feature-flag app - and to check, build, run, install, launch, read the logs of, or fix the install of such an app. For behaviour inside MetaCodex itself use quest-scripting, quest-gesture-plugins or quest-views instead (they apply live, nothing to install).
---

# Native Quest apps from MetaCodex

A native app here is one APK: C++ compiled with the Android NDK, OpenXR for the headset (Khronos loader), OpenGL ES 3
for drawing, `NativeActivity` (no Java). You build it in this Linux and MetaCodex installs it with Android's
PackageInstaller; **Android asks the user, never you.** One command does each step: `mcx-app`.

## What is installed (MetaCodex runs `mcx-setup quest` once, by itself, at first use)

| What | Where |
|---|---|
| Android NDK r27d (clang 18, arm64 host build) | `/opt/android-ndk` (`ANDROID_NDK_HOME`) |
| android.jar, Android SDK Platform 34 | `/opt/android-sdk/platforms/android-34/android.jar` |
| OpenXR 1.1.65: headers + Khronos Android loader | `/opt/openxr/include/openxr/`, `/opt/openxr/lib/arm64-v8a/libopenxr_loader.so` |
| aapt2, zipalign, apksigner, keytool (JDK) | Debian packages, on PATH |
| the template (Hello Native: three turning cubes) | `/opt/metacodex/quest-native/template` |
| the module template (Hello Module: the same cubes as a module) | `/opt/metacodex/quest-native/module-template` |
| the native host + Runner sources, the module header `mcx_module.h` | `/opt/metacodex/quest-native/runner` |
| the build pipeline / the builder daemon | `mcx-build`, `mcx-buildd` (on PATH; caches in `~/.cache/mcx-build`) |
| one signing key for every app built here | `~/.android/metacodex-debug.keystore` (made by the first build; **never delete**) |
| your apps | `/root/apps/<name>` |

Always start with `mcx-app doctor`. `WAIT` = the first-use setup is still running (log
`/root/.metacodex/first-use.log`): wait for it, never start a second one. `MISSING` = run `nice -n 10 mcx-setup quest`
(about 400 MB to download, 2.5 GB on disk, a few minutes on Wi-Fi; safe to rerun).

## Two kinds of app

| | classic (`mcx-app new <dir>`) | module (`mcx-app new <dir> --module`) |
|---|---|---|
| what you write | `src/main.cpp`: the whole NativeActivity (OpenXR loop included) | `src/app.cpp`: only the scene (`mcx_app_init / frame / draw / shutdown`) |
| try it | build + install + launch (an APK each time) | **`mcx-app dev`: running in the Runner ~2 s after an edit, no APK, no install** |
| ship it | `mcx-app run` | `mcx-app run` (the same module + MetaCodex's host in the app's own APK) |

**Classic apps on `mcx::App` (below) are the default**: they get every Quest feature. Module apps suit quick visual
iteration in the Runner (parked: it needs `mcx-app runner` once, and no controllers). The host (`mcx_module.h`) gives the module the
`XrInstance`, `XrSession`, LOCAL/STAGE spaces, `xrGetInstanceProcAddr`, `hasExtension(name)`, `log(...)`, the assets
and a private data folder; it enables Meta's extensions the runtime offers (hand tracking + aim/mesh, passthrough,
scene, spatial entities/anchors, environment depth, body tracking, refresh rate, foveation, ...: the log lists the
ones not offered). `mcx_app_frame` may add up to 4 layers under the projection (e.g. an
`XrCompositionLayerPassthroughFB`) and set `projectionFlags`. `mcx_app_draw` draws one eye into the bound
framebuffer (sRGB + depth, not cleared) with the given `projection` and `viewMatrix`. On a reload the old module's
`mcx_app_shutdown` runs, then the new one's `mcx_app_init`: free what you made, keep state in `host->dataPath`.

## Quest features: the native XR layer (`#include <mcx_xr.h>`)

For mixed reality and anything beyond a plain scene, write a **classic app on `mcx::App`** (mcx-build links the layer;
nothing to configure). Worked example to copy from: `/opt/metacodex/quest-native/examples/nightvision` (depth-shaded
night vision over a green passthrough, hands + controllers switch modes, HUD text, room query, anchors).

```cpp
#include <mcx_xr.h>
void android_main(android_app* a) {
    mcx::Config cfg; cfg.name = "My App"; cfg.passthrough = true; cfg.depth = true;   // hands, controllers: on by default
    mcx::App app; if (!app.Init(a, cfg)) { while (app.Pump()) {} return; }
    while (app.Pump()) {                                  // events + lifecycle; false = exit
        mcx::Frame f; if (!app.BeginFrame(f)) continue;   // views, input (only when f.focused), hands, depth
        for (int e = 0; e < 2; e++) { mcx::Eye eye; app.BeginEye(e, eye);
            glClearColor(0, 0, 0, app.clearAlpha()); glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            /* draw with eye.viewProj */ app.EndEye(e); }
        app.EndFrame(); }                                 // passthrough underlay + projection layer
    app.Shutdown(); }
```

| Feature | Turn on / call | Manifest (exact; the template lacks them: add only what you use) |
|---|---|---|
| Passthrough | `cfg.passthrough`; `app.passthrough.Start/Pause`, `SetMonoToRgba`, `SetBrightnessContrastSaturation`, `SetStyle(opacity, edge)`; clear alpha 0 where the room shows; premultiplied colour | `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />` |
| Environment depth | `cfg.depth`; `f.depth.valid`, `.texture` (GL_TEXTURE_2D_ARRAY, layer = eye), `.viewProj[eye]`, `.Linear(d)` metres; `app.depth.SetHandRemoval` | `<uses-permission android:name="com.oculus.permission.USE_SCENE" />` (runtime: asked at start, the app starts depth once granted); `com.oculus.supportedDevices` = `quest3|quest3s` |
| Hands | `app.hands.hand[0/1]`: `tracked`, `joints[XR_HAND_JOINT_*_EXT]`, `aim`, `pinch[0..3]`, `pinching`, `pinchStarted` | `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />` + `<uses-feature android:name="oculus.software.handtracking" android:required="false" />` |
| Controllers | `app.controllers.ctl[0/1]`: `active`, `aim`, `grip`, `trigger`, `squeeze`, `stick`, `primary` (A/X), `secondary` (B/Y), `*Pressed` edges; `Haptic(hand, amp, s)` | none |
| Room (scene) | `cfg.scene`; `app.spatial.QueryScene()` once `app.HasPermission(mcx::kPermScene)`; then `app.spatial.scene`: `labels` ("FLOOR", "WALL_FACE", "TABLE"...), `plane`, `volume`, `pose` | `USE_SCENE` (runtime); the user's Space Setup |
| Anchors | `cfg.anchors`; `app.spatial.CreateAnchor(app.local, pose, f.time, true)` (persists), `LoadAnchors()`, `anchors[i].pose` | `<uses-permission android:name="com.oculus.permission.USE_ANCHOR_API" />` |
| Display / perf | `cfg.refreshRate` (`app.refreshRates` lists 72/90/120...), `cfg.foveation` 0-3, `cfg.cpuLevel/gpuLevel` 0-3 | none |
| Text / panels | `app.text.Draw(eye.viewProj, model, "text", x, y, height, color)`, `app.text.Rect(...)`; place panels in the world (1-1.5 m, follow lazily), never head-locked | none |
| Library icon | `res/drawable/icon.png` + `android:icon="@drawable/icon"` on `<application>`, `android:label` | |

- `com.oculus.supportedDevices` canonical ids: `quest2|questpro|quest3|quest3s|vrglasses`; list only devices the app
  works on (depth: Quest 3 / 3S).
- After every build of an app with Meta features: **`mcx-app verify <dir>`**. It maps each Meta API the code uses to
  the permission / feature it needs, and checks unresolved symbols, icon and devices: fix every `PROBLEM:` line.
- Runtime permissions: only `USE_SCENE` is asked at runtime on Horizon OS (`HAND_TRACKING`, `USE_ANCHOR_API` are
  granted at install). Denied: the app runs without depth / room; tell the user Settings > Apps > the app > Permissions.
- Input only while `f.focused` (a system menu took focus: pause, hide pointers). Never `environmentBlendMode` other
  than OPAQUE: the passthrough is its own layer.
- Module apps (Runner) get `host->instance/session`; `mcx::Passthrough`, `Depth`, `Hands`, `Spatial` work there with
  `Init(host->instance, host->session)`, controllers do not (the host owns the action sets). The Runner is parked:
  prefer classic apps.

## The loop: exact commands

1. `mcx-app doctor`
2. `mcx-app new /root/apps/<name> --label "<Name>"` (package `com.metacodex.user.<name>`; `--package` to choose;
   `--module` for a module app). For mixed reality start from the Night Vision example instead:
   `cp -r /opt/metacodex/quest-native/examples/nightvision /root/apps/<name>`, then set package and label.
3. Edit `src/` (all `.cpp` / `.cc` / `.c` files are compiled). Optional: `res/` (icons, strings), `assets/` (data files
   packed into the APK), `build.conf` (`CXXFLAGS=`, `CFLAGS=`, `LDLIBS=`, `OPT=-O2`, `PCH=0`).
4. `mcx-app check /root/apps/<name>`: compile check only, about 1 s. Errors print as
   `<file>:<line>:<col>: error: <message>`, then `RESULT: N error(s) ...`: fix and check again before building.
5. Module apps: `mcx-app dev /root/apps/<name>` → `RESULT: running in the Runner ...`. The first time ever, run
   `mcx-app runner` once (builds the Runner and installs it: the user taps Install). Repeat 3-5 while you iterate;
   ask the user what they see; `mcx-app logs com.metacodex.runner` shows the module's log lines.
6. `mcx-app build /root/apps/<name>` → `build/<name>.apk` (incremental: only changed files compile; about 2 s).
7. `mcx-app install /root/apps/<name>` → path B (below). It prints `>>> ASK THE USER: ...` lines: tell the user that,
   in one short sentence, and let the command wait (up to 5 minutes; it polls by itself). It ends with `RESULT: ...`.
8. `mcx-app launch /root/apps/<name>` → the app opens in the headset and MetaCodex goes to the background.
9. When the user is back: `mcx-app logs /root/apps/<name>` (everything the app logged), then ask what they saw.

`mcx-app run <dir>` = steps 6 + 7 + 8. `mcx-app info <dir|package>` = installed version and installer of record.
Before installing, show the user what the app will do and get their OK (they will see Android's dialog anyway).

## Path B: how the install works

- `mcx-app install` → `mcx-ui system install-apk <apk>`: MetaCodex opens a PackageInstaller session, copies the
  APK in (off its render thread) and commits it. Android then decides and asks the user. The APK may be anywhere in
  this Linux (`/root/...`, `/mnt/quest/...`): MetaCodex reads it from its own storage; paths with `/../` are refused.
- **Once per headset**: Android needs the user's **"Install unknown apps"** switch for MetaCodex. While it is off,
  `install-apk` opens that settings page in the headset; the user turns on **"Allow from this source"** and comes
  back; `mcx-app` waits for it (`mcx-ui system can-install`: `yes` | `no: ...` | `unavailable: ...`, opens nothing).
- **Every new app**: Android shows "Do you want to install this app?" and the user taps **Install**.
- **Updates** (same package, same signing key, `versionCode` not lower): when MetaCodex installed the app, Android
  lets the update through **without a dialog** (`success: ... (no prompt: an update of an app MetaCodex installed)`;
  Android 12+, the app must target API 31 or newer on Android 14: the template targets 32). Apps first installed by
  adb or another installer get the dialog again ("Update").
- States, as `mcx-ui system install-status` shows them (only the latest install counts):
  `installing <apk> (session N)` → `waiting for the user to confirm in the headset (session N)` →
  `success: <package>` or `failure (<code>): <message>`.
- **Store builds of MetaCodex cannot install apps**: Meta prohibits `REQUEST_INSTALL_PACKAGES` on the Horizon Store,
  so those builds leave it out and `can-install` answers `unavailable: ...`. Tell the user; never look for a
  workaround. Path B exists in sideloaded MetaCodex builds (developer mode or SideQuest).

| RESULT | Meaning | What to do |
|---|---|---|
| `failure (3)` | the user cancelled or closed Android's dialog | ask; install again only if they want |
| `failure (5)` ...`UPDATE_INCOMPATIBLE` / signatures | an installed copy has another signing key (adb, another machine) | the user uninstalls it (Library > the app > ... > Uninstall), or use a new package name |
| `failure (7)` ...`VERSION_DOWNGRADE` | lower `versionCode` than the installed one | raise `android:versionCode` |
| `failure (7)` ...`NO_MATCHING_ABIS` / `OLDER_SDK` | no `lib/arm64-v8a`, or `minSdkVersion` above 34 | keep the template's ABI and SDK lines |
| `failure (4)` ...`PARSE` | broken or unsigned APK | `mcx-app build` again |
| `failure (6)` | the headset is full | `mcx-ui system storage` |
| switch still off (exit 2) | the user did not turn on "Allow from this source" | ask once more |
| no answer in 5 minutes (exit 2) | the dialog is still open, maybe behind a window | ask the user to look for it |
| `unavailable: ...` (exit 3) | Store build of MetaCodex | not possible in this build |

## Best practices

- **Package names**: `com.metacodex.user.<name>`, lower case, one per app. Never `com.pnncl.metacodex` (MetaCodex),
  `com.oculus.*`, `com.meta.*`, `android.*`.
- **Signing**: every app (and the Runner) is signed with `~/.android/metacodex-debug.keystore`; Android accepts an
  update only with the same key, and the Runner accepts only modules signed with it. Never delete it or its
  `.signcache.pem` companion, never copy either into a project, a commit or a message.
- **Versions**: the same `versionCode` reinstalls; raise it (and `versionName`) for a release; never lower it.
- **Manifest** (Meta's native manifest page; the template already has it): `minSdkVersion` and `targetSdkVersion` 32,
  `screenOrientation="landscape"`, theme `@android:style/Theme.Black.NoTitleBar.Fullscreen`, the template's
  `configChanges`, `<uses-feature android:name="android.hardware.vr.headtracking" android:required="true"
  android:version="1" />`, categories `com.oculus.intent.category.VR` + `android.intent.category.LAUNCHER`,
  `com.oculus.supportedDevices` = `quest3|quest3s|quest2|questpro`, `exported="true"`, `hasCode="false"`. No
  `noHistory`. For a Store submission of the app itself, Meta's current target rule (Android 14) applies.
- **Permissions**: only what the app uses. `INTERNET` is in the template for `mcx-app logs` only. Never a permission
  from Meta's prohibited list.
  - Hand tracking: `<uses-permission android:name="com.oculus.permission.HAND_TRACKING" />`,
    `<uses-feature android:name="oculus.software.handtracking" />`, extension `XR_EXT_hand_tracking`.
  - Passthrough: `<uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />`,
    extension `XR_FB_passthrough`; submit the passthrough layer (`XrCompositionLayerPassthroughFB`, flag
    `XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT`) **before** the projection layer, clear to alpha 0 where
    the room should show, keep `environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE` (Meta requires it).
- **OpenXR**: list the extensions first (`xrEnumerateInstanceExtensionProperties`) and enable only those present
  (instance creation fails otherwise). Keep the template's loop: `xrPollEvent` every frame; begin the session on
  `READY`, end it on `STOPPING`, quit on `EXITING` / `LOSS_PENDING`; draw only when `shouldRender`. The swapchains are
  sRGB (`GL_SRGB8_ALPHA8`): write linear colours.
- **Where builds run**: always through `mcx-app check|build|dev`, never `clang`/`build.sh` by hand in a MetaCodex
  terminal: those terminals share cores 3-5 with MetaCodex's frame loop. `mcx-app` builds in its own shell only
  when that shell may use cores 0-2 (agents under MQH), otherwise it hands the job to `mcx-buildd` (on cores 0-2,
  one job at a time: a second one prints `queued behind 1 job(s)`). Measured: 72 fps held during builds.
  `mcx-buildd status` shows the daemon; `warning: no builder daemon` means MQH is not running.
- **Guards**: a build waits while free memory is under 900 MB and stops a tool under 450 MB (`exit 4`, "stopped ...:
  MemAvailable"): tell the user, close something, build again. Never raise the parallel jobs (default 2).
- **Logs**: `LOG("x = %d", x)` (printf style) goes to logcat and to `mcx-app logs`; a crash ends the log, so its
  last line shows how far the app got. You cannot read logcat from here.
- **Small steps**: one feature per app, one change per install, ask the user after each launch.
- While the app runs, MetaCodex (and this shell) may be paused. When the user quits the app (Meta button > Quit) they
  land in Home and reopen MetaCodex from the Library.
- **Uninstall**: the user does it in the Library (the app > ... > Uninstall). MetaCodex has no uninstall command.

## The Runner

- One app, `com.metacodex.runner` ("Runner"), built on this headset by `mcx-app runner` (installed once with path B).
  While it is in front it asks MetaCodex's Linux for the current module (127.0.0.1:47951) and loads a new one within
  a second, keeping its OpenXR session. It loads only modules signed with this headset's key for the nonce it chose:
  `dev channel: REFUSED ...` in its log means something else answered on the port.
- Its log: `mcx-app logs com.metacodex.runner` (`module loaded: ... in N ms`, the module's own lines). A module that
  crashes ends the Runner: fix it, `mcx-app dev` again (it starts the Runner when it is not running).
- Runtime permissions (scene, body tracking) of the Runner or of a module app are granted by the user in the
  headset's Settings > Apps > the app > Permissions (these apps have no code to ask).

## Do not

- Do not install with adb (`adb connect 127.0.0.1:5555`): it needs developer mode, a PC to switch it on after every
  reboot and the user's approval for each new key; every retry shows Android a new prompt. Only when the user asks.
- No `pm install` / `cmd package` (not allowed for apps); never write into other apps' folders.
- Never ask for a MetaCodex rebuild or update to install an app: none is needed.
- Do not run `mcx-build`, `clang`, `aapt2`, `apksigner` or `build.sh` directly in a MetaCodex terminal (cores 3-5:
  MetaCodex's frame loop); `mcx-app` puts them where they belong.
