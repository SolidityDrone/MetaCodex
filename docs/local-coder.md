# The local coder: a coding model on the headset for the agent harnesses

The local coder lets an agent thread run with no network: a small coding model in the local AI service, reached by
the agent CLIs through the APIs they already speak. It is a demonstration that agents can run offline, not a strong
coder.

## What it is

- **The model**: Qwen2.5-Coder 0.5B Instruct, GGUF q4_0 by the Qwen team (430 MB, Apache-2.0, see
  [LICENSES.md](LICENSES.md)). Engine `coder` in the local AI service (`local-ai/server`): 16k context, f16 KV cache,
  3 threads on the AI cores 0-2, unloaded after 10 idle minutes. While it works, the voice models are unloaded; they
  load again at their next use.
- **Optional**: it is not in the APK. AGENTS > LOCAL CODER (or the voice command "install the local coder") downloads
  it; "remove the local coder" deletes it.
- **The APIs the harnesses speak** (`local-ai/server/compat.js`, all streaming, tool calls both ways):

  | Harness | API | Endpoint | How it is pointed there |
  |---|---|---|---|
  | OpenCode | OpenAI chat completions | `/v1/chat/completions`, model `local-coder` | `OPENCODE_CONFIG=/mnt/quest/local-coder.opencode.json` (provider "MetaCodex (this headset)") |
  | Claude Code | Anthropic Messages | `/v1/messages`, `/v1/messages/count_tokens` | MQH gives only a `local-coder` thread `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_MODEL` |
  | Codex | OpenAI Responses | `/v1/responses` exists in the service | Codex is not offered the local coder: small models loop on its tools |

- **The key**: `<data>/coder.key`, made once by the app, shared as `/mnt/quest/local-coder.key` and given to MQH as
  `MCX_LOCAL_CODER_KEY`. It opens only the coder's routes; the per-start token still guards the rest. MQH also gets
  `MCX_LOCAL_CODER_URL`. With both set, the model lists of Claude Code and OpenCode show "Local coder (this headset,
  slow)" (MQH's ACP bridge puts `metacodex/local-coder` first under that name). NEW THREAD shows the local model as
  the first row of every agent's list (⌂, LOCAL); it never becomes the default.
- **Model lists**: they come from MQH's provider bridges in the host daemon's long-lived maintenance runtime. A
  change of the machine environment (the app sets the three variables after the install) restarts that runtime
  (`apps/host-daemon/src/app.ts`), so `opencode acp` sees `OPENCODE_CONFIG` and lists the local coder. OpenCode's own
  background service keeps the config it started with: `opencode service restart` after a change, or
  `--standalone`.

## Voice waits while it codes

The coder and the interface AI never share the 3 AI cores. While a coder request runs, and 15 s after the last one
(an agent turn is many requests with tool runs between them), speech-to-text, the voice LLM, speech and sentence
vectors wait in the service (`engines.coderHolds`, logged "waited ... ms for the local coder"). A coder request waits
for a voice job that already runs. TALK asks the service first and shows "⏸ LOCAL CODER WORKING" instead of
recording.

## The slim profile (`coderSlim` in the service config, always on for the local coder)

Every coder request is slimmed in the service (`compat.slim`):

- the harness's system prompt is replaced by a short one plus its environment facts (`<env>` block, working folder,
  platform, date);
- the `<system-reminder>` blocks of user turns are removed, except the project's own instructions (CLAUDE.md /
  AGENTS.md, for example how native apps are built here);
- only three tools are offered: bash, read and write, with one-line descriptions (a small model picks `edit` for a
  file that does not exist and loops on the error);
- the history stays under about 9k tokens (36,000 characters), so a long thread still fits the 16k context with the
  tools and the answer: a tool result longer than 4,000 characters is cut, then the oldest turns after the task (the
  first user message) go, a turn at a time (an assistant message stays with its tool results), never the last two; a
  short note in their place says that earlier steps were left out.

A turn whose tool calls repeat the previous successful ones exactly ends with "Done." (`stopRepeat` in `engines.js`).
Bare JSON calls in the text (`{"name": "bash", "arguments": {...}}`, without `<tool_call>` tags) count when they name
one of the request's tools.

## Memory

The model was chosen to fit beside the app, the headset OS and MQH with an agent CLI in 8 GB:

| | Qwen2.5-Coder 0.5B (q4_0) | A 1.7B model (q4_0), for comparison |
|---|---|---|
| Weights | 0.43 GB | 1.0 GB |
| KV cache per token (f16) | 24 layers x 2 KV heads x 64 x 2 = 12 KB | 28 layers x 8 KV heads x 128 x 2 = 112 KB |
| KV cache at 16k | 0.19 GB | 1.8 GB |
| The voice models beside it | unloaded while it works | |

The service's memory with the coder loaded is 1,044 MB RSS (measured on the PC). A 1.7B model file left by an older
install is deleted when the service starts (`legacy` in `models.js`).

## Measured

On the PC (x64, 3 cores; the headset is about 6-9 times slower), OpenCode with the provider as the app writes it:
"Create a file named hello.txt that contains the word hi." -> `write hello.txt` with "hi"; the prompt is 741 tokens
with the three tools (1,990 with seven). The slim profile cuts Claude Code's first prompt from 11,071 to 3,363
tokens. Reading the prompt is the main cost; the service keeps a repeated prefix while the model is loaded, so a
later turn reads only its new tokens.

Not measured on the headset: the time per turn, and the frame rate while it works (a local coding thread dropped
frames badly in a headset test).
