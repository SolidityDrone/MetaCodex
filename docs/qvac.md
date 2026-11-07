# QVAC in MetaCodex: where it runs and how it keeps the frame rate stable

> **Status 2026-11-07 (evening): phases 0-3 built.** Phase 0 ran the three models natively on the Quest 3S
> over adb (numbers in `docs/voice.md`: speech-to-text 0.7 s, an LLM tool call 1.2-2 s, 72 fps held with 2
> threads at nice 10). The service (`qvac/server`), its packing into the APK (`tools/qvac-bundle.sh`), the
> app side (QvacHost, microphone, voice pipeline, VOICE panel) and the command registry (shell commands,
> Lua `voice.command`, control port `voice`) are in; `docs/voice.md` is the reference. One change from the
> plan below: the app starts the Bare executable as its own child process (like proot), pinned to cores
> 0-2 at nice 10, instead of a Java service hosting BareKit (no BareKit Android release exists to link; the
> isolation is the same). Next: the first APK with it, then MQH tools in the registry.

Status: decision 2026-11-07, replaces PLAN v2 D12 ("R1 first: the SDK in the Linux userland").
Nothing below is measured on the headset yet; the validation order is at the end.

## Scope and plan (owner, 2026-11-07 afternoon): voice control of the environment

QVAC is **not** a coding agent or a provider. It is the private, local interface to the environment:
speech-to-text, a small LLM and text-to-speech turn "close all windows", "change workspace", "find the
thread where we do X", "open providers page" into actions, and private data never leaves the headset.
OCR, translation, video and embeddings are out of scope for now.

**Placement: native on Horizon OS** (confirmed). An Android service process of our APK hosts a BareKit
worklet with the QVAC SDK worker (`qvac bundle sdk`, plugins: transcription, TTS, llamacpp completion) and
QVAC's android-arm64 addons (`@qvac/asr-ggml-android-arm64`, `@qvac/tts-ggml-android-arm64`,
`@qvac/fabric-android-arm64` exist; the CLI's `qvac serve` is desktop-only, so the worklet serves its own
small HTTP API). Why native for frame stability: Android keeps a service process on cores 0-2 at background
priority and kills it first under memory pressure, with no proot overhead; and the microphone and speaker
are only reachable from Android (the Linux side has no audio devices).

```
 Horizon OS: AI service (:ai, BareKit + QVAC: STT, TTS, LLM)  127.0.0.1:47810, vault key
        ^                         ^
        | HTTP                    | HTTP (loopback)
   Unity app (mic, speaker,     MQH daemon / terminal (mcx-ai) in the Linux userland
   voice commands -> actions)
```

| Capability | First model (small, CPU, cores 0-2, 2 threads) | API |
|---|---|---|
| Speech-to-text | `WHISPER_TINY` or `PARAKEET_TRANSCRIBEPSY_250M_Q8_0` (8 European languages) | `POST /v1/audio/transcriptions` |
| Text-to-speech | Supertonic (tts-ggml) | `POST /v1/audio/speech` |
| Commands | `QWEN3_600M_INST_Q4`, then `QWEN3_1_7B_INST_Q4` if needed (tool calling; deferred tool loading) | `POST /v1/chat/completions` with tools |
| Control | | `GET /status`, `POST /pause`, `POST /resume` (Unity pauses inference in frame-critical moments) |

**Commands are a registry (MCP-like):** each command has a name, a description and a JSON schema
(`close_all_panels`, `open_panel(id)`, `switch_workspace(name)`, `find_thread(query)`, `new_terminal`,
`set_layout(mode)`, `run_in_terminal(cmd)` with confirmation, ...). The shell registers its own, Lua
plugins add theirs (`voice.command{...}`), MQH plugins can expose MCP tools; with deferred loading the LLM
sees only the few that match, so it needs no context about the app. Obvious phrasings take a fast path
(rule match) and skip the LLM.

**Phases:** 0. native spike over adb, no APK (bare + addons + the three models: works on Horizon OS? load
time, latency, RAM, fps with the app running) - 1. the service in the APK (BareKit, worker bundle, addons,
model download on first use) - 2. voice control in Unity (push-to-talk, mic permission, STT -> fast path /
LLM -> action -> spoken or visual confirmation) - 3. the command registry for plugins and MQH.

## Decision

QVAC runs **natively on Horizon OS, in its own Android process**: a service in the MetaCodex APK
(process `:ai`), hosting the Bare runtime through `bare-kit`, the way QVAC's own mobile path works
(`@qvac/sdk` ships `./worker.mobile.bundle` and an Expo plugin for bare-kit worklets). It never runs in
the Unity process. Unity and MQH reach it through one local API on `127.0.0.1` with a token.

The QVAC install in the Linux userland (`mcx-setup qvac`, proven on the Quest 3S on Nov 6) stays as the
development path (fast iteration over adb, no APK rebuild) and the fallback if the native route fails.

### The fact that changed

On Nov 6 we read the main addon packages' `optionalDependencies`, saw no Android entry for speech-to-text
and text-to-speech, and concluded they had no Android build. They do: `@qvac/asr-ggml-android-arm64`
0.12.0 (67 MB unpacked) and `@qvac/tts-ggml-android-arm64` 0.15.0 (71 MB) exist on npm since 2026-09-09,
as does `@qvac/fabric-android-arm64` 0.27.0 (LLM engine, 106 MB). They are simply not listed as optional
dependencies, so they must be installed explicitly. Every capability we need exists for Android.

