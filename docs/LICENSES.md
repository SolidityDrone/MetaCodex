# MetaCodex: proof of licence

Every piece of third-party software, data, font and model MetaCodex ships, downloads or builds with: its version, its
licence (SPDX id where one exists), who holds the copyright, where it comes from, how it reaches the user, and what its
licence asks of us. Written for the contest's rule (Official Rules section 4: the sponsor "may at any time request
written proof of ownership or proper license or use of any material comprising an Entry") and kept current by the
rule in `AGENTS.md` (anything new goes in here in the same change). The project's own licence (MIT) and the reasoning
behind it are in [section 2](#2-the-licence-of-metacodex-itself). The licence texts are in [docs/licenses/](licenses/);
every licence named below is linked to its text the first time it appears in a table.

**State: 2026-11-12.** Measured, not recalled. The shipped contents are those of **APK 0.7.16** (versionCode 23,
`unity/Builds/MetaCodex.apk`, 204,841,243 bytes, sha256 `d1fbb083dd73bca6...`, built 2026-11-11 16:05 from the commit
`648c785`): every file of the APK was listed, the four bundles inside it (`assets/linux/rootfs.tar`, `overlay.tar`,
`mqh.tar`, `assets/qvac/runtime.tar`) were unpacked, every `package.json` in them was read by a script, the Debian
image's `dpkg` status and `copyright` files were read, the native libraries were inspected for the libraries embedded in
them (`strings`), the assemblies and fonts in the build data were listed, and the Unity package cache supplied the
package licences. What is downloaded later was read from the scripts that download it (`mcx-setup`,
`local-ai/server/models.js`, `ShellSystem.cs`, `ShellAgentGet.cs`), from the npm registry, from the Hugging Face model cards
and from the upstream repositories. Where something could not be verified, the text says so. Open duties are in
[section 8](#8-gaps).

On the headset this file and the licence texts it points to are in `/usr/share/doc/metacodex/` (laid by
`userland/build-overlay.py`, which ships every file that lies directly in `docs/licenses/`: keep that folder flat). The
standard texts of the Debian image are also in `/usr/share/common-licenses/` there.

## Summary

| Group | What | Licences | What they ask of us |
|---|---|---|---|
| **Our own code** | the app, userland scripts, Core v0, native template, tools, tests, AI service | [MIT](licenses/MIT.txt) (see [section 2](#2-the-licence-of-metacodex-itself)) | keep the notice |
| **A. In the APK** | Unity engine + 20 packages | Unity Terms of Service (engine); [Unity Companion License v1.4](licenses/Unity-Companion-License-v1.4.txt) for 15 packages; UCL for the source plus the [Unity Package Distribution License](licenses/Unity-Package-Distribution-License.txt) for the rest, for 4; UPDL alone for 1 | licence + third-party notices travel with the packages; no standalone redistribution of the packages; a valid Unity licence |
| | Meta XR SDK 205.0.0 (4 packages) and its native libraries | [Meta Platform Technologies SDK License](licenses/Meta-Platform-Technologies-SDK-License.txt) (the "Oculus SDK License") | only for Meta hardware; redistributable parts only inside the app; never put the SDK under an open-source licence; keep its notices |
| | PRoot, talloc, libandroid-shmem (separate programs/libraries) | [GPL-2.0-or-later](licenses/GPL-2.0.txt), [LGPL-3.0-or-later](licenses/LGPL-3.0.txt), [BSD-3-Clause](licenses/BSD-3-Clause.txt) | **source offer** (done: [section 6](#6-source-code-for-the-gpl-and-lgpl-parts)); licence text |
| | Bare runtime (V8, libuv, BoringSSL, ICU inside), QVAC engines and 133 npm packages | Apache-2.0 83, MIT 45, ISC 5, plus BSD-3, ISC/OpenSSL, Unicode | licence + NOTICE texts (done) |
| | MoonSharp | BSD-3-Clause | notice (done) |
| | 8 fonts | [OFL-1.1](licenses/OFL-1.1.txt) (6), Bitstream Vera / Arev (2) | notice + licence with each copy (done) |
| | Linux image: Debian 13 "trixie", 78 packages | GPL-2/3, LGPL, BSD, MIT/X11, ISC, public domain, ... (each package's own) | **source offer** (done); licence texts (done) |
| | 47 bundled agent skills | Unity Companion License (36), [Apache-2.0](licenses/Apache-2.0.txt) (7), ours (4) | licence file travels with them (done) |
| | MQH harness (fork of bb) + 72 npm packages + 97 bundled npm packages + 4 native modules | MIT, plus ISC, Apache-2.0, BSD-3, BlueOak, dual licences | MIT notice of Michael Yong (done), package notices (done) |
| **B. Downloaded at first use** | 4 AI models (+1 optional: the local coder, Qwen2.5-Coder 0.5B, Apache-2.0) | MIT (Whisper base, multilingual-e5-small), Apache-2.0 (Qwen3 0.6B), **OpenRAIL-M** (Supertonic 3) | OpenRAIL-M use restrictions must reach the users |
| | Node.js, NDK (community build), OpenXR 1.1.65, 198 Debian packages | MIT, Apache-2.0, LLVM exception, GPL/LGPL/... | none for us (the user's device downloads them); keep the pointers |
| | android.jar | Android SDK License Agreement | the user must accept it (gap G6) |
| | agent CLIs the user installs | Claude Code: proprietary, Anthropic's terms; OpenCode MIT; Codex Apache-2.0 | never bundled |
| **C. Build / development only** | Unity Editor, build tools, MQH dev tree (1,012 npm packages), voice-bench packages and models, diagram fonts | many; none is shipped | listed for completeness |

Counts, measured: 4 AI models; 78 Debian packages in the image and 198 downloaded; 133 + 72 + 97 npm packages
(overlapping sets, 302 entries, 301 distinct, [Appendix B](#appendix-b-npm-packages-in-the-apk)); 22 native libraries in
`lib/arm64-v8a` and 28 more in `assets/lib`; 8 fonts; 20 Unity packages in the APK (8 more only in the Editor).

---

## 1. Reading guide

- **Notice**: reproduce the copyright line and licence text with the software. **Attribution**: name the author.
  **Source offer**: GPL/LGPL, give the source or a written offer. **Use restrictions**: OpenRAIL-M.
  **No redistribution**: the SDK terms; we ship it only inside the app, as the terms allow.
- "Where" says where the thing is in the APK (paths inside the APK, or inside one of its four bundles) or in the
  repository.
- There is no separate `THIRD_PARTY_NOTICES.md`: the notices are the files of `docs/licenses/` (they ship to
  `/usr/share/doc/metacodex/licenses/`), indexed below.
- "Text" links the licence text in `docs/licenses/` (`debian-image-copyright.txt` holds all 78 Debian copyright files).

### Licence texts in `docs/licenses/`

| Text | SPDX / name | Used by |
|---|---|---|
| [MetaCodex-MIT-LICENSE.txt](licenses/MetaCodex-MIT-LICENSE.txt) | MIT (ours; the root `LICENSE` for the headset) | our code |
| [MIT.txt](licenses/MIT.txt), [BSD-2-Clause.txt](licenses/BSD-2-Clause.txt), [BSD-3-Clause.txt](licenses/BSD-3-Clause.txt), [ISC.txt](licenses/ISC.txt), [Zlib.txt](licenses/Zlib.txt), [BlueOak-1.0.0.txt](licenses/BlueOak-1.0.0.txt), [BSL-1.0.txt](licenses/BSL-1.0.txt), [Unicode-3.0.txt](licenses/Unicode-3.0.txt) | the standard permissive texts (templates: each project's copyright line is in its own notice file) | npm packages, Bare's embedded libraries, Unity/Meta notices (Boost), ICU |
| [Apache-2.0.txt](licenses/Apache-2.0.txt), [LLVM-exception.txt](licenses/LLVM-exception.txt), [NCSA.txt](licenses/NCSA.txt) | Apache-2.0; the LLVM exception; University of Illinois/NCSA (legacy LLVM parts inside Burst) | Bare, QVAC, Khronos OpenXR, Meta skills, Qwen3, libc++, Burst |
| [GPL-2.0.txt](licenses/GPL-2.0.txt), [GPL-3.0.txt](licenses/GPL-3.0.txt), [LGPL-2.1.txt](licenses/LGPL-2.1.txt), [LGPL-3.0.txt](licenses/LGPL-3.0.txt), [MPL-2.0.txt](licenses/MPL-2.0.txt) | copyleft texts (copied from the Debian image's `common-licenses`) | PRoot, talloc, the Debian packages; MPL-2.0 for the dev-tree packages (lightningcss) |
| [OFL-1.1.txt](licenses/OFL-1.1.txt); the font files' own texts in `unity/Assets/MetaCodex/Fonts/` | SIL OFL 1.1; Bitstream Vera / Arev | fonts |
| [Supertonic-3-OpenRAIL-M.txt](licenses/Supertonic-3-OpenRAIL-M.txt) | BigScience OpenRAIL-M | Supertonic 3 voice |
| [Unity-Companion-License-v1.4.txt](licenses/Unity-Companion-License-v1.4.txt), [Unity-Package-Distribution-License.txt](licenses/Unity-Package-Distribution-License.txt), [Unity-packages-LICENSE-and-third-party-notices.txt](licenses/Unity-packages-LICENSE-and-third-party-notices.txt), [Unity-6000.4.5f1-third-party-notices.txt](licenses/Unity-6000.4.5f1-third-party-notices.txt), [Unity-skills-LICENSE.md](licenses/Unity-skills-LICENSE.md) | Unity's licences and notices (the Unity Terms of Service are not reproduced: https://unity.com/legal/terms-of-service) | engine, packages, skills |
| [Meta-Platform-Technologies-SDK-License.txt](licenses/Meta-Platform-Technologies-SDK-License.txt), [Meta-XR-SDK-core-NOTICE.txt](licenses/Meta-XR-SDK-core-NOTICE.txt), [Meta-XR-SDK-mrutilitykit-NOTICE.txt](licenses/Meta-XR-SDK-mrutilitykit-NOTICE.txt), [Meta-XR-SDK-package-LICENSE.md](licenses/Meta-XR-SDK-package-LICENSE.md), [Meta-agentic-tools-Apache-2.0-LICENSE.txt](licenses/Meta-agentic-tools-Apache-2.0-LICENSE.txt) | Meta's licence and notices | Meta XR SDK, Meta skills |
| [Android-SDK-License-Agreement.txt](licenses/Android-SDK-License-Agreement.txt) | Android SDK License Agreement | android.jar |
| [V8-LICENSE.txt](licenses/V8-LICENSE.txt), [libuv-LICENSE.txt](licenses/libuv-LICENSE.txt), [BoringSSL-LICENSE.txt](licenses/BoringSSL-LICENSE.txt), [ICU-LICENSE.txt](licenses/ICU-LICENSE.txt), [RocksDB-LevelDB-BSD-3-Clause.txt](licenses/RocksDB-LevelDB-BSD-3-Clause.txt), [libsodium-ISC.txt](licenses/libsodium-ISC.txt) | licences of the libraries embedded in Bare and the AI addons | `libbare.so`, `.bare` addons |
| [MoonSharp-LICENSE.txt](licenses/MoonSharp-LICENSE.txt), [libandroid-shmem-BSD-3-Clause.txt](licenses/libandroid-shmem-BSD-3-Clause.txt), [proot-COPYING.txt](licenses/proot-COPYING.txt), [talloc-LICENSE.txt](licenses/talloc-LICENSE.txt), [MQH-bb-MIT-LICENSE.txt](licenses/MQH-bb-MIT-LICENSE.txt) | the component's own licence file | MoonSharp, shmem, PRoot, talloc, MQH |
| [Qwen3-Apache-2.0-LICENSE.txt](licenses/Qwen3-Apache-2.0-LICENSE.txt), [Whisper-OpenAI-MIT-LICENSE.txt](licenses/Whisper-OpenAI-MIT-LICENSE.txt), [whisper.cpp-ggml-MIT-LICENSE.txt](licenses/whisper.cpp-ggml-MIT-LICENSE.txt) | model and engine licences | the downloaded models, the engines |
| [npm-qvac-runtime-notices.txt](licenses/npm-qvac-runtime-notices.txt), [npm-mqh-runtime-notices.txt](licenses/npm-mqh-runtime-notices.txt), [npm-mqh-bundled-notices.txt](licenses/npm-mqh-bundled-notices.txt) | every `LICENSE`/`NOTICE` of the 133 + 72 + 97 npm packages | Appendix B |
| [debian-image-copyright.txt](licenses/debian-image-copyright.txt), [debian-image-sources.txt](licenses/debian-image-sources.txt), [debian-first-use-packages.md](licenses/debian-first-use-packages.md) | the 78 copyright files; the source files; the first-use set | the Debian image and downloads |
| `source-proot-5.1.107.102.zip.bin`, `source-talloc-2.5.0.tar.gz.bin`, `source-libandroid-shmem-0.7.tar.gz.bin` | corresponding source | [section 6](#6-source-code-for-the-gpl-and-lgpl-parts) |
| [fonts.md](licenses/fonts.md) | copyright lines of the fonts | section 3.5 |

## 2. The licence of MetaCodex itself

**Decision: MIT.** `LICENSE` at the repository root is the MIT licence, "Copyright (c) 2026 the MetaCodex authors",
with the copyright line of the upstream MQH author next to ours ("Copyright (c) 2026 Michael Yong", the `mqh/` tree is
a fork of bb, MIT). There is no owner name in the repository besides the git author handle (SolidityDrone, 133
commits, github.com/SolidityDrone/MetaCodex); the owner may put his legal name in the first line.

**What the MIT licence covers:** the code and documents written for MetaCodex: `unity/Assets/MetaCodex` (C#, HLSL
shaders; not its `Fonts` folder), the Java classes in `unity/Assets/Plugins/Android`, `userland/` (scripts, Core v0,
build tools; not `userland/agent-skills`), `native/`, `tools/`, `tests/`, `local-ai/server`, `docs/` (not `docs/licenses` and
not `docs/diagrams/fonts`), and our changes to MQH. Written by the owner with AI coding assistants (git history is the
record); no third-party code was found copied into these files (searched for "adapted from", "based on",
"copied from", copyright and licence headers; the only hits are fonts and MQH, listed below).

**What it does not cover** (third-party files that are in the repository; the `LICENSE` file lists them too):

| In the repository | Licence | Why it is excluded |
|---|---|---|
| `unity/Assets/TextMesh Pro/**`, `Assets/Settings/**`, `Assets/XR/**`, `Assets/CompositionLayers/**`, `Assets/Scenes/SampleScene.unity`, `Assets/InputSystem_Actions.inputactions` | Unity Companion License | output of Unity's URP template and package importers; a derivative of a UCL "Work" belongs to Unity (UCL section 3.2) |
| `unity/Assets/Oculus/`, `Assets/Resources/{OculusRuntimeSettings,OVRBuildConfig,ImmersiveDebuggerSettings,DevAgentSettings}.asset` | Meta SDK terms | settings generated by the Meta SDK |
| `unity/ProjectSettings/**` | Unity-generated | our values in Unity's format; treated as configuration data, no claim made |
| `unity/Assets/Plugins/Android/libs/arm64-v8a/lib{proot,proot-loader,talloc,android-shmem}.so`, `Assets/Plugins/MoonSharp/**` | GPL-2.0+, LGPL-3.0+, BSD-3-Clause | third-party binaries, [section 6](#6-source-code-for-the-gpl-and-lgpl-parts) |
| `unity/Assets/MetaCodex/Fonts/**`, `unity/Assets/TextMesh Pro/Fonts/**`, `docs/diagrams/fonts/**` | OFL-1.1, Bitstream Vera | fonts |
| `userland/agent-skills/unity/**` (36 skills) | Unity Companion License | Unity Technologies' skills; use only together with Unity |
| `userland/agent-skills/meta/**` (7 skills) | Apache-2.0 | Meta's agentic-tools |
| `mqh/**` except our changes | MIT, Copyright (c) 2026 Michael Yong | fork of bb; `mqh/LICENSE` and `mqh/UPSTREAM.md` stay |
| `mqh/plugins/*/icons/*.svg` (52 files; 50 of them are in the APK) | **unknown**, see gap G10 | logos of third-party agents and generic icons that came with bb |
| `.gitattributes` | MIT | from the gitattributes/gitattributes Unity template |
| `native/xr/mcx_font.h` (also in the overlay) | Bitstream Vera (derived from DejaVu Sans Mono) | rasterised font data |

**Why MIT is possible, step by step.**

1. *Nothing in the repository forces a copyleft licence on our code.* Copyleft (GPL-2.0, LGPL-3.0) applies to the
   programs under it and to works that link to them or derive from them. Ours do neither:
   - **PRoot (GPL-2.0-or-later)** is a separate executable: `LinuxHost.ProotArgv` builds a command line, the app starts
     `libproot.so` as a child process in a pty (and `MqhHost` hands the same line to the AI service process), and they
     talk through arguments, environment, files and the terminal. No MetaCodex code is linked into it, and none of
     PRoot is linked into the app. GPL-2.0 section 2 (last paragraph) says that mere aggregation of another work on the
     same storage or distribution medium does not bring it under the licence. The same holds for the Debian packages in
     the rootfs tarball (the tarball is data, its programs run as separate processes) and for the Bare runtime.
   - **talloc (LGPL-3.0-or-later)** is a shared library used only by PRoot (`libproot.so` needs `libtalloc.so`). Our
     code does not call it. It is replaceable (a separate file, [source and licence supplied](#6-source-code-for-the-gpl-and-lgpl-parts)).
     PRoot is GPL-2.0-or-later, which may be combined with an LGPL-3.0 library; that combination concerns PRoot only.
   - Measured with `readelf -d`: `libmcxpty.so` (ours) needs only `libc` and `libdl`; `libproot.so` needs `libtalloc.so`,
     `libandroid-shmem.so` (BSD-3-Clause) and `libc`; `libbare.so` needs `libc`, `libm`, `libdl`, `liblog`. No Unity, Meta or
     MetaCodex library is linked to a GPL or LGPL one.
2. *The proprietary parts do not forbid it.* The Unity Companion License (v1.4, section 1) allows use and distribution
   of the packages "in connection with the authoring and/or distribution of applications ... under a valid Unity ...
   Engine License"; MetaCodex is such an application and the repository carries no package source (`Packages/` has
   only `manifest.json` and the lock; no embedded or modified package). Section 3.2 assigns to Unity only changes to
   the Work itself; apps made with it stay ours. The Meta SDK terms (section 1.2.8) forbid redistributing the SDK so
   that it becomes subject to an open-source licence: the SDK is not in the repository (it comes from Meta's registry
   at build time), and its binaries in the APK stay under Meta's terms; MIT does not touch them.
3. *MQH is MIT upstream* (bb, Copyright (c) 2026 Michael Yong), so the fork may carry our changes under MIT with his
   notice kept: `mqh/LICENSE` is unchanged and ships in the APK (`/opt/mqh/LICENSE`); the root `LICENSE` names him too.
4. *The AI models are not in the repository nor the APK* (downloaded by the user's device at first use), so the
   OpenRAIL-M use restrictions bind the model and its users, not our source code; they are passed on in the app.
5. *What MIT does not give:* the **APK is an aggregate** of components under the licences listed here, not a single MIT
   work. Whoever redistributes the APK must follow every one of them (source offer for GPL/LGPL, the Meta SDK terms,
   Unity's terms). The repository also tracks a few third-party binaries (PRoot, talloc, libandroid-shmem, MoonSharp):
   publishing them is allowed, but their notices and the source offer must stay with them (done: sources are in
   `docs/licenses/`). To keep the "MIT" repository cleaner the owner could stop tracking those binaries and fetch them with
   `userland/fetch-proot.sh` instead (optional, no licence need).

**Conflicts with publishing the code under MIT: none that blocks it.** What must be handled (all listed in
[section 8](#8-gaps)): the Unity-made files and the skills under the Unity Companion License stay under it and need a valid
Unity licence to be used (G11); the 50 third-party icons of MQH are of unknown licence and should leave the APK and
the repository (G10); a written-offer contact address is needed for the GPL parts (G2).

**`local-ai/package.json` says `"license": "Apache-2.0"`.** Recommendation: change it to `"MIT"` (it is `private: true`,
the service `local-ai/server/*.js` is our code, and it contains no Tether or Holepunch code: it only `require`s their
packages). Apache-2.0 for our own service would also be legal, but it would give one project two licences for no gain
and make the Apache NOTICE duty look like ours. The Apache-2.0 packages it depends on (`@qvac/*`, `bare-*`) keep
their licence; they are separate packages. The file also names the licence of the shipped bundle's own package
(`metacodex-ai 0.1.0`, listed as ours in the QVAC set below). Not edited here: the change belongs to the next
`local-ai-bundle.sh` run.

## 3. Group A: shipped inside the APK

### 3.1 Unity engine and packages

The engine runtime is the owner's Unity 6000.4.5f1 under the Unity Terms of Service and Software Additional Terms.
Unity's own third-party components (FMOD, ogg/vorbis, zlib/libpng, brotli, LZ4, libtess2, Recast/Detour, ...) are
named in [Unity's notice file for this Editor](licenses/Unity-6000.4.5f1-third-party-notices.txt) (the Editor's whole
list; which of them end up in `libunity.so` cannot be told from outside). In the dex files the player's Java classes
(`com.unity3d.player`, `org.fmod`, `bitter.jnibridge`, R8 annotations) are Unity's.

Assemblies measured in the build's metadata (105 names): ours is `Assembly-CSharp`; the rest are Unity modules and the
packages below, the Meta SDK, MoonSharp, Newtonsoft.Json, and Unity's Mono class libraries (`mscorlib`, `System.*`,
`Mono.Security`: MIT/X11, in Unity's notice file).

| Package (UPM) | Version | Licence | Copyright | Notices inside the package (its `Third Party Notices.md`) | In the APK as |
|---|---|---|---|---|---|
| com.unity.render-pipelines.universal | 17.4.0 | UCL v1.4 | Unity Technologies ApS | FXAA3_11 (NVIDIA licence) | shaders, IL2CPP code |
| com.unity.render-pipelines.core | 17.4.0 | UCL | Unity Technologies ApS | RadeonRays 4.1 (MIT, AMD), Bullet Physics SDK (zlib), Sobol sampler (MIT, L. Gruenschloss) | same |
| com.unity.render-pipelines.universal-config | 17.4.0 | UCL | Unity Technologies ApS | none | same |
| com.unity.shadergraph | 17.4.0 | UCL | Unity Technologies ApS | none | shaders |
| com.unity.inputsystem | 1.19.0 | UCL | Unity Technologies | none | managed |
| com.unity.ugui (with TextMesh Pro 5.0.0) | 2.0.0 | UCL | Unity Technologies ApS | none | managed, TMP shaders |
| com.unity.textmeshpro | 5.0.0 | UCL | Unity Technologies | none (a stub; TMP is in ugui) | |
| com.unity.burst | 1.8.29 | UCL for the source, otherwise UPDL | Unity Technologies | LLVM (Apache-2.0 with LLVM exceptions, legacy NCSA), Mono.Cecil (MIT), xxHash/Smash (BSD-2), musl (MIT), SLEEF (BSL-1.0), gRPC for .NET (Apache-2.0), Google.Protobuf (BSD-3) | `lib_burst_generated.so` |
| com.unity.collections | 6.4.0 | UCL | Unity Technologies | none | managed |
| com.unity.mathematics | 1.3.3 | UCL | Unity Technologies ApS | none | managed |
| com.unity.nuget.newtonsoft-json | 3.2.2 | UCL for the package; Newtonsoft.Json MIT (Copyright (c) 2007 James Newton-King) | Unity / J. Newton-King | Json.Net.Unity3D (MIT, SaladLab), Newtonsoft.Json-for-Unity (MIT, K. Jillheden), com.newtonsoft.json (MIT, M. Wuetherick) | `Newtonsoft.Json.dll` |
| com.unity.ai.navigation | 2.0.12 | UCL | Unity Technologies ApS | none | managed |
| com.unity.xr.management | 4.7.0 | UCL | Unity Technologies ApS | none | managed |
| com.unity.xr.openxr | 1.18.0 | UCL source, otherwise UPDL | Unity Technologies ApS | OpenXR-SDK-Source (Apache-2.0, Khronos), Oculus OpenXR Mobile SDK (Oculus SDK License) | managed, `libUnityOpenXR.so`, `libopenxr_loader.so` |
| com.unity.xr.meta-openxr | 2.6.1 | UCL source, otherwise UPDL | Unity Technologies | OpenXR-SDK-Source (Apache-2.0), Oculus OpenXR Mobile SDK + AudioPluginOculusSpatializer (Oculus SDK License, (c) Facebook Technologies) | managed, `libUnityARFoundationMeta.so` |
| com.unity.xr.arfoundation | 6.6.0 | UCL | Unity Technologies | none | managed |
| com.unity.xr.hands | 1.7.3 | [UPDL](licenses/Unity-Package-Distribution-License.txt) | Unity Technologies | OpenXR-SDK-Source (Apache-2.0), Oculus OpenXR Mobile SDK (Oculus SDK License) | managed, `libUnityOpenXRHands.so` |
| com.unity.xr.compositionlayers | 2.4.0 | UCL source, otherwise UPDL | Unity Technologies ApS | none | managed |
| com.unity.xr.core-utils | 2.6.0 | UCL | Unity Technologies | none | managed |
| com.unity.xr.legacyinputhelpers | 3.0.1 | UCL | Unity Technologies | ArmModel / SwingArmModel / TransitionArmModel (Apache-2.0) | managed |
| 36 built-in modules `com.unity.modules.*` | 1.0.0 | part of the engine | Unity | | inside `libunity.so` |

UCL = [Unity Companion License v1.4](licenses/Unity-Companion-License-v1.4.txt) (the packages name the licence by URL; the URL serves v1.4, 10.29.2024; "Unity Companion Use": only together
with content under a valid Unity engine licence; section 5: the licence and copyright notice travel with substantial
portions and the packages' third-party notices bind). The full `LICENSE.md` and third-party notices of the packages are
in [Unity-packages-LICENSE-and-third-party-notices.txt](licenses/Unity-packages-LICENSE-and-third-party-notices.txt).
**Editor only** (checked: none of their assemblies is in the build's metadata): com.unity.pipeline 0.8.0-exp.1 (Unity
Terms of Service), com.unity.test-framework 1.6.0 and .performance 3.4.0 (UCL), com.unity.ext.nunit 2.0.5 (UPDL),
com.unity.ide.visualstudio 2.0.27 (MIT, Microsoft), com.unity.editorcoroutines 1.1.0 (UCL), com.unity.nuget.mono-cecil
1.11.6 (UCL), com.unity.searcher 4.9.4 (Unity Companion Package License v1.0).

**Obligations:** keep the licence and notices (done: the two files above ship in `/usr/share/doc/metacodex/licenses/`);
the app must be made under a valid Unity licence (owner's subscription: keep the proof, gap G11).

### 3.2 Meta XR SDK 205.0.0

`com.meta.xr.sdk.core`, `.interaction`, `.interaction.ovr`, `com.meta.xr.mrutilitykit`, all 205.0.0, "Copyright (c)
Meta Platforms, Inc. and affiliates. All rights reserved", [Meta Platform Technologies SDK License
Agreement](licenses/Meta-Platform-Technologies-SDK-License.txt) (formerly the Oculus SDK License Agreement, effective
2022-10-25; https://developers.meta.com/horizon/licenses/oculussdk/). Terms that matter here: use only for apps for
Meta-approved hardware (section 1.1); may distribute the parts meant for incorporation inside the app (1.1.1); no
derivative works of the SDK, no reverse engineering (1.2.1, 1.2.4); **do not redistribute the SDK so that it becomes
subject to an open-source licence** (1.2.8); keep the terms and notices (1.2.7, 1.3). We comply by never putting the
SDK in the repository (registry dependency) and by shipping its binaries only in the APK.

| In the APK | Licence of the part | Origin |
|---|---|---|
| `libOVRPlugin.so`, `libOVRMetricsTool.so`, `libloaderimpl.so`; `assets/lib/{arm64-v8a,armeabi-v7a}/`: `libxplat_perflogger_*` (9 each), `libxplat_secure_lib_secure_string.so`, `libxplat_folly_logging_logging.so`, `libdouble-conversion.so`, `libxplat_third-party_zstd__zstd.so` | Meta SDK terms; inside: folly (Apache-2.0), double-conversion (BSD-3-Clause), zstd (BSD-3-Clause or GPL-2.0: used as BSD) | `OVRPlugin.aar` (core) |
| `libopenxr_loader.so` | Apache-2.0, Khronos | `OVRPlugin.aar` / Unity's `openxr_loader.aar` |
| `LibRuntimeOptimizer.so` | Meta SDK terms | `RuntimeOptimizer_Plugin.aar` (core) |
| `libXrApiLayer_METAX_operator.so`, `assets/openxr/1/api_layers/implicit.d/XrApiLayer_METAX_operator.json` | Meta SDK terms | an implicit OpenXR API layer ("agentic interface") of the core package |
| `libInteractionSdk.so`, `libISDKEngineTelemetry.so` | Meta SDK terms | `com.meta.xr.sdk.interaction` |
| `libmrutilitykitshared.so` | Meta SDK terms; notices: Boost-1.0, ISC, nlohmann/json (MIT), Protocol Buffers (BSD-3), folly, earcut.hpp (ISC), Unity built-in shaders | `com.meta.xr.mrutilitykit` |
| managed code: `Oculus.VR`, `Oculus.Interaction(.OVR)`, `Meta.XR.*` (BuildingBlocks, EnvironmentDepth, ImmersiveDebugger, MRUtilityKit, ai.agentbridge, ai.mcpbridge), the Java classes `com.oculus.*`, `com.meta.agenticxr` | Meta SDK terms | the four packages |
| `SimpleJSON` (MIT, (c) 2012-2017 Markus Goebel); `OVRGLTFLoader` / `OVRGLTFAccessor` parts (MIT, (c) 2017 AltspaceVR) | MIT | `com.meta.xr.sdk.core` (`ThirdParty/`, `Scripts/`); the folder's `openvr_api.cs` (Valve, BSD-3) is not in the build (no `Valve.VR` type in the metadata) |

The SDK's NOTICE files are in [Meta-XR-SDK-core-NOTICE.txt](licenses/Meta-XR-SDK-core-NOTICE.txt) and
[Meta-XR-SDK-mrutilitykit-NOTICE.txt](licenses/Meta-XR-SDK-mrutilitykit-NOTICE.txt), the package licence in
[Meta-XR-SDK-package-LICENSE.md](licenses/Meta-XR-SDK-package-LICENSE.md). Not a licence matter, noted for honesty in
privacy statements: the SDK's own telemetry and performance-logging libraries (`libISDKEngineTelemetry.so`, the
`perflogger` set) ship with every app built on it.

### 3.3 Native libraries and runtimes (all 22 files of `lib/arm64-v8a`)

| File | What | Version | Licence (SPDX) | Copyright / author | Source | Obligations | Text |
|---|---|---|---|---|---|---|---|
| `libunity.so`, `libmain.so`, `libil2cpp.so`, `lib_burst_generated.so` | Unity player, IL2CPP runtime with all managed code, Burst output | 6000.4.5f1 | Unity ToS | Unity Technologies | the owner's Editor | Unity's terms | [Unity notices](licenses/Unity-6000.4.5f1-third-party-notices.txt) |
| `libUnityOpenXR.so`, `libUnityARFoundationMeta.so`, `libUnityOpenXRHands.so` | Unity XR plug-ins | see 3.1 | UCL / UPDL | Unity | UPM | notices | 3.1 |
| 8 Meta libraries | see 3.2 | 205.0.0 | Meta SDK terms | Meta Platforms | Meta registry | see 3.2 | [terms](licenses/Meta-Platform-Technologies-SDK-License.txt) |
| `libopenxr_loader.so` | OpenXR loader | from OVRPlugin | Apache-2.0 | The Khronos Group Inc. | github.com/KhronosGroup/OpenXR-SDK-Source | licence | [Apache-2.0](licenses/Apache-2.0.txt) |
| `libproot.so`, `libproot-loader.so` | PRoot, Termux build (`bin/proot`, `libexec/proot/loader`), renamed; `DT_NEEDED libtalloc.so.2` changed to `libtalloc.so` and RUNPATH removed with patchelf, otherwise unmodified | 5.1.107.102 | **GPL-2.0-or-later** | STMicroelectronics and the PRoot authors (C. Vincent et al., see COPYING); Termux changes | github.com/termux/proot (tag v5.1.107.102); Termux package `proot_5.1.107.102_aarch64.deb` sha256 `10a394b585c2fc2b2c8502544f4e5f1be581dc8bfff79e8ba62f16207423e030` | licence text; **source offer** | [GPL-2.0](licenses/GPL-2.0.txt), [proot COPYING](licenses/proot-COPYING.txt), [source](licenses/source-proot-5.1.107.102.zip.bin) |
| `libtalloc.so` | talloc, hierarchical memory allocator, soname changed | 2.5.0 | **LGPL-3.0-or-later** | Andrew Tridgell, Stefan Metzmacher, the Samba team | talloc.samba.org, `talloc-2.5.0.tar.gz` sha256 `912afa23...b007`; Termux `libtalloc_2.5.0_aarch64.deb` sha256 `556591f43bb773ad8777e1a29522640866a55f95dab71914418b94a8c58ad5a7` | licence texts; **source offer**; replaceable library | [LGPL-3.0](licenses/LGPL-3.0.txt), [GPL-3.0](licenses/GPL-3.0.txt), [talloc LICENSE](licenses/talloc-LICENSE.txt), [source](licenses/source-talloc-2.5.0.tar.gz.bin) |
| `libandroid-shmem.so` | System V shared memory on ashmem, PRoot's dependency | 0.7 | BSD-3-Clause | (c) 2013 Sergii Pylypenko; (c) 2017 Fredrik Fornwall | github.com/termux/libandroid-shmem v0.7; Termux `libandroid-shmem_0.7_aarch64.deb` sha256 `0da3a24d558b93c92bcf8d611e0826a99ff96e396b148e6cdf33b47c47c57ff6` | notice | [licence](licenses/libandroid-shmem-BSD-3-Clause.txt), [source](licenses/source-libandroid-shmem-0.7.tar.gz.bin) |
| `libbare.so` | **Bare** JavaScript runtime executable (stripped, the AI service's host); embeds V8, libuv 1.53.0 (Bare's CMake pin), BoringSSL, ICU, abseil-cpp, perfetto (found by `strings`) | 1.36.0 (`bare-runtime-android-arm64@1.36.0`, `bin/bare` sha256 `4e6506e5...9957` before strip) | Apache-2.0 | Holepunch Inc | npm `bare-runtime-android-arm64`; github.com/holepunchto/bare-runtime | Apache licence + NOTICE; embedded notices below | [Apache-2.0](licenses/Apache-2.0.txt), [V8](licenses/V8-LICENSE.txt) (BSD-3-Clause, (c) the V8 project authors), [libuv](licenses/libuv-LICENSE.txt) (MIT, (c) libuv project and Joyent), [BoringSSL](licenses/BoringSSL-LICENSE.txt) (OpenSSL + SSLeay + ISC), [ICU](licenses/ICU-LICENSE.txt) (Unicode-3.0, (c) Unicode, Inc.) |
| `libmcxpty.so` | our pty helper (`native/pty`) | ours | MIT | the MetaCodex authors | this repository | none | [MIT](licenses/MIT.txt) |

The Bare package ships the NOTICE "Copyright 2022 Holepunch Inc" (Apache-2.0); the embedded libraries have the
licences named above. abseil-cpp and perfetto (V8's third-party code) are Apache-2.0.

### 3.4 Managed libraries, Java

- **MoonSharp 2.0.0.0** (Lua interpreter in C#, used by the in-session behaviour scripts): BSD-3-Clause, Copyright (c)
  2014-2016 Marco Mastropaolo; parts of the string library from KopiLua (Copyright (c) 2012 LoDC); the debugger code
  is based on Microsoft's vscode-mono-debug (MIT). NuGet `MoonSharp` 2.0.0, `lib/net40-client` (nupkg sha256
  `6be871a1...e060`), `Assets/Plugins/MoonSharp/MoonSharp.Interpreter.dll` compiled into `libil2cpp.so`. Duty: reproduce the
  notice. Text: [MoonSharp-LICENSE.txt](licenses/MoonSharp-LICENSE.txt).
- Newtonsoft.Json (MIT), SimpleJSON (MIT): see 3.1 and 3.2.
- Java classes of ours: `MetaCodexVault`, `MetaCodexInstaller`, `MetaCodexAiService`, `MetaCodexReturn`,
  `MetaCodexWebView` (package `com.pnncl.metacodex`). No Gradle dependencies (no androidx, no Maven libraries): the
  dex files hold Unity's (`com.unity3d.player`, `org.fmod`, `bitter.jnibridge`, `com.UnityTechnologies.XR`), Meta's (`com.oculus.*`, `com.meta.agenticxr`), R8 annotations and our classes only (class lists read from the dex files).
- **Launcher icon:** `res/mipmap-*/app_icon.png`, `ic_launcher_foreground.png` are Unity's default icon, the Unity
  logo (gap G5).

### 3.5 Fonts (8 in the build; 3 more only in the repository)

Checked against the APK's data (font names in the assets) and each font's name table. All unmodified; MetaCodex builds
TextMesh Pro SDF atlases from them at run time. Full details: [fonts.md](licenses/fonts.md).

| Font | Version | Copyright (verbatim from the font) | Licence | Text |
|---|---|---|---|---|
| Inter Medium | 4.001 | Copyright 2016 The Inter Project Authors | [OFL-1.1](licenses/OFL-1.1.txt) | `unity/Assets/MetaCodex/Fonts/Inter-OFL.txt` |
| Rajdhani SemiBold | 1.201 | Copyright (c) 2014 Indian Type Foundry | OFL-1.1 | `.../Rajdhani-OFL.txt` |
| DejaVu Sans Mono | 2.37 | Copyright (c) 2003 by Bitstream, Inc.; DejaVu changes public domain | Bitstream Vera licence | `.../DejaVu-LICENSE.txt` |
| DejaVu Sans | 2.37 | Copyright (c) 2003 by Bitstream, Inc.; (c) 2006 by Tavmjong Bah (Arev); DejaVu changes public domain | Bitstream Vera + Arev licences | `.../DejaVu-LICENSE.txt` |
| Noto Sans Symbols | 2.003 | Copyright 2022 The Noto Project Authors | OFL-1.1 | `.../Noto-OFL.txt` |
| Noto Sans Symbols 2 | 2.008 | Copyright 2022 The Noto Project Authors | OFL-1.1 | `.../Noto-OFL.txt` |
| Noto Emoji | 3.002 | Copyright 2013 Google LLC | OFL-1.1 | `.../Noto-OFL.txt` |
| Liberation Sans (TextMesh Pro default) | 2.00.1 | Digitized data copyright (c) 2010 Google Corporation; Copyright (c) 2012 Red Hat, Inc. | OFL-1.1 | `unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` |

The OFL texts ship on the headset in `/usr/share/doc/metacodex/licenses/fonts/`; the OFL itself is [here](licenses/OFL-1.1.txt).
`native/xr/mcx_font.h` is a rasterised DejaVu Sans Mono atlas (ASCII) used by the native-app template; it is derived
from the Bitstream Vera licensed font and must carry that notice (gap G12).

### 3.6 The Linux image: Debian 13 "trixie" (`assets/linux/rootfs.tar`)

The official `debian:trixie-slim` image for linux/arm64, unmodified: manifest digest
`sha256:24a01011d10eba3a023bc8585b9650b8b29021240d4217c05b905e09ef4a5232`, layer
`sha256:acd43e9bebc37bc4ba7a5f47bd7598913337803e53d468f37d940f11f5d5f1ef` (30,214,037 bytes; 3,266 entries; fetched
2026-11-06 by `userland/fetch-rootfs.sh`; the APK packs it as plain tar). It holds **78 Debian packages** from **55
source packages** (list in [Appendix A](#appendix-a-debian-packages-in-the-linux-image)); 64 of them name GPL or LGPL
licences in their copyright file, 9 are permissive only, 5 have free-text files (debian-archive-keyring, gcc-14-base,
libcrypt1 and, by link, libgcc-s1, libstdc++6). Licences: GPL-2/GPL-3 (+), LGPL-2/2.1/3, BSD (2/3/4 clause and
variants), MIT/Expat, X11, ISC, Zlib, Apache-2.0 (openssl), public domain, GFDL (docs), Artistic/GPL (perl),
Sleepycat (libdb) and others, each as its own `copyright` file states. Texts: all 78 copyright files in
[debian-image-copyright.txt](licenses/debian-image-copyright.txt); the standard texts are also in the image
(`/usr/share/common-licenses/`) and in this folder ([GPL-2.0](licenses/GPL-2.0.txt), [GPL-3.0](licenses/GPL-3.0.txt),
[LGPL-2.1](licenses/LGPL-2.1.txt), [LGPL-3.0](licenses/LGPL-3.0.txt), [Apache-2.0](licenses/Apache-2.0.txt),
[MPL-2.0](licenses/MPL-2.0.txt)). Obligation: **source offer** for the GPL/LGPL ones: [section 6](#6-source-code-for-the-gpl-and-lgpl-parts).

### 3.7 Overlay (`assets/linux/overlay.tar`, 489 files)

Our files laid over the image (scripts `mcx`, `mcx-setup`, `mcx-ui`, `mcx-secret`, `mcx-ai`, `mcx-app`, `mcx-core`,
`mcx-build(d)`, `mcx-project`; Core v0; the Quest native template; our 4 skills), plus:

| Bundled | Source | Licence | Licence file shipped |
|---|---|---|---|
| 36 Unity agent skills | github.com/Unity-Technologies/skills @ `60f95e6d4beaeacf4dfdff7829ec9b35008a0be0` (2026-11-06) | Unity Companion License ("Unity Skills (c) 2026 Unity Technologies") | `/root/.claude/skills/THIRD-PARTY/LICENSE-unity-skills.md` ([copy](licenses/Unity-skills-LICENSE.md)) |
| 7 Meta agent skills (hz-*) | github.com/meta-quest/agentic-tools (via the skill set's vendor lock) | Apache-2.0 | `/root/.claude/skills/THIRD-PARTY/LICENSE-meta-agentic-tools` ([copy](licenses/Meta-agentic-tools-Apache-2.0-LICENSE.txt)) |
| cgltf 1.15 (`/opt/metacodex/quest-native/xr/third_party/cgltf.h`, used by the native kit `mcx_model.h`; compiled into a native app only when the app loads glTF models) | github.com/jkuhlmann/cgltf, tag `v1.15`, commit `360db1a95480fe102ae9c69b27c5d101167ff5ba`, sha256 `e378a21c...c6ec` | MIT ("Copyright (c) 2018-2021 Johannes Kuhlmann") | the notice at the end of the file; versions in `third_party/README.md` |
| stb_image 2.32 (`/opt/metacodex/quest-native/xr/third_party/stb_image.h`, the same use) | github.com/nothings/stb, commit `9f755b604db3492df2cbeb39bf84b81e3e3932e3`, sha256 `deb4f18c...6e91` | public domain or MIT (the file's ALTERNATIVE A / B) | the licence text at the end of the file |
| this file and the texts in `docs/licenses/` | this repository | | `/usr/share/doc/metacodex/` |

`userland/agent-skills/SOURCES.md` lists every skill. Our own skills (`metacodex-context`, `quest-gesture-plugins`,
`quest-scripting`, `quest-views`) are MIT, and so are the quest-native-* skills and the native kits (`mcx_gfx.h`,
`mcx_ui.h`, `mcx_physics.h`, `mcx_model.h`, 2026-11-14).

### 3.8 MQH, the Meta Quest Harness (`assets/linux/mqh.tar`, 7,481 files, 70,174,720 bytes unpacked)

In the APK now (the 2026-11-10 version of this file, for APK 0.7.8, said it was repository-only). MQH is a trimmed hard fork
of bb (github.com/get-bb/bb) at `8e4208e` (2026-11-06): **MIT, Copyright (c) 2026 Michael Yong** ([text, as in
`mqh/LICENSE`](licenses/MQH-bb-MIT-LICENSE.txt); `UPSTREAM.md`). Our changes are MIT. It runs on the headset under Node.
What is in the tar (read from `opt/mqh/`):

- the server, host daemon and CLI bundles and the bundled plugins (esbuild output, `apps/*/dist`,
  `packages/bundled-plugins/dist`), plus 5 builtin skills in the `metacodex-guide` plugin (`skill-creator`, adapted from
  bb's; `metacodex-context`, `quest-views`, `quest-scripting`, `quest-gesture-plugins` ours). Since 2026-11-13 bb's
  `bb-cli`, `bb-plugin-authoring` and `submit-a-plugin` skills and 10 bb plugins are out of `mqh/` (`docs/mqh.md`, "Plugins");
- **97 npm packages bundled into those files** (found from the build's source maps and path comments; the files carry
  no licence comments, so their notices are in [npm-mqh-bundled-notices.txt](licenses/npm-mqh-bundled-notices.txt)):
  MIT 79, ISC 8, Apache-2.0 5 (`@modelcontextprotocol/core`, `@modelcontextprotocol/server`, `@opentelemetry/api`,
  `drizzle-orm`, `human-signals`), BSD-3-Clause 4 (`fast-uri`, `fzf`, `smol-toml`, `source-map`), BlueOak-1.0.0 1 (`tar`);
- **72 npm packages in `opt/mqh/node_modules`** (the logger, the SQLite binding, pty, the file watcher and their
  dependencies; [npm-mqh-runtime-notices.txt](licenses/npm-mqh-runtime-notices.txt)): MIT 59, ISC 7, BSD-3-Clause 2,
  Apache-2.0 2, (MIT OR WTFPL) 1, (BSD-2-Clause OR MIT OR Apache-2.0) 1;
- **4 native modules** (prebuilt linux-arm64): `better_sqlite3.node` (better-sqlite3 12.11.1, MIT, (c) 2017 Joshua Wise;
  its release build for Node 24, sha256-pinned in `tools/mqh-bundle.sh`; SQLite itself is public domain),
  `pty.node` (node-pty 1.2.0-beta.15, MIT, (c) 2012-2015 Christopher Jeffrey, (c) 2016 Daniel Imms), `watcher.node` twice
  (@parcel/watcher 2.6.0, MIT, (c) 2017-present Devon Govett; the glibc and the musl build).
- **Not in the APK** (verified: no `claude-agent-sdk`, no `provider-claude-code`; `provider-pi` is out of `mqh/`): Anthropic's Agent
  SDK, which carries a Claude Code binary (proprietary).
- **49 SVG icons** inside the bundled plugins (47 in `provider-acp/icons`, 2 in `provider-codex/icons`; the
  `storage-retention` plugin and its icon are out of `mqh/` since 2026-11-13): logos of third-party agents and generic icons inherited from bb, of **unknown licence and
  provenance**, trademarks of their owners (gap G10).

### 3.9 The local AI runtime (`assets/qvac/runtime.tar`, 489 files, 62,791,511 bytes unpacked)

The service `metacodex-ai 0.1.0` (`local-ai/server`, ours, MIT) on Bare 1.36.0, built 2026-11-11 by `tools/local-ai-bundle.sh`,
with **133 third-party npm packages** (listed in [Appendix B](#appendix-b-npm-packages-in-the-apk)):
Apache-2.0 83, MIT 45, ISC 5. 15 are named by `local-ai/package.json` (`@qvac/asr-ggml` 0.12.0, `@qvac/embed-llamacpp`
0.55.0, `@qvac/fabric` 0.26.0, `@qvac/llm-llamacpp` 0.68.0, `@qvac/registry-client` 0.8.0, `@qvac/tts-ggml` 0.15.0 and
their android-arm64 builds, `bare-fs`, `bare-http1`, `bare-https`, `bare-os`, `bare-path`, `sodium-native`), the rest
are their dependencies. The `@qvac/*` packages are "Copyright 2026 Tether Data, S.A. de C.V.", Apache-2.0; the
`bare-*`, Hyper* and related packages are Holepunch's, Apache-2.0 or MIT. Notices ([npm-qvac-runtime-notices.txt](licenses/npm-qvac-runtime-notices.txt))
reproduce every `LICENSE` and `NOTICE` file of the tar (35 distinct texts).

Native code in the tar (prebuilt, `.bare` addons and `.so`), with the licences their NOTICE files and the embedded
paths show:

| Component | Where | Licence | Note |
|---|---|---|---|
| qvac-fabric (a llama.cpp + ggml fork) | `@qvac/fabric-android-arm64` (+ 3 CPU-variant `.so`) | MIT, (c) The ggml authors / Tether | [text](licenses/whisper.cpp-ggml-MIT-LICENSE.txt) (same authors) |
| speech-cpp (a whisper.cpp fork), ggml-speech | `@qvac/asr-ggml-*`, `@qvac/tts-ggml-*` (+ 3 CPU-variant `.so` each) | MIT, (c) The ggml authors / Tether | |
| qvac-lib-inference-addon-cpp | the engine addons | Apache-2.0, Tether | |
| OpenCL ICD loader | `@qvac/fabric` NOTICE | Apache-2.0, Khronos | |
| libc++_shared.so (NDK r29) | `lib/` | Apache-2.0 WITH LLVM-exception | [text](licenses/LLVM-exception.txt); none for binaries |
| BoringSSL | `bare-tls.bare`, `libbare.so` | OpenSSL + SSLeay + ISC | [text](licenses/BoringSSL-LICENSE.txt) |
| RocksDB | `rocksdb-native.bare` | Apache-2.0 (RocksDB is dual-licensed with GPL-2.0; used under Apache-2.0); LevelDB parts BSD-3-Clause | [text](licenses/RocksDB-LevelDB-BSD-3-Clause.txt) |
| libsodium | `sodium-native` | ISC | [text](licenses/libsodium-ISC.txt) |

**Note on QVAC's NOTICE files.** They also list licences of models of the QVAC registry that MetaCodex neither ships
nor downloads (Llama 3.2 with its "Built with Llama" duty, HAI-DEF terms, NVIDIA open model licence, gpt-oss and
others). Those terms bind only whoever distributes those models. MetaCodex's four models are in section 4.1.

## 4. Group B: downloaded on the headset at first use (not in the APK)

### 4.1 The four AI models

The AI service fetches them on request into the app's private storage (VOICE > INSTALL MODELS, 660 MB), pinned by
sha256 in `local-ai/server/models.js`; nothing is redistributed by us inside the APK. Licences read from the model cards on
2026-11-12.

| Role | Exact model | File, sha256 | From | Licence | Copyright | Duties |
|---|---|---|---|---|---|---|
| Speech to text | **Whisper base**, multilingual, ggml q8_0 (OpenAI Whisper weights converted by whisper.cpp) | `ggml-base-q8_0.bin`, `c577b9a8...b7d9` | huggingface.co/ggerganov/whisper.cpp @ `5359861c739e955e79d9a303bcbc70fb988958b1` | **MIT** (the repository's card says MIT; OpenAI's README: "code and model weights are released under the MIT License"; Hugging Face's own copy `openai/whisper-base` is labelled Apache-2.0: either way permissive) | (c) 2022 OpenAI; (c) 2023-2026 The ggml authors | notice: [Whisper](licenses/Whisper-OpenAI-MIT-LICENSE.txt), [whisper.cpp](licenses/whisper.cpp-ggml-MIT-LICENSE.txt) |
| LLM | **Qwen3 0.6B** (hybrid thinking/non-thinking, tool calling), GGUF q4_0 by Unsloth | `Qwen3-0.6B-Q4_0.gguf`, `33bcc570...75cb` | huggingface.co/unsloth/Qwen3-0.6B-GGUF @ `50968a4468ef4233ed78cd7c3de230dd1d61a56b`; base Qwen/Qwen3-0.6B | **Apache-2.0** (cards of both repositories) | Copyright 2024 Alibaba Cloud (Qwen team) | licence + say that the file is a quantised conversion: [text](licenses/Qwen3-Apache-2.0-LICENSE.txt) |
| Local coder (optional, only when the user installs it: AGENTS > LOCAL CODER) | **Qwen2.5-Coder 0.5B Instruct** (tool calling), GGUF q4_0 by the Qwen team | `qwen2.5-coder-0.5b-instruct-q4_0.gguf`, 428,730,240 bytes, `9739055e...91d5` | huggingface.co/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF @ `ebb2015119c907b064c512bf053e945850b5875f`; base Qwen/Qwen2.5-Coder-0.5B-Instruct | **Apache-2.0** (model card) | Copyright 2024 Alibaba Cloud (Qwen team) | Copyright 2024 Alibaba Cloud (Qwen team) | the same licence text as Qwen3 0.6B: [text](licenses/Qwen3-Apache-2.0-LICENSE.txt) |
| Embeddings (voice-command decisions) | **multilingual-e5-small**, GGUF q8_0 by TwinSunsLLC (conversion of intfloat/multilingual-e5-small, which was initialised from microsoft/Multilingual-MiniLM-L12-H384) | `multilingual-e5-small-q8_0.gguf`, `e011debc...7c93` | huggingface.co/TwinSunsLLC/multilingual-e5-small-gguf @ `b6cac9615d4ecce28d7f22539b7322d695fc2886` | **MIT** (both cards: the conversion's and the base model's; neither repository has a licence file, copyright holder not stated in the cards) | Liang Wang et al. (Microsoft) for the base model, as its card credits | keep the notice (this file); [MIT](licenses/MIT.txt) |
| Text to speech | **Supertonic 3**, 31 languages, voices F1-F5 and M1-M5, GGUF q4_0 (QVAC conversion of Supertone/supertonic-3, "2026-06-24") | `supertonic3-q4_0.gguf`, `e3f3edc8...5fb7` | QVAC model registry `qvac_models_compiled/ggml/supertonic/2026-06-24/` (Hyperswarm); upstream huggingface.co/Supertone/supertonic-3 @ `3cadd1ee6394adea1bd021217a0e650ede09a323` | **BigScience OpenRAIL-M** for the weights (card: "released under the OpenRAIL-M License"; sample code MIT); the registry stores no licence metadata for the GGUF itself | Copyright (c) 2026 Supertone Inc. (conversion: Tether) | include the licence ([text](licenses/Supertonic-3-OpenRAIL-M.txt), identical to the upstream `LICENSE`) and **pass the use restrictions on**: below, gap G4 |

**Supertonic 3 use restrictions (OpenRAIL-M, Attachment A), which apply to everyone using MetaCodex's voice.** The
voice may not be used: to violate any law; to exploit or harm minors; to generate or spread verifiably false information
meant to harm others; to generate or spread personal information that can harm someone; to publish machine-generated
content in any context without clearly saying it is machine-generated; to defame, disparage or harass; to impersonate
anyone (e.g. deepfakes) without consent; for fully automated decisions that harm a person's legal rights or create
binding obligations; to discriminate against or harm people for their social behaviour or personal characteristics; to
exploit the vulnerabilities of a group of people to distort their behaviour in a harmful way; or the other uses the
licence lists. The full text is binding, not this summary. The restrictions follow the model and its derivatives, not
our source code (MIT).

### 4.2 What `mcx-setup` downloads (first use, or on request)

| What | Version | From | Licence | Notes |
|---|---|---|---|---|
| Base tools: `ca-certificates curl git xz-utils python3 procps less nano` + dependencies | Debian trixie | Debian mirrors, by `apt` | each package's own; **54 packages** (GPL-2/3, LGPL, MIT/X11, BSD, PSF, ...) | [list with licences](licenses/debian-first-use-packages.md) |
| Quest build tools: `aapt zipalign apksigner default-jdk-headless unzip` + dependencies | Debian trixie (OpenJDK 21, Android tools, the GTK/Mesa/X11 libraries apt pulls in with the JRE) | same | **144 more packages**: OpenJDK GPL-2.0 with Classpath Exception, Android tools Apache-2.0, GTK/Mesa/X11 LGPL/MIT, ... | same list |
| Node.js 24 LTS (linux-arm64) | 24.22.3 when checked (`latest-v24.x`, checked against `SHASUMS256.txt`) | nodejs.org/dist | MIT, plus the notices of its bundled parts (V8, ICU, OpenSSL, ...) in Node's own `LICENSE` | needed by MQH and the agent CLIs |
| Android NDK r27d for arm64 Linux (community build) | r27d | github.com/HomuHomu833/android-ndk-custom, release r27, `android-ndk-r27d-aarch64-linux-gnu.tar.xz`, sha256 `94a7e5a0dffee4ab68cc14adc081674c6f42116046d5ae1eafe1f3492a1045dc` (matches the release's digest) | the build scripts: MIT, Copyright (c) 2025-2026 Lily Ross (checked via the repository's licence file); the NDK's own components keep their licences (LLVM: Apache-2.0 with LLVM exception; Bionic and NDK parts: Apache-2.0 / BSD; the NDK's `NOTICE` files) | Google ships Linux NDKs for x86_64 only |
| android.jar (Android SDK Platform 34 ext7 r03) | API 34 | dl.google.com/android/repository/platform-34-ext7_r03.zip, sha256 `16fdb74c55e59ae3ef52def135aec713508467bd56d7dabcd8c9be31fa8b20f3` | **[Android Software Development Kit License Agreement](licenses/Android-SDK-License-Agreement.txt)** | the user must accept the agreement: gap G6 |
| OpenXR loader for Android + headers | 1.1.65 | Maven Central `org.khronos.openxr:openxr_loader_for_android` (sha256 `a298c326085b09181c85235fb04d37510939bd30938ed4322c093c09780e8643`, re-checked 2026-11-12) | Apache-2.0, The Khronos Group Inc. | its `LICENSE` is kept in `/opt/openxr/LICENSE` |
| Cloud agent CLI (Claude Code) | 2.1.322 when checked | Anthropic's installer (claude.ai/install.sh), npm `@anthropic-ai/claude-code` as fallback | **proprietary**: "(c) Anthropic PBC. All rights reserved. Use is subject to the Legal Agreements" (code.claude.com/docs/en/legal-and-compliance); package.json `SEE LICENSE IN README.md` | **never bundled**; installed by the user's device, used with the user's own account; MetaCodex never touches its login |
| Open agent CLI (OpenCode) | 1.19.5 when checked | npm `opencode-ai` | MIT | |
| QVAC CLI and SDK (development path, `mcx-setup qvac`) | 0.18.0 / 0.23.0 | npm `@qvac/cli`, `@qvac/sdk` | Apache-2.0 | |

### 4.3 What the user chooses to install (SYSTEM > TOOLS, AGENTS)

Each is installed on the user's tap, from its official source, under its own licence; MetaCodex runs the installers in a
visible terminal and redistributes none of them. Catalogue measured in `ShellSystem.cs` and `ShellAgentGet.cs`: pnpm
12.18.0 (MIT, npm), bun (MIT, bun.sh installer), uv (MIT OR Apache-2.0, astral.sh installer), Rust via rustup (MIT OR
Apache-2.0, sh.rustup.rs), the agents Claude Code (proprietary, above), Codex CLI (`@openai/codex` 0.167.2, Apache-2.0)
and OpenCode (above), and Debian packages through `apt` (python3-pip, build-essential, clang, cmake, ninja-build, go,
adb, glslc, git, curl, wget, openssh-client, jq, ripgrep, fd-find, fzf, tmux, sqlite3, nano, vim, neovim). Pi (MIT) was
offered before 2026-11-10 and is no longer.

## 5. Group C: used only to build or develop (not shipped)

| What | Licence | Note |
|---|---|---|
| Unity Editor 6000.4.5f1, Unity Hub | Unity Terms of Service | the owner's installation; its Android player build tools (NDK r27c for `native/pty`, OpenJDK, Gradle) come with it under their licences (see its `NOTICE` files) |
| Unity packages used only in the Editor | see 3.1 | none of their code is in the APK |
| MQH dev tree (`~/mcxwork/mqh-build`, pnpm, 1,012 packages) | MIT 805, Apache-2.0 77 (75 + 2 spelled lower case), ISC 48, BSD-3-Clause 30, BlueOak-1.0.0 13, MPL-2.0 10 (lightningcss and its platform builds), BSD-2-Clause 10, MIT-0 2, and one each of Artistic-2.0 (npm), Python-2.0 (argparse), CC-BY-4.0 (caniuse-lite data), CC0-1.0 (mdn-data), Unlicense (isbot), 0BSD, **LGPL-3.0-only** (rollup-plugin-dts, build time only, not bundled), five dual-licensed, five Anthropic packages "SEE LICENSE IN ..." (`@anthropic-ai/claude-agent-sdk` 0.3.318 and its four platform builds, proprietary; kept out of the APK) | scanned 2026-11-12; of these only the 97 + 72 packages of section 3.8 ship |
| `tools/voicebench` (dev machine): numpy 2.5.1 (BSD-3-Clause), fastembed (Apache-2.0), model2vec (MIT); models only for the comparison: paraphrase-multilingual-MiniLM-L12-v2 (Apache-2.0), multilingual-e5-small (MIT), bge-small-en-v1.5 (MIT), potion-multilingual-128M and potion-base-8M (MIT), **embeddinggemma-300m (Gemma Terms of Use, gated)** | as listed | none ships or is downloaded by the app; the bench never leaves the dev machine |
| Terminal tests: tmux (ISC) run at test time | ISC | not shipped; `tests/terminal/corpus` holds captures of the output of bash, less, nano, vim, top (program output) |
| `docs/diagrams` (created 2026-11-12, may grow; its own table is `docs/diagrams/LICENSES.md`): `kit.css`, `kit.js` (with its hand-drawn inline icons), `render.sh`, `src/*.html` ours; **fonts** `Inter-Medium.ttf`, `Inter-SemiBold.ttf`, `Rajdhani-SemiBold.ttf`, `DejaVuSansMono.ttf` with `Inter-OFL.txt`, `Rajdhani-OFL.txt`, `DejaVu-LICENSE.txt` | OFL-1.1 (Inter, Rajdhani), Bitstream Vera (DejaVu); render with Windows Chrome headless (not redistributed) | the rendered PNGs are pictures made with these fonts (allowed by the OFL); re-check this row when the folder changes |
| Fonts only in the repository: Orbitron 2.001, Inter SemiBold 4.001, Rajdhani Medium 1.201 (`unity/Assets/MetaCodex/Fonts`) | OFL-1.1 | not in the APK |
| `userland/fetch-*.sh` fetch tools: curl, ar, tar, patchelf | their own | run on the dev machine |

The tool logos that SYSTEM > TOOLS showed in APKs 0.7.3-0.7.8 (Simple Icons, Lucide) were removed on 2026-11-10; the
app shows no logo or mark of a third party (the launcher icon is the exception, G5).

## 6. Source code for the GPL and LGPL parts

What is under GPL/LGPL in the APK and how its source is offered. The programs run as separate processes (PRoot, the
Debian programs) or are separate replaceable libraries (talloc, glibc and the other shared libraries of the image); no
MetaCodex code is linked to them.

| Component | Licence | Exact version | Corresponding source | Where it ships |
|---|---|---|---|---|
| PRoot (Termux fork) | GPL-2.0-or-later | 5.1.107.102 (Termux package `proot_5.1.107.102_aarch64.deb`; changed only by renaming and by patchelf: `DT_NEEDED`, RUNPATH) | [source-proot-5.1.107.102.zip.bin](licenses/source-proot-5.1.107.102.zip.bin): github.com/termux/proot, tag `v5.1.107.102`, archive sha256 `30d12a6e2cc4147123756ff58edea262d3083bbc238c12d0ab9b39e3d1c40852` (the value pinned by Termux's `packages/proot/build.sh`) | this repository (`docs/licenses/`), the headset (`/usr/share/doc/metacodex/licenses/`) |
| talloc | LGPL-3.0-or-later | 2.5.0 | [source-talloc-2.5.0.tar.gz.bin](licenses/source-talloc-2.5.0.tar.gz.bin): samba.org/ftp/talloc, sha256 `912afa237510ae542a7733998eb18a12bcda35ab6729c8e2ddb43e8d0ebab007` (Termux's `libtalloc/build.sh`) | same |
| libandroid-shmem (BSD-3, no source duty; supplied anyway) | BSD-3-Clause | 0.7 | [source-libandroid-shmem-0.7.tar.gz.bin](licenses/source-libandroid-shmem-0.7.tar.gz.bin), sha256 `1e5ff8459bc0a8c229dd8a94b27d119987e09ef3414331c2b5ebfff20b98e867` | same |
| 55 Debian source packages (78 binaries of the image) | GPL / LGPL / others | exactly the versions of [Appendix A](#appendix-a-debian-packages-in-the-linux-image) | every source file with its SHA-1 on snapshot.debian.org: [debian-image-sources.txt](licenses/debian-image-sources.txt) (read from the archive's API 2026-11-12: all 55 found, 178 files) | the list ships with the APK files; the files themselves are Debian's, fetched on request |
| Debian packages installed at first use (198) | as above | those of the user's `apt` at that time | `apt-get source <package>` from the same archive | downloaded by the user's device from Debian, not by us |

The three source archives are the upstream files byte for byte (the sha256 values above); they carry an extra `.bin`
suffix only because the repository's `.gitattributes` sends `*.zip` and `*.gz` to Git LFS, and source that must be
available with the binaries should be ordinary git content. Remove `.bin` to use them.

The licence texts that go with them: [GPL-2.0](licenses/GPL-2.0.txt), [GPL-3.0](licenses/GPL-3.0.txt),
[LGPL-2.1](licenses/LGPL-2.1.txt), [LGPL-3.0](licenses/LGPL-3.0.txt) (and, in the image,
`/usr/share/common-licenses/`). LGPL-3.0 section 4: talloc is a separate file (`libtalloc.so`) used by PRoot through
the dynamic loader, so it can be replaced with a modified build of the same source; the "Installation Information" duty of LGPL-3.0 / GPL-3.0 section 6 applies to
"User Products"; the headset lets its owner install any APK in developer mode, MetaCodex puts no restriction on doing so,
and everything needed to rebuild the APK is in this repository (`userland/fetch-proot.sh`, the Unity project).

**Written offer (GPL-2.0 section 3(b); GPL-3.0 section 6, which LGPL-3.0 section 4 applies to talloc).** For three years from the day a
MetaCodex release containing them is made available, anyone who has a copy of the MetaCodex APK may obtain, for no
charge beyond the cost of physically performing the copy, the complete corresponding source code of PRoot 5.1.107.102,
talloc 2.5.0 and of every Debian package named in Appendix A, in the exact versions listed there: from the folder
`docs/licenses/` of https://github.com/SolidityDrone/MetaCodex (the PRoot, talloc and libandroid-shmem archives), from
snapshot.debian.org with the file list `docs/licenses/debian-image-sources.txt` (the Debian packages), or by a request
through the issue tracker of that repository (the contact must be confirmed by the owner: gap G2).

## 7. Obligations checklist

| Obligation | Applies to | Done where | State |
|---|---|---|---|
| Our MIT notice | our code | `LICENSE` (+ `docs/licenses/MetaCodex-MIT-LICENSE.txt` for the headset) | done |
| MIT notice of Michael Yong | MQH | `LICENSE`, `mqh/LICENSE`, `/opt/mqh/LICENSE` in the APK | done |
| Licence text + copyright with binaries: BSD-3 (MoonSharp, libandroid-shmem, V8 ...), MIT (libuv, whisper.cpp ...), ISC, OFL fonts, Bitstream Vera | components in 3.x | `docs/licenses/` (shipped to `/usr/share/doc/metacodex/licenses/`), fonts in `licenses/fonts/` | done in the files; reaches users with the next build (the overlay ships the folder) |
| Apache-2.0: licence + NOTICE retained | Bare, QVAC, Khronos, Meta skills, Qwen3 | `npm-qvac-runtime-notices.txt` (every NOTICE), inside the tar too; Apache text | done |
| Source offer: GPL-2.0 / LGPL-3.0 | PRoot, talloc | section 6; source archives in `docs/licenses/` | done (contact: G2) |
| Source offer: Debian GPL/LGPL packages | 78 packages in the image | `debian-image-sources.txt` + written offer | done |
| Unity Companion License section 5: licence and notices with the Work | 20 packages, 36 skills | Unity files in `docs/licenses/` | done |
| Unity: valid engine licence | the app, UCL-licensed skills | the owner's subscription | owner keeps the proof (G11) |
| Meta SDK terms: only for Meta hardware; SDK not under open source; keep notices | Meta XR SDK | SDK not in the repository; NOTICE files in `docs/licenses/` | done |
| OpenRAIL-M: licence copy; use restrictions passed to users | Supertonic 3 | `Supertonic-3-OpenRAIL-M.txt`; summary in 4.1 and in the ABOUT tab (names it, points here) | partly: VOICE panel next to the models (G4) |
| Android SDK License accepted by the user | android.jar | `mcx-setup` prints the licence name and URL | not done: it does not ask (G6) |
| No third-party trademark use in the app | contest section 4 | no tool logos; | open: launcher icon (G5), MQH icons (G10) |
| Never bundle Anthropic's proprietary CLI/SDK | Claude Code, `claude-agent-sdk` | verified absent from the APK | done |

## 8. Gaps

Status of the gaps of 2026-11-10 (G1-G9) and the new ones. "Owner" = a decision or an action only the owner can take;
"code" = a change in a file outside this documentation task (the agent that owns it should make it).

| # | Gap | State | What is left, and who |
|---|---|---|---|
| G1 | V8, libuv (and the embedded BoringSSL, ICU) notices; Unity runtime notices | **closed**: texts in `docs/licenses/` (V8, libuv, BoringSSL, ICU, Unity's 6000.4.5f1 notice file) | ships with the next build (the overlay copies the folder; keep it flat) |
| G2 | GPL-2.0 PRoot / LGPL-3.0 talloc / Debian GPL: source not offered | **closed in the documents**: source archives and the Debian file list are in `docs/licenses/`; written offer in section 6 | **owner**: confirm the contact for the written offer (an e-mail, or "GitHub issues") and publish the release with the repository; **code**: `THIRD_PARTY_PROOT.md` still says "source is available at the URLs above" and the ABOUT tab does not mention the offer (add a line pointing to section 6) |
| G3 | Debian image source | **closed**: `debian-image-sources.txt` (55 source packages, exact versions, SHA-1 addresses, all resolved on snapshot.debian.org) | none |
| G4 | OpenRAIL-M use restrictions must reach users | **partly closed**: the ABOUT tab says the voice licence forbids some uses and points here; summary in 4.1 | **code**: also show them in the VOICE panel next to INSTALL MODELS |
| G5 | Launcher icon is Unity's logo (`res/mipmap*/app_icon.png`, `ic_launcher_foreground.png`, checked in APK 0.7.16) | **open** | **code**: set our own icon in `MetaCodexSetup` (drawn from the MetaCodex mark) |
| G6 | android.jar is downloaded without asking for acceptance of the Android SDK License | **open** (the script prints the name and URL, it does not ask) | **code**: `mcx-setup quest` must show the terms and ask first |
| G7 | No licence for our code; `local-ai/package.json` says Apache-2.0 | **closed**: MIT, `LICENSE` written; section 2 | **code**: change `local-ai/package.json` `license` to `MIT` at the next bundle |
| G8 | MQH on the headset must not carry Anthropic's Agent SDK | **closed**: verified absent from `mqh.tar` (APK 0.7.16); MQH's MIT and npm notices are in `docs/licenses/` | keep `tools/mqh-bundle.sh` removing `provider-claude-code` (`provider-pi` is out of `mqh/` since 2026-11-13) |
| G9 | `UiIcon` shader left without a user | open, tidiness only (still in `Resources/Shaders`, still named by the self-test) | **code**: delete when the orb branch has landed |
| G10 | **New.** 49 third-party icons in the APK (50 in the repository): `mqh/plugins/provider-acp/icons` (47 in the APK: logos of other agents), `provider-codex/icons` (2), plus `provider-claude-code` (1, repository only; `storage-retention` and `provider-pi` and their icons left `mqh/` on 2026-11-13); licence and provenance unknown (came with bb), trademarks of their owners | **open**: conflicts with the no-logo rule and contest section 4, and the MIT notice of bb does not clearly cover them | **code**: delete the `icons` folders in `tools/mqh-bundle.sh` (the plugins show no icon in the VR terminal) and drop them from `mqh/`, or replace them with our own; **owner** decides |
| G11 | **New.** Unity-made and UCL files in the repository (TextMesh Pro resources, settings, 36 Unity skills): UCL section 1 allows use and distribution only with content made under a valid Unity engine licence | **noted**, not a block | **owner**: keep the proof of the Unity licence (plan, seat) with the submission; the `LICENSE` scope clause already names these files |
| G12 | **New.** `native/xr/mcx_font.h` (a DejaVu Sans Mono atlas) gets copied into every app built on the headset from the template; the Bitstream Vera licence wants its notice with copies | **open**, small | **code**: put the licence text in the header (or ship `DejaVu-LICENSE.txt` in `quest-native/xr`) |
| G13 | **New.** Four npm packages in the AI bundle publish no licence file (only a `license` field in `package.json`): `bare-stdio` 1.0.3 (Apache-2.0), `corestore` 7.16.1 (MIT), `noise-curve-ed` 2.1.0 (ISC), `varint` 5.0.0 (MIT) | **noted**: the declared licence is clear, the standard text is in `docs/licenses/`; copyright holders from package.json: Holepunch (Inc), Chris Dickinson | none for us; upstream omission |

## 9. Services the app and its tools talk to (terms, not licences)

The user's own accounts and the providers' terms apply: Anthropic (Claude Code login or API key), OpenAI (Codex),
OpenCode's model providers, GitHub (only if the user stores a token in VAULT > GIT), Hugging Face and the QVAC registry
(model downloads), Debian mirrors, nodejs.org, the npm registry, Google's SDK repository (android.jar), Maven Central,
GitHub Releases (the NDK), skills.sh (MQH's skill registry). MetaCodex never reads or stores provider login tokens.

## 10. How this was checked (rerun before every submission)

- What ships: `unzip -l unity/Builds/MetaCodex.apk` (22 native libs, 28 `assets/lib` files, 4 bundles), the four
  bundles unpacked into scratch folders; the bundle manifests (`assets/linux/*.json`, `assets/qvac/runtime.json`);
  the build's `global-metadata.dat` for assemblies; the dex class lists (by package); the fonts' names in the build data;
  `strings` of the native libraries for embedded libraries.
- npm packages: a script reads every `package.json` under `opt/mqh`, `qvac` and the bundles (dropping test fixtures and
  playgrounds), takes the `license` field and the `LICENSE`/`NOTICE` files, and writes the three notice files. The
  bundled-in packages of MQH come from the build's source maps and `// node_modules/.pnpm/...` path comments, matched by
  sha256 to the build in `~/mcxwork/mqh-build`.
- Debian: `/var/lib/dpkg/status` and `/usr/share/doc/*/copyright` of the image; the first-use set by simulating
  `apt-get install --no-install-recommends` on the image's dpkg status against trixie's `Packages`; copyright files from
  sources.debian.org; sources from snapshot.debian.org.
- Licences of Unity packages: `LICENSE.md` and the Third Party Notices of each package in `unity/Library/PackageCache`;
  Unity's Editor notices; Meta's `NOTICE.txt` files.
- Downloads: `npm view <package> license version`, the Hugging Face API and model cards (licence, revision sha), the
  upstream LICENSE files, GitHub's licence API, Maven Central (sha256 re-computed for the OpenXR archive), Termux's
  build recipes (source hashes of PRoot, talloc, libandroid-shmem; the three source archives were downloaded and their
  sha256 matched).


---

## Appendix A: Debian packages in the Linux image

Read from the image's `/var/lib/dpkg/status` and each package's `/usr/share/doc/<package>/copyright` (DEP-5 `License:` fields, in file order, at most six shown; the complete files are in [debian-image-copyright.txt](licenses/debian-image-copyright.txt)). "free text" means the copyright file is not machine-readable.

| Package | Version | Source package | Licences declared in its copyright file |
|---|---|---|---|
| apt | 3.0.4 | same | GPL-2+, GPL-2+ and curl, BSD-3-clause, Expat, GPL-2, curl |
| base-files | 13.8+deb13u7 | same | GPL-2+, verbatim |
| base-passwd | 3.6.7 | same | GPL-2, public-domain |
| bash | 5.2.37-2+b10 | same | GPL-3+, GPL-3+ with Bison exception, GPL-2+, GFDL-NIV-1.3, Latex2e, BSD-4-clause-UC and MIT-like ... |
| bsdutils | 1:2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| coreutils | 9.7-3 | same | GPL-3+, GPL-3+ and BSD-4-clause-UC, BSD-4-clause-UC, GPL-3+ and ISC, ISC, FSFULLR ... |
| dash | 0.5.12-12 | same | BSD-3-Clause, public-domain, GPL-2+, BSD-3-clause |
| debconf | 1.5.91 | same | BSD-2-clause |
| debian-archive-keyring | 2025.1 | same | free text: GPL-2 or later for the support files; the keys are not copyrightable |
| debianutils | 5.23.2 | same | GPL-2+, public-domain, SMAIL-GPL |
| diffutils | 1:3.10-4 | same | GPL-3+, FSFULLR, GPL-3+ and FSFULLR, LGPL-2.1+, GPL-3+ with autoconf exception, GPL-3+ with texinfo exception ... |
| dpkg | 1.22.22 | same | GPL-2+, public-domain-s-s-d |
| findutils | 4.10.0-3 | same | GFDL-NIV-1.3+ or GFDL-NIV-1.3+, GPL-3+, FSFAP, GFDL-NIV-1.3+, GPL-2+ with Autoconf-data exception, GPL-3+ with Autoconf-data exception ... |
| gcc-14-base | 14.2.0-19 | gcc-14 | free text: GPL-3 or later with the GCC Runtime Library Exception for libgcc and libstdc++; libbacktrace BSD-3, libffi MIT, GFDL-1.2 docs |
| grep | 3.11-4+b1 | same | GPL-3+ |
| gzip | 1.13-1+deb13u1 | same | GPL-3+, GFDL-1.3+-no-invariant, FSF-manpages |
| hostname | 3.25 | same | GPL-2 |
| init-system-helpers | 1.69~deb13u1 | same | BSD-3-clause, GPL-2+ |
| libacl1 | 2.3.2-2+b1 | acl | GPL-2+, LGPL-2+ |
| libapt-pkg7.0 | 3.0.4 | apt | GPL-2+, GPL-2+ and curl, BSD-3-clause, Expat, GPL-2, curl |
| libattr1 | 1:2.5.2-3 | attr | GPL-2+, LGPL-2+ |
| libaudit-common | 1:4.0.2-2+deb13u1 | audit | GPL-2, LGPL-2.1 |
| libaudit1 | 1:4.0.2-2+deb13u1 | audit | GPL-2, LGPL-2.1 |
| libblkid1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| libbsd0 | 0.12.2-2 | libbsd | BSD-3-clause, BSD-3-clause-Regents, BSD-3-clause-Regents and BSD-2-clause-NetBSD, BSD-3-clause-author, BSD-3-clause-John-Birrell, BSD-5-clause-Peter-Wemm ... |
| libbz2-1.0 | 1.0.8-6 | bzip2 | BSD-variant, GPL-2 |
| libc-bin | 2.41-12+deb13u4 | glibc | LGPL-2.1+, LGPL-2+, LGPL-2.1+-with-link-exception, LGPL-3+, GPL-2+, GPL-2+-with-link-exception ... |
| libc6 | 2.41-12+deb13u4 | glibc | LGPL-2.1+, LGPL-2+, LGPL-2.1+-with-link-exception, LGPL-3+, GPL-2+, GPL-2+-with-link-exception ... |
| libcap-ng0 | 0.8.5-4+b1 | libcap-ng | LGPL-2.1+, GPL-2+, GPL-3 |
| libcap2 | 1:2.75-10+deb13u1+b3 | same | BSD-3-clause or GPL-2, BSD-3-clause or GPL-2+, BSD-3-clause, GPL-2, GPL-2+ |
| libcrypt1 | 1:4.4.38-1 | libxcrypt | free text: LGPL-2.1 or later overall; parts BSD-3-Clause, BSD-0, public domain |
| libdb5.3t64 | 5.3.28+dfsg2-9 | db5.3 | Sleepycat and BSD-3-clause, Sleepycat, BSD-3-clause, Ms-PL, GPL or Artistic, X11 ... |
| libdebconfclient0 | 0.280 | cdebconf | BSD-2-Clause, GPL-2+, BSD-2-clause |
| libgcc-s1 | 14.2.0-19 | gcc-14 | see gcc-14-base (its doc folder is a link) |
| libgmp10 | 2:6.3.0+dfsg-3 | gmp | GPL-2+ or LGPL-3+, GPL-3+, GPL-3+ with Bison exception, GPL-2+, LGPL-3+ |
| libhogweed6t64 | 3.10.1-1 | nettle | LGPL-3+ or GPL-2+, LGPL-2+, Expat, GPL-2+, GPL-3+ with Autoconf exception, public-domain ... |
| liblastlog2-2 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| liblz4-1 | 1.10.0-4 | lz4 | GPL-2+, BSD-2-clause, BSD-2-clause or GPL-2+ |
| liblzma5 | 5.8.1-1+deb13u2 | xz-utils | 0BSD, GPL-2+, LGPL-2.1+, FSFULLR, GPL-3.0-or-later-WITH-Autoconf-exception-macro, none ... |
| libmd0 | 1.1.0-2+b1 | libmd | BSD-3-clause, BSD-3-clause-Aaron-D-Gifford, BSD-2-clause, BSD-2-clause-NetBSD, ISC, Beerware ... |
| libmount1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| libnettle8t64 | 3.10.1-1 | nettle | LGPL-3+ or GPL-2+, LGPL-2+, Expat, GPL-2+, GPL-3+ with Autoconf exception, public-domain ... |
| libpam-modules | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers ... |
| libpam-modules-bin | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers ... |
| libpam-runtime | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers ... |
| libpam0g | 1.7.0-5 | pam | BSD-3-clause or GPL, GPL-2, GPL-2+, GPL-3, GPL-3+ with Bison exception, BSD-tcp_wrappers ... |
| libpcre2-8-0 | 10.46-1~deb13u3 | pcre2 | BSD-3-clause-Cambridge with BINARY LIBRARY-LIKE PACKAGES exception, BSD-3-clause, X11, BSD-2-clause, public-domain |
| libseccomp2 | 2.6.0-2 | libseccomp | LGPL-2.1 |
| libselinux1 | 3.8.1-1 | libselinux | public-domain, GPL-2 |
| libsemanage-common | 3.8.1-1 | libsemanage | LGPL-2.1+, GPL-2 |
| libsemanage2 | 3.8.1-1 | libsemanage | LGPL-2.1+, GPL-2 |
| libsepol2 | 3.8.1-1 | libsepol | LGPL-2.1+, Zlib, GPL-2, GPL-2+ |
| libsmartcols1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| libsqlite3-0 | 3.46.1-7+deb13u2 | sqlite3 | public-domain, GPL-2+ |
| libssl3t64 | 3.5.7-1~deb13u3 | openssl | Apache-2.0, Artistic or GPL-1+, Artistic, GPL-1+ |
| libstdc++6 | 14.2.0-19 | gcc-14 | see gcc-14-base (its doc folder is a link) |
| libsystemd0 | 257.13-1~deb13u1 | systemd | LGPL-2.1+, CC0-1.0, GPL-2 with Linux-syscall-note exception, Expat, public-domain, GPL-2+ |
| libtinfo6 | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| libudev1 | 257.13-1~deb13u1 | systemd | LGPL-2.1+, CC0-1.0, GPL-2 with Linux-syscall-note exception, Expat, public-domain, GPL-2+ |
| libuuid1 | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| libxxhash0 | 0.8.3-2 | xxhash | BSD-2-clause, GPL-2+, BSD-2-clause or GPL-2+ |
| libzstd1 | 1.5.7+dfsg-1 | libzstd | BSD-3-clause or GPL-2, zlib, Expat, GPL-2, BSD-3-clause |
| login | 1:4.16.0-2+really2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| login.defs | 1:4.17.4-2 | shadow | BSD-3-clause, GPL-1, GPL-2+ |
| mawk | 1.3.4.20250131-1 | same | GPL-2.0-only, X11, CC-BY-3.0 |
| mount | 2.41.5-0+deb13u1 | util-linux | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| ncurses-base | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| ncurses-bin | 6.5+20250216-2 | ncurses | MIT/X11, X11, BSD-3-clause |
| openssl-provider-legacy | 3.5.7-1~deb13u3 | openssl | Apache-2.0, Artistic or GPL-1+, Artistic, GPL-1+ |
| passwd | 1:4.17.4-2 | shadow | BSD-3-clause, GPL-1, GPL-2+ |
| perl-base | 5.40.1-6+deb13u1 | perl | GPL-1+ or Artistic, Expat, REGCOMP, and GPL-1+ or Artistic, GPL-3+-WITH-BISON-EXCEPTION, Unicode, GPL-1+ or Artistic, and Unicode ... |
| sed | 4.9-2+deb13u1 | same | GPL-3+, X11, GFDL-NIV-1.3+, GPL-3+ and ISC, ISC, BSD-4-clause-UC ... |
| sqv | 1.3.0-3+b2 | rust-sequoia-sqv | LGPL-2.0-or-later |
| sysvinit-utils | 3.14-4 | sysvinit | GPL-2.0+, LGPL-2.1+, GPL-3.0, GPL-2.0 |
| tar | 1.35+dfsg-3.1 | same | GPL-3+, GPL-3+ with Bison exception, LGPL-2.1+, LGPL-3+, GPL-2+ |
| tzdata | 2026f-0+deb13u1 | same | public-domain |
| util-linux | 2.41.5-0+deb13u1 | same | GPL-2+, GPL-2, GPL-3+, LGPL-2.1+, public-domain, BSD-4-clause ... |
| zlib1g | 1:1.3.dfsg+really1.3.1-1+b1 | zlib | Zlib |

Source packages and exact versions with the addresses of their files: [debian-image-sources.txt](licenses/debian-image-sources.txt).

## Appendix B: npm packages in the APK

Read from every `package.json` in the bundles of APK 0.7.16 (test fixtures and playground folders skipped), grouped by the `license` field. The `LICENSE` and `NOTICE` files of all of them are reproduced (identical texts once) in the three notice files.

### B.1 The AI service bundle (`assets/qvac/runtime.tar`; plus our `metacodex-ai` 0.1.0, MIT)

133 packages; notices: [npm-qvac-runtime-notices.txt](licenses/npm-qvac-runtime-notices.txt).

- **Apache-2.0** (83): @hyperswarm/secret-stream 6.9.2; @qvac/asr-ggml 0.12.0; @qvac/asr-ggml-android-arm64 0.12.0; @qvac/embed-llamacpp 0.55.0; @qvac/error 0.1.1; @qvac/fabric 0.26.0; @qvac/fabric-android-arm64 0.26.0; @qvac/infer-base 0.8.0; @qvac/langdetect-text 0.1.3; @qvac/llm-llamacpp 0.68.0; @qvac/logging 0.1.1; @qvac/registry-client 0.8.0; @qvac/registry-schema 0.4.0; @qvac/tts-ggml 0.15.0; @qvac/tts-ggml-android-arm64 0.15.0; adaptive-timeout 1.0.1; b4a 1.9.0; bare-abort 2.0.14; bare-abort-controller 1.1.2; bare-addon-resolve 1.11.1; bare-ansi-escapes 2.2.4; bare-assert 1.3.0; bare-buffer 3.7.1; bare-dns 2.3.1; bare-env 3.0.1; bare-events 2.9.2; bare-fs 4.8.4; bare-hrtime 2.1.2; bare-http-parser 2.1.4; bare-http1 4.6.3; bare-https 3.1.0; bare-inspect 3.1.10; bare-module-resolve 1.12.6; bare-net 2.3.3; bare-os 3.9.4; bare-path 3.1.2; bare-pipe 4.4.0; bare-posix 1.0.1; bare-process 4.5.1; bare-semver 1.1.0; bare-signals 5.0.0; bare-stdio 1.0.3; bare-stream 2.13.4; bare-tcp 2.6.1; bare-tls 3.2.0; bare-tty 5.2.2; bare-type 1.4.0; bare-url 2.5.6; blind-relay 1.6.1; compact-encoding 3.6.0; compact-encoding-bitfield 1.1.0; compact-encoding-net 1.3.0; device-file 2.3.1; events-universal 1.0.1; fd-lock 2.2.0; fs-native-extensions 1.5.3; hyperblobs 2.13.0; hypercore-errors 1.5.0; hypercore-id-encoding 1.3.0; hypercore-stats 2.5.2; hypercore-storage 3.4.0; hyperdb 6.11.1; hyperdht-address 1.1.1; hyperdht-stats 1.11.1; hyperdispatch 1.6.0; hyperschema 1.26.2; hyperswarm-stats 1.3.4; index-encoder 3.5.0; noise-handshake 4.2.0; paparam 1.15.0; passive-core-watcher 1.0.2; quickbit-native 2.4.9; rache 1.0.1; refcounter 1.0.0; require-addon 1.3.0; resource-on-exit 1.0.0; rocksdb-native 3.19.3; scope-lock 1.2.4; simdle-native 1.3.10; text-decoder 1.2.7; udx-native 1.22.1; unslab 1.3.1; which-runtime 1.4.0
- **MIT** (45): big-sparse-array 1.0.3; bogon 1.3.0; codecs 3.1.0; corestore 7.16.1; debounceify 1.1.0; dht-rpc 6.28.0; fast-fifo 1.3.2; flat-tree 1.13.0; generate-object-property 2.0.0; generate-string 1.0.1; hyperbee 2.27.3; hypercore 11.40.3; hypercore-crypto 3.7.0; hyperdht 6.36.0; hyperswarm 4.17.2; is-options 1.0.2; is-property 1.0.2; kademlia-routing-table 1.0.6; mutexify 1.4.0; nat-sampler 1.0.1; protocol-buffers-encodings 1.2.0; protomux 3.13.0; queue-tick 1.0.1; random-array-iterator 1.0.0; ready-resource 1.2.0; record-cache 1.2.0; resolve-reject-promise 1.1.0; safety-catch 1.0.3; shuffled-priority-queue 2.1.0; signal-promise 1.0.3; signed-varint 2.0.1; sodium-native 5.2.0; sodium-secretstream 1.2.0; sodium-universal 5.0.1; speedometer 1.1.0; streamx 2.28.1; teex 1.0.1; time-ordered-set 2.0.1; timeout-refresh 2.0.1; tiny-byte-size 1.1.0; tinyld 1.3.4; unordered-set 2.0.1; varint 5.0.0; xache 1.3.0; z32 1.1.0
- **ISC** (5): bits-to-bytes 1.3.0; nanoassert 2.0.0; noise-curve-ed 2.1.0; quickbit-universal 2.2.1; simdle-universal 1.1.3

### B.2 MQH: `opt/mqh/node_modules`

72 packages; notices: [npm-mqh-runtime-notices.txt](licenses/npm-mqh-runtime-notices.txt).

- **MIT** (59): @parcel/watcher 2.6.0; @parcel/watcher-linux-arm64-glibc 2.6.0; @parcel/watcher-linux-arm64-musl 2.6.0; @pinojs/redact 0.4.0; atomic-sleep 1.0.0; base64-js 1.5.1; better-sqlite3 12.11.1; bindings 1.5.0; bl 4.1.0; buffer 5.7.1; colorette 2.0.20; date-fns 4.4.0; dateformat 4.6.3; decompress-response 6.0.0; deep-extend 0.6.0; end-of-stream 1.4.5; fast-copy 4.3.1; fast-safe-stringify 2.1.1; file-uri-to-path 1.0.0; fs-constants 1.0.0; github-from-package 0.0.0; help-me 5.0.0; is-extglob 2.1.1; is-glob 4.0.3; joycon 3.1.1; mimic-response 3.1.0; minimist 1.2.8; mkdirp-classic 0.5.3; napi-build-utils 2.0.0; node-abi 3.101.0; node-addon-api 7.1.1; node-pty 1.2.0-beta.15; on-exit-leak-free 2.1.2; picomatch 4.0.7; pino 9.14.0; pino-abstract-transport 2.0.0; pino-abstract-transport 3.0.0; pino-pretty 13.2.0; pino-roll 4.0.0; pino-std-serializers 7.1.0; prebuild-install 7.1.3; process-warning 5.1.1; pump 3.0.4; quick-format-unescaped 4.0.4; readable-stream 3.6.2; real-require 0.2.0; safe-buffer 5.2.1; safe-stable-stringify 2.5.0; simple-concat 1.0.1; simple-get 4.0.1; sonic-boom 4.2.1; sonic-boom 5.0.2; string_decoder 1.3.0; strip-json-comments 2.0.1; strip-json-comments 5.0.3; tar-fs 2.1.5; tar-stream 2.2.0; thread-stream 3.2.0; util-deprecate 1.0.2
- **ISC** (7): chownr 1.1.4; inherits 2.0.4; ini 1.3.8; once 1.4.0; semver 7.8.6; split2 4.2.0; wrappy 1.0.2
- **BSD-3-Clause** (2): ieee754 1.2.1; secure-json-parse 4.1.1
- **Apache-2.0** (2): detect-libc 2.1.2; tunnel-agent 0.6.0
- **(MIT OR WTFPL)** (1): expand-template 2.0.3
- **(BSD-2-Clause OR MIT OR Apache-2.0)** (1): rc 1.2.8

### B.3 MQH: packages bundled into the harness files

97 packages; notices: [npm-mqh-bundled-notices.txt](licenses/npm-mqh-bundled-notices.txt).

- **MIT** (79): @better-auth/api-key 1.7.18; @better-auth/core 1.7.18; @better-auth/drizzle-adapter 1.7.18; @better-auth/kysely-adapter 1.7.18; @better-auth/memory-adapter 1.7.18; @better-auth/telemetry 1.7.18; @better-auth/utils 0.5.1; @better-fetch/fetch 1.3.3; @colors/colors 1.5.0; @hono/node-server 1.19.18; @hono/node-ws 1.3.1; @jitl/quickjs-ffi-types 0.32.0; @jitl/quickjs-singlefile-cjs-release-sync 0.32.0; @noble/ciphers 2.4.0; @noble/hashes 2.4.0; @sec-ant/readable-stream 0.4.1; @sindresorhus/merge-streams 4.0.0; acorn 8.19.0; acorn-walk 8.3.5; ajv 8.20.0; ansi-regex 5.0.1; better-auth 1.7.18; better-call 1.4.5; cli-table3 0.6.5; commander 13.1.0; cron-parser 5.10.3; cross-spawn 7.0.6; defu 6.1.7; emoji-regex 8.0.0; es-module-lexer 2.3.2; execa 9.6.1; extend-shallow 2.0.1; fast-deep-equal 3.1.3; figures 6.1.0; get-stream 9.0.1; gray-matter 4.0.3; handlebars 4.7.10; hono 4.13.16; is-extendable 0.1.1; is-fullwidth-code-point 3.0.0; is-network-error 1.3.2; is-plain-obj 4.1.0; is-stream 4.0.1; is-unicode-supported 2.1.0; jose 6.2.15; js-yaml 3.15.2; json-schema-traverse 1.0.0; kind-of 6.0.3; kysely 0.29.8; luxon 3.7.2; mime-db 1.54.0; mime-types 3.0.2; nanoid 5.1.16; npm-run-path 6.0.0; p-retry 6.2.1; parse-ms 4.0.0; partysocket 1.3.0; path-key 3.1.1; path-key 4.0.0; pretty-ms 9.3.1; proper-lockfile 4.1.2; quickjs-emscripten-core 0.32.0; retry 0.12.0; retry 0.13.1; rou3 0.9.2; section-matter 1.0.0; shebang-command 2.0.0; shebang-regex 3.0.0; string-width 4.2.3; strip-ansi 6.0.1; strip-bom-string 1.0.0; strip-final-newline 4.0.0; undici 7.30.2; unicorn-magic 0.3.0; which-command 0.1.0; ws 8.22.1; xmlchars 2.2.0; yoctocolors 2.2.0; zod 4.6.7
- **ISC** (8): graceful-fs 4.2.11; isexe 2.0.0; saxes 6.0.0; semver 7.8.6; signal-exit 3.0.7; signal-exit 4.1.0; which 2.0.2; yaml 2.9.1
- **Apache-2.0** (5): @modelcontextprotocol/core 2.4.0; @modelcontextprotocol/server 2.4.0; @opentelemetry/api 1.10.0; drizzle-orm 0.45.4; human-signals 8.0.1
- **BSD-3-Clause** (4): fast-uri 3.1.9; fzf 0.5.2; smol-toml 1.9.1; source-map 0.6.1
- **BlueOak-1.0.0** (1): tar 7.5.22

