# Native Horizon OS apps: build and install from MetaCodex

Plan of 2026-11-11. Branch `feat/native-horizon-building-and-install` (from `feat/core-session-context-protocol`
2aacb5b; the old `feat/native-builder` branch is not used). The working agent fills in the tables and the
decisions as it measures.

## Status (2026-11-11, measured on the Quest 3S)

| Piece | State | Evidence |
|---|---|---|
| Builder off the frame loop (`mcx-buildd`, started by `mqh-run`) | **done, device-proven with dev copies** | a build typed into a MetaCodex terminal (`/immersive`) ran as job 7 on cores 0-2; fps 72 held |
| Incremental pipeline `mcx-build` + `mcx-app check` | **done, device-proven** | one-file change 9.0 s -> 1.9 s, check 2.9 s -> 0.7 s (table below) |
| Built-in APK v2 signer (no JVM) | **done** | `apksigner verify` passes on PC and headset; same certificate as the keytool keystore |
| `:ai` foreground service + build wake lock | **committed, compile-checked; required** (R3, R9) | E4: service stopped ~65 s after another app came in front; R9: `:ai` killed for child CPU while in the background |
| Background CPU budget in `mcx-buildd` | done (device test: see R9) | keeps the builder under 15 % CPU per 5 min while MetaCodex is in the background, the guard that works without the APK change |
| Runner / module apps / `mcx-app dev` (JIT) | **parked (works, E6 passed)** | 2.2 s edit -> running, signed channel; parked by the owner's priority change (Quest SDK completeness first) |
| Native XR layer `mcx_xr` + Night Vision | see "Feature matrix" | |
| Agent skill suite `quest-native-*` (apps, xr, recipes, dev-loop, debug) | done; accuracy gate `tools/check-native-skills.py`: ok | every command, flag, path, API name and quoted message exists; all C++ blocks compile; the clock recipe builds as an app and `verify`s ok |
| Shipping it | needs a MetaCodex APK build (overlay + `mqh-run` in the MQH bundle) | until then the dev copies live in `/root/.metacodex/dev/bin` on the headset |

## Goal

Inside MetaCodex on the headset, a user (through an agent, or by hand) goes **code → compile check → build →
install → launch** for a native Horizon OS app: C/C++, OpenXR with Meta's OpenXR extensions, OpenGL ES (or
Vulkan). After install the app lives in the Library on its own and runs without MetaCodex. On top of that, a
**run-now path without an APK build** for fast testing ("JIT").

Owner's order of priority:

1. **Feasible with no crash.** Blockers first. MetaCodex never crashes or gets killed because of a build. The
   built app installs and stays installed.
2. **Speed.** Edit → running as fast as possible.
3. **MetaCodex keeps running well during builds:** 72 fps, voice and MQH responsive.

Allowed fallback if (3) cannot hold: a **Build mode** that turns the heavy features off and shows a BUILDING
panel with progress, suggesting the user take the headset off for a while. That only works if a build keeps
running with the headset off (risk R4).

## What exists (read from the code, 2026-11-11)

| Piece | Where | State |
|---|---|---|
| `mcx-app` doctor / new / build / install / launch / run / logs / info | `userland/overlay/usr/local/bin/mcx-app` | shipped in 0.7.5+; build = `nice -n 10 [taskset -c 0-2] build.sh device` |
| Build script: compile every `src/*` → link → `aapt2 link` → add libs (python zip) → `zipalign` → `apksigner` (JVM) | `native/hello-native/build.sh device\|pc` | full rebuild each time (`rm -rf build`); `pc` mode proven, **`device` mode never measured on the headset** |
| Toolchain: NDK r27d (community arm64 build), android.jar 34, OpenXR 1.1.65 headers and Khronos loader, apt `aapt zipalign apksigner default-jdk-headless` | `mcx-setup quest` | ~400 MB download, 2.5 GB on disk, sha256-pinned |
| Install (path B): PackageInstaller session; first install = one Install tap; updates of apps MetaCodex installed = no prompt (~2 s, proven by the owner) | `unity/Assets/Plugins/Android/MetaCodexInstaller.java` | works |
| Launch: launcher intent; MetaCodex goes to the background | `MetaCodexInstaller.launch` | works |
| Agent skill | `mqh/plugins/bb-guide/skills/quest-native-apps/SKILL.md` | written for the current flow |
| `:ai` service process: named children (extras `argv` `env` `cwd` `log` `name`); MQH and the agents run under it | `MetaCodexAiService.java`, `MqhHost.cs` | **plain started service: no `startForeground`, no wake lock** |

