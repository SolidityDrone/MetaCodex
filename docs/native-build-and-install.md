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

## Facts that shape the design (LEARNINGS 2026-11-07, docs/qvac.md)

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
| Runner reload | | | | | |
