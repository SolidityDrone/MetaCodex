# The local coder: a coding model on the headset for the agent harnesses

Owner 2026-11-12: "a local provider in the harnesses we use (OpenCode, Claude Code, Codex) ... even if it's slow and
stupid, just to try it", for judges who want to try coding fully offline. Branch `local-coder`.

## What it is

- **The model**: Qwen2.5-Coder 0.5B Instruct, GGUF q4_0 by the Qwen team (430 MB, Apache-2.0, docs/LICENSES.md):
  a demonstration that agents can run offline, not a useful coder. Engine `coder` in the local AI service
  (`local-ai/server`): 16k context, f16 KV cache, 3 threads on the AI cores 0-2, unloaded after 10 idle minutes; while
  it works the voice models are unloaded (they load again at their next use). Until 2026-11-13 it was Qwen3 1.7B: see
  "Memory" below. Optional: installed only from AGENTS > LOCAL CODER
  (or "install the local coder"), never by the default model install.
- **The APIs the harnesses speak** (`local-ai/server/compat.js`, all streaming, tool calls both ways):

  | Harness | API | Endpoint | How it is pointed there |
  |---|---|---|---|
  | OpenCode | OpenAI chat completions | `/v1/chat/completions`, model `local-coder` | `OPENCODE_CONFIG=/mnt/quest/local-coder.opencode.json` (provider "MetaCodex (this headset)") |
  | (Codex) | OpenAI Responses | `/v1/responses` stays in the service, but Codex is not offered the local coder (owner: "we can ignore codex"; small models loop on its tools) | - |
  | Claude Code | Anthropic Messages | `/v1/messages`, `/v1/messages/count_tokens` | MQH gives only a `local-coder` thread `ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_MODEL` |

- **The key**: `<data>/coder.key`, made once by the app, shared as `/mnt/quest/local-coder.key`, given to MQH as
  `MCX_LOCAL_CODER_KEY`; it opens only the coder's routes (the per-start token still guards the rest). MQH also gets
  `MCX_LOCAL_CODER_URL`; with both set, Claude Code's model list shows "Local coder (this headset, slow)", and so does
  OpenCode's (with `OPENCODE_CONFIG`; MQH's ACP bridge puts `metacodex/local-coder` first under that name). NEW THREAD
  shows the local model as the first row of any agent's list (⌂, LOCAL).
- **Why OpenCode first showed only its own models** (fixed 2026-11-13): model lists come from MQH's provider bridges in
  the host daemon's long-lived maintenance runtime, which inherits the daemon's environment when it starts. The app sets
  the three variables after the install, so `opencode acp` (started by the bridge to read its models) never had
  `OPENCODE_CONFIG`; MQH marked its cached list stale, but the refresh asked the same bridge. Now a machine environment
  change restarts that runtime (`apps/host-daemon/src/app.ts`). Checked with OpenCode 2.1.4: `opencode acp`
  `session/new` lists `metacodex/local-coder` only when `OPENCODE_CONFIG` is in its environment, and
  `session/set_config_option` selects it. (Its background service, used by `opencode models`, keeps the config it
  started with: `opencode service restart` after a change, or `--standalone`.)

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

The history is kept under about 9k tokens (36,000 characters, 2026-11-14), so a long thread still fits the 16k context
with the tools and the answer: a tool result longer than 4,000 characters is cut, then the oldest turns after the task
(the first user message) go, a turn at a time (an assistant message stays with its tool results), never the last two;
a short note in their place says that earlier steps were left out. OpenCode lists the model as "Local coder (this
headset, slow)", described as Qwen2.5-Coder 0.5B (the ACP bridge's `local-coder.ts`; it still said Qwen3 1.7B).

## Open (before 0.7.18)

- Headset measurements; the voice bench after the two new commands (AGENTS.md rule 3).

## Memory: why the coder is 0.5B now (2026-11-13)

Owner, on 0.7.19: a thread on the local provider "starts, but after a few seconds the frame rate drops until Horizon OS
does not refresh frames any more, weird artifacts, then the app crashes or needs a hard restart". The session log: the
AI service stopped answering ("restarting (1/3)") and MQH's host daemon dropped ("Thread interrupted because the host
daemon disconnected"), as when the system kills processes for memory. The numbers:

| | Qwen3 1.7B (before) | Qwen2.5-Coder 0.5B (now) |
|---|---|---|
| Weights (q4_0) | 1.0 GB | 0.43 GB |
| KV cache per token (f16) | 28 layers x 8 KV heads x 128 x 2 = 112 KB | 24 layers x 2 KV heads x 64 x 2 = 12 KB |
| KV cache at 16k | 1.8 GB | 0.19 GB |
| Voice models beside it | loaded (about 1.2 GB) | unloaded while it works |
| The service's memory with the coder loaded (PC, measured) | - | 1,044 MB RSS |

With the app (about 2 GB), Horizon OS and MQH with an agent CLI, the 1.7B coder with its 16k context did not fit in the
Quest 3S's 8 GB. The old 1.7B file is deleted when the service starts (`legacy` in models.js), 1 GB back.

For the small model, the slim profile (compat.js) offers only three tools, bash, read and write (it chose `edit` for
a file that did not exist and looped on the error), and a turn whose tool calls repeat the previous successful ones
exactly ends with "Done." (`stopRepeat` in engines.js: it wrote hello.txt 29 times). Bare JSON calls in the text
(`{"name": "bash", "arguments": {...}}`, no `<tool_call>` tags) count when they name one of the request's tools.

Checked on the PC (OpenCode 2.1.4 in a throwaway home, provider as the app writes it): "Create a file named hello.txt
that contains the word hi." -> `write hello.txt` with "hi"; prompt 741 tokens with the three tools (1,990 with seven).
Not checked on the headset yet: the frame rate while it works, and the time per turn (the PC is 6-9x the Quest).

