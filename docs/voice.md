# Voice control and the local AI service

Status: v0.2, 2026-11-07. Runs in the APK on the Quest 3S (0.4: service process on cores 0-2, models
installed on the headset); 0.5 makes the cycle faster (measured below) and fixes the service after the
headset sleeps.

## What it does

Push-to-talk, then the headset does the rest, privately, with no cloud:

```
 TALK (dock) / gesture voice:toggle / mcx-ui voice listen
   -> microphone (Unity, 16 kHz, stops after ~1 s of silence)
   -> speech-to-text (Whisper base, local AI service)
   -> fast path: a phrase template of a command matches        -> run it   (~0 ms)
      else the local LLM writes one command line ("open_panel git"), held to the valid ones by a
      grammar                                                   -> run it   (~0.5 s)
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

The LLM path (0.5): the model answers with one command line, `open_panel git` or `none`, not a JSON tool
call. The prompt lists the commands in that form (`open_panel PANEL, set_layout fixed|float, ...`), example
turns teach it ("show me my secrets" → `open_panel vault`), and a GBNF grammar built from the registry only
lets it write valid lines, so it decodes 1-4 tokens instead of ~20. The system prompt and the example turns
are prefilled once and saved to disk (`/v1/warmup`, QVAC's `prefill` + `cacheKey`); each request then
decodes only the user's words. The app warms it when TALK is pressed (in parallel with the recording) and
again when the command set changes. The line is parsed and validated against the schema (enum values
mapped) before anything runs. Commands that take free text verbatim (`type_text`, `run_in_terminal`) are
not offered to the LLM: only their exact phrases ("type …", "run …") run them.

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

How the app runs it (`Runtime/AI/QvacHost.cs`, `Plugins/Android/MetaCodexAiService.java`): the runtime is
unpacked to `files/qvac/runtime` when its hash changes; a new token goes to `files/qvac/token` and
`/mnt/quest/qvac.token`; then the app asks its own Android service process (`:ai`, declared in the manifest)
to start `taskset 7 nice -n 10 libbare.so server/main.js --data files/qvac/data --port 47810 --threads 2`
(exec is only allowed from `nativeLibraryDir`; the addons are dlopen'ed from the unpacked runtime). The
service replaces a running child on every start (new token); the app health-checks it every 5 s and asks
for a restart after 3 misses (3 times in 5 minutes, then FAILED). Models load on first use and unload after
idle minutes (speech-to-text 10, LLM 15, voice 3). `GET /status` reports where the system placed the
process (`placement`: CPU set and allowed cores).

Why a separate Android process (measured on the Quest 3S, 0.4.0): **Horizon OS keeps the running immersive
app in the `/immersive` CPU set, cores 3-5 only, and every process the app starts inherits it**: the first
build started the service as a child of the app (like proot) and `taskset 7` failed with EINVAL (cores 0-2
not allowed). The frame loop (Unity's main, render and job threads) runs on those same cores. A service
process of the app is placed by the system instead (top-app 0-5 / foreground and background 0-2), so its
child can be pinned to 0-2. BareKit (Bare inside the service process) has no Android release to link, so
the service starts the Bare executable as its child.

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

## Faster cycle (0.5, measured)

| Change | Effect |
|---|---|
| CPU level Boost while a voice request runs (`OVRManager.suggestedCpuPerfLevel`; back after) | the app's level held cores 2-5 at 1.38 GHz; Boost allows 2.05: speech-to-text 0.80 → 0.66 s, LLM 1.39 → 1.17 s, speech 3.4 → 2.8 s (same requests, API) |
| LLM writes one command line under a grammar (above) | 1-4 tokens instead of ~20; 9/12 off-template phrasings right vs 5/8 with tool calls; 0.15-0.3 s on the PC (x4-6 on the headset) |
| 2 inference threads, fixed | 3 threads measured slower (LLM 10 vs 14.5 tok/s: the third core is the slow, shared one), 4 stalls (more threads than the 3 cores): the old THREADS 3/4 options are gone |
| End of speech after 0.7 s of silence (was 1.0 s) | 0.3 s less on every request |
| Models stay loaded 30 min (speech 10) | no 1-4 s reloads between requests |
| Replies' audio cached (64 entries) | the second "Opened GIT" is instant |
| Whisper encoder window sized to the clip, changed with `reload` (instant) | long clips get a bigger window; below 512 frames Whisper base repeats and mishears (tested 256: "Open the Provider's page. Open the Provider's page.", "MedicoDex"), so 512 stays the minimum |
| Supertonic steps | kept at 5: 3 and 2 steps are faster but garble words (round trip through speech-to-text: "I don't have a trip partner") |
| Health checks skip a busy service; 6 s timeout; restarts that actually restart; the app binds to the service | the service is no longer restarted under load, and survives the headset sleeping |

Voices: Supertonic 3 has ten, F1-F5 and M1-M5 (VOICE panel; `mcx-ui voice voice M2`).

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
