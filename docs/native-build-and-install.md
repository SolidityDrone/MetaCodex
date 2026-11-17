# Native apps: build, install, launch and return, on the headset

An agent in a native project, or the user by hand, takes a native headset app from code to check, build, verify,
install and launch, on the headset itself. Two kinds: **immersive** apps (C/C++, OpenXR, OpenGL ES; most of this file)
and **2D panel** apps (Java, Android views, shown as a window: [2D panel apps](#2d-panel-apps)). The built app lives in the Library and runs
without MetaCodex. The README has the user's view (the warning before native apps, the permissions); this file is the
reference.

## The pipeline

| Step | Command | What happens |
|---|---|---|
| Doctor | `mcx-app doctor` | Says what the toolchain lacks; `mcx-setup quest` installs it. |
| Check | `mcx-app check` | Syntax and type check only (0.7 s warm). |
| Build | `mcx-app build` | Compile, link, package and sign (1.9 s for a one-file change). |
| Verify | `mcx-app verify` | Static checks of the APK: symbols, permissions, features, devices, the app menu. |
| Install | `mcx-app install` | The user taps INSTALL in the APP READY dialog. |
| Launch | `mcx-app launch` | The user taps LAUNCH; the app starts with a launch id. |
| Return | the app's built-in menu | Back to MetaCodex, or Report with a picture and the log. |

The toolchain is not in the APK. `mcx-setup quest` installs it: a community arm64 build of the NDK (r27d),
android.jar 34, the OpenXR 1.1.65 headers and loader, D8 (the R8 9.5.32 jar from Google Maven, for 2D apps), and the
Debian packages aapt, zipalign, apksigner and a headless JDK (OpenJDK 21); about 420 MB to download and 2.5 GB on
disk, each download pinned by SHA-256.

## How a build runs

- **Off the frame loop.** `mcx-buildd` is a daemon in MQH's process tree (`mqh-run` starts it), so it runs on cores
  0-2 under `:ai`. An agent under MQH builds in its own shell. A build typed in a MetaCodex terminal (cores 3-5) is
  handed to the daemon over a Unix socket (`/tmp/mcx-buildd/sock`, folder 0700: other apps cannot reach it). One job
  at a time (4 waiting at most), at nice 10, cancelled when the client goes away.
- **Incremental.** `mcx-build` compiles per file with clang depfiles. The NDK glue object and the precompiled headers
  are built once per toolchain into `~/.cache/mcx-build` (the daemon warms them at start); a source that defines a
  macro that changes a header's meaning is compiled without the PCH (`PCH=0` in `build.conf` turns it off). aapt2 runs
  only when the manifest, `res/` or `assets/` changed. `mcx-build` writes the APK itself (stored entries 4-aligned,
  `.so` 16 KiB-aligned, libraries deflated once per content).
- **Signing.** A built-in APK Signature Scheme v2 signer (RSA PKCS#1 v1.5, SHA-256; min SDK 32 needs no v1) with one
  key for every app: openssl reads the keystore once into `~/.android/metacodex-debug.keystore.signcache.pem` (0600).
  `--signer apksigner` is the fallback. An update of an app MetaCodex installed asks nothing.
- **Guards.** A build waits while less than 900 MB of memory is free (`MCX_BUILD_MIN_MB`) and stops a tool below
  450 MB (`MCX_BUILD_KILL_MB`, exit code 4); a build needs about 100 MB. A heat guard exists (`MCX_BUILD_MAX_C`) but is
  off: the thermal zones cannot be read from the app's domain.
- **In the background.** While `:ai` has child processes it is a foreground service, and a build holds a wake lock.
  Android stops a background process whose children use more than 25 % CPU over 5 minutes; when `:ai` has no
  foreground service (`oom_score_adj` 600 or more), the daemon pauses a job whenever the builder's CPU over the last 5
  minutes would pass 15 %.

## Install, launch and the way back

- **Install** goes through Android's PackageInstaller (`Plugins/Android/MetaCodexInstaller.java`). The first install of
  an app needs the user's INSTALL tap and Android's own prompt once; MetaCodex must be allowed to install apps.
- **Launch only on the user's tap, in every approval mode.** `system launch` (and `install-apk` for an app that is not
  installed yet) is a request in MetaCodex (`ShellReturn.RequestLaunch`): the dialog APP READY with LAUNCH or INSTALL
  and LATER, a callout kept in MESSAGES, and the voice commands `launch_pending_app` / `decline_pending_app` (enabled
  only while a request waits). `mcx-app launch|install|run` return at once with `RESULT: requested ...`; `mcx-app
  status` reads pending, launched, declined, installing or failed. An allowed first install turns into a LAUNCH offer.
- **The app menu.** Every app on `mcx::App` has a built-in menu on the left controller's menu button and on a hand's
  menu gesture (left hand: palm towards your face, then pinch; it needs the hand aim extension): Back to MetaCodex and
  Report to MetaCodex (only when MetaCodex launched the app),
  the app's own items, Quit and Close menu. `mcx-app verify` fails an app that is not on `mcx::App` or has its own
  exit or OpenXR loop.
- **Back** starts MetaCodex's launch intent (`NEW_TASK | REORDER_TO_FRONT`) while the app is still in front, then
  ends the session at once (`xrRequestExitSession`, `ANativeActivity_finish`) and stops calling `xrWaitFrame`, so a
  pause never blocks the main thread. MetaCodex's activity is `singleTask`: it resumes and reloads nothing. It shows
  "Back from <app>" and brings the launching thread forward; the windows stay in the room ([ui.md](ui.md)).
- **Report** travels in the same intent: a PNG of the app's left-eye image (scaled down below about 450 KB), the last
  60 log lines, the label and the version. MetaCodex puts the picture into CLIPBOARD, attaches both to the thread and
  drafts "Feedback on <app> <version>:". The microphone does not start by itself.
- **Authentication.** Each launch gets a random 128-bit id (kept a day in `launches.json`), only in that app's launch
  intent. A return counts only with an id MetaCodex gave out for that package, and on Android 14 and later the
  sender package must match (`ActivityOptions.setShareIdentityEnabled`, set by `mcx_xr`).
- **After a crash** MetaCodex cannot come forward by itself (the permission that would allow it is not allowed on the
  headset); the user opens it again.

## Project types

`mcx-project set <dir> native|default|research [--kind immersive|panel]` writes `.metacodex/project.json`. A native
project has a kind, fixed once stored (`"kind": "immersive"` or `"panel"`; a marker without one is immersive): an
immersive one gets the 13 `quest-native-*` skills linked into its `.claude/skills` and a managed block in `AGENTS.md`
and `CLAUDE.md` with the rules (`native/project/AGENTS.md`); a 2D panel one gets the skill `quest-panel-apps` and its
block (`native/project/AGENTS-panel.md`); a research project gets a research note instead; default removes them. The native skills are not global (MQH
hands its global skills to every thread), so a website thread never sees native rules. `mcx-app new` marks app
folders native with the kind of what it copied; + PROJECT asks NATIVE APP (then IMMERSIVE or 2D PANEL), DEFAULT or
RESEARCH and makes the project in `/root/metacodex-projects/<type>/` (2D panel projects too: `native/<name>`).

## The skills

The 13 skills in `native/skills/` (shipped to `/opt/metacodex/quest-native/skills`): apps, xr, recipes, dev-loop,
debug, quality, art-direction, hands-first, rendering, ui-design, physics, 3d-models and performance. Three are
mandatory: hands-first (hands and mixed reality unless the user asks for VR or controllers), quality (a curated app
built in passes: blockout, look, motion, UI, polish and performance, self-review; the first install comes after the
self-review) and art-direction (one of six looks, with its palette, light and materials). The kits next to
`mcx_xr.h` (`mcx_gfx.h` with motion helpers and additive glow: `Sprites`, `Trail`, `Particles`; `mcx_ui.h`,
`mcx_physics.h`, `mcx_model.h`) are what the skills teach; the native layer has no audio. Two whole example apps live
in the skills: a solar system in the room (`quest-native-rendering/references/effects.md`) and a breakout game on the
real table (`quest-native-recipes/references/desk-game.md`). `python3 tools/check-native-skills.py` checks every
command, flag, path, API name and code block of the skills, and compiles the C++ blocks.

The Runner (`native/runner`, `mcx-app dev`) loads a module into a host app and reloads it (2.2 s from an edit to the
running app, over a signed channel); it works, but classic apps on `mcx::App` are the default.

## The native XR layer and its feature matrix

Layer: `native/xr/mcx_xr.{h,cpp}` (shipped to `/opt/metacodex/quest-native/xr`), compiled once per toolchain by
`mcx-build` into `~/.cache/mcx-build/libmcxxr-*.a` (6.1 s on the headset, once) and linked as an archive (apps that do
not use it carry nothing). Khronos OpenXR 1.1.65 headers only (the vendor's extensions are registered there): no Meta SDK
headers, no new third-party code (the text font is rasterized from DejaVu Sans Mono, which MetaCodex already ships).
Worked example: `native/examples/nightvision` (`/opt/metacodex/quest-native/examples/nightvision`).

Evidence levels: **compiles** (PC, NDK r27.1) < **builds on headset** (`mcx-build` on the Quest 3S, `mcx-build --verify`
ok: manifest entries, 0 unresolved symbols, APK signed) < **offered on headset** (the runtime offered the extension to a
probe app with these manifest entries: Runner log) < **ran on headset** (observed working at runtime).

| Feature | API (in `mcx_xr`) | Manifest / permission (the vendor's native docs) | Status |
|---|---|---|---|
| App lifecycle | `App::Pump/BeginFrame/EndFrame`: session READY -> begin, STOPPING -> end, EXITING / LOSS_PENDING -> exit; `Frame::focused` false while a system menu has focus (input off); Android pause/resume/destroy | NativeActivity, `singleTask`, `configChanges`, categories VR + LAUNCHER (native manifest page) | **ran on headset** (the same loop in hello-native and the Runner: session running, 72 fps) |
| Passthrough | `Passthrough`: start/pause, opacity, edges, `SetMonoToRgba` (256-entry map), brightness/contrast/saturation; layer submitted under the projection, projection with `BLEND_TEXTURE_SOURCE_ALPHA` | `<uses-feature android:name="com.oculus.feature.PASSTHROUGH">` (without it the extension does not enable) | builds on headset; **offered on headset** (Runner: `passthrough yes`); running pending (worn test) |
| Environment depth | `Depth`: provider + swapchain (GL texture array, 2 layers), `Acquire` per frame with near/far (far may be inf) and per-eye view-projection, hand removal | `com.oculus.permission.USE_SCENE` (runtime: `App` asks at start, polls the grant; `pm grant` works over adb); Quest 3 / 3S | builds on headset; **offered on headset** once USE_SCENE is declared (Runner); depth frames pending (worn test) |
| Hand tracking | `Hands`: 26 joints per hand, `XR_FB_hand_tracking_aim` pinch strengths, aim ray, `pinchStarted` edge, menu/system gesture | `com.oculus.permission.HAND_TRACKING` (install-time on Horizon OS: granted at install, seen in dumpsys) + `oculus.software.handtracking` | builds on headset; **offered on headset** (Runner: `hands yes`) |
| Controllers | `Controllers`: one action set; trigger, grip, A/X, B/Y, stick (+click), menu, aim/grip poses, haptics; Touch, Touch Plus (`XR_META_touch_controller_plus`), Touch Pro, simple profiles | none | builds on headset; runtime pending |
| Scene / room | `Spatial::QueryScene`: walls, floor, ceiling, furniture with semantic labels, 2D planes, 3D boxes, located each frame | `USE_SCENE` (runtime); a room from Space Setup | builds on headset; **offered on headset** (`XR_FB_scene`, `XR_FB_spatial_entity*` after the manifest entry); room mesh (`XR_META_spatial_entity_mesh`) not implemented |
| Spatial anchors | `Spatial::CreateAnchor` (+ storable + save, local, indefinite), `LoadAnchors`, `EraseAnchor` | `com.oculus.permission.USE_ANCHOR_API` (install-time on Horizon OS) | builds on headset; offered on headset (`XR_FB_spatial_entity_storage`); runtime pending |
| Display / performance | `refreshRates`, `SetRefreshRate` (`XR_FB_display_refresh_rate`), `SetFoveation` 0-3 (`XR_FB_foveation(_configuration)`, `swapchain_update_state`), `SetPerformance` CPU/GPU (`XR_EXT_performance_settings`) | none | builds on headset; refresh-rate query **ran on headset** (Runner: 72 Hz); foveation/perf offered |
| Runtime permissions | `Permissions::Granted/Request` (JNI `checkSelfPermission` / `requestPermissions` on the NativeActivity; polled once a second) | the permission in the manifest | builds on headset; dialog pending (worn test) |
| Text / UI | `Text::Draw` (DejaVu Sans Mono atlas, 3D), `Text::Rect` (panels) | none | builds on headset; runtime pending |
| Library icon + label | `android:icon="@drawable/icon"` + `res/drawable/icon.png`, `android:label` | aapt2 compiles `res/` on the headset | **builds on headset** (`--verify`: label Night Vision, icon res/drawable/icon.png) |
| Devices | `com.oculus.supportedDevices` | canonical ids: `quest2|questpro|quest3|quest3s|vrglasses` (Meta 2026-10-07); depth apps: `quest3|quest3s` (`--verify` flags quest2/questpro with depth) | verified against the docs |
| Static assertion | `mcx-app verify <dir>` (`mcx-build --verify`) | maps each Meta API in use to its permission / feature and reports what is missing | **ran on headset** (Night Vision ok; a manifest without the passthrough feature and USE_SCENE gave 4 problems) |

### Night Vision (the example app)

`native/examples/nightvision`: environment depth -> per-eye full-screen pass that reprojects the depth map through the
depth camera's view-projection, green phosphor brightness from distance, edges from depth discontinuities, grain,
scanlines; three modes (PHOSPHOR, EDGES, RANGE); passthrough underneath, turned green by a mono -> RGBA colour map,
toggled; a pinch of either hand (or the right trigger) = mode, both hands pinching together (or A) = passthrough, stick = gain, left grip = drop an anchor; HUD panel
(text) following the head lazily; scene query and anchor count in the HUD; foveation 2, CPU/GPU sustained high.

| Step on the Quest 3S | Result |
|---|---|
| build, cold (XR layer once + PCH + app), adb context pinned to cores 0-2 | 11.2 s (XR layer 6.1 s once), 710 KB |
| build, one file changed | 1.5 s |
| `mcx-app verify` on the headset | ok: 6 permissions (INTERNET, HAND_TRACKING, USE_ANCHOR_API, USE_SCENE, OPENXR, OPENXR_SYSTEM), features PASSTHROUGH + handtracking + headtracking, 226 imports / 0 unresolved, 21 extensions asked for, 42 extension functions, devices quest3\|quest3s |
| `apksigner verify --min-sdk-version 32` (on the headset) | ok |
| adb install as `com.metacodex.probe.nightvision`, `pm grant ... USE_SCENE` | installed; USE_SCENE (runtime) granted, HAND_TRACKING / USE_ANCHOR_API install-time granted |
| launch | not verified on the headset yet: the runtime checks (session, passthrough, depth frames, screenshot) are in [TEST_CHECKLIST.md](TEST_CHECKLIST.md#native-apps) |


## Measured on the test headset

In the `:ai` context unless noted, with MetaCodex running at 72 fps:

| Stage | Cold | Warm | Peak RSS | Cores | Notes |
|---|---|---|---|---|---|
| check (`mcx-app check`) | 2.9 s (no PCH yet) | **0.7 s** | 67 MB | 0-2 | |
| compile (1 file) | 4.35 s (glue + no PCH) | **1.7 s** (PCH) | 77-100 MB | 0-2 | |
| link | 0.09 s | 0.09 s (skipped if objects unchanged) | 30 MB | 0-2 | |
| package | 0.52 s (aapt2) | **0.06 s** (aapt2 skipped) | 40 MB | 0-2 | |
| sign | 0.03-0.06 s | 0.03 s | 23 MB | 0-2 | apksigner on the JVM: 1.2-1.8 s |
| **build total** | 2.4 s (app cold, cache warm); 5.1 s (all cold) | **1.9 s** (one file changed); 0.1 s (nothing changed) | 100 MB | 0-2 | a full rebuild with the stock tools: 9.04 s |
| through the daemon from `/immersive` | | +0.35-0.5 s (client start + socket) | | 0-2 | job 7 |
| install (an update of an app MetaCodex installed) | | about 2 s | | | |
| Runner reload (`mcx-app dev`) | 2.6 s | **2.2 s** edit -> running (compile 1.15 + link 0.10 + deliver + load 2.4 ms) | 71 MB | 0-2 | adb context |
| Night Vision build (XR layer) | 11.2 s (layer once 6.1 s) | 1.5 s | 129 MB | 0-2 (taskset) | adb context |

Other measurements: builds on cores 0-2 cost no measurable frame rate (72.5 fps on average, 70 at least, against 72.5
and 71 without a build); 5 builds in a row took 4.1 s each with no slowdown; with the headset asleep, 139 builds ran
in 240 s and the CPU was never suspended; free memory with MetaCodex, MQH and the local AI idle was 2.0-2.1 GB, and
1,868 MB at the lowest during a build. A precompiled header cuts a syntax check from 2.75 to 0.68 s.

## 2D panel apps

A 2D panel app is a plain Android app (an Activity with Android views, Java) that Horizon OS shows as a window beside
the user's other windows: no OpenXR, no VR category. Opening it puts MetaCodex in the background; the app's top row has
Back to MetaCodex and Report (from the template's `Mcx.java`, with the same launch id and extras as `mcx_xr`), so the
loop with the agent's thread is the same as for immersive apps. Skill: `quest-panel-apps` (with `references/design.md`,
`android-apis.md`, `debug.md`). Projects: + PROJECT > NATIVE APP > 2D PANEL (kind `panel`); `mcx-app new . --panel`
(the template: a window with a counter) or `--example zipper` (pick files with the system file picker, zip them into a
file the user chooses, progress, stop, the result size; no storage permission).

**Manifest rules** (Meta's docs for Android apps, checked with the metavr docs search on 2026-11-17: create-app,
port-an-existing-app, publish-mobile-manifest, gaze-app-config, permissions-prohibited; `mcx-app verify` checks each):
MAIN + LAUNCHER and no `com.oculus.intent.category.VR`; `<layout android:defaultWidth="1024dp"
android:defaultHeight="640dp" android:minWidth="360dp" android:minHeight="225dp" />` in the activity (the system may
shrink a window to 360dp wide for Look and Pinch); resizable; `minSdkVersion` 32 and `targetSdkVersion` 34 (2D apps
may target 32-37; new store apps 34); `android.hardware.vr.headtracking` absent or not required;
`com.oculus.supportedDevices` (`quest2|questpro|quest3|quest3s|vrglasses`); no eye tracking permission; nothing from
Meta's prohibited list; INTERNET when the code uses `java.net` (the log lines do); the launcher class present in
`classes.dex`. Design (verify cannot see it; the skill teaches it): targets 48dp+ (60dp main), 16dp apart, hover states,
18sp text, contrast 4.5:1, no pure white or black, usable from 360 to 1280dp wide.

**The build** (`mcx-build`, app type `panel`: `build.conf TYPE=panel`, or no `android.app.lib_name` and `.java` files in
`src/`):

| Stage | What | Skipped when |
|---|---|---|
| resources | aapt2 links the manifest, `res/`, `assets/` into the base APK and writes `R.java` (build/.cache/gen) | none of them changed |
| javac | `javac --release 17 -g -proc:none -cp android.jar[:libs/*.jar]` over `src/**/*.java` + `R.java` | no `.java`, `R.java`, jar or flag changed (then D8 is skipped too) |
| dex | D8 (`--release --min-api <minSdkVersion> --lib android.jar --thread-count 2`) over the classes (+ `libs/*.jar`) -> `classes*.dex` | as javac |
| package, sign | the base APK's entries + the dex files (deflated once per content), stored entries aligned; the same v2 signer and key | |

javac and D8 run in **mcx-buildd's warm JVM** (`McxJavaServer`, shipped as source in
`/opt/metacodex/quest-native/jserver/` and compiled once per JDK into `~/.cache/mcx-build/jserver-*`): javac through
`javax.tools`, D8 through its API, one request at a time, asked for over the daemon's socket (`{"op": "java"}`, served
outside the job queue). The JVM starts on the first request at the daemon's nice level on cores 0-2
(`-XX:+UseSerialGC -Xmx512m -XX:ActiveProcessorCount=2`, heap given back after each request), and stops after
`MCX_BUILDD_JVM_IDLE` seconds without one (300), when MemAvailable stays under `MCX_BUILD_MIN_MB` while idle, when it
falls under `MCX_BUILD_KILL_MB` during a request (the build stops with exit 4), or with the daemon; its CPU counts in a
job's background budget; `mcx-buildd status` shows it (`jvm`). Without a daemon (or with one from before) `mcx-build`
starts a JVM per tool (`-XX:TieredStopAtLevel=1 -XX:+UseSerialGC`). D8 is fetched by `mcx-setup d8` on the first 2D
build when it is missing.

Java: the Java 17 language; `android.*` is checked against API 34, but `java.*` against Java 17 (a Java 17 API that
Android lacks compiles and fails at run time: the skill lists what is safe). No Gradle, AndroidX, Kotlin or Google Play
Services; plain jars in `libs/`.

Measured on the PC (WSL, Debian's aapt2, OpenJDK 27, android-34's android.jar, R8 9.5.32; a dry run of the same
`mcx-build`), the template app:

| Build | JVM per tool | Warm JVM (mcx-buildd) |
|---|---|---|
| first build (aapt2 + javac + D8 + sign) | 5.1 s | 5.1 s (JVM start 1.5 s) |
| one `.java` changed | 3.5 s (javac 1.8, D8 1.7) | **1.3-1.5 s** (javac 0.8-0.9, D8 0.45-0.5) |
| nothing changed | 0.1 s | 0.1 s |

The zipper: 25 KB APK, `mcx-app verify` ok, `apksigner verify --min-sdk-version 32` ok (v2). The warm JVM held 158 MB
RSS. On the headset: see the table below once measured.

## Limits

- MetaCodex's GPU memory grows across pauses: after about six switches to other apps in 45 minutes the app held
  5.4 GB, and Android's low-memory killer stopped `:ai` twice. `tools/gpu-switch-test.sh <pkg> 10` and
  `tools/return-test.sh <pkg> [report] [cold]` measure it.
- Not verified on the headset: REPORT; the runtime of the camera, depth, hand and controller features; Night Vision's
  depth view.
- The signing key and its PEM copy must stay out of backups that leave the headset.
