# MetaCodex: proof of licence

Every piece of third-party software, data, font and model MetaCodex has used, with its licence, where it comes from,
how it reaches the user, and what its licence asks of us. Written for the contest's rule (Official Rules §4: the
sponsor "may at any time request written proof of ownership or proper license or use of any material comprising an
Entry") and kept current by the rule in `AGENTS.md` (anything new goes in here in the same change).

**State: 2026-11-10.** Measured, not recalled: the contents of **APK 0.7.8** (versionCode 15, built 2026-11-09 22:37 from
commit `288c431`, 188,053,248 bytes) were listed file by file; licences were read from each package's own files (Unity
package cache, the APK's bundled `package.json` / `LICENSE` / `NOTICE` files, the Debian image's `copyright` files, the
fonts' name tables), from the npm registry for what is downloaded later, and from the model cards for the models.
Where something could not be verified it says so. Open duties are collected in [§9](#9-duties-and-open-gaps).

On the headset this file and the licence texts it points to are in `/usr/share/doc/metacodex/` (from the next build:
`userland/build-overlay.py` ships them); the standard licence texts (GPL-2, GPL-3, LGPL-2.1, LGPL-3, Apache-2.0,
MPL-2.0, OFL-1.1, CC-BY-4.0, BSL-1.0, Artistic, BSD) are in the Debian image at `/usr/share/common-licenses/`.

| § | What | Licences in it |
|---|---|---|
| 1 | Our own work | owned by the entrant (no licence file yet, see G7) |
| 2 | Unity engine and packages (in the APK) | Unity Terms of Service, Unity Companion License, Unity Package Distribution License, + notices (MIT, Apache-2.0, BSD, BSL-1.0, zlib, NCSA) |
| 3 | Meta XR SDK (in the APK) | Oculus SDK License Agreement (+ Khronos Apache-2.0 loader, folly Apache-2.0, double-conversion BSD-3, zstd BSD-3) |
| 4 | Other code in the APK | BSD-3 (MoonSharp, android-shmem), GPL-2.0+ (proot), LGPL-3.0+ (talloc), Apache-2.0 (Bare, QVAC), MIT, ISC |
| 5 | Linux image and overlay in the APK | Debian (78 packages, own copyright files), Unity Companion License and Apache-2.0 (bundled agent skills) |
| 6 | Fonts in the APK | SIL OFL 1.1, Bitstream Vera / Arev |
| 7 | Downloaded at first use (models, tools) | MIT, Apache-2.0, OpenRAIL-M (Supertonic 3), Android SDK License (android.jar), proprietary (Claude Code, installed by the user) |
| 8 | In the repository only | MIT (MQH, a fork of bb) and its 1,012 dependencies, our tools and tests |
| 9 | Duties and open gaps | what is done, what is still to do |

---

## 1. Our own work

- **Ownership.** The app (C#, shaders, the Java classes `MetaCodexVault`, `MetaCodexInstaller`, `MetaCodexAiService`,
  the native pty library `native/pty`), the userland scripts (`mcx`, `mcx-setup`, `mcx-ui`, `mcx-secret`, `mcx-ai`,
  `mcx-app`, `mcx-core`), Core v0 (`userland/overlay/opt/metacodex/core`), the AI service (`qvac/server`), the Quest
  native template (`native/hello-native`), MetaCodex's own agent skills (`mqh/plugins/bb-guide/skills/quest-*`,
  `metacodex-context`), the tests, tools and docs were written for MetaCodex by its owner, with AI coding assistants,
  from 2026-11-06 on; the git history (github.com/SolidityDrone/MetaCodex) is the record.
- **No third-party art, sound, 3D models or pictures.** Every shape, panel, glow and the line icons of the dock and
  wrist menu are drawn by code (`UiShapeMesh`, `UiIconAtlas`: strokes computed at first use, "no image files to ship or
  license"); every UI sound is synthesised at run time (`UiSounds`); the app has no image, audio or model files of its
  own. The look takes its mood from real-time-strategy command consoles in general; no game's assets, names or artwork
  are used.
- **The Unity project** was created from Unity's own URP template (`com.unity.template.urp-blank` 17.0.14, Unity
  Companion License); its settings files were then rewritten by `MetaCodexSetup`.
