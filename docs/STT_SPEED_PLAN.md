# Faster speech-to-text (research, 2026-11-09)

Owner: "figure out if we have any way to make the STT faster, what are the numbers so far?"

## Numbers so far (Quest 3S, MetaCodex running at 72 fps)

| Stage | Time | Source |
|---|---|---|
| End of speech: silence wait | 0.7 s | `VoiceRecorder` (was 1.0 s before 0.5) |
| Speech-to-text, Whisper base q8_0, 2 threads, encoder window 512 frames (~10 s) | **median 1.08 s**, p75 1.38 s, min 0.02 s, max 6.6 s (a reload after idle) over 37 real commands | headset session log, 2026-11-08 |
| Same, API test with CPU Boost | 0.66-0.73 s | docs/voice.md, "Measured" |
| Decision model (0.7.2) | 17 ms median on the PC (HTTP + model); headset still to measure | tools/voicebench |
| **End of speech -> action** | **~1.8-1.9 s typical** (0.7 + 1.08 + decision) | |

Already measured and set aside: 4 threads 0.37-0.44 s but 3 stale frames per request (frame rate first); GPU
(OpenCL) -22 % but shares the GPU with rendering; Whisper tiny loops; an encoder window under 512 frames makes
Whisper base repeat and mishear (256 tested).

## Options, by gain for cost

1. **Encoder window sized to the clip, with an ACFT fine-tune.** FUTO's `acft-whisper-base` (multilingual, 99M,
   Apache-2.0, a q8_0 build for whisper.cpp) is fine-tuned to stay accurate when `audio_ctx` follows the clip
   length, which is exactly what stops us at 512 frames. A 2-3 s command would need ~150-200 frames: the encoder
   (most of the time for a few output tokens) does 2.5-3x less work. Estimate: 1.08 s -> ~0.45-0.6 s. Cost: a model
   swap (`qvac/server/models.js`, sha256, pinned revision) and `minAudioCtx` by measurement; the service already
   sets `audio_ctx` per clip (`engines.js`, reload is instant).
2. **Speculative transcription.** Start speech-to-text after ~0.3 s of silence; if no more speech comes by 0.7 s,
   the text is already there, otherwise drop it and run again. Saves ~0.35 s on every request with no accuracy
   cost (the cores are idle while the user is silent). App-only change (`VoiceRecorder`, `ShellVoice`).
3. **No cold reloads.** The app tells the service to unload speech-to-text after 10 idle minutes; the next command
   then takes several seconds (the 6.6 s above). Keep it loaded while voice is on (0.28 GB) and load it when TALK
   is pressed, during the recording.
4. **Not now:**
   - Parakeet TDT 0.6B v3 (25 European languages including Italian, in QVAC as `@qvac/transcription-parakeet`):
     batch only, 6x the parameters of Whisper base, no ARM CPU numbers published (x86 int8: ~0.03 real-time factor).
   - QVAC's streaming Parakeet with end-of-utterance detection (and the "unified" one): English only.
   - Moonshine (fast on short clips, streaming): a separate model per language, no Italian.
   - GPU and 4 threads: measured, they cost frames.

**With 1 + 2 + 3:** end of speech -> action ~0.35 + ~0.5 + ~0.05 = **~0.9 s** instead of ~1.85 s; the 6 s
outliers gone.

## Plan

1. PC bench: speak the bench sentences with Supertonic (English and Italian voices), transcribe with Whisper base
   at 512 and with `acft-whisper-base` at 512 / 256 / 192 / 128 frames; word error and command accuracy through the
   app's pipeline (fast path + decision model), time per clip.
2. If the fine-tune holds accuracy: swap the model, set `minAudioCtx` from the bench, add speculative transcription
   and keep-warm in the app; headset numbers from the session log; then a build.

## Sources

- FUTO ACFT models: https://huggingface.co/futo-org/acft-whisper-base, https://huggingface.co/futo-org/acft-whisper-tiny,
  https://awesome.ecosyste.ms/projects/github.com%2Ffuto-org%2Fwhisper-acft
- Short clips and `audio_ctx` in whisper.cpp: https://github.hscsec.cn/ggerganov/whisper.cpp/issues/1855
- QVAC transcription: https://docs.qvac.tether.io/sdk/ai-capabilities/transcription/,
  https://docs.qvac.tether.io/addons/transcription-parakeet/, https://www.npmjs.com/package/@qvac/transcription-parakeet
- Parakeet TDT 0.6B v3 int8 ONNX: https://huggingface.co/CoderViking/parakeet-tdt-0.6b-v3-onnx,
  https://huggingface.co/talatapp/parakeet-tdt-0.6b-v3-onnx-split
- Moonshine: https://moonshine-voice.readthedocs.io/en/latest/moonshine-vs-whisper/, https://arxiv.org/abs/2509.02523
