# MetaCodex: instructions for coding agents

Monorepo: `unity/` (the Quest app), `mqh/` (the harness, see docs/mqh.md), `userland/`, `native/`,
`tests/`, `tools/`, `docs/`. The Unity project root is `unity/` (Windows `C:\Users\pnncl\MetaCodex\unity`).

Read first: the unity-skill-set router `/home/drone/projects/unity-skill-set/skills/quest-dev/SKILL.md`
and its `knowledge/LEARNINGS.md` (search it for your topic). Plan of record:
`/home/drone/bs/BELVEDERE_PLAN.md`. Project overview and commands: `README.md`.

## Rules on this machine (16 GB Windows laptop, Editor on Windows, agents in WSL)

- **No GUI Editor** unless the owner asks. Use `tools/cscheck.sh` (compile check, no Editor) and
  `tests/terminal/run-core-tests.sh` first; open a batch Editor only through `tools/unity-batch.sh` (RAM guard,
  kills only its own PID). Never `taskkill /IM Unity.exe`: the owner runs other headless Editors.
- **Build an APK only when the owner says so**, and only after the self-test passed and its picture
  (`unity/Bench/selftest/terminal.png`) looks right. Grep the build log for `error CS` and `Shader error in`.
  Generated parts of the APK, refreshed before a build when their sources changed: `tools/qvac-bundle.sh` (the
  local AI), `tools/mqh-build.sh && tools/mqh-bundle.sh` (MQH for the headset's Linux), `userland/build-overlay.py`.
- Long commands (batch Editor, builds, `userland/g0-shell.sh`) run in the background; never wait in a
  foreground polling loop.
- The headset: never scan the network for it. Ask for one USB plug-in, then Wi-Fi adb
  (`adb tcpip 5555`, `adb connect <ip>:5555`, ip from `adb shell ip -f inet addr show wlan0`).
- While an Editor has the project open, change scenes/assets through `unity command eval`, not YAML.
- Never touch Claude subscription credentials; never bundle the Claude Code binary (plan D3/D4).
- No brand names or logos in the app UI or video (contest rules). One exception the owner approved: the
  installable tools' own icons in SYSTEM > TOOLS (node, npm, bun, rust, ...), which identify the software.

## The command registry (the app's MCP-like surface): strict rules

Everything the user can do in the app is a registered command (`VoiceCommands`, `Runtime/AI/VoiceCommands.cs`):
voice (fast path and the decision model `VoiceDecider`), the local LLM, agents (`mcx-ui voice registry`), plugins
(`voice.registry()`) and the bench all read the same entries. A gap there is a feature voice and agents cannot reach.

1. **Every capability has a complete command.** A new or changed app action (a `RunAction` verb, control-port
   command, panel, setting, mode) gets its command in the same change: `Name` (snake_case), one-line
   `Description` with its options, a JSON schema with typed and described `Parameters` (`enum` for closed sets,
   `minimum` / `maximum` / unit for numbers), `Synonyms` for every enum value people say differently (English
   and Italian), `Phrases` (fast path), `Samples` (4+ natural requests, English and Italian, none copied from
   `tools/voicebench/eval.jsonl`), `Examples` and `Vocabulary` for unusual words. Settings go into
   `VoiceSettingsCatalog` (English `Names`) and `VoiceSettingOther` (Italian and colloquial names).
   `VoiceCommands.Lint(command)` must return nothing; the self-test section `decider` fails otherwise.
2. **Disabled or removed means unregistered, at once.** A plugin or script that is disabled, deleted or stopped
   by an error loses its commands (ScriptHost `Unload` -> `RemoveOwnedBy`); a shell feature that is switched
   off or not available calls `Voice.SetEnabled(name, false)` (or `Remove`) and enables it again when it is
   back. Never leave a command whose action cannot run: agents, the LLM and the decision model see only
   enabled commands (`Describe`, `ToolSpecs`, `LlmSystem`, `BuildExamples`), and nothing may offer a dead one.
3. **Measured, not guessed.** After a registry change run the `decider` self-test and the named bench
   (`MCX_UITEST_ONLY=voiceexport,voicedecide`, dev AI service up), score it with
   `tools/voicebench/score_app.py`, and compare with the last `tools/voicebench/results/` file: no drop in
   exact accuracy, English or Italian. Commit the new results file with the change.
4. **Every change goes through review before it lands.** Work on a feature branch; before merging, the diff,
   the test and bench numbers and what changed for the user are reviewed (the owner, or a reviewer agent the
   owner asked for), and only then merged. The same holds for agents inside the app changing plugins,
   gestures, scripts or voice commands (skills `quest-scripting`, `quest-gesture-plugins`): they show the
   change and get the user's OK first.

## Before you finish

Write verified learnings to the skill set's `knowledge/LEARNINGS.md` (format in its
`skills/self-improve/SKILL.md`) and update the plan's status table.
