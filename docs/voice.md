# Voice control and the local AI service

Status: v0.1, 2026-11-07. The service runs on the Quest 3S over adb (phase 0 and the dev path, measured
below); the Editor self-test drives the whole pipeline against it. The in-APK start (`libbare.so` from
`nativeLibraryDir`) is built and waits for the next APK.

## What it does

Push-to-talk, then the headset does the rest, privately, with no cloud:

```
 TALK (dock) / gesture voice:toggle / mcx-ui voice listen
   -> microphone (Unity, 16 kHz, stops after ~1 s of silence)
   -> speech-to-text (Whisper base, local AI service)
   -> fast path: a phrase template of a command matches        -> run it   (~0 ms)
      else the local LLM picks one tool from the command list  -> run it   (~1.5 s)
   -> HUD above the dock ("✓ Opened GIT"), optional spoken reply (Supertonic 3)
```

Examples that work out of the box: "open the providers page", "close all windows", "find the thread where
we fixed the keyboard", "switch to the workspace called metacodex", "use the floating layout", "open a new
terminal", "recenter", "type ls -la", "run git status" (asks first).

## Commands: an MCP-like registry

Each command is a name, a one-line description, a JSON schema for its arguments (the LLM sees it as a
tool), phrase templates for the fast path, and a run function. The shell registers its own
(`Runtime/Shell/ShellVoice.cs`), Lua scripts and plugins add theirs with `voice.command{...}`
(`quest-scripting` skill, "Voice commands"); they disappear with the plugin.

| Command | Arguments | Phrases (fast path) |
|---|---|---|
| `open_panel` | `panel`: terminals files git agents threads browser scripts gestures vault settings voice | "open [the] {panel} [page]", "show me the {panel}", "go to {panel}" + synonyms (providers -> agents, source control -> git, secrets -> vault, ...) |
| `close_panel` | `panel` | "close [the] {panel}" |
| `close_all_panels` | | "close all [windows]", "clear the view" |
| `new_terminal` | | "open a new terminal", "new shell" |
| `find_thread` | `query` | "find the thread where {query}", "search threads for {query}" |
| `switch_workspace` | `name` (optional) | "switch to the workspace called {name}", "change workspace" |
| `set_layout` | `mode`: fixed float | "use the floating layout", "fixed mode" |
| `recenter` | | "recenter", "bring the panels back" |
| `type_text` | `text` | "type {text}" |
| `run_in_terminal` | `command` (confirmed in VR) | "run {command}" |
| `show_keyboard` | | "show the keyboard" |

Phrase templates: words, `(a|b)` one of, `[a|b]` optional, `{param}` captures words. Text is matched lower
case without punctuation or apostrophes, after fillers ("hey", "could you", "please", "now") are dropped.
Enum values accept synonyms and close misspellings of the whole phrase ("provider's" -> agents); a value
that fits nothing is not forced, the request goes to the LLM instead.

The LLM path: the system prompt and every enabled command as a tool are prefilled once in the service and
saved to disk (`/v1/warmup`, QVAC's `prefill` + `cacheKey`); each request then decodes only the user's
words. The app warms it when TALK is pressed (in parallel with the recording) and again when the command
set changes. The model's answer is validated against the schema (unknown tools, invented arguments,
enum values mapped) before anything runs.

`find_thread` scores MQH thread titles and branch names against the spoken words (stems, one-letter
slips) and opens the best match; `switch_workspace` matches MQH project names and folder names.

## The local AI service

