---
name: metacodex-context
description: Where you are when you run inside MetaCodex - a Meta Quest headset app with a Debian Linux userland (proot), the MQH harness (mqh CLI, threads, providers), the shared folder /mnt/quest, the app's control port (mcx-ui), the secret vault (mcx-secret), the local AI service and voice commands (mcx-ai, mcx-ui voice) and the frame-rate rules. Read it before changing anything on the headset, before writing gesture plugins or Lua behaviours, or when a command, path or login behaves unexpectedly.
---

# MetaCodex context

## The machine

- A **Meta Quest 3 / 3S** (Android 14, Horizon OS, 6 ARM cores, about 4 GB free RAM, no GPU for you).
- **MetaCodex** is the headset app (Unity): passthrough, hand tracking, floating panels, terminals.
- You run in its **Linux userland**: Debian 13 arm64 under **proot**, as `root` (a fake root: you are
  the app's Android user). It is the app's private storage; other apps cannot see it.
- **MQH** (the Meta Quest Harness, a fork of bb) runs in this Linux: server on 127.0.0.1:38990, daemon
  on 38991, data in `~/.mqh`, CLI `mqh`. The app is MQH's only client: it shows projects, threads,
  providers and settings as panels.

## Paths

| Path | What |
|---|---|
| `/root` | home; projects usually live here (`/root/<project>`) |
| `/mnt/quest` | the app's shared folder (the user and adb can reach it) |
| `/mnt/quest/plugins/<id>/` | gesture / behaviour plugins (skill `quest-gesture-plugins`) |
| `/mnt/quest/scripts/*.lua` | single-file Lua behaviours (skill `quest-scripting`) |
| `/mnt/quest/control.token` | token for the app's control port (new at every app start) |
| `/mnt/quest/qvac.token` | token for the local AI service (new at every start of the service) |
| `~/.mqh` | MQH data (database, logs, plugin data) |
| `~/.claude/skills` | skills for agents (this one, quest-*, Unity and Meta skills) |

## Talking to the app

- `mcx-ui ...` drives the app over its control port (127.0.0.1:47800): `ui list|open|close|toast`,
  `script ...`, `plugin ...`, `gesture ...`, `action <verb:arg>`, `status`, `screen` (the focused
  terminal's text). Run `mcx-ui` alone for the list.
- `mcx-secret get NAME` asks the app's vault for a secret (API keys, tokens). Values are typed by the
  user in VR and sealed by the Android Keystore; the app may ask the user to approve your request.
  Never write secrets into files, logs, commits or messages; prefer `mcx-secret env NAME -- cmd`.
- `mcx-ai ...` reaches the **local AI service** (QVAC, native on Horizon OS, 127.0.0.1:47810, OpenAI-style):
  `mcx-ai status`, `transcribe file.wav`, `say "text" out.wav`, `chat "prompt"`, `env` (prints
  `OPENAI_BASE_URL` / `OPENAI_API_KEY` for OpenAI-compatible tools). Speech-to-text (Whisper base), a 0.6B
  LLM for commands and short answers (not for coding), text-to-speech (Supertonic 3). Private: nothing
  leaves the headset. It shares 3 CPU cores with you at low priority: one request at a time per model.
- Voice: the user speaks commands (TALK on the dock). `mcx-ui voice run "close all windows"` runs text as if
  spoken; `mcx-ui voice list` lists commands; scripts and plugins add their own (`voice.command`, skill
  `quest-scripting`).
- Agents' API keys usually come from MQH's machine environment (the user pushes them from the vault);
  provider logins (`claude auth login`, `opencode auth login`, `codex login`) are done by the user in a
  terminal: the CLIs keep their own credentials. Never read, copy or print those credential files.

## Rules on the headset

- **The app must keep its frame rate.** Heavy work (builds, installs, model downloads) runs at low
  priority: prefix long commands with `nice -n 10` and, for CPU-heavy jobs, `taskset -c 0-2`
  (cores 0-2; the app renders on the others).
- Android kills processes above 32 child processes app-wide: avoid huge parallel builds (`-j2`).
- No GPU, no Docker, no systemd, no hard links (copies are made instead). Node comes from nodejs.org
  (`mcx-setup` installs it); Debian packages work with `apt`.
- The user wears the headset: keep answers short, and say what to do with the hands or panels in body
  words ("make a fist", "open GESTURES from the dock").
- Things you change for the app (plugins, scripts, settings) apply live; never ask for an app rebuild
  for behaviour changes.

## MQH from inside

`mqh status`, `mqh thread ...`, `mqh plugin list`. Threads you are part of carry `MQH_THREAD_ID`.
The app shows threads in THREADS / THREAD; the user approves tool use there or in the terminal.