## Facts that shape the design (LEARNINGS 2026-11-07, docs/local-ai.md)

- Horizon OS puts the immersive app **and all its child processes** in `/immersive` = **cores 3-5**. That
  includes Unity, proot and every in-app terminal. `taskset -c 0-2` from there fails (EINVAL), so
  `mcx-app build` typed into a MetaCodex terminal competes with the frame loop today.
- A process of the `:ai` service lands in `/foreground` = cores 0-2, and its children inherit that. Builds run
  by agents (MQH under `:ai`) land there. QVAC's LLM on 0-2 held 72 fps.
- MetaCodex targets SDK 34 (Android 14). Meta's docs: Quest apps target API 32 or lower (the template targets 32).
- Off-face (`am broadcast -a com.oculus.vrpowermanager.prox_close`), MetaCodex renders at 36 fps and the
  userland sees 3 cores. Off-face fps numbers are relative only; the 72 fps check needs the owner wearing the
  headset (checklist). Restore with `...automation_disable`.
- Wi-Fi adb drops when the headset sleeps; USB stays up.
- `~/mcxwork/qx/qx.sh '<cmds>'` runs commands in the headset's userland through adb `run-as` + proot. Those
  processes sit in adb's cgroup, not the app's: pin them to cores 0-2 with `taskset` to mimic the `:ai` context.

## Risks to settle first

| # | Risk | Why it matters |
|---|---|---|
| R1 | **Memory**: peaks of clang++, lld, aapt2 and the JVM (apksigner) on top of Unity, QVAC, MQH and the agent CLIs | lmkd or OOM kills; which process dies first? |
| R2 | **CPU / fps**: builds from `/immersive` steal Unity's cores; builds on 0-2 still share memory bandwidth and heat | frame drops |
| R3 | **Surviving in the background**: while the built app is in front, MetaCodex is in the background, and Android may stop a plain started service (`:ai`) after ~1 min and freeze or kill the main process | MQH, agents and the builder die mid-session |
| R4 | **Headset off**: display off → CPU suspend without a wake lock → builds stall; Wi-Fi adb drops | Build mode only works if this is solved |
| R5 | **Heat**: repeated builds → throttling → Unity fps | |
| R6 | **Speed**: proot (ptrace) overhead on clang's file I/O; JVM start for apksigner; full rebuild every time | |
| R7 | **`dlopen` of a delivered `.so` in a separate app** (no-build Runner): SELinux, W^X on Horizon OS | gates the JIT path |
| R8 | **Signing key**: one key for all user apps; losing it breaks every update (uninstall needed) | |

## Phase 0: measure on the headset (no MetaCodex APK build)

| # | Experiment | Record |
|---|---|---|
| E1 | `mcx-app doctor` in the userland; `mcx-setup quest` if tools are missing (check free storage first) | tools present, disk |
| E2 | Build the template: (a) pinned to cores 0-2 like the `:ai` tree; (b) from MQH (a real agent context) if reachable | wall time and CPU time per stage, peak RSS per stage, `/proc/<pid>/cpuset` |
| E3 | Memory baseline with MetaCodex + QVAC + MQH (+ one agent CLI) running, then during a build | MemAvailable min, `/proc/pressure/memory`, lmkd props (`getprop \| grep lmk`), `oom_score_adj` of each MetaCodex process |
| E4 | Launch a built app (path B), keep it in front 1 / 3 / 10 min | MetaCodex main and `:ai` alive? frozen? MQH answering? `oom_score_adj`, `dumpsys activity processes` |
| E5 | A 2-minute CPU job under the `:ai`-like context, headset off (display asleep) | does it keep running? wake locks held? |
| E6 | Probe APK (target 32): copy a `.so` into its files dir, `chmod 0500`, `dlopen` it; also fetch over 127.0.0.1 | works? `avc: denied` lines? |
| E7 | `apksigner` on the device: time and RSS, default vs tuned JVM (`-Xmx`, `-XX:TieredStopAtLevel=1`, CDS) | seconds, MB |
| E8 | 5 builds in a row | temperatures, CPU frequencies, off-face fps (relative) |

## Phase 1: decide

One table per stage (executor, packaging, signing, install, JIT), the options with their measured numbers,
and the choice. Ranked by the owner's order: no crash > speed > MetaCodex smooth.

## Phase 2: implement (likely shape, confirmed or changed by Phase 0)

