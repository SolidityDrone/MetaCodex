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

### The SETUP panel, the locks, the tour (branch feat/first-run-tour)

Owner 2026-11-14: "while the app settles the installation, a small guided wizard ... meanwhile show a progress bar on a
panel that explains: models copied, AI services starting, Linux unpacking and install from the APK, MQH harness
started, voice decider ready ... It must be clear it is a ONE-TIME setup for the first ever start."

When: only on the very first start: on the headset, no Linux installed before (`files/linux/installed.json` missing)
and the setting `firstStartDone` off. Any other start (an update, a new Linux install later, the Editor) sets the flag
and shows nothing. The flag is set when the panel shows the end.

**The panel** "FIRST START · ONE-TIME SETUP" (`ShellFirstRun.cs`): a window placed where the user looks, after the
splash. It says once that this happens once, takes about a minute, downloads nothing and keeps everything on the
headset. It shows one step at a time: its number ("STEP 3 OF 5 · ONE-TIME SETUP"), its name while it runs, one line on
what it does, its own bar (empty at each new step) and a spinner that keeps turning. The steps shown before stay
listed with ✓ and their time ("✓ Linux ready · 9.4 s"). A step already done long ago (1 and 2 usually end while 3
runs) still gets its turn: its bar fills in about 0.4 s, at least 0.6 s on show.

| # | While it runs | Done | Progress from | Typical (Quest 3S, first-run log) |
|---|---|---|---|---|
| 1 | Unpacking the AI models | AI models in place | QvacHost: the runtime unpack (first half), `ModelsProgress` (bytes) | runtime 6 s + models 0.9 s |
| 2 | Starting the local AI service | Local AI running | QvacHost `Running`; by time (90 % at 3 s, never more) | 2.2 s |
| 3 | Building your Linux workstation | Linux ready | `LinuxHost.Progress` (copy, unpack, overlay, MQH bundle) | 11.6 s |
| 4 | Starting MQH, the agent harness | MQH running | MqhHost `Running`; by time (4.5 s) | 4.1 s |
| 5 | Teaching the voice your commands | Voice commands ready | `VoiceDecider.EmbedDone / EmbedTotal`; by time without a count | 31 s after MQH |

Lines under the names: 1 "Speech, language and voice models (about 650 MB) come out of the app onto this headset: no
download."; 2 "Speech-to-text, the language model and the voice run here on the headset, on cores of their own."; 3
"Debian with Node.js, git and Python, unpacked from the app: no download."; 4 "The harness that runs your coding
agents, their threads and their tools, inside your Linux."; 5 "About 1,900 ways to say the app's commands become
examples TALK can match, worked out here on the headset."

A failed step stays on show 4 s with what failed and what still works (no endless spinner); a step that needs it is
skipped (2 needs 1, 4 needs 3, 5 needs 2); the retry paths stay as they were (VOICE > START, MQH > STATUS > RESTART).
The decision model is called failed 4 min after the AI service is up. At the end: "Setup done in N s" and "Ready now:
TALK, THREADS, AGENTS, TERMINALS and FILES. This window closes by itself." (6 s later; with a failed step, what still
works, a CLOSE button, 20 s). Control port: `ui setup` lists the steps. Log lines: `setup: step N A -> B (s)`,
`setup: all steps over in N s`, `setup: done in N s`.

**The locks** (`ShellSetupLocks.cs`): until its step is over, a feature is dimmed with 🔒 and a press gives one short
callout by the orb, nothing else: "🔒 TALK is ready after step 5 of 5 · one-time setup". TALK and every way to listen
(dock, wrist menu, orb menu, gesture bindings `voice:*`, Lua `voice.listen`, control port `voice listen`, dictation ◉)
wait for step 5; THREADS, THREAD, NEW THREAD, the thread sidebar, WORKSPACES, CONTROL > AGENTS and its API KEY for step
4; TERMINALS (window, + NEW, a hidden terminal, voice "new terminal"), FILES and GIT for step 3. Each lock lifts by
itself when its step is over (done, failed or skipped). Settings (CONTROL), WEB, CLIP, FOCUS, SELECT, KEYS, CENTER and
the tour stay usable. The voice registry is not changed meanwhile: the checks sit in OpenPanel, OpenControl,
VoiceListen, TerminalNew and ShowTerminal, where every way in passes.

**The tour** (`ShellTour.cs`): starts 1.5 s after the panel, about a minute, five cards beside their targets, a ring of
light round the target (a DOCK tile, the left wrist, the orb) or the target window's frame lit. Each card waits until
the user did it, says ✓, then the next: 1 open the main menu (the wrist menu, or CLIP on the DOCK), 2 open the orb's
menu, 3 move a window by its top bar (the SETUP window), 4 open CONTROL, 5 what comes next (what unlocks by itself,
TALK, THREADS, AGENTS > GET with the user's own key or sign-in). SKIP STEP and SKIP TOUR on every card, FINISH on the
last. Again from CONTROL > ABOUT > TOUR, the SETUP panel's TOUR, the action `tour` and the voice command `start_tour`.

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
