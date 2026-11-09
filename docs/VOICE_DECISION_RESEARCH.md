# A Jev-style decision layer for voice commands (research, branch `feat/kev`, 2026-11-09)

Owner: take the LLM out of the voice-command loop and use a Jev / Kev-like decision model instead; keep the LLM for
other things. Goals: local, fast, light (the app stays smooth at 72 fps), at least as accurate as today's LLM; take
special care of composite commands where order matters ("turn on ambient focus and set intensity to 100 %"). Also:
can QVAC use the GPU for faster speech-to-text or LLM?

## Where the time goes today (headset logs, Quest 3S, app running)

| Stage | Time | Notes |
|---|---|---|
| End of speech | ~1 s | VoiceRecorder stops after ~1 s of silence |
| Speech-to-text (Whisper base q8_0, CPU) | 0.6-1.2 s warm, several s cold | |
| Interpretation, fast path (phrase templates) | ~0 ms | most commands |
| Interpretation, LLM (Qwen3 0.6B q4_0, GBNF grammar, cached prefix) | 0.5-0.7 s warm, 2.7-9 s cold | and wrong: "Turn off ambient fogus" -> `set_layout` four times |

The LLM path is the slowest and least reliable part; speech-to-text plus the silence wait is the next ~2 s.

## What Jev-style models are (verified 2026-11-09)

- **Jev** (TypeSafe, closed, hosted): returns calibrated probabilities over options you give it (choice, score, yes/no)
  instead of text. Not local.
- **Kev 0.8B** (Jared Palmer, Apache-2.0): Qwen3.5-0.8B-Base + LoRA + a *pointer head* that scores each option's closing
  token against the question's final token (T ~ 2.35). GGUF 814 MB (q8_0; demo quant 466 MB, less accurate); the head
  needs a Kev-enabled llama.cpp **fork** (stock llama.cpp ignores it). Reported accuracy varies by source: 0.65-0.70 on
  the author's new-source split, 73.4 % on Banking77, 57.7 % on LangWatch's tool routing. ~110 ms on a desktop CPU (Q8).