QVAC (Tether's local AI stack) on the Bare runtime, in its own process: `qvac/server/` (Bare JS),
packed by `tools/qvac-bundle.sh` into `StreamingAssets/qvac/runtime.tar.gz` (24 MB: QVAC addons for
android-arm64 with CPU backends only, `libc++_shared.so` from NDK r29) plus `libbare.so` (the Bare
executable, 62 MB, stripped) in `Plugins/Android/libs/arm64-v8a`. Models are not in the APK: VOICE >
INSTALL MODELS downloads them once (530 MB) into the app's private storage, each checked against the
sha256 of the QVAC model registry.

| Model | Size | Source | Use |
|---|---|---|---|
| Whisper base q8_0 (multilingual) | 78 MB | Hugging Face (ggerganov/whisper.cpp, pinned revision) | speech-to-text |
| Qwen3 0.6B q4_0 | 364 MB | Hugging Face (unsloth/Qwen3-0.6B-GGUF, pinned revision) | picks commands (tool calling), short answers |
| Supertonic 3 q4_0 | 81 MB | QVAC model registry (Hyperswarm) | spoken replies, 31 languages |

How the app runs it (`Runtime/AI/QvacHost.cs`): the runtime is unpacked to `files/qvac/runtime` when its
hash changes; a new token goes to `files/qvac/token` and `/mnt/quest/qvac.token`; then
`taskset 7 nice -n 10 libbare.so server/main.js --data files/qvac/data --port 47810 --threads 2`, the way
proot is started (exec is only allowed from `nativeLibraryDir`; the addons are dlopen'ed from the
unpacked runtime). Cores 0-2 are the ones Android gives background work; Unity keeps 3-5. A dead service
is restarted (3 times in 5 minutes, then FAILED with the log). Models load on first use and unload after
idle minutes (speech-to-text 10, LLM 15, voice 3).

Why a child process and not an Android service hosting BareKit (the first plan in `docs/qvac.md`):
phase 0 proved the plain `bare` executable with the QVAC addons on Horizon OS; the same isolation (own
process, CPU only, cores 0-2, low priority) comes from `taskset` + `nice`, with nothing new in Java;
BareKit 3 is a C library with no Android release to link. The cost is one process of Android's 32
phantom-process budget. A BareKit service stays possible if that budget gets tight.

### API (127.0.0.1:47810, `Authorization: Bearer <token>`)

| Endpoint | Does |
|---|---|
| `GET /health` | no token; liveness |
| `GET /status` | models (installed, download progress), engines (loaded, idle), busy, RAM |
| `POST /v1/models/install` `{kinds:["asr","llm","tts"]}` | downloads in the background; progress in `/status` |
| `POST /v1/audio/transcriptions` | WAV body, raw `audio/pcm;rate=` or OpenAI-style multipart (`file`) -> `{text, ms, audioMs}` |
| `POST /v1/audio/speech` `{input, voice}` | WAV (44.1 kHz, 16-bit mono) |
| `POST /v1/chat/completions` | OpenAI-style, with `tools` -> `tool_calls`; `timings` |
| `POST /v1/warmup` `{messages:[system], tools}` | prefills and saves the prompt prefix |
| `POST /config` | threads, language, vocabulary prompt, voice, idle minutes |
| `POST /unload`, `/pause`, `/resume`, `/shutdown`; `GET /log` | control |

From the Linux userland: `mcx-ai status | install | transcribe f.wav | say "text" | chat "prompt" | env`.
From the app's control port: `mcx-ui voice status | list | match <text> | run <text> | say <text> |
wav /mnt/quest/f.wav | listen | install | threads N | lang en|it|auto`.

## Measured (Quest 3S, MetaCodex running at 72 fps, nice 10)

| | 2 threads | 4 threads |
|---|---|---|
| Speech-to-text, Whisper base, encoder window 10 s | 0.66-0.73 s per command | 0.37-0.44 s |
| LLM tool call, prefix cached | 1.2-2.0 s | 1.1-1.7 s |
| LLM first call after load, prefix from disk | 4.6 s (load 2.9 s + 1.7 s) | |
| LLM first call without the prefix cache | 7.7-9 s | 4.8 s |
| Text-to-speech, Supertonic 3 q4 | 2.1-3.5 s for 1.6-3 s of speech | |
| App frame rate (VrApi) | 72, 0-2 stale frames per stage | 72, 3 stale frames in speech-to-text |
| Memory (RSS) | speech-to-text 0.28 GB, LLM 1.1 GB, voice 0.5 GB | |

Whisper tiny was tried and rejected: with the 10 s encoder window it loops ("Open a new terminal. Open a
new terminal.") and takes up to 10 s. Accuracy of Whisper base on the test commands: 5/5, and the
vocabulary prompt fixes names ("MetaCodex", not "MedicoDex").

## Development without an APK

- `tools/qvac-bundle.sh`: builds the runtime and `libbare.so` (also needed once before the dev runners).
- `tools/qvac-dev.sh device start`: the same service on the headset over adb (`/data/local/tmp/mcx-ai`),
  port forwarded; `tools/qvac-dev.sh pc start` in WSL with the linux-x64 addons. Both write
  `unity/Bench/qvac-dev.json`, which the Editor uses instead of starting a process.
- Self-test: `MCX_UITEST_ONLY=voice tools/unity-batch.sh BatchUiTest` (fast path, actions, a Lua plugin
  command, the control port, MQH commands, and with a dev service: speech -> text -> command and the
  LLM path).

## Files

| Piece | Path |
|---|---|
| Service (Bare JS) | `qvac/server/main.js`, `engines.js`, `models.js`, `audio.js`; `qvac/package.json` + `bun.lock` |
| Bundle and dev runners | `tools/qvac-bundle.sh`, `tools/qvac-dev.sh`, `tools/remote-unzip.py` |
| Process, client | `unity/Assets/MetaCodex/Runtime/AI/QvacHost.cs`, `QvacClient.cs` |
| Microphone | `Runtime/AI/VoiceRecorder.cs` |
| Registry, fast path | `Runtime/AI/VoiceCommands.cs` |
| Pipeline, commands, VOICE panel, HUD | `Runtime/Shell/ShellVoice.cs` |
| Lua API | `Runtime/Scripting/ScriptHost.cs` (`voice.*`) |
| Userland | `userland/overlay/usr/local/bin/mcx-ai`, `mcx-ui voice ...` |