- **A. Builder off the frame loop.** `mcx-buildd` runs as a named child of the `:ai` service (or under MQH), so it
  sits on cores 0-2 and has its own process. One job at a time, nice 19, `-j1`/`-j2`. `mcx-app build` and
  `check`, from any shell (including an `/immersive` terminal), hand the job to it over a Unix socket. It
  reports progress and structured errors (`file:line:col: message`), and has a memory guard: it refuses to
  start below a MemAvailable floor and stops the job on PSI or MemAvailable limits, with per-tool limits. Also
  a heat guard, and time plus peak RSS logged per stage.
- **B. MetaCodex stays alive in the background.** `:ai` becomes a foreground service (with the Android 14
  service type rules for targetSdk 34) and holds a partial wake lock only while a build runs. Verify that MQH,
  the agents and the builder survive 10 min with the user's app in front and with the headset off.
- **C. Speed.**
  - `mcx-app check` checks syntax only, using a precompiled header.
  - Object files are rebuilt only when their source hash changes; the glue and framework objects are prebuilt.
  - `aapt2` is skipped when the manifest, res and assets are unchanged; only the libs are re-zipped.
  - A tuned JVM or a faster signer.
  - Path B install, where updates are silent.
- **D. Run without an APK build ("Runner").**
  - One native host APK, signed with the same key and built once on the headset, loads the app as a module
    (`.so`) and hot-reloads it while keeping the OpenXR session.
  - The module gets the `XrInstance`, `XrSession` and `xrGetInstanceProcAddr`, so it has full access to
    Meta's OpenXR extensions. The Runner enables the common Meta extensions and declares their permissions.
  - Modules arrive over an authenticated localhost channel. Local ports can be taken by other apps (port
    squatting), so the Runner verifies the module's origin.
  - `mcx-app dev` compiles the module and pushes it: running again in seconds. `mcx-app ship` builds the
    standalone APK from the same sources.
  - Gated by E6. Use crash-safe reload ideas (e.g. `fungos/cr`, MIT) only if they pay off.
  - An embedded script engine (QuickJS or Lua) only if module compiles turn out too slow.
- **E. Build mode.**
  - A BUILDING panel in Unity: stage, progress, time left, errors.
  - A "quiet build" switches heavy features off (e.g. QVAC models unloaded, passthrough camera and blur off)
    and invites the user to take the headset off once R4 is solved.
  - New app actions are registered as complete commands (AGENTS.md).
- **F. Skill and docs.**
  - Update the quest-native-apps skill: new commands, never build from `/immersive`, check before build, the
    error format.
  - A `docs/TEST_CHECKLIST.md` section for the owner's worn test.
  - `docs/LICENSES.md` entries for any new third-party code.

## Done means

- On the Quest 3S, from an agent thread: the template app goes code → check → build → install → launch, shows
  in the Library and runs after MetaCodex is force-stopped.
- 10 builds in a row: MetaCodex never crashed or killed; MemAvailable above the floor; MQH, agents and QVAC
  alive after 10 min with the built app in front.
- A timing table: cold build, warm build (one file changed), check only, Runner reload.
- Off-face fps (relative) measured; the owner's worn fps test written as a checklist section.
- Each finished piece committed and pushed on this branch.

## Phase 0 results (Quest 3S, 2026-11-11, MetaCodex running with MQH and QVAC, headset off-face with `prox_close`)

Context labels: **ai** = a command run by MQH's host daemon (`mqh terminal run --machine localhost`), i.e. inside
MetaCodex's `:ai` service process tree: `/proc/self/cpuset` = `/foreground`, `Cpus_allowed_list` 0-2, `Seccomp_filters` 2
(the same filters as the app). **imm** = a MetaCodex terminal (`/immersive`, cores 3-5). **adb** = `qx.sh` (adb run-as,
cores 0-5). Off-face with `prox_close` MetaCodex rendered **72/72 fps** this time (VrApi), not the 36 fps seen on 11-07.

