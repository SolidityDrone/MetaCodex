# Voice control and the local AI service

Status: v0.3, 2026-11-08. Runs in the APK on the Quest 3S (0.4: service process on cores 0-2, models
installed on the headset); 0.5 makes the cycle faster (measured below) and fixes the service after the
headset sleeps; 0.6 confirms actions silently (the HUD shows them) and adds `set_focus` and the SYSTEM panel;
0.7 fixes misheard words against the commands' vocabulary, runs several commands said in one sentence and
adds `set_setting` (every setting by voice: on / off, numbers, more / less). 0.7.2 replaces the LLM in the loop
with a **decision model** (below: English and Italian, 79-81 % exact against the LLM's 58 %, ~10-60 ms
instead of 0.5-9 s) and makes the registry complete (samples, Italian names, four more commands, `voice registry`).

## What it does

Push-to-talk, then the headset does the rest, privately, with no cloud:

```
 TALK (dock) / gesture voice:toggle / mcx-ui voice listen
   -> microphone (Unity, 16 kHz, stops after ~1 s of silence)
   -> speech-to-text (Whisper base, local AI service)
   -> fast path: a phrase template of a command matches        -> run it   (~0 ms)
      else the decision model: each part of the sentence is compared with every command's samples,
      examples and phrases (multilingual sentence vectors); the best command, or none; values read
      from the command's schema                                 -> run them in order (~10-60 ms)
      else (only with "Ask the LLM when nothing fits") the local LLM writes the command lines (~0.5 s)
   -> HUD above the dock ("✓ Opened GIT"); actions are confirmed there only (0.6: a spoken "Opened GIT"
      arrived seconds after the action and was annoying); spoken replies are for answers (commands marked
      `speak`, e.g. a plugin's `voice.command{speak=true}`)
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
| `open_panel` | `panel`: terminals files git agents threads web (the browser) links (bookmarks, local servers) scripts gestures vault settings voice messages … | "open [the] {panel} [page]", "show me the {panel}", "go to {panel}" + synonyms (providers -> agents, source control -> git, secrets -> vault, ...) |
| `close_panel` | `panel` | "close [the] {panel}" |
| `close_all_panels` | | "close all [windows]", "clear the view" |
| `new_terminal` | | "open a new terminal", "new shell" |
| `find_thread` | `query` | "find the thread where {query}", "search threads for {query}" |
| `switch_workspace` | `name` (optional) | "switch to the workspace called {name}", "change workspace" |
| `thread_sidebar` | `action` (open, close), `tab` (changes, files, git, status, views) | "open the sidebar on the git tab", "chiudi la barra laterale" (while a thread is open) |
| `init_git` | none | "make this folder a git repository" (only while the open thread's folder is no repo) |
| `set_layout` | `mode`: fixed float | "use the floating layout", "fixed mode" |
| `recenter` | | "recenter", "bring the panels back" |
| `type_text` | `text` | "type {text}" |
| `run_in_terminal` | `command` (confirmed in VR) | "run {command}" |
| `show_keyboard` | | "show the keyboard" |
| `set_focus` | `state`: on off | "focus mode on", "turn off the focus", "darken the room" (Ambient Focus) |
| `set_setting` | `setting` (see below), `value`: on, off, toggle, a number, + or - | "set the dome radius to two meters", "intensity 30", "turn off the sounds", "make the windows bigger", "volume a bit less" |
| `focus_preset` | `preset`: subtle focus blackout | "use the subtle preset", "blackout preset", "usa il preset leggero" |
| `spin_carousel` | `direction`: left right, `panels` (1-6, optional) | "spin the carousel to the right", "next panel", "gira il carosello a sinistra" (float layout) |
| `select_mode` | `state`: on off | "turn on the selection tool", "stop selecting", "attiva la selezione" |
| `open_url` | `url` (spoken: "github dot com slash docs") | "browse to example dot org", "open the website github dot com", "apri il sito github punto com" |

Every command also carries **samples**: 4-15 natural requests in English and Italian that the decision model
compares with (none of them copied from the bench sentences). Panels and settings have Italian names too
(`impostazioni` -> settings, `cassaforte` -> vault, `morbidezza` -> focus_softness, `cupola` -> focus_radius...;
`VoiceSettingOther` keeps them out of the speech-to-text prompt).

`open_panel` also knows panels by their tabs: `focus` (Settings, FOCUS tab: "open ambient focus"),
`storage` and `tools` (SYSTEM: "show the installed tools", "how much disk space is left"), `git` (the
vault's GIT tab).

`set_setting` settings (spoken names in brackets): `focus` (ambient focus, the dome), `focus_intensity`
(intensity, darkness), `focus_radius` (radius, dome radius, dome size), `focus_blur` (blur), `occlusion` (real things in front of windows), `depth_camera` (sharp depth), `focus_softness`, `opacity`,
`panel_distance`, `ui_scale` (scale, windows, text size), `carousel_radius`, `bend`, `sounds`,
`volume`, `speak`, `clap`, `hand_rays`, `foveation`, `views_auto_open`. Numbers are
spoken freely: "zero", "fifty percent", "two point five", "30 cm", "max", "half", "a quarter"; "30" on a
0-1 setting means 30 %. More / less words ("bigger", "darker", "a bit less") move it by a tenth of its
range. The slider or toggle in an open panel moves with it.

### Misheard words and several commands at once (0.7)

- **Vocabulary.** Every word a command uses (its phrases, synonyms, enum values and the `vocabulary` it
  declares) goes into the speech-to-text prompt, so Whisper hears "ambient focus" and not "ambient fogus".
  When it still mishears, a word that matches no phrase is replaced by the nearest command word (edit
  distance 1 for 4-6 letters, 2 from 7 letters) and the request is matched again; the HUD shows the fixed
  words.
- **Slots split right.** An enum slot tries its known words first, longest first ("dome radius" before
  "dome"), and a command can reject arguments that do not fit (`Check`: "radius to two meters" is not on /
  off), so the next phrase is tried instead of running a wrong command.
- **Compound sentences.** "Turn on ambient focus and set intensity to 0": when the whole sentence matches
  nothing, it is split on "and", "then", "also" and commas; when every part matches, they run in order and
  the HUD joins the replies. The LLM may also answer with up to three lines joined by " ; " (the grammar
  allows it).
- **Context for every command, like MCP.** Plugins describe their commands as fully as the shell does:
  `description`, typed `params` with `enum`, `min` / `max` / `unit`, `synonyms`, `vocabulary` and
  `examples` (skill `quest-scripting`, "Voice commands"). The LLM sees the same schema the fast path uses.

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

## The decision model (0.7.2)

Jev-style, without generating text: the options are the registry's enabled commands plus "none"; the answer is
a command and its values, or nothing. Chosen by measurement (`docs/VOICE_DECISION_RESEARCH.md`,
`tools/voicebench/`): on 355 sentences (English and Italian; single, composite, misheard, chit-chat) today's
pipeline (fast path -> Qwen3 0.6B) was 58 % exact (Italian 34 %), fast path -> this model 79-81 % (Italian
70-72 %), and the decision takes ~6-10 ms on the PC instead of ~205 ms for the LLM.

- **Model:** multilingual-e5-small (118M parameters, q8_0 GGUF, 132 MB) in the local AI service
  (`POST /v1/embeddings`, `@qvac/embed-llamacpp`), on the service's cores 0-2, unloaded after 60 idle minutes.
  Installed with the voice models; a headset that already has them fetches it once by itself.
- **Examples** (`VoiceDecider.BuildExamples`): each enabled command's phrase templates expanded with its enum
  values and synonyms, its examples, samples and description; "none" from chit-chat in both languages. Their
  vectors are cached on disk (`voice-decider-vectors.bin`, ~2 MB) and rebuilt only for what changed when a
  plugin adds, removes or disables commands; a removed or disabled command is never offered, even before the
  rebuild.
- **Clauses:** the sentence is split on and / then / also / after that / commas and e / e poi / poi / anche,
  keeping "and a half", "e mezzo" and decimal commas; fillers and punctuation are dropped. Each part is decided
  on its own and they run in the spoken order ("turn on ambient focus and set the intensity to 100 %": focus on,
  then intensity). A part without a verb repeats the previous command with its own values ("imposta il volume
  al 30 per cento e la morbidezza a mezzo metro").
- **Choice:** the best cosine against each command's nearest example; "none" wins chit-chat, and below 0.30
  nothing runs. Commands that take words verbatim (`type_text`, `run_in_terminal`, `open_url`) run only when the
  part starts with their own verb.
- **Values from the schema:** enum values and synonyms (longest first, then one or two letters off), numbers
  with units and percent (English and Italian words: "ventitré", "due metri e mezzo"), on / off, more / less
  (the most specific word wins: "più trasparenti" is less opacity), free text without the command's own words.
  The command's `Check` and the schema validate before anything runs.
- **Settings:** VOICE > "Decision model for commands" (on), "Ask the LLM when nothing fits" (off). The VOICE
  status line shows the model's state ("ready: 1500 examples, 17 commands").
- **The LLM** stays for answers and, when that toggle is on, for sentences the model does not place.

## The registry as JSON (agents, plugins)

`mcx-ui voice registry` (control port), `voice.registry()` (Lua) and the bench export read
`VoiceCommands.Describe()`: every enabled command with its schema (enum values, ranges, units), synonyms, phrases,
examples, samples, vocabulary, flags and `warnings` (`VoiceCommands.Lint`: what it still lacks). A plugin hears its
warnings in its log when it registers. `mcx-ui voice decide "<text>"` shows what the decision model would run,
with each part's command, score and runner-up; nothing runs. Rules for every command, shell or plugin:
`AGENTS.md` ("The command registry") and the in-app skill `quest-scripting` ("MUST").

## The local AI service

QVAC (Tether's local AI stack) on the Bare runtime, in its own process: `qvac/server/` (Bare JS),
packed by `tools/qvac-bundle.sh` into `StreamingAssets/qvac/runtime.tar.gz` (24 MB: QVAC addons for
android-arm64 with CPU backends only, `libc++_shared.so` from NDK r29) plus `libbare.so` (the Bare
executable, 62 MB, stripped) in `Plugins/Android/libs/arm64-v8a`. Models are not in the APK: VOICE >
INSTALL MODELS downloads them once (660 MB) into the app's private storage, each checked against the
sha256 of the QVAC model registry.

| Model | Size | Source | Use |
|---|---|---|---|
| Whisper base q8_0 (multilingual) | 78 MB | Hugging Face (ggerganov/whisper.cpp, pinned revision) | speech-to-text |
| Qwen3 0.6B q4_0 | 364 MB | Hugging Face (unsloth/Qwen3-0.6B-GGUF, pinned revision) | picks commands (tool calling), short answers |
| Supertonic 3 q4_0 | 81 MB | QVAC model registry (Hyperswarm) | spoken replies, 31 languages |
| multilingual-e5-small q8_0 | 126 MB | Hugging Face (TwinSunsLLC/multilingual-e5-small-gguf, pinned revision) | the decision model's sentence vectors |

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
| `POST /v1/models/install` `{kinds:["asr","llm","tts","embed"]}` | downloads in the background; progress in `/status` |
| `POST /v1/embeddings` `{input: text or [texts]}` | OpenAI-style sentence vectors (multilingual-e5-small, L2-normalised; prefix texts with `query: `) |
| `POST /v1/audio/transcriptions` | WAV body, raw `audio/pcm;rate=` or OpenAI-style multipart (`file`) -> `{text, ms, audioMs}` |
| `POST /v1/audio/speech` `{input, voice}` | WAV (44.1 kHz, 16-bit mono) |
| `POST /v1/chat/completions` | OpenAI-style, with `tools` -> `tool_calls`; `timings` |
| `POST /v1/warmup` `{messages:[system], tools}` | prefills and saves the prompt prefix |
| `POST /config` | threads, language, vocabulary prompt, voice, idle minutes |
| `POST /unload`, `/pause`, `/resume`, `/shutdown`; `GET /log` | control |

From the Linux userland: `mcx-ai status | install | transcribe f.wav | say "text" | chat "prompt" | env`.
From the app's control port: `mcx-ui voice status | list | match <text> | run <text> | say <text> |
wav /mnt/quest/f.wav | listen | install | threads N | lang en|it|auto | registry | decide <text> | decider on|off |
fallback on|off`.

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

## Spoken replies, sentence by sentence (feat/polish, 2026-11-12)

Before: one request for the whole reply, and playback only after the last sample was made. Synthesis time is
linear in characters (Supertonic 3, 5 steps, 2 threads; PC service API: 11 chars 157-197 ms, 103 chars 558-792 ms,
303 chars 1929-2463 ms, its first sentence alone 250-337 ms = 13 %). The headset runs at RTF 1.0-1.3, so a
2-sentence answer stayed silent 8-24 s after its text existed.

Now (`ShellVoice.Say`, `Runtime/AI/SpokenText.cs`):

| Step | What it does |
|---|---|
| Split | `SpokenText.Sentences`: after `. ! ? …` when a space and a word that is not lower case follow; not inside `foo.cs`, `3.5`, `v1.2.4`, `e.g.`, `Dr. Rossi`, `J. Smith`, `No. 5`, `wait... then`, Italian `sig.`; line breaks, list items and ` · ` (joined replies) end a sentence; code blocks are not read. A sentence under 20 characters goes with the next one (a tiny clip costs a request and leaves a gap); one over 180 is cut at a comma. |
| Shorten | A reply (`SayReply`: answers, session questions) speaks at most 2 sentences / about 200 characters; the full text stays on the HUD. `SAY…`, `voice say`, Lua `voice.say` speak everything. |
| Speakable | `SpokenText.Speakable`: `3 min 20 s` -> `3 minutes 20 seconds`, `1 h`, `250 ms`; Italian words when the reply reads as Italian (`3 minuti`). `ThreadPulse.Speak` writes its durations as words itself (`SpokenText.Duration`). Before, "3 min" came back from speech-to-text as "three men". |
| Pipeline | One request at a time, at most two clips made ahead: sentence 2 is made while sentence 1 plays; a Stop never waits behind a long service queue. Clip 1 plays as soon as it arrives; the next one follows 0.2 s after the previous ends (`SentenceGap`). |
| Cancel | Stop (the HUD's ■ STOP, `voice:stop` / `voice:cancel`), TALK and a new reply call `StopSpeaking`: the playing clip stops, waiting clips are destroyed, late answers of the old reply are dropped (a generation counter). |
| Orb | `OnSpeechStarted` runs per clip: the orb speaks each sentence, quiet in the gap. |
| CPU Boost | Held while the reply speaks (was a fixed 15 s), 0.5 s after the last clip, capped at 60 s from its start (`SpeechBoostCap`). Boost makes synthesis 16-18 % faster, which brings the headset to about RTF 1: sentence 2 is ready when sentence 1 ends. |
| Model kept loaded | TTS idle unload 3 -> 15 min (`PushVoiceConfig`): most replies no longer pay the 1-4 s reload. Costs about 0.5 GB RSS while loaded. |
| Language | `ttsLanguage` = `it` when VOICE > LANGUAGE is IT, else `en` (Supertonic 3 speaks Italian; a change reloads the voice once). |
| Shorter answers | Session answers: LLM `max_tokens` 110 -> 70, the prompt asks for one or two short sentences under 40 words; an answer cut by the limit keeps only its finished sentences (`CleanAnswer`). |
| Clips | Every played or dropped `AudioClip` is destroyed (before, a replaced clip stayed in memory until Unity unloaded unused assets). The WAV is decoded without a call per sample. |

Expected on the headset (scaled from the PC at about 8x; the owner's headset check is open): first audio for
a 2-sentence answer about 3-4 s after the text exists (was 8.5-9.5 s), for long replies about 3 s (was 21-24 s),
and 1-4 s less on most first answers (no reload). Unchanged on purpose: Supertonic steps 5 (2-4 garble or drop
words in the speech-to-text round trip), 2 threads (72 fps), CPU only (GPU 12 % faster, not worth sharing it),
and speed 1.0 (1.15 is 9 % shorter and still exact in the round trip, but speed is the owner's ear: not changed).
Next step, not done: server-side chunk streaming (`streamChunkTokens`) and LLM token streaming into this queue.

Tests: `tests/voicefx/run-voicefx-tests.sh` (splitter, units, shortening; also run by
`tests/terminal/run-core-tests.sh`), Editor self-test section `spokenreplies` (a fake speech service: one request
in flight, first clip at once, Stop and a new reply drop late clips, clips freed, the VOICE panel's ■ STOP / ▶ START
button and the COMMANDS row hint).

## Speech quality: FAST (4 steps) or CLEAR (5) (feat/polish, 2026-11-12)

VOICE > SPEECH (setting `voice.speech`, voice `set_setting speech_quality fast|clear`) picks Supertonic's diffusion steps,
sent to the service as `ttsSteps` (a change reloads the voice model once). FAST = 4 is the default, CLEAR = 5 (the model
default). Measured on the PC, linux-x64 addon, 2 threads on 2 cores, warm, median of 3, two interleaved rounds:

| Text | chars | audio | 5 steps | 4 steps | 4 vs 5 |
|---|---|---|---|---|---|
| "Opened the agents panel." | 24 | 1.97 s | 599-661 ms | 500-502 ms | -17 to -24 % |
| "Three threads are working. Claude finished the login fix." | 57 | 4.23 s | 1137-1403 ms | 949-953 ms | -17 to -32 % |
| a 127-char status line | 127 | 8.32 s | 2020-2033 ms | 1687-1724 ms | -15 to -17 % |
| a 303-char paragraph | 303 | 18.40 s | 4615-4662 ms | 4060-4421 ms | -4 to -13 % |

Speech-to-text round trip (the service's Whisper base): 5 steps exact on all five; 4 steps (identical in both rounds) "a
login fix" for "the login fix", "most the project" for "most of the project", "came out" for "came up": small words slip,
nothing becomes unreadable. Synthesis on the PC runs at 55-70 characters a second (speech plays at about 16 a second);
the headset is about 4-6x slower than this loaded PC, so there Supertonic makes speech about as fast as it plays
(RTF ~1.0-1.3 at 5 steps, ~0.85-1.1 at 4). The 0.6B LLM writes about 14 tokens (~55 characters) a second on the same cores:
the voice, not the LLM, is the slow part of a spoken answer.

## Voice effect (feat/polish, 2026-11-12)

The owner: "post process the voices so they sound more like an AI of a true sci-fi, just tweaking the output".
`Runtime/AI/VoiceFx.cs` (pure C#, no UnityEngine) processes each sentence's WAV in `QvacClient.Speak`, on the
worker thread that fetched it, never on the main thread. If it fails, the sentence plays dry.

| Setting `voice.fx` | VOICE > SOUND | What it is |
|---|---|---|
| `off` | OFF | The service's voice, only trimmed and levelled (no filter: correlation 0.9994-1.0000 with the dry voice on the 8 sample replies). |
| `adjutant` (default) | AI | A calm computer, clearly the same voice: presence and air, two faint detuned doubles (+9 / -11 cents at -21 dB), a light metallic comb and flanger, glue compression, soft saturation, a small bright plate (RT60 0.55 s at -22 dB). |
| `starship` | SHIP | Clearly synthetic: an octave-down layer, a 1.9 kHz ring-mod sheen on the highs, stronger doubles and comb, a longer plate (0.85 s) with an octave-up shimmer in the tail. |
| `comm` | COMM | A comm channel: 380-3500 Hz, an 8 kHz sample-and-hold grit, static that rides the speech, a squelch burst that opens the reply (first sentence) and closes it (last sentence), almost no tail. |

Every preset, off included:
- Trims the silence the service pads around speech (gate -60 dBFS, 25 ms kept in front, 20 ms behind). On the 8
  sample replies it cuts 346 ms in front and 398 ms behind on average: the first word comes that much sooner.
- Levels to -18 LUFS (BS.1770 K-weighted, gated), at most +24 dB (near silence is not pumped up), with a 2 ms
  look-ahead limiter and a hard ceiling at -1 dBFS. The dry service audio hard-clips (7 of 8 replies have 10-272
  samples at full scale) and sits at -14.8 to -18.1 LUFS, so a reply is now about 2 dB quieter on average, never
  clipped, and every preset is equally loud (within 0.11 LU of off on the sample replies).
- The tail: after the reply's last sentence the preset's reverb (0.2-0.85 s); between sentences 0.1 s, so the
  0.2 s gap between sentences stays short.

Changing it: VOICE > SOUND (a pick says "Systems online. All panels ready." with the new sound when the service
runs), voice or the LLM (`set_setting voice_effect`: off / adjutant / starship / comm, or AI, ship, radio, robot,
normal, "effetto vocale radio", ...; + / - step through the list; on = adjutant), the control port
`voice fx off|adjutant|starship|comm`, MQH (`quest.settings`, key `voice.fx`).

Measured (tests/voicefx, Windows dev PC, Unity's bundled .NET 8 with Unity's Roslyn, netstandard 2.1):

| Preset | 5 s of synthetic speech | 7.45 s service reply | standalone bench, 7.57 s reply (draft in the same run) |
|---|---|---|---|
| off | 24 ms (4.8 ms per s) | 26 ms (3.5 ms per s) | - |
| adjutant | 100 ms (19.8) | 119 ms (16.0) | 96 ms = 12.7 ms per s (draft 11.4) |
| starship | 177 ms (35.1) | 212 ms (28.4) | 165 ms = 21.8 (draft 21.1) |
| comm | 82 ms (16.3) | 108 ms (14.5) | 84 ms = 11.1 (draft 10.1) |

Headset estimate (not measured; the PC is 4-6x faster than a Quest 3S core): adjutant about 50-120 ms per
second of speech, starship 90-210, comm 45-100. Only the first sentence's cost is felt (the next one is made and
processed while the first plays), and trimming gives back about 0.35 s in front. For a 3 s first sentence:
off starts about 0.35 s sooner than before, adjutant costs 0.15-0.36 s (about what trimming gives back), starship
0.27-0.63 s (up to about 0.3 s later than before), comm 0.13-0.29 s.
Memory: scratch buffers are pooled (16 per type, the larger ones kept): a 5 s sentence allocates 483 KB for a
481 KB output, a run of 7 sentences of all presets 1722 KB for 1722 KB of output, so no garbage beyond the WAV
itself. The pool is dropped 2 min after the last reply (`VoiceFx.ReleaseScratch`).
Intelligibility proxy (the service's Whisper base on 12 replies x 2 voices, 142 words; it mishears even the
clean voice, about +-3 points is noise): clean 60.6 % word errors, adjutant 62.7 %, starship 73.2 %, comm
59.9 %. Adjutant and comm cost no words for the recogniser; starship does (a human ear may differ).

Retuning: `tools/voicefx/` (`fx.py` = the same chain in numpy, `PRESETS` 1:1 with `VoiceFxParams.For`; `asr.py`
for the round trip; README there). `tests/voicefx/run-voicefx-tests.sh` checks: off = trim + level (correlation
with the dry voice > 0.995), edge inputs (empty, 1 and 63 samples, silence, near silence, DC, square, noise, NaN,
inf, 8-48 kHz, rate 0) finite and under the ceiling, peaks <= -1 dBFS, loudness within 1 LU of off, the same
output twice and from two threads at once, the WAV header / rate / length, the tails, the allocations, the chain
time; with the samples folder also the service's own replies and parity with the prototype's renders
(correlation adjutant 0.995, starship 0.986, comm 0.974). Editor self-test section `voicefx`.

## GPU? (measured 2026-11-08, Quest 3S, the app not running)

QVAC's Android prebuilds carry an OpenCL backend for Adreno (and Vulkan). On the headset it works once
`libqvac-ggml-vulkan.so` is next to the OpenCL one (fabric detects the Adreno version through Vulkan; without
it: "no adreno GPU version found (-2) removing OpenCL backend") and `LD_LIBRARY_PATH` includes
`/vendor/lib64`. Forcing the Vulkan backend aborts (SIGABRT).

| | CPU | GPU (OpenCL) |
|---|---|---|
| LLM (Qwen3 0.6B q4_0) | 37 tok/s with 2 threads, 57 with 6 | 13.8 tok/s (29/29 layers on the GPU; first load 29.6 s compiling kernels) |
| Speech-to-text (Whisper base) | 430-450 ms | 328-346 ms (-22 %) |
| Text-to-speech (Supertonic 3) | 1.7-3.7 s | 1.5-3.2 s (-12 %) |

So the LLM stays on the CPU (2.7x slower on the GPU), and the small speech gains do not pay for sharing
the GPU with the app's rendering (frame rate first) and the extra 70 MB of libraries. What does cost time
on the headset is the app itself: with MetaCodex running the service gets 14-17 tok/s instead of 37 (CPU
level, the shared cores, memory). Kept as measured for a later decision; not in the APK.

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
  LLM path); `decider` (the decision model's mechanics with an oracle embedder, the registry complete, a
  plugin's commands gone when it is disabled); named `voiceexport,voicedecide` (the 355 bench sentences through
  the app's pipeline with the real model in the dev service -> `Bench/voice/app.jsonl`, scored by
  `tools/voicebench/score_app.py`).

## Files

| Piece | Path |
|---|---|
| Service (Bare JS) | `qvac/server/main.js`, `engines.js`, `models.js`, `audio.js`; `qvac/package.json` + `bun.lock` |
| Bundle and dev runners | `tools/qvac-bundle.sh`, `tools/qvac-dev.sh`, `tools/remote-unzip.py` |
| Process, client | `unity/Assets/MetaCodex/Runtime/AI/QvacHost.cs`, `QvacClient.cs` |
| Microphone | `Runtime/AI/VoiceRecorder.cs` |
| Registry, fast path, registry JSON and lint | `Runtime/AI/VoiceCommands.cs` |
| Decision model | `Runtime/AI/VoiceDecider.cs`; bench `tools/voicebench/` (`bench.py`, `score_app.py`, `results/`) |
| Pipeline, commands, VOICE panel, HUD | `Runtime/Shell/ShellVoice.cs` |
| Spoken text (sentences, units), voice effect | `Runtime/AI/SpokenText.cs`, `Runtime/AI/VoiceFx.cs`; tests `tests/voicefx/`; retuning `tools/voicefx/` |
| Lua API | `Runtime/Scripting/ScriptHost.cs` (`voice.*`) |
| Userland | `userland/overlay/usr/local/bin/mcx-ai`, `mcx-ui voice ...` |
