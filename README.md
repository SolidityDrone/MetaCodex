# MetaCodex

A standalone agent workstation for Meta Quest 3 / 3S. A real Linux userland (Debian 13, arm64) runs
inside the app; agents work in it; Unity shows it all as spatial panels you read, poke and talk to.
Three parts, in build order: the **terminal** (Linux in VR), **local AI** (QVAC, native on Horizon OS in its own process: [docs/qvac.md](docs/qvac.md)), and
**MQH**, the Meta Quest Harness (threads, plugins, skills, providers), with Unity as its client.

Plan and decisions: `/home/drone/bs/BELVEDERE_PLAN.md` (v2). Harness: [docs/mqh.md](docs/mqh.md).
Spatial UI, gestures, plugins, agents and threads: [docs/ui.md](docs/ui.md). Secrets (Android Keystore vault,
Linux daemon): [docs/secrets.md](docs/secrets.md). What to test on the headset: [docs/TEST_CHECKLIST.md](docs/TEST_CHECKLIST.md).

What the app has today: terminals (xterm-compatible, multi-terminal frames, sign-in link detection, paste);
panels for files, git (metro history), web (Horizon browser, local servers), Lua scripts, a gesture studio
(poses, Lua detectors, no-code bindings, recording, plugins in `/mnt/quest/plugins`), agents (provider
sign-in through each CLI's own login), threads (timeline, send, approvals, models, checkout or worktree),
a secret vault, settings (gestures, layout fixed / float carousel, look themes, sounds, MQH daemon); hand
tracking (ray, pinch, poke, fist, select tool); agent skills for all of it bundled into the userland.

## Repository layout (monorepo)

| Path | What | Language / tools |
|---|---|---|
| `unity/` | the Quest app: Unity 6000.4.5f1 project (URP, OpenXR, Meta XR 205, Android ARM64, IL2CPP, Vulkan), package `com.pnncl.metacodex` | C#, Unity |
| `mqh/` | Meta Quest Harness: trimmed fork of bb, rebranded, runs in the userland; its guide plugin holds MetaCodex's agent skills | TypeScript, Node 24 |
| `userland/` | the Linux side the APK ships: proot + Debian rootfs fetchers, the overlay (our files laid over the rootfs: `mcx-setup`, `mcx-ui`, `mcx-secret`, Core v0, agent skills), bundled third-party skills (`agent-skills/`), headset test without an APK | shell, Python |
| `native/` | `pty/`: fork + pseudo-terminal library the app calls through P/Invoke | C (NDK r27c) |
| `tests/` | `terminal/`: the Unity-free C# core (emulator, extractor) against real programs, tmux as reference | C#, .NET 8 (Unity's) |
| `tools/` | dev tools: compile check, guarded batch Editor, device test, control-port client, Editor pty bridge | shell, Python |
| `docs/` | design docs | |

Generated artefacts land inside `unity/Assets` (`Plugins/Android/libs/arm64-v8a/*.so`,
`StreamingAssets/linux/{rootfs,overlay}.*`); the 30 MB rootfs is not in git, rebuild it with
`userland/fetch-rootfs.sh`.

## How the terminal works

```
Unity app: TerminalView (TMP rows + cell mesh) <- VtTerminal (xterm emulator) <- pty bytes
           TerminalInput (BT keyboard, system keyboard) + KeyBar (fingertip poke) -> pty bytes
           ControlServer 127.0.0.1:47800 (token) <- agents/tests (tools/qterm)
  libmcxpty.so (fork + pty) -> libproot.so (exec'd from nativeLibraryDir) -> Debian rootfs -> bash
```

Android 10+ forbids exec() of files an app writes (W^X): proot, its loader and libtalloc ship as
`lib*.so`, unpacked by Android into `nativeLibraryDir` (Unity 6 sets `useLegacyPackaging true`); the guest
binaries in the rootfs are only mmap'ed by the loader.

## Commands (WSL, repo root)

```bash
export PATH="/home/drone/projects/unity-skill-set/bin:$PATH"      # adb = the Windows adb shim
userland/fetch-proot.sh && userland/fetch-rootfs.sh && native/pty/build.sh && python3 -I userland/build-overlay.py
tests/terminal/run-core-tests.sh    # ~10 s, no Editor: emulator vs tmux + extractor (ALL OK)
tools/cscheck.sh                    # ~10 s, no Editor: compile check, Editor and device defines
tools/unity-batch.sh BatchSetup     # headless: configure, scene, render self-test (unity/Bench/selftest/terminal.png)
tools/unity-batch.sh BatchCheck 45  # headless: scene + terminal self-test + UI self-test (unity/Bench/selftest/ui-*.png)
tools/unity-batch.sh BatchBuild 75  # APK -> unity/Builds/MetaCodex.apk (only when asked)
tools/mqh-build.sh && tools/mqh-dev.sh start   # MQH in WSL, isolated from a real bb (ports 38990/38991)
tools/sync-agent-skills.sh          # refresh the bundled Unity + Meta agent skills (userland/agent-skills)
tools/agent-plugin-test.sh          # a fresh agent writes a gesture plugin from the skills (replayed by the UI self-test)
userland/g0-shell.sh                # headset on adb: proot + Debian + Node + agents + QVAC, no APK
tools/device-test.sh                # install the APK and drive the in-app terminal over adb
(cd userland/overlay && node --test opt/metacodex/core/test/core.test.ts)   # Core v0 tests
```

## Credits

Every third-party part (what ships in the APK, what the app and `mcx-setup` download, what is only in the repo), its
licence, its source and what the licence asks of us: [docs/LICENSES.md](docs/LICENSES.md) (also on the headset in
`/usr/share/doc/metacodex/`). In short: PRoot (Termux build, GPL-2.0), talloc (LGPL-3.0), libandroid-shmem (BSD-3)
ship as separate binaries; Debian 13 (its own licences); Unity (Unity Terms, Unity Companion License) and Meta XR SDK
205 (Oculus SDK License); MoonSharp (BSD-3-Clause); QVAC and Bare (Apache-2.0); fonts Inter, Rajdhani, Noto,
Liberation Sans (OFL) and DejaVu (Bitstream Vera licence); models Whisper base (MIT), Qwen3 0.6B (Apache-2.0),
multilingual-e5-small (MIT), Supertonic 3 (OpenRAIL-M, with use restrictions). MQH is based on bb (MIT, Copyright 2026
Michael Yong). Bundled agent skills: Unity Technologies (Unity Companion License) and Meta (Apache-2.0), see
`userland/agent-skills/SOURCES.md`. The app shows no third-party logos.