| # | Result |
|---|---|
| E1 | `mcx-app doctor`: every tool present (`mcx-setup quest` ran at first use); 23 GB free; Python 3.13.5, OpenSSL 3.5.7 in the rootfs. In the app's domain `/sys/class/thermal`, `/proc/pressure/*` and the battery temperature are **not readable**; `cpufreq/scaling_{cur,max}_freq` is. |
| E2 | Template (Hello Native, 1 source, 496 lines), ai context, nice 10. **Old `build.sh device`: 9.04 s** (compile + glue 5.0, link 0.24, package 1.04, sign 2.54), peak RSS 100 MB. **New `mcx-build`**: table below. |
| E3 | MemAvailable with MetaCodex + MQH + QVAC idle: 2.0-2.1 GB (MemTotal 7.9 GB). Lowest during any build: **1868 MB** (a build costs ~100 MB). lmkd: PSI-based (`use_psi` true, `use_minfree_levels` false). oom_score_adj: main 0 (`/immersive`), `:ai` 100 (`/foreground`, kept up by QvacHost's binding). |
| E7 | apksigner on the headset (ai): default 1.80 s / 84 MB; tuned JVM (`-XX:TieredStopAtLevel=1 -XX:+UseSerialGC -Xmx96m`) 1.20 s / 58 MB; **built-in Python v2 signer 0.03 s / 23 MB**. |
| E8 | 5 builds in a row (one-file change each): 4.1 s every time (no slowdown), `scaling_max_freq` unchanged (cpu0 1.92 GHz, cpu2 1.38 GHz: the CPU level, not heat), VrApi `Temp` 28 C. fps during builds: avg 72.5, min 70; before 72.5 / min 71; after 72.6 / min 69: **no measurable fps cost from builds on cores 0-2**. |
| E4 | adb-installed probe (built on the headset by `mcx-build`) in front for 8.5 min, MetaCodex behind. 10 s: main adj 700, `:ai` 500 (started service). **67-93 s: both at adj 900 = cached**; logcat `Stopping service due to app idle: u0a165 ... MetaCodexAiService` (~65 s after the switch). Not frozen (Horizon OS: `use_freezer=false`); MQH answered 19/19; builds through the daemon still ran (2.4 s at 38, 148, 498 s). Cached = lmkd's first victims: R3 confirmed. |
| E5 | MetaCodex in front, headset forced asleep (`input keyevent KEYCODE_SLEEP`, `mWakefulness=Asleep`, USB connected): a detached loop in the ai context ran **139 builds in 240 s, CPU suspended 0.0 s** (CLOCK_BOOTTIME - CLOCK_MONOTONIC). Builds were faster asleep (1.2-1.5 s vs 2.0-2.1 s awake: Unity stops rendering). Off-face without `prox_close` the headset did not sleep by itself in 3 min (`mProximityPositive=false`, no sleep timeout). Without USB the CPU may suspend: the build wake lock covers it (needs the APK). |
| **R9** (new) | **Android kills `:ai` for its children's CPU while MetaCodex is in the background**: `Killing 8993:com.pnncl.metacodex:ai (adj 700): Caused by child process: excessive cpu 78510 during 300014 dur=31032 limit=25` one minute after E5 (78.5 s CPU in 5 min > 25 %). MQH, QVAC and the builder died; the service restarted empty and MQH stayed down until Unity ran again. Foreground services are exempt (ActivityManager checks only background process states): the `:ai` foreground service is required. Without the APK: `mcx-buildd` pauses (SIGSTOP) a job while `:ai`'s `oom_score_adj` >= 600 and the builder's CPU over the last 5 min would pass 15 %. |
| **GPU memory** (MetaCodex, not the builder) | after ~6 pause/resume cycles in 45 min (probe apps in front, sleep/wake) **the Unity process held 5.4 GB PSS, `GL mtrack` 4.87 GB** (limit 5.75 GiB on Quest 3S), MemAvailable 326 MB, and lmkd killed the restarted `:ai` twice (`oom_reaper ... cl.metacodex:ai`). Every `mcx-app launch` pauses MetaCodex, so this decides how many app tries a session survives: per-switch numbers below. |
| E6 | Runner probe (targetSdk 32, adb-installed): `dlopen` of a module received over 127.0.0.1 and written to its files dir works, no denial; first load 22 ms, reload 2.4 ms with the OpenXR session kept; forged and replayed modules refused. |
| PCH | clang on the headset spends most of a compile in headers (preprocess alone 1.18 s, 71k lines). A shared precompiled header (12.6 MB, 3.2 s to make once per toolchain and -O level): syntax check 2.75 -> 0.68 s, -O2 compile 3.67 -> ~1.2 s. Extra `-D` flags are allowed; the `-O` level must match (`__OPTIMIZE__`), so there is one PCH for check (-O0) and one for builds (-O2). |

### Decisions

- **Executor**: `mcx-buildd`, a daemon in MQH's process tree (`mqh-run` starts it), so on cores 0-2 under `:ai`. `mcx-app`
  builds in its own shell only when that shell may use cores 0-2 (agents under MQH); from `/immersive` it hands the job
  over a Unix socket (`/tmp/mcx-buildd/sock`, dir 0700: other Android apps cannot reach it, unlike a TCP port). One job
  at a time (FIFO, 4 waiting at most), nice 10, cancelled when the client goes away. Evidence: E8 fps, the job-7 test.
- **Packaging**: aapt2 only when the manifest, `res/` or `assets/` changed; the APK is written by `mcx-build` itself
  (stored entries 4-aligned, `.so` 16 KiB, libraries deflated once per content): no zipalign, no python zipfile append.
- **Signing**: built-in APK Signature Scheme v2 (RSA PKCS#1 v1.5 SHA-256, min SDK 32 needs no v1) with the same key:
  openssl reads the keystore once into `~/.android/metacodex-debug.keystore.signcache.pem` (0600). apksigner stays the
  fallback (`--signer apksigner`, or no openssl). 60x faster than the JVM.
- **Compile**: per-file objects with clang depfiles; the NDK glue object and the PCHs are built once per toolchain into
  `~/.cache/mcx-build` (shared by all apps; `mcx-buildd` warms it at start, 6.5 s CPU at nice 19). The PCH holds only
  headers whose meaning no app macro changes; a source defining `XR_EXTENSION_PROTOTYPES`, `GL_*`, `_GNU_SOURCE`, ... is
  compiled without it (and `PCH=0` in `build.conf` turns it off).
- **Guards**: memory floor (wait while MemAvailable < 900 MB, kill a tool under 450 MB: a build needs ~100 MB and the idle
  floor is ~2 GB); heat guard only through `MCX_BUILD_MAX_C` (thermal zones are unreadable in the app's domain; VrApi
  reports 28 C and the CPU caps did not move in E8, so it stays off by default).

## Feature matrix: Quest SDK features in apps built here (native XR layer `mcx_xr.h`)

Layer: `native/xr/mcx_xr.{h,cpp}` (shipped to `/opt/metacodex/quest-native/xr`), compiled once per toolchain by
`mcx-build` into `~/.cache/mcx-build/libmcxxr-*.a` (6.1 s on the headset, once) and linked as an archive (apps that do
not use it carry nothing). Khronos OpenXR 1.1.65 headers only (Meta's extensions are registered there): no Meta SDK
headers, no new third-party code (the text font is rasterized from DejaVu Sans Mono, which MetaCodex already ships).
Worked example: `native/examples/nightvision` (`/opt/metacodex/quest-native/examples/nightvision`).

Evidence levels: **compiles** (PC, NDK r27.1) < **builds on headset** (`mcx-build` on the Quest 3S, `mcx-build --verify`
ok: manifest entries, 0 unresolved symbols, APK signed) < **offered on headset** (the runtime offered the extension to a
probe app with these manifest entries: Runner log) < **ran on headset** (observed working at runtime).

| Feature | API (in `mcx_xr`) | Manifest / permission (Meta's native docs) | Status |
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

### Night Vision (the demo)

`native/examples/nightvision`: environment depth -> per-eye full-screen pass that reprojects the depth map through the
depth camera's view-projection, green phosphor brightness from distance, edges from depth discontinuities, grain,
scanlines; three modes (PHOSPHOR, EDGES, RANGE); passthrough underneath, turned green by a mono -> RGBA colour map,
toggled; right pinch / trigger = mode, left pinch / A = passthrough, stick = gain, left grip = drop an anchor; HUD panel
(text) following the head lazily; scene query and anchor count in the HUD; foveation 2, CPU/GPU sustained high.

| Step on the Quest 3S | Result |
|---|---|
| build, cold (XR layer once + PCH + app), adb context pinned to cores 0-2 | 11.2 s (XR layer 6.1 s once), 710 KB |
| build, one file changed | 1.5 s |
| `mcx-app verify` on the headset | ok: 6 permissions (INTERNET, HAND_TRACKING, USE_ANCHOR_API, USE_SCENE, OPENXR, OPENXR_SYSTEM), features PASSTHROUGH + handtracking + headtracking, 226 imports / 0 unresolved, 21 extensions asked for, 42 extension functions, devices quest3\|quest3s |
| `apksigner verify --min-sdk-version 32` (on the headset) | ok |
| adb install as `com.metacodex.probe.nightvision`, `pm grant ... USE_SCENE` | installed; USE_SCENE (runtime) granted, HAND_TRACKING / USE_ANCHOR_API install-time granted |
| launch | **blocked**: the headset was on the sensor lock screen (after a forced sleep for E5); launched apps do not start there and the lock is not to be bypassed. Runtime proof (session, passthrough, depth frames, screenshot) is in the owner's checklist |

## Back and report: the way from a built app to MetaCodex (owner 2026-11-11)

**Approach (chosen, with the evidence):**
- Every app on `mcx::App` (the template now too) has a built-in app menu on the left controller's menu button and the
  non-dominant hand's menu gesture. Meta's design table: "Open/Close app menu: Menu button / System gesture left
  hand"; the native hand-tracking page: the non-dominant hand's system gesture sets MenuPressed for one frame.
- **Back** = JNI `startActivity` of the caller's launch intent (`NEW_TASK | REORDER_TO_FRONT`) **while the app is still
  in front** (Android lets a foreground app start activities; a background start is refused), then `xrRequestExitSession`.
  MetaCodex's activity is `singleTask`: an existing instance resumes (`onNewIntent`; Unity's `UnityPlayerActivity`
  calls `setIntent`, read in its source), nothing reloads.
- **Report** travels in the same intent: a PNG of the app's left-eye image (`glReadPixels`, box-downscaled until under
  ~450 KB: the Binder limit is ~1 MB; zlib from the NDK, PNG written in mcx_xr: no new third-party code), the last
  60 log lines, label and version. No socket and no file drop: nothing else on the device can reach it.
- **Authentication**: MetaCodex gives each launch a random 128-bit id (`system launch <pkg> [thread <id>]`, kept a day
  in `launches.json`), only in that app's launch intent; a return counts only with an id MetaCodex gave out for that
  package, and on Android 14+ `getLaunchedFromPackage()` must match when the app shares its identity
  (`ActivityOptions.setShareIdentityEnabled`, set by mcx_xr). Other apps cannot put pictures or text into a thread.
- In MetaCodex (`ShellReturn.cs`): Back -> toast "Back from <app>" + the launching thread (`MQH_THREAD_ID` of the agent
  that ran `mcx-app launch`, else the open / last used thread) in front. Report -> the picture into CLIPBOARD and
  both files attached to that thread, composer draft "Feedback on <app> <version>:", a hint to speak (◉) and send.
  The microphone is not started by itself.
- **Quit from the system menu**: mcx_xr tries the same start on EXITING (best effort; Android may refuse a start from
  an app leaving the foreground): owner checklist section 30.
- **After a crash** MetaCodex cannot come forward by itself: `SYSTEM_ALERT_WINDOW` (the background-start exemption) is on
  Meta's prohibited-permissions list. The user opens MetaCodex from the dock.

**Measured (Quest 3S, 2026-11-11 afternoon, headset free, MetaCodex 0.7.14 = without the receiving side):**
- The new mcx_xr (menu, return, report) builds on the headset: template 12.4 s cold (XR layer 9.9 s once), Night
  Vision 3.6 s; `verify ok` for both; libz.so is the only new system library.
- **MetaCodex's memory, after the owner's ~1 h session with Night Vision tests: 4.35 GB PSS before any switch of
  mine.** Three probe switches later `oom_reaper` killed MetaCodex, the probe, the Library and Guidebook, and the
  headset's sensors service aborted (`CameraFrame_t00`, SIGABRT); a Guardian dialog then blocked every immersive start,
  so the per-switch GPU numbers and the Back/Report timings could not be taken (they need the owner once to clear
  the dialog). Scripts ready: `tools/gpu-switch-test.sh <pkg> 10` (GL / EGL mtrack and PSS per switch),
  `tools/return-test.sh <pkg> [report] [cold]` (back -> MetaCodex resumed -> first frame).
- Static look at the GPU growth: MRUK's `PassthroughCameraAccess` keeps its texture across pause/resume (Stop on pause,
  Play on resume, `_texture` reused), so the Unity-side blur code does not re-allocate per switch; the native camera
  restart and the XR swapchains are the open suspects. No fix without the per-switch numbers (measured, not guessed).

### The ANR after Back (0.7.15, the owner's "Passthrough" app) and the fix

- **Evidence** (`~/mcxwork/native-build/evidence/passthrough-anr-153901.log`): 15:37:49.790 the app starts MetaCodex;
  49.795 `wm_on_top_resumed_lost_called` for the app, but no `wm_on_pause_called` after it; 50.299 `Activity pause
  timeout`; 54.836 `ANR ... Input dispatching timed out ... Waited 5001ms for FocusEvent(hasFocus=false)`; no
  `wm_finish_activity`.
- **Root cause**: Back started MetaCodex and asked the session to exit, but the frame loop went on and called
  `xrWaitFrame` again. With the display handed to MetaCodex the runtime gave the app no frame, so `xrWaitFrame` did
  not return; the app thread stopped pumping its looper; NativeActivity's `onPause` (which in native_app_glue waits
  for the app thread to take APP_CMD_PAUSE) blocked the main thread; the focus event stayed unconsumed: ANR, and the
  session never reached EXITING, so the activity never finished.
