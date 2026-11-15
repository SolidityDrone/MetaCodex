# Ready on install: what the APK carries and the first start

The APK carries what the app needs to work at once and offline. The first start lays it out in a one-time setup and
downloads nothing. What needs a choice or an account stays on request.

## What the APK carries

| In the APK | Size | What it is |
|---|---|---|
| The ready Linux (`linux/rootfs.tar.gz`, described by `rootfs.json`) | 124,780,445 bytes, 10,594 entries | The official Debian 13 trixie-slim arm64 layer, Node.js 24.22.3 in `/usr/local`, and 54 Debian packages installed with dpkg: ca-certificates, curl, git, xz-utils, python3, procps, less, nano and their dependencies (git 2.47.3, Python 3.13.5, curl 8.14.1). Node's C headers are left out: they only build native Node add-ons, and node-gyp downloads them when one is built. |
| The four core models of the local AI service (`qvac/models/`, described by `models.json`) | 681,022,441 bytes (649 MiB) | Whisper base, Qwen3 0.6B, multilingual-e5-small and the text-to-speech model, each checked against the SHA-256 in `local-ai/server/models.js`. |
| The voice decision model's vectors of every command example (`qvac/decider-vectors.bin`) | 2.8 MB | Made with the same e5 model, so commands are decided by meaning from the first second. |
| MQH, the local AI runtime, the overlay | | As in every build. |

The whole APK is 994,868,919 bytes (949 MiB). `BuildApk` deletes Gradle's previous APK before a build: Gradle updates
an APK in place, and a replaced entry leaves its old bytes as a hole. `tools/local-ai-models-pack.sh --clean` gives a
light development APK without the models; VOICE > INSTALL MODELS then downloads them.