- **No logos.** Since 2026-11-10 the app shows no logo or brand mark of any third party (SYSTEM > TOOLS was a grid of
  tool logos until then, see [§8.3](#83-removed)). Software is named in plain text only where the name is the
  function (a tool's command, the agent CLI one signs in to).
- **Licence of our own code: none declared yet**, so all rights are reserved by default. One file says otherwise:
  `qvac/package.json` declares `"license": "Apache-2.0"` for the AI service (gap G7).

## 2. Unity engine and packages (in the APK)

| Component | Version | Licence | In the APK as |
|---|---|---|---|
| Unity engine runtime | 6000.4.5f1 | Unity Terms of Service and Unity Software Additional Terms (the owner's Unity subscription) | `libunity.so`, `libmain.so`, `libil2cpp.so` (IL2CPP runtime + all managed code), `lib_burst_generated.so`, `assets/bin/Data/*`, the player's Java classes in `classes*.dex` |
| Built-in modules (`com.unity.modules.*`, 37) | 1.0.0 | part of the engine | inside `libunity.so` |
| com.unity.render-pipelines.universal / .core / .universal-config, com.unity.shadergraph | 17.4.0 | Unity Companion License (UCL); notices: FXAA3_11 (NVIDIA FXAA licence), RadeonRays 4.1 (MIT), Bullet Physics (zlib), Sobol sampler (MIT) | shaders, C# in `libil2cpp.so` |
| com.unity.inputsystem | 1.19.0 | UCL | managed code |
| com.unity.ugui (includes TextMesh Pro 5.0.0) | 2.0.0 | UCL | managed code, TMP shaders, TMP Settings |
| com.unity.burst | 1.8.29 | UCL; notices: LLVM (Apache-2.0 with LLVM exceptions; legacy parts NCSA), Mono.Cecil (MIT), Smash and xxHash (BSD-2-Clause), musl (MIT), SLEEF (Boost Software License 1.0), gRPC for .NET (Apache-2.0), Google.Protobuf (BSD-3-Clause) | `lib_burst_generated.so` |
| com.unity.collections | 6.4.0 | UCL | managed code |
| com.unity.mathematics | 1.3.3 | UCL | managed code |
| com.unity.nuget.newtonsoft-json | 3.2.2 | UCL for the package; Newtonsoft.Json itself MIT (James Newton-King) | managed code |
| com.unity.ai.navigation | 2.0.12 | UCL | managed code |
| com.unity.xr.management | 4.7.0 | UCL | managed code |
| com.unity.xr.openxr | 1.18.0 | UCL; notices: OpenXR-SDK-Source (Apache-2.0, Khronos), Oculus OpenXR Mobile SDK (Oculus SDK License Agreement) | managed code, `libUnityOpenXR.so` |
| com.unity.xr.meta-openxr | 2.6.1 | UCL; notices: OpenXR-SDK-Source (Apache-2.0), Oculus OpenXR Mobile SDK and AudioPluginOculusSpatializer (Oculus SDK License Agreement) | managed code, `libUnityARFoundationMeta.so` |
| com.unity.xr.arfoundation | 6.6.0 | UCL | managed code |
| com.unity.xr.hands | 1.7.3 | Unity Package Distribution License; notices: OpenXR-SDK-Source (Apache-2.0), Oculus OpenXR Mobile SDK (Oculus SDK License Agreement) | managed code, `libUnityOpenXRHands.so` |
| com.unity.xr.compositionlayers | 2.4.0 | UCL | managed code |
| com.unity.xr.core-utils | 2.6.0 | UCL | managed code |
| com.unity.xr.legacyinputhelpers | 3.0.1 | UCL; notice: ArmModel / SwingArmModel / TransitionArmModel (Apache-2.0) | managed code |
| TextMesh Pro Essential Resources | 5.0.0 | UCL (shaders, settings); Liberation Sans font: SIL OFL 1.1 ([fonts](licenses/fonts.md)) | `assets/bin/Data` |

Editor-only (not in the APK; checked: none of their assemblies is in the build's metadata): com.unity.pipeline
0.8.0-exp.1 (Unity Terms of Service; notices: Roslyn and System.* packages, MIT), com.unity.test-framework 1.6.0 and
.performance 3.4.0, com.unity.ext.nunit 2.0.5, com.unity.ide.visualstudio 2.0.27, com.unity.editorcoroutines 1.1.0,
com.unity.nuget.mono-cecil 1.11.6, com.unity.searcher 4.9.4 (Unity Companion Package License v1.0).

**Duties.** The UCL lets us use and ship these packages in Unity-made apps; the third-party notices travel with the
packages and Unity publishes the runtime's own notices with each Unity release. Our notice file (this one) lists them.

## 3. Meta XR SDK (in the APK)

`com.meta.xr.sdk.core`, `com.meta.xr.sdk.interaction`, `com.meta.xr.sdk.interaction.ovr`, `com.meta.xr.mrutilitykit`,
all **205.0.0**: "Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved. Licensed under the Oculus SDK
License Agreement" (https://developers.meta.com/horizon/licenses/oculussdk/). The licence allows shipping the SDK's
redistributable parts inside apps for Meta's platforms.

| In the APK | From |
|---|---|
| `libOVRPlugin.so`, `libOVRMetricsTool.so`, `libloaderimpl.so`, and in `assets/lib/{arm64-v8a,armeabi-v7a}/`: `libxplat_perflogger_*` (9), `libxplat_folly_logging_logging.so`, `libdouble-conversion.so`, `libxplat_third-party_zstd__zstd.so` | `Plugins/AndroidOpenXR/OVRPlugin.aar` (core) |
| `libopenxr_loader.so` | the Khronos OpenXR loader (Apache-2.0), shipped by both `OVRPlugin.aar` and Unity's `openxr_loader.aar` |
| `LibRuntimeOptimizer.so` | `Plugins/Android/RuntimeOptimizer_Plugin.aar` (core) |
| `libXrApiLayer_METAX_operator.so` + `assets/openxr/1/api_layers/implicit.d/XrApiLayer_METAX_operator.json`, `libxplat_secure_lib_secure_string.so` | `Plugins/Android/XrApiLayer_METAX_operator_unity_android.aar` (core): an implicit OpenXR API layer, "agentic interface for OpenXR" |
| `libInteractionSdk.so` | `InteractionSdk.aar` (interaction) |
| `libISDKEngineTelemetry.so` | `com.meta.xr.sdk.interaction` |
| `libmrutilitykitshared.so` | `com.meta.xr.mrutilitykit` |
| managed code (Oculus.Interaction, Meta.XR.MRUtilityKit, OVR*) and Java classes | the four packages |
| `SimpleJSON` (MIT, Copyright (c) 2012-2017 Markus Göbel (Bunny83)) | `com.meta.xr.sdk.core/ThirdParty/` (the folder's `openvr_api.cs`, Valve's OpenVR bindings, is not in the build: no `Valve.VR` type in its metadata) |

Open-source parts inside Meta's binaries, under their own licences: Khronos OpenXR loader (Apache-2.0), folly
(Apache-2.0), double-conversion (BSD-3-Clause), zstd (dual-licensed BSD-3-Clause or GPL-2.0).

Not a licence matter, noted for honesty in privacy statements: the SDK's own telemetry and performance-logging
libraries (`libISDKEngineTelemetry.so`, the `perflogger` set) ship with every app built on it.

## 4. Other code in the APK

| Component | Version | Licence | Source | In the APK as | Duties |
|---|---|---|---|---|---|
| MoonSharp (Lua interpreter in C#) | 2.0.0.0 | BSD-3-Clause, Copyright (c) 2014-2016 Marco Mastropaolo; parts of the string library from KopiLua, Copyright (c) 2012 LoDC | NuGet `MoonSharp`, `lib/net40-client` | `Assets/Plugins/MoonSharp/MoonSharp.Interpreter.dll` → `libil2cpp.so` | reproduce the notice: `Assets/Plugins/MoonSharp/LICENSE.txt` (ships in `/usr/share/doc/metacodex/licenses/`) |
| PRoot, Termux fork | 5.1.107.102 | GPL-2.0-or-later | Termux package `proot_5.1.107.102_aarch64.deb` (sha256 `10a394b5…e030`), source github.com/termux/proot + termux-packages `packages/proot` | `libproot.so`, `libproot-loader.so` (renamed, DT_NEEDED and RUNPATH changed with patchelf, otherwise unmodified) | run as a separate program (no linking with our code); give the licence text (`/usr/share/common-licenses/GPL-2`) and the corresponding source or a written offer: **G2** |
| talloc | 2.5.0 | LGPL-3.0-or-later | Termux `libtalloc_2.5.0_aarch64.deb` (sha256 `556591f4…5a7`), upstream talloc.samba.org | `libtalloc.so` (proot's dependency, a separate shared library, replaceable) | licence texts (`LGPL-3`, `GPL-3` in `/usr/share/common-licenses/`), source or offer: **G2** |
| libandroid-shmem | 0.7 | BSD-3-Clause, Copyright (c) 2013 Sergii Pylypenko, (c) 2017 Fredrik Fornwall | Termux `libandroid-shmem_0.7_aarch64.deb` (sha256 `0da3a24d…ff6`) | `libandroid-shmem.so` | notice: [licenses/libandroid-shmem-BSD-3-Clause.txt](licenses/libandroid-shmem-BSD-3-Clause.txt) (copied from the package) |
| Bare (JavaScript runtime of the AI service) | 1.36.0 | Apache-2.0 (Holepunch); built on V8 (BSD-3-Clause) and libuv (MIT) | npm `bare-runtime-android-arm64@1.36.0` | `libbare.so` (the `bare` executable, stripped) | Apache-2.0: licence + notices; V8 and libuv notices: **G1** |
| QVAC runtime (local speech, voice, LLM, embeddings) | 15 `@qvac` packages (fabric 0.26.0, asr-ggml 0.12.0, tts-ggml 0.15.0, llm-llamacpp 0.68.0, embed-llamacpp 0.55.0, registry-client 0.8.0, …) and their dependencies | 136 npm packages: Apache-2.0 84, MIT 45, ISC 7 ([Appendix B](#appendix-b-qvac-runtime-packages-in-the-apk)); native parts: qvac-fabric (a llama.cpp + ggml fork, MIT), speech-cpp (a whisper.cpp fork, MIT), ggml-speech (MIT), qvac-lib-inference-addon-cpp (Apache-2.0), OpenCL ICD loader (Apache-2.0), libc++ (Apache-2.0 with LLVM exception) | npm, pinned in `tools/qvac-bundle.sh` | `assets/qvac/runtime.tar` (unpacked into the app's files) | each package's `LICENSE` and `NOTICE` files are inside the tar and on the device; see the note below |
| libc++_shared.so | NDK r29 | Apache-2.0 with LLVM exception | Android NDK r29 | inside `assets/qvac/runtime.tar` (`lib/`) | none for binaries (LLVM exception) |
| `libmcxpty.so` | ours | ours | `native/pty`, built with NDK r27c | `lib/arm64-v8a/` | — |

**Note on QVAC's NOTICE files.** They also list licences of models in the QVAC model registry that MetaCodex neither
ships nor downloads (Llama 3.2 with its "Built with Llama" duty, HAI-DEF, NVIDIA Parakeet, gpt-oss and others). Those
terms bind only whoever distributes those models; MetaCodex's four models are in [§7.1](#71-models-the-app-downloads).

## 5. Linux image and overlay (in the APK)

### 5.1 Debian 13 "trixie" (assets/linux/rootfs.tar)

The official `debian:trixie-slim` image for linux/arm64 from Docker Hub, unmodified: manifest digest
`sha256:24a01011d10eba3a023bc8585b9650b8b29021240d4217c05b905e09ef4a5232`, layer
`sha256:acd43e9bebc37bc4ba7a5f47bd7598913337803e53d468f37d940f11f5d5f1ef` (30,214,037 bytes, fetched and checked by
`userland/fetch-rootfs.sh` on 2026-11-06; the APK packs it as plain tar). It holds **78 Debian packages**, each with
its licence in `/usr/share/doc/<package>/copyright` (76 files; `libgcc-s1` and `libstdc++6` point to `gcc-14-base`'s),
and the standard licence texts in `/usr/share/common-licenses/`. Licences: mostly GPL-2+/GPL-3+/LGPL, BSD, MIT/Expat,
public domain ([Appendix A](#appendix-a-debian-packages-in-the-linux-image)). Duty for the GPL ones: the corresponding
source (Debian's source packages of exactly these versions, deb.debian.org / snapshot.debian.org): **G3**.

### 5.2 Overlay (assets/linux/overlay.tar, 443 files)

Our own files laid over the image (§1), plus:

| Bundled | Source | Licence | Licence file shipped |
|---|---|---|---|
| 36 Unity agent skills | github.com/Unity-Technologies/skills @ `60f95e6d` (2026-11-06) | Unity Companion License | `/root/.claude/skills/THIRD-PARTY/LICENSE-unity-skills.md` |
| 7 Meta agent skills (hz-*) | github.com/meta-quest/agentic-tools (via the skill set's vendor lock) | Apache-2.0 | `/root/.claude/skills/THIRD-PARTY/LICENSE-meta-agentic-tools` |
| this file and the licence texts not already in the image | this repository | — | `/usr/share/doc/metacodex/` (from the next build) |

`userland/agent-skills/SOURCES.md` lists every skill.

## 6. Fonts (in the APK)

Inter Medium, Rajdhani SemiBold (SIL OFL 1.1); DejaVu Sans Mono and DejaVu Sans (Bitstream Vera / Arev licences,
DejaVu changes public domain); Noto Sans Symbols, Noto Sans Symbols 2, Noto Emoji (SIL OFL 1.1); Liberation Sans
(TextMesh Pro's default, SIL OFL 1.1). Exact copyright lines, versions and which are only in the repository:
[licenses/fonts.md](licenses/fonts.md).

## 7. Downloaded at first use (not in the APK)

### 7.1 Models the app downloads

The AI service fetches them at its first start (pinned by sha256 in `qvac/server/models.js`) into the app's own files;
nothing is redistributed by us inside the APK.

| Model | File (sha256) | From | Licence | Duties |
|---|---|---|---|---|
| Whisper base, multilingual, ggml q8_0 (speech to text) | `ggml-base-q8_0.bin` (`c577b9a8…b7d9`) | huggingface.co/ggerganov/whisper.cpp @ `5359861c` | MIT (OpenAI's Whisper weights; whisper.cpp's conversion) | keep the notice (this file) |
| Qwen3 0.6B, GGUF q4_0 (local LLM) | `Qwen3-0.6B-Q4_0.gguf` (`33bcc570…75cb`) | huggingface.co/unsloth/Qwen3-0.6B-GGUF @ `50968a44` | Apache-2.0 (Qwen team, Alibaba Cloud) | licence + notice (this file) |
| multilingual-e5-small, GGUF q8_0 (voice command decisions) | `multilingual-e5-small-q8_0.gguf` (`e011debc…7c93`) | huggingface.co/TwinSunsLLC/multilingual-e5-small-gguf @ `b6cac961` | MIT, "inherited from the base model" intfloat/multilingual-e5-small (the conversion's card; the base card did not load when checked) | keep the notice |
| Supertonic 3, GGUF q4_0 (text to speech, 31 languages, voices F1–F5, M1–M5) | `supertonic3-q4_0.gguf` (`e3f3edc8…5fb7`) | QVAC model registry `qvac_models_compiled/ggml/supertonic/2026-06-24/`; upstream huggingface.co/Supertone/supertonic-3 | **BigScience OpenRAIL-M** (weights; Supertone's code is MIT) | include the licence (done: [licenses/Supertonic-3-OpenRAIL-M.txt](licenses/Supertonic-3-OpenRAIL-M.txt)) and **pass its use restrictions on to users**, see below and **G4** |

**Supertonic 3 use restrictions (OpenRAIL-M, Attachment A), which apply to everyone using MetaCodex's voice.** The voice
may not be used: against the law; to exploit or harm minors; to generate or spread verifiably false information meant
to harm others; to generate or spread personal information that can harm someone; to publish machine-generated content
in any context without clearly saying it is machine-generated; to defame, disparage or harass; to impersonate anyone
(e.g. deepfakes) without consent; for fully automated decisions that harm a person's legal rights or create binding
obligations; to discriminate against or harm people for their social behaviour or personal characteristics; to
exploit the vulnerabilities of a group of people to distort their behaviour in a harmful way; or the other uses the
licence lists. The full text is binding, not this summary.

### 7.2 What `mcx-setup` downloads (first use, or on request)

| What | From | Licence | Note |
|---|---|---|---|
| Base tools: ca-certificates, curl, git, xz-utils, python3, procps, less, nano | Debian (apt) | their own (in each package's `copyright`) | installed on the device by apt |
| Node.js 24 LTS (linux-arm64) | nodejs.org/dist/latest-v24.x (checked against SHASUMS256.txt) | MIT, plus the notices of its bundled parts in its `LICENSE` | needed by MQH and the agent CLIs |
| Cloud agent CLI (Claude Code) | Anthropic's official installer (claude.ai/install.sh), npm `@anthropic-ai/claude-code` as fallback | proprietary: "SEE LICENSE IN README.md" (© Anthropic PBC; Anthropic's terms) | **never bundled** (plan D3); installed by the user's own device, used with the user's own account; MetaCodex never touches its login |
| Open agent CLI (OpenCode) | npm `opencode-ai` (1.19.5 when checked) | MIT | |
| QVAC CLI and SDK (development path only) | npm `@qvac/cli` 0.18.0, `@qvac/sdk` 0.23.0 | Apache-2.0 | `mcx-setup qvac` |
| Android NDK r27d for arm64 Linux (community build) | github.com/HomuHomu833/android-ndk-custom, release r27 (sha256 `94a7e5a0…45dc`) | the build scripts: MIT (Copyright (c) 2025-2026 Lily Ross); the NDK's own components keep their licences (LLVM: Apache-2.0 with LLVM exception; Bionic and NDK parts: Apache-2.0 / BSD; listed in the NDK's NOTICE files) | Google ships Linux NDKs for x86_64 only |
| android.jar, Android SDK Platform 34 ext7 r03 | dl.google.com/android/repository/platform-34-ext7_r03.zip (sha256 `16fdb74c…20f3`) | **Android Software Development Kit License Agreement** (developer.android.com/studio/terms) | the user must accept that agreement: **G6** |
| OpenXR loader for Android + headers 1.1.65 | Maven Central `org.khronos.openxr:openxr_loader_for_android` (sha256 `a298c326…e8643`) | Apache-2.0 (Khronos) | its `LICENSE` is kept in `/opt/openxr/LICENSE` |
| APK tools (aapt, apksigner, zipalign), adb, JDK (`default-jdk-headless`) | Debian (apt) | Apache-2.0 (Android tools), GPL-2.0 with Classpath Exception (OpenJDK) | `mcx-setup quest` |

### 7.3 What the user chooses to install (SYSTEM > TOOLS, AGENTS)

Each is installed on the user's tap, from its official source, under its own licence: Debian packages (apt), pnpm
(MIT), bun (MIT, bun.sh installer), uv (MIT OR Apache-2.0, astral.sh installer), Rust via rustup (MIT OR Apache-2.0),
and from AGENTS: Codex CLI (npm `@openai/codex`, Apache-2.0), Pi coding agent (npm `@earendil-works/pi-coding-agent`,
MIT). MetaCodex runs the installers in a visible terminal; it redistributes none of them.

## 8. In the repository only (not shipped)

### 8.1 MQH, the Meta Quest Harness (`mqh/`)

A trimmed hard fork of bb (github.com/get-bb/bb) at `8e4208e` (2026-11-06): **MIT, Copyright (c) 2026 Michael Yong**
(`mqh/LICENSE`, `mqh/UPSTREAM.md`). Today it runs in WSL for development; it is not in the APK or the overlay. Its
installed dependency tree (pnpm, 1,012 packages, read from `~/mcxwork/mqh-build/node_modules` on 2026-11-10): MIT 805,
Apache-2.0 77, ISC 48, BSD-3-Clause 30, BlueOak-1.0.0 13 (glob, minimatch, lru-cache, tar, …), MPL-2.0 10
(lightningcss and its platform builds), BSD-2-Clause 10, MIT-0 2, and one each of Artistic-2.0 (npm), Python-2.0
(argparse), CC-BY-4.0 (caniuse-lite data), CC0-1.0 (mdn-data), Unlicense (isbot), 0BSD, LGPL-3.0-only
(rollup-plugin-dts, build time only), five dual-licensed (MIT OR Apache-2.0, MIT OR CC0-1.0, BSD-2-Clause OR MIT OR
Apache-2.0, MIT AND ISC, MIT OR WTFPL), and five Anthropic packages under "SEE LICENSE IN …" (`@anthropic-ai/claude-agent-sdk`
0.3.318 and its four platform builds, which carry a Claude Code binary). Before MQH ships: **G8**.

### 8.2 Tools, tests, templates

- `tools/`, `tests/`, `native/hello-native`: ours. The terminal tests compare against tmux (run at test time, not
  shipped). Hello Native is built against the OpenXR headers and loader of §7.2 and Unity's Android NDK.
- `.gitattributes`: from the gitattributes/gitattributes Unity template (MIT, as its header says).
- `tools/voicebench/`: our own sentences and results.

### 8.3 Removed

Shipped in APKs 0.7.3–0.7.8 and removed on 2026-11-10 (branch `chore/tools-list-and-licenses`): the SYSTEM > TOOLS
icon atlas, logos from Simple Icons (CC0 1.0 for the SVG data; each logo a trademark of its owner, used only to name
the tool) and pictograms from Lucide (ISC), built by the deleted `tools/make-tool-icons.js` with resvg-js (MPL-2.0) and
pngjs (MIT) at development time. No logo of a third party is in the app any more.

## Services the app or its tools talk to (terms, not licences)

The user's own accounts and the providers' terms apply: Anthropic (Claude Code login or API key), OpenAI (Codex),
OpenCode's model providers, GitHub (only if the user stores a token in VAULT > GIT), Hugging Face and the QVAC registry
(model downloads), Debian mirrors, nodejs.org, the npm registry, Google's SDK repository (android.jar), Maven Central,
GitHub Releases (the NDK), skills.sh (MQH's skill registry). MetaCodex never reads or stores provider login tokens.

## 9. Duties and open gaps

Done in this change: the tool logos are gone (§8.3); this inventory exists; the notices it needs (this file, the
Supertonic licence, the android-shmem notice, the font notices and licences, MoonSharp's licence, the proot/talloc
notice) ship with the next build in `/usr/share/doc/metacodex/`, and the Settings > ABOUT tab points there.

| # | Gap | Why it matters | Fix |
|---|---|---|---|
| G1 | V8 and libuv notices (inside `libbare.so`) and the Unity runtime's third-party notices are not yet in our notice folder | BSD-3 / MIT ask for the copyright notice with binary distributions | copy V8's and libuv's `LICENSE` texts (Bare's sources) into `docs/licenses/`; link Unity's notices for 6000.4.5f1 |
| G2 | GPL-2.0 proot and LGPL-3.0 talloc: no corresponding source offered yet | GPL-2.0 §3, and LGPL-3.0 with GPL-3.0 §6: who ships the binary must give or offer its source | publish the exact sources (termux/proot at the commit Termux built 5.1.107.102 from, termux-packages' `proot` and `libtalloc` recipes, talloc-2.5.0.tar.gz) next to each release, or a written offer valid 3 years, and say so in ABOUT |
| G3 | Debian image: same duty for its GPL packages | the APK ships those binaries | a script that fetches the 78 source packages of Appendix A from snapshot.debian.org into the release assets, or the written offer |
| G4 | Supertonic 3 (OpenRAIL-M): its use restrictions must reach users | licence §4(a), §5 | done in this file; still to show in the app (VOICE panel, next to the voice models) |
| G5 | The APK's launcher icon is Unity's default icon, the Unity logo (`res/mipmap*/app_icon.png`) | a third party's logo, against our no-logo rule | set our own icon (drawn from the MetaCodex mark of `UiIconAtlas`) in `MetaCodexSetup` |
| G6 | android.jar is downloaded under the Android SDK License without asking the user to accept it | that licence requires acceptance | show the terms and ask before `mcx-setup quest` fetches it (the first-use flow and the NDK row) |
| G7 | No licence for our own code; `qvac/package.json` says Apache-2.0 | clarity for judges, users and contributors | the owner picks one (keep all rights reserved, or a licence) and adds `LICENSE` at the root; align `qvac/package.json` |
| G8 | When MQH ships to the headset: `@anthropic-ai/claude-agent-sdk` (proprietary, carries a Claude Code binary) must not be packed into the APK or overlay | plan rule D3, Anthropic's terms | install it at first use like the CLI, from npm; ship MQH's MIT notice and the MPL-2.0 / CC-BY-4.0 / BlueOak / Artistic-2.0 notices of §8.1 |
| G9 | The `UiIcon` shader is left without a user (it drew the logos) | tidiness, a few KB | delete it once the orb branch, which edits it, has landed |

## How this was checked (rerun before every submission)

- What ships: `unzip -l unity/Builds/MetaCodex.apk` (native libs, assets), the bundle manifests inside it
  (`assets/linux/*.json`, `assets/qvac/runtime.json`), the build's `global-metadata.dat` for assemblies, and the Unity
  data for fonts.
- Licences: `LICENSE.md` and the third-party notices of each package in `unity/Library/PackageCache`; `package.json`,
  `LICENSE` and `NOTICE` inside `assets/qvac/runtime.tar`; the `copyright` files inside `assets/linux/rootfs.tar`;
  the fonts' name tables; `npm view <package> license version` for downloads; the model cards for models.

---

## Appendix A: Debian packages in the Linux image

Read from the image's `/var/lib/dpkg/status` and each package's `/usr/share/doc/<package>/copyright` (DEP-5 `License:`
fields; "not machine-readable" means the file is free text).

| Package | Version | Source package | Licences declared in its copyright file |
|---|---|---|---|
| apt | 3.0.4 | same | GPL-2+, GPL-2+ and curl, BSD-3-clause, Expat, GPL-2, curl |
| base-files | 13.8+deb13u7 | same | GPL-2+, verbatim |
| base-passwd | 3.6.7 | same | GPL-2, public-domain |
| bash | 5.2.37-2+b10 | same | GPL-3+, GPL-3+ with Bison exception, GPL-2+, GFDL-NIV-1.3, Latex2e, BSD-4-clause-UC and MIT-like … |
| bsdutils | 1:2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| coreutils | 9.7-3 | same | GPL-3+, GPL-3+ and BSD-4-clause-UC, BSD-4-clause-UC, GPL-3+ and ISC, ISC, FSFULLR … |
| dash | 0.5.12-12 | same | BSD-3-Clause, public-domain, GPL-2+, BSD-3-clause |
| debconf | 1.5.91 | same | BSD-2-clause |
| debian-archive-keyring | 2025.1 | same | see copyright file (not machine-readable) |
| debianutils | 5.23.2 | same | GPL-2+, public-domain, SMAIL-GPL |
| diffutils | 1:3.10-4 | same | GPL-3+, FSFULLR, GPL-3+ and FSFULLR, LGPL-2.1+, GPL-3+ with autoconf exception, GPL-3+ with texinfo exception … |
| dpkg | 1.22.22 | same | GPL-2+, public-domain-s-s-d |
| findutils | 4.10.0-3 | same | GFDL-NIV-1.3+ or GFDL-NIV-1.3+, GPL-3+, FSFAP, GFDL-NIV-1.3+, GPL-2+ with Autoconf-data exception, GPL-3+ with Autoconf-data exception … |
| gcc-14-base | 14.2.0-19 | gcc-14 | GPL-3+ with the GCC Runtime Library Exception for the runtime libraries (libgcc, libstdc++); see its copyright file (free text) |
| grep | 3.11-4+b1 | same | GPL-3+ |
| gzip | 1.13-1+deb13u1 | same | GPL-3+, GFDL-1.3+-no-invariant, FSF-manpages |
| hostname | 3.25 | same | GPL-2 |
| init-system-helpers | 1.69~deb13u1 | same | BSD-3-clause, GPL-2+ |
| libacl1 | 2.3.2-2+b1 | acl | GPL-2+, LGPL-2+ |
| libapt-pkg7.0 | 3.0.4 | apt | GPL-2+, GPL-2+ and curl, BSD-3-clause, Expat, GPL-2, curl |
| libattr1 | 1:2.5.2-3 | attr | GPL-2+, LGPL-2+ |
| libaudit-common | 1:4.0.2-2+deb13u1 | audit | GPL-2, LGPL-2.1 |
| libaudit1 | 1:4.0.2-2+deb13u1 | audit | GPL-2, LGPL-2.1 |
| libblkid1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| libbsd0 | 0.12.2-2 | libbsd | BSD-3-clause, BSD-3-clause-Regents, BSD-3-clause-Regents and BSD-2-clause-NetBSD, BSD-3-clause-author, BSD-3-clause-John-Birrell, BSD-5-clause-Peter-Wemm … |
| libbz2-1.0 | 1.0.8-6 | bzip2 | BSD-variant, GPL-2 |
| libc-bin | 2.41-12+deb13u4 | glibc | LGPL-2.1+, LGPL-2+, LGPL-2.1+-with-link-exception, LGPL-3+, GPL-2+, GPL-2+-with-link-exception … |
| libc6 | 2.41-12+deb13u4 | glibc | LGPL-2.1+, LGPL-2+, LGPL-2.1+-with-link-exception, LGPL-3+, GPL-2+, GPL-2+-with-link-exception … |
| libcap-ng0 | 0.8.5-4+b1 | libcap-ng | LGPL-2.1+, GPL-2+, GPL-3 |
| libcap2 | 1:2.75-10+deb13u1+b3 | same | BSD-3-clause or GPL-2, BSD-3-clause or GPL-2+, BSD-3-clause, GPL-2, GPL-2+ |
| libcrypt1 | 1:4.4.38-1 | libxcrypt | see copyright file (not machine-readable) |
| libdb5.3t64 | 5.3.28+dfsg2-9 | db5.3 | Sleepycat and BSD-3-clause, Sleepycat, BSD-3-clause, Ms-PL, GPL or Artistic, X11 … |
| libdebconfclient0 | 0.280 | cdebconf | BSD-2-Clause, GPL-2+, BSD-2-clause |
| libgcc-s1 | 14.2.0-19 | gcc-14 | see gcc-14-base (its doc folder is a link to gcc-14-base's) |
| libgmp10 | 2:6.3.0+dfsg-3 | gmp | GPL-2+ or LGPL-3+, GPL-3+, GPL-3+ with Bison exception, GPL-2+, LGPL-3+ |
| libhogweed6t64 | 3.10.1-1 | nettle | LGPL-3+ or GPL-2+, LGPL-2+, Expat, GPL-2+, GPL-3+ with Autoconf exception, public-domain … |
| liblastlog2-2 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| liblz4-1 | 1.10.0-4 | lz4 | GPL-2+, BSD-2-clause, BSD-2-clause or GPL-2+ |
| liblzma5 | 5.8.1-1+deb13u2 | xz-utils | Different licenses apply to different files in this package. Here, 0BSD, GPL-2+, LGPL-2.1+, FSFULLR, GPL-3.0-or-later-WITH-Autoconf-exception-macro … |
| libmd0 | 1.1.0-2+b1 | libmd | BSD-3-clause, BSD-3-clause-Aaron-D-Gifford, BSD-2-clause, BSD-2-clause-NetBSD, ISC, Beerware … |
| libmount1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| libnettle8t64 | 3.10.1-1 | nettle | LGPL-3+ or GPL-2+, LGPL-2+, Expat, GPL-2+, GPL-3+ with Autoconf exception, public-domain … |
| libpam-modules | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers … |
| libpam-modules-bin | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers … |
| libpam-runtime | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers … |
| libpam0g | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers … |
| libpcre2-8-0 | 10.46-1~deb13u3 | pcre2 | BSD-3-clause-Cambridge with BINARY LIBRARY-LIKE PACKAGES exception, BSD-3-clause, X11, BSD-2-clause, public-domain |
| libseccomp2 | 2.6.0-2 | libseccomp | LGPL-2.1 |
| libselinux1 | 3.8.1-1 | libselinux | public-domain, GPL-2 |
| libsemanage-common | 3.8.1-1 | libsemanage | LGPL-2.1+, GPL-2 |
| libsemanage2 | 3.8.1-1 | libsemanage | LGPL-2.1+, GPL-2 |
| libsepol2 | 3.8.1-1 | libsepol | LGPL-2.1+, Zlib, GPL-2, GPL-2+ |
| libsmartcols1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| libsqlite3-0 | 3.46.1-7+deb13u2 | sqlite3 | public-domain, GPL-2+ |
| libssl3t64 | 3.5.7-1~deb13u3 | openssl | Apache-2.0, Artistic or GPL-1+, Artistic, GPL-1+ |
| libstdc++6 | 14.2.0-19 | gcc-14 | see gcc-14-base (its doc folder is a link to gcc-14-base's) |
| libsystemd0 | 257.13-1~deb13u1 | systemd | LGPL-2.1+, CC0-1.0, GPL-2 with Linux-syscall-note exception, Expat, public-domain, GPL-2+ |
| libtinfo6 | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| libudev1 | 257.13-1~deb13u1 | systemd | LGPL-2.1+, CC0-1.0, GPL-2 with Linux-syscall-note exception, Expat, public-domain, GPL-2+ |
| libuuid1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| libxxhash0 | 0.8.3-2 | xxhash | BSD-2-clause, GPL-2+, BSD-2-clause or GPL-2+ |
| libzstd1 | 1.5.7+dfsg-1 | libzstd | BSD-3-clause or GPL-2, zlib, Expat, GPL-2, BSD-3-clause |
| login | 1:4.16.0-2+really2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| login.defs | 1:4.17.4-2 | shadow | BSD-3-clause, GPL-1, GPL-2+ |
| mawk | 1.3.4.20250131-1 | same | GPL-2.0-only, X11, CC-BY-3.0 |
| mount | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| ncurses-base | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| ncurses-bin | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| openssl-provider-legacy | 3.5.7-1~deb13u3 | openssl | Apache-2.0, Artistic or GPL-1+, Artistic, GPL-1+ |
| passwd | 1:4.17.4-2 | shadow | BSD-3-clause, GPL-1, GPL-2+ |
| perl-base | 5.40.1-6+deb13u1 | perl | GPL-1+ or Artistic, Expat, REGCOMP, and GPL-1+ or Artistic, GPL-3+-WITH-BISON-EXCEPTION, Unicode, GPL-1+ or Artistic, and Unicode … |
| sed | 4.9-2+deb13u1 | same | GPL-3+, X11, GFDL-NIV-1.3+, GPL-3+ and ISC, ISC, BSD-4-clause-UC … |
| sqv | 1.3.0-3+b2 | rust-sequoia-sqv | LGPL-2.0-or-later |
| sysvinit-utils | 3.14-4 | sysvinit | GPL-2.0+, LGPL-2.1+, GPL-3.0, GPL-2.0 |
| tar | 1.35+dfsg-3.1 | same | GPL-3+, GPL-3+ with Bison exception, LGPL-2.1+, LGPL-3+, GPL-2+ |
| tzdata | 2026f-0+deb13u1 | same | public-domain |
| util-linux | 2.41.5-0+deb13u1 | same | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause … |
| zlib1g | 1:1.3.dfsg+really1.3.1-1+b1 | zlib | Zlib |

## Appendix B: QVAC runtime packages in the APK

Read from every `package.json` inside `assets/qvac/runtime.tar` of APK 0.7.8 (`metacodex-ai` is our own service; see G7
about its declared licence). Each package's `LICENSE` / `NOTICE` file ships inside the same tar.

136 packages: Apache-2.0 84, MIT 45, ISC 7

| Package | Version | Licence |
|---|---|---|
| @hyperswarm/secret-stream | 6.9.2 | Apache-2.0 |
| @qvac/asr-ggml | 0.12.0 | Apache-2.0 |
| @qvac/asr-ggml-android-arm64 | 0.12.0 | Apache-2.0 |
| @qvac/embed-llamacpp | 0.55.0 | Apache-2.0 |
| @qvac/error | 0.1.1 | Apache-2.0 |
| @qvac/fabric | 0.26.0 | Apache-2.0 |
| @qvac/fabric-android-arm64 | 0.26.0 | Apache-2.0 |
| @qvac/infer-base | 0.8.0 | Apache-2.0 |
| @qvac/langdetect-text | 0.1.3 | Apache-2.0 |
| @qvac/llm-llamacpp | 0.68.0 | Apache-2.0 |
| @qvac/logging | 0.1.1 | Apache-2.0 |
| @qvac/registry-client | 0.8.0 | Apache-2.0 |
| @qvac/registry-schema | 0.4.0 | Apache-2.0 |
| @qvac/tts-ggml | 0.15.0 | Apache-2.0 |
| @qvac/tts-ggml-android-arm64 | 0.15.0 | Apache-2.0 |
| adaptive-timeout | 1.0.1 | Apache-2.0 |
| b4a | 1.9.0 | Apache-2.0 |
| bare-abort | 2.0.14 | Apache-2.0 |
| bare-abort-controller | 1.1.2 | Apache-2.0 |
| bare-addon-resolve | 1.11.1 | Apache-2.0 |
| bare-ansi-escapes | 2.2.4 | Apache-2.0 |
| bare-assert | 1.3.0 | Apache-2.0 |
| bare-buffer | 3.7.1 | Apache-2.0 |
| bare-dns | 2.3.1 | Apache-2.0 |
| bare-env | 3.0.1 | Apache-2.0 |
| bare-events | 2.9.2 | Apache-2.0 |
| bare-fs | 4.8.4 | Apache-2.0 |
| bare-hrtime | 2.1.2 | Apache-2.0 |
| bare-http-parser | 2.1.4 | Apache-2.0 |
| bare-http1 | 4.6.3 | Apache-2.0 |
| bare-https | 3.1.0 | Apache-2.0 |
| bare-inspect | 3.1.10 | Apache-2.0 |
| bare-module-resolve | 1.12.6 | Apache-2.0 |
| bare-net | 2.3.3 | Apache-2.0 |
| bare-os | 3.9.4 | Apache-2.0 |
| bare-path | 3.1.2 | Apache-2.0 |
| bare-pipe | 4.4.0 | Apache-2.0 |
| bare-posix | 1.0.1 | Apache-2.0 |
| bare-process | 4.5.1 | Apache-2.0 |
| bare-semver | 1.1.0 | Apache-2.0 |
| bare-signals | 5.0.0 | Apache-2.0 |
| bare-stdio | 1.0.3 | Apache-2.0 |
| bare-stream | 2.13.4 | Apache-2.0 |
| bare-tcp | 2.6.1 | Apache-2.0 |
| bare-tls | 3.2.0 | Apache-2.0 |
| bare-tty | 5.2.2 | Apache-2.0 |
| bare-type | 1.4.0 | Apache-2.0 |
| bare-url | 2.5.6 | Apache-2.0 |
| big-sparse-array | 1.0.3 | MIT |
| bits-to-bytes | 1.3.0 | ISC |
| blind-relay | 1.6.1 | Apache-2.0 |
| bogon | 1.3.0 | MIT |
| codecs | 3.1.0 | MIT |
| compact-encoding | 3.6.0 | Apache-2.0 |
| compact-encoding-bitfield | 1.1.0 | Apache-2.0 |
| compact-encoding-net | 1.3.0 | Apache-2.0 |
| corestore | 7.16.1 | MIT |
| debounceify | 1.1.0 | MIT |
| device-file | 2.3.1 | Apache-2.0 |
| dht-rpc | 6.28.0 | MIT |
| events-universal | 1.0.1 | Apache-2.0 |
| fast-fifo | 1.3.2 | MIT |
| fd-lock | 2.2.0 | Apache-2.0 |
| flat-tree | 1.13.0 | MIT |
| fs-native-extensions | 1.5.3 | Apache-2.0 |
| generate-object-property | 2.0.0 | MIT |
| generate-string | 1.0.1 | MIT |
| hyperbee | 2.27.3 | MIT |
| hyperblobs | 2.13.0 | Apache-2.0 |
| hypercore | 11.40.3 | MIT |
| hypercore-crypto | 3.7.0 | MIT |
| hypercore-errors | 1.5.0 | Apache-2.0 |
| hypercore-id-encoding | 1.3.0 | Apache-2.0 |
| hypercore-stats | 2.5.2 | Apache-2.0 |
| hypercore-storage | 3.4.0 | Apache-2.0 |
| hyperdb | 6.11.1 | Apache-2.0 |
| hyperdht | 6.36.0 | MIT |
| hyperdht-address | 1.1.1 | Apache-2.0 |
| hyperdht-stats | 1.11.1 | Apache-2.0 |
| hyperdispatch | 1.6.0 | Apache-2.0 |
| hyperschema | 1.26.2 | Apache-2.0 |
| hyperswarm | 4.17.2 | MIT |
| hyperswarm-stats | 1.3.4 | Apache-2.0 |
| index-encoder | 3.5.0 | Apache-2.0 |
| is-options | 1.0.2 | MIT |
| is-property | 1.0.2 | MIT |
| kademlia-routing-table | 1.0.6 | MIT |
| language-detect-node | 1.0.0 | ISC |
| language-detect-node-esm | 1.0.0 | ISC |
| metacodex-ai | 0.1.0 | Apache-2.0 |
| mutexify | 1.4.0 | MIT |
| nanoassert | 2.0.0 | ISC |
| nat-sampler | 1.0.1 | MIT |
| noise-curve-ed | 2.1.0 | ISC |
| noise-handshake | 4.2.0 | Apache-2.0 |
| paparam | 1.15.0 | Apache-2.0 |
| passive-core-watcher | 1.0.2 | Apache-2.0 |
| protocol-buffers-encodings | 1.2.0 | MIT |
| protomux | 3.13.0 | MIT |
| queue-tick | 1.0.1 | MIT |
| quickbit-native | 2.4.9 | Apache-2.0 |
| quickbit-universal | 2.2.1 | ISC |
| rache | 1.0.1 | Apache-2.0 |
| random-array-iterator | 1.0.0 | MIT |
| ready-resource | 1.2.0 | MIT |
| record-cache | 1.2.0 | MIT |
| refcounter | 1.0.0 | Apache-2.0 |
| require-addon | 1.3.0 | Apache-2.0 |
| resolve-reject-promise | 1.1.0 | MIT |
| resource-on-exit | 1.0.0 | Apache-2.0 |
| rocksdb-native | 3.19.3 | Apache-2.0 |
| safety-catch | 1.0.3 | MIT |
| scope-lock | 1.2.4 | Apache-2.0 |
| shuffled-priority-queue | 2.1.0 | MIT |
| signal-promise | 1.0.3 | MIT |
| signed-varint | 2.0.1 | MIT |
| simdle-native | 1.3.10 | Apache-2.0 |
| simdle-universal | 1.1.3 | ISC |
| sodium-native | 5.2.0 | MIT |
| sodium-secretstream | 1.2.0 | MIT |
| sodium-universal | 5.0.1 | MIT |
| speedometer | 1.1.0 | MIT |
| streamx | 2.28.1 | MIT |
| teex | 1.0.1 | MIT |
| text-decoder | 1.2.7 | Apache-2.0 |
| time-ordered-set | 2.0.1 | MIT |
| timeout-refresh | 2.0.1 | MIT |
| tiny-byte-size | 1.1.0 | MIT |
| tinyld | 1.3.4 | MIT |
| udx-native | 1.22.1 | Apache-2.0 |
| unordered-set | 2.0.1 | MIT |
| unslab | 1.3.1 | Apache-2.0 |
| varint | 5.0.0 | MIT |
| which-runtime | 1.4.0 | Apache-2.0 |
| xache | 1.3.0 | MIT |
| z32 | 1.1.0 | MIT |