## Why native, factor by factor

| Factor | Native (Android service `:ai`) | Linux userland (proot) |
|---|---|---|
| Capabilities | LLM, speech-to-text, text-to-speech, OCR, embeddings (android-arm64 packages) | the same (linux-arm64), already installed and importing |
| Android's limit of 32 child processes (phantom-process killer) | uses none: a service process started by Android is not a phantom | one or more processes; MQH and the agents already need that budget |
| Crash isolation from Unity | own process | own process |
| Scheduling | **measured on the Quest 3S (Nov 7): the foreground app's CPU set is cores 0-5, the `foreground`/`background` sets (services, background work) are cores 0-2**, so a service process is kept off cores 3-5, which stay Unity's alone; plus per-thread priority | children of the app inherit Unity's set (0-5); we must pin them ourselves (`taskset -c 0-2`) |
| Overhead | none | proot intercepts syscalls: negligible for matrix maths, visible when loading a model |
| Microphone, speaker, cameras | on the Android side, next to Unity | audio and images streamed from Unity over a socket (small: 32 KB/s for 16 kHz speech) |
| GPU | reachable (Vulkan), but kept for Unity (rule 2) | not reachable from a glibc userland |
| Engineering | Java service + bare-kit + our JS bundle + addons as `jniLibs`; an APK rebuild per change | done; push and run over adb |

Managing the hardware stays Unity's job in both cases (CPU/GPU levels, thermal headroom, frame budget).
Native wins on the process budget, scheduling isolation and syscall overhead, and loses only on
iteration speed. So we develop and compare in the userland, and ship native.

## Rules for a 100% stable frame rate (wherever QVAC runs)

1. **Never in the Unity process.** A native crash or a long stall in an inference library must not reach
   the frame loop.
2. **CPU only while the app renders.** The GPU is the frame-time bottleneck and belongs to Unity; the
   NPU is closed to apps (SELinux, tested in SandyBridge).
3. **Few threads, low priority, away from Unity's cores.** Start at 2 inference threads at background
   priority, on cores 0-2 (the Quest 3S has 6 cores: 0-1 at 2.05 GHz, 2-5 at 2.36 GHz; Android gives
   services cores 0-2 and the foreground app all six). The same pinning applies to MQH, the agents and
   everything else in the Linux userland.
4. **On demand only.** Push-to-talk speech-to-text, short spoken replies, one clean-up pass per prompt.
   No always-on inference; models unloaded after a few idle minutes.
5. **Small quantized models.** Whisper tiny/base class for speech, a ~1B Q4 LLM for clean-up, one small
   voice. Target: all local AI together under ~1.5 GB of RAM (to measure).
6. **Unity owns the frame budget.** It watches frame time and thermal headroom and tells the AI service
   to slow down (fewer threads) or pause, then resume when back in budget.
7. **Measure frame time, not only speed.** LLM inference saturates memory bandwidth, which slows the
   whole chip even when it runs on "free" cores: the gate is p99 frame time with inference running.

## Local AI API (same for both placements)

`127.0.0.1:47810`, token in the app's files (like the control port). OpenAI-compatible where a standard
exists, so MQH, the agents (OpenCode can point at a local OpenAI endpoint) and Unity use one interface:

| Endpoint | Use |
|---|---|
| `POST /v1/audio/transcriptions` (+ a WebSocket stream for push-to-talk) | voice prompts |
| `POST /v1/audio/speech` | spoken replies |
| `POST /v1/chat/completions` | local clean-up of private data before a prompt goes to a cloud provider; summaries |
| `POST /ocr`, `POST /vision` | "point and say": text and description of the captured region |
| `POST /pause`, `POST /resume`, `GET /status` | frame-budget control from Unity; models loaded, RAM, threads |

Clients: Unity (voice input, spoken replies, point-and-say capture) and an MQH plugin `qvac` (clean-up
step before cloud providers, tools for agents).

## Validation, in order

1. **A/B over adb, no APK.** Same model, prompt and audio clip: native `bare` (android-arm64,
   `bare-runtime-android-arm64` 1.36.0) against Node in the userland. Load time, tokens/s, speech
   real-time factor, RAM. Tells us whether native is also faster.
2. **Native inside the app.** bare-kit worklet in an Android service process; addons loaded from
   `nativeLibraryDir`; one speech-to-text, one text-to-speech and one LLM call from Unity.
3. **Frame time with inference running** (OVR Metrics or our perf readout): p50/p99 with and without
   inference, with rules 3 and 6 applied; temperature over 10 minutes.
4. **Then** the MQH `qvac` plugin (clean-up before providers) and point-and-say.

## Open questions

- Answered (Nov 7): yes. `/dev/cpuset/top-app/cpus` = 0-5, `foreground` and `background` = 0-2. Still
  to check: that a foreground-service process of our APK really lands in `foreground` while the app
  is in front (`/proc/<pid>/cpuset`).
- APK size: the addon packages hold several builds each; ship only the variant we use and download
  models on first use (never inside the APK).
- bare-kit's Java API in a plain Android service (it is mostly used from React Native apps).