- **Fix (mcx_xr)**: Back / Report / Quit / Restart = start MetaCodex (Back, Report), then `xrRequestExitSession` +
  `ANativeActivity_finish` at once and a leaving state: no `xrWaitFrame` / `xrBeginFrame` from then on, the looper
  pumped every 16 ms, out of the loop when Android destroys the activity or after 3 s; no frame while the activity is
  paused (dock, Home), so a pause never meets a blocked `xrWaitFrame`; `Shutdown` ends a stopping session and keeps
  pumping until the activity is destroyed.
- **Launch source** (owner): the menu offers Back / Report only when MetaCodex launched the app (CALLER + a launch
  id); from the Library, or reopened after a pause (NativeActivity does not pass a new intent to native code; Back
  always closes the app, so MetaCodex's launches are fresh starts), only the app's items + Quit. The title says which.
- **Rule** (owner): the skills' "Always" (keep the menu; Back closes the app; no own exit or OpenXR loop) and
  `mcx-app verify` fails apps not on `mcx::App` or with an own exit / OpenXR path (tested: the old template's
  main.cpp gives both problems; the new template, Night Vision, the clock: verify ok).
- **Shipped without an APK**: the fixed layer, mcx-build and the five skills were laid into the headset's rootfs
  (`/opt/metacodex/quest-native/xr`, `/usr/local/bin/mcx-build`, `/root/.claude/skills`, MQH's builtin-skills and
  bb-guide copies); the XR layer's cache key is its source hash, so the next build of any app recompiles it. An app
  gets the fix when it is rebuilt and installed again.

## Launches by request and project types (owner 2026-11-11)

- **Launch only on the user's tap, in every approval mode**: `system launch` (and `install-apk` for a package that is
  not installed yet) is a request in MetaCodex (`ShellReturn.RequestLaunch`): a dialog APP READY with LAUNCH / INSTALL
  and LATER, a toast kept in MESSAGES, voice commands `launch_pending_app` / `decline_pending_app` (EN + IT, enabled
  only while one waits); `mcx-app launch|install|run` return at once with `RESULT: requested ...`, `mcx-app status`
  reads pending / launched / declined / installing / failed. An allowed first install turns into a LAUNCH offer when
  Android is done. Silent updates of installed apps still go ahead. Not done: an update while the app runs is not
  asked (an app cannot see other apps' processes on Android 14), the orb has no Launch button of its own (the dialog,
  MESSAGES and voice cover it), no chip in the thread.
