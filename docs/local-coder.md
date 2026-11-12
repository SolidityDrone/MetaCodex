# The local coder: a coding model on the headset for the agent harnesses

Owner 2026-11-12: "a local provider in the harnesses we use (OpenCode, Claude Code, Codex) ... even if it's slow and
stupid, just to try it", for judges who want to try coding fully offline. Branch `local-coder`.

## What it is

- **The model**: Qwen3 1.7B instruct, GGUF q4_0 (1 GB, Apache-2.0, docs/LICENSES.md), engine `coder` in the local AI
  service (`local-ai/server`): its own instance next to the voice LLM (Qwen3 0.6B), 16k context, f16 KV cache, thinking
  off, 3 threads on the AI cores 0-2, unloaded after 10 idle minutes. Optional: installed only from AGENTS > LOCAL CODER
  (or "install the local coder"), never by the default model install.
- **The APIs the harnesses speak** (`local-ai/server/compat.js`, all streaming, tool calls both ways):

  | Harness | API | Endpoint | How it is pointed there |
  |---|---|---|---|
  | OpenCode | OpenAI chat completions | `/v1/chat/completions`, model `local-coder` | `OPENCODE_CONFIG=/mnt/quest/local-coder.opencode.json` (provider "MetaCodex (this headset)") |
  | (Codex) | OpenAI Responses | `/v1/responses` stays in the service, but Codex is not offered the local coder (owner: "we can ignore codex"; the 1.7B model loops on its tools) | - |
  | Claude Code | Anthropic Messages | `/v1/messages`, `/v1/messages/count_tokens` | MQH gives only a `local-coder` thread `ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_MODEL` |

- **The key**: `<data>/coder.key`, made once by the app, shared as `/mnt/quest/local-coder.key`, given to MQH as
  `MCX_LOCAL_CODER_KEY`; it opens only the coder's routes (the per-start token still guards the rest). MQH also gets
  `MCX_LOCAL_CODER_URL`; with both set, Claude Code's model list shows "Local coder (this headset, slow)".

## Voice waits while it codes

The coder and the interface AI never share the 3 AI cores (owner: "if we choose the local provider ... talk or local AI
for interfacing with UI shall await, otherwise we hit a bottleneck"): while a coder request runs, and 15 s after the last
one (an agent turn is many requests with tool runs between them), speech-to-text, the voice LLM, speech and sentence
vectors wait in the service (`engines.coderHolds`, logged "waited ... ms for the local coder"); a coder request waits for
a voice job that already runs. TALK asks the service first and shows "⏸ LOCAL CODER WORKING" instead of recording.

## Measured (PC, x64, the linux-x64 addon, 3 cores; the headset is about 4-8x slower)

| Harness | Task | Result | Time |
|---|---|---|---|
| OpenCode 2.1.4 (`--standalone`) | "Create a file hello.py that prints hello world." | ✓ wrote it with its Write tool | 15 min (the first turn's prompt with a q8_0 KV cache: 848 s) |
| Claude Code 2.1.319 | same | ✓ wrote it | 5.7 min (first turn 11,071 prompt tokens, 300 s; the next 91 new tokens, 30 s) |
| Codex 0.139.0 | same | ✗ the protocol works (exec_command / write_stdin ran) but the 1.7B model looped and never wrote the file | 22 min |

Prompt reading is the cost: 8k tokens took 149 s with an f16 KV cache (54 tok/s) and 364 s with q8_0 (so f16), and
0.6-0.8 s when the same prefix comes again (the addon keeps it while loaded). On the headset expect the first turn of a
new session to take 10-20 min and later turns seconds to a couple of minutes.

## The slim profile (always on for the local coder; `coderSlim` in the service config)

Owner: "if using local shall use slim agent profiles". Every coder request gets, in the service (`compat.slim`), the
harness's system prompt replaced by a short one plus its environment facts (`<env>` block, working folder, platform,
date), the `<system-reminder>` blocks of user turns removed except the project's own instructions (CLAUDE.md /
AGENTS.md, e.g. how native apps are built here), and only the core tools (read / write / edit / bash / glob / grep),
their descriptions cut to one line. Claude Code's first turn: 11,071 -> 3,363 prompt tokens; the same task 340 s -> 78 s
on the PC (headset estimate 5-10 min -> 1-3 min).

## Open (before 0.7.18)

- Headset measurements; the voice bench after the two new commands (AGENTS.md rule 3).
