# The local coder: a coding model on the headset for the agent harnesses

The local coder lets an agent thread run with no network: a small coding model in the local AI service, reached by
the agent CLIs through the APIs they already speak. It is a demonstration that agents can run offline, not a strong
coder: a 0.5B model handles small tasks (one small file, one command) and simple questions, not long agent work.

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
  | Claude Code | Anthropic Messages | `/v1/messages`, `/v1/messages/count_tokens` | only with MQH's own Claude plugin (a PC's MQH; not in the APK): it gives only a `local-coder` thread `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_MODEL`. The headset's Claude Code threads run through the ACP adapter (`docs/mqh.md`), which does not list the local coder |
  | Codex | OpenAI Responses | `/v1/responses` exists in the service | Codex is not offered the local coder: small models loop on its tools |

- **The key**: `<data>/coder.key`, made once by the app, shared as `/mnt/quest/local-coder.key` and given to MQH as
  `MCX_LOCAL_CODER_KEY`. It opens only the coder's routes; the per-start token still guards the rest. MQH also gets
  `MCX_LOCAL_CODER_URL`. With both set, OpenCode's model list shows "Local coder (this headset, slow)" (MQH's ACP
  bridge puts `metacodex/local-coder` first under that name; MQH's own Claude plugin, on a PC, lists it too). NEW THREAD shows the local model as
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

- the harness's system prompt is replaced by a short one: a question or a chat gets a plain answer with no tool;
  the tools are named as the request names them (Claude Code: `Bash`, `Read`, `Write`; OpenCode: `bash`, `read`,
  `write`; Codex: `exec_command`), one line each; then the harness's environment facts (`<env>` block, working folder,
  platform, date). It is replaced, not cut to its first characters: those are the harness's identity and style rules,
  which a 0.5B model cannot follow. The tool list itself is written by the model's own chat template (the GGUF's:
  `<tools>` with one JSON schema a line, then "return ... within `<tool_call></tool_call>` tags");
- the project's own instructions stay, cut to their first 1,500 characters: Claude Code's CLAUDE.md / AGENTS.md
  `<system-reminder>`, OpenCode's `Instructions from: <path>` blocks, Codex's `<INSTRUCTIONS>`; the other
  `<system-reminder>` blocks of user turns (skill lists, todos) are removed;
- only three tools are offered: shell, read and write, with one-line descriptions (a small model picks `edit` for a
  file that does not exist and loops on the error), and only their required arguments, command or path first, then
  the content (the template's tool grammar holds the model to the schema's order);
- a request with no tools (a title, a summary) keeps its system prompt, up to 1,200 characters;
- the temperature is at most 0.2 (`coderMaxTemperature`; Claude Code asks for 1, and a 0.5B model then writes more
  broken JSON);
- the history stays under about 9k tokens (36,000 characters), so a long thread still fits the 16k context with the
  tools and the answer: a tool result longer than 4,000 characters is cut, then the oldest turns after the task (the
  first user message) go, a turn at a time (an assistant message stays with its tool results), never the last two; a
  short note in their place says that earlier steps were left out.

A turn whose tool calls repeat the previous successful ones exactly ends with "Done." (`stopRepeat` in `engines.js`).

## Tool calls in the model's text (`local-ai/server/toolcalls.js`)

Owner, 2026-11-16: "it only answers with JSON: I asked a question and it answered with a JSON to echo a hello world".
The path of a question: harness -> endpoint (`compat.js`: chat completions, Responses, Messages) -> `compat.slim` ->
`engines.chat` (the model's chat template renders the tools) -> the model's text -> `parseAssistant` -> tool calls or
text in the API's own format. The 0.5B model often writes a call without the template's `<tool_call>` tags, and the
text leaked in two places:

- **the name**: a bare or fenced `{"name": "bash", ...}` counted only when the name was exactly one of the request's
  tools. Claude Code's tool is `Bash`, Codex's `exec_command`: the call went back to the user as text (for both, every
  bare or fenced call did). A tagged call went out as `bash`, which Claude Code does not have;
- **the stream**: all three harnesses stream. Only `<tool_call>` text was held back, so a bare JSON call reached the
  user token by token before the parse made it a call (OpenCode showed the JSON and then ran it).

Now, for the local coder:

- a call counts in any of the forms the model writes: `<tool_call>` tags (also with a ```` ```json ```` block
  inside), a ```` ```json ```` block, a bare JSON object; `name` / `function` / `tool`, `arguments` / `parameters` /
  `args` / `input`, the OpenAI `{"type": "function", "function": {...}}` shape, arguments as a JSON string;
- the name is matched in any case and through its usual words (`bash`, `shell`, `run` -> the request's shell tool;
  `read_file`, `cat` -> its read tool; `write_file`, `create_file` -> its write tool); a read or write call when the
  request has only a shell tool (Codex) becomes the same work as a shell command (`cat`, `printf`);
- the arguments are fitted to the tool's schema: `path` -> `file_path` / `filePath`, `cmd` <-> `command`, a missing
  required `description` (OpenCode's bash) from the command; keys a strict schema does not declare are dropped;
- broken JSON is repaired (a stray quote after a number, as in a 2026-11-12 Codex run, trailing commas, a missing
  closing brace, raw line breaks in strings, single quotes); a call cut off inside a string (the output limit) is not
  run, and a short note replaces it: never the JSON;
- a lone `echo` call (no redirection, pipe, variable or second command) is returned as its text: it only prints, and
  the extra tool turn costs minutes on the headset;
- not a call: JSON that names no tool of the request (a `package.json`, an example of another API) unless it is the
  whole answer (then it goes to the harness, which says the tool does not exist: the model wrote `edit` calls out of
  habit), a call-shaped example inside an answer longer than 400 characters, any JSON when the request has no tools;
- the stream: text goes out as it comes until a point where a call could start (a `{`, a code fence, a tag); from
  there it is held to the end of the answer, and only what the parse leaves as text goes out (`callGuard`). Prose
  with a code block arrives in one piece from the block on.

The app's own voice LLM uses the same parser without these coder rules (exact tool names, as before).

Check: `tests/local-coder/run-local-coder-tests.sh` (Node, no install, under a second): 21 model outputs through the
three endpoints, streamed and not (126 cases), plus the slim prompt, the parser pieces and the voice LLM's rules.
Before the change, the same outputs gave raw JSON as the answer for Claude Code and Codex in every bare and fenced
case, and OpenCode streamed the JSON before running the call.

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
with the three tools (1,990 with seven; both before the 2026-11-16 prompt change, which keeps only the required
arguments and adds the question rule: not measured again). The slim profile cuts Claude Code's first prompt from 11,071 to 3,363
tokens. Reading the prompt is the main cost; the service keeps a repeated prefix while the model is loaded, so a
later turn reads only its new tokens.

Not measured on the headset: the time per turn, and the frame rate while it works (a local coding thread dropped
frames badly in a headset test).
