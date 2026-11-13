# Ready on install (branch feat/ready-on-install)

Owner 2026-11-13: "a judge must not get upset at startup: otherwise he downloads, spends minutes on setup and just
quits". The APK carries what the app needs to work at once, offline; what needs a choice or an account stays on
request.

## What is in the APK, what stays on request

| In the APK | Size | Before |
|---|---|---|
| The Linux userland ready for MQH: Debian trixie-slim + Node.js 24 (unpacked) + the base Debian packages as .deb files (ca-certificates curl git xz-utils python3 procps less nano and their dependencies, 54 files) | rootfs.tar.gz 116 MB (was 28 MB) | Node and the packages downloaded by `mcx-setup` at first use |
| The four core models of the local AI service: Whisper base, Qwen3 0.6B, multilingual-e5-small, Supertonic 3 | 649 MB | downloaded with VOICE > INSTALL MODELS |
| MQH, the local AI runtime, the overlay | as before | as before |

On request, as before: the agents (AGENTS > GET: OpenCode, Codex, Claude Code; Claude Code is never bundled), the
native app toolchain (`mcx-setup quest`: APK tools, JDK, NDK, android.jar, OpenXR, 400 MB; the first time a native app
is made, `mcx-app doctor` says so, or SYSTEM > TOOLS), the local coder (AGENTS > LOCAL CODER, 1 GB).

## First start

1. The splash; every runtime permission asked once, together (microphone, spatial data, headset camera): Horizon OS
   shows its dialogs one after another. A refused one is asked again by its feature, as before.
2. The userland is unpacked from the APK (LinuxHost, only where none is installed: an installed one is never
   replaced). MQH starts at once on the shipped Node.
3. The local AI service copies the packed models into its models folder with their `.sha256` markers (QvacHost
   `SeedModels`): no download. Then it starts.
4. 20 s after start, first use (`ShellSystem`): the base is complete -> nothing; shipped packages waiting -> `mcx-setup`
   installs them with dpkg from `/var/cache/metacodex/base-debs`, offline, in the background, no toast; an older
   userland without them -> `mcx-setup` downloads as before. The native toolchain is no longer installed by itself.

## Building it

```
userland/fetch-rootfs.sh                          # once: the official Debian layer (copy it to ~/mcxwork/readyinstall/base-trixie-slim.tar.gz + .json)
tools/userland-image.sh                           # the ready userland -> StreamingAssets/linux/rootfs.tar.gz + rootfs.json
tools/local-ai-models-pack.sh                     # the core models -> StreamingAssets/qvac/models + models.json (--clean: a lighter dev APK)
tools/mqh-build.sh all && tools/mqh-bundle.sh     # when mqh/ changed
python3 -I userland/build-overlay.py              # when userland/overlay/ changed
tools/unity-batch.sh BatchBuild
```

`tools/userland-image.sh` runs nothing arm64 on the PC (owner: no emulation): Node is a plain archive (pinned version
and sha256); the PC's apt works out which arm64 packages the base layer lacks (the layer's own dpkg status), from
Debian's index signed with the keyring in the layer, and each .deb is checked against that index's sha256. dpkg installs
them on the headset.

## Install for judges

`tools/release/install-metacodex.sh` (or `.bat` on Windows) next to the APK: `adb install -r -g` (grants the runtime
permissions at install), `pm grant` for each as a fallback, `appops set REQUEST_INSTALL_PACKAGES allow` (the apps it
builds). No prompt at the first start then.

## Measured

`system bench-first-run` on the control port (result: `system bench-first-run-result`, also
`/mnt/quest/first-run-bench.txt`): the first start measured on a headset that already has MetaCodex, without touching
its userland or models: the rootfs copied out of the APK, unpacked and set up in a scratch folder, its packages installed
there with dpkg, node / git / python3 run; every model copied out of the APK; all deleted after.

| Step | Quest 3S |
|---|---|
| rootfs out of the APK | not measured yet |
| unpacked and set up | not measured yet |
| shipped packages (dpkg, offline) | not measured yet |
| models out of the APK | not measured yet |