- **Laya** (Convai, Apache-2.0): 421M ModernBERT-large encoder with a decision head, non-autoregressive (33 ms on a T4
  GPU, vendor figure); weak out of the box (0.425 on Banking77 against Jev's 0.870).
- **jevlike** (MIT): a trainer, not a model: options act as queries over the context, a shared dot product scores each
  pair, softmax gives probabilities; works on a frozen pretrained encoder with only a small head trained (a router on a
  frozen Qwen2.5-0.5B trained on 1.4k rows in ~2 min on a CPU).
- **FunctionGemma 270M** (Google): generative function calling; 58 % base, 85 % fine-tuned on Mobile Actions; phone
  latencies reported from 0.3 s to 13 s end to end. Still a decoder: same cost class as today's LLM.

## Why not Kev or Laya on the headset

- **Compute is what matters here:** ~2 x parameters x tokens read per request. Today's LLM path reads only the user's
  ~20 words (the system prompt and options are cached) and writes 1-4 tokens: 0.5 s warm on the shared CPU. Kev (0.8B)
  and Laya (0.4B) read the question *and every option* each time (100-300 tokens): on this CPU that is no faster, often
  slower, and 0.5-0.8 GB more memory next to Unity.
- **Our runtime cannot run them:** QVAC's `@qvac/llm-llamacpp` 0.68 exposes generation only (grammar / JSON schema), no
  log-probabilities, logits or embeddings; Kev's head needs a custom llama.cpp build for Android.
- **Their accuracy on routing-like tasks is not better** than a tuned small model on our own commands.

## Recommendation: a tiny embedding router with Jev's semantics ("System 1" for commands)

Jev's *interface* (options in, calibrated probabilities out, a "none" option) with a model ~100x smaller:

1. **Encoder:** a static embedding model (Model2Vec "potion", 8-32M parameters: token vectors averaged; within ~2-8 %
   of all-MiniLM-L6-v2 on classification, 50-500x faster on CPU), run **in C# in Unity**: a WordPiece tokenizer and a
   lookup table (8-16 MB in StreamingAssets). Microseconds per utterance, no native library, works even while the AI
   service is down. Step up if the bench says so: all-MiniLM / bge-small (22-33M) as GGUF through QVAC's
   `@qvac/embed-llamacpp` (out of process, ~10-30 ms).
2. **Options = the command registry** (the "MCP-like" context pays off here): every command contributes example
   utterances from its phrase templates (expanded), `examples`, synonyms and `vocabulary`; plugins' commands are
   embedded when they register, no retraining. Score = similarity to the option's nearest examples, softmax with a
   calibrated temperature; **"none"** below a threshold (non-commands go to the LLM or are ignored).
3. **Hierarchical choices** keep option lists short: command first (`set_setting`), then its enum argument
   (`focus_intensity` among the settings, `vault` among the panels) on the same clause.
4. **Values by rules**, as today: numbers and units (`TryNumber`), on / off / toggle, more / less, free text captured by
   the matching template (thread queries, workspace names).
5. **Composites, in order:** split on "and", "then", "and then", "also", "after that", commas (already done for the
   fast path); each clause decided on its own; run in the spoken order (left to right). A clause that still carries a
   second value ("turn on ambient focus at 100 %") yields a second command after the first.
6. **The LLM stays** for what is not a command (questions, chat) and, optionally, below the router's confidence.

Exposed "MCP-like" surface (registry unchanged for plugins): `voice.decide(text) -> [{command, args, p}]` ordered,
with `p` per command and `none`; `examples` / `vocabulary` / `phrases` become the router's training data, so rich
command context directly improves accuracy.

## GPU through QVAC (asked 2026-11-09)

- **LLM:** no. Measured on this headset 2026-11-08: 13.8 tok/s on the Adreno GPU (OpenCL) against 37 on 2 CPU threads.
  QVAC's own device table sends Adreno below 800 (the Quest 3S has an Adreno 740) to the CPU; llama.cpp's OpenCL docs do
  not list the 740 as verified; a Snapdragon 8 Gen 2 phone reports ~75 tok/s prefill on OpenCL for a ~0.5-1B Q4_0.
- **Speech-to-text:** Whisper on the GPU was 22 % faster (430 -> 335 ms) but shares the GPU with rendering (72 fps
  first): not worth it. The NPU is closed to apps (SELinux, tested in SandyBridge).
- **The bigger speech win is streaming:** QVAC now has `@qvac/transcription-parakeet` (ONNX Runtime) with a streaming
  end-of-utterance model and `transcribeStream()`: words are decoded while you speak and the end is detected without the
  1 s silence wait. Worth a separate test after the router (Horizon OS support of its NNAPI path is unknown; CPU first).

## Plan

1. **Bench on the PC first (no app code):** ~150 utterances (every command's phrasings, the misheard ones from the
   headset log, composites, non-commands); accuracy, "none" accuracy and latency for today's LLM (dev AI service, same
   model), the static router and a MiniLM router. Choose by the numbers.
2. Implement the winner as `VoiceDecider` beside `VoiceCommands` (fast path -> router -> LLM fallback), with self-tests
   on the bench set; then a build for the headset.
3. Later: streaming speech-to-text (Parakeet EOU) to remove the silence wait.

## Sources

- Jev: https://openrouter.ai/docs/guides/community/jev, https://flaviocopes.com/jev/
- Kev: https://huggingface.co/jaredpalmer/kev-0.8b, https://huggingface.co/espetro/kev-0.8b-gguf,
  https://github.com/jaredpalmer/kev, https://sotaaz.com/post/kev-mojev-bench-en, https://langwatch.ai/compare/jev-benchmark
- Laya: https://aiweekly.co/alerts/convai-ships-laya-a-421m-modernbert-decision-model-apache-20,
  https://www.layer3labs.io/comparisons/laya-vs-kev
- jevlike: https://github.com/vinnylarouge/jevlike, https://github.com/islee23520/omo-jevlike-router
- FunctionGemma: https://ai.google.dev/gemma/docs/functiongemma/model_card
- Model2Vec: https://www.tomaarsen.com/projects/model2vec, https://huggingface.co/minishlab/potion-base-4M
- QVAC: https://www.npmjs.com/package/@qvac/llm-llamacpp, https://docs.qvac.tether.io/addons/transcription-parakeet/,
  https://docs.qvac.tether.io/sdk/ai-capabilities/transcription/, https://www.pkgstats.com/pkg:@qvac/embed-llamacpp
- Adreno OpenCL: https://github.com/ggml-org/llama.cpp/blob/master/docs/backend/OPENCL.md,
  https://github.com/ggml-org/llama.cpp/issues/12462
