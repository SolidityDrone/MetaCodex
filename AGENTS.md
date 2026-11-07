# MetaCodex: instructions for coding agents

Read first: the unity-skill-set router `/home/drone/projects/unity-skill-set/skills/quest-dev/SKILL.md`
and its `knowledge/LEARNINGS.md` (search it for your topic). Plan of record:
`/home/drone/bs/BELVEDERE_PLAN.md`. Project overview and commands: `README.md`.

## Rules on this machine (16 GB Windows laptop, Editor on Windows, agents in WSL)

- **No GUI Editor** unless the owner asks. Use `Tools/cscheck.sh` (compile check, no Editor) and
  `Tests/run-core-tests.sh` first; open a batch Editor only through `Tools/unity-batch.sh` (RAM guard,
  kills only its own PID). Never `taskkill /IM Unity.exe`: the owner runs other headless Editors.
- **Build an APK only when the owner says so**, and only after the self-test passed and its picture
  (`Bench/selftest/terminal.png`) looks right. Grep the build log for `error CS` and `Shader error in`.
- Long commands (batch Editor, builds, `Linux/g0-shell.sh`) run in the background; never wait in a
  foreground polling loop.
- The headset: never scan the network for it. Ask for one USB plug-in, then Wi-Fi adb
  (`adb tcpip 5555`, `adb connect <ip>:5555`, ip from `adb shell ip -f inet addr show wlan0`).
- While an Editor has the project open, change scenes/assets through `unity command eval`, not YAML.
- Never touch Claude subscription credentials; never bundle the Claude Code binary (plan D3/D4).
- No brand names or logos in the app UI or video (contest rules).

## Before you finish

Write verified learnings to the skill set's `knowledge/LEARNINGS.md` (format in its
`skills/self-improve/SKILL.md`) and update the plan's status table.