- **Project types**: MQH hands its built-in and data-dir skills to every thread as global skills
  (`apps/server/src/services/skills/injected-skills.ts`: builtin + data-dir; the host daemon stages them as
  `runtime/global-skills`), and `/root/.claude/skills` is global for Claude Code, so the quest-native skills left both:
  they live in `native/skills` -> `/opt/metacodex/quest-native/skills`. `mcx-project set <dir> native` writes
  `.metacodex/project.json`, links the five skills into the project's `.claude/skills` (Claude Code's project skills)
  and puts a managed block into AGENTS.md (Codex, OpenCode) and CLAUDE.md (Claude Code) that points at them and states
  the rules; `general` removes all of it and keeps the rest of those files. `mcx-app new` marks app folders native;
  + PROJECT… asks NATIVE APP / DEFAULT / RESEARCH (2026-11-13: `general` is now `default`, still read; RESEARCH adds a
  research note instead of the native rules) and makes the project in `/root/metacodex-projects/<type>/`; THREADS
  groups the projects under their type. Existing projects: native when the folder is an app (AndroidManifest.xml +
  src/), else default; `mcx-project set` changes it. `mqh-run` removes the old
  global copies. metacodex-context (global) only says where native apps live.

## More decisions (evidence above)

- **Build mode / BUILDING panel: not built.** Builds on cores 0-2 cost no measurable fps (E8: avg 72.5 / min 70 vs
  72.5 / min 71) and ~100 MB (floor 1.87 GB), and a one-file build takes ~2 s: there is nothing to hide behind a panel.
  The real risks were background survival (R3, R9) and memory (the GPU-memory finding), fixed by the foreground service
  and the CPU budget. No new app action, so no command-registry change (AGENTS.md rules 1-3 not triggered).
