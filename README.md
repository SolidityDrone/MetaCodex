# MetaCodex

A standalone agent workstation for Meta Quest 3 / 3S: a real Linux userland (Debian 13, arm64) runs
inside the app, and Unity shows it as a terminal you can read and type into in VR. Next come an
on-device local-AI tier (QVAC) and a bb-style control plane (daemon in the userland, Unity as the
spatial client). Plan and decisions: `/home/drone/bs/BELVEDERE_PLAN.md` (v2). Contest rules:
`/home/drone/bs/META_HACKATON_RULES.md`.

Unity 6000.4.5f1, URP, OpenXR + Meta XR Core/Interaction 205, Android ARM64, IL2CPP, Vulkan,
package `com.pnncl.metacodex`. Driven headless from WSL with the unity-skill-set
(`/home/drone/projects/unity-skill-set`; read its `skills/quest-dev/SKILL.md` first).

## How the terminal works

```
Unity app (foreground, immersive)
  TerminalView (TMP rows + cell mesh)  <-  VtTerminal (xterm emulator, pure C#)  <-  pty bytes
  TerminalInput (BT keyboard, system keyboard)  +  KeyBar (poke with a fingertip)  ->  pty bytes
  ControlServer 127.0.0.1:47800 (token)  ->  agents/tests drive and read the screen (Tools/qterm)
       |
  libmcxpty.so (fork + pty, P/Invoke)  ->  libproot.so (exec'd from nativeLibraryDir)
       ->  Debian rootfs in files/linux/rootfs (bash, apt, node, the agents...)
```

Android 10+ forbids exec() of files an app writes (W^X). proot, its loader and libtalloc ship as
`lib*.so`, which the package manager unpacks into `nativeLibraryDir` (exec allowed, Unity 6 sets
`useLegacyPackaging true`); the guest binaries in the rootfs are only mmap'ed by the loader.

## Layout

| Path | What |
|---|---|
| `Assets/MetaCodex/Runtime/Terminal` | `VtTerminal` (emulator), `Wcwidth`, `KeyEncoder`, `TerminalView` (renderer) |
| `Assets/MetaCodex/Runtime/Linux` | `LinuxHost` (install + launch), `TarGzExtractor`, `RootfsSetup`, `PtyNative`, sessions |
| `Assets/MetaCodex/Runtime/App` | `MetaCodexApp` (bootstrap), `TerminalInput`, `KeyBar` + `HandTips`, `ControlServer`, `SessionLog` |
| `Assets/MetaCodex/Editor/MetaCodexSetup.cs` | headless configure / scene / self-test / build (`Batch*` methods) |
| `Assets/Plugins/Android/libs/arm64-v8a` | `libproot.so`, `libproot-loader.so`, `libtalloc.so`, `libandroid-shmem.so` (Termux build, GPL/LGPL/BSD, see THIRD_PARTY_PROOT.md), `libmcxpty.so` (ours) |
| `Assets/StreamingAssets/linux` | `rootfs.tar.gz` (Debian trixie-slim arm64, generated, not in git) + `rootfs.json` |
| `Native/pty` | `mcx_pty.c`, `pty_test.c`, `build.sh` (NDK r27c) |
| `Linux` | `fetch-proot.sh`, `fetch-rootfs.sh`, `g0-shell.sh` (headset test without an APK) |
| `Tests` | `run-core-tests.sh` (emulator vs tmux on real programs, extractor), corpus |
| `Tools` | `cscheck.sh` (compile check, no Editor), `unity-batch.sh` (guarded one-shot Editor), `qterm`, `ptyserver.py` |

## Commands (WSL, from the project root)

```bash
export PATH="/home/drone/projects/unity-skill-set/bin:$PATH"   # adb = Windows adb shim
Linux/fetch-proot.sh && Linux/fetch-rootfs.sh && Native/pty/build.sh   # regenerate binaries
Tests/run-core-tests.sh        # ~10 s, no Editor: emulator + extractor (must say ALL OK)
Tools/cscheck.sh               # ~10 s, no Editor: all Unity scripts, Editor and device defines
Tools/unity-batch.sh BatchSetup     # first open: configure, TMP essentials, scene, self-test (needs ~4.5 GB free)
Tools/unity-batch.sh BatchSelfTest  # renders the terminal headless -> Bench/selftest/terminal.png + report.txt
Tools/unity-batch.sh BatchBuild 60  # APK -> Builds/MetaCodex.apk (only when asked)
Linux/g0-shell.sh              # headset on adb: proot + Debian + apt + node + agents, no APK
adb install -r -g Builds/MetaCodex.apk && Tools/qterm status && Tools/qterm run 'uname -a'
adb pull /sdcard/Android/data/com.pnncl.metacodex/files/session_log.txt
```

In the Editor (Play Mode) there is no Android: run `python3 -I Tools/ptyserver.py` in WSL and the
terminal connects to a real bash on this PC instead (else it falls back to echo).
