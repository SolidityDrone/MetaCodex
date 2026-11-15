# The local AI service: where it runs and how it keeps the frame rate

The local AI service runs the interface AI on the headset: speech-to-text, a small language model, text-to-speech,
the voice decision model's sentence vectors and the optional local coder. It is the private, local interface to the
app: it never writes code and is not a provider of the agent threads (the local coder is the one exception, see
[local-coder.md](local-coder.md)). [voice.md](voice.md) describes the voice path that uses it.

## Where it runs

```
 :ai (MetaCodexAiService.java, cores 0-2)                 main process (cores 3-5)
   libbare.so server/main.js  127.0.0.1:47810, token  <--HTTP--  the app (QvacHost, QvacClient: mic, speaker, commands)
   MQH, agent CLIs, build daemon (proot)               <--HTTP--  mcx-ai in the Linux
```

- The headset runs the immersive app and all its child processes in one CPU set, cores 3-5, with the frame loop.
  Only a separate Android process of the app reaches cores 0-2. For this reason the service lives in the app's own
  Android service process `:ai` (`MetaCodexAiService.java`), which starts the Bare JavaScript runtime as a child:
  `taskset 7 nice -n 10 libbare.so server/main.js --data files/qvac/data --token-file files/qvac/token --port 47810
  --threads 2`. MQH, the agent CLIs and the build daemon run under `:ai` too.
- While `:ai` has children, it is a foreground service; a build also holds a wake lock
  ([native-build-and-install.md](native-build-and-install.md)).
- The app checks the service's health every 5 s; three missed checks restart it.
- Packaging: `tools/local-ai-bundle.sh` packs the service and five addon families for android-arm64 (CPU only) into
  `StreamingAssets/qvac/runtime.tar.gz` (25,015,017 bytes; 62,791,511 unpacked). The addons need the NDK r29
  `libc++_shared.so`. The four core models ship next to it ([ready-on-install.md](ready-on-install.md)); the first
  start unpacks the runtime and copies the models (`QvacHost`).

## Rules for a stable frame rate

1. **Never in the app's process.** A native crash or a long stall in an inference library cannot reach the frame
   loop.
2. **CPU only.** The GPU is the frame-time bottleneck and belongs to the app; the NPU is closed to apps. A GPU test
   was slower for the language model (13.8 tok/s against 37 on 2 CPU threads), so every model runs on the CPU.
3. **Few threads, low priority, away from the frame loop's cores**: 2 inference threads at nice 10, on cores 0-2
   (the test headset has 6 cores: 0-1 at 2.05 GHz, 2-5 at 2.36 GHz).
4. **On demand only.** Push-to-talk speech-to-text, short spoken replies, one decision per clause. No always-on
   inference. Idle models unload with the times the app sends in its config (`ShellVoice`): speech-to-text after 10
   minutes, the language model and the voice after 15; the decision model stays loaded; the local coder goes after
   10. The service's own defaults (30, 30, 10 and 60 minutes) apply only before the app's config arrives.
5. **Small quantized models**: Whisper base (q8_0), Qwen3 0.6B (q4_0), multilingual-e5-small (q8_0) and a
   text-to-speech model with 10 voices (q4_0).
6. **The app owns the frame budget.** It can pause the service (`POST /pause`) and resume it.
7. **Measure frame time, not only speed.** Inference uses memory bandwidth and slows the whole chip even on "free"
   cores: the gate is the frame time with inference running.

## Engine settings

Speech: `use_gpu false`, Whisper `audio_ctx` 512 at least. Language model: `device cpu`, `ctx_size 2048`, `temp 0`,
`reasoning_budget 0`, `predict 256`. Embeddings: `device cpu`, batch 512, context 512. Voice: no GPU, 3 diffusion
steps (FAST) or 5 (CLEAR). Each model file is pinned by revision and SHA-256 in `local-ai/server/models.js`.

## API

`127.0.0.1:47810`, `Authorization: Bearer <token>`; the token is random at every start and lies in the app's files.
OpenAI-style where a standard exists:

| Endpoint | Use |
|---|---|
| `GET /health`, `GET /status`, `GET /log` | health, models loaded, memory, threads; the service log |
| `POST /v1/audio/transcriptions` | speech-to-text of a push-to-talk clip |
| `POST /v1/audio/speech` | spoken replies |
| `POST /v1/audio/wake` | the "Hey Whisp" wake check: a short greedy decode of a voiced segment, never queued (docs/voice.md) |
| `POST /v1/chat/completions` | the language model (answers, recaps, the optional command fallback) |
| `POST /v1/embeddings` | the decision model's sentence vectors |
| `POST /v1/models/install` | downloads into a development APK without the models, and the local coder |
| `POST /v1/warmup`, `/config`, `/unload`, `/pause`, `/resume`, `/shutdown` | control from the app |
| `/v1/chat/completions`, `/v1/responses`, `/v1/messages` with the coder's key | the local coder ([local-coder.md](local-coder.md)) |

From the Linux: `mcx-ai status|install|transcribe f.wav|say "text"|chat "prompt"|env|log`.
For development: `tools/local-ai-dev.sh pc start` runs the service on the PC, `tools/local-ai-dev.sh device start`
on the headset over adb, without an APK.