**On request:** the agent CLIs (CONTROL > AGENTS > GET, with the user's own key or sign-in); the native app toolchain
(`mcx-setup quest`: APK tools, JDK, NDK, android.jar, OpenXR; about 400 MB to download and 2.5 GB on disk; installed
when the first native app needs it, or from SYSTEM > TOOLS); the local coder (AGENTS > LOCAL CODER).

## The first start

1. The splash shows in the first frame. Every runtime permission (microphone, spatial data, headset camera) is asked
   once, together; the headset shows its dialogs one after another. A refused one is asked again by its feature.
   `tools/release/install-metacodex.sh` grants them at install time, so then nothing is asked.
2. Behind the splash, the shell is built in small pieces (see [perf.md](perf.md#first-start)).
3. The Linux starts at once: the app copies the archive out of the APK and unpacks it (`LinuxHost`). It installs a
   Linux only where none is installed; an installed one is never replaced. MQH then starts on the shipped Node.
4. The local AI service copies the packed models into its models folder with their `.sha256` markers
   (`QvacHost.SeedModels`). Then it starts.
5. 20 s after the start, the first-use check (`ShellSystem`) finds the base complete and does nothing. An older
   userland without the shipped packages gets them from `mcx-setup`.
6. Every Linux process gets the headset's time zone (`TZ`, from Android's default zone; the zone files are in the
   image), so git commit times and an agent's "today" are local.

### When the setup shows

Only on the very first start: on the headset, with no Linux installed before (`files/linux/installed.json` missing)
and the setting `firstStartDone` off. Any other start (an update, a new Linux install later, the Editor) sets the
flag and shows nothing. The flag is set when the card shows the end of the setup.

### The guide card and its setup strip (`ShellFirstRun.cs`, `ShellTour.cs`)

One compact card (about 0.40 x 0.24 m, panel id `tour`) is made behind the splash and opens 0.3 s after the splash is
gone. On a first start its top is the setup strip:

- a spinner while anything runs, the running step in plain words, and STEP k OF 5;
- one bar over the whole setup, with its percentage. Each step weighs by its measured time, the bar never goes back,
  and it shows 100 % only at the end;
- the line "One-time setup: only on this first start, nothing is downloaded".

| Step | Words while it runs | Progress from | Measured (test headset) |
|---|---|---|---|
| 1 | Unpacking the speech and language models | `QvacHost`: the runtime unpack, then the model bytes | 7.5 s |
| 2 | Starting the local AI on this headset | `QvacHost.Running` | 1.6 s |
| 3 | Unpacking your Linux workstation | `LinuxHost.Progress` (copy, unpack, overlay, MQH bundle) | 12.1 s (the unpack itself 9.6 s) |
| 4 | Starting MQH, the agent harness | `MqhHost.Running` (MQH's `/health` asked every 0.5 s) | 3.6 s |
| 5 | Teaching the voice your commands | `VoiceDecider.EmbedDone / EmbedTotal` | 11.3 s (1,916 vectors came from the APK; 75 new ones were embedded) |

- A failed step shows "⚠ <step> did not start" and what still works, for at least 4 s; a step that needs it is
  skipped (2 needs 1, 4 needs 3, 5 needs 2). The decision model counts as failed 4 minutes after the AI service is
  up, and any step still not over 5 minutes after the setup began is skipped. The retry paths are VOICE > START and
  MQH > STATUS > RESTART.
- The end: "✓ Setup done in N s: everything is unlocked" for 3.5 s (with a failed step: "Setup finished in N s ·
  <step> needs a look (CONTROL > SYSTEM)", 8 s). Then the strip fades and the tour slides up into its place.
- While the setup runs, the card has no ✕: SKIP TOUR ends the tour, and the card stays with the strip until the end.
- Control port: `ui setup` lists the steps with their times. Log lines: `setup: step N A -> B (s)`,
  `setup: all steps over in N s`, `setup: guide card shown`, `setup: done in N s · the guide card shows the end`,
  `setup: the strip folded away`.

### The locks (`ShellSetupLocks.cs`)

Until its step is over, a feature is dimmed with 🔒. A press gives one short callout by the Whisp and nothing else:
"🔒 TALK unlocks by itself in a few seconds (one-time setup, step 5 of 5)".

| Waits for | Features |
|---|---|
| Step 5 | TALK and every way to listen: the DOCK, the wrist menu, the Whisp's menu, gesture bindings `voice:*`, Lua `voice.listen`, the control port's `voice listen`, dictation ◉. |
| Step 4 | THREADS, every thread window and group, NEW THREAD, the thread side bar, WORKSPACES, CONTROL > AGENTS and its API KEY. |
| Step 3 | TERMINALS (the window, + NEW, a hidden terminal, the voice command "new terminal"), FILES and GIT. |

Each lock lifts by itself when its step is over (done, failed or skipped). CONTROL, WEB, CLIP, FOCUS, SELECT, KEYS,
CENTER and the tour stay usable. The voice registry does not change meanwhile: the checks sit in `OpenPanel`,
`OpenControl`, `VoiceListen`, `TerminalNew` and `ShowTerminal`, where every way in passes.

### The tour

The Whisp gives a tour of six beats (five when the Whisp is off). Each beat has one action, a hint while it waits, a
done line with a ping and progress dots (● done, ◉ now, ○ next). NEXT ▸, SKIP STEP, FINISH ✓ and SKIP TOUR are on the
card.

| Beat | The user | Done when | Target |
|---|---|---|---|
| HELLO | reads the greeting (on a first start: about 20 seconds of setup, only this once) | NEXT ▸, or after 12 s | none |
| THE MAIN MENU | taps CLIP on the DOCK, or opens the wrist menu | CLIP or the wrist menu is open | ring round CLIP or the left wrist |
| THE WHISP | touches the Whisp or points at it and pinches | the Whisp's menu is open | ring round the Whisp |
| WINDOWS | moves the card by its top bar (or with a closed hand) | the card is dropped after a move | the card's frame breathes |
| CONTROL | opens ⚙ CONTROL | CONTROL is open | ring round the CONTROL tile |
| YOUR TURN | presses TALK and says a command; the card shows what the Whisp heard | FINISH ✓ | ring round TALK (when it is unlocked) |

- The ring is 12 warm dots, 1.5 cm in front of the target, sized to it, breathing slowly.
- The card never covers its target, the DOCK, the Whisp or an open window. It checks again every 0.6 s and moves
  aside smoothly. Once the user moved it, it keeps that place unless it covers something. When the user turns away
  for 2 s, it comes back.
- Replay without the strip: CONTROL > ABOUT > TOUR, the action `tour`, or the voice command `start_tour`.

### Frame rate during the setup

The render scale is 1.4 while the setup runs and 1.6 after it, and on a first start the depth and the camera of
Ambient Focus start 1.5 s after the setup. [perf.md](perf.md#first-start) has the reasons and the measured GPU load.

## Measured

A clean install of the packed APK on the test headset, before the start was staged and before the guide card
replaced the two setup windows: the first app log line came 13 s after the launch, the shell was up at 18 s, and
all five steps were over at 34 s, 21.2 s after the shell was up. The step times are in the table above. The staged
start and the guide card have no headset run yet.

`system bench-first-run` on the control port measures the same work on a headset that already has MetaCodex,
without touching its Linux or its models: the archive copied out of the APK, unpacked and set up in a scratch
folder, node, git and python3 run, every model copied out of the APK; everything is deleted after. The result: `system bench-first-run-result`, also `/mnt/quest/first-run-bench.txt`.

## Building it

```
userland/fetch-rootfs.sh                          # the official Debian layer; userland-image.sh reads it from MCX_IMAGE_BASE
tools/userland-image.sh                           # the ready Linux -> StreamingAssets/linux/rootfs.tar.gz + rootfs.json
tools/local-ai-models-pack.sh                     # the core models -> StreamingAssets/qvac/models + models.json
tools/local-ai-dev.sh pc start && MCX_UITEST_ONLY=voicedecide tools/unity-batch.sh BatchUiTest && tools/local-ai-dev.sh pc stop
                                                  # the decision vectors -> StreamingAssets/qvac/decider-vectors.bin (again when commands change)
tools/mqh-build.sh all && tools/mqh-bundle.sh     # when mqh/ changed
python3 -I userland/build-overlay.py              # when userland/overlay/ changed
tools/unity-batch.sh BatchBuild
```

`tools/userland-image.sh` fetches Node as a plain archive (pinned version and SHA-256). The PC's apt works out which
arm64 packages the base layer lacks (from the layer's own dpkg status), from Debian's index signed with the keyring in
the layer, and each `.deb` is checked against that index's SHA-256. dpkg installs them inside the image under proot
5.5.0 and qemu-aarch64 user mode (both fetched into the work folder; nothing is installed on the PC), in passes as apt
does (`mcx-install-debs`: a package whose Pre-Depends are not configured yet waits for the next pass). Then node, npm,
git, python3 and curl run in the image. When the emulation fails (or `MCX_IMAGE_EMULATE=0`), the `.deb` files stay
in the image and `mcx-setup` installs them on the headset with the same `mcx-install-debs`, offline. A run takes
about 6 minutes on the development PC.

## Install for judges

- `MCX_RELEASE=1 tools/unity-batch.sh BatchBuild` makes the release APK: not debuggable, with optimised C++.
- `tools/release/make-release.sh` puts that APK, the install scripts, `INSTALL.md` and `SHA256SUMS` into
  `release/MetaCodex-<version>/`, and makes its zip.
- `tools/release/install-metacodex.sh` (or `.bat` on Windows), next to the APK: `adb install -r -g` (grants the
  runtime permissions at install), `pm grant` for each as a fallback, and `appops set REQUEST_INSTALL_PACKAGES allow`
  (for the apps it builds). The app then asks nothing at the first start.
