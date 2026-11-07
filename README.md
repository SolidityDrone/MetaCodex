# MetaCodex

A standalone agent workstation for Meta Quest 3 / 3S. A real Linux userland (Debian 13, arm64) runs
inside the app; agents work in it; Unity shows it all as spatial panels you read, poke and talk to.
Three parts, in build order: the **terminal** (Linux in VR), **local AI** (QVAC, native on Horizon OS in its own process: [docs/qvac.md](docs/qvac.md)), and
**MQH**, the Meta Quest Harness (threads, plugins, skills, providers), with Unity as its client.

Plan and decisions: `/home/drone/bs/BELVEDERE_PLAN.md` (v2). Harness: [docs/mqh.md](docs/mqh.md).

## Repository layout (monorepo)

| Path | What | Language / tools |
|---|---|---|
| `unity/` | the Quest app: Unity 6000.4.5f1 project (URP, OpenXR, Meta XR 205, Android ARM64, IL2CPP, Vulkan), package `com.pnncl.metacodex` | C#, Unity |
| `mqh/` | Meta Quest Harness: trimmed fork of bb, rebranded, runs in the userland (import pending) | TypeScript, Node 24 |
| `userland/` | the Linux side the APK ships: proot + Debian rootfs fetchers, the overlay (our files laid over the rootfs: `mcx-setup`, Core v0), headset test without an APK | shell, Python |
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
tools/unity-batch.sh BatchBuild 75  # APK -> unity/Builds/MetaCodex.apk (only when asked)
userland/g0-shell.sh                # headset on adb: proot + Debian + Node + agents + QVAC, no APK
tools/device-test.sh                # install the APK and drive the in-app terminal over adb
(cd userland/overlay && node --test opt/metacodex/core/test/core.test.ts)   # Core v0 tests
```

## Credits

PRoot (Termux build, GPL-2.0), talloc (LGPL-3.0), libandroid-shmem (BSD-3) ship as separate binaries,
see `unity/Assets/Plugins/Android/libs/arm64-v8a/THIRD_PARTY_PROOT.md`. Debian (its own licences). Fonts:
DejaVu (Bitstream Vera licence), Noto (OFL). MQH is based on bb (MIT, Copyright 2026 Michael Yong).
