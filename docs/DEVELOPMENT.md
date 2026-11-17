# MetaCodex: development notes

This file holds the developer details that the [README](../README.md) leaves out.
It names the tools, products and folders by their real names.
Where a design document and the code differ, the code wins.

## Contents

- [Names in this repository](#names-in-this-repository)
- [Build stack](#build-stack)
- [Folder map](#folder-map)
- [Commands](#commands)
- [Device test workflow](#device-test-workflow)
- [Release build and debuggable build](#release-build-and-debuggable-build)
- [Test suites](#test-suites)
- [Voice bench](#voice-bench)
- [Local AI service](#local-ai-service)
- [MQH](#mqh)
- [How the terminal works](#how-the-terminal-works)
- [Machine guards](#machine-guards)
- [Design documents](#design-documents)
- [Third-party parts](#third-party-parts)

## Names in this repository

The README uses neutral terms. This table maps them to the real names.

| README term | Real name |
|---|---|
| Standalone mixed-reality headset | Meta Quest 3 / 3S. Every measurement used a Quest 3S. |
| Headset OS | Horizon OS (Android-based) |
| The 3D engine | Unity 6000.4.5f1 (IL2CPP, URP) |
| The headset maker's SDK | Meta XR SDK 205.0.0 (core, interaction, MR Utility Kit) |
| Camera passthrough, depth API | Passthrough, Environment Depth API, `USE_SCENE` permission |
| MQH | Meta Quest Harness. A trimmed fork of bb, an open-source agent IDE backend (MIT). |
| Local AI runtime, local AI service | QVAC (JavaScript runtime Bare) |
| Agent CLIs | Claude Code, Codex, OpenCode |
| Text-to-speech model | Supertonic 3 (OpenRAIL-M, with use restrictions) |
| A Lua interpreter written in C# | MoonSharp |
| A community Android build of proot | The Termux build of proot |
| The shared folder | `Application.persistentDataPath`, mounted at `/mnt/quest` in the Linux |
| The Whisp | The orb of the code: `UiOrb`, `ShellOrb`, the shader `MetaCodex/Orb`, the settings key `orb` |
| A public skills site, a code host, a model host | skills.sh, GitHub, Hugging Face |
| A terminal multiplexer | tmux |

## Build stack

| Part | Version |
|---|---|
| Editor | Unity 6000.4.5f1 (the value in `unity/ProjectSettings/ProjectVersion.txt`) |
| Render pipeline | URP 17.4.0 |
| OpenXR | OpenXR plugin 1.18.0, vendor OpenXR plugin 2.6.1 |
| Headset SDK | Meta XR SDK 205.0.0 (core, interaction, MR Utility Kit) |
| Scripting | IL2CPP, ARM64 only, Vulkan, multithreaded rendering on |
| Android | package `com.pnncl.metacodex`, versionName 0.7.19, versionCode 26, minSdk 32, targetSdk 34 |
| Native pty library | C, Android NDK r27c (`native/pty/build.sh`) |
| MQH | Node.js 24, TypeScript, SQLite |
| Tests | .NET 8 (the version that the engine ships) |

Unity 6 sets `useLegacyPackaging true`, so Android unpacks the `.so` files into `nativeLibraryDir`.
Android 10 and later forbids `exec()` of files an app writes. Only files in `nativeLibraryDir` may run.

## Folder map

| Path | What | Language |
|---|---|---|
| `unity/` | The headset app. Runtime code in `Assets/MetaCodex/Runtime` (`AI/ App/ Hands/ Linux/ Scripting/ Security/ Shell/ Terminal/ UI/`); the example app plugins in `Runtime/Scripting/Resources/PluginExamples`. Editor tools and self-tests in `Assets/MetaCodex/Editor`. Java plugins in `Assets/Plugins/Android`. | C#, Java |
| `mqh/` | `apps/{server,host-daemon,cli}`, `packages/*`, `plugins/*` (including the `metacodex-guide` skills). | TypeScript, Node.js 24 |
| `local-ai/` | The local AI service: `server/{main,engines,models,audio}.js`, `package.json`. | JavaScript (Bare) |
| `userland/` | The proot and Debian fetchers, the overlay (`overlay/usr/local/bin/mcx-*`, `overlay/opt/metacodex/*`), `agent-skills/` (bundled third-party skills), `mqh/mqh-run`, `g0-shell.sh`. | shell, Python |
| `native/` | `pty/`, `xr/` (`mcx_xr`), `hello-native/` (template), `examples/nightvision`, `runner/`, `module-template/`, `skills/` (13 native-app skills), `project/AGENTS.md`. | C, C++ |
| `tests/` | `terminal/`, `sidebar/`, `voicefx/`, `plugins/`. | C# (.NET 8), shell |
| `tools/` | Compile check, batch Editor, device test, `qterm`, MQH build and bundle, `local-ai-bundle.sh`, `local-ai-dev.sh`, `local-ai-models-pack.sh`, `userland-image.sh`, `release/`, `voicebench/`, agent tests, `check-native-skills.py`, `comments-only-check.py`, `return-test.sh`, `gpu-switch-test.sh`. | shell, Python |
| `docs/` | Design documents, licence inventory, diagrams. | Markdown |

The build writes generated parts into `unity/Assets`:
`Plugins/Android/libs/arm64-v8a/*.so`, `StreamingAssets/linux/{rootfs,overlay,mqh}.*`, `StreamingAssets/qvac/runtime.*`,
`StreamingAssets/qvac/models/` with `models.json`, and `StreamingAssets/qvac/decider-vectors.bin`.
The Linux archive and the models are not in git: `userland/fetch-rootfs.sh` and `tools/userland-image.sh` make the
first, `tools/local-ai-models-pack.sh` packs the second ([ready-on-install.md](ready-on-install.md#building-it)).

## Commands

Run all commands from the repository root, in a Linux shell (WSL on the development machine).

```bash
export PATH="<skill set>/bin:$PATH"     # on the development machine: adb is a shim to the Windows adb
userland/fetch-proot.sh && userland/fetch-rootfs.sh && native/pty/build.sh && python3 -I userland/build-overlay.py
tools/userland-image.sh             # the ready Linux of the APK (Debian layer + Node.js + base packages)
tools/local-ai-models-pack.sh       # the four core models into the APK (--clean: a light development APK)
tests/terminal/run-core-tests.sh    # ~10 s, no Editor: emulator vs tmux + extractor (ALL OK)
tools/cscheck.sh                    # ~10 s, no Editor: compile check, Editor and device defines
tools/unity-batch.sh BatchSetup     # headless: configure, scene, render self-test (unity/Bench/selftest/terminal.png)
tools/unity-batch.sh BatchCheck 45  # headless: scene + terminal self-test + UI self-test (unity/Bench/selftest/ui-*.png)
tools/unity-batch.sh BatchBuild 75  # APK -> unity/Builds/MetaCodex.apk (only when asked)
tools/release/make-release.sh       # release/MetaCodex-<version>/: the APK, install scripts, INSTALL.md, SHA256SUMS
tests/plugins/run-plugin-tests.sh --examples   # app plugins on the real script host with a fake app, no Editor
python3 tools/comments-only-check.py <git-ref> # a C# change touched only comments and whitespace
tools/mqh-build.sh && tools/mqh-dev.sh start   # MQH in WSL, isolated from a real bb (ports 38990/38991)
tools/sync-agent-skills.sh          # refresh the bundled Unity + Meta agent skills (userland/agent-skills)
tools/agent-plugin-test.sh          # a fresh agent writes a gesture plugin from the skills (replayed by the UI self-test)
userland/g0-shell.sh                # headset on adb: proot + Debian + Node + agents + QVAC, no APK
tools/device-test.sh                # install the APK and drive the in-app terminal over adb
(cd userland/overlay && node --test opt/metacodex/core/test/core.test.ts)   # Core v0 tests
```

Generated parts before a build:

- `tools/local-ai-bundle.sh` packs the local AI service.
- `tools/local-ai-models-pack.sh` packs the four core models.
- `tools/userland-image.sh` makes the ready Linux.
- The decision vectors: `tools/local-ai-dev.sh pc start && MCX_UITEST_ONLY=voicedecide tools/unity-batch.sh BatchUiTest`, again when the voice commands change.
- `tools/mqh-build.sh && tools/mqh-bundle.sh` pack MQH (server, host daemon, CLI and plugins, without the Claude Code provider).
- `userland/build-overlay.py` builds the overlay.

Inside the headset, in a terminal:
`mcx-app doctor|new|check|build|verify|install|launch|run|status|dev|runner|logs|info`, `mcx-setup [quest|ndk|agents|qvac|all]`,
`mcx-ui ...` (for example `mcx-ui plugin example timers`, `mcx-ui plugin check <id>`), `mcx-secret ...`, `mcx-ai ...`,
`mcx-project type|set`, `mqh ...`.

## Device test workflow

- Use Wi-Fi adb: `adb tcpip 5555`, then `adb connect <ip>:5555`.
- Forward the control port: `adb forward tcp:47800 tcp:47800`, then use `tools/qterm`.
- The app writes `files/session_log.txt`.
- Dev AI service: `tools/local-ai-dev.sh device start` (on the headset, over adb) or `tools/local-ai-dev.sh pc start` (in WSL).
- Return-path tests: `tools/return-test.sh <pkg> [report] [cold]` and `tools/gpu-switch-test.sh <pkg> 10`.
- App plugins on the headset: `tests/plugins/device-test.sh [timers|calendar|weather]`.
- The checklist is [TEST_CHECKLIST.md](TEST_CHECKLIST.md), by area.

## Release build and debuggable build

- `tools/unity-batch.sh BatchBuild` makes a development (debuggable) APK. Anyone with `adb` can `run-as` the app and use its Keystore key.
- `MCX_RELEASE=1 tools/unity-batch.sh BatchBuild` makes a release APK: not debuggable, with optimised C++ (IL2CPP Release; a development APK keeps the Debug configuration). Use it before you store real keys on a shared device, and for the release folder: `tools/release/make-release.sh` packs whatever `unity/Builds/MetaCodex.apk` is.
- The manifest declares `REQUEST_INSTALL_PACKAGES`. A store build must not declare it.

## Test suites

| Suite | Command | Count |
|---|---|---|
| Terminal core (emulator vs tmux on 7 programs) | `tests/terminal/run-core-tests.sh` | 62 core checks |
| Side bar | `tests/sidebar/run-sidebar-tests.sh` | not counted here |
| Voice effect | `tests/voicefx/run-voicefx-tests.sh` | |
| App plugins (real script host, fake app) | `tests/plugins/run-plugin-tests.sh --examples` | load, tiles, every button, 3 h of clock, voice lint, restart, unload |
| MQH type check and tests | in `mqh/` | |
| `concurrency-limit` plugin | in `mqh/` | 35 |
| UI self-test (headless Editor) | `tools/unity-batch.sh BatchUiTest` | 52 sections, needs 3 GB free |
| Native-app skills | `python3 tools/check-native-skills.py` | every command, flag, path and code block |
| Comment-only changes | `python3 tools/comments-only-check.py <git-ref>` | every changed `.cs` file |

Run one UI section: `MCX_UITEST_ONLY=voice tools/unity-batch.sh BatchUiTest`.
Sections include `decider`, `context`, `chat`, `sidebar`, `workspaces`, `threadwindows`, `archive`, `firstrun`, `control` and `syskeyboard`; `voiceexport` and `voicedecide` run only when named.

## Voice bench

- `python3 tools/voicebench/make_eval.py` makes the 180-sentence English set.
- Export the registry from the Editor: `echo voiceexport > unity/Bench/uitest-only && tools/unity-batch.sh BatchUiTest 20`.
- Start the PC AI service: `tools/local-ai-dev.sh pc start`. Then run `python3 tools/voicebench/llm_run.py`.
- Compare routers: `tools/voicebench/bench.py`, in its own Python venv ([tools/voicebench/README.md](../tools/voicebench/README.md)).
- Score the app run: `tools/voicebench/score_app.py`.
- Results: `tools/voicebench/results/` (the latest: `2026-11-12-english-only.md`).
- The vectors of every command example ship in the APK as `qvac/decider-vectors.bin`; the app keeps its own cache in `voice-decider-vectors.bin`. The router comparison is in [voice.md](voice.md#the-decision-model).

## Local AI service

- The service runs the QVAC JavaScript runtime (`libbare.so server/main.js`) as a child of `MetaCodexAiService`.
- Command line: `taskset 7 nice -n 10 libbare.so server/main.js --data files/qvac/data --token-file files/qvac/token --port 47810 --threads 2`.
- Packaging: `tools/local-ai-bundle.sh` packs five addon families for android-arm64 (CPU only) into `StreamingAssets/qvac/runtime.tar.gz` (25,015,017 bytes; 62,791,511 unpacked).
- Versions: service 0.1.0; `asr-ggml` 0.12.0; `tts-ggml` 0.15.0; `llm-llamacpp` 0.68.0 with `fabric` 0.26.0; `embed-llamacpp` 0.55.0; registry client 0.8.0. All Apache-2.0.
- The addons need the NDK r29 `libc++_shared.so`. An r27 one fails `dlopen`.
- Engine settings: speech `use_gpu false`, Whisper `audio_ctx` 512 minimum; LLM `device cpu`, `ctx_size 2048`, `temp 0`, `reasoning_budget 0`, `predict 256`; embeddings `device cpu`, batch 512, ctx 512; voice `useGPU false`, 5 diffusion steps.
- Models: `ggml-base-q8_0.bin` (81,768,585 bytes), `Qwen3-0.6B-Q4_0.gguf` (382,156,480), `multilingual-e5-small-q8_0.gguf` (132,439,008), `supertonic3-q4_0.gguf` (84,658,368). Each is pinned by revision and SHA-256 in `local-ai/server/models.js`.
- API: `GET /health`, `GET /status`, `POST /v1/models/install`, `/v1/embeddings`, `/v1/audio/transcriptions`, `/v1/audio/speech`, `/v1/chat/completions`, `/v1/warmup`, `/config`, `/unload`, `/pause`, `/resume`, `/shutdown`, `GET /log`.
- From the Linux: `mcx-ai status|install|transcribe f.wav|say "text"|chat "prompt"|env|log`.
- The four core models ship in the APK ([ready-on-install.md](ready-on-install.md)). VOICE > INSTALL MODELS downloads them into a development APK built without them.
- A GPU test with the app closed: the LLM made 13.8 tok/s on the GPU (OpenCL) against 37 on 2 CPU threads (57 on 6). Whisper base: 328 to 346 ms on the GPU against 430 to 450 ms on the CPU. The NPU is closed to apps. The service stays on the CPU.

## MQH

- Forked from bb at upstream commit `8e4208e` (2026-11-06). The user-facing names changed (`mqh`, `~/.mqh`, `MQH_*`, ports 38990/38991, `mqh.db`). Internal package names keep the upstream prefix.
- Removed: web app, desktop, mobile, cloud/connect, tunnels, the web UI stack, account/telemetry/browser-style plugins.
- `mqh-run` starts the server, waits up to 120 s for `/health`, runs `mcx-buildd ensure`, enrolls the local daemon once (key in `~/.mqh/auth.json`), then starts the daemon.
- `tools/mqh-bundle.sh` packs 12.4 MB (70 MB unpacked, 7,481 files) into `StreamingAssets/linux/mqh.tar.gz`. The app lays it at `/opt/mqh`.
- The API token is the vault entry `mqh.api-token`. `mqh-run` copies it to `~/.mqh/api-token` (umask 077) for the `mqh` CLI.
- The provider plugin `provider-claude-code` is not in the bundle. It holds a vendor SDK that the project never ships.
- Plugin folders (15): `ask-user-question, automations, concurrency-limit, custom-instructions, environment-git-worktree, environment-personal-workspace, environment-project-checkout, git, github-pr-status, metacodex-guide, provider-acp, provider-claude-code, provider-codex, provider-retry, workflows`. `github-pr-status` is source only (the CLI compiles against its contract): it is not registered or bundled. Why each plugin stays or went: `docs/mqh.md`, "Plugins".
- Agent CLIs: Codex (provider `codex`), OpenCode (provider `acp-opencode` through ACP) and Claude Code (provider `acp-claude-code` through ACP, once the user has installed its ACP adapter `claude-agent-acp`; the app adds it to provider-acp's `customAgents` setting, `docs/mqh.md` "Claude Code through ACP") work in MQH threads. Claude Code without the adapter runs in a terminal (AGENTS: TERMINAL, ENABLE THREADS). Cursor, Pi and other agents are switched off.
- The app polls MQH over HTTP: the open thread every 0.8 s while the agent works (2.5 s idle), thread metadata every 2 s or 3 s, other threads every 5 s or 15 s.
- Per-project native-app skills: `mcx-project set <dir> native` links the 13 `quest-native-*` skills into `<project>/.claude/skills`. It writes a managed block into `AGENTS.md` and `CLAUDE.md` between `<!-- metacodex:native-app -->` markers. `research` writes a research note between `<!-- metacodex:research -->` markers (notes in Markdown, sources cited, no app build); `default` (old name `general`) removes both. New projects live in `/root/metacodex-projects/{native,default,research}/<name>`, each a git repository.

## How the terminal works

```text
App: TerminalView (TMP rows + cell mesh) <- VtTerminal (xterm emulator) <- pty bytes
     TerminalInput (BT keyboard, system keyboard) + KeyBar (fingertip poke) -> pty bytes
     ControlServer 127.0.0.1:47800 (token) <- agents/tests (tools/qterm)
  libmcxpty.so (fork + pty) -> libproot.so (exec'd from nativeLibraryDir) -> Debian rootfs -> bash
```

- `libmcxpty.so` exports `mcx_pty_spawn`, `read`, `write`, `resize`, `wait`, `kill`, `close`, `mcx_symlink`, `mcx_chmod`, `mcx_lstat_type`, `mcx_ids` and `mcx_errno_text`. The app calls it with `[DllImport("mcxpty")]`, not JNI.
- The pty reader polls every 250 ms with a 16 KB buffer. The writer uses a `BlockingCollection`.
- proot command line: `--kill-on-exit --link2symlink -0 -r <rootfs>`, with binds for `/dev`, `/proc`, `/sys`, `<rootfs>/tmp:/dev/shm` and the shared folder at `/mnt/quest`.
- `--link2symlink` fakes a hard link as a symlink to a hidden `.l2s.<name>` file that holds the absolute host path. Start proot with the path `/data/data/<pkg>/...`, not `/data/user/0/...`.
- The editor-only classes `TcpTerminalSession` (a WSL pty server) and `EchoSession` exist for tests.
- Debian's `/etc/profile` resets root's PATH. A drop-in at `/etc/profile.d/metacodex.sh` adds `~/.local/bin` again.
- The official layer: `debian:trixie-slim`, linux/arm64, layer digest `sha256:acd43e9b...` (the field `base` of `unity/Assets/StreamingAssets/linux/rootfs.json`).

## Machine guards

- Never run the Editor below 3000 MB free memory. The default is 4500. The guard stops it below 700 MB.
- Build an APK only when the owner asks. Run long commands in the background.
- Use `nice -n 10` for heavy work in an immersive terminal.
- `mcx-build` waits while memory is below 900 MB (`MCX_BUILD_MIN_MB`) and kills a tool below 450 MB (`MCX_BUILD_KILL_MB`). It exits with code 4.
- `mcx-buildd` pauses a job (SIGSTOP) while `:ai` has `oom_score_adj >= 600` and the builder would pass 15 % CPU over 300 s. Android kills `:ai` when its children pass 25 %.
- `MCX_BUILD_MAX_C` sets a heat guard. It is off by default because the thermal zones are not readable in the app domain.

## Design documents

| Document | Content |
|---|---|
| [ready-on-install.md](ready-on-install.md) | What the APK carries, the first start, the release folder. |
| [perf.md](perf.md) | Frame rate: rendering options, the first start, main thread, depth, UI shader. |
| [ui.md](ui.md) | Spatial UI, the Whisp, windows, hands, gestures, layouts, Ambient Focus. |
| [control-center.md](control-center.md) | CONTROL and the main menu. |
| [scripting.md](scripting.md) | Lua scripts, gesture plugins and app plugins: the API, limits, tests. |
| [thread-view.md](thread-view.md), [thread-sidebar.md](thread-sidebar.md), [workspaces.md](workspaces.md) | Thread windows and groups, the side bar, workspaces. |
| [voice.md](voice.md) | The voice path, the registry, the decision model, spoken replies. |
| [local-ai.md](local-ai.md) | The local AI runtime and why it runs in its own process. |
| [local-coder.md](local-coder.md) | The local coding model. |
| [session-context.md](session-context.md) | The session context (CSCP). |
| [mqh.md](mqh.md) | The agent harness. |
| [mqh-spatial.md](mqh-spatial.md) | The `spatial.json` format of plugin panels (served, not rendered). |
| [secrets.md](secrets.md) | The vault, policies and the threat model. |
| [native-build-and-install.md](native-build-and-install.md) | The native app pipeline, background behaviour and guards. |
| [TEST_CHECKLIST.md](TEST_CHECKLIST.md) | The headset checklist, by area. |
| [LICENSES.md](LICENSES.md) | Licence inventory. |
| [diagrams/README.md](diagrams/README.md) | The diagram kit that draws the README images. |

## Third-party parts

[LICENSES.md](LICENSES.md) is the full inventory. In short:

- proot (Termux build, GPL-2.0-or-later), talloc (LGPL-3.0-or-later) and libandroid-shmem (BSD-3-Clause) ship as separate binaries.
- Debian 13 keeps its own licences.
- Unity (Unity Terms, Unity Companion License) and Meta XR SDK 205 (Oculus SDK License).
- MoonSharp (BSD-3-Clause). QVAC and Bare (Apache-2.0).
- Open fonts: Inter, Rajdhani, Noto, Liberation Sans (OFL) and DejaVu (Bitstream Vera licence).
- Models: Whisper base (MIT), Qwen3 0.6B (Apache-2.0), multilingual-e5-small (MIT), Supertonic 3 (OpenRAIL-M, with use restrictions that the app must pass on to users).
- MQH is based on bb (MIT, Copyright 2026 Michael Yong). The notice is in `mqh/LICENSE` and the root `LICENSE`.
- Bundled agent skills: Unity Technologies (Unity Companion License) and Meta (Apache-2.0). See `userland/agent-skills/SOURCES.md`.
- The app shows no third-party logos.
