# Ready on install (branch feat/ready-on-install)

Owner 2026-11-13: "a judge must not get upset at startup: otherwise he downloads, spends minutes on setup and just
quits". The APK carries what the app needs to work at once, offline; what needs a choice or an account stays on
request.

## What is in the APK, what stays on request

| In the APK | Size | Before |
|---|---|---|
| The Linux userland ready for MQH: Debian trixie-slim + Node.js 24.22.3 + the base Debian packages installed (ca-certificates curl git xz-utils python3 procps less nano and their dependencies: 54 packages; git 2.47.3, Python 3.13.5, curl 8.14.1); Node's C headers left out (67 MB, only for building native Node add-ons: node-gyp downloads them) | rootfs.tar.gz 118 MB (was 28 MB) | Node and the packages downloaded by `mcx-setup` at first use |
| The four core models of the local AI service: Whisper base, Qwen3 0.6B, multilingual-e5-small, Supertonic 3 | 649 MB | downloaded with VOICE > INSTALL MODELS |
| The voice decision model's vectors of every command example (1809 texts, the same e5 model) | 2.8 MB | computed at the first start on the AI cores (14 s on the PC's 8 cores, far longer on the headset), meanwhile only exact phrases matched |
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

Also at the first start: every Linux process gets the headset's time zone (`TZ`, from Android's default zone; the zone
files are in the image), so git commit times and an agent's "today" are local, not UTC.

## Building it

```
userland/fetch-rootfs.sh                          # once: the official Debian layer (copy it to ~/mcxwork/readyinstall/base-trixie-slim.tar.gz + .json)
tools/userland-image.sh                           # the ready userland -> StreamingAssets/linux/rootfs.tar.gz + rootfs.json
tools/local-ai-models-pack.sh                     # the core models -> StreamingAssets/qvac/models + models.json (--clean: a lighter dev APK)
tools/local-ai-dev.sh pc start && ~/mcxwork/tools/uirun.sh voicedecide && tools/local-ai-dev.sh pc stop
                                                  # the decision vectors -> StreamingAssets/qvac/decider-vectors.bin (again when commands change)
tools/mqh-build.sh all && tools/mqh-bundle.sh     # when mqh/ changed
python3 -I userland/build-overlay.py              # when userland/overlay/ changed
tools/unity-batch.sh BatchBuild
```

`tools/userland-image.sh`: Node is a plain archive (pinned version and sha256); the PC's apt works out which arm64
packages the base layer lacks (the layer's own dpkg status), from Debian's index signed with the keyring in the layer,
and each .deb is checked against that index's sha256; dpkg installs them inside the image under proot 5.5.0 + qemu-aarch64
user mode (both fetched into the work folder, nothing installed on the PC), in passes as apt does
(`mcx-install-debs`: a package whose Pre-Depends are not configured yet waits for the next pass), then node, npm, git,
python3 and curl run in it. When emulation fails (or `MCX_IMAGE_EMULATE=0`) the .deb files stay in the image and
`mcx-setup` installs them on the headset with the same `mcx-install-debs`, offline. About 6 minutes on this PC; memory
stayed above 6 GB available.

## Size

The APK with the pack: 994,868,919 bytes (949 MiB): the models 649 MB (stored as they are), the ready userland 138 MB
(deflated in the APK), the rest as before (about 205 MB). Gradle updates its APK in place and a replaced entry leaves a
hole: BuildApk deletes Gradle's previous APK first (a build after an image change was 1.14 GB with the old rootfs still in
it). `tools/local-ai-models-pack.sh --clean` gives a light APK for development.

## Install for judges

`tools/release/make-release.sh` -> `release/MetaCodex-<version>/` (the APK, the install scripts, INSTALL.md, SHA256SUMS)
and its zip.


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