- **R4 (headset off)**: the CPU did not suspend in E5 (USB connected); the build wake lock covers the no-USB case once
  the APK ships. The dangerous effect of "headset off" is R9 (MetaCodex goes to the background), not suspend.
- **Runner / JIT: parked** at a working state (owner's priority change, 11-11): edit -> running 2.2 s, signed channel.
  Classic apps on `mcx::App` are the default; the skill says so.
- **Licences**: no new third-party component ships in the MetaCodex APK (host.cpp, mcx_xr, the SHA-256/RSA code and the
  font atlas are ours; the atlas is rendered from DejaVu Sans Mono, already shipped and credited). Apps built on the
  headset contain the Khronos OpenXR loader and the NDK's native_app_glue (Apache-2.0) and static libc++ (Apache-2.0
  with LLVM exception): the "downloaded at first use" row of docs/LICENSES.md (branch chore/tools-list-and-licenses)
  covers that toolchain; nothing to add there.

## Done means: status

| Criterion | Status |
|---|---|
| template code -> check -> build on the headset from an agent context | **done** (ai context: check 0.7 s, build 1.9 s); install/launch through path B: owner-proven earlier, not re-run (owner's order) |
| 10 builds in a row, MetaCodex never killed | builds: 5 + 139 in a row fine; **MetaCodex's `:ai` was killed once by R9** -> foreground service (APK) + CPU budget (userland, tested) |
| MQH alive after 10 min with the built app in front | 8.5 min: alive (19/19) but cached; the foreground service makes it robust (APK) |
| timing table | done (Metrics) |
| off-face fps | done (72/72 with prox_close; builds cost nothing measurable) |
| a native MR app with Meta extensions builds on the headset | **done**: Night Vision, verify ok; runtime proof pending (lock screen) |

## Remaining risks

1. **MetaCodex GPU memory** across pause/resume (5.4 GB after ~6 switches): every app launch pauses MetaCodex. Not a
   builder issue; needs a look in Unity (render textures / passthrough-camera resources on resume) and the checklist
   measurement.
2. R9 until the APK with the foreground service ships: the CPU budget is the only guard (it slows long background builds).
3. Night Vision at runtime (passthrough + depth together, the HUD, input) is unproven on the device.
4. The signing key (R8): `mcx-build` adds `~/.android/metacodex-debug.keystore.signcache.pem` (private, 0600): keep both
   out of backups that leave the headset.

## Metrics (ai context unless noted; MetaCodex at 72 fps)

| Stage | Cold | Warm | Peak RSS | Cores | Notes |
|---|---|---|---|---|---|
| check (`mcx-app check`) | 2.9 s (no PCH yet) | **0.7 s** | 67 MB | 0-2 | old pipeline: none (a full build) |
| compile (1 file) | 4.35 s (glue + no PCH) | **1.7 s** (PCH) | 77-100 MB | 0-2 | old: 5.0 s every time |
| link | 0.09 s | 0.09 s (skipped if objects unchanged) | 30 MB | 0-2 | |
| package | 0.52 s (aapt2) | **0.06 s** (aapt2 skipped) | 40 MB | 0-2 | old: 1.04 s |
| sign | 0.03-0.06 s | 0.03 s | 23 MB | 0-2 | old (apksigner JVM): 2.54 s |
| **build total** | 2.4 s (app cold, cache warm); 5.1 s (all cold) | **1.9 s** (one file changed); 0.1 s (nothing changed) | 100 MB | 0-2 | **old: 9.04 s** |
| through the daemon from `/immersive` | | +0.35-0.5 s (client start + socket) | | 0-2 | job 7 |
| install (path B) | owner-proven ~2 s update | | | | not re-measured (owner's order) |
| launch | | | | | |
| Runner reload (`mcx-app dev`, parked) | 2.6 s | **2.2 s** edit -> running (compile 1.15 + link 0.10 + deliver + load 2.4 ms) | 71 MB | 0-2 | adb context |
| Night Vision build (XR layer) | 11.2 s (layer once 6.1 s) | 1.5 s | 129 MB | 0-2 (taskset) | adb context |
